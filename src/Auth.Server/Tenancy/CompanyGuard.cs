using Auth.Infrastructure.Persistence;

namespace Auth.Server.Tenancy;

/// <summary>
/// The first step of everything that changes a company's members, roles or invitations: lock the company, and — for a
/// member — read their membership and role again under the lock. The company API checked the caller against the
/// database before it called the service, but another request may have demoted or removed them since; what they are
/// allowed to do is what the database says once nobody else can change the company.
/// </summary>
public sealed class CompanyGuard(AuthDbContext db, MembershipReader memberships)
{
    /// <summary>
    /// Locks the company (<see cref="CompanyLock"/>; the caller has opened the transaction). For a member, returns the
    /// actor as the database now has them, or <c>permissions_changed</c> when they are no longer a member of this company
    /// or no longer hold <paramref name="permission"/>. The operator needs no check.
    /// </summary>
    public async Task<Outcome<Actor>> EnterAsync(Actor actor, Guid companyId, string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(permission);

        if (!await CompanyLock.AcquireAsync(db, companyId, cancellationToken))
        {
            return Outcome.Fail<Actor>(TenancyErrors.NotFound);
        }

        if (actor.Member is null)
        {
            return Outcome.Ok(actor);
        }

        var current = await memberships.ReadAsync(actor.Member.UserId, cancellationToken);
        return current is null || current.CompanyId != companyId || !current.Holds(permission)
            ? Outcome.Fail<Actor>(TenancyErrors.PermissionsChanged)
            : Outcome.Ok(Actor.Of(current));
    }
}
