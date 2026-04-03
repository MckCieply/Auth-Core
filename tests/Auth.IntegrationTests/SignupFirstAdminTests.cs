using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class SignupFirstAdminTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string Password = "Chosen-Passw0rd";

    /// <summary>A host with this manifest (it must say <c>signup: self-service</c>), a completed sign-up, and the roles and permissions of the first admin's token.</summary>
    private async Task<(string[] Roles, string[] Permissions)> FirstAdminAsync(string manifest)
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup().WithManifest(manifest);
        using var client = SessionApi.CreateClient(factory);
        var token = await PendingSignupSeed.IssueAsync(factory.Services, "boss@firma.test", "Firma");

        using var done = await SignupApi.Complete(client, token, Password, "Firma");
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        var session = await SessionApi.LoginAsync(client, "boss@firma.test", Password);
        return (AccessTokens.Array(session.AccessToken, "roles"), AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task The_first_admin_gets_the_first_default_role_that_holds_the_star()   // criterion 10
    {
        var (roles, permissions) = await FirstAdminAsync("""
            permissions: [docs:read]
            signup: self-service
            default_roles:
              viewer: [docs:read]
              boss: ["*"]
              admin: ["*"]
            """);

        Assert.Equal(["boss"], roles);
        Assert.Contains("members:manage", permissions);
        Assert.Contains("docs:read", permissions);
    }

    [Fact]
    public async Task Without_a_star_the_first_admin_gets_the_first_role_that_holds_members_manage()   // criterion 10
    {
        var (roles, permissions) = await FirstAdminAsync("""
            permissions: [docs:read]
            signup: self-service
            default_roles:
              viewer: [docs:read]
              lead: [members:manage, docs:read]
              deputy: [members:manage, roles:manage]
            """);

        Assert.Equal(["lead"], roles);
        Assert.Equal(["docs:read", "members:manage"], permissions);
    }

    [Fact]
    public async Task A_star_role_after_a_members_manage_role_still_wins()   // criterion 10
    {
        var (roles, _) = await FirstAdminAsync("""
            permissions: [docs:read]
            signup: self-service
            default_roles:
              lead: [members:manage]
              owner: ["*"]
            """);

        Assert.Equal(["owner"], roles);
    }
}
