using OpenIddict.Abstractions;

namespace Auth.Server.Sessions;

/// <summary>
/// One pruning pass over the OpenIddict store: entries older than the sliding window that are no longer usable
/// (expired, consumed, revoked). Younger entries stay, consumed ones included — reuse detection needs them.
/// </summary>
public sealed partial class TokenPruner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<TokenPruner> logger)
{
    public async Task<(long Tokens, long Authorizations)> PruneOnceAsync(CancellationToken cancellationToken)
    {
        var threshold = clock.GetUtcNow() - SessionPolicy.SlidingLifetime;

        await using var scope = scopes.CreateAsyncScope();
        var tokens = await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>().PruneAsync(threshold, cancellationToken);
        // Tokens first: an authorization is only removable once no token refers to it.
        var authorizations = await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>().PruneAsync(threshold, cancellationToken);

        LogPruned(logger, tokens, authorizations);
        return (tokens, authorizations);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Tokens} token and {Authorizations} authorization entries.")]
    private static partial void LogPruned(ILogger logger, long tokens, long authorizations);
}
