# Python Consumer Package and the Notes Sample Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0006
- **Date:** 2026-02-09
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0006-python-consumer-package.md`](../specs/0006-python-consumer-package.md)
  — the plan argues from the spec; where they disagree, **the spec wins** and the
  disagreement is a finding (see [`docs/workflow.md`](../../workflow.md)).

**Goal:** A backend written in Python can trust Auth-Core's access tokens with a few lines of code, and a product built on
it lets each user see and do only what their company and role allow: a generic package for FastAPI backends in
`clients/python/`, a small sample product ("notes") in `samples/notes-api/` that uses it, a compose overlay that puts both
behind one proxy, an e2e script that drives the whole path, and the integration guide.

**Architecture:** The package is one object, `AuthCore`, made at startup from an issuer, an audience and an optional JWKS
URL (no network call). Its two FastAPI dependencies are plain `def`, so FastAPI runs them in its thread pool and the key
fetch never blocks the event loop; they check the token by the rules of the spec (RS256 only, `typ` `at+jwt`, a `kid`,
`iss`, `aud`, `exp`, `iat`, 5 minutes of skew, four claims) and raise `AuthError`, which `auth.install(app)` turns into the
three responses of the contract. The keys live in a small cache of its own over `PyJWKSet` (not `PyJWKClient`): keys by
`kid`, renewed after 5 minutes and on an unknown `kid`, at most once in 10 seconds with failures counted, kept when a fetch
fails, one fetch at a time, and an answer of 401 or 503 that depends on whether the latest fetch worked. The sample is a
FastAPI service on SQLAlchemy and PostgreSQL with one Alembic migration, applied at startup; every query is filtered by
the token's `org_id`. A compose overlay adds the sample, a one-shot job that creates its database, and Caddy on
`http://localhost:8088` (`/auth` to Auth-Core, `/api` to the sample), and points Auth-Core at the sample's manifest and at
the proxy's origin as its issuer. `scripts/e2e-notes.sh` starts that stack and drives it through Caddy in the six steps of
the spec.

**Tech Stack:** Python 3.12; FastAPI, `PyJWT[crypto]` (the package's only runtime dependencies); Uvicorn, SQLAlchemy 2,
Alembic, psycopg 3 (the sample); pytest, httpx (tests); hatchling (the build backend); Caddy 2 and PostgreSQL 16 (images);
and, from plans 0001–0005, .NET 10 Auth-Core, Mailpit, Docker Compose. **Every package here is on the owner's list of
approved downloads** (spec 0006, "To verify": FastAPI and `PyJWT[crypto]`; Uvicorn, SQLAlchemy, Alembic and psycopg;
pytest and httpx; the Caddy 2 image; a build backend), at the versions the plan was proved with: FastAPI 0.142.2, PyJWT
2.15.1 with `cryptography`, Uvicorn 0.54.0, SQLAlchemy 2.1.3, Alembic 1.20.0, psycopg 3.3.6, pytest 9.1.1, httpx 0.28.1,
hatchling 1.32.4. The `pyproject.toml` and `requirements.txt` below state floors, not pins. One thing is **not** on that
list and needs the owner's yes before the first image build: the base image `python:3.12-slim` of the sample's Dockerfile
(it is already on this machine). Nothing else is new.

## Global Constraints

Values copied from the spec, one line each; every task includes them.

- **The package:** distribution `auth-core-fastapi`, import package `auth_core_fastapi`, version `0.1.0`. It supports
  Python 3.12 and FastAPI 0.115. Its only runtime dependencies are FastAPI and `PyJWT[crypto]`. A product installs it from
  this repository at a tag:
  `pip install "auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v0.1.0#subdirectory=clients/python"`.
- **Configuration:** `issuer` (must equal `iss`), `audience` (must equal `aud`), `jwks_url` (optional; defaults to
  `issuer` + `/.well-known/jwks.json`). Creating the object makes **no network call**.
- **A token is valid** when: the request has exactly one `Authorization` header of the form `Bearer <token>` (scheme
  case-insensitive); the token is a JWS with header `alg` `RS256`, `typ` `at+jwt` and a `kid`, and no other algorithm is
  accepted; the signature verifies with the JWKS key of that `kid`; `iss` and `aud` equal the configured values; `exp` is in
  the future and `iat` is present, **both with a clock skew of 5 minutes** (Decision 9: `leeway=300`; a token is still
  accepted up to 5 minutes after `exp`, so a test of an expired token uses one that expired more than 5 minutes ago); `sub`
  and `org_id` are non-empty strings; `roles` and `permissions` are arrays of strings. `Principal` carries `sub`, `org_id`,
  `roles` (a tuple) and `permissions` (a frozenset).
- **Responses of the package:** no token, malformed header, invalid token → `401`, empty body, `WWW-Authenticate: Bearer`;
  a valid token without the required permission → `403 {"error":"forbidden"}`; the key for the token's `kid` is not known
  and the JWKS cannot be fetched → `503 {"error":"auth_unavailable"}`. JSON bodies carry `Cache-Control: no-store`. The
  package never answers `permissions_changed` and never calls Auth-Core from `require_permission`.
- **Keys:** fetched on first use and kept in memory; fetched again after **5 minutes**, and at once when a token names an
  unknown `kid`, but at most once every **10 seconds** (a failed fetch counts); a failed fetch keeps the keys already held; a
  token whose `kid` is still unknown is a `401` when the latest fetch succeeded and a `503` when it failed (whether that
  fetch ran for this request or within the 10 seconds before it); a fetch has a **5-second timeout** and never blocks the
  event loop.
- **Logging:** the package never logs a token or a claim value. A rejected token is logged at debug level with the reason
  only.
- **The sample:** all endpoints under `/api`, all behind the package except `health`; `GET /api/notes` (`notes:read`, the
  notes of the caller's company, newest first, `[{"id","text","author_sub","created_at"}]`), `GET /api/notes/{id}`
  (`notes:read`; `404 {"error":"not_found"}` when the id is not a note of the caller's company, or not a UUID),
  `POST /api/notes` (`notes:write`; `201` the note; `400 {"error":"invalid_request"}` unless the body is a JSON object whose
  `text` is a string of 1–1000 characters), `GET /api/health` (no token; `200` when the database answers). **The company is
  always the token's `org_id`**; a request never names a company.
- **The sample's data:** a database `notes` on the stack's PostgreSQL server, separate from Auth-Core's. One table `notes`:
  `id` (UUID), `org_id` (UUID, not null, indexed), `author_sub`, `text`, `created_at`. The first Alembic migration creates it;
  the service applies migrations at startup. Environment variables: `AUTH_ISSUER`, `AUTH_AUDIENCE`, `AUTH_JWKS_URL`,
  `DATABASE_URL`. Manifest `samples/notes-api/auth.yaml`: `permissions: [notes:read, notes:write]`, default roles
  `admin: ["*"]`, `user: [notes:read, notes:write]`, `viewer: [notes:read]`.
- **The overlay:** `samples/notes-api/compose.yml` is used on top of `deploy/docker-compose.yml`, **which stays unchanged**,
  so the e2e scripts of specs 0001–0005 run as before. It adds Caddy 2 on `http://localhost:8088` (loopback only; `/auth/*` to
  Auth-Core, `/api/*` to the sample; plain HTTP), the sample (reachable only through Caddy), the `notes` database, Auth-Core's
  manifest set to the sample's `auth.yaml` and its issuer to `http://localhost:8088/auth`, and gives the sample that issuer,
  Auth-Core's audience and the internal JWKS URL.
- **The guide** `docs/integration/python-fastapi.md` is written for the developer of any Python product and **names no
  product**; it has the eight steps of the spec and every file it points at exists in the sample.
- **Workflow** ([`docs/workflow.md`](../../workflow.md)): local only (no CI, no pull request), Conventional Commits,
  documentation in English. **The orchestrator makes the commits** (their author and date are its own): implementers
  leave their changes uncommitted in the working tree, and the orchestrator reads the diff, runs the gate and commits with
  the subject named in the task's last step.
- **No new package** beyond the list above; no change under `src/`, `tests/Auth.IntegrationTests/` or
  `deploy/docker-compose.yml`. The 829 .NET tests and the five e2e scripts of plans 0001–0005 pass unedited.
- **Python on this machine:** the `python3` on `PATH` is the Microsoft Store shim and does not work. Use the real
  interpreter (`C:/Users/mwppl/AppData/Local/Programs/Python/Python312/python.exe`, Python 3.12) and make the virtual
  environment at a **short path** (`C:/p6v` below): the worktree's own path is too long for Windows, which has no
  long-path support here. The e2e script needs a `python3` that works too (a shim directory first on `PATH` that runs the
  venv's `python.exe`), and `python3`'s standard library only.
- **Shell scripts** are LF and executable in the index: after `git add scripts/e2e-notes.sh`, run
  `git update-index --chmod=+x scripts/e2e-notes.sh`.
- **Unicode escapes stay out of the source.** A backslash followed by `u` and four hex digits in a file is decoded by some
  editing tools on the way in. The tests below build such text with `chr(92)` and `chr(0x…)` and are plain ASCII; after
  writing the Python files, `grep -rInP --exclude-dir=__pycache__ --exclude-dir=.pytest_cache '\\[uU][0-9a-fA-F]{4}|[^\x00-\x7F]' clients samples scripts` must find nothing.
- From Task 2 on, the pytest summary line shows a `StarletteDeprecationWarning` ("Using `httpx` with
  `starlette.testclient` is deprecated") with FastAPI 0.142.2; it is expected and harmless (the spec names `httpx` as the
  test dependency).

## Verified before this plan was written

Probes in a scratch copy (not in the repository), with the packages above and Docker, settled the spec's "To verify" items.
The code blocks of this plan are those scratch files, byte for byte.

- **`PyJWKClient` (PyJWT 2.15.1) does not give the key rules of the spec; a small cache of our own does.** Its cooldown for an
  unknown `kid` starts only at a *successful* fetch, so while Auth-Core is down every request with an unknown `kid` goes to
  the network (run against a server that answers 500: 1 known and 5 unknown `kid`s caused 6 fetches; with the first fetch
  failing, every request tried the network and waited for its timeout); when its cached key set expires during an outage
  even a **known** `kid` fails (`PyJWKClientConnectionError`), where the spec says the keys already held keep working; and
  one lock is held for the whole fetch, known keys included. `JwksCache` is about a hundred lines over `PyJWKSet`: proved with a
  clock moved by hand (10 seconds with failures counted, 5 minutes, held keys kept), with threads (one fetch for eight waiting
  requests; a request that holds its key does not wait) and against a real server on 127.0.0.1 (the 5-second timeout, error
  statuses, a non-key-set answer, a valid key set over 1 MiB that is refused for its size and not for a parse error, and a
  server that sends one byte every 0.1 s, which the **total** deadline of the fetch ends although no single wait is long).
- **The blocking fetch stays off the event loop** with a plain `def` dependency: FastAPI runs it in the thread pool. With a
  key set that takes 1.5 s, a concurrent `/ping` answered in 0.3 s (`test_server.test_a_slow_fetch_does_not_block_the_event_loop`);
  the negative control (the same call made directly inside an `async def` dependency) held the loop for the whole 2 s.
- **FastAPI cannot answer an empty `401` by itself.** Its handler for `HTTPException` always sends `{"detail": …}`. The
  package therefore raises `AuthError` (a subclass of `HTTPException`, with the status and headers already right) and
  `auth.install(app)` registers a handler for it. A product that forgets the call still gets the right status codes and
  headers, only FastAPI's own body (`test_tokens.test_without_install_the_status_and_the_headers_are_still_right`).
- **The clock skew** is `leeway=300`: a token expired 200 s ago is accepted, 400 s ago is not; an `iat` 200 s in the future
  is accepted, 1000 s is not.
- **Alembic**: `alembic revision --rev-id 0001` writes `0001_create_notes.py` (the default file template is
  `<rev>_<slug>`). A migration run from inside the service must not call `fileConfig` (it would disable the service's
  loggers): `env.py` skips it when the service sets `configure_logger`. The migration ran on PostgreSQL 16 (`upgrade head`,
  `upgrade head --sql`, `downgrade base`), and the sample's tests ran there too (`NOTES_TEST_DATABASE_URL`): all 57 pass.
- **Compose**: relative paths in the overlay resolve against `deploy/` (the first `-f` file); a `volumes` entry with the
  same container path as the base file's **replaces** it (the merged config has one mount of `/etc/auth-core/auth.yaml`,
  from the sample); `notes-db-init` (one `psql` script with `\gexec`, `$$` for a literal `$` in YAML) created the login and the
  database on a fresh volume and, run again, only set the password; `docker port <container>` prints nothing for a service
  that only has `expose` (`docker compose port` prints `invalid IP:0` and exits 0, so the script does not use it).
- **The stack**, built and run for real with the Dockerfile below and a stand-in for Auth-Core's key set: the sample applied
  its migration at startup, answered through Caddy (`/api/health` 200, no token 401, a `POST` 201, a viewer's `POST` 403),
  held its keys while Auth-Core was stopped (200), answered `503 auth_unavailable` after a restart with Auth-Core stopped,
  and went back to `200` six seconds after Auth-Core started (the 10-second rule: the first answers after a restart can
  still be `503`; the e2e script polls).
- **The real Auth-Core** (the image of slices 1–4, which has no tenancy claims yet) behind the same Caddy, with
  `Auth__Tokens__Issuer=http://localhost:8088/auth` and `Auth__Tokens__Audience=notes-api`: login through the proxy is `200`
  with `auth_rt` `path=/auth; secure; samesite=strict; httponly`; a refresh through the proxy over plain HTTP, with the cookie
  sent by hand, is `200`; the token's header is `alg` `RS256`, `typ` `at+jwt`, a `kid`; `iss` is the proxy's origin; the key
  set at `/auth/.well-known/jwks.json` (with `x5c`) loads in `JwksCache` through the proxy **and** inside the sample's
  container over `http://auth:8080/…`; the package accepts that token on every rule up to the claims, and rejects it only for
  the missing `org_id`, `roles` and `permissions`, which slice 5 adds.
- **`scripts/e2e-notes.sh`**: its helpers and its steps 1 and 2 ran against that stack with tokens made by the sample's test
  signer (`bash -n` clean). Its steps 3–6 call the company API and the operator CLI of **spec 0005, which is not on this
  branch**: they are written against the exact routes and payloads of plan 0005, quoted below.
- **Mutations**: with each of these one-line changes the suites fail where Tasks 2, 3 and 5 say they do (leeway 0, `iss` not
  checked, `typ` not checked, two headers accepted, a key taken from the token's `jwk` header, the token in a log line, no
  10-second limit, a failed fetch dropping the keys, `503` answered as `401`, a known key waiting for a running fetch, no size
  check of the key set, no total deadline of the fetch; and in the sample, a lookup by id without the company, a list without
  the company, no NUL check, a body cap of 1 MiB instead of 16 KiB, `notes:read` guarding the write).

**The routes of plan 0005 that the e2e script relies on** (line numbers of
`docs/superpowers/plans/0005-tenancy-and-rbac.md`; the spec wins if they differ):

| What the script does | Plan 0005 |
| --- | --- |
| The development company's admin is the seed user; its token carries `org_id`, `roles`, `permissions` (`*` expanded, sorted, no `*`) | Task 4 (the development company), Task 5; the script's step 2 and 5, lines 12749–12760 and 12809–12815 |
| The service reads the manifest at `Auth:Manifest:Path`; the base compose mounts `./auth.yaml:/etc/auth-core/auth.yaml:ro` and names it | lines 12488 and 12495 (the overlay replaces that mount) |
| `GET /auth/org/roles` → `{"catalog": [...], "roles": [{"id", "name", ...}]}` | lines 12818–12823 |
| `GET /auth/org/members` → `{"members": [{"user_id", "email", ...}]}` | lines 12836–12840 |
| `POST /auth/org/invites` with `{"email", "role_id"}` → `202`, empty body; an API invitation wakes the dispatcher (mail within 30 s) | lines 12824–12828 |
| `PUT /auth/org/members/{user_id}/role` with `{"role_id"}` → `204` | line 12869 |
| `POST /auth/invites/accept` with `{"token", "password"}` → `204`; the link in the mail is `http://localhost:4200/invite?token=<43 characters>` | lines 12709–12723 (`token_body`), 12829–12831 |
| The operator CLI as `docker compose run --rm -T --no-deps auth admin …`: `create-org --name` prints the company id only; `invite --org --email --role` queues a mail the server sends at its next poll (up to 120 s) | lines 12729–12734, 12763–12776 |
| Login `POST /auth/login` → `{"access_token"}` and `Set-Cookie: auth_rt=…`; `POST /auth/refresh` with the cookie | lines 12665–12684 |

## Review Focus

The spec does not name these, but a person using this software would hit them. Each line has a pinning test in the task
named in brackets, and a row in `docs/superpowers/plans/0006-acceptance-map.md`.

1. **A note the database cannot store, or a body that is not what the endpoint expects** — a NUL character, a lone
   surrogate, 1001 characters, a body of 20 000 bytes, `[]`, `null`, not JSON at all: `400 invalid_request`, never a `500`,
   and the `401` or `403` comes first. [Task 5]
2. **Auth-Core slow or hung at the moment a key is needed** — no other request waits for it (a request that holds its key
   never takes the lock while another request is fetching), the fetch gives up at 5 seconds, eight requests that wait share
   one fetch. [Task 1, Task 3]
3. **Auth-Core back after an outage** — the answer stays `503` (not `401`, so the user stays signed in) until 10 seconds
   after the failed fetch, then `200`. [Task 1, Task 3; e2e step 6]
4. **A valid token whose `org_id` is not a UUID, and an id in another spelling** (`{…}`, no hyphens, `urn:uuid:`, a leading
   space, SQL text): `401` and `404`, never a `500`, never another company's row. [Task 5]
5. **Odd headers and a token on the edge of expiry** — two `Authorization` headers (even identical and valid), `bEaReR`,
   trailing text, a token that expired 200 seconds ago (valid) and 400 seconds ago (not), a product that forgot
   `auth.install(app)`. [Task 2]

## File Structure

```
.env.example                                    + NOTES_DB_PASSWORD
.gitattributes                                  + LF for the files that go into Linux containers
.dockerignore                                   + __pycache__, .pytest_cache, .venv
README.md                                       the sample and the tests in the quickstart
clients/python/                                 the package (Tasks 1–3)
  pyproject.toml                                hatchling, src layout; fastapi and PyJWT[crypto]; pytest config
  src/auth_core_fastapi/
    __init__.py  py.typed                       AuthCore, AuthError, JwksCache, Principal, __version__
    _principal.py                               Principal
    _errors.py                                  AuthError, handle_auth_error
    _jwks.py                                    JwksCache, KeyUnknown, KeysUnavailable, fetch_jwks
    _verify.py                                  bearer_token, verify_token, TokenRejected
    _core.py                                    AuthCore: install, current_user, require_permission
  tests/
    helpers.py  conftest.py                     keys, tokens, FakeJwks, JwksServer, a clock; the app and its fixtures
    test_jwks_cache.py                          the cache by itself (Task 1)
    test_core.py  test_tokens.py  test_logging.py   the dependencies, criteria 2, 3, 4, 6, 7 (Task 2)
    test_keys.py  test_server.py                the key rules through the dependencies, over HTTP (Task 3)
samples/notes-api/                              the sample (Tasks 4–6)
  auth.yaml  requirements.txt  pytest.ini  alembic.ini
  migrations/env.py  script.py.mako  versions/0001_create_notes.py
  src/notes_api/__init__.py  settings.py  db.py  app.py  main.py
  tests/conftest.py  test_migration_and_settings.py  test_notes.py
  Dockerfile  Caddyfile  compose.yml            the image, the proxy, the overlay
scripts/e2e-notes.sh                            the six steps of the spec over the real network (Task 7)
docs/integration/python-fastapi.md              the guide (Task 8)
docs/superpowers/plans/0006-acceptance-map.md   criteria → tests (Task 9)
```

Test conventions used below: `helpers.py` holds the tools (`Signer`: an RSA key with a `kid` that signs tokens and publishes
its JWK; `FakeJwks`: the JWKS URL, counting fetches, failing when told to; `Clock`: moved by hand; `JwksServer`: a real
server on 127.0.0.1; `assert_unauthorized`, `assert_unavailable`); `conftest.py` holds the fixtures `key1`, `key2`,
`stranger` (a key that is not published, with the `kid` of one that is), `jwks`, `clock`, and from Task 2 `auth` and `client`
(a FastAPI app with `GET /me`, `POST /write` and `GET /ping`).

---

## Acceptance criteria of the spec, and where each is built and guarded

Every criterion 1–10 of the spec is built by the tasks named and guarded by the tests named (the class and file names are
the main guards; **the full table, test by test, is the content of `docs/superpowers/plans/0006-acceptance-map.md`, which
Task 9 produces**). A criterion with no guard would be a finding, so the tests that guard a criterion say so in a
`# criterion N` comment.

| # | Criterion (short) | Built in | Guarded by |
| - | ----------------- | -------- | ---------- |
| 1 | The Goal sequence and the six steps of `scripts/e2e-notes.sh` pass on a clean stack; the earlier e2e scripts still pass without the overlay | 6, 7 | `scripts/e2e-notes.sh`; the five existing scripts unedited, on a compose file this slice does not change |
| 2 | A token that meets every rule is accepted; the `Principal` carries its claims | 2 | `test_tokens`, `test_server` |
| 3 | `401`, empty body, `WWW-Authenticate: Bearer` for each listed case | 2 | `test_tokens`, `test_jwks_cache.test_only_rsa_keys_are_taken_from_the_key_set`; e2e 2 |
| 4 | `403 {"error":"forbidden"}` without the permission; with it, through | 2, 5 | `test_tokens`, `test_notes`; e2e 4 |
| 5 | Keys: one refetch for a new key; at most one per 10 seconds; held keys survive a failing URL; `503` otherwise | 1, 3 | `test_jwks_cache`, `test_keys`, `test_server`; e2e 6 |
| 6 | Creating the object makes no network call | 1, 2 | `test_core`, `test_jwks_cache` |
| 7 | No token or claim value in the logs at any level | 2 | `test_logging` |
| 8 | The sample never returns or changes a note of another company; another company's note and a non-UUID id are `404` | 5 | `test_notes`; e2e 3 |
| 9 | The package's tests run with `pytest`, no Docker, keys generated in the test | 1–3 | all of `clients/python/tests/` |
| 10 | The guide covers the eight steps; every file it points at exists | 8 | the checks of Task 8, step 3 |

---

## Which tasks need what

| Group | Task | Needs Docker | Needs slice 5 (spec 0005) code to run |
| ----- | ---- | ------------ | ------------------------------------- |
| A — the package | 1 The key cache | no | no |
| A | 2 Token checks and the dependencies | no | no |
| A | 3 The key rules through the dependencies, over HTTP | no | no |
| B — the sample | 4 Settings, database and the first migration | no (PostgreSQL optional) | no |
| B | 5 The endpoints | no (PostgreSQL optional) | no |
| B | 6 The image, the proxy, the overlay | yes | no (the checks use Auth-Core as it is on this branch; the overlay is complete without slice 5) |
| C — the e2e, the guide, the verifiers | 7 `scripts/e2e-notes.sh` | yes | **the run: yes** (steps 3–6 use the company API and the CLI); writing it and its static checks: no |
| C | 8 The integration guide and the README | no | no |
| C | 9 Acceptance map and the final gate | yes | **the e2e run and the regression pass: yes** |

Groups A and B can be implemented and gated on this branch as it is. Task 7's run and Task 9's gate need spec 0005 built:
merge it (or `main`, once it holds it) into this branch first. Nothing in this plan edits a file that slice 5 edits, except
the four insertions into `README.md` of Task 8, which are placed by an anchor line and not by a line number.

---

## Group A — the package

When the group is done, `cd clients/python && python -m pytest -q` runs 131 tests with no Docker, and a FastAPI product can
check Auth-Core's tokens with `AuthCore`. Every task leaves the suite green.

### Task 1: The key cache

**Files:**
- Create: `clients/python/pyproject.toml`, `clients/python/src/auth_core_fastapi/__init__.py`,
  `clients/python/src/auth_core_fastapi/py.typed` (empty), `clients/python/src/auth_core_fastapi/_jwks.py`
- Test: `clients/python/tests/helpers.py`, `clients/python/tests/conftest.py`, `clients/python/tests/test_jwks_cache.py`

**Interfaces:**
- Produces: `JwksCache(url, *, ttl=300.0, min_interval=10.0, timeout=5.0, clock=time.monotonic, fetch=fetch_jwks)` with
  `key_for(kid: str)` → the public key object (what `jwt.decode` takes as its key), or raises `KeyUnknown` (the latest fetch
  worked and the key set has no such key: a `401`) or `KeysUnavailable` (the key is not held and the latest fetch failed: a
  `503`). `fetch(url, timeout)` returns the parsed JSON of the key set; `clock()` returns seconds. Creating a `JwksCache`
  makes no network call. Also `fetch_jwks(url, timeout)` (HTTP GET, at most 1 MiB, parsed JSON, at most `timeout` seconds **in all**), the constants `TTL_SECONDS`,
  `MIN_INTERVAL_SECONDS`, `TIMEOUT_SECONDS`, `MAX_BYTES`, and the two exceptions.
- Produces (tests): `helpers.Signer(kid)` (`.kid`, `.jwk`, `.public_pem`, `.claims(**overrides)`, `.token(headers=None,
  algorithm="RS256", **overrides)`; the override value `MISSING` leaves a claim or header out), `helpers.FakeJwks(*signers)`
  (a `fetch` function; `.publish(*signers)`, `.fails`, `.fetches`), `helpers.Clock` (`.advance(seconds)`),
  `helpers.SlowJwks`, `helpers.JwksServer(document)` (`.url`, `.status`, `.delay`, `.trickle`, `.requests`, `.close()`; usable in a `with`), `jws`,
  `hs256_with_public_key`, `with_changed_signature`, `with_changed_payload`, `bearer`, `assert_unauthorized`,
  `assert_unavailable`, and the fixtures `key1`, `key2`, `stranger`, `jwks`, `clock`.

**Why a cache of our own and not `PyJWKClient`:** see "Verified before this plan was written". The rules it implements
are the "Keys" bullet of the Global Constraints, one for one; the comment next to each test says which criterion it guards.

- [ ] **Step 1: The virtual environment and the empty package.** Make the venv at a short path with the real Python 3.12,
  then the project file, an empty `py.typed` and a package that only knows its version, and install the package in it with
  its test dependencies (the packages are approved: FastAPI, PyJWT with `cryptography`, pytest, httpx; hatchling builds it).

```bash
"C:/Users/mwppl/AppData/Local/Programs/Python/Python312/python.exe" -m venv C:/p6v
source C:/p6v/Scripts/activate
mkdir -p clients/python/src/auth_core_fastapi clients/python/tests
: > clients/python/src/auth_core_fastapi/py.typed
```

`clients/python/pyproject.toml`:

```toml
[build-system]
requires = ["hatchling>=1.27"]
build-backend = "hatchling.build"

[project]
name = "auth-core-fastapi"
version = "0.1.0"
description = "Verify Auth-Core access tokens in a FastAPI backend"
requires-python = ">=3.12"
dependencies = ["fastapi>=0.115", "PyJWT[crypto]>=2.8"]

[project.optional-dependencies]
test = ["pytest>=8", "httpx>=0.27"]

[tool.hatch.build.targets.wheel]
packages = ["src/auth_core_fastapi"]

[tool.pytest.ini_options]
testpaths = ["tests"]
pythonpath = ["src"]
```

`clients/python/src/auth_core_fastapi/__init__.py` (Task 2 replaces it with the full one):

```python
"""Auth-Core access tokens in a FastAPI backend."""

__version__ = "0.1.0"
```

```bash
pip install -e "clients/python[test]"
python -c "import auth_core_fastapi; print(auth_core_fastapi.__version__)"
```

  Expected: `0.1.0`.

- [ ] **Step 2: Write the tests and their tools.**

`clients/python/tests/helpers.py`:

```python
"""Test tools: keys made here, tokens signed here, stand-ins for the JWKS URL, a clock we move by hand."""

import base64
import hashlib
import hmac
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import jwt
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa

ISSUER = "http://localhost:8088/auth"
AUDIENCE = "notes-api"
JWKS_URL = "http://auth.test/auth/.well-known/jwks.json"

MISSING = object()  # as a claim or header override: leave it out


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def b64url_int(number: int) -> str:
    return b64url(number.to_bytes((number.bit_length() + 7) // 8, "big"))


def b64url_decode(text: str) -> bytes:
    return base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))


class Signer:
    """An RSA key with a `kid`: signs tokens, and publishes its public half as a JWK."""

    def __init__(self, kid: str) -> None:
        self.kid = kid
        self.private_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        numbers = self.private_key.public_key().public_numbers()
        self.jwk = {
            "kty": "RSA", "use": "sig", "alg": "RS256", "kid": kid,
            "n": b64url_int(numbers.n), "e": b64url_int(numbers.e),
        }
        self.public_pem = self.private_key.public_key().public_bytes(
            serialization.Encoding.PEM, serialization.PublicFormat.SubjectPublicKeyInfo
        )

    def claims(self, **overrides) -> dict:
        now = int(time.time())
        claims = {
            "iss": ISSUER, "aud": AUDIENCE, "sub": "user-1", "org_id": "11111111-1111-1111-1111-111111111111",
            "roles": ["user"], "permissions": ["notes:read", "notes:write"], "iat": now, "exp": now + 600,
        }
        for name, value in overrides.items():
            if value is MISSING:
                claims.pop(name, None)
            else:
                claims[name] = value
        return claims

    def token(self, headers: dict | None = None, algorithm: str = "RS256", **overrides) -> str:
        header = {"kid": self.kid, "typ": "at+jwt"}
        header.update(headers or {})
        header = {name: value for name, value in header.items() if value is not MISSING}
        return jwt.encode(self.claims(**overrides), self.private_key, algorithm=algorithm, headers=header)


def jws(header: dict, payload: dict, signature: bytes = b"") -> str:
    """A token made by hand, for the ones PyJWT will not sign."""
    parts = [b64url(json.dumps(header).encode()), b64url(json.dumps(payload).encode())]
    return ".".join(parts) + "." + b64url(signature)


def hs256_with_public_key(signer: Signer, **overrides) -> str:
    """The algorithm-confusion token: HS256, the public key as the secret."""
    header = {"alg": "HS256", "typ": "at+jwt", "kid": signer.kid}
    signing_input = ".".join(
        [b64url(json.dumps(header).encode()), b64url(json.dumps(signer.claims(**overrides)).encode())]
    )
    mac = hmac.new(signer.public_pem, signing_input.encode(), hashlib.sha256).digest()
    return signing_input + "." + b64url(mac)


def with_changed_signature(token: str) -> str:
    head, payload, signature = token.split(".")
    raw = bytearray(b64url_decode(signature))
    raw[0] ^= 0xFF
    return ".".join([head, payload, b64url(bytes(raw))])


def with_changed_payload(token: str, **changes) -> str:
    head, payload, signature = token.split(".")
    claims = json.loads(b64url_decode(payload))
    claims.update(changes)
    return ".".join([head, b64url(json.dumps(claims).encode()), signature])


class FakeJwks:
    """Stands for the JWKS URL: counts the fetches, serves the key set it is given, fails when told to."""

    def __init__(self, *signers: Signer) -> None:
        self.publish(*signers)
        self.fails = False
        self.fetches = 0

    def publish(self, *signers: Signer) -> None:
        self.document = {"keys": [signer.jwk for signer in signers]}

    def __call__(self, url: str, timeout: float) -> dict:
        self.fetches += 1
        if self.fails:
            raise OSError("the key set is down")
        return self.document


class Clock:
    def __init__(self) -> None:
        self.now = 1000.0

    def __call__(self) -> float:
        return self.now

    def advance(self, seconds: float) -> None:
        self.now += seconds


def bearer(token: str) -> dict:
    return {"Authorization": f"Bearer {token}"}


def assert_unauthorized(response) -> None:
    assert response.status_code == 401
    assert response.content == b""
    assert response.headers["www-authenticate"] == "Bearer"
    assert response.headers["cache-control"] == "no-store"


def assert_unavailable(response) -> None:
    assert response.status_code == 503
    assert response.content == b'{"error":"auth_unavailable"}'
    assert response.headers["content-type"] == "application/json"
    assert response.headers["cache-control"] == "no-store"


class SlowJwks(FakeJwks):
    def __init__(self, *signers):
        super().__init__(*signers)
        self.started = threading.Event()
        self.release = threading.Event()

    def __call__(self, url, timeout):
        self.started.set()
        assert self.release.wait(10)
        return super().__call__(url, timeout)


class JwksServer:
    """Serves `document` at /jwks, after `delay` seconds, with `status`, one byte every `trickle` seconds if that is set."""

    def __init__(self, document: dict) -> None:
        self.document = document
        self.status = 200
        self.delay = 0.0
        self.trickle = 0.0
        self.requests = 0
        server = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                server.requests += 1
                time.sleep(server.delay)
                body = json.dumps(server.document).encode()
                self.send_response(server.status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                try:
                    if server.trickle:
                        for index in range(len(body)):
                            self.wfile.write(body[index:index + 1])
                            self.wfile.flush()
                            time.sleep(server.trickle)
                    else:
                        self.wfile.write(body)
                except OSError:
                    pass  # the client gave up

        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.httpd.server_port}/jwks"
        threading.Thread(target=self.httpd.serve_forever, args=(0.05,), daemon=True).start()

    def close(self) -> None:
        self.httpd.shutdown()
        self.httpd.server_close()

    def __enter__(self) -> "JwksServer":
        return self

    def __exit__(self, *exc_info) -> None:
        self.close()
```

`clients/python/tests/conftest.py` (Task 2 adds the app and two fixtures to it):

```python
import pytest

from helpers import Clock, FakeJwks, Signer


@pytest.fixture(scope="session")
def key1() -> Signer:
    return Signer("k1")


@pytest.fixture(scope="session")
def key2() -> Signer:
    return Signer("k2")


@pytest.fixture(scope="session")
def stranger() -> Signer:
    """A key that is not published, with the `kid` of one that is."""
    return Signer("k1")


@pytest.fixture
def jwks(key1) -> FakeJwks:
    return FakeJwks(key1)


@pytest.fixture
def clock() -> Clock:
    return Clock()
```

`clients/python/tests/test_jwks_cache.py`:

```python
"""The key cache by itself (criteria 5 and 6): a fake JWKS URL and a clock moved by hand, then a real server on 127.0.0.1."""

import json
import threading
import time

import pytest

from auth_core_fastapi import _jwks
from auth_core_fastapi._jwks import JwksCache, KeysUnavailable, KeyUnknown
from helpers import JWKS_URL, FakeJwks, JwksServer, SlowJwks


def cache_of(jwks, clock) -> JwksCache:
    return JwksCache(JWKS_URL, clock=clock, fetch=jwks)


def test_the_defaults_are_the_rules_of_the_spec():
    assert _jwks.TTL_SECONDS == 300.0
    assert _jwks.MIN_INTERVAL_SECONDS == 10.0
    assert _jwks.TIMEOUT_SECONDS == 5.0


# --- criterion 6: nothing is fetched until a key is needed ----------------------------------------------------------------


def test_creating_the_cache_fetches_nothing(jwks, clock):  # criterion 6
    cache_of(jwks, clock)

    assert jwks.fetches == 0


def test_the_keys_are_fetched_on_first_use_and_kept(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)

    for _ in range(3):
        assert cache.key_for("k1") is not None

    assert jwks.fetches == 1


# --- criterion 5: a new key after one refetch, at most every 10 seconds, whatever the number of unknown kids --------------


def test_a_new_key_is_accepted_after_one_refetch(jwks, clock, key1, key2):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")
    jwks.publish(key1, key2)
    clock.advance(10)

    assert cache.key_for("k2") is not None
    assert jwks.fetches == 2


def test_a_new_key_is_not_looked_for_within_ten_seconds_of_the_last_fetch(jwks, clock, key1, key2):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")
    jwks.publish(key1, key2)
    clock.advance(9.9)

    with pytest.raises(KeyUnknown):
        cache.key_for("k2")
    assert jwks.fetches == 1

    clock.advance(0.1)
    assert cache.key_for("k2") is not None
    assert jwks.fetches == 2


def test_many_unknown_kids_cause_one_fetch_in_ten_seconds(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")

    for number in range(30):
        clock.advance(0.3)
        with pytest.raises(KeyUnknown):
            cache.key_for(f"stray-{number}")
    assert jwks.fetches == 1  # nine seconds on: none of the thirty caused a fetch

    clock.advance(1.1)
    for number in range(30):
        with pytest.raises(KeyUnknown):
            cache.key_for(f"more-{number}")
    assert jwks.fetches == 2  # the first of the next thirty did, the other twenty-nine did not


def test_a_key_that_left_the_key_set_stops_working_at_the_next_fetch(jwks, clock, key1, key2):  # criterion 5
    jwks.publish(key1, key2)
    cache = cache_of(jwks, clock)
    cache.key_for("k2")
    jwks.publish(key2)
    clock.advance(301)

    assert cache.key_for("k2") is not None  # this call renews the keys
    with pytest.raises(KeyUnknown):
        cache.key_for("k1")


def test_the_keys_are_fetched_again_after_five_minutes_not_before(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")

    clock.advance(299)
    cache.key_for("k1")
    assert jwks.fetches == 1

    clock.advance(1)
    cache.key_for("k1")
    assert jwks.fetches == 2


# --- criterion 5: held keys keep working while the fetch fails -----------------------------------------------------------


def test_held_keys_keep_working_while_the_fetch_fails(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")
    jwks.fails = True

    for _ in range(5):
        clock.advance(301)
        assert cache.key_for("k1") is not None

    assert jwks.fetches == 6  # one each time the keys were old: a failed fetch is retried, not given up on


def test_a_failed_renewal_is_retried_no_sooner_than_ten_seconds_later(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    cache.key_for("k1")
    jwks.fails = True
    clock.advance(301)

    for _ in range(20):
        assert cache.key_for("k1") is not None
    assert jwks.fetches == 2

    clock.advance(10)
    assert cache.key_for("k1") is not None
    assert jwks.fetches == 3


# --- criterion 5: an unknown key is unavailable when the latest fetch failed, unknown when it worked ----------------------


def test_no_key_and_a_failing_key_set_is_unavailable(jwks, clock):  # criterion 5
    jwks.fails = True

    with pytest.raises(KeysUnavailable):
        cache_of(jwks, clock).key_for("k1")


def test_unavailable_stays_the_answer_for_ten_seconds_without_another_fetch(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    jwks.fails = True
    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")
    jwks.fails = False

    clock.advance(9)
    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")
    assert jwks.fetches == 1

    clock.advance(1)
    assert cache.key_for("k1") is not None
    assert jwks.fetches == 2


def test_the_answer_to_an_unknown_kid_names_the_state_of_the_latest_fetch(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    with pytest.raises(KeyUnknown):
        cache.key_for("nope")

    jwks.fails = True
    clock.advance(10)
    with pytest.raises(KeysUnavailable):
        cache.key_for("nope")

    jwks.fails = False
    clock.advance(10)
    with pytest.raises(KeyUnknown):
        cache.key_for("nope")


def test_unavailable_does_not_need_the_failed_fetch_to_have_run_for_the_request(jwks, clock):  # criterion 5
    cache = cache_of(jwks, clock)
    jwks.fails = True
    with pytest.raises(KeysUnavailable):
        cache.key_for("first")

    clock.advance(5)
    with pytest.raises(KeysUnavailable):
        cache.key_for("second")
    assert jwks.fetches == 1


def test_a_key_set_with_nothing_usable_counts_as_a_failed_fetch(clock, key1):  # criterion 5
    hmac_only = {"keys": [{"kty": "oct", "kid": "k9", "k": "AAAA"}]}
    served = [{"keys": [key1.jwk]}, {"keys": []}, "not a key set", hmac_only]
    cache = JwksCache(JWKS_URL, clock=clock, fetch=lambda url, timeout: served.pop(0))
    assert cache.key_for("k1") is not None

    for _ in range(3):
        clock.advance(301)
        assert cache.key_for("k1") is not None  # a broken key set leaves the held keys as they were
        clock.advance(10)
        with pytest.raises(KeysUnavailable):
            cache.key_for("unknown")
    assert served == []


def test_only_rsa_keys_are_taken_from_the_key_set(clock, key1):  # criterion 3: algorithm confusion
    mixed = {"keys": [{"kty": "oct", "kid": "k9", "k": "AAAA"}, key1.jwk]}
    cache = JwksCache(JWKS_URL, clock=clock, fetch=lambda url, timeout: mixed)

    assert cache.key_for("k1") is not None
    clock.advance(10)
    with pytest.raises(KeyUnknown):
        cache.key_for("k9")


# --- one fetch at a time, and nobody who holds the key waits for it -------------------------------------------------------


def test_requests_that_wait_for_the_keys_share_one_fetch(key1):  # criterion 5
    slow = SlowJwks(key1)
    cache = JwksCache(JWKS_URL, fetch=slow)
    results = []

    def ask():
        results.append(cache.key_for("k1") is not None)

    threads = [threading.Thread(target=ask) for _ in range(8)]
    for thread in threads:
        thread.start()
    assert slow.started.wait(10)
    time.sleep(0.2)  # the others are now waiting at the lock
    slow.release.set()
    for thread in threads:
        thread.join(10)

    assert results == [True] * 8
    assert slow.fetches == 1


def test_a_request_that_holds_its_key_does_not_wait_for_a_fetch_that_is_running(key1, clock):
    slow = SlowJwks(key1)
    slow.release.set()
    cache = JwksCache(JWKS_URL, clock=clock, fetch=slow)
    assert cache.key_for("k1") is not None
    slow.release.clear()
    slow.started.clear()
    clock.advance(301)
    renewing = threading.Thread(target=lambda: cache.key_for("k1"))
    renewing.start()
    assert slow.started.wait(10)

    started = time.perf_counter()
    assert cache.key_for("k1") is not None
    waited = time.perf_counter() - started

    slow.release.set()
    renewing.join(10)
    assert waited < 1
    assert slow.fetches == 2


# --- the real fetch, against a server on 127.0.0.1 ------------------------------------------------------------------------


@pytest.fixture
def server(key1):
    server = JwksServer({"keys": [key1.jwk]})
    yield server
    server.close()


def test_the_keys_are_fetched_over_http(server):  # criterion 5
    cache = JwksCache(server.url)

    assert cache.key_for("k1") is not None
    assert cache.key_for("k1") is not None
    assert server.requests == 1


@pytest.mark.parametrize("status", [404, 500, 503])
def test_an_error_status_is_a_failed_fetch(server, status):  # criterion 5
    server.status = status

    with pytest.raises(KeysUnavailable):
        JwksCache(server.url).key_for("k1")


def test_an_answer_that_is_not_a_key_set_is_a_failed_fetch(server):  # criterion 5
    server.document = {"keys": "no"}

    with pytest.raises(KeysUnavailable):
        JwksCache(server.url).key_for("k1")


def padded(server, size: int) -> int:
    """Make the key set a valid JSON document of about `size` bytes (the keys stay usable). Returns its length."""
    server.document = {"keys": server.document["keys"], "pad": "x" * size}
    return len(json.dumps(server.document))


def test_a_key_set_just_under_the_size_limit_is_fetched(server):  # criterion 5
    length = padded(server, _jwks.MAX_BYTES - 1000)
    assert length <= _jwks.MAX_BYTES

    assert JwksCache(server.url).key_for("k1") is not None


def test_a_valid_key_set_over_the_size_limit_is_refused_for_its_size(server):  # criterion 5
    length = padded(server, _jwks.MAX_BYTES)
    assert length > _jwks.MAX_BYTES  # valid JSON with a usable key: nothing but its size is wrong with it

    with pytest.raises(ValueError, match="too large"):
        _jwks.fetch_jwks(server.url, 5)
    with pytest.raises(KeysUnavailable):
        JwksCache(server.url).key_for("k1")


def test_nobody_listening_is_a_failed_fetch():  # criterion 5
    with pytest.raises(KeysUnavailable):
        JwksCache("http://127.0.0.1:9/jwks").key_for("k1")  # nothing listens on the discard port


def test_the_fetch_gives_up_at_its_timeout(server):  # criterion 5
    server.delay = 3
    cache = JwksCache(server.url, timeout=0.5)

    started = time.perf_counter()
    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")

    assert time.perf_counter() - started < 2.5


def test_a_server_that_trickles_the_answer_is_given_up_on_at_the_deadline_of_the_whole_fetch(server):  # criterion 5
    server.trickle = 0.1  # a byte every 0.1 s: no single wait is long, the whole transfer takes minutes
    started = time.perf_counter()

    with pytest.raises(TimeoutError):
        _jwks.fetch_jwks(server.url, 1.0)

    assert 0.9 < time.perf_counter() - started < 2.0


def test_the_deadline_reaches_the_cache_as_an_unavailable_key_set(server):  # criterion 5
    server.trickle = 0.1
    cache = JwksCache(server.url, timeout=0.5)
    started = time.perf_counter()

    with pytest.raises(KeysUnavailable):
        cache.key_for("k1")

    assert time.perf_counter() - started < 1.5
```

- [ ] **Step 3: Run them and confirm they fail.**

```bash
cd clients/python && python -m pytest -q
```

  Expected: FAIL at collection, `ImportError: cannot import name '_jwks' from 'auth_core_fastapi'`.

- [ ] **Step 4: Implement.** `clients/python/src/auth_core_fastapi/_jwks.py`:

```python
"""The signing keys of the instance: fetched on first use, kept in memory, renewed on a schedule."""

import json
import logging
import threading
import time
import urllib.request
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

from jwt import PyJWK, PyJWKSet

log = logging.getLogger(__name__)

TTL_SECONDS = 300.0  # the keys are fetched again after this long
MIN_INTERVAL_SECONDS = 10.0  # at most one fetch, failed or not, in this long
TIMEOUT_SECONDS = 5.0
MAX_BYTES = 1_048_576


class KeyUnknown(Exception):
    """The latest fetch worked and the key set has no such key: the token is a 401."""


class KeysUnavailable(Exception):
    """The key is not held and the latest fetch failed: a 503, the user stays signed in."""


def fetch_jwks(url: str, timeout: float) -> Any:
    """GET the key set as JSON. Gives up after `timeout` seconds **in all**, however slowly the server sends.

    The socket timeout of `urlopen` bounds each wait, not the whole transfer: a server that sends a byte a second would
    never trip it. So the transfer runs in a worker thread that the caller stops waiting for at the deadline; the
    thread notices at its next chunk and ends.
    """
    outcome: dict[str, Any] = {}
    finished = threading.Event()
    cancelled = threading.Event()

    def work() -> None:
        try:
            outcome["document"] = _download(url, timeout, cancelled)
        except BaseException as exc:  # handed to the caller below
            outcome["error"] = exc
        finally:
            finished.set()

    threading.Thread(target=work, name="auth-core-jwks-fetch", daemon=True).start()
    if not finished.wait(timeout):
        cancelled.set()
        raise TimeoutError("the key set was not fetched in time")
    if "error" in outcome:
        raise outcome["error"]
    return outcome["document"]


def _download(url: str, timeout: float, cancelled: threading.Event) -> Any:
    body = bytearray()
    with urllib.request.urlopen(url, timeout=timeout) as response:
        while chunk := response.read1(8192):  # what has arrived, not a wait for 8192 bytes
            if cancelled.is_set():
                raise TimeoutError("the key set was not fetched in time")
            body += chunk
            if len(body) > MAX_BYTES:
                raise ValueError("the key set is too large")
    return json.loads(bytes(body))


@dataclass(frozen=True)
class _Snapshot:
    keys: dict[str, PyJWK]  # by kid
    fetched_at: float | None  # of the last fetch that worked


class JwksCache:
    """Keys by `kid`. Creating it makes no network call.

    A fetch happens on first use, when the keys are older than `ttl`, and when a token names a `kid` that is not
    held, but never twice within `min_interval` (a failed fetch counts). A failed fetch keeps the keys already held.
    One fetch runs at a time; a request that finds its key held never waits for it.
    """

    def __init__(
        self,
        url: str,
        *,
        ttl: float = TTL_SECONDS,
        min_interval: float = MIN_INTERVAL_SECONDS,
        timeout: float = TIMEOUT_SECONDS,
        clock: Callable[[], float] = time.monotonic,
        fetch: Callable[[str, float], Any] = fetch_jwks,
    ) -> None:
        self._url = url
        self._ttl = ttl
        self._min_interval = min_interval
        self._timeout = timeout
        self._clock = clock
        self._fetch = fetch
        self._snapshot = _Snapshot({}, None)
        self._attempted_at: float | None = None
        self._last_ok = False
        self._lock = threading.Lock()

    def key_for(self, kid: str) -> Any:
        snapshot = self._snapshot
        held = snapshot.keys.get(kid)
        if held is not None and self._is_fresh(snapshot, self._clock()):
            return held.key
        if held is not None:
            # Held but old: one request renews the keys, the others carry on with what they hold.
            if not self._lock.acquire(blocking=False):
                return held.key
        else:
            self._lock.acquire()
        try:
            self._renew(kid)
            found = self._snapshot.keys.get(kid)
            last_ok = self._last_ok
        finally:
            self._lock.release()
        if found is not None:
            return found.key
        raise KeyUnknown() if last_ok else KeysUnavailable()

    def _is_fresh(self, snapshot: _Snapshot, now: float) -> bool:
        return snapshot.fetched_at is not None and now - snapshot.fetched_at < self._ttl

    def _renew(self, kid: str) -> None:  # called with the lock held
        now = self._clock()
        if self._attempted_at is not None and now - self._attempted_at < self._min_interval:
            return
        snapshot = self._snapshot
        if kid in snapshot.keys and self._is_fresh(snapshot, now):
            return
        self._attempted_at = now
        try:
            keys = _parse(self._fetch(self._url, self._timeout))
        except Exception as exc:  # any failure keeps the keys already held
            self._last_ok = False
            log.warning("the key set could not be fetched (%s)", type(exc).__name__)
            return
        self._snapshot = _Snapshot(keys, now)
        self._last_ok = True


def _parse(document: Any) -> dict[str, PyJWK]:
    jwks = PyJWKSet.from_dict(document)  # raises when no key in it is usable
    keys = {key.key_id: key for key in jwks.keys if key.key_id and key.key_type == "RSA"}
    if not keys:
        raise ValueError("the key set holds no RSA key with a kid")
    return keys
```

  Three decisions in it, so that nobody "simplifies" them away. A failed fetch counts toward the 10 seconds (`_attempted_at` is
  set before the fetch), so a dead Auth-Core is asked once in 10 seconds and not once per request. A request whose key is held
  but old tries the lock without waiting (`acquire(blocking=False)`) and carries on with the key it holds when someone else is
  already fetching: during an outage no request that holds its key waits for a 5-second timeout. A request whose key is not
  held waits for the fetch that is running and then reads its result, which is how eight waiting requests share one fetch.
  The 5-second timeout is a deadline for the **whole** fetch: `urlopen`'s own timeout bounds each wait on the socket, so a
  server that sends a byte a second would never trip it. The transfer therefore runs in a worker thread that the caller stops
  waiting for at the deadline (`finished.wait(timeout)`); the thread sees `cancelled` at its next chunk and ends. Chunks are
  read with `read1`, which returns what has arrived and does not wait for 8 KiB. The size limit (1 MiB) is checked as the
  chunks come in.
  The key set must hold an **RSA** key with a `kid`; anything else is treated as a failed fetch (a key set of `oct` keys would
  otherwise be the material of an algorithm-confusion attack).

- [ ] **Step 5: Run them and confirm they pass.**

```bash
cd clients/python && python -m pytest -q
```

  Expected: `29 passed`.

- [ ] **Step 6: Hand over** — leave the changes uncommitted. The orchestrator commits
  `clients/python/pyproject.toml`, `clients/python/src/auth_core_fastapi/__init__.py`,
  `clients/python/src/auth_core_fastapi/py.typed`, `clients/python/src/auth_core_fastapi/_jwks.py` and `clients/python/tests/`
  as `feat(clients): key cache for the Python package`.

### Task 2: The token checks and the two dependencies

**Files:**
- Create: `clients/python/src/auth_core_fastapi/_principal.py`, `clients/python/src/auth_core_fastapi/_errors.py`,
  `clients/python/src/auth_core_fastapi/_verify.py`, `clients/python/src/auth_core_fastapi/_core.py`
- Modify: `clients/python/src/auth_core_fastapi/__init__.py` (replaced whole), `clients/python/tests/conftest.py`
- Test: `clients/python/tests/test_core.py`, `clients/python/tests/test_tokens.py`, `clients/python/tests/test_logging.py`

**Interfaces:**
- Consumes: `JwksCache.key_for(kid)`, `KeyUnknown`, `KeysUnavailable` (Task 1); the test tools of Task 1.
- Produces: `Principal(sub: str, org_id: str, roles: tuple[str, ...], permissions: frozenset[str])` (frozen dataclass);
  `AuthError(status_code: int, error: str | None = None)` (an `HTTPException` with `Cache-Control: no-store`, and
  `WWW-Authenticate: Bearer` for `401`); `AuthCore(issuer: str, audience: str, jwks_url: str | None = None, *,
  jwks_cache: JwksCache | None = None)` with `install(app: FastAPI) -> None`, `current_user(request: Request) -> Principal`
  (a plain `def`) and `require_permission(permission: str) -> Callable[..., Principal]`; `bearer_token(headers: list[str])
  -> str` and `verify_token(token, keys, issuer, audience) -> Principal`, both raising `TokenRejected(reason)`. The package
  exports `AuthCore`, `AuthError`, `JwksCache`, `Principal` and `__version__`.
- Produces (tests): the fixtures `auth` and `client`, and `conftest.build_app(auth)` (the app of the tests: `GET /me`,
  `POST /write` guarded by `notes:write`, `GET /ping`).

**The decisions the spec leaves open, and what this task does:**
- The empty `401` and the `{"error": …}` bodies need a handler, which FastAPI does not give a dependency. So the
  dependencies raise `AuthError`, and `auth.install(app)` (one line at startup) registers the handler. Without the call the
  status codes and headers are still right (FastAPI's own `{"detail": …}` body is sent), and a test pins that.
- `Cache-Control: no-store` is on the empty `401` too (the spec says it of JSON bodies; Auth-Core puts it on every answer).
- `typ` must be exactly `at+jwt`; `aud` may be a list that holds the configured audience (the JWT rule of PyJWT); `iat` is
  required and a token issued more than 5 minutes in the future is refused.
- `require_permission` is an `async def`: it only looks up a set, and a plain `def` would cost a second thread-pool hop.
- The permission a test or an endpoint requires is never logged, nor is anything of the token: the debug line of a rejected
  token is a fixed word or the name of PyJWT's exception class.

- [ ] **Step 1: Write the tests.** `clients/python/tests/conftest.py` gains the app and the two fixtures (the change):

```diff
--- a/clients/python/tests/conftest.py
+++ b/clients/python/tests/conftest.py
@@ -1,6 +1,32 @@
 import pytest
+from fastapi import Depends, FastAPI
+from fastapi.testclient import TestClient
 
-from helpers import Clock, FakeJwks, Signer
+from auth_core_fastapi import AuthCore, JwksCache, Principal
+from helpers import AUDIENCE, ISSUER, JWKS_URL, Clock, FakeJwks, Signer
+
+
+def build_app(auth: AuthCore) -> FastAPI:
+    app = FastAPI()
+    auth.install(app)
+
+    @app.get("/me")
+    def me(user: Principal = Depends(auth.current_user)):
+        return {
+            "sub": user.sub, "org_id": user.org_id, "roles": list(user.roles),
+            "permissions": sorted(user.permissions),
+            "types": [type(user.roles).__name__, type(user.permissions).__name__],
+        }
+
+    @app.post("/write")
+    def write(user: Principal = Depends(auth.require_permission("notes:write"))):
+        return {"sub": user.sub}
+
+    @app.get("/ping")
+    async def ping():
+        return {"ok": True}
+
+    return app
 
 
 @pytest.fixture(scope="session")
@@ -27,3 +53,13 @@
 @pytest.fixture
 def clock() -> Clock:
     return Clock()
+
+
+@pytest.fixture
+def auth(jwks, clock) -> AuthCore:
+    return AuthCore(ISSUER, AUDIENCE, JWKS_URL, jwks_cache=JwksCache(JWKS_URL, clock=clock, fetch=jwks))
+
+
+@pytest.fixture
+def client(auth) -> TestClient:
+    return TestClient(build_app(auth))
```

`clients/python/tests/test_core.py`:

```python
"""What `AuthCore` is given: the key set URL, and that creating it touches no network (criterion 6)."""

import pytest

from auth_core_fastapi import AuthCore
from helpers import AUDIENCE, ISSUER


def test_creating_the_object_makes_no_network_call(monkeypatch):  # criterion 6
    def no_network(*args, **kwargs):
        raise AssertionError("the network was used")

    monkeypatch.setattr("urllib.request.urlopen", no_network)
    monkeypatch.setattr("socket.socket.connect", no_network)
    monkeypatch.setattr("socket.getaddrinfo", no_network)

    AuthCore(ISSUER, AUDIENCE)
    AuthCore(ISSUER, AUDIENCE, "http://auth:8080/auth/.well-known/jwks.json")


def test_the_default_key_set_url_is_the_issuer_plus_the_well_known_path():
    assert AuthCore("https://app.example.com/auth", "x")._keys._url == "https://app.example.com/auth/.well-known/jwks.json"
    assert AuthCore("https://app.example.com/auth/", "x")._keys._url == "https://app.example.com/auth/.well-known/jwks.json"
    assert AuthCore("https://app.example.com/auth", "x", "http://auth:8080/k")._keys._url == "http://auth:8080/k"


@pytest.mark.parametrize("url", ["file:///etc/passwd", "ftp://example.com/jwks", "jwks.json"])
def test_a_key_set_url_that_is_not_http_is_refused(url):
    with pytest.raises(ValueError):
        AuthCore(ISSUER, AUDIENCE, url)


@pytest.mark.parametrize("issuer, audience", [("", "notes-api"), (ISSUER, "")])
def test_an_empty_issuer_or_audience_is_refused(issuer, audience):
    with pytest.raises(ValueError):
        AuthCore(issuer, audience)


def test_an_empty_permission_is_refused():
    with pytest.raises(ValueError):
        AuthCore(ISSUER, AUDIENCE).require_permission("")
```

`clients/python/tests/test_tokens.py`:

```python
"""Criteria 2, 3 and 4: which tokens are accepted, which are a 401, and what `require_permission` answers."""

import time

import pytest
from fastapi import Depends, FastAPI
from fastapi.testclient import TestClient

from helpers import (
    MISSING, JwksServer, Signer, assert_unauthorized, bearer, hs256_with_public_key, jws, with_changed_payload,
    with_changed_signature,
)


# --- criterion 2: a token that meets every rule is accepted and says who the caller is ----------------------------------


def test_valid_token_gives_the_principal_of_its_claims(client, key1):  # criterion 2
    token = key1.token(
        sub="user-42", org_id="22222222-2222-2222-2222-222222222222",
        roles=["admin", "auditor"], permissions=["notes:read", "notes:write", "notes:read"],
    )

    response = client.get("/me", headers=bearer(token))

    assert response.status_code == 200
    assert response.json() == {
        "sub": "user-42", "org_id": "22222222-2222-2222-2222-222222222222",
        "roles": ["admin", "auditor"], "permissions": ["notes:read", "notes:write"],
        "types": ["tuple", "frozenset"],
    }


@pytest.mark.parametrize("scheme", ["Bearer", "bearer", "BEARER", "bEaReR"])
def test_the_scheme_is_case_insensitive(client, key1, scheme):  # criterion 2
    response = client.get("/me", headers={"Authorization": f"{scheme} {key1.token()}"})

    assert response.status_code == 200


def test_a_token_without_roles_or_permissions_is_valid_and_holds_none(client, key1):  # criterion 2
    response = client.get("/me", headers=bearer(key1.token(roles=[], permissions=[])))

    assert response.status_code == 200
    assert response.json()["roles"] == []
    assert response.json()["permissions"] == []


def test_an_audience_list_that_holds_the_configured_audience_is_valid(client, key1):  # criterion 2
    response = client.get("/me", headers=bearer(key1.token(aud=["other-api", "notes-api"])))

    assert response.status_code == 200


def test_a_token_expired_less_than_five_minutes_ago_is_still_valid(client, key1):  # criterion 2, Decision 9
    now = int(time.time())

    response = client.get("/me", headers=bearer(key1.token(iat=now - 900, exp=now - 200)))

    assert response.status_code == 200


# --- criterion 3: everything else is a 401 with an empty body and WWW-Authenticate: Bearer -----------------------------


def test_no_header_is_a_401(client):  # criterion 3
    assert_unauthorized(client.get("/me"))


@pytest.mark.parametrize("value", ["Basic dXNlcjpwYXNz", "Token abc.def.ghi", "Bearer", "Bearer ", "abc.def.ghi"])
def test_a_scheme_other_than_bearer_or_no_token_is_a_401(client, value):  # criterion 3
    assert_unauthorized(client.get("/me", headers={"Authorization": value}))


def test_two_authorization_headers_are_a_401_even_when_both_are_valid(client, key1):  # criterion 3
    token = key1.token()

    response = client.get("/me", headers=[("Authorization", f"Bearer {token}"), ("Authorization", f"Bearer {token}")])

    assert_unauthorized(response)


@pytest.mark.parametrize("value", ["Bearer  {t}", "Bearer {t} extra", "Bearer {t},", 'Bearer "{t}"'])
def test_a_header_that_is_not_bearer_and_one_token_is_a_401(client, key1, value):  # criterion 3
    assert_unauthorized(client.get("/me", headers={"Authorization": value.format(t=key1.token())}))


@pytest.mark.parametrize("token", ["abc", "a.b", "a.b.c", "....", "e30.e30.", "not-a-token", "e30.e30.e30"])
def test_a_token_that_is_not_a_jws_is_a_401(client, token):  # criterion 3
    assert_unauthorized(client.get("/me", headers={"Authorization": "Bearer " + token}))


def test_alg_none_is_a_401(client, key1):  # criterion 3
    token = jws({"alg": "none", "typ": "at+jwt", "kid": "k1"}, key1.claims())

    assert_unauthorized(client.get("/me", headers=bearer(token)))


def test_hs256_signed_with_the_public_key_is_a_401(client, key1):  # criterion 3: algorithm confusion
    assert_unauthorized(client.get("/me", headers=bearer(hs256_with_public_key(key1))))


@pytest.mark.parametrize("algorithm", ["RS384", "RS512", "PS256"])
def test_an_algorithm_other_than_rs256_is_a_401_even_with_a_good_signature(client, key1, algorithm):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(algorithm=algorithm))))


@pytest.mark.parametrize("typ", ["JWT", "at+JWT", "application/at+jwt", "", MISSING])
def test_a_type_other_than_at_jwt_is_a_401(client, key1, typ):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(headers={"typ": typ}))))


@pytest.mark.parametrize("kid", ["", MISSING])
def test_a_token_without_a_usable_kid_is_a_401(client, key1, kid):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(headers={"kid": kid}))))


def test_a_kid_that_is_not_text_is_a_401(client, key1):  # criterion 3
    token = jws({"alg": "RS256", "typ": "at+jwt", "kid": 7}, key1.claims(), b"signature")

    assert_unauthorized(client.get("/me", headers=bearer(token)))


def test_a_changed_signature_is_a_401(client, key1):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(with_changed_signature(key1.token()))))


def test_a_changed_payload_is_a_401(client, key1):  # criterion 3
    token = with_changed_payload(key1.token(), permissions=["notes:write", "everything"])

    assert_unauthorized(client.get("/me", headers=bearer(token)))


def test_a_token_signed_by_another_key_with_a_published_kid_is_a_401(client, stranger):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(stranger.token())))


@pytest.mark.parametrize("kid", ["k1", "evil"])
def test_the_key_is_chosen_by_kid_alone_whatever_the_header_offers(client, key1, kid):  # criterion 3
    """A token of an attacker's key that brings its own key (`jwk`, `x5c`) or says where to find it (`jku`, `x5u`)."""
    evil = Signer("evil")
    with JwksServer({"keys": [evil.jwk]}) as attackers_key_set:
        headers = {"jwk": evil.jwk, "jku": attackers_key_set.url, "x5u": attackers_key_set.url, "x5c": ["MIIB"], "kid": kid}

        response = client.get("/me", headers=bearer(evil.token(headers=headers)))

        assert_unauthorized(response)
        assert attackers_key_set.requests == 0  # nobody went to fetch the key the token pointed at


def test_a_wrong_issuer_is_a_401(client, key1):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(iss="http://elsewhere.test/auth"))))


def test_a_wrong_audience_is_a_401(client, key1):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(aud="another-api"))))


def test_a_token_expired_more_than_five_minutes_ago_is_a_401(client, key1):  # criterion 3, Decision 9
    now = int(time.time())

    assert_unauthorized(client.get("/me", headers=bearer(key1.token(iat=now - 1000, exp=now - 400))))


@pytest.mark.parametrize("claim", ["exp", "iat", "iss", "aud"])
def test_a_token_without_a_registered_claim_is_a_401(client, key1, claim):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(**{claim: MISSING}))))


def test_a_token_issued_in_the_future_is_a_401(client, key1):  # criterion 3
    now = int(time.time())

    assert_unauthorized(client.get("/me", headers=bearer(key1.token(iat=now + 1000, exp=now + 1600))))


@pytest.mark.parametrize(
    "claim, value",
    [
        ("sub", MISSING), ("sub", ""), ("sub", 7), ("sub", ["u"]),
        ("org_id", MISSING), ("org_id", ""), ("org_id", 7), ("org_id", None),
        ("roles", MISSING), ("roles", "admin"), ("roles", [1]), ("roles", ["admin", None]), ("roles", {"admin": True}),
        ("permissions", MISSING), ("permissions", "notes:read"), ("permissions", [True]), ("permissions", None),
    ],
)
def test_a_missing_claim_or_one_of_the_wrong_type_is_a_401(client, key1, claim, value):  # criterion 3
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(**{claim: value}))))


def test_a_valid_token_in_another_header_is_a_401(client, key1):  # criterion 3
    assert_unauthorized(client.get("/me", headers={"X-Authorization": f"Bearer {key1.token()}"}))


# --- criterion 4: the permission ------------------------------------------------------------------------------------


def test_a_valid_token_without_the_permission_is_a_403(client, key1):  # criterion 4
    response = client.post("/write", headers=bearer(key1.token(permissions=["notes:read"])))

    assert response.status_code == 403
    assert response.content == b'{"error":"forbidden"}'
    assert response.headers["content-type"] == "application/json"
    assert response.headers["cache-control"] == "no-store"
    assert "www-authenticate" not in response.headers


def test_a_token_with_the_permission_is_let_through(client, key1):  # criterion 4
    response = client.post("/write", headers=bearer(key1.token(sub="writer")))

    assert response.status_code == 200
    assert response.json() == {"sub": "writer"}


@pytest.mark.parametrize("held", [["notes:writer"], ["notes"], ["NOTES:WRITE"], ["notes:write "], ["*"]])
def test_the_permission_is_matched_whole(client, key1, held):  # criterion 4
    response = client.post("/write", headers=bearer(key1.token(permissions=held)))

    assert response.status_code == 403


def test_an_invalid_token_is_a_401_before_it_is_a_403(client, key1):  # criterion 4
    assert_unauthorized(client.post("/write"))
    assert_unauthorized(client.post("/write", headers=bearer(with_changed_signature(key1.token(permissions=[])))))


def test_without_install_the_status_and_the_headers_are_still_right(auth, key1):
    app = FastAPI()  # auth.install(app) is not called

    @app.get("/me")
    def me(user=Depends(auth.require_permission("notes:write"))):
        return {}

    client = TestClient(app)

    missing = client.get("/me")
    forbidden = client.get("/me", headers=bearer(key1.token(permissions=[])))

    assert missing.status_code == 401
    assert missing.headers["www-authenticate"] == "Bearer"
    assert forbidden.status_code == 403
    assert forbidden.headers["cache-control"] == "no-store"
```

`clients/python/tests/test_logging.py`:

```python
"""Criterion 7: no token and no claim value in the logs, at any level; a rejected token is logged at debug, by reason."""

import logging
import time

from helpers import bearer, hs256_with_public_key, with_changed_signature

SECRETS = {
    "sub": "sub-4f7a9c1e-secret",
    "org_id": "0a1b2c3d-org-secret",
    "roles": ["role-secret-aaa"],
    "permissions": ["perm-secret:read", "perm-secret:write"],
}


def secret_token(signer, headers=None, **overrides) -> str:
    return signer.token(headers=headers, **{**SECRETS, **overrides})


def _everything_logged(caplog) -> str:
    return "\n".join(
        " ".join([record.name, record.getMessage(), str(record.args), str(record.exc_info), str(record.exc_text)])
        for record in caplog.records
    )


def test_no_token_and_no_claim_value_is_logged_at_any_level(client, jwks, clock, key1, caplog):  # criterion 7
    caplog.set_level(logging.DEBUG)
    now = int(time.time())
    tokens = [
        secret_token(key1),                                          # accepted
        secret_token(key1, permissions=["perm-secret:read"]),        # accepted, then refused (403) below
        with_changed_signature(secret_token(key1)),                  # signature
        secret_token(key1, aud="someone-else"),                      # audience
        secret_token(key1, iss="http://elsewhere.test/auth"),        # issuer
        secret_token(key1, iat=now - 1000, exp=now - 400),           # expired
        secret_token(key1, roles="role-secret-aaa"),                 # claim of the wrong type
        secret_token(key1, headers={"typ": "JWT"}),                  # type
        secret_token(key1, headers={"kid": "kid-secret-zzz"}),       # unknown key
        hs256_with_public_key(key1, **SECRETS),                    # algorithm
    ]
    for token in tokens:
        client.get("/me", headers=bearer(token))
        client.post("/write", headers=bearer(token))
    jwks.fails = True
    clock.advance(10)
    client.get("/me", headers=bearer(secret_token(key1, headers={"kid": "kid-secret-yyy"})))  # key set down

    logged = _everything_logged(caplog)

    for token in tokens:
        assert token not in logged
        assert token.split(".")[1] not in logged  # not the payload on its own either
    for value in ("sub-4f7a9c1e-secret", "0a1b2c3d-org-secret", "role-secret-aaa", "perm-secret", "kid-secret"):
        assert value not in logged
    assert "Bearer" not in logged
    assert "authorization" not in logged.lower()


def test_a_rejected_token_is_logged_at_debug_with_the_reason_only(client, key1, caplog):  # criterion 7
    caplog.set_level(logging.DEBUG, logger="auth_core_fastapi")

    client.get("/me", headers=bearer(with_changed_signature(key1.token())))
    client.get("/me")

    messages = [(record.levelno, record.getMessage()) for record in caplog.records if record.name.startswith("auth_core")]
    assert (logging.DEBUG, "token rejected: InvalidSignatureError") in messages
    assert (logging.DEBUG, "token rejected: no_authorization_header") in messages


def test_a_rejected_token_is_not_logged_above_debug(client, key1, caplog):  # criterion 7
    caplog.set_level(logging.INFO, logger="auth_core_fastapi")

    client.get("/me", headers=bearer(with_changed_signature(key1.token())))

    assert [record for record in caplog.records if record.name.startswith("auth_core")] == []


def test_a_failed_fetch_is_logged_as_a_warning_by_the_class_of_the_error_only(client, jwks, key1, caplog):
    caplog.set_level(logging.INFO, logger="auth_core_fastapi")
    jwks.fails = True

    client.get("/me", headers=bearer(key1.token()))

    warnings = [record.getMessage() for record in caplog.records if record.levelno == logging.WARNING]
    assert warnings == ["the key set could not be fetched (OSError)"]
```

- [ ] **Step 2: Run them and confirm they fail.**

```bash
(cd clients/python && python -m pytest -q)
```

  Expected: FAIL while loading `conftest.py`, `ImportError: cannot import name 'AuthCore' from 'auth_core_fastapi'`.

- [ ] **Step 3: Implement.** `clients/python/src/auth_core_fastapi/_principal.py`:

```python
from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class Principal:
    """The caller of a request, as its access token says: who, in which company, with what rights."""

    sub: str
    org_id: str
    roles: tuple[str, ...]
    permissions: frozenset[str]
```

`clients/python/src/auth_core_fastapi/_errors.py`:

```python
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
```

`clients/python/src/auth_core_fastapi/_verify.py`:

```python
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
    return _principal(claims)


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
```

`clients/python/src/auth_core_fastapi/_core.py`:

```python
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
```

`clients/python/src/auth_core_fastapi/__init__.py` (replaces the one of Task 1):

```python
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

__version__ = "0.1.0"

__all__ = ["AuthCore", "AuthError", "JwksCache", "Principal", "__version__"]
```

  Two things in `_verify.py` that are easy to break. The checks of `alg`, `typ` and `kid` come **before** the key is looked
  up, so a token that is wrong in any of those never causes a fetch and is a `401` even while Auth-Core is down; and
  `algorithms=["RS256"]` stays in the `jwt.decode` call even though the header was checked, so that an `HS256` token signed
  with the public key is refused twice. The key is chosen by `kid` among the keys of the configured key set and by nothing
  else in the token (`jku`, `jwk` and `x5u` are never looked at).

- [ ] **Step 4: Run them and confirm they pass.**

```bash
(cd clients/python && python -m pytest -q)
```

  Expected: `120 passed` (29 of Task 1, 91 new).

- [ ] **Step 5: Prove that the tests bite.** Make each of these one-line changes in `src/auth_core_fastapi`, run the suite,
  and put the line back. Each must fail the test named (more may fail with it).

| Change | Fails |
| ------ | ----- |
| `_verify.py`: `LEEWAY_SECONDS = 0` | `test_a_token_expired_less_than_five_minutes_ago_is_still_valid` |
| `_verify.py`: delete `issuer=issuer,` from `jwt.decode` | `test_a_wrong_issuer_is_a_401` |
| `_verify.py`: `if header.get("typ") != TOKEN_TYPE:` → `if False:` | `test_a_type_other_than_at_jwt_is_a_401` |
| `_verify.py`: `if len(authorization_headers) > 1:` → `if False:` | `test_two_authorization_headers_are_a_401_even_when_both_are_valid` |
| `_verify.py`: `key = keys.key_for(kid)` → `key = jwt.PyJWK(header["jwk"]).key if "jwk" in header else keys.key_for(kid)` | `test_the_key_is_chosen_by_kid_alone_whatever_the_header_offers` |
| `_core.py`: add `request.headers.get("authorization")` to the `log.debug` of a rejected token | `test_no_token_and_no_claim_value_is_logged_at_any_level` |

- [ ] **Step 6: Hand over** — uncommitted. The orchestrator commits the four new modules, the
  new `__init__.py`, `clients/python/tests/` as `feat(clients): current_user and require_permission for FastAPI`.

### Task 3: The key rules through the dependencies, and over HTTP

**Files:**
- Test: `clients/python/tests/test_keys.py`, `clients/python/tests/test_server.py`

**Interfaces:**
- Consumes: everything of Tasks 1 and 2. No production code is written in this task: it pins criterion 5 as the caller
  sees it (what a request gets while the keys change or cannot be fetched), the real fetch through `AuthCore`, and the event
  loop. `JwksServer` of Task 1 is the server.

- [ ] **Step 1: Write the tests.** `clients/python/tests/test_keys.py`:

```python
"""Criterion 5 through the dependencies: what a request gets while the keys change or cannot be fetched.

The cache rules themselves (10 seconds, 5 minutes, held keys) are pinned in test_jwks_cache.py; this file pins how they
reach the caller: a 401 for a token whose key is not in the key set, a 503 when the key set cannot be had.
"""

from helpers import assert_unauthorized, assert_unavailable, bearer


def test_the_keys_are_fetched_on_first_use_and_kept(client, jwks, key1):  # criterion 5
    assert jwks.fetches == 0

    for _ in range(3):
        assert client.get("/me", headers=bearer(key1.token())).status_code == 200

    assert jwks.fetches == 1


def test_a_token_signed_by_a_new_key_is_accepted_after_one_refetch(client, jwks, clock, key1, key2):  # criterion 5
    assert client.get("/me", headers=bearer(key1.token())).status_code == 200
    jwks.publish(key1, key2)
    clock.advance(10)

    response = client.get("/me", headers=bearer(key2.token()))

    assert response.status_code == 200
    assert jwks.fetches == 2


def test_a_token_of_a_new_key_is_a_401_until_ten_seconds_after_the_last_fetch(client, jwks, clock, key1, key2):  # criterion 5
    assert client.get("/me", headers=bearer(key1.token())).status_code == 200
    jwks.publish(key1, key2)
    clock.advance(9.9)

    assert_unauthorized(client.get("/me", headers=bearer(key2.token())))
    assert jwks.fetches == 1

    clock.advance(0.1)
    assert client.get("/me", headers=bearer(key2.token())).status_code == 200


def test_tokens_with_unknown_kids_cause_at_most_one_fetch_in_ten_seconds(client, jwks, clock, key1):  # criterion 5
    assert client.get("/me", headers=bearer(key1.token())).status_code == 200

    for number in range(20):
        clock.advance(0.4)
        assert_unauthorized(client.get("/me", headers=bearer(key1.token(headers={"kid": f"stray-{number}"}))))

    assert jwks.fetches == 1


def test_held_keys_keep_working_while_the_fetch_fails(client, jwks, clock, key1):  # criterion 5
    assert client.get("/me", headers=bearer(key1.token())).status_code == 200
    jwks.fails = True

    for _ in range(3):
        clock.advance(301)
        assert client.get("/me", headers=bearer(key1.token())).status_code == 200


def test_no_key_and_a_failing_key_set_is_a_503(client, jwks, key1):  # criterion 5
    jwks.fails = True

    assert_unavailable(client.get("/me", headers=bearer(key1.token())))
    assert_unavailable(client.post("/write", headers=bearer(key1.token())))


def test_an_unknown_kid_is_a_503_when_the_latest_fetch_failed_and_a_401_when_it_worked(client, jwks, clock, key1):  # criterion 5
    assert client.get("/me", headers=bearer(key1.token())).status_code == 200
    unknown = key1.token(headers={"kid": "unknown"})

    jwks.fails = True
    clock.advance(10)
    assert_unavailable(client.get("/me", headers=bearer(unknown)))

    jwks.fails = False
    clock.advance(10)
    assert_unauthorized(client.get("/me", headers=bearer(unknown)))


def test_a_token_that_is_invalid_for_another_reason_is_a_401_even_when_the_key_set_is_down(client, jwks, key1):  # criterion 5
    jwks.fails = True

    assert_unauthorized(client.get("/me"))
    assert_unauthorized(client.get("/me", headers=bearer("abc.def.ghi")))
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(headers={"typ": "JWT"}))))
    assert jwks.fetches == 0
```

`clients/python/tests/test_server.py`:

```python
"""The dependencies against a key set served over real HTTP, and the event loop."""

import asyncio
import time

import httpx
import pytest
from fastapi.testclient import TestClient

from auth_core_fastapi import AuthCore
from conftest import build_app
from helpers import AUDIENCE, ISSUER, JwksServer, assert_unauthorized, assert_unavailable, bearer


@pytest.fixture
def server(key1):
    server = JwksServer({"keys": [key1.jwk]})
    yield server
    server.close()


def test_a_token_is_checked_against_the_keys_served_over_http(server, key1):  # criterion 2
    client = TestClient(build_app(AuthCore(ISSUER, AUDIENCE, server.url)))

    assert client.get("/me", headers=bearer(key1.token())).status_code == 200
    assert client.get("/me", headers=bearer(key1.token())).status_code == 200

    assert server.requests == 1


def test_an_unknown_kid_is_a_401_and_a_key_set_that_does_not_answer_is_a_503(server, key1):  # criterion 5
    client = TestClient(build_app(AuthCore(ISSUER, AUDIENCE, server.url)))
    assert_unauthorized(client.get("/me", headers=bearer(key1.token(headers={"kid": "other"}))))

    server.status = 500
    down = TestClient(build_app(AuthCore(ISSUER, AUDIENCE, server.url)))
    assert_unavailable(down.get("/me", headers=bearer(key1.token())))


def test_a_slow_fetch_does_not_block_the_event_loop(server, key1):
    """A request that waits for the key set must not hold up the others (the dependencies are plain `def`)."""
    server.delay = 1.5
    app = build_app(AuthCore(ISSUER, AUDIENCE, server.url))

    async def scenario():
        transport = httpx.ASGITransport(app=app)
        async with httpx.AsyncClient(transport=transport, base_url="http://test") as client:
            started = time.perf_counter()
            slow = asyncio.create_task(client.get("/me", headers=bearer(key1.token())))
            await asyncio.sleep(0.3)  # the slow request is now inside the fetch
            await client.get("/ping")
            ping_done = time.perf_counter() - started
            response = await slow
            return ping_done, time.perf_counter() - started, response.status_code

    ping_done, slow_done, status = asyncio.run(scenario())

    assert status == 200
    assert slow_done > 1.4
    assert ping_done < 0.8
```

- [ ] **Step 2: Run them.**

```bash
(cd clients/python && python -m pytest -q)
```

  Expected: `131 passed` (120 of Tasks 1 and 2, 11 new). These tests pass at once, as they pin behaviour that Tasks 1 and 2
  built, so they are proved the other way: make each of these changes, see the named test fail, put the line back.

| Change | Fails |
| ------ | ----- |
| `_jwks.py`: `if self._attempted_at is not None and now - self._attempted_at < self._min_interval:` → `if False:` | `test_tokens_with_unknown_kids_cause_at_most_one_fetch_in_ten_seconds` (and, in `test_jwks_cache.py`, `test_many_unknown_kids_cause_one_fetch_in_ten_seconds`) |
| `_jwks.py`: in `_renew`'s `except`, add `self._snapshot = _Snapshot({}, None)` | `test_held_keys_keep_working_while_the_fetch_fails` |
| `_core.py`: `raise AuthError(503, "auth_unavailable")` → `raise AuthError(401)` | `test_no_key_and_a_failing_key_set_is_a_503`, `test_an_unknown_kid_is_a_401_and_a_key_set_that_does_not_answer_is_a_503` |
| `_core.py`: make `current_user` an `async def` (the blocking fetch now runs on the loop) | `test_a_slow_fetch_does_not_block_the_event_loop` |

- [ ] **Step 3: Hand over** — uncommitted. The orchestrator commits `clients/python/tests/test_keys.py`
  and `clients/python/tests/test_server.py` as `test(clients): key rules through the dependencies and over HTTP`.

---

## Group B — the sample product, its migration, the overlay

When the group is done, `cd samples/notes-api && python -m pytest -q` runs 57 tests with no Docker, and
`docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env up -d --build` brings up
Auth-Core, the sample and Caddy on one origin. Nothing in the group needs the code of spec 0005 to be written or gated;
the stack comes up on this branch with the Auth-Core of slices 1–4, where a login works and the sample answers `401` (the
token has no `org_id` yet).

### Task 4: Settings, the database and the first migration

**Files:**
- Create: `samples/notes-api/requirements.txt`, `samples/notes-api/pytest.ini`, `samples/notes-api/alembic.ini`,
  `samples/notes-api/migrations/env.py`, `samples/notes-api/migrations/script.py.mako`,
  `samples/notes-api/migrations/versions/0001_create_notes.py`, `samples/notes-api/src/notes_api/__init__.py`,
  `samples/notes-api/src/notes_api/settings.py`, `samples/notes-api/src/notes_api/db.py`
- Test: `samples/notes-api/tests/test_migration_and_settings.py`

**Interfaces:**
- Consumes: nothing of the package (the sample's tests of Task 5 do).
- Produces: `Settings(auth_issuer, auth_audience, auth_jwks_url, database_url)` with `Settings.from_env(env=os.environ)`
  (`RuntimeError` naming the missing variable, never its value); `Base`, `Note` (`id`, `org_id`, `author_sub`, `text`,
  `created_at`); `make_engine(database_url) -> Engine`; `upgrade_database(engine) -> None` (Alembic to `head`, with the
  service's logging left alone); the revision `0001`.

**The revision id is `0001`** (`alembic revision --rev-id 0001`, whose file name is `0001_create_notes.py`): an id that
Alembic does not draw at random, so that the file name and the history are the same on every machine. The docstring of the
file has no `Create Date:` line (the template of `alembic revision` writes one; Alembic never reads it).

- [ ] **Step 1: The project files.** `samples/notes-api/requirements.txt`:

```text
# The sample's own dependencies. The package itself is not listed: a product installs it from a tag of the Auth-Core
# repository with this line (docs/integration/python-fastapi.md, step 1):
#
#   auth-core-fastapi @ git+https://github.com/MckCieply/Auth-Core@python-v0.1.0#subdirectory=clients/python
#
# The Dockerfile of this sample installs it from the clone (clients/python) instead, so that the sample builds
# from one clone, with no tag and no GitHub.
uvicorn>=0.30
SQLAlchemy>=2.0
alembic>=1.13
psycopg[binary]>=3.2
```

`samples/notes-api/pytest.ini`:

```ini
[pytest]
testpaths = tests
pythonpath = src
```

```bash
source C:/p6v/Scripts/activate        # the venv of Task 1
pip install -r samples/notes-api/requirements.txt
mkdir -p samples/notes-api/src/notes_api samples/notes-api/migrations/versions samples/notes-api/tests
```

- [ ] **Step 2: Write the tests.** `samples/notes-api/tests/test_migration_and_settings.py`:

```python
"""The migration makes what the model says, and the settings come from the environment."""

import pytest
from alembic.autogenerate import compare_metadata
from alembic.migration import MigrationContext
from sqlalchemy import create_engine, inspect

from notes_api.db import Base, upgrade_database
from notes_api.settings import Settings


def test_the_migration_creates_the_table_of_the_spec(tmp_path):
    engine = create_engine(f"sqlite:///{tmp_path / 'notes.db'}")

    upgrade_database(engine)

    columns = {column["name"]: column for column in inspect(engine).get_columns("notes")}
    assert list(columns) == ["id", "org_id", "author_sub", "text", "created_at"]
    assert not columns["org_id"]["nullable"]
    assert not columns["author_sub"]["nullable"]
    assert not columns["text"]["nullable"]
    assert [index["column_names"] for index in inspect(engine).get_indexes("notes")] == [["org_id"]]
    assert inspect(engine).get_pk_constraint("notes")["constrained_columns"] == ["id"]


def test_the_migration_and_the_model_agree(tmp_path):
    engine = create_engine(f"sqlite:///{tmp_path / 'notes.db'}")
    upgrade_database(engine)

    with engine.connect() as connection:
        differences = compare_metadata(MigrationContext.configure(connection), Base.metadata)

    assert differences == []


def test_migrating_twice_changes_nothing(tmp_path):
    engine = create_engine(f"sqlite:///{tmp_path / 'notes.db'}")

    upgrade_database(engine)
    upgrade_database(engine)

    assert inspect(engine).get_table_names() == ["alembic_version", "notes"]


def test_the_settings_are_read_from_the_environment():
    settings = Settings.from_env(
        {"AUTH_ISSUER": "http://localhost:8088/auth", "AUTH_AUDIENCE": "notes-api", "DATABASE_URL": "postgresql+psycopg://u:p@db/notes"}
    )

    assert settings.auth_jwks_url is None
    assert settings.database_url == "postgresql+psycopg://u:p@db/notes"
    assert Settings.from_env(
        {"AUTH_ISSUER": "i", "AUTH_AUDIENCE": "a", "AUTH_JWKS_URL": "http://auth:8080/auth/.well-known/jwks.json", "DATABASE_URL": "d"}
    ).auth_jwks_url == "http://auth:8080/auth/.well-known/jwks.json"


@pytest.mark.parametrize("missing", ["AUTH_ISSUER", "AUTH_AUDIENCE", "DATABASE_URL"])
def test_a_missing_setting_is_named_and_its_value_is_not(missing):
    env = {"AUTH_ISSUER": "issuer-value", "AUTH_AUDIENCE": "audience-value", "DATABASE_URL": "postgresql://user:password-value@db/notes"}
    del env[missing]

    with pytest.raises(RuntimeError) as error:
        Settings.from_env(env)

    assert missing in str(error.value)
    assert "password-value" not in str(error.value)
```

- [ ] **Step 3: Run them and confirm they fail.**

```bash
(cd samples/notes-api && python -m pytest -q)
```

  Expected: FAIL at collection, `ModuleNotFoundError: No module named 'notes_api'`.

- [ ] **Step 4: Implement.** `samples/notes-api/src/notes_api/__init__.py`:

```python
"""The "notes" sample: a small product that uses Auth-Core the way a real one would."""
```

`samples/notes-api/src/notes_api/settings.py`:

```python
import os
from collections.abc import Mapping
from dataclasses import dataclass


@dataclass(frozen=True)
class Settings:
    """What the service is told by its environment."""

    auth_issuer: str  # the `iss` of the tokens, e.g. http://localhost:8088/auth
    auth_audience: str  # their `aud`
    auth_jwks_url: str | None  # where the keys are; unset means issuer + /.well-known/jwks.json
    database_url: str

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
```

`samples/notes-api/src/notes_api/db.py`:

```python
import uuid
from datetime import datetime, timezone
from pathlib import Path

from alembic import command
from alembic.config import Config
from sqlalchemy import DateTime, Text, Uuid, create_engine
from sqlalchemy.engine import Engine
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column

# alembic.ini and migrations/ sit next to src/ (in the image: /app)
ROOT = Path(__file__).resolve().parents[2]


class Base(DeclarativeBase):
    pass


class Note(Base):
    """A note belongs to a company: `org_id` is the `org_id` claim of the token that wrote it."""

    __tablename__ = "notes"

    id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True, default=uuid.uuid4)
    org_id: Mapped[uuid.UUID] = mapped_column(Uuid, nullable=False, index=True)
    author_sub: Mapped[str] = mapped_column(Text, nullable=False)
    text: Mapped[str] = mapped_column(Text, nullable=False)
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=lambda: datetime.now(timezone.utc)
    )


def make_engine(database_url: str) -> Engine:
    # hide_parameters: an error message must not carry the text of a note into the log
    return create_engine(database_url, pool_pre_ping=True, hide_parameters=True)


def upgrade_database(engine: Engine) -> None:
    """Apply the Alembic migrations up to `head`. The service does this at startup."""
    config = Config(str(ROOT / "alembic.ini"))
    config.set_main_option("script_location", str(ROOT / "migrations"))
    config.attributes["configure_logger"] = False  # the service's logging stays as it is
    with engine.connect() as connection:
        config.attributes["connection"] = connection
        command.upgrade(config, "head")
        connection.commit()
```

`samples/notes-api/alembic.ini`:

```ini
# Alembic. The service applies the migrations itself at startup (src/notes_api/db.py).
# From a shell, in this directory:  DATABASE_URL=postgresql+psycopg://... alembic upgrade head
[alembic]
script_location = %(here)s/migrations
prepend_sys_path = src
path_separator = os

[loggers]
keys = root,sqlalchemy,alembic

[handlers]
keys = console

[formatters]
keys = generic

[logger_root]
level = WARNING
handlers = console
qualname =

[logger_sqlalchemy]
level = WARNING
handlers =
qualname = sqlalchemy.engine

[logger_alembic]
level = INFO
handlers =
qualname = alembic

[handler_console]
class = StreamHandler
args = (sys.stderr,)
level = NOTSET
formatter = generic

[formatter_generic]
format = %(levelname)-5.5s [%(name)s] %(message)s
datefmt = %H:%M:%S
```

`samples/notes-api/migrations/env.py`:

```python
import os
from logging.config import fileConfig

from alembic import context
from sqlalchemy import create_engine, pool

from notes_api.db import Base

config = context.config
# The service runs the migrations inside itself and keeps its own logging (it sets "configure_logger").
if config.config_file_name is not None and config.attributes.get("configure_logger", True):
    fileConfig(config.config_file_name)

target_metadata = Base.metadata


def run_migrations_offline() -> None:
    context.configure(
        url=os.environ["DATABASE_URL"],
        target_metadata=target_metadata,
        literal_binds=True,
        dialect_opts={"paramstyle": "named"},
    )
    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online() -> None:
    connection = config.attributes.get("connection")  # given by the service when it migrates itself
    if connection is not None:
        _migrate(connection)
        return
    engine = create_engine(os.environ["DATABASE_URL"], poolclass=pool.NullPool)
    with engine.connect() as connection:
        _migrate(connection)


def _migrate(connection) -> None:
    context.configure(connection=connection, target_metadata=target_metadata)
    with context.begin_transaction():
        context.run_migrations()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
```

`samples/notes-api/migrations/script.py.mako` (the template of the next revisions; the `alembic init` one without its
docstrings):

```text
"""${message}

Revision ID: ${up_revision}
Revises: ${down_revision | comma,n}
Create Date: ${create_date}

"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa
${imports if imports else ""}

# revision identifiers, used by Alembic.
revision: str = ${repr(up_revision)}
down_revision: Union[str, Sequence[str], None] = ${repr(down_revision)}
branch_labels: Union[str, Sequence[str], None] = ${repr(branch_labels)}
depends_on: Union[str, Sequence[str], None] = ${repr(depends_on)}


def upgrade() -> None:
    ${upgrades if upgrades else "pass"}


def downgrade() -> None:
    ${downgrades if downgrades else "pass"}
```

`samples/notes-api/migrations/versions/0001_create_notes.py`:

```python
"""create notes

Revision ID: 0001
Revises:

"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa

# revision identifiers, used by Alembic.
revision: str = "0001"
down_revision: Union[str, Sequence[str], None] = None
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.create_table(
        "notes",
        sa.Column("id", sa.Uuid(), nullable=False),
        sa.Column("org_id", sa.Uuid(), nullable=False),
        sa.Column("author_sub", sa.Text(), nullable=False),
        sa.Column("text", sa.Text(), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), server_default=sa.func.now(), nullable=False),
        sa.PrimaryKeyConstraint("id"),
    )
    op.create_index("ix_notes_org_id", "notes", ["org_id"])


def downgrade() -> None:
    op.drop_index("ix_notes_org_id", table_name="notes")
    op.drop_table("notes")
```

- [ ] **Step 5: Run them and confirm they pass.**

```bash
(cd samples/notes-api && python -m pytest -q)
```

  Expected: `7 passed`. (The sample's tests find `auth_core_fastapi` through the editable install of Task 1.)

- [ ] **Step 6 (needs Docker; once): the migration on PostgreSQL.** With a throwaway database and a name of its own:

```bash
docker run -d --name notes-pg-check -e POSTGRES_PASSWORD=check -e POSTGRES_DB=notes -p 127.0.0.1:0:5432 postgres:16-alpine
PORT="$(docker port notes-pg-check 5432 | sed 's/.*://')"
export DATABASE_URL="postgresql+psycopg://postgres:check@127.0.0.1:$PORT/notes"
sleep 6
(cd samples/notes-api && alembic upgrade head && alembic upgrade head --sql | tail -n 12 && alembic downgrade base && alembic upgrade head)
docker exec notes-pg-check psql -U postgres -d notes -c '\d notes'
```

  Expected: `Running upgrade  -> 0001, create notes` (twice, with the downgrade between), the SQL of the table with
  `org_id UUID NOT NULL` and `CREATE INDEX ix_notes_org_id`, and `\d notes` listing `id`, `org_id`, `author_sub`, `text`,
  `created_at` (`timestamp with time zone`, default `now()`) and the indexes `notes_pkey` and `ix_notes_org_id`. Keep the
  container for Task 5, step 6, then `docker rm -f notes-pg-check`.

- [ ] **Step 7: Hand over** — uncommitted: the files of this task, as
  `feat(samples): notes sample — settings, database and first migration`.

### Task 5: The endpoints

**Files:**
- Create: `samples/notes-api/src/notes_api/app.py`, `samples/notes-api/src/notes_api/main.py`
- Test: `samples/notes-api/tests/conftest.py`, `samples/notes-api/tests/test_notes.py`

**Interfaces:**
- Consumes: `Settings`, `Note`, `make_engine`, `upgrade_database` (Task 4); `AuthCore`, `AuthError`, `JwksCache`,
  `Principal` (Tasks 1–2).
- Produces: `create_app(settings, *, engine=None, auth=None, migrate=True) -> FastAPI` (with `migrate=True` the migrations
  run at startup, in the lifespan); `notes_api.main:app` (what Uvicorn runs; reads the environment when imported); the four
  endpoints of the spec. Test tools: `conftest.Signer.token(org_id, permissions, sub)`, the fixtures `engine` (SQLite in
  memory, built by the real migration; or the PostgreSQL of `NOTES_TEST_DATABASE_URL`), `settings`, `auth`, `client`,
  `headers(signer, org_id, permissions, sub)`.

**What the code does that the spec leaves to the sample:** the body of a `POST` is read by hand, so that every refusal is
`400 invalid_request` (FastAPI would answer `422`) and the `401` or `403` of the dependency comes first; the body is at most
16 KiB (a resource limit of the sample: the spec says nothing about the size of a body); a text with a NUL character or a lone surrogate (JSON allows both, PostgreSQL stores neither) is refused as invalid;
an id is found only in its 36-character written form; a valid token whose `org_id` is not a UUID is a `401` (it cannot
have been issued by Auth-Core for a product like this); `GET /api/health` answers `503 {"error":"database_unavailable"}`
when the database does not answer; every answer is `Cache-Control: no-store`; the interactive docs of FastAPI are off.
`GET /api/notes` has no paging, as in the spec: a product with many rows pages its lists (the guide says so).

- [ ] **Step 1: Write the tests.** `samples/notes-api/tests/conftest.py`:

```python
"""The service against SQLite in memory (the real migration builds the table) and keys made here: no Docker."""

import base64
import os
import time
import uuid

import jwt
import pytest
from auth_core_fastapi import AuthCore, JwksCache
from cryptography.hazmat.primitives.asymmetric import rsa
from fastapi.testclient import TestClient
from sqlalchemy import create_engine, text
from sqlalchemy.pool import StaticPool

from notes_api.app import create_app
from notes_api.db import upgrade_database
from notes_api.settings import Settings

ISSUER = "http://localhost:8088/auth"
AUDIENCE = "notes-api"
JWKS_URL = "http://auth.test/auth/.well-known/jwks.json"

COMPANY_A = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
COMPANY_B = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"


def _b64url(number: int) -> str:
    raw = number.to_bytes((number.bit_length() + 7) // 8, "big")
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()


class Signer:
    def __init__(self) -> None:
        self.key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
        numbers = self.key.public_key().public_numbers()
        self.jwk = {"kty": "RSA", "use": "sig", "alg": "RS256", "kid": "k1", "n": _b64url(numbers.n), "e": _b64url(numbers.e)}

    def token(self, org_id: str, permissions: list[str], sub: str = "user-1") -> str:
        now = int(time.time())
        claims = {
            "iss": ISSUER, "aud": AUDIENCE, "sub": sub, "org_id": org_id, "roles": [],
            "permissions": permissions, "iat": now, "exp": now + 600,
        }
        return jwt.encode(claims, self.key, algorithm="RS256", headers={"kid": "k1", "typ": "at+jwt"})


@pytest.fixture(scope="session")
def signer() -> Signer:
    return Signer()


@pytest.fixture
def engine():
    """SQLite in memory. With NOTES_TEST_DATABASE_URL set (an empty PostgreSQL database), the same tests run there."""
    url = os.environ.get("NOTES_TEST_DATABASE_URL")
    if url:
        engine = create_engine(url)
        _drop_everything(engine)
    else:
        engine = create_engine("sqlite://", poolclass=StaticPool, connect_args={"check_same_thread": False})
    upgrade_database(engine)  # the real migration builds the table
    yield engine
    if url:
        _drop_everything(engine)
    engine.dispose()


def _drop_everything(engine) -> None:
    with engine.begin() as connection:
        connection.execute(text("DROP TABLE IF EXISTS notes, alembic_version"))


@pytest.fixture
def settings() -> Settings:
    return Settings(auth_issuer=ISSUER, auth_audience=AUDIENCE, auth_jwks_url=None, database_url="sqlite://")


@pytest.fixture
def auth(signer) -> AuthCore:
    cache = JwksCache(JWKS_URL, fetch=lambda url, timeout: {"keys": [signer.jwk]})
    return AuthCore(ISSUER, AUDIENCE, JWKS_URL, jwks_cache=cache)


@pytest.fixture
def client(settings, engine, auth) -> TestClient:
    return TestClient(create_app(settings, engine=engine, auth=auth, migrate=False))


def headers(signer: Signer, org_id: str = COMPANY_A, permissions=("notes:read", "notes:write"), sub: str = "user-1") -> dict:
    return {"Authorization": "Bearer " + signer.token(org_id, list(permissions), sub)}


def new_id() -> str:
    return str(uuid.uuid4())
```

`samples/notes-api/tests/test_notes.py`:

```python
"""The three endpoints and the health check: what a company sees, what it cannot reach, what is refused."""

import json
import uuid
from datetime import datetime, timedelta, timezone

import pytest
from auth_core_fastapi import AuthCore, JwksCache
from fastapi.testclient import TestClient
from sqlalchemy import create_engine, event, func, select
from sqlalchemy.orm import Session

from conftest import COMPANY_A, COMPANY_B, headers
from notes_api.app import create_app
from notes_api.db import Note

BACKSLASH = chr(92)  # a JSON escape is built with it, so that no editing tool turns the escape into its character
READER = ("notes:read",)
WRITER = ("notes:write",)


def add(client, signer, text="hello", **kwargs):
    return client.post("/api/notes", json={"text": text}, headers=headers(signer, **kwargs))


# --- the endpoints, for one company --------------------------------------------------------------------------------------


def test_a_note_is_added_and_listed(client, signer):
    created = add(client, signer, "hello", sub="alice")

    assert created.status_code == 201
    note = created.json()
    assert set(note) == {"id", "text", "author_sub", "created_at"}
    assert uuid.UUID(note["id"])
    assert note["text"] == "hello"
    assert note["author_sub"] == "alice"
    assert datetime.fromisoformat(note["created_at"]).tzinfo is not None
    assert note["created_at"].endswith("Z")

    listed = client.get("/api/notes", headers=headers(signer))

    assert listed.status_code == 200
    assert listed.json() == [note]


def test_notes_are_listed_newest_first(client, signer, engine):
    start = datetime.now(timezone.utc) - timedelta(days=1)
    with Session(engine) as session:
        for number in range(3):
            session.add(Note(org_id=uuid.UUID(COMPANY_A), author_sub="u", text=f"note {number}", created_at=start + timedelta(hours=number)))
        session.commit()

    listed = client.get("/api/notes", headers=headers(signer))

    assert [note["text"] for note in listed.json()] == ["note 2", "note 1", "note 0"]


def test_a_note_is_read_by_its_id(client, signer):
    note = add(client, signer, "find me").json()

    found = client.get(f"/api/notes/{note['id']}", headers=headers(signer))

    assert found.status_code == 200
    assert found.json() == note
    assert client.get(f"/api/notes/{note['id'].upper()}", headers=headers(signer)).status_code == 200


def test_the_text_may_be_one_to_a_thousand_characters_of_any_script(client, signer):
    assert add(client, signer, "x").status_code == 201
    assert add(client, signer, "x" * 1000).status_code == 201
    unusual = "".join(map(chr, [0x17C, 0xF3, 0x142, 0x107, 0x1F600, 0x645]))  # Polish letters, an emoji, an Arabic letter
    assert add(client, signer, "text " + unusual).status_code == 201
    assert add(client, signer, "   ").status_code == 201


def test_every_answer_is_marked_never_to_be_stored(client, signer):
    for response in (
        add(client, signer),
        client.get("/api/notes", headers=headers(signer)),
        client.get(f"/api/notes/{uuid.uuid4()}", headers=headers(signer)),
        client.get("/api/notes"),
        client.post("/api/notes", json={}, headers=headers(signer)),
        client.post("/api/notes", json={"text": "x"}, headers=headers(signer, permissions=READER)),
        client.get("/api/health"),
    ):
        assert response.headers["cache-control"] == "no-store"


# --- criterion 8: another company's notes are out of reach -----------------------------------------------------------------


def test_another_companys_note_is_a_404_and_its_list_is_empty(client, signer):  # criterion 8
    note = add(client, signer, "secret of A").json()

    other = client.get(f"/api/notes/{note['id']}", headers=headers(signer, COMPANY_B))
    nobody = client.get(f"/api/notes/{uuid.uuid4()}", headers=headers(signer, COMPANY_B))

    assert other.status_code == 404
    assert other.json() == {"error": "not_found"}
    assert (other.status_code, other.content) == (nobody.status_code, nobody.content)  # the same as a note that is not there
    assert client.get("/api/notes", headers=headers(signer, COMPANY_B)).json() == []
    assert "secret of A" not in other.text


@pytest.mark.parametrize(
    "note_id",
    ["abc", "1", "' OR '1'='1", "%00", "{0}", "00000000000000000000000000000000", "urn:uuid:aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
     "{aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa}", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa-", "null"],
)
def test_an_id_that_is_not_a_uuid_is_a_404(client, signer, note_id):  # criterion 8
    response = client.get(f"/api/notes/{note_id}", headers=headers(signer))

    assert response.status_code == 404
    assert response.json() == {"error": "not_found"}


def test_only_the_written_form_of_an_id_finds_a_note(client, signer):  # criterion 8
    note = add(client, signer).json()

    for spelling in (note["id"].replace("-", ""), "{" + note["id"] + "}", "urn:uuid:" + note["id"], " " + note["id"]):
        assert client.get(f"/api/notes/{spelling}", headers=headers(signer)).status_code == 404


def test_the_company_of_a_new_note_is_the_tokens_whatever_the_body_says(client, signer, engine):  # criterion 8
    response = client.post(
        "/api/notes",
        json={"text": "mine", "org_id": COMPANY_B, "id": str(uuid.uuid4()), "author_sub": "someone-else"},
        headers=headers(signer, COMPANY_A, sub="alice"),
    )

    assert response.status_code == 201
    with Session(engine) as session:
        note = session.scalars(select(Note)).one()
    assert str(note.org_id) == COMPANY_A
    assert note.author_sub == "alice"
    assert str(note.id) == response.json()["id"] != ""
    assert client.get("/api/notes", headers=headers(signer, COMPANY_B)).json() == []


def test_two_companies_keep_their_own_notes(client, signer):  # criterion 8
    add(client, signer, "a1", org_id=COMPANY_A)
    add(client, signer, "b1", org_id=COMPANY_B)
    add(client, signer, "a2", org_id=COMPANY_A)

    assert [n["text"] for n in client.get("/api/notes", headers=headers(signer, COMPANY_A)).json()] == ["a2", "a1"]
    assert [n["text"] for n in client.get("/api/notes", headers=headers(signer, COMPANY_B)).json()] == ["b1"]


def test_every_statement_on_the_notes_table_names_the_company(settings, engine, auth, signer):  # criterion 8
    statements = []
    event.listen(engine, "before_cursor_execute", lambda conn, cursor, statement, *rest: statements.append(statement))
    client = TestClient(create_app(settings, engine=engine, auth=auth, migrate=False))

    note = add(client, signer, "x").json()
    client.get("/api/notes", headers=headers(signer))
    client.get(f"/api/notes/{note['id']}", headers=headers(signer))
    client.get(f"/api/notes/{note['id']}", headers=headers(signer, COMPANY_B))

    on_notes = [statement for statement in statements if "notes" in statement.lower()]
    assert len(on_notes) == 4
    assert all("org_id" in statement for statement in on_notes)
    assert all(statement.count("org_id") >= 2 for statement in on_notes if statement.lstrip().upper().startswith("SELECT"))


def test_a_valid_token_whose_company_is_not_a_uuid_is_a_401(client, signer):  # criterion 8
    response = client.get("/api/notes", headers=headers(signer, org_id="not-a-uuid"))

    assert response.status_code == 401
    assert response.content == b""
    assert response.headers["www-authenticate"] == "Bearer"


# --- what the package decides, seen from the product ---------------------------------------------------------------------


def test_no_token_is_a_401_on_every_endpoint(client):
    for response in (client.get("/api/notes"), client.get(f"/api/notes/{uuid.uuid4()}"), client.post("/api/notes", json={"text": "x"})):
        assert response.status_code == 401
        assert response.content == b""
        assert response.headers["www-authenticate"] == "Bearer"


def test_a_viewer_may_read_and_may_not_write(client, signer):
    note = add(client, signer).json()

    assert client.get("/api/notes", headers=headers(signer, permissions=READER)).status_code == 200
    assert client.get(f"/api/notes/{note['id']}", headers=headers(signer, permissions=READER)).status_code == 200
    refused = client.post("/api/notes", json={"text": "x"}, headers=headers(signer, permissions=READER))
    assert refused.status_code == 403
    assert refused.json() == {"error": "forbidden"}


def test_a_writer_without_the_read_permission_may_not_read(client, signer):
    assert client.get("/api/notes", headers=headers(signer, permissions=WRITER)).status_code == 403


def test_a_refused_write_stores_nothing(client, signer, engine):
    client.post("/api/notes", json={"text": "x"}, headers=headers(signer, permissions=READER))
    client.post("/api/notes", json={"text": "x"})

    with Session(engine) as session:
        assert session.scalar(select(func.count()).select_from(Note)) == 0


# --- the body of a POST -------------------------------------------------------------------------------------------------


@pytest.mark.parametrize(
    "body",
    [
        b"", b"not json", b"[]", b'"text"', b"null", b"1", b"{}", b'{"text": 1}', b'{"text": null}', b'{"text": ["a"]}',
        b'{"text": ""}', b'{"Text": "x"}', b'{"text": "' + b"x" * 1001 + b'"}', ('{"text": "a' + BACKSLASH + 'u0000b"}').encode(), ('{"text": "' + BACKSLASH + 'ud800"}').encode(),
        b'{"text": "x"', b"\xff\xfe", b'{"text": "' + b"x" * 20000 + b'"}',
    ],
)
def test_a_body_that_is_not_an_object_with_a_good_text_is_a_400(client, signer, engine, body):
    response = client.post("/api/notes", content=body, headers={**headers(signer), "Content-Type": "application/json"})

    assert response.status_code == 400
    assert response.json() == {"error": "invalid_request"}
    with Session(engine) as session:
        assert session.scalar(select(func.count()).select_from(Note)) == 0


def test_a_body_over_the_size_cap_is_a_400_even_when_its_text_is_good(client, signer, engine):
    body = json.dumps({"text": "x", "pad": "y" * 20000}).encode()  # about 20 KB; the cap is 16 KiB
    assert len(body) > 16 * 1024

    response = client.post("/api/notes", content=body, headers={**headers(signer), "Content-Type": "application/json"})

    assert response.status_code == 400
    assert response.json() == {"error": "invalid_request"}
    with Session(engine) as session:
        assert session.scalar(select(func.count()).select_from(Note)) == 0


def test_a_body_under_the_size_cap_may_carry_other_members(client, signer):
    body = json.dumps({"text": "x", "pad": "y" * 10000}).encode()
    assert len(body) < 16 * 1024

    response = client.post("/api/notes", content=body, headers={**headers(signer), "Content-Type": "application/json"})

    assert response.status_code == 201


def test_a_bad_body_is_a_401_or_a_403_before_it_is_a_400(client, signer):
    assert client.post("/api/notes", content=b"not json").status_code == 401
    assert client.post("/api/notes", content=b"not json", headers=headers(signer, permissions=READER)).status_code == 403


# --- health -----------------------------------------------------------------------------------------------------------


def test_health_is_200_when_the_database_answers_and_needs_no_token(client):
    response = client.get("/api/health")

    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


def test_health_is_503_when_the_database_does_not_answer(settings, auth, tmp_path):
    engine = create_engine(f"sqlite:///{tmp_path / 'missing-directory' / 'notes.db'}")
    client = TestClient(create_app(settings, engine=engine, auth=auth, migrate=False))

    response = client.get("/api/health")

    assert response.status_code == 503
    assert response.json() == {"error": "database_unavailable"}


# --- startup ----------------------------------------------------------------------------------------------------------


def test_the_service_applies_its_migrations_at_startup(settings, auth, signer, tmp_path):
    engine = create_engine(f"sqlite:///{tmp_path / 'notes.db'}")
    app = create_app(settings, engine=engine, auth=auth)  # migrate=True is the default

    with TestClient(app) as client:  # entering the client runs the startup
        assert add(client, signer, "first").status_code == 201
        assert len(client.get("/api/notes", headers=headers(signer)).json()) == 1


def test_the_service_answers_503_while_auth_core_is_down_and_it_holds_no_key(settings, engine, signer):
    def down(url, timeout):
        raise OSError("down")

    auth = AuthCore(settings.auth_issuer, settings.auth_audience, "http://auth.test/jwks", jwks_cache=JwksCache("http://auth.test/jwks", fetch=down))
    client = TestClient(create_app(settings, engine=engine, auth=auth, migrate=False))

    response = client.get("/api/notes", headers=headers(signer))

    assert response.status_code == 503
    assert response.json() == {"error": "auth_unavailable"}
```

- [ ] **Step 2: Run them and confirm they fail.**

```bash
(cd samples/notes-api && python -m pytest -q)
```

  Expected: FAIL at collection, `ModuleNotFoundError: No module named 'notes_api.app'`.

- [ ] **Step 3: Implement.** `samples/notes-api/src/notes_api/app.py`:

```python
import json
import uuid
from contextlib import asynccontextmanager
from datetime import datetime, timezone

from auth_core_fastapi import AuthCore, AuthError, Principal
from fastapi import Depends, FastAPI, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse
from sqlalchemy import select, text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from .db import Note, make_engine, upgrade_database
from .settings import Settings

MAX_BODY_BYTES = 16 * 1024
MAX_TEXT_CHARACTERS = 1000


class InvalidRequest(Exception):
    pass


def create_app(
    settings: Settings,
    *,
    engine: Engine | None = None,
    auth: AuthCore | None = None,
    migrate: bool = True,
) -> FastAPI:
    engine = engine or make_engine(settings.database_url)
    auth = auth or AuthCore(settings.auth_issuer, settings.auth_audience, settings.auth_jwks_url)

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        if migrate:
            upgrade_database(engine)  # at startup, before the first request
        yield
        engine.dispose()

    app = FastAPI(title="notes", lifespan=lifespan, docs_url=None, redoc_url=None, openapi_url=None)
    auth.install(app)

    @app.middleware("http")
    async def never_stored(request: Request, call_next):
        response = await call_next(request)
        response.headers.setdefault("Cache-Control", "no-store")
        return response

    @app.get("/api/health")
    def health():
        try:
            with engine.connect() as connection:
                connection.execute(text("SELECT 1"))
        except Exception:
            return JSONResponse({"error": "database_unavailable"}, status_code=503)
        return {"status": "ok"}

    @app.get("/api/notes")
    def list_notes(user: Principal = Depends(auth.require_permission("notes:read"))):
        org_id = _org_id(user)
        with Session(engine) as session:
            statement = (
                select(Note)
                .where(Note.org_id == org_id)  # every query is filtered by the token's company
                .order_by(Note.created_at.desc(), Note.id.desc())
            )
            return [_view(note) for note in session.scalars(statement)]

    @app.get("/api/notes/{note_id}")
    def get_note(note_id: str, user: Principal = Depends(auth.require_permission("notes:read"))):
        org_id = _org_id(user)
        wanted = _uuid(note_id)
        if wanted is None:
            return _not_found()
        with Session(engine) as session:
            statement = select(Note).where(Note.org_id == org_id, Note.id == wanted)
            note = session.scalars(statement).one_or_none()
            return _view(note) if note is not None else _not_found()

    @app.post("/api/notes", status_code=201)
    async def add_note(request: Request, user: Principal = Depends(auth.require_permission("notes:write"))):
        org_id = _org_id(user)
        try:
            note_text = await _read_text(request)
        except InvalidRequest:
            return JSONResponse({"error": "invalid_request"}, status_code=400)
        return await run_in_threadpool(_insert, engine, org_id, user.sub, note_text)

    return app


def _org_id(user: Principal) -> uuid.UUID:
    """The company of the caller: the only company a query is ever made for.

    Auth-Core's `org_id` is a UUID. A valid token whose `org_id` is not one was not issued for a product like this.
    """
    try:
        return uuid.UUID(user.org_id)
    except ValueError:
        raise AuthError(401) from None


def _uuid(value: str) -> uuid.UUID | None:
    try:
        parsed = uuid.UUID(value)
    except ValueError:
        return None
    return parsed if str(parsed) == value.lower() else None  # the 36-character form only


def _not_found() -> JSONResponse:
    return JSONResponse({"error": "not_found"}, status_code=404)


async def _read_text(request: Request) -> str:
    """The `text` of a body that is a JSON object with a text of 1 to 1000 characters Postgres can store."""
    body = bytearray()
    async for chunk in request.stream():
        body += chunk
        if len(body) > MAX_BODY_BYTES:
            raise InvalidRequest()
    try:
        document = json.loads(bytes(body))
    except ValueError:
        raise InvalidRequest() from None
    value = document.get("text") if isinstance(document, dict) else None
    if not isinstance(value, str) or not 1 <= len(value) <= MAX_TEXT_CHARACTERS:
        raise InvalidRequest()
    if "\x00" in value:
        raise InvalidRequest()
    try:
        value.encode("utf-8")  # a lone surrogate (JSON allows one) cannot be stored
    except UnicodeEncodeError:
        raise InvalidRequest() from None
    return value


def _insert(engine: Engine, org_id: uuid.UUID, author_sub: str, note_text: str) -> JSONResponse:
    note = Note(org_id=org_id, author_sub=author_sub, text=note_text)
    with Session(engine) as session:
        session.add(note)
        session.flush()  # the id and the time are set now, so that the answer needs no second query
        view = _view(note)
        session.commit()
    return JSONResponse(view, status_code=201)


def _view(note: Note) -> dict:
    created_at = note.created_at if note.created_at.tzinfo else note.created_at.replace(tzinfo=timezone.utc)
    return {
        "id": str(note.id),
        "text": note.text,
        "author_sub": note.author_sub,
        "created_at": created_at.astimezone(timezone.utc).isoformat().replace("+00:00", "Z"),
    }
```

`samples/notes-api/src/notes_api/main.py`:

```python
"""The ASGI application of the container: `uvicorn notes_api.main:app`."""

from .app import create_app
from .settings import Settings

app = create_app(Settings.from_env())
```

- [ ] **Step 4: Run them and confirm they pass.**

```bash
(cd samples/notes-api && python -m pytest -q)
```

  Expected: `57 passed` (7 of Task 4, 50 new).

- [ ] **Step 5: Prove that the tests bite.** Make each change in `src/notes_api/app.py`, run the suite, put the line back.

| Change | Fails |
| ------ | ----- |
| in `get_note`, `select(Note).where(Note.org_id == org_id, Note.id == wanted)` → `select(Note).where(Note.id == wanted)` | `test_another_companys_note_is_a_404_and_its_list_is_empty` |
| in `list_notes`, `.where(Note.org_id == org_id)` → `.where(Note.id != None)` | `test_two_companies_keep_their_own_notes` (and three more) |
| in `_read_text`, the `if` that tests `value` for a NUL character → `if False:` | `test_a_body_that_is_not_an_object_with_a_good_text_is_a_400` |
| in `_read_text`, `value.encode("utf-8")` → `pass` (the lone surrogate) | the same test, for the body with the escaped lone surrogate |
| `MAX_BODY_BYTES = 16 * 1024` → `1024 * 1024` | `test_a_body_over_the_size_cap_is_a_400_even_when_its_text_is_good` |
| `return parsed if str(parsed) == value.lower() else None` → `return parsed` | `test_only_the_written_form_of_an_id_finds_a_note` |
| `auth.require_permission("notes:write")` → `auth.require_permission("notes:read")` | `test_a_viewer_may_read_and_may_not_write` |

- [ ] **Step 6 (needs Docker): the same tests on PostgreSQL.** With the container of Task 4, step 6:

```bash
export NOTES_TEST_DATABASE_URL="postgresql+psycopg://postgres:check@127.0.0.1:$PORT/notes"
(cd samples/notes-api && python -m pytest -q)
unset NOTES_TEST_DATABASE_URL
docker rm -f notes-pg-check
```

  Expected: `57 passed` again. (The fixture drops `notes` and `alembic_version` before and after each test and runs the
  migration itself.) The `uuid` and `timestamptz` columns, and the way PostgreSQL treats a NUL character and a lone
  surrogate (an error, where the service must answer `400`), are only really tested here.

- [ ] **Step 7: Hand over** — uncommitted. The orchestrator commits the files of this task as
  `feat(samples): notes endpoints guarded by the package`.

### Task 6: The image, the proxy and the overlay

**Files:**
- Create: `samples/notes-api/auth.yaml`, `samples/notes-api/Dockerfile`, `samples/notes-api/Caddyfile`,
  `samples/notes-api/compose.yml`
- Modify: `.env.example`, `.gitattributes`, `.dockerignore`

**Interfaces:**
- Consumes: the base stack `deploy/docker-compose.yml` (services `postgres`, `mailpit`, `auth`; the file stays unchanged), the
  service `notes_api.main:app` (Task 5), the manifest format of spec 0005.
- Produces: the services `notes-db-init`, `notes-api`, `caddy` and the changes to `auth`, as the Global Constraints say; the
  variable `NOTES_DB_PASSWORD` in `.env`; the origin `http://localhost:8088`.

**Choices:** the audience is `notes-api` on **both** sides (Auth-Core's `Auth__Tokens__Audience` and the sample's
`AUTH_AUDIENCE`), set in the overlay, because a product has an audience of its own and the development default
(`auth-core-dev`) would hide a mix-up. The overlay's relative paths are written from `deploy/` (Compose resolves them against
the first `-f` file). The sample is reached by Caddy at `notes-api:8000` and has `expose`, no `ports`. The sample's
password for the database is `NOTES_DB_PASSWORD`, in `.env`: letters, digits and hyphens only, because `compose.yml` puts it into
`DATABASE_URL`.

- [ ] **Step 1: Write the files.** `samples/notes-api/auth.yaml`:

```yaml
# The manifest of the "notes" sample: what the product declares to Auth-Core (spec 0005 -> Manifest).
#
# permissions    what the product's code checks. The package guards GET /api/notes with notes:read and
#                POST /api/notes with notes:write (src/notes_api/app.py).
# default_roles  the roles every new company starts with a copy of. "*" stands for every permission of the catalog.
permissions: [notes:read, notes:write]
default_roles:
  admin:  ["*"]
  user:   [notes:read, notes:write]
  viewer: [notes:read]
```

`samples/notes-api/Dockerfile`:

```dockerfile
# syntax=docker/dockerfile:1
# The "notes" sample. Build from the repository root (the compose overlay does):
#   docker build -f samples/notes-api/Dockerfile .

FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PYTHONPATH=/app/src

WORKDIR /app

# The package, from this clone. A product installs it from a tag instead (docs/integration/python-fastapi.md).
COPY clients/python/pyproject.toml /tmp/auth-core-fastapi/pyproject.toml
COPY clients/python/src /tmp/auth-core-fastapi/src
RUN pip install /tmp/auth-core-fastapi && rm -rf /tmp/auth-core-fastapi

COPY samples/notes-api/requirements.txt .
RUN pip install -r requirements.txt

COPY samples/notes-api/alembic.ini .
COPY samples/notes-api/migrations migrations
COPY samples/notes-api/src src

RUN useradd --system --uid 10001 --no-create-home notes
USER notes

EXPOSE 8000
# The service applies its migrations at startup (src/notes_api/db.py).
CMD ["uvicorn", "notes_api.main:app", "--host", "0.0.0.0", "--port", "8000"]
```

`samples/notes-api/Caddyfile`:

```text
# One origin for the browser: /auth is Auth-Core, /api is the notes service. Plain HTTP, development only.
{
	admin off
	auto_https off
}

http://:8088 {
	handle /auth/* {
		reverse_proxy auth:8080
	}
	handle /api/* {
		reverse_proxy notes-api:8000
	}
	handle {
		respond "not found" 404
	}
}
```

`samples/notes-api/compose.yml`:

```yaml
# The "notes" sample and a Caddy proxy on top of the development stack. Use it with the base file:
#
#   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env up -d --build
#
# Relative paths in this file are resolved from deploy/ (the directory of the FIRST -f file), not from here.
# Needs NOTES_DB_PASSWORD in .env (see .env.example). The browser's origin is http://localhost:8088:
#   /auth -> Auth-Core, /api -> the notes service. Neither of them is published on a port of its own.
services:
  notes-db-init:
    # One-shot and idempotent: creates the login "notes" and the database "notes" on the stack's PostgreSQL server,
    # or sets the password again when they exist. (An init script of the postgres image runs on a fresh volume only.)
    image: postgres:16-alpine
    depends_on:
      postgres:
        condition: service_healthy
    restart: "no"
    environment:
      PGHOST: postgres
      PGUSER: auth
      PGDATABASE: auth
      PGPASSWORD: ${POSTGRES_PASSWORD:?set POSTGRES_PASSWORD in .env (copy .env.example)}
      NOTES_DB_PASSWORD: ${NOTES_DB_PASSWORD:?set NOTES_DB_PASSWORD in .env (copy .env.example)}
    entrypoint: ["sh", "-ec"]
    command:
      - |
        psql -v ON_ERROR_STOP=1 -v pw="$$NOTES_DB_PASSWORD" <<'SQL'
        SELECT format('CREATE ROLE notes LOGIN PASSWORD %L', :'pw')
          WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'notes') \gexec
        ALTER ROLE notes PASSWORD :'pw';
        SELECT 'CREATE DATABASE notes OWNER notes'
          WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'notes') \gexec
        SQL

  notes-api:
    build:
      context: ..
      dockerfile: samples/notes-api/Dockerfile
    depends_on:
      notes-db-init:
        condition: service_completed_successfully
    environment:
      # The same issuer and audience as Auth-Core below; the keys are fetched over the internal network.
      AUTH_ISSUER: http://localhost:8088/auth
      AUTH_AUDIENCE: notes-api
      AUTH_JWKS_URL: http://auth:8080/auth/.well-known/jwks.json
      DATABASE_URL: postgresql+psycopg://notes:${NOTES_DB_PASSWORD:?set NOTES_DB_PASSWORD in .env}@postgres:5432/notes
    # Reachable from the other containers (Caddy), not published on the host.
    expose:
      - "8000"

  caddy:
    image: caddy:2
    depends_on:
      auth:
        condition: service_started
      notes-api:
        condition: service_started
    ports:
      # Loopback only, like every other port of this stack.
      - "127.0.0.1:8088:8088"
    volumes:
      - ../samples/notes-api/Caddyfile:/etc/caddy/Caddyfile:ro

  auth:
    environment:
      # Tokens name the origin the browser uses, and are for the notes service.
      Auth__Tokens__Issuer: http://localhost:8088/auth
      Auth__Tokens__Audience: notes-api
    volumes:
      # The same container path as in the base file: this mount replaces the development manifest.
      - ../samples/notes-api/auth.yaml:/etc/auth-core/auth.yaml:ro
```

The three existing files are changed by appending, which works whatever line endings the working copy has (on Windows they
are CRLF, and git normalises them). `.env.example` gets the password of the sample's database login; `.gitattributes` keeps
the files that are copied or mounted into Linux containers LF on Windows, as it does the shell scripts; `.dockerignore`
keeps a local virtual environment or a test cache out of the image's build context:

```bash
cat >> .env.example <<'EOF'

# Password of the "notes" database login of the sample product (samples/notes-api). Letters, digits and hyphens only: it goes
# into a URL. Used by the compose overlay (docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml).
NOTES_DB_PASSWORD=change-me-notes
EOF
cat >> .gitattributes <<'EOF'
# Files that are copied or mounted into Linux containers: keep them LF as well.
*.py text eol=lf
*.ini text eol=lf
*.mako text eol=lf
*.yml text eol=lf
*.yaml text eol=lf
Caddyfile text eol=lf
Dockerfile text eol=lf
EOF
sed -i '/^\.claude\//a **/__pycache__/\n**/.pytest_cache/\n**/.venv/' .dockerignore
git diff --stat -- .env.example .gitattributes .dockerignore
```

  Expected: three files changed, with 4, 8 and 3 lines added (`.dockerignore` gains `**/__pycache__/`, `**/.pytest_cache/`
  and `**/.venv/` after its `.claude/` line).

- [ ] **Step 2: Check the merge of the two compose files.** With a `.env` that has `POSTGRES_PASSWORD`,
  `AUTH_DEV_SEED_EMAIL`, `AUTH_DEV_SEED_PASSWORD` and `NOTES_DB_PASSWORD` (`cp .env.example .env`, then local values), and a
  compose project name of its own:

```bash
export COMPOSE_PROJECT_NAME=auth-core-e2e-0006
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env config > /tmp/merged.yml
grep -c 'target: /etc/auth-core/auth.yaml' /tmp/merged.yml
grep -n 'source: .*auth.yaml' /tmp/merged.yml
sed -n '/^  notes-api:/,/^  notes-db-init:/p' /tmp/merged.yml | grep -n 'published\|ports\|expose'
git diff --stat deploy/docker-compose.yml
```

  Expected: `1` (one mount at that path, not two); a source path ending `samples/notes-api/auth.yaml`; for `notes-api`
  only the `expose` of `"8000"` and no `ports`; and no output from `git diff --stat` (the base file is unchanged). On this
  branch the base file has no manifest mount yet (slice 5 adds it), so the first two checks read `1` and the sample's path
  because the overlay adds the mount; after slice 5 is merged they show that the overlay **replaces** the base's mount.

- [ ] **Step 3: Bring the stack up and check what does not need slice 5** (needs Docker; ports 8088, 8080 and 8025 free).
  The first build takes a few minutes. `scripts/dev-keys.sh` needs `MSYS2_ARG_CONV_EXCL="/CN"` in Git Bash.

```bash
MSYS2_ARG_CONV_EXCL="/CN" scripts/dev-keys.sh
export COMPOSE_PROJECT_NAME=auth-core-e2e-0006
C="docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env"
$C up -d --build
curl -si http://localhost:8088/api/health | sed -n '1p;/^Cache-Control/p;$p'
curl -si http://localhost:8088/api/notes | sed -n '1p;/^Cache-Control/p;/^Www-Authenticate/p'
curl -s  http://localhost:8088/auth/health; echo
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:8088/nothing
echo "published by the sample: [$(docker port "$($C ps -q notes-api)")]"
curl -s -o /dev/null -m 3 -w 'host port 8000: %{http_code}\n' http://localhost:8000/api/health
$C logs notes-db-init | tail -n 3
$C run --rm notes-db-init | tail -n 2
```

  Expected, in order: `HTTP/1.1 200 OK`, `Cache-Control: no-store` and `{"status":"ok"}` (the sample, through Caddy, with its
  migration applied at startup); `HTTP/1.1 401 Unauthorized`, `Cache-Control: no-store` and `Www-Authenticate: Bearer`;
  `Healthy` (Auth-Core, through Caddy); `404` (Caddy's own); `published by the sample: []`; `host port 8000: 000` (nothing
  listens there); the log of `notes-db-init` ending in `CREATE ROLE`, `ALTER ROLE`, `CREATE DATABASE`; and a second run that
  prints `ALTER ROLE` only (it is safe to run again).

- [ ] **Step 4: The proxy and the token, as far as this branch goes** (a login and a refresh work; the claims of spec 0005 are
  missing, so the sample's answer is `401`):

```bash
curl -s -X POST http://localhost:8088/auth/login -H 'Content-Type: application/json' \
  -d "{\"email\":\"$(grep ^AUTH_DEV_SEED_EMAIL= .env | cut -d= -f2- | tr -d '\r')\",\"password\":\"$(grep ^AUTH_DEV_SEED_PASSWORD= .env | cut -d= -f2- | tr -d '\r')\"}" \
  -D /tmp/login.hdr -o /tmp/login.json
grep -i '^set-cookie' /tmp/login.hdr | sed 's/auth_rt=[^;]*/auth_rt=<hidden>/'
PAIR="$(grep -i '^set-cookie: auth_rt' /tmp/login.hdr | tr -d '\r' | cut -d: -f2- | sed 's/^ *//' | cut -d';' -f1)"
curl -s -o /dev/null -w 'refresh through the proxy: %{http_code}\n' -X POST http://localhost:8088/auth/refresh -H "Cookie: $PAIR"
python -c "
import base64, json, sys
token = json.load(sys.stdin)['access_token'].split('.')
decode = lambda part: json.loads(base64.urlsafe_b64decode(part + '=' * (-len(part) % 4)))
print('header', {k: decode(token[0])[k] for k in ('alg', 'typ')}, 'kid' in decode(token[0]))
payload = decode(token[1]); print('iss', payload['iss'], 'aud', payload['aud'])" < /tmp/login.json
rm -f /tmp/login.hdr /tmp/login.json
```

  Expected: a `Set-Cookie: auth_rt=<hidden>; … path=/auth; secure; samesite=strict; httponly`; `refresh through the proxy:
  200`; `header {'alg': 'RS256', 'typ': 'at+jwt'} True`; `iss http://localhost:8088/auth aud notes-api`. With Auth-Core as it is
  on this branch the sample's answer to that token is a `401` (no `org_id`); after slice 5 it is the e2e script's business.
  This is the check that the issuer can be the proxy's origin while the sample fetches the keys over `http://auth:8080`, and
  that the refresh cookie (`Path=/auth`) survives Caddy on plain HTTP.

- [ ] **Step 5: Tear down.**

```bash
export COMPOSE_PROJECT_NAME=auth-core-e2e-0006
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
rm -f .env
rm -rf .secrets
```

  Do not commit `.env` or `.secrets/` (both are git-ignored); `git status --porcelain` shows only the files of this task.

- [ ] **Step 6: Hand over** — uncommitted. The orchestrator commits the four files in
  `samples/notes-api/`, `.env.example`, `.gitattributes` and `.dockerignore` as
  `feat(samples): compose overlay with Caddy, the notes database and the manifest`.

---

## Group C — the e2e script, the guide, the acceptance map

When the group is done, `scripts/e2e-notes.sh` drives the whole path through the proxy, the integration guide exists with
every file it names, and the verifiers have a map from every criterion to a test or a script step.

### Task 7: `scripts/e2e-notes.sh`

**Files:**
- Create: `scripts/e2e-notes.sh`

**Interfaces:**
- Consumes: the overlay and the sample (Task 6); the company API, the operator CLI and the claims of **spec 0005** (see the
  table of routes in "Verified before this plan was written"); `.env` with `AUTH_DEV_SEED_EMAIL`,
  `AUTH_DEV_SEED_PASSWORD` and `NOTES_DB_PASSWORD`.
- Produces: a script that starts the stack with the overlay and leaves it running, drives the six steps of the spec, prints
  `PASS step N: …` for each and `ALL PASS`, and exits non-zero on the first failure. It never prints a password, a token, a
  cookie or a mail body.

**What the script does that the spec leaves open:**
- It **starts** the stack itself (`up -d --build`), as the spec says, and leaves it running; the earlier scripts do not start
  anything. It is re-runnable on the same stack (every address is made for the run); it is **not** meant for a stack that an
  earlier run without the overlay made: step 1 checks that the development company has the roles `admin`, `user` and
  `viewer` of the sample's manifest and fails with "start from down -v" if not.
- "The first admin" of the spec is the development seed user: the admin of the development company, which the service creates
  from the active manifest when it first starts (spec 0005).
- Step 6 stops Auth-Core (an `EXIT` trap starts it again if the script dies in between), restarts the sample, and then **polls**
  the same request for up to 60 seconds: the package asks for the keys again at most once in 10 seconds, so the first
  answers after Auth-Core is back may still be `503 auth_unavailable`. Anything but `503` or `200` fails the step.
- Step 1 also checks that the sample publishes no port of its own (`docker port` of its container prints nothing), and
  step 5 checks that the token issued before the role change still says `viewer` (the sample trusts a token for its
  lifetime).
- The password `E2E_PASSWORD` meets the policy of spec 0004 (8 characters, upper, lower, digit).

- [ ] **Step 1: Write the script.** `scripts/e2e-notes.sh`:

```bash
#!/usr/bin/env bash
# Real-network end-to-end check of spec 0006 (the Python package and the "notes" sample) against the compose stack with
# the sample overlay: Auth-Core, PostgreSQL, the mail catcher (Mailpit), the notes service and Caddy. Everything goes
# through the proxy, as a browser would: http://localhost:8088/auth is Auth-Core, http://localhost:8088/api is the sample.
#
# This script STARTS the stack itself (docker compose up -d --build) and leaves it running. What it checks, in order
# (SEED = the development seed user, the admin of the development company A; B = a second company):
#   1. SEED logs in (the token carries notes:read and notes:write, the issuer is the proxy's origin), adds a note (201),
#      and the list holds it; the sample publishes no port of its own - criteria 1, 2 of the Goal sequence.
#   2. No token, a token with a changed signature and malformed headers each get 401, an empty body and
#      WWW-Authenticate: Bearer - criterion 3.
#   3. The operator creates company B with the CLI and invites its first admin; the admin accepts the invitation from the
#      mail in Mailpit and logs in. Their list is empty, and company A's note by its id is a 404 - criterion 8.
#   4. SEED invites a member with the role viewer through the company API; the viewer accepts and logs in. Reading is
#      200; adding a note is 403 forbidden - criterion 4.
#   5. SEED changes the viewer's role to user. The old token still says viewer; after a refresh the same person adds a
#      note (201).
#   6. Auth-Core is stopped and the sample is restarted, so it holds no keys: a valid token gets 503 auth_unavailable.
#      After Auth-Core starts again, the same request gets 200 (the sample asks for the keys again at most every
#      10 seconds, so the first answers after the restart may still be 503) - criterion 5.
#
# Full sequence, from the repo root (a clean stack: the development company is made from the manifest of the sample the
# first time the service starts, so a volume of an earlier stack that used another manifest makes step 1 fail):
#   cp .env.example .env                  # then set real local values (git-ignored); NOTES_DB_PASSWORD too
#   scripts/dev-keys.sh                   # dev signing/encryption keys into .secrets/ (git-ignored)
#   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
#   scripts/e2e-notes.sh                  # this script: brings the stack up (builds the images), then drives it
#   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
# The earlier e2e scripts run on the stack WITHOUT the overlay (deploy/docker-compose.yml alone), as before.
#
# Reads AUTH_DEV_SEED_EMAIL, AUTH_DEV_SEED_PASSWORD and NOTES_DB_PASSWORD from the repo-root .env (parsed, never sourced).
# The operator commands run as `docker compose run --rm -T --no-deps auth admin ...` (spec 0005). Needs: curl, python3
# (standard library only), docker compose, and host ports 8088 (Caddy), 8080 (Auth-Core) and 8025 (Mailpit) free.
# Env: BASE_URL (default http://localhost:8088), MAILPIT_URL (default http://localhost:8025). The links in the mails start
# with http://localhost:4200/invite (appsettings.Development.json). Takes about three minutes, most of it the first image
# build and the wait for the server to pick up the invitation the CLI queued. Re-runnable on the same stack: every company
# and address is made for the run. Exits non-zero on the first failure; prints "PASS <step>" per step; never prints a
# password, a token, a cookie or a mail body. Request bodies are built into files in a mktemp -d directory (removed on
# exit) and curl reads them with --data-binary @file; tokens and cookies reach curl through header files.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8088}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
compose=(docker compose -f "$root/deploy/docker-compose.yml" -f "$root/samples/notes-api/compose.yml" --env-file "$root/.env")

tmp="$(mktemp -d)"
AUTH_STOPPED=0
cleanup() {
  # a run that dies during step 6 must not leave Auth-Core stopped
  if [[ "$AUTH_STOPPED" == "1" ]]; then "${compose[@]}" start auth >/dev/null 2>&1 || true; fi
  rm -rf "$tmp"
}
trap cleanup EXIT

fail() { echo "FAIL $*" >&2; exit 1; }
pass() { echo "PASS $*"; }

env_get() { # read KEY from .env without executing it; strips one pair of surrounding quotes
  local line
  line="$(grep -E "^$1=" "$root/.env" | tail -n1 || true)"
  line="${line#*=}"
  line="${line%$'\r'}"
  if [[ "$line" =~ ^\"(.*)\"$ || "$line" =~ ^\'(.*)\'$ ]]; then line="${BASH_REMATCH[1]}"; fi
  printf '%s' "$line"
}

[[ -f "$root/.env" ]] || fail "missing $root/.env (copy .env.example and set local values)"
SEED_EMAIL="$(env_get AUTH_DEV_SEED_EMAIL)"
SEED_PASSWORD="$(env_get AUTH_DEV_SEED_PASSWORD)"
[[ -n "$SEED_EMAIL" && -n "$SEED_PASSWORD" ]] || fail "AUTH_DEV_SEED_EMAIL / AUTH_DEV_SEED_PASSWORD not set in .env"
[[ -n "$(env_get NOTES_DB_PASSWORD)" ]] || fail "NOTES_DB_PASSWORD not set in .env (see .env.example)"

run="$RANDOM$RANDOM"
BADMIN_EMAIL="boss-$run@globex.test"
VIEWER_EMAIL="viewer-$run@acme.test"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
export E2E_BADMIN_EMAIL="$BADMIN_EMAIL" E2E_VIEWER_EMAIL="$VIEWER_EMAIL"
export E2E_PASSWORD="E2e-Passw0rd-$run"
NOTE_TEXT="hello from run $run"
export E2E_NOTE_TEXT="$NOTE_TEXT"

wait_ok() { # wait_ok <url> <seconds>: waits until the URL answers 200
  local i
  for i in $(seq 1 "$2"); do
    if [[ "$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "$1" || true)" == "200" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

# call <method> <path> [body-file] [auth-header-file] -> sets HTTP_CODE and BODY; response headers in $tmp/hdr
call() {
  local args=(-sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' -X "$1" "$BASE_URL$2")
  if [[ -n "${3:-}" ]]; then args+=(-H 'Content-Type: application/json' --data-binary "@$3"); fi
  if [[ -n "${4:-}" ]]; then args+=(-H "@$4"); fi
  HTTP_CODE="$(curl "${args[@]}")"
  BODY="$(cat "$tmp/body")"
}

header() { # value of a response header of the last response, CR stripped (empty when absent)
  { grep -i "^$1:" "$tmp/hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//'
}

# The python3 of some machines is a Windows interpreter, which ends its lines with CR LF: the helpers below that print a
# value strip the CR, so that the value compares equal in bash.

# val <python-expression-of-d>: evaluates it against the JSON of $BODY and prints the result
val() { python3 -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1" <<< "$BODY" | tr -d '\r'; }

# jwt_payload <auth-header-file>: the payload of the bearer token in the file, as JSON (the token is never printed)
jwt_payload() {
  python3 -c '
import base64, json, sys
token = sys.stdin.read().split("Bearer ", 1)[1].strip().split(".")[1]
print(json.dumps(json.loads(base64.urlsafe_b64decode(token + "=" * (-len(token) % 4)))))
' < "$1" | tr -d '\r'
}

# changed_signature <auth-header-file> <out-file>: the same header with the first byte of the token's signature changed
changed_signature() {
  python3 -c '
import base64, sys
token = sys.stdin.read().split("Bearer ", 1)[1].strip()
head, payload, signature = token.split(".")
raw = bytearray(base64.urlsafe_b64decode(signature + "=" * (-len(signature) % 4)))
raw[0] ^= 0xFF
print("Authorization: Bearer " + ".".join([head, payload, base64.urlsafe_b64encode(bytes(raw)).rstrip(b"=").decode()]))
' < "$1" > "$2"
}

json_body() { # json_body <file> <python-expression>: writes the JSON of the expression, which may read os.environ
  python3 -c 'import json,os,sys; print(json.dumps(eval(sys.argv[1])))' "$2" > "$1"
}

expect_status() { # expect_status <what> <code> [exact-body]
  [[ "$HTTP_CODE" == "$2" ]] || fail "$1: HTTP $HTTP_CODE, expected $2"
  if [[ $# -ge 3 ]]; then [[ "$BODY" == "$3" ]] || fail "$1: unexpected body"; fi
}

expect_error() { expect_status "$1" "$2" "{\"error\":\"$3\"}"; }

expect_unauthorized() { # a 401 of the package: an empty body and WWW-Authenticate: Bearer
  [[ "$HTTP_CODE" == "401" ]] || fail "$1: HTTP $HTTP_CODE, expected 401"
  [[ -z "$BODY" ]] || fail "$1: the body of a 401 must be empty"
  [[ "$(header www-authenticate)" == "Bearer" ]] || fail "$1: WWW-Authenticate must be Bearer"
}

expect_eq() { [[ "$2" == "$3" ]] || fail "$1: got '$2', expected '$3'"; }

# login <name> <email-env> <password-env>: logs in, keeps the bearer header in $tmp/<name>.auth and the cookie in $tmp/<name>.cookie
login() {
  json_body "$tmp/$1.login" "{'email': os.environ['$2'], 'password': os.environ['$3']}"
  call POST /auth/login "$tmp/$1.login"
  expect_status "login of $1" 200
  keep_session "$1"
}

keep_session() { # keep_session <name>: the access token of $BODY and the cookie of the last response
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$1.auth"
  local pair
  pair="$({ grep -i '^set-cookie:[[:space:]]*auth_rt=[^;]' "$tmp/hdr" || true; } | head -n1 | tr -d '\r' | cut -d: -f2- | sed 's/^ *//' | cut -d';' -f1)"
  [[ -n "$pair" ]] || fail "the response set no auth_rt cookie for $1"
  printf 'Cookie: %s\n' "$pair" > "$tmp/$1.cookie"
}

# refresh <name>: refreshes the session of <name> with its cookie (through the proxy: the cookie has Path=/auth); keeps the
# new tokens. Sets HTTP_CODE.
refresh() {
  call POST /auth/refresh "" "$tmp/$1.cookie"
  if [[ "$HTTP_CODE" == "200" ]]; then keep_session "$1"; fi
}

# --- the mail catcher ---------------------------------------------------------------------------------------------

mail_count() {
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" -o "$tmp/search.json"
  python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages_count"])' < "$tmp/search.json" | tr -d '\r'
}

# wait_mail <address> <count> <seconds>: waits until the catcher holds <count> mails for the address, then saves the newest
wait_mail() {
  local i id
  for i in $(seq 1 "$3"); do
    if [[ "$(mail_count "$1")" == "$2" ]]; then
      id="$(python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages"][0]["ID"])' < "$tmp/search.json" | tr -d '\r')"
      curl -sS --max-time 10 "$MAILPIT_URL/api/v1/message/$id" -o "$tmp/mail.json"
      return 0
    fi
    sleep 1
  done
  return 1
}

# token_body <file> [password-env]: takes the token of the invitation link from the text part of $tmp/mail.json, checks that
# the HTML part holds the same token, and writes the request body ({"token"} or {"token","password"}). Never printed.
token_body() {
  python3 -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(r"http://localhost:4200/invite\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no invitation link with a token")
if match.group(1) not in mail["HTML"]:
    sys.exit("the HTML part does not hold the link of the text part")
body = {"token": match.group(1)}
if len(sys.argv) > 1 and sys.argv[1]:
    body["password"] = os.environ[sys.argv[1]]
print(json.dumps(body))
' "${2:-}" < "$tmp/mail.json" > "$1"
}

# --- the operator CLI, in the service's own image ----------------------------------------------------------------

# cli <args...>: runs `auth-server admin <args>` in a one-off container; sets CLI_OUT (stdout), CLI_EXIT, and keeps stderr in $tmp/cli.err
cli() {
  CLI_EXIT=0
  CLI_OUT="$("${compose[@]}" run --rm -T --no-deps auth admin "$@" 2> "$tmp/cli.err")" || CLI_EXIT=$?
  CLI_OUT="${CLI_OUT//$'\r'/}"
}

# --- Step 1: the stack, SEED, the first note -------------------------------------------------------------------------
echo "bringing the stack up (the first build takes a few minutes)..."
"${compose[@]}" up -d --build > "$tmp/up.log" 2>&1 || { cat "$tmp/up.log" >&2; fail "step 1: docker compose up failed"; }
wait_ok "$BASE_URL/auth/health" 120 || fail "step 1: $BASE_URL/auth/health (Auth-Core through Caddy) did not return 200 within 120s"
wait_ok "$BASE_URL/api/health" 60 || fail "step 1: $BASE_URL/api/health (the sample through Caddy) did not return 200 within 60s"
wait_ok "$MAILPIT_URL/readyz" 60 || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s (is the mailpit service up?)"
SAMPLE_CONTAINER="$("${compose[@]}" ps -q notes-api)"
[[ -n "$SAMPLE_CONTAINER" ]] || fail "step 1: the notes-api service is not running"
[[ -z "$(docker port "$SAMPLE_CONTAINER")" ]] || fail "step 1: the notes service publishes a port of its own; it must be reachable only through Caddy"

login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD
BODY="$(jwt_payload "$tmp/seed.auth")"
SEED_SUB="$(val 'd["sub"]')"
expect_eq "step 1: issuer" "$(val 'd["iss"]')" "$BASE_URL/auth"
expect_eq "step 1: audience" "$(val 'd["aud"]')" "notes-api"
[[ "$(val '"notes:read" in d["permissions"] and "notes:write" in d["permissions"]')" == "True" ]] \
  || fail "step 1: the seed user's token does not carry notes:read and notes:write (a volume of an earlier stack? start from down -v)"
A="$(val 'd["org_id"]')"
call GET /auth/org/roles "" "$tmp/seed.auth"
expect_status "step 1: GET /auth/org/roles" 200
[[ "$(val 'sorted(r["name"] for r in d["roles"]) == ["admin", "user", "viewer"]')" == "True" ]] \
  || fail "step 1: the development company's roles are not the default roles of the notes manifest (start from down -v)"
USER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "user"][0]')"
VIEWER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "viewer"][0]')"
export E2E_USER_ROLE="$USER_ROLE" E2E_VIEWER_ROLE="$VIEWER_ROLE"

json_body "$tmp/note.json" "{'text': os.environ['E2E_NOTE_TEXT']}"
call POST /api/notes "$tmp/note.json" "$tmp/seed.auth"
expect_status "step 1: add a note" 201
NOTE_ID="$(val 'd["id"]')"
expect_eq "step 1: the note's text" "$(val 'd["text"]')" "$NOTE_TEXT"
expect_eq "step 1: the note's author" "$(val 'd["author_sub"]')" "$SEED_SUB"
call GET /api/notes "" "$tmp/seed.auth"
expect_status "step 1: list the notes" 200
[[ "$(val 'any(n["id"] == "'"$NOTE_ID"'" for n in d)')" == "True" ]] || fail "step 1: the list does not hold the note just added"
pass "step 1: the stack is up; the notes service publishes no port; SEED's token names the proxy's origin and carries notes:read and notes:write; a note was added (201) and is listed"

# --- Step 2: no token, a changed signature, malformed headers ---------------------------------------------------------
call GET /api/notes
expect_unauthorized "step 2: no token"
changed_signature "$tmp/seed.auth" "$tmp/seed-badsig.auth"
call GET /api/notes "" "$tmp/seed-badsig.auth"
expect_unauthorized "step 2: a changed signature"
printf 'Authorization: Bearer\n' > "$tmp/malformed-1.auth"
call GET /api/notes "" "$tmp/malformed-1.auth"
expect_unauthorized "step 2: a Bearer header without a token"
printf 'Authorization: Basic dXNlcjpwYXNz\n' > "$tmp/malformed-2.auth"
call GET /api/notes "" "$tmp/malformed-2.auth"
expect_unauthorized "step 2: a scheme other than Bearer"
printf 'Authorization: Bearer not.a.token\n' > "$tmp/malformed-3.auth"
call GET /api/notes "" "$tmp/malformed-3.auth"
expect_unauthorized "step 2: a token that is not a JWS"
call POST /api/notes "$tmp/note.json"
expect_unauthorized "step 2: adding a note without a token"
pass "step 2: no token, a changed signature and three malformed headers are each a 401 with an empty body and WWW-Authenticate: Bearer"

# --- Step 3: company B sees none of company A --------------------------------------------------------------------------
cli create-org --name "E2E Globex $run"
expect_eq "step 3: create-org exit code" "$CLI_EXIT" "0"
[[ "$CLI_OUT" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || fail "step 3: create-org printed more than the company id"
B="$CLI_OUT"
cli invite --org "$B" --email "$BADMIN_EMAIL" --role admin
expect_eq "step 3: invite exit code" "$CLI_EXIT" "0"
# The CLI only queues the mail: the server sends it at its next poll, within a minute.
wait_mail "$BADMIN_EMAIL" 1 120 || fail "step 3: no invitation mail within 120s of the CLI invitation"
token_body "$tmp/badmin-accept.json" E2E_PASSWORD || fail "step 3: no usable invitation link in the mail"
call POST /auth/invites/accept "$tmp/badmin-accept.json"
expect_status "step 3: B's admin accepts" 204 ""
login badmin E2E_BADMIN_EMAIL E2E_PASSWORD
BODY="$(jwt_payload "$tmp/badmin.auth")"
expect_eq "step 3: B's admin belongs to company B" "$(val 'd["org_id"]')" "$B"
[[ "$B" != "$A" ]] || fail "step 3: companies A and B are the same"
call GET /api/notes "" "$tmp/badmin.auth"
expect_status "step 3: B's list" 200 "[]"
call GET "/api/notes/$NOTE_ID" "" "$tmp/badmin.auth"
expect_error "step 3: company A's note, asked for by B" 404 not_found
call GET "/api/notes/not-a-uuid" "" "$tmp/badmin.auth"
expect_error "step 3: an id that is not a UUID" 404 not_found
call GET "/api/notes/$NOTE_ID" "" "$tmp/seed.auth"
expect_status "step 3: A still reads its own note" 200
pass "step 3: company B was made by the CLI, its admin accepted the mail and logged in; B's list is empty and A's note, by id, is a 404"

# --- Step 4: a viewer of company A reads and does not write ----------------------------------------------------------------
login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD  # a fresh token: step 3 may have waited a minute or two for the mail
json_body "$tmp/invite-viewer.json" "{'email': os.environ['E2E_VIEWER_EMAIL'], 'role_id': os.environ['E2E_VIEWER_ROLE']}"
call POST /auth/org/invites "$tmp/invite-viewer.json" "$tmp/seed.auth"
expect_status "step 4: invite the viewer" 202 ""
wait_mail "$VIEWER_EMAIL" 1 30 || fail "step 4: no invitation mail for the viewer within 30s (an API invitation wakes the dispatcher)"
token_body "$tmp/viewer-accept.json" E2E_PASSWORD
call POST /auth/invites/accept "$tmp/viewer-accept.json"
expect_status "step 4: the viewer accepts" 204 ""
login viewer E2E_VIEWER_EMAIL E2E_PASSWORD
BODY="$(jwt_payload "$tmp/viewer.auth")"
expect_eq "step 4: the viewer's permissions" "$(val 'd["permissions"]')" "['notes:read']"
expect_eq "step 4: the viewer's company" "$(val 'd["org_id"]')" "$A"
call GET /api/notes "" "$tmp/viewer.auth"
expect_status "step 4: the viewer reads" 200
[[ "$(val 'any(n["id"] == "'"$NOTE_ID"'" for n in d)')" == "True" ]] || fail "step 4: the viewer does not see the note of the company"
call POST /api/notes "$tmp/note.json" "$tmp/viewer.auth"
expect_error "step 4: the viewer adds a note" 403 forbidden
[[ "$(header cache-control)" == *no-store* ]] || fail "step 4: the 403 is not marked no-store"
pass "step 4: the viewer of company A reads its notes (200) and cannot add one (403 forbidden)"

# --- Step 5: the viewer becomes a user -----------------------------------------------------------------------------------
call GET /auth/org/members "" "$tmp/seed.auth"
expect_status "step 5: GET /auth/org/members" 200
VIEWER_ID="$(val '[m["user_id"] for m in d["members"] if m["email"] == "'"$VIEWER_EMAIL"'"][0]')"
json_body "$tmp/role-user.json" "{'role_id': os.environ['E2E_USER_ROLE']}"
call PUT "/auth/org/members/$VIEWER_ID/role" "$tmp/role-user.json" "$tmp/seed.auth"
expect_status "step 5: change the viewer's role to user" 204 ""
# The sample trusts a valid token for its lifetime (spec 0005, Decisions 4 and 12): the old token still says viewer.
call POST /api/notes "$tmp/note.json" "$tmp/viewer.auth"
expect_error "step 5: the token issued before the change" 403 forbidden
refresh viewer
expect_eq "step 5: the viewer's refresh" "$HTTP_CODE" "200"
BODY="$(jwt_payload "$tmp/viewer.auth")"
expect_eq "step 5: the new permissions" "$(val 'd["permissions"]')" "['notes:read', 'notes:write']"
call POST /api/notes "$tmp/note.json" "$tmp/viewer.auth"
expect_status "step 5: the same person adds a note after the refresh" 201
pass "step 5: after the role change and a refresh, the same person adds a note (201); the token issued before the change was still a viewer's"

# --- Step 6: Auth-Core down, the sample holds no keys ----------------------------------------------------------------------
"${compose[@]}" stop auth > "$tmp/stop.log" 2>&1 || { cat "$tmp/stop.log" >&2; fail "step 6: could not stop Auth-Core"; }
AUTH_STOPPED=1
"${compose[@]}" restart notes-api > "$tmp/restart.log" 2>&1 || { cat "$tmp/restart.log" >&2; fail "step 6: could not restart the sample"; }
wait_ok "$BASE_URL/api/health" 60 || fail "step 6: the sample did not come back within 60s (it must start while Auth-Core is down)"
call GET /api/notes "" "$tmp/viewer.auth"
expect_error "step 6: a valid token while Auth-Core is down and the sample holds no key" 503 auth_unavailable
[[ "$(header cache-control)" == *no-store* ]] || fail "step 6: the 503 is not marked no-store"
call GET /api/notes
expect_unauthorized "step 6: no token is still a 401 while Auth-Core is down"
"${compose[@]}" start auth > "$tmp/start.log" 2>&1 || { cat "$tmp/start.log" >&2; fail "step 6: could not start Auth-Core again"; }
AUTH_STOPPED=0
wait_ok "$BASE_URL/auth/health" 120 || fail "step 6: Auth-Core did not answer within 120s of its start"
# The sample asks for the keys at most every 10 seconds: until then the answer stays 503, and nothing else is allowed.
ok=0
for _ in $(seq 1 30); do
  call GET /api/notes "" "$tmp/viewer.auth"
  if [[ "$HTTP_CODE" == "200" ]]; then ok=1; break; fi
  expect_error "step 6: while the sample waits to ask for the keys again" 503 auth_unavailable
  sleep 2
done
[[ "$ok" == "1" ]] || fail "step 6: the same request was not a 200 within 60s of Auth-Core's start"
pass "step 6: with Auth-Core down and no key held, a valid token is a 503 auth_unavailable (no token is still 401); after Auth-Core is back the same request is a 200"

echo "ALL PASS"
```

- [ ] **Step 2: Static checks, and the mode in the index.**

```bash
bash -n scripts/e2e-notes.sh && echo "syntax ok"
file scripts/e2e-notes.sh
grep -c $'\r' scripts/e2e-notes.sh
git add scripts/e2e-notes.sh && git update-index --chmod=+x scripts/e2e-notes.sh
git ls-files -s scripts/e2e-notes.sh
```

  Expected: `syntax ok`; `Bourne-Again shell script, ASCII text executable` (no CRLF); `0`; and a mode of `100755`.
  Do not commit; leave the file staged for the orchestrator.

- [ ] **Step 3: Run it** (**needs spec 0005's code on the branch**, Docker, and ports 8088, 8080 and 8025 free:
  `netstat -ano | grep -E ':(8088|8080|8025) .*LISTENING'` prints nothing; another session may hold them). The script needs a
  working `python3` on `PATH` (standard library only); on this machine put a shim first:

```bash
mkdir -p /tmp/shim && printf '#!/bin/sh\nexec C:/p6v/Scripts/python.exe "$@"\n' > /tmp/shim/python3 && chmod +x /tmp/shim/python3
export PATH="/tmp/shim:$PATH"
export COMPOSE_PROJECT_NAME=auth-core-e2e-0006
cp .env.example .env                  # then POSTGRES_PASSWORD, AUTH_DEV_SEED_PASSWORD, NOTES_DB_PASSWORD to local values
MSYS2_ARG_CONV_EXCL="/CN" scripts/dev-keys.sh
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
scripts/e2e-notes.sh
scripts/e2e-notes.sh                  # again, on the same stack
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
```

  Expected: both runs print `PASS step 1` … `PASS step 6` and `ALL PASS` (about three minutes the first time, most of it the
  image build and step 3's wait for the server to pick up the invitation the CLI queued; less the second time). If step 3
  or 4 fails because the mail does not come, read the Mailpit inbox at `http://localhost:8025` and the log of the `auth`
  service; if step 1 says the roles are not those of the notes manifest, the volume is from another stack:
  `down -v` and run again. Record in the acceptance map's notes how long step 3 waited for the CLI invitation, and the
  number of polls of step 6.
  Until spec 0005 is on the branch, only steps 1 and 2 can run (and step 1 stops at the claims): the script's helpers and
  those two steps were run against a stand-in for Auth-Core's key set with the sample's test tokens, and passed.

- [ ] **Step 4: Hand over** — the file staged, nothing else changed. The orchestrator commits it
  as `test(e2e): notes sample over the real network`.

### Task 8: The integration guide and the README

**Files:**
- Create: `docs/integration/python-fastapi.md`
- Modify: `README.md`

**Interfaces:**
- Consumes: every file of the sample (the guide points at them), `scripts/e2e-notes.sh`.
- Produces: the guide of the spec's "Integration guide" (eight steps, each pointing at the sample's files; names no product).

- [ ] **Step 1: Write the guide.** `docs/integration/python-fastapi.md` (the one snippet of step 5 that is not in the sample,
  the migration for a product that already has data, was run against PostgreSQL 16 with two existing rows and a made-up
  company id: both rows got it, the column became `NOT NULL`, the index was made):

````markdown
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
from auth_core_fastapi import Principal

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

- **A proxy** (Caddy) on one port, routing `/auth/*` to Auth-Core and `/api/*` to your backend. Auth-Core and your backend
  publish no port of their own; only the proxy is reachable.
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
`/auth/org/...`, with no operator. [`scripts/e2e-notes.sh`](../../scripts/e2e-notes.sh) does all of this over HTTP, steps
3 to 5.

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
````

- [ ] **Step 2: The README.** Four insertions in `README.md`, each placed by the line it follows or
  precedes (not by a line number: spec 0005's plan inserts its own text near some of them).

  (a) In the status block, **before** the line `` > Implementation follows the milestones in [`docs/design.md`](docs/design.md). ``
  insert:

```
> A first consumer is built in this repository: a generic Python package for FastAPI backends (`clients/python/`), a small
> sample product that uses it (`samples/notes-api/`) and an integration guide
> ([spec 0006](docs/superpowers/specs/0006-python-consumer-package.md),
> [`docs/integration/python-fastapi.md`](docs/integration/python-fastapi.md)).
```

  (b) In "Quickstart (development)", **after** the line `` The stack includes a mail catcher; its inbox is at `http://localhost:8025`. ``
  insert a blank line and:

````
The sample product "notes" and a proxy that puts Auth-Core and the sample on one origin are a compose overlay on the same
stack. `scripts/e2e-notes.sh` starts it (it needs `NOTES_DB_PASSWORD` in `.env`) and drives it through the proxy at
`http://localhost:8088`, in about three minutes:

```bash
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
scripts/e2e-notes.sh
```

The Python package (`clients/python/`) and the sample have tests that need no Docker (Python 3.12):

```bash
python -m venv .venv && . .venv/bin/activate        # .venv/Scripts/activate on Windows
pip install -e "clients/python[test]" -r samples/notes-api/requirements.txt
(cd clients/python && python -m pytest -q) && (cd samples/notes-api && python -m pytest -q)
```
````

  (c) In "Repository layout (planned)", **after** the line `  clients/python/               FastAPI package` insert:

```
  samples/notes-api/            the "notes" sample product: FastAPI, PostgreSQL, a compose overlay with Caddy
  docs/integration/             guides for products that use Auth-Core
```

  (d) In "Documentation", **after** the bullet that starts ``- [`docs/workflow.md`](docs/workflow.md)`` (it ends the list)
  add:

```
- [`docs/integration/python-fastapi.md`](docs/integration/python-fastapi.md) — connecting a Python (FastAPI) product, step
  by step, with the sample as the worked example.
```

  The note in `docs/design.md` on Decisions 2 and 4 is **not** part of this task: the spec says it is added with "As built"
  (see "After the plan").

- [ ] **Step 3: Check the guide** (criterion 10): eight steps, every link and every path in a code span resolves, no product is
  named.

```bash
grep -c '^## [1-8]\. ' docs/integration/python-fastapi.md
(cd docs/integration && grep -o '](\.\./[^)#]*' python-fastapi.md | sed 's/^](//' | sort -u | while read -r p; do test -e "$p" || echo "MISSING $p"; done)
grep -o '`\(samples\|scripts\|clients\)/[^`]*`' docs/integration/python-fastapi.md | tr -d '`' | sort -u | while read -r p; do test -e "$p" || echo "MISSING $p"; done
grep -inE 'speech|stereo|investing' docs/integration/python-fastapi.md
git diff --stat -- README.md
```

  Expected: `8`; no output from the next three commands; and `README.md | 25 +` (a CRLF working copy counts the same). The anchors of Step 2 must each have matched exactly once.

- [ ] **Step 4: Hand over** — uncommitted: the two files, as
  `docs: integration guide for Python products; README quickstart`.

### Task 9: The acceptance map and the final gate

**Files:**
- Create: `docs/superpowers/plans/0006-acceptance-map.md`

**Interfaces:**
- Consumes: every test and script of Tasks 1–8.
- Produces: the table from criteria to guards that the verifiers use (layer 2 of `docs/workflow.md`), the Review Focus table,
  and two sections the orchestrator fills in.

- [ ] **Step 1: Write the map.** `docs/superpowers/plans/0006-acceptance-map.md`:

```markdown
# Spec 0006 — acceptance map

Maps each acceptance criterion of
[spec 0006](../specs/0006-python-consumer-package.md) to the tests that guard it,
for verifier layer 2 ([`docs/workflow.md`](../../workflow.md#verification)).
The package's tests live in `clients/python/tests/` and the sample's in `samples/notes-api/tests/`; both run with
`pytest` and need no Docker (the sample's can also run on PostgreSQL, see "How the tests run"). The e2e steps are in
`scripts/e2e-notes.sh` and run against the compose stack with the sample overlay (Auth-Core, PostgreSQL, the Mailpit
mail catcher, the notes service and Caddy) over real HTTP, through the proxy.

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | The Goal sequence and all six steps of `scripts/e2e-notes.sh` pass on a clean stack; the four earlier e2e scripts and the one of spec 0005 still pass on the stack without the overlay | `scripts/e2e-notes.sh` (steps 1–6); `scripts/e2e-login.sh`, `scripts/e2e-refresh.sh`, `scripts/e2e-lockout.sh`, `scripts/e2e-email.sh`, `scripts/e2e-tenancy.sh` unedited, run on `deploy/docker-compose.yml` alone, which this slice does not change |
| 2 | A token that meets every rule is accepted, and its `Principal` carries `sub`, `org_id`, `roles` and `permissions` | `test_tokens.test_valid_token_gives_the_principal_of_its_claims`, `test_tokens.test_the_scheme_is_case_insensitive`, `test_tokens.test_a_token_without_roles_or_permissions_is_valid_and_holds_none`, `test_tokens.test_an_audience_list_that_holds_the_configured_audience_is_valid`, `test_tokens.test_a_token_expired_less_than_five_minutes_ago_is_still_valid`, `test_server.test_a_token_is_checked_against_the_keys_served_over_http`; e2e step 1 |
| 3 | `401`, empty body, `WWW-Authenticate: Bearer` for each of the listed cases | no header: `test_tokens.test_no_header_is_a_401`; a scheme other than `Bearer`: `test_tokens.test_a_scheme_other_than_bearer_or_no_token_is_a_401`, `test_tokens.test_a_header_that_is_not_bearer_and_one_token_is_a_401`, `test_tokens.test_a_valid_token_in_another_header_is_a_401`; two headers: `test_tokens.test_two_authorization_headers_are_a_401_even_when_both_are_valid`; not a JWS: `test_tokens.test_a_token_that_is_not_a_jws_is_a_401`; `none`, `HS256` with the public key, another algorithm: `test_tokens.test_alg_none_is_a_401`, `test_tokens.test_hs256_signed_with_the_public_key_is_a_401`, `test_tokens.test_an_algorithm_other_than_rs256_is_a_401_even_with_a_good_signature`, `test_jwks_cache.test_only_rsa_keys_are_taken_from_the_key_set`; `typ`: `test_tokens.test_a_type_other_than_at_jwt_is_a_401`; `kid`: `test_tokens.test_a_token_without_a_usable_kid_is_a_401`, `test_tokens.test_a_kid_that_is_not_text_is_a_401`; a changed signature: `test_tokens.test_the_key_is_chosen_by_kid_alone_whatever_the_header_offers`, `test_tokens.test_a_changed_signature_is_a_401`, `test_tokens.test_a_changed_payload_is_a_401`, `test_tokens.test_a_token_signed_by_another_key_with_a_published_kid_is_a_401`; `iss`, `aud`: `test_tokens.test_a_wrong_issuer_is_a_401`, `test_tokens.test_a_wrong_audience_is_a_401`; expired: `test_tokens.test_a_token_expired_more_than_five_minutes_ago_is_a_401`; missing or mistyped claims: `test_tokens.test_a_missing_claim_or_one_of_the_wrong_type_is_a_401`, `test_tokens.test_a_token_without_a_registered_claim_is_a_401`, `test_tokens.test_a_token_issued_in_the_future_is_a_401`, `test_notes.test_no_token_is_a_401_on_every_endpoint`; e2e step 2 |
| 4 | `require_permission` answers `403 {"error":"forbidden"}` for a valid token without the permission, and lets a token with it through | `test_tokens.test_a_valid_token_without_the_permission_is_a_403`, `test_tokens.test_a_token_with_the_permission_is_let_through`, `test_tokens.test_the_permission_is_matched_whole`, `test_tokens.test_an_invalid_token_is_a_401_before_it_is_a_403`, `test_notes.test_a_viewer_may_read_and_may_not_write`, `test_notes.test_a_writer_without_the_read_permission_may_not_read`, `test_notes.test_a_refused_write_stores_nothing`; e2e steps 4, 5 |
| 5 | Keys: a new key after one refetch; at most one refetch per 10 seconds; held keys keep working while the URL fails; `503 {"error":"auth_unavailable"}` with no key and a failing URL | the cache: `test_jwks_cache.test_the_keys_are_fetched_on_first_use_and_kept`, `test_jwks_cache.test_a_new_key_is_accepted_after_one_refetch`, `test_jwks_cache.test_a_new_key_is_not_looked_for_within_ten_seconds_of_the_last_fetch`, `test_jwks_cache.test_many_unknown_kids_cause_one_fetch_in_ten_seconds`, `test_jwks_cache.test_the_keys_are_fetched_again_after_five_minutes_not_before`, `test_jwks_cache.test_a_key_that_left_the_key_set_stops_working_at_the_next_fetch`, `test_jwks_cache.test_held_keys_keep_working_while_the_fetch_fails`, `test_jwks_cache.test_a_failed_renewal_is_retried_no_sooner_than_ten_seconds_later`, `test_jwks_cache.test_no_key_and_a_failing_key_set_is_unavailable`, `test_jwks_cache.test_unavailable_stays_the_answer_for_ten_seconds_without_another_fetch`, `test_jwks_cache.test_the_answer_to_an_unknown_kid_names_the_state_of_the_latest_fetch`, `test_jwks_cache.test_unavailable_does_not_need_the_failed_fetch_to_have_run_for_the_request`, `test_jwks_cache.test_a_key_set_with_nothing_usable_counts_as_a_failed_fetch`, `test_jwks_cache.test_requests_that_wait_for_the_keys_share_one_fetch`, `test_jwks_cache.test_a_request_that_holds_its_key_does_not_wait_for_a_fetch_that_is_running`, `test_jwks_cache.test_the_fetch_gives_up_at_its_timeout`, `test_jwks_cache.test_a_server_that_trickles_the_answer_is_given_up_on_at_the_deadline_of_the_whole_fetch`, `test_jwks_cache.test_the_deadline_reaches_the_cache_as_an_unavailable_key_set`, `test_jwks_cache.test_the_defaults_are_the_rules_of_the_spec`, over HTTP: `test_jwks_cache.test_the_keys_are_fetched_over_http`, `test_jwks_cache.test_an_error_status_is_a_failed_fetch`, `test_jwks_cache.test_an_answer_that_is_not_a_key_set_is_a_failed_fetch`, `test_jwks_cache.test_a_key_set_just_under_the_size_limit_is_fetched`, `test_jwks_cache.test_a_valid_key_set_over_the_size_limit_is_refused_for_its_size`, `test_jwks_cache.test_nobody_listening_is_a_failed_fetch`; through the dependencies: `test_keys` (all), `test_server.test_an_unknown_kid_is_a_401_and_a_key_set_that_does_not_answer_is_a_503`, `test_server.test_a_slow_fetch_does_not_block_the_event_loop`, `test_notes.test_the_service_answers_503_while_auth_core_is_down_and_it_holds_no_key`; e2e step 6 |
| 6 | Creating the `AuthCore` object makes no network call | `test_core.test_creating_the_object_makes_no_network_call`, `test_jwks_cache.test_creating_the_cache_fetches_nothing`, `test_keys.test_the_keys_are_fetched_on_first_use_and_kept` |
| 7 | No token or claim value appears in the package's logs at any level | `test_logging.test_no_token_and_no_claim_value_is_logged_at_any_level`, `test_logging.test_a_rejected_token_is_logged_at_debug_with_the_reason_only`, `test_logging.test_a_rejected_token_is_not_logged_above_debug`, `test_logging.test_a_failed_fetch_is_logged_as_a_warning_by_the_class_of_the_error_only` |
| 8 | The sample never returns or changes a note of another company, whatever id it is given; another company's note and a non-UUID id are both `404` | `test_notes.test_another_companys_note_is_a_404_and_its_list_is_empty`, `test_notes.test_an_id_that_is_not_a_uuid_is_a_404`, `test_notes.test_only_the_written_form_of_an_id_finds_a_note`, `test_notes.test_the_company_of_a_new_note_is_the_tokens_whatever_the_body_says`, `test_notes.test_two_companies_keep_their_own_notes`, `test_notes.test_every_statement_on_the_notes_table_names_the_company`, `test_notes.test_a_valid_token_whose_company_is_not_a_uuid_is_a_401`; e2e step 3 |
| 9 | The package's tests run with `pytest` and no Docker, with keys generated in the test | all of `clients/python/tests/` (`helpers.Signer` makes the keys; `helpers.FakeJwks` and `helpers.JwksServer` stand in for the JWKS URL, the latter a server on 127.0.0.1 that the test starts) |
| 10 | The integration guide covers the eight steps, and every file it points at exists in the sample | the commands of Task 8, step 3 (the eight headings; every link and every path in code spans resolves) |

## How the tests run

- `cd clients/python && python -m pytest -q` — the package: 131 tests.
- `cd samples/notes-api && python -m pytest -q` — the sample on SQLite in memory (the real migration builds the table):
  57 tests. With `NOTES_TEST_DATABASE_URL=postgresql+psycopg://…` (an empty database) the same tests run on PostgreSQL,
  which is where the NUL character and a lone surrogate in a note are refused by the service and not by the database.
- The e2e script needs Docker, the Auth-Core image and the code of spec 0005 (the company API and the operator CLI).

## Review Focus (plan 0006)

| # | Failure mode | Guarding test(s) |
| - | ------------ | ---------------- |
| 1 | A note whose text the database cannot store (a NUL character, a lone surrogate), is too long, or whose body is not JSON, is a `400 invalid_request`, never a `500` | `test_notes.test_a_body_that_is_not_an_object_with_a_good_text_is_a_400`, `test_notes.test_a_body_over_the_size_cap_is_a_400_even_when_its_text_is_good`, `test_notes.test_a_body_under_the_size_cap_may_carry_other_members`, `test_notes.test_a_bad_body_is_a_401_or_a_403_before_it_is_a_400`, `test_notes.test_the_text_may_be_one_to_a_thousand_characters_of_any_script` |
| 2 | Auth-Core slow or hung when a key is needed: no other request waits for it, the fetch gives up at its timeout, N requests that wait share one fetch, a request that holds its key never waits | `test_server.test_a_slow_fetch_does_not_block_the_event_loop`, `test_jwks_cache.test_the_fetch_gives_up_at_its_timeout`, `test_jwks_cache.test_requests_that_wait_for_the_keys_share_one_fetch`, `test_jwks_cache.test_a_request_that_holds_its_key_does_not_wait_for_a_fetch_that_is_running` |
| 3 | Auth-Core is back after an outage: the answer stays `503` (not `401`, so the user stays signed in) until the 10 seconds since the failed fetch have passed | `test_jwks_cache.test_unavailable_stays_the_answer_for_ten_seconds_without_another_fetch`, `test_keys.test_an_unknown_kid_is_a_503_when_the_latest_fetch_failed_and_a_401_when_it_worked`; e2e step 6 (polls) |
| 4 | A valid token whose `org_id` is not a UUID, and an id in another spelling (`{…}`, without hyphens, `urn:uuid:`, a space, SQL text) never reach a query as another company's row and never end as a `500` | `test_notes.test_a_valid_token_whose_company_is_not_a_uuid_is_a_401`, `test_notes.test_only_the_written_form_of_an_id_finds_a_note`, `test_notes.test_an_id_that_is_not_a_uuid_is_a_404` |
| 5 | Odd headers and a token on the edge of expiry: two `Authorization` headers, a lower-case scheme, trailing text, a token expired 200 seconds ago (valid) and 400 seconds ago (not), a product that forgot `auth.install(app)` still answers with the right status and headers | `test_tokens.test_two_authorization_headers_are_a_401_even_when_both_are_valid`, `test_tokens.test_the_scheme_is_case_insensitive`, `test_tokens.test_a_header_that_is_not_bearer_and_one_token_is_a_401`, `test_tokens.test_a_token_expired_less_than_five_minutes_ago_is_still_valid`, `test_tokens.test_a_token_expired_more_than_five_minutes_ago_is_a_401`, `test_tokens.test_without_install_the_status_and_the_headers_are_still_right` |

## Contract sentences that are not acceptance criteria

| Sentence | Guarding test |
| -------- | ------------- |
| JSON bodies carry `Cache-Control: no-store` (the package's `401`, `403`, `503`, and the sample's answers) | `helpers.assert_unauthorized` and `helpers.assert_unavailable` (used by `test_tokens`, `test_keys`, `test_server`), `test_tokens.test_a_valid_token_without_the_permission_is_a_403`, `test_notes.test_every_answer_is_marked_never_to_be_stored` |
| `GET /api/notes` lists the company's notes newest first, as `[{"id","text","author_sub","created_at"}]` | `test_notes.test_a_note_is_added_and_listed`, `test_notes.test_notes_are_listed_newest_first` |
| `GET /api/notes/{id}` is one note; `POST` is `201` with the note; the text is 1–1000 characters | `test_notes.test_a_note_is_read_by_its_id`, `test_notes.test_the_text_may_be_one_to_a_thousand_characters_of_any_script` |
| `GET /api/health` is `200` when the database answers, needs no token | `test_notes.test_health_is_200_when_the_database_answers_and_needs_no_token`, `test_notes.test_health_is_503_when_the_database_does_not_answer` |
| The service applies its migrations at startup; the first migration creates the table of the spec | `test_notes.test_the_service_applies_its_migrations_at_startup`, `test_migration_and_settings.test_the_migration_creates_the_table_of_the_spec`, `test_migration_and_settings.test_the_migration_and_the_model_agree`, `test_migration_and_settings.test_migrating_twice_changes_nothing` |
| The sample's configuration is `AUTH_ISSUER`, `AUTH_AUDIENCE`, `AUTH_JWKS_URL`, `DATABASE_URL` | `test_migration_and_settings.test_the_settings_are_read_from_the_environment`, `test_migration_and_settings.test_a_missing_setting_is_named_and_its_value_is_not` |
| The default key set URL is the issuer plus `/.well-known/jwks.json`; a URL that is not http(s), an empty issuer, audience or permission is refused | `test_core.test_the_default_key_set_url_is_the_issuer_plus_the_well_known_path`, `test_core.test_a_key_set_url_that_is_not_http_is_refused`, `test_core.test_an_empty_issuer_or_audience_is_refused`, `test_core.test_an_empty_permission_is_refused` |
| The sample is reachable only through Caddy | `scripts/e2e-notes.sh` step 1 (`docker port` of the sample prints nothing), `Caddyfile` and `compose.yml` (`expose`, no `ports`) |

## Plan-vs-implementation notes

(Filled in by the orchestrator after implementation: every name or behaviour that differed from the plan, per task.)

## Local verification log

(Filled in by the orchestrator after the three verifiers have run.)
```

- [ ] **Step 2: Check that every name in it is a real test.** From the repository root, in the venv:

```bash
python - <<'EOF'
import glob, os, re
text = open("docs/superpowers/plans/0006-acceptance-map.md", encoding="utf-8").read()
defs = {}
for path in glob.glob("clients/python/tests/*.py") + glob.glob("samples/notes-api/tests/*.py"):
    module = os.path.basename(path)[:-3]
    for match in re.finditer(r"^(?:async )?def (\w+)\(", open(path, encoding="utf-8").read(), re.M):
        defs.setdefault(module, set()).add(match.group(1))
used, bad = set(), 0
for match in re.finditer(r"`(\w+)\.(\w+)`", text):
    module, name = match.groups()
    if module.startswith("test_"):
        used.add((module, name))
        if name not in defs.get(module, set()):
            print("MISSING", module, name)
            bad += 1
unmapped = [(m, n) for m, names in defs.items() if m.startswith("test_") for n in names
            if n.startswith("test_") and (m, n) not in used and f"`{m}`" not in text]
print("checked", len(used), "missing", bad, "unmapped", len(unmapped))
for item in sorted(unmapped):
    print("UNMAPPED", *item)
EOF
```

  Expected: `checked 102 missing 0 unmapped 0`. A name that moved or was renamed during the work is fixed in the map, not in
  the test.

- [ ] **Step 3: The final gate** (everything but the e2e run; no Docker needed except for the optional PostgreSQL run):

```bash
source C:/p6v/Scripts/activate
(cd clients/python && python -m pytest -q)
(cd samples/notes-api && python -m pytest -q)
grep -rInP --exclude-dir=__pycache__ --exclude-dir=.pytest_cache '\\[uU][0-9a-fA-F]{4}|[^\x00-\x7F]' clients samples scripts
bash -n scripts/e2e-notes.sh
git ls-files -s scripts/e2e-notes.sh
git diff --stat HEAD -- deploy/docker-compose.yml src tests
git status --porcelain
```

  Expected: `131 passed`; `57 passed`; no output from the `grep` (only ASCII, no escape written out); no output from `bash -n`;
  mode `100755`; no output from `git diff --stat` (nothing under `deploy/docker-compose.yml`, `src/` or `tests/` changed);
  and `git status --porcelain` listing only this slice's files (no `.env`, no `.secrets/`, no `.venv/`, no
  `__pycache__`).

- [ ] **Step 4: The e2e run and the regression pass.** **Precondition: spec 0005 is implemented and merged into this branch**
  (see "Preconditions" at the end); Docker; a compose project name of its own; the ports free. Tasks 6 and 7 may have removed
  `.env` and `.secrets/`, so make them again first, and put the `python3` shim first on the `PATH`:

```bash
mkdir -p /tmp/shim && printf '#!/bin/sh\nexec C:/p6v/Scripts/python.exe "$@"\n' > /tmp/shim/python3 && chmod +x /tmp/shim/python3
export PATH="/tmp/shim:$PATH"
export COMPOSE_PROJECT_NAME=auth-core-e2e-0006
[ -f .env ] || cp .env.example .env     # then POSTGRES_PASSWORD, AUTH_DEV_SEED_PASSWORD, NOTES_DB_PASSWORD to local values
[ -d .secrets ] || MSYS2_ARG_CONV_EXCL="/CN" scripts/dev-keys.sh
# with spec 0005 merged the base file mounts its own manifest at the same path: the overlay's mount must replace it
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env config | grep -c 'target: /etc/auth-core/auth.yaml'
# the overlay stack:
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
scripts/e2e-notes.sh
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
# the base stack, as before this slice:
docker compose -f deploy/docker-compose.yml --env-file .env down -v
docker compose -f deploy/docker-compose.yml --env-file .env up -d --build
scripts/e2e-login.sh && scripts/e2e-refresh.sh && scripts/e2e-lockout.sh && scripts/e2e-email.sh && scripts/e2e-tenancy.sh
docker compose -f deploy/docker-compose.yml --env-file .env down -v
```

  Expected: `1` from the `grep` (one mount of that path, whose source ends in `samples/notes-api/auth.yaml`; `2` would mean
  the overlay does not replace the base's mount); `ALL PASS` from `e2e-notes.sh` and from the five others, which are **unedited** (`e2e-login.sh` and
  `e2e-refresh.sh` need `PyJWT[crypto]` on the `python3` of the `PATH`: the venv of this plan has it). Then remove `.env` and
  `.secrets/`. Write the durations in the map's notes.

- [ ] **Step 5: Hand over** — uncommitted. The orchestrator commits the map as
  `docs: acceptance map for spec 0006`.

---

## Self-review against the spec

- **In scope, one by one.** The package (generic, `AuthCore`, `Principal`, `require_permission`, 401/403/503, keys, logging):
  Tasks 1–3. The sample (FastAPI on PostgreSQL, Alembic migration applied at startup, own `auth.yaml`, three endpoints
  and `health`, every query filtered by `org_id`): Tasks 4–5. The overlay (Caddy, `/auth` and `/api` on one origin, the sample
  reachable only through Caddy, `notes` database and its own login, manifest, issuer, audience, internal JWKS URL, base
  compose file unchanged): Task 6. `scripts/e2e-notes.sh`, six steps: Task 7. Tests of the package that need no Docker: Tasks
  1–3. The integration guide, eight steps: Task 8. The README quickstart and status the spec asks for: Task 8. The
  one-line note of `docs/design.md` on Decisions 2 and 4 is added with "As built" (see "After the plan"), as the spec says.
- **Decisions 1–9 of the spec** are built as written; 9 (5 minutes of skew) is the `LEEWAY_SECONDS` of `_verify.py` and two
  tests (200 s after `exp` accepted, 400 s refused).
- **No placeholder.** Every file of this plan is complete.
- **Names.** `JwksCache.key_for`, `KeyUnknown`, `KeysUnavailable`, `AuthCore.install` / `current_user` / `require_permission`,
  `AuthError`, `Principal`, `Settings.from_env`, `create_app`, `upgrade_database`, the fixtures and helpers are the same in
  every task that uses them (the test suites of Tasks 2, 3 and 5 import them).

## Findings: where the plan adds to, or goes beyond, the spec

The spec wins; each of these is a point the spec is silent on, or a place where it cannot be built as worded. None changes a
decision of the owner.

1. **A product must call `auth.install(app)`.** FastAPI's own handler for `HTTPException` always sends a JSON body, so an
   empty `401` and the exact `{"error":"forbidden"}` / `{"error":"auth_unavailable"}` bodies of the contract need a handler.
   The package exports `AuthError` and `AuthCore.install` for it; the spec's "Use at an endpoint" shows only the two
   dependencies. Without the call the status codes and `WWW-Authenticate` / `Cache-Control` headers are still right. The
   spec is amended to say so (Contract → "Use at an endpoint").
2. **`Cache-Control: no-store` is also on the empty `401`** (the spec says it of JSON bodies); Auth-Core puts it on every answer.
3. **The residual risk is 15 minutes, not 10, at the extreme.** The spec says a demoted member keeps the token's permissions at
   the product's endpoints "for up to 10 minutes" (the lifetime of the token); with the 5 minutes of skew of Decision 9 a token
   is accepted until 5 minutes after `exp`. The spec is amended to say 15 minutes (residual risks); the guide says "10
   minutes, plus the skew".
4. **After Auth-Core comes back, the sample can still answer `503` for up to 10 seconds** (the 10-second rule counts the
   failed fetch), where the spec's step 6 says "the same request gets 200". `scripts/e2e-notes.sh` polls for up to 60 seconds
   and allows only `503` until the `200`.
5. **The audience** is `notes-api`, set on both sides in the overlay; the spec says "Auth-Core's audience" and the
   development default is `auth-core-dev`.
6. **The package exports more than `AuthCore` and `Principal`**: `AuthError` (for `install`) and `JwksCache` (so that a
   product's tests can serve keys made in the test, as the sample's do; the guide documents it).
7. **The sample's choices where the spec is silent:** a request body over **16 KiB** is `400 invalid_request` (a resource
   limit chosen by the orchestrator, because the spec says nothing about the size of a body; the spec is amended to record it,
   and `test_a_body_over_the_size_cap_is_a_400_even_when_its_text_is_good` pins it); a NUL character or a lone surrogate in the text is
   `400 invalid_request` (PostgreSQL stores neither: it would be a `500`); an id is found only in its 36-character form; a
   valid token whose `org_id` is not a UUID is a `401`; `GET /api/health` is `503 {"error":"database_unavailable"}` when
   the database does not answer; every answer is `Cache-Control: no-store`; FastAPI's interactive docs are off; the list of
   notes has no paging.
8. **Three files outside the sample change for the stack to build and behave on Windows**: `.env.example` (the database
   password), `.gitattributes` and `.dockerignore` (Task 6), as well as the README the spec asks for.
9. **FastAPI 0.115, the floor the spec names, was not run**: only FastAPI 0.142.2 (with Starlette 1.7.0) is on this machine,
   and the packages are not to be downloaded again. The floors in `pyproject.toml` are the spec's (`fastapi>=0.115`,
   `PyJWT[crypto]>=2.8`); a verifier with both versions installed should run the suite on 0.115 as well.
10. **The fetch of the key set has a total deadline of 5 seconds, not only the socket timeout** of `urlopen` (which bounds
    each wait and not the whole transfer: a server that sends a byte a second would never trip it). The transfer runs in a
    worker thread that the caller stops waiting for at the deadline; the size limit of 1 MiB is checked chunk by chunk, and
    tested with a valid key set that is only too large.
11. **Starlette 1.x warns that `fastapi.testclient` with `httpx` is deprecated** and suggests `httpx2`, which is not on the
    owner's list; the warning is expected and the tests do not depend on it.

## After the plan: verify, then merge

1. Dispatch the three local verifiers **in parallel** (Sonnet, fresh context, read-only), as defined in
   [`docs/workflow.md`](../../workflow.md#verification): realization vs **spec** (8 layers, using the acceptance map, and
   checking the five Review Focus lines), API/e2e (a clean stack with the overlay, `scripts/e2e-notes.sh`, taking each
   invitation token from the mail as delivered; then the five earlier scripts on the stack without the overlay as the regression
   pass), and security. Only one of them builds and tests in the tree; only one uses compose and ports 8088, 8080 and 8025,
   with a compose project name of its own.
2. What the security verifier is asked to prove, from the spec's notes: no algorithm other than RS256 is accepted, algorithm
   confusion with the public key included; a key is chosen only by `kid` from the configured JWKS (nothing in the token's
   header is followed); unknown `kid`s cannot make the package fetch more than once every 10 seconds; no token or claim is
   logged; every query of the sample is filtered by the token's `org_id`; the sample is not reachable except through Caddy
   (`docker port`, and a request to the host at port 8000).
3. Each finding carries a `scope`. At most 2 fix rounds per verifier, then the issue goes to the owner.
4. Record the outcome: the plan-vs-implementation notes and the verification log in the acceptance map, and an
   `## As built (owner, date)` section in spec 0006. The note of `docs/design.md` on Decisions 2 and 4 is added in this
   step, with the as-built section, as the spec says: in the list of "Week 4: first consumer, backend", **after** the line
   ``  - First test on a real phone (cookies, proxy, iOS PWA)`` insert

```
  - *As built (spec 0006):* the first consumer is a sample product inside this repository, not speech-to-mail (Decision 2), and the real-phone test moved to slice 7 (Decision 4).
```
5. When every verifier passes, merge the feature branch into `main` locally.

## Open questions for owner, and preconditions

1. **The tag `python-v0.1.0`.** The guide, the sample's `requirements.txt` and the spec's installation line name it, but
   nothing in this plan creates it, and until it exists in the GitHub repository `pip install …@python-v0.1.0#subdirectory=
   clients/python` cannot resolve. Suggested: the orchestrator tags the merge commit on `main` locally, and the owner pushes
   the tag with the first push after the merge. Is that the intent (spec: "versioned with the token contract")?
2. **The base image `python:3.12-slim`** in the sample's Dockerfile is not among the packages the spec lists for approval (it
   lists FastAPI, PyJWT, Uvicorn, SQLAlchemy, Alembic, psycopg, pytest, httpx, the Caddy 2 image and a build backend). It is
   already on this machine. Is it approved, or should the sample build from another base?

### Preconditions

- **Spec 0005 must be implemented and merged into this branch before Task 7, step 3, and Task 9, step 4, can run**: the
  e2e script's steps 3–6 and the regression pass need its company API, its operator CLI and its claims. Groups A and B, and
  the static checks of Task 7, do not wait for it; **slice 6 is gated and merged only after slice 5 is.**
- Once slice 5 is merged, **re-check that the overlay's mount of `auth.yaml` replaces the base file's** (Task 9, step 4, first
  command: `1`, not `2`). Before slice 5 the base file has no such mount, so the check could only be made on a copy of it
  with plan 0005's change applied, which gave `1`.

**Risks found and not fixed here:**

- The package keeps the keys of one JWKS URL in the memory of each process: a product run with several worker processes
  fetches the key set once per process. Accepted: the fetch is rare and bounded.
- A product that sets `jwks_url` to an address an attacker controls accepts the attacker's tokens (spec, residual risk). The
  package refuses a scheme other than `http` and `https`; the guide tells a product to use HTTPS or an internal address.

## As built

Implemented and verified locally. Tasks 1-9 were built from this plan, one implementer per task; each task's commit
was reviewed against the spec before the next began. What changed after the task reviews: a deeply nested JSON body
answers `400 invalid_request` in the sample (Task 5), and the guide and the overlay comment no longer say that Auth-Core
publishes no port (Task 8). The review of the whole branch led to one fix wave (the key cache no longer waits for a fetch
that runs too long, the package's version floors and its single-sourced version, the e2e script's project name, the
`.gitignore`, two sentences of the guide, the map). Four rounds of fixes followed the verifiers: the key cache (a request
that lacks its key is answered `503` at once while a fetch runs, no proxy and no redirect in the fetch, `aud` a string,
held keys that expire after 24 hours, a clock that raises, a positive `min_interval`); the sample (JSON `404`, `405` and
`503` shapes, a `500` logged by its class alone, the health check logging the start and the end of an outage); the overlay
and the image (Caddy and PostgreSQL hardening, digests, hash-pinned packages and build tool); and the e2e script and the
guide. They are listed in the [acceptance map](0006-acceptance-map.md) ("Plan-vs-implementation notes", "Local
verification log") and in [spec 0006](../specs/0006-python-consumer-package.md), "As built". 206 package tests and 97
sample tests pass (131 and 57 planned); the 935 .NET tests are unchanged; `scripts/e2e-notes.sh` and the five earlier
e2e scripts pass on a clean stack.

