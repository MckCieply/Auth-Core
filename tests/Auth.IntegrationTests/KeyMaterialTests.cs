using System.Security.Cryptography;
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

    [Fact]
    public void Rsa_key_shorter_than_2048_bits_is_rejected()
    {
        var directory = Directory.CreateTempSubdirectory("auth-core-weak-key-");
        try
        {
            var certPath = Path.Combine(directory.FullName, "weak.crt");
            var keyPath = Path.Combine(directory.FullName, "weak.key");
            using (var rsa = RSA.Create(1024))
            {
                var request = new CertificateRequest("CN=auth-core-test-weak", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
                File.WriteAllText(certPath, cert.ExportCertificatePem());
                File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
            }

            var ex = Assert.Throws<InvalidOperationException>(() =>
                KeyMaterialLoader.Load(certPath, keyPath, "Auth:Keys:SigningKeyPath"));
            Assert.Contains("Auth:Keys:SigningKeyPath", ex.Message);
            Assert.Contains("2048", ex.Message);
            Assert.DoesNotContain("BEGIN", ex.Message);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
