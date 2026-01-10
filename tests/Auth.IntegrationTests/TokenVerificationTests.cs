using System.Text;
using System.Text.Json.Nodes;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Auth.IntegrationTests;

/// <summary>Criteria 4 and 7: a consumer verifies tokens offline against the JWKS, across restarts.</summary>
public class TokenVerificationTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static readonly TokenOptions Expected = new();

    private static string UniqueDatabase() => "auth_" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Token_verifies_against_jwks_kid_without_shared_secret()   // criterion 4
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        using var client = factory.CreateClient();
        var token = await LoginApi.LoginToken(client, factory);

        var jwks = await Jwks.FetchAsync(client);
        Assert.Contains(jwks.Keys, k => k.Kid == new JsonWebToken(token).Kid);

        var result = await Jwks.ValidateAsync(token, jwks, Expected);
        Assert.True(result.IsValid, result.Exception?.Message);
        var validated = Assert.IsType<JsonWebToken>(result.SecurityToken);
        Assert.Equal((await factory.SeedUserIdAsync()).ToString(), validated.Subject, ignoreCase: true);
    }

    [Fact]
    public async Task Tampered_signature_fails_verification()
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        using var client = factory.CreateClient();
        var token = await LoginApi.LoginToken(client, factory);
        var jwks = await Jwks.FetchAsync(client);
        Assert.True((await Jwks.ValidateAsync(token, jwks, Expected)).IsValid); // control: the untouched token is valid

        var parts = token.Split('.');
        // Flip the FIRST signature character: all six of its bits are significant, so the decoded bytes
        // certainly change (the last character of a 256-byte signature carries unused bits and would not).
        parts[2] = (parts[2][0] == 'A' ? 'B' : 'A') + parts[2][1..];
        var tampered = string.Join('.', parts);

        Assert.NotEqual(token, tampered);
        Assert.False((await Jwks.ValidateAsync(tampered, jwks, Expected)).IsValid);
    }

    [Fact]
    public async Task Tampered_payload_with_original_signature_fails_verification()
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        using var client = factory.CreateClient();
        var token = await LoginApi.LoginToken(client, factory);
        var jwks = await Jwks.FetchAsync(client);

        var parts = token.Split('.');
        var payload = JsonNode.Parse(Base64UrlEncoder.Decode(parts[1]))!.AsObject();
        payload["sub"] = Guid.NewGuid().ToString();
        parts[1] = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var tampered = string.Join('.', parts);

        Assert.NotEqual(token, tampered);
        Assert.False((await Jwks.ValidateAsync(tampered, jwks, Expected)).IsValid);
    }

    [Fact]
    public async Task Token_with_alg_none_and_empty_signature_fails_verification()
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        using var client = factory.CreateClient();
        var token = await LoginApi.LoginToken(client, factory);
        var jwks = await Jwks.FetchAsync(client);

        var parts = token.Split('.');
        var header = JsonNode.Parse(Base64UrlEncoder.Decode(parts[0]))!.AsObject();
        header["alg"] = "none";
        var unsigned = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + parts[1] + ".";

        Assert.False((await Jwks.ValidateAsync(unsigned, jwks, Expected)).IsValid);
    }

    [Fact]
    public async Task Token_issued_before_restart_verifies_against_jwks_after_restart()   // criterion 7
    {
        var database = UniqueDatabase();
        string token;
        var before = new AuthAppFactory(postgres, keys, database);
        try
        {
            using var client = before.CreateClient();
            token = await LoginApi.LoginToken(client, before);
        }
        finally
        {
            await before.DisposeAsync(); // the first host is fully stopped before the second one starts
        }

        await using var after = new AuthAppFactory(postgres, keys, database); // new host, same mounted keys + database
        using var afterClient = after.CreateClient();
        var jwks = await Jwks.FetchAsync(afterClient);

        Assert.Contains(jwks.Keys, k => k.Kid == new JsonWebToken(token).Kid);
        var result = await Jwks.ValidateAsync(token, jwks, Expected);
        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Fact]
    public async Task Token_from_a_host_with_different_keys_fails_against_this_jwks()
    {
        // A host signing with an independently generated key pair: its tokens must NOT verify against
        // the JWKS of a host using the shared fixture keys. Guards against a verifier that accepts anything.
        using var otherKeys = new KeyMaterialFixture();
        await using var other = new AuthAppFactory(postgres, otherKeys);
        using var otherClient = other.CreateClient();
        var foreignToken = await LoginApi.LoginToken(otherClient, other);
        var foreignJwks = await Jwks.FetchAsync(otherClient);
        Assert.True((await Jwks.ValidateAsync(foreignToken, foreignJwks, Expected)).IsValid); // control: valid on its own host

        await using var ours = new AuthAppFactory(postgres, keys);
        using var ourClient = ours.CreateClient();
        var ourJwks = await Jwks.FetchAsync(ourClient);

        Assert.DoesNotContain(ourJwks.Keys, k => k.Kid == new JsonWebToken(foreignToken).Kid);
        Assert.False((await Jwks.ValidateAsync(foreignToken, ourJwks, Expected)).IsValid);
    }
}
