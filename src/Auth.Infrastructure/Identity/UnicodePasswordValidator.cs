using Microsoft.AspNetCore.Identity;

namespace Auth.Infrastructure.Identity;

/// <summary>
/// ASP.NET Identity's password validator with one difference: a letter or a digit of any script counts, not only
/// A–Z, a–z and 0–9. With the built-in rule a password such as "zażółć12Ż" has no uppercase letter (spec 0004,
/// Decision 20). The policy itself (length, which kinds of character are required) stays in
/// <see cref="PasswordOptions"/>.
/// </summary>
public sealed class UnicodePasswordValidator(IdentityErrorDescriber? errors = null) : PasswordValidator<ApplicationUser>(errors)
{
    public override bool IsUpper(char c) => char.IsUpper(c);

    public override bool IsLower(char c) => char.IsLower(c);

    public override bool IsDigit(char c) => char.IsDigit(c);

    public override bool IsLetterOrDigit(char c) => char.IsLetterOrDigit(c);
}
