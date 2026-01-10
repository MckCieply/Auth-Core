# Login and Token Issuance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0001
- **Date:** 2026-01-07
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0001-login-and-token-issuance.md`](../specs/0001-login-and-token-issuance.md)
  — the plan argues from the spec; where they disagree, **the spec wins** and the
  disagreement is a finding (see [`docs/workflow.md`](../../workflow.md)).

**Goal:** Bootstrap the .NET repo skeleton and ship `POST /auth/login` (email +
password → RS256 JWT) plus `GET /auth/.well-known/jwks.json`, with signing keys
loaded from a mounted secret so tokens survive a restart.

**Architecture:** One ASP.NET Core 10 minimal-API host (`Auth.Server`) runs the
OpenIddict 7 server, with its **token endpoint mapped to `/auth/login`** in
pass-through mode and only the password flow enabled. Two small OpenIddict event
handlers adapt the spec's contract to OpenIddict: one turns the JSON body into
an OpenIddict password request (or rejects it with `400`), and one cuts the token
response down to `{status, access_token}`. The pass-through endpoint checks the
credentials with ASP.NET Core Identity and either signs in (OpenIddict issues the
JWT) or returns the uniform `401`. Persistence (Identity + OpenIddict entities,
PostgreSQL) lives in `Auth.Infrastructure`. Mapping refresh (spec 0002) onto the
same token endpoint later is a one-line `SetTokenEndpointUris` change. That is
why the token engine is not hand-rolled.

**Tech Stack:** .NET 10 LTS, C# 14, ASP.NET Core minimal APIs, ASP.NET Core
Identity (`Microsoft.AspNetCore.Identity.EntityFrameworkCore`), OpenIddict 7.x
(`OpenIddict.AspNetCore`, `OpenIddict.EntityFrameworkCore`), EF Core 10 +
`Npgsql.EntityFrameworkCore.PostgreSQL` 10.x, PostgreSQL 16, xUnit v3,
`Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`,
`Microsoft.IdentityModel.JsonWebTokens` (tests), Docker (chiseled `aspnet:10.0`),
Python 3.12 + `PyJWT[crypto]` 2.x (e2e verifier only).

## Global Constraints

- Target `net10.0`; SDK pinned in `global.json` (`10.0.x`, `rollForward: latestFeature`).
- `Nullable` enabled, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`,
  `ImplicitUsings` enabled — set once in `Directory.Build.props`.
- Package versions pinned **only** in `Directory.Packages.props` (central package
  management); every package must be MIT / Apache 2.0 / BSD. Not allowed:
  Duende, MediatR, AutoMapper, FluentAssertions, Serilog, Newtonsoft.Json.
- Issuer (dev default) **`http://localhost:8080/auth`**; audience (dev default)
  **`auth-core-dev`**. Both come from config (`Auth:Tokens:Issuer`,
  `Auth:Tokens:Audience`) because they are per-instance in production.
- Access-token lifetime **10 minutes**: a constant in code, not a config value.
- Signing algorithm **RS256**; access-token encryption **disabled**.
- Success body exactly `{"status":"authenticated","access_token":"<jwt>"}`.
- Invalid credentials: `401` with body exactly `{"error":"invalid_credentials"}`,
  the same for an unknown email and for a wrong password.
- Malformed request: `400`, never `500`.
- The access token carries no `org_id`, `roles` or `permissions` claim. They are
  absent, not empty.
- No private key, password or connection-string secret in the repo, logs or any
  response. Key material and dev secrets live in the git-ignored `.secrets/` and `.env`.
- **Local-only workflow:** no CI, no GitHub PR. Work on a feature branch and merge
  to `main` locally after the verifiers pass ([`docs/workflow.md`](../../workflow.md)).
  This replaces the "CI" bullet in `design.md` → "Repo bootstrap".
- Conventional Commits, English docs and identifiers.

## Review Focus

The spec does not name these failure modes, but a user or consumer would hit
them. Each line has a pinning test in the task named in brackets.

1. **Email casing:** `USER@example.com` logs in to the account seeded as
   `user@example.com`, because Identity compares normalized emails. [Task 7]
2. **Wrong JSON types or shape:** `"email": 123`, `"password": null`, a top-level
   array or an empty object each return `400`, never `500`. [Task 6]
3. **Other formats to the same URL:** a classic OIDC form post
   (`grant_type=password&username=…`) or a `text/plain` body to `/auth/login`
   returns `400`. The only accepted format is JSON. [Task 6]
4. **Uniform failure, beyond the body:** an unknown email and a wrong password
   also return the same status, `Content-Type` and set of headers. Header
   differences would leak too. [Task 7]
5. **Missing or broken key material at startup:** the host fails fast with an
   error that names the config key. The error never contains key bytes, and the
   host never falls back to an ephemeral or development key, which would silently
   break criterion 7. [Task 3]

## File Structure

```
global.json                                  SDK pin
Directory.Build.props                        shared compiler/analyzer settings
Directory.Packages.props                     central package versions
Auth-Core.slnx                               solution
.env.example                                 documented dev env vars (no real values)
.gitignore                                   + .secrets/
scripts/dev-keys.sh                          generate dev signing/encryption PEMs into .secrets/
scripts/e2e-login.sh                         curl sequence + restart + Python verify
scripts/verify_jwt.py                        PyJWT/PyJWKClient cross-language verifier
deploy/docker-compose.yml                    postgres:16 + auth on :8080, .secrets mounted read-only
src/Auth.Server/
  Auth.Server.csproj
  Program.cs                                 composition root; `public partial class Program`
  Dockerfile                                 chiseled, non-root
  appsettings.json / appsettings.Development.json
  Keys/KeyMaterialOptions.cs                 Auth:Keys section
  Keys/KeyMaterialLoader.cs                  PEM pair → X509Certificate2, fail-fast
  Tokens/TokenOptions.cs                     Auth:Tokens section + AccessTokenLifetime constant
  Tokens/OpenIddictSetup.cs                  AddAuthOpenIddict(...)
  Login/JsonLoginRequestHandler.cs           ExtractTokenRequestContext handler
  Login/LoginResponseShaper.cs               ApplyTokenResponseContext handler
  Login/LoginEndpoint.cs                     pass-through endpoint: Identity check → SignIn | 401
  Seeding/DevUserSeeder.cs                   Development-only seed user
src/Auth.Infrastructure/
  Auth.Infrastructure.csproj
  Identity/ApplicationUser.cs                IdentityUser<Guid>
  Persistence/AuthDbContext.cs               IdentityDbContext + UseOpenIddict()
  Persistence/Migrations/…                   EF migrations
  DependencyInjection.cs                     AddAuthPersistence(...)
tests/Auth.IntegrationTests/
  Auth.IntegrationTests.csproj
  Infrastructure/PostgresFixture.cs          Testcontainers Postgres, one per test collection
  Infrastructure/KeyMaterialFixture.cs       temp-dir PEMs generated in-process
  Infrastructure/AuthAppFactory.cs           WebApplicationFactory<Program>
  Infrastructure/Jwks.cs                     fetch JWKS + validate a JWT with JsonWebTokenHandler
  SkeletonTests.cs  PersistenceTests.cs  KeyMaterialTests.cs  JwksTests.cs
  DevSeedTests.cs  LoginRequestTests.cs  LoginTests.cs  TokenVerificationTests.cs
```

`Auth.Core` and `Auth.Cli` from the `design.md` layout are **not** created yet.
Nothing in this slice belongs in them, so YAGNI applies. They arrive with the
first code that needs them (tenancy/RBAC and the admin CLI, Week 3).

---

### Task 1: Repo skeleton

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`,
  `Auth-Core.slnx`, `src/Auth.Server/Auth.Server.csproj`, `src/Auth.Server/Program.cs`,
  `src/Auth.Server/appsettings.json`, `src/Auth.Infrastructure/Auth.Infrastructure.csproj`,
  `tests/Auth.IntegrationTests/Auth.IntegrationTests.csproj`,
  `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`
- Modify: `.gitignore` (add `.secrets/`)
- Test: `tests/Auth.IntegrationTests/SkeletonTests.cs`

**Interfaces:**
- Produces: `public partial class Program` (entry point for `WebApplicationFactory`);
  `AuthAppFactory : WebApplicationFactory<Program>`, which later tasks extend with
  config overrides through `ConfigureWebHost` → `UseSetting(key, value)`.
- Produces: `GET /auth/health` → `200` (built-in health checks, `MapHealthChecks`).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Health_endpoint_returns_200()
{
    using var client = new AuthAppFactory().CreateClient();
    var response = await client.GetAsync("/auth/health");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}
```

- [ ] **Step 2: Run it and confirm it fails.** Run `dotnet test`. Expected: the
  build fails because `Program` and the projects do not exist yet.
- [ ] **Step 3: Create the skeleton.** Global settings and package pins as in
  [Global Constraints](#global-constraints). Pin the latest stable versions at
  bootstrap, and copy the resolved versions into the commit body. `Program.cs`
  needs only `AddHealthChecks()` and `MapHealthChecks("/auth/health")`.
- [ ] **Step 4: Run it and confirm it passes.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: `SkeletonTests` PASS, 0 warnings.
- [ ] **Step 5: Commit** — `chore: bootstrap .NET 10 solution skeleton`

### Task 2: Persistence (Identity + OpenIddict on PostgreSQL)

**Files:**
- Create: `src/Auth.Infrastructure/Identity/ApplicationUser.cs`,
  `src/Auth.Infrastructure/Persistence/AuthDbContext.cs`,
  `src/Auth.Infrastructure/DependencyInjection.cs`, `…/Persistence/Migrations/*`,
  `tests/Auth.IntegrationTests/Infrastructure/PostgresFixture.cs`
- Modify: `src/Auth.Server/Program.cs`, `tests/…/AuthAppFactory.cs`
- Test: `tests/Auth.IntegrationTests/PersistenceTests.cs`

**Interfaces:**
- Produces: `ApplicationUser : IdentityUser<Guid>`;
  `AuthDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>`, with
  `options.UseOpenIddict()` in its registration;
  `IServiceCollection AddAuthPersistence(this IServiceCollection, IConfiguration)`,
  which reads `ConnectionStrings:Auth` and registers `AddIdentityCore<ApplicationUser>()`
  with EF stores.
- Produces: migrations are applied at host startup when
  `Auth:Database:MigrateOnStartup` is `true` (default `true` in
  `appsettings.Development.json`, `false` in `appsettings.json`).
- Produces: `PostgresFixture` (xUnit collection fixture, `postgres:16-alpine`)
  exposing `string ConnectionString`; `AuthAppFactory(PostgresFixture)` sets
  `ConnectionStrings:Auth` and `MigrateOnStartup=true`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Created_user_is_found_by_email_after_migrations()
{
    await using var factory = new AuthAppFactory(_postgres);
    using var scope = factory.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var result = await users.CreateAsync(new ApplicationUser { UserName = "p@example.com", Email = "p@example.com" }, "Correct-Horse-1");
    Assert.True(result.Succeeded);
    Assert.NotNull(await users.FindByEmailAsync("p@example.com"));
}
```

- [ ] **Step 2: Run it and confirm it fails.** Run `dotnet test --filter PersistenceTests`. Expected: FAIL (types missing).
- [ ] **Step 3: Implement** the context, user type and DI extension. Generate the initial
  migration with `dotnet ef migrations add InitialIdentityAndOpenIddict -p src/Auth.Infrastructure -s src/Auth.Server`.
- [ ] **Step 4: Run it and confirm it passes.** Run `dotnet test`. Expected: all PASS.
- [ ] **Step 5: Commit** — `feat(persistence): add Identity and OpenIddict EF model on PostgreSQL`

### Task 3: Key material from a mounted secret

**Files:**
- Create: `src/Auth.Server/Keys/KeyMaterialOptions.cs`, `src/Auth.Server/Keys/KeyMaterialLoader.cs`,
  `scripts/dev-keys.sh`, `tests/Auth.IntegrationTests/Infrastructure/KeyMaterialFixture.cs`
- Test: `tests/Auth.IntegrationTests/KeyMaterialTests.cs`

**Interfaces:**
- Produces: `KeyMaterialOptions { string SigningCertificatePath; string SigningKeyPath;
  string EncryptionCertificatePath; string EncryptionKeyPath; }` bound from `Auth:Keys`.
- Produces: `static X509Certificate2 KeyMaterialLoader.Load(string? certPath, string? keyPath, string configKey)`.
  It uses `X509Certificate2.CreateFromPemFile`. It throws `InvalidOperationException`
  when a path is blank, a file is missing, the PEM is unreadable or the private key
  is absent. The message names `configKey` (for example `Auth:Keys:SigningKeyPath`)
  and never includes file contents.
- Produces: `KeyMaterialFixture`, which writes a fresh RSA-2048 self-signed
  cert + PKCS#8 key pair for signing and for encryption into a temp directory
  (`CertificateRequest`, `ExportCertificatePem`, `ExportPkcs8PrivateKeyPem`) and
  exposes the four paths. `AuthAppFactory` points `Auth:Keys:*` at them.
- **Decision (resolves spec "To verify" #1): PEM** (certificate + PKCS#8 key
  files). OpenIddict prefers X.509 certificates, and PEM mounts cleanly as a Docker
  or Kubernetes secret. OpenIddict also needs an **encryption** credential even
  though access-token encryption is disabled (refresh tokens in spec 0002 use it),
  so that credential is mounted the same way.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Loads_certificate_with_private_key_from_pem_pair()
{ var cert = KeyMaterialLoader.Load(_keys.SigningCertPath, _keys.SigningKeyPath, "Auth:Keys:SigningKeyPath");
  Assert.True(cert.HasPrivateKey); Assert.NotNull(cert.GetRSAPrivateKey()); }

[Fact] public void Missing_key_file_fails_fast_naming_the_config_key()
{ var ex = Assert.Throws<InvalidOperationException>(() =>
      KeyMaterialLoader.Load(_keys.SigningCertPath, "/nonexistent/signing.key", "Auth:Keys:SigningKeyPath"));
  Assert.Contains("Auth:Keys:SigningKeyPath", ex.Message);
  Assert.DoesNotContain("PRIVATE KEY", ex.Message); }

[Fact] public void Certificate_without_key_is_rejected()
{ Assert.Throws<InvalidOperationException>(() =>
      KeyMaterialLoader.Load(_keys.SigningCertPath, _keys.SigningCertPath, "Auth:Keys:SigningKeyPath")); }

[Fact] public void Host_refuses_to_start_without_key_configuration()   // Review Focus #5
{ var factory = new AuthAppFactory(_postgres).WithSetting("Auth:Keys:SigningKeyPath", "");
  var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
  Assert.Contains("Auth:Keys:SigningKeyPath", ex.ToString()); }
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --filter KeyMaterialTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the loader and options. Write `scripts/dev-keys.sh`:
  it uses `openssl req -x509 -newkey rsa:2048 -nodes` to write `.secrets/signing.{crt,key}`
  and `.secrets/encryption.{crt,key}`. It refuses to overwrite existing files, and
  it sets mode `0644` so the non-root container user can read the bind mount. That
  mode is for dev only; production uses orchestrator secrets with real ownership.
  Add a comment in the script that says so.
- [ ] **Step 4: Run them and confirm they pass.** Run `dotnet test`. Expected: all PASS. Then run
  `git status --porcelain`. Expected: no `.secrets/` entries (they are ignored).
- [ ] **Step 5: Commit** — `feat(keys): load signing and encryption keys from mounted PEM files`

### Task 4: OpenIddict server and JWKS endpoint

**Files:**
- Create: `src/Auth.Server/Tokens/TokenOptions.cs`, `src/Auth.Server/Tokens/OpenIddictSetup.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/Jwks.cs`
- Modify: `src/Auth.Server/Program.cs`
- Test: `tests/Auth.IntegrationTests/JwksTests.cs`

**Interfaces:**
- Consumes: `KeyMaterialLoader.Load` (Task 3), `AuthDbContext` (Task 2).
- Produces: `TokenOptions { string Issuer = "http://localhost:8080/auth"; string Audience = "auth-core-dev"; }`
  bound from `Auth:Tokens`; `public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(10);`.
- Produces: `IServiceCollection AddAuthOpenIddict(this IServiceCollection, IConfiguration)`,
  which configures:
  - core: `UseEntityFrameworkCore().UseDbContext<AuthDbContext>()`;
  - server: `SetIssuer`, `SetTokenEndpointUris("auth/login")`,
    `SetJsonWebKeySetEndpointUris("auth/.well-known/jwks.json")`, `AllowPasswordFlow()`
    (**no other flows**), `AcceptAnonymousClients()`, `SetAccessTokenLifetime(AccessTokenLifetime)`,
    `DisableAccessTokenEncryption()`, `RegisterAudiences(Audience)`,
    `AddSigningCertificate(...)` / `AddEncryptionCertificate(...)` from Task 3,
    `UseAspNetCore().EnableTokenEndpointPassthrough()`. HTTPS is enforced by the
    reverse proxy (ADR 0002), so `DisableTransportSecurityRequirement()` is set
    for the HTTP-only container port.
- Produces: `Jwks.FetchAsync(HttpClient) : Task<JsonWebKeySet>` and
  `Jwks.ValidateAsync(string jwt, JsonWebKeySet keys, TokenOptions expected) : Task<TokenValidationResult>`
  (`JsonWebTokenHandler`, `ValidAlgorithms = ["RS256"]`, issuer and audience
  validated, no other key source).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Jwks_exposes_the_rsa_signing_key_with_a_kid()
{ var jwks = await Jwks.FetchAsync(_client);
  var key = Assert.Single(jwks.Keys, k => k.Use == "sig");
  Assert.Equal("RSA", key.Kty); Assert.False(string.IsNullOrEmpty(key.Kid)); }

[Fact] public async Task Jwks_never_contains_private_key_members()   // criterion 8
{ var json = await _client.GetStringAsync("/auth/.well-known/jwks.json");
  foreach (var key in JsonDocument.Parse(json).RootElement.GetProperty("keys").EnumerateArray())
      foreach (var member in new[] { "d", "p", "q", "dp", "dq", "qi" })
          Assert.False(key.TryGetProperty(member, out _), $"JWKS leaks private member '{member}'"); }
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --filter JwksTests`. Expected: FAIL (`404`).
- [ ] **Step 3: Implement** `AddAuthOpenIddict` with the settings listed above.
  Check every method name against the installed OpenIddict 7 package. This
  resolves spec "To verify" #2. Record any renamed API in the commit body.
- [ ] **Step 4: Run them and confirm they pass.** Run `dotnet test`. Expected: all PASS.
- [ ] **Step 5: Commit** — `feat(tokens): configure OpenIddict server and publish JWKS`

### Task 5: Development seed user

**Files:**
- Create: `src/Auth.Server/Seeding/DevUserSeeder.cs`, `.env.example`
- Modify: `src/Auth.Server/Program.cs`
- Test: `tests/Auth.IntegrationTests/DevSeedTests.cs`

**Interfaces:**
- Consumes: `UserManager<ApplicationUser>` (Task 2).
- Produces: `static Task DevUserSeeder.SeedAsync(IServiceProvider services, CancellationToken ct)`.
  It runs at host startup only when the environment is `Development` **and** both
  `Auth:DevSeed:Email` and `Auth:DevSeed:Password` are set. It creates the user
  with `EmailConfirmed = true` and is idempotent: if the user exists it does
  nothing, and it never resets the password. `AuthAppFactory` exposes the seeded
  credentials as `SeedEmail`/`SeedPassword` and the id as `SeedUserIdAsync()`.
- **Decision (resolves spec "To verify" #3): seed at host startup**, Development only.
  The admin CLI's create-user path arrives in Week 3 and will replace this as the
  real way to create users.
- `.env.example` documents `AUTH_DEV_SEED_EMAIL` / `AUTH_DEV_SEED_PASSWORD` /
  `POSTGRES_PASSWORD` with placeholder values only.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Development_host_seeds_the_configured_user_once()
{ await using var a = new AuthAppFactory(_postgres); _ = a.CreateClient();
  await using var b = new AuthAppFactory(_postgres); _ = b.CreateClient();   // second start = restart
  using var scope = b.Services.CreateScope();
  var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
  Assert.Equal(1, await db.Users.CountAsync(u => u.NormalizedEmail == b.SeedEmail.ToUpperInvariant())); }

[Fact] public async Task Production_host_never_seeds()
{ await using var f = new AuthAppFactory(_postgres).WithEnvironment("Production")
      .WithSetting("Auth:DevSeed:Email", "prod-seed@example.com");
  _ = f.CreateClient();
  using var scope = f.Services.CreateScope();
  Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
      .FindByEmailAsync("prod-seed@example.com")); }
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --filter DevSeedTests`. Expected: FAIL.
- [ ] **Step 3: Implement** `DevUserSeeder` and call it from `Program.cs` after migrations.
- [ ] **Step 4: Run them and confirm they pass.** Run `dotnet test`. Expected: all PASS.
- [ ] **Step 5: Commit** — `feat(seed): seed a development user at startup`

### Task 6: JSON login request → OpenIddict password request

**Files:**
- Create: `src/Auth.Server/Login/JsonLoginRequestHandler.cs`
- Modify: `src/Auth.Server/Tokens/OpenIddictSetup.cs` (register the handler)
- Test: `tests/Auth.IntegrationTests/LoginRequestTests.cs`

**Interfaces:**
- Consumes: the token endpoint at `auth/login` (Task 4).
- Produces: `JsonLoginRequestHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ExtractTokenRequestContext>`,
  registered with `AddEventHandler` and ordered **before** OpenIddict's ASP.NET Core
  form extraction handler. The handler:
  - rejects any `Content-Type` other than `application/json` (with or without a
    charset), including the form-encoded OIDC request, with `Errors.InvalidRequest` → `400`;
  - reads at most **8 KiB**; a larger, unparsable or non-object body → `400`;
  - requires `email` and `password` to be JSON strings that are not empty or
    whitespace → otherwise `400`;
  - otherwise sets `context.Request = new OpenIddictRequest { GrantType = GrantTypes.Password,
    Username = email, Password = password }`.
- **Spike gate (design.md risk "custom login endpoint").** Time-box: one evening.
  If the installed OpenIddict 7 overwrites or re-validates a pre-set request, use
  the **fallback**: a small middleware placed before `UseRouting` that does the
  same validation and rewrites the body to the form-encoded password request.
  The tests below stay identical either way. Record which path was taken in the
  commit body. If neither works, stop and escalate to the owner. Do not fall
  back to a hand-rolled JWT.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("""{"password":"x"}""")]                       // missing email
[InlineData("""{"email":"a@example.com","password":""}""")]   // blank password
[InlineData("""{"email":"a@example.com","password":"   "}""")]
[InlineData("""{"email":123,"password":"x"}""")]           // Review Focus #2
[InlineData("""{"email":"a@example.com","password":null}""")]
[InlineData("""["a@example.com","x"]""")]
[InlineData("""{}""")]
[InlineData("""not json""")]
public async Task Malformed_json_login_returns_400(string body)
{ var response = await _client.PostAsync("/auth/login", new StringContent(body, Encoding.UTF8, "application/json"));
  Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); }

[Fact] public async Task Form_encoded_oidc_request_returns_400()   // Review Focus #3
{ var form = new FormUrlEncodedContent(new Dictionary<string, string> {
      ["grant_type"] = "password", ["username"] = _factory.SeedEmail, ["password"] = _factory.SeedPassword });
  Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/auth/login", form)).StatusCode); }

[Fact] public async Task Plain_text_body_returns_400()
{ var r = await _client.PostAsync("/auth/login", new StringContent("hello", Encoding.UTF8, "text/plain"));
  Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); }

[Fact] public async Task Oversized_body_returns_400()
{ var big = $$"""{"email":"a@example.com","password":"{{new string('x', 9000)}}"}""";
  var r = await _client.PostAsync("/auth/login", new StringContent(big, Encoding.UTF8, "application/json"));
  Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); }
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --filter LoginRequestTests`. Expected: FAIL
  (OpenIddict's own form extraction rejects JSON differently, or the request reaches the endpoint).
- [ ] **Step 3: Implement** the handler (or the fallback middleware, per the spike gate).
- [ ] **Step 4: Run them and confirm they pass.** Run `dotnet test`. Expected: all PASS, and no `500` in the log.
- [ ] **Step 5: Commit** — `feat(login): accept JSON login body and reject malformed requests`

### Task 7: Credential check, token issuance and response shape

**Files:**
- Create: `src/Auth.Server/Login/LoginEndpoint.cs`, `src/Auth.Server/Login/LoginResponseShaper.cs`
- Modify: `src/Auth.Server/Program.cs` (`MapPost("/auth/login", LoginEndpoint.HandleAsync)`),
  `src/Auth.Server/Tokens/OpenIddictSetup.cs` (register the shaper)
- Test: `tests/Auth.IntegrationTests/LoginTests.cs`

**Interfaces:**
- Consumes: `HttpContext.GetOpenIddictServerRequest()` (populated in Task 6),
  `UserManager<ApplicationUser>`, `IOptions<TokenOptions>`.
- Produces: `static Task<IResult> LoginEndpoint.HandleAsync(HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens)`:
  - looks the user up with `FindByEmailAsync` and checks `CheckPasswordAsync`.
    Lockout is **not** involved (spec 0003). On an unknown user **or** a wrong
    password it returns the same result:
    `Results.Json(new { error = "invalid_credentials" }, statusCode: 401)`.
  - on success it builds a `ClaimsIdentity` (authentication type
    `TokenValidationParameters.DefaultAuthenticationType`) with **only**
    `sub = user.Id`. It calls `SetResources(tokens.Audience)` so `aud` is the
    registered audience, `SetDestinations` → access token for `sub`, and returns
    `Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)`.
- Produces: `LoginResponseShaper : IOpenIddictServerHandler<ApplyTokenResponseContext>`.
  On a response with no error, it replaces the parameters with exactly
  `status = "authenticated"` and `access_token`, removing `token_type` and
  `expires_in`. Error responses pass through unchanged. It is ordered before
  OpenIddict's JSON response writer.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Valid_credentials_return_200_with_exact_contract_body()        // criterion 1
{ var body = await LoginOk(_factory.SeedEmail, _factory.SeedPassword);
  Assert.Equal("authenticated", body.GetProperty("status").GetString());
  Assert.Equal(["access_token", "status"], body.EnumerateObject().Select(p => p.Name).Order()); }

[Fact] public async Task Token_is_unencrypted_RS256_jws_with_kid()                       // criterion 2
{ var jwt = new JsonWebToken(await LoginToken());
  Assert.False(jwt.IsEncrypted); Assert.Equal("RS256", jwt.Alg); Assert.False(string.IsNullOrEmpty(jwt.Kid)); }

[Fact] public async Task Token_carries_contract_claims_and_nothing_from_week_3()        // criterion 3
{ var jwt = new JsonWebToken(await LoginToken());
  Assert.Equal("http://localhost:8080/auth", jwt.Issuer);
  Assert.Equal(["auth-core-dev"], jwt.Audiences);
  Assert.Equal((await _factory.SeedUserIdAsync()).ToString(), jwt.Subject);
  Assert.True(jwt.ValidTo > DateTime.UtcNow);
  Assert.True(jwt.ValidTo - jwt.IssuedAt <= TimeSpan.FromMinutes(10));
  foreach (var absent in new[] { "org_id", "roles", "permissions" })
      Assert.False(jwt.TryGetPayloadValue<object>(absent, out _), $"'{absent}' must be absent"); }

[Fact] public async Task Unknown_email_and_wrong_password_are_indistinguishable()        // criterion 5 + Review Focus #4
{ var wrongPw = await Login(_factory.SeedEmail, "definitely-wrong");
  var unknown = await Login("nobody@example.com", "definitely-wrong");
  foreach (var r in new[] { wrongPw, unknown }) Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
  Assert.Equal("""{"error":"invalid_credentials"}""", await wrongPw.Content.ReadAsStringAsync());
  Assert.Equal(await wrongPw.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
  Assert.Equal(wrongPw.Content.Headers.ContentType, unknown.Content.Headers.ContentType);
  Assert.Equal(HeaderNames(wrongPw), HeaderNames(unknown)); }   // names only; Date differs

[Fact] public async Task Email_match_is_case_insensitive()                                // Review Focus #1
{ await LoginOk(_factory.SeedEmail.ToUpperInvariant(), _factory.SeedPassword); }
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet test --filter LoginTests`. Expected: FAIL.
- [ ] **Step 3: Implement** the endpoint and the shaper.
- [ ] **Step 4: Run them and confirm they pass.** Run `dotnet test`. Expected: all PASS.
- [ ] **Step 5: Commit** — `feat(login): issue RS256 access token on valid credentials`

### Task 8: Offline verification and persistence across restart

**Files:**
- Test: `tests/Auth.IntegrationTests/TokenVerificationTests.cs`
- Modify: only if a test fails (a production fix belongs in the task that owns that code).

**Interfaces:**
- Consumes: `Jwks.FetchAsync` / `Jwks.ValidateAsync` (Task 4), `LoginToken()` helper (Task 7),
  `KeyMaterialFixture` (Task 3).

- [ ] **Step 1: Write the tests**

```csharp
[Fact] public async Task Token_verifies_against_jwks_kid_without_shared_secret()        // criterion 4
{ var token = await LoginToken(_client);
  var jwks = await Jwks.FetchAsync(_client);
  Assert.Contains(jwks.Keys, k => k.Kid == new JsonWebToken(token).Kid);
  Assert.True((await Jwks.ValidateAsync(token, jwks, _tokenOptions)).IsValid); }

[Fact] public async Task Tampered_token_fails_verification()
{ var token = await LoginToken(_client);
  var tampered = token[..^4] + (token[^4..] == "AAAA" ? "BBBB" : "AAAA");
  Assert.False((await Jwks.ValidateAsync(tampered, await Jwks.FetchAsync(_client), _tokenOptions)).IsValid); }

[Fact] public async Task Token_issued_before_restart_verifies_against_jwks_after_restart()   // criterion 7
{ string token;
  await using (var before = new AuthAppFactory(_postgres, _keys)) token = await LoginToken(before.CreateClient());
  await using var after = new AuthAppFactory(_postgres, _keys);   // new process-equivalent host, same mounted keys
  Assert.True((await Jwks.ValidateAsync(token, await Jwks.FetchAsync(after.CreateClient()), _tokenOptions)).IsValid); }
```

- [ ] **Step 2: Run them.** Run `dotnet test --filter TokenVerificationTests`. Expected: PASS. If a test
  fails, fix the code in the task that owns it, and rerun the full suite.
- [ ] **Step 3: Commit** — `test(tokens): prove offline JWKS verification and key persistence across restart`

### Task 9: Container, compose and real-network e2e

**Files:**
- Create: `src/Auth.Server/Dockerfile`, `deploy/docker-compose.yml`, `scripts/e2e-login.sh`,
  `scripts/verify_jwt.py`

**Interfaces:**
- Consumes: everything above; `.secrets/` from `scripts/dev-keys.sh`; `.env` (copied from `.env.example`).
- Produces: `docker compose -f deploy/docker-compose.yml up -d --build`, which serves
  the API on `http://localhost:8080`. `auth` runs as non-root
  (`aspnet:10.0-noble-chiseled`), with `ASPNETCORE_ENVIRONMENT=Development` (for
  the seed and migrations), `.secrets/` mounted read-only at `/run/secrets/auth`
  and `Auth__Keys__*` pointing there. `postgres:16-alpine` has a named volume.
- Produces: `python3 scripts/verify_jwt.py <jwt> <jwks_url> <issuer> <audience>`. It uses
  `PyJWKClient(jwks_url).get_signing_key_from_jwt(jwt)` and
  `jwt.decode(..., algorithms=["RS256"], audience=..., issuer=...)`, exits 0 when
  the token is valid, and never prints the token.
- Produces: `scripts/e2e-login.sh`. It is the spec's [Goal](../specs/0001-login-and-token-issuance.md#goal)
  `curl` sequence over the real network, and it exits non-zero on the first failure:
  1. it waits until `/auth/health` returns 200, logs in, and asserts `200` + `status=authenticated`;
  2. it fetches the JWKS, asserts that no key has a `d/p/q/dp/dq/qi` member, and runs `verify_jwt.py`;
  3. it checks that a wrong password and an unknown email produce identical `401` bodies, and that a form post returns `400`;
  4. it runs `docker compose restart auth`, waits for health, and runs `verify_jwt.py` on the **pre-restart** token against the post-restart JWKS (criterion 7);
  5. it runs a regression pass, which is a fresh login after the restart.

- [ ] **Step 1: Write** the Dockerfile, the compose file and both scripts.
- [ ] **Step 2: Bring the stack up from a clean state.** Run
  `docker compose -f deploy/docker-compose.yml down -v && scripts/dev-keys.sh && docker compose -f deploy/docker-compose.yml up -d --build`.
  Expected: `auth` is healthy, its user is non-root (`docker compose exec auth id` fails
  because the image has no shell, which is expected; check `docker inspect` → `Config.User`).
- [ ] **Step 3: Run the e2e.** Run `scripts/e2e-login.sh`. Expected: exit 0, every step logs `PASS`.
- [ ] **Step 4: Run a secrets scan.** Run `git diff main --stat` and `git grep -nE "PRIVATE KEY|Password=" -- ':!*.md' ':!.env.example'`.
  Expected: no hits.
- [ ] **Step 5: Commit** — `build: add container image, compose stack and e2e login script`

### Task 10: Docs and acceptance map

**Files:**
- Modify: `README.md` (status: "Week 1 slice in progress", plus a quickstart:
  `dev-keys.sh` → `.env` → `compose up` → `e2e-login.sh`)
- Create: `docs/superpowers/plans/0001-acceptance-map.md`, a table mapping each
  spec acceptance criterion to the test(s) that guard it, for verifier layer 2.

| Criterion | Guarding test(s) |
| --------- | ---------------- |
| 1 | `LoginTests.Valid_credentials_return_200_with_exact_contract_body`, e2e step 1 |
| 2 | `LoginTests.Token_is_unencrypted_RS256_jws_with_kid` |
| 3 | `LoginTests.Token_carries_contract_claims_and_nothing_from_week_3` |
| 4 | `TokenVerificationTests.Token_verifies_against_jwks_kid_without_shared_secret`, e2e step 2 (PyJWT) |
| 5 | `LoginTests.Unknown_email_and_wrong_password_are_indistinguishable`, e2e step 3 |
| 6 | `LoginRequestTests.Malformed_json_login_returns_400` (+ form, text, oversized) |
| 7 | `TokenVerificationTests.Token_issued_before_restart_verifies_against_jwks_after_restart`, e2e step 4 |
| 8 | `JwksTests.Jwks_never_contains_private_key_members`, e2e step 2 |

- [ ] **Step 1: Write** both files. Keep each test name in the map in sync with the code (use `grep` to check).
- [ ] **Step 2: Run the final local gate.** Run `dotnet format --verify-no-changes && dotnet build -warnaserror && dotnet test && scripts/e2e-login.sh`.
  Expected: all green.
- [ ] **Step 3: Commit** — `docs: README quickstart and spec 0001 acceptance map`

---

## After the plan: verify, then merge

1. Dispatch the three local verifiers **in parallel** (Sonnet, fresh context, read-only),
   as defined in [`docs/workflow.md`](../../workflow.md#verification):
   realization vs **spec** (8 layers, using the acceptance map), API/e2e (clean stack,
   `e2e-login.sh` + an independent PyJWT check), security (keys, JWKS, logs, diff).
2. Each finding carries a `scope`. The implementer may make at most 2 fix rounds per verifier, then the issue goes to the owner.
3. When every verifier passes, merge the feature branch into `main` locally (the "MR").

## Open questions for owner

1. **Migrations at startup** (`Auth:Database:MigrateOnStartup`, default on only in
   Development). Should production migrate through the admin CLI (Week 3) instead?
   The default proposal is CLI-only for production.
2. **Response body is strictly `{status, access_token}`.** OpenIddict's `token_type`
   and `expires_in` are removed to match the spec exactly. Should a consumer later
   want `expires_in` to schedule refreshes, adding it is additive. Keep it out for now?
3. **`aud` source.** The spec names `RegisterAudiences(...)`. In OpenIddict the
   access token's `aud` is filled from the principal's *resources*, and
   `RegisterAudiences` registers the allowed `audience` request parameter. The
   plan does both: the audience is registered, and `aud` comes from `SetResources`.
   Criterion 3 (the `aud` value) is the authority. Confirm that this reading of the
   spec is acceptable.

## As built

Implemented, verified locally (three verifiers, PASS in round 3) and merged.
What the implementation settled, including where it departs from this plan, is
recorded in spec 0001 → "As built" and in the
[acceptance map](0001-acceptance-map.md). In particular, the Architecture
paragraph's "one-line `SetTokenEndpointUris` change" for refresh is superseded:
spec 0002 also needs its own extraction handler.
