using System.Security.Claims;
using Auth.Server.Providers;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011, criterion 9: the table of the identity, its address and whether it is verified, row by row.</summary>
public sealed class ProviderIdentityTests
{
    private const string Personal = "9188040d-6c67-4c5b-b112-36a304b66dad";
    private const string Work = "5c1a8d3e-0000-4000-8000-00000000f001";
    private const string ObjectId = "00000000-0000-0000-0000-00000000b001";

    private static readonly UpperInvariantLookupNormalizer Normalizer = new();

    private static (string Type, string Value, string ValueType) Text(string type, string value) => (type, value, ClaimValueTypes.String);

    private static (string Type, string Value, string ValueType) Flag(string type, bool value) =>
        (type, value ? "true" : "false", ClaimValueTypes.Boolean);

    private static (string Type, string Value, string ValueType) Issuer(string tenant) =>
        Text("iss", $"https://login.microsoftonline.com/{tenant}/v2.0");

    private static ClaimsPrincipal Token(params (string Type, string Value, string ValueType)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value, c.ValueType)), "test"));

    private static ProviderIdentity? Google(params (string Type, string Value, string ValueType)[] claims) =>
        ProviderIdentities.Read(ProviderNames.Google, Token(claims), Normalizer, checkMicrosoftIssuer: true);

    private static ProviderIdentity? Microsoft(params (string Type, string Value, string ValueType)[] claims) =>
        ProviderIdentities.Read(ProviderNames.Microsoft, Token(claims), Normalizer, checkMicrosoftIssuer: true);

    private static ProviderIdentity? WorkAccount(string email, (string Type, string Value, string ValueType)? edov = null)
    {
        var claims = new List<(string Type, string Value, string ValueType)> { Issuer(Work), Text("tid", Work), Text("oid", ObjectId), Text("email", email) };
        if (edov is { } given)
        {
            claims.Add(given);
        }

        return Microsoft([.. claims]);
    }

    [Fact]
    public void A_google_identity_is_keyed_by_sub_and_verified_with_email_verified_true()   // criterion 9, Google row
    {
        var identity = Google(Text("sub", "100000000000000000001"), Text("email", "ola@gmail.test"), Flag("email_verified", true));

        Assert.Equal(new ProviderIdentity("google", "100000000000000000001", "ola@gmail.test", true), identity);
    }

    [Theory]
    [InlineData("false", ClaimValueTypes.Boolean)]
    [InlineData("yes", ClaimValueTypes.String)]
    [InlineData("1", ClaimValueTypes.String)]
    public void A_google_address_is_verified_only_with_email_verified_true(string value, string valueType)   // criterion 9
    {
        Assert.False(Google(Text("sub", "1"), Text("email", "ola@gmail.test"), ("email_verified", value, valueType))!.EmailVerified);
        Assert.False(Google(Text("sub", "1"), Text("email", "ola@gmail.test"))!.EmailVerified);
    }

    [Fact]
    public void A_google_identity_without_a_usable_sub_is_refused()   // criterion 8: a missing sub is provider_error
    {
        Assert.Null(Google(Text("email", "ola@gmail.test"), Flag("email_verified", true)));
        Assert.Null(Google(Text("sub", new string('1', 256)), Text("email", "ola@gmail.test")));
        Assert.Null(Google(Text("sub", "1"), Text("sub", "2"), Text("email", "ola@gmail.test")));
        Assert.NotNull(Google(Text("sub", new string('1', 255)), Text("email", "ola@gmail.test")));
    }

    [Fact]
    public void An_identity_without_an_email_claim_has_no_address()   // spec 0011 → the identity: no email is no address
    {
        Assert.Equal(new ProviderIdentity("google", "1", null, false), Google(Text("sub", "1"), Flag("email_verified", true)));
        Assert.Equal(
            new ProviderIdentity("microsoft", $"{Personal}:{ObjectId}", null, false),
            Microsoft(Issuer(Personal), Text("tid", Personal), Text("oid", ObjectId)));
    }

    [Fact]
    public void A_microsoft_identity_is_keyed_by_tenant_and_object_id_in_lower_case_and_never_by_its_address()   // criterion 9, Microsoft row
    {
        var upper = Work.ToUpperInvariant();
        var first = Microsoft(Issuer(Work), Text("tid", upper), Text("oid", ObjectId.ToUpperInvariant()), Text("email", "bo@firma.test"));
        var second = Microsoft(Issuer(Work), Text("tid", Work), Text("oid", ObjectId), Text("email", "bo.new@firma.test"));

        Assert.Equal($"{Work}:{ObjectId}", first!.Key);
        Assert.Equal(first.Key, second!.Key);
        Assert.NotEqual(first.Email, second.Email);
    }

    [Fact]
    public void A_personal_microsoft_address_is_verified()   // criterion 9
    {
        var identity = Microsoft(Issuer(Personal), Text("tid", Personal), Text("oid", ObjectId), Text("email", "ola@outlook.test"));

        Assert.Equal(new ProviderIdentity("microsoft", $"{Personal}:{ObjectId}", "ola@outlook.test", true), identity);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("true", ClaimValueTypes.Boolean, true)]
    [InlineData("true", ClaimValueTypes.String, true)]
    [InlineData("1", ClaimValueTypes.String, true)]
    [InlineData("false", ClaimValueTypes.Boolean, false)]
    [InlineData("yes", ClaimValueTypes.String, false)]
    [InlineData("True", ClaimValueTypes.String, false)]
    [InlineData("0", ClaimValueTypes.String, false)]
    public void A_work_address_is_verified_only_with_xms_edov_true(string? value, string? valueType, bool verified)   // criterion 9, nOAuth
    {
        var identity = WorkAccount("bo@firma.test", value is null ? null : ("xms_edov", value, valueType!));

        Assert.Equal("bo@firma.test", identity!.Email);
        Assert.Equal(verified, identity.EmailVerified);
    }

    [Fact]
    public void Preferred_username_upn_and_unique_name_are_never_read_as_the_address()   // criterion 9
    {
        var identity = Microsoft(
            Issuer(Personal), Text("tid", Personal), Text("oid", ObjectId),
            Text("preferred_username", "ola@outlook.test"), Text("upn", "ola@outlook.test"), Text("unique_name", "ola@outlook.test"));

        Assert.Null(identity!.Email);
        Assert.False(identity.EmailVerified);
    }

    [Theory]
    [InlineData(null, ObjectId)]
    [InlineData(Work, null)]
    [InlineData("not-a-guid", ObjectId)]
    [InlineData(Work, "12345")]
    public void A_microsoft_identity_without_a_tenant_or_an_object_id_is_refused(string? tenant, string? objectId)   // criterion 8: tid/oid missing
    {
        var claims = new List<(string Type, string Value, string ValueType)> { Issuer(tenant ?? Work), Text("email", "bo@firma.test") };
        if (tenant is not null)
        {
            claims.Add(Text("tid", tenant));
        }

        if (objectId is not null)
        {
            claims.Add(Text("oid", objectId));
        }

        Assert.Null(Microsoft([.. claims]));
    }

    [Fact]
    public void A_microsoft_token_whose_issuer_is_not_that_of_its_tenant_is_refused_for_the_web_provider()   // Amendment A6, criterion 9
    {
        (string Type, string Value, string ValueType)[] claims =
            [Issuer("0b0b0b0b-0000-4000-8000-0000000000aa"), Text("tid", Work), Text("oid", ObjectId), Text("email", "bo@firma.test")];

        Assert.Null(ProviderIdentities.Read(ProviderNames.Microsoft, Token(claims), Normalizer, checkMicrosoftIssuer: true));
        Assert.Null(ProviderIdentities.Read(ProviderNames.Microsoft, Token(claims[1..]), Normalizer, checkMicrosoftIssuer: true));
        Assert.NotNull(ProviderIdentities.Read(ProviderNames.Microsoft, Token(claims), Normalizer, checkMicrosoftIssuer: false));   // the test issuer: OpenIddict checks it
    }

    [Theory]
    [InlineData("ola@localhost")]
    [InlineData("ola@10.0.0.1")]
    [InlineData("Ola <ola@gmail.test>")]
    [InlineData("ola@intranet.123")]
    public void An_address_that_breaks_the_invitation_rule_is_kept_and_unverified(string email)   // criterion 9
    {
        var identity = Google(Text("sub", "1"), Text("email", email), Flag("email_verified", true));

        Assert.Equal(email, identity!.Email);
        Assert.False(identity.EmailVerified);
    }

    [Fact]
    public void An_email_claim_that_is_no_mailbox_is_kept_and_unverified()   // spec 0011: only no `email` claim is no address
    {
        var control = "ola" + (char)0x01 + "@gmail.test";
        var tooLong = new string('a', 250) + "@gmail.test";

        foreach (var email in new[] { control, tooLong, "" })
        {
            var identity = Google(Text("sub", "1"), Text("email", email), Flag("email_verified", true))!;
            Assert.Equal(email, identity.Email);
            Assert.False(identity.EmailVerified);
        }
    }

    [Fact]
    public void An_unknown_provider_or_no_token_is_refused()
    {
        Assert.Null(ProviderIdentities.Read("github", Token(Text("sub", "1")), Normalizer, checkMicrosoftIssuer: true));
        Assert.Null(ProviderIdentities.Read(ProviderNames.Google, null, Normalizer, checkMicrosoftIssuer: true));
    }

    private static readonly SymmetricSecurityKey SigningKey = new(new byte[32]);

    /// <summary>The principal OpenIddict's client reads: a signed JWT validated by <see cref="JsonWebTokenHandler"/>, its claims as that handler writes them.</summary>
    private static async Task<ClaimsPrincipal> FromRealTokenAsync(string payloadJson)
    {
        var handler = new JsonWebTokenHandler();
        var jwt = handler.CreateToken(payloadJson, new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        var result = await handler.ValidateTokenAsync(jwt, new TokenValidationParameters
        {
            IssuerSigningKey = SigningKey,
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        return new ClaimsPrincipal(result.ClaimsIdentity);
    }

    private static string WorkPayload(string verifiedMember) =>
        $$"""{"iss":"https://login.microsoftonline.com/{{Work}}/v2.0","tid":"{{Work}}","oid":"{{ObjectId}}","email":"bo@firma.test",{{verifiedMember}}}""";

    [Theory]
    [InlineData("\"xms_edov\":true", true)]
    [InlineData("\"xms_edov\":\"true\"", true)]
    [InlineData("\"xms_edov\":\"1\"", true)]
    [InlineData("\"xms_edov\":false", false)]
    [InlineData("\"xms_edov\":\"True\"", false)]
    [InlineData("\"xms_edov\":[true,true]", false)]
    [InlineData("\"other\":1", false)]
    public async Task A_microsoft_token_read_from_a_real_jwt_is_verified_by_a_json_boolean_xms_edov(string member, bool verified)   // criterion 9
    {
        var principal = await FromRealTokenAsync(WorkPayload(member));

        var identity = ProviderIdentities.Read(ProviderNames.Microsoft, principal, Normalizer, checkMicrosoftIssuer: true);

        Assert.Equal(new ProviderIdentity("microsoft", $"{Work}:{ObjectId}", "bo@firma.test", verified), identity);
    }

    [Theory]
    [InlineData("\"email_verified\":true", true)]
    [InlineData("\"email_verified\":\"true\"", true)]
    [InlineData("\"email_verified\":false", false)]
    [InlineData("\"email_verified\":\"True\"", false)]
    [InlineData("\"email_verified\":[true,true]", false)]
    [InlineData("\"other\":1", false)]
    public async Task A_google_token_read_from_a_real_jwt_is_verified_by_a_json_boolean_email_verified(string member, bool verified)   // criterion 9
    {
        var principal = await FromRealTokenAsync($$"""{"sub":"100000000000000000001","email":"ola@gmail.test",{{member}}}""");

        var identity = ProviderIdentities.Read(ProviderNames.Google, principal, Normalizer, checkMicrosoftIssuer: true);

        Assert.Equal(new ProviderIdentity("google", "100000000000000000001", "ola@gmail.test", verified), identity);
    }

    [Fact]
    public async Task A_personal_microsoft_token_read_from_a_real_jwt_is_verified_and_a_doubled_email_there_is_no_address()   // criterion 9
    {
        var single = await FromRealTokenAsync(
            $$"""{"iss":"https://login.microsoftonline.com/{{Personal}}/v2.0","tid":"{{Personal}}","oid":"{{ObjectId}}","email":"ola@outlook.test"}""");
        var doubled = await FromRealTokenAsync(
            $$"""{"iss":"https://login.microsoftonline.com/{{Personal}}/v2.0","tid":"{{Personal}}","oid":"{{ObjectId}}","email":["ola@outlook.test","bo@outlook.test"]}""");

        Assert.Equal(
            new ProviderIdentity("microsoft", $"{Personal}:{ObjectId}", "ola@outlook.test", true),
            ProviderIdentities.Read(ProviderNames.Microsoft, single, Normalizer, checkMicrosoftIssuer: true));
        Assert.Equal(
            new ProviderIdentity("microsoft", $"{Personal}:{ObjectId}", null, false),
            ProviderIdentities.Read(ProviderNames.Microsoft, doubled, Normalizer, checkMicrosoftIssuer: true));
    }

    [Theory]
    [InlineData("google", "sub", "refused")]
    [InlineData("google", "email", "no_address")]
    [InlineData("google", "email_verified", "unverified")]
    [InlineData("microsoft", "tid", "refused")]
    [InlineData("microsoft", "oid", "refused")]
    [InlineData("microsoft", "iss", "refused")]
    [InlineData("microsoft", "email", "no_address")]
    [InlineData("microsoft", "xms_edov", "unverified")]
    public void A_claim_given_twice_reads_as_missing(string provider, string type, string expected)   // plan decision 5
    {
        var claims = provider == ProviderNames.Google
            ? new List<(string Type, string Value, string ValueType)>
            {
                Text("sub", "1"), Text("email", "ola@gmail.test"), Flag("email_verified", true),
            }
            : [Issuer(Work), Text("tid", Work), Text("oid", ObjectId), Text("email", "bo@firma.test"), Flag("xms_edov", true)];
        claims.Add(claims.First(c => c.Type == type));

        var identity = ProviderIdentities.Read(provider, Token([.. claims]), Normalizer, checkMicrosoftIssuer: true);

        if (expected == "refused")
        {
            Assert.Null(identity);
            return;
        }

        Assert.NotNull(identity);
        Assert.False(identity.EmailVerified);
        Assert.Equal(expected == "no_address", identity.Email is null);
    }

    [Theory]
    [InlineData("bo@localhost")]
    [InlineData("bo@10.0.0.1")]
    [InlineData("Bo <bo@firma.test>")]
    [InlineData("bo@intranet.123")]
    [InlineData("")]
    public void A_microsoft_address_that_breaks_the_invitation_rule_is_kept_and_unverified_in_both_ways_of_being_verified(string email)   // criterion 9
    {
        var personal = Microsoft(Issuer(Personal), Text("tid", Personal), Text("oid", ObjectId), Text("email", email));
        var work = WorkAccount(email, Flag("xms_edov", true));

        Assert.Equal(email, personal!.Email);
        Assert.False(personal.EmailVerified);
        Assert.Equal(email, work!.Email);
        Assert.False(work.EmailVerified);
    }

    [Fact]
    public void The_personal_tenant_is_recognised_whatever_the_case_of_tid()   // criterion 9: tid is normalised before the comparison
    {
        var identity = Microsoft(Issuer(Personal), Text("tid", Personal.ToUpperInvariant()), Text("oid", ObjectId), Text("email", "ola@outlook.test"));

        Assert.Equal(new ProviderIdentity("microsoft", $"{Personal}:{ObjectId}", "ola@outlook.test", true), identity);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/{tid}/v2.0/")]
    [InlineData("http://login.microsoftonline.com/{tid}/v2.0")]
    [InlineData("https://login.microsoftonline.com/{TID}/v2.0")]
    [InlineData("https://login.microsoftonline.com/{tid}/v2.0 ")]
    [InlineData("https://login.microsoftonline.com/{tid}")]
    [InlineData("https://login.microsoftonline.com/common/v2.0")]
    [InlineData("https://login.microsoftonline.com:443/{tid}/v2.0")]
    [InlineData("https://login.microsoftonline.de/{tid}/v2.0")]
    [InlineData("https://login.microsoft.com/{tid}/v2.0")]
    [InlineData("https://evil.test/login.microsoftonline.com/{tid}/v2.0")]
    [InlineData("https://sts.windows.net/{tid}/")]
    [InlineData("")]
    public void An_issuer_that_is_not_exactly_that_of_the_tenant_is_refused_for_the_web_provider(string issuerTemplate)   // Amendment A6
    {
        var issuer = issuerTemplate.Replace("{tid}", Work, StringComparison.Ordinal).Replace("{TID}", Work.ToUpperInvariant(), StringComparison.Ordinal);

        var lowerTid = Microsoft(Text("iss", issuer), Text("tid", Work), Text("oid", ObjectId), Text("email", "bo@firma.test"));
        var upperTid = Microsoft(Text("iss", issuer), Text("tid", Work.ToUpperInvariant()), Text("oid", ObjectId), Text("email", "bo@firma.test"));

        Assert.Null(lowerTid);
        Assert.Null(upperTid);
    }

    [Fact]
    public void The_exact_issuer_is_accepted_whatever_the_case_of_the_tid_claim()   // Amendment A6: the issuer is compared with tid in lower case
    {
        var identity = Microsoft(Issuer(Work), Text("tid", Work.ToUpperInvariant()), Text("oid", ObjectId), Text("email", "bo@firma.test"));

        Assert.NotNull(identity);
    }
}
