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
| 3 | `401`, empty body, `WWW-Authenticate: Bearer` for each of the listed cases | no header: `test_tokens.test_no_header_is_a_401`; a scheme other than `Bearer`: `test_tokens.test_a_scheme_other_than_bearer_or_no_token_is_a_401`, `test_tokens.test_a_header_that_is_not_bearer_and_one_token_is_a_401`, `test_tokens.test_a_valid_token_in_another_header_is_a_401`; two headers: `test_tokens.test_two_authorization_headers_are_a_401_even_when_both_are_valid`; not a JWS: `test_tokens.test_a_token_that_is_not_a_jws_is_a_401`; `none`, `HS256` with the public key, another algorithm: `test_tokens.test_alg_none_is_a_401`, `test_tokens.test_hs256_signed_with_the_public_key_is_a_401`, `test_tokens.test_an_algorithm_other_than_rs256_is_a_401_even_with_a_good_signature`, `test_jwks_cache.test_only_rsa_keys_are_taken_from_the_key_set`; `typ`: `test_tokens.test_a_type_other_than_at_jwt_is_a_401`; `kid`: `test_tokens.test_a_token_without_a_usable_kid_is_a_401`, `test_tokens.test_a_kid_that_is_not_text_is_a_401`; a changed signature: `test_tokens.test_the_key_is_chosen_by_kid_alone_whatever_the_header_offers`, `test_tokens.test_a_changed_signature_is_a_401`, `test_tokens.test_a_changed_payload_is_a_401`, `test_tokens.test_a_token_signed_by_another_key_with_a_published_kid_is_a_401`; `iss`, `aud`: `test_tokens.test_a_wrong_issuer_is_a_401`, `test_tokens.test_a_wrong_audience_is_a_401`, `test_tokens.test_an_audience_that_is_a_list_is_a_401_even_when_it_holds_the_configured_one` (`aud` must equal the configured string; Auth-Core writes it as one); expired: `test_tokens.test_a_token_expired_more_than_five_minutes_ago_is_a_401`; missing or mistyped claims: `test_tokens.test_a_missing_claim_or_one_of_the_wrong_type_is_a_401`, `test_tokens.test_a_token_without_a_registered_claim_is_a_401`, `test_tokens.test_a_token_issued_in_the_future_is_a_401`, `test_tokens.test_a_time_claim_that_is_not_a_json_number_is_a_401` (`exp`, `iat`, `nbf` as strings or booleans), `test_tokens.test_a_not_before_in_the_past_is_accepted_and_one_in_the_future_is_a_401`, `test_jwks_cache.test_a_key_without_a_kid_is_ignored`, `test_notes.test_no_token_is_a_401_on_every_endpoint`; e2e step 2 |
| 4 | `require_permission` answers `403 {"error":"forbidden"}` for a valid token without the permission, and lets a token with it through | `test_tokens.test_a_valid_token_without_the_permission_is_a_403`, `test_tokens.test_a_token_with_the_permission_is_let_through`, `test_tokens.test_the_permission_is_matched_whole`, `test_tokens.test_an_invalid_token_is_a_401_before_it_is_a_403`, `test_notes.test_a_viewer_may_read_and_may_not_write`, `test_notes.test_a_writer_without_the_read_permission_may_not_read`, `test_notes.test_a_refused_write_stores_nothing`; e2e steps 4, 5 |
| 5 | Keys: a new key after one refetch; at most one refetch per 10 seconds; held keys keep working while the URL fails; `503 {"error":"auth_unavailable"}` with no key and a failing URL | the cache: `test_jwks_cache.test_the_keys_are_fetched_on_first_use_and_kept`, `test_jwks_cache.test_a_new_key_is_accepted_after_one_refetch`, `test_jwks_cache.test_a_new_key_is_not_looked_for_within_ten_seconds_of_the_last_fetch`, `test_jwks_cache.test_many_unknown_kids_cause_one_fetch_in_ten_seconds`, `test_jwks_cache.test_the_keys_are_fetched_again_after_five_minutes_not_before`, `test_jwks_cache.test_a_key_that_left_the_key_set_stops_working_at_the_next_fetch`, `test_jwks_cache.test_held_keys_keep_working_while_the_fetch_fails`, `test_jwks_cache.test_a_failed_renewal_is_retried_no_sooner_than_ten_seconds_later`, `test_jwks_cache.test_no_key_and_a_failing_key_set_is_unavailable`, `test_jwks_cache.test_unavailable_stays_the_answer_for_ten_seconds_without_another_fetch`, `test_jwks_cache.test_the_answer_to_an_unknown_kid_names_the_state_of_the_latest_fetch`, `test_jwks_cache.test_unavailable_does_not_need_the_failed_fetch_to_have_run_for_the_request`, `test_jwks_cache.test_a_key_set_with_nothing_usable_counts_as_a_failed_fetch`, `test_jwks_cache.test_requests_that_arrive_during_the_first_fetch_wait_for_it_and_share_it`, `test_jwks_cache.test_a_request_that_waits_for_the_first_fetch_gives_up_when_the_fetch_may_not_take_longer`, `test_jwks_cache.test_no_request_of_a_product_that_has_just_started_is_turned_away_while_the_first_fetch_ends`, `test_jwks_cache.test_requests_with_an_unknown_kid_do_not_wait_for_a_fetch_that_is_running_and_share_it`, `test_jwks_cache.test_a_request_that_holds_its_key_does_not_wait_for_a_fetch_that_is_running`, `test_jwks_cache.test_a_request_that_lacks_its_key_is_unavailable_at_once_while_a_fetch_runs`, `test_jwks_cache.test_the_fetch_gives_up_at_its_timeout`, `test_jwks_cache.test_a_server_that_trickles_the_answer_is_given_up_on_at_the_deadline_of_the_whole_fetch`, `test_jwks_cache.test_the_deadline_reaches_the_cache_as_an_unavailable_key_set`, `test_jwks_cache.test_the_defaults_are_the_rules_of_the_spec`, over HTTP: `test_jwks_cache.test_the_keys_are_fetched_over_http`, `test_jwks_cache.test_an_error_status_is_a_failed_fetch`, `test_jwks_cache.test_an_answer_that_is_not_a_key_set_is_a_failed_fetch`, `test_jwks_cache.test_a_key_set_just_under_the_size_limit_is_fetched`, `test_jwks_cache.test_a_valid_key_set_over_the_size_limit_is_refused_for_its_size`, `test_jwks_cache.test_nobody_listening_is_a_failed_fetch`; how the key set is fetched: `test_jwks_cache.test_the_fetch_ignores_proxy_environment_variables`, `test_jwks_cache.test_a_redirect_is_a_failed_fetch_and_is_not_followed`, `test_jwks_cache.test_a_key_set_url_that_is_not_http_or_https_is_refused`, `test_jwks_cache.test_http_and_https_key_set_urls_are_accepted`; times counted from the end of a fetch: `test_jwks_cache.test_the_five_minutes_and_the_ten_seconds_count_from_the_end_of_a_fetch`, `test_jwks_cache.test_a_failed_fetch_counts_from_its_end_too`; held keys expire 24 hours after the last good fetch (Decision 10): `test_jwks_cache.test_the_longest_a_held_key_outlives_the_last_good_fetch_is_24_hours`, `test_jwks_cache.test_a_held_key_works_until_24_hours_after_the_last_good_fetch_while_the_fetch_fails`, `test_jwks_cache.test_the_24_hours_count_from_the_last_fetch_that_worked_not_from_the_last_try`, `test_jwks_cache.test_a_fetch_that_works_again_restores_the_keys_after_the_limit`, `test_keys.test_a_known_key_is_a_503_once_the_keys_are_a_day_old_and_the_key_set_still_fails`; through the dependencies: `test_keys` (all), `test_server.test_an_unknown_kid_is_a_401_and_a_key_set_that_does_not_answer_is_a_503`, `test_server.test_a_slow_fetch_does_not_block_the_event_loop`, `test_notes.test_the_service_answers_503_while_auth_core_is_down_and_it_holds_no_key`; e2e step 6 |
| 6 | Creating the `AuthCore` object makes no network call | `test_core.test_creating_the_object_makes_no_network_call`, `test_jwks_cache.test_creating_the_cache_fetches_nothing`, `test_keys.test_the_keys_are_fetched_on_first_use_and_kept` |
| 7 | No token or claim value appears in the package's logs at any level | `test_logging.test_no_token_and_no_claim_value_is_logged_at_any_level`, `test_logging.test_a_rejected_token_is_logged_at_debug_with_the_reason_only`, `test_logging.test_a_rejected_token_is_not_logged_above_debug`, `test_logging.test_a_failed_fetch_is_logged_as_a_warning_by_the_class_of_the_error_only` |
| 8 | The sample never returns or changes a note of another company, whatever id it is given; another company's note and a non-UUID id are both `404` | `test_notes.test_another_companys_note_is_a_404_and_its_list_is_empty`, `test_notes.test_an_id_that_is_not_a_uuid_is_a_404`, `test_notes.test_only_the_written_form_of_an_id_finds_a_note`, `test_notes.test_the_company_of_a_new_note_is_the_tokens_whatever_the_body_says`, `test_notes.test_two_companies_keep_their_own_notes`, `test_notes.test_every_statement_on_the_notes_table_names_the_company`, `test_notes.test_a_valid_token_whose_company_is_not_a_uuid_is_a_401`; e2e step 3 |
| 9 | The package's tests run with `pytest` and no Docker, with keys generated in the test | all of `clients/python/tests/` (`helpers.Signer` makes the keys; `helpers.FakeJwks` and `helpers.JwksServer` stand in for the JWKS URL, the latter a server on 127.0.0.1 that the test starts) |
| 10 | The integration guide covers the eight steps, and every file it points at exists in the sample | the commands of Task 8, step 3 (the eight headings; every link and every path in code spans resolves) |

## How the tests run

- `cd clients/python && python -m pytest -q` — the package: 180 tests.
- `cd samples/notes-api && python -m pytest -q` — the sample on SQLite in memory (the real migration builds the table):
  78 tests. With `NOTES_TEST_DATABASE_URL=postgresql+psycopg://…` (an empty database) the same tests run on PostgreSQL,
  which is where the NUL character and a lone surrogate in a note are refused by the service and not by the database.
- The e2e script needs Docker, the Auth-Core image and the code of spec 0005 (the company API and the operator CLI).

## Review Focus (plan 0006)

| # | Failure mode | Guarding test(s) |
| - | ------------ | ---------------- |
| 1 | A note whose text the database cannot store (a NUL character, a lone surrogate), is too long, whose body is not JSON, or whose JSON is nested so deeply that parsing it fails, is a `400 invalid_request`, never a `500` | `test_notes.test_a_body_that_is_not_an_object_with_a_good_text_is_a_400`, `test_notes.test_a_body_over_the_size_cap_is_a_400_even_when_its_text_is_good`, `test_notes.test_a_deeply_nested_body_under_the_size_cap_is_a_400`, `test_notes.test_a_body_under_the_size_cap_may_carry_other_members`, `test_notes.test_a_bad_body_is_a_401_or_a_403_before_it_is_a_400`, `test_notes.test_the_text_may_be_one_to_a_thousand_characters_of_any_script` |
| 2 | Auth-Core slow or hung when a key is needed: no other request waits for it, the fetch gives up at its timeout, N requests that ask during a fetch share it, a request that holds its key never waits, until the first fetch has worked a request waits for it (no longer than the fetch timeout), and once a fetch has worked a request that lacks its key while a fetch runs is unavailable (503) at once, so a flood of unknown `kid`s cannot hold the thread pool | `test_server.test_a_slow_fetch_does_not_block_the_event_loop`, `test_jwks_cache.test_the_fetch_gives_up_at_its_timeout`, `test_jwks_cache.test_requests_that_arrive_during_the_first_fetch_wait_for_it_and_share_it`, `test_jwks_cache.test_a_request_that_waits_for_the_first_fetch_gives_up_when_the_fetch_may_not_take_longer`, `test_jwks_cache.test_no_request_of_a_product_that_has_just_started_is_turned_away_while_the_first_fetch_ends`, `test_jwks_cache.test_requests_with_an_unknown_kid_do_not_wait_for_a_fetch_that_is_running_and_share_it`, `test_jwks_cache.test_a_request_that_holds_its_key_does_not_wait_for_a_fetch_that_is_running`, `test_jwks_cache.test_a_request_that_lacks_its_key_is_unavailable_at_once_while_a_fetch_runs` |
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
| Every error the sample answers is `{"error": ...}` and never stored: an unknown path `404`, a wrong method `405` (the `Allow` header is kept), a database that does not answer `503 database_unavailable` on every notes endpoint, anything else `500 internal_error`; a trailing slash is a `404`, not a redirect | `test_shapes_and_limits.test_an_unknown_path_is_a_json_404_that_is_never_stored`, `test_shapes_and_limits.test_a_method_that_is_not_allowed_is_a_json_405_with_the_allow_header`, `test_shapes_and_limits.test_the_trailing_slash_is_not_redirected`, `test_shapes_and_limits.test_a_database_that_does_not_answer_is_a_503`, `test_shapes_and_limits.test_the_database_error_is_logged_by_its_class_and_not_by_its_text`, `test_shapes_and_limits.test_an_unexpected_error_is_a_json_500_that_is_never_stored` |
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
- Verification round 1, fixes (the changes of this round; the controller's rulings V1 to V4 and the owner's addendum, uncommitted when this
  map was written):
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
- Tasks 1-4, 6, 7, 9: as planned.

## Local verification log

Verification round 1 (on commit 4a3ed88, before the fixes of this round):

- e2e verifier: PASS. `scripts/e2e-notes.sh` and the five earlier e2e scripts all pass; .NET 935/935; the Python suites 132 (package) and
  58 (sample). Logs in `.superpowers/sdd/0006-python-consumer-package/` (`verify-e2e-s6-r1.md`).
- spec verifier: FAIL (a proxy environment variable broke 6 package tests; the FastAPI floor against the spec's 0.115; `aud` as a list;
  a stale map; a misnamed test). Report: `verify-spec-s6-r1.md`.
- security verifier: FAIL (one Medium: an unknown-`kid` flood held the thread pool through the bounded lock wait; Lows: `aud` list,
  redirects and proxy variables in the key set fetch, `repr` of the settings, a 307 that reflects `Host`, the body drained past the cap,
  no connect timeout, a mutable tag, Caddy as root, `eval` in the e2e script, numeric strings in `exp`/`iat`/`nbf`).
  Report: `verify-security-s6-r1.md`.
- The findings are fixed in the uncommitted changes of this round (see "Plan-vs-implementation notes", "Verification round 1"). The FastAPI
  floor is for the owner (spec "As built"). The e2e run on the changed overlay and the changed script is for the next round.

After the fixes of round 1:

- Package: `cd clients/python && python -m pytest -q` - 180 passed; also 180 passed with `HTTP_PROXY` and `http_proxy` set to
  `http://127.0.0.1:9` (a dead proxy).
- Sample: `cd samples/notes-api && python -m pytest -q` - 78 passed, on SQLite in memory.
- Sample on PostgreSQL 16: 57 passed, run in Task 5. That run predates the nested-body test and every test added since; it has not been
  re-run.
- Name check (the command of Task 9, step 2, in `.superpowers/sdd/0006-python-consumer-package/task-9-brief.md`): `checked 130 missing 0
  unmapped 0`.
- `bash -n scripts/e2e-notes.sh` is clean, the file is LF with mode 100755; the grep for backslash-u escapes and non-ASCII characters in
  `clients`, `samples` and `scripts` finds nothing in the files of this slice (two dashes in the comments of `scripts/e2e-tenancy.sh`, a
  file of slice 5 that is the same as on main, are the only hits).
- The guide's checks (Task 8, step 3): 8 numbered headings, every link and every path in a code span exists, no product name.
- `docker compose -p auth-core-review-0006 --env-file <copy of .env.example> -f deploy/docker-compose.yml -f samples/notes-api/compose.yml config -q`
  is clean (with the changed overlay). Caddy 2.11.7 as uid 10002 with tmpfs `/data` and `/config` validates the Caddyfile and answers.
  The stack was not started: the e2e run of the changed overlay is for the next round.
- The security probes of `.superpowers/sdd/0006-python-consumer-package/sec-probes/` were run again (`*.fix-r1.out`): the flood no longer
  delays an unrelated endpoint, a redirect and a proxy variable are not followed, `aud` as a list and numeric-string time claims are 401,
  the settings do not print the password, `/api/notes/` is a JSON 404, a 200 MiB chunked upload is cut after 4 MiB, an aborted body logs
  no traceback. After V1b: the cold-start probes (`p3e_coldstart`, `p3d_threadpool` at delay 0) turn nobody away (120 of 120 valid
  requests are 200 with one fetch; 120 of 120 unknown-kid requests are 401, 0 are 503), and the warm flood (`p3d_warm`) leaves an
  unrelated endpoint at 0.00 to 0.01 s.
- One test of the sample was flaky on Windows and is fixed: `test_notes.test_two_companies_keep_their_own_notes` created two notes in one
  15 ms clock tick, so they were listed in the order of their random ids (seen once in about 30 runs with a dead `HTTP_PROXY`; it is not
  related to the proxy). The test pauses 50 ms between the two writes.
