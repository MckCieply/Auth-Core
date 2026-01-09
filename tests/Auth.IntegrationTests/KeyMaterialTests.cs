using System.Security.Cryptography.X509Certificates;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Keys;

namespace Auth.IntegrationTests;

public class KeyMaterialTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    [Fact]
    public void Loads_certificate_with_private_key_from_pem_pair()
    {
        using var cert = KeyMaterialLoader.Load(keys.SigningCertPath, keys.SigningKeyPath, KeyMaterialOptions.SigningKeyPathKey);
        Assert.True(cert.HasPrivateKey);
        Assert.NotNull(cert.GetRSAPrivateKey());
    }

    [Fact]
    public void Missing_key_file_fails_fast_naming_the_config_key()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            KeyMaterialLoader.Load(keys.SigningCertPath, "/nonexistent/signing.key", "Auth:Keys:SigningKeyPath"));
        Assert.Contains("Auth:Keys:SigningKeyPath", ex.Message);
        Assert.DoesNotContain("PRIVATE KEY", ex.Message);
        Assert.DoesNotContain("BEGIN", ex.Message);
    }

    [Fact]
    public void Certificate_without_key_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            KeyMaterialLoader.Load(keys.SigningCertPath, keys.SigningCertPath, "Auth:Keys:SigningKeyPath"));
        Assert.Contains("Auth:Keys:SigningKeyPath", ex.Message);
        Assert.DoesNotContain("BEGIN", ex.Message);
    }

    [Fact]
    public void Non_rsa_certificate_is_rejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            KeyMaterialLoader.Load(keys.EcCertPath, keys.EcKeyPath, "Auth:Keys:SigningKeyPath"));
        Assert.Contains("Auth:Keys:SigningKeyPath", ex.Message);
        Assert.Contains("RSA", ex.Message);
        Assert.DoesNotContain("BEGIN", ex.Message);
    }

    [Fact]
    public async Task Host_refuses_to_start_without_key_configuration()   // Review Focus #5
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSetting("Auth:Keys:SigningKeyPath", "");
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Auth:Keys:SigningKeyPath", ex.ToString());
    }
}
