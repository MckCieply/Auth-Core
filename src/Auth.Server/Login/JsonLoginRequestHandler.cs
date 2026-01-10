using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore;
using Microsoft.Net.Http.Headers;
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
    public const int MaxBodyBytes = 8 * 1024;

    private const string JsonMediaType = "application/json";

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

        if (!HttpMethods.IsPost(request.Method))
        {
            Reject(context, "The login endpoint only accepts POST requests.");
            return;
        }

        if (!IsJson(request.ContentType))
        {
            Reject(context, $"The request body must be '{JsonMediaType}'.");
            return;
        }

        if (request.ContentLength > MaxBodyBytes)
        {
            Reject(context, "The request body is too large.");
            return;
        }

        var body = await ReadBoundedAsync(request.Body, MaxBodyBytes, context.CancellationToken);
        if (body is null)
        {
            Reject(context, "The request body is too large.");
            return;
        }

        if (!TryParseCredentials(body, out var email, out var password))
        {
            Reject(context, "The request body must be a JSON object with non-empty string properties 'email' and 'password'.");
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

    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && string.Equals(parsed.MediaType.Value, JsonMediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the whole body, or returns <see langword="null"/> as soon as it exceeds <paramref name="limit"/> bytes.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(limit + 1);
        try
        {
            var total = 0;
            while (total <= limit)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total, limit + 1 - total), cancellationToken);
                if (read == 0)
                {
                    return buffer.AsSpan(0, total).ToArray();
                }

                total += read;
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryParseCredentials(byte[] body, out string email, out string password)
    {
        email = password = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && TryGetRequiredString(root, "email", out email)
                && TryGetRequiredString(root, "password", out password);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: GetString() on a string holding an unpaired surrogate escape (e.g. "\ud800").
            return false;
        }
    }

    private static bool TryGetRequiredString(JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }
}
