using Auth.Server.Email;

namespace Auth.IntegrationTests;

public sealed class MailLimitPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Registers requests at the given offsets from <see cref="T0"/> and returns the state and the last decision.</summary>
    private static (MailLimitState State, MailLimitDecision Last) After(params TimeSpan[] offsets)
    {
        var state = MailLimitState.Fresh(T0);
        MailLimitDecision last = default;
        foreach (var offset in offsets)
        {
            (state, last) = MailLimitPolicy.Register(state, T0 + offset);
        }

        return (state, last);
    }

    private static TimeSpan Seconds(int seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void First_request_is_accepted_and_opens_a_window()
    {
        var (state, last) = After(TimeSpan.Zero);

        Assert.True(last.Allowed);
        Assert.Equal(new MailLimitState(T0, 1, T0), state);
    }

    [Fact]
    public void Second_request_within_sixty_seconds_is_refused_with_the_time_left()   // criterion 9
    {
        var (state, last) = After(TimeSpan.Zero, Seconds(20));

        Assert.False(last.Allowed);
        Assert.Equal(Seconds(40), last.RetryAfter);
        Assert.Equal(new MailLimitState(T0, 1, T0), state);   // a refused request changes nothing
    }

    [Fact]
    public void Refused_requests_do_not_push_the_wait_up()   // criterion 9
    {
        var (_, last) = After(TimeSpan.Zero, Seconds(20), Seconds(30), Seconds(59));

        Assert.False(last.Allowed);
        Assert.Equal(Seconds(1), last.RetryAfter);
    }

    [Fact]
    public void Request_after_sixty_seconds_is_accepted_in_the_same_window()
    {
        var (state, last) = After(TimeSpan.Zero, Seconds(60));

        Assert.True(last.Allowed);
        Assert.Equal(new MailLimitState(T0, 2, T0 + Seconds(60)), state);
    }

    [Fact]
    public void Sixth_request_in_the_window_is_refused_until_the_window_ends()   // criterion 9
    {
        var (state, last) = After(Seconds(0), Seconds(61), Seconds(122), Seconds(183), Seconds(244), Seconds(305));

        Assert.False(last.Allowed);
        Assert.Equal(Seconds(3600 - 305), last.RetryAfter);
        Assert.Equal(5, state.WindowCount);
    }

    [Fact]
    public void Window_is_fixed_and_reopens_an_hour_after_it_opened()
    {
        var (state, last) = After(Seconds(0), Seconds(61), Seconds(122), Seconds(183), Seconds(244), Seconds(3600));

        Assert.True(last.Allowed);
        Assert.Equal(new MailLimitState(T0 + Seconds(3600), 1, T0 + Seconds(3600)), state);
    }

    [Fact]
    public void Longer_of_the_two_waits_is_reported()
    {
        // The fifth request lands 10 s before the window ends: the window frees in 5 s, the gap in 55 s.
        var (_, last) = After(Seconds(0), Seconds(900), Seconds(1800), Seconds(2700), Seconds(3590), Seconds(3595));

        Assert.False(last.Allowed);
        Assert.Equal(Seconds(55), last.RetryAfter);
    }

    [Fact]
    public void Window_that_closed_long_ago_counts_for_nothing()
    {
        var (state, last) = After(Seconds(0), Seconds(61), Seconds(122), Seconds(183), Seconds(244), TimeSpan.FromDays(3));

        Assert.True(last.Allowed);
        Assert.Equal(1, state.WindowCount);
    }
}
