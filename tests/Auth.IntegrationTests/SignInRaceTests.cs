using System.Data.Common;
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Locks: a sign-in or a refresh that meets a deleted account writes no session for it.</summary>
public sealed class SignInRaceTests : AuditTestBase
{
    private const string Worker = "worker@acme.test";

    private readonly ShareLockInterceptor _race = new();

    public SignInRaceTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(_race)));
    }

    private Task<int> SessionRowsAsync(Guid user)
    {
        var subject = user.ToString();
        return InDbAsync(async db =>
            await db.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync(t => t.Subject == subject, TestContext.Current.CancellationToken)
            + await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().CountAsync(a => a.Subject == subject, TestContext.Current.CancellationToken));
    }

    /// <summary>The account is deleted by another request just before the sign-in reads it again under its share lock.</summary>
    private void DeleteJustBeforeTheShareLock(Guid user) =>
        _race.Before = () => InDbAsync(db => db.Users.Where(u => u.Id == user).ExecuteDeleteAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_login_for_an_account_deleted_after_it_was_read_is_an_unknown_address_and_writes_no_session()
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");
        DeleteJustBeforeTheShareLock(worker);

        using var login = await LoginApi.Login(Client, Worker, UserPassword);

        Assert.True(_race.Fired);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await login.Content.ReadAsStringAsync());
        Assert.Equal(0, await SessionRowsAsync(worker));
        Assert.DoesNotContain(await AuditAsync(AuditKinds.LoginSucceeded), row => row.SubjectUserId == worker);   // the admin's own login is there
        Assert.Equal("unknown_address", AuditApi.Text(await SingleAsync(AuditKinds.LoginFailed), "reason"));
    }

    [Fact]
    public async Task A_refresh_for_an_account_deleted_after_it_was_read_is_an_invalid_grant_and_writes_no_session()
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");
        var session = await SessionApi.LoginAsync(Client, Worker, UserPassword);
        var before = await SessionRowsAsync(worker);
        DeleteJustBeforeTheShareLock(worker);

        using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);

        Assert.True(_race.Fired);
        await SessionApi.AssertInvalidGrantAsync(refresh);
        Assert.Equal(before, await SessionRowsAsync(worker));
    }

    /// <summary>Runs a step once, just before the first command that reads a row under a share lock.</summary>
    private sealed class ShareLockInterceptor : DbCommandInterceptor
    {
        public Func<Task>? Before { get; set; }

        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var step = Before;
            if (step is not null && command.CommandText.Contains("FOR KEY SHARE", StringComparison.Ordinal))
            {
                Before = null;
                Fired = true;
                await step();
            }

            return result;
        }
    }
}