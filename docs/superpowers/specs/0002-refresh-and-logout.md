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
- Ending every session — revoking all of a user's refresh tokens — when their
  password changes. **To be picked up** by the password forgot/reset spec (Week 2),
  where a password first changes; until then a refresh only checks that the user
  still exists (Decision 11).
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
   invalid_grant` (it was consumed) once the grace window of criterion 4 has passed.
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

## Decisions (owner, 2026-01-13)

Taken after slice 1 (spec 0001 as built), before plan 0002. None changes the scope
or the contract above; each records how the contract is met, or tightens wording.

9. **Reference refresh tokens.** The cookie carries an opaque reference; the token
   payload stays in the server-side store. Why: a small cookie, and logout can find
   and revoke the family by reference without running the token through the
   OpenIddict pipeline.
10. **Lifetimes are constants, not configuration:** sliding 14 days, absolute cap 30
    days, grace 15 s — the same stance as the access-token lifetime in spec 0001
    (part of the contract, not a knob). The cap is enforced from a session-start
    claim carried only in the refresh token; each rotated token lives for the
    shorter of the sliding window and the time left to the cap, and the cookie's
    `Max-Age` equals that lifetime. Consequence: criterion 9 cannot be waited out
    over real HTTP; it is proven by integration tests against real PostgreSQL with a
    controlled clock, and the verification note below is amended accordingly.
11. **A refresh checks only that the user still exists.** A missing user gets the
    same `401 invalid_grant`. Invalidating sessions on a password change is out of
    scope here and owned by the forgot/reset spec (see Out of scope).
12. **Criterion 2 wording tightened** to "once the grace window has passed": as
    first written it contradicted criterion 4.
13. **Pruning is an hourly in-process background job**, with no scheduler
    dependency. It removes token and authorization entries older than the sliding
    window (14 days). The threshold is deliberate: a consumed refresh token's entry
    must stay in the store, or presenting it again would no longer revoke the
    family. This also closes spec 0001's known gap (one `OpenIddictTokens` row per
    login, never removed).
14. **Logout does not invalidate an already-issued access token.** It is verified
    offline (ADR 0003) and stays valid until its own expiry, at most 10 minutes.
    Accepted.
15. **Only a successful refresh (rotate) and a logout (clear) write the cookie.** A
    failed refresh leaves it untouched. A wrong HTTP method on `/auth/refresh` is a
    malformed request, not a refresh failure: `400 invalid_request`, as on login.
16. **Login (spec 0001) changes only by adding the `Set-Cookie`.** Its response body
    and the access token's claim set stay exactly as built.

## Verification notes (for the local verifiers)

Per [`docs/workflow.md`](../../workflow.md) — verifiers run locally before merge.

- **realization vs spec:** map each acceptance criterion to its guarding test. The
  reuse-detection and grace-window behaviours (criteria 2–4) are the subtle ones —
  test both the punished path (reuse revokes family) and the forgiven path (grace).
- **API / e2e:** drive the real endpoints over HTTP with a cookie jar (the `curl`
  sequence in [Goal](#goal)), against a freshly started instance. Criterion 3
  (family revocation) needs real token state, not an in-process client: wait out
  the grace window, then replay the consumed token. Criterion 9 (absolute cap)
  cannot be waited out over HTTP; it is covered by integration tests with a
  controlled clock (Decision 10). Exercise rotation across a genuine process
  restart to confirm the server-side store persists.
- **security:** the refresh token never appears in a response body or a log; cookie
  flags exactly as ADR 0004; confirm `SameSite=Strict` is actually set (the CSRF
  mitigation the MVP relies on); no secret in the diff.

## To verify during implementation

- That OpenIddict 7's native **rolling refresh tokens and reuse leeway** deliver
  criteria 2–4 as configuration. Reading its source says they do; the first tests
  must confirm it. If they do not, re-raise the grace decision with the owner
  rather than silently dropping criterion 4.
- That a password-flow sign-in creates an **ad-hoc authorization**, which serves as
  the token family for reuse revocation and for logout.
- A way to issue the refresh token at login **without adding a `scope` claim** to
  the access token, whose claim set is pinned by a spec 0001 test (Decision 16).
- That the **pruning** threshold (Decision 13) leaves consumed entries in place
  long enough for reuse detection, and that pruning removes what it should.
- That the cookie `Max-Age` tracks the refresh token's own lifetime as the session
  approaches the absolute cap (Decision 10).

## As built (owner, 2026-01-18)

Recorded after implementation and local verification (plan 0002,
[acceptance map](../plans/0002-acceptance-map.md)). What entered this stage stays
in it; this section records it rather than rewriting the decisions above.

**"To verify during implementation", resolved:**

1. Rolling refresh tokens and the reuse leeway are OpenIddict 7.7.1 configuration.
   Criteria 2–4 hold with no custom reuse handling, so criterion 4 stands.
2. A password sign-in that issues a refresh token creates an ad-hoc authorization;
   that is the family. Rotation stays in it; reuse outside the grace window revokes
   its tokens; logout revokes the authorization and every token under it.
3. The refresh token is issued without the `offline_access` scope, so no `scope`
   claim appears. Not foreseen: once an authorization exists, OpenIddict adds its
   id (`oi_au_id`) to the access token. A handler removes it there, which keeps the
   claim set of spec 0001 exact (Decision 16).
4. Pruning removes only entries created before the 14-day threshold, so a consumed
   entry younger than that stays and reuse detection keeps working. A revoked
   authorization stays until its newest token is 14 days old; then both go.
5. The cookie's `Max-Age` equals the refresh token's own lifetime, including the
   shortened one near the cap. OpenIddict takes its clock from the host, which is
   what lets the lifetime tests run on a controlled clock.

**Contract as built.** The refresh response is compact JSON — exactly
`{"access_token":"…"}` or `{"error":"invalid_grant"}` — with `Cache-Control:
no-store`. The cookie value is a 43-character opaque reference; the store holds only
its hash.

**Behaviour the spec was silent on:**

- A wrong method on `/auth/logout` returns `405` (`Allow: POST`); on
  `/auth/refresh` it returns `400 invalid_request` (Decision 15).
- Every error on `/auth/refresh` other than that wrong-method case is answered with
  the uniform `401 invalid_grant`, including a server-side OpenIddict error. An
  unhandled exception is still a `500`.
- Pruning runs once at host start as well as hourly.
- Logout with an already-consumed token of the family still ends the family.
- A cookie value that is empty, longer than 256 characters or contains a control
  character is treated as no cookie.
- `/auth/refresh/` and `/auth/logout/` behave like the paths without the slash.
- The logout response carries `Cache-Control: no-store`.

**Delivered beyond the plan:** a `.gitattributes` rule that keeps shell scripts LF
on Windows checkouts, and the test host now releases its database connection pool.

**Known gaps, owned elsewhere:** ending every session on a password change → the
forgot/reset spec; rate limiting of refresh and logout → spec 0003; a CSRF token →
still deferred (Decision 6); within the grace window a stolen, just-rotated token
can start a second chain that reuse detection does not see (the trade-off of
Decision 2). The acceptance map lists the rest.
