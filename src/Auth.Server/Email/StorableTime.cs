namespace Auth.Server.Email;

public static class StorableTime
{
    /// <summary>
    /// The current instant at the precision PostgreSQL keeps (microseconds), so that an instant computed here and
    /// one read back from a row compare equal.
    /// </summary>
    public static DateTimeOffset Now(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var instant = clock.GetUtcNow();
        return instant.AddTicks(-(instant.Ticks % TimeSpan.TicksPerMicrosecond));
    }
}
