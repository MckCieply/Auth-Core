"""The dependencies against a key set served over real HTTP, and the event loop."""

import asyncio
import time

import httpx
import pytest
from fastapi.testclient import TestClient

from auth_core_fastapi import AuthCore
from conftest import build_app
from helpers import AUDIENCE, ISSUER, JwksServer, assert_unauthorized, assert_unavailable, bearer


@pytest.fixture
def server(key1):
    server = JwksServer({"keys": [key1.jwk]})
    yield server
    server.close()


def test_a_token_is_checked_against_the_keys_served_over_http(server, key1):  # criterion 2
    client = TestClient(build_app(AuthCore(ISSUER, AUDIENCE, server.url)))

    assert client.get("/me", headers=bearer(key1.token())).status_code == 200
    assert client.get("/me", headers=bearer(key1.token())).status_code == 200

    assert server.requests == 1


def test_an_unknown_kid_is_a_401_and_a_key_set_that_does_not_answer_is_a_503(server, key1):  # criterion 5
    client = TestClient(build_app(AuthCore(ISSUER, AUDIENCE, server.url)))
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(headers={"kid": "other"}))))

    server.status = 500
    down = TestClient(build_app(AuthCore(ISSUER, AUDIENCE, server.url)))
    assert_unavailable(down.get("/me", headers=bearer(key1.token())))


def test_a_slow_fetch_does_not_block_the_event_loop(server, key1):
    """A request that waits for the key set must not hold up the others (the dependencies are plain `def`)."""
    server.delay = 1.5
    app = build_app(AuthCore(ISSUER, AUDIENCE, server.url))

    async def scenario():
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(transport=transport, base_url="http://test") as client:
            started = time.perf_counter()
            slow = asyncio.create_task(client.get("/me", headers=bearer(key1.token())))
            await asyncio.sleep(0.3)  # the slow request is now inside the fetch
            await client.get("/ping")
            ping_done = time.perf_counter() - started
            response = await slow
            return ping_done, time.perf_counter() - started, response.status_code

    ping_done, slow_done, status = asyncio.run(scenario())

    assert status == 200
    assert slow_done > 1.4
    assert ping_done < 0.8
