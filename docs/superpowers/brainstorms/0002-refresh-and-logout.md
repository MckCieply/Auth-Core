# Brainstorm 0002 — Refresh and logout (session lifecycle)

- **Status:** Brainstorm (exploration — **not** a spec)
- **Date:** 2026-01-04
- **Author:** Alex
- **Feeds:** spec 0002 (to be written after this settles)
- **Context:** [`docs/design.md`](../../design.md) (Week 2), ADR
  [0004](../../adr/0004-same-origin-cookie-refresh.md) (same-origin cookie refresh),
  ADR [0003](../../adr/0003-openiddict-jwt-tokens.md), and spec
  [0001](../specs/0001-login-and-token-issuance.md) (login → JWT → JWKS).

## Purpose of this document

Spec 0001 skipped an explicit brainstorm because the product decisions were
already in `design.md`. Spec 0002 has **real design choices** with trade-offs, so
this document explores them first. It ends with a recommended direction and a list
of decisions to confirm — the spec formalizes what we agree here; it does not
re-open it.

## The capability under consideration

Spec 0001 hands the SPA a short-lived (5–10 min) access token held in memory. That
token expires fast on purpose. Something has to let a returning user keep a session
without re-entering a password: a **refresh** flow. And a user must be able to end
a session on demand: **logout**. This is the direct continuation of spec 0001 and
the first item of Week 2.

## Scope options for spec 0002

- **Option A — refresh + logout only (recommended).** `POST /auth/refresh`
  (rotation + reuse detection, `HttpOnly` cookie) and `POST /auth/logout`
  (revocation). Smallest slice that closes the session loop; nothing in it depends
  on email/SMTP.
- **Option B — A + lockout + rate limiting.** Adds brute-force defenses on
  `/auth/login`, `/auth/refresh`, and the (future) password endpoints. Coherent
  "abuse resistance" theme, but lockout couples to the account-lifecycle work and
  bloats the slice.
- **Option C — all of Week 2.** Refresh/logout + lockout + rate limiting + SMTP +
  email verification + password reset + OpenAPI. Too big for one spec; verification
  becomes unfocused.

**Recommendation: A.** Keep spec 0002 to the token session lifecycle. Lockout,
rate limiting and the email flows become their own specs (0003+), each with a tight
acceptance set. This mirrors spec 0001's "one buildable slice" discipline.

## Key design questions & trade-offs

The meat of the brainstorm. Each has a leaning, marked **→**, to confirm before the
spec.

1. **Rotation + reuse detection.** Rotate the refresh token on every use; the old
   one is consumed. If a **consumed** refresh token is presented again, treat it as
   theft and **revoke the whole token family** (all descendants), forcing re-login.
   - Trade-off: strict revocation vs false positives from legitimate double-submits
     (a flaky network retry, two tabs, a mobile app resuming). Too strict logs
     honest users out; too lax defeats the point.
   - **→** Rotate + revoke-family on reuse (this is `design.md`'s stated rule).
     Mitigate races with a small grace window (see #7), not by weakening it.

2. **Refresh token lifetime — sliding vs absolute.** Sliding: each use extends the
   window (good UX, "stay logged in while active"). Absolute: a hard cap regardless
   of activity (bounds a stolen token's value).
   - **→** Sliding window **with an absolute cap** (e.g. slide 14 days, cap 30–90
     days). Numbers to pin in the spec.

3. **Storage & revocation model.** Reuse detection and logout-revocation require
   the server to know a token's state → use OpenIddict's **EF Core token store**
   (server-side reference/refresh tokens), not stateless-only refresh.
   - Trade-off: a DB read/write per refresh (and a cleanup job for expired rows) vs
     the ability to revoke and detect reuse at all.
   - **→** Server-side token store. Accept the DB cost; it is what makes #1 and
     logout real. Note: a pruning strategy for expired/consumed tokens is its own
     small concern (background prune vs on-write).

4. **Cookie mechanics.** ADR 0004 already fixes this: refresh token in an
   `HttpOnly; Secure; SameSite=Strict; Path=/auth` cookie, access token in memory.
   Open sub-question: **CSRF**. `SameSite=Strict` + same-origin (`/auth` under the
   app's domain) blocks cross-site POSTs, so a dedicated CSRF token is likely
   redundant for the MVP.
   - **→** Rely on `SameSite=Strict` + same-origin for the MVP; record CSRF as an
     explicit "considered, deferred" note rather than silently omitting it.

5. **Logout scope — this session vs all sessions.** Revoke just the current
   session's refresh family, or every session for the user?
   - **→** MVP: **current session only** (revoke this family, clear the cookie).
     "Log out everywhere" is a later feature (session management is Beyond-MVP in
     `design.md`).

6. **What `/auth/refresh` returns.** A fresh access token (same short TTL) plus a
   rotated refresh cookie (`Set-Cookie`). No refresh token ever in a response body.
   - **→** Body: `{ "access_token": "..." }`; rotation via `Set-Cookie` only.

7. **Concurrency / race grace.** With hard reuse-detection (#1), two near-
   simultaneous refreshes (double-submit) could nuke a valid session. Options: (a)
   no grace — strictest; (b) a short reuse grace window where the immediately-prior
   token still works for N seconds; (c) idempotent refresh keyed on a client nonce.
   - **→** Lean (b): a small grace window (e.g. 10–30 s) on the just-rotated token,
     which absorbs honest retries without opening a meaningful theft window. Confirm
     whether OpenIddict supports this natively or it needs custom handling —
     **verify at plan time**.

8. **Failure responses — no enumeration.** A missing/expired/revoked/reused refresh
   token all return the same `401` (uniform body), like spec 0001's login. No field
   tells the caller which case it was.
   - **→** Uniform `401 { "error": "invalid_grant" }`.

## Out of scope for spec 0002

Lockout, rate limiting, email verification, password forgot/reset, SMTP, OpenAPI
description (later Week-2 specs); organizations, `org_id`, RBAC, manifest (Week 3).

## Decisions to confirm before writing spec 0002

1. Scope = **Option A** (refresh + logout only)?
2. Lifetimes: sliding **14 d**, absolute cap **30–90 d** — pick the cap.
3. Reuse grace window: **yes, ~10–30 s** (pick a value), or **no grace** (strict)?
4. CSRF: rely on `SameSite=Strict` + same-origin for MVP (defer dedicated token)?
5. Logout = current session only for MVP?

## Things to verify at plan time (not blockers for the spec)

- Whether OpenIddict 7 exposes rotation, reuse detection and a reuse grace window as
  configuration, or whether the reuse/grace behavior needs a custom handler.
- Token-store pruning approach for expired/consumed refresh tokens.
- Exact refresh cookie `Max-Age` vs the token's own absolute expiry (keep them in
  sync).

## Recommended direction (one paragraph the spec would formalize)

Spec 0002 covers `POST /auth/refresh` and `POST /auth/logout` only. Refresh tokens
live server-side (OpenIddict EF Core token store), rotate on every use, and a reuse
of a consumed token revokes the whole family — softened by a short grace window for
honest double-submits. The refresh token rides the `HttpOnly; Secure;
SameSite=Strict; Path=/auth` cookie (ADR 0004); `/auth/refresh` returns only a new
access token and rotates the cookie. Lifetime is a sliding window under an absolute
cap. Logout revokes the current session's family and clears the cookie. All failure
paths return a uniform `401`. CSRF is handled by `SameSite=Strict` + same-origin for
the MVP, recorded as a deliberate deferral.
