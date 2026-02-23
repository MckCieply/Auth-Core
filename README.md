# Auth-Core

One reusable, self-hosted authentication service in .NET that every future
project drops in instead of rebuilding login, tenancy, RBAC and token refresh.

`speech-to-mail` is the first consumer; the MVP milestone is a working end-to-end
login on a real phone, delivered over a six-week build.

> **Status:** Week 1 milestone done — `POST /auth/login` issues RS256 JWTs and
> `GET /auth/.well-known/jwks.json` publishes the verification key
> ([spec 0001](docs/superpowers/specs/0001-login-and-token-issuance.md)).
> Week 2's session half is done too: `POST /auth/refresh` and `POST /auth/logout`
> ([spec 0002](docs/superpowers/specs/0002-refresh-and-logout.md)).
> Login is protected by a per-identifier lockout
> ([spec 0003](docs/superpowers/specs/0003-lockout-and-abuse-resistance.md)).
> Password reset and email verification by mail are in
> ([spec 0004](docs/superpowers/specs/0004-email-flows.md)).
> Companies, members, roles and invitations are in too: the access token carries `org_id`, `roles`
> and `permissions`, the company API manages them, an operator CLI creates companies, and the whole
> API is described in OpenAPI
> ([spec 0005](docs/superpowers/specs/0005-tenancy-and-rbac.md)).
> A first consumer is built in this repository: a generic Python package for FastAPI backends (`clients/python/`), a small
> sample product that uses it (`samples/notes-api/`) and an integration guide
> ([spec 0006](docs/superpowers/specs/0006-python-consumer-package.md),
> [`docs/integration/python-fastapi.md`](docs/integration/python-fastapi.md)).
> Implementation follows the milestones in [`docs/design.md`](docs/design.md).

## Quickstart (development)

Needs the .NET 10 SDK, Docker with Compose, `openssl`, and Python 3 with
`PyJWT[crypto]` for the e2e check.

```bash
cp .env.example .env            # set local values; git-ignored
scripts/dev-keys.sh             # dev signing/encryption keys into .secrets/ (git-ignored)
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
```

The per-IP rate limits (30 logins a minute, and so on) are on by default. The lockout and mail checks exceed them on purpose, so the
older scripts run on a stack started with the limiter off; `scripts/e2e-hardening.sh` runs last, on the same stack recreated with the
defaults.

The stack includes a mail catcher; its inbox is at `http://localhost:8025`.

The operator's commands are subcommands of the service's own binary, so they run from its image. With the
stack up, create a company and invite its first admin (the invitation is mailed at the server's next poll,
within a minute, and only while the server is running):

```bash
auth() { docker compose -f deploy/docker-compose.yml --env-file .env run --rm -T --no-deps auth admin "$@"; }
auth create-org --name "Acme"                                    # prints the company id
auth invite --org <id> --email boss@acme.test --role admin
auth list-orgs                                                   # id, name, number of members
auth remove-member --org <id> --email worker@acme.test [--force] # --force overrides last_manager
```

The commands work on the database with the service's configuration and never migrate it. The API is
described at `http://localhost:8080/auth/openapi/v1.json`; in Development an interactive reference is at
`http://localhost:8080/auth/scalar`. The product's permissions and default roles come from its manifest
(`Auth:Manifest:Path`; `deploy/auth.yaml` in development): a broken file never stops the service, and
`/auth/health` then says `Degraded`.

The sample product "notes" and a proxy that puts Auth-Core and the sample on one origin are a compose overlay on the same
stack. `scripts/e2e-notes.sh` starts it (it needs `NOTES_DB_PASSWORD` in `.env`) and drives it through the proxy at
`http://localhost:8088`, in about three minutes:

```bash
export COMPOSE_PROJECT_NAME=auth-core-notes   # the script's own compose project; the stack above is auth-core
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
scripts/e2e-notes.sh
```

The Python package (`clients/python/`) and the sample have tests that need no Docker (Python 3.12):

```bash
python -m venv .venv && . .venv/bin/activate        # .venv/Scripts/activate on Windows
pip install -e "clients/python[test]" -r samples/notes-api/requirements.txt
(cd clients/python && python -m pytest -q) && (cd samples/notes-api && python -m pytest -q)
```

Tests (integration, Postgres via Testcontainers — Docker must be running):

```bash
dotnet build -warnaserror && dotnet test
```

## What this is (and is not)

- **Internal tool, not a product for sale.** The repo may be public as a
  portfolio piece, but there is no support, docs site or marketing.
- **Consumers are unrelated projects.** No SSO and no shared users between them.
- **One instance per consumer:** each project gets its own database, users and
  signing keys. No realm dimension in code.
- **Headless REST API.** Each app owns its own login screens; the service issues
  standard JWTs that any language can verify.

## Key decisions at a glance

| Decision      | Choice                                                             |
| ------------- | ----------------------------------------------------------------- |
| Deployment    | One instance per consumer (own DB, users, signing keys)           |
| Interface     | Headless REST API, served at `/auth` on the consumer's domain     |
| Token engine  | OpenIddict issuing JWTs (RS256), JWKS for offline verification    |
| Token storage | Refresh token in `HttpOnly; Secure; SameSite=Strict` cookie       |
| Tenancy       | Organizations inside an instance; global users with memberships   |
| Authorization | RBAC: per-app permission catalog, permissions travel in the token |
| Configuration | `auth.yaml` manifest in the consumer's repo, applied idempotently |
| Stack         | .NET 10 LTS, ASP.NET Core Identity, OpenIddict, EF Core, Postgres |

## Stack

.NET 10 LTS · C# 14 · ASP.NET Core Minimal APIs · ASP.NET Core Identity ·
OpenIddict 7 · EF Core 10 · PostgreSQL 16 · MailKit · YamlDotNet ·
Microsoft.AspNetCore.OpenApi · Scalar.AspNetCore (Development only) · xUnit v3 + Testcontainers.

## Repository layout (planned)

```
auth-service/
  src/Auth.Server/              ASP.NET Core host, endpoints, OpenIddict
  src/Auth.Core/                users, orgs, memberships, roles, permissions
  src/Auth.Infrastructure/      EF Core, migrations, SMTP, manifest loader
  src/Auth.Cli/                 admin CLI
  tests/Auth.IntegrationTests/  WebApplicationFactory + Testcontainers
  clients/python/               FastAPI package
  samples/notes-api/            the "notes" sample product: FastAPI, PostgreSQL, a compose overlay with Caddy
  docs/integration/             guides for products that use Auth-Core
  docs/adr/  docs/threat-model.md
  deploy/docker-compose.example.yml
  auth.example.yaml
```

## Documentation

- [`docs/design.md`](docs/design.md) — full MVP brief: goals, decisions, scope,
  architecture, contracts, timeline, risks.
- [`docs/adr/`](docs/adr/) — architecture decision records.
- [`docs/workflow.md`](docs/workflow.md) — development workflow: model policy,
  plugins, the spec-driven flow, branch and PR rules.
- [`docs/integration/python-fastapi.md`](docs/integration/python-fastapi.md) — connecting a Python (FastAPI) product, step
  by step, with the sample as the worked example.

## License

[MIT](LICENSE) © 2026 Aleksander Torka
