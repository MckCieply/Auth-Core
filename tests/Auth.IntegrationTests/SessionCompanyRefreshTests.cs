using System.Globalization;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0015 → Refresh: a refresh keeps the company of the session it belongs to (never the sign-in's pick of the moment), reads the membership
/// there and nowhere else, writes no <c>LastUsedAt</c>, and gives a token of the code before this version the sign-in's space.
/// </summary>
public sealed class SessionCompanyRefreshTests : TenancyTestBase
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] TenancyClaims = ["org_id", "org_kind", "roles", "permissions"];

    private readonly OldRefreshTokens _oldTokens;

    public SessionCompanyRefreshTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        _oldTokens = OldRefreshTokens.Install(Factory);
    }

    /// <summary>The whole Unix microseconds of an instant, worked out here and not by the code under test.</summary>
    private static string MicrosOf(DateTimeOffset instant) =>
        ((instant.UtcDateTime - UnixEpoch).Ticks / 10).ToString(CultureInfo.InvariantCulture);

    private DateTimeOffset Now => StorableTime.Now(Clock);

    private Task<DateTimeOffset> JoinedAtAsync(Guid userId, Guid companyId) =>
        InDbAsync(db => db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.CompanyId == companyId)
            .Select(m => m.JoinedAt)
            .SingleAsync(TestContext.Current.CancellationToken));

    /// <summary>Sets when a membership was last used, as a switch or an earlier sign-in would have left it.</summary>
    private Task<int> SetLastUsedAtAsync(Guid userId, Guid companyId, DateTimeOffset? at) =>
        InDbAsync(db => db.Memberships.Where(m => m.UserId == userId && m.CompanyId == companyId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastUsedAt, at), TestContext.Current.CancellationToken));

    /// <summary>The seed user, in the development company (A) and in a second one (B) that a sign-in does not choose first (joined an hour later).</summary>
    private async Task<(Guid User, Guid A, Guid B)> SeedInTwoCompaniesAsync()
    {
        var user = await Factory.SeedUserIdAsync();
        var a = await DevCompanyIdAsync();
        var b = await CreateCompanyAsync("Second");
        await AddMembershipAsync(user, b, "user");
        return (user, a, b);
    }

    private static string OrgOf(SessionApi.Session session) => AccessTokens.Text(session.AccessToken, "org_id");

    [Fact]
    public async Task A_refresh_keeps_the_session_company_when_another_membership_was_used_later()   // criterion 6
    {
        var (user, a, b) = await SeedInTwoCompaniesAsync();
        var session = await SessionApi.LoginAsync(Client, Factory);
        Assert.Equal(a.ToString(), OrgOf(session));

        await SetLastUsedAtAsync(user, b, Now.AddHours(1));
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        Assert.Equal(a.ToString(), OrgOf(refreshed));
    }

    [Fact]
    public async Task A_refresh_does_not_write_last_used_at()
    {
        var (user, a, b) = await SeedInTwoCompaniesAsync();
        var session = await SessionApi.LoginAsync(Client, Factory);
        var before = await LastUsedAtAsync(user, a);
        Assert.NotNull(before);

        Clock.Advance(TimeSpan.FromMinutes(5));
        await SessionApi.RefreshOk(Client, session.RefreshToken);

        Assert.Equal(before, await LastUsedAtAsync(user, a));
        Assert.Null(await LastUsedAtAsync(user, b));
    }

    [Fact]
    public async Task A_refresh_after_the_session_company_was_removed_is_invalid_grant_and_a_session_in_the_other_company_refreshes()   // criterion 8
    {
        var (user, a, b) = await SeedInTwoCompaniesAsync();
        var inA = await SessionApi.LoginAsync(Client, Factory);
        Assert.Equal(a.ToString(), OrgOf(inA));
        await SetLastUsedAtAsync(user, b, Now.AddHours(1));   // the next sign-in lands in B
        var inB = await SessionApi.LoginAsync(Client, Factory);
        Assert.Equal(b.ToString(), OrgOf(inB));

        await RemoveMembershipAsync(user, a);

        using var refusedInA = await SessionApi.Refresh(Client, inA.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refusedInA);
        var refreshedInB = await SessionApi.RefreshOk(Client, inB.RefreshToken);
        Assert.Equal(b.ToString(), OrgOf(refreshedInB));
    }

    [Fact]
    public async Task A_member_removed_and_added_again_does_not_get_the_old_session_back()   // criterion 9
    {
        var user = await Factory.SeedUserIdAsync();
        var a = await DevCompanyIdAsync();
        var session = await SessionApi.LoginAsync(Client, Factory);
        Assert.Equal(a.ToString(), OrgOf(session));

        await RemoveMembershipAsync(user, a);
        await AddMembershipAsync(user, a, "admin", Now.AddHours(2));

        using var refused = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refused);
        var again = await SessionApi.LoginAsync(Client, Factory);
        Assert.Equal(a.ToString(), OrgOf(again));
    }

    [Fact]
    public async Task A_token_without_the_session_company_refreshes_into_the_sign_in_space_and_the_next_token_carries_it()   // criterion 10
    {
        var (user, a, b) = await SeedInTwoCompaniesAsync();
        var old = await _oldTokens.LoginAsync(Client, Factory.SeedEmail, Factory.SeedPassword);
        Assert.Equal(a.ToString(), OrgOf(old));
        var stored = await StoredTokens.RefreshClaimsAsync(Factory, old.RefreshToken);
        Assert.False(stored.ContainsKey("session_org_id"), "The minted token must be one of the code before spec 0015.");
        Assert.False(stored.ContainsKey("session_org_joined"), "The minted token must be one of the code before spec 0015.");

        await SetLastUsedAtAsync(user, b, Now.AddHours(1));   // the sign-in order now says B
        var refreshed = await SessionApi.RefreshOk(Client, old.RefreshToken);

        Assert.Equal(b.ToString(), OrgOf(refreshed));
        var next = await StoredTokens.RefreshClaimsAsync(Factory, refreshed.RefreshToken);
        Assert.Equal(b.ToString(), next["session_org_id"]!.GetValue<string>());
        Assert.Equal(MicrosOf(await JoinedAtAsync(user, b)), next["session_org_joined"]!.GetValue<string>());

        var second = await SessionApi.RefreshOk(Client, refreshed.RefreshToken);
        Assert.Equal(b.ToString(), OrgOf(second));
    }

    [Fact]
    public async Task A_token_without_the_session_company_of_an_account_with_no_membership_is_invalid_grant()
    {
        var user = await Factory.SeedUserIdAsync();
        var old = await _oldTokens.LoginAsync(Client, Factory.SeedEmail, Factory.SeedPassword);
        await RemoveMembershipAsync(user);

        using var refused = await SessionApi.Refresh(Client, old.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(refused);
    }

    [Theory]
    [InlineData("session_org_id")]
    [InlineData("session_org_joined")]
    public async Task A_token_with_only_one_of_the_two_session_claims_is_invalid_grant(string kept)
    {
        var removed = kept == "session_org_id" ? "session_org_joined" : "session_org_id";
        await Factory.SeedUserIdAsync();
        var half = await _oldTokens.LoginWithoutAsync(Client, Factory.SeedEmail, Factory.SeedPassword, removed);
        var stored = await StoredTokens.RefreshClaimsAsync(Factory, half.RefreshToken);
        Assert.True(stored.ContainsKey(kept), "The minted token must keep one claim.");
        Assert.False(stored.ContainsKey(removed), "The minted token must lack the other claim.");

        using var refused = await SessionApi.Refresh(Client, half.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(refused);
    }

    [Fact]
    public async Task The_next_refresh_token_carries_the_company_and_join_time()   // criterion 11, refresh side
    {
        var (user, a, _) = await SeedInTwoCompaniesAsync();
        var session = await SessionApi.LoginAsync(Client, Factory);

        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        var stored = await StoredTokens.RefreshClaimsAsync(Factory, refreshed.RefreshToken);
        Assert.Equal(a.ToString(), stored["session_org_id"]!.GetValue<string>());
        Assert.Equal(MicrosOf(await JoinedAtAsync(user, a)), stored["session_org_joined"]!.GetValue<string>());
        Assert.All(TenancyClaims, type => Assert.False(stored.ContainsKey(type), $"The refresh token carries '{type}'."));

        var access = AccessTokens.Payload(refreshed.AccessToken);
        Assert.False(access.ContainsKey("session_org_id"));
        Assert.False(access.ContainsKey("session_org_joined"));
        Assert.Equal(AccessTokens.ContractClaims, access.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_membership_written_with_a_time_finer_than_a_microsecond_still_refreshes()   // Review Focus 4
    {
        var company = await CreateCompanyAsync("Fine");
        var account = await CreateUserAsync("fine@acme.test", confirmed: true, member: false);
        var joined = new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero).AddTicks(1234567);   // ...:00.1234567, 100 ns ticks
        await AddMembershipAsync(account.Id, company, "user", joined);
        var session = await SessionApi.LoginAsync(Client, "fine@acme.test", UserPassword);
        Assert.Equal(company.ToString(), OrgOf(session));

        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        Assert.Equal(company.ToString(), OrgOf(refreshed));
        var stored = await StoredTokens.RefreshClaimsAsync(Factory, refreshed.RefreshToken);
        Assert.Equal(MicrosOf(await JoinedAtAsync(account.Id, company)), stored["session_org_joined"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_refresh_waits_for_a_removal_holding_its_membership_and_then_is_invalid_grant()
    {
        var user = await Factory.SeedUserIdAsync();
        var a = await DevCompanyIdAsync();
        var session = await SessionApi.LoginAsync(Client, Factory);
        Assert.Equal("valid", await StoredTokens.StatusAsync(Factory, session.RefreshToken));

        await using var held = await HeldLock.MembershipRowAsync(Factory, user, a);
        var pending = SessionApi.Refresh(Client, session.RefreshToken);
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await held.DeleteMembershipAsync(user, a);
        await held.ReleaseAsync();

        using var response = await pending;
        await SessionApi.AssertInvalidGrantAsync(response);
        Assert.Equal("valid", await StoredTokens.StatusAsync(Factory, session.RefreshToken));
    }
}
