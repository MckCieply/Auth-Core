using System.Globalization;
using Auth.Server.Tenancy;

namespace Auth.Server.Providers;

/// <summary>
/// Which providers are offered (spec 0011 → General rules): listed by the active manifest, both keys set and the return address set. It reads the
/// manifest of the process at every call, as every request does, so a provider dropped from the manifest is not offered to the next request
/// (the callback then answers <c>provider_unavailable</c>). It also judges health: a listed provider that is not offered, and the Microsoft secret
/// from 30 days before its date on.
/// </summary>
public sealed partial class ProviderCatalog(ManifestHolder manifest, ProviderSettings settings, TimeProvider clock, ILogger<ProviderCatalog> logger)
{
    /// <summary>From this many days before the secret's date, and after it, health says <c>Degraded</c>.</summary>
    public const int SecretWarningDays = 30;

    /// <summary>The offered providers, in the manifest's order: the buttons of <c>GET /auth/options</c>.</summary>
    public IReadOnlyList<string> Offered => [.. manifest.Current.Providers.Where(p => settings.MissingKeysOf(p).Count == 0)];

    public bool IsOffered(string provider) =>
        manifest.Current.Providers.Contains(provider, StringComparer.Ordinal) && settings.MissingKeysOf(provider).Count == 0;

    /// <summary>A provider the manifest lists lacks a key or the return address: a misconfiguration.</summary>
    public bool ListedButNotOffered => manifest.Current.Providers.Any(p => settings.MissingKeysOf(p).Count > 0);

    /// <summary>The Microsoft secret lapses within <see cref="SecretWarningDays"/> days, or has lapsed. Without the setting nothing is checked.</summary>
    public bool SecretLapsesSoon =>
        settings.MicrosoftSecretExpiresOn is { } date
        && date.DayNumber - DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).DayNumber <= SecretWarningDays;

    public bool IsDegraded => ListedButNotOffered || SecretLapsesSoon;

    /// <summary>Called once at start, after the manifest is active: one warning for each listed provider that is not offered, and one for the secret.</summary>
    public void WarnAtStart()
    {
        foreach (var provider in manifest.Current.Providers)
        {
            var missing = settings.MissingKeysOf(provider);
            if (missing.Count > 0)
            {
                LogNotOffered(logger, provider, string.Join(", ", missing));
            }
        }

        if (SecretLapsesSoon && settings.MicrosoftSecretExpiresOn is { } date)
        {
            LogSecretLapses(logger, ProviderSettings.MicrosoftSecretExpiresOnKey, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The manifest lists {Provider}, but these settings are not set: {Keys}. The provider is not offered.")]
    private static partial void LogNotOffered(ILogger logger, string provider, string keys);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Microsoft client secret lapses on {Date} ({Key}): rotate it before then (docs/operations/microsoft-secret.md).")]
    private static partial void LogSecretLapses(ILogger logger, string key, string date);
}
