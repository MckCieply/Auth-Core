"""Auth-Core access tokens in a FastAPI backend.

    auth = AuthCore(issuer=..., audience=..., jwks_url=...)
    auth.install(app)

    @app.post("/api/notes")
    def add(user: Principal = Depends(auth.require_permission("notes:write"))): ...
"""

from ._core import AuthCore
from ._errors import AuthError
from ._jwks import JwksCache
from ._principal import Principal

__version__ = "0.1.1"

__all__ = ["AuthCore", "AuthError", "JwksCache", "Principal", "__version__"]
