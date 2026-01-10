using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Auth.Server.Keys;

/// <summary>
/// Loads key material from mounted PEM files and fails fast on anything missing or broken.
/// Never falls back to an ephemeral or development certificate: that would silently break
/// token verification across restarts and replicas.
/// </summary>
public static class KeyMaterialLoader
{
    /// <summary>Smallest accepted RSA modulus size, in bits.</summary>
    public const int MinimumRsaKeySizeBits = 2048;

    /// <summary>
    /// Loads a certificate and its RSA private key from a PEM pair.
    /// </summary>
    /// <param name="certPath">Path of the PEM certificate file.</param>
    /// <param name="keyPath">Path of the PEM private key file.</param>
    /// <param name="configKey">
    /// Configuration key to blame in error messages (for example <c>Auth:Keys:SigningKeyPath</c>).
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A path is blank, a file is missing or unreadable, or the key is not an RSA private key of at least 2048 bits.
    /// Messages name <paramref name="configKey"/> only; they never include file contents or key bytes.
    /// </exception>
    public static X509Certificate2 Load(string? certPath, string? keyPath, string configKey)
    {
        if (string.IsNullOrWhiteSpace(certPath) || string.IsNullOrWhiteSpace(keyPath))
        {
            throw Fail(configKey, "a certificate path and a key path are both required and one is missing or blank");
        }

        if (!File.Exists(certPath) || !File.Exists(keyPath))
        {
            throw Fail(configKey, "the certificate or key file does not exist");
        }

        X509Certificate2 cert;
        try
        {
            cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // The inner exception is deliberately dropped: it may carry parser output derived from the key.
            throw Fail(configKey, "the certificate or key file could not be read as PEM");
        }

        try
        {
            if (!cert.HasPrivateKey)
            {
                throw Fail(configKey, "no private key was found");
            }

            using var rsa = cert.GetRSAPrivateKey();
            if (rsa is null)
            {
                throw Fail(configKey, "the private key is not an RSA key (RS256 requires RSA)");
            }

            if (rsa.KeySize < MinimumRsaKeySizeBits)
            {
                throw Fail(configKey, $"the RSA key is shorter than the required {MinimumRsaKeySizeBits} bits");
            }

            return cert;
        }
        catch
        {
            cert.Dispose();
            throw;
        }
    }

    /// <summary>Loads both the signing and the encryption certificate from the <c>Auth:Keys</c> section.</summary>
    public static KeyMaterial LoadAll(IConfiguration configuration)
    {
        var options = configuration.GetSection(KeyMaterialOptions.SectionName).Get<KeyMaterialOptions>()
            ?? new KeyMaterialOptions();

        var signing = Load(options.SigningCertificatePath, options.SigningKeyPath, KeyMaterialOptions.SigningKeyPathKey);
        try
        {
            var encryption = Load(options.EncryptionCertificatePath, options.EncryptionKeyPath, KeyMaterialOptions.EncryptionKeyPathKey);
            return new KeyMaterial(signing, encryption);
        }
        catch
        {
            signing.Dispose();
            throw;
        }
    }

    private static InvalidOperationException Fail(string configKey, string reason) =>
        new($"Key material configured at '{configKey}' is unusable: {reason}.");
}
