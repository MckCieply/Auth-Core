# Spec 0003 — acceptance map

Maps each acceptance criterion of
[spec 0003](../specs/0003-lockout-and-abuse-resistance.md) to the tests that guard it,
for verifier layer 2 ([`docs/workflow.md`](../../workflow.md#verification)).
Integration tests live in `tests/Auth.IntegrationTests/`; e2e steps are in
`scripts/e2e-lockout.sh` and run against the compose stack over real HTTP.

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | Ten failures lock; a correct password is then refused | `LockoutTests.Tenth_failure_locks_and_the_correct_password_is_then_refused`, `LockoutPolicyTests.Tenth_attempt_is_still_allowed_and_starts_a_one_minute_cooldown`; e2e step 4 |
| 2 | `429` with the error and the remaining cooldown | `LockoutTests.Lockout_response_carries_the_error_and_the_remaining_cooldown` (and `LockoutApi.AssertLockedAsync` wherever a lock is asserted); e2e steps 3–4 |
| 3 | Every attempt while locked adds one minute, cap 30 | `LockoutTests.Every_attempt_while_locked_adds_one_minute_whatever_the_password`, `LockoutTests.Cooldown_never_exceeds_thirty_minutes`, `LockoutPolicyTests.Attempt_during_a_cooldown_is_refused_and_adds_one_minute`, `LockoutPolicyTests.Remaining_cooldown_never_exceeds_thirty_minutes` |
| 4 | Unknown and existing email: same lockout response, same timing | `LockoutTests.Unknown_and_existing_email_get_the_same_lockout_response`, `LockoutVelocityTests.Burst_on_an_unknown_email_locks_the_same_way`, `LoginTimingTests.Locked_attempt_runs_no_verification_at_all`; e2e steps 3–4 |
| 5 | Five failures in 10 s lock the sixth | `LockoutVelocityTests.Five_failures_within_ten_seconds_lock_the_sixth_attempt`, `LockoutVelocityTests.Five_failures_at_a_human_pace_do_not_lock`, `LockoutPolicyTests.Window_runs_ten_seconds_from_its_first_attempt`; e2e steps 3–4 |
| 6 | Timing: one decoy verification; medians within 0.5–2× | `LoginTimingTests.Unknown_email_runs_exactly_one_verification_against_the_decoy`, `LoginTimingTests.Wrong_password_runs_exactly_one_verification_against_the_stored_hash`, `LoginTimingTests.Decoy_has_the_cost_parameters_of_a_real_hash`; e2e step 2 |
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

- **Tasks 1–5:** the plan's code compiled and passed the analyzers as written; no name or
  behaviour differs from the plan.
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

## Local verification log

To be filled by the orchestrator after verification.
