using Auth.Server.Login;
using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Signup;

/// <summary>
/// Makes <c>POST /auth/signup/complete</c> a token-endpoint request of OpenIddict, in pass-through mode like the login, so that its endpoint can sign the
/// person in (spec 0009, Amendment 2): OpenIddict signs in only inside a request it recognised as one of its endpoints. The handler claims the path
/// and gives OpenIddict a <b>placeholder</b> password-grant request (its validation wants a user name and a password that are not empty). It never reads
/// the body: the endpoint does, exactly as it did before, so the order of its answers (<c>403</c> before the body is read, <c>400</c> for a body that is not
/// what it takes) is its own, and nothing a client sends can reach the OpenIddict request. Any method is claimed; the route answers <c>405</c> to all but POST.
/// </summary>
public sealed class SignupCompleteRequestHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ExtractTokenRequestContext>
{
    private const string Placeholder = "signup-complete";

    /// <summary>
    /// <see langword="true"/> for <c>/auth/signup/complete</c> and <c>/auth/signup/complete/</c>, case-insensitive: OpenIddict matches a token endpoint with an
    /// optional trailing slash, so this check must agree with it.
    /// </summary>
    internal static bool IsCompletePath(PathString path) =>
        path.Equals(SignupCompleteEndpoint.Path, StringComparison.OrdinalIgnoreCase)
        || path.Equals(SignupCompleteEndpoint.Path + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// After <see cref="JsonLoginRequestHandler"/> (which holds the slot of OpenIddict's removed form extraction) and before
    /// <see cref="UnhandledTokenRequestGuard"/> (+500), beside the refresh handler (+250): the three claim different paths.
    /// </summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ExtractTokenRequestContext>()
            .AddFilter<OpenIddictServerAspNetCoreHandlerFilters.RequireHttpRequest>()
            .UseSingletonHandler<SignupCompleteRequestHandler>()
            .SetOrder(JsonLoginRequestHandler.Descriptor.Order + 300)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Transaction.GetHttpRequest()
            ?? throw new InvalidOperationException("The token endpoint was reached without an ASP.NET Core request.");

        if (IsCompletePath(request.Path))
        {
            context.Request = new OpenIddictRequest { GrantType = GrantTypes.Password, Username = Placeholder, Password = Placeholder };
        }

        return ValueTask.CompletedTask;
    }
}
