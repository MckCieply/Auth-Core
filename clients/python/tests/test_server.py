"""The dependencies against a key set served over real HTTP, and the event loop."""

import asyncio

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
    server.delay = 3
    app = build_app(AuthCore(ISSUER, AUDIENCE, server.url))

    async def scenario():
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(transport=transport, base_url="http://test") as client:
            slow = asyncio.create_task(client.get("/me", headers=bearer(key1.token())))
            for _ in range(500):  # until the slow request is inside the fetch: the server has it
                if server.requests:
                    break
                await asyncio.sleep(0.01)
            requests_at_ping = server.requests
            await client.get("/ping")
            slow_done_at_ping = slow.done()
            response = await slow
            return requests_at_ping, slow_done_at_ping, response.status_code

    requests_at_ping, slow_done_at_ping, status = asyncio.run(scenario())

    assert requests_at_ping == 1  # the server had the fetch before the ping was sent
    assert not slow_done_at_ping  # and the ping was answered while the fetch was still running
    assert status == 200
