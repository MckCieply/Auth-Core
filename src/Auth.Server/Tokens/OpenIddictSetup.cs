using Auth.Infrastructure.Persistence;
using Auth.Server.Keys;
using Auth.Server.Login;
using Auth.Server.Sessions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;

namespace Auth.Server.Tokens;

public static class OpenIddictSetup
{
    /// <summary>
    /// Registers the OpenIddict server: RS256 JWT access tokens (unencrypted) and reference refresh tokens from the
    /// password and refresh-token flows, the token endpoint at <c>auth/login</c> and <c>auth/refresh</c> in pass-through mode, and the JWKS
    /// endpoint at <c>auth/.well-known/jwks.json</c>.
    /// Signing and encryption credentials come from the mounted <paramref name="keys"/>, never from a generated certificate.
    /// </summary>
    /// <param name="isDevelopment">
    /// Outside Development the issuer and audience must be set explicitly in configuration: the class defaults are
    /// development identifiers and must never end up in a production token.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <c>Auth:Tokens:Issuer</c> is not an absolute URI or <c>Auth:Tokens:Audience</c> is blank, or (outside
    /// Development) either key is missing from configuration.
    /// </exception>
    public static IServiceCollection AddAuthOpenIddict(this IServiceCollection services, IConfiguration configuration, KeyMaterial keys, bool isDevelopment)
    {
        if (!isDevelopment)
        {
            foreach (var key in new[] { TokenOptions.IssuerKey, TokenOptions.AudienceKey })
            {
                if (string.IsNullOrWhiteSpace(configuration[key]))
                {
                    throw new InvalidOperationException($"Configuration value '{key}' must be set explicitly outside the Development environment.");
                }
            }
        }

        var section = configuration.GetSection(TokenOptions.SectionName);
        var tokens = section.Get<TokenOptions>() ?? new TokenOptions();

        if (!Uri.TryCreate(tokens.Issuer, UriKind.Absolute, out var issuer))
        {
            throw new InvalidOperationException($"Configuration value '{TokenOptions.IssuerKey}' must be an absolute URI.");
        }

        if (string.IsNullOrWhiteSpace(tokens.Audience))
        {
            throw new InvalidOperationException($"Configuration value '{TokenOptions.AudienceKey}' is missing or blank.");
        }

        services.Configure<TokenOptions>(section);

        services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AuthDbContext>())
            .AddServer(options =>
            {
                options.SetIssuer(issuer)
                    .SetTokenEndpointUris(
                        JsonLoginRequestHandler.LoginPath.TrimStart('/'),
                        RefreshRequestHandler.RefreshPath.TrimStart('/'))
                    .SetJsonWebKeySetEndpointUris("auth/.well-known/jwks.json")
                    // No discovery document: it is built from the Host header and is not part of the contract.
                    .SetConfigurationEndpointUris(Array.Empty<Uri>());

                // The login endpoint is the password flow's one entry point; the refresh-token flow renews a session.
                options.AllowPasswordFlow()
                    .AllowRefreshTokenFlow()
                    .AcceptAnonymousClients();

                options.SetAccessTokenLifetime(TokenOptions.AccessTokenLifetime)
                    .SetRefreshTokenLifetime(SessionPolicy.SlidingLifetime)
                    // Spec 0002 Decision 2: rotation is on by default; a consumed token is forgiven for 15 s (an honest
                    // retry or a double submit), after which its reuse revokes the whole family.
                    .SetRefreshTokenReuseLeeway(SessionPolicy.ReuseLeeway)
                    .UseReferenceRefreshTokens()
                    .DisableAccessTokenEncryption()
                    .RegisterAudiences(tokens.Audience);

                // The token endpoint takes JSON, not a form post: swap OpenIddict's form extraction for ours.
                options.RemoveEventHandler(OpenIddictServerAspNetCoreHandlers.ExtractPostRequest<OpenIddictServerEvents.ExtractTokenRequestContext>.Descriptor)
                    .AddEventHandler(JsonLoginRequestHandler.Descriptor)
                    .AddEventHandler(RefreshRequestHandler.Descriptor)
                    .AddEventHandler(UnhandledTokenRequestGuard.Descriptor);

                // Only anonymous clients exist, so the token endpoint ignores client authentication: without this, an
                // `Authorization: Basic ...` header would be taken as client credentials and fail with invalid_client.
                options.RemoveEventHandler(OpenIddictServerAspNetCoreHandlers.ExtractBasicAuthenticationCredentials<OpenIddictServerEvents.ExtractTokenRequestContext>.Descriptor);

                // ...and cut the token response down to {status, access_token}.
                options.AddEventHandler(LoginResponseShaper.Descriptor);

                // Every sign-in gets a refresh token, and it travels in the auth_rt cookie, never in a body. The
                // authorization that comes with it must not leak into the access token's claim set.
                options.AddEventHandler(RefreshTokenIssuanceHandler.Descriptor)
                    .AddEventHandler(AccessTokenClaimFilter.Descriptor)
                    .AddEventHandler(SessionResponseHandler.Descriptor);

                options.AddSigningCertificate(keys.Signing)
                    .AddEncryptionCertificate(keys.Encryption);

                // HTTPS is enforced by the reverse proxy; the container port itself speaks plain HTTP.
                options.UseAspNetCore()
                    .EnableTokenEndpointPassthrough()
                    .DisableTransportSecurityRequirement();
            })
            // The company API takes the service's own access tokens as a consumer would: signature against the
            // instance's keys, issuer, audience and expiry. Without AddAudiences a token for another product would pass.
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
                options.AddAudiences(tokens.Audience);
            });

        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        services.AddAuthorization();

        return services;
    }
}
