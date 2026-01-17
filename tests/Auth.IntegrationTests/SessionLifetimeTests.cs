using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class SessionLifetimeTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public async Task Unused_session_expires_after_the_sliding_window()   // criterion 9
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        Clock.Advance(TimeSpan.FromDays(14) + TimeSpan.FromMinutes(1));
        using var response = await SessionApi.Refresh(Client, login.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(response);
    }

    [Fact]
    public async Task Each_refresh_extends_the_sliding_window()   // criterion 9
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        Clock.Advance(TimeSpan.FromDays(13));
        var day13 = await SessionApi.RefreshOk(Client, login.RefreshToken);
        Clock.Advance(TimeSpan.FromDays(13));                                  // day 26: dead without sliding
        await SessionApi.RefreshOk(Client, day13.RefreshToken);
    }

    [Fact]
    public async Task Session_never_outlives_the_absolute_cap()   // criterion 9
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        Clock.Advance(TimeSpan.FromDays(13));
        var day13 = await SessionApi.RefreshOk(Client, login.RefreshToken);
        Clock.Advance(TimeSpan.FromDays(13));
        var day26 = await SessionApi.RefreshOk(Client, day13.RefreshToken);

        Clock.Advance(TimeSpan.FromDays(4) + TimeSpan.FromMinutes(1));          // day 30: used 4 days ago, still dead
        using var response = await SessionApi.Refresh(Client, day26.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(response);
    }

    [Fact]
    public async Task Cookie_max_age_tracks_the_time_left_to_the_cap()   // Decision 10
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        Assert.Equal(TimeSpan.FromDays(14), login.Cookie.MaxAge);

        Clock.Advance(TimeSpan.FromDays(13));
        var day13 = await SessionApi.RefreshOk(Client, login.RefreshToken);
        Assert.Equal(TimeSpan.FromDays(14), day13.Cookie.MaxAge);

        Clock.Advance(TimeSpan.FromDays(13));
        var day26 = await SessionApi.RefreshOk(Client, day13.RefreshToken);
        // 4 days to the cap. The session start is stored in whole seconds, so allow one second.
        Assert.InRange(day26.Cookie.MaxAge!.Value, TimeSpan.FromDays(4) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(4));
    }
}
