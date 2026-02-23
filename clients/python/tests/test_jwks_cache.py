"""The key cache by itself (criteria 5 and 6): a fake JWKS URL and a clock moved by hand, then a real server on 127.0.0.1."""

import json
import threading
import time
import urllib.request

import pytest

from auth_core_fastapi import _jwks
from auth_core_fastapi._jwks import JwksCache, KeysUnavailable, KeyUnknown
from helpers import JWKS_URL, Clock, FakeJwks, JwksServer, SlowJwks, closed_port_url


def cache_of(jwks, clock) -> JwksCache:
    return JwksCache(JWKS_URL, clock=clock, fetch=jwks)


def test_the_defaults_are_the_rules_of_the_spec():
    assert _jwks.TTL_SECONDS == 300.0
    assert _jwks.MIN_INTERVAL_SECONDS == 10.0
    assert _jwks.TIMEOUT_SECONDS == 5.0


# --- criterion 6: nothing is fetched until a key is needed ----------------------------------------------------------------


def test_creating_the_cache_fetches_nothing(jwks, clock):  # criterion 6
    cache_of(jwks, clock)

    assert jwks.fetches == 0


def test_the_keys_are_fetched_on_first_use_and_kept(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)

    for _ in range(3):
        assert cache.key_for("k1") is not None

    assert jwks.fetches == 1


# --- criterion 5: a new key after one refetch, at most every 10 seconds, whatever the number of unknown kids --------------


def test_a_new_key_is_accepted_after_one_refetch(jwks, clock, key1, key2):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")
    jwks.publish(key1, key2)
    clock.advance(10)

    assert cache.key_for("k2") is not None
    assert jwks.fetches == 2


def test_a_new_key_is_not_looked_for_within_ten_seconds_of_the_last_fetch(jwks, clock, key1, key2):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")
    jwks.publish(key1, key2)
    clock.advance(9.9)

    with pytest.raises(KeyUnknown):
        cache.key_for("k2")
    assert jwks.fetches == 1

    clock.advance(0.1)
    assert cache.key_for("k2") is not None
    assert jwks.fetches == 2


def test_many_unknown_kids_cause_one_fetch_in_ten_seconds(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")

    for number in range(30):
        clock.advance(0.3)
        with pytest.raises(KeyUnknown):
            cache.key_for(f"stray-{number}")
    assert jwks.fetches == 1  # nine seconds on: none of the thirty caused a fetch

    clock.advance(1.1)
    for number in range(30):
        with pytest.raises(KeyUnknown):
            cache.key_for(f"more-{number}")
    assert jwks.fetches == 2  # the first of the next thirty did, the other twenty-nine did not


def test_a_key_that_left_the_key_set_stops_working_at_the_next_fetch(jwks, clock, key1, key2):  # criterion 5
    jwks.publish(key1, key2)
    cache = cache_of(jwks, clock)
    cache.key_for("k2")
    jwks.publish(key2)
    clock.advance(301)

    assert cache.key_for("k2") is not None  # this call renews the keys
    with pytest.raises(KeyUnknown):
        cache.key_for("k1")


def test_the_keys_are_fetched_again_after_five_minutes_not_before(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")

    clock.advance(299)
    cache.key_for("k1")
    assert jwks.fetches == 1

    clock.advance(1)
    cache.key_for("k1")
    assert jwks.fetches == 2


# --- criterion 5: held keys keep working while the fetch fails -----------------------------------------------------------


def test_held_keys_keep_working_while_the_fetch_fails(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")
    jwks.fails = True

    for _ in range(5):
        clock.advance(301)
        assert cache.key_for("k1") is not None

    assert jwks.fetches == 6  # one each time the keys were old: a failed fetch is retried, not given up on


def test_a_failed_renewal_is_retried_no_sooner_than_ten_seconds_later(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")
    jwks.fails = True
    clock.advance(301)

    for _ in range(30):
        assert cache.key_for("k1") is not None
    assert jwks.fetches == 2

    clock.advance(10)
    assert cache.key_for("k1") is not None
    assert jwks.fetches == 3


# --- criterion 5: the times are taken after the fetch returns -------------------------------------------------------------


def test_the_five_minutes_and_the_ten_seconds_count_from_the_end_of_a_fetch(clock, key1):  # criterion 5
    fetches = []

    def slow_fetch(url, timeout):  # a fetch that takes 8 seconds
        fetches.append(clock.now)
        clock.advance(8)
        return {"keys": [key1.jwk]}

    cache = JwksCache(JWKS_URL, clock=clock, fetch=slow_fetch)
    cache.key_for("k1")

    clock.advance(299)  # 307 seconds after the fetch began, 299 after it ended
    cache.key_for("k1")
    assert len(fetches) == 1

    clock.advance(1)
    cache.key_for("k1")
    assert len(fetches) == 2

    clock.advance(1)  # 1 second after the end of the second fetch: too soon for a new key
    with pytest.raises(KeyUnknown):
        cache.key_for("k2")
    assert len(fetches) == 2
    clock.advance(9)
    with pytest.raises(KeyUnknown):
        cache.key_for("k2")
    assert len(fetches) == 3


def test_a_failed_fetch_counts_from_its_end_too(clock):  # criterion 5
    fetches = []

    def failing_fetch(url, timeout):
        fetches.append(clock.now)
        clock.advance(8)
        raise OSError("down")

    cache = JwksCache(JWKS_URL, clock=clock, fetch=failing_fetch)
    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")
    clock.advance(9)
    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")
    assert len(fetches) == 1

    clock.advance(1)
    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")
    assert len(fetches) == 2


# --- held keys are kept while the fetch fails, for 24 hours after the last fetch that worked (Decision 10) ---------------


def test_the_longest_a_held_key_outlives_the_last_good_fetch_is_24_hours():
    assert _jwks.MAX_STALE_SECONDS == 24 * 3600


def test_a_held_key_works_until_24_hours_after_the_last_good_fetch_while_the_fetch_fails(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")
    jwks.fails = True

    clock.advance(24 * 3600 - 1)
    assert cache.key_for("k1") is not None

    clock.advance(2)  # 24 hours and a second after the last good fetch
    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")


def test_the_24_hours_count_from_the_last_fetch_that_worked_not_from_the_last_try(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")
    jwks.fails = True
    for _ in range(95):  # failed tries all day: every 15 minutes, 23 hours and 45 minutes in all
        clock.advance(900)
        assert cache.key_for("k1") is not None

    clock.advance(900 + 1)  # the last try was a second ago, but the last good fetch is over 24 hours ago

    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")


def test_a_fetch_that_works_again_restores_the_keys_after_the_limit(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")
    jwks.fails = True
    clock.advance(24 * 3600 + 1)
    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")

    jwks.fails = False
    clock.advance(10)

    assert cache.key_for("k1") is not None
    assert cache.key_for("k1") is not None
    clock.advance(299)
    assert cache.key_for("k1") is not None  # and the keys are fresh again for 5 minutes


# --- criterion 5: an unknown key is unavailable when the latest fetch failed, unknown when it worked ----------------------


def test_no_key_and_a_failing_key_set_is_unavailable(jwks, clock):  # criterion 5
    jwks.fails = True

    with pytest.raises(KeysUnavailable):
        cache_of(jwks, clock).key_for("k1")


def test_unavailable_stays_the_answer_for_ten_seconds_without_another_fetch(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    jwks.fails = True
    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")
    jwks.fails = False

    clock.advance(9)
    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")
    assert jwks.fetches == 1

    clock.advance(1)
    assert cache.key_for("k1") is not None
    assert jwks.fetches == 2


def test_the_answer_to_an_unknown_kid_names_the_state_of_the_latest_fetch(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    with pytest.raises(KeyUnknown):
        cache.key_for("nope")

    jwks.fails = True
    clock.advance(10)
    with pytest.raises(KeysUnavailable):
        cache.key_for("nope")

    jwks.fails = False
    clock.advance(10)
    with pytest.raises(KeyUnknown):
        cache.key_for("nope")


def test_unavailable_does_not_need_the_failed_fetch_to_have_run_for_the_request(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    jwks.fails = True
    with pytest.raises(KeysUnavailable):
        cache.key_for("first")

    clock.advance(5)
    with pytest.raises(KeysUnavailable):
        cache.key_for("second")
    assert jwks.fetches == 1


def test_a_key_set_with_nothing_usable_counts_as_a_failed_fetch(clock, key1):  # criterion 5
    hmac_only = {"keys": [{"kty": "oct", "kid": "k9", "k": "AAAA"}]}
    served = [{"keys": [key1.jwk]}, {"keys": []}, "not a key set", hmac_only]
    cache = JwksCache(JWKS_URL, clock=clock, fetch=lambda url, timeout: served.pop(0))
    assert cache.key_for("k1") is not None

    for _ in range(3):
        clock.advance(301)
        assert cache.key_for("k1") is not None  # a broken key set leaves the held keys as they were
        clock.advance(10)
        with pytest.raises(KeysUnavailable):
            cache.key_for("unknown")
    assert served == []


def test_a_key_without_a_kid_is_ignored(clock, key1):  # criterion 3
    keyless = {k: v for k, v in key1.jwk.items() if k != "kid"}
    cache = JwksCache(JWKS_URL, clock=clock, fetch=lambda url, timeout: {"keys": [keyless, key1.jwk]})

    assert cache.key_for("k1") is not None
    clock.advance(10)
    with pytest.raises(KeyUnknown):
        cache.key_for("")  # the key that has no kid cannot be asked for by one

    only_keyless = JwksCache(JWKS_URL, clock=clock, fetch=lambda url, timeout: {"keys": [keyless]})
    with pytest.raises(KeysUnavailable):  # a key set with no usable key is a failed fetch
        only_keyless.key_for("k1")


def test_only_rsa_keys_are_taken_from_the_key_set(clock, key1):  # criterion 3: algorithm confusion
    mixed = {"keys": [{"kty": "oct", "kid": "k9", "k": "AAAA"}, key1.jwk]}
    cache = JwksCache(JWKS_URL, clock=clock, fetch=lambda url, timeout: mixed)

    assert cache.key_for("k1") is not None
    clock.advance(10)
    with pytest.raises(KeyUnknown):
        cache.key_for("k9")


# --- one fetch at a time: a cold cache waits for the first one, a warm cache never waits ----------------------------------


def run_in_threads(count, ask):
    threads = [threading.Thread(target=ask) for _ in range(count)]
    for thread in threads:
        thread.start()
    return threads


def test_requests_that_arrive_during_the_first_fetch_wait_for_it_and_share_it(key1):  # criterion 5, Decision 7
    """The cache has never had a key: a request that arrives while the first fetch runs is not turned away."""
    slow = SlowJwks(key1)
    cache = JwksCache(JWKS_URL, fetch=slow)
    results = []
    threads = run_in_threads(8, lambda: results.append(cache.key_for("k1") is not None))
    assert slow.started.wait(10)
    threading.Timer(0.3, slow.release.set).start()  # the first fetch takes 0.3 s

    for thread in threads:
        thread.join(10)

    assert results == [True] * 8
    assert slow.fetches == 1


def test_a_request_that_waits_for_the_first_fetch_gives_up_when_the_fetch_may_not_take_longer(key1, clock):  # criterion 5
    slow = SlowJwks(key1)
    cache = JwksCache(JWKS_URL, clock=clock, fetch=slow, timeout=0.3)
    fetching = threading.Thread(target=lambda: cache.key_for("k1"))
    fetching.start()
    assert slow.started.wait(10)
    safety = threading.Timer(3, slow.release.set)  # frees the fetch should the waiter wait without bound
    safety.start()

    started = time.perf_counter()
    try:
        with pytest.raises(KeysUnavailable):
            cache.key_for("k1")
        waited = time.perf_counter() - started
    finally:
        safety.cancel()
        slow.release.set()
        fetching.join(10)

    assert 0.2 < waited < 2.5  # it waited for the fetch's own limit, no longer
    assert slow.fetches == 1


def test_no_request_of_a_product_that_has_just_started_is_turned_away_while_the_first_fetch_ends(key1):  # criterion 5
    """Requests keep arriving while the first fetch runs and while those that waited for it are let through. A request with a
    key that is in the key set is answered with the key, one with a key that is not is a 401: nobody gets a 503 (nothing failed)."""
    for _ in range(30):
        fetches = []

        def fetch(url, timeout):
            fetches.append(1)
            time.sleep(0.02)
            return {"keys": [key1.jwk]}

        cache = JwksCache(JWKS_URL, fetch=fetch)
        start = threading.Barrier(201)
        answers = []

        def ask(kid, delay):
            start.wait(10)
            time.sleep(delay)  # they arrive over the 40 ms around the end of the 20 ms fetch, not all at once
            try:
                cache.key_for(kid)
                answers.append((kid, "found"))
            except KeyUnknown:
                answers.append((kid, "unknown"))
            except KeysUnavailable:
                answers.append((kid, "unavailable"))

        threads = [threading.Thread(target=ask, args=("k1" if number % 2 else "stray", number * 0.0002)) for number in range(200)]
        for thread in threads:
            thread.start()
        start.wait(10)
        for thread in threads:
            thread.join(10)

        assert sorted(set(answers)) == [("k1", "found"), ("stray", "unknown")]
        assert len(fetches) == 1


def warm(slow, clock, key1):
    """A cache that has had a good fetch, 10 seconds ago, with a fetch that is now held open by `slow`."""
    slow.release.set()
    cache = JwksCache(JWKS_URL, clock=clock, fetch=slow)
    assert cache.key_for("k1") is not None
    slow.release.clear()
    slow.started.clear()
    clock.advance(10)
    return cache


def test_requests_with_an_unknown_kid_do_not_wait_for_a_fetch_that_is_running_and_share_it(key1, clock):  # criterion 5
    slow = SlowJwks(key1)
    cache = warm(slow, clock, key1)
    results = []

    def ask():
        try:
            cache.key_for("unknown")
            results.append("found")
        except KeyUnknown:
            results.append("unknown")
        except KeysUnavailable:
            results.append("unavailable")

    threads = run_in_threads(8, ask)
    assert slow.started.wait(10)
    time.sleep(0.2)  # the other seven have asked by now, and have been answered
    assert results == ["unavailable"] * 7
    slow.release.set()
    for thread in threads:
        thread.join(10)

    assert sorted(results) == ["unavailable"] * 7 + ["unknown"]
    assert slow.fetches == 2  # the first fetch of the warm-up, and this one


def test_a_request_that_holds_its_key_does_not_wait_for_a_fetch_that_is_running(key1, clock):
    slow = SlowJwks(key1)
    slow.release.set()
    cache = JwksCache(JWKS_URL, clock=clock, fetch=slow)
    assert cache.key_for("k1") is not None
    slow.release.clear()
    slow.started.clear()
    clock.advance(301)
    renewing = threading.Thread(target=lambda: cache.key_for("k1"))
    renewing.start()
    assert slow.started.wait(10)

    started = time.perf_counter()
    assert cache.key_for("k1") is not None
    waited = time.perf_counter() - started

    slow.release.set()
    renewing.join(10)
    assert waited < 1  # the fetch it did not wait for is held open until the test lets go
    assert slow.fetches == 2


def test_a_request_that_lacks_its_key_is_unavailable_at_once_while_a_fetch_runs(key1, clock):  # criterion 5
    slow = SlowJwks(key1)
    cache = warm(slow, clock, key1)
    fetching = threading.Thread(target=lambda: pytest.raises(KeyUnknown, cache.key_for, "unknown"))
    fetching.start()
    assert slow.started.wait(10)

    try:
        started = time.perf_counter()
        with pytest.raises(KeysUnavailable):
            cache.key_for("another-unknown")
        waited = time.perf_counter() - started
        assert waited < 1  # it did not wait for the fetch, which is held open until the test lets go
    finally:
        slow.release.set()
        fetching.join(10)

    assert slow.fetches == 2
    assert cache.key_for("k1") is not None


def test_after_the_first_fetch_has_ended_a_cold_cache_is_unavailable_at_once_while_a_fetch_runs(key1, clock):  # Ruling V7
    """No fetch has ever worked, and the first one failed: the fetch that retries must not make the others wait again."""
    slow = SlowJwks(key1)
    slow.fails = True
    cache = JwksCache(JWKS_URL, clock=clock, fetch=slow, timeout=3)
    slow.release.set()
    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")  # the first fetch has ended, and failed
    slow.release.clear()
    slow.started.clear()
    clock.advance(10)
    outcomes = []

    def retry():
        try:
            cache.key_for("k1")
        except KeysUnavailable:
            outcomes.append("unavailable")

    retrying = threading.Thread(target=retry)
    retrying.start()
    assert slow.started.wait(10)

    try:
        started = time.perf_counter()
        with pytest.raises(KeysUnavailable):
            cache.key_for("k1")
        waited = time.perf_counter() - started
    finally:
        slow.release.set()
        retrying.join(10)

    assert waited < 1  # the retry is held open until the test lets go, and the 3 s of waiting are not spent
    assert outcomes == ["unavailable"]
    assert slow.fetches == 2


def test_the_requests_that_wait_for_the_first_fetch_are_woken_when_it_ends(key1):  # criterion 5, Decision 7
    """Not when each one's own limit runs out: the 4 s limit of the waiters is far longer than the 0.3 s fetch."""
    slow = SlowJwks(key1)
    cache = JwksCache(JWKS_URL, fetch=slow, timeout=4)
    results = []
    threads = run_in_threads(8, lambda: results.append(cache.key_for("k1") is not None))
    assert slow.started.wait(10)
    time.sleep(0.2)  # the seven others are waiting by now

    slow.release.set()
    released = time.perf_counter()
    for thread in threads:
        thread.join(10)

    assert time.perf_counter() - released < 1.5
    assert results == [True] * 8


def test_a_clock_that_raises_after_a_fetch_does_not_leave_the_cache_waiting_for_a_fetch_that_has_ended(key1):
    """The flag that says "a fetch is running" is cleared whatever happens, so that no request is ever turned away for it."""
    clock_fails = []
    fetches = []
    now = [1000.0]

    def clock():
        if clock_fails:
            raise OSError("the clock does not answer")
        return now[0]

    def fetch(url, timeout):
        fetches.append(1)
        if len(fetches) == 1:
            clock_fails.append(True)  # the clock breaks while the first fetch runs, and is read when it returns
        return {"keys": [key1.jwk]}

    cache = JwksCache(JWKS_URL, clock=clock, fetch=fetch, timeout=0.5)
    with pytest.raises(OSError):
        cache.key_for("k1")
    clock_fails.clear()
    now[0] += 11  # that attempt counted: the next one is not made before `min_interval` has passed

    started = time.perf_counter()
    assert cache.key_for("k1") is not None  # a fetch of its own, not a wait for the one that ended

    assert time.perf_counter() - started < 0.4
    assert len(fetches) == 2


def test_an_attempt_whose_end_could_not_be_timed_still_counts_as_an_attempt(key1):  # R3-1
    """The clock fails once, when the end of the first fetch is read. The attempt is counted from the clock reading made just
    before it began, so `min_interval` holds and the waiters that its end wakes are answered, not sent on to a second attempt."""
    clock_fails = []
    fetches = []
    started, release = threading.Event(), threading.Event()

    def clock():
        if clock_fails:
            clock_fails.pop()
            raise OSError("the clock does not answer")
        return 1000.0

    def fetch(url, timeout):
        fetches.append(1)
        started.set()
        assert release.wait(10)
        clock_fails.append(True)  # read by the fetching request when the fetch returns, by nobody before it
        return {"keys": [key1.jwk]}

    cache = JwksCache(JWKS_URL, clock=clock, fetch=fetch, timeout=4)
    outcomes = []

    def ask():
        try:
            cache.key_for("k1")
            outcomes.append("key")
        except KeysUnavailable:
            outcomes.append("unavailable")
        except OSError:
            outcomes.append("clock")

    threads = run_in_threads(5, ask)
    assert started.wait(10)
    time.sleep(0.2)  # the four others are waiting by now
    release.set()
    for thread in threads:
        thread.join(10)

    assert sorted(outcomes) == ["clock"] + ["unavailable"] * 4
    assert len(fetches) == 1


def test_an_attempt_that_ends_on_something_that_is_not_an_exception_still_counts_as_an_attempt():  # R3-1
    """A `SystemExit` leaves the fetch (a timeout of an async framework does the like): the clock was never read for its end,
    and the waiters that it wakes must be answered, not sent on to a second attempt that they would wait for again."""
    fetches = []
    started, release = threading.Event(), threading.Event()

    def fetch(url, timeout):
        fetches.append(1)
        started.set()
        assert release.wait(10)
        raise SystemExit

    cache = JwksCache(JWKS_URL, fetch=fetch, timeout=4)
    outcomes = []

    def ask():
        try:
            cache.key_for("k1")
            outcomes.append("key")
        except KeysUnavailable:
            outcomes.append("unavailable")
        except SystemExit:
            outcomes.append("exit")

    threads = run_in_threads(5, ask)
    assert started.wait(10)
    time.sleep(0.2)
    release.set()
    for thread in threads:
        thread.join(10)

    assert sorted(outcomes) == ["exit"] + ["unavailable"] * 4
    assert len(fetches) == 1
    with pytest.raises(KeysUnavailable):  # and the next request, inside `min_interval`, makes no attempt either
        cache.key_for("k1")
    assert len(fetches) == 1


def test_a_worker_that_ends_on_something_that_is_not_an_exception_is_a_failed_fetch(monkeypatch):
    def leave(*args):
        raise SystemExit  # not an `Exception`: nothing of the worker's `except` takes it

    monkeypatch.setattr(_jwks, "_download", leave)
    monkeypatch.setattr(threading, "excepthook", lambda args: None)  # the worker's end is expected: no warning for it

    started = time.perf_counter()
    with pytest.raises(RuntimeError, match="without an answer"):
        _jwks.fetch_jwks("http://auth.test/jwks", 5)

    assert time.perf_counter() - started < 2  # the caller was told at once, it did not wait for the deadline


def test_a_key_older_than_24_hours_is_not_served_while_a_fetch_runs(key1, clock):  # Decision 10
    slow = SlowJwks(key1)
    cache = warm(slow, clock, key1)
    clock.advance(24 * 3600)  # 24 hours and 10 seconds after the last good fetch
    outcomes = []

    def renew():
        try:
            outcomes.append(cache.key_for("k1") is not None)
        except KeysUnavailable:
            outcomes.append("unavailable")

    renewing = threading.Thread(target=renew)
    renewing.start()
    assert slow.started.wait(10)

    try:
        with pytest.raises(KeysUnavailable):
            cache.key_for("k1")  # the key is still in memory, and a fetch is running: it is not used
    finally:
        slow.release.set()
        renewing.join(10)

    assert outcomes == [True]
    assert cache.key_for("k1") is not None  # and the fetch that worked restored it


# --- the real fetch, against a server on 127.0.0.1 ------------------------------------------------------------------------


@pytest.fixture
def server(key1):
    server = JwksServer({"keys": [key1.jwk]})
    yield server
    server.close()


def test_the_keys_are_fetched_over_http(server):  # criterion 5
    cache = JwksCache(server.url)

    assert cache.key_for("k1") is not None
    assert cache.key_for("k1") is not None
    assert server.requests == 1


@pytest.mark.parametrize("status", [404, 429, 500, 503])
def test_an_error_status_is_a_failed_fetch(server, status):  # criterion 5
    server.status = status

    with pytest.raises(KeysUnavailable):
        JwksCache(server.url).key_for("k1")


def test_an_answer_that_is_not_a_key_set_is_a_failed_fetch(server):  # criterion 5
    server.document = {"keys": "no"}

    with pytest.raises(KeysUnavailable):
        JwksCache(server.url).key_for("k1")


def padded(server, size: int) -> int:
    """Make the key set a valid JSON document of about `size` bytes (the keys stay usable). Returns its length."""
    server.document = {"keys": server.document["keys"], "pad": "x" * size}
    return len(json.dumps(server.document))


def test_a_key_set_just_under_the_size_limit_is_fetched(server):  # criterion 5
    length = padded(server, _jwks.MAX_BYTES - 1000)
    assert length <= _jwks.MAX_BYTES

    assert JwksCache(server.url).key_for("k1") is not None


def test_a_valid_key_set_over_the_size_limit_is_refused_for_its_size(server):  # criterion 5
    length = padded(server, _jwks.MAX_BYTES)
    assert length > _jwks.MAX_BYTES  # valid JSON with a usable key: nothing but its size is wrong with it

    with pytest.raises(ValueError, match="too large"):
        _jwks.fetch_jwks(server.url, 5)
    with pytest.raises(KeysUnavailable):
        JwksCache(server.url).key_for("k1")


def test_nobody_listening_is_a_failed_fetch():  # criterion 5
    with pytest.raises(KeysUnavailable):
        JwksCache(closed_port_url()).key_for("k1")


def test_the_fetch_gives_up_at_its_timeout(server):  # criterion 5
    server.delay = 4
    cache = JwksCache(server.url, timeout=0.5)

    started = time.perf_counter()
    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")

    assert time.perf_counter() - started < 3.5  # it gave up at the timeout, not when the server answered


def test_a_server_that_trickles_the_answer_is_given_up_on_at_the_deadline_of_the_whole_fetch(server):  # criterion 5
    server.trickle = 0.1  # a byte every 0.1 s: no single wait is long, the whole transfer takes minutes
    started = time.perf_counter()

    with pytest.raises(TimeoutError):
        _jwks.fetch_jwks(server.url, 1.0)

    assert 0.9 < time.perf_counter() - started < 5.0  # the whole transfer would take minutes


def test_the_deadline_reaches_the_cache_as_an_unavailable_key_set(server):  # criterion 5
    server.trickle = 0.1
    cache = JwksCache(server.url, timeout=0.5)
    started = time.perf_counter()

    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")

    assert time.perf_counter() - started < 4.0  # the whole transfer would take minutes


# --- how the key set is fetched: no proxy, no redirect, http or https only ------------------------------------------------


@pytest.mark.parametrize("variable", ["HTTP_PROXY", "http_proxy"])
def test_the_fetch_ignores_proxy_environment_variables(server, monkeypatch, variable):
    """The variable is set after the module is imported: the rule is made where each fetch is made, so this guards it."""
    for name in ("HTTP_PROXY", "http_proxy", "NO_PROXY", "no_proxy"):
        monkeypatch.delenv(name, raising=False)
    monkeypatch.setenv(variable, "http://127.0.0.1:9")  # a proxy nobody listens at

    assert JwksCache(server.url).key_for("k1") is not None
    assert server.requests == 1


def test_a_redirect_is_a_failed_fetch_and_is_not_followed(server, key1):
    with JwksServer({"keys": [key1.jwk]}) as elsewhere:
        server.redirect_to = elsewhere.url

        with pytest.raises(KeysUnavailable):
            JwksCache(server.url).key_for("k1")

        assert server.requests == 1
        assert elsewhere.requests == 0


@pytest.mark.parametrize("url", ["ftp://auth.test/jwks", "file:///etc/jwks.json", "data:,{}", "auth.test/jwks", ""])
def test_a_key_set_url_that_is_not_http_or_https_is_refused(url):
    with pytest.raises(ValueError, match="http or https"):
        JwksCache(url)


@pytest.mark.parametrize("url", ["http://auth.test/jwks", "https://auth.test/jwks"])
def test_http_and_https_key_set_urls_are_accepted(url):
    JwksCache(url)

@pytest.mark.parametrize("min_interval", [0, 0.0, -1, float("nan")])
def test_a_min_interval_that_is_not_positive_is_refused(min_interval):
    """Without a pause between fetches, a woken waiter could find that another request has already started the next one."""
    with pytest.raises(ValueError, match="min_interval"):
        JwksCache("http://auth.test/jwks", min_interval=min_interval)


def test_a_small_positive_min_interval_is_accepted():
    JwksCache("http://auth.test/jwks", min_interval=0.001)
