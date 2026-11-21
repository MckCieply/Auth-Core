using Auth.Server.Sessions;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Mints a refresh token the way the code before spec 0015 did: without the two session claims (<c>session_org_id</c>,
/// <c>session_org_joined</c>). A handler of the host removes them from the principal of the sign-in while a login is made through
/// <see cref="LoginAsync"/>, so the stored token's payload lacks them (a test asserts that). Install it before the host is first used.
/// </summary>
public sealed class OldRefreshTokens
{
    // volatile: the handler runs on the server's own threads.
    private volatile string[]? _removing;

    private OldRefreshTokens()
    {
    }

    /// <summary>Adds the handler to the factory's host; the switch is off until <see cref="LoginAsync"/> turns it on.</summary>
    public static OldRefreshTokens Install(AuthAppFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var switchBoard = new OldRefreshTokens();
        factory.WithServices(services => services.Configure<OpenIddictServerOptions>(options => options.Handlers.Add(
            OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
                .UseInlineHandler(context =>
                {
                    if (switchBoard._removing is { } claims && context.Principal is { } principal)
                    {
                        foreach (var claim in claims)
                        {
                            principal.SetClaim(claim, (string?)null);
                        }
                    }

                    return ValueTask.CompletedTask;
                })
                // The Prepare handlers attach the principal of each token; the claims must be gone before the earlier of the two. The orders are
                // taken from the handlers themselves, so this holds if their numbers change. The stored payload is what proves it (a test asserts it).
                .SetOrder(Math.Min(
                    OpenIddictServerHandlers.PrepareAccessTokenPrincipal.Descriptor.Order,
                    OpenIddictServerHandlers.PrepareRefreshTokenPrincipal.Descriptor.Order) - 1)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build())));
        return switchBoard;
    }

    /// <summary>Logs in as any account with both session claims removed from the refresh token; the switch is off again when this returns.</summary>
    public Task<SessionApi.Session> LoginAsync(HttpClient client, string email, string password) =>
        LoginWithoutAsync(client, email, password, SessionPolicy.CompanyClaim, SessionPolicy.JoinedClaim);

    /// <summary>Logs in as any account with only the named claims removed from the refresh token; the switch is off again when this returns.</summary>
    public async Task<SessionApi.Session> LoginWithoutAsync(HttpClient client, string email, string password, params string[] claims)
    {
        _removing = claims;
        try
        {
            return await SessionApi.LoginAsync(client, email, password);
        }
        finally
        {
            _removing = null;
        }
    }
}
