using System.Text.Json;
using Auth.Server.Requests;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Login;

/// <summary>
/// Turns the spec's JSON login body (<c>{"email":"…","password":"…"}</c>) into the OpenIddict password request the
/// token endpoint expects. It <b>replaces</b> OpenIddict's own form extraction for the token endpoint (see
/// <see cref="OpenIddictSetup"/>): JSON is the only accepted format, so a classic form-encoded OIDC request, a
/// <c>text/plain</c> body and every malformed JSON body are rejected with <c>400 invalid_request</c> before any
/// credential is looked at. Error descriptions are fixed strings and never echo submitted values.
/// </summary>
public sealed class JsonLoginRequestHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ExtractTokenRequestContext>
{
    /// <summary>Largest accepted request body, in bytes.</summary>
    public const int MaxBodyBytes = JsonObjectBody.MaxBytes;

    /// <summary>The one path this handler (and <see cref="LoginResponseShaper"/>) acts on.</summary>
    public const string LoginPath = "/auth/login";

    private const string InvalidCredentialsShapeDescription =
        "The request body must be a JSON object with non-empty string properties 'email' and 'password'.";

    /// <summary>
    /// <see langword="true"/> when <paramref name="request"/> targets the login endpoint (see <see cref="IsLoginPath"/>).
    /// </summary>
    internal static bool IsLoginRequest(HttpRequest request) => IsLoginPath(request.Path);

    /// <summary>
    /// <see langword="true"/> for <c>/auth/login</c> and <c>/auth/login/</c>, case-insensitive, and nothing else.
    /// OpenIddict matches the token endpoint with an optional trailing slash, so the login-specific handlers must
    /// agree with it: a request OpenIddict routes to the login endpoint must never slip past them.
    /// </summary>
    internal static bool IsLoginPath(PathString path) =>
        path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase)
        || path.Equals(LoginPath + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Takes the exact slot of OpenIddict's <c>ExtractPostRequest</c> (which must be removed from the pipeline), so
    /// the client-authentication extraction handlers that follow it still see a populated request.
    /// </summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ExtractTokenRequestContext>()
            .AddFilter<OpenIddictServerAspNetCoreHandlerFilters.RequireHttpRequest>()
            .UseSingletonHandler<JsonLoginRequestHandler>()
            .SetOrder(OpenIddictServerAspNetCoreHandlers.ExtractPostRequest<OpenIddictServerEvents.ExtractTokenRequestContext>.Descriptor.Order)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public async ValueTask HandleAsync(OpenIddictServerEvents.ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Transaction.GetHttpRequest()
            ?? throw new InvalidOperationException("The token endpoint was reached without an ASP.NET Core request.");

        // Login only. Another token-endpoint path belongs to another flow: do nothing, so that flow's own extraction
        // handler can populate the request (RefreshRequestHandler does so for /auth/refresh). OpenIddict's
        // ExtractPostRequest is removed (see OpenIddictSetup), so a path nobody claims is rejected by
        // UnhandledTokenRequestGuard with 400 invalid_request. This is intended: do not re-add ExtractPostRequest.
        if (!IsLoginRequest(request))
        {
            return;
        }

        if (!HttpMethods.IsPost(request.Method))
        {
            Reject(context, "The login endpoint only accepts POST requests.");
            return;
        }

        if (!JsonObjectBody.IsJson(request.ContentType))
        {
            Reject(context, "The request body must be 'application/json'.");
            return;
        }

        if (request.ContentLength > MaxBodyBytes)
        {
            Reject(context, "The request body is too large.");
            return;
        }

        var body = await JsonObjectBody.ReadBoundedAsync(request.Body, MaxBodyBytes, context.CancellationToken);
        if (body is null)
        {
            Reject(context, "The request body is too large.");
            return;
        }

        if (!TryParseCredentials(body, out var email, out var password))
        {
            Reject(context, InvalidCredentialsShapeDescription);
            return;
        }

        // NUL is not storable in a Postgres text column, control characters have no place in an email, and an
        // email the normaliser cannot take would throw in the endpoint. Same fixed 400 as any other malformed body.
        var normalizer = request.HttpContext.RequestServices.GetRequiredService<ILookupNormalizer>();
        if (!EmailInput.TryNormalize(email, normalizer, out _) || password.Contains('\0'))
        {
            Reject(context, InvalidCredentialsShapeDescription);
            return;
        }

        context.Request = new OpenIddictRequest
        {
            GrantType = GrantTypes.Password,
            Username = email,
            Password = password,
        };
    }

    private static void Reject(OpenIddictServerEvents.ExtractTokenRequestContext context, string description) =>
        context.Reject(error: Errors.InvalidRequest, description: description);

    private static bool TryParseCredentials(byte[] body, out string email, out string password)
    {
        email = password = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && JsonObjectBody.TryGetRequiredString(root, "email", out email)
                && JsonObjectBody.TryGetRequiredString(root, "password", out password);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: GetString() on a string holding an unpaired surrogate escape (e.g. "\ud800").
            return false;
        }
    }
}
