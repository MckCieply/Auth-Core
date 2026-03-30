using System.Text.Json;
using System.Text.Json.Serialization;

namespace Auth.Server.Tenancy;

/// <summary>A role every new company starts with a copy of (spec 0005 → Manifest).</summary>
public sealed record DefaultRole(string Name, IReadOnlyList<string> Permissions);

/// <summary>Whether a visitor may start a company of their own (spec 0009 → Manifest). Invite-only is the default.</summary>
public enum SignupMode
{
    InviteOnly = 0,
    SelfService = 1,
}

/// <summary>The names of the modes in the manifest, in the stored form and in <c>GET /auth/options</c>.</summary>
public static class SignupModes
{
    public const string InviteOnlyName = "invite-only";
    public const string SelfServiceName = "self-service";

    public static string NameOf(SignupMode mode) => mode switch
    {
        SignupMode.InviteOnly => InviteOnlyName,
        SignupMode.SelfService => SelfServiceName,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown sign-up mode."),
    };

    /// <summary>Exactly one of the two names, in lower case. Anything else is not a mode.</summary>
    public static bool TryParse(string? name, out SignupMode mode)
    {
        if (string.Equals(name, SelfServiceName, StringComparison.Ordinal))
        {
            mode = SignupMode.SelfService;
            return true;
        }

        mode = SignupMode.InviteOnly;
        return string.Equals(name, InviteOnlyName, StringComparison.Ordinal);
    }
}

/// <summary>
/// What a product declares in its <c>auth.yaml</c>: its permissions, its default roles and whether a visitor may start a company
/// (<c>signup</c>). Always valid: it is made by <see cref="ManifestParser"/> or is <see cref="BuiltIn"/>, and what is stored in the
/// database was one of those.
/// </summary>
public sealed class Manifest
{
    private static readonly JsonSerializerOptions StoredJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public Manifest(IReadOnlyList<string> permissions, IReadOnlyList<DefaultRole> defaultRoles, SignupMode signup = SignupMode.InviteOnly)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(defaultRoles);

        Permissions = permissions;
        DefaultRoles = defaultRoles;
        Signup = signup;
        Catalog = new PermissionCatalog(permissions);
    }

    /// <summary>With no valid manifest ever stored: the built-in permissions and one default role, <c>admin</c>, holding everything.</summary>
    public static Manifest BuiltIn { get; } = new([], [new DefaultRole("admin", [PermissionCatalog.All])]);

    /// <summary>The permissions the product declares, in the order of the file.</summary>
    public IReadOnlyList<string> Permissions { get; }

    public IReadOnlyList<DefaultRole> DefaultRoles { get; }

    /// <summary>Whether a visitor may start a company; whether sign-up is on also needs the completion address (<c>SignupGate</c>).</summary>
    public SignupMode Signup { get; }

    public PermissionCatalog Catalog { get; }

    /// <summary>
    /// The role of the first admin of a company a visitor starts (spec 0009 → The first admin's role): the first default role, in the
    /// order of the manifest, that holds <c>*</c>; else the first that holds <c>members:manage</c>. The rule of the manifest (spec 0005)
    /// guarantees that one of them exists.
    /// </summary>
    public DefaultRole FirstAdminRole =>
        DefaultRoles.FirstOrDefault(r => r.Permissions.Contains(PermissionCatalog.All, StringComparer.Ordinal))
        ?? DefaultRoles.FirstOrDefault(r => r.Permissions.Contains(PermissionCatalog.MembersManage, StringComparer.Ordinal))
        ?? throw new InvalidOperationException("The manifest has no default role that holds '*' or 'members:manage'.");

    /// <summary>The form kept in the database.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new StoredManifest(
            [.. Permissions], [.. DefaultRoles.Select(r => new StoredRole(r.Name, [.. r.Permissions]))], SignupModes.NameOf(Signup)),
        StoredJson);

    /// <summary>
    /// The manifest kept in the database; <see langword="null"/> when the text is not one (which it never is, unless edited by hand).
    /// A form stored by an earlier version has no <c>signup</c>, and reads as invite-only.
    /// </summary>
    public static Manifest? FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            var stored = JsonSerializer.Deserialize<StoredManifest>(json, StoredJson);
            if (stored is null)
            {
                return null;
            }

            var signup = SignupModes.TryParse(stored.Signup, out var parsed) ? parsed : SignupMode.InviteOnly;
            return new Manifest(stored.Permissions, [.. stored.DefaultRoles.Select(r => new DefaultRole(r.Name, r.Permissions))], signup);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record StoredRole(string Name, [property: JsonPropertyName("permissions")] string[] Permissions);

    private sealed record StoredManifest(string[] Permissions, StoredRole[] DefaultRoles, string? Signup = null);
}
