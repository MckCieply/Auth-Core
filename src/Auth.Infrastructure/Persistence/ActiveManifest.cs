namespace Auth.Infrastructure.Persistence;

/// <summary>
/// The last valid manifest the service has read (spec 0005 → Manifest), kept so that a broken file never leaves the
/// service without one. There is one row, <see cref="TheOnlyId"/>.
/// </summary>
public sealed class ActiveManifest
{
    public const int TheOnlyId = 1;

    public int Id { get; init; } = TheOnlyId;

    /// <summary>The manifest in the form its class writes: the permissions and the default roles.</summary>
    public required string Content { get; set; }

    public required DateTimeOffset StoredAt { get; set; }
}
