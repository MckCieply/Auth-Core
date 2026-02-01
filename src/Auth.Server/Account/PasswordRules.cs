using Auth.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Account;

/// <summary>
/// Turns what ASP.NET Identity's validators say about a password into the rule names of the contract
/// (spec 0004 → Password policy). The names are ours and stable; Identity's codes and texts are not sent.
/// </summary>
public static class PasswordRules
{
    private static readonly string[] Order = ["too_short", "requires_upper", "requires_lower", "requires_digit"];

    /// <summary>The rules <paramref name="password"/> breaks, in contract order; empty when it is acceptable.</summary>
    public static async Task<IReadOnlyList<string>> BrokenAsync(UserManager<ApplicationUser> users, ApplicationUser user, string password)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(user);

        var broken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, user, password);
            foreach (var error in result.Errors)
            {
                broken.Add(error.Code switch
                {
                    nameof(IdentityErrorDescriber.PasswordTooShort) => "too_short",
                    nameof(IdentityErrorDescriber.PasswordRequiresUpper) => "requires_upper",
                    nameof(IdentityErrorDescriber.PasswordRequiresLower) => "requires_lower",
                    nameof(IdentityErrorDescriber.PasswordRequiresDigit) => "requires_digit",
                    // A rule the contract has no name for means the policy and this mapping have drifted apart.
                    _ => throw new InvalidOperationException($"Password rule '{error.Code}' has no name in the contract."),
                });
            }
        }

        return [.. Order.Where(broken.Contains)];
    }
}
