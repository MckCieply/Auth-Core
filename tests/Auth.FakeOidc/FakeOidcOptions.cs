using Microsoft.Extensions.Configuration;

namespace Auth.FakeOidc;

/// <summary>
/// Where the test issuer says its endpoints are. Both are optional; without them every address is the origin of the request. In the end-to-end
/// stack Auth-Core reaches the fake on the container network (<see cref="IssuerOrigin"/>, http://fake-oidc:8099: the issuer, the token and key
/// endpoints) and the browser reaches it on the host (<see cref="BrowserOrigin"/>, http://localhost:8099: the authorization endpoint).
/// </summary>
public sealed class FakeOidcOptions
{
    public const string IssuerOriginKey = "FakeOidc:IssuerOrigin";
    public const string BrowserOriginKey = "FakeOidc:BrowserOrigin";

    public Uri? IssuerOrigin { get; init; }

    public Uri? BrowserOrigin { get; init; }

    public static FakeOidcOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new FakeOidcOptions
        {
            IssuerOrigin = Origin(configuration[IssuerOriginKey], IssuerOriginKey),
            BrowserOrigin = Origin(configuration[BrowserOriginKey], BrowserOriginKey),
        };
    }

    private static Uri? Origin(string? text, string key)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            ? new Uri(uri.GetLeftPart(UriPartial.Authority))
            : throw new InvalidOperationException($"Configuration value '{key}' must be an absolute URL.");
    }
}
