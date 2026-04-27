using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Limits: a password typed at the link counts in the streak of the ticket's address, as a login does (criterion 16).</summary>
public sealed class ProviderLinkLockoutTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;

    private async Task FailLinksAsync(ProviderApi.Ticket ticket, int times)
    {
        for (var i = 0; i < times; i++)
        {
            Clock.Advance(LockoutApi.HumanPace);
            using var response = await ProviderApi.Link(Client, ticket, LockoutApi.WrongPassword);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task After_ten_failures_the_link_is_429_too_many_attempts_and_recorded_as_login_locked_with_the_provider()   // criterion 16
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        await FailLinksAsync(ticket, 10);

        using var locked = await ProviderApi.Link(Client, ticket, UserPassword);   // even the right password

        var seconds = await LockoutApi.AssertLockedAsync(locked);
        var row = await SingleAsync(AuditKinds.LoginLocked);
        Assert.Equal(Ola, row.SubjectEmail);
        Assert.Equal("google", AuditApi.Text(row, "provider"));
        Assert.Equal(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), AuditApi.Text(row, "retry_after_seconds"));
        Assert.Equal(1, await TicketCountAsync());   // a refusal leaves the ticket
    }

    [Fact]
    public async Task Failures_at_the_login_and_at_the_link_share_one_streak()   // criterion 16
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        await LockoutApi.FailAsync(Client, Clock, Ola, 5);
        await FailLinksAsync(ticket, 5);

        using var locked = await LoginApi.Login(Client, Ola, UserPassword);

        await LockoutApi.AssertLockedAsync(locked);
    }

    [Fact]
    public async Task A_provider_sign_in_neither_counts_nor_clears_the_streak()   // spec 0011 → Limits
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        await LockoutApi.FailAsync(Client, Clock, Ola, 3);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var attempts = await InDbAsync(db => db.LoginStreaks.Where(s => s.IdentifierHash == LoginIdentifier.HashOf("OLA@GMAIL.TEST"))
            .Select(s => s.AttemptCount).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, attempts);
    }
}
