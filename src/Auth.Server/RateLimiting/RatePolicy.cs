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
    /// <see langword="null"/> for a request outside <c>/auth/</c>. Only a <c>POST</c> to the five specific paths is not "general".
    /// </summary>
    public static RatePolicy? Classify(string method, PathString path)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (!path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!HttpMethods.IsPost(method))
        {
            return RatePolicy.General;
        }

        var text = path.Value ?? "";
        var trimmed = text.Length > 1 && text[^1] == '/' ? text[..^1] : text;
        if (trimmed.Equals("/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Login;
        }

        if (trimmed.Equals("/auth/refresh", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Refresh;
        }

        if (trimmed.Equals("/auth/password/forgot", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/password/reset", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/email/verify/request", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/email/verify", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Email;
        }

        if (trimmed.Equals("/auth/invites/preview", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/invites/accept", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Invite;
        }

        return RatePolicy.General;
    }
}
