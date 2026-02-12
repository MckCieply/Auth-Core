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
| 1 | The Goal sequence and all six steps of `scripts/e2e-notes.sh` pass on a clean stack; the four earlier e2e scripts and the one of spec 0005 still pass on the stack without the overlay | `scripts/e2e-notes.sh` (steps 1–6); `scripts/e2e-login.sh`, `scripts/e2e-refresh.sh`, `scripts/e2e-lockout.sh`, `scripts/e2e-email.sh`, `scripts/e2e-tenancy.sh` (arrives with slice 5) unedited, run on `deploy/docker-compose.yml` alone, which this slice does not change |
| 2 | A token that meets every rule is accepted, and its `Principal` carries `sub`, `org_id`, `roles` and `permissions` | `test_tokens.test_valid_token_gives_the_principal_of_its_claims`, `test_tokens.test_the_scheme_is_case_insensitive`, `test_tokens.test_a_token_without_roles_or_permissions_is_valid_and_holds_none`, `test_tokens.test_an_audience_list_that_holds_the_configured_audience_is_valid`, `test_tokens.test_a_token_expired_less_than_five_minutes_ago_is_still_valid`, `test_server.test_a_token_is_checked_against_the_keys_served_over_http`; e2e step 1 |
| 3 | `401`, empty body, `WWW-Authenticate: Bearer` for each of the listed cases | no header: `test_tokens.test_no_header_is_a_401`; a scheme other than `Bearer`: `test_tokens.test_a_scheme_other_than_bearer_or_no_token_is_a_401`, `test_tokens.test_a_header_that_is_not_bearer_and_one_token_is_a_401`, `test_tokens.test_a_valid_token_in_another_header_is_a_401`; two headers: `test_tokens.test_two_authorization_headers_are_a_401_even_when_both_are_valid`; not a JWS: `test_tokens.test_a_token_that_is_not_a_jws_is_a_401`; `none`, `HS256` with the public key, another algorithm: `test_tokens.test_alg_none_is_a_401`, `test_tokens.test_hs256_signed_with_the_public_key_is_a_401`, `test_tokens.test_an_algorithm_other_than_rs256_is_a_401_even_with_a_good_signature`, `test_jwks_cache.test_only_rsa_keys_are_taken_from_the_key_set`; `typ`: `test_tokens.test_a_type_other_than_at_jwt_is_a_401`; `kid`: `test_tokens.test_a_token_without_a_usable_kid_is_a_401`, `test_tokens.test_a_kid_that_is_not_text_is_a_401`; a changed signature: `test_tokens.test_the_key_is_chosen_by_kid_alone_whatever_the_header_offers`, `test_tokens.test_a_changed_signature_is_a_401`, `test_tokens.test_a_changed_payload_is_a_401`, `test_tokens.test_a_token_signed_by_another_key_with_a_published_kid_is_a_401`; `iss`, `aud`: `test_tokens.test_a_wrong_issuer_is_a_401`, `test_tokens.test_a_wrong_audience_is_a_401`; expired: `test_tokens.test_a_token_expired_more_than_five_minutes_ago_is_a_401`; missing or mistyped claims: `test_tokens.test_a_missing_claim_or_one_of_the_wrong_type_is_a_401`, `test_tokens.test_a_token_without_a_registered_claim_is_a_401`, `test_tokens.test_a_token_issued_in_the_future_is_a_401`, `test_notes.test_no_token_is_a_401_on_every_endpoint`; e2e step 2 |
| 4 | `require_permission` answers `403 {"error":"forbidden"}` for a valid token without the permission, and lets a token with it through | `test_tokens.test_a_valid_token_without_the_permission_is_a_403`, `test_tokens.test_a_token_with_the_permission_is_let_through`, `test_tokens.test_the_permission_is_matched_whole`, `test_tokens.test_an_invalid_token_is_a_401_before_it_is_a_403`, `test_notes.test_a_viewer_may_read_and_may_not_write`, `test_notes.test_a_writer_without_the_read_permission_may_not_read`, `test_notes.test_a_refused_write_stores_nothing`; e2e steps 4, 5 |
| 5 | Keys: a new key after one refetch; at most one refetch per 10 seconds; held keys keep working while the URL fails; `503 {"error":"auth_unavailable"}` with no key and a failing URL | the cache: `test_jwks_cache.test_the_keys_are_fetched_on_first_use_and_kept`, `test_jwks_cache.test_a_new_key_is_accepted_after_one_refetch`, `test_jwks_cache.test_a_new_key_is_not_looked_for_within_ten_seconds_of_the_last_fetch`, `test_jwks_cache.test_many_unknown_kids_cause_one_fetch_in_ten_seconds`, `test_jwks_cache.test_the_keys_are_fetched_again_after_five_minutes_not_before`, `test_jwks_cache.test_a_key_that_left_the_key_set_stops_working_at_the_next_fetch`, `test_jwks_cache.test_held_keys_keep_working_while_the_fetch_fails`, `test_jwks_cache.test_a_failed_renewal_is_retried_no_sooner_than_ten_seconds_later`, `test_jwks_cache.test_no_key_and_a_failing_key_set_is_unavailable`, `test_jwks_cache.test_unavailable_stays_the_answer_for_ten_seconds_without_another_fetch`, `test_jwks_cache.test_the_answer_to_an_unknown_kid_names_the_state_of_the_latest_fetch`, `test_jwks_cache.test_unavailable_does_not_need_the_failed_fetch_to_have_run_for_the_request`, `test_jwks_cache.test_a_key_set_with_nothing_usable_counts_as_a_failed_fetch`, `test_jwks_cache.test_requests_that_wait_for_the_keys_share_one_fetch`, `test_jwks_cache.test_a_request_that_holds_its_key_does_not_wait_for_a_fetch_that_is_running`, `test_jwks_cache.test_a_request_that_lacks_its_key_gives_up_waiting_for_a_fetch_that_runs_too_long`, `test_jwks_cache.test_the_fetch_gives_up_at_its_timeout`, `test_jwks_cache.test_a_server_that_trickles_the_answer_is_given_up_on_at_the_deadline_of_the_whole_fetch`, `test_jwks_cache.test_the_deadline_reaches_the_cache_as_an_unavailable_key_set`, `test_jwks_cache.test_the_defaults_are_the_rules_of_the_spec`, over HTTP: `test_jwks_cache.test_the_keys_are_fetched_over_http`, `test_jwks_cache.test_an_error_status_is_a_failed_fetch`, `test_jwks_cache.test_an_answer_that_is_not_a_key_set_is_a_failed_fetch`, `test_jwks_cache.test_a_key_set_just_under_the_size_limit_is_fetched`, `test_jwks_cache.test_a_valid_key_set_over_the_size_limit_is_refused_for_its_size`, `test_jwks_cache.test_nobody_listening_is_a_failed_fetch`; through the dependencies: `test_keys` (all), `test_server.test_an_unknown_kid_is_a_401_and_a_key_set_that_does_not_answer_is_a_503`, `test_server.test_a_slow_fetch_does_not_block_the_event_loop`, `test_notes.test_the_service_answers_503_while_auth_core_is_down_and_it_holds_no_key`; e2e step 6 |
| 6 | Creating the `AuthCore` object makes no network call | `test_core.test_creating_the_object_makes_no_network_call`, `test_jwks_cache.test_creating_the_cache_fetches_nothing`, `test_keys.test_the_keys_are_fetched_on_first_use_and_kept` |
| 7 | No token or claim value appears in the package's logs at any level | `test_logging.test_no_token_and_no_claim_value_is_logged_at_any_level`, `test_logging.test_a_rejected_token_is_logged_at_debug_with_the_reason_only`, `test_logging.test_a_rejected_token_is_not_logged_above_debug`, `test_logging.test_a_failed_fetch_is_logged_as_a_warning_by_the_class_of_the_error_only` |
| 8 | The sample never returns or changes a note of another company, whatever id it is given; another company's note and a non-UUID id are both `404` | `test_notes.test_another_companys_note_is_a_404_and_its_list_is_empty`, `test_notes.test_an_id_that_is_not_a_uuid_is_a_404`, `test_notes.test_only_the_written_form_of_an_id_finds_a_note`, `test_notes.test_the_company_of_a_new_note_is_the_tokens_whatever_the_body_says`, `test_notes.test_two_companies_keep_their_own_notes`, `test_notes.test_every_statement_on_the_notes_table_names_the_company`, `test_notes.test_a_valid_token_whose_company_is_not_a_uuid_is_a_401`; e2e step 3 |
| 9 | The package's tests run with `pytest` and no Docker, with keys generated in the test | all of `clients/python/tests/` (`helpers.Signer` makes the keys; `helpers.FakeJwks` and `helpers.JwksServer` stand in for the JWKS URL, the latter a server on 127.0.0.1 that the test starts) |
| 10 | The integration guide covers the eight steps, and every file it points at exists in the sample | the commands of Task 8, step 3 (the eight headings; every link and every path in code spans resolves) |

## How the tests run

- `cd clients/python && python -m pytest -q` — the package: 132 tests.
- `cd samples/notes-api && python -m pytest -q` — the sample on SQLite in memory (the real migration builds the table):
  58 tests. With `NOTES_TEST_DATABASE_URL=postgresql+psycopg://…` (an empty database) the same tests run on PostgreSQL,
  which is where the NUL character and a lone surrogate in a note are refused by the service and not by the database.
- The e2e script needs Docker, the Auth-Core image and the code of spec 0005 (the company API and the operator CLI).

## Review Focus (plan 0006)

| # | Failure mode | Guarding test(s) |
| - | ------------ | ---------------- |
| 1 | A note whose text the database cannot store (a NUL character, a lone surrogate), is too long, whose body is not JSON, or whose JSON is nested so deeply that parsing it fails, is a `400 invalid_request`, never a `500` | `test_notes.test_a_body_that_is_not_an_object_with_a_good_text_is_a_400`, `test_notes.test_a_body_over_the_size_cap_is_a_400_even_when_its_text_is_good`, `test_notes.test_a_deeply_nested_body_under_the_size_cap_is_a_400`, `test_notes.test_a_body_under_the_size_cap_may_carry_other_members`, `test_notes.test_a_bad_body_is_a_401_or_a_403_before_it_is_a_400`, `test_notes.test_the_text_may_be_one_to_a_thousand_characters_of_any_script` |
| 2 | Auth-Core slow or hung when a key is needed: no other request waits for it, the fetch gives up at its timeout, N requests that wait share one fetch, a request that holds its key never waits, a request that lacks its key waits for a running fetch no longer than the fetch timeout | `test_server.test_a_slow_fetch_does_not_block_the_event_loop`, `test_jwks_cache.test_the_fetch_gives_up_at_its_timeout`, `test_jwks_cache.test_requests_that_wait_for_the_keys_share_one_fetch`, `test_jwks_cache.test_a_request_that_holds_its_key_does_not_wait_for_a_fetch_that_is_running`, `test_jwks_cache.test_a_request_that_lacks_its_key_gives_up_waiting_for_a_fetch_that_runs_too_long` |
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
- The fix wave after the whole-branch review (uncommitted when this map was written):
  - `_jwks.py`: a request that lacks its key no longer waits for a running fetch without bound; it waits at most `timeout` (5 s by
    default) and then gets `KeysUnavailable` (a 503). A request that holds its key still never waits; the 10-second limit still counts
    failures; waiters that get the lock after a fetch finished use its keys without fetching again. New test
    `test_jwks_cache.test_a_request_that_lacks_its_key_gives_up_waiting_for_a_fetch_that_runs_too_long`: the package has 132 tests.
  - `pyproject.toml` of the package: the floors are the tested versions (`fastapi>=0.142`, `PyJWT[crypto]>=2.15`) and the version is
    single-sourced from `__version__` in `__init__.py` through hatch (`dynamic = ["version"]`). The guide says "FastAPI 0.142 or newer".
  - `scripts/e2e-notes.sh`: runs as the compose project `auth-core-notes` (`COMPOSE_PROJECT_NAME`, overridable) so that it does not share
    containers and volumes with the `auth-core` stack of other checkouts; `cli()` prints the CLI's stderr when the CLI fails;
    `AUTH_STOPPED=1` is set before `stop auth`, so the trap restarts Auth-Core even when the stop fails halfway. README and the script
    header name the project for the `down -v`.
  - `.gitignore`: `*.egg-info/` and the build and dist directories of the package and the samples.
  - The guide: `auth.install(app)` covers only the app it is called on (call it on each mounted sub-app); the sample's list endpoint
    has no paging and a real product should page.
  - This map: criterion 1 marks `scripts/e2e-tenancy.sh` as arriving with slice 5; the new test is mapped (criterion 5, Review Focus 2).
- Tasks 1-4, 6, 7, 9: as planned.

## Local verification log

- Package: `cd clients/python && python -m pytest -q` - 132 passed (131 before the fix wave).
- Sample: `cd samples/notes-api && python -m pytest -q` - 58 passed, on SQLite in memory.
- Sample on PostgreSQL 16: 57 passed, run in Task 5 before the nested-body test was added; not re-run since.
- Name check (the command of Task 9, step 2, in `.superpowers/sdd/0006-python-consumer-package/task-9-brief.md`): `checked 104 missing 0
  unmapped 0`.
- `bash -n scripts/e2e-notes.sh` is clean, the file is LF with mode 100755; the grep for backslash-u escapes and non-ASCII characters in
  `clients`, `samples` and `scripts` finds nothing.
- The guide's checks (Task 8, step 3): 8 numbered headings, every link and every path in a code span exists, no product name.
- `docker compose -p auth-core-review-0006 --env-file <copy of .env.example> -f deploy/docker-compose.yml -f samples/notes-api/compose.yml config -q`
  is clean.
- The overlay checks of Task 6: build, routing through Caddy, `notes-db-init` run twice, no published port on the sample, issuer
  `http://localhost:8088/auth`, refresh through Caddy.
- `pip install --no-deps --force-reinstall ./clients/python` into a throwaway venv builds `auth-core-fastapi` 0.1.0 with the version
  read from `__init__.py`.
- PENDING until slice 5 merges (the company API and the operator CLI are not on this branch): the run of `scripts/e2e-notes.sh`, and the
  regression pass (829 .NET tests and the five earlier e2e scripts).
