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
| 3 | `iss`, `aud`, `sub`, `exp` ≤ 10 min; no Week 3 claims | `LoginTests.Token_carries_contract_claims_and_nothing_from_week_3`, `LoginTests.Token_header_typ_is_at_jwt_and_payload_has_no_identity_extras` |
| 4 | Verifies against JWKS `kid`, no shared secret | `TokenVerificationTests.Token_verifies_against_jwks_kid_without_shared_secret`, `TokenVerificationTests.Token_from_a_host_with_different_keys_fails_against_this_jwks`; e2e step 2 (PyJWT) |
| 5 | Unknown email and wrong password → identical `401` | `LoginTests.Unknown_email_and_wrong_password_are_indistinguishable`; e2e step 3 |
| 6 | Malformed request → `400`, not `500` | `LoginRequestTests.Malformed_json_login_returns_400` (14 cases), `Form_encoded_oidc_request_returns_400`, `Plain_text_body_returns_400`, `Oversized_body_returns_400` |
| 7 | Token survives a full restart | `TokenVerificationTests.Token_issued_before_restart_verifies_against_jwks_after_restart`; e2e step 4 (real container restart) |
| 8 | JWKS has no private key members | `JwksTests.Jwks_never_contains_private_key_members`; e2e step 2 |

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
