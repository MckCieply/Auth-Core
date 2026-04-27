using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → exchange, step 3.1: a linked account that is deleted between the read of the login and the sign-in.</summary>
public sealed class ProviderExchangeRaceTests : ProviderTestBase
{
    private readonly ShareLockInterceptor _race = new();

    public ProviderExchangeRaceTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(_race)));
    }

    [Fact]
    public async Task An_account_deleted_after_its_login_was_read_is_invalid_ticket_and_the_ticket_and_auth_pt_stay()   // spec 0011 → exchange, step 3.1
    {
        var user = await CreateUserAsync(FakePeople.GoogleEmail, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        var ticket = await TicketAsync("google", GooglePerson());
        _race.Before = () => InDbAsync(db => db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        using var response = await ProviderApi.Exchange(Client, ticket);

        Assert.True(_race.Fired);
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.False(response.Headers.Contains("Set-Cookie"));   // auth_pt is not cleared, no refresh cookie is set
        Assert.Equal(1, await TicketCountAsync());   // the rollback gave the ticket back
        var subject = user.Id.ToString();
        Assert.Equal(
            0,
            await InDbAsync(async db =>
                await db.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync(t => t.Subject == subject, TestContext.Current.CancellationToken)
                + await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().CountAsync(a => a.Subject == subject, TestContext.Current.CancellationToken)));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }
}
