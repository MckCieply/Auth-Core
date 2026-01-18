# Refresh and Logout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0002
- **Date:** 2026-01-13
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0002-refresh-and-logout.md`](../specs/0002-refresh-and-logout.md)
  — the plan argues from the spec; where they disagree, **the spec wins** and the
  disagreement is a finding (see [`docs/workflow.md`](../../workflow.md)).

**Goal:** Login sets an `HttpOnly` refresh cookie; `POST /auth/refresh` trades it
for a new access token and a rotated cookie; `POST /auth/logout` revokes the
session and clears the cookie; a background job prunes old token entries.

**Architecture:** `/auth/refresh` becomes a second URI of the OpenIddict token
endpoint, running the refresh-token grant. Its only input is the cookie: a small
extraction handler turns the cookie into an OpenIddict refresh request, and a
response handler moves the new refresh token out of the body into `Set-Cookie`.
Rotation, the reuse grace window and family revocation are OpenIddict's own
(rolling **reference** refresh tokens, one ad-hoc authorization per login = the
"family"); this slice configures them and proves them with tests. The absolute
cap is ours: a session-start claim that lives only in the refresh token.
`/auth/logout` is a plain minimal-API endpoint that revokes the family through
the OpenIddict managers.

**Tech Stack:** as plan 0001 (.NET 10, ASP.NET Core minimal APIs, OpenIddict
7.7.1, EF Core 10 + PostgreSQL 16, xUnit v3, Testcontainers). New, tests only:
`Microsoft.Extensions.TimeProvider.Testing` (MIT) for a controllable clock.

## Global Constraints

- Everything in plan 0001 → Global Constraints still holds (central package
  versions, licences, `TreatWarningsAsErrors`, no secrets in repo or logs,
  Conventional Commits, local-only workflow).
- Cookie: name **`auth_rt`**, attributes exactly `HttpOnly; Secure;
  SameSite=Strict; Path=/auth; Max-Age=<seconds>`. No `Domain`, no `Expires`.
  `Secure` is always set, whatever the request scheme.
- Lifetimes are **constants in code**, not configuration: sliding **14 days**,
  absolute cap **30 days**, reuse grace **15 seconds**. Access token: 10 minutes
  (unchanged).
- Refresh success: `200`, body exactly `{"access_token":"<jwt>"}`.
- Refresh failure (missing, expired, revoked, reused token; missing user; cap
  reached): `401`, body exactly `{"error":"invalid_grant"}`, no `Set-Cookie`.
- Wrong HTTP method on `/auth/refresh`: `400 invalid_request`.
- Logout: always `204` with `Set-Cookie: auth_rt=; Max-Age=0` (same attributes).
- The refresh token never appears in a response body or a log line.
- Login's response body and the access token's claim set stay exactly as built
  in slice 1. The existing `LoginTests` must pass **unedited**.
- No new migration: the OpenIddict tables already exist.
- Gate for every task: `dotnet build -warnaserror && dotnet test`. Before the
  last commit also the hermetic run:
  `Auth__Tokens__Audience=x Auth__Tokens__Issuer=http://x/auth dotnet test`.

## Review Focus

The spec does not name these, but a browser or an attacker would hit them. Each
line has a pinning test in the task named in brackets.

1. **Trailing slash:** `/auth/refresh/` and `/auth/logout/` behave exactly like
   the plain paths. Slice 1 shipped a `500` on `/auth/login/`; OpenIddict matches
   endpoint URIs with an optional trailing slash, so every path check must too.
   [Task 2, Task 5]
2. **Hostile cookie values:** an empty value, percent-encoded NUL or CR/LF, a
   4 KiB string, a JWT-shaped string, a real access token → `401` on refresh and
   `204` on logout, never `500`. [Task 2, Task 5]
3. **The request body is not an input:** a body that carries valid credentials or
   another refresh token neither stands in for a missing cookie nor overrides a
   present one. [Task 2]
4. **Two sessions of one user are independent:** reuse detection or logout in one
   session leaves the other session working. [Task 3, Task 5]
5. **Real concurrency, not only a late retry:** two refreshes sent at the same
   instant with the same cookie both succeed. This is the case the grace window
   exists for. [Task 3]

## File Structure

```
Directory.Packages.props                         + Microsoft.Extensions.TimeProvider.Testing
src/Auth.Server/
  Program.cs                                     + TimeProvider, /auth/refresh, /auth/logout, pruning
  Tokens/OpenIddictSetup.cs                      + refresh flow, reference tokens, lifetimes, handlers
  Login/LoginEndpoint.cs                         + session-start claim, refresh lifetime
  Sessions/SessionPolicy.cs                      lifetime constants + cap arithmetic
  Sessions/RefreshCookie.cs                      the auth_rt cookie: read, write, clear
  Sessions/RefreshTokenIssuanceHandler.cs        ProcessSignIn: always issue a refresh token
  Sessions/SessionResponseHandler.cs             ApplyTokenResponse: token → cookie, refresh body, uniform 401
  Sessions/RefreshRequestHandler.cs              ExtractTokenRequest: cookie → refresh grant
  Sessions/RefreshEndpoint.cs                    pass-through: user exists, cap, sign in
  Sessions/LogoutEndpoint.cs                     revoke family, clear cookie
  Sessions/TokenPruner.cs                        one pruning pass
  Sessions/TokenPruningService.cs                hourly BackgroundService around TokenPruner
tests/Auth.IntegrationTests/
  Auth.IntegrationTests.csproj                   + TimeProvider.Testing
  Infrastructure/AuthAppFactory.cs               + WithClock(TimeProvider)
  Infrastructure/SessionApi.cs                   login/refresh/logout over HTTP with an explicit cookie
  Infrastructure/SessionTestBase.cs              factory + client + fake clock for session tests
  LoginCookieTests.cs  RefreshTests.cs  RefreshReuseTests.cs
  SessionPolicyTests.cs  SessionLifetimeTests.cs  LogoutTests.cs  TokenPruningTests.cs
scripts/e2e-refresh.sh                           real-network refresh/logout sequence
docs/superpowers/plans/0002-acceptance-map.md    criteria → tests
README.md                                        quickstart + status
```

Session code lives in a new `Sessions/` folder: `Login/` stays about the
password grant. `Login/LoginResponseShaper.cs` is **not** modified.

**Every OpenIddict type and method name in this plan must be checked against
the installed 7.7.1 package before use** (as plan 0001 Task 4 did). Record any
name that differs in the commit body.

---

### Task 1: Login issues the refresh cookie

**Files:**
- Create: `src/Auth.Server/Sessions/SessionPolicy.cs`, `src/Auth.Server/Sessions/RefreshCookie.cs`,
  `src/Auth.Server/Sessions/RefreshTokenIssuanceHandler.cs`,
  `src/Auth.Server/Sessions/SessionResponseHandler.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/SessionApi.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/SessionTestBase.cs`
- Modify: `Directory.Packages.props`, `tests/Auth.IntegrationTests/Auth.IntegrationTests.csproj`,
  `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`,
  `src/Auth.Server/Tokens/OpenIddictSetup.cs`, `src/Auth.Server/Program.cs`
- Test: `tests/Auth.IntegrationTests/LoginCookieTests.cs`

**Interfaces:**
- Produces: `SessionPolicy` with `static readonly TimeSpan SlidingLifetime` (14 d),
  `AbsoluteLifetime` (30 d), `ReuseLeeway` (15 s).
- Produces: `RefreshCookie` with `const string Name = "auth_rt"`, `const string Path = "/auth"`,
  `const int MaxTokenLength = 256`,
  `static void Append(HttpResponse response, string token, TimeSpan lifetime)`,
  `static void Clear(HttpResponse response)`,
  `static bool TryRead(HttpRequest request, [NotNullWhen(true)] out string? token)`
  (`false` when the cookie is missing, empty, longer than `MaxTokenLength`, or
  contains a control character).
- Produces: `RefreshTokenIssuanceHandler.Descriptor`, `SessionResponseHandler.Descriptor`.
- Produces (tests): `AuthAppFactory WithClock(TimeProvider clock)`;
  `SessionTestBase` (protected `Postgres`, `Keys`, `Clock` (`FakeTimeProvider`,
  starts at real now, frozen), `Factory`, `Client`);
  `SessionApi` (`RefreshPath`, `LogoutPath`, `CookieName`, `record Session(string AccessToken,
  string RefreshToken, SetCookieHeaderValue Cookie)`, `CreateClient`, `RefreshCookieOf`,
  `LoginAsync`, `Refresh`, `RefreshOk`, `Logout`, `AssertInvalidGrantAsync`).
- **Spike gate (spec "To verify": no `scope` claim; ad-hoc authorization).**
  Primary path: `RefreshTokenIssuanceHandler` sets `GenerateRefreshToken` and
  `IncludeRefreshToken` on the sign-in context, so no `offline_access` scope is
  ever granted. If the installed OpenIddict ignores that or creates no
  authorization, use the **fallback**: grant `Scopes.OfflineAccess` on the
  principal in the endpoint, and strip the scope claim from
  `context.AccessTokenPrincipal` in a handler ordered before access-token
  generation. The tests below are identical either way. Record the path taken in
  the commit body. If neither keeps `LoginTests` green unedited, stop and
  escalate to the owner.

- [ ] **Step 1: Write the test infrastructure.** Add a `PackageVersion` for
  `Microsoft.Extensions.TimeProvider.Testing` (MIT) to `Directory.Packages.props`,
  pinned to the latest stable version
  (`dotnet package search Microsoft.Extensions.TimeProvider.Testing --exact-match`),
  and the matching `PackageReference` in the test project. Copy the resolved
  version into the commit body.

`AuthAppFactory` (add; keep everything else):

```csharp
private TimeProvider? _clock;

/// <summary>Replaces the host's clock, so a test can move time forward.</summary>
public AuthAppFactory WithClock(TimeProvider clock)
{
    _clock = clock;
    return this;
}

// at the end of ConfigureWebHost:
if (_clock is { } clock)
{
    builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton(clock)));
}
```

`Infrastructure/SessionTestBase.cs`:

```csharp
public abstract class SessionTestBase : IAsyncLifetime
{
    protected SessionTestBase(PostgresFixture postgres, KeyMaterialFixture keys)
    {
        Postgres = postgres;
        Keys = keys;
        // Starts at the real time and only ever moves forward, so tokens stay valid for system-clock validators.
        Clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Factory = new AuthAppFactory(postgres, keys).WithClock(Clock);
    }

    protected PostgresFixture Postgres { get; }
    protected KeyMaterialFixture Keys { get; }
    protected FakeTimeProvider Clock { get; }
    protected AuthAppFactory Factory { get; }
    protected HttpClient Client { get; private set; } = null!;

    public ValueTask InitializeAsync()
    {
        Client = SessionApi.CreateClient(Factory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
```

`Infrastructure/SessionApi.cs`:

```csharp
/// <summary>
/// Drives login, refresh and logout the way a browser would, but carries the cookie by hand: the cookie is
/// <c>Secure</c> and the test server speaks plain HTTP, so an automatic cookie container would never send it.
/// </summary>
public static class SessionApi
{
    public const string RefreshPath = "/auth/refresh";
    public const string LogoutPath = "/auth/logout";
    public const string CookieName = "auth_rt";

    public sealed record Session(string AccessToken, string RefreshToken, SetCookieHeaderValue Cookie);

    public static HttpClient CreateClient(AuthAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    public static SetCookieHeaderValue RefreshCookieOf(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values), "The response has no Set-Cookie header.");
        return Assert.Single(SetCookieHeaderValue.ParseList(values.ToList()), c => c.Name == CookieName);
    }

    public static async Task<Session> LoginAsync(HttpClient client, AuthAppFactory factory)
    {
        using var response = await LoginApi.Login(client, factory.SeedEmail, factory.SeedPassword);
        return await ReadSessionAsync(response);
    }

    public static Task<HttpResponseMessage> Refresh(HttpClient client, string? refreshToken, string path = RefreshPath) =>
        client.SendAsync(WithCookie(new HttpRequestMessage(HttpMethod.Post, path), refreshToken));

    public static async Task<Session> RefreshOk(HttpClient client, string refreshToken)
    {
        using var response = await Refresh(client, refreshToken);
        return await ReadSessionAsync(response);
    }

    public static Task<HttpResponseMessage> Logout(HttpClient client, string? refreshToken, string path = LogoutPath) =>
        client.SendAsync(WithCookie(new HttpRequestMessage(HttpMethod.Post, path), refreshToken));

    /// <summary>The one refresh failure: 401, the exact body, and the cookie left alone.</summary>
    public static async Task AssertInvalidGrantAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"Expected 401, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("""{"error":"invalid_grant"}""", raw);
        Assert.False(response.Headers.Contains("Set-Cookie"), "A failed refresh must not write the cookie.");
    }

    private static HttpRequestMessage WithCookie(HttpRequestMessage request, string? refreshToken)
    {
        if (refreshToken is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{CookieName}={refreshToken}");
        }

        return request;
    }

    private static async Task<Session> ReadSessionAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)response.StatusCode}: {raw}");
        using var body = JsonDocument.Parse(raw);
        var cookie = RefreshCookieOf(response);
        var accessToken = body.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("The response has no access_token.");
        return new Session(accessToken, cookie.Value.Value!, cookie);
    }
}
```

- [ ] **Step 2: Write the failing tests** (`LoginCookieTests : SessionTestBase`, constructor
  passes `postgres, keys` to the base)

```csharp
[Fact]
public async Task Login_sets_the_refresh_cookie_with_adr_0004_attributes()   // criterion 8
{
    using var response = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

    Assert.Single(response.Headers.GetValues("Set-Cookie"));
    var cookie = SessionApi.RefreshCookieOf(response);
    Assert.True(cookie.HttpOnly);
    Assert.True(cookie.Secure);
    Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
    Assert.Equal("/auth", cookie.Path.Value);
    Assert.Equal(TimeSpan.FromDays(14), cookie.MaxAge);
    Assert.False(cookie.Domain.HasValue);
    Assert.Null(cookie.Expires);
}

[Fact]
public async Task Login_body_is_unchanged_and_never_carries_the_refresh_token()   // criterion 8, Decision 16
{
    using var response = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);
    var cookie = SessionApi.RefreshCookieOf(response);
    var raw = await response.Content.ReadAsStringAsync();

    using var body = JsonDocument.Parse(raw);
    Assert.Equal(["access_token", "status"], body.RootElement.EnumerateObject().Select(p => p.Name).Order());
    Assert.DoesNotContain(cookie.Value.Value!, raw, StringComparison.Ordinal);
}

[Fact]
public async Task Refresh_cookie_is_an_opaque_reference_with_a_stored_family()   // Decision 9, spec "To verify"
{
    var session = await SessionApi.LoginAsync(Client, Factory);

    Assert.InRange(session.RefreshToken.Length, 32, 128);
    Assert.DoesNotContain('.', session.RefreshToken);   // not a JWT/JWE

    using var scope = Factory.Services.CreateScope();
    var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
    var entry = await tokens.FindByReferenceIdAsync(session.RefreshToken);
    Assert.NotNull(entry);
    Assert.Equal("refresh_token", await tokens.GetTypeAsync(entry));
    Assert.False(string.IsNullOrEmpty(await tokens.GetAuthorizationIdAsync(entry)), "The refresh token has no authorization (family).");
}

[Fact]
public async Task Failed_login_sets_no_cookie()
{
    using var response = await LoginApi.Login(Client, Factory.SeedEmail, "definitely-wrong");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    Assert.False(response.Headers.Contains("Set-Cookie"));
}
```

- [ ] **Step 3: Run them and confirm they fail.** Run
  `dotnet test -- --filter-class "*LoginCookieTests"`. Expected: FAIL (no `Set-Cookie`).
- [ ] **Step 4: Implement.**

`Program.cs`: add `builder.Services.TryAddSingleton(TimeProvider.System);` before
`AddAuthOpenIddict` (OpenIddict takes its clock from DI).

`Sessions/SessionPolicy.cs`:

```csharp
/// <summary>
/// How long a session lives. Constants on purpose (spec 0002, Decision 10): they are part of the contract, not knobs.
/// </summary>
public static class SessionPolicy
{
    /// <summary>A refresh token is usable this long after it was issued; each use issues a fresh one.</summary>
    public static readonly TimeSpan SlidingLifetime = TimeSpan.FromDays(14);

    /// <summary>No session outlives this, however active it is.</summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);

    /// <summary>A just-consumed refresh token is still accepted this long: an honest double-submit is not theft.</summary>
    public static readonly TimeSpan ReuseLeeway = TimeSpan.FromSeconds(15);
}
```

`Sessions/RefreshCookie.cs`:

```csharp
/// <summary>The refresh cookie of ADR 0004. The only place that knows its name and attributes.</summary>
public static class RefreshCookie
{
    public const string Name = "auth_rt";
    public const string Path = "/auth";

    /// <summary>Longest value treated as a token. A reference token is 43 characters; anything far longer is junk.</summary>
    public const int MaxTokenLength = 256;

    public static void Append(HttpResponse response, string token, TimeSpan lifetime) =>
        response.Cookies.Append(Name, token, Options(lifetime));

    public static void Clear(HttpResponse response) =>
        response.Cookies.Append(Name, string.Empty, Options(TimeSpan.Zero));

    /// <summary>
    /// Reads the cookie. <see langword="false"/> when it is missing, empty, too long or holds a control character:
    /// such a value cannot be a token and must never reach the token store.
    /// </summary>
    public static bool TryRead(HttpRequest request, [NotNullWhen(true)] out string? token)
    {
        if (request.Cookies.TryGetValue(Name, out var value)
            && value.Length is > 0 and <= MaxTokenLength
            && !value.Any(char.IsControl))
        {
            token = value;
            return true;
        }

        token = null;
        return false;
    }

    // Secure is unconditional: TLS ends at the reverse proxy, so the request scheme seen here says nothing.
    private static CookieOptions Options(TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        MaxAge = lifetime,
        IsEssential = true,
    };
}
```

`Sessions/RefreshTokenIssuanceHandler.cs`:

```csharp
/// <summary>
/// Every sign-in on the token endpoint gets a refresh token. OpenIddict would otherwise require the
/// <c>offline_access</c> scope, which would also put a <c>scope</c> claim into the access token and break its
/// pinned claim set (spec 0002, Decision 16).
/// </summary>
public sealed class RefreshTokenIssuanceHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>
{
    /// <summary>
    /// After OpenIddict decided which tokens to generate, and before it attaches the ad-hoc authorization, which it
    /// creates only when a refresh token is generated. That authorization is the token family.
    /// </summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
            .UseSingletonHandler<RefreshTokenIssuanceHandler>()
            .SetOrder(OpenIddictServerHandlers.EvaluateGeneratedTokens.Descriptor.Order + 500)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.EndpointType is OpenIddictServerEndpointType.Token)
        {
            context.GenerateRefreshToken = true;
            context.IncludeRefreshToken = true;
        }

        return ValueTask.CompletedTask;
    }
}
```

`Sessions/SessionResponseHandler.cs`:

```csharp
/// <summary>
/// Moves the refresh token of a successful token response out of the body and into the <c>auth_rt</c> cookie. The
/// refresh token never reaches a response body (spec 0002, criterion 8).
/// </summary>
public sealed class SessionResponseHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ApplyTokenResponseContext>
{
    /// <summary>
    /// Before <see cref="LoginResponseShaper"/>, which rebuilds the body and would drop the refresh token before
    /// this handler could read it.
    /// </summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ApplyTokenResponseContext>()
            .AddFilter<OpenIddictServerAspNetCoreHandlerFilters.RequireHttpRequest>()
            .UseSingletonHandler<SessionResponseHandler>()
            .SetOrder(LoginResponseShaper.Descriptor.Order - 100)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ApplyTokenResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var http = context.Transaction.GetHttpRequest()?.HttpContext
            ?? throw new InvalidOperationException("The token endpoint was reached without an ASP.NET Core request.");

        if (!string.IsNullOrEmpty(context.Response.Error))
        {
            return ValueTask.CompletedTask;
        }

        // Fail closed: a session without its cookie must not be handed out.
        var refreshToken = context.Response.RefreshToken
            ?? throw new InvalidOperationException("A successful token response has no refresh token.");

        RefreshCookie.Append(http.Response, refreshToken, SessionPolicy.SlidingLifetime);
        context.Response.RefreshToken = null;

        return ValueTask.CompletedTask;
    }
}
```

`OpenIddictSetup.cs`: `AllowPasswordFlow().AllowRefreshTokenFlow().AcceptAnonymousClients()`;
after `SetAccessTokenLifetime(...)` add `.SetRefreshTokenLifetime(SessionPolicy.SlidingLifetime)`
and `.UseReferenceRefreshTokens()`; register
`options.AddEventHandler(RefreshTokenIssuanceHandler.Descriptor).AddEventHandler(SessionResponseHandler.Descriptor);`.
Update the XML summary (the password flow is no longer "the only flow").

- [ ] **Step 5: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: all PASS, including every slice 1 test **unedited** — in particular
  `LoginTests.Token_payload_claim_set_is_exactly_the_contract_plus_openiddict_metadata`.
- [ ] **Step 6: Commit** — `feat(session): issue the refresh cookie at login`

### Task 2: `POST /auth/refresh`

**Files:**
- Create: `src/Auth.Server/Sessions/RefreshRequestHandler.cs`, `src/Auth.Server/Sessions/RefreshEndpoint.cs`
- Modify: `src/Auth.Server/Sessions/SessionResponseHandler.cs`, `src/Auth.Server/Tokens/OpenIddictSetup.cs`,
  `src/Auth.Server/Program.cs`; comments only in `Login/JsonLoginRequestHandler.cs`,
  `Login/UnhandledTokenRequestGuard.cs`, `Login/LoginResponseShaper.cs` (they speak of refresh as future work)
- Test: `tests/Auth.IntegrationTests/RefreshTests.cs`

**Interfaces:**
- Consumes: `RefreshCookie.TryRead`, `SessionResponseHandler`, `SessionApi`, `SessionTestBase` (Task 1);
  `JsonLoginRequestHandler.Descriptor`, `UnhandledTokenRequestGuard` (slice 1).
- Produces: `RefreshRequestHandler` with `const string RefreshPath = "/auth/refresh"`,
  `internal static bool IsRefreshPath(PathString path)` (plain path and trailing slash,
  case-insensitive), `Descriptor` (order `JsonLoginRequestHandler.Descriptor.Order + 250`:
  after the login extraction, before `UnhandledTokenRequestGuard` at `+ 500`).
- Produces: `static Task<IResult> RefreshEndpoint.HandleAsync(HttpContext http,
  UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens)`.
- Produces: on the refresh path `SessionResponseHandler` shapes success to
  `{"access_token"}` and every error except `invalid_request` to `401 {"error":"invalid_grant"}`.

- [ ] **Step 1: Write the failing tests** (`RefreshTests : SessionTestBase`)

```csharp
[Fact]
public async Task Refresh_returns_a_new_access_token_and_a_rotated_cookie()   // criteria 1, 8
{
    var login = await SessionApi.LoginAsync(Client, Factory);

    using var response = await SessionApi.Refresh(Client, login.RefreshToken);
    var raw = await response.Content.ReadAsStringAsync();

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    using var body = JsonDocument.Parse(raw);
    Assert.Equal(["access_token"], body.RootElement.EnumerateObject().Select(p => p.Name));
    Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected");

    var cookie = SessionApi.RefreshCookieOf(response);
    Assert.NotEqual(login.RefreshToken, cookie.Value.Value);
    Assert.True(cookie.HttpOnly);
    Assert.True(cookie.Secure);
    Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
    Assert.Equal("/auth", cookie.Path.Value);
    Assert.DoesNotContain(cookie.Value.Value!, raw, StringComparison.Ordinal);

    var accessToken = body.RootElement.GetProperty("access_token").GetString()!;
    Assert.NotEqual(login.AccessToken, accessToken);
    var result = await Jwks.ValidateAsync(accessToken, await Jwks.FetchAsync(Client), new TokenOptions());
    Assert.True(result.IsValid, result.Exception?.Message);
    Assert.Equal((await Factory.SeedUserIdAsync()).ToString(), new JsonWebToken(accessToken).Subject);
}

[Fact]
public async Task Refreshed_access_token_has_the_login_claim_set()
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    var refreshed = await SessionApi.RefreshOk(Client, login.RefreshToken);

    static string[] ClaimTypes(string jwt) =>
        new JsonWebToken(jwt).Claims.Select(c => c.Type).Distinct().Order(StringComparer.Ordinal).ToArray();
    Assert.Equal(ClaimTypes(login.AccessToken), ClaimTypes(refreshed.AccessToken));
    Assert.Equal("at+jwt", new JsonWebToken(refreshed.AccessToken).Typ);
}

[Fact]
public async Task Rotation_stays_in_one_family_and_can_repeat()   // spec "To verify": family
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    var first = await SessionApi.RefreshOk(Client, login.RefreshToken);
    var second = await SessionApi.RefreshOk(Client, first.RefreshToken);
    var third = await SessionApi.RefreshOk(Client, second.RefreshToken);

    using var scope = Factory.Services.CreateScope();
    var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
    async Task<string?> FamilyOf(string reference) =>
        await tokens.GetAuthorizationIdAsync((await tokens.FindByReferenceIdAsync(reference))!);
    var family = await FamilyOf(login.RefreshToken);
    Assert.False(string.IsNullOrEmpty(family));
    Assert.Equal(family, await FamilyOf(first.RefreshToken));
    Assert.Equal(family, await FamilyOf(third.RefreshToken));
}

[Fact]
public async Task Every_refresh_failure_returns_the_same_401()   // criterion 7
{
    var login = await SessionApi.LoginAsync(Client, Factory);

    using var missing = await SessionApi.Refresh(Client, refreshToken: null);
    using var empty = await SessionApi.Refresh(Client, "");
    using var unknown = await SessionApi.Refresh(Client, "not-a-real-reference-0123456789abcdef");

    using (var scope = Factory.Services.CreateScope())
    {
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(Factory.SeedEmail);
        Assert.True((await users.DeleteAsync(user!)).Succeeded);
    }

    using var userGone = await SessionApi.Refresh(Client, login.RefreshToken);   // Decision 11

    foreach (var response in new[] { missing, empty, unknown, userGone })
    {
        await SessionApi.AssertInvalidGrantAsync(response);
        Assert.Equal(missing.Content.Headers.ContentType, response.Content.Headers.ContentType);
        Assert.Equal(LoginApi.HeaderNames(missing), LoginApi.HeaderNames(response));
    }
}

public static TheoryData<string> HostileCookieValues => new()
{
    "%00",                       // NUL
    "a%0D%0Ab",                  // CR LF
    "%22quoted%22",
    "aaa.bbb.ccc",               // JWT-shaped
    new string('x', 4000),       // far past any token length
};

[Theory]
[MemberData(nameof(HostileCookieValues))]
public async Task Hostile_cookie_value_returns_401_never_500(string value)   // Review Focus #2
{
    using var response = await SessionApi.Refresh(Client, value);

    await SessionApi.AssertInvalidGrantAsync(response);
}

[Fact]
public async Task Access_token_in_the_cookie_is_not_a_refresh_token()   // Review Focus #2
{
    var login = await SessionApi.LoginAsync(Client, Factory);

    using var response = await SessionApi.Refresh(Client, login.AccessToken);

    await SessionApi.AssertInvalidGrantAsync(response);
}

[Fact]
public async Task Request_body_cannot_stand_in_for_the_cookie()   // Review Focus #3
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    using var form = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "password", ["username"] = Factory.SeedEmail, ["password"] = Factory.SeedPassword,
        ["refresh_token"] = login.RefreshToken,
    });
    using var json = JsonContent.Create(new { email = Factory.SeedEmail, password = Factory.SeedPassword, refresh_token = login.RefreshToken });

    using var viaForm = await Client.PostAsync(SessionApi.RefreshPath, form);
    using var viaJson = await Client.PostAsync(SessionApi.RefreshPath, json);

    await SessionApi.AssertInvalidGrantAsync(viaForm);
    await SessionApi.AssertInvalidGrantAsync(viaJson);
}

[Fact]
public async Task Request_body_cannot_override_the_cookie()   // Review Focus #3
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    using var request = new HttpRequestMessage(HttpMethod.Post, SessionApi.RefreshPath)
    {
        Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = "not-a-real-reference-0123456789abcdef",
        }),
    };
    request.Headers.TryAddWithoutValidation("Cookie", $"{SessionApi.CookieName}={login.RefreshToken}");

    using var response = await Client.SendAsync(request);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // the cookie decided; the body was never read
}

[Fact]
public async Task Get_returns_400_invalid_request()   // Decision 15
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    using var request = new HttpRequestMessage(HttpMethod.Get, SessionApi.RefreshPath);
    request.Headers.TryAddWithoutValidation("Cookie", $"{SessionApi.CookieName}={login.RefreshToken}");

    using var response = await Client.SendAsync(request);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Assert.Equal("invalid_request", body.RootElement.GetProperty("error").GetString());
    Assert.False(response.Headers.Contains("Set-Cookie"));
}

[Fact]
public async Task Trailing_slash_path_refreshes_too()   // Review Focus #1
{
    var login = await SessionApi.LoginAsync(Client, Factory);

    using var ok = await SessionApi.Refresh(Client, login.RefreshToken, SessionApi.RefreshPath + "/");
    using var failed = await SessionApi.Refresh(Client, refreshToken: null, SessionApi.RefreshPath + "/");

    Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    using var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
    Assert.Equal(["access_token"], body.RootElement.EnumerateObject().Select(p => p.Name));
    await SessionApi.AssertInvalidGrantAsync(failed);
}

[Fact]
public async Task Login_path_ignores_the_refresh_cookie()
{
    var login = await SessionApi.LoginAsync(Client, Factory);

    using var response = await SessionApi.Refresh(Client, login.RefreshToken, LoginApi.Path);   // cookie, no body

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test -- --filter-class "*RefreshTests"`.
  Expected: FAIL (`404` on `/auth/refresh`).
- [ ] **Step 3: Implement.**

`Sessions/RefreshRequestHandler.cs`:

```csharp
/// <summary>
/// Turns <c>POST /auth/refresh</c> into an OpenIddict refresh-token request. The <c>auth_rt</c> cookie is the only
/// input: the body is never read, so it can neither supply a token nor switch the grant.
/// </summary>
public sealed class RefreshRequestHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ExtractTokenRequestContext>
{
    public const string RefreshPath = "/auth/refresh";

    /// <summary>
    /// <see langword="true"/> for <c>/auth/refresh</c> and <c>/auth/refresh/</c>, case-insensitive: OpenIddict
    /// matches the token endpoint with an optional trailing slash, so this check must agree with it.
    /// </summary>
    internal static bool IsRefreshPath(PathString path) =>
        path.Equals(RefreshPath, StringComparison.OrdinalIgnoreCase)
        || path.Equals(RefreshPath + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>After <see cref="JsonLoginRequestHandler"/>, before <see cref="UnhandledTokenRequestGuard"/>.</summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ExtractTokenRequestContext>()
            .AddFilter<OpenIddictServerAspNetCoreHandlerFilters.RequireHttpRequest>()
            .UseSingletonHandler<RefreshRequestHandler>()
            .SetOrder(JsonLoginRequestHandler.Descriptor.Order + 250)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Transaction.GetHttpRequest()
            ?? throw new InvalidOperationException("The token endpoint was reached without an ASP.NET Core request.");

        if (!IsRefreshPath(request.Path))
        {
            return ValueTask.CompletedTask;
        }

        if (!HttpMethods.IsPost(request.Method))
        {
            context.Reject(error: Errors.InvalidRequest, description: "The refresh endpoint only accepts POST requests.");
            return ValueTask.CompletedTask;
        }

        if (!RefreshCookie.TryRead(request, out var token))
        {
            context.Reject(error: Errors.InvalidGrant);
            return ValueTask.CompletedTask;
        }

        context.Request = new OpenIddictRequest
        {
            GrantType = GrantTypes.RefreshToken,
            RefreshToken = token,
        };

        return ValueTask.CompletedTask;
    }
}
```

`Sessions/RefreshEndpoint.cs`:

```csharp
/// <summary>
/// The pass-through half of <c>POST /auth/refresh</c>. By the time it runs, OpenIddict has validated the refresh
/// token from the cookie (unknown, expired, revoked and reused tokens never get here). This endpoint checks that the
/// user still exists and asks OpenIddict to issue the next pair of tokens.
/// </summary>
public static class RefreshEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(tokens);

        var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var subject = result.Principal?.GetClaim(Claims.Subject);

        // Only existence is checked (spec 0002, Decision 11). Ending sessions on a password change is the
        // forgot/reset spec's job.
        var user = subject is null ? null : await users.FindByIdAsync(subject);
        if (user is null)
        {
            return InvalidGrant();
        }

        // Start from the refresh token's own claims, so OpenIddict's internal ones (the authorization id that makes
        // the family) carry over to the new tokens.
        var identity = new ClaimsIdentity(
            result.Principal!.Claims,
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);
        identity.SetClaim(Claims.Subject, user.Id.ToString());

        var principal = new ClaimsPrincipal(identity);
        principal.SetResources(tokens.Value.Audience);
        principal.SetDestinations(static claim => claim.Type == Claims.Subject ? [Destinations.AccessToken] : []);

        return Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>Rejects through OpenIddict, so the failure takes the same path (and shape) as its own rejections.</summary>
    internal static IResult InvalidGrant() => Results.Forbid(
        properties: new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
        }),
        authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
```

`SessionResponseHandler.HandleAsync` — replace the body after the `http` lookup with:

```csharp
        var isRefresh = RefreshRequestHandler.IsRefreshPath(http.Request.Path);

        if (!string.IsNullOrEmpty(context.Response.Error))
        {
            // invalid_request is the wrong-method case: a malformed request, answered like login's 400. Every other
            // error on this path is a refresh failure and gets the one uniform answer (spec 0002, criterion 7).
            if (isRefresh && !string.Equals(context.Response.Error, Errors.InvalidRequest, StringComparison.Ordinal))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response = new OpenIddictResponse { Error = Errors.InvalidGrant };
            }

            return ValueTask.CompletedTask;
        }

        // Fail closed: a session without its cookie must not be handed out.
        var refreshToken = context.Response.RefreshToken
            ?? throw new InvalidOperationException("A successful token response has no refresh token.");

        RefreshCookie.Append(http.Response, refreshToken, SessionPolicy.SlidingLifetime);
        context.Response.RefreshToken = null;

        if (isRefresh)
        {
            var accessToken = context.Response.AccessToken
                ?? throw new InvalidOperationException("A successful token response has no access token.");

            // Replace, rather than remove one by one: whatever else OpenIddict adds must not leak into the contract.
            context.Response = new OpenIddictResponse { AccessToken = accessToken };
        }

        return ValueTask.CompletedTask;
```

The handler must run **after** OpenIddict's handler that sets the HTTP status
for an error response, or the `401` is overwritten with `400`. If
`Every_refresh_failure_returns_the_same_401` sees `400`, move the order later,
keeping it before `LoginResponseShaper` and the JSON writer.

`OpenIddictSetup.cs`: `SetTokenEndpointUris(JsonLoginRequestHandler.LoginPath.TrimStart('/'),
RefreshRequestHandler.RefreshPath.TrimStart('/'))`; add
`.AddEventHandler(RefreshRequestHandler.Descriptor)` next to the other extraction handlers.
`Program.cs`: `app.MapPost(RefreshRequestHandler.RefreshPath, RefreshEndpoint.HandleAsync);`.
Bring the three slice 1 comments up to date (refresh is no longer "a future flow").

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: all PASS; no `500` and no refresh token value in the test log.
- [ ] **Step 5: Commit** — `feat(session): add POST /auth/refresh with cookie rotation`

### Task 3: Reuse detection and the grace window

**Files:**
- Modify: `src/Auth.Server/Tokens/OpenIddictSetup.cs`
- Test: `tests/Auth.IntegrationTests/RefreshReuseTests.cs`

**Interfaces:**
- Consumes: `SessionPolicy.ReuseLeeway` (Task 1), `/auth/refresh` (Task 2),
  `SessionTestBase.Clock` (Task 1).
- **Gate (spec "To verify" #1).** Rotation, the grace window and family
  revocation are expected to be OpenIddict's own behaviour, switched on by
  configuration. If a test below cannot pass with configuration alone, **stop
  and escalate to the owner**; do not hand-roll reuse detection and do not drop
  criterion 4.
- Clock note: the tests move the fake clock by **10 s** ("inside") and **20 s**
  ("outside"). 20 s is past the 15 s window with room for real time spent in the
  test, and still inside OpenIddict's default of 30 s — so "outside" fails until
  the leeway is configured.

- [ ] **Step 1: Write the failing tests** (`RefreshReuseTests : SessionTestBase`)

```csharp
[Fact]
public async Task Consumed_token_is_forgiven_inside_the_grace_window()   // criterion 4
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    var first = await SessionApi.RefreshOk(Client, login.RefreshToken);

    Clock.Advance(TimeSpan.FromSeconds(10));
    var second = await SessionApi.RefreshOk(Client, login.RefreshToken);   // an honest retry

    Assert.NotEqual(first.RefreshToken, second.RefreshToken);
    await SessionApi.RefreshOk(Client, first.RefreshToken);    // nothing was revoked
    await SessionApi.RefreshOk(Client, second.RefreshToken);
}

[Fact]
public async Task Concurrent_double_submit_succeeds_twice()   // criterion 4, Review Focus #5
{
    var login = await SessionApi.LoginAsync(Client, Factory);

    var responses = await Task.WhenAll(
        SessionApi.Refresh(Client, login.RefreshToken),
        SessionApi.Refresh(Client, login.RefreshToken));

    foreach (var response in responses)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}

[Fact]
public async Task Consumed_token_is_rejected_after_the_grace_window()   // criterion 2
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    await SessionApi.RefreshOk(Client, login.RefreshToken);

    Clock.Advance(TimeSpan.FromSeconds(20));
    using var replay = await SessionApi.Refresh(Client, login.RefreshToken);

    await SessionApi.AssertInvalidGrantAsync(replay);
}

[Fact]
public async Task Reuse_after_the_grace_window_revokes_the_whole_family()   // criterion 3
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    var current = await SessionApi.RefreshOk(Client, login.RefreshToken);

    Clock.Advance(TimeSpan.FromSeconds(20));
    using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
    using var afterReplay = await SessionApi.Refresh(Client, current.RefreshToken);

    await SessionApi.AssertInvalidGrantAsync(replay);
    await SessionApi.AssertInvalidGrantAsync(afterReplay);   // the legitimate, post-rotation token died too
}

[Fact]
public async Task Reuse_in_one_session_leaves_another_session_alone()   // Review Focus #4
{
    var attacked = await SessionApi.LoginAsync(Client, Factory);
    var other = await SessionApi.LoginAsync(Client, Factory);
    await SessionApi.RefreshOk(Client, attacked.RefreshToken);

    Clock.Advance(TimeSpan.FromSeconds(20));
    using var replay = await SessionApi.Refresh(Client, attacked.RefreshToken);

    await SessionApi.AssertInvalidGrantAsync(replay);
    await SessionApi.RefreshOk(Client, other.RefreshToken);
}

[Fact]
public async Task Rotated_session_survives_a_host_restart()   // server-side store persists
{
    SessionApi.Session rotated;
    await using (var before = new AuthAppFactory(Postgres, Keys, Factory.DatabaseName).WithClock(Clock))
    {
        using var client = SessionApi.CreateClient(before);
        var login = await SessionApi.LoginAsync(client, before);
        rotated = await SessionApi.RefreshOk(client, login.RefreshToken);
    }

    await using var after = new AuthAppFactory(Postgres, Keys, Factory.DatabaseName).WithClock(Clock);
    using var restarted = SessionApi.CreateClient(after);

    await SessionApi.RefreshOk(restarted, rotated.RefreshToken);
}
```

- [ ] **Step 2: Run them and confirm the right ones fail.** Run
  `dotnet test -- --filter-class "*RefreshReuseTests"`. Expected:
  `Consumed_token_is_rejected_after_the_grace_window` and
  `Reuse_after_the_grace_window_revokes_the_whole_family` FAIL (default leeway is
  30 s, so the replay at +20 s is still accepted). The others PASS already.
- [ ] **Step 3: Implement.** In `OpenIddictSetup.cs`, next to the lifetimes:
  `.SetRefreshTokenReuseLeeway(SessionPolicy.ReuseLeeway)`, with a comment naming
  spec 0002 Decision 2.
- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: all PASS. Run the class five times
  (`for i in 1 2 3 4 5; do dotnet test -- --filter-class "*RefreshReuseTests" || break; done`):
  the concurrency test must not flake. A flake is a finding, not a retry.
- [ ] **Step 5: Commit** — `feat(session): set the 15 s reuse grace window and prove family revocation`

### Task 4: Sliding window and the absolute cap

**Files:**
- Modify: `src/Auth.Server/Sessions/SessionPolicy.cs`, `src/Auth.Server/Sessions/RefreshCookie.cs`,
  `src/Auth.Server/Sessions/SessionResponseHandler.cs`, `src/Auth.Server/Sessions/RefreshEndpoint.cs`,
  `src/Auth.Server/Login/LoginEndpoint.cs`
- Test: `tests/Auth.IntegrationTests/SessionPolicyTests.cs`, `tests/Auth.IntegrationTests/SessionLifetimeTests.cs`

**Interfaces:**
- Produces: `SessionPolicy.StartClaim` (`const string = "session_start"`, Unix
  seconds, invariant culture; carried **only** in the refresh token: it has no
  destination) and
  `static TimeSpan? SessionPolicy.RemainingLifetime(DateTimeOffset sessionStart, DateTimeOffset now)`
  — `null` once the cap is reached, else the shorter of `SlidingLifetime` and
  the time left to the cap.
- Produces: `internal static readonly object RefreshCookie.LifetimeItemKey` — the
  `HttpContext.Items` key under which an endpoint leaves the refresh token's
  lifetime (`TimeSpan`) for `SessionResponseHandler`.
- Changes: `LoginEndpoint.HandleAsync` and `RefreshEndpoint.HandleAsync` take a
  `TimeProvider clock` parameter (resolved from DI by minimal APIs).

- [ ] **Step 1: Write the failing tests**

`SessionPolicyTests` (plain unit tests, no host):

```csharp
public static TheoryData<double, double?> Cases => new()
{
    { 0, 14 },          // fresh session: the whole sliding window
    { 16, 14 },         // exactly 14 days left to the cap
    { 17, 13 },         // the cap is closer than the window
    { 29.5, 0.5 },
    { 30, null },       // cap reached
    { 31, null },
};

[Theory]
[MemberData(nameof(Cases))]
public void Remaining_lifetime_is_the_shorter_of_window_and_cap(double ageDays, double? expectedDays)
{
    var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    TimeSpan? expected = expectedDays is { } days ? TimeSpan.FromDays(days) : null;

    var remaining = SessionPolicy.RemainingLifetime(start, start.AddDays(ageDays));

    Assert.Equal(expected, remaining);
}
```

`SessionLifetimeTests : SessionTestBase`:

```csharp
[Fact]
public async Task Unused_session_expires_after_the_sliding_window()   // criterion 9
{
    var login = await SessionApi.LoginAsync(Client, Factory);

    Clock.Advance(TimeSpan.FromDays(14) + TimeSpan.FromMinutes(1));
    using var response = await SessionApi.Refresh(Client, login.RefreshToken);

    await SessionApi.AssertInvalidGrantAsync(response);
}

[Fact]
public async Task Each_refresh_extends_the_sliding_window()   // criterion 9
{
    var login = await SessionApi.LoginAsync(Client, Factory);

    Clock.Advance(TimeSpan.FromDays(13));
    var day13 = await SessionApi.RefreshOk(Client, login.RefreshToken);
    Clock.Advance(TimeSpan.FromDays(13));                                  // day 26: dead without sliding
    await SessionApi.RefreshOk(Client, day13.RefreshToken);
}

[Fact]
public async Task Session_never_outlives_the_absolute_cap()   // criterion 9
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    Clock.Advance(TimeSpan.FromDays(13));
    var day13 = await SessionApi.RefreshOk(Client, login.RefreshToken);
    Clock.Advance(TimeSpan.FromDays(13));
    var day26 = await SessionApi.RefreshOk(Client, day13.RefreshToken);

    Clock.Advance(TimeSpan.FromDays(4) + TimeSpan.FromMinutes(1));          // day 30: used 4 days ago, still dead
    using var response = await SessionApi.Refresh(Client, day26.RefreshToken);

    await SessionApi.AssertInvalidGrantAsync(response);
}

[Fact]
public async Task Cookie_max_age_tracks_the_time_left_to_the_cap()   // Decision 10
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    Assert.Equal(TimeSpan.FromDays(14), login.Cookie.MaxAge);

    Clock.Advance(TimeSpan.FromDays(13));
    var day13 = await SessionApi.RefreshOk(Client, login.RefreshToken);
    Assert.Equal(TimeSpan.FromDays(14), day13.Cookie.MaxAge);

    Clock.Advance(TimeSpan.FromDays(13));
    var day26 = await SessionApi.RefreshOk(Client, day13.RefreshToken);
    // 4 days to the cap. The session start is stored in whole seconds, so allow one second.
    Assert.InRange(day26.Cookie.MaxAge!.Value, TimeSpan.FromDays(4) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(4));
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run
  `dotnet test -- --filter-class "*SessionPolicyTests"` and `"*SessionLifetimeTests"`.
  Expected: `SessionPolicyTests` does not compile; after Step 3's `SessionPolicy`
  part, `Session_never_outlives_the_absolute_cap` and
  `Cookie_max_age_tracks_the_time_left_to_the_cap` FAIL (the session slides forever).
  If `Unused_session_expires_after_the_sliding_window` fails, OpenIddict is not
  using the injected clock for expiry: stop and escalate.
- [ ] **Step 3: Implement.**

`SessionPolicy` (add):

```csharp
    /// <summary>
    /// Claim holding when the session began (the login), in Unix seconds. It has no destination, so it lives only
    /// in the refresh token and never reaches an access token.
    /// </summary>
    public const string StartClaim = "session_start";

    /// <summary>
    /// Lifetime of the next refresh token: the sliding window, cut short so it never crosses the absolute cap.
    /// <see langword="null"/> once the cap is reached: the session is over.
    /// </summary>
    public static TimeSpan? RemainingLifetime(DateTimeOffset sessionStart, DateTimeOffset now)
    {
        var untilCap = sessionStart + AbsoluteLifetime - now;
        if (untilCap <= TimeSpan.Zero)
        {
            return null;
        }

        return untilCap < SlidingLifetime ? untilCap : SlidingLifetime;
    }
```

`RefreshCookie` (add): `internal static readonly object LifetimeItemKey = new();`

`LoginEndpoint.HandleAsync` — add the `TimeProvider clock` parameter; after `sub`:

```csharp
        identity.SetClaim(SessionPolicy.StartClaim, clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        var principal = new ClaimsPrincipal(identity);
        principal.SetResources(tokens.Value.Audience);
        // `sub` is the whole access token; the session start stays in the refresh token only.
        principal.SetDestinations(static claim => claim.Type == Claims.Subject ? [Destinations.AccessToken] : []);
        principal.SetRefreshTokenLifetime(SessionPolicy.SlidingLifetime);
        http.Items[RefreshCookie.LifetimeItemKey] = SessionPolicy.SlidingLifetime;
```

`RefreshEndpoint.HandleAsync` — add the `TimeProvider clock` parameter; after the user check:

```csharp
        // A refresh token without a readable session start cannot be placed against the cap: reject it.
        if (!long.TryParse(result.Principal!.GetClaim(SessionPolicy.StartClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var startSeconds)
            || SessionPolicy.RemainingLifetime(DateTimeOffset.FromUnixTimeSeconds(startSeconds), clock.GetUtcNow()) is not { } lifetime)
        {
            return InvalidGrant();
        }
```

and before `Results.SignIn`:

```csharp
        principal.SetRefreshTokenLifetime(lifetime);
        http.Items[RefreshCookie.LifetimeItemKey] = lifetime;
```

`SessionResponseHandler` — replace the `RefreshCookie.Append(...)` line with:

```csharp
        var lifetime = http.Items[RefreshCookie.LifetimeItemKey] as TimeSpan?
            ?? throw new InvalidOperationException("The endpoint did not record the refresh token lifetime.");

        RefreshCookie.Append(http.Response, refreshToken, lifetime);
```

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: all PASS, `LoginTests` unedited (no `session_start` in the access token).
- [ ] **Step 5: Commit** — `feat(session): cap a session at 30 days from login`

### Task 5: `POST /auth/logout`

**Files:**
- Create: `src/Auth.Server/Sessions/LogoutEndpoint.cs`
- Modify: `src/Auth.Server/Program.cs`
- Test: `tests/Auth.IntegrationTests/LogoutTests.cs`

**Interfaces:**
- Consumes: `RefreshCookie.TryRead` / `Clear` (Task 1), `IOpenIddictTokenManager`,
  `IOpenIddictAuthorizationManager`.
- Produces: `LogoutEndpoint` with `const string LogoutPath = "/auth/logout"` and
  `static Task<IResult> HandleAsync(HttpContext http, IOpenIddictTokenManager tokens,
  IOpenIddictAuthorizationManager authorizations)`. Any token of the family —
  current or already consumed — ends the whole family.

- [ ] **Step 1: Write the failing tests** (`LogoutTests : SessionTestBase`)

```csharp
[Fact]
public async Task Logout_returns_204_clears_the_cookie_and_ends_the_session()   // criterion 5
{
    var login = await SessionApi.LoginAsync(Client, Factory);

    using var response = await SessionApi.Logout(Client, login.RefreshToken);

    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    var cookie = SessionApi.RefreshCookieOf(response);
    Assert.Equal(string.Empty, cookie.Value.Value);
    Assert.Equal(TimeSpan.Zero, cookie.MaxAge);
    Assert.True(cookie.HttpOnly);
    Assert.True(cookie.Secure);
    Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
    Assert.Equal("/auth", cookie.Path.Value);

    using var refresh = await SessionApi.Refresh(Client, login.RefreshToken);
    await SessionApi.AssertInvalidGrantAsync(refresh);
}

[Fact]
public async Task Logout_with_a_consumed_token_still_ends_the_family()
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    var current = await SessionApi.RefreshOk(Client, login.RefreshToken);

    using var response = await SessionApi.Logout(Client, login.RefreshToken);   // a stale tab logs out
    using var refresh = await SessionApi.Refresh(Client, current.RefreshToken);

    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    await SessionApi.AssertInvalidGrantAsync(refresh);
}

public static TheoryData<string?> InvalidCookieValues => new()
{
    null,                        // no cookie at all
    "",
    "not-a-real-reference-0123456789abcdef",
    "%00",
    "aaa.bbb.ccc",
    new string('x', 4000),
};

[Theory]
[MemberData(nameof(InvalidCookieValues))]
public async Task Logout_without_a_valid_cookie_still_returns_204(string? value)   // criterion 6, Review Focus #2
{
    using var response = await SessionApi.Logout(Client, value);

    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    Assert.Equal(TimeSpan.Zero, SessionApi.RefreshCookieOf(response).MaxAge);
}

[Fact]
public async Task Logout_is_indistinguishable_with_and_without_a_session()   // criterion 6: no enumeration
{
    var login = await SessionApi.LoginAsync(Client, Factory);

    using var real = await SessionApi.Logout(Client, login.RefreshToken);
    using var again = await SessionApi.Logout(Client, login.RefreshToken);   // idempotent
    using var none = await SessionApi.Logout(Client, refreshToken: null);

    foreach (var response in new[] { again, none })
    {
        Assert.Equal(real.StatusCode, response.StatusCode);
        Assert.Equal(LoginApi.HeaderNames(real), LoginApi.HeaderNames(response));
        Assert.Equal(real.Headers.GetValues("Set-Cookie"), response.Headers.GetValues("Set-Cookie"));
    }
}

[Fact]
public async Task Logout_leaves_another_session_alone()   // Decision 7, Review Focus #4
{
    var first = await SessionApi.LoginAsync(Client, Factory);
    var second = await SessionApi.LoginAsync(Client, Factory);

    using var response = await SessionApi.Logout(Client, first.RefreshToken);

    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    await SessionApi.RefreshOk(Client, second.RefreshToken);
}

[Fact]
public async Task Revoked_token_gets_the_uniform_401()   // criterion 7: "revoked"
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    using var logout = await SessionApi.Logout(Client, login.RefreshToken);

    using var revoked = await SessionApi.Refresh(Client, login.RefreshToken);
    using var missing = await SessionApi.Refresh(Client, refreshToken: null);

    await SessionApi.AssertInvalidGrantAsync(revoked);
    Assert.Equal(missing.Content.Headers.ContentType, revoked.Content.Headers.ContentType);
    Assert.Equal(LoginApi.HeaderNames(missing), LoginApi.HeaderNames(revoked));
}

[Fact]
public async Task Trailing_slash_path_logs_out_too()   // Review Focus #1
{
    var login = await SessionApi.LoginAsync(Client, Factory);

    using var response = await SessionApi.Logout(Client, login.RefreshToken, SessionApi.LogoutPath + "/");
    using var refresh = await SessionApi.Refresh(Client, login.RefreshToken);

    Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    await SessionApi.AssertInvalidGrantAsync(refresh);
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test -- --filter-class "*LogoutTests"`.
  Expected: FAIL (`404` on `/auth/logout`).
- [ ] **Step 3: Implement.**

`Sessions/LogoutEndpoint.cs`:

```csharp
/// <summary>
/// <c>POST /auth/logout</c>: ends the session the cookie belongs to and clears the cookie. Always <c>204</c>, with
/// or without a valid cookie, so the answer says nothing about whether a session existed.
/// </summary>
/// <remarks>
/// An access token already issued stays valid until it expires (at most 10 minutes): it is verified offline and
/// cannot be recalled (spec 0002, Decision 14).
/// </remarks>
public static class LogoutEndpoint
{
    public const string LogoutPath = "/auth/logout";

    public static async Task<IResult> HandleAsync(HttpContext http, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(authorizations);

        if (RefreshCookie.TryRead(http.Request, out var reference))
        {
            await RevokeFamilyAsync(reference, tokens, authorizations, http.RequestAborted);
        }

        RefreshCookie.Clear(http.Response);
        http.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return Results.NoContent();
    }

    /// <summary>
    /// Revokes the authorization the token belongs to, and every token under it. A consumed token of the family
    /// counts too: a stale tab logging out must still end the session.
    /// </summary>
    private static async Task RevokeFamilyAsync(
        string reference, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, CancellationToken cancellationToken)
    {
        var token = await tokens.FindByReferenceIdAsync(reference, cancellationToken);
        if (token is null || !await tokens.HasTypeAsync(token, TokenTypeHints.RefreshToken, cancellationToken))
        {
            return;
        }

        var family = await tokens.GetAuthorizationIdAsync(token, cancellationToken);
        if (string.IsNullOrEmpty(family))
        {
            await tokens.TryRevokeAsync(token, cancellationToken);
            return;
        }

        if (await authorizations.FindByIdAsync(family, cancellationToken) is { } authorization)
        {
            await authorizations.TryRevokeAsync(authorization, cancellationToken);
        }

        await tokens.RevokeByAuthorizationIdAsync(family, cancellationToken);
    }
}
```

`Program.cs`: `app.MapPost(LogoutEndpoint.LogoutPath, LogoutEndpoint.HandleAsync);`.
The stored token type string must equal what Task 1's test asserts (`"refresh_token"`).

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`. Expected: all PASS.
- [ ] **Step 5: Commit** — `feat(session): add POST /auth/logout`

### Task 6: Pruning

**Files:**
- Create: `src/Auth.Server/Sessions/TokenPruner.cs`, `src/Auth.Server/Sessions/TokenPruningService.cs`
- Modify: `src/Auth.Server/Program.cs`
- Test: `tests/Auth.IntegrationTests/TokenPruningTests.cs`

**Interfaces:**
- Consumes: `SessionPolicy.SlidingLifetime`, `TimeProvider`, the OpenIddict managers.
- Produces: `TokenPruner` (singleton) with
  `Task<(long Tokens, long Authorizations)> PruneOnceAsync(CancellationToken cancellationToken)` —
  removes token entries, then authorization entries, created before
  `now − SlidingLifetime` that are no longer usable.
- Produces: `TokenPruningService : BackgroundService` with
  `static readonly TimeSpan Interval = TimeSpan.FromHours(1)` — one pass at host
  start, then one per interval; a failed pass is logged and the loop continues.
- **Why 14 days (spec Decision 13):** a consumed refresh token's entry is what
  lets a replay revoke the family. Pruning it earlier would switch reuse
  detection off for that token. An entry older than the sliding window is
  expired anyway.

- [ ] **Step 1: Write the failing tests** (`TokenPruningTests : SessionTestBase`)

```csharp
private async Task<(long Tokens, long Authorizations)> PruneAsync() =>
    await Factory.Services.GetRequiredService<TokenPruner>().PruneOnceAsync(TestContext.Current.CancellationToken);

private async Task<bool> EntryExistsAsync(string reference)
{
    using var scope = Factory.Services.CreateScope();
    var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
    return await tokens.FindByReferenceIdAsync(reference) is not null;
}

[Fact]
public async Task Pruning_removes_an_ended_session_older_than_the_sliding_window()
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    using var logout = await SessionApi.Logout(Client, login.RefreshToken);

    Clock.Advance(TimeSpan.FromDays(15));
    var (tokens, authorizations) = await PruneAsync();

    Assert.True(tokens >= 2, $"Expected the access and refresh token entries to go, pruned {tokens}.");
    Assert.Equal(1, authorizations);
    Assert.False(await EntryExistsAsync(login.RefreshToken));
}

[Fact]
public async Task Pruning_removes_consumed_entries_older_than_the_sliding_window()
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    Clock.Advance(TimeSpan.FromDays(13));
    var day13 = await SessionApi.RefreshOk(Client, login.RefreshToken);

    Clock.Advance(TimeSpan.FromDays(2));          // day 15: the login token is consumed and 15 days old
    await PruneAsync();

    Assert.False(await EntryExistsAsync(login.RefreshToken));
    await SessionApi.RefreshOk(Client, day13.RefreshToken);   // the live session is untouched
}

[Fact]
public async Task Pruning_keeps_a_consumed_entry_that_reuse_detection_still_needs()   // Decision 13
{
    var login = await SessionApi.LoginAsync(Client, Factory);
    var current = await SessionApi.RefreshOk(Client, login.RefreshToken);

    Clock.Advance(TimeSpan.FromDays(13));
    await PruneAsync();

    // Had the consumed entry been pruned, the replay would be a plain unknown token and `current` would live on.
    using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
    using var afterReplay = await SessionApi.Refresh(Client, current.RefreshToken);
    await SessionApi.AssertInvalidGrantAsync(replay);
    await SessionApi.AssertInvalidGrantAsync(afterReplay);
}

[Fact]
public async Task Pruning_leaves_a_live_session_working()
{
    var login = await SessionApi.LoginAsync(Client, Factory);

    Clock.Advance(TimeSpan.FromDays(1));
    await PruneAsync();

    await SessionApi.RefreshOk(Client, login.RefreshToken);
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test -- --filter-class "*TokenPruningTests"`.
  Expected: FAIL (`TokenPruner` does not exist).
- [ ] **Step 3: Implement.**

`Sessions/TokenPruner.cs`:

```csharp
/// <summary>
/// One pruning pass over the OpenIddict store: entries older than the sliding window that are no longer usable
/// (expired, consumed, revoked). Younger entries stay, consumed ones included — reuse detection needs them.
/// </summary>
public sealed partial class TokenPruner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<TokenPruner> logger)
{
    public async Task<(long Tokens, long Authorizations)> PruneOnceAsync(CancellationToken cancellationToken)
    {
        var threshold = clock.GetUtcNow() - SessionPolicy.SlidingLifetime;

        await using var scope = scopes.CreateAsyncScope();
        var tokens = await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>().PruneAsync(threshold, cancellationToken);
        // Tokens first: an authorization is only removable once no token refers to it.
        var authorizations = await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>().PruneAsync(threshold, cancellationToken);

        LogPruned(logger, tokens, authorizations);
        return (tokens, authorizations);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Tokens} token and {Authorizations} authorization entries.")]
    private static partial void LogPruned(ILogger logger, long tokens, long authorizations);
}
```

`Sessions/TokenPruningService.cs`:

```csharp
/// <summary>Runs <see cref="TokenPruner"/> at host start and then every <see cref="Interval"/>.</summary>
public sealed partial class TokenPruningService(TokenPruner pruner, TimeProvider clock, ILogger<TokenPruningService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await pruner.PruneOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Pruning is housekeeping: a failed pass must not take the auth service down. Try again next time.
                LogFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Token pruning failed; it will run again at the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
```

`Program.cs`: `builder.Services.AddSingleton<TokenPruner>();` and
`builder.Services.AddHostedService<TokenPruningService>();`. Hosted services start
inside `app.Run()`, after the startup migration, so the tables exist.

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`. Expected:
  all PASS; the whole suite still starts hosts without a pruning error in the log.
- [ ] **Step 5: Commit** — `feat(session): prune token entries older than the sliding window`

### Task 7: Real-network e2e, docs and acceptance map

**Files:**
- Create: `scripts/e2e-refresh.sh`, `docs/superpowers/plans/0002-acceptance-map.md`
- Modify: `README.md` (quickstart: add `scripts/e2e-refresh.sh`; status line)

**Interfaces:**
- Consumes: the compose stack and `.env` of slice 1 (unchanged), `scripts/verify_jwt.py`.
- Produces: `scripts/e2e-refresh.sh` — same conventions as `scripts/e2e-login.sh`
  (`set -euo pipefail`, `BASE_URL`, `env_get`, `wait_healthy`, `PASS <step>` lines,
  exit non-zero on the first failure, does not bring the stack up or down). It
  **never prints a token**, and keeps tokens off the command line: each cookie is
  written to a header file in a `mktemp -d` directory (removed by an `EXIT` trap)
  and sent with `curl -H @"$file"`. The cookie is carried by hand because it is
  `Secure` and the stack speaks plain HTTP. Steps:
  1. health; login with `-D` to a header file → `200`; the `Set-Cookie: auth_rt=`
     line contains `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/auth` (case-insensitive);
     the body does not contain the cookie value. → `RT0`
  2. refresh with `RT0` → `200`, body keys exactly `access_token`; `verify_jwt.py`
     accepts the token; new cookie `RT1 != RT0`; body does not contain `RT1`.
  3. `docker compose restart auth`, wait healthy; refresh with `RT1` → `200` → `RT2`
     (the store survives a real process restart).
  4. `sleep 16`; refresh with `RT1` (consumed, grace over) → `401`, body exactly
     `{"error":"invalid_grant"}`; refresh with `RT2` → the same `401` (family revoked).
  5. refresh with no cookie and with a junk cookie → the same `401` body.
  6. new login → `RT`; logout with `RT` → `204`, `Set-Cookie` has `auth_rt=;` and
     `Max-Age=0`; refresh with `RT` → `401`; logout with no cookie → `204`.
  7. regression: fresh login → `200` and `verify_jwt.py` accepts the token.
- Produces: `0002-acceptance-map.md`, same layout as `0001-acceptance-map.md`:

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | Refresh → `200`, new access token, rotated cookie | `RefreshTests.Refresh_returns_a_new_access_token_and_a_rotated_cookie`, `RefreshTests.Rotation_stays_in_one_family_and_can_repeat`; e2e step 2 |
| 2 | Consumed token rejected after the grace window | `RefreshReuseTests.Consumed_token_is_rejected_after_the_grace_window`; e2e step 4 |
| 3 | Reuse outside the grace window revokes the family | `RefreshReuseTests.Reuse_after_the_grace_window_revokes_the_whole_family`, `TokenPruningTests.Pruning_keeps_a_consumed_entry_that_reuse_detection_still_needs`; e2e step 4 |
| 4 | Reuse inside the grace window succeeds | `RefreshReuseTests.Consumed_token_is_forgiven_inside_the_grace_window`, `RefreshReuseTests.Concurrent_double_submit_succeeds_twice` |
| 5 | Logout → `204`, cookie cleared, token dead | `LogoutTests.Logout_returns_204_clears_the_cookie_and_ends_the_session`, `LogoutTests.Logout_with_a_consumed_token_still_ends_the_family`; e2e step 6 |
| 6 | Logout without a valid cookie → `204` | `LogoutTests.Logout_without_a_valid_cookie_still_returns_204`, `LogoutTests.Logout_is_indistinguishable_with_and_without_a_session`; e2e step 6 |
| 7 | Every refresh failure → the same `401` | `RefreshTests.Every_refresh_failure_returns_the_same_401`, `LogoutTests.Revoked_token_gets_the_uniform_401`, `RefreshTests.Hostile_cookie_value_returns_401_never_500`; e2e steps 4–5 |
| 8 | Cookie flags; refresh token never in a body | `LoginCookieTests.Login_sets_the_refresh_cookie_with_adr_0004_attributes`, `LoginCookieTests.Login_body_is_unchanged_and_never_carries_the_refresh_token`, `RefreshTests.Refresh_returns_a_new_access_token_and_a_rotated_cookie`; e2e steps 1–2 |
| 9 | Sliding window, absolute cap | `SessionLifetimeTests.Unused_session_expires_after_the_sliding_window`, `SessionLifetimeTests.Each_refresh_extends_the_sliding_window`, `SessionLifetimeTests.Session_never_outlives_the_absolute_cap`, `SessionPolicyTests.Remaining_lifetime_is_the_shorter_of_window_and_cap` (integration only, spec Decision 10) |

  plus a Review Focus table (the five lines above → their tests), a
  "Plan-vs-implementation notes" section (which spike path Task 1 took; any
  OpenIddict name that differed), and an empty "Local verification log" the
  orchestrator fills after verification.

- [ ] **Step 1: Write** the script, the acceptance map and the README change. Keep every
  test name in the map in sync with the code (`grep` each one).
- [ ] **Step 2: Run the e2e on a clean stack.** `cp .env.example .env` (set values) →
  `scripts/dev-keys.sh` → `docker compose -f deploy/docker-compose.yml --env-file .env down -v` →
  `… up -d --build` → `scripts/e2e-login.sh` → `scripts/e2e-refresh.sh` → `… down -v`.
  Expected: both end with `ALL PASS`. Then remove `.env` and `.secrets/`.
- [ ] **Step 3: Run the final local gate.**
  `dotnet format --verify-no-changes && dotnet build -warnaserror && dotnet test`, then
  `Auth__Tokens__Audience=x Auth__Tokens__Issuer=http://x/auth dotnet test`, then
  `git grep -nE "PRIVATE KEY|Password=" -- ':!*.md' ':!.env.example'` (no hits) and
  `git status --porcelain` (no `.env`, no `.secrets/`).
- [ ] **Step 4: Commit** — `test(e2e): refresh and logout over the real network; acceptance map for spec 0002`

---

## After the plan: verify, then merge

1. Dispatch the three local verifiers **in parallel** (Sonnet, fresh context,
   read-only), as defined in [`docs/workflow.md`](../../workflow.md#verification):
   realization vs **spec** (8 layers, using the acceptance map), API/e2e (clean
   stack, `e2e-login.sh` + `e2e-refresh.sh` + independent probes), security
   (cookie flags, no refresh token in a body or a log, uniform failures, diff).
2. Each finding carries a `scope`. At most 2 fix rounds per verifier, then the
   issue goes to the owner.
3. Record the outcome: the verification log in the acceptance map, and an
   `## As built (owner, date)` section in spec 0002.
4. When every verifier passes, merge the feature branch into `main` locally.

## Open questions for owner

1. **Wrong method on `/auth/logout`.** A `GET` gets ASP.NET Core's `405` (the
   route only maps `POST`); the spec is silent. Refresh answers `400` because it
   is an OpenIddict endpoint. Keep the difference, or make logout `400` too?
   Default proposal: keep `405`.
2. **Every non-`invalid_request` error on `/auth/refresh` becomes `401
   invalid_grant`**, including an OpenIddict `server_error`. That keeps the
   failure uniform, at the cost of hiding a server fault behind a `401`. An
   unhandled exception is still a `500`. Acceptable?
3. **Pruning also runs once at host start**, not only hourly, so a service that
   restarts often still prunes. Acceptable?

## As built

Implemented and verified locally (three verifiers, PASS in round 1). What the
implementation settled, including where it departs from this plan, is recorded in
spec 0002 → "As built" and in the [acceptance map](0002-acceptance-map.md). In
particular: Task 1 needed an extra handler (`AccessTokenClaimFilter`) to keep the
access token's claim set; Task 2's response handler writes the refresh response
itself; Task 5 uses the token type urn, not the `"refresh_token"` hint; Task 6's
service ends its loop normally on cancellation. Open questions 1–3 above are carried
to the owner as escalations E1–E3 in the acceptance map.
