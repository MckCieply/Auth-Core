"""Spec 0012: DELETE /api/org, the sample's "delete the company's data" (criterion 29)."""

import uuid

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from conftest import COMPANY_A, COMPANY_B, headers
from notes_api.db import Note

DELETER = ("notes:read", "notes:write", "org:delete")


def add(client, signer, text, org_id, org_kind=None):
    response = client.post("/api/notes", json={"text": text}, headers=headers(signer, org_id, org_kind=org_kind))
    assert response.status_code == 201


def count(engine, org_id) -> int:
    with Session(engine) as session:
        return session.scalar(select(func.count()).select_from(Note).where(Note.org_id == uuid.UUID(org_id)))


def test_a_holder_of_org_delete_deletes_the_notes_of_the_company_and_no_other(client, signer, engine):  # criterion 29
    add(client, signer, "ours", COMPANY_A)
    add(client, signer, "ours too", COMPANY_A)
    add(client, signer, "theirs", COMPANY_B)

    response = client.delete("/api/org", headers=headers(signer, COMPANY_A, permissions=DELETER, org_kind="organization"))

    assert response.status_code == 204
    assert response.content == b""
    assert response.headers["cache-control"] == "no-store"
    assert count(engine, COMPANY_A) == 0
    assert count(engine, COMPANY_B) == 1


def test_a_token_without_org_kind_is_an_organization_and_may_delete(client, signer, engine):  # the package's default kind
    add(client, signer, "ours", COMPANY_A)

    response = client.delete("/api/org", headers=headers(signer, COMPANY_A, permissions=DELETER))

    assert response.status_code == 204
    assert count(engine, COMPANY_A) == 0


def test_a_member_without_org_delete_is_403_and_nothing_is_deleted(client, signer, engine):  # criterion 29
    add(client, signer, "ours", COMPANY_A)

    response = client.delete("/api/org", headers=headers(signer, COMPANY_A, org_kind="organization"))

    assert response.status_code == 403
    assert response.json() == {"error": "forbidden"}
    assert response.headers["cache-control"] == "no-store"
    assert count(engine, COMPANY_A) == 1


def test_a_private_token_is_403_even_with_org_delete(client, signer, engine):  # criterion 29
    add(client, signer, "mine", COMPANY_A, "personal")

    response = client.delete("/api/org", headers=headers(signer, COMPANY_A, permissions=DELETER, org_kind="personal"))

    assert response.status_code == 403
    assert count(engine, COMPANY_A) == 1


def test_without_a_token_it_is_401(client):  # criterion 29
    assert client.delete("/api/org").status_code == 401


def test_it_is_204_when_there_are_no_notes_and_again_after(client, signer):  # criterion 29
    first = client.delete("/api/org", headers=headers(signer, COMPANY_A, permissions=DELETER))
    second = client.delete("/api/org", headers=headers(signer, COMPANY_A, permissions=DELETER))

    assert (first.status_code, second.status_code) == (204, 204)
