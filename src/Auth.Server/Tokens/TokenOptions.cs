namespace Auth.Server.Tokens;

/// <summary>
/// Issuer and audience of the access tokens, bound from the <c>Auth:Tokens</c> section. Both are per-instance
/// in production; the defaults are the development identifiers from the spec.
/// </summary>
public sealed class TokenOptions
{
    public const string SectionName = "Auth:Tokens";

    public const string IssuerKey = SectionName + ":" + nameof(Issuer);
    public const string AudienceKey = SectionName + ":" + nameof(Audience);

    /// <summary>Access-token lifetime. A constant on purpose: it is part of the token contract, not a knob.</summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The <c>iss</c> claim; an absolute URI. The token and JWKS endpoints live under its path.</summary>
    public string Issuer { get; set; } = "http://localhost:8080/auth";

    /// <summary>The registered <c>aud</c> claim.</summary>
    public string Audience { get; set; } = "auth-core-dev";
}
