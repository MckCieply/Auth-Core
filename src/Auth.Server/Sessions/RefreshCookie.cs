using System.Diagnostics.CodeAnalysis;

namespace Auth.Server.Sessions;

/// <summary>The refresh cookie of ADR 0004. The only place that knows its name and attributes.</summary>
public static class RefreshCookie
{
    public const string Name = "auth_rt";
    public const string Path = "/auth";

    /// <summary>Longest value treated as a token. A reference token is 43 characters; anything far longer is junk.</summary>
    public const int MaxTokenLength = 256;

    public static void Append(HttpResponse response, string token, TimeSpan lifetime) =>
        response.Cookies.Append(Name, token, Options(lifetime));

    public static void Clear(HttpResponse response) =>
        response.Cookies.Append(Name, string.Empty, Options(TimeSpan.Zero));

    /// <summary>
    /// Reads the cookie. <see langword="false"/> when it is missing, empty, too long or holds a control character:
    /// such a value cannot be a token and must never reach the token store.
    /// </summary>
    public static bool TryRead(HttpRequest request, [NotNullWhen(true)] out string? token)
    {
        if (request.Cookies.TryGetValue(Name, out var value)
            && value.Length is > 0 and <= MaxTokenLength
            && !value.Any(char.IsControl))
        {
            token = value;
            return true;
        }

        token = null;
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
