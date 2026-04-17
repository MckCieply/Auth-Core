# Changelog

All notable changes to Auth-Core. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are those of the tags
(`v0.1.0` for the service and its image; `python-v0.1.1` for the Python package in `clients/python/`).

## Unreleased

Self-service sign-up ([spec 0009](docs/superpowers/specs/0009-self-service-signup.md)): a product can let a visitor start a company of their own. Off by default; an
instance that does not turn it on behaves as `0.1.0`, apart from what spec 0010 below changes (accounts are deleted instead of kept).

Private accounts ([spec 0010](docs/superpowers/specs/0010-private-accounts.md)): a product can let a private person sign up alone, and a
private person can delete their account. Off by default; an instance that does not turn it on sees `org_kind: "organization"` in its tokens and
`GET /auth/me`, `"private_signup":"off"` in `GET /auth/options`, and accounts deleted instead of kept when they lose their company.

### Added

- **Sign-up.** `POST /auth/signup` (an email address and a company name; the same `202` for every address), `POST /auth/signup/preview` and
  `POST /auth/signup/complete` (the person chooses a password at the link of the mail, becomes the first admin of a new company, in one transaction, and is signed in:
  the answer is a login's, the access token and the refresh cookie, and the `login.succeeded` audit row is written in that transaction),
  and `GET /auth/options` (what a product's screens may offer). The first admin gets the first default role of the manifest that holds `*`, else the first that
  holds `members:manage`. An address with an account (of either kind, spec 0010) gets a mail that says so, with no link; at the completion such an account answers `409 already_registered`, whatever the password (`weak_password` is for an address without an account). With sign-up off the three endpoints answer `403 signup_disabled`, and the sign-up mails still queued are dropped.
- **The manifest key `signup`** (`invite-only`, the default, or `self-service`) and the optional setting `Auth:App:FrontendUrls:CompleteSignup`
  (`AUTH_FRONTEND_SIGNUP_URL` in `deploy/docker-compose.prod.yml`). Sign-up is on only with both; the key without the address leaves it off, logs a warning at start and makes
  `/auth/health` say `Degraded`.
- **Pending sign-ups**, a table of their own (one per address, expiring after 24 hours, removed by the hourly clean-up), and the mail kind `signup`
  with its own mail limit (one request a minute and five an hour per address).
- **Two audit kinds**, `signup.requested` and `signup.completed` (24 in all; 26 with the two of spec 0010 below); `org.created` gains the value `signup` for `details.via`.
- **The sample** `samples/notes-web`: `/signup`, `/complete-signup` and a "Create an account" link on the sign-in screen when `GET /auth/options` says
  `self-service`, with unit tests and Playwright tests.
- **`scripts/e2e-signup.sh`**: the live check of the sign-up flow against the development stack (the run sequences of `e2e-tenancy.sh` and `e2e-hardening.sh` name it).
- **Private sign-up** (spec 0010). `POST /auth/signup` takes `kind` (`organization`, the default, or `private`, with no company name); the
  preview says `kind`; a private completion makes the account and a hidden one-person space and signs the person in. The manifest block
  `private` (`signup: self-service` and `permissions`) turns it on, with the same completion address; `GET /auth/options` says
  `private_signup`.
- **`org_kind`** in the access token and in `GET /auth/me`: `organization` or `personal`. The private role's permissions come from the
  active manifest at every login and refresh. The company API answers a personal space `403 forbidden`, and the operator CLI hides personal
  spaces.
- **`DELETE /auth/account`**: a private person deletes their account with their password (counted in the login streak); the answer clears
  the refresh cookie. The product deletes its own data first.
- **An invitation to a private person** is a warning: the mail has a paragraph, the preview says `has_private_account`, and accepting needs
  `leave_private_account: true` (else `409 private_account_exists`); the account keeps its password and its space is deleted.
- **Two audit kinds**, `account.deleted` and `account.delete_refused` (26 in all); `signup.requested` and `signup.completed` gain
  `details.kind`, `invite.accepted` gains `left_private_account` and `personal_org_id`.
- **The Python package**: `Principal.org_kind` (`"organization"` when the claim is absent); it is released with `v0.2.0`.
- **The samples**: the sign-up choice, the private completion, the account bar, "Delete account" (`DELETE /api/me` first), the invitation
  warning and a small `/team` screen in `samples/notes-web`; `DELETE /api/me` in `samples/notes-api`; `scripts/e2e-private.sh` and
  `e2e/10-private.spec.ts`.

### Changed

- `deploy/auth.yaml` and `samples/notes-api/auth.yaml` say `signup: self-service`; the development settings, the sample overlay and `scripts/e2e-prod.sh` name a completion address.
- The per-IP limits count `POST /auth/signup` against the mail policy, `/auth/signup/preview` and `/auth/signup/complete` against the invitation policy, and `DELETE /auth/account` against the general policy. The OpenAPI description has 31 operations.
- The guides, the threat model (residual risk 46: sign-up mails any well-formed address) and the audit runbook describe the feature.
- **An account that loses its only space is deleted** (spec 0010): removing a member (`204` as before) and deleting a company (every member,
  the admin included) delete the accounts, with `account.deleted` rows; a login is then the `401` of an unknown address, and the address can
  be invited again or sign up. The CLI says so ("Member removed; the account is deleted.", "Company deleted; the accounts of its members are
  deleted.").
- **"You already have an account"** goes to any account under the address, of either kind, and gains a sentence: how to get a new account on
  purpose. The comeback of an account without a company by sign-up (spec 0009, Amendment 3: `has_account`, the completion without a password,
  `details.existing_account`) is removed before any release.
- **Sign-in and refresh read the account again under a share lock,** in the transaction that writes the session, so an account that is being deleted never gets one: a login then answers the `401` of an unknown address and a refresh `401 invalid_grant`, and writes no token. A refresh now runs in one transaction with its token writes.
- `POST /auth/invites/accept`: an account that exists keeps its password (a `password` sent is not used); a missing password with an unknown
  token is now `400 invalid_token`. `POST /auth/signup/complete`: the company name is required for an organization and refused for a private
  sign-up, judged after the token.

### Fixed

- **The sample no longer says "Something went wrong" on a per-IP `429`.** On the forgot-password, reset-password, verify and invitation screens of `samples/notes-web`, a `429 too_many_requests` (the per-IP limit) showed "Something went wrong" under the wait bar; now only the wait bar is shown (the invitation screen keeps its "Try again" button). The screens that already handled the per-address `429` are unchanged.

### Upgrading

Two migrations: the first adds the table `PendingSignups`; the second adds `Kind` to companies and pending sign-ups (every existing row is a
company, or an organization sign-up). The service applies them at start. At the first start of this version every account without a
membership (one an earlier version kept after a removal or a company deletion) is deleted, each with an `account.deleted` row whose
`details.reason` is `upgrade`. This cannot be undone but by restoring a backup, so back up first; a start that fails on an account stops and logs its id (level Critical). Nothing else is required: without the manifest keys and the address, neither kind of sign-up is on and no
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
