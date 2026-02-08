using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Requests;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// Creates companies (spec 0005 → Concepts): only the operator does, through the CLI, and the development seeder. And renames
/// one, for a member who may manage it.
/// </summary>
public sealed class CompanyService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, TimeProvider clock)
{
    /// <summary>
    /// Creates a company with its own copy of the active default roles. After that its roles are its own: a later
    /// change to the manifest does not touch them.
    /// </summary>
    public async Task<Outcome<Guid>> CreateAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!NameInput.IsValid(name))
        {
            return Outcome.Fail<Guid>(TenancyErrors.InvalidRequest);
        }

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

        await db.SaveChangesAsync(cancellationToken);
        return Outcome.Ok(company.Id);
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

        await db.Companies.Where(c => c.Id == companyId)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, name), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }
}
