using System.Diagnostics.CodeAnalysis;

namespace Auth.Server.Providers;

/// <summary>
/// The cookie <c>auth_pt</c> of spec 0011 → The browser binding: 32 random bytes that the callback sets with a ticket, whose SHA-256 the ticket
/// stores. Every request that takes a ticket needs it, so a ticket pushed into another browser is worth nothing there. The only place that knows
/// its name and attributes.
/// </summary>
public static class ProviderBindingCookie
{
    public const string Name = "auth_pt";
    public const string Path = "/auth";

    /// <summary>Longest value treated as a binding; a binding is 43 characters.</summary>
    public const int MaxValueLength = 256;

    /// <summary><c>Max-Age=600</c>: the 10 minutes of a ticket (a re-authentication's 5 are judged on the ticket).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static void Append(HttpResponse response, string value)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Append(Name, value, Options(Lifetime));
    }

    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Append(Name, string.Empty, Options(TimeSpan.Zero));
    }

    /// <summary>Reads the cookie; <see langword="false"/> when it is missing, empty, too long or holds a control character.</summary>
    public static bool TryRead(HttpRequest request, [NotNullWhen(true)] out string? value)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Cookies.TryGetValue(Name, out var found) && found.Length is > 0 and <= MaxValueLength && !found.Any(char.IsControl))
        {
            value = found;
            return true;
        }

        value = null;
        return false;
    }

    // Secure is unconditional: TLS ends at the reverse proxy, so the request scheme seen here says nothing.
    private static CookieOptions Options(TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        MaxAge = lifetime,
        IsEssential = true,
    };
}
