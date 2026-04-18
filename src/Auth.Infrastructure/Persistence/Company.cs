namespace Auth.Infrastructure.Persistence;

/// <summary>
/// A space of the instance: a company (spec 0005 → Concepts) or the personal space of a private account (spec 0010 → Spaces). An id, a name
/// and its kind.
/// </summary>
public sealed class Company
{
    public Guid Id { get; init; }

    public required string Name { get; set; }

    /// <summary>
    /// Left unset (<c>0</c>, EF's sentinel) the database's default applies: an organization. Only a private sign-up sets
    /// <see cref="CompanyKind.Personal"/>.
    /// </summary>
    public CompanyKind Kind { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}
