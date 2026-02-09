# Spec 0006 — First consumer, backend: the Python package and a sample product

- **Status:** Accepted
- **Date:** 2026-02-09
- **Author:** Alex
- **Milestone:** Week 4 (first consumer, backend) — *"the API accepts only valid
  tokens with the right permission"*, with its first item, the Python package
  (FastAPI dependency, JWKS cache, `require_permission`). The product is a sample
  inside this repository instead of speech-to-mail (Decision 2). Built 09–12.02.
- **Context:** [`docs/design.md`](../../design.md) (Tech stack: Python backend and
  reverse proxy; "Request flow"; repository layout `clients/python/`), ADR
  [0001](../../adr/0001-instance-per-project.md) (instance per product), ADR
  [0004](../../adr/0004-same-origin-cookie-refresh.md) (one origin, `/auth` and
  `/api` behind one proxy), and specs [0001](0001-login-and-token-issuance.md) (the
  token: RS256, `kid`, `typ: at+jwt`, `iss`, `aud`, JWKS) and
  [0005](0005-tenancy-and-rbac.md) (the claims `org_id`, `roles`, `permissions`;
  the manifest; the operator CLI; the company API and its error codes).

## Goal

A backend written in Python can trust Auth-Core's access tokens with a few lines of
code, and a product built on it lets each user see and do only what their company
and role allow.

The work is Auth-Core's alone. It ships a generic Python package and a small sample
product, "notes", that uses it the way a real product would. The sample is also the
worked example for the integration guide, which a product such as speech-to-mail
follows later.

Concretely, this is met when this sequence works on a clean stack started from the
repository:

```
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env up -d --build

# everything goes through the proxy, as a browser would
curl -i -X POST http://localhost:8088/auth/login -d '{"email":"<admin>","password":"<p>"}'  # -> 200, token with notes:read, notes:write
curl -i -X POST http://localhost:8088/api/notes -H 'Authorization: Bearer <admin>' -d '{"text":"hello"}'  # -> 201
curl -i      http://localhost:8088/api/notes -H 'Authorization: Bearer <admin>'   # -> 200, the note is listed
curl -i      http://localhost:8088/api/notes                                       # -> 401, WWW-Authenticate: Bearer

# a viewer of the same company may read, not write
curl -i      http://localhost:8088/api/notes -H 'Authorization: Bearer <viewer>'  # -> 200
curl -i -X POST http://localhost:8088/api/notes -H 'Authorization: Bearer <viewer>' -d '{"text":"x"}'  # -> 403 forbidden

# a member of another company sees none of it
curl -i      http://localhost:8088/api/notes/<id> -H 'Authorization: Bearer <other>'  # -> 404
```

## In scope

- **The Python package** in `clients/python/`: verifies access tokens against the
  instance's JWKS and gives each endpoint the caller's `sub`, `org_id`, `roles` and
  `permissions`; guards an endpoint by a permission.
- **The sample product** in `samples/notes-api/`: a FastAPI service on PostgreSQL
  with an Alembic migration, its own `auth.yaml`, and three endpoints guarded by the
  package.
- **A compose overlay** that adds the sample and Caddy to the existing development
  stack, with `/auth` routed to Auth-Core and `/api` to the sample on one origin.
- **An e2e script**, `scripts/e2e-notes.sh`, that drives the whole path through the
  proxy.
- **Tests of the package** that need no Docker.
- **The integration guide** `docs/integration/python-fastapi.md`: how any Python
  product connects to Auth-Core, step by step, pointing at the sample's files.

## Out of scope / Deferred

See [Deferred / follow-ups](#deferred--follow-ups). Not built here: any change to
speech-to-mail, the test on a real phone, a "no login" mode in the sample or the
package, moving existing single-company data into a company, HTTPS on the local
proxy, publishing the package to PyPI, and a frontend.

## Contract

### The package

**Name and installation.** The distribution is `auth-core-fastapi`, the import
package `auth_core_fastapi`, version `0.1.0`. It supports Python 3.12 and
FastAPI 0.115. Its only runtime dependencies are FastAPI and `PyJWT[crypto]`. A
product installs it from this repository at a tag:

```
pip install "auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v0.1.0#subdirectory=clients/python"
```

**Configuration.** The product creates one object at startup with three values:

| Value | Meaning |
| --- | --- |
| `issuer` | Must equal the token's `iss`, for example `https://app.example.com/auth` |
| `audience` | Must equal the token's `aud`: the instance's audience |
| `jwks_url` | Optional. Where the keys are fetched. Defaults to `issuer` + `/.well-known/jwks.json`; a backend that reaches Auth-Core over an internal network sets it, for example `http://auth:8080/auth/.well-known/jwks.json` |

Creating the object makes no network call: a product starts even while Auth-Core
is down.

**Use at an endpoint.** Two FastAPI dependencies, each giving a `Principal` with
`sub`, `org_id`, `roles` (a tuple of strings) and `permissions` (a frozenset of
strings):

```python
from auth_core_fastapi import AuthCore, Principal

auth = AuthCore(issuer=..., audience=..., jwks_url=...)

@app.get("/api/me")
def me(user: Principal = Depends(auth.current_user)): ...

@app.post("/api/notes")
def add(user: Principal = Depends(auth.require_permission("notes:write"))): ...
```

`require_permission` checks the token's `permissions` only. It never calls
Auth-Core: an endpoint trusts a valid token for its lifetime, as spec 0005
(Decisions 4 and 12) accepts.

**A token is valid** when all of these hold:

- The request has exactly one `Authorization` header of the form `Bearer <token>`
  (scheme case-insensitive).
- The token is a JWS with header `alg` `RS256`, `typ` `at+jwt` and a `kid`. No other
  algorithm is accepted.
- The signature verifies with the JWKS key of that `kid`.
- `iss` and `aud` equal the configured values. `exp` is in the future and `iat` is
  present, both with a clock skew of 5 minutes (the default Auth-Core uses when it
  validates its own tokens): a token is still accepted up to 5 minutes after `exp`.
- `sub` and `org_id` are non-empty strings. `roles` and `permissions` are arrays of
  strings.

**Responses the package produces.** JSON bodies carry
`Cache-Control: no-store`, as Auth-Core's do.

| Situation | Response |
| --- | --- |
| No token, malformed header, invalid token as above | `401`, empty body, `WWW-Authenticate: Bearer` |
| Valid token without the required permission | `403 {"error":"forbidden"}` |
| The key for the token's `kid` is not known and the JWKS cannot be fetched | `503 {"error":"auth_unavailable"}` |

A frontend handles them as it handles the company API of spec 0005: on `401` it
refreshes and retries; on `403` it shows "no access"; on `503` it shows "try again
shortly" and keeps the user signed in. The package never answers
`permissions_changed`, because it does not read the database.

**Keys.**

- The keys are fetched on first use and kept in memory.
- They are fetched again after 5 minutes, and at once when a token names an unknown
  `kid` (key rotation), but at most once every 10 seconds.
- A failed fetch keeps the keys already held, so tokens signed by known keys keep
  working while Auth-Core is down.
- A token whose `kid` is still unknown is a `401` when the latest fetch succeeded,
  and a `503` when it failed (whether that fetch ran for this request or within the
  10 seconds before it).
- A fetch has a 5-second timeout and never blocks the event loop.

**Logging.** The package never logs a token or a claim value. A rejected token is
logged at debug level with the reason only.

### The sample product, "notes"

**Endpoints.** All under `/api`, all behind the package except `health`:

| Endpoint | Needs | Answer |
| --- | --- | --- |
| `GET /api/notes` | `notes:read` | `200`, the notes of the caller's company, newest first: `[{"id","text","author_sub","created_at"}]` |
| `GET /api/notes/{id}` | `notes:read` | `200` one note; `404 {"error":"not_found"}` when the id is not a note of the caller's company, or not a UUID |
| `POST /api/notes` — `{"text"}` | `notes:write` | `201` the note; `400 {"error":"invalid_request"}` unless the body is a JSON object whose `text` is a string of 1–1000 characters |
| `GET /api/health` | nothing | `200` when the database answers |

The company is always the token's `org_id`. A request never names a company, so no
request can reach another company's notes; another company's note is a `404`, as in
spec 0005.

**Manifest.** `samples/notes-api/auth.yaml`, read by Auth-Core in the overlay:

```yaml
permissions: [notes:read, notes:write]
default_roles:
  admin:  ["*"]
  user:   [notes:read, notes:write]
  viewer: [notes:read]
```

**Data.** A database `notes` on the stack's PostgreSQL server, separate from
Auth-Core's database. One table, `notes`: `id` (UUID), `org_id` (UUID, not null,
indexed), `author_sub`, `text`, `created_at`. The first Alembic migration creates it.
The service applies migrations at startup.

**Configuration.** Environment variables: `AUTH_ISSUER`, `AUTH_AUDIENCE`,
`AUTH_JWKS_URL`, `DATABASE_URL`.

### Compose overlay and proxy

`samples/notes-api/compose.yml` is used on top of `deploy/docker-compose.yml`, which
stays unchanged, so the e2e scripts of specs 0001–0005 run as before. The overlay:

- adds **Caddy 2** on `http://localhost:8088` (loopback only), routing `/auth/*` to
  Auth-Core and `/api/*` to the sample. Plain HTTP for now.
- adds the **sample**, reachable only through Caddy;
- creates the `notes` database on the existing PostgreSQL server;
- sets Auth-Core's manifest to the sample's `auth.yaml`, and its issuer to
  `http://localhost:8088/auth`, so tokens name the origin the browser uses;
- gives the sample that issuer, Auth-Core's audience, and the internal JWKS URL.

### E2E script

`scripts/e2e-notes.sh` starts the stack with the overlay and drives, through Caddy:

1. The development admin logs in and adds a note (`201`); the list holds it.
2. No token, a token with a changed signature, and a malformed header each get `401`
   with `WWW-Authenticate: Bearer`.
3. The operator creates a second company with the CLI and invites its first admin.
   That admin accepts the invitation from the mail in Mailpit and logs in. Their list
   is empty, and the first company's note by id is a `404`.
4. The first admin invites a member with the role `viewer` through the company API.
   The viewer accepts and logs in. Reading is `200`; adding a note is
   `403 forbidden`.
5. The admin changes the viewer's role to `user`. After a refresh, the same person
   adds a note (`201`).
6. Auth-Core is stopped and the sample is restarted, so it holds no keys. A request
   with a valid token gets `503 auth_unavailable`. After Auth-Core starts again, the
   same request gets `200`.

### Integration guide

`docs/integration/python-fastapi.md`, written for the developer of any Python
product. It names no product. Steps, each pointing at the sample's file:

1. Install the package at a tag.
2. Write the product's `auth.yaml`: permissions and default roles.
3. Configure `issuer`, `audience` and `jwks_url`.
4. Guard endpoints with `current_user` and `require_permission`.
5. Add `org_id` to every table a company owns, with a migration, and filter every
   query by the token's `org_id`. For a product that already has data: create the
   first company with the CLI and assign the existing rows to its id in the same
   migration.
6. Add Auth-Core and the proxy to the product's compose.
7. Create the first company and invite its admin with the CLI.
8. What the product's frontend must handle: `401`, `403`, `503`, and
   `permissions_changed` from the company API.

## Acceptance criteria (Done when)

1. The Goal sequence and all six steps of `scripts/e2e-notes.sh` pass on a clean
   stack; the four earlier e2e scripts and the one of spec 0005 still pass on the
   stack without the overlay.
2. The package accepts a token that meets every rule of "A token is valid", and its
   `Principal` carries the token's `sub`, `org_id`, `roles` and `permissions`.
3. The package answers `401` with an empty body and `WWW-Authenticate: Bearer` for
   each of: no header; a scheme other than `Bearer`; two `Authorization` headers; a
   token that is not a JWS; `alg` `none`, `HS256` or any other than `RS256` (an
   `HS256` token signed with the public key included); `typ` other than `at+jwt`; a
   changed signature; a wrong `iss`; a wrong `aud`; an expired token; a missing `sub`,
   `org_id`, `roles` or `permissions`, or one of the wrong type.
4. `require_permission` answers `403 {"error":"forbidden"}` for a valid token without
   the permission, and lets a token with it through.
5. Keys: a token signed by a new key is accepted after one refetch; the refetch
   happens at most once every 10 seconds whatever the number of tokens with unknown
   `kid`s; keys already held keep working while the JWKS URL fails; with no key for
   the `kid` and a failing JWKS URL the answer is `503 {"error":"auth_unavailable"}`.
6. Creating the `AuthCore` object makes no network call.
7. No token or claim value appears in the package's logs at any level.
8. The sample never returns or changes a note of another company, whatever id it is
   given; another company's note and a non-UUID id are both `404`.
9. The package's tests run with `pytest` and no Docker, using keys generated in the
   test.
10. The integration guide covers the eight steps, and every file it points at exists
    in the sample.

## Decisions (owner, 2026-02-09)

1. **The package lives in Auth-Core**, in `clients/python/`, and is generic: any
   Python product uses it. It is versioned with the token contract and installed
   from a tag of this repository.
2. **This slice is Auth-Core work only.** No code or pull request in speech-to-mail.
   The product that proves the milestone is a sample built here; speech-to-mail
   connects later by following the integration guide. This departs from design.md,
   where week 4 changes speech-to-mail itself (`AUTH_MODE`, `org_id` on its tables,
   compose).
3. **The sample is "notes" in `samples/notes-api/`**, inside this repository, so one
   clone runs the whole example and the e2e script needs nothing else.
   stereo-splitter was considered and set aside: it has no backend, and it promises
   that files never leave the device.
4. **The test on a real phone moves to slice 7** (frontend). This slice ends with
   the e2e script driven by `curl`. This departs from design.md, which puts the phone
   test in the first consumer week to find cookie, proxy and iOS PWA problems early.
5. **The sample stores its notes in PostgreSQL with an Alembic migration**, so the
   guide's `org_id` step points at working code.
6. **The sample requires a token from its first migration.** It has no "no login"
   mode and no move of single-company data into a company; the guide describes
   both, the package does not provide them.
7. **When Auth-Core is down and the backend holds no key for a token, the answer is
   `503 auth_unavailable`**, not `401`, so the user stays signed in and sees "try
   again shortly" instead of the login screen.
8. **The guide is generic.** It names no product. A checklist specific to
   speech-to-mail is written in speech-to-mail when its integration starts.
9. **The clock skew is 5 minutes**, the same as Auth-Core's own validation, so a
   backend and Auth-Core agree on when a token has expired. Tests of an expired token
   use one that expired more than 5 minutes ago.

## Deferred / follow-ups

- **speech-to-mail's integration:** its switch between "no login" and tokens, `org_id`
  on its tables with existing data moved into the first company, its real
  permissions and roles, its compose. A decision in its `docs/mvp/` is needed first,
  because multi-tenancy is out of scope there today.
- **The test on a real phone**, with HTTPS on the local proxy → slice 7.
- **Publishing the package to PyPI** and extracting the clients into their own
  repository → Beyond MVP ("SDKs: NuGet, npm, extracted Python package").
- **A "no login" mode** in the package, for products that run both ways.
- **Asking Auth-Core whether a token's permissions are still current** (the
  `permissions_changed` check of spec 0005 at a product's own endpoints).
- **design.md:** a one-line note on Decisions 2 and 4, added with "As built".

**Residual risks:**

- A member demoted or removed keeps the token's permissions at the product's
  endpoints for up to 10 minutes. Accepted in spec 0005.
- A product that sets `jwks_url` to an address an attacker controls accepts the
  attacker's tokens. The guide says to use HTTPS or an internal network address only.

## Verification notes (for the local verifiers)

Per [`docs/workflow.md`](../../workflow.md) — verifiers run locally before merge.

- **realization vs spec:** map each criterion to its guarding test. Criteria 3–7 are
  package tests with keys generated in the test and a local JWKS server; criterion 5
  needs a controlled clock for the 10-second and 5-minute rules.
- **API / e2e:** bring the stack up clean with the overlay and run
  `scripts/e2e-notes.sh`, taking each invitation token from the mail as delivered.
  Then run the earlier e2e scripts on the stack without the overlay as the
  regression pass.
- **security:** confirm that no algorithm other than RS256 is accepted, including
  algorithm confusion with the public key; that a key is chosen only by `kid` from
  the configured JWKS; that unknown `kid`s cannot make the package fetch more than
  once every 10 seconds; that no token or claim is logged; that every query of the
  sample is filtered by the token's `org_id`; and that the sample is not reachable
  except through Caddy.

## To verify during implementation

- That **`PyJWKClient`** provides the key rules above (keep keys on a failed fetch,
  refetch on unknown `kid` with the 10-second limit, 5-second timeout), or that the
  package needs its own small cache on top of `PyJWKSet`.
- How **a blocking JWKS fetch stays off the event loop**: synchronous dependencies
  run in FastAPI's thread pool, or the fetch runs in a worker thread.
- That **Auth-Core's issuer can be set to the proxy's origin** through `Auth:Tokens:Issuer`
  in the overlay while the sample fetches the JWKS over the internal network, and the
  refresh cookie (`Path=/auth`) still works through Caddy on `http://localhost`.
- How the overlay **creates the `notes` database and its own login** on a PostgreSQL
  volume that may already exist (an init script runs only on a fresh volume).
- That `scripts/e2e-notes.sh` can **get the viewer role's id** from the company API
  and change a member's role, as spec 0005 built them (read its "As built" first).
- **New packages**, each needing the owner's approval before download: FastAPI and
  `PyJWT[crypto]` (the package); Uvicorn, SQLAlchemy, Alembic and psycopg (the
  sample); pytest and httpx (tests); the Caddy 2 image; a build backend for
  `pyproject.toml`.
- That **Python 3.12** is available locally for the tests (`python3` here is the
  Microsoft Store stub).
