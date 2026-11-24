using Auth.Server.Keys;
using OpenIddict.Client;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Providers;

/// <summary>
/// OpenIddict's client beside the server in the one host (spec 0011, Decision 2): the authorization code flow with PKCE, the two
/// callback paths as its redirection endpoint in pass-through mode for successes and errors alike (so that Auth-Core always answers the callback
/// itself), the state for 10 minutes, protected by the instance's own key files (the client needs signing and encryption credentials), the request-forgery cookie
/// <c>SameSite=Lax</c> on <c>/auth/providers</c> (Amendment A4), userinfo off (the claims come from the identity token alone), and the client's URI
/// the return address's origin (Amendment A2; without it a callback that reaches the server under another scheme or host than the start fails as an invalid state). A provider is registered only when its two keys and the return address are set: the web
/// providers of Google, Microsoft (tenant <c>common</c>) and GitHub, or, with the test issuer (Development), plain registrations of it (OpenID Connect for the
/// first two, OAuth 2.0 for GitHub, spec 0012). <see cref="GitHubReader"/> reads GitHub's identity in both modes.
/// </summary>
public static class ProviderClientSetup
{
    public static OpenIddictBuilder AddProviderClient(this OpenIddictBuilder builder, ProviderSettings settings, KeyMaterial keys)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(keys);

        // Spec 0012, Amendment A4: the GitHub reader's own client, one attempt per call (OpenIddict's own clients retry), with GitHub's headers.
        // It follows no redirect: a 3xx is "not 200" (provider_error), not a second request, and a revocation is never repeated at another address.
        // It keeps no cookies: the factory pools the handler, and a cookie of one person's answer must never go out with another's request.
        builder.Services.AddHttpClient(GitHubReader.HttpClientName, client =>
        {
            client.Timeout = GitHubReader.CallBound;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Auth-Core");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

        return builder.AddClient(options =>
        {
            options.AllowAuthorizationCodeFlow();
            options.SetRedirectionEndpointUris([.. ProviderNames.All.Select(p => ProviderPaths.CallbackPathOf(p).TrimStart('/'))]);
            options.SetStateTokenLifetime(ProviderTickets.StateLifetime);
            options.AddSigningCertificate(keys.Signing).AddEncryptionCertificate(keys.Encryption);
            options.DisableWebServicesFederationClaimMapping();
            options.AddEventHandler(UserInfoOff.Descriptor);
            options.AddEventHandler(GitHubReader.Descriptor);
            if (settings.ReturnOrigin is { } origin)
            {
                options.SetClientUri(origin);
            }

            // TLS ends at the reverse proxy, as for the server; the callback's scheme and host are those of the return address (ProviderCallbackOrigin).
            options.UseAspNetCore()
                .EnableRedirectionEndpointPassthrough()
                .EnableErrorPassthrough()
                .DisableTransportSecurityRequirement()
                .Configure(aspNetCore =>
                {
                    aspNetCore.CookieBuilder.SameSite = SameSiteMode.Lax;
                    aspNetCore.CookieBuilder.Path = ProviderPaths.Prefix;
                });

            options.UseSystemNetHttp();

            if (settings.ReturnOrigin is null)
            {
                return;   // no provider can be offered without the return address
            }

            if (settings.TestIssuer is not null)
            {
                foreach (var provider in ProviderNames.All)
                {
                    if (settings.KeysOf(provider) is { } testKeys)
                    {
                        var registration = new OpenIddictClientRegistration
                        {
                            Issuer = settings.TestIssuerOf(provider),
                            ProviderName = provider,
                            ProviderDisplayName = ProviderNames.DisplayNameOf(provider),
                            ClientId = testKeys.ClientId,
                            ClientSecret = testKeys.ClientSecret,
                            RedirectUri = settings.CallbackUriOf(provider),
                        };

                        // GitHub is OAuth 2.0 (spec 0012, Amendment A5): the scope user:email and no openid, so no identity token is asked for.
                        foreach (var scope in provider == ProviderNames.GitHub ? GitHubScopes : OpenIdScopes)
                        {
                            registration.Scopes.Add(scope);
                        }

                        options.AddRegistration(registration);
                    }
                }

                return;
            }

            var web = options.UseWebProviders();
            if (settings.Google is { } google)
            {
                web.AddGoogle(registration => registration
                    .SetClientId(google.ClientId)
                    .SetClientSecret(google.ClientSecret)
                    .SetRedirectUri(settings.CallbackUriOf(ProviderNames.Google))
                    .AddScopes(Scopes.Email, Scopes.Profile)
                    .SetProviderName(ProviderNames.Google)
                    .SetProviderDisplayName(ProviderNames.DisplayNameOf(ProviderNames.Google)));
            }

            if (settings.Microsoft is { } microsoft)
            {
                web.AddMicrosoft(registration => registration
                    .SetClientId(microsoft.ClientId)
                    .SetClientSecret(microsoft.ClientSecret)
                    .SetRedirectUri(settings.CallbackUriOf(ProviderNames.Microsoft))
                    .SetTenant("common")
                    .AddScopes(Scopes.Email, Scopes.Profile)
                    .SetProviderName(ProviderNames.Microsoft)
                    .SetProviderDisplayName(ProviderNames.DisplayNameOf(ProviderNames.Microsoft)));
            }

            if (settings.GitHub is { } gitHub)
            {
                // Spec 0012: an OAuth App; OpenIddict's provider requires GitHub's `iss` on the answer (Amendment A6) and sends PKCE (A7).
                web.AddGitHub(registration => registration
                    .SetClientId(gitHub.ClientId)
                    .SetClientSecret(gitHub.ClientSecret)
                    .SetRedirectUri(settings.CallbackUriOf(ProviderNames.GitHub))
                    .AddScopes(GitHubScopes)
                    .SetProviderName(ProviderNames.GitHub)
                    .SetProviderDisplayName(ProviderNames.DisplayNameOf(ProviderNames.GitHub)));
            }
        });
    }

    private static readonly string[] OpenIdScopes = [Scopes.OpenId, Scopes.Email, Scopes.Profile];

    private static readonly string[] GitHubScopes = ["user:email"];

    /// <summary>
    /// Turns userinfo off before OpenIddict decides to call it (it is on by default): the identity rules read the identity token only, and a userinfo
    /// answer must never shadow one of its claims.
    /// </summary>
    public sealed class UserInfoOff : IOpenIddictClientHandler<OpenIddictClientEvents.ProcessAuthenticationContext>
    {
        public static OpenIddictClientHandlerDescriptor Descriptor { get; } =
            OpenIddictClientHandlerDescriptor.CreateBuilder<OpenIddictClientEvents.ProcessAuthenticationContext>()
                .UseSingletonHandler<UserInfoOff>()
                .SetOrder(OpenIddictClientHandlers.EvaluateUserInfoRequest.Descriptor.Order - 500)
                .SetType(OpenIddictClientHandlerType.Custom)
                .Build();

        public ValueTask HandleAsync(OpenIddictClientEvents.ProcessAuthenticationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            context.DisableUserInfoRetrieval = true;
            return ValueTask.CompletedTask;
        }
    }
}
