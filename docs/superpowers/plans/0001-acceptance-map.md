# Spec 0001 — acceptance map

Maps each acceptance criterion of
[spec 0001](../specs/0001-login-and-token-issuance.md) to the tests that guard it,
for verifier layer 2 ([`docs/workflow.md`](../../workflow.md#verification)).
Integration tests live in `tests/Auth.IntegrationTests/`; e2e steps are in
`scripts/e2e-login.sh` and run against the compose stack over real HTTP.

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | Correct credentials → `200` + JWT | `LoginTests.Valid_credentials_return_200_with_exact_contract_body`; e2e step 1 |
| 2 | Unencrypted RS256 JWS with `kid` | `LoginTests.Token_is_unencrypted_RS256_jws_with_kid` |
| 3 | `iss`, `aud`, `sub`, `exp` ≤ 10 min; the Week 3 claims `org_id`, `roles` and `permissions` came with spec 0005 | `LoginTests.Token_carries_contract_claims_and_the_tenancy_claims` (exp bounded against now), `LoginTests.Token_payload_claim_set_is_exactly_the_contract_plus_openiddict_metadata`, `LoginTests.Token_header_typ_is_at_jwt_and_payload_has_no_identity_extras` |
| 4 | Verifies against JWKS `kid`, no shared secret | `TokenVerificationTests.Token_verifies_against_jwks_kid_without_shared_secret`, `TokenVerificationTests.Token_from_a_host_with_different_keys_fails_against_this_jwks`; e2e step 2 (PyJWT) |
| 5 | Unknown email and wrong password → identical `401` | `LoginTests.Unknown_email_and_wrong_password_are_indistinguishable`, `LoginTests.Basic_authorization_header_does_not_change_the_uniform_401`; e2e step 3 |
| 6 | Malformed request → `400`, not `500` | `LoginRequestTests.Malformed_json_login_returns_400` (18 cases, incl. NUL/control characters), `Form_encoded_oidc_request_returns_400`, `Plain_text_body_returns_400`, `Oversized_body_returns_400`, `Trailing_slash_login_with_non_json_body_returns_400`, `Trailing_slash_login_with_get_returns_400`; `UnhandledTokenRequestGuardTests` |
| 7 | Token survives a full restart | `TokenVerificationTests.Token_issued_before_restart_verifies_against_jwks_after_restart`; e2e step 4 (real container restart) |
| 8 | JWKS has no private key members | `JwksTests.Jwks_never_contains_private_key_members`, `JwksTests.Jwks_has_exactly_one_key_and_never_publishes_the_encryption_certificate`; e2e step 2 |

## Review Focus (plan 0001)

| # | Failure mode | Guarding test(s) |
| - | ------------ | ---------------- |
| 1 | Email casing | `LoginTests.Email_match_is_case_insensitive` |
| 2 | Wrong JSON types / shape | `LoginRequestTests.Malformed_json_login_returns_400` |
| 3 | Other formats to the same URL | `LoginRequestTests.Form_encoded_oidc_request_returns_400`, `Plain_text_body_returns_400` |
| 4 | Uniform failure beyond the body | `LoginTests.Unknown_email_and_wrong_password_are_indistinguishable`, `LoginTests.Wrong_password_response_has_no_store_cache_header` |
| 5 | Missing or broken key material at startup | `KeyMaterialTests.Host_refuses_to_start_without_key_configuration`, `Missing_key_file_fails_fast_naming_the_config_key`, `Certificate_without_key_is_rejected`, `Non_rsa_certificate_is_rejected` |

## Plan-vs-implementation notes

- **Task 6 spike:** handler path taken. OpenIddict's `ExtractPostRequest` is
  removed for the token endpoint and `JsonLoginRequestHandler` takes its slot
  (ordering alone does not work: the built-in handler overwrites a pre-set request
  and rejects non-form bodies). No fallback middleware.
- **Task 8:** the plan's single `Tampered_token_fails_verification` became three
  tests (signature, payload, `alg=none`), plus the different-keys control.

## Local verification log

Three Sonnet verifiers (realization vs spec, API/e2e, security) ran per
[`docs/workflow.md`](../../workflow.md#verification).

| Round | Realization vs spec | API / e2e | Security | Fix commit |
| ----- | ------------------- | --------- | -------- | ---------- |
| 1 | FAIL: login handlers applied to every token-endpoint request (spec 0002 trap) | FAIL: NUL in email → `500` + stack trace | PASS | `fix(login): address local verification round 1` |
| 2 | FAIL: `/auth/login/` → `500` (regression from round 1) | FAIL: same | — | `fix(login): handle trailing-slash login path and unclaimed token requests` |
| 3 | PASS (combined re-check, incl. mutation check of the new tests) | PASS (clean stack, ~100 probes, no `500`; e2e ALL PASS) | — | — |

### Deferred / follow-ups (not fixed in this slice, by design)

- **Timing oracle** on login: unknown email ≈ 5–10 ms vs wrong password ≈ 100 ms →
  spec 0003 (timing equalisation). No lockout or rate limiting → spec 0003.
- **Unconfirmed emails can log in**: spec 0001 is silent; decide in the email
  verification spec (Week 2).
- **`OpenIddictTokens` grows by one row per login** (access-token entries, no
  pruning) → spec 0002 (revocation) or a cleanup job.
- **Encryption certificate is mandatory** although access-token encryption is
  off: OpenIddict requires one, and refresh tokens (spec 0002) use it.
- **Dev seeder** is not safe for two replicas starting at once on one database
  (dev only) and logs the seed email once.
- **`scripts/dev-keys.sh` writes keys with mode `0644`** so the non-root
  container can read the bind mount: single-user dev machines only.
- **Image ships unused OpenIddict client/validation assemblies** (meta-package);
  narrower package references could shrink it.

### Escalations for the owner (plan vs spec)

- **E1.** The plan's architecture paragraph says refresh can be mapped onto the
  token endpoint with "a one-line `SetTokenEndpointUris` change". With the shipped
  design, spec 0002 must also add its own extraction handler ordered before
  `UnhandledTokenRequestGuard`. Recorded by the owner in spec 0001 → "As built"; plan 0001 → "As built" marks the remark superseded.
- **E2.** Spec 0001 Decision 1 tracks the repo skeleton separately; plan 0001
  bundled it as Task 1 (and the Docker image/compose as Task 9). Recorded by the owner in spec 0001 → "As built".
- **E3.** The token carries OpenIddict metadata claims `iat`, `jti`, `oi_tkn_id`
  beyond the spec's `iss/aud/sub/exp`; the claim-set test accepts them pending an
  owner decision on whether the spec list is exhaustive. Accepted by the owner (spec 0001 → "As built").
