# Hardening and Release v0.1.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0008
- **Date:** 2026-02-19
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0008-hardening-and-release.md`](../specs/0008-hardening-and-release.md)
  — the plan argues from the spec; where they disagree, **the spec wins** and the
  disagreement is a finding (see [`docs/workflow.md`](../../workflow.md)). The spec is not changed by this plan; the points
  that cannot be built as written are listed in "Open questions for the owner" at the end.
- **Based on:** `main` at `193d3c6` (merge of spec 0007: slice 7, `samples/notes-web`, `scripts/e2e-web.sh` and the TCP health check of the
  development compose are all in it). Task 10 edits slice 7's files; every anchor was checked against this base.

**Goal:** Someone who has never seen the project can deploy Auth-Core on a server from a published image, back it up, restore it, change its keys and
know which risks remain, and the service resists the common abuse of a public login endpoint: per-IP rate limiting behind a trusted-proxy rule, a
`503` instead of a `401` when the database is down, security headers on every answer, a basic audit log, company deletion, a production compose
file, the documents, and the release (`v0.1.0`, the image in GHCR, `python-v0.1.1`).

**Architecture:** Three new pieces sit in front of everything else in the pipeline, in this order: the outermost `ErrorHandlingMiddleware` (writes `500
internal_error`, and the `503` of a refresh that fails for a reason that is not the client's), the `SecurityHeaders` middleware (sets the table of the
contract in `OnStarting`, so every answer carries it), and, after the forwarded-headers middleware that is installed **only** when a proxy is configured,
the `RateLimitMiddleware` over Auth-Core's own sliding-window limiter on the injected `TimeProvider` (6 segments of 10 s). The audit log is one table written
by one `AuditLog` service: `Stage` adds the row to the unit of work of the change it describes (same transaction), `WriteAloneAsync` writes events that
change nothing on their own and never throws; a pruning service deletes old rows hourly. Company deletion is one `CompanyDeletionService` under the company
lock, used by `DELETE /auth/org` and by the operator CLI. The production compose file, the runbooks and the scripts that try them (`e2e-hardening.sh`,
`e2e-prod.sh` with a test overlay) close the slice.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, OpenIddict, EF Core on PostgreSQL 16 (Npgsql), xUnit v3 with Testcontainers; Python 3.12 (package and
sample tests); Angular (the slice-7 sample: Vitest specs and Playwright e2e); Caddy 2; Docker Compose. **No new NuGet, npm or pip package**
is added (owner rule): the limiter, the forwarded-headers handling (`Microsoft.AspNetCore.HttpOverrides`, in the framework), the audit log and the outage
tests (a TCP forwarder written in the test project) use the framework and the packages already referenced. The tools that run in Task 14's checks and
in the release (gitleaks by image digest, `pip-audit`, `npm audit`, `dotnet list package`) are the ones the spec names.

## Global Constraints

Values copied from the spec, one line each; every task includes them.

- **Client address:** the connection's remote address, unless that address is a trusted proxy: then the last address in `X-Forwarded-For` that is not itself a trusted proxy. `X-Forwarded-Proto` only from trusted proxies; `X-Forwarded-Host` never. `Auth:Proxy:KnownNetworks` (CIDR list) and `Auth:Proxy:KnownProxies` (plain addresses), both empty by default: nothing trusted, forwarded headers not read, a client-supplied `X-Forwarded-For` ignored. The framework defaults (loopback trusted, one hop) are never used; every hop is walked. `::ffff:a.b.c.d` is the IPv4 address; no remote address is `unknown`. The limiter partitions on the address (IPv6 by its `/64`); the audit log records the full address.
- **Limits per client address, one minute each:** `login` `POST /auth/login` 30; `refresh` `POST /auth/refresh` 60; `email` `POST /auth/password/forgot`, `/auth/password/reset`, `/auth/email/verify/request`, `/auth/email/verify` 10; `invite` `POST /auth/invites/preview`, `/auth/invites/accept` 20; `general` every other request under `/auth/` 300. One policy per request, chosen by method and path (case and a trailing slash ignored). Sliding window of one minute in 6 segments of 10 seconds on the injected `TimeProvider`, counted by Auth-Core's own limiter (Decision 13).
- **Settings:** `Auth:RateLimit:<Policy>:PermitPerMinute` (at least 1), `Auth:RateLimit:Enabled` (default `true`), `Auth:Audit:RetentionDays` (default 90, at least 1); a blank value means the default; an invalid one stops the host with a message naming the key. The test host turns the limiter off unless a test turns it on; the development compose passes the settings from `.env`.
- **Over the limit:** `429` with `{"error": "too_many_requests", "retry_after_seconds": <n>}`, `Retry-After: <n>`, `Cache-Control: no-store`; `<n>` is the number of seconds until the window holds fewer requests than the limit, at least 1. No other work (no password evaluated, no streak changed, no mail queued). A request over the limit is not counted. Counters in memory. `GET /auth/health` and `GET /auth/.well-known/jwks.json` are under `general`. The lockout of spec 0003 and the mail limits of specs 0004 and 0005 stay.
- **Refresh outage:** `server_error` from OpenIddict, or a transient `DbException`, a `TimeoutException` or a `SocketException` at any depth, on the refresh path: `503 {"error": "temporarily_unavailable"}`, `Retry-After: 5`, `Cache-Control: no-store`, no `Set-Cookie`; the cookie is neither cleared nor rotated. Every other error as spec 0002. Login, logout and the company API keep a `500`.
- **Unhandled errors:** Auth-Core's own outermost handler, in every environment (the developer exception page included): `500 {"error": "internal_error"}` with the security headers; logged.
- **Security headers on every response of Auth-Core:** `X-Content-Type-Options: nosniff`; `X-Frame-Options: DENY`; `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'`; `Referrer-Policy: no-referrer`; `Cross-Origin-Resource-Policy: same-origin`; `Cache-Control: no-store` plus `Pragma: no-cache`, except on the key set and the OpenAPI document (which send none today and get none). No `Server` header (`AddServerHeader = false`). No `Strict-Transport-Security`. `/auth/scalar` (Development) gets `default-src 'none'; script-src 'self' 'nonce-<per request>'; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'`, with Scalar's nonce, default fonts, telemetry and agent turned off. The no-store middleware of spec 0005 is replaced.
- **Fixes:** `/auth/health` is `GET` and `HEAD`, any other method `405` with `Allow: GET, HEAD`. A JSON body with a `charset` other than `utf-8` (any case) is `415 {"error": "unsupported_media_type"}` for `POST`, `PUT`, `PATCH`, `DELETE` under `/auth/` other than refresh and logout, checked before OpenIddict. The last label of an invitation domain as it reaches the relay is two or more ASCII letters or `xn--` and more, and as typed two or more letters of any script (`x@пример.рф` passes; `127.0x1`, `10.0.0.5`, `0x7f.1`, `host.123` are `400 invalid_request`, in the API and the CLI). The second development seed user's role: the first role of the development company, by name in ordinal order, that holds neither `members:manage` nor `*`, read from the roles stored in the database, not from the manifest (the spec, as amended by the owner). Data Protection keys in memory.
- **Audit:** table `audit_events` (`id`, `occurred_at`, `kind`, `actor_user_id`, `subject_user_id`, `subject_email` at most 256 characters, `org_id`, `org_name`, `target_id`, `client_ip`, `details` JSON), no foreign keys; never a password, token, link, cookie or mail body; the 22 kinds `login.succeeded`, `login.failed`, `login.locked`, `logout`, `refresh.reuse_detected`, `password.reset_requested`, `password.reset`, `email.verified`, `invite.sent`, `invite.resent`, `invite.accepted`, `invite.cancelled`, `member.removed`, `member.role_changed`, `role.created`, `role.updated`, `role.deleted`, `org.created`, `org.renamed`, `org.deleted`, `org.delete_refused`, `rate_limit.hit`; a change and its row in one transaction, events that change nothing on their own; `rate_limit.hit` at most once per address and policy per minute and never failing the request; pruned hourly past the retention; read by SQL.
- **Company deletion:** built-in `org:delete` (a `*` role holds it; in the catalog whether or not the manifest lists it); `DELETE /auth/org` `{"name", "password"}` with the answers and the order of the spec (permission, body, lockout, password, name, rule 1); CLI `auth-server admin delete-org --org <id> --confirm <name>`, the operator not bound by rule 1; under the company lock, in one transaction: the queued mails of the invitations, the invitations, the memberships, the roles, the company; every member's sessions revoked; an `org.deleted` row; accounts stay.
- **Production compose:** `deploy/docker-compose.prod.yml` (a file of its own), image `ghcr.io/mckcieply/auth-core:${AUTH_CORE_VERSION:?}`, Production, `Auth:Database:MigrateOnStartup=true`, no seed users, no Scalar, `read_only`, `tmpfs: /tmp`, `cap_drop: [ALL]`, `security_opt: [no-new-privileges:true]`, port `127.0.0.1:${AUTH_PORT:-8080}:8080`, PostgreSQL pinned by digest with no published port; a network with a fixed subnet (`${AUTH_SUBNET}`, with a default); no default for any secret; `deploy/.env.prod.example` lists every variable. Dockerfile base images pinned by digest, labels from build arguments; the development compose pins `postgres` and `mailpit` by digest.
- **The samples and the package:** the Caddyfiles send the headers of the spec and pass the client address to Auth-Core, their compose files trust the proxy's network; Angular shows "Try again in N s." on `429 too_many_requests` and keeps the session on refresh `503` and `429`; OpenAPI adds `DELETE /auth/org`, `429` with `Retry-After` on every endpoint under a limit, `415` on JSON-body endpoints, `503` on refresh and `org:delete`; the Python package changes only its metadata (`license = "MIT"`, the licence file, a readme, version `0.1.1`).
- **Release:** annotated tags `v0.1.0` and `python-v0.1.1` on the merge commit (`python-v0.1.0` is not moved); the image built from the tag with the OCI labels and pushed as `ghcr.io/mckcieply/auth-core:0.1.0` and `:latest`, a public package. Release without CI (Decision 1).
- **Workflow** ([`docs/workflow.md`](../../workflow.md)): local only (no CI, no pull request), Conventional Commits in English, documentation in English. **The orchestrator makes the commits**: implementers leave their changes uncommitted in the working tree, and the orchestrator reads the diff, runs the gate and commits with the subject named in the task's last step ("Hand back"). Every task ends with that step; it lists files and a proposed subject, never a git command.
- **.NET:** run tests with `"C:/Program Files/dotnet/dotnet.exe" test`; the build has warnings as errors (`-warnaserror` is the gate, the analyzers are strict: `ArgumentNullException.ThrowIfNull`, explicit `StringComparison`, `CultureInfo.InvariantCulture`, `[LoggerMessage]` partial classes). Test classes follow the existing bases (`SessionTestBase`, `MailTestBase`, `TenancyTestBase`) and the injected `FakeTimeProvider` (`Clock`).
- **Every compose command carries its own project name.** A script or an instruction that starts, stops, runs, execs in or removes a stack sets `COMPOSE_PROJECT_NAME` to a name of its own (`auth-core-hardening` for the older scripts and `e2e-hardening.sh` on one stack, `auth-core-notes`, `auth-core-web` or `auth-core-web-<suffix>` (the only names `scripts/e2e-web.sh` accepts; it also refuses to start over a running stack), `auth-core-prodtest`); never `auth-core`, the owner's development project, which a bare `down -v` would wipe. The development compose's PostgreSQL health check stays `pg_isready -h 127.0.0.1 ...` (over TCP, slice 7) in every edit of `deploy/docker-compose.yml`.
- **No `docker compose up` of the Auth-Core stack and no stopping of containers by implementers** (another session holds ports 8080 and 8025): tests use Testcontainers; the live scripts are run by the verifiers on Tuesday, one stack at a time.
- **Python on this machine:** `python3` on `PATH` is the Microsoft Store shim and does not work. Use `C:/p6v/Scripts/python.exe` (the virtual environment of plan 0006).
- **Shell scripts** are LF; after the implementer creates one, the **orchestrator** marks it executable in the index (`git update-index --chmod=+x <file>`); the implementer does not touch git state.
- **The development keys:** `scripts/dev-keys.sh` runs only as `MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh` on Windows.
- **Unicode escapes stay out of the source.** A backslash followed by `u` and four hex digits typed in a tool parameter arrives as the character. The tests below build such text with `char.ConvertFromUtf32(...)` or `(char)0x...`; after writing files, `grep -rInP '\\u[0-9a-fA-F]{4}' src tests scripts docs clients samples --include=*.cs --include=*.sh --include=*.py --include=*.md --include=*.ts` must find nothing that is not meant (the files of slice 7 and the Angular build output excluded).

## Verified before this plan was written

Probes in a scratch copy (not in the repository) settled the points where the framework could surprise. The code blocks of this plan are the probed ones.

- **The limiter:** the framework's `SlidingWindowRateLimiter` reports no `RetryAfter` for a sliding window and cannot be driven by a fake clock, so Auth-Core's own limiter is the contract's choice (Decision 13). The `KnownNetworks` property of `ForwardedHeadersOptions` is obsolete in .NET 10 (a warning, an error under warnings-as-errors): `KnownIPNetworks` takes `System.Net.IPNetwork`. `ForwardedHeadersOptions` has defaults (loopback, one hop): both lists are cleared and `ForwardLimit = null` so that every hop is walked, and the middleware is not installed at all when no proxy is configured.
- **A controllable client address in tests:** the test server has no remote address. `WithServices` called after the host was built by `InitializeAsync` registers nothing; the working form is a plain `AddSingleton<IStartupFilter>` in the test's **constructor** (`AuthAppFactory.WithRemoteAddressHeader()`).
- **The outage:** with PostgreSQL stopped, the first database read happens while OpenIddict authenticates the refresh request and the exception escapes (a `500` with an empty body today). The tests stop and start a TCP forwarder in front of the test PostgreSQL (`DatabaseOutage`), so no other test is disturbed. OpenIddict keeps the request in a `WeakReference<HttpRequest>`: a handler harness needs a real request, not a mocked context.
- **Reuse of a refresh token:** the place where OpenIddict finds a redeemed token presented again is its own validation handler `ValidateTokenEntry`; a scoped handler ordered just before it that pre-checks "status redeemed, redemption older than the 15-second leeway" records `refresh.reuse_detected` without changing OpenIddict's decision (`RefreshReuseAuditHandler`).
- **Role order** (the spec was amended to "by name in ordinal order" after this probe): the role ids are version-7 UUIDs that are random inside one millisecond and EF Core inserts the roles of one company in id order, so neither the order of the ids nor the physical order is the order of the manifest. The spec (as amended) reads the roles from the database and takes the first by name, in ordinal order.
- **Deleting a company:** the keys from memberships and invitations to roles are `Restrict`, so one delete of the company would stop at them; the order mails of the invitations, invitations, memberships, roles, company is the one the spec names, and Task 7's `CompanyDeletionTests` and `DeleteOrgCommandTests` prove it on PostgreSQL.
- **Scalar:** `ScalarOptions.NonceHttpContextItemKey` hands the per-request nonce to Auth-Core's policy; `WithNonce().DisableDefaultFonts().DisableTelemetry().DisableAgent()` is the option set.
- **IDN under invariant globalization:** the invitation rule works on the ASCII form and on the typed form with Unicode categories, and its tests also pass with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` (a container image may run in that mode).
- **The production compose file and its environment example** render with `docker compose config` (checked on this machine); the gitleaks fingerprints in `.gitleaksignore` are written without a commit hash so that they survive any rewrite of the history; and `scripts/secret-scan.sh` was run on the history and on the files not committed yet (it found the plan's own wrong-password literal, which was then changed).
- **Atomic sign-in and logout:** in a scratch copy of the repository, with PostgreSQL 16 from Testcontainers, a sign-in executed inside a transaction on the request's `AuthDbContext` with the response body held in memory stored the authorization, the tokens and a staged row together and answered the contract body with the cookie; with the commit made to fail, or with the table of the staged row dropped, it stored nothing, answered `500` with no `Set-Cookie` and no `access_token`; a logout inside such a transaction rolled the revocation back and the cookie still refreshed.
- **The refusal of `10.250.0.5/24` as a network:** `IPNetwork.TryParse` of .NET 10 accepts it and masks it to `10.250.0.0/24`; `ProxySettings.Load` compares the network's base address with the address written and refuses a difference. A quoted `charset="utf-8"` keeps its quotes in `MediaTypeHeaderValue.Charset`; `HeaderUtilities.RemoveQuotes` removes them. The in-memory test server returns the body of the health check on `HEAD`, which Kestrel does not (the live script checks it).
- **The revocation list of the production test:** `openssl ca -gencrl` writes an empty list that `openssl verify -crl_check` accepts for a certificate whose distribution point names it.
- **Not proved on this machine:** that a proxy on the host appears to the container as the gateway of its compose network on Docker Desktop (Open question 4); that .NET's fetch and cache of the revocation list work on the read-only root file system of the production container (the verifier probes it first on Tuesday: Task 12); the live scripts, which wait for Tuesday and the free ports.

## Review Focus

The inputs and failure modes the spec implies that a person using this software is most likely to meet, most likely first; each has its test in the task named.

1. **An address behind a proxy in the other spellings:** IPv4-mapped IPv6, an IPv6 address by its `/64`, a forged `X-Forwarded-For` from an untrusted sender, a header whose every hop is trusted, a list with a blank entry; the person expects the right address to be counted, never the forged one (`ClientAddressTests`, `ClientAddressSetupTests`, `ProxySettingsTests.Blank_entries_count_as_unset`, `RateLimitMiddlewareTests`; Task 1).
2. **A flood:** the request over the limit does no other work and the limiter's memory follows the traffic (`RateLimitMiddlewareTests.The_request_over_the_limit_is_a_429_that_evaluates_no_password_and_changes_no_streak`, `SlidingWindowLimiterTests.Idle_partitions_are_forgotten`, `.Requests_that_arrive_together_cannot_pass_the_limit`; Task 1).
3. **A failing audit write:** it must not fail a refusal (a failed login, a rate-limit hit), and a change whose row cannot be written must roll back, a session issued or ended included (`AuditRateLimitTests.A_failing_audit_write_never_fails_the_request`, `AuditLogTests.A_write_on_its_own_that_fails_is_logged_and_never_thrown_and_is_not_retried`, `AuditAccountFlowsTests.A_login_whose_row_cannot_be_written_issues_no_session`, `.A_logout_whose_row_cannot_be_written_ends_nothing`, `AuditAtomicityTests`; Tasks 4 to 6).
4. **Deleting a company in the orders and races a person meets:** a wrong name with the right password, a locked identifier, two deletions at once, a member with more rights (`OrgDeleteEndpointTests`; Task 8).
5. **A setting that compose passes empty, or that is wrong, in Production:** blank means the default, a bad value stops the host naming the key and never the value (`RateLimitSettingsTests.A_blank_value_means_the_default`, `ProxySettingsTests.A_bad_entry_stops_the_host_naming_the_key_and_not_the_value`, `RateLimitDefaultsTests`; Task 9's static checks; `e2e-prod.sh` step 1).

## File Structure

New and changed files by responsibility. Everything under `src/Auth.Server/` unless said otherwise; tests under `tests/Auth.IntegrationTests/`.

| Area | Files | Responsibility |
| --- | --- | --- |
| Client address | `Network/ProxySettings.cs`, `Network/ClientAddress.cs`, `Network/ClientAddressSetup.cs` | read the trusted proxies, compute the client address and its partition, install forwarded headers only when a proxy is configured (Task 1) |
| Rate limiting | `RateLimiting/RatePolicy.cs`, `RateLimitSettings.cs`, `SlidingWindowLimiter.cs`, `TooManyRequestsResult.cs`, `RateLimitMiddleware.cs`, `RateLimitAudit.cs` | classify, count, refuse, record (Tasks 1 and 5) |
| Sign-in | `Login/AtomicSignInResult.cs` | the session of a login and its audit row in one transaction (Task 5) |
| Pipeline edges | `Api/ErrorHandling.cs`, `Api/SecurityHeaders.cs`, `Api/JsonCharsetGuard.cs` (replaces `Api/NoStoreMiddleware.cs`), `Sessions/SessionResponseHandler.cs`, `Api/OpenApiSetup.cs` | outermost handler, the `503`, the headers, the charset guard, Scalar's policy (Tasks 2, 3, 8) |
| Fixes | `Api/AccountEndpoints.cs`, `Requests/EmailInput.cs`, `Seeding/DevUserSeeder.cs` | health methods, invitation domains, the second seed user (Task 3) |
| Audit | `Audit/*` (`AuditKinds`, `AuditEntry`, `AuditLog`, `AuditSettings`, `AuditPruner`, the pruning service), `src/Auth.Infrastructure/Persistence/AuditEvent.cs` and the migration `AddAuditEvents`, `Sessions/RefreshReuseAuditHandler.cs` | table, writer, retention, the hooks of every flow (Tasks 4 to 6) |
| Company deletion | `Tenancy/CompanyDeletionService.cs`, `Tenancy/PermissionCatalog.cs`, `Tenancy/OperatorCommands.cs`, `Admin/*`, `Api/OrgEndpoints.cs`, `Api/TenancyEndpoints.cs` | `org:delete`, the service, the CLI, the endpoint (Tasks 7 and 8) |
| Production | `Dockerfile`, `deploy/docker-compose.yml`, `deploy/docker-compose.prod.yml`, `deploy/.env.prod.example`, `.env.example`, `.gitignore` | pinned images, the production compose (Task 9) |
| Samples | `samples/notes-api/Caddyfile`, `compose.yml`, `samples/notes-web/*` (slice 7) | headers, client address, the notice (Task 10) |
| Checks | `scripts/secret-scan.sh`, `scripts/check-docs.py` | the secret scan of the history and of the files not committed yet; the documents against the repository (Tasks 13 and 14) |
| Live checks | `scripts/e2e-hardening.sh`, `scripts/prod-test.compose.yml`, `scripts/prod-test.Caddyfile`, `scripts/e2e-prod.sh`, edits to the older scripts | the criteria that need a live stack (Tasks 11 and 12) |
| Documents | `docs/security/threat-model.md`, `docs/operations/backup.md`, `docs/operations/key-rotation.md`, `docs/deployment/vps.md`, `README.md`, `CHANGELOG.md`, `clients/python/*`, `.gitleaksignore`, `docs/superpowers/plans/0008-acceptance-map.md` | the documents, the licence metadata, the acceptance map (Tasks 13 and 14) |

## The tasks

| Day | Task | Deliverable |
| --- | --- | --- |
| Fri 20.02 | 1 | The client address, trusted proxies and the per-IP rate limiter |
| Fri 20.02 | 2 | The outermost error handler, the security headers, the outage answer, the host settings |
| Fri 20.02 | 3 | Health methods, the JSON charset, invitation domains, the second seed user |
| Sat 21.02 | 4 | The audit table, the writer and the pruning |
| Sat 21.02 | 5 | The audit events of the account flows |
| Sat 21.02 | 6 | The audit events of the company flows |
| Sun 22.02 | 7 | `org:delete`, the deletion service and the CLI command |
| Sun 22.02 | 8 | `DELETE /auth/org`, the new error codes and the OpenAPI additions |
| Sun 22.02 | 9 | Pinned images, the production compose file, its environment file, the development compose |
| Mon 23.02 | 10 | The sample proxies and the Angular sample |
| Mon 23.02 | 11 | `scripts/e2e-hardening.sh` and the older e2e scripts |
| Mon 23.02 | 12 | The production test overlay and `scripts/e2e-prod.sh` |
| Mon 23.02 | 13 | The threat model, the backup runbook, the key rotation runbook, `scripts/check-docs.py` |
| Mon 23.02 | 14 | The deployment guide, README, changelog, package metadata, `scripts/secret-scan.sh` and its list, and the acceptance map |
| Tue 24.02 (before verification) | 15 | A database role of its own for Auth-Core in production: the init script, the compose file, the restore, the e2e checks, the documents |
| Tue 24.02 | verifiers | The three verifiers, the "As built", the merge, the tags and the release (below, owner-gated) |

Tasks run in order inside a day and across days; each leaves `dotnet build -warnaserror` and the tests of the files it touches green, and each task's tests are
written first and seen to fail.

---

## Day 1 — Friday 20.02: the front door

When the day is done, a request to Auth-Core has a client address that cannot be spoofed, is counted per address against the five limits of the contract, and every answer of the pipeline carries the security headers, also when it is an error. Three tasks; every task leaves `dotnet build -warnaserror && dotnet test` green.

### Task 1: The client address, trusted proxies and the per-IP rate limiter

**Files:**
- Create: `src/Auth.Server/Network/ProxySettings.cs`, `src/Auth.Server/Network/ClientAddress.cs`, `src/Auth.Server/Network/ClientAddressSetup.cs`
- Create: `src/Auth.Server/RateLimiting/RatePolicy.cs`, `src/Auth.Server/RateLimiting/RateLimitSettings.cs`, `src/Auth.Server/RateLimiting/SlidingWindowLimiter.cs`, `src/Auth.Server/RateLimiting/TooManyRequestsResult.cs`, `src/Auth.Server/RateLimiting/RateLimitMiddleware.cs`
- Create: `tests/Auth.IntegrationTests/Infrastructure/RemoteAddressFilter.cs`, `tests/Auth.IntegrationTests/Infrastructure/RateLimitApi.cs`
- Modify: `src/Auth.Server/Program.cs`, `src/Auth.Server/Tenancy/Outcome.cs` (it holds `TenancyErrors`), `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`
- Test: `tests/Auth.IntegrationTests/ProxySettingsTests.cs`, `RateLimitSettingsTests.cs`, `RatePolicyTests.cs`, `ClientAddressTests.cs`, `ClientAddressSetupTests.cs`, `SlidingWindowLimiterTests.cs`, `RateLimitMiddlewareTests.cs`, `RateLimitProxyTests.cs`, `RateLimitDefaultsTests.cs`

**Interfaces:**
- Consumes: `TimeProvider` (singleton, replaced by the tests' `FakeTimeProvider`), `AuthAppFactory.WithSetting`, `SessionTestBase`.
- Produces (src):
  - `Auth.Server.Network.ProxySettings` with `const string KnownNetworksKey = "Auth:Proxy:KnownNetworks"`, `const string KnownProxiesKey = "Auth:Proxy:KnownProxies"`, `IReadOnlyList<System.Net.IPNetwork> Networks`, `IReadOnlyList<System.Net.IPAddress> Proxies`, `bool IsEmpty`, `static ProxySettings None`, `static ProxySettings Load(IConfiguration configuration)`, constructor `ProxySettings(IReadOnlyList<System.Net.IPNetwork> networks, IReadOnlyList<System.Net.IPAddress> proxies)`.
  - `Auth.Server.Network.ClientAddress` (static): `const string Unknown = "unknown"`, `System.Net.IPAddress? Of(HttpContext context)` (IPv4-mapped IPv6 turned into IPv4), `string Text(HttpContext context)` (the full address, or `unknown`), `string PartitionOf(HttpContext context)`, `string PartitionOf(System.Net.IPAddress? address)` (IPv4: the address; IPv6: its `/64` network as `2001:db8:1:2::/64`; none: `unknown`).
  - `Auth.Server.Network.ClientAddressSetup` (static): `Microsoft.AspNetCore.Builder.ForwardedHeadersOptions OptionsFor(ProxySettings proxies)`, extension `IApplicationBuilder UseClientAddress(this IApplicationBuilder app, ProxySettings proxies)` (adds nothing when `proxies.IsEmpty`).
  - `Auth.Server.RateLimiting.RatePolicy` enum `{ Login, Refresh, Email, Invite, General }`; `static class RatePolicies` with `string NameOf(RatePolicy)` (`login`, `refresh`, `email`, `invite`, `general`), `int DefaultPermitPerMinute(RatePolicy)` (30, 60, 10, 20, 300), `RatePolicy? Classify(string method, PathString path)`.
  - `Auth.Server.RateLimiting.RateLimitSettings` with `const string EnabledKey = "Auth:RateLimit:Enabled"`, `static string PermitKey(RatePolicy policy)` (`Auth:RateLimit:<Policy>:PermitPerMinute`), `bool Enabled`, `int PermitPerMinute(RatePolicy policy)`, `static RateLimitSettings Load(IConfiguration configuration)`, constructor `RateLimitSettings(bool enabled, IReadOnlyList<int> permits)` (the list is indexed by `(int)RatePolicy`).
  - `Auth.Server.RateLimiting.RateDecision(bool Allowed, int RetryAfterSeconds)` and `SlidingWindowLimiter(TimeProvider clock)` with `RateDecision TryAcquire(RatePolicy policy, string partition, int permitPerMinute)` and `int PartitionCount`.
  - `Auth.Server.RateLimiting.TooManyRequestsResult(int retryAfterSeconds) : IResult` (the `429` of the contract) and `RateLimitMiddleware` (`app.UseMiddleware<RateLimitMiddleware>()`).
  - `TenancyErrors.TooManyRequests = "too_many_requests"` (status `429` in `StatusOf`).
- Produces (tests): `AuthAppFactory.WithRemoteAddressHeader()` (call it in the test's constructor: the host then takes the client address of each request from the header `X-Test-Remote-Address`; the test server has none otherwise), `AuthAppFactory.RemoteAddressHeader` (the header's name), `RateLimitApi` helpers (below). The test host turns the limiter off (`Auth:RateLimit:Enabled=false`) and trusts no proxy unless a test says otherwise.

**Why the limiter is our own** (Decision 13, probe): the framework's `SlidingWindowRateLimiter` gives no `RetryAfter` metadata for a sliding window and cannot be driven by a fake clock. Ours keeps, per `(policy, partition)`, six counters of ten seconds each on the injected `TimeProvider`; a request is let through when the six counters add up to less than the limit; a refused request counts nothing; `retry_after_seconds` is the time until the oldest counters leave the window far enough for the sum to fall below the limit. Idle partitions are swept once a minute, so memory follows the traffic of the last minute. The middleware sits after the forwarded-headers middleware (it needs the real address) and before authentication (login is handled by OpenIddict inside it).

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/ProxySettingsTests.cs`:

```csharp
using System.Net;
using Auth.Server.Network;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests;

public sealed class ProxySettingsTests
{
    private static ProxySettings Load(Dictionary<string, string?> values) =>
        ProxySettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void Nothing_is_trusted_by_default()   // criterion 2
    {
        var settings = Load([]);

        Assert.True(settings.IsEmpty);
        Assert.Empty(settings.Networks);
        Assert.Empty(settings.Proxies);
        Assert.True(ProxySettings.None.IsEmpty);
    }

    [Fact]
    public void Blank_entries_count_as_unset()   // what compose passes for a variable that is not set
    {
        var settings = Load(new()
        {
            [ProxySettings.KnownNetworksKey + ":0"] = "",
            [ProxySettings.KnownProxiesKey] = "  ",
        });

        Assert.True(settings.IsEmpty);
    }

    [Fact]
    public void Networks_and_proxies_are_read_as_lists()
    {
        var settings = Load(new()
        {
            [ProxySettings.KnownNetworksKey + ":0"] = "10.250.0.0/24",
            [ProxySettings.KnownNetworksKey + ":1"] = "fd00::/8",
            [ProxySettings.KnownProxiesKey + ":0"] = "10.0.0.7",
            [ProxySettings.KnownProxiesKey + ":1"] = "2001:db8::1",
        });

        Assert.False(settings.IsEmpty);
        Assert.Equal(["10.250.0.0/24", "fd00::/8"], settings.Networks.Select(n => n.ToString()));
        Assert.Equal(["10.0.0.7", "2001:db8::1"], settings.Proxies.Select(p => p.ToString()));
    }

    [Fact]
    public void One_value_may_hold_several_entries_separated_by_commas()   // a .env file has one line per variable
    {
        var settings = Load(new()
        {
            [ProxySettings.KnownNetworksKey] = "10.250.0.0/24, 10.250.1.0/24",
            [ProxySettings.KnownProxiesKey] = "10.0.0.7,10.0.0.8",
        });

        Assert.Equal(2, settings.Networks.Count);
        Assert.Equal(2, settings.Proxies.Count);
    }

    [Fact]
    public void A_proxy_written_as_an_ipv4_mapped_address_is_the_ipv4_address()   // a plain remote would not match the mapped form
    {
        var settings = Load(new() { [ProxySettings.KnownProxiesKey + ":0"] = "::ffff:10.0.0.7" });

        Assert.Equal([IPAddress.Parse("10.0.0.7")], settings.Proxies);
    }

    [Theory]
    [InlineData(ProxySettings.KnownNetworksKey, "10.250.0.0")]       // no prefix length
    [InlineData(ProxySettings.KnownNetworksKey, "10.250.0.5/24")]    // bits set beyond the prefix: .NET 10 parses it and masks it to 10.250.0.0/24, we refuse it
    [InlineData(ProxySettings.KnownNetworksKey, "10.250.0.0/33")]
    [InlineData(ProxySettings.KnownNetworksKey, "not-a-network")]
    [InlineData(ProxySettings.KnownProxiesKey, "10.250.0.0/24")]     // a network where an address belongs
    [InlineData(ProxySettings.KnownProxiesKey, "proxy.internal")]    // a name, not an address
    [InlineData(ProxySettings.KnownProxiesKey, "300.1.1.1")]
    public void A_bad_entry_stops_the_host_naming_the_key_and_not_the_value(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(new() { [key + ":0"] = value }));

        Assert.Contains($"'{key}'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
    }
}
```

`tests/Auth.IntegrationTests/RateLimitSettingsTests.cs`:

```csharp
using Auth.Server.RateLimiting;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests;

public sealed class RateLimitSettingsTests
{
    private static RateLimitSettings Load(Dictionary<string, string?> values) =>
        RateLimitSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void The_defaults_are_the_numbers_of_the_contract()
    {
        var settings = Load([]);

        Assert.True(settings.Enabled);
        Assert.Equal(30, settings.PermitPerMinute(RatePolicy.Login));
        Assert.Equal(60, settings.PermitPerMinute(RatePolicy.Refresh));
        Assert.Equal(10, settings.PermitPerMinute(RatePolicy.Email));
        Assert.Equal(20, settings.PermitPerMinute(RatePolicy.Invite));
        Assert.Equal(300, settings.PermitPerMinute(RatePolicy.General));
    }

    [Fact]
    public void The_keys_are_named_by_policy()
    {
        Assert.Equal("Auth:RateLimit:Login:PermitPerMinute", RateLimitSettings.PermitKey(RatePolicy.Login));
        Assert.Equal("Auth:RateLimit:Refresh:PermitPerMinute", RateLimitSettings.PermitKey(RatePolicy.Refresh));
        Assert.Equal("Auth:RateLimit:Email:PermitPerMinute", RateLimitSettings.PermitKey(RatePolicy.Email));
        Assert.Equal("Auth:RateLimit:Invite:PermitPerMinute", RateLimitSettings.PermitKey(RatePolicy.Invite));
        Assert.Equal("Auth:RateLimit:General:PermitPerMinute", RateLimitSettings.PermitKey(RatePolicy.General));
        Assert.Equal("Auth:RateLimit:Enabled", RateLimitSettings.EnabledKey);
    }

    [Fact]
    public void A_policy_is_set_on_its_own()
    {
        var settings = Load(new() { [RateLimitSettings.PermitKey(RatePolicy.Email)] = "25", [RateLimitSettings.EnabledKey] = "false" });

        Assert.False(settings.Enabled);
        Assert.Equal(25, settings.PermitPerMinute(RatePolicy.Email));
        Assert.Equal(30, settings.PermitPerMinute(RatePolicy.Login));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_value_means_the_default(string? blank)   // compose passes an empty string for a variable that is not set
    {
        var settings = Load(new()
        {
            [RateLimitSettings.EnabledKey] = blank,
            [RateLimitSettings.PermitKey(RatePolicy.Login)] = blank,
        });

        Assert.True(settings.Enabled);
        Assert.Equal(30, settings.PermitPerMinute(RatePolicy.Login));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("99999999999")]
    public void A_permit_that_is_not_a_whole_number_of_at_least_one_stops_the_host_naming_the_key(string value)
    {
        var key = RateLimitSettings.PermitKey(RatePolicy.Refresh);

        var ex = Assert.Throws<InvalidOperationException>(() => Load(new() { [key] = value }));

        Assert.Contains($"'{key}'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TRUE", true)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    public void Enabled_reads_true_and_false_in_any_case(string value, bool expected) =>
        Assert.Equal(expected, Load(new() { [RateLimitSettings.EnabledKey] = value }).Enabled);

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("off")]
    public void Enabled_that_is_not_true_or_false_stops_the_host_naming_the_key(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(new() { [RateLimitSettings.EnabledKey] = value }));

        Assert.Contains($"'{RateLimitSettings.EnabledKey}'", ex.Message, StringComparison.Ordinal);
    }
}
```

`tests/Auth.IntegrationTests/RatePolicyTests.cs`:

```csharp
using Auth.Server.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

public sealed class RatePolicyTests
{
    [Theory]
    [InlineData("POST", "/auth/login", RatePolicy.Login)]
    [InlineData("POST", "/auth/refresh", RatePolicy.Refresh)]
    [InlineData("POST", "/auth/password/forgot", RatePolicy.Email)]
    [InlineData("POST", "/auth/password/reset", RatePolicy.Email)]
    [InlineData("POST", "/auth/email/verify/request", RatePolicy.Email)]
    [InlineData("POST", "/auth/email/verify", RatePolicy.Email)]
    [InlineData("POST", "/auth/invites/preview", RatePolicy.Invite)]
    [InlineData("POST", "/auth/invites/accept", RatePolicy.Invite)]
    [InlineData("POST", "/auth/logout", RatePolicy.General)]
    [InlineData("GET", "/auth/health", RatePolicy.General)]
    [InlineData("GET", "/auth/.well-known/jwks.json", RatePolicy.General)]
    [InlineData("GET", "/auth/me", RatePolicy.General)]
    [InlineData("PATCH", "/auth/org", RatePolicy.General)]
    [InlineData("POST", "/auth/org/invites", RatePolicy.General)]
    [InlineData("GET", "/auth/nothing/here", RatePolicy.General)]
    [InlineData("GET", "/auth", RatePolicy.General)]
    [InlineData("GET", "/auth/", RatePolicy.General)]
    public void A_request_counts_against_one_policy_by_its_method_and_path(string method, string path, RatePolicy expected) =>
        Assert.Equal(expected, RatePolicies.Classify(method, new PathString(path)));

    [Theory]
    [InlineData("GET", "/auth/login")]       // the policy is for the POST; any other method is an ordinary request
    [InlineData("HEAD", "/auth/refresh")]
    [InlineData("PUT", "/auth/password/forgot")]
    [InlineData("DELETE", "/auth/invites/accept")]
    public void The_method_matters(string method, string path) =>
        Assert.Equal(RatePolicy.General, RatePolicies.Classify(method, new PathString(path)));

    [Theory]
    [InlineData("/AUTH/Login")]
    [InlineData("/auth/login/")]
    [InlineData("/Auth/LOGIN/")]
    public void The_path_is_compared_without_regard_to_case_or_a_trailing_slash(string path) =>
        Assert.Equal(RatePolicy.Login, RatePolicies.Classify("POST", new PathString(path)));

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("GET", "/nope")]
    [InlineData("POST", "/authx/login")]   // not under /auth/
    [InlineData("POST", "/api/auth/login")]
    public void A_request_outside_auth_is_not_limited(string method, string path) =>
        Assert.Null(RatePolicies.Classify(method, new PathString(path)));

    [Fact]
    public void The_names_and_defaults_are_those_of_the_contract()
    {
        Assert.Equal(["login", "refresh", "email", "invite", "general"], Enum.GetValues<RatePolicy>().Select(RatePolicies.NameOf));
        Assert.Equal([30, 60, 10, 20, 300], Enum.GetValues<RatePolicy>().Select(RatePolicies.DefaultPermitPerMinute));
    }
}
```

`tests/Auth.IntegrationTests/ClientAddressTests.cs`:

```csharp
using System.Net;
using Auth.Server.Network;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

public sealed class ClientAddressTests
{
    private static DefaultHttpContext With(string? remote)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
        return context;
    }

    [Fact]
    public void An_ipv4_address_is_its_own_partition()
    {
        var context = With("203.0.113.9");

        Assert.Equal("203.0.113.9", ClientAddress.Text(context));
        Assert.Equal("203.0.113.9", ClientAddress.PartitionOf(context));
    }

    [Fact]
    public void An_ipv4_address_that_reaches_kestrel_mapped_into_ipv6_is_the_ipv4_address()
    {
        var context = With("::ffff:203.0.113.9");

        Assert.Equal(IPAddress.Parse("203.0.113.9"), ClientAddress.Of(context));
        Assert.Equal("203.0.113.9", ClientAddress.Text(context));
        Assert.Equal("203.0.113.9", ClientAddress.PartitionOf(context));
    }

    [Fact]
    public void An_ipv6_address_is_counted_by_its_64_bit_network_and_recorded_in_full()
    {
        var one = With("2001:db8:1:2::1");
        var other = With("2001:db8:1:2:ffff:ffff:ffff:ffff");
        var elsewhere = With("2001:db8:1:3::1");

        Assert.Equal("2001:db8:1:2::/64", ClientAddress.PartitionOf(one));
        Assert.Equal(ClientAddress.PartitionOf(one), ClientAddress.PartitionOf(other));
        Assert.NotEqual(ClientAddress.PartitionOf(one), ClientAddress.PartitionOf(elsewhere));
        Assert.Equal("2001:db8:1:2::1", ClientAddress.Text(one));   // the audit log keeps the whole address
    }

    [Fact]
    public void A_request_without_an_address_is_unknown()   // the test host has none
    {
        var context = With(null);

        Assert.Null(ClientAddress.Of(context));
        Assert.Equal("unknown", ClientAddress.Text(context));
        Assert.Equal("unknown", ClientAddress.PartitionOf(context));
        Assert.Equal("unknown", ClientAddress.PartitionOf((IPAddress?)null));
        Assert.Equal("unknown", ClientAddress.Unknown);
    }
}
```

`tests/Auth.IntegrationTests/ClientAddressSetupTests.cs` — the forwarded-headers middleware on its own, with no host (spec 0008 → Client address and trusted proxies: nothing trusted by default, every hop walked, `X-Forwarded-Host` never honoured, the framework's loopback default never used):

```csharp
using System.Net;
using Auth.Server.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class ClientAddressSetupTests
{
    private static readonly ProxySettings Network = new([System.Net.IPNetwork.Parse("10.250.0.0/24")], []);

    private sealed record Seen(string? Remote, string Scheme, string Host);

    private static async Task<Seen> RunAsync(
        ProxySettings proxies, string remote, string? forwardedFor = null, string? proto = null, string? forwardedHost = null)
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseClientAddress(proxies);
        Seen? seen = null;
        app.Run(context =>
        {
            seen = new Seen(context.Connection.RemoteIpAddress?.ToString(), context.Request.Scheme, context.Request.Host.Value ?? "");
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("auth.example");
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        if (proto is not null)
        {
            context.Request.Headers["X-Forwarded-Proto"] = proto;
        }

        if (forwardedHost is not null)
        {
            context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        }

        await app.Build()(context);
        return seen!;
    }

    [Fact]
    public async Task With_no_proxy_configured_a_forwarded_for_header_is_ignored()   // criterion 2
    {
        var seen = await RunAsync(ProxySettings.None, "203.0.113.9", forwardedFor: "1.1.1.1", proto: "https");

        Assert.Equal("203.0.113.9", seen.Remote);
        Assert.Equal("http", seen.Scheme);
    }

    [Fact]
    public async Task With_no_proxy_configured_the_framework_default_of_trusting_loopback_is_not_used()
    {
        var seen = await RunAsync(ProxySettings.None, "127.0.0.1", forwardedFor: "1.1.1.1");

        Assert.Equal("127.0.0.1", seen.Remote);
    }

    [Fact]
    public async Task A_configured_network_does_not_trust_loopback_as_the_framework_would()
    {
        var seen = await RunAsync(Network, "127.0.0.1", forwardedFor: "1.1.1.1");

        Assert.Equal("127.0.0.1", seen.Remote);
    }

    [Fact]
    public async Task From_a_trusted_proxy_the_client_is_the_last_address_that_is_not_a_proxy()   // criterion 2
    {
        var seen = await RunAsync(Network, "10.250.0.2", forwardedFor: "198.51.100.7");

        Assert.Equal("198.51.100.7", seen.Remote);
    }

    [Fact]
    public async Task An_address_the_client_wrote_into_the_header_is_not_the_client()   // the proxy appends the real one
    {
        var seen = await RunAsync(Network, "10.250.0.2", forwardedFor: "9.9.9.9, 198.51.100.7");

        Assert.Equal("198.51.100.7", seen.Remote);
    }

    [Fact]
    public async Task Every_hop_is_walked_not_just_the_first()
    {
        var seen = await RunAsync(Network, "10.250.0.2", forwardedFor: "9.9.9.9, 198.51.100.7, 10.250.0.9");

        Assert.Equal("198.51.100.7", seen.Remote);
    }

    [Fact]
    public async Task From_an_untrusted_address_the_headers_are_ignored()
    {
        var seen = await RunAsync(Network, "203.0.113.9", forwardedFor: "198.51.100.7", proto: "https");

        Assert.Equal("203.0.113.9", seen.Remote);
        Assert.Equal("http", seen.Scheme);
    }

    [Fact]
    public async Task The_scheme_is_taken_from_a_trusted_proxy_and_the_host_never_is()
    {
        var seen = await RunAsync(Network, "10.250.0.2", forwardedFor: "198.51.100.7", proto: "https", forwardedHost: "evil.example");

        Assert.Equal("https", seen.Scheme);
        Assert.Equal("auth.example", seen.Host);
    }

    [Fact]
    public async Task A_listed_proxy_is_trusted_also_when_kestrel_reports_it_mapped_into_ipv6()
    {
        var proxies = new ProxySettings([], [IPAddress.Parse("10.0.0.7")]);

        var seen = await RunAsync(proxies, "::ffff:10.0.0.7", forwardedFor: "198.51.100.7");

        Assert.Equal("198.51.100.7", seen.Remote);
    }

    [Fact]
    public void The_options_name_exactly_the_configured_proxies_and_walk_every_hop()
    {
        var options = ClientAddressSetup.OptionsFor(new ProxySettings([System.Net.IPNetwork.Parse("10.250.0.0/24")], [IPAddress.Parse("10.0.0.7")]));

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Null(options.ForwardLimit);
        Assert.Equal([IPAddress.Parse("10.0.0.7")], options.KnownProxies);
        Assert.Equal(["10.250.0.0/24"], options.KnownIPNetworks.Select(n => n.ToString()));
    }
}
```

`tests/Auth.IntegrationTests/SlidingWindowLimiterTests.cs`:

```csharp
using Auth.Server.RateLimiting;
using Microsoft.Extensions.Time.Testing;

namespace Auth.IntegrationTests;

public sealed class SlidingWindowLimiterTests
{
    // 2026-01-01T00:00:00Z: a multiple of ten seconds, so a segment starts exactly here.
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_767_225_600);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly SlidingWindowLimiter _limiter;

    public SlidingWindowLimiterTests() => _limiter = new SlidingWindowLimiter(_clock);

    private RateDecision Try(int limit, string partition = "a", RatePolicy policy = RatePolicy.Login) =>
        _limiter.TryAcquire(policy, partition, limit);

    [Fact]
    public void Exactly_the_limit_is_let_through_and_the_next_request_is_refused()   // criterion 1
    {
        for (var i = 0; i < 30; i++)
        {
            Assert.True(Try(30).Allowed, $"request {i + 1}");
        }

        var refused = Try(30);

        Assert.False(refused.Allowed);
        Assert.Equal(60, refused.RetryAfterSeconds);   // the first segment leaves the window 60 s after it began
    }

    [Fact]
    public void The_wait_shrinks_with_the_clock()
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.True(Try(3).Allowed);
        }

        _clock.Advance(TimeSpan.FromSeconds(25));

        Assert.Equal(35, Try(3).RetryAfterSeconds);
    }

    [Fact]
    public void The_wait_is_the_time_until_the_window_holds_fewer_requests_than_the_limit()
    {
        Assert.True(Try(3).Allowed);              // segment 0
        Assert.True(Try(3).Allowed);              // segment 0
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(Try(3).Allowed);              // segment 3

        _clock.Advance(TimeSpan.FromSeconds(1));
        var refused = Try(3);

        Assert.False(refused.Allowed);
        Assert.Equal(29, refused.RetryAfterSeconds);   // dropping segment 0 leaves one request: below 3, at +60 s

        _clock.Advance(TimeSpan.FromSeconds(29));
        Assert.True(Try(3).Allowed);
    }

    [Fact]
    public void The_wait_is_never_less_than_one_second()
    {
        for (var i = 0; i < 2; i++)
        {
            Assert.True(Try(2).Allowed);
        }

        _clock.Advance(TimeSpan.FromMilliseconds(59_999));

        Assert.Equal(1, Try(2).RetryAfterSeconds);
    }

    [Fact]
    public void A_refused_request_is_not_counted()   // criterion 1
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.True(Try(3).Allowed);
        }

        for (var i = 0; i < 100; i++)
        {
            Assert.False(Try(3).Allowed);
        }

        _clock.Advance(TimeSpan.FromSeconds(60));

        Assert.True(Try(3).Allowed);   // had the refusals counted, the window would still be full
        Assert.True(Try(3).Allowed);
        Assert.True(Try(3).Allowed);
        Assert.False(Try(3).Allowed);
    }

    [Fact]
    public void Requests_in_the_last_minute_count_and_older_ones_do_not()
    {
        Assert.True(Try(2).Allowed);
        _clock.Advance(TimeSpan.FromSeconds(50));
        Assert.True(Try(2).Allowed);
        Assert.False(Try(2).Allowed);   // both are inside the last minute

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(Try(2).Allowed);    // the first one has left
        Assert.False(Try(2).Allowed);
    }

    [Fact]
    public void Partitions_and_policies_are_counted_apart()
    {
        for (var i = 0; i < 3; i++)
        {
            Assert.True(Try(3, "a").Allowed);
        }

        Assert.False(Try(3, "a").Allowed);
        Assert.True(Try(3, "b").Allowed);
        Assert.True(Try(3, "a", RatePolicy.Refresh).Allowed);
    }

    [Fact]
    public void Idle_partitions_are_forgotten()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.True(Try(3, "partition-" + i).Allowed);
        }

        Assert.Equal(100, _limiter.PartitionCount);

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(Try(3, "newcomer").Allowed);

        Assert.Equal(1, _limiter.PartitionCount);
    }

    [Fact]
    public void A_partition_that_is_still_in_use_is_not_forgotten()
    {
        Assert.True(Try(3, "busy").Allowed);
        _clock.Advance(TimeSpan.FromSeconds(55));
        Assert.True(Try(3, "busy").Allowed);
        _clock.Advance(TimeSpan.FromSeconds(10));   // the sweep runs on this call, and the second request is still in the window

        Assert.True(Try(3, "busy").Allowed);
        Assert.True(Try(3, "busy").Allowed);
        Assert.False(Try(3, "busy").Allowed);
    }

    [Fact]
    public void Requests_that_arrive_together_cannot_pass_the_limit()
    {
        var allowed = 0;

        Parallel.For(0, 400, _ =>
        {
            if (Try(50).Allowed)
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(50, allowed);
    }
}
```

`tests/Auth.IntegrationTests/Infrastructure/RemoteAddressFilter.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Gives a request of the test host the client address its header names. The test server has no remote address of its own, and
/// the rate limiter and the audit log are about that address. Runs before the pipeline of the service, like the connection of
/// a real server would be.
/// </summary>
public sealed class RemoteAddressFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(AuthAppFactory.RemoteAddressHeader, out var value)
                    && IPAddress.TryParse(value.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await nextMiddleware();
            });
            next(app);
        };
    }
}
```

`tests/Auth.IntegrationTests/Infrastructure/RateLimitApi.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Sends requests as a client at a given address, and asserts the <c>429 too_many_requests</c> of spec 0008.</summary>
public static class RateLimitApi
{
    /// <summary>A request from <paramref name="remote"/>, optionally behind a proxy that says <paramref name="forwardedFor"/>.</summary>
    public static HttpRequestMessage Request(
        HttpMethod method, string path, string? remote = null, string? forwardedFor = null, string? body = "{}")
    {
        var request = new HttpRequestMessage(method, path);
        if (remote is not null)
        {
            request.Headers.Add(AuthAppFactory.RemoteAddressHeader, remote);
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        if (body is not null && method != HttpMethod.Get && method != HttpMethod.Head)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? remote = null, string? forwardedFor = null, string? body = "{}")
    {
        using var request = Request(method, path, remote, forwardedFor, body);
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Asserts the whole contract of the <c>429</c>: the exact body, <c>Retry-After</c> with the same number, <c>no-store</c> and no
    /// cookie. Returns <c>retry_after_seconds</c>.
    /// </summary>
    public static async Task<int> AssertTooManyRequestsAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.TooManyRequests, $"Expected 429, got {(int)response.StatusCode}: {raw}");

        using var body = JsonDocument.Parse(raw);
        var seconds = body.RootElement.GetProperty("retry_after_seconds").GetInt32();
        Assert.Equal($$"""{"error":"too_many_requests","retry_after_seconds":{{seconds}}}""", raw);
        Assert.True(seconds >= 1, $"retry_after_seconds must be at least 1, got {seconds}.");
        Assert.Equal(TimeSpan.FromSeconds(seconds), response.Headers.RetryAfter?.Delta);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected");
        Assert.Contains(response.Headers.Pragma, p => p.Name == "no-cache");
        Assert.False(response.Headers.Contains("Set-Cookie"), "A refused request must not write a cookie.");
        return seconds;
    }
}
```

`tests/Auth.IntegrationTests/RateLimitMiddlewareTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.IntegrationTests;

public sealed class RateLimitMiddlewareTests : SessionTestBase
{
    private const string Remote = "203.0.113.9";

    private readonly CountingPasswordHasher _hasher = new();

    public RateLimitMiddlewareTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithRemoteAddressHeader()
            .WithoutHostedService<MailDispatchService>()   // nothing may take the queued rows away while a test counts them
            .WithSetting(RateLimitSettings.EnabledKey, "true")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Login), "3")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Refresh), "3")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Email), "3")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Invite), "3")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.General), "5")
            .WithServices(services => services.Replace(ServiceDescriptor.Singleton<IPasswordHasher<ApplicationUser>>(_hasher)));
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? remote = Remote, string? forwardedFor = null) =>
        RateLimitApi.SendAsync(Client, method, path, remote, forwardedFor);

    /// <summary>A login of an address that nobody has, a new one each time: the lockout of spec 0003 must not answer in its place.</summary>
    private Task<HttpResponseMessage> UnknownLogin(int number, string? remote = Remote, string path = "/auth/login") =>
        RateLimitApi.SendAsync(Client, HttpMethod.Post, path, remote, null, $$"""{"email":"nobody-{{number}}@example.test","password":"Wrong-Password-1"}""");

    [Fact]
    public async Task The_request_over_the_limit_is_a_429_that_evaluates_no_password_and_changes_no_streak()   // criterion 1
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await UnknownLogin(i);
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        Assert.Equal(3, _hasher.VerifiedHashes.Count);

        using var refused = await UnknownLogin(4);

        await RateLimitApi.AssertTooManyRequestsAsync(refused);
        Assert.Equal(3, _hasher.VerifiedHashes.Count);   // no password was evaluated, not even against the decoy
        using var scope = Factory.Services.CreateScope();
        Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<AuthDbContext>().LoginStreaks.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_429_says_how_long_to_wait_on_the_clock()   // criterion 1
    {
        // The clock of SessionTestBase starts at the real time, in the middle of a ten-second segment: start on a segment boundary
        // so that the arithmetic below does not depend on when the test runs.
        Clock.Advance(TimeSpan.FromMilliseconds(10_000 - (Clock.GetUtcNow().ToUnixTimeMilliseconds() % 10_000)));
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await UnknownLogin(i);
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        Clock.Advance(TimeSpan.FromSeconds(25));
        using var refused = await UnknownLogin(4);

        Assert.Equal(35, await RateLimitApi.AssertTooManyRequestsAsync(refused));
    }

    [Fact]
    public async Task A_request_over_the_limit_is_not_counted_and_the_window_slides()
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await UnknownLogin(i);
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        for (var i = 4; i <= 12; i++)
        {
            using var refused = await UnknownLogin(i);
            await RateLimitApi.AssertTooManyRequestsAsync(refused);
        }

        Clock.Advance(TimeSpan.FromSeconds(60));
        using var again = await UnknownLogin(13);

        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/auth/login", 3)]
    [InlineData("POST", "/auth/refresh", 3)]
    [InlineData("POST", "/auth/password/forgot", 3)]
    [InlineData("POST", "/auth/password/reset", 3)]
    [InlineData("POST", "/auth/email/verify/request", 3)]
    [InlineData("POST", "/auth/email/verify", 3)]
    [InlineData("POST", "/auth/invites/preview", 3)]
    [InlineData("POST", "/auth/invites/accept", 3)]
    [InlineData("GET", "/auth/health", 5)]
    [InlineData("GET", "/auth/.well-known/jwks.json", 5)]
    [InlineData("POST", "/auth/logout", 5)]
    [InlineData("GET", "/auth/me", 5)]
    public async Task Each_policy_refuses_at_its_own_number(string method, string path, int limit)   // criterion 1
    {
        var http = new HttpMethod(method);
        for (var i = 1; i <= limit; i++)
        {
            using var answered = await Send(http, path);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, answered.StatusCode);
        }

        using var refused = await Send(http, path);

        await RateLimitApi.AssertTooManyRequestsAsync(refused);
    }

    [Fact]
    public async Task The_four_email_endpoints_share_one_limit_and_the_two_invitation_endpoints_another()
    {
        foreach (var path in new[] { "/auth/password/forgot", "/auth/password/reset", "/auth/email/verify/request" })
        {
            using var answered = await Send(HttpMethod.Post, path);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, answered.StatusCode);
        }

        using var fourth = await Send(HttpMethod.Post, "/auth/email/verify");
        await RateLimitApi.AssertTooManyRequestsAsync(fourth);

        foreach (var path in new[] { "/auth/invites/preview", "/auth/invites/accept", "/auth/invites/preview" })
        {
            using var answered = await Send(HttpMethod.Post, path);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, answered.StatusCode);   // another policy, its own count
        }
    }

    [Fact]
    public async Task A_request_refused_by_the_email_policy_queues_no_mail()
    {
        for (var i = 1; i <= 3; i++)
        {
            using var accepted = await RateLimitApi.SendAsync(
                Client, HttpMethod.Post, "/auth/password/forgot", Remote, null, $$"""{"email":"someone-{{i}}@example.test"}""");
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        Assert.Equal(3, await QueuedMailsAsync());   // a request queues one row, also for an address that has no account (spec 0004)

        using var refused = await RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/password/forgot", Remote, null, """{"email":"someone-4@example.test"}""");

        await RateLimitApi.AssertTooManyRequestsAsync(refused);
        Assert.Equal(3, await QueuedMailsAsync());   // the refused request queued nothing
    }

    private async Task<int> QueuedMailsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().MailRequests.CountAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_path_is_compared_without_regard_to_case_or_a_trailing_slash()
    {
        foreach (var path in new[] { "/auth/login", "/AUTH/Login", "/auth/login/" })
        {
            using var answered = await UnknownLogin(1, Remote, path);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, answered.StatusCode);
        }

        using var refused = await UnknownLogin(2, Remote, "/auth/LOGIN/");

        await RateLimitApi.AssertTooManyRequestsAsync(refused);
    }

    [Fact]
    public async Task A_request_counts_against_one_policy_only()
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await UnknownLogin(i);
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var refused = await UnknownLogin(4);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        using var refresh = await Send(HttpMethod.Post, "/auth/refresh");
        using var health = await Send(HttpMethod.Get, "/auth/health");
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Nothing_outside_auth_is_limited()
    {
        for (var i = 0; i < 20; i++)
        {
            using var answered = await Send(HttpMethod.Get, "/nope");
            Assert.Equal(HttpStatusCode.NotFound, answered.StatusCode);
        }
    }

    [Fact]
    public async Task Two_addresses_are_counted_apart()
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await UnknownLogin(i, "203.0.113.9");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var refused = await UnknownLogin(4, "203.0.113.9");
        using var other = await UnknownLogin(5, "203.0.113.10");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    [Fact]
    public async Task An_ipv4_address_mapped_into_ipv6_is_the_same_address()
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await UnknownLogin(i, "203.0.113.9");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var mapped = await UnknownLogin(4, "::ffff:203.0.113.9");

        Assert.Equal(HttpStatusCode.TooManyRequests, mapped.StatusCode);
    }

    [Fact]
    public async Task An_ipv6_address_is_counted_with_the_rest_of_its_64_bit_network()
    {
        using var first = await UnknownLogin(1, "2001:db8:1:2::1");
        using var second = await UnknownLogin(2, "2001:db8:1:2::2");
        using var third = await UnknownLogin(3, "2001:db8:1:2:aaaa:bbbb:cccc:dddd");
        using var fourth = await UnknownLogin(4, "2001:db8:1:2::3");
        using var elsewhere = await UnknownLogin(5, "2001:db8:1:3::1");

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, elsewhere.StatusCode);
    }

    [Fact]
    public async Task Without_a_trusted_proxy_a_forwarded_for_header_changes_nothing()   // criterion 2
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await RateLimitApi.SendAsync(
                Client, HttpMethod.Post, "/auth/login", Remote, "198.51.100." + i, $$"""{"email":"nobody-{{i}}@example.test","password":"Wrong-Password-1"}""");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var refused = await RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/login", Remote, "198.51.100.99", """{"email":"nobody-4@example.test","password":"Wrong-Password-1"}""");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }
}
```

`tests/Auth.IntegrationTests/RateLimitProxyTests.cs` (criterion 2, second half):

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Network;
using Auth.Server.RateLimiting;

namespace Auth.IntegrationTests;

public sealed class RateLimitProxyTests : SessionTestBase
{
    private const string Proxy = "10.250.0.2";

    public RateLimitProxyTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithRemoteAddressHeader()
            .WithSetting(RateLimitSettings.EnabledKey, "true")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Login), "3")
            .WithSetting(ProxySettings.KnownNetworksKey + ":0", "10.250.0.0/24");
    }

    private Task<HttpResponseMessage> LoginBehindProxy(int number, string forwardedFor, string remote = Proxy) =>
        RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/login", remote, forwardedFor, $$"""{"email":"nobody-{{number}}@example.test","password":"Wrong-Password-1"}""");

    [Fact]
    public async Task Two_clients_behind_the_trusted_proxy_are_counted_separately()   // criterion 2
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await LoginBehindProxy(i, "198.51.100.1");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var firstClient = await LoginBehindProxy(4, "198.51.100.1");
        using var secondClient = await LoginBehindProxy(5, "198.51.100.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, firstClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, secondClient.StatusCode);
    }

    [Fact]
    public async Task What_the_client_wrote_into_the_header_does_not_choose_its_partition()
    {
        // The proxy appends the address it saw: whatever comes before it was written by the client.
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await LoginBehindProxy(i, $"10.0.0.{i}, 198.51.100.1");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var refused = await LoginBehindProxy(4, "10.9.9.9, 198.51.100.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task From_an_address_that_is_not_the_proxy_the_header_is_ignored()   // criterion 2
    {
        for (var i = 1; i <= 3; i++)
        {
            using var answered = await LoginBehindProxy(i, $"198.51.100.{i}", remote: "203.0.113.9");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }

        using var refused = await LoginBehindProxy(4, "198.51.100.77", remote: "203.0.113.9");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }
}
```

`tests/Auth.IntegrationTests/RateLimitDefaultsTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.RateLimiting;

namespace Auth.IntegrationTests;

public sealed class RateLimitDefaultsTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public async Task The_test_host_has_the_limiter_off_so_that_no_other_test_meets_it()
    {
        for (var i = 1; i <= 40; i++)
        {
            using var answered = await LoginApi.Login(Client, $"nobody-{i}@example.test", "Wrong-Password-1");
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }
    }

    [Fact]
    public async Task A_host_with_a_permit_that_is_not_a_number_does_not_start_and_names_the_key()
    {
        await using var factory = new AuthAppFactory(Postgres, Keys).WithSetting(RateLimitSettings.PermitKey(RatePolicy.Login), "many");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(RateLimitSettings.PermitKey(RatePolicy.Login), ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_with_a_proxy_that_is_not_an_address_does_not_start_and_names_the_key()
    {
        await using var factory = new AuthAppFactory(Postgres, Keys).WithSetting("Auth:Proxy:KnownProxies:0", "proxy.internal");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Auth:Proxy:KnownProxies", ex.ToString(), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests and see them fail.** `"C:/Program Files/dotnet/dotnet.exe" build -warnaserror`.
  Expected: the build fails (CS0234 / CS0246: the namespaces `Auth.Server.Network` and `Auth.Server.RateLimiting` do not exist, `WithRemoteAddressHeader` and `AuthAppFactory.RemoteAddressHeader` are not defined).

- [ ] **Step 3: Implement.** The sources:

`src/Auth.Server/Network/ProxySettings.cs`:

```csharp
using System.Net;
using IPNetwork = System.Net.IPNetwork;

namespace Auth.Server.Network;

/// <summary>
/// Who may tell Auth-Core the client address (spec 0008 → Client address and trusted proxies): the networks and the single
/// addresses of the reverse proxies in front of it. Empty by default: nothing is trusted, forwarded headers are not read at all.
/// </summary>
public sealed class ProxySettings
{
    public const string KnownNetworksKey = "Auth:Proxy:KnownNetworks";
    public const string KnownProxiesKey = "Auth:Proxy:KnownProxies";

    public ProxySettings(IReadOnlyList<IPNetwork> networks, IReadOnlyList<IPAddress> proxies)
    {
        ArgumentNullException.ThrowIfNull(networks);
        ArgumentNullException.ThrowIfNull(proxies);

        Networks = networks;
        Proxies = proxies;
    }

    /// <summary>Nothing is trusted.</summary>
    public static ProxySettings None { get; } = new([], []);

    /// <summary>CIDR ranges whose addresses are proxies.</summary>
    public IReadOnlyList<IPNetwork> Networks { get; }

    /// <summary>Single proxy addresses; an IPv4-mapped IPv6 address is held as the IPv4 address.</summary>
    public IReadOnlyList<IPAddress> Proxies { get; }

    public bool IsEmpty => Networks.Count == 0 && Proxies.Count == 0;

    /// <summary>
    /// Reads both lists. Each key may hold one value with entries separated by commas, or a list (<c>Key:0</c>, <c>Key:1</c>);
    /// blank entries are ignored. A bad entry stops the host, naming the key and never echoing the value.
    /// </summary>
    /// <exception cref="InvalidOperationException">An entry is not a CIDR range written as its network (bits set beyond the prefix are refused), or not an address.</exception>
    public static ProxySettings Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var networks = new List<IPNetwork>();
        foreach (var entry in EntriesOf(configuration, KnownNetworksKey))
        {
            if (!IPNetwork.TryParse(entry, out var network) || !IsWrittenAsItsNetwork(entry, network))
            {
                throw new InvalidOperationException($"Configuration value '{KnownNetworksKey}' must be a list of CIDR ranges (an address, a slash and a prefix length).");
            }

            networks.Add(network);
        }

        var proxies = new List<IPAddress>();
        foreach (var entry in EntriesOf(configuration, KnownProxiesKey))
        {
            if (!IPAddress.TryParse(entry, out var address))
            {
                throw new InvalidOperationException($"Configuration value '{KnownProxiesKey}' must be a list of IP addresses.");
            }

            proxies.Add(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
        }

        return new ProxySettings(networks, proxies);
    }

    /// <summary>
    /// <c>IPNetwork.TryParse</c> accepts <c>10.250.0.5/24</c> and quietly masks it to <c>10.250.0.0/24</c>: a typing slip that would
    /// trust a wider network than the operator wrote. The address before the slash must be the network's own base address.
    /// </summary>
    private static bool IsWrittenAsItsNetwork(string entry, IPNetwork network)
    {
        var slash = entry.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && IPAddress.TryParse(entry.AsSpan(0, slash), out var written) && written.Equals(network.BaseAddress);
    }

    private static IEnumerable<string> EntriesOf(IConfiguration configuration, string key)
    {
        var values = new List<string?> { configuration[key] };
        values.AddRange(configuration.GetSection(key).GetChildren().Select(child => child.Value));
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }
}
```

`src/Auth.Server/Network/ClientAddress.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace Auth.Server.Network;

/// <summary>
/// The client address of a request (spec 0008): the connection's remote address, or, from a trusted proxy, the address the proxy
/// forwarded (see <see cref="ClientAddressSetup"/>, which has already replaced the connection's address by then). An IPv4 address
/// that reaches Kestrel mapped into IPv6 is the IPv4 address. The rate limiter partitions on it and the audit log records it.
/// </summary>
public static class ClientAddress
{
    /// <summary>The address of a request that has none (the test host).</summary>
    public const string Unknown = "unknown";

    public static IPAddress? Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var address = context.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    /// <summary>The whole address as text, or <see cref="Unknown"/>.</summary>
    public static string Text(HttpContext context) => Of(context)?.ToString() ?? Unknown;

    public static string PartitionOf(HttpContext context) => PartitionOf(Of(context));

    /// <summary>An IPv4 address as it is; an IPv6 address by its <c>/64</c> network, since one subscriber holds a whole <c>/64</c>.</summary>
    public static string PartitionOf(IPAddress? address)
    {
        if (address is null)
        {
            return Unknown;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out _))
        {
            return address.ToString();
        }

        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }
}
```

`src/Auth.Server/Network/ClientAddressSetup.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Auth.Server.Network;

public static class ClientAddressSetup
{
    /// <summary>
    /// The forwarded-headers options of spec 0008: <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> only (never the host), from
    /// the configured proxies only, every hop walked. The framework's own defaults (loopback trusted, one hop) are removed. Both
    /// lists must not be left empty: the middleware then trusts every sender, which is why <see cref="UseClientAddress"/> adds
    /// nothing when the settings are empty.
    /// </summary>
    public static ForwardedHeadersOptions OptionsFor(ProxySettings proxies)
    {
        ArgumentNullException.ThrowIfNull(proxies);

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = null,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var network in proxies.Networks)
        {
            options.KnownIPNetworks.Add(network);
        }

        foreach (var proxy in proxies.Proxies)
        {
            options.KnownProxies.Add(proxy);
        }

        return options;
    }

    /// <summary>Makes <see cref="ClientAddress"/> follow a trusted proxy. With no proxy configured a forwarded header is not read at all.</summary>
    public static IApplicationBuilder UseClientAddress(this IApplicationBuilder app, ProxySettings proxies)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(proxies);

        return proxies.IsEmpty ? app : app.UseForwardedHeaders(OptionsFor(proxies));
    }
}
```

`src/Auth.Server/RateLimiting/RatePolicy.cs`:

```csharp
namespace Auth.Server.RateLimiting;

/// <summary>The five limits of spec 0008 → Per-IP rate limiting. A request counts against exactly one.</summary>
public enum RatePolicy
{
    Login = 0,
    Refresh = 1,
    Email = 2,
    Invite = 3,
    General = 4,
}

public static class RatePolicies
{
    public static string NameOf(RatePolicy policy) => policy switch
    {
        RatePolicy.Login => "login",
        RatePolicy.Refresh => "refresh",
        RatePolicy.Email => "email",
        RatePolicy.Invite => "invite",
        RatePolicy.General => "general",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown policy."),
    };

    /// <summary>Requests per minute: 30, 60, 10, 20 and 300.</summary>
    public static int DefaultPermitPerMinute(RatePolicy policy) => policy switch
    {
        RatePolicy.Login => 30,
        RatePolicy.Refresh => 60,
        RatePolicy.Email => 10,
        RatePolicy.Invite => 20,
        RatePolicy.General => 300,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown policy."),
    };

    /// <summary>
    /// The policy of a request, chosen by method and path (the path compared without regard to case or one trailing slash), or
    /// <see langword="null"/> for a request outside <c>/auth/</c>. Only a <c>POST</c> to the five specific paths is not "general".
    /// </summary>
    public static RatePolicy? Classify(string method, PathString path)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (!path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!HttpMethods.IsPost(method))
        {
            return RatePolicy.General;
        }

        var text = path.Value ?? "";
        var trimmed = text.Length > 1 && text[^1] == '/' ? text[..^1] : text;
        if (trimmed.Equals("/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Login;
        }

        if (trimmed.Equals("/auth/refresh", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Refresh;
        }

        if (trimmed.Equals("/auth/password/forgot", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/password/reset", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/email/verify/request", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/email/verify", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Email;
        }

        if (trimmed.Equals("/auth/invites/preview", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/invites/accept", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Invite;
        }

        return RatePolicy.General;
    }
}
```

`src/Auth.Server/RateLimiting/RateLimitSettings.cs`:

```csharp
using System.Globalization;

namespace Auth.Server.RateLimiting;

/// <summary>
/// <c>Auth:RateLimit:Enabled</c> (default <c>true</c>) and <c>Auth:RateLimit:&lt;Policy&gt;:PermitPerMinute</c> (at least 1, the
/// numbers of the contract by default). Read as strings: .NET 10 throws a binding error for an empty value of a number or a
/// flag, and compose passes one for a variable that is not set; blank means the default.
/// </summary>
public sealed class RateLimitSettings
{
    public const string EnabledKey = "Auth:RateLimit:Enabled";

    private readonly int[] _permits;

    public RateLimitSettings(bool enabled, IReadOnlyList<int> permits)
    {
        ArgumentNullException.ThrowIfNull(permits);

        if (permits.Count != Enum.GetValues<RatePolicy>().Length)
        {
            throw new ArgumentException("One number for every policy is needed.", nameof(permits));
        }

        Enabled = enabled;
        _permits = [.. permits];
    }

    public bool Enabled { get; }

    public int PermitPerMinute(RatePolicy policy) => _permits[(int)policy];

    public static string PermitKey(RatePolicy policy) => $"Auth:RateLimit:{policy}:PermitPerMinute";

    /// <exception cref="InvalidOperationException">A value is not a whole number of at least 1 (or not true or false); the message names the key.</exception>
    public static RateLimitSettings Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = true;
        if (configuration[EnabledKey] is { } flag && !string.IsNullOrWhiteSpace(flag) && !bool.TryParse(flag.Trim(), out enabled))
        {
            throw new InvalidOperationException($"Configuration value '{EnabledKey}' must be 'true' or 'false'.");
        }

        var permits = new List<int>();
        foreach (var policy in Enum.GetValues<RatePolicy>())
        {
            var key = PermitKey(policy);
            if (configuration[key] is not { } text || string.IsNullOrWhiteSpace(text))
            {
                permits.Add(RatePolicies.DefaultPermitPerMinute(policy));
                continue;
            }

            if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var permit) || permit < 1)
            {
                throw new InvalidOperationException($"Configuration value '{key}' must be a whole number of at least 1.");
            }

            permits.Add(permit);
        }

        return new RateLimitSettings(enabled, permits);
    }
}
```

`src/Auth.Server/RateLimiting/SlidingWindowLimiter.cs`:

```csharp
using System.Collections.Concurrent;

namespace Auth.Server.RateLimiting;

public readonly record struct RateDecision(bool Allowed, int RetryAfterSeconds);

/// <summary>
/// The limiter of spec 0008 (Decision 13): per policy and partition, a sliding window of one minute in six segments of ten
/// seconds, counted on the injected clock. A request is let through when the six segments add up to less than the limit. A
/// refused request counts nothing. When one is refused, the answer says how many seconds pass before the window holds fewer
/// requests than the limit, at least 1. The counters live in memory (one instance per product, ADR 0001): a restart clears them.
/// Partitions that have been idle for a whole window are removed once a minute, so memory follows the last minute's traffic.
/// </summary>
public sealed class SlidingWindowLimiter(TimeProvider clock)
{
    public const int Segments = 6;
    private const long SegmentMilliseconds = 10_000;
    private const long SweepIntervalMilliseconds = 60_000;

    private readonly ConcurrentDictionary<(RatePolicy Policy, string Partition), Window> _windows = new();
    private long _lastSweep = clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>How many partitions are being counted (for the tests).</summary>
    public int PartitionCount => _windows.Count;

    public RateDecision TryAcquire(RatePolicy policy, string partition, int permitPerMinute)
    {
        ArgumentNullException.ThrowIfNull(partition);

        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        SweepIfDue(now);
        while (true)
        {
            var window = _windows.GetOrAdd((policy, partition), static _ => new Window());
            lock (window)
            {
                if (window.Removed)
                {
                    continue; // swept between the lookup and the lock: take a new one
                }

                return window.TryAcquire(now, permitPerMinute);
            }
        }
    }

    private void SweepIfDue(long now)
    {
        var last = Interlocked.Read(ref _lastSweep);
        if (now - last < SweepIntervalMilliseconds || Interlocked.CompareExchange(ref _lastSweep, now, last) != last)
        {
            return;
        }

        foreach (var (key, window) in _windows)
        {
            lock (window)
            {
                if (window.IsIdle(now))
                {
                    window.Removed = true;
                    _windows.TryRemove(new KeyValuePair<(RatePolicy Policy, string Partition), Window>(key, window));
                }
            }
        }
    }

    private sealed class Window
    {
        private readonly long[] _ids = new long[Segments];
        private readonly int[] _counts = new int[Segments];
        private long _newest = long.MinValue;

        public Window() => Array.Fill(_ids, long.MinValue);

        public bool Removed { get; set; }

        public bool IsIdle(long now) => _newest <= (now / SegmentMilliseconds) - Segments;

        public RateDecision TryAcquire(long now, int limit)
        {
            var current = now / SegmentMilliseconds;
            var total = 0;
            for (var i = 0; i < Segments; i++)
            {
                if (_ids[i] > current - Segments)
                {
                    total += _counts[i];
                }
            }

            if (total < limit)
            {
                var slot = (int)(current % Segments);
                if (_ids[slot] != current)
                {
                    _ids[slot] = current;
                    _counts[slot] = 0;
                }

                _counts[slot]++;
                _newest = current;
                return new RateDecision(true, 0);
            }

            // Refused. Segments leave the window oldest first; the answer is when enough of them have left.
            var remaining = total;
            for (var id = current - Segments + 1; id <= current; id++)
            {
                var slot = (int)(((id % Segments) + Segments) % Segments);
                if (_ids[slot] != id)
                {
                    continue;
                }

                remaining -= _counts[slot];
                if (remaining < limit)
                {
                    var leavesAt = (id + Segments) * SegmentMilliseconds;
                    return new RateDecision(false, (int)Math.Max(1, (leavesAt - now + 999) / 1000));
                }
            }

            return new RateDecision(false, Segments * (int)(SegmentMilliseconds / 1000)); // not reached for a limit of at least 1
        }
    }
}
```

`src/Auth.Server/RateLimiting/TooManyRequestsResult.cs`:

```csharp
using System.Globalization;
using Auth.Server.Tenancy;
using Microsoft.Net.Http.Headers;

namespace Auth.Server.RateLimiting;

/// <summary>
/// The answer of the per-IP limiter: <c>429</c>, <c>{"error":"too_many_requests","retry_after_seconds":n}</c>, <c>Retry-After: n</c>
/// and <c>Cache-Control: no-store</c>. It sets no cookie. (The lockout of spec 0003 answers <c>too_many_attempts</c>, the mail
/// limits too; this one is about the address.)
/// </summary>
public sealed class TooManyRequestsResult(int retryAfterSeconds) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var seconds = Math.Max(1, retryAfterSeconds);
        httpContext.Response.Headers[HeaderNames.CacheControl] = "no-store";
        httpContext.Response.Headers[HeaderNames.Pragma] = "no-cache";
        httpContext.Response.Headers[HeaderNames.RetryAfter] = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(new { error = TenancyErrors.TooManyRequests, retry_after_seconds = seconds }, statusCode: StatusCodes.Status429TooManyRequests)
            .ExecuteAsync(httpContext);
    }
}
```

`src/Auth.Server/RateLimiting/RateLimitMiddleware.cs`:

```csharp
using Auth.Server.Network;

namespace Auth.Server.RateLimiting;

/// <summary>
/// Counts every request under <c>/auth/</c> against the policy of its method and path, per client address, and answers
/// <c>429</c> over the limit without any other work: nothing downstream runs, so no password is evaluated and no lockout streak
/// changes. After the forwarded-headers middleware (the address is the real one) and before authentication (login is OpenIddict's,
/// inside it).
/// </summary>
public sealed class RateLimitMiddleware(RequestDelegate next, SlidingWindowLimiter limiter, RateLimitSettings settings)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (settings.Enabled && RatePolicies.Classify(context.Request.Method, context.Request.Path) is { } policy)
        {
            var decision = limiter.TryAcquire(policy, ClientAddress.PartitionOf(context), settings.PermitPerMinute(policy));
            if (!decision.Allowed)
            {
                await new TooManyRequestsResult(decision.RetryAfterSeconds).ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }
}
```

`src/Auth.Server/Tenancy/Outcome.cs` — the change in `TenancyErrors`:

```diff
     public const string TooManyAttempts = "too_many_attempts";
+    public const string TooManyRequests = "too_many_requests";
@@
-        TooManyAttempts => StatusCodes.Status429TooManyRequests,
+        TooManyAttempts or TooManyRequests => StatusCodes.Status429TooManyRequests,
```

`src/Auth.Server/Program.cs` — the changes (everything else stays):

```diff
 using Auth.Server.Login;
+using Auth.Server.Network;
+using Auth.Server.RateLimiting;
 using Auth.Server.Seeding;
@@
 builder.Services.AddSingleton(MailSettingsLoader.Load(builder.Configuration, builder.Environment.IsDevelopment()));
+// Fail fast on a bad proxy list or a bad rate limit, naming the key (spec 0008).
+var proxies = ProxySettings.Load(builder.Configuration);
+builder.Services.AddSingleton(proxies);
+builder.Services.AddSingleton(RateLimitSettings.Load(builder.Configuration));
+builder.Services.AddSingleton<SlidingWindowLimiter>();
@@
-// Before authentication, so that the 401 of a missing or invalid token is marked never to be stored too.
-app.UseNoStoreForTenancyPaths();
+// The client address first (the rate limiter and the audit log are about it), then the limiter: both before authentication,
+// because login is answered inside it. The no-store rule runs before authentication too, so that the 401 of a missing or
+// invalid token is marked never to be stored.
+app.UseClientAddress(proxies);
+app.UseMiddleware<RateLimitMiddleware>();
+app.UseNoStoreForTenancyPaths();
 app.UseAuthentication();
```

`tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs` — the changes:

```diff
 using Auth.Infrastructure.Identity;
 using Auth.Server.Email;
+using Auth.Server.Network;
+using Auth.Server.RateLimiting;
 using Auth.Server.Seeding;
@@
         _settings[DevUserSeeder.UnverifiedEmailKey] = "";
         _settings[DevUserSeeder.UnverifiedPasswordKey] = "";
+        // The per-IP limiter is off in a test host, so that no test meets it by accident (the test server sends everything from one
+        // address, "unknown"); a test about it turns it on with WithSetting. Blank proxy lists count as unset, so that a variable of
+        // the machine cannot make a test host trust a proxy.
+        _settings[RateLimitSettings.EnabledKey] = "false";
+        _settings[ProxySettings.KnownNetworksKey] = "";
+        _settings[ProxySettings.KnownProxiesKey] = "";
@@
     public AuthAppFactory WithClock(TimeProvider clock)
@@
+    /// <summary>The header that names the client address of a request in a host made with <see cref="WithRemoteAddressHeader"/>.</summary>
+    public const string RemoteAddressHeader = "X-Test-Remote-Address";
+
+    /// <summary>
+    /// Lets a request say, in <see cref="RemoteAddressHeader"/>, which address it comes from: the test server has none. Call it in
+    /// the constructor of the test, before the host is built.
+    /// </summary>
+    public AuthAppFactory WithRemoteAddressHeader() =>
+        WithServices(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, RemoteAddressFilter>());
+
```

- [ ] **Step 4: Run the new tests and see them pass.**

```bash
"C:/Program Files/dotnet/dotnet.exe" build -warnaserror
"C:/Program Files/dotnet/dotnet.exe" test --filter-class "Auth.IntegrationTests.ProxySettingsTests" --filter-class "Auth.IntegrationTests.RateLimitSettingsTests" --filter-class "Auth.IntegrationTests.RatePolicyTests" --filter-class "Auth.IntegrationTests.ClientAddressTests" --filter-class "Auth.IntegrationTests.ClientAddressSetupTests" --filter-class "Auth.IntegrationTests.SlidingWindowLimiterTests" --filter-class "Auth.IntegrationTests.RateLimitMiddlewareTests" --filter-class "Auth.IntegrationTests.RateLimitProxyTests" --filter-class "Auth.IntegrationTests.RateLimitDefaultsTests"
```

  Expected: the build is clean; every test passes (the pure tests run in milliseconds; the host tests need Docker).

- [ ] **Step 5: Run the whole suite.** `"C:/Program Files/dotnet/dotnet.exe" test`. Expected: PASS: nothing else changed, because the test host has the limiter off and no proxy.

- [ ] **Step 6: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(server): client address behind trusted proxies and a per-IP rate limiter`.

### Task 2: The outermost error handler, the security headers, the outage answer and the host settings

**Files:**
- Create: `src/Auth.Server/Api/ErrorHandling.cs`, `src/Auth.Server/Api/SecurityHeaders.cs`
- Delete: `src/Auth.Server/Api/NoStoreMiddleware.cs`
- Modify: `src/Auth.Server/Program.cs`, `src/Auth.Server/Sessions/SessionResponseHandler.cs`, `src/Auth.Server/Api/OpenApiSetup.cs`, `tests/Auth.IntegrationTests/Infrastructure/SessionTestBase.cs`
- Create (tests): `tests/Auth.IntegrationTests/Infrastructure/SecurityHeadersApi.cs`, `tests/Auth.IntegrationTests/Infrastructure/DatabaseOutage.cs`
- Test: `tests/Auth.IntegrationTests/SecurityHeadersTests.cs`, `SecurityHeadersOnRefusalTests.cs`, `ErrorHandlingTests.cs`, `TransientFailureTests.cs`, `RefreshOutageTests.cs`, `SessionResponseHandlerTests.cs`, `HostHardeningTests.cs`

**Interfaces:**
- Consumes: Task 1 (`ProxySettings`, the limiter, `RateLimitApi`, `AuthAppFactory.WithRemoteAddressHeader`), `RefreshRequestHandler.IsRefreshPath(PathString)` (internal), `OpenApiSetup.ReferencePath/JwksPath/DocumentName`.
- Produces (src):
  - `Auth.Server.Api.ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)`; `const string InternalError = "internal_error"`, `const string TemporarilyUnavailable = "temporarily_unavailable"`, `const int RefreshRetryAfterSeconds = 5`; `static bool IsTransientFailure(Exception exception)` (internal); extension `IApplicationBuilder UseErrorHandling(this IApplicationBuilder app)`.
  - `Auth.Server.Api.SecurityHeaders` (static): `const string ContentSecurityPolicy` (the strict policy), `static string ScalarPolicy(string? nonce)`, extension `IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)`.
  - `SessionResponseHandler` answers `503 {"error":"temporarily_unavailable"}` with `Retry-After: 5` to a refresh that OpenIddict reports as `server_error`.
- Produces (tests): `SecurityHeadersApi.AssertSecurityHeaders(HttpResponseMessage response, bool cacheable = false)` and `SecurityHeadersApi.HeaderValue(response, name)` (every later task asserts the headers with it); `DatabaseOutage(string connectionString)` with `ConnectionString`, `Cut()`, `Start()`, `DisposeAsync()`; `SessionTestBase.DisposeAsync` is now `virtual`.

**What the probe settled** (so that nothing below is a guess): with the database down a refresh answers `500` with an empty body. The first database read is the lookup of the refresh token inside `UseAuthentication`, before any endpoint of ours, and the exception chain is `InvalidOperationException` > `NpgsqlException` (`IsTransient` true) > `SocketException`; so a middleware around the whole pipeline sees it. Kestrel's own `500` for an unhandled exception drops every header set before it, so the handler has to write the answer itself. `HttpResponse.Clear()` drops the headers set so far, `Set-Cookie` included. In the test host the outage is made real by a small TCP forwarder in front of the test PostgreSQL (`DatabaseOutage`): cutting it closes every connection and refuses new ones, which gives the same exception chain; starting it again lets the very same cookie refresh.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/Infrastructure/SecurityHeadersApi.cs`:

```csharp
namespace Auth.IntegrationTests.Infrastructure;

/// <summary>The headers of spec 0008 → Security headers, asserted the same way by every test that sees an answer.</summary>
public static class SecurityHeadersApi
{
    public const string StrictPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>The single value of a header, looked for among the response's headers and its content's.</summary>
    public static string HeaderValue(HttpResponseMessage response, string name)
    {
        ArgumentNullException.ThrowIfNull(response);

        var found = response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values);
        Assert.True(found, $"The response has no {name} header.");
        return Assert.Single(values!);
    }

    /// <summary>
    /// Every header of the table, no <c>Server</c> and no <c>Strict-Transport-Security</c> (the proxy sends that one). An answer is
    /// <c>no-store</c> with <c>Pragma: no-cache</c>, except the key set and the OpenAPI document (<paramref name="cacheable"/>), which
    /// send no <c>Cache-Control</c> at all.
    /// </summary>
    public static void AssertSecurityHeaders(HttpResponseMessage response, bool cacheable = false)
    {
        ArgumentNullException.ThrowIfNull(response);

        Assert.Equal("nosniff", HeaderValue(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", HeaderValue(response, "X-Frame-Options"));
        Assert.Equal(StrictPolicy, HeaderValue(response, "Content-Security-Policy"));
        Assert.Equal("no-referrer", HeaderValue(response, "Referrer-Policy"));
        Assert.Equal("same-origin", HeaderValue(response, "Cross-Origin-Resource-Policy"));
        Assert.False(response.Headers.Contains("Server"), "No Server header is sent.");
        Assert.False(response.Headers.Contains("Strict-Transport-Security"), "TLS ends at the proxy, which sends HSTS.");
        if (cacheable)
        {
            Assert.False(response.Headers.Contains("Cache-Control"), "The key set and the OpenAPI document send no Cache-Control.");
            Assert.False(response.Headers.Contains("Pragma"));
        }
        else
        {
            Assert.Equal("no-store", HeaderValue(response, "Cache-Control"));
            Assert.Equal("no-cache", HeaderValue(response, "Pragma"));
        }
    }
}
```

`tests/Auth.IntegrationTests/Infrastructure/DatabaseOutage.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// A TCP forwarder between a host and the test PostgreSQL, to stop and start "the database" for one host without touching the
/// server the other tests share. <see cref="Cut"/> closes every connection through it and refuses new ones (what a stopped
/// server does); <see cref="Start"/> lets connections through again, on the same port. A host that is made to use
/// <see cref="ConnectionString"/> sees an outage and a recovery.
/// </summary>
public sealed class DatabaseOutage : IAsyncDisposable
{
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly object _gate = new();
    private readonly List<TcpClient> _connections = [];
    private TcpListener? _listener;

    public DatabaseOutage(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var upstream = new NpgsqlConnectionStringBuilder(connectionString);
        _upstreamHost = upstream.Host ?? "localhost";
        _upstreamPort = upstream.Port;

        // Take a free port once and keep it for every Start.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        upstream.Host = "127.0.0.1";
        upstream.Port = Port;
        ConnectionString = upstream.ConnectionString;
        Start();
    }

    public int Port { get; }

    /// <summary>The connection string of the upstream database, with the host and the port of the forwarder.</summary>
    public string ConnectionString { get; }

    public void Start()
    {
        lock (_gate)
        {
            if (_listener is not null)
            {
                return;
            }

            var listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            _listener = listener;
            _ = Task.Run(() => AcceptAsync(listener));
        }

        ClearPool();
    }

    public void Cut()
    {
        lock (_gate)
        {
            _listener?.Stop();
            _listener = null;
            foreach (var connection in _connections)
            {
                connection.Close();
            }

            _connections.Clear();
        }

        ClearPool();
    }

    /// <summary>
    /// The host's connection pool holds connections that were opened before the cut: they are dead sockets, and would be handed to the
    /// first requests after <see cref="Start"/> (or to the first after <see cref="Cut"/>, which then fail slowly instead of at once).
    /// Both ends empty the pool, so that every connection after either is a new one.
    /// </summary>
    private void ClearPool()
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    public ValueTask DisposeAsync()
    {
        Cut();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return; // stopped
            }

            var upstream = new TcpClient { NoDelay = true };
            try
            {
                await upstream.ConnectAsync(_upstreamHost, _upstreamPort);
            }
            catch (SocketException)
            {
                client.Close();
                upstream.Close();
                continue;
            }

            client.NoDelay = true;
            lock (_gate)
            {
                _connections.Add(client);
                _connections.Add(upstream);
            }

            _ = PumpAsync(client, upstream);
            _ = PumpAsync(upstream, client);
        }
    }

    private static async Task PumpAsync(TcpClient from, TcpClient to)
    {
        try
        {
            await from.GetStream().CopyToAsync(to.GetStream());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or SocketException)
        {
            // one side went away
        }
        finally
        {
            to.Close();
            from.Close();
        }
    }
}
```

`tests/Auth.IntegrationTests/SecurityHeadersTests.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class SecurityHeadersTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private static readonly Regex Nonce = new("'nonce-([^']+)'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData("GET", "/auth/health", HttpStatusCode.OK)]
    [InlineData("HEAD", "/auth/health", HttpStatusCode.OK)]
    [InlineData("GET", "/nope", HttpStatusCode.NotFound)]
    [InlineData("GET", "/auth/nope", HttpStatusCode.NotFound)]
    [InlineData("GET", "/auth/logout", HttpStatusCode.MethodNotAllowed)]
    [InlineData("POST", "/auth/login", HttpStatusCode.BadRequest)]
    [InlineData("POST", "/auth/refresh", HttpStatusCode.Unauthorized)]
    [InlineData("POST", "/auth/password/forgot", HttpStatusCode.BadRequest)]
    [InlineData("GET", "/auth/me", HttpStatusCode.Unauthorized)]
    [InlineData("GET", "/auth/org/members", HttpStatusCode.Unauthorized)]
    [InlineData("POST", "/auth/invites/preview", HttpStatusCode.BadRequest)]
    public async Task Every_answer_of_the_pipeline_has_the_headers_the_framework_404_and_405_included(string method, string path, HttpStatusCode status)   // criterion 4
    {
        using var response = await RateLimitApi.SendAsync(Client, new HttpMethod(method), path, body: null);

        Assert.Equal(status, response.StatusCode);
        SecurityHeadersApi.AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task The_key_set_and_the_openapi_document_have_every_header_but_no_cache_control()   // criterion 4
    {
        using var jwks = await Client.GetAsync("/auth/.well-known/jwks.json");
        using var document = await Client.GetAsync("/auth/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, jwks.StatusCode);
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        SecurityHeadersApi.AssertSecurityHeaders(jwks, cacheable: true);
        SecurityHeadersApi.AssertSecurityHeaders(document, cacheable: true);
    }

    [Fact]
    public async Task A_successful_login_has_the_headers_and_still_its_own_cookie()
    {
        using var response = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SecurityHeadersApi.AssertSecurityHeaders(response);
        Assert.True(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task The_interactive_reference_gets_a_policy_of_its_own_with_the_nonce_of_its_script()   // spec 0008 → Security headers
    {
        using var first = await Client.GetAsync("/auth/scalar/");
        var body = await first.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var policy = SecurityHeadersApi.HeaderValue(first, "Content-Security-Policy");
        var nonce = Nonce.Match(policy).Groups[1].Value;
        Assert.NotEmpty(nonce);
        Assert.Equal(
            $"default-src 'none'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'",
            policy);
        Assert.Contains(nonce, body, StringComparison.Ordinal);   // the one inline script carries it
        Assert.DoesNotContain("fonts.scalar.com", body, StringComparison.OrdinalIgnoreCase);   // the default fonts are off
        Assert.Equal("nosniff", SecurityHeadersApi.HeaderValue(first, "X-Content-Type-Options"));
        Assert.Equal("DENY", SecurityHeadersApi.HeaderValue(first, "X-Frame-Options"));
        Assert.Equal("no-referrer", SecurityHeadersApi.HeaderValue(first, "Referrer-Policy"));

        using var second = await Client.GetAsync("/auth/scalar/");
        var otherNonce = Nonce.Match(SecurityHeadersApi.HeaderValue(second, "Content-Security-Policy")).Groups[1].Value;
        Assert.NotEqual(nonce, otherNonce);   // new for every response
    }

    [Fact]
    public async Task The_policy_of_the_interactive_reference_is_for_its_own_path_only()
    {
        using var health = await Client.GetAsync("/auth/health");

        Assert.Equal(SecurityHeadersApi.StrictPolicy, SecurityHeadersApi.HeaderValue(health, "Content-Security-Policy"));
    }
}
```

`tests/Auth.IntegrationTests/SecurityHeadersOnRefusalTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.RateLimiting;

namespace Auth.IntegrationTests;

public sealed class SecurityHeadersOnRefusalTests : SessionTestBase
{
    public SecurityHeadersOnRefusalTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(RateLimitSettings.EnabledKey, "true").WithSetting(RateLimitSettings.PermitKey(RatePolicy.Login), "1");
    }

    [Fact]
    public async Task The_429_of_the_limiter_has_the_headers()   // criterion 4
    {
        using var first = await LoginApi.Login(Client, "nobody-1@example.test", "Wrong-Password-1");
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);

        using var refused = await LoginApi.Login(Client, "nobody-2@example.test", "Wrong-Password-1");

        await RateLimitApi.AssertTooManyRequestsAsync(refused);
        SecurityHeadersApi.AssertSecurityHeaders(refused);
    }
}
```

`tests/Auth.IntegrationTests/ErrorHandlingTests.cs` (the Production variant is a subclass that runs the same tests in that environment):

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

public class ErrorHandlingTests : TenancyTestBase
{
    private bool _armed;

    public ErrorHandlingTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // Once armed, the company API cannot read a membership: an exception from inside the endpoint, after authentication.
        Factory.WithServices(services => services.Replace(ServiceDescriptor.Scoped<MembershipReader>(provider =>
            _armed
                ? throw new InvalidOperationException("boom-for-the-test")
                : new MembershipReader(provider.GetRequiredService<AuthDbContext>(), provider.GetRequiredService<ManifestHolder>()))));
    }

    [Fact]
    public async Task An_unhandled_exception_is_a_500_internal_error_with_the_headers_and_is_logged()   // criterion 4
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        _armed = true;

        using var response = await TenancyApi.Get(Client, "/auth/me", token);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("""{"error":"internal_error"}""", raw);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        SecurityHeadersApi.AssertSecurityHeaders(response);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.DoesNotContain("boom-for-the-test", raw, StringComparison.Ordinal);   // nothing of the exception reaches the client
        Assert.Contains(
            Logs.Entries,
            e => e.Level == LogLevel.Error && e.Category.EndsWith("ErrorHandlingMiddleware", StringComparison.Ordinal)
                && e.Message.Contains("boom-for-the-test", StringComparison.Ordinal));   // the exception is logged
    }

    [Fact]
    public async Task The_refusals_of_the_service_are_not_taken_for_errors_by_the_handler()
    {
        _ = await CompanyWithAdminAsync();

        using var response = await SessionApi.Refresh(Client, "not-a-token");

        await SessionApi.AssertInvalidGrantAsync(response);   // still the uniform 401 of spec 0002, not a 500 or a 503
        SecurityHeadersApi.AssertSecurityHeaders(response);
    }
}

/// <summary>The same tests in Production, where the framework would otherwise show no developer page but a bare 500.</summary>
public sealed class ErrorHandlingProductionTests : ErrorHandlingTests
{
    public ErrorHandlingProductionTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithEnvironment("Production");
    }
}
```

`tests/Auth.IntegrationTests/TransientFailureTests.cs`:

```csharp
using System.Net.Sockets;
using Auth.Server.Api;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Auth.IntegrationTests;

public sealed class TransientFailureTests
{
    [Fact]
    public void A_transient_database_failure_a_timeout_and_a_socket_error_are_found_at_any_depth()
    {
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(new NpgsqlException("down", new SocketException())));
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(new NpgsqlException("down", new IOException("reset"))));
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(
            new InvalidOperationException("outer", new NpgsqlException("down", new IOException("reset", new SocketException())))));
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(new DbUpdateException("save", new NpgsqlException("down", new SocketException()))));
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(new TimeoutException()));
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(new SocketException()));
        Assert.True(ErrorHandlingMiddleware.IsTransientFailure(
            new AggregateException(new ArgumentException("a"), new InvalidOperationException("b", new TimeoutException()))));
    }

    [Fact]
    public void Anything_else_is_not_the_outage_of_a_dependency()
    {
        Assert.False(ErrorHandlingMiddleware.IsTransientFailure(new InvalidOperationException("x")));
        Assert.False(ErrorHandlingMiddleware.IsTransientFailure(new NpgsqlException("a failure that is not transient")));
        Assert.False(ErrorHandlingMiddleware.IsTransientFailure(new ArgumentNullException("parameter")));
        Assert.False(ErrorHandlingMiddleware.IsTransientFailure(new DbUpdateException("save", new InvalidOperationException("x"))));
        Assert.False(ErrorHandlingMiddleware.IsTransientFailure(new AggregateException(new ArgumentException("a"))));
    }
}
```

`tests/Auth.IntegrationTests/RefreshOutageTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class RefreshOutageTests : SessionTestBase
{
    private readonly DatabaseOutage _outage;

    public RefreshOutageTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        _outage = new DatabaseOutage(postgres.ConnectionStringFor(Factory.DatabaseName));
        Factory.WithSetting("ConnectionStrings:Auth", _outage.ConnectionString);
    }

    public override async ValueTask DisposeAsync()
    {
        await _outage.DisposeAsync();
        await base.DisposeAsync();
    }

    private static async Task AssertUnavailableAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"Expected 503, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("""{"error":"temporarily_unavailable"}""", raw);
        Assert.Equal(TimeSpan.FromSeconds(5), response.Headers.RetryAfter?.Delta);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        SecurityHeadersApi.AssertSecurityHeaders(response);
        Assert.False(response.Headers.Contains("Set-Cookie"), "The cookie is neither cleared nor rotated.");
    }

    private static async Task AssertInternalErrorAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.InternalServerError, $"Expected 500, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("""{"error":"internal_error"}""", raw);
        SecurityHeadersApi.AssertSecurityHeaders(response);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task A_refresh_while_the_database_is_down_is_a_503_and_the_same_cookie_refreshes_when_it_is_back()   // criterion 3
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        _outage.Cut();
        using (var during = await SessionApi.Refresh(Client, login.RefreshToken))
        {
            await AssertUnavailableAsync(during);
        }

        using (var stillDown = await SessionApi.Refresh(Client, login.RefreshToken))
        {
            await AssertUnavailableAsync(stillDown);
        }

        _outage.Start();
        var after = await SessionApi.RefreshOk(Client, login.RefreshToken);   // the same cookie: it was neither used up nor revoked
        await SessionApi.RefreshOk(Client, after.RefreshToken);               // and the chain goes on
    }

    [Fact]
    public async Task A_cookie_the_service_cannot_look_up_is_a_503_not_a_401_so_that_the_person_stays_signed_in()   // Decision 4
    {
        _ = await SessionApi.LoginAsync(Client, Factory);

        _outage.Cut();
        using var response = await SessionApi.Refresh(Client, "a-cookie-nobody-could-look-up");

        await AssertUnavailableAsync(response);
    }

    [Fact]
    public async Task No_cookie_is_still_a_401_as_before_for_no_database_is_needed_to_see_it()
    {
        _ = await SessionApi.LoginAsync(Client, Factory);

        _outage.Cut();
        using var response = await SessionApi.Refresh(Client, null);

        await SessionApi.AssertInvalidGrantAsync(response);
    }

    [Fact]
    public async Task Login_logout_and_the_company_api_keep_a_500_for_an_outage_now_written_by_the_handler()
    {
        var login = await SessionApi.LoginAsync(Client, Factory);

        _outage.Cut();
        using var signIn = await LoginApi.Login(Client, Factory.SeedEmail, Factory.SeedPassword);
        using var signOut = await SessionApi.Logout(Client, login.RefreshToken);
        using var company = await TenancyApi.Get(Client, "/auth/me", login.AccessToken);

        await AssertInternalErrorAsync(signIn);
        await AssertInternalErrorAsync(signOut);
        await AssertInternalErrorAsync(company);
    }

    [Fact]
    public async Task The_health_check_does_not_reach_the_database()
    {
        _outage.Cut();

        using var response = await Client.GetAsync("/auth/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

`tests/Auth.IntegrationTests/SessionResponseHandlerTests.cs`:

```csharp
using Auth.Server.Sessions;
using Microsoft.AspNetCore.Http;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Auth.IntegrationTests;

/// <summary>The error branch of the handler that writes the refresh answer, driven directly: OpenIddict hardly ever reports <c>server_error</c>.</summary>
public sealed class SessionResponseHandlerTests
{
    private sealed record Written(int Status, string Body, string RetryAfter, string SetCookie, bool Handled);

    private static async Task<Written> RunAsync(string path, string error)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();

        // OpenIddict keeps the request of its transaction behind a weak reference.
        var transaction = new OpenIddictServerTransaction();
        transaction.Properties[typeof(HttpRequest).FullName!] = new WeakReference<HttpRequest>(http.Request);
        var context = new OpenIddictServerEvents.ApplyTokenResponseContext(transaction) { Response = new OpenIddictResponse { Error = error } };

        await new SessionResponseHandler().HandleAsync(context);

        http.Response.Body.Position = 0;
        return new Written(
            http.Response.StatusCode,
            await new StreamReader(http.Response.Body).ReadToEndAsync(),
            http.Response.Headers.RetryAfter.ToString(),
            http.Response.Headers.SetCookie.ToString(),
            context.IsRequestHandled);
    }

    [Fact]
    public async Task A_server_error_on_refresh_is_a_503_temporarily_unavailable_with_retry_after_and_no_cookie()   // criterion 3
    {
        var written = await RunAsync("/auth/refresh", "server_error");

        Assert.Equal(503, written.Status);
        Assert.Equal("""{"error":"temporarily_unavailable"}""", written.Body);
        Assert.Equal("5", written.RetryAfter);
        Assert.Equal("", written.SetCookie);
        Assert.True(written.Handled);
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("access_denied")]
    [InlineData("unsupported_grant_type")]
    public async Task Every_other_refresh_error_stays_the_uniform_401_invalid_grant(string error)
    {
        var written = await RunAsync("/auth/refresh", error);

        Assert.Equal(401, written.Status);
        Assert.Equal("""{"error":"invalid_grant"}""", written.Body);
        Assert.Equal("", written.RetryAfter);
    }

    [Fact]
    public async Task A_malformed_refresh_request_is_left_to_openiddict_as_a_400()
    {
        var written = await RunAsync("/auth/refresh", "invalid_request");

        Assert.Equal(200, written.Status);   // untouched here: OpenIddict's own writer makes the 400
        Assert.Equal("", written.Body);
        Assert.False(written.Handled);
    }

    [Fact]
    public async Task A_server_error_on_login_is_left_alone()
    {
        var written = await RunAsync("/auth/login", "server_error");

        Assert.Equal(200, written.Status);
        Assert.Equal("", written.Body);
        Assert.False(written.Handled);
    }
}
```

`tests/Auth.IntegrationTests/HostHardeningTests.cs`:

```csharp
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Auth.IntegrationTests;

public sealed class HostHardeningTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public void Data_protection_keys_are_kept_in_memory_for_the_file_system_of_the_container_is_read_only()   // spec 0008 → Fixes
    {
        var provider = Factory.Services.GetRequiredService<IDataProtectionProvider>();

        Assert.Equal("EphemeralDataProtectionProvider", provider.GetType().Name);
    }

    [Fact]
    public void Kestrel_is_told_not_to_send_the_server_header()   // criterion 4: only a live stack shows the header itself (scripts/e2e-hardening.sh)
    {
        var options = Factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.False(options.AddServerHeader);
    }
}
```

`tests/Auth.IntegrationTests/Infrastructure/SessionTestBase.cs` — one word changes, so that a test can release what it holds before the host goes:

```diff
-    public async ValueTask DisposeAsync()
+    public virtual async ValueTask DisposeAsync()
     {
         Client.Dispose();
```

- [ ] **Step 2: Run the new tests and see them fail.** `"C:/Program Files/dotnet/dotnet.exe" build -warnaserror`.
  Expected: it fails (CS0117 `ErrorHandlingMiddleware` / `IsTransientFailure` not defined). After Step 3's code the tests are what fails or passes.

- [ ] **Step 3: Implement.**

`src/Auth.Server/Api/ErrorHandling.cs`:

```csharp
using System.Data.Common;
using System.Net.Sockets;
using Auth.Server.Sessions;
using Microsoft.Net.Http.Headers;

namespace Auth.Server.Api;

/// <summary>
/// The outermost handler (spec 0008 → Unhandled errors): whatever the pipeline throws is answered here, in every environment
/// (the developer exception page included, since this catches first) with <c>500 {"error":"internal_error"}</c>, and the exception
/// is logged. Kestrel's own <c>500</c> would drop every header set so far, the security headers with them. A refresh that fails
/// because the database cannot be reached (a transient <see cref="DbException"/>, a <see cref="TimeoutException"/> or a
/// <see cref="SocketException"/> at any depth) is <c>503 {"error":"temporarily_unavailable"}</c> with <c>Retry-After: 5</c> and no
/// cookie, so that the person stays signed in (Decision 4). A request the server itself rejected as malformed
/// (<see cref="BadHttpRequestException"/>) and one the client has abandoned are left to the server.
/// </summary>
public sealed partial class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public const string InternalError = "internal_error";
    public const string TemporarilyUnavailable = "temporarily_unavailable";
    public const int RefreshRetryAfterSeconds = 5;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away: nobody is left to answer.
        }
        catch (Exception exception) when (exception is not BadHttpRequestException)
        {
            if (context.Response.HasStarted)
            {
                // Too late to say anything: leave it to the server, which ends the connection.
                LogAfterStart(logger, exception);
                throw;
            }

            var outage = RefreshRequestHandler.IsRefreshPath(context.Request.Path) && IsTransientFailure(exception);
            if (outage)
            {
                LogOutage(logger, exception);
            }
            else
            {
                LogUnhandled(logger, exception);
            }

            // Drops what the request set so far: a Set-Cookie above all, so that the cookie is neither cleared nor rotated.
            context.Response.Clear();
            context.Response.Headers[HeaderNames.CacheControl] = "no-store";
            context.Response.Headers[HeaderNames.Pragma] = "no-cache";
            if (outage)
            {
                context.Response.Headers[HeaderNames.RetryAfter] = RefreshRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Results.Json(new { error = TemporarilyUnavailable }, statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
            }
            else
            {
                await Results.Json(new { error = InternalError }, statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
            }
        }
    }

    /// <summary>
    /// Whether the exception, or anything it wraps (an <see cref="AggregateException"/> included), is a transient
    /// <see cref="DbException"/>, a <see cref="TimeoutException"/> or a <see cref="SocketException"/>: a dependency that is out, and
    /// not a fault of the request or of our code.
    /// </summary>
    internal static bool IsTransientFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is DbException { IsTransient: true } or TimeoutException or SocketException)
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } wrapped)
            {
                pending.Push(wrapped);
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception; answering 500 internal_error.")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A dependency could not be reached while refreshing a session; answering 503 temporarily_unavailable.")]
    private static partial void LogOutage(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception after the response had started; the connection is ended.")]
    private static partial void LogAfterStart(ILogger logger, Exception exception);
}

public static class ErrorHandling
{
    /// <summary>Adds <see cref="ErrorHandlingMiddleware"/>. It must be the first middleware.</summary>
    public static IApplicationBuilder UseErrorHandling(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<ErrorHandlingMiddleware>();
    }
}
```

`src/Auth.Server/Api/SecurityHeaders.cs`:

```csharp
using Scalar.AspNetCore;

namespace Auth.Server.Api;

/// <summary>
/// The headers of spec 0008 → Security headers, on every response of the pipeline whatever its status (so also the framework's
/// <c>404</c> and <c>405</c>), set when the response starts. This replaces the no-store middleware of spec 0005. The interactive
/// reference (Development only) gets a policy of its own with the nonce of its one inline script. No
/// <c>Strict-Transport-Security</c>: TLS ends at the proxy, which sends it. The key set and the OpenAPI document send no
/// <c>Cache-Control</c> and get none.
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    private static readonly string OpenApiDocumentPath = $"/auth/openapi/{OpenApiSetup.DocumentName}.json";

    /// <summary>The policy for everything under the interactive reference. Without a nonce the script is not allowed to run.</summary>
    public static string ScalarPolicy(string? nonce) =>
        "default-src 'none'; script-src 'self'" + (nonce is null ? "" : $" 'nonce-{nonce}'")
        + "; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use((context, next) =>
        {
            context.Response.OnStarting(static state => Apply((HttpContext)state), context);
            return next(context);
        });
    }

    private static Task Apply(HttpContext context)
    {
        var headers = context.Response.Headers;
        var path = context.Request.Path;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Content-Security-Policy"] = path.StartsWithSegments(OpenApiSetup.ReferencePath, StringComparison.OrdinalIgnoreCase)
            ? ScalarPolicy(context.Items.TryGetValue(ScalarOptions.NonceHttpContextItemKey, out var nonce) ? nonce as string : null)
            : ContentSecurityPolicy;

        var cacheable = path.Equals(OpenApiSetup.JwksPath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(OpenApiDocumentPath, StringComparison.OrdinalIgnoreCase);
        if (!cacheable)
        {
            headers["Cache-Control"] = "no-store";
            headers["Pragma"] = "no-cache";
        }

        return Task.CompletedTask;
    }
}
```

`src/Auth.Server/Api/NoStoreMiddleware.cs` — delete the file (`git rm` is the orchestrator's; the implementer deletes it from the tree).

`src/Auth.Server/Sessions/SessionResponseHandler.cs` — the change in the error branch (the rest of the file stays):

```diff
+using Auth.Server.Api;
 using Auth.Server.Login;
@@
         if (!string.IsNullOrEmpty(context.Response.Error))
         {
-            // invalid_request is the wrong-method case: a malformed request, answered like login's 400. Every other
-            // error on this path is a refresh failure and gets the one uniform answer (spec 0002, criterion 7).
-            if (isRefresh && !string.Equals(context.Response.Error, Errors.InvalidRequest, StringComparison.Ordinal))
+            // A refresh that failed for a reason that is not the client's (OpenIddict says server_error) is a 503, and the person
+            // stays signed in: the cookie is neither cleared nor rotated (spec 0008 → POST /auth/refresh during an outage).
+            if (isRefresh && string.Equals(context.Response.Error, Errors.ServerError, StringComparison.Ordinal))
+            {
+                http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
+                http.Response.Headers.RetryAfter = ErrorHandlingMiddleware.RefreshRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
+                await WriteCompactAsync(http.Response, new OpenIddictResponse { Error = ErrorHandlingMiddleware.TemporarilyUnavailable }, context);
+            }
+            // invalid_request is the wrong-method case: a malformed request, answered like login's 400. Every other
+            // error on this path is a refresh failure and gets the one uniform answer (spec 0002, criterion 7).
+            else if (isRefresh && !string.Equals(context.Response.Error, Errors.InvalidRequest, StringComparison.Ordinal))
             {
                 http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                 await WriteCompactAsync(http.Response, new OpenIddictResponse { Error = Errors.InvalidGrant }, context);
```

`src/Auth.Server/Api/OpenApiSetup.cs` — the interactive reference with its nonce and without what it fetches from elsewhere:

```diff
-            app.MapScalarApiReference(ReferencePath, options => options.WithOpenApiRoutePattern(DocumentPath));
+            // The bundle is served from the same origin. The one inline script carries a nonce (SecurityHeaders puts it into the
+            // policy); the default fonts, the telemetry and the agent would reach other hosts, which the policy forbids.
+            app.MapScalarApiReference(
+                ReferencePath,
+                options => options.WithOpenApiRoutePattern(DocumentPath).WithNonce().DisableDefaultFonts().DisableTelemetry().DisableAgent());
```

`src/Auth.Server/Program.cs` — the changes:

```diff
 builder.Services.AddHealthChecks().AddCheck<ManifestHealthCheck>("manifest");
+// Nothing of Auth-Core uses Data Protection, and the production container's file system is read-only: keys stay in memory.
+builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
@@
 var builder = WebApplication.CreateBuilder(args);
+// No Server header (spec 0008). Only a live stack shows it: the test server never sends one.
+builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
@@
-// The client address first (the rate limiter and the audit log are about it), then the limiter: both before authentication,
-// because login is answered inside it. The no-store rule runs before authentication too, so that the 401 of a missing or
-// invalid token is marked never to be stored.
+// The outermost handler and the headers come first: whatever the pipeline answers, a 404, a 429 or an exception included, has
+// them. Then the client address (the rate limiter and the audit log are about it) and the limiter: all before authentication,
+// because login is answered inside it.
+app.UseErrorHandling();
+app.UseSecurityHeaders();
 app.UseClientAddress(proxies);
 app.UseMiddleware<RateLimitMiddleware>();
-app.UseNoStoreForTenancyPaths();
 app.UseAuthentication();
```

(`using Microsoft.AspNetCore.DataProtection;` is needed at the top of `Program.cs` for `UseEphemeralDataProtectionProvider`.)

- [ ] **Step 4: Run the new tests, then the whole suite.**

```bash
"C:/Program Files/dotnet/dotnet.exe" build -warnaserror
"C:/Program Files/dotnet/dotnet.exe" test --filter-class "Auth.IntegrationTests.SecurityHeadersTests" --filter-class "Auth.IntegrationTests.SecurityHeadersOnRefusalTests" --filter-class "Auth.IntegrationTests.ErrorHandlingTests" --filter-class "Auth.IntegrationTests.ErrorHandlingProductionTests" --filter-class "Auth.IntegrationTests.TransientFailureTests" --filter-class "Auth.IntegrationTests.RefreshOutageTests" --filter-class "Auth.IntegrationTests.SessionResponseHandlerTests" --filter-class "Auth.IntegrationTests.HostHardeningTests"
"C:/Program Files/dotnet/dotnet.exe" test
```

  Expected: PASS. If an old test failed because it expected the framework's `404`/`405` to lack `Cache-Control`, or `Server`-less details that the new middleware now changes, fix the test to the new contract (every answer is `no-store` now) and say so in the hand-back; no other test should need a change.

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files, with `NoStoreMiddleware.cs` deleted. Proposed subject: `feat(server): outermost error handler, security headers and a 503 for a refresh during an outage`.

### Task 3: The small fixes: health methods, the JSON charset, invitation domains, the second seed user

**Files:**
- Create: `src/Auth.Server/Api/JsonCharsetGuard.cs`
- Modify: `src/Auth.Server/Api/AccountEndpoints.cs`, `src/Auth.Server/Tenancy/Outcome.cs` (`TenancyErrors`), `src/Auth.Server/Requests/EmailInput.cs`, `src/Auth.Server/Seeding/DevUserSeeder.cs`, `src/Auth.Server/Program.cs`
- Modify (tests): `tests/Auth.IntegrationTests/OpenApiTests.cs`, `tests/Auth.IntegrationTests/DevCompanySeedTests.cs`
- Test: `tests/Auth.IntegrationTests/HealthMethodsTests.cs`, `JsonCharsetTests.cs`, `InvitationDomainTests.cs`, `DevCompanySeedTests.cs`

**Interfaces:**
- Consumes: Task 1 (`RateLimitMiddleware` is in the pipeline), Task 2 (`SecurityHeadersApi`, `UseSecurityHeaders`), `JsonObjectBody.IsJson`, `ApiResults.Error(string)`, `RefreshRequestHandler.IsRefreshPath`.
- Produces:
  - `TenancyErrors.UnsupportedMediaType = "unsupported_media_type"` (status `415` in `StatusOf`).
  - `Auth.Server.Api.JsonCharsetGuard` (static) with `bool Applies(HttpRequest request)` and `bool IsRefused(string? contentType)` (both internal), and the extension `IApplicationBuilder UseJsonCharsetGuard(this IApplicationBuilder app)`.
  - `EmailInput.IsInvitable` also refuses a domain whose last label is not `xn--` plus more, or two or more letters (see below), in the API and in the CLI.
  - The second development seed user takes the first role of the development company, ordered by name (ordinal), that holds neither `members:manage` nor `*`, read from the database.

**What the spec says about the second seed user** (as amended by the owner on 2026-02-19): the first role of the development company, **by name in ordinal order**, that holds neither `members:manage` nor `*`, read from the roles stored in the database and not from the manifest. (The order in which the roles were stored cannot be read back: the role ids are version-7 UUIDs that are random inside one millisecond, and EF Core inserts the roles of one company in id order; probed with 300 roles in one `SaveChanges`.) The existing test of the manifest's order is replaced by tests of the name order.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/HealthMethodsTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class HealthMethodsTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public async Task Get_answers_200_and_head_answers_200()   // criterion 5
    {
        using var get = await Client.GetAsync("/auth/health");
        using var head = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/auth/health"));

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("Healthy", await get.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        // No assertion on the body of the HEAD answer: the in-memory test server hands the health check's body ("Healthy") back on a
        // HEAD request, which Kestrel never sends. `scripts/e2e-hardening.sh` step 1 asserts the empty body against Kestrel.
        SecurityHeadersApi.AssertSecurityHeaders(head);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("OPTIONS")]
    public async Task Any_other_method_is_405_with_the_two_that_are_allowed(string method)   // criterion 5
    {
        using var response = await Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/auth/health"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("GET, HEAD", string.Join(", ", response.Content.Headers.Allow));
        SecurityHeadersApi.AssertSecurityHeaders(response);
    }
}
```

`tests/Auth.IntegrationTests/JsonCharsetTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

public sealed class JsonCharsetTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private const string Utf16 = "application/json; charset=utf-16";
    private const string SomeId = "11111111-1111-1111-1111-111111111111";

    private Task<HttpResponseMessage> SendAsync(string method, string path, string contentType, string body = "{}")
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent(body, Encoding.UTF8) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return Client.SendAsync(request);
    }

    private static async Task AssertUnsupportedAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnsupportedMediaType, $"Expected 415, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("""{"error":"unsupported_media_type"}""", raw);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        SecurityHeadersApi.AssertSecurityHeaders(response);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("POST", "/auth/login")]
    [InlineData("POST", "/auth/password/forgot")]
    [InlineData("POST", "/auth/email/verify/request")]
    [InlineData("POST", "/auth/password/reset")]
    [InlineData("POST", "/auth/email/verify")]
    [InlineData("POST", "/auth/invites/preview")]
    [InlineData("POST", "/auth/invites/accept")]
    [InlineData("PATCH", "/auth/org")]
    [InlineData("POST", "/auth/org/invites")]
    [InlineData("PUT", "/auth/org/members/" + SomeId + "/role")]
    [InlineData("POST", "/auth/org/roles")]
    [InlineData("PUT", "/auth/org/roles/" + SomeId)]
    [InlineData("DELETE", "/auth/org/roles/" + SomeId)]
    public async Task A_body_declared_utf_16_is_a_415_on_every_endpoint_that_reads_one_and_before_authentication(string method, string path)   // criterion 6
    {
        using var response = await SendAsync(method, path, Utf16);   // no token: the 415 comes before the 401

        await AssertUnsupportedAsync(response);
    }

    [Theory]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/json; charset=UTF-16")]
    [InlineData("APPLICATION/JSON;charset=utf-16")]
    [InlineData("application/json; charset=\"utf-16\"")]
    [InlineData("application/json; charset=iso-8859-1")]
    [InlineData("application/json; charset=utf8")]   // not the spelling the contract allows
    [InlineData("application/json; charset=utf-32")]
    public async Task Any_charset_other_than_utf_8_is_refused_in_any_spelling(string contentType)   // criterion 6
    {
        using var response = await SendAsync("POST", "/auth/login", contentType);

        await AssertUnsupportedAsync(response);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json; charset=UTF-8")]
    [InlineData("application/json; charset=\"utf-8\"")]
    public async Task A_body_with_utf_8_or_no_charset_is_read_as_before(string contentType)   // criterion 6
    {
        using var response = await SendAsync(
            "POST", "/auth/login", contentType, $$"""{"email":"{{Factory.SeedEmail}}","password":"{{Factory.SeedPassword}}"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("application/json; charset=utf-8", false)]
    [InlineData("application/json; charset=UTF-8", false)]
    [InlineData("application/json; charset=\"utf-8\"", false)]     // quoted: the parsed value keeps its quotes, the guard must not
    [InlineData("application/json; charset=\"UTF-8\"", false)]
    [InlineData("application/json; charset=\"utf-16\"", true)]
    [InlineData("application/json; charset=utf-16", true)]
    [InlineData("application/json; charset=\"\"", true)]            // an empty charset is not UTF-8 either
    [InlineData("application/json", false)]
    [InlineData("text/plain; charset=utf-16", false)]                // not JSON: the handler's own 400
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("not a content type", false)]
    public void The_guard_reads_the_charset_without_its_quotes(string? contentType, bool refused)
    {
        Assert.Equal(refused, JsonCharsetGuard.IsRefused(contentType));
    }

    [Fact]
    public async Task A_body_that_is_not_json_at_all_keeps_its_own_answer()
    {
        using var response = await SendAsync("POST", "/auth/password/forgot", "text/plain; charset=utf-16");

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);   // the handler's 400, not a 415
    }

    [Fact]
    public async Task Refresh_and_logout_read_no_body_and_are_not_checked()
    {
        using var refresh = await SendAsync("POST", "/auth/refresh", Utf16);
        using var logout = await SendAsync("POST", "/auth/logout", Utf16);

        await SessionApi.AssertInvalidGrantAsync(refresh);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
    }

    [Fact]
    public async Task A_get_with_such_a_header_is_left_alone()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/health") { Content = new StringContent("", Encoding.UTF8) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(Utf16);

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/auth/refresh", false)]
    [InlineData("POST", "/auth/refresh/", false)]
    [InlineData("POST", "/AUTH/Logout", false)]
    [InlineData("POST", "/auth/logout/", false)]
    [InlineData("GET", "/auth/login", false)]
    [InlineData("HEAD", "/auth/health", false)]
    [InlineData("POST", "/api/notes", false)]
    [InlineData("POST", "/auth/login", true)]
    [InlineData("POST", "/AUTH/LOGIN/", true)]
    [InlineData("PUT", "/auth/org/roles/x", true)]
    [InlineData("PATCH", "/auth/org", true)]
    [InlineData("DELETE", "/auth/org/members/x", true)]
    public void The_guard_applies_to_the_four_methods_under_auth_except_refresh_and_logout(string method, string path, bool applies)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(applies, JsonCharsetGuard.Applies(context.Request));
    }
}
```

`tests/Auth.IntegrationTests/InvitationDomainTests.cs`:

```csharp
using System.Globalization;
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests;

public sealed class InvitationDomainTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    [Theory]
    [InlineData("x@example.pl")]
    [InlineData("x@żółw.pl")]
    [InlineData("x@пример.рф")]
    [InlineData("x@xn--e1afmkfd.xn--p1ai")]
    [InlineData("x@xn--w-uga1v8h.pl")]
    [InlineData("joe@mail.acme.co.uk")]
    [InlineData("joe@1and1.example")]
    [InlineData("JOE@ACME.TEST")]
    public void A_domain_that_ends_in_letters_or_in_xn_and_more_passes_the_domain_rule(string email)   // criterion 7
    {
        Assert.True(EmailInput.IsInvitable(email));
    }

    [Theory]
    [InlineData("x@127.0x1")]
    [InlineData("x@host.123")]
    [InlineData("x@0x7f.1")]
    [InlineData("x@0x7f.0x0.0x0.0x1")]
    [InlineData("x@10.0.0.5")]
    [InlineData("x@1.2.3.4a1")]
    [InlineData("x@example.c0m")]
    [InlineData("x@example.c")]
    [InlineData("x@example.xn--")]
    [InlineData("x@example.xn--@")]
    [InlineData("x@example.1")]
    public void A_domain_whose_last_label_is_not_letters_or_xn_and_more_is_refused(string email)   // criterion 7
    {
        Assert.False(EmailInput.IsInvitable(email));
    }

    private async Task<HttpResponseMessage> InviteAsync(string token, string email, Guid role) =>
        await TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", token, new { email, role_id = role });

    [Theory]
    [InlineData("x@127.0x1")]
    [InlineData("x@host.123")]
    [InlineData("x@0x7f.1")]
    public async Task The_api_answers_400_invalid_request_for_such_a_domain(string email)   // criterion 7
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        using var response = await InviteAsync(token, email, await RoleIdAsync(company, "user"));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Empty(await InDbAsync(db => Task.FromResult(db.Invites.ToList())));
    }

    [Theory]
    [InlineData("x@example.pl")]
    [InlineData("x@żółw.pl")]
    [InlineData("x@xn--e1afmkfd.xn--p1ai")]
    public async Task The_api_takes_the_domains_that_pass(string email)   // criterion 7
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        using var response = await InviteAsync(token, email, await RoleIdAsync(company, "user"));

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
    }

    private async Task<(int Exit, string Error)> InviteByCliAsync(Guid company, string email)
    {
        _ = Factory.Services;
        var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await AdminCli.RunAsync(
            ["invite", "--org", company.ToString(), "--email", email, "--role", "user"],
            new StringWriter(CultureInfo.InvariantCulture),
            error,
            builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Auth"] = Factory.ConnectionString,
                [ManifestSettings.PathKey] = Factory.ManifestPath,
            }),
            TestContext.Current.CancellationToken);
        return (exit, error.ToString());
    }

    [Theory]
    [InlineData("x@127.0x1")]
    [InlineData("x@host.123")]
    [InlineData("x@0x7f.1")]
    public async Task The_cli_refuses_such_a_domain_too(string email)   // criterion 7
    {
        var company = await CreateCompanyAsync();

        var (exit, error) = await InviteByCliAsync(company, email);

        Assert.Equal(AdminCli.Refused, exit);
        Assert.Contains("error: invalid_request", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cli_takes_a_domain_that_passes()   // criterion 7
    {
        var company = await CreateCompanyAsync();

        var (exit, error) = await InviteByCliAsync(company, "x@example.pl");

        Assert.True(exit == AdminCli.Done, error);
    }
}
```

`tests/Auth.IntegrationTests/DevCompanySeedTests.cs` — the existing test of the manifest's order is replaced, and two are added:

```csharp
    [Fact]
    public async Task Second_seed_user_gets_the_first_role_by_name_that_holds_neither_members_manage_nor_star()   // spec 0008 → Fixes
    {
        await using var factory = WithSecondUser(Factory())
            .WithManifest("permissions: [a:b]\ndefault_roles:\n  owner: [\"*\"]\n  zeta: [a:b]\n  alpha: [a:b]\n");
        _ = factory.Services;

        // The roles are read from the database and ordered by name, not taken in the order of the file: alpha, not zeta.
        Assert.Equal(("Development", "alpha"), await MembershipOfAsync(factory, UnverifiedEmail));
        Assert.Equal(("Development", "owner"), await MembershipOfAsync(factory, factory.SeedEmail));
    }

    [Fact]
    public async Task Second_seed_user_takes_a_role_the_company_has_not_one_the_manifest_names()   // read from the database, not from the manifest
    {
        var database = UniqueDatabase();
        await using var first = Factory(database);
        _ = first.Services;   // the company is made with admin and user

        await using var second = WithSecondUser(Factory(database))
            .WithManifest("permissions: [a:b]\ndefault_roles:\n  boss: [\"*\"]\n  zeta: [a:b]\n");
        _ = second.Services;

        Assert.Equal(("Development", "user"), await MembershipOfAsync(second, UnverifiedEmail));   // zeta is only in the manifest
    }

    [Fact]
    public async Task A_role_that_holds_members_manage_or_star_is_never_the_second_users()
    {
        await using var factory = WithSecondUser(Factory())
            .WithManifest("permissions: [a:b]\ndefault_roles:\n  aaa: [members:manage]\n  bbb: [\"*\"]\n  ccc: [a:b]\n");
        _ = factory.Services;

        Assert.Equal(("Development", "ccc"), await MembershipOfAsync(factory, UnverifiedEmail));
    }
```

(The test `Second_seed_user_gets_the_first_default_role_in_the_order_of_the_manifest_that_does_not_manage_members` is removed: it asserts the opposite of the contract.)

`tests/Auth.IntegrationTests/OpenApiTests.cs` — `GET /auth/health` now answers `GET` and `HEAD`, and the description names the `GET`, which says it for the `HEAD` too. The test that every route is described skips `HEAD`:

```diff
-            var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
+            // HEAD answers wherever GET does (the health check): the description says it once, under GET.
+            var methods = (route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Where(m => m != "HEAD");
```

- [ ] **Step 2: Run the new tests and see them fail.** `"C:/Program Files/dotnet/dotnet.exe" build -warnaserror`.
  Expected: the build fails (`JsonCharsetGuard` is not defined). Everything else is behaviour.

- [ ] **Step 3: Implement.**

`src/Auth.Server/Tenancy/Outcome.cs` — `TenancyErrors`:

```diff
     public const string TooManyRequests = "too_many_requests";
+    public const string UnsupportedMediaType = "unsupported_media_type";
@@
         NotFound => StatusCodes.Status404NotFound,
+        UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
```

`src/Auth.Server/Api/JsonCharsetGuard.cs`:

```csharp
using Auth.Server.Requests;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.Net.Http.Headers;

namespace Auth.Server.Api;

/// <summary>
/// A request body declared <c>application/json</c> with a <c>charset</c> other than <c>utf-8</c> (any case) is
/// <c>415 {"error":"unsupported_media_type"}</c>: every reader of a JSON body in this service reads UTF-8 (spec 0008 → Fixes). It
/// covers <c>POST</c>, <c>PUT</c>, <c>PATCH</c> and <c>DELETE</c> under <c>/auth/</c> other than refresh and logout, which read no
/// body, and it runs before authentication so that it also covers login, which OpenIddict answers inside it. A missing charset means
/// UTF-8, as before; a content type that is not JSON is the handler's own <c>400</c>.
/// </summary>
public static class JsonCharsetGuard
{
    internal static bool Applies(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!(HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method) || HttpMethods.IsDelete(request.Method)))
        {
            return false;
        }

        var path = request.Path;
        return path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase)
            && !RefreshRequestHandler.IsRefreshPath(path)
            && !path.Equals(LogoutEndpoint.LogoutPath, StringComparison.OrdinalIgnoreCase)
            && !path.Equals(LogoutEndpoint.LogoutPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRefused(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && JsonObjectBody.IsJson(contentType)
        && parsed.Charset.HasValue
        && !HeaderUtilities.RemoveQuotes(parsed.Charset).Equals("utf-8", StringComparison.OrdinalIgnoreCase);   // charset="utf-8" keeps its quotes in the parsed value

    public static IApplicationBuilder UseJsonCharsetGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            if (Applies(context.Request) && IsRefused(context.Request.ContentType))
            {
                await ApiResults.Error(TenancyErrors.UnsupportedMediaType).ExecuteAsync(context);
                return;
            }

            await next(context);
        });
    }
}
```

`src/Auth.Server/Program.cs`:

```diff
 using Auth.Server.Network;
@@
 app.UseClientAddress(proxies);
 app.UseMiddleware<RateLimitMiddleware>();
+app.UseJsonCharsetGuard();
 app.UseAuthentication();
```

`src/Auth.Server/Api/AccountEndpoints.cs`:

```diff
-        // The health check has no OpenAPI metadata of its own: the description adds it (see OpenApiSetup).
-        app.MapHealthChecks(HealthPath);
+        // The health check has no OpenAPI metadata of its own: the description adds it (see OpenApiSetup). GET and HEAD only: any
+        // other method is the framework's 405 with `Allow: GET, HEAD` (spec 0008).
+        app.MapHealthChecks(HealthPath).WithMetadata(new HttpMethodMetadata(["GET", "HEAD"]));
```

`src/Auth.Server/Requests/EmailInput.cs` — the domain rule. The two callers of `IsDomainName` and the method itself change, and a helper is added; the rest of the file stays:

```diff
         var domain = email[(at + 1)..];
-        return IsDomainName(domain)
+        return IsDomainName(domain, encoded: false)
             && KeepsItsShapeThroughIdna(domain)
             && EncodedDomainOf(email) is { } sent
-            && IsDomainName(sent);
+            && IsDomainName(sent, encoded: true);
     }
 
-    /// <summary>A name with a dot between non-empty labels whose last label is not all digits; not a literal.</summary>
-    private static bool IsDomainName(string domain)
+    /// <summary>
+    /// A name with a dot between non-empty labels whose last label can be the end of a real domain name (see
+    /// <see cref="IsTopLevelLabel"/>); not a literal. Numbers, hexadecimal forms (<c>127.0x1</c>) and names such as <c>host.123</c>
+    /// are not domain names: a resolver may turn them into an address.
+    /// </summary>
+    private static bool IsDomainName(string domain, bool encoded)
     {
         var labels = domain.Split('.');
         return labels.Length > 1
             && labels.All(label => label.Length > 0)
-            && !labels[^1].All(char.IsAsciiDigit)
+            && IsTopLevelLabel(labels[^1], encoded)
             && !domain.StartsWith('[');
     }
+
+    /// <summary>
+    /// The last label of a domain: <c>xn--</c> and more, or two or more letters. As the relay gets it (<paramref name="encoded"/>)
+    /// the letters are ASCII. As typed they are letters of any script, with their combining marks, so that
+    /// <c>x@пример.рф</c> still passes. Decided by the characters alone, so that the host's IDN tables and its globalisation
+    /// mode change nothing.
+    /// </summary>
+    private static bool IsTopLevelLabel(string label, bool encoded)
+    {
+        if (label.StartsWith("xn--", StringComparison.OrdinalIgnoreCase))
+        {
+            return label.Length > 4 && label[4..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
+        }
+
+        if (encoded)
+        {
+            return label.Length >= 2 && label.All(char.IsAsciiLetter);
+        }
+
+        var runes = label.EnumerateRunes().ToList();
+        return runes.Count(Rune.IsLetter) >= 2
+            && runes.All(rune => Rune.IsLetter(rune)
+                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark);
+    }
```

`src/Auth.Server/Seeding/DevUserSeeder.cs` — the second user's role:

```diff
-        // The company's copy of the first default role, in the order of the manifest, that manages members or does not.
-        CompanyRole? FirstDefaultRole(bool manages) => manifest.DefaultRoles
-            .Where(d => manifest.Catalog.Expand(d.Permissions).Contains(PermissionCatalog.MembersManage) == manages)
-            .Select(d => roles.FirstOrDefault(r => r.NormalizedName == NameInput.Normalize(d.Name)))
-            .FirstOrDefault(r => r is not null);
+        // The company's copy of the first default role, in the order of the manifest, that manages members.
+        CompanyRole? FirstManagingDefaultRole() => manifest.DefaultRoles
+            .Where(d => manifest.Catalog.Expand(d.Permissions).Contains(PermissionCatalog.MembersManage))
+            .Select(d => roles.FirstOrDefault(r => r.NormalizedName == NameInput.Normalize(d.Name)))
+            .FirstOrDefault(r => r is not null);
@@
-            var admin = roles.FirstOrDefault(r => r.NormalizedName == NameInput.Normalize("admin")) ?? FirstDefaultRole(manages: true)
+            var admin = roles.FirstOrDefault(r => r.NormalizedName == NameInput.Normalize("admin")) ?? FirstManagingDefaultRole()
                 ?? throw new InvalidOperationException("The development company has no role that manages members.");
@@
-            var plain = FirstDefaultRole(manages: false) ?? roles.FirstOrDefault(r => r.NormalizedName == NameInput.Normalize("member"));
+            // Read from the database, not from the manifest (a role of the manifest may not be the company's any more): the first
+            // role of the company, by name, that holds neither members:manage nor *. The order the roles were stored in cannot be
+            // read back, so the name decides.
+            var plain = roles
+                .OrderBy(r => r.Name, StringComparer.Ordinal)
+                .FirstOrDefault(r => !r.Permissions.Contains(PermissionCatalog.All, StringComparer.Ordinal)
+                    && !r.Permissions.Contains(PermissionCatalog.MembersManage, StringComparer.Ordinal));
             if (plain is null)
```

and the class summary line "(the first default role, in the order of the manifest, that does not; else a new empty role `member`)" becomes "(the first role of the company, by name, that holds neither `members:manage` nor `*`; else a new empty role `member`)".

(`EmailInput.cs` needs `using System.Text;` for `Rune` — it has it — and `System.Globalization` for `UnicodeCategory` — it has it.)

- [ ] **Step 4: Run the new tests, then the whole suite.**

```bash
"C:/Program Files/dotnet/dotnet.exe" build -warnaserror
"C:/Program Files/dotnet/dotnet.exe" test --filter-class "Auth.IntegrationTests.HealthMethodsTests" --filter-class "Auth.IntegrationTests.JsonCharsetTests" --filter-class "Auth.IntegrationTests.InvitationDomainTests" --filter-class "Auth.IntegrationTests.DevCompanySeedTests" --filter-class "Auth.IntegrationTests.OpenApiTests"
"C:/Program Files/dotnet/dotnet.exe" test
```

  The domain rule must give the same answers where the host has no ICU, as the container has none (probed: MimeKit encodes `пример.рф` and `żółw.pl` there too). Run the invitation tests once more in that mode:

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 "C:/Program Files/dotnet/dotnet.exe" test --filter-class "Auth.IntegrationTests.InvitationDomainTests" --filter-class "Auth.IntegrationTests.InvitationAddressTests"
```

  Expected: PASS. If an older test refuses to pass because its domain ends in a label that is no longer a domain name (for example `a@b.c`), change the test's address to one that ends in letters (`a@b.cc`) and say so in the hand-back; no other change of behaviour is intended.

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `fix(server): health methods, JSON charset, invitation domains and the second seed user's role`.

## Day 2 — Saturday 21.02: the audit log

When the day is done, every flow of the service writes its row to `audit_events` in the same transaction as its change, the rows older than the retention are pruned every hour, and the table holds no password, token, link, cookie or mail body. Three tasks.

### Task 4: The audit table, the writer and the pruning

**Files:**
- Create: `src/Auth.Infrastructure/Persistence/AuditEvent.cs`, `src/Auth.Infrastructure/Persistence/Migrations/<id>_AddAuditEvents.cs` (+ `.Designer.cs`; `<id>` is supplied by the orchestrator)
- Create: `src/Auth.Server/Audit/AuditKinds.cs`, `src/Auth.Server/Audit/AuditEntry.cs`, `src/Auth.Server/Audit/AuditLog.cs`, `src/Auth.Server/Audit/AuditSettings.cs`, `src/Auth.Server/Audit/AuditPruner.cs`, `src/Auth.Server/Audit/AuditPruningService.cs`
- Modify: `src/Auth.Infrastructure/Persistence/AuthDbContext.cs`, `src/Auth.Infrastructure/Persistence/Migrations/AuthDbContextModelSnapshot.cs` (generated), `src/Auth.Server/Tenancy/TenancyServices.cs`, `src/Auth.Server/Program.cs`, `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`
- Test: `tests/Auth.IntegrationTests/AuditSettingsTests.cs`, `AuditTableTests.cs`, `AuditLogTests.cs`, `AuditPruningTests.cs`

**Interfaces:**
- Consumes: `AuthDbContext`, `Actor` (`Actor.IsOperator`, `Actor.UserId`), `ClientAddress.Text(HttpContext)` (Task 1), `StorableTime.Now(TimeProvider)` (`Auth.Server.Email`), the pattern of `LockoutPruner` and `LockoutPruningService`.
- Produces:
  - Entity `Auth.Infrastructure.Persistence.AuditEvent` (`Id`, `OccurredAt`, `Kind`, `ActorUserId`, `SubjectUserId`, `SubjectEmail`, `OrgId`, `OrgName`, `TargetId`, `ClientIp`, `Details`) and `AuthDbContext.AuditEvents`; table `audit_events` with the snake_case columns of the contract, no foreign keys.
  - `Auth.Server.Audit.AuditKinds` (the 22 kinds as string constants, and `All`), `AuditEntry` (`required string Kind`, `Guid? ActorUserId`, `Guid? SubjectUserId`, `string? SubjectEmail`, `Guid? OrgId`, `string? OrgName`, `Guid? TargetId`, `IReadOnlyDictionary<string, object?>? Details`).
  - `Auth.Server.Audit.AuditLog` (scoped; `AuditLog(AuthDbContext db, IServiceScopeFactory scopes, IHttpContextAccessor http, TimeProvider clock, ILogger<AuditLog> logger)`):
    - `AuditEvent Stage(AuditEntry entry, Actor? actor = null)` — puts the row on the request's `DbContext`; the caller's own `SaveChangesAsync` inside its transaction writes it (the change and its row: both or neither).
    - `Task WriteAloneAsync(AuditEntry entry, Actor? actor = null, CancellationToken cancellationToken = default)` — for an event that changes nothing else: saves at once, from a **scope and a `DbContext` of its own** (`IServiceScopeFactory`), so that it can never save what the request's context is tracking (a change the caller has half made), and a failure leaves nothing behind on the caller's context; **never throws** (a failure is logged at `Warning`, and the row is not retried).
    - The client address is read from `IHttpContextAccessor` (`ClientAddress.Text`, the whole address; `null` when there is no request: the operator CLI). An `Actor.Operator` adds `"via": "cli"` to the details; a member's id becomes `actor_user_id` unless the entry names one. `subject_email` is cut to 256 characters and `org_name` to 100, never inside a surrogate pair. An empty `Details` is stored as null.
  - `Auth.Server.Audit.AuditSettings` (`const string RetentionDaysKey = "Auth:Audit:RetentionDays"`, `int RetentionDays`, `static AuditSettings Load(IConfiguration)`, default 90, at least 1, blank = default), `AuditPruner.PruneOnceAsync(CancellationToken)` (returns the rows removed) and `AuditPruningService` (a pass at start and then every hour, as the other pruning services).
  - `AddTenancy` registers `AddHttpContextAccessor()` and `AuditLog` (scoped): the host and the operator CLI both get them.
  - Tests: `AuthAppFactory` pins `Auth:Audit:RetentionDays` blank.

**Design notes.** The writer has two doors because the contract has two kinds of event. A change and its row are one transaction: the services of Tasks 5 and 6 call `Stage` before the `SaveChangesAsync` that already commits their change. An event that changes nothing else (a failed login, a rate-limit hit) is written on its own by `WriteAloneAsync`, which must not break the request it describes and, because it uses a context of its own, can neither save a half-made change of the caller nor be broken by one. There are no foreign keys, so a row outlives the account, company, role or invitation it names; the columns are `text` or `uuid` (the client address is text, because `unknown` is a legal value). `details` is `jsonb` so that the runbook can ask `details->>'reason'`.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/AuditSettingsTests.cs`:

```csharp
using Auth.Server.Audit;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests;

public sealed class AuditSettingsTests
{
    private static AuditSettings Load(Dictionary<string, string?> values) =>
        AuditSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void The_retention_is_90_days_by_default()
    {
        Assert.Equal(90, Load([]).RetentionDays);
        Assert.Equal("Auth:Audit:RetentionDays", AuditSettings.RetentionDaysKey);
    }

    [Fact]
    public void The_retention_is_a_setting()
    {
        Assert.Equal(7, Load(new() { [AuditSettings.RetentionDaysKey] = "7" }).RetentionDays);
        Assert.Equal(1, Load(new() { [AuditSettings.RetentionDaysKey] = "1" }).RetentionDays);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_value_means_the_default(string? blank) =>
        Assert.Equal(90, Load(new() { [AuditSettings.RetentionDaysKey] = blank }).RetentionDays);

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("99999999999")]
    public void A_value_that_is_not_a_whole_number_of_at_least_one_stops_the_host_naming_the_key(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Load(new() { [AuditSettings.RetentionDaysKey] = value }));

        Assert.Contains($"'{AuditSettings.RetentionDaysKey}'", ex.Message, StringComparison.Ordinal);
    }
}
```

`tests/Auth.IntegrationTests/AuditTableTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class AuditTableTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private async Task<List<string>> StringsAsync(FormattableString sql)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Database.SqlQuery<string>(sql).ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_migration_makes_the_table_the_contract_names()   // criterion 8
    {
        var columns = await StringsAsync(
            $"""
            SELECT column_name || ':' || data_type || ':' || is_nullable AS "Value"
            FROM information_schema.columns WHERE table_name = 'audit_events' ORDER BY ordinal_position
            """);

        Assert.Equal(
            [
                "id:uuid:NO",
                "occurred_at:timestamp with time zone:NO",
                "kind:character varying:NO",
                "actor_user_id:uuid:YES",
                "subject_user_id:uuid:YES",
                "subject_email:character varying:YES",
                "org_id:uuid:YES",
                "org_name:character varying:YES",
                "target_id:uuid:YES",
                "client_ip:character varying:YES",
                "details:jsonb:YES",
            ],
            columns);
    }

    [Fact]
    public async Task The_table_has_no_foreign_key_so_that_a_row_outlives_what_it_names()
    {
        var keys = await StringsAsync(
            $"""SELECT conname AS "Value" FROM pg_constraint WHERE conrelid = 'audit_events'::regclass AND contype = 'f'""");

        Assert.Empty(keys);
    }

    [Fact]
    public async Task The_table_has_the_indexes_the_runbook_queries_and_the_pruning_use()
    {
        var indexes = await StringsAsync(
            $"""SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'audit_events' ORDER BY indexname""");

        Assert.Equal(
            [
                "IX_audit_events_actor_user_id",
                "IX_audit_events_client_ip",
                "IX_audit_events_occurred_at",
                "IX_audit_events_org_id",
                "IX_audit_events_subject_user_id",
                "PK_audit_events",
            ],
            indexes);
    }

    [Fact]
    public void The_kinds_are_the_twenty_two_of_the_contract()
    {
        Assert.Equal(
            [
                "login.succeeded", "login.failed", "login.locked", "logout", "refresh.reuse_detected",
                "password.reset_requested", "password.reset", "email.verified",
                "invite.sent", "invite.resent", "invite.accepted", "invite.cancelled",
                "member.removed", "member.role_changed", "role.created", "role.updated", "role.deleted",
                "org.created", "org.renamed", "org.deleted", "org.delete_refused", "rate_limit.hit",
            ],
            AuditKinds.All);
        Assert.All(AuditKinds.All, kind => Assert.True(kind.Length <= 40));
    }
}
```

`tests/Auth.IntegrationTests/AuditLogTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

public sealed class AuditLogTests : TenancyTestBase
{
    public AuditLogTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithoutHostedService<AuditPruningService>();
    }

    private static AuditEntry Entry(string kind = AuditKinds.LoginFailed) => new() { Kind = kind };

    /// <summary>PostgreSQL stores <c>jsonb</c> in its own normal form, with a space after each colon.</summary>
    private static string? Compact(string? json) => json?.Replace(" ", "", StringComparison.Ordinal);

    private async Task<List<AuditEvent>> RowsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().AuditEvents.AsNoTracking()
            .OrderBy(a => a.OccurredAt).ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_staged_row_is_written_by_the_save_of_the_change_it_belongs_to()   // criterion 8
    {
        var subject = Guid.NewGuid();
        var org = Guid.NewGuid();
        var target = Guid.NewGuid();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
            await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.MemberRemoved,
                SubjectUserId = subject,
                SubjectEmail = "worker@acme.test",
                OrgId = org,
                OrgName = "Acme",
                TargetId = target,
                Details = new Dictionary<string, object?> { ["role"] = "user" },
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        var row = Assert.Single(await RowsAsync());
        Assert.Equal("member.removed", row.Kind);
        Assert.Equal(subject, row.SubjectUserId);
        Assert.Equal("worker@acme.test", row.SubjectEmail);
        Assert.Equal(org, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(target, row.TargetId);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.ClientIp);   // no request: the operator CLI
        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.Equal(Clock.GetUtcNow().UtcDateTime, row.OccurredAt.UtcDateTime, TimeSpan.FromMilliseconds(1));
        Assert.Equal("""{"role":"user"}""", Compact(row.Details));
    }

    [Fact]
    public async Task A_change_that_is_rolled_back_leaves_no_row()   // criterion 8: a failed change leaves no row
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
            await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            audit.Stage(Entry(AuditKinds.OrgRenamed));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task A_row_that_is_staged_and_never_saved_is_never_written()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AuditLog>().Stage(Entry());
        }

        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task The_operator_is_marked_and_a_member_is_the_actor()
    {
        var member = Guid.NewGuid();
        var tenant = new TenantContext(member, Guid.NewGuid(), "Acme", Guid.NewGuid(), "admin", [], false);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
            audit.Stage(Entry(AuditKinds.OrgCreated), Actor.Operator);
            audit.Stage(new AuditEntry { Kind = AuditKinds.OrgRenamed, Details = new Dictionary<string, object?> { ["to"] = "B" } }, Actor.Of(tenant));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var rows = await RowsAsync();
        var created = Assert.Single(rows, r => r.Kind == "org.created");
        Assert.Null(created.ActorUserId);
        Assert.Equal("""{"via":"cli"}""", Compact(created.Details));
        var renamed = Assert.Single(rows, r => r.Kind == "org.renamed");
        Assert.Equal(member, renamed.ActorUserId);
        Assert.DoesNotContain("cli", renamed.Details ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_with_no_details_stores_null()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuditLog>().WriteAloneAsync(Entry(), cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Null(Assert.Single(await RowsAsync()).Details);
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::1")]
    [InlineData(null, "unknown")]
    public async Task The_whole_client_address_is_recorded_and_unknown_when_a_request_has_none(string? remote, string expected)   // criterion 2
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = remote is null ? null : IPAddress.Parse(remote);
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
            try
            {
                await scope.ServiceProvider.GetRequiredService<AuditLog>().WriteAloneAsync(Entry(), cancellationToken: TestContext.Current.CancellationToken);
            }
            finally
            {
                scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;
            }
        }

        Assert.Equal(expected, Assert.Single(await RowsAsync()).ClientIp);
    }

    [Fact]
    public async Task The_typed_address_is_cut_to_256_characters_and_the_org_name_to_100_and_no_pair_is_split()
    {
        var email = new string('a', 255) + char.ConvertFromUtf32(0x1F600) + "@example.test";   // a pair that straddles the 256th character
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuditLog>().WriteAloneAsync(
                new AuditEntry { Kind = AuditKinds.LoginFailed, SubjectEmail = email, OrgName = new string('o', 150) },
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(new string('a', 255), row.SubjectEmail);   // the high half of the pair went with the low one
        Assert.Equal(new string('o', 100), row.OrgName);
    }

    [Fact]
    public async Task A_write_on_its_own_that_fails_is_logged_and_never_thrown_and_is_not_retried()   // the write of a rate-limit hit never fails the request
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();

            await audit.WriteAloneAsync(new AuditEntry { Kind = new string('k', 100) }, cancellationToken: TestContext.Current.CancellationToken);   // longer than the column

            Assert.Equal(0, await db.SaveChangesAsync(TestContext.Current.CancellationToken));   // nothing is left to retry
        }

        Assert.Empty(await RowsAsync());
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Warning && e.Category.EndsWith("AuditLog", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_write_on_its_own_never_saves_what_the_callers_context_is_tracking()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
        audit.Stage(new AuditEntry { Kind = AuditKinds.OrgRenamed });   // a change the caller has half made

        await audit.WriteAloneAsync(new AuditEntry { Kind = AuditKinds.LoginFailed }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([AuditKinds.LoginFailed], (await RowsAsync()).Select(r => r.Kind));   // only the event on its own is in the table
        Assert.Equal(1, await db.SaveChangesAsync(TestContext.Current.CancellationToken)); // the caller's row still waits for the caller's save
        Assert.Equal(2, (await RowsAsync()).Count);
    }

    [Fact]
    public async Task A_failed_write_on_its_own_leaves_the_callers_pending_change_alone()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
        audit.Stage(new AuditEntry { Kind = AuditKinds.OrgRenamed });

        await audit.WriteAloneAsync(new AuditEntry { Kind = new string('k', 100) }, cancellationToken: TestContext.Current.CancellationToken);   // fails: longer than the column

        Assert.Equal(1, await db.SaveChangesAsync(TestContext.Current.CancellationToken));   // the caller's own save still works
        Assert.Equal([AuditKinds.OrgRenamed], (await RowsAsync()).Select(r => r.Kind));
    }

    [Fact]
    public async Task The_details_are_queryable_as_json()   // the backup runbook asks details->>'reason'
    {
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuditLog>().WriteAloneAsync(
                new AuditEntry { Kind = AuditKinds.LoginFailed, Details = new Dictionary<string, object?> { ["reason"] = "wrong_password" } },
                cancellationToken: TestContext.Current.CancellationToken);
        }

        var reasons = await InDbAsync(db => db.Database
            .SqlQuery<string>($"""SELECT details->>'reason' AS "Value" FROM audit_events""")
            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["wrong_password"], reasons);
    }
}
```

`tests/Auth.IntegrationTests/AuditPruningTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Auth.IntegrationTests;

public sealed class AuditPruningTests : SessionTestBase
{
    public AuditPruningTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The hosts' pruning loops wait on the fake clock: a background pass would race the explicit pass a test counts.
        Factory.WithoutHostedService<AuditPruningService>();
    }

    private async Task AddRowAsync(string email, TimeSpan age)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            OccurredAt = Clock.GetUtcNow() - age,
            Kind = AuditKinds.LoginFailed,
            SubjectEmail = email,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<string?>> EmailsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().AuditEvents.AsNoTracking()
            .OrderBy(a => a.SubjectEmail).Select(a => a.SubjectEmail).ToListAsync(TestContext.Current.CancellationToken);
    }

    private Task<int> PruneAsync() => Factory.Services.GetRequiredService<AuditPruner>().PruneOnceAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Rows_older_than_the_retention_are_pruned_and_newer_ones_stay()   // criterion 9
    {
        await AddRowAsync("ancient", TimeSpan.FromDays(400));
        await AddRowAsync("old", TimeSpan.FromDays(91));
        await AddRowAsync("edge", TimeSpan.FromDays(90) - TimeSpan.FromMinutes(1));
        await AddRowAsync("young", TimeSpan.FromDays(89));
        await AddRowAsync("new", TimeSpan.Zero);

        Assert.Equal(2, await PruneAsync());

        Assert.Equal(["edge", "new", "young"], await EmailsAsync());
    }

    [Fact]
    public async Task The_clock_decides_what_is_old()
    {
        await AddRowAsync("a", TimeSpan.FromDays(10));

        Assert.Equal(0, await PruneAsync());

        Clock.Advance(TimeSpan.FromDays(81));   // now 91 days old

        Assert.Equal(1, await PruneAsync());
        Assert.Empty(await EmailsAsync());
    }

    [Fact]
    public async Task A_pass_with_nothing_to_remove_removes_nothing()
    {
        await AddRowAsync("a", TimeSpan.FromDays(1));

        Assert.Equal(0, await PruneAsync());
        Assert.Equal(["a"], await EmailsAsync());
    }

    [Fact]
    public async Task The_retention_is_the_setting()   // criterion 9
    {
        await using var factory = new AuthAppFactory(Postgres, Keys)
            .WithClock(Clock)
            .WithSetting(AuditSettings.RetentionDaysKey, "7")
            .WithoutHostedService<AuditPruningService>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), OccurredAt = Clock.GetUtcNow() - TimeSpan.FromDays(8), Kind = AuditKinds.LoginFailed, SubjectEmail = "eight" });
            db.AuditEvents.Add(new AuditEvent { Id = Guid.NewGuid(), OccurredAt = Clock.GetUtcNow() - TimeSpan.FromDays(6), Kind = AuditKinds.LoginFailed, SubjectEmail = "six" });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var removed = await factory.Services.GetRequiredService<AuditPruner>().PruneOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
    }

    [Fact]
    public async Task The_service_is_part_of_the_host_and_runs_every_hour()
    {
        await using var factory = new AuthAppFactory(Postgres, Keys).WithClock(Clock);
        _ = factory.Services;
        Assert.Single(factory.Services.GetServices<IHostedService>().OfType<AuditPruningService>());
        Assert.Equal(TimeSpan.FromHours(1), AuditPruningService.Interval);
    }
}
```

(`AuditPruningTests.The_retention_is_the_setting` builds a second host on a database of its own, so it counts only its own rows.)

- [ ] **Step 2: Run the tests and see them fail.** `"C:/Program Files/dotnet/dotnet.exe" build -warnaserror`. Expected: the build fails (the `Auth.Server.Audit` namespace and `AuditEvent` do not exist).

- [ ] **Step 3: Implement the entity, the model and the migration.**

`src/Auth.Infrastructure/Persistence/AuditEvent.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>
/// One row of the audit log (spec 0008 → Audit log): who did what, to whom, in which company, from which address, when. There are
/// no foreign keys: a row outlives the account, company, role or invitation it names. A password, a token, a link, a cookie or a
/// mail body is never recorded. Written by Auth-Core only, read by SQL.
/// </summary>
public sealed class AuditEvent
{
    public required Guid Id { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>One of the kinds of the contract, for example <c>login.failed</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>The account that acted; null for the operator CLI, an anonymous request or a failed login.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>The account acted on, when there is one.</summary>
    public Guid? SubjectUserId { get; init; }

    /// <summary>The address involved, as stored or (a failed login of an unknown address) as typed; at most 256 characters.</summary>
    public string? SubjectEmail { get; init; }

    public Guid? OrgId { get; init; }

    /// <summary>The company's name at that moment.</summary>
    public string? OrgName { get; init; }

    /// <summary>The role or the invitation involved.</summary>
    public Guid? TargetId { get; init; }

    /// <summary>The client address, whole; <c>unknown</c> when the request had none; null for the operator CLI.</summary>
    public string? ClientIp { get; init; }

    /// <summary>A small JSON object: the reason of a failure, the old and the new role, <c>"via": "cli"</c>.</summary>
    public string? Details { get; init; }
}
```

`src/Auth.Infrastructure/Persistence/AuthDbContext.cs`:

```diff
     public DbSet<ActiveManifest> ActiveManifests => Set<ActiveManifest>();
+
+    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
@@
         builder.Entity<ActiveManifest>(manifest =>
         {
             manifest.HasKey(m => m.Id);
             manifest.Property(m => m.Id).ValueGeneratedNever();
         });
+
+        // Spec 0008 → Audit log. The names are the contract's (snake_case): the backup runbook queries them. No foreign keys on purpose.
+        builder.Entity<AuditEvent>(audit =>
+        {
+            audit.ToTable("audit_events");
+            audit.HasKey(a => a.Id);
+            audit.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
+            audit.Property(a => a.OccurredAt).HasColumnName("occurred_at");
+            audit.Property(a => a.Kind).HasColumnName("kind").HasMaxLength(40);
+            audit.Property(a => a.ActorUserId).HasColumnName("actor_user_id");
+            audit.Property(a => a.SubjectUserId).HasColumnName("subject_user_id");
+            audit.Property(a => a.SubjectEmail).HasColumnName("subject_email").HasMaxLength(256);
+            audit.Property(a => a.OrgId).HasColumnName("org_id");
+            audit.Property(a => a.OrgName).HasColumnName("org_name").HasMaxLength(100);
+            audit.Property(a => a.TargetId).HasColumnName("target_id");
+            audit.Property(a => a.ClientIp).HasColumnName("client_ip").HasMaxLength(64);
+            audit.Property(a => a.Details).HasColumnName("details").HasColumnType("jsonb");
+            // Pruning selects by age; the runbook's queries select by account, company and address.
+            audit.HasIndex(a => a.OccurredAt);
+            audit.HasIndex(a => a.SubjectUserId);
+            audit.HasIndex(a => a.ActorUserId);
+            audit.HasIndex(a => a.OrgId);
+            audit.HasIndex(a => a.ClientIp);
+        });
```

- [ ] **Step 4: Generate the migration and give it its id.**

```bash
"C:/Program Files/dotnet/dotnet.exe" tool restore
"C:/Program Files/dotnet/dotnet.exe" ef migrations add AddAuditEvents -p src/Auth.Infrastructure -s src/Auth.Server
```

  Expected: two new files in `src/Auth.Infrastructure/Persistence/Migrations/` (`<stamp>_AddAuditEvents.cs`, `<stamp>_AddAuditEvents.Designer.cs`) and a changed `AuthDbContextModelSnapshot.cs`. `Up` creates the one table `audit_events` (`id uuid`, `occurred_at timestamp with time zone`, `kind character varying(40)`, `actor_user_id uuid` null, `subject_user_id uuid` null, `subject_email character varying(256)` null, `org_id uuid` null, `org_name character varying(100)` null, `target_id uuid` null, `client_ip character varying(64)` null, `details jsonb` null; primary key `PK_audit_events`) and its five indexes (`IX_audit_events_occurred_at`, `_subject_user_id`, `_actor_user_id`, `_org_id`, `_client_ip`); `Down` drops the table. It must touch **no other table**; if it does, stop and report.

  `dotnet ef` stamps the id from the machine's clock. The id kept in the repository is the one **the orchestrator supplies in the task brief**: 14 digits, `yyyyMMddHHmmss`, greater than the newest id in `src/Auth.Infrastructure/Persistence/Migrations/` (a plain `ls` shows it). Apply it before anything else is built:

  1. Rename both generated files to `<id>_AddAuditEvents.cs` and `<id>_AddAuditEvents.Designer.cs` (plain `mv`; they are not tracked yet).
  2. In the `.Designer.cs` file change the attribute to `[Migration("<id>_AddAuditEvents")]`.
  3. Leave `AuthDbContextModelSnapshot.cs` as generated; it holds no id.
  4. Check: `git grep --untracked -n "_AddAuditEvents" -- src` prints exactly one line, the `[Migration("<id>_AddAuditEvents")]` attribute with the given id; `ls src/Auth.Infrastructure/Persistence/Migrations` shows the two files under that id; and `"C:/Program Files/dotnet/dotnet.exe" ef migrations has-pending-model-changes -p src/Auth.Infrastructure -s src/Auth.Server` reports no pending change.

- [ ] **Step 5: Implement the writer, the settings and the pruning.**

`src/Auth.Server/Audit/AuditKinds.cs`:

```csharp
namespace Auth.Server.Audit;

/// <summary>The kinds of the audit log (spec 0008 → Audit log). The names are the contract's; the runbook queries them.</summary>
public static class AuditKinds
{
    public const string LoginSucceeded = "login.succeeded";
    public const string LoginFailed = "login.failed";
    public const string LoginLocked = "login.locked";
    public const string Logout = "logout";
    public const string RefreshReuseDetected = "refresh.reuse_detected";
    public const string PasswordResetRequested = "password.reset_requested";
    public const string PasswordReset = "password.reset";
    public const string EmailVerified = "email.verified";
    public const string InviteSent = "invite.sent";
    public const string InviteResent = "invite.resent";
    public const string InviteAccepted = "invite.accepted";
    public const string InviteCancelled = "invite.cancelled";
    public const string MemberRemoved = "member.removed";
    public const string MemberRoleChanged = "member.role_changed";
    public const string RoleCreated = "role.created";
    public const string RoleUpdated = "role.updated";
    public const string RoleDeleted = "role.deleted";
    public const string OrgCreated = "org.created";
    public const string OrgRenamed = "org.renamed";
    public const string OrgDeleted = "org.deleted";
    public const string OrgDeleteRefused = "org.delete_refused";
    public const string RateLimitHit = "rate_limit.hit";

    public static IReadOnlyList<string> All { get; } =
    [
        LoginSucceeded, LoginFailed, LoginLocked, Logout, RefreshReuseDetected,
        PasswordResetRequested, PasswordReset, EmailVerified,
        InviteSent, InviteResent, InviteAccepted, InviteCancelled,
        MemberRemoved, MemberRoleChanged, RoleCreated, RoleUpdated, RoleDeleted,
        OrgCreated, OrgRenamed, OrgDeleted, OrgDeleteRefused, RateLimitHit,
    ];
}
```

`src/Auth.Server/Audit/AuditEntry.cs`:

```csharp
namespace Auth.Server.Audit;

/// <summary>What a flow says about an event; <see cref="AuditLog"/> adds the time, the id, the client address and the operator mark.</summary>
public sealed class AuditEntry
{
    public required string Kind { get; init; }

    /// <summary>The account that acted. Left null for an anonymous request, a failed login and the operator; a member's id comes from the actor.</summary>
    public Guid? ActorUserId { get; init; }

    public Guid? SubjectUserId { get; init; }

    /// <summary>The address involved: as stored, or as typed for an address nobody has. Never a password, a token or a link.</summary>
    public string? SubjectEmail { get; init; }

    public Guid? OrgId { get; init; }

    public string? OrgName { get; init; }

    public Guid? TargetId { get; init; }

    /// <summary>Small values only: names, reasons, counts. Never a secret.</summary>
    public IReadOnlyDictionary<string, object?>? Details { get; init; }
}
```

`src/Auth.Server/Audit/AuditLog.cs`:

```csharp
using System.Text.Json;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Network;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Audit;

/// <summary>
/// The writer of the audit log (spec 0008 → Audit log), scoped like the <c>DbContext</c> it uses. <see cref="Stage"/> is for a
/// change: the row goes onto the request's context and is written by the save, inside the transaction, that commits the change,
/// so that either both are there or neither. <see cref="WriteAloneAsync"/> is for an event that changes nothing else: it saves at
/// once, from a scope and a context of its own (it must never save what the request's context is tracking, and a failure must
/// leave nothing behind there), and never fails the request it describes. Neither ever takes a password, a token, a link, a cookie or a mail body: the
/// callers hand over names, reasons and counts only.
/// </summary>
public sealed partial class AuditLog(
    AuthDbContext db, IServiceScopeFactory scopes, IHttpContextAccessor http, TimeProvider clock, ILogger<AuditLog> logger)
{
    public const int MaxEmailLength = 256;
    public const int MaxOrgNameLength = 100;

    /// <summary>Adds the row to the context; the caller's own <c>SaveChangesAsync</c> writes it.</summary>
    public AuditEvent Stage(AuditEntry entry, Actor? actor = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var row = Build(entry, actor);
        db.AuditEvents.Add(row);
        return row;
    }

    /// <summary>
    /// Writes the row now, from a scope and a context of its own: the request's context may be tracking a change that is half made,
    /// and this save must neither write it nor be undone by it. A failure is logged and swallowed, and nothing retries the row.
    /// </summary>
    public async Task WriteAloneAsync(AuditEntry entry, Actor? actor = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var row = Build(entry, actor);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var own = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            own.AuditEvents.Add(row);
            await own.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNotWritten(logger, entry.Kind, exception);
        }
    }

    private AuditEvent Build(AuditEntry entry, Actor? actor)
    {
        var details = entry.Details is { Count: > 0 } given ? new Dictionary<string, object?>(given) : [];
        if (actor is { IsOperator: true })
        {
            details["via"] = "cli";
        }

        return new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = StorableTime.Now(clock),
            Kind = entry.Kind,
            ActorUserId = entry.ActorUserId ?? actor?.UserId,
            SubjectUserId = entry.SubjectUserId,
            SubjectEmail = Truncate(entry.SubjectEmail, MaxEmailLength),
            OrgId = entry.OrgId,
            OrgName = Truncate(entry.OrgName, MaxOrgNameLength),
            TargetId = entry.TargetId,
            ClientIp = http.HttpContext is { } context ? ClientAddress.Text(context) : null,
            Details = details.Count == 0 ? null : JsonSerializer.Serialize(details),
        };
    }

    /// <summary>At most <paramref name="max"/> characters, and never half of a surrogate pair.</summary>
    internal static string? Truncate(string? value, int max)
    {
        if (value is null || value.Length <= max)
        {
            return value;
        }

        return value[..(char.IsHighSurrogate(value[max - 1]) ? max - 1 : max)];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The audit event {Kind} could not be written.")]
    private static partial void LogNotWritten(ILogger logger, string kind, Exception exception);
}
```

`src/Auth.Server/Audit/AuditSettings.cs`:

```csharp
using System.Globalization;

namespace Auth.Server.Audit;

/// <summary><c>Auth:Audit:RetentionDays</c>: how long a row of the audit log is kept (default 90, at least 1; blank means the default).</summary>
public sealed class AuditSettings
{
    public const string RetentionDaysKey = "Auth:Audit:RetentionDays";
    public const int DefaultRetentionDays = 90;

    public AuditSettings(int retentionDays)
    {
        if (retentionDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays), retentionDays, "The retention is at least one day.");
        }

        RetentionDays = retentionDays;
    }

    public int RetentionDays { get; }

    /// <exception cref="InvalidOperationException">The value is not a whole number of at least 1; the message names the key.</exception>
    public static AuditSettings Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration[RetentionDaysKey] is not { } text || string.IsNullOrWhiteSpace(text))
        {
            return new AuditSettings(DefaultRetentionDays);
        }

        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days >= 1
            ? new AuditSettings(days)
            : throw new InvalidOperationException($"Configuration value '{RetentionDaysKey}' must be a whole number of at least 1.");
    }
}
```

`src/Auth.Server/Audit/AuditPruner.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Audit;

/// <summary>
/// One pruning pass over the audit log: the rows older than the retention (<see cref="AuditSettings"/>). Anyone can make rows
/// (failed logins, mail requests), so the table is bounded by the per-IP limits and by this.
/// </summary>
public sealed partial class AuditPruner(IServiceScopeFactory scopes, TimeProvider clock, AuditSettings settings, ILogger<AuditPruner> logger)
{
    public async Task<int> PruneOnceAsync(CancellationToken cancellationToken)
    {
        var threshold = clock.GetUtcNow() - TimeSpan.FromDays(settings.RetentionDays);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var removed = await db.AuditEvents.Where(a => a.OccurredAt < threshold).ExecuteDeleteAsync(cancellationToken);

        LogPruned(logger, removed);
        return removed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Events} audit events.")]
    private static partial void LogPruned(ILogger logger, int events);
}
```

`src/Auth.Server/Audit/AuditPruningService.cs` (the same loop as `LockoutPruningService`, which the owner accepted as a copy in spec 0008, Decision 3):

```csharp
namespace Auth.Server.Audit;

/// <summary>Runs <see cref="AuditPruner"/> at host start and then every <see cref="Interval"/>.</summary>
public sealed partial class AuditPruningService(AuditPruner pruner, TimeProvider clock, ILogger<AuditPruningService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        try
        {
            do
            {
                await PruneAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: end the loop normally rather than as a cancelled task, which the host would count as a failure.
        }
    }

    private async Task PruneAsync(CancellationToken stoppingToken)
    {
        try
        {
            await pruner.PruneOnceAsync(stoppingToken);
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // The host stopped in the middle of a pass. Whatever the store made of the cancellation, it is not a failure.
        }
        catch (Exception exception)
        {
            // Pruning is housekeeping: a failed pass must not take the auth service down. Try again next time.
            LogFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit log pruning failed; it will run again at the next interval.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
```

`src/Auth.Server/Tenancy/TenancyServices.cs`:

```diff
+using Auth.Server.Audit;
+
 namespace Auth.Server.Tenancy;
@@
         services.AddSingleton<ManifestActivator>();
+        // The audit writer needs the request's client address; the operator CLI has no request, and the accessor then says so.
+        services.AddHttpContextAccessor();
+        services.AddScoped<AuditLog>();
         services.AddScoped<CompanyService>();
```

`src/Auth.Server/Program.cs`:

```diff
+using Auth.Server.Audit;
 using Auth.Server.Email;
@@
 builder.Services.AddSingleton<LockoutPruner>();
+builder.Services.AddSingleton(AuditSettings.Load(builder.Configuration));
+builder.Services.AddSingleton<AuditPruner>();
@@
 builder.Services.AddHostedService<LockoutPruningService>();
+builder.Services.AddHostedService<AuditPruningService>();
```

`tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`:

```diff
 using Auth.Infrastructure.Identity;
+using Auth.Server.Audit;
 using Auth.Server.Email;
@@
         _settings[ProxySettings.KnownProxiesKey] = "";
+        // A variable of the machine cannot change how long a test host keeps its audit rows.
+        _settings[AuditSettings.RetentionDaysKey] = "";
```

- [ ] **Step 6: Run the new tests, then the whole suite.**

```bash
"C:/Program Files/dotnet/dotnet.exe" build -warnaserror
"C:/Program Files/dotnet/dotnet.exe" test --filter-class "Auth.IntegrationTests.AuditSettingsTests" --filter-class "Auth.IntegrationTests.AuditTableTests" --filter-class "Auth.IntegrationTests.AuditLogTests" --filter-class "Auth.IntegrationTests.AuditPruningTests"
"C:/Program Files/dotnet/dotnet.exe" test
```

  Expected: PASS. Every host migrates at startup, so a model that differs from the migration would fail them all.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files, with the migration under the id the orchestrator gave. Proposed subject: `feat(audit): audit_events table, the writer and the pruning service`.

### Task 5: The audit events of the account flows

**Files:**
- Create: `src/Auth.Server/Sessions/RefreshReuseAuditHandler.cs`, `src/Auth.Server/RateLimiting/RateLimitAudit.cs`, `src/Auth.Server/Login/AtomicSignInResult.cs`
- Modify: `src/Auth.Server/Login/LoginEndpoint.cs`, `src/Auth.Server/Lockout/TooManyAttemptsResult.cs`, `src/Auth.Server/Sessions/LogoutEndpoint.cs`, `src/Auth.Server/Tokens/OpenIddictSetup.cs`, `src/Auth.Server/Email/MailRequestStore.cs`, `src/Auth.Server/Account/MailRequestEndpoint.cs`, `src/Auth.Server/Account/ResetPasswordEndpoint.cs`, `src/Auth.Server/Account/VerifyEmailEndpoint.cs`, `src/Auth.Server/RateLimiting/RateLimitMiddleware.cs`, `src/Auth.Server/Program.cs`
- Create (tests): `tests/Auth.IntegrationTests/Infrastructure/AuditTestBase.cs`
- Test: `tests/Auth.IntegrationTests/AuditAccountFlowsTests.cs`, `AuditRefreshReuseTests.cs`, `AuditRateLimitTests.cs`, `AuditClientAddressTests.cs`, `RateLimitAuditTests.cs`

**Interfaces:**
- Consumes: Task 4 (`AuditLog`, `AuditEntry`, `AuditKinds`, `AuditSettings`, `AuditEvent`), Task 1 (`ClientAddress.Text`, `RateLimitMiddleware`, `RatePolicy`, `RateLimitSettings`), `LockoutApi`, `SessionApi`, `MailTestBase.DispatchAsync/TokenIn`.
- Produces (src):
  - Rows of the kinds `login.succeeded`, `login.failed` (reason in `details`: `unknown_address`, `wrong_password`, `unconfirmed_address`, `no_company`), `login.locked` (`details.retry_after_seconds`), `logout`, `refresh.reuse_detected`, `password.reset_requested`, `password.reset`, `email.verified`, `rate_limit.hit` (`details.policy`, `details.limit`).
  - `TooManyAttemptsResult.SecondsOf(TimeSpan retryAfter)` (`long`, at least 1; the same number the `429` carries).
  - `MailRequestStore.SubmitAsync(MailKind kind, string normalizedEmail, CancellationToken cancellationToken, string? typedEmail = null)` — a reset request is recorded with the address as typed.
  - `Auth.Server.RateLimiting.RateLimitAudit` (singleton; `RateLimitAudit(TimeProvider clock)`, `bool ShouldRecord(string address, RatePolicy policy)` true at most once per address and policy per minute, `int Count`).
  - `Auth.Server.Sessions.RefreshReuseAuditHandler` (an OpenIddict handler registered by `OpenIddictSetup`).
  - `Auth.Server.Login.AtomicSignInResult(ClaimsPrincipal principal, AuditEntry entry) : IResult` — issues the session of a login and writes its `login.succeeded` row in one transaction (see below); `LogoutEndpoint.HandleAsync` gains the parameters `AuthDbContext db, AuditLog audit`.
- Produces (tests): `AuditTestBase` (`AuditAsync(kind?)`, `SingleAsync(kind)`, `AssertNoSecretsAsync(params string[])`, a host with the test remote-address header and without the pruning service) and `AuditApi.Text(AuditEvent, string detail)`.

**Where each row is written, and why there** (decided against the code; the rows of Task 6 follow the same rule):
- A row for a **change** is staged on the request's `DbContext` and saved by the save that commits the change, inside its transaction: `password.reset` and `email.verified` before the `CommitAsync` of their endpoints, `password.reset_requested` inside `MailRequestStore`'s transaction, together with the queued request. A refused or failed change commits nothing, so it leaves no row. **A session issued by a login and a session ended by a logout are changes too** (the first creates an authorization and its tokens, the second revokes them), so `login.succeeded` and `logout` are in this group, with the transactions described below; if their row cannot be written, the login or the logout fails with a `500` and has no effect.
- A row for an **event that changes nothing** is written on its own, best effort, and never fails the request: `login.failed`, `login.locked`, `refresh.reuse_detected`, `rate_limit.hit`. The login rows are written once the outcome is decided and before the answer is built; unknown and wrong-password failures each cost one insert, so the two answers stay as slow as each other (spec 0003).
- `login.succeeded` is atomic with the issuance of the session, which is the only way to keep "a change and its row in the same transaction" true here. OpenIddict issues the session while the endpoint's result is executed (`Results.SignIn(...)`), in several saves of its own (the authorization, then each token entry, then its payload), all on the request's scoped `AuthDbContext`. Staging the row on that context and letting OpenIddict's first save write it would tie it to the authorization only: a later failure would leave a `login.succeeded` row, and an authorization, for a login that answered `500`. So the login endpoint returns `AtomicSignInResult`, which (1) opens a transaction on the request's `AuthDbContext` (OpenIddict's stores use the same context and join it), (2) stages the row, (3) executes `Results.SignIn` **with the response body held in a memory buffer** (OpenIddict's JSON writer writes the body once and starts nothing else; the `Set-Cookie` of the refresh token is a header and is not sent until the body is), (4) saves and commits when the answer is a `200`, and only then copies the buffered body to the real response. If the save or the commit fails, the transaction rolls back (no authorization, no token, no row), the buffered body is dropped, the response is cleared (so no `Set-Cookie` leaves) and the exception goes to the outermost error handler: `500 internal_error`, and no session. Probed in a scratch copy on OpenIddict 7.7.1 and PostgreSQL 16 with an injected failure at the commit: the success path stored the tokens and the row and answered the contract body with the cookie; the failing path stored nothing, answered `500` with no `Set-Cookie` and no `access_token` in the body.
- A login to an account that has **no password** (an account an invitation has made but that has not accepted yet) is recorded as `unknown_address`, with no subject and the address as typed, exactly as an address nobody has: the answer is the same `401` as for an unknown address (no enumeration), and so is its audit row. The "As built" section records this.
- `refresh.reuse_detected`: OpenIddict decides a reuse inside its own token validation (`ValidateTokenEntry`), where a rejection stops the handlers after it, so nothing of ours can run *after* the decision. A handler placed just **before** it asks the same question: the token entry is a refresh token, its status is `redeemed`, and its redemption is older than the 15-second leeway of `SessionPolicy.ReuseLeeway`. Probed on OpenIddict 7.7.1: inside the leeway the status is `redeemed` too but the redemption is younger, so no row; after the first replay OpenIddict revokes the family, the status becomes `revoked`, and a second replay writes nothing. (Two replays that arrive at the very same instant may both see `redeemed` and write two rows; that is the only duplicate possible and it is harmless.)
- `logout` names the account of the cookie's session, looked up in the token store; a logout with no valid cookie, or with a token that is already revoked, writes nothing. The revocation and the row are one transaction: the endpoint opens it on the request's `AuthDbContext`, OpenIddict's stores join it, the row is staged and saved, and the commit ends it (probed with an injected failure: the revocation was rolled back and the cookie still refreshed).
- `rate_limit.hit` is written by the limiter's middleware, once per client address and policy per minute, **before** the `429` is sent, so that a test can read it as soon as it has the answer; a failing write is swallowed by the writer.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/Infrastructure/AuditTestBase.cs`:

```csharp
using System.Text.Json;
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Base of the audit tests: a company-capable host (invitations are mails) whose requests may name their client address in
/// <see cref="AuthAppFactory.RemoteAddressHeader"/>, and without the hourly pruning pass, which the fake clock would fire.
/// </summary>
public abstract class AuditTestBase : TenancyTestBase
{
    protected const string WrongPassword = "Wrong-Password-1";

    protected AuditTestBase(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithRemoteAddressHeader().WithoutHostedService<AuditPruningService>();
    }

    /// <summary>The rows of the audit log, of one kind or all, oldest first.</summary>
    protected Task<List<AuditEvent>> AuditAsync(string? kind = null) =>
        InDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(a => kind == null || a.Kind == kind)
            .OrderBy(a => a.OccurredAt).ThenBy(a => a.Id)
            .ToListAsync(TestContext.Current.CancellationToken));

    protected async Task<AuditEvent> SingleAsync(string kind) => Assert.Single(await AuditAsync(kind));

    /// <summary>No password, token, link or other secret in any column of any row: the rows are searched for each of the texts.</summary>
    protected async Task AssertNoSecretsAsync(params string[] secrets)
    {
        var rows = await AuditAsync();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var text = string.Join(
                '\n',
                new[] { row.Kind, row.SubjectEmail, row.OrgName, row.ClientIp, row.Details }.Where(v => v is not null));
            foreach (var secret in secrets)
            {
                Assert.False(string.IsNullOrEmpty(secret));
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            }
        }
    }
}

public static class AuditApi
{
    /// <summary>A string detail of a row (<c>jsonb</c> comes back in PostgreSQL's own form, so the row is parsed).</summary>
    public static string? Text(AuditEvent row, string detail)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Details is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(row.Details);
        return document.RootElement.TryGetProperty(detail, out var value) ? value.ToString() : null;
    }

    /// <summary>A detail of a row as compact JSON text (an array stays an array), or null.</summary>
    public static string? Raw(AuditEvent row, string detail)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Details is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(row.Details);
        return document.RootElement.TryGetProperty(detail, out var value)
            ? JsonSerializer.Serialize(value)
            : null;
    }

    /// <summary>The names of the details of a row, sorted.</summary>
    public static string[] DetailNames(AuditEvent row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Details is null)
        {
            return [];
        }

        using var document = JsonDocument.Parse(row.Details);
        return [.. document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
    }
}
```

`tests/Auth.IntegrationTests/AuditAccountFlowsTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class AuditAccountFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Remote = "203.0.113.5";
    private const string NewPassword = "Brand-New-Passw0rd";
    private static readonly TimeSpan PastTheGap = TimeSpan.FromSeconds(61);

    private Task<HttpResponseMessage> LoginFromAsync(string email, string password, string remote = Remote) =>
        RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/login", remote, null, System.Text.Json.JsonSerializer.Serialize(new { email, password }));

    // ---- login

    [Fact]
    public async Task A_login_that_succeeds_is_recorded_with_the_account_the_company_and_the_address()   // criterion 8
    {
        using var response = await LoginFromAsync(Factory.SeedEmail, Factory.SeedPassword);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await SingleAsync(AuditKinds.LoginSucceeded);
        var seed = await Factory.SeedUserIdAsync();
        Assert.Equal(seed, row.ActorUserId);
        Assert.Equal(seed, row.SubjectUserId);
        Assert.Equal(Factory.SeedEmail, row.SubjectEmail);
        Assert.Equal(await DevCompanyIdAsync(), row.OrgId);
        Assert.Equal(DevUserSeeder.DefaultOrgName, row.OrgName);
        Assert.Equal(Remote, row.ClientIp);
        Assert.Null(row.TargetId);
        Assert.Empty(await AuditAsync(AuditKinds.LoginFailed));
        await AssertNoSecretsAsync(Factory.SeedPassword);
    }

    [Fact]
    public async Task A_wrong_password_is_recorded_with_the_account_as_stored_and_the_reason()   // criterion 8
    {
        using var response = await LoginFromAsync(Factory.SeedEmail.ToUpperInvariant(), WrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Null(row.ActorUserId);   // a failed login has no actor
        Assert.Equal(await Factory.SeedUserIdAsync(), row.SubjectUserId);
        Assert.Equal(Factory.SeedEmail, row.SubjectEmail);   // as stored, not as typed
        Assert.Equal("wrong_password", AuditApi.Text(row, "reason"));
        Assert.Equal(Remote, row.ClientIp);
        await AssertNoSecretsAsync(WrongPassword, Factory.SeedPassword);
    }

    [Fact]
    public async Task An_address_nobody_has_is_recorded_as_typed_with_no_account()   // criterion 8
    {
        using var response = await LoginFromAsync("Nobody@Example.test", WrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.SubjectUserId);
        Assert.Equal("Nobody@Example.test", row.SubjectEmail);
        Assert.Equal("unknown_address", AuditApi.Text(row, "reason"));
        await AssertNoSecretsAsync(WrongPassword);
    }

    [Fact]
    public async Task An_account_with_no_password_yet_is_recorded_like_an_address_nobody_has()   // an invited account that has not accepted
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await users.CreateAsync(new ApplicationUser { UserName = "invited@example.com", Email = "invited@example.com", EmailConfirmed = true });
            Assert.True(created.Succeeded);
        }

        using var response = await LoginFromAsync("Invited@Example.com", WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);   // the answer of an unknown address (no enumeration)
        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Null(row.SubjectUserId);                                    // and so is its row: no account is named
        Assert.Equal("Invited@Example.com", row.SubjectEmail);
        Assert.Equal("unknown_address", AuditApi.Text(row, "reason"));
    }

    [Fact]
    public async Task A_login_whose_row_cannot_be_written_issues_no_session()   // criterion 8: a session and its row are both made, or neither
    {
        var tokensBefore = await TokenCountAsync();
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var response = await LoginFromAsync(Factory.SeedEmail, Factory.SeedPassword);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("""{"error":"internal_error"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(response.Headers.Contains("Set-Cookie"), "no refresh cookie may leave for a session that was rolled back");
        Assert.Equal(tokensBefore, await TokenCountAsync());   // OpenIddict's authorization and token entries were rolled back with it
        Assert.Equal(0, await InDbAsync(db => db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "OpenIddictAuthorizations" """)
            .SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_logout_whose_row_cannot_be_written_ends_nothing()   // criterion 8: the revocation and its row are both made, or neither
    {
        var session = await SessionApi.LoginAsync(Client, Factory);
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var logout = await SessionApi.Logout(Client, session.RefreshToken);

        Assert.Equal(HttpStatusCode.InternalServerError, logout.StatusCode);
        using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);   // the session was not revoked: the cookie still refreshes
    }

    private Task<int> TokenCountAsync() =>
        InDbAsync(db => db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "OpenIddictTokens" """).SingleAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_correct_password_for_an_unconfirmed_address_is_recorded_as_a_failed_login()   // criterion 8
    {
        var user = await CreateUserAsync("new@example.com", confirmed: false);

        using var response = await LoginFromAsync("new@example.com", UserPassword);
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "email_not_verified");

        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Equal(user.Id, row.SubjectUserId);
        Assert.Equal("unconfirmed_address", AuditApi.Text(row, "reason"));
        await AssertNoSecretsAsync(UserPassword);
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task A_correct_password_for_an_account_without_a_company_is_recorded_as_a_failed_login()   // criterion 8
    {
        var user = await CreateUserAsync("lonely@example.com", confirmed: true, member: false);

        using var response = await LoginFromAsync("lonely@example.com", UserPassword);
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");

        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Equal(user.Id, row.SubjectUserId);
        Assert.Equal("no_company", AuditApi.Text(row, "reason"));
    }

    [Fact]
    public async Task A_locked_identifier_is_recorded_with_the_address_as_typed_and_the_wait()   // criterion 8
    {
        await LockoutApi.FailAsync(Client, Clock, "ghost@example.test", 10);

        using var refused = await LoginFromAsync("Ghost@Example.test", WrongPassword);
        var seconds = await LockoutApi.AssertLockedAsync(refused);

        var row = await SingleAsync(AuditKinds.LoginLocked);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.SubjectUserId);   // a locked attempt looks nothing up (spec 0003)
        Assert.Equal("Ghost@Example.test", row.SubjectEmail);
        Assert.Equal(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), AuditApi.Text(row, "retry_after_seconds"));
        Assert.Equal(Remote, row.ClientIp);
        Assert.Equal(10, (await AuditAsync(AuditKinds.LoginFailed)).Count);   // the ten failures that led here
    }

    [Fact]
    public async Task A_login_the_server_cannot_read_writes_nothing()
    {
        using var malformed = await RateLimitApi.SendAsync(Client, HttpMethod.Post, "/auth/login", Remote, null, "{ not json");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        Assert.Empty(await AuditAsync(AuditKinds.LoginFailed));
        Assert.Empty(await AuditAsync(AuditKinds.LoginLocked));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    // ---- logout

    [Fact]
    public async Task A_logout_names_the_account_of_the_cookie_and_a_second_one_writes_nothing()   // criterion 8
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        using var first = await RateLimitApi.SendAsync(Client, HttpMethod.Post, "/auth/logout", Remote, null, null);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);   // no cookie: no row
        Assert.Empty(await AuditAsync(AuditKinds.Logout));

        using var real = await SessionApi.Logout(Client, session.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, real.StatusCode);
        var row = await SingleAsync(AuditKinds.Logout);
        var seed = await Factory.SeedUserIdAsync();
        Assert.Equal(seed, row.ActorUserId);
        Assert.Equal(seed, row.SubjectUserId);

        using var again = await SessionApi.Logout(Client, session.RefreshToken);   // already revoked
        using var junk = await SessionApi.Logout(Client, "a-cookie-nobody-issued");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, junk.StatusCode);
        Assert.Single(await AuditAsync(AuditKinds.Logout));
    }

    // ---- password reset and email verification

    [Fact]
    public async Task A_reset_request_is_recorded_with_the_address_as_typed_and_no_account_and_only_when_queued()   // criterion 8
    {
        for (var i = 0; i < 5; i++)
        {
            Clock.Advance(PastTheGap);   // one accepted request per minute (spec 0004)
            using var response = await AccountApi.Forgot(Client, "Typed@Example.test");
            await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        }

        Clock.Advance(PastTheGap);
        using var limited = await AccountApi.Forgot(Client, "Typed@Example.test");   // the sixth within the hour: the mail limit
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        var rows = await AuditAsync(AuditKinds.PasswordResetRequested);
        Assert.Equal(5, rows.Count);   // a request that is not queued leaves no row
        Assert.All(rows, row =>
        {
            Assert.Equal("Typed@Example.test", row.SubjectEmail);   // as typed
            Assert.Null(row.SubjectUserId);   // no account: the request does not look one up
            Assert.Null(row.ActorUserId);
        });
    }

    [Fact]
    public async Task A_verification_request_writes_no_row()   // the contract has no kind for it
    {
        using var response = await AccountApi.RequestVerification(Client, "new@example.com");
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);

        Assert.Empty(await AuditAsync(AuditKinds.PasswordResetRequested));
        Assert.Empty(await AuditAsync(AuditKinds.EmailVerified));
    }

    [Fact]
    public async Task A_reset_is_recorded_without_the_link_or_the_password_and_a_failed_one_leaves_no_row()   // criteria 8 and 9
    {
        using (var forgot = await AccountApi.Forgot(Client, Factory.SeedEmail))
        {
            await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var token = TokenIn(Mail.Sent[^1]);

        using (var weak = await AccountApi.Reset(Client, token, "abc"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        }

        using (var unknown = await AccountApi.Reset(Client, "x".PadRight(43, 'x'), NewPassword))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }

        Assert.Empty(await AuditAsync(AuditKinds.PasswordReset));   // nothing changed, so no row

        using var done = await AccountApi.Reset(Client, token, NewPassword);
        await AccountApi.AssertEmptyAsync(done, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.PasswordReset);
        Assert.Equal(await Factory.SeedUserIdAsync(), row.SubjectUserId);
        Assert.Equal(Factory.SeedEmail, row.SubjectEmail);
        Assert.Null(row.ActorUserId);   // the person who holds the link is anonymous
        await AssertNoSecretsAsync(token, NewPassword, "abc", Factory.SeedPassword);
    }

    [Fact]
    public async Task A_verification_is_recorded_and_a_refused_one_leaves_no_row()   // criterion 8
    {
        var user = await CreateUserAsync("new@example.com", confirmed: false);
        using (var request = await AccountApi.RequestVerification(Client, "new@example.com"))
        {
            await AccountApi.AssertEmptyAsync(request, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var token = TokenIn(Mail.Sent[^1]);

        using (var invalid = await AccountApi.Verify(Client, "y".PadRight(43, 'y')))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        Assert.Empty(await AuditAsync(AuditKinds.EmailVerified));

        using var done = await AccountApi.Verify(Client, token);
        await AccountApi.AssertEmptyAsync(done, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.EmailVerified);
        Assert.Equal(user.Id, row.SubjectUserId);
        Assert.Equal("new@example.com", row.SubjectEmail);
        await AssertNoSecretsAsync(token);
    }
}
```

`tests/Auth.IntegrationTests/AuditRefreshReuseTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Seeding;

namespace Auth.IntegrationTests;

public sealed class AuditRefreshReuseTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    [Fact]
    public async Task A_consumed_token_presented_after_the_leeway_is_recorded_once_with_its_account()   // criterion 8
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        await SessionApi.RefreshOk(Client, login.RefreshToken);

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(replay);

        var row = await SingleAsync(AuditKinds.RefreshReuseDetected);
        var seed = await Factory.SeedUserIdAsync();
        Assert.Null(row.ActorUserId);   // whoever replayed the token is not known
        Assert.Equal(seed, row.SubjectUserId);
        Assert.Equal(await DevCompanyIdAsync(), row.OrgId);
        Assert.Equal(DevUserSeeder.DefaultOrgName, row.OrgName);

        using var again = await SessionApi.Refresh(Client, login.RefreshToken);   // the family is revoked now: nothing more to detect
        await SessionApi.AssertInvalidGrantAsync(again);
        Assert.Single(await AuditAsync(AuditKinds.RefreshReuseDetected));
        await AssertNoSecretsAsync(login.RefreshToken);
    }

    [Fact]
    public async Task A_consumed_token_presented_inside_the_leeway_is_an_honest_retry_and_writes_nothing()
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        await SessionApi.RefreshOk(Client, login.RefreshToken);

        Clock.Advance(TimeSpan.FromSeconds(10));
        _ = await SessionApi.RefreshOk(Client, login.RefreshToken);

        Assert.Empty(await AuditAsync(AuditKinds.RefreshReuseDetected));
    }

    [Fact]
    public async Task Tokens_that_are_unknown_or_revoked_or_current_write_nothing()
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        using (var unknown = await SessionApi.Refresh(Client, "a-cookie-nobody-issued"))
        {
            await SessionApi.AssertInvalidGrantAsync(unknown);
        }

        var current = await SessionApi.RefreshOk(Client, login.RefreshToken);
        _ = await SessionApi.RefreshOk(Client, current.RefreshToken);
        using (var logout = await SessionApi.Logout(Client, current.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        using (var revoked = await SessionApi.Refresh(Client, current.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(revoked);
        }

        Assert.Empty(await AuditAsync(AuditKinds.RefreshReuseDetected));
    }

    [Fact]
    public async Task The_handler_does_not_change_what_the_refresh_answers()   // spec 0002 stays as it is
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        var next = await SessionApi.RefreshOk(Client, login.RefreshToken);
        Clock.Advance(TimeSpan.FromSeconds(20));
        using var replay = await SessionApi.Refresh(Client, login.RefreshToken);
        using var afterReplay = await SessionApi.Refresh(Client, next.RefreshToken);

        await SessionApi.AssertInvalidGrantAsync(replay);
        await SessionApi.AssertInvalidGrantAsync(afterReplay);   // the family died with the replay, as before
    }
}
```

`tests/Auth.IntegrationTests/AuditRateLimitTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class AuditRateLimitTests : AuditTestBase
{
    public AuditRateLimitTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(RateLimitSettings.EnabledKey, "true").WithSetting(RateLimitSettings.PermitKey(RatePolicy.Login), "3");
    }

    private Task<HttpResponseMessage> UnknownLogin(int number, string remote) =>
        RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/login", remote, null, $$"""{"email":"nobody-{{number}}@example.test","password":"Wrong-Password-1"}""");

    private async Task ExhaustAsync(string remote, int first)
    {
        for (var i = 0; i < 3; i++)
        {
            using var answered = await UnknownLogin(first + i, remote);
            Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
        }
    }

    [Fact]
    public async Task A_refusal_is_recorded_once_per_address_and_policy_per_minute()   // criterion 8
    {
        await ExhaustAsync("203.0.113.9", 1);

        for (var i = 4; i <= 9; i++)
        {
            using var refused = await UnknownLogin(i, "203.0.113.9");
            await RateLimitApi.AssertTooManyRequestsAsync(refused);
        }

        var row = await SingleAsync(AuditKinds.RateLimitHit);   // six refusals, one row
        Assert.Equal("203.0.113.9", row.ClientIp);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.SubjectUserId);
        Assert.Equal("login", AuditApi.Text(row, "policy"));
        Assert.Equal("3", AuditApi.Text(row, "limit"));

        Clock.Advance(TimeSpan.FromSeconds(61));   // a minute later the window is empty and the next flood is its own event
        await ExhaustAsync("203.0.113.9", 20);
        using var again = await UnknownLogin(30, "203.0.113.9");
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);

        Assert.Equal(2, (await AuditAsync(AuditKinds.RateLimitHit)).Count);
    }

    [Fact]
    public async Task Another_address_and_another_policy_are_events_of_their_own()
    {
        await ExhaustAsync("203.0.113.9", 1);
        using (var first = await UnknownLogin(4, "203.0.113.9"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        }

        await ExhaustAsync("203.0.113.10", 10);
        using (var second = await UnknownLogin(14, "203.0.113.10"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        }

        var rows = await AuditAsync(AuditKinds.RateLimitHit);
        Assert.Equal(["203.0.113.10", "203.0.113.9"], rows.Select(r => r.ClientIp).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_refused_request_writes_no_login_row_for_it()   // nothing downstream ran
    {
        await ExhaustAsync("203.0.113.9", 1);
        var before = (await AuditAsync(AuditKinds.LoginFailed)).Count;

        using var refused = await UnknownLogin(4, "203.0.113.9");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(before, (await AuditAsync(AuditKinds.LoginFailed)).Count);
    }

    [Fact]
    public async Task A_failing_audit_write_never_fails_the_request()   // spec 0008: the write of a rate-limit hit never fails the request
    {
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));
        await ExhaustAsync("203.0.113.9", 1);   // the login rows fail to write too, and the logins still answer

        using var refused = await UnknownLogin(4, "203.0.113.9");

        await RateLimitApi.AssertTooManyRequestsAsync(refused);
    }
}
```

`tests/Auth.IntegrationTests/AuditClientAddressTests.cs` (criterion 2: the recorded address, with and without a trusted proxy):

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Network;

namespace Auth.IntegrationTests;

public sealed class AuditClientAddressTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private Task<HttpResponseMessage> WrongLogin(string remote, string? forwardedFor) =>
        RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/login", remote, forwardedFor, """{"email":"nobody@example.test","password":"Wrong-Password-1"}""");

    [Fact]
    public async Task With_no_trusted_proxy_the_recorded_address_is_the_connections_not_the_header()   // criterion 2
    {
        using var response = await WrongLogin("203.0.113.9", "198.51.100.77");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal("203.0.113.9", (await SingleAsync(AuditKinds.LoginFailed)).ClientIp);
    }

    [Fact]
    public async Task An_ipv4_address_mapped_into_ipv6_is_recorded_as_the_ipv4_address()
    {
        using var response = await WrongLogin("::ffff:203.0.113.9", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal("203.0.113.9", (await SingleAsync(AuditKinds.LoginFailed)).ClientIp);
    }

    [Fact]
    public async Task A_request_with_no_address_is_recorded_as_unknown()
    {
        using var response = await LoginApi.Login(Client, "nobody@example.test", "Wrong-Password-1");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal("unknown", (await SingleAsync(AuditKinds.LoginFailed)).ClientIp);
    }
}

public sealed class AuditClientAddressBehindProxyTests : AuditTestBase
{
    public AuditClientAddressBehindProxyTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(ProxySettings.KnownNetworksKey + ":0", "10.250.0.0/24");
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_the_whole_client_address_is_recorded_and_what_the_client_wrote_is_not()   // criterion 2
    {
        using var response = await RateLimitApi.SendAsync(
            Client, HttpMethod.Post, "/auth/login", "10.250.0.2", "9.9.9.9, 2001:db8:1:2::77",
            """{"email":"nobody@example.test","password":"Wrong-Password-1"}""");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal("2001:db8:1:2::77", (await SingleAsync(AuditKinds.LoginFailed)).ClientIp);   // the whole address, though the limiter counts the /64
    }
}
```

`tests/Auth.IntegrationTests/RateLimitAuditTests.cs`:

```csharp
using Auth.Server.RateLimiting;
using Microsoft.Extensions.Time.Testing;

namespace Auth.IntegrationTests;

public sealed class RateLimitAuditTests
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(1_767_225_600));
    private readonly RateLimitAudit _audit;

    public RateLimitAuditTests() => _audit = new RateLimitAudit(_clock);

    [Fact]
    public void An_address_and_policy_are_recorded_once_a_minute()
    {
        Assert.True(_audit.ShouldRecord("203.0.113.9", RatePolicy.Login));
        Assert.False(_audit.ShouldRecord("203.0.113.9", RatePolicy.Login));
        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(_audit.ShouldRecord("203.0.113.9", RatePolicy.Login));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_audit.ShouldRecord("203.0.113.9", RatePolicy.Login));
    }

    [Fact]
    public void Another_address_or_policy_is_its_own_event()
    {
        Assert.True(_audit.ShouldRecord("203.0.113.9", RatePolicy.Login));
        Assert.True(_audit.ShouldRecord("203.0.113.10", RatePolicy.Login));
        Assert.True(_audit.ShouldRecord("203.0.113.9", RatePolicy.Refresh));
    }

    [Fact]
    public void Old_entries_are_forgotten_so_that_memory_stays_bounded()
    {
        for (var i = 0; i < 500; i++)
        {
            Assert.True(_audit.ShouldRecord("198.51.100." + (i % 250) + "/" + i, RatePolicy.General));
        }

        Assert.Equal(500, _audit.Count);

        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(_audit.ShouldRecord("203.0.113.9", RatePolicy.Login));

        Assert.Equal(1, _audit.Count);
    }

    [Fact]
    public void Requests_that_arrive_together_record_one_event()
    {
        var recorded = 0;

        Parallel.For(0, 200, _ =>
        {
            if (_audit.ShouldRecord("203.0.113.9", RatePolicy.Login))
            {
                Interlocked.Increment(ref recorded);
            }
        });

        Assert.Equal(1, recorded);
    }
}
```

- [ ] **Step 2: Run the tests and see them fail.** `"C:/Program Files/dotnet/dotnet.exe" build -warnaserror`. Expected: the build fails (`RateLimitAudit` is not defined).

- [ ] **Step 3: Implement.**

`src/Auth.Server/RateLimiting/RateLimitAudit.cs`:

```csharp
using System.Collections.Concurrent;

namespace Auth.Server.RateLimiting;

/// <summary>
/// Decides which refusals of the limiter are written to the audit log: at most one per client address and policy per minute, so
/// that a flood cannot fill the table. In memory, like the limiter; entries older than a minute are forgotten once a minute.
/// </summary>
public sealed class RateLimitAudit(TimeProvider clock)
{
    private const long WindowMilliseconds = 60_000;

    private readonly ConcurrentDictionary<(string Address, RatePolicy Policy), long> _recorded = new();
    private long _lastSweep = clock.GetUtcNow().ToUnixTimeMilliseconds();

    public int Count => _recorded.Count;

    /// <summary><see langword="true"/> for the first refusal of this address and policy in the last minute.</summary>
    public bool ShouldRecord(string address, RatePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(address);

        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        SweepIfDue(now);
        var key = (address, policy);
        while (true)
        {
            if (_recorded.TryGetValue(key, out var seen))
            {
                if (now - seen < WindowMilliseconds)
                {
                    return false;
                }

                if (_recorded.TryUpdate(key, now, seen))
                {
                    return true;
                }
            }
            else if (_recorded.TryAdd(key, now))
            {
                return true;
            }
        }
    }

    private void SweepIfDue(long now)
    {
        var last = Interlocked.Read(ref _lastSweep);
        if (now - last < WindowMilliseconds || Interlocked.CompareExchange(ref _lastSweep, now, last) != last)
        {
            return;
        }

        foreach (var (key, seen) in _recorded)
        {
            if (now - seen >= WindowMilliseconds)
            {
                _recorded.TryRemove(new KeyValuePair<(string Address, RatePolicy Policy), long>(key, seen));
            }
        }
    }
}
```

`src/Auth.Server/RateLimiting/RateLimitMiddleware.cs` (the whole file):

```csharp
using Auth.Server.Audit;
using Auth.Server.Network;

namespace Auth.Server.RateLimiting;

/// <summary>
/// Counts every request under <c>/auth/</c> against the policy of its method and path, per client address, and answers
/// <c>429</c> over the limit without any other work: nothing downstream runs, so no password is evaluated and no lockout streak
/// changes. After the forwarded-headers middleware (the address is the real one) and before authentication (login is OpenIddict's,
/// inside it). A refusal is written to the audit log, at most once per address and policy per minute, before the answer is sent;
/// the write never fails the request.
/// </summary>
public sealed class RateLimitMiddleware(RequestDelegate next, SlidingWindowLimiter limiter, RateLimitSettings settings, RateLimitAudit audit)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (settings.Enabled && RatePolicies.Classify(context.Request.Method, context.Request.Path) is { } policy)
        {
            var limit = settings.PermitPerMinute(policy);
            var decision = limiter.TryAcquire(policy, ClientAddress.PartitionOf(context), limit);
            if (!decision.Allowed)
            {
                if (audit.ShouldRecord(ClientAddress.Text(context), policy))
                {
                    await context.RequestServices.GetRequiredService<AuditLog>().WriteAloneAsync(
                        new AuditEntry
                        {
                            Kind = AuditKinds.RateLimitHit,
                            Details = new Dictionary<string, object?> { ["policy"] = RatePolicies.NameOf(policy), ["limit"] = limit },
                        },
                        cancellationToken: CancellationToken.None);
                }

                await new TooManyRequestsResult(decision.RetryAfterSeconds).ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }
}
```

`src/Auth.Server/Lockout/TooManyAttemptsResult.cs` — one number, computed in one place:

```diff
 public sealed class TooManyAttemptsResult(TimeSpan retryAfter) : IResult
 {
     public const string Error = "too_many_attempts";
 
+    /// <summary>The seconds the answer says to wait: the cooldown rounded up, at least 1.</summary>
+    public static long SecondsOf(TimeSpan retryAfter) => Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds));
+
     public Task ExecuteAsync(HttpContext httpContext)
     {
         ArgumentNullException.ThrowIfNull(httpContext);
 
-        var seconds = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds));
+        var seconds = SecondsOf(retryAfter);
```

`src/Auth.Server/Login/AtomicSignInResult.cs`:

```csharp
using System.Security.Claims;
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using OpenIddict.Server.AspNetCore;

namespace Auth.Server.Login;

/// <summary>
/// Issues the session of a login and writes the <c>login.succeeded</c> row in one transaction (spec 0008: a change and its row are
/// written together, or neither is). OpenIddict makes the authorization and the token entries, in several saves, on the request's own
/// <see cref="AuthDbContext"/>; the transaction opened here holds all of them and the row. The body of the answer is held in memory
/// until the transaction has committed, so that no token leaves the server for a session that was rolled back; on any failure the
/// response is cleared (no cookie either) and the exception goes to the outermost error handler (<c>500 internal_error</c>).
/// An answer that is not a <c>200</c> (OpenIddict refused the sign-in) is passed on as it is and nothing is committed.
/// </summary>
public sealed class AtomicSignInResult(ClaimsPrincipal principal, AuditEntry entry) : IResult
{
    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var db = httpContext.RequestServices.GetRequiredService<AuthDbContext>();
        var audit = httpContext.RequestServices.GetRequiredService<AuditLog>();
        var realBody = httpContext.Response.Body;
        await using var held = new MemoryStream();
        httpContext.Response.Body = held;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(httpContext.RequestAborted);
            audit.Stage(entry);
            await Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)
                .ExecuteAsync(httpContext);
            if (httpContext.Response.StatusCode == StatusCodes.Status200OK)
            {
                await db.SaveChangesAsync(httpContext.RequestAborted);
                await transaction.CommitAsync(httpContext.RequestAborted);
            }
        }
        catch
        {
            httpContext.Response.Body = realBody;
            httpContext.Response.Clear();
            throw;
        }

        httpContext.Response.Body = realBody;
        held.Position = 0;
        await held.CopyToAsync(realBody, httpContext.RequestAborted);
    }
}
```

`src/Auth.Server/Login/LoginEndpoint.cs` — the changes (the rest of the file stays; if `using OpenIddict.Server.AspNetCore;` is reported unused once `Results.SignIn` has moved to `AtomicSignInResult`, remove that line):

```diff
+using Auth.Server.Audit;
 using Auth.Server.Account;
@@
     public static async Task<IResult> HandleAsync(
         HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens, TimeProvider clock, LoginStreakStore streaks,
-        DecoyPasswordHash decoy, MembershipReader memberships)
+        DecoyPasswordHash decoy, MembershipReader memberships, AuditLog audit)
     {
@@
         ArgumentNullException.ThrowIfNull(memberships);
+        ArgumentNullException.ThrowIfNull(audit);
@@
         var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
         if (!decision.Allowed)
         {
+            // Nothing is looked up while a cooldown runs, so the row names the address as typed (spec 0008 → Audit log).
+            await audit.WriteAloneAsync(new AuditEntry
+            {
+                Kind = AuditKinds.LoginLocked,
+                SubjectEmail = request.Username,
+                Details = new Dictionary<string, object?> { ["retry_after_seconds"] = TooManyAttemptsResult.SecondsOf(decision.RetryAfter) },
+            });
             return new TooManyAttemptsResult(decision.RetryAfter);
         }
@@
         if (user?.PasswordHash is null)
         {
             // No account, or one without a password: still pay for one verification, so this answer takes as long
             // as a wrong password does.
             _ = users.PasswordHasher.VerifyHashedPassword(user ?? new ApplicationUser(), decoy.Value, password);
+            await RecordFailureAsync(audit, "unknown_address", null, request.Username ?? string.Empty);
             return new InvalidCredentialsResult();
         }
 
         if (!await users.CheckPasswordAsync(user, password))
         {
+            await RecordFailureAsync(audit, "wrong_password", user, request.Username ?? string.Empty);
             return new InvalidCredentialsResult();
         }
@@
         if (!user.EmailConfirmed)
         {
+            await RecordFailureAsync(audit, "unconfirmed_address", user, request.Username ?? string.Empty);
             return AccountResults.EmailNotVerified();
         }
@@
         if (await memberships.ReadAsync(user.Id, http.RequestAborted) is not { } tenant)
         {
+            await RecordFailureAsync(audit, "no_company", user, request.Username ?? string.Empty);
             return AccountResults.NoMembership();
         }
@@
         http.Items[RefreshCookie.LifetimeItemKey] = SessionPolicy.SlidingLifetime;
 
-        return Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
+        // The session and its row are one change: AtomicSignInResult issues the one inside a transaction that also holds the other.
+        return new AtomicSignInResult(principal, new AuditEntry
+        {
+            Kind = AuditKinds.LoginSucceeded,
+            ActorUserId = user.Id,
+            SubjectUserId = user.Id,
+            SubjectEmail = user.Email,
+            OrgId = tenant.CompanyId,
+            OrgName = tenant.CompanyName,
+        });
     }
+
+    /// <summary>
+    /// A failed login: the account as stored when there is one, else the address as typed. One insert whatever the reason, so the
+    /// answers of an unknown address and of a wrong password stay as slow as each other.
+    /// </summary>
+    private static Task RecordFailureAsync(AuditLog audit, string reason, ApplicationUser? user, string typedEmail) =>
+        audit.WriteAloneAsync(new AuditEntry
+        {
+            Kind = AuditKinds.LoginFailed,
+            SubjectUserId = user?.Id,
+            SubjectEmail = user?.Email ?? typedEmail,
+            Details = new Dictionary<string, object?> { ["reason"] = reason },
+        });
```

`src/Auth.Server/Sessions/LogoutEndpoint.cs` — the changes:

```diff
+using Auth.Infrastructure.Persistence;
+using Auth.Server.Audit;
 using Microsoft.Net.Http.Headers;
@@
-    public static async Task<IResult> HandleAsync(HttpContext http, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations)
+    public static async Task<IResult> HandleAsync(
+        HttpContext http, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, AuthDbContext db, AuditLog audit)
     {
         ArgumentNullException.ThrowIfNull(http);
         ArgumentNullException.ThrowIfNull(tokens);
         ArgumentNullException.ThrowIfNull(authorizations);
+        ArgumentNullException.ThrowIfNull(db);
+        ArgumentNullException.ThrowIfNull(audit);

         if (RefreshCookie.TryRead(http.Request, out var reference))
         {
-            await RevokeFamilyAsync(reference, tokens, authorizations, http.RequestAborted);
+            // The revocation and the row of the logout are one transaction (spec 0008: a change and its row are written together, or
+            // neither is). OpenIddict's stores use the request's AuthDbContext, so their saves join it. If the row cannot be written the
+            // logout is a 500 and the session is still there.
+            await using var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
+            var subject = await RevokeFamilyAsync(reference, tokens, authorizations, http.RequestAborted);
+            if (Guid.TryParse(subject, out var userId))
+            {
+                // The account the cookie's session belonged to, from the token store; a logout with no valid cookie writes nothing.
+                audit.Stage(new AuditEntry { Kind = AuditKinds.Logout, ActorUserId = userId, SubjectUserId = userId });
+                await db.SaveChangesAsync(http.RequestAborted);
+            }
+
+            await transaction.CommitAsync(http.RequestAborted);
         }
@@
-    /// Revokes the authorization the token belongs to, and every token under it. A consumed token of the family
-    /// counts too: a stale tab logging out must still end the session.
+    /// Revokes the authorization the token belongs to, and every token under it. A consumed token of the family
+    /// counts too: a stale tab logging out must still end the session. Returns the account of the session when the token was a
+    /// refresh token that had not been revoked yet, so that the logout can be recorded; <see langword="null"/> otherwise.
     /// </summary>
-    private static async Task RevokeFamilyAsync(
+    private static async Task<string?> RevokeFamilyAsync(
         string reference, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, CancellationToken cancellationToken)
     {
         var token = await tokens.FindByReferenceIdAsync(reference, cancellationToken);
         if (token is null || !await tokens.HasTypeAsync(token, TokenTypeIdentifiers.RefreshToken, cancellationToken))
         {
-            return;
+            return null;
         }

+        var subject = await tokens.HasStatusAsync(token, Statuses.Revoked, cancellationToken)
+            ? null
+            : await tokens.GetSubjectAsync(token, cancellationToken);
         var family = await tokens.GetAuthorizationIdAsync(token, cancellationToken);
         if (string.IsNullOrEmpty(family))
         {
             await tokens.TryRevokeAsync(token, cancellationToken);
-            return;
+            return subject;
         }
@@
         await tokens.RevokeByAuthorizationIdAsync(family, cancellationToken);
+        return subject;
     }
```

`src/Auth.Server/Sessions/RefreshReuseAuditHandler.cs`:

```csharp
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Sessions;

/// <summary>
/// Records <c>refresh.reuse_detected</c> (spec 0008 → Audit log). OpenIddict decides a reuse in <c>ValidateTokenEntry</c>, where a
/// rejection ends the handlers after it, so this handler runs just before and asks the same question: the entry is a refresh
/// token, it has been redeemed, and the redemption is older than the reuse leeway. Inside the leeway an honest retry is let
/// through and nothing is recorded; once OpenIddict has revoked the family the status is <c>revoked</c>, so a repeated replay does
/// not record again. It changes nothing about the answer and fails only as the token lookup itself would.
/// </summary>
public sealed class RefreshReuseAuditHandler(
    IOpenIddictTokenManager tokens, TimeProvider clock, AuditLog audit, MembershipReader memberships)
    : IOpenIddictServerHandler<OpenIddictServerEvents.ValidateTokenContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ValidateTokenContext>()
            .UseScopedHandler<RefreshReuseAuditHandler>()
            .SetOrder(OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor.Order - 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public async ValueTask HandleAsync(OpenIddictServerEvents.ValidateTokenContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrEmpty(context.TokenId))
        {
            return;
        }

        var token = await tokens.FindByIdAsync(context.TokenId, context.CancellationToken);
        if (token is null
            || !await tokens.HasTypeAsync(token, TokenTypeIdentifiers.RefreshToken, context.CancellationToken)
            || !await tokens.HasStatusAsync(token, Statuses.Redeemed, context.CancellationToken)
            || await tokens.GetRedemptionDateAsync(token, context.CancellationToken) is not { } redeemedAt
            || redeemedAt + SessionPolicy.ReuseLeeway > clock.GetUtcNow())
        {
            return;
        }

        var subject = Guid.TryParse(await tokens.GetSubjectAsync(token, context.CancellationToken), out var userId) ? userId : (Guid?)null;
        var tenant = subject is { } id ? await memberships.ReadAsync(id, context.CancellationToken) : null;
        await audit.WriteAloneAsync(new AuditEntry
        {
            Kind = AuditKinds.RefreshReuseDetected,
            SubjectUserId = subject,
            OrgId = tenant?.CompanyId,
            OrgName = tenant?.CompanyName,
        });
    }
}
```

`src/Auth.Server/Tokens/OpenIddictSetup.cs`:

```diff
                 options.AddEventHandler(RefreshTokenIssuanceHandler.Descriptor)
                     .AddEventHandler(AccessTokenClaimFilter.Descriptor)
-                    .AddEventHandler(SessionResponseHandler.Descriptor);
+                    .AddEventHandler(SessionResponseHandler.Descriptor)
+                    // Spec 0008: records a refresh token that is presented again after its leeway, just before OpenIddict revokes the family.
+                    .AddEventHandler(RefreshReuseAuditHandler.Descriptor);
```

`src/Auth.Server/Email/MailRequestStore.cs`:

```diff
+using Auth.Server.Audit;
 using Auth.Server.Lockout;
@@
-    public async Task<MailLimitDecision> SubmitAsync(MailKind kind, string normalizedEmail, CancellationToken cancellationToken)
+    /// <param name="typedEmail">
+    /// The address as it was typed. A password reset request is recorded in the audit log with it, in the same transaction as the
+    /// queued request, and with no account: the request does not look the account up (spec 0004).
+    /// </param>
+    public async Task<MailLimitDecision> SubmitAsync(
+        MailKind kind, string normalizedEmail, CancellationToken cancellationToken, string? typedEmail = null)
     {
@@
             db.MailRequests.Add(new MailRequest { Kind = kind, NormalizedEmail = normalizedEmail, RequestedAt = now, NextAttemptAt = now });
+            if (kind == MailKind.PasswordReset && typedEmail is not null)
+            {
+                scope.ServiceProvider.GetRequiredService<AuditLog>().Stage(
+                    new AuditEntry { Kind = AuditKinds.PasswordResetRequested, SubjectEmail = typedEmail });
+            }
+
             await db.SaveChangesAsync(cancellationToken);
```

`src/Auth.Server/Account/MailRequestEndpoint.cs`:

```diff
-        var decision = await requests.SubmitAsync(kind, normalized, http.RequestAborted);
+        var decision = await requests.SubmitAsync(kind, normalized, http.RequestAborted, typedEmail: fields[0]);
```

`src/Auth.Server/Account/ResetPasswordEndpoint.cs`:

```diff
+using Auth.Server.Audit;
 using Auth.Server.Email;
@@
     public static async Task<IResult> HandleAsync(
         HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users,
-        IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, TimeProvider clock)
+        IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, TimeProvider clock, AuditLog audit)
     {
@@
         ArgumentNullException.ThrowIfNull(clock);
+        ArgumentNullException.ThrowIfNull(audit);
@@
         await AccountSessions.EndAllAsync(db, tokens, authorizations, user, cancellationToken);
 
+        // The row is written by the save inside this transaction: the reset and its row, both or neither. Who, never the link or the password.
+        audit.Stage(new AuditEntry { Kind = AuditKinds.PasswordReset, SubjectUserId = user.Id, SubjectEmail = user.Email });
+        await db.SaveChangesAsync(CancellationToken.None);
+
         // Not the request's token: a client that goes away now must not leave the outcome open.
```

`src/Auth.Server/Account/VerifyEmailEndpoint.cs`:

```diff
+using Auth.Server.Audit;
 using Auth.Server.Email;
@@
-    public static async Task<IResult> HandleAsync(
-        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, TimeProvider clock)
+    public static async Task<IResult> HandleAsync(
+        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, TimeProvider clock, AuditLog audit)
     {
@@
         ArgumentNullException.ThrowIfNull(clock);
+        ArgumentNullException.ThrowIfNull(audit);
@@
                 "Could not confirm the email: " + string.Join(", ", result.Errors.Select(e => e.Code)));
         }
 
+        audit.Stage(new AuditEntry { Kind = AuditKinds.EmailVerified, SubjectUserId = user.Id, SubjectEmail = user.Email });
+        await db.SaveChangesAsync(CancellationToken.None);
+
         // Not the request's token: a client that goes away now must not leave the outcome open.
```

`src/Auth.Server/Program.cs`:

```diff
 builder.Services.AddSingleton<SlidingWindowLimiter>();
+builder.Services.AddSingleton<RateLimitAudit>();
```

- [ ] **Step 4: Run the new tests, then the whole suite.**

```bash
"C:/Program Files/dotnet/dotnet.exe" build -warnaserror
"C:/Program Files/dotnet/dotnet.exe" test --filter-class "Auth.IntegrationTests.AuditAccountFlowsTests" --filter-class "Auth.IntegrationTests.AuditRefreshReuseTests" --filter-class "Auth.IntegrationTests.AuditRateLimitTests" --filter-class "Auth.IntegrationTests.AuditClientAddressTests" --filter-class "Auth.IntegrationTests.AuditClientAddressBehindProxyTests" --filter-class "Auth.IntegrationTests.RateLimitAuditTests"
"C:/Program Files/dotnet/dotnet.exe" test
```

  Expected: PASS. Watch the timing tests of spec 0003 (`LoginTimingTests`): they compare an unknown address with a wrong password, and each now does one more insert; they must still pass unedited. If the reuse test shows no row, the handler's order is wrong: print `RefreshReuseAuditHandler.Descriptor.Order` and `OpenIddictServerHandlers.Protection.ValidateTokenEntry.Descriptor.Order` and report; do not move the hook to another place without telling the orchestrator.

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(audit): record the login, session, reset and verification events and the rate-limit hits`.

### Task 6: The audit events of the company flows

**Files:**
- Modify: `src/Auth.Server/Audit/AuditLog.cs`, `src/Auth.Server/Tenancy/CompanyService.cs`, `src/Auth.Server/Tenancy/InvitationService.cs`, `src/Auth.Server/Tenancy/InviteAcceptance.cs`, `src/Auth.Server/Tenancy/MemberService.cs`, `src/Auth.Server/Tenancy/RoleService.cs`, `src/Auth.Server/Seeding/DevUserSeeder.cs`
- Create (tests): `tests/Auth.IntegrationTests/Infrastructure/OperatorCli.cs`
- Test: `tests/Auth.IntegrationTests/AuditCompanyFlowsTests.cs`, `AuditInvitationFlowsTests.cs`, `AuditMemberFlowsTests.cs`, `AuditRoleFlowsTests.cs`, `AuditAtomicityTests.cs`

**Interfaces:**
- Consumes: Task 4 (`AuditLog.Stage`, `AuditKinds`, `AuditEntry`), Task 5 (`AuditTestBase`, `AuditApi`), the services of spec 0005 and their transactions (`CompanyGuard.EnterAsync` returns the actor as the database has them now).
- Produces:
  - Rows of the kinds `invite.sent`, `invite.resent`, `invite.accepted`, `invite.cancelled`, `member.removed`, `member.role_changed`, `role.created`, `role.updated`, `role.deleted`, `org.created`, `org.renamed`, each written by the save that commits its change.
  - `AuditLog.CompanyNameAsync(Guid companyId, CancellationToken cancellationToken)` — `Task<string?>`, the company's name now, for the `org_name` of a change made under the company's lock.
  - `CompanyService.CreateAsync(string name, CancellationToken cancellationToken, string via = "cli")` — `via` goes into the details of `org.created` (`cli` for the operator, `seed` for the development seeder).
  - Tests: `OperatorCli.RunAsync(AuthAppFactory factory, params string[] args)` → `OperatorCli.Result(int Exit, string Out, string Error)`.
- The services take one more constructor parameter, `AuditLog audit` (they are made by the container; no test builds them by hand).

**The rows, one by one** (the columns not named are null):

| Kind | actor | subject | `subject_email` | org | target | `details` |
| --- | --- | --- | --- | --- | --- | --- |
| `org.created` | none | none | none | the new company and its name | none | `via` (`cli` or `seed`) |
| `org.renamed` | the member | none | none | the company, with the **new** name | none | `from`, `to` |
| `invite.sent`, `invite.resent` | the member (none for the CLI: `via: cli`) | none (the address may have no account) | the invited address as typed | the company | the invitation | `role` (its name) |
| `invite.cancelled` | the member | none | the invited address | the company | the invitation | `role` |
| `invite.accepted` | none (the person with the link is anonymous) | the account that joined | its address | the company | the invitation | `role` |
| `member.role_changed` | the member | the member changed | their address | the company | the **new** role | `from`, `to` (role names) |
| `member.removed` | the member | the member removed | their address | the company | none | `role` (the role they had), `forced: true` when the operator forced it |
| `role.created` | the member | none | none | the company | the role | `name`, `permissions` |
| `role.updated` | the member | none | none | the company | the role | `name`, `permissions`, and `previous_name`, `previous_permissions` |
| `role.deleted` | the member | none | none | the company | the role | `name`, `permissions` |

Every row of a change is staged just before the `SaveChangesAsync` that already commits the change, so a refusal (which returns before that save) or a failure (which rolls the transaction back) leaves no row. `AuditAtomicityTests` proves it the hard way: with the table gone the change itself fails.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/Infrastructure/OperatorCli.cs`:

```csharp
using System.Globalization;
using Auth.Server.Admin;
using Auth.Server.Tenancy;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Runs the operator's command the way the container does: the same code, the database and manifest of a test host.</summary>
public static class OperatorCli
{
    public sealed record Result(int Exit, string Out, string Error);

    public static async Task<Result> RunAsync(AuthAppFactory factory, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _ = factory.Services;   // starts the host, which migrates and seeds the database
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await AdminCli.RunAsync(
            args,
            output,
            error,
            builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Auth"] = factory.ConnectionString,
                [ManifestSettings.PathKey] = factory.ManifestPath,
            }),
            TestContext.Current.CancellationToken);
        return new Result(exit, output.ToString(), error.ToString());
    }
}
```

`tests/Auth.IntegrationTests/AuditCompanyFlowsTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Seeding;

namespace Auth.IntegrationTests;

public sealed class AuditCompanyFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    [Fact]
    public async Task A_company_made_by_the_operator_is_recorded_with_its_id_and_name()   // criterion 8
    {
        var run = await OperatorCli.RunAsync(Factory, "create-org", "--name", "Acme");
        Assert.Equal(0, run.Exit);
        var company = Guid.Parse(run.Out.Trim());

        var row = Assert.Single(await AuditAsync(AuditKinds.OrgCreated), r => r.OrgId == company);
        Assert.Equal("Acme", row.OrgName);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.SubjectUserId);
        Assert.Null(row.ClientIp);   // the CLI has no request
        Assert.Equal("cli", AuditApi.Text(row, "via"));
    }

    [Fact]
    public async Task The_development_company_is_recorded_as_made_by_the_seed()
    {
        var row = Assert.Single(await AuditAsync(AuditKinds.OrgCreated));

        Assert.Equal(DevUserSeeder.DefaultOrgName, row.OrgName);
        Assert.Equal(await DevCompanyIdAsync(), row.OrgId);
        Assert.Equal("seed", AuditApi.Text(row, "via"));
    }

    [Fact]
    public async Task A_company_that_is_refused_leaves_no_row()
    {
        var before = (await AuditAsync(AuditKinds.OrgCreated)).Count;

        var run = await OperatorCli.RunAsync(Factory, "create-org", "--name", "   ");

        Assert.Equal(1, run.Exit);
        Assert.Equal(before, (await AuditAsync(AuditKinds.OrgCreated)).Count);
    }

    [Fact]
    public async Task A_rename_is_recorded_with_the_old_and_the_new_name_the_actor_and_the_address()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();

        using var response = await RateLimitApi.SendAsync(
            Client, HttpMethod.Patch, "/auth/org", "203.0.113.5", null, """{"name":"Acme Corp"}""");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);   // no token: refused, and nothing is recorded
        Assert.Empty(await AuditAsync(AuditKinds.OrgRenamed));

        using var renamed = await TenancyApi.Send(Client, HttpMethod.Patch, "/auth/org", token, new { name = "Acme Corp" });
        await TenancyApi.AssertEmptyAsync(renamed, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.OrgRenamed);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme Corp", row.OrgName);   // the name at that moment: the new one
        Assert.Equal("Acme", AuditApi.Text(row, "from"));
        Assert.Equal("Acme Corp", AuditApi.Text(row, "to"));
        Assert.Null(row.SubjectUserId);
    }

    [Fact]
    public async Task A_rename_that_is_refused_leaves_no_row()   // criterion 8: a failed change leaves no row
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var workerToken = (await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword)).AccessToken;
        Assert.NotEqual(Guid.Empty, worker);

        using var forbidden = await TenancyApi.Send(Client, HttpMethod.Patch, "/auth/org", workerToken, new { name = "Hijacked" });
        using var invalid = await TenancyApi.Send(Client, HttpMethod.Patch, "/auth/org", token, new { name = "   " });

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty(await AuditAsync(AuditKinds.OrgRenamed));
    }
}
```

`tests/Auth.IntegrationTests/AuditInvitationFlowsTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;

namespace Auth.IntegrationTests;

public sealed class AuditInvitationFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Worker = "Worker@Acme.test";
    private const string NewPassword = "Brand-New-Passw0rd";
    private static readonly TimeSpan PastTheGap = TimeSpan.FromSeconds(61);

    private Task<HttpResponseMessage> InviteAsync(string token, string email, Guid role) =>
        TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", token, new { email, role_id = role });

    private async Task<Guid> InviteIdAsync(string email) =>
        await InDbAsync(db => db.Invites.Where(i => i.Email == email).Select(i => i.Id).SingleAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task An_invitation_sent_is_recorded_with_the_address_as_typed_the_role_and_the_invitation()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();

        using var response = await InviteAsync(token, Worker, await RoleIdAsync(company, "user"));
        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);

        var row = await SingleAsync(AuditKinds.InviteSent);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Null(row.SubjectUserId);   // the address may have no account, and is not looked up
        Assert.Equal(Worker, row.SubjectEmail);   // as typed
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(await InviteIdAsync(Worker), row.TargetId);
        Assert.Equal("user", AuditApi.Text(row, "role"));
    }

    [Fact]
    public async Task An_invitation_that_is_refused_leaves_no_row()   // criterion 8: a failed change leaves no row
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var user = await RoleIdAsync(company, "user");
        using (var first = await InviteAsync(token, Worker, user))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        }

        using var pending = await InviteAsync(token, Worker, user);                          // 409 invite_pending
        using var badDomain = await InviteAsync(token, "x@127.0x1", user);                   // 400 invalid_request
        using var unknownRole = await InviteAsync(token, "other@acme.test", Guid.NewGuid()); // 404 not_found
        using var member = await InviteAsync(token, "boss@acme.test", user);                 // 409 already_in_org

        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badDomain.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownRole.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, member.StatusCode);
        Assert.Single(await AuditAsync(AuditKinds.InviteSent));
    }

    [Fact]
    public async Task An_invitation_that_the_mail_limit_refuses_leaves_no_row_and_one_that_is_resent_is_its_own_kind()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        using (var sent = await InviteAsync(token, Worker, await RoleIdAsync(company, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        }

        var invite = await InviteIdAsync(Worker);
        using (var tooSoon = await TenancyApi.Send(Client, HttpMethod.Post, $"/auth/org/invites/{invite}/resend", token))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);   // one mail a minute
        }

        Assert.Empty(await AuditAsync(AuditKinds.InviteResent));

        Clock.Advance(PastTheGap);
        using var resent = await TenancyApi.Send(Client, HttpMethod.Post, $"/auth/org/invites/{invite}/resend", token);
        await TenancyApi.AssertEmptyAsync(resent, HttpStatusCode.Accepted);

        var row = await SingleAsync(AuditKinds.InviteResent);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(Worker, row.SubjectEmail);
        Assert.Equal(invite, row.TargetId);
        Assert.Equal("user", AuditApi.Text(row, "role"));
        Assert.Single(await AuditAsync(AuditKinds.InviteSent));
    }

    [Fact]
    public async Task A_cancelled_invitation_is_recorded_and_one_that_is_not_found_is_not()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        using (var sent = await InviteAsync(token, Worker, await RoleIdAsync(company, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        }

        var invite = await InviteIdAsync(Worker);
        using (var missing = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/invites/{Guid.NewGuid()}", token))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        Assert.Empty(await AuditAsync(AuditKinds.InviteCancelled));

        using var cancelled = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/invites/{invite}", token);
        await TenancyApi.AssertEmptyAsync(cancelled, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.InviteCancelled);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(Worker, row.SubjectEmail);
        Assert.Equal(invite, row.TargetId);
        Assert.Equal(company, row.OrgId);
    }

    [Fact]
    public async Task An_invitation_made_by_the_operator_says_so_and_has_no_actor()   // criterion 8
    {
        var company = await CreateCompanyAsync("Acme");

        var run = await OperatorCli.RunAsync(Factory, "invite", "--org", company.ToString(), "--email", Worker, "--role", "user");
        Assert.Equal(0, run.Exit);

        var row = await SingleAsync(AuditKinds.InviteSent);
        Assert.Null(row.ActorUserId);
        Assert.Equal("cli", AuditApi.Text(row, "via"));
        Assert.Null(row.ClientIp);
        Assert.Equal("user", AuditApi.Text(row, "role"));
    }

    [Fact]
    public async Task An_accepted_invitation_is_recorded_without_the_link_or_the_password_and_a_refused_one_is_not()   // criteria 8
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        using (var sent = await InviteAsync(token, Worker, await RoleIdAsync(company, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        }

        await DispatchAsync();
        var link = TokenIn(Mail.Sent[^1]);
        var invite = await InviteIdAsync(Worker);

        using (var weak = await TenancyApi.Accept(Client, link, "abc"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        }

        using (var unknown = await TenancyApi.Accept(Client, "z".PadRight(43, 'z'), NewPassword))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }

        Assert.Empty(await AuditAsync(AuditKinds.InviteAccepted));

        using var accepted = await TenancyApi.Accept(Client, link, NewPassword);
        await TenancyApi.AssertEmptyAsync(accepted, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.InviteAccepted);
        var joined = await InDbAsync(db => db.Users.Where(u => u.Email == Worker).Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Null(row.ActorUserId);   // the person who holds the link is anonymous
        Assert.Equal(joined, row.SubjectUserId);
        Assert.Equal(Worker, row.SubjectEmail);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(invite, row.TargetId);
        Assert.Equal("user", AuditApi.Text(row, "role"));
        await AssertNoSecretsAsync(link, NewPassword, "abc");
    }
}
```

`tests/Auth.IntegrationTests/AuditMemberFlowsTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;

namespace Auth.IntegrationTests;

public sealed class AuditMemberFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Worker = "worker@acme.test";

    [Fact]
    public async Task A_role_change_is_recorded_with_the_old_and_the_new_role_and_a_refused_one_is_not()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");
        var adminRole = await RoleIdAsync(company, "admin");

        using (var self = await TenancyApi.Send(Client, HttpMethod.Put, $"/auth/org/members/{admin}/role", token, new { role_id = adminRole }))
        {
            Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);   // cannot_change_self
        }

        using (var unknown = await TenancyApi.Send(Client, HttpMethod.Put, $"/auth/org/members/{worker}/role", token, new { role_id = Guid.NewGuid() }))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        Assert.Empty(await AuditAsync(AuditKinds.MemberRoleChanged));

        using var changed = await TenancyApi.Send(Client, HttpMethod.Put, $"/auth/org/members/{worker}/role", token, new { role_id = adminRole });
        await TenancyApi.AssertEmptyAsync(changed, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.MemberRoleChanged);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(worker, row.SubjectUserId);
        Assert.Equal(Worker, row.SubjectEmail);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(adminRole, row.TargetId);   // the new role
        Assert.Equal("user", AuditApi.Text(row, "from"));
        Assert.Equal("admin", AuditApi.Text(row, "to"));
    }

    [Fact]
    public async Task A_removed_member_is_recorded_with_the_role_they_had_and_a_refused_removal_is_not()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");

        using (var self = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{admin}", token))
        {
            Assert.Equal(HttpStatusCode.Conflict, self.StatusCode);
        }

        Assert.Empty(await AuditAsync(AuditKinds.MemberRemoved));

        using var removed = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", token);
        await TenancyApi.AssertEmptyAsync(removed, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.MemberRemoved);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(worker, row.SubjectUserId);
        Assert.Equal(Worker, row.SubjectEmail);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("user", AuditApi.Text(row, "role"));
        Assert.Null(AuditApi.Text(row, "forced"));
    }

    [Fact]
    public async Task The_operator_removing_the_last_manager_by_force_is_recorded_as_forced_and_by_the_cli()   // criterion 8
    {
        var (company, admin, _) = await CompanyWithAdminAsync();

        var refused = await OperatorCli.RunAsync(Factory, "remove-member", "--org", company.ToString(), "--email", "boss@acme.test");
        Assert.Equal(1, refused.Exit);   // last_manager
        Assert.Empty(await AuditAsync(AuditKinds.MemberRemoved));

        var forced = await OperatorCli.RunAsync(Factory, "remove-member", "--org", company.ToString(), "--email", "boss@acme.test", "--force");
        Assert.Equal(0, forced.Exit);

        var row = await SingleAsync(AuditKinds.MemberRemoved);
        Assert.Null(row.ActorUserId);
        Assert.Equal(admin, row.SubjectUserId);
        Assert.Equal("cli", AuditApi.Text(row, "via"));
        Assert.Equal("true", AuditApi.Text(row, "forced")?.ToLowerInvariant());
    }
}
```

`tests/Auth.IntegrationTests/AuditRoleFlowsTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;

namespace Auth.IntegrationTests;

public sealed class AuditRoleFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private Task<HttpResponseMessage> CreateAsync(string token, string name, params string[] permissions) =>
        TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/roles", token, new { name, permissions });

    [Fact]
    public async Task A_created_role_is_recorded_with_its_name_and_permissions_and_a_refused_one_is_not()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();

        using (var taken = await CreateAsync(token, "USER", "reports:read"))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);   // role_name_taken
        }

        using (var unknown = await CreateAsync(token, "Auditor", "nothing:here"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);   // unknown_permission
        }

        Assert.Empty(await AuditAsync(AuditKinds.RoleCreated));

        using var created = await CreateAsync(token, "Auditor", "reports:read", "templates:manage");
        var role = await TenancyApi.ReadCreatedAsync(created);

        var row = await SingleAsync(AuditKinds.RoleCreated);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(Guid.Parse(role.GetProperty("id").GetString()!), row.TargetId);
        Assert.Equal("Auditor", AuditApi.Text(row, "name"));
        Assert.Equal("""["reports:read","templates:manage"]""", AuditApi.Raw(row, "permissions"));
    }

    [Fact]
    public async Task A_replaced_role_is_recorded_with_what_it_was_and_a_refused_replacement_is_not()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var user = await RoleIdAsync(company, "user");

        using (var taken = await TenancyApi.Send(Client, HttpMethod.Put, $"/auth/org/roles/{user}", token, new { name = "admin", permissions = new[] { "reports:read" } }))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        }

        using (var lastManager = await TenancyApi.Send(
            Client, HttpMethod.Put, $"/auth/org/roles/{await RoleIdAsync(company, "admin")}", token, new { name = "admin", permissions = new[] { "reports:read" } }))
        {
            Assert.Equal(HttpStatusCode.Conflict, lastManager.StatusCode);   // the company would have no manager
        }

        Assert.Empty(await AuditAsync(AuditKinds.RoleUpdated));

        using var replaced = await TenancyApi.Send(Client, HttpMethod.Put, $"/auth/org/roles/{user}", token, new { name = "Staff", permissions = new[] { "templates:manage" } });
        await TenancyApi.AssertEmptyAsync(replaced, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.RoleUpdated);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(user, row.TargetId);
        Assert.Equal("Staff", AuditApi.Text(row, "name"));
        Assert.Equal("user", AuditApi.Text(row, "previous_name"));
        Assert.Contains("templates:manage", row.Details!, StringComparison.Ordinal);
        Assert.Contains("reports:approve", row.Details!, StringComparison.Ordinal);   // what the role held before
    }

    [Fact]
    public async Task A_deleted_role_is_recorded_and_a_role_in_use_is_not()   // criterion 8
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var spare = await AddRoleAsync(company, "Spare", "reports:read");
        var user = await RoleIdAsync(company, "user");
        await AddMemberAsync(company, "worker@acme.test", "user");

        using (var inUse = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/roles/{user}", token))
        {
            Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);   // role_in_use
        }

        Assert.Empty(await AuditAsync(AuditKinds.RoleDeleted));

        using var deleted = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/roles/{spare}", token);
        await TenancyApi.AssertEmptyAsync(deleted, HttpStatusCode.NoContent);

        var row = await SingleAsync(AuditKinds.RoleDeleted);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(spare, row.TargetId);
        Assert.Equal("Spare", AuditApi.Text(row, "name"));
        Assert.Equal(company, row.OrgId);
    }
}
```

`tests/Auth.IntegrationTests/AuditAtomicityTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>A change and its row are written in one transaction: with no table to write the row to, the change does not happen either.</summary>
public sealed class AuditAtomicityTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    [Fact]
    public async Task When_the_row_cannot_be_written_the_change_is_not_made()   // criterion 8: either both or neither
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var rename = await TenancyApi.Send(Client, HttpMethod.Patch, "/auth/org", token, new { name = "Acme Corp" });
        using var remove = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", token);
        using var role = await TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/roles", token, new { name = "Auditor", permissions = new[] { "reports:read" } });

        Assert.Equal(HttpStatusCode.InternalServerError, rename.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, remove.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, role.StatusCode);
        Assert.Equal("Acme", await InDbAsync(db => db.Companies.Where(c => c.Id == company).Select(c => c.Name).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == worker, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.Name == "Auditor", TestContext.Current.CancellationToken)));
    }
}
```

- [ ] **Step 2: Run the tests and see them fail.** `"C:/Program Files/dotnet/dotnet.exe" build -warnaserror && "C:/Program Files/dotnet/dotnet.exe" test --filter-class "Auth.IntegrationTests.AuditCompanyFlowsTests"`. Expected: the build fails if `OperatorCli` is missing; once it compiles, every test fails because no row is written yet.

- [ ] **Step 3: Implement.**

`src/Auth.Server/Audit/AuditLog.cs` — one method more:

```diff
     /// <summary>Adds the row to the context; the caller's own <c>SaveChangesAsync</c> writes it.</summary>
@@
+    /// <summary>The company's name now: the <c>org_name</c> of a change made under the company's lock.</summary>
+    public Task<string?> CompanyNameAsync(Guid companyId, CancellationToken cancellationToken) =>
+        db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => (string?)c.Name).FirstOrDefaultAsync(cancellationToken);
+
     /// <summary>Writes the row now. ...
```

(place it between `Stage` and `WriteAloneAsync`; the comment line shown last is the existing one.)

`src/Auth.Server/Tenancy/CompanyService.cs`:

```diff
+using Auth.Server.Audit;
 using Auth.Server.Email;
@@
-public sealed class CompanyService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, TimeProvider clock)
+public sealed class CompanyService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, TimeProvider clock, AuditLog audit)
 {
@@
-    public async Task<Outcome<Guid>> CreateAsync(string name, CancellationToken cancellationToken)
+    /// <param name="via">Who made it, for the audit log: <c>cli</c> for the operator, <c>seed</c> for the development seeder.</param>
+    public async Task<Outcome<Guid>> CreateAsync(string name, CancellationToken cancellationToken, string via = "cli")
     {
@@
-        await db.SaveChangesAsync(cancellationToken);
+        // The row is written by the same save as the company and its roles.
+        audit.Stage(new AuditEntry
+        {
+            Kind = AuditKinds.OrgCreated,
+            OrgId = company.Id,
+            OrgName = name,
+            Details = new Dictionary<string, object?> { ["via"] = via },
+        });
+        await db.SaveChangesAsync(cancellationToken);
         return Outcome.Ok(company.Id);
@@
         var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.OrgManage, cancellationToken);
         if (!entered.Succeeded)
         {
             return entered.Without;
         }
 
+        var previous = await audit.CompanyNameAsync(companyId, cancellationToken);
         await db.Companies.Where(c => c.Id == companyId)
             .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, name), cancellationToken);
+        audit.Stage(
+            new AuditEntry
+            {
+                Kind = AuditKinds.OrgRenamed,
+                OrgId = companyId,
+                OrgName = name,
+                Details = new Dictionary<string, object?> { ["from"] = previous, ["to"] = name },
+            },
+            entered.Value);
+        await db.SaveChangesAsync(cancellationToken);
         await transaction.CommitAsync(cancellationToken);
```

`src/Auth.Server/Seeding/DevUserSeeder.cs`:

```diff
-        var created = await companies.CreateAsync(name, ct);
+        var created = await companies.CreateAsync(name, ct, via: "seed");
```

`src/Auth.Server/Tenancy/InvitationService.cs`:

```diff
+using Auth.Server.Audit;
 using Auth.Infrastructure.Persistence;
@@
-public sealed class InvitationService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, TimeProvider clock)
+public sealed class InvitationService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, TimeProvider clock, AuditLog audit)
 {
@@
-        return await QueueMailAsync(companyId, inviteId, normalizedEmail, now, transaction, cancellationToken);
+        return await QueueMailAsync(
+            companyId, inviteId, normalizedEmail, now, transaction, AuditKinds.InviteSent, email, role.Name, current, cancellationToken);
     }
@@
-        return await QueueMailAsync(companyId, invite.Id, invite.NormalizedEmail, now, transaction, cancellationToken);
+        return await QueueMailAsync(
+            companyId, invite.Id, invite.NormalizedEmail, now, transaction, AuditKinds.InviteResent, invite.Email,
+            await RoleNameAsync(invite.RoleId, cancellationToken), entered.Value!, cancellationToken);
     }
@@
         await db.Invites.Where(i => i.Id == inviteId).ExecuteDeleteAsync(cancellationToken);
+        audit.Stage(
+            new AuditEntry
+            {
+                Kind = AuditKinds.InviteCancelled,
+                SubjectEmail = invite.Email,
+                OrgId = companyId,
+                OrgName = await audit.CompanyNameAsync(companyId, cancellationToken),
+                TargetId = inviteId,
+                Details = new Dictionary<string, object?> { ["role"] = await RoleNameAsync(invite.RoleId, cancellationToken) },
+            },
+            entered.Value);
+        await db.SaveChangesAsync(cancellationToken);
         await transaction.CommitAsync(cancellationToken);
@@
-    /// <summary>Applies the mail limit of the company and address and, if it lets the mail through, queues it and commits.</summary>
+    private Task<string> RoleNameAsync(Guid roleId, CancellationToken cancellationToken) =>
+        db.CompanyRoles.AsNoTracking().Where(r => r.Id == roleId).Select(r => r.Name).SingleAsync(cancellationToken);
+
+    /// <summary>
+    /// Applies the mail limit of the company and address and, if it lets the mail through, queues it, records it in the audit log
+    /// (the row is written by the same save) and commits.
+    /// </summary>
     private async Task<Outcome> QueueMailAsync(
         Guid companyId, Guid inviteId, string normalizedEmail, DateTimeOffset now,
-        IDbContextTransaction transaction, CancellationToken cancellationToken)
+        IDbContextTransaction transaction, string auditKind, string email, string roleName, Actor actor, CancellationToken cancellationToken)
     {
@@
             NextAttemptAt = now,
         });
+        audit.Stage(
+            new AuditEntry
+            {
+                Kind = auditKind,
+                SubjectEmail = email,
+                OrgId = companyId,
+                OrgName = await audit.CompanyNameAsync(companyId, cancellationToken),
+                TargetId = inviteId,
+                Details = new Dictionary<string, object?> { ["role"] = roleName },
+            },
+            actor);
         await db.SaveChangesAsync(cancellationToken);
```

(In `SendAsync` the variable `current` is `entered.Value!` and `role` is the `CompanyRole` read a few lines earlier; in `ResendAsync` `entered.Value!` is the re-read actor.)

`src/Auth.Server/Tenancy/InviteAcceptance.cs`:

```diff
+using Auth.Server.Audit;
 using Auth.Infrastructure.Identity;
@@
 public sealed class InviteAcceptance(
     AuthDbContext db, UserManager<ApplicationUser> users, IOpenIddictTokenManager tokens,
-    IOpenIddictAuthorizationManager authorizations, TimeProvider clock)
+    IOpenIddictAuthorizationManager authorizations, TimeProvider clock, AuditLog audit)
@@
         db.Memberships.Add(new Membership { UserId = account.Id, CompanyId = invite.CompanyId, RoleId = invite.RoleId, JoinedAt = now });
+        // The person who holds the link is anonymous: no actor. Who joined, where, and as what; never the link or the password.
+        audit.Stage(new AuditEntry
+        {
+            Kind = AuditKinds.InviteAccepted,
+            SubjectUserId = account.Id,
+            SubjectEmail = account.Email,
+            OrgId = invite.CompanyId,
+            OrgName = await audit.CompanyNameAsync(invite.CompanyId, cancellationToken),
+            TargetId = invite.Id,
+            Details = new Dictionary<string, object?>
+            {
+                ["role"] = await db.CompanyRoles.AsNoTracking().Where(r => r.Id == invite.RoleId).Select(r => r.Name).FirstOrDefaultAsync(cancellationToken),
+            },
+        });
         await db.SaveChangesAsync(cancellationToken);
```

`src/Auth.Server/Tenancy/MemberService.cs`:

```diff
+using Auth.Server.Audit;
 using Auth.Infrastructure.Persistence;
@@
 public sealed class MemberService(
-    AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations)
+    AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations,
+    AuditLog audit)
 {
@@
-        membership.RoleId = roleId;
+        var previousRole = await db.CompanyRoles.AsNoTracking().Where(r => r.Id == membership.RoleId).Select(r => r.Name).SingleAsync(cancellationToken);
+        membership.RoleId = roleId;
+        audit.Stage(
+            new AuditEntry
+            {
+                Kind = AuditKinds.MemberRoleChanged,
+                SubjectUserId = userId,
+                SubjectEmail = await EmailOfAsync(userId, cancellationToken),
+                OrgId = companyId,
+                OrgName = await audit.CompanyNameAsync(companyId, cancellationToken),
+                TargetId = roleId,
+                Details = new Dictionary<string, object?> { ["from"] = previousRole, ["to"] = role.Name },
+            },
+            current);
         await db.SaveChangesAsync(cancellationToken);
@@
-        db.Memberships.Remove(membership);
+        var removedRole = await db.CompanyRoles.AsNoTracking().Where(r => r.Id == membership.RoleId).Select(r => r.Name).SingleAsync(cancellationToken);
+        var details = new Dictionary<string, object?> { ["role"] = removedRole };
+        if (force)
+        {
+            details["forced"] = true;
+        }
+
+        db.Memberships.Remove(membership);
+        audit.Stage(
+            new AuditEntry
+            {
+                Kind = AuditKinds.MemberRemoved,
+                SubjectUserId = userId,
+                SubjectEmail = await EmailOfAsync(userId, cancellationToken),
+                OrgId = companyId,
+                OrgName = await audit.CompanyNameAsync(companyId, cancellationToken),
+                Details = details,
+            },
+            current);
         await db.SaveChangesAsync(cancellationToken);
@@
+    private Task<string?> EmailOfAsync(Guid userId, CancellationToken cancellationToken) =>
+        db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(cancellationToken);
+
     /// <summary>
     /// Safety rule 1, the other way round: ...
```

`src/Auth.Server/Tenancy/RoleService.cs`:

```diff
+using Auth.Server.Audit;
 using Auth.Infrastructure.Persistence;
@@
-public sealed class RoleService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, TimeProvider clock)
+public sealed class RoleService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, TimeProvider clock, AuditLog audit)
 {
@@
         db.CompanyRoles.Add(role);
         await db.SaveChangesAsync(cancellationToken);
+        // The role has its id now. The row is saved before the commit, so the role and its row are both there or neither.
+        audit.Stage(
+            new AuditEntry
+            {
+                Kind = AuditKinds.RoleCreated,
+                OrgId = companyId,
+                OrgName = await audit.CompanyNameAsync(companyId, cancellationToken),
+                TargetId = role.Id,
+                Details = new Dictionary<string, object?> { ["name"] = name, ["permissions"] = held },
+            },
+            entered.Value);
+        await db.SaveChangesAsync(cancellationToken);
         await transaction.CommitAsync(cancellationToken);
@@
-        role.Name = name;
+        var previousName = role.Name;
+        var previousPermissions = role.Permissions;
+        role.Name = name;
         role.NormalizedName = normalizedName;
         role.Permissions = held;
+        audit.Stage(
+            new AuditEntry
+            {
+                Kind = AuditKinds.RoleUpdated,
+                OrgId = companyId,
+                OrgName = await audit.CompanyNameAsync(companyId, cancellationToken),
+                TargetId = roleId,
+                Details = new Dictionary<string, object?>
+                {
+                    ["name"] = name,
+                    ["permissions"] = held,
+                    ["previous_name"] = previousName,
+                    ["previous_permissions"] = previousPermissions,
+                },
+            },
+            entered.Value);
         await db.SaveChangesAsync(cancellationToken);
@@
         db.CompanyRoles.Remove(role);
+        audit.Stage(
+            new AuditEntry
+            {
+                Kind = AuditKinds.RoleDeleted,
+                OrgId = companyId,
+                OrgName = await audit.CompanyNameAsync(companyId, cancellationToken),
+                TargetId = roleId,
+                Details = new Dictionary<string, object?> { ["name"] = role.Name, ["permissions"] = role.Permissions },
+            },
+            entered.Value);
         await db.SaveChangesAsync(cancellationToken);
```

(In each service the three-line context shown is the existing code; the usings are added at the top; `entered.Value` is the actor as the database has them under the lock, read by `CompanyGuard.EnterAsync` at the start of the transaction.)

- [ ] **Step 4: Run the new tests, then the whole suite.**

```bash
"C:/Program Files/dotnet/dotnet.exe" build -warnaserror
"C:/Program Files/dotnet/dotnet.exe" test --filter-class "Auth.IntegrationTests.AuditCompanyFlowsTests" --filter-class "Auth.IntegrationTests.AuditInvitationFlowsTests" --filter-class "Auth.IntegrationTests.AuditMemberFlowsTests" --filter-class "Auth.IntegrationTests.AuditRoleFlowsTests" --filter-class "Auth.IntegrationTests.AuditAtomicityTests"
"C:/Program Files/dotnet/dotnet.exe" test
```

  Expected: PASS. Spec 0005's tests (the parallel changes under the company lock, the invitations, the CLI) must still pass unedited; they exercise the same transactions with one more row.

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(audit): record the company, invitation, member and role changes in their transactions`.

## Day 3 — Sunday 22.02: deleting a company, the OpenAPI additions, the production images

When the day is done, a company can be deleted by the operator or by its admin, and what members and consumers see afterwards is what they see of a removed member; the OpenAPI description tells the new answers; the images are pinned and a production compose file exists. Three tasks.

### Task 7: The permission `org:delete`, the deletion service and the CLI command

**Files:**
- Create: `src/Auth.Server/Tenancy/CompanyDeletionService.cs`
- Modify: `src/Auth.Server/Tenancy/PermissionCatalog.cs`, `src/Auth.Server/Tenancy/TenancyServices.cs`, `src/Auth.Server/Tenancy/OperatorCommands.cs`, `src/Auth.Server/Admin/AdminArguments.cs`, `src/Auth.Server/Admin/AdminCli.cs`
- Modify (tests that list the catalog): `tests/Auth.IntegrationTests/InviteAcceptTests.cs`, `ManifestParserTests.cs`, `OrgEndpointTests.cs`, `OrgRoleTests.cs`, `PermissionCatalogTests.cs`, `TenantClaimsTests.cs`
- Modify (documents and scripts that list it): `scripts/e2e-tenancy.sh`, `docs/integration/python-fastapi.md`, `deploy/auth.yaml`
- Test: `tests/Auth.IntegrationTests/OrgDeletePermissionTests.cs`, `CompanyDeletionTests.cs`, `DeleteOrgCommandTests.cs`

**Interfaces:**
- Consumes: `CompanyGuard.EnterAsync`, `Actor.MayGrant`, `AuditLog` (Task 4, 6), `IOpenIddictTokenManager.RevokeBySubjectAsync`, `OperatorCli` (Task 6), `AuditTestBase`.
- Produces:
  - `PermissionCatalog.OrgDelete = "org:delete"`, and `PermissionCatalog.BuiltIn` is now `[members:manage, org:delete, org:manage, roles:manage]`. A role that holds `*` holds it; the `permissions` claim of every token whose role holds `*` gains it.
  - `Auth.Server.Tenancy.CompanyDeletionService` (scoped; `CompanyDeletionService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, AuditLog audit)`) with
    `Task<Outcome> DeleteAsync(Actor actor, Guid companyId, string confirmName, CancellationToken cancellationToken)`:
    the refusals, in the order the code makes them, are `not_found` (no such company), `permissions_changed` (a member who has lost `org:delete`), `invalid_request` (`confirmName` is not exactly the company's name, ordinal), `permission_not_held` (rule 1: a member of the company holds a permission the actor does not; the operator is not bound). On success: one transaction, under the company lock, deletes the queued mails of the company's invitations, the invitations, the memberships, the roles and the company, revokes every member's tokens and authorizations, writes `org.deleted`, and commits.
  - `OperatorCommands.DeleteOrgAsync(string org, string confirm, CancellationToken cancellationToken)` and the command `auth-server admin delete-org --org <id> --confirm <name>`: prints `Company deleted; every session of its members has ended.`; a refusal is `error: <code>` and exit code 1 (`invalid_request` when `--confirm` is not exactly the name, `not_found` for an unknown company); a malformed command is exit code 2.
  - `AdminArguments`: `DeleteOrgCommand(string Org, string Confirm)`.

**What was settled before this plan** (spec 0008 "To verify during implementation", answered by the probe and by reading the keys): the foreign keys of the company tables are `Cascade` from `CompanyRoles`, `Memberships` and `Invites` to `Companies`, and `Restrict` from `Memberships` and `Invites` to `CompanyRoles` on `(CompanyId, RoleId)`. A single `DELETE` of the company would therefore stop at the `Restrict` keys, so the service deletes in the order that satisfies them: invitations, memberships, roles, company; the first test below proves the order on PostgreSQL. `MailRequests.InviteId` is not a foreign key, so the queued mails are deleted by the service; the dispatcher would also drop them (it drops a request whose invitation is gone), but the contract says no mail of the company is sent, and deleting them makes it true at once. After the commit, a request that waited on the company lock finds no company: an invitation acceptance answers `invalid_token`, a company change answers `not_found`.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/OrgDeletePermissionTests.cs`:

```csharp
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

public sealed class OrgDeletePermissionTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    [Fact]
    public void It_is_a_fourth_built_in_permission_next_to_the_other_three()   // spec 0008 → Company deletion
    {
        Assert.Equal("org:delete", PermissionCatalog.OrgDelete);
        Assert.Equal(["members:manage", "org:delete", "org:manage", "roles:manage"], PermissionCatalog.BuiltIn.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void It_is_in_the_catalog_whether_or_not_the_manifest_lists_it_and_star_expands_to_it()
    {
        var unlisted = new PermissionCatalog(["docs:read"]);
        var listed = new PermissionCatalog(["docs:read", "org:delete"]);

        Assert.Contains("org:delete", unlisted.Permissions);
        Assert.Contains("org:delete", unlisted.Expand(["*"]));
        Assert.Equal(unlisted.Permissions, listed.Permissions);   // listing it changes nothing
        Assert.Contains("org:delete", unlisted.Listed);
    }

    [Fact]
    public void A_role_may_hold_it_by_name_without_star()
    {
        var catalog = new PermissionCatalog(["docs:read"]);

        Assert.True(catalog.Accepts("org:delete"));
        Assert.Equal(["org:delete"], catalog.Expand(["org:delete"]));
    }

    [Fact]
    public async Task The_token_of_an_admin_whose_role_holds_star_carries_it()   // the permissions claim gains org:delete
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        Assert.Contains("org:delete", AccessTokens.Array(session.AccessToken, "permissions"));
    }
}
```

`tests/Auth.IntegrationTests/CompanyDeletionTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class CompanyDeletionTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private Task<HttpResponseMessage> InviteAsync(string token, string email, Guid role) =>
        TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", token, new { email, role_id = role });

    private async Task<int> CountAsync(Func<AuthDbContext, Task<int>> count) => await InDbAsync(count);

    [Fact]
    public async Task The_company_its_roles_members_invitations_and_their_queued_mails_are_deleted_and_nothing_else()   // criterion 10, 11
    {
        var (acme, _, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var worker = await AddMemberAsync(acme, "worker@acme.test", "user");
        var (globex, globexAdmin, globexToken) = await CompanyWithAdminAsync("Globex", "boss@globex.test");
        using (var one = await InviteAsync(adminToken, "new@acme.test", await RoleIdAsync(acme, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, one.StatusCode);
        }

        using (var two = await InviteAsync(globexToken, "new@globex.test", await RoleIdAsync(globex, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, two.StatusCode);
        }

        Assert.Equal(2, await CountAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", acme.ToString(), "--confirm", "Acme");   // passes the Restrict keys on PostgreSQL

        Assert.True(run.Exit == 0, run.Error);
        Assert.Equal(0, await CountAsync(db => db.Companies.CountAsync(c => c.Id == acme, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CountAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == acme, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CountAsync(db => db.Memberships.CountAsync(m => m.CompanyId == acme, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CountAsync(db => db.Invites.CountAsync(i => i.CompanyId == acme, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));   // only the other company's mail is left

        // The other company is untouched, and the accounts stay.
        Assert.Equal(1, await CountAsync(db => db.Companies.CountAsync(c => c.Id == globex, TestContext.Current.CancellationToken)));
        Assert.Equal(2, await CountAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == globex, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Memberships.CountAsync(m => m.UserId == globexAdmin, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Invites.CountAsync(i => i.CompanyId == globex, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Users.CountAsync(u => u.Email == "boss@acme.test", TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task What_a_member_sees_is_what_a_removed_member_sees()   // criterion 10, Decision 12
    {
        var (acme, _, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        await AddMemberAsync(acme, "worker@acme.test", "user");
        var worker = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        using (var invite = await InviteAsync(adminToken, "new@acme.test", await RoleIdAsync(acme, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, invite.StatusCode);
        }

        await DispatchAsync();
        var link = TokenIn(Mail.Sent[^1]);
        using (var preview = await TenancyApi.Preview(Client, link))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        }

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", acme.ToString(), "--confirm", "Acme");
        Assert.True(run.Exit == 0, run.Error);

        using (var me = await TenancyApi.Get(Client, "/auth/me", worker.AccessToken))
        {
            await TenancyApi.AssertErrorAsync(me, HttpStatusCode.Forbidden, "permissions_changed");   // an access token held: at once
        }

        using (var refresh = await SessionApi.Refresh(Client, worker.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(refresh);   // the next refresh: the app signs the person out
        }

        using (var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword))
        {
            await TenancyApi.AssertErrorAsync(login, HttpStatusCode.Forbidden, "no_membership");
        }

        using (var preview = await TenancyApi.Preview(Client, link))
        {
            await TenancyApi.AssertErrorAsync(preview, HttpStatusCode.BadRequest, "invalid_token");   // the pending link
        }

        using (var accept = await TenancyApi.Accept(Client, link, "Brand-New-Passw0rd"))
        {
            await TenancyApi.AssertErrorAsync(accept, HttpStatusCode.BadRequest, "invalid_token");
        }
    }

    [Fact]
    public async Task A_mail_queued_for_an_invitation_of_the_company_is_never_sent()   // criterion 10
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await OperatorCli.RunAsync(Factory, "invite", "--org", company.ToString(), "--email", "new@acme.test", "--role", "user");
        Assert.Equal(0, invite.Exit);
        Assert.Equal(1, await CountAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", company.ToString(), "--confirm", "Acme");
        Assert.True(run.Exit == 0, run.Error);

        await DispatchAsync();
        Assert.Empty(Mail.Attempted);
        Assert.Equal(0, await CountAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task The_deletion_is_recorded_with_the_company_the_counts_and_the_operator_mark()   // criterion 8
    {
        var (acme, admin, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        await AddMemberAsync(acme, "worker@acme.test", "user");
        using (var invite = await InviteAsync(adminToken, "new@acme.test", await RoleIdAsync(acme, "user")))
        {
            Assert.Equal(HttpStatusCode.Accepted, invite.StatusCode);
        }

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", acme.ToString(), "--confirm", "Acme");
        Assert.True(run.Exit == 0, run.Error);

        var row = await SingleAsync(AuditKinds.OrgDeleted);
        Assert.Equal(acme, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Null(row.ActorUserId);
        Assert.Equal("cli", AuditApi.Text(row, "via"));
        Assert.Equal("2", AuditApi.Text(row, "members"));
        Assert.Equal("1", AuditApi.Text(row, "invitations"));
        Assert.NotEqual(Guid.Empty, admin);
    }
}
```

`tests/Auth.IntegrationTests/DeleteOrgCommandTests.cs`:

```csharp
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class DeleteOrgCommandTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Org = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void The_command_takes_an_org_and_the_name_to_confirm_in_any_order()
    {
        var expected = new DeleteOrgCommand(Org, "Acme sp. z o.o.");

        Assert.Equal(expected, AdminArguments.Parse(["delete-org", "--org", Org, "--confirm", "Acme sp. z o.o."]).Command);
        Assert.Equal(expected, AdminArguments.Parse(["delete-org", "--confirm=Acme sp. z o.o.", "--org=" + Org]).Command);
    }

    [Theory]
    [InlineData("delete-org")]
    [InlineData("delete-org --org " + Org)]
    [InlineData("delete-org --confirm Acme")]
    [InlineData("delete-org --org " + Org + " --confirm Acme --force")]
    [InlineData("delete-org --org " + Org + " --confirm Acme extra")]
    public void A_command_without_both_options_or_with_more_is_a_usage_error(string line)
    {
        var result = AdminArguments.Parse(line.Split(' '));

        Assert.Null(result.Command);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void The_usage_names_the_command()
    {
        Assert.Contains("delete-org --org <id> --confirm <name>", AdminArguments.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_deletes_with_the_exact_name_prints_one_line_and_touches_nothing_else()   // criterion 11
    {
        var company = await CreateCompanyAsync("Acme sp. z o.o.");
        var other = await CreateCompanyAsync("Globex");

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", company.ToString(), "--confirm", "Acme sp. z o.o.");

        Assert.Equal(AdminCli.Done, run.Exit);
        Assert.Equal("Company deleted; every session of its members has ended.", run.Out.Trim());
        Assert.Equal("", run.Error);
        var left = await InDbAsync(db => db.Companies.AsNoTracking().Select(c => c.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(company, left);
        Assert.Contains(other, left);
    }

    [Theory]
    [InlineData("acme")]          // not the same case
    [InlineData("Acme ")]         // a trailing space
    [InlineData(" Acme")]
    [InlineData("Acme Corp")]
    [InlineData("")]
    public async Task It_refuses_a_confirmation_that_is_not_exactly_the_name_and_deletes_nothing(string confirm)   // criterion 11
    {
        var company = await CreateCompanyAsync("Acme");

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", company.ToString(), "--confirm=" + confirm);

        Assert.Equal(AdminCli.Refused, run.Exit);
        Assert.Contains("error: invalid_request", run.Error, StringComparison.Ordinal);
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == company, TestContext.Current.CancellationToken)));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleted));
    }

    [Fact]
    public async Task It_refuses_a_company_that_does_not_exist_and_an_id_that_is_not_one()
    {
        var unknown = await OperatorCli.RunAsync(Factory, "delete-org", "--org", Org, "--confirm", "Acme");
        var malformed = await OperatorCli.RunAsync(Factory, "delete-org", "--org", "not-an-id", "--confirm", "Acme");

        Assert.Equal(AdminCli.Refused, unknown.Exit);
        Assert.Contains("error: not_found", unknown.Error, StringComparison.Ordinal);
        Assert.Equal(AdminCli.Refused, malformed.Exit);
        Assert.Contains("error: invalid_request", malformed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_that_is_not_understood_is_exit_code_2_and_deletes_nothing()
    {
        var company = await CreateCompanyAsync("Acme");

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", company.ToString());

        Assert.Equal(AdminCli.UsageError, run.Exit);
        Assert.Contains("usage: auth-server admin", run.Error, StringComparison.Ordinal);
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == company, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task The_operator_is_not_bound_by_the_rule_that_nobody_acts_on_a_member_who_holds_more()   // criterion 11
    {
        var company = await CreateCompanyAsync("Acme");
        await AddMemberAsync(company, "boss@acme.test", "admin");   // holds *

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", company.ToString(), "--confirm", "Acme");

        Assert.Equal(AdminCli.Done, run.Exit);
    }
}
```

The tests that list the catalog follow the permission (every list below gains `org:delete`, which sorts between `members:manage` (and `orders:read`) and `org:manage`):

```diff
--- a/tests/Auth.IntegrationTests/InviteAcceptTests.cs
@@ -311
-            ["members:manage", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
+            ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
--- a/tests/Auth.IntegrationTests/ManifestParserTests.cs
@@ -44
-            ["members:manage", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
+            ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
@@ -46
-        Assert.Equal(7, manifest.Catalog.Listed.Count);
+        Assert.Equal(8, manifest.Catalog.Listed.Count);
@@ -59
-        Assert.Equal(["docs:read", "members:manage", "org:manage", "roles:manage"], manifest.Catalog.Permissions);
+        Assert.Equal(["docs:read", "members:manage", "org:delete", "org:manage", "roles:manage"], manifest.Catalog.Permissions);
--- a/tests/Auth.IntegrationTests/OrgEndpointTests.cs
@@ -33
-            ["members:manage", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
+            ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
--- a/tests/Auth.IntegrationTests/OrgRoleTests.cs
@@ -65
-            ["*", "members:manage", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
+            ["*", "members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
@@ -125
-        Assert.Equal(["*", "members:manage", "orders:read", "org:manage", "roles:manage"], Strings(list.GetProperty("catalog")));
+        Assert.Equal(["*", "members:manage", "orders:read", "org:delete", "org:manage", "roles:manage"], Strings(list.GetProperty("catalog")));
@@ -503
-        await AddRoleAsync(everything, "caller", "members:manage", "roles:manage", "org:manage", "reports:read", "reports:approve", "templates:manage");
+        await AddRoleAsync(everything, "caller", "members:manage", "roles:manage", "org:manage", "org:delete", "reports:read", "reports:approve", "templates:manage");
--- a/tests/Auth.IntegrationTests/PermissionCatalogTests.cs
@@ -15
-            ["members:manage", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
+            ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
--- a/tests/Auth.IntegrationTests/TenantClaimsTests.cs
@@ -22
-        ["members:manage", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"];
+        ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"];
@@ -261
-        Assert.Equal(["members:manage", "orders:read", "org:manage", "reports:read", "roles:manage"], permissions);
+        Assert.Equal(["members:manage", "orders:read", "org:delete", "org:manage", "reports:read", "roles:manage"], permissions);
```

(The line numbers are those of the repository when the plan was written; find each line by its text. The test at `OrgRoleTests.cs:503` keeps its meaning: the caller holds every permission but `*`. If the full run shows another test that lists the catalog, change it the same way and name it in the hand-back.)

- [ ] **Step 2: Run the tests and see them fail.** `"C:/Program Files/dotnet/dotnet.exe" build -warnaserror`. Expected: the build fails (`PermissionCatalog.OrgDelete`, `DeleteOrgCommand` are not defined).

- [ ] **Step 3: Implement.**

`src/Auth.Server/Tenancy/PermissionCatalog.cs`:

```diff
 /// <summary>
-/// The permissions of an instance (spec 0005 → Concepts): the three built-in ones that guard the company API, plus
+/// The permissions of an instance (spec 0005 → Concepts): the four built-in ones that guard the company API, plus
 /// the ones the product declares in its manifest. A role holds permissions from the catalog, or <c>*</c>, which is
 /// all of them, now and after the catalog grows.
 /// </summary>
@@
     public const string OrgManage = "org:manage";
+
+    /// <summary>Deletes the company (<c>DELETE /auth/org</c>, spec 0008). In the catalog whether or not the manifest lists it.</summary>
+    public const string OrgDelete = "org:delete";
 
-    public static readonly IReadOnlyList<string> BuiltIn = [MembersManage, OrgManage, RolesManage];
+    public static readonly IReadOnlyList<string> BuiltIn = [MembersManage, OrgDelete, OrgManage, RolesManage];
```

`src/Auth.Server/Tenancy/CompanyDeletionService.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Auth.Server.Tenancy;

/// <summary>
/// Deletes a company (spec 0008 → Company deletion): the operator's <c>delete-org</c> and the company API's
/// <c>DELETE /auth/org</c> both call it. Everything happens under the company lock, in one transaction. The queued mails of the
/// company's invitations, the invitations, the memberships, the roles and the company are deleted in that order (the keys from
/// memberships and invitations to roles are <c>Restrict</c>, so a single delete of the company would stop at them). Every member's
/// sessions are revoked, as removing a member does; the accounts stay. A member who later presents a token is treated as a removed
/// member: <c>403 permissions_changed</c> at the company API, <c>401 invalid_grant</c> at refresh, <c>403 no_membership</c> at login.
/// </summary>
public sealed class CompanyDeletionService(
    AuthDbContext db, CompanyGuard guard, ManifestHolder manifest,
    IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, AuditLog audit)
{
    /// <summary>
    /// Refused, in this order: <c>not_found</c> (no such company), <c>permissions_changed</c> (a member who no longer holds
    /// <c>org:delete</c>), <c>invalid_request</c> (<paramref name="confirmName"/> is not exactly the company's name, compared
    /// ordinally), <c>permission_not_held</c> (safety rule 1: deleting acts on every member, so a member of the company holds a
    /// permission the actor does not; the operator is not bound).
    /// </summary>
    public async Task<Outcome> DeleteAsync(Actor actor, Guid companyId, string confirmName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(confirmName);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.OrgDelete, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
        }

        var current = entered.Value!;
        var name = await audit.CompanyNameAsync(companyId, cancellationToken);
        if (name is null || !string.Equals(name, confirmName, StringComparison.Ordinal))
        {
            return Outcome.Fail(TenancyErrors.InvalidRequest);
        }

        // Rule 1, over everyone the deletion acts on: no member of the company may hold more than the actor.
        var catalog = manifest.Current.Catalog;
        var held = await db.Memberships.AsNoTracking()
            .Where(m => m.CompanyId == companyId)
            .Join(db.CompanyRoles, m => m.RoleId, r => r.Id, (m, r) => r.Permissions)
            .ToListAsync(cancellationToken);
        if (held.Any(permissions => !current.MayGrant(permissions, catalog)))
        {
            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
        }

        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.CompanyId == companyId)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
        var invitationIds = db.Invites.Where(i => i.CompanyId == companyId).Select(i => i.Id);
        await db.MailRequests
            .Where(r => r.InviteId != null && invitationIds.Contains(r.InviteId.Value))
            .ExecuteDeleteAsync(cancellationToken);
        var invitations = await db.Invites.Where(i => i.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        await db.Memberships.Where(m => m.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        var roles = await db.CompanyRoles.Where(r => r.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        await db.Companies.Where(c => c.Id == companyId).ExecuteDeleteAsync(cancellationToken);

        // Every session of every member ends (spec 0002, Decision 11), as removing a member does.
        foreach (var member in members)
        {
            var subject = member.ToString();
            await tokens.RevokeBySubjectAsync(subject, cancellationToken);
            await authorizations.RevokeBySubjectAsync(subject, cancellationToken);
        }

        audit.Stage(
            new AuditEntry
            {
                Kind = AuditKinds.OrgDeleted,
                OrgId = companyId,
                OrgName = name,
                Details = new Dictionary<string, object?> { ["members"] = members.Count, ["invitations"] = invitations, ["roles"] = roles },
            },
            current);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }
}
```

`src/Auth.Server/Tenancy/TenancyServices.cs`:

```diff
         services.AddScoped<CompanyService>();
+        services.AddScoped<CompanyDeletionService>();
```

`src/Auth.Server/Tenancy/OperatorCommands.cs`:

```diff
 public sealed class OperatorCommands(
-    AuthDbContext db, CompanyService companies, InvitationService invitations, MemberService members, ILookupNormalizer normalizer)
+    AuthDbContext db, CompanyService companies, InvitationService invitations, MemberService members, CompanyDeletionService deletion,
+    ILookupNormalizer normalizer)
 {
@@
+    /// <summary>
+    /// Deletes a company as the operator, who is not bound by safety rule 1. <paramref name="confirm"/> must be exactly the company's
+    /// name (<c>invalid_request</c> otherwise): the command is not to be typed by accident.
+    /// </summary>
+    public async Task<Outcome> DeleteOrgAsync(string org, string confirm, CancellationToken cancellationToken)
+    {
+        ArgumentNullException.ThrowIfNull(confirm);
+
+        return IdInput.TryParse(org, out var orgId)
+            ? await deletion.DeleteAsync(Actor.Operator, orgId, confirm, cancellationToken)
+            : Outcome.Fail(TenancyErrors.InvalidRequest);
+    }
+
     /// <summary>Queues an invitation as the operator: ...
```

`src/Auth.Server/Admin/AdminArguments.cs`:

```diff
 public sealed record RemoveMemberCommand(string Org, string Email, bool Force) : AdminCommand;
+
+public sealed record DeleteOrgCommand(string Org, string Confirm) : AdminCommand;
@@
-/// Reads the arguments of the four operator commands by hand (spec 0005 → Operator CLI): <c>--name value</c> or
-/// <c>--name=value</c> in any order, and the flag <c>--force</c> on <c>remove-member</c>. Four commands do not need a
+/// Reads the arguments of the five operator commands by hand (spec 0005 → Operator CLI, spec 0008): <c>--name value</c> or
+/// <c>--name=value</c> in any order, and the flag <c>--force</c> on <c>remove-member</c>. Five commands do not need a
@@
           remove-member --org <id> --email <email> [--force]
+          delete-org    --org <id> --confirm <name>
         """;
@@
         ["remove-member"] = (["org", "email"], ["force"]),
+        ["delete-org"] = (["org", "confirm"], []),
     };
@@
                 "list-orgs" => new ListOrgsCommand(),
+                "delete-org" => new DeleteOrgCommand(values["org"], values["confirm"]),
                 _ => new RemoveMemberCommand(values["org"], values["email"], flags.Contains("force")),
```

`src/Auth.Server/Admin/AdminCli.cs`:

```diff
             case RemoveMemberCommand remove:
@@
                 await output.WriteLineAsync("Member removed; every session of the account has ended.");
                 return Done;
 
+            case DeleteOrgCommand delete:
+                var deleted = await operatorCommands.DeleteOrgAsync(delete.Org, delete.Confirm, cancellationToken);
+                if (!deleted.Succeeded)
+                {
+                    return await RefusedAsync(deleted, error);
+                }
+
+                await output.WriteLineAsync("Company deleted; every session of its members has ended.");
+                return Done;
+
             default:
```

The existing text lists, one line each:

`scripts/e2e-tenancy.sh` (find the three lines by their text):

```diff
-expect_eq "step 2: permissions" "$(val 'd["permissions"]')" "['documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:manage', 'roles:manage']"
+expect_eq "step 2: permissions" "$(val 'd["permissions"]')" "['documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:delete', 'org:manage', 'roles:manage']"
-expect_eq "step 5: permissions" "$(val 'd["permissions"]')" "['documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:manage', 'roles:manage']"
+expect_eq "step 5: permissions" "$(val 'd["permissions"]')" "['documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:delete', 'org:manage', 'roles:manage']"
-expect_eq "step 6: catalog" "$(val 'd["catalog"]')" "['*', 'documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:manage', 'roles:manage']"
+expect_eq "step 6: catalog" "$(val 'd["catalog"]')" "['*', 'documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:delete', 'org:manage', 'roles:manage']"
```

`deploy/auth.yaml`, line 3 to 4 of the comment:

```diff
-# permissions    the permissions the product's code checks. Three more are built in and always in the catalog:
-#                members:manage, roles:manage and org:manage (they guard the company API itself).
+# permissions    the permissions the product's code checks. Four more are built in and always in the catalog:
+#                members:manage, roles:manage, org:manage and org:delete (they guard the company API itself).
```

`docs/integration/python-fastapi.md`, the second bullet of step 2:

```diff
-- `members:manage`, `roles:manage` and `org:manage` are built in: they guard Auth-Core's company API, so you do not list
-  them, but a role may hold them. `"*"` stands for every permission of the catalog.
+- `members:manage`, `roles:manage`, `org:manage` and `org:delete` are built in: they guard Auth-Core's company API, so you do
+  not list them, but a role may hold them. `"*"` stands for every permission of the catalog (so the admin holds `org:delete`,
+  which lets them delete the whole company).
```

(If the continuation line of that bullet differs in the file, keep its wording and change only the names of the permissions and add the parenthesis.)

- [ ] **Step 4: Run the new tests, then the whole suite.**

```bash
"C:/Program Files/dotnet/dotnet.exe" build -warnaserror
"C:/Program Files/dotnet/dotnet.exe" test --filter-class "Auth.IntegrationTests.OrgDeletePermissionTests" --filter-class "Auth.IntegrationTests.CompanyDeletionTests" --filter-class "Auth.IntegrationTests.DeleteOrgCommandTests"
"C:/Program Files/dotnet/dotnet.exe" test
bash -n scripts/e2e-tenancy.sh
```

  Expected: PASS, and no output from `bash -n`. If `CompanyDeletionTests.The_company_its_roles...` fails with a foreign key violation, the order of the deletes is wrong for the keys of this database: report it with the constraint's name; do not weaken the keys.

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(tenancy): the org:delete permission, company deletion and the delete-org command`.

### Task 8: `DELETE /auth/org`, the new error codes and the OpenAPI additions

**Files:**
- Modify: `src/Auth.Server/Api/OrgEndpoints.cs`, `src/Auth.Server/Api/TenancyEndpoints.cs`, `src/Auth.Server/Api/AccountEndpoints.cs`, `src/Auth.Server/Api/EndpointMetadata.cs`, `src/Auth.Server/Api/OpenApiSetup.cs`, `src/Auth.Server/Tenancy/Contracts.cs`, `src/Auth.Server/Tenancy/Outcome.cs` (`TenancyErrors`)
- Modify (tests): `tests/Auth.IntegrationTests/OpenApiTests.cs`
- Test: `tests/Auth.IntegrationTests/OrgDeleteEndpointTests.cs`, `OpenApiHardeningTests.cs`

**Interfaces:**
- Consumes: Task 7 (`CompanyDeletionService.DeleteAsync`, `PermissionCatalog.OrgDelete`), `CompanyAccess.AuthorizeAsync`, `JsonObjectBody.ReadStringsAsync`, `LoginStreakStore.RegisterAttemptAsync/ClearAsync`, `LoginIdentifier.HashOf`, `TooManyAttemptsResult`, `AuditLog.WriteAloneAsync`, Task 3 (`JsonCharsetGuard`), Task 2 (`ErrorHandlingMiddleware.TemporarilyUnavailable`).
- Produces:
  - `DELETE /auth/org` with `{"name", "password"}` (record `DeleteOrgRequest(string Name, string Password)`), answering as the table of the spec, checks in the order permission, body, lockout, password, name, rule 1; `TenancyErrors.WrongPassword = "wrong_password"` (status `403`).
  - Rows `org.delete_refused` (`details.reason`: `wrong_password` or `locked`) written on their own; `org.deleted` is the service's (Task 7).
  - OpenAPI: `DELETE /auth/org`; `429 too_many_requests` with `Retry-After` on **every** operation (the two paths the description adds by hand included); `415 unsupported_media_type` on every operation that reads a JSON body; `503 temporarily_unavailable` with `Retry-After` on refresh; the permission `org:delete` named in the document's description and in the operation's.
  - `EndpointMetadata.RetryAfterDescription` (the one text of the `Retry-After` headers) and `ProducesTemporarilyUnavailable()`.

**Decisions of this task.**
- A company that has vanished between the check of the token and the lock (a second deletion that was waiting on the lock) is `not_found` inside the service. The endpoint answers it `403 permissions_changed`, which the table has: the caller's membership is gone with the company. The table has no `404` for this endpoint.
- The caller's identifier for the lockout is the normalised address of their account: the same key as login's, so wrong passwords here and failed logins add up in one streak, and a correct password here ends it (the password is evaluated once, as login does it).
- `wrong_password` is `403`, not `401`: the caller is signed in and authorised; a `401` would make the Angular sample's interceptor refresh and retry.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/OrgDeleteEndpointTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class OrgDeleteEndpointTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Boss = "boss@acme.test";

    private Task<HttpResponseMessage> DeleteAsync(string token, object? body) =>
        TenancyApi.Send(Client, HttpMethod.Delete, "/auth/org", token, body);

    private Task<HttpResponseMessage> DeleteAsync(string token, string name = "Acme", string? password = null) =>
        DeleteAsync(token, new { name, password = password ?? UserPassword });

    private Task<int> CompaniesAsync(Guid company) =>
        InDbAsync(db => db.Companies.CountAsync(c => c.Id == company, TestContext.Current.CancellationToken));

    private Task<int> StreaksAsync() =>
        InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task The_admin_with_the_name_and_the_password_deletes_the_company_204()   // criterion 10
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        await AddMemberAsync(company, "worker@acme.test", "user");

        using var response = await DeleteAsync(token);

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(0, await CompaniesAsync(company));
        var row = await SingleAsync(AuditKinds.OrgDeleted);
        Assert.Equal(admin, row.ActorUserId);   // by a member, not by the operator
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Null(AuditApi.Text(row, "via"));
    }

    [Fact]
    public async Task What_a_member_sees_afterwards_is_what_a_removed_member_sees()   // criterion 10
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        Assert.NotEqual(Guid.Empty, worker);

        using (var response = await DeleteAsync(token))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var me = await TenancyApi.Get(Client, "/auth/me", session.AccessToken))
        {
            await TenancyApi.AssertErrorAsync(me, HttpStatusCode.Forbidden, "permissions_changed");
        }

        using (var refresh = await SessionApi.Refresh(Client, session.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(refresh);
        }

        using (var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword))
        {
            await TenancyApi.AssertErrorAsync(login, HttpStatusCode.Forbidden, "no_membership");
        }

        using var again = await DeleteAsync(token);   // the deleting admin's own token is just as stale
        await TenancyApi.AssertErrorAsync(again, HttpStatusCode.Forbidden, "permissions_changed");
    }

    [Fact]
    public async Task A_caller_without_the_permission_is_403_forbidden_before_anything_else_is_looked_at()   // table row 2
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        await AddMemberAsync(company, "worker@acme.test", "user");
        var workerToken = (await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword)).AccessToken;

        using var wrongEverything = await DeleteAsync(workerToken, name: "Wrong name", password: "Wrong-Password-1");
        using var noBody = await TenancyApi.Send(Client, HttpMethod.Delete, "/auth/org", workerToken);

        await TenancyApi.AssertErrorAsync(wrongEverything, HttpStatusCode.Forbidden, "forbidden");
        await TenancyApi.AssertErrorAsync(noBody, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(0, await StreaksAsync());   // nothing was counted
    }

    [Fact]
    public async Task A_caller_whose_token_no_longer_matches_the_database_is_403_permissions_changed()   // table row 2
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var plain = await RoleIdAsync(company, "user");
        await AddMemberAsync(company, "second@acme.test", "admin");   // so that the demotion leaves a manager
        await SetMemberRoleAsync(admin, plain);

        using var response = await DeleteAsync(token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
        Assert.Equal(1, await CompaniesAsync(company));
    }

    [Fact]
    public async Task Without_a_token_it_is_the_401_of_every_company_endpoint()
    {
        using var response = await TenancyApi.Send(Client, HttpMethod.Delete, "/auth/org", token: null, new { name = "Acme", password = UserPassword });

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"name":"Acme"}""")]
    [InlineData("""{"password":"Another-Passw0rd"}""")]
    [InlineData("""{"name":"Acme","password":""}""")]
    [InlineData("""{"name":"   ","password":"Another-Passw0rd"}""")]
    [InlineData("""{"name":1,"password":"Another-Passw0rd"}""")]
    [InlineData("""{"name":"Acme","password":["Another-Passw0rd"]}""")]
    public async Task A_malformed_body_is_400_invalid_request_and_counts_for_nothing(string body)   // table row 3
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/auth/org");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal(0, await StreaksAsync());
    }

    [Fact]
    public async Task A_password_with_a_nul_character_is_a_malformed_body()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var response = await DeleteAsync(token, new { name = "Acme", password = "Another-Passw0rd" + (char)0 });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_content_type_that_is_not_json_is_400_and_one_that_is_not_utf_8_is_415()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var plain = new HttpRequestMessage(HttpMethod.Delete, "/auth/org");
        plain.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        plain.Content = new StringContent("""{"name":"Acme","password":"x"}""", System.Text.Encoding.UTF8, "text/plain");
        using var plainResponse = await Client.SendAsync(plain);

        using var utf16 = new HttpRequestMessage(HttpMethod.Delete, "/auth/org");
        utf16.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        utf16.Content = new StringContent("""{"name":"Acme","password":"x"}""", System.Text.Encoding.UTF8);
        utf16.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json; charset=utf-16");
        using var utf16Response = await Client.SendAsync(utf16);

        await TenancyApi.AssertErrorAsync(plainResponse, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, utf16Response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_is_403_wrong_password_and_is_recorded_and_the_company_stays()   // table row 5, criterion 10
    {
        var (company, admin, token) = await CompanyWithAdminAsync();

        using var response = await DeleteAsync(token, password: "Wrong-Password-1");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Equal(1, await CompaniesAsync(company));
        var row = await SingleAsync(AuditKinds.OrgDeleteRefused);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal("wrong_password", AuditApi.Text(row, "reason"));
        await AssertNoSecretsAsync("Wrong-Password-1", UserPassword);
    }

    [Fact]
    public async Task The_password_is_checked_before_the_name_and_a_wrong_name_with_the_right_password_is_400()   // the order of the table
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        using var both = await DeleteAsync(token, name: "acme", password: "Wrong-Password-1");   // wrong name and wrong password
        Clock.Advance(LockoutApi.HumanPace);
        using var nameOnly = await DeleteAsync(token, name: "acme");                          // right password, name differs in case
        Clock.Advance(LockoutApi.HumanPace);
        using var trailing = await DeleteAsync(token, name: "Acme ");

        await TenancyApi.AssertErrorAsync(both, HttpStatusCode.Forbidden, "wrong_password");
        await TenancyApi.AssertErrorAsync(nameOnly, HttpStatusCode.BadRequest, "invalid_request");
        await TenancyApi.AssertErrorAsync(trailing, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(1, await CompaniesAsync(company));
    }

    [Fact]
    public async Task A_wrong_password_counts_in_the_login_streak_and_ten_of_them_lock_the_identifier()   // criterion 10
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        for (var i = 0; i < 10; i++)
        {
            Clock.Advance(LockoutApi.HumanPace);   // past the burst window of the lockout
            using var wrong = await DeleteAsync(token, password: "Wrong-Password-1");
            await TenancyApi.AssertErrorAsync(wrong, HttpStatusCode.Forbidden, "wrong_password");
        }

        Clock.Advance(LockoutApi.HumanPace);
        using var locked = await DeleteAsync(token);   // even the right password is refused now

        await LockoutApi.AssertLockedAsync(locked);
        Assert.Equal(1, await CompaniesAsync(company));
        var refusals = await AuditAsync(AuditKinds.OrgDeleteRefused);
        Assert.Equal(10, refusals.Count(r => AuditApi.Text(r, "reason") == "wrong_password"));
        Assert.Equal("locked", AuditApi.Text(Assert.Single(refusals, r => AuditApi.Text(r, "reason") == "locked"), "reason"));
    }

    [Fact]
    public async Task Failed_logins_and_wrong_passwords_here_add_up_in_one_streak()
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        await LockoutApi.FailAsync(Client, Clock, Boss, 9);   // nine failed logins

        Clock.Advance(LockoutApi.HumanPace);
        using var tenth = await DeleteAsync(token, password: "Wrong-Password-1");   // the tenth attempt of the streak
        Clock.Advance(LockoutApi.HumanPace);
        using var eleventh = await DeleteAsync(token);

        await TenancyApi.AssertErrorAsync(tenth, HttpStatusCode.Forbidden, "wrong_password");
        await LockoutApi.AssertLockedAsync(eleventh);
        Assert.Equal(1, await CompaniesAsync(company));
    }

    [Fact]
    public async Task A_locked_identifier_is_429_too_many_attempts_whatever_the_password_and_nothing_is_evaluated()   // table row 4
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        await LockoutApi.FailAsync(Client, Clock, Boss, 10);

        using var response = await DeleteAsync(token);

        await LockoutApi.AssertLockedAsync(response);
        Assert.Equal(1, await CompaniesAsync(company));
        var row = await SingleAsync(AuditKinds.OrgDeleteRefused);
        Assert.Equal("locked", AuditApi.Text(row, "reason"));
        Assert.Equal(admin, row.ActorUserId);
    }

    [Fact]
    public async Task The_lockout_comes_before_the_password_and_a_malformed_body_before_the_lockout()   // the order of the table
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        await LockoutApi.FailAsync(Client, Clock, Boss, 10);

        using var locked = await DeleteAsync(token, password: "Wrong-Password-1");
        using var malformed = await DeleteAsync(token, new { name = "Acme" });

        await LockoutApi.AssertLockedAsync(locked);
        await TenancyApi.AssertErrorAsync(malformed, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_correct_password_ends_the_streak_even_when_the_name_is_wrong()   // the password is evaluated once, as login does it
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        await LockoutApi.FailAsync(Client, Clock, Boss, 4);
        Assert.Equal(1, await StreaksAsync());

        Clock.Advance(LockoutApi.HumanPace);
        using var response = await DeleteAsync(token, name: "Not the name");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, await StreaksAsync());
    }

    [Fact]
    public async Task A_member_of_the_company_who_holds_a_permission_the_caller_does_not_makes_it_403_permission_not_held()   // table row 6, rule 1
    {
        var (company, _, _) = await CompanyWithAdminAsync();   // the admin holds *
        await AddRoleAsync(company, "Deleter", "org:delete");
        await AddMemberAsync(company, "deleter@acme.test", "Deleter");
        var token = (await SessionApi.LoginAsync(Client, "deleter@acme.test", UserPassword)).AccessToken;

        using var response = await DeleteAsync(token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permission_not_held");
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleted));
    }

    [Fact]
    public async Task Rule_1_comes_after_the_name_and_the_password()
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        await AddRoleAsync(company, "Deleter", "org:delete");
        await AddMemberAsync(company, "deleter@acme.test", "Deleter");
        var token = (await SessionApi.LoginAsync(Client, "deleter@acme.test", UserPassword)).AccessToken;

        using var wrongName = await DeleteAsync(token, name: "Wrong name");
        Clock.Advance(LockoutApi.HumanPace);
        using var wrongPassword = await DeleteAsync(token, password: "Wrong-Password-1");

        await TenancyApi.AssertErrorAsync(wrongName, HttpStatusCode.BadRequest, "invalid_request");
        await TenancyApi.AssertErrorAsync(wrongPassword, HttpStatusCode.Forbidden, "wrong_password");
    }

    [Fact]
    public async Task A_caller_who_holds_org_delete_without_star_may_delete_a_company_of_lesser_members()
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        await AddRoleAsync(company, "Deleter", "org:delete", "reports:read");
        await AddMemberAsync(company, "deleter@acme.test", "Deleter");
        // the admin holds * and is a member, so the deleter cannot delete: remove them first through the operator
        var run = await OperatorCli.RunAsync(Factory, "remove-member", "--org", company.ToString(), "--email", Boss, "--force");
        Assert.Equal(0, run.Exit);
        var token = (await SessionApi.LoginAsync(Client, "deleter@acme.test", UserPassword)).AccessToken;

        using var response = await DeleteAsync(token);

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(0, await CompaniesAsync(company));
    }

    [Fact]
    public async Task Two_deletions_at_once_give_one_204_and_one_403_permissions_changed()   // the lock, and the company that has vanished
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        var responses = await Task.WhenAll(DeleteAsync(token), DeleteAsync(token));

        try
        {
            Assert.Equal(
                [HttpStatusCode.NoContent, HttpStatusCode.Forbidden],
                responses.Select(r => r.StatusCode).Order());   // 204 before 403
            var refused = responses.Single(r => r.StatusCode == HttpStatusCode.Forbidden);
            await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.Forbidden, "permissions_changed");
            Assert.Equal(0, await CompaniesAsync(company));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }
}
```

`tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

/// <summary>What spec 0008 adds to the OpenAPI description (the rest of it is in <see cref="OpenApiTests"/>).</summary>
public sealed class OpenApiHardeningTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private async Task<(JsonObject Document, Dictionary<string, JsonObject> Operations)> DescriptionAsync()
    {
        using var response = await Client.GetAsync("/auth/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        var operations = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (path, item) in document["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                operations[$"{method.ToUpperInvariant()} {path}"] = operation!.AsObject();
            }
        }

        return (document, operations);
    }

    private static string Description(JsonObject operation, string status) => operation["responses"]![status]!["description"]!.GetValue<string>();

    [Fact]
    public async Task The_company_deletion_is_described_with_its_body_and_its_answers()   // criterion 10
    {
        var (document, operations) = await DescriptionAsync();

        var operation = operations["DELETE /auth/org"];

        var schema = operation["requestBody"]!["content"]!["application/json"]!["schema"]!.AsObject();
        if (schema["$ref"] is { } reference)
        {
            schema = document["components"]!["schemas"]![reference.GetValue<string>().Split('/')[^1]]!.AsObject();
        }

        Assert.Equal(["name", "password"], schema["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["204", "400", "401", "403", "415", "429"], operation["responses"]!.AsObject().Select(r => r.Key).Order(StringComparer.Ordinal));
        Assert.All(["forbidden", "permissions_changed", "permission_not_held", "wrong_password"], code => Assert.Contains(code, Description(operation, "403")));
        Assert.Contains("invalid_request", Description(operation, "400"));
        Assert.All(["too_many_attempts", "too_many_requests"], code => Assert.Contains(code, Description(operation, "429")));
        Assert.NotNull(operation["security"]);
    }

    [Fact]
    public async Task The_permission_org_delete_is_named_in_the_description_and_in_the_operation()
    {
        var (document, operations) = await DescriptionAsync();

        Assert.Contains("org:delete", document["info"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("org:delete", operations["DELETE /auth/org"]["description"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_operation_can_say_429_too_many_requests_with_retry_after_the_two_added_by_hand_included()   // spec 0008 → OpenAPI
    {
        var (_, operations) = await DescriptionAsync();

        Assert.Equal(26, operations.Count);
        foreach (var (endpoint, operation) in operations)
        {
            Assert.Contains("too_many_requests", Description(operation, "429"));
            var header = operation["responses"]!["429"]!["headers"]!["Retry-After"]!;
            Assert.Equal("integer", header["schema"]!["type"]!.GetValue<string>());
            Assert.True(operation["responses"]!["429"]!["content"]!["application/json"]!["schema"] is not null, endpoint);
        }
    }

    [Fact]
    public async Task Exactly_the_operations_that_read_a_json_body_say_415_unsupported_media_type()   // spec 0008 → OpenAPI
    {
        var (_, operations) = await DescriptionAsync();

        foreach (var (endpoint, operation) in operations)
        {
            var reads = operation["requestBody"] is not null;
            Assert.Equal(reads, operation["responses"]!["415"] is not null);
            if (reads)
            {
                Assert.Contains("unsupported_media_type", Description(operation, "415"));
            }
        }
    }

    [Fact]
    public async Task Refresh_can_say_503_temporarily_unavailable_with_retry_after_and_no_other_endpoint_can()   // spec 0008 → OpenAPI
    {
        var (_, operations) = await DescriptionAsync();

        var refresh = operations["POST /auth/refresh"];
        Assert.Contains("temporarily_unavailable", Description(refresh, "503"));
        Assert.NotNull(refresh["responses"]!["503"]!["headers"]!["Retry-After"]);
        Assert.Equal(["POST /auth/refresh"], operations.Where(o => o.Value["responses"]!["503"] is not null).Select(o => o.Key));
    }

    [Fact]
    public async Task The_health_check_and_the_key_set_are_described_with_the_429_and_no_other_error()
    {
        var (_, operations) = await DescriptionAsync();

        foreach (var endpoint in new[] { "GET /auth/health", "GET /auth/.well-known/jwks.json" })
        {
            Assert.Equal(["200", "429"], operations[endpoint]["responses"]!.AsObject().Select(r => r.Key).Order(StringComparer.Ordinal));
        }
    }
}
```

`tests/Auth.IntegrationTests/OpenApiTests.cs` — the existing tests that list endpoints and statuses follow the description:

```diff
         "GET /auth/org", "PATCH /auth/org",
+        "DELETE /auth/org",
         "GET /auth/org/members", ...
```

```diff
-    [InlineData("POST /auth/login", "200,400,401,403,429")]
-    [InlineData("POST /auth/refresh", "200,401")]
-    [InlineData("POST /auth/logout", "204")]
-    [InlineData("POST /auth/password/forgot", "202,400,429")]
-    [InlineData("POST /auth/password/reset", "204,400")]
-    [InlineData("POST /auth/email/verify/request", "202,400,429")]
-    [InlineData("POST /auth/email/verify", "204,400")]
-    [InlineData("GET /auth/me", "200,401,403")]
-    [InlineData("POST /auth/invites/preview", "200,400,409")]
-    [InlineData("POST /auth/invites/accept", "204,400,409")]
-    [InlineData("GET /auth/org", "200,401,403")]
-    [InlineData("PATCH /auth/org", "204,400,401,403")]
-    [InlineData("GET /auth/org/members", "200,401,403")]
-    [InlineData("PUT /auth/org/members/{user_id}/role", "204,400,401,403,404,409")]
-    [InlineData("DELETE /auth/org/members/{user_id}", "204,400,401,403,404,409")]
-    [InlineData("GET /auth/org/invites", "200,401,403")]
-    [InlineData("POST /auth/org/invites", "202,400,401,403,404,409,429")]
-    [InlineData("POST /auth/org/invites/{id}/resend", "202,400,401,403,404,429")]
-    [InlineData("DELETE /auth/org/invites/{id}", "204,400,401,403,404")]
-    [InlineData("GET /auth/org/roles", "200,401,403")]
-    [InlineData("POST /auth/org/roles", "201,400,401,403,409")]
-    [InlineData("PUT /auth/org/roles/{id}", "204,400,401,403,404,409")]
-    [InlineData("DELETE /auth/org/roles/{id}", "204,400,401,403,404,409")]
+    // Every operation can also say 429 (the per-IP limit), and every one that reads a JSON body 415 (spec 0008).
+    [InlineData("POST /auth/login", "200,400,401,403,415,429")]
+    [InlineData("POST /auth/refresh", "200,401,429,503")]
+    [InlineData("POST /auth/logout", "204,429")]
+    [InlineData("POST /auth/password/forgot", "202,400,415,429")]
+    [InlineData("POST /auth/password/reset", "204,400,415,429")]
+    [InlineData("POST /auth/email/verify/request", "202,400,415,429")]
+    [InlineData("POST /auth/email/verify", "204,400,415,429")]
+    [InlineData("GET /auth/me", "200,401,403,429")]
+    [InlineData("POST /auth/invites/preview", "200,400,409,415,429")]
+    [InlineData("POST /auth/invites/accept", "204,400,409,415,429")]
+    [InlineData("GET /auth/org", "200,401,403,429")]
+    [InlineData("PATCH /auth/org", "204,400,401,403,415,429")]
+    [InlineData("DELETE /auth/org", "204,400,401,403,415,429")]
+    [InlineData("GET /auth/org/members", "200,401,403,429")]
+    [InlineData("PUT /auth/org/members/{user_id}/role", "204,400,401,403,404,409,415,429")]
+    [InlineData("DELETE /auth/org/members/{user_id}", "204,400,401,403,404,409,429")]
+    [InlineData("GET /auth/org/invites", "200,401,403,429")]
+    [InlineData("POST /auth/org/invites", "202,400,401,403,404,409,415,429")]
+    [InlineData("POST /auth/org/invites/{id}/resend", "202,400,401,403,404,429")]
+    [InlineData("DELETE /auth/org/invites/{id}", "204,400,401,403,404,429")]
+    [InlineData("GET /auth/org/roles", "200,401,403,429")]
+    [InlineData("POST /auth/org/roles", "201,400,401,403,409,415,429")]
+    [InlineData("PUT /auth/org/roles/{id}", "204,400,401,403,404,409,415,429")]
+    [InlineData("DELETE /auth/org/roles/{id}", "204,400,401,403,404,409,429")]
```

```diff
     [InlineData("POST /auth/org/invites")]
@@
     [InlineData("DELETE /auth/org/roles/{id}")]
+    [InlineData("DELETE /auth/org")]
     public async Task Each_endpoint_under_safety_rule_1_lists_permission_not_held_under_403(string endpoint)   // criterion 24
```

```diff
     [InlineData("PATCH /auth/org", "name")]
+    [InlineData("DELETE /auth/org", "name,password")]
     [InlineData("POST /auth/org/invites", "email,role_id")]
```

```diff
-        Assert.Equal(5, limited);   // login, the two mail requests, invite and resend
+        Assert.Equal(Endpoints.Length, limited);   // every operation can say 429 now (spec 0008)
```

and the same test's name no longer claims five:

```diff
-    public async Task Every_429_carries_retry_after()   // criterion 24
+    public async Task Every_429_carries_retry_after()   // criterion 24; spec 0008: on every operation
```

- [ ] **Step 2: Run the tests and see them fail.** `"C:/Program Files/dotnet/dotnet.exe" build -warnaserror`. Expected: the build is clean (the endpoint is not referenced by a test); the tests fail (`DELETE /auth/org` is a 405 today).

- [ ] **Step 3: Implement.**

`src/Auth.Server/Tenancy/Outcome.cs` (`TenancyErrors`):

```diff
     public const string UnsupportedMediaType = "unsupported_media_type";
+    public const string WrongPassword = "wrong_password";
@@
-        Forbidden or PermissionsChanged or PermissionNotHeld => StatusCodes.Status403Forbidden,
+        Forbidden or PermissionsChanged or PermissionNotHeld or WrongPassword => StatusCodes.Status403Forbidden,
```

`src/Auth.Server/Tenancy/Contracts.cs`:

```diff
 /// <summary>Request of <c>PATCH /auth/org</c>.</summary>
 public sealed record RenameOrgRequest(string Name);
+
+/// <summary>Request of <c>DELETE /auth/org</c>: the company's name, exactly, and the caller's own password.</summary>
+public sealed record DeleteOrgRequest(string Name, string Password);
```

`src/Auth.Server/Api/OrgEndpoints.cs`:

```diff
+using Auth.Server.Audit;
 using Auth.Infrastructure.Identity;
+using Auth.Server.Lockout;
 using Auth.Server.Requests;
@@
     private static readonly string[] RenameFields = ["name"];
+    private static readonly string[] DeleteFields = ["name", "password"];
@@
         var outcome = await companies.RenameAsync(Actor.Of(caller), caller.CompanyId, fields[0], http.RequestAborted);
         return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
     }
+
+    /// <summary>
+    /// <c>DELETE /auth/org</c> (spec 0008 → Company deletion). The checks run in this order: the permission <c>org:delete</c> (read from
+    /// the database), the body, the lockout of the caller's identifier, the password, the company's name, safety rule 1. The attempt
+    /// is counted first, as login counts it, and a correct password ends the streak: the password is evaluated once.
+    /// </summary>
+    public static async Task<IResult> DeleteAsync(
+        HttpContext http, MembershipReader memberships, UserManager<ApplicationUser> users, LoginStreakStore streaks,
+        CompanyDeletionService deletion, AuditLog audit)
+    {
+        ArgumentNullException.ThrowIfNull(http);
+        ArgumentNullException.ThrowIfNull(memberships);
+        ArgumentNullException.ThrowIfNull(users);
+        ArgumentNullException.ThrowIfNull(streaks);
+        ArgumentNullException.ThrowIfNull(deletion);
+        ArgumentNullException.ThrowIfNull(audit);
+
+        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.OrgDelete);
+        if (access.Caller is not { } caller)
+        {
+            return access.Failure!;
+        }
+
+        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, DeleteFields, http.RequestAborted);
+        if (fields is null || fields[1].Contains('\0'))
+        {
+            return ApiResults.InvalidRequest();
+        }
+
+        var user = await users.FindByIdAsync(caller.UserId.ToString());
+        if (user?.Email is null)
+        {
+            return ApiResults.Error(TenancyErrors.Forbidden);
+        }
+
+        var identifier = LoginIdentifier.HashOf(users.NormalizeEmail(user.Email));
+        var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
+        if (!decision.Allowed)
+        {
+            await RecordRefusalAsync(audit, caller, "locked");
+            return new TooManyAttemptsResult(decision.RetryAfter);
+        }
+
+        if (!await users.CheckPasswordAsync(user, fields[1]))
+        {
+            await RecordRefusalAsync(audit, caller, "wrong_password");
+            return ApiResults.Error(TenancyErrors.WrongPassword);
+        }
+
+        // Not the request's token: from the tenth attempt on, counting has already started a cooldown, and a client that goes away
+        // right after its correct password was verified must not be left with it (as login does).
+        await streaks.ClearAsync(identifier, CancellationToken.None);
+
+        var outcome = await deletion.DeleteAsync(Actor.Of(caller), caller.CompanyId, fields[0], http.RequestAborted);
+        if (outcome.Succeeded)
+        {
+            return ApiResults.NoContent();
+        }
+
+        // A company that has vanished while this request waited for its lock is a membership that is gone: the token no longer
+        // matches the database, which is what the contract says for that case.
+        return outcome.Error == TenancyErrors.NotFound ? ApiResults.Error(TenancyErrors.PermissionsChanged) : ApiResults.Refused(outcome);
+    }
+
+    private static Task RecordRefusalAsync(AuditLog audit, TenantContext caller, string reason) =>
+        audit.WriteAloneAsync(
+            new AuditEntry
+            {
+                Kind = AuditKinds.OrgDeleteRefused,
+                OrgId = caller.CompanyId,
+                OrgName = caller.CompanyName,
+                Details = new Dictionary<string, object?> { ["reason"] = reason },
+            },
+            Actor.Of(caller));
 }
```

`src/Auth.Server/Api/TenancyEndpoints.cs`:

```diff
             .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
             .ProducesGuarded();
+        company.MapDelete("", OrgEndpoints.DeleteAsync)
+            .WithDescription(
+                "Deletes the caller's company with its roles, members, invitations and the mails queued for them; the accounts stay. "
+                + "Needs the permission `org:delete`, the company's name exactly and the caller's own password. The members' sessions end: "
+                + "an access token still held gets `403 permissions_changed`, the next refresh `401 invalid_grant`, a new login `403 no_membership`.")
+            .ReadsJson<DeleteOrgRequest>()
+            .Produces(StatusCodes.Status204NoContent)
+            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
+            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.WrongPassword, TenancyErrors.PermissionNotHeld)
+            .ProducesTooManyAttempts()
+            .ProducesGuarded();
```

(placed after `company.MapPatch("", ...)` and before `company.MapGet("invites", ...)`.)

`src/Auth.Server/Api/EndpointMetadata.cs`:

```diff
 public static class EndpointMetadata
 {
+    /// <summary>The text of every <c>Retry-After</c> header of the description.</summary>
+    public const string RetryAfterDescription = "The seconds to wait before the next attempt: the same number as `retry_after_seconds` in the body.";
+
@@
             .WithMetadata(new ResponseHeaderMetadata(
                 StatusCodes.Status429TooManyRequests, "Retry-After", JsonSchemaType.Integer,
-                "The seconds to wait before the next attempt: the same number as `retry_after_seconds` in the body."));
+                RetryAfterDescription));
     }
+
+    /// <summary>
+    /// The <c>503 temporarily_unavailable</c> of a refresh while the database cannot be reached, and its <c>Retry-After</c>
+    /// (spec 0008): the cookie is neither cleared nor rotated, and the same cookie works once the database is back.
+    /// </summary>
+    public static RouteHandlerBuilder ProducesTemporarilyUnavailable(this RouteHandlerBuilder builder)
+    {
+        ArgumentNullException.ThrowIfNull(builder);
+
+        return builder
+            .ProducesError(StatusCodes.Status503ServiceUnavailable, ErrorHandlingMiddleware.TemporarilyUnavailable)
+            .WithMetadata(new ResponseHeaderMetadata(
+                StatusCodes.Status503ServiceUnavailable, "Retry-After", JsonSchemaType.Integer,
+                "The seconds to wait before refreshing again: 5. The cookie is neither cleared nor rotated."));
+    }
```

`src/Auth.Server/Api/AccountEndpoints.cs`:

```diff
             .SetsRefreshCookie(StatusCodes.Status200OK, RefreshCookieSet + " The token that was sent is used up.")
-            .ProducesError(StatusCodes.Status401Unauthorized, "invalid_grant");
+            .ProducesError(StatusCodes.Status401Unauthorized, "invalid_grant")
+            .ProducesTemporarilyUnavailable();
```

`src/Auth.Server/Api/OpenApiSetup.cs` — the document's description, the two paths added by hand, and the operation transformer (the whole method is replaced; `using System.Globalization;` and `using Auth.Server.Tenancy;` are added at the top):

```csharp
    private static Task DescribeDocumentAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Auth-Core",
            Version = DocumentName,
            Description = "Accounts, sessions, companies, members, roles and invitations. Errors are `{\"error\":\"<code>\"}`; "
                + "every response of the account and company endpoints is marked `Cache-Control: no-store`, and every response carries the "
                + "security headers. A request over the per-address limit is answered `429 too_many_requests` with `Retry-After`, on every "
                + "endpoint. The company API is guarded by the built-in permissions `members:manage`, `roles:manage`, `org:manage` and "
                + "`org:delete`.",
        };

        // (no server, the bearer scheme: as before)
        document.Servers = [];

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "The access token of `POST /auth/login` or `POST /auth/refresh`. The company API reads the caller's "
                + "membership and permissions from the database, not from the token.",
        };

        // Two endpoints that are not routes of ours: the key set is OpenIddict's, the health check has no metadata. Like every
        // endpoint under /auth/ they can say 429.
        document.Paths ??= [];
        document.Paths[JwksPath] = WithTooManyRequests(PathWithGet(
            "The public keys that verify the access tokens (RFC 7517).", "200", "The JSON Web Key Set.", "application/json"));
        document.Paths[AccountEndpoints.HealthPath] = WithTooManyRequests(PathWithGet(
            "Whether the service works. `Degraded` means the product's manifest file was not used and the last valid one is active. "
            + "It does not reach the database. Answers `GET` and `HEAD`.",
            "200", "`Healthy` or `Degraded`.", "text/plain"));
        return Task.CompletedTask;
    }

    private static OpenApiPathItem WithTooManyRequests(OpenApiPathItem item)
    {
        foreach (var operation in item.Operations!.Values)
        {
            var response = ErrorResponse(
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["error"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["retry_after_seconds"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                    },
                });
            response.Description = "Error: " + TenancyErrors.TooManyRequests;
            AddRetryAfter(response);
            operation.Responses!["429"] = response;
        }

        return item;
    }

    private static OpenApiResponse ErrorResponse(IOpenApiSchema schema) => new()
    {
        Description = "Error",
        Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = new OpenApiMediaType { Schema = schema } },
    };

    private static void AddRetryAfter(OpenApiResponse response)
    {
        response.Headers ??= new Dictionary<string, IOpenApiHeader>();
        response.Headers["Retry-After"] = new OpenApiHeader
        {
            Description = EndpointMetadata.RetryAfterDescription,
            Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
        };
    }

    private static async Task DescribeOperationAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;

        // The body the handler reads itself.
        if (metadata.OfType<JsonRequestMetadata>().FirstOrDefault() is { } request)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType { Schema = await context.GetOrCreateSchemaAsync(request.Body, null, cancellationToken) },
                },
            };
        }

        // The cookie the handler reads itself: refresh and logout take no body.
        foreach (var cookie in metadata.OfType<CookieRequestMetadata>())
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = cookie.Name,
                In = ParameterLocation.Cookie,
                Description = cookie.Description,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }

        // The codes of each error status, from every place that declared the status, and two answers that come from the pipeline and
        // not from a handler (spec 0008): every endpoint can say 429 too_many_requests, and every one that reads a JSON body can
        // say 415 unsupported_media_type. A status that no handler declared gets its response made here.
        var codesByStatus = metadata.OfType<ErrorCodesMetadata>()
            .GroupBy(e => e.Status)
            .ToDictionary(g => g.Key, g => g.SelectMany(e => e.Codes).ToHashSet(StringComparer.Ordinal));
        AddCode(codesByStatus, StatusCodes.Status429TooManyRequests, TenancyErrors.TooManyRequests);
        if (metadata.OfType<JsonRequestMetadata>().Any())
        {
            AddCode(codesByStatus, StatusCodes.Status415UnsupportedMediaType, TenancyErrors.UnsupportedMediaType);
        }

        operation.Responses ??= [];
        foreach (var (status, codes) in codesByStatus)
        {
            var key = status.ToString(CultureInfo.InvariantCulture);
            if (!(operation.Responses.TryGetValue(key, out var existing) && existing is OpenApiResponse response))
            {
                var schema = await context.GetOrCreateSchemaAsync(
                    status == StatusCodes.Status429TooManyRequests ? typeof(TooManyAttemptsBody) : typeof(ErrorBody), null, cancellationToken);
                response = ErrorResponse(schema);
                operation.Responses[key] = response;
            }

            response.Description = "Error: " + string.Join(", ", codes.Order(StringComparer.Ordinal));
            if (status == StatusCodes.Status429TooManyRequests)
            {
                AddRetryAfter(response);
            }
        }

        // The headers the handler sets on a response: the refresh cookie, and the Retry-After of a 429 or a 503.
        foreach (var header in metadata.OfType<ResponseHeaderMetadata>())
        {
            if (operation.Responses.TryGetValue(header.Status.ToString(CultureInfo.InvariantCulture), out var response) && response is OpenApiResponse concrete)
            {
                concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
                concrete.Headers[header.Name] = new OpenApiHeader
                {
                    Description = header.Description,
                    Schema = new OpenApiSchema { Type = header.Type },
                };
            }
        }

        if (metadata.OfType<IAuthorizeData>().Any())
        {
            operation.Security =
            [
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, null)] = [] },
            ];
        }
    }

    private static void AddCode(Dictionary<int, HashSet<string>> codesByStatus, int status, string code)
    {
        if (!codesByStatus.TryGetValue(status, out var codes))
        {
            codesByStatus[status] = codes = new HashSet<string>(StringComparer.Ordinal);
        }

        codes.Add(code);
    }
```

(`System.Net.Http.HttpMethod` is already imported by the file for `PathWithGet`; the unchanged parts of the file are `AddAuthOpenApi`, `MapAuthOpenApi` and `PathWithGet`.)

- [ ] **Step 4: Run the new tests, then the whole suite.**

```bash
"C:/Program Files/dotnet/dotnet.exe" build -warnaserror
"C:/Program Files/dotnet/dotnet.exe" test --filter-class "Auth.IntegrationTests.OrgDeleteEndpointTests" --filter-class "Auth.IntegrationTests.OpenApiHardeningTests" --filter-class "Auth.IntegrationTests.OpenApiTests" --filter-class "Auth.IntegrationTests.JsonCharsetTests"
"C:/Program Files/dotnet/dotnet.exe" test
```

  Expected: PASS. If an `OpenApiTests` status theory differs from the table above only by a status that the generator adds or drops (for example a `200` for `HEAD`), keep the contract and say so; do not paper over a missing `429`/`415`/`503`.

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(api): DELETE /auth/org and the OpenAPI description of the limits and the outage`.

### Task 9: Pinned images, the production compose file, its environment file and the development compose

**Files:**
- Modify: `src/Auth.Server/Dockerfile`, `deploy/docker-compose.yml`, `.env.example`, `.gitignore`
- Create: `deploy/docker-compose.prod.yml`, `deploy/.env.prod.example`

**Interfaces:**
- Consumes: the settings of the earlier tasks (`Auth:RateLimit:*`, `Auth:Proxy:*`, `Auth:Audit:RetentionDays`), the settings Production already requires (the four key paths, `Auth:Tokens:Issuer` and `Audience`, `Auth:Manifest:Path`, `ConnectionStrings:Auth`, the mail settings of `MailSettingsLoader`).
- Produces: the image recipe (`docker build -f src/Auth.Server/Dockerfile --build-arg VERSION=<v> --build-arg REVISION=<sha> -t ghcr.io/mckcieply/auth-core:<v> .`), `deploy/docker-compose.prod.yml` (project name `auth-core-prod`; services `postgres` and `auth`; networks `auth` and `proxy`), the variables of `deploy/.env.prod.example`, and these variables of the development compose: `AUTH_RATE_LIMIT_ENABLED`, `AUTH_RATE_LIMIT_LOGIN`, `AUTH_RATE_LIMIT_REFRESH`, `AUTH_RATE_LIMIT_EMAIL`, `AUTH_RATE_LIMIT_INVITE`, `AUTH_RATE_LIMIT_GENERAL`, `AUTH_AUDIT_RETENTION_DAYS`. Task 12 builds the test overlay and its script on this file; Task 14's guide describes it.

**Rules this task works to** (spec 0008 → Production compose): `docker-compose.prod.yml` is a file of its own, not an overlay of the development one. No secret has a default: every required variable is written `${NAME:?message}`, so that `docker compose` refuses to start without it. The images are pinned by digest (`postgres`, and the two base images of the Dockerfile); the one image that is not is Auth-Core's own, named by version. The `auth` container is as locked down as the sample's proxy is: read-only root file system, `/tmp` in memory, no capabilities, no privilege gain. `/auth/health` does not reach the database, so nothing here waits for it: the guide waits for a login or for the CLI.

**Digests** (obtained with `docker buildx imagetools inspect <image:tag>`, registry metadata only; the index digests, so every platform is covered):

| Image | Digest |
| --- | --- |
| `mcr.microsoft.com/dotnet/sdk:10.0` | `sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317` |
| `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` | `sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c` |
| `postgres:16-alpine` | `sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea` (the one `samples/notes-api/compose.yml` already uses) |
| `axllent/mailpit:v1.31.3` | `sha256:ed9b00c609e77e99c79b93f1178255ebc271868920f2c69a8d166bd5634ed10d` |

- [ ] **Step 1: The static checks that fail today.** From the repository root:

```bash
# every image of the compose files is pinned by digest, except the one the production file names by version
grep -n "image:" deploy/docker-compose.yml
# docker compose refuses the production file without its secrets (there is no deploy/docker-compose.prod.yml yet)
ls deploy/docker-compose.prod.yml
```

  Expected: the first command shows `postgres:16-alpine` and `axllent/mailpit:v1.31.3` without a digest; the second says `No such file or directory`.

- [ ] **Step 2: The Dockerfile.** Replace `src/Auth.Server/Dockerfile` with:

```dockerfile
# syntax=docker/dockerfile:1
# Build from the repository root (the version and the commit come in as build arguments, because .git is not in the build
# context; the release uses the tag and `git rev-parse HEAD`):
#   docker build -f src/Auth.Server/Dockerfile \
#     --build-arg VERSION=0.1.0 --build-arg REVISION="$(git rev-parse HEAD)" \
#     -t ghcr.io/mckcieply/auth-core:0.1.0 .
#
# Both base images are pinned by digest: the tag says which release, the digest says which bytes, so the image cannot change under
# the tag. Updating them is a deliberate commit (docker buildx imagetools inspect <image:tag> prints the index digest).

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src

# Restore first, from project files only, so the package layer is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Auth.Infrastructure/Auth.Infrastructure.csproj src/Auth.Infrastructure/
COPY src/Auth.Server/Auth.Server.csproj src/Auth.Server/
RUN dotnet restore src/Auth.Server/Auth.Server.csproj

COPY src/ src/
RUN dotnet publish src/Auth.Server/Auth.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c AS runtime
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/MckCieply/Auth-Core" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.revision="${REVISION}"
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Chiseled images ship a non-root "app" user; run as it explicitly.
USER $APP_UID
ENTRYPOINT ["dotnet", "Auth.Server.dll"]
```

- [ ] **Step 3: The development compose.** `deploy/docker-compose.yml` (slice 7 changed its PostgreSQL health check to `pg_isready -h 127.0.0.1 -U auth -d auth`, over TCP: that line stays exactly as it is) — pin the two images, and pass the rate limits and the audit retention from `.env` (a blank value is the default, so nothing changes for anyone who sets nothing):

```diff
   postgres:
-    image: postgres:16-alpine
+    # The tag says which release, the digest says which bytes (the same image as the notes sample's database job).
+    image: postgres:16-alpine@sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea
@@
   mailpit:
     # A mail catcher: the auth service sends to it over SMTP, the e2e script reads the mails from its HTTP API.
-    image: axllent/mailpit:v1.31.3
+    # Its API is unauthenticated: development only, on loopback (spec 0008).
+    image: axllent/mailpit:v1.31.3@sha256:ed9b00c609e77e99c79b93f1178255ebc271868920f2c69a8d166bd5634ed10d
@@
       Auth__Keys__EncryptionKeyPath: /run/secrets/auth/encryption.key
+      # The per-IP limits of spec 0008 (30 logins, 60 refreshes, 10 mail requests, 20 invitation requests and 300 other requests per
+      # minute and address) and how long the audit log keeps a row (90 days). A blank value is the default. Some of the older e2e
+      # scripts exceed the limits on purpose: run them with AUTH_RATE_LIMIT_ENABLED=false (see the header of each script).
+      Auth__RateLimit__Enabled: ${AUTH_RATE_LIMIT_ENABLED:-true}
+      Auth__RateLimit__Login__PermitPerMinute: ${AUTH_RATE_LIMIT_LOGIN:-}
+      Auth__RateLimit__Refresh__PermitPerMinute: ${AUTH_RATE_LIMIT_REFRESH:-}
+      Auth__RateLimit__Email__PermitPerMinute: ${AUTH_RATE_LIMIT_EMAIL:-}
+      Auth__RateLimit__Invite__PermitPerMinute: ${AUTH_RATE_LIMIT_INVITE:-}
+      Auth__RateLimit__General__PermitPerMinute: ${AUTH_RATE_LIMIT_GENERAL:-}
+      Auth__Audit__RetentionDays: ${AUTH_AUDIT_RETENTION_DAYS:-}
     volumes:
```

`.env.example` — appended:

```bash

# Per-IP rate limits of Auth-Core (spec 0008); leave blank for the defaults. Development only: the e2e scripts of the lockout and of the
# mail limits make more requests a minute than the defaults allow, so start the stack with AUTH_RATE_LIMIT_ENABLED=false for them
# (scripts/e2e-hardening.sh wants the defaults).
AUTH_RATE_LIMIT_ENABLED=
AUTH_RATE_LIMIT_LOGIN=
AUTH_RATE_LIMIT_REFRESH=
AUTH_RATE_LIMIT_EMAIL=
AUTH_RATE_LIMIT_INVITE=
AUTH_RATE_LIMIT_GENERAL=
AUTH_AUDIT_RETENTION_DAYS=
```

`.gitignore` — the example file of the production environment is not an environment file:

```diff
 .env
 .env.*
 !.env.example
+!.env.prod.example
```

- [ ] **Step 4: The production compose file.** `deploy/docker-compose.prod.yml`:

```yaml
# Auth-Core in production: the published image and its PostgreSQL, and nothing else. Used on its own (it is not an overlay):
#
#   docker compose -f deploy/docker-compose.prod.yml --env-file .env up -d
#   docker compose -f deploy/docker-compose.prod.yml --env-file .env run --rm auth admin create-org --name "Acme"
#
# Needs .env (start from deploy/.env.prod.example, which explains every variable), the four key files and the manifest. There is no
# default for any secret: docker compose refuses to start without one, and so does the service without its keys, its issuer and
# audience, its mail relay (TLS required outside Development) and https frontend URLs.
#
# The service listens on 127.0.0.1:${AUTH_PORT:-8080} only; TLS ends at the reverse proxy (docs/deployment/vps.md has a Caddyfile), which
# must be the only way in. /auth/health does not reach the database, so a healthy answer says nothing about it: wait for a login, or for
# the CLI command above, before calling the database up.
name: auth-core-prod

services:
  postgres:
    # The tag says which release, the digest says which bytes. No published port: only the auth service reaches it.
    image: postgres:16-alpine@sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea
    restart: unless-stopped
    environment:
      POSTGRES_DB: auth
      POSTGRES_USER: auth
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?set POSTGRES_PASSWORD in .env}
    volumes:
      - postgres-data:/var/lib/postgresql/data
    healthcheck:
      # Over TCP (-h): on a fresh volume the image runs a temporary init server that listens on its socket only and would pass a socket check.
      test: ["CMD-SHELL", "pg_isready -h 127.0.0.1 -U auth -d auth"]
      interval: 5s
      timeout: 3s
      retries: 30
    networks:
      - auth

  auth:
    image: ghcr.io/mckcieply/auth-core:${AUTH_CORE_VERSION:?set AUTH_CORE_VERSION in .env (for example 0.1.0)}
    restart: unless-stopped
    depends_on:
      postgres:
        condition: service_healthy
    # Not root, nothing writable but memory: the image runs as its own user, the root file system is read-only and /tmp is in memory. No
    # capability, and no way to gain one. (Data Protection keys are kept in memory too: nothing of Auth-Core uses them.)
    read_only: true
    tmpfs:
      - /tmp
    cap_drop:
      - ALL
    security_opt:
      - no-new-privileges:true
    ports:
      # Loopback only: the reverse proxy on this host reaches it here, from the gateway address of the "auth" network below.
      - "127.0.0.1:${AUTH_PORT:-8080}:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      # The service creates and migrates its database at start. No seed users and no interactive reference in Production.
      Auth__Database__MigrateOnStartup: "true"
      ConnectionStrings__Auth: Host=postgres;Port=5432;Database=auth;Username=auth;Password=${POSTGRES_PASSWORD:?set POSTGRES_PASSWORD in .env}
      # What the tokens say about themselves: the origin the browser uses plus /auth, and the audience of the product's backend.
      Auth__Tokens__Issuer: ${AUTH_ISSUER:?set AUTH_ISSUER in .env (for example https://app.example.com/auth)}
      Auth__Tokens__Audience: ${AUTH_AUDIENCE:?set AUTH_AUDIENCE in .env (the audience your backend checks)}
      Auth__Manifest__Path: /etc/auth-core/auth.yaml
      Auth__Keys__SigningCertificatePath: /run/secrets/auth/signing.crt
      Auth__Keys__SigningKeyPath: /run/secrets/auth/signing.key
      Auth__Keys__EncryptionCertificatePath: /run/secrets/auth/encryption.crt
      Auth__Keys__EncryptionKeyPath: /run/secrets/auth/encryption.key
      # The product the mails speak for, and where their links lead (https only outside Development).
      Auth__App__Name: ${AUTH_APP_NAME:?set AUTH_APP_NAME in .env}
      Auth__App__Locale: ${AUTH_APP_LOCALE:?set AUTH_APP_LOCALE in .env (pl or en)}
      Auth__App__FrontendUrls__ResetPassword: ${AUTH_FRONTEND_RESET_URL:?set AUTH_FRONTEND_RESET_URL in .env}
      Auth__App__FrontendUrls__VerifyEmail: ${AUTH_FRONTEND_VERIFY_URL:?set AUTH_FRONTEND_VERIFY_URL in .env}
      Auth__App__FrontendUrls__AcceptInvite: ${AUTH_FRONTEND_INVITE_URL:?set AUTH_FRONTEND_INVITE_URL in .env}
      # The sender and the SMTP relay. The relay must offer TLS: "none" is refused outside Development.
      Auth__Email__From: ${AUTH_EMAIL_FROM:?set AUTH_EMAIL_FROM in .env}
      Auth__Email__Smtp__Host: ${AUTH_SMTP_HOST:?set AUTH_SMTP_HOST in .env}
      Auth__Email__Smtp__Port: ${AUTH_SMTP_PORT:-587}
      Auth__Email__Smtp__Security: ${AUTH_SMTP_SECURITY:-starttls}
      Auth__Email__Smtp__Username: ${AUTH_SMTP_USERNAME:-}
      Auth__Email__Smtp__Password: ${AUTH_SMTP_PASSWORD:-}
      # Who may tell the service the client address (spec 0008): the proxy on this host reaches the published port from the gateway of
      # the "auth" network (10.250.0.1 for the default subnet below); a proxy in a container joins the "proxy" network, whose
      # subnet is trusted instead. With neither set, no forwarded header is read and every client looks like the proxy, so all of
      # them share one set of rate limits.
      Auth__Proxy__KnownProxies__0: ${AUTH_PROXY_KNOWN_PROXIES:-10.250.0.1}
      Auth__Proxy__KnownNetworks__0: ${AUTH_PROXY_KNOWN_NETWORKS:-}
      # Per-IP limits and the retention of the audit log; a blank value is the default of the contract.
      Auth__RateLimit__Enabled: ${AUTH_RATE_LIMIT_ENABLED:-true}
      Auth__RateLimit__Login__PermitPerMinute: ${AUTH_RATE_LIMIT_LOGIN:-}
      Auth__RateLimit__Refresh__PermitPerMinute: ${AUTH_RATE_LIMIT_REFRESH:-}
      Auth__RateLimit__Email__PermitPerMinute: ${AUTH_RATE_LIMIT_EMAIL:-}
      Auth__RateLimit__Invite__PermitPerMinute: ${AUTH_RATE_LIMIT_INVITE:-}
      Auth__RateLimit__General__PermitPerMinute: ${AUTH_RATE_LIMIT_GENERAL:-}
      Auth__Audit__RetentionDays: ${AUTH_AUDIT_RETENTION_DAYS:-}
    volumes:
      # The key files, readable by the container's user (uid 1654); see docs/operations/key-rotation.md for how to make them.
      - ${AUTH_KEYS_DIR:?set AUTH_KEYS_DIR in .env (the directory with signing.crt, signing.key, encryption.crt, encryption.key)}:/run/secrets/auth:ro
      # The product's manifest (permissions and default roles).
      - ${AUTH_MANIFEST:?set AUTH_MANIFEST in .env (the path of the product's auth.yaml)}:/etc/auth-core/auth.yaml:ro
    # No health check: the image has no shell and no curl, and /auth/health does not reach the database anyway.
    networks:
      - auth
      - proxy

volumes:
  postgres-data:

networks:
  # A fixed subnet, so that the proxy can be named in Auth__Proxy__KnownProxies above: a proxy on the host reaches the published port from
  # this network's gateway, which is the first address of the subnet.
  auth:
    ipam:
      config:
        - subnet: ${AUTH_SUBNET:-10.250.0.0/24}
  # For a reverse proxy that runs in a container of its own: it joins this network (declared there as external) and reaches the
  # service as auth:8080. Set AUTH_PROXY_KNOWN_NETWORKS to AUTH_PROXY_SUBNET then. Nothing joins it otherwise.
  proxy:
    name: ${AUTH_PROXY_NETWORK:-auth-core-proxy}
    ipam:
      config:
        - subnet: ${AUTH_PROXY_SUBNET:-10.250.1.0/24}
```

`deploy/.env.prod.example`:

```bash
# The environment of deploy/docker-compose.prod.yml. Copy it to .env next to your compose command, fill in every value that says CHANGE,
# and keep the file out of version control. Placeholders only: nothing here is a real secret.
#
#   docker compose -f deploy/docker-compose.prod.yml --env-file .env up -d

# ---- The release -------------------------------------------------------------------------------------------------------
# The version of ghcr.io/mckcieply/auth-core to run (a tag of the repository without the leading v). Required.
AUTH_CORE_VERSION=0.1.0

# ---- The database ------------------------------------------------------------------------------------------------------
# The password of the PostgreSQL login "auth" of the bundled database. CHANGE: a long random value. Letters and digits only, because
# it is part of a connection string. Required; it is used once, when the volume is first made, so changing it later also means
# changing it inside PostgreSQL (ALTER ROLE auth PASSWORD ...).
POSTGRES_PASSWORD=CHANGEMEaLongRandomValue0123456789

# ---- The tokens --------------------------------------------------------------------------------------------------------
# The "iss" of the access tokens: the origin your users see, plus /auth. Your backend's package must be given the same value. Required.
AUTH_ISSUER=https://app.example.com/auth
# The "aud": the audience your product's backend checks. Required.
AUTH_AUDIENCE=my-product-api

# ---- Files on the host -------------------------------------------------------------------------------------------------
# The directory with signing.crt, signing.key, encryption.crt and encryption.key (RSA of at least 2048 bits). Generate them as
# docs/operations/key-rotation.md says and make them readable by uid 1654, the user of the container. Required.
AUTH_KEYS_DIR=/etc/auth-core/keys
# Your product's manifest: its permissions and the default roles of a new company (see docs/integration/python-fastapi.md, step 2). Required.
AUTH_MANIFEST=/etc/auth-core/auth.yaml

# ---- The product the mails speak for ------------------------------------------------------------------------------------
AUTH_APP_NAME=My Product
# pl or en
AUTH_APP_LOCALE=en
# The three screens of YOUR frontend that the links of the mails open (https only). The token is added to them as ?token=...
AUTH_FRONTEND_RESET_URL=https://app.example.com/reset
AUTH_FRONTEND_VERIFY_URL=https://app.example.com/verify
AUTH_FRONTEND_INVITE_URL=https://app.example.com/invite

# ---- The mail relay ------------------------------------------------------------------------------------------------------
# The sender of every mail, and the SMTP relay that sends it. The relay must offer TLS (STARTTLS on 587, or TLS from the first byte on 465);
# the service refuses to start with "none" in Production.
AUTH_EMAIL_FROM=no-reply@app.example.com
AUTH_SMTP_HOST=smtp.example.com
AUTH_SMTP_PORT=587
# starttls or tls
AUTH_SMTP_SECURITY=starttls
# Both or neither. CHANGE.
AUTH_SMTP_USERNAME=
AUTH_SMTP_PASSWORD=

# ---- Where the service listens and who is in front of it ----------------------------------------------------------------
# The port on 127.0.0.1 that your reverse proxy forwards /auth to.
AUTH_PORT=8080
# The subnet of the compose network "auth". Its gateway (the first address) is where a proxy on THIS host appears to come from, so it is
# the proxy the service trusts for the client address. Change the subnet only if it clashes with one you have, and change the
# proxy address with it.
AUTH_SUBNET=10.250.0.0/24
AUTH_PROXY_KNOWN_PROXIES=10.250.0.1
# For a proxy that runs in a container: the network it joins, its subnet, and then the subnet to trust instead. Leave blank otherwise.
AUTH_PROXY_NETWORK=auth-core-proxy
AUTH_PROXY_SUBNET=10.250.1.0/24
AUTH_PROXY_KNOWN_NETWORKS=

# ---- Limits and the audit log (optional: blank means the default) -------------------------------------------------------
# Requests a minute from one address: 30 logins, 60 refreshes, 10 requests for a mail, 20 for an invitation, 300 for anything else under
# /auth. AUTH_RATE_LIMIT_ENABLED=false turns the limiter off; never do that on a public server. Many people behind one address
# (an office) share its limits: raise AUTH_RATE_LIMIT_LOGIN for them.
AUTH_RATE_LIMIT_ENABLED=
AUTH_RATE_LIMIT_LOGIN=
AUTH_RATE_LIMIT_REFRESH=
AUTH_RATE_LIMIT_EMAIL=
AUTH_RATE_LIMIT_INVITE=
AUTH_RATE_LIMIT_GENERAL=
# How many days a row of the audit log is kept (90 by default).
AUTH_AUDIT_RETENTION_DAYS=
```

- [ ] **Step 5: Check the files.** From the repository root. No stack is started: `docker compose config` only reads and renders the files.

```bash
# 1. the production file renders with the example environment and names the image by version
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env.prod.example config | grep -E "image:|read_only|cap_drop|subnet"
# 2. it refuses to render without its secrets: an empty environment file
empty="$(mktemp)"
docker compose -f deploy/docker-compose.prod.yml --env-file "$empty" config 2>&1 | head -n 3
rm -f "$empty"
# 3. the development file renders and every image of both files is pinned by digest, except Auth-Core's own
docker compose -f deploy/docker-compose.yml --env-file .env.example config | grep "image:"
grep -n "image:" deploy/docker-compose.yml deploy/docker-compose.prod.yml
# 4. the example file of the production environment is not ignored by git
git check-ignore -v deploy/.env.prod.example; echo "exit $? (1 means not ignored)"
```

  Expected: (1) `ghcr.io/mckcieply/auth-core:0.1.0`, `postgres:16-alpine@sha256:7218...`, `read_only: true`, `cap_drop` with `ALL`, and the two subnets; (2) one `required variable ... is missing a value: set ... in .env` error for each variable that has no default (checked: with the example file it renders, with an empty one it names the missing ones); (3) the development file shows `postgres:16-alpine@sha256:7218...` and `axllent/mailpit:v1.31.3@sha256:ed9b...`, and `grep` finds an `@sha256:` on every `image:` line but `ghcr.io/mckcieply/auth-core`; (4) `exit 1`.

- [ ] **Step 6: Build the image once and read its labels.** (This pulls the pinned base images; if Docker cannot reach the registry, say so in the hand-back and leave this to the verifier of Task 12.)

```bash
docker build -f src/Auth.Server/Dockerfile --build-arg VERSION=0.0.0-test --build-arg REVISION=0123456789abcdef -t ghcr.io/mckcieply/auth-core:0.0.0-test .
docker image inspect ghcr.io/mckcieply/auth-core:0.0.0-test --format '{{json .Config.Labels}}'
docker image inspect ghcr.io/mckcieply/auth-core:0.0.0-test --format '{{.Config.User}}'
```

  Expected: the labels `org.opencontainers.image.source` (`https://github.com/MckCieply/Auth-Core`), `.version` (`0.0.0-test`), `.licenses` (`MIT`), `.revision` (`0123456789abcdef`), and the user `1654` or `app`. Do not push the image. Leave it in the local image store: Task 12 uses it under this name.

- [ ] **Step 7: The suite is untouched.** `"C:/Program Files/dotnet/dotnet.exe" build -warnaserror` passes (the Dockerfile and the compose files are not part of the build); no other command is needed.

- [ ] **Step 8: Hand back** — uncommitted. Files: `src/Auth.Server/Dockerfile`, `deploy/docker-compose.yml`, `deploy/docker-compose.prod.yml`, `deploy/.env.prod.example`, `.env.example`, `.gitignore`. Proposed subject: `feat(deploy): production compose file, digest-pinned images and OCI labels`.

## Day 4 — Monday 23.02: the samples, the e2e scripts, the documents, the release material

When the day is done, the sample proxies send the headers and the client address, the Angular sample shows "Try again in N s." and keeps the session on `429` and `503`, `scripts/e2e-hardening.sh` and the production test overlay exist, and the threat model, the runbooks, the deployment guide, the README, the changelog and the package metadata are written. Five tasks.

### Task 10: The sample proxies and the Angular sample

**Base.** This branch is based on `main` at `193d3c6`, which holds slice 7 (`samples/notes-web`, the compose healthcheck over TCP, `scripts/e2e-web.sh`). Every anchor below was checked against that tree: the `Caddyfile` with its `(routes)` snippet, `auth.service.ts` (with `session`), `auth.interceptor.ts` (with `sentIn`), `texts.ts`, `app.ts`, `login.ts`, `e2e/07-headers.spec.ts`, `tools/check-guide.mjs` and `docs/integration/angular.md`. Every edit below to a file of slice 7 is nevertheless given as an anchor (a line the other edits do not touch) and the lines to put there. If an anchor is not in the file, stop and report to the orchestrator which one: do not improvise a different place. New files never conflict.

**Files:**
- Modify: `samples/notes-api/Caddyfile`, `samples/notes-api/compose.yml`, `scripts/e2e-notes.sh`
- Modify (slice 7): `samples/notes-web/Caddyfile`, `samples/notes-web/src/app/auth/auth.service.ts`, `samples/notes-web/src/app/auth/auth.interceptor.ts`, `samples/notes-web/src/app/texts.ts`, `samples/notes-web/src/app/app.ts`, `samples/notes-web/src/app/pages/login.ts`, `samples/notes-web/e2e/07-headers.spec.ts` (two tests that assume Auth-Core sends no Content-Security-Policy and no Referrer-Policy), `docs/integration/angular.md`
- Create (tests): `samples/notes-web/src/app/auth/too-many-requests.spec.ts`, `samples/notes-web/src/app/app.wait.spec.ts`, `samples/notes-web/src/app/pages/login.too-many-requests.spec.ts`, `samples/notes-web/e2e/08-proxy-headers.spec.ts`

**Interfaces:**
- Consumes: the answers of Auth-Core (`429 {"error":"too_many_requests","retry_after_seconds":n}`, `503 {"error":"temporarily_unavailable"}`), slice 7's `AuthService`, `authInterceptor`, `texts`, `App`, `LoginPage`, `holdToken` and `settle` of `src/testing/helpers.ts`.
- Produces:
  - Caddy: `X-Forwarded-For {remote_host}` and `X-Forwarded-Proto {scheme}` sent to Auth-Core by both samples (a client's own `X-Forwarded-For` is replaced, because `header_up` sets the header); `X-Content-Type-Options: nosniff` and `X-Frame-Options: DENY` on `/api/*` of the notes-api proxy; `X-Frame-Options: DENY`, `Cross-Origin-Opener-Policy: same-origin` and `Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=()` on the app's routes of the notes-web proxy; `Strict-Transport-Security: max-age=31536000` on its HTTPS listener only.
  - The notes overlay (`samples/notes-api/compose.yml`) gives its project network a fixed subnet and Caddy a fixed address in it, and tells Auth-Core to trust that one address (`Auth__Proxy__KnownNetworks__0: <address>/32`); the web overlay inherits it.
  - Angular: `AuthNotice` gains `'wait'`; `AuthService.noticeSeconds` (a read-only signal) and `showNotice(notice, seconds = 0)`; `retryAfterOf(error: HttpErrorResponse): number` (exported by `auth.service.ts`); `Failure` gains `{ kind: 'too_many_requests'; retryAfterSeconds: number }`; `texts.notices.tryAgainIn(seconds)` = `Try again in N s.`.

**Decisions of this task.**
- The trusted network is one address: Caddy's, fixed in the overlay. The host's own gateway, from which the published `127.0.0.1:8080` and the e2e scripts reach Auth-Core, is **not** trusted, so a script that sends `X-Forwarded-For` straight to Auth-Core is not believed.
- A `429 too_many_requests` is announced by the interceptor for **every** request to the app's own origin (the notice bar says "Try again in N s."), so it covers the endpoints that need no token too (login, the mail flows, the refresh). Nothing in the path of a `429` or a `503` signs the person out: the refresh's `'unavailable'` result already keeps the session; only a `401` ends it.
- The login screen shows nothing of its own for a `429 too_many_requests` (the bar says when to try again); the other screens keep their generic message beside the bar.

- [ ] **Step 1: The proxy of the notes sample.** `samples/notes-api/Caddyfile` (whole file):

```
# One origin for the browser: /auth is Auth-Core, /api is the notes service. Plain HTTP, development only.
{
	admin off
	auto_https off
}

http://:8088 {
	handle /auth/* {
		# Auth-Core limits and records requests per client address (spec 0008). This proxy says which address that is: the one it saw,
		# and nothing the client wrote (header_up sets the header, so a forged X-Forwarded-For is replaced).
		reverse_proxy auth:8080 {
			header_up X-Forwarded-For {remote_host}
			header_up X-Forwarded-Proto {scheme}
		}
	}
	handle /api/* {
		header {
			X-Content-Type-Options nosniff
			X-Frame-Options DENY
		}
		reverse_proxy notes-api:8000
	}
	handle {
		respond "not found" 404
	}
}
```

`samples/notes-api/compose.yml` — the proxy gets a fixed address, Auth-Core trusts exactly it, and the project's network gets a subnet to hold it:

```diff
   caddy:
@@
     ports:
       # Loopback only, like every other port of this stack.
       - "127.0.0.1:8088:8088"
+    networks:
+      default:
+        # A fixed address, so that Auth-Core can be told which proxy to believe about the client address (see `auth` below).
+        ipv4_address: ${NOTES_PROXY_IP:-10.250.2.10}
     volumes:
       - ../samples/notes-api/Caddyfile:/etc/caddy/Caddyfile:ro
@@
   auth:
     environment:
       # Tokens name the origin the browser uses, and are for the notes service.
       Auth__Tokens__Issuer: http://localhost:8088/auth
       Auth__Tokens__Audience: notes-api
+      # The proxy in front of Auth-Core in this stack, and nothing else, is believed about the client address (spec 0008): a network of
+      # the one address Caddy has. A request that reaches Auth-Core's own port from the host is taken as it comes.
+      Auth__Proxy__KnownNetworks__0: ${NOTES_PROXY_IP:-10.250.2.10}/32
     volumes:
       # The same container path as in the base file: this mount replaces the development manifest.
       - ../samples/notes-api/auth.yaml:/etc/auth-core/auth.yaml:ro
+
+networks:
+  default:
+    ipam:
+      config:
+        # A fixed subnet for the project's network, so that the proxy's address above is in it. Change both (NOTES_SUBNET and
+        # NOTES_PROXY_IP in .env) if another network of yours already uses this range.
+        - subnet: ${NOTES_SUBNET:-10.250.2.0/24}
```

(The comment at the top of the file, which says the overlay is used with the base file, stays. `samples/notes-web/compose.yml` needs no change: its `caddy` service inherits the `networks` and the address, and its `auth` entry inherits the trusted network.)

- [ ] **Step 2: The proxy of the Angular sample.** `samples/notes-web/Caddyfile` — four edits, each by anchor; nothing else of the file changes (the Content-Security-Policy stays):

  1. The line `reverse_proxy auth:8080` (inside the handler of `/auth`) becomes:

```
		reverse_proxy auth:8080 {
			# Auth-Core limits and records requests per client address (spec 0008): the one this proxy saw, never one the client wrote.
			header_up X-Forwarded-For {remote_host}
			header_up X-Forwarded-Proto {scheme}
		}
```

  2. In the `header { ... }` block of the app's handler, **after** the line `X-Content-Type-Options nosniff`, add:

```
			X-Frame-Options DENY
			Cross-Origin-Opener-Policy same-origin
			Permissions-Policy "camera=(), microphone=(), geolocation=(), payment=()"
```

  3. In the site block whose address begins `https://localhost:8443`, **after** its line `tls internal`, add (HTTPS listener only: the `http://:8088` block gets nothing):

```
	# TLS ends here, so this proxy sends HSTS (Auth-Core does not). Over HTTPS only.
	header Strict-Transport-Security "max-age=31536000"
```

  4. Nothing else: the `X-Frame-Options` of the app is new here (the policy's `frame-ancestors 'none'` stays and says the same to modern browsers).

  Check: `docker run --rm -v "$PWD/samples/notes-web/Caddyfile:/etc/caddy/Caddyfile:ro" caddy:2.11.7@sha256:f2a1290d0463aad60660d4ec134943f183ee2a5f6c3eb7bf32dd984f2f020772 caddy validate --config /etc/caddy/Caddyfile` prints `Valid configuration`. (It starts no stack: `validate` only parses.)

- [ ] **Step 3: The e2e script of the notes sample checks the headers and the client address.** `scripts/e2e-notes.sh` — a seventh step, before the last line (`echo "ALL PASS"`), and the header comment gains one line after the one that describes step 6:

```diff
 #      10 seconds, so the first answers after the restart may still be 503) - criterion 5.
+#   7. The proxy's headers reach the browser (nosniff and DENY on /api, Auth-Core's own on /auth), and the client address that
+#      Auth-Core records for a request through the proxy is the one the proxy saw: not the proxy's, and not the one the client wrote
+#      into X-Forwarded-For (spec 0008, criteria 2 and 15).
```

```bash
# --- Step 7: the headers of the proxy, and the client address behind it (spec 0008) -----------------------------------------
call GET /api/health
expect_status "step 7: GET /api/health" 200
[[ "$(header x-content-type-options)" == "nosniff" ]] || fail "step 7: /api/health has no X-Content-Type-Options: nosniff"
[[ "$(header x-frame-options)" == "DENY" ]] || fail "step 7: /api/health has no X-Frame-Options: DENY"
call GET /auth/health
expect_status "step 7: GET /auth/health" 200
[[ "$(header x-content-type-options)" == "nosniff" && "$(header x-frame-options)" == "DENY" ]] \
  || fail "step 7: the headers of Auth-Core do not reach the browser"
[[ "$(header content-security-policy)" == "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'" ]] \
  || fail "step 7: /auth/health has not the policy of Auth-Core"
SPOOFED_EMAIL="nobody-$run@example.invalid"
json_body "$tmp/spoof.json" "{'email': '$SPOOFED_EMAIL', 'password': 'Wrong-Password-1'}"
curl -sS --max-time 20 -o /dev/null -X POST "$BASE_URL/auth/login" -H 'Content-Type: application/json' \
  -H 'X-Forwarded-For: 203.0.113.99' --data-binary "@$tmp/spoof.json" || fail "step 7: the login through the proxy did not answer"
RECORDED="$("${compose[@]}" exec -T postgres psql -U auth -d auth -tA \
  -c "SELECT client_ip FROM audit_events WHERE kind = 'login.failed' AND subject_email = '$SPOOFED_EMAIL'" | tr -d '\r')"
[[ -n "$RECORDED" ]] || fail "step 7: the failed login through the proxy is not in the audit log"
[[ "$RECORDED" != "203.0.113.99" ]] || fail "step 7: the recorded address is the one the client wrote into X-Forwarded-For"
[[ "$RECORDED" != "${NOTES_PROXY_IP:-10.250.2.10}" ]] || fail "step 7: the recorded address is the proxy's: the proxy is not trusted"
pass "step 7: the proxy's headers reach the browser, and Auth-Core records the address the proxy saw, not the proxy's and not a forged one"
```

  (`NOTES_PROXY_IP` is read from the script's environment, like the compose variable of the same name, which a `.env` file may also set: if you set it only in `.env`, export it too, or the script compares with the default.) Check: `bash -n scripts/e2e-notes.sh` prints nothing. The live run is Task 12's and the verifiers' (the stack's ports are theirs).

- [ ] **Step 4: Write the failing Angular tests.** (They are new files: they do not touch slice 7's specs.) Run them from `samples/notes-web` with `npx ng test --watch=false`. The existing specs are the regression check, so the whole run must stay green.

`samples/notes-web/src/app/auth/too-many-requests.spec.ts`:

```ts
import { Component } from '@angular/core';
import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { lastValueFrom } from 'rxjs';
import { holdToken, settle } from '../../testing/helpers';
import { texts } from '../texts';
import { authInterceptor } from './auth.interceptor';
import { AuthService, failureOf, retryAfterOf } from './auth.service';

@Component({ template: '' })
class Blank {}

const status = (code: number) => ({ status: code, statusText: String(code) });
const tooMany = (seconds: unknown = 12) => ({ error: 'too_many_requests', retry_after_seconds: seconds });
const outage = { error: 'temporarily_unavailable' };

describe('a 429 too_many_requests', () => {
  let http: HttpClient;
  let ctrl: HttpTestingController;
  let auth: AuthService;
  let router: Router;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'notes', component: Blank },
          { path: 'login', component: Blank },
        ]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    ctrl = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthService);
    router = TestBed.inject(Router);
    await router.navigateByUrl('/notes');
    await holdToken(auth, ctrl, 'tok');
  });

  afterEach(() => ctrl.verify());

  it('says "Try again in N s." and has the notice bar show it', () => {
    expect(texts.notices.tryAgainIn(12)).toBe('Try again in 12 s.');
    expect(texts.notices.tryAgainIn(1)).toBe('Try again in 1 s.');
  });

  it('from an endpoint that needs no token: the notice, and the session stays', async () => {
    const done = lastValueFrom(http.post('/auth/login', { email: 'a@b.example', password: 'x' })).catch((error: unknown) => error);
    ctrl.expectOne('/auth/login').flush(tooMany(12), status(429));
    expect(await done).toBeInstanceOf(HttpErrorResponse);
    expect(auth.notice()).toBe('wait');
    expect(auth.noticeSeconds()).toBe(12);
    expect(auth.token()).toBe('tok');
    expect(router.url).toBe('/notes');
  });

  it.each(['/api/notes', '/auth/me', '/auth/org/members'])('from %s: the notice, no refresh, nobody signed out', async (url) => {
    const done = lastValueFrom(http.get(url)).catch((error: unknown) => error);
    ctrl.expectOne(url).flush(tooMany(30), status(429));
    expect(await done).toBeInstanceOf(HttpErrorResponse);
    await settle();
    ctrl.expectNone('/auth/refresh');
    expect(auth.notice()).toBe('wait');
    expect(auth.noticeSeconds()).toBe(30);
    expect(auth.token()).toBe('tok');
    expect(router.url).toBe('/notes');
  });

  it.each([
    ['no number', { error: 'too_many_requests' }],
    ['a number that is not one', tooMany('soon')],
    ['zero', tooMany(0)],
    ['a negative number', tooMany(-5)],
  ])('with %s the wait is a minute', async (_name, body) => {
    const done = lastValueFrom(http.post('/auth/password/forgot', { email: 'a@b.example' })).catch((error: unknown) => error);
    ctrl.expectOne('/auth/password/forgot').flush(body, status(429));
    await done;
    expect(auth.notice()).toBe('wait');
    expect(auth.noticeSeconds()).toBe(60);
  });

  it.each([
    ['the lockout of a person (too_many_attempts), which the login screen words itself', { error: 'too_many_attempts', retry_after_seconds: 120 }, 429],
    ['a 429 with no code (a proxy)', '<html>slow down</html>', 429],
    ['a 503 of the service', outage, 503],
    ['a 500', { error: 'internal_error' }, 500],
  ])('is not announced: %s', async (_name, body, code) => {
    const done = lastValueFrom(http.post('/auth/login', {})).catch((error: unknown) => error);
    ctrl.expectOne('/auth/login').flush(body, status(code));
    await done;
    expect(auth.notice()).toBeNull();
    expect(auth.token()).toBe('tok');
  });

  it('from another origin: not announced', async () => {
    const done = lastValueFrom(http.get('https://elsewhere.example/api')).catch((error: unknown) => error);
    ctrl.expectOne('https://elsewhere.example/api').flush(tooMany(9), status(429));
    await done;
    expect(auth.notice()).toBeNull();
  });

  it('a refresh answered 429 keeps the session and says when to try again', async () => {
    const refreshed = auth.refresh();
    ctrl.expectOne('/auth/refresh').flush(tooMany(30), status(429));
    expect(await refreshed).toBe('unavailable');
    expect(auth.token()).toBe('tok');
    expect(auth.notice()).toBe('wait');
    expect(auth.noticeSeconds()).toBe(30);
  });

  it('a refresh answered 503 temporarily_unavailable keeps the session', async () => {
    const refreshed = auth.refresh();
    ctrl.expectOne('/auth/refresh').flush(outage, status(503));
    expect(await refreshed).toBe('unavailable');
    expect(auth.token()).toBe('tok');
    expect(router.url).toBe('/notes');
  });

  it('a request that got a 401, whose refresh then gets 429, keeps the session and the "try again" notice', async () => {
    const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
    ctrl.expectOne('/api/notes').flush(null, status(401));
    await settle();
    ctrl.expectOne('/auth/refresh').flush(tooMany(8), status(429));
    await settle();
    expect(await done).toBeInstanceOf(HttpErrorResponse);
    expect(auth.token()).toBe('tok');
    expect(auth.notice()).toBe('wait');   // not replaced by "can't reach the server"
    expect(auth.noticeSeconds()).toBe(8);
    expect(router.url).toBe('/notes');   // no trip to /login
  });

  it('a request that got a 401, whose refresh then gets 503, keeps the session too', async () => {
    const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
    ctrl.expectOne('/api/notes').flush(null, status(401));
    await settle();
    ctrl.expectOne('/auth/refresh').flush(outage, status(503));
    await settle();
    expect(await done).toBeInstanceOf(HttpErrorResponse);
    expect(auth.token()).toBe('tok');
    expect(router.url).toBe('/notes');
  });
});

describe('the answers of the service that failureOf and retryAfterOf read', () => {
  const answer = (code: number, body: unknown) => new HttpErrorResponse({ status: code, error: body });

  it('a 429 too_many_requests is its own failure, with the wait', () => {
    expect(failureOf(answer(429, tooMany(7)))).toEqual({ kind: 'too_many_requests', retryAfterSeconds: 7 });
    expect(failureOf(answer(429, tooMany('x')))).toEqual({ kind: 'too_many_requests', retryAfterSeconds: 60 });
  });

  it('a 429 too_many_attempts is still the lockout', () => {
    expect(failureOf(answer(429, { error: 'too_many_attempts', retry_after_seconds: 90 }))).toEqual({
      kind: 'too_many_attempts',
      retryAfterSeconds: 90,
    });
  });

  it('retryAfterOf is the number of the body, a minute when there is none', () => {
    expect(retryAfterOf(answer(429, tooMany(5)))).toBe(5);
    expect(retryAfterOf(answer(429, null))).toBe(60);
    expect(retryAfterOf(answer(429, 'text'))).toBe(60);
  });
});
```

`samples/notes-web/src/app/app.wait.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { settle } from '../testing/helpers';
import { App } from './app';
import { AuthService } from './auth/auth.service';

describe('App, the notice of a request limit', () => {
  function open() {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    return { fixture, auth: TestBed.inject(AuthService), root: fixture.nativeElement as HTMLElement };
  }

  const text = (root: HTMLElement) => root.querySelector('[data-testid="notice-text"]')?.textContent;

  it('shows "Try again in N s." with the number the server gave', async () => {
    const { fixture, auth, root } = open();
    auth.showNotice('wait', 12);
    await settle(fixture);
    expect(text(root)).toBe('Try again in 12 s.');
  });

  it('follows a newer notice and can be dismissed', async () => {
    const { fixture, auth, root } = open();
    auth.showNotice('wait', 12);
    await settle(fixture);
    auth.showNotice('wait', 3);
    await settle(fixture);
    expect(text(root)).toBe('Try again in 3 s.');
    auth.showNotice('tryLater');
    await settle(fixture);
    expect(text(root)).toBe('Try again shortly.');
    root.querySelector<HTMLButtonElement>('[data-testid="notice"] button')?.click();
    await settle(fixture);
    expect(root.querySelector('[data-testid="notice"]')).toBeNull();
  });
});
```

`samples/notes-web/src/app/pages/login.too-many-requests.spec.ts`:

```ts
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { has, settle, submitForm, typeInto } from '../../testing/helpers';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService } from '../auth/auth.service';
import { LoginPage } from './login';

describe('LoginPage when the address has made too many requests', () => {
  it('leaves the words to the notice bar, says nothing of its own and can be tried again', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'login', component: LoginPage }]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/login', LoginPage);
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const ctrl = TestBed.inject(HttpTestingController);
    const auth = TestBed.inject(AuthService);

    typeInto(harness.fixture, '#email', 'admin@example.test');
    typeInto(harness.fixture, '#password', 'pw');
    submitForm(harness.fixture);
    ctrl
      .expectOne('/auth/login')
      .flush({ error: 'too_many_requests', retry_after_seconds: 17 }, { status: 429, statusText: '429' });
    await settle(harness.fixture);

    expect(auth.notice()).toBe('wait');
    expect(auth.noticeSeconds()).toBe(17);
    expect(has(harness.fixture, '[data-testid="login-message"]')).toBe(false);
    expect(harness.fixture.nativeElement.querySelector('button[type="submit"]').disabled).toBe(false);
    expect(auth.token()).toBeNull();
    ctrl.verify();
  });
});
```

`samples/notes-web/e2e/08-proxy-headers.spec.ts`:

```ts
import { expect, test } from '@playwright/test';
import { apiUrl } from './support/env';

const AUTH_CORE_POLICY = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

test('the app answers with the headers the proxy adds', async ({ page }) => {
  const answer = await page.request.get('/login');
  expect(answer.status()).toBe(200);
  const headers = answer.headers();
  expect(headers['x-frame-options']).toBe('DENY');
  expect(headers['cross-origin-opener-policy']).toBe('same-origin');
  expect(headers['permissions-policy']).toBe('camera=(), microphone=(), geolocation=(), payment=()');
  expect(headers['content-security-policy']).toContain("frame-ancestors 'none'");   // the policy of spec 0007 is still there
});

test('HSTS is sent over HTTPS, and not over plain HTTP', async ({ page }) => {
  const secure = await page.request.get('/login');
  expect(secure.headers()['strict-transport-security']).toBe('max-age=31536000');
  const plain = await page.request.get(`${apiUrl()}/login`);
  expect(plain.status()).toBe(200);
  expect(plain.headers()['strict-transport-security']).toBeUndefined();
});

test("Auth-Core's own headers reach the browser through the proxy, with HSTS added over HTTPS", async ({ page }) => {
  const answer = await page.request.get('/auth/health');
  expect(answer.status()).toBe(200);
  const headers = answer.headers();
  expect(headers['content-security-policy']).toBe(AUTH_CORE_POLICY);
  expect(headers['x-content-type-options']).toBe('nosniff');
  expect(headers['x-frame-options']).toBe('DENY');
  expect(headers['cache-control']).toBe('no-store');
  expect(headers['strict-transport-security']).toBe('max-age=31536000');
});
```

- [ ] **Step 4b: The two tests of slice 7 that Auth-Core's headers break.** `samples/notes-web/e2e/07-headers.spec.ts` asserts that `/auth` and `/auth/health` carry **no** `Content-Security-Policy` and **no** `Referrer-Policy` (the app's own are not put on the services' routes). Since this slice Auth-Core sends its own on every answer, so those assertions hold for `/api` only. Two edits, by anchor (the rest of the file stays):

  1. After the declaration of `CSP` (the statement that ends with `$/;`), add:

```ts
// Auth-Core's own policy on every answer of /auth (spec 0008): it allows nothing; the app's policy above is the app's.
const AUTH_CORE_POLICY = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
```

  2. Replace the whole test whose title begins `'/auth and /api without a slash go to their services` with:

```ts
test('/auth and /api without a slash go to their services, not to the app', async ({ page }) => {
  for (const path of ['/auth', '/api']) {
    const answer = await page.request.get(path, { maxRedirects: 0 });
    expect(await answer.text(), path).not.toContain('<app-root');
  }
  // Auth-Core sends its own headers on every answer, its 404 included; the notes service sends none of these two.
  const auth = await page.request.get('/auth', { maxRedirects: 0 });
  expect(auth.headers()['content-security-policy']).toBe(AUTH_CORE_POLICY);
  expect(auth.headers()['referrer-policy']).toBe('no-referrer');
  const api = await page.request.get('/api', { maxRedirects: 0 });
  expect(api.headers()['content-security-policy']).toBeUndefined();
  expect(api.headers()['referrer-policy']).toBeUndefined();
});
```

  3. Replace the whole test whose title begins `'the headers of the app are not put on /auth and /api` with:

```ts
test('the headers of the app are not put on /auth and /api, which set their own', async ({ page }) => {
  const auth = await page.request.get('/auth/health');
  expect(auth.status()).toBe(200);
  // Auth-Core's policy, not the app's (the app's names 'self' sources and a style nonce; Auth-Core's allows nothing).
  expect(auth.headers()['content-security-policy']).toBe(AUTH_CORE_POLICY);
  expect(auth.headers()['referrer-policy']).toBe('no-referrer');
  const api = await page.request.get('/api/health');
  expect(api.status()).toBe(200);
  expect(api.headers()['content-security-policy']).toBeUndefined();
  expect(api.headers()['referrer-policy']).toBeUndefined();
});
```

  Type-check: `cd samples/notes-web && npx tsc -p e2e/tsconfig.json` prints nothing. The test itself runs with `scripts/e2e-web.sh` (Step 7).

- [ ] **Step 5: Run the tests and see them fail.** `cd samples/notes-web && npx ng test --watch=false`. Expected: the build fails (`retryAfterOf` is not exported, `texts.notices.tryAgainIn` and `auth.noticeSeconds` do not exist, `showNotice` takes one argument).

- [ ] **Step 6: Implement the Angular changes.** Each is by anchor; the rest of each file stays.

`samples/notes-web/src/app/texts.ts` — in `notices`, after the line `dismiss: 'Dismiss',`:

```ts
    // A request limit (429 too_many_requests): the number is what the server said.
    tryAgainIn: (seconds: number): string => `Try again in ${seconds} s.`,
```

`samples/notes-web/src/app/auth/auth.service.ts`:

  1. Replace the line `export type AuthNotice = 'unreachable' | 'tryLater' | 'forbidden';` with:

```ts
export type AuthNotice = 'unreachable' | 'tryLater' | 'forbidden' | 'wait';
```

  2. In the `Failure` union, after the line `| { kind: 'too_many_attempts'; retryAfterSeconds: number }`, add:

```ts
  | { kind: 'too_many_requests'; retryAfterSeconds: number }
```

  3. Before the function `failureOf`, add (and in `failureOf`, **before** the branch `if (error.status === 401 && code === 'invalid_credentials') {`, the branch below):

```ts
/** The `retry_after_seconds` of a 429 body, or a minute when it has none that is a positive number. */
export function retryAfterOf(error: HttpErrorResponse): number {
  const seconds = isRecord(error.error) ? error.error['retry_after_seconds'] : undefined;
  return typeof seconds === 'number' && seconds > 0 ? seconds : DEFAULT_WAIT_SECONDS;
}
```

```ts
  if (error.status === 429 && code === 'too_many_requests') {
    return { kind: 'too_many_requests', retryAfterSeconds: retryAfterOf(error) };
  }
```

  4. After the line `private readonly noticeState = signal<AuthNotice | null>(null);` add `private readonly noticeSecondsState = signal(0);`, and after the line `readonly notice = this.noticeState.asReadonly();` add:

```ts
  /** The number of the 'wait' notice: the seconds the server asked the person to wait. */
  readonly noticeSeconds = this.noticeSecondsState.asReadonly();
```

  5. Replace the method `showNotice` with:

```ts
  showNotice(notice: AuthNotice, seconds = 0): void {
    this.noticeSecondsState.set(seconds);
    this.noticeState.set(notice);
  }
```

  6. In `start()`, the branch `if (result === 'unavailable') {` followed by `this.noticeState.set('unreachable');` becomes:

```ts
    if (result === 'unavailable') {
      // Not over a "try again in N s." that a 429 of the refresh has just put there.
      if (this.noticeState() !== 'wait') {
        this.noticeState.set('unreachable');
      }
```

`samples/notes-web/src/app/auth/auth.interceptor.ts`:

  1. The import `import { AuthService, errorCode } from './auth.service';` becomes `import { AuthService, errorCode, retryAfterOf } from './auth.service';`.
  2. The whole `export const authInterceptor` is replaced by (keep its comment about observables):

```ts
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const answer = wantsToken(req.url) ? send(req, next, auth, inject(Router)) : next(req);
  // Built from observables, never promises: when the time is up the timeout unsubscribes, and that cancels the request in flight and
  // ends the chain (no refresh, no second try, no /login) for a caller that has already been told it failed. A 429 from any endpoint
  // of the app's own origin is announced on the way out, whatever the endpoint is.
  return ownUrl(req.url) === null
    ? answer
    : answer.pipe(
        timeout(REQUEST_TIMEOUT_MS),
        tap({ error: (error: unknown) => announceTooManyRequests(error, auth) }),
      );
};

/**
 * A 429 too_many_requests (the limit per address, spec 0008): the notice bar says when to try again. It signs nobody out and
 * ends no session: only a 401 does.
 */
function announceTooManyRequests(error: unknown, auth: AuthService): void {
  if (error instanceof HttpErrorResponse && error.status === 429 && errorCode(error) === 'too_many_requests') {
    auth.showNotice('wait', retryAfterOf(error));
  }
}
```

  3. In `tokenIsRenewed`, the line `auth.showNotice('unreachable');` becomes:

```ts
        if (auth.notice() !== 'wait') {
          auth.showNotice('unreachable'); // not over the "try again in N s." that a 429 of this refresh has put there
        }
```

`samples/notes-web/src/app/app.ts` — the `notice` computed becomes:

```ts
  protected readonly notice = computed(() => {
    const kind = this.auth.notice();
    if (kind === null) {
      return null;
    }
    return kind === 'wait' ? texts.notices.tryAgainIn(this.auth.noticeSeconds()) : texts.notices[kind];
  });
```

`samples/notes-web/src/app/pages/login.ts` — in `show(failure: Failure)`, after the case `'too_many_attempts'` (which ends with `break;`), add:

```ts
      case 'too_many_requests':
        // The notice bar above the screen says when to try again: the interceptor put it there.
        break;
```

`docs/integration/angular.md` — a section appended at the end of the file:

```markdown

## The limits and an outage (spec 0008)

Auth-Core limits requests per address (30 sign-ins, 60 refreshes, 10 requests for a mail, 20 for an invitation and 300 others a minute) and
answers `429 {"error":"too_many_requests","retry_after_seconds":n}` with `Retry-After` when a limit is reached. The sample's interceptor
shows "Try again in N s." for such an answer from any endpoint of your origin and **does not sign the person out**: only a `401 invalid_grant`
from the refresh ends the session. During a database outage a refresh answers `503 temporarily_unavailable` with `Retry-After: 5` instead of
`401`; the cookie is neither cleared nor rotated, so the same cookie works once the database is back, and the sample keeps the session too.
Keep both rules in your own interceptor: a `429` or a `503` is never a reason to sign out. The behind-one-address limits apply to people behind a
shared NAT together; the lockout of a person's own address (`429 too_many_attempts`) is a different answer and the sign-in screen words it itself.
```

- [ ] **Step 7: Run the tests, then the whole Angular suite and the build.**

```bash
cd samples/notes-web
npx ng test --watch=false
npx ng build --configuration production
```

  Expected: every spec passes (the new ones and the 361 unit tests in 17 files of slice 7), and the production build is clean. Then, from the same directory, the two checks of slice 7 that touch what this task edits: `npx tsc -p e2e/tsconfig.json` (prints nothing) and `npm run check:docs` (the integration guide: still seven numbered steps, every path and file name in a code span exists, no product named; prints `PASS the guide: ...`). The Playwright tests (`e2e/07-headers.spec.ts` as edited in Step 4b and `e2e/08-proxy-headers.spec.ts`) run with `scripts/e2e-web.sh`, which is Task 12's and the verifiers' (it needs the stack's ports 8088, 8443, 8080 and 8025, and a project name of `auth-core-web` or `auth-core-web-<suffix>`: it refuses any other, and a name that is running): if the ports are free, run it once and report its result; otherwise say that it was not run.

- [ ] **Step 8: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(samples): proxy headers and the client address, and the Angular notice for 429 and 503`.

### Task 11: `scripts/e2e-hardening.sh` and the older e2e scripts

**Files:**
- Create: `scripts/e2e-hardening.sh`
- Modify: `scripts/e2e-login.sh`, `scripts/e2e-refresh.sh`, `scripts/e2e-lockout.sh`, `scripts/e2e-email.sh`, `scripts/e2e-tenancy.sh` (header comments only), `README.md` (one paragraph, see Step 4; Task 14 rewrites the README, so this edit is small and anchored)

**Interfaces:**
- Consumes: the live stack of `deploy/docker-compose.yml` started with the **default** limits (no `AUTH_RATE_LIMIT_*` set); the operator CLI; the mail catcher; `docker compose exec postgres psql` to read `audit_events`; everything of Tasks 1–9.
- Produces: `scripts/e2e-hardening.sh`, which covers criteria 1, 3, 4, 5, 6, 8 and 10 of the spec on a live stack (and the live half of 2): the table of the spec's "Verification notes" for the verifiers.

**How the older scripts meet the limits** (probed: `e2e-lockout.sh` makes about 58 logins a minute, over the 30; `e2e-email.sh` makes exactly 10 requests for a mail, with no margin; the others are well inside). The stack of the older scripts is started with `AUTH_RATE_LIMIT_ENABLED=false`; `e2e-hardening.sh` runs last, on the same stack recreated with the defaults (`docker compose ... up -d` after the variable is unset recreates `auth`, because its environment changed). The header of each older script says so. `e2e-hardening.sh` makes its own logins with a new unknown address each time, so that the lockout of spec 0003 (`429 too_many_attempts`) can never be taken for, or hide, the limiter's `429 too_many_requests`.

**What `e2e-hardening.sh` checks, in order** (it is the only script that stops PostgreSQL; a `trap` starts it again however the script ends):

| Step | Checks | Criteria |
| --- | --- | --- |
| 1 | the headers of the table on `200`, `HEAD`, `404`, `405`, the key set and the OpenAPI document (the last two without `Cache-Control`), **no `Server` header on the live server**; `HEAD /auth/health` has an empty body (a raw request to Kestrel); `POST /auth/health` is `405` with `Allow: GET, HEAD`; a body declared `charset=utf-16` is `415` on login and on `DELETE /auth/org`, and `charset=utf-8` is read; the interactive reference's own policy with a nonce | 4, 5, 6 |
| 2 | after a minute without requests: 30 logins of new unknown addresses (each with another forged `X-Forwarded-For`) are `401`, the 31st is `429 too_many_requests` with `Retry-After`; the 31st left no failed-login row (no password was evaluated); 30 failed-login rows, one `rate_limit.hit`, all with the same recorded address, which is not one the client wrote | 1, 2, 8 |
| 3 | each other policy at its own number: refresh (60), mail requests (10), invitation requests (20), the rest (300); one `rate_limit.hit` row for each of the five policies | 1, 8 |
| 4 | a refresh while PostgreSQL is stopped: `503 temporarily_unavailable`, `Retry-After: 5`, no `Set-Cookie`, the headers; the health check still `200`; a login is `500 internal_error`; after PostgreSQL starts, the same cookie refreshes (`200`); a logout and a replayed refresh token leave their rows | 3, 4, 8 |
| 5 | the operator makes a company and its admin; the admin makes a pending invitation; `DELETE /auth/org` with a wrong password (`403 wrong_password`), a wrong name (`400`) and the right ones (`204`); afterwards the access token is `403 permissions_changed`, the refresh `401 invalid_grant`, the login `403 no_membership`, the pending link `invalid_token`; the company's rows and the queued mail of an invitation are gone (that the mail is never sent is pinned by `CompanyDeletionTests`: the dispatcher may legitimately send a queued request in the second before the deletion, so a live check of it would be a race); the accounts stay | 10, 8 |
| 6 | the audit log has a row of each kind this run produced; none of the passwords or links this run used is in any column | 8 |

- [ ] **Step 1: Write the script.** `scripts/e2e-hardening.sh`:

```bash
#!/usr/bin/env bash
# Real-network end-to-end check of spec 0008 (hardening) against the development compose stack, started with the DEFAULT rate limits.
#
# What it checks, in order (SEED = the development seed user; ADMIN = the first admin of a company made for the run):
#   1. the security headers on 200, HEAD, 404, 405, the key set and the OpenAPI document (the last two without Cache-Control) and no
#      Server header on the live server; POST /auth/health is 405 with Allow: GET, HEAD; a body declared charset=utf-16 is 415 on login
#      and on DELETE /auth/org, charset=utf-8 is read; the interactive reference has its own policy with a nonce - criteria 4, 5, 6.
#   2. after a minute without requests: 30 logins of new unknown addresses (each with another forged X-Forwarded-For) are 401, the 31st
#      is 429 too_many_requests with Retry-After; it evaluated no password (no failed-login row); 30 failed-login rows and one
#      rate_limit.hit carry the same recorded address, which the client did not write - criteria 1, 2, 8.
#   3. refresh (60 a minute), mail requests (10), invitation requests (20) and the rest (300) each answer 429 at their own number; the
#      audit log has a rate_limit.hit row for each of the five policies - criteria 1, 8.
#   4. a refresh while PostgreSQL is stopped is 503 temporarily_unavailable with Retry-After: 5 and no Set-Cookie; the health check
#      still answers; a login is 500 internal_error; once PostgreSQL is back the same cookie refreshes; a logout and a replayed refresh
#      token leave their audit rows - criteria 3, 4, 8.
#   5. the operator makes a company and its admin, the admin has a pending invitation and the operator queues another; DELETE /auth/org
#      with a wrong password (403 wrong_password), a wrong name (400) and the right ones (204); then the old access token is 403
#      permissions_changed, the refresh 401 invalid_grant, the login 403 no_membership, the pending link invalid_token, the company's
#      rows and the queued mail are gone, and the accounts stay - criteria 10, 8. (That the queued mail is never sent is not checked here: the
#      dispatcher may send a queued request in the second before the deletion; CompanyDeletionTests pins it.)
#   6. the audit log holds a row of each kind the run produced, and none of the passwords or links the run used - criterion 8.
#
# Full sequence, from the repo root. The older scripts run first on a stack whose limiter is off (the lockout script makes about 58
# logins a minute and the mail script exactly 10 requests for a mail); this one runs last on the same stack recreated WITH the defaults:
#   cp .env.example .env                  # then set real local values (git-ignored); leave the AUTH_RATE_LIMIT_* lines blank
#   MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh
#   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, for the whole sequence: never "auth-core", the development stack
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build
#   scripts/e2e-login.sh && scripts/e2e-refresh.sh && scripts/e2e-lockout.sh && scripts/e2e-email.sh && scripts/e2e-tenancy.sh
#   docker compose -f deploy/docker-compose.yml --env-file .env up -d            # recreates auth with the defaults (AUTH_RATE_LIMIT_ENABLED unset)
#   scripts/e2e-hardening.sh              # this script (does NOT bring the stack up or down; it stops and starts PostgreSQL)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# Reads AUTH_DEV_SEED_EMAIL and AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced). The operator commands run as
# `docker compose run --rm -T --no-deps auth admin ...`; the audit log is read with `docker compose exec postgres psql`. Needs: curl,
# python3 (standard library only), docker compose. Env: BASE_URL (default http://localhost:8080), MAILPIT_URL (default
# http://localhost:8025), COMPOSE_PROJECT_NAME (default auth-core-hardening: the project of the running stack, which this script stops
# PostgreSQL in, so "auth-core" is refused), E2E_NO_SETTLE=1 (skips the minute of
# waiting before step 2 and the one after step 3: only when nothing else has sent requests to the stack for a minute).
# Takes about six minutes: two minutes of waiting for the limiter's windows to empty, and up to two for the mail of the new admin.
# Re-runnable on the same stack. Exits non-zero on the first failure; prints "PASS <step>"
# per step; never prints a password, a token, a cookie or a mail body. Request bodies are built into files in a mktemp -d
# directory (removed on exit) and curl reads them with --data-binary @file; tokens and cookies reach curl through header files;
# a secret that is looked for in the audit log goes to psql on its standard input, never on a command line.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
# Step 4 stops PostgreSQL of the stack: never of the development stack, whose project is "auth-core".
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-hardening}"
[[ "$COMPOSE_PROJECT_NAME" != "auth-core" ]] \
  || { echo "FAIL COMPOSE_PROJECT_NAME=auth-core is the development stack: this script stops its PostgreSQL; use another name" >&2; exit 1; }
compose=(docker compose -f "$root/deploy/docker-compose.yml" --env-file "$root/.env")

tmp="$(mktemp -d)"
POSTGRES_STOPPED=0
cleanup() {
  # a run that dies during step 4 must not leave PostgreSQL stopped
  if [[ "$POSTGRES_STOPPED" == "1" ]]; then
    "${compose[@]}" start postgres > /dev/null 2>&1 || echo "WARNING: PostgreSQL could not be started again: start it by hand (docker compose start postgres)" >&2
  fi
  rm -rf "$tmp"
}
trap cleanup EXIT

fail() { echo "FAIL $*" >&2; exit 1; }
pass() { echo "PASS $*"; }

env_get() { # read KEY from .env without executing it; strips one pair of surrounding quotes
  local line
  line="$(grep -E "^$1=" "$root/.env" | tail -n1 || true)"
  line="${line#*=}"
  line="${line%$'\r'}"
  if [[ "$line" =~ ^\"(.*)\"$ || "$line" =~ ^\'(.*)\'$ ]]; then line="${BASH_REMATCH[1]}"; fi
  printf '%s' "$line"
}

[[ -f "$root/.env" ]] || fail "missing $root/.env (copy .env.example and set local values)"
SEED_EMAIL="$(env_get AUTH_DEV_SEED_EMAIL)"
SEED_PASSWORD="$(env_get AUTH_DEV_SEED_PASSWORD)"
[[ -n "$SEED_EMAIL" && -n "$SEED_PASSWORD" ]] || fail "AUTH_DEV_SEED_EMAIL / AUTH_DEV_SEED_PASSWORD not set in .env"

run="$RANDOM$RANDOM"
ADMIN_EMAIL="boss-$run@hardening.test"
PENDING_EMAIL="pending-$run@hardening.test"
QUEUED_EMAIL="queued-$run@hardening.test"
ORG_NAME="Hardening $run"
WRONG_PASSWORD="Wrong-Password-1"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
export E2E_ADMIN_EMAIL="$ADMIN_EMAIL" E2E_PASSWORD="E2e-Passw0rd-$run" E2E_ORG_NAME="$ORG_NAME"

wait_healthy() {
  local i
  for i in $(seq 1 90); do
    if [[ "$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "$BASE_URL/auth/health" || true)" == "200" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

wait_mailpit() {
  local i
  for i in $(seq 1 60); do
    if [[ "$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "$MAILPIT_URL/readyz" || true)" == "200" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

# call <method> <path> [body-file] [auth-header-file] [content-type] -> sets HTTP_CODE and BODY; response headers in $tmp/hdr.
# EXTRA_HEADER, when set, is sent as one more header (a forged X-Forwarded-For).
call() {
  local args=(-sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' -X "$1" "$BASE_URL$2")
  if [[ -n "${3:-}" ]]; then args+=(-H "Content-Type: ${5:-application/json}" --data-binary "@$3"); fi
  if [[ -n "${4:-}" ]]; then args+=(-H "@$4"); fi
  if [[ -n "${EXTRA_HEADER:-}" ]]; then args+=(-H "$EXTRA_HEADER"); fi
  HTTP_CODE="$(curl "${args[@]}")"
  BODY="$(cat "$tmp/body")"
}

# call_head <path>: a HEAD request, headers in $tmp/hdr
call_head() {
  HTTP_CODE="$(curl -sS --max-time 20 -I -o /dev/null -D "$tmp/hdr" -w '%{http_code}' "$BASE_URL$1")"
  BODY=""
}

# head_body_bytes <path>: sends a raw HEAD request and prints how many bytes follow the header block (Kestrel sends none, whatever the
# in-memory test server of the .NET tests does); -1 when the answer has no header block.
head_body_bytes() {
  python3 - "$BASE_URL" "$1" <<'PY' | tr -d '\r'
import socket, sys, urllib.parse
u = urllib.parse.urlparse(sys.argv[1])
s = socket.create_connection((u.hostname, u.port or 80), timeout=10)
s.sendall(("HEAD %s HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n" % (sys.argv[2], u.netloc)).encode("ascii"))
data = b""
while True:
    chunk = s.recv(65536)
    if not chunk:
        break
    data += chunk
head, sep, rest = data.partition(b"\r\n\r\n")
print(len(rest) if sep else -1)
PY
}

header() { # value of a response header of the last response, CR stripped (empty when absent)
  { grep -i "^$1:" "$tmp/hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//'
}

# The python3 of some machines is a Windows interpreter, which ends its lines with CR LF: the helpers below that print a value
# strip the CR, so that the value compares equal in bash.

# val <python-expression-of-d>: evaluates it against the JSON of $BODY and prints the result
val() { python3 -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1" <<< "$BODY" | tr -d '\r'; }

json_body() { # json_body <file> <python-expression>: writes the JSON of the expression, which may read os.environ
  python3 -c 'import json,os,sys; print(json.dumps(eval(sys.argv[1])))' "$2" > "$1"
}

expect_status() { # expect_status <what> <code> [exact-body]
  [[ "$HTTP_CODE" == "$2" ]] || fail "$1: HTTP $HTTP_CODE, expected $2"
  if [[ $# -ge 3 ]]; then [[ "$BODY" == "$3" ]] || fail "$1: unexpected body"; fi
}

expect_error() { expect_status "$1" "$2" "{\"error\":\"$3\"}"; }

expect_eq() { [[ "$2" == "$3" ]] || fail "$1: got '$2', expected '$3'"; }

expect_no_cookie() { [[ -z "$(header set-cookie)" ]] || fail "$1: the answer sets a cookie"; }

# expect_security_headers <what> [cacheable]: every header of the table, no Server and no HSTS; Cache-Control: no-store with
# Pragma: no-cache, except on the key set and the OpenAPI document ("cacheable"), which send none.
expect_security_headers() {
  [[ "$(header x-content-type-options)" == "nosniff" ]] || fail "$1: X-Content-Type-Options is not nosniff"
  [[ "$(header x-frame-options)" == "DENY" ]] || fail "$1: X-Frame-Options is not DENY"
  [[ "$(header content-security-policy)" == "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'" ]] \
    || fail "$1: Content-Security-Policy is not the strict one"
  [[ "$(header referrer-policy)" == "no-referrer" ]] || fail "$1: Referrer-Policy is not no-referrer"
  [[ "$(header cross-origin-resource-policy)" == "same-origin" ]] || fail "$1: Cross-Origin-Resource-Policy is not same-origin"
  [[ -z "$(header server)" ]] || fail "$1: a Server header is sent"
  [[ -z "$(header strict-transport-security)" ]] || fail "$1: Auth-Core sends HSTS (the proxy does)"
  if [[ "${2:-}" == "cacheable" ]]; then
    [[ -z "$(header cache-control)" ]] || fail "$1: the answer sends a Cache-Control"
  else
    [[ "$(header cache-control)" == *no-store* ]] || fail "$1: Cache-Control is not no-store"
    [[ "$(header pragma)" == *no-cache* ]] || fail "$1: Pragma is not no-cache"
  fi
}

# expect_too_many_requests <what>: the 429 of the per-address limit, whole: body, Retry-After, no-store, the headers, no cookie
expect_too_many_requests() {
  [[ "$HTTP_CODE" == "429" ]] || fail "$1: HTTP $HTTP_CODE, expected 429"
  [[ "$BODY" =~ ^\{\"error\":\"too_many_requests\",\"retry_after_seconds\":([0-9]+)\}$ ]] || fail "$1: the body is not the 429 of the limiter"
  local seconds="${BASH_REMATCH[1]}"
  (( seconds >= 1 && seconds <= 60 )) || fail "$1: retry_after_seconds is $seconds, not between 1 and 60"
  [[ "$(header retry-after)" == "$seconds" ]] || fail "$1: Retry-After is not $seconds"
  expect_no_cookie "$1"
  expect_security_headers "$1"
}

# login <name> <email-env> <password-env>: logs in, keeps the bearer header in $tmp/<name>.auth and the cookie in $tmp/<name>.cookie
login() {
  json_body "$tmp/$1.login" "{'email': os.environ['$2'], 'password': os.environ['$3']}"
  call POST /auth/login "$tmp/$1.login"
  expect_status "login of $1" 200
  keep_session "$1"
}

keep_session() { # keep_session <name>: the access token of $BODY and the cookie of the last response
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$1.auth"
  local pair
  pair="$({ grep -i '^set-cookie:[[:space:]]*auth_rt=[^;]' "$tmp/hdr" || true; } | head -n1 | tr -d '\r' | cut -d: -f2- | sed 's/^ *//' | cut -d';' -f1)"
  [[ -n "$pair" ]] || fail "the response set no auth_rt cookie for $1"
  printf 'Cookie: %s\n' "$pair" > "$tmp/$1.cookie"
}

# psql_value <sql>: the value the query returns. The SQL is this script's own text: a value that came from the service under test is
# never put into it (the run's own addresses and ids are). It goes in on the standard input.
psql_value() {
  printf '%s\n' "$1" | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'
}

# secret_in_audit <secret>: how many rows of the audit log hold the text in any column; the secret goes on stdin only
secret_in_audit() {
  [[ "$1" != *"'"* ]] || fail "a secret with a quote cannot be looked for"
  printf "SELECT count(*) FROM audit_events WHERE position('%s' in (kind || ' ' || coalesce(subject_email, '') || ' ' || coalesce(org_name, '') || ' ' || coalesce(client_ip, '') || ' ' || coalesce(details::text, ''))) > 0;\n" "$1" \
    | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'
}

# flood <method> <path> <times> [body]: one curl, the same request <times> times; the status of each is a line of $tmp/flood.codes
flood() {
  local args=(-sS --max-time 180 -o /dev/null -w '%{http_code}\n' -X "$1") urls=() i
  if [[ -n "${4:-}" ]]; then args+=(-H 'Content-Type: application/json' --data-binary "$4"); fi
  for ((i = 0; i < $3; i++)); do urls+=("$BASE_URL$2"); done
  curl "${args[@]}" "${urls[@]}" | tr -d '\r' > "$tmp/flood.codes" || fail "the burst of $3 requests to $2 failed"
}

# first_429: the line number (the request number) of the first 429 in $tmp/flood.codes; fails when there is none, or when a status before it is not $1
first_429() {
  local n
  n="$(awk '$1 == 429 { print NR; exit }' "$tmp/flood.codes")"
  [[ -n "$n" ]] || fail "$2: no request of the burst was refused"
  if (( n > 1 )) && [[ -n "$(head -n "$((n - 1))" "$tmp/flood.codes" | grep -vx "$1" || true)" ]]; then
    fail "$2: before the first 429 some request answered something other than $1"
  fi
  echo "$n"
}

# --- the mail catcher ---------------------------------------------------------------------------------------------

mail_count() {
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" -o "$tmp/search.json"
  python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages_count"])' < "$tmp/search.json" | tr -d '\r'
}

# wait_mail <address> <count> <seconds>: waits until the catcher holds <count> mails for the address, then saves the newest
wait_mail() {
  local i id
  for i in $(seq 1 "$3"); do
    if [[ "$(mail_count "$1")" == "$2" ]]; then
      id="$(python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages"][0]["ID"])' < "$tmp/search.json" | tr -d '\r')"
      curl -sS --max-time 10 "$MAILPIT_URL/api/v1/message/$id" -o "$tmp/mail.json"
      return 0
    fi
    sleep 1
  done
  return 1
}

# token_body <file> [password-env]: takes the token of the invitation link from the text part of $tmp/mail.json and writes the
# request body ({"token"} or {"token","password"}). Never printed.
token_body() {
  python3 -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(r"http://localhost:4200/invite\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no invitation link with a token")
body = {"token": match.group(1)}
if len(sys.argv) > 1 and sys.argv[1]:
    body["password"] = os.environ[sys.argv[1]]
print(json.dumps(body))
' "${2:-}" < "$tmp/mail.json" > "$1"
}

# token_of <body-file>: the token of a body made by token_body (only to look for it in the audit log, never to print)
token_of() { python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])' < "$1" | tr -d '\r'; }

# cli <args...>: runs `auth-server admin <args>` in a one-off container; sets CLI_OUT (stdout), CLI_EXIT, and keeps stderr in $tmp/cli.err
cli() {
  CLI_EXIT=0
  CLI_OUT="$("${compose[@]}" run --rm -T --no-deps auth admin "$@" 2> "$tmp/cli.err")" || CLI_EXIT=$?
  CLI_OUT="${CLI_OUT//$'\r'/}"
}

UUID_PATTERN='^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
uuid_or_fail() { [[ "$2" =~ $UUID_PATTERN ]] || fail "$1 is not a UUID"; }

wait_healthy || fail "$BASE_URL/auth/health did not return 200 within 90s (is the stack up? see the header)"
wait_mailpit || fail "$MAILPIT_URL/readyz did not return 200 within 60s (is the mailpit service up?)"
START_TS="$(psql_value "SELECT now()")"
[[ -n "$START_TS" ]] || fail "the audit log cannot be read: is the compose project of the running stack the one this script uses? (set COMPOSE_PROJECT_NAME)"

# --- Step 1: the headers, the methods, the charset ----------------------------------------------------------------------------
call GET /auth/health
expect_status "step 1: GET /auth/health" 200 "Healthy"
expect_security_headers "step 1: GET /auth/health"
call_head /auth/health
expect_eq "step 1: HEAD /auth/health" "$HTTP_CODE" "200"
expect_security_headers "step 1: HEAD /auth/health"
expect_eq "step 1: the bytes after the headers of HEAD /auth/health" "$(head_body_bytes /auth/health)" "0"
call POST /auth/health
expect_status "step 1: POST /auth/health" 405
expect_eq "step 1: the Allow of POST /auth/health" "$(header allow)" "GET, HEAD"
expect_security_headers "step 1: POST /auth/health"
call GET /nope
expect_status "step 1: the framework's 404" 404
expect_security_headers "step 1: the framework's 404"
call GET /auth/logout
expect_status "step 1: the framework's 405" 405
expect_security_headers "step 1: the framework's 405"
call GET /auth/.well-known/jwks.json
expect_status "step 1: the key set" 200
expect_security_headers "step 1: the key set" cacheable
call GET /auth/openapi/v1.json
expect_status "step 1: the OpenAPI description" 200
expect_security_headers "step 1: the OpenAPI description" cacheable
json_body "$tmp/charset.json" "{'email': 'charset-$run@example.invalid', 'password': 'x'}"
call POST /auth/login "$tmp/charset.json" "" "application/json; charset=utf-16"
expect_error "step 1: a login body declared utf-16" 415 unsupported_media_type
expect_security_headers "step 1: the 415"
call DELETE /auth/org "$tmp/charset.json" "" "application/json; charset=utf-16"
expect_error "step 1: DELETE /auth/org with a body declared utf-16 (before the token is looked at)" 415 unsupported_media_type
call POST /auth/login "$tmp/charset.json" "" "application/json; charset=utf-8"
expect_error "step 1: a login body declared utf-8 is read" 401 invalid_credentials
call POST /auth/login "$tmp/charset.json"
expect_error "step 1: a login body with no charset is read" 401 invalid_credentials
# The interactive reference (Development): a policy of its own, with a nonce that is in the page. The slash matters: /auth/scalar redirects.
curl -sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" "$BASE_URL/auth/scalar/" || fail "step 1: the interactive reference did not answer"
POLICY="$(header content-security-policy)"
grep -Eq "^default-src 'none'; script-src 'self' 'nonce-[A-Za-z0-9+/=_-]+'; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'$" <<< "$POLICY"   || fail "step 1: the interactive reference has not the policy of the spec"
NONCE="$(sed -E "s/.*'nonce-([^']+)'.*/\1/" <<< "$POLICY")"
grep -q -F -- "$NONCE" "$tmp/body" || fail "step 1: the nonce of the policy is not in the page"
[[ "$(header x-frame-options)" == "DENY" && "$(header x-content-type-options)" == "nosniff" ]] || fail "step 1: the interactive reference lacks a header"
pass "step 1: the headers are on 200, HEAD, 404, 405, 415 and the two documents (no Server header); POST /auth/health is 405 Allow: GET, HEAD; utf-16 is 415, utf-8 is read; the reference has its own policy"

# --- Step 2: the login limit -----------------------------------------------------------------------------------------------
if [[ "${E2E_NO_SETTLE:-0}" != "1" ]]; then
  echo "waiting a minute so that the limiter's window is empty..."
  sleep 61
fi
for i in $(seq 1 30); do
  json_body "$tmp/flood.json" "{'email': 'nobody-$run-$i@example.invalid', 'password': '$WRONG_PASSWORD'}"
  EXTRA_HEADER="X-Forwarded-For: 198.51.100.$i" call POST /auth/login "$tmp/flood.json"
  expect_error "step 2: login $i of an unknown address" 401 invalid_credentials
done
json_body "$tmp/flood.json" "{'email': 'nobody-$run-31@example.invalid', 'password': '$WRONG_PASSWORD'}"
EXTRA_HEADER="X-Forwarded-For: 198.51.100.31" call POST /auth/login "$tmp/flood.json"
if [[ "$HTTP_CODE" == "401" ]]; then
  fail "step 2: the 31st login was not refused: is the limiter off? Recreate the stack without AUTH_RATE_LIMIT_ENABLED=false (see the header)"
fi
expect_too_many_requests "step 2: the 31st login"
[[ "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.failed' AND subject_email = 'nobody-$run-31@example.invalid'")" == "0" ]] \
  || fail "step 2: the refused login evaluated a password (it left a failed-login row)"
expect_eq "step 2: the failed logins that were evaluated" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.failed' AND details->>'reason' = 'unknown_address' AND subject_email LIKE 'nobody-$run-%@example.invalid'")" "30"
expect_eq "step 2: one recorded address for the 31 logins" \
  "$(psql_value "SELECT count(DISTINCT client_ip) FROM audit_events WHERE kind IN ('login.failed', 'rate_limit.hit') AND occurred_at >= '$START_TS' AND (subject_email LIKE 'nobody-$run-%@example.invalid' OR (kind = 'rate_limit.hit' AND details->>'policy' = 'login'))")" "1"
RECORDED="$(psql_value "SELECT client_ip FROM audit_events WHERE kind = 'login.failed' AND subject_email = 'nobody-$run-1@example.invalid'")"
[[ -n "$RECORDED" && "$RECORDED" != 198.51.100.* ]] || fail "step 2: the recorded address is one the client wrote into X-Forwarded-For ($RECORDED)"
expect_eq "step 2: one rate_limit.hit for the login policy" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'rate_limit.hit' AND details->>'policy' = 'login' AND occurred_at >= '$START_TS'")" "1"
pass "step 2: 30 logins are 401, the 31st is 429 with Retry-After and evaluated no password; the forged X-Forwarded-For changed neither the count nor the recorded address; one rate_limit.hit"

# --- Step 3: the other limits ----------------------------------------------------------------------------------------------------
flood POST /auth/refresh 70 '{}'
N="$(first_429 401 "step 3: refresh")"
(( N >= 56 && N <= 61 )) || fail "step 3: the first refused refresh was request $N, expected about the 61st"
call POST /auth/refresh "" "" 
expect_too_many_requests "step 3: refresh"
for i in $(seq 1 10); do
  json_body "$tmp/mail.json.req" "{'email': 'mail-$run-$i@example.invalid'}"
  call POST /auth/password/forgot "$tmp/mail.json.req"
  expect_status "step 3: mail request $i" 202 ""
done
json_body "$tmp/mail.json.req" "{'email': 'mail-$run-11@example.invalid'}"
call POST /auth/password/forgot "$tmp/mail.json.req"
expect_too_many_requests "step 3: the 11th request for a mail"
flood POST /auth/invites/preview 25 '{"token":"not-a-token"}'
N="$(first_429 400 "step 3: invitation requests")"
(( N >= 16 && N <= 21 )) || fail "step 3: the first refused invitation request was request $N, expected about the 21st"
flood GET /auth/health 330
N="$(first_429 200 "step 3: general requests")"
(( N >= 290 && N <= 301 )) || fail "step 3: the first refused request was request $N, expected about the 301st"
expect_eq "step 3: a rate_limit.hit row for each of the five policies" \
  "$(psql_value "SELECT count(DISTINCT details->>'policy') FROM audit_events WHERE kind = 'rate_limit.hit' AND occurred_at >= '$START_TS'")" "5"
pass "step 3: refresh, mail, invitation and general requests are refused at their own numbers; one rate_limit.hit row for each of the five policies"

if [[ "${E2E_NO_SETTLE:-0}" != "1" ]]; then
  echo "waiting a minute so that the limiter's windows empty..."
  sleep 61
fi

# --- Step 4: a refresh during an outage ------------------------------------------------------------------------------------
login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD
cp "$tmp/seed.cookie" "$tmp/seed.cookie.kept"
POSTGRES_STOPPED=1
"${compose[@]}" stop postgres > "$tmp/stop.log" 2>&1 || { cat "$tmp/stop.log" >&2; fail "step 4: could not stop PostgreSQL"; }
call POST /auth/refresh "" "$tmp/seed.cookie"
expect_error "step 4: a refresh while PostgreSQL is stopped" 503 temporarily_unavailable
expect_eq "step 4: its Retry-After" "$(header retry-after)" "5"
expect_no_cookie "step 4: the 503 of the refresh"
expect_security_headers "step 4: the 503 of the refresh"
call GET /auth/health
expect_status "step 4: the health check does not reach the database" 200 "Healthy"
json_body "$tmp/outage.login" "{'email': os.environ['E2E_SEED_EMAIL'], 'password': os.environ['E2E_SEED_PASSWORD']}"
call POST /auth/login "$tmp/outage.login"
expect_error "step 4: a login while PostgreSQL is stopped" 500 internal_error
expect_no_cookie "step 4: the 500 of the login"
expect_security_headers "step 4: the 500 of the login"
"${compose[@]}" start postgres > "$tmp/start.log" 2>&1 || { cat "$tmp/start.log" >&2; fail "step 4: could not start PostgreSQL again"; }
POSTGRES_STOPPED=0
ok=0
for _ in $(seq 1 60); do
  call POST /auth/refresh "" "$tmp/seed.cookie.kept"
  if [[ "$HTTP_CODE" == "200" ]]; then ok=1; break; fi
  expect_error "step 4: while PostgreSQL starts again" 503 temporarily_unavailable
  sleep 2
done
[[ "$ok" == "1" ]] || fail "step 4: the same cookie did not refresh within two minutes of PostgreSQL's start"
keep_session seed
# A logout and a replayed refresh token (the leeway is 15 seconds) leave their rows.
cp "$tmp/seed.cookie" "$tmp/seed.cookie.rotated"
call POST /auth/refresh "" "$tmp/seed.cookie"
expect_status "step 4: a refresh to be replayed" 200
sleep 16
call POST /auth/refresh "" "$tmp/seed.cookie.rotated"
expect_error "step 4: the replayed refresh token" 401 invalid_grant
login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD
call POST /auth/logout "" "$tmp/seed.cookie"
expect_status "step 4: logout" 204 ""
pass "step 4: with PostgreSQL stopped a refresh is 503 temporarily_unavailable (Retry-After 5, no cookie), health is 200, a login is 500 internal_error; afterwards the same cookie refreshes"

# --- Step 5: deleting a company -----------------------------------------------------------------------------------------------
cli create-org --name "$ORG_NAME"
[[ "$CLI_EXIT" == "0" ]] || fail "step 5: create-org failed"
ORG="$CLI_OUT"
uuid_or_fail "step 5: the id of the new company" "$ORG"
cli invite --org "$ORG" --email "$ADMIN_EMAIL" --role admin
[[ "$CLI_EXIT" == "0" ]] || fail "step 5: the invitation of the admin failed"
wait_mail "$ADMIN_EMAIL" 1 150 || fail "step 5: no invitation mail for the admin within 150s"
token_body "$tmp/accept.json" E2E_PASSWORD
ADMIN_LINK="$(token_of "$tmp/accept.json")"
call POST /auth/invites/accept "$tmp/accept.json"
expect_status "step 5: the admin accepts" 204 ""
login admin E2E_ADMIN_EMAIL E2E_PASSWORD
call GET /auth/org/roles "" "$tmp/admin.auth"
expect_status "step 5: GET /auth/org/roles" 200
export E2E_USER_ROLE
E2E_USER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "user"][0]')"
uuid_or_fail "step 5: the id of the user role" "$E2E_USER_ROLE"
json_body "$tmp/invite.json" "{'email': '$PENDING_EMAIL', 'role_id': os.environ['E2E_USER_ROLE']}"
call POST /auth/org/invites "$tmp/invite.json" "$tmp/admin.auth"
expect_status "step 5: the admin invites a person" 202 ""
wait_mail "$PENDING_EMAIL" 1 40 || fail "step 5: no invitation mail for the pending person within 40s (an API invitation wakes the dispatcher)"
token_body "$tmp/pending.json"
PENDING_LINK="$(token_of "$tmp/pending.json")"
call POST /auth/invites/preview "$tmp/pending.json"
expect_status "step 5: the pending link works before the deletion" 200
json_body "$tmp/delete.json" "{'name': os.environ['E2E_ORG_NAME'], 'password': '$WRONG_PASSWORD'}"
call DELETE /auth/org "$tmp/delete.json" "$tmp/admin.auth"
expect_error "step 5: DELETE /auth/org with a wrong password" 403 wrong_password
json_body "$tmp/delete.json" "{'name': 'not the name', 'password': os.environ['E2E_PASSWORD']}"
call DELETE /auth/org "$tmp/delete.json" "$tmp/admin.auth"
expect_error "step 5: DELETE /auth/org with a wrong name" 400 invalid_request
# Queued now, so that the deletion follows within a second: the server mails a queued request at its next poll, within a minute.
cli invite --org "$ORG" --email "$QUEUED_EMAIL" --role user   # queued; the server would mail it at its next poll
[[ "$CLI_EXIT" == "0" ]] || fail "step 5: the operator's invitation failed"
json_body "$tmp/delete.json" "{'name': os.environ['E2E_ORG_NAME'], 'password': os.environ['E2E_PASSWORD']}"
call DELETE /auth/org "$tmp/delete.json" "$tmp/admin.auth"
expect_status "step 5: DELETE /auth/org with the name and the password" 204 ""
expect_security_headers "step 5: the 204"
call GET /auth/me "" "$tmp/admin.auth"
expect_error "step 5: the old access token" 403 permissions_changed
call POST /auth/refresh "" "$tmp/admin.cookie"
expect_error "step 5: the refresh of a member of a deleted company" 401 invalid_grant
expect_no_cookie "step 5: that refresh"
call POST /auth/login "$tmp/admin.login"
expect_error "step 5: a new login" 403 no_membership
call POST /auth/invites/preview "$tmp/pending.json"
expect_error "step 5: the pending invitation link" 400 invalid_token
for table in '"Companies" WHERE "Id"' '"CompanyRoles" WHERE "CompanyId"' '"Memberships" WHERE "CompanyId"' '"Invites" WHERE "CompanyId"'; do
  expect_eq "step 5: the rows of $table" "$(psql_value "SELECT count(*) FROM $table = '$ORG'")" "0"
done
expect_eq "step 5: the queued mail of the company's invitation" \
  "$(psql_value "SELECT count(*) FROM \"MailRequests\" WHERE \"NormalizedEmail\" = '${QUEUED_EMAIL^^}'")" "0"
expect_eq "step 5: the account of the admin stays" \
  "$(psql_value "SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = '${ADMIN_EMAIL^^}'")" "1"
expect_eq "step 5: the deletion is in the audit log" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'org.deleted' AND org_id = '$ORG' AND org_name = '$ORG_NAME' AND actor_user_id IS NOT NULL")" "1"
expect_eq "step 5: the refused deletion is in the audit log" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'org.delete_refused' AND org_id = '$ORG' AND details->>'reason' = 'wrong_password'")" "1"
pass "step 5: DELETE /auth/org (403 wrong_password, 400 wrong name, 204); then permissions_changed, invalid_grant, no_membership, an invalid link, no rows left, the queued mail gone, the account stays"

# --- Step 6: the audit log -------------------------------------------------------------------------------------------------------
KINDS="$(psql_value "SELECT string_agg(DISTINCT kind, ',' ORDER BY kind) FROM audit_events WHERE occurred_at >= '$START_TS'")"
for kind in login.succeeded login.failed logout refresh.reuse_detected password.reset_requested invite.sent invite.accepted org.created org.deleted org.delete_refused rate_limit.hit; do
  [[ ",$KINDS," == *",$kind,"* ]] || fail "step 6: this run left no audit row of the kind $kind (kinds: $KINDS)"
done
for secret in "$SEED_PASSWORD" "$E2E_PASSWORD" "$WRONG_PASSWORD" "$ADMIN_LINK" "$PENDING_LINK"; do
  [[ -n "$secret" ]] || fail "step 6: a secret to look for is empty"
  expect_eq "step 6: rows of the audit log that hold a password or a link of this run" "$(secret_in_audit "$secret")" "0"
done
pass "step 6: the audit log has a row of each kind this run produced, and none of its passwords or links"

echo "ALL PASS"
```

- [ ] **Step 2: Make it executable in the index and check its syntax.** The file is LF (`.gitattributes` already says `*.sh text eol=lf`). The orchestrator runs `git update-index --chmod=+x scripts/e2e-hardening.sh` after `git add`; the implementer only runs:

```bash
bash -n scripts/e2e-hardening.sh
```

  Expected: no output. (The steps that need the live stack are run by Task 12's verifier and the verifiers of Tuesday; the ports 8080 and 8025 are theirs. If they are free when you finish, run the whole sequence of the header once, with `COMPOSE_PROJECT_NAME=auth-core-hardening`, and report the result; otherwise say it was not run.)

- [ ] **Step 3: The header of each older script.** In each of `scripts/e2e-login.sh`, `e2e-refresh.sh`, `e2e-lockout.sh`, `e2e-email.sh` and `e2e-tenancy.sh`, in the "Full sequence" comment, the line that starts the stack gets the variable and one explaining line, and a line before the "clean slate" one says to use a project name of one's own (the scripts read `COMPOSE_PROJECT_NAME` from the environment, so the `down -v` of the comment, which would otherwise wipe the development stack, and the script's own `exec` and `run` all meet the same stack; the other lines of the comment stay):

```bash
for f in scripts/e2e-login.sh scripts/e2e-refresh.sh scripts/e2e-lockout.sh scripts/e2e-email.sh scripts/e2e-tenancy.sh; do
  sed -i -e '/^#   docker compose -f deploy\/docker-compose.yml --env-file .env down -v          # clean slate$/i #   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, never "auth-core" (the development stack): a bare down -v would wipe it' \
         -e 's|^#   docker compose -f deploy/docker-compose.yml --env-file .env up -d --build  |#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build  |' \
         -e '/^#   AUTH_RATE_LIMIT_ENABLED=false docker compose/a #                                         # the per-IP limiter is off for the older checks (spec 0008): the lockout script makes about 58 logins a minute and the mail script exactly 10 mail requests; scripts/e2e-hardening.sh runs last on the stack recreated with the defaults' "$f"
done
git diff --stat scripts
```

  Expected: `git diff --stat` shows the five scripts with three changed lines each (one replaced, two added); `bash -n` is clean for each; nothing else of the scripts changed.

- [ ] **Step 4: The README names the new check.** `README.md`, in the quickstart code block, **after** the line that runs `scripts/e2e-tenancy.sh`, the older block is changed by one paragraph placed right below that code block (Task 14 rewrites the README; this is the interim line):

```markdown
The per-IP rate limits (30 logins a minute, and so on) are on by default. The lockout and mail checks above exceed them on purpose, so start
that stack (with `COMPOSE_PROJECT_NAME` set to a name of your own) with `AUTH_RATE_LIMIT_ENABLED=false docker compose ... up -d --build`; `scripts/e2e-hardening.sh` (limits, headers, the outage,
company deletion, the audit log, about six minutes) runs last, on the stack recreated with the defaults (`docker compose ... up -d`).
```

  Anchor: the paragraph that begins `The stack includes a mail catcher;`: put this one **before** it.

- [ ] **Step 5: Hand back** — uncommitted. Files: `scripts/e2e-hardening.sh`, the five older scripts, `README.md`. The orchestrator makes the new script executable in the index (`git update-index --chmod=+x`). Proposed subject: `test(e2e): scripts/e2e-hardening.sh and the older scripts for the rate limits`.

### Task 12: The production test overlay and `scripts/e2e-prod.sh`

**Files:**
- Create: `scripts/prod-test.compose.yml`, `scripts/prod-test.Caddyfile`, `scripts/e2e-prod.sh`

**Interfaces:**
- Consumes: `deploy/docker-compose.prod.yml` and `deploy/.env.prod.example` (Task 9: the variables, the networks `auth` and `proxy` with the subnets `10.250.0.0/24` and `10.250.1.0/24`), the image recipe of Task 9, the settings of Production, the CLI, `scripts/dev-keys.sh`'s way of making keys (its commands, with a 3072-bit key as the key-rotation runbook says).
- Produces: a throwaway production stack for the verifiers (spec 0008 → Verification notes): the image built locally under the GHCR name `ghcr.io/mckcieply/auth-core:0.0.0-prodtest`, Mailpit as the SMTP relay **with STARTTLS required** and a certificate signed by a test authority that the `auth` container trusts through `SSL_CERT_FILE`, Caddy on `https://localhost:8443` as the proxy (the frontend URLs of the mails are on it), and `scripts/e2e-prod.sh`, which drives it and follows the backup runbook command for command (the commands are the ones of `docs/operations/backup.md`, written in Task 13: **if a command changes there it changes here**).

**What this task settles** (spec 0008 "To verify during implementation"): that Mailpit with STARTTLS and a test authority trusted through `SSL_CERT_FILE` works with the chiseled image and MailKit (the mail of an invitation arrives, and Mailpit refuses a connection that does not start TLS, so its arrival is the proof); that the production container starts with a read-only root file system (it serves `/auth/health` and runs the CLI); that the certificate check of the relay, which MailKit makes with revocation checking on and which no setting of the service turns off, passes against a certificate that has a distribution point and a list to match; that the compose file refuses to start without its secrets; and that the backup runbook, followed literally, brings back a dropped database with a refresh cookie issued before the backup still working (criteria 12 and 13).

**Why a revocation list** (a review finding, probed with OpenSSL, not with a stack): MailKit's `CheckCertificateRevocation` is `true` by default and Auth-Core does not change it (and must not: a setting that disables it would be one more thing to get wrong in Production). A leaf certificate with no CRL distribution point has the status "unknown" for the chain build, and the handshake is refused (`RevocationStatusUnknown`). So the test authority gives Mailpit's certificate a distribution point, `http://pki.test:8081/ca.crl`, and publishes an empty list there (a Caddy `file-server` container of the overlay, pinned by the digest the notes sample uses, with the alias `pki.test` on the `auth` network). The authority needs `cRLSign` in its key usage. Probed: `openssl ca -gencrl` with a four-line configuration writes the list, `openssl crl -outform DER` makes the DER form an HTTP distribution point serves, and `openssl verify -crl_check` accepts the certificate against it.

**To verify on Tuesday, first** (the verifier of the live stack): that .NET's fetch of the list and its cache work on the container's read-only root file system. The fetch is plain HTTP to `pki.test`; the cache is written under the home directory of the container's user, and a failed cache write is best effort in .NET (it fetches again), but this has not been run. If the invitation mail does not arrive in step 3 and the log of `auth` shows a revocation or an I/O error at the handshake, that is a finding about the production file, not about the test: the fix is a writable place for the cache (`HOME: /tmp` in the `auth` service of `deploy/docker-compose.prod.yml`, which has `/tmp` in memory), made in this slice, and the same finding changes `docs/deployment/vps.md` (the relay's certificate must be checkable from the container: outbound HTTP to its CRL or OCSP address).

- [ ] **Step 1: The overlay.** `scripts/prod-test.compose.yml`:

```yaml
# A throwaway test overlay for deploy/docker-compose.prod.yml (spec 0008): used by scripts/e2e-prod.sh, never in production.
#
# It adds what a production host has outside this repository: an SMTP relay that REQUIRES STARTTLS and whose certificate comes from a test
# authority, which the auth container trusts through SSL_CERT_FILE (the chiseled image has no tools to install one), a small static server
# for that authority's certificate revocation list (MailKit checks revocation by default and no setting of the service turns that off),
# and a reverse proxy on https://localhost:8443 (Caddy, `tls internal`: a browser warns, the script accepts it). The proxy is on the "proxy" network of the
# production file, whose subnet the script tells Auth-Core to trust for the client address.
#
#   docker compose -f deploy/docker-compose.prod.yml -f scripts/prod-test.compose.yml --env-file <env made by the script> up -d
services:
  mailpit:
    image: axllent/mailpit:v1.31.3@sha256:ed9b00c609e77e99c79b93f1178255ebc271868920f2c69a8d166bd5634ed10d
    environment:
      MP_SMTP_TLS_CERT: /pki/mailpit.crt
      MP_SMTP_TLS_KEY: /pki/mailpit.key
      # A client that does not start TLS gets no mail through: the arrival of a mail proves the handshake.
      MP_SMTP_REQUIRE_STARTTLS: "true"
      MP_SMTP_AUTH_ACCEPT_ANY: "true"
    volumes:
      - ${E2E_PROD_PKI_DIR:?set by scripts/e2e-prod.sh}:/pki:ro
    ports:
      # The API the script reads the mails from. Loopback only.
      - "127.0.0.1:8025:8025"
    networks:
      - auth

  pki:
    # Serves the (empty) certificate revocation list of the test authority at http://pki.test:8081/ca.crl, the address in the
    # certificate of Mailpit. Without a distribution point the revocation status of the certificate is unknown, and MailKit, which
    # checks revocation by default, refuses the handshake. Only the list is mounted, never the authority's key.
    image: caddy:2.11.7@sha256:f2a1290d0463aad60660d4ec134943f183ee2a5f6c3eb7bf32dd984f2f020772
    command: ["caddy", "file-server", "--root", "/crl", "--listen", ":8081"]
    user: "10002:10002"
    read_only: true
    cap_drop:
      - ALL
    cap_add:
      - NET_BIND_SERVICE  # the binary of the image carries this file capability: without it in the set, it does not start
    security_opt:
      - no-new-privileges:true
    tmpfs:
      - /data:mode=1777
      - /config:mode=1777
    volumes:
      - ${E2E_PROD_PKI_DIR:?set by scripts/e2e-prod.sh}/crl:/crl:ro
    networks:
      auth:
        aliases:
          - pki.test

  caddy:
    image: caddy:2.11.7@sha256:f2a1290d0463aad60660d4ec134943f183ee2a5f6c3eb7bf32dd984f2f020772
    user: "10002:10002"
    read_only: true
    cap_drop:
      - ALL
    cap_add:
      - NET_BIND_SERVICE  # the binary of the image carries this file capability: without it in the set, it does not start
    security_opt:
      - no-new-privileges:true
    tmpfs:
      - /data:mode=1777
      - /config:mode=1777
    depends_on:
      auth:
        condition: service_started
    ports:
      - "127.0.0.1:8443:8443"
    volumes:
      - ${E2E_PROD_CADDYFILE:?set by scripts/e2e-prod.sh}:/etc/caddy/Caddyfile:ro
    networks:
      - proxy

  auth:
    depends_on:
      mailpit:
        condition: service_started
      pki:
        condition: service_started
    environment:
      # The test authority that signed Mailpit's certificate. It replaces the system's authorities for this container.
      SSL_CERT_FILE: /pki/ca.crt
    volumes:
      - ${E2E_PROD_PKI_DIR:?set by scripts/e2e-prod.sh}/ca.crt:/pki/ca.crt:ro
```

`scripts/prod-test.Caddyfile`:

```
# The reverse proxy of the production test stack (spec 0008): HTTPS with a certificate from Caddy's own authority, HSTS, and the client
# address handed to Auth-Core. Test only: see docs/deployment/vps.md for the Caddyfile of a real server.
{
	admin off
	skip_install_trust
	default_sni localhost
	servers {
		protocols h1 h2
	}
}

https://localhost:8443 {
	tls internal
	header Strict-Transport-Security "max-age=31536000"
	handle /auth/* {
		reverse_proxy auth:8080 {
			header_up X-Forwarded-For {remote_host}
			header_up X-Forwarded-Proto {scheme}
		}
	}
	handle {
		respond "not found" 404
	}
}
```

- [ ] **Step 2: The script.** `scripts/e2e-prod.sh`:

```bash
#!/usr/bin/env bash
# Real-network check of the production compose file (spec 0008, criteria 12 and 13). It builds the image under the GHCR name, starts
# deploy/docker-compose.prod.yml with the test overlay (Mailpit with STARTTLS and a test authority, Caddy on https://localhost:8443) from an
# EMPTY volume, and drives it.
#
# What it checks, in order:
#   1. docker compose refuses to render the production file without its secrets, and the service refuses to start with a key file that
#      is not there and with a mail relay without TLS.
#   2. the image carries the OCI labels (source, version, licenses MIT, revision); the container runs with a read-only root file system,
#      no capability and no privilege gain, and serves /auth/health; no Server header; the headers of the table; no interactive reference.
#   3. the first company from the CLI (create-org prints the id), an invitation for its admin: the mail arrives over STARTTLS (Mailpit
#      refuses plain SMTP; the relay's certificate is checked for revocation against the list the test authority publishes) and names
#      the https frontend URL; no seed user exists.
#   4. through the proxy: the admin accepts, logs in (the refresh cookie is HttpOnly, Secure, SameSite=Strict, Path=/auth), refreshes;
#      HSTS is sent; a failed login through the proxy is recorded with the address the proxy saw, not the one the client wrote.
#   5. the backup runbook (docs/operations/backup.md), command for command: a dump, the database dropped, recreated and restored, the
#      service started again; the refresh cookie issued BEFORE the backup still refreshes and the company is still there.
#
# Run from the repo root: scripts/e2e-prod.sh. Needs: docker (with buildx for the image build), openssl, curl, python3 (standard library
# only) and the host ports 8080 (Auth-Core, loopback), 8443 (Caddy) and 8025 (Mailpit) free. Env: COMPOSE_PROJECT_NAME (default
# auth-core-prodtest; a name must start with it, because the script runs `down -v` on its project), E2E_PROD_SKIP_BUILD=1 (reuse an image
# already built under the name), E2E_KEEP_STACK=1 (leave the stack up), PROXY_URL, DIRECT_URL, MAILPIT_URL.
# Takes about five minutes (the first build longer; the invitation mail waits for the server's next poll, within a minute). Exits non-zero
# on the first failure; prints "PASS <step>" per step; never prints a password, a token, a cookie or a mail body. Keys, the test
# authority and the environment file are made in a mktemp -d directory (removed on exit); request bodies and cookies go through files.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-prodtest}"
PROXY_URL="${PROXY_URL:-https://localhost:8443}"
DIRECT_URL="${DIRECT_URL:-http://127.0.0.1:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
VERSION="0.0.0-prodtest"
IMAGE="ghcr.io/mckcieply/auth-core:$VERSION"

fail() { echo "FAIL $*" >&2; exit 1; }
pass() { echo "PASS $*"; }

[[ "$COMPOSE_PROJECT_NAME" == auth-core-prodtest* ]] \
  || fail "COMPOSE_PROJECT_NAME must start with auth-core-prodtest: this script removes the volumes of its project; use such a name"
for tool in docker openssl curl python3; do command -v "$tool" > /dev/null || fail "$tool is not on the PATH"; done

tmp="$(mktemp -d)"
STACK_STARTED=0
cleanup() {
  if [[ "$STACK_STARTED" == "1" && "${E2E_KEEP_STACK:-0}" != "1" ]]; then "${compose[@]}" down -v > /dev/null 2>&1 || true; fi
  rm -rf "$tmp"
}
trap cleanup EXIT

# The docker of a Windows machine wants Windows paths for files it mounts.
host_path() { if command -v cygpath > /dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

pki="$tmp/pki"
keys="$tmp/keys"
backup="$tmp/backup"
mkdir -p "$pki" "$keys" "$backup"
compose=(docker compose -f "$(host_path "$root/deploy/docker-compose.prod.yml")" -f "$(host_path "$root/scripts/prod-test.compose.yml")" --env-file "$(host_path "$tmp/prod.env")")

# --- what the stack needs: a test authority and a certificate for the relay, the keys of the service, the environment ----------
mkcert() {  # MSYS2_ARG_CONV_EXCL keeps Git Bash from turning /CN=... into a path
  MSYS2_ARG_CONV_EXCL='/CN=' openssl "$@" > /dev/null 2>&1 || fail "openssl $1 failed"
}
mkcert req -x509 -newkey rsa:2048 -nodes -days 2 -subj "/CN=auth-core-prod-test-ca" \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" -keyout "$pki/ca.key" -out "$pki/ca.crt"
mkcert req -newkey rsa:2048 -nodes -subj "/CN=mailpit" -keyout "$pki/mailpit.key" -out "$pki/mailpit.csr"
printf 'subjectAltName=DNS:mailpit\nextendedKeyUsage=serverAuth\ncrlDistributionPoints=URI:http://pki.test:8081/ca.crl\n' > "$pki/mailpit.ext"
mkcert x509 -req -in "$pki/mailpit.csr" -CA "$pki/ca.crt" -CAkey "$pki/ca.key" -CAcreateserial -days 2 -extfile "$pki/mailpit.ext" -out "$pki/mailpit.crt"
# The authority's certificate revocation list: empty, signed by the authority, valid for two days, in DER (what an HTTP distribution point
# serves). `openssl ca -gencrl` wants a small configuration with the (empty) index of issued certificates and a CRL number. The service
# "pki" of the overlay serves the directory crl/ and nothing else.
mkdir -p "$pki/crl"
: > "$pki/index.txt"
printf '01\n' > "$pki/crlnumber"
printf '[ca]\ndefault_ca = CA_default\n[CA_default]\ndatabase = index.txt\ncrlnumber = crlnumber\ndefault_md = sha256\ndefault_crl_days = 2\n' > "$pki/ca.cnf"
( cd "$pki" && mkcert ca -config ca.cnf -cert ca.crt -keyfile ca.key -gencrl -out ca.crl.pem )
mkcert crl -in "$pki/ca.crl.pem" -outform DER -out "$pki/crl/ca.crl"
mkcert verify -crl_check -CAfile "$pki/ca.crt" -CRLfile "$pki/ca.crl.pem" "$pki/mailpit.crt"   # the certificate of the relay is good against this list
# The keys of the service, as docs/operations/key-rotation.md makes them: RSA 3072, PKCS#8, the key usage OpenIddict checks.
mkcert req -x509 -newkey rsa:3072 -nodes -days 30 -subj "/CN=auth-core-prodtest-signing" \
  -addext "keyUsage=critical,digitalSignature" -keyout "$keys/signing.key" -out "$keys/signing.crt"
mkcert req -x509 -newkey rsa:3072 -nodes -days 30 -subj "/CN=auth-core-prodtest-encryption" \
  -addext "keyUsage=critical,keyEncipherment" -keyout "$keys/encryption.key" -out "$keys/encryption.crt"
# Readable by the container's user (uid 1654) and by Mailpit: this is a throwaway directory.
chmod 0644 "$pki"/*.crt "$pki"/*.key "$keys"/*.crt "$keys"/*.key "$pki"/crl/ca.crl
chmod 0755 "$pki/crl"

POSTGRES_PASSWORD="$(openssl rand -hex 16)"
cat > "$tmp/prod.env" <<EOF
AUTH_CORE_VERSION=$VERSION
POSTGRES_PASSWORD=$POSTGRES_PASSWORD
AUTH_ISSUER=$PROXY_URL/auth
AUTH_AUDIENCE=prod-test-api
AUTH_KEYS_DIR=$(host_path "$keys")
AUTH_MANIFEST=$(host_path "$root/deploy/auth.yaml")
AUTH_APP_NAME=Prod Test
AUTH_APP_LOCALE=en
AUTH_FRONTEND_RESET_URL=$PROXY_URL/reset
AUTH_FRONTEND_VERIFY_URL=$PROXY_URL/verify
AUTH_FRONTEND_INVITE_URL=$PROXY_URL/invite
AUTH_EMAIL_FROM=no-reply@prodtest.example
AUTH_SMTP_HOST=mailpit
AUTH_SMTP_PORT=1025
AUTH_SMTP_SECURITY=starttls
AUTH_PROXY_KNOWN_NETWORKS=10.250.1.0/24
E2E_PROD_PKI_DIR=$(host_path "$pki")
E2E_PROD_CADDYFILE=$(host_path "$root/scripts/prod-test.Caddyfile")
EOF
chmod 0600 "$tmp/prod.env"

# --- helpers ---------------------------------------------------------------------------------------------------------------------
# call <method> <url-path> [body-file] [header-file]: through the proxy (https, the certificate of Caddy's own authority is accepted)
call() {
  local args=(-sS -k --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' -X "$1" "$PROXY_URL$2")
  if [[ -n "${3:-}" ]]; then args+=(-H 'Content-Type: application/json' --data-binary "@$3"); fi
  if [[ -n "${4:-}" ]]; then args+=(-H "@$4"); fi
  if [[ -n "${EXTRA_HEADER:-}" ]]; then args+=(-H "$EXTRA_HEADER"); fi
  HTTP_CODE="$(curl "${args[@]}")"
  BODY="$(cat "$tmp/body")"
}

# direct <path>: straight to the published loopback port of the service
direct() {
  HTTP_CODE="$(curl -sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' "$DIRECT_URL$1")"
  BODY="$(cat "$tmp/body")"
}

header() { { grep -i "^$1:" "$tmp/hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//'; }
val() { python3 -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1" <<< "$BODY" | tr -d '\r'; }
json_body() { python3 -c 'import json,os,sys; print(json.dumps(eval(sys.argv[1])))' "$2" > "$1"; }
expect_status() { [[ "$HTTP_CODE" == "$2" ]] || fail "$1: HTTP $HTTP_CODE, expected $2"; if [[ $# -ge 3 ]]; then [[ "$BODY" == "$3" ]] || fail "$1: unexpected body"; fi; }
expect_eq() { [[ "$2" == "$3" ]] || fail "$1: got '$2', expected '$3'"; }

wait_ok() { # wait_ok <seconds> <curl args...>: waits until the URL answers 200
  local deadline=$((SECONDS + $1)); shift
  while (( SECONDS < deadline )); do
    if [[ "$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "$@" || true)" == "200" ]]; then return 0; fi
    sleep 1
  done
  return 1
}

psql_value() { printf '%s\n' "$1" | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'; }

cli() { # cli <args...>: the operator's command in a one-off container of the production service; sets CLI_OUT and CLI_EXIT
  CLI_EXIT=0
  CLI_OUT="$("${compose[@]}" run --rm -T --no-deps auth admin "$@" 2> "$tmp/cli.err")" || CLI_EXIT=$?
  CLI_OUT="${CLI_OUT//$'\r'/}"
}

# --- Step 1: the file and the service refuse to start without their secrets --------------------------------------------------------
: > "$tmp/empty.env"
if docker compose -f "$(host_path "$root/deploy/docker-compose.prod.yml")" --env-file "$(host_path "$tmp/empty.env")" config > "$tmp/config.out" 2>&1; then
  fail "step 1: docker compose rendered the production file with no secrets"
fi
grep -q "required variable" "$tmp/config.out" || fail "step 1: docker compose did not say which variable is required"
if [[ "${E2E_PROD_SKIP_BUILD:-0}" != "1" ]]; then
  echo "building the image under its GHCR name (the first build takes a few minutes)..."
  docker build -q -f "$root/src/Auth.Server/Dockerfile" --build-arg "VERSION=$VERSION" --build-arg "REVISION=$(git -C "$root" rev-parse HEAD)" \
    -t "$IMAGE" "$root" > "$tmp/build.out" 2>&1 || { tail -n 20 "$tmp/build.out" >&2; fail "step 1: the image did not build"; }
fi
docker image inspect "$IMAGE" > /dev/null 2>&1 || fail "step 1: the image $IMAGE is not there (build it, or unset E2E_PROD_SKIP_BUILD)"
if timeout 120 "${compose[@]}" run --rm -T --no-deps -e Auth__Keys__SigningKeyPath=/nonexistent/signing.key auth > "$tmp/nokey.out" 2>&1; then
  fail "step 1: the service started with a signing key that is not there"
fi
grep -q "Auth:Keys" "$tmp/nokey.out" || fail "step 1: the refusal does not name the key setting"
if timeout 120 "${compose[@]}" run --rm -T --no-deps -e Auth__Email__Smtp__Security=none auth > "$tmp/nomail.out" 2>&1; then
  fail "step 1: the service started with a mail relay without TLS in Production"
fi
grep -q "Auth:Email:Smtp:Security" "$tmp/nomail.out" || fail "step 1: the refusal does not name the mail security setting"
pass "step 1: docker compose refuses the file without its secrets; the service refuses a missing key file and a relay without TLS, naming the setting"

# --- Step 2: the image, the container, the headers ------------------------------------------------------------------------------------
labels="$(docker image inspect "$IMAGE" --format '{{json .Config.Labels}}')"
for expected in '"org.opencontainers.image.source":"https://github.com/MckCieply/Auth-Core"' "\"org.opencontainers.image.version\":\"$VERSION\"" \
                '"org.opencontainers.image.licenses":"MIT"' '"org.opencontainers.image.revision":"'; do
  [[ "$labels" == *"$expected"* ]] || fail "step 2: the image has no label $expected"
done
"${compose[@]}" up -d > "$tmp/up.log" 2>&1 || { tail -n 20 "$tmp/up.log" >&2; fail "step 2: docker compose up failed"; }
STACK_STARTED=1
wait_ok 120 "$DIRECT_URL/auth/health" || fail "step 2: $DIRECT_URL/auth/health did not return 200 within 120s (docker compose logs auth)"
wait_ok 60 -k "$PROXY_URL/auth/health" || fail "step 2: $PROXY_URL/auth/health (through the proxy) did not return 200 within 60s"
wait_ok 60 "$MAILPIT_URL/readyz" || fail "step 2: the mail catcher did not answer within 60s"
auth_id="$("${compose[@]}" ps -q auth)"
expect_eq "step 2: the root file system is read-only" "$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$auth_id")" "true"
[[ "$(docker inspect -f '{{.HostConfig.CapDrop}}' "$auth_id")" == *ALL* ]] || fail "step 2: the container does not drop every capability"
[[ "$(docker inspect -f '{{.HostConfig.SecurityOpt}}' "$auth_id")" == *no-new-privileges* ]] || fail "step 2: the container may gain privileges"
direct /auth/health
expect_status "step 2: /auth/health" 200 "Healthy"
[[ -z "$(header server)" ]] || fail "step 2: the live server sends a Server header"
[[ "$(header x-content-type-options)" == "nosniff" && "$(header x-frame-options)" == "DENY" ]] || fail "step 2: the security headers are missing on the live server"
direct /auth/scalar/
expect_status "step 2: no interactive reference in Production" 404
direct /auth/openapi/v1.json
expect_status "step 2: the OpenAPI description is served in Production" 200
pass "step 2: the image has its labels; the container is read-only with no capability and no privilege gain and serves /auth/health; no Server header; no interactive reference"

# --- Step 3: the first company from the CLI, the mail over STARTTLS ----------------------------------------------------------------------
run="$RANDOM$RANDOM"
ADMIN_EMAIL="boss-$run@prodtest.example"
export E2E_PASSWORD="E2e-Passw0rd-$run" E2E_LINK_PREFIX="$PROXY_URL/invite"
cli create-org --name "Acme $run"
[[ "$CLI_EXIT" == "0" ]] || { cat "$tmp/cli.err" >&2; fail "step 3: create-org failed"; }
ORG="$CLI_OUT"
[[ "$ORG" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || fail "step 3: create-org did not print a company id"
cli invite --org "$ORG" --email "$ADMIN_EMAIL" --role admin
[[ "$CLI_EXIT" == "0" ]] || { cat "$tmp/cli.err" >&2; fail "step 3: the invitation failed"; }
mail_ok=0
for _ in $(seq 1 150); do
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$ADMIN_EMAIL" -o "$tmp/search.json"
  if [[ "$(python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages_count"])' < "$tmp/search.json" | tr -d '\r')" == "1" ]]; then mail_ok=1; break; fi
  sleep 1
done
[[ "$mail_ok" == "1" ]] || { "${compose[@]}" logs --no-color --tail 30 auth >&2 || true; fail "step 3: no invitation mail arrived within 150s (the relay requires STARTTLS and the service must trust its test authority)"; }
mail_id="$(python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages"][0]["ID"])' < "$tmp/search.json" | tr -d '\r')"
curl -sS --max-time 10 "$MAILPIT_URL/api/v1/message/$mail_id" -o "$tmp/mail.json"
python3 -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(re.escape(os.environ["E2E_LINK_PREFIX"]) + r"\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no invitation link on the https frontend URL")
print(json.dumps({"token": match.group(1), "password": os.environ["E2E_PASSWORD"]}))
' < "$tmp/mail.json" > "$tmp/accept.json" || fail "step 3: the mail has no link on $PROXY_URL/invite"
json_body "$tmp/seed.json" "{'email': 'user@example.com', 'password': 'Correct-Horse-Battery-1'}"
call POST /auth/login "$tmp/seed.json"
expect_status "step 3: a development seed user does not exist in Production" 401
pass "step 3: create-org prints the id; the invitation mail arrives over STARTTLS with a link on the https frontend URL; no seed user exists"

# --- Step 4: through the proxy -----------------------------------------------------------------------------------------------
call POST /auth/invites/accept "$tmp/accept.json"
expect_status "step 4: the admin accepts the invitation" 204 ""
json_body "$tmp/login.json" "{'email': '$ADMIN_EMAIL', 'password': os.environ['E2E_PASSWORD']}"
call POST /auth/login "$tmp/login.json"
expect_status "step 4: login through the proxy" 200
cookie_line="$({ grep -i '^set-cookie:[[:space:]]*auth_rt=' "$tmp/hdr" || true; } | head -n1 | tr -d '\r' | cut -d: -f2- | sed 's/^ *//')"
for attribute in 'HttpOnly' 'Secure' 'SameSite=Strict' 'Path=/auth'; do
  [[ "$cookie_line" == *"$attribute"* ]] || fail "step 4: the refresh cookie lacks $attribute"
done
[[ -n "$(header strict-transport-security)" ]] || fail "step 4: the proxy sends no HSTS over HTTPS"
printf 'Cookie: %s\n' "$(printf '%s' "$cookie_line" | cut -d';' -f1)" > "$tmp/before.cookie"
call POST /auth/refresh "" "$tmp/before.cookie"
expect_status "step 4: refresh through the proxy" 200
# The cookie that the backup must keep alive is the one of THIS refresh: a login now, kept for step 5.
call POST /auth/login "$tmp/login.json"
expect_status "step 4: a second login, for the backup" 200
printf 'Cookie: %s\n' "$({ grep -i '^set-cookie:[[:space:]]*auth_rt=' "$tmp/hdr" || true; } | head -n1 | tr -d '\r' | cut -d: -f2- | sed 's/^ *//' | cut -d';' -f1)" > "$tmp/session.cookie"
json_body "$tmp/spoof.json" "{'email': 'nobody-$run@example.invalid', 'password': 'Wrong-Password-1'}"
EXTRA_HEADER="X-Forwarded-For: 203.0.113.77" call POST /auth/login "$tmp/spoof.json"
expect_status "step 4: a failed login through the proxy" 401
RECORDED="$(psql_value "SELECT client_ip FROM audit_events WHERE kind = 'login.failed' AND subject_email = 'nobody-$run@example.invalid'")"
[[ -n "$RECORDED" && "$RECORDED" != "203.0.113.77" ]] || fail "step 4: the recorded address is the one the client wrote ($RECORDED) or none"
pass "step 4: accept, login and refresh through the proxy; the cookie is HttpOnly, Secure, SameSite=Strict, Path=/auth; HSTS; the recorded address is not a forged one"

# --- Step 5: the backup runbook, command for command ----------------------------------------------------------------------------
# docs/operations/backup.md: the database is dumped with pg_dump in the custom format, no owner; the key files, the manifest and the
# environment file are copied. Then the disaster: the service stops, the database is dropped. The restore: recreate, pg_restore, start.
"${compose[@]}" exec -T postgres pg_dump -U auth -d auth --format=custom --no-owner > "$backup/auth.dump" \
  || fail "step 5: pg_dump failed"
[[ -s "$backup/auth.dump" ]] || fail "step 5: the dump is empty"
cp -a "$keys" "$backup/keys"
cp "$root/deploy/auth.yaml" "$backup/auth.yaml"
cp "$tmp/prod.env" "$backup/prod.env"
"${compose[@]}" stop auth > /dev/null 2>&1 || fail "step 5: could not stop the service"
"${compose[@]}" exec -T postgres psql -U auth -d postgres -c 'DROP DATABASE IF EXISTS auth WITH (FORCE)' > /dev/null || fail "step 5: could not drop the database"
"${compose[@]}" exec -T postgres psql -U auth -d postgres -c 'CREATE DATABASE auth OWNER auth' > /dev/null || fail "step 5: could not create the database"
"${compose[@]}" exec -T postgres pg_restore -U auth -d auth --no-owner --exit-on-error < "$backup/auth.dump" || fail "step 5: pg_restore failed"
"${compose[@]}" start auth > /dev/null 2>&1 || fail "step 5: could not start the service again"
wait_ok 120 "$DIRECT_URL/auth/health" || fail "step 5: the service did not answer within 120s of its start"
ok=0
for _ in $(seq 1 30); do
  call POST /auth/refresh "" "$tmp/session.cookie"
  if [[ "$HTTP_CODE" == "200" ]]; then ok=1; break; fi
  sleep 2
done
[[ "$ok" == "1" ]] || fail "step 5: the refresh cookie issued before the backup does not refresh after the restore (HTTP $HTTP_CODE)"
cli list-orgs
[[ "$CLI_EXIT" == "0" && "$CLI_OUT" == *"$ORG"* ]] || fail "step 5: the company is not in the restored database"
pass "step 5: the runbook's dump, drop, restore and start brought the database back: the cookie issued before the backup still refreshes and the company is there"

echo "ALL PASS"
```

- [ ] **Step 3: Check the files.**

```bash
bash -n scripts/e2e-prod.sh
docker compose -f deploy/docker-compose.prod.yml -f scripts/prod-test.compose.yml --env-file deploy/.env.prod.example config > /dev/null 2>&1; echo "exit $? (non-zero is right: E2E_PROD_PKI_DIR is not set)"
docker run --rm -v "$PWD/scripts/prod-test.Caddyfile:/etc/caddy/Caddyfile:ro" caddy:2.11.7@sha256:f2a1290d0463aad60660d4ec134943f183ee2a5f6c3eb7bf32dd984f2f020772 caddy validate --config /etc/caddy/Caddyfile
```

  Expected: no output from `bash -n`; a non-zero exit for the second (the overlay needs the two variables the script sets); `Valid configuration` for the Caddyfile. None of these starts a stack.

- [ ] **Step 4: Run it if the ports are free.** The ports 8080, 8025 and 8443 belong to whoever runs a stack on this machine. If they are free (`netstat -ano | grep LISTENING` shows none of them) run `scripts/e2e-prod.sh` once and report its output (it ends with `ALL PASS`); if one of its steps fails, fix the files of this task, or, when the cause is in Tasks 1–9, report it to the orchestrator with the output. If they are not free, say so in the hand-back: the verifier of Tuesday runs it.

- [ ] **Step 5: Hand back** — uncommitted. Files: `scripts/prod-test.compose.yml`, `scripts/prod-test.Caddyfile`, `scripts/e2e-prod.sh` (the orchestrator makes the script executable in the index). Proposed subject: `test(deploy): production test overlay and scripts/e2e-prod.sh`.

### Task 13: The threat model, the backup runbook and the key rotation runbook

**Files:**
- Create: `docs/security/threat-model.md`, `docs/operations/backup.md`, `docs/operations/key-rotation.md`, `scripts/check-docs.py`

**Interfaces:**
- Consumes: specs 0002–0008 (their residual risks and decisions), `deploy/docker-compose.prod.yml` and `deploy/.env.prod.example` (Task 9), `scripts/e2e-prod.sh` (Task 12: the restore commands below are the ones it runs, **command for command**), the audit table (Task 4), the settings of Tasks 1–9.
- Produces: three documents that the deployment guide, the README and the acceptance map link to. Every file path, setting name and command they name exists (Step 4 checks the paths and links).

**Rules.** English. No emoji, no marketing. Every statement of a mitigation names where it is built (a spec, a setting, a test class or a script), so that a reader can check it. Every residual risk is listed with its owner decision; none is left out (the list is in "Residual risks" below and the document's last section must contain each of them). The commands of the runbooks are written for `deploy/docker-compose.prod.yml` and are tried on it by `scripts/e2e-prod.sh`.

- [ ] **Step 1: The threat model.** `docs/security/threat-model.md`:

````markdown
# Threat model

Auth-Core is a self-hosted authentication service: one instance per product, behind one reverse proxy on the product's origin
([ADR 0001](../adr/0001-instance-per-project.md), [ADR 0004](../adr/0004-same-origin-cookie-refresh.md)). This is its threat model, made
with STRIDE for each element of the deployment. For every element it lists the threat, what stops it and where that is built, and the risk
that is accepted. The last section collects the accepted risks in one place, with the decision that accepted each. It describes version
0.1.0 ([spec 0008](../superpowers/specs/0008-hardening-and-release.md)).

## What is protected, and from whom

| Asset | Why it matters |
| --- | --- |
| The accounts: passwords (hashed), addresses, memberships | Taking one over is taking over the person's place in a company |
| The sessions: the refresh cookie, the access token | The refresh cookie is the person for up to 14 days after its last use (a sliding window, capped at 30 days from the login); the access token is the person for 10 minutes |
| The signing key | Whoever holds it can make an access token for anyone |
| The company data: roles, members, invitations | A wrong role is a wrong permission in every product that trusts the token |
| The audit log | The only record of who did what |
| Availability of sign-in | A product that cannot sign people in is down |

The attackers considered: an anonymous person on the internet (guesses passwords, floods endpoints, sends hostile input); someone who has
a victim's mailbox for a moment, a stolen invitation link or a stolen refresh cookie; a member of a company who tries to reach beyond their
role or into another company; a malicious or compromised product backend or frontend; a person with read access to a backup. **Not
considered:** an attacker with root on the server (they hold the keys and the database), a compromised PostgreSQL or SMTP relay *operator*
beyond the listed effects, a nation-state, side channels of the hardware.

## The elements and their boundaries

```
 Browser / SPA ──https──> Reverse proxy ──http──> Auth-Core ──tcp──> PostgreSQL
 (product frontend)       (TLS, HSTS, headers,    (127.0.0.1:8080)      (private network, no published port)
                           client address)             │  └──smtp+TLS──> SMTP relay
 Product backend ──https GET /auth/.well-known/jwks.json──┘
 Operator ──docker compose run auth admin ...──> Auth-Core's database       Key files (read-only mount)
```

The boundaries: internet to proxy (TLS), proxy to Auth-Core (a trusted network, the client address is believed from there only), Auth-Core
to PostgreSQL and to the relay (private network; TLS to the relay), the host's file system to the container (keys, manifest; read-only).

## Browser and single-page application

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | Someone else signs in as the person with a stolen refresh cookie or access token | The cookie is `HttpOnly; Secure; SameSite=Strict; Path=/auth` ([spec 0002](../superpowers/specs/0002-refresh-and-logout.md), `RefreshCookie`); a rotated refresh token that is presented again after 15 seconds revokes the whole session (reuse detection, `RefreshReuseTests`, audited as `refresh.reuse_detected`); the access token lives 10 minutes and is held in memory only ([spec 0007](../superpowers/specs/0007-angular-sample.md)) | A just-rotated token stolen within the 15-second leeway can start a second chain that reuse detection does not see. A stolen cookie otherwise works until the 14-day window lapses, the 30-day cap ends the session, or the owner's next refresh makes the thief's next use a reuse |
| T | A script in the page reads or changes the token | The sample's Content-Security-Policy allows only the app's own scripts; no token is in storage, a cookie or a URL (`e2e/02-session.spec.ts`) | A token in memory can be read by script that runs in the page |
| T | Cross-site requests with the cookie | `SameSite=Strict`, same origin, a JSON-only API (a form post is `400`) | No CSRF token ([spec 0002](../superpowers/specs/0002-refresh-and-logout.md), Decision 6): `SameSite=Strict` on one origin is the whole defence |
| R | A person denies a sign-in or a deletion | The audit log records logins, failures, logouts, reuse, resets, invitations, role and member changes and company deletion, with the address ([spec 0008](../superpowers/specs/0008-hardening-and-release.md)) | Rows older than the retention (90 days by default) are pruned |
| I | The token or a link leaks through a URL, a log or the `Referer` | Links carry the token in the query only to the frontend, which reads it once and removes it from the address (`token-from-url.ts`); `Referrer-Policy: no-referrer`; mail tokens are stored hashed | A mail link in a mailbox is a credential for its lifetime |
| D | A person locked out by someone else | Per-identifier lockout with a cap of 30 minutes ([spec 0003](../superpowers/specs/0003-lockout-and-abuse-resistance.md)) | An attacker can keep a chosen account locked: it costs one request per cooldown |
| E | A tab of a signed-out person stays usable | Access tokens expire in 10 minutes | Another tab stays usable for up to 10 minutes after sign-out |
| | Browser quirks on a phone | The proxy sends HSTS and the app's policy | Safari on a real phone may treat cookies differently from WebKit in Playwright ([spec 0007](../superpowers/specs/0007-angular-sample.md)); to be tested on the first real deployment |

## Reverse proxy

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | A client forges `X-Forwarded-For` to hide, or to frame another address, or to dodge the per-IP limits | The forwarded headers are read only from the proxies listed in `Auth:Proxy:KnownProxies` and `Auth:Proxy:KnownNetworks`; with none listed they are not read at all; every hop is walked; the proxy *sets* the header (`header_up`); `X-Forwarded-Host` is never read (`ClientAddressSetupTests`, `RateLimitProxyTests`, `scripts/e2e-hardening.sh` step 2) | A mistake in the list (too wide a network) trusts too much: the deployment guide names the exact address |
| T | Plain-HTTP downgrade | The proxy sends HSTS; the cookie is `Secure`; TLS ends at the proxy ([`docs/deployment/vps.md`](../deployment/vps.md)) | The first visit before HSTS is cached is on the user's browser |
| I | The mail link's token is written to an access log | The sample's Caddy writes no access log; the integration guide (step 4) and the deployment guide say to keep the logs of `/reset`, `/verify` and `/invite` off, or to scrub `token=` ([spec 0007](../superpowers/specs/0007-angular-sample.md)) | A proxy or web server that logs the whole request line records a token that is still valid: such a log is as private as a password |
| I | Headers that reveal or help the attacker | `Server` is off; every answer carries `nosniff`, `X-Frame-Options`, a restrictive policy, `no-referrer`, `Cross-Origin-Resource-Policy`, `no-store` (`SecurityHeadersTests`, `e2e-hardening.sh` step 1) | Kestrel's own errors before the pipeline (a malformed request line, headers too large) cannot carry the headers |
| D | Floods | Per-IP limits (30 logins, 60 refreshes, 10 mail requests, 20 invitation requests and 300 other requests a minute) answered `429` before any work is done (`RateLimitMiddlewareTests`) | Many people behind one address share its limits; 30 logins a minute is the ceiling for an office behind one NAT. A distributed guesser below the limits on many addresses is stopped only by the per-identifier lockout. The counters live in memory (one instance per product): a restart clears them, so whoever can make the service restart gets a fresh allowance |
| E | The proxy is bypassed: Auth-Core's port reached directly | The published port is on `127.0.0.1` only (`docker-compose.prod.yml`) | A process on the host can reach it; a request that does is taken as it comes |

## Auth-Core

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | Password guessing; account enumeration | Identical `401` for unknown and wrong; a decoy hash so both take about as long (close enough, not constant time: the medians are within 0.5 to 2 times of each other; [spec 0003](../superpowers/specs/0003-lockout-and-abuse-resistance.md), Decision 5, `LoginTimingTests`); lockout per identifier ([spec 0003](../superpowers/specs/0003-lockout-and-abuse-resistance.md)); per-IP limits; the mail endpoints answer alike for every address ([spec 0004](../superpowers/specs/0004-email-flows.md)) | A prober can see the login streak of an address reset by its owner's login (about eleven requests per probe). A correct password given during a cooldown extends it by a minute, like a wrong one, so an impatient person lengthens their own lock (Decision 7) |
| S | Forged access token | RS256 only; `typ at+jwt`; audience and issuer checked; the key set is the instance's own; the package refuses other algorithms ([spec 0006](../superpowers/specs/0006-python-consumer-package.md)) | A product that sets `jwks_url` to an address an attacker controls accepts the attacker's tokens |
| T | Hostile input: oversize bodies, NUL, malformed JSON, a body in another charset, look-alike or internal-host addresses | A body is at most 8 KiB and a JSON object; a charset other than UTF-8 is `415`; NUL and control characters are refused; invitations to addresses that may stand for another account or whose domain is an address (`127.0x1`) or has no dot are refused; role and company names refuse format characters ([specs 0004, 0005](../superpowers/specs/0005-tenancy-and-rbac.md), 0008) | A fullwidth or Cyrillic look-alike of an address can still be invited: it normalises to another address and never reaches the account it imitates |
| T | A member reaches beyond their role or another company | Permissions are read from the database on every call, never from the token; nobody grants, edits, removes or deletes more than they hold (rule 1); the company lock serialises changes; every company query is scoped to the caller's company ([spec 0005](../superpowers/specs/0005-tenancy-and-rbac.md)) | A removed or demoted member keeps the token's permissions at a product backend for up to 15 minutes (10 minutes of life, 5 of clock skew) |
| T | Deleting a company by mistake or by a hijacked session | `DELETE /auth/org` needs the permission `org:delete`, the company's exact name and the caller's password, counted as a login attempt; rule 1 over every member | A company admin holding every permission can delete the company with their password; there is no undo but the backup |
| R | Actions that leave no trace | The audit log: 22 kinds, written in the transaction of the change, never holding a secret (`AuditCompanyFlowsTests`, `AuditAtomicityTests`) | Anyone can create `login.failed` and `password.reset_requested` rows; only the per-IP limits and the retention bound the table. The address typed into a failed login is stored as typed and may be a password pasted into the wrong field; it is kept for the retention period |
| I | Secrets in logs, answers or the audit log | Errors name settings, never values; the CLI prints no connection string; an unhandled exception is `500 {"error":"internal_error"}` with no detail and is only logged; the audit log holds no password, token, link, cookie or mail body (`AssertNoSecretsAsync`, `e2e-hardening.sh` step 6) | |
| I | Reset and invitation links in the wrong hands | Tokens are random, stored hashed, single-use, newest-only; a link works for the mailbox it was sent to ([specs 0004, 0005](../superpowers/specs/0004-email-flows.md)) | A stolen invitation link can join the company with the invited role (7 days; only the newest link works). A reset mail requested before a reset still goes out after it, with a new working link that reaches only the mailbox owner. A known address can be sent up to five reset and five verification mails an hour by anyone; only the newest link of each kind works |
| D | Resource exhaustion through the database or the mail queue | A request over the per-IP limit does no work; every attempt is one small write; a pass of the dispatcher first removes, in one statement, the requests that need no mail (an address without an account) within seconds, and drops a request an hour old at the first pass after it; link and limit rows are pruned | Every login attempt, refused ones included, is one write, and an unknown address costs one hash; parallel attempts for one identifier hold pooled connections for milliseconds, and a flood on one identifier can exhaust the pool |
| D | The database is down | A refresh answers `503 temporarily_unavailable` and keeps the session; the health check does not reach the database ([spec 0008](../superpowers/specs/0008-hardening-and-release.md)) | A refresh interrupted between marking the old token redeemed and storing the new one is usable for the 15-second leeway only; retried later it is reuse and ends the session. Login, logout and the company API answer `500` |
| D | Concurrent changes of one account | Everything that changes a company runs under its lock; link use is one `DELETE ... RETURNING` | Two requests that change one account at the same instant with different tokens: one can fail with a `500`, nothing is left half-done and its token stays usable; a click on an earlier link waits up to 20 seconds while a mail for the account is sent; cancelling an invitation whose mail is being sent waits for the send, holding the company lock, and the other changes of that company wait with it |
| E | A session survives its password | A password change ends every session and bumps the security stamp ([spec 0004](../superpowers/specs/0004-email-flows.md)) | A login or refresh that overlaps a reset gets a refresh token that is refused at once, but its access token lives up to 10 minutes; a verification racing a reset of the same account can deadlock in PostgreSQL and one of the two gets a `500` |
| E | Privilege through the container | Non-root user, read-only root file system, no capabilities, no privilege gain (`docker-compose.prod.yml`, proven by `scripts/e2e-prod.sh`) | |

## PostgreSQL

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | Another container or host connects as `auth` | No published port; a private network; a long random password from `.env` | The password is in the container's environment |
| T | A stolen or tampered database | Passwords are hashed (ASP.NET Identity); link tokens and refresh tokens are stored as hashes or protected payloads | Anyone with write access to the database can make themselves a member of any company; PostgreSQL is part of the trusted base |
| R | Rows removed to hide actions | Deleting an audit row needs database access | The audit log is not tamper-evident |
| I | Addresses in the mail queue | A request is queued with the normalised address and nothing else; the row is removed within seconds when no account has the address, and at delivery or when the request is an hour old otherwise ([spec 0004](../superpowers/specs/0004-email-flows.md), Decision 12) | Whoever reads the database sees the addresses of the requests that are pending |
| I | A backup read by the wrong person | The runbook keeps the dump apart from the key files, and says to encrypt it off the host ([`backup.md`](../operations/backup.md)) | A dump holds every account's password hash |
| D | The disk fills, the server is lost | Pruning of expired rows; a nightly dump and a restore that is tried ([`backup.md`](../operations/backup.md), `scripts/e2e-prod.sh` step 5) | Whatever happened after the last backup is lost |

## SMTP relay

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | A mail that pretends to come from the product | The sender is a setting; the relay must offer TLS and the service refuses `none` in Production | The product's domain needs SPF, DKIM and DMARC on its own DNS: outside this repository |
| T | A mail read or changed on the way | STARTTLS or TLS is required, the certificate is validated | The relay sees the mail, links included |
| I | Names and mail content | Company and role names are encoded and limited to 100 characters | A hostile admin can word a company or role name as they like; every mail names the company |
| D | The relay is down | Mail goes through a queue with retries; the request is answered `202` at once (`MailDispatcherTests`) | A mail may arrive late. Delivery is at-least-once: a retry after a failure that came after the relay took the mail sends a second mail, with a new link ([spec 0004](../superpowers/specs/0004-email-flows.md), Decision 13) |

## Key files

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| I | The signing or encryption key is read | Read-only mount, a directory of its own, a mode that only the container's user can read ([`key-rotation.md`](../operations/key-rotation.md)); RSA of at least 2048 bits is enforced at start | Anyone who reads the signing key can make tokens until the key is rotated |
| T | A key replaced by an attacker | Write access to the host is root | |
| D | A key lost or rotated | The service refuses to start without its keys; keys are backed up apart from the dump | A key change signs everyone out (Decision 7 of spec 0008): no previous keys are kept for verification or decryption |

## Product backend

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | A token for another product or audience | The audience is checked; one instance per product ([ADR 0001](../adr/0001-instance-per-project.md)) | |
| T | A backend that trusts an attacker's key set | The package takes the key set from a configured URL only, without proxy or redirect, size and time limited | A product that sets `jwks_url` to an address an attacker controls accepts the attacker's tokens: use an internal or HTTPS address |
| I | A query without the company | The package gives `org_id`; the sample filters every query by it and has tests for it ([spec 0006](../superpowers/specs/0006-python-consumer-package.md)) | A product that forgets the filter leaks across companies: the guide says so in step 5 |
| D | Auth-Core unreachable | The package keeps the keys it holds for 24 hours and answers `503 auth_unavailable` when it has none | A key that Auth-Core has removed or rotated still works at a product backend until its next successful fetch, and for at most 24 hours while Auth-Core is down ([spec 0006](../superpowers/specs/0006-python-consumer-package.md), Decision 10) |
| E | A demoted member's token | Short life | 15 minutes at the product's endpoints at the extreme |

## Operator CLI

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | Someone else runs it | It needs access to the host and the compose project: root-equivalent | |
| T | A command by mistake | `delete-org` needs the exact name; `remove-member` refuses the last manager without `--force`; every command is recorded with `"via": "cli"` | The operator is not bound by rule 1 |
| I | Secrets in its output | It prints no connection string or exception text, only `error: <code>` | |
| E | The CLI used to bypass the API's rules | It is the operator's own tool; the same services and rules apply except rule 1 | |

## Residual risks

Accepted, with the decision that accepted each. None of them is hidden by a mitigation above.

1. **A key change signs everyone out.** No previous keys are kept for verification or decryption; rotation is a runbook (spec 0008, Decision 7).
2. **A distributed guesser below the per-IP limits** on many addresses is stopped only by the per-identifier lockout, which an attacker can use to keep a chosen account locked, at one request per cooldown (spec 0003, Decision 8; spec 0008).
3. **Many people behind one address share its limits;** 30 logins a minute is the ceiling for an office behind one NAT (spec 0008).
4. **A company admin holding every permission can delete the company** with their password; there is no undo but the backup (spec 0008, Decision 8).
5. **A refresh interrupted by an outage** between marking the old token redeemed and storing the new one can be used for the 15-second leeway only; retried later it ends the session (spec 0008).
6. **The address typed into a failed login** is stored as typed and may be a password pasted into the wrong field; it is kept for the retention period (spec 0008).
7. **Anyone can create `login.failed` and `password.reset_requested` rows;** only the per-IP limits and the retention bound the table (spec 0008).
8. **The login streak reset a prober can observe** (about eleven requests per probe; it shows that the account exists and that its owner logged in) (spec 0003, escalation E2; accepted in spec 0008, Decision 3).
9. **Fullwidth and Cyrillic look-alike invitation addresses** can still be invited; they normalise to other addresses and never reach the account they imitate (spec 0005; accepted in spec 0008, Decision 3).
10. **A just-rotated refresh token stolen within the 15-second leeway** can start a second chain that reuse detection does not see (spec 0002, Decision 2). A stolen refresh cookie is otherwise bounded by the 14-day sliding window and the 30-day cap, not removed (spec 0002, Decision 3).
11. **No CSRF token;** `SameSite=Strict` on one origin is the defence (spec 0002, Decision 6).
12. **Issued access tokens stay valid until they expire,** up to 10 minutes after logout, a password change or a removal (spec 0002, Decision 14; spec 0004, deferred follow-ups).
13. **A demoted or removed member keeps the token's permissions at a product's endpoints** for up to 15 minutes: the 10 minutes of the token and the 5 of clock skew the package allows (spec 0005; spec 0006, Decision 9).
14. **A product that sets `jwks_url` to an address an attacker controls** accepts the attacker's tokens (spec 0006).
15. **Another browser tab stays usable** for up to 10 minutes after sign-out: open tabs are not synchronised on sign-out, a follow-up is named in the spec (spec 0007, design choices and deferred follow-ups).
16. **A token in memory can be read by script running in the page;** the policy limits scripts to the app's own files (spec 0007).
17. **Safari on a real phone** may treat cookies differently from WebKit in Playwright; the test on a real phone is part of the first real deployment (spec 0007).
18. **Every login attempt costs a database write and an unknown address one hash;** a flood on one identifier can exhaust the pool (spec 0003, residual risks found in verification).
19. **Parallel correct-password logins:** six or more at the same instant for one identifier, the later ones are refused by the burst rule (spec 0003, Decisions 9 and 14).
20. **The decoy hash follows the current hasher settings;** after a settings change, accounts with older hashes cost differently until their next login (spec 0003).
21. **Timing equalisation is close enough, not constant time:** over the network the median of an unknown-address login is within 0.5 to 2 times that of a wrong-password login (spec 0003, Decisions 5 and 12).
22. **A correct password given during a cooldown extends it** by a minute, like a wrong one, so that the answer does not tell a guesser whether the password was right; an impatient person lengthens their own lock (spec 0003, Decision 7).
23. **Mail limits and queue rows:** an anonymous client makes two small rows per request for any address (removed within seconds and two hours); a known address can be sent five reset and five verification mails an hour by anyone, and anyone can use up an address's hourly limit; only the newest link of each kind works and the owner still receives the mails (spec 0004, residual risks).
24. **The mail queue holds the normalised address** of a request until it is processed: seconds for an address without an account, and until delivery or the one-hour limit for an account (spec 0004, Decision 12).
25. **Mail delivery is at-least-once:** a retry after a failure that came after the relay accepted the mail sends another mail, and every retry composes a new link (spec 0004, Decision 13 and the questions the owner accepted on plan 0004).
26. **Races accepted as they are:** a reset mail being sent at the instant another reset of the account commits may leave a working link that reaches only the mailbox owner; two requests that change one account at the same instant with different tokens: one fails with a `500`, nothing is left half-done and its token stays usable; a click on an earlier link waits up to 20 seconds while a mail for the account is sent (spec 0004, "Accepted as they are").
27. **Races found in verification:** a reset mail requested before a reset still goes out after it, with a new working link (it reaches only the mailbox owner); a verification racing a reset of the same account can deadlock in PostgreSQL, one of the two gets a `500`, nothing is left half-done and its token stays usable (spec 0004, residual risks found in verification).
28. **A login or refresh that overlaps a reset** gets a refresh token that is refused at once, but its access token lives up to 10 minutes (spec 0004, Decision 17 and the residual risks found in verification).
29. **A stolen invitation link** can join the company with the invited role: it lives 7 days and only the newest works (spec 0005).
30. **The invitation mail limit is per company and address,** so a manager can mail any number of distinct addresses, each with the company's own wording of its name and role; the names are encoded, limited to 100 characters, and each mail names the company (spec 0005, Decision 16).
31. **An invitation reaches an existing account only under that account's own spelling** (apart from the case of `A` to `Z`): an account spelled `Żaneta@...` is not reached by an invitation to `żaneta@...`, and the inviter types the address as the account spells it (spec 0005, behaviour added after verification).
32. **Cancelling an invitation** whose mail is being sent waits for the send (at most 20 seconds) while it holds the company's lock, and the other changes of that company wait with it (spec 0005, residual risks found in verification).
33. **Package limit:** an asynchronous exception (a gevent or eventlet timeout) delivered between marking a fetch as running and the `try` that ends it can leave the key cache marked as fetching; while the cache is warm, a request that lacks its key is then answered `503` at once (spec 0006, known limits).
34. **The refresh cookie is `Secure`,** so on a plain-HTTP origin other than `localhost` browsers drop it; HTTPS comes with the real deployment (spec 0006, known limits).
35. **Angular, its CLI and its build stay at 21.1.4 with their known advisories:** six high advisories against the runtime packages (cross-site scripting through i18n bindings, sanitisation bypasses, denial of service in pipes and server-side rendering, leaks of the transfer cache), none reachable by what the sample uses, and advisories in the development tooling that never reach the build; the content-security-policy is the second line. A product that copies the sample moves to a fixed release (spec 0007, Decision 13 and known limits).
36. **An access log records the mail link's `?token=` query** unless it is turned off or scrubbed for `/reset`, `/verify` and `/invite`; a token that is still valid in a log is a way into someone's account (spec 0007, the guide's step 4).
37. **Kestrel's answers before the pipeline** (a malformed request line, headers too large) cannot carry the security headers (spec 0008).
38. **The per-IP counters are in memory:** a restart clears them (one instance per product, ADR 0001; spec 0008).
39. **The audit log is not tamper-evident** and is bounded by the retention; it is read by SQL (spec 0008, Decision 5).
40. **A mail link in a mailbox** is a credential for its lifetime; whoever reads the mailbox can use it (specs 0004, 0005).
````

- [ ] **Step 2: The backup runbook.** `docs/operations/backup.md`:

````markdown
# Backup and restore

For a server run with [`deploy/docker-compose.prod.yml`](../../deploy/docker-compose.prod.yml). The commands below are the ones
`scripts/e2e-prod.sh` runs against a throwaway production stack (a dump, the database dropped, a restore, the service started again), and
after them a refresh cookie issued before the backup still refreshes. In the commands `$C` stands for

```bash
C="docker compose -f deploy/docker-compose.prod.yml --env-file .env"
```

run from the directory that holds `deploy/` and `.env`.

## What to back up

| What | Where it lives | Why |
| --- | --- | --- |
| The database | the `postgres-data` volume | Accounts, companies, members, sessions, the audit log |
| The key files | the directory named by `AUTH_KEYS_DIR` | Without the signing key every issued token is void; without the encryption key no session can be read. A restore needs the **same** keys |
| The manifest | the file named by `AUTH_MANIFEST` | The product's permissions and default roles |
| `.env` | next to your compose command | The passwords, the issuer and audience, the URLs |

Keep the key files and `.env` **apart from the dump**: a dump holds every account's password hash, and the keys sign tokens. Encrypt what
leaves the host (for example with `age` or `gpg`) and test a restore at least once a quarter.

## A nightly backup

```bash
#!/usr/bin/env bash
# /usr/local/bin/auth-core-backup: a dump of the database, kept for 14 days. Run from cron as the user that runs docker compose.
set -euo pipefail
cd /srv/auth-core                      # the directory with deploy/ and .env
C="docker compose -f deploy/docker-compose.prod.yml --env-file .env"
mkdir -p backups
umask 077
$C exec -T postgres pg_dump -U auth -d auth --format=custom --no-owner > "backups/auth-$(date +%F).dump"
find backups -name 'auth-*.dump' -mtime +14 -delete
```

```cron
17 3 * * *  /usr/local/bin/auth-core-backup
```

The custom format is compressed and restores with `pg_restore`; `--no-owner` lets it restore into the role `auth` whatever the dump was
made as. Copy `backups/` and (once, and after every key rotation) the key files, the manifest and `.env` off the host.

## Restore, step by step

A database was lost or damaged. Take the newest dump you trust (`auth-DATE.dump`), and the key files, manifest and `.env` that were in
use when it was made.

1. **Stop the service**, so that nothing writes while you restore:

   ```bash
   $C stop auth
   ```

2. **Recreate the database** (the connection is to the `postgres` database, so the target can be dropped):

   ```bash
   $C exec -T postgres psql -U auth -d postgres -c 'DROP DATABASE IF EXISTS auth WITH (FORCE)'
   $C exec -T postgres psql -U auth -d postgres -c 'CREATE DATABASE auth OWNER auth'
   ```

3. **Restore the dump**:

   ```bash
   $C exec -T postgres pg_restore -U auth -d auth --no-owner --exit-on-error < backups/auth-DATE.dump
   ```

4. **Start the service.** It migrates at start; a restored database is already at its migration, so nothing is applied unless you restored
   a dump of an older version, which then migrates forward:

   ```bash
   $C start auth
   ```

5. **Check it.** `/auth/health` does not reach the database, so it proves little: ask for something that does.

   ```bash
   $C run --rm -T auth admin list-orgs        # the companies: id, name, members
   curl -s -o /dev/null -w '%{http_code}\n' -X POST https://app.example.com/auth/refresh   # 401 without a cookie: the service answers
   ```

   Then sign in as someone and refresh. **Every session still valid at the time of the backup is valid now** (the refresh tokens are in
   the dump, the keys are the same); sessions started after the backup are gone, and so is everything else that happened after it.

If the **keys are lost** too: put new keys in place ([`key-rotation.md`](key-rotation.md)). Everyone signs in again; accounts, companies and
roles are intact.

If you restored into a **new server**: install Docker, put `deploy/`, `.env`, the keys and the manifest in place, run `$C up -d postgres`,
wait until `$C exec -T postgres pg_isready -h 127.0.0.1 -U auth -d auth` says it accepts connections (over TCP: on a fresh volume the image first
runs a temporary server that listens on its socket only), then do steps 2 and 3, and step 5. Skip step 1: there is no `auth` container yet to
stop. In step 4 use `$C up -d auth` instead of `$C start auth`: it creates the container, which `start` cannot.

## Reading the audit log

The audit log is the table `audit_events`; it is read with SQL. Define once:

```bash
auth_psql() { docker compose -f deploy/docker-compose.prod.yml --env-file .env exec -T postgres psql -U auth -d auth "$@"; }
```

The columns: `occurred_at`, `kind`, `actor_user_id` (the account that acted; empty for the operator CLI, an anonymous request or a failed
login), `subject_user_id`, `subject_email`, `org_id`, `org_name`, `target_id` (the role or invitation), `client_ip`, `details` (a small JSON
object; `"via": "cli"` for the operator). A row outlives the account, company, role or invitation it names. It never holds a password, a
token, a link, a cookie or a mail body. Rows older than `AUTH_AUDIT_RETENTION_DAYS` (90 by default) are deleted every hour.

**Everything about an account** (the address as you know it; the first line finds the account's id):

```sql
SELECT occurred_at, kind, actor_user_id, subject_user_id, subject_email, org_name, client_ip, details
FROM audit_events
WHERE subject_user_id = (SELECT "Id" FROM "AspNetUsers" WHERE "NormalizedEmail" = upper('boss@acme.example'))
   OR actor_user_id   = (SELECT "Id" FROM "AspNetUsers" WHERE "NormalizedEmail" = upper('boss@acme.example'))
   OR upper(subject_email) = upper('boss@acme.example')
ORDER BY occurred_at;
```

**Every change in a company** (the company may be gone: find its id by name from the log itself):

```sql
SELECT DISTINCT org_id, org_name FROM audit_events WHERE org_name ILIKE '%acme%';

SELECT occurred_at, kind, actor_user_id, subject_email, target_id, details
FROM audit_events
WHERE org_id = '00000000-0000-0000-0000-000000000000'     -- the id from the query above
ORDER BY occurred_at;
```

**Failed logins from an address** (an IPv6 address is recorded whole; one subscriber holds a whole `/64`, so match its prefix):

```sql
SELECT occurred_at, subject_email, details->>'reason' AS reason
FROM audit_events
WHERE kind = 'login.failed' AND client_ip = '203.0.113.9'
ORDER BY occurred_at DESC LIMIT 200;

SELECT client_ip, count(*) FROM audit_events
WHERE kind = 'login.failed' AND client_ip LIKE '2001:db8:1:2:%' GROUP BY client_ip ORDER BY 2 DESC;
```

Two more that are asked for often: who deleted a company, and who is hitting the limits.

```sql
SELECT occurred_at, kind, actor_user_id, org_name, client_ip, details FROM audit_events WHERE kind IN ('org.deleted', 'org.delete_refused') ORDER BY occurred_at DESC;

SELECT client_ip, details->>'policy' AS policy, count(*) FROM audit_events
WHERE kind = 'rate_limit.hit' AND occurred_at > now() - interval '1 day' GROUP BY 1, 2 ORDER BY 3 DESC;
```

The kinds are: `login.succeeded`, `login.failed` (reason: `wrong_password`, `unknown_address`, `unconfirmed_address`, `no_company`), `login.locked`, `logout`,
`refresh.reuse_detected`, `password.reset_requested`, `password.reset`, `email.verified`, `invite.sent`, `invite.resent`, `invite.accepted`,
`invite.cancelled`, `member.removed`, `member.role_changed`, `role.created`, `role.updated`, `role.deleted`, `org.created`, `org.renamed`,
`org.deleted`, `org.delete_refused` (reason: `wrong_password`, `locked`) and `rate_limit.hit` (at most one per address and policy per minute).
````

- [ ] **Step 3: The key rotation runbook.** `docs/operations/key-rotation.md`:

````markdown
# Key rotation

Auth-Core signs its access tokens with an RSA key and protects its refresh tokens with another; both live in four files that the
container mounts read-only (`AUTH_KEYS_DIR` in `.env`): `signing.crt`, `signing.key`, `encryption.crt`, `encryption.key`. **There is no
rotation without signing everyone out.** The service keeps no previous keys for verification or decryption (spec 0008, Decision 7), and
rotation is not automatic. Plan it for a quiet hour; the cost is that every person signs in again.

## What a key change does

| Who | What happens |
| --- | --- |
| Everyone with a session | Their refresh token cannot be read with the new encryption key: the next refresh is `401 invalid_grant`, and the app signs them out. They sign in again with their password |
| A person with an access token | It keeps working at product backends until it expires (10 minutes at most) or until the backend fetches the new key set. The package does that at most every 10 seconds when it meets an unknown `kid` and every 5 minutes anyway, so its old key stops being believed within 5 minutes of its next fetch |
| A product backend | Nothing to do. It sees the new `kid` at its next key fetch (`/auth/.well-known/jwks.json`) |
| Accounts, companies, roles, invitations, the audit log | Untouched |
| An invitation link or a reset link | Still works: those tokens are hashes in the database, not signed |

## Generating production keys

On a machine you trust, in a new directory. RSA of at least 2048 bits is required; use 3072. The key must be PKCS#8 PEM, and the certificate must
carry the key usage OpenIddict checks (`digitalSignature` for signing, `keyEncipherment` for encryption).

```bash
set -euo pipefail
mkdir -p keys-new && cd keys-new
umask 077
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=auth-core-signing" \
  -addext "keyUsage=critical,digitalSignature" -keyout signing.key -out signing.crt
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=auth-core-encryption" \
  -addext "keyUsage=critical,keyEncipherment" -keyout encryption.key -out encryption.crt
openssl pkey -in signing.key -noout && openssl pkey -in encryption.key -noout && echo "keys are readable"
```

(On Git Bash for Windows prefix the two `openssl req` lines with `MSYS2_ARG_CONV_EXCL='/CN='`.) The certificates are only containers for the
public keys: their dates and names are not checked by the service. The container runs as the user with uid `1654`, so the files must be
readable by it and by nobody else:

```bash
sudo chown 1654:1654 signing.key signing.crt encryption.key encryption.crt
sudo chmod 0400 signing.key encryption.key
sudo chmod 0444 signing.crt encryption.crt
sudo chmod 0555 .
```

Never use the development keys of `scripts/dev-keys.sh` (they are world-readable on purpose). Keep a copy of every set of keys somewhere safe, apart from
the database dumps ([`backup.md`](backup.md)): a restore needs the keys that were in use when the dump was made.

## Planned rotation

1. **Back up first**: the database ([`backup.md`](backup.md)), and the current key directory.
2. **Announce it.** People are signed out within the access token's life, 10 minutes, or at their next refresh.
3. **Put the new keys in place.** Either replace the four files in `AUTH_KEYS_DIR`, or generate them in a new directory and change `AUTH_KEYS_DIR`
   in `.env` to it. Keep the old directory.
4. **Restart the service** so that it reads them (the compose file mounts the directory read-only; a running container does not notice a change):

   ```bash
   docker compose -f deploy/docker-compose.prod.yml --env-file .env up -d --force-recreate auth
   ```

5. **Check the key set** (the `kid` must be new) and sign in:

   ```bash
   curl -s https://app.example.com/auth/.well-known/jwks.json | python3 -c 'import json,sys; print([k["kid"] for k in json.load(sys.stdin)["keys"]])'
   ```

   Compare it with the `kid` you saw before the change (record it in step 1). Then sign in as someone and call a product endpoint.
6. **Watch the backends.** For up to five minutes a backend may still believe the old key for tokens issued before the change, and answers `401` for the
   new ones until it has fetched the new key set; with the package it is a few seconds. Every person who was signed in is asked to sign in again.
7. **Keep the old keys for a day**, in case you must roll back (step 8), then delete them securely.
8. **Rolling back** is the same as rotating: put the old files back, recreate the service. Sessions that were started with the new keys are signed out again.

## Emergency rotation: a key may have leaked

Do the planned steps at once, and also:

- If the **signing key** leaked, anyone can make tokens that products accept until their key caches drop the old key. To end that **at once**
  everywhere, change the audience too: set a new `AUTH_AUDIENCE` in `.env` and in every backend that checks it (the package's `audience`),
  and restart both. Tokens with the old audience are refused by backends whatever their signature.
- If the **encryption key** leaked, the refresh tokens in the database can be read; the rotation makes them unreadable, so every session ends.
- If the **database or `.env`** leaked as well: change `POSTGRES_PASSWORD` (`ALTER ROLE auth PASSWORD '...'` inside PostgreSQL, then `.env`), and
  consider every password hash exposed: ask people to reset their passwords (the audit log shows who signed in meanwhile).
- Look at the audit log for the period ([`backup.md`](backup.md), "Reading the audit log"): `login.succeeded` from addresses you do not know, `refresh.reuse_detected`,
  `member.role_changed`, `invite.sent`.
- Write down what happened and when; the old keys are evidence.
````

- [ ] **Step 4: The document check, and its run.** `scripts/check-docs.py` checks documents against the repository (no Docker, no network, the standard library only). Review finding: the first version of this check looked only at paths that start with `src/`, `docs/` and so on, and so passed `e2e/02-session.spec.ts` and `token-from-url.ts`, which name files of the Angular sample without its folder, and never looked at the tests the threat model names. This one checks every relative link, every path that starts with a folder of the repository, every bare file name in a code span (it is a path of the repository, or the end of the path of at least one file), every test class and `Class.Method` in a code span (the class exists under `tests/`, the method is in it), and a span such as `.Some_method` against the class named before it on the same line, and it refuses an emoji. It lists tracked and untracked files (`git ls-files -co --exclude-standard`), so new files are seen before they are committed.

```python
#!/usr/bin/env python3
"""Checks the documents of spec 0008 against the repository: no Docker, no network, the standard library only.

Usage, from the repository root:  python scripts/check-docs.py [file.md ...]
With no file it checks the documents of the release (the list below). Exit status 0 when everything resolves, 1 otherwise.

What it checks in each document:
  1. every relative link ends at a file or a directory that exists;
  2. every code span that is a path of the repository (it starts with src/, tests/, docs/, deploy/, scripts/, samples/ or clients/)
     exists;
  3. every code span that is a bare file name (`token-from-url.ts`, `e2e/02-session.spec.ts`, `docker-compose.prod.yml`, `Caddyfile`)
     is a path of the repository or the end of the path (after a slash) of at least one file;
  4. every code span that names a test class (`SecurityHeadersTests`) has a file of that name under tests/ or declares that class, and
     `Class.Method` has the method in it; a span that starts with a dot and is a method name (`.Some_method`) is checked against the
     class named last on the same line;
  5. no emoji.
"""
import pathlib
import re
import subprocess
import sys

DOCUMENTS = [
    "README.md",
    "CHANGELOG.md",
    "docs/deployment/vps.md",
    "docs/security/threat-model.md",
    "docs/operations/backup.md",
    "docs/operations/key-rotation.md",
    "clients/python/README.md",
]
ROOTS = ("src/", "tests/", "docs/", "deploy/", "scripts/", "samples/", "clients/")
FILE_NAME = re.compile(r"^[A-Za-z0-9_][A-Za-z0-9_./@-]*\.(ts|tsx|cs|py|sh|yml|yaml|md|json|mjs|toml|csproj|props|slnx)$")
BARE_NAMES = {"Caddyfile", "Dockerfile"}
TEST_CLASS = re.compile(r"^([A-Z]\w*Tests)(?:\.(\w+))?$")
TEST_METHOD = re.compile(r"^\.(\w+)$")
EMOJI = re.compile("[\U0001F300-\U0001FAFF]")

root = pathlib.Path(__file__).resolve().parent.parent
listing = subprocess.run(
    ["git", "-C", str(root), "ls-files", "-co", "--exclude-standard"], capture_output=True, text=True, check=True
).stdout.splitlines()
files = {line.strip() for line in listing if line.strip()}
test_sources = {name: (root / name).read_text(encoding="utf-8", errors="replace") for name in files if name.startswith("tests/") and name.endswith(".cs")}


def class_source(name):
    """The text of the file that declares the test class, or None."""
    exact = [text for path, text in test_sources.items() if path.rsplit("/", 1)[-1] == name + ".cs"]
    if exact:
        return exact[0]
    for text in test_sources.values():
        if re.search(r"\bclass\s+" + re.escape(name) + r"\b", text):
            return text
    return None


def check(name):
    path = root / name
    if not path.exists():
        return [f"{name}: the document does not exist"]
    problems = []
    text = path.read_text(encoding="utf-8")
    for target in re.findall(r"\]\(([^)#\s]+)(?:#[^)]*)?\)", text):
        if target.startswith(("http://", "https://", "mailto:")):
            continue
        if not (path.parent / target).resolve().exists():
            problems.append(f"{name}: the link {target} does not resolve")
    for number, line in enumerate(text.splitlines(), start=1):
        last_class = None
        for span in re.findall(r"`([^`\n]+)`", line):
            span = span.strip()
            if re.search(r"[\s*<>{}=$()|,:\\]", span):
                continue
            if span.startswith(ROOTS):
                if not (root / span.rstrip("/")).exists():
                    problems.append(f"{name}:{number}: the path {span} does not exist")
                continue
            if FILE_NAME.match(span) or span in BARE_NAMES:
                if span not in files and not any(f.endswith("/" + span) for f in files):
                    problems.append(f"{name}:{number}: the file {span} is no file of the repository")
                continue
            found = TEST_CLASS.match(span)
            if found:
                last_class = found.group(1)
                source = class_source(last_class)
                if source is None:
                    problems.append(f"{name}:{number}: the test class {last_class} does not exist")
                elif found.group(2) and not re.search(r"\b" + re.escape(found.group(2)) + r"\b", source):
                    problems.append(f"{name}:{number}: {last_class} has no test {found.group(2)}")
                continue
            method = TEST_METHOD.match(span)
            if method and last_class:
                source = class_source(last_class)
                if source is not None and not re.search(r"\b" + re.escape(method.group(1)) + r"\b", source):
                    problems.append(f"{name}:{number}: {last_class} has no test {method.group(1)}")
    if EMOJI.search(text):
        problems.append(f"{name}: an emoji")
    return problems


def main(arguments):
    documents = arguments or DOCUMENTS
    problems = []
    for document in documents:
        problems.extend(check(document))
    print("\n".join(problems) or "all links, paths, files and tests resolve")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
```

  Run it on the three documents of this task (on this machine `python3` is a stub: use the interpreter of plan 0006):

```bash
C:/p6v/Scripts/python.exe scripts/check-docs.py docs/security/threat-model.md docs/operations/backup.md docs/operations/key-rotation.md
```

  Expected: `all links, paths, files and tests resolve`. (The links to `docs/deployment/vps.md` resolve only after Task 14: until then that one line is the only
  expected problem; the check runs again at the end of Task 14. The test classes it names are those of Tasks 1 to 8, which exist by now.) Every setting and command the documents name exists: `AUTH_KEYS_DIR`,
  `AUTH_AUDIT_RETENTION_DAYS`, `AUTH_AUDIENCE` and `POSTGRES_PASSWORD` are in `deploy/docker-compose.prod.yml`; `admin list-orgs` is in the CLI;
  `audit_events`, its columns and its kinds are the table of Task 4.

- [ ] **Step 5: Hand back** — uncommitted. Files: the three documents and `scripts/check-docs.py`. Proposed subjects: `docs: threat model, backup runbook and key rotation runbook`; `chore: scripts/check-docs.py, a check of the documents against the repository`.

### Task 14: The deployment guide, the README, the changelog, the package metadata, the secret-scan list and the acceptance map

**Files:**
- Create: `docs/deployment/vps.md`, `CHANGELOG.md`, `.gitleaksignore`, `scripts/secret-scan.sh`, `clients/python/LICENSE`, `clients/python/README.md`, `docs/superpowers/plans/0008-acceptance-map.md`
- Modify: `README.md` (rewritten), `clients/python/pyproject.toml`, `clients/python/src/auth_core_fastapi/__init__.py`, `docs/integration/python-fastapi.md` (the install line), `samples/notes-api/requirements.txt` (the comment that names the tag)
- Modify (test): `clients/python/tests/test_jwks_cache.py` (one line: a `429` from the key set is a failed fetch)
- Test: `clients/python/tests/test_metadata.py`

**Interfaces:**
- Consumes: everything of Tasks 1–13 (the guide describes `deploy/docker-compose.prod.yml` and the runbooks, which this task links to), the test and script names of the plan (the map names them), `scripts/e2e-prod.sh`.
- Produces: the deployment guide that someone who has never seen the project follows from a published image to a first sign-in; the README and the changelog of `v0.1.0`; the package at `0.1.1` with its licence and readme in the metadata; `.gitleaksignore` (the eight known false positives of the history scan) and `scripts/secret-scan.sh` (the secret scan of the history and of the files not committed yet, which the verifiers and the orchestrator run); the acceptance map, which the verifiers use (layer 2 of `docs/workflow.md`).

- [ ] **Step 1: The package metadata (test first).** `clients/python/tests/test_metadata.py`:

```python
"""The package's metadata (spec 0008): MIT, the licence file, a readme, version 0.1.1."""

import tomllib
import zipfile
from pathlib import Path

import pytest

import auth_core_fastapi

PACKAGE = Path(__file__).resolve().parents[1]
REPOSITORY = PACKAGE.parents[1]


def project() -> dict:
    return tomllib.loads((PACKAGE / "pyproject.toml").read_text(encoding="utf-8"))["project"]


def test_the_version_is_0_1_1_and_has_one_source():
    assert auth_core_fastapi.__version__ == "0.1.1"
    assert "version" not in project()
    assert project()["dynamic"] == ["version"]


def test_the_licence_is_mit_and_its_file_is_the_repositorys():
    data = project()
    assert data["license"] == "MIT"
    assert data["license-files"] == ["LICENSE"]
    text = (PACKAGE / "LICENSE").read_text(encoding="utf-8")
    assert text.startswith("MIT License")
    assert text == (REPOSITORY / "LICENSE").read_text(encoding="utf-8")


def test_the_readme_is_in_the_metadata_and_names_the_install_line():
    assert project()["readme"] == "README.md"
    readme = (PACKAGE / "README.md").read_text(encoding="utf-8")
    assert "auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v0.1.1#subdirectory=clients/python" in readme
    assert "docs/integration/python-fastapi.md" in readme


def test_the_guide_and_the_sample_name_the_new_tag():
    line = "@python-v0.1.1#subdirectory=clients/python"
    assert line in (REPOSITORY / "docs/integration/python-fastapi.md").read_text(encoding="utf-8")
    assert "python-v0.1.1" in (REPOSITORY / "samples/notes-api/requirements.txt").read_text(encoding="utf-8")


def test_a_wheel_carries_the_licence_the_readme_and_the_version(tmp_path, monkeypatch):
    build = pytest.importorskip("hatchling.build")
    monkeypatch.chdir(PACKAGE)
    name = build.build_wheel(str(tmp_path))
    with zipfile.ZipFile(tmp_path / name) as wheel:
        names = wheel.namelist()
        metadata = wheel.read(next(n for n in names if n.endswith(".dist-info/METADATA"))).decode("utf-8")
        assert any(n.endswith(".dist-info/licenses/LICENSE") for n in names)
    assert "Version: 0.1.1" in metadata
    assert "License-Expression: MIT" in metadata
    assert "Description-Content-Type: text/markdown" in metadata
    assert "auth-core-fastapi" in metadata.split("\n\n", 1)[1]   # the readme is the long description
```

Spec 0008 says the package needs no change of behaviour: a `429` or `503` from the key set is a failed fetch, which its key cache already handles. The existing
test covers `404`, `500` and `503`; one line pins the `429` too, in `clients/python/tests/test_jwks_cache.py`:

```diff
-@pytest.mark.parametrize("status", [404, 500, 503])
+@pytest.mark.parametrize("status", [404, 429, 500, 503])
 def test_an_error_status_is_a_failed_fetch(server, status):  # criterion 5
```

Run (the virtual environment of plan 0006, `C:/p6v`, has the package installed with its test dependencies):

```bash
cd clients/python && C:/p6v/Scripts/python.exe -m pytest tests/test_metadata.py -q
```

Expected: FAIL (the version is `0.1.0`, there is no `LICENSE` and no `README.md` in `clients/python/`).

- [ ] **Step 2: Make it pass.**

```bash
cp LICENSE clients/python/LICENSE
```

`clients/python/pyproject.toml` — the project table (the rest of the file stays; the build backend stays pinned):

```diff
 [project]
 name = "auth-core-fastapi"
 dynamic = ["version"]
 description = "Verify Auth-Core access tokens in a FastAPI backend"
+readme = "README.md"
+license = "MIT"
+license-files = ["LICENSE"]
 requires-python = ">=3.12"
 dependencies = ["fastapi>=0.142", "PyJWT[crypto]>=2.15"]
 
+[project.urls]
+Repository = "https://github.com/MckCieply/Auth-Core"
+Documentation = "https://github.com/MckCieply/Auth-Core/blob/main/docs/integration/python-fastapi.md"
+
 [project.optional-dependencies]
```

`clients/python/src/auth_core_fastapi/__init__.py`:

```diff
-__version__ = "0.1.0"
+__version__ = "0.1.1"
```

`clients/python/README.md`:

````markdown
# auth-core-fastapi

Verify the access tokens of [Auth-Core](https://github.com/MckCieply/Auth-Core), a self-hosted authentication service, in a FastAPI
backend: two dependencies, `current_user` and `require_permission`, over a small cache of the service's public keys. The package checks
the token offline (RS256, issuer, audience, expiry, the claims `sub`, `org_id`, `roles` and `permissions`); it never calls Auth-Core per
request.

```python
from fastapi import Depends, FastAPI
from auth_core_fastapi import AuthCore, Principal

app = FastAPI()
auth = AuthCore(issuer="https://app.example.com/auth", audience="my-product-api")
auth.install(app)


@app.post("/api/notes")
def add(user: Principal = Depends(auth.require_permission("notes:write"))):
    return {"company": user.org_id}   # every query is filtered by user.org_id
```

Install it from the repository at a tag (it is not on PyPI):

```
auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v0.1.1#subdirectory=clients/python
```

Needs Python 3.12, FastAPI 0.142 or newer and `PyJWT[crypto]` 2.15 or newer. The step-by-step guide, with the answers of the package
(`401`, `403 forbidden`, `503 auth_unavailable`), the key rules and a sample product, is
[`docs/integration/python-fastapi.md`](https://github.com/MckCieply/Auth-Core/blob/main/docs/integration/python-fastapi.md).

Licensed under the MIT licence.
````

`docs/integration/python-fastapi.md` — the install line (step 1) and every other mention of the tag:

```diff
-auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v0.1.0#subdirectory=clients/python
+auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v0.1.1#subdirectory=clients/python
```

`samples/notes-api/requirements.txt` — in the comment on line 4:

```diff
-#   auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v0.1.0#subdirectory=clients/python
+#   auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v0.1.1#subdirectory=clients/python
```

(If the guide says "pin to the commit the tag points at", leave that sentence: it is still right for `python-v0.1.1`. The tag `python-v0.1.0` is not
moved and not removed: the spec keeps it.)

Run the package tests and the sample's, which must not have changed:

```bash
cd clients/python && C:/p6v/Scripts/python.exe -m pytest -q && cd ../../samples/notes-api && C:/p6v/Scripts/python.exe -m pytest -q
```

Expected: PASS (the package: its 206 tests and the five new ones; the sample: 97).

- [ ] **Step 3: The secret scan and its list.** Two files: the list of the known false positives, and the script that runs the scan (so that Task 14, the verifiers and the orchestrator run one and the same command).

  What was found, on this machine, on the history of the repository as it is now (main `193d3c6`, every branch and tag; re-run with `--branches --tags` after the change below: the same eight findings, the same two fingerprints): gitleaks v8.30.1 reports **eight findings in two lines**, all of one rule (`generic-api-key`): a test password of the Angular sample in `samples/notes-web/src/app/pages/invite.spec.ts` (line 65) and in the plan that wrote it, `docs/superpowers/plans/0007-angular-sample.md` (line 3859), each in four commits. (The password appears in twelve commits when searched by text, `git log -S`, because it moved with the files; the scan reports a finding where its rule fires and the history of those two files holds eight such places.) The fingerprint without a commit (`file:rule:line`) matches in every commit and survives a rewrite of the history.

`.gitleaksignore`:

```
# gitleaks v8.30.1 over the whole history (spec 0008, criterion 17): the eight findings are two lines, each in four commits, and each is a
# test password in a spec file of the Angular sample and in the plan that wrote it. Not a secret of any system. The fingerprint is
# file:rule:line, which matches in every commit (a commit's own hash would break whenever the history is rewritten).
samples/notes-web/src/app/pages/invite.spec.ts:generic-api-key:65
docs/superpowers/plans/0007-angular-sample.md:generic-api-key:3859
```

`scripts/secret-scan.sh` (LF; the orchestrator makes it executable in the index):

```bash
#!/usr/bin/env bash
# The secret scan of spec 0008 (criterion 17): gitleaks v8.30.1, pinned by digest, in a container. It reads the repository and writes
# nothing but its two reports, in a scratch directory that is removed on exit. The text of a finding is redacted, and never printed.
#
#   1. the committed history of every branch and tag (what is published), and not the local refs that are never pushed (backup refs, the stash);
#   2. the files of this working tree that are not committed yet (modified or new, and not git-ignored): what the next commit adds.
#
# The findings that are known and harmless are listed in .gitleaksignore (file:rule:line, with no commit: it matches in every commit and
# survives a rewrite of the history). Any other finding is printed as its fingerprint (commit:file:rule:line for the history, file:rule:line
# for the files) and fails the script. To list a finding that is a throw-away test value, copy the file:rule:line part of its fingerprint
# into .gitleaksignore; a finding that may be a real secret is never listed: it stops the work.
#
# Run from anywhere in the repository (a worktree too): scripts/secret-scan.sh. Needs docker and tar. Exit code: 0 clean, 1 findings.
set -euo pipefail

IMAGE="zricethezav/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# The .git of a worktree is a file that names a path of the host, which the container does not have: mount the repository that owns
# the history (the parent of the common git directory) instead.
repo="$(dirname "$(git -C "$root" rev-parse --path-format=absolute --git-common-dir)")"

scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
mkdir -p "$scratch/out" "$scratch/tree" "$scratch/ignore"
# The history scan reads the list as it is (file:rule:line). The files are scanned as /work/..., and gitleaks names a finding by that
# path, so the same list with that prefix is what the second scan reads.
cp "$root/.gitleaksignore" "$scratch/ignore/history"
sed -e '/^#/b' -e '/^[[:space:]]*$/b' -e 's#^#/work/#' "$root/.gitleaksignore" > "$scratch/ignore/tree"
git -C "$root" ls-files -z -m -o --exclude-standard | tar --null --ignore-failed-read -C "$root" -T - -cf - 2> /dev/null \
  | tar -C "$scratch/tree" -xf -

# The docker of a Windows machine wants Windows paths for what it mounts.
host_path() { if command -v cygpath > /dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

status=0
scan() { # scan <label> <report> <ignore list> <docker args and gitleaks command...>: findings make gitleaks exit 2, any other non-zero is an error
  local label="$1" report="$2" ignore="$3" code=0
  shift 3
  MSYS_NO_PATHCONV=1 docker run --rm \
    -v "$(host_path "$scratch/ignore"):/ignore:ro" -v "$(host_path "$scratch/out"):/out" "$@" \
    --redact --no-banner --exit-code 2 --gitleaks-ignore-path "/ignore/$ignore" --report-format json --report-path "/out/$report" \
    > "$scratch/$report.log" 2>&1 || code=$?
  case "$code" in
    0) echo "PASS $label: no leaks found" ;;
    2)
      echo "FOUND $label:"
      grep '"Fingerprint"' "$scratch/out/$report" | sed 's/^[[:space:]]*"Fingerprint": "//; s/",\{0,1\}\r\{0,1\}$//; s#^/work/##; s/^/  /'
      status=1
      ;;
    *)
      cat "$scratch/$report.log" >&2
      echo "FAIL $label: gitleaks or docker failed (exit $code)" >&2
      exit 1
      ;;
  esac
}

scan "the history" history.json history \
  -v "$(host_path "$repo"):/repo:ro" "$IMAGE" git /repo --log-opts="--branches --tags"
scan "the files not committed yet" tree.json tree \
  -v "$(host_path "$scratch/tree"):/work:ro" "$IMAGE" dir /work

exit "$status"
```

  **What the script does and why it is built this way** (all of it probed on this machine): it scans (1) the committed history of every branch and tag (`--branches --tags`: `--all` would also read `refs/backup/*` and the stash, which are never pushed) and (2) the files of the working tree that are not committed yet, because gitleaks' `git` mode reads commits only, and the work of this slice is uncommitted until the orchestrator commits it. A worktree's `.git` is a file that names a path of the host, which the container does not have, so the script mounts the repository that owns the history (the parent of `git rev-parse --git-common-dir`) and passes the list of known findings as a separate mount (`--gitleaks-ignore-path`). The files of the second scan are copied from `git ls-files -m -o --exclude-standard`, so a git-ignored local file (`.env`, `.secrets/`, the development keys) is never scanned or copied. gitleaks names the findings of a `dir` scan by their path in the container, so the script gives that scan the same list with the `/work/` prefix. The exit code of gitleaks is set to 2 for findings, so that a failure of docker or of the scan is not read as a finding. A finding is printed as its fingerprint, never as its text.

  **What to do with a finding.** (a) Run `scripts/secret-scan.sh`. (b) For every finding that the scan reports for the Angular sample's known test password (the two files named above), its fingerprint (`file:rule:line`) is already in the list; if the history of slice 7 moved the lines, put **all** the fingerprints the scan reports for those two files in `.gitleaksignore` (replace the two lines), and no others. (c) **Any other finding stops the task and is reported to the orchestrator**, the plan's own new text included: the generic rule fires on a value that looks like a password next to the word `password` (it fired on the wrong-password literal of this slice's tests while the plan was being written, and that literal was changed to one it does not flag). A test value that fires the rule is changed to one that does not, not listed; a value that may be real is never listed.

  Expected: `PASS the history: no leaks found` and `PASS the files not committed yet: no leaks found`, exit code 0.

- [ ] **Step 4: The deployment guide.** `docs/deployment/vps.md`:

````markdown
# Deploying Auth-Core on a server

From nothing to a first sign-in on a small Linux server (a VPS), from the published image. It is written for a server that runs Docker and a
reverse proxy; the proxy gives you HTTPS. Everything here is tried on a throwaway stack by `scripts/e2e-prod.sh` (the production compose
file, an SMTP relay that requires STARTTLS, a proxy with HTTPS), including the backup and the restore.

## What you need

- A Linux server with Docker and the Compose plugin (`docker compose version`), and `openssl`.
- A domain name for the product, say `app.example.com`, pointing at the server. Auth-Core lives at `/auth` of **that** origin, next to your product's
  backend at `/api` and its frontend ([ADR 0004](../adr/0004-same-origin-cookie-refresh.md)).
- An SMTP relay that offers TLS (STARTTLS on 587 or TLS on 465) and a sender address on a domain you control (SPF, DKIM and DMARC are the relay's and
  the domain's business). Auth-Core refuses to start with a relay that does not use TLS.
- A manifest for your product: its permissions and the default roles of a new company ([`docs/integration/python-fastapi.md`](../integration/python-fastapi.md), step 2).

## 1. Get the files

You need two files and nothing else from the repository: the compose file and the example of its environment. At a tag:

```bash
sudo mkdir -p /srv/auth-core && cd /srv/auth-core
curl -fsSLO https://raw.githubusercontent.com/MckCieply/Auth-Core/v0.1.0/deploy/docker-compose.prod.yml
curl -fsSL  https://raw.githubusercontent.com/MckCieply/Auth-Core/v0.1.0/deploy/.env.prod.example -o .env
mkdir -p deploy && mv docker-compose.prod.yml deploy/
chmod 600 .env
```

(Or clone the repository at the tag and use its `deploy/` directory.) The image `ghcr.io/mckcieply/auth-core:0.1.0` is public; `docker compose` pulls it.

## 2. The keys

Make the two RSA keys as [`docs/operations/key-rotation.md`](../operations/key-rotation.md) says ("Generating production keys"), into `/etc/auth-core/keys`, owned by
uid `1654` (the container's user) and not readable by anyone else. Put a copy somewhere safe, apart from the server.

## 3. The manifest

Copy your product's `auth.yaml` to `/etc/auth-core/auth.yaml`. At least one default role must hold `members:manage` or `"*"`, so that the first admin of a
company can manage it. (`deploy/auth.yaml` in the repository is a development example.) The built-in permissions are `members:manage`, `roles:manage`,
`org:manage` and `org:delete`; a role with `"*"` holds all of them, **including the right to delete the company**.

## 4. The environment

Edit `.env`. Every variable is explained in the file; these you must set:

| Variable | What |
| --- | --- |
| `POSTGRES_PASSWORD` | a long random value, letters and digits only |
| `AUTH_CORE_VERSION` | `0.1.0` |
| `AUTH_ISSUER` | `https://app.example.com/auth`: the origin your users see plus `/auth`. Your backend's package must be given the same value |
| `AUTH_AUDIENCE` | the audience your backend checks, for example `my-product-api` |
| `AUTH_KEYS_DIR`, `AUTH_MANIFEST` | the two paths above |
| `AUTH_APP_NAME`, `AUTH_APP_LOCALE` | the product's name in the mails, and `pl` or `en` |
| `AUTH_FRONTEND_RESET_URL`, `AUTH_FRONTEND_VERIFY_URL`, `AUTH_FRONTEND_INVITE_URL` | the three screens of **your frontend** that the mails link to, https |
| `AUTH_EMAIL_FROM`, `AUTH_SMTP_HOST`, `AUTH_SMTP_PORT`, `AUTH_SMTP_SECURITY`, `AUTH_SMTP_USERNAME`, `AUTH_SMTP_PASSWORD` | the relay |

Leave `AUTH_PROXY_KNOWN_PROXIES` at `10.250.0.1` while your reverse proxy runs **on this host**: it reaches the published port from the gateway of the compose
network `auth` (the first address of `AUTH_SUBNET`, `10.250.0.0/24`), and that is the one address whose `X-Forwarded-For` Auth-Core believes. If you change
the subnet, change this address with it. If the proxy runs in a container, see "A proxy in a container" under step 6.
**If no proxy is trusted, no forwarded header is read and every client looks like the proxy: all of them then share one set of rate limits.**

**The subnets.** The two networks of the compose file have fixed subnets, `10.250.0.0/24` (`auth`, `AUTH_SUBNET`) and `10.250.1.0/24` (the proxy network,
`AUTH_PROXY_SUBNET`), so that the trusted proxy can be named. They are outside the ranges Docker takes its own networks from (`172.17.0.0/16` to
`172.31.0.0/16` and `192.168.0.0/16`): a fixed subnet taken from those can overlap a network Docker has already made, and Compose then stops with "Pool
overlaps". If `10.250.0.0/24` or `10.250.1.0/24` clashes with a network of yours (a VPN, an office network), change `AUTH_SUBNET`, `AUTH_PROXY_SUBNET` and
the addresses that name them (`AUTH_PROXY_KNOWN_PROXIES`, `AUTH_PROXY_KNOWN_NETWORKS`) together.

## 5. Start it

```bash
docker compose -f deploy/docker-compose.prod.yml --env-file .env up -d
docker compose -f deploy/docker-compose.prod.yml --env-file .env logs -f auth      # until you see it listening
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8080/auth/health          # 200
```

The service creates and migrates its database on its own at start. **`/auth/health` does not reach the database**, so a `200` there does not prove it is up. Create the
first company: that needs the database.

```bash
docker compose -f deploy/docker-compose.prod.yml --env-file .env run --rm auth admin create-org --name "Acme"   # prints the company id
```

If it prints an id the database works. If the service does not start, `docker compose logs auth` names the setting that is wrong or missing (it never prints a
value): a key file that cannot be read, a mail relay without TLS, an `http` frontend URL, a missing issuer.

## 6. The reverse proxy and HTTPS

Auth-Core speaks plain HTTP on `127.0.0.1:8080` and expects TLS to end at your proxy. With Caddy (it gets and renews the certificate itself) a
`Caddyfile` for the product looks like this; it sends HSTS and the headers, hands Auth-Core the client address, and removes the `Server` header:

```
app.example.com {
	encode gzip
	header {
		Strict-Transport-Security "max-age=31536000"
		X-Content-Type-Options nosniff
		X-Frame-Options DENY
		Referrer-Policy no-referrer
		Permissions-Policy "camera=(), microphone=(), geolocation=(), payment=()"
		-Server
	}
	handle /auth/* {
		# The address Caddy saw, never one the client wrote: header_up sets the header.
		reverse_proxy 127.0.0.1:8080 {
			header_up X-Forwarded-For {remote_host}
			header_up X-Forwarded-Proto {scheme}
		}
	}
	handle /api/* {
		reverse_proxy 127.0.0.1:8000        # your product's backend
	}
	handle {
		root * /srv/app                     # your frontend, built
		try_files {path} /index.html
		file_server
	}
}
```

Auth-Core sends no `Strict-Transport-Security` itself: the proxy does. A frontend needs its own `Content-Security-Policy` (the Angular sample's `Caddyfile` is an example).
The mails' links open `AUTH_FRONTEND_*_URL`: they must be routes of your frontend.

### A proxy in a container

A proxy that runs on this host (as above) needs nothing more. A proxy that runs in a container joins a network that the compose file of Auth-Core creates:
the network named by `AUTH_PROXY_NETWORK` (default `auth-core-proxy`), with the subnet `AUTH_PROXY_SUBNET` (default `10.250.1.0/24`). Declare it as an
**external** network in the proxy's own compose file, and use the service name `auth` as the address of Auth-Core:

```yaml
# the proxy's own compose file
services:
  caddy:
    image: caddy:2            # pin it by digest, as the compose file of Auth-Core does
    networks: [auth-core-proxy]
    # ... ports 80 and 443, the Caddyfile, a volume for /data
networks:
  auth-core-proxy:
    external: true
    name: auth-core-proxy     # the value of AUTH_PROXY_NETWORK
```

In the Caddyfile use `reverse_proxy auth:8080 { ... }` (the headers are those above). In `.env` of Auth-Core set `AUTH_PROXY_KNOWN_NETWORKS=10.250.1.0/24` (the same as
`AUTH_PROXY_SUBNET`) and leave `AUTH_PROXY_KNOWN_PROXIES` as it is or blank it if no proxy runs on the host. Start Auth-Core first: the network exists once its
compose file has been brought up, and the proxy's compose file refuses an external network that does not exist.

## 7. The first company and its admin

```bash
C="docker compose -f deploy/docker-compose.prod.yml --env-file .env"
$C run --rm -T auth admin create-org --name "Acme"                              # prints the company id
$C run --rm -T auth admin invite --org <id> --email boss@acme.example --role admin
$C run --rm -T auth admin list-orgs
```

The invitation is mailed at the server's next poll, within a minute, and only while the service runs. The admin follows the link to your frontend's invitation
screen, chooses a password and signs in. Other members are invited from the company API (`POST /auth/org/invites`) by anyone who holds `members:manage`.

## 8. Check it

```bash
curl -si https://app.example.com/auth/health | head -n 12          # 200, the security headers, no Server header
curl -s  https://app.example.com/auth/.well-known/jwks.json         # the public keys
curl -s  https://app.example.com/auth/openapi/v1.json | head -c 200  # the API description (the interactive reference is not served in Production)
```

Sign in through your frontend, and call one product endpoint with the token.

## Running it

- **Limits.** 30 logins, 60 refreshes, 10 mail requests, 20 invitation requests and 300 other requests a minute per client address, answered `429 too_many_requests`
  with `Retry-After`. An office behind one NAT shares them: raise `AUTH_RATE_LIMIT_LOGIN` for it. A person who fails ten times is locked for a minute or more
  (`429 too_many_attempts`).
- **Backups.** A nightly dump and a restore that you have tried: [`docs/operations/backup.md`](../operations/backup.md).
- **The audit log** is the table `audit_events`, kept 90 days by default (`AUTH_AUDIT_RETENTION_DAYS`) and read with SQL: the queries are in the backup runbook.
- **Keys** change only by a rotation that signs everyone out: [`docs/operations/key-rotation.md`](../operations/key-rotation.md).
- **What can still go wrong** is listed in [`docs/security/threat-model.md`](../security/threat-model.md).

## Upgrading to a new version

1. Read the [changelog](../../CHANGELOG.md) for the version.
2. **Back up** ([`backup.md`](../operations/backup.md)): a new version may migrate the database, and there is no downgrade of a migration.
3. Set `AUTH_CORE_VERSION` in `.env` to the new version and:

   ```bash
   docker compose -f deploy/docker-compose.prod.yml --env-file .env pull auth
   docker compose -f deploy/docker-compose.prod.yml --env-file .env up -d
   ```

   The service migrates the database at start. People stay signed in: the keys and the database are the same.
4. Run a sign-in and `admin list-orgs`.
5. **Rolling back** is not a downgrade: restore the backup of step 2 with the old `AUTH_CORE_VERSION` ([`backup.md`](../operations/backup.md), "Restore").

## When something is wrong

| What you see | Why, and what to do |
| --- | --- |
| The `auth` container exits at start | `docker compose logs auth`: it names the setting (a key file, the relay's security, a frontend URL, the issuer or audience, the database) |
| `docker compose` says "required variable ... is missing" | A variable of `.env` has no default on purpose |
| Everybody gets `429 too_many_requests` | The proxy is not trusted: every client looks like the proxy and shares its limits. Check `AUTH_PROXY_KNOWN_PROXIES`, and that the proxy sets `X-Forwarded-For` |
| Refresh is `503 temporarily_unavailable` | The database cannot be reached; the cookie is kept. Look at `docker compose ps` and the logs of `postgres` |
| Mails do not arrive | `docker compose logs auth` shows each failed attempt (the dispatcher retries); check the relay's host, port, security, credentials and the sender's domain. The service verifies the relay's certificate against its chain **and for revocation**: the container needs outbound HTTP to the address of the certificate authority's revocation list (or OCSP responder), which is in the relay's certificate |
| Signed-in people are asked to sign in again | The keys changed, or the database was restored from before their session |
| The recorded client address is always the same | The proxy is not trusted, or does not send `X-Forwarded-For`; see above |
````

- [ ] **Step 5: The changelog.** `CHANGELOG.md`:

```markdown
# Changelog

All notable changes to Auth-Core. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are those of the tags
(`v0.1.0` for the service and its image; `python-v0.1.1` for the Python package in `clients/python/`).

## 0.1.0 - 2026-02-24

The first release: the service, its image, a Python package for backends, an Angular sample for frontends, and the documents to run it.

### Added

- **Sign-in.** `POST /auth/login` issues RS256 JWT access tokens (10 minutes; `sub`, `org_id`, `roles`, `permissions`) with the public keys at
  `/auth/.well-known/jwks.json`; the refresh token is an `HttpOnly; Secure; SameSite=Strict; Path=/auth` cookie that rotates on every use, with
  reuse detection (`POST /auth/refresh`, `POST /auth/logout`).
- **Abuse resistance.** A lockout per identifier with growing cooldowns; **per-IP rate limits** (30 logins, 60 refreshes, 10 mail requests, 20
  invitation requests and 300 other requests a minute) answered `429 too_many_requests` with `Retry-After`; the client address is read from
  `X-Forwarded-For` only from configured trusted proxies.
- **Mail flows.** Password reset and email verification, and invitations, by mail with STARTTLS or TLS.
- **Companies.** Companies, members, roles and invitations, with the company API (`/auth/org`, `/auth/me`), safety rules (nobody grants more than they
  hold; a company keeps a manager), the manifest of a product's permissions and default roles, and the operator CLI
  (`create-org`, `invite`, `list-orgs`, `remove-member`, `delete-org`).
- **Company deletion** by `DELETE /auth/org` (the permission `org:delete`, the company's name and the caller's password) and by `delete-org`.
- **Audit log.** The table `audit_events`: 22 kinds written in the transaction of the change, kept 90 days, read with SQL.
- **Security headers** on every response, and no `Server` header; a `Content-Security-Policy` of its own for the interactive reference.
- **OpenAPI** description at `/auth/openapi/v1.json`, with the interactive reference in Development.
- **Production.** The image `ghcr.io/mckcieply/auth-core:0.1.0` (public, with OCI labels, base images pinned by digest), `deploy/docker-compose.prod.yml`
  (read-only, no capabilities, a fixed subnet), `deploy/.env.prod.example`, and `docs/deployment/vps.md`.
- **Documents.** The threat model (STRIDE), the backup and the key rotation runbooks, the integration guides for Python and Angular.
- **Samples.** `samples/notes-api` (FastAPI and PostgreSQL behind Caddy) and `samples/notes-web` (Angular 21): the first backend and frontend of the service.

### Changed

- A refresh while the database is down answers `503 temporarily_unavailable` (it was `500`), and the cookie is kept.
- An unhandled error answers `500 {"error":"internal_error"}` with the headers (Kestrel's own `500` dropped them).
- `GET /auth/health` answers `GET` and `HEAD` only (any other method is `405` with `Allow: GET, HEAD`).
- A JSON body declared with a charset other than UTF-8 is `415 unsupported_media_type`.
- An invitation to an address whose domain ends in digits or in a hexadecimal form (`127.0x1`, `host.123`) is refused.
- The second development seed user takes the first role of the company, by name, that holds neither `members:manage` nor `*`.
- The built-in permission catalog gains `org:delete`: the `permissions` claim of a token whose role holds `*` has it.
- The development compose file pins `postgres` and `mailpit` by digest and passes the rate limits and the audit retention from `.env`.

### Fixed

- Data Protection keys are kept in memory (nothing uses them, and the production container's file system is read-only).

### Known limits

See [`docs/security/threat-model.md`](docs/security/threat-model.md), "Residual risks". The main ones: a key change signs everyone out; many people behind one address share its
rate limits; a company admin can delete the company; the audit log is not tamper-evident.

## python-v0.1.1 - 2026-02-24

`auth-core-fastapi` 0.1.1: the MIT licence, its file and a readme in the package metadata. No change of behaviour. `python-v0.1.0` stays as it was published.
```

- [ ] **Step 6: The README.** Replace `README.md` with:

````markdown
# Auth-Core

One reusable, self-hosted authentication service in .NET that every project drops in instead of rebuilding login, tenancy, RBAC and token
refresh. One instance per product, behind the product's own reverse proxy at `/auth`; headless: each product owns its screens.

> **Version 0.1.0.** The image is `ghcr.io/mckcieply/auth-core:0.1.0`. Sign-in with RS256 tokens and a rotating refresh cookie, lockout and
> per-IP rate limits, password reset and email verification by mail, companies with members, roles and invitations, an operator CLI, an
> audit log, security headers, an OpenAPI description, a Python package and an Angular sample, a production compose file and the runbooks to
> run it. See the [changelog](CHANGELOG.md), the [deployment guide](docs/deployment/vps.md) and the [threat model](docs/security/threat-model.md).

## How it fits

```
Browser ──> app.example.com (your reverse proxy: HTTPS, HSTS)
                ├── /api  ──> your backend (checks the JWT offline against the cached key set)
                └── /auth ──> Auth-Core  (issues tokens, serves the keys, manages companies)
```

The refresh token is an `HttpOnly; Secure; SameSite=Strict; Path=/auth` cookie; the access token (10 minutes) lives in the SPA's memory. A backend never calls
Auth-Core per request: it verifies the token with the public keys at `/auth/.well-known/jwks.json`.

## Run it in development

Needs the .NET 10 SDK, Docker with Compose, `openssl`, and Python 3 with `PyJWT[crypto]` for the e2e checks.

```bash
cp .env.example .env            # set local values; git-ignored
scripts/dev-keys.sh             # dev signing/encryption keys into .secrets/ (git-ignored)
export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own for the whole sequence (scripts/e2e-hardening.sh refuses "auth-core", the project of a development stack); the down -v at the end removes only it
AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build
scripts/e2e-login.sh            # login, key set, PyJWT verify, restart, verify again
scripts/e2e-refresh.sh          # refresh, rotation, reuse detection, logout (~30 s)
scripts/e2e-lockout.sh          # lockout, cooldown, timing medians (~2.5 min)
scripts/e2e-email.sh            # verification, reset, sessions end, mail outage (~1 min)
scripts/e2e-tenancy.sh          # CLI, invitations, roles and safety rules, removal (~2 min)
docker compose -f deploy/docker-compose.yml --env-file .env up -d      # recreates auth with the default rate limits
scripts/e2e-hardening.sh        # limits, headers, a refresh during an outage, company deletion, the audit log (~6 min)
docker compose -f deploy/docker-compose.yml --env-file .env down -v
```

The first group of checks runs with the per-IP rate limiter off (the lockout check makes more logins a minute than the limit allows); the hardening check runs
with the defaults. The stack includes a mail catcher at `http://localhost:8025`. The API is described at `http://localhost:8080/auth/openapi/v1.json`; in Development an
interactive reference is at `http://localhost:8080/auth/scalar`.

The operator's commands are subcommands of the service's own binary, so they run from its image (in the same shell, so with the same `COMPOSE_PROJECT_NAME`; against a development stack of your own, set that stack's project name instead):

```bash
auth() { docker compose -f deploy/docker-compose.yml --env-file .env run --rm -T --no-deps auth admin "$@"; }
auth create-org --name "Acme"                                     # prints the company id
auth invite --org <id> --email boss@acme.test --role admin        # mailed at the server's next poll, within a minute
auth list-orgs                                                    # id, name, number of members
auth remove-member --org <id> --email worker@acme.test [--force]  # --force overrides last_manager
auth delete-org --org <id> --confirm "Acme"                       # deletes the company; the name must be exact
```

A product's permissions and default roles come from its manifest (`Auth:Manifest:Path`; `deploy/auth.yaml` in development): a broken file never stops the service, and
`/auth/health` then says `Degraded`.

The samples are overlays of the same stack: the notes service and a proxy (`scripts/e2e-notes.sh`, on `http://localhost:8088`), and the Angular app on top of them
(`scripts/e2e-web.sh`, on `https://localhost:8443`). Tests: `dotnet build -warnaserror && dotnet test` (integration tests with Testcontainers: Docker must be running);
the Python package and the sample have tests that need no Docker (`cd clients/python && python -m pytest -q`); the Angular app has its own (`cd samples/notes-web && npx ng test --watch=false`).

## Run it for real

The image is public and the compose file is `deploy/docker-compose.prod.yml`: PostgreSQL pinned by digest, the service read-only with no capabilities, the
port on `127.0.0.1` only. A server with Docker, a domain and an SMTP relay is enough; the guide goes from nothing to a first sign-in,
with a `Caddyfile` for HTTPS, upgrading and troubleshooting:

- [`docs/deployment/vps.md`](docs/deployment/vps.md): the deployment guide.
- [`docs/operations/backup.md`](docs/operations/backup.md): what to back up, a nightly dump, a restore step by step, and the audit queries.
- [`docs/operations/key-rotation.md`](docs/operations/key-rotation.md): generating production keys, planned and emergency rotation (it signs everyone out).
- [`docs/security/threat-model.md`](docs/security/threat-model.md): STRIDE for each element, and the residual risks that are accepted.

## Connect a product

- A Python (FastAPI) backend: [`clients/python/`](clients/python/) (`auth-core-fastapi`, installed from the repository at a tag) and
  [`docs/integration/python-fastapi.md`](docs/integration/python-fastapi.md), with the sample product `samples/notes-api` as the worked example.
- An Angular frontend: [`docs/integration/angular.md`](docs/integration/angular.md), with `samples/notes-web`.

## What this is (and is not)

- **Internal tool, not a product for sale.** The repository is public as a portfolio piece; there is no support, docs site or marketing.
- **Consumers are unrelated projects.** No SSO and no shared users between them; one instance per consumer, with its own database, users and keys.
- **Headless REST API.** Each app owns its login screens; the service issues standard JWTs that any language can verify.
- **Never in scope:** SAML, SCIM, LDAP, multi-realm mode, fine-grained resource permissions.

## Stack

.NET 10 LTS, C# 14, ASP.NET Core minimal APIs, ASP.NET Core Identity, OpenIddict 7, EF Core 10, PostgreSQL 16, MailKit, YamlDotNet, Microsoft.AspNetCore.OpenApi, Scalar.AspNetCore
(Development only), xUnit v3 and Testcontainers. Samples: FastAPI, SQLAlchemy and Alembic, Angular 21, Caddy 2.

## Repository layout

```
src/Auth.Server/              ASP.NET Core host, endpoints, OpenIddict, the operator CLI
src/Auth.Infrastructure/      EF Core model and migrations, identity
tests/Auth.IntegrationTests/  WebApplicationFactory + Testcontainers
clients/python/               auth-core-fastapi: the package for FastAPI backends
samples/notes-api/            the "notes" product: FastAPI and PostgreSQL behind Caddy
samples/notes-web/            its Angular frontend
deploy/                       the development compose file, the production compose file, the manifest example
scripts/                      dev keys and the e2e checks of every slice
docs/                         design brief, ADRs, integration guides, deployment, operations, security, specs and plans
```

## Documentation

- [`docs/design.md`](docs/design.md): the MVP brief: goals, decisions, scope, architecture, contracts, timeline, risks.
- [`docs/adr/`](docs/adr/): architecture decision records.
- [`docs/workflow.md`](docs/workflow.md): how the work is done: the spec-driven flow, verification, branches.
- [`docs/superpowers/specs/`](docs/superpowers/specs/) and [`plans/`](docs/superpowers/plans/): the specs (0001 to 0008) and the plans and acceptance maps built from them.

## License

[MIT](LICENSE) © 2026 Aleksander Torka
````

- [ ] **Step 7: Check the documents and the README.** From the repository root, run the check of Task 13, Step 4 on all the documents of the release (its default list: `README.md`, `CHANGELOG.md`, `docs/deployment/vps.md`, the three documents of Task 13 and `clients/python/README.md`):

```bash
C:/p6v/Scripts/python.exe scripts/check-docs.py
```

  Expected: `all links, paths, files and tests resolve`. (`clients/python/README.md` links to GitHub by URL, so it has nothing to resolve; `docs/integration/angular.md` is slice 7's: its link in the README resolves because it is on the base, and slice 7's own `npm run check:docs` guards that guide.)

- [ ] **Step 8: The acceptance map.** `docs/superpowers/plans/0008-acceptance-map.md`:

````markdown
# Spec 0008 — acceptance map

Maps each acceptance criterion of [spec 0008](../specs/0008-hardening-and-release.md) to the tests and the e2e steps that guard it, for verifier layer 2
([`docs/workflow.md`](../../workflow.md#verification)). The .NET tests are in `tests/Auth.IntegrationTests/` (xUnit v3, PostgreSQL by Testcontainers: Docker must be running;
`dotnet test`); the package's tests are in `clients/python/tests/`, the Angular sample's in `samples/notes-web/src/` and `e2e/`. The live checks are
`scripts/e2e-hardening.sh` (the development stack with the **default** limits), `scripts/e2e-prod.sh` (the production compose file with a test overlay), the steps 7 of
`scripts/e2e-notes.sh` and `scripts/e2e-web.sh`.

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | 31 logins from one address in a minute: the 31st is `429 too_many_requests` with `Retry-After` and evaluates no password; the same for each policy at its own number | `RateLimitMiddlewareTests.The_request_over_the_limit_is_a_429_that_evaluates_no_password_and_changes_no_streak`, `.The_429_says_how_long_to_wait_on_the_clock`, `.A_request_over_the_limit_is_not_counted_and_the_window_slides`, `.Each_policy_refuses_at_its_own_number` (twelve endpoints), `.The_four_email_endpoints_share_one_limit_and_the_two_invitation_endpoints_another`, `.The_path_is_compared_without_regard_to_case_or_a_trailing_slash`; `SlidingWindowLimiterTests` (the window, the wait, no counting of refusals, partitions, sweeping, parallel callers); `RatePolicyTests`; `RateLimitSettingsTests`; `RateLimitDefaultsTests`; live: `e2e-hardening.sh` steps 2 and 3 |
| 2 | With no trusted proxy a forged `X-Forwarded-For` changes neither the partition nor the recorded address; with the proxy's network trusted two clients behind it are counted separately | `ProxySettingsTests`; `ClientAddressSetupTests` (nothing trusted by default, loopback not trusted, the last non-proxy address, every hop walked, `X-Forwarded-Host` never); `ClientAddressTests` (IPv4-mapped, `/64`, unknown); `RateLimitMiddlewareTests.Without_a_trusted_proxy_a_forwarded_for_header_changes_nothing`, `.Two_addresses_are_counted_apart`, `.An_ipv4_address_mapped_into_ipv6_is_the_same_address`, `.An_ipv6_address_is_counted_with_the_rest_of_its_64_bit_network`; `RateLimitProxyTests.Two_clients_behind_the_trusted_proxy_are_counted_separately`, `.What_the_client_wrote_into_the_header_does_not_choose_its_partition`, `.From_an_address_that_is_not_the_proxy_the_header_is_ignored`; `AuditClientAddressTests`, `AuditClientAddressBehindProxyTests`; live: `e2e-hardening.sh` step 2, `e2e-notes.sh` step 7, `e2e-prod.sh` step 4 |
| 3 | A refresh while PostgreSQL is stopped is `503 temporarily_unavailable` with `Retry-After: 5`; afterwards the same cookie refreshes | `RefreshOutageTests.A_refresh_while_the_database_is_down_is_a_503_and_the_same_cookie_refreshes_when_it_is_back`, `.A_cookie_the_service_cannot_look_up_is_a_503_not_a_401_so_that_the_person_stays_signed_in`, `.No_cookie_is_still_a_401_as_before_for_no_database_is_needed_to_see_it`; `SessionResponseHandlerTests` (the `server_error` branch and the others); `TransientFailureTests`; live: `e2e-hardening.sh` step 4 |
| 4 | Every pipeline answer (`200`, `401`, `404`, `405`, `415`, `429`, `500`, `503`) carries the headers and no `Server` header (the last on a live stack); the key set and the OpenAPI document carry no `Cache-Control`; an unhandled exception is `500 internal_error` | `SecurityHeadersTests.Every_answer_of_the_pipeline_has_the_headers_the_framework_404_and_405_included`, `.The_key_set_and_the_openapi_document_have_every_header_but_no_cache_control`, `.The_interactive_reference_gets_a_policy_of_its_own_with_the_nonce_of_its_script`; `SecurityHeadersOnRefusalTests` (`429`); `JsonCharsetTests` (`415`, via `AssertSecurityHeaders`); `ErrorHandlingTests` and `ErrorHandlingProductionTests` (`500`, logged, no detail); `RefreshOutageTests` (`503`, `500`); `HostHardeningTests` (`AddServerHeader`, Data Protection); live: `e2e-hardening.sh` step 1 and 4, `e2e-prod.sh` step 2 (no `Server` header) |
| 5 | `POST /auth/health` is `405` with `Allow: GET, HEAD`; `HEAD` is `200` | `HealthMethodsTests`; `OpenApiTests.Every_route_of_the_service_is_in_the_description` (HEAD under GET); live: `e2e-hardening.sh` step 1 |
| 6 | A JSON body declared `charset=utf-16` is `415 unsupported_media_type` on every endpoint that reads a JSON body; `utf-8` or no charset is read as before | `JsonCharsetTests.A_body_declared_utf_16_is_a_415_on_every_endpoint_that_reads_one_and_before_authentication` (thirteen endpoints), `.Any_charset_other_than_utf_8_is_refused_in_any_spelling`, `.A_body_with_utf_8_or_no_charset_is_read_as_before`, `.Refresh_and_logout_read_no_body_and_are_not_checked`, `.The_guard_applies_to_the_four_methods_under_auth_except_refresh_and_logout`; `OrgDeleteEndpointTests.A_content_type_that_is_not_json_is_400_and_one_that_is_not_utf_8_is_415`; live: `e2e-hardening.sh` step 1 |
| 7 | `x@127.0x1`, `x@host.123`, `x@0x7f.1` are `400 invalid_request` in the API and the CLI; `x@example.pl`, `x@żółw.pl`, `x@пример.рф`, `x@xn--e1afmkfd.xn--p1ai` pass | `InvitationDomainTests` (the rule, the API, the CLI), also run with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`; `InvitationAddressTests` (unchanged) |
| 8 | Each audit kind is written by its flow with the columns of the contract; no row holds a password, a token or a link; a failed change leaves no row | The 22 kinds: `login.*`, `logout`, `password.*`, `email.verified`, `rate_limit.hit`: `AuditAccountFlowsTests`, `AuditRateLimitTests`; `refresh.reuse_detected`: `AuditRefreshReuseTests`; `org.*`, `invite.*`, `member.*`, `role.*`: `AuditCompanyFlowsTests`, `AuditInvitationFlowsTests`, `AuditMemberFlowsTests`, `AuditRoleFlowsTests`, `CompanyDeletionTests.The_deletion_is_recorded_with_the_company_the_counts_and_the_operator_mark`, `OrgDeleteEndpointTests` (`org.delete_refused`); the table: `AuditTableTests`; the writer: `AuditLogTests`; no secrets: `AssertNoSecretsAsync` in the flow tests; a failed change: the "refused" tests of each flow, `AuditAtomicityTests` and the two login and logout tests of `AuditAccountFlowsTests` named in Review Focus 3; live: `e2e-hardening.sh` step 6 |
| 9 | Rows older than the retention are pruned; newer rows stay | `AuditPruningTests.Rows_older_than_the_retention_are_pruned_and_newer_ones_stay`, `.The_clock_decides_what_is_old`, `.The_retention_is_the_setting`, `.The_service_is_part_of_the_host_and_runs_every_hour`; `AuditSettingsTests` |
| 10 | `DELETE /auth/org` follows the table in its order; a wrong password counts in the lockout streak; afterwards the members' tokens get `403 permissions_changed`, their refresh is `401`, their login `403 no_membership`, the links are `invalid_token`, no queued mail is sent | `OrgDeleteEndpointTests` (every row of the table and the order: permission, body, lockout, password, name, rule 1; `.A_wrong_password_counts_in_the_login_streak_and_ten_of_them_lock_the_identifier`, `.Failed_logins_and_wrong_passwords_here_add_up_in_one_streak`, `.What_a_member_sees_afterwards_is_what_a_removed_member_sees`, `.Two_deletions_at_once_give_one_204_and_one_403_permissions_changed`); `CompanyDeletionTests` (the Restrict keys, the effects, the queued mail); `OrgDeletePermissionTests`; `OpenApiHardeningTests.The_company_deletion_is_described_with_its_body_and_its_answers`; live: `e2e-hardening.sh` step 5 |
| 11 | `delete-org` deletes with the exact name and refuses otherwise | `DeleteOrgCommandTests` |
| 12 | `docker-compose.prod.yml` starts from an empty volume with an image built locally under the GHCR name, migrates, serves login and refresh through a proxy, refuses to start without its secrets | `scripts/e2e-prod.sh` steps 1 to 4; the static checks of Task 9 (`docker compose config` with the example and with an empty environment; digests; labels) |
| 13 | The backup runbook, followed literally, restores a dropped database; a refresh cookie issued before the backup still refreshes | `scripts/e2e-prod.sh` step 5 (the commands of `docs/operations/backup.md`); the verifier follows the runbook by hand once |
| 14 | The Angular sample shows "Try again in N s." on `429 too_many_requests` and keeps the session on refresh `503` and `429` | `too-many-requests.spec.ts` (the notice from every kind of endpoint, the refresh answered `429` and `503`, a `401` whose refresh is `429` or `503`), `app.wait.spec.ts`, `login.too-many-requests.spec.ts` |
| 15 | The sample proxies send the headers of the contract; HSTS only over HTTPS | `e2e/08-proxy-headers.spec.ts`; `scripts/e2e-notes.sh` step 7; `caddy validate` of both Caddyfiles |
| 16 | All earlier e2e scripts, the .NET tests, the package and sample tests pass; `e2e-hardening.sh` covers 1, 3, 4, 5, 6, 8 and 10 on a live stack | `dotnet build -warnaserror && dotnet test`; the Python and Angular suites; the five older scripts (limiter off), `e2e-notes.sh`, `e2e-web.sh`, `e2e-hardening.sh`, `e2e-prod.sh` |
| 17 | A secret scan of the whole history and a known-vulnerability check of the .NET, npm and Python dependencies find nothing unaddressed | `scripts/secret-scan.sh` (gitleaks v8.30.1 with `.gitleaksignore`); `dotnet list package --vulnerable --include-transitive`; `npm audit` (the known advisories of Angular 21.1.4, spec 0007 Decision 13, addressed by reference; nothing else); `pip-audit` (the commands and the expected output are in "Verification and release" of the plan) |

## Contract sentences that are not acceptance criteria

| Sentence of the contract | Guard |
| --- | --- |
| Nothing is trusted by default; the framework's loopback default is never used | `ClientAddressSetupTests.With_no_proxy_configured_the_framework_default_of_trusting_loopback_is_not_used`, `.A_configured_network_does_not_trust_loopback_as_the_framework_would` |
| A blank setting means the default; an invalid one stops the host naming the key | `RateLimitSettingsTests`, `AuditSettingsTests`, `ProxySettingsTests`, `RateLimitDefaultsTests` |
| The counters live in memory and idle partitions are forgotten | `SlidingWindowLimiterTests.Idle_partitions_are_forgotten`, `.A_partition_that_is_still_in_use_is_not_forgotten` |
| `GET /auth/health` and the key set are under `general` | `RateLimitMiddlewareTests.Each_policy_refuses_at_its_own_number` (rows for both) |
| The lockout and the mail limits stay; a request must pass both | the unedited `LockoutTests`, `MailRequestEndpointTests`, `LoginTimingTests` |
| Login, logout and the company API keep a `500` for an outage, written by the handler | `RefreshOutageTests.Login_logout_and_the_company_api_keep_a_500_for_an_outage_now_written_by_the_handler` |
| `/auth/health` does not reach the database | `RefreshOutageTests.The_health_check_does_not_reach_the_database` |
| The Scalar policy, its nonce, no default fonts, telemetry or agent | `SecurityHeadersTests.The_interactive_reference_gets_a_policy_of_its_own_with_the_nonce_of_its_script`; `e2e-hardening.sh` step 1 |
| The second development seed user's role is read from the database | `DevCompanySeedTests.Second_seed_user_gets_the_first_role_by_name_that_holds_neither_members_manage_nor_star`, `.Second_seed_user_takes_a_role_the_company_has_not_one_the_manifest_names`, `.A_role_that_holds_members_manage_or_star_is_never_the_second_users` |
| `org:delete` is in the catalog whether or not the manifest lists it; `*` holds it | `OrgDeletePermissionTests`; the catalog lists of `PermissionCatalogTests`, `OrgRoleTests`, `TenantClaimsTests`, `OrgEndpointTests`, `ManifestParserTests`, `InviteAcceptTests`; `e2e-tenancy.sh` |
| The OpenAPI description: `DELETE /auth/org`, `429` with `Retry-After` on every operation, `415`, `503` on refresh, `org:delete` | `OpenApiHardeningTests`, `OpenApiTests` |
| The Python package: version `0.1.1`, MIT, the licence file and a readme | `clients/python/tests/test_metadata.py` |
| `rate_limit.hit` at most once per address and policy per minute, and its write never fails the request | `RateLimitAuditTests`, `AuditRateLimitTests.A_refusal_is_recorded_once_per_address_and_policy_per_minute`, `.A_failing_audit_write_never_fails_the_request` |
| The image: labels from build arguments, digest-pinned base images, the development images pinned | Task 9, steps 5 and 6 (the labels read back from the built image) |
| Annotated tags `v0.1.0` and `python-v0.1.1` on the merge commit; the image pushed as `0.1.0` and `latest`, public | the release steps of the plan's "Verification and release" (owner-gated) |

## Review Focus (plan 0008)

| Line | Pinned by |
| --- | --- |
| 1. An address behind a proxy in the other spellings: IPv4-mapped IPv6, IPv6 by its `/64`, a forged header from an untrusted sender, a header with every hop trusted, a list with a blank entry | `ClientAddressTests`, `ClientAddressSetupTests`, `ProxySettingsTests.Blank_entries_count_as_unset`, `RateLimitMiddlewareTests` (Task 1) |
| 2. A flood must do no other work and the limiter's memory must follow the traffic | `RateLimitMiddlewareTests.The_request_over_the_limit_is_a_429_that_evaluates_no_password_and_changes_no_streak`, `SlidingWindowLimiterTests.Idle_partitions_are_forgotten`, `.Requests_that_arrive_together_cannot_pass_the_limit` (Task 1) |
| 3. A failing audit write must not fail a refusal (a failed login, a rate-limit hit) and must roll a change back, a session issued or ended included | `AuditRateLimitTests.A_failing_audit_write_never_fails_the_request`, `AuditLogTests.A_write_on_its_own_that_fails_is_logged_and_never_thrown_and_is_not_retried`, `.A_write_on_its_own_never_saves_what_the_callers_context_is_tracking`, `.A_failed_write_on_its_own_leaves_the_callers_pending_change_alone`, `AuditAccountFlowsTests.A_login_whose_row_cannot_be_written_issues_no_session`, `.A_logout_whose_row_cannot_be_written_ends_nothing`, `AuditAtomicityTests` (Tasks 4 to 6) |
| 4. The deletion of a company in the orders and races a person would meet: a wrong name with the right password, a locked identifier, two deletions at once, a member with more rights | `OrgDeleteEndpointTests.The_password_is_checked_before_the_name_and_a_wrong_name_with_the_right_password_is_400`, `.The_lockout_comes_before_the_password_and_a_malformed_body_before_the_lockout`, `.Two_deletions_at_once_give_one_204_and_one_403_permissions_changed`, `.A_member_of_the_company_who_holds_a_permission_the_caller_does_not_makes_it_403_permission_not_held` (Task 8) |
| 5. A setting that compose passes empty, or that is wrong, in Production | `RateLimitSettingsTests.A_blank_value_means_the_default`, `ProxySettingsTests.A_bad_entry_stops_the_host_naming_the_key_and_not_the_value`, `RateLimitDefaultsTests`, the compose static checks (Task 9) and `e2e-prod.sh` step 1 (Tasks 1, 9, 12) |

## How the tests run

`dotnet build -warnaserror && dotnet test` runs the whole .NET suite (Docker running: one PostgreSQL container for the assembly). The tests that need a running service
use `WebApplicationFactory` with a fake clock; `RefreshOutageTests` and its helper `DatabaseOutage` stop and start a TCP forwarder in front of the
test PostgreSQL, so no other test is disturbed. The Python tests: `cd clients/python && python -m pytest -q`; the sample's: `cd samples/notes-api && python -m pytest -q`.
The Angular tests: `cd samples/notes-web && npx ng test --watch=false`. The live checks need the stack's ports and are run by one verifier at a time.

## Plan-vs-implementation notes

(The orchestrator writes this section after the verification: what the plan said and what was built, and why they differ.)

## Local verification log

(The orchestrator writes this section: which command or script ran, when, with what result, for each verifier and each round.)
````

- [ ] **Step 9: Final checks of the task.**

```bash
cd clients/python && C:/p6v/Scripts/python.exe -m pytest -q
cd ../.. && C:/p6v/Scripts/python.exe scripts/check-docs.py docs/superpowers/plans/0008-acceptance-map.md
scripts/secret-scan.sh
git status --short
```

  Expected: the package tests pass, **except that `test_a_wheel_carries_the_licence_the_readme_and_the_version` is reported as skipped on this machine**: it builds a wheel with `hatchling`, and the virtual environment `C:/p6v` does not have it (`pytest.importorskip`). Do not install anything: report the skip as "not run" in the hand-back. The orchestrator installs `hatchling==1.32.4` into `C:/p6v` for the verifier (it is the version the package's `[build-system]` pins and the owner approved in slice 6; no new package) and the verifier runs the test and expects it to pass (see "Verification and release"). The check of the acceptance map says `all links, paths, files and tests resolve`: every test the map names exists in the code of Tasks 1 to 14 (a name that was changed while building is changed in the map). `scripts/secret-scan.sh` says `PASS` twice. `git status` shows exactly the files of this task (the README, the changelog, the guide, `.gitleaksignore`, `scripts/secret-scan.sh`, the package files, the guide and requirements edits, the map).

- [ ] **Step 10: Hand back** — uncommitted. Files: everything listed under Files. Proposed subjects (the orchestrator may split them): `docs: deployment guide, README rewrite and changelog for v0.1.0`; `chore(python): package 0.1.1 with its licence and readme`; `chore: secret scan script and the gitleaks ignore list for the eight known false positives` (the orchestrator makes `scripts/secret-scan.sh` executable in the index); `docs: acceptance map for spec 0008`.


## Day 5 — Tuesday 24.02 (before verification): a database role of its own

Taken after Task 13, on the owner's decision of 2026-02-23 (spec 0008, "Production compose" → "Two database roles", criterion 12's last clause, Decision 14): Auth-Core must not connect to PostgreSQL as the superuser. One task; it changes the production compose file, the scripts and the documents that Tasks 9, 12, 13 and 14 made (and aligns what they say about a proxy in a container with the guide of Task 14), and it runs **before** the verification below, because the gate, the verifiers and the live checks all read its result. It changes no .NET code and no test of the .NET suite.

### Task 15: A database role of its own for Auth-Core in production

**Base.** HEAD `87502d8` holds Tasks 1 to 14 as built (review fixes included), and the spec as amended. They differ from the plan text in places, so the files below were read as they are now, and the edits to the documents are given as anchors (a line or a row to find, and what replaces it); if an anchor is not in the file, stop and report which one to the orchestrator, and do not improvise a place.

**Files:**
- Create: `deploy/postgres-init/10-auth-app-role.sh`
- Modify: `deploy/docker-compose.prod.yml`, `deploy/.env.prod.example`, `scripts/prod-test.compose.yml`, `scripts/e2e-prod.sh`
- Modify (documents of Tasks 13 and 14): `docs/operations/backup.md`, `docs/operations/key-rotation.md`, `docs/security/threat-model.md`, `docs/deployment/vps.md`, `CHANGELOG.md`, `docs/superpowers/plans/0008-acceptance-map.md`
- No change: `deploy/docker-compose.yml` and `.env.example` (the development compose is unchanged, as the spec says), every file under `src/` and `tests/`.

**Interfaces:**
- Consumes: Task 9's `deploy/docker-compose.prod.yml` (services `postgres` and `auth`, the long bind syntax with `create_host_path: false`, `${NAME:?...}` for secrets) and `deploy/.env.prod.example`; Task 12's `scripts/e2e-prod.sh` (the helpers `fail`, `pass`, `expect_eq`, `psql_value`, `direct`, `call`, `json_body`, `cli`, the array `compose`, the variables `run`, `tmp`, `backup`, the deadlines of the readiness loops); Task 13's runbooks and the threat model; the OpenIddict, Identity and application tables that `AddAuditEvents` and the four migrations before it make in the schema `public`.
- Produces:
  - Two variables of the environment file: `AUTH_DB_APP_USER` (the name of the role; not a secret; blank or absent means `auth_app`; lower-case letters, digits and underscores, and not an SQL keyword) and `AUTH_DB_APP_PASSWORD` (a secret; **no default**: `${AUTH_DB_APP_PASSWORD:?...}`, so that `docker compose` refuses to start without it; letters and digits, at least 16).
  - `deploy/postgres-init/10-auth-app-role.sh`, mounted read-only into `/docker-entrypoint-initdb.d/` of the `postgres` service: it runs once, when the volume is first made, and creates the role and gives it the database.
  - `ConnectionStrings__Auth` of the `auth` service with `Username=${AUTH_DB_APP_USER:-auth_app}` and the new password. The superuser `auth` (`POSTGRES_USER`, `POSTGRES_PASSWORD`) is used by the operator only.
  - The restore of `docs/operations/backup.md` in its new form (the commands below are literal; `scripts/e2e-prod.sh` step 5 runs the same ones): `pg_restore ... --no-owner --role="$AUTH_DB_APP_USER"`, a database created `OWNER $AUTH_DB_APP_USER`, `CONNECT` revoked from PUBLIC again.
  - `scripts/e2e-prod.sh`: the function `check_app_role <when>`, called after the first start (step 2) and after the restore (step 5), and a check in step 1 that the file refuses to render without the new password.
  - A proxy in a container is trusted by its own fixed address, never by its subnet (the pattern of `docs/deployment/vps.md`, "A proxy in a container", settled in Task 14's review): `AUTH_PROXY_KNOWN_PROXIES=<address>`, `AUTH_PROXY_KNOWN_NETWORKS` blank. The test proxy of `scripts/prod-test.compose.yml` gets `ipv4_address: ${E2E_PROD_PROXY_IP}` (`10.250.1.10`, inside the proxy subnet and not its gateway), and `scripts/e2e-prod.sh` trusts that one address, so that step 4 tests the pattern the guide recommends and step 6 tests that the host's gateway is not believed.

**What was probed before this task was written** (throwaway `docker run` of `postgres:16-alpine@sha256:7218...` with no published port, on a private network; every container, network and volume removed afterwards; nothing of the repository was started):
- The init script, as a bind mount of an executable file (the entrypoint logs `running ...`) and as a file of mode 0644 put into the directory with `docker cp` (it logs `sourcing ...`): both create the role with `rolsuper`, `rolcreaterole`, `rolcreatedb`, `rolreplication`, `rolbypassrls` all `f` and `rolcanlogin` `t`; the owner of the database `auth` is the role; `has_database_privilege('pg_monitor', 'auth', 'CONNECT')` is `f` (PUBLIC may not connect); the role cannot connect to `postgres` or `template1` (`permission denied for database`); from another container over the network the role's password is accepted and a wrong one is `password authentication failed`.
- Every value that does not fit stops the initialisation with exit code 3 and a message that does not hold the password: a role name with an upper-case letter or a hyphen, the superuser's own name, a password of fewer than 16 characters, a password with a semicolon, the `CHANGEME...` placeholder, a reserved word as the name (`user`, `select`, `table`, `all`: refused; `auth_app`, `auth_app_prodtest` and `_x` are accepted, and `CREATE DATABASE auth OWNER _x` then works). The log of the container never held the password; the control (the same script without its two `SET` lines, run against an existing role) **did** write it, so the `SET log_statement` and `SET log_min_error_statement` lines are what keeps it out.
- **A failed initialisation is not retried.** After the script failed, `docker start` of the same container came up as a running database with no role: the entrypoint treats a data directory that exists as initialised. The compose file has `restart: unless-stopped`, so the same happens by itself. The documents say what to do (remove the volume, which holds nothing yet, correct `.env`, start again).
- All five migrations (`dotnet ef migrations script --idempotent`, 625 lines) ran as the role over TCP with its password, with `ON_ERROR_STOP`, and exited 0: 22 tables, 3 sequences and 53 indexes in `public`, every one owned by the role. The migrations hold no `CREATE EXTENSION`, no `CREATE ROLE`, no `GRANT`, no `SECURITY DEFINER` and nothing else that needs a superuser (`grep` of `src/Auth.Infrastructure/Persistence/Migrations/`); the application's raw SQL is `SELECT`, `INSERT`, `DELETE` and `FOR UPDATE` on its own tables. So **no .NET code assumes a superuser**. The role's own refusals: `COPY (SELECT 1) TO PROGRAM 'true'` is `permission denied to COPY to or from an external program`, `CREATE ROLE` and `CREATE DATABASE` are `permission denied`.
- The schema `public` needs no `ALTER SCHEMA ... OWNER`: since PostgreSQL 15 it belongs to the built-in role `pg_database_owner`, which is whoever owns the database, so the role that owns the database owns it (`has_schema_privilege(role, 'public', 'CREATE')` is `t`). A database made by `CREATE DATABASE auth OWNER <role>` after a drop is the same.
- The restore: a dump taken as the superuser (`pg_dump -U auth -d auth --format=custom --no-owner`, 78 objects), the database dropped, `CREATE DATABASE auth OWNER <role>`, `REVOKE CONNECT ... FROM PUBLIC`, then `pg_restore -U auth -d auth --no-owner --role=<role> --exit-on-error` exited 0 and every restored object (22 tables, 3 sequences, 53 indexes) is owned by the role, the data is there, and the role can write to it. **Without `--role`** the same restore exits 0 too, and every object is owned by the superuser `auth`: Auth-Core would then fail at its next migration, and could not write the tables at all. Restoring as the role itself (`-U <role>`) also gives the right owners, but only because the local socket of the image trusts every user; `--role` keeps the operator's identity and needs no password. A later `REASSIGN OWNED BY auth TO <role>` would also move whatever else the superuser owns: not used.
- `psql -c '\password <role>'` works through `docker exec ... sh -c 'psql ... -c "\password $AUTH_DB_APP_USER"'`; answering with an empty password **clears** the password (the notice says so): the documents say so.
- In the image, `pg_hba.conf` trusts the local socket and the loopback addresses for every role and asks for `scram-sha-256` from every other address. So the password protects the path from the `auth` container (and from anything else on its network) and not a process inside the `postgres` container, which is part of the trusted base either way; `docker compose exec postgres psql -U auth_app` needs no password.

**Decisions of this task** (the spec leaves them open; each is argued).
- **The role is made by a script in `/docker-entrypoint-initdb.d/`**, not by a one-shot service, not by Auth-Core. The image's entrypoint runs that directory exactly when the volume is first initialised (the spec's wording), as the superuser, on the socket, before the database accepts connections, so nothing can connect before the role exists and nothing runs again on a later start. A one-shot job (`docker compose run`, a second service) would run on every `up` and need the superuser's password in a second place; Auth-Core creating its own role would need the superuser. A `.sql` file cannot read the environment, so the file is a `.sh` whose only work is one `psql` call that reads both values with `\getenv`: the password is on no command line and in no process list.
- **The script checks its inputs and fails closed**, and says nothing of the password. The name is checked against `^[a-z_][a-z0-9_]{0,62}$`, must differ from the superuser, not start with `pg_` and need no quoting (`quote_ident(name) = name`, which refuses reserved words such as `user`, `select`, `table` and `all`), so that the unquoted use in the runbook's commands (`CREATE DATABASE auth OWNER $AUTH_DB_APP_USER`) is safe; the password is letters and digits, at least 16 (it is part of a connection string, like `POSTGRES_PASSWORD`) and not the `CHANGEME...` placeholder of the example file. The statement that carries the password is run with statement logging off for the session.
- **The role owns the database and nothing more.** `ALTER DATABASE ... OWNER TO` gives the migrations what they need (see the probe: the schema follows); `CONNECT` is revoked from PUBLIC on `auth`, `postgres` and `template1`, so the role connects to its own database only (the notes sample's job does the same for its login). The superuser is not affected.
- **The restore runs as the superuser with `--role`**, over restoring as the role or reassigning afterwards: it keeps the operator's identity, needs no second password and no trust in `pg_hba.conf`, changes only the objects the restore creates, and was probed to give the right owners; without it the restore silently gives every object to the superuser (probed above), so the flag is part of the literal command and a check in `scripts/e2e-prod.sh` guards it. The recreated database also gets `OWNER $AUTH_DB_APP_USER` and the `REVOKE CONNECT` again, because a database made by `CREATE DATABASE` starts with PUBLIC allowed to connect. The commands run in the container through `sh -c '...'`, so that `$AUTH_DB_APP_USER` is the container's value, the same as the one the init script used, and a custom name needs no edit of the runbook.
- **There is no migration path for an existing volume.** `v0.1.0` is the first release; the volumes that exist are development and test stacks that run the development compose, which is unchanged, and throwaway production tests. A volume made before this task has no role and the script does not run on it: the guide says to start from an empty volume.
- **A containerised proxy is trusted by its own address** (Task 14's review). Trusting the whole proxy subnet also trusts its gateway, the address a process on the host arrives from, which would let that process choose the address recorded for its own requests (threat model, item 43). Five places still advised or tested the subnet: the comments in `deploy/docker-compose.prod.yml` (two) and `deploy/.env.prod.example`, the overlay's header, and `scripts/e2e-prod.sh` (`AUTH_PROXY_KNOWN_NETWORKS=10.250.1.0/24`). They are aligned here. An explicit `AUTH_PROXY_KNOWN_PROXIES` **replaces** the default (both gateways), so the test stack no longer trusts a host proxy: step 6 still checks what the Docker engine does (which gateway the host arrives from, for the default that a host proxy needs), and now also that a forged `X-Forwarded-For` from the host is not believed.

- [ ] **Step 1: The checks that fail today.** The new e2e checks need a stack and are run by the verifiers; what can be seen to fail now without one is the compose file. From the repository root:

```bash
tmp="$(mktemp -d)"
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env.prod.example config > /dev/null && echo "renders with the example"
grep -v '^AUTH_DB_APP_PASSWORD=' deploy/.env.prod.example > "$tmp/no-app-password.env"
docker compose -f deploy/docker-compose.prod.yml --env-file "$tmp/no-app-password.env" config > /dev/null 2>&1 && echo "renders WITHOUT the app password (wrong)"
ls deploy/postgres-init 2>&1 | head -n 1
grep -c "AUTH_DB_APP" deploy/docker-compose.prod.yml
rm -rf "$tmp"
```

  Expected now: `renders with the example`, `renders WITHOUT the app password (wrong)`, `ls: cannot access 'deploy/postgres-init': No such file or directory`, `0`.

- [ ] **Step 2: The init script.** `deploy/postgres-init/10-auth-app-role.sh` (LF; the orchestrator makes it executable in the index; it works executable or not, see its header):

```sh
#!/bin/sh
# Creates the database role that Auth-Core connects as (spec 0008, Decision 14). The image of PostgreSQL runs this once, when the volume
# is first initialised, as the superuser POSTGRES_USER and against POSTGRES_DB; on a volume that already holds a database it does not run.
#
# The role may log in and nothing else: no SUPERUSER, CREATEROLE, CREATEDB, REPLICATION or BYPASSRLS. It owns the database, so the
# migrations that Auth-Core runs at its start can make the tables, and it is the only role besides the superuser that may connect to it
# (nor may it connect to the maintenance databases).
# The superuser stays for the operator (backup, restore, password changes).
#
# The name comes from AUTH_DB_APP_USER (lower-case letters, digits and underscores, not an SQL keyword) and the password from AUTH_DB_APP_PASSWORD (letters and
# digits, at least 16, and not the placeholder of .env.prod.example: it is part of a connection string); both reach this container from the compose file. A value that does not fit stops
# the initialisation, and the message never holds the password. The password is read from the environment by psql itself, so it is on no
# command line, and statement logging is switched off for this session so that not even a failing statement writes it to the log of the
# container.
#
# The file works whether the image executes it (it is executable) or reads it into its own shell (it is not): it has no `exit`, and
# the status of its last command is the status of the script.
psql -v ON_ERROR_STOP=1 --no-psqlrc --quiet --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
SET log_statement = 'none';
SET log_min_error_statement = 'panic';

\getenv app_user AUTH_DB_APP_USER
\getenv app_pw AUTH_DB_APP_PASSWORD
SELECT current_database() AS db, current_user AS superuser \gset

SELECT (:'app_user' ~ '^[a-z_][a-z0-9_]{0,62}$'
        AND :'app_user' <> :'superuser'
        AND :'app_user' NOT LIKE 'pg\_%'
        AND quote_ident(:'app_user') = :'app_user'
        AND :'app_pw' ~ '^[A-Za-z0-9]{16,}$'
        AND :'app_pw' !~ '^CHANGEME') AS fits \gset
\if :fits
\else
  DO $$ BEGIN
    RAISE EXCEPTION 'AUTH_DB_APP_USER must be lower-case letters, digits and underscores, starting with a letter or an underscore, not the superuser, not starting with pg_ and not an SQL keyword, and AUTH_DB_APP_PASSWORD must be at least 16 letters and digits and not the placeholder of .env.prod.example';
  END $$;
\endif

SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEROLE NOCREATEDB NOREPLICATION NOBYPASSRLS PASSWORD %L', :'app_user', :'app_pw') \gexec

ALTER DATABASE :"db" OWNER TO :"app_user";
-- Only the superuser and the owner may connect to the database; the role cannot even connect to the two maintenance databases.
REVOKE CONNECT ON DATABASE :"db" FROM PUBLIC;
REVOKE CONNECT ON DATABASE postgres FROM PUBLIC;
REVOKE CONNECT ON DATABASE template1 FROM PUBLIC;
SQL
```

- [ ] **Step 3: The compose file.** `deploy/docker-compose.prod.yml`, two edits by anchor (the rest of the file stays):

  1. In the `postgres` service, the two lines `POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?set POSTGRES_PASSWORD in .env}` and, under `volumes:`, `- postgres-data:/var/lib/postgresql/data`, become:

```yaml
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?set POSTGRES_PASSWORD in .env}
      # The role Auth-Core connects as (spec 0008, Decision 14): made by postgres-init/10-auth-app-role.sh when the volume is first
      # initialised, from these two values. The image's superuser above is for the operator only (backup, restore, password changes).
      AUTH_DB_APP_USER: ${AUTH_DB_APP_USER:-auth_app}
      AUTH_DB_APP_PASSWORD: ${AUTH_DB_APP_PASSWORD:?set AUTH_DB_APP_PASSWORD in .env (letters and digits, at least 16)}
    volumes:
      - postgres-data:/var/lib/postgresql/data
      # Run once, on an empty volume, by the image's entrypoint. The long syntax with create_host_path: false, so that a missing file
      # stops the start instead of becoming an empty directory (and a database without the role).
      - type: bind
        source: ./postgres-init/10-auth-app-role.sh
        target: /docker-entrypoint-initdb.d/10-auth-app-role.sh
        read_only: true
        bind:
          create_host_path: false
```

  (`./postgres-init/...` is relative to the directory of the compose file, `deploy/`, whichever directory the command runs from.)

  2. In the `auth` service, the line `ConnectionStrings__Auth: Host=postgres;Port=5432;Database=auth;Username=auth;Password=${POSTGRES_PASSWORD:?set POSTGRES_PASSWORD in .env}` becomes:

```yaml
      ConnectionStrings__Auth: Host=postgres;Port=5432;Database=auth;Username=${AUTH_DB_APP_USER:-auth_app};Password=${AUTH_DB_APP_PASSWORD:?set AUTH_DB_APP_PASSWORD in .env (letters and digits, at least 16)}
```

  Probed: with these edits `docker compose config` renders the connection string with the role, the mount with `create_host_path: false`, and without `AUTH_DB_APP_PASSWORD` it refuses and names it twice (the `auth` service and the `postgres` service). A blank `AUTH_DB_APP_PASSWORD=` is refused too (`:?`).

- [ ] **Step 4: The environment example.** `deploy/.env.prod.example`: the block "The database" is replaced by (the rest stays):

```bash
# ---- The database ------------------------------------------------------------------------------------------------------
# PostgreSQL has two roles here. The superuser "auth" is the operator's (backup, restore, password changes: docs/operations/): its password is
# POSTGRES_PASSWORD. CHANGE: a long random value, letters and digits only. Required; it is used once, when the volume is first made, so
# changing it later also means changing it inside PostgreSQL (docs/operations/key-rotation.md). Auth-Core does not connect as it.
POSTGRES_PASSWORD=CHANGEMEaLongRandomValue0123456789
# Auth-Core's own role, the only one it connects as: not a superuser, owner of the database "auth" and of its tables. It is created when the
# volume is first made (deploy/postgres-init/10-auth-app-role.sh) from these two values, and not again: on a volume that already exists, change
# the password inside PostgreSQL too. The name: lower-case letters, digits and underscores, not an SQL keyword (blank means auth_app). The password: CHANGE, a long
# random value of letters and digits, at least 16 (it is part of a connection string); the script refuses the placeholder. Required.
AUTH_DB_APP_USER=auth_app
AUTH_DB_APP_PASSWORD=CHANGEMEanotherLongRandomValue0123456789
```

- [ ] **Step 4b: The proxy in a container is trusted by its own address.** Three files, by anchor (the rest of each stays).

  1. `deploy/docker-compose.prod.yml`, in the `auth` service, the two comment lines `# default subnets below; change them with AUTH_SUBNET and AUTH_PROXY_SUBNET). A proxy in a container joins the "proxy" network,` and `# whose subnet can be trusted instead. All are written plainly (IPv4 as four decimal numbers, a network as its base address and` become:

```yaml
      # default subnets below; change them with AUTH_SUBNET and AUTH_PROXY_SUBNET). A proxy in a container joins the "proxy" network with
      # a fixed address of its own, and that one address is trusted instead (AUTH_PROXY_KNOWN_PROXIES=<address>: an explicit value replaces
      # the default; docs/deployment/vps.md, "A proxy in a container"), never the whole subnet, whose gateway is where a process on this host
      # arrives from. All are written plainly (IPv4 as four decimal numbers, a network as its base address and
```

  and, under `networks:`, the comment line `# service as auth:8080. Set AUTH_PROXY_KNOWN_NETWORKS to AUTH_PROXY_SUBNET then. Nothing joins it otherwise.` becomes `# service as auth:8080. Give it a fixed address in AUTH_PROXY_SUBNET and trust that address in AUTH_PROXY_KNOWN_PROXIES, not the subnet. Nothing joins it otherwise.`

  2. `deploy/.env.prod.example`, the seven lines from `# Write AUTH_PROXY_KNOWN_PROXIES= (empty) to trust no host proxy;` to `AUTH_PROXY_KNOWN_NETWORKS=` become:

```bash
# Write AUTH_PROXY_KNOWN_PROXIES= (empty) to trust no host proxy; leaving the variable out keeps the default (both gateways). An explicit
# value replaces the default. This differs from the rate-limit settings below, where blank means the default.
AUTH_PROXY_KNOWN_PROXIES=10.250.0.1,10.250.1.1
# For a proxy that runs in a container: the network it joins and its subnet. Give the proxy a FIXED address in that subnet (not the gateway,
# the first address; docs/deployment/vps.md, "A proxy in a container") and trust that one address: AUTH_PROXY_KNOWN_PROXIES=<that address>
# (it replaces the default, so the two gateways are no longer trusted). Do not trust the whole subnet: it includes the gateway, from which
# a process on this host reaches the published port, and such a process could then choose the address recorded for its own requests.
# AUTH_PROXY_KNOWN_NETWORKS is for a network whose every address you control: leave it blank here.
AUTH_PROXY_NETWORK=auth-core-proxy
AUTH_PROXY_SUBNET=10.250.1.0/24
AUTH_PROXY_KNOWN_NETWORKS=
```

  3. `scripts/prod-test.compose.yml`, the two comment lines `# and a reverse proxy on https://localhost:8443 (Caddy, `tls internal`: a browser warns, the script accepts it). The proxy is on the "proxy" network of the` and `# production file, whose subnet the script tells Auth-Core to trust for the client address.` become:

```yaml
# and a reverse proxy on https://localhost:8443 (Caddy, `tls internal`: a browser warns, the script accepts it). The proxy is on the "proxy" network of the
# production file with a fixed address (E2E_PROD_PROXY_IP), the one address the script tells Auth-Core to trust for the client address, as
# docs/deployment/vps.md recommends for a proxy in a container (never the whole subnet: it includes the gateway).
```

  and the `caddy` service's lines `    networks:` and `      - proxy` become:

```yaml
    networks:
      proxy:
        # A fixed address (not the gateway, the first address): the one address Auth-Core trusts for the client address.
        ipv4_address: ${E2E_PROD_PROXY_IP:?set by scripts/e2e-prod.sh}
```

  Probed: the production file and the overlay render together with `E2E_PROD_PROXY_IP=10.250.1.10` and `AUTH_PROXY_KNOWN_PROXIES=10.250.1.10`: the caddy service shows `ipv4_address: 10.250.1.10` under the `proxy` network and the `auth` service `Auth__Proxy__KnownProxies__0: 10.250.1.10` and `Auth__Proxy__KnownNetworks__0: ""`.

- [ ] **Step 5: `scripts/e2e-prod.sh`.** The checks. Each edit is by anchor in the file as it is now; the rest stays.

  1. **The header.** In the list of steps, the line `#      is answered (the database is reached and migrated: /auth/health does not say so).` of step 2 becomes two lines:

```bash
#      is answered (the database is reached and migrated: /auth/health does not say so); Auth-Core's database role is not a superuser,
#      owns the database and every table, cannot connect to the maintenance database, run a program or make a role (Decision 14).
```

  The line `#      service started again and waited for with the runbook's login (not /auth/health); the refresh cookie issued BEFORE the backup still refreshes and the company is still there.` of step 5 becomes:

```bash
#      service started again and waited for with the runbook's login (not /auth/health); the restored objects belong to Auth-Core's role
#      again (pg_restore --role), the role still has every property of step 2, a login and the refresh cookie issued BEFORE the backup work,
#      and the company is still there.
```

  And the line `#      is not there and with a mail relay without TLS.` of step 1 becomes `#      is not there and with a mail relay without TLS; the file does not render without the password of Auth-Core's database role.`

  2. **The environment file.** After the line `POSTGRES_PASSWORD="$(openssl rand -hex 16)"` add:

```bash
APP_DB_PASSWORD="$(openssl rand -hex 16)"
# Auth-Core's database role: a name of its own (not the default), so that the variable is exercised by the compose file, the init script and the commands of the runbook.
APP_ROLE="auth_app_prodtest"
```

  and in the here-document of `prod.env`, after the line `POSTGRES_PASSWORD=$POSTGRES_PASSWORD`, add `AUTH_DB_APP_USER=$APP_ROLE` and `AUTH_DB_APP_PASSWORD=$APP_DB_PASSWORD`. (The loop that unsets the variables of the compose file in the shell reads the names from `deploy/.env.prod.example` and from `prod.env`, so the new ones are covered; the password is never printed.)

  3. **The function.** Before the line `run="$RANDOM$RANDOM"`, add:

```bash
# check_app_role <when>: Auth-Core's database role (spec 0008, Decision 14). It is not a superuser and has none of the other privileges that
# matter; it owns the database and every table, sequence and index of the schema public; PUBLIC may not connect to the database; the role
# itself may not connect to the maintenance database, run a program from the database or make a role. Read as the superuser, from inside the
# postgres container (the operator's way: the local socket needs no password). Called after the first start, where the service has migrated
# the database as the role, and again after the restore.
check_app_role() {
  local when="$1" out object
  out="$(psql_value "SELECT rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls, rolcanlogin FROM pg_roles WHERE rolname = '$APP_ROLE'")" \
    || fail "$when: the database could not be queried"
  expect_eq "$when: rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls and rolcanlogin of $APP_ROLE" "$out" "f|f|f|f|f|t"
  out="$(psql_value "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'auth'")" || fail "$when: the database could not be queried"
  expect_eq "$when: the owner of the database auth" "$out" "$APP_ROLE"
  out="$(psql_value "SELECT count(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind = 'r'")" || fail "$when: the database could not be queried"
  [[ "$out" =~ ^[0-9]+$ ]] && (( out >= 10 )) || fail "$when: the schema public holds '$out' tables: the database is not migrated"
  out="$(psql_value "SELECT count(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind IN ('r', 'S', 'i', 'v', 'm', 'p', 'f') AND pg_get_userbyid(relowner) <> '$APP_ROLE'")" \
    || fail "$when: the database could not be queried"
  expect_eq "$when: tables, sequences and indexes of the schema public that $APP_ROLE does not own" "$out" "0"
  for object in '"AspNetUsers"' '"OpenIddictTokens"' '"Companies"' audit_events; do
    out="$(psql_value "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relname = '${object//\"/}'")" \
      || fail "$when: the database could not be queried"
    expect_eq "$when: the owner of $object" "$out" "$APP_ROLE"
  done
  out="$(psql_value "SELECT has_database_privilege('pg_monitor', 'auth', 'CONNECT')")" || fail "$when: the database could not be queried"
  expect_eq "$when: PUBLIC may connect to the database auth" "$out" "f"
  if timeout 120 "${compose[@]}" exec -T postgres psql -U "$APP_ROLE" -d postgres -tA -c 'SELECT 1' > /dev/null 2>&1; then
    fail "$when: $APP_ROLE can connect to the maintenance database postgres"
  fi
  out="$(timeout 120 "${compose[@]}" exec -T postgres psql -U "$APP_ROLE" -d auth -tA -c "COPY (SELECT 1) TO PROGRAM 'true'" 2>&1 || true)"
  [[ "$out" == *"permission denied"* ]] || fail "$when: $APP_ROLE may run a program from the database (answer: $out)"
  out="$(timeout 120 "${compose[@]}" exec -T postgres psql -U "$APP_ROLE" -d auth -tA -c "CREATE ROLE e2e_not_allowed_$run" 2>&1 || true)"
  [[ "$out" == *"permission denied"* ]] || fail "$when: $APP_ROLE may make a role (answer: $out)"
}
```

  4. **Step 1.** After the line `grep -q "required variable" "$tmp/config.out" || fail "step 1: docker compose did not say which variable is required"` add:

```bash
# The password of Auth-Core's database role has no default: the file does not render without it.
grep -v '^AUTH_DB_APP_PASSWORD=' "$tmp/prod.env" > "$tmp/no-app-password.env"
if docker compose -p "$COMPOSE_PROJECT_NAME" -f "$(host_path "$root/deploy/docker-compose.prod.yml")" --env-file "$(host_path "$tmp/no-app-password.env")" config > "$tmp/config2.out" 2>&1; then
  fail "step 1: docker compose rendered the production file without AUTH_DB_APP_PASSWORD"
fi
grep -q "AUTH_DB_APP_PASSWORD" "$tmp/config2.out" || fail "step 1: docker compose did not name AUTH_DB_APP_PASSWORD as the missing variable"
```

  and in the `pass` line of step 1 replace `docker compose refuses the file without its secrets` by `docker compose refuses the file without its secrets (the password of Auth-Core's database role among them)`.

  5. **Step 2.** Before the `pass "step 2: ..."` line add `check_app_role "step 2"`, and in that `pass` line replace `answers a login;` by `answers a login (as a role that is not a superuser and owns the database and its tables);`.

  6. **Step 5.** The comment above the first command of step 5 (`# docs/operations/backup.md: ...`) becomes:

```bash
# docs/operations/backup.md: the database is dumped with pg_dump in the custom format, no owner; the key files, the manifest and the
# environment file are copied. Then the disaster: the service stops, the database is dropped. The restore: recreate it owned by Auth-Core's
# role, pg_restore as the superuser with --role so that the restored objects belong to that role, start. The commands are the runbook's, word for word.
```

  The two lines that recreate and restore:

```bash
timeout 120 "${compose[@]}" exec -T postgres psql -U auth -d postgres -c 'CREATE DATABASE auth OWNER auth' > /dev/null || fail "step 5: could not create the database"
timeout 300 "${compose[@]}" exec -T postgres pg_restore -U auth -d auth --no-owner --exit-on-error < "$backup/auth.dump" || fail "step 5: pg_restore failed"
```

  become:

```bash
timeout 120 "${compose[@]}" exec -T postgres sh -c 'psql -U auth -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE auth OWNER $AUTH_DB_APP_USER" -c "REVOKE CONNECT ON DATABASE auth FROM PUBLIC"' > /dev/null || fail "step 5: could not create the database"
timeout 300 "${compose[@]}" exec -T postgres sh -c 'pg_restore -U auth -d auth --no-owner --role="$AUTH_DB_APP_USER" --exit-on-error' < "$backup/auth.dump" || fail "step 5: pg_restore failed"
```

  (The `sh -c '...'` is the container's shell: `$AUTH_DB_APP_USER` is the value the `postgres` service has, the one the init script used. Nothing in the single quotes is expanded by the host's shell.) After the line `[[ "$CLI_EXIT" == "0" && "$CLI_OUT" == *"$ORG"* ]] || fail "step 5: the company is not in the restored database"` add:

```bash
# The runbook's own check of the owners (docs/operations/backup.md, step 5 of the restore): one line, Auth-Core's role.
owners="$(timeout 120 "${compose[@]}" exec -T postgres psql -U auth -d auth -tA -c "SELECT pg_get_userbyid(relowner), count(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace GROUP BY 1" | tr -d '\r')" \
  || fail "step 5: the owners of the restored objects could not be read"
[[ "$owners" =~ ^${APP_ROLE}\|[0-9]+$ ]] || fail "step 5: the restored objects do not all belong to $APP_ROLE (owner and count: $owners)"
check_app_role "step 5"
# The role works after the restore: a login (a write of the lockout row and of the token entries) and a refresh as the role the service connects as.
call POST /auth/login "$tmp/login.json"
expect_status "step 5: a login after the restore" 200
```

  and in the `pass "step 5: ..."` line replace `brought the database back:` by `brought the database back, owned by Auth-Core's role again:`. (The refresh of the cookie issued before the backup, a few lines above, is the other write as that role; `$tmp/login.json` is the admin's login body of step 4.)

  7. **The proxy's fixed address.** After the line `DIRECT_URL="http://127.0.0.1:$AUTH_PORT"` add:

```bash
# The test proxy's fixed address on the proxy network (inside AUTH_PROXY_SUBNET, not its gateway): the one address the service trusts, the
# pattern of docs/deployment/vps.md ("A proxy in a container"). Trusting the whole subnet would also trust its gateway.
PROXY_IP="10.250.1.10"
```

  In the here-document of `prod.env`, the line `AUTH_PROXY_KNOWN_NETWORKS=10.250.1.0/24` becomes three lines:

```bash
AUTH_PROXY_KNOWN_PROXIES=$PROXY_IP
AUTH_PROXY_KNOWN_NETWORKS=
E2E_PROD_PROXY_IP=$PROXY_IP
```

  and the two comment lines after `chmod 0600 "$tmp/prod.env"` (`# AUTH_PROXY_KNOWN_PROXIES is left out on purpose: ...` and `# compose file that is set in this shell would win ...`) become:

```bash
# AUTH_PROXY_KNOWN_PROXIES is the proxy's own address, not the default of the file (both gateways): an explicit value replaces the default.
# A variable of the compose file that is set in this shell would win over the environment file: they are unset, so the stack gets exactly
# the file above.
```

  8. **Step 4.** The two header comment lines `#      HSTS is sent; a failed login through the proxy is recorded with the address the proxy saw, not the one the client wrote and not` and `#      the proxy's own.` become:

```bash
#      HSTS is sent; a failed login through the proxy is recorded with the address the proxy saw, not the one the client wrote and not
#      the proxy's own. The proxy has a fixed address on the proxy network and that one address is all the service trusts (the pattern of
#      docs/deployment/vps.md, "A proxy in a container").
```

  In the code, after the line that reads `CADDY_IP` (`CADDY_IP="$(docker inspect ... )" || fail "step 4: docker inspect of the caddy container failed"`) add:

```bash
expect_eq "step 4: the proxy has the fixed address the service trusts" "$CADDY_IP" "$PROXY_IP"
proxy_env="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$auth_id" | tr -d '\r' | grep '^Auth__Proxy__' | sort)" \
  || fail "step 4: docker inspect of the auth container failed"
expect_eq "step 4: what the service trusts for the client address" "$proxy_env" "$(printf 'Auth__Proxy__KnownNetworks__0=\nAuth__Proxy__KnownProxies__0=%s' "$PROXY_IP")"
```

  and in the line `[[ "$RECORDED" != "$CADDY_IP" ]] || fail "step 4: ...: X-Forwarded-For was not honoured from the trusted proxy network"` replace `from the trusted proxy network` by `from the trusted proxy`. The assertions of step 4 hold as they were: the recorded address is not the forged `203.0.113.77` (Caddy sets the header itself; the Caddyfile names no `trusted_proxies`), not `CADDY_IP` (read from `docker inspect`; the proxy is on one network only, so the inspect prints one address, which is now also compared with the fixed one), and not empty. (`$auth_id` is set in step 2.)

  9. **Step 6.** The header lines of step 6 (`#   6. which gateway a proxy on the host arrives from: ...` to `...not about the script. It runs last so that steps 1 to 5 are not lost to it.`) become:

```bash
#   6. which gateway a proxy on the host arrives from: one failed login straight to the published port, with no X-Forwarded-For, is
#      recorded with the gateway of one of the two networks (10.250.0.1 or 10.250.1.1), the two addresses the compose file trusts BY DEFAULT
#      (a host proxy needs them). The networks of the container and the Docker version are printed. This is a check of the Docker of THIS
#      machine: Docker Desktop (Windows, macOS) is not a Linux VPS and may deliver the connection from another address (its own VM's); a
#      failure there is a finding about the default of AUTH_PROXY_KNOWN_PROXIES for that Docker, not about the script. This stack trusts only
#      its proxy's own address (step 4), so a second login from the host with a forged X-Forwarded-For must be recorded with the same
#      connection address, not the forged one: the host's gateway is not believed. It runs last so that steps 1 to 5 are not lost to it.
```

  After the `fi` that closes the gateway check (the `if [[ "$GW_IP" != "10.250.0.1" && "$GW_IP" != "10.250.1.1" ]]; then fail ...; fi`, so that a wrong gateway is reported first) and before the `pass "step 6: ..."` line add:

```bash
# The gateway is NOT trusted in this stack (only the proxy's own address is): a forged X-Forwarded-For sent from the host is not believed.
GW2_EMAIL="gateway-forged-$run@example.invalid"
json_body "$tmp/gateway2.json" "{'email': '$GW2_EMAIL', 'password': 'Wrong-Password-1'}"
curl -sS --max-time 20 -o /dev/null -H 'Content-Type: application/json' -H 'X-Forwarded-For: 203.0.113.88' --data-binary "@$tmp/gateway2.json" "$DIRECT_URL/auth/login" \
  || fail "step 6: the login with a forged X-Forwarded-For was not answered"
GW2_IP="$(recorded_ip "$GW2_EMAIL" || true)"
[[ -n "$GW2_IP" && "$GW2_IP" == "$GW_IP" ]] \
  || fail "step 6: a forged X-Forwarded-For sent from the host was recorded as '${GW2_IP:-no address}' instead of '$GW_IP': the host's gateway is trusted"
```

  and in the last `pass "step 6: ..."` line append `; a forged X-Forwarded-For from the host is not believed (only the proxy's own address is trusted)` before the closing quote.

- [ ] **Step 6: The runbooks.** `docs/operations/backup.md`, edits by anchor (the rest stays):

  1. Before the heading `## What to back up` add:

```markdown
## Two database roles

PostgreSQL here has two roles. The **superuser** `auth` (`POSTGRES_USER`) is the operator's: every command below that talks to PostgreSQL runs as it,
inside the `postgres` container, where the local socket needs no password. **Auth-Core's own role** (`AUTH_DB_APP_USER`, `auth_app` unless you
changed it, with the password `AUTH_DB_APP_PASSWORD`) is not a superuser, owns the database `auth` and everything in it, and is the only way
Auth-Core connects. The image creates it when the volume is first made ([`deploy/postgres-init/10-auth-app-role.sh`](../../deploy/postgres-init/10-auth-app-role.sh)).
A dump holds no roles and (as made here) no owners, so a restore has to give the restored objects back to Auth-Core's role: step 3 of the restore
does, and an object that stays with the superuser stops the next migration of Auth-Core.
```

  2. In the paragraph after the nightly backup, the sentence `` The custom format is compressed and restores with `pg_restore`; `--no-owner` lets it restore into the role `auth` whatever the dump was made as. `` (in the file it wraps after `whatever the dump was`) becomes: `` The custom format is compressed and restores with `pg_restore`; `--no-owner` leaves the owners out of the dump, so who owns the restored objects is decided when you restore (step 3 below). ``

  3. Step 2 of the restore becomes:

````markdown
2. **Recreate the database**, owned by Auth-Core's role (the connection is to the `postgres` database, so the target can be dropped). A new
   database starts with every role allowed to connect, so the second command takes that away again:

   ```bash
   $C exec -T postgres psql -U auth -d postgres -c 'DROP DATABASE IF EXISTS auth WITH (FORCE)'
   $C exec -T postgres sh -c 'psql -U auth -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE auth OWNER $AUTH_DB_APP_USER" -c "REVOKE CONNECT ON DATABASE auth FROM PUBLIC"'
   ```

   (`sh -c '...'` runs the command in the container, where `$AUTH_DB_APP_USER` is the name the volume was made with.)
````

  and step 3 becomes:

````markdown
3. **Restore the dump** as the superuser, **as Auth-Core's role** (`--role`): the restored tables, sequences and indexes then belong to it.
   Without `--role` the restore succeeds and every object belongs to the superuser, and Auth-Core fails at its next migration:

   ```bash
   $C exec -T postgres sh -c 'pg_restore -U auth -d auth --no-owner --role="$AUTH_DB_APP_USER" --exit-on-error' < backups/auth-DATE.dump
   ```
````

  4. In step 5, after the `list-orgs` block and before the paragraph that starts `Then sign in as someone and refresh.`, add:

````markdown
   The restored objects must all belong to Auth-Core's role: this prints one line, with its name and the number of objects (the superuser `auth`
   in that line means step 3 was run without `--role`):

   ```bash
   $C exec -T postgres psql -U auth -d auth -tA -c "SELECT pg_get_userbyid(relowner), count(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace GROUP BY 1"
   ```
````

  5. The paragraph that starts `If you restored into a **new server**:` is replaced by:

```markdown
If you restored into a **new server**: install Docker, put `deploy/` (with its `postgres-init/` directory), `.env`, the keys and the manifest in place
(restore the keys with `sudo cp -a`, or run the `chown` and `chmod` of [`key-rotation.md`](key-rotation.md) again), and run `$C up -d postgres`. On the
empty volume the image runs `deploy/postgres-init/10-auth-app-role.sh`, which makes Auth-Core's role and the empty database it owns from
`AUTH_DB_APP_USER` and `AUTH_DB_APP_PASSWORD` in `.env` (the name and the password may differ from the old server's: the dump holds no role).
Wait until `$C exec -T postgres pg_isready -h 127.0.0.1 -U auth -d auth` says it accepts connections (over TCP: on a fresh volume the image first
runs a temporary server that listens on its socket only), then do steps 2 and 3, and step 5. Skip step 1: there is no `auth` container yet to
stop. In step 4 use `$C up -d auth` instead of `$C start auth`: it creates the container, which `start` cannot. If the first start of `postgres`
stopped in the script (its log says `AUTH_DB_APP_USER must be ...`), the volume already counts as made and a second start skips the script: on a
server whose database is still empty, remove the volume (`$C down -v`), correct `.env` and start again.
```

  `docs/operations/key-rotation.md`: the bullet that starts `` - If the **database or `.env`** leaked as well: `` (it runs to the end of the code block and the line `(Letters and digits only, because ... recreate both services.)`) is replaced by:

````markdown
- If the **database or `.env`** leaked as well: change the passwords of **both** database roles, the operator's superuser `auth` (`POSTGRES_PASSWORD`)
  and Auth-Core's own role (`AUTH_DB_APP_USER`, `AUTH_DB_APP_PASSWORD`), inside PostgreSQL and then in `.env`; change the relay's password
  (`AUTH_SMTP_PASSWORD`) at the relay and in `.env`; and consider every password hash exposed: ask people to reset their passwords (the audit
  log shows who signed in meanwhile). Each command asks for the new password itself, twice, so it is in no argument and no shell history
  (an empty answer removes the password: type one):

  ```bash
  $C exec postgres psql -U auth -d postgres -c '\password auth'
  $C exec postgres sh -c 'psql -U auth -d postgres -c "\password $AUTH_DB_APP_USER"'
  ```

  (Letters and digits only, at least 16, because the value is part of a connection string. Put both in `.env` at once, then `$C up -d`: compose
  recreates the services whose environment changed. `postgres` reads the two passwords only when the volume is first made, so recreating it
  changes nothing in the database; `auth` gets the new connection string. Until it does, its new connections fail.)
````

- [ ] **Step 7: The threat model and the guide.** `docs/security/threat-model.md`:

  1. In the table "PostgreSQL", the row that starts `| S | Another container or host connects as `auth` |` becomes:

```markdown
| S | Another container or host connects as `auth` or as Auth-Core's role | No published port; a private network; a long random password for each role from `.env`; Auth-Core's role may connect to its own database only (`CONNECT` is revoked from PUBLIC on it and on the maintenance databases, `deploy/postgres-init/10-auth-app-role.sh`) | The passwords are in the containers' environment. Inside the `postgres` container the local socket and the loopback addresses trust every role (which is why the operator's commands need no password): a process in that container is part of the trusted base |
```

  and the row that starts `| E | Code in Auth-Core runs arbitrary SQL |` becomes:

```markdown
| E | Code in Auth-Core runs arbitrary SQL | EF Core parameterises every query; the few raw statements (`FromSql`, `ExecuteSqlAsync`, `SqlQuery`, all with interpolated values, which EF sends as parameters) never concatenate input into the text; **Auth-Core connects as a role of its own that is not a superuser** (none of `SUPERUSER`, `CREATEROLE`, `CREATEDB`, `REPLICATION`, `BYPASSRLS`; spec 0008, Decision 14; `scripts/e2e-prod.sh`, `check_app_role`, shows the attributes and that the role cannot connect to the maintenance database, run `COPY ... TO PROGRAM` or make a role, after the first start and after a restore) | A flaw that ran SQL reaches everything that role owns, which is all of Auth-Core's data (accounts, password hashes, sessions, companies, the audit log), but not the rest of PostgreSQL and not the host: it cannot run a program in the postgres container |
```

  2. The sub-heading `### Pending the owner's decision` and the paragraph under it (`Found while building ... accepted them yet.`) are removed. Items 41 to 44 stay where they are, now ordinary items of the list of accepted risks, each with the decision that accepted it: append `; accepted by Decision 15 (spec 0008)` before the final full stop of items 41, 42 and 43 (the text in the brackets stays), and `. Accepted by Decision 15 (spec 0008)` as a last sentence of item 44. Item 45 (`**Auth-Core connects to PostgreSQL as its superuser**` ...) is **removed**: Decision 14 closes it. In its place, as item 45:

```markdown
45. **Auth-Core's database role owns all of Auth-Core's data.** A flaw that ran SQL reaches the accounts, the password hashes, the sessions, the companies and the audit log, but not the rest of PostgreSQL and not the host (spec 0008, Decision 14).
```

  3. In the intro of "Residual risks", the sentence `Items 1 to 40 are the risks accepted by specs 0002 to 0008. The risks that no spec has accepted are listed apart, after them.` (it wraps after `accepted by`) becomes `Items 1 to 45 are the risks accepted by specs 0002 to 0008 (41 to 44 by Decision 15; 45 is what Decision 14 leaves).`

  4. In the section "Accepted earlier and closed by spec 0008", the intro `These items of specs 0003 to 0005 no longer stand as they were written; they are named so that none is lost.` becomes `These items of specs 0003 to 0005, and one found while building spec 0008, no longer stand as they were written; they are named so that none is lost.` Then add as the last bullet:

```markdown
- Auth-Core connecting to PostgreSQL as its superuser, found while the production compose file was built: closed by spec 0008, Decision 14 (`deploy/postgres-init/10-auth-app-role.sh`, `scripts/e2e-prod.sh`); what is left of it is item 45.
```

  `docs/deployment/vps.md` (Task 14), by anchor:

  1. In step 1, the sentence `You need two files and nothing else from the repository: the compose file and the example of its environment.` becomes `You need three files and nothing else from the repository: the compose file, the script that makes Auth-Core's database role, and the example of its environment.` In the code block, the line `mkdir -p deploy` becomes `mkdir -p deploy/postgres-init`, and after the line that fetches `docker-compose.prod.yml` a line is added:

```bash
curl -fsSL https://raw.githubusercontent.com/MckCieply/Auth-Core/v0.1.0/deploy/postgres-init/10-auth-app-role.sh -o deploy/postgres-init/10-auth-app-role.sh
```

  (The lines `sudo chown "$USER" /srv/auth-core`, the fetch of `.env.prod.example` and `chmod 600 .env` stay.)

  1b. In the code block of step 1, after the `curl` of `10-auth-app-role.sh` add the line `chmod 644 deploy/postgres-init/10-auth-app-role.sh` (the image's database user, uid 70, reads the file through the bind mount: a `umask` of 077 would leave it unreadable and the first start would fail).

  1c. In step 5, the sentence `The service creates and migrates its database on its own at start.` becomes `The service migrates its database on its own at start (PostgreSQL creates it, owned by Auth-Core's role, at the first start).`

  2. In the table of step 4, after the row that starts `` | `POSTGRES_PASSWORD` | ``, add the row ``| `AUTH_DB_APP_PASSWORD` | a second long random value, letters and digits only, at least 16: the password of the database role Auth-Core connects as (`AUTH_DB_APP_USER`, `auth_app`; leave it) |`` and change the `POSTGRES_PASSWORD` row's text to `` a long random value, letters and digits only: the password of the superuser, which only you use (backups, restores) ``. Under the table add a paragraph:

```markdown
**Two database roles.** Auth-Core connects to PostgreSQL as a role of its own that is not a superuser and owns only its database; the superuser stays for you. Both are
created when the volume is first made, from `.env`. They are **not** created later: if the first start stops with a message that starts `AUTH_DB_APP_USER must be`
(it names the rule, never the password), the volume already counts as made and a second start skips the step, so remove the volume, which holds nothing yet
(`docker compose -f deploy/docker-compose.prod.yml --env-file .env down -v`), correct `.env` and start again. Start from an empty volume: a volume made by anything else has no such role.
```

  3. In the table "When something is wrong", the row `"bind source path does not exist"` gets the cause `, or `deploy/postgres-init/10-auth-app-role.sh` is missing (step 1)`: its text becomes `` `AUTH_KEYS_DIR` or `AUTH_MANIFEST` names a path that is not there (compose never creates it), or `deploy/postgres-init/10-auth-app-role.sh` is missing (step 1) ``. Then, before the row that starts `` | `docker compose` says "required variable ... is missing" ``, add:

```markdown
| `password authentication failed for user "auth_app"` in the log of `auth`, or `role "auth_app" does not exist` | The volume was made without the role: the first start of `postgres` stopped in its script and was restarted, or the volume is older than this version. On a server whose database is empty: `down -v` and start again; otherwise restore the dump into a new volume as in [`backup.md`](../operations/backup.md) |
```

  `CHANGELOG.md`: in the `Production.` bullet, `a named network for a proxy in a container), ` becomes `a named network for a proxy in a container; Auth-Core connects to PostgreSQL as a role of its own that is not a superuser), `.

  `docs/superpowers/plans/0008-acceptance-map.md` (Task 14 left two markers for this task in the row of criterion 12):
  - In the criterion of row 12, ` (role checks: Task 15)` becomes `; Auth-Core's database role is not a superuser and owns the tables, and a restored database is owned by it again`.
  - In the tests cell of row 12, the closing clause `; the clause on Auth-Core's database role (not a superuser, owns the tables, a restored database owned by it again) is added by Task 15` becomes `` ; the clause on Auth-Core's database role: `scripts/e2e-prod.sh` `check_app_role` in step 2 (the role's attributes, the owners of the database and of the tables, sequences and indexes, no connection to the maintenance database, no `COPY ... TO PROGRAM`, no `CREATE ROLE`) and in step 5 (the same after the restore, the owners line of the runbook, a login), and, in step 1, the refusal to render without `AUTH_DB_APP_PASSWORD` ``.
  - In the tests cell of row 13, append `` ; the restore uses `--role` and a database created `OWNER $AUTH_DB_APP_USER`, and step 5 runs the runbook's commands, then its owners query ``.
  - In the table "Contract sentences that are not acceptance criteria", after the row that starts `| The image: labels from build arguments`, add the row `` | Two database roles: the superuser for the operator, Auth-Core's own role created when the volume is first initialised, owning the database; the migrations run as it | `deploy/postgres-init/10-auth-app-role.sh` (probed: all five migrations run as the role; Step 8 of Task 15); `scripts/e2e-prod.sh` steps 1, 2 and 5 | ``.

- [ ] **Step 8: Check the files, and probe the script on a throwaway container.** None of this starts the Auth-Core stack, publishes a port or touches a container that is not the probe's. From the repository root:

```bash
bash -n scripts/e2e-prod.sh
tmp="$(mktemp -d)"
grep -v '^AUTH_DB_APP_PASSWORD=' deploy/.env.prod.example > "$tmp/no-app-password.env"
docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env.prod.example config | grep -E "Username=|AUTH_DB_APP|10-auth-app-role|create_host_path"
docker compose -f deploy/docker-compose.prod.yml --env-file "$tmp/no-app-password.env" config 2>&1 | head -n 2
{ cat deploy/.env.prod.example; printf 'E2E_PROD_PROXY_IP=10.250.1.10\nE2E_PROD_PKI_DIR=%s\nE2E_PROD_CADDYFILE=%s\n' "$PWD" "$PWD/scripts/prod-test.Caddyfile"; } > "$tmp/overlay.env"
docker compose -f deploy/docker-compose.prod.yml -f scripts/prod-test.compose.yml --env-file "$tmp/overlay.env" config | grep -E "ipv4_address"
C:/p6v/Scripts/python.exe scripts/check-docs.py docs/operations/backup.md docs/operations/key-rotation.md docs/security/threat-model.md docs/deployment/vps.md CHANGELOG.md
scripts/secret-scan.sh
rm -rf "$tmp"
```

  Expected: no output from `bash -n`; the rendered file shows `Username=auth_app`, `AUTH_DB_APP_USER: auth_app`, `AUTH_DB_APP_PASSWORD: CHANGEME...`, the mount of `10-auth-app-role.sh` and `create_host_path: false`; the second render stops with `required variable AUTH_DB_APP_PASSWORD is missing a value` (twice); the overlay renders with `ipv4_address: 10.250.1.10` for the proxy; `all links, paths, files and tests resolve` (the variables `AUTH_DB_APP_USER` and `AUTH_DB_APP_PASSWORD` are in the compose file and the example; the path of the script exists); `PASS` twice (the example's placeholder is not a secret and has no `password` assignment that gitleaks flags; if it does, report it: do not list it).

  The probe of the script (the same one this plan was written from; it needs only Docker and the image that `docker-compose.prod.yml` pins). It is a separate block: it makes its own temporary directory and the two throwaway passwords of its throwaway container, so that it does not depend on any variable of the block above:

```bash
tmp="$(mktemp -d)"
SUPER_PW="$(openssl rand -hex 16)"; APP_PW="$(openssl rand -hex 16)"
IMG="postgres:16-alpine@sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea"
export MSYS_NO_PATHCONV=1
docker network create probe-net > /dev/null
docker run -d --name probe-pg --network probe-net -e POSTGRES_DB=auth -e POSTGRES_USER=auth -e POSTGRES_PASSWORD="$SUPER_PW" \
  -e AUTH_DB_APP_USER=auth_app -e AUTH_DB_APP_PASSWORD="$APP_PW" \
  -v "$PWD/deploy/postgres-init/10-auth-app-role.sh:/docker-entrypoint-initdb.d/10-auth-app-role.sh:ro" "$IMG" > /dev/null
for _ in $(seq 1 40); do docker exec probe-pg pg_isready -h 127.0.0.1 -U auth -d auth > /dev/null 2>&1 && break; sleep 1; done
docker exec probe-pg psql -U auth -d auth -tA -c "SELECT rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls, rolcanlogin FROM pg_roles WHERE rolname = 'auth_app'" \
  -c "SELECT pg_get_userbyid(datdba), has_database_privilege('pg_monitor', 'auth', 'CONNECT') FROM pg_database WHERE datname = 'auth'"
docker exec probe-pg psql -U auth_app -d postgres -c 'SELECT 1' 2>&1 | head -n 1
docker exec probe-pg psql -U auth_app -d auth -c "COPY (SELECT 1) TO PROGRAM 'true'" 2>&1 | head -n 1
docker run --rm --network probe-net -e PGPASSWORD="$APP_PW" "$IMG" psql -h probe-pg -U auth_app -d auth -tA -c 'SELECT current_user'
echo "the password is in the log of the container $(docker logs probe-pg 2>&1 | grep -c "$APP_PW") time(s)"
"C:/Program Files/dotnet/dotnet.exe" tool restore > /dev/null
"C:/Program Files/dotnet/dotnet.exe" ef migrations script --idempotent -p src/Auth.Infrastructure -s src/Auth.Server -o "$tmp/migrate.sql" > /dev/null
docker exec -i -e PGPASSWORD="$APP_PW" probe-pg psql -h 127.0.0.1 -U auth_app -d auth -v ON_ERROR_STOP=1 -q < "$tmp/migrate.sql"; echo "migrations as the role: exit $?"
docker exec probe-pg pg_dump -U auth -d auth --format=custom --no-owner > "$tmp/dump.bin"
docker exec probe-pg psql -U auth -d postgres -c 'DROP DATABASE IF EXISTS auth WITH (FORCE)'
docker exec probe-pg sh -c 'psql -U auth -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE auth OWNER $AUTH_DB_APP_USER" -c "REVOKE CONNECT ON DATABASE auth FROM PUBLIC"'
docker exec -i probe-pg sh -c 'pg_restore -U auth -d auth --no-owner --role="$AUTH_DB_APP_USER" --exit-on-error' < "$tmp/dump.bin"; echo "restore: exit $?"
docker exec probe-pg psql -U auth -d auth -tA -c "SELECT pg_get_userbyid(relowner), count(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace GROUP BY 1"
docker rm -fv probe-pg > /dev/null; docker network rm probe-net > /dev/null; rm -rf "$tmp"; unset SUPER_PW APP_PW
```

  Expected, in order: `f|f|f|f|f|t`; `auth_app|f`; `FATAL:  permission denied for database "postgres"` (inside the first lines of psql's error); `ERROR:  permission denied to COPY to or from an external program`; `auth_app`; `the password is in the log of the container 0 time(s)`; `migrations as the role: exit 0`; `DROP DATABASE`, `CREATE DATABASE`, `REVOKE`; `restore: exit 0`; one line `auth_app|78` (the number of objects may differ if a migration was added since: what matters is the single line and the role's name, and that it is not `auth`). If the first start of `probe-pg` stops (`docker ps -a` shows it exited), read `docker logs probe-pg`: the message names the rule. The container, its anonymous volume and the network are removed by the last line (`docker rm -fv`); `docker ps -a`, `docker network ls` and `docker volume ls -f dangling=true` must not list anything of the probe.

- [ ] **Step 9: Hand back** — uncommitted. Files: everything listed under Files. Proposed subjects (the orchestrator may split them): `feat(deploy): a database role of its own for Auth-Core in production`; `test(deploy): e2e-prod checks the role and a restore that gives its objects back, and trusts the test proxy by its own address`; `docs: two database roles in the backup runbook, the rotation runbook, the threat model and the guide`. The orchestrator makes `deploy/postgres-init/10-auth-app-role.sh` executable in the index. Report: the output of the probe, whether `docker compose config` and the document check were clean, and that nothing was started but the probe.

---

## Verification and release (Tue 24.02)

**Task 15 runs first.** The task above ("A database role of its own for Auth-Core in production", Tuesday 24.02 before verification) is an implementer task like the others and is done, and committed by the orchestrator, **before** anything below: the gate, the three verifiers, the live checks (`scripts/e2e-prod.sh` now checks the role and a restore that gives the objects back to it) and the backup runbook that the security verifier follows by hand all read its result.

Nothing here is a task for an implementer. The orchestrator runs the gate, dispatches the three verifiers, records the result, and, **each after its own "yes" from the owner**, merges, tags
and releases. Only one verifier builds and tests in the tree; only one uses the Docker stack and the ports 8080, 8025, 8088 and 8443, with a `COMPOSE_PROJECT_NAME` of its own, one stack at a time (the other
session's verifiers may hold the ports: ask it, by message, before starting a stack). The names: `auth-core-hardening` for the five older scripts and `e2e-hardening.sh` (exported once for the whole
sequence), `auth-core-notes` (the default of `e2e-notes.sh`), `auth-core-web` or `auth-core-web-<suffix>` (the only names `e2e-web.sh` accepts; it refuses to start over a running stack of that name),
`auth-core-prodtest` (`e2e-prod.sh`); never `auth-core`, the owner's development project.

### The gate before the verifiers

```bash
"C:/Program Files/dotnet/dotnet.exe" build -warnaserror
"C:/Program Files/dotnet/dotnet.exe" test                                    # every test; the count is in the acceptance map's log
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 "C:/Program Files/dotnet/dotnet.exe" test --filter-class "Auth.IntegrationTests.InvitationDomainTests"
(cd clients/python && C:/p6v/Scripts/python.exe -m pip install "hatchling==1.32.4" && C:/p6v/Scripts/python.exe -m pytest -q)   # hatchling: the pin of the package's [build-system], approved in slice 6, so that the wheel-metadata test runs and does not skip
(cd samples/notes-api && C:/p6v/Scripts/python.exe -m pytest -q)
(cd samples/notes-web && npx ng test --watch=false && npx ng build --configuration production)
C:/p6v/Scripts/python.exe scripts/check-docs.py                              # the documents against the repository
scripts/secret-scan.sh                                                       # PASS twice (the history; the files not committed yet)
git status --short                                                           # clean apart from what the orchestrator is about to commit
```

### The three verifiers (Sonnet, fresh context, read-only; fail-closed; at most two fix rounds each, as `docs/workflow.md` says)

Each reads **the spec, not this plan**, and uses `docs/superpowers/plans/0008-acceptance-map.md` (its table, its Review Focus, its list of contract sentences).

1. **Realization against the spec (the eight layers).** Every criterion 1–17 and every sentence of the contract to the named test or script step; the five Review Focus lines; the
   file list against the diff; the plan against the spec (where they disagree, the spec wins and the disagreement is a finding: the ones the plan itself names are in "Open questions");
   the OpenAPI description against the endpoints; the deployment guide followed as written by someone who has not seen the project (every path, setting and command exists); the
   documents' claims against the code (the threat model names a test or script for each mitigation: do they exist and say what is claimed?).
2. **End to end on a live stack.** On a **clean** stack (`export COMPOSE_PROJECT_NAME=auth-core-hardening`, then `down -v` before and after: never a bare `down -v` without the variable): the five older scripts on a stack started with `AUTH_RATE_LIMIT_ENABLED=false`, then
   `scripts/e2e-hardening.sh` on the same stack recreated with the defaults, then `scripts/e2e-notes.sh` and `scripts/e2e-web.sh` (each brings its own stack up), then
   `scripts/e2e-prod.sh` (the production compose file, in Production, through the test overlay: Mailpit with STARTTLS and a test authority the container trusts, frontend URLs on
   `https://localhost:8443`). First, before the rest of the stack steps: the probe that `scripts/e2e-prod.sh` Task 12 names (the production container's read-only root file system and the fetch of the revocation list at the handshake with the relay: the invitation mail of step 3 must arrive; if it does not, that is a finding about `deploy/docker-compose.prod.yml`). Then **the backup runbook followed literally by hand** on the production stack of `e2e-prod.sh` (`E2E_KEEP_STACK=1`): the dump, the restore, a refresh
   cookie from before the backup. Also by hand, once: a failed login with a forged `X-Forwarded-For` straight to the published port of the production stack (`127.0.0.1:8080`), read back from
   `audit_events`: the recorded address is the connection's, and with the default `AUTH_PROXY_KNOWN_PROXIES` the question "does a proxy on the host appear as `10.250.0.1`?" is answered by
   `curl` from the host through a one-line proxy of the verifier's choice (the guide's claim about the gateway address is the one thing the throwaway stack does not prove; Docker Desktop and a
   Linux host may differ, and the answer goes into the log).
3. **Security.** The headers on every error path; the trusted-proxy rule against spoofed `X-Forwarded-For` (including the IPv4-mapped and the IPv6 spellings and a list that is too wide);
   the audit log never holding a secret (every flow's row, searched for the password, the token and the link it used); `DELETE /auth/org` against rule 1, the lockout, the order of the checks and a race of two;
   the limiter's memory under many addresses; the outage path leaking nothing (no stack, no host name) and clearing no cookie; the container's flags (`read_only`, `cap_drop`, `no-new-privileges`, uid), the images
   pinned by digest and the labels; the secret scan and the dependency checks below; the production compose and its environment example for a default that is a secret.

Each finding carries a `scope` (does the same failure reproduce on `main`? `in-diff`, `pre-existing-blocking`, `pre-existing-nonblocking`). Every finding in the diff, Minor and Low too, is fixed in this slice;
a finding that needs a change of the spec goes to the owner.

### The secret scan and the dependency checks (criterion 17)

Run by the security verifier, and by the orchestrator before the merge. They read the repository and public advisory databases and write nothing but reports in a scratch directory.

```bash
# 1. the secret scan: the committed history of every branch and tag and the files not committed yet, with the list of the eight known false positives
scripts/secret-scan.sh
# 2. the .NET packages, direct and transitive
"C:/Program Files/dotnet/dotnet.exe" list package --vulnerable --include-transitive
# 3. the npm packages of the Angular sample: the names of the packages that have an advisory, runtime only and then with the development ones
(cd samples/notes-web && npm audit --omit=dev --json | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>console.log(Object.keys(JSON.parse(s).vulnerabilities).sort().join("\n")))')
(cd samples/notes-web && npm audit --json | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>console.log(Object.keys(JSON.parse(s).vulnerabilities).sort().join("\n")))')
# 4. the Python packages: the sample's pins, and what the virtual environment holds (the package's dependencies are floors, so the environment is what runs)
C:/p6v/Scripts/pip-audit.exe -r samples/notes-api/requirements.txt
C:/p6v/Scripts/pip-audit.exe
```

Expected:
- **The scan:** both lines `PASS ... no leaks found`, exit code 0.
- **The .NET packages:** no package listed as vulnerable (the command prints `has no vulnerable packages` for each project).
- **The npm packages: not zero, and the set is known.** Angular, its CLI and its build stay at 21.1.4 by spec 0007 Decision 13 (owner, 2026-02-17), with their advisories written down in the sample's README. The audit read while this plan was written gave 23 packages with an advisory (1 low, 7 moderate, 13 high, 2 critical). The runtime command prints exactly these six, all `@angular/*` at 21.1.4: `@angular/common`, `@angular/compiler`, `@angular/core`, `@angular/forms`, `@angular/platform-browser`, `@angular/router`. The second prints those and these seventeen of the development tooling, none of which reaches the built app or the image: `@angular-devkit/architect`, `@angular-devkit/core`, `@angular-devkit/schematics`, `@angular/build`, `@angular/cli`, `@angular/compiler-cli`, `@babel/core`, `@modelcontextprotocol/sdk`, `@schematics/angular`, `@vitest/mocker`, `ajv`, `pacote`, `picomatch`, `piscina`, `undici`, `vite`, `vitest`. These are **addressed by reference**: the acceptance map's "Plan-vs-implementation notes" cites Decision 13 and the sample's README. **A package that is not in the lists above, or an advisory in a runtime package that is not one of the six, stops the verifier: it is a finding.** (The advisory databases move: if a listed package disappears, nothing is wrong; if a new one appears, it is new.)
- **The Python packages:** `No known vulnerabilities found` from each `pip-audit`.

A finding that is not covered above is **addressed** by upgrading the package (a pin in `Directory.Packages.props`, `package.json`, a requirements file) in this slice, **or** by a written reason in the acceptance map's "Plan-vs-implementation notes" when the vulnerable code path is not used (the owner reads it). `gitleaks` is pinned by digest and runs from a container; the three audits use the public advisory databases (nuget.org, the npm registry, PyPA).

### Recording the outcome, and the merge

1. In `docs/superpowers/plans/0008-acceptance-map.md`: the "Plan-vs-implementation notes" and the "Local verification log" (commands, dates, results, the number of tests of each suite).
2. `docs/superpowers/specs/0008-hardening-and-release.md`: an `## As built (owner, <date>)` section (what the spec left open and how it was read; the second seed user's role by name; the trusted network of the sample
   overlays being one address; the rows that a limited mail request does not write; `login.succeeded` written before the issuance; what the verifiers changed). The owner signs it: a change of a spec is theirs.
3. `docs/design.md`: one line each, as plan 0006 did for week 4:
   - in "Security and ops" of the MVP table and in the paragraph "Deferred within the MVP", the note *"As built (spec 0008): the per-IP rate limiting and the trusted-proxy rule were built in week 6 with Auth-Core's own sliding-window limiter, not the framework's"*;
   - in "Week 6: hardening and release", after the line `Tag v0.1.0, image published to GHCR`: *"As built (spec 0008): released without CI (Decision 1); the audit log, company deletion and the production compose file were added; the threat model, the runbooks and the deployment guide are under `docs/`"*.
4. Update `docs/superpowers/plans/0008-hardening-and-release.md`: a short "As built" at its end, as plan 0006 has.
5. **The owner says "yes"**, then the local merge of `feat/0008-hardening-and-release` into `main` (no fast-forward; `main` must equal `origin/main` and the base checkout must be clean).

### The release (owner-gated: each step needs a separate "yes"; the owner logs in to GHCR himself)

1. **Tags.** Two annotated tags on the merge commit `M`: `v0.1.0` (message `Auth-Core 0.1.0`) and `python-v0.1.1` (message `auth-core-fastapi 0.1.1`). `python-v0.1.0` is not moved:

   ```bash
   git tag -a v0.1.0 -m "Auth-Core 0.1.0" "$M"
   git tag -a python-v0.1.1 -m "auth-core-fastapi 0.1.1" "$M"
   git tag -l --format='%(refname:short) %(objecttype) %(*objectname)' 'v0.1.0' 'python-v0.1.*'   # both are tags (not lightweight), both point at M; python-v0.1.0 is as before
   ```

2. **Push** `main` and the two tags (the owner's yes, a separate one): `git push origin main v0.1.0 python-v0.1.1`. The repository is made public by the owner.
3. **Build the image from the tag**, in a clean checkout of it, with the labels from the build arguments (`.git` is not in the build context):

   ```bash
   git worktree add C:/rel v0.1.0           # a short path; remove it afterwards: git worktree remove C:/rel
   cd C:/rel
   docker build -f src/Auth.Server/Dockerfile \
     --build-arg VERSION=0.1.0 --build-arg REVISION="$(git rev-parse HEAD)" \
     -t ghcr.io/mckcieply/auth-core:0.1.0 -t ghcr.io/mckcieply/auth-core:latest .
   docker image inspect ghcr.io/mckcieply/auth-core:0.1.0 --format '{{json .Config.Labels}}'
   ```

   Expected: the four labels, `version` `0.1.0`, `revision` the full hash of `M`, `licenses` `MIT`, `source` the repository.
4. **Smoke the image** before it leaves the machine: `docker run --rm ghcr.io/mckcieply/auth-core:0.1.0 admin` prints `error: missing_command` with the usage and exits with code 2.
5. **Push the image** (the owner logs in to GHCR himself with a token that has the `write:packages` scope; the plan never handles the token):

   ```bash
   docker login ghcr.io -u MckCieply         # the owner, interactively
   docker push ghcr.io/mckcieply/auth-core:0.1.0
   docker push ghcr.io/mckcieply/auth-core:latest
   ```

6. **Make the package public** (the owner, in the GitHub settings of the package `auth-core`: "Change visibility" to public; the label `org.opencontainers.image.source` already links it to the repository), then check as a stranger would:

   ```bash
   docker logout ghcr.io
   docker pull ghcr.io/mckcieply/auth-core:0.1.0
   docker buildx imagetools inspect ghcr.io/mckcieply/auth-core:latest | head -n 5      # the digest of 0.1.0
   ```

7. Install the package from the tag in a clean virtual environment (`pip install "auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v0.1.1#subdirectory=clients/python"`) and `python -c "import auth_core_fastapi; print(auth_core_fastapi.__version__)"`: `0.1.1`.

---

## Self-review

**1. Spec coverage.** Every bullet of the contract and every acceptance criterion maps to a task and a guard; the criteria also have their own table in the acceptance map (Task 14).

| Spec | Task | Guard |
| --- | --- | --- |
| Client address: remote address, trusted proxy, last non-proxy address, `X-Forwarded-Proto` only from proxies, `X-Forwarded-Host` never | 1 | `ClientAddressSetupTests`, `RateLimitProxyTests` |
| Proxy settings, empty by default, no framework defaults, every hop walked | 1 | `ProxySettingsTests`, `ClientAddressSetupTests.With_no_proxy_configured_*`, `.The_options_name_exactly_the_configured_proxies_and_walk_every_hop` |
| IPv4-mapped is IPv4; no remote address is `unknown`; the limiter partitions on it (IPv6 by `/64`); the audit records it whole | 1, 4, 5 | `ClientAddressTests`, `AuditLogTests.The_whole_client_address_is_recorded_*`, `AuditClientAddressTests` |
| The five limits, one policy per request by method and path, the sliding window of 6 × 10 s on the injected clock | 1 | `RatePolicyTests`, `SlidingWindowLimiterTests`, `RateLimitMiddlewareTests` |
| Settings `Auth:RateLimit:*`, blank = default, invalid stops the host naming the key; test host off; dev compose passes them | 1, 9 | `RateLimitSettingsTests`, `RateLimitDefaultsTests`; Task 9 step 5 |
| `429` body, `Retry-After`, `no-store`; no other work; refusals not counted; memory only; health and key set under `general`; lockout and mail limits stay | 1, 5 | `RateLimitMiddlewareTests`, `SlidingWindowLimiterTests`; the unedited lockout and mail tests |
| Refresh during an outage: `503`, `Retry-After: 5`, no cookie, same cookie later; the rest as spec 0002; login, logout, company API `500`; the residual risk | 2 | `RefreshOutageTests`, `SessionResponseHandlerTests`; threat model |
| Unhandled errors: `500 internal_error`, logged, in every environment; the `503` decided in the same place; Kestrel's early answers out of reach | 2 | `ErrorHandlingTests`, `ErrorHandlingProductionTests`; threat model, residual risk 28 |
| Security headers on every response, the table, no `Server`, key set and OpenAPI without `Cache-Control`, the Scalar policy, no HSTS, the no-store middleware replaced | 2 | `SecurityHeadersTests`, `SecurityHeadersOnRefusalTests`, `HostHardeningTests`; `e2e-hardening.sh` step 1 |
| `/auth/health` `GET` and `HEAD` only | 3 | `HealthMethodsTests` |
| JSON charset `415` | 3 | `JsonCharsetTests` |
| Invitation domain rule (encoded and typed), API and CLI | 3 | `InvitationDomainTests` |
| Second development seed user's role: by name in ordinal order, from the database (spec as amended) | 3 | `DevCompanySeedTests` |
| Data Protection in memory | 2 | `HostHardeningTests` |
| Audit table, columns, no foreign keys, never recorded, the 22 kinds | 4, 5, 6, 7, 8 | `AuditTableTests`, the flow tests, `AssertNoSecretsAsync` |
| `logout`, `refresh.reuse_detected`, `password.reset_requested` as specified | 5 | `AuditAccountFlowsTests`, `AuditRefreshReuseTests` |
| `rate_limit.hit` once per address and policy per minute, never failing the request | 5 | `RateLimitAuditTests`, `AuditRateLimitTests` |
| A change and its row in one transaction (a session issued and a session ended included); events on their own for what changes nothing, from a context of their own | 4, 5, 6 | `AuditLogTests`, `AuditAtomicityTests`, `AuditAccountFlowsTests.A_login_whose_row_cannot_be_written_issues_no_session`, `.A_logout_whose_row_cannot_be_written_ends_nothing` |
| Retention, hourly pruning, `Auth:Audit:RetentionDays` | 4 | `AuditPruningTests`, `AuditSettingsTests` |
| Reading by SQL, the three queries | 13 | `docs/operations/backup.md`; Task 13 step 4 |
| `org:delete` in the catalog; the lists that name the catalog follow | 7 | `OrgDeletePermissionTests`; the edited catalog tests; `e2e-tenancy.sh` |
| `DELETE /auth/org`, the table, the order of the checks, the lockout shared with login | 8 | `OrgDeleteEndpointTests` |
| `delete-org --org --confirm`, exit codes, the operator not bound by rule 1 | 7 | `DeleteOrgCommandTests` |
| Effects of deletion in one transaction under the lock; what a member sees | 7, 8 | `CompanyDeletionTests`, `OrgDeleteEndpointTests` |
| Production compose, environment example, `/auth/health` not reaching the database, Dockerfile pins and labels, development compose pins | 9, 12 | Task 9 steps 5 and 6; `e2e-prod.sh` |
| Sample proxies: headers, client address, trusted network | 10 | `e2e/08-proxy-headers.spec.ts`; `e2e-notes.sh` step 7 (the trusted network is one address: Open question 1) |
| Angular: the notice, `429` and `503` keep the session | 10 | the three new specs |
| OpenAPI: `DELETE /auth/org`, `429`, `415`, `503`, `org:delete` | 8 | `OpenApiHardeningTests`, `OpenApiTests` |
| Python: no change of behaviour; licence metadata; `0.1.1` | 14 | `test_metadata.py`, the `429` case of `test_jwks_cache.py` |
| Documents: threat model (all residual risks of 0002–0008), backup, key rotation, deployment guide, README, changelog | 13, 14 | the document check (`scripts/check-docs.py`); the 40 residual risks listed |
| Release: tags, image, GHCR public | the release section | owner-gated steps |
| Criteria 1–17 | all | `docs/superpowers/plans/0008-acceptance-map.md` |

**2. Placeholder scan.** The plan names no "TBD" and no "similar to Task N"; every task carries its code and its test code. Where a file of slice 7 is edited the plan gives anchors and the lines to put there (the files were still moving when it was written: Task 10's
precondition), and the tasks that start from migration ids say the orchestrator supplies the id (as plan 0005 does). The two copies that are commands (`cp LICENSE ...`) copy a file of the repository.

**3. Type consistency.** Names are the same wherever used: `ProxySettings`, `ClientAddress.Text/PartitionOf`, `RateLimitSettings.PermitKey/EnabledKey`, `RatePolicy`, `SlidingWindowLimiter.TryAcquire`, `TooManyRequestsResult`, `RateLimitAudit.ShouldRecord`,
`ErrorHandlingMiddleware.IsTransientFailure/RefreshRetryAfterSeconds/TemporarilyUnavailable`, `SecurityHeaders.ContentSecurityPolicy`, `JsonCharsetGuard.Applies/IsRefused`, `AuditLog.Stage/WriteAloneAsync/CompanyNameAsync`, `AtomicSignInResult`, `AuditEntry`, `AuditKinds.*`,
`AuditSettings.RetentionDaysKey`, `AuditPruner.PruneOnceAsync`, `CompanyService.CreateAsync(name, ct, via)`, `CompanyDeletionService.DeleteAsync(actor, companyId, confirmName, ct)`, `OperatorCommands.DeleteOrgAsync`, `DeleteOrgCommand`,
`TenancyErrors.TooManyRequests/UnsupportedMediaType/WrongPassword`, `AuthAppFactory.WithRemoteAddressHeader/RemoteAddressHeader`, `AuditTestBase`, `AuditApi.Text/Raw/DetailNames`, `OperatorCli.RunAsync`, `DatabaseOutage`, `SecurityHeadersApi`, `RateLimitApi`.

**4. Review Focus.** Each of the five lines has its test in the owning task (the acceptance map's table names them): 1 in Task 1, 2 in Task 1, 3 in Tasks 4–6, 4 in Task 8, 5 in Tasks 1, 9 and 12.

---

## Open questions for the owner

The spec was amended while this plan was reviewed (the second seed user's role is "by name in ordinal order"; the production compose has "a named network that a containerised proxy may join from its own compose file"): the plan follows it, and the earlier question about the role order is closed.

1. **The trusted network of the sample overlays is one address.** The spec says the compose files set Auth-Core's trusted network "to the proxy's". The plan gives Caddy a fixed address in a fixed subnet (`10.250.2.0/24`, outside Docker's default pools) of the notes overlay and trusts `<address>/32` through `Auth:Proxy:KnownNetworks`, so that the host's own
   gateway (from which the published `127.0.0.1:8080` and every e2e script reach Auth-Core) is **not** trusted and a script cannot spoof the address. A whole-subnet trust would believe the host. Confirm, or say that the whole network is wanted.
2. **`password.reset_requested` is written only for a request that is queued.** The mail limit's `429` (the sixth request an hour for an address) writes no row, because the request changed nothing and the per-IP limit and the retention already bound the table. If the log should hold every request, the row would be
   written on its own for the refused ones too.
3. **`DELETE /auth/org` answers `403 permissions_changed` when the company vanished while the request waited for its lock** (a second deletion at the same time). The spec's table has no `404` for this endpoint and the case is the caller losing their membership with the company; the service itself says `not_found`.
4. **The guide says a proxy on the host appears as the gateway of the compose network** (`10.250.0.1` for the default subnet) and sets `AUTH_PROXY_KNOWN_PROXIES` to it by default. That is how Docker's bridge networks behave on Linux; the throwaway stack of `e2e-prod.sh` proves the proxy-in-a-container case, not this one, and this machine runs Docker Desktop. The verifier checks it on the stack and writes the result into the log; the default stays unless it is wrong.

**Decisions the plan took that you may want to know about** (no answer needed unless you disagree):

- **`login.succeeded` and `logout` are part of the change they record.** The session is issued, and the session is revoked, in a transaction that also holds the audit row, so that the contract's "a change and its row are written together, or neither" holds. The cost is that a database that cannot write `audit_events` also cannot log anyone in or out (`500 internal_error`, no session, no cookie); a failed login, a refused request and a rate-limit hit are still written on their own and never fail a request. The sign-in holds its response body in memory until the commit (probed).
- **A login to an account that has no password yet** (an invited account that has not accepted) is recorded as `unknown_address`, with no subject: the answer is the same `401` as for an unknown address, and so is its row. The "As built" section records it.
