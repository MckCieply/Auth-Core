using System.Globalization;
using System.Text;
using Auth.Server.Providers;
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

    // Spelled out, so that the escape written into a reason is not read as an escape of this file.
    private const char Backslash = (char)0x5C;

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

        // A missing value (`signup:` or `signup: ~`) is the same as a missing key. Any other text must be one of the two names exactly.
        if (!SignupModes.TryParse(document.Signup ?? SignupModes.InviteOnlyName, out var signup))
        {
            return Fail($"signup must be '{SignupModes.InviteOnlyName}' or '{SignupModes.SelfServiceName}'");
        }

        // Spec 0010: the block `private`. `private:` with no value is no block. Its switch is on with `self-service` only, and any other value
        // leaves it off (unlike the top-level `signup`, which refuses a value it does not know). The private role never manages a company.
        var privateSignup = string.Equals(document.Private?.Signup, SignupModes.SelfServiceName, StringComparison.Ordinal);
        var privateListed = (document.Private?.Permissions ?? []).Select(p => p ?? "").ToList();
        if (privateListed.Count > MaxPermissions)
        {
            return Fail($"private.permissions must hold at most {MaxPermissions} permissions");
        }

        if (privateListed.FirstOrDefault(p => p == PermissionCatalog.All || PermissionCatalog.BuiltIn.Contains(p, StringComparer.Ordinal)) is { } managing)
        {
            return Fail($"private.permissions may not hold '{managing}': a private account manages no company");
        }

        if (privateListed.FirstOrDefault(p => !catalog.Accepts(p)) is { } outside)
        {
            return Fail($"private.permissions holds '{outside}', which is not in the catalog");
        }

        List<string> privatePermissions = [.. privateListed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // Spec 0011: the key `providers`. `providers:` with no value is none; every name must be one of the two exactly; duplicates fold and the
        // first place is kept, because the order is the order of the buttons.
        var providersListed = (document.Providers ?? []).Select(p => p ?? "").ToList();
        if (providersListed.FirstOrDefault(p => !ProviderNames.IsName(p)) is { } refused)
        {
            return Fail($"providers holds '{refused}', which is not '{ProviderNames.Google}' or '{ProviderNames.Microsoft}'");
        }

        List<string> providers = [.. providersListed.Distinct(StringComparer.Ordinal)];
        return new ManifestParseResult(new Manifest(permissions, defaults, signup, privateSignup, privatePermissions, providers), null);
    }

    private static ManifestParseResult Fail(string error) => new(null, Printable(error));

    /// <summary>
    /// The reason as it may be logged. It can quote a name from the file, and a name refused because it holds a line break
    /// or an escape character must not forge a log line or drive a terminal: such characters (control and format
    /// characters, line and paragraph separators, lone surrogates) are written as <c>\uXXXX</c> text.
    /// </summary>
    private static string Printable(string text)
    {
        var printable = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsSurrogatePair(text, i))
            {
                printable.Append(c).Append(text[++i]);
            }
            else if (char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate)
            {
                printable.Append(CultureInfo.InvariantCulture, $"{Backslash}u{(int)c:X4}");
            }
            else
            {
                printable.Append(c);
            }
        }

        return printable.ToString();
    }

    // Public properties because the deserializer sets them; unknown keys are an error, so a typo is not an empty list.
    internal sealed class ManifestDocument
    {
        public List<string?>? Permissions { get; set; }

        // `invite-only` or `self-service` (spec 0009); a text so that any other value reaches the check above instead of a binding error.
        public string? Signup { get; set; }

        // The block of spec 0010. Its own keys only: any other is an error, like a typo at the top level.
        public PrivateDocument? Private { get; set; }

        // Spec 0011: the providers a product offers. A list of texts, so that any other value reaches the check above.
        public List<string?>? Providers { get; set; }

        // Ordered: the roles keep the order of the file, which the development seeder reads.
        public OrderedDictionary<string, List<string?>?>? DefaultRoles { get; set; }
    }

    // The block `private` (spec 0010 → Manifest). The switch is a text, so that any value reaches the rule above.
    internal sealed class PrivateDocument
    {
        public string? Signup { get; set; }

        public List<string?>? Permissions { get; set; }
    }
}
