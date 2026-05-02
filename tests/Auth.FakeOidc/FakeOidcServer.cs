using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Auth.FakeOidc;

/// <summary>
/// The fake OpenID provider of spec 0011 (Settled 22). It answers by host and path: the real host names of Google and Microsoft (which only the
/// tests' routing sends to it) with documents that mirror the real ones where OpenIddict reads them, and on any other host the test issuer's
/// paths <c>/{google|microsoft}/.well-known/openid-configuration</c>, <c>/authorize</c>, <c>/token</c> and <c>/keys</c>. The authorization endpoint
/// answers at once for a <see cref="FakeSignIn"/> in its query and shows a page otherwise.
/// </summary>
public sealed class FakeOidcServer : IDisposable
{
    public const string GoogleIssuer = "https://accounts.google.com";
    public const string MicrosoftIssuerTemplate = "https://login.microsoftonline.com/{tenantid}/v2.0";

    /// <summary>The <c>error_description</c> of every refusal: a test asserts it never reaches a person (spec 0011 → the callback).</summary>
    public const string RefusalDescription = "FAKE-PROVIDER-DESCRIPTION";

    /// <summary>The tenant whose issuer a Microsoft token with <c>wrong_issuer</c> names.</summary>
    public const string OtherTenant = "0b0b0b0b-0000-4000-8000-0000000000aa";

    private const string KeyId = "fake-oidc-1";

    private static readonly string[] CodeOnly = ["code"];
    private static readonly string[] ResponseTypes = ["code", "id_token", "code id_token"];
    private static readonly string[] ResponseModes = ["query", "fragment", "form_post"];
    // Google lists these; without the list OpenIddict's web integration assumes only the implicit grant for Google and finds no flow.
    private static readonly string[] GoogleGrantTypes =
        ["authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:device_code", "urn:ietf:params:oauth:grant-type:jwt-bearer"];
    private static readonly string[] PublicSubjects = ["public"];
    private static readonly string[] PairwiseSubjects = ["pairwise"];
    private static readonly string[] Rs256 = ["RS256"];
    private static readonly string[] Scopes = ["openid", "email", "profile"];
    private static readonly string[] AuthMethods = ["client_secret_post", "client_secret_basic"];
    private static readonly string[] S256Only = ["S256"];
    private static readonly string[] GoogleChallengeMethods = ["plain", "S256"];
    private static readonly string[] GoogleClaims = ["aud", "email", "email_verified", "exp", "iat", "iss", "name", "sub"];
    private static readonly string[] MicrosoftClaims = ["sub", "iss", "aud", "exp", "iat", "name", "email", "tid", "oid"];

    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Grant> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _identityTokens = new();
    private readonly FakeOidcOptions _options;
    private readonly TimeProvider _clock;
    private int _userInfoRequests;

    public FakeOidcServer(FakeOidcOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _options = options;
        _clock = clock;
    }

    private enum Endpoint
    {
        Discovery,
        Authorize,
        Token,
        Keys,
    }

    /// <summary>Every identity token the token endpoint issued, oldest first: a test searches the audit log and the logs for them.</summary>
    public IReadOnlyCollection<string> IssuedIdentityTokens => [.. _identityTokens];

    /// <summary>How many times a client asked a userinfo endpoint (Google's, Microsoft's Graph, or any path ending in <c>/userinfo</c>): the client must never.</summary>
    public int UserInfoRequests => Volatile.Read(ref _userInfoRequests);

    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Route(context.Request) is not { } site)
        {
            if (IsUserInfo(context.Request))
            {
                Interlocked.Increment(ref _userInfoRequests);
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        switch (site.Endpoint)
        {
            case Endpoint.Discovery:
                await WriteJsonAsync(context.Response, Discovery(site, context.Request), context.RequestAborted);
                break;
            case Endpoint.Authorize:
                await AuthorizeAsync(context, site);
                break;
            case Endpoint.Token:
                await TokenAsync(context);
                break;
            default:
                await WriteJsonAsync(context.Response, KeySet(), context.RequestAborted);
                break;
        }
    }

    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
    }

    private static Site? Route(HttpRequest request)
    {
        var host = request.Host.Host;
        var path = request.Path.Value ?? "/";
        if (Is(host, "accounts.google.com"))
        {
            return path switch
            {
                "/.well-known/openid-configuration" => new Site(FakeKinds.Google, true, Endpoint.Discovery),
                "/o/oauth2/v2/auth" => new Site(FakeKinds.Google, true, Endpoint.Authorize),
                _ => null,
            };
        }

        if (Is(host, "oauth2.googleapis.com"))
        {
            return path == "/token" ? new Site(FakeKinds.Google, true, Endpoint.Token) : null;
        }

        if (Is(host, "www.googleapis.com"))
        {
            return path == "/oauth2/v3/certs" ? new Site(FakeKinds.Google, true, Endpoint.Keys) : null;
        }

        if (Is(host, "login.microsoftonline.com"))
        {
            return path switch
            {
                "/common/v2.0/.well-known/openid-configuration" => new Site(FakeKinds.Microsoft, true, Endpoint.Discovery),
                "/common/oauth2/v2.0/authorize" => new Site(FakeKinds.Microsoft, true, Endpoint.Authorize),
                "/common/oauth2/v2.0/token" => new Site(FakeKinds.Microsoft, true, Endpoint.Token),
                "/common/discovery/v2.0/keys" => new Site(FakeKinds.Microsoft, true, Endpoint.Keys),
                _ => null,
            };
        }

        // The test issuer: /{google|microsoft}/... on any other host.
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments[0] is not (FakeKinds.Google or FakeKinds.Microsoft))
        {
            return null;
        }

        Endpoint? endpoint = string.Join('/', segments[1..]) switch
        {
            ".well-known/openid-configuration" => Endpoint.Discovery,
            "authorize" => Endpoint.Authorize,
            "token" => Endpoint.Token,
            "keys" => Endpoint.Keys,
            _ => null,
        };
        return endpoint is { } found ? new Site(segments[0], false, found) : null;
    }

    private static bool IsUserInfo(HttpRequest request) =>
        Is(request.Host.Host, "openidconnect.googleapis.com") || Is(request.Host.Host, "graph.microsoft.com")
        || (request.Path.Value ?? "").EndsWith("/userinfo", StringComparison.OrdinalIgnoreCase);

    private static bool Is(string host, string name) => string.Equals(host, name, StringComparison.OrdinalIgnoreCase);

    private static string RequestOrigin(HttpRequest request) => request.Scheme + "://" + request.Host.Value;

    /// <summary>The test issuer of a kind: its configured origin (the container network) or the request's, then <c>/google</c> or <c>/microsoft</c>.</summary>
    private string IssuerOf(string kind, HttpRequest request) =>
        (_options.IssuerOrigin?.GetLeftPart(UriPartial.Authority) ?? RequestOrigin(request)) + "/" + kind;

    private string BrowserOf(string kind, HttpRequest request) =>
        (_options.BrowserOrigin?.GetLeftPart(UriPartial.Authority) ?? RequestOrigin(request)) + "/" + kind;

    private Dictionary<string, object> Discovery(Site site, HttpRequest request)
    {
        if (site.Web && site.Kind == FakeKinds.Google)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["issuer"] = GoogleIssuer,
                ["authorization_endpoint"] = "https://accounts.google.com/o/oauth2/v2/auth",
                ["token_endpoint"] = "https://oauth2.googleapis.com/token",
                ["userinfo_endpoint"] = "https://openidconnect.googleapis.com/v1/userinfo",
                ["jwks_uri"] = "https://www.googleapis.com/oauth2/v3/certs",
                ["grant_types_supported"] = GoogleGrantTypes,
                ["response_types_supported"] = ResponseTypes,
                ["response_modes_supported"] = ResponseModes,
                ["subject_types_supported"] = PublicSubjects,
                ["id_token_signing_alg_values_supported"] = Rs256,
                ["scopes_supported"] = Scopes,
                ["token_endpoint_auth_methods_supported"] = AuthMethods,
                ["claims_supported"] = GoogleClaims,
                ["code_challenge_methods_supported"] = GoogleChallengeMethods,
                ["authorization_response_iss_parameter_supported"] = true,
            };
        }

        if (site.Web)
        {
            // As Microsoft's: the issuer names the {tenantid} placeholder, and no PKCE method is listed (OpenIddict adds S256 itself).
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["issuer"] = MicrosoftIssuerTemplate,
                ["authorization_endpoint"] = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
                ["token_endpoint"] = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                ["jwks_uri"] = "https://login.microsoftonline.com/common/discovery/v2.0/keys",
                ["userinfo_endpoint"] = "https://graph.microsoft.com/oidc/userinfo",
                ["response_types_supported"] = ResponseTypes,
                ["response_modes_supported"] = ResponseModes,
                ["subject_types_supported"] = PairwiseSubjects,
                ["id_token_signing_alg_values_supported"] = Rs256,
                ["scopes_supported"] = Scopes,
                ["token_endpoint_auth_methods_supported"] = AuthMethods,
                ["claims_supported"] = MicrosoftClaims,
            };
        }

        var issuer = IssuerOf(site.Kind, request);
        var document = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = BrowserOf(site.Kind, request) + "/authorize",
            ["token_endpoint"] = issuer + "/token",
            ["jwks_uri"] = issuer + "/keys",
            // Advertised, never served: a client that asks for it gets a 404 and is counted (UserInfoRequests).
            ["userinfo_endpoint"] = issuer + "/userinfo",
            ["response_types_supported"] = CodeOnly,
            ["subject_types_supported"] = PublicSubjects,
            ["id_token_signing_alg_values_supported"] = Rs256,
            ["scopes_supported"] = Scopes,
            ["token_endpoint_auth_methods_supported"] = AuthMethods,
            ["code_challenge_methods_supported"] = S256Only,
        };
        if (site.Kind == FakeKinds.Google)
        {
            document["authorization_response_iss_parameter_supported"] = true;
        }

        return document;
    }

    private async Task AuthorizeAsync(HttpContext context, Site site)
    {
        var query = context.Request.Query;
        var clientId = One(query, "client_id");
        var redirectUri = One(query, "redirect_uri");
        var state = One(query, "state");
        var nonce = One(query, "nonce");
        var challenge = One(query, "code_challenge");
        if (!string.Equals(One(query, "response_type"), "code", StringComparison.Ordinal)
            || !string.Equals(One(query, "code_challenge_method"), "S256", StringComparison.Ordinal)
            || clientId is null || state is null || nonce is null || challenge is null
            || redirectUri is null || !Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
        {
            await PlainAsync(context.Response, "This is not an authorization request of the code flow with PKCE (S256), a state and a nonce.");
            return;
        }

        if (FakeSignIn.From(query) is not { } signIn)
        {
            await PageAsync(context, site);
            return;
        }

        if (signIn.Defect is { } defect && !FakeDefects.IsKnown(defect))
        {
            await PlainAsync(context.Response, "This is not a defect the fake knows.");
            return;
        }

        var answer = new List<KeyValuePair<string, string?>>();
        if (signIn.Person == FakePeople.Cancel || signIn.Defect == FakeDefects.ProviderError)
        {
            answer.Add(new("error", signIn.Person == FakePeople.Cancel ? "access_denied" : "server_error"));
            answer.Add(new("error_description", RefusalDescription));
        }
        else
        {
            if (FakePeople.ClaimsFor(site.Kind, signIn) is not { } claims)
            {
                await PlainAsync(context.Response, "This provider has no such person, or the person has no such claim to leave out.");
                return;
            }

            var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            _codes[code] = new Grant(clientId, redirectUri, nonce, challenge, claims, TokenIssuerOf(site, context.Request, claims, signIn.Defect), signIn.Defect);
            answer.Add(new("code", code));
        }

        answer.Add(new("state", state));
        if (site.Kind == FakeKinds.Google)
        {
            // Google advertises authorization_response_iss_parameter_supported: OpenIddict then requires iss on every answer (Amendment A5).
            answer.Add(new("iss", site.Web ? GoogleIssuer : IssuerOf(site.Kind, context.Request)));
        }

        context.Response.Redirect(QueryHelpers.AddQueryString(redirectUri, answer));
    }

    private string TokenIssuerOf(Site site, HttpRequest request, Dictionary<string, object> claims, string? defect)
    {
        var wrong = defect == FakeDefects.WrongIssuer;
        if (!site.Web)
        {
            return IssuerOf(site.Kind, request) + (wrong ? "-other" : "");
        }

        if (site.Kind == FakeKinds.Google)
        {
            return wrong ? "https://evil.example" : GoogleIssuer;
        }

        var tenant = wrong ? OtherTenant : claims.TryGetValue("tid", out var tid) ? (string)tid : FakePeople.WorkTenant;
        return $"https://login.microsoftonline.com/{tenant}/v2.0";
    }

    private async Task TokenAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !context.Request.HasFormContentType)
        {
            await RefuseAsync(context.Response, "invalid_request", context.RequestAborted);
            return;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var clientId = form.TryGetValue("client_id", out var formClient) ? formClient.ToString() : ClientIdOfBasic(context.Request);
        if (!string.Equals(form["grant_type"].ToString(), "authorization_code", StringComparison.Ordinal)
            || !_codes.TryRemove(form["code"].ToString(), out var grant)
            || grant.Defect == FakeDefects.RefuseCode
            || !string.Equals(clientId, grant.ClientId, StringComparison.Ordinal)
            || !string.Equals(form["redirect_uri"].ToString(), grant.RedirectUri, StringComparison.Ordinal)
            || !string.Equals(S256(form["code_verifier"].ToString()), grant.Challenge, StringComparison.Ordinal))
        {
            await RefuseAsync(context.Response, "invalid_grant", context.RequestAborted);
            return;
        }

        if (grant.Defect == FakeDefects.RefuseClient)
        {
            await RefuseAsync(context.Response, "invalid_client", context.RequestAborted);   // a wrong or lapsed client secret
            return;
        }

        var identityToken = Mint(grant);
        _identityTokens.Enqueue(identityToken);
        await WriteJsonAsync(
            context.Response,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["access_token"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                ["token_type"] = "Bearer",
                ["expires_in"] = 3600,
                ["scope"] = "openid email profile",
                ["id_token"] = identityToken,
            },
            context.RequestAborted);
    }

    private string Mint(Grant grant)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var claims = new Dictionary<string, object>(grant.Claims, StringComparer.Ordinal)
        {
            ["nonce"] = grant.Defect == FakeDefects.WrongNonce ? "another-nonce" : grant.Nonce,
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = grant.Issuer,
            Audience = grant.Defect == FakeDefects.WrongAudience ? "another-client" : grant.ClientId,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(grant.Defect == FakeDefects.BadSignature ? _otherKey : _key) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private Dictionary<string, object> KeySet()
    {
        var key = _key.ExportParameters(includePrivateParameters: false);
        var published = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["kid"] = KeyId,
            ["n"] = Base64UrlEncoder.Encode(key.Modulus!),   // RSAParameters holds both for a key made here; `!` for the nullable annotation
            ["e"] = Base64UrlEncoder.Encode(key.Exponent!),
        };
        return new Dictionary<string, object>(StringComparer.Ordinal) { ["keys"] = new object[] { published } };
    }

    /// <summary>The page of a browser that came without a person: the people, a free address with a "verified" box, and Cancel.</summary>
    private static async Task PageAsync(HttpContext context, Site site)
    {
        var request = context.Request;
        var here = (request.Path.Value ?? "/") + request.QueryString.Value;
        var name = site.Kind == FakeKinds.Google ? "Google" : "Microsoft";
        (string Person, string Label)[] people = site.Kind == FakeKinds.Google
            ? [(FakePeople.Google, $"Ola ({FakePeople.GoogleEmail}, verified)")]
            : [(FakePeople.MicrosoftPersonal, $"Ola, a personal account ({FakePeople.PersonalEmail})"),
               (FakePeople.MicrosoftWork, $"Bo, a work account ({FakePeople.WorkEmail}, not verified)")];

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Fake ").Append(name).Append("</title></head><body>");
        html.Append("<h1>Sign in to the fake ").Append(name).Append("</h1><p>The test provider of Auth-Core. Choose who signs in.</p><ul>");
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

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string S256(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>The client id of <c>client_secret_basic</c> (RFC 6749 2.3.1: both parts form-urlencoded, then Base64); <see langword="null"/> when there is none.</summary>
    private static string? ClientIdOfBasic(HttpRequest request)
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
            return colon < 0 ? null : WebUtility.UrlDecode(pair[..colon]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? One(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var values) && values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;

    private static async Task WriteJsonAsync(HttpResponse response, Dictionary<string, object> body, CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.Body, body, cancellationToken: cancellationToken);
    }

    private static async Task RefuseAsync(HttpResponse response, string error, CancellationToken cancellationToken)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await WriteJsonAsync(
            response,
            new Dictionary<string, object>(StringComparer.Ordinal) { ["error"] = error, ["error_description"] = RefusalDescription },
            cancellationToken);
    }

    private static async Task PlainAsync(HttpResponse response, string text)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        response.ContentType = "text/plain; charset=utf-8";
        await response.WriteAsync(text);
    }

    private sealed record Site(string Kind, bool Web, Endpoint Endpoint);

    private sealed record Grant(
        string ClientId, string RedirectUri, string Nonce, string Challenge, Dictionary<string, object> Claims, string Issuer, string? Defect);
}
