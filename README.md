# Auth-Core

Self-hosted sign-in, companies and roles for B2B products with a backend in any language: one container and its own database, kept apart from your app, with sign-in screens in your own look.

[![Latest release](https://img.shields.io/github/v/release/MckCieply/Auth-Core?sort=semver&filter=v*&label=release)](https://github.com/MckCieply/Auth-Core/releases/latest) · [Site](https://mckcieply.github.io/Auth-Core/)

> **Version 0.4.0.** Image `ghcr.io/mckcieply/auth-core:0.4.0` (public, `linux/amd64`). See the [releases](https://github.com/MckCieply/Auth-Core/releases) and the [changelog](CHANGELOG.md).

**Price:** EUR 490 a year per product, net of VAT, flat, for any number of users. It covers production use, security fixes and best-effort email support. Development, testing, evaluation and some non-commercial uses are free ([License](#license)). The source is public, and each version becomes Apache 2.0 three years after release.

## Why use it

- **Isolated from your app.** Auth-Core is its own container with its own PostgreSQL database, users and signing keys. It connects to the database as a role of its own (the database's superuser is for the operator only). The container runs as a non-root user with a read-only file system, `/tmp` in memory, all Linux capabilities dropped and `no-new-privileges`, and it listens on loopback only, behind your reverse proxy. Your backend never touches the auth database and needs only the public keys.
- **Your own screens.** Auth-Core is headless: a REST API with no pages. Sign-in, password reset and invitation screens are pages of your product, in its own look, and not a redirect to someone else's hosted page.
- **Works with any backend.** Access tokens are standard RS256 JWTs (10 minutes) that carry the user, the company, the roles and the permissions. Any language checks them offline, with any JWT library that reads a JWKS, against the public keys at `/auth/.well-known/jwks.json`. FastAPI and Angular have ready-made guides below. Auth-Core is not in the path of every API call.
- **Companies built in.** Users belong to companies (a private person to a hidden one-person space of their own), with members, roles, invitations and a permission catalog that your product declares. Sign-in, sessions, password reset, email verification and invitations come as one container and a database, the same in every product.
- **Safe defaults.** The refresh token is an `HttpOnly; Secure; SameSite=Strict; Path=/auth` cookie that rotates on every use, with reuse detection; the access token lives in the frontend's memory only. Lockout with growing cooldowns, per-IP rate limits, security headers and an audit log are on from the start.
- **Your data, your server.** No external identity provider is needed and there are no per-user fees; sign-in with Google, Microsoft and GitHub is optional. It runs on a small x86-64 Linux VPS with Docker. The repository has a production compose file, a deployment guide, backup and key-rotation runbooks and a STRIDE threat model.

```
Browser ──> app.example.com (your reverse proxy: HTTPS, HSTS)
                ├── /api  ──> your backend (checks the JWT offline against the cached key set)
                └── /auth ──> Auth-Core  (issues tokens, serves the keys, manages companies)
```

## What it adds to run

- One container and one PostgreSQL 16 database, from the [production compose file](deploy/docker-compose.prod.yml), behind a reverse proxy that ends TLS.
- An SMTP relay with TLS (Auth-Core refuses to start with a relay that does not use TLS) and a sender address on a domain you control.
- Upgrades: read the changelog, back up, set the new version in `.env`, pull and start. The service migrates its database at start, and people stay signed in. A rollback is a restore of the backup. See [`docs/deployment/vps.md`](docs/deployment/vps.md), "Upgrading to a new version".
- A nightly backup and a restore that you have tried: [`docs/operations/backup.md`](docs/operations/backup.md).
- Key rotation when a key may have leaked, or on a plan; it signs everyone out: [`docs/operations/key-rotation.md`](docs/operations/key-rotation.md).

## Status

- Version 0.4.0. Auth-Core runs in production in Toviq. One author.
- There has been no external audit yet. What the design defends against, and the risks that are accepted, are in the [threat model](docs/security/threat-model.md) (STRIDE for each element).
- The features have end-to-end suites that run against a real stack; see [Develop it](#develop-it) for the scripts.
- Runbooks: [deployment and upgrading](docs/deployment/vps.md), [backup](docs/operations/backup.md), [key rotation](docs/operations/key-rotation.md).

**Not there yet:** MFA (TOTP) and passkeys are planned, with no date. Until then sign-in is a password, or Google, Microsoft or GitHub.

**Not in scope:**

- **No SSO between products.** Each product has its own instance and its own users.
- **Never:** SAML, SCIM, LDAP, multi-realm mode, fine-grained resource permissions ("is this my document?" stays in your product).

## Auth-Core or something else

| | Pick it when | Auth-Core instead when |
|---|---|---|
| **Better Auth** | Your backend is TypeScript and you want auth as a library inside the app, with 2FA and passkeys. | Your backend is in another language, or you want auth apart from the app, with its own database. |
| **Keycloak** | You need SSO across products, SAML or LDAP, or the broadest set of standards. | You want a small service for one product, with companies and roles built in. |
| **Clerk** | You want ready-made sign-in screens and MFA today, as a service. | You want to host it yourself, keep the users' data on your server, and pay a flat yearly price. |
| **WorkOS** | You sell to enterprises and need SSO and directory sync per customer. | Your customers sign in with a password or Google, Microsoft or GitHub, and you want to host it yourself. |
| **Auth-Core** | You want one self-hosted service per product, isolated from the app, any backend language. | |

Auth-Core does not fit when you need MFA today, SSO across products, SAML or SCIM, ready-made screens, or a TypeScript backend that wants auth inside the app.

As of November 2026.

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

`signup: self-service` is optional (the default is `invite-only`, and a manifest without the key behaves as in `0.1.0`): it lets a visitor start a company of their own, and it
needs the address of your frontend's completion screen as well (`AUTH_FRONTEND_SIGNUP_URL`, see [`docs/deployment/vps.md`](docs/deployment/vps.md), "Self-service sign-up"). Without
that address sign-up stays off and `/auth/health` says `Degraded`, so add the line to your manifest only when you have the address.
The block `private` (`signup: self-service` and `permissions`) lets a private person sign up alone, with an email address only, into a space nobody else joins; it needs the same address, and your backend tells the two kinds apart by `org_kind`.
`providers: [google, microsoft, github]` is optional too (no key, no provider): it offers "Continue with Google", "Continue with Microsoft" and "Continue with GitHub", and each listed provider needs its keys and your frontend's return address in `.env` ([`docs/deployment/provider-keys.md`](docs/deployment/provider-keys.md); a listed provider without them is not offered and `/auth/health` says `Degraded`). Your backend sees the same access token.
`org_creation: self-service` is optional as well (the default is `off`): a signed-in person may create another company (`POST /auth/orgs`, off until the key is there), and `GET /auth/options` tells your screens whether to offer it. It needs no address; the number of companies an account may belong to before it can create another (joined ones count too) is `AUTH_MAX_COMPANIES_PER_ACCOUNT` (20 by default).

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

Or let visitors start on their own: set `signup: self-service` in the manifest and give Auth-Core the address of your frontend's completion screen
(`AUTH_FRONTEND_SIGNUP_URL`). A visitor gives an email address and a company name, opens the link in the mail, chooses a password and is the first admin of a
new company. It is off by default ([`docs/deployment/vps.md`](docs/deployment/vps.md), "Self-service sign-up").

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

The guide is [`docs/integration/python-fastapi.md`](docs/integration/python-fastapi.md), with the sample product [`samples/notes-api`](samples/notes-api/README.md) (its section "Multi-tenancy, step by step" shows how it serves several companies per account). A backend
in another language checks the JWT with any library that reads a JWKS.

### 5. Connect your frontend

An Angular app copies three files (a service, an interceptor and a guard) and adds the screens the mail links open. The guide is
[`docs/integration/angular.md`](docs/integration/angular.md), with the working app [`samples/notes-web`](samples/notes-web/README.md) (its section "Several companies" shows the company picker).

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
scripts/e2e-signup.sh           # sign-up → link → company → first admin; an account that exists; an invitation first; a removed member's address signs up again as a new address (~6 min)
scripts/e2e-private.sh          # private sign-up, /auth/me, the company API refused, an invitation that keeps the space and the opt-in move, self-deletion, removal (~3 min)
scripts/e2e-orgs.sh             # several companies: list, switch, refresh, sign-in lands in the company used last, an invitation of another company joins, removal keeps the account
scripts/e2e-orgs-create.sh      # creating another company: POST /auth/orgs, switch into it, delete it; a private person cannot delete the account while in one
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
auth remove-member --org <id> --email worker@acme.test [--force]  # --force overrides last_manager; the account is deleted only when that was its last space, else kept
auth delete-org --org <id> --confirm "Acme"                       # deletes the company; members with no other space lose their accounts too
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
site/                         the project site (Astro Starlight), published to GitHub Pages
docs/                         design brief, ADRs, integration guides, deployment, operations, security, specs and plans
```

## Documentation

- [`docs/design.md`](docs/design.md): the MVP brief: goals, decisions, scope, architecture, contracts, timeline, risks.
- [`docs/adr/`](docs/adr/): architecture decision records.
- [`docs/workflow.md`](docs/workflow.md): how the work is done: the spec-driven flow, verification, branches.
- [`docs/superpowers/specs/`](docs/superpowers/specs/) and [`plans/`](docs/superpowers/plans/): the specs and the plans and acceptance maps built from them.

## License

The service is source-available under the [Business Source License 1.1](LICENSE). It is not open source.

- **Free:** development, testing and evaluation; and production use by an individual for personal, non-commercial projects, or by a non-profit organization or an educational institution for non-commercial purposes.
- **Commercial licence:** any other production use, including a company's internal use, needs one. Write to [kontakt@toviq.pl](mailto:kontakt@toviq.pl).
- **Price:** EUR 490 a year per product, net of VAT, flat, for any number of users. It covers production use, security fixes and best-effort email support, with a reply usually within two working days.
- **Change to Apache 2.0:** each version becomes available under the Apache License 2.0 three years after it is first published.
- **MIT:** `clients/`, `samples/`, `docs/` and `site/` are under the MIT License (see the `LICENSE` file in each).
- **Earlier releases:** `v0.1.0` and everything published before this change stay under the MIT License.

© 2026 Aleksander Torka
