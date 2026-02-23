# Auth-Core

One reusable, self-hosted authentication service in .NET that every project drops in instead of rebuilding login, tenancy, RBAC and token
refresh. One instance per product, behind the product's own reverse proxy at `/auth`; headless: each product owns its screens.

> **Version 0.1.0.** The image is `ghcr.io/mckcieply/auth-core:0.1.0`. Sign-in with RS256 tokens and a rotating refresh cookie, lockout and
> per-IP rate limits, password reset and email verification by mail, companies with members, roles and invitations, an operator CLI, an
> audit log, security headers, an OpenAPI description, a Python package and an Angular sample, a production compose file and the runbooks to
> run it. See the [changelog](CHANGELOG.md), the [deployment guide](docs/deployment/vps.md) and the [threat model](docs/security/threat-model.md).

## How it fits

```
Browser ──> app.example.com (your reverse proxy: HTTPS, HSTS)
                ├── /api  ──> your backend (checks the JWT offline against the cached key set)
                └── /auth ──> Auth-Core  (issues tokens, serves the keys, manages companies)
```

The refresh token is an `HttpOnly; Secure; SameSite=Strict; Path=/auth` cookie; the access token (10 minutes) lives in the SPA's memory. A backend never calls
Auth-Core per request: it verifies the token with the public keys at `/auth/.well-known/jwks.json`.

## Run it in development

Needs the .NET 10 SDK, Docker with Compose, `openssl`, and Python 3 with `PyJWT[crypto]` for the e2e checks.

```bash
cp .env.example .env            # set local values; git-ignored
scripts/dev-keys.sh             # dev signing/encryption keys into .secrets/ (git-ignored)
# On Windows (Git Bash) run it as: MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh
export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own for this sequence, never "auth-core" (your development stack)
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

The operator's commands are subcommands of the service's own binary, so they run from its image. With a development stack up, set `COMPOSE_PROJECT_NAME` to that
stack's project (the function refuses to run without it), then create a company and invite its first admin (the invitation is mailed at the server's next poll,
within a minute, and only while the server is running):

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

## Run it for real

The image is public and the compose file is `deploy/docker-compose.prod.yml`: PostgreSQL pinned by digest, the service read-only with no capabilities, the
port on `127.0.0.1` only. A server with Docker, a domain and an SMTP relay is enough; the guide goes from nothing to a first sign-in,
with a `Caddyfile` for HTTPS, upgrading and troubleshooting:

- [`docs/deployment/vps.md`](docs/deployment/vps.md): the deployment guide.
- [`docs/operations/backup.md`](docs/operations/backup.md): what to back up, a nightly dump, a restore step by step, and the audit queries.
- [`docs/operations/key-rotation.md`](docs/operations/key-rotation.md): generating production keys, planned and emergency rotation (it signs everyone out).
- [`docs/security/threat-model.md`](docs/security/threat-model.md): STRIDE for each element, and the residual risks that are accepted.

## Connect a product

- A Python (FastAPI) backend: [`clients/python/`](clients/python/) (`auth-core-fastapi`, installed from the repository at a tag) and
  [`docs/integration/python-fastapi.md`](docs/integration/python-fastapi.md), with the sample product `samples/notes-api` as the worked example.
- An Angular frontend: [`docs/integration/angular.md`](docs/integration/angular.md), with `samples/notes-web`.

## What this is (and is not)

- **Internal tool, not a product for sale.** The repository is public as a portfolio piece; there is no support, docs site or marketing.
- **Consumers are unrelated projects.** No SSO and no shared users between them; one instance per consumer, with its own database, users and keys.
- **Headless REST API.** Each app owns its login screens; the service issues standard JWTs that any language can verify.
- **Never in scope:** SAML, SCIM, LDAP, multi-realm mode, fine-grained resource permissions.

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
