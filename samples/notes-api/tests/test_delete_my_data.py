"""Spec 0010: DELETE /api/me, the sample's "delete my data" (criterion 33)."""

import uuid

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from conftest import COMPANY_A, COMPANY_B, headers
from notes_api.db import Note


def add(client, signer, text, org_id, org_kind=None):
    response = client.post("/api/notes", json={"text": text}, headers=headers(signer, org_id, org_kind=org_kind))
    assert response.status_code == 201


def count(engine, org_id) -> int:
    with Session(engine) as session:
        return session.scalar(select(func.count()).select_from(Note).where(Note.org_id == uuid.UUID(org_id)))


def test_a_private_account_deletes_its_notes_and_no_other(client, signer, engine):  # criterion 33
    add(client, signer, "mine", COMPANY_A, "personal")
    add(client, signer, "mine too", COMPANY_A, "personal")
    add(client, signer, "theirs", COMPANY_B, "personal")

    response = client.delete("/api/me", headers=headers(signer, COMPANY_A, permissions=(), org_kind="personal"))

    assert response.status_code == 204
    assert response.content == b""
    assert response.headers["cache-control"] == "no-store"
    assert count(engine, COMPANY_A) == 0
    assert count(engine, COMPANY_B) == 1


def test_it_is_204_when_there_are_no_notes(client, signer):  # criterion 33
    response = client.delete("/api/me", headers=headers(signer, COMPANY_A, org_kind="personal"))

    assert response.status_code == 204


def test_a_company_member_is_403_forbidden_and_nothing_is_deleted(client, signer, engine):  # criterion 33
    add(client, signer, "company note", COMPANY_A)

    for kind in (None, "organization"):
        response = client.delete("/api/me", headers=headers(signer, COMPANY_A, org_kind=kind))
        assert response.status_code == 403
        assert response.json() == {"error": "forbidden"}
        assert response.headers["cache-control"] == "no-store"

    assert count(engine, COMPANY_A) == 1


def test_without_a_token_it_is_401(client):  # criterion 33
    response = client.delete("/api/me")

    assert response.status_code == 401


def test_a_token_of_another_kind_is_refused_before_anything_is_read(client, signer, engine):  # the package refuses a value it does not know
    add(client, signer, "kept", COMPANY_A, "personal")

    response = client.delete("/api/me", headers=headers(signer, COMPANY_A, org_kind="team"))

    assert response.status_code == 401
    assert count(engine, COMPANY_A) == 1


def test_a_second_delete_is_204_again_and_touches_nothing_else(client, signer, engine):  # criterion 33: "also when there are none"
    add(client, signer, "mine", COMPANY_A, "personal")
    add(client, signer, "theirs", COMPANY_B, "personal")

    first = client.delete("/api/me", headers=headers(signer, COMPANY_A, org_kind="personal"))
    second = client.delete("/api/me", headers=headers(signer, COMPANY_A, org_kind="personal"))

    assert first.status_code == 204
    assert second.status_code == 204
    assert second.content == b""
    assert count(engine, COMPANY_A) == 0
    assert count(engine, COMPANY_B) == 1
