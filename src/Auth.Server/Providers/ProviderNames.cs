namespace Auth.Server.Providers;

/// <summary>The providers of spec 0011 by the names the contract uses everywhere: lower case, in paths, the manifest, answers and audit rows.</summary>
public static class ProviderNames
{
    public const string Google = "google";
    public const string Microsoft = "microsoft";

    public static IReadOnlyList<string> All { get; } = [Google, Microsoft];

    /// <summary>Exactly one of the two names, in lower case. Anything else is not a provider.</summary>
    public static bool IsName(string? name) =>
        string.Equals(name, Google, StringComparison.Ordinal) || string.Equals(name, Microsoft, StringComparison.Ordinal);

    /// <summary>The name a person reads: <c>Google</c> or <c>Microsoft</c>.</summary>
    public static string DisplayNameOf(string name) => name switch
    {
        Google => "Google",
        Microsoft => "Microsoft",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a provider name."),
    };
}
