namespace Auth.Infrastructure.Persistence;

/// <summary>
/// A role of one company: a name and a set of permissions from the catalog, or <c>*</c>, which stands for the whole
/// catalog. The permissions are stored as they were given; what they grant today is worked out against the catalog.
/// </summary>
public sealed class CompanyRole
{
    public Guid Id { get; init; }

    public required Guid CompanyId { get; init; }

    public required string Name { get; set; }

    /// <summary>The name in upper case: names are unique within a company regardless of case.</summary>
    public required string NormalizedName { get; set; }

    public required string[] Permissions { get; set; }
}
