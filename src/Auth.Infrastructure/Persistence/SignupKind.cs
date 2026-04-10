namespace Auth.Infrastructure.Persistence;

/// <summary>What a pending sign-up asks for (spec 0010 → Pending sign-ups). The values are stored: never renumber them.</summary>
public enum SignupKind : short
{
    /// <summary>A company of one's own, as in spec 0009. Every row made before spec 0010 is one.</summary>
    Organization = 1,

    /// <summary>A private account, with a personal space.</summary>
    Private = 2,
}
