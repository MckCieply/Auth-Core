using Auth.Server.Login;
using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Providers;

/// <summary>
/// Makes <c>POST /auth/providers/exchange</c>, <c>/link</c> and <c>/finish</c> token-endpoint requests of OpenIddict in pass-through mode with a
/// placeholder request, as <c>SignupCompleteRequestHandler</c> does for the sign-up completion (spec 0009, Amendment 2; spec 0011, Settled 7): a
/// sign-in there is a login's in every respect. It never reads the body; each endpoint does, in the order of its contract, and nothing a client
/// sends reaches the OpenIddict request.
/// </summary>
public sealed class ProviderRequestHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ExtractTokenRequestContext>
{
    private const string Placeholder = "provider-sign-in";

    /// <summary>The three paths, case-insensitive and with an optional trailing slash, as OpenIddict matches a token endpoint.</summary>
    internal static bool IsProviderTokenPath(PathString path) =>
        ProviderPaths.TokenPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.Equals(p + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>After the login's slot and before <see cref="UnhandledTokenRequestGuard"/> (+500), apart from the refresh (+250) and the completion (+300).</summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ExtractTokenRequestContext>()
            .AddFilter<OpenIddictServerAspNetCoreHandlerFilters.RequireHttpRequest>()
            .UseSingletonHandler<ProviderRequestHandler>()
            .SetOrder(JsonLoginRequestHandler.Descriptor.Order + 350)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Transaction.GetHttpRequest()
            ?? throw new InvalidOperationException("The token endpoint was reached without an ASP.NET Core request.");

        if (IsProviderTokenPath(request.Path))
        {
            context.Request = new OpenIddictRequest { GrantType = GrantTypes.Password, Username = Placeholder, Password = Placeholder };
        }

        return ValueTask.CompletedTask;
    }
}
