# Spec 0002 — Refresh and logout

- **Status:** Accepted
- **Date:** 2026-01-04
- **Author:** Alex
- **Milestone:** Week 2 (account lifecycle) — the session half of
  *"the full account lifecycle is covered by integration tests."*
- **Context:** [`docs/design.md`](../../design.md) (Week 2), ADR
  [0004](../../adr/0004-same-origin-cookie-refresh.md) (same-origin cookie refresh),
  ADR [0003](../../adr/0003-openiddict-jwt-tokens.md), and spec
  [0001](0001-login-and-token-issuance.md) (login → JWT → JWKS).

## Goal

A signed-in user keeps their session without re-entering a password, and can end it
on demand — without ever exposing a long-lived token to JavaScript. Spec 0001 issues
a 10-minute access token held in memory; this spec adds the **refresh** that renews
it from an `HttpOnly` cookie, and the **logout** that revokes it.

Concretely, this is met when this sequence works against a running instance:

```
# login (spec 0001) sets the refresh cookie and returns an access token
curl -i -c jar.txt -X POST .../auth/login -d '{"email":"...","password":"..."}'

# refresh: cookie in, new access token out, cookie rotated
curl -i -b jar.txt -c jar.txt -X POST .../auth/refresh
# -> 200 {"access_token":"<new jwt>"}, Set-Cookie rotates the refresh token

# reusing the OLD (pre-rotation) refresh token is rejected and kills the family
curl -i -b jar.OLD.txt -X POST .../auth/refresh   # -> 401 invalid_grant

# logout revokes the current session and clears the cookie
curl -i -b jar.txt -X POST .../auth/logout        # -> 204, Set-Cookie cleared
```

## In scope

- `POST /auth/refresh` — refresh cookie → new access token; **rotates** the refresh
  token (new cookie) on every use.
- `POST /auth/logout` — revokes the current session's refresh-token family and
  clears the cookie.
- **Reuse detection**: presenting an already-consumed refresh token revokes the
  whole token family (forces re-login), softened by a short grace window for honest
  double-submits.
- **Server-side token store** (OpenIddict EF Core) so tokens can be rotated,
  revoked and reuse-detected.
- The refresh cookie's attributes per ADR 0004.

## Out of scope (later specs / weeks)

- Lockout, rate limiting, timing-equalised failure responses (own Week 2 spec).
- Email verification, password forgot/reset, SMTP, OpenAPI description (Week 2).
- "Log out everywhere" / session & device management (Beyond MVP).
- Organizations, `org_id`, RBAC, the `auth.yaml` manifest (Week 3).

## Contract

### Refresh cookie (set by login in spec 0001, rotated here)

`Set-Cookie: auth_rt=<token>; HttpOnly; Secure; SameSite=Strict; Path=/auth;
Max-Age=<sliding window>` — per ADR 0004. Never readable by JavaScript; only ever
sent to `/auth`.

### `POST /auth/refresh`

- Request: no body; the browser sends the `auth_rt` cookie automatically.
- Success — `200 OK`: body `{ "access_token": "<jwt>" }` (same claims/shape and
  10-min TTL as spec 0001), **plus** a `Set-Cookie` that rotates `auth_rt`. The
  refresh token is **never** in a response body.
- Failure — missing, expired, revoked, or reused token — `401 Unauthorized`,
  uniform body `{ "error": "invalid_grant" }` (no field distinguishes the cases).

### `POST /auth/logout`

- Request: no body; sends the `auth_rt` cookie.
- Success — `204 No Content`, with `Set-Cookie: auth_rt=; Max-Age=0` clearing the
  cookie; the current session's refresh-token family is revoked server-side.
- Called without a valid cookie: still `204` (idempotent; no enumeration).

## Acceptance criteria (Done when)

1. `POST /auth/refresh` with a valid refresh cookie returns `200`, a new usable
   access token, and a `Set-Cookie` whose refresh value **differs** from the one
   sent (rotation).
2. After a successful refresh, the **previous** refresh token is rejected with `401
   invalid_grant` (it was consumed).
3. Presenting a consumed refresh token **outside** the grace window revokes the
   whole family: the current (post-rotation) token is then also rejected, forcing
   re-login.
4. Within the grace window (~15 s), a second refresh with the just-rotated-from
   token still succeeds (an honest double-submit is not punished) — **verify this is
   achievable in OpenIddict** (see "To verify").
5. `POST /auth/logout` returns `204`, clears the cookie, and the refresh token used
   no longer works (`401` on a subsequent `/auth/refresh`).
6. `POST /auth/logout` with no/invalid cookie still returns `204` (idempotent).
7. All `/auth/refresh` failure modes (missing / expired / revoked / reused) return
   the **same** `401 { "error": "invalid_grant" }` body.
8. The refresh cookie is `HttpOnly; Secure; SameSite=Strict; Path=/auth`, and no
   refresh token ever appears in any response **body**.
9. A refresh token is usable within its **sliding** window (each use extends it) but
   never past the **absolute cap**, after which refresh returns `401` and the user
   must log in again.

## Decisions (owner, 2026-01-04)

1. **Scope = refresh + logout only.** Lockout, rate limiting and the email flows are
   separate Week-2 specs (0003+), each with its own tight acceptance set.
2. **Rotation + reuse detection.** Rotate on every use; reuse of a consumed token
   revokes the whole family. A **~15 s grace window** on the just-rotated token
   absorbs honest double-submits (retries, two tabs) without a meaningful theft
   window.
3. **Lifetime: sliding 14 days, absolute cap 30 days.** Active users stay signed in;
   a stolen token's value is bounded at 30 days regardless of activity.
4. **Server-side token store** (OpenIddict EF Core). The DB read/write per refresh is
   the accepted cost of having revocation and reuse detection at all.
5. **`/auth/refresh` returns a new access token in the body and rotates the cookie
   via `Set-Cookie`.** The refresh token is never in a body.
6. **CSRF: rely on `SameSite=Strict` + same-origin for the MVP.** A dedicated CSRF
   token is **deliberately deferred** — recorded here, not silently omitted.
7. **Logout = current session only.** Revoke this family, clear the cookie.
   "Log out everywhere" is Beyond MVP.
8. **Uniform `401 { "error": "invalid_grant" }`** on every refresh failure (same
   no-enumeration posture as spec 0001's login).

## Verification notes (for the local verifiers)

Per [`docs/workflow.md`](../../workflow.md) — verifiers run locally before merge.

- **realization vs spec:** map each acceptance criterion to its guarding test. The
  reuse-detection and grace-window behaviours (criteria 2–4) are the subtle ones —
  test both the punished path (reuse revokes family) and the forgiven path (grace).
- **API / e2e:** drive the real endpoints over HTTP with a cookie jar (the `curl`
  sequence in [Goal](#goal)), against a freshly started instance. Criterion 3
  (family revocation) and criterion 9 (absolute cap) need real token state, not an
  in-process client. Exercise rotation across a genuine process restart to confirm
  the server-side store persists.
- **security:** the refresh token never appears in a response body or a log; cookie
  flags exactly as ADR 0004; confirm `SameSite=Strict` is actually set (the CSRF
  mitigation the MVP relies on); no secret in the diff.

## To verify during implementation

- Whether OpenIddict 7 exposes **rotation, reuse detection and a reuse grace
  window** as configuration, or whether the grace behaviour (criterion 4) needs a
  custom handler. If a native grace window is not available, re-raise the grace
  decision with the owner rather than silently dropping criterion 4.
- A **pruning** strategy for expired/consumed refresh tokens in the store
  (background job vs on-write cleanup).
- Keeping the cookie `Max-Age` in sync with the token's own sliding/absolute expiry.
