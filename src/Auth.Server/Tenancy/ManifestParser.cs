using Auth.Server.Requests;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Auth.Server.Tenancy;

/// <summary>The outcome of reading a manifest: the manifest, or the reason the text is not one.</summary>
public readonly record struct ManifestParseResult(Manifest? Manifest, string? Error);

/// <summary>
/// Reads and validates the product's <c>auth.yaml</c> as a whole (spec 0005 → Manifest). It never throws for bad
/// input: a broken file must not stop the service, so the caller gets the reason instead.
/// </summary>
public static class ManifestParser
{
    /// <summary>The largest manifest read, in bytes. A permission list does not come close.</summary>
    public const int MaxBytes = 64 * 1024;

    private const int MaxPermissions = 500;
    private const int MaxDefaultRoles = 100;

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();

    public static ManifestParseResult Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        try
        {
            if (StructureError(yaml) is { } structure)
            {
                return Fail(structure);
            }

            var document = Yaml.Deserialize<ManifestDocument?>(yaml);
            return document is null ? Fail("the file is empty") : Validate(document);
        }
        catch (Exception exception) when (exception is YamlException or InvalidOperationException or ArgumentException or FormatException)
        {
            // YamlDotNet's scanner reports some malformed input (an unterminated flow sequence) as an
            // InvalidOperationException, not as a YamlException. Either way the file is not a manifest.
            return Fail("the file is not valid YAML for a manifest: " + exception.Message);
        }
    }

    /// <summary>Anchors, aliases and a second document have no use here, and aliases are how a small file becomes a huge one.</summary>
    private static string? StructureError(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        var documents = 0;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case DocumentStart:
                    documents++;
                    break;
                case AnchorAlias:
                    return "aliases are not allowed";
                case NodeEvent { Anchor.IsEmpty: false }:
                    return "anchors are not allowed";
            }
        }

        return documents > 1 ? "the file holds more than one document" : null;
    }

    private static ManifestParseResult Validate(ManifestDocument document)
    {
        var permissions = (document.Permissions ?? []).Select(p => p ?? "").ToList();
        if (permissions.Count > MaxPermissions)
        {
            return Fail($"more than {MaxPermissions} permissions");
        }

        if (permissions.FirstOrDefault(p => !PermissionCatalog.IsWellFormed(p)) is { } malformed)
        {
            return Fail($"permission '{malformed}' must be 1-64 lowercase letters, digits, '_', '-' or ':', starting with a letter");
        }

        permissions = [.. permissions.Distinct(StringComparer.Ordinal)];
        var catalog = new PermissionCatalog(permissions);

        var roles = document.DefaultRoles ?? [];
        if (roles.Count is 0 or > MaxDefaultRoles)
        {
            return Fail($"default_roles must hold between 1 and {MaxDefaultRoles} roles");
        }

        var defaults = new List<DefaultRole>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, listed) in roles)
        {
            if (!NameInput.IsValid(name))
            {
                return Fail($"default role name '{name}' is not a valid name");
            }

            if (!seen.Add(NameInput.Normalize(name)))
            {
                return Fail($"default role '{name}' is declared twice (names are compared without regard to case)");
            }

            var held = (listed ?? []).Select(p => p ?? "").ToList();
            if (held.FirstOrDefault(p => !catalog.Accepts(p)) is { } unknown)
            {
                return Fail($"default role '{name}' holds '{unknown}', which is not in the catalog");
            }

            // `*` already holds the rest: a role that lists it is `*` alone, wherever it is copied.
            defaults.Add(new DefaultRole(
                name,
                held.Contains(PermissionCatalog.All, StringComparer.Ordinal)
                    ? [PermissionCatalog.All]
                    : [.. held.Distinct(StringComparer.Ordinal)]));
        }

        if (!defaults.Any(r => catalog.Expand(r.Permissions).Contains(PermissionCatalog.MembersManage)))
        {
            return Fail($"at least one default role must hold '{PermissionCatalog.MembersManage}' or '*', so that the first admin of a company can manage it");
        }

        return new ManifestParseResult(new Manifest(permissions, defaults), null);
    }

    private static ManifestParseResult Fail(string error) => new(null, error);

    // Public properties because the deserializer sets them; unknown keys are an error, so a typo is not an empty list.
    internal sealed class ManifestDocument
    {
        public List<string?>? Permissions { get; set; }

        // Ordered: the roles keep the order of the file, which the development seeder reads.
        public OrderedDictionary<string, List<string?>?>? DefaultRoles { get; set; }
    }
}
