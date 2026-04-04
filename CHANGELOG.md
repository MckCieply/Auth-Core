# Changelog

All notable changes to Auth-Core. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are those of the tags
(`v0.1.0` for the service and its image; `python-v0.1.1` for the Python package in `clients/python/`).

## Unreleased

Self-service sign-up ([spec 0009](docs/superpowers/specs/0009-self-service-signup.md)): a product can let a visitor start a company of their own. Off by default; an
instance that does not turn it on behaves as `0.1.0`.

### Added

- **Sign-up.** `POST /auth/signup` (an email address and a company name; the same `202` for every address), `POST /auth/signup/preview` and
  `POST /auth/signup/complete` (the person chooses a password at the link of the mail, becomes the first admin of a new company, in one transaction, and is signed in:
  the answer is a login's, the access token and the refresh cookie, and the `login.succeeded` audit row is written in that transaction),
  and `GET /auth/options` (what a product's screens may offer). The first admin gets the first default role of the manifest that holds `*`, else the first that
  holds `members:manage`. An address whose account belongs to a company gets a mail that says so, with no link; at the completion such an account answers `409 already_registered`, whatever the password (`weak_password` is for an address without an account). With sign-up off the three endpoints answer `403 signup_disabled`, and the sign-up mails still queued are dropped.
- **A person without a company comes back by sign-up** (Amendment 3). Someone whose company was deleted, or who was removed from it, has an account and no access; if they sign up with their address they get the usual link, `POST /auth/signup/preview` says `has_account` `true`, and `POST /auth/signup/complete` needs only the token and a company name (the `password` member may be left out; if it is sent it must be a non-empty string, and it is not used): the account keeps its password, its email is confirmed, its earlier sessions and mail links end (defence in depth), the company is made with the account as its first admin, the person is signed in, and the audit row `signup.completed` carries `details.existing_account` `true`. An account that belongs to a company still gets the "you already have an account" mail.
- **The manifest key `signup`** (`invite-only`, the default, or `self-service`) and the optional setting `Auth:App:FrontendUrls:CompleteSignup`
  (`AUTH_FRONTEND_SIGNUP_URL` in `deploy/docker-compose.prod.yml`). Sign-up is on only with both; the key without the address leaves it off, logs a warning at start and makes
  `/auth/health` say `Degraded`.
- **Pending sign-ups**, a table of their own (one per address, expiring after 24 hours, removed by the hourly clean-up), and the mail kind `signup`
  with its own mail limit (one request a minute and five an hour per address).
- **Two audit kinds**, `signup.requested` and `signup.completed` (24 in all); `org.created` gains the value `signup` for `details.via`.
- **The sample** `samples/notes-web`: `/signup`, `/complete-signup` and a "Create an account" link on the sign-in screen when `GET /auth/options` says
  `self-service`, with unit tests and Playwright tests.
- **`scripts/e2e-signup.sh`**: the live check of the sign-up flow against the development stack (the run sequences of `e2e-tenancy.sh` and `e2e-hardening.sh` name it).

### Changed

- `deploy/auth.yaml` and `samples/notes-api/auth.yaml` say `signup: self-service`; the development settings, the sample overlay and `scripts/e2e-prod.sh` name a completion address.
- The per-IP limits count `POST /auth/signup` against the mail policy and `/auth/signup/preview` and `/auth/signup/complete` against the invitation policy.
- The guides, the threat model (residual risk 46: sign-up mails any well-formed address) and the audit runbook describe the feature.

### Upgrading

A new migration adds the table `PendingSignups`; the service applies it at start. Nothing else is required: without the manifest key and the address, sign-up is off and no
setting is new and required.

## 0.1.0 - 2026-02-24

The first release: the service, its image, a Python package for backends, an Angular sample for frontends, and the documents to run it.

### Added

- **Sign-in.** `POST /auth/login` issues RS256 JWT access tokens (10 minutes; `sub`, `org_id`, `roles`, `permissions`) with the public keys at
  `/auth/.well-known/jwks.json`; the refresh token is an `HttpOnly; Secure; SameSite=Strict; Path=/auth` cookie that rotates on every use, with
  reuse detection (`POST /auth/refresh`, `POST /auth/logout`).
- **Abuse resistance.** A lockout per identifier with growing cooldowns; **per-IP rate limits** (30 logins, 60 refreshes, 10 mail requests, 20
  invitation requests and 300 other requests a minute) answered `429 too_many_requests` with `Retry-After`; the client address is read from
  `X-Forwarded-For` only from configured trusted proxies (`Auth:Proxy:KnownProxies`, `Auth:Proxy:KnownNetworks`; nothing is trusted by default).
- **Mail flows.** Password reset and email verification, and invitations, by mail with STARTTLS or TLS.
- **Companies.** Companies, members, roles and invitations, with the company API (`/auth/org`, `/auth/me`), safety rules (nobody grants more than they
  hold; a company keeps a manager), the manifest of a product's permissions and default roles, and the operator CLI
  (`create-org`, `invite`, `list-orgs`, `remove-member`, `delete-org`).
- **Company deletion** by `DELETE /auth/org` (the permission `org:delete`, the company's name and the caller's password) and by `delete-org`.
- **Audit log.** The table `audit_events`: 22 kinds, each written in the transaction of the change it records (events that change nothing are written on their own and never fail the request), kept 90 days, read with SQL.
- **Security headers** on every response, and no `Server` header; a `Content-Security-Policy` of its own for the interactive reference.
- **OpenAPI** description at `/auth/openapi/v1.json`, with the interactive reference in Development.
- **Production.** The image `ghcr.io/mckcieply/auth-core:0.1.0` (public, with OCI labels, base images pinned by digest), `deploy/docker-compose.prod.yml`
  (read-only, no capabilities, two fixed subnets outside Docker's default pools, a named network for a proxy in a container; Auth-Core connects to PostgreSQL as a role of its own that is not a superuser), `deploy/.env.prod.example`,
  and `docs/deployment/vps.md`.
- **Documents.** The threat model (STRIDE), the backup and the key rotation runbooks, the integration guides for Python and Angular.
- **Samples.** `samples/notes-api` (FastAPI and PostgreSQL behind Caddy) and `samples/notes-web` (Angular 21): the first backend and frontend of the service.

### Changed

- A refresh while the database is down answers `503 temporarily_unavailable` (it was `500`), and the cookie is kept.
- An unhandled error answers `500 {"error":"internal_error"}` with the headers (Kestrel's own `500` dropped them).
- `/auth/health` answers `GET` and `HEAD` only (any other method is `405` with `Allow: GET, HEAD`).
- A JSON body declared with a charset other than UTF-8 is `415 unsupported_media_type`.
- An invitation to an address whose domain ends in digits or in a hexadecimal form (`127.0x1`, `host.123`) is refused.
- The second development seed user takes the first role of the company, by name, that holds neither `members:manage` nor `*`.
- The built-in permission catalog gains `org:delete`: the `permissions` claim of a token whose role holds `*` has it.
- The development compose file pins `postgres` and `mailpit` by digest and passes the rate limits and the audit retention from `.env`.

### Fixed

- Data Protection keys are kept in memory (nothing uses them, and the production container's file system is read-only).

### Known limits

See [`docs/security/threat-model.md`](docs/security/threat-model.md), "Residual risks". The main ones: a key change signs everyone out; many people behind one address share its
rate limits; a company admin can delete the company; the audit log is not tamper-evident.

## python-v0.1.1 - 2026-02-24

`auth-core-fastapi` 0.1.1: the MIT licence, its file and a readme in the package metadata. No change of behaviour. `python-v0.1.0` stays as it was published.
