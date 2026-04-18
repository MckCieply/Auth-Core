# auth-core-fastapi

Verify the access tokens of [Auth-Core](https://github.com/MckCieply/Auth-Core), a self-hosted authentication service, in a FastAPI
backend: two dependencies, `current_user` and `require_permission`, over a small cache of the service's public keys. The package checks
the token offline (RS256, issuer, audience, expiry, the claims `sub`, `org_id`, `roles`, `permissions` and, when present, `org_kind`); it never calls Auth-Core per
request.

```python
from fastapi import Depends, FastAPI
from auth_core_fastapi import AuthCore, Principal

app = FastAPI()
auth = AuthCore(issuer="https://app.example.com/auth", audience="my-product-api")
auth.install(app)


@app.post("/api/notes")
def add(user: Principal = Depends(auth.require_permission("notes:write"))):
    return {"company": user.org_id}   # every query is filtered by user.org_id
```

`Principal.org_kind` is `"personal"` for a private account (Auth-Core spec 0010) and `"organization"` for a company member, also for a token without the claim. It is in the package released with `v0.2.0`; `python-v0.1.1` has no `org_kind`.

Install it from the repository at a tag (it is not on PyPI):

```
auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v0.1.1#subdirectory=clients/python
```

Needs Python 3.12, FastAPI 0.142 or newer and `PyJWT[crypto]` 2.15 or newer. The step-by-step guide, with the answers of the package
(`401`, `403 forbidden`, `503 auth_unavailable`), the key rules and a sample product, is
[`docs/integration/python-fastapi.md`](https://github.com/MckCieply/Auth-Core/blob/main/docs/integration/python-fastapi.md).

Licensed under the MIT licence.
