using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class RefreshReuseTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public async Task Consumed_token_is_forgiven_inside_the_grace_window()   // criterion 4
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        var first = await SessionApi.RefreshOk(Client, login.RefreshToken);

        Clock.Advance(TimeSpan.FromSeconds(10));
        var second = await SessionApi.RefreshOk(Client, login.RefreshToken);   // an honest retry

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        await SessionApi.RefreshOk(Client, first.RefreshToken);    // nothing was revoked
        await SessionApi.RefreshOk(Client, second.RefreshToken);
    }

    [Fact]
    public async Task Concurrent_double_submit_succeeds_twice()   // criterion 4, Review Focus #5
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        var responses = await Task.WhenAll(
            SessionApi.Refresh(Client, login.RefreshToken),
            SessionApi.Refresh(Client, login.RefreshToken));

        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task Consumed_token_is_rejected_after_the_grace_window()   // criterion 2
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        await SessionApi.RefreshOk(Client, login.RefreshToken);

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(replay);
    }

    [Fact]
    public async Task Reuse_after_the_grace_window_revokes_the_whole_family()   // criterion 3
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        var current = await SessionApi.RefreshOk(Client, login.RefreshToken);

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        using var afterReplay = await SessionApi.Refresh(Client, current.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(replay);
        await SessionApi.AssertInvalidGrantAsync(afterReplay);   // the legitimate, post-rotation token died too
    }

    [Fact]
    public async Task Reuse_in_one_session_leaves_another_session_alone()   // Review Focus #4
    {
        var attacked = await SessionApi.LoginAsync(Client, Factory);
        var other = await SessionApi.LoginAsync(Client, Factory);
        await SessionApi.RefreshOk(Client, attacked.RefreshToken);

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, attacked.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(replay);
        await SessionApi.RefreshOk(Client, other.RefreshToken);
    }

    [Fact]
    public async Task Rotated_session_survives_a_host_restart()   // server-side store persists
    {
        SessionApi.Session rotated;
        await using (var before = new AuthAppFactory(Postgres, Keys, Factory.DatabaseName).WithClock(Clock))
        {
            using var client = SessionApi.CreateClient(before);
            var login = await SessionApi.LoginAsync(client, before);
            rotated = await SessionApi.RefreshOk(client, login.RefreshToken);
        }

        await using var after = new AuthAppFactory(Postgres, Keys, Factory.DatabaseName).WithClock(Clock);
        using var restarted = SessionApi.CreateClient(after);

        await SessionApi.RefreshOk(restarted, rotated.RefreshToken);
    }
}
