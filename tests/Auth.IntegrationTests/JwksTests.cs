using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace Auth.IntegrationTests;

public sealed class JwksTests : IAsyncLifetime
{
    private readonly KeyMaterialFixture _keys;
    private readonly AuthAppFactory _factory;
    private HttpClient _client = null!;

    public JwksTests(PostgresFixture postgres, KeyMaterialFixture keys)
    {
        _keys = keys;
        _factory = new AuthAppFactory(postgres, keys);
    }

    public ValueTask InitializeAsync()
    {
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Jwks_exposes_the_rsa_signing_key_with_a_kid()
    {
        var jwks = await Jwks.FetchAsync(_client);
        var key = Assert.Single(jwks.Keys, k => k.Use == "sig");
        Assert.Equal("RSA", key.Kty);
        Assert.False(string.IsNullOrEmpty(key.Kid));
    }

    [Fact]
    public async Task Jwks_never_contains_private_key_members()   // criterion 8
    {
        var json = await _client.GetStringAsync(Jwks.Path);
        foreach (var key in JsonDocument.Parse(json).RootElement.GetProperty("keys").EnumerateArray())
        {
            foreach (var member in new[] { "d", "p", "q", "dp", "dq", "qi" })
            {
                Assert.False(key.TryGetProperty(member, out _), $"JWKS leaks private member '{member}'");
            }
        }
    }

    [Fact]
    public async Task Jwks_signing_key_matches_the_mounted_certificate()
    {
        using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(_keys.SigningCertPath));
        using var rsa = cert.GetRSAPublicKey();
        Assert.NotNull(rsa);
        var expectedModulus = Base64UrlEncoder.Encode(rsa.ExportParameters(includePrivateParameters: false).Modulus);

        var jwks = await Jwks.FetchAsync(_client);
        var key = Assert.Single(jwks.Keys, k => k.Use == "sig");

        Assert.Equal(expectedModulus, key.N);
    }

    [Fact]
    public async Task Jwks_has_exactly_one_key_and_never_publishes_the_encryption_certificate()
    {
        using var encryption = X509Certificate2.CreateFromPem(File.ReadAllText(_keys.EncryptionCertPath));
        using var rsa = encryption.GetRSAPublicKey();
        Assert.NotNull(rsa);
        var encryptionModulus = Base64UrlEncoder.Encode(rsa.ExportParameters(includePrivateParameters: false).Modulus);

        var jwks = await Jwks.FetchAsync(_client);

        Assert.Single(jwks.Keys);
        Assert.DoesNotContain(jwks.Keys, k => k.N == encryptionModulus);
    }

    [Theory]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/auth/.well-known/openid-configuration")]
    public async Task Discovery_document_is_not_served(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await _client.GetAsync(Jwks.Path)).StatusCode);
    }
}
