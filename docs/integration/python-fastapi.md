# Connecting a Python (FastAPI) product to Auth-Core

This guide takes a FastAPI backend from "no login" to "each user sees and does only what their company and role allow",
in eight steps. It points at a small working product in this repository, the **notes** sample in
[`samples/notes-api/`](../../samples/notes-api/), and every file it names exists there: when a step is unclear, read the
file.

What you get: your backend trusts Auth-Core's access tokens with a few lines of code, knows the company (`org_id`) and
the permissions of every caller, and never calls Auth-Core to find out. Users log in, refresh and manage their company
through Auth-Core's own API ([spec 0005](../superpowers/specs/0005-tenancy-and-rbac.md)); your product only checks tokens.

Needs: Python 3.12, FastAPI 0.115 or newer, PostgreSQL (for the data steps), Docker with Compose (for the stack).

## 1. Install the package at a tag

The package is `auth-core-fastapi` (import name `auth_core_fastapi`). It is installed from this repository at a tag;
its only dependencies are FastAPI and `PyJWT[crypto]`. In your `requirements.txt` or `pyproject.toml`:

```
auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v0.1.0#subdirectory=clients/python
```

Pin the tag: the package is versioned with the token contract. The sample's
[`requirements.txt`](../../samples/notes-api/requirements.txt) lists the rest of its dependencies and
[`Dockerfile`](../../samples/notes-api/Dockerfile) installs the package from the clone (`clients/python`) instead of a
tag, so that the sample builds from one clone with no GitHub.

## 2. Write your `auth.yaml`

The manifest tells Auth-Core which permissions your product checks and which roles every new company starts with. The
sample's is [`samples/notes-api/auth.yaml`](../../samples/notes-api/auth.yaml):

```yaml
permissions: [notes:read, notes:write]
default_roles:
  admin:  ["*"]
  user:   [notes:read, notes:write]
  viewer: [notes:read]
```

- `permissions` are the strings your code passes to `require_permission`. Name them `resource:action`.
- `members:manage`, `roles:manage` and `org:manage` are built in: they guard Auth-Core's company API, so you do not list
  them, but a role may hold them. `"*"` stands for every permission of the catalog.
- At least one default role must hold `members:manage` or `"*"`, so that the first admin of a company can manage it.
- Auth-Core reads the file at startup (`Auth__Manifest__Path`; the overlay of step 6 mounts it). A broken file never stops
  the service: the last valid manifest stays active and `/auth/health` says `Degraded`.
- The default roles are **copied** when a company is created. Changing the manifest later changes the catalog and the
  default roles of companies created afterwards; a company's own roles stay its own.

## 3. Configure the package

Three values, all from your environment ([`src/notes_api/settings.py`](../../samples/notes-api/src/notes_api/settings.py)):

| Value | Meaning |
| --- | --- |
| `issuer` | Must equal the `iss` of the tokens: Auth-Core's `Auth:Tokens:Issuer`, the origin your users see plus `/auth`, for example `https://app.example.com/auth` |
| `audience` | Must equal the `aud`: Auth-Core's `Auth:Tokens:Audience` |
| `jwks_url` | Optional. Where the signing keys are fetched. Defaults to `issuer` + `/.well-known/jwks.json`. A backend that reaches Auth-Core over an internal network sets it, for example `http://auth:8080/auth/.well-known/jwks.json` |

Create one object when the application starts, and let it answer for the dependencies
([`src/notes_api/app.py`](../../samples/notes-api/src/notes_api/app.py)):

```python
from auth_core_fastapi import AuthCore

auth = AuthCore(issuer=settings.auth_issuer, audience=settings.auth_audience, jwks_url=settings.auth_jwks_url)
auth.install(app)  # makes the 401, 403 and 503 of the dependencies the responses described in step 4
```

Creating the object makes **no network call**: your product starts even while Auth-Core is down. The keys are fetched on
first use, kept in memory, fetched again after 5 minutes, and at once (but at most every 10 seconds) when a token names
a key the package does not know, which is how a key rotation reaches you. If a fetch fails the package keeps the keys it
holds, so tokens signed by known keys keep working while Auth-Core is down.

**Use HTTPS, or an address on a network you trust, for `jwks_url`.** Whoever controls that address controls which tokens
your product accepts.

## 4. Guard your endpoints

Two FastAPI dependencies, each giving a `Principal` (`sub`, `org_id`, `roles` as a tuple, `permissions` as a frozenset):

```python
from fastapi import Depends
from auth_core_fastapi import Principal

# `app` and `auth` are the objects of step 3
@app.get("/api/me")
def me(user: Principal = Depends(auth.current_user)): ...

@app.post("/api/notes")
def add(user: Principal = Depends(auth.require_permission("notes:write"))): ...
```

`require_permission` checks the token's `permissions` and nothing else; it never calls Auth-Core. Declare endpoints that
touch a database or the network as plain `def`: FastAPI then runs them in its thread pool, as it does the package's own
dependencies (the key fetch blocks a thread, never the event loop). If an endpoint must be `async def`, hand its blocking
work to `fastapi.concurrency.run_in_threadpool`, as the sample's `add_note` does.

The package answers for you:

| Situation | Response |
| --- | --- |
| No token, a malformed header, or an invalid token | `401`, empty body, `WWW-Authenticate: Bearer` |
| A valid token without the permission | `403 {"error":"forbidden"}` |
| The key of the token is not known and Auth-Core cannot be reached | `503 {"error":"auth_unavailable"}` |

A token is valid when it has exactly one `Authorization: Bearer <token>` header, is signed with RS256 by a key of the
instance (the header says `typ: at+jwt` and a `kid`), has the configured `iss` and `aud`, has not expired, and carries a
`sub` and an `org_id` (non-empty strings) and `roles` and `permissions` (arrays of strings). No other algorithm is
accepted. A clock skew of 5 minutes is allowed, the same as Auth-Core allows itself.

A token is trusted for its lifetime (10 minutes, plus the skew). A member who is demoted or removed keeps what the token
says at your endpoints until it expires; the next refresh gives the new permissions. Auth-Core's own company API reads the
database on every call and does not have this delay.

The package never logs a token or a claim value. A rejected token is logged at debug level, by reason only
(`logging.getLogger("auth_core_fastapi")`).

### Testing your endpoints

Generate an RSA key in the test, sign tokens with it, and give the package a key cache that serves its public half, so
that no network and no Auth-Core is needed ([`tests/conftest.py`](../../samples/notes-api/tests/conftest.py) of the sample):

```python
from auth_core_fastapi import AuthCore, JwksCache

cache = JwksCache(jwks_url, fetch=lambda url, timeout: {"keys": [public_jwk]})
auth = AuthCore(issuer, audience, jwks_url, jwks_cache=cache)
```

Tokens must carry `typ: at+jwt` and a `kid` in their header, and `iss`, `aud`, `sub`, `org_id`, `roles`, `permissions`,
`iat` and `exp` in their claims.

## 5. Every table a company owns gets `org_id`, and every query filters by it

Add a non-null, indexed `org_id` (UUID) to each table that belongs to a company, in a migration
([`migrations/versions/0001_create_notes.py`](../../samples/notes-api/migrations/versions/0001_create_notes.py), applied at
startup by [`src/notes_api/db.py`](../../samples/notes-api/src/notes_api/db.py)). Then follow three rules, as
[`src/notes_api/app.py`](../../samples/notes-api/src/notes_api/app.py) does:

1. **The company is always the token's `org_id`.** Never read a company id from a path, a query string or a body: a
   request that cannot name a company cannot reach another company's rows.
2. **Every query is filtered by it**: reads, updates, deletes, and the lookup by id (`WHERE org_id = :org AND id = :id`).
   A row of another company is answered exactly as a row that does not exist (`404`), so ids reveal nothing.
3. **Writes set it from the token**, whatever the body says. Unique constraints that should hold per company include
   `org_id`.

A product that **already has data** takes one more step, because the old rows belong to nobody yet. Create the first
company with the CLI (step 7), which prints its id, and give that id to the migration that adds the column, so that the
column is added, filled and made `NOT NULL` in the same migration (PostgreSQL):

```python
import os

import sqlalchemy as sa
from alembic import op


def upgrade() -> None:
    first_company = os.environ["FIRST_COMPANY_ID"]  # printed by `admin create-org`
    op.add_column("reports", sa.Column("org_id", sa.Uuid(), nullable=True))
    op.execute(sa.text("UPDATE reports SET org_id = CAST(:org AS uuid)").bindparams(org=first_company))
    op.alter_column("reports", "org_id", nullable=False)
    op.create_index("ix_reports_org_id", "reports", ["org_id"])
```

The sample requires a token from its first migration and has no "no login" mode; the package does not provide one either.
A product that has to keep running both ways decides that itself.

## 6. Add Auth-Core and the proxy to your compose

Your browser talks to **one origin**: `/auth` goes to Auth-Core and `/api` to your backend, behind one proxy
([ADR 0004](../adr/0004-same-origin-cookie-refresh.md): the refresh cookie has `Path=/auth` and is `SameSite=Strict`).
The sample does this with a compose overlay on top of the repository's development stack,
[`samples/notes-api/compose.yml`](../../samples/notes-api/compose.yml), and a 17-line proxy file,
[`samples/notes-api/Caddyfile`](../../samples/notes-api/Caddyfile):

```
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env up -d --build
```

What the overlay does, and what yours must do:

- **A proxy** (Caddy) on one port, routing `/auth/*` to Auth-Core and `/api/*` to your backend. Your backend publishes
  no port and is reachable only through the proxy. Auth-Core's own port is published on the loopback interface, for
  development only; in production only the proxy faces the network.
- **Auth-Core's issuer is the origin the browser uses** (`Auth__Tokens__Issuer: http://localhost:8088/auth` in the
  overlay), and its audience is yours (`Auth__Tokens__Audience`). The backend gets the same two values and fetches the keys
  over the internal network (`AUTH_JWKS_URL: http://auth:8080/auth/.well-known/jwks.json`).
- **Your manifest** is mounted into Auth-Core at `/etc/auth-core/auth.yaml`.
- **Your database**: a database and a login of its own on the PostgreSQL server of the stack. An init script of the
  `postgres` image runs on a fresh volume only, so the overlay uses a one-shot `notes-db-init` service that is safe to run
  again and again: it creates the login and the database when they are missing and sets the password again otherwise.
- **Your backend starts after the database exists**, and applies its migrations itself at startup.

The overlay is for development: plain HTTP, ports on the loopback interface only. A deployment needs HTTPS at the proxy
(the refresh cookie is `Secure`), and real secrets in place of the `.env` of the development stack.

## 7. Create the first company and invite its admin

Companies are made by the operator, with a command of Auth-Core's own binary, run from its image
([spec 0005](../superpowers/specs/0005-tenancy-and-rbac.md)):

```bash
auth() { docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env run --rm -T --no-deps auth admin "$@"; }
auth create-org --name "Acme"                                   # prints the company id
auth invite --org <company-id> --email boss@acme.test --role admin
```

The invitation is mailed (in development, to the mail catcher at `http://localhost:8025`) at the server's next poll,
within a minute. The mail links to the screen of your frontend that accepts it; accepting sets the person's password and
makes them the company's admin. From then on the admin invites, re-roles and removes members and defines roles through
`/auth/org/...`, with no operator. [`scripts/e2e-notes.sh`](../../scripts/e2e-notes.sh) does all of this, its steps 3 to 5: the
operator's part (create the company, invite its first admin) with the CLI, the rest over HTTP.

## 8. What your frontend must handle

| Answer | Where from | What to do |
| --- | --- | --- |
| `401` | your backend | `POST /auth/refresh` (the cookie goes along by itself) and retry once. If the refresh is a `401` too, show the login screen. |
| `403 {"error":"forbidden"}` | your backend | The user has no access to this: show "no access". Do not refresh. |
| `503 {"error":"auth_unavailable"}` | your backend | Auth-Core cannot be reached and the backend holds no key for the token. Show "try again shortly" and keep the user signed in; do not log them out. |
| `403 {"error":"permissions_changed"}` | Auth-Core's company API (`/auth/org/...`) | The token still claims a permission the database no longer grants. Refresh and retry once. |

Your backend never answers `permissions_changed`: it does not read the database.

## Check that it works

With a clean stack, [`scripts/e2e-notes.sh`](../../scripts/e2e-notes.sh) starts the sample's stack and drives it through
the proxy: login, a note, the `401` cases, a second company that sees none of the first company's notes, a viewer that
reads and cannot write, a role change that reaches the next refresh, and Auth-Core going down and coming back.
