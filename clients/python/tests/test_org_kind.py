"""Spec 0010, criterion 15: `Principal.org_kind`, read from the claim `org_kind`; a token of an earlier Auth-Core has none."""

import dataclasses

import pytest
from fastapi import Depends, FastAPI
from fastapi.testclient import TestClient

from auth_core_fastapi import AuthCore, Principal
from helpers import MISSING, assert_unauthorized, bearer


def _client(auth: AuthCore) -> TestClient:
    app = FastAPI()
    auth.install(app)

    @app.get("/kind")
    def kind(user: Principal = Depends(auth.current_user)):
        return {"org_kind": user.org_kind}

    return TestClient(app)


@pytest.mark.parametrize("value", ["organization", "personal"])
def test_the_claim_gives_the_kind(auth, key1, value):  # criterion 15
    response = _client(auth).get("/kind", headers=bearer(key1.token(org_kind=value)))

    assert response.status_code == 200
    assert response.json() == {"org_kind": value}


def test_a_token_without_the_claim_is_an_organization(auth, key1):  # criterion 15: the tokens of earlier instances
    response = _client(auth).get("/kind", headers=bearer(key1.token(org_kind=MISSING)))

    assert response.status_code == 200
    assert response.json() == {"org_kind": "organization"}


@pytest.mark.parametrize("value", [None, "", "Personal", "company", 1, True, ["personal"], {"kind": "personal"}])
def test_any_other_value_is_a_401(auth, key1, value):  # criterion 15
    assert_unauthorized(_client(auth).get("/kind", headers=bearer(key1.token(org_kind=value))))


def test_org_kind_is_the_last_field_and_defaults_to_organization():  # code that builds a Principal by position keeps working
    principal = Principal("user-1", "org-1", ("user",), frozenset())

    assert principal.org_kind == "organization"
    assert [field.name for field in dataclasses.fields(Principal)][-1] == "org_kind"
