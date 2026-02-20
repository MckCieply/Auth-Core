using System.Globalization;

namespace Auth.Server.RateLimiting;

/// <summary>
/// <c>Auth:RateLimit:Enabled</c> (default <c>true</c>) and <c>Auth:RateLimit:&lt;Policy&gt;:PermitPerMinute</c> (at least 1, the
/// numbers of the contract by default). Read as strings: .NET 10 throws a binding error for an empty value of a number or a
/// flag, and compose passes one for a variable that is not set; blank means the default.
/// </summary>
public sealed class RateLimitSettings
{
    public const string EnabledKey = "Auth:RateLimit:Enabled";

    private readonly int[] _permits;

    public RateLimitSettings(bool enabled, IReadOnlyList<int> permits)
    {
        ArgumentNullException.ThrowIfNull(permits);

        if (permits.Count != Enum.GetValues<RatePolicy>().Length)
        {
            throw new ArgumentException("One number for every policy is needed.", nameof(permits));
        }

        Enabled = enabled;
        _permits = [.. permits];
    }

    public bool Enabled { get; }

    public int PermitPerMinute(RatePolicy policy) => _permits[(int)policy];

    public static string PermitKey(RatePolicy policy) => $"Auth:RateLimit:{policy}:PermitPerMinute";

    /// <exception cref="InvalidOperationException">A value is not a whole number of at least 1 (or not true or false); the message names the key.</exception>
    public static RateLimitSettings Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = true;
        if (configuration[EnabledKey] is { } flag && !string.IsNullOrWhiteSpace(flag) && !bool.TryParse(flag.Trim(), out enabled))
        {
            throw new InvalidOperationException($"Configuration value '{EnabledKey}' must be 'true' or 'false'.");
        }

        var permits = new List<int>();
        foreach (var policy in Enum.GetValues<RatePolicy>())
        {
            var key = PermitKey(policy);
            if (configuration[key] is not { } text || string.IsNullOrWhiteSpace(text))
            {
                permits.Add(RatePolicies.DefaultPermitPerMinute(policy));
                continue;
            }

            if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var permit) || permit < 1)
            {
                throw new InvalidOperationException($"Configuration value '{key}' must be a whole number of at least 1.");
            }

            permits.Add(permit);
        }

        return new RateLimitSettings(enabled, permits);
    }
}
