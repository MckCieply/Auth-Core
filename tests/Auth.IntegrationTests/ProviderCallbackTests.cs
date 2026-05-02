using System.Security.Cryptography;
using System.Text;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>GET /auth/providers/{name}/callback</c>: success.</summary>
public sealed class ProviderCallbackTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Invite43 = "BBBBBBBBBB" + "BBBBBBBBBB" + "BBBBBBBBBB" + "BBBBBBBBBB" + "BBB";

    private static byte[] Sha256(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private Task<List<ProviderTicket>> TicketRowsAsync() =>
        InDbAsync(db => db.ProviderTickets.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    public static TheoryData<string, string, string, string, bool> People => new()
    {
        { "google", FakePeople.Google, FakePeople.GoogleSubject, FakePeople.GoogleEmail, true },
        { "microsoft", FakePeople.MicrosoftPersonal, FakePeople.PersonalTenant + ":" + FakePeople.PersonalObjectId, FakePeople.PersonalEmail, true },
        { "microsoft", FakePeople.MicrosoftWork, FakePeople.WorkTenant + ":" + FakePeople.WorkObjectId, FakePeople.WorkEmail, false },
    };

    [Theory]
    [MemberData(nameof(People))]
    public async Task A_good_code_answers_302_with_a_ticket_sets_auth_pt_and_stores_hashes_only(
        string provider, string person, string key, string email, bool verified)   // criterion 7
    {
        var start = await StartAsync(provider);

        using var callback = await CallbackAsync(await AuthorizeAsync(start, new FakeSignIn(person)), start.StateCookie);

        var fragment = ProviderApi.FragmentOf(callback);
        Assert.Equal(["purpose", "ticket"], fragment.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("sign_in", fragment["purpose"]);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", fragment["ticket"]);
        Assert.True(callback.Headers.CacheControl?.NoStore);
        Assert.Equal("", await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var cookie = ProviderApi.BindingCookieOf(callback)!;
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.Equal("/auth", cookie.Path.Value);
        Assert.Equal(TimeSpan.FromSeconds(600), cookie.MaxAge);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", cookie.Value.Value!);

        var row = Assert.Single(await TicketRowsAsync());
        Assert.Equal(Sha256(fragment["ticket"]), row.TicketHash);
        Assert.Equal(Sha256(cookie.Value.Value!), row.BindingHash);
        Assert.Equal(ProviderTicketPurpose.SignIn, row.Purpose);
        Assert.Equal(provider, row.Provider);
        Assert.Equal(key, row.IdentityKey);
        Assert.Equal(email, row.Email);
        Assert.Equal(verified, row.EmailVerified);
        Assert.Null(row.InviteTokenHash);
        Assert.Equal(StorableTime.Now(Clock) + TimeSpan.FromMinutes(10), row.ExpiresAt);
        // No column holds the ticket or the cookie in clear.
        var text = row.Provider + row.IdentityKey + row.Email;
        Assert.DoesNotContain(fragment["ticket"], text, StringComparison.Ordinal);
        Assert.DoesNotContain(cookie.Value.Value!, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invitation_and_a_reauth_come_back_with_their_purpose_and_their_ticket_with_its_lifetime()   // criterion 7
    {
        var invite = await TicketAsync("google", GooglePerson(), "?invite=" + Invite43);
        var reauth = await TicketAsync("microsoft", PersonalMicrosoft(), "?purpose=reauth");

        Assert.Equal("invite", invite.Purpose);
        Assert.Equal("reauth", reauth.Purpose);
        var rows = await TicketRowsAsync();
        Assert.Equal(Sha256(Invite43), rows.Single(r => r.Purpose == ProviderTicketPurpose.Invite).InviteTokenHash);
        Assert.Equal(StorableTime.Now(Clock) + TimeSpan.FromMinutes(5), rows.Single(r => r.Purpose == ProviderTicketPurpose.Reauth).ExpiresAt);
    }

    [Fact]
    public async Task No_state_value_and_no_code_verifier_reaches_the_log()   // criterion 7, Amendment A1
    {
        var start = await StartAsync("google");
        var back = await AuthorizeAsync(start, GooglePerson());

        using var callback = await CallbackAsync(back, start.StateCookie);

        var ticket = ProviderApi.TicketOf(callback);
        var code = QueryHelpers.ParseQuery(new Uri("https://app.example.com" + back).Query)["code"].ToString();
        var text = Logs.Text;
        Assert.DoesNotContain(start.Query["state"], text, StringComparison.Ordinal);
        Assert.DoesNotContain(code, text, StringComparison.Ordinal);
        Assert.DoesNotContain("code_verifier", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket.Value, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket.Binding, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Assert.Single(Fake.Server.IssuedIdentityTokens), text, StringComparison.Ordinal);
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("google", StringComparison.Ordinal) && e.Message.Contains("sign_in", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("google", FakePeople.Google)]
    [InlineData("microsoft", FakePeople.MicrosoftPersonal)]
    public async Task The_client_never_calls_the_userinfo_endpoint_of_the_provider(string provider, string person)   // Amendment: userinfo is off
    {
        var start = await StartAsync(provider);

        using var callback = await CallbackAsync(await AuthorizeAsync(start, new FakeSignIn(person)), start.StateCookie);

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);
        Assert.Equal(0, Fake.Server.UserInfoRequests);
    }

    [Fact]
    public async Task A_callback_that_reaches_the_service_under_another_host_and_scheme_still_makes_a_ticket()   // Review Focus 1, Amendment A2
    {
        var start = await StartAsync("google");

        using var callback = await ProviderApi.CallbackAsync(Browser, await AuthorizeAsync(start, GooglePerson()), start.StateCookie, request =>
        {
            request.Headers.Host = "auth:8080";
            request.Headers.Add("X-Forwarded-Host", "evil.test");
            request.Headers.Add("X-Forwarded-Proto", "http");
        });

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);
    }

    [Fact]
    public async Task The_state_still_works_after_nine_minutes()   // spec 0011 → start: the state lives 10 minutes
    {
        var start = await StartAsync("microsoft");
        Clock.Advance(TimeSpan.FromMinutes(9));

        using var callback = await CallbackAsync(await AuthorizeAsync(start, PersonalMicrosoft()), start.StateCookie);

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);
    }

    [Fact]
    public async Task Every_round_trip_makes_its_own_ticket_and_binding()
    {
        var first = await TicketAsync("google", GooglePerson());
        var second = await TicketAsync("google", GooglePerson());

        Assert.NotEqual(first.Value, second.Value);
        Assert.NotEqual(first.Binding, second.Binding);
        Assert.Equal(2, (await TicketRowsAsync()).Count);
    }
}
