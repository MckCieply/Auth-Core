namespace Auth.Server.Api;

// The bodies of the endpoints of specs 0001–0004, as the OpenAPI description names them. Property names become snake_case on
// the wire. The endpoints read and write these bodies by hand, so these types document the contract; they do not enforce it.

/// <summary>Request of <c>POST /auth/login</c>.</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>Response of <c>POST /auth/login</c>. The refresh token travels in the <c>auth_rt</c> cookie, never in the body.</summary>
public sealed record LoginResponse(string Status, string AccessToken);

/// <summary>Response of <c>POST /auth/refresh</c>.</summary>
public sealed record RefreshResponse(string AccessToken);

/// <summary>Request of <c>POST /auth/password/forgot</c> and <c>POST /auth/email/verify/request</c>.</summary>
public sealed record EmailRequest(string Email);

/// <summary>Request of <c>POST /auth/password/reset</c>.</summary>
public sealed record ResetPasswordRequest(string Token, string NewPassword);

/// <summary>Request of <c>POST /auth/email/verify</c>.</summary>
public sealed record TokenRequest(string Token);

/// <summary>The <c>429</c> of the lockout (spec 0003) and of the mail limit (spec 0004); <c>Retry-After</c> carries the same number.</summary>
public sealed record TooManyAttemptsBody(string Error, int RetryAfterSeconds);
