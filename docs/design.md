# Auth Service — MVP Brief

_Design document for Auth-Core. As of 2026-01-03. Author: Aleksander Torka._

## Goal and context

Build one reusable, self-hosted auth service in .NET that every future project
drops in instead of rebuilding login, tenancy, RBAC and token refresh.
`speech-to-mail` is the first consumer; the MVP ships at the end of a six-week
build.

- **Internal tool, not a product for sale.** The repo may be public as a
  portfolio piece, but there is no support, docs site or marketing.
- **Consumers are unrelated projects.** No SSO and no shared users between them.
- **Why it is worth it.** For `speech-to-mail` alone this would be overkill;
  reuse across projects pays for it.
- **Portfolio value comes from judgement,** not from "I wrote my own auth":
  standard tokens, a threat model, real tests and a real consumer in another
  language.

## Key decisions

Each consumer runs its own instance of a headless REST auth API, with standard
JWTs underneath, served from `/auth` on the consumer's own domain.

| Decision       | Choice                                                                                | Why                                                                        |
| -------------- | ------------------------------------------------------------------------------------ | -------------------------------------------------------------------------- |
| Deployment     | One instance per consumer project: own database, users and signing keys              | Physical isolation, independent upgrades, no realm dimension in code       |
| SSO            | None                                                                                  | Consumers are unrelated                                                     |
| Interface      | Headless REST API; each app owns its login screens                                    | Native UX with no redirects; acceptable because every client is first-party |
| Token engine   | OpenIddict issuing JWTs, JWKS for verification                                        | Rotation, revocation and key handling are not hand-rolled; any language can verify a JWT |
| Not `MapIdentityApi` | Rejected                                                                        | Its bearer tokens are an opaque .NET format that Python consumers cannot verify |
| Placement      | Reverse proxy at `/auth` on the consumer's domain                                     | Same origin: no CORS, no redirects, passkeys bind to the app's domain      |
| Token storage  | Refresh token in an `HttpOnly; Secure; SameSite=Strict; Path=/auth` cookie; access token in memory, 5–10 min | No tokens in localStorage and no separate BFF                              |
| Tenancy        | Organizations inside an instance; global users with memberships; one org per user in the MVP | Schema is many-to-many from day one, so org switching is additive later    |
| Authorization  | RBAC: a per-app permission catalog, roles map to permissions, permissions travel in the access token | Resource-level checks ("is this my draft?") stay in the consumer           |
| Configuration  | `auth.yaml` manifest in the consumer's repo, applied idempotently                    | Config as code, reviewable in PRs                                          |
| Stack          | .NET 10 LTS, ASP.NET Core Identity, OpenIddict, EF Core, PostgreSQL                   | OpenIddict is Apache 2.0; Identity handles passwords and lockout           |

## Tech stack and dependencies

The service runs on .NET 10 LTS with OpenIddict 7 and PostgreSQL, and needs about
five third-party runtime packages; everything else ships with the framework.
Every dependency must have a permissive license (MIT, Apache 2.0, BSD) and active
maintenance. Versions are pinned in `Directory.Packages.props` at bootstrap.

**Auth service**

| Area                | Choice                                                          | Why                                                              |
| ------------------- | -------------------------------------------------------------- | --------------------------------------------------------------- |
| Runtime             | .NET 10 LTS, C# 14, ASP.NET Core Minimal APIs                  | LTS support until Nov 2028; minimal APIs fit a small REST surface |
| Users and passwords | `Microsoft.AspNetCore.Identity.EntityFrameworkCore`           | Password hashing, lockout, email and reset tokens; passkeys later |
| Token engine        | `OpenIddict.AspNetCore` + `OpenIddict.EntityFrameworkCore` 7.x | Password and refresh flows, rotation, revocation, JWKS; Apache 2.0 |
| Database            | PostgreSQL 16, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.x, EF Core 10 | Same Postgres as speech-to-mail, separate `auth` database        |
| Key storage         | `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`     | Identity's email and reset tokens survive restarts and replicas (not used: spec 0004, Decision 4 stores link tokens instead) |
| Validation          | Built-in minimal API validation (.NET 10)                     | No FluentValidation needed for a handful of DTOs                 |
| Rate limiting       | Built-in `Microsoft.AspNetCore.RateLimiting`                  | Per-IP and per-account limits on login, forgot and reset        |
| Email               | `MailKit`; Mailpit in dev and tests                          | Microsoft's recommended SMTP client; templates as `.resx` for PL/EN |
| Manifest            | `YamlDotNet`                                                   | Parses `auth.yaml`                                              |
| Admin CLI           | Four hand-parsed subcommands of the same binary (no library; spec 0005) | One image: `auth-server admin create-org ...`        |
| API docs            | Built-in `Microsoft.AspNetCore.OpenApi` + `Scalar.AspNetCore` (dev only) | OpenAPI document for typed clients later                        |
| Logging and health  | Built-in `ILogger` (JSON console), built-in health checks     | OpenTelemetry comes after the MVP                              |
| Container           | `mcr.microsoft.com/dotnet/aspnet:10.0` chiseled image, non-root | Small image, no shell, smaller attack surface                  |

**Tests and tooling**

| Area           | Choice                                                                                   |
| -------------- | ---------------------------------------------------------------------------------------- |
| Test framework | xUnit v3, `Microsoft.AspNetCore.Mvc.Testing` (WebApplicationFactory)                      |
| Integration    | `Testcontainers.PostgreSql`, Mailpit through a generic Testcontainers container           |
| Assertions     | Plain xUnit asserts, no extra dependency                                                  |
| Code quality   | `Nullable` enabled, `TreatWarningsAsErrors`, `AnalysisLevel` latest-recommended, `dotnet format` in CI |
| CI             | GitHub Actions: build and test, CodeQL (C#, Python), gitleaks, Dependabot, image to GHCR on tag |

**Consumers (speech-to-mail)**

| Area             | Choice                                                                          | Why                                              |
| ---------------- | ------------------------------------------------------------------------------ | ------------------------------------------------ |
| Python backend   | `PyJWT[crypto]` 2.x with `PyJWKClient`, on Python 3.12 and FastAPI 0.115        | Verifies RS256 JWTs with a cached JWKS; one new dependency |
| Angular frontend | No new dependency: `HttpClient` interceptor, route guard, signals, reactive forms on Angular 19 | Headless API means no OIDC client library         |
| E2E              | Playwright                                                                      | Already available in the environment             |
| Reverse proxy    | Caddy 2                                                                         | Short config for `/api` and `/auth`; `tls internal` for local HTTPS in dev |

**Deliberately not used:** Duende IdentityServer (commercial license),
`MapIdentityApi` (opaque tokens), MediatR, AutoMapper and FluentAssertions 8+
(moved to commercial licenses in 2025), Serilog and Newtonsoft.Json (the
framework covers both).

**Configuration gotchas to settle in week 1:**

- Call `DisableAccessTokenEncryption()`: OpenIddict encrypts access tokens by
  default, which Python cannot read.
- OpenIddict 7 rejects unregistered audiences, so call
  `RegisterAudiences("speech-to-mail-api")` from the manifest.
- Load signing keys from a mounted secret; development certificates are for local
  runs only.
- Persist Data Protection keys to the database, or email and reset links break on
  restart. (Superseded by spec 0004, Decision 4: link tokens are stored, not Data
  Protection tokens.)

## MVP scope

The MVP is done when a second company can use `speech-to-mail` with its own users
and roles. Estimated effort is about 32 focused days before AI-assisted speed-up.

| Area             | In the MVP                                                                                 | Later                                              |
| ---------------- | ----------------------------------------------------------------------------------------- | ------------------------------------------------- |
| Accounts         | Email and password, email verification, password reset, lockout                           | TOTP, passkeys, recovery codes, breached-password check |
| Login providers  | Password only                                                                              | Google, Microsoft, GitHub with account linking    |
| Tokens           | JWT access token, refresh rotation with reuse detection, logout with revocation, JWKS, persisted signing keys | Automated key rotation                            |
| Tenancy          | Organizations, memberships, email invites, `org_id` claim, one org per user               | Org switching, self-service org signup            |
| RBAC             | Permissions and roles from the manifest, role per membership, `permissions` in the token   | Admin UI for roles                               |
| Admin            | CLI: create org, invite owner, assign role                                                 | Admin panel, session and device management        |
| Consumers        | Python FastAPI package; Angular screens, interceptor and guard inside speech-to-mail       | NuGet SDK, npm package, generated clients          |
| Security and ops | Rate limiting, security headers, basic audit log, threat model, Docker image, health checks | OpenTelemetry, full audit log, OpenID conformance suite |

**Never in scope:** SAML, SCIM, LDAP, multi-realm mode, fine-grained resource
permissions (ReBAC), custom crypto or custom token formats.

**Deferred within the MVP (tracked, must be built later):** trusted
reverse-proxy real-client-IP resolution (`ForwardedHeaders` + `KnownNetworks`)
and the **per-IP rate limiting** that depends on it. Until these land, abuse
resistance is **per-identifier lockout only** (spec 0003), which does not
throttle distributed / multi-IP attacks and leaves a targeted account-lockout
DoS open. See
[`docs/superpowers/specs/0003-lockout-and-abuse-resistance.md`](superpowers/specs/0003-lockout-and-abuse-resistance.md)
→ "Deferred / follow-ups".

## Architecture and contracts

The browser sees one origin; the reverse proxy splits `/api` to the consumer
backend and `/auth` to the auth service, and the backend verifies tokens offline
via a cached JWKS. The browser only ever talks to `app.example.com`; the backend
contacts the auth service only to refresh its JWKS cache, never per request.

```
Browser ──> app.example.com (Caddy)
                 ├── /api  ──> consumer backend (verifies JWT via cached JWKS)
                 └── /auth ──> Auth-Core (issues JWTs, serves JWKS)
```

**Access token claims** (the stable contract every consumer relies on; changes
follow semver):

```json
{
  "iss": "https://app.example.com/auth",
  "aud": "speech-to-mail-api",
  "sub": "user id",
  "org_id": "organization id",
  "roles": ["worker"],
  "permissions": ["drafts:read", "drafts:accept"],
  "exp": 1700000600
}
```

**Endpoint sketch:**

```
POST /auth/login             {email, password} -> {status: "authenticated", access_token}
                                                | {status: "mfa_required", challenge}   (later)
POST /auth/refresh           (cookie) -> {access_token}
POST /auth/logout
POST /auth/password/forgot   -> always 202 (no user enumeration)
POST /auth/password/reset    {token, new_password}
POST /auth/invites/accept    {token, password}
POST /auth/email/verify      {token}
GET  /auth/me                -> {sub, org_id, roles, permissions}
POST /auth/orgs/{id}/invites (requires members:manage)
GET  /auth/.well-known/jwks.json
```

Links in emails point to the consumer's frontend (from the manifest), for example
`app.example.com/reset?token=...`; the frontend then calls the API. The API is
described in OpenAPI, so typed clients can be generated.

**Manifest example** (lives in the consumer's repo):

```yaml
app:
  name: speech-to-mail
  locale: pl
  frontend_urls:
    reset_password: https://app.example.com/reset
    accept_invite: https://app.example.com/invite
    verify_email: https://app.example.com/verify
clients:
  - id: speech-to-mail-web
    audience: speech-to-mail-api
tenancy:
  signup: invite-only
permissions: [drafts:read, drafts:accept, formats:manage, members:manage]
roles:
  owner:  ["*"]
  worker: [drafts:read, drafts:accept]
providers: [password]
```

## First consumer: speech-to-mail

`speech-to-mail` adopts the service behind an `AUTH_MODE=none|oidc` switch, so the
current single-company demo keeps working unchanged.

- **Backend (FastAPI):** a dependency that verifies the JWT against the cached
  JWKS, plus `require_permission("drafts:accept")` on endpoints; `org_id` added to
  tenant-owned tables such as `recordings`.
- **Frontend (Angular PWA):** login, password reset, invite acceptance and email
  verification screens in Polish; an HTTP interceptor; a route guard; a silent
  `/auth/refresh` on PWA start.
- **Compose:** an `auth` service with its own database on the existing Postgres,
  and the reverse proxy routing `/auth`.
- **Outside the auth service:** moving per-company config (format, IMAP
  credentials, signature) from env vars to the database. About 5–8 days, tracked
  as stretch.
- **Process:** multi-tenancy is listed under "Out of scope" in speech-to-mail's
  `docs/mvp/spec-v0.md`, so adopting it needs a recorded decision in `docs/mvp/`
  first.

## Timeline: six weeks

Six weeks at 15–20 hours a week, about 100 hours in total, with agents doing most
of the implementation. It fits only at about 20 h/week or with the stretch items
cut. The consumer work starts in week 4, so a real-phone test lands two weeks
before release.

- **Week 1: foundation.** Milestone: `curl` logs in and gets a JWT that verifies
  via JWKS.
  - Design doc and ADRs: headless REST, instance per project, OpenIddict, token contract
  - Repo skeleton: .NET 10, EF Core with Postgres, Docker, CI, Testcontainers
  - OpenIddict with a custom `POST /auth/login` issuing JWTs, JWKS, persisted signing keys
- **Week 2: account lifecycle.** Milestone: the full account lifecycle is covered
  by integration tests.
  - `/auth/refresh` with rotation and reuse detection in an HttpOnly cookie; `/auth/logout` with revocation
  - Lockout, rate limiting, uniform responses with no user enumeration
  - SMTP and templates: email verification, password forgot and reset
  - OpenAPI description
- **Week 3: tenancy and RBAC.** Milestone: invite, accept, then a token carrying
  `org_id` and permissions.
  - Organizations, memberships, invites linking to the consumer's frontend, `org_id` claim
  - Manifest loader for clients, permissions and roles; `permissions` in the access token
  - Admin CLI: create org, invite owner, assign role
- **Week 4: first consumer, backend.** Milestone: the speech-to-mail API accepts
  only valid tokens with the right permission.
  - Python package: FastAPI dependency, JWKS cache, `require_permission`
  - speech-to-mail: `AUTH_MODE` switch, `org_id` on tables, `/auth` proxy in compose
  - First test on a real phone (cookies, proxy, iOS PWA)
  - *As built (spec 0006):* slice 6 built the Python package and a sample product inside Auth-Core instead of changing
    speech-to-mail (Decision 2); the real-phone test moved to the frontend slice (Decision 4).
- **Week 5: first consumer, frontend.** Milestone: login on a phone works end to
  end.
  - Angular screens in Polish: login, reset, invite acceptance, verification
  - Interceptor, guard, silent refresh on PWA start
  - Playwright end-to-end tests
- **Week 6: hardening and release.** Milestone: `v0.1.0` tagged.
  - Threat model (STRIDE), security headers, basic audit log
  - Key rotation plan, backup runbook, README
  - Tag `v0.1.0`, image published to GHCR
  - Buffer for slips

**Stretch, cut first:** per-company config in the speech-to-mail database; full
audit log; extracting the Angular screens into a shared package.

## Risks

The biggest risk is the time budget; every other risk has a cheap early test.

| Risk                                                          | Mitigation                                                                    |
| ------------------------------------------------------------ | ----------------------------------------------------------------------------- |
| 100 hours does not fit into 15 h/week                        | Cut the stretch items first; keep the weekly milestone, not the task list      |
| OpenIddict learning curve, especially the custom login endpoint | Spike on the first weekend of week 1; fall back to the standard password grant if it stalls |
| Cookies, reverse proxy and iOS PWA behave differently on a phone | Test on a real phone in week 4, not week 5                                    |
| Email lands in spam                                          | Local SMTP catcher (for example Mailpit) until week 6, real SMTP only then      |
| Scope creep toward the full product                          | Anything not in the MVP table goes to "Beyond MVP" and waits for a consumer that needs it |

## Beyond MVP

The full internal product adds about 70 focused days (55 plus a 30% buffer),
roughly 100 days including the MVP; each item is built only when a consumer needs
it.

| Item                                                                  | Effort (days) |
| --------------------------------------------------------------------- | ------------- |
| Admin panel: orgs, users, roles, sessions, audit viewer               | 10            |
| Connected accounts, e.g. Gmail OAuth for speech-to-mail               | 9             |
| Hosted OIDC login module (optional; third-party tools + conformance)  | 5–6           |
| Passkeys, TOTP, recovery codes                                        | 5             |
| SDKs: NuGet, npm, extracted Python package                            | 5             |
| OpenTelemetry, automated key rotation, upgrade and migration tests    | 5             |
| Social login (Google, Microsoft, GitHub) with account linking         | 4             |
| Session and device management, full audit log                         | 4             |
| Multiple orgs per user with org switching                             | 3             |
| OpenID conformance suite in CI                                        | 3             |
| ADRs, full threat model, runbook                                      | 3             |
| Self-service org signup mode                                          | 2             |
| Breached-password check, stronger rate limiting                       | 2             |

## Repo bootstrap

Start the repo private and flip it to public when ready; history, PRs and CI runs
are all kept with their real dates.

```
auth-service/
  src/Auth.Server/              ASP.NET Core host, endpoints, OpenIddict
  src/Auth.Core/                users, orgs, memberships, roles, permissions
  src/Auth.Infrastructure/      EF Core, migrations, SMTP, manifest loader
  src/Auth.Cli/                 admin CLI
  tests/Auth.IntegrationTests/  WebApplicationFactory + Testcontainers
  clients/python/               FastAPI package
  docs/adr/  docs/threat-model.md
  deploy/docker-compose.example.yml
  auth.example.yaml
```

- Create a private repo and connect it to a session
- Commit this brief as `docs/design.md`, plus ADRs 0001–0004 (instance per
  project, headless REST, OpenIddict with JWT, same-origin cookie refresh)
- CI: build, tests, container image pushed to GHCR on tag
- Ruleset on `main` with required checks
- Secrets only in `.env` (git-ignored) and GitHub Secrets; `gitleaks` in CI
- Milestones for weeks 1–6 with one issue per task above
- Record the multi-tenancy decision in speech-to-mail's `docs/mvp/`
