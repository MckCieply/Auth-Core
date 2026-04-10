namespace Auth.Infrastructure.Persistence;

/// <summary>What a row of the companies table is (spec 0010 → Spaces). The values are stored: never renumber them.</summary>
public enum CompanyKind : short
{
    /// <summary>A company, as in spec 0005. Every row made before spec 0010 is one.</summary>
    Organization = 1,

    /// <summary>The hidden one-person space of a private account.</summary>
    Personal = 2,
}
