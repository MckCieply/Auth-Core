using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Seeding;

/// <summary>
/// Creates the development users and their company at host startup so there is someone to log in as. It is a no-op
/// outside the <c>Development</c> environment. The first user (<see cref="EmailKey"/>, <see cref="PasswordKey"/>) has a
/// confirmed email and is the <c>admin</c> of the development company (<see cref="OrgNameKey"/>, created with the
/// default roles of the active manifest). The optional second one (<see cref="UnverifiedEmailKey"/>,
/// <see cref="UnverifiedPasswordKey"/>) has not confirmed its email: until self-service sign-up exists it is the only
/// way to have an account that needs verification (spec 0004, Decision 2), and it is a member with a role that does
/// not manage members (the first role of the company, by name, that holds neither <c>members:manage</c> nor <c>*</c>;
/// else a new empty role <c>member</c>). Each user is created only when both of its settings are present. An optional
/// second company (<see cref="SecondOrgNameKey"/>, spec 0015) is made the same way, and the first user is a plain member of
/// it (the first default role, in the order of the manifest, that holds neither <c>members:manage</c> nor <c>*</c>; else an
/// empty <c>member</c>). Seeding is idempotent, adds a membership in each seeded company a user lacks, and never resets the
/// password of an existing user or removes a membership.
/// </summary>
public static partial class DevUserSeeder
{
    public const string EmailKey = "Auth:DevSeed:Email";
    public const string PasswordKey = "Auth:DevSeed:Password";
    public const string UnverifiedEmailKey = "Auth:DevSeed:UnverifiedEmail";
    public const string UnverifiedPasswordKey = "Auth:DevSeed:UnverifiedPassword";
    public const string OrgNameKey = "Auth:DevSeed:OrgName";
    public const string SecondOrgNameKey = "Auth:DevSeed:SecondOrgName";

    public const string DefaultOrgName = "Development";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            return;
        }

        var configuration = services.GetRequiredService<IConfiguration>();
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DevUserSeeder));

        var first = await SeedOneAsync(users, logger, configuration[EmailKey], configuration[PasswordKey], emailConfirmed: true, ct);
        var second = await SeedOneAsync(users, logger, configuration[UnverifiedEmailKey], configuration[UnverifiedPasswordKey], emailConfirmed: false, ct);
        if (first is null && second is null)
        {
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var companies = scope.ServiceProvider.GetRequiredService<CompanyService>();
        var configuredName = configuration[OrgNameKey];
        var firstName = string.IsNullOrWhiteSpace(configuredName) ? DefaultOrgName : configuredName;
        var companyId = await EnsureCompanyAsync(db, companies, logger, firstName, ct);
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var manifest = scope.ServiceProvider.GetRequiredService<ManifestHolder>().Current;
        var roles = await db.CompanyRoles.Where(r => r.CompanyId == companyId).ToListAsync(ct);

        // The company's copy of the first default role, in the order of the manifest, that manages members.
        CompanyRole? FirstManagingDefaultRole() => manifest.DefaultRoles
            .Where(d => manifest.Catalog.Expand(d.Permissions).Contains(PermissionCatalog.MembersManage))
            .Select(d => roles.FirstOrDefault(r => r.NormalizedName == NameInput.Normalize(d.Name)))
            .FirstOrDefault(r => r is not null);

        if (first is not null)
        {
            var admin = roles.FirstOrDefault(r => r.NormalizedName == NameInput.Normalize("admin")) ?? FirstManagingDefaultRole()
                ?? throw new InvalidOperationException("The development company has no role that manages members.");
            await EnsureMemberAsync(db, clock, first, companyId, admin, ct);
        }

        if (second is not null)
        {
            // Read from the database, not from the manifest (a role of the manifest may not be the company's any more): the first
            // role of the company, by name, that holds neither members:manage nor *. The order the roles were stored in cannot be
            // read back, so the name decides.
            var plain = roles
                .OrderBy(r => r.Name, StringComparer.Ordinal)
                .FirstOrDefault(r => !r.Permissions.Contains(PermissionCatalog.All, StringComparer.Ordinal)
                    && !r.Permissions.Contains(PermissionCatalog.MembersManage, StringComparer.Ordinal));
            if (plain is null)
            {
                // Every default role manages members: the second user still must not.
                plain = new CompanyRole { CompanyId = companyId, Name = "member", NormalizedName = NameInput.Normalize("member"), Permissions = [] };
                db.CompanyRoles.Add(plain);
                await db.SaveChangesAsync(ct);
            }

            await EnsureMemberAsync(db, clock, second, companyId, plain, ct);
        }

        // A second company, when one is named and is not the first: the first user is a member of it with no right to manage.
        var secondName = configuration[SecondOrgNameKey];
        if (first is not null && !string.IsNullOrWhiteSpace(secondName) && !string.Equals(secondName, firstName, StringComparison.Ordinal))
        {
            var secondCompanyId = await EnsureCompanyAsync(db, companies, logger, secondName, ct);
            var plainRole = await EnsurePlainRoleAsync(db, manifest, secondCompanyId, ct);
            await EnsureMemberAsync(db, clock, first, secondCompanyId, plainRole, ct);
        }
    }

    /// <summary>
    /// The role of a company a plain member takes: the company's copy of the first default role, in the order of the manifest,
    /// that holds neither <c>*</c> nor <c>members:manage</c>; else the role <c>member</c> with no permission (made once).
    /// </summary>
    private static async Task<CompanyRole> EnsurePlainRoleAsync(AuthDbContext db, Manifest manifest, Guid companyId, CancellationToken ct)
    {
        var roles = await db.CompanyRoles.Where(r => r.CompanyId == companyId).ToListAsync(ct);
        var plain = manifest.DefaultRoles
            .Where(d =>
            {
                // Expand turns * into the whole catalog, so a role with * is already caught through members:manage.
                return !manifest.Catalog.Expand(d.Permissions).Contains(PermissionCatalog.MembersManage, StringComparer.Ordinal);
            })
            .Select(d => roles.FirstOrDefault(r => r.NormalizedName == NameInput.Normalize(d.Name)))
            .FirstOrDefault(r => r is not null);
        if (plain is not null)
        {
            return plain;
        }

        // A restart finds the role it made before (the name is unique in a company).
        var member = roles.FirstOrDefault(r => r.NormalizedName == NameInput.Normalize("member"));
        if (member is not null)
        {
            return member;
        }

        member = new CompanyRole { CompanyId = companyId, Name = "member", NormalizedName = NameInput.Normalize("member"), Permissions = [] };
        db.CompanyRoles.Add(member);
        await db.SaveChangesAsync(ct);
        return member;
    }

    private static async Task<ApplicationUser?> SeedOneAsync(
        UserManager<ApplicationUser> users, ILogger logger, string? email, string? password, bool emailConfirmed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();
        if (await users.FindByEmailAsync(email) is { } existing)
        {
            return existing;
        }

        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = emailConfirmed };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            // Codes only: descriptions can echo policy details, and the password must never be logged.
            throw new InvalidOperationException(
                "Could not seed the development user: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }

        LogSeeded(logger, email);
        return user;
    }

    private static async Task<Guid> EnsureCompanyAsync(
        AuthDbContext db, CompanyService companies, ILogger logger, string name, CancellationToken ct)
    {
        // A personal space is named "private" (spec 0010): only a company is the development company.
        var existing = await db.Companies.AsNoTracking().OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(c => c.Name == name && c.Kind == CompanyKind.Organization, ct);
        if (existing is not null)
        {
            return existing.Id;
        }

        var created = await companies.CreateAsync(name, ct, via: "seed");
        if (!created.Succeeded)
        {
            throw new InvalidOperationException($"Could not seed the development company: {created.Error}.");
        }

        LogSeededCompany(logger, name);
        return created.Value;
    }

    private static async Task EnsureMemberAsync(
        AuthDbContext db, TimeProvider clock, ApplicationUser user, Guid companyId, CompanyRole role, CancellationToken ct)
    {
        // In the company it seeds, not in any: a user may belong to several, and one removed from this one comes back to it.
        if (await db.Memberships.AnyAsync(m => m.UserId == user.Id && m.CompanyId == companyId, ct))
        {
            return;
        }

        db.Memberships.Add(new Membership { UserId = user.Id, CompanyId = companyId, RoleId = role.Id, JoinedAt = StorableTime.Now(clock) });
        await db.SaveChangesAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded development user {Email}")]
    private static partial void LogSeeded(ILogger logger, string email);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded development company {Name}")]
    private static partial void LogSeededCompany(ILogger logger, string name);
}
