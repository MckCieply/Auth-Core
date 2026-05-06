using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace Auth.FakeOidc;

/// <summary>
/// A revocation the fake was asked for: the client id of its path, the token of its body (empty when it had none) and the answer's status
/// (<c>0</c>: the <c>revoke_slow</c> defect held the request and gave no answer).
/// </summary>
public sealed record GitHubRevocation(string ClientId, string Token, int Status);

/// <summary>
/// The fake provider as GitHub (spec 0012, criterion 32): OAuth 2.0 with PKCE, no identity token, no nonce. By host it answers as GitHub does
/// (<c>github.com</c>: the authorization and the token endpoints; <c>api.github.com</c>: <c>/user</c>, <c>/user/emails</c> and the revocation),
/// with <c>iss</c> on every authorization answer (Amendment A6); under <c>/github/...</c> on any other host it is the test issuer of
/// Amendment A5, with a minimal discovery document, a key set and the same endpoints, and no <c>iss</c>. It records every token it issues,
/// every request to its API and every revocation.
/// </summary>
public sealed class FakeGitHub : IDisposable
{
    public const string WebIssuer = "https://github.com/login/oauth";

    /// <summary>For the end-to-end script: the revocations as JSON, the client id and the status of each, never a token.</summary>
    public const string InspectionPath = "/_fake/github/revocations";

    private const string Prefix = "/github";
    private const int LargeBodyBytes = 70 * 1024;

    private static readonly string[] CodeOnly = ["code"];
    private static readonly string[] S256Only = ["S256"];
    private static readonly string[] AuthorizationCodeOnly = ["authorization_code"];
    private static readonly string[] AuthMethods = ["client_secret_post", "client_secret_basic"];

    private readonly ConcurrentDictionary<string, Grant> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Grant> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _issued = new();
    private readonly ConcurrentQueue<GitHubRevocation> _revocations = new();
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _slowArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FakeOidcOptions _options;
    private readonly Func<Dictionary<string, object>> _keySet;
    private int _disposed;

    public FakeGitHub(FakeOidcOptions options, Func<Dictionary<string, object>> keySet)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keySet);

        _options = options;
        _keySet = keySet;
    }

    /// <summary>Every access token the token endpoint issued, oldest first.</summary>
    public IReadOnlyCollection<string> IssuedTokens => [.. _issued];

    public IReadOnlyCollection<GitHubRevocation> Revocations => [.. _revocations];

    /// <summary>The token requests and the API requests, as <c>"METHOD path?query"</c> of GitHub's paths, oldest first.</summary>
    public IReadOnlyCollection<string> Requests => [.. _requests];

    /// <summary>Completes when a request of a slow defect (<c>user_slow</c>, <c>emails_slow</c>, <c>revoke_slow</c>) has arrived and is being held.</summary>
    public Task SlowRequestArrived => _slowArrived.Task;

    /// <summary>Releases every held request; calling it again does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _closing.Cancel();
            _closing.Dispose();
        }
    }

    /// <summary>Answers the request when it is GitHub's; <see langword="false"/> otherwise (the OpenID providers answer it then).</summary>
    public async Task<bool> TryHandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        var path = request.Path.Value ?? "/";
        if (Is(request.Host.Host, "github.com"))
        {
            switch (path)
            {
                case "/login/oauth/authorize":
                    await AuthorizeAsync(context, web: true);
                    return true;
                case "/login/oauth/access_token":
                    await TokenAsync(context, "/login/oauth/access_token");
                    return true;
                default:
                    return false;
            }
        }

        if (Is(request.Host.Host, "api.github.com"))
        {
            return await ApiAsync(context, path);
        }

        if (path == InspectionPath && HttpMethods.IsGet(request.Method))
        {
            await WriteJsonAsync(
                context.Response,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["revocations"] = _revocations.Select(r => new Dictionary<string, object> { ["client_id"] = r.ClientId, ["status"] = r.Status }).ToArray(),
                });
            return true;
        }

        if (!path.StartsWith(Prefix + "/", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = path[Prefix.Length..];
        switch (rest)
        {
            case "/.well-known/openid-configuration":
                await WriteJsonAsync(context.Response, Discovery(request));
                return true;
            case "/keys":
                await WriteJsonAsync(context.Response, _keySet());
                return true;
            case "/authorize":
                await AuthorizeAsync(context, web: false);
                return true;
            case "/token":
                await TokenAsync(context, "/login/oauth/access_token");
                return true;
            default:
                return await ApiAsync(context, rest);
        }
    }

    private Dictionary<string, object> Discovery(HttpRequest request)
    {
        var origin = request.Scheme + "://" + request.Host.Value;
        var issuer = (_options.IssuerOrigin?.GetLeftPart(UriPartial.Authority) ?? origin) + Prefix;
        var browser = (_options.BrowserOrigin?.GetLeftPart(UriPartial.Authority) ?? origin) + Prefix;

        // Amendment A5: what OpenIddict needs and no more. No `iss` parameter is advertised, so the plain registration does not require it.
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = browser + "/authorize",
            ["token_endpoint"] = issuer + "/token",
            ["jwks_uri"] = issuer + "/keys",
            ["response_types_supported"] = CodeOnly,
            ["grant_types_supported"] = AuthorizationCodeOnly,
            ["code_challenge_methods_supported"] = S256Only,
            ["token_endpoint_auth_methods_supported"] = AuthMethods,
        };
    }

    private async Task AuthorizeAsync(HttpContext context, bool web)
    {
        var query = context.Request.Query;
        var clientId = One(query, "client_id");
        var redirectUri = One(query, "redirect_uri");
        var state = One(query, "state");
        var challenge = One(query, "code_challenge");
        var scopes = (One(query, "scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!string.Equals(One(query, "response_type"), "code", StringComparison.Ordinal)
            || !string.Equals(One(query, "code_challenge_method"), "S256", StringComparison.Ordinal)
            || clientId is null || state is null || challenge is null || !scopes.Contains("user:email", StringComparer.Ordinal)
            || redirectUri is null || !Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
        {
            await PlainAsync(context.Response, "This is not an authorization request of GitHub's code flow with PKCE (S256), a state and the scope user:email.");
            return;
        }

        if (FakeSignIn.From(query) is not { } signIn)
        {
            await PageAsync(context);
            return;
        }

        if (signIn.Defect is { } defect
            && (!FakeDefects.IsKnown(defect) || FakeDefects.IsOpenIdOnly(defect) || (defect == FakeDefects.NoIss && !web)))
        {
            await PlainAsync(context.Response, "This is not a defect the fake GitHub knows.");   // the claim defects of the OpenID fakes cannot apply here
            return;
        }

        var answer = new List<KeyValuePair<string, string?>>();
        if (signIn.Person == FakePeople.Cancel || signIn.Defect == FakeDefects.ProviderError)
        {
            answer.Add(new("error", signIn.Person == FakePeople.Cancel ? "access_denied" : "server_error"));
            answer.Add(new("error_description", FakeOidcServer.RefusalDescription));
        }
        else
        {
            if (Person.Of(signIn) is not { } person)
            {
                await PlainAsync(context.Response, "GitHub has no such person.");
                return;
            }

            var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            _codes[code] = new Grant(clientId, redirectUri, challenge, person, signIn.Defect);
            answer.Add(new("code", code));
        }

        answer.Add(new("state", state));
        if (web && signIn.Defect != FakeDefects.NoIss)
        {
            answer.Add(new("iss", WebIssuer));   // GitHub sends it since April 2026, and OpenIddict's GitHub provider requires it
        }

        context.Response.Redirect(QueryHelpers.AddQueryString(redirectUri, answer));
    }

    private async Task TokenAsync(HttpContext context, string name)
    {
        var request = context.Request;
        if (!HttpMethods.IsPost(request.Method) || !request.HasFormContentType)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        _requests.Enqueue("POST " + name);
        var form = await request.ReadFormAsync(context.RequestAborted);
        var clientId = form.TryGetValue("client_id", out var formClient) ? formClient.ToString() : BasicOf(request)?.Id;
        if (!string.Equals(form["grant_type"].ToString(), "authorization_code", StringComparison.Ordinal)
            || !_codes.TryRemove(form["code"].ToString(), out var grant)
            || grant.Defect == FakeDefects.RefuseCode
            || !string.Equals(clientId, grant.ClientId, StringComparison.Ordinal)
            || !string.Equals(form["redirect_uri"].ToString(), grant.RedirectUri, StringComparison.Ordinal)
            || !string.Equals(S256(form["code_verifier"].ToString()), grant.Challenge, StringComparison.Ordinal))
        {
            // GitHub answers a refused code with 200 and an error.
            await AnswerTokenAsync(context, new() { ["error"] = "bad_verification_code", ["error_description"] = FakeOidcServer.RefusalDescription });
            return;
        }

        if (grant.Defect == FakeDefects.NoToken)
        {
            await AnswerTokenAsync(context, new() { ["token_type"] = "bearer", ["scope"] = "user:email" });
            return;
        }

        var token = "gho_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(18));
        _tokens[token] = grant;
        _issued.Enqueue(token);
        await AnswerTokenAsync(context, new() { ["access_token"] = token, ["token_type"] = "bearer", ["scope"] = "user:email" });
    }

    /// <summary>GitHub answers the token endpoint form-encoded unless the request asks for JSON (OpenIddict does).</summary>
    private static async Task AnswerTokenAsync(HttpContext context, Dictionary<string, string> body)
    {
        if (context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, body, cancellationToken: context.RequestAborted);
            return;
        }

        context.Response.ContentType = "application/x-www-form-urlencoded";
        await context.Response.WriteAsync(
            string.Join('&', body.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))), context.RequestAborted);
    }

    private async Task<bool> ApiAsync(HttpContext context, string path)
    {
        var request = context.Request;
        if (path.StartsWith("/applications/", StringComparison.Ordinal) && path.EndsWith("/token", StringComparison.Ordinal) && HttpMethods.IsDelete(request.Method))
        {
            await RevokeAsync(context, path);
            return true;
        }

        if (path is not ("/user" or "/user/emails") || !HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        _requests.Enqueue("GET " + path + request.QueryString.Value);
        if (!AsksLikeGitHub(request))
        {
            await MessageAsync(context.Response, StatusCodes.Status403Forbidden, "Request forbidden by administrative rules. Please make sure your request has a User-Agent header.");
            return true;
        }

        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || !_tokens.TryGetValue(header["Bearer ".Length..], out var grant))
        {
            await MessageAsync(context.Response, StatusCodes.Status401Unauthorized, "Bad credentials");
            return true;
        }

        if (path == "/user")
        {
            await UserAsync(context, grant);
        }
        else
        {
            await EmailsAsync(context, grant);
        }

        return true;
    }

    private async Task UserAsync(HttpContext context, Grant grant)
    {
        switch (grant.Defect)
        {
            case FakeDefects.UserFails:
                await MessageAsync(context.Response, StatusCodes.Status500InternalServerError, "Server Error");
                return;
            case FakeDefects.UserNotJson:
                await HtmlAsync(context.Response);
                return;
            case FakeDefects.UserSlow:
                await HoldAsync(context);
                return;
        }

        var person = grant.Person;
        await WriteWithAsync(context.Response, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("login", person.Login);
            if (grant.Defect != FakeDefects.UserNoId)
            {
                if (grant.Defect != FakeDefects.UserBadId && long.TryParse(person.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    writer.WriteNumber("id", id);
                }
                else
                {
                    writer.WriteString("id", grant.Defect == FakeDefects.UserBadId ? "not-a-number" : person.Id);
                }
            }

            writer.WriteString("email", person.PublicEmail);
            writer.WriteString("name", "Ola Test");
            if (grant.Defect == FakeDefects.UserTooLarge)
            {
                writer.WriteString("bio", new string('x', LargeBodyBytes));
            }

            writer.WriteEndObject();
        });
    }

    private async Task EmailsAsync(HttpContext context, Grant grant)
    {
        switch (grant.Defect)
        {
            case FakeDefects.EmailsFails:
                await MessageAsync(context.Response, StatusCodes.Status500InternalServerError, "Server Error");
                return;
            case FakeDefects.EmailsNotJson:
                await HtmlAsync(context.Response);
                return;
            case FakeDefects.EmailsSlow:
                await HoldAsync(context);
                return;
        }

        await WriteWithAsync(context.Response, writer =>
        {
            writer.WriteStartArray();
            foreach (var entry in grant.Person.Emails)
            {
                writer.WriteStartObject();
                writer.WriteString("email", entry.Address);
                if (grant.Defect == FakeDefects.EmailsTooLarge)
                {
                    writer.WriteString("note", new string('x', LargeBodyBytes));   // documented JSON, larger than 64 KiB
                }

                if (grant.Defect == FakeDefects.EmailsBadEntry)
                {
                    writer.WriteString("primary", entry.Primary ? "yes" : "no");
                }
                else
                {
                    writer.WriteBoolean("primary", entry.Primary);
                }

                writer.WriteBoolean("verified", entry.Verified);
                writer.WriteString("visibility", entry.Primary ? "private" : null);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private async Task RevokeAsync(HttpContext context, string path)
    {
        var request = context.Request;
        var clientId = Uri.UnescapeDataString(path["/applications/".Length..^"/token".Length]);
        _requests.Enqueue("DELETE /applications/" + clientId + "/token");
        string? token = null;
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: context.RequestAborted);
            token = body.RootElement.TryGetProperty("access_token", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            // no body, or not JSON: GitHub answers 422; here the answer below is 401 or 422
        }

        int status;
        if (!AsksLikeGitHub(request))
        {
            status = StatusCodes.Status403Forbidden;   // GitHub refuses every API request without a User-Agent; the token stays valid
        }
        else if (!string.Equals(BasicOf(request)?.Id, clientId, StringComparison.Ordinal))
        {
            status = StatusCodes.Status401Unauthorized;
        }
        else if (token is null)
        {
            status = StatusCodes.Status422UnprocessableEntity;
        }
        else if (_tokens.TryGetValue(token, out var slow) && slow.Defect == FakeDefects.RevokeSlow)
        {
            _revocations.Enqueue(new GitHubRevocation(clientId, token, 0));   // no answer: the request is held until it is aborted; the token stays valid
            await HoldAsync(context);
            return;
        }
        else if (_tokens.TryGetValue(token, out var grant) && grant.Defect == FakeDefects.RevokeFails)
        {
            status = StatusCodes.Status500InternalServerError;   // the token stays valid, as at GitHub when the call fails
        }
        else
        {
            _tokens.TryRemove(token, out _);
            status = StatusCodes.Status204NoContent;
        }

        _revocations.Enqueue(new GitHubRevocation(clientId, token ?? "", status));
        context.Response.StatusCode = status;
    }

    /// <summary>The answer of the slow defects: nothing, until the request is aborted or the fake is disposed.</summary>
    private async Task HoldAsync(HttpContext context)
    {
        _slowArrived.TrySetResult();
        using var held = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _closing.Token);
        try
        {
            await Task.Delay(Timeout.Infinite, held.Token);
        }
        catch (OperationCanceledException)
        {
            // the caller gave up, or the test is over
        }
    }

    private static async Task PageAsync(HttpContext context)
    {
        var request = context.Request;
        var here = (request.Path.Value ?? "/") + request.QueryString.Value;
        (string Person, string Label)[] people =
        [
            (FakePeople.GitHub, $"Ola ({FakePeople.GitHubEmail}, primary and verified)"),
            (FakePeople.GitHubUnverified, $"Kim ({FakePeople.GitHubUnverifiedEmail}, primary, not verified)"),
            (FakePeople.GitHubNoPrimary, "Lee (no primary address)"),
        ];

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Fake GitHub</title></head><body>");
        html.Append("<h1>Sign in to the fake GitHub</h1><p>The test provider of Auth-Core. Choose who signs in.</p><ul>");
        foreach (var (person, label) in people)
        {
            html.Append("<li><a href=\"").Append(Encode(new FakeSignIn(person).AppendTo(here))).Append("\">").Append(Encode(label)).Append("</a></li>");
        }

        html.Append("</ul><form method=\"get\" action=\"").Append(Encode(request.Path.Value ?? "/")).Append("\">");
        foreach (var (key, values) in request.Query)
        {
            foreach (var value in values)
            {
                html.Append("<input type=\"hidden\" name=\"").Append(Encode(key)).Append("\" value=\"").Append(Encode(value ?? "")).Append("\">");
            }
        }

        html.Append("<input type=\"hidden\" name=\"person\" value=\"").Append(FakePeople.Custom).Append("\">");
        html.Append("<p><label for=\"email\">Email</label> <input id=\"email\" name=\"email\" type=\"email\" required></p>");
        html.Append("<p><label><input type=\"checkbox\" name=\"verified\" value=\"true\"> The provider verified this address</label></p>");
        html.Append("<p><button type=\"submit\">Sign in</button></p></form>");
        html.Append("<p><a href=\"").Append(Encode(new FakeSignIn(FakePeople.Cancel).AppendTo(here))).Append("\">Cancel</a></p></body></html>");

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html.ToString(), context.RequestAborted);
    }

    /// <summary>GitHub's API wants a <c>User-Agent</c>, and the reader asks for <c>application/vnd.github+json</c>.</summary>
    private static bool AsksLikeGitHub(HttpRequest request) =>
        !string.IsNullOrEmpty(request.Headers.UserAgent.ToString())
        && request.Headers.Accept.ToString().Contains("application/vnd.github+json", StringComparison.Ordinal);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string S256(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static (string Id, string Secret)? BasicOf(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
            var colon = pair.IndexOf(':', StringComparison.Ordinal);
            return colon < 0 ? null : (WebUtility.UrlDecode(pair[..colon]), WebUtility.UrlDecode(pair[(colon + 1)..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool Is(string host, string name) => string.Equals(host, name, StringComparison.OrdinalIgnoreCase);

    private static string? One(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var values) && values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;

    private static async Task WriteJsonAsync(HttpResponse response, Dictionary<string, object> body)
    {
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.Body, body);
    }

    private static async Task WriteWithAsync(HttpResponse response, Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        response.ContentType = "application/json; charset=utf-8";
        await response.Body.WriteAsync(buffer.ToArray());
    }

    private static async Task MessageAsync(HttpResponse response, int status, string message)
    {
        response.StatusCode = status;
        await WriteJsonAsync(response, new Dictionary<string, object>(StringComparer.Ordinal) { ["message"] = message });
    }

    private static async Task HtmlAsync(HttpResponse response)
    {
        response.ContentType = "text/html; charset=utf-8";
        await response.WriteAsync("<!doctype html><html><body>GitHub is having a moment.</body></html>");
    }

    private static async Task PlainAsync(HttpResponse response, string text)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        response.ContentType = "text/plain; charset=utf-8";
        await response.WriteAsync(text);
    }

    /// <summary>One address of <c>/user/emails</c>.</summary>
    private sealed record MailEntry(string Address, bool Primary, bool Verified);

    /// <summary>A GitHub person: the id (a decimal number, or the text a script asked for), the login, the public address of <c>/user</c>, the addresses.</summary>
    private sealed record Person(string Id, string Login, string? PublicEmail, IReadOnlyList<MailEntry> Emails)
    {
        /// <summary>The person of a sign-in at the fake; an address, a "verified" or a subject the sign-in names replaces the primary entry's or the id.</summary>
        public static Person? Of(FakeSignIn signIn) => signIn.Person switch
        {
            FakePeople.GitHub => new Person(
                signIn.Subject ?? FakePeople.GitHubId,
                FakePeople.GitHubLogin,
                FakePeople.GitHubPublicEmail,
                [new MailEntry(signIn.Email ?? FakePeople.GitHubEmail, true, signIn.Verified ?? true), new MailEntry(FakePeople.GitHubSecondaryEmail, false, true)]),
            FakePeople.GitHubUnverified => new Person(
                signIn.Subject ?? FakePeople.GitHubUnverifiedId, "kim-gh", null,
                [new MailEntry(signIn.Email ?? FakePeople.GitHubUnverifiedEmail, true, signIn.Verified ?? false)]),
            FakePeople.GitHubNoPrimary => new Person(
                signIn.Subject ?? FakePeople.GitHubNoPrimaryId, "lee-gh", null,
                [new MailEntry(FakePeople.GitHubNoPrimaryEmail, false, true), new MailEntry(FakePeople.GitHubNoPrimaryOtherEmail, false, true)]),
            FakePeople.Custom when signIn.Email is { } email => new Person(
                signIn.Subject ?? FakePeople.GitHubIdOf(email), "custom-gh", null, [new MailEntry(email, true, signIn.Verified ?? false)]),
            _ => null,
        };
    }

    private sealed record Grant(string ClientId, string RedirectUri, string Challenge, Person Person, string? Defect);
}
