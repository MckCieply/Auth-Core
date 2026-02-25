using System.Text.RegularExpressions;

namespace Auth.Server.Tenancy;

/// <summary>
/// The permissions of an instance (spec 0005 → Concepts): the four built-in ones that guard the company API, plus
/// the ones the product declares in its manifest. A role holds permissions from the catalog, or <c>*</c>, which is
/// all of them, now and after the catalog grows.
/// </summary>
public sealed partial class PermissionCatalog
{
    public const string All = "*";
    public const string MembersManage = "members:manage";
    public const string RolesManage = "roles:manage";
    public const string OrgManage = "org:manage";

    /// <summary>Deletes the company (<c>DELETE /auth/org</c>, spec 0008). In the catalog whether or not the manifest lists it.</summary>
    public const string OrgDelete = "org:delete";

    public static readonly IReadOnlyList<string> BuiltIn = [MembersManage, OrgDelete, OrgManage, RolesManage];

    private readonly HashSet<string> _members;

    public PermissionCatalog(IEnumerable<string> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        _members = new HashSet<string>(declared.Concat(BuiltIn), StringComparer.Ordinal);
        Permissions = [.. _members.Order(StringComparer.Ordinal)];
        Listed = [All, .. Permissions];
    }

    /// <summary>Every permission of the catalog, sorted ordinally; never <c>*</c>.</summary>
    public IReadOnlyList<string> Permissions { get; }

    /// <summary>What a role may hold, as the company API lists it: <c>*</c> first, then the permissions.</summary>
    public IReadOnlyList<string> Listed { get; }

    /// <summary>1–64 characters of lowercase ASCII letters, digits, <c>_</c>, <c>-</c> and <c>:</c>, starting with a letter.</summary>
    public static bool IsWellFormed(string permission)
    {
        ArgumentNullException.ThrowIfNull(permission);

        return PermissionShape().IsMatch(permission);
    }

    /// <summary>Whether a role may hold it: <c>*</c> or a permission of the catalog.</summary>
    public bool Accepts(string permission) =>
        string.Equals(permission, All, StringComparison.Ordinal) || _members.Contains(permission);

    /// <summary>
    /// What a role with these stored permissions grants: <c>*</c> expanded to the whole catalog, names that have left
    /// the catalog dropped, no duplicates, sorted ordinally. Never contains <c>*</c>.
    /// </summary>
    public string[] Expand(IEnumerable<string> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var list = stored as IReadOnlyCollection<string> ?? [.. stored];
        if (list.Contains(All, StringComparer.Ordinal))
        {
            return [.. Permissions];
        }

        return [.. list.Where(_members.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// What the role list shows: the stored permissions that are still in the catalog (<c>*</c> kept as it is),
    /// <c>*</c> first, then ordinally.
    /// </summary>
    public string[] Visible(IEnumerable<string> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return [.. stored.Where(Accepts).Distinct(StringComparer.Ordinal)
            .OrderBy(p => p == All ? 0 : 1)
            .ThenBy(p => p, StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_:-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionShape();
}
