namespace Auth.Infrastructure.Persistence;

/// <summary>A company of the instance (spec 0005 → Concepts): an id and a name. Created by the operator only.</summary>
public sealed class Company
{
    public Guid Id { get; init; }

    public required string Name { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }
}
