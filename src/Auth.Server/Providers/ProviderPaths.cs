namespace Auth.Server.Providers;

/// <summary>The paths of spec 0011 → Settled 1.</summary>
public static class ProviderPaths
{
    public const string Prefix = "/auth/providers";
    public const string StartSegment = "start";
    public const string CallbackSegment = "callback";
    public const string Start = Prefix + "/{name}/" + StartSegment;
    public const string Callback = Prefix + "/{name}/" + CallbackSegment;
    public const string Exchange = "/auth/providers/exchange";
    public const string Link = "/auth/providers/link";
    public const string Finish = "/auth/providers/finish";

    /// <summary>The three POSTs that are token-endpoint requests of OpenIddict, as the sign-up completion is.</summary>
    public static IReadOnlyList<string> TokenPaths { get; } = [Exchange, Link, Finish];

    public static string CallbackPathOf(string provider) => Prefix + "/" + provider + "/" + CallbackSegment;

    /// <summary>The callback of a provider, case-insensitive and with an optional trailing slash: the paths OpenIddict's redirection endpoint answers.</summary>
    public static bool IsCallbackPath(PathString path) =>
        ProviderNames.All.Any(provider =>
            path.Equals(CallbackPathOf(provider), StringComparison.OrdinalIgnoreCase)
            || path.Equals(CallbackPathOf(provider) + "/", StringComparison.OrdinalIgnoreCase));
}
