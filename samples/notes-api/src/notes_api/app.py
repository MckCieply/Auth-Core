import json
import logging
import uuid
from contextlib import asynccontextmanager
from datetime import timezone

from auth_core_fastapi import AuthCore, AuthError, Principal
from fastapi import Depends, FastAPI, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.exception_handlers import http_exception_handler
from fastapi.responses import JSONResponse
from sqlalchemy import select, text
from sqlalchemy.engine import Engine
from sqlalchemy.exc import DBAPIError
from sqlalchemy.orm import Session
from starlette.exceptions import HTTPException
from starlette.requests import ClientDisconnect

from .db import Note, make_engine, upgrade_database
from .settings import Settings

log = logging.getLogger(__name__)

MAX_BODY_BYTES = 16 * 1024
MAX_TEXT_CHARACTERS = 1000


class InvalidRequest(Exception):
    pass


class TooLarge(InvalidRequest):
    pass


def create_app(
    settings: Settings,
    *,
    engine: Engine | None = None,
    auth: AuthCore | None = None,
    migrate: bool = True,
) -> FastAPI:
    engine = engine or make_engine(settings.database_url)
    auth = auth or AuthCore(settings.auth_issuer, settings.auth_audience, settings.auth_jwks_url)

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        if migrate:
            upgrade_database(engine)  # at startup, before the first request
        yield
        engine.dispose()

    # redirect_slashes off: "/api/notes/" is a 404, not a redirect to an address built from the Host header
    app = FastAPI(
        title="notes", lifespan=lifespan, docs_url=None, redoc_url=None, openapi_url=None, redirect_slashes=False
    )
    auth.install(app)
    _install_error_shapes(app)

    @app.middleware("http")
    async def never_stored(request: Request, call_next):
        response = await call_next(request)
        response.headers.setdefault("Cache-Control", "no-store")
        return response

    @app.get("/api/health")
    def health():
        try:
            with engine.connect() as connection:
                connection.execute(text("SELECT 1"))
        except Exception:
            return _error(503, "database_unavailable")
        return {"status": "ok"}

    @app.get("/api/notes")
    def list_notes(user: Principal = Depends(auth.require_permission("notes:read"))):
        org_id = _org_id(user)
        with Session(engine) as session:
            statement = (
                select(Note)
                .where(Note.org_id == org_id)  # every query is filtered by the token's company
                .order_by(Note.created_at.desc(), Note.id.desc())
            )
            return [_view(note) for note in session.scalars(statement)]

    @app.get("/api/notes/{note_id}")
    def get_note(note_id: str, user: Principal = Depends(auth.require_permission("notes:read"))):
        org_id = _org_id(user)
        wanted = _uuid(note_id)
        if wanted is None:
            return _not_found()
        with Session(engine) as session:
            statement = select(Note).where(Note.org_id == org_id, Note.id == wanted)
            note = session.scalars(statement).one_or_none()
            return _view(note) if note is not None else _not_found()

    @app.post("/api/notes", status_code=201)
    async def add_note(request: Request, user: Principal = Depends(auth.require_permission("notes:write"))):
        org_id = _org_id(user)
        try:
            note_text = await _read_text(request)
        except TooLarge:
            # Not read to its end: the answer says the connection ends with it, so the server drops the rest of the body.
            return _error(400, "invalid_request", headers={"Connection": "close"})
        except InvalidRequest:
            return _error(400, "invalid_request")
        except ClientDisconnect:
            log.debug("the client went away in the middle of the body")
            return _error(400, "invalid_request")  # nobody is left to read it
        return await run_in_threadpool(_insert, engine, org_id, user.sub, note_text)

    return app


def _org_id(user: Principal) -> uuid.UUID:
    """The company of the caller: the only company a query is ever made for.

    Auth-Core's `org_id` is a UUID. A valid token whose `org_id` is not one was not issued for a product like this.
    """
    try:
        return uuid.UUID(user.org_id)
    except ValueError:
        raise AuthError(401) from None


def _uuid(value: str) -> uuid.UUID | None:
    try:
        parsed = uuid.UUID(value)
    except ValueError:
        return None
    return parsed if str(parsed) == value.lower() else None  # the 36-character form only


def _error(status: int, error: str, headers: dict[str, str] | None = None) -> JSONResponse:
    """Every answer that is an error: `{"error": ...}`, never stored. (Set here too: the 500 does not pass the middleware.)"""
    return JSONResponse({"error": error}, status_code=status, headers={"Cache-Control": "no-store", **(headers or {})})


def _not_found() -> JSONResponse:
    return _error(404, "not_found")


def _install_error_shapes(app: FastAPI) -> None:
    async def http_error(request: Request, exc: HTTPException):
        if exc.status_code == 404:
            return _error(404, "not_found")
        if exc.status_code == 405:
            return _error(405, "method_not_allowed", headers={k: v for k, v in (exc.headers or {}).items()})
        return await http_exception_handler(request, exc)

    async def database_error(request: Request, exc: DBAPIError):
        log.warning("the database did not answer (%s)", type(exc).__name__)  # the class only: no statement, no value
        return _error(503, "database_unavailable")

    async def unexpected_error(request: Request, exc: Exception):
        log.error("unexpected error (%s)", type(exc).__name__)
        return _error(500, "internal_error")

    app.add_exception_handler(HTTPException, http_error)
    app.add_exception_handler(DBAPIError, database_error)
    app.add_exception_handler(Exception, unexpected_error)


async def _read_text(request: Request) -> str:
    """The `text` of a body that is a JSON object with a text of 1 to 1000 characters Postgres can store."""
    body = bytearray()
    async for chunk in request.stream():
        body += chunk
        if len(body) > MAX_BODY_BYTES:
            raise TooLarge()  # stop here: the rest of the body is not read
    try:
        document = json.loads(bytes(body))
    except (ValueError, RecursionError):  # not JSON, or nested deeper than the parser can follow
        raise InvalidRequest() from None
    value = document.get("text") if isinstance(document, dict) else None
    if not isinstance(value, str) or not 1 <= len(value) <= MAX_TEXT_CHARACTERS:
        raise InvalidRequest()
    if "\x00" in value:
        raise InvalidRequest()
    try:
        value.encode("utf-8")  # a lone surrogate (JSON allows one) cannot be stored
    except UnicodeEncodeError:
        raise InvalidRequest() from None
    return value


def _insert(engine: Engine, org_id: uuid.UUID, author_sub: str, note_text: str) -> JSONResponse:
    note = Note(org_id=org_id, author_sub=author_sub, text=note_text)
    with Session(engine) as session:
        session.add(note)
        session.flush()  # the id and the time are set now, so that the answer needs no second query
        view = _view(note)
        session.commit()
    return JSONResponse(view, status_code=201)


def _view(note: Note) -> dict:
    created_at = note.created_at if note.created_at.tzinfo else note.created_at.replace(tzinfo=timezone.utc)
    return {
        "id": str(note.id),
        "text": note.text,
        "author_sub": note.author_sub,
        "created_at": created_at.astimezone(timezone.utc).isoformat().replace("+00:00", "Z"),
    }
