using System.Text.Json;
using System.Text.Json.Serialization;

namespace Auth.Server.Tenancy;

/// <summary>A role every new company starts with a copy of (spec 0005 → Manifest).</summary>
public sealed record DefaultRole(string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// What a product declares in its <c>auth.yaml</c>: its permissions and its default roles. Always valid: it is made by
/// <see cref="ManifestParser"/> or is <see cref="BuiltIn"/>, and what is stored in the database was one of those.
/// </summary>
public sealed class Manifest
{
    private static readonly JsonSerializerOptions StoredJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public Manifest(IReadOnlyList<string> permissions, IReadOnlyList<DefaultRole> defaultRoles)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(defaultRoles);

        Permissions = permissions;
        DefaultRoles = defaultRoles;
        Catalog = new PermissionCatalog(permissions);
    }

    /// <summary>With no valid manifest ever stored: the built-in permissions and one default role, <c>admin</c>, holding everything.</summary>
    public static Manifest BuiltIn { get; } = new([], [new DefaultRole("admin", [PermissionCatalog.All])]);

    /// <summary>The permissions the product declares, in the order of the file.</summary>
    public IReadOnlyList<string> Permissions { get; }

    public IReadOnlyList<DefaultRole> DefaultRoles { get; }

    public PermissionCatalog Catalog { get; }

    /// <summary>The form kept in the database.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new StoredManifest([.. Permissions], [.. DefaultRoles.Select(r => new StoredRole(r.Name, [.. r.Permissions]))]), StoredJson);

    /// <summary>The manifest kept in the database; <see langword="null"/> when the text is not one (which it never is, unless edited by hand).</summary>
    public static Manifest? FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            var stored = JsonSerializer.Deserialize<StoredManifest>(json, StoredJson);
            return stored is null
                ? null
                : new Manifest(stored.Permissions, [.. stored.DefaultRoles.Select(r => new DefaultRole(r.Name, r.Permissions))]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record StoredRole(string Name, [property: JsonPropertyName("permissions")] string[] Permissions);

    private sealed record StoredManifest(string[] Permissions, StoredRole[] DefaultRoles);
}
