"""Criterion 5 through the dependencies: what a request gets while the keys change or cannot be fetched.

The cache rules themselves (10 seconds, 5 minutes, held keys) are pinned in test_jwks_cache.py; this file pins how they
reach the caller: a 401 for a token whose key is not in the key set, a 503 when the key set cannot be had.
"""

from helpers import assert_unauthorized, assert_unavailable, bearer


def test_the_keys_are_fetched_on_first_use_and_kept(client, jwks, key1):  # criterion 5
    assert jwks.fetches == 0

    for _ in range(3):
        assert client.get("/me", headers=bearer(key1.token())).status_code == 200

    assert jwks.fetches == 1


def test_a_token_signed_by_a_new_key_is_accepted_after_one_refetch(client, jwks, clock, key1, key2):  # criterion 5
    assert client.get("/me", headers=bearer(key1.token())).status_code == 200
    jwks.publish(key1, key2)
    clock.advance(10)

    response = client.get("/me", headers=bearer(key2.token()))

    assert response.status_code == 200
    assert jwks.fetches == 2


def test_a_token_of_a_new_key_is_a_401_until_ten_seconds_after_the_last_fetch(client, jwks, clock, key1, key2):  # criterion 5
    assert client.get("/me", headers=bearer(key1.token())).status_code == 200
    jwks.publish(key1, key2)
    clock.advance(9.9)

    assert_unauthorized(client.get("/me", headers=bearer(key2.token())))
    assert jwks.fetches == 1

    clock.advance(0.1)
    assert client.get("/me", headers=bearer(key2.token())).status_code == 200


def test_tokens_with_unknown_kids_cause_at_most_one_fetch_in_ten_seconds(client, jwks, clock, key1):  # criterion 5
    assert client.get("/me", headers=bearer(key1.token())).status_code == 200

    for number in range(20):
        clock.advance(0.4)
        assert_unauthorized(client.get("/me", headers=bearer(key1.token(headers={"kid": f"stray-{number}"}))))

    assert jwks.fetches == 1


def test_held_keys_keep_working_while_the_fetch_fails(client, jwks, clock, key1):  # criterion 5
    assert client.get("/me", headers=bearer(key1.token())).status_code == 200
    jwks.fails = True

    for _ in range(3):
        clock.advance(301)
        assert client.get("/me", headers=bearer(key1.token())).status_code == 200


def test_no_key_and_a_failing_key_set_is_a_503(client, jwks, key1):  # criterion 5
    jwks.fails = True

    assert_unavailable(client.get("/me", headers=bearer(key1.token())))
    assert_unavailable(client.post("/write", headers=bearer(key1.token())))


def test_an_unknown_kid_is_a_503_when_the_latest_fetch_failed_and_a_401_when_it_worked(client, jwks, clock, key1):  # criterion 5
    assert client.get("/me", headers=bearer(key1.token())).status_code == 200
    unknown = key1.token(headers={"kid": "unknown"})

    jwks.fails = True
    clock.advance(10)
    assert_unavailable(client.get("/me", headers=bearer(unknown)))

    jwks.fails = False
    clock.advance(10)
    assert_unauthorized(client.get("/me", headers=bearer(unknown)))


def test_a_token_that_is_invalid_for_another_reason_is_a_401_even_when_the_key_set_is_down(client, jwks, key1):  # criterion 5
    jwks.fails = True

    assert_unauthorized(client.get("/me"))
    assert_unauthorized(client.get("/me", headers=bearer("abc.def.ghi")))
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(headers={"typ": "JWT"}))))
    assert jwks.fetches == 0
