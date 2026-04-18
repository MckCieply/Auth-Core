from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class Principal:
    """The caller of a request, as its access token says: who, in which space, with what rights.

    `org_kind` is "organization" for a member of a company and "personal" for a private account, whose space nobody else can join
    (Auth-Core spec 0010). A token of an Auth-Core before that has no such claim, and is a company member's.
    """

    sub: str
    org_id: str
    roles: tuple[str, ...]
    permissions: frozenset[str]
    org_kind: str = "organization"
