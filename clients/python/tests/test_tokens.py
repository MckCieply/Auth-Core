"""Criteria 2, 3 and 4: which tokens are accepted, which are a 401, and what `require_permission` answers."""

import time

import pytest
from fastapi import Depends, FastAPI
from fastapi.testclient import TestClient

from helpers import (
    MISSING, JwksServer, Signer, assert_unauthorized, bearer, hs256_with_public_key, jws, with_changed_payload,
    with_changed_signature,
)


# --- criterion 2: a token that meets every rule is accepted and says who the caller is ----------------------------------


def test_valid_token_gives_the_principal_of_its_claims(client, key1):  # criterion 2
    token = key1.token(
        sub="user-42", org_id="22222222-2222-2222-2222-222222222222",
        roles=["admin", "auditor"], permissions=["notes:read", "notes:write", "notes:read"],
    )

    response = client.get("/me", headers=bearer(token))

    assert response.status_code == 200
    assert response.json() == {
        "sub": "user-42", "org_id": "22222222-2222-2222-2222-222222222222",
        "roles": ["admin", "auditor"], "permissions": ["notes:read", "notes:write"],
        "types": ["tuple", "frozenset"],
    }


@pytest.mark.parametrize("scheme", ["Bearer", "bearer", "BEARER", "bEaReR"])
def test_the_scheme_is_case_insensitive(client, key1, scheme):  # criterion 2
    response = client.get("/me", headers={"Authorization": f"{scheme} {key1.token()}"})

    assert response.status_code == 200


def test_a_token_without_roles_or_permissions_is_valid_and_holds_none(client, key1):  # criterion 2
    response = client.get("/me", headers=bearer(key1.token(roles=[], permissions=[])))

    assert response.status_code == 200
    assert response.json()["roles"] == []
    assert response.json()["permissions"] == []


def test_an_audience_list_that_holds_the_configured_audience_is_valid(client, key1):  # criterion 2
    response = client.get("/me", headers=bearer(key1.token(aud=["other-api", "notes-api"])))

    assert response.status_code == 200


def test_a_token_expired_less_than_five_minutes_ago_is_still_valid(client, key1):  # criterion 2, Decision 9
    now = int(time.time())

    response = client.get("/me", headers=bearer(key1.token(iat=now - 900, exp=now - 200)))

    assert response.status_code == 200


# --- criterion 3: everything else is a 401 with an empty body and WWW-Authenticate: Bearer -----------------------------


def test_no_header_is_a_401(client):  # criterion 3
    assert_unauthorized(client.get("/me"))


@pytest.mark.parametrize("value", ["Basic dXNlcjpwYXNz", "Token abc.def.ghi", "Bearer", "Bearer ", "abc.def.ghi"])
def test_a_scheme_other_than_bearer_or_no_token_is_a_401(client, value):  # criterion 3
    assert_unauthorized(client.get("/me", headers={"Authorization": value}))


def test_two_authorization_headers_are_a_401_even_when_both_are_valid(client, key1):  # criterion 3
    token = key1.token()

    response = client.get("/me", headers=[("Authorization", f"Bearer {token}"), ("Authorization", f"Bearer {token}")])

    assert_unauthorized(response)


@pytest.mark.parametrize("value", ["Bearer  {t}", "Bearer {t} extra", "Bearer {t},", 'Bearer "{t}"'])
def test_a_header_that_is_not_bearer_and_one_token_is_a_401(client, key1, value):  # criterion 3
    assert_unauthorized(client.get("/me", headers={"Authorization": value.format(t=key1.token())}))


@pytest.mark.parametrize("token", ["abc", "a.b", "a.b.c", "....", "e30.e30.", "not-a-token", "e30.e30.e30"])
def test_a_token_that_is_not_a_jws_is_a_401(client, token):  # criterion 3
    assert_unauthorized(client.get("/me", headers={"Authorization": "Bearer " + token}))


def test_alg_none_is_a_401(client, key1):  # criterion 3
    token = jws({"alg": "none", "typ": "at+jwt", "kid": "k1"}, key1.claims())

    assert_unauthorized(client.get("/me", headers=bearer(token)))


def test_hs256_signed_with_the_public_key_is_a_401(client, key1):  # criterion 3: algorithm confusion
    assert_unauthorized(client.get("/me", headers=bearer(hs256_with_public_key(key1))))


@pytest.mark.parametrize("algorithm", ["RS384", "RS512", "PS256"])
def test_an_algorithm_other_than_rs256_is_a_401_even_with_a_good_signature(client, key1, algorithm):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(algorithm=algorithm))))


@pytest.mark.parametrize("typ", ["JWT", "at+JWT", "application/at+jwt", "", MISSING])
def test_a_type_other_than_at_jwt_is_a_401(client, key1, typ):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(headers={"typ": typ}))))


@pytest.mark.parametrize("kid", ["", MISSING])
def test_a_token_without_a_usable_kid_is_a_401(client, key1, kid):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(headers={"kid": kid}))))


def test_a_kid_that_is_not_text_is_a_401(client, key1):  # criterion 3
    token = jws({"alg": "RS256", "typ": "at+jwt", "kid": 7}, key1.claims(), b"signature")

    assert_unauthorized(client.get("/me", headers=bearer(token)))


def test_a_changed_signature_is_a_401(client, key1):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(with_changed_signature(key1.token()))))


def test_a_changed_payload_is_a_401(client, key1):  # criterion 3
    token = with_changed_payload(key1.token(), permissions=["notes:write", "everything"])

    assert_unauthorized(client.get("/me", headers=bearer(token)))


def test_a_token_signed_by_another_key_with_a_published_kid_is_a_401(client, stranger):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(stranger.token())))


@pytest.mark.parametrize("kid", ["k1", "evil"])
def test_the_key_is_chosen_by_kid_alone_whatever_the_header_offers(client, key1, kid):  # criterion 3
    """A token of an attacker's key that brings its own key (`jwk`, `x5c`) or says where to find it (`jku`, `x5u`)."""
    evil = Signer("evil")
    with JwksServer({"keys": [evil.jwk]}) as attackers_key_set:
        headers = {"jwk": evil.jwk, "jku": attackers_key_set.url, "x5u": attackers_key_set.url, "x5c": ["MIIB"], "kid": kid}

        response = client.get("/me", headers=bearer(evil.token(headers=headers)))

        assert_unauthorized(response)
        assert attackers_key_set.requests == 0  # nobody went to fetch the key the token pointed at


def test_a_wrong_issuer_is_a_401(client, key1):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(iss="http://elsewhere.test/auth"))))


def test_a_wrong_audience_is_a_401(client, key1):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(aud="another-api"))))


def test_a_token_expired_more_than_five_minutes_ago_is_a_401(client, key1):  # criterion 3, Decision 9
    now = int(time.time())

    assert_unauthorized(client.get("/me", headers=bearer(key1.token(iat=now - 1000, exp=now - 400))))


@pytest.mark.parametrize("claim", ["exp", "iat", "iss", "aud"])
def test_a_token_without_a_registered_claim_is_a_401(client, key1, claim):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(**{claim: MISSING}))))


def test_a_token_issued_in_the_future_is_a_401(client, key1):  # criterion 3
    now = int(time.time())

    assert_unauthorized(client.get("/me", headers=bearer(key1.token(iat=now + 1000, exp=now + 1600))))


@pytest.mark.parametrize(
    "claim, value",
    [
        ("sub", MISSING), ("sub", ""), ("sub", 7), ("sub", ["u"]),
        ("org_id", MISSING), ("org_id", ""), ("org_id", 7), ("org_id", None),
        ("roles", MISSING), ("roles", "admin"), ("roles", [1]), ("roles", ["admin", None]), ("roles", {"admin": True}),
        ("permissions", MISSING), ("permissions", "notes:read"), ("permissions", [True]), ("permissions", None),
    ],
)
def test_a_missing_claim_or_one_of_the_wrong_type_is_a_401(client, key1, claim, value):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(**{claim: value}))))


def test_a_valid_token_in_another_header_is_a_401(client, key1):  # criterion 3
    assert_unauthorized(client.get("/me", headers={"X-Authorization": f"Bearer {key1.token()}"}))


# --- criterion 4: the permission ------------------------------------------------------------------------------------


def test_a_valid_token_without_the_permission_is_a_403(client, key1):  # criterion 4
    response = client.post("/write", headers=bearer(key1.token(permissions=["notes:read"])))

    assert response.status_code == 403
    assert response.content == b'{"error":"forbidden"}'
    assert response.headers["content-type"] == "application/json"
    assert response.headers["cache-control"] == "no-store"
    assert "www-authenticate" not in response.headers


def test_a_token_with_the_permission_is_let_through(client, key1):  # criterion 4
    response = client.post("/write", headers=bearer(key1.token(sub="writer")))

    assert response.status_code == 200
    assert response.json() == {"sub": "writer"}


@pytest.mark.parametrize("held", [["notes:writer"], ["notes"], ["NOTES:WRITE"], ["notes:write "], ["*"]])
def test_the_permission_is_matched_whole(client, key1, held):  # criterion 4
    response = client.post("/write", headers=bearer(key1.token(permissions=held)))

    assert response.status_code == 403


def test_an_invalid_token_is_a_401_before_it_is_a_403(client, key1):  # criterion 4
    assert_unauthorized(client.post("/write"))
    assert_unauthorized(client.post("/write", headers=bearer(with_changed_signature(key1.token(permissions=[])))))


def test_without_install_the_status_and_the_headers_are_still_right(auth, key1):
    app = FastAPI()  # auth.install(app) is not called

    @app.get("/me")
    def me(user=Depends(auth.require_permission("notes:write"))):
        return {}

    client = TestClient(app)

    missing = client.get("/me")
    forbidden = client.get("/me", headers=bearer(key1.token(permissions=[])))

    assert missing.status_code == 401
    assert missing.headers["www-authenticate"] == "Bearer"
    assert forbidden.status_code == 403
    assert forbidden.headers["cache-control"] == "no-store"
