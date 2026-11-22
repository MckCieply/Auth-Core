using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Requests;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>The company a signed-in account created, with its creator's role in it.</summary>
public sealed record CreatedCompany(Guid CompanyId, string Name, Guid RoleId, string RoleName);

/// <summary>
/// Creates companies (spec 0005 → Concepts): the operator through the CLI, the development seeder, a self-service sign-up (spec 0009)
/// and a signed-in account that makes another one (spec 0016). And renames one, for a member who may manage it.
/// </summary>
public sealed class CompanyService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, TimeProvider clock, AuditLog audit)
{
    /// <summary>
    /// Creates a company with its own copy of the active default roles. After that its roles are its own: a later
    /// change to the manifest does not touch them.
    /// </summary>
    /// <param name="via">Who made it, for the audit log: <c>cli</c> for the operator, <c>seed</c> for the development seeder, <c>signup</c> for a self-service sign-up.</param>
    public async Task<Outcome<Guid>> CreateAsync(string name, CancellationToken cancellationToken, string via = "cli")
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!NameInput.IsValid(name))
        {
            return Outcome.Fail<Guid>(TenancyErrors.InvalidRequest);
        }

        var company = Stage(name, via, userId: null, email: null);
        await db.SaveChangesAsync(cancellationToken);
        return Outcome.Ok(company.Id);
    }

    /// <summary>
    /// Creates a company for a signed-in account (spec 0016, <c>POST /auth/orgs</c>): the company with its copy of the default roles,
    /// the account's membership with the first admin's role of the new company, and <c>org.created</c> with <c>via</c> <c>api</c>.
    /// It needs the caller's transaction, which holds the address lock and the account row, and saves into it but does not commit,
    /// so that the caller commits or rolls back the company, the membership and the row together. A name that breaks the rules is
    /// <c>invalid_request</c> and stages nothing.
    /// </summary>
    public async Task<Outcome<CreatedCompany>> CreateForAccountAsync(Guid userId, string? email, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Creating a company for an account needs the caller's transaction: the company, the membership and the audit row must commit together.");
        }

        if (!NameInput.IsValid(name))
        {
            return Outcome.Fail<CreatedCompany>(TenancyErrors.InvalidRequest);
        }

        var company = Stage(name, "api", userId, email);

        await db.SaveChangesAsync(cancellationToken);

        // The role is the company's own copy of the first admin's default role, found by its normalised name.
        var roleKey = NameInput.Normalize(manifest.Current.FirstAdminRole.Name);
        var role = await db.CompanyRoles.AsNoTracking()
            .Where(r => r.CompanyId == company.Id && r.NormalizedName == roleKey)
            .Select(r => new { r.Id, r.Name })
            .SingleAsync(cancellationToken);
        db.Memberships.Add(new Membership
        {
            UserId = userId,
            CompanyId = company.Id,
            RoleId = role.Id,
            JoinedAt = StorableTime.Now(clock),
            LastUsedAt = null,
        });
        await db.SaveChangesAsync(cancellationToken);
        return Outcome.Ok(new CreatedCompany(company.Id, name, role.Id, role.Name));
    }

    /// <summary>
    /// Adds the company, one role per default role and the <c>org.created</c> row to the context; the caller saves. The row is written
    /// by the same save as the company and its roles. The creator, when there is one, is the actor and the subject of the row.
    /// </summary>
    private Company Stage(string name, string via, Guid? userId, string? email)
    {
        var company = new Company { Id = Guid.NewGuid(), Name = name, CreatedAt = StorableTime.Now(clock) };
        db.Companies.Add(company);
        foreach (var role in manifest.Current.DefaultRoles)
        {
            db.CompanyRoles.Add(new CompanyRole
            {
                CompanyId = company.Id,
                Name = role.Name,
                NormalizedName = NameInput.Normalize(role.Name),
                Permissions = [.. role.Permissions],
            });
        }

        audit.Stage(new AuditEntry
        {
            Kind = AuditKinds.OrgCreated,
            OrgId = company.Id,
            OrgName = name,
            ActorUserId = userId,
            SubjectUserId = userId,
            SubjectEmail = email,
            Details = new Dictionary<string, object?> { ["via"] = via },
        });
        return company;
    }

    /// <summary>
    /// Renames the company (<c>PATCH /auth/org</c>). Like every other write to a company it runs in one transaction that
    /// begins by locking the company and reading the actor again (see <see cref="CompanyGuard"/>): a member who lost
    /// <c>org:manage</c> after the endpoint checked them gets <c>permissions_changed</c>. A name that breaks the rules is
    /// <c>invalid_request</c>.
    /// </summary>
    public async Task<Outcome> RenameAsync(Actor actor, Guid companyId, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(name);

        if (!NameInput.IsValid(name))
        {
            return Outcome.Fail(TenancyErrors.InvalidRequest);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.OrgManage, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
        }

        var previous = await audit.CompanyNameAsync(companyId, cancellationToken);
        await db.Companies.Where(c => c.Id == companyId)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, name), cancellationToken);
        audit.Stage(
            new AuditEntry
            {
                Kind = AuditKinds.OrgRenamed,
                OrgId = companyId,
                OrgName = name,
                Details = new Dictionary<string, object?> { ["from"] = previous, ["to"] = name },
            },
            entered.Value);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }
}
