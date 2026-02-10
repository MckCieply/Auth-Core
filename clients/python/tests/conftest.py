import pytest

from helpers import Clock, FakeJwks, Signer


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
