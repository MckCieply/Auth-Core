using Auth.Server.Login;
using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Account;

/// <summary>
/// Makes <c>POST /auth/account/logins/unlink</c> and <c>POST /auth/account/password</c> (spec 0012) token-endpoint requests of OpenIddict in
/// pass-through mode with a placeholder request, as the provider sign-ins are, so that their answer can hand this device a renewed session. It
/// never reads the body; each endpoint does, and nothing a client sends reaches the OpenIddict request. Their routes require the access token.
/// </summary>
public sealed class SettingsRequestHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ExtractTokenRequestContext>
{
    public const string UnlinkPath = "/auth/account/logins/unlink";
    public const string PasswordPath = "/auth/account/password";

    private const string Placeholder = "session-renewal";

    public static IReadOnlyList<string> Paths { get; } = [UnlinkPath, PasswordPath];

    /// <summary>The two paths, case-insensitive and with an optional trailing slash, as OpenIddict matches a token endpoint.</summary>
    internal static bool IsSettingsTokenPath(PathString path) =>
        Paths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.Equals(p + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>After the provider sign-ins (+350) and before <see cref="UnhandledTokenRequestGuard"/> (+500).</summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ExtractTokenRequestContext>()
            .AddFilter<OpenIddictServerAspNetCoreHandlerFilters.RequireHttpRequest>()
            .UseSingletonHandler<SettingsRequestHandler>()
            .SetOrder(JsonLoginRequestHandler.Descriptor.Order + 400)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Transaction.GetHttpRequest()
            ?? throw new InvalidOperationException("The token endpoint was reached without an ASP.NET Core request.");

        if (IsSettingsTokenPath(request.Path))
        {
            context.Request = new OpenIddictRequest { GrantType = GrantTypes.Password, Username = Placeholder, Password = Placeholder };
        }

        return ValueTask.CompletedTask;
    }
}
