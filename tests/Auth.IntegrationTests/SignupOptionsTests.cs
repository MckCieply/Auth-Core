using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection;

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
        Assert.Equal("""{"signup":"invite-only","private_signup":"off","providers":[],"org_creation":"off"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Options_say_self_service_with_the_key_and_the_address()   // criterion 1
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup();
        using var client = factory.CreateClient();

        using var response = await SignupApi.Options(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"signup":"self-service","private_signup":"off","providers":[],"org_creation":"off"}""", await response.Content.ReadAsStringAsync());
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

    private static async Task<string> BodyAsync(HttpClient client)
    {
        using var response = await SignupApi.Options(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Options_say_org_creation_off_by_default()   // spec 0016
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        using var client = factory.CreateClient();

        Assert.Equal("""{"signup":"invite-only","private_signup":"off","providers":[],"org_creation":"off"}""", await BodyAsync(client));
    }

    [Fact]
    public async Task Options_say_org_creation_self_service_when_the_manifest_does()   // spec 0016
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithOrgCreation();
        using var client = factory.CreateClient();

        Assert.Contains("\"org_creation\":\"self-service\"}", await BodyAsync(client), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_member_comes_last()   // spec 0016
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithOrgCreation();
        using var client = factory.CreateClient();

        using var body = System.Text.Json.JsonDocument.Parse(await BodyAsync(client));

        Assert.Equal(["signup", "private_signup", "providers", "org_creation"], body.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_manifest_change_reaches_the_options_at_once()   // spec 0016
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        using var client = factory.CreateClient();
        var holder = factory.Services.GetRequiredService<ManifestHolder>();

        Assert.Contains("\"org_creation\":\"off\"}", await BodyAsync(client), StringComparison.Ordinal);

        holder.Set(ManifestParser.Parse(AuthAppFactory.OrgCreationManifest).Manifest!, null);
        Assert.Contains("\"org_creation\":\"self-service\"}", await BodyAsync(client), StringComparison.Ordinal);

        holder.Set(ManifestParser.Parse(AuthAppFactory.DefaultManifest).Manifest!, null);
        Assert.Contains("\"org_creation\":\"off\"}", await BodyAsync(client), StringComparison.Ordinal);
    }
}
