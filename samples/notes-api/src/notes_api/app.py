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
from sqlalchemy.exc import TimeoutError as PoolTimeout
from sqlalchemy.orm import Session
from starlette.exceptions import HTTPException
from starlette.requests import ClientDisconnect
from starlette.routing import Match
from starlette.types import ASGIApp, Receive, Scope, Send

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
    app.add_middleware(_InternalErrors)  # added first: the innermost of the middlewares, so that its answer passes the others

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
        except Exception as exc:
            log.warning("the database did not answer (%s)", type(exc).__name__)  # as on the notes endpoints: the class only
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


class _InternalErrors:
    """Whatever the handlers did not take: logged by its class alone, answered with the 500 here, and not raised again.

    The framework's own last layer logs a traceback, with the message of the exception, and re-raises it for the server to
    log again: the message of a database or a driver can hold a statement or a value. So the 500 is answered before that.
    """

    def __init__(self, app: ASGIApp) -> None:
        self.app = app

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return
        started = False

        async def watching_send(message):
            nonlocal started
            if message["type"] == "http.response.start":
                started = True
            await send(message)

        try:
            await self.app(scope, receive, watching_send)
        except Exception as exc:
            log.error("unexpected error (%s)", type(exc).__name__)  # the class only: no message, no traceback
            if not started:
                await _error(500, "internal_error")(scope, receive, send)
            # else: the answer is on its way and cannot be changed. The server ends it, and says so without a traceback.


def _allowed_methods(request: Request) -> str:
    """Every method of the path, from all the routes that have it: the framework names those of the first one only."""
    methods: set[str] = set()
    for route in request.app.router.routes:
        match, _ = route.matches(request.scope)
        if match in (Match.FULL, Match.PARTIAL):
            methods.update(getattr(route, "methods", None) or ())
    return ", ".join(sorted(methods))


def _install_error_shapes(app: FastAPI) -> None:
    async def http_error(request: Request, exc: HTTPException):
        if exc.status_code == 404:
            return _error(404, "not_found")
        if exc.status_code == 405:
            return _error(405, "method_not_allowed", headers={"Allow": _allowed_methods(request)})
        return await http_exception_handler(request, exc)

    async def database_error(request: Request, exc: Exception):
        log.warning("the database did not answer (%s)", type(exc).__name__)  # the class only: no statement, no value
        return _error(503, "database_unavailable")

    app.add_exception_handler(HTTPException, http_error)
    app.add_exception_handler(DBAPIError, database_error)
    app.add_exception_handler(PoolTimeout, database_error)  # no connection came free in time: the database is as good as away


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
