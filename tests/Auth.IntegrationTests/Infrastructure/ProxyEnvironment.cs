namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// The proxy variables of the machine, turned into settings that blank them. A variable such as
/// <c>Auth__Proxy__KnownNetworks__0</c> adds a child to the list that the plain keys do not cover, so a test host pins each one.
/// Takes the environment as a list, so that it can be tested without touching the real one.
/// </summary>
internal static class ProxyEnvironment
{
    private const string Prefix = "Auth:Proxy:";

    /// <summary>
    /// Every variable that starts with <c>Auth__Proxy__</c> or <c>Auth:Proxy:</c> (Windows allows a colon in a name), any case, as
    /// its configuration key with a blank value.
    /// </summary>
    public static Dictionary<string, string?> PinnedBlank(IEnumerable<KeyValuePair<string, string>> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var pinned = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in environment)
        {
            var key = name.Replace("__", ":", StringComparison.Ordinal);
            if (key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                pinned[key] = "";
            }
        }

        return pinned;
    }

    /// <summary>The same for the real environment of this process.</summary>
    public static Dictionary<string, string?> PinnedBlankFromProcess() =>
        PinnedBlank(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(entry => new KeyValuePair<string, string>((string)entry.Key, entry.Value as string ?? "")));
}
