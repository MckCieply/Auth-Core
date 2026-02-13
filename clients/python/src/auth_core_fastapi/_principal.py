from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class Principal:
    """The caller of a request, as its access token says: who, in which company, with what rights."""

    sub: str
    org_id: str
    roles: tuple[str, ...]
    permissions: frozenset[str]
