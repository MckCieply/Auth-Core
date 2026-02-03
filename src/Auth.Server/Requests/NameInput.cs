namespace Auth.Server.Requests;

/// <summary>
/// The rules for the names members and the manifest give to companies and roles (spec 0005 → General rules): at most
/// <see cref="MaxLength"/> characters, no control character, noncharacter or unpaired surrogate, no leading or trailing
/// white space. A name ends up in mails and admin pages, so what it may hold is narrow.
/// </summary>
public static class NameInput
{
    public const int MaxLength = 100;

    public static bool IsValid(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Length is > 0 and <= MaxLength
            && EmailInput.IsWellFormed(name)
            && string.Equals(name, name.Trim(), StringComparison.Ordinal);
    }

    /// <summary>The key a name is compared by: names are unique within a company regardless of case.</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.ToUpperInvariant();
    }
}
