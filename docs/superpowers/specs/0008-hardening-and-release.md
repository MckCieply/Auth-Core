# Spec 0008 — Hardening and release v0.1.0

- **Status:** Accepted
- **Date:** 2026-02-19
- **Author:** Alex
- **Milestone:** Week 6 (hardening and release) — *"`v0.1.0` tagged"*: threat model
  (STRIDE), security headers, basic audit log, key rotation plan, backup runbook,
  README, tag `v0.1.0`, image published to GHCR. Also closes what the earlier slices
  left to hardening (Decision 3) and adds company deletion (Decision 8). Built
  19–24.02.
- **Context:** [`docs/design.md`](../../design.md) (MVP scope "Security and ops";
  Week 6; Release), ADR [0001](../../adr/0001-instance-per-project.md) (one instance
  per product), ADR [0004](../../adr/0004-same-origin-cookie-refresh.md) (one origin
  behind one proxy), specs [0002](0002-refresh-and-logout.md) (refresh errors, E1–E4),
  [0003](0003-lockout-and-abuse-resistance.md) (per-identifier lockout; per-IP limit
  and trusted proxy deferred), [0004](0004-email-flows.md),
  [0005](0005-tenancy-and-rbac.md) (company API, safety rules, CLI; "As built" items
  left to hardening), [0006](0006-python-consumer-package.md) and
  [0007](0007-angular-sample.md) (the samples and their proxies).

## Goal

Someone who has never seen the project can deploy Auth-Core on a server from a
published image, back it up, restore it, change its keys, and know which risks remain —
and the service resists the common abuse of a public login endpoint.

Concretely, this is met when:

```
# on a clean machine, with the production compose file and a filled-in .env
docker compose -f deploy/docker-compose.prod.yml --env-file .env up -d
#   -> pulls ghcr.io/mckcieply/auth-core:0.1.0, migrates the database, answers /auth/health
docker compose -f deploy/docker-compose.prod.yml run --rm auth admin create-org --name Acme
#   -> prints the company id

curl -i http://127.0.0.1:8080/auth/health          # -> 200, with the security headers, no Server header
# 31 logins within a minute from one address       # -> the 31st is 429 too_many_requests
# refresh while PostgreSQL is stopped               # -> 503 temporarily_unavailable, the cookie still works afterwards
# DELETE /auth/org with name and password           # -> 204; the members' tokens get 403 permissions_changed
# SELECT ... FROM audit_events                      # -> each of the above is recorded
```

and the backup runbook, followed step by step, brings back a dropped database with every
session still valid.

## In scope

1. **Per-IP rate limiting** and the trusted-proxy rule for the client address.
2. **Refresh during an outage** answers `503`, not `401`.
3. **Security headers** on every Auth-Core response, and on the sample proxies.
4. **Fixes left to hardening:** `/auth/health` methods, JSON charset, invitation domain
   rule, the second development seed user's role.
5. **Basic audit log:** a table, the events of every flow, 90-day retention.
6. **Company deletion:** operator CLI and company API.
7. **Production compose file** and digest-pinned images in the development compose.
8. **Angular sample:** the new `429` and `503` answers.
9. **Documents:** threat model, backup runbook, key rotation runbook, deployment guide,
   README, changelog; licence metadata of the Python package.
10. **Release:** tag `v0.1.0`, image in GHCR; tag `python-v0.1.1`.

## Out of scope / Deferred

- **CI** (GitHub Actions, CodeQL, Dependabot) — Decision 1. The checks run locally.
- **Automatic key rotation**, and rotation without signing everyone out — Decision 7.
- **An API or a screen to read the audit log** (design.md: audit viewer, stretch).
- **Deploying to the VPS** next to speech-to-mail: later work, outside this history.
- Leaving a company on one's own; more than one company per user (spec 0005).

## Contract

### Client address and trusted proxies

- The client address of a request is the connection's remote address, unless that
  address is a **trusted proxy**: then it is the last address in `X-Forwarded-For` that
  is not itself a trusted proxy. `X-Forwarded-Proto` is honoured from trusted proxies
  only. `X-Forwarded-Host` is never honoured.
- Trusted proxies are set in `Auth:Proxy:KnownNetworks` (a list of CIDR ranges) and
  `Auth:Proxy:KnownProxies` (a list of addresses, written as plain IPv4 or IPv6). Both
  are empty by default: nothing is trusted, forwarded headers are not read at all, and a
  client-supplied `X-Forwarded-For` is ignored. The framework's own defaults (loopback
  trusted, one hop) are never used; with proxies configured, every hop is walked.
- An IPv4 address that reaches Kestrel as IPv4-mapped IPv6 (`::ffff:a.b.c.d`) is
  treated as the IPv4 address. A request with no remote address (a test host) has the
  client address "unknown".
- The client address is what the rate limiter partitions on (an IPv6 address by its
  `/64` network, since one subscriber holds a whole `/64`) and what the audit log
  records (the full address).

### Per-IP rate limiting

- Limits per client address, each a window of one minute:

| Policy | Endpoints | Requests per minute |
| --- | --- | --- |
| `login` | `POST /auth/login` | 30 |
| `refresh` | `POST /auth/refresh` | 60 |
| `email` | `POST /auth/password/forgot`, `POST /auth/password/reset`, `POST /auth/email/verify/request`, `POST /auth/email/verify` | 10 |
| `invite` | `POST /auth/invites/preview`, `POST /auth/invites/accept` | 20 |
| `general` | every other request under `/auth/` | 300 |

- A request counts against exactly one policy, chosen by method and path (path compared
  without regard to case or a trailing slash). The window is a sliding window of one
  minute in 6 segments of 10 seconds, counted by Auth-Core's own limiter on the injected
  `TimeProvider` (Decision 13): the framework's sliding-window limiter does not report
  how long to wait.
- The numbers are settings (`Auth:RateLimit:<Policy>:PermitPerMinute`, at least 1) with
  the values above as defaults; `Auth:RateLimit:Enabled` (default `true`) turns the
  limiter off. A blank value means the default; an invalid one stops the host with a
  message naming the key, as other settings do. The test host turns the limiter off
  unless a test turns it on; the development compose passes the settings from `.env`.
- Over the limit: `429` with `{"error": "too_many_requests", "retry_after_seconds": <n>}`,
  `Retry-After: <n>` and `Cache-Control: no-store`. `<n>` is the number of seconds until
  the window holds fewer requests than the limit, at least 1. The request does no other
  work: no password is evaluated, no lockout streak changes, no mail is queued. A
  request over the limit is not counted.
- The counters live in memory: one instance per product (ADR 0001); a restart clears
  them. `GET /auth/health` and `GET /auth/.well-known/jwks.json` are under `general`.
- The per-identifier lockout of spec 0003 and the mail limits of specs 0004 and 0005
  stay as they are; a request must pass both.

### `POST /auth/refresh` during an outage (changes spec 0002)

- Today a refresh with the database down is a `500` with an empty body: the first
  database read happens while OpenIddict authenticates the request, before any handler
  of ours, and the exception escapes.
- A refresh that fails for a reason that is not the client's — OpenIddict reports
  `server_error`, or the exception holds (at any depth) a transient `DbException`, a
  `TimeoutException` or a `SocketException` — answers `503` with
  `{"error": "temporarily_unavailable"}`, `Retry-After: 5` and `Cache-Control: no-store`,
  and no `Set-Cookie`. The cookie is neither cleared nor rotated; once the database is
  back, the same cookie refreshes.
- Every other error stays as spec 0002 has it: `401 invalid_grant` for a missing,
  unknown, expired, revoked or reused cookie; `400 invalid_request` for a wrong method.
- Login, logout and the company API keep a `500` for an outage, now written by the
  handler of the next section.
- Residual risk: a failure exactly between marking the old refresh token redeemed and
  storing the new one leaves the cookie usable for the 15-second reuse leeway of spec
  0002 only; a retry after that is reuse, and the session ends.

### Unhandled errors

- An unhandled exception anywhere is answered by Auth-Core's own outermost handler —
  in every environment, the developer exception page included — with `500` and
  `{"error": "internal_error"}`, and the headers of the next section; the exception is
  logged. (Kestrel's own `500` drops every header set before.) The `503` of a refresh
  is decided in the same place.
- Kestrel's answers before the pipeline (a malformed request line, headers too large)
  cannot carry the headers; they are out of reach.

### Security headers

On every response of Auth-Core, whatever its status, including the framework's `404`
and `405`:

| Header | Value |
| --- | --- |
| `X-Content-Type-Options` | `nosniff` |
| `X-Frame-Options` | `DENY` |
| `Content-Security-Policy` | `default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'` |
| `Referrer-Policy` | `no-referrer` |
| `Cross-Origin-Resource-Policy` | `same-origin` |
| `Cache-Control` | `no-store` (plus `Pragma: no-cache`), except on the key set and the OpenAPI document |

- The `Server` header is not sent (Kestrel's `AddServerHeader = false`).
- `GET /auth/.well-known/jwks.json` and `GET /auth/openapi/v1.json` send no
  `Cache-Control` today and get none; they get every other header of the table.
- Everything under `/auth/scalar` (Development only) gets instead:
  `default-src 'none'; script-src 'self' 'nonce-<per request>'; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'`,
  with Scalar's nonce, its default fonts, telemetry and agent turned off.
- Auth-Core sends no `Strict-Transport-Security`: TLS ends at the proxy, which sends it.
- The no-store middleware of spec 0005 is replaced by this one.

### Fixes left to hardening

- **`/auth/health`** answers `GET` and `HEAD`; any other method is `405` with
  `Allow: GET, HEAD`.
- **JSON charset.** A request body declared `application/json` with a `charset` other
  than `utf-8` (any case) is `415` with `{"error": "unsupported_media_type"}`, for
  `POST`, `PUT`, `PATCH` and `DELETE` under `/auth/` other than refresh and logout
  (which read no body). It is checked before the request reaches OpenIddict, so login is
  covered. A missing `charset` means UTF-8, as now.
- **Invitation domains** (changes spec 0005 As built). The last label of the domain
  as it reaches the relay must be two or more ASCII letters, or `xn--` and more; the
  last label as typed must be two or more letters (of any script, so `x@пример.рф` still
  passes). So `127.0x1`, `10.0.0.5`, `0x7f.1` and `host.123` are refused with
  `400 invalid_request`, in the API and in the CLI. The rules already in place stay.
- **Second development seed user.** Its role is the first role of the development
  company, by name in ordinal order, that holds neither `members:manage` nor `*` — read
  from the roles stored in the database, not from the manifest. (The order in which
  roles were stored cannot be read back: their ids are random.)
- **Data Protection** keys are kept in memory: nothing of Auth-Core uses them, and the
  production container's file system is read-only.

### Audit log

- Table `audit_events`, written by Auth-Core only:

| Column | Holds |
| --- | --- |
| `id` | a `uuid` |
| `occurred_at` | UTC time |
| `kind` | one of the kinds below |
| `actor_user_id` | the account that acted; null for the operator CLI, an anonymous request or a failed login |
| `subject_user_id` | the account acted on, when there is one |
| `subject_email` | the address involved, as stored or (for a failed login of an unknown address) as typed, at most 256 characters |
| `org_id`, `org_name` | the company involved, with its name at that moment |
| `target_id` | the role or invitation involved |
| `client_ip` | the client address (null for the CLI) |
| `details` | a small JSON object: for example the old and the new role, or `"via": "cli"` |

- No foreign keys: a row outlives the account, company, role or invitation it names.
- **Never recorded:** a password, a token, a link, a cookie, a mail body.
- **Kinds:** `login.succeeded`, `login.failed` (wrong password, unknown address,
  unconfirmed address, no company — the reason in `details`), `login.locked`, `logout`,
  `refresh.reuse_detected`, `password.reset_requested`, `password.reset`,
  `email.verified`, `invite.sent`, `invite.resent`, `invite.accepted`,
  `invite.cancelled`, `member.removed`, `member.role_changed`, `role.created`,
  `role.updated`, `role.deleted`, `org.created`, `org.renamed`, `org.deleted`,
  `org.delete_refused` (wrong password, or locked), `rate_limit.hit`.
- `logout` names the account the cookie's session belongs to, looked up in the token
  store; a logout with no valid cookie writes nothing. `refresh.reuse_detected` is
  written where OpenIddict finds a redeemed token presented again after the leeway.
  `password.reset_requested` holds the address as typed and no account: the request
  does not look the account up (spec 0004).
- `rate_limit.hit` is written at most once per client address and policy per minute,
  and its write never fails the request.
- A change and its row are written in the same transaction: either both or neither.
  Events that change nothing else (a failed login, a rate-limit hit) are written on
  their own.
- **Retention.** A pruning service deletes rows older than `Auth:Audit:RetentionDays`
  (default 90, at least 1) every hour, as the other pruning services do.
- **Reading** is by SQL; the backup runbook carries the queries for "everything about
  an account", "every change in a company" and "failed logins from an address".

### Company deletion

- **Built-in permission `org:delete`**, a fourth one next to `members:manage`,
  `roles:manage` and `org:manage`. A role holding `*` holds it. It is in the catalog
  whether or not the manifest lists it, so the `permissions` claim of every token whose
  role holds `*` gains `org:delete`; the development manifest, the integration guide
  and the tests that list the catalog follow.
- **`DELETE /auth/org`** with `{"name", "password"}`, for a member holding `org:delete`:

| Answer | When |
| --- | --- |
| `204` | the company is deleted |
| `403 forbidden` / `403 permissions_changed` | the caller lacks `org:delete`, or the token no longer matches the database — as on every company endpoint |
| `400 invalid_request` | the body is malformed, or `name` is not exactly the company's name (ordinal comparison) |
| `429 too_many_attempts` | the caller's identifier is locked (spec 0003) |
| `403 wrong_password` | the password is wrong; this counts in the caller's login streak as a failed login does |
| `403 permission_not_held` | a member of the company holds a permission the caller does not (safety rule 1 of spec 0005: deleting acts on every member) |

  Checks run in the order of the table: permission, body, lockout, password, name,
  rule 1. The password is evaluated once, as login does it: the attempt is counted
  first, a locked identifier is `429`, a correct password ends the streak.
- **CLI:** `auth-server admin delete-org --org <id> --confirm <name>`. Refused with
  `error: invalid_request` when `--confirm` is not exactly the name; exit codes as in
  spec 0005. The operator is not bound by rule 1.
- **Effects,** under the company lock, in one transaction: the queued mails of the
  company's invitations, the invitations, the memberships, the roles and the company
  itself are deleted, every member's sessions are revoked (as removing a member does),
  and an `org.deleted` row is written. Accounts stay.
- **What a member sees** (Decision 12) — the same as a member who was removed: an access
  token still held gets `403 permissions_changed` at the company API at once; the next
  refresh is `401 invalid_grant`, so the app signs the person out within the token's
  10 minutes; a new login is `403 no_membership`. A pending invitation link answers
  `invalid_token`.

### Production compose

- `deploy/docker-compose.prod.yml`, a file of its own (not an overlay), with:
  - `auth`: image `ghcr.io/mckcieply/auth-core:${AUTH_CORE_VERSION:?}`,
    `ASPNETCORE_ENVIRONMENT=Production`, `Auth:Database:MigrateOnStartup=true`, no seed
    users, no Scalar; `read_only`, `tmpfs: /tmp`, `cap_drop: [ALL]`,
    `security_opt: [no-new-privileges:true]`; port `127.0.0.1:${AUTH_PORT:-8080}:8080`;
    a named network that a containerised proxy may join from its own compose file (a
    proxy on the host needs nothing of it).
  - `postgres`: pinned by digest, no published port, a named volume, the health check
    over TCP.
  - A network with a fixed subnet (`${AUTH_SUBNET}`, with a default), so that the
    trusted proxy can be named: a proxy on the host reaches the published port from
    that network's gateway.
  - Every setting Production requires, from `.env` and mounted files, with no default
    for any secret: the connection string, the four key paths, the token issuer and
    audience, the manifest path, the app name, locale and the three frontend URLs
    (https), the sender and the SMTP relay (TLS required in Production), the trusted
    proxies, the rate limits and the audit retention.
- `deploy/.env.prod.example` lists and explains every variable.
- `/auth/health` does not reach the database; the guide waits for a login or for the
  CLI to succeed, not for the health check, before calling the database "up".
- The base images of `src/Auth.Server/Dockerfile` are pinned by digest; the version and
  revision labels come in as build arguments (`.git` is not in the build context).
- The development compose pins `postgres` and `mailpit` by digest and passes the rate
  limit settings from `.env`; otherwise unchanged. Mailpit's API stays unauthenticated
  on loopback (development only).

### Sample proxies

- `samples/notes-web/Caddyfile`, on the app's routes: adds `X-Frame-Options: DENY`,
  `Cross-Origin-Opener-Policy: same-origin`, `Permissions-Policy` denying camera,
  microphone, geolocation and payment; `Strict-Transport-Security: max-age=31536000`
  on the HTTPS listener only. The existing Content-Security-Policy stays.
- `samples/notes-api/Caddyfile`: `X-Content-Type-Options: nosniff` and
  `X-Frame-Options: DENY` on `/api/*`.
- Both pass the client address to Auth-Core, and their compose files set Auth-Core's
  trusted network to the proxy's.

### Angular sample

- A `429 too_many_requests` from any endpoint shows the notice "Try again in N s." and
  does not sign the person out.
- Tests pin that a refresh answered `503 temporarily_unavailable` or `429` keeps the
  session.

### OpenAPI and the Python package

- The OpenAPI description adds `DELETE /auth/org` with its answers, `429
  too_many_requests` with `Retry-After` on every endpoint under a limit, `415` on the
  endpoints with a JSON body, `503 temporarily_unavailable` on refresh, and the
  permission `org:delete`.
- The Python package needs no change of behaviour: a `429` or `503` from the key set is
  a failed fetch, which its key cache already handles.

### Documents

- `docs/security/threat-model.md` — STRIDE per element (browser and SPA, proxy,
  Auth-Core, PostgreSQL, SMTP relay, key files, product backend, operator CLI): threat,
  mitigation, accepted residual risk. Every residual risk accepted in specs 0002–0007
  appears here, with the two of Decision 3.
- `docs/operations/backup.md` — what to back up (database, key files, manifest, `.env`),
  a nightly `pg_dump` example, restore step by step, the audit queries.
- `docs/operations/key-rotation.md` — planned and emergency rotation: generating
  production keys, the swap, the effect (everyone signed out; product backends see the
  new `kid` at their next key fetch), checking JWKS.
- `docs/deployment/vps.md` — the production compose, a `Caddyfile` for HTTPS with
  HSTS, the headers and the client address, the first company from the CLI, upgrading
  to a new version.
- `README.md` rewritten for `v0.1.0`; `CHANGELOG.md` with its `0.1.0` entry.
- `clients/python/`: `license = "MIT"`, the licence file and a readme in the package
  metadata; version `0.1.1`.

### Release

- Annotated tag `v0.1.0` on the merge commit; annotated tag `python-v0.1.1` on the
  same commit. `python-v0.1.0` is not moved.
- The image is built from the tag with OCI labels (`source`, `version`, `licenses: MIT`,
  `revision`) and pushed as `ghcr.io/mckcieply/auth-core:0.1.0` and `:latest`, as a
  public package. The owner logs in to GHCR and makes the repository and the package
  public.

## Acceptance criteria (Done when)

1. 31 `POST /auth/login` from one address within a minute: the 31st is `429
   too_many_requests` with `Retry-After`, and evaluates no password. The same holds for
   each policy at its own number.
2. With no trusted proxy, a client-supplied `X-Forwarded-For` changes neither the
   partition nor the recorded address. With the proxy's network trusted, two clients
   behind it are counted separately.
3. A refresh while PostgreSQL is stopped is `503 temporarily_unavailable` with
   `Retry-After: 5`; after PostgreSQL starts, the same cookie refreshes (`200`).
4. Every Auth-Core response from the pipeline — `200`, `401`, `404`, `405`, `415`,
   `429`, `500`, `503` — carries the headers of the table and no `Server` header (the
   last proven on a live stack); JWKS and the OpenAPI document carry no `Cache-Control`;
   an unhandled exception is `500 internal_error`.
5. `POST /auth/health` is `405` with `Allow: GET, HEAD`; `HEAD` is `200`.
6. A JSON body declared `charset=utf-16` is `415 unsupported_media_type` on every
   endpoint that reads a JSON body (login, the email flows, the invitations, the
   company API); a body with `charset=utf-8` or with no charset is read as before.
7. An invitation to `x@127.0x1`, `x@host.123` or `x@0x7f.1` is `400 invalid_request` in
   the API and the CLI; `x@example.pl`, `x@żółw.pl`, `x@пример.рф` and
   `x@xn--e1afmkfd.xn--p1ai` pass the domain rule.
8. Each audit kind is written by its flow, with the columns the contract names; no row
   holds a password, a token or a link; a failed change leaves no row.
9. Rows older than the retention are pruned; newer rows stay.
10. `DELETE /auth/org` follows the table, in the order given; a wrong password counts in
    the lockout streak; after `204` the members' access tokens get `403
    permissions_changed`, their refresh is `401 invalid_grant`, their login `403
    no_membership`, the invitation links are `invalid_token`, and no queued mail of the
    company is sent.
11. `delete-org` deletes with the exact name and refuses otherwise.
12. `deploy/docker-compose.prod.yml` starts from an empty volume with an image built
    locally under the GHCR name, migrates, serves login and refresh through a proxy,
    and refuses to start without its secrets.
13. The backup runbook, followed literally, restores a dropped database; a refresh
    cookie issued before the backup still refreshes.
14. The Angular sample shows "Try again in N s." on `429 too_many_requests` and keeps
    the session on refresh `503` and `429`.
15. The sample proxies send the headers of the contract; HSTS only over HTTPS.
16. All earlier e2e scripts (0001–0007), the .NET tests, the package and sample tests
    pass; `scripts/e2e-hardening.sh` covers criteria 1, 3, 4, 5, 6, 8 and 10 on a live
    stack.
17. A secret scan of the whole history and a known-vulnerability check of the .NET,
    npm and Python dependencies find nothing unaddressed.

## Decisions (owner, 2026-02-19)

1. **Release without CI.** The image is built locally from the tag and pushed to GHCR
   once, by hand; no GitHub Actions. The checks design.md gives to CI (build and test,
   static analysis, secret scan, dependency updates) run locally before the merge, as
   `docs/workflow.md` has it.
2. **The GHCR package is public**, as the repository will be.
3. **What earlier slices left to hardening:** fixed — `/auth/health` methods, the JSON
   charset, hexadecimal and numeric domains, headers on framework `404`/`405`; kept as
   final — `405` on `GET /auth/logout` against `400` on login and refresh, pruning at
   start, `AccessTokenClaimFilter`, the copied pruning service, the edited slice 2 tests;
   already fixed by spec 0004 — the U+FFFE email; accepted as residual risks — the login
   streak reset a prober can observe (spec 0003 E2), fullwidth and Cyrillic look-alike
   invitation addresses.
4. **A refresh during an outage keeps the person signed in** (`503`, not `401`).
5. **Audit log** in the database, every event of the list, the client address, 90 days,
   read by SQL.
6. **Security headers** in Auth-Core and in the sample proxies, plus a `Caddyfile` for
   the server in the deployment guide.
7. **Key rotation is a runbook only:** a key change signs everyone out. No "previous
   keys" support.
8. **Company deletion** by the operator CLI and by the company API, guarded by the
   permission `org:delete`, the company's name and the caller's password.
9. **A production compose file** of its own; the runbooks are written for it and
   tried on it.
10. **Per-IP rate limiting** in this release, with the numbers of the contract.
11. **The Python package is MIT**, as the repository; its metadata change is released as
    `0.1.1`.
12. **The members of a deleted company are treated as removed members:** signed out at
    their next refresh, `no_membership` at login. There is no "signed in without a
    company" state.
13. **The rate limiter is Auth-Core's own** sliding window, so that the `429` can say
    exactly how long to wait; the framework's sliding window cannot, and its fixed
    window lets twice the limit through around the turn of a minute.

## Deferred / follow-ups

- Rotation without signing everyone out (previous keys kept for verification and
  decryption); automatic rotation.
- An audit viewer; exporting the audit log.
- Rate-limit counters shared between instances (only needed with more than one).
- CI, should the project ever take contributions.

**Residual risks** (also in the threat model):

- A key change signs everyone out (Decision 7).
- A distributed guesser below the per-IP limits on many addresses is stopped only by
  the per-identifier lockout, which an attacker can use to keep a chosen account
  locked (spec 0003).
- Many people behind one address share its limits; 30 logins a minute is the ceiling
  for an office behind one NAT.
- A company admin holding every permission can delete the company with their password;
  there is no undo but the backup.
- A refresh interrupted by an outage between its two writes ends the session if it is
  retried after the 15-second leeway.
- The address typed into a failed login is stored as typed, and may be a password
  pasted into the wrong field; it is kept for the retention period.
- Anyone can create `login.failed` and `password.reset_requested` rows; only the per-IP
  limits and the retention bound the table.

## Verification notes (for the local verifiers)

- Realization vs spec: every criterion mapped to a named test or e2e step.
- E2E: `scripts/e2e-hardening.sh` and every earlier script on a clean stack; the
  production compose started locally, in Production, through a test overlay in
  `scripts/`: the image built under the GHCR name, Mailpit as the relay with STARTTLS
  and a test certificate authority the container trusts, and frontend URLs on
  `https://localhost:8443`; the backup runbook followed literally.
- Earlier e2e scripts that exceed the default limits (`e2e-lockout.sh` makes about 58
  logins a minute; `e2e-email.sh` exactly 10 email requests) run with the limiter off or
  raised through `.env`; `e2e-hardening.sh` keeps the defaults and uses a new unknown
  address for each login, so that the lockout's `429` does not hide the limiter's.
- Security: the headers on error paths; the trusted-proxy rule against spoofed
  `X-Forwarded-For`; the audit log never holding secrets; `DELETE /auth/org` against
  rule 1 and the lockout; the secret scan of the history and the dependency checks.
- Only one Auth-Core stack at a time on this machine (ports 8080, 8025, 8088, 8443); a
  `COMPOSE_PROJECT_NAME` of its own.

## To verify during implementation

Answered by the technical probe of 2026-02-19 and folded into the contract above: the
refresh outage path, the forwarded-headers and limiter APIs of .NET 10, the `Server`
header, the scripts over the limits, Scalar's policy, the keys between the company
tables, the JSON readers, the audit hooks, Production's required settings, the image
digests. Still to verify:

- That deleting in the order invitations' mails → invitations → memberships → roles →
  company passes the `Restrict` keys from memberships and invitations to roles on
  PostgreSQL.
- Where OpenIddict detects a redeemed refresh token presented again, and how to record
  `refresh.reuse_detected` there.
- That Mailpit with STARTTLS and a test authority trusted through `SSL_CERT_FILE` works
  with the chiseled image and MailKit.
- That the production container starts with a read-only root file system.
- The secret scan (gitleaks v8.30.1, pinned by digest; its eight findings in history are
  a test password of the Angular sample, to be listed in `.gitleaksignore`) and the
  dependency checks (`dotnet list package --vulnerable --include-transitive`,
  `npm audit`, `pip-audit` 2.10.1), which query public advisory databases.
