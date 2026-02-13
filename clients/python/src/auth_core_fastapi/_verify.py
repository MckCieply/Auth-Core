"""The rules of "a token is valid", in one place."""

import re

import jwt

from ._jwks import JwksCache
from ._principal import Principal

LEEWAY_SECONDS = 300  # clock skew, as Auth-Core itself allows when it validates its own tokens
ALGORITHM = "RS256"
TOKEN_TYPE = "at+jwt"

_BEARER = re.compile(r"bearer ([A-Za-z0-9\-._~+/]+=*)", re.ASCII | re.IGNORECASE)


class TokenRejected(Exception):
    """The request carries no valid token. `reason` is a fixed word or an exception class name, never a value."""

    def __init__(self, reason: str) -> None:
        super().__init__(reason)
        self.reason = reason


def bearer_token(authorization_headers: list[str]) -> str:
    """The token of the request's one `Authorization: Bearer <token>` header."""
    if not authorization_headers:
        raise TokenRejected("no_authorization_header")
    if len(authorization_headers) > 1:
        raise TokenRejected("several_authorization_headers")
    match = _BEARER.fullmatch(authorization_headers[0])
    if match is None:
        raise TokenRejected("not_a_bearer_header")
    return match.group(1)


def verify_token(token: str, keys: JwksCache, issuer: str, audience: str) -> Principal:
    """The caller named by `token`.

    Raises `TokenRejected` for an invalid token, and lets `KeyUnknown` and `KeysUnavailable` of the key cache through.
    The key is chosen by `kid` among the keys of the configured key set, and nothing else in the token
    (`jku`, `jwk`, `x5u`) is looked at.
    """
    try:
        header = jwt.get_unverified_header(token)
    except jwt.PyJWTError:
        raise TokenRejected("not_a_jws") from None
    if header.get("alg") != ALGORITHM:
        raise TokenRejected("algorithm")
    if header.get("typ") != TOKEN_TYPE:
        raise TokenRejected("type")
    kid = header.get("kid")
    if not isinstance(kid, str) or not kid:
        raise TokenRejected("no_kid")

    key = keys.key_for(kid)
    try:
        claims = jwt.decode(
            token,
            key,
            algorithms=[ALGORITHM],
            issuer=issuer,
            audience=audience,
            leeway=LEEWAY_SECONDS,
            options={"require": ["exp", "iat", "iss", "aud", "sub"]},
        )
    except jwt.PyJWTError as exc:
        raise TokenRejected(type(exc).__name__) from None
    _check_shape(claims, audience)
    return _principal(claims)


def _check_shape(claims: dict, audience: str) -> None:
    """What PyJWT lets through and the contract does not: `aud` is the configured string, not a list holding it, and
    the time claims are JSON numbers, not numeric strings or booleans."""
    if claims.get("aud") != audience or not isinstance(claims.get("aud"), str):
        raise TokenRejected("audience")
    for name in ("exp", "iat", "nbf"):
        if name in claims and (isinstance(claims[name], bool) or not isinstance(claims[name], (int, float))):
            raise TokenRejected("time_claim")


def _principal(claims: dict) -> Principal:
    sub, org_id = claims.get("sub"), claims.get("org_id")
    roles, permissions = claims.get("roles"), claims.get("permissions")
    if not (isinstance(sub, str) and sub and isinstance(org_id, str) and org_id):
        raise TokenRejected("claims")
    if not (_is_list_of_text(roles) and _is_list_of_text(permissions)):
        raise TokenRejected("claims")
    return Principal(sub=sub, org_id=org_id, roles=tuple(roles), permissions=frozenset(permissions))


def _is_list_of_text(value: object) -> bool:
    return isinstance(value, list) and all(isinstance(item, str) for item in value)
