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

/// <summary>
/// What the completion screen shows before it asks for anything: the address as it was typed, the company name of the request, and whether
/// the address has an account without a company (<see cref="SignupAccount.MayStartCompany"/>), which the link will use as it is.
/// </summary>
public sealed record SignupPreview(string Email, string CompanyName, bool HasAccount);

/// <summary>
/// Previews and completes a sign-up by its link token (spec 0009). Completing is how a visitor becomes the first admin of a company of their
/// own, and it is one transaction: it uses the pending sign-up up, makes the account (email confirmed) — or, for an account that has no company
/// (Amendment 3), takes that account as it is and confirms its email — makes the company with a copy of the default roles and the membership with
/// the first admin's role, and writes the audit rows, and leaves the transaction open for the sign-in (Amendment 2) — or, when anything is refused,
/// changes nothing at all, the pending sign-up included (except that an account found under the address deletes it).
/// </summary>
public sealed class SignupService(
    AuthDbContext db, UserManager<ApplicationUser> users, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations,
    CompanyService companies, ManifestHolder manifest, TimeProvider clock, AuditLog audit)
{
    /// <summary>
    /// The address, the company name and <c>HasAccount</c> of a usable link; the token stays usable. Unknown, used, expired and replaced tokens
    /// are all <see langword="null"/>. It changes nothing.
    /// </summary>
    public async Task<SignupPreview?> PreviewAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var pending = await PendingSignups.FindUsableAsync(db, EmailTokens.HashOf(token), StorableTime.Now(clock), cancellationToken);
        if (pending is null)
        {
            return null;
        }

        var account = await SignupAccounts.FindAsync(db, pending.NormalizedEmail, cancellationToken);
        return new SignupPreview(pending.Email, pending.CompanyName, account?.MayStartCompany(pending.Email) == true);
    }

    /// <summary>
    /// Completes the sign-up as far as the database goes. Refused with <c>invalid_token</c>, with <c>weak_password</c> (the broken rules are
    /// <see cref="SignupCompletion.BrokenRules"/>; the token stays usable), with <c>already_registered</c> (an account that the link may not
    /// use has the address by now: the pending sign-up is deleted) or with <c>invalid_request</c> (no password, and no account under the
    /// address either; the token stays usable). The checks are made in the order: the token, the account, the password; a weak or a missing password
    /// counts only for an address with no account.
    /// The caller has checked <paramref name="companyName"/> against the name rule and <paramref name="password"/>, when there is one, for NUL.
    /// An account under the address that belongs to no company, and whose address is the typed one (<see cref="SignupAccount.MayStartCompany"/>),
    /// is used as it is: the password is neither required nor checked nor set, the email is confirmed, and whatever sessions it still has end as a new account's
    /// do. Every other address chooses a password here.
    /// On success nothing is committed: the account, the company with its roles, the membership and the audit rows are saved inside a transaction
    /// that is handed to the caller in <see cref="SignupCompletion.Transaction"/>, because the completion signs the person in (spec 0009,
    /// Amendment 2) and the sign-in is part of it: if it cannot be written, nothing of the completion stays. Every refusal and every exception
    /// leaves the transaction rolled back (except that an account found under the address deletes the pending sign-up).
    /// </summary>
    public async Task<SignupCompletion> CompleteAsync(string token, string? password, string companyName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
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
            // acceptance comes second sees the membership or the account the first one made and answers `409`.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({address}, 0))", cancellationToken);

            // The token is used up by one statement: of parallel completions with one token one gets the row, and the others, which waited
            // for the lock, find nothing.
            var pending = await PendingSignups.ConsumeAsync(db, hash, now, cancellationToken);
            if (pending is null)
            {
                return Refused(TenancyErrors.InvalidToken);
            }

            // Any account under the address, whatever its spelling or state (the lookup is by the normalised address), read under the lock: a
            // membership an invitation gave it a moment ago is seen.
            var found = await SignupAccounts.FindAsync(db, pending.NormalizedEmail, cancellationToken);
            var usable = found is not null && found.MayStartCompany(pending.Email) ? found : null;

            ApplicationUser account;
            if (usable is null)
            {
                account = new ApplicationUser { UserName = pending.Email, Email = pending.Email, EmailConfirmed = true };

                if (found is not null)
                {
                    // An account the link may not use: one that belongs to a company (it joined one since the mail), or one that only looks like
                    // the typed address. The answer does not depend on the password (a screen that was told "has_account" sent none, and a weak
                    // password is only reported where an account is to be made: spec step 3 and 4). Committed, not rolled back: the pending
                    // sign-up is gone, as the contract says.
                    await transaction.CommitAsync(CancellationToken.None);
                    return Refused(TenancyErrors.AlreadyRegistered);
                }

                // A password that is there is checked before a missing one is refused (the order is the token, the account, the password).
                if (password is not null)
                {
                    var broken = await PasswordRules.BrokenAsync(users, account, password);
                    if (broken.Count > 0)
                    {
                        return new SignupCompletion(TenancyErrors.WeakPassword, broken);
                    }
                }

                if (password is null)
                {
                    return Refused(TenancyErrors.InvalidRequest);   // a new address chooses a password; nothing was changed for good
                }

                // The link came through the mailbox of the address: that proves it, and it is why the email is confirmed and a password is set here.
                AccountSessions.Ensure(await users.CreateAsync(account, password), "Could not set up the account");
            }
            else
            {
                // An account without a company comes back (Amendment 3): it keeps its password and its linked sign-ins, whatever was sent.
                account = await db.Users.SingleAsync(u => u.Id == usable.Id, cancellationToken);
                account.EmailConfirmed = true;
                AccountSessions.Ensure(await users.UpdateAsync(account), "Could not confirm the account");
            }

            // A new account has no sessions or links yet, but its address may have a login streak: streaks do not need an account. An account
            // that comes back has normally no session left (removing a member and deleting a company revoke them), but whatever is left ends
            // here, as it does at an invitation, so that nothing of the lost company refreshes into the new one: defence in depth.
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

            // The person who holds the link is anonymous: no actor. Who, where and what company; never the link or the password. Which path was
            // taken: `existing_account` is there, and true, only for an account that existed.
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.SignupCompleted,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = created.Value,
                OrgName = companyName,
                Details = usable is null ? null : new Dictionary<string, object?> { ["existing_account"] = true },
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
