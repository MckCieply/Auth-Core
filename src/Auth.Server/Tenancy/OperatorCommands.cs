using Auth.Infrastructure.Persistence;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>One line of <c>list-orgs</c>.</summary>
public sealed record OrgListing(Guid Id, string Name, int Members);

/// <summary>
/// What the operator can do from the command line (spec 0005 → Operator CLI), on strings as typed: it finds the company,
/// the role and the member by what the operator wrote, and then calls the same services as the company API with the
/// operator as the actor. The operator is exempt from safety rule 1 and held to rules 2 and 3, except that
/// <c>--force</c> lifts rule 2.
/// </summary>
public sealed class OperatorCommands(
    AuthDbContext db, CompanyService companies, InvitationService invitations, MemberService members, ILookupNormalizer normalizer)
{
    public Task<Outcome<Guid>> CreateOrgAsync(string name, CancellationToken cancellationToken) =>
        companies.CreateAsync(name, cancellationToken);

    /// <summary>Companies sorted by name, ordinally, with their number of members.</summary>
    public async Task<IReadOnlyList<OrgListing>> ListOrgsAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Companies.AsNoTracking()
            .Select(c => new OrgListing(c.Id, c.Name, db.Memberships.Count(m => m.CompanyId == c.Id)))
            .ToListAsync(cancellationToken);
        return [.. rows.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id)];
    }

    /// <summary>Queues an invitation as the operator: the role is named, not identified, and compared without regard to case.</summary>
    public async Task<Outcome> InviteAsync(string org, string email, string role, CancellationToken cancellationToken)
    {
        if (!IdInput.TryParse(org, out var orgId)
            || email.Length > EmailInput.MaxLength
            || !EmailInput.IsMailbox(email)
            || !EmailInput.TryNormalize(email, normalizer, out var normalized)
            || !NameInput.IsValid(role))
        {
            return Outcome.Fail(TenancyErrors.InvalidRequest);
        }

        var roleKey = NameInput.Normalize(role);
        var roleId = await db.CompanyRoles.AsNoTracking()
            .Where(r => r.CompanyId == orgId && r.NormalizedName == roleKey)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return roleId is { } id
            ? await invitations.SendAsync(Actor.Operator, orgId, email, normalized, id, cancellationToken)
            : Outcome.Fail(TenancyErrors.NotFound);
    }

    /// <summary>Removes a member as the API does and ends their sessions; <paramref name="force"/> overrides <c>last_manager</c>.</summary>
    public async Task<Outcome> RemoveMemberAsync(string org, string email, bool force, CancellationToken cancellationToken)
    {
        if (!IdInput.TryParse(org, out var orgId) || email.Length > EmailInput.MaxLength || !EmailInput.TryNormalize(email, normalizer, out var normalized))
        {
            return Outcome.Fail(TenancyErrors.InvalidRequest);
        }

        var userId = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == normalized)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return userId is { } id
            ? await members.RemoveAsync(Actor.Operator, orgId, id, force, cancellationToken)
            : Outcome.Fail(TenancyErrors.NotFound);
    }
}
