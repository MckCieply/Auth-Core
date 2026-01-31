# Spec 0004 — acceptance map

Maps each acceptance criterion of
[spec 0004](../specs/0004-email-flows.md) to the tests that guard it,
for verifier layer 2 ([`docs/workflow.md`](../../workflow.md#verification)).
Integration tests live in `tests/Auth.IntegrationTests/`; e2e steps are in
`scripts/e2e-email.sh` and run against the compose stack (PostgreSQL, the Mailpit mail catcher and
the auth service) over real HTTP and real SMTP.

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | Forgot → `202`, a reset mail with the configured link | `MailRequestEndpointTests.Forgot_for_an_account_is_accepted_and_a_reset_mail_follows`, `MailDispatcherTests.Request_for_an_account_sends_one_mail_and_stores_only_the_hash_of_its_token`, `MailThroughSmtpTests.Forgot_request_ends_as_a_mail_on_the_mail_server`; e2e steps 6–7 |
| 2 | An address without an account: same response, same statements, no mail | `MailRequestEndpointTests.Address_without_an_account_gets_the_same_answer_the_same_rows_and_no_mail`, `MailDispatcherTests.Request_for_an_address_without_an_account_is_dropped_without_a_mail`, `MailRequestEndpointTests.Request_runs_the_same_statements_for_any_address_and_never_reads_the_accounts`, `MailThroughSmtpTests.Forgot_for_an_unknown_address_sends_nothing`, `MailDispatcherTests.Requests_for_addresses_without_an_account_do_not_hold_up_a_real_mail`; e2e steps 6–7 |
| 3 | Reset → `204`; old password `401`, new one logs in | `ResetPasswordTests.Reset_replaces_the_password`; e2e steps 7–8 |
| 4 | Every refresh token issued before the reset is refused | `ResetPasswordTests.Reset_ends_every_session_of_the_account`, `ResetPasswordTests.Session_whose_login_overlapped_a_password_change_cannot_refresh`; e2e step 8 |
| 5 | Reset lifts the cooldown, confirms the email, kills the other links | `ResetPasswordTests.Reset_lifts_a_lockout_and_confirms_the_email_in_one_go`, `ResetPasswordTests.Reset_makes_the_other_links_of_the_account_unusable` |
| 6 | A token works once; unknown, used, expired, replaced → the same `invalid_token`; one of parallel uses succeeds | `EmailTokensTests.Token_works_once`, `EmailTokensTests.Token_expires_after_its_lifetime`, `EmailTokensTests.Issuing_again_replaces_the_earlier_token_of_that_kind_only`, `EmailTokensTests.Parallel_consumption_succeeds_exactly_once`, `ResetPasswordTests.Token_works_once`, `ResetPasswordTests.Unknown_used_expired_and_replaced_tokens_get_one_answer`, `ResetPasswordTests.Token_is_good_until_just_under_an_hour`, `VerifyEmailTests.Unknown_used_expired_and_replaced_tokens_get_one_answer`, `VerifyEmailTests.Expired_token_confirms_nothing`, `VerifyEmailTests.Token_is_good_until_just_under_24_hours`, `MailDispatcherTests.Newer_mail_replaces_the_earlier_token`; e2e steps 4, 7 |
| 7 | Weak password → `weak_password` with every broken rule; nothing changes; the token stays usable | `ResetPasswordTests.Weak_password_names_every_broken_rule_and_leaves_everything_as_it_was`; e2e step 7 |
| 8 | Policy: 8 characters, upper, lower, digit; no special character needed; letters of any script | `PasswordPolicyTests.Policy_is_eight_characters_upper_lower_and_digit`, `ResetPasswordTests.Password_without_a_special_character_is_accepted`, `ResetPasswordTests.Letters_of_any_script_satisfy_the_policy` |
| 9 | Mail limit: second request within 60 s and sixth within the hour refused; kinds independent; refusals do not add up | `MailLimitPolicyTests` (all), `MailRequestStoreTests.Refused_request_writes_nothing`, `MailRequestStoreTests.Kinds_and_addresses_have_limits_of_their_own`, `MailRequestEndpointTests.Second_request_within_a_minute_is_refused_and_asking_again_does_not_add_to_the_wait`, `MailRequestEndpointTests.Sixth_request_within_the_hour_is_refused_until_the_hour_is_over`, `MailRequestEndpointTests.Kinds_do_not_share_a_limit`, `EmailPruningTests.Limit_row_that_still_counts_survives_a_pass`; e2e step 3 |
| 10 | The `429` is identical for an address without an account | `MailRequestEndpointTests.Limit_answers_the_same_for_an_address_without_an_account` |
| 11 | A verification mail only for an unconfirmed account; same `202` for all | `MailRequestEndpointTests.Verification_mail_is_sent_only_to_an_unconfirmed_account`, `MailDispatcherTests.Verification_mail_goes_to_an_unconfirmed_account_only`, `VerifyEmailTests.Confirmed_account_gets_no_further_verification_mail` |
| 12 | Verify → `204`; the account can then log in | `VerifyEmailTests.Verification_confirms_the_email`, `UnverifiedLoginTests.After_verification_the_account_logs_in`; e2e steps 4–5 |
| 13 | Unconfirmed login: `403` with the right password, `401` with a wrong one, `429` in a cooldown; the `403` ends the streak | `UnverifiedLoginTests.Correct_password_for_an_unconfirmed_account_is_refused_with_its_own_answer`, `UnverifiedLoginTests.Wrong_password_for_an_unconfirmed_account_is_the_ordinary_401`, `UnverifiedLoginTests.During_a_cooldown_the_answer_is_the_lockout_not_the_refusal`, `UnverifiedLoginTests.Refusal_ends_the_streak_as_a_success_would`; e2e step 2 |
| 14 | A queued mail survives a restart; failed sends are retried on the schedule and dropped after an hour | `MailDispatcherTests.Request_recorded_before_a_restart_is_sent_after_it`, `MailDispatcherTests.Failed_send_is_retried_on_the_schedule_and_sent_once`, `MailDispatcherTests.Request_that_could_not_be_delivered_within_an_hour_is_dropped_and_logged`, `MailDispatcherTests.Two_dispatchers_at_once_try_every_request_exactly_once`, `MailDispatchServiceTests.Failed_send_is_retried_once_its_delay_has_passed_on_the_clock`; e2e step 9 |
| 15 | HTML + text, configured language, application name, lifetime, diacritics; no token in logs or database | `MailComposerTests.Reset_mail_in_english`, `MailComposerTests.Verification_mail_in_polish`, `SmtpMailTransportTests.Mail_arrives_with_both_parts_and_diacritics_intact`, `EmailTokensTests.Issued_token_is_43_url_safe_characters_and_only_its_hash_is_stored`, `MailDispatcherTests.No_token_and_no_mail_body_reach_the_log`; e2e step 4 |
| 16 | An email that cannot be used is a `400` on login and on the mail endpoints, on every host, and is not counted | `EmailInputTests` (all), `MailRequestEndpointTests.Malformed_request_is_a_400_and_is_not_counted`; e2e step 2 (the image without ICU) |
| 17 | A host with a missing or invalid mail setting does not start | `MailSettingsTests` (all) |
| 18 | The e2e script drives both flows; the three existing scripts still pass | `scripts/e2e-email.sh`; `scripts/e2e-login.sh`, `scripts/e2e-refresh.sh`, `scripts/e2e-lockout.sh` unedited |

Criteria 5, 8, 10, 17 and most of 6, 9, 11 and 14 are not exercised over HTTP: the expiry of a
token, the sixth request within the hour, the one-hour drop of an undelivered request, the cooldown
and the races need a controlled clock or parallel requests, so integration tests carry them. The e2e
script covers what needs a real network: SMTP to a real mail server, the links in the mails, a refresh
cookie refused after a reset, the `400` for U+FFFE in the image without ICU, and a retry after a real
outage of the mail server. It takes about a minute and is not re-runnable on the same stack: it
confirms the second seed user and changes its password, so a second run needs
`docker compose ... down -v` first.

## Review Focus (plan 0004)

| # | Failure mode | Guarding test(s) |
| - | ------------ | ---------------- |
| 1 | Parallel uses of one token change the password or confirm the email once | `ResetPasswordTests.Parallel_resets_with_one_token_change_the_password_once`, `VerifyEmailTests.Parallel_verifications_with_one_token_succeed_once` |
| 2 | Spelling variants of one address share the limit and reach the account | `MailRequestEndpointTests.Spelling_variants_of_one_address_share_the_limit_and_reach_the_account` |
| 3 | Nothing from a requester or from configuration breaks a mail: the application name is encoded in the HTML part only; a frontend URL that has a query string keeps it | `MailComposerTests.Application_name_is_encoded_in_the_html_part_only`, `MailComposerTests.Link_keeps_a_query_string_the_frontend_url_already_has` |
| 4 | A failed send is retried on the schedule, leaves the earlier link in force, and two dispatchers never try one request twice at once | `MailDispatcherTests.Failed_send_is_retried_on_the_schedule_and_sent_once`, `MailDispatcherTests.Failed_send_leaves_the_earlier_link_in_force`, `MailDispatcherTests.Two_dispatchers_at_once_try_every_request_exactly_once` |
| 5 | A reset lifts a lockout and confirms the email in one go | `ResetPasswordTests.Reset_lifts_a_lockout_and_confirms_the_email_in_one_go` |

## Contract sentences that are not acceptance criteria

| Sentence | Guarding test |
| -------- | ------------- |
| A reset with an unusable token is reported as such before a weak password is | `ResetPasswordTests.Unusable_token_is_reported_before_a_weak_password` |
| A reset does not clear the mail limit | `ResetPasswordTests.Reset_does_not_clear_the_mail_limit` |
| The password policy does not block a login with a password set before it | `PasswordPolicyTests.Policy_does_not_block_a_login_with_a_password_set_before_it` |
| Login does not apply the length limit of the mail endpoints | `EmailInputTests.Login_does_not_apply_the_length_limit_of_the_mail_endpoints` |
| Only `POST` is allowed on the mail endpoints | `MailRequestEndpointTests.Only_post_is_allowed` |

## Plan-vs-implementation notes

- **Tasks 1–5 and 7–11:** the plan's code compiled and passed the analyzers as written; no name or
  behaviour differs from the plan. What is worth knowing about each task follows.
- **Task 1:** the helpers of the login handler moved to `JsonObjectBody`; login now validates its
  email with `EmailInput.TryNormalize`. The password options and `UnicodePasswordValidator` (letters and
  digits of any script) replace Identity's defaults.
- **Task 2:** the mail settings are loaded and checked at host start; `AuthAppFactory` pins default
  settings (`smtp.invalid:587`, starttls, locale `en`) so a test host never reaches a real server.
- **Task 3:** the migration is `20260127210512_AddEmailFlows` and only adds three tables.
- **Task 6:** the dispatcher differs from the plan in three places, found by the task review (fix commit
  `fix(email): time the next mail attempt from the failure`). The plan computed the next attempt time
  from a clock reading taken before the send, so a send that hung to its 20 s deadline made the retry
  due at once and retries 1–3 ran without a backoff; it now reads the clock after the failure. Issuing
  the token and composing the mail moved inside the `try`, so a failure there is a failed attempt
  (rolled back to the savepoint) instead of an exception that aborts every pass while its row stays
  first in line. The give-up log line was reworded so that a request with no recorded failure reads
  correctly. One test was added: `MailDispatcherTests.Next_attempt_is_timed_from_the_failure_not_from_the_start`.
- **Task 8:** a session now carries the account's security stamp (`SessionPolicy.StampClaim`, written at
  login, compared at refresh): this is the owner's answer to open question 8. A refresh token issued
  before this change has no stamp and is refused at its next refresh.
- **Task 10:** the optional second seed user (`Auth:DevSeed:UnverifiedEmail` / `UnverifiedPassword`) is
  created only in Development and only when both values are set.
- **Task 12:** `scripts/e2e-email.sh` follows the plan's helper code, with three small additions. Every
  helper that prints a value from `python3` strips a carriage return, because a Windows interpreter ends
  its lines with CR LF and the value would not compare equal in bash. `refresh` sends the cookie of step
  5 through a header file, as `e2e-refresh.sh` does, so no cookie reaches a command line. The script's
  exit trap starts the mail catcher again if the run dies during the outage of step 9. The compose
  service `mailpit` publishes only the web UI and API port, on loopback; SMTP stays inside the
  compose network.

Step 9 of the e2e script, on the development machine (Windows, Docker Desktop): with the mail
catcher stopped, `forgot` answered `202` in 0.22 s; the log showed the failed first attempt; the
mail arrived **9 s** after the request, once the catcher was back, through one of the dispatcher's
retries (5 s, 30 s and 2 min after the failures). All four e2e scripts ended with `ALL PASS` on one
clean stack, the three existing ones unedited.

## Local verification log

_To be filled after the three local verifiers have run, per
[`docs/workflow.md`](../../workflow.md#verification)._

| Round | Realization vs spec | API / e2e | Security | Fix commit |
| ----- | ------------------- | --------- | -------- | ---------- |
