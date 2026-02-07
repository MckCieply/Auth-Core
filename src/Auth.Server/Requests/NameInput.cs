using System.Globalization;
using System.Text;

namespace Auth.Server.Requests;

/// <summary>
/// The rules for the names members and the manifest give to companies and roles (spec 0005 → General rules): at most
/// <see cref="MaxLength"/> characters, no control character, noncharacter, unpaired surrogate or format character, no
/// leading or trailing white space. A name ends up in mails and admin pages, so what it may hold is narrow.
/// </summary>
public static class NameInput
{
    public const int MaxLength = 100;

    public static bool IsValid(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Length is > 0 and <= MaxLength
            && EmailInput.IsWellFormed(name)
            && !HasFormatCharacter(name)
            && string.Equals(name, name.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the name holds a Unicode format character (<see cref="UnicodeCategory.Format"/>: a zero-width space, a
    /// right-to-left override, a byte order mark, a soft hyphen…). Such a character shows as nothing, or turns the text
    /// around, so a role could look like another: <c>admin</c> followed by a zero-width space is a different role that
    /// reads <c>admin</c> in the admin pages, in the <c>roles</c> claim and in an invitation mail. The table is .NET's own,
    /// the same on every host.
    /// </summary>
    private static bool HasFormatCharacter(string name)
    {
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The key a name is compared by: names are unique within a company regardless of case.</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.ToUpperInvariant();
    }
}
