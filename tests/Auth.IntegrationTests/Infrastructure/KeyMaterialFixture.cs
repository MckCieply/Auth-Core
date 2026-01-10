using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

[assembly: AssemblyFixture(typeof(Auth.IntegrationTests.Infrastructure.KeyMaterialFixture))]

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Generates throwaway signing and encryption key material (self-signed certificate + PKCS#8 key, both PEM)
/// into a unique temp directory, once for the whole test assembly. Mirrors what <c>scripts/dev-keys.sh</c>
/// produces for local development. The directory is deleted on dispose.
/// </summary>
public sealed class KeyMaterialFixture : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "auth-core-keys-" + Guid.NewGuid().ToString("N"))).FullName;

    public KeyMaterialFixture()
    {
        SigningCertPath = Path.Combine(_directory, "signing.crt");
        SigningKeyPath = Path.Combine(_directory, "signing.key");
        EncryptionCertPath = Path.Combine(_directory, "encryption.crt");
        EncryptionKeyPath = Path.Combine(_directory, "encryption.key");
        EcCertPath = Path.Combine(_directory, "ec.crt");
        EcKeyPath = Path.Combine(_directory, "ec.key");

        WriteRsaPair(SigningCertPath, SigningKeyPath, "CN=auth-core-test-signing", X509KeyUsageFlags.DigitalSignature);
        WriteRsaPair(EncryptionCertPath, EncryptionKeyPath, "CN=auth-core-test-encryption", X509KeyUsageFlags.KeyEncipherment);
        WriteEcPair(EcCertPath, EcKeyPath, "CN=auth-core-test-ec");
    }

    public string SigningCertPath { get; }

    public string SigningKeyPath { get; }

    public string EncryptionCertPath { get; }

    public string EncryptionKeyPath { get; }

    /// <summary>An ECDSA (non-RSA) certificate, for negative tests: the token contract is RS256.</summary>
    public string EcCertPath { get; }

    public string EcKeyPath { get; }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static void WriteRsaPair(string certPath, string keyPath, string subject, X509KeyUsageFlags usage)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));
        WritePair(request, rsa.ExportPkcs8PrivateKeyPem(), certPath, keyPath);
    }

    private static void WriteEcPair(string certPath, string keyPath, string subject)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        WritePair(request, ecdsa.ExportPkcs8PrivateKeyPem(), certPath, keyPath);
    }

    private static void WritePair(CertificateRequest request, string privateKeyPem, string certPath, string keyPath)
    {
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, privateKeyPem);
    }
}
