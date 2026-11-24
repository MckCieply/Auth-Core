# Changelog

All notable changes to Auth-Core. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are those of the tags
(`v0.3.0` for the service and its image; `python-v0.1.1` for the Python package in `clients/python/`).

## Unreleased

GitHub, and managing one's own logins ([spec 0012](docs/superpowers/specs/0012-github-and-managing-logins.md)): "Continue with GitHub", and a
settings screen to connect and disconnect providers and change the password. An instance that does not list GitHub sees no change in sign-in;
`GET /auth/me` gains `confirmed_until`, the link refuses a second login of one provider, and the link queues a mail.

### Added

- **GitHub sign-in** (spec 0012): the provider name `github`, `AUTH_GITHUB_CLIENT_ID`/`AUTH_GITHUB_CLIENT_SECRET`, the round trip through
  OpenIddict's GitHub provider, `GET /user` and `/user/emails` read and the token revoked at once.
- **The confirmation window** (spec 0012): five minutes after a sign-in or `POST /auth/account/confirm`, bound to the session of the device;
  `GET /auth/me` `confirmed_until`.
- **`POST /auth/account/confirm`, `POST /auth/providers/connect`, `POST /auth/account/logins/unlink`, `POST /auth/account/password`**, and the
  `connect` purpose of `GET /auth/providers/{name}/start`.
- **Three notice mails** (provider connected, provider disconnected, password changed), Polish and English, no link, 10 an hour per address.
- **Four audit kinds** (33 in all): `account.confirmed`, `account.confirm_refused`, `provider.unlinked`, `password.changed`; `provider.linked`
  and `login.succeeded` gain `details.via`.
- **`DELETE /auth/org` with a provider ticket** for an admin without a password.
- **The sample**: GitHub buttons, `/settings`, "Delete company" on `/team`, notes-api `DELETE /api/org`; `scripts/e2e-settings.sh`. "Delete
  account" moves to `/settings`; the notes screen has no account section.
- A Development-only endpoint, `POST /auth/testing/end-confirmation` (`Auth:Testing:WindowEndpoint`), for browser tests.

### Changed

- **The link** (`POST /auth/providers/link`) answers `409 provider_in_use` for an account that already has a login of the ticket's provider,
  and queues the mail "provider connected" (spec 0012).
- **A provider sign-in holds its login** against an unlink in the same moment (spec 0012).
- **`DELETE /auth/org` with a provider ticket** deletes only the accounts that have no other space (spec 0015 rule), as the password path does.

### Upgrading

- Spec 0012: one migration adds the table `SessionStates` and the column `MailRequests.Provider`. Sessions from before the upgrade keep working;
  their confirmation window is closed until the person confirms. An account that the link of spec 0011 gave two logins of one provider keeps
  both; `GET /auth/me` lists the provider once, and a disconnect removes both.

## 0.3.0 - 2026-11-23

Several companies per account ([spec 0015](docs/superpowers/specs/0015-multiple-companies.md), the first of three slices with 0016 and 0017): one
person belongs to several companies (and at most one personal space) with one account and moves between them without signing in again. A product that
never switches companies sees no change: every person has one company, the token carries the same claims and every endpoint answers as before. No
endpoint made a second membership in spec 0015 itself: spec 0016 adds `POST /auth/orgs` (below) and spec 0017 lets an invitation keep the other memberships (below).

### Added

- **`POST /auth/orgs`** (spec 0016): a signed-in account creates another company with `{"name"}` and becomes its first admin (the manifest's first admin role); it
  answers `201` with the entry of `GET /auth/orgs` (`current` `false`) and does not move the session, so the product calls `POST /auth/switch`. It is off until the
  manifest says `org_creation: self-service` (`403 org_creation_disabled`), any account may create (a private one keeps its personal space), and it counts against the
  per-IP `invite` limit. `org.created` is written with `details.via` `api`; there is no new audit kind.
- **The manifest key `org_creation`** (`self-service` or `off`, the default; any other value makes the manifest invalid) and **the option `org_creation`** in
  `GET /auth/options`, `self-service` or `off`.
- **`Auth:Tenancy:MaxCompaniesPerAccount`** (env `AUTH_MAX_COMPANIES_PER_ACCOUNT`): how many companies one account may be a member of before `POST /auth/orgs`
  answers `409 company_limit_reached`; a whole number from 1 to 1000, 20 when absent or empty, anything else stops the start. The personal space is not counted
  and an invitation is not refused by it.
- **`DELETE /auth/account` answers `403 company_member`** to an account that holds its personal space and a company, with a token of either; nothing is deleted
  and no attempt is counted. An account with company memberships only still gets `403 forbidden`.
- **The sample**: "New company..." in the picker of `samples/notes-web`, and the `company_member` text before the account's notes are deleted.
- **`GET /auth/orgs`**: every space of the token's user (companies and the personal space, listed as `private` with `kind` `personal`), ordered by name, with the
  role and `current` for the token's `org_id`; it counts against the `general` limit.
- **`POST /auth/switch`** with `{"org_id"}` and the refresh cookie: a new access token and cookie for another space of the person, answered as `/auth/refresh`;
  `404 not_found` for a space that is not the person's (the cookie is not used up), the uniform `401 invalid_grant` of a refresh that would fail. It counts against
  the `refresh` limit with `/auth/refresh`.
- **The session's company.** Every session works in one space: a sign-in sets it, a switch changes it, a refresh keeps it. The refresh token carries it as two
  internal claims, `session_org_id` and `session_org_joined`; the access token gains no claim.
- **The company used last.** A membership gains `LastUsedAt`, written by a sign-in and a switch (never by a refresh); a sign-in lands in the company used last,
  so a person who never switched lands where they always did.
- **One audit kind**, `org.switched` (29 in all), with `details.from_org_id`; none for a switch to the session's own company.
- **`Auth:DevSeed:SecondOrgName`**: the development seed also creates that company and makes its first user a plain member of it.
- **The sample**: a company picker in the account bar of `samples/notes-web`, and a reload of the notes when another tab switched.
- **`scripts/e2e-orgs.sh`**: the live check of the sequence above against the development stack (the run sequences of `e2e-tenancy.sh` and `e2e-hardening.sh` name it).
- **An invitation keeps the other memberships** (spec 0017). An address that has an account of any kind (a company member's, a private account, or both) accepts an
  invitation and joins the company with that account: the password, the linked providers, the other memberships, the personal space and the sessions stay, and the new
  company is in `GET /auth/orgs` at once (`current` `false`). The acceptance needs no password for such an address (one sent is not used), confirms an unconfirmed
  email and writes `LastUsedAt` with `JoinedAt`.
- **`has_account` and `org_id` in `POST /auth/invites/preview`**: `has_account` is `true` when the address has an account of any kind, `org_id` is the invited
  company's id. A client uses them to show one button instead of a password form and to call `POST /auth/switch` after the `204`.
- **The acceptance through Google or Microsoft joins an existing account** when the identity is one of its logins: the person is signed in to the invited company
  with the answer of a login. An identity that is not one of the account's logins is `409 already_registered`; one that belongs to another account is
  `409 provider_already_linked`; nothing is used up in those cases.
- **The sample**: `/invite` shows one line and the button "Accept the invitation" for an existing account, with the provider buttons below, and moves the session of
  the invited person into the company after the `204`; it never sends `leave_private_account`.

### Changed

- **An invitation to an existing account no longer ends sessions, mail links or the login streak** (spec 0017). Until now an acceptance bumped the security stamp and
  ended every session of the account; it ends none, on either path, and a lockout, a reset link or a verification link sent before it stays as it was.
- **`409 already_member` of an invitation means a member of the invited company only** (preview, acceptance and provider exchange); a member of another company
  joins. **`409 private_account_exists` is no longer answered by any path** (the OpenAPI description drops it; the exchange describes `already_registered` for the
  purpose `invite`). A client built for spec 0010 that sends `leave_private_account: true` still gets the move: the personal space is deleted, and sessions of that space
  get `401 invalid_grant` at their next refresh or switch. A screen built for an older server that sends a `password` for an existing account sees it ignored.
- **The invitation mail to an address that has an account** names the application and the company and says that the account joins and keeps everything it has (English
  and Polish); no mail holds the warning of spec 0010 any more. As since `0.1.2`, no invitation mail names the role.
- **An account is deleted only when it loses its last space.** Removing a member and deleting a company delete the membership; an account left with no space is
  deleted as before (`account.deleted`, now naming in `org_id` the space it lost), and one that has another space is kept (`member.removed` is written either way).
  The sessions whose company was the removed one get `401 invalid_grant` at their next refresh or switch; the others go on. `remove-member` prints
  "Member removed; the account is deleted." or "Member removed; the account is kept."; `delete-org` prints "Company deleted; members with no other space lose their accounts too."
- `GET /auth/me`, every endpoint under `/auth/org` and `DELETE /auth/account` act on the token's company, checked against the database. `DELETE /auth/account`
  with a personal-space token of an account that is also a company member answers `403 company_member` (spec 0016; it was `403 permissions_changed`).
- The per-IP limits count `POST /auth/switch` against the `refresh` policy and `GET /auth/orgs` against `general`. The OpenAPI description has 38 operations (39 with `POST /auth/orgs`).
- The threat model (residual risks 62 to 65, the session's company), the guides for Angular and Python, the design document and the audit runbook describe the feature (residual risks 66 and 67 are spec 0016's, 68 to 70 spec 0017's).

### Upgrading

One migration adds `Memberships.LastUsedAt` (nullable; every existing row reads `null`); the service applies it at start. A refresh token issued before this
version carries no company and refreshes into the space a sign-in would take.

## 0.2.0 - 2026-11-21

Self-service sign-up ([spec 0009](docs/superpowers/specs/0009-self-service-signup.md)): a product can let a visitor start a company of their own. Off by default; an
instance that does not turn it on behaves as `0.1.0`, apart from what spec 0010 below changes (accounts are deleted instead of kept).

Private accounts ([spec 0010](docs/superpowers/specs/0010-private-accounts.md)): a product can let a private person sign up alone, and a
private person can delete their account. Off by default; an instance that does not turn it on sees `org_kind: "organization"` in its tokens and
`GET /auth/me`, `"private_signup":"off"` in `GET /auth/options`, and accounts deleted instead of kept when they lose their company.

Sign-in with Google and Microsoft ([spec 0011](docs/superpowers/specs/0011-google-and-microsoft-sign-in.md)): a product can offer "Continue with
Google" and "Continue with Microsoft". Off by default; an instance that does not list providers sees `"providers":[]` in `GET /auth/options`
and two new members in `GET /auth/me`, and nothing else changes.

### Added

- **A health check for the `auth` container** in `deploy/docker-compose.prod.yml`: the image has no shell and no curl, so `Auth.Server.dll --health-probe` makes one request to `/auth/health` on the container's own loopback address (the port of `ASPNETCORE_HTTP_PORTS`) and exits 0 for a 2xx, 1 otherwise. Liveness only: `Degraded` passes, and the database is not asked. It runs before the host is built, so it needs no key and no setting.
- **A memory ceiling for the `auth` container**: `mem_limit: ${AUTH_MEM_LIMIT:-512m}` (and the same `memswap_limit`, so there is no swap). The .NET garbage collector keeps its heap under 75% of it by itself.
- **`scripts/test-linux.sh`**: the whole test suite in the official .NET 10 SDK container, with the Docker socket mounted for Testcontainers; part of the checklist before a merge to `main` (`docs/workflow.md`).
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
- **Google and Microsoft sign-in.** The manifest key `providers`; `GET /auth/providers/{name}/start` and `GET /auth/providers/{name}/callback`
  (the round trip, with PKCE, a state bound to the browser and a nonce), `POST /auth/providers/exchange`, `POST /auth/providers/link` and
  `POST /auth/providers/finish`; single-use tickets stored as hashes (the table `ProviderTickets`) and the browser-binding cookie `auth_pt`;
  `DELETE /auth/account` with `provider_ticket`; `GET /auth/options` `providers`; `GET /auth/me` `has_password` and `linked_providers`; the
  audit kinds `provider.linked` and `provider.reauthenticated` (28 in all) and `details.provider` on the sign-in, sign-up and invitation rows;
  the optional variables `AUTH_FRONTEND_PROVIDER_URL`, `AUTH_GOOGLE_CLIENT_ID`, `AUTH_GOOGLE_CLIENT_SECRET`, `AUTH_MICROSOFT_CLIENT_ID`,
  `AUTH_MICROSOFT_CLIENT_SECRET` and `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES`; the guide `docs/deployment/provider-keys.md` and the runbook
  `docs/operations/microsoft-secret.md`; the sample's buttons and its `/provider-return`, `/provider-link` and `/provider-finish` screens;
  the fake provider `tests/Auth.FakeOidc` and `scripts/e2e-providers.sh`.

### Changed

- The licence of the service changes from the MIT License to the Business Source License 1.1 (see `LICENSE`). Development, testing and evaluation stay free; production use is free for an individual's personal, non-commercial projects and for non-profit organizations and educational institutions (non-commercial purposes); any other production use, including a company's internal use, needs a commercial licence (kontakt@toviq.pl). Each version changes to the Apache License 2.0 three years after it is first published. `clients/`, `samples/` and `docs/` stay under the MIT License, each with its own `LICENSE` file, and `v0.1.0` stays under the MIT License. The image label `org.opencontainers.image.licenses` is now `BUSL-1.1`.
- `Directory.Packages.props` pins `System.Security.Cryptography.Pkcs` and `System.Security.Cryptography.Xml` (transitive packages, brought in by MailKit and the test host) to 10.0.13: NuGet audit reports advisories for the versions they resolved to, and the build treats warnings as errors.
- `deploy/auth.yaml` and `samples/notes-api/auth.yaml` say `signup: self-service` and, since spec 0010, `private.signup: self-service`, so the shipped manifests turn both kinds of sign-up on; a deployment that wants company sign-up off writes `signup: invite-only` or drops the line, one that wants private sign-up off writes `private.signup: off` or drops the `private` block, and either one left on needs `AUTH_FRONTEND_SIGNUP_URL`; the development settings, the sample overlay and `scripts/e2e-prod.sh` name a completion address.
- The per-IP limits count `POST /auth/signup` against the mail policy, `/auth/signup/preview` and `/auth/signup/complete` against the invitation policy, and `DELETE /auth/account` against the general policy. The OpenAPI description has 31 operations.
- The guides, the threat model (residual risk 46: sign-up mails any well-formed address) and the audit runbook describe the feature.
- The per-IP limits count the start and the callback of a provider round trip (the two `GET`s) and `POST /auth/providers/exchange`, `link` and
  `finish` against the login policy. The OpenAPI description has 36 operations.
- **An account without a password** (made through a provider) can set one with "forgot password" and then signs in with either; a password
  login for an account without one is the `401` of an unknown address. `DELETE /auth/org` keeps its password confirmation: an admin without a
  password gets `403 reauth_required` for any password (nothing is counted, no row is written, the company stays) and sets one with "forgot
  password" first; a provider confirmation for deleting a company is left to a later spec.
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
- The invitation to an address that has a private account no longer names the role either (as in `0.1.2`, for every invitation mail).

### Fixed

- **The sample no longer says "Something went wrong" on a per-IP `429`.** On the forgot-password, reset-password, verify and invitation screens of `samples/notes-web`, a `429 too_many_requests` (the per-IP limit) showed "Something went wrong" under the wait bar; now only the wait bar is shown (the invitation screen keeps its "Try again" button). The screens that already handled the per-address `429` are unchanged.

### Upgrading

Three migrations: the first adds the table `PendingSignups`; the second adds `Kind` to companies and pending sign-ups (every existing row is a
company, or an organization sign-up); the third adds the table `ProviderTickets`. The service applies them at start. At the first start of this version every account without a
membership (one an earlier version kept after a removal or a company deletion) is deleted, each with an `account.deleted` row whose
`details.reason` is `upgrade`. This cannot be undone but by restoring a backup, so back up first; a start that fails on an account stops and logs its id (level Critical). Nothing else is required: without the manifest keys and the address, neither kind of sign-up is on and no
setting is new and required.

## 0.1.2 - 2026-10-28

A patch release of `0.1.1`.

### Changed

- The invitation mail no longer names the role. It printed the role's key from the consumer's manifest (for example `worker`), which is a code, not a word the invitee should read. The rest of the mail is unchanged.

## 0.1.1 - 2026-10-20

A patch release of `0.1.0`: the same code, with the patched transitive packages.

### Changed

- `Directory.Packages.props` pins `System.Security.Cryptography.Pkcs` and `System.Security.Cryptography.Xml` (transitive packages, brought in by MailKit and the test host) to 10.0.13: NuGet audit reports advisories (GHSA-2fmv-rv2c-59x7, GHSA-846f-f764-76mx; both denial of service) for the versions they resolved to, and the build treats warnings as errors, so `0.1.0` no longer restored from source. The image carries the patched packages.

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
