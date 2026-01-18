# Spec 0002 — acceptance map

Maps each acceptance criterion of
[spec 0002](../specs/0002-refresh-and-logout.md) to the tests that guard it,
for verifier layer 2 ([`docs/workflow.md`](../../workflow.md#verification)).
Integration tests live in `tests/Auth.IntegrationTests/`; e2e steps are in
`scripts/e2e-refresh.sh` and run against the compose stack over real HTTP.

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | Refresh → `200`, new access token, rotated cookie | `RefreshTests.Refresh_returns_a_new_access_token_and_a_rotated_cookie`, `RefreshTests.Rotation_stays_in_one_family_and_can_repeat`; e2e step 2 |
| 2 | Consumed token rejected after the grace window | `RefreshReuseTests.Consumed_token_is_rejected_after_the_grace_window`; e2e step 4 |
| 3 | Reuse outside the grace window revokes the family | `RefreshReuseTests.Reuse_after_the_grace_window_revokes_the_whole_family`, `TokenPruningTests.Pruning_keeps_a_consumed_entry_that_reuse_detection_still_needs`; e2e step 4 |
| 4 | Reuse inside the grace window succeeds | `RefreshReuseTests.Consumed_token_is_forgiven_inside_the_grace_window`, `RefreshReuseTests.Concurrent_double_submit_succeeds_twice` |
| 5 | Logout → `204`, cookie cleared, token dead | `LogoutTests.Logout_returns_204_clears_the_cookie_and_ends_the_session`, `LogoutTests.Logout_with_a_consumed_token_still_ends_the_family`; e2e step 6 |
| 6 | Logout without a valid cookie → `204` | `LogoutTests.Logout_without_a_valid_cookie_still_returns_204`, `LogoutTests.Logout_is_indistinguishable_with_and_without_a_session`; e2e step 6 |
| 7 | Every refresh failure → the same `401` | `RefreshTests.Every_refresh_failure_returns_the_same_401`, `LogoutTests.Revoked_token_gets_the_uniform_401`, `RefreshTests.Hostile_cookie_value_returns_401_never_500`; e2e steps 4–5 |
| 8 | Cookie flags; refresh token never in a body | `LoginCookieTests.Login_sets_the_refresh_cookie_with_adr_0004_attributes`, `LoginCookieTests.Login_body_is_unchanged_and_never_carries_the_refresh_token`, `RefreshTests.Refresh_returns_a_new_access_token_and_a_rotated_cookie`; e2e steps 1–2 |
| 9 | Sliding window, absolute cap | `SessionLifetimeTests.Unused_session_expires_after_the_sliding_window`, `SessionLifetimeTests.Each_refresh_extends_the_sliding_window`, `SessionLifetimeTests.Session_never_outlives_the_absolute_cap`, `SessionPolicyTests.Remaining_lifetime_is_the_shorter_of_window_and_cap` (integration only, spec Decision 10) |

Also guarded, beyond the nine criteria: the token store survives a host restart
(`RefreshReuseTests.Rotated_session_survives_a_host_restart`; e2e step 3, a real container
restart), the cookie's `Max-Age` tracks the time left to the cap
(`SessionLifetimeTests.Cookie_max_age_tracks_the_time_left_to_the_cap`, Decision 10), and the
cookie is an opaque reference with a stored family
(`LoginCookieTests.Refresh_cookie_is_an_opaque_reference_with_a_stored_family`, Decision 9).

## Review Focus (plan 0002)

| # | Failure mode | Guarding test(s) |
| - | ------------ | ---------------- |
| 1 | Trailing slash | `RefreshTests.Trailing_slash_path_refreshes_too`, `LogoutTests.Trailing_slash_path_logs_out_too` |
| 2 | Hostile cookie values | `RefreshTests.Hostile_cookie_value_returns_401_never_500`, `RefreshTests.Access_token_in_the_cookie_is_not_a_refresh_token`, `LogoutTests.Logout_without_a_valid_cookie_still_returns_204` |
| 3 | The request body is not an input | `RefreshTests.Request_body_cannot_stand_in_for_the_cookie`, `RefreshTests.Request_body_cannot_override_the_cookie` |
| 4 | Two sessions of one user are independent | `RefreshReuseTests.Reuse_in_one_session_leaves_another_session_alone`, `LogoutTests.Logout_leaves_another_session_alone` |
| 5 | Real concurrency, not only a late retry | `RefreshReuseTests.Concurrent_double_submit_succeeds_twice` |

## Plan-vs-implementation notes

- **Task 1 spike:** primary path taken. `RefreshTokenIssuanceHandler` sets
  `GenerateRefreshToken` and `IncludeRefreshToken` on the password sign-in; the `offline_access`
  scope is not needed, and no `scope` claim reaches the access token. The fallback was not used.
- **`AccessTokenClaimFilter` (not in the plan):** once an authorization exists, OpenIddict stamps its
  id (`oi_au_id`) into the access token. The filter removes it so the claim set pinned by spec 0001
  stays exact (Decision 16).
- **Stored token type:** the urn `TokenTypeIdentifiers.RefreshToken`, not the string `"refresh_token"`
  (`TokenTypeHints.RefreshToken`). Logout and the tests check the urn.
- **Task 2:** OpenIddict's JSON writer indents, so `SessionResponseHandler` writes the refresh response
  itself (compact JSON, same headers, `HandleRequest()`) to keep the contract bodies byte-exact.
- **Task 3:** rotation, the 15 s grace window and family revocation are native OpenIddict
  configuration (`SetRefreshTokenReuseLeeway`), so spec "To verify" #1 is confirmed and criterion 4
  stands. Three tests were red before that configuration line, not the two the plan predicted.
- **Task 4:** OpenIddict honours the injected `TimeProvider` for refresh-token expiry, which is what
  lets the lifetime tests run on a controlled clock.
- **Task 5:** `AuthAppFactory.DisposeAsync` clears each test host's Npgsql pool; the suite hit
  PostgreSQL `53300 too many clients` at 136 tests. A change to shared test infrastructure.
- **Task 6:** `TokenPruningService` ends its loop normally on cancellation, because stopping one
  background service while the host runs would otherwise stop the host. The pruning tests stop that
  service first, since advancing the fake clock fires its timer. OpenIddict's EF `PruneAsync` deletes
  only entries created before the threshold, which is what keeps a consumed token younger than 14 days
  in place for reuse detection.
- **Task 7:** the e2e carries the `Secure` cookie by hand (plain-HTTP stack) through header files in a
  temporary directory; criterion 9 is not exercised over HTTP (spec Decision 10).
- **Repository:** a `.gitattributes` (`*.sh text eol=lf`) keeps shell scripts LF on Windows checkouts.

## Local verification log

Three Sonnet verifiers (realization vs spec, API/e2e, security) run per
[`docs/workflow.md`](../../workflow.md#verification).

| Round | Realization vs spec | API / e2e | Security | Fix commit |
| ----- | ------------------- | --------- | -------- | ---------- |
