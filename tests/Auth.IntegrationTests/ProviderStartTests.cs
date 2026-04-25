using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Keys;
using Auth.Server.Lockout;
using Auth.Server.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>GET /auth/providers/{name}/start</c>.</summary>
public sealed class ProviderStartTests : ProviderTestBase
{
    private const string Invite43 = "AAAAAAAAAA" + "AAAAAAAAAA" + "AAAAAAAAAA" + "AAAAAAAAAA" + "AAA";
    private static readonly string[] OpenIdScopes = ["email", "openid", "profile"];

    public ProviderStartTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // As in PrivateSignupRequestTests: no background pass may log statements of its own while a test reads what a request ran.
        Factory.WithoutHostedService<TokenPruningService>().WithoutHostedService<LockoutPruningService>();
    }

    private List<string> StatementsSince(int mark) =>
        [.. Logs.Entries.Skip(mark).Where(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command").Select(e => e.Message)];

    [Theory]
    [InlineData("google", "https://accounts.google.com/o/oauth2/v2/auth", AuthAppFactory.GoogleClientId)]
    [InlineData("microsoft", "https://login.microsoftonline.com/common/oauth2/v2.0/authorize", AuthAppFactory.MicrosoftClientId)]
    public async Task The_start_answers_302_to_the_authorization_endpoint_with_the_code_flow_pkce_state_nonce_and_the_scopes(
        string provider, string endpoint, string clientId)   // criterion 5
    {
        var start = await StartAsync(provider);

        Assert.Equal(endpoint, start.Location.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", start.Query["response_type"]);
        Assert.Equal("S256", start.Query["code_challenge_method"]);
        Assert.Equal(43, start.Query["code_challenge"].Length);
        Assert.NotEmpty(start.Query["state"]);
        Assert.NotEmpty(start.Query["nonce"]);
        Assert.Equal(OpenIdScopes, start.Query["scope"].Split(' ').Order(StringComparer.Ordinal));
        Assert.Equal(clientId, start.Query["client_id"]);
        Assert.Equal($"https://app.example.com/auth/providers/{provider}/callback", start.Query["redirect_uri"]);
        Assert.Equal("select_account", start.Query["prompt"]);
        Assert.False(start.Query.ContainsKey("max_age"));
        Assert.False(start.Query.ContainsKey("response_mode"));   // query, the default of the code flow (probe Q2)
    }

    [Fact]
    public async Task The_request_forgery_cookie_is_lax_secure_http_only_and_scoped_to_the_provider_paths()   // Amendment A4
    {
        var cookie = (await StartAsync("google")).StateCookieHeader;

        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, cookie.SameSite);
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("/auth/providers", cookie.Path.Value);
        Assert.NotEqual(RefreshCookie.Name, cookie.Name.Value);
    }

    [Fact]
    public async Task The_callback_address_comes_from_the_return_address_whatever_the_host_headers_say()   // criterion 5, Review Focus 1
    {
        var start = await ProviderApi.StartAsync(Browser, "google", configure: request =>
        {
            request.Headers.Host = "evil.test";
            request.Headers.Add("X-Forwarded-Host", "evil.test");
            request.Headers.Add("X-Forwarded-Proto", "http");
        });

        Assert.Equal("https://app.example.com/auth/providers/google/callback", start.Query["redirect_uri"]);
    }

    [Theory]
    [InlineData("microsoft", "prompt", "login", "max_age")]
    [InlineData("google", "max_age", "0", "prompt")]
    public async Task Reauth_asks_microsoft_for_a_login_and_google_for_a_fresh_sign_in(string provider, string parameter, string value, string absent)   // criterion 5, Settled 26
    {
        var start = await StartAsync(provider, "?purpose=reauth");

        Assert.Equal(value, start.Query[parameter]);
        Assert.False(start.Query.ContainsKey(absent));
    }

    [Fact]
    public async Task An_invitation_start_carries_neither_the_token_nor_the_parameter_to_the_provider()   // spec 0011 → start, step 3
    {
        var start = await StartAsync("google", "?invite=" + Invite43);

        Assert.DoesNotContain(Invite43, start.Location.AbsoluteUri, StringComparison.Ordinal);
        Assert.False(start.Query.ContainsKey("invite"));
        Assert.Equal("select_account", start.Query["prompt"]);
    }

    [Fact]
    public async Task The_state_of_an_invitation_start_holds_the_hash_of_the_token_and_never_the_token_or_the_address_of_the_request()   // T6, spec 0011 → start, step 3
    {
        await StartAsync("google", "?invite=" + Invite43);

        var payload = await InDbAsync(db => db.Database
            .SqlQueryRaw<string>("""
                SELECT "Payload" AS "Value" FROM "OpenIddictTokens"
                WHERE "Type" = 'urn:openiddict:params:oauth:token-type:state_token'
                ORDER BY "CreationDate" DESC LIMIT 1
                """)
            .SingleAsync(TestContext.Current.CancellationToken));

        // The state is an encrypted token (the instance's encryption key) around a signed one; both layers are read in clear here.
        var encryption = Factory.Services.GetRequiredService<KeyMaterial>().Encryption;
        var handler = new JsonWebTokenHandler();
        var signed = handler.DecryptToken(new JsonWebToken(payload), new TokenValidationParameters { TokenDecryptionKey = new X509SecurityKey(encryption) });
        var claims = new JsonWebToken(signed);
        var text = signed + Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(claims.EncodedPayload));
        Assert.DoesNotContain(Invite43, text, StringComparison.Ordinal);
        Assert.DoesNotContain("invite=", text, StringComparison.Ordinal);

        var items = JsonNode.Parse(claims.GetClaim("oi_hst_props").Value)!.AsObject();
        var stored = items["items"]?.AsObject() ?? items;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Invite43))), (string?)stored["invite_hash"]);
        Assert.Equal("invite", (string?)stored["purpose"]);
        Assert.Equal("google", (string?)stored["provider"]);
        Assert.Equal("/", claims.GetClaim("target_link_uri").Value);
    }

    [Fact]
    public async Task The_start_302_is_not_to_be_stored()   // spec 0011 → General rules: Cache-Control no-store
    {
        using var response = await Browser.GetAsync("/auth/providers/google/start");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("github")]
    [InlineData("Google")]
    [InlineData("password")]
    public async Task An_unknown_name_is_404_not_found(string name)   // criterion 6
    {
        using var response = await Browser.GetAsync($"/auth/providers/{name}/start");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task A_provider_that_is_not_offered_is_404_not_found()   // criterion 6
    {
        SetManifest(AuthAppFactory.DefaultManifest + "\nproviders: [google]\n", withProviders: false);

        using var response = await Browser.GetAsync("/auth/providers/microsoft/start");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    public static TheoryData<string> BadQueries => new()
    {
        "?invite=abc",
        "?invite=" + Invite43 + "A",
        "?invite=" + Invite43[..42] + ".",
        "?invite=" + Invite43[..42] + "%2B",
        "?invite=",
        "?purpose=login",
        "?purpose=sign_in",
        "?purpose=invite",
        "?purpose=",
        "?purpose=REAUTH",
        "?invite=" + Invite43 + "&purpose=reauth",
        "?purpose=reauth&purpose=reauth",
        "?invite=" + Invite43 + "&invite=" + Invite43,
    };

    [Theory]
    [MemberData(nameof(BadQueries))]
    public async Task A_malformed_invite_another_purpose_both_or_one_given_twice_is_400_invalid_request(string query)   // criterion 6
    {
        using var response = await Browser.GetAsync("/auth/providers/google/start" + query);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Theory]
    [InlineData("github", "?purpose=login")]
    [InlineData("password", "?invite=abc")]
    public async Task An_unknown_name_with_a_bad_query_is_404_not_400(string name, string query)   // spec 0011 → start: step 1 comes before step 2
    {
        using var response = await Browser.GetAsync($"/auth/providers/{name}/start{query}");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task A_provider_that_is_not_offered_with_a_bad_query_is_404_not_400()   // spec 0011 → start: step 1 comes before step 2
    {
        SetManifest(AuthAppFactory.DefaultManifest + "\nproviders: [google]\n", withProviders: false);

        using var response = await Browser.GetAsync("/auth/providers/microsoft/start?purpose=login");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Other_parameters_are_ignored_once_or_twice()   // spec 0011 → start, step 2
    {
        var start = await StartAsync("google", "?utm_source=mail&utm_source=again&lang=pl");

        Assert.Equal("select_account", start.Query["prompt"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?purpose=reauth")]
    [InlineData("?invite=" + Invite43)]
    public async Task The_start_reads_no_account_and_no_invitation(string query)   // criterion 6: statement capture
    {
        var mark = Logs.Entries.Count;

        await StartAsync("google", query);

        var statements = StatementsSince(mark);
        Assert.Contains(statements, sql => sql.Contains("\"OpenIddictTokens\"", StringComparison.Ordinal));   // the state, and nothing else
        Assert.DoesNotContain(statements, sql =>
            sql.Contains("\"AspNetUsers\"", StringComparison.Ordinal)
            || sql.Contains("\"Invites\"", StringComparison.Ordinal)
            || sql.Contains("\"PendingSignups\"", StringComparison.Ordinal)
            || sql.Contains("\"Memberships\"", StringComparison.Ordinal));
    }
}
