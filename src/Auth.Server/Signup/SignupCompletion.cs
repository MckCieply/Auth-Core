using Auth.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore.Storage;

namespace Auth.Server.Signup;

/// <summary>
/// How <see cref="SignupService.CompleteAsync"/> ended. A refusal has an <see cref="Error"/> (and, for a weak password, the rules it breaks in
/// <see cref="BrokenRules"/>). Success has the new <see cref="Account"/>, the id of the space it was made for (the company of an organization,
/// the personal space of a private person), and the <see cref="Transaction"/> that still holds them, <b>not committed</b>: whoever receives it
/// signs the person in inside it and commits (spec 0009, Amendment 2); letting it go rolls everything back.
/// </summary>
public sealed record SignupCompletion(
    string? Error = null,
    IReadOnlyList<string>? BrokenRules = null,
    ApplicationUser? Account = null,
    Guid CompanyId = default,
    IDbContextTransaction? Transaction = null)
{
    public bool Succeeded => Error is null;
}
