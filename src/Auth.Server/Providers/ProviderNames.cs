namespace Auth.Server.Providers;

/// <summary>The providers of specs 0011 and 0012 by the names the contract uses everywhere: lower case, in paths, the manifest, answers and audit rows.</summary>
public static class ProviderNames
{
    public const string Google = "google";
    public const string Microsoft = "microsoft";
    public const string GitHub = "github";

    /// <summary>The providers in a fixed order (the redirection paths of OpenIddict's client follow it); the manifest orders the buttons.</summary>
    public static IReadOnlyList<string> All { get; } = [Google, Microsoft, GitHub];

    /// <summary>Exactly one of the three names, in lower case (spec 0012). Anything else is not a provider.</summary>
    public static bool IsName(string? name) => name is not null && All.Contains(name, StringComparer.Ordinal);

    /// <summary>The name a person reads: <c>Google</c>, <c>Microsoft</c> or <c>GitHub</c>.</summary>
    public static string DisplayNameOf(string name) => name switch
    {
        Google => "Google",
        Microsoft => "Microsoft",
        GitHub => "GitHub",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a provider name."),
    };
}
