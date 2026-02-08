using Auth.Server.Account;
using Auth.Server.Lockout;
using Auth.Server.Tenancy;

namespace Auth.Server.Api;

/// <summary>
/// The responses of the company API and the invitation endpoints (spec 0005 → General rules): never stored, never a
/// cookie, an error is <c>{"error":"&lt;code&gt;"}</c>. The same results as the account endpoints use, so the headers
/// are the same.
/// </summary>
public static class ApiResults
{
    public static IResult Ok(object body) => new AccountResults.NoStoreResult(StatusCodes.Status200OK, body);

    public static IResult Created(object body) => new AccountResults.NoStoreResult(StatusCodes.Status201Created, body);

    public static IResult Accepted() => AccountResults.Accepted();

    public static IResult NoContent() => AccountResults.Done();

    /// <summary>The error with this code, at the status the contract gives it.</summary>
    public static IResult Error(string code) =>
        new AccountResults.NoStoreResult(TenancyErrors.StatusOf(code), new { error = code });

    /// <summary>A refusal of a service: its code, or the <c>429</c> of the mail limit with the time to wait.</summary>
    public static IResult Refused(Outcome outcome)
    {
        if (outcome.Succeeded)
        {
            throw new InvalidOperationException("A successful outcome is not a refusal.");
        }

        return outcome.Error == TenancyErrors.TooManyAttempts
            ? new TooManyAttemptsResult(outcome.RetryAfter)
            : Error(outcome.Error!);
    }

    /// <summary>The answer for a request body that is not what the endpoint takes.</summary>
    public static IResult InvalidRequest() => Error(TenancyErrors.InvalidRequest);

    /// <summary>The answer for an id that does not exist in the caller's company.</summary>
    public static IResult NotFound() => Error(TenancyErrors.NotFound);
}
