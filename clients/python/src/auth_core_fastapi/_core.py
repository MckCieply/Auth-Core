import logging
from collections.abc import Callable
from urllib.parse import urlsplit

from fastapi import Depends, FastAPI, Request

from ._errors import AuthError, handle_auth_error
from ._jwks import JwksCache, KeysUnavailable, KeyUnknown
from ._principal import Principal
from ._verify import TokenRejected, bearer_token, verify_token

log = logging.getLogger(__name__)


class AuthCore:
    """One per product, created at startup. Creating it makes no network call.

    `issuer` must equal the `iss` of the tokens, `audience` their `aud`. `jwks_url` defaults to
    `issuer + "/.well-known/jwks.json"`; a backend that reaches Auth-Core over an internal network sets it.
    Use HTTPS or an address on a network you trust: whoever controls that address controls who is let in.

    `jwks_cache` replaces the key cache: a test hands in `JwksCache(url, fetch=...)` that serves keys made in the test.
    """

    def __init__(
        self,
        issuer: str,
        audience: str,
        jwks_url: str | None = None,
        *,
        jwks_cache: JwksCache | None = None,
    ) -> None:
        if not issuer or not audience:
            raise ValueError("issuer and audience must not be empty")
        url = jwks_url or issuer.rstrip("/") + "/.well-known/jwks.json"
        if urlsplit(url).scheme not in ("http", "https"):
            raise ValueError("jwks_url must be an http or https URL")
        self._issuer = issuer
        self._audience = audience
        self._keys = jwks_cache if jwks_cache is not None else JwksCache(url)

    def install(self, app: FastAPI) -> None:
        """Make the 401, 403 and 503 of the dependencies the responses of the contract."""
        app.add_exception_handler(AuthError, handle_auth_error)

    def current_user(self, request: Request) -> Principal:
        """FastAPI dependency: the caller, or 401, or 503 when the key cannot be had.

        A plain `def`, so FastAPI runs it in its thread pool: fetching the keys never blocks the event loop.
        """
        try:
            token = bearer_token(request.headers.getlist("authorization"))
            return verify_token(token, self._keys, self._issuer, self._audience)
        except TokenRejected as rejected:
            log.debug("token rejected: %s", rejected.reason)
            raise AuthError(401) from None
        except KeyUnknown:
            log.debug("token rejected: the key is not in the key set")
            raise AuthError(401) from None
        except KeysUnavailable:
            log.debug("token not checked: the key is not held and the key set cannot be fetched")
            raise AuthError(503, "auth_unavailable") from None

    def require_permission(self, permission: str) -> Callable[..., Principal]:
        """FastAPI dependency: the caller, if its token holds `permission`; else 401 or 403.

        Reads the token only; it never calls Auth-Core.
        """
        if not permission:
            raise ValueError("permission must not be empty")

        async def dependency(user: Principal = Depends(self.current_user)) -> Principal:
            if permission not in user.permissions:
                log.debug("permission refused")
                raise AuthError(403, "forbidden")
            return user

        return dependency
