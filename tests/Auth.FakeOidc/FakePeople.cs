using System.Security.Cryptography;
using System.Text;

namespace Auth.FakeOidc;

/// <summary>The two providers the fake plays.</summary>
public static class FakeKinds
{
    public const string Google = "google";
    public const string Microsoft = "microsoft";
}

/// <summary>What can go wrong on demand (<c>defect</c>): the cases of spec 0011, criterion 8.</summary>
public static class FakeDefects
{
    public const string NoSubject = "no_sub";
    public const string NoTenant = "no_tid";
    public const string NoObjectId = "no_oid";
    public const string NoEmail = "no_email";
    public const string WrongAudience = "wrong_audience";
    public const string WrongNonce = "wrong_nonce";
    public const string WrongIssuer = "wrong_issuer";
    public const string BadSignature = "bad_signature";
    public const string RefuseCode = "refuse_code";

    /// <summary>The token endpoint answers <c>invalid_client</c>, as a provider does for a wrong or lapsed client secret (spec 0011 → the runbook).</summary>
    public const string RefuseClient = "refuse_client";

    /// <summary>The authorization endpoint answers another error than <c>access_denied</c> (<c>server_error</c>).</summary>
    public const string ProviderError = "provider_error";

    /// <summary>Whether this is one of the defects above; the fake refuses any other name, so a typo never yields a good identity token.</summary>
    public static bool IsKnown(string defect) => defect is NoSubject or NoTenant or NoObjectId or NoEmail or WrongAudience or WrongNonce
        or WrongIssuer or BadSignature or RefuseCode or RefuseClient or ProviderError;
}

/// <summary>
/// The people of spec 0011 (Decision 20): a Google person, a Microsoft personal account and a Microsoft work account whose address the tenant did not
/// verify; a free address (<see cref="Custom"/>); and <see cref="Cancel"/>.
/// </summary>
public static class FakePeople
{
    public const string Google = "google";
    public const string MicrosoftPersonal = "microsoft-personal";
    public const string MicrosoftWork = "microsoft-work";
    public const string Custom = "custom";
    public const string Cancel = "cancel";

    public const string PersonalTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";
    public const string WorkTenant = "5c1a8d3e-0000-4000-8000-00000000f001";

    public const string GoogleSubject = "100000000000000000001";
    public const string GoogleEmail = "ola@gmail.test";
    public const string PersonalObjectId = "00000000-0000-0000-0000-00000000a001";
    public const string PersonalEmail = "ola@outlook.test";
    public const string WorkObjectId = "00000000-0000-0000-0000-00000000b001";
    public const string WorkEmail = "bo@firma.test";
    public const string WorkUserName = "bo@firma.onmicrosoft.test";

    /// <summary>
    /// The claims of the identity token of this person at this provider (<see cref="FakeKinds"/>), before the fake adds <c>iss</c>, <c>aud</c>,
    /// <c>nonce</c> and the times; <see langword="null"/> for a person this provider does not have, or when a claim defect names a claim the person lacks.
    /// </summary>
    public static Dictionary<string, object>? ClaimsFor(string kind, FakeSignIn signIn)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(signIn);

        var claims = (kind, signIn.Person) switch
        {
            (FakeKinds.Google, Google) => GooglePerson(signIn.Subject ?? GoogleSubject, signIn.Email ?? GoogleEmail, signIn.Verified ?? true),
            (FakeKinds.Google, Custom) when signIn.Email is { } email =>
                GooglePerson(signIn.Subject ?? "custom-" + Digest(email)[..20], email, signIn.Verified ?? false),
            (FakeKinds.Microsoft, MicrosoftPersonal) =>
                MicrosoftPerson(PersonalTenant, signIn.Subject ?? PersonalObjectId, signIn.Email ?? PersonalEmail, signIn.Email ?? PersonalEmail, signIn.Edov),
            (FakeKinds.Microsoft, MicrosoftWork) =>
                MicrosoftPerson(signIn.Tenant ?? WorkTenant, signIn.Subject ?? WorkObjectId, signIn.Email ?? WorkEmail, WorkUserName, signIn.Edov),
            (FakeKinds.Microsoft, Custom) when signIn.Email is { } email =>
                MicrosoftPerson(signIn.Tenant ?? WorkTenant, signIn.Subject ?? GuidOf(email), email, email, signIn.Verified == true ? "true" : signIn.Edov),
            _ => null,
        };
        if (claims is null)
        {
            return null;
        }

        var missing = signIn.Defect switch
        {
            FakeDefects.NoSubject => "sub",
            FakeDefects.NoTenant => "tid",
            FakeDefects.NoObjectId => "oid",
            FakeDefects.NoEmail => "email",
            _ => null,
        };
        if (missing is not null && !claims.Remove(missing))
        {
            return null;   // a defect that cannot apply to this person (no_tid for a Google person) is refused, not ignored
        }

        return claims;
    }

    private static Dictionary<string, object> GooglePerson(string subject, string email, bool verified) => new(StringComparer.Ordinal)
    {
        ["sub"] = subject,
        ["email"] = email,
        ["email_verified"] = verified,
        ["name"] = "Ola Test",
    };

    private static Dictionary<string, object> MicrosoftPerson(string tenant, string objectId, string email, string userName, string? edov)
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = "pairwise-" + objectId,
            ["oid"] = objectId,
            ["tid"] = tenant,
            ["email"] = email,
            ["preferred_username"] = userName,
            ["name"] = "Test Person",
            ["ver"] = "2.0",
        };
        if (!string.Equals(tenant, PersonalTenant, StringComparison.Ordinal))
        {
            claims["upn"] = userName;
        }

        if (edov is not null)
        {
            claims["xms_edov"] = edov switch
            {
                "true" => true,
                "false" => false,
                _ => edov,
            };
        }

        return claims;
    }

    private static byte[] Hash(string email) => SHA256.HashData(Encoding.UTF8.GetBytes(email.ToUpperInvariant()));

    private static string Digest(string email) => Convert.ToHexStringLower(Hash(email));

    private static string GuidOf(string email) => new Guid(Hash(email).AsSpan(0, 16)).ToString("D");
}
