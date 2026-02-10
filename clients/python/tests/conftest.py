import pytest
from fastapi import Depends, FastAPI
from fastapi.testclient import TestClient

from auth_core_fastapi import AuthCore, JwksCache, Principal
from helpers import AUDIENCE, ISSUER, JWKS_URL, Clock, FakeJwks, Signer


def build_app(auth: AuthCore) -> FastAPI:
    app = FastAPI()
    auth.install(app)

    @app.get("/me")
    def me(user: Principal = Depends(auth.current_user)):
        return {
            "sub": user.sub, "org_id": user.org_id, "roles": list(user.roles),
            "permissions": sorted(user.permissions),
            "types": [type(user.roles).__name__, type(user.permissions).__name__],
        }

    @app.post("/write")
    def write(user: Principal = Depends(auth.require_permission("notes:write"))):
        return {"sub": user.sub}

    @app.get("/ping")
    async def ping():
        return {"ok": True}

    return app


@pytest.fixture(scope="session")
def key1() -> Signer:
    return Signer("k1")


@pytest.fixture(scope="session")
def key2() -> Signer:
    return Signer("k2")


@pytest.fixture(scope="session")
def stranger() -> Signer:
    """A key that is not published, with the `kid` of one that is."""
    return Signer("k1")


@pytest.fixture
def jwks(key1) -> FakeJwks:
    return FakeJwks(key1)


@pytest.fixture
def clock() -> Clock:
    return Clock()


@pytest.fixture
def auth(jwks, clock) -> AuthCore:
    return AuthCore(ISSUER, AUDIENCE, JWKS_URL, jwks_cache=JwksCache(JWKS_URL, clock=clock, fetch=jwks))


@pytest.fixture
def client(auth) -> TestClient:
    return TestClient(build_app(auth))
