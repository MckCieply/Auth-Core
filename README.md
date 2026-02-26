# Auth-Core

A self-hosted authentication service in .NET: sign-in, companies, roles and token refresh, ready to put in front of any product.

> **Version 0.1.0.** Image `ghcr.io/mckcieply/auth-core:0.1.0` (public, `linux/amd64`). See the [changelog](CHANGELOG.md).

## What it is

- **One service per product.** It runs next to your product, behind the same reverse proxy, at `/auth` of the product's own domain. It has its
  own PostgreSQL database, users and signing keys.
- **Headless.** A REST API with no screens: your frontend owns the login, reset and invitation pages, in its own look.
- **Standard tokens.** Short RS256 JWT access tokens that carry the user, the company, the roles and the permissions. Any backend in any language
  checks them offline against the public keys at `/auth/.well-known/jwks.json`.
- **Companies built in.** Users belong to companies, with members, roles, invitations and a permission catalog that your product declares.

```
Browser ──> app.example.com (your reverse proxy: HTTPS, HSTS)
                ├── /api  ──> your backend (checks the JWT offline against the cached key set)
                └── /auth ──> Auth-Core  (issues tokens, serves the keys, manages companies)
```

## Why use it

- **Stop rebuilding login.** Sign-in, sessions, password reset, email verification, invitations, companies and roles come as one container
  and a database, the same in every product.
- **Safe defaults.** The refresh token is an `HttpOnly; Secure; SameSite=Strict; Path=/auth` cookie that rotates on every use, with reuse
  detection; the access token (10 minutes) lives in the frontend's memory only. Lockout with growing cooldowns, per-IP rate limits, security
  headers and an audit log are on from the start.
- **No call per request.** A backend verifies tokens locally, so Auth-Core is not in the path of every API call.
- **Your data, your server.** No external identity provider and no per-user fees. It runs on a small VPS with Docker.
- **Ready to run.** A production compose file (read-only service, no capabilities, its own database role), a deployment guide, backup and
  key-rotation runbooks and a STRIDE threat model.

It is not for everything:

- **No SSO between products.** Each product has its own instance and its own users.
- **Never in scope:** SAML, SCIM, LDAP, multi-realm mode, fine-grained resource permissions ("is this my document?" stays in your product).
- **An internal tool, public as a portfolio piece.** There is no support, docs site or marketing.

## How to use it

### 1. Declare your product's permissions

A manifest lists the permissions your code checks and the roles a new company starts with ([`deploy/auth.yaml`](deploy/auth.yaml) is the
example):

```yaml
permissions: [documents:read, documents:write, documents:approve]
default_roles:
  admin: ["*"]
  user: [documents:read, documents:write]
```

### 2. Run it on your server

You need an x86-64 Linux server with Docker, a domain and an SMTP relay with TLS. The compose file is
[`deploy/docker-compose.prod.yml`](deploy/docker-compose.prod.yml); [`docs/deployment/vps.md`](docs/deployment/vps.md) takes you from nothing
to a first sign-in, with a `Caddyfile` for HTTPS.

### 3. Create the first company

The operator's commands are subcommands of the service's own binary, so they run from its image (`$C` is the compose command of the
deployment guide):

```bash
$C run --rm -T --no-deps auth admin create-org --name "Acme"            # prints the company id
$C run --rm -T --no-deps auth admin invite --org <id> --email boss@acme.example --role admin
```

The invited admin gets a mail, sets a password on your frontend's invitation screen and signs in. Anyone who holds `members:manage`
invites the rest of the company through the company API (`POST /auth/org/invites`).

### 4. Connect your backend

A FastAPI backend uses the package in [`clients/python/`](clients/python/) (`auth-core-fastapi`, installed from the repository at a tag):

```python
from fastapi import Depends, FastAPI
from auth_core_fastapi import AuthCore, Principal

app = FastAPI()
auth = AuthCore(issuer="https://app.example.com/auth", audience="my-product-api")
auth.install(app)


@app.post("/api/documents")
def add(user: Principal = Depends(auth.require_permission("documents:write"))):
    return {"company": user.org_id}   # every query is filtered by user.org_id
```

The guide is [`docs/integration/python-fastapi.md`](docs/integration/python-fastapi.md), with the sample product `samples/notes-api`. A backend
in another language checks the JWT with any library that reads a JWKS.

### 5. Connect your frontend

An Angular app copies three files (a service, an interceptor and a guard) and adds the screens the mail links open. The guide is
[`docs/integration/angular.md`](docs/integration/angular.md), with the working app `samples/notes-web`.

The API is described in OpenAPI at `/auth/openapi/v1.json`.

## Develop it

Needs the .NET 10 SDK, Docker with Compose, `openssl`, and Python 3 with `PyJWT[crypto]` for the e2e checks.

```bash
cp .env.example .env            # set local values; git-ignored
scripts/dev-keys.sh             # dev signing/encryption keys into .secrets/ (git-ignored)
# On Windows (Git Bash) run it as: MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh
export COMPOSE_PROJECT_NAME=auth-core-hardening   # a Compose project of its own, so the checks never touch another stack
AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build
scripts/e2e-login.sh            # login → JWKS → PyJWT verify → restart → verify again
scripts/e2e-refresh.sh          # refresh → rotation → reuse detection → logout (~30 s)
scripts/e2e-lockout.sh          # lockout → cooldown → timing medians (~2.5 min)
scripts/e2e-email.sh            # verification → reset → sessions end → mail outage (~1 min)
scripts/e2e-tenancy.sh          # CLI → invitations → roles and safety rules → removal (~2 min)
docker compose -f deploy/docker-compose.yml --env-file .env up -d   # recreates auth with the rate limits on
scripts/e2e-hardening.sh        # limits, headers, the outage, company deletion, the audit log (~6 min)
docker compose -f deploy/docker-compose.yml --env-file .env down -v
unset COMPOSE_PROJECT_NAME        # the project is gone: do not leave the name in your shell
```

The per-IP rate limits (30 logins a minute, and so on) are on by default. The lockout and mail checks exceed them on purpose, so the older scripts run on a stack
started with the limiter off; `scripts/e2e-hardening.sh` runs last, on the same stack recreated with the defaults. The stack includes a mail catcher at
`http://localhost:8025`. The API is described at `http://localhost:8080/auth/openapi/v1.json`; in Development an interactive reference is at
`http://localhost:8080/auth/scalar`.

With a development stack up, set `COMPOSE_PROJECT_NAME` to that stack's project (the function refuses to run without it) to use the operator's
commands (the invitation is mailed at the server's next poll, within a minute, and only while the server is running):

```bash
auth() { docker compose -p "${COMPOSE_PROJECT_NAME:?set it to the project of your stack}" -f deploy/docker-compose.yml --env-file .env run --rm -T --no-deps auth admin "$@"; }
auth create-org --name "Acme"                                     # prints the company id
auth invite --org <id> --email boss@acme.test --role admin
auth list-orgs                                                    # id, name, number of members
auth remove-member --org <id> --email worker@acme.test [--force]  # --force overrides last_manager
auth delete-org --org <id> --confirm "Acme"                       # deletes the company; the name must be exact
```

The commands work on the database with the service's configuration and never migrate it. A product's permissions and default roles come from its manifest
(`Auth:Manifest:Path`; `deploy/auth.yaml` in development): a broken file never stops the service, and `/auth/health` then says `Degraded`.

The samples are overlays of the same stack: the notes service and a proxy (`scripts/e2e-notes.sh`, needs `NOTES_DB_PASSWORD` in `.env`, on `http://localhost:8088`, its own
compose project `auth-core-notes`), and the Angular app on top of them (`scripts/e2e-web.sh`, on `https://localhost:8443`, `auth-core-web`).

Tests (integration, PostgreSQL by Testcontainers: Docker must be running):

```bash
dotnet build -warnaserror && dotnet test
```

The Python package and the sample have tests that need no Docker (Python 3.12):

```bash
python -m venv .venv && . .venv/bin/activate        # on Windows in Git Bash: . .venv/Scripts/activate
pip install -e "clients/python[test]" -r samples/notes-api/requirements.txt
(cd clients/python && python -m pytest -q) && (cd samples/notes-api && python -m pytest -q)
```

The Angular app has its own: `cd samples/notes-web && npx ng test --watch=false`.

## Operations

- [`docs/deployment/vps.md`](docs/deployment/vps.md): the deployment guide, upgrading and troubleshooting.
- [`docs/operations/backup.md`](docs/operations/backup.md): what to back up, a nightly dump, a restore step by step, and the audit queries.
- [`docs/operations/key-rotation.md`](docs/operations/key-rotation.md): generating production keys, planned and emergency rotation (it signs everyone out).
- [`docs/security/threat-model.md`](docs/security/threat-model.md): STRIDE for each element, and the residual risks that are accepted.

## Stack

.NET 10 LTS, C# 14, ASP.NET Core minimal APIs, ASP.NET Core Identity, OpenIddict 7, EF Core 10, PostgreSQL 16, MailKit, YamlDotNet, Microsoft.AspNetCore.OpenApi, Scalar.AspNetCore
(Development only), xUnit v3 and Testcontainers. Samples: FastAPI, SQLAlchemy and Alembic, Angular 21, Caddy 2.

## Repository layout

```
src/Auth.Server/              ASP.NET Core host, endpoints, OpenIddict, the operator CLI
src/Auth.Infrastructure/      EF Core model and migrations, identity
tests/Auth.IntegrationTests/  WebApplicationFactory + Testcontainers
clients/python/               auth-core-fastapi: the package for FastAPI backends
samples/notes-api/            the "notes" product: FastAPI and PostgreSQL behind Caddy
samples/notes-web/            its Angular frontend
deploy/                       the development compose file, the production compose file, the manifest example
scripts/                      dev keys and the e2e checks of every slice
docs/                         design brief, ADRs, integration guides, deployment, operations, security, specs and plans
```

## Documentation

- [`docs/design.md`](docs/design.md): the MVP brief: goals, decisions, scope, architecture, contracts, timeline, risks.
- [`docs/adr/`](docs/adr/): architecture decision records.
- [`docs/workflow.md`](docs/workflow.md): how the work is done: the spec-driven flow, verification, branches.
- [`docs/superpowers/specs/`](docs/superpowers/specs/) and [`plans/`](docs/superpowers/plans/): the specs (0001 to 0008) and the plans and acceptance maps built from them.

## License

[MIT](LICENSE) © 2026 Aleksander Torka
