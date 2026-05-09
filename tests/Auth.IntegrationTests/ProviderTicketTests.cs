using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011, criterion 14: one answer for every dead ticket, and one sign-in of parallel exchanges.</summary>
public sealed class ProviderTicketTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private async Task<ProviderApi.Ticket> LinkedTicketAsync(string query = "")
    {
        var user = await CreateUserAsync(FakePeople.GoogleEmail, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        return await TicketAsync("google", GooglePerson(), query);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("used")]
    [InlineData("expired")]
    [InlineData("reauth")]
    [InlineData("no-binding")]
    [InlineData("another-binding")]
    public async Task A_ticket_that_is_unknown_used_expired_of_another_purpose_or_without_its_binding_is_invalid_ticket(string how)   // criterion 14, Review Focus 2
    {
        var ticket = await LinkedTicketAsync(how == "reauth" ? "?purpose=reauth" : "");
        var value = ticket.Value;
        string? binding = ticket.Binding;
        switch (how)
        {
            case "unknown":
                value = "unknownunknown" + ticket.Value[14..];
                break;
            case "used":
                using (var first = await ProviderApi.Exchange(Client, ticket))
                {
                    Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                }

                break;
            case "expired":
                Clock.Advance(TimeSpan.FromMinutes(10));
                break;
            case "no-binding":
                binding = null;
                break;
            case "another-binding":
                binding = (await TicketAsync("microsoft", PersonalMicrosoft())).Binding;
                break;
        }

        using var response = await ProviderApi.Exchange(Client, value, binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
    }

    [Fact]
    public async Task Of_parallel_exchanges_with_one_ticket_exactly_one_signs_in()   // criterion 14
    {
        var ticket = await LinkedTicketAsync();

        var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ProviderApi.Exchange(Client, ticket)));
        try
        {
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            Assert.Equal(4, answers.Count(a => a.StatusCode == HttpStatusCode.BadRequest));
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }

        Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task A_second_round_trip_in_the_same_browser_makes_the_first_ticket_unusable()   // Settled 9, Review Focus 2
    {
        var first = await LinkedTicketAsync();
        var second = await TicketAsync("google", GooglePerson());

        // The browser holds the auth_pt of the second round trip only: the callback replaced it.
        using var older = await ProviderApi.Exchange(Client, first.Value, second.Binding);
        using var newer = await ProviderApi.Exchange(Client, second);

        await TenancyApi.AssertErrorAsync(older, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(HttpStatusCode.OK, newer.StatusCode);
    }

    [Fact]
    public async Task A_connect_ticket_is_refused_by_the_exchange_the_link_and_the_finish_as_invalid_ticket()   // spec 0012, criterion 14
    {
        await CreateUserAsync(FakePeople.GoogleEmail, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=connect");
        Assert.Equal("connect", ticket.Purpose);   // the fragment says purpose=connect

        using var exchanged = await ProviderApi.Exchange(Client, ticket);
        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);
        using var finished = await ProviderApi.Finish(Client, ticket, "private");

        await TenancyApi.AssertErrorAsync(exchanged, HttpStatusCode.BadRequest, "invalid_ticket");
        await TenancyApi.AssertErrorAsync(linked, HttpStatusCode.BadRequest, "invalid_ticket");
        await TenancyApi.AssertErrorAsync(finished, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Null(ProviderApi.BindingCookieOf(exchanged));   // none of them used the ticket up, so none cleared auth_pt
        Assert.Null(ProviderApi.BindingCookieOf(linked));
        Assert.Null(ProviderApi.BindingCookieOf(finished));
        Assert.Equal(0, await InDbAsync(db => db.UserLogins.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_connect_ticket_is_refused_by_the_account_deletion_and_the_confirmation_as_reauth_failed()   // spec 0012, criterion 14
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=connect");

        using var confirm = await SettingsApi.ConfirmWithTicket(Client, session, ticket);
        using var deleteAccount = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = ticket.Value }, ticket.Binding);

        await TenancyApi.AssertErrorAsync(confirm, HttpStatusCode.Forbidden, "reauth_failed");
        await TenancyApi.AssertErrorAsync(deleteAccount, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Null(ProviderApi.BindingCookieOf(confirm));
        Assert.Null(ProviderApi.BindingCookieOf(deleteAccount));
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Email == FakePeople.GoogleEmail, TestContext.Current.CancellationToken)));   // the account is still there
    }

    [Fact]
    public async Task A_connect_ticket_lives_ten_minutes()   // spec 0012 → Provider tickets
    {
        var before = StorableTimeNow();
        await TicketAsync("google", GooglePerson(), "?purpose=connect");

        var expires = await InDbAsync(db => db.ProviderTickets.Select(t => t.ExpiresAt).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(before + TimeSpan.FromMinutes(10), expires);
    }

    [Fact]
    public async Task A_connect_ticket_is_refused_by_the_company_deletion_as_reauth_failed()   // spec 0012, criterion 14
    {
        var (company, adminId, token) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        await InDbAsync(db => db.Users.Where(u => u.Id == adminId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken));
        await LinkAsync(adminId, "google", "100000000000000000055");
        var ticket = await TicketAsync("google", GooglePerson(email: "boss@acme.test", subject: "100000000000000000055"), "?purpose=connect");

        using var response = await OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == company, TestContext.Current.CancellationToken)));
    }

    private DateTimeOffset StorableTimeNow() => Auth.Server.Email.StorableTime.Now(Clock);
}
