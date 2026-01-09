using Auth.Infrastructure.Persistence;
using Auth.Server.Keys;

namespace Auth.Server.Tokens;

public static class OpenIddictSetup
{
    /// <summary>
    /// Registers the OpenIddict server: RS256 JWT access tokens (unencrypted) from the password flow only, the token
    /// endpoint at <c>auth/login</c> in pass-through mode, and the JWKS endpoint at <c>auth/.well-known/jwks.json</c>.
    /// Signing and encryption credentials come from the mounted <paramref name="keys"/>, never from a generated certificate.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <c>Auth:Tokens:Issuer</c> is not an absolute URI or <c>Auth:Tokens:Audience</c> is blank.
    /// </exception>
    public static IServiceCollection AddAuthOpenIddict(this IServiceCollection services, IConfiguration configuration, KeyMaterial keys)
    {
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
                    .SetTokenEndpointUris("auth/login")
                    .SetJsonWebKeySetEndpointUris("auth/.well-known/jwks.json");

                // The password flow is the only flow: the login endpoint is its one entry point.
                options.AllowPasswordFlow()
                    .AcceptAnonymousClients();

                options.SetAccessTokenLifetime(TokenOptions.AccessTokenLifetime)
                    .DisableAccessTokenEncryption()
                    .RegisterAudiences(tokens.Audience);

                options.AddSigningCertificate(keys.Signing)
                    .AddEncryptionCertificate(keys.Encryption);

                // HTTPS is enforced by the reverse proxy; the container port itself speaks plain HTTP.
                options.UseAspNetCore()
                    .EnableTokenEndpointPassthrough()
                    .DisableTransportSecurityRequirement();
            });

        return services;
    }
}
