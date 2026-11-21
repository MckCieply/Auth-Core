using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Reads refresh tokens as the service keeps them: the cookie holds only a reference, and the entry holds the token itself, encrypted to the instance's key.</summary>
public static class StoredTokens
{
    /// <summary>The claims of a stored refresh token.</summary>
    public static async Task<JsonObject> RefreshClaimsAsync(AuthAppFactory factory, string refreshToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(refreshToken);

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var entry = await tokens.FindByReferenceIdAsync(refreshToken, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The refresh token is not stored.");
        var payload = await tokens.GetPayloadAsync(entry, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The refresh token entry has no payload.");

        var server = factory.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;
        var handler = new JsonWebTokenHandler();
        var signed = handler.DecryptToken(
            handler.ReadJsonWebToken(payload),
            new TokenValidationParameters { TokenDecryptionKeys = [.. server.EncryptionCredentials.Select(c => c.Key)] });
        return AccessTokens.Payload(signed);
    }

    /// <summary>The status of a stored refresh token: <c>valid</c>, <c>redeemed</c> or <c>revoked</c>.</summary>
    public static async Task<string> StatusAsync(AuthAppFactory factory, string refreshToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(refreshToken);

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var entry = await tokens.FindByReferenceIdAsync(refreshToken, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The refresh token is not stored.");
        return await tokens.GetStatusAsync(entry, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("The refresh token entry has no status.");
    }
}
