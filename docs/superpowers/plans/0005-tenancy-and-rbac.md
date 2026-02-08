# Tenancy and RBAC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0005
- **Date:** 2026-02-02
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0005-tenancy-and-rbac.md`](../specs/0005-tenancy-and-rbac.md)
  — the plan argues from the spec; where they disagree, **the spec wins** and the
  disagreement is a finding (see [`docs/workflow.md`](../../workflow.md)).

**Goal:** A second company can use a product with its own users and roles, and run
them itself: the operator creates a company and invites its first admin; from then on
the company's admins invite, re-role and remove members and define the company's roles
through an API; every access token says which company its user belongs to and what they
may do there.

**Architecture:** Companies, memberships, per-company roles and invitations are tables.
The product's `auth.yaml` declares its permissions and default roles; the file is
validated as a whole at startup, stored when valid, and replaced by the last stored one
when broken (health says `Degraded`). Login and refresh read the membership, the role
and the catalog afresh and put `org_id`, `roles` and `permissions` into the access token
as JSON arrays. The company API takes the service's own bearer tokens the way a consumer
does (OpenIddict validation in the same host, audience required) but answers every
permission question from the database, and says `permissions_changed` when only the
token still claims a permission. Everything that changes a company's members, roles or
invitations is one transaction at the default isolation level that begins by locking the
company row and reading the caller again, which is what makes the last-manager rule hold
under concurrency. An invitation lives in a table of its own (its address may have no
account), carries a single-use token stored as a SHA-256, and is mailed through the queue
of spec 0004 as a third kind of mail; accepting it is one transaction that also creates
or resets the account. The operator CLI is four hand-parsed subcommands of the same
binary that build the services and never run the host.

**Tech Stack:** as plans 0001–0004 (.NET 10, ASP.NET Core minimal APIs, ASP.NET Core
Identity, OpenIddict 7.7.1, EF Core 10.0.12 + Npgsql 10.0.3, PostgreSQL 16, MailKit
4.18.1, xUnit v3, Testcontainers 4.15, Mailpit v1.31.3). **Three new packages**, each
approved for download by the owner: `YamlDotNet` 18.1.0 (MIT; no
dependencies on net10.0), `Microsoft.AspNetCore.OpenApi` 10.0.12 (MIT; the version of
the repository's other ASP.NET Core 10.0.12 packages; brings `Microsoft.OpenApi` 2.12.0,
MIT) and `Scalar.AspNetCore` 2.17.13 (MIT; no dependencies on net10.0; mapped in
`Development` only). Nothing else is new: bearer validation is OpenIddict's own
validation package, which `OpenIddict.AspNetCore` 7.7.1 already brings, and the CLI has
no command-line library. `dotnet list package --vulnerable --include-transitive`: none.

## Global Constraints

- Everything in plans 0001–0004 → Global Constraints still holds (central package
  versions, `TreatWarningsAsErrors`, no secrets in repo or logs, Conventional Commits,
  local-only workflow, mail host rules).
- Numbers are **constants in code**, not configuration: an invitation is valid **7 days**
  from the moment its mail is composed; the mail limit of an invitation is the limit of
  spec 0004 — **one accepted request per 60 seconds, five per fixed 60-minute window** —
  counted per company and address; a company or role name is **at most 100 characters**;
  a request body is **at most 8 KiB**; a manifest file is at most **64 KiB**, at most
  500 permissions and 100 default roles.
- Contract (spec → Contract). Every response under `/auth/me`, `/auth/org` and
  `/auth/invites` — the `401` of a missing token and a `404` or `405` included — carries
  `Cache-Control: no-store` and `Pragma: no-cache` and never a `Set-Cookie`. An error
  body is exactly `{"error":"<code>"}` (plus `rules` for `weak_password`, as in spec
  0004, and `retry_after_seconds` for `too_many_attempts`). JSON property names are
  `snake_case`; times are ISO 8601 in UTC. A missing or invalid bearer token is `401`
  with an **empty body** and `WWW-Authenticate: Bearer` (with `error="invalid_token"`
  when the token is wrong or expired).
- **The company API reads the database on every call** for the caller's membership,
  company and role; the token only tells who the caller is. Database grants → proceeds;
  database does not but the token claims it → `403 permissions_changed`; neither →
  `403 forbidden`. No endpoint under `/auth/org` takes a company id: the caller's company
  comes from the database, and an id that is not in it is `404 not_found`.
- **The lock is a row lock on the company inside a transaction left at `READ COMMITTED`.**
  Never raise the isolation level of these transactions: under `REPEATABLE READ` the lock
  does not protect the check (probe, below). Lock order: the company row first, then
  invitation rows, then user rows.
- Safety rules (spec): rule 1, **one rule: nobody acts on anything that holds more than they
  do** — a role they create, edit or delete, a member they remove or re-role, an invitation
  they send, resend or cancel: every permission of the role it has now and of the role it
  would get must be one the caller holds, and `*` only by a caller whose role holds `*`
  (`403 permission_not_held`; the operator is exempt); rule 2 (a company that has a manager
  keeps one); rule 3 (nobody changes their own role or removes themselves).
- **The order of checks** (spec → General rules): authorisation (`401`, then `403`), the
  shape of the request (`400`), existence (`404`), rule 3, rule 1, the conflicts
  (`already_in_org`, `invite_pending`, `role_name_taken`), the mail limit (`429`), rule 2.
  One test pins each neighbouring pair that could be got wrong. A path the service does not
  have is the framework's own empty `404` (or `405`), still `no-store`; `{"error":"not_found"}`
  is the body of a `404` for an id that is not in the caller's company.
- **An invitation token in clear exists only in the mail.** The database holds its
  SHA-256. No token, link, mail body, password, address, connection string or manifest
  content is logged; log lines carry ids, kinds, counts, the reason a manifest was not
  used, and exception **type names** only. Company and role names, typed by members, are
  encoded in the HTML part of a mail and are never placed in a header.
- `Auth:Manifest:Path` and `Auth:App:FrontendUrls:AcceptInvite` are required settings:
  a host without them does not start. A missing or broken manifest **file** never stops
  the host.
- The operator CLI parses its arguments by hand (owner decision: no `System.CommandLine`),
  never migrates the database, never prints a connection string, and prints a refusal as
  `error: <code>` on the error stream with a non-zero exit code.
- **Unicode escapes stay escapes.** Where the code below writes a backslash followed by
  `u` and four hex digits (C# literals, JSON in raw strings), the file must hold those
  six characters, not the character they stand for. This plan has them in
  `NameInputTests.cs`, `ManifestParserTests.cs`, `OrgInviteTests.cs`, `InviteAcceptTests.cs`,
  `EmailInput.cs` (the noncharacters) and, in a comment, `RoleBody.cs` (an unpaired surrogate).
  Some editing tools decode such escapes on the way in: after writing those files, check with
  `grep -n 'uFFFE\|uFDD0\|u0007\|u0000\|u0085\|u00A0\|u000A\|u0009\|uD83D\|uD800' <file>` that
  each escape is there as text, and that
  `grep -nP '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]|\x{0085}|\x{FFFE}|\x{FFFF}|[\x{FDD0}-\x{FDEF}]|[\x{D800}-\x{DFFF}]'`
  (with a UTF-8 locale) finds nothing in `src`, `tests`, `scripts` and `deploy`.
- Existing test files must pass **unedited**, except the ones these tasks name:
  `Infrastructure/AuthAppFactory.cs` (Tasks 3, 7, 13: a manifest file of its own for every
  host, the invitation URL, the connection string), `Infrastructure/MailTestBase.cs`
  (Task 5: an account made by `CreateUserAsync` now joins the development company,
  because since this slice an account without a company cannot log in; `member: false`
  makes one that does not), `Infrastructure/SessionApi.cs` (Task 5: a login as any
  account), `LoginTests.cs` (Task 5: the claim set of the token now holds the three
  tenancy claims; one test is renamed and the 0001 acceptance map follows) and, for the
  new `AcceptInvite` setting, `MailSettingsTests.cs`, `MailComposerTests.cs` and
  `SmtpMailTransportTests.cs` (Task 7). Every other test of the 390 existing ones passes
  as it stands.
- Exactly one new migration (Task 2), which touches one existing table (one added column
  on `MailRequests`). Its id is given by the orchestrator and must be greater than the newest
  id in `src/Auth.Infrastructure/Persistence/Migrations/`.
- Every task states the test count its own commit has, and each of those counts was proved by
  building and testing that commit alone: a task never leans on a fix that comes later.
- Implementers leave their changes **uncommitted** in the working tree. The
  orchestrator reads the diff, runs the gate and commits with the subject named in the
  task's last step.
- Gate for every task: `dotnet build -warnaserror && dotnet test`. At the end of every day
  also `dotnet format --verify-no-changes`; before the last commit of the plan also the
  hermetic run: `Auth__Tokens__Audience=x Auth__Tokens__Issuer=http://x/auth dotnet test`.
- Test hosts: a test that asks for mail builds its host **without**
  `MailDispatchService` and drives `MailDispatcher.DispatchDueAsync` by hand
  (`MailTestBase`, and `TenancyTestBase` on top of it, do both). Never call `StopAsync` on a
  hosted service of a running host. At most about 12 parallel calls per test.
- A shell on Windows (Git Bash): `dotnet` may be missing from `PATH` (use its full path);
  `scripts/dev-keys.sh` needs `MSYS2_ARG_CONV_EXCL="/CN"` so that `-subj /CN=...` is not
  taken for a path; the e2e scripts need a `python3` on `PATH` that works (the Microsoft
  Store shim does not), and `e2e-login.sh` and `e2e-refresh.sh` also need `PyJWT[crypto]`.
  Give the compose project a name of its own (`COMPOSE_PROJECT_NAME`), because another
  session may be using `auth-core`.

## Verified before this plan was written

Probes in a scratch clone (not in the repository), against the real host (PostgreSQL 16,
OpenIddict 7.7.1, Identity 10.0.12), settled the spec's "To verify" items that could
sink the design.

- **Claims.** Login and refresh can add `org_id` (a string) and `roles` and `permissions`
  to the access token. An array must be made with
  `new Claim(type, jsonText, JsonClaimValueTypes.JsonArray)`: one claim per element turns
  a one-element array into a string. By default the **refresh token also receives** the
  three claims, and `RefreshEndpoint` copies the refresh token's claims into the next
  identity: stale values would be doubled. The fix is to strip them before adding the
  fresh ones, and to strip them from the refresh token in `AccessTokenClaimFilter`
  (`TokenType == RefreshToken`). The stamp check of spec 0004, Decision 17, keeps working.
- **Bearer validation of the service's own tokens needs no new package.**
  `AddOpenIddict().AddValidation(o => { o.UseLocalServer(); o.UseAspNetCore(); o.AddAudiences(aud); })`
  plus `AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)`,
  `AddAuthorization()` and `.RequireAuthorization()`. A tampered signature or payload, an
  expired token, a wrong issuer and a wrong audience are `401` with an empty body.
  **Without `AddAudiences` a token for another audience passes**, so it is required and
  tested. Observed challenges: no token → `WWW-Authenticate: Bearer`; a bad or expired
  token → `Bearer error="invalid_token", error_description="…", error_uri="…"` (RFC 6750).
  A multi-element array claim arrives as repeated claims (`FindAll`).
- **The last-manager check can be serialised per company.** Inside
  `BeginTransactionAsync()` (default `READ COMMITTED`), the first statement
  `SELECT 1 FROM "Companies" WHERE "Id" = {id} FOR UPDATE`, then count the managers,
  change, commit: 20 of 20 races of two managers removing each other left one winner. The
  same race without the lock removed both. **At `REPEATABLE READ` the lock does not
  protect the check.**
- **Invitations need a table of their own.** `EmailToken` is keyed `(UserId, Kind)` with a
  foreign key to users, and an invitation may name an address with no account. The mail
  queue is keyed by kind and address, and the dispatcher deletes a row whose address has no
  account: an invitation to a new address would be silently dropped. Hence
  `MailKind.Invitation = 3`, a nullable `InviteId` on `MailRequest` and a branch in the
  dispatcher. The mail limit table is keyed `(IdentifierHash, Kind)`, so a per-company
  limit is `Kind = Invitation` with a domain-separated hash and no schema change.
- **The CLI runs from the same image.** `if (args is ["admin", ..])` at the top of
  `Program.cs`, `WebApplication.CreateBuilder([])` (the empty argument list keeps `--force`
  out of the configuration) with the persistence registration, `Build()` and never
  `Run()`: no Kestrel, no hosted service, no key material. `docker run <image> admin …`
  hands the arguments to the entrypoint; the chiseled image has no shell. An unhandled
  exception ended the process with exit code 139, so the commands catch and report.
- **OpenAPI.** `Microsoft.AspNetCore.OpenApi` is a NuGet package, not part of the shared
  framework. A custom `IResult` (such as the `NoStoreResult` of the account endpoints)
  tells the generator nothing, so every endpoint declares what it produces. Login and
  refresh read their bodies inside OpenIddict handlers, and the JWKS endpoint is OpenIddict
  middleware with no route, so a document transformer adds it.

Found while building this plan's code, and built into it:

- `Accepts<T>()` on a route makes the routing answer `415` to another content type, but
  the contract is `400 invalid_request`, said by the handler. The request bodies are
  therefore documented through metadata of our own (`ReadsJson<T>()`) and an operation
  transformer, not through `Accepts`.
- YamlDotNet's scanner reports an unterminated flow sequence as `InvalidOperationException`,
  not as `YamlException`; the parser catches both, since a broken manifest must never throw.
  Anchors, aliases and a second document are refused before deserialising.
- Identity's default list of allowed user-name characters refuses `zażółć@example.com`,
  and an account made by an invitation has its address as its user name. The list is
  emptied (`AllowedUserNameCharacters = string.Empty`).
- A group's empty route (`MapGroup("/auth/org").MapGet("", …)`) is `/auth/org/` in the
  endpoint data source and `/auth/org` in the description; a route parameter named
  `user_id` needs `[FromRoute(Name = "user_id")]`; and `/auth/scalar` redirects (302) to
  `/auth/scalar/`.
- `UserManager.CreateAsync`, `RemovePasswordAsync` and `AddPasswordAsync` run on the
  request's `AuthDbContext`, so a transaction opened on it covers an acceptance whole,
  OpenIddict's revocations included.

**The plan's code as a whole.** The code below is not the output of a thought: a scratch
copy of the repository received the tasks in this order, each built with warnings as
errors and tested, and the blocks of this plan are that copy's files and diffs, taken from
its commits by a script. **Every task's commit was built and tested on its own**
(`dotnet build -warnaserror`, then the whole suite) and the counts the tasks state are the
ones that run printed; at the end, `dotnet format --verify-no-changes` is clean, the hermetic
run passes, and **all 829 tests pass — the 390 existing ones with the edits named above,
439 new**. All five e2e scripts ended with `ALL PASS` against one real compose stack
(`e2e-login.sh`, `e2e-refresh.sh`, `e2e-lockout.sh`, `e2e-email.sh`, then `e2e-tenancy.sh`,
which was run twice on the same stack, about two minutes each); `e2e-login.sh` and
`e2e-refresh.sh` ran with the repository's PyJWT shim first on `PATH`. The migration below is
described rather than printed: it is generated.

## Review Focus

The spec does not name these, but a client or an attacker would hit them. Each line has a
pinning test in the task named in brackets.

1. **Two requests change one company at the same instant** — two managers removing each
   other, two acceptances of one address by two companies, one role name created six
   times, one address invited six times: exactly one wins, the company keeps a manager,
   nobody gets a `500`. A request that was authorised just before its caller was removed
   or demoted is refused under the lock. [Task 8, Task 9, Task 11, Task 12]
2. **An address in another spelling** (`WORKER@ACME.TEST`, `zażółć.gęślą@…`): the same
   address for the one-invitation rule, the mail limit and the member check; the account
   is made with the address as typed and its letters do not break the account.
   [Task 8, Task 9]
3. **Company and role names with markup or format characters** (`Tom & <Jerry>`,
   `{0}{1}{2}`): never a format error, encoded in the HTML part, absent from every header.
   [Task 7]
4. **The mail server is down while invitations go out:** the request still answers `202`
   at once, the earlier link keeps working until a new mail has really left, a cancelled
   invitation's queued mail is dropped. [Task 7, Task 8]
5. **A manifest that breaks, empties, or drops a permission a role still holds:** the
   service starts, health says `Degraded`, no token carries a name that is not in the
   catalog, a role listing hides it and an edit removes it. [Task 1, Task 3, Task 5, Task 12]
6. **A lesser manager acts on something that holds more than they do:** a caller with
   `members:manage` or `roles:manage` but without `*` removes or re-roles a member who holds
   `*` (or any permission the caller lacks) — even to a role the caller could give — edits or
   deletes a role that holds more than they do, even to shrink it, or resends or cancels an
   invitation for one: `403 permission_not_held`, nothing changes; the operator may.
   [Task 11, Task 12]

## File Structure

```
Directory.Packages.props                         + YamlDotNet, Microsoft.AspNetCore.OpenApi, Scalar.AspNetCore
deploy/auth.yaml                                 the development manifest
deploy/docker-compose.yml                        + the manifest, mounted and named
src/Auth.Infrastructure/
  DependencyInjection.cs                         user names may hold any character
  Persistence/MailKind.cs                        + Invitation = 3
  Persistence/MailRequest.cs                     + InviteId
  Persistence/Company.cs  CompanyRole.cs  Membership.cs  Invite.cs  ActiveManifest.cs
  Persistence/AuthDbContext.cs                   + five sets and their mapping
  Persistence/Migrations/<id>_AddTenancy.cs (+ .Designer.cs), AuthDbContextModelSnapshot.cs
src/Auth.Server/
  Auth.Server.csproj                             + three packages
  appsettings.Development.json                   + manifest path, invitation URL
  Program.cs                                     + the admin branch, tenancy, authentication, routes, OpenAPI
  Requests/NameInput.cs  IdInput.cs  RoleBody.cs  names, UUIDs, the body of a role
  Requests/EmailInput.cs                         + IsMailbox
  Tenancy/PermissionCatalog.cs  Manifest.cs  ManifestParser.cs   the catalog, the manifest, reading it
  Tenancy/ManifestSettings.cs  ManifestHolder.cs  ManifestActivator.cs  ManifestHealthCheck.cs   which manifest is active
  Tenancy/Outcome.cs                             error codes and what a service answers
  Tenancy/CompanyService.cs  MembershipReader.cs  TenantClaims.cs  creating companies, reading a membership, the claims
  Tenancy/CompanyAccess.cs                       the permission check of the company API
  Tenancy/Actor.cs  CompanyLock.cs  CompanyGuard.cs  CompanyPeople.cs   who acts, the lock, the manager arithmetic
  Tenancy/InviteTokens.cs  InvitationService.cs  InviteAcceptance.cs   invitations
  Tenancy/MemberService.cs  RoleService.cs  OperatorCommands.cs  Contracts.cs  TenancyServices.cs
  Api/ApiResults.cs  NoStoreMiddleware.cs  EndpointMetadata.cs  results, headers, what an endpoint declares
  Api/OrgEndpoints.cs  OrgInviteEndpoints.cs  OrgMemberEndpoints.cs  OrgRoleEndpoints.cs  InviteEndpoints.cs   the handlers
  Api/TenancyEndpoints.cs  AccountEndpoints.cs  AccountContracts.cs   the routes of specs 0001–0005
  Api/OpenApiSetup.cs                            the description
  Admin/AdminArguments.cs  AdminCli.cs           the operator commands
  Account/AccountResults.cs                      + no_membership; NoStoreResult is internal
  Login/LoginEndpoint.cs  Sessions/RefreshEndpoint.cs  Sessions/AccessTokenClaimFilter.cs   the claims
  Tokens/OpenIddictSetup.cs                      + bearer validation
  Email/MailSettings.cs  MailTexts.cs  MailComposer.cs  EmailTokens.cs   the invitation mail
  Email/MailLimits.cs  MailRequestStore.cs  MailDispatcher.cs  the shared limit, the Invitation branch
  Email/EmailPruner.cs  EmailPruningService.cs   + expired invitations
  Seeding/DevUserSeeder.cs                       + the development company
tests/Auth.IntegrationTests/
  Infrastructure/TenancyTestBase.cs  TenancyApi.cs  AccessTokens.cs        helpers
  Infrastructure/AuthAppFactory.cs  MailTestBase.cs  SessionApi.cs         edited
  ManifestParserTests.cs  PermissionCatalogTests.cs  NameInputTests.cs  TenancyTablesTests.cs
  ManifestActivationTests.cs  CompanyServiceTests.cs  DevCompanySeedTests.cs  TenantClaimsTests.cs
  BearerValidationTests.cs  OrgEndpointTests.cs  InvitationMailComposerTests.cs  InvitationDispatchTests.cs
  OrgInviteTests.cs  InviteAcceptTests.cs  InvitePruningTests.cs  OrgMemberTests.cs  OrgRoleTests.cs
  AdminArgumentsTests.cs  AdminCliTests.cs  OpenApiTests.cs
scripts/e2e-tenancy.sh                           the Goal sequence and the safety cases over the real network
docs/superpowers/plans/0005-acceptance-map.md    criteria → tests
docs/superpowers/plans/0001-acceptance-map.md    one renamed test
docs/design.md  README.md                        the CLI row; quickstart and status
```

New code lives in `Tenancy/` (the model and its services), `Api/` (handlers, routes and the
description) and `Admin/` (the CLI). The files of the earlier slices change only where this
plan says: the claims at login and refresh (Task 5), bearer validation (Task 6), the mail
queue and dispatcher (Task 7), pruning (Task 10), the seeder (Task 4), and the routes of
specs 0001–0004, which Task 14 moves out of `Program.cs` into `AccountEndpoints.cs` so that
they can say what they take and answer.

Test conventions used below (all exist, or are made by the task that first uses them):
`SessionTestBase` gives `Postgres`, `Keys`, `Clock` (a `FakeTimeProvider`, frozen until a
test advances it), `Factory` and `Client`; `MailTestBase` adds `Mail` (the capturing
transport), `Logs`, `DispatchAsync`, `InDbAsync`, `EnqueueAsync`, `CreateUserAsync`,
`TokenIn`; `TenancyTestBase` (Task 4) adds the company helpers; `LoginApi`, `SessionApi`,
`LockoutApi`, `AccountApi`, `AuthAppFactory.WithSetting`, `WithServices`,
`WithoutHostedService<T>()`, `WithEnvironment`, `SeedEmail`, `SeedPassword`. Every test host
has a database of its own, and a manifest file of its own. The test manifest is the example
of the spec: `permissions: [reports:read, reports:approve, templates:manage]`, roles
`admin: ["*"]` and `user: [reports:read, reports:approve]`, so the catalog of every test
host is `members:manage`, `org:manage`, `reports:approve`, `reports:read`, `roles:manage`,
`templates:manage`.

---

## Acceptance criteria of the spec, and where each is built and guarded

Every criterion 1–25 of the spec is built by the tasks named and guarded by the tests named
(the class names are the main guards; **the full table, test by test, is the content of
`docs/superpowers/plans/0005-acceptance-map.md`, which Task 15 produces**). A criterion with
no guard would be a finding, so the tests that guard a criterion say so in a `// criterion N` comment.

| # | Criterion (short) | Built in | Guarded by |
| - | ----------------- | -------- | ---------- |
| 1 | `create-org` copies the default roles, prints the id; `list-orgs` shows it | 4, 13 | `CompanyServiceTests`, `AdminCliTests`, `AdminArgumentsTests`; e2e step 3 |
| 2 | Invitation mail: language, application, company, role, URL + `token`, 7 days | 7, 8, 13 | `InvitationMailComposerTests`, `InvitationDispatchTests`, `OrgInviteTests`, `AdminCliTests`; e2e 4, 6 |
| 3 | `preview` shows company, address, role; token stays usable | 9 | `InviteAcceptTests`; e2e 4 |
| 4 | `accept` → `204`: confirmed member; login has the claims (`*` expanded, sorted) | 5, 9 | `InviteAcceptTests`, `TenantClaimsTests`, `PermissionCatalogTests`; e2e 4–5 |
| 5 | Account without a company: password replaced, every session ended | 9 | `InviteAcceptTests` |
| 6 | One `invalid_token` for every unusable token; parallel accepts: one wins | 9 | `InviteAcceptTests`; e2e 4 |
| 7 | Weak password: `400`, nothing changes, token usable | 9 | `InviteAcceptTests`; e2e 4 |
| 8 | Address of another company: same `202`; `409 already_member` at preview and accept | 8, 9 | `OrgInviteTests`, `InviteAcceptTests`; e2e 8 |
| 9 | `already_in_org`, `invite_pending` | 8 | `OrgInviteTests` |
| 10 | Resend, cancel, mail limit per company and address | 7, 8, 9, 13 | `OrgInviteTests`, `InvitationDispatchTests`, `InviteAcceptTests`, `AdminCliTests` |
| 11 | `403 no_membership`: no token, no cookie, streak ends | 5 | `TenantClaimsTests`; e2e 9 |
| 12 | Role change or role edit reaches the next refresh; no session ends | 5, 11, 12 | `TenantClaimsTests`, `OrgMemberTests`, `OrgRoleTests`; e2e 7 |
| 13 | Removal: refresh `401`, login `403 no_membership`, invitable again | 5, 11 | `OrgMemberTests`, `TenantClaimsTests`; e2e 9 |
| 14 | `401` / `forbidden` / `permissions_changed`; never another company's data | 2, 6, 8, 11, 12 | `BearerValidationTests`, `OrgEndpointTests`, `Org*Tests`, `TenancyTablesTests`; e2e 7–9 |
| 15 | Rule 1, one rule: nobody acts on anything that holds more than they do (roles, members, invitations); `*` only by `*` | 8, 11, 12 | `OrgInviteTests`, `OrgMemberTests`, `OrgRoleTests`; e2e 7 |
| 16 | Rule 2: a company keeps a manager, also under concurrency | 11, 12, 13 | `OrgMemberTests`, `OrgRoleTests`; e2e 7, 10 |
| 17 | Rule 3: not one's own role, not oneself | 11 | `OrgMemberTests`; e2e 7 |
| 18 | Roles: create, rename, change, delete; `role_name_taken`, `unknown_permission`, `role_in_use` | 12 | `OrgRoleTests`; e2e 7 |
| 19 | `/auth/me`, `/auth/org`, `PATCH /auth/org` | 6 | `OrgEndpointTests`; e2e 2 |
| 20 | Manifest: valid → active and stored; broken → last stored, `Degraded`, host starts | 1, 3, 4 | `ManifestParserTests`, `ManifestActivationTests`, `CompanyServiceTests`, `AdminCliTests`; e2e 1 |
| 21 | A permission removed from or added to the manifest reaches tokens at the next refresh | 1, 5, 12 | `TenantClaimsTests`, `PermissionCatalogTests`, `ManifestActivationTests`, `OrgRoleTests` |
| 22 | `remove-member`; `last_manager` unless `--force` | 11, 13 | `AdminCliTests`, `AdminArgumentsTests`; e2e 10 |
| 23 | No token in the database or logs; names encoded in the mail | 7, 9 | `InvitationDispatchTests`, `InvitationMailComposerTests`, `InviteAcceptTests` |
| 24 | OpenAPI describes every endpoint, request, response and error code | 14 | `OpenApiTests`; e2e 1 |
| 25 | e2e script drives the Goal and the safety cases; the four old scripts pass | 15 | `scripts/e2e-tenancy.sh`; the four existing scripts unedited |

---

## Day 1 — the manifest, the catalog and the tables

Nothing a client can see changes yet: when the day is done the service reads
its manifest at startup, stores it, reports `Degraded` when the file is broken, and has
the tables the rest of the slice needs. Every task leaves the build and all tests green.

### Task 1: The permission catalog and the manifest

**Files:**
- Create: `src/Auth.Server/Requests/NameInput.cs`, `src/Auth.Server/Tenancy/PermissionCatalog.cs`,
  `src/Auth.Server/Tenancy/Manifest.cs`, `src/Auth.Server/Tenancy/ManifestParser.cs`
- Modify: `Directory.Packages.props`, `src/Auth.Server/Auth.Server.csproj`
- Test: `tests/Auth.IntegrationTests/ManifestParserTests.cs`,
  `tests/Auth.IntegrationTests/PermissionCatalogTests.cs`,
  `tests/Auth.IntegrationTests/NameInputTests.cs`

**Interfaces:**
- Produces: `NameInput` (static) with `const int MaxLength = 100`,
  `static bool IsValid(string name)` (1–100 characters, no control character,
  noncharacter or unpaired surrogate, no leading or trailing white space) and
  `static string Normalize(string name)` (upper case: names are unique within a company
  regardless of case).
- Produces: `PermissionCatalog` with `const string All = "*"`, `MembersManage`,
  `RolesManage`, `OrgManage`, `BuiltIn`, a constructor from the declared permissions,
  `Permissions` (the whole catalog, ordinal, never `*`), `Listed` (`*` first),
  `static bool IsWellFormed(string)`, `bool Accepts(string)` (`*` or a permission of the
  catalog), `string[] Expand(IEnumerable<string> stored)` (`*` expanded, names that left the
  catalog dropped, once each, sorted) and `string[] Visible(IEnumerable<string> stored)`
  (what a role listing shows: still in the catalog, `*` kept and first).
- Produces: `DefaultRole(string Name, IReadOnlyList<string> Permissions)`;
  `Manifest` with `Permissions`, `DefaultRoles`, `Catalog`, `static Manifest BuiltIn`
  (no permissions of its own, one default role `admin: ["*"]`), `ToJson()` and
  `static Manifest? FromJson(string)` (the form kept in the database).
- Produces: `ManifestParser.Parse(string yaml)` → `ManifestParseResult(Manifest? Manifest,
  string? Error)`; it never throws for bad input, and `ManifestParser.MaxBytes` (64 KiB).
  The default roles **keep the order of the file** (it decides the role of the second
  development seed user, Task 4), and a default role that lists `*` is kept as `["*"]`
  alone, as a role edited through the API is stored (so every company's copy is too).

- [ ] **Step 1: Add the package** (the owner approved the download). The
  version goes into the central file, the reference into the server project:

`Directory.Packages.props` — the change:

```diff
--- a/Directory.Packages.props
+++ b/Directory.Packages.props
@@ -20,5 +20,7 @@
     <PackageVersion Include="Microsoft.IdentityModel.JsonWebTokens" Version="8.23.0" />
     <!-- Mail (plan 0004, Task 2). MIT; brings MimeKit and BouncyCastle.Cryptography, both MIT. -->
     <PackageVersion Include="MailKit" Version="4.18.1" />
+    <!-- Manifest (plan 0005, Task 1). MIT; no dependencies on net10.0. -->
+    <PackageVersion Include="YamlDotNet" Version="18.1.0" />
   </ItemGroup>
 </Project>
```

`src/Auth.Server/Auth.Server.csproj` — the change:

```diff
--- a/src/Auth.Server/Auth.Server.csproj
+++ b/src/Auth.Server/Auth.Server.csproj
@@ -11,6 +11,7 @@
   <ItemGroup>
     <PackageReference Include="OpenIddict.AspNetCore" />
     <PackageReference Include="MailKit" />
+    <PackageReference Include="YamlDotNet" />
   </ItemGroup>
 
   <ItemGroup>
```

- [ ] **Step 2: Write the failing tests.**

`tests/Auth.IntegrationTests/ManifestParserTests.cs`:

```csharp
using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

public sealed class ManifestParserTests
{
    private const string Valid = """
        permissions: [reports:read, reports:approve, templates:manage]
        default_roles:
          admin: ["*"]
          user:  [reports:read, reports:approve]
        """;

    private static string Error(string yaml)
    {
        var result = ManifestParser.Parse(yaml);

        Assert.Null(result.Manifest);
        return Assert.IsType<string>(result.Error);
    }

    [Fact]
    public void Manifest_of_the_spec_is_read()   // criterion 20
    {
        var result = ManifestParser.Parse(Valid);

        Assert.Null(result.Error);
        var manifest = Assert.IsType<Manifest>(result.Manifest);
        Assert.Equal(["reports:read", "reports:approve", "templates:manage"], manifest.Permissions);
        Assert.Equal(["admin", "user"], manifest.DefaultRoles.Select(r => r.Name));
        Assert.Equal(["*"], manifest.DefaultRoles[0].Permissions);
        Assert.Equal(["reports:read", "reports:approve"], manifest.DefaultRoles[1].Permissions);
    }

    [Fact]
    public void Catalog_is_the_declared_permissions_plus_the_built_in_ones()
    {
        var manifest = ManifestParser.Parse(Valid).Manifest!;

        Assert.Equal(
            ["members:manage", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            manifest.Catalog.Permissions);
        Assert.Equal("*", manifest.Catalog.Listed[0]);
        Assert.Equal(7, manifest.Catalog.Listed.Count);
    }

    [Fact]
    public void Built_in_permissions_may_be_listed_and_are_not_duplicated_in_the_catalog()
    {
        var manifest = ManifestParser.Parse("""
            permissions: [members:manage, docs:read]
            default_roles:
              owner: [members:manage]
            """).Manifest!;

        Assert.Equal(["docs:read", "members:manage", "org:manage", "roles:manage"], manifest.Catalog.Permissions);
    }

    [Fact]
    public void A_manifest_without_permissions_has_the_built_in_catalog()
    {
        var manifest = ManifestParser.Parse("""
            default_roles:
              owner: ["*"]
            """).Manifest!;

        Assert.Equal(PermissionCatalog.BuiltIn, manifest.Catalog.Permissions);
    }

    [Fact]
    public void Built_in_manifest_has_one_default_role_holding_everything()
    {
        Assert.Empty(Manifest.BuiltIn.Permissions);
        var role = Assert.Single(Manifest.BuiltIn.DefaultRoles);
        Assert.Equal("admin", role.Name);
        Assert.Equal(["*"], role.Permissions);
    }

    [Fact]
    public void Manifest_survives_the_form_kept_in_the_database()
    {
        var manifest = ManifestParser.Parse(Valid).Manifest!;

        var back = Manifest.FromJson(manifest.ToJson());

        Assert.NotNull(back);
        Assert.Equal(manifest.Permissions, back.Permissions);
        Assert.Equal(
            manifest.DefaultRoles.Select(r => (r.Name, string.Join(',', r.Permissions))),
            back.DefaultRoles.Select(r => (r.Name, string.Join(',', r.Permissions))));
        Assert.Null(Manifest.FromJson("not json"));
    }

    [Theory]
    [InlineData("Reports:read")]            // uppercase
    [InlineData("1reports")]                // starts with a digit
    [InlineData(":reports")]                // starts with a colon
    [InlineData("reports read")]            // a space
    [InlineData("reports.read")]            // a dot
    [InlineData("zażółć")]                  // not ASCII
    [InlineData("")]                        // empty
    [InlineData("*")]                       // the wildcard is not a permission to declare
    public void Malformed_permission_makes_the_manifest_invalid(string permission)   // criterion 20
    {
        var yaml = $"""
            permissions: ["{permission}"]
            default_roles:
              admin: ["*"]
            """;

        Assert.Contains("permission", Error(yaml));
    }

    [Fact]
    public void Permission_of_64_characters_is_accepted_and_of_65_is_not()
    {
        var sixtyFour = "a" + new string('b', 63);
        Assert.Null(ManifestParser.Parse($"permissions: [{sixtyFour}]\ndefault_roles:\n  admin: [\"*\"]\n").Error);
        Assert.NotNull(ManifestParser.Parse($"permissions: [{sixtyFour}b]\ndefault_roles:\n  admin: [\"*\"]\n").Error);
    }

    [Fact]
    public void Permission_with_a_trailing_newline_is_malformed()
    {
        Assert.False(PermissionCatalog.IsWellFormed("reports:read\n"));
        Assert.True(PermissionCatalog.IsWellFormed("reports:read"));
    }

    [Fact]
    public void Default_role_with_a_permission_outside_the_catalog_makes_the_manifest_invalid()
    {
        var error = Error("""
            permissions: [reports:read]
            default_roles:
              admin: ["*"]
              user: [reports:write]
            """);

        Assert.Contains("reports:write", error);
        Assert.Contains("user", error);
    }

    [Fact]
    public void Default_role_may_hold_a_built_in_permission_that_is_not_listed()
    {
        var result = ManifestParser.Parse("""
            default_roles:
              owner: [members:manage, org:manage]
            """);

        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("default_roles:\n  user: []\n")]                                    // nobody can manage members
    [InlineData("permissions: [a:b]\ndefault_roles:\n  user: [a:b]\n")]             // same
    [InlineData("default_roles:\n  user: [roles:manage, org:manage]\n")]            // manages roles, not members
    public void At_least_one_default_role_must_manage_members(string yaml)   // criterion 20
    {
        Assert.Contains("members:manage", Error(yaml));
    }

    [Theory]
    [InlineData("")]
    [InlineData("permissions: [a:b]\n")]                                            // no default_roles
    [InlineData("permissions: [a:b]\ndefault_roles: {}\n")]
    [InlineData("- just\n- a list\n")]
    [InlineData("just a string\n")]
    [InlineData("permissions: [a:b\ndefault_roles: [\n")]                           // not YAML
    [InlineData("permissions: {a: b}\ndefault_roles:\n  admin: [\"*\"]\n")]         // wrong shape
    [InlineData("permissions: [[a:b]]\ndefault_roles:\n  admin: [\"*\"]\n")]        // a list inside the list
    [InlineData("permisions: [a:b]\ndefault_roles:\n  admin: [\"*\"]\n")]           // a typo is an unknown key, not an empty list
    [InlineData("default_roles:\n  admin: [\"*\"]\napp:\n  name: x\n")]             // the old keys of design.md are not accepted
    public void Text_that_is_not_a_manifest_is_refused_with_a_reason(string yaml)   // criterion 20
    {
        Assert.False(string.IsNullOrWhiteSpace(Error(yaml)));
    }

    [Fact]
    public void Duplicate_keys_are_refused()
    {
        Assert.NotNull(ManifestParser.Parse("default_roles:\n  admin: [\"*\"]\ndefault_roles:\n  user: [\"*\"]\n").Error);
        Assert.NotNull(ManifestParser.Parse("default_roles:\n  admin: [\"*\"]\n  admin: [\"*\"]\n").Error);
    }

    [Fact]
    public void Anchors_aliases_and_a_second_document_are_refused()
    {
        Assert.Contains("anchors", Error("permissions: &p [a:b]\ndefault_roles:\n  admin: [\"*\"]\n"));
        Assert.Contains("aliases", Error("permissions: *p\ndefault_roles:\n  admin: [\"*\"]\n"));
        Assert.Contains("more than one document", Error("default_roles:\n  admin: [\"*\"]\n---\ndefault_roles:\n  admin: [\"*\"]\n"));
    }

    [Fact]
    public void Role_names_follow_the_rules_for_names()
    {
        Assert.Contains("name", Error("default_roles:\n  \" admin\": [\"*\"]\n"));
        Assert.Contains("name", Error($"default_roles:\n  {new string('a', 101)}: [\"*\"]\n"));
        // A raw string: the YAML itself holds the escape, which the parser turns into a control character.
        Assert.Contains("name", Error("""
            default_roles:
              "ad\u0007min": ["*"]
            """));
        Assert.Null(ManifestParser.Parse($"default_roles:\n  {new string('a', 100)}: [\"*\"]\n").Error);
        Assert.Null(ManifestParser.Parse("default_roles:\n  Kierownik żółw: [\"*\"]\n").Error);
    }

    [Fact]
    public void Role_names_that_differ_only_in_case_are_a_duplicate()
    {
        Assert.Contains("twice", Error("default_roles:\n  admin: [\"*\"]\n  Admin: [\"*\"]\n"));
    }

    [Fact]
    public void Role_without_a_value_holds_nothing()
    {
        var manifest = ManifestParser.Parse("default_roles:\n  admin: [\"*\"]\n  guest:\n").Manifest!;

        Assert.Empty(manifest.DefaultRoles.Single(r => r.Name == "guest").Permissions);
    }

    [Fact]
    public void Repeated_permissions_are_kept_once()
    {
        var manifest = ManifestParser.Parse("permissions: [a:b, a:b]\ndefault_roles:\n  admin: [\"*\", \"*\", a:b]\n").Manifest!;

        Assert.Equal(["a:b"], manifest.Permissions);
        Assert.Equal(["*"], manifest.DefaultRoles[0].Permissions);   // star holds the rest
    }

    [Fact]
    public void A_role_that_lists_star_is_star_alone()
    {
        var manifest = ManifestParser.Parse("permissions: [a:b]\ndefault_roles:\n  admin: [a:b, \"*\"]\n  user: [a:b]\n").Manifest!;

        Assert.Equal(["*"], manifest.DefaultRoles[0].Permissions);
        Assert.Equal(["a:b"], manifest.DefaultRoles[1].Permissions);
    }

    [Fact]
    public void Default_roles_keep_the_order_of_the_file()
    {
        var manifest = ManifestParser.Parse("permissions: [a:b]\ndefault_roles:\n  zeta: [a:b]\n  admin: [\"*\"]\n  alpha: [a:b]\n").Manifest!;

        Assert.Equal(["zeta", "admin", "alpha"], manifest.DefaultRoles.Select(r => r.Name));
    }

    [Fact]
    public void Manifest_with_101_default_roles_is_refused_and_with_100_is_accepted()
    {
        static string Roles(int count) =>
            "default_roles:\n  admin: [\"*\"]\n" + string.Concat(Enumerable.Range(1, count - 1).Select(i => $"  r{i}: []\n"));

        Assert.Null(ManifestParser.Parse(Roles(100)).Error);
        Assert.Contains("100", Error(Roles(101)));
    }

    [Fact]
    public void Manifest_with_too_many_permissions_is_refused()
    {
        var many = string.Join(", ", Enumerable.Range(0, 501).Select(i => "p" + i));

        Assert.Contains("500", Error($"permissions: [{many}]\ndefault_roles:\n  admin: [\"*\"]\n"));
    }
}
```

`tests/Auth.IntegrationTests/NameInputTests.cs`:

```csharp
using Auth.Server.Requests;

namespace Auth.IntegrationTests;

public sealed class NameInputTests
{
    [Theory]
    [InlineData("Acme", true)]
    [InlineData("Acme sp. z o.o.", true)]
    [InlineData("Żółć & Spółka", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData(" Acme", false)]            // leading white space
    [InlineData("Acme ", false)]            // trailing white space
    [InlineData("\u00A0Acme", false)]       // a no-break space counts as white space
    [InlineData("Ac\u0009me", false)]       // a control character
    [InlineData("Ac\u000Ame", false)]
    [InlineData("Ac\u0085me", false)]
    [InlineData("Ac\uFFFEme", false)]       // a noncharacter
    [InlineData("Ac\uFDD0me", false)]
    public void Names_follow_the_rules(string name, bool valid)
    {
        Assert.Equal(valid, NameInput.IsValid(name));
    }

    [Fact]
    public void Name_may_be_100_characters_but_not_101()
    {
        Assert.True(NameInput.IsValid(new string('a', 100)));
        Assert.False(NameInput.IsValid(new string('a', 101)));
    }

    [Fact]
    public void Unpaired_surrogate_is_not_a_valid_name()
    {
        // Built at run time: an attribute argument cannot hold an unpaired surrogate.
        Assert.False(NameInput.IsValid("a" + (char)0xD800 + "b"));
        Assert.True(NameInput.IsValid("a\uD83D\uDE00b"));     // a well-formed pair
    }

    [Fact]
    public void Normalised_names_ignore_case()
    {
        Assert.Equal(NameInput.Normalize("Admin"), NameInput.Normalize("aDMIN"));
        Assert.Equal(NameInput.Normalize("zażółć"), NameInput.Normalize("ZAŻÓŁĆ"));
        Assert.NotEqual(NameInput.Normalize("admin"), NameInput.Normalize("admins"));
    }
}
```

`tests/Auth.IntegrationTests/PermissionCatalogTests.cs`:

```csharp
using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

public sealed class PermissionCatalogTests
{
    private static readonly PermissionCatalog Catalog = new(["reports:read", "reports:approve", "templates:manage"]);

    [Fact]
    public void Star_expands_to_the_whole_catalog_sorted_without_star()   // criterion 4
    {
        var expanded = Catalog.Expand(["*"]);

        Assert.Equal(
            ["members:manage", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            expanded);
        Assert.DoesNotContain("*", expanded);
    }

    [Fact]
    public void Star_among_other_permissions_still_gives_each_one_once()
    {
        Assert.Equal(Catalog.Permissions, Catalog.Expand(["reports:read", "*", "reports:read"]));
    }

    [Fact]
    public void Listed_permissions_are_sorted_ordinally_without_duplicates()
    {
        Assert.Equal(["reports:approve", "reports:read"], Catalog.Expand(["reports:read", "reports:approve", "reports:read"]));
    }

    [Fact]
    public void A_permission_that_left_the_catalog_grants_nothing_and_is_not_shown()   // criterion 21
    {
        Assert.Equal(["reports:read"], Catalog.Expand(["reports:read", "old:thing"]));
        Assert.Equal(["reports:read"], Catalog.Visible(["old:thing", "reports:read"]));
    }

    [Fact]
    public void The_list_of_a_role_keeps_star_as_it_is_and_puts_it_first()
    {
        Assert.Equal(["*", "reports:read"], Catalog.Visible(["reports:read", "*"]));
    }

    [Fact]
    public void Catalog_accepts_star_the_built_in_permissions_and_the_declared_ones_only()
    {
        Assert.True(Catalog.Accepts("*"));
        Assert.True(Catalog.Accepts("members:manage"));
        Assert.True(Catalog.Accepts("reports:read"));
        Assert.False(Catalog.Accepts("reports:write"));
        Assert.False(Catalog.Accepts("REPORTS:READ"));
        Assert.False(Catalog.Accepts(""));
    }

    [Fact]
    public void Listed_has_star_first_then_every_permission()
    {
        Assert.Equal("*", Catalog.Listed[0]);
        Assert.Equal(Catalog.Permissions, Catalog.Listed.Skip(1));
    }
}
```

- [ ] **Step 3: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`ManifestParser`, `Manifest`, `PermissionCatalog` and
  `NameInput` do not exist). Check the escapes of the Unicode rule in
  `NameInputTests.cs` and `ManifestParserTests.cs` now.

- [ ] **Step 4: Implement.**

`src/Auth.Server/Requests/NameInput.cs`:

```csharp
namespace Auth.Server.Requests;

/// <summary>
/// The rules for the names members and the manifest give to companies and roles (spec 0005 → General rules): at most
/// <see cref="MaxLength"/> characters, no control character, noncharacter or unpaired surrogate, no leading or trailing
/// white space. A name ends up in mails and admin pages, so what it may hold is narrow.
/// </summary>
public static class NameInput
{
    public const int MaxLength = 100;

    public static bool IsValid(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Length is > 0 and <= MaxLength
            && EmailInput.IsWellFormed(name)
            && string.Equals(name, name.Trim(), StringComparison.Ordinal);
    }

    /// <summary>The key a name is compared by: names are unique within a company regardless of case.</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.ToUpperInvariant();
    }
}
```

`src/Auth.Server/Tenancy/Manifest.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Auth.Server.Tenancy;

/// <summary>A role every new company starts with a copy of (spec 0005 → Manifest).</summary>
public sealed record DefaultRole(string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// What a product declares in its <c>auth.yaml</c>: its permissions and its default roles. Always valid: it is made by
/// <see cref="ManifestParser"/> or is <see cref="BuiltIn"/>, and what is stored in the database was one of those.
/// </summary>
public sealed class Manifest
{
    private static readonly JsonSerializerOptions StoredJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public Manifest(IReadOnlyList<string> permissions, IReadOnlyList<DefaultRole> defaultRoles)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(defaultRoles);

        Permissions = permissions;
        DefaultRoles = defaultRoles;
        Catalog = new PermissionCatalog(permissions);
    }

    /// <summary>With no valid manifest ever stored: the built-in permissions and one default role, <c>admin</c>, holding everything.</summary>
    public static Manifest BuiltIn { get; } = new([], [new DefaultRole("admin", [PermissionCatalog.All])]);

    /// <summary>The permissions the product declares, in the order of the file.</summary>
    public IReadOnlyList<string> Permissions { get; }

    public IReadOnlyList<DefaultRole> DefaultRoles { get; }

    public PermissionCatalog Catalog { get; }

    /// <summary>The form kept in the database.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new StoredManifest([.. Permissions], [.. DefaultRoles.Select(r => new StoredRole(r.Name, [.. r.Permissions]))]), StoredJson);

    /// <summary>The manifest kept in the database; <see langword="null"/> when the text is not one (which it never is, unless edited by hand).</summary>
    public static Manifest? FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            var stored = JsonSerializer.Deserialize<StoredManifest>(json, StoredJson);
            return stored is null
                ? null
                : new Manifest(stored.Permissions, [.. stored.DefaultRoles.Select(r => new DefaultRole(r.Name, r.Permissions))]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record StoredRole(string Name, [property: JsonPropertyName("permissions")] string[] Permissions);

    private sealed record StoredManifest(string[] Permissions, StoredRole[] DefaultRoles);
}
```

`src/Auth.Server/Tenancy/ManifestParser.cs`:

```csharp
using Auth.Server.Requests;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Auth.Server.Tenancy;

/// <summary>The outcome of reading a manifest: the manifest, or the reason the text is not one.</summary>
public readonly record struct ManifestParseResult(Manifest? Manifest, string? Error);

/// <summary>
/// Reads and validates the product's <c>auth.yaml</c> as a whole (spec 0005 → Manifest). It never throws for bad
/// input: a broken file must not stop the service, so the caller gets the reason instead.
/// </summary>
public static class ManifestParser
{
    /// <summary>The largest manifest read, in bytes. A permission list does not come close.</summary>
    public const int MaxBytes = 64 * 1024;

    private const int MaxPermissions = 500;
    private const int MaxDefaultRoles = 100;

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();

    public static ManifestParseResult Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        try
        {
            if (StructureError(yaml) is { } structure)
            {
                return Fail(structure);
            }

            var document = Yaml.Deserialize<ManifestDocument?>(yaml);
            return document is null ? Fail("the file is empty") : Validate(document);
        }
        catch (Exception exception) when (exception is YamlException or InvalidOperationException or ArgumentException or FormatException)
        {
            // YamlDotNet's scanner reports some malformed input (an unterminated flow sequence) as an
            // InvalidOperationException, not as a YamlException. Either way the file is not a manifest.
            return Fail("the file is not valid YAML for a manifest: " + exception.Message);
        }
    }

    /// <summary>Anchors, aliases and a second document have no use here, and aliases are how a small file becomes a huge one.</summary>
    private static string? StructureError(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        var documents = 0;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case DocumentStart:
                    documents++;
                    break;
                case AnchorAlias:
                    return "aliases are not allowed";
                case NodeEvent { Anchor.IsEmpty: false }:
                    return "anchors are not allowed";
            }
        }

        return documents > 1 ? "the file holds more than one document" : null;
    }

    private static ManifestParseResult Validate(ManifestDocument document)
    {
        var permissions = (document.Permissions ?? []).Select(p => p ?? "").ToList();
        if (permissions.Count > MaxPermissions)
        {
            return Fail($"more than {MaxPermissions} permissions");
        }

        if (permissions.FirstOrDefault(p => !PermissionCatalog.IsWellFormed(p)) is { } malformed)
        {
            return Fail($"permission '{malformed}' must be 1-64 lowercase letters, digits, '_', '-' or ':', starting with a letter");
        }

        permissions = [.. permissions.Distinct(StringComparer.Ordinal)];
        var catalog = new PermissionCatalog(permissions);

        var roles = document.DefaultRoles ?? [];
        if (roles.Count is 0 or > MaxDefaultRoles)
        {
            return Fail($"default_roles must hold between 1 and {MaxDefaultRoles} roles");
        }

        var defaults = new List<DefaultRole>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, listed) in roles)
        {
            if (!NameInput.IsValid(name))
            {
                return Fail($"default role name '{name}' is not a valid name");
            }

            if (!seen.Add(NameInput.Normalize(name)))
            {
                return Fail($"default role '{name}' is declared twice (names are compared without regard to case)");
            }

            var held = (listed ?? []).Select(p => p ?? "").ToList();
            if (held.FirstOrDefault(p => !catalog.Accepts(p)) is { } unknown)
            {
                return Fail($"default role '{name}' holds '{unknown}', which is not in the catalog");
            }

            // `*` already holds the rest: a role that lists it is `*` alone, wherever it is copied.
            defaults.Add(new DefaultRole(
                name,
                held.Contains(PermissionCatalog.All, StringComparer.Ordinal)
                    ? [PermissionCatalog.All]
                    : [.. held.Distinct(StringComparer.Ordinal)]));
        }

        if (!defaults.Any(r => catalog.Expand(r.Permissions).Contains(PermissionCatalog.MembersManage)))
        {
            return Fail($"at least one default role must hold '{PermissionCatalog.MembersManage}' or '*', so that the first admin of a company can manage it");
        }

        return new ManifestParseResult(new Manifest(permissions, defaults), null);
    }

    private static ManifestParseResult Fail(string error) => new(null, error);

    // Public properties because the deserializer sets them; unknown keys are an error, so a typo is not an empty list.
    internal sealed class ManifestDocument
    {
        public List<string?>? Permissions { get; set; }

        // Ordered: the roles keep the order of the file, which the development seeder reads.
        public OrderedDictionary<string, List<string?>?>? DefaultRoles { get; set; }
    }
}
```

`src/Auth.Server/Tenancy/PermissionCatalog.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Auth.Server.Tenancy;

/// <summary>
/// The permissions of an instance (spec 0005 → Concepts): the three built-in ones that guard the company API, plus
/// the ones the product declares in its manifest. A role holds permissions from the catalog, or <c>*</c>, which is
/// all of them, now and after the catalog grows.
/// </summary>
public sealed partial class PermissionCatalog
{
    public const string All = "*";
    public const string MembersManage = "members:manage";
    public const string RolesManage = "roles:manage";
    public const string OrgManage = "org:manage";

    public static readonly IReadOnlyList<string> BuiltIn = [MembersManage, OrgManage, RolesManage];

    private readonly HashSet<string> _members;

    public PermissionCatalog(IEnumerable<string> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        _members = new HashSet<string>(declared.Concat(BuiltIn), StringComparer.Ordinal);
        Permissions = [.. _members.Order(StringComparer.Ordinal)];
        Listed = [All, .. Permissions];
    }

    /// <summary>Every permission of the catalog, sorted ordinally; never <c>*</c>.</summary>
    public IReadOnlyList<string> Permissions { get; }

    /// <summary>What a role may hold, as the company API lists it: <c>*</c> first, then the permissions.</summary>
    public IReadOnlyList<string> Listed { get; }

    /// <summary>1–64 characters of lowercase ASCII letters, digits, <c>_</c>, <c>-</c> and <c>:</c>, starting with a letter.</summary>
    public static bool IsWellFormed(string permission)
    {
        ArgumentNullException.ThrowIfNull(permission);

        return PermissionShape().IsMatch(permission);
    }

    /// <summary>Whether a role may hold it: <c>*</c> or a permission of the catalog.</summary>
    public bool Accepts(string permission) =>
        string.Equals(permission, All, StringComparison.Ordinal) || _members.Contains(permission);

    /// <summary>
    /// What a role with these stored permissions grants: <c>*</c> expanded to the whole catalog, names that have left
    /// the catalog dropped, no duplicates, sorted ordinally. Never contains <c>*</c>.
    /// </summary>
    public string[] Expand(IEnumerable<string> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var list = stored as IReadOnlyCollection<string> ?? [.. stored];
        if (list.Contains(All, StringComparer.Ordinal))
        {
            return [.. Permissions];
        }

        return [.. list.Where(_members.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// What the role list shows: the stored permissions that are still in the catalog (<c>*</c> kept as it is),
    /// <c>*</c> first, then ordinally.
    /// </summary>
    public string[] Visible(IEnumerable<string> stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return [.. stored.Where(Accepts).Distinct(StringComparer.Ordinal)
            .OrderBy(p => p == All ? 0 : 1)
            .ThenBy(p => p, StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_:-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionShape();
}
```

  Three details that the tests pin and that are easy to get wrong. The permission pattern
  ends in `\z`, not `$`: `$` also matches before a trailing newline, and
  `reports:read\n` must be malformed. The parser reads the document's *events* first and
  refuses anchors, aliases and a second document, because an alias is how a small file
  becomes a huge one. And it catches `InvalidOperationException` and `ArgumentException`
  beside `YamlException`: YamlDotNet's scanner throws the former for an unterminated flow
  sequence, and a broken manifest must never throw.

- [ ] **Step 5: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS — 64 new tests, 454 in all.

- [ ] **Step 6: Commit** — `feat(tenancy): read the manifest and the permission catalog`

### Task 2: The tables and the migration

**Files:**
- Create: `src/Auth.Infrastructure/Persistence/Company.cs`, `CompanyRole.cs`,
  `Membership.cs`, `Invite.cs`, `ActiveManifest.cs` (same folder),
  `src/Auth.Infrastructure/Persistence/Migrations/<id>_AddTenancy.cs` (+ `.Designer.cs`)
- Modify: `src/Auth.Infrastructure/Persistence/MailKind.cs`, `MailRequest.cs`,
  `AuthDbContext.cs`,
  `src/Auth.Infrastructure/Persistence/Migrations/AuthDbContextModelSnapshot.cs` (generated)
- Test: `tests/Auth.IntegrationTests/TenancyTablesTests.cs`

**Interfaces:**
- Produces: `Company` (`Guid Id`, `string Name`, `DateTimeOffset CreatedAt`);
  `CompanyRole` (`Id`, `CompanyId`, `Name`, `NormalizedName`, `string[] Permissions` as
  `text[]`, stored as given); `Membership` (key `UserId` + `CompanyId`, `RoleId`,
  `JoinedAt`); `Invite` (`Id`, `CompanyId`, `Email` as typed, `NormalizedEmail`, `RoleId`,
  `Guid? InvitedBy`, `InvitedAt`, `byte[]? TokenHash`, `ExpiresAt`); `ActiveManifest`
  (one row, `Id = 1`, `Content`, `StoredAt`).
- Produces: `MailKind.Invitation = 3`; `MailRequest.InviteId` (`Guid?`, **not** a foreign
  key: cancelling an invitation must not wait for a mail that is being sent).
- Produces: `AuthDbContext.Companies`, `.CompanyRoles`, `.Memberships`, `.Invites`,
  `.ActiveManifests`.
- The schema enforces what the code relies on: a role name is unique per company
  (`NormalizedName`); a membership or an invitation can only name a role **of its own
  company** (a composite foreign key `(CompanyId, RoleId)` to an alternate key
  `(CompanyId, Id)`), restricted on delete; at most one invitation per company and
  `NormalizedEmail`; `TokenHash` unique (nulls do not collide: an invitation has no token
  until its mail is composed); deleting a user sets `InvitedBy` to null. The primary key of
  a membership allows several companies per user, as the spec says; the code keeps it to
  one (Task 9).

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/TenancyTablesTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Auth.IntegrationTests;

public sealed class TenancyTablesTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    private async Task<(Guid Company, Guid Role)> CompanyWithRoleAsync(string name = "Acme", string role = "admin", string[]? permissions = null)
    {
        var company = new Company { Id = Guid.NewGuid(), Name = name, CreatedAt = Now() };
        var companyRole = new CompanyRole
        {
            CompanyId = company.Id,
            Name = role,
            NormalizedName = role.ToUpperInvariant(),
            Permissions = permissions ?? ["*"],
        };
        await InDbAsync(async db =>
        {
            db.Companies.Add(company);
            db.CompanyRoles.Add(companyRole);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });
        return (company.Id, companyRole.Id);
    }

    private DateTimeOffset Now() => StorableTime.Now(Clock);

    private Task<int> SaveAsync(Action<AuthDbContext> add) =>
        InDbAsync(async db =>
        {
            add(db);
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    [Fact]
    public async Task Company_role_and_membership_are_stored_and_read_back()
    {
        var (company, role) = await CompanyWithRoleAsync(permissions: ["reports:read", "*", "members:manage"]);
        var user = await Factory.SeedUserIdAsync();
        await SaveAsync(db => db.Memberships.Add(new Membership { UserId = user, CompanyId = company, RoleId = role, JoinedAt = Now() }));

        var read = await InDbAsync(db => db.CompanyRoles.AsNoTracking().SingleAsync(r => r.Id == role, TestContext.Current.CancellationToken));

        Assert.Equal(["reports:read", "*", "members:manage"], read.Permissions);   // as stored, in order
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.CompanyId == company, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Role_may_hold_no_permission_at_all()
    {
        var (_, role) = await CompanyWithRoleAsync(permissions: []);

        var read = await InDbAsync(db => db.CompanyRoles.AsNoTracking().SingleAsync(r => r.Id == role, TestContext.Current.CancellationToken));

        Assert.Empty(read.Permissions);
    }

    [Fact]
    public async Task Role_names_are_unique_within_a_company_whatever_their_case_and_free_in_another()
    {
        var (company, _) = await CompanyWithRoleAsync("Acme", "Admin");

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db => db.CompanyRoles.Add(
            new CompanyRole { CompanyId = company, Name = "ADMIN", NormalizedName = "ADMIN", Permissions = [] })));

        await CompanyWithRoleAsync("Globex", "Admin");   // another company may use the name
    }

    [Fact]
    public async Task Tables_allow_a_user_in_two_companies()   // spec 0005 → Concepts: switching companies later is additive
    {
        var (first, firstRole) = await CompanyWithRoleAsync("Acme");
        var (second, secondRole) = await CompanyWithRoleAsync("Globex");
        var user = await Factory.SeedUserIdAsync();

        await SaveAsync(db =>
        {
            db.Memberships.Add(new Membership { UserId = user, CompanyId = first, RoleId = firstRole, JoinedAt = Now() });
            db.Memberships.Add(new Membership { UserId = user, CompanyId = second, RoleId = secondRole, JoinedAt = Now() });
        });

        Assert.Equal(2, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Membership_cannot_name_a_role_of_another_company()   // criterion 14: no way across companies, down to the schema
    {
        var (first, _) = await CompanyWithRoleAsync("Acme");
        var (_, foreignRole) = await CompanyWithRoleAsync("Globex");
        var user = await Factory.SeedUserIdAsync();

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db => db.Memberships.Add(
            new Membership { UserId = user, CompanyId = first, RoleId = foreignRole, JoinedAt = Now() })));
    }

    [Fact]
    public async Task Invite_cannot_name_a_role_of_another_company()
    {
        var (first, _) = await CompanyWithRoleAsync("Acme");
        var (_, foreignRole) = await CompanyWithRoleAsync("Globex");

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db => db.Invites.Add(NewInvite(first, foreignRole, "a@example.com"))));
    }

    [Fact]
    public async Task Role_in_use_cannot_be_deleted_by_the_database()
    {
        var (company, role) = await CompanyWithRoleAsync();
        var user = await Factory.SeedUserIdAsync();
        await SaveAsync(db => db.Memberships.Add(new Membership { UserId = user, CompanyId = company, RoleId = role, JoinedAt = Now() }));

        var refused = await Assert.ThrowsAsync<PostgresException>(() => InDbAsync(db =>
            db.Database.ExecuteSqlAsync($"""DELETE FROM "CompanyRoles" WHERE "Id" = {role}""", TestContext.Current.CancellationToken)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);
    }

    private Invite NewInvite(Guid company, Guid role, string email, byte[]? tokenHash = null) => new()
    {
        CompanyId = company,
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        RoleId = role,
        InvitedAt = Now(),
        ExpiresAt = Now() + TimeSpan.FromDays(7),
        TokenHash = tokenHash,
    };

    [Fact]
    public async Task Invite_is_unique_per_company_and_address_but_not_across_companies()   // criterion 9
    {
        var (first, firstRole) = await CompanyWithRoleAsync("Acme");
        var (second, secondRole) = await CompanyWithRoleAsync("Globex");
        await SaveAsync(db => db.Invites.Add(NewInvite(first, firstRole, "a@example.com")));

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db => db.Invites.Add(NewInvite(first, firstRole, "a@example.com"))));

        await SaveAsync(db => db.Invites.Add(NewInvite(second, secondRole, "a@example.com")));
    }

    [Fact]
    public async Task Invites_without_a_token_do_not_collide_and_a_token_hash_is_unique()
    {
        var (company, role) = await CompanyWithRoleAsync();
        await SaveAsync(db =>
        {
            db.Invites.Add(NewInvite(company, role, "a@example.com"));
            db.Invites.Add(NewInvite(company, role, "b@example.com"));
        });
        var hash = new byte[32];
        hash[0] = 7;
        await SaveAsync(db => db.Invites.Add(NewInvite(company, role, "c@example.com", hash)));

        await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(db => db.Invites.Add(NewInvite(company, role, "d@example.com", hash))));
    }

    [Fact]
    public async Task Deleting_the_inviter_keeps_the_invitation()
    {
        var (company, role) = await CompanyWithRoleAsync();
        var inviter = await CreateUserAsync("inviter@example.com", confirmed: true);
        var invite = NewInvite(company, role, "a@example.com");
        var withInviter = new Invite
        {
            CompanyId = invite.CompanyId,
            Email = invite.Email,
            NormalizedEmail = invite.NormalizedEmail,
            RoleId = invite.RoleId,
            InvitedBy = inviter.Id,
            InvitedAt = invite.InvitedAt,
            ExpiresAt = invite.ExpiresAt,
        };
        await SaveAsync(db => db.Invites.Add(withInviter));

        await InDbAsync(db => db.Database.ExecuteSqlAsync($"""DELETE FROM "AspNetUsers" WHERE "Id" = {inviter.Id}""", TestContext.Current.CancellationToken));

        var kept = await InDbAsync(db => db.Invites.AsNoTracking().SingleAsync(i => i.Id == withInviter.Id, TestContext.Current.CancellationToken));
        Assert.Null(kept.InvitedBy);
    }

    [Fact]
    public async Task Queue_row_for_an_invitation_keeps_its_invite_and_may_outlive_it()
    {
        var invite = Guid.NewGuid();
        await SaveAsync(db => db.MailRequests.Add(new MailRequest
        {
            Kind = MailKind.Invitation,
            NormalizedEmail = "A@EXAMPLE.COM",
            InviteId = invite,
            RequestedAt = Now(),
            NextAttemptAt = Now(),
        }));

        var row = await InDbAsync(db => db.MailRequests.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));

        Assert.Equal(invite, row.InviteId);   // no foreign key: the invitation need not exist (cancelled) when the row is read
        Assert.Equal(3, (short)row.Kind);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`Company`, `CompanyRole`, `Membership`, `Invite`,
  `ActiveManifest`, `MailKind.Invitation` and `MailRequest.InviteId` do not exist).

- [ ] **Step 3: Add the entities and map them.**

`src/Auth.Infrastructure/Persistence/ActiveManifest.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>
/// The last valid manifest the service has read (spec 0005 → Manifest), kept so that a broken file never leaves the
/// service without one. There is one row, <see cref="TheOnlyId"/>.
/// </summary>
public sealed class ActiveManifest
{
    public const int TheOnlyId = 1;

    public int Id { get; init; } = TheOnlyId;

    /// <summary>The manifest in the form its class writes: the permissions and the default roles.</summary>
    public required string Content { get; set; }

    public required DateTimeOffset StoredAt { get; set; }
}
```

`src/Auth.Infrastructure/Persistence/AuthDbContext.cs` — the change:

```diff
--- a/src/Auth.Infrastructure/Persistence/AuthDbContext.cs
+++ b/src/Auth.Infrastructure/Persistence/AuthDbContext.cs
@@ -15,6 +15,16 @@
     public DbSet<MailRequest> MailRequests => Set<MailRequest>();
 
     public DbSet<MailRequestLimit> MailRequestLimits => Set<MailRequestLimit>();
+
+    public DbSet<Company> Companies => Set<Company>();
+
+    public DbSet<CompanyRole> CompanyRoles => Set<CompanyRole>();
+
+    public DbSet<Membership> Memberships => Set<Membership>();
+
+    public DbSet<Invite> Invites => Set<Invite>();
+
+    public DbSet<ActiveManifest> ActiveManifests => Set<ActiveManifest>();
 
     protected override void OnModelCreating(ModelBuilder builder)
     {
@@ -52,5 +62,61 @@
             // Pruning selects by age.
             limit.HasIndex(l => l.LastAcceptedAt);
         });
+
+        builder.Entity<Company>(company =>
+        {
+            company.HasKey(c => c.Id);
+            company.Property(c => c.Name).HasMaxLength(100);
+        });
+
+        builder.Entity<CompanyRole>(role =>
+        {
+            role.HasKey(r => r.Id);
+            role.Property(r => r.Name).HasMaxLength(100);
+            role.Property(r => r.NormalizedName).HasMaxLength(100);
+            role.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
+            // The key other tables point at, so that a membership or an invitation can only name a role of its own company.
+            role.HasAlternateKey(r => new { r.CompanyId, r.Id });
+            // Role names are unique within a company, whatever their case.
+            role.HasIndex(r => new { r.CompanyId, r.NormalizedName }).IsUnique();
+        });
+
+        builder.Entity<Membership>(membership =>
+        {
+            // The key allows several companies per user (spec 0005 → Concepts); the code lets a user have one.
+            membership.HasKey(m => new { m.UserId, m.CompanyId });
+            membership.HasOne<ApplicationUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
+            membership.HasOne<Company>().WithMany().HasForeignKey(m => m.CompanyId).OnDelete(DeleteBehavior.Cascade);
+            // A role is never deleted from under a member: the API refuses first, and the database agrees.
+            membership.HasOne<CompanyRole>().WithMany()
+                .HasForeignKey(m => new { m.CompanyId, m.RoleId })
+                .HasPrincipalKey(r => new { r.CompanyId, r.Id })
+                .OnDelete(DeleteBehavior.Restrict);
+            // The foreign key to the role gets an index on (CompanyId, RoleId): it serves the member list of a company
+            // and the count of members of a role.
+        });
+
+        builder.Entity<Invite>(invite =>
+        {
+            invite.HasKey(i => i.Id);
+            invite.HasOne<Company>().WithMany().HasForeignKey(i => i.CompanyId).OnDelete(DeleteBehavior.Cascade);
+            invite.HasOne<CompanyRole>().WithMany()
+                .HasForeignKey(i => new { i.CompanyId, i.RoleId })
+                .HasPrincipalKey(r => new { r.CompanyId, r.Id })
+                .OnDelete(DeleteBehavior.Restrict);
+            invite.HasOne<ApplicationUser>().WithMany().HasForeignKey(i => i.InvitedBy).OnDelete(DeleteBehavior.SetNull);
+            // At most one invitation per company and address; an expired one is replaced, not kept beside the new one.
+            invite.HasIndex(i => new { i.CompanyId, i.NormalizedEmail }).IsUnique();
+            // A link is looked up by its hash; rows without a token yet (null) do not collide.
+            invite.HasIndex(i => i.TokenHash).IsUnique();
+            // Pruning selects by expiry.
+            invite.HasIndex(i => i.ExpiresAt);
+        });
+
+        builder.Entity<ActiveManifest>(manifest =>
+        {
+            manifest.HasKey(m => m.Id);
+            manifest.Property(m => m.Id).ValueGeneratedNever();
+        });
     }
 }
```

`src/Auth.Infrastructure/Persistence/Company.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>A company of the instance (spec 0005 → Concepts): an id and a name. Created by the operator only.</summary>
public sealed class Company
{
    public Guid Id { get; init; }

    public required string Name { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }
}
```

`src/Auth.Infrastructure/Persistence/CompanyRole.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>
/// A role of one company: a name and a set of permissions from the catalog, or <c>*</c>, which stands for the whole
/// catalog. The permissions are stored as they were given; what they grant today is worked out against the catalog.
/// </summary>
public sealed class CompanyRole
{
    public Guid Id { get; init; }

    public required Guid CompanyId { get; init; }

    public required string Name { get; set; }

    /// <summary>The name in upper case: names are unique within a company regardless of case.</summary>
    public required string NormalizedName { get; set; }

    public required string[] Permissions { get; set; }
}
```

`src/Auth.Infrastructure/Persistence/Invite.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>
/// An invitation to join a company with a role (spec 0005 → Invitations). It names an address that may have no account
/// yet, so it cannot live in <see cref="EmailToken"/>, whose rows belong to users. The row exists from the moment the
/// invitation is made; its link token is issued, and its seven days start, when the mail is composed. Until then it has
/// no token, and an expiry set from the time it was made.
/// </summary>
public sealed class Invite
{
    public Guid Id { get; init; }

    public required Guid CompanyId { get; init; }

    /// <summary>The address as the inviter typed it: the account is created with it, and lists show it.</summary>
    public required string Email { get; init; }

    public required string NormalizedEmail { get; init; }

    public required Guid RoleId { get; init; }

    /// <summary>The member who sent it; <see langword="null"/> for the operator.</summary>
    public Guid? InvitedBy { get; init; }

    public required DateTimeOffset InvitedAt { get; init; }

    /// <summary>SHA-256 of the link token; <see langword="null"/> until the mail has been composed.</summary>
    public byte[]? TokenHash { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }
}
```

`src/Auth.Infrastructure/Persistence/MailKind.cs` — the change:

```diff
--- a/src/Auth.Infrastructure/Persistence/MailKind.cs
+++ b/src/Auth.Infrastructure/Persistence/MailKind.cs
@@ -5,4 +5,5 @@
 {
     PasswordReset = 1,
     EmailVerification = 2,
+    Invitation = 3,
 }
```

`src/Auth.Infrastructure/Persistence/MailRequest.cs` — the change:

```diff
--- a/src/Auth.Infrastructure/Persistence/MailRequest.cs
+++ b/src/Auth.Infrastructure/Persistence/MailRequest.cs
@@ -12,6 +12,13 @@
 
     public required string NormalizedEmail { get; init; }
 
+    /// <summary>
+    /// The invitation a <see cref="MailKind.Invitation"/> request is for; <see langword="null"/> for the other kinds.
+    /// Deliberately not a foreign key: cancelling an invitation must not wait for a mail that is being sent, so a
+    /// request whose invitation is gone is dropped by the dispatcher instead.
+    /// </summary>
+    public Guid? InviteId { get; init; }
+
     public required DateTimeOffset RequestedAt { get; init; }
 
     /// <summary>Failed sends so far.</summary>
```

`src/Auth.Infrastructure/Persistence/Membership.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>
/// A user's membership in a company, with exactly one role. The key allows a user to belong to several companies,
/// so that switching companies later is additive; this version lets a user belong to one, and the code keeps it so.
/// </summary>
public sealed class Membership
{
    public required Guid UserId { get; init; }

    public required Guid CompanyId { get; init; }

    public required Guid RoleId { get; set; }

    public required DateTimeOffset JoinedAt { get; init; }
}
```

- [ ] **Step 4: Generate the migration and give it its id.**

```bash
dotnet tool restore
dotnet ef migrations add AddTenancy -p src/Auth.Infrastructure -s src/Auth.Server
```

  Expected: two new files in `src/Auth.Infrastructure/Persistence/Migrations/`
  (`<stamp>_AddTenancy.cs`, `<stamp>_AddTenancy.Designer.cs`) and a changed
  `AuthDbContextModelSnapshot.cs`. `Up` adds one column and creates five tables, and
  nothing else: `MailRequests.InviteId` (`uuid`, nullable); `ActiveManifests` (`Id integer`
  primary key without identity, `Content text`, `StoredAt`); `Companies` (`Id uuid`,
  `Name varchar(100)`, `CreatedAt`); `CompanyRoles` (`Id uuid`, `CompanyId`,
  `Name varchar(100)`, `NormalizedName varchar(100)`, `Permissions text[]`; the unique
  constraint `AK_CompanyRoles_CompanyId_Id`, a cascading foreign key to `Companies`, the
  unique index `IX_CompanyRoles_CompanyId_NormalizedName`); `Invites` (`Id uuid`,
  `CompanyId`, `Email text`, `NormalizedEmail text`, `RoleId`, `InvitedBy uuid` nullable,
  `InvitedAt`, `TokenHash bytea` nullable, `ExpiresAt`; foreign keys to `AspNetUsers`
  (set null), `Companies` (cascade) and `CompanyRoles` on `(CompanyId, RoleId)`
  (restrict); the unique indexes `IX_Invites_CompanyId_NormalizedEmail` and
  `IX_Invites_TokenHash`, and `IX_Invites_ExpiresAt`); `Memberships` (primary key
  `UserId` + `CompanyId`, `RoleId`, `JoinedAt`; foreign keys to `AspNetUsers` and
  `Companies` (cascade) and `CompanyRoles` on `(CompanyId, RoleId)` (restrict), with the
  index `IX_Memberships_CompanyId_RoleId`). `Down` drops the five tables and the column.
  It must touch **no existing table but `MailRequests`**; if it does, stop and report.

  `dotnet ef` stamps the id from the machine's clock. The id kept in the repository is the
  one **the orchestrator supplies in the task brief**: 14 digits, `yyyyMMddHHmmss`, greater
  than the newest id in `src/Auth.Infrastructure/Persistence/Migrations/` (a plain `ls` shows
  it). Apply it before anything else is built:

  1. Rename both generated files to `<id>_AddTenancy.cs` and `<id>_AddTenancy.Designer.cs`
     (plain `mv`; they are not tracked yet).
  2. In the `.Designer.cs` file change the attribute to `[Migration("<id>_AddTenancy")]`.
  3. Leave `AuthDbContextModelSnapshot.cs` as generated; it holds no id.
  4. Check: `git grep --untracked -n "_AddTenancy" -- src` (the files are untracked, so
     plain `git grep` would not see them) prints exactly one line, the
     `[Migration("<id>_AddTenancy")]` attribute with the given id;
     `ls src/Auth.Infrastructure/Persistence/Migrations` shows the two files under that id;
     and `dotnet ef migrations has-pending-model-changes -p src/Auth.Infrastructure -s src/Auth.Server`
     reports no pending change.

- [ ] **Step 5: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS — 11 new tests, 465 in all. The host migrates at startup in every test, so
  a model that differs from the migration would fail every one of them.

- [ ] **Step 6: Commit** — `feat(tenancy): add the tenancy tables and the migration`

### Task 3: Which manifest is active

**Files:**
- Create: `deploy/auth.yaml`, `src/Auth.Server/Tenancy/ManifestSettings.cs`,
  `ManifestHolder.cs`, `ManifestActivator.cs`, `ManifestHealthCheck.cs`,
  `TenancyServices.cs` (same folder)
- Modify: `src/Auth.Server/Program.cs`, `src/Auth.Server/appsettings.Development.json`,
  `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`
- Test: `tests/Auth.IntegrationTests/ManifestActivationTests.cs`

**Interfaces:**
- Consumes: Task 1 (`Manifest`, `ManifestParser`), Task 2 (`ActiveManifest`).
- Produces: setting `Auth:Manifest:Path` (`ManifestSettings.PathKey`), **required**; a
  relative path is taken from the content root. `ManifestHolder` (singleton) with `State`
  (`ManifestState(Manifest, string? DegradedReason)`), `Current` and `Set(manifest,
  reason)`; until something is set the built-in manifest applies. `ManifestActivator`
  (singleton) with `ActivateAsync(ct)`: the file if valid (then stored with one upsert
  statement), otherwise the last valid one stored, otherwise the built-in one; the reason
  is logged at `Error` and is the holder's `DegradedReason`. `ManifestHealthCheck` is
  `Degraded` exactly while a reason is set (`/auth/health` is then `200` with the body
  `Degraded`). `AddTenancy(configuration, contentRoot)` registers the manifest services;
  later tasks add theirs to it.
- Produces (tests): every `AuthAppFactory` host gets a manifest file of its own with the
  spec's example, `WithManifest(yaml)` rewrites it, `WithoutManifestFile()` deletes it,
  `ManifestPath` and `DefaultManifest` expose it.

- [ ] **Step 1: Write the failing tests.** The factory change comes first: the tests of
  this task and every later one lean on it.

`tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs
+++ b/tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs
@@ -1,6 +1,7 @@
 using Auth.Infrastructure.Identity;
 using Auth.Server.Email;
 using Auth.Server.Seeding;
+using Auth.Server.Tenancy;
 using TokenOptions = Auth.Server.Tokens.TokenOptions;
 using Microsoft.AspNetCore.Hosting;
 using Microsoft.AspNetCore.Identity;
@@ -19,6 +20,7 @@
     private string? _environment;
     private TimeProvider? _clock;
     private readonly List<Action<IServiceCollection>> _serviceOverrides = [];
+    private readonly string _manifestDirectory;
 
     /// <param name="postgres">The shared Postgres server.</param>
     /// <param name="keys">Throwaway signing/encryption key material the host loads from disk.</param>
@@ -60,12 +62,26 @@
         _settings[MailSettingsLoader.SmtpPasswordKey] = "";
         _settings[DevUserSeeder.UnverifiedEmailKey] = "";
         _settings[DevUserSeeder.UnverifiedPasswordKey] = "";
+        // The manifest: a file of this host's own, so that a test changes it without touching another's.
+        _manifestDirectory = Directory.CreateDirectory(
+            Path.Combine(Path.GetTempPath(), "auth-core-manifest-" + Guid.NewGuid().ToString("N"))).FullName;
+        ManifestPath = Path.Combine(_manifestDirectory, "auth.yaml");
+        File.WriteAllText(ManifestPath, DefaultManifest);
+        _settings[ManifestSettings.PathKey] = ManifestPath;
     }
 
     /// <summary>Default development seed credentials; the password satisfies Identity's default policy.</summary>
     public const string DefaultSeedEmail = "user@example.com";
 
     public const string DefaultSeedPassword = "Correct-Horse-Battery-1";
+
+    /// <summary>The manifest every test host starts with: the example of spec 0005.</summary>
+    public const string DefaultManifest = """
+        permissions: [reports:read, reports:approve, templates:manage]
+        default_roles:
+          admin: ["*"]
+          user: [reports:read, reports:approve]
+        """;
 
     public const string DefaultAppName = "Auth-Core Test";
     public const string DefaultResetUrl = "https://app.example.com/reset";
@@ -89,6 +105,23 @@
     }
 
     public string DatabaseName { get; }
+
+    /// <summary>The manifest file of this host.</summary>
+    public string ManifestPath { get; }
+
+    /// <summary>Replaces the content of the manifest file; it is read when the host starts.</summary>
+    public AuthAppFactory WithManifest(string yaml)
+    {
+        File.WriteAllText(ManifestPath, yaml);
+        return this;
+    }
+
+    /// <summary>Removes the manifest file, as if the product had not shipped one.</summary>
+    public AuthAppFactory WithoutManifestFile()
+    {
+        File.Delete(ManifestPath);
+        return this;
+    }
 
     public AuthAppFactory WithSetting(string key, string? value)
     {
@@ -145,6 +178,15 @@
         // minutes after the test. Without this the suite runs out of server connections as it grows.
         await using var connection = new NpgsqlConnection(_settings["ConnectionStrings:Auth"]);
         NpgsqlConnection.ClearPool(connection);
+
+        try
+        {
+            Directory.Delete(_manifestDirectory, recursive: true);
+        }
+        catch (IOException)
+        {
+            // A temporary directory that cannot be removed is not a failure of the test.
+        }
     }
 
     protected override void ConfigureWebHost(IWebHostBuilder builder)
```

`tests/Auth.IntegrationTests/ManifestActivationTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

public sealed class ManifestActivationTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string Broken = "default_roles:\n  user: [reports:write]\n";

    private const string Other = """
        permissions: [orders:read]
        default_roles:
          owner: ["*"]
        """;

    private static string UniqueDatabase() => "auth_" + Guid.NewGuid().ToString("N");

    private static ManifestState StateOf(AuthAppFactory factory) =>
        factory.Services.GetRequiredService<ManifestHolder>().State;

    private static async Task<string> HealthAsync(AuthAppFactory factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/auth/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);   // Degraded is still a 200
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<ActiveManifest?> StoredAsync(AuthAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().ActiveManifests.AsNoTracking()
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Valid_manifest_becomes_active_and_is_stored()   // criterion 20
    {
        await using var factory = new AuthAppFactory(postgres, keys);

        var state = StateOf(factory);

        Assert.False(state.IsDegraded);
        Assert.Equal(["reports:read", "reports:approve", "templates:manage"], state.Manifest.Permissions);
        Assert.Equal("Healthy", await HealthAsync(factory));
        var stored = Assert.IsType<ActiveManifest>(await StoredAsync(factory));
        Assert.Equal(state.Manifest.ToJson(), stored.Content);
    }

    [Fact]
    public async Task A_new_valid_manifest_replaces_the_stored_one_at_the_next_start()   // criterion 21
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        _ = StateOf(first);

        await using var second = new AuthAppFactory(postgres, keys, database).WithManifest(Other);
        var state = StateOf(second);

        Assert.False(state.IsDegraded);
        Assert.Equal(["orders:read"], state.Manifest.Permissions);
        Assert.Equal(state.Manifest.ToJson(), (await StoredAsync(second))!.Content);
        using var scope = second.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AuthDbContext>().ActiveManifests.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Invalid_manifest_leaves_the_last_stored_one_active_and_says_why()   // criterion 20
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        var good = StateOf(first).Manifest;
        var logs = new CapturingLoggerProvider();

        await using var second = new AuthAppFactory(postgres, keys, database)
            .WithManifest(Broken)
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));
        var state = StateOf(second);   // the host starts

        Assert.True(state.IsDegraded);
        Assert.Equal(good.ToJson(), state.Manifest.ToJson());
        Assert.Equal("Degraded", await HealthAsync(second));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("reports:write") && e.Message.Contains("last valid stored"));
        Assert.Equal(good.ToJson(), (await StoredAsync(second))!.Content);   // the broken file is not stored
    }

    [Fact]
    public async Task Missing_manifest_file_leaves_the_last_stored_one_active()   // criterion 20
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        var good = StateOf(first).Manifest;

        await using var second = new AuthAppFactory(postgres, keys, database).WithoutManifestFile();
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.Contains("does not exist", state.DegradedReason);
        Assert.Equal(good.ToJson(), state.Manifest.ToJson());
        Assert.Equal("Degraded", await HealthAsync(second));
    }

    [Fact]
    public async Task Unreadable_manifest_path_leaves_the_last_stored_one_active()   // criterion 20
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        var good = StateOf(first).Manifest;

        // A directory where the file should be: nothing can be read from it.
        await using var second = new AuthAppFactory(postgres, keys, database).WithSetting(ManifestSettings.PathKey, Path.GetTempPath());
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.Equal(good.ToJson(), state.Manifest.ToJson());
    }

    [Fact]
    public async Task Manifest_file_over_the_size_limit_is_not_read()
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + "\n# " + new string('x', ManifestParser.MaxBytes));

        var state = StateOf(factory);

        Assert.True(state.IsDegraded);
        Assert.Contains("larger than", state.DegradedReason);
    }

    [Fact]
    public async Task A_manifest_with_more_than_100_default_roles_leaves_the_last_stored_one_active()   // criterion 20
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        var good = StateOf(first).Manifest;
        var tooMany = "default_roles:\n  admin: [\"*\"]\n" + string.Concat(Enumerable.Range(1, 100).Select(i => $"  r{i}: []\n"));   // 101 roles

        await using var second = new AuthAppFactory(postgres, keys, database).WithManifest(tooMany);
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.Contains("100", state.DegradedReason);
        Assert.Equal(good.ToJson(), state.Manifest.ToJson());
    }

    [Fact]
    public async Task With_nothing_ever_stored_the_catalog_is_the_built_in_one_and_the_only_default_role_is_admin()   // criterion 20
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithManifest(Broken);

        var state = StateOf(factory);

        Assert.True(state.IsDegraded);
        Assert.Equal(PermissionCatalog.BuiltIn, state.Manifest.Catalog.Permissions);
        var role = Assert.Single(state.Manifest.DefaultRoles);
        Assert.Equal("admin", role.Name);
        Assert.Equal(["*"], role.Permissions);
        Assert.Equal("Degraded", await HealthAsync(factory));
        Assert.Null(await StoredAsync(factory));
    }

    [Fact]
    public async Task A_stored_manifest_that_cannot_be_read_back_is_replaced_by_the_built_in_one()
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        _ = StateOf(first);
        using (var scope = first.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database
                .ExecuteSqlAsync($"""UPDATE "ActiveManifests" SET "Content" = 'not json'""", TestContext.Current.CancellationToken);
        }

        await using var second = new AuthAppFactory(postgres, keys, database).WithManifest(Broken);
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.Equal(Manifest.BuiltIn.ToJson(), state.Manifest.ToJson());
    }

    [Fact]
    public async Task Host_refuses_to_start_without_a_manifest_path()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSetting(ManifestSettings.PathKey, "");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(ManifestSettings.PathKey, ex.ToString());
    }

    [Fact]
    public async Task Shipped_development_manifest_is_valid()
    {
        // The path development uses: relative to the content root of the server project.
        await using var factory = new AuthAppFactory(postgres, keys).WithSetting(ManifestSettings.PathKey, "../../deploy/auth.yaml");

        var state = StateOf(factory);

        Assert.False(state.IsDegraded, state.DegradedReason);
        Assert.Equal(["documents:read", "documents:write", "documents:approve"], state.Manifest.Permissions);
        Assert.Equal(["admin", "user"], state.Manifest.DefaultRoles.Select(r => r.Name));
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`ManifestHolder`, `ManifestSettings` do not exist).

- [ ] **Step 3: Implement.** `deploy/auth.yaml` is the development manifest: it is what a
  developer's `dotnet run` and the compose stack read (Task 15 mounts it), and a test
  proves that it is valid.

`deploy/auth.yaml`:

```yaml
# The development manifest: what the product declares to Auth-Core (spec 0005 -> Manifest).
#
# permissions    the permissions the product's code checks. Three more are built in and always in the catalog:
#                members:manage, roles:manage and org:manage (they guard the company API itself).
# default_roles  the roles every new company starts with a copy of. After that its roles are its own.
#                "*" stands for every permission of the catalog. At least one default role must hold
#                members:manage or "*", so that the first admin of a company can manage it.
#
# A broken file never stops the service: the last valid manifest stays active and /auth/health says "Degraded".
permissions: [documents:read, documents:write, documents:approve]
default_roles:
  admin: ["*"]
  user: [documents:read, documents:write]
```

`src/Auth.Server/Program.cs` — the change:

```diff
--- a/src/Auth.Server/Program.cs
+++ b/src/Auth.Server/Program.cs
@@ -7,6 +7,7 @@
 using Auth.Server.Login;
 using Auth.Server.Seeding;
 using Auth.Server.Sessions;
+using Auth.Server.Tenancy;
 using Auth.Server.Tokens;
 using Microsoft.AspNetCore.Identity;
 using Microsoft.EntityFrameworkCore;
@@ -19,7 +20,8 @@
 builder.Services.AddSingleton(keys);
 // Fail fast on missing or invalid mail settings too.
 builder.Services.AddSingleton(MailSettingsLoader.Load(builder.Configuration, builder.Environment.IsDevelopment()));
-builder.Services.AddHealthChecks();
+builder.Services.AddHealthChecks().AddCheck<ManifestHealthCheck>("manifest");
+builder.Services.AddTenancy(builder.Configuration, builder.Environment.ContentRootPath);
 builder.Services.AddAuthPersistence(builder.Configuration);
 // OpenIddict takes its clock from DI; tests replace this registration to move time.
 builder.Services.TryAddSingleton(TimeProvider.System);
@@ -50,6 +52,9 @@
     await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.MigrateAsync();
 }
 
+// Which manifest is active: the product's file, or the last valid one. Never a reason to stop.
+await app.Services.GetRequiredService<ManifestActivator>().ActivateAsync(app.Lifetime.ApplicationStopping);
+
 await DevUserSeeder.SeedAsync(app.Services, app.Lifetime.ApplicationStopping);
 
 app.MapHealthChecks("/auth/health");
```

`src/Auth.Server/Tenancy/ManifestActivator.cs`:

```csharp
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// Decides which manifest is active (spec 0005 → Manifest): the product's file if it is valid, and then it is stored in
/// the database; otherwise the last valid manifest stored, and with none ever stored the built-in one. A broken file
/// never stops the service: the reason is logged and the health check reports <c>Degraded</c>.
/// </summary>
public sealed partial class ManifestActivator(
    ManifestSettings settings, ManifestHolder holder, IServiceScopeFactory scopes, TimeProvider clock, ILogger<ManifestActivator> logger)
{
    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        var read = await ReadFileAsync(cancellationToken);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        if (read.Manifest is { } manifest)
        {
            await StoreAsync(db, manifest, cancellationToken);
            holder.Set(manifest, degradedReason: null);
            return;
        }

        var reason = read.Error ?? "the manifest is not valid";
        var stored = await db.ActiveManifests.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var fallback = stored is null ? null : Manifest.FromJson(stored.Content);
        LogNotUsed(logger, settings.Path, reason, fallback is null ? "built-in" : "last valid stored");
        holder.Set(fallback ?? Manifest.BuiltIn, reason);
    }

    private async Task<ManifestParseResult> ReadFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(settings.Path);
            if (!file.Exists)
            {
                return new ManifestParseResult(null, "the file does not exist");
            }

            if (file.Length > ManifestParser.MaxBytes)
            {
                return new ManifestParseResult(null, $"the file is larger than {ManifestParser.MaxBytes} bytes");
            }

            return ManifestParser.Parse(await File.ReadAllTextAsync(settings.Path, Encoding.UTF8, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ManifestParseResult(null, $"the file cannot be read ({exception.GetType().Name})");
        }
    }

    // One statement, so replicas starting together leave one row.
    private async Task StoreAsync(AuthDbContext db, Manifest manifest, CancellationToken cancellationToken)
    {
        var id = ActiveManifest.TheOnlyId;
        var content = manifest.ToJson();
        var now = StorableTime.Now(clock);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "ActiveManifests" ("Id", "Content", "StoredAt")
            VALUES ({id}, {content}, {now})
            ON CONFLICT ("Id") DO UPDATE SET "Content" = EXCLUDED."Content", "StoredAt" = EXCLUDED."StoredAt"
            """,
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The manifest {Path} was not used: {Reason}. The {Fallback} manifest stays active.")]
    private static partial void LogNotUsed(ILogger logger, string path, string reason, string fallback);
}
```

`src/Auth.Server/Tenancy/ManifestHealthCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Auth.Server.Tenancy;

/// <summary>
/// <c>Degraded</c> while the active manifest is not the product's file (spec 0005 → Manifest). The response of
/// <c>/auth/health</c> is still <c>200</c>: the service works, and the reason is in the log, not in a public response.
/// </summary>
public sealed class ManifestHealthCheck(ManifestHolder holder) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(holder.State.IsDegraded
            ? HealthCheckResult.Degraded("The manifest file was not used.")
            : HealthCheckResult.Healthy());
}
```

`src/Auth.Server/Tenancy/ManifestHolder.cs`:

```csharp
namespace Auth.Server.Tenancy;

/// <summary>The active manifest, and why it is not the one the product's file describes, when it is not.</summary>
public sealed record ManifestState(Manifest Manifest, string? DegradedReason)
{
    public bool IsDegraded => DegradedReason is not null;
}

/// <summary>
/// Holds the active manifest of this process (spec 0005 → Manifest). It is set once, at startup, before the host
/// serves anything, and read on every login, refresh and company API call: the catalog is the manifest's, so a change
/// to the file takes effect with a restart. Until something is set the built-in manifest applies.
/// </summary>
public sealed class ManifestHolder
{
    private volatile ManifestState _state = new(Manifest.BuiltIn, null);

    public ManifestState State => _state;

    public Manifest Current => _state.Manifest;

    /// <param name="degradedReason">Why the file was not used; <see langword="null"/> when it was.</param>
    public void Set(Manifest manifest, string? degradedReason)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        _state = new ManifestState(manifest, degradedReason);
    }
}
```

`src/Auth.Server/Tenancy/ManifestSettings.cs`:

```csharp
namespace Auth.Server.Tenancy;

/// <summary>Where the product's manifest is. The path is configuration; what the file says is the product's.</summary>
public sealed record ManifestSettings(string Path)
{
    public const string PathKey = "Auth:Manifest:Path";

    /// <summary>
    /// A relative path is taken from the application's content root. The path itself is required like every other
    /// setting; whether the file is good is not checked here, because a broken manifest never stops the service.
    /// </summary>
    /// <exception cref="InvalidOperationException">The path is missing or blank.</exception>
    public static ManifestSettings Load(IConfiguration configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(contentRoot);

        var path = configuration[PathKey];
        if (string.IsNullOrWhiteSpace(path))
        {
            // Name the key only.
            throw new InvalidOperationException($"Configuration value '{PathKey}' is missing or blank.");
        }

        return new ManifestSettings(System.IO.Path.GetFullPath(path, contentRoot));
    }
}
```

`src/Auth.Server/Tenancy/TenancyServices.cs`:

```csharp
namespace Auth.Server.Tenancy;

public static class TenancyServices
{
    /// <summary>
    /// The registrations shared by the host and the operator commands: what decides which manifest is active, and the
    /// services that work on companies, members and invitations.
    /// </summary>
    public static IServiceCollection AddTenancy(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(ManifestSettings.Load(configuration, contentRoot));
        services.AddSingleton<ManifestHolder>();
        services.AddSingleton<ManifestActivator>();
        return services;
    }
}
```

`src/Auth.Server/appsettings.Development.json` — the change:

```diff
--- a/src/Auth.Server/appsettings.Development.json
+++ b/src/Auth.Server/appsettings.Development.json
@@ -5,6 +5,9 @@
   "Auth": {
     "Database": {
       "MigrateOnStartup": true
+    },
+    "Manifest": {
+      "Path": "../../deploy/auth.yaml"
     },
     "App": {
       "Name": "auth-core-dev",
```

  `Program.cs` activates the manifest after the migration and before the seeder, which
  needs the default roles (Task 4). The path comes from `Auth:Manifest:Path`; the
  development setting is relative to the content root of the server project.

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`,
  then `dotnet format --verify-no-changes`. Expected: PASS — 11 new tests, 476 in all.
  This is the end of Day 1.

- [ ] **Step 5: Commit** — `feat(tenancy): keep the last valid manifest active and report Degraded`

---

## Day 2 — companies, claims, bearer validation, `/auth/me` and `/auth/org`

When the day is done a user without a company cannot log in, a user with one
gets `org_id`, `roles` and `permissions` in the access token, and the first endpoints of the
company API work behind the service's own bearer tokens with permission checks against
the database.

### Task 4: Creating companies, and the development company

**Files:**
- Create: `src/Auth.Server/Tenancy/Outcome.cs`, `src/Auth.Server/Tenancy/CompanyService.cs`
- Modify: `src/Auth.Server/Seeding/DevUserSeeder.cs`, `src/Auth.Server/Tenancy/TenancyServices.cs`
- Test: `tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs`,
  `tests/Auth.IntegrationTests/CompanyServiceTests.cs`,
  `tests/Auth.IntegrationTests/DevCompanySeedTests.cs`
- Modify (test of Task 2): `tests/Auth.IntegrationTests/TenancyTablesTests.cs`

This task comes before the claims on purpose: from Task 5 on a user without a company is
refused at login, and every existing test logs in as the development seed user, so the
seed user must be a member by then. For the same reason `TenancyTablesTests.Tables_allow_a_user_in_two_companies`
(Task 2), which put the seed user into two companies, now makes an account of its own with
`CreateUserAsync(email, confirmed: true)`: the seed user is a member from this task on, and
the test would count three memberships. (Task 5 adds the `member: false` argument to that
call when it gives `CreateUserAsync` the parameter.)

**Interfaces:**
- Produces: `TenancyErrors` (the error codes of the contract as constants, and
  `StatusOf(code)`, the status each one is answered with); `Outcome(string? Error,
  TimeSpan RetryAfter)` with `Done`, `Succeeded`, `Fail(code)`, `Limited(retryAfter)`
  (the `429` of the mail limit) and `Outcome.Ok<T>(value)` / `Outcome.Fail<T>(code)`
  making an `Outcome<T>(T? Value, string? Error, TimeSpan RetryAfter)`. The API turns a
  refusal into a response and the CLI prints it, so a refusal means the same on both.
- Produces: `CompanyService` (scoped) with
  `Task<Outcome<Guid>> CreateAsync(string name, CancellationToken)`: validates the name
  (`invalid_request`), creates the company with **its own copy** of the active default
  roles (a role that lists `*` is `*` alone, as the parser made it), one `SaveChanges`.
- Produces: `DevUserSeeder.OrgNameKey` (`Auth:DevSeed:OrgName`) and `DefaultOrgName`
  (`Development`). In Development, when a seed user is configured, the seeder makes the
  company (once; found again by name on a restart), puts the first seed user into it as
  its `admin` (else the first default role, in the order of the manifest, that manages
  members), and the second (unconfirmed) as a member of the first default role, **in the
  order of the manifest**, that does not manage members, or else of a role called `member`,
  made empty when the company has none. It never moves an existing member to another company; a
  seed user of a database made before this slice joins at the next start.
- Produces (tests): `TenancyTestBase : MailTestBase` with `Holder`, `CreateCompanyAsync`,
  `DevCompanyIdAsync`, `RoleIdAsync`, `AddMemberAsync`.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/CompanyServiceTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class CompanyServiceTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private async Task<List<(string Name, string Permissions)>> RolesOfAsync(Guid company) =>
        (await InDbAsync(db => db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == company).OrderBy(r => r.Name).ToListAsync(TestContext.Current.CancellationToken)))
            .Select(r => (r.Name, string.Join(',', r.Permissions)))
            .ToList();

    [Fact]
    public async Task Company_starts_with_a_copy_of_the_active_default_roles()   // criterion 1
    {
        var company = await CreateCompanyAsync("Acme");

        Assert.Equal([("admin", "*"), ("user", "reports:read,reports:approve")], await RolesOfAsync(company));
        var row = await InDbAsync(db => db.Companies.AsNoTracking().SingleAsync(c => c.Id == company, TestContext.Current.CancellationToken));
        Assert.Equal("Acme", row.Name);
    }

    [Fact]
    public async Task Later_changes_to_the_defaults_do_not_touch_an_existing_company()
    {
        var first = await CreateCompanyAsync("Acme");

        Holder.Set(ManifestParser.Parse("permissions: [orders:read]\ndefault_roles:\n  owner: [\"*\"]\n").Manifest!, null);
        var second = await CreateCompanyAsync("Globex");

        Assert.Equal([("admin", "*"), ("user", "reports:read,reports:approve")], await RolesOfAsync(first));
        Assert.Equal([("owner", "*")], await RolesOfAsync(second));
    }

    [Fact]
    public async Task A_default_role_that_lists_star_is_copied_as_star_alone()
    {
        Holder.Set(ManifestParser.Parse("permissions: [orders:read]\ndefault_roles:\n  owner: [orders:read, \"*\"]\n").Manifest!, null);

        var company = await CreateCompanyAsync("Acme");

        Assert.Equal([("owner", "*")], await RolesOfAsync(company));
    }

    [Fact]
    public async Task Companies_do_not_share_roles()
    {
        var first = await CreateCompanyAsync("Acme");
        var second = await CreateCompanyAsync("Globex");

        Assert.NotEqual(await RoleIdAsync(first, "admin"), await RoleIdAsync(second, "admin"));
    }

    [Fact]
    public async Task With_the_built_in_manifest_a_company_gets_one_admin_role_holding_everything()   // criterion 20
    {
        Holder.Set(Manifest.BuiltIn, "test");

        var company = await CreateCompanyAsync("Acme");

        Assert.Equal([("admin", "*")], await RolesOfAsync(company));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Acme")]
    [InlineData("Acme ")]
    [InlineData("Ac\tme")]
    public async Task A_name_that_breaks_the_rules_creates_nothing(string name)
    {
        using var scope = Factory.Services.CreateScope();
        var before = await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken));

        var created = await scope.ServiceProvider.GetRequiredService<CompanyService>().CreateAsync(name, TestContext.Current.CancellationToken);

        Assert.False(created.Succeeded);
        Assert.Equal("invalid_request", created.Error);
        Assert.Equal(before, await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));
    }
}
```

`tests/Auth.IntegrationTests/DevCompanySeedTests.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class DevCompanySeedTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string UnverifiedEmail = "new@example.com";
    private const string UnverifiedPassword = "Dev-Unverified-Passw0rd";

    private static string UniqueDatabase() => "auth_" + Guid.NewGuid().ToString("N");

    private AuthAppFactory Factory(string? database = null) => new(postgres, keys, database);

    private static AuthAppFactory WithSecondUser(AuthAppFactory factory) => factory
        .WithSetting(DevUserSeeder.UnverifiedEmailKey, UnverifiedEmail)
        .WithSetting(DevUserSeeder.UnverifiedPasswordKey, UnverifiedPassword);

    private static async Task<T> InDbAsync<T>(AuthAppFactory factory, Func<AuthDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private static async Task<(string Company, string Role)?> MembershipOfAsync(AuthAppFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            return null;
        }

        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var row = await (
            from m in db.Memberships
            join c in db.Companies on m.CompanyId equals c.Id
            join r in db.CompanyRoles on m.RoleId equals r.Id
            where m.UserId == user.Id
            select new { c.Name, RoleName = r.Name })
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
        return row is null ? null : (row.Name, row.RoleName);
    }

    [Fact]
    public async Task First_seed_user_is_the_admin_of_the_development_company()
    {
        await using var factory = Factory();
        _ = factory.Services;

        Assert.Equal(("Development", "admin"), await MembershipOfAsync(factory, factory.SeedEmail));
        var roles = await InDbAsync(factory, db => db.CompanyRoles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["admin", "user"], roles.Select(r => r.Name));   // a copy of the default roles
    }

    [Fact]
    public async Task Company_name_is_a_setting()
    {
        await using var factory = Factory().WithSetting(DevUserSeeder.OrgNameKey, "Acme Dev");
        _ = factory.Services;

        Assert.Equal(("Acme Dev", "admin"), await MembershipOfAsync(factory, factory.SeedEmail));
    }

    [Fact]
    public async Task Second_seed_user_is_a_member_with_a_role_that_does_not_manage_members()
    {
        await using var factory = WithSecondUser(Factory());
        _ = factory.Services;

        Assert.Equal(("Development", "user"), await MembershipOfAsync(factory, UnverifiedEmail));
        using var scope = factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(UnverifiedEmail);
        Assert.False(user!.EmailConfirmed);   // it stays unconfirmed: the verification flow needs one
    }

    [Fact]
    public async Task When_every_default_role_manages_members_the_second_user_gets_an_empty_role_of_its_own()
    {
        await using var factory = WithSecondUser(Factory()).WithManifest("default_roles:\n  owner: [\"*\"]\n");
        _ = factory.Services;

        Assert.Equal(("Development", "member"), await MembershipOfAsync(factory, UnverifiedEmail));
        var member = await InDbAsync(factory, db => db.CompanyRoles.AsNoTracking().SingleAsync(r => r.Name == "member", TestContext.Current.CancellationToken));
        Assert.Empty(member.Permissions);
        Assert.Equal(("Development", "owner"), await MembershipOfAsync(factory, factory.SeedEmail));   // the first user takes a role that manages
    }

    [Fact]
    public async Task Second_seed_user_gets_the_first_default_role_in_the_order_of_the_manifest_that_does_not_manage_members()
    {
        await using var factory = WithSecondUser(Factory())
            .WithManifest("permissions: [a:b]\ndefault_roles:\n  owner: [\"*\"]\n  zeta: [a:b]\n  alpha: [a:b]\n");
        _ = factory.Services;

        // Not `alpha`, which comes first by name, and not a role called `user`: the order of the file decides.
        Assert.Equal(("Development", "zeta"), await MembershipOfAsync(factory, UnverifiedEmail));
        Assert.Equal(("Development", "owner"), await MembershipOfAsync(factory, factory.SeedEmail));
    }

    [Fact]
    public async Task A_restart_changes_nothing()
    {
        var database = UniqueDatabase();
        await using var first = WithSecondUser(Factory(database));
        _ = first.Services;
        await using var second = WithSecondUser(Factory(database));
        _ = second.Services;

        Assert.Equal(1, await InDbAsync(second, db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(2, await InDbAsync(second, db => db.CompanyRoles.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(2, await InDbAsync(second, db => db.Memberships.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_seed_user_without_a_membership_joins_at_the_next_start()   // development databases made before spec 0005
    {
        var database = UniqueDatabase();
        await using var first = Factory(database);
        _ = first.Services;
        await InDbAsync(first, db => db.Database.ExecuteSqlAsync($"""DELETE FROM "Memberships" """, TestContext.Current.CancellationToken));

        await using var second = Factory(database);
        _ = second.Services;

        Assert.Equal(("Development", "admin"), await MembershipOfAsync(second, second.SeedEmail));
    }

    [Fact]
    public async Task A_member_is_not_moved_to_another_company_by_a_new_setting()
    {
        var database = UniqueDatabase();
        await using var first = Factory(database);
        _ = first.Services;

        await using var second = Factory(database).WithSetting(DevUserSeeder.OrgNameKey, "Elsewhere");
        _ = second.Services;

        Assert.Equal(("Development", "admin"), await MembershipOfAsync(second, second.SeedEmail));
        Assert.Equal(2, await InDbAsync(second, db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Production_host_creates_no_company()
    {
        await using var factory = Factory().WithEnvironment("Production");
        _ = factory.Services;

        Assert.Equal(0, await InDbAsync(factory, db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Without_seed_users_there_is_no_company()
    {
        await using var factory = Factory().WithSetting(DevUserSeeder.EmailKey, "").WithSetting(DevUserSeeder.PasswordKey, "");
        _ = factory.Services;

        Assert.Equal(0, await InDbAsync(factory, db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));
    }
}
```

`tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Seeding;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Base of the tests about companies, members and roles: the mail host of <see cref="MailTestBase"/> (invitations
/// are mails), with helpers that make companies and members straight in the database. The test host runs in
/// Development, so its seed user is the <c>admin</c> of the company <see cref="DevSeedTests"/> names
/// <see cref="DevUserSeeder.DefaultOrgName"/>.
/// </summary>
public abstract class TenancyTestBase : MailTestBase
{
    protected TenancyTestBase(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
    }

    protected ManifestHolder Holder => Factory.Services.GetRequiredService<ManifestHolder>();

    /// <summary>Makes a company the way the operator does, with a copy of the active default roles.</summary>
    protected async Task<Guid> CreateCompanyAsync(string name = "Acme")
    {
        using var scope = Factory.Services.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<CompanyService>().CreateAsync(name, TestContext.Current.CancellationToken);
        Assert.True(created.Succeeded, created.Error);
        return created.Value;
    }

    /// <summary>The company the development seed made.</summary>
    protected Task<Guid> DevCompanyIdAsync() =>
        InDbAsync(db => db.Companies.Where(c => c.Name == DevUserSeeder.DefaultOrgName).Select(c => c.Id).SingleAsync(TestContext.Current.CancellationToken));

    protected Task<Guid> RoleIdAsync(Guid companyId, string name)
    {
        var normalized = NameInput.Normalize(name);
        return InDbAsync(db => db.CompanyRoles
            .Where(r => r.CompanyId == companyId && r.NormalizedName == normalized)
            .Select(r => r.Id)
            .SingleAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Makes an account with <see cref="MailTestBase.UserPassword"/> and puts it into the company with the named role.</summary>
    protected async Task<Guid> AddMemberAsync(Guid companyId, string email, string role, bool confirmed = true)
    {
        var user = await CreateUserAsync(email, confirmed);
        var roleId = await RoleIdAsync(companyId, role);
        await InDbAsync(async db =>
        {
            db.Memberships.Add(new Membership { UserId = user.Id, CompanyId = companyId, RoleId = roleId, JoinedAt = StorableTime.Now(Clock) });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });
        return user.Id;
    }
}
```

`tests/Auth.IntegrationTests/TenancyTablesTests.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/TenancyTablesTests.cs
+++ b/tests/Auth.IntegrationTests/TenancyTablesTests.cs
@@ -76,7 +76,7 @@
     {
         var (first, firstRole) = await CompanyWithRoleAsync("Acme");
         var (second, secondRole) = await CompanyWithRoleAsync("Globex");
-        var user = await Factory.SeedUserIdAsync();
+        var user = (await CreateUserAsync("two@example.com", confirmed: true)).Id;
 
         await SaveAsync(db =>
         {
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`CompanyService`, `DevUserSeeder.OrgNameKey` do not exist).

- [ ] **Step 3: Implement.**

`src/Auth.Server/Seeding/DevUserSeeder.cs` — the change:

```diff
--- a/src/Auth.Server/Seeding/DevUserSeeder.cs
+++ b/src/Auth.Server/Seeding/DevUserSeeder.cs
@@ -1,15 +1,23 @@
 using Auth.Infrastructure.Identity;
+using Auth.Infrastructure.Persistence;
+using Auth.Server.Email;
+using Auth.Server.Requests;
+using Auth.Server.Tenancy;
 using Microsoft.AspNetCore.Identity;
+using Microsoft.EntityFrameworkCore;
 
 namespace Auth.Server.Seeding;
 
 /// <summary>
-/// Creates the development users at host startup so there is someone to log in as. It is a no-op outside the
-/// <c>Development</c> environment. The first user (<see cref="EmailKey"/>, <see cref="PasswordKey"/>) has a
-/// confirmed email. The optional second one (<see cref="UnverifiedEmailKey"/>, <see cref="UnverifiedPasswordKey"/>)
-/// has not: until invitations exist it is the only way to have an account that needs verification (spec 0004,
-/// Decision 2). Each is created only when both of its settings are present. Seeding is idempotent and never resets
-/// the password of an existing user.
+/// Creates the development users and their company at host startup so there is someone to log in as. It is a no-op
+/// outside the <c>Development</c> environment. The first user (<see cref="EmailKey"/>, <see cref="PasswordKey"/>) has a
+/// confirmed email and is the <c>admin</c> of the development company (<see cref="OrgNameKey"/>, created with the
+/// default roles of the active manifest). The optional second one (<see cref="UnverifiedEmailKey"/>,
+/// <see cref="UnverifiedPasswordKey"/>) has not confirmed its email: until self-service sign-up exists it is the only
+/// way to have an account that needs verification (spec 0004, Decision 2), and it is a member with a role that does not
+/// manage members (the first default role, in the order of the manifest, that does not; else a new empty role
+/// <c>member</c>). Each user is created only when both of its settings are present. Seeding is idempotent and never
+/// resets the password of an existing user or moves a member to another company.
 /// </summary>
 public static partial class DevUserSeeder
 {
@@ -17,6 +25,9 @@
     public const string PasswordKey = "Auth:DevSeed:Password";
     public const string UnverifiedEmailKey = "Auth:DevSeed:UnverifiedEmail";
     public const string UnverifiedPasswordKey = "Auth:DevSeed:UnverifiedPassword";
+    public const string OrgNameKey = "Auth:DevSeed:OrgName";
+
+    public const string DefaultOrgName = "Development";
 
     public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
     {
@@ -32,27 +43,63 @@
         var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
         var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DevUserSeeder));
 
-        await SeedOneAsync(users, logger, configuration[EmailKey], configuration[PasswordKey], emailConfirmed: true, ct);
-        await SeedOneAsync(users, logger, configuration[UnverifiedEmailKey], configuration[UnverifiedPasswordKey], emailConfirmed: false, ct);
+        var first = await SeedOneAsync(users, logger, configuration[EmailKey], configuration[PasswordKey], emailConfirmed: true, ct);
+        var second = await SeedOneAsync(users, logger, configuration[UnverifiedEmailKey], configuration[UnverifiedPasswordKey], emailConfirmed: false, ct);
+        if (first is null && second is null)
+        {
+            return;
+        }
+
+        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
+        var companyId = await EnsureCompanyAsync(db, scope.ServiceProvider.GetRequiredService<CompanyService>(), logger, configuration[OrgNameKey], ct);
+        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
+        var manifest = scope.ServiceProvider.GetRequiredService<ManifestHolder>().Current;
+        var roles = await db.CompanyRoles.Where(r => r.CompanyId == companyId).ToListAsync(ct);
+
+        // The company's copy of the first default role, in the order of the manifest, that manages members or does not.
+        CompanyRole? FirstDefaultRole(bool manages) => manifest.DefaultRoles
+            .Where(d => manifest.Catalog.Expand(d.Permissions).Contains(PermissionCatalog.MembersManage) == manages)
+            .Select(d => roles.FirstOrDefault(r => r.NormalizedName == NameInput.Normalize(d.Name)))
+            .FirstOrDefault(r => r is not null);
+
+        if (first is not null)
+        {
+            var admin = roles.FirstOrDefault(r => r.NormalizedName == NameInput.Normalize("admin")) ?? FirstDefaultRole(manages: true)
+                ?? throw new InvalidOperationException("The development company has no role that manages members.");
+            await EnsureMemberAsync(db, clock, first, companyId, admin, ct);
+        }
+
+        if (second is not null)
+        {
+            var plain = FirstDefaultRole(manages: false) ?? roles.FirstOrDefault(r => r.NormalizedName == NameInput.Normalize("member"));
+            if (plain is null)
+            {
+                // Every default role manages members: the second user still must not.
+                plain = new CompanyRole { CompanyId = companyId, Name = "member", NormalizedName = NameInput.Normalize("member"), Permissions = [] };
+                db.CompanyRoles.Add(plain);
+                await db.SaveChangesAsync(ct);
+            }
+
+            await EnsureMemberAsync(db, clock, second, companyId, plain, ct);
+        }
     }
 
-    private static async Task SeedOneAsync(
+    private static async Task<ApplicationUser?> SeedOneAsync(
         UserManager<ApplicationUser> users, ILogger logger, string? email, string? password, bool emailConfirmed, CancellationToken ct)
     {
         if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
         {
-            return;
+            return null;
         }
 
         ct.ThrowIfCancellationRequested();
-        if (await users.FindByEmailAsync(email) is not null)
+        if (await users.FindByEmailAsync(email) is { } existing)
         {
-            return;
+            return existing;
         }
 
-        var result = await users.CreateAsync(
-            new ApplicationUser { UserName = email, Email = email, EmailConfirmed = emailConfirmed },
-            password);
+        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = emailConfirmed };
+        var result = await users.CreateAsync(user, password);
         if (!result.Succeeded)
         {
             // Codes only: descriptions can echo policy details, and the password must never be logged.
@@ -61,8 +108,44 @@
         }
 
         LogSeeded(logger, email);
+        return user;
+    }
+
+    private static async Task<Guid> EnsureCompanyAsync(
+        AuthDbContext db, CompanyService companies, ILogger logger, string? configuredName, CancellationToken ct)
+    {
+        var name = string.IsNullOrWhiteSpace(configuredName) ? DefaultOrgName : configuredName;
+        var existing = await db.Companies.AsNoTracking().OrderBy(c => c.CreatedAt).FirstOrDefaultAsync(c => c.Name == name, ct);
+        if (existing is not null)
+        {
+            return existing.Id;
+        }
+
+        var created = await companies.CreateAsync(name, ct);
+        if (!created.Succeeded)
+        {
+            throw new InvalidOperationException($"Could not seed the development company: {created.Error}.");
+        }
+
+        LogSeededCompany(logger, name);
+        return created.Value;
+    }
+
+    private static async Task EnsureMemberAsync(
+        AuthDbContext db, TimeProvider clock, ApplicationUser user, Guid companyId, CompanyRole role, CancellationToken ct)
+    {
+        if (await db.Memberships.AnyAsync(m => m.UserId == user.Id, ct))
+        {
+            return;
+        }
+
+        db.Memberships.Add(new Membership { UserId = user.Id, CompanyId = companyId, RoleId = role.Id, JoinedAt = StorableTime.Now(clock) });
+        await db.SaveChangesAsync(ct);
     }
 
     [LoggerMessage(Level = LogLevel.Information, Message = "Seeded development user {Email}")]
     private static partial void LogSeeded(ILogger logger, string email);
+
+    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded development company {Name}")]
+    private static partial void LogSeededCompany(ILogger logger, string name);
 }
```

`src/Auth.Server/Tenancy/CompanyService.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Requests;

namespace Auth.Server.Tenancy;

/// <summary>Creates companies (spec 0005 → Concepts): only the operator does, through the CLI, and the development seeder.</summary>
public sealed class CompanyService(AuthDbContext db, ManifestHolder manifest, TimeProvider clock)
{
    /// <summary>
    /// Creates a company with its own copy of the active default roles. After that its roles are its own: a later
    /// change to the manifest does not touch them.
    /// </summary>
    public async Task<Outcome<Guid>> CreateAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!NameInput.IsValid(name))
        {
            return Outcome.Fail<Guid>(TenancyErrors.InvalidRequest);
        }

        var company = new Company { Id = Guid.NewGuid(), Name = name, CreatedAt = StorableTime.Now(clock) };
        db.Companies.Add(company);
        foreach (var role in manifest.Current.DefaultRoles)
        {
            db.CompanyRoles.Add(new CompanyRole
            {
                CompanyId = company.Id,
                Name = role.Name,
                NormalizedName = NameInput.Normalize(role.Name),
                Permissions = [.. role.Permissions],
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return Outcome.Ok(company.Id);
    }
}
```

`src/Auth.Server/Tenancy/Outcome.cs`:

```csharp
namespace Auth.Server.Tenancy;

/// <summary>The error codes of spec 0005 → Contract, and the status each one is answered with.</summary>
public static class TenancyErrors
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidToken = "invalid_token";
    public const string WeakPassword = "weak_password";
    public const string UnknownPermission = "unknown_permission";
    public const string Forbidden = "forbidden";
    public const string PermissionsChanged = "permissions_changed";
    public const string PermissionNotHeld = "permission_not_held";
    public const string NotFound = "not_found";
    public const string AlreadyInOrg = "already_in_org";
    public const string AlreadyMember = "already_member";
    public const string InvitePending = "invite_pending";
    public const string LastManager = "last_manager";
    public const string CannotChangeSelf = "cannot_change_self";
    public const string RoleInUse = "role_in_use";
    public const string RoleNameTaken = "role_name_taken";
    public const string TooManyAttempts = "too_many_attempts";

    public static int StatusOf(string error) => error switch
    {
        InvalidRequest or InvalidToken or WeakPassword or UnknownPermission => StatusCodes.Status400BadRequest,
        Forbidden or PermissionsChanged or PermissionNotHeld => StatusCodes.Status403Forbidden,
        NotFound => StatusCodes.Status404NotFound,
        AlreadyInOrg or AlreadyMember or InvitePending or LastManager or CannotChangeSelf or RoleInUse or RoleNameTaken
            => StatusCodes.Status409Conflict,
        TooManyAttempts => StatusCodes.Status429TooManyRequests,
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "Unknown error code."),
    };
}

/// <summary>
/// What a service did: nothing wrong, or the code of what was refused. The API turns the code into a response and the
/// operator commands print it, so a refusal means the same on both.
/// </summary>
public readonly record struct Outcome(string? Error = null, TimeSpan RetryAfter = default)
{
    public static Outcome Done => default;

    public bool Succeeded => Error is null;

    public static Outcome Fail(string error) => new(error);

    /// <summary>Over the mail limit: refused, and when it would not be.</summary>
    public static Outcome Limited(TimeSpan retryAfter) => new(TenancyErrors.TooManyAttempts, retryAfter);

    public static Outcome<T> Ok<T>(T value) => new(value);

    public static Outcome<T> Fail<T>(string error) => new(default, error);
}

/// <summary>An <see cref="Outcome"/> that carries a value when it succeeded.</summary>
public readonly record struct Outcome<T>(T? Value, string? Error = null, TimeSpan RetryAfter = default)
{
    public bool Succeeded => Error is null;

    public Outcome Without => new(Error, RetryAfter);
}
```

`src/Auth.Server/Tenancy/TenancyServices.cs` — the change:

```diff
--- a/src/Auth.Server/Tenancy/TenancyServices.cs
+++ b/src/Auth.Server/Tenancy/TenancyServices.cs
@@ -13,6 +13,7 @@
         services.AddSingleton(ManifestSettings.Load(configuration, contentRoot));
         services.AddSingleton<ManifestHolder>();
         services.AddSingleton<ManifestActivator>();
+        services.AddScoped<CompanyService>();
         return services;
     }
 }
```

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS — 19 new tests, 495 in all; `DevSeedTests` and every other existing test
  unedited.

- [ ] **Step 5: Commit** — `feat(tenancy): create companies with the default roles; the dev seed joins one`

### Task 5: The claims, and `no_membership`

**Files:**
- Create: `src/Auth.Server/Tenancy/MembershipReader.cs`, `src/Auth.Server/Tenancy/TenantClaims.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/AccessTokens.cs`,
  `tests/Auth.IntegrationTests/TenantClaimsTests.cs`
- Modify: `src/Auth.Server/Login/LoginEndpoint.cs`, `src/Auth.Server/Sessions/RefreshEndpoint.cs`,
  `src/Auth.Server/Sessions/AccessTokenClaimFilter.cs`, `src/Auth.Server/Account/AccountResults.cs`,
  `src/Auth.Server/Tenancy/TenancyServices.cs`,
  `tests/Auth.IntegrationTests/LoginTests.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/MailTestBase.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/SessionApi.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs`,
  `tests/Auth.IntegrationTests/TenancyTablesTests.cs`,
  `docs/superpowers/plans/0001-acceptance-map.md`

**Interfaces:**
- Consumes: Task 1 (`Manifest.Catalog.Expand`), Task 2 (tables), Task 3 (`ManifestHolder`),
  Task 4.
- Produces: `TenantContext(UserId, CompanyId, CompanyName, RoleId, RoleName,
  IReadOnlyList<string> Permissions, bool HoldsAll)` with `Holds(permission)`: what the
  database says now, the permissions already expanded against the active catalog;
  `HoldsAll` is true when the role holds `*` itself, not merely every permission one by one.
- Produces: `MembershipReader` (scoped) with
  `Task<TenantContext?> ReadAsync(Guid userId, CancellationToken)` (the earliest
  membership; `null` for none). Login, refresh, `/auth/me` and every company API call read
  through it.
- Produces: `TenantClaims.Apply(ClaimsIdentity, TenantContext)` (removes any claim of the
  three types, then adds `org_id` as a string and `roles` and `permissions` as JSON
  arrays), `TenantClaims.DestinationsOf` and `TenantClaims.Types`. The access token carries
  `sub` and the three; the session start and stamp stay in the refresh token only, and the
  refresh token never keeps a copy of the three.
- Changes: `AccountResults.NoMembership()` (`403`, `{"error":"no_membership"}`), and
  `AccountResults.NoStoreResult` becomes `internal` so that the company API of Task 6 can
  write its answers the same way.
- Changes: login, after the correct password and the confirmed email, refuses an account
  with no membership as `403 {"error":"no_membership"}` — no token, no cookie, and the
  streak has ended already. Refresh refuses a user who is no longer a member with the
  `InvalidGrant` of spec 0002.
- Changes (tests): `MailTestBase.CreateUserAsync(email, confirmed, password, member = true)`
  makes the account a member of the development company's `user` role, because an account
  without a company can no longer log in and several existing tests log in as accounts
  they created; `member: false` makes one that belongs nowhere.
  `SessionApi.LoginAsync(client, email, password)` logs in as any account. In
  `LoginTests`, `Token_carries_contract_claims_and_nothing_from_week_3` becomes
  `Token_carries_contract_claims_and_the_tenancy_claims` and the exact claim set gains
  `org_id`, `permissions`, `roles`; the 0001 acceptance map follows. In
  `TenancyTablesTests` the one test that made an account of its own (Task 4) now says
  `member: false`, so that the account belongs to no company before the test puts it into two.

- [ ] **Step 1: Write the failing tests and make the test changes.**

`tests/Auth.IntegrationTests/Infrastructure/AccessTokens.cs`:

```csharp
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Reads the payload of an access token without validating it: the tests ask what the token says.</summary>
public static class AccessTokens
{
    public static JsonObject Payload(string jwt)
    {
        ArgumentNullException.ThrowIfNull(jwt);

        return JsonNode.Parse(Base64UrlEncoder.Decode(jwt.Split('.')[1]))!.AsObject();
    }

    /// <summary>A claim that must be a JSON array of strings, as the contract says of <c>roles</c> and <c>permissions</c>.</summary>
    public static string[] Array(string jwt, string claim)
    {
        var node = Payload(jwt)[claim];
        Assert.NotNull(node);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, node.GetValueKind());
        return [.. node.AsArray().Select(n => n!.GetValue<string>())];
    }

    public static string Text(string jwt, string claim) =>
        Payload(jwt)[claim]?.GetValue<string>() ?? throw new InvalidOperationException($"The token has no claim '{claim}'.");
}
```

`tests/Auth.IntegrationTests/Infrastructure/MailTestBase.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/Infrastructure/MailTestBase.cs
+++ b/tests/Auth.IntegrationTests/Infrastructure/MailTestBase.cs
@@ -2,7 +2,9 @@
 using Auth.Infrastructure.Identity;
 using Auth.Infrastructure.Persistence;
 using Auth.Server.Email;
+using Auth.Server.Seeding;
 using Microsoft.AspNetCore.Identity;
+using Microsoft.EntityFrameworkCore;
 using Microsoft.Extensions.DependencyInjection;
 using Microsoft.Extensions.DependencyInjection.Extensions;
 using Microsoft.Extensions.Logging;
@@ -53,13 +55,31 @@
         Assert.True(decision.Allowed, $"The mail limit refused the request; wait {decision.RetryAfter} on the test clock.");
     }
 
-    protected async Task<ApplicationUser> CreateUserAsync(string email, bool confirmed, string password = UserPassword)
+    /// <summary>
+    /// Makes an account. Since spec 0005 an account that belongs to no company cannot log in, so by default the account
+    /// joins the development company with its role <c>user</c> (when the host has one); pass <paramref name="member"/>
+    /// <see langword="false"/> for an account that belongs nowhere.
+    /// </summary>
+    protected async Task<ApplicationUser> CreateUserAsync(string email, bool confirmed, string password = UserPassword, bool member = true)
     {
         using var scope = Factory.Services.CreateScope();
         var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
         var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = confirmed };
         var result = await users.CreateAsync(user, password);
         Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Code)));
+
+        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
+        var role = member
+            ? await db.CompanyRoles.AsNoTracking().FirstOrDefaultAsync(
+                r => r.NormalizedName == "USER" && db.Companies.Any(c => c.Id == r.CompanyId && c.Name == DevUserSeeder.DefaultOrgName),
+                TestContext.Current.CancellationToken)
+            : null;
+        if (role is not null)
+        {
+            db.Memberships.Add(new Membership { UserId = user.Id, CompanyId = role.CompanyId, RoleId = role.Id, JoinedAt = StorableTime.Now(Clock) });
+            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
+        }
+
         return user;
     }
 
```

`tests/Auth.IntegrationTests/Infrastructure/SessionApi.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/Infrastructure/SessionApi.cs
+++ b/tests/Auth.IntegrationTests/Infrastructure/SessionApi.cs
@@ -29,6 +29,13 @@
     public static async Task<Session> LoginAsync(HttpClient client, AuthAppFactory factory)
     {
         using var response = await LoginApi.Login(client, factory.SeedEmail, factory.SeedPassword);
+        return await ReadSessionAsync(response);
+    }
+
+    /// <summary>Logs in as any account and returns its session; asserts the <c>200</c>.</summary>
+    public static async Task<Session> LoginAsync(HttpClient client, string email, string password)
+    {
+        using var response = await LoginApi.Login(client, email, password);
         return await ReadSessionAsync(response);
     }
 
```

`tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs
+++ b/tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs
@@ -45,10 +45,34 @@
             .SingleAsync(TestContext.Current.CancellationToken));
     }
 
+    /// <summary>Changes what a role holds, straight in the database.</summary>
+    protected Task SetRolePermissionsAsync(Guid roleId, params string[] permissions) =>
+        InDbAsync(async db =>
+        {
+            var role = await db.CompanyRoles.SingleAsync(r => r.Id == roleId, TestContext.Current.CancellationToken);
+            role.Permissions = permissions;
+            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
+            return 0;
+        });
+
+    /// <summary>Gives a member another role of their company, straight in the database.</summary>
+    protected Task SetMemberRoleAsync(Guid userId, Guid roleId) =>
+        InDbAsync(async db =>
+        {
+            var membership = await db.Memberships.SingleAsync(m => m.UserId == userId, TestContext.Current.CancellationToken);
+            membership.RoleId = roleId;
+            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
+            return 0;
+        });
+
+    /// <summary>Ends a membership, straight in the database.</summary>
+    protected Task RemoveMembershipAsync(Guid userId) =>
+        InDbAsync(db => db.Memberships.Where(m => m.UserId == userId).ExecuteDeleteAsync(TestContext.Current.CancellationToken));
+
     /// <summary>Makes an account with <see cref="MailTestBase.UserPassword"/> and puts it into the company with the named role.</summary>
     protected async Task<Guid> AddMemberAsync(Guid companyId, string email, string role, bool confirmed = true)
     {
-        var user = await CreateUserAsync(email, confirmed);
+        var user = await CreateUserAsync(email, confirmed, member: false);
         var roleId = await RoleIdAsync(companyId, role);
         await InDbAsync(async db =>
         {
```

`tests/Auth.IntegrationTests/LoginTests.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/LoginTests.cs
+++ b/tests/Auth.IntegrationTests/LoginTests.cs
@@ -50,7 +50,7 @@
     }
 
     [Fact]
-    public async Task Token_carries_contract_claims_and_nothing_from_week_3()   // criterion 3
+    public async Task Token_carries_contract_claims_and_the_tenancy_claims()   // criterion 3; spec 0005 adds org_id, roles, permissions
     {
         var jwt = new JsonWebToken(await LoginToken(_client, _factory));
 
@@ -61,9 +61,9 @@
         Assert.True(jwt.ValidTo - jwt.IssuedAt <= TimeSpan.FromMinutes(10));
         // Bound exp against NOW too, so a trivially short (or overlong) lifetime fails: ~10 min from the login.
         Assert.InRange(jwt.ValidTo, DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow.AddMinutes(10).AddSeconds(30));
-        foreach (var absent in new[] { "org_id", "roles", "permissions" })
+        foreach (var present in new[] { "org_id", "roles", "permissions" })
         {
-            Assert.DoesNotContain(jwt.Claims, c => c.Type == absent);
+            Assert.Contains(jwt.Claims, c => c.Type == present);
         }
     }
 
@@ -73,8 +73,9 @@
         var jwt = new JsonWebToken(await LoginToken(_client, _factory));
 
         // iss, aud, sub, exp are the contract. iat, jti and oi_tkn_id are OpenIddict metadata that cannot be
-        // switched off; they are accepted extras pending an owner decision. Anything else is a contract leak.
-        string[] expected = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "sub"];
+        // switched off; they are accepted extras pending an owner decision. org_id, roles and permissions came with
+        // spec 0005. Anything else is a contract leak.
+        string[] expected = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "permissions", "roles", "sub"];
         Assert.Equal(expected, jwt.Claims.Select(c => c.Type).Distinct().Order(StringComparer.Ordinal));
     }
 
```

`tests/Auth.IntegrationTests/TenancyTablesTests.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/TenancyTablesTests.cs
+++ b/tests/Auth.IntegrationTests/TenancyTablesTests.cs
@@ -76,7 +76,7 @@
     {
         var (first, firstRole) = await CompanyWithRoleAsync("Acme");
         var (second, secondRole) = await CompanyWithRoleAsync("Globex");
-        var user = (await CreateUserAsync("two@example.com", confirmed: true)).Id;
+        var user = (await CreateUserAsync("two@example.com", confirmed: true, member: false)).Id;
 
         await SaveAsync(db =>
         {
```

`tests/Auth.IntegrationTests/TenantClaimsTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class TenantClaimsTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string NoMembership = """{"error":"no_membership"}""";

    private static readonly string[] EveryPermission =
        ["members:manage", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"];

    private Task<SessionApi.Session> SeedLoginAsync() => SessionApi.LoginAsync(Client, Factory);

    [Fact]
    public async Task Login_token_names_the_company_the_role_and_every_permission_of_the_catalog()   // criterion 4
    {
        var session = await SeedLoginAsync();

        Assert.Equal((await DevCompanyIdAsync()).ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        // `*` expanded to the whole catalog: sorted ordinally, no duplicates, no star.
        Assert.Equal(EveryPermission, AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task Role_and_permission_lists_are_arrays_even_with_one_entry()
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "reader@example.com", "user");
        await SetRolePermissionsAsync(await RoleIdAsync(company, "user"), "reports:read");

        var session = await SessionApi.LoginAsync(Client, "reader@example.com", UserPassword);

        Assert.Equal(["user"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(["reports:read"], AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task A_role_holding_nothing_gives_an_empty_permission_array()
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "nobody@example.com", "user");
        await SetRolePermissionsAsync(await RoleIdAsync(company, "user"));

        var session = await SessionApi.LoginAsync(Client, "nobody@example.com", UserPassword);

        Assert.Empty(AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task Permissions_are_sorted_ordinally_without_duplicates_and_without_names_that_left_the_catalog()
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "mixed@example.com", "user");
        await SetRolePermissionsAsync(await RoleIdAsync(company, "user"), "reports:read", "members:manage", "reports:read", "old:thing", "reports:approve");

        var session = await SessionApi.LoginAsync(Client, "mixed@example.com", UserPassword);

        Assert.Equal(["members:manage", "reports:approve", "reports:read"], AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task Token_has_no_claim_beyond_the_contract_and_the_tenancy_claims_after_refreshes()
    {
        var session = await SeedLoginAsync();
        for (var i = 0; i < 3; i++)
        {
            session = await SessionApi.RefreshOk(Client, session.RefreshToken);
        }

        // Three generations on, nothing is doubled: one company, one role, each permission once.
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(EveryPermission, AccessTokens.Array(session.AccessToken, "permissions"));
        Assert.Equal(
            ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "permissions", "roles", "sub"],
            AccessTokens.Payload(session.AccessToken).Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Account_without_a_company_is_refused_with_no_token_and_no_cookie()   // criterion 11
    {
        await CreateUserAsync("lonely@example.com", confirmed: true, member: false);

        using var response = await LoginApi.Login(Client, "lonely@example.com", UserPassword);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"Expected 403, got {(int)response.StatusCode}: {raw}");
        Assert.Equal(NoMembership, raw);
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }

    [Fact]
    public async Task Wrong_password_for_an_account_without_a_company_is_the_ordinary_401()   // criterion 11
    {
        await CreateUserAsync("lonely@example.com", confirmed: true, member: false);

        using var lonely = await LoginApi.Login(Client, "lonely@example.com", LockoutApi.WrongPassword);
        using var member = await LoginApi.Login(Client, Factory.SeedEmail, LockoutApi.WrongPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, lonely.StatusCode);
        Assert.Equal(await member.Content.ReadAsStringAsync(), await lonely.Content.ReadAsStringAsync());
        Assert.Equal(LoginApi.HeaderNames(member), LoginApi.HeaderNames(lonely));
    }

    [Fact]
    public async Task The_refusal_ends_the_streak_as_a_success_would()   // criterion 11
    {
        await CreateUserAsync("lonely@example.com", confirmed: true, member: false);
        await LockoutApi.FailAsync(Client, Clock, "lonely@example.com", 9);

        Clock.Advance(LockoutApi.HumanPace);
        using (var refused = await LoginApi.Login(Client, "lonely@example.com", UserPassword))   // the tenth attempt of the streak
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        await LockoutApi.FailAsync(Client, Clock, "lonely@example.com", 9);   // nine more ordinary 401s: no lock
    }

    [Fact]
    public async Task The_order_is_password_then_confirmed_email_then_membership()   // spec 0005 → POST /auth/login
    {
        await CreateUserAsync("both@example.com", confirmed: false, member: false);

        using var response = await LoginApi.Login(Client, "both@example.com", UserPassword);

        Assert.Equal("""{"error":"email_not_verified"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Next_refresh_carries_a_changed_role_and_ends_no_session()   // criterion 12
    {
        var company = await DevCompanyIdAsync();
        var worker = await AddMemberAsync(company, "worker@example.com", "admin");
        var session = await SessionApi.LoginAsync(Client, "worker@example.com", UserPassword);
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "user"));
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        Assert.Equal(["user"], AccessTokens.Array(refreshed.AccessToken, "roles"));
        Assert.Equal(["reports:approve", "reports:read"], AccessTokens.Array(refreshed.AccessToken, "permissions"));
        _ = await SessionApi.RefreshOk(Client, refreshed.RefreshToken);   // the session goes on
    }

    [Fact]
    public async Task Next_refresh_carries_an_edited_role_and_ends_no_session()   // criterion 12
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "worker@example.com", "user");
        var session = await SessionApi.LoginAsync(Client, "worker@example.com", UserPassword);

        await SetRolePermissionsAsync(await RoleIdAsync(company, "user"), "templates:manage");
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        Assert.Equal(["templates:manage"], AccessTokens.Array(refreshed.AccessToken, "permissions"));
        _ = await SessionApi.RefreshOk(Client, refreshed.RefreshToken);
    }

    [Fact]
    public async Task Refresh_of_someone_who_is_no_longer_a_member_is_the_401_of_spec_0002()   // criterion 13
    {
        var company = await DevCompanyIdAsync();
        var leaver = await AddMemberAsync(company, "leaver@example.com", "user");
        var session = await SessionApi.LoginAsync(Client, "leaver@example.com", UserPassword);

        await RemoveMembershipAsync(leaver);

        using var response = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(response);
    }

    [Fact]
    public async Task A_permission_that_leaves_the_catalog_is_gone_from_the_next_refresh_and_a_new_one_reaches_star()   // criterion 21
    {
        var session = await SeedLoginAsync();   // admin: *
        Assert.Contains("templates:manage", AccessTokens.Array(session.AccessToken, "permissions"));

        Holder.Set(ManifestParser.Parse("permissions: [reports:read, orders:read]\ndefault_roles:\n  admin: [\"*\"]\n").Manifest!, null);
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        var permissions = AccessTokens.Array(refreshed.AccessToken, "permissions");
        Assert.DoesNotContain("templates:manage", permissions);
        Assert.DoesNotContain("reports:approve", permissions);
        Assert.Contains("orders:read", permissions);
        Assert.Equal(["members:manage", "orders:read", "org:manage", "reports:read", "roles:manage"], permissions);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: the build passes (the helpers are in step 1) and the suite FAILS where the
  slice is not there yet: the claim tests (`org_id`, `roles` and `permissions` are not in
  the token), the `no_membership` tests (the login answers `200`), the refresh test of a
  removed member, and the two `LoginTests` that now expect the three claims.

- [ ] **Step 3: Implement.** The refresh endpoint reads the membership after the stamp
  check; the claims are applied after the identity is built from the refresh token's own
  claims, and `Apply` removes the stale ones first. The refresh token is stripped of the
  three claims in `AccessTokenClaimFilter`.

`docs/superpowers/plans/0001-acceptance-map.md` — the change:

```diff
--- a/docs/superpowers/plans/0001-acceptance-map.md
+++ b/docs/superpowers/plans/0001-acceptance-map.md
@@ -10,7 +10,7 @@
 | - | --------- | ---------------- |
 | 1 | Correct credentials → `200` + JWT | `LoginTests.Valid_credentials_return_200_with_exact_contract_body`; e2e step 1 |
 | 2 | Unencrypted RS256 JWS with `kid` | `LoginTests.Token_is_unencrypted_RS256_jws_with_kid` |
-| 3 | `iss`, `aud`, `sub`, `exp` ≤ 10 min; no Week 3 claims | `LoginTests.Token_carries_contract_claims_and_nothing_from_week_3` (exp bounded against now), `LoginTests.Token_payload_claim_set_is_exactly_the_contract_plus_openiddict_metadata`, `LoginTests.Token_header_typ_is_at_jwt_and_payload_has_no_identity_extras` |
+| 3 | `iss`, `aud`, `sub`, `exp` ≤ 10 min; the Week 3 claims `org_id`, `roles` and `permissions` came with spec 0005 | `LoginTests.Token_carries_contract_claims_and_the_tenancy_claims` (exp bounded against now), `LoginTests.Token_payload_claim_set_is_exactly_the_contract_plus_openiddict_metadata`, `LoginTests.Token_header_typ_is_at_jwt_and_payload_has_no_identity_extras` |
 | 4 | Verifies against JWKS `kid`, no shared secret | `TokenVerificationTests.Token_verifies_against_jwks_kid_without_shared_secret`, `TokenVerificationTests.Token_from_a_host_with_different_keys_fails_against_this_jwks`; e2e step 2 (PyJWT) |
 | 5 | Unknown email and wrong password → identical `401` | `LoginTests.Unknown_email_and_wrong_password_are_indistinguishable`, `LoginTests.Basic_authorization_header_does_not_change_the_uniform_401`; e2e step 3 |
 | 6 | Malformed request → `400`, not `500` | `LoginRequestTests.Malformed_json_login_returns_400` (18 cases, incl. NUL/control characters), `Form_encoded_oidc_request_returns_400`, `Plain_text_body_returns_400`, `Oversized_body_returns_400`, `Trailing_slash_login_with_non_json_body_returns_400`, `Trailing_slash_login_with_get_returns_400`; `UnhandledTokenRequestGuardTests` |
```

`src/Auth.Server/Account/AccountResults.cs` — the change:

```diff
--- a/src/Auth.Server/Account/AccountResults.cs
+++ b/src/Auth.Server/Account/AccountResults.cs
@@ -12,6 +12,7 @@
     public const string InvalidTokenError = "invalid_token";
     public const string WeakPasswordError = "weak_password";
     public const string EmailNotVerifiedError = "email_not_verified";
+    public const string NoMembershipError = "no_membership";
 
     /// <summary>202: the request was taken. Says nothing about whether a mail will follow.</summary>
     public static IResult Accepted() => new NoStoreResult(StatusCodes.Status202Accepted, null);
@@ -32,7 +33,11 @@
     public static IResult EmailNotVerified() =>
         new NoStoreResult(StatusCodes.Status403Forbidden, new { error = EmailNotVerifiedError });
 
-    private sealed class NoStoreResult(int statusCode, object? body) : IResult
+    /// <summary>403: the password is right and the email confirmed, but the account belongs to no company (spec 0005).</summary>
+    public static IResult NoMembership() =>
+        new NoStoreResult(StatusCodes.Status403Forbidden, new { error = NoMembershipError });
+
+    internal sealed class NoStoreResult(int statusCode, object? body) : IResult
     {
         public Task ExecuteAsync(HttpContext httpContext)
         {
```

`src/Auth.Server/Login/LoginEndpoint.cs` — the change:

```diff
--- a/src/Auth.Server/Login/LoginEndpoint.cs
+++ b/src/Auth.Server/Login/LoginEndpoint.cs
@@ -4,6 +4,7 @@
 using Auth.Server.Account;
 using Auth.Server.Lockout;
 using Auth.Server.Sessions;
+using Auth.Server.Tenancy;
 using Auth.Server.Tokens;
 using Microsoft.AspNetCore;
 using TokenOptions = Auth.Server.Tokens.TokenOptions;
@@ -23,7 +24,9 @@
 /// with ASP.NET Core Identity and either asks OpenIddict to issue the token (<see cref="Results.SignIn"/>) or returns
 /// the uniform <c>401 invalid_credentials</c>. Every attempt is counted first (<see cref="LoginStreakStore"/>); during
 /// a cooldown it is refused with <c>429</c> before the account is looked up or the password evaluated. The success body is reshaped by <see cref="LoginResponseShaper"/>.
-/// A correct password for an account whose email is not confirmed is refused with <c>403 email_not_verified</c>.
+/// A correct password for an account whose email is not confirmed is refused with <c>403 email_not_verified</c>, and
+/// one for an account that belongs to no company with <c>403 no_membership</c>; an access token carries the company,
+/// the role and its permissions as the database says at that moment (spec 0005).
 /// </summary>
 public static class LoginEndpoint
 {
@@ -31,7 +34,7 @@
 
     public static async Task<IResult> HandleAsync(
         HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens, TimeProvider clock, LoginStreakStore streaks,
-        DecoyPasswordHash decoy)
+        DecoyPasswordHash decoy, MembershipReader memberships)
     {
         ArgumentNullException.ThrowIfNull(http);
         ArgumentNullException.ThrowIfNull(users);
@@ -39,6 +42,7 @@
         ArgumentNullException.ThrowIfNull(clock);
         ArgumentNullException.ThrowIfNull(streaks);
         ArgumentNullException.ThrowIfNull(decoy);
+        ArgumentNullException.ThrowIfNull(memberships);
 
         var request = http.GetOpenIddictServerRequest()
             ?? throw new InvalidOperationException("The login endpoint was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
@@ -78,6 +82,12 @@
             return AccountResults.EmailNotVerified();
         }
 
+        // Likewise an account that belongs to no company (spec 0005): it has nothing to put into a token.
+        if (await memberships.ReadAsync(user.Id, http.RequestAborted) is not { } tenant)
+        {
+            return AccountResults.NoMembership();
+        }
+
         var identity = new ClaimsIdentity(
             authenticationType: TokenValidationParameters.DefaultAuthenticationType,
             nameType: Claims.Name,
@@ -91,10 +101,13 @@
         // whose login was still in flight when the change was committed, carries the old stamp and cannot refresh.
         identity.SetClaim(SessionPolicy.StampClaim, user.SecurityStamp);
 
+        // The company, the role and what it grants, read from the database now.
+        TenantClaims.Apply(identity, tenant);
+
         var principal = new ClaimsPrincipal(identity);
         principal.SetResources(tokens.Value.Audience);
-        // `sub` is the whole access token; the session start stays in the refresh token only.
-        principal.SetDestinations(static claim => claim.Type == Claims.Subject ? [Destinations.AccessToken] : []);
+        // `sub` and the tenancy claims make the access token; the session start stays in the refresh token only.
+        principal.SetDestinations(TenantClaims.DestinationsOf);
         principal.SetRefreshTokenLifetime(SessionPolicy.SlidingLifetime);
         http.Items[RefreshCookie.LifetimeItemKey] = SessionPolicy.SlidingLifetime;
 
```

`src/Auth.Server/Sessions/AccessTokenClaimFilter.cs` — the change:

```diff
--- a/src/Auth.Server/Sessions/AccessTokenClaimFilter.cs
+++ b/src/Auth.Server/Sessions/AccessTokenClaimFilter.cs
@@ -1,3 +1,4 @@
+using Auth.Server.Tenancy;
 using OpenIddict.Server;
 using static OpenIddict.Abstractions.OpenIddictConstants;
 
@@ -36,6 +37,16 @@
             }
         }
 
+        // The company, role and permissions belong to the access token: a refresh reads them afresh, so a copy in the
+        // refresh token would only be stale (spec 0005 → Access token).
+        if (context.TokenType == TokenTypeIdentifiers.RefreshToken && context.SecurityTokenDescriptor?.Subject is { } refreshSubject)
+        {
+            foreach (var claim in refreshSubject.Claims.Where(c => TenantClaims.Types.Contains(c.Type)).ToList())
+            {
+                refreshSubject.RemoveClaim(claim);
+            }
+        }
+
         return ValueTask.CompletedTask;
     }
 }
```

`src/Auth.Server/Sessions/RefreshEndpoint.cs` — the change:

```diff
--- a/src/Auth.Server/Sessions/RefreshEndpoint.cs
+++ b/src/Auth.Server/Sessions/RefreshEndpoint.cs
@@ -1,6 +1,7 @@
 using System.Globalization;
 using System.Security.Claims;
 using Auth.Infrastructure.Identity;
+using Auth.Server.Tenancy;
 using Microsoft.AspNetCore.Authentication;
 using Microsoft.AspNetCore.Identity;
 using Microsoft.Extensions.Options;
@@ -15,17 +16,18 @@
 /// <summary>
 /// The pass-through half of <c>POST /auth/refresh</c>. By the time it runs, OpenIddict has validated the refresh
 /// token from the cookie (unknown, expired, revoked and reused tokens never get here). This endpoint checks that the
-/// user still exists and asks OpenIddict to issue the next pair of tokens.
+/// user still exists and is still a member of a company, and asks OpenIddict to issue the next pair of tokens.
 /// </summary>
 public static class RefreshEndpoint
 {
     public static async Task<IResult> HandleAsync(
-        HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens, TimeProvider clock)
+        HttpContext http, UserManager<ApplicationUser> users, IOptions<TokenOptions> tokens, TimeProvider clock, MembershipReader memberships)
     {
         ArgumentNullException.ThrowIfNull(http);
         ArgumentNullException.ThrowIfNull(users);
         ArgumentNullException.ThrowIfNull(tokens);
         ArgumentNullException.ThrowIfNull(clock);
+        ArgumentNullException.ThrowIfNull(memberships);
 
         var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
         var subject = result.Principal?.GetClaim(Claims.Subject);
@@ -50,6 +52,13 @@
             return InvalidGrant();
         }
 
+        // Someone who is no longer a member has no session (spec 0005 → Effects of removing a member), and everyone else
+        // gets the company, role and permissions as the database says now: a role change reaches the next token here.
+        if (await memberships.ReadAsync(user.Id, http.RequestAborted) is not { } tenant)
+        {
+            return InvalidGrant();
+        }
+
         // Start from the refresh token's own claims, so OpenIddict's internal ones (the authorization id that makes
         // the family) carry over to the new tokens.
         var identity = new ClaimsIdentity(
@@ -58,10 +67,11 @@
             nameType: Claims.Name,
             roleType: Claims.Role);
         identity.SetClaim(Claims.Subject, user.Id.ToString());
+        TenantClaims.Apply(identity, tenant);
 
         var principal = new ClaimsPrincipal(identity);
         principal.SetResources(tokens.Value.Audience);
-        principal.SetDestinations(static claim => claim.Type == Claims.Subject ? [Destinations.AccessToken] : []);
+        principal.SetDestinations(TenantClaims.DestinationsOf);
         principal.SetRefreshTokenLifetime(lifetime);
         http.Items[RefreshCookie.LifetimeItemKey] = lifetime;
 
```

`src/Auth.Server/Tenancy/MembershipReader.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>A user's company and role as the database says now, with what the role grants against the active catalog.</summary>
/// <param name="Permissions">What the role grants: <c>*</c> expanded, no duplicates, sorted ordinally, never <c>*</c>.</param>
/// <param name="HoldsAll">Whether the role holds <c>*</c> itself, not merely every permission one by one.</param>
public sealed record TenantContext(
    Guid UserId, Guid CompanyId, string CompanyName, Guid RoleId, string RoleName, IReadOnlyList<string> Permissions, bool HoldsAll)
{
    public bool Holds(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);
}

/// <summary>
/// Reads a user's membership, role and the catalog afresh (spec 0005 → Access token): login, refresh, <c>GET /auth/me</c>
/// and every call of the company API go through it, so none of them trusts what a token said earlier.
/// </summary>
public sealed class MembershipReader(AuthDbContext db, ManifestHolder manifest)
{
    /// <summary>The user's membership; <see langword="null"/> when they belong to no company. With several, the earliest.</summary>
    public async Task<TenantContext?> ReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var row = await (
            from membership in db.Memberships.AsNoTracking()
            join company in db.Companies.AsNoTracking() on membership.CompanyId equals company.Id
            join role in db.CompanyRoles.AsNoTracking() on membership.RoleId equals role.Id
            where membership.UserId == userId
            orderby membership.JoinedAt
            select new { CompanyId = company.Id, CompanyName = company.Name, RoleId = role.Id, RoleName = role.Name, role.Permissions })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        return new TenantContext(
            userId,
            row.CompanyId,
            row.CompanyName,
            row.RoleId,
            row.RoleName,
            manifest.Current.Catalog.Expand(row.Permissions),
            row.Permissions.Contains(PermissionCatalog.All, StringComparer.Ordinal));
    }
}
```

`src/Auth.Server/Tenancy/TenancyServices.cs` — the change:

```diff
--- a/src/Auth.Server/Tenancy/TenancyServices.cs
+++ b/src/Auth.Server/Tenancy/TenancyServices.cs
@@ -14,6 +14,7 @@
         services.AddSingleton<ManifestHolder>();
         services.AddSingleton<ManifestActivator>();
         services.AddScoped<CompanyService>();
+        services.AddScoped<MembershipReader>();
         return services;
     }
 }
```

`src/Auth.Server/Tenancy/TenantClaims.cs`:

```csharp
using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Tenancy;

/// <summary>
/// The three claims the access token gains (spec 0005 → Access token): <c>org_id</c>, <c>roles</c> and
/// <c>permissions</c>. Login and refresh build them in the same place, from the same read, so they cannot differ.
/// </summary>
public static class TenantClaims
{
    public const string OrgId = "org_id";
    public const string Roles = "roles";
    public const string Permissions = "permissions";

    public static readonly IReadOnlyList<string> Types = [OrgId, Roles, Permissions];

    /// <summary>
    /// Puts the three claims on the identity, replacing any that are there: a refresh starts from the claims of the
    /// refresh token, and what they say is out of date. The two lists are JSON arrays, so that a role list of one
    /// name is an array in the token and not a string.
    /// </summary>
    public static void Apply(ClaimsIdentity identity, TenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(tenant);

        foreach (var stale in identity.Claims.Where(c => Types.Contains(c.Type)).ToList())
        {
            identity.RemoveClaim(stale);
        }

        identity.AddClaim(new Claim(OrgId, tenant.CompanyId.ToString(), ClaimValueTypes.String));
        identity.AddClaim(new Claim(Roles, JsonSerializer.Serialize(new[] { tenant.RoleName }), JsonClaimValueTypes.JsonArray));
        identity.AddClaim(new Claim(Permissions, JsonSerializer.Serialize(tenant.Permissions), JsonClaimValueTypes.JsonArray));
    }

    /// <summary>
    /// The access token carries <c>sub</c> and the three claims; everything else (the session start and stamp) lives in
    /// the refresh token only.
    /// </summary>
    public static IEnumerable<string> DestinationsOf(Claim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        return claim.Type == Claims.Subject || Types.Contains(claim.Type) ? [Destinations.AccessToken] : [];
    }
}
```

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS — 13 new tests, 508 in all, among them the three existing tests that log
  in as an account made by `CreateUserAsync`
  (`UnverifiedLoginTests.After_verification_the_account_logs_in` and two of
  `ResetPasswordTests`), which pass because that account now joins the development company.

- [ ] **Step 5: Commit** — `feat(tenancy): put org_id, roles and permissions into the access token; refuse no_membership`

### Task 6: Bearer validation, permission checks against the database, `/auth/me` and `/auth/org`

**Files:**
- Create: `src/Auth.Server/Tenancy/CompanyAccess.cs`, `src/Auth.Server/Tenancy/Contracts.cs`,
  `src/Auth.Server/Api/ApiResults.cs`, `src/Auth.Server/Api/NoStoreMiddleware.cs`,
  `src/Auth.Server/Api/EndpointMetadata.cs`, `src/Auth.Server/Api/OrgEndpoints.cs`,
  `src/Auth.Server/Api/TenancyEndpoints.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/TenancyApi.cs`,
  `tests/Auth.IntegrationTests/BearerValidationTests.cs`,
  `tests/Auth.IntegrationTests/OrgEndpointTests.cs`
- Modify: `src/Auth.Server/Tokens/OpenIddictSetup.cs`, `src/Auth.Server/Program.cs`

**Interfaces:**
- Consumes: Task 5 (`MembershipReader`, `TenantContext`).
- Produces: bearer validation of the service's own tokens (signature, issuer, audience —
  **`AddAudiences` is required** — and expiry), the default authentication scheme, and
  `app.UseAuthentication()`/`UseAuthorization()` placed after `UseNoStoreForTenancyPaths()`
  so that even the `401` carries `Cache-Control: no-store`. JSON property names become
  `snake_case` (`ConfigureHttpJsonOptions`); the bodies written before are unaffected,
  their names already are.
- Produces: `CompanyAccess.AuthorizeAsync(http, memberships, params string[] anyOf)` →
  `Access(TenantContext? Caller, IResult? Failure)`: no membership or no permission in the
  database → `permissions_changed` if the token claims it (for "any member", if it has an
  `org_id`), otherwise `forbidden`.
- Produces: `ApiResults` (`Ok`, `Created`, `Accepted`, `NoContent`, `Error(code)`,
  `Refused(outcome)`, `InvalidRequest`, `NotFound`), the same no-store results as the
  account endpoints (`AccountResults.NoStoreResult`, made `internal` in Task 5);
  `NoStoreMiddleware.UseNoStoreForTenancyPaths()`; and `EndpointMetadata` — `ErrorBody`,
  `ProducesError(status, codes)`, `ProducesUnauthorized()`, `ReadsJson<T>()` — which only
  *describe* an endpoint for the OpenAPI document of Task 14.
- Produces: `GET /auth/me` (any member), `GET /auth/org` (any member), `PATCH /auth/org`
  `{"name"}` (`org:manage`, `204`); `MeResponse`, `OrgResponse`, `RenameOrgRequest`.
  `MapTenancyApi()` maps them; the next tasks add their routes to it.
- Produces (tests): `TenancyApi` (a request with a bearer token, and the assertions of the
  contract: `AssertErrorAsync`, `AssertEmptyAsync`, `AssertUnauthorizedAsync`, `ReadOkAsync`).

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/BearerValidationTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Microsoft.IdentityModel.Tokens;

namespace Auth.IntegrationTests;

public sealed class BearerValidationTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private static string Tamper(string jwt, Action<JsonObject> change)
    {
        var parts = jwt.Split('.');
        var payload = AccessTokens.Payload(jwt);
        change(payload);
        return string.Join('.', parts[0], Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payload.ToJsonString())), parts[2]);
    }

    private async Task<HttpResponseMessage> MeAsync(string? token) => await TenancyApi.Get(Client, OrgEndpoints.MePath, token);

    [Fact]
    public async Task A_valid_token_is_accepted()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        using var response = await MeAsync(session.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Request_without_a_token_is_a_401_with_an_empty_body_and_a_bearer_challenge()   // criterion 14
    {
        using var response = await MeAsync(null);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_that_is_not_a_bearer_token_is_a_401()
    {
        using var request = TenancyApi.Request(HttpMethod.Get, OrgEndpoints.MePath, null);
        request.Headers.TryAddWithoutValidation("Authorization", "Basic Zm9vOmJhcg==");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_with_a_changed_signature_is_a_401()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);
        var parts = session.AccessToken.Split('.');

        using var response = await MeAsync(string.Join('.', parts[0], parts[1], (parts[2][0] == 'A' ? 'B' : 'A') + parts[2][1..]));

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_with_a_changed_payload_is_a_401()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        // The roles are rewritten, the signature stays: whoever does this must not get the company of someone else.
        using var response = await MeAsync(Tamper(session.AccessToken, payload => payload["org_id"] = Guid.NewGuid().ToString()));

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("a.b.c")]
    [InlineData("")]
    public async Task Something_that_is_not_a_token_is_a_401(string token)
    {
        using var response = await MeAsync(token);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_without_a_signature_is_a_401()
    {
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"at+jwt"}""");
        var payload = Base64UrlEncoder.Encode(AccessTokens.Payload((await SessionApi.LoginAsync(Client, Factory)).AccessToken).ToJsonString());

        using var response = await MeAsync($"{header}.{payload}.");

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_for_another_audience_is_a_401()   // without AddAudiences it would pass
    {
        await using var other = new AuthAppFactory(Postgres, Keys).WithSetting("Auth:Tokens:Audience", "some-other-product");
        using var otherClient = SessionApi.CreateClient(other);
        var foreign = await LoginApi.LoginToken(otherClient, other);

        using var response = await MeAsync(foreign);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_from_another_issuer_is_a_401()
    {
        await using var other = new AuthAppFactory(Postgres, Keys).WithSetting("Auth:Tokens:Issuer", "http://evil.example/auth");
        using var otherClient = SessionApi.CreateClient(other);
        var foreign = await LoginApi.LoginToken(otherClient, other);

        using var response = await MeAsync(foreign);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task An_expired_token_is_a_401()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        Clock.Advance(TimeSpan.FromMinutes(9));
        using (var live = await MeAsync(session.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }

        Clock.Advance(TimeSpan.FromMinutes(2));
        using var response = await MeAsync(session.AccessToken);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task The_challenge_of_a_bad_token_names_the_bearer_scheme_and_the_error_of_rfc_6750()
    {
        using var response = await MeAsync("garbage");

        var challenge = Assert.Single(response.Headers.WwwAuthenticate);
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains("invalid_token", challenge.Parameter);
    }

    [Fact]
    public async Task A_refresh_token_cookie_is_not_a_bearer_token()
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        using var response = await MeAsync(session.RefreshToken);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }
}
```

`tests/Auth.IntegrationTests/Infrastructure/TenancyApi.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Drives the endpoints of spec 0005 over HTTP with a bearer token, and asserts their response contracts.</summary>
public static class TenancyApi
{
    public static HttpRequestMessage Request(HttpMethod method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string? token, object? body = null)
    {
        using var request = Request(method, path, token, body);
        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> Get(HttpClient client, string path, string? token) => Send(client, HttpMethod.Get, path, token);

    /// <summary>Asserts the status, that the answer is never stored and sets no cookie, and returns the raw body.</summary>
    public static async Task<string> ReadAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {(int)status}, got {(int)response.StatusCode}: {raw}");
        AccountApi.AssertNeverStoredAndNoCookie(response);
        return raw;
    }

    /// <summary>A <c>200</c> with a JSON body; returns it.</summary>
    public static async Task<JsonElement> ReadOkAsync(HttpResponseMessage response)
    {
        var raw = await ReadAsync(response, HttpStatusCode.OK);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    /// <summary>A <c>201</c> with a JSON body; returns it.</summary>
    public static async Task<JsonElement> ReadCreatedAsync(HttpResponseMessage response)
    {
        var raw = await ReadAsync(response, HttpStatusCode.Created);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    /// <summary>The error of the contract: this status and exactly <c>{"error":"code"}</c>.</summary>
    public static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var raw = await ReadAsync(response, status);
        Assert.Equal($$"""{"error":"{{code}}"}""", raw);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>A <c>202</c> or <c>204</c>: no body.</summary>
    public static async Task AssertEmptyAsync(HttpResponseMessage response, HttpStatusCode status) =>
        Assert.Equal("", await ReadAsync(response, status));

    /// <summary>The <c>401</c> of a missing or invalid token: an empty body and a bearer challenge, never stored.</summary>
    public static async Task AssertUnauthorizedAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"Expected 401, got {(int)response.StatusCode}: {raw}");
        Assert.Equal("", raw);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }
}
```

`tests/Auth.IntegrationTests/OrgEndpointTests.cs`:

```csharp
using System.Net;
using System.Text;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class OrgEndpointTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private async Task<string> AdminTokenAsync() => (await SessionApi.LoginAsync(Client, Factory)).AccessToken;

    private async Task<string> TokenOfAsync(string email) => (await SessionApi.LoginAsync(Client, email, UserPassword)).AccessToken;

    [Fact]
    public async Task Me_returns_the_callers_current_data_from_the_database()   // criterion 19
    {
        var company = await DevCompanyIdAsync();
        var token = await AdminTokenAsync();

        using var response = await TenancyApi.Get(Client, OrgEndpoints.MePath, token);

        var me = await TenancyApi.ReadOkAsync(response);
        Assert.Equal(["email", "org_id", "org_name", "permissions", "roles", "sub"], me.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal((await Factory.SeedUserIdAsync()).ToString(), me.GetProperty("sub").GetString());
        Assert.Equal(Factory.SeedEmail, me.GetProperty("email").GetString());
        Assert.Equal(company.ToString(), me.GetProperty("org_id").GetString());
        Assert.Equal("Development", me.GetProperty("org_name").GetString());
        Assert.Equal(["admin"], me.GetProperty("roles").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(
            ["members:manage", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            me.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Me_follows_the_database_not_the_token()   // criterion 19
    {
        var company = await DevCompanyIdAsync();
        var worker = await AddMemberAsync(company, "worker@example.com", "admin");
        var token = await TokenOfAsync("worker@example.com");

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "user"));   // no refresh yet
        using var response = await TenancyApi.Get(Client, OrgEndpoints.MePath, token);

        var me = await TenancyApi.ReadOkAsync(response);
        Assert.Equal(["user"], me.GetProperty("roles").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["reports:approve", "reports:read"], me.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Me_of_someone_who_is_no_longer_a_member_is_permissions_changed()   // spec 0005 → GET /auth/me
    {
        var company = await DevCompanyIdAsync();
        var leaver = await AddMemberAsync(company, "leaver@example.com", "user");
        var token = await TokenOfAsync("leaver@example.com");

        await RemoveMembershipAsync(leaver);
        using var response = await TenancyApi.Get(Client, OrgEndpoints.MePath, token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
    }

    [Fact]
    public async Task Org_returns_the_callers_company_to_any_member()   // criterion 19
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "worker@example.com", "user");

        using var response = await TenancyApi.Get(Client, OrgEndpoints.OrgPath, await TokenOfAsync("worker@example.com"));

        var org = await TenancyApi.ReadOkAsync(response);
        Assert.Equal(["id", "name"], org.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(company.ToString(), org.GetProperty("id").GetString());
        Assert.Equal("Development", org.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Org_shows_the_company_of_the_caller_and_not_another()
    {
        var other = await CreateCompanyAsync("Globex");
        await AddMemberAsync(other, "boss@globex.test", "admin");

        using var response = await TenancyApi.Get(Client, OrgEndpoints.OrgPath, await TokenOfAsync("boss@globex.test"));

        var org = await TenancyApi.ReadOkAsync(response);
        Assert.Equal(other.ToString(), org.GetProperty("id").GetString());
        Assert.Equal("Globex", org.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Rename_changes_the_name_for_org_manage_and_only_that_company()   // criterion 19
    {
        var other = await CreateCompanyAsync("Globex");
        var token = await AdminTokenAsync();

        using (var renamed = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, token, new { name = "Acme Development" }))
        {
            await TenancyApi.AssertEmptyAsync(renamed, HttpStatusCode.NoContent);
        }

        using var after = await TenancyApi.Get(Client, OrgEndpoints.OrgPath, token);
        Assert.Equal("Acme Development", (await TenancyApi.ReadOkAsync(after)).GetProperty("name").GetString());
        Assert.Equal("Globex", await InDbAsync(async db => (await db.Companies.SingleAsync(c => c.Id == other, TestContext.Current.CancellationToken)).Name));
    }

    [Fact]
    public async Task Rename_without_org_manage_is_forbidden()   // criterion 14
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "worker@example.com", "user");

        using var response = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, await TokenOfAsync("worker@example.com"), new { name = "Mine now" });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task A_demoted_caller_with_an_old_token_gets_permissions_changed_and_with_a_new_one_forbidden()   // criterion 14
    {
        var company = await DevCompanyIdAsync();
        var worker = await AddMemberAsync(company, "worker@example.com", "admin");
        var session = await SessionApi.LoginAsync(Client, "worker@example.com", UserPassword);

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "user"));

        using (var stale = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, session.AccessToken, new { name = "Hijacked" }))
        {
            await TenancyApi.AssertErrorAsync(stale, HttpStatusCode.Forbidden, "permissions_changed");
        }

        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using var fresh = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, refreshed.AccessToken, new { name = "Hijacked" });
        await TenancyApi.AssertErrorAsync(fresh, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task A_promoted_caller_may_use_the_new_permission_before_refreshing()   // the database decides
    {
        var company = await DevCompanyIdAsync();
        var worker = await AddMemberAsync(company, "worker@example.com", "user");
        var token = await TokenOfAsync("worker@example.com");

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "admin"));
        using var response = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, token, new { name = "Promoted" });

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_removed_member_with_a_live_token_gets_permissions_changed_on_every_endpoint()   // criterion 13, 14
    {
        var company = await DevCompanyIdAsync();
        var leaver = await AddMemberAsync(company, "leaver@example.com", "admin");
        var token = await TokenOfAsync("leaver@example.com");

        await RemoveMembershipAsync(leaver);

        using (var org = await TenancyApi.Get(Client, OrgEndpoints.OrgPath, token))
        {
            await TenancyApi.AssertErrorAsync(org, HttpStatusCode.Forbidden, "permissions_changed");
        }

        using var rename = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, token, new { name = "Gone" });
        await TenancyApi.AssertErrorAsync(rename, HttpStatusCode.Forbidden, "permissions_changed");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Padded")]
    [InlineData("Padded ")]
    [InlineData("Ac\tme")]
    public async Task Rename_to_a_name_that_breaks_the_rules_is_a_400(string name)
    {
        using var response = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, await AdminTokenAsync(), new { name });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Rename_to_a_name_of_101_characters_is_a_400_and_of_100_is_accepted()
    {
        var token = await AdminTokenAsync();

        using (var long101 = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, token, new { name = new string('a', 101) }))
        {
            await TenancyApi.AssertErrorAsync(long101, HttpStatusCode.BadRequest, "invalid_request");
        }

        using var long100 = await TenancyApi.Send(Client, HttpMethod.Patch, OrgEndpoints.OrgPath, token, new { name = new string('a', 100) });
        await TenancyApi.AssertEmptyAsync(long100, HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"name":42}""")]
    [InlineData("""{"name":null}""")]
    [InlineData("""["name"]""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task Rename_with_a_malformed_body_is_a_400(string body)
    {
        using var request = TenancyApi.Request(HttpMethod.Patch, OrgEndpoints.OrgPath, await AdminTokenAsync());
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Rename_with_the_wrong_content_type_or_an_oversized_body_is_a_400()
    {
        var token = await AdminTokenAsync();
        using (var request = TenancyApi.Request(HttpMethod.Patch, OrgEndpoints.OrgPath, token))
        {
            request.Content = new StringContent("""{"name":"Acme"}""", Encoding.UTF8, "text/plain");
            using var response = await Client.SendAsync(request);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        }

        using var big = TenancyApi.Request(HttpMethod.Patch, OrgEndpoints.OrgPath, token);
        big.Content = new StringContent("{\"name\":\"Acme\",\"pad\":\"" + new string('x', 9 * 1024) + "\"}", Encoding.UTF8, "application/json");
        using var oversized = await Client.SendAsync(big);
        await TenancyApi.AssertErrorAsync(oversized, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Theory]
    [InlineData("GET", OrgEndpoints.MePath)]
    [InlineData("GET", OrgEndpoints.OrgPath)]
    [InlineData("PATCH", OrgEndpoints.OrgPath)]
    public async Task Every_endpoint_answers_401_without_a_valid_token(string method, string path)   // criterion 14
    {
        using var response = await TenancyApi.Send(Client, new HttpMethod(method), path, null, method == "PATCH" ? new { name = "x" } : null);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task Authorisation_comes_before_the_shape_of_the_request()   // spec 0005 → the order of checks
    {
        var company = await DevCompanyIdAsync();
        await AddMemberAsync(company, "worker@example.com", "user");
        using var request = TenancyApi.Request(HttpMethod.Patch, OrgEndpoints.OrgPath, await TokenOfAsync("worker@example.com"));
        request.Content = new StringContent("not json", Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");   // not 400
    }

    [Fact]
    public async Task An_unknown_path_under_org_is_the_frameworks_empty_404_and_is_still_never_stored()   // spec 0005 → Responses
    {
        using var response = await TenancyApi.Get(Client, "/auth/org/nothing-here", await AdminTokenAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }

    [Fact]
    public async Task Answers_never_set_the_refresh_cookie_and_are_never_stored()
    {
        var token = await AdminTokenAsync();

        using var response = await TenancyApi.Get(Client, OrgEndpoints.OrgPath, token);

        AccountApi.AssertNeverStoredAndNoCookie(response);
    }

    [Fact]
    public async Task A_method_the_path_does_not_have_is_a_405_that_is_still_never_stored()
    {
        using var response = await TenancyApi.Send(Client, HttpMethod.Delete, OrgEndpoints.OrgPath, await AdminTokenAsync());

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`OrgEndpoints` does not exist).

- [ ] **Step 3: Implement.** Three things here are deliberate. Authorisation comes before
  validation: a caller who may not do something is told so (`401`/`403`) before they are
  told their body is malformed. A request body is documented with `ReadsJson<T>()` and
  not with `Accepts<T>()`, which would make the routing answer `415` to a wrong content
  type, where the contract says `400 invalid_request`. And the middleware that marks
  responses never to be stored runs before authentication, because the `401` is written
  by the authentication handler.

`src/Auth.Server/Api/ApiResults.cs`:

```csharp
using Auth.Server.Account;
using Auth.Server.Lockout;
using Auth.Server.Tenancy;

namespace Auth.Server.Api;

/// <summary>
/// The responses of the company API and the invitation endpoints (spec 0005 → General rules): never stored, never a
/// cookie, an error is <c>{"error":"&lt;code&gt;"}</c>. The same results as the account endpoints use, so the headers
/// are the same.
/// </summary>
public static class ApiResults
{
    public static IResult Ok(object body) => new AccountResults.NoStoreResult(StatusCodes.Status200OK, body);

    public static IResult Created(object body) => new AccountResults.NoStoreResult(StatusCodes.Status201Created, body);

    public static IResult Accepted() => AccountResults.Accepted();

    public static IResult NoContent() => AccountResults.Done();

    /// <summary>The error with this code, at the status the contract gives it.</summary>
    public static IResult Error(string code) =>
        new AccountResults.NoStoreResult(TenancyErrors.StatusOf(code), new { error = code });

    /// <summary>A refusal of a service: its code, or the <c>429</c> of the mail limit with the time to wait.</summary>
    public static IResult Refused(Outcome outcome)
    {
        if (outcome.Succeeded)
        {
            throw new InvalidOperationException("A successful outcome is not a refusal.");
        }

        return outcome.Error == TenancyErrors.TooManyAttempts
            ? new TooManyAttemptsResult(outcome.RetryAfter)
            : Error(outcome.Error!);
    }

    /// <summary>The answer for a request body that is not what the endpoint takes.</summary>
    public static IResult InvalidRequest() => Error(TenancyErrors.InvalidRequest);

    /// <summary>The answer for an id that does not exist in the caller's company.</summary>
    public static IResult NotFound() => Error(TenancyErrors.NotFound);
}
```

`src/Auth.Server/Api/EndpointMetadata.cs`:

```csharp
namespace Auth.Server.Api;

/// <summary>The body of every error of the API: <c>{"error":"&lt;code&gt;"}</c>.</summary>
public sealed record ErrorBody(string Error);

/// <summary>The codes an endpoint answers with at a status, so that the OpenAPI description can name them.</summary>
public sealed record ErrorCodesMetadata(int Status, IReadOnlyList<string> Codes);

/// <summary>The JSON body an endpoint reads, for the OpenAPI description only.</summary>
public sealed record JsonRequestMetadata(Type Body);

public static class EndpointMetadata
{
    /// <summary>
    /// Documents the JSON body the endpoint reads. It is not <c>Accepts</c> on purpose: that would make the routing
    /// answer <c>415</c> to another content type, and the contract is <c>400 invalid_request</c>, said by the handler.
    /// </summary>
    public static RouteHandlerBuilder ReadsJson<T>(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithMetadata(new JsonRequestMetadata(typeof(T)));
    }

    /// <summary>Documents an error status of the endpoint and the codes it carries.</summary>
    public static RouteHandlerBuilder ProducesError(this RouteHandlerBuilder builder, int status, params string[] codes)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Produces<ErrorBody>(status, "application/json").WithMetadata(new ErrorCodesMetadata(status, codes));
    }

    /// <summary>The <c>401</c> of an endpoint that needs an access token: an empty body and a <c>WWW-Authenticate: Bearer</c> challenge.</summary>
    public static RouteHandlerBuilder ProducesUnauthorized(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Produces(StatusCodes.Status401Unauthorized);
    }
}
```

`src/Auth.Server/Api/NoStoreMiddleware.cs`:

```csharp
using Microsoft.Net.Http.Headers;

namespace Auth.Server.Api;

/// <summary>
/// Marks every response under the paths of spec 0005 as never to be stored, including the ones no handler of ours
/// writes: the <c>401</c> of a missing or invalid token, a <c>404</c>, a <c>405</c>. It runs before authentication
/// for that reason; the handlers' own results set the same headers.
/// </summary>
public static class NoStoreMiddleware
{
    private static readonly string[] Prefixes = ["/auth/me", "/auth/org", "/auth/invites"];

    public static IApplicationBuilder UseNoStoreForTenancyPaths(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            if (Prefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[HeaderNames.CacheControl] = "no-store";
                    context.Response.Headers[HeaderNames.Pragma] = "no-cache";
                    return Task.CompletedTask;
                });
            }

            await next(context);
        });
    }
}
```

`src/Auth.Server/Api/OrgEndpoints.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Api;

/// <summary>
/// <c>GET /auth/me</c>, <c>GET /auth/org</c> and <c>PATCH /auth/org</c> (spec 0005 → Company API): who the caller is,
/// which company, and its name. Every answer is read from the database, never from the token.
/// </summary>
public static class OrgEndpoints
{
    public const string MePath = "/auth/me";
    public const string OrgPath = "/auth/org";

    private static readonly string[] RenameFields = ["name"];

    public static async Task<IResult> MeAsync(HttpContext http, MembershipReader memberships, UserManager<ApplicationUser> users)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(users);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        var user = await users.FindByIdAsync(caller.UserId.ToString());
        if (user?.Email is null)
        {
            return ApiResults.Error(TenancyErrors.Forbidden);
        }

        return ApiResults.Ok(new MeResponse(caller.UserId, user.Email, caller.CompanyId, caller.CompanyName, [caller.RoleName], [.. caller.Permissions]));
    }

    public static async Task<IResult> GetAsync(HttpContext http, MembershipReader memberships)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships);
        return access.Caller is { } caller
            ? ApiResults.Ok(new OrgResponse(caller.CompanyId, caller.CompanyName))
            : access.Failure!;
    }

    public static async Task<IResult> RenameAsync(HttpContext http, MembershipReader memberships, AuthDbContext db)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(db);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.OrgManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, RenameFields, http.RequestAborted);
        if (fields is null || !NameInput.IsValid(fields[0]))
        {
            return ApiResults.InvalidRequest();
        }

        var name = fields[0];
        await db.Companies.Where(c => c.Id == caller.CompanyId)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, name), http.RequestAborted);
        return ApiResults.NoContent();
    }
}
```

`src/Auth.Server/Api/TenancyEndpoints.cs`:

```csharp
using Auth.Server.Tenancy;

namespace Auth.Server.Api;

public static class TenancyEndpoints
{
    /// <summary>
    /// The endpoints of spec 0005: the caller's own data and the company API. Everything under <c>/auth/org</c> acts on
    /// the caller's company, taken from the database, and needs an access token; the invitation endpoints that need
    /// none are mapped beside it.
    /// </summary>
    public static IEndpointRouteBuilder MapTenancyApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(OrgEndpoints.MePath, OrgEndpoints.MeAsync)
            .RequireAuthorization()
            .Produces<MeResponse>()
            .ProducesUnauthorized()
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionsChanged, TenancyErrors.Forbidden);

        var company = app.MapGroup(OrgEndpoints.OrgPath).RequireAuthorization();

        company.MapGet("", OrgEndpoints.GetAsync)
            .Produces<OrgResponse>()
            .ProducesUnauthorized()
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionsChanged, TenancyErrors.Forbidden);
        company.MapPatch("", OrgEndpoints.RenameAsync)
            .ReadsJson<RenameOrgRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesUnauthorized()
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionsChanged, TenancyErrors.Forbidden);

        return app;
    }
}
```

`src/Auth.Server/Program.cs` — the change:

```diff
--- a/src/Auth.Server/Program.cs
+++ b/src/Auth.Server/Program.cs
@@ -1,6 +1,8 @@
 using Auth.Infrastructure;
+using System.Text.Json;
 using Auth.Infrastructure.Persistence;
 using Auth.Server.Account;
+using Auth.Server.Api;
 using Auth.Server.Email;
 using Auth.Server.Keys;
 using Auth.Server.Lockout;
@@ -20,6 +22,9 @@
 builder.Services.AddSingleton(keys);
 // Fail fast on missing or invalid mail settings too.
 builder.Services.AddSingleton(MailSettingsLoader.Load(builder.Configuration, builder.Environment.IsDevelopment()));
+// JSON property names are snake_case (spec 0005 → General rules). The bodies written before are unaffected: their
+// property names already are.
+builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
 builder.Services.AddHealthChecks().AddCheck<ManifestHealthCheck>("manifest");
 builder.Services.AddTenancy(builder.Configuration, builder.Environment.ContentRootPath);
 builder.Services.AddAuthPersistence(builder.Configuration);
@@ -57,7 +62,13 @@
 
 await DevUserSeeder.SeedAsync(app.Services, app.Lifetime.ApplicationStopping);
 
+// Before authentication, so that the 401 of a missing or invalid token is marked never to be stored too.
+app.UseNoStoreForTenancyPaths();
+app.UseAuthentication();
+app.UseAuthorization();
+
 app.MapHealthChecks("/auth/health");
+app.MapTenancyApi();
 app.MapPost(JsonLoginRequestHandler.LoginPath, LoginEndpoint.HandleAsync);
 app.MapPost(RefreshRequestHandler.RefreshPath, RefreshEndpoint.HandleAsync);
 app.MapPost(LogoutEndpoint.LogoutPath, LogoutEndpoint.HandleAsync);
```

`src/Auth.Server/Tenancy/CompanyAccess.cs`:

```csharp
using Auth.Server.Api;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Tenancy;

/// <summary>The caller of a company API endpoint, or the answer that refuses them.</summary>
public readonly record struct Access(TenantContext? Caller, IResult? Failure);

/// <summary>
/// The permission check of the company API (spec 0005 → General rules). The token proves who the caller is; what they
/// may do is read from the database on every call: their membership, company and role at this moment.
/// <list type="bullet">
/// <item>The database grants the permission: the call proceeds, whatever the token says.</item>
/// <item>It does not, but the token claims it: the caller was demoted or removed since the token was issued, and
/// the answer is <c>403 permissions_changed</c>, so that the frontend refreshes and redraws.</item>
/// <item>Neither grants it: <c>403 forbidden</c>.</item>
/// </list>
/// </summary>
public static class CompanyAccess
{
    /// <param name="anyOf">The call needs one of these permissions; none means any member will do.</param>
    public static async Task<Access> AuthorizeAsync(HttpContext http, MembershipReader memberships, params string[] anyOf)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);

        // The sub of a token this service issued is always a user id; anything else is not a caller we know.
        if (!Guid.TryParse(http.User.FindFirst(Claims.Subject)?.Value, out var userId))
        {
            return new Access(null, ApiResults.Error(TenancyErrors.Forbidden));
        }

        var tenant = await memberships.ReadAsync(userId, http.RequestAborted);
        if (tenant is not null && (anyOf.Length == 0 || anyOf.Any(tenant.Holds)))
        {
            return new Access(tenant, null);
        }

        // Refused. If the token says the caller could, the token is out of date; if it never said so, it is simply no.
        var claimed = anyOf.Length == 0
            ? http.User.HasClaim(c => c.Type == TenantClaims.OrgId)
            : http.User.FindAll(TenantClaims.Permissions).Any(c => anyOf.Contains(c.Value, StringComparer.Ordinal));
        return new Access(null, ApiResults.Error(claimed ? TenancyErrors.PermissionsChanged : TenancyErrors.Forbidden));
    }
}
```

`src/Auth.Server/Tenancy/Contracts.cs`:

```csharp
namespace Auth.Server.Tenancy;

// The bodies of the company API (spec 0005 → Contract). Property names become snake_case on the wire.

/// <summary>Response of <c>GET /auth/me</c>: the caller as the database says now.</summary>
public sealed record MeResponse(Guid Sub, string Email, Guid OrgId, string OrgName, string[] Roles, string[] Permissions);

/// <summary>Response of <c>GET /auth/org</c>.</summary>
public sealed record OrgResponse(Guid Id, string Name);

/// <summary>Request of <c>PATCH /auth/org</c>.</summary>
public sealed record RenameOrgRequest(string Name);
```

`src/Auth.Server/Tokens/OpenIddictSetup.cs` — the change:

```diff
--- a/src/Auth.Server/Tokens/OpenIddictSetup.cs
+++ b/src/Auth.Server/Tokens/OpenIddictSetup.cs
@@ -4,6 +4,7 @@
 using Auth.Server.Sessions;
 using OpenIddict.Server;
 using OpenIddict.Server.AspNetCore;
+using OpenIddict.Validation.AspNetCore;
 
 namespace Auth.Server.Tokens;
 
@@ -103,7 +104,18 @@
                 options.UseAspNetCore()
                     .EnableTokenEndpointPassthrough()
                     .DisableTransportSecurityRequirement();
+            })
+            // The company API takes the service's own access tokens as a consumer would: signature against the
+            // instance's keys, issuer, audience and expiry. Without AddAudiences a token for another product would pass.
+            .AddValidation(options =>
+            {
+                options.UseLocalServer();
+                options.UseAspNetCore();
+                options.AddAudiences(tokens.Audience);
             });
+
+        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
+        services.AddAuthorization();
 
         return services;
     }
```

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`,
  then `dotnet format --verify-no-changes`. Expected: PASS — 44 new tests, 552 in all.
  This is the end of Day 2.

- [ ] **Step 5: Commit** — `feat(tenancy): validate bearer tokens, check permissions against the database; /auth/me and /auth/org`

---

## Day 3 — invitations, end to end

When the day is done a manager can invite an address, list, resend and cancel
invitations; the invited person gets a mail in Polish or English, previews the invitation
and accepts it with a password; and expired invitations are pruned.

### Task 7: The invitation mail

**Files:**
- Create: `src/Auth.Server/Email/MailLimits.cs`, `src/Auth.Server/Tenancy/InviteTokens.cs`,
  `tests/Auth.IntegrationTests/InvitationMailComposerTests.cs`,
  `tests/Auth.IntegrationTests/InvitationDispatchTests.cs`
- Modify: `src/Auth.Server/Email/MailSettings.cs`, `MailTexts.cs`, `MailComposer.cs`,
  `EmailTokens.cs`, `MailRequestStore.cs`, `MailDispatcher.cs`,
  `src/Auth.Server/appsettings.Development.json`,
  `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`,
  `Infrastructure/TenancyTestBase.cs`, `MailSettingsTests.cs`, `MailComposerTests.cs`,
  `SmtpMailTransportTests.cs`

**Interfaces:**
- Consumes: Task 2 (`MailKind.Invitation`, `MailRequest.InviteId`, `Invite`), Task 4
  (`TenancyTestBase`).
- Produces: setting `Auth:App:FrontendUrls:AcceptInvite` (`MailSettingsLoader.AcceptInviteUrlKey`,
  `MailSettings.AcceptInviteUrl`), **required** like the other two frontend URLs, `https`
  outside Development, no fragment, no `token` parameter. A host without it does not start,
  so every host of the tests, the development settings and (Task 15) the compose stack set it.
- Produces: the invitation mail, Polish and English (`MailTexts.For(locale, MailKind.Invitation)`):
  a subject with the application's name only, an intro with `{0}` application, `{1}` company
  and `{2}` role, the link, "valid for 7 days", and the sentence for someone who did not
  expect it. `MailComposer.Compose(kind, recipient, token, string? companyName = null,
  string? roleName = null)`; the names are required for an invitation. The company and role
  names are typed by members: they are arguments of the format and never its template, they
  are encoded in the HTML part with the rest of the intro, and no header carries them.
- Produces: `EmailTokens.NewToken()` (the 32-random-bytes token, now shared);
  `MailLimits.RegisterAsync(db, identifierHash, kind, now, ct)` — the limit logic of
  `MailRequestStore` made reusable inside the caller's transaction, the store itself now
  calls it; `InviteTokens` with `Lifetime` (7 days), `IssueAsync(db, inviteId, now, ct)`
  (sets `TokenHash` and `ExpiresAt` on the locked invitation row) and
  `LimitIdentifierOf(companyId, normalizedEmail)` (a domain-separated SHA-256, so that the
  limit is per company and address and can never equal the per-address hash of spec 0004).
- Changes: `MailDispatcher` handles `MailKind.Invitation`. Such a request names an
  invitation, not an account: the pass that drops requests for addresses without an account
  leaves it alone; it is selected whether or not the address has an account; the dispatcher
  locks the invitation row (`FOR UPDATE`), issues the token **into the invitation**, composes
  the mail to the address on the invitation and sends it; a failed send rolls the token back
  to the savepoint exactly as for a reset, so the earlier link stays in force; a request whose
  invitation is gone (cancelled) is dropped without a mail. Because the seven days start at
  the mail, an invitation is made with a provisional expiry and gets its real one here.
- Produces (tests): `TenancyTestBase.AddInviteAsync`, `EnqueueInvitationAsync`, `InviteAsync`;
  `AuthAppFactory.DefaultInviteUrl`.

- [ ] **Step 1: Write the failing tests and make the test changes.** The three existing
  files construct `MailSettings` or its configuration: each gains the one new value.

`tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs
+++ b/tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs
@@ -51,6 +51,7 @@
         _settings[MailSettingsLoader.LocaleKey] = "en";
         _settings[MailSettingsLoader.ResetPasswordUrlKey] = DefaultResetUrl;
         _settings[MailSettingsLoader.VerifyEmailUrlKey] = DefaultVerifyUrl;
+        _settings[MailSettingsLoader.AcceptInviteUrlKey] = DefaultInviteUrl;
         _settings[MailSettingsLoader.FromKey] = DefaultFrom;
         _settings[MailSettingsLoader.SmtpHostKey] = "smtp.invalid";
         _settings[MailSettingsLoader.SmtpPortKey] = "587";
@@ -86,6 +87,7 @@
     public const string DefaultAppName = "Auth-Core Test";
     public const string DefaultResetUrl = "https://app.example.com/reset";
     public const string DefaultVerifyUrl = "https://app.example.com/verify";
+    public const string DefaultInviteUrl = "https://app.example.com/invite";
     public const string DefaultFrom = "no-reply@example.com";
 
     /// <summary>The effective seed email: the default, or the value set via <see cref="WithSetting"/>.</summary>
```

`tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs
+++ b/tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs
@@ -3,6 +3,7 @@
 using Auth.Server.Requests;
 using Auth.Server.Seeding;
 using Auth.Server.Tenancy;
+using Microsoft.AspNetCore.Identity;
 using Microsoft.EntityFrameworkCore;
 using Microsoft.Extensions.DependencyInjection;
 
@@ -45,6 +46,50 @@
             .SingleAsync(TestContext.Current.CancellationToken));
     }
 
+    /// <summary>Makes an invitation straight in the database, as the API would, with no token yet.</summary>
+    protected async Task<Guid> AddInviteAsync(Guid companyId, string email, string role, Guid? invitedBy = null)
+    {
+        var roleId = await RoleIdAsync(companyId, role);
+        using var scope = Factory.Services.CreateScope();
+        var normalized = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>().NormalizeEmail(email);
+        var now = StorableTime.Now(Clock);
+        var invite = new Invite
+        {
+            Id = Guid.NewGuid(),
+            CompanyId = companyId,
+            Email = email,
+            NormalizedEmail = normalized,
+            RoleId = roleId,
+            InvitedBy = invitedBy,
+            InvitedAt = now,
+            ExpiresAt = now + InviteTokens.Lifetime,
+        };
+        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
+        db.Invites.Add(invite);
+        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
+        return invite.Id;
+    }
+
+    /// <summary>Puts the mail of an invitation on the queue, due now, as sending or resending does.</summary>
+    protected Task EnqueueInvitationAsync(Guid inviteId, string email) =>
+        InDbAsync(async db =>
+        {
+            var now = StorableTime.Now(Clock);
+            db.MailRequests.Add(new MailRequest
+            {
+                Kind = MailKind.Invitation,
+                NormalizedEmail = email.ToUpperInvariant(),
+                InviteId = inviteId,
+                RequestedAt = now,
+                NextAttemptAt = now,
+            });
+            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
+            return 0;
+        });
+
+    protected Task<Invite?> InviteAsync(Guid inviteId) =>
+        InDbAsync(db => db.Invites.AsNoTracking().SingleOrDefaultAsync(i => i.Id == inviteId, TestContext.Current.CancellationToken));
+
     /// <summary>Changes what a role holds, straight in the database.</summary>
     protected Task SetRolePermissionsAsync(Guid roleId, params string[] permissions) =>
         InDbAsync(async db =>
```

`tests/Auth.IntegrationTests/InvitationDispatchTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class InvitationDispatchTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Address = "worker@acme.test";

    private Task<List<MailRequest>> QueueAsync() =>
        InDbAsync(db => db.MailRequests.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    private async Task<(Guid Company, Guid Invite)> InvitedAsync(string companyName = "Acme", string role = "user", string address = Address)
    {
        var company = await CreateCompanyAsync(companyName);
        var invite = await AddInviteAsync(company, address, role);
        await EnqueueInvitationAsync(invite, address);
        return (company, invite);
    }

    [Fact]
    public async Task Invitation_for_an_address_without_an_account_is_mailed_and_not_dropped()   // criterion 2
    {
        var (_, invite) = await InvitedAsync();

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Address, mail.To);
        Assert.Contains(AuthAppFactory.DefaultInviteUrl + "?token=", mail.TextBody);
        Assert.Equal("Auth-Core Test", AuthAppFactory.DefaultAppName);
        Assert.Contains("Auth-Core Test", mail.Subject);
        Assert.Contains("Acme", mail.TextBody);
        Assert.Contains("as user", mail.TextBody);
        Assert.Contains("valid for 7 days", mail.TextBody);
        Assert.Empty(await QueueAsync());
        Assert.NotNull((await InviteAsync(invite))!.TokenHash);
    }

    [Fact]
    public async Task Only_the_hash_of_the_token_is_stored_and_the_seven_days_start_at_the_mail()   // criteria 2, 23
    {
        var company = await CreateCompanyAsync();
        var invite = await AddInviteAsync(company, Address, "user");
        Clock.Advance(TimeSpan.FromHours(3));   // the invitation was made earlier than its mail
        await EnqueueInvitationAsync(invite, Address);

        await DispatchAsync();

        var row = (await InviteAsync(invite))!;
        var token = TokenIn(Assert.Single(Mail.Sent));
        Assert.Equal(43, token.Length);
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);
        Assert.Equal(StorableTime.Now(Clock) + InviteTokens.Lifetime, row.ExpiresAt);
        Assert.Equal(TimeSpan.FromDays(7), InviteTokens.Lifetime);

        // The token in clear is in no row of any table that could hold it.
        var dump = await InDbAsync(db => db.Database.SqlQuery<string>(
            $"""
            SELECT coalesce(string_agg(row_to_json(i)::text, ' '), '') AS "Value" FROM "Invites" i
            UNION ALL SELECT coalesce(string_agg(row_to_json(m)::text, ' '), '') FROM "MailRequests" m
            UNION ALL SELECT coalesce(string_agg(row_to_json(t)::text, ' '), '') FROM "EmailTokens" t
            """).ToListAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(token, string.Join(' ', dump));
    }

    [Fact]
    public async Task Invitation_to_an_address_that_has_an_account_is_mailed_too()
    {
        await CreateUserAsync(Address, confirmed: true, member: false);
        await InvitedAsync();

        await DispatchAsync();

        Assert.Equal(Address, Assert.Single(Mail.Sent).To);
    }

    [Fact]
    public async Task Recipient_is_the_address_as_the_inviter_typed_it()
    {
        await InvitedAsync(address: "Anna.Nowak@Acme.Test");

        await DispatchAsync();

        Assert.Equal("Anna.Nowak@Acme.Test", Assert.Single(Mail.Sent).To);
    }

    [Fact]
    public async Task A_new_mail_replaces_the_token_of_the_earlier_one_and_restarts_the_seven_days()   // criterion 10
    {
        var (_, invite) = await InvitedAsync();
        await DispatchAsync();
        var first = TokenIn(Assert.Single(Mail.Sent));

        Clock.Advance(TimeSpan.FromDays(1));
        await EnqueueInvitationAsync(invite, Address);
        await DispatchAsync();

        var second = TokenIn(Mail.Sent[1]);
        var row = (await InviteAsync(invite))!;
        Assert.NotEqual(first, second);
        Assert.Equal(EmailTokens.HashOf(second), row.TokenHash);
        Assert.NotEqual(EmailTokens.HashOf(first), row.TokenHash);
        Assert.Equal(StorableTime.Now(Clock) + InviteTokens.Lifetime, row.ExpiresAt);
    }

    [Fact]
    public async Task Failed_send_is_retried_on_the_schedule_and_the_earlier_link_stays_in_force()   // spec 0004, Decision 13
    {
        var (_, invite) = await InvitedAsync();
        await DispatchAsync();
        var first = TokenIn(Assert.Single(Mail.Sent));
        var expiry = (await InviteAsync(invite))!.ExpiresAt;

        Clock.Advance(TimeSpan.FromHours(1));
        await EnqueueInvitationAsync(invite, Address);
        Mail.Failing = true;
        await DispatchAsync();

        var kept = (await InviteAsync(invite))!;
        Assert.Equal(EmailTokens.HashOf(first), kept.TokenHash);   // the failed attempt left no new token behind
        Assert.Equal(expiry, kept.ExpiresAt);
        var queued = Assert.Single(await QueueAsync());
        Assert.Equal(1, queued.Attempts);

        Mail.Failing = false;
        Clock.Advance(MailDelivery.RetryDelay(1));
        await DispatchAsync();

        Assert.Equal(2, Mail.Sent.Count);
        Assert.Equal(EmailTokens.HashOf(TokenIn(Mail.Sent[1])), (await InviteAsync(invite))!.TokenHash);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Request_for_an_invitation_that_was_cancelled_is_dropped_without_a_mail()
    {
        var (_, invite) = await InvitedAsync();
        await InDbAsync(db => db.Invites.Where(i => i.Id == invite).ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, await DispatchAsync());

        Assert.Empty(Mail.Sent);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Request_without_an_invitation_is_dropped_without_a_mail()
    {
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            db.MailRequests.Add(new MailRequest { Kind = MailKind.Invitation, NormalizedEmail = "X@Y.Z", RequestedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        await DispatchAsync();

        Assert.Empty(Mail.Sent);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Names_with_markup_are_encoded_in_the_mail_and_stay_out_of_the_headers()   // criterion 23
    {
        var company = await CreateCompanyAsync("Tom & <Jerry>");
        await InDbAsync(db => db.CompanyRoles.Where(r => r.CompanyId == company && r.Name == "user")
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Name, "<b>boss</b>").SetProperty(r => r.NormalizedName, "<B>BOSS</B>"), TestContext.Current.CancellationToken));
        var invite = await InDbAsync(async db =>
        {
            var role = await db.CompanyRoles.AsNoTracking().SingleAsync(r => r.CompanyId == company && r.Name == "<b>boss</b>", TestContext.Current.CancellationToken);
            var now = StorableTime.Now(Clock);
            var row = new Invite
            {
                Id = Guid.NewGuid(),
                CompanyId = company,
                Email = Address,
                NormalizedEmail = Address.ToUpperInvariant(),
                RoleId = role.Id,
                InvitedAt = now,
                ExpiresAt = now + InviteTokens.Lifetime,
            };
            db.Invites.Add(row);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return row.Id;
        });
        await EnqueueInvitationAsync(invite, Address);

        await DispatchAsync();

        var mail = Assert.Single(Mail.Sent);
        Assert.Contains("Tom &amp; &lt;Jerry&gt;", mail.HtmlBody);
        Assert.Contains("&lt;b&gt;boss&lt;/b&gt;", mail.HtmlBody);
        Assert.DoesNotContain("Jerry", mail.Subject + mail.To);
        Assert.DoesNotContain("boss", mail.Subject + mail.To);
    }

    [Fact]
    public async Task No_token_and_no_address_reach_the_log()   // criterion 23
    {
        await InvitedAsync();
        Mail.Failing = true;
        await DispatchAsync();
        Mail.Failing = false;
        Clock.Advance(MailDelivery.RetryDelay(1));
        await DispatchAsync();

        var token = TokenIn(Assert.Single(Mail.Sent));
        Assert.DoesNotContain(token, Logs.Text);
        Assert.DoesNotContain(Address, Logs.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Logs.Entries, e => e.Message.Contains("Invitation"));   // the failure was logged, with the kind
    }

    [Fact]
    public async Task Requests_for_accounts_are_handled_as_before_beside_an_invitation()
    {
        await InvitedAsync();
        await EnqueueAsync(MailKind.PasswordReset, Factory.SeedEmail);
        await EnqueueAsync(MailKind.PasswordReset, "nobody@example.com");

        Assert.Equal(3, await DispatchAsync());

        Assert.Equal(2, Mail.Sent.Count);
        Assert.Contains(Mail.Sent, m => m.To == Address);
        Assert.Contains(Mail.Sent, m => m.To == Factory.SeedEmail);
    }
}
```

`tests/Auth.IntegrationTests/InvitationMailComposerTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

public sealed class InvitationMailComposerTests
{
    private const string Token = "abcDEF123_-abcDEF123_-abcDEF123_-abcDEF123_";

    private static MailComposer Composer(string locale = "en", string appName = "speech-to-mail", string invite = "https://app.example.com/invite") =>
        new(new MailSettings
        {
            AppName = appName,
            Locale = locale,
            ResetPasswordUrl = new Uri("https://app.example.com/reset"),
            VerifyEmailUrl = new Uri("https://app.example.com/verify"),
            AcceptInviteUrl = new Uri(invite),
            FromAddress = "no-reply@example.com",
            Smtp = new SmtpSettings { Host = "smtp.invalid", Port = 587, Security = SmtpSecurity.StartTls },
        });

    [Fact]
    public void Invitation_in_english_names_the_application_the_company_the_role_the_link_and_the_lifetime()   // criterion 2
    {
        var mail = Composer().Compose(MailKind.Invitation, "worker@acme.test", Token, "Acme", "user");

        Assert.Equal("worker@acme.test", mail.To);
        Assert.Equal("You are invited to speech-to-mail", mail.Subject);
        Assert.Contains("speech-to-mail", mail.TextBody);
        Assert.Contains("Acme", mail.TextBody);
        Assert.Contains("as user", mail.TextBody);
        Assert.Contains($"https://app.example.com/invite?token={Token}", mail.TextBody);
        Assert.Contains("valid for 7 days", mail.TextBody);
        Assert.Contains("ignore this message", mail.TextBody);
        Assert.Contains($"href=\"https://app.example.com/invite?token={Token}\"", mail.HtmlBody);
        Assert.Contains("valid for 7 days", mail.HtmlBody);
        Assert.Contains("<html lang=\"en\">", mail.HtmlBody);
        Assert.DoesNotContain("{", mail.Subject + mail.TextBody + mail.HtmlBody);
    }

    [Fact]
    public void Invitation_in_polish_keeps_its_letters()   // criterion 2
    {
        var mail = Composer("pl").Compose(MailKind.Invitation, "worker@acme.test", Token, "Żółw i Spółka", "kierownik");

        Assert.Equal("Zaproszenie do speech-to-mail", mail.Subject);
        Assert.Contains("Żółw i Spółka", mail.TextBody);
        Assert.Contains("jako kierownik", mail.TextBody);
        Assert.Contains($"https://app.example.com/invite?token={Token}", mail.TextBody);
        Assert.Contains("ważny przez 7 dni", mail.TextBody);
        Assert.Contains("zignoruj tę wiadomość", mail.TextBody);
        Assert.Contains("Żółw i Spółka", mail.HtmlBody);      // letters stay letters, not entities
        Assert.Contains("ważny przez 7 dni", mail.HtmlBody);
        Assert.Contains("<html lang=\"pl\">", mail.HtmlBody);
    }

    [Fact]
    public void Company_and_role_names_are_encoded_in_the_html_part_and_never_in_a_header()   // criterion 23
    {
        var mail = Composer().Compose(MailKind.Invitation, "worker@acme.test", Token, "Tom & <Jerry>", "<b>boss</b>");

        Assert.Contains("Tom & <Jerry>", mail.TextBody);
        Assert.Contains("Tom &amp; &lt;Jerry&gt;", mail.HtmlBody);
        Assert.Contains("&lt;b&gt;boss&lt;/b&gt;", mail.HtmlBody);
        Assert.DoesNotContain("<Jerry>", mail.HtmlBody);
        Assert.DoesNotContain("<b>boss</b>", mail.HtmlBody);
        // The subject is a header: the names a member typed are not in it, nor in the recipient.
        Assert.Equal("You are invited to speech-to-mail", mail.Subject);
        Assert.DoesNotContain("Jerry", mail.Subject + mail.To);
        Assert.DoesNotContain("boss", mail.Subject + mail.To);
    }

    [Fact]
    public void A_name_that_looks_like_a_format_placeholder_is_printed_as_it_is()
    {
        var mail = Composer().Compose(MailKind.Invitation, "worker@acme.test", Token, "{0}{1}{2}", "{2}");

        Assert.Contains("{0}{1}{2}", mail.TextBody);
        Assert.Contains("as {2}.", mail.TextBody);
    }

    [Fact]
    public void Link_keeps_a_query_string_the_invitation_screen_already_has()
    {
        var mail = Composer(invite: "https://app.example.com/invite?lang=pl").Compose(MailKind.Invitation, "worker@acme.test", Token, "Acme", "user");

        Assert.Contains($"https://app.example.com/invite?lang=pl&token={Token}", mail.TextBody);
    }

    [Fact]
    public void An_invitation_without_its_company_or_role_cannot_be_composed()
    {
        Assert.Throws<ArgumentException>(() => Composer().Compose(MailKind.Invitation, "worker@acme.test", Token));
        Assert.Throws<ArgumentException>(() => Composer().Compose(MailKind.Invitation, "worker@acme.test", Token, "Acme"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public void Every_language_has_every_text_of_an_invitation(string locale)
    {
        var text = MailTexts.For(locale, MailKind.Invitation);

        Assert.All(
            new[] { text.Subject, text.Greeting, text.Intro, text.Button, text.Validity, text.LinkHint, text.Ignore },
            value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Contains("{0}", text.Subject);
        Assert.DoesNotContain("{1}", text.Subject);   // no header carries what a member typed
        Assert.DoesNotContain("{2}", text.Subject);
        Assert.Contains("{1}", text.Intro);
        Assert.Contains("{2}", text.Intro);
    }

    [Fact]
    public void The_other_mails_do_not_take_a_company_or_a_role_into_account()
    {
        var mail = Composer().Compose(MailKind.PasswordReset, "user@example.com", Token, "Acme", "admin");

        Assert.DoesNotContain("Acme", mail.TextBody);
        Assert.Contains("https://app.example.com/reset?token=", mail.TextBody);
    }
}
```

`tests/Auth.IntegrationTests/MailComposerTests.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/MailComposerTests.cs
+++ b/tests/Auth.IntegrationTests/MailComposerTests.cs
@@ -14,6 +14,7 @@
             Locale = locale,
             ResetPasswordUrl = new Uri(reset),
             VerifyEmailUrl = new Uri("https://app.example.com/verify"),
+            AcceptInviteUrl = new Uri("https://app.example.com/invite"),
             FromAddress = "no-reply@example.com",
             Smtp = new SmtpSettings { Host = "smtp.invalid", Port = 587, Security = SmtpSecurity.StartTls },
         });
```

`tests/Auth.IntegrationTests/MailSettingsTests.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/MailSettingsTests.cs
+++ b/tests/Auth.IntegrationTests/MailSettingsTests.cs
@@ -15,6 +15,7 @@
         [MailSettingsLoader.LocaleKey] = "pl",
         [MailSettingsLoader.ResetPasswordUrlKey] = "https://app.example.com/reset",
         [MailSettingsLoader.VerifyEmailUrlKey] = "https://app.example.com/verify",
+        [MailSettingsLoader.AcceptInviteUrlKey] = "https://app.example.com/invite",
         [MailSettingsLoader.FromKey] = "no-reply@example.com",
         [MailSettingsLoader.SmtpHostKey] = "smtp.example.com",
         [MailSettingsLoader.SmtpPortKey] = "587",
@@ -35,6 +36,7 @@
         Assert.Equal("pl", settings.Locale);
         Assert.Equal(new Uri("https://app.example.com/reset"), settings.ResetPasswordUrl);
         Assert.Equal(new Uri("https://app.example.com/verify"), settings.VerifyEmailUrl);
+        Assert.Equal(new Uri("https://app.example.com/invite"), settings.AcceptInviteUrl);
         Assert.Equal("no-reply@example.com", settings.FromAddress);
         Assert.Equal("smtp.example.com", settings.Smtp.Host);
         Assert.Equal(587, settings.Smtp.Port);
@@ -48,6 +50,7 @@
     [InlineData(MailSettingsLoader.LocaleKey)]
     [InlineData(MailSettingsLoader.ResetPasswordUrlKey)]
     [InlineData(MailSettingsLoader.VerifyEmailUrlKey)]
+    [InlineData(MailSettingsLoader.AcceptInviteUrlKey)]
     [InlineData(MailSettingsLoader.FromKey)]
     [InlineData(MailSettingsLoader.SmtpHostKey)]
     [InlineData(MailSettingsLoader.SmtpPortKey)]
@@ -71,6 +74,8 @@
     [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "ftp://app.example.com/reset")]
     [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "http://app.example.com/reset")]     // not https outside Development
     [InlineData(MailSettingsLoader.VerifyEmailUrlKey, "https://app.example.com/verify#top")] // a fragment would swallow the token
+    [InlineData(MailSettingsLoader.AcceptInviteUrlKey, "http://app.example.com/invite")]       // not https outside Development
+    [InlineData(MailSettingsLoader.AcceptInviteUrlKey, "https://app.example.com/invite?token=x")]   // the link adds its own
     [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "https://user:secret@app.example.com/reset")]
     [InlineData(MailSettingsLoader.ResetPasswordUrlKey, "https://app.example.com/reset?token=x")]         // the link adds its own
     [InlineData(MailSettingsLoader.FromKey, "not an address")]
```

`tests/Auth.IntegrationTests/SmtpMailTransportTests.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/SmtpMailTransportTests.cs
+++ b/tests/Auth.IntegrationTests/SmtpMailTransportTests.cs
@@ -14,6 +14,7 @@
         Locale = "pl",
         ResetPasswordUrl = new Uri("https://app.example.com/reset"),
         VerifyEmailUrl = new Uri("https://app.example.com/verify"),
+        AcceptInviteUrl = new Uri("https://app.example.com/invite"),
         FromAddress = "no-reply@mail.example.com",
         Smtp = new SmtpSettings { Host = mailpit.Host, Port = port ?? mailpit.SmtpPort, Security = SmtpSecurity.None },
     };
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`MailSettings.AcceptInviteUrl`, `MailSettingsLoader.AcceptInviteUrlKey`
  and `InviteTokens` do not exist).

- [ ] **Step 3: Implement.**

`src/Auth.Server/Email/EmailTokens.cs` — the change:

```diff
--- a/src/Auth.Server/Email/EmailTokens.cs
+++ b/src/Auth.Server/Email/EmailTokens.cs
@@ -31,6 +31,9 @@
         return SHA256.HashData(Encoding.UTF8.GetBytes(token));
     }
 
+    /// <summary>A new token in clear: 32 random bytes as 43 base64url characters. Invitations (spec 0005) use it too.</summary>
+    public static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
+
     /// <summary>
     /// Replaces the user's token of this kind by a new one and returns it in clear: 32 random bytes as 43
     /// base64url characters. The caller puts it into a mail and nowhere else. One statement on the row the table
@@ -41,7 +44,7 @@
     {
         ArgumentNullException.ThrowIfNull(db);
 
-        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
+        var token = NewToken();
         var hash = HashOf(token);
         var kindValue = (short)kind;
         var expiresAt = now + LifetimeOf(kind);
```

`src/Auth.Server/Email/MailComposer.cs` — the change:

```diff
--- a/src/Auth.Server/Email/MailComposer.cs
+++ b/src/Auth.Server/Email/MailComposer.cs
@@ -19,24 +19,38 @@
 }
 
 /// <summary>
-/// Builds the two mails of spec 0004 in the configured language, each as plain text and as HTML with the same
-/// content. Only configuration and the token go into a mail; nothing a requester submitted does.
+/// Builds the mails of specs 0004 and 0005 in the configured language, each as plain text and as HTML with the same
+/// content. Only configuration and the token go into a mail; nothing a requester submitted does. An invitation also
+/// names its company and role, which members typed: they are encoded in the HTML part and are never in a header.
 /// </summary>
 public sealed class MailComposer(MailSettings settings)
 {
     // Every letter stays a letter (Polish diacritics included); only markup characters are escaped.
     private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);
 
-    public ComposedMail Compose(MailKind kind, string recipient, string token)
+    /// <param name="companyName">The company of an invitation; required for <see cref="MailKind.Invitation"/>, ignored otherwise.</param>
+    /// <param name="roleName">The role of an invitation; required for <see cref="MailKind.Invitation"/>, ignored otherwise.</param>
+    public ComposedMail Compose(MailKind kind, string recipient, string token, string? companyName = null, string? roleName = null)
     {
         ArgumentNullException.ThrowIfNull(recipient);
         ArgumentNullException.ThrowIfNull(token);
+        if (kind == MailKind.Invitation && (companyName is null || roleName is null))
+        {
+            throw new ArgumentException("An invitation names its company and its role.", nameof(kind));
+        }
 
         var text = MailTexts.For(settings.Locale, kind);
-        var target = kind == MailKind.PasswordReset ? settings.ResetPasswordUrl : settings.VerifyEmailUrl;
+        var target = kind switch
+        {
+            MailKind.PasswordReset => settings.ResetPasswordUrl,
+            MailKind.EmailVerification => settings.VerifyEmailUrl,
+            _ => settings.AcceptInviteUrl,
+        };
         var link = QueryHelpers.AddQueryString(target.AbsoluteUri, "token", token);
+        // The subject takes the application name only: the company and the role are typed by members, and a header is
+        // not the place for that. They appear in the body, encoded in its HTML part.
         var subject = string.Format(CultureInfo.InvariantCulture, text.Subject, settings.AppName);
-        var intro = string.Format(CultureInfo.InvariantCulture, text.Intro, settings.AppName);
+        var intro = string.Format(CultureInfo.InvariantCulture, text.Intro, settings.AppName, companyName, roleName);
 
         return new ComposedMail
         {
```

`src/Auth.Server/Email/MailDispatcher.cs` — the change:

```diff
--- a/src/Auth.Server/Email/MailDispatcher.cs
+++ b/src/Auth.Server/Email/MailDispatcher.cs
@@ -1,4 +1,5 @@
 using Auth.Infrastructure.Persistence;
+using Auth.Server.Tenancy;
 using Microsoft.EntityFrameworkCore;
 
 namespace Auth.Server.Email;
@@ -10,7 +11,9 @@
 /// transaction with the row locked: issue a token, compose the mail and send it. Only such rows (and rows an hour
 /// old, which are dropped) are handled singly: requests that need no mail and arrive during the pass wait for the
 /// bulk delete of the next one. The token is committed only once the server has accepted the mail, so a failed
-/// attempt leaves the earlier link in force and no clear token is ever stored (Decision 13).
+/// attempt leaves the earlier link in force and no clear token is ever stored (Decision 13). An invitation request
+/// (spec 0005) names an invitation, not an account: its mail goes to the address on the invitation, the token is
+/// issued into the invitation, and the request is never dropped for want of an account.
 /// </summary>
 public sealed partial class MailDispatcher(
     IServiceScopeFactory scopes, TimeProvider clock, MailComposer composer, IMailTransport transport, ILogger<MailDispatcher> logger)
@@ -43,6 +46,7 @@
     {
         var now = StorableTime.Now(clock);
         var passwordReset = (short)MailKind.PasswordReset;
+        var invitation = (short)MailKind.Invitation;
 
         await using var scope = scopes.CreateAsyncScope();
         var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
@@ -51,6 +55,7 @@
             DELETE FROM "MailRequests" WHERE "Id" IN (
                 SELECT r."Id" FROM "MailRequests" r
                 WHERE r."NextAttemptAt" <= {now}
+                  AND r."Kind" <> {invitation}
                   AND NOT EXISTS (
                       SELECT 1 FROM "AspNetUsers" u
                       WHERE u."NormalizedEmail" = r."NormalizedEmail"
@@ -77,15 +82,17 @@
         // is never handled one by one ahead of the real mails. SKIP LOCKED: a second instance takes another row
         // instead of waiting for this one.
         var passwordReset = (short)MailKind.PasswordReset;
+        var invitation = (short)MailKind.Invitation;
         var due = await db.MailRequests
             .FromSql($"""
                 SELECT r.* FROM "MailRequests" r
                 WHERE (r."NextAttemptAt" <= {now}
-                       AND EXISTS (
-                           SELECT 1 FROM "AspNetUsers" u
-                           WHERE u."NormalizedEmail" = r."NormalizedEmail"
-                             AND u."Email" IS NOT NULL
-                             AND (r."Kind" = {passwordReset} OR NOT u."EmailConfirmed")))
+                       AND (r."Kind" = {invitation}
+                            OR EXISTS (
+                                SELECT 1 FROM "AspNetUsers" u
+                                WHERE u."NormalizedEmail" = r."NormalizedEmail"
+                                  AND u."Email" IS NOT NULL
+                                  AND (r."Kind" = {passwordReset} OR NOT u."EmailConfirmed"))))
                    OR r."RequestedAt" <= {givenUp}
                 ORDER BY r."NextAttemptAt", r."Id"
                 LIMIT 1
@@ -106,12 +113,13 @@
             return true;
         }
 
-        var user = await db.Users.AsNoTracking()
-            .OrderBy(u => u.Id)
-            .FirstOrDefaultAsync(u => u.NormalizedEmail == request.NormalizedEmail, cancellationToken);
-        if (user?.Email is null || (request.Kind == MailKind.EmailVerification && user.EmailConfirmed))
-        {
-            // The account went away, or was confirmed, after the pass began.
+        // What it takes to make this request's mail, or nothing when there is no mail to make any more.
+        var makeMail = request.Kind == MailKind.Invitation
+            ? await PrepareInvitationAsync(db, request, now, cancellationToken)
+            : await PrepareAccountMailAsync(db, request, now, cancellationToken);
+        if (makeMail is null)
+        {
+            // The account went away, or was confirmed, or the invitation was cancelled, after the pass began.
             await RemoveAsync(db, request, cancellationToken);
             await transaction.CommitAsync(cancellationToken);
             return true;
@@ -125,9 +133,7 @@
 
         try
         {
-            var token = await EmailTokens.IssueAsync(db, user.Id, request.Kind, now, cancellationToken);
-            var mail = composer.Compose(request.Kind, user.Email, token);
-            await transport.SendAsync(mail, cancellationToken);
+            await transport.SendAsync(await makeMail(), cancellationToken);
         }
         catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
         {
@@ -148,6 +154,56 @@
         return true;
     }
 
+    /// <summary>The mail of a reset or a verification: to the stored address of the account, with a token issued to it.</summary>
+    private async Task<Func<Task<ComposedMail>>?> PrepareAccountMailAsync(
+        AuthDbContext db, MailRequest request, DateTimeOffset now, CancellationToken cancellationToken)
+    {
+        var user = await db.Users.AsNoTracking()
+            .OrderBy(u => u.Id)
+            .FirstOrDefaultAsync(u => u.NormalizedEmail == request.NormalizedEmail, cancellationToken);
+        if (user?.Email is null || (request.Kind == MailKind.EmailVerification && user.EmailConfirmed))
+        {
+            return null;
+        }
+
+        return async () =>
+        {
+            var token = await EmailTokens.IssueAsync(db, user.Id, request.Kind, now, cancellationToken);
+            return composer.Compose(request.Kind, user.Email, token);
+        };
+    }
+
+    /// <summary>
+    /// The mail of an invitation (spec 0005): to the address on the invitation, which may have no account, with a token
+    /// issued to the invitation. The invitation row is locked, so that cancelling or accepting it waits for the mail.
+    /// </summary>
+    private async Task<Func<Task<ComposedMail>>?> PrepareInvitationAsync(
+        AuthDbContext db, MailRequest request, DateTimeOffset now, CancellationToken cancellationToken)
+    {
+        if (request.InviteId is not { } inviteId)
+        {
+            return null;
+        }
+
+        var invites = await db.Invites
+            .FromSql($"""SELECT * FROM "Invites" WHERE "Id" = {inviteId} FOR UPDATE""")
+            .AsNoTracking()
+            .ToListAsync(cancellationToken);
+        if (invites.Count == 0)
+        {
+            return null;
+        }
+
+        var invite = invites[0];
+        var companyName = await db.Companies.AsNoTracking().Where(c => c.Id == invite.CompanyId).Select(c => c.Name).SingleAsync(cancellationToken);
+        var roleName = await db.CompanyRoles.AsNoTracking().Where(r => r.Id == invite.RoleId).Select(r => r.Name).SingleAsync(cancellationToken);
+        return async () =>
+        {
+            var token = await InviteTokens.IssueAsync(db, invite.Id, now, cancellationToken);
+            return composer.Compose(MailKind.Invitation, invite.Email, token, companyName, roleName);
+        };
+    }
+
     private static async Task RemoveAsync(AuthDbContext db, MailRequest request, CancellationToken cancellationToken)
     {
         db.MailRequests.Remove(request);
```

`src/Auth.Server/Email/MailLimits.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// Applies the mail limit to one identifier and kind inside the caller's transaction: one statement creates the limit
/// row or locks the existing one (the pattern of <c>LoginStreakStore</c>), the rules decide, and an accepted request
/// changes the tracked row. The caller saves, so the limit and what it lets through commit together. Shared by the
/// queue of spec 0004 (an identifier per address) and the invitations of spec 0005 (one per company and address).
/// </summary>
public static class MailLimits
{
    public static async Task<MailLimitDecision> RegisterAsync(
        AuthDbContext db, byte[] identifierHash, MailKind kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(identifierHash);

        var kindValue = (short)kind;

        // ToListAsync, not SingleAsync: EF must send the statement as it is.
        var rows = await db.MailRequestLimits
            .FromSql($"""
                INSERT INTO "MailRequestLimits" ("IdentifierHash", "Kind", "WindowStartedAt", "WindowCount", "LastAcceptedAt")
                VALUES ({identifierHash}, {kindValue}, {now}, 0, {now})
                ON CONFLICT ("IdentifierHash", "Kind") DO UPDATE SET "WindowCount" = "MailRequestLimits"."WindowCount"
                RETURNING *
                """)
            .ToListAsync(cancellationToken);
        var row = rows.Single();

        var (next, decision) = MailLimitPolicy.Register(
            new MailLimitState(row.WindowStartedAt, row.WindowCount, row.LastAcceptedAt), now);
        if (decision.Allowed)
        {
            row.WindowStartedAt = next.WindowStartedAt;
            row.WindowCount = next.WindowCount;
            row.LastAcceptedAt = next.LastAcceptedAt;
        }

        return decision;
    }
}
```

`src/Auth.Server/Email/MailRequestStore.cs` — the change:

```diff
--- a/src/Auth.Server/Email/MailRequestStore.cs
+++ b/src/Auth.Server/Email/MailRequestStore.cs
@@ -18,31 +18,15 @@
 
         var now = StorableTime.Now(clock);
         var identifierHash = LoginIdentifier.HashOf(normalizedEmail);
-        var kindValue = (short)kind;
 
         await using var scope = scopes.CreateAsyncScope();
         var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
         await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
 
-        // One statement creates the row or locks the existing one, and returns it either way (the pattern of
-        // LoginStreakStore). ToListAsync, not SingleAsync: EF must send the statement as it is.
-        var rows = await db.MailRequestLimits
-            .FromSql($"""
-                INSERT INTO "MailRequestLimits" ("IdentifierHash", "Kind", "WindowStartedAt", "WindowCount", "LastAcceptedAt")
-                VALUES ({identifierHash}, {kindValue}, {now}, 0, {now})
-                ON CONFLICT ("IdentifierHash", "Kind") DO UPDATE SET "WindowCount" = "MailRequestLimits"."WindowCount"
-                RETURNING *
-                """)
-            .ToListAsync(cancellationToken);
-        var row = rows.Single();
-
-        var (next, decision) = MailLimitPolicy.Register(
-            new MailLimitState(row.WindowStartedAt, row.WindowCount, row.LastAcceptedAt), now);
+        // The limit row is created or locked by one statement (MailLimits), and returned either way.
+        var decision = await MailLimits.RegisterAsync(db, identifierHash, kind, now, cancellationToken);
         if (decision.Allowed)
         {
-            row.WindowStartedAt = next.WindowStartedAt;
-            row.WindowCount = next.WindowCount;
-            row.LastAcceptedAt = next.LastAcceptedAt;
             db.MailRequests.Add(new MailRequest { Kind = kind, NormalizedEmail = normalizedEmail, RequestedAt = now, NextAttemptAt = now });
             await db.SaveChangesAsync(cancellationToken);
         }
```

`src/Auth.Server/Email/MailSettings.cs` — the change:

```diff
--- a/src/Auth.Server/Email/MailSettings.cs
+++ b/src/Auth.Server/Email/MailSettings.cs
@@ -45,6 +45,9 @@
 
     public required Uri VerifyEmailUrl { get; init; }
 
+    /// <summary>The product's invitation screen (spec 0005): the link of an invitation mail is this URL plus <c>token</c>.</summary>
+    public required Uri AcceptInviteUrl { get; init; }
+
     public required string FromAddress { get; init; }
 
     public required SmtpSettings Smtp { get; init; }
@@ -56,6 +59,7 @@
     public const string LocaleKey = "Auth:App:Locale";
     public const string ResetPasswordUrlKey = "Auth:App:FrontendUrls:ResetPassword";
     public const string VerifyEmailUrlKey = "Auth:App:FrontendUrls:VerifyEmail";
+    public const string AcceptInviteUrlKey = "Auth:App:FrontendUrls:AcceptInvite";
     public const string FromKey = "Auth:Email:From";
     public const string SmtpHostKey = "Auth:Email:Smtp:Host";
     public const string SmtpPortKey = "Auth:Email:Smtp:Port";
@@ -125,6 +129,7 @@
             Locale = locale,
             ResetPasswordUrl = FrontendUrl(configuration, ResetPasswordUrlKey, isDevelopment),
             VerifyEmailUrl = FrontendUrl(configuration, VerifyEmailUrlKey, isDevelopment),
+            AcceptInviteUrl = FrontendUrl(configuration, AcceptInviteUrlKey, isDevelopment),
             FromAddress = from,
             Smtp = new SmtpSettings
             {
```

`src/Auth.Server/Email/MailTexts.cs` — the change:

```diff
--- a/src/Auth.Server/Email/MailTexts.cs
+++ b/src/Auth.Server/Email/MailTexts.cs
@@ -2,7 +2,11 @@
 
 namespace Auth.Server.Email;
 
-/// <summary>The texts of one mail. <see cref="Subject"/> and <see cref="Intro"/> hold <c>{0}</c> for the application name.</summary>
+/// <summary>
+/// The texts of one mail. <see cref="Subject"/> and <see cref="Intro"/> hold <c>{0}</c> for the application name; an
+/// invitation's <see cref="Intro"/> also holds <c>{1}</c> for the company and <c>{2}</c> for the role. Those two are
+/// set by members, so no subject uses them: nothing a member typed goes into a mail header.
+/// </summary>
 public sealed record MailText(string Subject, string Greeting, string Intro, string Button, string Validity, string LinkHint, string Ignore);
 
 /// <summary>
@@ -45,6 +49,22 @@
             Validity: "Link jest ważny przez 24 godziny i działa jeden raz.",
             LinkHint: "Jeśli przycisk nie działa, skopiuj ten adres do przeglądarki:",
             Ignore: "Jeśli to nie Ty, zignoruj tę wiadomość. Nic się nie zmieni, dopóki link nie zostanie użyty."),
+        ("en", MailKind.Invitation) => new MailText(
+            Subject: "You are invited to {0}",
+            Greeting: "Hello,",
+            Intro: "You have been invited to join {1} on {0} as {2}. Use the link below to choose a password and accept the invitation.",
+            Button: "Accept the invitation",
+            Validity: "The link is valid for 7 days and works once.",
+            LinkHint: "If the button does not work, copy this address into your browser:",
+            Ignore: "If you did not expect this invitation, you can ignore this message. Nothing changes until the link is used."),
+        ("pl", MailKind.Invitation) => new MailText(
+            Subject: "Zaproszenie do {0}",
+            Greeting: "Dzień dobry,",
+            Intro: "Zaproszono Cię do firmy {1} w {0} jako {2}. Użyj poniższego linku, aby ustawić hasło i przyjąć zaproszenie.",
+            Button: "Przyjmij zaproszenie",
+            Validity: "Link jest ważny przez 7 dni i działa jeden raz.",
+            LinkHint: "Jeśli przycisk nie działa, skopiuj ten adres do przeglądarki:",
+            Ignore: "Jeśli nie spodziewałeś się tego zaproszenia, zignoruj tę wiadomość. Nic się nie zmieni, dopóki link nie zostanie użyty."),
         _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No mail text for locale '{locale}' and kind '{kind}'."),
     };
 }
```

`src/Auth.Server/Tenancy/InviteTokens.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// The link tokens of invitations (spec 0005 → Invitations). They work like the link tokens of spec 0004 — 32 random
/// bytes as base64url, only the SHA-256 stored, single use — but live on the invitation, because an invitation names an
/// address that may have no account, and <see cref="EmailToken"/> rows belong to users.
/// </summary>
public static class InviteTokens
{
    /// <summary>An invitation is valid this long from the moment its mail is composed. A constant, as the lifetimes of spec 0004.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Gives the invitation a new token, replacing the one it had, and returns it in clear; the caller puts it into a mail
    /// and nowhere else. The seven days start now. The invitation row must be locked by the caller, as the dispatcher does.
    /// </summary>
    public static async Task<string> IssueAsync(AuthDbContext db, Guid inviteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var token = EmailTokens.NewToken();
        var hash = EmailTokens.HashOf(token);
        var expiresAt = now + Lifetime;
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE "Invites" SET "TokenHash" = {hash}, "ExpiresAt" = {expiresAt} WHERE "Id" = {inviteId}""",
            cancellationToken);
        return token;
    }

    /// <summary>
    /// The identifier the mail limit counts an invitation under: the company and the address together, so that one
    /// company's invitations to a person neither block nor reveal another's (spec 0005, Decision 16). Domain-separated,
    /// so that it can never equal the hash the per-address limits of spec 0004 use for the same address.
    /// </summary>
    public static byte[] LimitIdentifierOf(Guid companyId, string normalizedEmail)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        return SHA256.HashData(Encoding.UTF8.GetBytes($"invitation\0{companyId:N}\0{normalizedEmail}"));
    }
}
```

`src/Auth.Server/appsettings.Development.json` — the change:

```diff
--- a/src/Auth.Server/appsettings.Development.json
+++ b/src/Auth.Server/appsettings.Development.json
@@ -14,7 +14,8 @@
       "Locale": "en",
       "FrontendUrls": {
         "ResetPassword": "http://localhost:4200/reset",
-        "VerifyEmail": "http://localhost:4200/verify"
+        "VerifyEmail": "http://localhost:4200/verify",
+        "AcceptInvite": "http://localhost:4200/invite"
       }
     },
     "Email": {
```

  `MailRequestStore` now delegates its limit statement to `MailLimits`; the tests of spec
  0004 that exercise it (`MailRequestStoreTests`, `MailRequestEndpointTests`) must pass as
  they stand.

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS — 23 new tests, 575 in all.

- [ ] **Step 5: Commit** — `feat(tenancy): the invitation mail`

### Task 8: Send, list, resend and cancel invitations

**Files:**
- Create: `src/Auth.Server/Requests/IdInput.cs`, `src/Auth.Server/Tenancy/Actor.cs`,
  `src/Auth.Server/Tenancy/CompanyLock.cs`, `src/Auth.Server/Tenancy/InvitationService.cs`,
  `src/Auth.Server/Api/OrgInviteEndpoints.cs`,
  `tests/Auth.IntegrationTests/OrgInviteTests.cs`
- Modify: `src/Auth.Server/Requests/EmailInput.cs`, `src/Auth.Server/Tenancy/Contracts.cs`,
  `src/Auth.Server/Tenancy/TenancyServices.cs`, `src/Auth.Server/Api/TenancyEndpoints.cs`,
  `src/Auth.Server/Api/EndpointMetadata.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs`

**Interfaces:**
- Consumes: Tasks 6 and 7.
- Produces: `IdInput.TryParse(string?, out Guid)` — a UUID in its usual `8-4-4-4-12` form and
  nothing else (no braces, no missing hyphens); `EmailInput.IsMailbox(string)` — one mailbox,
  `local@domain`, no name, no list (a typo such as `bob.acme.test` is a `400` at once instead
  of an invitation that never arrives; this is stricter than the rules of spec 0004, for
  invitations only: see the open questions).
- Produces: `Actor` — a member (`Actor.Of(tenantContext)`) or `Actor.Operator` — with
  `MayGrant(stored, catalog)`, **safety rule 1**: for an operator always; for a member, a
  role holding `*` only if the member's own role holds `*`, otherwise every permission the
  role ends up with, expanded against the catalog, must be one the member holds.
  `CompanyLock.AcquireAsync(db, companyId, ct)` — `SELECT 1 … FOR UPDATE` on the company
  row, `false` when there is no such company.
- Produces: `InvitationService` (scoped):
  - `SendAsync(actor, companyId, email, normalizedEmail, roleId, ct)` → `Outcome`. One
    transaction at the default isolation level that begins with the company lock. Checks, in
    this order: the role is the company's (`not_found`), rule 1 (`permission_not_held`),
    the address is a member of the company (`already_in_org`), then an expired invitation of
    that address is replaced and a pending one stops the insert (`invite_pending`), then the
    mail limit of the company and address (`too_many_attempts`; the invitation just made is
    rolled back with it), then the queue row. It never looks at whether the address has an
    account or belongs to another company: the answer is the same for all of them.
  - `ListAsync(companyId, ct)` — the invitations that have not expired, sorted by address,
    ordinally, with their role and ISO 8601 UTC times; an invitation whose mail is still
    queued is listed too.
  - `ResendAsync(companyId, inviteId, ct)` — a new queue row for a pending invitation, under
    the same mail limit; the new token (and the new seven days) come when the mail is
    composed, and the earlier link stops working then.
  - `CancelAsync(companyId, inviteId, ct)` — deletes the invitation at once; **not**
    subject to the mail limit, so that a link can always be killed (decided by the owner: a cancel sends no mail).
- Produces: `GET`, `POST /auth/org/invites`, `POST /auth/org/invites/{id}/resend`,
  `DELETE /auth/org/invites/{id}`, all `members:manage`; `POST` answers `202` with no body
  for every address outside the company, wakes the dispatcher (`MailDispatchSignal`).
  `InviteItem`, `InvitesResponse`, `SendInviteRequest`, `RoleRef`.
- Changes: `EndpointMetadata.ProducesUnauthorized()` becomes `ProducesGuarded()`: the `401`
  and the two `403`s of the permission check that every endpoint behind a token can give.
- Produces (tests): `TenancyTestBase.AddRoleAsync`.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs
+++ b/tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs
@@ -114,6 +114,16 @@
     protected Task RemoveMembershipAsync(Guid userId) =>
         InDbAsync(db => db.Memberships.Where(m => m.UserId == userId).ExecuteDeleteAsync(TestContext.Current.CancellationToken));
 
+    /// <summary>Adds a role to a company straight in the database; returns its id.</summary>
+    protected Task<Guid> AddRoleAsync(Guid companyId, string name, params string[] permissions) =>
+        InDbAsync(async db =>
+        {
+            var role = new CompanyRole { CompanyId = companyId, Name = name, NormalizedName = NameInput.Normalize(name), Permissions = permissions };
+            db.CompanyRoles.Add(role);
+            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
+            return role.Id;
+        });
+
     /// <summary>Makes an account with <see cref="MailTestBase.UserPassword"/> and puts it into the company with the named role.</summary>
     protected async Task<Guid> AddMemberAsync(Guid companyId, string email, string role, bool confirmed = true)
     {
```

`tests/Auth.IntegrationTests/OrgInviteTests.cs`:

```csharp
using System.Net;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class OrgInviteTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string InvitesPath = "/auth/org/invites";
    private const string Boss = "boss@acme.test";
    private const string Worker = "worker@acme.test";

    /// <summary>A company "Acme" with an admin, who is logged in.</summary>
    private async Task<(Guid Company, Guid AdminId, string Token)> AcmeAsync()
    {
        var company = await CreateCompanyAsync("Acme");
        var admin = await AddMemberAsync(company, Boss, "admin");
        return (company, admin, (await SessionApi.LoginAsync(Client, Boss, UserPassword)).AccessToken);
    }

    private async Task<HttpResponseMessage> InviteAsync(string token, string email, Guid roleId) =>
        await TenancyApi.Send(Client, HttpMethod.Post, InvitesPath, token, new { email, role_id = roleId });

    private async Task<string> InviteOkAsync(string token, string email, Guid roleId)
    {
        using var response = await InviteAsync(token, email, roleId);
        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        return email;
    }

    private async Task<System.Text.Json.JsonElement> ListAsync(string token)
    {
        using var response = await TenancyApi.Get(Client, InvitesPath, token);
        return await TenancyApi.ReadOkAsync(response);
    }

    private Task<List<Invite>> InvitesAsync() =>
        InDbAsync(db => db.Invites.AsNoTracking().OrderBy(i => i.Email).ToListAsync(TestContext.Current.CancellationToken));

    private Task<List<MailRequest>> QueueAsync() =>
        InDbAsync(db => db.MailRequests.AsNoTracking().OrderBy(r => r.Id).ToListAsync(TestContext.Current.CancellationToken));

    private static async Task<string> FingerprintAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => !string.Equals(h.Key, "Date", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}: {string.Join(",", h.Value)}");
        return $"{(int)response.StatusCode}\n{string.Join("\n", headers)}\n{await response.Content.ReadAsStringAsync()}";
    }

    // ---- sending

    [Fact]
    public async Task Manager_invites_an_address_and_a_mail_follows()   // criterion 2
    {
        var (company, _, token) = await AcmeAsync();

        await InviteOkAsync(token, Worker, await RoleIdAsync(company, "user"));
        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Worker, mail.To);
        Assert.Contains(AuthAppFactory.DefaultInviteUrl + "?token=", mail.TextBody);
        Assert.Contains("Acme", mail.TextBody);
        Assert.Contains("as user", mail.TextBody);
        Assert.Contains("valid for 7 days", mail.TextBody);
    }

    [Fact]
    public async Task Invitation_records_the_inviter_the_address_as_typed_and_the_role()
    {
        var (company, admin, token) = await AcmeAsync();

        await InviteOkAsync(token, "Anna.Worker@Acme.Test", await RoleIdAsync(company, "user"));

        var invite = Assert.Single(await InvitesAsync());
        Assert.Equal("Anna.Worker@Acme.Test", invite.Email);
        Assert.Equal("ANNA.WORKER@ACME.TEST", invite.NormalizedEmail);
        Assert.Equal(admin, invite.InvitedBy);
        Assert.Equal(company, invite.CompanyId);
        Assert.Equal(await RoleIdAsync(company, "user"), invite.RoleId);
        Assert.Null(invite.TokenHash);   // no token until the mail is composed
        Assert.Equal(StorableTime.Now(Clock) + InviteTokens.Lifetime, invite.ExpiresAt);
        var queued = Assert.Single(await QueueAsync());
        Assert.Equal(MailKind.Invitation, queued.Kind);
        Assert.Equal(invite.Id, queued.InviteId);
    }

    [Fact]
    public async Task Answer_is_the_same_for_every_address_outside_the_company()   // criterion 8
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");
        var elsewhere = await CreateCompanyAsync("Globex");
        await AddMemberAsync(elsewhere, "carol@globex.test", "user");
        await AddMemberAsync(elsewhere, "dave@globex.test", "user", confirmed: false);
        await CreateUserAsync("erin@nowhere.test", confirmed: true, member: false);

        var answers = new List<string>();
        foreach (var address in new[] { "nobody@nowhere.test", "carol@globex.test", "dave@globex.test", "erin@nowhere.test" })
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            using var response = await InviteAsync(token, address, user);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            answers.Add(await FingerprintAsync(response));
        }

        // The same status, headers and body: the inviter learns nothing about the addresses.
        Assert.Single(answers.Distinct());
        // The same effects, too: an invitation and a queued mail each.
        Assert.Equal(4, (await InvitesAsync()).Count);
        Assert.Equal(4, (await QueueAsync()).Count);
    }

    [Fact]
    public async Task Inviting_a_member_of_the_own_company_is_already_in_org_whatever_the_spelling()   // criterion 9
    {
        var (company, _, token) = await AcmeAsync();
        await AddMemberAsync(company, Worker, "user");
        var user = await RoleIdAsync(company, "user");

        foreach (var spelling in new[] { Worker, "WORKER@ACME.TEST", "Worker@Acme.Test" })
        {
            using var response = await InviteAsync(token, spelling, user);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "already_in_org");
        }

        Assert.Empty(await InvitesAsync());
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Already_in_org_is_answered_before_the_pending_check_and_the_mail_limit()   // spec 0005 → the order of checks
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");
        await InviteOkAsync(token, Worker, user);   // a pending invitation, and a mail limit row that is still hot
        await AddMemberAsync(company, Worker, "user");

        using var response = await InviteAsync(token, Worker, user);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "already_in_org");   // not invite_pending, not 429
    }

    [Fact]
    public async Task Inviting_an_address_with_a_pending_invitation_is_invite_pending()   // criterion 9
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");
        await InviteOkAsync(token, Worker, user);

        using var again = await InviteAsync(token, Worker, user);
        using var respelled = await InviteAsync(token, "WORKER@acme.test", await RoleIdAsync(company, "admin"));

        await TenancyApi.AssertErrorAsync(again, HttpStatusCode.Conflict, "invite_pending");
        await TenancyApi.AssertErrorAsync(respelled, HttpStatusCode.Conflict, "invite_pending");
        Assert.Single(await InvitesAsync());
        Assert.Single(await QueueAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_invitation_is_not_pending_and_is_replaced(bool mailed)   // criterion 9
    {
        var (company, _, _) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");
        var token = (await SessionApi.LoginAsync(Client, Boss, UserPassword)).AccessToken;
        await InviteOkAsync(token, Worker, user);
        if (mailed)
        {
            await DispatchAsync();
        }

        Clock.Advance(InviteTokens.Lifetime);   // exactly seven days from the mail (or from the invitation, if it was never mailed)
        var fresh = (await SessionApi.LoginAsync(Client, Boss, UserPassword)).AccessToken;
        Assert.Empty(ListItems(await ListAsync(fresh)));   // it has disappeared from the list

        await InviteOkAsync(fresh, Worker, user);

        var invite = Assert.Single(await InvitesAsync());
        Assert.Equal(StorableTime.Now(Clock) + InviteTokens.Lifetime, invite.ExpiresAt);
        Assert.Single(ListItems(await ListAsync(fresh)));
    }

    private static List<System.Text.Json.JsonElement> ListItems(System.Text.Json.JsonElement body) =>
        [.. body.GetProperty("invites").EnumerateArray()];

    [Fact]
    public async Task Role_must_belong_to_the_callers_company()   // criterion 14
    {
        var (_, _, token) = await AcmeAsync();
        var elsewhere = await CreateCompanyAsync("Globex");

        using var foreign = await InviteAsync(token, Worker, await RoleIdAsync(elsewhere, "user"));
        using var unknown = await InviteAsync(token, Worker, Guid.NewGuid());

        await TenancyApi.AssertErrorAsync(foreign, HttpStatusCode.NotFound, "not_found");
        await TenancyApi.AssertErrorAsync(unknown, HttpStatusCode.NotFound, "not_found");
        Assert.Empty(await InvitesAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"email":"a@acme.test"}""")]
    [InlineData("""{"role_id":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"email":"a@acme.test","role_id":"not-a-uuid"}""")]
    [InlineData("""{"email":"a@acme.test","role_id":"11111111111111111111111111111111"}""")]       // a UUID without its hyphens is not the usual form
    [InlineData("""{"email":"a@acme.test","role_id":"{11111111-1111-1111-1111-111111111111}"}""")]
    [InlineData("""{"email":"a@acme.test","role_id":42}""")]
    [InlineData("""{"email":42,"role_id":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"email":"   ","role_id":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"email":"bob.acme.test","role_id":"11111111-1111-1111-1111-111111111111"}""")]            // not an address
    [InlineData("""{"email":"Bob <bob@acme.test>","role_id":"11111111-1111-1111-1111-111111111111"}""")]     // a name and an address
    [InlineData("""{"email":"a@acme.test, b@acme.test","role_id":"11111111-1111-1111-1111-111111111111"}""")] // two addresses
    [InlineData("""{"email":"a\u0007b@acme.test","role_id":"11111111-1111-1111-1111-111111111111"}""")]        // a control character
    [InlineData("""{"email":"a\uFFFEb@acme.test","role_id":"11111111-1111-1111-1111-111111111111"}""")]        // a noncharacter
    [InlineData("""["a@acme.test"]""")]
    [InlineData("not json")]
    public async Task Malformed_request_is_a_400_and_changes_nothing(string body)
    {
        var (_, _, token) = await AcmeAsync();
        using var request = TenancyApi.Request(HttpMethod.Post, InvitesPath, token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Empty(await InvitesAsync());
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Address_longer_than_254_characters_is_a_400()
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");

        using (var ok = await InviteAsync(token, new string('a', 244) + "@acme.test", user))   // exactly 254
        {
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }

        Clock.Advance(TimeSpan.FromSeconds(61));
        using var over = await InviteAsync(token, new string('b', 245) + "@acme.test", user);   // 255
        await TenancyApi.AssertErrorAsync(over, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Request_with_the_wrong_content_type_is_a_400()
    {
        var (company, _, token) = await AcmeAsync();
        using var request = TenancyApi.Request(HttpMethod.Post, InvitesPath, token);
        request.Content = new StringContent($$"""{"email":"{{Worker}}","role_id":"{{await RoleIdAsync(company, "user")}}"}""", Encoding.UTF8, "text/plain");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    // ---- the mail limit

    [Fact]
    public async Task Inviting_again_within_a_minute_of_cancelling_is_refused_with_the_time_left()   // criterion 10
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");
        await InviteOkAsync(token, Worker, user);
        var id = Assert.Single(await InvitesAsync()).Id;
        using (var cancelled = await TenancyApi.Send(Client, HttpMethod.Delete, $"{InvitesPath}/{id}", token))
        {
            await TenancyApi.AssertEmptyAsync(cancelled, HttpStatusCode.NoContent);
        }

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var refused = await InviteAsync(token, Worker, user);

        var seconds = await LockoutApi.AssertLockedAsync(refused);
        Assert.Equal(40, seconds);
        Assert.Empty(await InvitesAsync());   // a refused invitation is not left behind
        Assert.Single(await QueueAsync());
    }

    [Fact]
    public async Task One_company_cannot_block_or_observe_the_invitations_of_another_to_the_same_person()   // criterion 10
    {
        var (acme, _, acmeToken) = await AcmeAsync();
        var globex = await CreateCompanyAsync("Globex");
        await AddMemberAsync(globex, "boss@globex.test", "admin");
        var globexToken = (await SessionApi.LoginAsync(Client, "boss@globex.test", UserPassword)).AccessToken;

        await InviteOkAsync(acmeToken, Worker, await RoleIdAsync(acme, "user"));
        await InviteOkAsync(globexToken, Worker, await RoleIdAsync(globex, "user"));   // not refused: its own limit

        var invites = await InvitesAsync();
        Assert.Equal(new[] { acme, globex }.Order(), invites.Select(i => i.CompanyId).Order());
        Assert.Equal(2, (await InDbAsync(db => db.MailRequestLimits.Where(l => l.Kind == MailKind.Invitation).CountAsync(TestContext.Current.CancellationToken))));
    }

    [Fact]
    public async Task Sixth_mail_within_the_hour_is_refused_until_the_hour_is_over()   // criterion 10
    {
        var (company, _, token) = await AcmeAsync();
        await InviteOkAsync(token, Worker, await RoleIdAsync(company, "user"));
        var id = Assert.Single(await InvitesAsync()).Id;

        for (var i = 0; i < 4; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(61));
            using var resent = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{id}/resend", token);
            await TenancyApi.AssertEmptyAsync(resent, HttpStatusCode.Accepted);
        }

        Clock.Advance(TimeSpan.FromSeconds(61));
        using var sixth = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{id}/resend", token);
        var seconds = await LockoutApi.AssertLockedAsync(sixth);
        Assert.Equal(3600 - (5 * 61), seconds);   // the window opened at the first mail, five minutes ago
        Assert.Equal(5, (await QueueAsync()).Count);
    }

    // ---- safety rule 1

    [Fact]
    public async Task Nobody_invites_with_a_role_that_holds_a_permission_they_lack()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        var manager = await AddRoleAsync(company, "manager", "members:manage", "reports:read");
        await AddMemberAsync(company, "mgr@acme.test", "manager");
        var plain = await AddRoleAsync(company, "plain");
        var narrower = await AddRoleAsync(company, "narrower", "reports:read");
        var wider = await AddRoleAsync(company, "wider", "reports:read", "reports:approve");
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;

        using var tooMuch = await InviteAsync(token, "a@acme.test", wider);                      // reports:approve is not theirs
        using var star = await InviteAsync(token, "b@acme.test", await RoleIdAsync(company, "admin"));
        await TenancyApi.AssertErrorAsync(tooMuch, HttpStatusCode.Forbidden, "permission_not_held");
        await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        Assert.Empty(await InvitesAsync());

        await InviteOkAsync(token, "c@acme.test", plain);        // nothing at all
        await InviteOkAsync(token, "d@acme.test", narrower);     // a subset
        await InviteOkAsync(token, "e@acme.test", manager);      // their own role
    }

    [Fact]
    public async Task A_role_with_star_needs_a_caller_whose_role_holds_star_even_if_it_lists_everything()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        var everything = await AddRoleAsync(company, "everything", [.. Holder.Current.Catalog.Permissions]);
        await AddMemberAsync(company, "all@acme.test", "everything");
        var token = (await SessionApi.LoginAsync(Client, "all@acme.test", UserPassword)).AccessToken;

        using var star = await InviteAsync(token, "a@acme.test", await RoleIdAsync(company, "admin"));
        await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        await InviteOkAsync(token, "b@acme.test", everything);   // the same permissions, listed one by one
    }

    [Fact]
    public async Task An_admin_with_star_may_invite_with_star()   // criterion 15
    {
        var (company, _, token) = await AcmeAsync();

        await InviteOkAsync(token, "a@acme.test", await RoleIdAsync(company, "admin"));
    }

    [Fact]
    public async Task The_operator_is_exempt_from_rule_1_and_is_recorded_as_no_member()   // criterion 2
    {
        var company = await CreateCompanyAsync("Acme");
        var admin = await RoleIdAsync(company, "admin");
        using var scope = Factory.Services.CreateScope();

        var outcome = await scope.ServiceProvider.GetRequiredService<InvitationService>()
            .SendAsync(Actor.Operator, company, "first@acme.test", "FIRST@ACME.TEST", admin, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Error);
        var invite = Assert.Single(await InvitesAsync());
        Assert.Null(invite.InvitedBy);
        Assert.Equal(1, await DispatchAsync());
        Assert.Equal("first@acme.test", Assert.Single(Mail.Sent).To);
    }

    // ---- listing

    [Fact]
    public async Task List_shows_the_pending_invitations_sorted_by_address_with_their_roles_and_times()
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");
        await InviteOkAsync(token, "zed@acme.test", user);
        Clock.Advance(TimeSpan.FromSeconds(1));
        await InviteOkAsync(token, "Bob@acme.test", await RoleIdAsync(company, "admin"));
        Clock.Advance(TimeSpan.FromSeconds(1));
        await InviteOkAsync(token, "amy@acme.test", user);
        await DispatchAsync();   // some mailed, some not: all are listed

        var list = await ListAsync(token);

        Assert.Equal(["invites"], list.EnumerateObject().Select(p => p.Name));
        var items = ListItems(list);
        Assert.Equal(["Bob@acme.test", "amy@acme.test", "zed@acme.test"], items.Select(i => i.GetProperty("email").GetString()));   // ordinal: upper case first
        Assert.Equal(["email", "expires_at", "id", "invited_at", "role"], items[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["id", "name"], items[0].GetProperty("role").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("admin", items[0].GetProperty("role").GetProperty("name").GetString());
        Assert.Equal((await RoleIdAsync(company, "admin")).ToString(), items[0].GetProperty("role").GetProperty("id").GetString());
        Assert.EndsWith("Z", items[0].GetProperty("invited_at").GetString());     // ISO 8601, UTC
        Assert.EndsWith("Z", items[0].GetProperty("expires_at").GetString());
        Assert.True(items[0].GetProperty("expires_at").GetDateTimeOffset() > items[0].GetProperty("invited_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task List_holds_nothing_of_another_company()   // criterion 14
    {
        var (_, _, token) = await AcmeAsync();
        var globex = await CreateCompanyAsync("Globex");
        await AddInviteAsync(globex, "secret@globex.test", "user");

        Assert.Empty(ListItems(await ListAsync(token)));
    }

    // ---- resending and cancelling

    [Fact]
    public async Task Resend_queues_a_new_mail_and_the_new_link_replaces_the_old()   // criterion 10
    {
        var (company, _, token) = await AcmeAsync();
        await InviteOkAsync(token, Worker, await RoleIdAsync(company, "user"));
        await DispatchAsync();
        var first = TokenIn(Assert.Single(Mail.Sent));
        var id = Assert.Single(await InvitesAsync()).Id;

        Clock.Advance(TimeSpan.FromSeconds(61));
        using (var resent = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{id}/resend", token))
        {
            await TenancyApi.AssertEmptyAsync(resent, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var second = TokenIn(Mail.Sent[1]);
        Assert.NotEqual(first, second);
        Assert.Equal(EmailTokens.HashOf(second), Assert.Single(await InvitesAsync()).TokenHash);
    }

    [Fact]
    public async Task Resend_of_an_invitation_that_is_unknown_expired_or_of_another_company_is_404()   // criterion 14
    {
        var (_, _, token) = await AcmeAsync();
        var globex = await CreateCompanyAsync("Globex");
        var foreign = await AddInviteAsync(globex, "x@globex.test", "user");

        foreach (var id in new[] { Guid.NewGuid(), foreign })
        {
            using var response = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{id}/resend", token);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Resend_of_an_expired_invitation_is_404()
    {
        var (company, _, _) = await AcmeAsync();
        var invite = await AddInviteAsync(company, Worker, "user");
        Clock.Advance(InviteTokens.Lifetime);
        var token = (await SessionApi.LoginAsync(Client, Boss, UserPassword)).AccessToken;

        using var response = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{invite}/resend", token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Cancel_removes_the_invitation_and_drops_its_queued_mail()   // criterion 10
    {
        var (company, _, token) = await AcmeAsync();
        await InviteOkAsync(token, Worker, await RoleIdAsync(company, "user"));
        var id = Assert.Single(await InvitesAsync()).Id;

        using (var cancelled = await TenancyApi.Send(Client, HttpMethod.Delete, $"{InvitesPath}/{id}", token))   // at once: cancelling is not limited
        {
            await TenancyApi.AssertEmptyAsync(cancelled, HttpStatusCode.NoContent);
        }

        Assert.Empty(ListItems(await ListAsync(token)));
        Assert.Empty(await InvitesAsync());
        await DispatchAsync();
        Assert.Empty(Mail.Sent);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Cancel_of_an_invitation_that_is_unknown_gone_or_of_another_company_is_404()   // criterion 14
    {
        var (company, _, token) = await AcmeAsync();
        var globex = await CreateCompanyAsync("Globex");
        var foreign = await AddInviteAsync(globex, "x@globex.test", "user");
        var own = await AddInviteAsync(company, Worker, "user");

        using (var first = await TenancyApi.Send(Client, HttpMethod.Delete, $"{InvitesPath}/{own}", token))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        foreach (var id in new[] { own, Guid.NewGuid(), foreign })
        {
            using var response = await TenancyApi.Send(Client, HttpMethod.Delete, $"{InvitesPath}/{id}", token);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        Assert.NotNull(await InviteAsync(foreign));   // the other company's invitation is untouched
    }

    [Theory]
    [InlineData("POST", "/resend")]
    [InlineData("DELETE", "")]
    public async Task An_id_that_is_not_a_uuid_is_a_400(string method, string suffix)
    {
        var (_, _, token) = await AcmeAsync();

        using var response = await TenancyApi.Send(Client, new HttpMethod(method), $"{InvitesPath}/not-a-uuid{suffix}", token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    // ---- who may

    [Theory]
    [InlineData("GET", "")]
    [InlineData("POST", "")]
    [InlineData("POST", "/11111111-1111-1111-1111-111111111111/resend")]
    [InlineData("DELETE", "/11111111-1111-1111-1111-111111111111")]
    public async Task Every_endpoint_needs_a_token_and_members_manage_checked_against_the_database(string method, string suffix)   // criterion 14
    {
        var company = await CreateCompanyAsync("Acme");
        var worker = await AddMemberAsync(company, Worker, "admin");
        var session = await SessionApi.LoginAsync(Client, Worker, UserPassword);
        var path = InvitesPath + suffix;
        object? body = method == "POST" && suffix == "" ? new { email = "a@acme.test", role_id = Guid.NewGuid() } : null;

        using (var anonymous = await TenancyApi.Send(Client, new HttpMethod(method), path, null, body))
        {
            await TenancyApi.AssertUnauthorizedAsync(anonymous);
        }

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "user"));   // demoted after the token was issued
        using (var stale = await TenancyApi.Send(Client, new HttpMethod(method), path, session.AccessToken, body))
        {
            await TenancyApi.AssertErrorAsync(stale, HttpStatusCode.Forbidden, "permissions_changed");
        }

        var fresh = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using var plain = await TenancyApi.Send(Client, new HttpMethod(method), path, fresh.AccessToken, body);
        await TenancyApi.AssertErrorAsync(plain, HttpStatusCode.Forbidden, "forbidden");
    }

    // ---- races

    [Fact]
    public async Task Parallel_invitations_of_one_address_leave_one_invitation_and_one_mail()
    {
        var (company, _, token) = await AcmeAsync();
        var user = await RoleIdAsync(company, "user");

        // Six, not more: every waiting caller holds a server connection.
        var statuses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var response = await InviteAsync(token, Worker, user);
            return response.StatusCode;
        })));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Accepted));
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Single(await InvitesAsync());
        Assert.Single(await QueueAsync());
    }

    [Fact]
    public async Task Responses_are_never_stored_and_set_no_cookie()
    {
        var (company, _, token) = await AcmeAsync();

        using var accepted = await InviteAsync(token, Worker, await RoleIdAsync(company, "user"));
        using var listed = await TenancyApi.Get(Client, InvitesPath, token);

        AccountApi.AssertNeverStoredAndNoCookie(accepted);
        AccountApi.AssertNeverStoredAndNoCookie(listed);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`InvitationService`, `Actor`, `IdInput` do not exist). Check
  the Unicode rule in `OrgInviteTests.cs` now.

- [ ] **Step 3: Implement.** Some choices the tests pin: the mail limit is checked *after*
  the pending check, so a second invitation to an address with a pending one is
  `invite_pending` and not `429`; a refused request leaves no invitation behind; a path id
  that is not a UUID is `400`, after the `401`/`403` (the id parameter is a string and is
  parsed by the handler).

`src/Auth.Server/Api/EndpointMetadata.cs` — the change:

```diff
--- a/src/Auth.Server/Api/EndpointMetadata.cs
+++ b/src/Auth.Server/Api/EndpointMetadata.cs
@@ -1,3 +1,5 @@
+using Auth.Server.Tenancy;
+
 namespace Auth.Server.Api;
 
 /// <summary>The body of every error of the API: <c>{"error":"&lt;code&gt;"}</c>.</summary>
@@ -30,11 +32,16 @@
         return builder.Produces<ErrorBody>(status, "application/json").WithMetadata(new ErrorCodesMetadata(status, codes));
     }
 
-    /// <summary>The <c>401</c> of an endpoint that needs an access token: an empty body and a <c>WWW-Authenticate: Bearer</c> challenge.</summary>
-    public static RouteHandlerBuilder ProducesUnauthorized(this RouteHandlerBuilder builder)
+    /// <summary>
+    /// The answers every endpoint behind an access token and a permission check can give: a <c>401</c> with an empty
+    /// body and a <c>WWW-Authenticate: Bearer</c> challenge, and the two <c>403</c>s of the permission check.
+    /// </summary>
+    public static RouteHandlerBuilder ProducesGuarded(this RouteHandlerBuilder builder)
     {
         ArgumentNullException.ThrowIfNull(builder);
 
-        return builder.Produces(StatusCodes.Status401Unauthorized);
+        return builder
+            .Produces(StatusCodes.Status401Unauthorized)
+            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionsChanged, TenancyErrors.Forbidden);
     }
 }
```

`src/Auth.Server/Api/OrgInviteEndpoints.cs`:

```csharp
using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Api;

/// <summary>
/// The invitations of the caller's company (spec 0005 → Company API): list, send, resend, cancel. All need
/// <c>members:manage</c>, checked against the database.
/// </summary>
public static class OrgInviteEndpoints
{
    private static readonly string[] SendFields = ["email", "role_id"];

    public static async Task<IResult> ListAsync(HttpContext http, MembershipReader memberships, InvitationService invitations)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(invitations);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        return access.Caller is { } caller
            ? ApiResults.Ok(new InvitesResponse(await invitations.ListAsync(caller.CompanyId, http.RequestAborted)))
            : access.Failure!;
    }

    public static async Task<IResult> SendAsync(
        HttpContext http, MembershipReader memberships, InvitationService invitations, ILookupNormalizer normalizer, MailDispatchSignal signal)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(invitations);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(signal);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, SendFields, http.RequestAborted);
        if (fields is null
            || fields[0].Length > EmailInput.MaxLength
            || !EmailInput.IsMailbox(fields[0])
            || !EmailInput.TryNormalize(fields[0], normalizer, out var normalized)
            || !IdInput.TryParse(fields[1], out var roleId))
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await invitations.SendAsync(Actor.Of(caller), caller.CompanyId, fields[0], normalized, roleId, http.RequestAborted);
        if (!outcome.Succeeded)
        {
            return ApiResults.Refused(outcome);
        }

        signal.Notify();
        return ApiResults.Accepted();
    }

    public static async Task<IResult> ResendAsync(
        string id, HttpContext http, MembershipReader memberships, InvitationService invitations, MailDispatchSignal signal)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(invitations);
        ArgumentNullException.ThrowIfNull(signal);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        if (!IdInput.TryParse(id, out var inviteId))
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await invitations.ResendAsync(caller.CompanyId, inviteId, http.RequestAborted);
        if (!outcome.Succeeded)
        {
            return ApiResults.Refused(outcome);
        }

        signal.Notify();
        return ApiResults.Accepted();
    }

    public static async Task<IResult> CancelAsync(string id, HttpContext http, MembershipReader memberships, InvitationService invitations)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(invitations);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        if (!IdInput.TryParse(id, out var inviteId))
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await invitations.CancelAsync(caller.CompanyId, inviteId, http.RequestAborted);
        return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
    }
}
```

`src/Auth.Server/Api/TenancyEndpoints.cs` — the change:

```diff
--- a/src/Auth.Server/Api/TenancyEndpoints.cs
+++ b/src/Auth.Server/Api/TenancyEndpoints.cs
@@ -16,21 +16,42 @@
         app.MapGet(OrgEndpoints.MePath, OrgEndpoints.MeAsync)
             .RequireAuthorization()
             .Produces<MeResponse>()
-            .ProducesUnauthorized()
-            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionsChanged, TenancyErrors.Forbidden);
+            .ProducesGuarded();
 
         var company = app.MapGroup(OrgEndpoints.OrgPath).RequireAuthorization();
 
         company.MapGet("", OrgEndpoints.GetAsync)
             .Produces<OrgResponse>()
-            .ProducesUnauthorized()
-            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionsChanged, TenancyErrors.Forbidden);
+            .ProducesGuarded();
         company.MapPatch("", OrgEndpoints.RenameAsync)
             .ReadsJson<RenameOrgRequest>()
             .Produces(StatusCodes.Status204NoContent)
-            .ProducesUnauthorized()
             .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
-            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionsChanged, TenancyErrors.Forbidden);
+            .ProducesGuarded();
+
+        company.MapGet("invites", OrgInviteEndpoints.ListAsync)
+            .Produces<InvitesResponse>()
+            .ProducesGuarded();
+        company.MapPost("invites", OrgInviteEndpoints.SendAsync)
+            .ReadsJson<SendInviteRequest>()
+            .Produces(StatusCodes.Status202Accepted)
+            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
+            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
+            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
+            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyInOrg, TenancyErrors.InvitePending)
+            .ProducesError(StatusCodes.Status429TooManyRequests, TenancyErrors.TooManyAttempts)
+            .ProducesGuarded();
+        company.MapPost("invites/{id}/resend", OrgInviteEndpoints.ResendAsync)
+            .Produces(StatusCodes.Status202Accepted)
+            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
+            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
+            .ProducesError(StatusCodes.Status429TooManyRequests, TenancyErrors.TooManyAttempts)
+            .ProducesGuarded();
+        company.MapDelete("invites/{id}", OrgInviteEndpoints.CancelAsync)
+            .Produces(StatusCodes.Status204NoContent)
+            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
+            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
+            .ProducesGuarded();
 
         return app;
     }
```

`src/Auth.Server/Requests/EmailInput.cs` — the change:

```diff
--- a/src/Auth.Server/Requests/EmailInput.cs
+++ b/src/Auth.Server/Requests/EmailInput.cs
@@ -1,5 +1,6 @@
 using System.Diagnostics.CodeAnalysis;
 using Microsoft.AspNetCore.Identity;
+using MimeKit;
 
 namespace Auth.Server.Requests;
 
@@ -51,6 +52,22 @@
     }
 
     /// <summary>
+    /// Whether the text is one mailbox and nothing more: <c>local@domain</c>, no name, no list, no comment. An invitation
+    /// creates an account with the address and mails it, so a typo such as <c>bob.acme.test</c> is refused at once instead
+    /// of becoming an invitation that never arrives.
+    /// </summary>
+    public static bool IsMailbox(string email)
+    {
+        ArgumentNullException.ThrowIfNull(email);
+
+        var at = email.LastIndexOf('@');
+        return at > 0
+            && at < email.Length - 1
+            && MailboxAddress.TryParse(email, out var mailbox)
+            && string.Equals(mailbox.Address, email, StringComparison.Ordinal);
+    }
+
+    /// <summary>
     /// The email as the account lookup normalises it, or <see langword="false"/> when it is not well formed or the
     /// host's normaliser rejects it.
     /// </summary>
```

`src/Auth.Server/Requests/IdInput.cs`:

```csharp
namespace Auth.Server.Requests;

/// <summary>An id in a path or a body is a UUID in its usual form, 8-4-4-4-12 hexadecimal digits, and nothing else.</summary>
public static class IdInput
{
    public static bool TryParse(string? text, out Guid id) => Guid.TryParseExact(text, "D", out id);
}
```

`src/Auth.Server/Tenancy/Actor.cs`:

```csharp
namespace Auth.Server.Tenancy;

/// <summary>
/// Who is doing something to a company: a member, with the role the database gave them a moment ago, or the operator
/// at the command line. Safety rule 1 (nobody grants more than they hold) binds a member and not the operator.
/// </summary>
public sealed class Actor
{
    private Actor(TenantContext? member) => Member = member;

    /// <summary>Whoever runs the instance: not a member of any company, and exempt from safety rule 1 (spec 0005 → Concepts).</summary>
    public static Actor Operator { get; } = new(null);

    public static Actor Of(TenantContext member)
    {
        ArgumentNullException.ThrowIfNull(member);

        return new Actor(member);
    }

    /// <summary>The acting member; <see langword="null"/> for the operator.</summary>
    public TenantContext? Member { get; }

    public bool IsOperator => Member is null;

    /// <summary>The member's user id; <see langword="null"/> for the operator.</summary>
    public Guid? UserId => Member?.UserId;

    /// <summary>
    /// Safety rule 1: whether this actor may give, create or edit a role that holds <paramref name="stored"/>. Every
    /// permission the role ends up with must be one the actor holds, and a role with <c>*</c> needs an actor whose own
    /// role holds <c>*</c>. Names that have left the catalog grant nothing and do not count.
    /// </summary>
    public bool MayGrant(IEnumerable<string> stored, PermissionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(catalog);

        if (Member is null)
        {
            return true;
        }

        var list = stored as IReadOnlyCollection<string> ?? [.. stored];
        return list.Contains(PermissionCatalog.All, StringComparer.Ordinal)
            ? Member.HoldsAll
            : catalog.Expand(list).All(Member.Holds);
    }
}
```

`src/Auth.Server/Tenancy/CompanyLock.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// The lock that serialises everything that changes one company's members, roles or invitations (spec 0005 → Safety
/// rules, rule 2): a row lock on the company, taken first, in a transaction left at the default isolation level. A
/// higher level would not do: under <c>REPEATABLE READ</c> the later transaction keeps reading the snapshot it took
/// before the lock was granted, and the check it makes is made against rows that have since changed.
/// </summary>
public static class CompanyLock
{
    /// <summary>Locks the company's row until the transaction ends; <see langword="false"/> when there is no such company.</summary>
    public static async Task<bool> AcquireAsync(AuthDbContext db, Guid companyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var locked = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM "Companies" WHERE "Id" = {companyId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        return locked.Count == 1;
    }
}
```

`src/Auth.Server/Tenancy/Contracts.cs` — the change:

```diff
--- a/src/Auth.Server/Tenancy/Contracts.cs
+++ b/src/Auth.Server/Tenancy/Contracts.cs
@@ -10,3 +10,15 @@
 
 /// <summary>Request of <c>PATCH /auth/org</c>.</summary>
 public sealed record RenameOrgRequest(string Name);
+
+/// <summary>A role as a list names it: its id and its name.</summary>
+public sealed record RoleRef(Guid Id, string Name);
+
+/// <summary>One pending invitation of <c>GET /auth/org/invites</c>. Times are ISO 8601 in UTC.</summary>
+public sealed record InviteItem(Guid Id, string Email, RoleRef Role, DateTime InvitedAt, DateTime ExpiresAt);
+
+/// <summary>Response of <c>GET /auth/org/invites</c>.</summary>
+public sealed record InvitesResponse(IReadOnlyList<InviteItem> Invites);
+
+/// <summary>Request of <c>POST /auth/org/invites</c>.</summary>
+public sealed record SendInviteRequest(string Email, Guid RoleId);
```

`src/Auth.Server/Tenancy/InvitationService.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Auth.Server.Tenancy;

/// <summary>
/// Sends, lists, resends and cancels the invitations of a company (spec 0005 → Invitations). The company API and the
/// operator commands both call it, so the rules are the same on both. Everything that changes anything runs in one
/// transaction that begins by locking the company (see <see cref="CompanyLock"/>).
/// </summary>
public sealed class InvitationService(AuthDbContext db, ManifestHolder manifest, TimeProvider clock)
{
    /// <summary>
    /// Invites an address to the company with a role: makes the invitation, applies the mail limit of the company and
    /// the address, and queues the mail — or refuses, and then changes nothing. Whether the address has an account, or
    /// belongs to another company, is not looked at: the answer is the same for every address outside this company.
    /// </summary>
    /// <param name="email">The address as typed, which the account will be created with.</param>
    /// <param name="normalizedEmail">The address as the account lookup normalises it.</param>
    public async Task<Outcome> SendAsync(
        Actor actor, Guid companyId, string email, string normalizedEmail, Guid roleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        var now = StorableTime.Now(clock);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await CompanyLock.AcquireAsync(db, companyId, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        var role = await db.CompanyRoles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId && r.CompanyId == companyId, cancellationToken);
        if (role is null)
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        if (!actor.MayGrant(role.Permissions, manifest.Current.Catalog))
        {
            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
        }

        var isMember = await (
            from user in db.Users
            join membership in db.Memberships on user.Id equals membership.UserId
            where user.NormalizedEmail == normalizedEmail && membership.CompanyId == companyId
            select membership.UserId).AnyAsync(cancellationToken);
        if (isMember)
        {
            return Outcome.Fail(TenancyErrors.AlreadyInOrg);
        }

        // An expired invitation is not pending: it makes room for the new one. A pending one stops the insert.
        await db.Invites
            .Where(i => i.CompanyId == companyId && i.NormalizedEmail == normalizedEmail && i.ExpiresAt <= now)
            .ExecuteDeleteAsync(cancellationToken);

        var inviteId = Guid.NewGuid();
        var expiresAt = now + InviteTokens.Lifetime;
        var invitedBy = actor.UserId;
        var inserted = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "Invites" ("Id", "CompanyId", "Email", "NormalizedEmail", "RoleId", "InvitedBy", "InvitedAt", "TokenHash", "ExpiresAt")
            VALUES ({inviteId}, {companyId}, {email}, {normalizedEmail}, {roleId}, {invitedBy}, {now}, NULL, {expiresAt})
            ON CONFLICT ("CompanyId", "NormalizedEmail") DO NOTHING
            """,
            cancellationToken);
        if (inserted == 0)
        {
            return Outcome.Fail(TenancyErrors.InvitePending);
        }

        return await QueueMailAsync(companyId, inviteId, normalizedEmail, now, transaction, cancellationToken);
    }

    /// <summary>The company's invitations that have not expired, sorted by address, ordinally.</summary>
    public async Task<IReadOnlyList<InviteItem>> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var now = StorableTime.Now(clock);
        var rows = await (
            from invite in db.Invites.AsNoTracking()
            join role in db.CompanyRoles.AsNoTracking() on invite.RoleId equals role.Id
            where invite.CompanyId == companyId && invite.ExpiresAt > now
            select new { invite.Id, invite.Email, RoleId = role.Id, RoleName = role.Name, invite.InvitedAt, invite.ExpiresAt })
            .ToListAsync(cancellationToken);

        return [.. rows
            .OrderBy(r => r.Email, StringComparer.Ordinal)
            .Select(r => new InviteItem(r.Id, r.Email, new RoleRef(r.RoleId, r.RoleName), r.InvitedAt.UtcDateTime, r.ExpiresAt.UtcDateTime))];
    }

    /// <summary>
    /// Queues a new mail for a pending invitation, subject to the same mail limit as sending. When the mail is composed it
    /// carries a new token, and the earlier link stops working; the seven days start again.
    /// </summary>
    public async Task<Outcome> ResendAsync(Guid companyId, Guid inviteId, CancellationToken cancellationToken)
    {
        var now = StorableTime.Now(clock);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await CompanyLock.AcquireAsync(db, companyId, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        var invite = await db.Invites.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == inviteId && i.CompanyId == companyId && i.ExpiresAt > now, cancellationToken);
        return invite is null
            ? Outcome.Fail(TenancyErrors.NotFound)
            : await QueueMailAsync(companyId, invite.Id, invite.NormalizedEmail, now, transaction, cancellationToken);
    }

    /// <summary>Makes the link of a pending invitation stop working at once, and removes it from the list. Not subject to the mail limit.</summary>
    public async Task<Outcome> CancelAsync(Guid companyId, Guid inviteId, CancellationToken cancellationToken)
    {
        var now = StorableTime.Now(clock);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await CompanyLock.AcquireAsync(db, companyId, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        var removed = await db.Invites
            .Where(i => i.Id == inviteId && i.CompanyId == companyId && i.ExpiresAt > now)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return removed == 0 ? Outcome.Fail(TenancyErrors.NotFound) : Outcome.Done;
    }

    /// <summary>Applies the mail limit of the company and address and, if it lets the mail through, queues it and commits.</summary>
    private async Task<Outcome> QueueMailAsync(
        Guid companyId, Guid inviteId, string normalizedEmail, DateTimeOffset now,
        IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        var decision = await MailLimits.RegisterAsync(
            db, InviteTokens.LimitIdentifierOf(companyId, normalizedEmail), MailKind.Invitation, now, cancellationToken);
        if (!decision.Allowed)
        {
            // Leaving without a commit takes the invitation just made back with it.
            return Outcome.Limited(decision.RetryAfter);
        }

        db.MailRequests.Add(new MailRequest
        {
            Kind = MailKind.Invitation,
            NormalizedEmail = normalizedEmail,
            InviteId = inviteId,
            RequestedAt = now,
            NextAttemptAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }
}
```

`src/Auth.Server/Tenancy/TenancyServices.cs` — the change:

```diff
--- a/src/Auth.Server/Tenancy/TenancyServices.cs
+++ b/src/Auth.Server/Tenancy/TenancyServices.cs
@@ -15,6 +15,7 @@
         services.AddSingleton<ManifestActivator>();
         services.AddScoped<CompanyService>();
         services.AddScoped<MembershipReader>();
+        services.AddScoped<InvitationService>();
         return services;
     }
 }
```

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS — 49 new tests, 624 in all.

- [ ] **Step 5: Commit** — `feat(tenancy): send, list, resend and cancel invitations`

### Task 9: Preview and accept

**Files:**
- Create: `src/Auth.Server/Tenancy/InviteAcceptance.cs`, `src/Auth.Server/Api/InviteEndpoints.cs`,
  `tests/Auth.IntegrationTests/InviteAcceptTests.cs`
- Modify: `src/Auth.Server/Tenancy/Contracts.cs`, `src/Auth.Server/Tenancy/TenancyServices.cs`,
  `src/Auth.Server/Api/TenancyEndpoints.cs`, `src/Auth.Server/Api/EndpointMetadata.cs`,
  `src/Auth.Infrastructure/DependencyInjection.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/TenancyApi.cs`

**Interfaces:**
- Consumes: Tasks 7 and 8; `EmailTokens.HashOf`, `PasswordRules.BrokenAsync` (spec 0004),
  OpenIddict's token and authorization managers.
- Produces: `InviteAcceptance` (scoped):
  - `PreviewAsync(token, ct)` → `Outcome<InvitePreview(OrgName, Email, Role)>`; the token
    stays usable. Unknown, used, expired, cancelled and replaced are all `invalid_token`;
    an address that already belongs to a company is `already_member`.
  - `AcceptAsync(token, password, ct)` → `Outcome<IReadOnlyList<string>>` (the value is the
    list of broken password rules on `weak_password`). One transaction: the company lock
    (the company is found from the token first); the invitation is **used up by one
    `DELETE … RETURNING`** (of parallel accepts exactly one gets it); a Postgres advisory
    lock on the address, so that two invitations of two companies accepted at once make one
    membership — the tables would allow two, this version does not; `already_member` if the
    address belongs to a member (checked before the password); the password policy
    (`weak_password`: nothing changes and the token stays usable, because the transaction
    is rolled back with the `DELETE`); then the account is created with the address as typed
    and as its user name, or — for an existing account — its password is replaced, and **every
    session ends** (refresh tokens and authorizations revoked, other link tokens and the
    login streak removed), exactly what a reset does; the email is confirmed either way;
    the membership is added with the role of the invitation. Other invitations of the same
    address are left as they are.
- Produces: `POST /auth/invites/preview` `{"token"}` → `200 {"org_name","email","role"}`;
  `POST /auth/invites/accept` `{"token","password"}` → `204`, no sign-in and no cookie.
  Public: the link token is the credential. A `token` that is a non-blank string is never
  an `invalid_request` for its content; a `password` with a NUL is.
- Changes: `AllowedUserNameCharacters = string.Empty` in the Identity options: the address
  is the user name, and the default character list refuses `zażółć@example.com`.
- Produces (tests): `TenancyApi.Preview`, `TenancyApi.Accept`.

- [ ] **Step 1: Write the failing tests.** (The NUL of the password test is the escape of
  the Unicode rule, in a raw string.)

`tests/Auth.IntegrationTests/Infrastructure/TenancyApi.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/Infrastructure/TenancyApi.cs
+++ b/tests/Auth.IntegrationTests/Infrastructure/TenancyApi.cs
@@ -31,6 +31,15 @@
     }
 
     public static Task<HttpResponseMessage> Get(HttpClient client, string path, string? token) => Send(client, HttpMethod.Get, path, token);
+
+    public const string PreviewPath = "/auth/invites/preview";
+    public const string AcceptPath = "/auth/invites/accept";
+
+    public static Task<HttpResponseMessage> Preview(HttpClient client, string token) =>
+        client.PostAsJsonAsync(PreviewPath, new { token });
+
+    public static Task<HttpResponseMessage> Accept(HttpClient client, string token, string password) =>
+        client.PostAsJsonAsync(AcceptPath, new { token, password });
 
     /// <summary>Asserts the status, that the answer is never stored and sets no cookie, and returns the raw body.</summary>
     public static async Task<string> ReadAsync(HttpResponseMessage response, HttpStatusCode status)
```

`tests/Auth.IntegrationTests/InviteAcceptTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class InviteAcceptTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Address = "worker@acme.test";
    private const string Password = "Chosen-Passw0rd";
    private const string InvalidToken = """{"error":"invalid_token"}""";

    /// <summary>An invitation of the company, with its mail composed: returns the invitation and the token as the mail holds it.</summary>
    private async Task<(Guid Invite, string Token)> MailedAsync(Guid company, string address = Address, string role = "user")
    {
        var invite = await AddInviteAsync(company, address, role);
        await EnqueueInvitationAsync(invite, address);
        await DispatchAsync();
        return (invite, TokenIn(Mail.Sent.Last(m => m.To == address)));
    }

    private async Task<ApplicationUser?> UserAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
    }

    private Task<List<Membership>> MembershipsAsync() =>
        InDbAsync(db => db.Memberships.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    private static async Task AssertInvalidTokenAsync(HttpResponseMessage response)
    {
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_token");
        Assert.Equal(InvalidToken, await response.Content.ReadAsStringAsync());
    }

    // ---- the whole way

    [Fact]
    public async Task A_manager_invites_the_person_accepts_and_logs_in_with_the_company_and_the_role()   // criteria 2, 3, 4
    {
        var company = await CreateCompanyAsync("Acme");
        await AddMemberAsync(company, "boss@acme.test", "admin");
        var boss = await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword);
        using (var sent = await TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", boss.AccessToken, new { email = Address, role_id = await RoleIdAsync(company, "user") }))
        {
            await TenancyApi.AssertEmptyAsync(sent, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        var token = TokenIn(Assert.Single(Mail.Sent));

        using (var preview = await TenancyApi.Preview(Client, token))
        {
            var body = await TenancyApi.ReadOkAsync(preview);
            Assert.Equal(["email", "org_name", "role"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal(Address, body.GetProperty("email").GetString());
            Assert.Equal("Acme", body.GetProperty("org_name").GetString());
            Assert.Equal("user", body.GetProperty("role").GetString());
        }

        using (var accepted = await TenancyApi.Accept(Client, token, Password))
        {
            await TenancyApi.AssertEmptyAsync(accepted, HttpStatusCode.NoContent);
        }

        var session = await SessionApi.LoginAsync(Client, Address, Password);
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(["user"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(["reports:approve", "reports:read"], AccessTokens.Array(session.AccessToken, "permissions"));
    }

    // ---- preview

    [Fact]
    public async Task Preview_leaves_the_token_usable()   // criterion 3
    {
        var company = await CreateCompanyAsync("Acme");
        var (invite, token) = await MailedAsync(company);

        for (var i = 0; i < 3; i++)
        {
            using var preview = await TenancyApi.Preview(Client, token);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        }

        Assert.NotNull(await InviteAsync(invite));
        using var accepted = await TenancyApi.Accept(Client, token, Password);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    [Fact]
    public async Task Preview_shows_the_names_as_they_are_now()
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);
        await InDbAsync(db => db.Companies.Where(c => c.Id == company).ExecuteUpdateAsync(set => set.SetProperty(c => c.Name, "Acme Holdings"), TestContext.Current.CancellationToken));

        using var preview = await TenancyApi.Preview(Client, token);

        Assert.Equal("Acme Holdings", (await TenancyApi.ReadOkAsync(preview)).GetProperty("org_name").GetString());
    }

    [Fact]
    public async Task Preview_and_accept_of_a_token_that_is_not_usable_give_one_answer_whatever_the_reason()   // criterion 6
    {
        var company = await CreateCompanyAsync("Acme");

        // Used.
        var (_, used) = await MailedAsync(company, "used@acme.test");
        using (var first = await TenancyApi.Accept(Client, used, Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        // Cancelled.
        var (cancelled, cancelledToken) = await MailedAsync(company, "cancelled@acme.test");
        await InDbAsync(db => db.Invites.Where(i => i.Id == cancelled).ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        // Replaced by a resend.
        var (replacedInvite, replaced) = await MailedAsync(company, "replaced@acme.test");
        await EnqueueInvitationAsync(replacedInvite, "replaced@acme.test");
        await DispatchAsync();

        // Expired: a week and a second old.
        var (_, expired) = await MailedAsync(company, "expired@acme.test");
        Clock.Advance(InviteTokens.Lifetime);

        var tokens = new[] { used, cancelledToken, replaced, expired, "unknown-token", new string('x', 43), "Zażółć" };
        var previews = new List<string>();
        var accepts = new List<string>();
        foreach (var token in tokens)
        {
            using var preview = await TenancyApi.Preview(Client, token);
            await AssertInvalidTokenAsync(preview);
            previews.Add(await preview.Content.ReadAsStringAsync());
            using var accept = await TenancyApi.Accept(Client, token, Password);
            await AssertInvalidTokenAsync(accept);
            accepts.Add(await accept.Content.ReadAsStringAsync());
        }

        Assert.Single(previews.Distinct());
        Assert.Single(accepts.Distinct());
        Assert.Equal(previews[0], accepts[0]);
    }

    [Fact]
    public async Task Token_is_good_until_the_seven_days_are_over()   // the 7-day lifetime, with the controlled clock
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);

        Clock.Advance(InviteTokens.Lifetime - TimeSpan.FromSeconds(1));
        using (var almost = await TenancyApi.Preview(Client, token))
        {
            Assert.Equal(HttpStatusCode.OK, almost.StatusCode);
        }

        Clock.Advance(TimeSpan.FromSeconds(1));
        using var over = await TenancyApi.Preview(Client, token);
        await AssertInvalidTokenAsync(over);
        using var accept = await TenancyApi.Accept(Client, token, Password);
        await AssertInvalidTokenAsync(accept);
        Assert.Null(await UserAsync(Address));
        Assert.Empty(await NonSeedMembershipsAsync());
    }

    /// <summary>The memberships that are not the seed user's in the development company.</summary>
    private async Task<List<Membership>> NonSeedMembershipsAsync()
    {
        var dev = await DevCompanyIdAsync();
        return [.. (await MembershipsAsync()).Where(m => m.CompanyId != dev)];
    }

    [Fact]
    public async Task A_resent_invitation_works_through_the_new_link_only()   // criterion 10
    {
        var company = await CreateCompanyAsync("Acme");
        var (invite, first) = await MailedAsync(company);
        await EnqueueInvitationAsync(invite, Address);
        await DispatchAsync();
        var second = TokenIn(Mail.Sent[1]);

        using (var old = await TenancyApi.Preview(Client, first))
        {
            await AssertInvalidTokenAsync(old);
        }

        using var fresh = await TenancyApi.Preview(Client, second);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task A_cancelled_invitation_cannot_be_previewed_or_accepted_and_is_not_listed()   // criterion 10
    {
        var company = await CreateCompanyAsync("Acme");
        await AddMemberAsync(company, "boss@acme.test", "admin");
        var boss = (await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword)).AccessToken;
        var (invite, token) = await MailedAsync(company);

        using (var cancel = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/invites/{invite}", boss))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }

        using (var preview = await TenancyApi.Preview(Client, token))
        {
            await AssertInvalidTokenAsync(preview);
        }

        using var accept = await TenancyApi.Accept(Client, token, Password);
        await AssertInvalidTokenAsync(accept);
        using var list = await TenancyApi.Get(Client, "/auth/org/invites", boss);
        Assert.Empty((await TenancyApi.ReadOkAsync(list)).GetProperty("invites").EnumerateArray());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"token":42}""")]
    [InlineData("""{"token":null}""")]
    [InlineData("""{"token":"   "}""")]
    [InlineData("""["token"]""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task Malformed_requests_are_a_400_invalid_request(string body)
    {
        foreach (var path in new[] { TenancyApi.PreviewPath, TenancyApi.AcceptPath })
        {
            using var response = await AccountApi.PostRaw(Client, path, body);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        }
    }

    [Fact]
    public async Task Accept_without_a_password_or_with_a_nul_in_it_is_a_400_invalid_request()
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);

        using var missing = await AccountApi.PostRaw(Client, TenancyApi.AcceptPath, $$"""{"token":"{{token}}"}""");
        using var nul = await AccountApi.PostRaw(Client, TenancyApi.AcceptPath, $$"""{"token":"{{token}}","password":"Abcdefg1\u0000x"}""");

        await TenancyApi.AssertErrorAsync(missing, HttpStatusCode.BadRequest, "invalid_request");
        await TenancyApi.AssertErrorAsync(nul, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Null(await UserAsync(Address));
    }

    [Fact]
    public async Task A_token_that_is_a_non_blank_string_is_never_an_invalid_request_for_its_content()
    {
        foreach (var token in new[] { "x", "Zażółć gęślą jaźń", new string('t', 7000), "{\"a\":1}" })
        {
            using var preview = await TenancyApi.Preview(Client, token);
            await AssertInvalidTokenAsync(preview);
        }
    }

    // ---- accept

    [Fact]
    public async Task Accepting_for_an_address_without_an_account_creates_a_confirmed_member()   // criterion 4
    {
        var company = await CreateCompanyAsync("Acme");
        var (invite, token) = await MailedAsync(company);

        using var accepted = await TenancyApi.Accept(Client, token, Password);

        await TenancyApi.AssertEmptyAsync(accepted, HttpStatusCode.NoContent);
        var user = Assert.IsType<ApplicationUser>(await UserAsync(Address));
        Assert.True(user.EmailConfirmed);
        Assert.Equal(Address, user.Email);
        var membership = Assert.Single(await NonSeedMembershipsAsync());
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(company, membership.CompanyId);
        Assert.Equal(await RoleIdAsync(company, "user"), membership.RoleId);
        Assert.Null(await InviteAsync(invite));   // used up
    }

    [Fact]
    public async Task Accept_signs_nobody_in()   // spec 0005 → POST /auth/invites/accept
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);

        using var accepted = await TenancyApi.Accept(Client, token, Password);

        Assert.False(accepted.Headers.Contains("Set-Cookie"));
        Assert.Equal("", await accepted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_role_with_star_gives_every_permission_of_the_catalog_sorted_without_star()   // criterion 4
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company, role: "admin");
        using (var accepted = await TenancyApi.Accept(Client, token, Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        var session = await SessionApi.LoginAsync(Client, Address, Password);

        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(
            ["members:manage", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task An_address_with_letters_beyond_ascii_gets_an_account()
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company, "zażółć.gęślą@acme.test");

        using var accepted = await TenancyApi.Accept(Client, token, Password);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        _ = await SessionApi.LoginAsync(Client, "zażółć.gęślą@acme.test", Password);
    }

    [Fact]
    public async Task Accepting_for_an_account_without_a_company_replaces_its_password_and_ends_every_session()   // criteria 5, 13
    {
        var company = await CreateCompanyAsync("Acme");
        var leaver = await AddMemberAsync(company, Address, "user", confirmed: false);

        // Make the account a member with a session of its own, confirmed, then take the membership away.
        await InDbAsync(db => db.Users.Where(u => u.Id == leaver).ExecuteUpdateAsync(set => set.SetProperty(u => u.EmailConfirmed, true), TestContext.Current.CancellationToken));
        var session = await SessionApi.LoginAsync(Client, Address, UserPassword);
        await RemoveMembershipAsync(leaver);
        var (_, token) = await MailedAsync(company);

        using (var accepted = await TenancyApi.Accept(Client, token, Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        // The person is a member again, so only the end of the sessions can refuse this refresh.
        using var refreshed = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refreshed);
        using var oldPassword = await LoginApi.Login(Client, Address, UserPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        _ = await SessionApi.LoginAsync(Client, Address, Password);
        Assert.Single(await NonSeedMembershipsAsync());
    }

    [Fact]
    public async Task Accepting_confirms_an_unconfirmed_account_lifts_a_lockout_and_removes_its_other_links()   // spec 0005 → Effects
    {
        await CreateUserAsync(Address, confirmed: false, member: false);
        await EnqueueAsync(MailKind.EmailVerification, Address);
        await DispatchAsync();
        Assert.Single(await InDbAsync(db => db.EmailTokens.ToListAsync(TestContext.Current.CancellationToken)));
        await LockoutApi.FailAsync(Client, Clock, Address, 10);   // locked out
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);

        using var accepted = await TenancyApi.Accept(Client, token, Password);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.True((await UserAsync(Address))!.EmailConfirmed);
        Assert.Empty(await InDbAsync(db => db.EmailTokens.ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        _ = await SessionApi.LoginAsync(Client, Address, Password);   // first try
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Weak_password_names_the_broken_rules_changes_nothing_and_leaves_the_token_usable(bool accountExists)   // criterion 7
    {
        if (accountExists)
        {
            await CreateUserAsync(Address, confirmed: false, member: false);
        }

        var company = await CreateCompanyAsync("Acme");
        var (invite, token) = await MailedAsync(company);
        var hash = (await InviteAsync(invite))!.TokenHash;

        using (var weak = await TenancyApi.Accept(Client, token, "abc"))
        {
            var raw = await TenancyApi.ReadAsync(weak, HttpStatusCode.BadRequest);
            Assert.Equal("""{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}""", raw);
        }

        var kept = Assert.IsType<Invite>(await InviteAsync(invite));
        Assert.Equal(hash, kept.TokenHash);
        Assert.Empty(await NonSeedMembershipsAsync());
        var user = await UserAsync(Address);
        if (accountExists)
        {
            Assert.False(user!.EmailConfirmed);   // untouched
            using var unchanged = await LoginApi.Login(Client, Address, UserPassword);
            Assert.Equal(HttpStatusCode.Forbidden, unchanged.StatusCode);   // the old password still works, and still needs verifying
        }
        else
        {
            Assert.Null(user);
        }

        using var good = await TenancyApi.Accept(Client, token, Password);
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
    }

    [Fact]
    public async Task Of_parallel_accepts_with_one_token_exactly_one_succeeds()   // criterion 6
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);

        // Eight, not more: every waiting caller holds a server connection.
        var statuses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            using var response = await TenancyApi.Accept(Client, token, Password);
            return response.StatusCode;
        })));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.NoContent));
        Assert.Equal(7, statuses.Count(s => s == HttpStatusCode.BadRequest));
        Assert.Single(await NonSeedMembershipsAsync());
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Address, TestContext.Current.CancellationToken)));
    }

    // ---- an address that belongs to a member of another company

    [Fact]
    public async Task For_a_member_of_another_company_preview_and_accept_say_already_member_and_the_invitation_stays()   // criterion 8
    {
        var acme = await CreateCompanyAsync("Acme");
        var globex = await CreateCompanyAsync("Globex");
        await AddMemberAsync(globex, Address, "user");
        var (invite, token) = await MailedAsync(acme);

        using (var preview = await TenancyApi.Preview(Client, token))
        {
            await TenancyApi.AssertErrorAsync(preview, HttpStatusCode.Conflict, "already_member");
        }

        using (var accept = await TenancyApi.Accept(Client, token, Password))
        {
            await TenancyApi.AssertErrorAsync(accept, HttpStatusCode.Conflict, "already_member");
        }

        Assert.NotNull(await InviteAsync(invite));                          // still usable until it expires
        using var login = await LoginApi.Login(Client, Address, UserPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);                  // the password was not replaced
        Assert.Equal(globex, (await NonSeedMembershipsAsync()).Single().CompanyId);
    }

    [Fact]
    public async Task A_weak_password_for_an_address_that_is_already_a_member_elsewhere_is_already_member()   // already_member comes before the password policy
    {
        var acme = await CreateCompanyAsync("Acme");
        var globex = await CreateCompanyAsync("Globex");
        await AddMemberAsync(globex, Address, "user");
        var (invite, token) = await MailedAsync(acme);

        using var response = await TenancyApi.Accept(Client, token, "abc");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "already_member");
        Assert.NotNull(await InviteAsync(invite));
    }

    [Fact]
    public async Task Accepting_one_invitation_leaves_the_others_of_the_address_as_they_are_and_they_then_say_already_member()   // spec 0005 → Effects
    {
        var acme = await CreateCompanyAsync("Acme");
        var globex = await CreateCompanyAsync("Globex");
        var (_, acmeToken) = await MailedAsync(acme);
        var (globexInvite, globexToken) = await MailedAsync(globex);

        using (var accepted = await TenancyApi.Accept(Client, acmeToken, Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        Assert.NotNull(await InviteAsync(globexInvite));
        using var second = await TenancyApi.Accept(Client, globexToken, "Another-Passw0rd");
        await TenancyApi.AssertErrorAsync(second, HttpStatusCode.Conflict, "already_member");
        _ = await SessionApi.LoginAsync(Client, Address, Password);   // the first password stands
        Assert.Equal(acme, Assert.Single(await NonSeedMembershipsAsync()).CompanyId);
    }

    [Fact]
    public async Task Two_invitations_of_two_companies_accepted_at_once_make_one_membership()
    {
        var acme = await CreateCompanyAsync("Acme");
        var globex = await CreateCompanyAsync("Globex");
        var (_, acmeToken) = await MailedAsync(acme);
        var (_, globexToken) = await MailedAsync(globex);

        var statuses = await Task.WhenAll(new[] { acmeToken, globexToken }.Select(token => Task.Run(async () =>
        {
            using var response = await TenancyApi.Accept(Client, token, Password);
            return response.StatusCode;
        })));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.NoContent));
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Single(await NonSeedMembershipsAsync());
        Assert.Equal(1, await InDbAsync(db => db.Invites.CountAsync(TestContext.Current.CancellationToken)));   // the refused one stays
    }

    // ---- what is never logged

    [Fact]
    public async Task No_token_password_or_address_reaches_the_log()   // criterion 23
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);
        using (await TenancyApi.Accept(Client, token, "abc"))
        {
        }

        using (await TenancyApi.Accept(Client, token, Password))
        {
        }

        using (await TenancyApi.Accept(Client, token, Password))
        {
        }

        Assert.DoesNotContain(token, Logs.Text);
        Assert.DoesNotContain(Password, Logs.Text);
        Assert.DoesNotContain(Address, Logs.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Answers_never_set_a_cookie_and_are_never_stored()
    {
        var company = await CreateCompanyAsync("Acme");
        var (_, token) = await MailedAsync(company);

        using var preview = await TenancyApi.Preview(Client, token);
        using var invalid = await TenancyApi.Preview(Client, "unknown");

        AccountApi.AssertNeverStoredAndNoCookie(preview);
        AccountApi.AssertNeverStoredAndNoCookie(invalid);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: the build passes (the `TenancyApi` helpers are in step 1) and the new tests
  FAIL: the endpoints are not mapped, every call answers `404`.

- [ ] **Step 3: Implement.**

`src/Auth.Infrastructure/DependencyInjection.cs` — the change:

```diff
--- a/src/Auth.Infrastructure/DependencyInjection.cs
+++ b/src/Auth.Infrastructure/DependencyInjection.cs
@@ -31,6 +31,9 @@
             .AddIdentityCore<ApplicationUser>(options =>
             {
                 options.User.RequireUniqueEmail = true;
+                // The user name of an account is its email (invitations, spec 0005, create accounts for any address a
+                // member types), and Identity's default list of allowed characters would refuse `zażółć@example.com`.
+                options.User.AllowedUserNameCharacters = string.Empty;
                 // Spec 0004, Decision 9. Applies when a password is set; a login never checks it.
                 options.Password.RequiredLength = 8;
                 options.Password.RequireUppercase = true;
```

`src/Auth.Server/Api/EndpointMetadata.cs` — the change:

```diff
--- a/src/Auth.Server/Api/EndpointMetadata.cs
+++ b/src/Auth.Server/Api/EndpointMetadata.cs
@@ -4,6 +4,9 @@
 
 /// <summary>The body of every error of the API: <c>{"error":"&lt;code&gt;"}</c>.</summary>
 public sealed record ErrorBody(string Error);
+
+/// <summary>The <c>400 weak_password</c> body: the password policy rules (spec 0004) the password breaks.</summary>
+public sealed record WeakPasswordBody(string Error, IReadOnlyList<string> Rules);
 
 /// <summary>The codes an endpoint answers with at a status, so that the OpenAPI description can name them.</summary>
 public sealed record ErrorCodesMetadata(int Status, IReadOnlyList<string> Codes);
```

`src/Auth.Server/Api/InviteEndpoints.cs`:

```csharp
using Auth.Server.Account;
using Auth.Server.Requests;
using Auth.Server.Tenancy;

namespace Auth.Server.Api;

/// <summary>
/// <c>POST /auth/invites/preview</c> and <c>POST /auth/invites/accept</c> (spec 0005 → Invitations — public). They need
/// no access token: the link token is the credential, and whoever holds it can see and accept the invitation.
/// </summary>
public static class InviteEndpoints
{
    public const string PreviewPath = "/auth/invites/preview";
    public const string AcceptPath = "/auth/invites/accept";

    private static readonly string[] PreviewFields = ["token"];
    private static readonly string[] AcceptFields = ["token", "password"];

    public static async Task<IResult> PreviewAsync(HttpContext http, InviteAcceptance acceptance)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(acceptance);

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, PreviewFields, http.RequestAborted);
        if (fields is null)
        {
            return ApiResults.InvalidRequest();
        }

        var preview = await acceptance.PreviewAsync(fields[0], http.RequestAborted);
        return preview.Succeeded
            ? ApiResults.Ok(new InvitePreviewResponse(preview.Value!.OrgName, preview.Value.Email, preview.Value.Role))
            : ApiResults.Error(preview.Error!);
    }

    public static async Task<IResult> AcceptAsync(HttpContext http, InviteAcceptance acceptance)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(acceptance);

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, AcceptFields, http.RequestAborted);
        if (fields is null || fields[1].Contains('\0'))
        {
            return ApiResults.InvalidRequest();
        }

        var accepted = await acceptance.AcceptAsync(fields[0], fields[1], http.RequestAborted);
        if (accepted.Succeeded)
        {
            return ApiResults.NoContent();
        }

        return accepted.Error == TenancyErrors.WeakPassword
            ? AccountResults.WeakPassword(accepted.Value!)
            : ApiResults.Error(accepted.Error!);
    }
}
```

`src/Auth.Server/Api/TenancyEndpoints.cs` — the change:

```diff
--- a/src/Auth.Server/Api/TenancyEndpoints.cs
+++ b/src/Auth.Server/Api/TenancyEndpoints.cs
@@ -17,6 +17,18 @@
             .RequireAuthorization()
             .Produces<MeResponse>()
             .ProducesGuarded();
+
+        app.MapPost(InviteEndpoints.PreviewPath, InviteEndpoints.PreviewAsync)
+            .ReadsJson<PreviewInviteRequest>()
+            .Produces<InvitePreviewResponse>()
+            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken)
+            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember);
+        app.MapPost(InviteEndpoints.AcceptPath, InviteEndpoints.AcceptAsync)
+            .ReadsJson<AcceptInviteRequest>()
+            .Produces(StatusCodes.Status204NoContent)
+            .Produces<WeakPasswordBody>(StatusCodes.Status400BadRequest, "application/json")
+            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken, TenancyErrors.WeakPassword)
+            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember);
 
         var company = app.MapGroup(OrgEndpoints.OrgPath).RequireAuthorization();
 
```

`src/Auth.Server/Tenancy/Contracts.cs` — the change:

```diff
--- a/src/Auth.Server/Tenancy/Contracts.cs
+++ b/src/Auth.Server/Tenancy/Contracts.cs
@@ -22,3 +22,12 @@
 
 /// <summary>Request of <c>POST /auth/org/invites</c>.</summary>
 public sealed record SendInviteRequest(string Email, Guid RoleId);
+
+/// <summary>Request of <c>POST /auth/invites/preview</c>.</summary>
+public sealed record PreviewInviteRequest(string Token);
+
+/// <summary>Response of <c>POST /auth/invites/preview</c>.</summary>
+public sealed record InvitePreviewResponse(string OrgName, string Email, string Role);
+
+/// <summary>Request of <c>POST /auth/invites/accept</c>.</summary>
+public sealed record AcceptInviteRequest(string Token, string Password);
```

`src/Auth.Server/Tenancy/InviteAcceptance.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Auth.Server.Tenancy;

/// <summary>What the acceptance screen shows before it asks for a password.</summary>
public sealed record InvitePreview(string OrgName, string Email, string Role);

/// <summary>
/// Previews and accepts an invitation by its link token (spec 0005 → Invitations — public, Effects). Accepting is how
/// anyone becomes a member, and it is one transaction: it uses the invitation up, makes the account if there is none,
/// sets the password, confirms the email, ends every session of the account and makes it a member with the role of
/// the invitation — or, when anything is refused, changes nothing at all, the invitation included.
/// </summary>
public sealed class InviteAcceptance(
    AuthDbContext db, UserManager<ApplicationUser> users, IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations, TimeProvider clock)
{
    /// <summary>
    /// The company, address and role of a usable invitation; the token stays usable. Unknown, used, expired, cancelled and
    /// replaced tokens are all <c>invalid_token</c>. An address that already belongs to a company is <c>already_member</c>.
    /// </summary>
    public async Task<Outcome<InvitePreview>> PreviewAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var hash = EmailTokens.HashOf(token);
        var now = StorableTime.Now(clock);
        var row = await (
            from invite in db.Invites.AsNoTracking()
            join company in db.Companies.AsNoTracking() on invite.CompanyId equals company.Id
            join role in db.CompanyRoles.AsNoTracking() on invite.RoleId equals role.Id
            where invite.TokenHash == hash && invite.ExpiresAt > now
            select new { invite.Email, invite.NormalizedEmail, CompanyName = company.Name, RoleName = role.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return Outcome.Fail<InvitePreview>(TenancyErrors.InvalidToken);
        }

        return await IsMemberAsync(row.NormalizedEmail, cancellationToken)
            ? Outcome.Fail<InvitePreview>(TenancyErrors.AlreadyMember)
            : Outcome.Ok(new InvitePreview(row.CompanyName, row.Email, row.RoleName));
    }

    /// <summary>
    /// Accepts the invitation. Refused with <c>invalid_token</c>, with <c>weak_password</c> (the broken rules are the value;
    /// the token stays usable) or with <c>already_member</c> (the invitation stays usable until it expires).
    /// </summary>
    public async Task<Outcome<IReadOnlyList<string>>> AcceptAsync(string token, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(password);

        var hash = EmailTokens.HashOf(token);
        var now = StorableTime.Now(clock);

        // Leaving this method without a commit rolls everything back, the use of the invitation included.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The company first, as every change to a company's members does; then the invitation. Of parallel accepts with
        // one token the lock lets one in at a time, and the later one finds the invitation gone.
        var companyId = await db.Invites.Where(i => i.TokenHash == hash && i.ExpiresAt > now)
            .Select(i => (Guid?)i.CompanyId).FirstOrDefaultAsync(cancellationToken);
        if (companyId is not { } company || !await CompanyLock.AcquireAsync(db, company, cancellationToken))
        {
            return Refused(TenancyErrors.InvalidToken);
        }

        // ToListAsync, not SingleOrDefaultAsync: EF must send the statement as it is, not wrapped in a subquery.
        var consumed = await db.Invites
            .FromSql($"""DELETE FROM "Invites" WHERE "TokenHash" = {hash} AND "ExpiresAt" > {now} RETURNING *""")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (consumed.Count != 1)
        {
            return Refused(TenancyErrors.InvalidToken);
        }

        var invite = consumed[0];

        // One address, one acceptance at a time: two invitations of two companies accepted at once must not make the
        // same person a member of both (the tables would allow it; this version does not).
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({invite.NormalizedEmail}, 0))", cancellationToken);

        var user = await users.FindByEmailAsync(invite.Email);
        if (user is not null && await db.Memberships.AnyAsync(m => m.UserId == user.Id, cancellationToken))
        {
            return Refused(TenancyErrors.AlreadyMember);
        }

        var account = user ?? new ApplicationUser { UserName = invite.Email, Email = invite.Email };
        var broken = await PasswordRules.BrokenAsync(users, account, password);
        if (broken.Count > 0)
        {
            return new Outcome<IReadOnlyList<string>>(broken, TenancyErrors.WeakPassword);
        }

        // The link came through the mailbox of the address: that proves it, and it is why a password is set here.
        account.EmailConfirmed = true;
        if (user is null)
        {
            Ensure(await users.CreateAsync(account, password));
        }
        else
        {
            if (await users.HasPasswordAsync(user))
            {
                Ensure(await users.RemovePasswordAsync(user));
            }

            Ensure(await users.AddPasswordAsync(user, password));
            await EndEverySessionAsync(user, cancellationToken);
        }

        db.Memberships.Add(new Membership { UserId = account.Id, CompanyId = invite.CompanyId, RoleId = invite.RoleId, JoinedAt = now });
        await db.SaveChangesAsync(cancellationToken);

        // Not the request's token: a client that goes away now must not leave the outcome open.
        await transaction.CommitAsync(CancellationToken.None);
        return new Outcome<IReadOnlyList<string>>([]);
    }

    /// <summary>What a reset of spec 0004 does to the sessions of an account: every refresh token and its authorization, every other link, the streak.</summary>
    private async Task EndEverySessionAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var subject = user.Id.ToString();
        await tokens.RevokeBySubjectAsync(subject, cancellationToken);
        await authorizations.RevokeBySubjectAsync(subject, cancellationToken);
        await db.EmailTokens.Where(t => t.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);

        // Someone failing logins on purpose must not keep the owner out.
        var identifier = LoginIdentifier.HashOf(user.NormalizedEmail);
        await db.LoginStreaks.Where(s => s.IdentifierHash == identifier).ExecuteDeleteAsync(cancellationToken);
    }

    private Task<bool> IsMemberAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        (from user in db.Users
         join membership in db.Memberships on user.Id equals membership.UserId
         where user.NormalizedEmail == normalizedEmail
         select membership.UserId).AnyAsync(cancellationToken);

    private static Outcome<IReadOnlyList<string>> Refused(string error) => new(null, error);

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            // Codes only: descriptions can echo policy details, and a password must never be logged.
            throw new InvalidOperationException(
                "Could not set up the account: " + string.Join(", ", result.Errors.Select(e => e.Code)));
        }
    }
}
```

`src/Auth.Server/Tenancy/TenancyServices.cs` — the change:

```diff
--- a/src/Auth.Server/Tenancy/TenancyServices.cs
+++ b/src/Auth.Server/Tenancy/TenancyServices.cs
@@ -16,6 +16,7 @@
         services.AddScoped<CompanyService>();
         services.AddScoped<MembershipReader>();
         services.AddScoped<InvitationService>();
+        services.AddScoped<InviteAcceptance>();
         return services;
     }
 }
```

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS — 31 new tests, 655 in all.

- [ ] **Step 5: Commit** — `feat(tenancy): preview and accept an invitation`

### Task 10: Pruning expired invitations

**Files:**
- Modify: `src/Auth.Server/Email/EmailPruner.cs`, `src/Auth.Server/Email/EmailPruningService.cs`
- Test: `tests/Auth.IntegrationTests/InvitePruningTests.cs`

**Interfaces:**
- Consumes: Task 8 (`Invite.ExpiresAt`).
- Produces: `EmailPruner.PruneInvitesAsync(ct)` → the number removed (the invitations whose
  expiry has passed; their queued mails, if any, are dropped by the dispatcher). It is a
  method of its own, so that `PruneOnceAsync` and its tuple, which spec 0004's tests read,
  stay as they are. `EmailPruningService` runs both in its hourly pass: an expired invitation
  is gone within the day (spec), and an hour is well inside that.

- [ ] **Step 1: Write the failing tests.** One of them runs the service itself on the fake
  clock: calling the pruner by hand would not notice a service that forgot to call it.

`tests/Auth.IntegrationTests/InvitePruningTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class InvitePruningTests : TenancyTestBase
{
    public InvitePruningTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // Left out of the host so that the pass a test counts is its own; one test runs the service itself, in another class.
        Factory.WithoutHostedService<EmailPruningService>();
    }

    private Task<int> PruneAsync() =>
        Factory.Services.GetRequiredService<EmailPruner>().PruneInvitesAsync(TestContext.Current.CancellationToken);

    private Task<int> InviteCountAsync() => InDbAsync(db => db.Invites.CountAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Pruning_removes_expired_invitations_and_keeps_the_others()   // spec 0005 → Invitations
    {
        var company = await CreateCompanyAsync("Acme");
        var old = await AddInviteAsync(company, "old@acme.test", "user");
        Clock.Advance(TimeSpan.FromDays(1));
        var newer = await AddInviteAsync(company, "newer@acme.test", "user");
        Clock.Advance(InviteTokens.Lifetime - TimeSpan.FromDays(1));   // the first is exactly seven days old

        Assert.Equal(1, await PruneAsync());

        Assert.Null(await InviteAsync(old));
        Assert.NotNull(await InviteAsync(newer));
        Assert.Equal(0, await PruneAsync());
        Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, await PruneAsync());
        Assert.Equal(0, await InviteCountAsync());
    }

    [Fact]
    public async Task A_mailed_invitation_lives_seven_days_from_its_mail_not_from_its_making()
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, "a@acme.test", "user");
        Clock.Advance(TimeSpan.FromDays(3));
        await EnqueueInvitationAsync(invite, "a@acme.test");
        await DispatchAsync();

        Clock.Advance(TimeSpan.FromDays(6));   // nine days after the making, six after the mail

        Assert.Equal(0, await PruneAsync());
        Assert.NotNull(await InviteAsync(invite));
    }

    [Fact]
    public async Task Pruning_leaves_the_queue_and_the_mail_limits_alone()
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, "a@acme.test", "user");
        await EnqueueInvitationAsync(invite, "a@acme.test");
        Clock.Advance(InviteTokens.Lifetime);

        await PruneAsync();

        Assert.Equal(1, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
    }
}

public sealed class InvitePruningServiceTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    [Fact]
    public async Task The_hourly_pass_of_the_service_removes_expired_invitations()   // spec 0005: removed within a day
    {
        Assert.True(EmailPruningService.Interval <= TimeSpan.FromDays(1));
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, "a@acme.test", "user");

        Clock.Advance(InviteTokens.Lifetime + EmailPruningService.Interval);   // the timer of the service fires on the fake clock

        await Poll.UntilAsync(() => InviteAsync(invite).GetAwaiter().GetResult() is null, "the pruning service to remove the invitation");
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`PruneInvitesAsync` does not exist).

- [ ] **Step 3: Implement.**

`src/Auth.Server/Email/EmailPruner.cs` — the change:

```diff
--- a/src/Auth.Server/Email/EmailPruner.cs
+++ b/src/Auth.Server/Email/EmailPruner.cs
@@ -6,7 +6,8 @@
 /// <summary>
 /// One pruning pass over the tables of spec 0004: link tokens that have expired, and mail-limit rows without an
 /// accepted request for <see cref="MailLimitPolicy.Window"/>, which the rules already treat as absent. Anyone can
-/// create a limit row by submitting an address, so rows must expire.
+/// create a limit row by submitting an address, so rows must expire. Expired invitations (spec 0005) go in a method of
+/// their own, <see cref="PruneInvitesAsync"/>, which <see cref="EmailPruningService"/> runs in the same pass.
 /// </summary>
 public sealed partial class EmailPruner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<EmailPruner> logger)
 {
@@ -24,6 +25,26 @@
         return (tokens, limits);
     }
 
+    /// <summary>
+    /// Removes the invitations whose seven days are over (spec 0005 → Invitations): they have left the list already, and
+    /// an expired one is replaced when the address is invited again, so this only keeps the table from growing. A queued
+    /// mail for a removed invitation is dropped by the dispatcher.
+    /// </summary>
+    public async Task<int> PruneInvitesAsync(CancellationToken cancellationToken)
+    {
+        var now = clock.GetUtcNow();
+
+        await using var scope = scopes.CreateAsyncScope();
+        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
+        var invites = await db.Invites.Where(i => i.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
+
+        LogPrunedInvites(logger, invites);
+        return invites;
+    }
+
     [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Tokens} link token and {Limits} mail limit entries.")]
     private static partial void LogPruned(ILogger logger, int tokens, int limits);
+
+    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Invites} expired invitations.")]
+    private static partial void LogPrunedInvites(ILogger logger, int invites);
 }
```

`src/Auth.Server/Email/EmailPruningService.cs` — the change:

```diff
--- a/src/Auth.Server/Email/EmailPruningService.cs
+++ b/src/Auth.Server/Email/EmailPruningService.cs
@@ -1,6 +1,9 @@
 namespace Auth.Server.Email;
 
-/// <summary>Runs <see cref="EmailPruner"/> at host start and then every <see cref="Interval"/>.</summary>
+/// <summary>
+/// Runs <see cref="EmailPruner"/> at host start and then every <see cref="Interval"/>: the link tokens, the mail limits and
+/// the invitations. An expired invitation is gone within the day (spec 0005), and an hour is well inside that.
+/// </summary>
 public sealed partial class EmailPruningService(EmailPruner pruner, TimeProvider clock, ILogger<EmailPruningService> logger) : BackgroundService
 {
     public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
@@ -27,6 +30,7 @@
         try
         {
             await pruner.PruneOnceAsync(stoppingToken);
+            await pruner.PruneInvitesAsync(stoppingToken);
         }
         catch (Exception) when (stoppingToken.IsCancellationRequested)
         {
@@ -39,6 +43,6 @@
         }
     }
 
-    [LoggerMessage(Level = LogLevel.Error, Message = "Pruning of link tokens and mail limits failed; it will run again at the next interval.")]
+    [LoggerMessage(Level = LogLevel.Error, Message = "Pruning of link tokens, mail limits and invitations failed; it will run again at the next interval.")]
     private static partial void LogFailed(ILogger logger, Exception exception);
 }
```

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`,
  then `dotnet format --verify-no-changes`. Expected: PASS — 4 new tests, 659 in all
  (`EmailPruningTests` unedited). This is the end of Day 3.

- [ ] **Step 5: Commit** — `feat(tenancy): prune expired invitations`

---

## Day 4 — members and roles, with the three safety rules

When the day is done the company's admins can list members, change their roles
and remove them, and define the company's roles, and nobody can act on anything that holds
more than they do (a role, a member, an invitation), leave a company without a manager, or
change or remove themselves.

### Task 11: Members, and the guard that makes the safety rules race-free

**Files:**
- Create: `src/Auth.Server/Tenancy/CompanyGuard.cs`, `src/Auth.Server/Tenancy/CompanyPeople.cs`,
  `src/Auth.Server/Tenancy/MemberService.cs`, `src/Auth.Server/Api/OrgMemberEndpoints.cs`,
  `tests/Auth.IntegrationTests/OrgMemberTests.cs`
- Modify: `src/Auth.Server/Tenancy/InvitationService.cs`, `src/Auth.Server/Api/OrgInviteEndpoints.cs`,
  `src/Auth.Server/Tenancy/Contracts.cs`, `src/Auth.Server/Tenancy/TenancyServices.cs`,
  `src/Auth.Server/Api/TenancyEndpoints.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs`,
  `tests/Auth.IntegrationTests/OrgInviteTests.cs` (three tests added)

**Interfaces:**
- Consumes: Task 8 (`Actor`, `CompanyLock`, `InvitationService`), Task 5 (`MembershipReader`).
- Produces: `CompanyGuard` (scoped) with
  `EnterAsync(actor, companyId, permission, ct)` → `Outcome<Actor>`: the first step of
  everything that changes a company. It takes the company lock and then — for a member —
  **reads their membership and role again under the lock**; if they are no longer a member
  of the company or no longer hold the permission it is `permissions_changed`, and what
  follows uses the actor as the database now has them. The endpoint checked the caller
  before it called the service, but another request may have demoted or removed them since.
- Produces: `CompanyPeople` — the members of a company and what their roles hold, loaded
  inside the locked transaction: `Managers()`, `ManagersIfMemberHad(userId, roleId)`,
  `ManagersWithout(userId)`, `ManagersIfRoleHeld(roleId, permissions)` and
  `LeavesNone(before, after)` — **rule 2** as a pure function: a change is refused when it
  takes a company that has a manager down to none; a company that has none already (the
  operator removed the last with `--force`) is not stopped from changing anything else.
  A manager is a member whose role grants `members:manage`, directly or through `*`,
  against the active catalog.
- Produces: `MemberService` (scoped), each change in one transaction that begins with
  `CompanyGuard.EnterAsync(…, members:manage)`:
  - `ListAsync(companyId, ct)` — members sorted by address, ordinally, with role and `joined_at`.
  - `ChangeRoleAsync(actor, companyId, userId, roleId, ct)`: `not_found` (the member or the
    role is not in the company), `cannot_change_self` (rule 3, before rule 1: a manager who
    asks for a bigger role for themselves is told `cannot_change_self`), `permission_not_held`
    (rule 1, both ways: the role the member would get **and the role they have now** must
    hold nothing the actor lacks, and a role with `*` only by an actor whose role holds
    `*`), `last_manager` (rule 2). Ends no session: the member's next refresh carries the
    change.
  - `RemoveAsync(actor, companyId, userId, force, ct)`: `not_found`, `cannot_change_self`,
    `permission_not_held` (rule 1: the member's current role holds something the actor
    lacks — a lesser manager cannot push out an admin), `last_manager` unless `force`; then the membership is deleted and every token and
    authorization of the account is revoked, so every refresh token issued before is refused
    with the `401` of spec 0002. The account stays.
- Changes: `InvitationService` takes `CompanyGuard` and enters through it; `ResendAsync` and
  `CancelAsync` take the `Actor` (the endpoints pass the caller) and apply **rule 1 to the role
  of the invitation**: once the invitation is found (`not_found` otherwise, as before), a role
  that holds a permission the actor lacks — `*` by an actor without `*` — is
  `permission_not_held`, for a cancel as for a resend; the operator is exempt, and a cancel is
  still never subject to the mail limit. The tests of Task 8 stand as they are; three tests
  are added to `OrgInviteTests` (a lesser manager cannot resend or cancel an invitation for a
  bigger role, an equal or smaller role may be, the operator may do both).
- Produces: `GET /auth/org/members`, `PUT /auth/org/members/{user_id}/role` `{"role_id"}`
  (`204`), `DELETE /auth/org/members/{user_id}` (`204`), all `members:manage`;
  `MemberItem`, `MembersResponse`, `ChangeMemberRoleRequest`. The route parameter is named
  `user_id`, so the handlers bind it with `[FromRoute(Name = "user_id")]`.
- The operator is exempt from rule 1 in all of its parts: `Actor.Operator` passes `MayGrant`
  always, so `remove-member` is unaffected.
- Produces (tests): `TenancyTestBase.CompanyWithAdminAsync`, `TenantOfAsync`.

Rule 2 is not reachable through the member endpoints for a request that is honest and alone:
the caller holds `members:manage` and cannot touch their own membership (rule 3), so after
any change they are still a manager. It is reached through the operator (service level,
`--force` lifts it), through a role edit by someone who manages roles but not members
(Task 12), and through a race, which the guard and the lock turn into one winner. The tests
say so.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs
+++ b/tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs
@@ -31,6 +31,22 @@
         var created = await scope.ServiceProvider.GetRequiredService<CompanyService>().CreateAsync(name, TestContext.Current.CancellationToken);
         Assert.True(created.Succeeded, created.Error);
         return created.Value;
+    }
+
+    /// <summary>A company with an admin who is logged in: the company, the admin's user id and their access token.</summary>
+    protected async Task<(Guid Company, Guid AdminId, string Token)> CompanyWithAdminAsync(string name = "Acme", string email = "boss@acme.test")
+    {
+        var company = await CreateCompanyAsync(name);
+        var admin = await AddMemberAsync(company, email, "admin");
+        return (company, admin, (await SessionApi.LoginAsync(Client, email, UserPassword)).AccessToken);
+    }
+
+    /// <summary>What the company API would know of the caller: their membership and role as the database has them now.</summary>
+    protected async Task<TenantContext> TenantOfAsync(Guid userId)
+    {
+        using var scope = Factory.Services.CreateScope();
+        return await scope.ServiceProvider.GetRequiredService<MembershipReader>().ReadAsync(userId, TestContext.Current.CancellationToken)
+            ?? throw new InvalidOperationException("The user belongs to no company.");
     }
 
     /// <summary>The company the development seed made.</summary>
```

`tests/Auth.IntegrationTests/OrgInviteTests.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/OrgInviteTests.cs
+++ b/tests/Auth.IntegrationTests/OrgInviteTests.cs
@@ -37,6 +37,12 @@
     {
         using var response = await TenancyApi.Get(Client, InvitesPath, token);
         return await TenancyApi.ReadOkAsync(response);
+    }
+
+    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
+    {
+        using var scope = Factory.Services.CreateScope();
+        return await work(scope.ServiceProvider);
     }
 
     private Task<List<Invite>> InvitesAsync() =>
@@ -386,6 +392,74 @@
         Assert.Equal("first@acme.test", Assert.Single(Mail.Sent).To);
     }
 
+    [Fact]
+    public async Task Nobody_resends_or_cancels_an_invitation_whose_role_holds_more_than_they_do()   // criterion 15
+    {
+        var company = await CreateCompanyAsync("Acme");
+        await AddRoleAsync(company, "manager", "members:manage", "reports:read");
+        await AddMemberAsync(company, "mgr@acme.test", "manager");
+        await AddRoleAsync(company, "wider", "reports:read", "reports:approve");
+        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;
+        var wider = await AddInviteAsync(company, "a@acme.test", "wider");
+        var star = await AddInviteAsync(company, "b@acme.test", "admin");
+
+        foreach (var id in new[] { wider, star })
+        {
+            using var resend = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{id}/resend", token);
+            await TenancyApi.AssertErrorAsync(resend, HttpStatusCode.Forbidden, "permission_not_held");
+            using var cancel = await TenancyApi.Send(Client, HttpMethod.Delete, $"{InvitesPath}/{id}", token);
+            await TenancyApi.AssertErrorAsync(cancel, HttpStatusCode.Forbidden, "permission_not_held");
+        }
+
+        Assert.Equal(2, (await InvitesAsync()).Count);
+        Assert.Empty(await QueueAsync());
+    }
+
+    [Fact]
+    public async Task A_caller_resends_and_cancels_invitations_whose_role_holds_what_they_hold_or_less()   // criterion 15
+    {
+        var company = await CreateCompanyAsync("Acme");
+        await AddRoleAsync(company, "manager", "members:manage", "reports:read");
+        await AddMemberAsync(company, "mgr@acme.test", "manager");
+        await AddRoleAsync(company, "narrower", "reports:read");
+        await AddRoleAsync(company, "plain");
+        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;
+        var own = await AddInviteAsync(company, "a@acme.test", "manager");
+        var subset = await AddInviteAsync(company, "b@acme.test", "narrower");
+        var nothing = await AddInviteAsync(company, "c@acme.test", "plain");
+
+        foreach (var id in new[] { own, subset })
+        {
+            using var resend = await TenancyApi.Send(Client, HttpMethod.Post, $"{InvitesPath}/{id}/resend", token);
+            await TenancyApi.AssertEmptyAsync(resend, HttpStatusCode.Accepted);
+        }
+
+        foreach (var id in new[] { own, subset, nothing })
+        {
+            using var cancel = await TenancyApi.Send(Client, HttpMethod.Delete, $"{InvitesPath}/{id}", token);
+            await TenancyApi.AssertEmptyAsync(cancel, HttpStatusCode.NoContent);
+        }
+
+        Assert.Empty(await InvitesAsync());
+    }
+
+    [Fact]
+    public async Task The_operator_resends_and_cancels_any_invitation()   // the operator is exempt from rule 1
+    {
+        var company = await CreateCompanyAsync("Acme");
+        var star = await AddInviteAsync(company, "a@acme.test", "admin");
+        var other = await AddInviteAsync(company, "b@acme.test", "admin");
+
+        var resend = await InScopeAsync(sp => sp.GetRequiredService<InvitationService>()
+            .ResendAsync(Actor.Operator, company, star, TestContext.Current.CancellationToken));
+        var cancel = await InScopeAsync(sp => sp.GetRequiredService<InvitationService>()
+            .CancelAsync(Actor.Operator, company, other, TestContext.Current.CancellationToken));
+
+        Assert.True(resend.Succeeded, resend.Error);
+        Assert.True(cancel.Succeeded, cancel.Error);
+        Assert.Equal(star, Assert.Single(await InvitesAsync()).Id);
+    }
+
     // ---- listing
 
     [Fact]
```

`tests/Auth.IntegrationTests/OrgMemberTests.cs`:

```csharp
using System.Net;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class OrgMemberTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string MembersPath = "/auth/org/members";

    private static string RolePath(Guid user) => $"{MembersPath}/{user}/role";

    private Task<HttpResponseMessage> ChangeRoleAsync(string token, Guid user, Guid role) =>
        TenancyApi.Send(Client, HttpMethod.Put, RolePath(user), token, new { role_id = role });

    private Task<HttpResponseMessage> RemoveAsync(string token, Guid user) =>
        TenancyApi.Send(Client, HttpMethod.Delete, $"{MembersPath}/{user}", token);

    private Task<Membership?> MembershipAsync(Guid user) =>
        InDbAsync(db => db.Memberships.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == user, TestContext.Current.CancellationToken));

    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    // ---- listing

    [Fact]
    public async Task List_shows_the_members_sorted_by_address_with_their_roles_and_join_times()
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        await AddMemberAsync(company, "zed@acme.test", "user");
        await AddMemberAsync(company, "Amy@acme.test", "user");

        using var response = await TenancyApi.Get(Client, MembersPath, token);

        var list = await TenancyApi.ReadOkAsync(response);
        Assert.Equal(["members"], list.EnumerateObject().Select(p => p.Name));
        var members = list.GetProperty("members").EnumerateArray().ToList();
        Assert.Equal(["Amy@acme.test", "boss@acme.test", "zed@acme.test"], members.Select(m => m.GetProperty("email").GetString()));   // ordinal
        Assert.Equal(["email", "joined_at", "role", "user_id"], members[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["id", "name"], members[0].GetProperty("role").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("user", members[0].GetProperty("role").GetProperty("name").GetString());
        Assert.Equal("admin", members[1].GetProperty("role").GetProperty("name").GetString());
        Assert.EndsWith("Z", members[0].GetProperty("joined_at").GetString());
        Assert.Equal((await InDbAsync(db => db.Users.Where(u => u.Email == "Amy@acme.test").Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken))).ToString(),
            members[0].GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task List_holds_no_member_of_another_company()   // criterion 14
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        var globex = await CreateCompanyAsync("Globex");
        await AddMemberAsync(globex, "carol@globex.test", "user");

        using var response = await TenancyApi.Get(Client, MembersPath, token);

        var emails = (await TenancyApi.ReadOkAsync(response)).GetProperty("members").EnumerateArray().Select(m => m.GetProperty("email").GetString());
        Assert.Equal(["boss@acme.test"], emails);
    }

    // ---- changing a role

    [Fact]
    public async Task Manager_changes_the_role_of_a_member()   // criterion 12
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");

        using var response = await ChangeRoleAsync(token, worker, await RoleIdAsync(company, "admin"));

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(await RoleIdAsync(company, "admin"), (await MembershipAsync(worker))!.RoleId);
    }

    [Fact]
    public async Task The_change_ends_no_session_and_reaches_the_members_next_refresh()   // criterion 12
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);

        using (var response = await ChangeRoleAsync(token, worker, await RoleIdAsync(company, "admin")))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);
        Assert.Equal(["admin"], AccessTokens.Array(refreshed.AccessToken, "roles"));
        Assert.Contains("members:manage", AccessTokens.Array(refreshed.AccessToken, "permissions"));
        _ = await SessionApi.RefreshOk(Client, refreshed.RefreshToken);
    }

    [Fact]
    public async Task A_user_or_role_outside_the_callers_company_is_a_404_like_one_that_does_not_exist()   // criterion 14
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var globex = await CreateCompanyAsync("Globex");
        var stranger = await AddMemberAsync(globex, "carol@globex.test", "user");
        var own = await RoleIdAsync(company, "user");

        foreach (var (user, role) in new[] { (stranger, own), (Guid.NewGuid(), own), (worker, await RoleIdAsync(globex, "admin")), (worker, Guid.NewGuid()) })
        {
            using var response = await ChangeRoleAsync(token, user, role);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        Assert.Equal(await RoleIdAsync(globex, "user"), (await MembershipAsync(stranger))!.RoleId);   // untouched
    }

    [Fact]
    public async Task Nobody_changes_their_own_role()   // criterion 17
    {
        var (company, admin, token) = await CompanyWithAdminAsync();

        foreach (var role in new[] { "user", "admin" })   // not even to the role they have
        {
            using var response = await ChangeRoleAsync(token, admin, await RoleIdAsync(company, role));
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "cannot_change_self");
        }

        Assert.Equal(await RoleIdAsync(company, "admin"), (await MembershipAsync(admin))!.RoleId);
    }

    [Fact]
    public async Task Rule_3_is_answered_before_rule_1_when_the_role_asked_for_is_their_own_change_and_too_big()   // spec 0005 → the order of checks
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage");
        var manager = await AddMemberAsync(company, "mgr@acme.test", "manager");
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;

        using var response = await ChangeRoleAsync(token, manager, await RoleIdAsync(company, "admin"));   // star: more than they hold

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "cannot_change_self");   // not permission_not_held
    }

    [Fact]
    public async Task Nobody_gives_a_role_that_holds_a_permission_they_lack()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage", "reports:read");
        await AddMemberAsync(company, "mgr@acme.test", "manager");
        var starter = await AddRoleAsync(company, "starter");   // a role the caller may touch: it holds nothing
        var worker = await AddMemberAsync(company, "worker@acme.test", "starter");
        var wider = await AddRoleAsync(company, "wider", "reports:read", "reports:approve");
        var narrower = await AddRoleAsync(company, "narrower", "reports:read");
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;

        using (var tooMuch = await ChangeRoleAsync(token, worker, wider))
        {
            await TenancyApi.AssertErrorAsync(tooMuch, HttpStatusCode.Forbidden, "permission_not_held");
        }

        using (var star = await ChangeRoleAsync(token, worker, await RoleIdAsync(company, "admin")))
        {
            await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        }

        Assert.Equal(starter, (await MembershipAsync(worker))!.RoleId);   // unchanged
        using var subset = await ChangeRoleAsync(token, worker, narrower);
        Assert.Equal(HttpStatusCode.NoContent, subset.StatusCode);
    }

    [Fact]
    public async Task A_caller_that_lists_every_permission_but_not_star_cannot_give_a_role_with_star()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        var everything = await AddRoleAsync(company, "everything", [.. Holder.Current.Catalog.Permissions]);
        await AddMemberAsync(company, "all@acme.test", "everything");
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var token = (await SessionApi.LoginAsync(Client, "all@acme.test", UserPassword)).AccessToken;

        using var star = await ChangeRoleAsync(token, worker, await RoleIdAsync(company, "admin"));
        await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        using var same = await ChangeRoleAsync(token, worker, everything);
        Assert.Equal(HttpStatusCode.NoContent, same.StatusCode);
    }

    // ---- removing

    [Fact]
    public async Task Removing_a_member_ends_the_membership_and_every_session_and_keeps_the_account()   // criterion 13
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var first = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        var second = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);

        using (var response = await RemoveAsync(token, worker))
        {
            await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        }

        Assert.Null(await MembershipAsync(worker));
        Assert.NotNull(await InDbAsync(db => db.Users.FindAsync([worker], TestContext.Current.CancellationToken).AsTask()));   // the account stays
        foreach (var session in new[] { first, second })
        {
            using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);
            await SessionApi.AssertInvalidGrantAsync(refresh);
        }

        using var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword);
        Assert.Equal("""{"error":"no_membership"}""", await login.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Fact]
    public async Task A_removed_member_can_be_invited_again_but_the_old_sessions_stay_ended()   // criterion 13
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var old = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        var boss = (await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword)).AccessToken;
        using (var removed = await RemoveAsync(boss, worker))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        var globex = await CreateCompanyAsync("Globex");
        var invite = await AddInviteAsync(globex, "worker@acme.test", "user");
        await EnqueueInvitationAsync(invite, "worker@acme.test");
        await DispatchAsync();
        using (var accepted = await TenancyApi.Accept(Client, TokenIn(Assert.Single(Mail.Sent)), "Chosen-Passw0rd"))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        var again = await SessionApi.LoginAsync(Client, "worker@acme.test", "Chosen-Passw0rd");
        Assert.Equal(globex.ToString(), AccessTokens.Text(again.AccessToken, "org_id"));
        using var refresh = await SessionApi.Refresh(Client, old.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refresh);
    }

    [Fact]
    public async Task A_removed_members_access_token_is_refused_by_the_company_api_at_once()   // criterion 13
    {
        var (company, _, bossToken) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "admin");
        var workerToken = (await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword)).AccessToken;

        using (var removed = await RemoveAsync(bossToken, worker))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var response = await TenancyApi.Get(Client, MembersPath, workerToken);
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
    }

    [Fact]
    public async Task Nobody_removes_themselves()   // criterion 17
    {
        var (_, admin, token) = await CompanyWithAdminAsync();

        using var response = await RemoveAsync(token, admin);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "cannot_change_self");
        Assert.NotNull(await MembershipAsync(admin));
    }

    [Fact]
    public async Task Removing_someone_outside_the_company_or_unknown_is_a_404()   // criterion 14
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        var globex = await CreateCompanyAsync("Globex");
        var stranger = await AddMemberAsync(globex, "carol@globex.test", "user");

        foreach (var user in new[] { stranger, Guid.NewGuid() })
        {
            using var response = await RemoveAsync(token, user);
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
        }

        Assert.NotNull(await MembershipAsync(stranger));
    }

    [Fact]
    public async Task Nobody_removes_a_member_who_holds_more_than_they_do()   // criterion 15: a lesser manager cannot push out an admin
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage");
        await AddMemberAsync(company, "mgr@acme.test", "manager");
        var admin = await AddMemberAsync(company, "boss@acme.test", "admin");   // another manager is left, through star
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;

        using var response = await RemoveAsync(token, admin);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permission_not_held");
        Assert.NotNull(await MembershipAsync(admin));
    }

    [Fact]
    public async Task Nobody_changes_the_role_of_a_member_who_holds_more_than_they_do_not_even_to_a_lesser_role()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage");
        await AddMemberAsync(company, "mgr@acme.test", "manager");
        var admin = await AddMemberAsync(company, "boss@acme.test", "admin");
        var plain = await AddRoleAsync(company, "plain");   // nothing: a role the caller could give
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;

        using var response = await ChangeRoleAsync(token, admin, plain);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permission_not_held");
        Assert.Equal(await RoleIdAsync(company, "admin"), (await MembershipAsync(admin))!.RoleId);
    }

    [Fact]
    public async Task A_caller_that_lists_every_permission_but_not_star_cannot_touch_a_member_with_star()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "everything", [.. Holder.Current.Catalog.Permissions]);
        await AddMemberAsync(company, "all@acme.test", "everything");
        var admin = await AddMemberAsync(company, "boss@acme.test", "admin");
        var token = (await SessionApi.LoginAsync(Client, "all@acme.test", UserPassword)).AccessToken;

        using var removed = await RemoveAsync(token, admin);
        using var changed = await ChangeRoleAsync(token, admin, await RoleIdAsync(company, "user"));

        await TenancyApi.AssertErrorAsync(removed, HttpStatusCode.Forbidden, "permission_not_held");
        await TenancyApi.AssertErrorAsync(changed, HttpStatusCode.Forbidden, "permission_not_held");
    }

    [Fact]
    public async Task A_member_whose_role_is_a_subset_of_the_callers_may_be_removed_and_re_roled()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage", "reports:read");
        await AddMemberAsync(company, "mgr@acme.test", "manager");
        var narrower = await AddRoleAsync(company, "narrower", "reports:read");
        var equal = await AddRoleAsync(company, "equal", "members:manage", "reports:read");
        var first = await AddMemberAsync(company, "first@acme.test", "narrower");
        var second = await AddMemberAsync(company, "second@acme.test", "equal");
        var third = await AddMemberAsync(company, "third@acme.test", "narrower");
        var token = (await SessionApi.LoginAsync(Client, "mgr@acme.test", UserPassword)).AccessToken;

        using (var re = await ChangeRoleAsync(token, first, equal))   // from a subset to the caller's own permissions
        {
            Assert.Equal(HttpStatusCode.NoContent, re.StatusCode);
        }

        using (var down = await ChangeRoleAsync(token, second, narrower))   // a member with exactly the caller's permissions
        {
            Assert.Equal(HttpStatusCode.NoContent, down.StatusCode);
        }

        using var removed = await RemoveAsync(token, third);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Fact]
    public async Task The_operator_may_touch_any_member()   // the operator is exempt from rule 1
    {
        var company = await CreateCompanyAsync("Acme");
        await AddMemberAsync(company, "boss@acme.test", "admin");
        var second = await AddMemberAsync(company, "second@acme.test", "admin");
        var third = await AddMemberAsync(company, "third@acme.test", "admin");
        var plain = await RoleIdAsync(company, "user");

        var changed = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .ChangeRoleAsync(Actor.Operator, company, second, plain, TestContext.Current.CancellationToken));
        var removed = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .RemoveAsync(Actor.Operator, company, third, force: false, TestContext.Current.CancellationToken));

        Assert.True(changed.Succeeded, changed.Error);
        Assert.True(removed.Succeeded, removed.Error);
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("PUT", "/11111111-1111-1111-1111-111111111111/role")]
    [InlineData("DELETE", "/11111111-1111-1111-1111-111111111111")]
    public async Task Every_endpoint_needs_a_token_and_members_manage_checked_against_the_database(string method, string suffix)   // criterion 14
    {
        var company = await CreateCompanyAsync("Acme");
        var worker = await AddMemberAsync(company, "worker@acme.test", "admin");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        var path = MembersPath + suffix;
        object? body = method == "PUT" ? new { role_id = Guid.NewGuid() } : null;

        using (var anonymous = await TenancyApi.Send(Client, new HttpMethod(method), path, null, body))
        {
            await TenancyApi.AssertUnauthorizedAsync(anonymous);
        }

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "user"));
        using (var stale = await TenancyApi.Send(Client, new HttpMethod(method), path, session.AccessToken, body))
        {
            await TenancyApi.AssertErrorAsync(stale, HttpStatusCode.Forbidden, "permissions_changed");
        }

        var fresh = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using var plain = await TenancyApi.Send(Client, new HttpMethod(method), path, fresh.AccessToken, body);
        await TenancyApi.AssertErrorAsync(plain, HttpStatusCode.Forbidden, "forbidden");
    }

    [Theory]
    [InlineData("not-a-uuid", """{"role_id":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("11111111-1111-1111-1111-111111111111", """{"role_id":"not-a-uuid"}""")]
    [InlineData("11111111-1111-1111-1111-111111111111", "{}")]
    [InlineData("11111111-1111-1111-1111-111111111111", """{"role_id":7}""")]
    [InlineData("11111111-1111-1111-1111-111111111111", "not json")]
    public async Task A_malformed_id_or_body_is_a_400(string user, string body)
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        using var request = TenancyApi.Request(HttpMethod.Put, $"{MembersPath}/{user}/role", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_removal_with_a_path_that_is_not_a_uuid_is_a_400()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var response = await TenancyApi.Send(Client, HttpMethod.Delete, $"{MembersPath}/not-a-uuid", token);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    // ---- safety rule 2

    [Fact]
    public async Task The_operator_cannot_remove_the_last_manager_unless_forced()   // criteria 16, 22
    {
        var (company, admin, _) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");

        var refused = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .RemoveAsync(Actor.Operator, company, admin, force: false, TestContext.Current.CancellationToken));
        Assert.Equal("last_manager", refused.Error);
        Assert.NotNull(await MembershipAsync(admin));

        var plain = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .RemoveAsync(Actor.Operator, company, worker, force: false, TestContext.Current.CancellationToken));   // not a manager: fine
        Assert.True(plain.Succeeded);

        var forced = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .RemoveAsync(Actor.Operator, company, admin, force: true, TestContext.Current.CancellationToken));
        Assert.True(forced.Succeeded);
        Assert.Null(await MembershipAsync(admin));
    }

    [Fact]
    public async Task A_role_change_that_would_leave_no_manager_is_refused()   // criterion 16
    {
        var (company, admin, _) = await CompanyWithAdminAsync();
        var plain = await RoleIdAsync(company, "user");

        // Only the operator can ask this of the service: through the API a member who is asking is a manager themselves.
        var refused = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .ChangeRoleAsync(Actor.Operator, company, admin, plain, TestContext.Current.CancellationToken));

        Assert.Equal("last_manager", refused.Error);
        Assert.Equal(await RoleIdAsync(company, "admin"), (await MembershipAsync(admin))!.RoleId);
    }

    [Fact]
    public async Task A_company_without_a_manager_is_not_stopped_from_changing_a_plain_member()
    {
        var company = await CreateCompanyAsync("Acme");
        var first = await AddMemberAsync(company, "a@acme.test", "user");
        await AddMemberAsync(company, "b@acme.test", "user");
        var narrower = await AddRoleAsync(company, "narrower", "reports:read");

        var outcome = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .ChangeRoleAsync(Actor.Operator, company, first, narrower, TestContext.Current.CancellationToken));

        Assert.True(outcome.Succeeded);   // it had no manager before and has none after
    }

    [Fact]
    public async Task Two_managers_removing_each_other_at_once_leave_one_manager()   // criterion 16
    {
        for (var round = 0; round < 4; round++)
        {
            var company = await CreateCompanyAsync($"Acme {round}");
            await AddRoleAsync(company, "manager", "members:manage");
            var a = await AddMemberAsync(company, $"a{round}@acme.test", "manager");
            var b = await AddMemberAsync(company, $"b{round}@acme.test", "manager");
            var tokenA = (await SessionApi.LoginAsync(Client, $"a{round}@acme.test", UserPassword)).AccessToken;
            var tokenB = (await SessionApi.LoginAsync(Client, $"b{round}@acme.test", UserPassword)).AccessToken;

            var answers = await Task.WhenAll(RemoveAsync(tokenA, b), RemoveAsync(tokenB, a));
            try
            {
                var winner = Assert.Single(answers, r => r.StatusCode == HttpStatusCode.NoContent);
                var loser = Assert.Single(answers, r => r != winner);
                await TenancyApi.AssertErrorAsync(loser, HttpStatusCode.Forbidden, "permissions_changed");   // removed before its turn
            }
            finally
            {
                foreach (var answer in answers)
                {
                    answer.Dispose();
                }
            }

            Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.CompanyId == company, TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task A_request_that_was_authorised_before_the_caller_was_removed_is_refused_under_the_lock()   // the race, made deterministic
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "manager", "members:manage");
        var a = await AddMemberAsync(company, "a@acme.test", "manager");
        var b = await AddMemberAsync(company, "b@acme.test", "manager");
        var asA = Actor.Of(await TenantOfAsync(a));   // what the endpoints of the two requests had read
        var asB = Actor.Of(await TenantOfAsync(b));

        var first = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .RemoveAsync(asA, company, b, force: false, TestContext.Current.CancellationToken));
        Assert.True(first.Succeeded);

        var second = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .RemoveAsync(asB, company, a, force: false, TestContext.Current.CancellationToken));

        Assert.Equal("permissions_changed", second.Error);
        Assert.NotNull(await MembershipAsync(a));
    }

    [Fact]
    public async Task A_caller_demoted_before_the_lock_cannot_change_a_role_or_invite()
    {
        var company = await CreateCompanyAsync("Acme");
        var boss = await AddMemberAsync(company, "boss@acme.test", "admin");
        var worker = await AddMemberAsync(company, "worker@acme.test", "user");
        var stale = Actor.Of(await TenantOfAsync(boss));
        await SetMemberRoleAsync(boss, await RoleIdAsync(company, "user"));   // demoted after the endpoint read them
        var admin = await RoleIdAsync(company, "admin");

        var change = await InScopeAsync(sp => sp.GetRequiredService<MemberService>()
            .ChangeRoleAsync(stale, company, worker, admin, TestContext.Current.CancellationToken));
        var invite = await InScopeAsync(sp => sp.GetRequiredService<InvitationService>()
            .SendAsync(stale, company, "new@acme.test", "NEW@ACME.TEST", admin, TestContext.Current.CancellationToken));

        Assert.Equal("permissions_changed", change.Error);
        Assert.Equal("permissions_changed", invite.Error);
        Assert.Equal(await RoleIdAsync(company, "user"), (await MembershipAsync(worker))!.RoleId);
        Assert.Equal(0, await InDbAsync(db => db.Invites.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Responses_are_never_stored_and_set_no_cookie()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var response = await TenancyApi.Get(Client, MembersPath, token);

        AccountApi.AssertNeverStoredAndNoCookie(response);
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`MemberService` does not exist).

- [ ] **Step 3: Implement.**

`src/Auth.Server/Api/OrgInviteEndpoints.cs` — the change:

```diff
--- a/src/Auth.Server/Api/OrgInviteEndpoints.cs
+++ b/src/Auth.Server/Api/OrgInviteEndpoints.cs
@@ -79,7 +79,7 @@
             return ApiResults.InvalidRequest();
         }
 
-        var outcome = await invitations.ResendAsync(caller.CompanyId, inviteId, http.RequestAborted);
+        var outcome = await invitations.ResendAsync(Actor.Of(caller), caller.CompanyId, inviteId, http.RequestAborted);
         if (!outcome.Succeeded)
         {
             return ApiResults.Refused(outcome);
@@ -106,7 +106,7 @@
             return ApiResults.InvalidRequest();
         }
 
-        var outcome = await invitations.CancelAsync(caller.CompanyId, inviteId, http.RequestAborted);
+        var outcome = await invitations.CancelAsync(Actor.Of(caller), caller.CompanyId, inviteId, http.RequestAborted);
         return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
     }
 }
```

`src/Auth.Server/Api/OrgMemberEndpoints.cs`:

```csharp
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Auth.Server.Api;

/// <summary>
/// The members of the caller's company (spec 0005 → Company API): list, change a role, remove. All need
/// <c>members:manage</c>, checked against the database.
/// </summary>
public static class OrgMemberEndpoints
{
    private static readonly string[] RoleFields = ["role_id"];

    public static async Task<IResult> ListAsync(HttpContext http, MembershipReader memberships, MemberService members)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(members);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        return access.Caller is { } caller
            ? ApiResults.Ok(new MembersResponse(await members.ListAsync(caller.CompanyId, http.RequestAborted)))
            : access.Failure!;
    }

    public static async Task<IResult> ChangeRoleAsync([FromRoute(Name = "user_id")] string userId, HttpContext http, MembershipReader memberships, MemberService members)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(members);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, RoleFields, http.RequestAborted);
        if (!IdInput.TryParse(userId, out var user) || fields is null || !IdInput.TryParse(fields[0], out var role))
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await members.ChangeRoleAsync(Actor.Of(caller), caller.CompanyId, user, role, http.RequestAborted);
        return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
    }

    public static async Task<IResult> RemoveAsync([FromRoute(Name = "user_id")] string userId, HttpContext http, MembershipReader memberships, MemberService members)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(members);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.MembersManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        if (!IdInput.TryParse(userId, out var user))
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await members.RemoveAsync(Actor.Of(caller), caller.CompanyId, user, force: false, http.RequestAborted);
        return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
    }
}
```

`src/Auth.Server/Api/TenancyEndpoints.cs` — the change:

```diff
--- a/src/Auth.Server/Api/TenancyEndpoints.cs
+++ b/src/Auth.Server/Api/TenancyEndpoints.cs
@@ -65,6 +65,24 @@
             .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
             .ProducesGuarded();
 
+        company.MapGet("members", OrgMemberEndpoints.ListAsync)
+            .Produces<MembersResponse>()
+            .ProducesGuarded();
+        company.MapPut("members/{user_id}/role", OrgMemberEndpoints.ChangeRoleAsync)
+            .ReadsJson<ChangeMemberRoleRequest>()
+            .Produces(StatusCodes.Status204NoContent)
+            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
+            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
+            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
+            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.CannotChangeSelf, TenancyErrors.LastManager)
+            .ProducesGuarded();
+        company.MapDelete("members/{user_id}", OrgMemberEndpoints.RemoveAsync)
+            .Produces(StatusCodes.Status204NoContent)
+            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
+            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
+            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.CannotChangeSelf, TenancyErrors.LastManager)
+            .ProducesGuarded();
+
         return app;
     }
 }
```

`src/Auth.Server/Tenancy/CompanyGuard.cs`:

```csharp
using Auth.Infrastructure.Persistence;

namespace Auth.Server.Tenancy;

/// <summary>
/// The first step of everything that changes a company's members, roles or invitations: lock the company, and — for a
/// member — read their membership and role again under the lock. The company API checked the caller against the
/// database before it called the service, but another request may have demoted or removed them since; what they are
/// allowed to do is what the database says once nobody else can change the company.
/// </summary>
public sealed class CompanyGuard(AuthDbContext db, MembershipReader memberships)
{
    /// <summary>
    /// Locks the company (<see cref="CompanyLock"/>; the caller has opened the transaction). For a member, returns the
    /// actor as the database now has them, or <c>permissions_changed</c> when they are no longer a member of this company
    /// or no longer hold <paramref name="permission"/>. The operator needs no check.
    /// </summary>
    public async Task<Outcome<Actor>> EnterAsync(Actor actor, Guid companyId, string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(permission);

        if (!await CompanyLock.AcquireAsync(db, companyId, cancellationToken))
        {
            return Outcome.Fail<Actor>(TenancyErrors.NotFound);
        }

        if (actor.Member is null)
        {
            return Outcome.Ok(actor);
        }

        var current = await memberships.ReadAsync(actor.Member.UserId, cancellationToken);
        return current is null || current.CompanyId != companyId || !current.Holds(permission)
            ? Outcome.Fail<Actor>(TenancyErrors.PermissionsChanged)
            : Outcome.Ok(Actor.Of(current));
    }
}
```

`src/Auth.Server/Tenancy/CompanyPeople.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// The members of one company and what their roles hold, as they are inside the transaction that holds the company's
/// lock (spec 0005 → Safety rules, rule 2): enough to say how many managers the company has now, and how many it would
/// have after a change. A manager is a member whose role grants <c>members:manage</c>, directly or through <c>*</c>.
/// </summary>
public sealed class CompanyPeople
{
    private readonly List<(Guid UserId, Guid RoleId)> _members;
    private readonly Dictionary<Guid, string[]> _permissions;
    private readonly PermissionCatalog _catalog;

    private CompanyPeople(List<(Guid UserId, Guid RoleId)> members, Dictionary<Guid, string[]> permissions, PermissionCatalog catalog)
    {
        _members = members;
        _permissions = permissions;
        _catalog = catalog;
    }

    public static async Task<CompanyPeople> LoadAsync(AuthDbContext db, Guid companyId, PermissionCatalog catalog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(catalog);

        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.CompanyId == companyId)
            .Select(m => new { m.UserId, m.RoleId })
            .ToListAsync(cancellationToken);
        var roles = await db.CompanyRoles.AsNoTracking()
            .Where(r => r.CompanyId == companyId)
            .Select(r => new { r.Id, r.Permissions })
            .ToListAsync(cancellationToken);
        return new CompanyPeople([.. members.Select(m => (m.UserId, m.RoleId))], roles.ToDictionary(r => r.Id, r => r.Permissions), catalog);
    }

    /// <summary>Managers now.</summary>
    public int Managers() => Count(_ => null, _ => false);

    /// <summary>Managers if the member had the role <paramref name="roleId"/> instead.</summary>
    public int ManagersIfMemberHad(Guid userId, Guid roleId) =>
        Count(m => m.UserId == userId ? _permissions[roleId] : null, _ => false);

    /// <summary>Managers if the member were gone.</summary>
    public int ManagersWithout(Guid userId) => Count(_ => null, m => m.UserId == userId);

    /// <summary>Managers if the role held <paramref name="permissions"/> instead of what it holds.</summary>
    public int ManagersIfRoleHeld(Guid roleId, IEnumerable<string> permissions)
    {
        var replacement = permissions.ToArray();
        return Count(m => m.RoleId == roleId ? replacement : null, _ => false);
    }

    /// <summary>
    /// Safety rule 2: a change is refused when it leaves a company that has a manager without one. A company that has none
    /// already (the operator removed the last with <c>--force</c>) is not stopped from changing anything else.
    /// </summary>
    public static bool LeavesNone(int before, int after) => before > 0 && after == 0;

    private int Count(Func<(Guid UserId, Guid RoleId), string[]?> replacePermissions, Func<(Guid UserId, Guid RoleId), bool> gone) =>
        _members.Count(m => !gone(m)
            && _catalog.Expand(replacePermissions(m) ?? _permissions[m.RoleId]).Contains(PermissionCatalog.MembersManage));
}
```

`src/Auth.Server/Tenancy/Contracts.cs` — the change:

```diff
--- a/src/Auth.Server/Tenancy/Contracts.cs
+++ b/src/Auth.Server/Tenancy/Contracts.cs
@@ -31,3 +31,12 @@
 
 /// <summary>Request of <c>POST /auth/invites/accept</c>.</summary>
 public sealed record AcceptInviteRequest(string Token, string Password);
+
+/// <summary>One member of <c>GET /auth/org/members</c>. The time is ISO 8601 in UTC.</summary>
+public sealed record MemberItem(Guid UserId, string Email, RoleRef Role, DateTime JoinedAt);
+
+/// <summary>Response of <c>GET /auth/org/members</c>.</summary>
+public sealed record MembersResponse(IReadOnlyList<MemberItem> Members);
+
+/// <summary>Request of <c>PUT /auth/org/members/{user_id}/role</c>.</summary>
+public sealed record ChangeMemberRoleRequest(Guid RoleId);
```

`src/Auth.Server/Tenancy/InvitationService.cs` — the change:

```diff
--- a/src/Auth.Server/Tenancy/InvitationService.cs
+++ b/src/Auth.Server/Tenancy/InvitationService.cs
@@ -8,9 +8,9 @@
 /// <summary>
 /// Sends, lists, resends and cancels the invitations of a company (spec 0005 → Invitations). The company API and the
 /// operator commands both call it, so the rules are the same on both. Everything that changes anything runs in one
-/// transaction that begins by locking the company (see <see cref="CompanyLock"/>).
+/// transaction that begins by locking the company and reading the actor again (see <see cref="CompanyGuard"/>).
 /// </summary>
-public sealed class InvitationService(AuthDbContext db, ManifestHolder manifest, TimeProvider clock)
+public sealed class InvitationService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, TimeProvider clock)
 {
     /// <summary>
     /// Invites an address to the company with a role: makes the invitation, applies the mail limit of the company and
@@ -29,18 +29,20 @@
         var now = StorableTime.Now(clock);
         await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
 
-        if (!await CompanyLock.AcquireAsync(db, companyId, cancellationToken))
-        {
-            return Outcome.Fail(TenancyErrors.NotFound);
-        }
-
+        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.MembersManage, cancellationToken);
+        if (!entered.Succeeded)
+        {
+            return entered.Without;
+        }
+
+        var current = entered.Value!;
         var role = await db.CompanyRoles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId && r.CompanyId == companyId, cancellationToken);
         if (role is null)
         {
             return Outcome.Fail(TenancyErrors.NotFound);
         }
 
-        if (!actor.MayGrant(role.Permissions, manifest.Current.Catalog))
+        if (!current.MayGrant(role.Permissions, manifest.Current.Catalog))
         {
             return Outcome.Fail(TenancyErrors.PermissionNotHeld);
         }
@@ -62,7 +64,7 @@
 
         var inviteId = Guid.NewGuid();
         var expiresAt = now + InviteTokens.Lifetime;
-        var invitedBy = actor.UserId;
+        var invitedBy = current.UserId;
         var inserted = await db.Database.ExecuteSqlAsync(
             $"""
             INSERT INTO "Invites" ("Id", "CompanyId", "Email", "NormalizedEmail", "RoleId", "InvitedBy", "InvitedAt", "TokenHash", "ExpiresAt")
@@ -95,42 +97,79 @@
     }
 
     /// <summary>
-    /// Queues a new mail for a pending invitation, subject to the same mail limit as sending. When the mail is composed it
+    /// Queues a new mail for a pending invitation, subject to the same mail limit as sending, and to rule 1 (the caller
+    /// holds everything the invitation's role holds). When the mail is composed it
     /// carries a new token, and the earlier link stops working; the seven days start again.
     /// </summary>
-    public async Task<Outcome> ResendAsync(Guid companyId, Guid inviteId, CancellationToken cancellationToken)
-    {
+    public async Task<Outcome> ResendAsync(Actor actor, Guid companyId, Guid inviteId, CancellationToken cancellationToken)
+    {
+        ArgumentNullException.ThrowIfNull(actor);
+
         var now = StorableTime.Now(clock);
         await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
 
-        if (!await CompanyLock.AcquireAsync(db, companyId, cancellationToken))
-        {
-            return Outcome.Fail(TenancyErrors.NotFound);
+        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.MembersManage, cancellationToken);
+        if (!entered.Succeeded)
+        {
+            return entered.Without;
         }
 
         var invite = await db.Invites.AsNoTracking()
             .FirstOrDefaultAsync(i => i.Id == inviteId && i.CompanyId == companyId && i.ExpiresAt > now, cancellationToken);
-        return invite is null
-            ? Outcome.Fail(TenancyErrors.NotFound)
-            : await QueueMailAsync(companyId, invite.Id, invite.NormalizedEmail, now, transaction, cancellationToken);
+        if (invite is null)
+        {
+            return Outcome.Fail(TenancyErrors.NotFound);
+        }
+
+        // Rule 1: nobody acts on an invitation whose role holds more than they do.
+        if (!await MayActOnAsync(entered.Value!, invite, cancellationToken))
+        {
+            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
+        }
+
+        return await QueueMailAsync(companyId, invite.Id, invite.NormalizedEmail, now, transaction, cancellationToken);
     }
 
     /// <summary>Makes the link of a pending invitation stop working at once, and removes it from the list. Not subject to the mail limit.</summary>
-    public async Task<Outcome> CancelAsync(Guid companyId, Guid inviteId, CancellationToken cancellationToken)
-    {
+    public async Task<Outcome> CancelAsync(Actor actor, Guid companyId, Guid inviteId, CancellationToken cancellationToken)
+    {
+        ArgumentNullException.ThrowIfNull(actor);
+
         var now = StorableTime.Now(clock);
         await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
 
-        if (!await CompanyLock.AcquireAsync(db, companyId, cancellationToken))
+        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.MembersManage, cancellationToken);
+        if (!entered.Succeeded)
+        {
+            return entered.Without;
+        }
+
+        var invite = await db.Invites.AsNoTracking()
+            .FirstOrDefaultAsync(i => i.Id == inviteId && i.CompanyId == companyId && i.ExpiresAt > now, cancellationToken);
+        if (invite is null)
         {
             return Outcome.Fail(TenancyErrors.NotFound);
         }
 
-        var removed = await db.Invites
-            .Where(i => i.Id == inviteId && i.CompanyId == companyId && i.ExpiresAt > now)
-            .ExecuteDeleteAsync(cancellationToken);
+        // Rule 1: not by the mail limit, but a link of an invitation that holds more than the caller does is not theirs to kill.
+        if (!await MayActOnAsync(entered.Value!, invite, cancellationToken))
+        {
+            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
+        }
+
+        await db.Invites.Where(i => i.Id == inviteId).ExecuteDeleteAsync(cancellationToken);
         await transaction.CommitAsync(cancellationToken);
-        return removed == 0 ? Outcome.Fail(TenancyErrors.NotFound) : Outcome.Done;
+        return Outcome.Done;
+    }
+
+    /// <summary>Safety rule 1 for an existing invitation: the actor holds every permission of its role (and <c>*</c> only through <c>*</c>).</summary>
+    private async Task<bool> MayActOnAsync(Actor actor, Invite invite, CancellationToken cancellationToken)
+    {
+        var held = await db.CompanyRoles.AsNoTracking()
+            .Where(r => r.Id == invite.RoleId)
+            .Select(r => r.Permissions)
+            .SingleAsync(cancellationToken);
+        return actor.MayGrant(held, manifest.Current.Catalog);
     }
 
     /// <summary>Applies the mail limit of the company and address and, if it lets the mail through, queues it and commits.</summary>
```

`src/Auth.Server/Tenancy/MemberService.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Auth.Server.Tenancy;

/// <summary>
/// Lists the members of a company, changes their roles and removes them (spec 0005 → Company API, Safety rules, Effects).
/// The company API and the operator command both call it. Everything that changes anything runs in one transaction
/// that begins by locking the company, so that the last-manager check and the change cannot be interleaved with another.
/// </summary>
public sealed class MemberService(
    AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations)
{
    /// <summary>The company's members, sorted by address, ordinally.</summary>
    public async Task<IReadOnlyList<MemberItem>> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var rows = await (
            from membership in db.Memberships.AsNoTracking()
            join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
            join role in db.CompanyRoles.AsNoTracking() on membership.RoleId equals role.Id
            where membership.CompanyId == companyId
            select new { membership.UserId, user.Email, RoleId = role.Id, RoleName = role.Name, membership.JoinedAt })
            .ToListAsync(cancellationToken);

        return [.. rows
            .OrderBy(r => r.Email, StringComparer.Ordinal)
            .Select(r => new MemberItem(r.UserId, r.Email ?? "", new RoleRef(r.RoleId, r.RoleName), r.JoinedAt.UtcDateTime))];
    }

    /// <summary>
    /// Gives a member another role of the company. Refused: the member or the role is not in the company (<c>not_found</c>);
    /// the actor is the member (<c>cannot_change_self</c>, rule 3); the new role, or the member's current one, holds what the actor does not (rule 1); the change
    /// would leave the company without a manager (<c>last_manager</c>, rule 2). Ends no session: the member's next refresh
    /// carries the change.
    /// </summary>
    public async Task<Outcome> ChangeRoleAsync(Actor actor, Guid companyId, Guid userId, Guid roleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.MembersManage, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
        }

        var current = entered.Value!;
        var membership = await db.Memberships.FirstOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == userId, cancellationToken);
        var role = await db.CompanyRoles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId && r.CompanyId == companyId, cancellationToken);
        if (membership is null || role is null)
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        if (current.UserId == userId)
        {
            return Outcome.Fail(TenancyErrors.CannotChangeSelf);
        }

        // Rule 1, both ways: the role the member would get, and the role they have now.
        var catalog = manifest.Current.Catalog;
        if (!current.MayGrant(role.Permissions, catalog) || !await MayTouchAsync(current, membership, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
        }

        var people = await CompanyPeople.LoadAsync(db, companyId, catalog, cancellationToken);
        if (CompanyPeople.LeavesNone(people.Managers(), people.ManagersIfMemberHad(userId, roleId)))
        {
            return Outcome.Fail(TenancyErrors.LastManager);
        }

        membership.RoleId = roleId;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }

    /// <summary>
    /// Removes a member and ends every one of their sessions: every refresh token issued before is refused. The account stays,
    /// so the person can be invited again. Refused: not a member (<c>not_found</c>); the actor is the member
    /// (<c>cannot_change_self</c>); the member's role holds what the actor does not (<c>permission_not_held</c>, rule 1); the removal would leave the company without a manager (<c>last_manager</c>), unless
    /// <paramref name="force"/> — which only the operator has.
    /// </summary>
    public async Task<Outcome> RemoveAsync(Actor actor, Guid companyId, Guid userId, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.MembersManage, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
        }

        var current = entered.Value!;
        var membership = await db.Memberships.FirstOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == userId, cancellationToken);
        if (membership is null)
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        if (current.UserId == userId)
        {
            return Outcome.Fail(TenancyErrors.CannotChangeSelf);
        }

        // Rule 1 reaches the member's current role: nobody pushes out a member who holds more than they do.
        if (!await MayTouchAsync(current, membership, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
        }

        if (!force)
        {
            var people = await CompanyPeople.LoadAsync(db, companyId, manifest.Current.Catalog, cancellationToken);
            if (CompanyPeople.LeavesNone(people.Managers(), people.ManagersWithout(userId)))
            {
                return Outcome.Fail(TenancyErrors.LastManager);
            }
        }

        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(cancellationToken);

        // Every session ends: the refresh tokens, and the authorizations they hang on (spec 0002, Decision 11).
        var subject = userId.ToString();
        await tokens.RevokeBySubjectAsync(subject, cancellationToken);
        await authorizations.RevokeBySubjectAsync(subject, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }

    /// <summary>
    /// Safety rule 1, the other way round: whether the actor may remove this member or change their role. Not when the
    /// member's current role holds a permission the actor lacks, and a role with <c>*</c> only by an actor whose own role
    /// holds <c>*</c>. The operator may always.
    /// </summary>
    private async Task<bool> MayTouchAsync(Actor actor, Membership membership, CancellationToken cancellationToken)
    {
        var held = await db.CompanyRoles.AsNoTracking()
            .Where(r => r.Id == membership.RoleId)
            .Select(r => r.Permissions)
            .SingleAsync(cancellationToken);
        return actor.MayGrant(held, manifest.Current.Catalog);
    }
}
```

`src/Auth.Server/Tenancy/TenancyServices.cs` — the change:

```diff
--- a/src/Auth.Server/Tenancy/TenancyServices.cs
+++ b/src/Auth.Server/Tenancy/TenancyServices.cs
@@ -15,8 +15,10 @@
         services.AddSingleton<ManifestActivator>();
         services.AddScoped<CompanyService>();
         services.AddScoped<MembershipReader>();
+        services.AddScoped<CompanyGuard>();
         services.AddScoped<InvitationService>();
         services.AddScoped<InviteAcceptance>();
+        services.AddScoped<MemberService>();
         return services;
     }
 }
```

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS — 38 new tests, 697 in all. Run the concurrency
  tests of this task a few times (`dotnet test --filter-class "*OrgMemberTests"`): they must
  not flake.

- [ ] **Step 5: Commit** — `feat(tenancy): list members, change roles, remove members; the three safety rules`

### Task 12: Roles

**Files:**
- Create: `src/Auth.Server/Requests/RoleBody.cs`, `src/Auth.Server/Tenancy/RoleService.cs`,
  `src/Auth.Server/Api/OrgRoleEndpoints.cs`, `tests/Auth.IntegrationTests/OrgRoleTests.cs`
- Modify: `src/Auth.Server/Tenancy/Contracts.cs`, `src/Auth.Server/Tenancy/TenancyServices.cs`,
  `src/Auth.Server/Api/TenancyEndpoints.cs`

**Interfaces:**
- Consumes: Tasks 8 and 11 (`Actor`, `CompanyGuard`, `CompanyPeople`).
- Produces: `RoleBodyReader.ReadAsync(request, ct)` → `RoleBody(Name, Permissions)?`: a JSON
  object of at most 8 KiB with a non-blank string `name` and a `permissions` array of
  strings (at most 500); anything else is `null`, which the endpoints answer with
  `400 invalid_request`. Whether the name is acceptable and the permissions exist is the
  service's to say.
- Produces: `RoleService` (scoped):
  - `ListAsync(companyId, ct)` → `RolesResponse(roles, catalog)`: roles sorted by name,
    ordinally, each with the permissions it holds that are **still in the catalog** (`*`
    kept as it is, first), and `members`, the number who hold it; `catalog` is every
    permission a role may hold, `*` first.
  - `CreateAsync(actor, companyId, name, permissions, ct)` → `Outcome<RoleItem>`:
    `invalid_request` (the name rules), `unknown_permission` (not `*` and not in the
    catalog; judged before the database is touched), `permission_not_held` (rule 1, judged
    on the permissions the role ends up with), `role_name_taken` (compared
    case-insensitively). What is stored: `*` alone when it is among them (it already holds
    the rest), otherwise each permission once, sorted ordinally.
  - `UpdateAsync(actor, companyId, roleId, name, permissions, ct)`: replaces the name and
    the permissions as a whole, so what the role held that is not in the request — names
    that have left the catalog included — is gone. As `CreateAsync`, plus `not_found` and
    `last_manager` (rule 2: the new permissions would leave the company without a
    manager). Rule 1 looks at **both** the new permissions and the ones the role holds now:
    a caller who lacks one of them cannot edit the role at all, not even to shrink or rename
    it. Ends no session: its members' next refresh carries the change. An unknown
    permission is `unknown_permission` before a role that does not exist is `not_found`.
  - `DeleteAsync(actor, companyId, roleId, ct)`: `not_found`, `permission_not_held` (rule 1:
    the role holds a permission the actor lacks), or `role_in_use` while a member or a
    **pending** invitation holds the role; an expired invitation is not pending and goes
    with the role.
  All three changes enter through `CompanyGuard` with `roles:manage`.
- Produces: `GET /auth/org/roles` (open to `roles:manage` **or** `members:manage`, because
  inviting needs the list), `POST` (`201` with the role), `PUT /{id}` (`204`),
  `DELETE /{id}` (`204`); `RoleItem`, `RolesResponse`, `SaveRoleRequest`.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/OrgRoleTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class OrgRoleTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string RolesPath = "/auth/org/roles";

    private Task<HttpResponseMessage> CreateAsync(string token, string name, params string[] permissions) =>
        TenancyApi.Send(Client, HttpMethod.Post, RolesPath, token, new { name, permissions });

    private Task<HttpResponseMessage> ReplaceAsync(string token, Guid role, string name, params string[] permissions) =>
        TenancyApi.Send(Client, HttpMethod.Put, $"{RolesPath}/{role}", token, new { name, permissions });

    private Task<HttpResponseMessage> DeleteAsync(string token, Guid role) =>
        TenancyApi.Send(Client, HttpMethod.Delete, $"{RolesPath}/{role}", token);

    private Task<CompanyRole?> RoleAsync(Guid role) =>
        InDbAsync(db => db.CompanyRoles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == role, TestContext.Current.CancellationToken));

    private async Task<JsonElement> ListAsync(string token)
    {
        using var response = await TenancyApi.Get(Client, RolesPath, token);
        return await TenancyApi.ReadOkAsync(response);
    }

    private static string?[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString())];

    /// <summary>A company with a member whose role holds exactly these permissions, logged in.</summary>
    private async Task<(Guid Company, string Token)> CompanyWithCallerAsync(params string[] permissions)
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "caller", permissions);
        await AddMemberAsync(company, "caller@acme.test", "caller");
        return (company, (await SessionApi.LoginAsync(Client, "caller@acme.test", UserPassword)).AccessToken);
    }

    // ---- listing

    [Fact]
    public async Task List_shows_the_roles_sorted_by_name_with_their_permissions_member_counts_and_the_catalog()
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        await AddMemberAsync(company, "a@acme.test", "user");
        await AddMemberAsync(company, "b@acme.test", "user");
        await AddRoleAsync(company, "Auditor", "reports:read", "org:manage");

        var list = await ListAsync(token);

        Assert.Equal(["catalog", "roles"], list.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var roles = list.GetProperty("roles").EnumerateArray().ToList();
        Assert.Equal(["Auditor", "admin", "user"], roles.Select(r => r.GetProperty("name").GetString()));   // ordinal
        Assert.Equal(["id", "members", "name", "permissions"], roles[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(["org:manage", "reports:read"], Strings(roles[0].GetProperty("permissions")));
        Assert.Equal(["*"], Strings(roles[1].GetProperty("permissions")));   // star is shown as it is
        Assert.Equal([0, 1, 2], roles.Select(r => r.GetProperty("members").GetInt32()));
        Assert.Equal(
            ["*", "members:manage", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            Strings(list.GetProperty("catalog")));
    }

    [Fact]
    public async Task List_shows_only_permissions_that_are_still_in_the_catalog()   // criterion 21
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        await AddRoleAsync(company, "old", "reports:read", "gone:forever");

        var role = (await ListAsync(token)).GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("name").GetString() == "old");

        Assert.Equal(["reports:read"], Strings(role.GetProperty("permissions")));
        Assert.Equal(["gone:forever", "reports:read"], (await RoleAsync(role.GetProperty("id").GetGuid()))!.Permissions.Order(StringComparer.Ordinal));   // the database keeps the name
    }

    [Fact]
    public async Task List_is_open_to_members_manage_as_well_as_roles_manage_and_to_nobody_else()   // spec 0005 → Company API
    {
        var (_, rolesToken) = await CompanyWithCallerAsync("roles:manage");
        using (var roles = await TenancyApi.Get(Client, RolesPath, rolesToken))
        {
            Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
        }

        var company = await CreateCompanyAsync("Initech");
        await AddRoleAsync(company, "inviter", "members:manage");
        await AddRoleAsync(company, "nothing");
        await AddMemberAsync(company, "inviter@initech.test", "inviter");
        await AddMemberAsync(company, "nothing@initech.test", "nothing");

        using (var inviter = await TenancyApi.Get(Client, RolesPath, (await SessionApi.LoginAsync(Client, "inviter@initech.test", UserPassword)).AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, inviter.StatusCode);
        }

        using var nothing = await TenancyApi.Get(Client, RolesPath, (await SessionApi.LoginAsync(Client, "nothing@initech.test", UserPassword)).AccessToken);
        await TenancyApi.AssertErrorAsync(nothing, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task List_holds_no_role_of_another_company()   // criterion 14
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        var globex = await CreateCompanyAsync("Globex");
        await AddRoleAsync(globex, "secret", "reports:read");

        var names = (await ListAsync(token)).GetProperty("roles").EnumerateArray().Select(r => r.GetProperty("name").GetString());

        Assert.Equal(["admin", "user"], names);
    }

    [Fact]
    public async Task The_catalog_follows_the_manifest()   // criterion 21
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        Holder.Set(ManifestParser.Parse("permissions: [orders:read]\ndefault_roles:\n  admin: [\"*\"]\n").Manifest!, null);

        var list = await ListAsync(token);

        Assert.Equal(["*", "members:manage", "orders:read", "org:manage", "roles:manage"], Strings(list.GetProperty("catalog")));
    }

    // ---- creating

    [Fact]
    public async Task Admin_creates_a_role_from_permissions_of_the_catalog()   // criterion 18
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        using var response = await CreateAsync(token, "Auditor", "reports:read", "org:manage", "reports:read");

        var role = await TenancyApi.ReadCreatedAsync(response);
        Assert.Equal(["id", "members", "name", "permissions"], role.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Auditor", role.GetProperty("name").GetString());
        Assert.Equal(["org:manage", "reports:read"], Strings(role.GetProperty("permissions")));   // once each, sorted
        Assert.Equal(0, role.GetProperty("members").GetInt32());
        var stored = (await RoleAsync(role.GetProperty("id").GetGuid()))!;
        Assert.Equal(company, stored.CompanyId);
        Assert.Equal("AUDITOR", stored.NormalizedName);
    }

    [Fact]
    public async Task A_role_may_hold_nothing_and_star_stands_alone()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var empty = await CreateAsync(token, "Guest");
        using var star = await CreateAsync(token, "Root", "reports:read", "*");

        Assert.Empty(Strings((await TenancyApi.ReadCreatedAsync(empty)).GetProperty("permissions")));
        Assert.Equal(["*"], Strings((await TenancyApi.ReadCreatedAsync(star)).GetProperty("permissions")));
    }

    [Theory]
    [InlineData("reports:write")]      // not in the catalog
    [InlineData("REPORTS:READ")]       // case matters
    [InlineData("")]
    [InlineData("reports:read ")]
    public async Task A_permission_outside_the_catalog_is_unknown_permission(string permission)   // criterion 18
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var response = await CreateAsync(token, "Auditor", "reports:read", permission);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "unknown_permission");
        Assert.Equal(0, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.Name == "Auditor", TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Padded")]
    [InlineData("Padded ")]
    [InlineData("Ac\tme")]
    public async Task A_name_that_breaks_the_rules_is_a_400(string name)
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var response = await CreateAsync(token, name, "reports:read");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_name_of_100_characters_is_accepted_and_of_101_is_a_400()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using (var long101 = await CreateAsync(token, new string('a', 101)))
        {
            await TenancyApi.AssertErrorAsync(long101, HttpStatusCode.BadRequest, "invalid_request");
        }

        using var long100 = await CreateAsync(token, new string('a', 100));
        Assert.Equal(HttpStatusCode.Created, long100.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"name":"X"}""")]
    [InlineData("""{"permissions":[]}""")]
    [InlineData("""{"name":"X","permissions":"reports:read"}""")]
    [InlineData("""{"name":"X","permissions":null}""")]
    [InlineData("""{"name":"X","permissions":[1]}""")]
    [InlineData("""{"name":"X","permissions":[null]}""")]
    [InlineData("""{"name":"X","permissions":[["reports:read"]]}""")]
    [InlineData("""{"name":7,"permissions":[]}""")]
    [InlineData("""["X"]""")]
    [InlineData("not json")]
    public async Task A_malformed_body_is_a_400_invalid_request(string body)
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        using var request = TenancyApi.Request(HttpMethod.Post, RolesPath, token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_name_the_company_has_is_taken_whatever_its_case_and_free_in_another_company()   // criterion 18
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        var globex = await CreateCompanyAsync("Globex");
        await AddMemberAsync(globex, "boss@globex.test", "admin");

        foreach (var name in new[] { "admin", "ADMIN", "Admin" })
        {
            using var response = await CreateAsync(token, name, "reports:read");
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "role_name_taken");
        }

        var globexToken = (await SessionApi.LoginAsync(Client, "boss@globex.test", UserPassword)).AccessToken;
        using var elsewhere = await CreateAsync(globexToken, "Auditor");
        Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
        using var again = await CreateAsync(globexToken, "AUDITOR");
        await TenancyApi.AssertErrorAsync(again, HttpStatusCode.Conflict, "role_name_taken");
    }

    [Fact]
    public async Task Nobody_creates_a_role_that_holds_a_permission_they_lack()   // criterion 15
    {
        var (_, token) = await CompanyWithCallerAsync("roles:manage", "reports:read");

        using (var tooMuch = await CreateAsync(token, "Wide", "reports:read", "reports:approve"))
        {
            await TenancyApi.AssertErrorAsync(tooMuch, HttpStatusCode.Forbidden, "permission_not_held");
        }

        using (var star = await CreateAsync(token, "Root", "*"))
        {
            await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        }

        using var subset = await CreateAsync(token, "Narrow", "reports:read");
        Assert.Equal(HttpStatusCode.Created, subset.StatusCode);
        using var own = await CreateAsync(token, "Same", "roles:manage", "reports:read");
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
    }

    [Fact]
    public async Task A_caller_that_lists_every_permission_but_not_star_cannot_create_a_role_with_star()   // criterion 15
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "everything", [.. Holder.Current.Catalog.Permissions]);
        await AddMemberAsync(company, "all@acme.test", "everything");
        var token = (await SessionApi.LoginAsync(Client, "all@acme.test", UserPassword)).AccessToken;

        using var star = await CreateAsync(token, "Root", "*");
        await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        using var every = await CreateAsync(token, "Everything else", [.. Holder.Current.Catalog.Permissions]);
        Assert.Equal(HttpStatusCode.Created, every.StatusCode);
    }

    [Fact]
    public async Task Parallel_creations_of_one_name_leave_one_role()
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        var statuses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var response = await CreateAsync(token, "Auditor", "reports:read");
            return response.StatusCode;
        })));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(1, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == company && r.Name == "Auditor", TestContext.Current.CancellationToken)));
    }

    // ---- replacing

    [Fact]
    public async Task Replace_changes_the_name_and_the_permissions_as_a_whole()   // criterion 18
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var user = await RoleIdAsync(company, "user");

        using var response = await ReplaceAsync(token, user, "Staff", "templates:manage");

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        var role = (await RoleAsync(user))!;
        Assert.Equal("Staff", role.Name);
        Assert.Equal("STAFF", role.NormalizedName);
        Assert.Equal(["templates:manage"], role.Permissions);   // what it held before is gone
    }

    [Fact]
    public async Task A_role_can_be_renamed_to_another_spelling_of_its_own_name()
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var user = await RoleIdAsync(company, "user");

        using var response = await ReplaceAsync(token, user, "USER", "reports:read");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("USER", (await RoleAsync(user))!.Name);
    }

    [Fact]
    public async Task A_name_another_role_has_is_taken()   // criterion 18
    {
        var (company, _, token) = await CompanyWithAdminAsync();

        using var response = await ReplaceAsync(token, await RoleIdAsync(company, "user"), "ADMIN", "reports:read");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "role_name_taken");
    }

    [Fact]
    public async Task A_replace_with_a_permission_outside_the_catalog_is_unknown_permission()   // criterion 18
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var user = await RoleIdAsync(company, "user");

        using var response = await ReplaceAsync(token, user, "user", "reports:write");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "unknown_permission");
        Assert.Equal(["reports:approve", "reports:read"], (await RoleAsync(user))!.Permissions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_edit_drops_the_permissions_that_left_the_catalog()   // spec 0005 → Manifest
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var old = await AddRoleAsync(company, "old", "reports:read", "gone:forever");

        using var response = await ReplaceAsync(token, old, "old", "reports:read");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(["reports:read"], (await RoleAsync(old))!.Permissions);
    }

    [Fact]
    public async Task The_edit_of_a_role_reaches_its_members_at_the_next_refresh_and_ends_no_session()   // criterion 12
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        await AddMemberAsync(company, "worker@acme.test", "user");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        Assert.Equal(["reports:approve", "reports:read"], AccessTokens.Array(session.AccessToken, "permissions"));

        using (var edit = await ReplaceAsync(token, await RoleIdAsync(company, "user"), "user", "templates:manage"))
        {
            Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        }

        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);
        Assert.Equal(["templates:manage"], AccessTokens.Array(refreshed.AccessToken, "permissions"));
        _ = await SessionApi.RefreshOk(Client, refreshed.RefreshToken);
    }

    [Fact]
    public async Task A_role_of_another_company_or_that_does_not_exist_is_a_404()   // criterion 14
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        var globex = await CreateCompanyAsync("Globex");
        var foreign = await RoleIdAsync(globex, "user");

        foreach (var role in new[] { foreign, Guid.NewGuid() })
        {
            using var replace = await ReplaceAsync(token, role, "Mine", "reports:read");
            await TenancyApi.AssertErrorAsync(replace, HttpStatusCode.NotFound, "not_found");
            using var delete = await DeleteAsync(token, role);
            await TenancyApi.AssertErrorAsync(delete, HttpStatusCode.NotFound, "not_found");
        }

        var untouched = (await RoleAsync(foreign))!;
        Assert.Equal("user", untouched.Name);
        Assert.Equal(["reports:approve", "reports:read"], untouched.Permissions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Nobody_edits_a_role_so_that_it_holds_what_they_lack_and_nobody_without_star_touches_a_star_role()   // criterion 15
    {
        var (company, token) = await CompanyWithCallerAsync("roles:manage", "reports:read");
        var starter = await AddRoleAsync(company, "starter", "reports:read");

        using (var tooMuch = await ReplaceAsync(token, starter, "starter", "reports:read", "reports:approve"))
        {
            await TenancyApi.AssertErrorAsync(tooMuch, HttpStatusCode.Forbidden, "permission_not_held");
        }

        using (var star = await ReplaceAsync(token, starter, "starter", "*"))
        {
            await TenancyApi.AssertErrorAsync(star, HttpStatusCode.Forbidden, "permission_not_held");
        }

        // The admin role holds star: even a rename leaves it holding star, which this caller does not.
        using (var rename = await ReplaceAsync(token, await RoleIdAsync(company, "admin"), "Boss", "*"))
        {
            await TenancyApi.AssertErrorAsync(rename, HttpStatusCode.Forbidden, "permission_not_held");
        }

        using (var shrink = await ReplaceAsync(token, starter, "starter", "reports:read"))
        {
            Assert.Equal(HttpStatusCode.NoContent, shrink.StatusCode);   // as much as the caller holds is fine
        }

        Assert.Equal(["*"], (await RoleAsync(await RoleIdAsync(company, "admin")))!.Permissions);
    }

    [Fact]
    public async Task Nobody_edits_or_deletes_a_role_that_holds_more_than_they_do_even_to_shrink_it()   // criterion 15
    {
        var (company, token) = await CompanyWithCallerAsync("roles:manage", "reports:read");
        var user = await RoleIdAsync(company, "user");   // reports:approve and reports:read: one more than the caller holds
        var admin = await RoleIdAsync(company, "admin");   // star

        foreach (var role in new[] { user, admin })
        {
            using var shrink = await ReplaceAsync(token, role, "Renamed", "reports:read");
            await TenancyApi.AssertErrorAsync(shrink, HttpStatusCode.Forbidden, "permission_not_held");
            using var empty = await ReplaceAsync(token, role, "Renamed");
            await TenancyApi.AssertErrorAsync(empty, HttpStatusCode.Forbidden, "permission_not_held");
            using var delete = await DeleteAsync(token, role);
            await TenancyApi.AssertErrorAsync(delete, HttpStatusCode.Forbidden, "permission_not_held");
        }

        var untouched = (await RoleAsync(user))!;
        Assert.Equal("user", untouched.Name);
        Assert.Equal(["reports:approve", "reports:read"], untouched.Permissions.Order(StringComparer.Ordinal));
        Assert.NotNull(await RoleAsync(admin));
    }

    [Fact]
    public async Task A_caller_edits_and_deletes_roles_that_hold_what_they_hold_or_less()   // criterion 15
    {
        var (company, token) = await CompanyWithCallerAsync("roles:manage", "reports:read");
        var equal = await AddRoleAsync(company, "equal", "roles:manage", "reports:read");
        var fewer = await AddRoleAsync(company, "fewer", "reports:read");
        var nothing = await AddRoleAsync(company, "nothing");
        var gone = await AddRoleAsync(company, "gone", "reports:read", "left:the-catalog");   // what left the catalog grants nothing

        using (var edit = await ReplaceAsync(token, equal, "equal", "reports:read"))
        {
            Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        }

        using (var other = await ReplaceAsync(token, fewer, "fewer", "roles:manage"))
        {
            Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
        }

        using (var delete = await DeleteAsync(token, nothing))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        using (var clean = await DeleteAsync(token, gone))
        {
            Assert.Equal(HttpStatusCode.NoContent, clean.StatusCode);
        }

        Assert.Null(await RoleAsync(nothing));
        Assert.Equal(["roles:manage"], (await RoleAsync(fewer))!.Permissions);
    }

    [Fact]
    public async Task A_holder_of_star_edits_and_deletes_a_role_with_star_and_a_list_of_everything_is_not_star()   // criterion 15
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var root = await AddRoleAsync(company, "root", "*");
        var other = await AddRoleAsync(company, "other", "*");

        using (var rename = await ReplaceAsync(token, root, "Root", "*"))
        {
            Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        }

        using (var delete = await DeleteAsync(token, other))
        {
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        }

        var everything = await CreateCompanyAsync("Globex");
        await AddRoleAsync(everything, "caller", "members:manage", "roles:manage", "org:manage", "reports:read", "reports:approve", "templates:manage");
        await AddMemberAsync(everything, "caller@globex.test", "caller");
        var starRole = await AddRoleAsync(everything, "root", "*");
        var listed = (await SessionApi.LoginAsync(Client, "caller@globex.test", UserPassword)).AccessToken;

        using var edit = await ReplaceAsync(listed, starRole, "root", "reports:read");
        await TenancyApi.AssertErrorAsync(edit, HttpStatusCode.Forbidden, "permission_not_held");
        using var remove = await DeleteAsync(listed, starRole);
        await TenancyApi.AssertErrorAsync(remove, HttpStatusCode.Forbidden, "permission_not_held");
    }

    [Fact]
    public async Task An_unknown_permission_is_answered_before_a_role_that_does_not_exist()   // spec 0005 → the order of checks
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var response = await ReplaceAsync(token, Guid.NewGuid(), "Mine", "reports:write");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "unknown_permission");   // not 404
    }

    [Fact]
    public async Task Missing_permission_is_answered_before_a_bad_body_on_roles_too()   // spec 0005 → the order of checks
    {
        var company = await CreateCompanyAsync("Acme");
        await AddMemberAsync(company, "worker@acme.test", "user");
        var token = (await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword)).AccessToken;

        using var response = await CreateAsync(token, "", "reports:write");   // an invalid name and an unknown permission

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");   // not 400
    }

    [Fact]
    public async Task An_edit_that_would_leave_the_company_without_a_manager_is_refused()   // criterion 16
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var adminRole = await RoleIdAsync(company, "admin");

        // The only manager is the caller themselves, through star: dropping star would leave nobody who manages members.
        using (var drop = await ReplaceAsync(token, adminRole, "admin", "roles:manage", "org:manage"))
        {
            await TenancyApi.AssertErrorAsync(drop, HttpStatusCode.Conflict, "last_manager");
        }

        Assert.Equal(["*"], (await RoleAsync(adminRole))!.Permissions);
        Assert.NotNull(await InDbAsync(db => db.Memberships.SingleOrDefaultAsync(m => m.UserId == admin, TestContext.Current.CancellationToken)));

        // Keeping members:manage is fine.
        using var keep = await ReplaceAsync(token, adminRole, "admin", "members:manage", "roles:manage");
        Assert.Equal(HttpStatusCode.NoContent, keep.StatusCode);
    }

    [Fact]
    public async Task An_edit_that_takes_members_manage_from_a_role_is_fine_while_another_manager_is_left()   // criterion 16
    {
        var company = await CreateCompanyAsync("Acme");
        var shared = await AddRoleAsync(company, "shared", "members:manage", "roles:manage");
        await AddMemberAsync(company, "a@acme.test", "shared");
        await AddMemberAsync(company, "b@acme.test", "admin");
        var token = (await SessionApi.LoginAsync(Client, "a@acme.test", UserPassword)).AccessToken;

        using var response = await ReplaceAsync(token, shared, "shared", "roles:manage");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);   // b still manages members, through star
    }

    // ---- deleting

    [Fact]
    public async Task A_role_nobody_holds_is_deleted()   // criterion 18
    {
        var (_, _, token) = await CompanyWithAdminAsync();
        using var created = await CreateAsync(token, "Temporary", "reports:read");
        var id = (await TenancyApi.ReadCreatedAsync(created)).GetProperty("id").GetGuid();

        using var response = await DeleteAsync(token, id);

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Null(await RoleAsync(id));
    }

    [Fact]
    public async Task A_role_held_by_a_member_cannot_be_deleted()   // criterion 18
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var user = await RoleIdAsync(company, "user");
        await AddMemberAsync(company, "worker@acme.test", "user");

        using var response = await DeleteAsync(token, user);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "role_in_use");
        Assert.NotNull(await RoleAsync(user));
    }

    [Fact]
    public async Task A_role_of_a_pending_invitation_cannot_be_deleted_and_of_an_expired_one_can()   // criterion 18
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        var role = await AddRoleAsync(company, "Temporary", "reports:read");
        var invite = await AddInviteAsync(company, "worker@acme.test", "Temporary");
        var token = (await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword)).AccessToken;

        using (var pending = await DeleteAsync(token, role))
        {
            await TenancyApi.AssertErrorAsync(pending, HttpStatusCode.Conflict, "role_in_use");
        }

        Clock.Advance(InviteTokens.Lifetime);
        var fresh = (await SessionApi.LoginAsync(Client, "boss@acme.test", UserPassword)).AccessToken;
        using var expired = await DeleteAsync(fresh, role);

        Assert.Equal(HttpStatusCode.NoContent, expired.StatusCode);
        Assert.Null(await InviteAsync(invite));   // it went with the role
    }

    // ---- who may

    [Theory]
    [InlineData("GET", "")]
    [InlineData("POST", "")]
    [InlineData("PUT", "/11111111-1111-1111-1111-111111111111")]
    [InlineData("DELETE", "/11111111-1111-1111-1111-111111111111")]
    public async Task Every_endpoint_needs_a_token_and_the_permission_checked_against_the_database(string method, string suffix)   // criterion 14
    {
        var company = await CreateCompanyAsync("Acme");
        var worker = await AddMemberAsync(company, "worker@acme.test", "admin");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);
        var path = RolesPath + suffix;
        object? body = method is "POST" or "PUT" ? new { name = "X", permissions = Array.Empty<string>() } : null;

        using (var anonymous = await TenancyApi.Send(Client, new HttpMethod(method), path, null, body))
        {
            await TenancyApi.AssertUnauthorizedAsync(anonymous);
        }

        await SetMemberRoleAsync(worker, await RoleIdAsync(company, "user"));
        using (var stale = await TenancyApi.Send(Client, new HttpMethod(method), path, session.AccessToken, body))
        {
            await TenancyApi.AssertErrorAsync(stale, HttpStatusCode.Forbidden, "permissions_changed");
        }

        var fresh = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using var plain = await TenancyApi.Send(Client, new HttpMethod(method), path, fresh.AccessToken, body);
        await TenancyApi.AssertErrorAsync(plain, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task A_member_with_members_manage_but_not_roles_manage_cannot_change_roles()
    {
        var company = await CreateCompanyAsync("Acme");
        await AddRoleAsync(company, "inviter", "members:manage");
        await AddMemberAsync(company, "inviter@acme.test", "inviter");
        var token = (await SessionApi.LoginAsync(Client, "inviter@acme.test", UserPassword)).AccessToken;

        using var response = await CreateAsync(token, "Mine");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task A_path_that_is_not_a_uuid_is_a_400()
    {
        var (_, _, token) = await CompanyWithAdminAsync();

        using var replace = await TenancyApi.Send(Client, HttpMethod.Put, $"{RolesPath}/not-a-uuid", token, new { name = "X", permissions = Array.Empty<string>() });
        using var delete = await TenancyApi.Send(Client, HttpMethod.Delete, $"{RolesPath}/not-a-uuid", token);

        await TenancyApi.AssertErrorAsync(replace, HttpStatusCode.BadRequest, "invalid_request");
        await TenancyApi.AssertErrorAsync(delete, HttpStatusCode.BadRequest, "invalid_request");
    }
}
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`RoleService` does not exist; the tests reach for endpoints
  that are not mapped).

- [ ] **Step 3: Implement.**

`src/Auth.Server/Api/OrgRoleEndpoints.cs`:

```csharp
using Auth.Server.Requests;
using Auth.Server.Tenancy;

namespace Auth.Server.Api;

/// <summary>
/// The roles of the caller's company (spec 0005 → Company API): list, create, replace, delete. Listing is open to
/// <c>roles:manage</c> and to <c>members:manage</c>, because inviting needs the list; the rest need <c>roles:manage</c>.
/// All are checked against the database.
/// </summary>
public static class OrgRoleEndpoints
{
    public static async Task<IResult> ListAsync(HttpContext http, MembershipReader memberships, RoleService roles)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(roles);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.RolesManage, PermissionCatalog.MembersManage);
        return access.Caller is { } caller
            ? ApiResults.Ok(await roles.ListAsync(caller.CompanyId, http.RequestAborted))
            : access.Failure!;
    }

    public static async Task<IResult> CreateAsync(HttpContext http, MembershipReader memberships, RoleService roles)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(roles);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.RolesManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        if (await RoleBodyReader.ReadAsync(http.Request, http.RequestAborted) is not { } body)
        {
            return ApiResults.InvalidRequest();
        }

        var created = await roles.CreateAsync(Actor.Of(caller), caller.CompanyId, body.Name, body.Permissions, http.RequestAborted);
        return created.Succeeded ? ApiResults.Created(created.Value!) : ApiResults.Refused(created.Without);
    }

    public static async Task<IResult> ReplaceAsync(string id, HttpContext http, MembershipReader memberships, RoleService roles)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(roles);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.RolesManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        if (!IdInput.TryParse(id, out var roleId) || await RoleBodyReader.ReadAsync(http.Request, http.RequestAborted) is not { } body)
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await roles.UpdateAsync(Actor.Of(caller), caller.CompanyId, roleId, body.Name, body.Permissions, http.RequestAborted);
        return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
    }

    public static async Task<IResult> DeleteAsync(string id, HttpContext http, MembershipReader memberships, RoleService roles)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(roles);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.RolesManage);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        if (!IdInput.TryParse(id, out var roleId))
        {
            return ApiResults.InvalidRequest();
        }

        var outcome = await roles.DeleteAsync(Actor.Of(caller), caller.CompanyId, roleId, http.RequestAborted);
        return outcome.Succeeded ? ApiResults.NoContent() : ApiResults.Refused(outcome);
    }
}
```

`src/Auth.Server/Api/TenancyEndpoints.cs` — the change:

```diff
--- a/src/Auth.Server/Api/TenancyEndpoints.cs
+++ b/src/Auth.Server/Api/TenancyEndpoints.cs
@@ -83,6 +83,31 @@
             .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.CannotChangeSelf, TenancyErrors.LastManager)
             .ProducesGuarded();
 
+        company.MapGet("roles", OrgRoleEndpoints.ListAsync)
+            .Produces<RolesResponse>()
+            .ProducesGuarded();
+        company.MapPost("roles", OrgRoleEndpoints.CreateAsync)
+            .ReadsJson<SaveRoleRequest>()
+            .Produces<RoleItem>(StatusCodes.Status201Created)
+            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.UnknownPermission)
+            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
+            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.RoleNameTaken)
+            .ProducesGuarded();
+        company.MapPut("roles/{id}", OrgRoleEndpoints.ReplaceAsync)
+            .ReadsJson<SaveRoleRequest>()
+            .Produces(StatusCodes.Status204NoContent)
+            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.UnknownPermission)
+            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
+            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
+            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.RoleNameTaken, TenancyErrors.LastManager)
+            .ProducesGuarded();
+        company.MapDelete("roles/{id}", OrgRoleEndpoints.DeleteAsync)
+            .Produces(StatusCodes.Status204NoContent)
+            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
+            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
+            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.RoleInUse)
+            .ProducesGuarded();
+
         return app;
     }
 }
```

`src/Auth.Server/Requests/RoleBody.cs`:

```csharp
using System.Text.Json;

namespace Auth.Server.Requests;

/// <summary>What a role is created or replaced with: a name and a list of permissions.</summary>
public sealed record RoleBody(string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// Reads the body of <c>POST /auth/org/roles</c> and <c>PUT /auth/org/roles/{id}</c> (spec 0005 → General rules): a JSON
/// object of at most <see cref="JsonObjectBody.MaxBytes"/> bytes with a <c>name</c> that is a non-blank string and a
/// <c>permissions</c> that is an array of strings. Whether the name is acceptable and the permissions exist is for the
/// caller to say; this says only whether the body has that shape.
/// </summary>
public static class RoleBodyReader
{
    /// <summary>More than this many entries is not a list of permissions; the 8 KiB body would hold about as many.</summary>
    public const int MaxPermissions = 500;

    public static async Task<RoleBody?> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!JsonObjectBody.IsJson(request.ContentType) || request.ContentLength > JsonObjectBody.MaxBytes)
        {
            return null;
        }

        var body = await JsonObjectBody.ReadBoundedAsync(request.Body, JsonObjectBody.MaxBytes, cancellationToken);
        if (body is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !JsonObjectBody.TryGetRequiredString(root, "name", out var name)
                || !root.TryGetProperty("permissions", out var list)
                || list.ValueKind != JsonValueKind.Array
                || list.GetArrayLength() > MaxPermissions)
            {
                return null;
            }

            var permissions = new List<string>();
            foreach (var element in list.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                permissions.Add(element.GetString() ?? "");
            }

            return new RoleBody(name, permissions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: GetString() on a string holding an unpaired surrogate escape (e.g. "\ud800").
            return null;
        }
    }
}
```

`src/Auth.Server/Tenancy/Contracts.cs` — the change:

```diff
--- a/src/Auth.Server/Tenancy/Contracts.cs
+++ b/src/Auth.Server/Tenancy/Contracts.cs
@@ -40,3 +40,15 @@
 
 /// <summary>Request of <c>PUT /auth/org/members/{user_id}/role</c>.</summary>
 public sealed record ChangeMemberRoleRequest(Guid RoleId);
+
+/// <summary>
+/// A role of <c>GET /auth/org/roles</c>, and the answer of <c>POST /auth/org/roles</c>: the permissions it holds that are
+/// still in the catalog (<c>*</c> is shown as it is), and how many members hold it.
+/// </summary>
+public sealed record RoleItem(Guid Id, string Name, string[] Permissions, int Members);
+
+/// <summary>Response of <c>GET /auth/org/roles</c>: the roles, and every permission a role may hold, <c>*</c> first.</summary>
+public sealed record RolesResponse(IReadOnlyList<RoleItem> Roles, IReadOnlyList<string> Catalog);
+
+/// <summary>Request of <c>POST /auth/org/roles</c> and <c>PUT /auth/org/roles/{id}</c>.</summary>
+public sealed record SaveRoleRequest(string Name, string[] Permissions);
```

`src/Auth.Server/Tenancy/RoleService.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Requests;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// Lists, creates, replaces and deletes the roles of a company (spec 0005 → Company API, Safety rules). A role is a name and
/// a set of permissions from the catalog, or <c>*</c>. Everything that changes anything runs in one transaction that
/// begins by locking the company, so that the last-manager check and the change cannot be interleaved with another.
/// </summary>
public sealed class RoleService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, TimeProvider clock)
{
    /// <summary>
    /// The company's roles sorted by name, ordinally, each with the permissions it holds that are still in the catalog and
    /// the number of members who hold it, and the catalog: everything a role may hold, <c>*</c> first.
    /// </summary>
    public async Task<RolesResponse> ListAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var catalog = manifest.Current.Catalog;
        var roles = await db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == companyId).ToListAsync(cancellationToken);
        var counts = await db.Memberships.AsNoTracking()
            .Where(m => m.CompanyId == companyId)
            .GroupBy(m => m.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.RoleId, g => g.Count, cancellationToken);

        return new RolesResponse(
            [.. roles
                .OrderBy(r => r.Name, StringComparer.Ordinal)
                .Select(r => new RoleItem(r.Id, r.Name, catalog.Visible(r.Permissions), counts.GetValueOrDefault(r.Id)))],
            catalog.Listed);
    }

    /// <summary>
    /// Creates a role. Refused: a name that breaks the rules (<c>invalid_request</c>); a permission outside the catalog
    /// (<c>unknown_permission</c>); a role that holds what the actor does not (<c>permission_not_held</c>, rule 1); a name
    /// the company has already, whatever its case (<c>role_name_taken</c>).
    /// </summary>
    public async Task<Outcome<RoleItem>> CreateAsync(
        Actor actor, Guid companyId, string name, IReadOnlyList<string> permissions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Validate(name, permissions) is { } invalid)
        {
            return Outcome.Fail<RoleItem>(invalid);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.RolesManage, cancellationToken);
        if (!entered.Succeeded)
        {
            return Outcome.Fail<RoleItem>(entered.Error!);
        }

        var catalog = manifest.Current.Catalog;
        var held = Normalize(permissions);
        if (!entered.Value!.MayGrant(held, catalog))
        {
            return Outcome.Fail<RoleItem>(TenancyErrors.PermissionNotHeld);
        }

        var normalizedName = NameInput.Normalize(name);
        if (await db.CompanyRoles.AnyAsync(r => r.CompanyId == companyId && r.NormalizedName == normalizedName, cancellationToken))
        {
            return Outcome.Fail<RoleItem>(TenancyErrors.RoleNameTaken);
        }

        var role = new CompanyRole { CompanyId = companyId, Name = name, NormalizedName = normalizedName, Permissions = held };
        db.CompanyRoles.Add(role);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Ok(new RoleItem(role.Id, role.Name, held, 0));
    }

    /// <summary>
    /// Replaces a role's name and permissions as a whole; what it held that is not in the request is gone, names that have
    /// left the catalog included. Refused like <see cref="CreateAsync"/> — rule 1 also looks at what the role holds now —
    /// and with <c>not_found</c> for a role that is not in the company and <c>last_manager</c> (rule 2) when the new permissions would leave the company without a manager.
    /// Ends no session: its members' next refresh carries the change.
    /// </summary>
    public async Task<Outcome> UpdateAsync(
        Actor actor, Guid companyId, Guid roleId, string name, IReadOnlyList<string> permissions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Validate(name, permissions) is { } invalid)
        {
            return Outcome.Fail(invalid);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.RolesManage, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
        }

        var role = await db.CompanyRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.CompanyId == companyId, cancellationToken);
        if (role is null)
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        // Rule 1, both ways: what the role would hold, and what it holds now.
        var catalog = manifest.Current.Catalog;
        var held = Normalize(permissions);
        if (!entered.Value!.MayGrant(held, catalog) || !entered.Value.MayGrant(role.Permissions, catalog))
        {
            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
        }

        var normalizedName = NameInput.Normalize(name);
        if (await db.CompanyRoles.AnyAsync(r => r.CompanyId == companyId && r.Id != roleId && r.NormalizedName == normalizedName, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.RoleNameTaken);
        }

        var people = await CompanyPeople.LoadAsync(db, companyId, catalog, cancellationToken);
        if (CompanyPeople.LeavesNone(people.Managers(), people.ManagersIfRoleHeld(roleId, held)))
        {
            return Outcome.Fail(TenancyErrors.LastManager);
        }

        role.Name = name;
        role.NormalizedName = normalizedName;
        role.Permissions = held;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }

    /// <summary>
    /// Deletes a role nobody holds. Refused with <c>permission_not_held</c> when the role holds a permission the actor
    /// lacks (rule 1), and with <c>role_in_use</c> while a member or a pending invitation has it; an expired
    /// invitation is not pending, and goes with the role.
    /// </summary>
    public async Task<Outcome> DeleteAsync(Actor actor, Guid companyId, Guid roleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var now = StorableTime.Now(clock);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.RolesManage, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
        }

        var role = await db.CompanyRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.CompanyId == companyId, cancellationToken);
        if (role is null)
        {
            return Outcome.Fail(TenancyErrors.NotFound);
        }

        // Rule 1: nobody deletes a role that holds more than they do.
        if (!entered.Value!.MayGrant(role.Permissions, manifest.Current.Catalog))
        {
            return Outcome.Fail(TenancyErrors.PermissionNotHeld);
        }

        await db.Invites.Where(i => i.RoleId == roleId && i.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);
        if (await db.Memberships.AnyAsync(m => m.RoleId == roleId, cancellationToken)
            || await db.Invites.AnyAsync(i => i.RoleId == roleId, cancellationToken))
        {
            return Outcome.Fail(TenancyErrors.RoleInUse);
        }

        db.CompanyRoles.Remove(role);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
    }

    /// <summary>The refusal a name or a permission list deserves before the database is asked anything; <see langword="null"/> when there is none.</summary>
    private string? Validate(string name, IReadOnlyList<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(permissions);

        if (!NameInput.IsValid(name))
        {
            return TenancyErrors.InvalidRequest;
        }

        var catalog = manifest.Current.Catalog;
        return permissions.All(catalog.Accepts) ? null : TenancyErrors.UnknownPermission;
    }

    /// <summary>
    /// What is stored: <c>*</c> alone when it is there, since it already holds the rest; otherwise the permissions once each,
    /// sorted ordinally.
    /// </summary>
    private static string[] Normalize(IReadOnlyList<string> permissions) =>
        permissions.Contains(PermissionCatalog.All, StringComparer.Ordinal)
            ? [PermissionCatalog.All]
            : [.. permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
```

`src/Auth.Server/Tenancy/TenancyServices.cs` — the change:

```diff
--- a/src/Auth.Server/Tenancy/TenancyServices.cs
+++ b/src/Auth.Server/Tenancy/TenancyServices.cs
@@ -19,6 +19,7 @@
         services.AddScoped<InvitationService>();
         services.AddScoped<InviteAcceptance>();
         services.AddScoped<MemberService>();
+        services.AddScoped<RoleService>();
         return services;
     }
 }
```

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`,
  then `dotnet format --verify-no-changes`. Expected: PASS — 56 new tests, 753 in all.
  This is the end of Day 4.

- [ ] **Step 5: Commit** — `feat(tenancy): the roles API`

---

## Day 5 — the operator CLI, the OpenAPI description, e2e, docs

The last two features, then the real-network e2e, the three verifiers and the
documents. The e2e and the verifiers need the whole day's attention, so the two tasks
before them are small.

### Task 13: The operator CLI

**Files:**
- Create: `src/Auth.Server/Admin/AdminArguments.cs`, `src/Auth.Server/Admin/AdminCli.cs`,
  `src/Auth.Server/Tenancy/OperatorCommands.cs`,
  `tests/Auth.IntegrationTests/AdminArgumentsTests.cs`,
  `tests/Auth.IntegrationTests/AdminCliTests.cs`
- Modify: `src/Auth.Server/Program.cs`, `src/Auth.Server/Tenancy/TenancyServices.cs`,
  `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`

**Interfaces:**
- Consumes: Tasks 4, 8 and 11 (`CompanyService`, `InvitationService`, `MemberService`,
  `Actor.Operator`).
- Produces: `AdminArguments.Parse(args)` → `AdminParseResult(AdminCommand? Command, string? Error)`
  for the four commands, by hand (owner decision: no `System.CommandLine`): `--name value`
  or `--name=value`, in any order, and the flag `--force` on `remove-member`. Errors are
  `missing_command`, `unknown_command`, `unknown_option`, `missing_option`, `missing_value`,
  `duplicate_option`, `unexpected_argument`. `AdminArguments.Usage`.
- Produces: `OperatorCommands` (scoped): `CreateOrgAsync(name)`, `ListOrgsAsync()`
  (companies by name, ordinal, with their member counts), `InviteAsync(org, email, role)`
  (the role is named, not identified, and found without regard to case) and
  `RemoveMemberAsync(org, email, force)`. They take strings as typed, and call the same
  services as the API with `Actor.Operator`: exempt from rule 1, held to rules 2 and 3,
  except that `--force` lifts rule 2 (and rule 3 cannot apply: the operator is not a member).
- Produces: `AdminCli.RunAsync(args, output, error, configure?, ct)` → exit code `0` (done),
  `1` (refused: `error: <code>` on the error stream; `error: too_many_attempts (retry in N
  seconds)` for the mail limit), `2` (the arguments were not understood: `error: <code>` and
  the usage), `3` (could not be carried out: `error: failed (<exception type name>)`, plus
  our own message when it is a missing or invalid setting, which names the key and never a
  value). It builds the services of the host with `WebApplication.CreateBuilder([])` — the
  empty argument list keeps `--force` out of the configuration —
  the persistence and the manifest, **never runs the host** (no listener, no background
  service, no key material), **never migrates**, activates the manifest like the host does,
  and writes its logs to the error stream at `Warning` and above, so that standard output is
  the command's output and nothing else: `create-org` prints the company id and only that.
- Changes: `Program.cs` starts with `if (args is ["admin", .. var adminArguments])` and ends
  with `return 0;`. The image is unchanged: `docker run <image> admin …` and
  `docker compose run … auth admin …` hand the arguments to its entrypoint.
- A CLI invitation is only **queued**: the server's dispatcher mails it at its next pass —
  at most one poll interval (`MailDelivery.PollInterval`, a minute) later — and only
  while the server runs; the command says so.
- Produces (tests): `AuthAppFactory.ConnectionString`.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/AdminArgumentsTests.cs`:

```csharp
using Auth.Server.Admin;

namespace Auth.IntegrationTests;

public sealed class AdminArgumentsTests
{
    private static AdminCommand Command(params string[] args)
    {
        var result = AdminArguments.Parse(args);

        Assert.Null(result.Error);
        return Assert.IsAssignableFrom<AdminCommand>(result.Command);
    }

    private static string Error(params string[] args)
    {
        var result = AdminArguments.Parse(args);

        Assert.Null(result.Command);
        return Assert.IsType<string>(result.Error);
    }

    [Fact]
    public void Create_org_takes_a_name()   // criterion 1
    {
        Assert.Equal(new CreateOrgCommand("Acme"), Command("create-org", "--name", "Acme"));
        Assert.Equal(new CreateOrgCommand("Acme sp. z o.o."), Command("create-org", "--name=Acme sp. z o.o."));
    }

    [Fact]
    public void Invite_takes_an_org_an_email_and_a_role_in_any_order()
    {
        var expected = new InviteCommand("11111111-1111-1111-1111-111111111111", "boss@acme.test", "admin");

        Assert.Equal(expected, Command("invite", "--org", "11111111-1111-1111-1111-111111111111", "--email", "boss@acme.test", "--role", "admin"));
        Assert.Equal(expected, Command("invite", "--role=admin", "--email=boss@acme.test", "--org=11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void List_orgs_takes_nothing()
    {
        Assert.Equal(new ListOrgsCommand(), Command("list-orgs"));
    }

    [Fact]
    public void Remove_member_takes_an_org_an_email_and_optionally_force()   // criterion 22
    {
        Assert.Equal(
            new RemoveMemberCommand("11111111-1111-1111-1111-111111111111", "a@acme.test", false),
            Command("remove-member", "--org", "11111111-1111-1111-1111-111111111111", "--email", "a@acme.test"));
        Assert.Equal(
            new RemoveMemberCommand("11111111-1111-1111-1111-111111111111", "a@acme.test", true),
            Command("remove-member", "--force", "--org", "11111111-1111-1111-1111-111111111111", "--email=a@acme.test"));
    }

    [Fact]
    public void A_value_may_hold_an_equals_sign_and_be_empty()
    {
        Assert.Equal(new CreateOrgCommand("a=b"), Command("create-org", "--name=a=b"));
        Assert.Equal(new CreateOrgCommand(""), Command("create-org", "--name="));   // refused later, as a name
    }

    [Fact]
    public void No_command_and_an_unknown_command_are_errors()
    {
        Assert.Equal("missing_command", Error());
        Assert.Equal("unknown_command", Error("delete-org"));
        Assert.Equal("unknown_command", Error("--name", "Acme"));
        Assert.Equal("unknown_command", Error("Create-Org", "--name", "Acme"));
    }

    [Fact]
    public void An_option_the_command_does_not_have_is_an_error()
    {
        Assert.Equal("unknown_option", Error("create-org", "--name", "Acme", "--force"));
        Assert.Equal("unknown_option", Error("invite", "--org", "x", "--email", "y", "--role", "z", "--force"));
        Assert.Equal("unknown_option", Error("list-orgs", "--all"));
        Assert.Equal("unknown_option", Error("remove-member", "--org", "x", "--email", "y", "--role", "z"));
    }

    [Fact]
    public void A_missing_option_is_an_error()
    {
        Assert.Equal("missing_option", Error("create-org"));
        Assert.Equal("missing_option", Error("invite", "--org", "x", "--email", "y"));
        Assert.Equal("missing_option", Error("remove-member", "--force", "--org", "x"));
    }

    [Fact]
    public void An_option_without_a_value_is_an_error()
    {
        Assert.Equal("missing_value", Error("create-org", "--name"));
        Assert.Equal("missing_value", Error("invite", "--org", "--email", "y", "--role", "z"));   // the next word is an option, not a value
    }

    [Fact]
    public void A_repeated_option_is_an_error()
    {
        Assert.Equal("duplicate_option", Error("create-org", "--name", "A", "--name", "B"));
        Assert.Equal("duplicate_option", Error("remove-member", "--org", "x", "--email", "y", "--force", "--force"));
    }

    [Fact]
    public void A_flag_takes_no_value_and_a_word_without_dashes_is_not_expected()
    {
        Assert.Equal("unknown_option", Error("remove-member", "--org", "x", "--email", "y", "--force=yes"));
        Assert.Equal("unexpected_argument", Error("create-org", "Acme"));
        Assert.Equal("unexpected_argument", Error("list-orgs", "now"));
        Assert.Equal("unexpected_argument", Error("create-org", "--name", "A", "B"));
    }
}
```

`tests/Auth.IntegrationTests/AdminCliTests.cs`:

```csharp
using System.Globalization;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Auth.IntegrationTests;

public sealed class AdminCliTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Boss = "boss@acme.test";

    private sealed record Run(int Exit, string Out, string Error);

    /// <summary>Runs the command the way the container does: the same binary, the configuration of the service.</summary>
    private Task<Run> RunAsync(params string[] args) => RunAgainstAsync(Factory.ConnectionString, Factory.ManifestPath, args);

    private static async Task<Run> RunAgainstAsync(string? connectionString, string manifestPath, string[] args, ILoggerProvider? logs = null)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = await AdminCli.RunAsync(
            args,
            output,
            error,
            builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Auth"] = connectionString,
                    [ManifestSettings.PathKey] = manifestPath,
                });
                if (logs is not null)
                {
                    builder.Logging.AddProvider(logs);
                }
            },
            TestContext.Current.CancellationToken);
        return new Run(exit, output.ToString(), error.ToString());
    }

    /// <summary>Starts the host, which migrates and seeds the database, and then runs the command against it.</summary>
    private async Task<Run> CliAsync(params string[] args)
    {
        _ = Factory.Services;
        return await RunAsync(args);
    }

    private async Task<Guid> CreateOrgAsync(string name)
    {
        var run = await CliAsync("create-org", "--name", name);
        Assert.True(run.Exit == 0, run.Error);
        return Guid.Parse(run.Out.Trim());
    }

    private Task<List<Invite>> InvitesAsync() =>
        InDbAsync(db => db.Invites.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    // ---- create-org and list-orgs

    [Fact]
    public async Task Create_org_prints_the_id_and_makes_a_company_with_a_copy_of_the_default_roles()   // criterion 1
    {
        var run = await CliAsync("create-org", "--name", "Acme");

        Assert.Equal(0, run.Exit);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\n$", run.Out.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal("", run.Error);   // nothing but the id on the output, and no log lines on the error stream
        var id = Guid.Parse(run.Out.Trim());
        var roles = await InDbAsync(db => db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == id).OrderBy(r => r.Name).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([("admin", "*"), ("user", "reports:read,reports:approve")], roles.Select(r => (r.Name, string.Join(',', r.Permissions))));
    }

    [Fact]
    public async Task List_orgs_shows_the_companies_with_their_members_sorted_by_name()   // criterion 1
    {
        var acme = await CreateOrgAsync("Acme");
        await AddMemberAsync(acme, Boss, "admin");
        await AddMemberAsync(acme, "worker@acme.test", "user");

        var run = await RunAsync("list-orgs");

        Assert.Equal(0, run.Exit);
        var lines = run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(
            [$"{acme}\tAcme\t2", $"{await DevCompanyIdAsync()}\tDevelopment\t1"],   // ordinal: A before D
            lines);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Acme")]
    [InlineData("Acme ")]
    public async Task Create_org_refuses_a_name_that_breaks_the_rules(string name)
    {
        var before = await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken));

        var run = await CliAsync("create-org", "--name=" + name);

        Assert.Equal(1, run.Exit);
        Assert.Contains("error: invalid_request", run.Error);
        Assert.Equal("", run.Out);
        Assert.Equal(before, await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));
    }

    // ---- invite

    [Fact]
    public async Task Invite_queues_a_mail_for_the_first_admin_and_the_dispatcher_sends_it()   // criterion 2
    {
        var acme = await CreateOrgAsync("Acme");

        var run = await RunAsync("invite", "--org", acme.ToString(), "--email", Boss, "--role", "admin");

        Assert.Equal(0, run.Exit);
        Assert.Contains("within a minute", run.Out);
        var invite = Assert.Single(await InvitesAsync());
        Assert.Null(invite.InvitedBy);   // the operator
        Assert.Equal(await RoleIdAsync(acme, "admin"), invite.RoleId);
        Clock.Advance(TimeSpan.FromMinutes(5));   // the command stamped real time; the test host runs on its own clock
        await DispatchAsync();
        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Boss, mail.To);
        Assert.Contains("Acme", mail.TextBody);
        Assert.Contains("as admin", mail.TextBody);
        Assert.Contains(AuthAppFactory.DefaultInviteUrl + "?token=", mail.TextBody);
    }

    [Fact]
    public async Task The_role_is_found_by_its_name_without_regard_to_case()
    {
        var acme = await CreateOrgAsync("Acme");

        var run = await RunAsync("invite", "--org", acme.ToString(), "--email", Boss, "--role", "ADMIN");

        Assert.Equal(0, run.Exit);
        Assert.Equal(await RoleIdAsync(acme, "admin"), Assert.Single(await InvitesAsync()).RoleId);
    }

    [Fact]
    public async Task Invite_refuses_what_the_company_api_refuses_with_the_same_codes()
    {
        var acme = await CreateOrgAsync("Acme");
        await AddMemberAsync(acme, "worker@acme.test", "user");
        var ok = await RunAsync("invite", "--org", acme.ToString(), "--email", Boss, "--role", "admin");
        Assert.Equal(0, ok.Exit);

        var cases = new (string Code, string[] Args)[]
        {
            ("already_in_org", ["invite", "--org", acme.ToString(), "--email", "worker@acme.test", "--role", "user"]),
            ("invite_pending", ["invite", "--org", acme.ToString(), "--email", "BOSS@acme.test", "--role", "user"]),
            ("not_found", ["invite", "--org", acme.ToString(), "--email", "new@acme.test", "--role", "nobody"]),
            ("not_found", ["invite", "--org", Guid.NewGuid().ToString(), "--email", "new@acme.test", "--role", "user"]),
            ("invalid_request", ["invite", "--org", "not-a-uuid", "--email", "new@acme.test", "--role", "user"]),
            ("invalid_request", ["invite", "--org", acme.ToString(), "--email", "not an address", "--role", "user"]),
            ("invalid_request", ["invite", "--org", acme.ToString(), "--email", "new@acme.test", "--role", ""]),
        };
        foreach (var (code, args) in cases)
        {
            var run = await RunAsync(args);

            Assert.Equal(1, run.Exit);
            Assert.Equal($"error: {code}", run.Error.Trim());
            Assert.Equal("", run.Out);
        }

        Assert.Single(await InvitesAsync());
    }

    [Fact]
    public async Task Invitations_from_the_command_line_go_through_the_mail_limit()   // criterion 10
    {
        var acme = await CreateOrgAsync("Acme");
        Assert.Equal(0, (await RunAsync("invite", "--org", acme.ToString(), "--email", Boss, "--role", "admin")).Exit);
        await InDbAsync(db => db.Invites.ExecuteDeleteAsync(TestContext.Current.CancellationToken));   // as if it had been cancelled

        var run = await RunAsync("invite", "--org", acme.ToString(), "--email", Boss, "--role", "admin");

        Assert.Equal(1, run.Exit);
        Assert.Matches(@"^error: too_many_attempts \(retry in \d+ seconds\)$", run.Error.Trim());
        Assert.Empty(await InvitesAsync());
    }

    // ---- remove-member

    [Fact]
    public async Task Remove_member_removes_the_member_and_ends_every_session()   // criterion 22
    {
        var acme = await CreateOrgAsync("Acme");
        await AddMemberAsync(acme, Boss, "admin");
        var worker = await AddMemberAsync(acme, "worker@acme.test", "user");
        var session = await SessionApi.LoginAsync(Client, "worker@acme.test", UserPassword);

        var run = await RunAsync("remove-member", "--org", acme.ToString(), "--email", "WORKER@acme.test");

        Assert.Equal(0, run.Exit);
        Assert.Null(await InDbAsync(db => db.Memberships.SingleOrDefaultAsync(m => m.UserId == worker, TestContext.Current.CancellationToken)));
        using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refresh);
        using var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword);
        Assert.Equal("""{"error":"no_membership"}""", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Removing_the_last_manager_is_refused_unless_forced()   // criteria 16, 22
    {
        var acme = await CreateOrgAsync("Acme");
        var boss = await AddMemberAsync(acme, Boss, "admin");

        var refused = await RunAsync("remove-member", "--org", acme.ToString(), "--email", Boss);
        Assert.Equal(1, refused.Exit);
        Assert.Equal("error: last_manager", refused.Error.Trim());
        Assert.NotNull(await InDbAsync(db => db.Memberships.SingleOrDefaultAsync(m => m.UserId == boss, TestContext.Current.CancellationToken)));

        var forced = await RunAsync("remove-member", "--org", acme.ToString(), "--email", Boss, "--force");
        Assert.Equal(0, forced.Exit);
        Assert.Null(await InDbAsync(db => db.Memberships.SingleOrDefaultAsync(m => m.UserId == boss, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Remove_member_refuses_someone_who_is_not_a_member_of_that_company()
    {
        var acme = await CreateOrgAsync("Acme");
        var globex = await CreateOrgAsync("Globex");
        await AddMemberAsync(globex, "carol@globex.test", "user");

        foreach (var email in new[] { "carol@globex.test", "nobody@nowhere.test" })
        {
            var run = await RunAsync("remove-member", "--org", acme.ToString(), "--email", email);

            Assert.Equal(1, run.Exit);
            Assert.Equal("error: not_found", run.Error.Trim());
        }

        Assert.NotNull(await InDbAsync(db => db.Memberships.SingleOrDefaultAsync(m => m.CompanyId == globex, TestContext.Current.CancellationToken)));
    }

    // ---- arguments, configuration, the database

    [Theory]
    [InlineData("")]
    [InlineData("delete-org")]
    [InlineData("create-org")]
    [InlineData("create-org --name Acme --force")]
    [InlineData("list-orgs extra")]
    public async Task A_command_that_is_not_understood_is_a_usage_error_and_touches_nothing(string line)
    {
        var args = line.Length == 0 ? [] : line.Split(' ');

        var run = await CliAsync(args);

        Assert.Equal(2, run.Exit);
        Assert.StartsWith("error: ", run.Error);
        Assert.Contains("usage: auth-server admin", run.Error);
        Assert.Equal("", run.Out);
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));   // only the development company
    }

    [Fact]
    public async Task The_command_does_not_migrate_the_database()
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Postgres.ConnectionStringFor("postgres")).ConnectionString;
        await using (var admin = new NpgsqlConnection(connection))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var run = await RunAgainstAsync(Postgres.ConnectionStringFor(database), Factory.ManifestPath, ["create-org", "--name", "Acme"]);

        Assert.Equal(3, run.Exit);
        Assert.StartsWith("error: failed (", run.Error);
        Assert.Equal("", run.Out);
        await using var check = new NpgsqlConnection(Postgres.ConnectionStringFor(database));
        await check.OpenAsync(TestContext.Current.CancellationToken);
        await using var tables = new NpgsqlCommand("SELECT to_regclass('\"Companies\"') IS NULL", check);
        Assert.True((bool)(await tables.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);   // still nothing in it
        NpgsqlConnection.ClearPool(check);
    }

    [Fact]
    public async Task A_missing_connection_string_is_a_failure_that_names_nothing_secret()
    {
        var run = await RunAgainstAsync(null, Factory.ManifestPath, ["list-orgs"]);

        Assert.Equal(3, run.Exit);
        Assert.Equal("error: failed (InvalidOperationException): Configuration value 'ConnectionStrings:Auth' is missing or blank.", run.Error.Trim());
    }

    [Fact]
    public async Task A_connection_string_never_reaches_the_output()
    {
        var run = await RunAgainstAsync("Host=127.0.0.1;Port=1;Database=x;Username=u;Password=SECRET-MARKER;Timeout=2", Factory.ManifestPath, ["list-orgs"]);

        Assert.Equal(3, run.Exit);
        Assert.DoesNotContain("SECRET-MARKER", run.Out + run.Error);
        Assert.DoesNotContain("127.0.0.1", run.Out + run.Error);
    }

    [Fact]
    public async Task A_broken_manifest_does_not_stop_the_command_and_the_stored_one_is_used()   // criterion 20
    {
        _ = Factory.Services;   // the host stores the valid manifest at its start
        File.WriteAllText(Factory.ManifestPath, "default_roles:\n  user: [reports:write]\n");

        var logs = new CapturingLoggerProvider();

        var run = await RunAgainstAsync(Factory.ConnectionString, Factory.ManifestPath, ["create-org", "--name", "Acme"], logs);

        Assert.Equal(0, run.Exit);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("was not used") && e.Message.Contains("reports:write"));   // the reason is logged
        var id = Guid.Parse(run.Out.Trim());
        var roles = await InDbAsync(db => db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == id).OrderBy(r => r.Name).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["admin", "user"], roles.Select(r => r.Name));   // the last valid manifest's defaults
    }
}
```

`tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs` — the change:

```diff
--- a/tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs
+++ b/tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs
@@ -108,6 +108,9 @@
 
     public string DatabaseName { get; }
 
+    /// <summary>The connection string of this host's database, for a test that runs the operator CLI against it.</summary>
+    public string ConnectionString => _settings["ConnectionStrings:Auth"] ?? "";
+
     /// <summary>The manifest file of this host.</summary>
     public string ManifestPath { get; }
 
```

- [ ] **Step 2: Run them and confirm they fail.** Run `dotnet build -warnaserror`.
  Expected: FAIL to compile (`AdminArguments`, `AdminCli` do not exist).

- [ ] **Step 3: Implement.**

`src/Auth.Server/Admin/AdminArguments.cs`:

```csharp
namespace Auth.Server.Admin;

/// <summary>A command of <c>auth-server admin</c>, parsed.</summary>
public abstract record AdminCommand;

public sealed record CreateOrgCommand(string Name) : AdminCommand;

public sealed record InviteCommand(string Org, string Email, string Role) : AdminCommand;

public sealed record ListOrgsCommand : AdminCommand;

public sealed record RemoveMemberCommand(string Org, string Email, bool Force) : AdminCommand;

/// <summary>The outcome of reading the arguments: a command, or the code of what is wrong with them.</summary>
public readonly record struct AdminParseResult(AdminCommand? Command, string? Error);

/// <summary>
/// Reads the arguments of the four operator commands by hand (spec 0005 → Operator CLI): <c>--name value</c> or
/// <c>--name=value</c> in any order, and the flag <c>--force</c> on <c>remove-member</c>. Four commands do not need a
/// command-line library, and a library is one more dependency in an image that has to stay small.
/// </summary>
public static class AdminArguments
{
    public const string Usage =
        """
        usage: auth-server admin <command>
          create-org    --name <name>
          invite        --org <id> --email <email> --role <name>
          list-orgs
          remove-member --org <id> --email <email> [--force]
        """;

    private static readonly Dictionary<string, (string[] Options, string[] Flags)> Commands = new(StringComparer.Ordinal)
    {
        ["create-org"] = (["name"], []),
        ["invite"] = (["org", "email", "role"], []),
        ["list-orgs"] = ([], []),
        ["remove-member"] = (["org", "email"], ["force"]),
    };

    /// <param name="args">What follows <c>admin</c> on the command line.</param>
    public static AdminParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            return Fail("missing_command");
        }

        if (!Commands.TryGetValue(args[0], out var spec))
        {
            return Fail("unknown_command");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                return Fail("unexpected_argument");
            }

            var (name, inline) = Split(argument[2..]);
            if (spec.Flags.Contains(name, StringComparer.Ordinal) && inline is null)
            {
                if (!flags.Add(name))
                {
                    return Fail("duplicate_option");
                }

                continue;
            }

            if (!spec.Options.Contains(name, StringComparer.Ordinal))
            {
                return Fail("unknown_option");
            }

            string value;
            if (inline is not null)
            {
                value = inline;
            }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }
            else
            {
                return Fail("missing_value");
            }

            if (!values.TryAdd(name, value))
            {
                return Fail("duplicate_option");
            }
        }

        if (spec.Options.Any(option => !values.ContainsKey(option)))
        {
            return Fail("missing_option");
        }

        return new AdminParseResult(
            args[0] switch
            {
                "create-org" => new CreateOrgCommand(values["name"]),
                "invite" => new InviteCommand(values["org"], values["email"], values["role"]),
                "list-orgs" => new ListOrgsCommand(),
                _ => new RemoveMemberCommand(values["org"], values["email"], flags.Contains("force")),
            },
            null);
    }

    private static (string Name, string? Value) Split(string option)
    {
        var equals = option.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? (option, null) : (option[..equals], option[(equals + 1)..]);
    }

    private static AdminParseResult Fail(string error) => new(null, error);
}
```

`src/Auth.Server/Admin/AdminCli.cs`:

```csharp
using System.Globalization;
using Auth.Infrastructure;
using Auth.Infrastructure.Persistence;
using Auth.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.Server.Admin;

/// <summary>
/// <c>auth-server admin ...</c>: the operator's commands (spec 0005 → Operator CLI), run by the same binary and image as the
/// service and working directly on its database with its configuration. It builds the host's services and never runs the
/// host: no listener, no background service, no key material. It does not migrate: a database the service has not
/// migrated is refused. Output is plain text, a refusal is <c>error: &lt;code&gt;</c> on the error stream and a non-zero
/// exit code, and nothing it prints or logs carries a connection string, a password or a token.
/// </summary>
public static class AdminCli
{
    public const int Done = 0;

    /// <summary>The command was understood and refused, with the error code it printed.</summary>
    public const int Refused = 1;

    /// <summary>The arguments were not understood.</summary>
    public const int UsageError = 2;

    /// <summary>The command could not be carried out: no database, a database that is not migrated, a broken setting.</summary>
    public const int Failed = 3;

    /// <param name="args">What follows <c>admin</c> on the command line.</param>
    /// <param name="configure">Lets a test add configuration; the command line itself never reaches the configuration.</param>
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, Action<WebApplicationBuilder>? configure = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var parsed = AdminArguments.Parse(args);
        if (parsed.Command is not { } command)
        {
            await error.WriteLineAsync($"error: {parsed.Error}");
            await error.WriteLineAsync(AdminArguments.Usage);
            return UsageError;
        }

        try
        {
            // An empty argument list: the operator's own words (--force, --name) are not configuration.
            var builder = WebApplication.CreateBuilder([]);
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            // Before the registrations below, which read the configuration as they are made.
            configure?.Invoke(builder);
            builder.Services.AddAuthPersistence(builder.Configuration);
            builder.Services.TryAddSingleton(TimeProvider.System);
            builder.Services.AddOpenIddict().AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AuthDbContext>());
            builder.Services.AddTenancy(builder.Configuration, builder.Environment.ContentRootPath);

            await using var app = builder.Build();
            await app.Services.GetRequiredService<ManifestActivator>().ActivateAsync(cancellationToken);

            using var scope = app.Services.CreateScope();
            var operatorCommands = scope.ServiceProvider.GetRequiredService<OperatorCommands>();
            return await ExecuteAsync(command, operatorCommands, output, error, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type name only: the text of a database failure can quote the connection string. The one message that is
            // safe, and what the operator needs when a setting is missing, is our own: it names the key and never a value.
            var detail = exception is InvalidOperationException && exception.Message.StartsWith("Configuration value '", StringComparison.Ordinal)
                ? $": {exception.Message}"
                : "";
            await error.WriteLineAsync($"error: failed ({exception.GetType().Name}){detail}");
            return Failed;
        }
    }

    private static async Task<int> ExecuteAsync(
        AdminCommand command, OperatorCommands operatorCommands, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case CreateOrgCommand create:
                var created = await operatorCommands.CreateOrgAsync(create.Name, cancellationToken);
                if (!created.Succeeded)
                {
                    return await RefusedAsync(created.Without, error);
                }

                await output.WriteLineAsync(created.Value.ToString("D", CultureInfo.InvariantCulture));
                return Done;

            case ListOrgsCommand:
                foreach (var org in await operatorCommands.ListOrgsAsync(cancellationToken))
                {
                    await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{org.Id:D}\t{org.Name}\t{org.Members}"));
                }

                return Done;

            case InviteCommand invite:
                var invited = await operatorCommands.InviteAsync(invite.Org, invite.Email, invite.Role, cancellationToken);
                if (!invited.Succeeded)
                {
                    return await RefusedAsync(invited, error);
                }

                await output.WriteLineAsync("Invitation queued. The server mails it at its next poll, within a minute, and only while it is running.");
                return Done;

            case RemoveMemberCommand remove:
                var removed = await operatorCommands.RemoveMemberAsync(remove.Org, remove.Email, remove.Force, cancellationToken);
                if (!removed.Succeeded)
                {
                    return await RefusedAsync(removed, error);
                }

                await output.WriteLineAsync("Member removed; every session of the account has ended.");
                return Done;

            default:
                throw new InvalidOperationException("A command that the parser makes is not handled.");
        }
    }

    private static async Task<int> RefusedAsync(Outcome outcome, TextWriter error)
    {
        var wait = outcome.Error == TenancyErrors.TooManyAttempts
            ? string.Create(CultureInfo.InvariantCulture, $" (retry in {Math.Max(1, (long)Math.Ceiling(outcome.RetryAfter.TotalSeconds))} seconds)")
            : "";
        await error.WriteLineAsync($"error: {outcome.Error}{wait}");
        return Refused;
    }
}
```

`src/Auth.Server/Program.cs` — the change:

```diff
--- a/src/Auth.Server/Program.cs
+++ b/src/Auth.Server/Program.cs
@@ -2,6 +2,7 @@
 using System.Text.Json;
 using Auth.Infrastructure.Persistence;
 using Auth.Server.Account;
+using Auth.Server.Admin;
 using Auth.Server.Api;
 using Auth.Server.Email;
 using Auth.Server.Keys;
@@ -14,6 +15,12 @@
 using Microsoft.AspNetCore.Identity;
 using Microsoft.EntityFrameworkCore;
 using Microsoft.Extensions.DependencyInjection.Extensions;
+
+// The operator's commands run the same binary without the host: no listener, no background service, no key material.
+if (args is ["admin", .. var adminArguments])
+{
+    return await AdminCli.RunAsync(adminArguments, Console.Out, Console.Error);
+}
 
 var builder = WebApplication.CreateBuilder(args);
 
@@ -82,5 +89,6 @@
 app.MapPost(VerifyEmailEndpoint.Path, VerifyEmailEndpoint.HandleAsync);
 
 app.Run();
+return 0;
 
 public partial class Program;
```

`src/Auth.Server/Tenancy/OperatorCommands.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>One line of <c>list-orgs</c>.</summary>
public sealed record OrgListing(Guid Id, string Name, int Members);

/// <summary>
/// What the operator can do from the command line (spec 0005 → Operator CLI), on strings as typed: it finds the company,
/// the role and the member by what the operator wrote, and then calls the same services as the company API with the
/// operator as the actor. The operator is exempt from safety rule 1 and held to rules 2 and 3, except that
/// <c>--force</c> lifts rule 2.
/// </summary>
public sealed class OperatorCommands(
    AuthDbContext db, CompanyService companies, InvitationService invitations, MemberService members, ILookupNormalizer normalizer)
{
    public Task<Outcome<Guid>> CreateOrgAsync(string name, CancellationToken cancellationToken) =>
        companies.CreateAsync(name, cancellationToken);

    /// <summary>Companies sorted by name, ordinally, with their number of members.</summary>
    public async Task<IReadOnlyList<OrgListing>> ListOrgsAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Companies.AsNoTracking()
            .Select(c => new OrgListing(c.Id, c.Name, db.Memberships.Count(m => m.CompanyId == c.Id)))
            .ToListAsync(cancellationToken);
        return [.. rows.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id)];
    }

    /// <summary>Queues an invitation as the operator: the role is named, not identified, and compared without regard to case.</summary>
    public async Task<Outcome> InviteAsync(string org, string email, string role, CancellationToken cancellationToken)
    {
        if (!IdInput.TryParse(org, out var orgId)
            || email.Length > EmailInput.MaxLength
            || !EmailInput.IsMailbox(email)
            || !EmailInput.TryNormalize(email, normalizer, out var normalized)
            || !NameInput.IsValid(role))
        {
            return Outcome.Fail(TenancyErrors.InvalidRequest);
        }

        var roleKey = NameInput.Normalize(role);
        var roleId = await db.CompanyRoles.AsNoTracking()
            .Where(r => r.CompanyId == orgId && r.NormalizedName == roleKey)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return roleId is { } id
            ? await invitations.SendAsync(Actor.Operator, orgId, email, normalized, id, cancellationToken)
            : Outcome.Fail(TenancyErrors.NotFound);
    }

    /// <summary>Removes a member as the API does and ends their sessions; <paramref name="force"/> overrides <c>last_manager</c>.</summary>
    public async Task<Outcome> RemoveMemberAsync(string org, string email, bool force, CancellationToken cancellationToken)
    {
        if (!IdInput.TryParse(org, out var orgId) || email.Length > EmailInput.MaxLength || !EmailInput.TryNormalize(email, normalizer, out var normalized))
        {
            return Outcome.Fail(TenancyErrors.InvalidRequest);
        }

        var userId = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == normalized)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return userId is { } id
            ? await members.RemoveAsync(Actor.Operator, orgId, id, force, cancellationToken)
            : Outcome.Fail(TenancyErrors.NotFound);
    }
}
```

`src/Auth.Server/Tenancy/TenancyServices.cs` — the change:

```diff
--- a/src/Auth.Server/Tenancy/TenancyServices.cs
+++ b/src/Auth.Server/Tenancy/TenancyServices.cs
@@ -20,6 +20,7 @@
         services.AddScoped<InviteAcceptance>();
         services.AddScoped<MemberService>();
         services.AddScoped<RoleService>();
+        services.AddScoped<OperatorCommands>();
         return services;
     }
 }
```

- [ ] **Step 4: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS — 32 new tests, 785 in all. The compose stack is not needed yet; the e2e
  of Task 15 runs the commands in the real image.

- [ ] **Step 5: Commit** — `feat(admin): the operator CLI`

### Task 14: The OpenAPI description

**Files:**
- Create: `src/Auth.Server/Api/OpenApiSetup.cs`, `src/Auth.Server/Api/AccountEndpoints.cs`,
  `src/Auth.Server/Api/AccountContracts.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`
- Modify: `Directory.Packages.props`, `src/Auth.Server/Auth.Server.csproj`,
  `src/Auth.Server/Program.cs`, `src/Auth.Server/Api/EndpointMetadata.cs`,
  `src/Auth.Server/Api/TenancyEndpoints.cs`

**Interfaces:**
- Consumes: the metadata of Tasks 6–12 (`ProducesError`, `ProducesGuarded`, `ReadsJson<T>`).
- Produces: `GET /auth/openapi/v1.json` in **every** environment, without a token, describing
  every endpoint of specs 0001–0005 with its request, its responses and — in each error
  response's description — the codes it carries; the bearer scheme and a security
  requirement on every endpoint that needs a token; two endpoints that are not routes of
  ours (the JWKS and the health check) added by a document transformer. `/auth/scalar/v1`
  (the interactive reference) in `Development` only; in a real server `/auth/scalar`
  redirects to `/auth/scalar/`.
- Produces: `AccountEndpoints.MapAccountApi()` — the routes of specs 0001–0004 (login,
  refresh, logout, forgot, reset, verify and its request, the health check) **moved out of
  `Program.cs`** unchanged and given what each takes and answers; `AccountContracts`
  (`LoginRequest`, `LoginResponse`, `RefreshResponse`, `EmailRequest`, `ResetPasswordRequest`,
  `TokenRequest`, `TooManyAttemptsBody`) — types that document the bodies, which the
  handlers still read and write by hand.
- Produces: `EndpointMetadata.ProducesTooManyAttempts()` (the lockout body and its codes) and
  `ProducesPasswordError(codes)` (`400` with `WeakPasswordBody`, whose `rules` the
  description names), and tags (`Sessions`, `Account`, `Invitations`, `Company`).
- The three new packages are the ones of the header; the first two are added here
  (`YamlDotNet` came with Task 1). **The owner approved the downloads.**

- [ ] **Step 1: Add the packages.**

`Directory.Packages.props` — the change:

```diff
--- a/Directory.Packages.props
+++ b/Directory.Packages.props
@@ -22,5 +22,9 @@
     <PackageVersion Include="MailKit" Version="4.18.1" />
     <!-- Manifest (plan 0005, Task 1). MIT; no dependencies on net10.0. -->
     <PackageVersion Include="YamlDotNet" Version="18.1.0" />
+    <!-- OpenAPI description (plan 0005, Task 14). Both MIT. The first brings Microsoft.OpenApi 2.12; the second has no
+         dependencies on net10.0 and is mapped in Development only. -->
+    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.12" />
+    <PackageVersion Include="Scalar.AspNetCore" Version="2.17.13" />
   </ItemGroup>
 </Project>
```

`src/Auth.Server/Auth.Server.csproj` — the change:

```diff
--- a/src/Auth.Server/Auth.Server.csproj
+++ b/src/Auth.Server/Auth.Server.csproj
@@ -12,6 +12,8 @@
     <PackageReference Include="OpenIddict.AspNetCore" />
     <PackageReference Include="MailKit" />
     <PackageReference Include="YamlDotNet" />
+    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
+    <PackageReference Include="Scalar.AspNetCore" />
   </ItemGroup>
 
   <ItemGroup>
```

- [ ] **Step 2: Write the failing tests.** They guard the description against drifting: one
  checks that every route of the service is in it (a new endpoint without a description
  fails here), one that every error code an endpoint declares is in the description of its
  status, and a table pins the statuses of the contract.

`tests/Auth.IntegrationTests/OpenApiTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class OpenApiTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string DocumentUrl = "/auth/openapi/v1.json";

    // Every endpoint of specs 0001–0005, as "METHOD path".
    private static readonly string[] Endpoints =
    [
        "POST /auth/login", "POST /auth/refresh", "POST /auth/logout",
        "POST /auth/password/forgot", "POST /auth/password/reset", "POST /auth/email/verify/request", "POST /auth/email/verify",
        "GET /auth/.well-known/jwks.json", "GET /auth/health",
        "GET /auth/me",
        "POST /auth/invites/preview", "POST /auth/invites/accept",
        "GET /auth/org", "PATCH /auth/org",
        "GET /auth/org/members", "PUT /auth/org/members/{user_id}/role", "DELETE /auth/org/members/{user_id}",
        "GET /auth/org/invites", "POST /auth/org/invites", "POST /auth/org/invites/{id}/resend", "DELETE /auth/org/invites/{id}",
        "GET /auth/org/roles", "POST /auth/org/roles", "PUT /auth/org/roles/{id}", "DELETE /auth/org/roles/{id}",
    ];

    private static async Task<JsonObject> DescriptionAsync(AuthAppFactory factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(DocumentUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static Dictionary<string, JsonObject> Operations(JsonObject document)
    {
        var operations = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (path, item) in document["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                operations[$"{method.ToUpperInvariant()} {path}"] = operation!.AsObject();
            }
        }

        return operations;
    }

    /// <summary>The path of a route as the description writes it: a group's empty route has no trailing slash there.</summary>
    private static string PathOf(RouteEndpoint route) => route.RoutePattern.RawText!.TrimEnd('/');

    private static string[] Statuses(JsonObject operation) => [.. operation["responses"]!.AsObject().Select(r => r.Key).Order(StringComparer.Ordinal)];

    private static string Description(JsonObject operation, string status) =>
        operation["responses"]![status]!["description"]!.GetValue<string>();

    private static string[] BodyProperties(JsonObject document, JsonObject operation)
    {
        var schema = operation["requestBody"]!["content"]!["application/json"]!["schema"]!.AsObject();
        if (schema["$ref"] is { } reference)
        {
            schema = document["components"]!["schemas"]![reference.GetValue<string>().Split('/')[^1]]!.AsObject();
        }

        return [.. schema["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task The_description_is_served_without_a_token_in_every_environment()   // criterion 24
    {
        foreach (var environment in new[] { "Development", "Production" })
        {
            await using var factory = new AuthAppFactory(postgres, keys).WithEnvironment(environment);

            var document = await DescriptionAsync(factory);

            Assert.StartsWith("3.", document["openapi"]!.GetValue<string>());
            Assert.Equal("Auth-Core", document["info"]!["title"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task The_description_names_every_endpoint_of_the_service_and_nothing_else()   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);

        var operations = Operations(await DescriptionAsync(factory));

        Assert.Equal(Endpoints.Order(StringComparer.Ordinal), operations.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Every_route_of_the_service_is_in_the_description()   // criterion 24: a new endpoint without a description fails here
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var operations = Operations(await DescriptionAsync(factory));
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => PathOf(e).StartsWith("/auth/", StringComparison.Ordinal)
                && !PathOf(e).StartsWith("/auth/openapi", StringComparison.Ordinal)
                && !PathOf(e).StartsWith("/auth/scalar", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(routes);
        foreach (var route in routes)
        {
            var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var method in methods)
            {
                Assert.True(operations.ContainsKey($"{method} {PathOf(route)}"), $"{method} {PathOf(route)} is not described.");
            }
        }
    }

    [Fact]
    public async Task Every_error_status_carries_the_codes_the_endpoint_declares()   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var operations = Operations(await DescriptionAsync(factory));
        var declared = 0;

        foreach (var route in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var codes = route.Metadata.GetOrderedMetadata<ErrorCodesMetadata>();
            if (codes.Count == 0)
            {
                continue;
            }

            var method = route.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single();
            var operation = operations[$"{method} {PathOf(route)}"];
            foreach (var entry in codes)
            {
                foreach (var code in entry.Codes)
                {
                    Assert.Contains(code, Description(operation, entry.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    declared++;
                }
            }
        }

        Assert.True(declared > 60, $"Only {declared} error codes were declared.");
    }

    [Theory]
    [InlineData("POST /auth/login", "200,400,401,403,429")]
    [InlineData("POST /auth/refresh", "200,401")]
    [InlineData("POST /auth/logout", "204")]
    [InlineData("POST /auth/password/forgot", "202,400,429")]
    [InlineData("POST /auth/password/reset", "204,400")]
    [InlineData("POST /auth/email/verify/request", "202,400,429")]
    [InlineData("POST /auth/email/verify", "204,400")]
    [InlineData("GET /auth/me", "200,401,403")]
    [InlineData("POST /auth/invites/preview", "200,400,409")]
    [InlineData("POST /auth/invites/accept", "204,400,409")]
    [InlineData("GET /auth/org", "200,401,403")]
    [InlineData("PATCH /auth/org", "204,400,401,403")]
    [InlineData("GET /auth/org/members", "200,401,403")]
    [InlineData("PUT /auth/org/members/{user_id}/role", "204,400,401,403,404,409")]
    [InlineData("DELETE /auth/org/members/{user_id}", "204,400,401,403,404,409")]
    [InlineData("GET /auth/org/invites", "200,401,403")]
    [InlineData("POST /auth/org/invites", "202,400,401,403,404,409,429")]
    [InlineData("POST /auth/org/invites/{id}/resend", "202,400,401,403,404,429")]
    [InlineData("DELETE /auth/org/invites/{id}", "204,400,401,403,404")]
    [InlineData("GET /auth/org/roles", "200,401,403")]
    [InlineData("POST /auth/org/roles", "201,400,401,403,409")]
    [InlineData("PUT /auth/org/roles/{id}", "204,400,401,403,404,409")]
    [InlineData("DELETE /auth/org/roles/{id}", "204,400,401,403,404,409")]
    public async Task Each_endpoint_describes_the_statuses_of_the_contract(string endpoint, string statuses)   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);

        var operation = Operations(await DescriptionAsync(factory))[endpoint];

        Assert.Equal(statuses.Split(','), Statuses(operation));
    }

    [Fact]
    public async Task The_error_codes_of_the_contract_are_in_the_descriptions()   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var operations = Operations(await DescriptionAsync(factory));

        Assert.All(["email_not_verified", "no_membership"], code => Assert.Contains(code, Description(operations["POST /auth/login"], "403")));
        Assert.Contains("invalid_credentials", Description(operations["POST /auth/login"], "401"));
        Assert.Contains("too_many_attempts", Description(operations["POST /auth/login"], "429"));
        Assert.Contains("invalid_grant", Description(operations["POST /auth/refresh"], "401"));
        Assert.All(["invalid_request", "invalid_token", "weak_password"], code => Assert.Contains(code, Description(operations["POST /auth/invites/accept"], "400")));
        Assert.Contains("already_member", Description(operations["POST /auth/invites/accept"], "409"));
        Assert.All(["permissions_changed", "forbidden"], code => Assert.Contains(code, Description(operations["GET /auth/org/members"], "403")));
        Assert.All(["already_in_org", "invite_pending"], code => Assert.Contains(code, Description(operations["POST /auth/org/invites"], "409")));
        Assert.Contains("permission_not_held", Description(operations["POST /auth/org/invites"], "403"));
        Assert.All(["cannot_change_self", "last_manager"], code => Assert.Contains(code, Description(operations["DELETE /auth/org/members/{user_id}"], "409")));
        Assert.All(["role_name_taken", "last_manager"], code => Assert.Contains(code, Description(operations["PUT /auth/org/roles/{id}"], "409")));
        Assert.Contains("role_in_use", Description(operations["DELETE /auth/org/roles/{id}"], "409"));
        Assert.Contains("unknown_permission", Description(operations["POST /auth/org/roles"], "400"));
        Assert.Contains("not_found", Description(operations["DELETE /auth/org/invites/{id}"], "404"));
    }

    [Theory]
    [InlineData("POST /auth/login", "email,password")]
    [InlineData("POST /auth/password/forgot", "email")]
    [InlineData("POST /auth/password/reset", "new_password,token")]
    [InlineData("POST /auth/email/verify/request", "email")]
    [InlineData("POST /auth/email/verify", "token")]
    [InlineData("POST /auth/invites/preview", "token")]
    [InlineData("POST /auth/invites/accept", "password,token")]
    [InlineData("PATCH /auth/org", "name")]
    [InlineData("POST /auth/org/invites", "email,role_id")]
    [InlineData("PUT /auth/org/members/{user_id}/role", "role_id")]
    [InlineData("POST /auth/org/roles", "name,permissions")]
    [InlineData("PUT /auth/org/roles/{id}", "name,permissions")]
    public async Task Each_endpoint_that_reads_a_body_describes_it_in_snake_case(string endpoint, string properties)   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var document = await DescriptionAsync(factory);

        var operation = Operations(document)[endpoint];

        Assert.Equal(properties.Split(','), BodyProperties(document, operation));
    }

    [Fact]
    public async Task The_responses_are_described_in_snake_case()   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var schemas = (await DescriptionAsync(factory))["components"]!["schemas"]!.AsObject();

        string[] Properties(string schema) => [.. schemas[schema]!["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)];

        Assert.Equal(["email", "org_id", "org_name", "permissions", "roles", "sub"], Properties("MeResponse"));
        Assert.Equal(["email", "joined_at", "role", "user_id"], Properties("MemberItem"));
        Assert.Equal(["email", "expires_at", "id", "invited_at", "role"], Properties("InviteItem"));
        Assert.Equal(["id", "members", "name", "permissions"], Properties("RoleItem"));
        Assert.Equal(["catalog", "roles"], Properties("RolesResponse"));
        Assert.Equal(["email", "org_name", "role"], Properties("InvitePreviewResponse"));
        Assert.Equal(["access_token", "status"], Properties("LoginResponse"));
        Assert.Equal(["error", "retry_after_seconds"], Properties("TooManyAttemptsBody"));
        Assert.Equal(["error", "rules"], Properties("WeakPasswordBody"));
        Assert.Equal(["error"], Properties("ErrorBody"));
    }

    [Fact]
    public async Task The_company_endpoints_ask_for_a_bearer_token_and_the_public_ones_do_not()   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var document = await DescriptionAsync(factory);

        var scheme = document["components"]!["securitySchemes"]!["Bearer"]!;
        Assert.Equal("http", scheme["type"]!.GetValue<string>());
        Assert.Equal("bearer", scheme["scheme"]!.GetValue<string>());
        Assert.Equal("JWT", scheme["bearerFormat"]!.GetValue<string>());
        foreach (var (endpoint, operation) in Operations(document))
        {
            var guarded = endpoint.Split(' ')[1].StartsWith("/auth/org", StringComparison.Ordinal) || endpoint.EndsWith("/auth/me", StringComparison.Ordinal);
            Assert.Equal(guarded, operation["security"] is JsonArray { Count: > 0 });
        }
    }

    [Fact]
    public async Task The_reset_and_accept_answers_name_the_password_rules()
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var operations = Operations(await DescriptionAsync(factory));

        foreach (var endpoint in new[] { "POST /auth/password/reset", "POST /auth/invites/accept" })
        {
            var schema = operations[endpoint]["responses"]!["400"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>();
            Assert.EndsWith("WeakPasswordBody", schema);
        }
    }

    [Fact]
    public async Task The_interactive_reference_is_served_in_development_only()   // criterion 24
    {
        await using var development = new AuthAppFactory(postgres, keys);
        using var developmentClient = development.CreateClient();
        using var shown = await developmentClient.GetAsync("/auth/scalar");
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Equal("text/html", shown.Content.Headers.ContentType?.MediaType);

        await using var production = new AuthAppFactory(postgres, keys).WithEnvironment("Production");
        using var productionClient = production.CreateClient();
        using var hidden = await productionClient.GetAsync("/auth/scalar");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var alsoHidden = await productionClient.GetAsync("/auth/scalar/v1");
        Assert.Equal(HttpStatusCode.NotFound, alsoHidden.StatusCode);
    }
}
```

- [ ] **Step 3: Run them and confirm they fail.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: the build passes and the new tests FAIL: `/auth/openapi/v1.json` is `404`.

- [ ] **Step 4: Implement.** The routes of specs 0001–0004 move to `AccountEndpoints.cs`
  (only the mapping moves: no handler changes), and `Program.cs` maps the three groups. The
  description is generated by `Microsoft.AspNetCore.OpenApi`; `OpenApiSetup` adds what the
  framework cannot see: the JSON bodies the handlers read, the error codes, the bearer scheme,
  the JWKS and the health check.

`src/Auth.Server/Api/AccountContracts.cs`:

```csharp
namespace Auth.Server.Api;

// The bodies of the endpoints of specs 0001–0004, as the OpenAPI description names them. Property names become snake_case on
// the wire. The endpoints read and write these bodies by hand, so these types document the contract; they do not enforce it.

/// <summary>Request of <c>POST /auth/login</c>.</summary>
public sealed record LoginRequest(string Email, string Password);

/// <summary>Response of <c>POST /auth/login</c>. The refresh token travels in the <c>auth_rt</c> cookie, never in the body.</summary>
public sealed record LoginResponse(string Status, string AccessToken);

/// <summary>Response of <c>POST /auth/refresh</c>.</summary>
public sealed record RefreshResponse(string AccessToken);

/// <summary>Request of <c>POST /auth/password/forgot</c> and <c>POST /auth/email/verify/request</c>.</summary>
public sealed record EmailRequest(string Email);

/// <summary>Request of <c>POST /auth/password/reset</c>.</summary>
public sealed record ResetPasswordRequest(string Token, string NewPassword);

/// <summary>Request of <c>POST /auth/email/verify</c>.</summary>
public sealed record TokenRequest(string Token);

/// <summary>The <c>429</c> of the lockout (spec 0003) and of the mail limit (spec 0004); <c>Retry-After</c> carries the same number.</summary>
public sealed record TooManyAttemptsBody(string Error, int RetryAfterSeconds);
```

`src/Auth.Server/Api/AccountEndpoints.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Email;
using Auth.Server.Login;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Api;

public static class AccountEndpoints
{
    public const string HealthPath = "/auth/health";

    /// <summary>
    /// The endpoints of specs 0001–0004: login, refresh, logout, and the two flows by mail. Mapped here, with what each takes
    /// and answers, so that the OpenAPI description (spec 0005) says it all in one place. The metadata only describes: the
    /// handlers read and answer the bodies themselves, and a request that is not what they take is their <c>400</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapAccountApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The health check has no OpenAPI metadata of its own: the description adds it (see OpenApiSetup).
        app.MapHealthChecks(HealthPath);

        app.MapPost(JsonLoginRequestHandler.LoginPath, LoginEndpoint.HandleAsync)
            .WithTags("Sessions")
            .ReadsJson<LoginRequest>()
            .Produces<LoginResponse>()
            .ProducesError(StatusCodes.Status400BadRequest, "invalid_request")
            .ProducesError(StatusCodes.Status401Unauthorized, LoginEndpoint.InvalidCredentialsError)
            .ProducesError(StatusCodes.Status403Forbidden, AccountResults.EmailNotVerifiedError, AccountResults.NoMembershipError)
            .ProducesTooManyAttempts();
        app.MapPost(RefreshRequestHandler.RefreshPath, RefreshEndpoint.HandleAsync)
            .WithTags("Sessions")
            .Produces<RefreshResponse>()
            .ProducesError(StatusCodes.Status401Unauthorized, "invalid_grant");
        app.MapPost(LogoutEndpoint.LogoutPath, LogoutEndpoint.HandleAsync)
            .WithTags("Sessions")
            .Produces(StatusCodes.Status204NoContent);

        app.MapPost(
                MailRequestEndpoint.ForgotPasswordPath,
                (HttpContext http, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal) =>
                    MailRequestEndpoint.HandleAsync(MailKind.PasswordReset, http, normalizer, requests, signal))
            .WithTags("Account")
            .ReadsJson<EmailRequest>()
            .Produces(StatusCodes.Status202Accepted)
            .ProducesError(StatusCodes.Status400BadRequest, AccountResults.InvalidRequestError)
            .ProducesTooManyAttempts();
        app.MapPost(
                MailRequestEndpoint.VerifyEmailRequestPath,
                (HttpContext http, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal) =>
                    MailRequestEndpoint.HandleAsync(MailKind.EmailVerification, http, normalizer, requests, signal))
            .WithTags("Account")
            .ReadsJson<EmailRequest>()
            .Produces(StatusCodes.Status202Accepted)
            .ProducesError(StatusCodes.Status400BadRequest, AccountResults.InvalidRequestError)
            .ProducesTooManyAttempts();
        app.MapPost(ResetPasswordEndpoint.Path, ResetPasswordEndpoint.HandleAsync)
            .WithTags("Account")
            .ReadsJson<ResetPasswordRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesPasswordError(AccountResults.InvalidRequestError, AccountResults.InvalidTokenError, AccountResults.WeakPasswordError);
        app.MapPost(VerifyEmailEndpoint.Path, VerifyEmailEndpoint.HandleAsync)
            .WithTags("Account")
            .ReadsJson<TokenRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, AccountResults.InvalidRequestError, AccountResults.InvalidTokenError);

        return app;
    }
}
```

`src/Auth.Server/Api/EndpointMetadata.cs` — the change:

```diff
--- a/src/Auth.Server/Api/EndpointMetadata.cs
+++ b/src/Auth.Server/Api/EndpointMetadata.cs
@@ -36,6 +36,29 @@
     }
 
     /// <summary>
+    /// The <c>400</c> of an endpoint that sets a password: <c>invalid_request</c> and <c>invalid_token</c> as everywhere, and
+    /// <c>weak_password</c>, whose body also names the rules the password breaks.
+    /// </summary>
+    public static RouteHandlerBuilder ProducesPasswordError(this RouteHandlerBuilder builder, params string[] codes)
+    {
+        ArgumentNullException.ThrowIfNull(builder);
+
+        return builder
+            .Produces<WeakPasswordBody>(StatusCodes.Status400BadRequest, "application/json")
+            .WithMetadata(new ErrorCodesMetadata(StatusCodes.Status400BadRequest, codes));
+    }
+
+    /// <summary>The <c>429</c> of the lockout or of a mail limit: the lockout body, and the <c>Retry-After</c> header with the same number.</summary>
+    public static RouteHandlerBuilder ProducesTooManyAttempts(this RouteHandlerBuilder builder)
+    {
+        ArgumentNullException.ThrowIfNull(builder);
+
+        return builder
+            .Produces<TooManyAttemptsBody>(StatusCodes.Status429TooManyRequests, "application/json")
+            .WithMetadata(new ErrorCodesMetadata(StatusCodes.Status429TooManyRequests, [TenancyErrors.TooManyAttempts]));
+    }
+
+    /// <summary>
     /// The answers every endpoint behind an access token and a permission check can give: a <c>401</c> with an empty
     /// body and a <c>WWW-Authenticate: Bearer</c> challenge, and the two <c>403</c>s of the permission check.
     /// </summary>
```

`src/Auth.Server/Api/OpenApiSetup.cs`:

```csharp
using System.Net.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Auth.Server.Api;

/// <summary>
/// The OpenAPI description of the service (spec 0005 → OpenAPI): every endpoint of specs 0001–0005 with its request, its
/// responses and the error codes each response carries, at <c>GET /auth/openapi/v1.json</c> in every environment. An
/// interactive reference of it is served in Development only. The endpoints describe themselves where they are mapped
/// (<see cref="EndpointMetadata"/>); this adds what the framework cannot see: the JSON bodies the handlers read, the error
/// codes, the bearer scheme, and the two endpoints that are not minimal-API routes.
/// </summary>
public static class OpenApiSetup
{
    public const string DocumentName = "v1";
    public const string DocumentPath = "/auth/openapi/{documentName}.json";
    public const string ReferencePath = "/auth/scalar";
    public const string BearerScheme = "Bearer";
    public const string JwksPath = "/auth/.well-known/jwks.json";

    public static IServiceCollection AddAuthOpenApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer(DescribeDocumentAsync);
            options.AddOperationTransformer(DescribeOperationAsync);
        });
    }

    public static IEndpointRouteBuilder MapAuthOpenApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapOpenApi(DocumentPath);
        if (app.Environment.IsDevelopment())
        {
            app.MapScalarApiReference(ReferencePath, options => options.WithOpenApiRoutePattern(DocumentPath));
        }

        return app;
    }

    private static Task DescribeDocumentAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Auth-Core",
            Version = DocumentName,
            Description = "Accounts, sessions, companies, members, roles and invitations. Errors are `{\"error\":\"<code>\"}`; "
                + "every response of the account and company endpoints is marked `Cache-Control: no-store`.",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "The access token of `POST /auth/login` or `POST /auth/refresh`. The company API reads the caller's "
                + "membership and permissions from the database, not from the token.",
        };

        // Two endpoints that are not routes of ours: the key set is OpenIddict's, the health check has no metadata.
        document.Paths ??= [];
        document.Paths[JwksPath] = PathWithGet(
            "The public keys that verify the access tokens (RFC 7517).", "200", "The JSON Web Key Set.", "application/json");
        document.Paths[AccountEndpoints.HealthPath] = PathWithGet(
            "Whether the service works. `Degraded` means the product's manifest file was not used and the last valid one is active.",
            "200", "`Healthy` or `Degraded`.", "text/plain");
        return Task.CompletedTask;
    }

    private static OpenApiPathItem PathWithGet(string summary, string status, string description, string contentType) => new()
    {
        Operations = new Dictionary<HttpMethod, OpenApiOperation>
        {
            [HttpMethod.Get] = new OpenApiOperation
            {
                Summary = summary,
                Responses = new OpenApiResponses
                {
                    [status] = new OpenApiResponse
                    {
                        Description = description,
                        Content = new Dictionary<string, OpenApiMediaType> { [contentType] = new OpenApiMediaType() },
                    },
                },
            },
        },
    };

    private static async Task DescribeOperationAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;

        // The body the handler reads itself.
        if (metadata.OfType<JsonRequestMetadata>().FirstOrDefault() is { } request)
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType { Schema = await context.GetOrCreateSchemaAsync(request.Body, null, cancellationToken) },
                },
            };
        }

        // The codes of each error status, from every place that declared the status.
        foreach (var status in metadata.OfType<ErrorCodesMetadata>().GroupBy(e => e.Status))
        {
            var codes = status.SelectMany(e => e.Codes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            if (operation.Responses?.TryGetValue(status.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), out var response) == true && response is OpenApiResponse concrete)
            {
                concrete.Description = "Error: " + string.Join(", ", codes);
            }
        }

        if (metadata.OfType<IAuthorizeData>().Any())
        {
            operation.Security =
            [
                new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(BearerScheme, null)] = [] },
            ];
        }
    }
}
```

`src/Auth.Server/Api/TenancyEndpoints.cs` — the change:

```diff
--- a/src/Auth.Server/Api/TenancyEndpoints.cs
+++ b/src/Auth.Server/Api/TenancyEndpoints.cs
@@ -15,22 +15,24 @@
 
         app.MapGet(OrgEndpoints.MePath, OrgEndpoints.MeAsync)
             .RequireAuthorization()
+            .WithTags("Company")
             .Produces<MeResponse>()
             .ProducesGuarded();
 
         app.MapPost(InviteEndpoints.PreviewPath, InviteEndpoints.PreviewAsync)
+            .WithTags("Invitations")
             .ReadsJson<PreviewInviteRequest>()
             .Produces<InvitePreviewResponse>()
             .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken)
             .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember);
         app.MapPost(InviteEndpoints.AcceptPath, InviteEndpoints.AcceptAsync)
+            .WithTags("Invitations")
             .ReadsJson<AcceptInviteRequest>()
             .Produces(StatusCodes.Status204NoContent)
-            .Produces<WeakPasswordBody>(StatusCodes.Status400BadRequest, "application/json")
-            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken, TenancyErrors.WeakPassword)
+            .ProducesPasswordError(TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken, TenancyErrors.WeakPassword)
             .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember);
 
-        var company = app.MapGroup(OrgEndpoints.OrgPath).RequireAuthorization();
+        var company = app.MapGroup(OrgEndpoints.OrgPath).RequireAuthorization().WithTags("Company");
 
         company.MapGet("", OrgEndpoints.GetAsync)
             .Produces<OrgResponse>()
@@ -51,13 +53,13 @@
             .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.PermissionNotHeld)
             .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
             .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyInOrg, TenancyErrors.InvitePending)
-            .ProducesError(StatusCodes.Status429TooManyRequests, TenancyErrors.TooManyAttempts)
+            .ProducesTooManyAttempts()
             .ProducesGuarded();
         company.MapPost("invites/{id}/resend", OrgInviteEndpoints.ResendAsync)
             .Produces(StatusCodes.Status202Accepted)
             .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
             .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
-            .ProducesError(StatusCodes.Status429TooManyRequests, TenancyErrors.TooManyAttempts)
+            .ProducesTooManyAttempts()
             .ProducesGuarded();
         company.MapDelete("invites/{id}", OrgInviteEndpoints.CancelAsync)
             .Produces(StatusCodes.Status204NoContent)
```

`src/Auth.Server/Program.cs` — the change:

```diff
--- a/src/Auth.Server/Program.cs
+++ b/src/Auth.Server/Program.cs
@@ -33,6 +33,7 @@
 // property names already are.
 builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
 builder.Services.AddHealthChecks().AddCheck<ManifestHealthCheck>("manifest");
+builder.Services.AddAuthOpenApi();
 builder.Services.AddTenancy(builder.Configuration, builder.Environment.ContentRootPath);
 builder.Services.AddAuthPersistence(builder.Configuration);
 // OpenIddict takes its clock from DI; tests replace this registration to move time.
@@ -74,19 +75,9 @@
 app.UseAuthentication();
 app.UseAuthorization();
 
-app.MapHealthChecks("/auth/health");
+app.MapAccountApi();
 app.MapTenancyApi();
-app.MapPost(JsonLoginRequestHandler.LoginPath, LoginEndpoint.HandleAsync);
-app.MapPost(RefreshRequestHandler.RefreshPath, RefreshEndpoint.HandleAsync);
-app.MapPost(LogoutEndpoint.LogoutPath, LogoutEndpoint.HandleAsync);
-app.MapPost(MailRequestEndpoint.ForgotPasswordPath,
-    (HttpContext http, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal) =>
-        MailRequestEndpoint.HandleAsync(MailKind.PasswordReset, http, normalizer, requests, signal));
-app.MapPost(MailRequestEndpoint.VerifyEmailRequestPath,
-    (HttpContext http, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal) =>
-        MailRequestEndpoint.HandleAsync(MailKind.EmailVerification, http, normalizer, requests, signal));
-app.MapPost(ResetPasswordEndpoint.Path, ResetPasswordEndpoint.HandleAsync);
-app.MapPost(VerifyEmailEndpoint.Path, VerifyEmailEndpoint.HandleAsync);
+app.MapAuthOpenApi();
 
 app.Run();
 return 0;
```

- [ ] **Step 5: Run and confirm they pass.** Run `dotnet build -warnaserror && dotnet test`.
  Expected: PASS — 44 new tests, 829 in all. Then `dotnet list package --vulnerable
  --include-transitive` (none) and `dotnet list src/Auth.Server package --include-transitive`
  (the three new packages and `Microsoft.OpenApi` 2.12.0 are the only additions).

- [ ] **Step 6: Commit** — `feat(openapi): describe every endpoint; the reference UI in Development`

### Task 15: Real-network e2e, compose, docs and acceptance map

**Files:**
- Create: `scripts/e2e-tenancy.sh`, `docs/superpowers/plans/0005-acceptance-map.md`
- Modify: `deploy/docker-compose.yml`, `README.md`, `docs/design.md`

**Interfaces:**
- Consumes: the compose stack and `.env` of slices 1 and 4 (PostgreSQL, Mailpit, the service),
  the endpoints of Tasks 5–14, the operator commands of Task 13.
- Produces: the development manifest mounted into the container
  (`./auth.yaml:/etc/auth-core/auth.yaml:ro`) and named (`Auth__Manifest__Path`);
  `scripts/e2e-tenancy.sh`; the README's CLI section; the acceptance map.

**Compose.** Two lines in the `auth` service, and the header comment:

`deploy/docker-compose.yml` — the change:

```diff
--- a/deploy/docker-compose.yml
+++ b/deploy/docker-compose.yml
@@ -1,4 +1,4 @@
-# Local development stack: PostgreSQL, a mail catcher and the auth service.
+# Local development stack: PostgreSQL, a mail catcher and the auth service (with the development manifest, deploy/auth.yaml).
 #
 # Compose looks for .env next to this file (deploy/), not in the repo root, so always pass the
 # repo-root file explicitly (the e2e script does this for you):
@@ -52,12 +52,15 @@
       Auth__DevSeed__UnverifiedEmail: ${AUTH_DEV_SEED_UNVERIFIED_EMAIL:-}
       Auth__DevSeed__UnverifiedPassword: ${AUTH_DEV_SEED_UNVERIFIED_PASSWORD:-}
       Auth__Email__Smtp__Host: mailpit
+      # The product's manifest (spec 0005): its permissions and the default roles of a new company. A product mounts its own.
+      Auth__Manifest__Path: /etc/auth-core/auth.yaml
       Auth__Keys__SigningCertificatePath: /run/secrets/auth/signing.crt
       Auth__Keys__SigningKeyPath: /run/secrets/auth/signing.key
       Auth__Keys__EncryptionCertificatePath: /run/secrets/auth/encryption.crt
       Auth__Keys__EncryptionKeyPath: /run/secrets/auth/encryption.key
     volumes:
       - ../.secrets:/run/secrets/auth:ro
+      - ./auth.yaml:/etc/auth-core/auth.yaml:ro
     # No healthcheck: the chiseled image has no shell or curl. scripts/e2e-login.sh polls /auth/health from the host.
 
 volumes:
```

**The script.** `scripts/e2e-tenancy.sh` has the conventions of the other four (`set -euo pipefail`,
`BASE_URL`, `MAILPIT_URL`, `env_get`, `wait_healthy`, `fail`/`pass`, a `mktemp -d` directory
removed by an `EXIT` trap, `ALL PASS`). It never prints a password, a token, a cookie or a
mail body: request bodies are files, a token goes from the mail straight into one, and the
bearer header and the cookie of a session reach `curl` through header files. The operator
commands run as `docker compose run --rm -T --no-deps auth admin …`. It makes every company
and address for the run, so it can be run again on the same stack. LF line endings; set the
executable bit in the index: `git update-index --chmod=+x scripts/e2e-tenancy.sh` (after
`git add`).

```bash
#!/usr/bin/env bash
# Real-network end-to-end check of spec 0005 (companies, members, roles and invitations) against the compose stack,
# with the mail catcher (Mailpit) in it. It drives the sequence of the spec's Goal — the operator creates a company and
# invites its first admin, who invites a member, whom the admin then removes — and the role and safety cases.
#
# What it checks, in order (ADMIN = the first admin of the new company A, WORKER = a member of A, SEED = the development
# seed user, B = a second company):
#   1. health (Healthy), the mail catcher, the OpenAPI description and the interactive reference (Development only)
#      - criteria 20, 24.
#   2. SEED logs in: its token carries org_id, roles ["admin"] and the expanded permissions; GET /auth/me agrees
#      - criteria 4, 19.
#   3. the operator CLI in the service's own image: create-org prints the id, list-orgs shows it - criterion 1.
#   4. the operator invites ADMIN: the mail arrives at the server's next poll (within a minute), with the link of the
#      invitation screen; preview twice, a weak password -> 400 weak_password (the token stays), accept -> 204, accept again
#      -> 400 invalid_token - criteria 2, 3, 4, 6, 7.
#   5. ADMIN logs in: org_id of A, roles ["admin"], permissions sorted, no "*" - criterion 4.
#   6. ADMIN invites WORKER through the API (202), the mail arrives, WORKER accepts and logs in with roles ["user"].
#   7. roles and safety: WORKER cannot list members (forbidden); nobody changes their own role or removes themselves; a new
#      role; unknown permission; a taken name; a role that holds what the caller lacks; star only by star; nobody
#      removes or re-roles a member, or resends or cancels an invitation, that holds more than they do; a demoted
#      caller's old token gets permissions_changed; an edit that would leave no manager is refused - criteria 12, 14-18.
#   8. B: ids of another company are 404; an invitation to an address of another company is answered exactly as one to an
#      unknown address, and its preview says already_member - criteria 8, 14.
#   9. removal: WORKER's refresh cookie -> 401, login -> 403 no_membership, the old access token -> permissions_changed - 13.
#  10. the CLI removes the last manager only with --force; afterwards ADMIN's sessions are over - criteria 16, 22.
#
# Full sequence, from the repo root (same stack and .env as the other e2e scripts; the four existing ones first,
# this one last - they must still pass with the seed users as members of the development company):
#   cp .env.example .env                  # then set real local values (git-ignored)
#   scripts/dev-keys.sh                   # dev signing/encryption keys into .secrets/ (git-ignored)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   docker compose -f deploy/docker-compose.yml --env-file .env up -d --build    # start postgres, mailpit, auth
#   scripts/e2e-login.sh                  # spec 0001 regression
#   scripts/e2e-refresh.sh                # spec 0002 regression
#   scripts/e2e-lockout.sh                # spec 0003 regression
#   scripts/e2e-email.sh                  # spec 0004 regression
#   scripts/e2e-tenancy.sh                # this script (does NOT bring the stack up or down)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# Reads AUTH_DEV_SEED_EMAIL and AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced). The operator
# commands run as `docker compose run --rm -T --no-deps auth admin ...` (the image has no shell: its entrypoint takes the
# arguments). Needs: curl, python3 (standard library only), docker compose. Env: BASE_URL (default http://localhost:8080),
# MAILPIT_URL (default http://localhost:8025). Takes about two minutes, most of it waiting for the server to pick up the
# invitation the CLI queued. Re-runnable on the same stack: every company and address is made for the run.
# Exits non-zero on the first failure; prints "PASS <step>" per step; never prints a password, a token, a cookie or a mail
# body. Request bodies are built into files in a mktemp -d directory (removed on exit) and curl reads them with
# --data-binary @file; tokens and cookies reach curl through header files.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
compose=(docker compose -f "$root/deploy/docker-compose.yml" --env-file "$root/.env")

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

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

run="$RANDOM$RANDOM"
ADMIN_EMAIL="boss-$run@acme.test"
WORKER_EMAIL="worker-$run@acme.test"
STRANGER_EMAIL="nobody-$run@example.invalid"
BADMIN_EMAIL="boss-$run@globex.test"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
export E2E_ADMIN_EMAIL="$ADMIN_EMAIL" E2E_WORKER_EMAIL="$WORKER_EMAIL" E2E_BADMIN_EMAIL="$BADMIN_EMAIL"
export E2E_PASSWORD="E2e-Passw0rd-$run" E2E_WEAK_PASSWORD="abc"

wait_healthy() {
  local i
  for i in $(seq 1 90); do
    if [[ "$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "$BASE_URL/auth/health" || true)" == "200" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

wait_mailpit() {
  local i
  for i in $(seq 1 60); do
    if [[ "$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "$MAILPIT_URL/readyz" || true)" == "200" ]]; then
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

header_names() { # sorted header names of the last response, Date excluded
  tail -n +2 "$tmp/hdr" | tr -d '\r' | grep ':' | cut -d: -f1 | tr '[:upper:]' '[:lower:]' | { grep -vx 'date' || true; } | sort | tr '\n' ' '
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

json_body() { # json_body <file> <python-expression>: writes the JSON of the expression, which may read os.environ
  python3 -c 'import json,os,sys; print(json.dumps(eval(sys.argv[1])))' "$2" > "$1"
}

expect_status() { # expect_status <what> <code> [exact-body]
  [[ "$HTTP_CODE" == "$2" ]] || fail "$1: HTTP $HTTP_CODE, expected $2"
  if [[ $# -ge 3 ]]; then [[ "$BODY" == "$3" ]] || fail "$1: unexpected body"; fi
}

expect_error() { expect_status "$1" "$2" "{\"error\":\"$3\"}"; }

expect_no_store() { [[ "$(header cache-control)" == *no-store* ]] || fail "$1: no Cache-Control: no-store"; }

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

# refresh <name>: refreshes the session of <name> with its cookie; keeps the new tokens. Sets HTTP_CODE.
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

mail_field() { python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)[sys.argv[1]])' "$1" < "$tmp/mail.json" | tr -d '\r'; }

# --- the operator CLI, in the service's own image ----------------------------------------------------------------

# cli <args...>: runs `auth-server admin <args>` in a one-off container; sets CLI_OUT (stdout), CLI_EXIT, and keeps stderr in $tmp/cli.err
cli() {
  CLI_EXIT=0
  CLI_OUT="$("${compose[@]}" run --rm -T --no-deps auth admin "$@" 2> "$tmp/cli.err")" || CLI_EXIT=$?
  CLI_OUT="${CLI_OUT//$'\r'/}"
}

# --- Step 1: health, the mail catcher, the description ---------------------------------------------------------------
wait_healthy || fail "step 1: $BASE_URL/auth/health did not return 200 within 90s"
wait_mailpit || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s (is the mailpit service up?)"
call GET /auth/health
expect_status "step 1: health" 200 "Healthy"
call GET /auth/openapi/v1.json
expect_status "step 1: the OpenAPI description" 200
for path in /auth/login /auth/org/invites /auth/org/members /auth/org/roles /auth/invites/accept /auth/me /auth/.well-known/jwks.json; do
  [[ "$(val "'$path' in d['paths']")" == "True" ]] || fail "step 1: $path is not in the OpenAPI description"
done
[[ "$(curl -sL -o /dev/null --max-time 10 -w '%{http_code}' "$BASE_URL/auth/scalar")" == "200" ]] || fail "step 1: the interactive reference is not served in Development"
pass "step 1: health is Healthy; the mail catcher answers; the OpenAPI description names the endpoints; the reference is served (Development)"

# --- Step 2: the seed user is the admin of the development company -----------------------------------------------------
login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD
PAYLOAD="$(jwt_payload "$tmp/seed.auth")"
BODY="$PAYLOAD"
expect_eq "step 2: roles" "$(val 'd["roles"]')" "['admin']"
expect_eq "step 2: permissions" "$(val 'd["permissions"]')" "['documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:manage', 'roles:manage']"
call GET /auth/me "" "$tmp/seed.auth"
expect_status "step 2: GET /auth/me" 200
expect_no_store "step 2: GET /auth/me"
expect_eq "step 2: /auth/me email" "$(val 'd["email"]')" "$SEED_EMAIL"
expect_eq "step 2: /auth/me company" "$(val 'd["org_name"]')" "Development"
pass "step 2: the seed user is the admin of the development company; its token and /auth/me say so"

# --- Step 3: the operator creates company A ----------------------------------------------------------------------------
cli create-org --name "E2E Acme $run"
expect_eq "step 3: create-org exit code" "$CLI_EXIT" "0"
[[ "$CLI_OUT" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || fail "step 3: create-org printed more than the company id"
A="$CLI_OUT"
cli list-orgs
expect_eq "step 3: list-orgs exit code" "$CLI_EXIT" "0"
grep -qF "$A"$'\t'"E2E Acme $run"$'\t'"0" <<< "$CLI_OUT" || fail "step 3: list-orgs does not show the new company with 0 members"
pass "step 3: create-org printed the company id; list-orgs shows it with no members"

# --- Step 4: the operator invites ADMIN; the invitation, its preview and its acceptance --------------------------------
cli invite --org "$A" --email "$ADMIN_EMAIL" --role admin
expect_eq "step 4: invite exit code" "$CLI_EXIT" "0"
# The CLI only queues the mail: the server sends it at its next poll, within a minute.
wait_mail "$ADMIN_EMAIL" 1 120 || fail "step 4: no invitation mail within 120s of the CLI invitation"
[[ "$(mail_field Subject)" == *auth-core-dev* ]] || fail "step 4: the subject does not carry the application name"
python3 -c '
import json, sys
mail = json.load(sys.stdin.buffer)
text = mail["Text"]
for needle in ("E2E Acme", "admin", "7 days"):
    if needle not in text:
        sys.exit("the invitation mail does not say: " + needle)
' < "$tmp/mail.json" || fail "step 4: the invitation mail misses its company, role or lifetime"
token_body "$tmp/admin-preview.json" || fail "step 4: no usable invitation link in the mail"
token_body "$tmp/admin-weak.json" E2E_WEAK_PASSWORD
token_body "$tmp/admin-accept.json" E2E_PASSWORD
call POST /auth/invites/preview "$tmp/admin-preview.json"
expect_status "step 4: preview" 200
expect_no_store "step 4: preview"
expect_eq "step 4: preview company" "$(val 'd["org_name"]')" "E2E Acme $run"
expect_eq "step 4: preview address" "$(val 'd["email"]')" "$ADMIN_EMAIL"
expect_eq "step 4: preview role" "$(val 'd["role"]')" "admin"
call POST /auth/invites/preview "$tmp/admin-preview.json"
expect_status "step 4: preview again (it does not use the token up)" 200
call POST /auth/invites/accept "$tmp/admin-weak.json"
expect_status "step 4: accept with a weak password" 400 '{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}'
call POST /auth/invites/accept "$tmp/admin-accept.json"
expect_status "step 4: accept" 204 ""
expect_no_store "step 4: accept"
[[ -z "$(header set-cookie)" ]] || fail "step 4: accept set a cookie"
call POST /auth/invites/accept "$tmp/admin-accept.json"
expect_error "step 4: accept again" 400 invalid_token
call POST /auth/invites/preview "$tmp/admin-preview.json"
expect_error "step 4: preview of a used token" 400 invalid_token
pass "step 4: the CLI invitation arrived (names the company, role and 7 days); preview twice; weak password -> 400 weak_password; accept -> 204; again -> 400 invalid_token"

# --- Step 5: ADMIN logs in ---------------------------------------------------------------------------------------------
login admin E2E_ADMIN_EMAIL E2E_PASSWORD
BODY="$(jwt_payload "$tmp/admin.auth")"
expect_eq "step 5: org_id" "$(val 'd["org_id"]')" "$A"
expect_eq "step 5: roles" "$(val 'd["roles"]')" "['admin']"
expect_eq "step 5: permissions" "$(val 'd["permissions"]')" "['documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:manage', 'roles:manage']"
pass "step 5: ADMIN's token carries org_id of company A, roles [admin] and the expanded, sorted permissions without a star"

# --- Step 6: ADMIN invites WORKER through the API ---------------------------------------------------------------------
call GET /auth/org/roles "" "$tmp/admin.auth"
expect_status "step 6: GET /auth/org/roles" 200
expect_eq "step 6: catalog" "$(val 'd["catalog"]')" "['*', 'documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:manage', 'roles:manage']"
USER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "user"][0]')"
ADMIN_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "admin"][0]')"
export E2E_USER_ROLE="$USER_ROLE" E2E_ADMIN_ROLE="$ADMIN_ROLE"
json_body "$tmp/invite-worker.json" "{'email': os.environ['E2E_WORKER_EMAIL'], 'role_id': os.environ['E2E_USER_ROLE']}"
call POST /auth/org/invites "$tmp/invite-worker.json" "$tmp/admin.auth"
expect_status "step 6: invite WORKER" 202 ""
expect_no_store "step 6: invite WORKER"
wait_mail "$WORKER_EMAIL" 1 30 || fail "step 6: no invitation mail for WORKER within 30s (an API invitation wakes the dispatcher)"
token_body "$tmp/worker-accept.json" E2E_PASSWORD
call POST /auth/invites/accept "$tmp/worker-accept.json"
expect_status "step 6: WORKER accepts" 204 ""
login worker E2E_WORKER_EMAIL E2E_PASSWORD
BODY="$(jwt_payload "$tmp/worker.auth")"
expect_eq "step 6: WORKER roles" "$(val 'd["roles"]')" "['user']"
expect_eq "step 6: WORKER permissions" "$(val 'd["permissions"]')" "['documents:read', 'documents:write']"
call GET /auth/org/members "" "$tmp/admin.auth"
expect_status "step 6: GET /auth/org/members" 200
expect_eq "step 6: members" "$(val '[m["email"] for m in d["members"]]')" "['$ADMIN_EMAIL', '$WORKER_EMAIL']"
WORKER_ID="$(val '[m["user_id"] for m in d["members"] if m["email"] == "'"$WORKER_EMAIL"'"][0]')"
ADMIN_ID="$(val '[m["user_id"] for m in d["members"] if m["email"] == "'"$ADMIN_EMAIL"'"][0]')"
pass "step 6: ADMIN invited WORKER (202, mail within seconds); WORKER accepted and logged in as [user]; the member list shows both"

# --- Step 7: roles and safety rules -------------------------------------------------------------------------------------
call GET /auth/org/members "" "$tmp/worker.auth"
expect_error "step 7: a member without members:manage lists members" 403 forbidden
call GET /auth/org/members
[[ "$HTTP_CODE" == "401" && -z "$BODY" && "$(header www-authenticate)" == Bearer* ]] || fail "step 7: no token must be a 401, empty body, WWW-Authenticate: Bearer"
json_body "$tmp/role-self.json" "{'role_id': os.environ['E2E_USER_ROLE']}"
call PUT "/auth/org/members/$ADMIN_ID/role" "$tmp/role-self.json" "$tmp/admin.auth"
expect_error "step 7: changing one's own role" 409 cannot_change_self
call DELETE "/auth/org/members/$ADMIN_ID" "" "$tmp/admin.auth"
expect_error "step 7: removing oneself" 409 cannot_change_self
json_body "$tmp/role-new.json" "{'name': 'Reviewer', 'permissions': ['documents:read', 'documents:approve']}"
call POST /auth/org/roles "$tmp/role-new.json" "$tmp/admin.auth"
expect_status "step 7: create a role" 201
expect_eq "step 7: the new role's permissions" "$(val 'd["permissions"]')" "['documents:approve', 'documents:read']"
json_body "$tmp/role-unknown.json" "{'name': 'Odd', 'permissions': ['documents:publish']}"
call POST /auth/org/roles "$tmp/role-unknown.json" "$tmp/admin.auth"
expect_error "step 7: a permission outside the catalog" 400 unknown_permission
json_body "$tmp/role-taken.json" "{'name': 'REVIEWER', 'permissions': []}"
call POST /auth/org/roles "$tmp/role-taken.json" "$tmp/admin.auth"
expect_error "step 7: a name that is taken, whatever its case" 409 role_name_taken
json_body "$tmp/role-lead.json" "{'name': 'Lead', 'permissions': ['members:manage', 'documents:read']}"
call POST /auth/org/roles "$tmp/role-lead.json" "$tmp/admin.auth"
expect_status "step 7: create the role Lead" 201
LEAD_ROLE="$(val 'd["id"]')"
export E2E_LEAD_ROLE="$LEAD_ROLE"
json_body "$tmp/role-lead-for-worker.json" "{'role_id': os.environ['E2E_LEAD_ROLE']}"
call PUT "/auth/org/members/$WORKER_ID/role" "$tmp/role-lead-for-worker.json" "$tmp/admin.auth"
expect_status "step 7: give WORKER the role Lead" 204 ""
refresh worker
expect_eq "step 7: WORKER refreshed" "$HTTP_CODE" "200"
BODY="$(jwt_payload "$tmp/worker.auth")"
expect_eq "step 7: the change reached the next refresh" "$(val 'd["permissions"]')" "['documents:read', 'members:manage']"
# WORKER (Lead) holds less than ADMIN (star): they can neither remove ADMIN nor give ADMIN another role.
call DELETE "/auth/org/members/$ADMIN_ID" "" "$tmp/worker.auth"
expect_error "step 7: removing a member who holds more than the caller" 403 permission_not_held
call PUT "/auth/org/members/$ADMIN_ID/role" "$tmp/role-self.json" "$tmp/worker.auth"
expect_error "step 7: changing the role of a member who holds more than the caller" 403 permission_not_held
# WORKER (Lead) may invite, but with nothing they do not hold, and may not touch roles.
json_body "$tmp/invite-star.json" "{'email': 'star-$run@acme.test', 'role_id': os.environ['E2E_ADMIN_ROLE']}"
call POST /auth/org/invites "$tmp/invite-star.json" "$tmp/worker.auth"
expect_error "step 7: a role with star given by a caller whose role lacks it" 403 permission_not_held
# An invitation for the role admin, made by ADMIN: WORKER (Lead) may neither resend nor cancel it; ADMIN may cancel it.
json_body "$tmp/invite-big.json" "{'email': 'big-$run@acme.test', 'role_id': os.environ['E2E_ADMIN_ROLE']}"
call POST /auth/org/invites "$tmp/invite-big.json" "$tmp/admin.auth"
expect_status "step 7: ADMIN invites with the role admin" 202 ""
call GET /auth/org/invites "" "$tmp/admin.auth"
BIG_INVITE="$(val "[i['id'] for i in d['invites'] if i['email'] == 'big-$run@acme.test'][0]")"
call POST "/auth/org/invites/$BIG_INVITE/resend" "" "$tmp/worker.auth"
expect_error "step 7: resending an invitation for a role that holds more than the caller" 403 permission_not_held
call DELETE "/auth/org/invites/$BIG_INVITE" "" "$tmp/worker.auth"
expect_error "step 7: cancelling an invitation for a role that holds more than the caller" 403 permission_not_held
call DELETE "/auth/org/invites/$BIG_INVITE" "" "$tmp/admin.auth"
expect_status "step 7: ADMIN cancels it" 204 ""
json_body "$tmp/role-by-worker.json" "{'name': 'Mine', 'permissions': []}"
call POST /auth/org/roles "$tmp/role-by-worker.json" "$tmp/worker.auth"
expect_error "step 7: Lead cannot manage roles" 403 forbidden
# ADMIN takes members:manage from Lead: WORKER's token still says they may, the database says no.
json_body "$tmp/role-lead-edit.json" "{'name': 'Lead', 'permissions': ['documents:read']}"
call PUT "/auth/org/roles/$LEAD_ROLE" "$tmp/role-lead-edit.json" "$tmp/admin.auth"
expect_status "step 7: ADMIN edits Lead" 204 ""
call GET /auth/org/members "" "$tmp/worker.auth"
expect_error "step 7: the old token of a demoted caller" 403 permissions_changed
refresh worker
expect_eq "step 7: WORKER refreshed after the edit" "$HTTP_CODE" "200"
call GET /auth/org/members "" "$tmp/worker.auth"
expect_error "step 7: the new token of the demoted caller" 403 forbidden
# The only manager is ADMIN, through star: an edit of admin that drops members:manage would leave nobody.
json_body "$tmp/role-admin-edit.json" "{'name': 'admin', 'permissions': ['roles:manage', 'org:manage']}"
call PUT "/auth/org/roles/$ADMIN_ROLE" "$tmp/role-admin-edit.json" "$tmp/admin.auth"
expect_error "step 7: an edit that leaves the company without a manager" 409 last_manager
pass "step 7: roles and the three safety rules hold over the network; a demoted caller gets permissions_changed with the old token and forbidden with the new one"

# --- Step 8: company B and the other company's ids -------------------------------------------------------------------
cli create-org --name "E2E Globex $run"
expect_eq "step 8: create-org B exit code" "$CLI_EXIT" "0"
B="$CLI_OUT"
cli invite --org "$B" --email "$BADMIN_EMAIL" --role admin
expect_eq "step 8: invite B's admin exit code" "$CLI_EXIT" "0"
wait_mail "$BADMIN_EMAIL" 1 120 || fail "step 8: no invitation mail for B's admin within 120s"
token_body "$tmp/badmin-accept.json" E2E_PASSWORD
call POST /auth/invites/accept "$tmp/badmin-accept.json"
expect_status "step 8: B's admin accepts" 204 ""
login badmin E2E_BADMIN_EMAIL E2E_PASSWORD
call GET /auth/org/roles "" "$tmp/badmin.auth"
B_USER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "user"][0]')"
call GET /auth/org/members "" "$tmp/badmin.auth"
B_ADMIN_ID="$(val '[m["user_id"] for m in d["members"] if m["email"] == "'"$BADMIN_EMAIL"'"][0]')"
export E2E_B_USER_ROLE="$B_USER_ROLE"
json_body "$tmp/role-foreign.json" "{'role_id': os.environ['E2E_B_USER_ROLE']}"
call PUT "/auth/org/members/$WORKER_ID/role" "$tmp/role-foreign.json" "$tmp/admin.auth"
expect_error "step 8: a role of another company" 404 not_found
call PUT "/auth/org/members/$B_ADMIN_ID/role" "$tmp/role-lead-for-worker.json" "$tmp/admin.auth"
expect_error "step 8: a member of another company" 404 not_found
call DELETE "/auth/org/members/$B_ADMIN_ID" "" "$tmp/admin.auth"
expect_error "step 8: removing a member of another company" 404 not_found
call DELETE "/auth/org/roles/$B_USER_ROLE" "" "$tmp/admin.auth"
expect_error "step 8: deleting a role of another company" 404 not_found
call PUT "/auth/org/roles/$B_USER_ROLE" "$tmp/role-new.json" "$tmp/admin.auth"
expect_error "step 8: editing a role of another company" 404 not_found
# An invitation to an address of another company is answered exactly as one to an address nobody has.
json_body "$tmp/invite-stranger.json" "{'email': '$STRANGER_EMAIL', 'role_id': os.environ['E2E_USER_ROLE']}"
call POST /auth/org/invites "$tmp/invite-stranger.json" "$tmp/admin.auth"
expect_status "step 8: invite an unknown address" 202 ""
STRANGER_HEADERS="$(header_names)"
json_body "$tmp/invite-badmin.json" "{'email': os.environ['E2E_BADMIN_EMAIL'], 'role_id': os.environ['E2E_USER_ROLE']}"
call POST /auth/org/invites "$tmp/invite-badmin.json" "$tmp/admin.auth"
expect_status "step 8: invite a member of another company" 202 ""
[[ "$(header_names)" == "$STRANGER_HEADERS" ]] || fail "step 8: the headers differ between an unknown address and a member of another company"
wait_mail "$BADMIN_EMAIL" 2 30 || fail "step 8: no second invitation mail for B's admin"
token_body "$tmp/badmin-second.json" E2E_PASSWORD
call POST /auth/invites/preview "$tmp/badmin-second.json"
expect_error "step 8: the preview tells the invited person" 409 already_member
call POST /auth/invites/accept "$tmp/badmin-second.json"
expect_error "step 8: the acceptance too" 409 already_member
pass "step 8: ids of another company are 404; an invitation to a member of another company is answered like one to an unknown address, and only the acceptance screen says already_member"

# --- Step 9: removing a member ---------------------------------------------------------------------------------------
call DELETE "/auth/org/members/$WORKER_ID" "" "$tmp/admin.auth"
expect_status "step 9: remove WORKER" 204 ""
refresh worker
expect_error "step 9: WORKER's refresh cookie" 401 invalid_grant
[[ -z "$(header set-cookie)" ]] || fail "step 9: a failed refresh wrote the cookie"
json_body "$tmp/worker.login2" "{'email': os.environ['E2E_WORKER_EMAIL'], 'password': os.environ['E2E_PASSWORD']}"
call POST /auth/login "$tmp/worker.login2"
expect_error "step 9: WORKER logs in" 403 no_membership
expect_no_store "step 9: no_membership"
[[ -z "$(header set-cookie)" ]] || fail "step 9: no_membership set a cookie"
call GET /auth/org "" "$tmp/worker.auth"
expect_error "step 9: WORKER's old access token on the company API" 403 permissions_changed
pass "step 9: the removed member's refresh -> 401 invalid_grant, login -> 403 no_membership, old access token -> permissions_changed"

# --- Step 10: the CLI removes the last manager only with --force -------------------------------------------------------
cli remove-member --org "$A" --email "$ADMIN_EMAIL"
expect_eq "step 10: remove-member exit code" "$CLI_EXIT" "1"
grep -qF "error: last_manager" "$tmp/cli.err" || fail "step 10: the refusal does not name last_manager"
call GET /auth/org "" "$tmp/admin.auth"
expect_status "step 10: ADMIN is still a member" 200
cli remove-member --org "$A" --email "$ADMIN_EMAIL" --force
expect_eq "step 10: remove-member --force exit code" "$CLI_EXIT" "0"
refresh admin
expect_error "step 10: ADMIN's refresh cookie" 401 invalid_grant
call POST /auth/login "$tmp/admin.login"
expect_error "step 10: ADMIN logs in" 403 no_membership
cli list-orgs
grep -qF "$A"$'\t'"E2E Acme $run"$'\t'"0" <<< "$CLI_OUT" || fail "step 10: company A should be left with no members"
pass "step 10: remove-member refused the last manager (last_manager) and removed it with --force; ADMIN's sessions are over"

echo "ALL PASS"
```

**The acceptance map and the documents.**

```markdown
# Spec 0005 — acceptance map

Maps each acceptance criterion of
[spec 0005](../specs/0005-tenancy-and-rbac.md) to the tests that guard it,
for verifier layer 2 ([`docs/workflow.md`](../../workflow.md#verification)).
Integration tests live in `tests/Auth.IntegrationTests/`; e2e steps are in
`scripts/e2e-tenancy.sh` and run against the compose stack (PostgreSQL, the Mailpit mail catcher and
the auth service) over real HTTP, real SMTP and the operator CLI of the service's own image.

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | `create-org` makes a company with a copy of the active default roles and prints its id; `list-orgs` shows it | `AdminCliTests.Create_org_prints_the_id_and_makes_a_company_with_a_copy_of_the_default_roles`, `AdminCliTests.List_orgs_shows_the_companies_with_their_members_sorted_by_name`, `AdminArgumentsTests.Create_org_takes_a_name`, `CompanyServiceTests.Company_starts_with_a_copy_of_the_active_default_roles`, `CompanyServiceTests.A_default_role_that_lists_star_is_copied_as_star_alone`, `CompanyServiceTests.Later_changes_to_the_defaults_do_not_touch_an_existing_company`; e2e step 3 |
| 2 | An invitation by the operator or a manager mails the address, in the configured language, naming application, company and role, with the configured URL plus `token`, stating 7 days | `InvitationMailComposerTests.Invitation_in_english_names_the_application_the_company_the_role_the_link_and_the_lifetime`, `InvitationMailComposerTests.Invitation_in_polish_keeps_its_letters`, `InvitationDispatchTests.Invitation_for_an_address_without_an_account_is_mailed_and_not_dropped`, `InvitationDispatchTests.Only_the_hash_of_the_token_is_stored_and_the_seven_days_start_at_the_mail`, `OrgInviteTests.Manager_invites_an_address_and_a_mail_follows`, `OrgInviteTests.The_operator_is_exempt_from_rule_1_and_is_recorded_as_no_member`, `AdminCliTests.Invite_queues_a_mail_for_the_first_admin_and_the_dispatcher_sends_it`, `MailSettingsTests.Missing_or_blank_value_is_refused_naming_the_key`; e2e steps 4, 6 |
| 3 | `preview` returns company, address and role and leaves the token usable | `InviteAcceptTests.Preview_leaves_the_token_usable`, `InviteAcceptTests.Preview_shows_the_names_as_they_are_now`, `InviteAcceptTests.A_manager_invites_the_person_accepts_and_logs_in_with_the_company_and_the_role`; e2e step 4 |
| 4 | `accept` → `204`: the account exists, its email is confirmed, it is a member with the role, and login returns `org_id`, `roles`, `permissions` (`*` expanded, sorted, no duplicates) | `InviteAcceptTests.Accepting_for_an_address_without_an_account_creates_a_confirmed_member`, `InviteAcceptTests.A_role_with_star_gives_every_permission_of_the_catalog_sorted_without_star`, `InviteAcceptTests.Accept_signs_nobody_in`, `InviteAcceptTests.An_address_with_letters_beyond_ascii_gets_an_account`, `TenantClaimsTests.Login_token_names_the_company_the_role_and_every_permission_of_the_catalog`, `TenantClaimsTests.Role_and_permission_lists_are_arrays_even_with_one_entry`, `TenantClaimsTests.Permissions_are_sorted_ordinally_without_duplicates_and_without_names_that_left_the_catalog`, `PermissionCatalogTests.Star_expands_to_the_whole_catalog_sorted_without_star`, `LoginTests.Token_carries_contract_claims_and_the_tenancy_claims`; e2e steps 4–5 |
| 5 | Accepting for an account without a company replaces its password and ends every session it had | `InviteAcceptTests.Accepting_for_an_account_without_a_company_replaces_its_password_and_ends_every_session`, `InviteAcceptTests.Accepting_confirms_an_unconfirmed_account_lifts_a_lockout_and_removes_its_other_links` |
| 6 | A lesser manager acts on something that holds more than they do: pushes out or demotes an admin, edits or deletes an admin's role, resends or cancels an invitation for it: refused with `permission_not_held`, whatever role it is given, while the operator may | `OrgMemberTests.Nobody_removes_a_member_who_holds_more_than_they_do`, `OrgMemberTests.Nobody_changes_the_role_of_a_member_who_holds_more_than_they_do_not_even_to_a_lesser_role`, `OrgMemberTests.The_operator_may_touch_any_member`, `OrgRoleTests.Nobody_edits_or_deletes_a_role_that_holds_more_than_they_do_even_to_shrink_it`, `OrgInviteTests.Nobody_resends_or_cancels_an_invitation_whose_role_holds_more_than_they_do`, `OrgInviteTests.The_operator_resends_and_cancels_any_invitation` |
| 7 | A password that breaks the policy → `400 weak_password`, nothing changes, the token stays usable | `InviteAcceptTests.Weak_password_names_the_broken_rules_changes_nothing_and_leaves_the_token_usable`, `InviteAcceptTests.Accept_without_a_password_or_with_a_nul_in_it_is_a_400_invalid_request`; e2e step 4 |
| 8 | An address of a member of another company: the invitation is answered exactly as for an unknown address; `preview` and `accept` say `409 already_member` and leave the invitation usable | `OrgInviteTests.Answer_is_the_same_for_every_address_outside_the_company`, `InviteAcceptTests.For_a_member_of_another_company_preview_and_accept_say_already_member_and_the_invitation_stays`, `InviteAcceptTests.Accepting_one_invitation_leaves_the_others_of_the_address_as_they_are_and_they_then_say_already_member`, `InviteAcceptTests.Two_invitations_of_two_companies_accepted_at_once_make_one_membership`, `InviteAcceptTests.A_weak_password_for_an_address_that_is_already_a_member_elsewhere_is_already_member`; e2e step 8 |
| 9 | Inviting a member of one's own company is `409 already_in_org`; an address with a pending invitation is `409 invite_pending` | `OrgInviteTests.Inviting_a_member_of_the_own_company_is_already_in_org_whatever_the_spelling`, `OrgInviteTests.Inviting_an_address_with_a_pending_invitation_is_invite_pending`, `OrgInviteTests.Already_in_org_is_answered_before_the_pending_check_and_the_mail_limit`, `OrgInviteTests.Expired_invitation_is_not_pending_and_is_replaced`, `OrgInviteTests.Parallel_invitations_of_one_address_leave_one_invitation_and_one_mail` |
| 10 | Resend mails a new link and the earlier one is `invalid_token`; cancel kills the link and removes the invitation from the list; sending and resending are subject to the mail limit per company and address (`429`), and one company's limit does not touch another's | `OrgInviteTests.Resend_queues_a_new_mail_and_the_new_link_replaces_the_old`, `InvitationDispatchTests.A_new_mail_replaces_the_token_of_the_earlier_one_and_restarts_the_seven_days`, `InviteAcceptTests.A_resent_invitation_works_through_the_new_link_only`, `OrgInviteTests.Cancel_removes_the_invitation_and_drops_its_queued_mail`, `InviteAcceptTests.A_cancelled_invitation_cannot_be_previewed_or_accepted_and_is_not_listed`, `OrgInviteTests.Inviting_again_within_a_minute_of_cancelling_is_refused_with_the_time_left`, `OrgInviteTests.Sixth_mail_within_the_hour_is_refused_until_the_hour_is_over`, `OrgInviteTests.One_company_cannot_block_or_observe_the_invitations_of_another_to_the_same_person`, `AdminCliTests.Invitations_from_the_command_line_go_through_the_mail_limit` (cancelling itself is not limited: see the plan's open questions) |
| 11 | A correct password by a confirmed user without a company → `403 no_membership`, no token, no cookie, the streak ends; a wrong password is the `401` of spec 0001 | `TenantClaimsTests.Account_without_a_company_is_refused_with_no_token_and_no_cookie`, `TenantClaimsTests.Wrong_password_for_an_account_without_a_company_is_the_ordinary_401`, `TenantClaimsTests.The_refusal_ends_the_streak_as_a_success_would`, `TenantClaimsTests.The_order_is_password_then_confirmed_email_then_membership`; e2e step 9 |
| 12 | After a role change or a role edit the next refresh carries the new `roles` and `permissions` and no session is ended | `TenantClaimsTests.Next_refresh_carries_a_changed_role_and_ends_no_session`, `TenantClaimsTests.Next_refresh_carries_an_edited_role_and_ends_no_session`, `OrgMemberTests.The_change_ends_no_session_and_reaches_the_members_next_refresh`, `OrgRoleTests.The_edit_of_a_role_reaches_its_members_at_the_next_refresh_and_ends_no_session`; e2e step 7 |
| 13 | After a removal every earlier refresh token is the `401` of spec 0002, login is `403 no_membership`, and an invitation to another company can make the person a member again | `OrgMemberTests.Removing_a_member_ends_the_membership_and_every_session_and_keeps_the_account`, `OrgMemberTests.A_removed_member_can_be_invited_again_but_the_old_sessions_stay_ended`, `OrgMemberTests.A_removed_members_access_token_is_refused_by_the_company_api_at_once`, `TenantClaimsTests.Refresh_of_someone_who_is_no_longer_a_member_is_the_401_of_spec_0002`; e2e step 9 |
| 14 | Every company API endpoint: `401` without a valid token; `403 forbidden` when neither token nor database grants; `403 permissions_changed` when the token claims what the database no longer grants; never another company's data (`404`) | `BearerValidationTests` (all), `OrgEndpointTests.Every_endpoint_answers_401_without_a_valid_token`, `OrgEndpointTests.A_demoted_caller_with_an_old_token_gets_permissions_changed_and_with_a_new_one_forbidden`, `OrgEndpointTests.A_promoted_caller_may_use_the_new_permission_before_refreshing`, `OrgEndpointTests.A_removed_member_with_a_live_token_gets_permissions_changed_on_every_endpoint`, `OrgInviteTests.Every_endpoint_needs_a_token_and_members_manage_checked_against_the_database`, `OrgMemberTests.Every_endpoint_needs_a_token_and_members_manage_checked_against_the_database`, `OrgRoleTests.Every_endpoint_needs_a_token_and_the_permission_checked_against_the_database`, `OrgInviteTests.Role_must_belong_to_the_callers_company`, `OrgInviteTests.List_holds_nothing_of_another_company`, `OrgInviteTests.Resend_of_an_invitation_that_is_unknown_expired_or_of_another_company_is_404`, `OrgInviteTests.Cancel_of_an_invitation_that_is_unknown_gone_or_of_another_company_is_404`, `OrgMemberTests.A_user_or_role_outside_the_callers_company_is_a_404_like_one_that_does_not_exist`, `OrgMemberTests.Removing_someone_outside_the_company_or_unknown_is_a_404`, `OrgMemberTests.List_holds_no_member_of_another_company`, `OrgRoleTests.A_role_of_another_company_or_that_does_not_exist_is_a_404`, `OrgRoleTests.List_holds_no_role_of_another_company`, `TenancyTablesTests.Membership_cannot_name_a_role_of_another_company`, `TenancyTablesTests.Invite_cannot_name_a_role_of_another_company`; e2e steps 7–9 |
| 15 | Safety rule 1, one rule: nobody acts on anything that holds more than they do. `403 permission_not_held` when the caller lacks a permission of the role they give, invite with, create or set on a role, or of the role they edit, delete, resend or cancel an invitation for, or of the role a member they remove or re-role holds now; `*` only by a caller whose role holds `*` (the operator is exempt) | `OrgInviteTests.Nobody_invites_with_a_role_that_holds_a_permission_they_lack`, `OrgInviteTests.A_role_with_star_needs_a_caller_whose_role_holds_star_even_if_it_lists_everything`, `OrgInviteTests.An_admin_with_star_may_invite_with_star`, `OrgInviteTests.Nobody_resends_or_cancels_an_invitation_whose_role_holds_more_than_they_do`, `OrgInviteTests.A_caller_resends_and_cancels_invitations_whose_role_holds_what_they_hold_or_less`, `OrgInviteTests.The_operator_resends_and_cancels_any_invitation`, `OrgMemberTests.Nobody_gives_a_role_that_holds_a_permission_they_lack`, `OrgMemberTests.A_caller_that_lists_every_permission_but_not_star_cannot_give_a_role_with_star`, `OrgMemberTests.Nobody_removes_a_member_who_holds_more_than_they_do`, `OrgMemberTests.Nobody_changes_the_role_of_a_member_who_holds_more_than_they_do_not_even_to_a_lesser_role`, `OrgMemberTests.A_caller_that_lists_every_permission_but_not_star_cannot_touch_a_member_with_star`, `OrgMemberTests.A_member_whose_role_is_a_subset_of_the_callers_may_be_removed_and_re_roled`, `OrgMemberTests.The_operator_may_touch_any_member`, `OrgRoleTests.Nobody_creates_a_role_that_holds_a_permission_they_lack`, `OrgRoleTests.A_caller_that_lists_every_permission_but_not_star_cannot_create_a_role_with_star`, `OrgRoleTests.Nobody_edits_a_role_so_that_it_holds_what_they_lack_and_nobody_without_star_touches_a_star_role`, `OrgRoleTests.Nobody_edits_or_deletes_a_role_that_holds_more_than_they_do_even_to_shrink_it`, `OrgRoleTests.A_caller_edits_and_deletes_roles_that_hold_what_they_hold_or_less`, `OrgRoleTests.A_holder_of_star_edits_and_deletes_a_role_with_star_and_a_list_of_everything_is_not_star`; e2e step 7 |
| 16 | Safety rule 2: a role change, a removal and a role edit that would leave no manager are `409 last_manager`; two managers removing each other at once leave one | `OrgRoleTests.An_edit_that_would_leave_the_company_without_a_manager_is_refused`, `OrgRoleTests.An_edit_that_takes_members_manage_from_a_role_is_fine_while_another_manager_is_left`, `OrgMemberTests.The_operator_cannot_remove_the_last_manager_unless_forced`, `OrgMemberTests.A_role_change_that_would_leave_no_manager_is_refused`, `OrgMemberTests.A_company_without_a_manager_is_not_stopped_from_changing_a_plain_member`, `OrgMemberTests.Two_managers_removing_each_other_at_once_leave_one_manager`, `OrgMemberTests.A_request_that_was_authorised_before_the_caller_was_removed_is_refused_under_the_lock`, `OrgMemberTests.A_caller_demoted_before_the_lock_cannot_change_a_role_or_invite`; e2e steps 7, 10 |
| 17 | Safety rule 3: changing one's own role or removing oneself is `409 cannot_change_self` | `OrgMemberTests.Nobody_changes_their_own_role`, `OrgMemberTests.Nobody_removes_themselves`; e2e step 7 |
| 18 | Roles: create, rename, change permissions and delete within the catalog; `role_name_taken`, `unknown_permission`, `role_in_use` as specified | `OrgRoleTests.Admin_creates_a_role_from_permissions_of_the_catalog`, `OrgRoleTests.A_role_may_hold_nothing_and_star_stands_alone`, `OrgRoleTests.Replace_changes_the_name_and_the_permissions_as_a_whole`, `OrgRoleTests.A_role_can_be_renamed_to_another_spelling_of_its_own_name`, `OrgRoleTests.A_role_nobody_holds_is_deleted`, `OrgRoleTests.A_name_the_company_has_is_taken_whatever_its_case_and_free_in_another_company`, `OrgRoleTests.A_name_another_role_has_is_taken`, `OrgRoleTests.A_permission_outside_the_catalog_is_unknown_permission`, `OrgRoleTests.A_replace_with_a_permission_outside_the_catalog_is_unknown_permission`, `OrgRoleTests.A_role_held_by_a_member_cannot_be_deleted`, `OrgRoleTests.A_role_of_a_pending_invitation_cannot_be_deleted_and_of_an_expired_one_can`, `OrgRoleTests.Parallel_creations_of_one_name_leave_one_role`, `OrgRoleTests.List_shows_the_roles_sorted_by_name_with_their_permissions_member_counts_and_the_catalog`; e2e step 7 |
| 19 | `GET /auth/me` and `GET /auth/org` return the caller's current data; `PATCH /auth/org` renames for `org:manage` | `OrgEndpointTests.Me_returns_the_callers_current_data_from_the_database`, `OrgEndpointTests.Me_follows_the_database_not_the_token`, `OrgEndpointTests.Org_returns_the_callers_company_to_any_member`, `OrgEndpointTests.Org_shows_the_company_of_the_caller_and_not_another`, `OrgEndpointTests.Rename_changes_the_name_for_org_manage_and_only_that_company`, `OrgEndpointTests.Rename_without_org_manage_is_forbidden`; e2e step 2 |
| 20 | A valid manifest becomes active and is stored; a missing, unreadable or invalid one leaves the last stored valid one active, logs the reason and makes `/auth/health` `Degraded`; the host starts either way; with nothing stored: built-in permissions and `admin: ["*"]` | `ManifestParserTests` (all), `ManifestActivationTests.Valid_manifest_becomes_active_and_is_stored`, `ManifestActivationTests.Invalid_manifest_leaves_the_last_stored_one_active_and_says_why`, `ManifestActivationTests.A_manifest_with_more_than_100_default_roles_leaves_the_last_stored_one_active`, `ManifestActivationTests.Missing_manifest_file_leaves_the_last_stored_one_active`, `ManifestActivationTests.Unreadable_manifest_path_leaves_the_last_stored_one_active`, `ManifestActivationTests.With_nothing_ever_stored_the_catalog_is_the_built_in_one_and_the_only_default_role_is_admin`, `ManifestActivationTests.Shipped_development_manifest_is_valid`, `CompanyServiceTests.With_the_built_in_manifest_a_company_gets_one_admin_role_holding_everything`, `AdminCliTests.A_broken_manifest_does_not_stop_the_command_and_the_stored_one_is_used`; e2e step 1 (`Healthy`) |
| 21 | A permission removed from the manifest is gone from tokens at the next refresh; one added appears in the tokens of every role holding `*` | `TenantClaimsTests.A_permission_that_leaves_the_catalog_is_gone_from_the_next_refresh_and_a_new_one_reaches_star`, `PermissionCatalogTests.A_permission_that_left_the_catalog_grants_nothing_and_is_not_shown`, `ManifestActivationTests.A_new_valid_manifest_replaces_the_stored_one_at_the_next_start`, `OrgRoleTests.The_catalog_follows_the_manifest`, `OrgRoleTests.List_shows_only_permissions_that_are_still_in_the_catalog`, `OrgRoleTests.An_edit_drops_the_permissions_that_left_the_catalog` |
| 22 | `remove-member` removes a member as the API does; refused with `last_manager` unless `--force` | `AdminCliTests.Remove_member_removes_the_member_and_ends_every_session`, `AdminCliTests.Removing_the_last_manager_is_refused_unless_forced`, `AdminCliTests.Remove_member_refuses_someone_who_is_not_a_member_of_that_company`, `AdminArgumentsTests.Remove_member_takes_an_org_an_email_and_optionally_force`; e2e step 10 |
| 23 | No invitation token in the database in clear or in the logs; company and role names are HTML-encoded in the mail | `InvitationDispatchTests.Only_the_hash_of_the_token_is_stored_and_the_seven_days_start_at_the_mail`, `InvitationDispatchTests.No_token_and_no_address_reach_the_log`, `InviteAcceptTests.No_token_password_or_address_reaches_the_log`, `InvitationMailComposerTests.Company_and_role_names_are_encoded_in_the_html_part_and_never_in_a_header`, `InvitationDispatchTests.Names_with_markup_are_encoded_in_the_mail_and_stay_out_of_the_headers` |
| 24 | `GET /auth/openapi/v1.json` describes every endpoint of specs 0001–0005 with its request, its responses and its error codes | `OpenApiTests` (all); e2e step 1 |
| 25 | The e2e script drives the Goal sequence and the role and safety cases over the real network against the compose stack, reading the mails from the mail catcher; the four existing scripts still pass against the same stack | `scripts/e2e-tenancy.sh`; `scripts/e2e-login.sh`, `scripts/e2e-refresh.sh`, `scripts/e2e-lockout.sh`, `scripts/e2e-email.sh` unedited |

Criteria 5, 7, 9, 10 (the limit), 12 (the edit), 15, 16 (the race) and 21 need a controlled clock, a
direct change of the database, a second company or parallel requests, so integration tests carry them; the
e2e script covers what needs a real network: the operator CLI in the image without a shell, SMTP to a real mail
server, the links in the mails as delivered, a refresh cookie refused after a removal, and the answer to an
invitation for an address of another company compared over the wire. It takes about two minutes, most of it
waiting for the server to pick up an invitation the CLI queued, and can be run again on the same stack: every
company and address is made for the run.

## Review Focus (plan 0005)

| # | Failure mode | Guarding test(s) |
| - | ------------ | ---------------- |
| 1 | Two requests change one company at the same instant (two managers removing each other; two acceptances of one address; one role name created twice): exactly one wins, the company keeps a manager, never a `500` | `OrgMemberTests.Two_managers_removing_each_other_at_once_leave_one_manager`, `OrgMemberTests.A_request_that_was_authorised_before_the_caller_was_removed_is_refused_under_the_lock`, `InviteAcceptTests.Two_invitations_of_two_companies_accepted_at_once_make_one_membership`, `OrgRoleTests.Parallel_creations_of_one_name_leave_one_role`, `OrgInviteTests.Parallel_invitations_of_one_address_leave_one_invitation_and_one_mail` |
| 2 | An address in another spelling (`WORKER@ACME.TEST`, letters beyond ASCII) is the same address for the one-invitation rule and the account is made with the address as typed | `OrgInviteTests.Inviting_an_address_with_a_pending_invitation_is_invite_pending`, `OrgInviteTests.Invitation_records_the_inviter_the_address_as_typed_and_the_role`, `InviteAcceptTests.An_address_with_letters_beyond_ascii_gets_an_account` |
| 3 | Company and role names with markup or format placeholders (`Tom & <Jerry>`, `{0}`) never break a mail, are encoded in its HTML and are not in a header | `InvitationMailComposerTests.Company_and_role_names_are_encoded_in_the_html_part_and_never_in_a_header`, `InvitationMailComposerTests.A_name_that_looks_like_a_format_placeholder_is_printed_as_it_is`, `InvitationDispatchTests.Names_with_markup_are_encoded_in_the_mail_and_stay_out_of_the_headers` |
| 4 | The mail server is down while invitations go out: the request still answers `202`, the earlier link keeps working until a new mail has really left, nothing is lost | `InvitationDispatchTests.Failed_send_is_retried_on_the_schedule_and_the_earlier_link_stays_in_force`, `OrgInviteTests.Manager_invites_an_address_and_a_mail_follows`, `InvitationDispatchTests.Request_for_an_invitation_that_was_cancelled_is_dropped_without_a_mail` |
| 5 | A manifest that breaks, or drops a permission a role still holds: the service starts, health says `Degraded`, no token carries a name outside the catalog, an edit drops it | `ManifestActivationTests.Invalid_manifest_leaves_the_last_stored_one_active_and_says_why`, `TenantClaimsTests.Permissions_are_sorted_ordinally_without_duplicates_and_without_names_that_left_the_catalog`, `OrgRoleTests.An_edit_drops_the_permissions_that_left_the_catalog` |
| 6 | A lesser manager pushes out or demotes an admin: refused with `permission_not_held`, whatever role it is given, while the operator may | `OrgMemberTests.Nobody_removes_a_member_who_holds_more_than_they_do`, `OrgMemberTests.Nobody_changes_the_role_of_a_member_who_holds_more_than_they_do_not_even_to_a_lesser_role`, `OrgMemberTests.The_operator_may_touch_any_member` |

## Contract sentences that are not acceptance criteria

| Sentence | Guarding test |
| -------- | ------------- |
| A method a path does not have is the framework's `405`, and a path it does not have is the framework's empty `404`, still never stored | `OrgEndpointTests.A_method_the_path_does_not_have_is_a_405_that_is_still_never_stored`, `OrgEndpointTests.An_unknown_path_under_org_is_the_frameworks_empty_404_and_is_still_never_stored` |
| The order of checks: authorisation, the shape of the request, existence, rule 3, rule 1, conflicts, the mail limit, rule 2 | `OrgEndpointTests.Authorisation_comes_before_the_shape_of_the_request`, `OrgRoleTests.Missing_permission_is_answered_before_a_bad_body_on_roles_too`, `OrgRoleTests.An_unknown_permission_is_answered_before_a_role_that_does_not_exist`, `OrgMemberTests.Rule_3_is_answered_before_rule_1_when_the_role_asked_for_is_their_own_change_and_too_big`, `OrgInviteTests.Already_in_org_is_answered_before_the_pending_check_and_the_mail_limit`, `InviteAcceptTests.A_weak_password_for_an_address_that_is_already_a_member_elsewhere_is_already_member` |
| The second development seed user gets the first default role of the manifest, in its order, that does not manage members | `DevCompanySeedTests.Second_seed_user_gets_the_first_default_role_in_the_order_of_the_manifest_that_does_not_manage_members` |
| Every response carries `Cache-Control: no-store` and `Pragma: no-cache`; none sets the refresh cookie | `OrgEndpointTests.Answers_never_set_the_refresh_cookie_and_are_never_stored`, `OrgInviteTests.Responses_are_never_stored_and_set_no_cookie`, `OrgMemberTests.Responses_are_never_stored_and_set_no_cookie`, `InviteAcceptTests.Answers_never_set_a_cookie_and_are_never_stored` |
| A missing or invalid token is `401` with an empty body and `WWW-Authenticate: Bearer` (with `error="invalid_token"` for a token that is wrong) | `BearerValidationTests.Request_without_a_token_is_a_401_with_an_empty_body_and_a_bearer_challenge`, `BearerValidationTests.The_challenge_of_a_bad_token_names_the_bearer_scheme_and_the_error_of_rfc_6750` |
| A token for another audience is refused | `BearerValidationTests.A_token_for_another_audience_is_a_401` |
| An id in a path or a body that is not a UUID is `400 invalid_request` | `OrgInviteTests.An_id_that_is_not_a_uuid_is_a_400`, `OrgMemberTests.A_malformed_id_or_body_is_a_400`, `OrgRoleTests.A_path_that_is_not_a_uuid_is_a_400` |
| A company or role name over 100 characters, with a control character or with white space at an end is `400` | `NameInputTests`, `OrgEndpointTests.Rename_to_a_name_that_breaks_the_rules_is_a_400`, `OrgRoleTests.A_name_that_breaks_the_rules_is_a_400` |
| The CLI does not migrate the database and never prints a connection string | `AdminCliTests.The_command_does_not_migrate_the_database`, `AdminCliTests.A_connection_string_never_reaches_the_output` |
| The development seed users join the development company; the second stays unconfirmed | `DevCompanySeedTests` (all) |
| Expired invitations are removed within a day | `InvitePruningTests` (all) |

## Plan-vs-implementation notes

(Filled in by the orchestrator after implementation: every name or behaviour that differed from the plan, per task.)

## Local verification log

(Filled in by the orchestrator after the three verifiers have run.)
```

The README and the design brief:

`README.md` — the change:

````diff
--- a/README.md
+++ b/README.md
@@ -15,6 +15,10 @@
 > ([spec 0003](docs/superpowers/specs/0003-lockout-and-abuse-resistance.md)).
 > Password reset and email verification by mail are in
 > ([spec 0004](docs/superpowers/specs/0004-email-flows.md)).
+> Companies, members, roles and invitations are in too: the access token carries `org_id`, `roles`
+> and `permissions`, the company API manages them, an operator CLI creates companies, and the whole
+> API is described in OpenAPI
+> ([spec 0005](docs/superpowers/specs/0005-tenancy-and-rbac.md)).
 > Implementation follows the milestones in [`docs/design.md`](docs/design.md).
 
 ## Quickstart (development)
@@ -30,10 +34,29 @@
 scripts/e2e-refresh.sh          # refresh → rotation → reuse detection → logout (~30 s)
 scripts/e2e-lockout.sh          # lockout → cooldown → timing medians (~2.5 min)
 scripts/e2e-email.sh            # verification → reset → sessions end → mail outage (~1 min)
+scripts/e2e-tenancy.sh          # CLI → invitations → roles and safety rules → removal (~2 min)
 docker compose -f deploy/docker-compose.yml --env-file .env down -v
 ```
 
 The stack includes a mail catcher; its inbox is at `http://localhost:8025`.
+
+The operator's commands are subcommands of the service's own binary, so they run from its image. With the
+stack up, create a company and invite its first admin (the invitation is mailed at the server's next poll,
+within a minute, and only while the server is running):
+
+```bash
+auth() { docker compose -f deploy/docker-compose.yml --env-file .env run --rm -T --no-deps auth admin "$@"; }
+auth create-org --name "Acme"                                    # prints the company id
+auth invite --org <id> --email boss@acme.test --role admin
+auth list-orgs                                                   # id, name, number of members
+auth remove-member --org <id> --email worker@acme.test [--force] # --force overrides last_manager
+```
+
+The commands work on the database with the service's configuration and never migrate it. The API is
+described at `http://localhost:8080/auth/openapi/v1.json`; in Development an interactive reference is at
+`http://localhost:8080/auth/scalar`. The product's permissions and default roles come from its manifest
+(`Auth:Manifest:Path`; `deploy/auth.yaml` in development): a broken file never stops the service, and
+`/auth/health` then says `Degraded`.
 
 Tests (integration, Postgres via Testcontainers — Docker must be running):
 
@@ -68,7 +91,7 @@
 
 .NET 10 LTS · C# 14 · ASP.NET Core Minimal APIs · ASP.NET Core Identity ·
 OpenIddict 7 · EF Core 10 · PostgreSQL 16 · MailKit · YamlDotNet ·
-System.CommandLine · xUnit v3 + Testcontainers.
+Microsoft.AspNetCore.OpenApi · Scalar.AspNetCore (Development only) · xUnit v3 + Testcontainers.
 
 ## Repository layout (planned)
 
````

`docs/design.md` — the change:

```diff
--- a/docs/design.md
+++ b/docs/design.md
@@ -57,7 +57,7 @@
 | Rate limiting       | Built-in `Microsoft.AspNetCore.RateLimiting`                  | Per-IP and per-account limits on login, forgot and reset        |
 | Email               | `MailKit`; Mailpit in dev and tests                          | Microsoft's recommended SMTP client; templates as `.resx` for PL/EN |
 | Manifest            | `YamlDotNet`                                                   | Parses `auth.yaml`                                              |
-| Admin CLI           | `System.CommandLine`, subcommands of the same binary          | One image: `auth-server admin create-org ...`                   |
+| Admin CLI           | Four hand-parsed subcommands of the same binary (no library; spec 0005) | One image: `auth-server admin create-org ...`        |
 | API docs            | Built-in `Microsoft.AspNetCore.OpenApi` + `Scalar.AspNetCore` (dev only) | OpenAPI document for typed clients later                        |
 | Logging and health  | Built-in `ILogger` (JSON console), built-in health checks     | OpenTelemetry comes after the MVP                              |
 | Container           | `mcr.microsoft.com/dotnet/aspnet:10.0` chiseled image, non-root | Small image, no shell, smaller attack surface                  |
```

- [ ] **Step 1: Write** the compose change, the script, the acceptance map and the document
  changes. Keep every test name in the map in sync with the code (`grep` each one: the map is
  a table of `Class.Method` names, and a script that splits them and greps the class file for
  the method is worth ten minutes).
- [ ] **Step 2: Preflight, then run the e2e on a clean stack.** Two checks first.
  `python3 -c "import jwt"` must succeed: `e2e-login.sh` and `e2e-refresh.sh` need
  `PyJWT[crypto]` (and a `python3` that works; the Microsoft Store shim does not). If the
  machine has none, the repository's local option is the venv shim at
  `.superpowers/sdd/0004-email-flows/bin/` of the main checkout: put that directory first
  on `PATH` for the e2e runs (in Git Bash as `/c/…/bin`: the `C:/…` form is not found).
  And host ports 8080 and 8025 must be free (`netstat -ano | grep -E ':(8080|8025) .*LISTENING'`
  prints nothing; sockets in `TIME_WAIT` do not count); another session may hold them. Then, with a
  compose project name of its own:
  `export COMPOSE_PROJECT_NAME=auth-core-e2e-0005` →
  `cp .env.example .env` (then `POSTGRES_PASSWORD` and `AUTH_DEV_SEED_PASSWORD` to local
  values) → `scripts/dev-keys.sh` → `docker compose -f deploy/docker-compose.yml --env-file .env down -v` →
  `… up -d --build` → `scripts/e2e-login.sh` → `scripts/e2e-refresh.sh` →
  `scripts/e2e-lockout.sh` → `scripts/e2e-email.sh` → `scripts/e2e-tenancy.sh` → `… down -v`.
  Expected: all five end with `ALL PASS`, the first four **unedited** (the seed users are now
  members of the development company, and that must not change what they see). Then remove
  `.env` and `.secrets/`. Record in the acceptance map's notes how long step 4 of the script
  waited for the CLI invitation.
- [ ] **Step 3: Run the final local gate.**
  `dotnet format --verify-no-changes && dotnet build -warnaserror && dotnet test` (829 tests, as in
  Task 14: this task adds none), then
  `Auth__Tokens__Audience=x Auth__Tokens__Issuer=http://x/auth dotnet test`, then
  `git grep -nE "PRIVATE KEY|Password=" -- ':!*.md' ':!.env.example'` (only the known hits:
  the `${POSTGRES_PASSWORD…}` placeholder in `deploy/docker-compose.yml`, an assertion string in
  `KeyMaterialTests.cs`, and the marker `SECRET-MARKER` of
  `AdminCliTests.A_connection_string_never_reaches_the_output`) and `git status --porcelain`
  (no `.env`, no `.secrets/`). Also
  `grep -rnP '[\x00-\x08\x0B\x0C\x0E-\x1F]' src tests scripts deploy` finds nothing.
- [ ] **Step 4: Commit** — `test(e2e): tenancy over the real network; acceptance map for spec 0005`

---

## After the plan: verify, then merge

1. Dispatch the three local verifiers **in parallel** (Sonnet, fresh context, read-only), as
   defined in [`docs/workflow.md`](../../workflow.md#verification): realization vs **spec**
   (8 layers, using the acceptance map — and checking the six Review Focus lines), API/e2e
   (a clean stack with the mail catcher, the five e2e scripts and independent probes of the
   Goal sequence, taking each token from the mail as delivered), security (see below). Only
   one of them builds and tests in the tree; only one uses compose and ports 8080 and 8025,
   with a compose project name of its own.
2. What the security verifier is asked to prove, from the spec's notes: no endpoint under
   `/auth/org` can read or change another company's data whatever ids it is given (the
   schema refuses a membership or an invitation that names another company's role as well);
   every company API check reads the database and not the token, and a request authorised
   before its caller was demoted is refused under the lock; safety rule 1 cannot be
   bypassed through a role edit or deletion, an invitation (sent, resent or cancelled), a
   member's role or `*`; the last-manager
   check holds under concurrency (and the transactions are still at `READ COMMITTED`);
   the invitation endpoint says nothing about addresses outside the caller's company, in
   body, headers, the rows it leaves or its timing; invitation tokens are neither stored
   nor logged in clear; names set by members are encoded in mails and absent from headers;
   a broken or hostile manifest (anchors, aliases, huge, unknown keys) cannot stop the
   host or grant a permission it does not declare; the operator CLI cannot be driven into
   printing a connection string.
3. Each finding carries a `scope`. At most 2 fix rounds per verifier, then the issue goes to
   the owner.
4. Record the outcome: the plan-vs-implementation notes and the verification log in the
   acceptance map, and an `## As built (owner, date)` section in spec 0005 — with the
   amendments in "Open questions for owner" below, once the owner has answered them.
5. When every verifier passes, merge the feature branch into `main` locally.

## Decided by the owner

Three points of the first drafts are settled and built as decided; the spec records them as
Decisions 17, 18 and 19, and its Safety rule 1 and criterion 15 say them.

- **A manager cannot remove or re-role a member who holds more than they do** (Decision 17). A
  caller may not remove a member, or change a member's role, when the member's *current* role
  holds any permission the caller does not hold (a role with `*` can only be touched by a caller
  whose role holds `*`); the answer is `403 permission_not_held`. The operator stays exempt.
  Built in Task 11 (`MemberService`), tested in `OrgMemberTests` and over the network in step 7
  of the e2e script.
- **Cancelling an invitation is never subject to the mail limit** (Decision 18). A cancel sends no
  mail and must always be able to kill a link; sending and resending are limited per company and
  address. Built in Task 8 (`InvitationService.CancelAsync`), tested in `OrgInviteTests`.
- **One rule: nobody acts on anything that holds more than they do** (Decision 19). Beyond the
  members of Decision 17, a caller may not edit or delete a role whose *current* permissions
  include one they lack (`*` only by a caller holding `*`), nor send, resend or cancel an
  invitation whose role holds one they lack: `403 permission_not_held`; the check on the
  permissions a role would get stays; the operator is exempt. Built in Task 11
  (`InvitationService`) and Task 12 (`RoleService`), tested in `OrgInviteTests` and
  `OrgRoleTests`, and in step 7 of the e2e script for roles and members.

## Settled by the amended spec

The first draft of this plan asked the owner about these. The spec as amended now decides each
of them, and the plan builds what it says; none is open.

- A member endpoint cannot give `last_manager` to an honest, lone caller (spec, criterion 16):
  the rule is built in all three changes and tested at service level (the operator) and under
  the race, where the loser is `403 permissions_changed`.
- An invited address must be one mailbox (spec → General rules): `400 invalid_request` for
  `bob.acme.test`, `Bob <bob@acme.test>` or a list, for invitations through the API and the CLI.
- `Auth:Manifest:Path` is a required setting; only the *file* may be missing or broken. The
  manifest is strict: unknown key, duplicate key, anchor, alias, second document, more than 500
  permissions or 100 default roles, or over 64 KiB makes it invalid, and the last valid one
  stays.
- A role that holds `*` is stored as `["*"]` alone, a default role that lists it too; the list
  shows `*` unexpanded.
- The development company is created only when a seed user is configured; the second seed user
  gets the first default role, in the order of the manifest, that does not manage members, else
  a role called `member`.
- Rule 2 refuses only a change that takes a company that has a manager down to none.
- A person is a member of at most one company by code, not by the schema; acceptance takes an
  advisory lock per address, and `already_member` is said for a member of any company.
- Accounts made by an invitation have their address as user name, and Identity's list of
  allowed user-name characters is emptied for the whole service.
- Resending an invitation *is* checked against rule 1 (Decision 19).

## Open questions for owner

None. Every question of the earlier drafts is decided above.

**Risks found and not fixed here:**

- A pre-existing raw U+FFFE character sits in
  `docs/superpowers/plans/0003-lockout-and-abuse-resistance.md` (the file is meant to hold the
  escape as text, as plan 0004's Global Constraints say); it is outside this slice.
- The manifest parser reads the document twice (its events first, then its values) and keeps
  the whole of it in memory. The cost is bounded by the file's cap: at most 64 KiB, 500
  permissions and 100 default roles are ever parsed, at startup and in a CLI command, so a
  hostile or careless manifest cannot make a start slow or large; accepted as it stands.
- `e2e-login.sh` and `e2e-refresh.sh` need `PyJWT[crypto]` on the machine that runs them, as
  they always did (Task 15, step 2, checks it).

## As built

Implemented and verified locally. Tasks 1–15 were built as written here: every code block
compiled and passed the analyzers unchanged (the blocks had been run in a scratch copy
before implementation). What changed after the plan — four fixes after the task reviews
(the bearer token from the header only, one helper that ends every session, the operator
CLI's error stream, the `permission_not_held` declarations) and two rounds of fixes after
the verifiers (look-alike and internal invitation addresses, format characters in names,
exact ids, the rename under the lock, the description's cookie and headers) — is listed in
the [acceptance map](0005-acceptance-map.md) ("Plan-vs-implementation notes", "Local
verification log") and in spec 0005 → "As built". 935 tests pass (829 planned), and the
five e2e scripts pass on a clean stack.
