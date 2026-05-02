using System.Globalization;
using Auth.Server.Email;
using Microsoft.AspNetCore.WebUtilities;

namespace Auth.Server.Providers;

/// <summary>The client id and the client secret a provider issued to the product. A class, not a record: a record would print the secret.</summary>
public sealed class ProviderKeys(string clientId, string clientSecret)
{
    public string ClientId { get; } = clientId;

    public string ClientSecret { get; } = clientSecret;
}

/// <summary>
/// The settings of spec 0011 → Configuration: the product's return screen, the keys of each provider, the day the Microsoft secret lapses and, in
/// Development only, the test issuer. Read once, at start: a bad value stops the start naming its key and never echoing the value; a missing one
/// never does, it only leaves its provider unoffered (<see cref="MissingKeysOf"/>).
/// </summary>
public sealed class ProviderSettings
{
    public const string ReturnUrlKey = "Auth:App:FrontendUrls:ProviderReturn";
    public const string GoogleClientIdKey = "Auth:Providers:Google:ClientId";
    public const string GoogleClientSecretKey = "Auth:Providers:Google:ClientSecret";
    public const string MicrosoftClientIdKey = "Auth:Providers:Microsoft:ClientId";
    public const string MicrosoftClientSecretKey = "Auth:Providers:Microsoft:ClientSecret";
    public const string MicrosoftSecretExpiresOnKey = "Auth:Providers:Microsoft:ClientSecretExpiresOn";
    public const string TestIssuerKey = "Auth:Providers:TestIssuer";

    private const string DateFormat = "yyyy-MM-dd";

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _missing;

    private ProviderSettings(IReadOnlyDictionary<string, IReadOnlyList<string>> missing) => _missing = missing;

    /// <summary>The product's return screen; <see langword="null"/> when it is not set, and then no provider is offered.</summary>
    public Uri? ReturnUrl { get; private init; }

    public ProviderKeys? Google { get; private init; }

    public ProviderKeys? Microsoft { get; private init; }

    /// <summary>The day the Microsoft client secret lapses at Microsoft; <see langword="null"/>: nothing is checked.</summary>
    public DateOnly? MicrosoftSecretExpiresOn { get; private init; }

    /// <summary>The fake provider's address (Development only): both providers are then plain OpenID Connect clients of it.</summary>
    public Uri? TestIssuer { get; private init; }

    /// <summary>The origin of the return screen: the origin of every callback address, and the client's own URI (Amendment A2).</summary>
    public Uri? ReturnOrigin => ReturnUrl is null ? null : new Uri(ReturnUrl.GetLeftPart(UriPartial.Authority));

    public ProviderKeys? KeysOf(string provider) => provider switch
    {
        ProviderNames.Google => Google,
        ProviderNames.Microsoft => Microsoft,
        _ => null,
    };

    /// <summary>The keys of configuration a provider still needs before it can be offered, in the order of the contract; empty when none.</summary>
    public IReadOnlyList<string> MissingKeysOf(string provider) => _missing[provider];

    /// <summary>The callback address the operator registers at the provider: the return address's origin and the callback path (Settled 2).</summary>
    public Uri CallbackUriOf(string provider) =>
        new(ReturnOrigin ?? throw new InvalidOperationException("The return address is not set."), ProviderPaths.CallbackPathOf(provider));

    /// <summary>The issuer of a provider under the test issuer: <c>{TestIssuer}/google</c> or <c>{TestIssuer}/microsoft</c>.</summary>
    public Uri TestIssuerOf(string provider) =>
        new((TestIssuer ?? throw new InvalidOperationException("No test issuer is set.")).GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + provider);

    /// <summary>Reads and checks the settings. Errors name the key and never echo a value.</summary>
    /// <param name="isDevelopment">Development accepts an <c>http</c> return address and the test issuer.</param>
    /// <exception cref="InvalidOperationException">A value is set and invalid.</exception>
    public static ProviderSettings Load(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // The rules of the completion address (spec 0009), and no `ticket` parameter: the ticket is added in the fragment.
        var returnUrl = MailSettingsLoader.OptionalFrontendUrl(configuration, ReturnUrlKey, isDevelopment);
        if (returnUrl is not null && QueryHelpers.ParseQuery(returnUrl.Query).ContainsKey("ticket"))
        {
            throw new InvalidOperationException($"Configuration value '{ReturnUrlKey}' must not hold a 'ticket' parameter.");
        }

        var missing = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var google = KeysAt(configuration, GoogleClientIdKey, GoogleClientSecretKey, returnUrl is null, out var googleMissing);
        missing[ProviderNames.Google] = googleMissing;
        var microsoft = KeysAt(configuration, MicrosoftClientIdKey, MicrosoftClientSecretKey, returnUrl is null, out var microsoftMissing);
        missing[ProviderNames.Microsoft] = microsoftMissing;

        return new ProviderSettings(missing)
        {
            ReturnUrl = returnUrl,
            Google = google,
            Microsoft = microsoft,
            MicrosoftSecretExpiresOn = SecretDate(configuration),
            TestIssuer = TestIssuerAt(configuration, isDevelopment),
        };
    }

    /// <summary>Both keys set (not blank): the keys, used exactly as configured; otherwise <see langword="null"/>, and the missing ones named.</summary>
    private static ProviderKeys? KeysAt(IConfiguration configuration, string idKey, string secretKey, bool noReturnAddress, out IReadOnlyList<string> missing)
    {
        var id = configuration[idKey];
        var secret = configuration[secretKey];
        var absent = new List<string>(3);
        if (string.IsNullOrWhiteSpace(id))
        {
            absent.Add(idKey);
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            absent.Add(secretKey);
        }

        if (noReturnAddress)
        {
            absent.Add(ReturnUrlKey);
        }

        missing = absent;
        return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) ? null : new ProviderKeys(id, secret);
    }

    private static DateOnly? SecretDate(IConfiguration configuration)
    {
        var text = configuration[MicrosoftSecretExpiresOnKey];
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateOnly.TryParseExact(text.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new InvalidOperationException($"Configuration value '{MicrosoftSecretExpiresOnKey}' must be a date written yyyy-mm-dd.");
    }

    private static Uri? TestIssuerAt(IConfiguration configuration, bool isDevelopment)
    {
        var text = configuration[TestIssuerKey];
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Settled 23: a production instance must never trust a provider an operator typed by mistake.
        if (!isDevelopment)
        {
            throw new InvalidOperationException($"Configuration value '{TestIssuerKey}' is for the Development environment only: remove it.");
        }

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var issuer)
            || (issuer.Scheme != Uri.UriSchemeHttp && issuer.Scheme != Uri.UriSchemeHttps)
            || issuer.Query.Length > 0
            || issuer.Fragment.Length > 0
            || issuer.UserInfo.Length > 0)
        {
            throw new InvalidOperationException(
                $"Configuration value '{TestIssuerKey}' must be an absolute http or https URL without a query, a fragment or credentials.");
        }

        return issuer;
    }
}
