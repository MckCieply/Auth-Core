# Spec 0001 — Login and token issuance

- **Status:** Accepted
- **Date:** 2026-01-04
- **Author:** Alex
- **Milestone:** Week 1 (foundation) — *"`curl` logs in and gets a JWT that
  verifies via JWKS."*
- **Context:** [`docs/design.md`](../../design.md) (key decisions, token contract),
  ADRs [0003](../../adr/0003-openiddict-jwt-tokens.md) (OpenIddict/JWT) and
  [0001](../../adr/0001-instance-per-project.md) (instance per project). The
  brainstorm behind this spec is those documents; this spec is the first
  buildable slice.

## Goal

A registered user can exchange email + password for a **signed JWT access token**
over HTTP, and any party can verify that token **offline** using the service's
published **JWKS**. This is the smallest end-to-end proof that the token engine,
signing keys and verification path work — the foundation every later capability
builds on.

Concretely, this milestone is met when this sequence works against a running
instance:

```
# 1. get a token
curl -X POST http://localhost:8080/auth/login \
     -H 'Content-Type: application/json' \
     -d '{"email":"user@example.com","password":"<correct>"}'
# -> 200 {"status":"authenticated","access_token":"<jwt>"}

# 2. fetch the public keys
curl http://localhost:8080/auth/.well-known/jwks.json
# -> 200 { "keys": [ { "kty":"RSA","use":"sig","kid":"...", ... } ] }

# 3. the JWT from step 1 verifies (RS256) against a key from step 2,
#    with no shared secret and no call back to the auth service.
```

## In scope

- `POST /auth/login` — email + password → JWT access token.
- `GET /auth/.well-known/jwks.json` — the public signing keys (JWKS).
- **RS256** signing via OpenIddict; **access token encryption disabled**
  (`DisableAccessTokenEncryption()`), so non-.NET consumers can read the JWT.
- Password verification through **ASP.NET Core Identity** (hashing, verify).
- **Persisted signing keys**: the private signing key is loaded from a mounted
  secret (not a per-process dev certificate), so a restart or a second replica
  serves tokens verifiable by the same JWKS.
- **Registered audience**: the access token's `aud` is a registered audience
  (`RegisterAudiences(...)`); OpenIddict 7 rejects unregistered ones.
- A minimal way to have a user to log in as (a seeded user, or the admin CLI's
  create-user path) — enough to exercise the flow, not the full account lifecycle.

## Out of scope (later specs / weeks)

- Refresh tokens, rotation, reuse detection, the `HttpOnly` cookie, `/auth/logout`
  (Week 2, [design.md](../../design.md) "Week 2").
- Lockout, rate limiting, no-user-enumeration hardening on failure responses
  (Week 2).
- Email verification, password reset, invites, SMTP (Week 2).
- Organizations, memberships, `org_id` claim, RBAC/permissions, the `auth.yaml`
  manifest (Week 3).
- Registration/self-service signup UI, any consumer integration (Weeks 4–5).

## Contract

### `POST /auth/login`

Request (`application/json`):

```json
{ "email": "user@example.com", "password": "..." }
```

Success — `200 OK`:

```json
{ "status": "authenticated", "access_token": "<jwt>" }
```

Failure — invalid credentials — `401 Unauthorized`, uniform body (same response
whether the email is unknown or the password is wrong — a first, deliberate step
toward the no-enumeration rule that Week 2 hardens fully):

```json
{ "error": "invalid_credentials" }
```

Malformed request (missing/blank field, not JSON) — `400 Bad Request`.

### Access token claims

Per the stable contract in [`design.md`](../../design.md) ("Access token claims").
This slice emits the identity claims only; `org_id`, `roles` and `permissions`
arrive in Week 3 and are **absent** here (not empty — absent):

```json
{
  "iss": "http://localhost:8080/auth",
  "aud": "auth-core-dev",
  "sub": "<user id>",
  "exp": 1700000600
}
```

- Algorithm: **RS256**. Header carries the `kid` of the signing key.
- Lifetime: **10 minutes** (short-lived; refresh comes in Week 2 — spec 0002).
- The token is a **signed, unencrypted** JWS — decodable by any JWT library.

### `GET /auth/.well-known/jwks.json`

- `200 OK`, `application/json`, a standard JWKS (`{ "keys": [ ... ] }`).
- Contains the **public** half of every current signing key, each with a `kid`
  that matches the `kid` in tokens it signed. **Never** exposes private key
  material.

## Acceptance criteria (Done when)

1. `POST /auth/login` with a correct email + password returns `200` and a JWT in
   `access_token`.
2. The returned JWT is a valid **RS256 JWS**: header `alg=RS256` + a `kid`, and it
   is **not** encrypted (a plain JWT decoder can read the payload).
3. The JWT's payload carries `iss`, `aud` (the registered audience), `sub` (the
   user's id) and a future `exp` no more than 10 minutes out.
4. `GET /auth/.well-known/jwks.json` returns a JWKS whose `keys` include the public
   key whose `kid` matches the token from criterion 1, and the token **verifies**
   against it with no shared secret and no call back to the service.
5. `POST /auth/login` with a wrong password **and** with an unknown email both
   return the **same** `401` body (`{"error":"invalid_credentials"}`); no field
   value distinguishes the two cases.
6. A malformed request (missing field, blank password, non-JSON body) returns
   `400`, not `500`.
7. **Persistence across restart:** a token issued before a full restart of the
   instance still verifies against the JWKS served after the restart (keys are
   loaded from the mounted secret, not regenerated per process).
8. JWKS **never** contains private key material (no `d`, `p`, `q`, `dp`, `dq`,
   `qi` members).

## Verification notes (for the local verifiers)

Grounded in [`docs/workflow.md`](../../workflow.md) — verifiers run locally before
the branch merges to `main`.

- **realization vs spec:** map each acceptance criterion above to the specific
  test that guards it. Watch layer 1 — the token contract in `design.md` is the
  authority; if a plan or the code lets a claim (e.g. `aud`) drift from it, the
  spec wins.
- **API / e2e:** drive the real running endpoint over HTTP (the `curl` sequence in
  [Goal](#goal)) against a **freshly started** instance — not an in-process test
  client. Criterion 7 requires a genuine restart between issuing and verifying.
  Verify the JWT with an independent library (ideally the Python
  `PyJWT`/`PyJWKClient` path a consumer will use), not only .NET, to prove
  cross-language verification.
- **security:** no private key or secret value anywhere in the diff, in logs, or in
  a response body; JWKS exposes public material only (criterion 8); the signing key
  comes from a mounted secret, never a committed file.

## Decisions (owner, 2026-01-04)

1. **First slice = `/auth/login` + JWKS only.** The repo skeleton (projects, EF
   Core, Docker) is bootstrap tracked separately; it is not part of this spec's
   acceptance criteria.
2. **A first no-enumeration step is in scope now** (the uniform `401` in criterion
   5). Full anti-enumeration hardening — lockout, rate limiting, timing-equalisation
   — stays in Week 2 (its own spec), not here.
3. **Access token TTL = 10 minutes.** (Within the design's 5–10 min range; pinned to
   the upper bound to minimise refreshes before spec 0002 lands refresh.)
4. **First user via a dev-only seed.** Enough to exercise the flow; real users come
   via the admin CLI / invites from Week 3. No self-service registration here.
5. **Dev identifiers (provisional):** issuer `http://localhost:8080/auth`, audience
   `auth-core-dev`. These are dev defaults; the production values are per-instance
   and confirmed when the repo skeleton and deployment land — a value change here
   does not change the contract's shape.

## To verify during implementation

- Signing-key file format for the mounted secret (PEM vs PKCS#12) — an
  implementation detail; does not change this contract.
- The exact OpenIddict 7 configuration to **disable access-token encryption**
  (`DisableAccessTokenEncryption()`) and **register the audience**
  (`RegisterAudiences(...)`), verified against the installed version.
- Whether the dev seed runs via the host startup or the admin CLI path (settle in
  the plan).

## As built (owner, 2026-01-10)

Recorded after implementation and local verification (plan 0001,
[acceptance map](../plans/0001-acceptance-map.md)). What entered this stage stays
in it; this section records it rather than rewriting the decisions above.

**Scope delivered beyond Decision 1.** The repo skeleton (.NET 10 solution, EF Core
on PostgreSQL with migrations, Testcontainers test host) and the container image,
compose stack and real-network e2e script were built in this slice (plan 0001
Tasks 1 and 9), because the acceptance criteria could not be exercised without
them.

**"To verify during implementation", resolved:**

1. Signing key format: **PEM** certificate + PKCS#8 key, mounted read-only. An
   **encryption** certificate is mounted the same way: OpenIddict requires one even
   with access-token encryption disabled, and refresh tokens (spec 0002) use it.
2. OpenIddict 7.7.1: `DisableAccessTokenEncryption()` and `RegisterAudiences(...)`
   exist as named. The access token's `aud` is filled from the principal's
   resources (`SetResources`); the registered audience and `aud` carry the same value.
3. Dev seed: at **host startup**, Development environment only.

**Token as built.** In addition to the contract's `iss`, `aud`, `sub`, `exp`, the
payload carries OpenIddict metadata `iat`, `jti`, `oi_tkn_id`; the header carries
`typ: at+jwt` and `x5t`, and the JWKS key carries `x5c`/`x5t` (public certificate
only). Accepted as OpenIddict-standard; the claim set is pinned by a test, so any
change is deliberate.

**Hardening added during local verification** (none changes the contract):
NUL/control characters in the login body → `400`; request body capped at 8 KiB;
`Authorization: Basic` ignored on the login endpoint (anonymous clients only), so
the `401` stays uniform; unique emails enforced; OpenIddict discovery endpoint
disabled; outside Development the issuer and audience must be configured
explicitly; RSA keys below 2048 bits rejected; `/auth/login/` (trailing slash)
treated as the login path; a token-endpoint request nothing claims → `400`.

**Consequence for spec 0002 (a fact of this implementation, not a scope change).**
The token endpoint uses a custom JSON extraction handler in place of OpenIddict's
form extraction. A refresh grant on the token endpoint therefore needs its own
extraction handler, ordered before `UnhandledTokenRequestGuard`. Plan 0001's
remark that refresh is "a one-line `SetTokenEndpointUris` change" is superseded.

**Known gaps, owned elsewhere:** timing difference between unknown email and wrong
password, lockout, rate limiting → spec 0003; whether unconfirmed emails may log
in → the email-verification spec (Week 2); one `OpenIddictTokens` row per login
with no pruning → spec 0002 (revocation) or a cleanup job.
