using System.Security.Claims;
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using OpenIddict.Server.AspNetCore;

namespace Auth.Server.Login;

/// <summary>
/// Issues the session of a login and writes the <c>login.succeeded</c> row in one transaction (spec 0008: a change and its row are
/// written together, or neither is). OpenIddict makes the authorization and the token entries, in several saves, on the request's own
/// <see cref="AuthDbContext"/>; the transaction opened here holds all of them and the row. The body of the answer is held in memory
/// until the transaction has committed, so that no token leaves the server for a session that was rolled back; on any failure the
/// response is cleared (no cookie either) and the exception goes to the outermost error handler (<c>500 internal_error</c>).
/// An answer that is not a <c>200</c> (OpenIddict refused the sign-in) is passed on as it is and nothing is committed.
/// </summary>
public sealed class AtomicSignInResult(ClaimsPrincipal principal, AuditEntry entry) : IResult
{
    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var db = httpContext.RequestServices.GetRequiredService<AuthDbContext>();
        var audit = httpContext.RequestServices.GetRequiredService<AuditLog>();
        var realBody = httpContext.Response.Body;
        await using var held = new MemoryStream();
        httpContext.Response.Body = held;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(httpContext.RequestAborted);
            await Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)
                .ExecuteAsync(httpContext);
            if (httpContext.Response.StatusCode == StatusCodes.Status200OK)
            {
                // Staged only now that OpenIddict has not refused: a refused sign-in leaves no row tracked on the request's context.
                audit.Stage(entry);
                await db.SaveChangesAsync(httpContext.RequestAborted);
                await transaction.CommitAsync(httpContext.RequestAborted);
            }
        }
        catch
        {
            httpContext.Response.Body = realBody;
            httpContext.Response.Clear();
            throw;
        }

        httpContext.Response.Body = realBody;
        held.Position = 0;
        await held.CopyToAsync(realBody, httpContext.RequestAborted);
    }
}
