using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class RefreshOutageTests : SessionTestBase
{
    private readonly DatabaseOutage _outage;

    public RefreshOutageTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        _outage = new DatabaseOutage(postgres.ConnectionStringFor(Factory.DatabaseName));
        Factory.WithSetting("ConnectionStrings:Auth", _outage.ConnectionString);
    }

    public override async ValueTask DisposeAsync()
    {
        await _outage.DisposeAsync();
        await base.DisposeAsync();
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"Expected 503, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("""{"error":"temporarily_unavailable"}""", raw);
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        SecurityHeadersApi.AssertSecurityHeaders(response);
        Assert.False(response.Headers.Contains("Set-Cookie"), "The cookie is neither cleared nor rotated.");
    }

    private static async Task AssertInternalErrorAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.InternalServerError, $"Expected 500, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("""{"error":"internal_error"}""", raw);
        SecurityHeadersApi.AssertSecurityHeaders(response);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task A_refresh_while_the_database_is_down_is_a_503_and_the_same_cookie_refreshes_when_it_is_back()   // criterion 3
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        _outage.Cut();
        using (var during = await SessionApi.Refresh(Client, login.RefreshToken))
        {
            await AssertUnavailableAsync(during);
        }

        using (var stillDown = await SessionApi.Refresh(Client, login.RefreshToken))
        {
            await AssertUnavailableAsync(stillDown);
        }

        _outage.Start();
        var after = await SessionApi.RefreshOk(Client, login.RefreshToken);   // the same cookie: it was neither used up nor revoked
        await SessionApi.RefreshOk(Client, after.RefreshToken);               // and the chain goes on
    }

    [Fact]
    public async Task A_cookie_the_service_cannot_look_up_is_a_503_not_a_401_so_that_the_person_stays_signed_in()   // Decision 4
    {
        _ = await SessionApi.LoginAsync(Client, Factory);

        _outage.Cut();
        using var response = await SessionApi.Refresh(Client, "a-cookie-nobody-could-look-up");

        await AssertUnavailableAsync(response);
    }

    [Fact]
    public async Task No_cookie_is_still_a_401_as_before_for_no_database_is_needed_to_see_it()
    {
        _ = await SessionApi.LoginAsync(Client, Factory);

        _outage.Cut();
        using var response = await SessionApi.Refresh(Client, null);

        await SessionApi.AssertInvalidGrantAsync(response);
    }

    [Fact]
    public async Task Login_logout_and_the_company_api_keep_a_500_for_an_outage_now_written_by_the_handler()
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        _outage.Cut();
        using var signIn = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);
        using var signOut = await SessionApi.Logout(Client, login.RefreshToken);
        using var company = await TenancyApi.Get(Client, "/auth/me", login.AccessToken);

        await AssertInternalErrorAsync(signIn);
        await AssertInternalErrorAsync(signOut);
        await AssertInternalErrorAsync(company);
    }

    [Fact]
    public async Task The_health_check_does_not_reach_the_database()
    {
        _outage.Cut();

        using var response = await Client.GetAsync("/auth/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
