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

Three Sonnet verifiers (realization vs spec, API/e2e, security) ran per
[`docs/workflow.md`](../../workflow.md#verification).

| Round | Realization vs spec | API / e2e | Security | Fix commit |
| ----- | ------------------- | --------- | -------- | ---------- |
| 1 | PASS (7 non-blocking notes; removing the leeway, the cap or logout's revocation each turned the guarding tests red; 140/140, also hermetic) | PASS (clean stack; `e2e-login.sh` and `e2e-refresh.sh` ALL PASS; 57 independent probes, no `500`; no token value in the service log) | PASS (9 non-blocking notes) | — |

No blocking finding, so no fix round. Two assumptions the security verifier could
not check statically were settled by probes: the stored refresh-token reference is
hashed at rest, and pruning a revoked authorization that still has younger tokens
does not fail (the authorization stays until its newest token is 14 days old, then
both go).

### Deferred / follow-ups (not fixed in this slice, by design)

- **Ending every session on a password change** → the forgot/reset spec (Decision
  11). There is no helper yet; OpenIddict can revoke by subject, and both tokens and
  authorizations carry the user id.
- **Grace-window fork:** a refresh token stolen within 15 s of its rotation can
  start a second chain in the same family that reuse detection does not see, until
  logout or the 30-day cap. Inherent in Decision 2; recorded so the trade-off is
  visible.
- **No rate limiting on `/auth/refresh` and `/auth/logout`**, and each refresh
  keeps two store rows for at least 14 days → spec 0003.
- **Logout when the token store fails:** `500`, and the cookie is not cleared.
  Revocation also uses the request's cancellation token; an aborted request can
  leave it partial, which is still safe (a revoked authorization rejects every
  token under it).
- **Cookie header the web server rejects:** non-ASCII bytes → `400`, over 32 KB →
  `431`, both from Kestrel before the app runs, so not the uniform `401` / `204`.
  Pre-existing; `/auth/login` behaves the same.
- **Same-site residual:** a sibling subdomain or same-origin XSS can trigger
  refresh/logout or plant a competing `auth_rt` cookie. The CSRF token stays
  deferred (Decision 6).
- **Development only:** the developer exception page would echo the `Cookie`
  header on an unhandled `500`. None was found; the compose stack is loopback-only.
- **Test gaps:** the pruning service's own loop (start pass, failed pass, stop) is
  covered by a probe, not a test; the 15 s boundary is bracketed at 10 s / 20 s;
  `RefreshEndpoint`'s explicit cap and missing-claim branches have no test of
  their own; `e2e-refresh.sh` does not replay inside the grace window or compare
  header sets on failures (integration tests and the verifier's probes do).
- **`session_start` beyond year 9999** would throw (`500`). The claim is written
  by the server and stored server-side; a client cannot reach it.
- **A later token-endpoint flow** must record the cookie lifetime
  (`RefreshCookie.LifetimeItemKey`), or `SessionResponseHandler` fails closed.
- **`scripts/dev-keys.sh` fails silently under Git Bash on Windows** (MSYS path
  conversion of `-subj "/CN=…"`; the error is hidden by `2>/dev/null`). Workaround:
  `MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh`. Pre-existing (slice 1).
- **The e2e scripts pass the access token to `verify_jwt.py` as an argument**
  (pre-existing pattern). A refresh token is never on a command line.

### Escalations for the owner (spec silent, or beyond the plan)

- **E1.** `GET /auth/logout` returns `405` with `Allow: POST`; a wrong method on
  `/auth/refresh` and `/auth/login` returns `400 invalid_request`.
- **E2.** Every error on `/auth/refresh` other than the wrong-method
  `invalid_request` becomes `401 invalid_grant`, including an OpenIddict
  `server_error`. In line with Decision 8, but it can hide a server fault. An
  unhandled exception is still a `500`.
- **E3.** Pruning also runs once at host start, not only hourly (Decision 13).
- **E4.** `AccessTokenClaimFilter` was added beyond the plan to keep Decision 16:
  without it the access token gains `oi_au_id`. The alternative is to accept that
  claim as OpenIddict metadata, as spec 0001 did for `oi_tkn_id`.

E1–E4 are recorded in spec 0002 → "As built".
