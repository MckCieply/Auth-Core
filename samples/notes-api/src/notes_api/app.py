import json
import uuid
from contextlib import asynccontextmanager
from datetime import timezone

from auth_core_fastapi import AuthCore, AuthError, Principal
from fastapi import Depends, FastAPI, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse
from sqlalchemy import select, text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from .db import Note, make_engine, upgrade_database
from .settings import Settings

MAX_BODY_BYTES = 16 * 1024
MAX_TEXT_CHARACTERS = 1000


class InvalidRequest(Exception):
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

    app = FastAPI(title="notes", lifespan=lifespan, docs_url=None, redoc_url=None, openapi_url=None)
    auth.install(app)

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
            return JSONResponse({"error": "database_unavailable"}, status_code=503)
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
        except InvalidRequest:
            return JSONResponse({"error": "invalid_request"}, status_code=400)
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


def _not_found() -> JSONResponse:
    return JSONResponse({"error": "not_found"}, status_code=404)


async def _read_text(request: Request) -> str:
    """The `text` of a body that is a JSON object with a text of 1 to 1000 characters Postgres can store."""
    body = bytearray()
    async for chunk in request.stream():
        body += chunk
        if len(body) > MAX_BODY_BYTES:
            raise InvalidRequest()
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
