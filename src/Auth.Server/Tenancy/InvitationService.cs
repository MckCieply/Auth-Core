using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Auth.Server.Tenancy;

/// <summary>
/// Sends, lists, resends and cancels the invitations of a company (spec 0005 → Invitations). The company API and the
/// operator commands both call it, so the rules are the same on both. Everything that changes anything runs in one
/// transaction that begins by locking the company (see <see cref="CompanyLock"/>).
/// </summary>
public sealed class InvitationService(AuthDbContext db, ManifestHolder manifest, TimeProvider clock)
{
    /// <summary>
    /// Invites an address to the company with a role: makes the invitation, applies the mail limit of the company and
    /// the address, and queues the mail — or refuses, and then changes nothing. Whether the address has an account, or
    /// belongs to another company, is not looked at: the answer is the same for every address outside this company.
    /// </summary>
    /// <param name="email">The address as typed, which the account will be created with.</param>
    /// <param name="normalizedEmail">The address as the account lookup normalises it.</param>
    public async Task<Outcome> SendAsync(
        Actor actor, Guid companyId, string email, string normalizedEmail, Guid roleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        var now = StorableTime.Now(clock);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await CompanyLock.AcquireAsync(db, companyId, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        var role = await db.CompanyRoles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId && r.CompanyId == companyId, cancellationToken);
        if (role is null)
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        if (!actor.MayGrant(role.Permissions, manifest.Current.Catalog))
        {
            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
        }

        var isMember = await (
            from user in db.Users
            join membership in db.Memberships on user.Id equals membership.UserId
            where user.NormalizedEmail == normalizedEmail && membership.CompanyId == companyId
            select membership.UserId).AnyAsync(cancellationToken);
        if (isMember)
        {
            return Outcome.Fail(TenancyErrors.AlreadyInOrg);
        }

        // An expired invitation is not pending: it makes room for the new one. A pending one stops the insert.
        await db.Invites
            .Where(i => i.CompanyId == companyId && i.NormalizedEmail == normalizedEmail && i.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);

        var inviteId = Guid.NewGuid();
        var expiresAt = now + InviteTokens.Lifetime;
        var invitedBy = actor.UserId;
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "Invites" ("Id", "CompanyId", "Email", "NormalizedEmail", "RoleId", "InvitedBy", "InvitedAt", "TokenHash", "ExpiresAt")
            VALUES ({inviteId}, {companyId}, {email}, {normalizedEmail}, {roleId}, {invitedBy}, {now}, NULL, {expiresAt})
            ON CONFLICT ("CompanyId", "NormalizedEmail") DO NOTHING
            """,
            cancellationToken);
        if (inserted == 0)
        {
            return Outcome.Fail(TenancyErrors.InvitePending);
        }

        return await QueueMailAsync(companyId, inviteId, normalizedEmail, now, transaction, cancellationToken);
    }

    /// <summary>The company's invitations that have not expired, sorted by address, ordinally.</summary>
    public async Task<IReadOnlyList<InviteItem>> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var now = StorableTime.Now(clock);
        var rows = await (
            from invite in db.Invites.AsNoTracking()
            join role in db.CompanyRoles.AsNoTracking() on invite.RoleId equals role.Id
            where invite.CompanyId == companyId && invite.ExpiresAt > now
            select new { invite.Id, invite.Email, RoleId = role.Id, RoleName = role.Name, invite.InvitedAt, invite.ExpiresAt })
            .ToListAsync(cancellationToken);

        return [.. rows
            .OrderBy(r => r.Email, StringComparer.Ordinal)
            .Select(r => new InviteItem(r.Id, r.Email, new RoleRef(r.RoleId, r.RoleName), r.InvitedAt.UtcDateTime, r.ExpiresAt.UtcDateTime))];
    }

    /// <summary>
    /// Queues a new mail for a pending invitation, subject to the same mail limit as sending. When the mail is composed it
    /// carries a new token, and the earlier link stops working; the seven days start again.
    /// </summary>
    public async Task<Outcome> ResendAsync(Guid companyId, Guid inviteId, CancellationToken cancellationToken)
    {
        var now = StorableTime.Now(clock);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await CompanyLock.AcquireAsync(db, companyId, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        var invite = await db.Invites.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == inviteId && i.CompanyId == companyId && i.ExpiresAt > now, cancellationToken);
        return invite is null
            ? Outcome.Fail(TenancyErrors.NotFound)
            : await QueueMailAsync(companyId, invite.Id, invite.NormalizedEmail, now, transaction, cancellationToken);
    }

    /// <summary>Makes the link of a pending invitation stop working at once, and removes it from the list. Not subject to the mail limit.</summary>
    public async Task<Outcome> CancelAsync(Guid companyId, Guid inviteId, CancellationToken cancellationToken)
    {
        var now = StorableTime.Now(clock);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await CompanyLock.AcquireAsync(db, companyId, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        var removed = await db.Invites
            .Where(i => i.Id == inviteId && i.CompanyId == companyId && i.ExpiresAt > now)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed == 0 ? Outcome.Fail(TenancyErrors.NotFound) : Outcome.Done;
    }

    /// <summary>Applies the mail limit of the company and address and, if it lets the mail through, queues it and commits.</summary>
    private async Task<Outcome> QueueMailAsync(
        Guid companyId, Guid inviteId, string normalizedEmail, DateTimeOffset now,
        IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        var decision = await MailLimits.RegisterAsync(
            db, InviteTokens.LimitIdentifierOf(companyId, normalizedEmail), MailKind.Invitation, now, cancellationToken);
        if (!decision.Allowed)
        {
            // Leaving without a commit takes the invitation just made back with it.
            return Outcome.Limited(decision.RetryAfter);
        }

        db.MailRequests.Add(new MailRequest
        {
            Kind = MailKind.Invitation,
            NormalizedEmail = normalizedEmail,
            InviteId = inviteId,
            RequestedAt = now,
            NextAttemptAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }
}
