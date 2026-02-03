namespace Auth.Server.Tenancy;

/// <summary>The active manifest, and why it is not the one the product's file describes, when it is not.</summary>
public sealed record ManifestState(Manifest Manifest, string? DegradedReason)
{
    public bool IsDegraded => DegradedReason is not null;
}

/// <summary>
/// Holds the active manifest of this process (spec 0005 → Manifest). It is set once, at startup, before the host
/// serves anything, and read on every login, refresh and company API call: the catalog is the manifest's, so a change
/// to the file takes effect with a restart. Until something is set the built-in manifest applies.
/// </summary>
public sealed class ManifestHolder
{
    private volatile ManifestState _state = new(Manifest.BuiltIn, null);

    public ManifestState State => _state;

    public Manifest Current => _state.Manifest;

    /// <param name="degradedReason">Why the file was not used; <see langword="null"/> when it was.</param>
    public void Set(Manifest manifest, string? degradedReason)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        _state = new ManifestState(manifest, degradedReason);
    }
}
