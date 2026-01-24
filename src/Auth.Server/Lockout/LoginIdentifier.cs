using System.Security.Cryptography;
using System.Text;

namespace Auth.Server.Lockout;

public static class LoginIdentifier
{
    /// <summary>
    /// The lockout key of a submitted email: SHA-256 of the email as <c>UserManager.NormalizeEmail</c> returns it, so
    /// the key follows the account lookup exactly. Always 32 bytes; the email itself is never stored (Decision 10).
    /// </summary>
    public static byte[] HashOf(string? normalizedEmail) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail ?? string.Empty));
}
