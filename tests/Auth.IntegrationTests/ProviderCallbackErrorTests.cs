using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011, criterion 8: every failure of the callback, one test per code; none stores a ticket or sets <c>auth_pt</c>.</summary>
public sealed class ProviderCallbackErrorTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    /// <summary>Runs the callback and asserts the refusal of the contract: the redirect with the code (and the purpose), nothing stored, no cookie.</summary>
    private async Task AssertRefusedAsync(
        string provider, Func<Task<HttpResponseMessage>> callback, string code, string? purpose, params string[] secrets)
    {
        var before = await TicketCountAsync();

        using var response = await callback();

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.Equal(ProviderApi.ReturnUrl + "#error=" + code + (purpose is null ? "" : "&purpose=" + purpose), location);
        Assert.DoesNotContain(FakeOidcServer.RefusalDescription, location, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeOidcServer.RefusalDescription, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Equal(before, await TicketCountAsync());
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains(code, StringComparison.Ordinal) && e.Message.Contains(provider, StringComparison.Ordinal));

        // No failure path logs what the provider said or what the round trip carries.
        var log = Logs.Text;
        Assert.DoesNotContain(FakeOidcServer.RefusalDescription, log, StringComparison.Ordinal);
        Assert.DoesNotContain("code_verifier", log, StringComparison.Ordinal);
        foreach (var secret in secrets.Where(s => s.Length > 0))
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }
    }

    /// <summary>The values of a round trip that no log line may hold: its state and, when the provider sent one, its code.</summary>
    private static string[] SecretsOf(ProviderApi.Start start, string back) =>
        [start.Query["state"], QueryHelpers.ParseQuery(new Uri("https://app.example.com" + back).Query).TryGetValue("code", out var code) ? code.ToString() : ""];

    private sealed record Trip(Func<Task<HttpResponseMessage>> Callback, string[] Secrets);

    private Task AssertRefusedAsync(string provider, Trip trip, string code, string? purpose) =>
        AssertRefusedAsync(provider, trip.Callback, code, purpose, trip.Secrets);

    private async Task<Trip> RoundTripAsync(string provider, FakeSignIn person, bool withCookie = true)
    {
        var start = await StartAsync(provider);
        var back = await AuthorizeAsync(start, person);
        return new Trip(() => CallbackAsync(back, withCookie ? start.StateCookie : null), SecretsOf(start, back));
    }

    [Fact]
    public async Task Access_denied_with_a_valid_state_and_its_cookie_is_cancelled()   // criterion 8: cancelled
    {
        await AssertRefusedAsync("google", await RoundTripAsync("google", new FakeSignIn(FakePeople.Cancel)), "cancelled", "sign_in");
    }

    [Fact]
    public async Task Access_denied_without_the_request_forgery_cookie_is_invalid_state()   // criterion 8, Amendment A3
    {
        await AssertRefusedAsync("google", await RoundTripAsync("google", new FakeSignIn(FakePeople.Cancel), withCookie: false), "invalid_state", "sign_in");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("forged")]
    [InlineData("expired")]
    [InlineData("replayed")]
    public async Task A_missing_forged_expired_or_replayed_state_is_invalid_state_without_a_purpose(string how)   // criterion 8: invalid_state
    {
        var start = await StartAsync("google");
        var back = await AuthorizeAsync(start, GooglePerson());
        var path = back;
        switch (how)
        {
            case "missing":
                path = "/auth/providers/google/callback?code=abc&iss=" + Uri.EscapeDataString(FakeOidcServer.GoogleIssuer);
                break;
            case "forged":
                path = back.Replace(start.Query["state"], "ZZZZZZZZZZ" + "ZZZZZZZZZZ" + "ZZZZZZZZZZ" + "ZZZZZZZZZZ" + "ZZZ", StringComparison.Ordinal);
                break;
            case "expired":
                Clock.Advance(TimeSpan.FromMinutes(11));
                break;
            default:
                using (var first = await CallbackAsync(back, start.StateCookie))
                {
                    ProviderApi.TicketOf(first);
                }

                break;
        }

        await AssertRefusedAsync("google", () => CallbackAsync(path, start.StateCookie), "invalid_state", null, SecretsOf(start, back));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_valid_state_without_its_cookie_or_with_the_cookie_of_another_round_trip_is_invalid_state_with_its_purpose(bool another)   // criterion 8
    {
        // The other round trip's cookie has another name (the name ends with a value of each round trip): for this state it is a missing cookie.
        var start = await StartAsync("microsoft", "?purpose=reauth");
        var back = await AuthorizeAsync(start, PersonalMicrosoft());
        var other = await StartAsync("microsoft");

        await AssertRefusedAsync(
            "microsoft", () => CallbackAsync(back, another ? other.StateCookie : null), "invalid_state", "reauth", SecretsOf(start, back));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cookie_of_the_right_name_with_a_garbage_value_or_another_browsers_value_is_invalid_state_with_its_purpose(bool garbage)   // criterion 8, Amendment A3
    {
        var start = await StartAsync("microsoft", "?purpose=reauth");
        var back = await AuthorizeAsync(start, PersonalMicrosoft());
        var other = await StartAsync("microsoft");
        var name = start.StateCookie[..start.StateCookie.IndexOf('=', StringComparison.Ordinal)];
        // Another browser's real cookie, under the name of this round trip; or a value that is no cookie of OpenIddict's at all.
        var cookie = name + "=" + (garbage ? "garbage" : other.StateCookieHeader.Value.Value);

        await AssertRefusedAsync("microsoft", () => CallbackAsync(back, cookie), "invalid_state", "reauth", SecretsOf(start, back));
    }

    [Fact]
    public async Task Access_denied_with_a_garbage_request_forgery_cookie_is_invalid_state_not_cancelled()   // criterion 8, Amendment A3
    {
        var start = await StartAsync("google");
        var back = await AuthorizeAsync(start, new FakeSignIn(FakePeople.Cancel));
        var name = start.StateCookie[..start.StateCookie.IndexOf('=', StringComparison.Ordinal)];

        await AssertRefusedAsync("google", () => CallbackAsync(back, name + "=garbage"), "invalid_state", "sign_in", SecretsOf(start, back));
    }

    [Theory]
    [InlineData("google", FakePeople.Google, FakeDefects.RefuseCode)]
    [InlineData("google", FakePeople.Google, FakeDefects.BadSignature)]
    [InlineData("google", FakePeople.Google, FakeDefects.WrongAudience)]
    [InlineData("google", FakePeople.Google, FakeDefects.WrongNonce)]
    [InlineData("google", FakePeople.Google, FakeDefects.WrongIssuer)]
    [InlineData("google", FakePeople.Google, FakeDefects.NoSubject)]
    [InlineData("google", FakePeople.Google, FakeDefects.ProviderError)]
    [InlineData("microsoft", FakePeople.MicrosoftPersonal, FakeDefects.BadSignature)]
    [InlineData("microsoft", FakePeople.MicrosoftWork, FakeDefects.WrongAudience)]
    [InlineData("microsoft", FakePeople.MicrosoftWork, FakeDefects.WrongIssuer)]
    [InlineData("microsoft", FakePeople.MicrosoftWork, FakeDefects.NoTenant)]
    [InlineData("microsoft", FakePeople.MicrosoftWork, FakeDefects.NoObjectId)]
    public async Task A_refused_code_an_identity_token_that_is_not_valid_or_a_missing_claim_is_provider_error(
        string provider, string person, string defect)   // criterion 8: provider_error; Amendment A6 for the Microsoft issuer
    {
        await AssertRefusedAsync(provider, await RoundTripAsync(provider, new FakeSignIn(person, Defect: defect)), "provider_error", "sign_in");
    }

    [Fact]
    public async Task A_refused_client_secret_is_provider_error_and_the_log_names_invalid_client()   // spec 0011 → the runbook: invalid_client in the log
    {
        await AssertRefusedAsync(
            "microsoft", await RoundTripAsync("microsoft", new FakeSignIn(FakePeople.MicrosoftPersonal, Defect: FakeDefects.RefuseClient)), "provider_error", "sign_in");

        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("provider_error", StringComparison.Ordinal) && e.Message.Contains("invalid_client", StringComparison.Ordinal));
        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains(FakeOidcServer.RefusalDescription, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_provider_dropped_from_the_manifest_during_the_round_trip_is_provider_unavailable()   // criterion 8: provider_unavailable
    {
        var callback = await RoundTripAsync("google", GooglePerson());
        SetManifest(AuthAppFactory.SelfServiceManifest + AuthAppFactory.PrivateBlock + "\nproviders: [microsoft]\n", withProviders: false);

        await AssertRefusedAsync("google", callback, "provider_unavailable", "sign_in");
    }

    [Fact]
    public async Task A_name_that_is_no_provider_is_404_and_a_post_is_405()
    {
        using var unknown = await Browser.GetAsync("/auth/providers/github/callback?code=x&state=y", TestContext.Current.CancellationToken);
        using var posted = await Browser.PostAsync(
            "/auth/providers/google/callback", new FormUrlEncodedContent([new("code", "x"), new("state", "y")]), TestContext.Current.CancellationToken);

        await TenancyApi.AssertErrorAsync(unknown, HttpStatusCode.NotFound, "not_found");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, posted.StatusCode);
    }
}
