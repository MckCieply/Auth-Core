using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Email;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Testing;

/// <summary>
/// <c>POST /auth/testing/end-confirmation</c> (spec 0012 → Testing endpoint): with the access token and the refresh cookie, and no body, it ends
/// the window of this device's session. Mapped only with <see cref="TestingSettings.WindowEndpoint"/>, and never in the OpenAPI description.
/// </summary>
public static class TestingEndpoints
{
    public const string EndConfirmationPath = "/auth/testing/end-confirmation";

    public static IEndpointRouteBuilder MapTestingApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(EndConfirmationPath, EndConfirmationAsync).RequireAuthorization().ExcludeFromDescription();
        return app;
    }

    public static async Task<IResult> EndConfirmationAsync(HttpContext http, AuthDbContext db, IOpenIddictTokenManager tokens, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);

        if (!Guid.TryParse(http.User.FindFirst(Claims.Subject)?.Value, out var userId)
            || await DeviceSessions.FindAsync(http, db, tokens, userId, StorableTime.Now(clock), http.RequestAborted) is not { } session)
        {
            return ApiResults.Error(TenancyErrors.SessionRequired);
        }

        await DeviceSessions.EndWindowAsync(db, session, http.RequestAborted);
        return ApiResults.NoContent();
    }
}
