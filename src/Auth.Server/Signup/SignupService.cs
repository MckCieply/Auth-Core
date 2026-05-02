using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Providers;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Auth.Server.Signup;

/// <summary>
/// What the completion screen shows before it asks for anything (spec 0010 → preview): the kind, the address as it was typed and, for an
/// organization, the company name of the request.
/// </summary>
public sealed record SignupPreview(SignupKind Kind, string Email, string? CompanyName);

/// <summary>A finish through a provider: the ticket as read, the ticket and its binding as sent, and what the body asked for.</summary>
public sealed record ProviderFinish(ProviderTicket Ticket, string TicketText, string Binding, SignupKind Kind, string? CompanyName);

/// <summary>
/// Previews and completes a sign-up by its link token (specs 0009 and 0010). Completing is one transaction: it uses the pending sign-up up, makes
/// the account (email confirmed) and its space — for an organization the company with a copy of the default roles and the membership with the
/// first admin's role, for a private person the personal space with its role row and the membership — writes the audit rows, and leaves the
/// transaction open for the sign-in (spec 0009, Amendment 2). When anything is refused nothing changes, the pending sign-up included, except that
/// an account found under the address deletes it.
/// </summary>
public sealed class SignupService(
    AuthDbContext db, UserManager<ApplicationUser> users, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations,
    CompanyService companies, ManifestHolder manifest, SignupGate gate, TimeProvider clock, AuditLog audit)
{
    /// <summary>The kind, the address and (for an organization) the company name of a usable link; <see langword="null"/> for any other token. It changes nothing.</summary>
    public async Task<SignupPreview?> PreviewAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var pending = await PendingSignups.FindUsableAsync(db, EmailTokens.HashOf(token), StorableTime.Now(clock), cancellationToken);
        return pending is null
            ? null
            : new SignupPreview(pending.Kind, pending.Email, pending.Kind == SignupKind.Private ? null : pending.CompanyName);
    }

    /// <summary>
    /// Completes the sign-up as far as the database goes, in the order of spec 0010 → Complete: the token (<c>invalid_token</c>); the switch of
    /// its kind (<c>signup_disabled</c>); the members the kind takes, a company name for an organization and none for a private person
    /// (<c>invalid_request</c>; the token stays usable); then, under the address lock and with the token used up, any account under the address
    /// (<c>already_registered</c>, whatever the password; the pending sign-up is deleted); the password (<c>invalid_request</c> when there is none,
    /// <c>weak_password</c> with the rules in <see cref="SignupCompletion.BrokenRules"/>; the token stays usable). The caller has checked
    /// <paramref name="companyName"/>, when there is one, against the name rule and <paramref name="password"/>, when there is one, for NUL.
    /// What is made follows the kind the pending sign-up stores, never the members of the body.
    /// On success nothing is committed: the account, the space and the audit rows are saved inside a transaction that is handed to the caller in
    /// <see cref="SignupCompletion.Transaction"/>, because the completion signs the person in and the sign-in is part of it.
    /// </summary>
    public async Task<SignupCompletion> CompleteAsync(string token, string? password, string? companyName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var hash = EmailTokens.HashOf(token);
        var now = StorableTime.Now(clock);

        // Leaving this method without handing the transaction over rolls everything back, the use of the token included.
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var handedOver = false;
        try
        {
            // Which address, and which kind, is this link for? Read first: the address is what is locked, and only the token knows the kind.
            var link = await db.PendingSignups.AsNoTracking()
                .Where(p => p.TokenHash == hash && p.TokenExpiresAt > now)
                .Select(p => new { p.NormalizedEmail, p.Kind })
                .FirstOrDefaultAsync(cancellationToken);
            if (link is null)
            {
                return Refused(TenancyErrors.InvalidToken);
            }

            if (!gate.IsOnFor(link.Kind))
            {
                return Refused(TenancyErrors.SignupDisabled);   // a request made while its kind was on, completed after it was turned off
            }

            if ((link.Kind == SignupKind.Organization) != (companyName is not null))
            {
                return Refused(TenancyErrors.InvalidRequest);   // an organization names its company; a private person names none
            }

            // One address, one account-maker at a time: the lock invitation acceptance and self-deletion take, so that whichever comes second sees
            // what the first one made.
            await AddressLock.AcquireAsync(db, link.NormalizedEmail, cancellationToken);

            // The token is used up by one statement: of parallel completions with one token one gets the row, and the others, which waited for
            // the lock, find nothing.
            var pending = await PendingSignups.ConsumeAsync(db, hash, now, cancellationToken);
            if (pending is null)
            {
                return Refused(TenancyErrors.InvalidToken);
            }

            // Any account under the address, of either kind and any spelling, read under the lock. Committed, not rolled back: the pending
            // sign-up is gone, as the contract says.
            if (await SignupAccounts.FindAsync(db, pending.NormalizedEmail, cancellationToken) is not null)
            {
                await transaction.CommitAsync(CancellationToken.None);
                return Refused(TenancyErrors.AlreadyRegistered);
            }

            if (password is null)
            {
                return Refused(TenancyErrors.InvalidRequest);   // nothing was changed for good
            }

            var account = new ApplicationUser { UserName = pending.Email, Email = pending.Email, EmailConfirmed = true };
            var broken = await PasswordRules.BrokenAsync(users, account, password);
            if (broken.Count > 0)
            {
                return new SignupCompletion(TenancyErrors.WeakPassword, broken);
            }

            // The link came through the mailbox of the address: that proves it, and it is why the email is confirmed and a password is set here.
            AccountSessions.Ensure(await users.CreateAsync(account, password), "Could not set up the account");

            // A new account has no sessions or links yet, but its address may have a login streak: streaks do not need an account.
            await AccountSessions.EndAllAsync(db, tokens, authorizations, account, cancellationToken);

            var (spaceId, spaceName) = pending.Kind == SignupKind.Private
                ? (PersonalSpaces.Add(db, account.Id, now), PersonalSpaces.Name)
                : await CreateCompanyAsync(account, companyName ?? throw new InvalidOperationException("An organization sign-up has a company name."), now, cancellationToken);

            // The person who holds the link is anonymous: no actor. Who, where and what kind; never the link or the password. A personal space
            // writes no org.* row (spec 0010): its life is in the rows of its account.
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.SignupCompleted,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = spaceId,
                OrgName = spaceName,
                Details = new Dictionary<string, object?> { ["kind"] = SignupKinds.NameOf(pending.Kind) },
            });
            await db.SaveChangesAsync(CancellationToken.None);

            // Not committed: the sign-in is written next, inside this transaction, by whoever receives it, and it commits.
            handedOver = true;
            return new SignupCompletion(Account: account, CompanyId: spaceId, Transaction: transaction);
        }
        finally
        {
            if (!handedOver)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Finishes a sign-up through a provider; the endpoint has judged the body, the ticket, the address and the switch of the kind. Under the
    /// address lock the ticket is used up; any account under the address (of either kind, under any spelling the normaliser folds together), or
    /// the identity a login by now, is <c>already_registered</c> and nothing changes, the ticket included. Otherwise the account is made with the
    /// provider's address and no password, its email confirmed (the provider verified it), the identity becomes its login, the space is made as
    /// at a completion, the pending sign-up of the address is deleted and <c>signup.completed</c> is written with the kind and the provider. As in
    /// <see cref="CompleteAsync"/>, nothing is committed: the transaction is handed over, because the sign-in is part of the finish.
    /// </summary>
    public async Task<SignupCompletion> FinishThroughProviderAsync(ProviderFinish finish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finish);

        var ticket = finish.Ticket;
        var email = ticket.Email ?? throw new InvalidOperationException("A finish needs the address of its ticket.");
        var normalized = users.NormalizeEmail(email);

        // Leaving this method without handing the transaction over rolls everything back, the use of the ticket included.
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var handedOver = false;
        try
        {
            // The lock of the completion and of the acceptance: of those running at once for one address, one makes the account.
            await AddressLock.AcquireAsync(db, normalized, cancellationToken);

            // Read after the wait: a ticket that expired while this request queued is expired.
            var now = StorableTime.Now(clock);
            if (await ProviderTickets.ConsumeAsync(db, finish.TicketText, finish.Binding, ProviderTicketPurpose.SignIn, now, cancellationToken) is null)
            {
                return Refused(TenancyErrors.InvalidTicket);   // used up while this request waited for the lock
            }

            if (await SignupAccounts.FindAsync(db, normalized, cancellationToken) is not null
                || await ProviderLogins.FindAccountAsync(db, ticket.Provider, ticket.IdentityKey, cancellationToken) is not null)
            {
                return Refused(TenancyErrors.AlreadyRegistered);
            }

            var account = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            AccountSessions.Ensure(await users.CreateAsync(account), "Could not set up the account");
            ProviderLogins.Add(db, account.Id, ticket.Provider, ticket.IdentityKey);

            var (spaceId, spaceName) = finish.Kind == SignupKind.Private
                ? (PersonalSpaces.Add(db, account.Id, now), PersonalSpaces.Name)
                : await CreateCompanyAsync(
                    account, finish.CompanyName ?? throw new InvalidOperationException("An organization finish has a company name."), now, cancellationToken);

            await db.PendingSignups.Where(p => p.NormalizedEmail == normalized).ExecuteDeleteAsync(cancellationToken);
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.SignupCompleted,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = spaceId,
                OrgName = spaceName,
                Details = new Dictionary<string, object?> { ["kind"] = SignupKinds.NameOf(finish.Kind), ["provider"] = ticket.Provider },
            });
            await db.SaveChangesAsync(CancellationToken.None);

            handedOver = true;
            return new SignupCompletion(Account: account, CompanyId: spaceId, Transaction: transaction);
        }
        finally
        {
            if (!handedOver)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>The company of an organization sign-up (spec 0009): a copy of the default roles, <c>org.created</c> with <c>via</c> <c>signup</c>, and the membership with the first admin's role.</summary>
    private async Task<(Guid Id, string Name)> CreateCompanyAsync(ApplicationUser account, string companyName, DateTimeOffset now, CancellationToken cancellationToken)
    {
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
        return (created.Value, companyName);
    }

    private static SignupCompletion Refused(string error) => new(error);
}
