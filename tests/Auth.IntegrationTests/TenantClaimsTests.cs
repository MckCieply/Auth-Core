using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Auth.IntegrationTests;

public sealed class TenantClaimsTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string NoMembership = """{"error":"no_membership"}""";

    private static readonly string[] EveryPermission =
        ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"];

    private Task<SessionApi.Session> SeedLoginAsync() => SessionApi.LoginAsync(Client, Factory);

    [Fact]
    public async Task Login_token_names_the_company_the_role_and_every_permission_of_the_catalog()   // criterion 4
    {
        var session = await SeedLoginAsync();

        Assert.Equal((await DevCompanyIdAsync()).ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        // `*` expanded to the whole catalog: sorted ordinally, no duplicates, no star.
        Assert.Equal(EveryPermission, AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task Role_and_permission_lists_are_arrays_even_with_one_entry()
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "reader@example.com", "user");
        await SetRolePermissionsAsync(await RoleIdAsync(company, "user"), "reports:read");

        var session = await SessionApi.LoginAsync(Client, "reader@example.com", UserPassword);

        Assert.Equal(["user"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(["reports:read"], AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task A_role_holding_nothing_gives_an_empty_permission_array()
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "nobody@example.com", "user");
        await SetRolePermissionsAsync(await RoleIdAsync(company, "user"));

        var session = await SessionApi.LoginAsync(Client, "nobody@example.com", UserPassword);

        Assert.Empty(AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task Permissions_are_sorted_ordinally_without_duplicates_and_without_names_that_left_the_catalog()
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "mixed@example.com", "user");
        await SetRolePermissionsAsync(await RoleIdAsync(company, "user"), "reports:read", "members:manage", "reports:read", "old:thing", "reports:approve");

        var session = await SessionApi.LoginAsync(Client, "mixed@example.com", UserPassword);

        Assert.Equal(["members:manage", "reports:approve", "reports:read"], AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task Token_has_no_claim_beyond_the_contract_and_the_tenancy_claims_after_refreshes()
    {
        var session = await SeedLoginAsync();
        for (var i = 0; i < 3; i++)
        {
            session = await SessionApi.RefreshOk(Client, session.RefreshToken);
        }

        // Three generations on, nothing is doubled: one company, one role, each permission once.
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(EveryPermission, AccessTokens.Array(session.AccessToken, "permissions"));
        Assert.Equal(
            ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "permissions", "roles", "sub"],
            AccessTokens.Payload(session.AccessToken).Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_refresh_token_never_carries_the_company_the_role_or_the_permissions()   // spec 0005 → Access token
    {
        var session = await SeedLoginAsync();
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        foreach (var reference in new[] { session.RefreshToken, refreshed.RefreshToken })   // made by a login and by a refresh
        {
            var claims = await StoredRefreshTokenClaimsAsync(reference);

            Assert.Equal((await Factory.SeedUserIdAsync()).ToString(), claims["sub"]!.GetValue<string>());   // the token was read
            Assert.All(TenantClaims.Types, type => Assert.False(claims.ContainsKey(type), $"The refresh token carries '{type}'."));
        }
    }

    [Fact]
    public async Task The_claim_filter_takes_the_three_claims_out_of_a_refresh_token_and_leaves_them_in_an_access_token()
    {
        static OpenIddictServerEvents.GenerateTokenContext ContextFor(string tokenType)
        {
            var context = new OpenIddictServerEvents.GenerateTokenContext(new OpenIddictServerTransaction()) { TokenType = tokenType };
            context.SecurityTokenDescriptor.Subject = new ClaimsIdentity(
            [
                new Claim(OpenIddictConstants.Claims.Subject, "someone"),
                new Claim(TenantClaims.OrgId, Guid.NewGuid().ToString()),
                new Claim(TenantClaims.Roles, """["admin"]"""),
                new Claim(TenantClaims.Permissions, """["reports:read"]"""),
            ]);
            return context;
        }

        var refresh = ContextFor(OpenIddictConstants.TokenTypeIdentifiers.RefreshToken);
        var access = ContextFor(OpenIddictConstants.TokenTypeIdentifiers.AccessToken);

        await new AccessTokenClaimFilter().HandleAsync(refresh);
        await new AccessTokenClaimFilter().HandleAsync(access);

        Assert.Equal([OpenIddictConstants.Claims.Subject], refresh.SecurityTokenDescriptor.Subject.Claims.Select(c => c.Type));
        Assert.Equal([OpenIddictConstants.Claims.Subject, .. TenantClaims.Types], access.SecurityTokenDescriptor.Subject.Claims.Select(c => c.Type));
    }

    /// <summary>
    /// The claims of a refresh token as the service keeps them: the cookie holds only a reference, and its entry holds the
    /// token itself, encrypted to the instance's key.
    /// </summary>
    private async Task<JsonObject> StoredRefreshTokenClaimsAsync(string reference)
    {
        using var scope = Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var entry = await tokens.FindByReferenceIdAsync(reference, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The refresh token is not stored.");
        var payload = await tokens.GetPayloadAsync(entry, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The refresh token entry has no payload.");

        var server = Factory.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;
        var handler = new JsonWebTokenHandler();
        var signed = handler.DecryptToken(
            handler.ReadJsonWebToken(payload),
            new TokenValidationParameters { TokenDecryptionKeys = [.. server.EncryptionCredentials.Select(c => c.Key)] });
        return AccessTokens.Payload(signed);
    }

    [Fact]
    public async Task Account_without_a_company_is_refused_with_no_token_and_no_cookie()   // criterion 11
    {
        await CreateUserAsync("lonely@example.com", confirmed: true, member: false);

        using var response = await LoginApi.Login(Client, "lonely@example.com", UserPassword);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"Expected 403, got {(int)response.StatusCode}: {raw}");
        Assert.Equal(NoMembership, raw);
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }

    [Fact]
    public async Task Wrong_password_for_an_account_without_a_company_is_the_ordinary_401()   // criterion 11
    {
        await CreateUserAsync("lonely@example.com", confirmed: true, member: false);

        using var lonely = await LoginApi.Login(Client, "lonely@example.com", LockoutApi.WrongPassword);
        using var member = await LoginApi.Login(Client, Factory.SeedEmail, LockoutApi.WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, lonely.StatusCode);
        Assert.Equal(await member.Content.ReadAsStringAsync(), await lonely.Content.ReadAsStringAsync());
        Assert.Equal(LoginApi.HeaderNames(member), LoginApi.HeaderNames(lonely));
    }

    [Fact]
    public async Task The_refusal_ends_the_streak_as_a_success_would()   // criterion 11
    {
        await CreateUserAsync("lonely@example.com", confirmed: true, member: false);
        await LockoutApi.FailAsync(Client, Clock, "lonely@example.com", 9);

        Clock.Advance(LockoutApi.HumanPace);
        using (var refused = await LoginApi.Login(Client, "lonely@example.com", UserPassword))   // the tenth attempt of the streak
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        await LockoutApi.FailAsync(Client, Clock, "lonely@example.com", 9);   // nine more ordinary 401s: no lock
    }

    [Fact]
    public async Task The_order_is_password_then_confirmed_email_then_membership()   // spec 0005 → POST /auth/login
    {
        await CreateUserAsync("both@example.com", confirmed: false, member: false);

        using var response = await LoginApi.Login(Client, "both@example.com", UserPassword);

        Assert.Equal("""{"error":"email_not_verified"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Next_refresh_carries_a_changed_role_and_ends_no_session()   // criterion 12
    {
        var company = await DevCompanyIdAsync();
        var worker = await AddMemberAsync(company, "worker@example.com", "admin");
        var session = await SessionApi.LoginAsync(Client, "worker@example.com", UserPassword);
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "user"));
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        Assert.Equal(["user"], AccessTokens.Array(refreshed.AccessToken, "roles"));
        Assert.Equal(["reports:approve", "reports:read"], AccessTokens.Array(refreshed.AccessToken, "permissions"));
        _ = await SessionApi.RefreshOk(Client, refreshed.RefreshToken);   // the session goes on
    }

    [Fact]
    public async Task Next_refresh_carries_an_edited_role_and_ends_no_session()   // criterion 12
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "worker@example.com", "user");
        var session = await SessionApi.LoginAsync(Client, "worker@example.com", UserPassword);

        await SetRolePermissionsAsync(await RoleIdAsync(company, "user"), "templates:manage");
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        Assert.Equal(["templates:manage"], AccessTokens.Array(refreshed.AccessToken, "permissions"));
        _ = await SessionApi.RefreshOk(Client, refreshed.RefreshToken);
    }

    [Fact]
    public async Task Refresh_of_someone_who_is_no_longer_a_member_is_the_401_of_spec_0002()   // criterion 13
    {
        var company = await DevCompanyIdAsync();
        var leaver = await AddMemberAsync(company, "leaver@example.com", "user");
        var session = await SessionApi.LoginAsync(Client, "leaver@example.com", UserPassword);

        await RemoveMembershipAsync(leaver);

        using var response = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(response);
    }

    [Fact]
    public async Task A_permission_that_leaves_the_catalog_is_gone_from_the_next_refresh_and_a_new_one_reaches_star()   // criterion 21
    {
        var session = await SeedLoginAsync();   // admin: *
        Assert.Contains("templates:manage", AccessTokens.Array(session.AccessToken, "permissions"));

        Holder.Set(ManifestParser.Parse("permissions: [reports:read, orders:read]\ndefault_roles:\n  admin: [\"*\"]\n").Manifest!, null);
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        var permissions = AccessTokens.Array(refreshed.AccessToken, "permissions");
        Assert.DoesNotContain("templates:manage", permissions);
        Assert.DoesNotContain("reports:approve", permissions);
        Assert.Contains("orders:read", permissions);
        Assert.Equal(["members:manage", "orders:read", "org:delete", "org:manage", "reports:read", "roles:manage"], permissions);
    }
}
