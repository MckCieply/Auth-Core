using Auth.Infrastructure.Persistence;
using Auth.Server.Requests;

namespace Auth.Server.Tenancy;

/// <summary>
/// The hidden one-person space of a private account (spec 0010 → Spaces): a row of the companies table of kind
/// <see cref="CompanyKind.Personal"/> named <see cref="Name"/>, one role row named <see cref="RoleName"/> whose stored list is empty (its
/// permissions come from the manifest when the membership is read), and exactly one member.
/// </summary>
public static class PersonalSpaces
{
    public const string Name = "private";
    public const string RoleName = "private";

    /// <summary>Adds the space, its role row and the membership of <paramref name="userId"/> to the context; the caller saves. Returns the id of the space.</summary>
    public static Guid Add(AuthDbContext db, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);

        var space = new Company { Id = Guid.NewGuid(), Name = Name, Kind = CompanyKind.Personal, CreatedAt = now };
        var role = new CompanyRole
        {
            Id = Guid.NewGuid(),
            CompanyId = space.Id,
            Name = RoleName,
            NormalizedName = NameInput.Normalize(RoleName),
            Permissions = [],
        };
        db.Companies.Add(space);
        db.CompanyRoles.Add(role);
        db.Memberships.Add(new Membership { UserId = userId, CompanyId = space.Id, RoleId = role.Id, JoinedAt = now });
        return space.Id;
    }
}
