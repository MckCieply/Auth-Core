# Spec 0001 — Login and token issuance

- **Status:** Draft
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
- Lifetime: **5–10 minutes** (short-lived; refresh comes in Week 2).
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
   user's id) and a future `exp` within the 5–10 min window.
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

## Open questions

1. **Issuer value** — fixed to the instance's public origin (`.../auth`). For local
   dev, `http://localhost:8080/auth`; confirm the exact dev port/host once the repo
   skeleton lands.
2. **Seed vs CLI for the first user** — seed a dev user for the milestone, or
   require the admin CLI's create-user first? Leaning seed-for-dev-only, real users
   via CLI/invite from Week 3. To settle when writing the plan.
3. **Key format** — PEM vs PKCS#12 for the mounted signing key. To settle in the
   plan; does not change this contract.
