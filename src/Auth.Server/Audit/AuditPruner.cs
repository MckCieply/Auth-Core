using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Audit;

/// <summary>
/// One pruning pass over the audit log: the rows older than the retention (<see cref="AuditSettings"/>). Anyone can make rows
/// (failed logins, mail requests), so the table is bounded by the per-IP limits and by this.
/// </summary>
public sealed partial class AuditPruner(IServiceScopeFactory scopes, TimeProvider clock, AuditSettings settings, ILogger<AuditPruner> logger)
{
    public async Task<int> PruneOnceAsync(CancellationToken cancellationToken)
    {
        var threshold = clock.GetUtcNow() - TimeSpan.FromDays(settings.RetentionDays);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var removed = await db.AuditEvents.Where(a => a.OccurredAt < threshold).ExecuteDeleteAsync(cancellationToken);

        LogPruned(logger, removed);
        return removed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Events} audit events.")]
    private static partial void LogPruned(ILogger logger, int events);
}
