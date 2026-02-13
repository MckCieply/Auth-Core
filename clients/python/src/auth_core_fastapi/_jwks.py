"""The signing keys of the instance: fetched on first use, kept in memory, renewed on a schedule."""

import json
import logging
import threading
import time
import urllib.request
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any
from urllib.parse import urlsplit

from jwt import PyJWK, PyJWKSet

log = logging.getLogger(__name__)

TTL_SECONDS = 300.0  # the keys are fetched again after this long
MIN_INTERVAL_SECONDS = 10.0  # at most one fetch, failed or not, in this long, counted from the end of the last one
MAX_STALE_SECONDS = 24 * 3600.0  # held keys are used for no longer than this after the last fetch that worked
TIMEOUT_SECONDS = 5.0
MAX_BYTES = 1_048_576


class KeyUnknown(Exception):
    """The latest fetch worked and the key set has no such key: the token is a 401."""


class KeysUnavailable(Exception):
    """The key is not held and the latest fetch failed: a 503, the user stays signed in."""


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """A 3xx answer is not followed: it reaches the caller as an `HTTPError`, a failed fetch."""

    def redirect_request(self, *args: Any, **kwargs: Any) -> None:
        return None


def _open(url: str, timeout: float) -> Any:
    """The key set is fetched from the address that was configured, and from nowhere else: no proxy named by the
    environment (HTTP_PROXY and the like, nor the registry on Windows), and no redirect.

    The opener is built for each fetch (a fetch is every few minutes at most), so that the rule is made where the fetch is
    made and not once at import, when the environment may be another one.
    """
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoRedirect)
    return opener.open(url, timeout=timeout)


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
        except Exception as exc:  # handed to the caller below
            outcome["error"] = exc
        finally:
            finished.set()

    threading.Thread(target=work, name="auth-core-jwks-fetch", daemon=True).start()
    if not finished.wait(timeout):
        cancelled.set()
        raise TimeoutError("the key set was not fetched in time")
    if "error" in outcome:
        raise outcome["error"]
    if "document" not in outcome:  # the worker ended on something that is not an Exception
        raise RuntimeError("the key set fetch ended without an answer")
    return outcome["document"]


def _download(url: str, timeout: float, cancelled: threading.Event) -> Any:
    body = bytearray()
    with _open(url, timeout) as response:
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
    held, but never twice within `min_interval` (a failed fetch counts). A failed fetch keeps the keys already held,
    for `max_stale` after the last fetch that worked and no longer: then they are gone, and the keys are unavailable
    until a fetch works. The times are read from the clock after a fetch returns, so a slow fetch does not shorten them.
    One fetch runs at a time. A request that holds its key never waits for it. Until the first fetch has
    ended, a request waits for it, for `timeout` at most (then the keys are unavailable). After that, when a fetch is
    running and a request lacks its key, the keys are unavailable at once, whether the first fetch worked or not, so that
    a flood of unknown `kid`s cannot hold the threads of the product while Auth-Core is slow.
    """

    def __init__(
        self,
        url: str,
        *,
        ttl: float = TTL_SECONDS,
        min_interval: float = MIN_INTERVAL_SECONDS,
        timeout: float = TIMEOUT_SECONDS,
        max_stale: float = MAX_STALE_SECONDS,
        clock: Callable[[], float] = time.monotonic,
        fetch: Callable[[str, float], Any] = fetch_jwks,
    ) -> None:
        if urlsplit(url).scheme not in ("http", "https"):
            raise ValueError("the key set URL must be an http or https URL")
        self._url = url
        self._ttl = ttl
        self._min_interval = min_interval
        self._timeout = timeout
        self._max_stale = max_stale
        self._clock = clock
        self._fetch = fetch
        self._snapshot = _Snapshot({}, None)
        self._attempted_at: float | None = None
        self._last_ok = False
        self._first_attempt_done = False  # a fetch has ended, whatever it came to: the cold waiters wait for this one only
        # `_cond` guards `_fetching` and the swap of the snapshot. It is held for a few instructions, never during a fetch, so
        # "a fetch is running" is exactly `_fetching`, and nothing else can make a request think so.
        self._cond = threading.Condition()
        self._fetching = False

    def key_for(self, kid: str) -> Any:
        waited_since: float | None = None
        while True:
            now = self._clock()
            snapshot = self._snapshot
            held = snapshot.keys.get(kid) if self._is_usable(snapshot, now) else None
            if held is not None and self._is_fresh(snapshot, now):
                return held.key
            with self._cond:
                snapshot = self._snapshot
                if self._fetching:
                    if held is not None:
                        return held.key  # held but old: one request renews the keys, the others carry on with what they hold
                    if snapshot.fetched_at is not None:
                        # Warm cache, a fetch is running and the key is not held: answer now, a thread is not worth holding.
                        raise KeysUnavailable()
                    # Cold cache: no fetch has ever worked. The requests of a product that has just started wait for the
                    # first fetch that is running, no longer than the fetch itself may take: nothing they could use is
                    # held, and a 503 would greet the users of a product that has just started. A later fetch of a cold
                    # cache follows a failed one: it is answered at once, so that a hung Auth-Core holds no thread again.
                    if self._first_attempt_done:
                        raise KeysUnavailable()
                    if waited_since is None:
                        waited_since = time.monotonic()
                    remaining = self._timeout - (time.monotonic() - waited_since)
                    if remaining <= 0 or not self._cond.wait_for(lambda: not self._fetching, remaining):
                        raise KeysUnavailable()
                    continue  # the fetch has ended: look at what it left
                started_at = self._clock()
                if not self._should_fetch(kid, snapshot, started_at):
                    return self._answer(kid)
                self._fetching = True
            break
        self._fetch_and_store(started_at)
        with self._cond:
            return self._answer(kid)

    def _answer(self, kid: str) -> Any:  # called with `_cond` held
        snapshot = self._snapshot
        found = snapshot.keys.get(kid) if self._is_usable(snapshot, self._clock()) else None
        if found is not None:
            return found.key
        raise KeyUnknown() if self._last_ok else KeysUnavailable()

    def _is_fresh(self, snapshot: _Snapshot, now: float) -> bool:
        return snapshot.fetched_at is not None and now - snapshot.fetched_at < self._ttl

    def _is_usable(self, snapshot: _Snapshot, now: float) -> bool:
        """Held keys are used while the fetches fail, but not for longer than `max_stale` after the last good one."""
        return snapshot.fetched_at is not None and now - snapshot.fetched_at < self._max_stale

    def _should_fetch(self, kid: str, snapshot: _Snapshot, now: float) -> bool:  # called with `_cond` held
        if self._attempted_at is not None and now - self._attempted_at < self._min_interval:
            return False
        return not (kid in snapshot.keys and self._is_fresh(snapshot, now) and self._is_usable(snapshot, now))

    def _fetch_and_store(self, started_at: float) -> None:  # called by the one request that set `_fetching`
        keys: dict[str, PyJWK] | None = None
        finished_at: float | None = None
        try:
            try:
                keys = _parse(self._fetch(self._url, self._timeout))
            except Exception as exc:  # any failure keeps the keys already held
                log.warning("the key set could not be fetched (%s)", type(exc).__name__)
            finished_at = self._clock()  # after the fetch: a slow fetch does not shorten the age of the keys
        finally:
            # Whatever happened above, even a clock that raises or a fetch that ends on something that is not an `Exception`: the
            # attempt is recorded, the flag is cleared and the waiters are woken.
            with self._cond:
                # When the end of the attempt could not be timed, it counts from its start: that is a reading of the same clock,
                # taken a moment before and known to have worked, and it needs no new read of a clock that may be failing. The
                # interval then runs out at most `timeout` early, never late, and the waiters that this end wakes find the
                # attempt recorded and are answered, not sent on to a second one. Only a time that was read after the fetch
                # dates the keys: a fallback never makes them look newer than they are.
                self._attempted_at = finished_at if finished_at is not None else started_at
                if finished_at is not None and keys is not None:
                    self._snapshot = _Snapshot(keys, finished_at)
                self._last_ok = keys is not None and finished_at is not None
                self._first_attempt_done = True
                self._fetching = False
                self._cond.notify_all()


def _parse(document: Any) -> dict[str, PyJWK]:
    jwks = PyJWKSet.from_dict(document)  # raises when no key in it is usable
    keys = {key.key_id: key for key in jwks.keys if key.key_id and key.key_type == "RSA"}
    if not keys:
        raise ValueError("the key set holds no RSA key with a kid")
    return keys
