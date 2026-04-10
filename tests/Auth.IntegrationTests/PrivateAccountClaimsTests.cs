using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Access token and <c>GET /auth/me</c>, and the private role's permissions read live from the manifest.</summary>
public sealed class PrivateAccountClaimsTests : TenancyTestBase
{
    private const string Private = "ola@poczta.test";

    public PrivateAccountClaimsTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The private role grants reports:read and reports:approve; the switch does not matter for signing in.
        Factory.WithManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.PrivateBlock);
    }

    [Fact]
    public async Task A_private_account_signs_in_with_its_space_the_private_role_and_the_manifests_permissions()   // criteria 9, 12
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);

        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);

        Assert.Equal(user.ToString(), AccessTokens.Text(session.AccessToken, "sub"));
        Assert.Equal(space.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal("personal", AccessTokens.Text(session.AccessToken, "org_kind"));
        Assert.Equal(["private"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(["reports:approve", "reports:read"], AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task A_company_members_token_says_organization_and_otherwise_holds_the_claims_it_held()   // criterion 12
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        Assert.Equal("organization", AccessTokens.Text(session.AccessToken, "org_kind"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(
            ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"],
            AccessTokens.Payload(session.AccessToken).Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Me_answers_org_kind_for_both_kinds_and_private_for_a_private_account()   // criterion 12
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var privateToken = (await SessionApi.LoginAsync(Client, Private, UserPassword)).AccessToken;
        var memberToken = (await SessionApi.LoginAsync(Client, Factory)).AccessToken;

        using var privateMe = await TenancyApi.Get(Client, OrgEndpoints.MePath, privateToken);
        using var memberMe = await TenancyApi.Get(Client, OrgEndpoints.MePath, memberToken);

        var mine = await TenancyApi.ReadOkAsync(privateMe);
        Assert.Equal(user.ToString(), mine.GetProperty("sub").GetString());
        Assert.Equal(space.ToString(), mine.GetProperty("org_id").GetString());
        Assert.Equal("private", mine.GetProperty("org_name").GetString());
        Assert.Equal("personal", mine.GetProperty("org_kind").GetString());
        Assert.Equal(["private"], mine.GetProperty("roles").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["reports:approve", "reports:read"], mine.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("organization", (await TenancyApi.ReadOkAsync(memberMe)).GetProperty("org_kind").GetString());
    }

    [Fact]
    public async Task A_change_of_the_private_permissions_reaches_the_next_refresh()   // criterion 13
    {
        await CreatePrivateAccountAsync(Private);
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var before = Holder.Current;

        Holder.Set(new Manifest(before.Permissions, before.DefaultRoles, before.Signup, before.PrivateSignup, ["templates:manage"]), null);
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        Assert.Equal(["templates:manage"], AccessTokens.Array(refreshed.AccessToken, "permissions"));
        Assert.Equal("personal", AccessTokens.Text(refreshed.AccessToken, "org_kind"));
        Assert.Equal(["private"], AccessTokens.Array(refreshed.AccessToken, "roles"));
    }

    [Fact]
    public async Task Without_the_block_a_private_account_still_signs_in_with_no_permissions()   // criterion 13
    {
        await CreatePrivateAccountAsync(Private);
        var before = Holder.Current;
        Holder.Set(new Manifest(before.Permissions, before.DefaultRoles, before.Signup), null);

        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);

        Assert.Empty(AccessTokens.Array(session.AccessToken, "permissions"));
        Assert.Equal(["private"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal("personal", AccessTokens.Text(session.AccessToken, "org_kind"));
    }

    [Fact]
    public async Task The_stored_list_of_the_private_role_row_is_ignored_and_its_star_is_never_held()   // a personal space's member never holds everything
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        await InDbAsync(async db =>
        {
            var role = await db.CompanyRoles.SingleAsync(r => r.CompanyId == space, TestContext.Current.CancellationToken);
            role.Permissions = ["*", "members:manage"];
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        var tenant = await TenantOfAsync(user);

        Assert.False(tenant.HoldsAll);
        Assert.Equal(CompanyKind.Personal, tenant.Kind);
        Assert.Equal(["reports:approve", "reports:read"], tenant.Permissions);
    }

    [Fact]
    public async Task A_stored_manifest_that_lists_the_star_or_company_permissions_for_the_private_role_does_not_grant_them()   // criterion 11
    {
        var (user, _) = await CreatePrivateAccountAsync(Private);
        var before = Holder.Current;
        var stored = Manifest.FromJson(
            new Manifest(
                before.Permissions, before.DefaultRoles, before.Signup, true,
                ["*", "members:manage", "roles:manage", "org:manage", "org:delete", "templates:manage"]).ToJson())!;
        Holder.Set(stored, null);

        var tenant = await TenantOfAsync(user);
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);

        Assert.Equal(["templates:manage"], tenant.Permissions);
        Assert.False(tenant.HoldsAll);
        Assert.Equal(["templates:manage"], AccessTokens.Array(session.AccessToken, "permissions"));
    }
}
