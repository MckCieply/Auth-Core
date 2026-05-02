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

    /// <summary>What <c>GET /auth/options</c> says of private sign-up while it is off (spec 0010).</summary>
    public const string OffName = "off";

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
/// (<c>signup</c>) and whether a private person may sign up alone (<c>private</c>, spec 0010). Always valid: it is made by
/// <see cref="ManifestParser"/> or is <see cref="BuiltIn"/>, and what is stored in the database was one of those.
/// </summary>
public sealed class Manifest
{
    // A member that is null is left out: the stored form of a manifest without the block `private` is the one of slice 0009.
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="privateSignup">Whether the block <c>private</c> says <c>signup: self-service</c> (spec 0010).</param>
    /// <param name="privatePermissions">
    /// What the private role grants: catalog permissions, none built in, sorted, distinct. None when absent.
    /// </param>
    /// <param name="providers">The providers the product lists (spec 0011): provider names, the order of the file, distinct. None when absent.</param>
    public Manifest(
        IReadOnlyList<string> permissions, IReadOnlyList<DefaultRole> defaultRoles, SignupMode signup = SignupMode.InviteOnly,
        bool privateSignup = false, IReadOnlyList<string>? privatePermissions = null, IReadOnlyList<string>? providers = null)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(defaultRoles);

        Permissions = permissions;
        DefaultRoles = defaultRoles;
        Signup = signup;
        PrivateSignup = privateSignup;
        PrivatePermissions = privatePermissions ?? [];
        Providers = providers ?? [];
        Catalog = new PermissionCatalog(permissions);
    }

    /// <summary>With no valid manifest ever stored: the built-in permissions and one default role, <c>admin</c>, holding everything.</summary>
    public static Manifest BuiltIn { get; } = new([], [new DefaultRole("admin", [PermissionCatalog.All])]);

    /// <summary>The permissions the product declares, in the order of the file.</summary>
    public IReadOnlyList<string> Permissions { get; }

    public IReadOnlyList<DefaultRole> DefaultRoles { get; }

    /// <summary>Whether a visitor may start a company; whether sign-up is on also needs the completion address (<c>SignupGate</c>).</summary>
    public SignupMode Signup { get; }

    /// <summary>
    /// Whether a private person may sign up (spec 0010 → Manifest, <c>private.signup: self-service</c>); whether private sign-up is on also
    /// needs the completion address (<c>SignupGate</c>).
    /// </summary>
    public bool PrivateSignup { get; }

    /// <summary>
    /// What the private role grants (spec 0010 → Spaces): read at every login and refresh of a private account, so a change reaches
    /// every one of them at its next refresh. Never a built-in permission, never <c>*</c>.
    /// </summary>
    public IReadOnlyList<string> PrivatePermissions { get; }

    /// <summary>
    /// The providers the product lists (spec 0011 → Manifest), in the order of its buttons. Listing is not offering: a provider is offered only
    /// with its keys and the return address too (<c>ProviderCatalog</c>).
    /// </summary>
    public IReadOnlyList<string> Providers { get; }

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
            [.. Permissions],
            [.. DefaultRoles.Select(r => new StoredRole(r.Name, [.. r.Permissions]))],
            SignupModes.NameOf(Signup),
            PrivateSignup || PrivatePermissions.Count > 0
                ? new StoredPrivate(PrivateSignup ? SignupModes.SelfServiceName : SignupModes.OffName, [.. PrivatePermissions])
                : null,
            // Written only when there is one, so that the stored form of a manifest without providers is the one of slice 0010.
            Providers.Count > 0 ? [.. Providers] : null),
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
            // A form stored before spec 0010 has no block: private sign-up off, no permissions.
            var privateSignup = string.Equals(stored.Private?.Signup, SignupModes.SelfServiceName, StringComparison.Ordinal);
            return new Manifest(
                stored.Permissions,
                [.. stored.DefaultRoles.Select(r => new DefaultRole(r.Name, r.Permissions))],
                signup,
                privateSignup,
                stored.Private?.Permissions ?? [],
                stored.Providers ?? []);   // a form stored before spec 0011 has no providers: none is listed
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record StoredRole(string Name, [property: JsonPropertyName("permissions")] string[] Permissions);

    private sealed record StoredPrivate(string? Signup, string[]? Permissions);

    private sealed record StoredManifest(
        string[] Permissions, StoredRole[] DefaultRoles, string? Signup = null, StoredPrivate? Private = null, string[]? Providers = null);
}
