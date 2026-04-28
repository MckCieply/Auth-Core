namespace Auth.Server.RateLimiting;

/// <summary>The five limits of spec 0008 → Per-IP rate limiting. A request counts against exactly one.</summary>
public enum RatePolicy
{
    Login = 0,
    Refresh = 1,
    Email = 2,
    Invite = 3,
    General = 4,
}

public static class RatePolicies
{
    public static string NameOf(RatePolicy policy) => policy switch
    {
        RatePolicy.Login => "login",
        RatePolicy.Refresh => "refresh",
        RatePolicy.Email => "email",
        RatePolicy.Invite => "invite",
        RatePolicy.General => "general",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown policy."),
    };

    /// <summary>Requests per minute: 30, 60, 10, 20 and 300.</summary>
    public static int DefaultPermitPerMinute(RatePolicy policy) => policy switch
    {
        RatePolicy.Login => 30,
        RatePolicy.Refresh => 60,
        RatePolicy.Email => 10,
        RatePolicy.Invite => 20,
        RatePolicy.General => 300,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown policy."),
    };

    /// <summary>
    /// The policy of a request, chosen by method and path (the path compared without regard to case or one trailing slash), or
    /// <see langword="null"/> for a request outside <c>/auth/</c>. Only a <c>POST</c> to the specific paths, and a <c>GET</c> of the start or the
    /// callback of a provider round trip (spec 0011), is not "general".
    /// </summary>
    public static RatePolicy? Classify(string method, PathString path)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (!path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var text = path.Value ?? "";
        var trimmed = text.Length > 1 && text[^1] == '/' ? text[..^1] : text;

        // Spec 0011: the start and the callback of a provider round trip are steps of a sign-in, the only GETs that are not general.
        if (HttpMethods.IsGet(method) && IsProviderRoundTrip(trimmed))
        {
            return RatePolicy.Login;
        }

        if (!HttpMethods.IsPost(method))
        {
            return RatePolicy.General;
        }

        if (trimmed.Equals("/auth/login", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/providers/exchange", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/providers/link", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/providers/finish", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Login;
        }

        if (trimmed.Equals("/auth/refresh", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Refresh;
        }

        if (trimmed.Equals("/auth/signup", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/password/forgot", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/password/reset", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/email/verify/request", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/email/verify", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Email;
        }

        if (trimmed.Equals("/auth/invites/preview", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/invites/accept", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/signup/preview", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/signup/complete", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Invite;
        }

        return RatePolicy.General;
    }

    /// <summary><c>/auth/providers/{any name}/start</c> or <c>/callback</c>, without regard to case: the name is judged later, by the endpoint.</summary>
    private static bool IsProviderRoundTrip(string path)
    {
        var segments = path.Split('/');
        return segments.Length == 5
            && segments[0].Length == 0
            && segments[1].Equals("auth", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("providers", StringComparison.OrdinalIgnoreCase)
            && segments[3].Length > 0
            && (segments[4].Equals("start", StringComparison.OrdinalIgnoreCase) || segments[4].Equals("callback", StringComparison.OrdinalIgnoreCase));
    }
}
