using System.Net;
using System.Text.Json;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0015 → <c>POST /auth/switch</c>, part 2: the target, the <c>404</c> that uses nothing up, <c>LastUsedAt</c>, the <c>org.switched</c> row, and what
/// the session does afterwards. The seed user is an admin of company A and a plain member of company B (joined an hour later), with a login in A.
/// </summary>
public sealed class SwitchTests : AuditTestBase
{
    private const string ClientAddress = "203.0.113.7";

    private readonly OldRefreshTokens _oldTokens;

    public SwitchTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.PrivateBlock);
        _oldTokens = OldRefreshTokens.Install(Factory);
    }

    private DateTimeOffset Now => StorableTime.Now(Clock);

    private async Task<(Guid User, Guid A, Guid B)> SeedInTwoCompaniesAsync()
    {
        var user = await Factory.SeedUserIdAsync();
        var a = await DevCompanyIdAsync();
        var b = await CreateCompanyAsync("Second");
        await AddMembershipAsync(user, b, "user");
        return (user, a, b);
    }

    private static string OrgOf(SessionApi.Session session) => AccessTokens.Text(session.AccessToken, "org_id");

    /// <summary>Everything a client can see of an answer but the time it was made: status, the other headers and the body.</summary>
    private static async Task<string> SeenAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => !string.Equals(h.Key, "Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}: {string.Join(",", h.Value)}");
        return $"{(int)response.StatusCode}\n{string.Join('\n', headers)}\n\n{await response.Content.ReadAsStringAsync()}";
    }

    [Fact]
    public async Task A_switch_to_another_company_answers_as_a_refresh_for_that_company()   // criterion 15
    {
        var (user, a, b) = await SeedInTwoCompaniesAsync();
        var viaRefresh = await SessionApi.LoginAsync(Client, Factory);
        var viaSwitch = await SessionApi.LoginAsync(Client, Factory);
        Assert.Equal(a.ToString(), OrgOf(viaSwitch));
        Clock.Advance(TimeSpan.FromMinutes(5));
        var switchedAt = Now;

        using var refreshed = await SessionApi.Refresh(Client, viaRefresh.RefreshToken);
        using var switched = await SessionApi.SwitchTo(Client, viaSwitch.RefreshToken, b, ClientAddress);

        var raw = await switched.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        using var body = JsonDocument.Parse(raw);
        Assert.Equal(["access_token"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(LoginApi.HeaderNames(refreshed), LoginApi.HeaderNames(switched));
        var cookie = SessionApi.RefreshCookieOf(switched);
        var expected = SessionApi.RefreshCookieOf(refreshed);
        Assert.Equal(expected.HttpOnly, cookie.HttpOnly);
        Assert.Equal(expected.Secure, cookie.Secure);
        Assert.Equal(expected.SameSite, cookie.SameSite);
        Assert.Equal(expected.Path, cookie.Path);
        Assert.Equal(expected.MaxAge, cookie.MaxAge);
        Assert.NotEqual(viaSwitch.RefreshToken, cookie.Value.Value);
        Assert.DoesNotContain(cookie.Value.Value!, raw, StringComparison.Ordinal);

        var token = body.RootElement.GetProperty("access_token").GetString()!;
        Assert.Equal(b.ToString(), AccessTokens.Text(token, "org_id"));
        Assert.Equal("organization", AccessTokens.Text(token, "org_kind"));
        Assert.Equal(["user"], AccessTokens.Array(token, "roles"));
        Assert.Equal(["reports:approve", "reports:read"], AccessTokens.Array(token, "permissions"));
        Assert.Equal(switchedAt, await LastUsedAtAsync(user, b));
        Assert.Equal("redeemed", await StoredTokens.StatusAsync(Factory, viaSwitch.RefreshToken));

        var row = await SingleAsync(AuditKinds.OrgSwitched);
        Assert.Equal(user, row.ActorUserId);
        Assert.Equal(user, row.SubjectUserId);
        Assert.Equal(Factory.SeedEmail, row.SubjectEmail);
        Assert.Equal(b, row.OrgId);
        Assert.Equal("Second", row.OrgName);
        Assert.Equal(a.ToString(), AuditApi.Text(row, "from_org_id"));
        Assert.Equal(["from_org_id"], AuditApi.DetailNames(row));
        Assert.Equal(ClientAddress, row.ClientIp);
    }

    [Fact]
    public async Task The_next_refresh_stays_in_the_company_switched_to_and_a_new_login_lands_there()   // criteria 4, 6
    {
        var (_, a, b) = await SeedInTwoCompaniesAsync();
        var login = await SessionApi.LoginAsync(Client, Factory);
        Assert.Equal(a.ToString(), OrgOf(login));
        Clock.Advance(TimeSpan.FromMinutes(5));

        var switched = await SessionApi.SwitchOk(Client, login.RefreshToken, b);
        var refreshed = await SessionApi.RefreshOk(Client, switched.RefreshToken);
        Clock.Advance(TimeSpan.FromMinutes(5));
        var again = await SessionApi.RefreshOk(Client, refreshed.RefreshToken);
        var secondClient = await SessionApi.LoginAsync(Client, Factory);

        Assert.Equal(b.ToString(), OrgOf(switched));
        Assert.Equal(b.ToString(), OrgOf(refreshed));
        Assert.Equal(b.ToString(), OrgOf(again));
        Assert.Equal(b.ToString(), OrgOf(secondClient));
    }

    [Fact]
    public async Task A_switch_on_one_device_leaves_the_other_device_in_its_company()   // criterion 7
    {
        var (_, a, b) = await SeedInTwoCompaniesAsync();
        var device1 = await SessionApi.LoginAsync(Client, Factory);
        var device2 = await SessionApi.LoginAsync(Client, Factory);
        Clock.Advance(TimeSpan.FromMinutes(5));

        var switched = await SessionApi.SwitchOk(Client, device1.RefreshToken, b);
        var other = await SessionApi.RefreshOk(Client, device2.RefreshToken);
        var first = await SessionApi.RefreshOk(Client, switched.RefreshToken);

        Assert.Equal(a.ToString(), OrgOf(other));
        Assert.Equal(b.ToString(), OrgOf(first));
    }

    [Fact]
    public async Task The_old_cookie_after_a_switch_is_a_consumed_refresh_token()   // criterion 16
    {
        var (_, _, b) = await SeedInTwoCompaniesAsync();
        var login = await SessionApi.LoginAsync(Client, Factory);
        var switched = await SessionApi.SwitchOk(Client, login.RefreshToken, b);

        await SessionApi.RefreshOk(Client, login.RefreshToken);   // inside the leeway, an honest retry
        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        using var afterReplay = await SessionApi.Refresh(Client, switched.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(replay);
        await SessionApi.AssertInvalidGrantAsync(afterReplay);   // the family is revoked
        Assert.Single(await AuditAsync(AuditKinds.RefreshReuseDetected));
    }

    [Fact]
    public async Task The_old_cookie_within_the_grace_window_gets_a_pair_for_the_old_company()   // Review Focus 1
    {
        var (_, a, b) = await SeedInTwoCompaniesAsync();
        var login = await SessionApi.LoginAsync(Client, Factory);
        await SessionApi.SwitchOk(Client, login.RefreshToken, b);

        var graced = await SessionApi.RefreshOk(Client, login.RefreshToken);

        Assert.Equal(a.ToString(), OrgOf(graced));
    }

    [Fact]
    public async Task A_company_of_others_an_unknown_id_and_another_accounts_personal_space_are_the_same_404()   // criterion 17
    {
        var (_, a, _) = await SeedInTwoCompaniesAsync();
        var others = await CreateCompanyAsync("Others");
        var (_, spaceOfAnother) = await CreatePrivateAccountAsync("ola@poczta.test");
        var login = await SessionApi.LoginAsync(Client, Factory);

        using var ofOthers = await SessionApi.SwitchTo(Client, login.RefreshToken, others);
        using var unknown = await SessionApi.SwitchTo(Client, login.RefreshToken, Guid.NewGuid());
        using var personal = await SessionApi.SwitchTo(Client, login.RefreshToken, spaceOfAnother);

        var expected = await SeenAsync(ofOthers);
        Assert.StartsWith("404\n", expected, StringComparison.Ordinal);
        Assert.EndsWith("""{"error":"not_found"}""", expected, StringComparison.Ordinal);
        Assert.False(ofOthers.Headers.Contains("Set-Cookie"));
        Assert.Equal(expected, await SeenAsync(unknown));
        Assert.Equal(expected, await SeenAsync(personal));
        Assert.Equal("valid", await StoredTokens.StatusAsync(Factory, login.RefreshToken));   // none of the three used the token up
        Assert.Equal(a.ToString(), OrgOf(await SessionApi.RefreshOk(Client, login.RefreshToken)));
        Assert.Empty(await AuditAsync(AuditKinds.OrgSwitched));
    }

    [Fact]
    public async Task A_switch_to_the_session_company_is_a_refresh_that_writes_last_used_at_and_no_row()   // criterion 20
    {
        var (user, a, b) = await SeedInTwoCompaniesAsync();
        var login = await SessionApi.LoginAsync(Client, Factory);
        Clock.Advance(TimeSpan.FromMinutes(5));
        var switchedAt = Now;

        var same = await SessionApi.SwitchOk(Client, login.RefreshToken, a);

        Assert.Equal(a.ToString(), OrgOf(same));
        Assert.Equal(switchedAt, await LastUsedAtAsync(user, a));
        Assert.Null(await LastUsedAtAsync(user, b));
        Assert.Empty(await AuditAsync(AuditKinds.OrgSwitched));
        Assert.Equal("redeemed", await StoredTokens.StatusAsync(Factory, login.RefreshToken));
        await SessionApi.RefreshOk(Client, same.RefreshToken);
    }

    [Fact]
    public async Task A_switch_to_the_personal_space_gives_its_claims_and_the_company_api_refuses()   // criterion 21
    {
        var user = await Factory.SeedUserIdAsync();
        var space = await InDbAsync(async db =>
        {
            var id = PersonalSpaces.Add(db, user, Now.AddHours(1));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return id;
        });
        var login = await SessionApi.LoginAsync(Client, Factory);

        var switched = await SessionApi.SwitchOk(Client, login.RefreshToken, space);

        Assert.Equal(space.ToString(), OrgOf(switched));
        Assert.Equal("personal", AccessTokens.Text(switched.AccessToken, "org_kind"));
        Assert.Equal(["private"], AccessTokens.Array(switched.AccessToken, "roles"));
        Assert.Equal(["reports:approve", "reports:read"], AccessTokens.Array(switched.AccessToken, "permissions"));
        using var company = await TenancyApi.Get(Client, "/auth/org", switched.AccessToken);
        Assert.Equal("""{"error":"forbidden"}""", await TenancyApi.ReadAsync(company, HttpStatusCode.Forbidden));
    }

    [Fact]
    public async Task An_org_id_in_upper_case_switches()   // Review Focus 2
    {
        var (_, _, b) = await SeedInTwoCompaniesAsync();
        var login = await SessionApi.LoginAsync(Client, Factory);

        using var response = await SessionApi.Switch(Client, login.RefreshToken, $$"""{"org_id":"{{b.ToString("D").ToUpperInvariant()}}"}""");

        Assert.Equal(b.ToString(), OrgOf(await SessionApi.ReadSessionAsync(response)));
    }

    [Fact]
    public async Task A_token_without_the_session_company_switches_and_names_the_sign_in_space_as_from()   // spec: old tokens
    {
        var (_, a, b) = await SeedInTwoCompaniesAsync();
        var old = await _oldTokens.LoginAsync(Client, Factory.SeedEmail, Factory.SeedPassword);
        var before = await StoredTokens.RefreshClaimsAsync(Factory, old.RefreshToken);
        Assert.Null(before[SessionPolicy.CompanyClaim]);

        var switched = await SessionApi.SwitchOk(Client, old.RefreshToken, b);

        Assert.Equal(b.ToString(), OrgOf(switched));
        Assert.Equal(a.ToString(), AuditApi.Text(await SingleAsync(AuditKinds.OrgSwitched), "from_org_id"));
        Assert.Equal(b.ToString(), (string?)(await StoredTokens.RefreshClaimsAsync(Factory, switched.RefreshToken))[SessionPolicy.CompanyClaim]);
    }

    [Fact]
    public async Task A_switch_with_a_changed_role_reads_the_role_now()
    {
        var (user, _, b) = await SeedInTwoCompaniesAsync();
        var login = await SessionApi.LoginAsync(Client, Factory);
        await SetMemberRoleAsync(user, b, "admin");

        var switched = await SessionApi.SwitchOk(Client, login.RefreshToken, b);

        Assert.Equal(["admin"], AccessTokens.Array(switched.AccessToken, "roles"));
        var permissions = AccessTokens.Array(switched.AccessToken, "permissions");   // the admin's "*", expanded: what a plain member of B never holds
        Assert.Contains("templates:manage", permissions);
        Assert.DoesNotContain("*", permissions);
    }
}
