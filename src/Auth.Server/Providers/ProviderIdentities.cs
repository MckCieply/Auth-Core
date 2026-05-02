using System.Globalization;
using System.Security.Claims;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Providers;

/// <summary>
/// The identity a callback takes from the claims of the provider's identity token, never from userinfo (spec 0011 → The provider identity and
/// its address; Amendment). Google: the key is <c>sub</c>, the address <c>email</c>, verified when <c>email_verified</c> is <c>true</c>.
/// Microsoft: the key is <c>{tid}:{oid}</c> in lower case, the address <c>email</c> (never <c>preferred_username</c>, <c>upn</c> or
/// <c>unique_name</c>), verified when the tenant is that of personal accounts and the address is there, or <c>xms_edov</c> is <c>true</c>; the
/// web-provider registration also requires the issuer of the token's own tenant (Amendment A6). An address that breaks the invitation rule is
/// unverified. <see langword="null"/> means a claim the identity needs is missing: the callback answers <c>provider_error</c>.
/// </summary>
public static class ProviderIdentities
{
    /// <summary>The tenant of personal Microsoft accounts.</summary>
    public const string PersonalTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

    /// <summary>The longest identity key the table holds; Google's <c>sub</c> is at most 255 ASCII characters.</summary>
    public const int MaxKeyLength = 255;

    public static ProviderIdentity? Read(string provider, ClaimsPrincipal? identityToken, ILookupNormalizer normalizer, bool checkMicrosoftIssuer)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(normalizer);

        if (identityToken is null)
        {
            return null;
        }

        return provider switch
        {
            ProviderNames.Google => ReadGoogle(identityToken, normalizer),
            ProviderNames.Microsoft => ReadMicrosoft(identityToken, normalizer, checkMicrosoftIssuer),
            _ => null,
        };
    }

    private static ProviderIdentity? ReadGoogle(ClaimsPrincipal token, ILookupNormalizer normalizer)
    {
        var subject = Single(token, "sub");
        if (string.IsNullOrEmpty(subject) || subject.Length > MaxKeyLength || subject.Any(char.IsControl))
        {
            return null;
        }

        var verified = string.Equals(Single(token, "email_verified"), "true", StringComparison.Ordinal);
        return WithAddress(ProviderNames.Google, subject, Single(token, "email"), verified, normalizer);
    }

    private static ProviderIdentity? ReadMicrosoft(ClaimsPrincipal token, ILookupNormalizer normalizer, bool checkIssuer)
    {
        if (!Guid.TryParse(Single(token, "tid"), out var tenantId) || !Guid.TryParse(Single(token, "oid"), out var objectId))
        {
            return null;
        }

        var tenant = tenantId.ToString("D");
        if (checkIssuer && !string.Equals(
                Single(token, "iss"),
                string.Create(CultureInfo.InvariantCulture, $"https://login.microsoftonline.com/{tenant}/v2.0"),
                StringComparison.Ordinal))
        {
            return null;   // OpenIddict does not check the issuer of the `common` tenant (Amendment A6)
        }

        var email = Single(token, "email");
        var edov = Single(token, "xms_edov");
        var verified = (string.Equals(tenant, PersonalTenant, StringComparison.Ordinal) && !string.IsNullOrEmpty(email))
            || string.Equals(edov, "true", StringComparison.Ordinal)
            || string.Equals(edov, "1", StringComparison.Ordinal);
        return WithAddress(ProviderNames.Microsoft, tenant + ":" + objectId.ToString("D"), email, verified, normalizer);
    }

    /// <summary>
    /// The address as the provider gave it (none only without an <c>email</c> claim), verified only when the provider verified it and it passes the
    /// invitation rule as sign-up applies it; an address that breaks the rule is kept and unverified (spec 0011 → the identity).
    /// </summary>
    private static ProviderIdentity WithAddress(string provider, string key, string? email, bool verified, ILookupNormalizer normalizer)
    {
        if (email is null)
        {
            return new ProviderIdentity(provider, key, null, false);
        }

        var invitable = email.Length is > 0 and <= EmailInput.MaxLength
            && EmailInput.TryNormalize(email, normalizer, out _)
            && EmailInput.IsMailbox(email)
            && EmailInput.IsInvitable(email);
        return new ProviderIdentity(provider, key, email, verified && invitable);
    }

    /// <summary>The value of a claim the token holds exactly once; <see langword="null"/> when it holds none or several.</summary>
    private static string? Single(ClaimsPrincipal token, string type)
    {
        string? found = null;
        foreach (var claim in token.FindAll(type))
        {
            if (found is not null)
            {
                return null;
            }

            found = claim.Value;
        }

        return found;
    }
}
