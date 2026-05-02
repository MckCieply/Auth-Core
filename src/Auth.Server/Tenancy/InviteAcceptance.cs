using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Providers;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenIddict.Abstractions;

namespace Auth.Server.Tenancy;

/// <summary>
/// What the acceptance screen shows before it asks for anything: the company, the address, the role, and whether the address has a private
/// account (spec 0010), which accepting would move into the company.
/// </summary>
public sealed record InvitePreview(string OrgName, string Email, string Role, bool HasPrivateAccount);

/// <summary>An acceptance through a provider (spec 0011): the ticket as read, and the ticket and its binding as sent.</summary>
public sealed record ProviderInvite(ProviderTicket Ticket, string TicketText, string Binding);

/// <summary>
/// How <see cref="InviteAcceptance.AcceptThroughProviderAsync"/> ended: a refusal has its <see cref="Error"/>; success has the new account, its
/// company and the transaction that still holds them, not committed (the sign-in is written in it).
/// </summary>
public sealed record ProviderAcceptance(string? Error, ApplicationUser? Account = null, Guid CompanyId = default, IDbContextTransaction? Transaction = null)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Previews and accepts an invitation by its link token (spec 0005 → Invitations — public, Effects; spec 0010 → Invitations). Accepting is how
/// anyone becomes a member of a company, and it is one transaction: it uses the invitation up, makes the account if there is none (with the
/// password sent) or takes the account there is (keeping its password; a private account only with <c>leave_private_account</c>, and its
/// personal space is deleted), confirms the email, ends every session of the account and makes it a member with the role of the invitation —
/// or, when anything is refused, changes nothing at all, the invitation included.
/// </summary>
public sealed class InviteAcceptance(
    AuthDbContext db, UserManager<ApplicationUser> users, IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations, TimeProvider clock, AuditLog audit)
{
    /// <summary>
    /// The company, address and role of a usable invitation, and whether the address has a private account; the token stays usable. Unknown,
    /// used, expired, cancelled and replaced tokens are all <c>invalid_token</c>, and so is an invitation whose address matches an existing
    /// account that is not spelled the same (<see cref="EmailInput.IsSameAddress"/>), or that
    /// <see cref="EmailInput.MayStandForAnotherAddress">may stand for another</see>. An address that belongs to a company is <c>already_member</c>.
    /// </summary>
    public async Task<Outcome<InvitePreview>> PreviewAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var hash = EmailTokens.HashOf(token);
        var now = StorableTime.Now(clock);
        var row = await (
            from invite in db.Invites.AsNoTracking()
            join company in db.Companies.AsNoTracking() on invite.CompanyId equals company.Id
            join role in db.CompanyRoles.AsNoTracking() on invite.RoleId equals role.Id
            where invite.TokenHash == hash && invite.ExpiresAt > now
            select new { invite.Email, CompanyName = company.Name, RoleName = role.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null || EmailInput.MayStandForAnotherAddress(row.Email))
        {
            return Outcome.Fail<InvitePreview>(TenancyErrors.InvalidToken);
        }

        // The account the acceptance would act on. Another spelling that only normalises to the invited address is not the invited mailbox:
        // acceptance refuses it, so the preview does too, and says nothing about that account.
        var user = await users.FindByEmailAsync(row.Email);
        if (user is not null && !EmailInput.IsSameAddress(user.Email, row.Email))
        {
            return Outcome.Fail<InvitePreview>(TenancyErrors.InvalidToken);
        }

        var space = user is null ? null : await SpaceOfAsync(user.Id, cancellationToken);
        return space is { Kind: CompanyKind.Organization }
            ? Outcome.Fail<InvitePreview>(TenancyErrors.AlreadyMember)
            : Outcome.Ok(new InvitePreview(row.CompanyName, row.Email, row.RoleName, space is { Kind: CompanyKind.Personal }));
    }

    /// <summary>
    /// Accepts the invitation, in the order of spec 0010 → Invitations: <c>invalid_token</c>; <c>already_member</c> (an account of a company; the
    /// invitation stays usable); <c>private_account_exists</c> (a private account and <paramref name="leavePrivateAccount"/> not set: nothing
    /// changes); the move of a private account; or, with no account, <c>invalid_request</c> without a password and <c>weak_password</c> (the
    /// broken rules are the value; the token stays usable). An existing account is used only when its address is the invited one up to ASCII
    /// case (<see cref="EmailInput.IsSameAddress"/>), and it keeps its password: a <paramref name="password"/> sent for it is not used.
    /// </summary>
    public async Task<Outcome<IReadOnlyList<string>>> AcceptAsync(
        string token, string? password, bool leavePrivateAccount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var hash = EmailTokens.HashOf(token);

        // Leaving this method without a commit rolls everything back, the use of the invitation included.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var locked = await LockInvitationAsync(hash, useTicket: null, judgeInvite: null, cancellationToken);
        if (locked.Error is { } refusal)
        {
            return Refused(refusal);
        }

        var invite = locked.Invite!;
        var user = locked.User;
        var space = locked.Space;
        var now = locked.Now;

        if (space is { Kind: CompanyKind.Organization })
        {
            return Refused(TenancyErrors.AlreadyMember);
        }

        if (space is { Kind: CompanyKind.Personal } && !leavePrivateAccount)
        {
            return Refused(TenancyErrors.PrivateAccountExists);   // nothing changes; the invitation stays usable
        }

        ApplicationUser account;
        if (user is null)
        {
            if (password is null)
            {
                return Refused(TenancyErrors.InvalidRequest);
            }

            account = new ApplicationUser { UserName = invite.Email, Email = invite.Email, EmailConfirmed = true };
            var broken = await PasswordRules.BrokenAsync(users, account, password);
            if (broken.Count > 0)
            {
                return new Outcome<IReadOnlyList<string>>(broken, TenancyErrors.WeakPassword);
            }

            // The link came through the mailbox of the address: that proves it, and it is why a password is set here.
            Ensure(await users.CreateAsync(account, password));
        }
        else
        {
            // The account keeps its password and its address. A private account moves on purpose (spec 0010): its personal space, the space's
            // role row and its membership are deleted, with everything the product keeps under that space's id left behind (spec 0010, Accepted
            // 16). An account with no space at all, which only a direct change of the database leaves, simply joins.
            if (space is { Kind: CompanyKind.Personal } personal)
            {
                await db.Memberships.Where(m => m.UserId == user.Id && m.CompanyId == personal.Id).ExecuteDeleteAsync(cancellationToken);
                await db.Companies.Where(c => c.Id == personal.Id && c.Kind == CompanyKind.Personal).ExecuteDeleteAsync(cancellationToken);
            }

            account = user;
            account.EmailConfirmed = true;
            // A new stamp: a login that raced this acceptance cannot refresh into the company.
            Ensure(await users.UpdateSecurityStampAsync(account));
        }

        // Every session, mail link and the login streak end, as at any acceptance.
        await AccountSessions.EndAllAsync(db, tokens, authorizations, account, cancellationToken);

        db.Memberships.Add(new Membership { UserId = account.Id, CompanyId = invite.CompanyId, RoleId = invite.RoleId, JoinedAt = now });
        // The person who holds the link is anonymous: no actor. Who joined, where, and as what; never the link or the password.
        var details = new Dictionary<string, object?>
        {
            ["role"] = await db.CompanyRoles.AsNoTracking().Where(r => r.Id == invite.RoleId).Select(r => r.Name).FirstOrDefaultAsync(cancellationToken),
        };
        if (space is { Kind: CompanyKind.Personal } left)
        {
            details["left_private_account"] = true;
            details["personal_org_id"] = left.Id;
        }

        audit.Stage(new AuditEntry
        {
            Kind = AuditKinds.InviteAccepted,
            SubjectUserId = account.Id,
            SubjectEmail = account.Email,
            OrgId = invite.CompanyId,
            OrgName = await audit.CompanyNameAsync(invite.CompanyId, cancellationToken),
            TargetId = invite.Id,
            Details = details,
        });
        await db.SaveChangesAsync(cancellationToken);

        // Not the request's token: a client that goes away now must not leave the outcome open.
        await transaction.CommitAsync(CancellationToken.None);
        return new Outcome<IReadOnlyList<string>>([]);
    }

    /// <summary>
    /// What <see cref="LockInvitationAsync"/> found under its locks: the invitation it used up, the account of the invited address (or
    /// <see langword="null"/>) and that account's space, and the time the invitation and the ticket were judged by; or the refusal.
    /// </summary>
    private sealed record LockedInvitation(string? Error, Invite? Invite = null, ApplicationUser? User = null, Space? Space = null, DateTimeOffset Now = default);

    /// <summary>
    /// The locks and reads that both acceptances share, in the order of spec 0010 → Locks: the company lock; the clock, read once the lock is
    /// held (a request that waited judges the invitation, and a ticket, by the time it gets in); then <paramref name="useTicket"/> (a provider
    /// acceptance uses its ticket up here, so that of parallel requests with one ticket the later finds it gone); the invitation, used up in
    /// one statement; <paramref name="judgeInvite"/> (a refusal of the invitation as it was read, before any address lock); the address lock;
    /// the account of the address, which must be spelled the same up to ASCII case (<see cref="EmailInput.IsSameAddress"/>); and its space,
    /// with the account's row locked when it has none. A refusal is the <see cref="LockedInvitation.Error"/>; the caller rolls the transaction
    /// back by leaving without a commit.
    /// </summary>
    private async Task<LockedInvitation> LockInvitationAsync(
        byte[] hash, Func<DateTimeOffset, Task<bool>>? useTicket, Func<Invite, string?>? judgeInvite, CancellationToken cancellationToken)
    {
        // The company first, as every change to a company's members does; then the invitation. Of parallel accepts with
        // one token the lock lets one in at a time, and the later one finds the invitation gone.
        var found = StorableTime.Now(clock);
        var companyId = await db.Invites.Where(i => i.TokenHash == hash && i.ExpiresAt > found)
            .Select(i => (Guid?)i.CompanyId).FirstOrDefaultAsync(cancellationToken);
        if (companyId is not { } company || !await CompanyLock.AcquireAsync(db, company, cancellationToken))
        {
            return new LockedInvitation(TenancyErrors.InvalidToken);
        }

        var now = StorableTime.Now(clock);
        if (useTicket is not null && !await useTicket(now))
        {
            return new LockedInvitation(TenancyErrors.InvalidTicket);
        }

        // ToListAsync, not SingleOrDefaultAsync: EF must send the statement as it is, not wrapped in a subquery.
        var consumed = await db.Invites
            .FromSql($"""DELETE FROM "Invites" WHERE "TokenHash" = {hash} AND "ExpiresAt" > {now} RETURNING *""")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (consumed.Count != 1)
        {
            return new LockedInvitation(TenancyErrors.InvalidToken);
        }

        var invite = consumed[0];

        // Sending refuses such an address (defence in depth; the check that decides is the one on the account below).
        if (EmailInput.MayStandForAnotherAddress(invite.Email))
        {
            return new LockedInvitation(TenancyErrors.InvalidToken);
        }

        if (judgeInvite?.Invoke(invite) is { } refusal)
        {
            return new LockedInvitation(refusal);
        }

        // One address at a time, after the company lock (spec 0010 → Locks): two invitations accepted at once, a self-deletion and a sign-up
        // completion of the address wait for each other here.
        await AddressLock.AcquireAsync(db, invite.NormalizedEmail, cancellationToken);

        // The lookup matches by the normalised address, which folds look-alikes together (µ and μ, ς and σ…). The link
        // proves the invited mailbox only: an account under another spelling is not touched, and nothing changes.
        var user = await users.FindByEmailAsync(invite.Email);
        if (user is not null && !EmailInput.IsSameAddress(user.Email, invite.Email))
        {
            return new LockedInvitation(TenancyErrors.InvalidToken);
        }

        var space = user is null ? null : await SpaceOfAsync(user.Id, cancellationToken);
        if (user is not null && space is null && !await LockAccountAsync(user, cancellationToken))
        {
            // The account was there when it was read and its space is not: a removal committed between the two reads, taking the account with
            // its membership. The address now belongs to no account, which is the case of an invitation to a new address.
            user = null;
        }

        return new LockedInvitation(null, invite, user, space, now);
    }

    /// <summary>
    /// The space of the account and its kind; <see langword="null"/> when it has none. A company wins over a personal space, should an account
    /// ever hold both (only a direct change of the database leaves that), so that it is refused as any member of a company is.
    /// </summary>
    private Task<Space?> SpaceOfAsync(Guid userId, CancellationToken cancellationToken) =>
        (from membership in db.Memberships.AsNoTracking()
         join company in db.Companies.AsNoTracking() on membership.CompanyId equals company.Id
         where membership.UserId == userId
         orderby company.Kind, membership.JoinedAt   // Organization (1) before Personal (2)
         select new Space(company.Id, company.Kind))
        .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Locks the row of an account that has no space, until the transaction ends, and reads it afresh; <see langword="false"/> when it is gone.
    /// A removal of a member takes no address lock, so it can commit between the read of the account and the read of its space; an account
    /// that really has no space is changed only by flows that hold the address lock, which this one holds. The lock is the last one of the
    /// order (company, address, account row) and is taken by <c>UpdateSecurityStampAsync</c> a few statements later in any case.
    /// </summary>
    private async Task<bool> LockAccountAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var locked = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM "AspNetUsers" WHERE "Id" = {user.Id} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        var entry = db.Entry(user);
        if (locked.Count == 0)
        {
            entry.State = EntityState.Detached;
            return false;
        }

        await entry.ReloadAsync(cancellationToken);   // what a sign-in or a password change wrote meanwhile
        return true;
    }

    /// <summary>
    /// Accepts an invitation through a provider (spec 0011 → exchange, purpose <c>invite</c>; spec 0010's acceptance with the provider in place of
    /// the password), in the order of the contract: the invitation found by the hash the ticket holds (unknown, used, expired, cancelled, replaced
    /// or a look-alike: <c>invalid_token</c>); the ticket's address missing, unverified or not the invited one (<c>address_mismatch</c>,
    /// Decision 9); a company member's address (<c>already_member</c>) or a private account's (<c>private_account_exists</c>: the move of spec 0010
    /// is the screen's own button, Settled 15); the identity a login already (<c>provider_already_linked</c>). Every refusal changes nothing, the
    /// invitation and the ticket included. Otherwise, under the company lock and then the address lock, the ticket is used up first (right after the
    /// company lock, so that of parallel requests with one ticket the later is <c>invalid_ticket</c>, and a ticket that expired while it waited is
    /// refused), then the invitation, the account is made with the invited address and no password, its email confirmed, the identity its login, a member with the invited role,
    /// and <c>invite.accepted</c> names the role and the provider. Nothing is committed: the transaction is handed over, because the acceptance
    /// signs the person in (Settled 16).
    /// </summary>
    public async Task<ProviderAcceptance> AcceptThroughProviderAsync(ProviderInvite request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticket = request.Ticket;
        var hash = ticket.InviteTokenHash ?? throw new InvalidOperationException("An invitation ticket holds the hash of its invitation.");

        // Leaving this method without handing the transaction over rolls everything back, the use of the invitation and of the ticket included.
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var handedOver = false;
        try
        {
            // The ticket is used up right after the company lock, as the link, the finish and the deletion use theirs; then the invitation,
            // which is bound to the address it named: only a verified provider address spelled the same up to ASCII case accepts it.
            var locked = await LockInvitationAsync(
                hash,
                async now => await ProviderTickets.ConsumeAsync(db, request.TicketText, request.Binding, ProviderTicketPurpose.Invite, now, cancellationToken) is not null,
                invited => ticket.Email is null || !ticket.EmailVerified || !EmailInput.IsSameAddress(ticket.Email, invited.Email)
                    ? TenancyErrors.AddressMismatch
                    : null,
                cancellationToken);
            if (locked.Error is { } refusal)
            {
                return ProviderRefused(refusal);
            }

            var invite = locked.Invite!;
            var user = locked.User;
            var space = locked.Space;
            var now = locked.Now;

            if (space is { Kind: CompanyKind.Organization })
            {
                return ProviderRefused(TenancyErrors.AlreadyMember);
            }

            if (space is { Kind: CompanyKind.Personal })
            {
                return ProviderRefused(TenancyErrors.PrivateAccountExists);
            }

            if (user is not null)
            {
                return ProviderRefused(TenancyErrors.AlreadyMember);   // an account with no space: a provider never takes an account over
            }

            if (await ProviderLogins.FindAccountAsync(db, ticket.Provider, ticket.IdentityKey, cancellationToken) is not null)
            {
                return ProviderRefused(TenancyErrors.ProviderAlreadyLinked);
            }

            // The provider proved the invited mailbox: the email is confirmed, and no password is set (spec 0011 → Accounts without a password).
            var account = new ApplicationUser { UserName = invite.Email, Email = invite.Email, EmailConfirmed = true };
            Ensure(await users.CreateAsync(account));
            ProviderLogins.Add(db, account.Id, ticket.Provider, ticket.IdentityKey);
            db.Memberships.Add(new Membership { UserId = account.Id, CompanyId = invite.CompanyId, RoleId = invite.RoleId, JoinedAt = now });
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.InviteAccepted,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = invite.CompanyId,
                OrgName = await audit.CompanyNameAsync(invite.CompanyId, cancellationToken),
                TargetId = invite.Id,
                Details = new Dictionary<string, object?>
                {
                    ["role"] = await db.CompanyRoles.AsNoTracking().Where(r => r.Id == invite.RoleId).Select(r => r.Name).FirstOrDefaultAsync(cancellationToken),
                    ["provider"] = ticket.Provider,
                },
            });
            await db.SaveChangesAsync(CancellationToken.None);

            handedOver = true;
            return new ProviderAcceptance(null, account, invite.CompanyId, transaction);
        }
        finally
        {
            if (!handedOver)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static ProviderAcceptance ProviderRefused(string error) => new(error);

    private static Outcome<IReadOnlyList<string>> Refused(string error) => new(null, error);

    private static void Ensure(IdentityResult result) => AccountSessions.Ensure(result, "Could not set up the account");

    private sealed record Space(Guid Id, CompanyKind Kind);
}
