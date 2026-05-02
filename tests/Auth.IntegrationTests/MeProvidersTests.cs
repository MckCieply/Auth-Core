using System.Net;
using System.Text.Json;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>GET /auth/me</c>: <c>has_password</c> and <c>linked_providers</c> (criterion 26).</summary>
public sealed class MeProvidersTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;
    private static readonly string[] SessionClaims = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"];

    private async Task<JsonElement> MeAsync(string accessToken)
    {
        using var response = await TenancyApi.Get(Client, "/auth/me", accessToken);
        return await TenancyApi.ReadOkAsync(response);
    }

    private static string[] Providers(JsonElement me) => [.. me.GetProperty("linked_providers").EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public async Task An_account_with_a_password_and_no_provider_has_a_password_and_no_providers()   // criterion 26
    {
        var me = await MeAsync((await SessionApi.LoginAsync(Client, Factory)).AccessToken);

        Assert.True(me.GetProperty("has_password").GetBoolean());
        Assert.Empty(Providers(me));
    }

    [Fact]
    public async Task An_account_with_a_password_and_both_providers_lists_them_sorted()   // criterion 26
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "microsoft", FakePeople.PersonalTenant + ":" + FakePeople.PersonalObjectId);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);

        var me = await MeAsync((await SessionApi.LoginAsync(Client, Ola, UserPassword)).AccessToken);

        Assert.True(me.GetProperty("has_password").GetBoolean());
        Assert.Equal(["google", "microsoft"], Providers(me));
    }

    [Fact]
    public async Task Two_logins_of_one_provider_are_listed_once()   // criterion 26: the providers of the logins, not the logins
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        await LinkAsync(user.Id, "google", "100000000000000000002");

        var me = await MeAsync((await SessionApi.LoginAsync(Client, Ola, UserPassword)).AccessToken);

        Assert.Equal(["google"], Providers(me));
    }

    [Fact]
    public async Task An_account_with_a_provider_only_has_no_password()   // criterion 26
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        var me = await MeAsync(session.AccessToken);

        Assert.False(me.GetProperty("has_password").GetBoolean());
        Assert.Equal(["google"], Providers(me));
    }

    [Fact]
    public async Task A_password_login_for_an_account_without_a_password_is_the_401_of_an_unknown_address()   // criterion 26
    {
        await PrivateThroughProviderAsync("google", GooglePerson());

        using var login = await LoginApi.Login(Client, Ola, "Any-Passw0rd-1");

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await login.Content.ReadAsStringAsync());
        Assert.Equal("unknown_address", AuditApi.Text(await SingleAsync(AuditKinds.LoginFailed), "reason"));
    }

    [Fact]
    public async Task A_provider_sign_in_issues_exactly_the_claims_of_a_password_sign_in()   // spec 0011 → /auth/me: the access token gains no claim
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        Assert.Equal(SessionClaims, AccessTokens.Payload(session.AccessToken).Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_password_sign_in_of_the_same_account_issues_the_same_claims_as_its_provider_sign_in()   // criterion 10: the two tokens carry the same members
    {
        var (_, provider) = await PrivateThroughProviderAsync("google", GooglePerson());
        using (var forgot = await AccountApi.Forgot(Client, Ola))
        {
            await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        using (var reset = await AccountApi.Reset(Client, TokenIn(Mail.Sent.Last(m => m.To == Ola)), "Chosen-Passw0rd"))
        {
            await AccountApi.AssertEmptyAsync(reset, HttpStatusCode.NoContent);
        }

        var password = await SessionApi.LoginAsync(Client, Ola, "Chosen-Passw0rd");

        Assert.Equal(
            AccessTokens.Payload(provider.AccessToken).Select(p => p.Key).Order(StringComparer.Ordinal),
            AccessTokens.Payload(password.AccessToken).Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_answer_gives_has_password_and_the_provider_names_and_nothing_of_the_identity()   // criterion 26
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        using var response = await TenancyApi.Get(Client, "/auth/me", session.AccessToken);
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"has_password\":false", raw, StringComparison.Ordinal);
        Assert.Contains("\"linked_providers\":[\"google\"]", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePeople.GoogleSubject, raw, StringComparison.Ordinal);
    }
}
