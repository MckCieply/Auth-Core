"""Criterion 7: no token and no claim value in the logs, at any level; a rejected token is logged at debug, by reason."""

import logging
import time

from helpers import bearer, hs256_with_public_key, with_changed_signature

SECRETS = {
    "sub": "sub-4f7a9c1e-secret",
    "org_id": "0a1b2c3d-org-secret",
    "roles": ["role-secret-aaa"],
    "permissions": ["perm-secret:read", "perm-secret:write"],
}


def secret_token(signer, headers=None, **overrides) -> str:
    return signer.token(headers=headers, **{**SECRETS, **overrides})


def _everything_logged(caplog) -> str:
    return "\n".join(
        " ".join([record.name, record.getMessage(), str(record.args), str(record.exc_info), str(record.exc_text)])
        for record in caplog.records
    )


def test_no_token_and_no_claim_value_is_logged_at_any_level(client, jwks, clock, key1, caplog):  # criterion 7
    caplog.set_level(logging.DEBUG)
    now = int(time.time())
    tokens = [
        secret_token(key1),                                          # accepted
        secret_token(key1, permissions=["perm-secret:read"]),        # accepted, then refused (403) below
        with_changed_signature(secret_token(key1)),                  # signature
        secret_token(key1, aud="someone-else"),                      # audience
        secret_token(key1, iss="http://elsewhere.test/auth"),        # issuer
        secret_token(key1, iat=now - 1000, exp=now - 400),           # expired
        secret_token(key1, roles="role-secret-aaa"),                 # claim of the wrong type
        secret_token(key1, headers={"typ": "JWT"}),                  # type
        secret_token(key1, headers={"kid": "kid-secret-zzz"}),       # unknown key
        hs256_with_public_key(key1, **SECRETS),                    # algorithm
    ]
    for token in tokens:
        client.get("/me", headers=bearer(token))
        client.post("/write", headers=bearer(token))
    jwks.fails = True
    clock.advance(10)
    client.get("/me", headers=bearer(secret_token(key1, headers={"kid": "kid-secret-yyy"})))  # key set down

    logged = _everything_logged(caplog)

    for token in tokens:
        assert token not in logged
        assert token.split(".")[1] not in logged  # not the payload on its own either
    for value in ("sub-4f7a9c1e-secret", "0a1b2c3d-org-secret", "role-secret-aaa", "perm-secret", "kid-secret"):
        assert value not in logged
    assert "Bearer" not in logged
    assert "authorization" not in logged.lower()


def test_a_rejected_token_is_logged_at_debug_with_the_reason_only(client, key1, caplog):  # criterion 7
    caplog.set_level(logging.DEBUG, logger="auth_core_fastapi")

    client.get("/me", headers=bearer(with_changed_signature(key1.token())))
    client.get("/me")

    messages = [(record.levelno, record.getMessage()) for record in caplog.records if record.name.startswith("auth_core")]
    assert (logging.DEBUG, "token rejected: InvalidSignatureError") in messages
    assert (logging.DEBUG, "token rejected: no_authorization_header") in messages


def test_a_rejected_token_is_not_logged_above_debug(client, key1, caplog):  # criterion 7
    caplog.set_level(logging.INFO, logger="auth_core_fastapi")

    client.get("/me", headers=bearer(with_changed_signature(key1.token())))

    assert [record for record in caplog.records if record.name.startswith("auth_core")] == []


def test_a_failed_fetch_is_logged_as_a_warning_by_the_class_of_the_error_only(client, jwks, key1, caplog):
    caplog.set_level(logging.INFO, logger="auth_core_fastapi")
    jwks.fails = True

    client.get("/me", headers=bearer(key1.token()))

    warnings = [record.getMessage() for record in caplog.records if record.levelno == logging.WARNING]
    assert warnings == ["the key set could not be fetched (OSError)"]
