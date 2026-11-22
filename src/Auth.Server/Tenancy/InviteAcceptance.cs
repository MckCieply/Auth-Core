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
/// What the acceptance screen shows before it asks for anything: the company (and its id), the address, the role, whether the address has an
/// account of any kind (spec 0017) and whether it has a private account (spec 0010: a personal space, also when it holds a company too).
/// </summary>
public sealed record InvitePreview(string OrgName, string Email, string Role, bool HasPrivateAccount, bool HasAccount, Guid OrgId);

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
/// Previews and accepts an invitation by its link token (spec 0005 → Invitations — public, Effects; spec 0010 → Invitations; spec 0017).
/// Accepting is how anyone becomes a member of a company, and it is one transaction: it uses the invitation up, makes the account if there is
/// none (with the password sent) or takes the account there is and keeps everything it has (its password, its other memberships, its sessions,
/// its mail links and login streak; only an unconfirmed email is confirmed), and makes it a member with the role of the invitation; a private
/// account's personal space is deleted only with <c>leave_private_account</c> (the move). When anything is refused nothing changes at all, the
/// invitation included.
/// </summary>
public sealed class InviteAcceptance(
    AuthDbContext db, UserManager<ApplicationUser> users, IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations, TimeProvider clock, AuditLog audit)
{
    /// <summary>
    /// The company, address and role of a usable invitation, and whether the address has a private account; the token stays usable. Unknown,
    /// used, expired, cancelled and replaced tokens are all <c>invalid_token</c>, and so is an invitation whose address matches an existing
    /// account that is not spelled the same (<see cref="EmailInput.IsSameAddress"/>), or that
    /// <see cref="EmailInput.MayStandForAnotherAddress">may stand for another</see>. An account that is a member of the invited company is <c>already_member</c>.
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
            select new { invite.Email, invite.CompanyId, CompanyName = company.Name, RoleName = role.Name })
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

        // Spec 0017: only a member of the invited company is already_member; any other account joins, and its personal space (if any) is told.
        if (user is null)
        {
            return Outcome.Ok(new InvitePreview(row.CompanyName, row.Email, row.RoleName, HasPrivateAccount: false, HasAccount: false, row.CompanyId));
        }

        var memberships = await ReadMembershipsAsync(user.Id, row.CompanyId, cancellationToken);
        return memberships.InInvitedCompany
            ? Outcome.Fail<InvitePreview>(TenancyErrors.AlreadyMember)
            : Outcome.Ok(new InvitePreview(
                row.CompanyName, row.Email, row.RoleName, HasPrivateAccount: memberships.PersonalSpaceId is not null, HasAccount: true, row.CompanyId));
    }

    /// <summary>What an account holds that the acceptance judges: a membership of the invited company, and its personal space, if any.</summary>
    private sealed record Memberships(bool InInvitedCompany, Guid? PersonalSpaceId);

    private async Task<Memberships> ReadMembershipsAsync(Guid userId, Guid companyId, CancellationToken cancellationToken)
    {
        var rows = await (
            from membership in db.Memberships.AsNoTracking()
            join company in db.Companies.AsNoTracking() on membership.CompanyId equals company.Id
            where membership.UserId == userId
            select new { company.Id, company.Kind })
            .ToListAsync(cancellationToken);
        return new Memberships(
            rows.Any(r => r.Id == companyId),
            rows.Where(r => r.Kind == CompanyKind.Personal).Select(r => (Guid?)r.Id).FirstOrDefault());
    }

    /// <summary>
    /// Accepts the invitation, in the order of spec 0017: <c>invalid_token</c>; <c>already_member</c> (an account that is a member of the invited
    /// company; the invitation stays usable); an existing account joins (a <paramref name="password"/> sent is neither used nor judged), and with
    /// <paramref name="leavePrivateAccount"/> and a personal space it moves: the space, its role row and its membership are deleted (without a
    /// personal space the flag is ignored); with no account, <c>invalid_request</c> without a password and <c>weak_password</c> (the broken rules
    /// are the value; the token stays usable). An existing account is used only when its address is the invited one up to ASCII case
    /// (<see cref="EmailInput.IsSameAddress"/>). A join changes nothing the account has: its password, other memberships, personal space, linked
    /// providers, security stamp, sessions, mail links and login streak stay; only an unconfirmed email is confirmed. The locks are those of
    /// <see cref="LockInvitationAsync"/>: the account row is locked and read afresh before its memberships are read, so a removal that took the
    /// account's last membership either finished first (the address then has no account: the new-account path) or waits for this acceptance and
    /// finds the new membership (the account is kept).
    /// </summary>
    public async Task<Outcome<IReadOnlyList<string>>> AcceptAsync(
        string token, string? password, bool leavePrivateAccount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var hash = EmailTokens.HashOf(token);

        // Leaving this method without a commit rolls everything back, the use of the invitation included.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var locked = await LockInvitationAsync(hash, useTicket: null, judgeInvite: null, lockPersonalMembership: leavePrivateAccount, cancellationToken);
        if (locked.Error is { } refusal)
        {
            return Refused(refusal);
        }

        var invite = locked.Invite!;
        var user = locked.User;
        var now = locked.Now;

        if (locked.InInvitedCompany)
        {
            return Refused(TenancyErrors.AlreadyMember);   // nothing changes; the invitation stays usable
        }

        ApplicationUser account;
        Guid? leftPersonalSpace = null;
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

            // The person has no session and no mail link yet; the login streak of the address ends, as at any new account.
            await AccountSessions.EndAllAsync(db, tokens, authorizations, account, cancellationToken);
        }
        else
        {
            // The account keeps everything it has (spec 0017): its password and address, its memberships, its sessions, mail links and login
            // streak. A private account moves on purpose, with leave_private_account (spec 0010): its personal space, the space's role row and
            // its membership are deleted, with everything the product keeps under that space's id left behind (spec 0010, Accepted 16). An
            // account with no personal space ignores the flag and simply joins.
            if (leavePrivateAccount && locked.PersonalSpaceId is { } personal)
            {
                await db.Memberships.Where(m => m.UserId == user.Id && m.CompanyId == personal).ExecuteDeleteAsync(cancellationToken);
                await db.Companies.Where(c => c.Id == personal && c.Kind == CompanyKind.Personal).ExecuteDeleteAsync(cancellationToken);
                leftPersonalSpace = personal;
            }

            account = user;
            if (!account.EmailConfirmed)
            {
                // The entity is tracked and was reloaded under the row lock; SaveChanges writes it. No new security stamp: nothing ends.
                account.EmailConfirmed = true;
            }
        }

        // LastUsedAt = JoinedAt: the next sign-in of the person lands in the company that has just been joined.
        db.Memberships.Add(new Membership { UserId = account.Id, CompanyId = invite.CompanyId, RoleId = invite.RoleId, JoinedAt = now, LastUsedAt = now });
        // The person who holds the link is anonymous: no actor. Who joined, where, and as what; never the link or the password.
        var details = new Dictionary<string, object?>
        {
            ["role"] = await db.CompanyRoles.AsNoTracking().Where(r => r.Id == invite.RoleId).Select(r => r.Name).FirstOrDefaultAsync(cancellationToken),
        };
        if (leftPersonalSpace is { } left)
        {
            details["left_private_account"] = true;
            details["personal_org_id"] = left;
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
    /// What <see cref="LockInvitationAsync"/> found under its locks: the invitation it used up; the account of the invited address (or
    /// <see langword="null"/>, also when the account was removed while this request waited) with its row locked and read afresh; whether that
    /// account is a member of the invited company; its personal space, if it has one; and the time the invitation and the ticket were judged by;
    /// or the refusal.
    /// </summary>
    private sealed record LockedInvitation(
        string? Error, Invite? Invite = null, ApplicationUser? User = null, bool InInvitedCompany = false, Guid? PersonalSpaceId = null, DateTimeOffset Now = default);

    /// <summary>
    /// The locks and reads that both acceptances share, in the order of spec 0010 → Locks (spec 0017): the company lock; the clock, read once the
    /// lock is held (a request that waited judges the invitation, and a ticket, by the time it gets in); then <paramref name="useTicket"/> (a
    /// provider acceptance uses its ticket up here, so that of parallel requests with one ticket the later finds it gone); the invitation, used
    /// up in one statement; <paramref name="judgeInvite"/> (a refusal of the invitation as it was read, before any address lock); the address
    /// lock; the account of the address, which must be spelled the same up to ASCII case (<see cref="EmailInput.IsSameAddress"/>). For an
    /// existing account then: the personal membership row <c>FOR UPDATE</c> when <paramref name="lockPersonalMembership"/> (the move; before
    /// the account row, as a sign-in locks its membership first and then waits for the account row, so the sign-in the move waits for can
    /// finish); the account row <c>FOR UPDATE</c> read afresh, for every account (a removal takes no address lock and can commit between the
    /// read of the account and now; a row that is gone makes the address one with no account); and only then the memberships, so that a removal
    /// that held the account row has either finished (nothing to read) or recounts after this acceptance's new membership. A refusal is the
    /// <see cref="LockedInvitation.Error"/>; the caller rolls the transaction back by leaving without a commit.
    /// </summary>
    private async Task<LockedInvitation> LockInvitationAsync(
        byte[] hash, Func<DateTimeOffset, Task<bool>>? useTicket, Func<Invite, string?>? judgeInvite, bool lockPersonalMembership,
        CancellationToken cancellationToken)
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

        if (user is null)
        {
            return new LockedInvitation(null, invite, null, Now: now);
        }

        if (lockPersonalMembership)
        {
            await db.Database
                .SqlQuery<Guid>($"""
                    SELECT "CompanyId" AS "Value" FROM "Memberships"
                    WHERE "UserId" = {user.Id} AND "CompanyId" IN (SELECT "Id" FROM "Companies" WHERE "Kind" = {(short)CompanyKind.Personal})
                    ORDER BY "CompanyId" FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);
        }

        if (!await LockAccountAsync(user, cancellationToken))
        {
            // The account was there when it was read and its row is gone: a removal committed meanwhile, taking the account with its
            // memberships. The address now belongs to no account, which is the case of an invitation to a new address.
            return new LockedInvitation(null, invite, null, Now: now);
        }

        var memberships = await ReadMembershipsAsync(user.Id, invite.CompanyId, cancellationToken);
        return new LockedInvitation(null, invite, user, memberships.InInvitedCompany, memberships.PersonalSpaceId, now);
    }

    /// <summary>
    /// The space of the account and its kind; <see langword="null"/> when it has none. A company wins over a personal space, should an account
    /// ever hold both. Only <see cref="AcceptThroughProviderAsync"/> still reads it, to keep its answers until the provider path joins too
    /// (Task 4 of plan 0017 removes it).
    /// </summary>
    private Task<Space?> SpaceOfAsync(Guid userId, CancellationToken cancellationToken) =>
        (from membership in db.Memberships.AsNoTracking()
         join company in db.Companies.AsNoTracking() on membership.CompanyId equals company.Id
         where membership.UserId == userId
         orderby company.Kind, membership.JoinedAt   // Organization (1) before Personal (2)
         select new Space(company.Id, company.Kind))
        .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Locks the row of an existing account until the transaction ends and reads it afresh; <see langword="false"/> when it is gone. A removal
    /// of a member takes no address lock, so it can commit between the read of the account and this lock; flows that hold the address lock are
    /// kept out by the lock this acceptance holds. The lock is the last one of the order (company, address, membership rows, account row).
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
                lockPersonalMembership: false,
                cancellationToken);
            if (locked.Error is { } refusal)
            {
                return ProviderRefused(refusal);
            }

            var invite = locked.Invite!;
            var user = locked.User;
            var now = locked.Now;

            // Shim: the provider path keeps its answers until Task 4 of plan 0017 makes it join; it reads the account's space on its own.
            var space = user is null ? null : await SpaceOfAsync(user.Id, cancellationToken);

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
