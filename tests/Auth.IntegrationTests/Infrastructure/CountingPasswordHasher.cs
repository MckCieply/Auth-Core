using System.Collections.Concurrent;
using Auth.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>The real password hasher, recording the stored hash of every verification it runs.</summary>
public sealed class CountingPasswordHasher : PasswordHasher<ApplicationUser>
{
    private readonly ConcurrentQueue<string> _verifiedHashes = new();

    public IReadOnlyCollection<string> VerifiedHashes => _verifiedHashes;

    public override PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
    {
        _verifiedHashes.Enqueue(hashedPassword);
        return base.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}
