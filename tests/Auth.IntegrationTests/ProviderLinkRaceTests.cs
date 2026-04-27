using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0011 → link, step 8: what the link does when the world changes while it waits for the address lock. The test holds the lock of the
/// address, sends the link, waits until it is queued behind it, changes the world, and releases.
/// </summary>
public sealed class ProviderLinkRaceTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string OlaNormalized = "OLA@GMAIL.TEST";

    private Task<int> LoginCountAsync() => InDbAsync(db => db.UserLogins.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>Sends the link while the address lock is held, waits until the request is queued behind it, runs <paramref name="change"/>, releases.</summary>
    private async Task<HttpResponseMessage> LinkWhileLockedAsync(ProviderApi.Ticket ticket, Func<HeldLock, Task> change)
    {
        await using var held = await HeldLock.AddressAsync(Factory, OlaNormalized);
        var link = ProviderApi.Link(Client, ticket, UserPassword);
        await HeldLock.WaitForWaitersAsync(Factory, 1);
        await change(held);
        await held.ReleaseAsync();
        return await link;
    }

    [Fact]
    public async Task A_ticket_used_up_while_the_link_waited_for_the_address_lock_is_400_invalid_ticket_and_links_nothing()   // spec 0011 → link, step 8
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await LinkWhileLockedAsync(ticket, _ => InDbAsync(db => db.ProviderTickets.ExecuteDeleteAsync(TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, await LoginCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task An_identity_linked_while_the_link_waited_signs_in_with_the_ticket_given_back_and_writes_no_provider_linked()   // spec 0011 → link, step 8
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await LinkWhileLockedAsync(ticket, _ => LinkAsync(user.Id, "google", FakePeople.GoogleSubject));

        Assert.Equal(user.Id.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(response)).AccessToken, "sub"));
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal(0, await TicketCountAsync());   // used up once, by the sign-in
        Assert.Equal(1, await LoginCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal("google", AuditApi.Text(await SingleAsync(AuditKinds.LoginSucceeded), "provider"));
    }

    [Fact]
    public async Task An_account_removed_while_the_link_waited_links_nothing_and_answers_401_and_the_ticket_stays()   // spec 0011 → link, step 8
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await LinkWhileLockedAsync(ticket, held => held.DeleteAccountAsync(user.Id));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, await LoginCountAsync());
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
    }

    [Fact]
    public async Task A_membership_removed_while_the_link_waited_is_403_no_membership_with_login_failed_and_the_ticket_stays()   // spec 0011 → link, steps 7 and 8
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await LinkWhileLockedAsync(ticket, _ => RemoveMembershipAsync(user.Id));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, await LoginCountAsync());
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Equal("no_company", AuditApi.Text(row, "reason"));
        Assert.Equal("google", AuditApi.Text(row, "provider"));
        Assert.Equal(user.Id, row.SubjectUserId);
    }

    [Fact]
    public async Task Two_links_with_one_ticket_give_exactly_one_session_one_login_and_one_provider_linked()   // spec 0011 → link, step 8
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        Task<HttpResponseMessage> first, second;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            first = ProviderApi.Link(Client, ticket, UserPassword);
            second = ProviderApi.Link(Client, ticket, UserPassword);
            await HeldLock.WaitForWaitersAsync(Factory, 2);
            await held.ReleaseAsync();
        }

        using var a = await first;
        using var b = await second;

        var ok = new[] { a, b }.Where(r => r.StatusCode == HttpStatusCode.OK).ToList();
        var refused = new[] { a, b }.Where(r => r.StatusCode != HttpStatusCode.OK).ToList();
        Assert.Single(ok);
        await TenancyApi.AssertErrorAsync(Assert.Single(refused), HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(user.Id.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(ok[0])).AccessToken, "sub"));
        Assert.Equal(1, await LoginCountAsync());
        Assert.Single(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded));
        Assert.Equal(0, await TicketCountAsync());
    }
}
