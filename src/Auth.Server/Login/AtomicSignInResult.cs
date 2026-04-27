using System.Security.Claims;
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

namespace Auth.Server.Login;

/// <summary>
/// Issues the session of a sign-in and writes the <c>login.succeeded</c> row in one transaction (spec 0008: a change and its row are
/// written together, or neither is). OpenIddict makes the authorization and the token entries, in several saves, on the request's own
/// <see cref="AuthDbContext"/>; the transaction holds all of them and the row. The body of the answer is held in memory
/// until the transaction has committed, so that no token leaves the server for a session that was rolled back; on any failure the
/// response is cleared (no cookie either) and the exception goes to the outermost error handler (<c>500 internal_error</c>).
/// An answer that is not a <c>200</c> (OpenIddict refused the sign-in) is passed on as it is and nothing is committed.
/// Before the sign-in the result re-reads the account of the principal under a share lock (<c>FOR KEY SHARE</c>) that is held to the end of the
/// transaction: an account deletion locks the row for update, so a session is never written for an account that is being deleted, and one that
/// is already gone gets no session. Then nothing is signed in, the transaction is rolled back and the answer is <paramref name="whenGone"/>
/// (login: the answer for an unknown address; refresh: <c>invalid_grant</c>). Without it, an account that is gone is an error.
/// A login has the result open the transaction. A completed sign-up (spec 0009, Amendment 2) passes the one that still holds the account, the
/// company and the membership it has just made (<paramref name="opened"/>): the sign-in is then part of the completion, a sign-in that
/// cannot be written rolls the whole completion back, and so does an answer of OpenIddict that is not a <c>200</c> (it is a <c>500</c> then, not
/// an error that leaves the account behind). The result owns that transaction from the first line of <see cref="ExecuteAsync"/> and disposes it as soon as it is committed or
/// refused, before the held answer is written to the client.
/// </summary>
/// <param name="whenCommitted">
/// Runs once the sign-in is committed, before the held answer is written: the answer has not started, so it may still set a header (a sign-in
/// through a provider clears <c>auth_pt</c> there, spec 0011). It never runs for an answer that is rolled back.
/// </param>
public sealed class AtomicSignInResult(
    ClaimsPrincipal principal, AuditEntry? entry, IDbContextTransaction? opened = null, Func<Task<IResult>>? whenGone = null,
    Action<HttpContext>? whenCommitted = null) : IResult
{
    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        // Ownership first: from this line on, whatever happens below, the transaction that was handed over is disposed (rolled back unless committed).
        var transaction = opened;
        try
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var db = httpContext.RequestServices.GetRequiredService<AuthDbContext>();
            var audit = httpContext.RequestServices.GetRequiredService<AuditLog>();
            var realBody = httpContext.Response.Body;
            await using var held = new MemoryStream();
            httpContext.Response.Body = held;
            var gone = false;
            try
            {
                transaction ??= await db.Database.BeginTransactionAsync(httpContext.RequestAborted);
                if (!await AccountExistsAsync(db, principal, httpContext.RequestAborted))
                {
                    if (whenGone is null)
                    {
                        throw new InvalidOperationException("The account to sign in does not exist any more.");
                    }

                    gone = true;
                }
                else
                {
                    await Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)
                        .ExecuteAsync(httpContext);
                    if (httpContext.Response.StatusCode == StatusCodes.Status200OK)
                    {
                        // Staged only now that OpenIddict has not refused: a refused sign-in leaves no row tracked on the request's context.
                        if (entry is not null)
                        {
                            audit.Stage(entry);
                        }

                        await db.SaveChangesAsync(httpContext.RequestAborted);
                        await transaction.CommitAsync(httpContext.RequestAborted);
                        whenCommitted?.Invoke(httpContext);
                    }
                    else if (opened is not null)
                    {
                        // An error of OpenIddict must not be the answer to a completion whose account would stay: roll it back, and say 500.
                        throw new InvalidOperationException("OpenIddict refused a sign-in that completes a change; the change is rolled back.");
                    }
                }
            }
            catch
            {
                httpContext.Response.Body = realBody;
                httpContext.Response.Clear();
                throw;
            }

            if (gone)
            {
                // Nothing was signed in: end the transaction (and the lock) and answer as the caller said.
                await transaction.DisposeAsync();
                transaction = null;
                httpContext.Response.Body = realBody;
                await (await whenGone!()).ExecuteAsync(httpContext);
                return;
            }

            // The transaction is finished here (committed, or to be rolled back by the disposal): the held answer is handed to the client
            // without it, so that a slow client holds no connection and no row lock of the database. The finally below is for the exceptions.
            await transaction.DisposeAsync();
            transaction = null;

            httpContext.Response.Body = realBody;
            held.Position = 0;
            await held.CopyToAsync(realBody, httpContext.RequestAborted);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>Whether the account of the principal is there, read with a share lock held to the end of the transaction.</summary>
    private static async Task<bool> AccountExistsAsync(AuthDbContext db, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(principal.GetClaim(OpenIddictConstants.Claims.Subject), out var userId))
        {
            throw new InvalidOperationException("The principal to sign in has no account id.");
        }

        var found = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM "AspNetUsers" WHERE "Id" = {userId} FOR KEY SHARE""")
            .ToListAsync(cancellationToken);
        return found.Count == 1;
    }
}
