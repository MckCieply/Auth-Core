using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// The start-up step of spec 0010 (Upgrade): every account without a membership, which an earlier version left behind when it removed a member
/// or deleted a company, is deleted by <see cref="AccountDeletion"/> with the reason <c>upgrade</c>, no actor and no client address. It runs
/// after the migrations and the manifest and before the development seeder, in the service only (the operator CLI never builds a host). Each
/// account in a transaction of its own, under the address lock, read again under it: another replica, or an invitation of the address, may have
/// given it a space since the list was read. A start that finds none changes nothing.
/// </summary>
public sealed partial class AccountsWithoutSpaceCleanup(IServiceScopeFactory scopes, ILogger<AccountsWithoutSpaceCleanup> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        List<Candidate> candidates;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            candidates = await db.Users.AsNoTracking()
                .Where(u => !db.Memberships.Any(m => m.UserId == u.Id))
                .Select(u => new Candidate(u.Id, u.NormalizedEmail))
                .ToListAsync(cancellationToken);
        }

        var deleted = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                deleted += await DeleteOneAsync(candidate, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(logger, candidate.Id, exception);
                throw;
            }
        }

        if (deleted > 0)
        {
            LogDeleted(logger, deleted);
        }

        return deleted;
    }

    private async Task<int> DeleteOneAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (candidate.NormalizedEmail is { } address)
        {
            await AddressLock.AcquireAsync(db, address, cancellationToken);
        }

        if (await db.Memberships.AnyAsync(m => m.UserId == candidate.Id, cancellationToken))
        {
            return 0;   // it has a space by now; the transaction ends unused with the scope
        }

        var deleted = await scope.ServiceProvider.GetRequiredService<AccountDeletion>()
            .DeleteAsync(candidate.Id, AccountDeletionReason.Upgrade, actor: null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private sealed record Candidate(Guid Id, string? NormalizedEmail);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The start-up step could not delete account {UserId}, which has no space; the service does not start.")]
    private static partial void LogFailed(ILogger logger, Guid userId, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} accounts that belonged to no company and had no personal space.")]
    private static partial void LogDeleted(ILogger logger, int count);
}
