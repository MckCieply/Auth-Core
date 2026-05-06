using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Testing;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → Testing endpoint (criterion 24): Development only, off by default, it ends the window of this device's session.</summary>
public sealed class TestingEndpointTests : TenancyTestBase
{
    private const string Ola = "ola@firma.test";

    public TestingEndpointTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The host of this class has the endpoint; a test that needs it off starts a host of its own.
        Factory.WithSetting(TestingSettings.WindowEndpointKey, "true");
    }

    [Fact]
    public async Task With_the_setting_on_it_ends_the_window_of_this_devices_session_and_no_other()   // criterion 24, Settled 22
    {
        await CreateUserAsync(Ola, confirmed: true);
        var here = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);

        using var ended = await SettingsApi.EndConfirmation(Client, here);

        await TenancyApi.AssertEmptyAsync(ended, HttpStatusCode.NoContent);
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, here));
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, there));
        var refreshed = await SessionApi.RefreshOk(Client, here.RefreshToken);   // the session itself goes on
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, refreshed));
    }

    [Fact]
    public async Task Without_the_cookie_it_is_401_session_required_and_without_a_token_the_plain_401()   // spec 0012 → General rules
    {
        await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);

        using var noCookie = await SettingsApi.PostAs(Client, SettingsApi.EndConfirmationPath, session.AccessToken, null, null);
        using var noToken = await SettingsApi.PostAs(Client, SettingsApi.EndConfirmationPath, null, session.RefreshToken, null);

        Assert.Equal(HttpStatusCode.Unauthorized, noCookie.StatusCode);
        Assert.Equal("""{"error":"session_required"}""", await noCookie.Content.ReadAsStringAsync());
        await TenancyApi.AssertUnauthorizedAsync(noToken);
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, session));
    }

    [Fact]
    public async Task With_the_setting_off_it_is_404()   // criterion 24
    {
        await using var off = new AuthAppFactory(Postgres, Keys);
        using var client = SessionApi.CreateClient(off);
        var session = await SessionApi.LoginAsync(client, off);

        using var response = await SettingsApi.EndConfirmation(client, session);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(client, session));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task The_setting_outside_development_stops_the_start_naming_the_key(string value)   // criterion 24
    {
        await using var production = new AuthAppFactory(Postgres, Keys)
            .WithEnvironment("Production")
            .WithSetting(TestingSettings.WindowEndpointKey, value);

        var ex = Assert.ThrowsAny<Exception>(() => production.CreateClient());

        Assert.Contains(TestingSettings.WindowEndpointKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_that_is_not_true_or_false_stops_the_start_naming_the_key()
    {
        await using var development = new AuthAppFactory(Postgres, Keys).WithSetting(TestingSettings.WindowEndpointKey, "yes");

        var ex = Assert.ThrowsAny<Exception>(() => development.CreateClient());

        Assert.Contains(TestingSettings.WindowEndpointKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_is_that_of_the_contract() => Assert.Equal("Auth:Testing:WindowEndpoint", TestingSettings.WindowEndpointKey);

    [Fact]
    public async Task Another_accounts_cookie_and_a_revoked_one_are_401_session_required_and_end_no_window()
    {
        await CreateUserAsync(Ola, confirmed: true);
        await CreateUserAsync("other@firma.test", confirmed: true);
        var mine = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var theirs = await SessionApi.LoginAsync(Client, "other@firma.test", UserPassword);

        using (var foreign = await SettingsApi.PostAs(Client, SettingsApi.EndConfirmationPath, mine.AccessToken, theirs.RefreshToken, null))
        using (var garbage = await SettingsApi.PostAs(Client, SettingsApi.EndConfirmationPath, mine.AccessToken, "not-a-token-at-all", null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, foreign.StatusCode);
            Assert.Equal("""{"error":"session_required"}""", await foreign.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
            Assert.Equal("""{"error":"session_required"}""", await garbage.Content.ReadAsStringAsync());
        }

        using (var logout = await SessionApi.Logout(Client, mine.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        using (var revoked = await SettingsApi.EndConfirmation(Client, mine))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            Assert.Equal("""{"error":"session_required"}""", await revoked.Content.ReadAsStringAsync());
        }

        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, theirs));   // nobody's window was ended
    }

    [Fact]
    public async Task A_session_from_before_this_version_has_no_window_to_end_and_the_endpoint_writes_nothing()
    {
        await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        await InDbAsync(db => db.SessionStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        using var ended = await SettingsApi.EndConfirmation(Client, session);

        await TenancyApi.AssertEmptyAsync(ended, HttpStatusCode.NoContent);
        Assert.Equal(0, await InDbAsync(db => db.SessionStates.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task The_endpoint_leaves_the_start_of_the_session_and_every_other_value_alone()
    {
        await CreateUserAsync(Ola, confirmed: true);
        var here = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var before = await InDbAsync(db => db.SessionStates.AsNoTracking().OrderBy(s => s.AuthorizationId).ToListAsync(TestContext.Current.CancellationToken));

        using (await SettingsApi.EndConfirmation(Client, here))
        {
        }

        var after = await InDbAsync(db => db.SessionStates.AsNoTracking().OrderBy(s => s.AuthorizationId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, after.Count);
        Assert.All(before.Zip(after), pair => Assert.Equal(pair.First.SessionStart, pair.Second.SessionStart));
        Assert.Equal(1, after.Count(s => s.ConfirmedUntil is null));
        Assert.Equal(before.Count(s => s.ConfirmedUntil is not null) - 1, after.Count(s => s.ConfirmedUntil is not null));
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, there));
    }

    [Fact]
    public async Task The_endpoint_is_not_in_the_description_even_with_the_setting_on()
    {
        using var response = await Client.GetAsync("/auth/openapi/v1.json", TestContext.Current.CancellationToken);

        var document = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("testing", document, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("end-confirmation", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Neither_token_nor_cookie_appears_in_the_log()
    {
        await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);

        using (await SettingsApi.EndConfirmation(Client, session))
        {
        }

        Assert.DoesNotContain(session.RefreshToken, Logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(session.AccessToken, Logs.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("  ")]
    public async Task With_the_setting_false_or_blank_the_route_does_not_exist(string value)
    {
        await using var off = new AuthAppFactory(Postgres, Keys).WithSetting(TestingSettings.WindowEndpointKey, value);
        using var client = SessionApi.CreateClient(off);
        var session = await SessionApi.LoginAsync(client, off);

        using var response = await SettingsApi.EndConfirmation(client, session);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_blank_value_outside_development_is_the_same_as_none_and_starts()
    {
        await using var production = new AuthAppFactory(Postgres, Keys)
            .WithEnvironment("Production")
            .WithSetting(TestingSettings.WindowEndpointKey, "");
        using var client = SessionApi.CreateClient(production);

        using var response = await client.GetAsync("/auth/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
