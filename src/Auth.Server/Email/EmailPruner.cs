using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// One pruning pass over the tables of spec 0004: link tokens that have expired, and mail-limit rows without an
/// accepted request for <see cref="MailLimitPolicy.Window"/>, which the rules already treat as absent. Anyone can
/// create a limit row by submitting an address, so rows must expire.
/// </summary>
public sealed partial class EmailPruner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<EmailPruner> logger)
{
    public async Task<(int Tokens, int Limits)> PruneOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var idleSince = now - MailLimitPolicy.Window;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var tokens = await db.EmailTokens.Where(t => t.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        var limits = await db.MailRequestLimits.Where(l => l.LastAcceptedAt <= idleSince).ExecuteDeleteAsync(cancellationToken);

        LogPruned(logger, tokens, limits);
        return (tokens, limits);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Tokens} link token and {Limits} mail limit entries.")]
    private static partial void LogPruned(ILogger logger, int tokens, int limits);
}
