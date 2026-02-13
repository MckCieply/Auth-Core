"""What the sample answers when something is wrong around the request: routing, the database, the client, the settings."""

import asyncio
import json
import logging
import shutil
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import create_engine, inspect
from sqlalchemy.exc import OperationalError
from sqlalchemy.exc import TimeoutError as PoolTimeout

from conftest import ISSUER, headers
from notes_api import db
from notes_api.app import MAX_BODY_BYTES, create_app
from notes_api.db import make_engine, upgrade_database
from notes_api.settings import Settings


# --- routing: 404 and 405 are JSON like every other answer -----------------------------------------------------------------


@pytest.mark.parametrize("path", ["/", "/api", "/api/nothing", "/api/notes/a/b", "/elsewhere"])
def test_an_unknown_path_is_a_json_404_that_is_never_stored(client, path):
    response = client.get(path)

    assert response.status_code == 404
    assert response.json() == {"error": "not_found"}
    assert response.headers["content-type"] == "application/json"
    assert response.headers["cache-control"] == "no-store"


@pytest.mark.parametrize(
    "method, path, allowed",
    [
        ("DELETE", "/api/notes", "GET, POST"),
        ("PUT", "/api/notes", "GET, POST"),
        ("PUT", "/api/notes/x", "GET"),
        ("POST", "/api/health", "GET"),
    ],
)
def test_a_method_that_is_not_allowed_is_a_json_405_that_lists_every_method_the_path_allows(client, method, path, allowed):
    response = client.request(method, path)

    assert response.status_code == 405
    assert response.json() == {"error": "method_not_allowed"}
    assert response.headers["cache-control"] == "no-store"
    assert response.headers["allow"] == allowed


def test_the_allow_header_of_every_route_lists_all_the_methods_of_its_path(client):
    """Whatever the routes are: the methods of a path are all the methods of the routes that have that path."""
    methods_of: dict[str, set[str]] = {}
    for route in client.app.routes:
        if getattr(route, "methods", None) and route.path.startswith("/api"):
            methods_of.setdefault(route.path, set()).update(route.methods)
    assert {"/api/notes", "/api/health"} <= set(methods_of)  # the routes were found

    for path, methods in methods_of.items():
        response = client.request("DELETE", path.replace("{note_id}", "x"))

        assert response.status_code == 405, path
        assert response.headers["allow"] == ", ".join(sorted(methods)), path


def test_the_trailing_slash_is_not_redirected(client, signer):
    response = client.get("/api/notes/", headers={**headers(signer), "Host": "evil.example"}, follow_redirects=False)

    assert response.status_code == 404
    assert "location" not in response.headers
    assert response.json() == {"error": "not_found"}


# --- the database: 503 when it does not answer, on every notes endpoint ---------------------------------------------------


@pytest.fixture
def broken_database(settings, auth, tmp_path):
    engine = create_engine(f"sqlite:///{tmp_path / 'missing-directory' / 'notes.db'}")
    return TestClient(create_app(settings, engine=engine, auth=auth, migrate=False))


@pytest.mark.parametrize(
    "method, path",
    [("GET", "/api/notes"), ("GET", "/api/notes/11111111-1111-4111-8111-111111111111"), ("POST", "/api/notes")],
)
def test_a_database_that_does_not_answer_is_a_503(broken_database, signer, method, path):
    response = broken_database.request(method, path, json={"text": "hello"}, headers=headers(signer))

    assert response.status_code == 503
    assert response.json() == {"error": "database_unavailable"}
    assert response.headers["content-type"] == "application/json"
    assert response.headers["cache-control"] == "no-store"


def test_the_database_error_is_logged_by_its_class_and_not_by_its_text(settings, engine, auth, signer, caplog, monkeypatch):
    """The error carries a statement and a driver message, as a real one does: none of it is in the log."""
    import notes_api.app as app_module

    def fail(*args, **kwargs):
        raise OperationalError("INSERT INTO secret_table VALUES (?)", ("secret note text",), Exception("driver says secret-driver-text"))

    monkeypatch.setattr(app_module, "_insert", fail)
    client = TestClient(create_app(settings, engine=engine, auth=auth, migrate=False))

    with caplog.at_level(logging.DEBUG):
        response = client.post("/api/notes", json={"text": "hello"}, headers=headers(signer))

    assert response.status_code == 503
    assert "OperationalError" in caplog.text
    for text in ("secret_table", "secret note text", "secret-driver-text", "INSERT"):
        assert text not in caplog.text
    assert all(record.exc_info is None for record in caplog.records)  # no traceback attached to any record


def test_a_database_that_does_not_answer_is_logged_once_per_request_by_its_class_on_the_notes_endpoints(broken_database, signer, caplog):
    """Each request to a notes endpoint leaves one line, the class and nothing else (the health check logs once per outage: below)."""
    with caplog.at_level(logging.DEBUG):
        first = broken_database.get("/api/notes", headers=headers(signer))
        second = broken_database.get("/api/notes", headers=headers(signer))

    assert (first.status_code, second.status_code) == (503, 503)
    lines = [record.getMessage() for record in caplog.records if "did not answer" in record.getMessage()]
    assert lines == ["the database did not answer (OperationalError)"] * 2
    assert all(record.exc_info is None for record in caplog.records)


def switchable_database(settings, engine, auth, monkeypatch):
    """A client whose database can be taken away and given back: `state["up"]`."""
    state = {"up": True}
    real_connect = engine.connect

    def connect(*args, **kwargs):
        if not state["up"]:
            raise OperationalError("SELECT 1", (), Exception("driver says secret-driver-text"))
        return real_connect(*args, **kwargs)

    monkeypatch.setattr(engine, "connect", connect)
    return TestClient(create_app(settings, engine=engine, auth=auth, migrate=False)), state


def health_lines(caplog):
    return [record.getMessage() for record in caplog.records if record.name.startswith("notes_api")]


def test_the_health_check_logs_a_database_that_does_not_answer_once_per_outage(settings, engine, auth, caplog, monkeypatch):
    """The compose healthcheck calls it every 5 seconds: a long outage must not fill the log, one line says it began."""
    client, state = switchable_database(settings, engine, auth, monkeypatch)
    state["up"] = False

    with caplog.at_level(logging.DEBUG):
        responses = [client.get("/api/health") for _ in range(3)]

    assert [response.status_code for response in responses] == [503, 503, 503]  # every call still says so
    assert [response.json() for response in responses] == [{"error": "database_unavailable"}] * 3
    assert health_lines(caplog) == ["the database did not answer (OperationalError)"]
    assert "secret-driver-text" not in caplog.text
    assert all(record.exc_info is None for record in caplog.records)


def test_the_health_check_logs_again_when_the_database_fails_after_it_has_recovered(settings, engine, auth, caplog, monkeypatch):
    client, state = switchable_database(settings, engine, auth, monkeypatch)

    with caplog.at_level(logging.DEBUG):
        state["up"] = False
        assert client.get("/api/health").status_code == 503
        assert client.get("/api/health").status_code == 503
        state["up"] = True
        assert client.get("/api/health").status_code == 200
        assert client.get("/api/health").status_code == 200  # up again: nothing more
        state["up"] = False
        assert client.get("/api/health").status_code == 503
        assert client.get("/api/health").status_code == 503

    assert health_lines(caplog) == [
        "the database did not answer (OperationalError)",
        "the database answers again",
        "the database did not answer (OperationalError)",
    ]


def test_a_healthy_database_leaves_no_line_in_the_log_from_the_health_check(client, caplog):
    with caplog.at_level(logging.DEBUG):
        assert client.get("/api/health").status_code == 200
        assert client.get("/api/health").status_code == 200

    assert health_lines(caplog) == []


def test_the_notes_endpoints_keep_logging_once_per_request_while_the_health_check_stays_quiet(settings, engine, auth, signer, caplog, monkeypatch):
    client, state = switchable_database(settings, engine, auth, monkeypatch)
    state["up"] = False

    with caplog.at_level(logging.DEBUG):
        client.get("/api/health")
        client.get("/api/health")  # quiet: the outage is already told
        client.get("/api/notes", headers=headers(signer))
        client.get("/api/notes", headers=headers(signer))

    # one from the first health call, one from each notes request: the notes endpoints do not share the health state
    assert len(health_lines(caplog)) == 3


def test_the_connection_pool_running_out_is_a_503_like_a_database_that_does_not_answer(settings, engine, auth, signer, monkeypatch):
    import notes_api.app as app_module

    def fail(*args, **kwargs):
        raise PoolTimeout("QueuePool limit of size 5 overflow 10 reached")

    monkeypatch.setattr(app_module, "_insert", fail)
    client = TestClient(create_app(settings, engine=engine, auth=auth, migrate=False))

    response = client.post("/api/notes", json={"text": "hello"}, headers=headers(signer))

    assert response.status_code == 503
    assert response.json() == {"error": "database_unavailable"}


# --- anything else that goes wrong: 500, in the same shape ----------------------------------------------------------------


def explode_on_insert(monkeypatch, message="the text of a note must not appear: hello"):
    import notes_api.app as app_module

    def explode(*args, **kwargs):
        raise RuntimeError(message)

    monkeypatch.setattr(app_module, "_view", explode)


def test_an_unexpected_error_is_a_json_500_that_is_never_stored(settings, engine, auth, signer, monkeypatch):
    explode_on_insert(monkeypatch)
    # The server's exceptions are raised in the test client's caller, as the framework re-raises what reaches its last layer:
    # this one must not get that far, or the server logs the traceback and the message of it (the default of the client).
    client = TestClient(create_app(settings, engine=engine, auth=auth, migrate=False))

    response = client.post("/api/notes", json={"text": "hello"}, headers=headers(signer))

    assert response.status_code == 500
    assert response.json() == {"error": "internal_error"}
    assert response.headers["content-type"] == "application/json"
    assert response.headers["cache-control"] == "no-store"
    assert "hello" not in response.text


def test_an_unexpected_error_is_logged_by_its_class_alone(settings, engine, auth, signer, caplog, monkeypatch):
    explode_on_insert(monkeypatch, "secret-exception-text with a note: hello")
    client = TestClient(create_app(settings, engine=engine, auth=auth, migrate=False))

    with caplog.at_level(logging.DEBUG):
        client.post("/api/notes", json={"text": "hello"}, headers=headers(signer))

    errors = [record for record in caplog.records if record.levelno >= logging.ERROR]
    assert len(errors) == 1  # at error level, once
    assert "RuntimeError" in errors[0].getMessage()
    assert "secret-exception-text" not in caplog.text
    assert "hello" not in caplog.text
    assert all(record.exc_info is None for record in caplog.records)  # no traceback attached to any record


def test_an_error_after_the_answer_has_started_is_not_logged_with_its_text(settings, engine, auth, caplog):
    """Nothing in the sample does this, but a handler that cannot send a 500 must not fall back to the framework's log."""
    app = create_app(settings, engine=engine, auth=auth, migrate=False)

    async def go():
        sent = []

        async def inner(scope, receive, send):
            await send({"type": "http.response.start", "status": 200, "headers": []})
            raise RuntimeError("secret-late-text")

        async def send(message):
            sent.append(message)

        async def receive():
            return {"type": "http.disconnect"}

        from notes_api.app import _InternalErrors

        await _InternalErrors(inner)({"type": "http", "method": "GET", "path": "/"}, receive, send)
        return sent

    with caplog.at_level(logging.DEBUG):
        sent = asyncio.run(go())

    assert [message["type"] for message in sent] == ["http.response.start"]  # no second answer on top of the first
    assert "RuntimeError" in caplog.text
    assert "secret-late-text" not in caplog.text


# --- the body: stop reading at the cap, and a client that goes away is not an error ---------------------------------------


def run_asgi(app, headers_list, chunks, *, disconnect_after=None):
    """Call the app as a server would. `chunks` is an iterator of body chunks. Returns (status, headers, chunks read)."""
    sent = []
    read = {"chunks": 0}
    scope = {
        "type": "http", "asgi": {"version": "3.0"}, "http_version": "1.1", "method": "POST", "path": "/api/notes",
        "raw_path": b"/api/notes", "query_string": b"", "headers": headers_list, "scheme": "http",
        "server": ("test", 80), "client": ("peer", 1),
    }

    async def receive():
        read["chunks"] += 1
        if disconnect_after is not None and read["chunks"] > disconnect_after:
            return {"type": "http.disconnect"}
        chunk = next(chunks, None)
        if chunk is None:
            return {"type": "http.request", "body": b"", "more_body": False}
        return {"type": "http.request", "body": chunk, "more_body": True}

    async def send(message):
        sent.append(message)

    async def go():
        await app(scope, receive, send)

    asyncio.run(go())
    start = next(message for message in sent if message["type"] == "http.response.start")
    return start["status"], {k.decode().lower(): v.decode() for k, v in start["headers"]}, read["chunks"]


def endless(size):
    while True:
        yield b"x" * size


def test_an_over_limit_body_is_not_read_to_its_end(settings, engine, auth, signer):
    app = create_app(settings, engine=engine, auth=auth, migrate=False)
    token = headers(signer)["Authorization"].encode()
    request_headers = [(b"authorization", token), (b"content-type", b"application/json"), (b"host", b"test")]

    status, response_headers, chunks_read = run_asgi(app, request_headers, endless(1024))

    assert status == 400
    assert chunks_read <= MAX_BODY_BYTES // 1024 + 10  # a few chunks past the cap, not the 200 MiB of the attacker
    assert response_headers["connection"] == "close"  # the server stops taking the rest of the body


def test_a_client_that_goes_away_in_the_middle_of_the_body_is_not_an_error(settings, engine, auth, signer, caplog):
    app = create_app(settings, engine=engine, auth=auth, migrate=False)
    token = headers(signer)["Authorization"].encode()
    request_headers = [(b"authorization", token), (b"content-length", b"1000"), (b"host", b"test")]

    with caplog.at_level(logging.DEBUG):
        status, _, _ = run_asgi(app, request_headers, iter([b'{"text": "ab']), disconnect_after=1)

    assert status < 500
    assert not [record for record in caplog.records if record.levelno >= logging.ERROR]


def test_the_database_connection_has_a_timeout_for_postgresql_only(monkeypatch):
    seen = []

    def fake_create_engine(url, **kwargs):
        seen.append((url, kwargs))
        return object()

    monkeypatch.setattr(db, "create_engine", fake_create_engine)

    make_engine("postgresql+psycopg://notes:pw@db:5432/notes")
    make_engine("sqlite://")

    assert seen[0][1]["connect_args"] == {"connect_timeout": 5}
    assert "connect_args" not in seen[1][1]
    assert seen[0][1]["hide_parameters"] is True


# --- the settings: the password stays out of anything that prints them ------------------------------------------------------


def test_the_settings_do_not_show_the_database_password():
    settings = Settings(
        auth_issuer=ISSUER, auth_audience="notes-api", auth_jwks_url=None,
        database_url="postgresql+psycopg://notes:S3cretPW@db:5432/notes",
    )

    for shown in (repr(settings), str(settings), f"{settings}", f"{settings!r}"):
        assert "S3cretPW" not in shown
        assert "notes-api" in shown  # the other settings are still shown
    assert settings.database_url.endswith("@db:5432/notes")  # and the value is still there to use


# --- the model and the migration agree on the time, and the migration finds its files wherever they are ------------------


def test_the_model_and_the_migration_agree_on_the_default_of_created_at(tmp_path):
    engine = create_engine(f"sqlite:///{tmp_path / 'notes.db'}")
    upgrade_database(engine)

    column = {column["name"]: column for column in inspect(engine).get_columns("notes")}["created_at"]

    assert column["default"] is not None  # the migration gives the column a default
    assert db.Note.__table__.c.created_at.server_default is not None  # and the model says so too


def test_a_path_with_a_percent_sign_does_not_break_the_migration(tmp_path, monkeypatch):
    root = tmp_path / "100%-sure"
    shutil.copytree(db.ROOT / "migrations", root / "migrations", ignore=shutil.ignore_patterns("__pycache__"))
    shutil.copy(db.ROOT / "alembic.ini", root / "alembic.ini")
    monkeypatch.setattr(db, "ROOT", Path(root))
    engine = create_engine(f"sqlite:///{tmp_path / 'notes.db'}")

    upgrade_database(engine)

    assert "notes" in inspect(engine).get_table_names()
