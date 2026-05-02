using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Login;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Storage;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Providers;

/// <summary>
/// The sign-ins through a provider (spec 0011): the session is a login's in every respect (<see cref="SessionPrincipal"/>,
/// <see cref="AtomicSignInResult"/>), <c>login.succeeded</c> names the provider, the ticket is used up in the transaction of the sign-in, and
/// <c>auth_pt</c> is cleared once it is committed.
/// </summary>
internal static class ProviderSignIn
{
    /// <summary>
    /// Signs in to the account the identity is a login of (the exchange, step 3.1; the link, step 3): an account with no membership is
    /// <c>403 no_membership</c>, as at login; otherwise, in one transaction, the ticket is used up and the session issued. A ticket used meanwhile,
    /// and an account deleted meanwhile, are <c>400 invalid_ticket</c>. The login streak is neither consulted nor cleared (Settled 12).
    /// </summary>
    public static async Task<IResult> LinkedAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, TokenOptions tokens, TimeProvider clock,
        Guid accountId, ProviderTicket ticket, string ticketText, string binding)
    {
        var account = await users.FindByIdAsync(accountId.ToString());
        if (account is null)
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);
        }

        if (await memberships.ReadAsync(account.Id, http.RequestAborted) is not { } tenant)
        {
            return ApiResults.Error(TenancyErrors.NoMembership);
        }

        var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
        try
        {
            if (await ProviderTickets.ConsumeAsync(db, ticketText, binding, ticket.Purpose, StorableTime.Now(clock), http.RequestAborted) is null)
            {
                await transaction.DisposeAsync();
                return ApiResults.Error(TenancyErrors.InvalidTicket);
            }

            return Session(http, account, tenant, ticket.Provider, transaction, tokens, clock, InvalidTicketAsync);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>The answer of a sign-in through a provider, in <paramref name="transaction"/>, which the result owns from now on.</summary>
    public static IResult Session(
        HttpContext http, ApplicationUser account, TenantContext tenant, string provider, IDbContextTransaction transaction, TokenOptions tokens,
        TimeProvider clock, Func<Task<IResult>>? whenGone = null) =>
        new AtomicSignInResult(
            SessionPrincipal.Create(http, account, tenant, tokens.Audience, clock),
            new AuditEntry
            {
                Kind = AuditKinds.LoginSucceeded,
                ActorUserId = account.Id,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = tenant.CompanyId,
                OrgName = tenant.CompanyName,
                Details = new Dictionary<string, object?> { ["provider"] = provider },
            },
            transaction,
            whenGone,
            whenCommitted: context => ProviderBindingCookie.Clear(context.Response));

    private static Task<IResult> InvalidTicketAsync() => Task.FromResult(ApiResults.Error(TenancyErrors.InvalidTicket));
}
