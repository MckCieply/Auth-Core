using Auth.Infrastructure.Identity;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Login;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore.Storage;
using OpenIddict.Abstractions;

namespace Auth.Server.Account;

/// <summary>
/// A renewal (spec 0012 → Sessions): the unlink and the password change end every session of the account and hand this device a new one, whose
/// principal is built after the change, with the stored start (so that the 30-day cap counts from there) and the old window. A session past its
/// cap cannot be renewed: the endpoint checks <see cref="CanRenew"/> before it changes anything.
/// </summary>
internal static class SessionRenewal
{
    public const string Via = "session_renewal";

    public static bool CanRenew(DeviceSession session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);

        return SessionPolicy.RemainingLifetime(session.Start, now) is not null;
    }

    /// <summary>
    /// Every refresh token and authorization of the account revoked, in the caller's transaction: what <c>AccountSessions.EndAllAsync</c> does to
    /// the sessions, without the email links and the streak. It runs before the new session is issued, which it would revoke too.
    /// </summary>
    public static async Task EndSessionsAsync(
        IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(authorizations);

        var subject = userId.ToString();
        await tokens.RevokeBySubjectAsync(subject, cancellationToken);
        await authorizations.RevokeBySubjectAsync(subject, cancellationToken);
    }

    /// <summary>
    /// The answer of a renewal, in <paramref name="transaction"/>, which the result owns from now on: a login's answer, <c>login.succeeded</c>
    /// with <c>via</c> <c>session_renewal</c>, the new session with the start and the window of <paramref name="caller"/>'s. An account gone
    /// meanwhile is <c>401 session_required</c>.
    /// </summary>
    public static IResult Issue(
        HttpContext http, ApplicationUser account, SettingsCaller caller, IDbContextTransaction transaction, string audience, TimeProvider clock,
        Action<HttpContext>? whenCommitted)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // The session's company claims come from caller.Tenant, which is the session's own company: SettingsCallers refuses (401 session_required)
        // a caller whose company or membership differs from the one the session's refresh token names, and a token from before spec 0015 names none.
        return new AtomicSignInResult(
            SessionPrincipal.Create(http, account, caller.Tenant, audience, clock, caller.Session.Start),
            SettingsCallers.Entry(AuditKinds.LoginSucceeded, caller, new Dictionary<string, object?> { ["via"] = Via }),
            transaction,
            whenGone: () => Task.FromResult(ApiResults.Error(TenancyErrors.SessionRequired)),
            whenCommitted,
            session: new SessionValues(caller.Session.Start, caller.Session.ConfirmedUntil));
    }
}
