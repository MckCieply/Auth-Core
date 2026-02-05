using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Auth.Server.Tenancy;

/// <summary>What the acceptance screen shows before it asks for a password.</summary>
public sealed record InvitePreview(string OrgName, string Email, string Role);

/// <summary>
/// Previews and accepts an invitation by its link token (spec 0005 → Invitations — public, Effects). Accepting is how
/// anyone becomes a member, and it is one transaction: it uses the invitation up, makes the account if there is none,
/// sets the password, confirms the email, ends every session of the account and makes it a member with the role of
/// the invitation — or, when anything is refused, changes nothing at all, the invitation included.
/// </summary>
public sealed class InviteAcceptance(
    AuthDbContext db, UserManager<ApplicationUser> users, IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations, TimeProvider clock)
{
    /// <summary>
    /// The company, address and role of a usable invitation; the token stays usable. Unknown, used, expired, cancelled and
    /// replaced tokens are all <c>invalid_token</c>. An address that already belongs to a company is <c>already_member</c>.
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
            select new { invite.Email, invite.NormalizedEmail, CompanyName = company.Name, RoleName = role.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return Outcome.Fail<InvitePreview>(TenancyErrors.InvalidToken);
        }

        return await IsMemberAsync(row.NormalizedEmail, cancellationToken)
            ? Outcome.Fail<InvitePreview>(TenancyErrors.AlreadyMember)
            : Outcome.Ok(new InvitePreview(row.CompanyName, row.Email, row.RoleName));
    }

    /// <summary>
    /// Accepts the invitation. Refused with <c>invalid_token</c>, with <c>weak_password</c> (the broken rules are the value;
    /// the token stays usable) or with <c>already_member</c> (the invitation stays usable until it expires).
    /// </summary>
    public async Task<Outcome<IReadOnlyList<string>>> AcceptAsync(string token, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(password);

        var hash = EmailTokens.HashOf(token);
        var now = StorableTime.Now(clock);

        // Leaving this method without a commit rolls everything back, the use of the invitation included.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The company first, as every change to a company's members does; then the invitation. Of parallel accepts with
        // one token the lock lets one in at a time, and the later one finds the invitation gone.
        var companyId = await db.Invites.Where(i => i.TokenHash == hash && i.ExpiresAt > now)
            .Select(i => (Guid?)i.CompanyId).FirstOrDefaultAsync(cancellationToken);
        if (companyId is not { } company || !await CompanyLock.AcquireAsync(db, company, cancellationToken))
        {
            return Refused(TenancyErrors.InvalidToken);
        }

        // ToListAsync, not SingleOrDefaultAsync: EF must send the statement as it is, not wrapped in a subquery.
        var consumed = await db.Invites
            .FromSql($"""DELETE FROM "Invites" WHERE "TokenHash" = {hash} AND "ExpiresAt" > {now} RETURNING *""")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (consumed.Count != 1)
        {
            return Refused(TenancyErrors.InvalidToken);
        }

        var invite = consumed[0];

        // One address, one acceptance at a time: two invitations of two companies accepted at once must not make the
        // same person a member of both (the tables would allow it; this version does not).
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({invite.NormalizedEmail}, 0))", cancellationToken);

        var user = await users.FindByEmailAsync(invite.Email);
        if (user is not null && await db.Memberships.AnyAsync(m => m.UserId == user.Id, cancellationToken))
        {
            return Refused(TenancyErrors.AlreadyMember);
        }

        var account = user ?? new ApplicationUser { UserName = invite.Email, Email = invite.Email };
        var broken = await PasswordRules.BrokenAsync(users, account, password);
        if (broken.Count > 0)
        {
            return new Outcome<IReadOnlyList<string>>(broken, TenancyErrors.WeakPassword);
        }

        // The link came through the mailbox of the address: that proves it, and it is why a password is set here.
        account.EmailConfirmed = true;
        if (user is null)
        {
            Ensure(await users.CreateAsync(account, password));
        }
        else
        {
            if (await users.HasPasswordAsync(user))
            {
                Ensure(await users.RemovePasswordAsync(user));
            }

            Ensure(await users.AddPasswordAsync(user, password));
            await EndEverySessionAsync(user, cancellationToken);
        }

        db.Memberships.Add(new Membership { UserId = account.Id, CompanyId = invite.CompanyId, RoleId = invite.RoleId, JoinedAt = now });
        await db.SaveChangesAsync(cancellationToken);

        // Not the request's token: a client that goes away now must not leave the outcome open.
        await transaction.CommitAsync(CancellationToken.None);
        return new Outcome<IReadOnlyList<string>>([]);
    }

    /// <summary>What a reset of spec 0004 does to the sessions of an account: every refresh token and its authorization, every other link, the streak.</summary>
    private async Task EndEverySessionAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var subject = user.Id.ToString();
        await tokens.RevokeBySubjectAsync(subject, cancellationToken);
        await authorizations.RevokeBySubjectAsync(subject, cancellationToken);
        await db.EmailTokens.Where(t => t.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);

        // Someone failing logins on purpose must not keep the owner out.
        var identifier = LoginIdentifier.HashOf(user.NormalizedEmail);
        await db.LoginStreaks.Where(s => s.IdentifierHash == identifier).ExecuteDeleteAsync(cancellationToken);
    }

    private Task<bool> IsMemberAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        (from user in db.Users
         join membership in db.Memberships on user.Id equals membership.UserId
         where user.NormalizedEmail == normalizedEmail
         select membership.UserId).AnyAsync(cancellationToken);

    private static Outcome<IReadOnlyList<string>> Refused(string error) => new(null, error);

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            // Codes only: descriptions can echo policy details, and a password must never be logged.
            throw new InvalidOperationException(
                "Could not set up the account: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }
    }
}
