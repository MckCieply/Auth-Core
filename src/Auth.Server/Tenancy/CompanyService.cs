using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Requests;

namespace Auth.Server.Tenancy;

/// <summary>Creates companies (spec 0005 → Concepts): only the operator does, through the CLI, and the development seeder.</summary>
public sealed class CompanyService(AuthDbContext db, ManifestHolder manifest, TimeProvider clock)
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
}
