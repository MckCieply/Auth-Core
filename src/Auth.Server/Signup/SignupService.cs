using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Auth.Server.Signup;

/// <summary>What the completion screen shows before it asks for a password: the address as it was typed and the company name of the request.</summary>
public sealed record SignupPreview(string Email, string CompanyName);

/// <summary>
/// Previews and completes a sign-up by its link token (spec 0009). Completing is how a visitor becomes the first admin of a company of their
/// own, and it is one transaction: it uses the pending sign-up up, makes the account (email confirmed), the company with a copy of the
/// default roles and the membership with the first admin's role, and writes the audit rows, and leaves the transaction open for the sign-in
/// (spec 0009, Amendment 2) — or, when anything is refused, changes nothing at all, the pending sign-up included (except that an account
/// found under the address deletes it).
/// </summary>
public sealed class SignupService(
    AuthDbContext db, UserManager<ApplicationUser> users, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations,
    CompanyService companies, ManifestHolder manifest, TimeProvider clock, AuditLog audit)
{
    /// <summary>
    /// The address and the company name of a usable link; the token stays usable. Unknown, used, expired and replaced tokens are all
    /// <see langword="null"/>. It changes nothing.
    /// </summary>
    public async Task<SignupPreview?> PreviewAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var pending = await PendingSignups.FindUsableAsync(db, EmailTokens.HashOf(token), StorableTime.Now(clock), cancellationToken);
        return pending is null ? null : new SignupPreview(pending.Email, pending.CompanyName);
    }

    /// <summary>
    /// Completes the sign-up as far as the database goes. Refused with <c>invalid_token</c>, with <c>weak_password</c> (the broken rules are
    /// <see cref="SignupCompletion.BrokenRules"/>; the token stays usable) or with <c>already_registered</c> (an account has the address by now:
    /// the pending sign-up is deleted). The checks are made in that order. The caller has checked <paramref name="companyName"/> against the
    /// name rule and <paramref name="password"/> for NUL.
    /// On success nothing is committed: the account, the company with its roles, the membership and the audit rows are saved inside a transaction
    /// that is handed to the caller in <see cref="SignupCompletion.Transaction"/>, because the completion signs the person in (spec 0009,
    /// Amendment 2) and the sign-in is part of it: if it cannot be written, nothing of the completion stays. Every refusal and every exception
    /// leaves the transaction rolled back (except that an account found under the address deletes the pending sign-up).
    /// </summary>
    public async Task<SignupCompletion> CompleteAsync(string token, string password, string companyName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(companyName);

        var hash = EmailTokens.HashOf(token);
        var now = StorableTime.Now(clock);

        // Leaving this method without handing the transaction over rolls everything back, the use of the token included.
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var handedOver = false;
        try
        {
            // Which address is this link for? Read first, because the address is what is locked.
            var address = await db.PendingSignups.AsNoTracking()
                .Where(p => p.TokenHash == hash && p.TokenExpiresAt > now)
                .Select(p => p.NormalizedEmail)
                .FirstOrDefaultAsync(cancellationToken);
            if (address is null)
            {
                return Refused(TenancyErrors.InvalidToken);
            }

            // One address, one account-maker at a time: the lock that invitation acceptance takes, so that whichever of a completion and an
            // acceptance comes second sees the account the first one made and answers `409`, instead of failing on the unique user name.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({address}, 0))", cancellationToken);

            // The token is used up by one statement: of parallel completions with one token one gets the row, and the others, which waited
            // for the lock, find nothing.
            var pending = await PendingSignups.ConsumeAsync(db, hash, now, cancellationToken);
            if (pending is null)
            {
                return Refused(TenancyErrors.InvalidToken);
            }

            var account = new ApplicationUser { UserName = pending.Email, Email = pending.Email, EmailConfirmed = true };
            var broken = await PasswordRules.BrokenAsync(users, account, password);
            if (broken.Count > 0)
            {
                return new SignupCompletion(TenancyErrors.WeakPassword, broken);
            }

            // Any account under the address, whatever its spelling or state: the lookup is by the normalised address (any account whose address normalises to it).
            if (await db.Users.AsNoTracking().AnyAsync(u => u.NormalizedEmail == pending.NormalizedEmail, cancellationToken))
            {
                // Committed, not rolled back: the pending sign-up is gone, as the contract says.
                await transaction.CommitAsync(CancellationToken.None);
                return Refused(TenancyErrors.AlreadyRegistered);
            }

            // The link came through the mailbox of the address: that proves it, and it is why the email is confirmed and a password is set here.
            AccountSessions.Ensure(await users.CreateAsync(account, password), "Could not set up the account");
            // A new account has no sessions or links yet, but its address may have a login streak: streaks do not need an account.
            await AccountSessions.EndAllAsync(db, tokens, authorizations, account, cancellationToken);

            var created = await companies.CreateAsync(companyName, cancellationToken, via: "signup");
            if (!created.Succeeded)
            {
                throw new InvalidOperationException($"Could not create the company: {created.Error}.");
            }

            // The role is the company's own copy of the first admin's default role, found by its normalised name.
            var roleKey = NameInput.Normalize(manifest.Current.FirstAdminRole.Name);
            var roleId = await db.CompanyRoles.AsNoTracking()
                .Where(r => r.CompanyId == created.Value && r.NormalizedName == roleKey)
                .Select(r => r.Id)
                .SingleAsync(cancellationToken);
            db.Memberships.Add(new Membership { UserId = account.Id, CompanyId = created.Value, RoleId = roleId, JoinedAt = now });

            // The person who holds the link is anonymous: no actor. Who, where and what company; never the link or the password.
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.SignupCompleted,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = created.Value,
                OrgName = companyName,
            });
            await db.SaveChangesAsync(CancellationToken.None);

            // Not committed: the sign-in is written next, inside this transaction, by whoever receives it, and it commits.
            handedOver = true;
            return new SignupCompletion(Account: account, CompanyId: created.Value, Transaction: transaction);
        }
        finally
        {
            if (!handedOver)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static SignupCompletion Refused(string error) => new(error);
}
