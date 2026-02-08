using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// One pruning pass over the tables of spec 0004: link tokens that have expired, and mail-limit rows without an
/// accepted request for <see cref="MailLimitPolicy.Window"/>, which the rules already treat as absent. Anyone can
/// create a limit row by submitting an address, so rows must expire. Expired invitations (spec 0005) go in a method of
/// their own, <see cref="PruneInvitesAsync"/>, which <see cref="EmailPruningService"/> runs in the same pass.
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

    /// <summary>
    /// Removes the invitations whose seven days are over (spec 0005 → Invitations): they have left the list already, and
    /// an expired one is replaced when the address is invited again, so this only keeps the table from growing. A queued
    /// mail for a removed invitation is dropped by the dispatcher.
    /// </summary>
    public async Task<int> PruneInvitesAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var invites = await db.Invites.Where(i => i.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);

        LogPrunedInvites(logger, invites);
        return invites;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Tokens} link token and {Limits} mail limit entries.")]
    private static partial void LogPruned(ILogger logger, int tokens, int limits);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Invites} expired invitations.")]
    private static partial void LogPrunedInvites(ILogger logger, int invites);
}
