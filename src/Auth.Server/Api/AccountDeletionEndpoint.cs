using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Auth.Server.Requests;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Api;

/// <summary>
/// <c>DELETE /auth/account</c> (spec 0010): a private person deletes their own account. The checks run in this order: the token's
/// <c>org_kind</c> against the database (a token that says <c>personal</c> while the database shows a company or nothing is
/// <c>403 permissions_changed</c>, so that the frontend refreshes; a company member is <c>403 forbidden</c>: company accounts are managed by their
/// admins), the body, the address lock, and then, under the lock, the membership again (an invitation may have moved the account meanwhile), the
/// lockout of the caller's identifier and the password (the attempt counted first, as a login counts it; a correct one ends the streak; the password
/// is evaluated once, as login and <c>DELETE /auth/org</c> evaluate it). The account is then deleted with everything it has
/// (<see cref="AccountDeletion"/>, reason <c>self</c>) in the same transaction; the answer clears the refresh cookie as logout does.
/// <para>
/// Why the lock comes before the counting and the hash. An account that moved into a company, or was deleted, while the request waited for the lock
/// is refused as <c>permissions_changed</c> before any attempt is counted and before any password is evaluated; the password that is verified is the
/// one read under the lock, and the check and the deletion sit under one lock, with no window between them. It is not a deadlock that the order
/// avoids: the login streak is written by <see cref="LoginStreakStore"/>, which commits on a connection of its own before it returns, so this
/// transaction never holds a streak row while it waits for the address lock. That is the rule to keep: never write the streak on the request's own
/// context inside this transaction.
/// </para>
/// <para>
/// The cost: the transaction and the lock are held across one password hash, and a second pooled connection serves the streak and the audit rows of
/// a refusal meanwhile. Only the holder of a valid token of a private account can take the lock this way, the lockout stops the hashing after ten
/// attempts, and the <c>general</c> limit caps the requests per client address.
/// </para>
/// The product deletes its own data under the token's <c>org_id</c> before it calls this: Auth-Core does not tell it.
/// </summary>
public static class AccountDeletionEndpoint
{
    public const string Path = "/auth/account";

    private static readonly string[] Fields = ["password"];

    public static async Task<IResult> DeleteAsync(
        HttpContext http, MembershipReader memberships, UserManager<ApplicationUser> users, LoginStreakStore streaks, AuthDbContext db,
        AccountDeletion deletion, AuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(streaks);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(deletion);
        ArgumentNullException.ThrowIfNull(audit);

        // The sub of a token this service issued is always a user id; anything else is not a caller we know.
        if (!Guid.TryParse(http.User.FindFirst(Claims.Subject)?.Value, out var userId))
        {
            return ApiResults.Error(TenancyErrors.Forbidden);
        }

        var tenant = await memberships.ReadAsync(userId, http.RequestAborted);
        if (Refusal(http.User, tenant) is { } refused)
        {
            return refused;
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, http.RequestAborted);
        if (fields is null || fields[0].Contains('\0'))
        {
            return ApiResults.InvalidRequest();
        }

        // Read without tracking: nothing of the account stays on the request's context to be saved after its row is deleted.
        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.NormalizedEmail })
            .FirstOrDefaultAsync(http.RequestAborted);
        if (account is not { Email: { } email, NormalizedEmail: { } normalized })
        {
            return ApiResults.Error(TenancyErrors.PermissionsChanged);   // gone since the membership was read
        }

        // The address lock before anything is counted or hashed (see the class summary). It is held to the end of the transaction, which is also
        // the end of this method: the refusals below roll it back.
        await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
        await AddressLock.AcquireAsync(db, normalized, http.RequestAborted);

        // Read again under the lock: an invitation may have moved the account into a company, or another deletion may have removed it, while this
        // request waited.
        var caller = await memberships.ReadAsync(userId, http.RequestAborted);
        if (caller is not { Kind: CompanyKind.Personal })
        {
            return ApiResults.Error(TenancyErrors.PermissionsChanged);
        }

        // The hash is read under the lock too: a password changed while this request waited must not be answered by the old one.
        var stored = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.PasswordHash })
            .FirstOrDefaultAsync(http.RequestAborted);
        if (stored is null)
        {
            return ApiResults.Error(TenancyErrors.PermissionsChanged);
        }

        var identifier = LoginIdentifier.HashOf(normalized);
        var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
        if (!decision.Allowed)
        {
            await RecordRefusalAsync(audit, caller, email, "locked");
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        if (!PasswordIsRight(users, stored.PasswordHash, fields[0]))
        {
            await RecordRefusalAsync(audit, caller, email, "wrong_password");
            return ApiResults.Error(TenancyErrors.WrongPassword);
        }

        // Not the request's token from here on: from the tenth attempt counting has started a cooldown, and a client that goes away must leave
        // neither the cooldown nor a half-done deletion behind.
        await streaks.ClearAsync(identifier, CancellationToken.None);

        await deletion.DeleteAsync(userId, AccountDeletionReason.Self, actor: null, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        RefreshCookie.Clear(http.Response);
        return ApiResults.NoContent();
    }

    /// <summary>
    /// Whether the password is the account's. The hash is only verified: <c>UserManager.CheckPasswordAsync</c> would also store a new hash
    /// when the old one is out of date, an update of a row this request is about to delete.
    /// </summary>
    private static bool PasswordIsRight(UserManager<ApplicationUser> users, string? hash, string password) =>
        hash is not null
        && users.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), hash, password) != PasswordVerificationResult.Failed;

    /// <summary>
    /// The first rows of the table of spec 0010, the token's <c>org_kind</c> judged first; <see langword="null"/> for a private account. A token
    /// that does not say <c>personal</c> while the database shows a personal space is let through: the database decides.
    /// </summary>
    private static IResult? Refusal(System.Security.Claims.ClaimsPrincipal user, TenantContext? tenant)
    {
        if (tenant is { Kind: CompanyKind.Personal })
        {
            return null;
        }

        var saysPersonal = string.Equals(user.FindFirst(TenantClaims.OrgKind)?.Value, SpaceKinds.PersonalName, StringComparison.Ordinal);
        if (saysPersonal)
        {
            return ApiResults.Error(TenancyErrors.PermissionsChanged);   // it moved to a company, or it is gone: the frontend refreshes
        }

        if (tenant is null)
        {
            return ApiResults.Error(user.HasClaim(c => c.Type == TenantClaims.OrgId) ? TenancyErrors.PermissionsChanged : TenancyErrors.Forbidden);
        }

        return ApiResults.Error(TenancyErrors.Forbidden);   // a company member: company accounts are managed by their admins
    }

    private static Task RecordRefusalAsync(AuditLog audit, TenantContext caller, string email, string reason) =>
        audit.WriteAloneAsync(new AuditEntry
        {
            Kind = AuditKinds.AccountDeleteRefused,
            ActorUserId = caller.UserId,
            SubjectUserId = caller.UserId,
            SubjectEmail = email,
            OrgId = caller.CompanyId,
            OrgName = caller.CompanyName,
            Details = new Dictionary<string, object?> { ["reason"] = reason },
        });
}
