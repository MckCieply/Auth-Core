# Spec 0006 — acceptance map

Maps each acceptance criterion of
[spec 0006](../specs/0006-python-consumer-package.md) to the tests that guard it,
for verifier layer 2 ([`docs/workflow.md`](../../workflow.md#verification)).
The package's tests live in `clients/python/tests/` and the sample's in `samples/notes-api/tests/`; both run with
`pytest` and need no Docker (the sample's can also run on PostgreSQL, see "How the tests run"). The e2e steps are in
`scripts/e2e-notes.sh` and run against the compose stack with the sample overlay (Auth-Core, PostgreSQL, the Mailpit
mail catcher, the notes service and Caddy) over real HTTP, through the proxy.

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | The Goal sequence and all six steps of `scripts/e2e-notes.sh` pass on a clean stack; the four earlier e2e scripts and the one of spec 0005 still pass on the stack without the overlay | `scripts/e2e-notes.sh` (steps 1–6); `scripts/e2e-login.sh`, `scripts/e2e-refresh.sh`, `scripts/e2e-lockout.sh`, `scripts/e2e-email.sh`, `scripts/e2e-tenancy.sh` unedited, run on `deploy/docker-compose.yml` alone, which this slice does not change |
| 2 | A token that meets every rule is accepted, and its `Principal` carries `sub`, `org_id`, `roles` and `permissions` | `test_tokens.test_valid_token_gives_the_principal_of_its_claims`, `test_tokens.test_the_scheme_is_case_insensitive`, `test_tokens.test_a_token_with_empty_roles_and_permissions_is_valid_and_holds_none`, `test_tokens.test_a_token_expired_less_than_five_minutes_ago_is_still_valid`, `test_server.test_a_token_is_checked_against_the_keys_served_over_http`; e2e step 1 |
| 3 | `401`, empty body, `WWW-Authenticate: Bearer` for each of the listed cases | no header: `test_tokens.test_no_header_is_a_401`; a scheme other than `Bearer`: `test_tokens.test_a_scheme_other_than_bearer_or_no_token_is_a_401`, `test_tokens.test_a_header_that_is_not_bearer_and_one_token_is_a_401`, `test_tokens.test_a_valid_token_in_another_header_is_a_401`; two headers: `test_tokens.test_two_authorization_headers_are_a_401_even_when_both_are_valid`; not a JWS: `test_tokens.test_a_token_that_is_not_a_jws_is_a_401`; `none`, `HS256` with the public key, another algorithm: `test_tokens.test_alg_none_is_a_401`, `test_tokens.test_hs256_signed_with_the_public_key_is_a_401`, `test_tokens.test_an_algorithm_other_than_rs256_is_a_401_even_with_a_good_signature`, `test_jwks_cache.test_only_rsa_keys_are_taken_from_the_key_set`; `typ`: `test_tokens.test_a_type_other_than_at_jwt_is_a_401`; `kid`: `test_tokens.test_a_token_without_a_usable_kid_is_a_401`, `test_tokens.test_a_kid_that_is_not_text_is_a_401`; a changed signature: `test_tokens.test_the_key_is_chosen_by_kid_alone_whatever_the_header_offers`, `test_tokens.test_a_changed_signature_is_a_401`, `test_tokens.test_a_changed_payload_is_a_401`, `test_tokens.test_a_token_signed_by_another_key_with_a_published_kid_is_a_401`; `iss`, `aud`: `test_tokens.test_a_wrong_issuer_is_a_401`, `test_tokens.test_a_wrong_audience_is_a_401`, `test_tokens.test_an_audience_that_is_a_list_is_a_401_even_when_it_holds_the_configured_one` (`aud` must equal the configured string; Auth-Core writes it as one); expired: `test_tokens.test_a_token_expired_more_than_five_minutes_ago_is_a_401`; missing or mistyped claims: `test_tokens.test_a_missing_claim_or_one_of_the_wrong_type_is_a_401`, `test_tokens.test_a_token_without_a_registered_claim_is_a_401`, `test_tokens.test_a_token_issued_in_the_future_is_a_401`, `test_tokens.test_a_time_claim_that_is_not_a_json_number_is_a_401` (`exp`, `iat`, `nbf` as strings or booleans), `test_tokens.test_a_time_claim_written_as_text_is_a_401_even_when_it_looks_like_a_float`, `test_tokens.test_time_claims_that_are_json_floats_are_accepted`, `test_tokens.test_a_float_expiry_in_the_past_is_still_a_401`, `test_tokens.test_a_not_before_in_the_past_is_accepted_and_one_in_the_future_is_a_401`, `test_jwks_cache.test_a_key_without_a_kid_is_ignored`, `test_notes.test_no_token_is_a_401_on_every_endpoint`; e2e step 2 |
| 4 | `require_permission` answers `403 {"error":"forbidden"}` for a valid token without the permission, and lets a token with it through | `test_tokens.test_a_valid_token_without_the_permission_is_a_403`, `test_tokens.test_a_token_with_the_permission_is_let_through`, `test_tokens.test_the_permission_is_matched_whole`, `test_tokens.test_an_invalid_token_is_a_401_before_it_is_a_403`, `test_notes.test_a_viewer_may_read_and_may_not_write`, `test_notes.test_a_writer_without_the_read_permission_may_not_read`, `test_notes.test_a_refused_write_stores_nothing`; e2e steps 4, 5 |
| 5 | Keys: a new key after one refetch; at most one refetch per 10 seconds; held keys keep working while the URL fails; `503 {"error":"auth_unavailable"}` with no key and a failing URL | the cache: `test_jwks_cache.test_the_keys_are_fetched_on_first_use_and_kept`, `test_jwks_cache.test_a_new_key_is_accepted_after_one_refetch`, `test_jwks_cache.test_a_new_key_is_not_looked_for_within_ten_seconds_of_the_last_fetch`, `test_jwks_cache.test_many_unknown_kids_cause_one_fetch_in_ten_seconds`, `test_jwks_cache.test_the_keys_are_fetched_again_after_five_minutes_not_before`, `test_jwks_cache.test_a_key_that_left_the_key_set_stops_working_at_the_next_fetch`, `test_jwks_cache.test_held_keys_keep_working_while_the_fetch_fails`, `test_jwks_cache.test_a_failed_renewal_is_retried_no_sooner_than_ten_seconds_later`, `test_jwks_cache.test_no_key_and_a_failing_key_set_is_unavailable`, `test_jwks_cache.test_unavailable_stays_the_answer_for_ten_seconds_without_another_fetch`, `test_jwks_cache.test_the_answer_to_an_unknown_kid_names_the_state_of_the_latest_fetch`, `test_jwks_cache.test_unavailable_does_not_need_the_failed_fetch_to_have_run_for_the_request`, `test_jwks_cache.test_a_key_set_with_nothing_usable_counts_as_a_failed_fetch`, `test_jwks_cache.test_requests_that_arrive_during_the_first_fetch_wait_for_it_and_share_it`, `test_jwks_cache.test_the_requests_that_wait_for_the_first_fetch_are_woken_when_it_ends`, `test_jwks_cache.test_a_request_that_waits_for_the_first_fetch_gives_up_when_the_fetch_may_not_take_longer`, `test_jwks_cache.test_after_the_first_fetch_has_ended_a_cold_cache_is_unavailable_at_once_while_a_fetch_runs`, `test_jwks_cache.test_no_request_of_a_product_that_has_just_started_is_turned_away_while_the_first_fetch_ends`, `test_jwks_cache.test_requests_with_an_unknown_kid_do_not_wait_for_a_fetch_that_is_running_and_share_it`, `test_jwks_cache.test_a_request_that_holds_its_key_does_not_wait_for_a_fetch_that_is_running`, `test_jwks_cache.test_a_request_that_lacks_its_key_is_unavailable_at_once_while_a_fetch_runs`, `test_jwks_cache.test_a_clock_that_raises_after_a_fetch_does_not_leave_the_cache_waiting_for_a_fetch_that_has_ended`, `test_jwks_cache.test_an_attempt_whose_end_could_not_be_timed_still_counts_as_an_attempt`, `test_jwks_cache.test_an_attempt_that_ends_on_something_that_is_not_an_exception_still_counts_as_an_attempt`, `test_jwks_cache.test_the_fetch_gives_up_at_its_timeout`, `test_jwks_cache.test_a_server_that_trickles_the_answer_is_given_up_on_at_the_deadline_of_the_whole_fetch`, `test_jwks_cache.test_the_deadline_reaches_the_cache_as_an_unavailable_key_set`, `test_jwks_cache.test_a_worker_that_ends_on_something_that_is_not_an_exception_is_a_failed_fetch`, `test_jwks_cache.test_the_defaults_are_the_rules_of_the_spec`, over HTTP: `test_jwks_cache.test_the_keys_are_fetched_over_http`, `test_jwks_cache.test_an_error_status_is_a_failed_fetch`, `test_jwks_cache.test_an_answer_that_is_not_a_key_set_is_a_failed_fetch`, `test_jwks_cache.test_a_key_set_just_under_the_size_limit_is_fetched`, `test_jwks_cache.test_a_valid_key_set_over_the_size_limit_is_refused_for_its_size`, `test_jwks_cache.test_nobody_listening_is_a_failed_fetch`; how the key set is fetched: `test_jwks_cache.test_the_fetch_ignores_proxy_environment_variables`, `test_jwks_cache.test_a_redirect_is_a_failed_fetch_and_is_not_followed`, `test_jwks_cache.test_a_key_set_url_that_is_not_http_or_https_is_refused`, `test_jwks_cache.test_http_and_https_key_set_urls_are_accepted`; times counted from the end of a fetch: `test_jwks_cache.test_the_five_minutes_and_the_ten_seconds_count_from_the_end_of_a_fetch`, `test_jwks_cache.test_a_failed_fetch_counts_from_its_end_too`; held keys expire 24 hours after the last good fetch (Decision 10): `test_jwks_cache.test_the_longest_a_held_key_outlives_the_last_good_fetch_is_24_hours`, `test_jwks_cache.test_a_held_key_works_until_24_hours_after_the_last_good_fetch_while_the_fetch_fails`, `test_jwks_cache.test_the_24_hours_count_from_the_last_fetch_that_worked_not_from_the_last_try`, `test_jwks_cache.test_a_fetch_that_works_again_restores_the_keys_after_the_limit`, `test_jwks_cache.test_a_key_older_than_24_hours_is_not_served_while_a_fetch_runs`, `test_keys.test_a_known_key_is_a_503_once_the_keys_are_a_day_old_and_the_key_set_still_fails`; through the dependencies: `test_keys` (all), `test_server.test_an_unknown_kid_is_a_401_and_a_key_set_that_does_not_answer_is_a_503`, `test_server.test_a_slow_fetch_does_not_block_the_event_loop`, `test_notes.test_the_service_answers_503_while_auth_core_is_down_and_it_holds_no_key`; e2e step 6, `test_jwks_cache.test_a_min_interval_that_is_not_positive_is_refused` (a pause of 0 or less between fetches is refused), `test_jwks_cache.test_a_small_positive_min_interval_is_accepted` |
| 6 | Creating the `AuthCore` object makes no network call | `test_core.test_creating_the_object_makes_no_network_call`, `test_jwks_cache.test_creating_the_cache_fetches_nothing`, `test_keys.test_the_keys_are_fetched_on_first_use_and_kept` |
| 7 | No token or claim value appears in the package's logs at any level | `test_logging.test_no_token_and_no_claim_value_is_logged_at_any_level`, `test_logging.test_a_rejected_token_is_logged_at_debug_with_the_reason_only`, `test_logging.test_a_rejected_token_is_not_logged_above_debug`, `test_logging.test_a_failed_fetch_is_logged_as_a_warning_by_the_class_of_the_error_only` |
| 8 | The sample never returns or changes a note of another company, whatever id it is given; another company's note and a non-UUID id are both `404` | `test_notes.test_another_companys_note_is_a_404_and_its_list_is_empty`, `test_notes.test_an_id_that_is_not_a_uuid_is_a_404`, `test_notes.test_only_the_written_form_of_an_id_finds_a_note`, `test_notes.test_the_company_of_a_new_note_is_the_tokens_whatever_the_body_says`, `test_notes.test_two_companies_keep_their_own_notes`, `test_notes.test_every_statement_on_the_notes_table_names_the_company`, `test_notes.test_a_valid_token_whose_company_is_not_a_uuid_is_a_401`; e2e step 3 |
| 9 | The package's tests run with `pytest` and no Docker, with keys generated in the test | all of `clients/python/tests/` (`helpers.Signer` makes the keys; `helpers.FakeJwks` and `helpers.JwksServer` stand in for the JWKS URL, the latter a server on 127.0.0.1 that the test starts) |
| 10 | The integration guide covers the eight steps, and every file it points at exists in the sample | the commands of Task 8, step 3 (the eight headings; every link and every path in code spans resolves) |

## How the tests run

- `cd clients/python && python -m pytest -q` — the package: 206 tests.
- `cd samples/notes-api && python -m pytest -q` — the sample on SQLite in memory (the real migration builds the table):
  97 tests. With `NOTES_TEST_DATABASE_URL=postgresql+psycopg://…` (an empty database) the same tests run on PostgreSQL,
  which is where the NUL character and a lone surrogate in a note are refused by the service and not by the database.
- The e2e script needs Docker, the Auth-Core image and the code of spec 0005 (the company API and the operator CLI).

## Review Focus (plan 0006)

| # | Failure mode | Guarding test(s) |
| - | ------------ | ---------------- |
| 1 | A note whose text the database cannot store (a NUL character, a lone surrogate), is too long, whose body is not JSON, or whose JSON is nested so deeply that parsing it fails, is a `400 invalid_request`, never a `500` | `test_notes.test_a_body_that_is_not_an_object_with_a_good_text_is_a_400`, `test_notes.test_a_body_over_the_size_cap_is_a_400_even_when_its_text_is_good`, `test_notes.test_a_deeply_nested_body_under_the_size_cap_is_a_400`, `test_notes.test_a_body_under_the_size_cap_may_carry_other_members`, `test_notes.test_a_bad_body_is_a_401_or_a_403_before_it_is_a_400`, `test_notes.test_the_text_may_be_one_to_a_thousand_characters_of_any_script` |
| 2 | Auth-Core slow or hung when a key is needed: no other request waits for it, the fetch gives up at its timeout, N requests that ask during a fetch share it, a request that holds its key never waits, until the first fetch has ended a request waits for it (no longer than the fetch timeout, and it is woken when the fetch ends), and after that a request that lacks its key while a fetch runs is unavailable (503) at once, whether the first fetch worked or not (ruling V7), so a flood of unknown `kid`s cannot hold the thread pool, not even once in 15 seconds while Auth-Core hangs; a clock that raises cannot leave the cache believing a fetch still runs | `test_server.test_a_slow_fetch_does_not_block_the_event_loop`, `test_jwks_cache.test_the_fetch_gives_up_at_its_timeout`, `test_jwks_cache.test_requests_that_arrive_during_the_first_fetch_wait_for_it_and_share_it`, `test_jwks_cache.test_the_requests_that_wait_for_the_first_fetch_are_woken_when_it_ends`, `test_jwks_cache.test_a_request_that_waits_for_the_first_fetch_gives_up_when_the_fetch_may_not_take_longer`, `test_jwks_cache.test_after_the_first_fetch_has_ended_a_cold_cache_is_unavailable_at_once_while_a_fetch_runs`, `test_jwks_cache.test_no_request_of_a_product_that_has_just_started_is_turned_away_while_the_first_fetch_ends`, `test_jwks_cache.test_requests_with_an_unknown_kid_do_not_wait_for_a_fetch_that_is_running_and_share_it`, `test_jwks_cache.test_a_request_that_holds_its_key_does_not_wait_for_a_fetch_that_is_running`, `test_jwks_cache.test_a_request_that_lacks_its_key_is_unavailable_at_once_while_a_fetch_runs`, `test_jwks_cache.test_a_clock_that_raises_after_a_fetch_does_not_leave_the_cache_waiting_for_a_fetch_that_has_ended` |
| 3 | Auth-Core is back after an outage: the answer stays `503` (not `401`, so the user stays signed in) until the 10 seconds since the failed fetch have passed | `test_jwks_cache.test_unavailable_stays_the_answer_for_ten_seconds_without_another_fetch`, `test_keys.test_an_unknown_kid_is_a_503_when_the_latest_fetch_failed_and_a_401_when_it_worked`; e2e step 6 (polls) |
| 4 | A valid token whose `org_id` is not a UUID, and an id in another spelling (`{…}`, without hyphens, `urn:uuid:`, a space, SQL text) never reach a query as another company's row and never end as a `500` | `test_notes.test_a_valid_token_whose_company_is_not_a_uuid_is_a_401`, `test_notes.test_only_the_written_form_of_an_id_finds_a_note`, `test_notes.test_an_id_that_is_not_a_uuid_is_a_404` |
| 5 | Odd headers and a token on the edge of expiry: two `Authorization` headers, a lower-case scheme, trailing text, a token expired 200 seconds ago (valid) and 400 seconds ago (not), a product that forgot `auth.install(app)` still answers with the right status and headers | `test_tokens.test_two_authorization_headers_are_a_401_even_when_both_are_valid`, `test_tokens.test_the_scheme_is_case_insensitive`, `test_tokens.test_a_header_that_is_not_bearer_and_one_token_is_a_401`, `test_tokens.test_a_token_expired_less_than_five_minutes_ago_is_still_valid`, `test_tokens.test_a_token_expired_more_than_five_minutes_ago_is_a_401`, `test_tokens.test_without_install_the_status_and_the_headers_are_still_right` |

## Contract sentences that are not acceptance criteria

| Sentence | Guarding test |
| -------- | ------------- |
| JSON bodies carry `Cache-Control: no-store` (the package's `401`, `403`, `503`, and the sample's answers) | `helpers.assert_unauthorized` and `helpers.assert_unavailable` (used by `test_tokens`, `test_keys`, `test_server`), `test_tokens.test_a_valid_token_without_the_permission_is_a_403`, `test_notes.test_every_answer_is_marked_never_to_be_stored` |
| `GET /api/notes` lists the company's notes newest first, as `[{"id","text","author_sub","created_at"}]` | `test_notes.test_a_note_is_added_and_listed`, `test_notes.test_notes_are_listed_newest_first` |
| `GET /api/notes/{id}` is one note; `POST` is `201` with the note; the text is 1–1000 characters | `test_notes.test_a_note_is_read_by_its_id`, `test_notes.test_the_text_may_be_one_to_a_thousand_characters_of_any_script` |
| `GET /api/health` is `200` when the database answers, needs no token | `test_notes.test_health_is_200_when_the_database_answers_and_needs_no_token`, `test_notes.test_health_is_503_when_the_database_does_not_answer` |
| Every error the sample answers is `{"error": ...}` and never stored: an unknown path `404`, a wrong method `405` (the `Allow` header lists every method of the path, `GET, POST` on `/api/notes`), a database that does not answer, or a connection pool that runs out, `503 database_unavailable` on every notes endpoint, anything else `500 internal_error` (answered by the sample, not raised again, and logged by its class alone: no message and no traceback); a trailing slash is a `404`, not a redirect | `test_shapes_and_limits.test_an_unknown_path_is_a_json_404_that_is_never_stored`, `test_shapes_and_limits.test_a_method_that_is_not_allowed_is_a_json_405_that_lists_every_method_the_path_allows`, `test_shapes_and_limits.test_the_allow_header_of_every_route_lists_all_the_methods_of_its_path`, `test_shapes_and_limits.test_the_trailing_slash_is_not_redirected`, `test_shapes_and_limits.test_a_database_that_does_not_answer_is_a_503`, `test_shapes_and_limits.test_the_database_error_is_logged_by_its_class_and_not_by_its_text`, `test_shapes_and_limits.test_a_database_that_does_not_answer_is_logged_once_per_request_by_its_class_on_the_notes_endpoints` (each notes request leaves one line), `test_shapes_and_limits.test_the_health_check_logs_a_database_that_does_not_answer_once_per_outage`, `test_shapes_and_limits.test_the_health_check_logs_again_when_the_database_fails_after_it_has_recovered`, `test_shapes_and_limits.test_both_ends_of_an_outage_are_logged_at_the_level_the_container_runs_with`, `test_shapes_and_limits.test_a_healthy_database_leaves_no_line_in_the_log_from_the_health_check`, `test_shapes_and_limits.test_the_notes_endpoints_keep_logging_once_per_request_while_the_health_check_stays_quiet` (the health check logs the start of an outage once, and its end), `test_shapes_and_limits.test_the_connection_pool_running_out_is_a_503_like_a_database_that_does_not_answer`, `test_shapes_and_limits.test_an_unexpected_error_is_a_json_500_that_is_never_stored`, `test_shapes_and_limits.test_an_unexpected_error_is_logged_by_its_class_alone`, `test_shapes_and_limits.test_an_error_after_the_answer_has_started_is_not_logged_with_its_text` |
| The image is built from pinned inputs: the base images and Caddy by digest, every package of the sample at an exact version (`requirements.txt`, for development; `requirements-image.txt`, the same versions with the hash of each file, for the image, installed with `--require-hashes --only-binary=:all:`; the build tool of the package, hatchling and its dependencies, in `requirements-build.txt`, pinned and hashed the same way, and the package built from it with `--no-build-isolation --no-deps`) | `test_image_files.test_the_list_for_development_is_all_exact_pins_without_hashes_so_that_it_installs_on_any_system`, `test_image_files.test_the_list_for_the_image_has_the_same_pins_and_a_hash_for_every_one`, `test_image_files.test_the_list_holds_what_the_sample_asks_for_and_what_that_needs`, `test_image_files.test_the_dockerfile_installs_the_libraries_from_the_list_with_hashes_and_wheels_only`, `test_image_files.test_the_build_tool_and_what_it_needs_are_pinned_and_hashed`, `test_image_files.test_the_version_of_the_build_tool_is_the_one_the_package_asks_for`, `test_image_files.test_the_dockerfile_builds_the_package_from_the_pinned_build_tool_and_fetches_nothing_unpinned`, `test_image_files.test_the_images_of_the_overlay_are_pinned_by_digest` |
| An over-limit body is not read to its end (the answer closes the connection); a client that goes away in the middle of a body is not an error; the PostgreSQL connection has a connect timeout; the settings do not show the database password | `test_shapes_and_limits.test_an_over_limit_body_is_not_read_to_its_end`, `test_shapes_and_limits.test_a_client_that_goes_away_in_the_middle_of_the_body_is_not_an_error`, `test_shapes_and_limits.test_the_database_connection_has_a_timeout_for_postgresql_only`, `test_shapes_and_limits.test_the_settings_do_not_show_the_database_password` |
| The model and the migration agree on the default of `created_at`; the migration runs from a path that holds a `%` | `test_shapes_and_limits.test_the_model_and_the_migration_agree_on_the_default_of_created_at`, `test_shapes_and_limits.test_a_path_with_a_percent_sign_does_not_break_the_migration`, `test_migration_and_settings.test_the_migration_and_the_model_agree` (with server defaults compared) |
| The service applies its migrations at startup; the first migration creates the table of the spec | `test_notes.test_the_service_applies_its_migrations_at_startup`, `test_migration_and_settings.test_the_migration_creates_the_table_of_the_spec`, `test_migration_and_settings.test_the_migration_and_the_model_agree`, `test_migration_and_settings.test_migrating_twice_changes_nothing` |
| The sample's configuration is `AUTH_ISSUER`, `AUTH_AUDIENCE`, `AUTH_JWKS_URL`, `DATABASE_URL` | `test_migration_and_settings.test_the_settings_are_read_from_the_environment`, `test_migration_and_settings.test_a_missing_setting_is_named_and_its_value_is_not` |
| The default key set URL is the issuer plus `/.well-known/jwks.json`; a URL that is not http(s), an empty issuer, audience or permission is refused | `test_core.test_the_default_key_set_url_is_the_issuer_plus_the_well_known_path`, `test_core.test_a_key_set_url_that_is_not_http_is_refused`, `test_core.test_an_empty_issuer_or_audience_is_refused`, `test_core.test_an_empty_permission_is_refused` |
| The sample is reachable only through Caddy | `scripts/e2e-notes.sh` step 1 (`docker port` of the sample prints nothing), `Caddyfile` and `compose.yml` (`expose`, no `ports`) |

## Plan-vs-implementation notes

One line per task; where the commit differs from the plan text, the difference is stated.

- Task 5 (commit 260f9b5): a deeply nested JSON body (a `RecursionError` while parsing) answers `400 invalid_request`, guarded by the new
  `test_notes.test_a_deeply_nested_body_under_the_size_cap_is_a_400`, so the sample has 58 tests, not 57. The unused `datetime` import and
  `new_id` were removed.
- Task 8 (commit a60e9c7): the guide and the overlay comment say that the backend publishes no port and that Auth-Core's port is
  loopback-only in development; the plan text said that neither publishes a port. The snippet of step 4 imports `Depends`.
- The fix wave after the whole-branch review (commit 4a3ed88):
  - `_jwks.py`: a request that lacked its key waited for a running fetch at most `timeout` (5 s) and then got a 503. Replaced in
    verification round 1 (below): it no longer waits at all.
  - `pyproject.toml` of the package: the floors are the tested versions (`fastapi>=0.142`, `PyJWT[crypto]>=2.15`) and the version is
    single-sourced from `__version__` in `__init__.py` through hatch (`dynamic = ["version"]`). The guide says "FastAPI 0.142 or newer".
  - `scripts/e2e-notes.sh`: runs as the compose project `auth-core-notes` (`COMPOSE_PROJECT_NAME`, overridable) so that it does not share
    containers and volumes with the `auth-core` stack of other checkouts; `cli()` prints the CLI's stderr when the CLI fails;
    `AUTH_STOPPED=1` is set before `stop auth`, so the trap restarts Auth-Core even when the stop fails halfway. README and the script
    header name the project for the `down -v`.
  - `.gitignore`: `*.egg-info/` and the build and dist directories of the package and the samples.
  - The guide: `auth.install(app)` covers only the app it is called on (call it on each mounted sub-app); the sample's list endpoint
    has no paging and a real product should page.
  - This map: the new test is mapped (criterion 5, Review Focus 2).
- Verification round 1, fixes (commits e4c2a6f to 4f1b07b; the controller's rulings V1 to V4 and the owner's addendum):
  - `_jwks.py`, V1: a request whose `kid` is not held and that finds a fetch running does not wait for the lock (a non-blocking acquire):
    it gets `KeysUnavailable` (503) at once. A request that holds its key never waits; the 10-second limit counts failures; 401 or 503
    follows the latest fetch. V1b (controller): this holds for a WARM cache only (a fetch has worked). On a cold cache (no fetch has ever
    worked) a request waits for the first fetch, for the fetch timeout at most, then `KeysUnavailable`, so that the first users of a product
    that has just started are not told "try again". The cache keeps "a fetch is running" in its own flag guarded by a `Condition` that is
    never held during a fetch (the lock of the first version made a request look at a lock held by a queue of waiters and refuse it:
    2 to 4 of 120 requests in the cold-start probe). The "bounded wait" test is replaced by
    `test_a_request_that_lacks_its_key_is_unavailable_at_once_while_a_fetch_runs` (warm) and
    `test_requests_that_arrive_during_the_first_fetch_wait_for_it_and_share_it` (cold).
  - `_jwks.py`, V2: the fetch uses one opener with `ProxyHandler({})` (the proxy environment variables, and the registry proxy on
    Windows, are ignored) and no redirect handler (a 3xx is a failed fetch); `JwksCache` refuses a URL that is not http or https.
    The suite passes with `HTTP_PROXY` set to a dead address.
  - `_verify.py`, V3 and V4d: `aud` must be the configured string (Auth-Core writes `aud` as a JSON string: the decoded token in
    `verify-probes.log` shows `"aud": "notes-api"`), so a list that holds it is a 401; `exp`, `iat` and `nbf` must be JSON numbers (a
    string or a boolean is a 401). The test of the list that was accepted is gone.
  - `_jwks.py`, owner's rulings: held keys are kept on a failed fetch for 24 hours after the last good fetch (`MAX_STALE_SECONDS`,
    spec Decision 10), after that a 503 until a fetch works; `fetched_at` and the 10-second mark are read from the clock after the fetch
    returns (the spec says "at most once every 10 seconds", which this keeps; a slow fetch makes the gap between two starts longer,
    not shorter); the worker catches `Exception`, not `BaseException`.
  - Sample: `Settings` hides `database_url` from `repr` (V4a); `redirect_slashes=False` (V4b); `_read_text` stops at the cap and the
    answer carries `Connection: close`, `ClientDisconnect` is handled quietly, the PostgreSQL engine has `connect_timeout` 5 (V4c);
    404, 405, a database error and any other error are JSON `{"error": ...}` with `no-store` (A); the model has
    `server_default=func.now()` and the agreement test compares server defaults; `%` in the path of `script_location` is escaped (F).
    New file `tests/test_shapes_and_limits.py`; the sample has 78 tests.
  - Overlay: Caddy `caddy:2.11.7`, uid 10002 with tmpfs `/data` and `/config`; `notes-db-init` runs
    `REVOKE CONNECT ON DATABASE auth FROM PUBLIC`; `notes-api` has a healthcheck on `/api/health` (Python standard library) and Caddy waits
    for `service_healthy` (B). The sample's `requirements.txt` has exact versions and the Dockerfile installs it before the package (C).
    `.dockerignore` and `.gitattributes` (D).
  - `scripts/e2e-notes.sh` (V4e, G): no `eval` of a value of the service (ids are checked as UUIDs and read from `os.environ`); the
    expected issuer and audience are read from the sample's container environment, not from `BASE_URL`; the refresh cookie must show
    `Path=/auth`, `HttpOnly`, `Secure`, `SameSite=Strict`; `wait_ok` and `wait_mail` count seconds by the clock; a failing `val` in
    `keep_session` fails; the trap's restart shows its errors.
  - Tests: the misnamed `test_a_token_without_roles_or_permissions_is_valid_and_holds_none` is now
    `test_a_token_with_empty_roles_and_permissions_is_valid_and_holds_none`; the wall-clock bounds are wider; the event-loop test checks
    that the server had the request before the ping; `test_keys` checks the fetch count; the port-9 assumption is a closed ephemeral port.
  - Guide: the install is pinned to the commit SHA for production; the key set URL is fetched without proxies and redirects; held keys
    last 24 hours.
- Verification round 2, fixes (the changes of this round; ruling V7 and the owner's rule that every finding is fixed):
  - `_jwks.py`, spec N1: the opener of the fetch (`ProxyHandler({})`, no redirect) is built for each fetch, not once at import, so
    that the proxy test, which sets the variable after the import, fails when `ProxyHandler({})` is removed (shown by mutation).
  - `_jwks.py`, V7 (security N1, spec N8): a cold cache waits only for the FIRST fetch attempt, whatever its result (a flag
    `_first_attempt_done`). After that it answers `KeysUnavailable` (503) at once while a fetch runs: with a hung Auth-Core,
    `/api/health` of the sample no longer stalls every 15 s (probe `p11_cold_hung_e2e`: one stall of 5.06 s at the start, none after).
  - `_jwks.py`, security N2: the flag "a fetch is running" is reset, and the waiters are woken, in a `finally` that cannot be skipped
    by a clock that raises (the clock is read inside the `try`; when it raises, that attempt records nothing and counts as failed).
  - `_jwks.py` and `_verify.py` tests, spec N2 to N5: waiters are woken when the first fetch ends (bound 1.5 s of a 4 s limit); a worker that
    ends on `SystemExit` is a failed fetch; a key older than 24 hours is not served while a fetch runs; `exp`, `iat`, `nbf` as JSON floats
    are accepted, as text that looks like a float they are refused. Each test fails under the mutation named for it in the report.
  - Sample, security N3 and spec N6: a new innermost middleware, `_InternalErrors`, answers the 500 itself and does not raise it again
    (the framework's last layer logged the traceback and the message, and re-raised it for the server to log again); it logs the class at
    error level. The handler for `Exception` is gone. A connection pool that runs out (`sqlalchemy.exc.TimeoutError`) is a 503
    `database_unavailable` like a database that does not answer. The log tests carry a statement, a driver message and a value in the
    error, so a handler that logs `str(exc)` fails them.
  - Sample, security N4: a `405` has `Allow` with the methods of all the routes of the path (`GET, POST` on `/api/notes`), not of the
    first one; tested for every route of the app.
  - Overlay, security N5: `notes-db-init` also runs `REVOKE CONNECT ON DATABASE postgres FROM PUBLIC` and the same for `template1` (run twice on
    a throwaway PostgreSQL 16: the login `notes` cannot connect to `auth`, `postgres`, `template1`, connects to `notes`; the superuser
    `auth` connects to all four; `pg_isready -U auth -d auth` is accepted). Caddy: `read_only: true`, `cap_drop: [ALL]`,
    `security_opt: [no-new-privileges:true]`, tmpfs kept; and `cap_add: [NET_BIND_SERVICE]`, which the plan did not name: the image's
    `caddy` binary carries that file capability, and without it in the set the binary does not start ("operation not permitted", seen in a throwaway run).
    With it, Caddy runs as uid 10002 with only that capability, a read-only root filesystem, answers on 8088 and writes `/data`.
  - Supply chain, security N6: the images of the overlay and of the Dockerfile are pinned by digest (`python:3.12-slim@sha256:...`,
    `caddy:2.11.7@sha256:...`, `postgres:16-alpine@sha256:...`, read from the local images). `requirements.txt` lists the full set of 24
    packages at exact versions (the closure of the sample's dependencies, from the metadata of the tested environment). New
    `requirements-image.txt`: the same versions with the hashes of the wheels for Python 3.12 on Linux, x86_64 and aarch64 (made with
    `pip download` and `pip hash`); the Dockerfile installs it with `--require-hashes --only-binary=:all:`, installs the package with
    `--no-deps` and runs `pip check`. A test keeps the two lists equal. Two lists, because hashes of Linux wheels do not install on
    Windows or macOS, where the README's development install uses `requirements.txt`. The build backend of the package is pinned too: `requires = ["hatchling==1.32.4"]` in `clients/python/pyproject.toml`
    (approved by the owner; the version of the trial and of the tested environment). It has no hash: it is fetched at build, and is not in the image afterwards.
    `pip install ./clients/python` in a fresh venv still gives version 0.1.0.
  - The guide names `requirements-image.txt`.
- Verification round 3, fixes (the changes of this round; the owner approved a small round after three PASS verdicts):
  - `_jwks.py`, security R3-1: an attempt whose end could not be timed (the clock raises when it is read after the fetch, or the fetch
    ends on something that is not an `Exception`) is still recorded in `_attempted_at`, so that `min_interval` holds and the waiters that
    its end wakes are answered at once and not sent on to a second attempt (59 of 60 waited two chained attempts in the verifier's probe).
    The fallback is the clock reading made just before the attempt began (`key_for` passes it to `_fetch_and_store`): a reading of the same
    clock that is known to have worked, taken without one more read of a clock that may be failing. The interval then runs out at most
    `timeout` early, never late. Only a time read after the fetch dates the keys (`fetched_at`): a fallback never makes them look newer.
    Two new tests (the clock failing once at the end of the first fetch; a fetch ending in `SystemExit`) fail without the change (all five
    requests made an attempt of their own); the test of the raising clock now moves the clock past `min_interval` before its second request.
  - Dockerfile of the sample, security R3-2: the build tool and what it needs are pinned and hashed. New `requirements-build.txt`: hatchling
    1.32.4, packaging 26.3, pathspec 1.1.1, pluggy 1.6.0, tomlkit 0.15.1, trove-classifiers 2026.9.21.13 (what `pip download hatchling==1.32.4`
    resolves for Python 3.12, all `py3-none-any`, one hash each from `pip hash`). The Dockerfile installs it with `--require-hashes
    --only-binary=:all:`, builds the package with `--no-build-isolation --no-deps` (pip fetches nothing of its own for the build) and
    uninstalls the six afterwards. A throwaway build (log `verify-fix-r3-docker-build.log`) fetched only those six files; the image has no
    hatchling, `pip check` is clean and `auth_core_fastapi` 0.1.0 imports; the image was removed. Three new tests of `test_image_files`
    (pins and hashes; the version equals the one of `pyproject.toml`; the Dockerfile uses both flags and has no other `pip install`);
    the Dockerfile of the round before fails the last one.
  - The sample, e2e finding 6: after PostgreSQL was stopped, one log line was written for two 503 requests. The cause was a missing log,
    not a de-duplication: `/api/health` caught the error and answered 503 without logging it, while the notes endpoints go through the
    handler that logs. `/api/health` now logs `the database did not answer (<Class>)` too (class only). New test: two requests to each of
    the two paths leave one line each, no traceback; it fails for `/api/health` without the change.
  - The guide: the table of step 8 lists `503 {"error":"database_unavailable"}` (try again shortly, do not refresh, do not sign out), and
    step 1 names `requirements-build.txt`.
- Verification round 4, fixes (the last small pass; the owner's rule that every finding is fixed, Low and Info included, unless the fix
  contradicts the spec; the scoped verification of round 3 passed with L1, I1 and I2 below, and R3-3 of the security verifier):
  - `test_image_files`, L1: the build tool and what it needs are listed once, in the test (`BUILD_TOOL`: hatchling, packaging, pathspec,
    pluggy, tomlkit, trove-classifiers). `test_the_build_tool_and_what_it_needs_are_pinned_and_hashed` requires the pins of
    `requirements-build.txt` to be exactly that set, and `test_the_dockerfile_builds_the_package_from_the_pinned_build_tool_and_fetches_nothing_unpinned`
    requires the `pip uninstall` line to name exactly that set. Dropping the `tomlkit` pin from the list failed the first (it passed before).
  - The sample, I1: `/api/health` logs the database error once per outage, when it goes from up to down, and `the database answers again`
    (WARNING, see M1 below) once when it is back. The healthcheck of the compose file calls it every 5 s, which made one line per call. The state is a small
    class (`_Outage`, a flag under a lock; the health check runs in the thread pool) with one instance per app, not one for the module: so
    that apps made in different tests do not share an outage. In the product there is one app, so it is the same thing. Every call still
    answers 503 `database_unavailable`. The notes endpoints are unchanged: one line per failed request. Four new tests
    (`test_the_health_check_logs_a_database_that_does_not_answer_once_per_outage`, `test_the_health_check_logs_again_when_the_database_fails_after_it_has_recovered`,
    `test_a_healthy_database_leaves_no_line_in_the_log_from_the_health_check`,
    `test_the_notes_endpoints_keep_logging_once_per_request_while_the_health_check_stays_quiet`); the first, second and last failed before
    the change. The test of round 3 that expected one line per health call is now the notes-endpoints test
    (`test_a_database_that_does_not_answer_is_logged_once_per_request_by_its_class_on_the_notes_endpoints`).
  - The sample, M1 of the scoped verification of this round (`verify-r4fix.md`): the recovery line was logged at INFO, and in the container the
    `notes_api` logger is at WARNING (uvicorn configures only its own loggers), so the operator saw the outage begin and never end (live: one line,
    no recovery line). Both lines are now WARNING. New test `test_both_ends_of_an_outage_are_logged_at_the_level_the_container_runs_with`: it
    checks that the logger's effective level is WARNING, does not use `caplog.at_level`, and asserts both lines once across failure, failure, recovery,
    recovery; it failed before the change (the recovery line was missing).
  - `_jwks.py`, security R3-3 and verifier I2, first half: a stolen wakeup with `min_interval=0`. `JwksCache` refuses a `min_interval` that is
    not greater than 0 (`ValueError`, `nan` too). Nothing needs 0: the spec does not name the parameter, the default is 10 s, and no test or helper
    passes it (the tests move the injected clock instead). New tests: `test_a_min_interval_that_is_not_positive_is_refused` (0, 0.0, -1, nan; failed
    before) and `test_a_small_positive_min_interval_is_accepted`.
  - R3-3, second half, left as it is: an asynchronous exception (a gevent or eventlet timeout, `PyThreadState_SetAsyncExc`) delivered between
    `self._fetching = True` and the entry of the `try` in `_fetch_and_store`. The window is the two statements after the lock is left. It cannot be closed
    in pure Python: whatever is moved, an asynchronous exception can land between any two bytecodes, in a `finally` as well. Closing it would mean
    a different design (a worker thread that owns the flag, or a deadline on the flag that the next request reads), a larger change for a case
    that needs an injected asynchronous exception, and the effect is bounded: every request that finds the flag set while the cache is warm is
    answered `KeysUnavailable` at once, and a cold request waits `timeout` at most.
  - `scripts/e2e-notes.sh`: `mail_count` (the polling of `wait_mail`) no longer prints curl's error text (`-s`, was `-sS`): while Mailpit starts the
    first tries are refused (`curl: (7)`). It prints nothing and fails when the catcher does not answer, so the loop tries again, and `wait_mail`
    ends in the script's own message (`step 3: no invitation mail within 120s ...`). The readiness loop (`wait_ok`) was already silent; the other
    `curl` calls keep `-sS`. The file is still LF and mode 100755; `bash -n` is clean.
- Tasks 1-4, 6, 7, 9: as planned.

## Local verification log

Per [`docs/workflow.md`](../../workflow.md#verification), three local verifiers ran in each of the first three rounds:
realization vs spec, API / e2e and security. After those, two scoped passes (one verifier each: spec, security and e2e of
the changed files) checked the fixes. Each e2e run brought the stack up from `down -v` under a compose project of its own
(`auth-core-s6verify`). The reports are `verify-spec-s6-r1.md` to `r3`, `verify-e2e-s6-r1.md` to `r3`,
`verify-security-s6-r1.md` to `r3`, `verify-r3fix.md` and `verify-r4fix.md` in `.superpowers/sdd/0006-python-consumer-package/`.

| Round | Realization vs spec | API / e2e | Security | Fix commits |
| ----- | ------------------- | --------- | -------- | ----------- |
| 1 (on `4a3ed88`) | **FAIL** - package 132, sample 58; F1 a proxy environment variable broke 6 package tests; F2 the package's FastAPI floor (0.142) against the spec's 0.115 (owner); F3 `aud` as a list accepted; F4 a stale map; F5 a misnamed test; F6 a database failure answered as a plain `500` | PASS - `scripts/e2e-notes.sh` and the five earlier scripts ALL PASS; .NET 935/935; Python 132 and 58; probes through Caddy (unknown paths, traversal, bodies, ids, the shape of every `401`); non-blocking: an unknown path under `/api` answered FastAPI's `{"detail"}`, and the `Secure` refresh cookie on plain HTTP | **FAIL** - F1 (Medium) a flood of unknown `kid`s held the thread pool through the bounded lock wait; Lows F2 to F11: `aud` as a list, held keys never expiring, redirects and proxy variables in the key set fetch, the password in `repr(Settings)`, a `307` that reflects `Host`, a body read past its cap and no connect timeout, a mutable tag, Caddy as root and the `notes` login able to connect to the `auth` database, `eval` in the e2e script, numeric strings in `exp`, `iat` and `nbf` | `e4c2a6f` to `4f1b07b` |
| 2 (on `4f1b07b`) | **FAIL** - package 180, sample 78 (also under a dead proxy); name check 133; N1 (blocking) the test of the "no proxy" rule passed with the rule removed; N2 to N7: `notify_all`, the non-`Exception` guard, a key over 24 hours during a fetch, float `exp` and `iat`, log tests that did not look for the message of the error, a stale map; every finding of round 1 addressed except the FastAPI floor | PASS - the six scripts ALL PASS (`e2e-refresh.sh` on its second attempt: the first died in step 4 with `curl: (7)`, a lost Docker port and not a race of the script; four later runs passed); .NET 935/935; 180 and 78; the changed overlay live (Caddy as uid 10002, the healthcheck, the `REVOKE`, the `404`, `405` and `503 database_unavailable` shapes, a body cut at the cap) | PASS with Lows - N1 the wait of a cold cache repeating every 15 seconds with a hung Auth-Core; N2 the "fetch running" flag left set when the clock raises; N3 the traceback and message of a `500` in the log; N4 `Allow` of the first route only; N5 `postgres` and `template1` open to the `notes` login, Caddy without `read_only` and `cap_drop`; N6 no transitive pins, hashes or digests | `4f1b07b` to `b1209ed` |
| 3 (on `b1209ed`) | PASS - package 199, sample 88 (also under the dead proxy); name check 150; N1 to N7 addressed, each shown by a mutation; infos only | PASS - the six scripts ALL PASS (the `e2e-refresh.sh` failure did not reproduce in 4 runs); .NET 935/935; 199 and 88; Caddy `ReadonlyRootfs`, effective capabilities `0x400` only; the digests equal the local images; the image holds the 24 pins; a `500` shown live with no traceback; with a hung Auth-Core `/api/health` answers in 0.2 s; one information item: one log line for two `503`s | PASS - N1 to N6 closed (the probes run again); two new Lows: R3-1 an attempt whose end could not be timed did not count for the 10-second rule, R3-2 the build tool's own dependencies were not pinned; Infos R3-3 (a `min_interval` of 0, an asynchronous exception), R3-4, R3-5 | `b1209ed` to `d5e30ce` |

Scoped verification of the fixes:

| Pass | Result | Evidence | Fix commits |
| ---- | ------ | -------- | ----------- |
| Fixes of round 3 (on `d5e30ce`, `verify-r3fix.md`) | PASS - L1 (Low, test), I1 and I2 (Info) | R3-1 closed (putting the old block back fails two tests); R3-2 closed (nine mutations of `test_image_files` caught, one not: L1); the guide and the `/api/health` log line right (live: `503` twice, class only, `200` within 8 seconds of the restart); `scripts/e2e-notes.sh` steps 1 to 6 PASS on a clean stack; the image holds the 24 pins and none of the build tool; package 201 and sample 93, also under the dead proxy; name check 156 | `d5e30ce` to `22201eb` |
| Fixes of round 4 (on `22201eb`, `verify-r4fix.md`) | **FAIL** - M1 (Medium); L1 and I2 addressed | M1: the line "the database answers again" was logged at INFO, and in the container the sample's logger runs at WARNING, so the operator saw an outage begin and never end (live: one line, no recovery line); L1 and I2 shown by mutation; `scripts/e2e-notes.sh` ALL PASS with no `curl: (` in its log; no regression of the 10-second, 24-hour and cold-wait rules (probes run again); package 206 and sample 96, also under the dead proxy; name check 162 | `22201eb` to `e86e9ac` |
| M1, fixed and re-reviewed | ADDRESSED | the recovery line is a WARNING, and one new test checks both ends of an outage at the level the container runs with (it failed before the change); sample 97; name check 163 | - |

### What was fixed, by round

The details are in "Plan-vs-implementation notes" above.

- **Round 1.** The key cache: a request whose `kid` is not held does not wait for a running fetch (`503` at once; a cold cache
  waits for the first fetch); no proxy and no redirect in the fetch, `http` and `https` only; `aud` must be a string; `exp`,
  `iat` and `nbf` must be JSON numbers; held keys expire 24 hours after the last good fetch (owner). The sample: no password in
  `repr(Settings)`, no trailing-slash redirect, the body read only up to its cap, a connect timeout, JSON `404`, `405` and `503`
  with `no-store`. The overlay: Caddy at a pinned tag as a non-root user, the `notes` login refused on `auth`, a healthcheck that
  Caddy waits for. The e2e script: no `eval` of a value of the service, the cookie's attributes checked, the clock counts the
  waits. The test that failed under a proxy variable, the misnamed test, the stale map.
- **Round 2.** The proxy test fails when the rule is removed (the opener is built for each fetch); a cold cache waits only for the
  first attempt (ruling V7); the flag "a fetch is running" cannot be left set by a clock that raises; five test gaps closed
  (`notify_all`, a worker that ends on something that is not an `Exception`, a key over 24 hours during a fetch, float time
  claims, log tests that carry the text of an error). The sample answers a `500` itself and logs its class alone, a pool that runs
  out is a `503`, `Allow` lists every method of the path. `postgres` and `template1` closed to the `notes` login, Caddy with a
  read-only root filesystem, no capabilities but one and no new privileges. Images by digest; the sample's 24 packages at exact
  versions and, for the image, with hashes; the package's build backend pinned.
- **Round 3.** An attempt whose end could not be timed still counts for the 10-second rule (R3-1). The build tool and what it
  needs are pinned, hashed and removed from the image (R3-2). `/api/health` logs a database that does not answer. The guide lists
  `503 database_unavailable`. The polling of `wait_mail` no longer prints `curl`'s errors.
- **Round 4.** The test of the build tool's pins names `tomlkit` (L1). `/api/health` logs an outage once when it begins and once
  when it ends, not at every call (I1). `JwksCache` refuses a `min_interval` that is not positive (I2). The end of an outage is
  logged at WARNING, the level of the container's log (M1).

### Final state

- Package: 206 tests. Sample: 97 tests on SQLite, also under a dead `HTTP_PROXY`, `http_proxy` and `HTTPS_PROXY`. .NET: 935/935
  (last run in round 3; `src/`, `tests/` and `deploy/docker-compose.yml` are the same as on `main`).
- Name check (Task 9, step 2, in `.superpowers/sdd/0006-python-consumer-package/task-9-brief.md`): `checked 163 missing 0 unmapped 0`.
- `scripts/e2e-notes.sh` steps 1 to 6 and the five earlier scripts pass on a clean stack (the five last ran in round 3, and
  `scripts/e2e-notes.sh` in the scoped pass on `22201eb`; the change after that pass is one log level and one test).
- The sample on PostgreSQL 16 (a throwaway `postgres:16-alpine`, `NOTES_TEST_DATABASE_URL` set): 97 passed, final state.
- `bash -n scripts/e2e-notes.sh` is clean; the file is LF with mode 100755. No backslash-u escape in the files of the last passes.
  The guide's checks (Task 8, step 3) hold: 8 headings, every link and path exists, no product name.

### Owner decisions during verification

- Every finding is fixed at once, Low and Info included, unless the fix contradicts the spec.
- Held keys expire 24 hours after the last successful fetch (spec Decision 10).
- The package's build backend is pinned (`hatchling==1.32.4`).
- A small third and a small fourth round after the PASS verdicts.
- The FastAPI floor (0.142) and the other readings in spec 0006 -> "As built" (A1 to A9).

### Rulings by the orchestrator during implementation

- Tasks 7 and 9 were built and checked statically first; their e2e run waited for the code of spec 0005 (the company API and
  the operator CLI), and the branch was verified on top of it.
- Final review: one fix wave (the wait of the key cache, the package's floors and version, the e2e script's project name, the
  `.gitignore`, the guide's two sentences, the map). Not done then: the shape of the `404` and `405` (done in round 1, by the
  owner's rule) and the package's readme and licence metadata (the repository has no licence decision).
- V1 and V1b: a request whose `kid` is not held does not wait for a running fetch; on a cold cache it waits for the first fetch.
  V2: no proxy and no redirect in the key set fetch. V3: `aud` is a string, as Auth-Core writes it. V4: the cheap Lows in the
  same round. V5: the deferral of Lows to the hardening slice, withdrawn by the owner. V6: the FastAPI floor is recorded in the
  spec's "As built". V7: a cold cache waits only for the first attempt.

### Deferred / follow-ups (not fixed in this slice)

- An asynchronous exception (a gevent or eventlet timeout) delivered between marking a fetch as running and its `try` can leave
  the cache marked as fetching. It cannot be closed in pure Python without a redesign; spec 0006 -> "As built", "Known limits".
- The refresh cookie is `Secure`, so on plain-HTTP origins other than localhost browsers drop it. The test on a real phone and
  HTTPS on the proxy belong to the frontend slice.
- Readme and licence metadata of the package.
- The base file's `postgres` and `mailpit` images are pinned by tag, and the `notes` database keeps PostgreSQL's default
  `CONNECT` for `PUBLIC` (only the login `notes` and the owner `auth` exist). The `REVOKE`s on `auth`, `postgres` and
  `template1` stay in the volume if the overlay is dropped.
- With a hung Auth-Core, the one request that runs the first fetch waits for the fetch timeout (5 seconds); every other request,
  and `/api/health`, answers at once.
- The tag `python-v0.1.0` is made at merge; the install line of the spec, the guide and `requirements.txt` resolve only after it.
- Not tested: FastAPI versions below 0.142 and the Windows registry proxy setting.
- Residual risks: in spec 0006 -> "Deferred / follow-ups" and "As built".
