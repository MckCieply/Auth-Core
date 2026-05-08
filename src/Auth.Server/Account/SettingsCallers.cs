using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Account;

/// <summary>The caller of a settings endpoint (spec 0012 → General rules): the account, its space and the session of this device, as read now.</summary>
internal sealed record SettingsCaller(
    Guid UserId, string Email, string NormalizedEmail, string? PasswordHash, TenantContext Tenant, DeviceSession Session);

/// <summary>The caller, or the answer that refuses them.</summary>
internal readonly record struct SettingsRead(SettingsCaller? Caller, IResult? Refusal);

/// <summary>
/// Step 2 of the four settings endpoints, in one place: the session of this device (<c>401 session_required</c>, after the access token's own
/// <c>401</c>), the account (an account deleted meanwhile has no session), and its membership (<c>403 no_membership</c>, nothing written). The
/// endpoints read it first, then again inside their transaction under the address lock, where its answer is the one that counts.
/// </summary>
internal static class SettingsCallers
{
    public const string ConfirmAction = "confirm";
    public const string PasswordAction = "password";

    public static async Task<SettingsRead> ReadAsync(
        HttpContext http, AuthDbContext db, IOpenIddictTokenManager tokens, MembershipReader memberships, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(clock);

        // The sub of a token this service issued is always a user id; without one there is no session of it either.
        if (!Guid.TryParse(http.User.FindFirst(Claims.Subject)?.Value, out var userId))
        {
            return Refused(TenancyErrors.SessionRequired);
        }

        var session = await DeviceSessions.FindAsync(http, db, tokens, userId, StorableTime.Now(clock), cancellationToken);
        return session is null ? Refused(TenancyErrors.SessionRequired) : await CallerOfAsync(db, memberships, userId, session, cancellationToken);
    }

    /// <summary>The same checks under the caller's lock: the session by its token, read afresh, the account and the membership.</summary>
    public static async Task<SettingsRead> ReadAgainAsync(AuthDbContext db, MembershipReader memberships, SettingsCaller caller, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(caller);

        var session = await DeviceSessions.ReadAsync(db, caller.Session.TokenId, caller.UserId, now, CancellationToken.None);
        return session is null
            ? Refused(TenancyErrors.SessionRequired)
            : await CallerOfAsync(db, memberships, caller.UserId, session, CancellationToken.None);
    }

    /// <summary>
    /// Whether the password is the account's. The hash is only verified: <c>UserManager.CheckPasswordAsync</c> would also store a new hash when the
    /// old one is out of date, an update of a row that a change under the lock is about to write.
    /// </summary>
    public static bool PasswordIsRight(UserManager<ApplicationUser> users, string hash, string password)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(hash);
        ArgumentNullException.ThrowIfNull(password);

        return users.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), hash, password) != PasswordVerificationResult.Failed;
    }

    /// <summary>An audit row of the caller's own change: the caller is actor and subject, in their space.</summary>
    public static AuditEntry Entry(string kind, SettingsCaller caller, IReadOnlyDictionary<string, object?>? details = null)
    {
        ArgumentNullException.ThrowIfNull(caller);

        return new AuditEntry
        {
            Kind = kind,
            ActorUserId = caller.UserId,
            SubjectUserId = caller.UserId,
            SubjectEmail = caller.Email,
            OrgId = caller.Tenant.CompanyId,
            OrgName = caller.Tenant.CompanyName,
            Details = details,
        };
    }

    /// <summary><c>account.confirm_refused</c> (spec 0012 → Audit events), written alone: the refusal changes nothing else.</summary>
    public static Task RefuseAsync(AuditLog audit, SettingsCaller caller, string reason, string action)
    {
        ArgumentNullException.ThrowIfNull(audit);

        return audit.WriteAloneAsync(Entry(
            AuditKinds.AccountConfirmRefused, caller, new Dictionary<string, object?> { ["reason"] = reason, ["action"] = action }));
    }

    private static async Task<SettingsRead> CallerOfAsync(
        AuthDbContext db, MembershipReader memberships, Guid userId, DeviceSession session, CancellationToken cancellationToken)
    {
        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.NormalizedEmail, u.PasswordHash })
            .FirstOrDefaultAsync(cancellationToken);
        if (account is not { Email: { } email, NormalizedEmail: { } normalized })
        {
            return Refused(TenancyErrors.SessionRequired);
        }

        return await memberships.ReadAsync(userId, cancellationToken) is { } tenant
            ? new SettingsRead(new SettingsCaller(userId, email, normalized, account.PasswordHash, tenant, session), null)
            : Refused(TenancyErrors.NoMembership);
    }

    private static SettingsRead Refused(string code) => new(null, ApiResults.Error(code));
}
