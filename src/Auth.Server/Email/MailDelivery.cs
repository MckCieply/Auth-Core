namespace Auth.Server.Email;

/// <summary>When mail is retried and when it is given up. Constants on purpose (spec 0004, Decision 15).</summary>
public static class MailDelivery
{
    /// <summary>A request that could not be delivered this long after it was made is dropped.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(1);

    /// <summary>The dispatcher looks at the queue at least this often, even when nothing wakes it.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>The dispatcher never waits less than this between two passes, so a row it cannot take does not spin it.</summary>
    public static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(1);

    /// <summary>How long after failed attempt number <paramref name="attempts"/> the next one is due.</summary>
    public static TimeSpan RetryDelay(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(30),
        3 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(10),
    };
}
