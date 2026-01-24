using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Lockout;

/// <summary>
/// One pruning pass over the login streaks: rows without an attempt for <see cref="LockoutPolicy.StreakLifetime"/>,
/// which the rules already treat as absent. Anyone can create a row by submitting an email, so rows must expire.
/// </summary>
public sealed partial class LockoutPruner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<LockoutPruner> logger)
{
    public async Task<int> PruneOnceAsync(CancellationToken cancellationToken)
    {
        var threshold = clock.GetUtcNow() - LockoutPolicy.StreakLifetime;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var removed = await db.LoginStreaks.Where(s => s.LastAttemptAt <= threshold).ExecuteDeleteAsync(cancellationToken);

        LogPruned(logger, removed);
        return removed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Streaks} login streak entries.")]
    private static partial void LogPruned(ILogger logger, int streaks);
}
