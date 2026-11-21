using System.Net;
using System.Text;
using System.Text.Json;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0015 → <c>POST /auth/switch</c>, part 1: the path, its body checks, its answers and its limit. The switch itself is a refresh in the
/// session's own company here; what these tests pin is what a person sees when the request is wrong, and that a refusal leaves the cookie
/// untouched (the token is not used up).
/// </summary>
public sealed class SwitchRequestTests : TenancyTestBase
{
    private const string AnId = "11111111-1111-1111-1111-111111111111";

    private readonly DatabaseOutage _outage;

    public SwitchRequestTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        _outage = new DatabaseOutage(postgres.ConnectionStringFor(Factory.DatabaseName));
        Factory.WithSetting("ConnectionStrings:Auth", _outage.ConnectionString);
    }

    public override async ValueTask DisposeAsync()
    {
        await _outage.DisposeAsync();
        await base.DisposeAsync();
    }

    public static TheoryData<string> MalformedBodies => new()
    {
        "not json at all",
        "[]",
        "{}",
        """{"org_id":1}""",
        """{"org_id":null}""",
        """{"org_id":"x"}""",
        $$"""{"org_id":"{{AnId}}","padding":"{{new string('x', 9000)}}"}""",   // over 8 KiB
    };

    public static TheoryData<string> BadSpellingsOfAnId => new()
    {
        "{" + AnId + "}",
        "(" + AnId + ")",
        AnId.Replace("-", string.Empty, StringComparison.Ordinal),   // the 32 digits, the "N" form
        " " + AnId,
        AnId + " ",
        string.Empty,
    };

    /// <summary>A <c>400 invalid_request</c>: its status, its error member and no cookie, never the whole body (it may carry a description).</summary>
    private static async Task AssertInvalidRequestAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400, got {(int)response.StatusCode}: {raw}");
        using var body = JsonDocument.Parse(raw);
        Assert.Equal("invalid_request", body.RootElement.GetProperty("error").GetString());
        Assert.False(response.Headers.Contains("Set-Cookie"), "A refused switch must not write the cookie.");
    }

    /// <summary>The cookie of a refused switch is as good as before: its stored token is still valid, and it refreshes.</summary>
    private async Task AssertCookieIntactAsync(string refreshToken)
    {
        Assert.Equal("valid", await StoredTokens.StatusAsync(Factory, refreshToken));
        await SessionApi.RefreshOk(Client, refreshToken);
    }

    private static async Task AssertUnsupportedAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnsupportedMediaType, $"Expected 415, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("""{"error":"unsupported_media_type"}""", raw);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task A_malformed_body_or_org_id_is_400_invalid_request_and_the_cookie_still_refreshes(string body)   // criterion 18
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        using var response = await SessionApi.Switch(Client, session.RefreshToken, body);

        await AssertInvalidRequestAsync(response);
        await AssertCookieIntactAsync(session.RefreshToken);
    }

    [Fact]
    public async Task A_body_of_another_media_type_is_400_and_a_form_post_too()   // criterion 18
    {
        var session = await SessionApi.LoginAsync(Client, Factory);
        var json = $$"""{"org_id":"{{AnId}}"}""";

        using var plain = await SessionApi.Switch(Client, session.RefreshToken, json, "text/plain");
        using var form = await SessionApi.Switch(Client, session.RefreshToken, "org_id=" + AnId, "application/x-www-form-urlencoded");
        var request = new HttpRequestMessage(HttpMethod.Post, SessionApi.SwitchPath) { Content = new StringContent(json, Encoding.UTF8) };
        request.Content.Headers.ContentType = null;
        request.Headers.TryAddWithoutValidation("Cookie", $"{SessionApi.CookieName}={session.RefreshToken}");
        using var untyped = await Client.SendAsync(request);

        await AssertInvalidRequestAsync(plain);
        await AssertInvalidRequestAsync(form);
        await AssertInvalidRequestAsync(untyped);
        await AssertCookieIntactAsync(session.RefreshToken);
    }

    [Fact]
    public async Task Json_with_another_charset_is_415_and_the_cookie_still_refreshes()   // criterion 18
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        using var response = await SessionApi.Switch(Client, session.RefreshToken, $$"""{"org_id":"{{AnId}}"}""", "application/json; charset=utf-16");

        await AssertUnsupportedAsync(response);
        await AssertCookieIntactAsync(session.RefreshToken);
    }

    [Theory]
    [MemberData(nameof(BadSpellingsOfAnId))]
    public async Task An_org_id_with_braces_without_hyphens_or_with_spaces_is_400(string orgId)   // Review Focus 2
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        using var response = await SessionApi.Switch(Client, session.RefreshToken, JsonSerializer.Serialize(new { org_id = orgId }));

        await AssertInvalidRequestAsync(response);
        await AssertCookieIntactAsync(session.RefreshToken);
    }

    /// <summary>A body whose length the request does not declare (chunked), so only the bounded read can stop it.</summary>
    private sealed class ChunkedContent(string text) : HttpContent
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(text);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task A_body_over_8_KiB_without_a_declared_length_is_400_and_the_cookie_still_refreshes()   // criterion 18
    {
        var session = await SessionApi.LoginAsync(Client, Factory);
        var request = new HttpRequestMessage(HttpMethod.Post, SessionApi.SwitchPath)
        {
            Content = new ChunkedContent($$"""{"org_id":"{{AnId}}","padding":"{{new string('x', 9000)}}"}"""),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Cookie", $"{SessionApi.CookieName}={session.RefreshToken}");

        using var response = await Client.SendAsync(request);

        await AssertInvalidRequestAsync(response);
        await AssertCookieIntactAsync(session.RefreshToken);
    }

    [Theory]
    [InlineData("/auth/switch/")]
    [InlineData("/AUTH/Switch")]
    [InlineData("/auth/SWITCH/")]
    public async Task The_path_is_taken_without_regard_to_case_or_a_trailing_slash(string path)   // as the refresh path is
    {
        var session = await SessionApi.LoginAsync(Client, Factory);
        var valid = $$"""{"org_id":"{{AnId}}"}""";

        using var badBody = await SessionApi.Switch(Client, session.RefreshToken, "{}", path: path);
        using var noCookie = await SessionApi.Switch(Client, null, valid, path: path);
        using var ok = await SessionApi.Switch(Client, session.RefreshToken, valid, path: path);

        await AssertInvalidRequestAsync(badBody);
        await SessionApi.AssertInvalidGrantAsync(noCookie);
        var next = await SessionApi.ReadSessionAsync(ok);   // 200, the body and the cookie of a refresh
        await SessionApi.RefreshOk(Client, next.RefreshToken);
    }

    [Fact]
    public async Task A_get_is_400_invalid_request()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, SessionApi.SwitchPath);
        request.Headers.TryAddWithoutValidation("Cookie", $"{SessionApi.CookieName}={session.RefreshToken}");

        using var response = await Client.SendAsync(request);

        await AssertInvalidRequestAsync(response);
        await AssertCookieIntactAsync(session.RefreshToken);
    }

    [Fact]
    public async Task Without_a_cookie_it_is_the_uniform_401()   // criterion 19
    {
        using var refresh = await SessionApi.Refresh(Client, refreshToken: null);
        using var missing = await SessionApi.SwitchTo(Client, null, Guid.NewGuid());
        using var empty = await SessionApi.SwitchTo(Client, "", Guid.NewGuid());
        using var unknown = await SessionApi.SwitchTo(Client, "not-a-real-reference-0123456789abcdef", Guid.NewGuid());

        foreach (var response in new[] { missing, empty, unknown })
        {
            await SessionApi.AssertInvalidGrantAsync(response);
            Assert.Equal(refresh.Content.Headers.ContentType, response.Content.Headers.ContentType);
            Assert.Equal(LoginApi.HeaderNames(refresh), LoginApi.HeaderNames(response));
        }

        // The body is judged before the cookie: a malformed body without a cookie is the 400.
        using var badBody = await SessionApi.Switch(Client, null, "{}");
        await AssertInvalidRequestAsync(badBody);
    }

    [Fact]
    public async Task An_expired_revoked_or_reused_cookie_a_changed_stamp_a_passed_cap_or_a_removed_session_company_is_the_uniform_401()   // criterion 19
    {
        var refresh = await SessionApi.Refresh(Client, refreshToken: null);
        var failures = new List<(string Name, HttpResponseMessage Response)>();

        // expired: unused for longer than the sliding window
        var expired = await SessionApi.LoginAsync(Client, Factory);
        Clock.Advance(TimeSpan.FromDays(14) + TimeSpan.FromMinutes(1));
        failures.Add(("expired", await SessionApi.SwitchTo(Client, expired.RefreshToken, Guid.NewGuid())));

        // passed cap: used within the window, but past the 30 days since the sign-in
        var capped = await SessionApi.LoginAsync(Client, Factory);
        Clock.Advance(TimeSpan.FromDays(13));
        var day13 = await SessionApi.RefreshOk(Client, capped.RefreshToken);
        Clock.Advance(TimeSpan.FromDays(13));
        var day26 = await SessionApi.RefreshOk(Client, day13.RefreshToken);
        Clock.Advance(TimeSpan.FromDays(4) + TimeSpan.FromMinutes(1));
        failures.Add(("cap", await SessionApi.SwitchTo(Client, day26.RefreshToken, Guid.NewGuid())));

        // reused: a used-up token presented after its grace window
        var reused = await SessionApi.LoginAsync(Client, Factory);
        await SessionApi.RefreshOk(Client, reused.RefreshToken);
        Clock.Advance(TimeSpan.FromSeconds(20));
        failures.Add(("reused", await SessionApi.SwitchTo(Client, reused.RefreshToken, Guid.NewGuid())));

        // revoked
        var revoked = await SessionApi.LoginAsync(Client, Factory);
        using (var scope = Factory.Services.CreateScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            var entry = await tokens.FindByReferenceIdAsync(revoked.RefreshToken, TestContext.Current.CancellationToken);
            Assert.True(await tokens.TryRevokeAsync(entry!, TestContext.Current.CancellationToken));
        }

        failures.Add(("revoked", await SessionApi.SwitchTo(Client, revoked.RefreshToken, Guid.NewGuid())));

        // changed stamp: a password change ends the session
        var stamped = await SessionApi.LoginAsync(Client, Factory);
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(Factory.SeedEmail))!;
            Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        }

        failures.Add(("stamp", await SessionApi.SwitchTo(Client, stamped.RefreshToken, Guid.NewGuid())));

        // removed session company: the membership the session names is gone (the stamp is the account's, so log in again after it)
        var user2 = await Factory.SeedUserIdAsync();
        var removed = await SessionApi.LoginAsync(Client, Factory);
        await RemoveMembershipAsync(user2);
        failures.Add(("removed", await SessionApi.SwitchTo(Client, removed.RefreshToken, Guid.NewGuid())));

        using (refresh)
        {
            foreach (var (name, response) in failures)
            {
                using (response)
                {
                    var raw = await response.Content.ReadAsStringAsync();
                    Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{name}: expected 401, got {(int)response.StatusCode}: {raw}");
                    Assert.Equal("""{"error":"invalid_grant"}""", raw);
                    Assert.False(response.Headers.Contains("Set-Cookie"), name);
                    Assert.Equal(refresh.Content.Headers.ContentType, response.Content.Headers.ContentType);
                    Assert.Equal(LoginApi.HeaderNames(refresh), LoginApi.HeaderNames(response));
                }
            }
        }
    }

    [Fact]
    public async Task A_failing_database_is_503_with_no_body_and_no_cookie_and_the_cookie_still_refreshes_afterwards()   // criterion 18
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        _outage.Cut();
        using (var during = await SessionApi.SwitchTo(Client, session.RefreshToken, Guid.NewGuid()))
        {
            await AssertUnavailableAsync(during);
        }

        _outage.Start();
        await AssertCookieIntactAsync(session.RefreshToken);   // neither used up nor revoked
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"Expected 503, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("""{"error":"temporarily_unavailable"}""", raw);
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        SecurityHeadersApi.AssertSecurityHeaders(response);
        Assert.False(response.Headers.Contains("Set-Cookie"), "The cookie is neither cleared nor rotated.");
    }

    [Fact]
    public async Task A_valid_switch_answers_as_a_refresh()   // this task's stub
    {
        var viaRefresh = await SessionApi.LoginAsync(Client, Factory);
        var viaSwitch = await SessionApi.LoginAsync(Client, Factory);

        using var refreshed = await SessionApi.Refresh(Client, viaRefresh.RefreshToken);
        using var switched = await SessionApi.SwitchTo(Client, viaSwitch.RefreshToken, Guid.NewGuid());   // the stub ignores org_id

        var raw = await switched.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        using var body = JsonDocument.Parse(raw);
        Assert.Equal(["access_token"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(LoginApi.HeaderNames(refreshed), LoginApi.HeaderNames(switched));

        var expected = SessionApi.RefreshCookieOf(refreshed);
        var cookie = SessionApi.RefreshCookieOf(switched);
        Assert.NotEqual(viaSwitch.RefreshToken, cookie.Value.Value);
        Assert.DoesNotContain(cookie.Value.Value!, raw, StringComparison.Ordinal);
        Assert.Equal(expected.HttpOnly, cookie.HttpOnly);
        Assert.Equal(expected.Secure, cookie.Secure);
        Assert.Equal(expected.SameSite, cookie.SameSite);
        Assert.Equal(expected.Path, cookie.Path);
        Assert.Equal(expected.MaxAge, cookie.MaxAge);
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal("/auth", cookie.Path.Value);

        Assert.Equal("redeemed", await StoredTokens.StatusAsync(Factory, viaSwitch.RefreshToken));   // used up, as a refresh uses it up
        await SessionApi.RefreshOk(Client, cookie.Value.Value!);   // and the chain goes on
    }

    [Fact]
    public async Task Answers_are_never_stored()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        using var ok = await SessionApi.SwitchTo(Client, session.RefreshToken, Guid.NewGuid());
        using var badRequest = await SessionApi.Switch(Client, session.RefreshToken, "{}");
        using var unauthorized = await SessionApi.SwitchTo(Client, null, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        foreach (var response in new[] { ok, badRequest, unauthorized })
        {
            Assert.True(response.Headers.CacheControl?.NoStore, $"{(int)response.StatusCode}: Cache-Control: no-store expected");
            Assert.Contains(response.Headers.Pragma, p => p.Name == "no-cache");
        }
    }
}
