namespace Auth.Server.Email;

/// <summary>
/// Runs <see cref="MailDispatcher"/>: a pass at host start, then one whenever a request signals, a retry falls due,
/// or <see cref="MailDelivery.PollInterval"/> has passed.
/// </summary>
public sealed partial class MailDispatchService(
    MailDispatcher dispatcher, MailDispatchSignal signal, TimeProvider clock, ILogger<MailDispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                var wait = await PassAsync(stoppingToken);
                await signal.WaitAsync(wait, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: end the loop normally rather than as a cancelled task, which the host would count as a failure.
        }
    }

    /// <summary>One pass; returns how long to wait before the next one unless a signal comes first.</summary>
    private async Task<TimeSpan> PassAsync(CancellationToken stoppingToken)
    {
        try
        {
            await dispatcher.DispatchDueAsync(stoppingToken);
            if (await dispatcher.NextDueAsync(stoppingToken) is { } due)
            {
                var until = due - clock.GetUtcNow();
                return until < MailDelivery.MinimumWait ? MailDelivery.MinimumWait
                    : until < MailDelivery.PollInterval ? until
                    : MailDelivery.PollInterval;
            }
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // The host stopped in the middle of a pass. Whatever the store made of the cancellation, it is not a failure.
        }
        catch (Exception exception)
        {
            // A failed pass must not take the auth service down. Try again at the next poll.
            LogFailed(logger, exception.GetType().Name);
        }

        return MailDelivery.PollInterval;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A mail dispatch pass failed ({Failure}); it will run again at the next poll.")]
    private static partial void LogFailed(ILogger logger, string failure);
}
