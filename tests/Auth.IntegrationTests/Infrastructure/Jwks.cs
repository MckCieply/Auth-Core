using Auth.Server.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Helpers that act like a token consumer: fetch the JWKS, then verify a JWT offline against it.</summary>
public static class Jwks
{
    public const string Path = "/auth/.well-known/jwks.json";

    public static async Task<JsonWebKeySet> FetchAsync(HttpClient client)
    {
        var json = await client.GetStringAsync(Path);
        return new JsonWebKeySet(json);
    }

    /// <summary>
    /// Validates <paramref name="jwt"/> the way a consumer would: RS256 only, issuer and audience pinned to
    /// <paramref name="expected"/>, lifetime checked, and signing keys taken from the JWKS and nowhere else.
    /// </summary>
    public static async Task<TokenValidationResult> ValidateAsync(string jwt, JsonWebKeySet keys, TokenOptions expected)
    {
        var parameters = new TokenValidationParameters
        {
            ValidAlgorithms = ["RS256"],
            ValidIssuer = expected.Issuer,
            ValidAudience = expected.Audience,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys.GetSigningKeys(),
            // OpenIddict marks access tokens with the RFC 9068 media type.
            ValidTypes = ["at+jwt"],
        };

        return await new JsonWebTokenHandler().ValidateTokenAsync(jwt, parameters);
    }
}
