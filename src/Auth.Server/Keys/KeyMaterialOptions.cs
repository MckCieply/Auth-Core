namespace Auth.Server.Keys;

/// <summary>
/// Locations of the mounted PEM files holding the signing and encryption key material,
/// bound from the <c>Auth:Keys</c> configuration section. All four paths are required.
/// </summary>
public sealed class KeyMaterialOptions
{
    public const string SectionName = "Auth:Keys";

    public const string SigningCertificatePathKey = SectionName + ":" + nameof(SigningCertificatePath);
    public const string SigningKeyPathKey = SectionName + ":" + nameof(SigningKeyPath);
    public const string EncryptionCertificatePathKey = SectionName + ":" + nameof(EncryptionCertificatePath);
    public const string EncryptionKeyPathKey = SectionName + ":" + nameof(EncryptionKeyPath);

    /// <summary>PEM X.509 certificate used to sign tokens (key usage: digitalSignature).</summary>
    public string? SigningCertificatePath { get; set; }

    /// <summary>PEM PKCS#8 RSA private key matching <see cref="SigningCertificatePath"/>.</summary>
    public string? SigningKeyPath { get; set; }

    /// <summary>PEM X.509 certificate used to encrypt tokens (key usage: keyEncipherment).</summary>
    public string? EncryptionCertificatePath { get; set; }

    /// <summary>PEM PKCS#8 RSA private key matching <see cref="EncryptionCertificatePath"/>.</summary>
    public string? EncryptionKeyPath { get; set; }
}
