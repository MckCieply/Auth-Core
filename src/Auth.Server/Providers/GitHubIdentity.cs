using System.Globalization;
using System.Text.Json;

namespace Auth.Server.Providers;

/// <summary>
/// What the GitHub reader read (spec 0012 → The identity): the key (<c>id</c> of <c>/user</c>, a positive integer, as its decimal text), the primary
/// address of <c>/user/emails</c> or none, and whether GitHub verified it. The invitation rule is applied after it, by the callback.
/// </summary>
public sealed record GitHubAccount(string Key, string? Email, bool EmailVerified);

/// <summary>The rules of GitHub's two documents. Anything that is not the documented JSON is <see langword="null"/>: the callback answers <c>provider_error</c>.</summary>
public static class GitHubIdentity
{
    /// <param name="user">The answer of <c>GET /user</c>: an object whose <c>id</c> is a positive integer. <c>login</c> and <c>email</c> are never read.</param>
    /// <param name="emails">The answer of <c>GET /user/emails?per_page=100</c>: an array of objects with <c>email</c> (a string), <c>primary</c> and
    /// <c>verified</c> (booleans). Exactly one entry with <c>primary</c> <c>true</c> is the address; none or several is no address.</param>
    public static GitHubAccount? Read(JsonElement user, JsonElement emails)
    {
        if (user.ValueKind != JsonValueKind.Object
            || !user.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.Number
            || !id.TryGetInt64(out var number)
            || number <= 0
            || emails.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? primary = null;
        var verified = false;
        var primaries = 0;
        foreach (var entry in emails.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("email", out var email) || email.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("primary", out var isPrimary) || isPrimary.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !entry.TryGetProperty("verified", out var isVerified) || isVerified.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            if (isPrimary.ValueKind == JsonValueKind.True)
            {
                primaries++;
                primary = Text(email);
                verified = isVerified.ValueKind == JsonValueKind.True;
            }
        }

        var key = number.ToString(CultureInfo.InvariantCulture);
        return primaries == 1 && primary is not null ? new GitHubAccount(key, primary, verified) : new GitHubAccount(key, null, false);
    }

    /// <summary>A JSON string with an unpaired surrogate escape cannot be read as text: it is not an address.</summary>
    private static string? Text(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// How the reader hands the identity to the callback (Amendment A3): three items of the client's authentication properties, which OpenIddict copies
/// into the callback's outcome. The start never writes them, so only the reader can.
/// </summary>
public static class GitHubItems
{
    public const string Key = "auth_core.github.key";
    public const string Email = "auth_core.github.email";
    public const string Verified = "auth_core.github.verified";

    public static void Write(IDictionary<string, string?> items, GitHubAccount account)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(account);

        items[Key] = account.Key;
        items[Email] = account.Email;
        items[Verified] = account.EmailVerified ? "true" : "false";
    }

    /// <summary>The identity the reader handed over; <see langword="null"/> when there is none, or its key is not a decimal number.</summary>
    public static GitHubAccount? Read(IDictionary<string, string?>? items)
    {
        if (items is null
            || !items.TryGetValue(Key, out var key)
            || key is not { Length: > 0 and <= 19 }
            || !key.All(char.IsAsciiDigit))
        {
            return null;
        }

        var email = items.TryGetValue(Email, out var address) ? address : null;
        var verified = items.TryGetValue(Verified, out var flag) && string.Equals(flag, "true", StringComparison.Ordinal);
        return new GitHubAccount(key, email, email is not null && verified);
    }
}
