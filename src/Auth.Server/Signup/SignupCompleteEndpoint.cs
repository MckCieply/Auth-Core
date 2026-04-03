using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Login;
using Auth.Server.Requests;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.Extensions.Options;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Signup;

/// <summary>
/// <c>POST /auth/signup/complete</c> (spec 0009): finishes a sign-up and signs the person in (Amendment 2). No access token: the link token is the
/// credential. While sign-up is off it answers <c>403 signup_disabled</c> before it reads the body — also for a request that was made while it
/// was on. The answer is that of a successful login (<c>200 {"status":"authenticated","access_token"}</c> and the refresh cookie): the endpoint is a
/// pass-through request of OpenIddict (<see cref="SignupCompleteRequestHandler"/>), builds the principal the way the login does
/// (<see cref="SessionPrincipal"/>) and returns an <see cref="AtomicSignInResult"/> that issues the session, writes <c>login.succeeded</c> and
/// commits the transaction of the completion. If any of that fails, nothing of the completion stays.
/// </summary>
public static class SignupCompleteEndpoint
{
    public const string Path = "/auth/signup/complete";

    private static readonly string[] Fields = ["token", "password", "company_name"];

    public static async Task<IResult> HandleAsync(
        HttpContext http, SignupGate gate, SignupService signup, MembershipReader memberships, IOptions<TokenOptions> tokens, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(signup);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);

        // Without it OpenIddict cannot sign in here (it would say so only after the account was made, and roll that back): fail at once.
        if (http.GetOpenIddictServerRequest() is null)
        {
            throw new InvalidOperationException(
                "The sign-up completion was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        }

        if (!gate.IsOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, http.RequestAborted);
        if (fields is null || fields[1].Contains('\0') || !NameInput.IsValid(fields[2]))
        {
            return AccountResults.InvalidRequest();
        }

        var done = await signup.CompleteAsync(fields[0], fields[1], fields[2], http.RequestAborted);
        if (!done.Succeeded)
        {
            return done.Error == TenancyErrors.WeakPassword
                ? AccountResults.WeakPassword(done.BrokenRules!)
                : ApiResults.Error(done.Error!);
        }

        try
        {
            // Read inside the transaction of the completion (the same context): it sees the membership that is not committed yet.
            var account = done.Account!;
            var tenant = await memberships.ReadAsync(account.Id, CancellationToken.None)
                ?? throw new InvalidOperationException("The account that was just made has no company.");
            if (tenant.CompanyId != done.CompanyId)
            {
                throw new InvalidOperationException("The membership read is not the company the completion made.");
            }

            var principal = SessionPrincipal.Create(http, account, tenant, tokens.Value.Audience, clock);
            return new AtomicSignInResult(
                principal,
                new AuditEntry
                {
                    Kind = AuditKinds.LoginSucceeded,
                    ActorUserId = account.Id,
                    SubjectUserId = account.Id,
                    SubjectEmail = account.Email,
                    OrgId = tenant.CompanyId,
                    OrgName = tenant.CompanyName,
                },
                done.Transaction);
        }
        catch
        {
            // The result would have owned the transaction; it does not exist, so this is the end of the completion: roll it back.
            await done.Transaction!.DisposeAsync();
            throw;
        }
    }
}
