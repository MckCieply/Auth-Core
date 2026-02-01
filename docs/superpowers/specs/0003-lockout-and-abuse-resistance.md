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
OpenAPI (their own Week-2 specs). `/auth/refresh` and `/auth/logout` get no limit
of any kind in this slice.

## Behaviour

An **attempt** is a well-formed `POST /auth/login` for one identifier. A malformed
request is rejected with `400` before the lockout is consulted (spec 0001) and is
not an attempt.

### Lockout (per submitted identifier)

- The identifier is the **normalised submitted email**, normalised exactly as the
  account lookup normalises it, so the counter and the account cannot drift apart.
  Attempts are counted for it **regardless of whether an account exists** — this is
  what keeps the lockout message from revealing account existence.
- The **streak** is the number of attempts on the identifier since its last
  successful login. It ends on a successful login, or after **24 hours without any
  attempt**.
- **Human-pace threshold: 10.** The tenth consecutive failed attempt puts the
  identifier into cooldown. That attempt itself still gets the ordinary `401`; the
  lockout response starts with the next one.
- **Cooldown length:** a failed attempt that starts a cooldown as attempt number
  *n* of the streak starts one of **`min(30, n − 9)` minutes** — 1 minute at the
  tenth attempt, and one minute more for every attempt since.
- **While the cooldown runs, every attempt is refused and adds +1 minute to it;**
  the time remaining never exceeds the **30-minute cap** (the cap bounds the
  targeted-DoS blast radius — see risks). The password is not evaluated and the
  account is not looked up, so a **correct** password is refused exactly like a
  wrong one. Otherwise the lock would be meaningless, and the growing timer would
  tell a guesser whether the password was right.
- **When the cooldown has expired,** the next attempt is evaluated normally. A
  correct password logs in and ends the streak. A wrong one starts a new cooldown
  by the formula above, so the escalation carries over from one cooldown to the
  next.
- A **successful** login ends the streak at any point before a lock as well.

### Velocity awareness ("spam → faster lockout")

- **Five failed attempts within 10 seconds** on one identifier start the cooldown
  at once: the streak is raised to the threshold (10), the cooldown is the base
  1 minute, and the escalation continues from there as above. The fifth attempt
  itself still gets the ordinary `401`.
- The 10-second window is **fixed**: it opens at a failed attempt that falls
  outside any open window.
- Only failed attempts count. A successful login ends the streak and closes the
  window, so repeated successful logins never trip the rule.
- This is the only "rate" control in this slice, and it is **per identifier**, not
  per IP (per-IP is deferred).

### Client response on lockout

- A locked attempt returns `429 Too Many Requests` with
  `{ "error": "too_many_attempts", "retry_after_seconds": <n> }` and a
  `Retry-After: <n>` header. `<n>` is the cooldown remaining after this attempt's
  own +1 minute, in whole seconds, rounded up, never less than 1 — a timer the UI
  can count down.
- It carries the same cache headers as the `401` of spec 0001
  (`Cache-Control: no-store`, `Pragma: no-cache`) and never sets or clears the
  refresh cookie.
- **No enumeration:** because the lock is keyed on the submitted identifier whether
  or not it exists, this exact response appears for a hammered **unknown** email too.
  The message never says "account locked" (which would imply the account exists) —
  it says attempts on *this login* are temporarily blocked.

### Timing equalisation

- On the unknown-email path, login still performs a **dummy password-hash
  verification** against a decoy hash, so an unknown email and a wrong password
  take ~the same time. (In ASP.NET Identity this is explicit: `FindByEmailAsync`
  returns `null`, so the null path must verify the submitted password against the
  decoy instead of short-circuiting.) The same applies to an account that has no
  password hash.
- The decoy is **produced at startup by the same password hasher** that verifies
  real passwords, so its cost parameters always match the current configuration.
- Goal is to remove the **order-of-magnitude** gap, not cryptographic constant-time;
  "close enough" is the accepted bar (owner decision).

## Acceptance criteria (Done when)

1. After **10 consecutive failed** logins on an identifier, the next attempt is
   refused with the lockout response **even if the password is correct**, until the
   cooldown elapses.
2. The lockout response carries an explicit "too many attempts" error **and** the
   remaining cooldown (a `retry_after_seconds` / `Retry-After` the UI can show as a
   timer).
3. Each **further attempt** while locked — with a correct or a wrong password
   alike — extends the cooldown by **+1 minute**, the time remaining never
   exceeding the **30-minute cap**.
4. A hammered **unknown** email produces the **same** lockout response and the
   **same** timing as a hammered existing email — nothing distinguishes the two
   (no enumeration via body or time).
5. **Five failed attempts within 10 seconds** on one identifier trip the lock: the
   sixth attempt gets the lockout response. Five failed attempts spaced wider than
   that do not.
6. **Timing:** the unknown-email path performs exactly one password-hash
   verification, against the decoy, as the wrong-password path performs one against
   the stored hash; and over the real network the median response time of an
   unknown-email login is within **0.5–2×** that of a wrong-password login.
7. After the cooldown expires, a **correct** login **succeeds** and the failed
   counter is **reset**; a successful login also resets the counter mid-streak
   (before any lock).
8. No response in any of the above reveals whether the email has an account.
9. When a cooldown has expired and the next attempt **fails**, a new cooldown of
   `min(30, n − 9)` minutes starts, *n* being that attempt's number in the streak:
   the escalation carries over from one cooldown to the next.
10. After **24 hours without an attempt** on an identifier its streak is gone: the
    next failed attempt counts as the first.

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

## Decisions (owner, 2026-01-20)

Taken after slices 1 and 2 (specs 0001 and 0002 as built), before plan 0003. The
scope is unchanged. Decisions 7 and 8 change or complete the behaviour first
written and say why; the rest pin what that version left to the plan, or record how
the contract is met.

7. **During a cooldown the password is not evaluated, and every attempt adds
   +1 minute** (amends Decision 2, which said "failed attempt"). Why: if only a
   wrong password extended the cooldown, `retry_after_seconds` would tell a guesser
   whether the password was right, and guessing could simply continue through the
   lock. Accepted cost: an impatient user with the right password lengthens their
   own lock; the timer in the response is there to prevent that.
8. **The escalation carries over from one cooldown to the next, and a streak ends
   only on a successful login or after 24 hours without an attempt.** The first
   version did not say what follows a failure after the cooldown expires. Why: a
   cooldown that always restarts at 1 minute allows about 1,440 guesses a day on
   one identifier, and a counter that restarts at zero about 14,400; carrying the
   escalation to the cap allows about 48. Below the threshold a patient guesser
   gets 9 a day. Accepted cost: one request per cooldown keeps a targeted
   identifier locked (see the residual risk below). Criteria 9 and 10 are added so
   that this behaviour has a guarding test.
9. **Velocity rule: five failed attempts within a fixed 10-second window** start
   the cooldown at once, by raising the streak to the threshold. Only failed
   attempts count. This replaces the two-stage wording ("≥ 5 attempts lower the
   threshold to 3"), which amounts to the same immediate lock with more state. Why
   these numbers: a person does not type five passwords in ten seconds, while a
   double-submitted form produces two or three attempts and must not lock anyone.
   Why failed attempts only: a client that logs in successfully several times in a
   few seconds (the e2e scripts do) must not lock itself.
10. **The lockout key is the SHA-256 of the normalised email**, not the email. Why:
    the login body may be 8 KiB, so an attacker would control the size of the rows
    as well as their number; and the email field receives whatever people type,
    including passwords and other people's addresses, which should not be stored.
    The hash is unsalted: it bounds size and incidental data, it is not secrecy.
11. **The decoy hash is generated at startup by the configured password hasher**,
    not a constant in the code (the first version said "fixed"). Why: the cost of a
    verification is set by the parameters stored in the hash, so a pasted constant
    would silently reopen the timing gap the day the hasher settings change.
12. **Criterion 6 is proven in two parts.** An integration test proves that the
    decoy verification runs, exactly once, on the unknown-email path. The medians
    are measured over the real network by the e2e script, with a 0.5–2× tolerance
    (the gap measured in slice 1 was 10–20×). Why: response-time medians in a unit
    test runner either fail at random or need a tolerance that proves nothing.
13. **Lockout state is a table of its own, pruned hourly.** ASP.NET Identity's
    lockout (`AccessFailedCount`, `LockoutEnd`) needs an existing user and a fixed
    window, so it cannot cover unknown emails, the escalation or the velocity rule.
    An in-memory counter would be reset by a restart. Rows without an attempt for
    24 hours are removed by an in-process background job, the same pattern as the
    token pruning of spec 0002; until it runs, such a row is treated as absent.
14. **An attempt is counted before its password is evaluated.** Why: with "check,
    then increment", parallel requests arriving at a streak of 9 would all pass the
    check and each get a password evaluation. Counting first gives every request
    its own number in the streak, so at most 10 passwords (5 in a burst) are
    evaluated per streak however the requests are sent. A successful login removes
    the state, so the behaviour of sequential requests is exactly as described
    above. No database lock is held while a password is hashed.
15. **Thresholds and durations are constants, not configuration:** 10 attempts,
    1 minute base, 30 minutes cap, 5 attempts in 10 seconds, 24 hours — the same
    stance as the token lifetimes in specs 0001 and 0002.

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
  and it leaves a **targeted account-lockout DoS**: an attacker can keep a specific
  user's identifier locked by failing its login, at the cost of one request per
  cooldown once the escalation has reached the cap (Decision 8). The 30-minute cap
  bounds each cooldown; it does not close the attack. Closing these is the point of
  the deferred work.

## Verification notes (for the local verifiers)

Per [`docs/workflow.md`](../../workflow.md) — verifiers run locally before merge.

- **realization vs spec:** map each acceptance criterion to its guarding test. The
  no-enumeration pair (criteria 4 + 8) needs tests that hammer **both** an existing
  and a non-existent email and assert identical responses. Criteria 1, 3, 9 and 10
  involve minutes or hours; they are proven by integration tests against real
  PostgreSQL with a controlled clock.
- **API / e2e:** drive the real endpoint. Trip the lock by actually sending a burst
  of failed logins, then assert that the correct-password attempt is still refused
  with the cooldown, and that it succeeds after expiry. Measure timing on the
  unknown-email path against the wrong-password path (criterion 6) without tripping
  the lock: a different unknown address for every sample, and on the existing
  account a successful login between the failed ones.
- **security:** confirm the lockout message never states account existence; confirm
  the decoy-hash path actually runs (not optimised away); confirm a locked attempt
  with the correct password is indistinguishable from one with a wrong password
  (Decision 7); confirm parallel requests cannot buy more password evaluations than
  the threshold (Decision 14); confirm the deferred per-IP gap is **documented**,
  not silently assumed handled.

## To verify during implementation

- A way to count an attempt **atomically** with EF Core and PostgreSQL — one short
  transaction per attempt, no lock held during the password hash (Decision 14) —
  that neither loses a count under parallel requests nor fails when two requests
  create the same row at once.
- That the `429` leaves through the login endpoint as its own result, outside the
  OpenIddict pipeline, and that no handler adds a `Set-Cookie` to it.
- That verifying a password against the decoy costs what verifying against a real
  hash costs (the e2e medians of criterion 6).
- That no existing test or e2e script sends five failed logins for one identifier
  within ten seconds on one host, so the velocity rule does not break them.

## As built (owner, 2026-01-24)

Recorded after implementation and local verification (plan 0003,
[acceptance map](../plans/0003-acceptance-map.md)). What entered this stage stays
in it; this section records it rather than rewriting the decisions above.

**"To verify during implementation", resolved:**

1. Counting an attempt is one transaction around one statement: `INSERT … ON
   CONFLICT DO UPDATE … RETURNING *` creates the identifier's row or locks the
   existing one, the rules run in memory, and the new state is saved. Twelve
   parallel failed logins get exactly five password evaluations, and a pruning pass
   racing an attempt cannot fail it. The transaction is over before a password is
   hashed.
2. The `429` is the login endpoint's own result. No OpenIddict or session handler
   runs on it, and it carries no `Set-Cookie`.
3. A verification against the decoy costs what one against a stored hash costs. Over
   the compose stack the medians were 279 ms for an unknown email against 283 ms for
   a wrong password (319 ms against 306 ms in the verifier's own measurement).
   Slice 1 had measured a 10–20× gap.
4. No existing test or script sent five failed logins for one identifier within ten
   seconds; none needed a change for the velocity rule.

**Contract as built.** `429`, compact JSON
`{"error":"too_many_attempts","retry_after_seconds":n}`, `Retry-After: n`,
`Cache-Control: no-store`, `Pragma: no-cache`, no cookie. `n` is at least 1 and at
most 1800.

**Behaviour the spec was silent on:**

- Refused attempts count toward the attempt number *n*: a failure after an expired
  cooldown starts `min(30, n − 9)` minutes with every attempt of the streak counted,
  the refused ones included.
- The 10-second window opens at an *evaluated* attempt; refused attempts neither
  open nor fill it. A successful login closes it together with the streak.
- A correct password ends the streak even if the client disconnects before the
  response.
- If an attempt cannot be counted (the database is unavailable), the request fails
  with a `500` before any password is evaluated.
- `/auth/login/` shares the streak of `/auth/login`. A wrong method is a `400`
  (spec 0001) and is not an attempt.
- A running cooldown survives a restart; the decoy is made anew at every start.
- Pruning runs once at host start as well as hourly, as in spec 0002.
- Six or more simultaneous logins with the correct password on one identifier:
  those arriving after the fifth, before the first has finished, get the `429`
  (Decisions 9 and 14 together). Accepted by the owner.

**Residual risks found in verification** — accepted until per-IP limiting lands;
they extend the list under "Deferred / follow-ups":

- Every login attempt, refused ones included, is one small database write, and an
  attempt for an unknown email costs one password hash. An anonymous client can
  cause both at will.
- Parallel requests for one identifier wait for its row, each holding a pooled
  database connection for the few milliseconds a count takes. A flood on one
  identifier can exhaust the pool.
- A successful login resets the streak, and someone probing an identifier can see
  that: it shows that the account exists and that its owner logged in meanwhile.
  It takes about eleven requests per probe (escalation E2).
- The decoy follows the current hasher settings. After a settings change, accounts
  whose hashes still carry the old parameters cost differently until their next
  login rehashes them.

**Delivered beyond the plan:** the pruning tests build their host without the
pruning services instead of stopping them, which changes a test file of slice 2
(escalation E4); a test helper to change or remove service registrations in the
test host.

**Known gaps, owned elsewhere:** an email containing U+FFFE makes the email
normaliser throw on a host with ICU (`500`; the container image answers `401`). It
predates this slice, evaluates no password and is not counted → request validation
of spec 0001, for the owner to schedule (E1). A later flow that can still refuse a
login after the password check (unverified email, organisation rules) must decide
what happens to the streak, which today ends as soon as the password is verified.
(Settled for the unverified email by spec 0004, Decision 3: the streak ends. E1 is fixed
by spec 0004, Decision 16.)
`LockoutPruningService` is a copy of `TokenPruningService` (E3). Per-IP rate
limiting and trusted-proxy real-IP stay deferred (Decision 6). The acceptance map
lists the rest.
