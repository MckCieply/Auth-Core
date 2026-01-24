# Spec 0003 — acceptance map

Maps each acceptance criterion of
[spec 0003](../specs/0003-lockout-and-abuse-resistance.md) to the tests that guard it,
for verifier layer 2 ([`docs/workflow.md`](../../workflow.md#verification)).
Integration tests live in `tests/Auth.IntegrationTests/`; e2e steps are in
`scripts/e2e-lockout.sh` and run against the compose stack over real HTTP.

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | Ten failures lock; a correct password is then refused | `LockoutTests.Tenth_failure_locks_and_the_correct_password_is_then_refused`, `LockoutPolicyTests.Tenth_attempt_is_still_allowed_and_starts_a_one_minute_cooldown`; e2e step 4 (the burst variant: a correct password refused during a cooldown) |
| 2 | `429` with the error and the remaining cooldown | `LockoutTests.Lockout_response_carries_the_error_and_the_remaining_cooldown` (and `LockoutApi.AssertLockedAsync` wherever a lock is asserted); e2e steps 3–4 |
| 3 | Every attempt while locked adds one minute, cap 30 | `LockoutTests.Every_attempt_while_locked_adds_one_minute_whatever_the_password`, `LockoutTests.Cooldown_never_exceeds_thirty_minutes`, `LockoutPolicyTests.Attempt_during_a_cooldown_is_refused_and_adds_one_minute`, `LockoutPolicyTests.Remaining_cooldown_never_exceeds_thirty_minutes` |
| 4 | Unknown and existing email: same lockout response, same timing | `LockoutTests.Unknown_and_existing_email_get_the_same_lockout_response`, `LockoutVelocityTests.Burst_on_an_unknown_email_locks_the_same_way`, `LoginTimingTests.Locked_attempt_runs_no_verification_at_all`; e2e steps 3–4 |
| 5 | Five failures in 10 s lock the sixth | `LockoutVelocityTests.Five_failures_within_ten_seconds_lock_the_sixth_attempt`, `LockoutVelocityTests.Five_failures_at_a_human_pace_do_not_lock`, `LockoutPolicyTests.Window_runs_ten_seconds_from_its_first_attempt`, `LockoutPolicyTests.Window_is_fixed_not_sliding`; e2e steps 3–4 |
| 6 | Timing: one decoy verification; medians within 0.5–2× | `LoginTimingTests.Unknown_email_runs_exactly_one_verification_against_the_decoy`, `LoginTimingTests.Wrong_password_runs_exactly_one_verification_against_the_stored_hash`, `LoginTimingTests.Decoy_has_the_cost_parameters_of_a_real_hash`, `LoginTimingTests.Decoy_follows_the_configured_hasher_settings`; e2e step 2 |
| 7 | Correct login after the cooldown succeeds and resets; success resets mid-streak | `LockoutTests.After_the_cooldown_a_correct_login_succeeds_and_resets_the_streak`, `LockoutTests.Successful_login_resets_the_streak_before_any_lock`, `LockoutVelocityTests.Successful_login_closes_the_window`; e2e steps 5–6 |
| 8 | No response reveals whether the email has an account | the tests of criterion 4, `LoginTimingTests.Account_without_a_password_runs_the_decoy_too`, and spec 0001's `LoginTests.Unknown_email_and_wrong_password_are_indistinguishable` (unedited) |
| 9 | Escalation carries over from one cooldown to the next | `LockoutTests.Failure_after_an_expired_cooldown_starts_a_longer_one`, `LockoutPolicyTests.Failure_after_an_expired_cooldown_starts_a_longer_one`, `LockoutPolicyTests.Cooldown_is_one_minute_at_the_threshold_plus_one_per_attempt_up_to_the_cap` (integration only) |
| 10 | Streak forgotten after 24 hours | `LockoutTests.Streak_is_forgotten_after_24_hours_without_an_attempt`, `LockoutPolicyTests.Streak_is_forgotten_after_24_hours_without_an_attempt`, `LockoutPolicyTests.Streak_is_kept_just_under_24_hours`, `LockoutPruningTests.Pruning_removes_streaks_without_an_attempt_for_24_hours` (integration only) |

Criteria 3, 9 and 10 are not exercised over HTTP: the cooldown cap, the escalation across
cooldowns and the 24-hour reset need minutes to hours of waiting, so integration tests with a
controlled clock carry them. The same goes for the threshold at human pace (criterion 5, second
sentence). The e2e script waits out one real cooldown (criterion 7) and takes about 2.5 minutes.

## Review Focus (plan 0003)

| # | Failure mode | Guarding test(s) |
| - | ------------ | ---------------- |
| 1 | Parallel attempts are each counted once, and buy at most five password evaluations | `LoginStreakStoreTests.Parallel_attempts_on_a_new_identifier_are_each_counted_once`, `LockoutVelocityTests.Parallel_failures_buy_at_most_five_password_evaluations` |
| 2 | Spelling variants of one email share a streak | `LockoutTests.Spelling_variants_of_one_email_share_a_streak` |
| 3 | A malformed request is not an attempt | `LockoutTests.Malformed_request_is_not_an_attempt` |
| 4 | A hostile email is counted and locked like any other | `LockoutTests.Hostile_email_is_counted_and_locked_like_any_other` |
| 5 | A cooldown survives a host restart | `LockoutTests.Cooldown_survives_a_host_restart` |

## Plan-vs-implementation notes

- **Tasks 1–5:** the plan's code compiled and passed the analyzers as written; no name differs
  from the plan. What is worth knowing about each task's behaviour follows.
- **Task 1:** counting an attempt is one `INSERT … ON CONFLICT DO UPDATE … RETURNING *` run
  through `FromSql(...).ToListAsync()` inside a transaction, then `SaveChanges`. The migration is
  `20260121180512_AddLoginStreaks` and touches only the new table.
- **Task 2:** `ClearAsync` after a correct password does not use the request's cancellation token.
- **Task 3:** twelve parallel failed logins give exactly five `401` and seven `429`.
- **Task 4:** the decoy is created at host start (resolved right after the app is built).
- **Task 5:** `LockoutPruningService` is a deliberate copy of `TokenPruningService`.
- **Task 6:** `scripts/e2e-lockout.sh` follows the plan's helper code verbatim, plus one small
  helper, `expect_401` (status and exact `invalid_credentials` body), which the plan wrote out inline
  at each use. Step 4 compares the seed email's lockout body with the unknown email's after replacing
  the number, and step 5 sleeps `RETRY + 1` s (121 s on both runs: five failures plus the refused
  correct password make 120 s). Measured over the compose stack on the development machine
  (Windows, Docker Desktop), 15 samples per path: run 1 median **279 ms** (unknown email) against
  **283 ms** (wrong password), ratio 0.99; run 2 median **275 ms** against **276 ms**, ratio 1.0.
  Both runs end with `ALL PASS`, the second on the same stack as the first, and take about 2.5
  minutes each.

- **After verification (one fix round, tests and script only):** the pruning tests no longer stop
  a hosted service on a running host. On .NET 10 a `BackgroundService` stopped before its loop has
  started ends as a cancelled task, which the host takes for a failed service and stops itself; the
  tests of slice 2 carried that race and a second pruning service made it frequent. Their host is
  now built without the two pruning services (`AuthAppFactory.WithoutHostedService`), which changes
  slice 2's `TokenPruningTests`. Added in the same round: `Decoy_follows_the_configured_hasher_settings`,
  `Window_is_fixed_not_sliding`, a store precision test that no longer depends on the clock's start,
  and a non-empty cookie check in the e2e script.

## Local verification log

Three Sonnet verifiers (realization vs spec, API/e2e, security) ran per
[`docs/workflow.md`](../../workflow.md#verification).

| Round | Realization vs spec | API / e2e | Security | Fix commit |
| ----- | ------------------- | --------- | -------- | ---------- |
| 1 | **FAIL** — one blocking finding: the pruning tests intermittently stopped their own host (8 of 16 suite runs under load, 1 of 13 sequential; a latent race from slice 2). Behaviour and Decisions 7–15 all implemented, boundaries match, every criterion has a real guard; 199/199 | PASS (clean stack; the three e2e scripts ALL PASS, the lockout script twice on one stack; 14 probe groups, all as specified, no `5xx`; medians 319 ms unknown email against 306 ms wrong password; nothing submitted in the service log) | PASS (no enumeration, no password oracle during a cooldown, no route past the gate, nothing submitted stored or logged; 5 non-blocking notes) | `test: build the pruning tests' host without the pruning services` |
| 2 | PASS (the race reproduced on its own, 1935 of 2000; 16 loaded suite runs, 0 failures; the two new tests would fail on a constant decoy and on a sliding window; 201/201, also hermetic) | not re-run by the verifier; the fix round re-ran the three scripts on a clean stack, ALL PASS | not re-run (no product code changed) | — |

Probes worth keeping from the API/e2e round: `retry_after_seconds` grew by a minute per refused
attempt and stopped at 1800; ten failures at human pace gave `401` and the eleventh `429`; a failure
after an expired cooldown gave `429` with 180 on the next attempt; twelve parallel failures gave
five `401` and seven `429`, three times; a cooldown survived `docker compose restart auth`;
`/auth/refresh` and `/auth/logout` answered as in spec 0002 while the seed email was locked.

### Owner decisions on the plan's open questions (2026-01-24)

1. Six or more simultaneous logins with the correct password on one identifier: those arriving
   after the fifth, before the first has finished, get the `429`. Accepted.
2. Pruning runs once at host start as well as hourly. Accepted.
3. Every login attempt writes to the database, refused ones included. Accepted until per-IP
   limiting lands.

### Deferred / follow-ups (not fixed in this slice, by design)

- **Residual risks until per-IP limiting** (recorded in spec 0003 → "As built"): one database
  write per attempt and one password hash per unknown email, at an anonymous client's will;
  parallel requests for one identifier each hold a pooled connection while they wait for its row,
  so a flood on one identifier can exhaust the pool.
- **The decoy follows the current hasher settings.** After a settings change, accounts whose
  hashes carry the old parameters cost differently until their next login rehashes them. Latent:
  the settings are the defaults.
- **A later flow that can refuse a login after the password check** (unverified email,
  organisation rules) must decide what happens to the streak: today it ends as soon as the
  password is verified, before the token is issued.
- **`LoginIdentifier.HashOf` expects an email already normalised** by `UserManager.NormalizeEmail`;
  nothing enforces that for a second caller. `LoginStreakStore` does not check the key's length.
- **The pruning services' own loops** (pass at start, hourly pass, failed pass logged and the loop
  continues, normal end at host stop) have no test, for tokens or for streaks. The passes
  themselves are tested.
- **Test gaps:** Decision 14's bound of ten evaluations at human pace is not tested with parallel
  requests (on a frozen clock the burst rule always trips first, at five); no store-level test of
  the 24-hour reset or of a cooldown expiring through the database; no test that a streak younger
  than 24 hours and inside its cooldown survives a pruning pass; "no account lookup during a
  cooldown" is visible in the code but pinned only for the password check; the test of a pruning
  pass racing an attempt cannot show that the two overlapped (a scratch run of 400 attempts
  against a deleting loop did, without an error).
- **Small things:** a clock stepping backwards keeps a burst window open a little longer;
  an account whose stored hash is an empty or malformed string is answered without a hash
  computation (not a state the service produces); counting an attempt writes the row twice;
  `LockoutTests` hard-codes one spelling of the seed email; two long lines in `LockoutPolicy.cs`
  and the `LoginEndpoint` summary.
- **Any method other than `POST` on `/auth/login`** is a `400`, not a `405`, and is not an attempt.
  Pre-existing (spec 0002, E1).
- **The initial migration's id was changed at the start of this branch.** A database that applied
  it under the old id fails at startup; a clean volume (`down -v`) is unaffected.
- **Under heavy parallel load** (three suites at once) Testcontainers sometimes loses its
  PostgreSQL container ("No such container"). Infrastructure, not the code under test.

### Escalations for the owner (spec silent, or beyond the plan)

- **E1.** An email containing U+FFFE makes Identity's email normaliser throw on a host with ICU:
  `500`. The container image answers `401`. It predates this slice (`FindByEmailAsync` uses the
  same normaliser), evaluates no password and is not counted. Where it gets fixed (request
  validation, spec 0001) is the owner's call.
- **E2.** A successful login resets the streak, which someone probing an identifier can observe:
  it shows that the account exists and that its owner logged in meanwhile (about eleven requests
  per probe). It follows from criterion 7 and Decision 4 together and is not in the spec's list of
  residual risks; recorded in "As built".
- **E3.** `LockoutPruningService` is a copy of `TokenPruningService`, as the design chose. The
  alternative is a shared base class, which touches slice 2's code.
- **E4.** The fix round changed slice 2's `TokenPruningTests` (see the notes above), against the
  plan's "existing tests unedited".

E1–E4 are recorded in spec 0003 → "As built".
