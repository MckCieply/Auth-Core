using System.Security.Cryptography;
using Auth.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Lockout;

/// <summary>
/// The hash a submitted password is verified against when there is no account, or no password, to verify it
/// against, so that such a login costs what a wrong password costs (spec 0003, Decision 5). It is made once, by
/// the configured hasher, so its cost parameters are the ones real hashes get (Decision 11). The password behind
/// it is random and discarded: no input verifies.
/// </summary>
public sealed class DecoyPasswordHash
{
    public DecoyPasswordHash(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        // The hasher is registered as a scoped service; this singleton must not hold on to it.
        using var scope = scopes.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<ApplicationUser>>();
        Value = hasher.HashPassword(new ApplicationUser(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public string Value { get; }
}
