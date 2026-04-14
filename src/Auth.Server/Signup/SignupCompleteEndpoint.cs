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
/// <c>POST /auth/signup/complete</c> (specs 0009 and 0010): finishes a sign-up and signs the person in (Amendment 2). No access token: the link
/// token is the credential. While both switches are off it answers <c>403 signup_disabled</c> before it reads the body — also for a request that
/// was made while its kind was on. The answer is that of a successful login (<c>200 {"status":"authenticated","access_token"}</c> and the
/// refresh cookie): the endpoint is a pass-through request of OpenIddict (<see cref="SignupCompleteRequestHandler"/>), builds the principal the
/// way the login does (<see cref="SessionPrincipal"/>) and returns an <see cref="AtomicSignInResult"/> that issues the session, writes
/// <c>login.succeeded</c> and commits the transaction of the completion. If any of that fails, nothing of the completion stays. The password and
/// the company name are optional members of the body: the order of spec 0010 → Complete judges them after the token, which says the kind.
/// </summary>
public static class SignupCompleteEndpoint
{
    public const string Path = "/auth/signup/complete";

    private static readonly string[] Required = ["token"];
    private static readonly string[] Optional = ["password", "company_name"];

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

        if (!gate.AnyOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        // [token, password, company_name]: an optional member is null when it is not in the body; when it is, it is a non-blank string like the
        // others. Which of the two a link needs is known only from its token: the service judges it (spec 0010 → Complete, step 5).
        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Required, Optional, http.RequestAborted);
        if (fields is not [{ } linkToken, var password, var name] || password?.Contains('\0') == true || (name is not null && !NameInput.IsValid(name)))
        {
            return AccountResults.InvalidRequest();
        }

        var done = await signup.CompleteAsync(linkToken, password, name, http.RequestAborted);
        if (!done.Succeeded)
        {
            return done.Error switch
            {
                TenancyErrors.WeakPassword => AccountResults.WeakPassword(done.BrokenRules!),
                TenancyErrors.InvalidRequest => AccountResults.InvalidRequest(),
                _ => ApiResults.Error(done.Error!),
            };
        }

        try
        {
            // Read inside the transaction of the completion (the same context): it sees the membership that is not committed yet.
            var account = done.Account!;
            var tenant = await memberships.ReadAsync(account.Id, CancellationToken.None)
                ?? throw new InvalidOperationException("The account that was just made has no space.");
            if (tenant.CompanyId != done.CompanyId)
            {
                throw new InvalidOperationException("The membership read is not the space the completion made.");
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
