using Auth.Server.Keys;
using OpenIddict.Client;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Providers;

/// <summary>
/// OpenIddict's client beside the server in the one host (spec 0011, Decision 2; probe Q3): the authorization code flow with PKCE, the two
/// callback paths as its redirection endpoint in pass-through mode for successes and errors alike (so that Auth-Core always answers the callback
/// itself, probe Q1), the state for 10 minutes, protected by the instance's own key files (probe Q10), the request-forgery cookie
/// <c>SameSite=Lax</c> on <c>/auth/providers</c> (Amendment A4), userinfo off (the claims come from the identity token alone), and the client's URI
/// the return address's origin (Amendment A2, T4). A provider is registered only when its two keys and the return address are set: the web
/// providers of Google and Microsoft (tenant <c>common</c>), or, with the test issuer (Development), plain OpenID Connect registrations of it.
/// </summary>
public static class ProviderClientSetup
{
    public static OpenIddictBuilder AddProviderClient(this OpenIddictBuilder builder, ProviderSettings settings, KeyMaterial keys)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(keys);

        return builder.AddClient(options =>
        {
            options.AllowAuthorizationCodeFlow();
            options.SetRedirectionEndpointUris([.. ProviderNames.All.Select(p => ProviderPaths.CallbackPathOf(p).TrimStart('/'))]);
            options.SetStateTokenLifetime(ProviderTickets.StateLifetime);
            options.AddSigningCertificate(keys.Signing).AddEncryptionCertificate(keys.Encryption);
            options.DisableWebServicesFederationClaimMapping();
            options.AddEventHandler(UserInfoOff.Descriptor);
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
                        options.AddRegistration(new OpenIddictClientRegistration
                        {
                            Issuer = settings.TestIssuerOf(provider),
                            ProviderName = provider,
                            ProviderDisplayName = ProviderNames.DisplayNameOf(provider),
                            ClientId = testKeys.ClientId,
                            ClientSecret = testKeys.ClientSecret,
                            RedirectUri = settings.CallbackUriOf(provider),
                            Scopes = { Scopes.OpenId, Scopes.Email, Scopes.Profile },
                        });
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
        });
    }

    /// <summary>
    /// Turns userinfo off before OpenIddict decides to call it (probe Q5, T7): the identity rules read the identity token only, and a userinfo
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
