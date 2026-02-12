"""The signing keys of the instance: fetched on first use, kept in memory, renewed on a schedule."""

import json
import logging
import threading
import time
import urllib.request
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

from jwt import PyJWK, PyJWKSet

log = logging.getLogger(__name__)

TTL_SECONDS = 300.0  # the keys are fetched again after this long
MIN_INTERVAL_SECONDS = 10.0  # at most one fetch, failed or not, in this long
TIMEOUT_SECONDS = 5.0
MAX_BYTES = 1_048_576


class KeyUnknown(Exception):
    """The latest fetch worked and the key set has no such key: the token is a 401."""


class KeysUnavailable(Exception):
    """The key is not held and the latest fetch failed: a 503, the user stays signed in."""


def fetch_jwks(url: str, timeout: float) -> Any:
    """GET the key set as JSON. Gives up after `timeout` seconds **in all**, however slowly the server sends.

    The socket timeout of `urlopen` bounds each wait, not the whole transfer: a server that sends a byte a second would
    never trip it. So the transfer runs in a worker thread that the caller stops waiting for at the deadline; the
    thread notices at its next chunk and ends.
    """
    outcome: dict[str, Any] = {}
    finished = threading.Event()
    cancelled = threading.Event()

    def work() -> None:
        try:
            outcome["document"] = _download(url, timeout, cancelled)
        except BaseException as exc:  # handed to the caller below
            outcome["error"] = exc
        finally:
            finished.set()

    threading.Thread(target=work, name="auth-core-jwks-fetch", daemon=True).start()
    if not finished.wait(timeout):
        cancelled.set()
        raise TimeoutError("the key set was not fetched in time")
    if "error" in outcome:
        raise outcome["error"]
    return outcome["document"]


def _download(url: str, timeout: float, cancelled: threading.Event) -> Any:
    body = bytearray()
    with urllib.request.urlopen(url, timeout=timeout) as response:
        while chunk := response.read1(8192):  # what has arrived, not a wait for 8192 bytes
            if cancelled.is_set():
                raise TimeoutError("the key set was not fetched in time")
            body += chunk
            if len(body) > MAX_BYTES:
                raise ValueError("the key set is too large")
    return json.loads(bytes(body))


@dataclass(frozen=True)
class _Snapshot:
    keys: dict[str, PyJWK]  # by kid
    fetched_at: float | None  # of the last fetch that worked


class JwksCache:
    """Keys by `kid`. Creating it makes no network call.

    A fetch happens on first use, when the keys are older than `ttl`, and when a token names a `kid` that is not
    held, but never twice within `min_interval` (a failed fetch counts). A failed fetch keeps the keys already held.
    One fetch runs at a time; a request that finds its key held never waits for it, and one that lacks its key waits
    for the running fetch no longer than `timeout` (then the keys are unavailable).
    """

    def __init__(
        self,
        url: str,
        *,
        ttl: float = TTL_SECONDS,
        min_interval: float = MIN_INTERVAL_SECONDS,
        timeout: float = TIMEOUT_SECONDS,
        clock: Callable[[], float] = time.monotonic,
        fetch: Callable[[str, float], Any] = fetch_jwks,
    ) -> None:
        self._url = url
        self._ttl = ttl
        self._min_interval = min_interval
        self._timeout = timeout
        self._clock = clock
        self._fetch = fetch
        self._snapshot = _Snapshot({}, None)
        self._attempted_at: float | None = None
        self._last_ok = False
        self._lock = threading.Lock()

    def key_for(self, kid: str) -> Any:
        snapshot = self._snapshot
        held = snapshot.keys.get(kid)
        if held is not None and self._is_fresh(snapshot, self._clock()):
            return held.key
        if held is not None:
            # Held but old: one request renews the keys, the others carry on with what they hold.
            if not self._lock.acquire(blocking=False):
                return held.key
        elif not self._lock.acquire(timeout=self._timeout):
            # A fetch is running and the keys are not there: wait for it no longer than the fetch itself may take.
            raise KeysUnavailable()
        try:
            self._renew(kid)
            found = self._snapshot.keys.get(kid)
            last_ok = self._last_ok
        finally:
            self._lock.release()
        if found is not None:
            return found.key
        raise KeyUnknown() if last_ok else KeysUnavailable()

    def _is_fresh(self, snapshot: _Snapshot, now: float) -> bool:
        return snapshot.fetched_at is not None and now - snapshot.fetched_at < self._ttl

    def _renew(self, kid: str) -> None:  # called with the lock held
        now = self._clock()
        if self._attempted_at is not None and now - self._attempted_at < self._min_interval:
            return
        snapshot = self._snapshot
        if kid in snapshot.keys and self._is_fresh(snapshot, now):
            return
        self._attempted_at = now
        try:
            keys = _parse(self._fetch(self._url, self._timeout))
        except Exception as exc:  # any failure keeps the keys already held
            self._last_ok = False
            log.warning("the key set could not be fetched (%s)", type(exc).__name__)
            return
        self._snapshot = _Snapshot(keys, now)
        self._last_ok = True


def _parse(document: Any) -> dict[str, PyJWK]:
    jwks = PyJWKSet.from_dict(document)  # raises when no key in it is usable
    keys = {key.key_id: key for key in jwks.keys if key.key_id and key.key_type == "RSA"}
    if not keys:
        raise ValueError("the key set holds no RSA key with a kid")
    return keys
