using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → GitHub, step 1 (criterion 2): the start of a GitHub round trip, for every purpose.</summary>
public sealed class GitHubStartTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys, github: true)
{
    private const string Invite43 = "AAAAAAAAAA" + "AAAAAAAAAA" + "AAAAAAAAAA" + "AAAAAAAAAA" + "AAA";

    [Fact]
    public async Task The_start_answers_302_to_githubs_authorization_endpoint_with_the_code_flow_pkce_a_state_and_user_email()   // criterion 2
    {
        var start = await StartAsync("github");

        Assert.Equal("https://github.com/login/oauth/authorize", start.Location.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", start.Query["response_type"]);
        Assert.Equal("S256", start.Query["code_challenge_method"]);   // Amendment A7
        Assert.Equal(43, start.Query["code_challenge"].Length);
        Assert.NotEmpty(start.Query["state"]);
        Assert.Equal("user:email", start.Query["scope"]);   // nothing of OpenID Connect
        Assert.False(start.Query.ContainsKey("nonce"));
        Assert.Equal(AuthAppFactory.GitHubClientId, start.Query["client_id"]);
        Assert.Equal("https://app.example.com/auth/providers/github/callback", start.Query["redirect_uri"]);
        Assert.Equal("select_account", start.Query["prompt"]);
        Assert.False(start.Query.ContainsKey("max_age"));
        Assert.False(start.Query.ContainsKey("response_mode"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?purpose=reauth")]
    [InlineData("?purpose=connect")]
    [InlineData("?invite=" + Invite43)]
    public async Task Every_purpose_asks_github_for_select_account_and_never_max_age(string query)   // criterion 2, Amendment A7
    {
        var start = await StartAsync("github", query);

        Assert.Equal("select_account", start.Query["prompt"]);
        Assert.False(start.Query.ContainsKey("max_age"));
        Assert.Equal("S256", start.Query["code_challenge_method"]);
    }

    [Fact]
    public async Task Google_alone_gets_max_age_0_on_a_reauth()   // the rule of spec 0011, for Google only
    {
        var google = await StartAsync("google", "?purpose=reauth");
        var microsoft = await StartAsync("microsoft", "?purpose=reauth");

        Assert.Equal("0", google.Query["max_age"]);
        Assert.False(microsoft.Query.ContainsKey("max_age"));
        Assert.Equal("login", microsoft.Query["prompt"]);
    }

    [Fact]
    public async Task The_callback_address_comes_from_the_return_address_whatever_the_host_headers_say()   // spec 0012 → Configuration
    {
        var start = await ProviderApi.StartAsync(Browser, "github", configure: request =>
        {
            request.Headers.Host = "evil.test";
            request.Headers.Add("X-Forwarded-Host", "evil.test");
        });

        Assert.Equal("https://app.example.com/auth/providers/github/callback", start.Query["redirect_uri"]);
    }

    [Fact]
    public async Task The_request_forgery_cookie_is_the_one_of_the_other_providers()   // spec 0012, step 1: the state bound to the browser
    {
        var cookie = (await StartAsync("github")).StateCookieHeader;

        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, cookie.SameSite);
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("/auth/providers", cookie.Path.Value);
    }

    [Fact]
    public async Task A_malformed_query_is_400_and_a_github_dropped_from_the_manifest_is_404()   // spec 0011's rules, unchanged
    {
        using var other = await Browser.GetAsync("/auth/providers/github/start?purpose=login");
        await TenancyApi.AssertErrorAsync(other, HttpStatusCode.BadRequest, "invalid_request");

        SetManifest(AuthAppFactory.SelfServiceManifest + AuthAppFactory.PrivateBlock + AuthAppFactory.ProvidersLine, withProviders: false);
        using var dropped = await Browser.GetAsync("/auth/providers/github/start");
        await TenancyApi.AssertErrorAsync(dropped, HttpStatusCode.NotFound, "not_found");
    }
}
