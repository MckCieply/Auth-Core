using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Signup;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Providers;

/// <summary>
/// <c>POST /auth/providers/exchange</c> (spec 0011): what a ticket leads to, in the order of the contract. A linked identity signs in (the ticket
/// used up in the transaction of the sign-in); otherwise the answer says what comes next, as plain JSON, and the ticket stays usable for the link
/// or the finish. An invitation ticket accepts its invitation and signs in; a ticket of the purpose <c>reauth</c> is never good here.
/// </summary>
public static class ProviderExchangeEndpoint
{
    private const string TicketField = "ticket";

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, SignupGate gate,
        InviteAcceptance acceptance, IOptions<TokenOptions> tokens, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);

        // Without it OpenIddict cannot sign in here: fail at once, as the sign-up completion does.
        if (http.GetOpenIddictServerRequest() is null)
        {
            throw new InvalidOperationException("The exchange was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        }

        // 1. A JSON object whose ticket is a string; a blank one is a string too, judged at step 2 as an unknown ticket.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields || !fields.TryAnyString(TicketField, out var ticketText))
        {
            return ApiResults.InvalidRequest();
        }

        ProviderBindingCookie.TryRead(http.Request, out var binding);
        var ticket = await ProviderTickets.FindAsync(db, ticketText, binding, StorableTime.Now(clock), http.RequestAborted);
        if (ticket is null || ticket.Purpose == ProviderTicketPurpose.Reauth)
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);   // unknown, used, expired, without its binding, or a re-authentication
        }

        // 4: an invitation, accepted with the provider's address in place of a password.
        if (ticket.Purpose == ProviderTicketPurpose.Invite)
        {
            return await AcceptAsync(http, acceptance, memberships, tokens.Value, clock, ticket, ticketText, binding!);
        }

        // 3.1: the identity key is the person; the address is never used to find a linked account.
        if (await ProviderLogins.FindAccountAsync(db, ticket.Provider, ticket.IdentityKey, http.RequestAborted) is { } accountId)
        {
            return await ProviderSignIn.LinkedAsync(http, db, users, memberships, tokens.Value, clock, accountId, ticket, ticketText, binding!);
        }

        // 3.2 and 3.3: no address, or one the provider did not prove; the latter says nothing about accounts (Settled 8).
        if (ticket.Email is not { } email)
        {
            return ApiResults.Error(TenancyErrors.NoEmail);
        }

        if (!ticket.EmailVerified)
        {
            return ApiResults.Ok(new ProviderLinkNext(ProviderNext.Link, email, false));
        }

        // 3.4 and 3.5: an account with the address spelled the same up to ASCII case links; one that only resembles it is a conflict.
        if (await users.FindByEmailAsync(email) is { } account)
        {
            return EmailInput.IsSameAddress(account.Email, email)
                ? ApiResults.Ok(new ProviderLinkNext(ProviderNext.Link, email, true))
                : ApiResults.Error(TenancyErrors.AlreadyRegistered);
        }

        // 3.6 and 3.7: a new person, with the kinds whose switch is on, organization first.
        List<string> kinds = [];
        if (gate.IsOn)
        {
            kinds.Add(SignupKinds.OrganizationName);
        }

        if (gate.PrivateOn)
        {
            kinds.Add(SignupKinds.PrivateName);
        }

        return kinds.Count == 0
            ? ApiResults.Error(TenancyErrors.NoAccount)
            : ApiResults.Ok(new ProviderFinishNext(ProviderNext.FinishSignup, email, kinds));
    }

    /// <summary>Step 4 of the exchange: the acceptance, then the sign-in in its transaction (an invitation through a provider signs in at once).</summary>
    private static async Task<IResult> AcceptAsync(
        HttpContext http, InviteAcceptance acceptance, MembershipReader memberships, TokenOptions tokens, TimeProvider clock,
        ProviderTicket ticket, string ticketText, string binding)
    {
        var accepted = await acceptance.AcceptThroughProviderAsync(new ProviderInvite(ticket, ticketText, binding), http.RequestAborted);
        if (!accepted.Succeeded)
        {
            return ApiResults.Error(accepted.Error!);
        }

        try
        {
            var account = accepted.Account!;
            var tenant = await memberships.ReadAsync(account.Id, CancellationToken.None)
                ?? throw new InvalidOperationException("The account that was just made has no membership.");
            if (tenant.CompanyId != accepted.CompanyId)
            {
                throw new InvalidOperationException("The membership read is not the company the acceptance joined.");
            }

            return ProviderSignIn.Session(http, account, tenant, ticket.Provider, accepted.Transaction!, tokens, clock);
        }
        catch
        {
            await accepted.Transaction!.DisposeAsync();
            throw;
        }
    }
}
