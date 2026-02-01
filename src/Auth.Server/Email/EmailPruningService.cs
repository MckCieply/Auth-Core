namespace Auth.Server.Email;

/// <summary>Runs <see cref="EmailPruner"/> at host start and then every <see cref="Interval"/>.</summary>
public sealed partial class EmailPruningService(EmailPruner pruner, TimeProvider clock, ILogger<EmailPruningService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        try
        {
            do
            {
                await PruneAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: end the loop normally rather than as a cancelled task, which the host would count as a failure.
        }
    }

    private async Task PruneAsync(CancellationToken stoppingToken)
    {
        try
        {
            await pruner.PruneOnceAsync(stoppingToken);
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // The host stopped in the middle of a pass. Whatever the store made of the cancellation, it is not a failure.
        }
        catch (Exception exception)
        {
            // Pruning is housekeeping: a failed pass must not take the auth service down. Try again next time.
            LogFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Pruning of link tokens and mail limits failed; it will run again at the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
