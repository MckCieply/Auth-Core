namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// The proxy variables of the machine, turned into settings that blank them. A variable such as
/// <c>Auth__Proxy__KnownNetworks__0</c> adds a child to the list that the plain keys do not cover, so a test host pins each one.
/// Takes the environment as a list, so that it can be tested without touching the real one.
/// </summary>
internal static class ProxyEnvironment
{
    private const string Prefix = "Auth:Proxy:";

    /// <summary>The prefixes under which the default host builder also reads the environment into configuration, with the prefix removed.</summary>
    private static readonly string[] HostPrefixes = ["ASPNETCORE_", "DOTNET_"];

    /// <summary>
    /// Every variable that starts with <c>Auth__Proxy__</c> or <c>Auth:Proxy:</c> (Windows allows a colon in a name), any case, as
    /// its configuration key with a blank value. A leading <c>ASPNETCORE_</c> or <c>DOTNET_</c> is dropped first: the host reads those
    /// variables too, so <c>ASPNETCORE_Auth__Proxy__KnownNetworks__0</c> reaches a host as <c>Auth:Proxy:KnownNetworks:0</c>.
    /// </summary>
    public static Dictionary<string, string?> PinnedBlank(IEnumerable<KeyValuePair<string, string>> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var pinned = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in environment)
        {
            var key = WithoutHostPrefix(name).Replace("__", ":", StringComparison.Ordinal);
            if (key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                pinned[key] = "";
            }
        }

        return pinned;
    }

    private static string WithoutHostPrefix(string name)
    {
        foreach (var prefix in HostPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name[prefix.Length..];
            }
        }

        return name;
    }

    /// <summary>The same for the real environment of this process.</summary>
    public static Dictionary<string, string?> PinnedBlankFromProcess() =>
        PinnedBlank(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(entry => new KeyValuePair<string, string>((string)entry.Key, entry.Value as string ?? "")));
}
