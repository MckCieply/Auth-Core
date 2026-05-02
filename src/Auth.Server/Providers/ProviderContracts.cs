namespace Auth.Server.Providers;

// The bodies of the provider endpoints (spec 0011 → Contract). Property names become snake_case on the wire. The endpoints read and write them by
// hand; the types document the contract in OpenAPI.

/// <summary>Request of <c>POST /auth/providers/exchange</c>.</summary>
public sealed record ProviderTicketRequest(string Ticket);

/// <summary>Request of <c>POST /auth/providers/link</c>: the ticket and the password of the account with the address.</summary>
public sealed record ProviderLinkRequest(string Ticket, string Password);

/// <summary>Request of <c>POST /auth/providers/finish</c>: <c>kind</c> as at <c>POST /auth/signup</c> (absent: organization), and the company name of an organization.</summary>
public sealed record ProviderFinishRequest(string Ticket, string? Kind = null, string? CompanyName = null);

/// <summary>What the exchange says comes next.</summary>
public static class ProviderNext
{
    public const string Link = "link";
    public const string FinishSignup = "finish_signup";
}

/// <summary><c>{"next":"link","email","email_verified"}</c>.</summary>
public sealed record ProviderLinkNext(string Next, string Email, bool EmailVerified);

/// <summary><c>{"next":"finish_signup","email","kinds"}</c>, <c>organization</c> first.</summary>
public sealed record ProviderFinishNext(string Next, string Email, IReadOnlyList<string> Kinds);

/// <summary>
/// For the OpenAPI description only: the <c>200</c> of the exchange is either a sign-in (<c>status</c>, <c>access_token</c>, as a login answers)
/// or what comes next (<c>next</c>, <c>email</c>, and <c>email_verified</c> or <c>kinds</c>).
/// </summary>
public sealed record ProviderExchangeResponse(
    string? Status = null, string? AccessToken = null, string? Next = null, string? Email = null, bool? EmailVerified = null,
    IReadOnlyList<string>? Kinds = null);
