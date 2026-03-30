using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class SignupOptionsTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    [Theory]
    [MemberData(nameof(SignupVariants.Off), MemberType = typeof(SignupVariants))]
    public async Task Options_say_invite_only_while_sign_up_is_off(string variant)   // criterion 1
    {
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), variant);
        using var client = factory.CreateClient();

        using var response = await SignupApi.Options(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"signup":"invite-only"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Options_say_self_service_with_the_key_and_the_address()   // criterion 1
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup();
        using var client = factory.CreateClient();

        using var response = await SignupApi.Options(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"signup":"self-service"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Options_need_no_token_are_never_stored_set_no_cookie_have_the_security_headers_and_are_json()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup();
        using var client = factory.CreateClient();

        using var response = await SignupApi.Options(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        AccountApi.AssertNeverStoredAndNoCookie(response);
        SecurityHeadersApi.AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task Options_follow_the_manifest_the_host_started_with()   // a second host on the same database does not change the first
    {
        await using var on = new AuthAppFactory(postgres, keys).WithSignup();
        using var onClient = on.CreateClient();

        using var first = await SignupApi.Options(onClient);
        Assert.Contains("self-service", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The second host activates its own file on the same database, so the stored manifest becomes invite-only.
        await using var off = new AuthAppFactory(postgres, keys, on.DatabaseName);
        using var offClient = off.CreateClient();

        using var second = await SignupApi.Options(offClient);
        using var third = await SignupApi.Options(onClient);

        Assert.Contains("invite-only", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("self-service", await third.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
