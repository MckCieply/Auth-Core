"""The three endpoints and the health check: what a company sees, what it cannot reach, what is refused."""

import json
import uuid
from datetime import datetime, timedelta, timezone

import pytest
from auth_core_fastapi import AuthCore, JwksCache
from fastapi.testclient import TestClient
from sqlalchemy import create_engine, event, func, select
from sqlalchemy.orm import Session

from conftest import COMPANY_A, COMPANY_B, headers
from notes_api.app import create_app
from notes_api.db import Note

BACKSLASH = chr(92)  # a JSON escape is built with it, so that no editing tool turns the escape into its character
READER = ("notes:read",)
WRITER = ("notes:write",)


def add(client, signer, text="hello", **kwargs):
    return client.post("/api/notes", json={"text": text}, headers=headers(signer, **kwargs))


# --- the endpoints, for one company --------------------------------------------------------------------------------------


def test_a_note_is_added_and_listed(client, signer):
    created = add(client, signer, "hello", sub="alice")

    assert created.status_code == 201
    note = created.json()
    assert set(note) == {"id", "text", "author_sub", "created_at"}
    assert uuid.UUID(note["id"])
    assert note["text"] == "hello"
    assert note["author_sub"] == "alice"
    assert datetime.fromisoformat(note["created_at"]).tzinfo is not None
    assert note["created_at"].endswith("Z")

    listed = client.get("/api/notes", headers=headers(signer))

    assert listed.status_code == 200
    assert listed.json() == [note]


def test_notes_are_listed_newest_first(client, signer, engine):
    start = datetime.now(timezone.utc) - timedelta(days=1)
    with Session(engine) as session:
        for number in range(3):
            session.add(Note(org_id=uuid.UUID(COMPANY_A), author_sub="u", text=f"note {number}", created_at=start + timedelta(hours=number)))
        session.commit()

    listed = client.get("/api/notes", headers=headers(signer))

    assert [note["text"] for note in listed.json()] == ["note 2", "note 1", "note 0"]


def test_a_note_is_read_by_its_id(client, signer):
    note = add(client, signer, "find me").json()

    found = client.get(f"/api/notes/{note['id']}", headers=headers(signer))

    assert found.status_code == 200
    assert found.json() == note
    assert client.get(f"/api/notes/{note['id'].upper()}", headers=headers(signer)).status_code == 200


def test_the_text_may_be_one_to_a_thousand_characters_of_any_script(client, signer):
    assert add(client, signer, "x").status_code == 201
    assert add(client, signer, "x" * 1000).status_code == 201
    unusual = "".join(map(chr, [0x17C, 0xF3, 0x142, 0x107, 0x1F600, 0x645]))  # Polish letters, an emoji, an Arabic letter
    assert add(client, signer, "text " + unusual).status_code == 201
    assert add(client, signer, "   ").status_code == 201


def test_every_answer_is_marked_never_to_be_stored(client, signer):
    for response in (
        add(client, signer),
        client.get("/api/notes", headers=headers(signer)),
        client.get(f"/api/notes/{uuid.uuid4()}", headers=headers(signer)),
        client.get("/api/notes"),
        client.post("/api/notes", json={}, headers=headers(signer)),
        client.post("/api/notes", json={"text": "x"}, headers=headers(signer, permissions=READER)),
        client.get("/api/health"),
    ):
        assert response.headers["cache-control"] == "no-store"


# --- criterion 8: another company's notes are out of reach -----------------------------------------------------------------


def test_another_companys_note_is_a_404_and_its_list_is_empty(client, signer):  # criterion 8
    note = add(client, signer, "secret of A").json()

    other = client.get(f"/api/notes/{note['id']}", headers=headers(signer, COMPANY_B))
    nobody = client.get(f"/api/notes/{uuid.uuid4()}", headers=headers(signer, COMPANY_B))

    assert other.status_code == 404
    assert other.json() == {"error": "not_found"}
    assert (other.status_code, other.content) == (nobody.status_code, nobody.content)  # the same as a note that is not there
    assert client.get("/api/notes", headers=headers(signer, COMPANY_B)).json() == []
    assert "secret of A" not in other.text


@pytest.mark.parametrize(
    "note_id",
    ["abc", "1", "' OR '1'='1", "%00", "{0}", "00000000000000000000000000000000", "urn:uuid:aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
     "{aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa}", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa-", "null"],
)
def test_an_id_that_is_not_a_uuid_is_a_404(client, signer, note_id):  # criterion 8
    response = client.get(f"/api/notes/{note_id}", headers=headers(signer))

    assert response.status_code == 404
    assert response.json() == {"error": "not_found"}


def test_only_the_written_form_of_an_id_finds_a_note(client, signer):  # criterion 8
    note = add(client, signer).json()

    for spelling in (note["id"].replace("-", ""), "{" + note["id"] + "}", "urn:uuid:" + note["id"], " " + note["id"]):
        assert client.get(f"/api/notes/{spelling}", headers=headers(signer)).status_code == 404


def test_the_company_of_a_new_note_is_the_tokens_whatever_the_body_says(client, signer, engine):  # criterion 8
    response = client.post(
        "/api/notes",
        json={"text": "mine", "org_id": COMPANY_B, "id": str(uuid.uuid4()), "author_sub": "someone-else"},
        headers=headers(signer, COMPANY_A, sub="alice"),
    )

    assert response.status_code == 201
    with Session(engine) as session:
        note = session.scalars(select(Note)).one()
    assert str(note.org_id) == COMPANY_A
    assert note.author_sub == "alice"
    assert str(note.id) == response.json()["id"] != ""
    assert client.get("/api/notes", headers=headers(signer, COMPANY_B)).json() == []


def test_two_companies_keep_their_own_notes(client, signer):  # criterion 8
    add(client, signer, "a1", org_id=COMPANY_A)
    add(client, signer, "b1", org_id=COMPANY_B)
    add(client, signer, "a2", org_id=COMPANY_A)

    assert [n["text"] for n in client.get("/api/notes", headers=headers(signer, COMPANY_A)).json()] == ["a2", "a1"]
    assert [n["text"] for n in client.get("/api/notes", headers=headers(signer, COMPANY_B)).json()] == ["b1"]


def test_every_statement_on_the_notes_table_names_the_company(settings, engine, auth, signer):  # criterion 8
    statements = []
    event.listen(engine, "before_cursor_execute", lambda conn, cursor, statement, *rest: statements.append(statement))
    client = TestClient(create_app(settings, engine=engine, auth=auth, migrate=False))

    note = add(client, signer, "x").json()
    client.get("/api/notes", headers=headers(signer))
    client.get(f"/api/notes/{note['id']}", headers=headers(signer))
    client.get(f"/api/notes/{note['id']}", headers=headers(signer, COMPANY_B))

    on_notes = [statement for statement in statements if "notes" in statement.lower()]
    assert len(on_notes) == 4
    assert all("org_id" in statement for statement in on_notes)
    assert all(statement.count("org_id") >= 2 for statement in on_notes if statement.lstrip().upper().startswith("SELECT"))


def test_a_valid_token_whose_company_is_not_a_uuid_is_a_401(client, signer):  # criterion 8
    response = client.get("/api/notes", headers=headers(signer, org_id="not-a-uuid"))

    assert response.status_code == 401
    assert response.content == b""
    assert response.headers["www-authenticate"] == "Bearer"


# --- what the package decides, seen from the product ---------------------------------------------------------------------


def test_no_token_is_a_401_on_every_endpoint(client):
    for response in (client.get("/api/notes"), client.get(f"/api/notes/{uuid.uuid4()}"), client.post("/api/notes", json={"text": "x"})):
        assert response.status_code == 401
        assert response.content == b""
        assert response.headers["www-authenticate"] == "Bearer"


def test_a_viewer_may_read_and_may_not_write(client, signer):
    note = add(client, signer).json()

    assert client.get("/api/notes", headers=headers(signer, permissions=READER)).status_code == 200
    assert client.get(f"/api/notes/{note['id']}", headers=headers(signer, permissions=READER)).status_code == 200
    refused = client.post("/api/notes", json={"text": "x"}, headers=headers(signer, permissions=READER))
    assert refused.status_code == 403
    assert refused.json() == {"error": "forbidden"}


def test_a_writer_without_the_read_permission_may_not_read(client, signer):
    assert client.get("/api/notes", headers=headers(signer, permissions=WRITER)).status_code == 403


def test_a_refused_write_stores_nothing(client, signer, engine):
    client.post("/api/notes", json={"text": "x"}, headers=headers(signer, permissions=READER))
    client.post("/api/notes", json={"text": "x"})

    with Session(engine) as session:
        assert session.scalar(select(func.count()).select_from(Note)) == 0


# --- the body of a POST -------------------------------------------------------------------------------------------------


@pytest.mark.parametrize(
    "body",
    [
        b"", b"not json", b"[]", b'"text"', b"null", b"1", b"{}", b'{"text": 1}', b'{"text": null}', b'{"text": ["a"]}',
        b'{"text": ""}', b'{"Text": "x"}', b'{"text": "' + b"x" * 1001 + b'"}', ('{"text": "a' + BACKSLASH + 'u0000b"}').encode(), ('{"text": "' + BACKSLASH + 'ud800"}').encode(),
        b'{"text": "x"', b"\xff\xfe", b'{"text": "' + b"x" * 20000 + b'"}',
    ],
)
def test_a_body_that_is_not_an_object_with_a_good_text_is_a_400(client, signer, engine, body):
    response = client.post("/api/notes", content=body, headers={**headers(signer), "Content-Type": "application/json"})

    assert response.status_code == 400
    assert response.json() == {"error": "invalid_request"}
    with Session(engine) as session:
        assert session.scalar(select(func.count()).select_from(Note)) == 0


def test_a_body_over_the_size_cap_is_a_400_even_when_its_text_is_good(client, signer, engine):
    body = json.dumps({"text": "x", "pad": "y" * 20000}).encode()  # about 20 KB; the cap is 16 KiB
    assert len(body) > 16 * 1024

    response = client.post("/api/notes", content=body, headers={**headers(signer), "Content-Type": "application/json"})

    assert response.status_code == 400
    assert response.json() == {"error": "invalid_request"}
    with Session(engine) as session:
        assert session.scalar(select(func.count()).select_from(Note)) == 0


def test_a_body_under_the_size_cap_may_carry_other_members(client, signer):
    body = json.dumps({"text": "x", "pad": "y" * 10000}).encode()
    assert len(body) < 16 * 1024

    response = client.post("/api/notes", content=body, headers={**headers(signer), "Content-Type": "application/json"})

    assert response.status_code == 201


def test_a_bad_body_is_a_401_or_a_403_before_it_is_a_400(client, signer):
    assert client.post("/api/notes", content=b"not json").status_code == 401
    assert client.post("/api/notes", content=b"not json", headers=headers(signer, permissions=READER)).status_code == 403


# --- health -----------------------------------------------------------------------------------------------------------


def test_health_is_200_when_the_database_answers_and_needs_no_token(client):
    response = client.get("/api/health")

    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


def test_health_is_503_when_the_database_does_not_answer(settings, auth, tmp_path):
    engine = create_engine(f"sqlite:///{tmp_path / 'missing-directory' / 'notes.db'}")
    client = TestClient(create_app(settings, engine=engine, auth=auth, migrate=False))

    response = client.get("/api/health")

    assert response.status_code == 503
    assert response.json() == {"error": "database_unavailable"}


# --- startup ----------------------------------------------------------------------------------------------------------


def test_the_service_applies_its_migrations_at_startup(settings, auth, signer, tmp_path):
    engine = create_engine(f"sqlite:///{tmp_path / 'notes.db'}")
    app = create_app(settings, engine=engine, auth=auth)  # migrate=True is the default

    with TestClient(app) as client:  # entering the client runs the startup
        assert add(client, signer, "first").status_code == 201
        assert len(client.get("/api/notes", headers=headers(signer)).json()) == 1


def test_the_service_answers_503_while_auth_core_is_down_and_it_holds_no_key(settings, engine, signer):
    def down(url, timeout):
        raise OSError("down")

    auth = AuthCore(settings.auth_issuer, settings.auth_audience, "http://auth.test/jwks", jwks_cache=JwksCache("http://auth.test/jwks", fetch=down))
    client = TestClient(create_app(settings, engine=engine, auth=auth, migrate=False))

    response = client.get("/api/notes", headers=headers(signer))

    assert response.status_code == 503
    assert response.json() == {"error": "auth_unavailable"}
