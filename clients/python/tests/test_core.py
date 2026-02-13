"""What `AuthCore` is given: the key set URL, and that creating it touches no network (criterion 6)."""

import pytest

from auth_core_fastapi import AuthCore
from helpers import AUDIENCE, ISSUER


def test_creating_the_object_makes_no_network_call(monkeypatch):  # criterion 6
    def no_network(*args, **kwargs):
        raise AssertionError("the network was used")

    monkeypatch.setattr("urllib.request.urlopen", no_network)
    monkeypatch.setattr("socket.socket.connect", no_network)
    monkeypatch.setattr("socket.getaddrinfo", no_network)

    AuthCore(ISSUER, AUDIENCE)
    AuthCore(ISSUER, AUDIENCE, "http://auth:8080/auth/.well-known/jwks.json")


def test_the_default_key_set_url_is_the_issuer_plus_the_well_known_path():
    assert AuthCore("https://app.example.com/auth", "x")._keys._url == "https://app.example.com/auth/.well-known/jwks.json"
    assert AuthCore("https://app.example.com/auth/", "x")._keys._url == "https://app.example.com/auth/.well-known/jwks.json"
    assert AuthCore("https://app.example.com/auth", "x", "http://auth:8080/k")._keys._url == "http://auth:8080/k"


@pytest.mark.parametrize("url", ["file:///etc/passwd", "ftp://example.com/jwks", "jwks.json"])
def test_a_key_set_url_that_is_not_http_is_refused(url):
    with pytest.raises(ValueError):
        AuthCore(ISSUER, AUDIENCE, url)


@pytest.mark.parametrize("issuer, audience", [("", "notes-api"), (ISSUER, "")])
def test_an_empty_issuer_or_audience_is_refused(issuer, audience):
    with pytest.raises(ValueError):
        AuthCore(issuer, audience)


def test_an_empty_permission_is_refused():
    with pytest.raises(ValueError):
        AuthCore(ISSUER, AUDIENCE).require_permission("")
