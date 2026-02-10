from fastapi import HTTPException, Request, Response
from fastapi.responses import JSONResponse


class AuthError(HTTPException):
    """What the dependencies raise: 401, 403 or 503.

    `AuthCore.install(app)` turns it into the responses of the contract (an empty 401, or `{"error": ...}`).
    Without `install` the status code and the headers are still right, and FastAPI's own body is sent.
    """

    def __init__(self, status_code: int, error: str | None = None) -> None:
        headers = {"Cache-Control": "no-store"}
        if status_code == 401:
            headers["WWW-Authenticate"] = "Bearer"
        super().__init__(status_code=status_code, detail=error, headers=headers)
        self.error = error


def handle_auth_error(request: Request, exc: Exception) -> Response:
    if not isinstance(exc, AuthError):
        raise exc
    if exc.error is None:
        return Response(status_code=exc.status_code, headers=exc.headers)
    return JSONResponse({"error": exc.error}, status_code=exc.status_code, headers=exc.headers)
