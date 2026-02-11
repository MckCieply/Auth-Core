"""The service against SQLite in memory (the real migration builds the table) and keys made here: no Docker."""

import base64
import os
import time
import uuid

import jwt
import pytest
from auth_core_fastapi import AuthCore, JwksCache
from cryptography.hazmat.primitives.asymmetric import rsa
from fastapi.testclient import TestClient
from sqlalchemy import create_engine, text
from sqlalchemy.pool import StaticPool

from notes_api.app import create_app
from notes_api.db import upgrade_database
from notes_api.settings import Settings

ISSUER = "http://localhost:8088/auth"
AUDIENCE = "notes-api"
JWKS_URL = "http://auth.test/auth/.well-known/jwks.json"

COMPANY_A = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
COMPANY_B = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"


def _b64url(number: int) -> str:
    raw = number.to_bytes((number.bit_length() + 7) // 8, "big")
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()


class Signer:
    def __init__(self) -> None:
        self.key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        numbers = self.key.public_key().public_numbers()
        self.jwk = {"kty": "RSA", "use": "sig", "alg": "RS256", "kid": "k1", "n": _b64url(numbers.n), "e": _b64url(numbers.e)}

    def token(self, org_id: str, permissions: list[str], sub: str = "user-1") -> str:
        now = int(time.time())
        claims = {
            "iss": ISSUER, "aud": AUDIENCE, "sub": sub, "org_id": org_id, "roles": [],
            "permissions": permissions, "iat": now, "exp": now + 600,
        }
        return jwt.encode(claims, self.key, algorithm="RS256", headers={"kid": "k1", "typ": "at+jwt"})


@pytest.fixture(scope="session")
def signer() -> Signer:
    return Signer()


@pytest.fixture
def engine():
    """SQLite in memory. With NOTES_TEST_DATABASE_URL set (an empty PostgreSQL database), the same tests run there."""
    url = os.environ.get("NOTES_TEST_DATABASE_URL")
    if url:
        engine = create_engine(url)
        _drop_everything(engine)
    else:
        engine = create_engine("sqlite://", poolclass=StaticPool, connect_args={"check_same_thread": False})
    upgrade_database(engine)  # the real migration builds the table
    yield engine
    if url:
        _drop_everything(engine)
    engine.dispose()


def _drop_everything(engine) -> None:
    with engine.begin() as connection:
        connection.execute(text("DROP TABLE IF EXISTS notes, alembic_version"))


@pytest.fixture
def settings() -> Settings:
    return Settings(auth_issuer=ISSUER, auth_audience=AUDIENCE, auth_jwks_url=None, database_url="sqlite://")


@pytest.fixture
def auth(signer) -> AuthCore:
    cache = JwksCache(JWKS_URL, fetch=lambda url, timeout: {"keys": [signer.jwk]})
    return AuthCore(ISSUER, AUDIENCE, JWKS_URL, jwks_cache=cache)


@pytest.fixture
def client(settings, engine, auth) -> TestClient:
    return TestClient(create_app(settings, engine=engine, auth=auth, migrate=False))


def headers(signer: Signer, org_id: str = COMPANY_A, permissions=("notes:read", "notes:write"), sub: str = "user-1") -> dict:
    return {"Authorization": "Bearer " + signer.token(org_id, list(permissions), sub)}


def new_id() -> str:
    return str(uuid.uuid4())
