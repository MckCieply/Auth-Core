using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Signup;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.Extensions.Options;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Providers;

/// <summary>
/// <c>POST /auth/providers/finish</c> (spec 0011): a new person finishes creating their account with a verified provider address and is signed in
/// at once. It mirrors <c>POST /auth/signup/complete</c> with the ticket in place of the mail link and no password; the checks run in the order
/// of the contract: both switches (before the body); the body; a live sign-in ticket of this browser; a verified address; the switch of the kind;
/// the members the kind takes; then, under the address lock, any account under the address or the identity linked meanwhile.
/// </summary>
public static class ProviderFinishEndpoint
{
    private const string TicketField = "ticket";
    private const string CompanyNameField = "company_name";

    public static async Task<IResult> HandleAsync(
        HttpContext http, SignupGate gate, AuthDbContext db, SignupService signup, MembershipReader memberships, IOptions<TokenOptions> tokens,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(signup);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);

        // Without it OpenIddict cannot sign in here (it would say so only after the account was made, and roll that back): fail at once.
        if (http.GetOpenIddictServerRequest() is null)
        {
            throw new InvalidOperationException("The finish was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        }

        // 1. Both switches off: before the body is read.
        if (!gate.AnyOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        // 2. The body: a ticket (a blank one is a string, unknown at step 3); `kind` absent (an organization) or one of the two; a company name
        // that keeps the name rule when it is there.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields
            || !fields.TryAnyString(TicketField, out var ticketText)
            || !SignupKinds.TryRead(fields, out var kind)
            || !fields.TryOptionalString(CompanyNameField, out var companyName)
            || (companyName is not null && !NameInput.IsValid(companyName)))
        {
            return ApiResults.InvalidRequest();
        }

        // 3. A live sign-in ticket of this browser.
        ProviderBindingCookie.TryRead(http.Request, out var binding);
        var ticket = await ProviderTickets.FindAsync(db, ticketText, binding, StorableTime.Now(clock), http.RequestAborted);
        if (ticket is not { Purpose: ProviderTicketPurpose.SignIn })
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);
        }

        // 4. Only an address the provider verified skips the mail of sign-up.
        if (ticket.Email is null || !ticket.EmailVerified)
        {
            return ApiResults.Error(TenancyErrors.EmailNotVerified);
        }

        // 5 and 6. The switch of the kind; an organization names its company and a private person names none (the ticket stays usable).
        if (!gate.IsOnFor(kind))
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        if ((kind == SignupKind.Organization) != (companyName is not null))
        {
            return ApiResults.InvalidRequest();
        }

        // 7 and 8.
        var done = await signup.FinishThroughProviderAsync(new ProviderFinish(ticket, ticketText, binding!, kind, companyName), http.RequestAborted);
        if (!done.Succeeded)
        {
            return ApiResults.Error(done.Error!);
        }

        try
        {
            // Read inside the transaction of the finish (the same context): it sees the membership that is not committed yet.
            var account = done.Account!;
            var tenant = await memberships.ReadAsync(account.Id, CancellationToken.None)
                ?? throw new InvalidOperationException("The account that was just made has no space.");
            if (tenant.CompanyId != done.CompanyId)
            {
                throw new InvalidOperationException("The membership read is not the space the finish made.");
            }

            return ProviderSignIn.Session(http, account, tenant, ticket.Provider, done.Transaction!, tokens.Value, clock);
        }
        catch
        {
            // The result would have owned the transaction; it does not exist, so this is the end of the finish: roll it back.
            await done.Transaction!.DisposeAsync();
            throw;
        }
    }
}
