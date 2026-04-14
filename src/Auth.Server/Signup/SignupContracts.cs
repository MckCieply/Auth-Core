using System.Text.Json.Serialization;

namespace Auth.Server.Signup;

// The bodies of the sign-up endpoints (spec 0009 → Contract). Property names become snake_case on the wire. The endpoints read and write
// these bodies by hand, so the types document the contract in OpenAPI; they do not enforce it.

/// <summary>
/// Response of <c>GET /auth/options</c>: <c>signup</c> (spec 0009) and <c>private_signup</c> (spec 0010), each following its switch. A client
/// ignores members it does not know.
/// </summary>
public sealed record OptionsResponse(string Signup, string PrivateSignup);

/// <summary>
/// Request of <c>POST /auth/signup</c>: <c>kind</c> is <c>organization</c> (the default) or <c>private</c> (spec 0010); <c>company_name</c> is
/// required for an organization and refused for a private person.
/// </summary>
public sealed record SignupRequest(string Email, string? Kind = null, string? CompanyName = null);

/// <summary>Request of <c>POST /auth/signup/preview</c>.</summary>
public sealed record SignupPreviewRequest(string Token);

/// <summary>
/// Response of <c>POST /auth/signup/preview</c> (spec 0010): <c>{"kind":"organization","email","company_name"}</c> or
/// <c>{"kind":"private","email"}</c>.
/// </summary>
public sealed record SignupPreviewResponse(
    string Kind, string Email, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CompanyName = null);

/// <summary>
/// Request of <c>POST /auth/signup/complete</c>: the token and the password the person chooses, and for an organization the company name (the
/// person's last word; the one of the request is a suggestion). A private completion has no company name (spec 0010).
/// </summary>
public sealed record SignupCompleteRequest(string Token, string? Password = null, string? CompanyName = null);
