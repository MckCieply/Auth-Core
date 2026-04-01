using Auth.Infrastructure.Persistence;
using Auth.Server.Email;

namespace Auth.Server.Signup;

/// <summary>What the completion screen shows before it asks for a password: the address as it was typed and the company name of the request.</summary>
public sealed record SignupPreview(string Email, string CompanyName);

/// <summary>Previews a sign-up by its link token (spec 0009).</summary>
public sealed class SignupService(AuthDbContext db, TimeProvider clock)
{
    /// <summary>
    /// The address and the company name of a usable link; the token stays usable. Unknown, used, expired and replaced tokens are all
    /// <see langword="null"/>. It changes nothing.
    /// </summary>
    public async Task<SignupPreview?> PreviewAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var pending = await PendingSignups.FindUsableAsync(db, EmailTokens.HashOf(token), StorableTime.Now(clock), cancellationToken);
        return pending is null ? null : new SignupPreview(pending.Email, pending.CompanyName);
    }
}
