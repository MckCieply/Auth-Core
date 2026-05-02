using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Login;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Providers;

/// <summary>
/// <c>POST /auth/providers/link</c> (spec 0011): the password of the account with the ticket's address, once (Decision 7), and from then on
/// the provider alone signs in. The checks run in the order of the contract: the body; the ticket (a live sign-in ticket of this browser); an identity
/// linked meanwhile signs in without the password being judged; no address; the attempt counted in the login streak of the address before anything
/// is looked up; an unknown address, an account under another spelling or without a password, and a wrong password are one
/// <c>401 invalid_credentials</c> after a password hash; then, the password right, the streak cleared, the address confirmed when the provider
/// verified it (else <c>403 email_not_verified</c>), no membership <c>403 no_membership</c>; and in one transaction under the address lock the
/// login, <c>provider.linked</c>, the ticket used up and the sign-in.
/// </summary>
public static class ProviderLinkEndpoint
{
    private const string TicketField = "ticket";
    private const string PasswordField = "password";

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, LoginStreakStore streaks,
        DecoyPasswordHash decoy, AuditLog audit, IOptions<TokenOptions> tokens, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(streaks);
        ArgumentNullException.ThrowIfNull(decoy);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);

        // Without it OpenIddict cannot sign in here: fail at once, as the exchange does.
        if (http.GetOpenIddictServerRequest() is null)
        {
            throw new InvalidOperationException("The link was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        }

        // 1. A ticket that is a string (a blank one is unknown at step 2), and a password that is a non-blank string with no NUL.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields
            || !fields.TryAnyString(TicketField, out var ticketText)
            || !fields.TryString(PasswordField, out var password)
            || password.Contains('\0', StringComparison.Ordinal))
        {
            return ApiResults.InvalidRequest();
        }

        // 2. A live sign-in ticket of this browser.
        ProviderBindingCookie.TryRead(http.Request, out var binding);
        var ticket = await ProviderTickets.FindAsync(db, ticketText, binding, StorableTime.Now(clock), http.RequestAborted);
        if (ticket is not { Purpose: ProviderTicketPurpose.SignIn })
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);
        }

        // 3. Linked by now (another tab): sign in as the exchange does; the password is not judged.
        if (await ProviderLogins.FindAccountAsync(db, ticket.Provider, ticket.IdentityKey, http.RequestAborted) is { } linked)
        {
            return await ProviderSignIn.LinkedAsync(http, db, users, memberships, tokens.Value, clock, linked, ticket, ticketText, binding!);
        }

        // 4.
        if (ticket.Email is not { } email)
        {
            return ApiResults.Error(TenancyErrors.NoEmail);
        }

        // 5. Counted before anything is looked up, in the streak of the address (spec 0003): a password typed here is a login attempt.
        var identifier = LoginIdentifier.HashOf(users.NormalizeEmail(email));
        var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
        if (!decision.Allowed)
        {
            await audit.WriteAloneAsync(new AuditEntry
            {
                Kind = AuditKinds.LoginLocked,
                SubjectEmail = email,
                Details = new Dictionary<string, object?>
                {
                    ["retry_after_seconds"] = TooManyAttemptsResult.SecondsOf(decision.RetryAfter),
                    ["provider"] = ticket.Provider,
                },
            });
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        // 6. One answer for no account, an account under another spelling, an account without a password, and a wrong password.
        var account = await users.FindByEmailAsync(email);
        if (account is null || !EmailInput.IsSameAddress(account.Email, email) || account.PasswordHash is null)
        {
            _ = users.PasswordHasher.VerifyHashedPassword(account ?? new ApplicationUser(), decoy.Value, password);
            await RecordFailureAsync(audit, "unknown_address", null, email, ticket.Provider);
            return new LoginEndpoint.InvalidCredentialsResult();
        }

        if (!await users.CheckPasswordAsync(account, password))
        {
            await RecordFailureAsync(audit, "wrong_password", account, email, ticket.Provider);
            return new LoginEndpoint.InvalidCredentialsResult();
        }

        // 7. The password proves the account: the streak is over (not the request's token, as at login).
        await streaks.ClearAsync(identifier, CancellationToken.None);
        if (!account.EmailConfirmed && !ticket.EmailVerified)
        {
            await RecordFailureAsync(audit, "unconfirmed_address", account, email, ticket.Provider);
            return ApiResults.Error(TenancyErrors.EmailNotVerified);
        }

        if (await memberships.ReadAsync(account.Id, http.RequestAborted) is null)
        {
            await RecordFailureAsync(audit, "no_company", account, email, ticket.Provider);
            return ApiResults.Error(TenancyErrors.NoMembership);
        }

        return await LinkAndSignInAsync(http, db, users, memberships, audit, tokens.Value, clock, account, ticket, ticketText, binding!);
    }

    /// <summary>8. In one transaction under the address lock: the login, <c>provider.linked</c>, the ticket used up, the sign-in.</summary>
    private static async Task<IResult> LinkAndSignInAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, AuditLog audit, TokenOptions tokens,
        TimeProvider clock, ApplicationUser account, ProviderTicket ticket, string ticketText, string binding)
    {
        // Not the request's token from here on: a client that goes away must not leave the link half done.
        var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        try
        {
            await AddressLock.AcquireAsync(db, account.NormalizedEmail!, CancellationToken.None);
            if (await ProviderTickets.ConsumeAsync(db, ticketText, binding, ProviderTicketPurpose.SignIn, StorableTime.Now(clock), CancellationToken.None) is null)
            {
                await transaction.DisposeAsync();
                return ApiResults.Error(TenancyErrors.InvalidTicket);
            }

            // Another request linked the identity while this one checked the password (it took the same lock): give the ticket back and sign in.
            if (await ProviderLogins.FindAccountAsync(db, ticket.Provider, ticket.IdentityKey, CancellationToken.None) is { } linked)
            {
                await transaction.DisposeAsync();
                return await ProviderSignIn.LinkedAsync(http, db, users, memberships, tokens, clock, linked, ticket, ticketText, binding);
            }

            // The account's row under a share lock to the end: one removed meanwhile is linked to nothing (the answer of an unknown address), and a
            // password reset meanwhile (a reset takes no address lock, but its update waits for this share lock) leaves a hash other than the one
            // that was judged: the login's own answer, the ticket kept, as DELETE /auth/account answers a password changed while it waited.
            var hashes = await db.Database
                .SqlQuery<string?>($"""SELECT "PasswordHash" AS "Value" FROM "AspNetUsers" WHERE "Id" = {account.Id} FOR SHARE""")
                .ToListAsync(CancellationToken.None);
            if (hashes.Count != 1 || !string.Equals(hashes[0], account.PasswordHash, StringComparison.Ordinal))
            {
                await transaction.DisposeAsync();
                return new LoginEndpoint.InvalidCredentialsResult();
            }

            // A membership removed meanwhile is what step 7 answers for an account that has none.
            if (await memberships.ReadAsync(account.Id, CancellationToken.None) is not { } tenant)
            {
                await transaction.DisposeAsync();
                await RecordFailureAsync(audit, "no_company", account, account.Email!, ticket.Provider);
                return ApiResults.Error(TenancyErrors.NoMembership);
            }

            ProviderLogins.Add(db, account.Id, ticket.Provider, ticket.IdentityKey);
            account.EmailConfirmed = true;   // the password proves the account, the provider the mailbox (Settled 13)
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.ProviderLinked,
                ActorUserId = account.Id,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = tenant.CompanyId,
                OrgName = tenant.CompanyName,
                Details = new Dictionary<string, object?> { ["provider"] = ticket.Provider },
            });
            await db.SaveChangesAsync(CancellationToken.None);
            return ProviderSignIn.Session(http, account, tenant, ticket.Provider, transaction, tokens, clock, InvalidCredentialsAsync);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private static Task<IResult> InvalidCredentialsAsync() => Task.FromResult<IResult>(new LoginEndpoint.InvalidCredentialsResult());

    /// <summary>A failed link, as a failed login is written, with the provider.</summary>
    private static Task RecordFailureAsync(AuditLog audit, string reason, ApplicationUser? user, string email, string provider) =>
        audit.WriteAloneAsync(new AuditEntry
        {
            Kind = AuditKinds.LoginFailed,
            SubjectUserId = user?.Id,
            SubjectEmail = user?.Email ?? email,
            Details = new Dictionary<string, object?> { ["reason"] = reason, ["provider"] = provider },
        });
}
