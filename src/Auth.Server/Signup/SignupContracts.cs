namespace Auth.Server.Signup;

// The bodies of the sign-up endpoints (spec 0009 → Contract). Property names become snake_case on the wire. The endpoints read and write
// these bodies by hand, so the types document the contract in OpenAPI; they do not enforce it.

/// <summary>
/// Response of <c>GET /auth/options</c>: <c>signup</c> (spec 0009) and <c>private_signup</c> (spec 0010), each following its switch. A client
/// ignores members it does not know.
/// </summary>
public sealed record OptionsResponse(string Signup, string PrivateSignup);

/// <summary>Request of <c>POST /auth/signup</c>.</summary>
public sealed record SignupRequest(string Email, string CompanyName);

/// <summary>Request of <c>POST /auth/signup/preview</c>.</summary>
public sealed record SignupPreviewRequest(string Token);

/// <summary>
/// Response of <c>POST /auth/signup/preview</c>: the address as typed, the company name of the request, and whether an account without a
/// company has the address (spec 0009, Amendment 3): then the completion needs no password.
/// </summary>
public sealed record SignupPreviewResponse(string Email, string CompanyName, bool HasAccount);

/// <summary>
/// Request of <c>POST /auth/signup/complete</c>. The company name is the person's last word; the one of the request is a suggestion. The
/// password is chosen here, except by an account that has no company (the preview says <c>has_account</c>): it may be left out, and when it
/// is sent it is not used.
/// </summary>
public sealed record SignupCompleteRequest(string Token, string CompanyName, string? Password = null);
