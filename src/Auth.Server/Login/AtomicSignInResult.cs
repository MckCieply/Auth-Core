using System.Security.Claims;
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore.Storage;
using OpenIddict.Server.AspNetCore;

namespace Auth.Server.Login;

/// <summary>
/// Issues the session of a sign-in and writes the <c>login.succeeded</c> row in one transaction (spec 0008: a change and its row are
/// written together, or neither is). OpenIddict makes the authorization and the token entries, in several saves, on the request's own
/// <see cref="AuthDbContext"/>; the transaction holds all of them and the row. The body of the answer is held in memory
/// until the transaction has committed, so that no token leaves the server for a session that was rolled back; on any failure the
/// response is cleared (no cookie either) and the exception goes to the outermost error handler (<c>500 internal_error</c>).
/// An answer that is not a <c>200</c> (OpenIddict refused the sign-in) is passed on as it is and nothing is committed.
/// A login has the result open the transaction. A completed sign-up (spec 0009, Amendment 2) passes the one that still holds the account, the
/// company and the membership it has just made (<paramref name="opened"/>): the sign-in is then part of the completion, a sign-in that
/// cannot be written rolls the whole completion back, and so does an answer of OpenIddict that is not a <c>200</c> (it is a <c>500</c> then, not
/// an error that leaves the account behind). The result owns that transaction from the first line of <see cref="ExecuteAsync"/> and disposes it as soon as it is committed or
/// refused, before the held answer is written to the client.
/// </summary>
public sealed class AtomicSignInResult(ClaimsPrincipal principal, AuditEntry entry, IDbContextTransaction? opened = null) : IResult
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
            try
            {
                transaction ??= await db.Database.BeginTransactionAsync(httpContext.RequestAborted);
                await Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)
                    .ExecuteAsync(httpContext);
                if (httpContext.Response.StatusCode == StatusCodes.Status200OK)
                {
                    // Staged only now that OpenIddict has not refused: a refused sign-in leaves no row tracked on the request's context.
                    audit.Stage(entry);
                    await db.SaveChangesAsync(httpContext.RequestAborted);
                    await transaction.CommitAsync(httpContext.RequestAborted);
                }
                else if (opened is not null)
                {
                    // An error of OpenIddict must not be the answer to a completion whose account would stay: roll it back, and say 500.
                    throw new InvalidOperationException("OpenIddict refused the sign-in of a completed sign-up; the completion is rolled back.");
                }
            }
            catch
            {
                httpContext.Response.Body = realBody;
                httpContext.Response.Clear();
                throw;
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
}
