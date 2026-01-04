# Auth-Core

One reusable, self-hosted authentication service in .NET that every future
project drops in instead of rebuilding login, tenancy, RBAC and token refresh.

`speech-to-mail` is the first consumer; the MVP milestone is a working end-to-end
login on a real phone, delivered over a six-week build.

> **Status:** design phase. This repository currently holds the design brief and
> architecture decision records. Implementation follows the milestones in
> [`docs/design.md`](docs/design.md).

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
System.CommandLine · xUnit v3 + Testcontainers.

## Repository layout (planned)

```
auth-service/
  src/Auth.Server/              ASP.NET Core host, endpoints, OpenIddict
  src/Auth.Core/                users, orgs, memberships, roles, permissions
  src/Auth.Infrastructure/      EF Core, migrations, SMTP, manifest loader
  src/Auth.Cli/                 admin CLI
  tests/Auth.IntegrationTests/  WebApplicationFactory + Testcontainers
  clients/python/               FastAPI package
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

## License

[MIT](LICENSE) © 2026 Aleksander Torka
