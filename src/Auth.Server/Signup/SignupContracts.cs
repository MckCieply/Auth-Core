namespace Auth.Server.Signup;

// The bodies of the sign-up endpoints (spec 0009 → Contract). Property names become snake_case on the wire. The endpoints read and write
// these bodies by hand, so the types document the contract in OpenAPI; they do not enforce it.

/// <summary>Response of <c>GET /auth/options</c>. A client ignores members it does not know (slice 0010 adds <c>providers</c>).</summary>
public sealed record OptionsResponse(string Signup);

/// <summary>Request of <c>POST /auth/signup</c>.</summary>
public sealed record SignupRequest(string Email, string CompanyName);

/// <summary>Request of <c>POST /auth/signup/preview</c>.</summary>
public sealed record SignupPreviewRequest(string Token);

/// <summary>Response of <c>POST /auth/signup/preview</c>: the address as typed and the company name of the request.</summary>
public sealed record SignupPreviewResponse(string Email, string CompanyName);

/// <summary>Request of <c>POST /auth/signup/complete</c>. The company name is the person's last word; the one of the request is a suggestion.</summary>
public sealed record SignupCompleteRequest(string Token, string Password, string CompanyName);
