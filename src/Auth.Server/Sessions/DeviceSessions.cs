using System.Security.Claims;
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using OpenIddict.Server;

namespace Auth.Server.Sessions;

/// <summary>
/// The session of this device (spec 0012 → General rules): the session the request's refresh cookie belongs to, with its start and its window.
/// </summary>
/// <param name="TokenId">The id of the cookie's refresh token, by which a re-read under a lock finds the session again.</param>
/// <param name="Start">The stored start, or for a session from before this version the creation of its authorization.</param>
/// <param name="ConfirmedUntil">The end of the window; <see langword="null"/> for one that was closed or never opened.</param>
/// <param name="Company">The company claims of the cookie's refresh token (spec 0015), when the finder read them; a token's claims never change.</param>
public sealed record DeviceSession(
    string TokenId, string AuthorizationId, Guid UserId, DateTimeOffset Start, DateTimeOffset? ConfirmedUntil, TokenCompany Company = default)
{
    public bool IsConfirmedAt(DateTimeOffset now) => ConfirmedUntil is { } until && until > now;
}

/// <summary>
/// The session company a refresh token names (spec 0015): <paramref name="Present"/> when it has either claim, <paramref name="Space"/> when both
/// are there and read. Present without a space is a token that does not read; not present is one from before spec 0015.
/// </summary>
public readonly record struct TokenCompany(bool Present, SessionSpace? Space);

/// <summary>
/// Finds the session of this device without redeeming the cookie (Amendment A8): its token must be a refresh token, <c>valid</c> or redeemed
/// within the reuse leeway of spec 0002 (15 seconds), not expired, of a <c>valid</c> authorization, and of the account the access token names.
/// Anything else is no session. The token manager finds the token (it hashes the cookie as the column holds it); everything else is read afresh,
/// without tracking, so that a re-read under a lock sees what other requests committed meanwhile.
/// </summary>
public static class DeviceSessions
{
    public static async Task<DeviceSession?> FindAsync(
        HttpContext http, AuthDbContext db, IOpenIddictTokenManager tokens, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokens);

        if (!RefreshCookie.TryRead(http.Request, out var reference))
        {
            return null;
        }

        var token = await tokens.FindByReferenceIdAsync(reference, cancellationToken);
        if (token is null || await tokens.GetIdAsync(token, cancellationToken) is not { } tokenId)
        {
            return null;
        }

        var company = await ReadCompanyAsync(http, tokens, token, cancellationToken);
        return await ReadAsync(db, tokenId, userId, now, cancellationToken, company);
    }

    /// <summary>
    /// The company claims of the stored refresh token: its entry holds the token encrypted to the instance's key, and the entry is already loaded.
    /// A payload that cannot be read counts as claims that do not read, which the renewal refuses.
    /// </summary>
    private static async Task<TokenCompany> ReadCompanyAsync(HttpContext http, IOpenIddictTokenManager tokens, object token, CancellationToken cancellationToken)
    {
        try
        {
            if (await tokens.GetPayloadAsync(token, cancellationToken) is not { } payload)
            {
                return new TokenCompany(true, null);
            }

            var server = http.RequestServices.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;
            // The validation OpenIddict makes of a token it issued (ValidateIdentityModelToken): its own handler and parameters, the signing and
            // decryption keys, the refresh-token type and the issuer. The lifetime was judged on the token row, and the audience is not
            // the refresh token's to check. The redeemed status is not judged here either: the 15 s leeway of this class does that.
            var parameters = server.TokenValidationParameters.Clone();
            parameters.IssuerSigningKeys = [.. server.SigningCredentials.Select(c => c.Key)];
            parameters.TokenDecryptionKeys = [.. server.EncryptionCredentials.Select(c => c.Key)];
            parameters.ValidTypes = [OpenIddictConstants.JsonWebTokenTypes.Private.RefreshToken];
            parameters.ValidIssuer = server.Issuer?.AbsoluteUri;
            parameters.ValidateIssuer = true;
            parameters.ValidateLifetime = false;
            parameters.ValidateAudience = false;
            var result = await server.JsonWebTokenHandler.ValidateTokenAsync(payload, parameters);
            if (!result.IsValid || result.ClaimsIdentity is null)
            {
                return new TokenCompany(true, null);
            }

            var principal = new ClaimsPrincipal(result.ClaimsIdentity);
            return new TokenCompany(SessionCompany.HasAny(principal), SessionCompany.Read(principal));
        }
        catch (Exception exception) when (exception is ArgumentException or SecurityTokenException or FormatException)
        {
            return new TokenCompany(true, null);
        }
    }

    /// <summary>The session of the refresh token <paramref name="tokenId"/>, by the rules above, read now; <see langword="null"/> when it names none.</summary>
    public static async Task<DeviceSession?> ReadAsync(
        AuthDbContext db, string tokenId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken, TokenCompany company = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokenId);

        var row = await db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking()
            .Where(t => t.Id == tokenId && t.Authorization != null)
            .Select(t => new
            {
                t.Type,
                t.Status,
                t.RedemptionDate,
                t.ExpirationDate,
                t.Subject,
                AuthorizationId = t.Authorization!.Id,
                AuthorizationStatus = t.Authorization.Status,
                Created = t.Authorization.CreationDate,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is not { AuthorizationId: { } authorizationId }
            || !string.Equals(row.Type, OpenIddictConstants.TokenTypeIdentifiers.RefreshToken, StringComparison.Ordinal)
            || !string.Equals(row.Subject, userId.ToString(), StringComparison.Ordinal)
            || !string.Equals(row.AuthorizationStatus, OpenIddictConstants.Statuses.Valid, StringComparison.Ordinal)
            || row.ExpirationDate is not { } expires || Utc(expires) <= now
            || !(string.Equals(row.Status, OpenIddictConstants.Statuses.Valid, StringComparison.Ordinal)
                || (string.Equals(row.Status, OpenIddictConstants.Statuses.Redeemed, StringComparison.Ordinal)
                    && row.RedemptionDate is { } redeemed && Utc(redeemed) + SessionPolicy.ReuseLeeway > now)))
        {
            return null;
        }

        var stored = await db.SessionStates.AsNoTracking().FirstOrDefaultAsync(s => s.AuthorizationId == authorizationId, cancellationToken);
        if (stored is not null)
        {
            return new DeviceSession(tokenId, authorizationId, userId, stored.SessionStart, stored.ConfirmedUntil, company);
        }

        // A session from before this version: it began when its authorization was made (a refresh keeps it), and its window is closed.
        // An authorization without a creation date has no start to give: it names no session.
        return row.Created is { } created ? new DeviceSession(tokenId, authorizationId, userId, Utc(created), null, company) : null;
    }

    /// <summary>
    /// Opens the window of the session until <paramref name="until"/>, in the caller's transaction (spec 0012 → <c>POST /auth/account/confirm</c>, step
    /// 6). A session from before this version gets its row now, with the start it has (its authorization's creation).
    /// </summary>
    public static Task<int> ConfirmAsync(AuthDbContext db, DeviceSession session, DateTimeOffset until, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(session);

        return db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "SessionStates" ("AuthorizationId", "SessionStart", "ConfirmedUntil")
            VALUES ({session.AuthorizationId}, {session.Start}, {until})
            ON CONFLICT ("AuthorizationId") DO UPDATE SET "ConfirmedUntil" = EXCLUDED."ConfirmedUntil"
            """,
            cancellationToken);
    }

    /// <summary>Ends the window of the session (the testing endpoint); a session without a row has none to end.</summary>
    public static Task<int> EndWindowAsync(AuthDbContext db, DeviceSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(session);

        return db.SessionStates
            .Where(s => s.AuthorizationId == session.AuthorizationId)
            .ExecuteUpdateAsync(s => s.SetProperty(state => state.ConfirmedUntil, (DateTimeOffset?)null), cancellationToken);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
