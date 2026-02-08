using System.Text.RegularExpressions;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Base of the mail tests: a host whose mails are captured instead of sent, whose log is kept, and whose dispatcher
/// does not run by itself — a test calls <see cref="DispatchAsync"/> when the queue should be handled, so nothing
/// races the assertions.
/// </summary>
public abstract partial class MailTestBase : SessionTestBase
{
    public const string UserPassword = "Another-Passw0rd";

    protected MailTestBase(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithoutHostedService<MailDispatchService>()
            .WithServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IMailTransport>(Mail));
                services.AddSingleton<ILoggerProvider>(Logs);
            });
    }

    protected CapturingMailTransport Mail { get; } = new();

    protected CapturingLoggerProvider Logs { get; } = new();

    /// <summary>One pass of the dispatcher over the rows that are due now; returns how many it handled.</summary>
    protected Task<int> DispatchAsync() =>
        Factory.Services.GetRequiredService<MailDispatcher>().DispatchDueAsync(TestContext.Current.CancellationToken);

    protected async Task<T> InDbAsync<T>(Func<AuthDbContext, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    /// <summary>Puts a request on the queue the way the endpoints do, and asserts the limit let it through.</summary>
    protected async Task EnqueueAsync(MailKind kind, string email)
    {
        using var scope = Factory.Services.CreateScope();
        var normalized = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>().NormalizeEmail(email);
        var decision = await Factory.Services.GetRequiredService<MailRequestStore>()
            .SubmitAsync(kind, normalized, TestContext.Current.CancellationToken);
        Assert.True(decision.Allowed, $"The mail limit refused the request; wait {decision.RetryAfter} on the test clock.");
    }

    /// <summary>
    /// Makes an account. Since spec 0005 an account that belongs to no company cannot log in, so by default the account
    /// joins the development company with its role <c>user</c> (when the host has one); pass <paramref name="member"/>
    /// <see langword="false"/> for an account that belongs nowhere.
    /// </summary>
    protected async Task<ApplicationUser> CreateUserAsync(string email, bool confirmed, string password = UserPassword, bool member = true)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = confirmed };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Code)));

        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var role = member
            ? await db.CompanyRoles.AsNoTracking().FirstOrDefaultAsync(
                r => r.NormalizedName == "USER" && db.Companies.Any(c => c.Id == r.CompanyId && c.Name == DevUserSeeder.DefaultOrgName),
                TestContext.Current.CancellationToken)
            : null;
        if (role is not null)
        {
            db.Memberships.Add(new Membership { UserId = user.Id, CompanyId = role.CompanyId, RoleId = role.Id, JoinedAt = StorableTime.Now(Clock) });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return user;
    }

    /// <summary>The link token of a mail, read from its plain-text part.</summary>
    public static string TokenIn(ComposedMail mail)
    {
        ArgumentNullException.ThrowIfNull(mail);

        var match = TokenInLink().Match(mail.TextBody);
        Assert.True(match.Success, "The mail holds no link with a token.");
        return match.Groups[1].Value;
    }

    [GeneratedRegex("[?&]token=([A-Za-z0-9_-]{43})(?![A-Za-z0-9_-])")]
    private static partial Regex TokenInLink();
}

public static class Poll
{
    /// <summary>Waits in real time, up to 10 seconds, for something a background service does.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}.");
            await Task.Delay(50);
        }
    }
}
