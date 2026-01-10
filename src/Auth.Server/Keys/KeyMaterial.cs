using System.Security.Cryptography.X509Certificates;

namespace Auth.Server.Keys;

/// <summary>The loaded signing and encryption certificates (each with an RSA private key).</summary>
public sealed record KeyMaterial(X509Certificate2 Signing, X509Certificate2 Encryption);
