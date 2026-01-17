using Auth.Server.Sessions;

namespace Auth.IntegrationTests;

public sealed class SessionPolicyTests
{
    public static TheoryData<double, double?> Cases => new()
    {
        { 0, 14 },          // fresh session: the whole sliding window
        { 16, 14 },         // exactly 14 days left to the cap
        { 17, 13 },         // the cap is closer than the window
        { 29.5, 0.5 },
        { 30, null },       // cap reached
        { 31, null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Remaining_lifetime_is_the_shorter_of_window_and_cap(double ageDays, double? expectedDays)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        TimeSpan? expected = expectedDays is { } days ? TimeSpan.FromDays(days) : null;

        var remaining = SessionPolicy.RemainingLifetime(start, start.AddDays(ageDays));

        Assert.Equal(expected, remaining);
    }
}
