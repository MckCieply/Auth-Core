namespace Auth.Server.Lockout;

/// <summary>Runs <see cref="LockoutPruner"/> at host start and then every <see cref="Interval"/>.</summary>
public sealed partial class LockoutPruningService(LockoutPruner pruner, TimeProvider clock, ILogger<LockoutPruningService> logger) : BackgroundService
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Login streak pruning failed; it will run again at the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
