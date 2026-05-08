using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenIddict.Abstractions;
using OpenIddict.Client;

namespace Auth.Server.Providers;

/// <summary>
/// Auth-Core's GitHub reader (spec 0012 → GitHub, step 2): an event handler of OpenIddict's client that runs on the callback of the <c>github</c>
/// registration right after the token response, before anything that could refuse the round trip and before the callback endpoint. With the
/// access token it reads <c>GET /user</c> and <c>GET /user/emails?per_page=100</c> through a client of its own (one attempt per call, Amendment
/// A4), each bounded to 10 seconds and 64 KiB; then, in a <c>finally</c>, it revokes the token on a bound of its own, never the request's, and
/// removes it from the context, so that the callback never sees it (Amendment A3). It hands over the identity in three items
/// (<see cref="GitHubItems"/>); a read that fails, a document that is not GitHub's, and a token answer without a token are refused as
/// <c>server_error</c>, which the callback answers with <c>provider_error</c>. A revocation that fails logs one warning with the provider and the
/// status, never the token, and changes nothing else.
/// </summary>
public sealed partial class GitHubReader(IHttpClientFactory clients, ProviderSettings settings, TimeProvider clock, ILogger<GitHubReader> logger)
    : IOpenIddictClientHandler<OpenIddictClientEvents.ProcessAuthenticationContext>
{
    public const string HttpClientName = "auth-core-github";

    /// <summary>The error address of a refusal of the reader: it marks the refusal as the reader's own. The callback does not log it.</summary>
    public const string ErrorUri = "urn:auth-core:github-read";

    public const int MaxBodyBytes = 64 * 1024;

    public static readonly TimeSpan CallBound = TimeSpan.FromSeconds(10);

    public static readonly Uri WebApiBase = new("https://api.github.com");

    /// <summary>After <c>ResolveValidatedBackchannelTokens</c>, which gives the token, and long before the first handler that could refuse the round trip.</summary>
    public static OpenIddictClientHandlerDescriptor Descriptor { get; } =
        OpenIddictClientHandlerDescriptor.CreateBuilder<OpenIddictClientEvents.ProcessAuthenticationContext>()
            .UseSingletonHandler<GitHubReader>()
            .SetOrder(OpenIddictClientHandlers.ResolveValidatedBackchannelTokens.Descriptor.Order + 1_000)
            .SetType(OpenIddictClientHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(OpenIddictClientEvents.ProcessAuthenticationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.EndpointType != OpenIddictClientEndpointType.Redirection
            || context.Registration is not { } registration
            || !string.Equals(registration.ProviderName, ProviderNames.GitHub, StringComparison.Ordinal)
            || context.TokenResponse is null)
        {
            return;
        }

        var token = context.BackchannelAccessToken;
        if (string.IsNullOrEmpty(token))
        {
            // A token answer without access_token: OpenIddict alone would answer it as a state it cannot read (Amendment A3).
            context.Reject(error: OpenIddictConstants.Errors.ServerError, description: "GitHub gave no access token.", uri: ErrorUri);
            return;
        }

        var client = clients.CreateClient(HttpClientName);
        var api = settings.TestIssuer is null ? WebApiBase : settings.TestIssuerOf(ProviderNames.GitHub);
        GitHubAccount? account;
        try
        {
            account = await ReadAsync(client, api, token, context.CancellationToken);
        }
        finally
        {
            await RevokeAsync(client, api, registration, token);
            // Setting only the first would make OpenIddict refuse the round trip for a missing token.
            context.BackchannelAccessToken = null;
            context.RequireBackchannelAccessToken = false;
        }

        if (account is null)
        {
            context.Reject(error: OpenIddictConstants.Errors.ServerError, description: "The GitHub account could not be read.", uri: ErrorUri);
            return;
        }

        GitHubItems.Write(context.Properties, account);
    }

    private async Task<GitHubAccount?> ReadAsync(HttpClient client, Uri api, string token, CancellationToken aborted)
    {
        try
        {
            using var user = await GetJsonAsync(client, Combine(api, "/user"), token, aborted);
            if (user is null)
            {
                return null;
            }

            using var emails = await GetJsonAsync(client, Combine(api, "/user/emails?per_page=100"), token, aborted);
            return emails is null ? null : GitHubIdentity.Read(user.RootElement, emails.RootElement);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is OperationCanceledException && !aborted.IsCancellationRequested))
        {
            return null;   // unreachable, slower than the bound, or not JSON: provider_error
        }
    }

    /// <summary>One <c>GET</c>, its answer a <c>200</c> with at most <see cref="MaxBodyBytes"/>; <see langword="null"/> otherwise.</summary>
    private async Task<JsonDocument?> GetJsonAsync(HttpClient client, Uri url, string token, CancellationToken aborted)
    {
        using var bound = new CancellationTokenSource(CallBound, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, bound.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        var body = await ReadBoundedAsync(response.Content, linked.Token);
        return body is null ? null : JsonDocument.Parse(body);
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxBodyBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0)
            {
                return buffer[..read];
            }

            read += count;
        }

        return null;   // larger than 64 KiB
    }

    /// <summary><c>DELETE /applications/{client_id}/token</c>, Basic with the registration's id and secret, the token in the body (spec 0012, step 4).</summary>
    private async Task RevokeAsync(HttpClient client, Uri api, OpenIddictClientRegistration registration, string token)
    {
        var clientId = registration.ClientId ?? throw new InvalidOperationException("The GitHub registration has no client id.");
        string status;
        try
        {
            // A bound of its own: a client that goes away must not leave the token alive.
            using var bound = new CancellationTokenSource(CallBound, clock);
            using var request = new HttpRequestMessage(HttpMethod.Delete, Combine(api, "/applications/" + Uri.EscapeDataString(clientId) + "/token"))
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal) { ["access_token"] = token }),
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + registration.ClientSecret)));
            using var response = await client.SendAsync(request, bound.Token);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return;
            }

            status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException)
        {
            status = "timeout";
        }
        catch (HttpRequestException)
        {
            status = "unreachable";
        }

        LogNotRevoked(logger, ProviderNames.GitHub, status);
    }

    private static Uri Combine(Uri api, string pathAndQuery) => new(api.AbsoluteUri.TrimEnd('/') + pathAndQuery);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Provider} access token could not be revoked (the answer was {Status}); the round trip goes on.")]
    private static partial void LogNotRevoked(ILogger logger, string provider, string status);
}
