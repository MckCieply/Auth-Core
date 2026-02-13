import os
from collections.abc import Mapping
from dataclasses import dataclass, field


@dataclass(frozen=True)
class Settings:
    """What the service is told by its environment."""

    auth_issuer: str  # the `iss` of the tokens, e.g. http://localhost:8088/auth
    auth_audience: str  # their `aud`
    auth_jwks_url: str | None  # where the keys are; unset means issuer + /.well-known/jwks.json
    database_url: str = field(repr=False)  # it holds the password: whatever prints the settings must not print it

    @classmethod
    def from_env(cls, env: Mapping[str, str] = os.environ) -> "Settings":
        def required(name: str) -> str:
            value = env.get(name, "").strip()
            if not value:
                raise RuntimeError(f"the environment variable {name} must be set")
            return value

        return cls(
            auth_issuer=required("AUTH_ISSUER"),
            auth_audience=required("AUTH_AUDIENCE"),
            auth_jwks_url=env.get("AUTH_JWKS_URL", "").strip() or None,
            database_url=required("DATABASE_URL"),
        )
