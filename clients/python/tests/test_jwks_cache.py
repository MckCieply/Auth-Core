"""The key cache by itself (criteria 5 and 6): a fake JWKS URL and a clock moved by hand, then a real server on 127.0.0.1."""

import json
import threading
import time

import pytest

from auth_core_fastapi import _jwks
from auth_core_fastapi._jwks import JwksCache, KeysUnavailable, KeyUnknown
from helpers import JWKS_URL, FakeJwks, JwksServer, SlowJwks


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

    for _ in range(20):
        assert cache.key_for("k1") is not None
    assert jwks.fetches == 2

    clock.advance(10)
    assert cache.key_for("k1") is not None
    assert jwks.fetches == 3


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


def test_only_rsa_keys_are_taken_from_the_key_set(clock, key1):  # criterion 3: algorithm confusion
    mixed = {"keys": [{"kty": "oct", "kid": "k9", "k": "AAAA"}, key1.jwk]}
    cache = JwksCache(JWKS_URL, clock=clock, fetch=lambda url, timeout: mixed)

    assert cache.key_for("k1") is not None
    clock.advance(10)
    with pytest.raises(KeyUnknown):
        cache.key_for("k9")


# --- one fetch at a time, and nobody who holds the key waits for it -------------------------------------------------------


def test_requests_that_wait_for_the_keys_share_one_fetch(key1):  # criterion 5
    slow = SlowJwks(key1)
    cache = JwksCache(JWKS_URL, fetch=slow)
    results = []

    def ask():
        results.append(cache.key_for("k1") is not None)

    threads = [threading.Thread(target=ask) for _ in range(8)]
    for thread in threads:
        thread.start()
    assert slow.started.wait(10)
    time.sleep(0.2)  # the others are now waiting at the lock
    slow.release.set()
    for thread in threads:
        thread.join(10)

    assert results == [True] * 8
    assert slow.fetches == 1


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
    assert waited < 1
    assert slow.fetches == 2


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


@pytest.mark.parametrize("status", [404, 500, 503])
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
        JwksCache("http://127.0.0.1:9/jwks").key_for("k1")  # nothing listens on the discard port


def test_the_fetch_gives_up_at_its_timeout(server):  # criterion 5
    server.delay = 3
    cache = JwksCache(server.url, timeout=0.5)

    started = time.perf_counter()
    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")

    assert time.perf_counter() - started < 2.5


def test_a_server_that_trickles_the_answer_is_given_up_on_at_the_deadline_of_the_whole_fetch(server):  # criterion 5
    server.trickle = 0.1  # a byte every 0.1 s: no single wait is long, the whole transfer takes minutes
    started = time.perf_counter()

    with pytest.raises(TimeoutError):
        _jwks.fetch_jwks(server.url, 1.0)

    assert 0.9 < time.perf_counter() - started < 2.0


def test_the_deadline_reaches_the_cache_as_an_unavailable_key_set(server):  # criterion 5
    server.trickle = 0.1
    cache = JwksCache(server.url, timeout=0.5)
    started = time.perf_counter()

    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")

    assert time.perf_counter() - started < 1.5
