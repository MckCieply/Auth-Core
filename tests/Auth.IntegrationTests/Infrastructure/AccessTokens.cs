using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Reads the payload of an access token without validating it: the tests ask what the token says.</summary>
public static class AccessTokens
{
    /// <summary>The claims of an access token as the contract has them (sorted ordinally): OpenIddict's own metadata, <c>sub</c> and the tenancy claims; no session claim.</summary>
    public static readonly string[] ContractClaims = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"];

    public static JsonObject Payload(string jwt)
    {
        ArgumentNullException.ThrowIfNull(jwt);

        return JsonNode.Parse(Base64UrlEncoder.Decode(jwt.Split('.')[1]))!.AsObject();
    }

    /// <summary>A claim that must be a JSON array of strings, as the contract says of <c>roles</c> and <c>permissions</c>.</summary>
    public static string[] Array(string jwt, string claim)
    {
        var node = Payload(jwt)[claim];
        Assert.NotNull(node);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, node.GetValueKind());
        return [.. node.AsArray().Select(n => n!.GetValue<string>())];
    }

    public static string Text(string jwt, string claim) =>
        Payload(jwt)[claim]?.GetValue<string>() ?? throw new InvalidOperationException($"The token has no claim '{claim}'.");
}
