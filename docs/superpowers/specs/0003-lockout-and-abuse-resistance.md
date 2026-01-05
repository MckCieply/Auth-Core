# Spec 0003 — Lockout and abuse resistance

- **Status:** Accepted
- **Date:** 2026-01-05
- **Author:** Alex
- **Milestone:** Week 2 (account lifecycle) — the abuse-resistance item:
  *"lockout, rate limiting, uniform responses with no user enumeration."*
- **Context:** [`docs/design.md`](../../design.md) (Week 2; "Deferred within the
  MVP"), ADR [0004](../../adr/0004-same-origin-cookie-refresh.md) (same-origin
  proxy), and specs [0001](0001-login-and-token-issuance.md) (login) and
  [0002](0002-refresh-and-logout.md) (refresh/logout), whose endpoints this spec
  hardens.

## Goal

Make `POST /auth/login` resistant to password brute force without opening an
enumeration side channel and without locking out honest users. A human who
mistypes a few times is nudged, not punished; an automated guesser is slowed hard;
and an attacker learns nothing about which emails exist — from the response body or
its timing.

This spec **extends** the login behaviour defined in spec 0001; it does not
re-define the endpoint.

## In scope

- **Per-identifier lockout** on `POST /auth/login`: a failed-attempt counter keyed
  on the **submitted email** (normalised), tracked whether or not that email has an
  account.
- **Escalating cooldown** with an explicit client message and a cooldown timer.
- **Velocity awareness**: non-human-fast bursts of attempts on one identifier
  trip the lockout sooner than the human-pace threshold.
- **Timing equalisation** on login so an unknown email cannot be told from a wrong
  password by response time.
- Completing the **uniform / no-enumeration** posture on login (body + timing +
  lockout message all identifier-agnostic).

## Out of scope / Deferred

See [Deferred / follow-ups](#deferred--follow-ups) — **per-IP rate limiting** and
**trusted-proxy real-client-IP** are deliberately deferred and documented there,
not built here. Also out: CAPTCHA, breached-password check, bot detection, IP
allow/deny lists (all Beyond MVP); the email flows (verify, forgot/reset) and
OpenAPI (their own Week-2 specs).

## Behaviour

### Lockout (per submitted identifier)

- The identifier is the **normalised submitted email**. Attempts are counted for it
  **regardless of whether an account exists** — this is what keeps the lockout
  message from revealing account existence.
- **Human-pace threshold: 10 consecutive failed attempts** → the identifier enters
  cooldown.
- **Escalating cooldown:** the first lockout is a **base cooldown of 1 minute**;
  **each further failed attempt** (including attempts made while locked) **adds
  +1 minute**, up to a **cap of 30 minutes** (the cap bounds the targeted-DoS blast
  radius — see risks).
- A **correct** password **during** an active cooldown does **not** log in — it is
  refused like any other attempt while locked (otherwise the lock is meaningless).
- On cooldown expiry, the next attempt is allowed; a **successful** login **resets**
  the counter and clears the lock.

### Velocity awareness ("spam → faster lockout")

- Attempts arriving **faster than a human** on one identifier trip the lock before
  10. Rule: if the recent attempt **interval is implausibly short** (e.g. ≥ 5
  attempts within ~10 s — exact numbers pinned at plan time), the effective
  threshold drops (e.g. to 3) and the cooldown starts immediately.
- This is the only "rate" control in this slice, and it is **per identifier**, not
  per IP (per-IP is deferred).

### Client response on lockout

- A locked attempt returns an **explicit, actionable** response: a "too many failed
  attempts" message **and the remaining cooldown** (seconds remaining / a timer the
  UI can count down) — e.g. `429 Too Many Requests` with
  `{ "error": "too_many_attempts", "retry_after_seconds": <n> }` and a `Retry-After`
  header.
- **No enumeration:** because the lock is keyed on the submitted identifier whether
  or not it exists, this exact response appears for a hammered **unknown** email too.
  The message never says "account locked" (which would imply the account exists) —
  it says attempts on *this login* are temporarily blocked.

### Timing equalisation

- On the unknown-email path, login still performs a **dummy password-hash
  verification** against a fixed decoy hash, so an unknown email and a wrong password
  take ~the same time. (In ASP.NET Identity this is explicit: `FindByEmailAsync`
  returns `null`, so the null path must call `PasswordHasher.VerifyHashedPassword`
  against a precomputed decoy instead of short-circuiting.)
- Goal is to remove the **order-of-magnitude** gap, not cryptographic constant-time;
  "close enough" is the accepted bar (owner decision).

## Acceptance criteria (Done when)

1. After **10 consecutive failed** logins on an identifier, the next attempt is
   refused with the lockout response **even if the password is correct**, until the
   cooldown elapses.
2. The lockout response carries an explicit "too many attempts" error **and** the
   remaining cooldown (a `retry_after_seconds` / `Retry-After` the UI can show as a
   timer).
3. Each **further failed attempt** while locked extends the cooldown by **+1 minute**,
   never exceeding the **30-minute cap**.
4. A hammered **unknown** email produces the **same** lockout response and the
   **same** timing as a hammered existing email — nothing distinguishes the two
   (no enumeration via body or time).
5. A non-human-fast **burst** on one identifier trips the lock **before** 10
   attempts (per the velocity rule).
6. **Timing:** median response time for an unknown-email login is within tolerance of
   a wrong-password login (the unknown path runs the decoy hash verify).
7. After the cooldown expires, a **correct** login **succeeds** and the failed
   counter is **reset**; a successful login also resets the counter mid-streak
   (before any lock).
8. No response in any of the above reveals whether the email has an account.

## Decisions (owner, 2026-01-05)

1. **Per-identifier lockout** (submitted email, normalised), counted even for
   non-existent emails — so the lockout message does not enumerate.
2. **Threshold = 10** failed attempts at human pace; **base cooldown 1 min, +1 min
   per further failed attempt, capped at 30 min.**
3. **Velocity-aware:** implausibly fast bursts trip the lock sooner (exact burst
   numbers tuned at plan time).
4. **Explicit client feedback** on lockout (message + cooldown timer) — a deliberate
   UX choice over a silent/uniform-401 lockout, made safe by keying on the submitted
   identifier (#1).
5. **Timing equalisation: yes**, via a decoy hash verify on the unknown-email path;
   "close enough," not cryptographic constant-time.
6. **Per-IP rate limiting and trusted-proxy real-IP are deferred** (see below), so
   the only rate control shipped here is the per-identifier velocity rule.

## Deferred / follow-ups

> **Not built in this slice — must be built later.** Recorded here and in
> [`design.md`](../../design.md) ("Deferred within the MVP") so it is not lost.

- **Trusted reverse-proxy real-client-IP resolution** — `ForwardedHeaders`
  middleware restricted to the known proxy network (`KnownProxies`/`KnownNetworks`),
  so the app sees the real client IP instead of the proxy's, and cannot be spoofed
  via a client-supplied `X-Forwarded-For`.
- **Per-IP rate limiting** on `login` / `refresh` (and the future email endpoints) —
  depends on the item above; meaningless and spoofable without it.
- **Known residual risk until both land:** per-identifier lockout alone does **not**
  stop a **distributed / multi-IP** guesser spreading attempts across many accounts,
  and it leaves a **targeted account-lockout DoS** (an attacker can keep a specific
  user's identifier locked by failing its login) — bounded, not closed, by the
  30-minute cooldown cap. Closing these is the point of the deferred work.

## Verification notes (for the local verifiers)

Per [`docs/workflow.md`](../../workflow.md) — verifiers run locally before merge.

- **realization vs spec:** map each acceptance criterion to its guarding test. The
  no-enumeration pair (criteria 4 + 8) needs tests that hammer **both** an existing
  and a non-existent email and assert identical responses **and** comparable timing.
- **API / e2e:** drive the real endpoint; drive the lock by actually sending 10+
  failed logins, then assert the correct-password attempt is still refused with the
  cooldown, and that it succeeds after expiry. Measure timing on the unknown-email
  path against the wrong-password path (criterion 6).
- **security:** confirm the lockout message never states account existence; confirm
  the decoy-hash path actually runs (not optimised away); confirm the deferred
  per-IP gap is **documented**, not silently assumed handled.

## To verify during implementation

- Exact **velocity** numbers (burst count / window) and the reduced threshold they
  trigger.
- Where lockout state lives (an EF Core table keyed on normalised identifier:
  `failed_count`, `first_failed_at`, `last_failed_at`, `locked_until`) and a
  **pruning** strategy — an attacker can mint many identifier rows with random
  emails, so rows must expire.
- Whether ASP.NET Identity's built-in lockout (`AccessFailedCount`,
  `lockoutEnd`) can back this, given it is keyed to an existing user and uses a fixed
  window — likely needs a custom per-identifier tracker to cover unknown emails, the
  +1-min escalation and the velocity rule.
- A fixed, well-formed **decoy password hash** for the timing-equalisation path.
