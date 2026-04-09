# Private Accounts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0010
- **Date:** 2026-04-09
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0010-private-accounts.md`](../specs/0010-private-accounts.md) (with its Amendment of 2026-04-09)
  — the plan argues from the spec; where they disagree, **the spec wins** and the disagreement is a finding (see
  [`docs/workflow.md`](../../workflow.md)). The spec's sentence on the time of audit rows was amended in two places together with this plan (the owner's ruling); nothing else in the spec is changed. The choices the spec leaves open are in "Decisions taken in this
  plan" at the end; "Open questions for the owner" lists what the plan could not settle.
- **Based on:** `272d9b4` (`main` at `6b53c97` with spec 0010 and its amendment on top of it). Every anchor of an edit below was checked against this base.

**Goal:** A product can let a private person use it alone: the person signs up with an email address only, proves it through the mail link,
chooses a password and is signed in to a hidden one-person space; the access token says `org_kind: "personal"`. A private person can delete
their own account; a company can invite a private person, who is warned and moves only on purpose. No account is ever left without a space:
an account that loses its only space is deleted, by a removal, a company deletion, a self-deletion or once at upgrade.

**Architecture:** A **space** is a row of the companies table with a new `Kind` (`Organization = 1`, `Personal = 2`); a personal space is named
`private`, has one role row `private` with an empty stored list and one member. **`MembershipReader`** reads the kind with the membership and
gives a personal member the permissions of the manifest's new `private` block, live; **`TenantClaims`** adds `org_kind` beside `org_id`, so
login, refresh and sign-up completion (one builder) all carry it. **`CompanyAccess`** refuses a personal caller on every company endpoint
(`GET /auth/me` keeps its own door), and **`CompanyLock`** locks organizations only, which hides personal spaces from every company service and
from the operator CLI at once. **`AccountDeletion`** is the one routine that deletes accounts (set-based, in the caller's transaction): mail
requests before pending sign-ups (the dispatcher's lock order), streaks, OpenIddict tokens before authorizations, the account (its Identity rows,
links and membership by cascade), the personal space, and one `account.deleted` row each. Member removal and company deletion call it under the
company lock; `DELETE /auth/account`, the invitation move and the start-up step take the **address lock** and read the membership again under
it. The sign-up flow carries a **`SignupKind`** on the pending row: `POST /auth/signup` reads its body in two phases (the kind first, so that its
switch is judged before the other members), the dispatcher follows the switch of the pending row's kind and sends "you already have an
account" for any account, and the preview and the completion learn the kind from the token. **`SignupGate`** holds two independent switches.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, ASP.NET Identity, OpenIddict, EF Core on PostgreSQL 16 (Npgsql), xUnit v3 with
Testcontainers; YamlDotNet (the manifest); Python 3.12 with FastAPI and PyJWT (`clients/python`, `samples/notes-api`); Angular 21 (standalone
components, signals, reactive forms), Vitest on jsdom and Playwright (Chromium and WebKit); bash and Python 3 (standard library only) for the
e2e script; Docker Compose. **No new NuGet, npm or pip package** is added (owner rule).

## Global Constraints

Values copied from the spec, one line each; every task includes them.

- **Off by default.** Private sign-up is on only when the manifest says `private.signup: self-service` **and** `Auth:App:FrontendUrls:CompleteSignup` is set; organization sign-up keeps the rule of spec 0009; the two switches are independent. An instance that does not turn it on sees only: `org_kind: "organization"` in tokens and `GET /auth/me`, `"private_signup":"off"` in `GET /auth/options`, and accounts deleted instead of kept when they lose their company.
- **General rules:** JSON bodies of at most 8 KiB in UTF-8, unknown members ignored, `400 invalid_request` for a body that is not a JSON object or a member of the wrong type, and for a blank `password` (empty or only white space) or one that holds NUL; `Cache-Control: no-store`; the security headers; the `415` of another charset and the per-IP `429` come first.
- **Spaces:** `Companies.Kind` `smallint` (`1` organization, `2` personal), every existing row an organization; a personal space is named `private`, has one role row `private` with an empty stored list, exactly one member, and nobody can join it.
- **The private role:** `private` in the token; its permissions come from the active manifest (`private.permissions`) at every login and refresh, sorted ordinally, without duplicates.
- **Manifest block `private`:** `signup: self-service` turns it on, any other value or no key or no block leaves it off; `permissions` must be in the catalog, never `members:manage`, `roles:manage`, `org:manage`, `org:delete` or `*`, at most 500, duplicates folded; any other key, or a refused permission, makes the manifest invalid (the last valid one stays, `/auth/health` `Degraded`); `private:` with no value is no block; a manifest stored earlier reads as off with no permissions.
- **`GET /auth/options`:** `200 {"signup": "self-service" | "invite-only", "private_signup": "self-service" | "off"}`; private wanted without the address: off, a start warning naming the key, `Degraded`.
- **`POST /auth/signup` `{"email","kind"?,"company_name"?}`:** both off → `403 signup_disabled` before the body; not an object or a `kind` other than `"organization"` (default) or `"private"` → `400 invalid_request`; the kind's switch off → `403 signup_disabled` before the other members are judged; then the address rule; an organization needs a valid `company_name`, a private request with any `company_name` member is `400`; one mail limit `signup` for both kinds; the pending sign-up is created or replaced with its kind; `signup.requested` with `details.kind`; `202` empty. A change of kind, or a look-alike spelling, voids the earlier link at once.
- **Mails:** the switch first (the pending row's kind must be on; with none, at least one switch on), else dropped; any account under the address → "you already have an account" (with the new sentence) to the stored address, pending sign-up deleted; else a live pending sign-up gets a new link in the variant of its kind.
- **`POST /auth/signup/preview`:** both off → `403`; bad token → `400 invalid_token`; the kind's switch off → `403`; `200 {"kind":"organization","email","company_name"}` or `{"kind":"private","email"}`; never `has_account`.
- **`POST /auth/signup/complete` `{"token","password"?,"company_name"?}`:** both off → `403` before the body; bad body (`password` not a string, blank or NUL; `company_name` present and against the name rule) → `400`; bad token → `400 invalid_token`; kind's switch off → `403`; organization without, or private with, `company_name` → `400` (token usable); any account → `409 already_registered` and the pending sign-up gone; no password → `400`; weak → `400 weak_password`; else one transaction: the account, the space (organization: company, default roles, first admin, `org.created`; private: personal space, its role row, the membership, no `org.*`), `signup.completed` with `details.kind`, the sign-in as a login (`login.succeeded`) in the same transaction.
- **Access token / `GET /auth/me`:** `org_kind` `"organization"` or `"personal"`, read with the membership at every login and refresh, never in the refresh token; `/auth/me` adds `org_kind` and, for a private account, `org_name` `"private"` and `roles` `["private"]`.
- **Company API:** every endpoint under `/auth/org` answers a caller whose space is personal (read from the database) `403 {"error":"forbidden"}`; `GET /auth/me` stays open. **CLI:** `list-orgs` hides personal spaces; `invite`, `remove-member`, `delete-org` with a personal id → `error: not_found`.
- **Invitations:** sending unchanged; the mail to an address with a private account spelled the same up to ASCII case gets the changed intro and the paragraph; preview adds `has_private_account`; accept `{"token","password"?,"leave_private_account"?}` (a boolean when present): company member → `409 already_member`; private account without `leave_private_account: true` → `409 private_account_exists`, nothing changes; with it → the space, role row and membership deleted, the account a member with its password kept, every session, link and streak ended, `invite.accepted` with `left_private_account` and `personal_org_id`, `204`; no account → password required as before.
- **`DELETE /auth/account` `{"password"}`:** the token's `org_kind` first: `personal` but the database is not → `403 permissions_changed`; not personal and a company in the database → `403 forbidden`; no membership → `permissions_changed` with an `org_id` claim, else `forbidden`; then `400`, `429` (lock, `account.delete_refused` `locked`), `403 wrong_password` (counted, `account.delete_refused` `wrong_password`), `204` with the refresh cookie cleared; under the address lock, membership read again; `general` per-IP policy.
- **Deletion:** removal, company deletion (every member, the caller included), self-deletion and the start-up step delete the account with `account.deleted` (`details.reason` `member_removed`, `org_deleted`, `self`, `upgrade`); the routine deletes the account and its Identity rows, its membership (and a personal space with its role row), refresh tokens and authorizations (tokens first), mail links, queued non-invitation mail requests of its address (before the pending sign-up), the pending sign-up and the streak; it keeps audit rows, invitations to the address and mail-limit counters. Removal and company deletion take no address lock.
- **Audit:** 26 kinds; `account.deleted` and `account.delete_refused` with the fields of the spec; `signup.requested` and `signup.completed` gain `details.kind`, `signup.completed` loses `existing_account`; the reason is `details.reason`.
- **OpenAPI:** `DELETE /auth/account` with its answers, its `415` and the cookie it clears; 31 operations, 21 under the charset guard.
- **Python package:** `Principal.org_kind: str = "organization"` (last field); absent claim → `"organization"`; any other present value than the two → `401`.
- **Samples:** the screens and calls of the spec's "Sample frontend" and "Sample backend"; texts in English; `samples/notes-api/auth.yaml` and `deploy/auth.yaml` turn private sign-up on.
- **Workflow** ([`docs/workflow.md`](../../workflow.md)): local only, Conventional Commits in English, documentation in English. **The orchestrator makes the commits**: implementers leave their changes uncommitted, never run a git command that changes state, and end with the "Hand back" step (files and a proposed subject, never a git command). One Sonnet implementer per task writes the code of that task, runs it and fixes what breaks; the code below is the plan's, written on paper, and the implementer's tests decide.
- **The EF migration** (Task 1) is created with `dotnet ef migrations add AddSpaceKinds`; its id is the orchestrator's business afterwards, and no test or document names it.
- **.NET on this machine:** the build gate is `"C:/Program Files/dotnet/dotnet.exe" build Auth-Core.slnx -warnaserror -v q -nologo -maxcpucount:2`; tests run as `tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.<Class> [-class ...]` after that build (`dotnet test` at the root finds no tests). Docker must be running (Testcontainers). The analyzers are strict: `ArgumentNullException.ThrowIfNull`, explicit `StringComparison`, `CultureInfo.InvariantCulture`, `[LoggerMessage]`.
- **Implementers run only their task's test classes and the classes their task edits** (`-maxThreads 2`). They never run the whole .NET suite, a live e2e script or Playwright: the verifiers do.
- **Python:** `C:/p6v/Scripts/python.exe` (the `python3` on `PATH` is a Store shim). The environment has an older copy of `auth_core_fastapi` installed from another checkout: the package's own tests put `src` first by themselves (`pyproject.toml`), and the sample's tests are run with `PYTHONPATH=../../clients/python/src` so that they use the package of this worktree.
- **Angular:** in `samples/notes-web`: `npm ci` once when `node_modules` is missing, then `npm test`, `npx ng build` and `npm run check:docs`. Texts are plain printable ASCII (`texts.spec.ts` checks it).
- **No `docker compose up` of any stack and no stopping of containers by implementers**; every compose command of a script carries its own project name, never `auth-core`.
- **Shell scripts** are LF; the orchestrator marks a new one executable in the index.
- **Unicode escapes stay out of the source.** A backslash followed by `u` and four hex digits typed into a tool parameter arrives as the character. The code below builds such text with `char.ConvertFromUtf32(...)`, `(char)0x...` or a `Backslash` constant. After writing, each task's "Hand back" step scans the files it touched ("scan the touched files") with
  `C:/p6v/Scripts/python.exe -c "import sys,unicodedata as u; bad=[(f,i,hex(ord(c))) for f in sys.argv[1:] for i,c in enumerate(open(f,encoding='utf-8').read()) if (u.category(c)=='Cc' and c not in '\t\r\n') or 0xFDD0<=ord(c)<=0xFDEF or (ord(c)&0xFFFE)==0xFFFE or ord(c)>0xFFFF]; print(bad or 'clean')" <files>`:
  it must print `clean` (Polish letters and the Greek letters of an existing comment are fine; no task writes an astral character).

## Checked on this code before the plan was written

Experiments in a throw-away clone of this branch at `4e2a2e0` (none of their code is copied here; the plan's code is written on paper):

- **Where `org_kind` goes.** Login, refresh and sign-up completion all build the access token's tenancy claims in `TenantClaims.Apply`; the
  refresh token never carries them (`AccessTokenClaimFilter` strips `TenantClaims.Types`). One claim added there reaches all three.
- **A deleted refresh-token row** gives `401 invalid_grant` through the existing handlers, as a revoked one does; deleting a user while its
  token rows exist gives the same. `OpenIddictTokens.AuthorizationId` has no cascade: tokens are deleted before authorizations.
- **Identity's delete** (`ExecuteDelete` on `AspNetUsers`) removes the roles, claims, logins, stored tokens, mail links and the membership by
  cascade and sets `Invites.InvitedBy` to null.
- **The migration** needs `HasDefaultValue(...)` on both new `Kind` columns; without it `dotnet ef` generates a default of `0`. With it: `Kind
  smallint NOT NULL DEFAULT 1` on both tables and `PendingSignups.CompanyName` nullable; a database at the last migration of slice 0009 keeps its
  rows and reads them as an organization and an organization sign-up.
- **The bulk delete of the dispatcher** written in Task 10 was run against all 24 combinations of {no pending sign-up, organization,
  private} x {organization switch on/off} x {private switch on/off} x {account or not}: every case kept or dropped as the spec's rule says.
  The pick of the next row carries no switch, so a row whose kind is turned off during a pass is still picked and then dropped by the switch
  check of the send, in the same pass (spec 0009's `SignupOffDispatchTests` pins that).
- **Deadlock.** The dispatcher locks a mail request and then the pending sign-up; deleting the pending sign-up first and then the mail requests
  deadlocks with it (`40P01`), the other order does not.
- **Lock slots.** One advisory lock per member of a company of 40 000 members runs PostgreSQL out of lock slots (`53200`); removal and company
  deletion therefore take none (spec, Amendment item 3).
- **`GET /auth/org` lets a personal account through** unless `CompanyAccess` checks the space explicitly (it needs no permission).
- **`JsonObjectBody`** rejects the whole body at the first bad member, so `POST /auth/signup` needs a reader that returns the members one by one
  (Task 9), and `leave_private_account` needs a boolean reader (Task 12).
- **A tracked `Membership` removed after the account's cascade** throws `DbUpdateConcurrencyException`: `MemberService` no longer removes it
  (Task 7).
- **YamlDotNet and the block:** `private:` and `private: ~` give no block; `private: {}` an empty one; `private.signup` with any string is valid
  (off unless `self-service`, unlike the top-level `signup`); `private: {foo: 1}`, `private: yes` and `private: [a]` are refused; `[~]` reads as
  `""`, which the catalog refuses.
- **Not proved here** (each has its test in the task named): the interleavings of self-deletion, invitation move and sign-up completion for one
  address (Task 13), and that WebKit keeps the sample's flows working (Task 17, run by the verifiers).

## Review Focus

The inputs and failure modes the spec implies that a person using this software is most likely to meet, most likely first; each has its test
in the task named.

1. **An instance that never turns private sign-up on, upgraded from slice 0009** (Toviq): a database made by the earlier migrations, accounts
   left without a company by earlier removals, a manifest without the block. The person expects the host to start, every company member to
   sign in as before with one more claim, and the left-over accounts to be gone once
   (`UpgradeToSpaceKindsTests.Rows_made_before_the_kinds_existed_are_an_organization_and_an_organization_sign_up`,
   `AccountsWithoutSpaceStartupTests.A_start_deletes_every_account_without_a_membership_once_and_keeps_the_seed_users`,
   `PrivateSignupGateTests.The_four_combinations_of_the_two_switches_give_their_own_options`; Tasks 1, 2 and 8).
2. **A private person who later gets an invitation from their employer**: the mail warns, the screen warns, one button moves them; the person
   pressing "Accept" without reading must not lose their space by accident
   (`PrivateInvitationTests.Accepting_without_leave_private_account_is_409_private_account_exists_and_changes_nothing`,
   `PrivateInvitationTests.Accepting_with_leave_private_account_moves_the_account_and_deletes_its_space`, `invite.private.spec.ts`; Tasks 12 and 16).
3. **A person who deletes their account and signs up again with the same address**, and the person who mistypes the password on the delete
   screen (`AccountDeletionEndpointTests.A_login_afterwards_is_the_401_of_an_unknown_address_and_a_new_sign_up_completes`,
   `AccountDeletionEndpointTests.A_wrong_password_is_403_wrong_password_counted_and_recorded`, `notes.account.spec.ts`; Tasks 13 and 16).
4. **An admin who removes a member, or deletes the company, without knowing that this deletes the accounts**: the confirmation says so, and
   the removed person can be invited again as a new account (`OrgMemberTests.A_removed_member_can_be_invited_again_but_the_old_sessions_stay_ended`,
   `team.spec.ts`; Tasks 7 and 16).
5. **Two things at once for one address**: a self-deletion, an invitation acceptance and a sign-up completion in any order, and a removal
   beside an acceptance (`PrivateAccountRaceTests`; Task 13).

## File Structure

New and changed files by responsibility. Everything under `src/Auth.Server/` unless said otherwise; tests under `tests/Auth.IntegrationTests/`.

| Area | Files | Responsibility |
| --- | --- | --- |
| Spaces in the schema | `src/Auth.Infrastructure/Persistence/CompanyKind.cs`, `SignupKind.cs`, `Company.cs`, `PendingSignup.cs`, `AuthDbContext.cs`, the migration `AddSpaceKinds`, `Tenancy/PersonalSpaces.cs` | the two kinds, their defaults, the personal space's rows (Task 1) |
| Manifest and switches | `Tenancy/Manifest.cs`, `Tenancy/ManifestParser.cs`, `Signup/SignupGate.cs`, `Signup/SignupContracts.cs`, `Signup/SignupOptionsEndpoint.cs`, `Api/SignupRoutes.cs` | the `private` block, two switches, `private_signup` (Task 2) |
| Claims | `Tenancy/TenantClaims.cs`, `Tenancy/MembershipReader.cs`, `Tenancy/SpaceKinds.cs`, `Tenancy/Contracts.cs`, `Api/OrgEndpoints.cs` | `org_kind`, the live private permissions, `GET /auth/me` (Task 3) |
| Refusals | `Tenancy/CompanyAccess.cs`, `Tenancy/CompanyLock.cs`, `Tenancy/OperatorCommands.cs`, `Seeding/DevUserSeeder.cs` | personal spaces refused by the company API, hidden from the CLI (Task 4) |
| Mail texts | `Email/MailTexts.cs`, `Email/MailComposer.cs` | the private sign-up intro, the new sentence, the invitation paragraph (Task 5) |
| Deletion | `Tenancy/AccountDeletion.cs`, `Audit/AuditKinds.cs`, `Tenancy/MemberService.cs`, `Tenancy/CompanyDeletionService.cs`, `Admin/AdminCli.cs`, `Api/TenancyEndpoints.cs`, `Tenancy/AddressLock.cs`, `Tenancy/AccountsWithoutSpaceCleanup.cs`, `Program.cs` | the routine, removal and company deletion, the start-up step (Tasks 6, 7, 8) |
| Sign-up by kind | `Requests/JsonFields.cs`, `Requests/JsonObjectBody.cs`, `Signup/SignupKinds.cs`, `Signup/SignupRequestEndpoint.cs`, `Email/MailRequestStore.cs`, `Signup/PendingSignups.cs`, `Email/MailDispatcher.cs`, `Signup/SignupAccounts.cs`, `Signup/SignupService.cs`, `Signup/SignupPreviewEndpoint.cs`, `Signup/SignupCompleteEndpoint.cs` | the request, the mails, the preview and the completion of both kinds; Amendment 3 of spec 0009 removed (Tasks 9, 10, 11) |
| Invitations | `Tenancy/InviteAcceptance.cs`, `Api/InviteEndpoints.cs`, `Tenancy/Outcome.cs` | `has_private_account`, `leave_private_account`, the move (Task 12) |
| Self-deletion | `Api/AccountDeletionEndpoint.cs`, `Tenancy/Contracts.cs`, `Api/TenancyEndpoints.cs` | `DELETE /auth/account` (Task 13) |
| Python | `clients/python/src/auth_core_fastapi/_principal.py`, `_verify.py`; `samples/notes-api/src/notes_api/app.py`; `samples/notes-api/auth.yaml`, `deploy/auth.yaml` | `org_kind`, `DELETE /api/me`, the shipped manifests (Task 14) |
| Sample frontend | `samples/notes-web/src/app/account-api.ts`, `auth/auth.service.ts`, `auth/auth.interceptor.ts`, `texts.ts`, `app.routes.ts`, `pages/signup.ts`, `pages/complete-signup.ts`, `pages/login.ts`, `pages/notes.ts`, `pages/invite.ts`, `pages/team.ts` and their specs, `src/testing/helpers.ts` | the screens (Tasks 15, 16) |
| Live checks | `scripts/e2e-private.sh`, `samples/notes-web/e2e/10-private.spec.ts`, `e2e/support/accounts.ts`, `scripts/e2e-signup.sh`, `scripts/e2e-tenancy.sh`, `scripts/e2e-hardening.sh`, `scripts/e2e-web.sh`, `e2e/09-signup.spec.ts` | the end-to-end checks (Task 17) |
| Documents | `docs/security/threat-model.md`, `docs/integration/angular.md`, `docs/integration/python-fastapi.md`, `docs/operations/backup.md`, `docs/design.md`, `CHANGELOG.md`, `docs/deployment/vps.md`, `README.md`, `clients/python/README.md`, `samples/notes-web/README.md` (spec 0009 is not edited: spec 0010 says what it replaces) | Task 18 |

## The tasks

| Day | Task | Deliverable |
| --- | --- | --- |
| Fri 10.04 | 1 | Spaces in the schema: `CompanyKind`, `SignupKind`, the migration, the personal space's rows, the test helper |
| Fri 10.04 | 2 | The manifest block `private`, the two switches, `private_signup` in `GET /auth/options` |
| Fri 10.04 | 3 | `org_kind` in the token and in `GET /auth/me`; the private role's live permissions |
| Sat 11.04 | 4 | The company API refuses a personal space; the CLI hides it |
| Sat 11.04 | 5 | The mail texts: the private sign-up intro, the new sentence, the invitation paragraph |
| Sat 11.04 | 6 | The deletion routine and the two audit kinds |
| Mon 13.04 | 7 | A removal and a company deletion delete the accounts |
| Mon 13.04 | 8 | The start-up step: accounts without a membership are deleted once |
| Mon 13.04 | 9 | `POST /auth/signup` with `kind` |
| Tue 14.04 | 10 | The dispatcher: the switch of the kind, any account gets the notice, the private mail, the invitation paragraph; Amendment 3's tests go |
| Tue 14.04 | 11 | The preview and the completion of both kinds; Amendment 3's code goes |
| Wed 15.04 | 12 | Invitations: `has_private_account`, `leave_private_account`, the move |
| Wed 15.04 | 13 | `DELETE /auth/account`, the races, the OpenAPI counts |
| Thu 16.04 | 14 | The Python package's `org_kind`, `DELETE /api/me` in `notes-api`, the shipped manifests |
| Thu 16.04 | 15 | The sample: the sign-up choice, the private completion, the sign-in link |
| Thu 16.04 | 16 | The sample: the account bar, "Delete account", the invitation warning, `/team` |
| Fri 17.04 | 17 | `scripts/e2e-private.sh`, the Playwright spec, the earlier e2e scripts and specs updated |
| Fri 17.04 | 18 | The documents and the acceptance map check |
| after Task 18 | verifiers | The three verifiers, the "As built", the merge (outside this plan, owner-gated) |

Tasks run in order inside a day and across days; each leaves the build gate and the tests of the files it touches green, and each task's tests
are written first and seen to fail.

**Contract counts that change** (tests that enumerate the contract; each task that changes one says so in its "Contract counts" line):
`OpenApiTests` (Tasks 2, 3, 9, 11, 12, 13), `OpenApiHardeningTests` (Task 13: 30 → 31 operations, 20 → 21 under the charset guard),
`AuditTableTests` (Task 6: 24 → 26 kinds), the token's claim lists in `LoginTests`, `SignupCompleteSignInTests` and `TenantClaimsTests`
(Task 3: `org_kind` added), `OrgEndpointTests` (Task 3: `/auth/me` gains `org_kind`), `RatePolicyTests` (Task 13: one line),
`PendingSignupTableTests` (Task 1: the columns).

---

## Day 1 — Friday 10.04: spaces, the switch, the claim

When the day is done, the database knows two kinds of space and two kinds of pending sign-up, a manifest can turn private sign-up on and say
what the private role grants, `GET /auth/options` tells a screen about both switches, and every access token and `GET /auth/me` say which kind
of space the caller is in. Nothing can make a private account yet except a test.

### Task 1: Spaces in the schema

**Files:**
- Create: `src/Auth.Infrastructure/Persistence/CompanyKind.cs`, `src/Auth.Infrastructure/Persistence/SignupKind.cs`, `src/Auth.Server/Tenancy/PersonalSpaces.cs`, the migration (generated)
- Modify: `src/Auth.Infrastructure/Persistence/Company.cs`, `src/Auth.Infrastructure/Persistence/PendingSignup.cs`, `src/Auth.Infrastructure/Persistence/AuthDbContext.cs`, `src/Auth.Server/Signup/SignupService.cs` (one line)
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs`, `tests/Auth.IntegrationTests/PendingSignupTableTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/SpaceKindTests.cs`, `tests/Auth.IntegrationTests/UpgradeToSpaceKindsTests.cs`

**Interfaces:**
- Consumes: `AuthDbContext`, `Company`, `CompanyRole`, `Membership`, `PendingSignup`, `NameInput.Normalize`, `TenancyTestBase`, `MailTestBase.CreateUserAsync`.
- Produces: `enum CompanyKind : short { Organization = 1, Personal = 2 }` and `enum SignupKind : short { Organization = 1, Private = 2 }` in `Auth.Infrastructure.Persistence`; `Company.Kind`; `PendingSignup.Kind`, `PendingSignup.CompanyName` (`string?`); `Auth.Server.Tenancy.PersonalSpaces` (`Name = "private"`, `RoleName = "private"`, `Guid Add(AuthDbContext db, Guid userId, DateTimeOffset now)`); test side `TenancyTestBase.CreatePrivateAccountAsync(string email, string password = UserPassword, bool confirmed = true)` returning `(Guid UserId, Guid SpaceId)`.

**Contract counts:** `PendingSignupTableTests.The_migration_makes_the_table` gains the column `Kind` and `CompanyName` becomes nullable.

**Decisions the plan takes here:**
- The CLR default of `Company.Kind` and `PendingSignup.Kind` is `0`, which is EF's sentinel, said explicitly with `HasSentinel` (else EF logs a warning at each start): code that does not set the kind sends no value, and the database's default `1` applies (read back by EF). A personal space sets `Personal` explicitly. Nothing else that makes a company changes.
- The role row of a personal space has a fixed id made in code (`Guid.NewGuid()`), so that the membership can name it in the same save.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/SpaceKindTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Seeding;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Spaces: the kind of a row of the companies table, and of a pending sign-up.</summary>
public sealed class SpaceKindTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private Task<List<string>> ColumnsAsync(string table) =>
        InDbAsync(db => db.Database.SqlQuery<string>(
            $"""
            SELECT column_name || ':' || data_type || ':' || is_nullable || ':' || coalesce(column_default, '') AS "Value"
            FROM information_schema.columns WHERE table_name = {table} AND column_name IN ('Kind', 'CompanyName') ORDER BY column_name
            """).ToListAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Companies_have_a_kind_that_defaults_to_organization()
    {
        Assert.Equal(["Kind:smallint:NO:1"], await ColumnsAsync("Companies"));
    }

    [Fact]
    public async Task Pending_sign_ups_have_a_kind_and_a_company_name_that_may_be_null()
    {
        Assert.Equal(["CompanyName:character varying:YES:", "Kind:smallint:NO:1"], await ColumnsAsync("PendingSignups"));
    }

    [Fact]
    public async Task A_company_made_as_before_is_an_organization()
    {
        var company = await CreateCompanyAsync("Acme");

        var kinds = await InDbAsync(db => db.Companies.AsNoTracking()
            .Where(c => c.Id == company || c.Name == DevUserSeeder.DefaultOrgName)
            .Select(c => c.Kind)
            .ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal([CompanyKind.Organization, CompanyKind.Organization], kinds);
    }

    [Fact]
    public async Task A_private_account_has_a_personal_space_named_private_with_one_empty_role_and_one_member()
    {
        var (userId, spaceId) = await CreatePrivateAccountAsync("ola@poczta.test");

        var space = await InDbAsync(db => db.Companies.AsNoTracking().SingleAsync(c => c.Id == spaceId, TestContext.Current.CancellationToken));
        Assert.Equal(CompanyKind.Personal, space.Kind);
        Assert.Equal("private", space.Name);
        var role = Assert.Single(await InDbAsync(db => db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == spaceId).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal("private", role.Name);
        Assert.Equal("PRIVATE", role.NormalizedName);
        Assert.Empty(role.Permissions);
        var member = Assert.Single(await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.CompanyId == spaceId).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(userId, member.UserId);
        Assert.Equal(role.Id, member.RoleId);
    }
}
```

`tests/Auth.IntegrationTests/UpgradeToSpaceKindsTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Auth.IntegrationTests;

/// <summary>A database of slice 0009 takes the kinds and keeps what it holds (spec 0010 → Spaces, Pending sign-ups).</summary>
public sealed class UpgradeToSpaceKindsTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string LastMigrationOfSlice0009 = "20260330193512_AddPendingSignups";

    [Fact]
    public async Task Rows_made_before_the_kinds_existed_are_an_organization_and_an_organization_sign_up()   // Review Focus 1
    {
        await using var factory = new AuthAppFactory(postgres, keys);   // never built: only its connection string is used
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(factory.ConnectionString).UseOpenIddict().Options;
        var cancellation = TestContext.Current.CancellationToken;
        var company = Guid.NewGuid();

        await using (var old = new AuthDbContext(options))
        {
            await old.GetService<IMigrator>().MigrateAsync(LastMigrationOfSlice0009, cancellation);
            await old.Database.ExecuteSqlAsync(
                $"""INSERT INTO "Companies" ("Id", "Name", "CreatedAt") VALUES ({company}, 'Toviq', now())""", cancellation);
            await old.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "PendingSignups" ("NormalizedEmail", "Email", "CompanyName", "RequestedAt", "ExpiresAt")
                VALUES ('ANNA@FIRMA.TEST', 'anna@firma.test', 'Firma', now(), now() + interval '1 day')
                """,
                cancellation);
        }

        await using (var upgraded = new AuthDbContext(options))
        {
            await upgraded.Database.MigrateAsync(cancellation);

            Assert.Equal(CompanyKind.Organization, await upgraded.Companies.Where(c => c.Id == company).Select(c => c.Kind).SingleAsync(cancellation));
            var pending = await upgraded.PendingSignups.AsNoTracking().SingleAsync(cancellation);
            Assert.Equal(SignupKind.Organization, pending.Kind);
            Assert.Equal("Firma", pending.CompanyName);
        }
    }
}
```

In `tests/Auth.IntegrationTests/PendingSignupTableTests.cs` replace

```csharp
                "CompanyName:character varying:100:NO",
                "Email:character varying:256:NO",
                "ExpiresAt:timestamp with time zone::NO",
                "NormalizedEmail:character varying:256:NO",
```

with

```csharp
                "CompanyName:character varying:100:YES",
                "Email:character varying:256:NO",
                "ExpiresAt:timestamp with time zone::NO",
                "Kind:smallint::NO",
                "NormalizedEmail:character varying:256:NO",
```

In `tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs`, before the line `    /// <summary>Makes an account with <see cref="MailTestBase.UserPassword"/> and puts it into the company with the named role.</summary>` add

```csharp
    /// <summary>
    /// A private account (spec 0010) made straight in the database, as a private sign-up leaves it: the account (with
    /// <see cref="MailTestBase.UserPassword"/> unless told otherwise), its personal space, the space's role row and the membership.
    /// </summary>
    protected async Task<(Guid UserId, Guid SpaceId)> CreatePrivateAccountAsync(string email, string password = UserPassword, bool confirmed = true)
    {
        var user = await CreateUserAsync(email, confirmed, password, member: false);
        var space = await InDbAsync(async db =>
        {
            var id = PersonalSpaces.Add(db, user.Id, StorableTime.Now(Clock));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return id;
        });
        return (user.Id, space);
    }

```

- [ ] **Step 2: See them fail.** `"C:/Program Files/dotnet/dotnet.exe" build Auth-Core.slnx -warnaserror -v q -nologo -maxcpucount:2` fails: `CompanyKind`, `SignupKind`, `PersonalSpaces` and the `Kind` properties do not exist.

- [ ] **Step 3: The two kinds.** `src/Auth.Infrastructure/Persistence/CompanyKind.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>What a row of the companies table is (spec 0010 → Spaces). The values are stored: never renumber them.</summary>
public enum CompanyKind : short
{
    /// <summary>A company, as in spec 0005. Every row made before spec 0010 is one.</summary>
    Organization = 1,

    /// <summary>The hidden one-person space of a private account.</summary>
    Personal = 2,
}
```

`src/Auth.Infrastructure/Persistence/SignupKind.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>What a pending sign-up asks for (spec 0010 → Pending sign-ups). The values are stored: never renumber them.</summary>
public enum SignupKind : short
{
    /// <summary>A company of one's own, as in spec 0009. Every row made before spec 0010 is one.</summary>
    Organization = 1,

    /// <summary>A private account, with a personal space.</summary>
    Private = 2,
}
```

- [ ] **Step 4: The entities and the model.** In `src/Auth.Infrastructure/Persistence/Company.cs` replace

```csharp
/// <summary>A company of the instance (spec 0005 → Concepts): an id and a name. Created by the operator only.</summary>
public sealed class Company
{
    public Guid Id { get; init; }

    public required string Name { get; set; }
```

with

```csharp
/// <summary>
/// A space of the instance: a company (spec 0005 → Concepts) or the personal space of a private account (spec 0010 → Spaces). An id, a name
/// and its kind.
/// </summary>
public sealed class Company
{
    public Guid Id { get; init; }

    public required string Name { get; set; }

    /// <summary>
    /// Left unset (<c>0</c>, EF's sentinel) the database's default applies: an organization. Only a private sign-up sets
    /// <see cref="CompanyKind.Personal"/>.
    /// </summary>
    public CompanyKind Kind { get; init; }
```

In `src/Auth.Infrastructure/Persistence/PendingSignup.cs` replace

```csharp
    public required string CompanyName { get; set; }
```

with

```csharp
    /// <summary>The company name of an organization sign-up; <see langword="null"/> exactly for a private one (spec 0010).</summary>
    public string? CompanyName { get; set; }

    /// <summary>Left unset (<c>0</c>, EF's sentinel) the database's default applies: an organization sign-up.</summary>
    public SignupKind Kind { get; set; }
```

In `src/Auth.Infrastructure/Persistence/AuthDbContext.cs` replace

```csharp
            company.HasKey(c => c.Id);
            company.Property(c => c.Name).HasMaxLength(100);
```

with

```csharp
            company.HasKey(c => c.Id);
            company.Property(c => c.Name).HasMaxLength(100);
            // Spec 0010: every row made before the column is an organization. Without the default the migration would give them 0.
            company.Property(c => c.Kind).HasDefaultValue(CompanyKind.Organization).HasSentinel((CompanyKind)0);   // the sentinel said: no EF warning at each start
```

and replace

```csharp
            pending.Property(p => p.CompanyName).HasMaxLength(100);
```

with

```csharp
            pending.Property(p => p.CompanyName).HasMaxLength(100);
            // Spec 0010: every row made before the column is an organization sign-up.
            pending.Property(p => p.Kind).HasDefaultValue(SignupKind.Organization).HasSentinel((SignupKind)0);
```

- [ ] **Step 5: The personal space.** `src/Auth.Server/Tenancy/PersonalSpaces.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Requests;

namespace Auth.Server.Tenancy;

/// <summary>
/// The hidden one-person space of a private account (spec 0010 → Spaces): a row of the companies table of kind
/// <see cref="CompanyKind.Personal"/> named <see cref="Name"/>, one role row named <see cref="RoleName"/> whose stored list is empty (its
/// permissions come from the manifest when the membership is read), and exactly one member.
/// </summary>
public static class PersonalSpaces
{
    public const string Name = "private";
    public const string RoleName = "private";

    /// <summary>Adds the space, its role row and the membership of <paramref name="userId"/> to the context; the caller saves. Returns the id of the space.</summary>
    public static Guid Add(AuthDbContext db, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);

        var space = new Company { Id = Guid.NewGuid(), Name = Name, Kind = CompanyKind.Personal, CreatedAt = now };
        var role = new CompanyRole
        {
            Id = Guid.NewGuid(),
            CompanyId = space.Id,
            Name = RoleName,
            NormalizedName = NameInput.Normalize(RoleName),
            Permissions = [],
        };
        db.Companies.Add(space);
        db.CompanyRoles.Add(role);
        db.Memberships.Add(new Membership { UserId = userId, CompanyId = space.Id, RoleId = role.Id, JoinedAt = now });
        return space.Id;
    }
}
```

- [ ] **Step 6: Keep the preview compiling.** `CompanyName` may now be null. In `src/Auth.Server/Signup/SignupService.cs` replace

```csharp
        return new SignupPreview(pending.Email, pending.CompanyName, account?.MayStartCompany(pending.Email) == true);
```

with

```csharp
        // Every row is an organization sign-up until the request can make a private one (Task 9); Task 11 answers the kind.
        return new SignupPreview(pending.Email, pending.CompanyName ?? string.Empty, account?.MayStartCompany(pending.Email) == true);
```

- [ ] **Step 7: The migration.** From the repository root: `"C:/Program Files/dotnet/dotnet.exe" tool restore`, then
`"C:/Program Files/dotnet/dotnet.exe" tool run dotnet-ef migrations add AddSpaceKinds --project src/Auth.Infrastructure --startup-project src/Auth.Server`.
Open the generated migration and check that its `Up` holds exactly: `AlterColumn<string>` of `PendingSignups.CompanyName` to `nullable: true`
(still `character varying(100)`), `AddColumn<short>("Kind", "PendingSignups", ..., nullable: false, defaultValue: (short)1)` and
`AddColumn<short>("Kind", "Companies", ..., nullable: false, defaultValue: (short)1)`. A `defaultValue: (short)0` means the `HasDefaultValue` of
Step 4 is missing. `HasSentinel` changes no column and adds nothing to the migration; it only tells EF that `0` means "not set", so EF
logs no warning about a database default at each start. Do not rename the migration.

- [ ] **Step 8: Run the tests.** Build (the gate above), then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.SpaceKindTests -class Auth.IntegrationTests.UpgradeToSpaceKindsTests -class Auth.IntegrationTests.PendingSignupTableTests -class Auth.IntegrationTests.TenancyTablesTests -class Auth.IntegrationTests.UpgradeFromV010Tests -class Auth.IntegrationTests.SignupPreviewTests -class Auth.IntegrationTests.PendingSignupsTests`
passes.

- [ ] **Step 9: Hand back** — uncommitted. Files: everything listed under Files, the generated migration and its designer file, and the
changed `AuthDbContextModelSnapshot.cs`. Scan the touched files. Proposed subject: `feat(db): spaces and pending sign-ups get a kind`.

### Task 2: The manifest block `private`, the two switches, `private_signup`

**Files:**
- Modify: `src/Auth.Server/Tenancy/Manifest.cs`, `src/Auth.Server/Tenancy/ManifestParser.cs`, `src/Auth.Server/Signup/SignupGate.cs`, `src/Auth.Server/Signup/SignupContracts.cs`, `src/Auth.Server/Signup/SignupOptionsEndpoint.cs`, `src/Auth.Server/Api/SignupRoutes.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`, `tests/Auth.IntegrationTests/SignupOptionsTests.cs`, `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`, `tests/Auth.IntegrationTests/SignupCompleteTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/ManifestPrivateTests.cs`, `tests/Auth.IntegrationTests/PrivateSignupGateTests.cs`

**Interfaces:**
- Consumes: `ManifestParser`, `Manifest`, `PermissionCatalog`, `SignupModes`, `SignupKind` (Task 1), `MailSettings`, `MailSettingsLoader.CompleteSignupUrlKey`.
- Produces: `Manifest(permissions, defaultRoles, signup = InviteOnly, bool privateSignup = false, IReadOnlyList<string>? privatePermissions = null)`, `Manifest.PrivateSignup`, `Manifest.PrivatePermissions` (stored and read back; a stored form without the block reads as off and empty); `SignupModes.OffName = "off"`; `SignupGate.IsOn` (organization, unchanged meaning), `PrivateOn`, `AnyOn`, `IsOnFor(SignupKind)`, `MissingAddress` (either switch wanted without the address), `ModeName`, `PrivateModeName`, `WarnIfMissingAddress()` (one warning per wanted switch); `OptionsResponse(string Signup, string PrivateSignup)`; test side `AuthAppFactory.PrivateBlock` and `AuthAppFactory.WithPrivateSignup(bool organization = true)`.

**Contract counts:** `OpenApiTests.The_responses_are_described_in_snake_case`: `OptionsResponse` becomes `["private_signup", "signup"]`. The `/auth/options` literals of `SignupOptionsTests`, `ShippedConfigurationTests` and `SignupCompleteTests` gain `"private_signup":"off"`.

**Decisions the plan takes here:**
- `private.signup` with a value other than `self-service` is valid and off (spec: "Any other value ... leaves it off"), unlike the top-level `signup`.
- The stored form writes the block only when it says something (`self-service` or permissions), so the stored form of a manifest of slice 0009 is unchanged.
- The endpoints keep reading `SignupGate.IsOn` (organization) until Tasks 9 to 11 teach them the kind: between those tasks, private sign-up is reported but not yet taken.

- [ ] **Step 1: Write the failing tests.**

In `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`, after the line `    public const string SelfServiceManifest = DefaultManifest + "\nsignup: self-service\n";` add

```csharp

    /// <summary>The block that turns private sign-up on (spec 0010), with two permissions of the default catalog for the private role.</summary>
    public const string PrivateBlock = "\nprivate:\n  signup: self-service\n  permissions: [reports:read, reports:approve]\n";
```

and before `    /// <summary>Removes the manifest file, as if the product had not shipped one.</summary>` add

```csharp
    /// <summary>
    /// Turns private sign-up on (spec 0010) and, unless told otherwise, organization sign-up too: the manifest and the completion address.
    /// Call it before the host is built.
    /// </summary>
    public AuthAppFactory WithPrivateSignup(bool organization = true)
    {
        WithManifest((organization ? SelfServiceManifest : DefaultManifest) + PrivateBlock);
        return WithSetting(MailSettingsLoader.CompleteSignupUrlKey, DefaultCompleteSignupUrl);
    }

```

`tests/Auth.IntegrationTests/ManifestPrivateTests.cs`:

```csharp
using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Manifest: the block <c>private</c>.</summary>
public sealed class ManifestPrivateTests
{
    private const string Roles = """
        permissions: [docs:read, docs:write]
        default_roles:
          admin: ["*"]
          user: [docs:read]
        """;

    private static Manifest Parsed(string yaml)
    {
        var result = ManifestParser.Parse(yaml);
        Assert.Null(result.Error);
        return Assert.IsType<Manifest>(result.Manifest);
    }

    private static string Invalid(string yaml)
    {
        var result = ManifestParser.Parse(yaml);
        Assert.Null(result.Manifest);
        return Assert.IsType<string>(result.Error);
    }

    [Fact]
    public void The_block_turns_private_sign_up_on_and_lists_its_permissions_sorted_without_duplicates()   // criterion 1
    {
        var manifest = Parsed(Roles + "\nprivate:\n  signup: self-service\n  permissions: [docs:write, docs:read, docs:write]\n");

        Assert.True(manifest.PrivateSignup);
        Assert.Equal(["docs:read", "docs:write"], manifest.PrivatePermissions);
        Assert.Equal(SignupMode.InviteOnly, manifest.Signup);   // the two switches are independent
    }

    [Theory]
    [InlineData("off")]
    [InlineData("invite-only")]
    [InlineData("Self-Service")]
    [InlineData("yes")]
    public void Any_other_value_of_the_switch_leaves_it_off_and_the_manifest_valid(string value)   // criterion 1
    {
        var manifest = Parsed(Roles + "\nprivate:\n  signup: " + value + "\n  permissions: [docs:read]\n");

        Assert.False(manifest.PrivateSignup);
        Assert.Equal(["docs:read"], manifest.PrivatePermissions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\nprivate:\n")]
    [InlineData("\nprivate: ~\n")]
    [InlineData("\nprivate: {}\n")]
    [InlineData("\nprivate:\n  signup:\n")]
    [InlineData("\nprivate:\n  permissions:\n")]
    public void No_block_an_empty_block_or_a_key_without_a_value_is_off_with_no_permissions(string block)   // criterion 1
    {
        var manifest = Parsed(Roles + block);

        Assert.False(manifest.PrivateSignup);
        Assert.Empty(manifest.PrivatePermissions);
    }

    [Theory]
    [InlineData("members:manage")]
    [InlineData("roles:manage")]
    [InlineData("org:manage")]
    [InlineData("org:delete")]
    [InlineData("\"*\"")]
    [InlineData("docs:delete")]
    [InlineData("~")]
    public void A_built_in_permission_the_star_or_one_outside_the_catalog_makes_the_manifest_invalid(string permission)   // criterion 1
    {
        var error = Invalid(Roles + "\nprivate:\n  signup: self-service\n  permissions: [docs:read, " + permission + "]\n");

        Assert.Contains("private.permissions", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\nprivate:\n  signup: self-service\n  extra: 1\n")]
    [InlineData("\nprivate: yes\n")]
    [InlineData("\nprivate: [docs:read]\n")]
    [InlineData("\nprivate:\n  signup: self-service\n  signup: off\n")]
    public void Another_key_or_a_block_that_is_not_a_map_makes_the_manifest_invalid(string block)   // criterion 1
    {
        _ = Invalid(Roles + block);
    }

    [Fact]
    public void More_than_500_permissions_make_the_manifest_invalid()
    {
        var many = string.Join(", ", Enumerable.Range(0, 501).Select(_ => "docs:read"));

        var error = Invalid(Roles + "\nprivate:\n  permissions: [" + many + "]\n");

        Assert.Contains("500", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stored_form_keeps_the_block()   // criterion 1
    {
        var manifest = new Manifest(["docs:read"], [new DefaultRole("admin", ["*"])], SignupMode.InviteOnly, privateSignup: true, privatePermissions: ["docs:read"]);

        var back = Manifest.FromJson(manifest.ToJson());

        Assert.NotNull(back);
        Assert.True(back.PrivateSignup);
        Assert.Equal(["docs:read"], back.PrivatePermissions);
    }

    [Fact]
    public void A_manifest_stored_by_slice_0009_reads_as_off_with_no_permissions()   // criterion 1
    {
        const string stored = """{"permissions":["docs:read"],"default_roles":[{"name":"admin","permissions":["*"]}],"signup":"self-service"}""";

        var manifest = Manifest.FromJson(stored);

        Assert.NotNull(manifest);
        Assert.False(manifest.PrivateSignup);
        Assert.Empty(manifest.PrivatePermissions);
        Assert.Equal(SignupMode.SelfService, manifest.Signup);
    }

    [Fact]
    public void A_manifest_without_the_block_is_stored_as_before()   // compatibility: the stored form of slice 0009 does not change
    {
        var manifest = new Manifest(["docs:read"], [new DefaultRole("admin", ["*"])]);

        Assert.DoesNotContain("\"private\":{", manifest.ToJson(), StringComparison.Ordinal);
    }
}
```

`tests/Auth.IntegrationTests/PrivateSignupGateTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Signup;
using Auth.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → General rules and <c>GET /auth/options</c>: two independent switches.</summary>
public sealed class PrivateSignupGateTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static async Task<string> BodyAsync(AuthAppFactory factory, string path)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Theory]
    [InlineData(false, false, """{"signup":"invite-only","private_signup":"off"}""")]
    [InlineData(true, false, """{"signup":"self-service","private_signup":"off"}""")]
    [InlineData(false, true, """{"signup":"invite-only","private_signup":"self-service"}""")]
    [InlineData(true, true, """{"signup":"self-service","private_signup":"self-service"}""")]
    public async Task The_four_combinations_of_the_two_switches_give_their_own_options(bool organization, bool @private, string expected)   // criterion 2
    {
        var manifest = (organization ? AuthAppFactory.SelfServiceManifest : AuthAppFactory.DefaultManifest) + (@private ? AuthAppFactory.PrivateBlock : "");
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(manifest)
            .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, AuthAppFactory.DefaultCompleteSignupUrl);

        Assert.Equal(expected, await BodyAsync(factory, "/auth/options"));
        var gate = factory.Services.GetRequiredService<SignupGate>();
        Assert.Equal(organization, gate.IsOn);
        Assert.Equal(@private, gate.PrivateOn);
        Assert.Equal(organization || @private, gate.AnyOn);
        Assert.Equal(@private, gate.IsOnFor(Auth.Infrastructure.Persistence.SignupKind.Private));
        Assert.Equal("Healthy", await BodyAsync(factory, "/auth/health"));
    }

    [Fact]
    public async Task Private_sign_up_without_the_address_is_off_warns_at_the_start_and_health_says_degraded()   // criterion 2
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.PrivateBlock)
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        Assert.Equal("""{"signup":"invite-only","private_signup":"off"}""", await BodyAsync(factory, "/auth/options"));
        Assert.Equal("Degraded", await BodyAsync(factory, "/auth/health"));
        var warning = Assert.Single(
            logs.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains(MailSettingsLoader.CompleteSignupUrlKey, StringComparison.Ordinal));
        Assert.Contains("private sign-up is off", warning.Message, StringComparison.Ordinal);
        Assert.True(factory.Services.GetRequiredService<SignupGate>().MissingAddress);
    }

    [Fact]
    public async Task A_manifest_that_breaks_the_block_keeps_the_last_valid_one_and_health_says_degraded()   // criterion 1
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        await using (var first = new AuthAppFactory(postgres, keys, database).WithPrivateSignup())
        {
            Assert.True(first.Services.GetRequiredService<ManifestHolder>().Current.PrivateSignup);
        }

        await using var second = new AuthAppFactory(postgres, keys, database)
            .WithManifest(AuthAppFactory.SelfServiceManifest + "\nprivate:\n  signup: self-service\n  permissions: [members:manage]\n")
            .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, AuthAppFactory.DefaultCompleteSignupUrl);

        var state = second.Services.GetRequiredService<ManifestHolder>().State;
        Assert.True(state.IsDegraded);
        Assert.True(state.Manifest.PrivateSignup);   // the last valid one, as stored by the first start
        Assert.Equal(["reports:approve", "reports:read"], state.Manifest.PrivatePermissions);
        Assert.Equal("Degraded", await BodyAsync(second, "/auth/health"));
    }
}
```

Update the existing literals (the body of `GET /auth/options` gains `private_signup`; nothing else changes in these tests):

- `tests/Auth.IntegrationTests/SignupOptionsTests.cs`: replace `Assert.Equal("""{"signup":"invite-only"}""", await response.Content.ReadAsStringAsync());` with `Assert.Equal("""{"signup":"invite-only","private_signup":"off"}""", await response.Content.ReadAsStringAsync());` and `Assert.Equal("""{"signup":"self-service"}""", await response.Content.ReadAsStringAsync());` with `Assert.Equal("""{"signup":"self-service","private_signup":"off"}""", await response.Content.ReadAsStringAsync());`.
- `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`: replace every `"""{"signup":"self-service"}"""` with `"""{"signup":"self-service","private_signup":"off"}"""` (two places) and every `"""{"signup":"invite-only"}"""` with `"""{"signup":"invite-only","private_signup":"off"}"""` (two places). (Task 14 turns the block on in `deploy/auth.yaml` and changes the first of them again.)
- `tests/Auth.IntegrationTests/SignupCompleteTests.cs`: replace `Assert.Equal("""{"signup":"self-service"}""", await options.Content.ReadAsStringAsync());` with `Assert.Equal("""{"signup":"self-service","private_signup":"off"}""", await options.Content.ReadAsStringAsync());`.
- `tests/Auth.IntegrationTests/OpenApiTests.cs`: replace `Assert.Equal(["signup"], Properties("OptionsResponse"));` with `Assert.Equal(["private_signup", "signup"], Properties("OptionsResponse"));`.

- [ ] **Step 2: See them fail.** The build fails: `Manifest.PrivateSignup`, `PrivatePermissions`, the constructor parameters and `SignupGate.PrivateOn`, `AnyOn`, `IsOnFor` do not exist.

- [ ] **Step 3: `Manifest`.** In `src/Auth.Server/Tenancy/Manifest.cs`:

(a) In `SignupModes`, after `    public const string SelfServiceName = "self-service";` add

```csharp

    /// <summary>What <c>GET /auth/options</c> says of private sign-up while it is off (spec 0010).</summary>
    public const string OffName = "off";
```

(b) Replace

```csharp
    public Manifest(IReadOnlyList<string> permissions, IReadOnlyList<DefaultRole> defaultRoles, SignupMode signup = SignupMode.InviteOnly)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(defaultRoles);

        Permissions = permissions;
        DefaultRoles = defaultRoles;
        Signup = signup;
        Catalog = new PermissionCatalog(permissions);
    }
```

with

```csharp
    /// <param name="privateSignup">Whether the block <c>private</c> says <c>signup: self-service</c> (spec 0010).</param>
    /// <param name="privatePermissions">What the private role grants: catalog permissions, none built in, sorted, distinct. None when absent.</param>
    public Manifest(
        IReadOnlyList<string> permissions, IReadOnlyList<DefaultRole> defaultRoles, SignupMode signup = SignupMode.InviteOnly,
        bool privateSignup = false, IReadOnlyList<string>? privatePermissions = null)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(defaultRoles);

        Permissions = permissions;
        DefaultRoles = defaultRoles;
        Signup = signup;
        PrivateSignup = privateSignup;
        PrivatePermissions = privatePermissions ?? [];
        Catalog = new PermissionCatalog(permissions);
    }
```

(c) After the property `public SignupMode Signup { get; }` (and its doc comment) add

```csharp

    /// <summary>
    /// Whether a private person may sign up (spec 0010 → Manifest, <c>private.signup: self-service</c>); whether private sign-up is on also
    /// needs the completion address (<c>SignupGate</c>).
    /// </summary>
    public bool PrivateSignup { get; }

    /// <summary>
    /// What the private role grants (spec 0010 → Spaces): read at every login and refresh of a private account, so a change reaches every one
    /// of them at its next refresh. Never a built-in permission, never <c>*</c>.
    /// </summary>
    public IReadOnlyList<string> PrivatePermissions { get; }
```

(d) Replace

```csharp
    public string ToJson() => JsonSerializer.Serialize(
        new StoredManifest(
            [.. Permissions], [.. DefaultRoles.Select(r => new StoredRole(r.Name, [.. r.Permissions]))], SignupModes.NameOf(Signup)),
        StoredJson);
```

with

```csharp
    public string ToJson() => JsonSerializer.Serialize(
        new StoredManifest(
            [.. Permissions],
            [.. DefaultRoles.Select(r => new StoredRole(r.Name, [.. r.Permissions]))],
            SignupModes.NameOf(Signup),
            PrivateSignup || PrivatePermissions.Count > 0
                ? new StoredPrivate(PrivateSignup ? SignupModes.SelfServiceName : SignupModes.OffName, [.. PrivatePermissions])
                : null),
        StoredJson);
```

(e) In `FromJson` replace

```csharp
            var signup = SignupModes.TryParse(stored.Signup, out var parsed) ? parsed : SignupMode.InviteOnly;
            return new Manifest(stored.Permissions, [.. stored.DefaultRoles.Select(r => new DefaultRole(r.Name, r.Permissions))], signup);
```

with

```csharp
            var signup = SignupModes.TryParse(stored.Signup, out var parsed) ? parsed : SignupMode.InviteOnly;
            // A form stored before spec 0010 has no block: private sign-up off, no permissions.
            var privateSignup = string.Equals(stored.Private?.Signup, SignupModes.SelfServiceName, StringComparison.Ordinal);
            return new Manifest(
                stored.Permissions,
                [.. stored.DefaultRoles.Select(r => new DefaultRole(r.Name, r.Permissions))],
                signup,
                privateSignup,
                stored.Private?.Permissions ?? []);
```

(f) Replace

```csharp
    private sealed record StoredManifest(string[] Permissions, StoredRole[] DefaultRoles, string? Signup = null);
```

with

```csharp
    private sealed record StoredPrivate(string? Signup, string[]? Permissions);

    private sealed record StoredManifest(string[] Permissions, StoredRole[] DefaultRoles, string? Signup = null, StoredPrivate? Private = null);
```

and in the class summary replace `(<c>signup</c>). Always valid:` with `(<c>signup</c>) and whether a private person may sign up alone (<c>private</c>, spec 0010). Always valid:`.

- [ ] **Step 4: `ManifestParser`.** In `src/Auth.Server/Tenancy/ManifestParser.cs`:

(a) Replace

```csharp
        return new ManifestParseResult(new Manifest(permissions, defaults, signup), null);
```

with

```csharp
        // Spec 0010: the block `private`. `private:` with no value is no block. Its switch is on with `self-service` only, and any other value
        // leaves it off (unlike the top-level `signup`, which refuses a value it does not know). The private role never manages a company.
        var privateSignup = string.Equals(document.Private?.Signup, SignupModes.SelfServiceName, StringComparison.Ordinal);
        var privateListed = (document.Private?.Permissions ?? []).Select(p => p ?? "").ToList();
        if (privateListed.Count > MaxPermissions)
        {
            return Fail($"private.permissions must hold at most {MaxPermissions} permissions");
        }

        if (privateListed.FirstOrDefault(p => p == PermissionCatalog.All || PermissionCatalog.BuiltIn.Contains(p, StringComparer.Ordinal)) is { } managing)
        {
            return Fail($"private.permissions may not hold '{managing}': a private account manages no company");
        }

        if (privateListed.FirstOrDefault(p => !catalog.Accepts(p)) is { } outside)
        {
            return Fail($"private.permissions holds '{outside}', which is not in the catalog");
        }

        List<string> privatePermissions = [.. privateListed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return new ManifestParseResult(new Manifest(permissions, defaults, signup, privateSignup, privatePermissions), null);
```

(b) In `ManifestDocument`, after

```csharp
        // `invite-only` or `self-service` (spec 0009); a text so that any other value reaches the check above instead of a binding error.
        public string? Signup { get; set; }
```

add

```csharp

        // The block of spec 0010. Its own keys only: any other is an error, like a typo at the top level.
        public PrivateDocument? Private { get; set; }
```

(c) After the closing brace of `ManifestDocument` (the last `    }` before the class's closing `}`) add

```csharp

    // The block `private` (spec 0010 → Manifest). The switch is a text, so that any value reaches the rule above.
    internal sealed class PrivateDocument
    {
        public string? Signup { get; set; }

        public List<string?>? Permissions { get; set; }
    }
```

- [ ] **Step 5: `SignupGate`.** Replace the body of `src/Auth.Server/Signup/SignupGate.cs` from `/// <summary>` to the end with

```csharp
/// <summary>
/// Whether sign-up is on (spec 0009 → General rules; spec 0010: two independent switches). Organization sign-up is on when the manifest says
/// <c>signup: self-service</c> <b>and</b> the completion address is configured; private sign-up when it says <c>private.signup: self-service</c>
/// and the same address is configured. It reads the manifest of the process at the moment of the call, as every request does. A switch wanted
/// without the address stays off, and that is a misconfiguration the start warns about and the health check reports.
/// </summary>
public sealed partial class SignupGate(ManifestHolder manifest, MailSettings mail, ILogger<SignupGate> logger)
{
    private bool OrganizationWanted => manifest.Current.Signup == SignupMode.SelfService;

    private bool PrivateWanted => manifest.Current.PrivateSignup;

    private bool HasAddress => mail.CompleteSignupUrl is not null;

    /// <summary>Organization sign-up (spec 0009) is on.</summary>
    public bool IsOn => OrganizationWanted && HasAddress;

    /// <summary>Private sign-up (spec 0010) is on.</summary>
    public bool PrivateOn => PrivateWanted && HasAddress;

    /// <summary>At least one of the two is on: otherwise the three sign-up endpoints answer <c>403</c> before they read the body.</summary>
    public bool AnyOn => IsOn || PrivateOn;

    /// <summary>The switch of one kind of sign-up.</summary>
    public bool IsOnFor(SignupKind kind) => kind == SignupKind.Private ? PrivateOn : IsOn;

    /// <summary>The manifest asks for either kind of sign-up but the completion address is not set.</summary>
    public bool MissingAddress => (OrganizationWanted || PrivateWanted) && !HasAddress;

    /// <summary>What <c>GET /auth/options</c> says in <c>signup</c>: <c>self-service</c> only while organization sign-up is on.</summary>
    public string ModeName => IsOn ? SignupModes.SelfServiceName : SignupModes.InviteOnlyName;

    /// <summary>What <c>GET /auth/options</c> says in <c>private_signup</c>: <c>self-service</c> only while private sign-up is on.</summary>
    public string PrivateModeName => PrivateOn ? SignupModes.SelfServiceName : SignupModes.OffName;

    /// <summary>Called once at start, after the manifest is active: one warning for each switch that is wanted without the address.</summary>
    public void WarnIfMissingAddress()
    {
        if (HasAddress)
        {
            return;
        }

        if (OrganizationWanted)
        {
            LogMissingAddress(logger, MailSettingsLoader.CompleteSignupUrlKey);
        }

        if (PrivateWanted)
        {
            LogMissingAddressForPrivate(logger, MailSettingsLoader.CompleteSignupUrlKey);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The manifest says signup: self-service, but {Key} is not set: sign-up is off.")]
    private static partial void LogMissingAddress(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The manifest says private.signup: self-service, but {Key} is not set: private sign-up is off.")]
    private static partial void LogMissingAddressForPrivate(ILogger logger, string key);
}
```

and add `using Auth.Infrastructure.Persistence;` as the first `using` of the file.

- [ ] **Step 6: The options.** In `src/Auth.Server/Signup/SignupContracts.cs` replace

```csharp
/// <summary>Response of <c>GET /auth/options</c>. A client ignores members it does not know (slice 0010 adds <c>providers</c>).</summary>
public sealed record OptionsResponse(string Signup);
```

with

```csharp
/// <summary>
/// Response of <c>GET /auth/options</c>: <c>signup</c> (spec 0009) and <c>private_signup</c> (spec 0010), each following its switch. A client
/// ignores members it does not know.
/// </summary>
public sealed record OptionsResponse(string Signup, string PrivateSignup);
```

In `src/Auth.Server/Signup/SignupOptionsEndpoint.cs` replace `return ApiResults.Ok(new OptionsResponse(gate.ModeName));` with
`return ApiResults.Ok(new OptionsResponse(gate.ModeName, gate.PrivateModeName));`.

In `src/Auth.Server/Api/SignupRoutes.cs` replace

```csharp
                "What a product's screens may offer. `signup` is `self-service` while sign-up is on and `invite-only` otherwise. "
                + "A client ignores members it does not know.")
```

with

```csharp
                "What a product's screens may offer. `signup` is `self-service` while organization sign-up is on and `invite-only` otherwise; "
                + "`private_signup` is `self-service` while private sign-up is on and `off` otherwise. A client ignores members it does not know.")
```

- [ ] **Step 7: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ManifestPrivateTests -class Auth.IntegrationTests.PrivateSignupGateTests -class Auth.IntegrationTests.SignupOptionsTests -class Auth.IntegrationTests.ShippedConfigurationTests -class Auth.IntegrationTests.SignupCompleteTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.ManifestSignupTests -class Auth.IntegrationTests.ManifestParserTests -class Auth.IntegrationTests.ManifestActivationTests -class Auth.IntegrationTests.ManifestSignupActivationTests -class Auth.IntegrationTests.SignupGateTests`
passes.

- [ ] **Step 8: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): the manifest block private, two sign-up switches and private_signup in the options`.

### Task 3: `org_kind` in the token and in `GET /auth/me`; the private role's live permissions

**Files:**
- Create: `src/Auth.Server/Tenancy/SpaceKinds.cs`
- Modify: `src/Auth.Server/Tenancy/MembershipReader.cs`, `src/Auth.Server/Tenancy/TenantClaims.cs`, `src/Auth.Server/Tenancy/Contracts.cs`, `src/Auth.Server/Api/OrgEndpoints.cs`
- Modify (tests): `tests/Auth.IntegrationTests/TenantClaimsTests.cs`, `tests/Auth.IntegrationTests/LoginTests.cs`, `tests/Auth.IntegrationTests/SignupCompleteSignInTests.cs`, `tests/Auth.IntegrationTests/OrgEndpointTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/PrivateAccountClaimsTests.cs`

**Interfaces:**
- Consumes: `CompanyKind` (Task 1), `Manifest.PrivatePermissions` (Task 2), `TenancyTestBase.CreatePrivateAccountAsync` (Task 1), `ManifestHolder.Set`.
- Produces: `TenantContext(..., bool HoldsAll, CompanyKind Kind = CompanyKind.Organization)` (the new parameter last, defaulted: `AuditLogTests` builds one positionally); `TenantClaims.OrgKind = "org_kind"` in `TenantClaims.Types` (`[OrgId, OrgKind, Roles, Permissions]`); `SpaceKinds.OrganizationName`, `PersonalName`, `NameOf(CompanyKind)`; `MeResponse(Guid Sub, string Email, Guid OrgId, string OrgName, string OrgKind, string[] Roles, string[] Permissions)`.

**Contract counts:** the claim list of the access token gains `org_kind` in `TenantClaimsTests.Token_has_no_claim_beyond_the_contract_and_the_tenancy_claims_after_refreshes`, `LoginTests` (line 78) and `SignupCompleteSignInTests.AccessTokenClaims`; `OrgEndpointTests.Me_returns_the_callers_current_data_from_the_database` and `OpenApiTests.The_responses_are_described_in_snake_case` (`MeResponse`) gain `org_kind`.

- [ ] **Step 1: Write the failing tests.** `tests/Auth.IntegrationTests/PrivateAccountClaimsTests.cs`:

```csharp
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Access token and <c>GET /auth/me</c>, and the private role's permissions read live from the manifest.</summary>
public sealed class PrivateAccountClaimsTests : TenancyTestBase
{
    private const string Private = "ola@poczta.test";

    public PrivateAccountClaimsTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The private role grants reports:read and reports:approve; the switch does not matter for signing in.
        Factory.WithManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.PrivateBlock);
    }

    [Fact]
    public async Task A_private_account_signs_in_with_its_space_the_private_role_and_the_manifests_permissions()   // criteria 9, 12
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);

        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);

        Assert.Equal(user.ToString(), AccessTokens.Text(session.AccessToken, "sub"));
        Assert.Equal(space.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal("personal", AccessTokens.Text(session.AccessToken, "org_kind"));
        Assert.Equal(["private"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(["reports:approve", "reports:read"], AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task A_company_members_token_says_organization_and_otherwise_holds_the_claims_it_held()   // criterion 12
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        Assert.Equal("organization", AccessTokens.Text(session.AccessToken, "org_kind"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(
            ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"],
            AccessTokens.Payload(session.AccessToken).Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Me_answers_org_kind_for_both_kinds_and_private_for_a_private_account()   // criterion 12
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var privateToken = (await SessionApi.LoginAsync(Client, Private, UserPassword)).AccessToken;
        var memberToken = (await SessionApi.LoginAsync(Client, Factory)).AccessToken;

        using var privateMe = await TenancyApi.Get(Client, OrgEndpoints.MePath, privateToken);
        using var memberMe = await TenancyApi.Get(Client, OrgEndpoints.MePath, memberToken);

        var mine = await TenancyApi.ReadOkAsync(privateMe);
        Assert.Equal(user.ToString(), mine.GetProperty("sub").GetString());
        Assert.Equal(space.ToString(), mine.GetProperty("org_id").GetString());
        Assert.Equal("private", mine.GetProperty("org_name").GetString());
        Assert.Equal("personal", mine.GetProperty("org_kind").GetString());
        Assert.Equal(["private"], mine.GetProperty("roles").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["reports:approve", "reports:read"], mine.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("organization", (await TenancyApi.ReadOkAsync(memberMe)).GetProperty("org_kind").GetString());
    }

    [Fact]
    public async Task A_change_of_the_private_permissions_reaches_the_next_refresh()   // criterion 13
    {
        await CreatePrivateAccountAsync(Private);
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var before = Holder.Current;

        Holder.Set(new Manifest(before.Permissions, before.DefaultRoles, before.Signup, before.PrivateSignup, ["templates:manage"]), null);
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        Assert.Equal(["templates:manage"], AccessTokens.Array(refreshed.AccessToken, "permissions"));
        Assert.Equal("personal", AccessTokens.Text(refreshed.AccessToken, "org_kind"));
        Assert.Equal(["private"], AccessTokens.Array(refreshed.AccessToken, "roles"));
    }

    [Fact]
    public async Task Without_the_block_a_private_account_still_signs_in_with_no_permissions()   // criterion 13
    {
        await CreatePrivateAccountAsync(Private);
        var before = Holder.Current;
        Holder.Set(new Manifest(before.Permissions, before.DefaultRoles, before.Signup), null);

        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);

        Assert.Empty(AccessTokens.Array(session.AccessToken, "permissions"));
        Assert.Equal(["private"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal("personal", AccessTokens.Text(session.AccessToken, "org_kind"));
    }

    [Fact]
    public async Task The_star_of_a_company_role_is_never_held_by_the_private_role()   // a personal space's member never holds everything
    {
        var (user, _) = await CreatePrivateAccountAsync(Private);

        var tenant = await TenantOfAsync(user);

        Assert.False(tenant.HoldsAll);
        Assert.Equal(Auth.Infrastructure.Persistence.CompanyKind.Personal, tenant.Kind);
    }
}
```

Update the existing pins (the access token gains `org_kind`, `/auth/me` gains `org_kind`; nothing else changes):

- `tests/Auth.IntegrationTests/TenantClaimsTests.cs`: replace
  `            ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "permissions", "roles", "sub"],` with
  `            ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"],`; and in
  `The_claim_filter_takes_the_three_claims_out_of_a_refresh_token_and_leaves_them_in_an_access_token` replace
  `                new Claim(TenantClaims.OrgId, Guid.NewGuid().ToString()),` with
  ```csharp
                new Claim(TenantClaims.OrgId, Guid.NewGuid().ToString()),
                new Claim(TenantClaims.OrgKind, "organization"),
  ```
- `tests/Auth.IntegrationTests/LoginTests.cs`: replace
  `        string[] expected = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "permissions", "roles", "sub"];` with
  `        string[] expected = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"];`.
- `tests/Auth.IntegrationTests/SignupCompleteSignInTests.cs`: replace
  `    private static readonly string[] AccessTokenClaims = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "permissions", "roles", "sub"];` with
  `    private static readonly string[] AccessTokenClaims = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"];`.
- `tests/Auth.IntegrationTests/OrgEndpointTests.cs`: replace
  `        Assert.Equal(["email", "org_id", "org_name", "permissions", "roles", "sub"], me.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));` with
  ```csharp
        Assert.Equal(["email", "org_id", "org_kind", "org_name", "permissions", "roles", "sub"], me.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("organization", me.GetProperty("org_kind").GetString());
  ```
- `tests/Auth.IntegrationTests/OpenApiTests.cs`: replace
  `        Assert.Equal(["email", "org_id", "org_name", "permissions", "roles", "sub"], Properties("MeResponse"));` with
  `        Assert.Equal(["email", "org_id", "org_kind", "org_name", "permissions", "roles", "sub"], Properties("MeResponse"));`.

- [ ] **Step 2: See them fail.** The build fails: `TenantClaims.OrgKind` and `TenantContext.Kind` do not exist.

- [ ] **Step 3: The names of the kinds.** `src/Auth.Server/Tenancy/SpaceKinds.cs`:

```csharp
using Auth.Infrastructure.Persistence;

namespace Auth.Server.Tenancy;

/// <summary>The kind of a space as the access token and <c>GET /auth/me</c> name it (spec 0010 → Access token).</summary>
public static class SpaceKinds
{
    public const string OrganizationName = "organization";
    public const string PersonalName = "personal";

    public static string NameOf(CompanyKind kind) => kind == CompanyKind.Personal ? PersonalName : OrganizationName;
}
```

- [ ] **Step 4: `MembershipReader`.** In `src/Auth.Server/Tenancy/MembershipReader.cs` replace from
`/// <summary>A user's company and role as the database says now, with what the role grants against the active catalog.</summary>`
to the end of the file with

```csharp
/// <summary>A user's space and role as the database says now, with what the role grants against the active catalog.</summary>
/// <param name="Permissions">What the role grants: <c>*</c> expanded, no duplicates, sorted ordinally, never <c>*</c>.</param>
/// <param name="HoldsAll">Whether the role holds <c>*</c> itself, not merely every permission one by one. Never for a personal space.</param>
/// <param name="Kind">An organization, or the personal space of a private account (spec 0010). Last and defaulted: the tests build one by position.</param>
public sealed record TenantContext(
    Guid UserId, Guid CompanyId, string CompanyName, Guid RoleId, string RoleName, IReadOnlyList<string> Permissions, bool HoldsAll,
    CompanyKind Kind = CompanyKind.Organization)
{
    public bool Holds(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);
}

/// <summary>
/// Reads a user's membership, role and the catalog afresh (spec 0005 → Access token): login, refresh, <c>GET /auth/me</c>
/// and every call of the company API go through it, so none of them trusts what a token said earlier. For a personal space (spec 0010) the
/// role's stored list is empty and the permissions are the manifest's <c>private.permissions</c>, read now: a manifest change reaches every
/// private account at its next refresh.
/// </summary>
public sealed class MembershipReader(AuthDbContext db, ManifestHolder manifest)
{
    /// <summary>The user's membership; <see langword="null"/> when they belong to no space. With several, the earliest.</summary>
    public async Task<TenantContext?> ReadAsync(Guid userId, CancellationToken cancellationToken)
    {
        var row = await (
            from membership in db.Memberships.AsNoTracking()
            join company in db.Companies.AsNoTracking() on membership.CompanyId equals company.Id
            join role in db.CompanyRoles.AsNoTracking() on membership.RoleId equals role.Id
            where membership.UserId == userId
            orderby membership.JoinedAt
            select new { CompanyId = company.Id, CompanyName = company.Name, company.Kind, RoleId = role.Id, RoleName = role.Name, role.Permissions })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var current = manifest.Current;
        var personal = row.Kind == CompanyKind.Personal;
        return new TenantContext(
            userId,
            row.CompanyId,
            row.CompanyName,
            row.RoleId,
            row.RoleName,
            current.Catalog.Expand(personal ? current.PrivatePermissions : row.Permissions),
            !personal && row.Permissions.Contains(PermissionCatalog.All, StringComparer.Ordinal),
            row.Kind);
    }
}
```

- [ ] **Step 5: `TenantClaims`.** In `src/Auth.Server/Tenancy/TenantClaims.cs`:

(a) Replace

```csharp
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
```

with

```csharp
/// <summary>
/// The tenancy claims of the access token: <c>org_id</c>, <c>roles</c> and <c>permissions</c> (spec 0005 → Access token) and
/// <c>org_kind</c> (spec 0010). Login, refresh and sign-up completion build them in the same place, from the same read, so they cannot differ.
/// </summary>
public static class TenantClaims
{
    public const string OrgId = "org_id";
    public const string OrgKind = "org_kind";
    public const string Roles = "roles";
    public const string Permissions = "permissions";

    public static readonly IReadOnlyList<string> Types = [OrgId, OrgKind, Roles, Permissions];
```

(b) Replace

```csharp
        identity.AddClaim(new Claim(OrgId, tenant.CompanyId.ToString(), ClaimValueTypes.String));
```

with

```csharp
        identity.AddClaim(new Claim(OrgId, tenant.CompanyId.ToString(), ClaimValueTypes.String));
        identity.AddClaim(new Claim(OrgKind, SpaceKinds.NameOf(tenant.Kind), ClaimValueTypes.String));
```

(c) In the two doc comments of the class, `the three claims` becomes `the tenancy claims` (two places: the summary of `Apply` and of `DestinationsOf`).

- [ ] **Step 6: `GET /auth/me`.** In `src/Auth.Server/Tenancy/Contracts.cs` replace

```csharp
/// <summary>Response of <c>GET /auth/me</c>: the caller as the database says now.</summary>
public sealed record MeResponse(Guid Sub, string Email, Guid OrgId, string OrgName, string[] Roles, string[] Permissions);
```

with

```csharp
/// <summary>
/// Response of <c>GET /auth/me</c>: the caller as the database says now. <c>org_kind</c> is <c>organization</c> or <c>personal</c> (spec
/// 0010); a private account's space is named <c>private</c> and its role is <c>private</c>.
/// </summary>
public sealed record MeResponse(Guid Sub, string Email, Guid OrgId, string OrgName, string OrgKind, string[] Roles, string[] Permissions);
```

In `src/Auth.Server/Api/OrgEndpoints.cs` replace

```csharp
        return ApiResults.Ok(new MeResponse(caller.UserId, user.Email, caller.CompanyId, caller.CompanyName, [caller.RoleName], [.. caller.Permissions]));
```

with

```csharp
        return ApiResults.Ok(new MeResponse(
            caller.UserId, user.Email, caller.CompanyId, caller.CompanyName, SpaceKinds.NameOf(caller.Kind), [caller.RoleName], [.. caller.Permissions]));
```

- [ ] **Step 7: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.PrivateAccountClaimsTests -class Auth.IntegrationTests.TenantClaimsTests -class Auth.IntegrationTests.LoginTests -class Auth.IntegrationTests.SignupCompleteSignInTests -class Auth.IntegrationTests.OrgEndpointTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.RefreshTests -class Auth.IntegrationTests.BearerValidationTests -class Auth.IntegrationTests.TokenVerificationTests -class Auth.IntegrationTests.AuditLogTests`
passes.

- [ ] **Step 8: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): org_kind in the access token and in /auth/me; the private role reads its permissions from the manifest`.

---

## Day 2 — Saturday 11.04: refusals, mail texts, the deletion routine

When the day is done, a personal space is out of reach of the company API and of the operator, the mails of this slice exist in both
languages, and one routine can delete accounts with everything the spec lists. Nothing calls the routine yet.

### Task 4: The company API refuses a personal space; the CLI hides it

**Files:**
- Modify: `src/Auth.Server/Tenancy/CompanyAccess.cs`, `src/Auth.Server/Tenancy/CompanyLock.cs`, `src/Auth.Server/Tenancy/OperatorCommands.cs`, `src/Auth.Server/Api/OrgEndpoints.cs`, `src/Auth.Server/Seeding/DevUserSeeder.cs`
- Create (tests): `tests/Auth.IntegrationTests/PersonalSpaceRefusalTests.cs`, `tests/Auth.IntegrationTests/PersonalSpaceCliTests.cs`

**Interfaces:**
- Consumes: `TenantContext.Kind` (Task 3), `CompanyKind` (Task 1), `TenancyTestBase.CreatePrivateAccountAsync`, `PersonalSpaces.Add`, `OperatorCli.RunAsync`.
- Produces: `CompanyAccess.AuthorizeAsync` refuses a personal caller with `403 forbidden` (permission or not); `CompanyAccess.AuthorizeAnyKindAsync(HttpContext, MembershipReader)` for `GET /auth/me` (and Task 13); `CompanyLock.AcquireAsync` locks organizations only (a personal id is `not_found` for every company service and every operator command).

**Contract counts:** none.

- [ ] **Step 1: Write the failing tests.** `tests/Auth.IntegrationTests/PersonalSpaceRefusalTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Api;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → The company API: every endpoint under /auth/org refuses a personal space, read from the database.</summary>
public sealed class PersonalSpaceRefusalTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Private = "ola@poczta.test";

    public static TheoryData<string, string> CompanyEndpoints => new()
    {
        { "GET", "/auth/org" },
        { "PATCH", "/auth/org" },
        { "DELETE", "/auth/org" },
        { "GET", "/auth/org/members" },
        { "PUT", "/auth/org/members/{id}/role" },
        { "DELETE", "/auth/org/members/{id}" },
        { "GET", "/auth/org/invites" },
        { "POST", "/auth/org/invites" },
        { "POST", "/auth/org/invites/{id}/resend" },
        { "DELETE", "/auth/org/invites/{id}" },
        { "GET", "/auth/org/roles" },
        { "POST", "/auth/org/roles" },
        { "PUT", "/auth/org/roles/{id}" },
        { "DELETE", "/auth/org/roles/{id}" },
    };

    /// <summary>A body each endpoint would take from a member who may use it: only the refusal of the space can stop it.</summary>
    private static object? BodyFor(string method, string path) => (method, path) switch
    {
        ("PATCH", "/auth/org") => new { name = "Renamed" },
        ("DELETE", "/auth/org") => new { name = PersonalSpaces.Name, password = UserPassword },
        ("PUT", "/auth/org/members/{id}/role") => new { role_id = Guid.NewGuid() },
        ("POST", "/auth/org/invites") => new { email = "new@poczta.test", role_id = Guid.NewGuid() },
        ("POST", "/auth/org/roles") or ("PUT", "/auth/org/roles/{id}") => new { name = "Reader", permissions = Array.Empty<string>() },
        _ => null,
    };

    [Theory]
    [MemberData(nameof(CompanyEndpoints))]
    public async Task Every_company_endpoint_answers_a_private_accounts_token_with_403_forbidden(string method, string path)   // criterion 14
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var token = (await SessionApi.LoginAsync(Client, Private, UserPassword)).AccessToken;

        using var response = await TenancyApi.Send(Client, new HttpMethod(method), path.Replace("{id}", user.ToString(), StringComparison.Ordinal), token, BodyFor(method, path));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space && c.Name == PersonalSpaces.Name, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user && m.CompanyId == space, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Get_auth_me_stays_open_to_a_private_account()   // spec 0010 → The company API
    {
        await CreatePrivateAccountAsync(Private);
        var token = (await SessionApi.LoginAsync(Client, Private, UserPassword)).AccessToken;

        using var response = await TenancyApi.Get(Client, OrgEndpoints.MePath, token);

        Assert.Equal("personal", (await TenancyApi.ReadOkAsync(response)).GetProperty("org_kind").GetString());
    }

    [Fact]
    public async Task A_token_that_says_organization_while_the_database_shows_a_personal_space_is_forbidden()   // "whatever the token says"
    {
        var company = await DevCompanyIdAsync();
        var user = await AddMemberAsync(company, "moved@poczta.test", "admin");
        var token = (await SessionApi.LoginAsync(Client, "moved@poczta.test", UserPassword)).AccessToken;   // org_kind organization, every permission
        await RemoveMembershipAsync(user);
        await InDbAsync(async db =>
        {
            PersonalSpaces.Add(db, user, StorableTimeOf());
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        using var org = await TenancyApi.Get(Client, OrgEndpoints.OrgPath, token);
        using var members = await TenancyApi.Get(Client, "/auth/org/members", token);

        await TenancyApi.AssertErrorAsync(org, HttpStatusCode.Forbidden, "forbidden");
        await TenancyApi.AssertErrorAsync(members, HttpStatusCode.Forbidden, "forbidden");   // not permissions_changed: the space decides
    }

    private DateTimeOffset StorableTimeOf() => Auth.Server.Email.StorableTime.Now(Clock);
}
```

`tests/Auth.IntegrationTests/PersonalSpaceCliTests.cs`:

```csharp
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
using Auth.Server.Seeding;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → The operator CLI: personal spaces are hidden.</summary>
public sealed class PersonalSpaceCliTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Private = "ola@poczta.test";

    [Fact]
    public async Task List_orgs_does_not_list_personal_spaces()   // criterion 14
    {
        var (_, space) = await CreatePrivateAccountAsync(Private);

        var run = await OperatorCli.RunAsync(Factory, "list-orgs");

        Assert.Equal(AdminCli.Done, run.Exit);
        Assert.DoesNotContain(space.ToString(), run.Out, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\tprivate\t", run.Out, StringComparison.Ordinal);
        Assert.Contains("\t" + DevUserSeeder.DefaultOrgName + "\t", run.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("invite")]
    [InlineData("remove-member")]
    [InlineData("delete-org")]
    public async Task A_command_given_the_id_of_a_personal_space_answers_not_found_and_changes_nothing(string command)   // criterion 14
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        string[] args = command switch
        {
            "invite" => ["invite", "--org", space.ToString(), "--email", "new@poczta.test", "--role", "private"],
            "remove-member" => ["remove-member", "--org", space.ToString(), "--email", Private, "--force"],
            _ => ["delete-org", "--org", space.ToString(), "--confirm", "private"],
        };

        var run = await OperatorCli.RunAsync(Factory, args);

        Assert.Equal(AdminCli.Refused, run.Exit);
        Assert.Equal("error: not_found", run.Error.Trim());
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user && m.CompanyId == space, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.Invites.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
    }
}
```

- [ ] **Step 2: See them fail.** Build, then run the two classes (command of Step 5): `GET /auth/org` answers `200` to the private account, the
CLI lists the space and `remove-member --force` removes the member.

- [ ] **Step 3: `CompanyAccess` and `/auth/me`.** In `src/Auth.Server/Tenancy/CompanyAccess.cs`:

(a) Add `using Auth.Infrastructure.Persistence;` as the first `using`.

(b) Replace

```csharp
    /// <param name="anyOf">The call needs one of these permissions; none means any member will do.</param>
    public static async Task<Access> AuthorizeAsync(HttpContext http, MembershipReader memberships, params string[] anyOf)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
```

with

```csharp
    /// <param name="anyOf">The call needs one of these permissions; none means any member of a company will do.</param>
    public static Task<Access> AuthorizeAsync(HttpContext http, MembershipReader memberships, params string[] anyOf) =>
        AuthorizeCoreAsync(http, memberships, allowPersonal: false, anyOf);

    /// <summary>
    /// The check of <c>GET /auth/me</c>: any member of any space, a private account included (spec 0010 → The company API). Every endpoint under
    /// <c>/auth/org</c> uses <see cref="AuthorizeAsync"/> instead.
    /// </summary>
    public static Task<Access> AuthorizeAnyKindAsync(HttpContext http, MembershipReader memberships) =>
        AuthorizeCoreAsync(http, memberships, allowPersonal: true, []);

    private static async Task<Access> AuthorizeCoreAsync(HttpContext http, MembershipReader memberships, bool allowPersonal, string[] anyOf)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
```

(c) Replace

```csharp
        var tenant = await memberships.ReadAsync(userId, http.RequestAborted);
        if (tenant is not null && (anyOf.Length == 0 || anyOf.Any(tenant.Holds)))
```

with

```csharp
        var tenant = await memberships.ReadAsync(userId, http.RequestAborted);

        // A personal space is no company (spec 0010). Read from the database, whatever the token says, and also where no permission is needed:
        // a check on permissions alone would let a private account through GET /auth/org.
        if (tenant is { Kind: CompanyKind.Personal } && !allowPersonal)
        {
            return new Access(null, ApiResults.Error(TenancyErrors.Forbidden));
        }

        if (tenant is not null && (anyOf.Length == 0 || anyOf.Any(tenant.Holds)))
```

(d) In the class summary, after `<item>Neither grants it: <c>403 forbidden</c>.</item>` add the line
`/// <item>The caller's space is personal (spec 0010): <c>403 forbidden</c>, before anything else.</item>`.

In `src/Auth.Server/Api/OrgEndpoints.cs`, in `MeAsync`, replace `var access = await CompanyAccess.AuthorizeAsync(http, memberships);` with
`var access = await CompanyAccess.AuthorizeAnyKindAsync(http, memberships);`. (`GetAsync` keeps `AuthorizeAsync`.)

- [ ] **Step 4: Hidden from every company service and from the CLI.** In `src/Auth.Server/Tenancy/CompanyLock.cs` replace

```csharp
    /// <summary>Locks the company's row until the transaction ends; <see langword="false"/> when there is no such company.</summary>
    public static async Task<bool> AcquireAsync(AuthDbContext db, Guid companyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var locked = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM "Companies" WHERE "Id" = {companyId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
```

with

```csharp
    /// <summary>
    /// Locks the company's row until the transaction ends; <see langword="false"/> when there is no such company. A personal space (spec 0010)
    /// is no company here: nobody locks one, so no company service and no operator command ever acts on it (they answer <c>not_found</c>).
    /// </summary>
    public static async Task<bool> AcquireAsync(AuthDbContext db, Guid companyId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var organization = (short)CompanyKind.Organization;
        var locked = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM "Companies" WHERE "Id" = {companyId} AND "Kind" = {organization} FOR UPDATE""")
            .ToListAsync(cancellationToken);
```

In `src/Auth.Server/Tenancy/OperatorCommands.cs` replace

```csharp
        var rows = await db.Companies.AsNoTracking()
            .Select(c => new OrgListing(c.Id, c.Name, db.Memberships.Count(m => m.CompanyId == c.Id)))
```

with

```csharp
        // Personal spaces (spec 0010) are not companies: the operator never sees one.
        var rows = await db.Companies.AsNoTracking()
            .Where(c => c.Kind == CompanyKind.Organization)
            .Select(c => new OrgListing(c.Id, c.Name, db.Memberships.Count(m => m.CompanyId == c.Id)))
```

In `src/Auth.Server/Seeding/DevUserSeeder.cs` replace

```csharp
        var existing = await db.Companies.AsNoTracking().OrderBy(c => c.CreatedAt).FirstOrDefaultAsync(c => c.Name == name, ct);
```

with

```csharp
        // A personal space is named "private" (spec 0010): only a company is the development company.
        var existing = await db.Companies.AsNoTracking().OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(c => c.Name == name && c.Kind == CompanyKind.Organization, ct);
```

- [ ] **Step 5: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.PersonalSpaceRefusalTests -class Auth.IntegrationTests.PersonalSpaceCliTests -class Auth.IntegrationTests.OrgEndpointTests -class Auth.IntegrationTests.OrgMemberTests -class Auth.IntegrationTests.OrgRoleTests -class Auth.IntegrationTests.OrgInviteTests -class Auth.IntegrationTests.AdminCliTests -class Auth.IntegrationTests.DevSeedTests -class Auth.IntegrationTests.DevCompanySeedTests -class Auth.IntegrationTests.InviteAcceptTests`
passes.

- [ ] **Step 6: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): the company API refuses a personal space and the operator never sees one`.

### Task 5: The mail texts

**Files:**
- Modify: `src/Auth.Server/Email/MailTexts.cs`, `src/Auth.Server/Email/MailComposer.cs`
- Create (tests): `tests/Auth.IntegrationTests/PrivateMailComposerTests.cs`

**Interfaces:**
- Consumes: `MailSettings`, `MailKind`.
- Produces: `MailText(..., string Ignore, string? Paragraph = null)`; `AccountExistsText(Subject, Greeting, Intro, SignInHint, Deliberate, Ignore)`; `MailTexts.PrivateSignup(string locale)`, `MailTexts.InvitationToPrivateAccount(string locale)`; `MailComposer.Compose(MailKind kind, string recipient, string token, string? companyName = null, string? roleName = null, bool privateAccount = false)` — for `Signup` the private variant needs no company; for `Invitation` it adds the paragraph; `ComposeAccountExists` writes the new sentence after the sign-in hint.

**Contract counts:** none.

- [ ] **Step 1: Write the failing tests.** `tests/Auth.IntegrationTests/PrivateMailComposerTests.cs`:

```csharp
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Mails and Invitations: the new texts, in English and Polish, HTML and text.</summary>
public sealed class PrivateMailComposerTests
{
    private const string Token = "abcDEF123_-abcDEF123_-abcDEF123_-abcDEF123_";
    private static readonly string[] Holes = ["{0}", "{1}", "{2}"];   // a field, not an inline array: CA1861 under -warnaserror

    private const string PrivateIntroEn = "Someone asked to create a private account on speech-to-mail with this email address. If it was you, use the link below to finish signing up.";
    private const string PrivateIntroPl = "Ktoś, podając ten adres e-mail, poprosił o założenie konta prywatnego w speech-to-mail. Jeśli to Ty, użyj poniższego linku, aby dokończyć rejestrację.";
    private const string DeliberateEn = "If you want a new account on purpose, delete the one you have first, or sign up with another email address.";
    private const string DeliberatePl = "Jeśli celowo chcesz założyć nowe konto, najpierw usuń dotychczasowe albo zarejestruj się z innym adresem e-mail.";

    private static MailComposer Composer(string locale = "en") =>
        new(new MailSettings
        {
            AppName = "speech-to-mail",
            Locale = locale,
            ResetPasswordUrl = new Uri("https://app.example.com/reset"),
            VerifyEmailUrl = new Uri("https://app.example.com/verify"),
            AcceptInviteUrl = new Uri("https://app.example.com/invite"),
            CompleteSignupUrl = new Uri("https://app.example.com/complete-signup"),
            FromAddress = "no-reply@example.com",
            Smtp = new SmtpSettings { Host = "smtp.invalid", Port = 587, Security = SmtpSecurity.StartTls },
        });

    private static void AssertHtmlHoldsTheText(ComposedMail mail)
    {
        var encoder = HtmlEncoder.Create(UnicodeRanges.All);
        Assert.All(
            mail.TextBody.Split("\r\n").Where(line => line.Length > 0),
            line => Assert.Contains(encoder.Encode(line), mail.HtmlBody, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("en", PrivateIntroEn, "Complete your sign-up for speech-to-mail")]
    [InlineData("pl", PrivateIntroPl, "Dokończ rejestrację w speech-to-mail")]
    public void The_private_sign_up_mail_has_the_intro_without_a_company_and_the_link(string locale, string intro, string subject)   // criteria 4, 31
    {
        var mail = Composer(locale).Compose(MailKind.Signup, "ola@poczta.test", Token, privateAccount: true);

        Assert.Equal(subject, mail.Subject);
        Assert.Contains(intro, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("https://app.example.com/complete-signup?token=" + Token, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("href=\"https://app.example.com/complete-signup?token=" + Token + "\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("{", mail.Subject + mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
        AssertHtmlHoldsTheText(mail);
    }

    [Theory]
    [InlineData("en", DeliberateEn)]
    [InlineData("pl", DeliberatePl)]
    public void The_account_exists_mail_holds_the_new_sentence_after_the_sign_in_hint(string locale, string sentence)   // criteria 5, 31
    {
        var mail = Composer(locale).ComposeAccountExists("ola@poczta.test");

        Assert.Contains(sentence, mail.TextBody, StringComparison.Ordinal);
        var hint = string.Format(System.Globalization.CultureInfo.InvariantCulture, MailTexts.AccountExists(locale).SignInHint, "speech-to-mail");
        Assert.True(mail.TextBody.IndexOf(hint, StringComparison.Ordinal) < mail.TextBody.IndexOf(sentence, StringComparison.Ordinal));
        Assert.DoesNotContain("://", mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
        AssertHtmlHoldsTheText(mail);
    }

    [Fact]
    public void The_invitation_to_a_private_account_in_english_has_the_changed_intro_and_the_paragraph()   // criteria 16, 31
    {
        var mail = Composer().Compose(MailKind.Invitation, "ola@poczta.test", Token, "Acme", "user", privateAccount: true);

        Assert.Contains("You have been invited to join Acme on speech-to-mail as user. Use the link below to accept the invitation.", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(
            "You already have a private account on speech-to-mail with this email address. If you accept, you move to Acme: the space of your "
            + "private account is deleted, with everything in it. Your account itself stays, with its email address and its password, and becomes a member of Acme.",
            mail.TextBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("choose a password", mail.TextBody, StringComparison.Ordinal);
        AssertHtmlHoldsTheText(mail);
    }

    [Fact]
    public void The_invitation_to_a_private_account_in_polish_has_the_changed_intro_and_the_paragraph()   // criteria 16, 31
    {
        var mail = Composer("pl").Compose(MailKind.Invitation, "ola@poczta.test", Token, "Żółw i Spółka", "użytkownik", privateAccount: true);

        Assert.Contains("Zaproszono Cię do firmy Żółw i Spółka w speech-to-mail jako użytkownik. Użyj poniższego linku, aby przyjąć zaproszenie.", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(
            "Masz już konto prywatne w speech-to-mail z tym adresem e-mail. Jeśli przyjmiesz zaproszenie, przejdziesz do firmy Żółw i Spółka: przestrzeń "
            + "Twojego konta prywatnego zostanie usunięta razem z całą zawartością. Samo konto zostaje, z tym samym adresem e-mail i hasłem, i stanie się "
            + "kontem członka firmy Żółw i Spółka.",
            mail.TextBody,
            StringComparison.Ordinal);
        Assert.Contains("Żółw i Spółka", mail.HtmlBody, StringComparison.Ordinal);   // letters stay letters
        AssertHtmlHoldsTheText(mail);
    }

    [Fact]
    public void The_company_and_the_role_are_encoded_in_the_html_of_the_paragraph_and_never_in_the_subject()   // criterion 31
    {
        var mail = Composer().Compose(MailKind.Invitation, "ola@poczta.test", Token, "Tom & <Jerry>", "<b>boss</b>", privateAccount: true);

        Assert.Contains("If you accept, you move to Tom & <Jerry>:", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("If you accept, you move to Tom &amp; &lt;Jerry&gt;:", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;boss&lt;/b&gt;", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<Jerry>", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Jerry", mail.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Any_other_invitation_is_the_mail_of_spec_0005()   // criterion 16
    {
        var mail = Composer().Compose(MailKind.Invitation, "ola@poczta.test", Token, "Acme", "user");

        Assert.Contains("Use the link below to choose a password and accept the invitation.", mail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("private account", mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public void Every_language_has_every_new_text(string locale)   // criterion 31
    {
        var signup = MailTexts.PrivateSignup(locale);
        Assert.Contains("{0}", signup.Intro, StringComparison.Ordinal);
        Assert.DoesNotContain("{1}", signup.Intro, StringComparison.Ordinal);
        Assert.Null(signup.Paragraph);

        var invitation = MailTexts.InvitationToPrivateAccount(locale);
        Assert.All(Holes, hole => Assert.Contains(hole, invitation.Intro, StringComparison.Ordinal));
        Assert.NotNull(invitation.Paragraph);
        Assert.Contains("{0}", invitation.Paragraph, StringComparison.Ordinal);
        Assert.Contains("{1}", invitation.Paragraph, StringComparison.Ordinal);
        Assert.Equal(MailTexts.For(locale, MailKind.Invitation).Subject, invitation.Subject);

        Assert.False(string.IsNullOrWhiteSpace(MailTexts.AccountExists(locale).Deliberate));
    }

    [Fact]
    public void A_private_sign_up_needs_no_company_and_an_organization_sign_up_still_does()
    {
        _ = Composer().Compose(MailKind.Signup, "ola@poczta.test", Token, privateAccount: true);

        Assert.Throws<ArgumentException>(() => Composer().Compose(MailKind.Signup, "ola@poczta.test", Token));
    }
}
```

- [ ] **Step 2: See them fail.** The build fails: `privateAccount`, `MailTexts.PrivateSignup`, `InvitationToPrivateAccount`, `Paragraph` and `Deliberate` do not exist.

- [ ] **Step 3: The texts.** In `src/Auth.Server/Email/MailTexts.cs`:

(a) Replace

```csharp
public sealed record MailText(string Subject, string Greeting, string Intro, string Button, string Validity, string LinkHint, string Ignore);
```

with

```csharp
/// <param name="Paragraph">
/// A paragraph after the intro, or none. The invitation to an address that has a private account (spec 0010) has one; it holds <c>{0}</c> for
/// the application and <c>{1}</c> for the company.
/// </param>
public sealed record MailText(
    string Subject, string Greeting, string Intro, string Button, string Validity, string LinkHint, string Ignore, string? Paragraph = null);
```

(b) Replace

```csharp
public sealed record AccountExistsText(string Subject, string Greeting, string Intro, string SignInHint, string Ignore);
```

with

```csharp
/// <param name="Deliberate">The sentence of spec 0010, after the sign-in hint: how to get a new account on purpose.</param>
public sealed record AccountExistsText(string Subject, string Greeting, string Intro, string SignInHint, string Deliberate, string Ignore);
```

(c) In `AccountExists`, after the `SignInHint:` line of `"en"` add
`            Deliberate: "If you want a new account on purpose, delete the one you have first, or sign up with another email address.",`
and after the `SignInHint:` line of `"pl"` add
`            Deliberate: "Jeśli celowo chcesz założyć nowe konto, najpierw usuń dotychczasowe albo zarejestruj się z innym adresem e-mail.",`.
Replace its doc comment `/// <summary>The mail an address gets when someone signs up with it and its account belongs to a company, or only looks like the typed address (spec 0009 → Mails, Amendment 3).</summary>` with
`/// <summary>The mail an address gets when someone signs up with it and it has an account, of either kind (spec 0009 → Mails; spec 0010 → Mails).</summary>`.

(d) Before the closing brace of `MailTexts` add

```csharp

    /// <summary>
    /// "Complete your sign-up" for a private person (spec 0010 → Mails): the subject, greeting, button, validity, link hint and ignore line of
    /// spec 0009, with an intro that names no company (<c>{0}</c> is the application).
    /// </summary>
    public static MailText PrivateSignup(string locale) => For(locale, MailKind.Signup) with
    {
        Intro = locale switch
        {
            "en" => "Someone asked to create a private account on {0} with this email address. If it was you, use the link below to finish signing up.",
            "pl" => "Ktoś, podając ten adres e-mail, poprosił o założenie konta prywatnego w {0}. Jeśli to Ty, użyj poniższego linku, aby dokończyć rejestrację.",
            _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No mail text for locale '{locale}'."),
        },
    };

    /// <summary>
    /// The invitation to an address that has a private account (spec 0010 → Invitations): the intro's last sentence asks no password, and a
    /// paragraph says what accepting does. <c>{0}</c> the application, <c>{1}</c> the company, <c>{2}</c> the role.
    /// </summary>
    public static MailText InvitationToPrivateAccount(string locale) => locale switch
    {
        "en" => For(locale, MailKind.Invitation) with
        {
            Intro = "You have been invited to join {1} on {0} as {2}. Use the link below to accept the invitation.",
            Paragraph = "You already have a private account on {0} with this email address. If you accept, you move to {1}: the space of your private "
                + "account is deleted, with everything in it. Your account itself stays, with its email address and its password, and becomes a member of {1}.",
        },
        "pl" => For(locale, MailKind.Invitation) with
        {
            Intro = "Zaproszono Cię do firmy {1} w {0} jako {2}. Użyj poniższego linku, aby przyjąć zaproszenie.",
            Paragraph = "Masz już konto prywatne w {0} z tym adresem e-mail. Jeśli przyjmiesz zaproszenie, przejdziesz do firmy {1}: przestrzeń Twojego "
                + "konta prywatnego zostanie usunięta razem z całą zawartością. Samo konto zostaje, z tym samym adresem e-mail i hasłem, i stanie się kontem "
                + "członka firmy {1}.",
        },
        _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No mail text for locale '{locale}'."),
    };
```

- [ ] **Step 4: The composer.** In `src/Auth.Server/Email/MailComposer.cs`:

(a) Replace

```csharp
    /// <param name="roleName">The role of an invitation; required for <see cref="MailKind.Invitation"/>, ignored otherwise.</param>
    public ComposedMail Compose(MailKind kind, string recipient, string token, string? companyName = null, string? roleName = null)
    {
```

with

```csharp
    /// <param name="roleName">The role of an invitation; required for <see cref="MailKind.Invitation"/>, ignored otherwise.</param>
    /// <param name="privateAccount">
    /// Spec 0010. For <see cref="MailKind.Signup"/>: the private variant, which names no company. For <see cref="MailKind.Invitation"/>: the
    /// invited address has a private account, and the mail says what accepting does to it. Ignored otherwise.
    /// </param>
    public ComposedMail Compose(
        MailKind kind, string recipient, string token, string? companyName = null, string? roleName = null, bool privateAccount = false)
    {
```

(b) Replace

```csharp
        if (kind == MailKind.Signup && companyName is null)
        {
            throw new ArgumentException("A sign-up names its company.", nameof(companyName));
        }

        var text = MailTexts.For(settings.Locale, kind);
```

with

```csharp
        if (kind == MailKind.Signup && companyName is null && !privateAccount)
        {
            throw new ArgumentException("An organization sign-up names its company.", nameof(companyName));
        }

        var text = (kind, privateAccount) switch
        {
            (MailKind.Signup, true) => MailTexts.PrivateSignup(settings.Locale),
            (MailKind.Invitation, true) => MailTexts.InvitationToPrivateAccount(settings.Locale),
            _ => MailTexts.For(settings.Locale, kind),
        };
```

(c) Replace

```csharp
        var intro = string.Format(CultureInfo.InvariantCulture, text.Intro, settings.AppName, companyName, roleName);

        return new ComposedMail
        {
            To = recipient,
            Subject = subject,
            TextBody = string.Join(
                "\r\n",
                text.Greeting, "", intro, "", link, "", text.Validity, text.Ignore, ""),
```

with

```csharp
        var intro = string.Format(CultureInfo.InvariantCulture, text.Intro, settings.AppName, companyName, roleName);
        // Formatted first and encoded whole, like the intro: the company and the role in it are encoded in the HTML part.
        var paragraph = text.Paragraph is null ? null : string.Format(CultureInfo.InvariantCulture, text.Paragraph, settings.AppName, companyName, roleName);
        var paragraphHtml = paragraph is null ? "" : "\n<p>" + Html.Encode(paragraph) + "</p>";
        var lines = new List<string> { text.Greeting, "", intro, "" };
        if (paragraph is not null)
        {
            lines.Add(paragraph);
            lines.Add("");
        }

        lines.AddRange([link, "", text.Validity, text.Ignore, ""]);

        return new ComposedMail
        {
            To = recipient,
            Subject = subject,
            TextBody = string.Join("\r\n", lines),
```

and in the HTML template of `Compose` replace the line `                <p>{Html.Encode(intro)}</p>` (the first of the two methods' identical lines: the one followed by the button line) with `                <p>{Html.Encode(intro)}</p>{paragraphHtml}`.

(d) In `ComposeAccountExists`, replace

```csharp
        var hint = string.Format(CultureInfo.InvariantCulture, text.SignInHint, settings.AppName);

        return new ComposedMail
        {
            To = recipient,
            Subject = subject,
            TextBody = string.Join("\r\n", text.Greeting, "", intro, "", hint, "", text.Ignore, ""),
```

with

```csharp
        var hint = string.Format(CultureInfo.InvariantCulture, text.SignInHint, settings.AppName);

        return new ComposedMail
        {
            To = recipient,
            Subject = subject,
            TextBody = string.Join("\r\n", text.Greeting, "", intro, "", hint, "", text.Deliberate, "", text.Ignore, ""),
```

and in its HTML replace `                <p>{Html.Encode(hint)}</p>` with

```csharp
                <p>{Html.Encode(hint)}</p>
                <p>{Html.Encode(text.Deliberate)}</p>
```

and its doc comment's first sentence `The mail an address gets when someone signs up with it and its account belongs to a company, or only looks like the typed address (spec 0009 → Mails, Amendment 3):` becomes `The mail an address gets when someone signs up with it and it has an account of either kind (spec 0010 → Mails):`; add at its end `It ends with how to get a new account on purpose (spec 0010, Decision 6).`

- [ ] **Step 5: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.PrivateMailComposerTests -class Auth.IntegrationTests.SignupMailComposerTests -class Auth.IntegrationTests.InvitationMailComposerTests -class Auth.IntegrationTests.MailComposerTests -class Auth.IntegrationTests.SignupDispatchTests -class Auth.IntegrationTests.InvitationDispatchTests`
passes.

- [ ] **Step 6: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files (Polish letters are meant; nothing
else beyond ASCII). Proposed subject: `feat(mail): the private sign-up mail, the sentence of the account mail and the invitation paragraph`.

### Task 6: The deletion routine and the two audit kinds

**Files:**
- Create: `src/Auth.Server/Tenancy/AccountDeletion.cs`
- Modify: `src/Auth.Server/Audit/AuditKinds.cs`, `src/Auth.Server/Tenancy/TenancyServices.cs`
- Modify (tests): `tests/Auth.IntegrationTests/AuditTableTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/AccountDeletionTests.cs`

**Interfaces:**
- Consumes: `AuthDbContext`, `AuditLog`, `Actor`, `CompanyKind`, `LoginIdentifier.HashOf`, `MailKind`, OpenIddict's `OpenIddictEntityFrameworkCoreToken` and `OpenIddictEntityFrameworkCoreAuthorization` (namespace `OpenIddict.EntityFrameworkCore.Models`, transitive from `Auth.Infrastructure`).
- Produces: `enum AccountDeletionReason { Self, MemberRemoved, OrgDeleted, Upgrade }`; `AccountDeletion` (scoped, in `AddTenancy`, so the CLI has it): `static string NameOf(AccountDeletionReason)`, `Task<int> DeleteAsync(Guid userId, AccountDeletionReason reason, Actor? actor, CancellationToken)`, `Task<int> DeleteAsync(IReadOnlyCollection<Guid> userIds, AccountDeletionReason reason, Actor? actor, CancellationToken)` (returns how many were deleted; an account already gone is skipped); `AuditKinds.AccountDeleted = "account.deleted"`, `AuditKinds.AccountDeleteRefused = "account.delete_refused"` appended to `All`.

**Contract counts:** `AuditTableTests` — the method `The_kinds_are_the_twenty_four_of_the_contract` becomes `The_kinds_are_the_twenty_six_of_the_contract` with the two kinds appended.

**Decisions the plan takes here:**
- The routine takes no lock and saves nothing itself but its own set-based statements and the staged rows (the caller's save writes them): the caller owns the transaction and the locks (spec, Locks).
- One routine serves one account and many: company deletion hands it every member at once, so a company of any size costs a fixed number of statements, with one row staged per account.
- `ActorUserId` of the row: the account itself for `self`; otherwise the actor's (an admin), or none for the operator and the start-up step; the operator's actor adds `details.via` `cli` by itself (`AuditLog`).

- [ ] **Step 1: Write the failing tests.** `tests/Auth.IntegrationTests/AccountDeletionTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → What an account deletion removes: the one routine, called here straight, in a transaction of the test.</summary>
public sealed class AccountDeletionTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Private = "ola@poczta.test";

    private async Task<int> DeleteAsync(IReadOnlyCollection<Guid> users, AccountDeletionReason reason, Actor? actor)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var deleted = await scope.ServiceProvider.GetRequiredService<AccountDeletion>().DeleteAsync(users, reason, actor, TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return deleted;
    }

    private Task<int> CountAsync(Func<AuthDbContext, Task<int>> count) => InDbAsync(count);

    [Fact]
    public async Task Deleting_a_private_account_removes_everything_of_it_and_keeps_its_audit_rows_invitations_and_mail_limits()   // criterion 21
    {
        var cancellation = TestContext.Current.CancellationToken;
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var subject = user.ToString();
        _ = await SessionApi.LoginAsync(Client, Private, UserPassword);
        _ = await SessionApi.LoginAsync(Client, Private, UserPassword);   // two sessions: tokens and authorizations
        await EnqueueAsync(MailKind.PasswordReset, Private);
        await DispatchAsync();                                              // a mail link (EmailTokens)
        await EnqueueAsync(MailKind.EmailVerification, Private);           // a queued request of the address, not an invitation
        var acme = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(acme, Private, "user");
        await EnqueueInvitationAsync(invite, Private);                     // an invitation to the address and its queued mail
        await PendingSignupSeed.IssueAsync(Factory.Services, Private, "Firma");
        using (var wrong = await LoginApi.Login(Client, Private, WrongPassword))
        {
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, wrong.StatusCode);   // a login streak
        }

        await InDbAsync(async db =>
        {
            db.UserLogins.Add(new IdentityUserLogin<Guid> { LoginProvider = "test", ProviderKey = "key", ProviderDisplayName = "Test", UserId = user });
            db.UserClaims.Add(new IdentityUserClaim<Guid> { UserId = user, ClaimType = "test", ClaimValue = "value" });
            db.UserTokens.Add(new IdentityUserToken<Guid> { UserId = user, LoginProvider = "test", Name = "test", Value = "value" });
            return await db.SaveChangesAsync(cancellation);
        });
        Assert.True(await CountAsync(db => db.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync(t => t.Subject == subject, cancellation)) > 0);
        Assert.Equal(1, await CountAsync(db => db.EmailTokens.CountAsync(t => t.UserId == user, cancellation)));
        Assert.Equal(1, await CountAsync(db => db.LoginStreaks.CountAsync(cancellation)));
        var limits = await CountAsync(db => db.MailRequestLimits.CountAsync(cancellation));
        var auditBefore = await CountAsync(db => db.AuditEvents.CountAsync(a => a.SubjectUserId == user, cancellation));

        Assert.Equal(1, await DeleteAsync([user], AccountDeletionReason.Self, actor: null));

        Assert.Equal(0, await CountAsync(db => db.Users.CountAsync(u => u.Id == user, cancellation)));
        Assert.Equal(0, await CountAsync(db => db.Memberships.CountAsync(m => m.UserId == user, cancellation)));
        Assert.Equal(0, await CountAsync(db => db.Companies.CountAsync(c => c.Id == space, cancellation)));
        Assert.Equal(0, await CountAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == space, cancellation)));
        Assert.Equal(0, await CountAsync(db => db.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync(t => t.Subject == subject, cancellation)));
        Assert.Equal(0, await CountAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>().CountAsync(a => a.Subject == subject, cancellation)));
        Assert.Equal(0, await CountAsync(db => db.UserLogins.CountAsync(l => l.UserId == user, cancellation)));
        Assert.Equal(0, await CountAsync(db => db.UserClaims.CountAsync(c => c.UserId == user, cancellation)));
        Assert.Equal(0, await CountAsync(db => db.UserTokens.CountAsync(t => t.UserId == user, cancellation)));
        Assert.Equal(0, await CountAsync(db => db.EmailTokens.CountAsync(t => t.UserId == user, cancellation)));
        Assert.Equal(0, await CountAsync(db => db.MailRequests.CountAsync(r => r.Kind != MailKind.Invitation, cancellation)));
        Assert.Equal(0, await CountAsync(db => db.PendingSignups.CountAsync(cancellation)));
        Assert.Equal(0, await CountAsync(db => db.LoginStreaks.CountAsync(cancellation)));

        // Kept: the invitation of a company to the address and its mail, the mail limits, and every audit row that names the account.
        Assert.Equal(1, await CountAsync(db => db.Invites.CountAsync(i => i.Id == invite, cancellation)));
        Assert.Equal(1, await CountAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.Invitation, cancellation)));
        Assert.Equal(limits, await CountAsync(db => db.MailRequestLimits.CountAsync(cancellation)));
        Assert.Equal(auditBefore + 1, await CountAsync(db => db.AuditEvents.CountAsync(a => a.SubjectUserId == user, cancellation)));

        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal(user, row.ActorUserId);
        Assert.Equal(user, row.SubjectUserId);
        Assert.Equal(Private, row.SubjectEmail);
        Assert.Equal(space, row.OrgId);
        Assert.Equal("private", row.OrgName);
        Assert.Equal("self", AuditApi.Text(row, "reason"));
        Assert.Equal(["reason"], AuditApi.DetailNames(row));
        Assert.Null(row.ClientIp);   // no request: the test called the routine
    }

    [Fact]
    public async Task Deleting_a_company_member_keeps_the_company_and_its_invitations_lose_their_sender()
    {
        var (company, admin, _) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var worker = await AddMemberAsync(company, "worker@acme.test", "admin");
        var invite = await AddInviteAsync(company, "new@acme.test", "user", invitedBy: worker);

        Assert.Equal(1, await DeleteAsync([worker], AccountDeletionReason.MemberRemoved, Actor.Of(await TenantOfAsync(admin))));

        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == company, TestContext.Current.CancellationToken)));
        Assert.Null((await InviteAsync(invite))!.InvitedBy);
        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(worker, row.SubjectUserId);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal("member_removed", AuditApi.Text(row, "reason"));
    }

    [Fact]
    public async Task The_operator_is_marked_cli_and_the_start_up_step_has_no_actor_and_no_space()
    {
        var (company, _, _) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var member = await AddMemberAsync(company, "worker@acme.test", "user");
        var lonely = (await CreateUserAsync("lonely@example.test", confirmed: true, member: false)).Id;

        await DeleteAsync([member], AccountDeletionReason.OrgDeleted, Actor.Operator);
        await DeleteAsync([lonely], AccountDeletionReason.Upgrade, actor: null);

        var rows = await AuditAsync(AuditKinds.AccountDeleted);
        var byOperator = Assert.Single(rows, r => r.SubjectUserId == member);
        Assert.Null(byOperator.ActorUserId);
        Assert.Equal("cli", AuditApi.Text(byOperator, "via"));
        Assert.Equal("org_deleted", AuditApi.Text(byOperator, "reason"));
        var upgrade = Assert.Single(rows, r => r.SubjectUserId == lonely);
        Assert.Null(upgrade.ActorUserId);
        Assert.Null(upgrade.OrgId);
        Assert.Null(upgrade.OrgName);
        Assert.Equal(["reason"], AuditApi.DetailNames(upgrade));
        Assert.Equal("upgrade", AuditApi.Text(upgrade, "reason"));
    }

    [Fact]
    public async Task An_account_that_is_already_gone_is_skipped_and_writes_nothing()
    {
        Assert.Equal(0, await DeleteAsync([Guid.NewGuid()], AccountDeletionReason.Self, actor: null));

        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleted));
    }

    [Fact]
    public async Task Many_accounts_at_once_get_one_row_each_and_leave_the_others()
    {
        var (company, admin, _) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var members = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            members.Add(await AddMemberAsync(company, $"worker{i}@acme.test", "user"));
        }

        Assert.Equal(3, await DeleteAsync(members, AccountDeletionReason.OrgDeleted, Actor.Of(await TenantOfAsync(admin))));

        Assert.Equal(3, (await AuditAsync(AuditKinds.AccountDeleted)).Count);
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Id == admin, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.CompanyId == company, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData(AccountDeletionReason.Self, "self")]
    [InlineData(AccountDeletionReason.MemberRemoved, "member_removed")]
    [InlineData(AccountDeletionReason.OrgDeleted, "org_deleted")]
    [InlineData(AccountDeletionReason.Upgrade, "upgrade")]
    public void The_reasons_have_the_names_of_the_contract(AccountDeletionReason reason, string name)   // spec 0010 → Audit events
    {
        Assert.Equal(name, AccountDeletion.NameOf(reason));
    }
}
```

In `tests/Auth.IntegrationTests/AuditTableTests.cs` replace

```csharp
    public void The_kinds_are_the_twenty_four_of_the_contract()
    {
        Assert.Equal(
            [
                "login.succeeded", "login.failed", "login.locked", "logout", "refresh.reuse_detected",
                "password.reset_requested", "password.reset", "email.verified",
                "invite.sent", "invite.resent", "invite.accepted", "invite.cancelled",
                "member.removed", "member.role_changed", "role.created", "role.updated", "role.deleted",
                "org.created", "org.renamed", "org.deleted", "org.delete_refused", "rate_limit.hit",
                "signup.requested", "signup.completed",
            ],
```

with

```csharp
    public void The_kinds_are_the_twenty_six_of_the_contract()   // spec 0010, criterion 29
    {
        Assert.Equal(
            [
                "login.succeeded", "login.failed", "login.locked", "logout", "refresh.reuse_detected",
                "password.reset_requested", "password.reset", "email.verified",
                "invite.sent", "invite.resent", "invite.accepted", "invite.cancelled",
                "member.removed", "member.role_changed", "role.created", "role.updated", "role.deleted",
                "org.created", "org.renamed", "org.deleted", "org.delete_refused", "rate_limit.hit",
                "signup.requested", "signup.completed",
                "account.deleted", "account.delete_refused",
            ],
```

- [ ] **Step 2: See them fail.** The build fails: `AccountDeletion`, `AccountDeletionReason` and `AuditKinds.AccountDeleted` do not exist.

- [ ] **Step 3: The kinds.** In `src/Auth.Server/Audit/AuditKinds.cs` replace

```csharp
    public const string SignupCompleted = "signup.completed";
```

with

```csharp
    public const string SignupCompleted = "signup.completed";
    public const string AccountDeleted = "account.deleted";
    public const string AccountDeleteRefused = "account.delete_refused";
```

and replace

```csharp
        SignupRequested, SignupCompleted,
    ];
```

with

```csharp
        SignupRequested, SignupCompleted,
        AccountDeleted, AccountDeleteRefused,
    ];
```

and in the class summary `(spec 0008 and spec 0009 → Audit log)` becomes `(specs 0008, 0009 and 0010 → Audit log)`.

- [ ] **Step 4: The routine.** `src/Auth.Server/Tenancy/AccountDeletion.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.Server.Tenancy;

/// <summary>Why an account is deleted (spec 0010 → Accounts are never left without a space); its name is <c>details.reason</c>.</summary>
public enum AccountDeletionReason
{
    Self,
    MemberRemoved,
    OrgDeleted,
    Upgrade,
}

/// <summary>
/// Deletes accounts (spec 0010 → What an account deletion removes): the one routine behind a self-deletion, the removal of a member, the deletion
/// of a company and the start-up step. It runs in the caller's transaction, under the caller's locks, by set-based statements, and stages one
/// <c>account.deleted</c> row per account for the caller's save. For each account it deletes: the queued mail requests of its address other than
/// invitations, then the pending sign-up of its address (the mail sender locks them in that order; the other one can deadlock), its login streak,
/// its refresh tokens and then their authorizations (deleted, not only revoked: they name the account; there is no cascade between them), the
/// account (its Identity rows, its mail links and its membership go by cascade; invitations it sent lose their sender) and, for a personal space,
/// the space with its role row. It keeps the audit rows, the invitations of companies to the address and the mail-limit counters.
/// </summary>
public sealed class AccountDeletion(AuthDbContext db, AuditLog audit)
{
    public static string NameOf(AccountDeletionReason reason) => reason switch
    {
        AccountDeletionReason.Self => "self",
        AccountDeletionReason.MemberRemoved => "member_removed",
        AccountDeletionReason.OrgDeleted => "org_deleted",
        AccountDeletionReason.Upgrade => "upgrade",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown reason."),
    };

    /// <summary>Deletes one account; see the other overload.</summary>
    public Task<int> DeleteAsync(Guid userId, AccountDeletionReason reason, Actor? actor, CancellationToken cancellationToken) =>
        DeleteAsync([userId], reason, actor, cancellationToken);

    /// <summary>
    /// Deletes the accounts; returns how many there were (one already gone is skipped, with no row). <paramref name="actor"/> is the admin who
    /// removes a member or deletes the company, <see cref="Actor.Operator"/> for the CLI, or <see langword="null"/>; for
    /// <see cref="AccountDeletionReason.Self"/> the account itself is the actor of its row.
    /// </summary>
    public async Task<int> DeleteAsync(
        IReadOnlyCollection<Guid> userIds, AccountDeletionReason reason, Actor? actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        List<Guid> ids = [.. userIds.Distinct()];
        var accounts = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Email, u.NormalizedEmail })
            .ToListAsync(cancellationToken);
        if (accounts.Count == 0)
        {
            return 0;
        }

        List<Guid> found = [.. accounts.Select(a => a.Id)];

        // The space each account loses, read before anything is deleted: the row names it.
        var spaces = await (
            from membership in db.Memberships.AsNoTracking()
            join company in db.Companies.AsNoTracking() on membership.CompanyId equals company.Id
            where found.Contains(membership.UserId)
            orderby membership.JoinedAt
            select new { membership.UserId, company.Id, company.Name, company.Kind })
            .ToListAsync(cancellationToken);
        var lost = spaces.GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => g.First());

        List<string> addresses = [.. accounts.Select(a => a.NormalizedEmail).OfType<string>().Distinct(StringComparer.Ordinal)];
        await db.MailRequests
            .Where(r => addresses.Contains(r.NormalizedEmail) && r.Kind != MailKind.Invitation)
            .ExecuteDeleteAsync(cancellationToken);
        await db.PendingSignups.Where(p => addresses.Contains(p.NormalizedEmail)).ExecuteDeleteAsync(cancellationToken);
        List<byte[]> streaks = [.. addresses.Select(LoginIdentifier.HashOf)];
        await db.LoginStreaks.Where(s => streaks.Contains(s.IdentifierHash)).ExecuteDeleteAsync(cancellationToken);

        List<string> subjects = [.. found.Select(id => id.ToString())];
        await db.Set<OpenIddictEntityFrameworkCoreToken>()
            .Where(t => t.Subject != null && subjects.Contains(t.Subject))
            .ExecuteDeleteAsync(cancellationToken);
        await db.Set<OpenIddictEntityFrameworkCoreAuthorization>()
            .Where(a => a.Subject != null && subjects.Contains(a.Subject))
            .ExecuteDeleteAsync(cancellationToken);

        await db.Users.Where(u => found.Contains(u.Id)).ExecuteDeleteAsync(cancellationToken);

        List<Guid> personal = [.. lost.Values.Where(s => s.Kind == CompanyKind.Personal).Select(s => s.Id)];
        if (personal.Count > 0)
        {
            await db.Companies.Where(c => personal.Contains(c.Id) && c.Kind == CompanyKind.Personal).ExecuteDeleteAsync(cancellationToken);
        }

        foreach (var account in accounts)
        {
            lost.TryGetValue(account.Id, out var space);
            audit.Stage(
                new AuditEntry
                {
                    Kind = AuditKinds.AccountDeleted,
                    ActorUserId = reason == AccountDeletionReason.Self ? account.Id : null,
                    SubjectUserId = account.Id,
                    SubjectEmail = account.Email,
                    OrgId = space?.Id,
                    OrgName = space?.Name,
                    Details = new Dictionary<string, object?> { ["reason"] = NameOf(reason) },
                },
                actor);
        }

        return accounts.Count;
    }
}
```

(If `streaks.Contains(...)` over `byte[]` does not translate, delete the streaks with a loop over the hashes, one statement each: the number of
addresses is the number of accounts. Report it.)

- [ ] **Step 5: Register it.** In `src/Auth.Server/Tenancy/TenancyServices.cs`, after `        services.AddScoped<CompanyDeletionService>();` add
`        services.AddScoped<AccountDeletion>();`.

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.AccountDeletionTests -class Auth.IntegrationTests.AuditTableTests -class Auth.IntegrationTests.AuditLogTests`
passes.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): one routine deletes an account with everything the spec lists; two audit kinds`.

---

## Day 3 — Monday 13.04: deletions, the start-up step, the request by kind

When the day is done, an admin who removes a member or deletes a company deletes the accounts that lose their only space, a start deletes
the accounts left without one by earlier versions, and `POST /auth/signup` takes the kind.

### Task 7: A removal and a company deletion delete the accounts

**Files:**
- Modify: `src/Auth.Server/Tenancy/MemberService.cs`, `src/Auth.Server/Tenancy/CompanyDeletionService.cs`, `src/Auth.Server/Admin/AdminCli.cs`, `src/Auth.Server/Api/TenancyEndpoints.cs`
- Modify (tests): `tests/Auth.IntegrationTests/OrgMemberTests.cs`, `tests/Auth.IntegrationTests/CompanyDeletionTests.cs`, `tests/Auth.IntegrationTests/OrgDeleteEndpointTests.cs`, `tests/Auth.IntegrationTests/AdminCliTests.cs`, `tests/Auth.IntegrationTests/DeleteOrgCommandTests.cs`, `tests/Auth.IntegrationTests/AuditAtomicityTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/AccountDeletionOnRemovalTests.cs`

**Interfaces:**
- Consumes: `AccountDeletion`, `AccountDeletionReason` (Task 6).
- Produces: `MemberService(AuthDbContext, CompanyGuard, ManifestHolder, AccountDeletion, AuditLog)` and `CompanyDeletionService(AuthDbContext, CompanyGuard, ManifestHolder, AccountDeletion, AuditLog)` (the OpenIddict managers leave both constructors: the routine deletes the sessions); the CLI's success lines `Member removed; the account is deleted.` and `Company deleted; the accounts of its members are deleted.`

**Contract counts:** none. Tests that pinned the kept account change to this spec (spec, criterion 28): `OrgMemberTests.Removing_a_member_ends_the_membership_and_every_session_and_keeps_the_account` becomes `..._and_deletes_the_account`; `CompanyDeletionTests` (the accounts and their session rows are gone), `CompanyDeletionTests.What_a_member_sees_is_what_a_removed_member_sees`, `OrgDeleteEndpointTests.What_a_member_sees_afterwards_is_what_a_removed_member_sees` and `AdminCliTests.Remove_member_removes_the_member_and_ends_every_session` (a login is the `401` of an unknown address), `DeleteOrgCommandTests.It_deletes_with_the_exact_name_prints_one_line_and_touches_nothing_else` (the new line). `AuditAtomicityTests` gains two assertions (the accounts stay when the audit row cannot be written: the deletion is in the transaction of the change); no earlier assertion changes.

**Decisions the plan takes here:**
- The CLI says what happened: the two success lines name the deletion of the accounts (the operator should not learn it from the audit log).
- The removal stages `member.removed` before calling the routine; both rows are written by the one save of the transaction.

- [ ] **Step 1: Write the failing tests.** `tests/Auth.IntegrationTests/AccountDeletionOnRemovalTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
using Auth.Server.Api;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Accounts are never left without a space: a removal and a company deletion delete the accounts.</summary>
public sealed class AccountDeletionOnRemovalTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Worker = "worker@acme.test";

    private Task<int> UsersAsync(Guid id) => InDbAsync(db => db.Users.CountAsync(u => u.Id == id, TestContext.Current.CancellationToken));

    private static async Task AssertUnknownAddressAsync(HttpResponseMessage login)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Removing_a_member_by_the_api_deletes_the_account_in_the_same_transaction()   // criterion 23
    {
        var (company, admin, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");
        var session = await SessionApi.LoginAsync(Client, Worker, UserPassword);

        using (var removed = await SendFromCallerAsync(HttpMethod.Delete, $"/auth/org/members/{worker}", token))
        {
            await TenancyApi.AssertEmptyAsync(removed, HttpStatusCode.NoContent);   // the answer is unchanged
        }

        Assert.Equal(0, await UsersAsync(worker));
        Assert.Single(await AuditAsync(AuditKinds.MemberRemoved));
        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal(admin, row.ActorUserId);
        Assert.Equal(worker, row.SubjectUserId);
        Assert.Equal(Worker, row.SubjectEmail);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("Acme", row.OrgName);
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Equal("member_removed", AuditApi.Text(row, "reason"));
        Assert.Equal(["reason"], AuditApi.DetailNames(row));

        using (var refresh = await SessionApi.Refresh(Client, session.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(refresh);
        }

        using var login = await LoginApi.Login(Client, Worker, UserPassword);
        await AssertUnknownAddressAsync(login);
    }

    [Fact]
    public async Task A_later_invitation_to_the_address_creates_a_new_account()   // criterion 23
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");
        using (var removed = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", token))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        var invite = await AddInviteAsync(company, Worker, "user");
        await EnqueueInvitationAsync(invite, Worker);
        await DispatchAsync();
        using (var accepted = await TenancyApi.Accept(Client, TokenIn(Mail.Sent.Last(m => m.To == Worker)), "Newly-Chosen-1"))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        var again = await SessionApi.LoginAsync(Client, Worker, "Newly-Chosen-1");
        Assert.NotEqual(worker.ToString(), AccessTokens.Text(again.AccessToken, "sub"));
    }

    [Fact]
    public async Task Removing_a_member_by_the_cli_deletes_the_account_and_the_row_says_cli()   // criterion 23
    {
        var (company, _, _) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");

        var run = await OperatorCli.RunAsync(Factory, "remove-member", "--org", company.ToString(), "--email", Worker);

        Assert.Equal(AdminCli.Done, run.Exit);
        Assert.Equal("Member removed; the account is deleted.", run.Out.Trim());
        Assert.Equal(0, await UsersAsync(worker));
        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.ClientIp);
        Assert.Equal("cli", AuditApi.Text(row, "via"));
        Assert.Equal("member_removed", AuditApi.Text(row, "reason"));
        Assert.Single(await AuditAsync(AuditKinds.MemberRemoved));
    }

    [Fact]
    public async Task Deleting_a_company_by_the_api_deletes_every_members_account_the_callers_included()   // criterion 24
    {
        var (acme, admin, token) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var one = await AddMemberAsync(acme, "one@acme.test", "user");
        var two = await AddMemberAsync(acme, "two@acme.test", "user");
        var (_, globexAdmin, _) = await CompanyWithAdminAsync("Globex", "boss@globex.test");

        using (var deleted = await SendFromCallerAsync(HttpMethod.Delete, OrgEndpoints.OrgPath, token, new { name = "Acme", password = UserPassword }))
        {
            await TenancyApi.AssertEmptyAsync(deleted, HttpStatusCode.NoContent);
        }

        foreach (var id in new[] { admin, one, two })
        {
            Assert.Equal(0, await UsersAsync(id));
        }

        Assert.Equal(1, await UsersAsync(globexAdmin));
        var rows = await AuditAsync(AuditKinds.AccountDeleted);   // counted, never sorted by time: they share one transaction
        Assert.Equal(new[] { admin, one, two }.Order(), rows.Select(r => r.SubjectUserId!.Value).Order());
        Assert.All(rows, row =>
        {
            Assert.Equal("org_deleted", AuditApi.Text(row, "reason"));
            Assert.Equal(acme, row.OrgId);
            Assert.Equal("Acme", row.OrgName);
            Assert.Equal(admin, row.ActorUserId);
        });
        Assert.Single(await AuditAsync(AuditKinds.OrgDeleted));
    }

    [Fact]
    public async Task Deleting_a_company_by_the_cli_deletes_every_members_account()   // criterion 24
    {
        var (acme, admin, _) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var one = await AddMemberAsync(acme, "one@acme.test", "user");

        var run = await OperatorCli.RunAsync(Factory, "delete-org", "--org", acme.ToString(), "--confirm", "Acme");

        Assert.Equal(AdminCli.Done, run.Exit);
        Assert.Equal("Company deleted; the accounts of its members are deleted.", run.Out.Trim());
        Assert.Equal(0, await UsersAsync(admin));
        Assert.Equal(0, await UsersAsync(one));
        var rows = await AuditAsync(AuditKinds.AccountDeleted);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Null(row.ActorUserId);
            Assert.Equal("cli", AuditApi.Text(row, "via"));
            Assert.Equal("org_deleted", AuditApi.Text(row, "reason"));
        });
    }

    [Fact]
    public async Task A_removed_persons_token_gets_permissions_changed_where_it_claims_and_forbidden_where_it_never_did()   // spec 0010, Amendment item 2
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        var worker = await AddMemberAsync(company, Worker, "user");   // the role user claims no built-in permission
        var workerToken = (await SessionApi.LoginAsync(Client, Worker, UserPassword)).AccessToken;
        using (var removed = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", token))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var me = await TenancyApi.Get(Client, OrgEndpoints.MePath, workerToken);
        using var org = await TenancyApi.Get(Client, OrgEndpoints.OrgPath, workerToken);
        using var members = await TenancyApi.Get(Client, "/auth/org/members", workerToken);

        await TenancyApi.AssertErrorAsync(me, HttpStatusCode.Forbidden, "permissions_changed");
        await TenancyApi.AssertErrorAsync(org, HttpStatusCode.Forbidden, "permissions_changed");
        await TenancyApi.AssertErrorAsync(members, HttpStatusCode.Forbidden, "forbidden");
    }
}
```

Change the tests that pinned the kept account:

(a) `tests/Auth.IntegrationTests/OrgMemberTests.cs`: replace

```csharp
    public async Task Removing_a_member_ends_the_membership_and_every_session_and_keeps_the_account()   // criterion 13
```

with

```csharp
    public async Task Removing_a_member_ends_the_membership_and_every_session_and_deletes_the_account()   // criterion 13; spec 0010, criterion 23
```

replace

```csharp
        Assert.NotNull(await InDbAsync(db => db.Users.FindAsync([worker], TestContext.Current.CancellationToken).AsTask()));   // the account stays
```

with

```csharp
        Assert.Null(await InDbAsync(db => db.Users.FindAsync([worker], TestContext.Current.CancellationToken).AsTask()));   // gone with its only space (spec 0010)
```

replace

```csharp
        using var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword);
        Assert.Equal("""{"error":"no_membership"}""", await login.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }
```

with

```csharp
        using var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword);
        Assert.Equal("""{"error":"invalid_credentials"}""", await login.Content.ReadAsStringAsync());   // the 401 of an unknown address
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }
```

and in `A_removed_member_can_be_invited_again_but_the_old_sessions_stay_ended` replace

```csharp
        Assert.Equal(globex.ToString(), AccessTokens.Text(again.AccessToken, "org_id"));
```

with

```csharp
        Assert.Equal(globex.ToString(), AccessTokens.Text(again.AccessToken, "org_id"));
        Assert.NotEqual(worker.ToString(), AccessTokens.Text(again.AccessToken, "sub"));   // a new account (spec 0010)
```

(b) `tests/Auth.IntegrationTests/CompanyDeletionTests.cs`: replace

```csharp
        // The other company is untouched, and the accounts stay.
```

with

```csharp
        // The other company is untouched; the accounts of the deleted company's members are gone with it (spec 0010).
```

replace

```csharp
        Assert.Equal(1, await CountAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CountAsync(db => db.Users.CountAsync(u => u.Email == "boss@acme.test", TestContext.Current.CancellationToken)));

        // Read from the store, so the revocation is proved apart from the membership check, which would refuse these sessions anyway.
        await AssertSessionsAsync(acmeAdmin, revoked: true);
        await AssertSessionsAsync(worker, revoked: true);
        await AssertSessionsAsync(globexAdmin, revoked: false);
    }
```

with

```csharp
        Assert.Equal(0, await CountAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CountAsync(db => db.Users.CountAsync(u => u.Email == "boss@acme.test", TestContext.Current.CancellationToken)));

        // Read from the store: the sessions of the deleted accounts are gone with them; the other company's are untouched.
        await AssertNoSessionsAsync(acmeAdmin);
        await AssertNoSessionsAsync(worker);
        await AssertSessionsAsync(globexAdmin, revoked: false);
    }

    private async Task AssertNoSessionsAsync(Guid user)
    {
        var subject = user.ToString();
        using var scope = Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

        Assert.Empty(await tokens.FindBySubjectAsync(subject, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await authorizations.FindBySubjectAsync(subject, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
    }
```

and replace (in `What_a_member_sees_is_what_a_removed_member_sees`)

```csharp
        using (var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword))
        {
            await TenancyApi.AssertErrorAsync(login, HttpStatusCode.Forbidden, "no_membership");
        }
```

with

```csharp
        using (var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword))
        {
            await TenancyApi.AssertErrorAsync(login, HttpStatusCode.Unauthorized, "invalid_credentials");   // the account is gone (spec 0010)
        }
```

(c) `tests/Auth.IntegrationTests/OrgDeleteEndpointTests.cs`: the same replacement as the last one of (b) (the block in `What_a_member_sees_afterwards_is_what_a_removed_member_sees`).

(d) `tests/Auth.IntegrationTests/AdminCliTests.cs`: in `Remove_member_removes_the_member_and_ends_every_session` replace

```csharp
        using var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword);
        Assert.Equal("""{"error":"no_membership"}""", await login.Content.ReadAsStringAsync());
```

with

```csharp
        Assert.Null(await InDbAsync(db => db.Users.SingleOrDefaultAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));   // spec 0010
        using var login = await LoginApi.Login(Client, "worker@acme.test", UserPassword);
        Assert.Equal("""{"error":"invalid_credentials"}""", await login.Content.ReadAsStringAsync());
```

(e) `tests/Auth.IntegrationTests/DeleteOrgCommandTests.cs`: replace `Assert.Equal("Company deleted; every session of its members has ended.", run.Out.Trim());` with `Assert.Equal("Company deleted; the accounts of its members are deleted.", run.Out.Trim());`.

(f) `tests/Auth.IntegrationTests/AuditAtomicityTests.cs` (spec 0010: the account deletion is written in the transaction of the change, so a
row that cannot be written leaves the accounts too). In `When_the_row_cannot_be_written_the_change_is_not_made`, after
`        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == worker, TestContext.Current.CancellationToken)));` add

```csharp
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));   // the account is not deleted either
```

In `When_the_row_cannot_be_written_the_company_is_not_deleted`, replace `        var (company, _, token) = await CompanyWithAdminAsync();` with
`        var (company, admin, token) = await CompanyWithAdminAsync();` (the line is in three other methods too: change only this one), and after
`        Assert.Equal(2, await InDbAsync(db => db.Memberships.CountAsync(m => m.CompanyId == company, TestContext.Current.CancellationToken)));` add

```csharp
        Assert.Equal(2, await InDbAsync(db => db.Users.CountAsync(u => u.Id == admin || u.Email == "worker@acme.test", TestContext.Current.CancellationToken)));   // no member's account is deleted
```

- [ ] **Step 2: See them fail.** Build, then run the classes of Step 6: the accounts are still there.

- [ ] **Step 3: `MemberService`.** In `src/Auth.Server/Tenancy/MemberService.cs`:

(a) Replace

```csharp
public sealed class MemberService(
    AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations,
    AuditLog audit)
```

with

```csharp
public sealed class MemberService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, AccountDeletion deletion, AuditLog audit)
```

and remove `using OpenIddict.Abstractions;` (nothing in the file needs it any more).

(b) Replace

```csharp
    /// Removes a member and ends every one of their sessions: every refresh token issued before is refused. The account stays,
    /// so the person can be invited again. Refused: not a member (<c>not_found</c>); the actor is the member
```

with

```csharp
    /// Removes a member. The account loses its only space and is deleted with it (spec 0010), its sessions, links and membership included:
    /// every refresh token issued before is refused, a login is the <c>401</c> of an unknown address, and a later invitation makes a new
    /// account. Refused: not a member (<c>not_found</c>); the actor is the member
```

(c) Replace

```csharp
        db.Memberships.Remove(membership);
        audit.Stage(
            new AuditEntry
            {
                Kind = AuditKinds.MemberRemoved,
                SubjectUserId = userId,
                SubjectEmail = await EmailOfAsync(userId, cancellationToken),
                OrgId = companyId,
                OrgName = await audit.CompanyNameAsync(companyId, cancellationToken),
                Details = details,
            },
            current);
        await db.SaveChangesAsync(cancellationToken);

        // Every session ends: the refresh tokens, and the authorizations they hang on (spec 0002, Decision 11).
        var subject = userId.ToString();
        await tokens.RevokeBySubjectAsync(subject, cancellationToken);
        await authorizations.RevokeBySubjectAsync(subject, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
```

with

```csharp
        // Read before the deletion: the address goes with the account.
        audit.Stage(
            new AuditEntry
            {
                Kind = AuditKinds.MemberRemoved,
                SubjectUserId = userId,
                SubjectEmail = await EmailOfAsync(userId, cancellationToken),
                OrgId = companyId,
                OrgName = await audit.CompanyNameAsync(companyId, cancellationToken),
                Details = details,
            },
            current);

        // The account loses its only space, so it is deleted (spec 0010): its membership, sessions and links go with it, and its own row is
        // staged. Under the company lock only: no address lock (spec 0010 → Locks). The tracked membership is not removed here: the deletion
        // takes it, and removing it again would find no row.
        await deletion.DeleteAsync(userId, AccountDeletionReason.MemberRemoved, current, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Outcome.Done;
```

- [ ] **Step 4: `CompanyDeletionService`.** In `src/Auth.Server/Tenancy/CompanyDeletionService.cs`:

(a) Replace the class summary's sentences

```csharp
/// <c>DELETE /auth/org</c> both call it. Everything happens under the company lock, in one transaction. The queued mails of the
/// company's invitations, the invitations, the memberships, the roles and the company are deleted in that order (the keys from
/// memberships and invitations to roles are <c>Restrict</c>, so a single delete of the company would stop at them). Every member's
/// sessions are revoked, as removing a member does; the accounts stay. A member who later presents a token is treated as a removed
/// member: <c>403 permissions_changed</c> at the company API, <c>401 invalid_grant</c> at refresh, <c>403 no_membership</c> at login.
```

with

```csharp
/// <c>DELETE /auth/org</c> both call it. Everything happens under the company lock, in one transaction. Every member loses the only space
/// they have, the caller included, so their accounts are deleted first (spec 0010, <see cref="AccountDeletion"/>: the memberships, sessions
/// and links go with them); then the queued mails of the company's invitations, the invitations, the roles and the company (the keys from
/// memberships and invitations to roles are <c>Restrict</c>, so a single delete of the company would stop at them). A member who later presents
/// a token: <c>403 permissions_changed</c> at the company API, <c>401 invalid_grant</c> at refresh, the <c>401</c> of an unknown address at login.
```

(b) Replace

```csharp
public sealed class CompanyDeletionService(
    AuthDbContext db, CompanyGuard guard, ManifestHolder manifest,
    IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, AuditLog audit)
```

with

```csharp
public sealed class CompanyDeletionService(AuthDbContext db, CompanyGuard guard, ManifestHolder manifest, AccountDeletion deletion, AuditLog audit)
```

and remove `using OpenIddict.Abstractions;`.

(c) Replace

```csharp
        var invitationIds = db.Invites.Where(i => i.CompanyId == companyId).Select(i => i.Id);
        await db.MailRequests
            .Where(r => r.InviteId != null && invitationIds.Contains(r.InviteId.Value))
            .ExecuteDeleteAsync(cancellationToken);
        var invitations = await db.Invites.Where(i => i.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        await db.Memberships.Where(m => m.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        var roles = await db.CompanyRoles.Where(r => r.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        await db.Companies.Where(c => c.Id == companyId).ExecuteDeleteAsync(cancellationToken);

        // Every session of every member ends (spec 0002, Decision 11), as removing a member does.
        foreach (var member in members)
        {
            var subject = member.ToString();
            await tokens.RevokeBySubjectAsync(subject, cancellationToken);
            await authorizations.RevokeBySubjectAsync(subject, cancellationToken);
        }
```

with

```csharp
        // Every member loses the only space they have, the caller included: their accounts are deleted (spec 0010), one row each, in one set
        // of statements. Under the company lock only, never an address lock per member (spec 0010 → Locks): a large company could not be
        // deleted otherwise. Their memberships go with them.
        await deletion.DeleteAsync(members, AccountDeletionReason.OrgDeleted, current, cancellationToken);

        var invitationIds = db.Invites.Where(i => i.CompanyId == companyId).Select(i => i.Id);
        await db.MailRequests
            .Where(r => r.InviteId != null && invitationIds.Contains(r.InviteId.Value))
            .ExecuteDeleteAsync(cancellationToken);
        var invitations = await db.Invites.Where(i => i.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        var roles = await db.CompanyRoles.Where(r => r.CompanyId == companyId).ExecuteDeleteAsync(cancellationToken);
        await db.Companies.Where(c => c.Id == companyId).ExecuteDeleteAsync(cancellationToken);
```

- [ ] **Step 5: The CLI's lines and the description.** In `src/Auth.Server/Admin/AdminCli.cs` replace
`await output.WriteLineAsync("Member removed; every session of the account has ended.");` with
`await output.WriteLineAsync("Member removed; the account is deleted.");` and
`await output.WriteLineAsync("Company deleted; every session of its members has ended.");` with
`await output.WriteLineAsync("Company deleted; the accounts of its members are deleted.");`.

In `src/Auth.Server/Api/TenancyEndpoints.cs` replace

```csharp
                "Deletes the caller's company with its roles, members, invitations and the mails queued for them; the accounts stay. "
                + "Needs the permission `org:delete`, the company's name exactly and the caller's own password. The members' sessions end: "
                + "an access token still held gets `403 permissions_changed`, the next refresh `401 invalid_grant`, a new login `403 no_membership`. "
```

with

```csharp
                "Deletes the caller's company with its roles, invitations and the mails queued for them, and the account of every member, the "
                + "caller's included: no account is left without a space. Needs the permission `org:delete`, the company's name exactly and the "
                + "caller's own password. An access token still held gets `403 permissions_changed`, the next refresh `401 invalid_grant`, a new "
                + "login the `401` of an unknown address. "
```

and replace

```csharp
        company.MapDelete("members/{user_id}", OrgMemberEndpoints.RemoveAsync)
            .Produces(StatusCodes.Status204NoContent)
```

with

```csharp
        company.MapDelete("members/{user_id}", OrgMemberEndpoints.RemoveAsync)
            .WithDescription(
                "Removes a member. The account loses its only space and is deleted with its sessions and links; the address can be invited "
                + "again or sign up, as a new account.")
            .Produces(StatusCodes.Status204NoContent)
```

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.AccountDeletionOnRemovalTests -class Auth.IntegrationTests.OrgMemberTests -class Auth.IntegrationTests.CompanyDeletionTests -class Auth.IntegrationTests.OrgDeleteEndpointTests -class Auth.IntegrationTests.AdminCliTests -class Auth.IntegrationTests.DeleteOrgCommandTests -class Auth.IntegrationTests.AuditMemberFlowsTests -class Auth.IntegrationTests.AuditAtomicityTests -class Auth.IntegrationTests.OrgDeletePermissionTests -class Auth.IntegrationTests.OrgEndpointTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests`
passes.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): removing a member and deleting a company delete the accounts that lose their only space`.

### Task 8: The start-up step

**Files:**
- Create: `src/Auth.Server/Tenancy/AddressLock.cs`, `src/Auth.Server/Tenancy/AccountsWithoutSpaceCleanup.cs`
- Modify: `src/Auth.Server/Program.cs`
- Create (tests): `tests/Auth.IntegrationTests/AccountsWithoutSpaceStartupTests.cs`

**Interfaces:**
- Consumes: `AccountDeletion` (Task 6), `IServiceScopeFactory`.
- Produces: `AddressLock.AcquireAsync(AuthDbContext db, string normalizedEmail, CancellationToken)` (the advisory lock that sign-up completion and invitation acceptance take: `pg_advisory_xact_lock(hashtextextended(<normalised address>, 0))`); `AccountsWithoutSpaceCleanup` (singleton, `Program.cs` only): `Task<int> RunAsync(CancellationToken)`.

**Contract counts:** none.

- [ ] **Step 1: Write the failing tests.** `tests/Auth.IntegrationTests/AccountsWithoutSpaceStartupTests.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Admin;
using Auth.Server.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Upgrade: at start, every account without a membership is deleted, once, in the service only.</summary>
public sealed class AccountsWithoutSpaceStartupTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static async Task<Guid> CreateUserAsync(AuthAppFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        var result = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().CreateAsync(user, "Another-Passw0rd");
        Assert.True(result.Succeeded);
        return user.Id;
    }

    private static async Task<T> InDbAsync<T>(AuthAppFactory factory, Func<AuthDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private static Task<List<AuditEvent>> DeletedRowsAsync(AuthAppFactory factory) =>
        InDbAsync(factory, db => db.AuditEvents.AsNoTracking().Where(a => a.Kind == AuditKinds.AccountDeleted).ToListAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_start_deletes_every_account_without_a_membership_once_and_keeps_the_seed_users()   // criterion 25, Review Focus 1
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        Guid lonely;
        Guid gone;
        await using (var first = new AuthAppFactory(postgres, keys, database))
        {
            lonely = await CreateUserAsync(first, "lonely@example.test");   // made while the host runs: an earlier version's left-overs
            gone = await CreateUserAsync(first, "gone@example.test");
        }

        await using (var second = new AuthAppFactory(postgres, keys, database))
        {
            var rows = await DeletedRowsAsync(second);
            Assert.Equal(new[] { lonely, gone }.Order(), rows.Select(r => r.SubjectUserId!.Value).Order());
            Assert.All(rows, row =>
            {
                Assert.Null(row.ActorUserId);
                Assert.Null(row.ClientIp);
                Assert.Null(row.OrgId);
                Assert.Equal("upgrade", AuditApi.Text(row, "reason"));
            });
            Assert.Equal(0, await InDbAsync(second, db => db.Users.CountAsync(u => u.Id == lonely || u.Id == gone, TestContext.Current.CancellationToken)));
            var seed = await second.SeedUserIdAsync();
            Assert.Equal(1, await InDbAsync(second, db => db.Memberships.CountAsync(m => m.UserId == seed, TestContext.Current.CancellationToken)));
        }

        await using (var third = new AuthAppFactory(postgres, keys, database))
        {
            Assert.Equal(2, (await DeletedRowsAsync(third)).Count);   // a second start changes nothing
            var seed = await third.SeedUserIdAsync();
            Assert.Equal(1, await InDbAsync(third, db => db.Memberships.CountAsync(m => m.UserId == seed, TestContext.Current.CancellationToken)));
        }
    }

    [Fact]
    public async Task The_operator_cli_never_runs_the_step()   // spec 0010 → Upgrade: in the service only
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var lonely = await CreateUserAsync(factory, "lonely@example.test");

        var run = await OperatorCli.RunAsync(factory, "list-orgs");

        Assert.Equal(AdminCli.Done, run.Exit);
        Assert.Equal(1, await InDbAsync(factory, db => db.Users.CountAsync(u => u.Id == lonely, TestContext.Current.CancellationToken)));
        Assert.Empty(await DeletedRowsAsync(factory));
    }
}
```

- [ ] **Step 2: See them fail.** Build, then run the class (command of Step 6): the second start leaves both accounts.

- [ ] **Step 3: The address lock.** `src/Auth.Server/Tenancy/AddressLock.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// The lock of one address (specs 0009 and 0010 → Locks): a transaction-scoped advisory lock on the normalised address, the lock that sign-up
/// completion and invitation acceptance take. Spec 0010 takes it where no company lock guards the account: self-deletion, the move of a private
/// account into a company, and the start-up step. A flow that also changes a company takes the company lock first.
/// </summary>
public static class AddressLock
{
    /// <summary>Takes the lock until the transaction the caller has opened ends.</summary>
    public static Task AcquireAsync(AuthDbContext db, string normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({normalizedEmail}, 0))", cancellationToken);
    }
}
```

- [ ] **Step 4: The step.** `src/Auth.Server/Tenancy/AccountsWithoutSpaceCleanup.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// The start-up step of spec 0010 (Upgrade): every account without a membership, which an earlier version left behind when it removed a member
/// or deleted a company, is deleted by <see cref="AccountDeletion"/> with the reason <c>upgrade</c>, no actor and no client address. It runs
/// after the migrations and the manifest and before the development seeder, in the service only (the operator CLI never builds a host). Each
/// account in a transaction of its own, under the address lock, read again under it: another replica, or an invitation of the address, may have
/// given it a space since the list was read. A start that finds none changes nothing.
/// </summary>
public sealed partial class AccountsWithoutSpaceCleanup(IServiceScopeFactory scopes, ILogger<AccountsWithoutSpaceCleanup> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        List<Candidate> candidates;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            candidates = await db.Users.AsNoTracking()
                .Where(u => !db.Memberships.Any(m => m.UserId == u.Id))
                .Select(u => new Candidate(u.Id, u.NormalizedEmail))
                .ToListAsync(cancellationToken);
        }

        var deleted = 0;
        foreach (var candidate in candidates)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (candidate.NormalizedEmail is { } address)
            {
                await AddressLock.AcquireAsync(db, address, cancellationToken);
            }

            if (await db.Memberships.AnyAsync(m => m.UserId == candidate.Id, cancellationToken))
            {
                continue;   // it has a space by now; the transaction ends unused with the scope
            }

            deleted += await scope.ServiceProvider.GetRequiredService<AccountDeletion>()
                .DeleteAsync(candidate.Id, AccountDeletionReason.Upgrade, actor: null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        if (deleted > 0)
        {
            LogDeleted(logger, deleted);
        }

        return deleted;
    }

    private sealed record Candidate(Guid Id, string? NormalizedEmail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} accounts that belonged to no company and had no personal space.")]
    private static partial void LogDeleted(ILogger logger, int count);
}
```

- [ ] **Step 5: Run it at start.** In `src/Auth.Server/Program.cs`:

(a) after `builder.Services.AddSingleton<SignupGate>();` add `builder.Services.AddSingleton<AccountsWithoutSpaceCleanup>();`;

(b) replace

```csharp
// A manifest that asks for self-service sign-up without the completion address leaves it off: say so (spec 0009).
app.Services.GetRequiredService<SignupGate>().WarnIfMissingAddress();
```

with

```csharp
// A manifest that asks for self-service sign-up without the completion address leaves it off: say so (spec 0009).
app.Services.GetRequiredService<SignupGate>().WarnIfMissingAddress();

// No account is left without a space (spec 0010): the accounts an earlier version left without a company are deleted, once, before the
// development seeder and the first request.
await app.Services.GetRequiredService<AccountsWithoutSpaceCleanup>().RunAsync(app.Lifetime.ApplicationStopping);
```

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.AccountsWithoutSpaceStartupTests -class Auth.IntegrationTests.DevSeedTests -class Auth.IntegrationTests.DevCompanySeedTests -class Auth.IntegrationTests.UnverifiedLoginTests -class Auth.IntegrationTests.LockoutTests -class Auth.IntegrationTests.MailDispatcherTests -class Auth.IntegrationTests.TenantClaimsTests -class Auth.IntegrationTests.AuditAccountFlowsTests`
passes. (`TenantClaimsTests.Account_without_a_company_is_refused_with_no_token_and_no_cookie` keeps guarding criterion 26: an account whose
membership a test removes while the host runs still gets `403 no_membership`.)

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): a start deletes the accounts that an earlier version left without a space`.

### Task 9: `POST /auth/signup` with `kind`

**Files:**
- Create: `src/Auth.Server/Requests/JsonFields.cs`, `src/Auth.Server/Signup/SignupKinds.cs`
- Modify: `src/Auth.Server/Requests/JsonObjectBody.cs`, `src/Auth.Server/Signup/SignupRequestEndpoint.cs`, `src/Auth.Server/Email/MailRequestStore.cs`, `src/Auth.Server/Signup/PendingSignups.cs`, `src/Auth.Server/Signup/SignupContracts.cs`, `src/Auth.Server/Api/SignupRoutes.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/PendingSignupSeed.cs`, `tests/Auth.IntegrationTests/SignupRequestTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/PrivateSignupRequestTests.cs`, `tests/Auth.IntegrationTests/SignupKindSwitchTests.cs`

**Interfaces:**
- Consumes: `SignupGate.AnyOn`, `IsOnFor` (Task 2), `SignupKind` (Task 1), `TenancyTestBase.CreatePrivateAccountAsync`, `AuthAppFactory.WithPrivateSignup`.
- Produces: `JsonObjectBody.ReadObjectAsync(HttpRequest, CancellationToken)` returning `JsonFields?`; `JsonFields` (`Has`, `TryString`, `TryOptionalString`, `Boolean`) and `enum JsonBoolean { Absent, True, False, Invalid }` in `Auth.Server.Requests`; `SignupKinds` (`OrganizationName`, `PrivateName`, `NameOf`, `TryParse`, `TryRead(JsonFields, out SignupKind)`); `MailRequestStore.SubmitAsync(..., string? companyName = null, SignupKind signupKind = SignupKind.Organization)`; `PendingSignups.UpsertAsync(db, normalizedEmail, email, string? companyName, SignupKind kind, now, ct)` beside the old overload (an organization sign-up); `SignupRequest(string Email, string? Kind = null, string? CompanyName = null)`; test side `PendingSignupSeed.IssueAsync(services, email, string? companyName, SignupKind kind = SignupKind.Organization)`.

**Contract counts:** `OpenApiTests.Each_endpoint_that_reads_a_body_describes_it_in_snake_case`: `POST /auth/signup` becomes `company_name,email,kind`. `SignupRequestTests.An_accepted_request_is_recorded_with_the_typed_address_only`: the row gains `details.kind` (spec, `signup.requested` gains `details.kind`); `SignupRequestTests.An_account_without_a_company_gets_the_same_status_headers_body_and_statements` (Amendment 3 of spec 0009) becomes `A_private_account_gets_the_same_status_headers_body_and_statements`.

**Decisions the plan takes here:**
- `kind` is read with the string rule of every member: `null`, `""`, a number or any other text is `400 invalid_request` (spec: "a `kind` that is not one of the two strings").
- The pending row of a private sign-up stores no company name even if one reached the store (it cannot: the endpoint refuses it first).
- The preview, the completion and the dispatcher still treat every pending row as before until Tasks 10 and 11; a private request is stored and counted now, and mailed from Task 10 on.

- [ ] **Step 1: Write the failing tests.**

In `tests/Auth.IntegrationTests/Infrastructure/PendingSignupSeed.cs` replace

```csharp
    public static async Task<string> IssueAsync(IServiceProvider services, string email, string companyName)
    {
```

with

```csharp
    /// <param name="kind">Spec 0010: a private sign-up has no company name.</param>
    public static async Task<string> IssueAsync(IServiceProvider services, string email, string? companyName, SignupKind kind = SignupKind.Organization)
    {
```

and replace `await PendingSignups.UpsertAsync(db, normalized, email, companyName, now, cancellation);` with
`await PendingSignups.UpsertAsync(db, normalized, email, companyName, kind, now, cancellation);`.

`tests/Auth.IntegrationTests/PrivateSignupRequestTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Auth.Server.Sessions;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → <c>POST /auth/signup</c> with both switches on.</summary>
public sealed class PrivateSignupRequestTests : SignupTestBase
{
    private const string Private = "ola@poczta.test";

    public PrivateSignupRequestTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithPrivateSignup();

        // As in SignupRequestTests: no background pass of the host may log statements of its own while a test compares what requests ran.
        Factory.WithoutHostedService<TokenPruningService>().WithoutHostedService<LockoutPruningService>();
    }

    private Task<HttpResponseMessage> PrivateRequestAsync(string email = Private) =>
        AccountApi.PostRaw(Client, SignupApi.RequestPath, $$"""{"email":"{{email}}","kind":"private"}""");

    /// <summary>The SQL of every database command the host has logged since <paramref name="mark"/> log entries.</summary>
    private List<string> StatementsSince(int mark) =>
        [.. Logs.Entries.Skip(mark)
            .Where(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command")
            .Select(e => e.Message[(e.Message.IndexOf('\n', StringComparison.Ordinal) + 1)..].Trim())];

    [Fact]
    public async Task A_private_request_is_accepted_with_an_empty_body_and_stores_its_kind_and_no_company()   // criterion 4
    {
        using var response = await PrivateRequestAsync("Ola@Poczta.test");

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        var pending = Assert.Single(await PendingAsync());
        Assert.Equal(SignupKind.Private, pending.Kind);
        Assert.Null(pending.CompanyName);
        Assert.Equal("Ola@Poczta.test", pending.Email);
        Assert.Equal(1, await QueueCountAsync());
        var row = await SingleAsync(AuditKinds.SignupRequested);
        Assert.Equal("private", AuditApi.Text(row, "kind"));
        Assert.Equal(["kind"], AuditApi.DetailNames(row));
        Assert.Equal("Ola@Poczta.test", row.SubjectEmail);
    }

    [Fact]
    public async Task A_request_without_kind_is_an_organization_sign_up_as_before()   // criterion 4
    {
        using var response = await SignupApi.Request(Client, Address, CompanyName);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        var pending = Assert.Single(await PendingAsync());
        Assert.Equal(SignupKind.Organization, pending.Kind);
        Assert.Equal(CompanyName, pending.CompanyName);
        Assert.Equal("organization", AuditApi.Text(await SingleAsync(AuditKinds.SignupRequested), "kind"));
    }

    [Theory]
    [InlineData("""{"email":"ola@poczta.test","kind":"Private"}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":"company"}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":""}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":null}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":2}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":"private","company_name":"Firma"}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":"private","company_name":null}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":"private","company_name":""}""")]
    [InlineData("""{"email":"ola@poczta.test","kind":"organization"}""")]
    [InlineData("""{"email":"ola@poczta.test"}""")]
    [InlineData("""{"kind":"private"}""")]
    [InlineData("""{"email":"not-an-address","kind":"private"}""")]
    public async Task Unknown_kinds_a_private_company_name_and_an_organization_without_one_are_400_and_queue_nothing(string body)   // criterion 4
    {
        using var response = await AccountApi.PostRaw(Client, SignupApi.RequestPath, body);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        Assert.Empty(await PendingAsync());
        Assert.Equal(0, await QueueCountAsync());
        Assert.Equal(0, await LimitCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.SignupRequested));
    }

    [Fact]
    public async Task The_mail_limit_counts_both_kinds_together()   // criterion 6
    {
        using (var organization = await SignupApi.Request(Client, Private, CompanyName))
        {
            Assert.Equal(HttpStatusCode.Accepted, organization.StatusCode);
        }

        using (var refused = await PrivateRequestAsync())
        {
            Assert.True(await LockoutApi.AssertLockedAsync(refused) > 0);   // 429 too_many_attempts with retry_after_seconds
        }

        Clock.Advance(PastTheLimit);
        using var later = await PrivateRequestAsync();
        Assert.Equal(HttpStatusCode.Accepted, later.StatusCode);
    }

    [Fact]
    public async Task A_request_that_changes_the_kind_voids_the_earlier_link_at_once()   // criterion 7
    {
        var link = await MailedAsync(Private);   // an organization link, mailed
        Clock.Advance(PastTheLimit);

        using (var changed = await PrivateRequestAsync())
        {
            Assert.Equal(HttpStatusCode.Accepted, changed.StatusCode);
        }

        using var preview = await SignupApi.Preview(Client, link);   // before any new mail is sent
        await AccountApi.AssertErrorAsync(preview, AccountApi.InvalidToken);
    }

    [Fact]
    public async Task A_request_that_changes_only_the_company_name_keeps_the_link_until_the_new_mail()   // criterion 7
    {
        var link = await MailedAsync(Private, "First");
        Clock.Advance(PastTheLimit);

        using (var renamed = await SignupApi.Request(Client, Private, "Second"))
        {
            Assert.Equal(HttpStatusCode.Accepted, renamed.StatusCode);
        }

        using var preview = await SignupApi.Preview(Client, link);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
    }

    [Fact]
    public async Task A_private_request_runs_the_same_statements_and_answers_the_same_for_any_address()   // criterion 5
    {
        await CreatePrivateAccountAsync("held@poczta.test");

        var mark = Logs.Entries.Count;
        using var company = await PrivateRequestAsync(Factory.SeedEmail);   // an account that belongs to a company
        var forCompany = StatementsSince(mark);

        mark = Logs.Entries.Count;
        using var held = await PrivateRequestAsync("held@poczta.test");     // a private account
        var forPrivate = StatementsSince(mark);

        mark = Logs.Entries.Count;
        using var unknown = await PrivateRequestAsync("nobody@poczta.test");
        var forUnknown = StatementsSince(mark);

        foreach (var response in new[] { company, held, unknown })
        {
            await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
            Assert.Equal(LoginApi.HeaderNames(company), LoginApi.HeaderNames(response));
        }

        Assert.NotEmpty(forUnknown);
        Assert.Equal(forUnknown, forCompany);
        Assert.Equal(forUnknown, forPrivate);
        Assert.DoesNotContain(forCompany.Concat(forPrivate).Concat(forUnknown), sql => sql.Contains("AspNetUsers", StringComparison.Ordinal));
        Assert.DoesNotContain(forCompany.Concat(forPrivate).Concat(forUnknown), sql => sql.Contains("Memberships", StringComparison.Ordinal));
    }
}
```

`tests/Auth.IntegrationTests/SignupKindSwitchTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → <c>POST /auth/signup</c>, checks 1 to 3: the switch of the kind comes before the other members.</summary>
public sealed class SignupKindSwitchTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static async Task<int> PendingCountAsync(AuthAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<Auth.Infrastructure.Persistence.AuthDbContext>().PendingSignups
            .CountAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("""{"kind":"private","email":5}""")]
    [InlineData("""{"kind":"private","email":"ola@poczta.test"}""")]
    [InlineData("""{"kind":"private","email":"ola@poczta.test","company_name":"Firma"}""")]
    public async Task With_only_organization_sign_up_on_a_private_request_is_403_before_its_other_members_are_judged(string body)   // criterion 3
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup();
        using var client = factory.CreateClient();

        using var response = await AccountApi.PostRaw(client, SignupApi.RequestPath, body);

        await SignupApi.AssertDisabledAsync(response);
        Assert.Equal(0, await PendingCountAsync(factory));
    }

    [Theory]
    [InlineData("""{"email":"bad","company_name":""}""")]
    [InlineData("""{"email":"anna@firma.test","company_name":"Firma"}""")]
    [InlineData("""{"kind":"organization","email":"anna@firma.test","company_name":"Firma"}""")]
    public async Task With_only_private_sign_up_on_an_organization_request_is_403_before_its_other_members_are_judged(string body)   // criterion 3
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup(organization: false);
        using var client = factory.CreateClient();

        using var response = await AccountApi.PostRaw(client, SignupApi.RequestPath, body);

        await SignupApi.AssertDisabledAsync(response);
        Assert.Equal(0, await PendingCountAsync(factory));
    }

    [Fact]
    public async Task With_one_switch_on_a_kind_that_is_not_one_of_the_two_is_400_first()   // criterion 3: the kind is read before its switch is judged
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup(organization: false);
        using var client = factory.CreateClient();

        using var response = await AccountApi.PostRaw(client, SignupApi.RequestPath, """{"kind":"company","email":"ola@poczta.test"}""");

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
    }

    [Fact]
    public async Task With_only_private_sign_up_on_a_private_request_is_accepted()   // criterion 4
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup(organization: false);
        using var client = factory.CreateClient();

        using var response = await AccountApi.PostRaw(client, SignupApi.RequestPath, """{"kind":"private","email":"ola@poczta.test"}""");

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        Assert.Equal(1, await PendingCountAsync(factory));
    }
}
```

Change the existing tests:

(a) `tests/Auth.IntegrationTests/SignupRequestTests.cs`: replace the whole method

```csharp
    [Fact]
    public async Task An_account_without_a_company_gets_the_same_status_headers_body_and_statements()   // criteria 4 and 24: the request still never reads the accounts
    {
        await RemovedMemberAsync("gone@firma.test");
```

(down to `        Assert.DoesNotContain(forCompany.Concat(forNone).Concat(forUnknown), sql => sql.Contains("Memberships", StringComparison.Ordinal));` and the
method's closing `    }`) with

```csharp
    [Fact]
    public async Task A_private_account_gets_the_same_status_headers_body_and_statements()   // spec 0010, criterion 5: the request never reads the accounts
    {
        await CreatePrivateAccountAsync("gone@firma.test");

        var mark = Logs.Entries.Count;
        using var withCompany = await SignupApi.Request(Client, Factory.SeedEmail, CompanyName);
        var forCompany = StatementsSince(mark);

        mark = Logs.Entries.Count;
        using var withPrivate = await SignupApi.Request(Client, "gone@firma.test", CompanyName);
        var forPrivate = StatementsSince(mark);

        mark = Logs.Entries.Count;
        using var unknown = await SignupApi.Request(Client, "nobody@firma.test", CompanyName);
        var forUnknown = StatementsSince(mark);

        foreach (var response in new[] { withCompany, withPrivate, unknown })
        {
            await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
            Assert.Equal(LoginApi.HeaderNames(withCompany), LoginApi.HeaderNames(response));
        }

        Assert.NotEmpty(forPrivate);
        Assert.Equal(forCompany, forPrivate);
        Assert.Equal(forUnknown, forPrivate);
        Assert.DoesNotContain(forCompany.Concat(forPrivate).Concat(forUnknown), sql => sql.Contains("AspNetUsers", StringComparison.Ordinal));
        Assert.DoesNotContain(forCompany.Concat(forPrivate).Concat(forUnknown), sql => sql.Contains("Memberships", StringComparison.Ordinal));
    }
```

and in `An_accepted_request_is_recorded_with_the_typed_address_only` replace `        Assert.Null(row.Details);` with

```csharp
        Assert.Equal("organization", AuditApi.Text(row, "kind"));   // spec 0010: details.kind
        Assert.Equal(["kind"], AuditApi.DetailNames(row));
```

(b) `tests/Auth.IntegrationTests/OpenApiTests.cs`: replace `    [InlineData("POST /auth/signup", "company_name,email")]` with `    [InlineData("POST /auth/signup", "company_name,email,kind")]`.

- [ ] **Step 2: See them fail.** The build fails: `SignupKind` is not a parameter of `UpsertAsync`, and the private requests are not taken.

- [ ] **Step 3: The members one by one.** `src/Auth.Server/Requests/JsonFields.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Auth.Server.Requests;

/// <summary>What a JSON member that should be a boolean is (spec 0010): absent, <c>true</c>, <c>false</c>, or anything else (<c>null</c> included).</summary>
public enum JsonBoolean
{
    Absent,
    True,
    False,
    Invalid,
}

/// <summary>
/// The members of a request body read one by one (<see cref="JsonObjectBody.ReadObjectAsync"/>), for an endpoint whose checks of the members
/// are interleaved with other checks: spec 0010 judges the switch of a sign-up's kind before its address. A string follows the rule of
/// <see cref="JsonObjectBody"/>: present, a JSON string, not blank, and with no lone surrogate.
/// </summary>
public readonly record struct JsonFields(JsonElement Root)
{
    public bool Has(string name) => Root.TryGetProperty(name, out _);

    /// <summary>The member as a non-blank string; <see langword="false"/> when it is missing or anything else.</summary>
    public bool TryString(string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        try
        {
            if (!JsonObjectBody.TryGetRequiredString(Root, name, out var text))
            {
                return false;
            }

            value = text;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;   // a string holding an unpaired surrogate escape
        }
    }

    /// <summary>An optional string member: absent is fine (<paramref name="value"/> is <see langword="null"/>); present it must be a non-blank string.</summary>
    public bool TryOptionalString(string name, out string? value)
    {
        value = null;
        return !Has(name) || TryString(name, out value);
    }

    public JsonBoolean Boolean(string name) =>
        !Root.TryGetProperty(name, out var element)
            ? JsonBoolean.Absent
            : element.ValueKind switch
            {
                JsonValueKind.True => JsonBoolean.True,
                JsonValueKind.False => JsonBoolean.False,
                _ => JsonBoolean.Invalid,
            };
}
```

In `src/Auth.Server/Requests/JsonObjectBody.cs`, before the doc comment of the first `ReadStringsAsync` (`    /// <summary>` followed by `    /// The values of the properties <paramref name="names"/>, in that order, or <see langword="null"/> when the`) add

```csharp
    /// <summary>
    /// The body as a JSON object whose members the caller reads one by one (<see cref="JsonFields"/>), or <see langword="null"/> when it is not
    /// acceptable: wrong content type, too large, not JSON, or not a JSON object.
    /// </summary>
    public static async Task<JsonFields?> ReadObjectAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsJson(request.ContentType) || request.ContentLength > MaxBytes)
        {
            return null;
        }

        var body = await ReadBoundedAsync(request.Body, MaxBytes, cancellationToken);
        if (body is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object ? new JsonFields(document.RootElement.Clone()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

```

- [ ] **Step 4: The kinds of sign-up.** `src/Auth.Server/Signup/SignupKinds.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Requests;

namespace Auth.Server.Signup;

/// <summary>The kind of a sign-up as the API and the audit log name it (spec 0010): <c>organization</c> or <c>private</c>.</summary>
public static class SignupKinds
{
    public const string OrganizationName = "organization";
    public const string PrivateName = "private";

    private const string Field = "kind";

    public static string NameOf(SignupKind kind) => kind == SignupKind.Private ? PrivateName : OrganizationName;

    /// <summary>Exactly one of the two names. Anything else is not a kind.</summary>
    public static bool TryParse(string? name, out SignupKind kind)
    {
        if (string.Equals(name, PrivateName, StringComparison.Ordinal))
        {
            kind = SignupKind.Private;
            return true;
        }

        kind = SignupKind.Organization;
        return string.Equals(name, OrganizationName, StringComparison.Ordinal);
    }

    /// <summary>The member <c>kind</c> of a request: absent is an organization; otherwise one of the two names, as a string.</summary>
    public static bool TryRead(JsonFields body, out SignupKind kind)
    {
        kind = SignupKind.Organization;
        if (!body.Has(Field))
        {
            return true;
        }

        return body.TryString(Field, out var name) && TryParse(name, out kind);
    }
}
```

- [ ] **Step 5: The endpoint.** Replace the content of `src/Auth.Server/Signup/SignupRequestEndpoint.cs` with

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Signup;

/// <summary>
/// <c>POST /auth/signup</c> (specs 0009 and 0010): a request to start a company or to open a private account. While both switches are off it
/// answers <c>403 signup_disabled</c> before it reads the body. The body is read in two phases: the kind first (absent means an organization),
/// and its switch is judged before the other members, so that a screen that offers only one kind never meets a <c>400</c> of the other. Then it
/// validates, applies the limit of the address (one counter for both kinds), stores the pending sign-up with its kind, queues the request and
/// answers; it never looks an account up, so its work and its answer are the same for every address. Which mail follows is decided when the
/// mail is sent.
/// </summary>
public static class SignupRequestEndpoint
{
    public const string Path = "/auth/signup";

    private const string EmailField = "email";
    private const string CompanyNameField = "company_name";

    public static async Task<IResult> HandleAsync(
        HttpContext http, SignupGate gate, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(signal);

        if (!gate.AnyOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields || !SignupKinds.TryRead(fields, out var kind))
        {
            return AccountResults.InvalidRequest();
        }

        if (!gate.IsOnFor(kind))
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        // The address rule of invitations (spec 0005), so that sign-up cannot make the mail relay deliver to an internal host.
        if (!fields.TryString(EmailField, out var email)
            || email.Length > EmailInput.MaxLength
            || !EmailInput.IsMailbox(email)
            || !EmailInput.IsInvitable(email)
            || !EmailInput.TryNormalize(email, normalizer, out var normalized))
        {
            return AccountResults.InvalidRequest();
        }

        string? companyName = null;
        if (kind == SignupKind.Organization)
        {
            if (!fields.TryString(CompanyNameField, out var name) || !NameInput.IsValid(name))
            {
                return AccountResults.InvalidRequest();
            }

            companyName = name;
        }
        else if (fields.Has(CompanyNameField))
        {
            return AccountResults.InvalidRequest();   // a private person names no company, whatever the value (spec 0010, Decision 5)
        }

        var decision = await requests.SubmitAsync(
            MailKind.Signup, normalized, http.RequestAborted, typedEmail: email, companyName: companyName, signupKind: kind);
        if (!decision.Allowed)
        {
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        signal.Notify();
        return AccountResults.Accepted();
    }
}
```

- [ ] **Step 6: The store and the pending row.** In `src/Auth.Server/Email/MailRequestStore.cs`:

(a) Replace

```csharp
    /// <param name="companyName">The company name of a sign-up request; required for <see cref="MailKind.Signup"/>, ignored otherwise.</param>
    public async Task<MailLimitDecision> SubmitAsync(
        MailKind kind, string normalizedEmail, CancellationToken cancellationToken, string? typedEmail = null, string? companyName = null)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        if (kind == MailKind.Signup && (typedEmail is null || companyName is null))
        {
            throw new ArgumentException(
                "A sign-up request carries the address as typed and the company name.",
                typedEmail is null ? nameof(typedEmail) : nameof(companyName));
        }
```

with

```csharp
    /// <param name="companyName">The company name of an organization sign-up; required for it, ignored otherwise.</param>
    /// <param name="signupKind">The kind of a sign-up request (spec 0010); a private one carries no company name. Ignored for other mails.</param>
    public async Task<MailLimitDecision> SubmitAsync(
        MailKind kind, string normalizedEmail, CancellationToken cancellationToken, string? typedEmail = null, string? companyName = null,
        SignupKind signupKind = SignupKind.Organization)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        if (kind == MailKind.Signup && (typedEmail is null || (signupKind == SignupKind.Organization && companyName is null)))
        {
            throw new ArgumentException(
                "A sign-up request carries the address as typed, and an organization sign-up its company name.",
                typedEmail is null ? nameof(typedEmail) : nameof(companyName));
        }
```

(b) Replace

```csharp
            else if (kind == MailKind.Signup && typedEmail is not null && companyName is not null)
            {
                // The pending sign-up takes the place an account would have: it is what the mail, when it is sent, is about. One
                // statement, whether the address has an account or not (which the request never learns).
                await PendingSignups.UpsertAsync(db, normalizedEmail, typedEmail, companyName, now, cancellationToken);
                audit.Stage(new AuditEntry { Kind = AuditKinds.SignupRequested, SubjectEmail = typedEmail });
            }
```

with

```csharp
            else if (kind == MailKind.Signup && typedEmail is not null)
            {
                // The pending sign-up takes the place an account would have: it is what the mail, when it is sent, is about. One
                // statement, whether the address has an account or not (which the request never learns). The row records its kind.
                await PendingSignups.UpsertAsync(
                    db, normalizedEmail, typedEmail, signupKind == SignupKind.Private ? null : companyName, signupKind, now, cancellationToken);
                audit.Stage(new AuditEntry
                {
                    Kind = AuditKinds.SignupRequested,
                    SubjectEmail = typedEmail,
                    Details = new Dictionary<string, object?> { ["kind"] = SignupKinds.NameOf(signupKind) },
                });
            }
```

In `src/Auth.Server/Signup/PendingSignups.cs` replace

```csharp
    public static async Task UpsertAsync(
        AuthDbContext db, string normalizedEmail, string email, string companyName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(companyName);

        var expiresAt = now + Lifetime;
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "PendingSignups" ("NormalizedEmail", "Email", "CompanyName", "RequestedAt", "TokenHash", "TokenExpiresAt", "ExpiresAt")
            VALUES ({normalizedEmail}, {email}, {companyName}, {now}, NULL, NULL, {expiresAt})
            ON CONFLICT ("NormalizedEmail") DO UPDATE SET
                "TokenHash" = CASE WHEN translate("PendingSignups"."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') = translate(EXCLUDED."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')
                                   THEN "PendingSignups"."TokenHash" END,
                "TokenExpiresAt" = CASE WHEN translate("PendingSignups"."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') = translate(EXCLUDED."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')
                                        THEN "PendingSignups"."TokenExpiresAt" END,
                "ExpiresAt" = CASE WHEN translate("PendingSignups"."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') = translate(EXCLUDED."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')
                                   THEN GREATEST(EXCLUDED."ExpiresAt", "PendingSignups"."TokenExpiresAt")
                                   ELSE EXCLUDED."ExpiresAt" END,
                "Email" = EXCLUDED."Email",
                "CompanyName" = EXCLUDED."CompanyName",
                "RequestedAt" = EXCLUDED."RequestedAt"
            """,
            cancellationToken);
    }
```

with

```csharp
    public static Task UpsertAsync(
        AuthDbContext db, string normalizedEmail, string email, string companyName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(companyName);

        return UpsertAsync(db, normalizedEmail, email, companyName, SignupKind.Organization, now, cancellationToken);
    }

    /// <summary>
    /// As the overload above, with the kind of the sign-up (spec 0010): a private one has no company name. A request of another kind than the
    /// stored one drops the link at once, as a look-alike spelling does: a link mailed for one kind never completes the other.
    /// </summary>
    public static async Task UpsertAsync(
        AuthDbContext db, string normalizedEmail, string email, string? companyName, SignupKind kind, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        ArgumentNullException.ThrowIfNull(email);

        var expiresAt = now + Lifetime;
        var kindValue = (short)kind;
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "PendingSignups" ("NormalizedEmail", "Email", "CompanyName", "Kind", "RequestedAt", "TokenHash", "TokenExpiresAt", "ExpiresAt")
            VALUES ({normalizedEmail}, {email}, {companyName}, {kindValue}, {now}, NULL, NULL, {expiresAt})
            ON CONFLICT ("NormalizedEmail") DO UPDATE SET
                "TokenHash" = CASE WHEN translate("PendingSignups"."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') = translate(EXCLUDED."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')
                                        AND "PendingSignups"."Kind" = EXCLUDED."Kind"
                                   THEN "PendingSignups"."TokenHash" END,
                "TokenExpiresAt" = CASE WHEN translate("PendingSignups"."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') = translate(EXCLUDED."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')
                                             AND "PendingSignups"."Kind" = EXCLUDED."Kind"
                                        THEN "PendingSignups"."TokenExpiresAt" END,
                "ExpiresAt" = CASE WHEN translate("PendingSignups"."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz') = translate(EXCLUDED."Email", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')
                                        AND "PendingSignups"."Kind" = EXCLUDED."Kind"
                                   THEN GREATEST(EXCLUDED."ExpiresAt", "PendingSignups"."TokenExpiresAt")
                                   ELSE EXCLUDED."ExpiresAt" END,
                "Email" = EXCLUDED."Email",
                "CompanyName" = EXCLUDED."CompanyName",
                "Kind" = EXCLUDED."Kind",
                "RequestedAt" = EXCLUDED."RequestedAt"
            """,
            cancellationToken);
    }
```

(If Npgsql refuses the untyped `NULL` parameter of a private sign-up's company name, pass it as an `NpgsqlParameter` of type `Varchar` whose
value is `DBNull.Value`; report it.) Add `using Auth.Infrastructure.Persistence;` if the file lacks it (it has it).

- [ ] **Step 7: The contract and its description.** In `src/Auth.Server/Signup/SignupContracts.cs` replace

```csharp
/// <summary>Request of <c>POST /auth/signup</c>.</summary>
public sealed record SignupRequest(string Email, string CompanyName);
```

with

```csharp
/// <summary>
/// Request of <c>POST /auth/signup</c>: <c>kind</c> is <c>organization</c> (the default) or <c>private</c> (spec 0010); <c>company_name</c> is
/// required for an organization and refused for a private person.
/// </summary>
public sealed record SignupRequest(string Email, string? Kind = null, string? CompanyName = null);
```

In `src/Auth.Server/Api/SignupRoutes.cs` replace

```csharp
                "Asks to start a company. The answer is the same `202` for every address; a mail follows: a notice without a link when the address "
                + "has an account that belongs to a company or only resembles the typed address, otherwise a link to finish (also when the account "
                + "belongs to no company). Off unless the "
                + "manifest says `signup: self-service` and the completion address is configured (`403 signup_disabled`). One request a minute and "
                + "five an hour per address.")
```

with

```csharp
                "Asks to start a company (`kind` `organization`, the default, with `company_name`) or to open a private account (`kind` `private`, "
                + "no `company_name`). The answer is the same `202` for every address; a mail follows: a notice without a link when the address has an "
                + "account, of either kind, otherwise a link to finish. `403 signup_disabled` while the switch of the kind is off (with both off, before "
                + "the body is read). One request a minute and five an hour per address, for both kinds together.")
```

- [ ] **Step 8: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.PrivateSignupRequestTests -class Auth.IntegrationTests.SignupKindSwitchTests -class Auth.IntegrationTests.SignupRequestTests -class Auth.IntegrationTests.SignupDisabledRequestTests -class Auth.IntegrationTests.SignupRateLimitTests -class Auth.IntegrationTests.PendingSignupsTests -class Auth.IntegrationTests.PendingSignupPruningTests -class Auth.IntegrationTests.MailRequestStoreTests -class Auth.IntegrationTests.SignupDispatchTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.JsonCharsetTests`
passes.

- [ ] **Step 9: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): POST /auth/signup takes the kind, judges its switch first and records it`.

---

## Day 4 — Tuesday 14.04: the mails and the completion by kind

When the day is done, a private person signs up through the mail and is signed in to a personal space; any account under the address gets
"you already have an account", for either kind; the comeback of spec 0009, Amendment 3, is gone from the code and the tests.

### Task 10: The dispatcher by kind, and the invitation paragraph

**Files:**
- Modify: `src/Auth.Server/Email/MailDispatcher.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/SignupApi.cs`, `tests/Auth.IntegrationTests/Infrastructure/SignupTestBase.cs`, `tests/Auth.IntegrationTests/SignupOffDispatchTests.cs`, `tests/Auth.IntegrationTests/SignupCompleteRaceTests.cs`, `tests/Auth.IntegrationTests/SignupMailComposerTests.cs`
- Delete (tests): `tests/Auth.IntegrationTests/SignupComebackTests.cs`, `tests/Auth.IntegrationTests/SignupComebackDispatchTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/PrivateSignupDispatchTests.cs`, `tests/Auth.IntegrationTests/PrivateSignupDispatchPolishTests.cs`, `tests/Auth.IntegrationTests/PrivateInvitationMailTests.cs`

**Interfaces:**
- Consumes: `SignupGate.IsOn`, `PrivateOn`, `AnyOn`, `IsOnFor` (Task 2), `PendingSignup.Kind` (Task 1), `MailComposer.Compose(..., privateAccount)` (Task 5), `EmailInput.IsSameAddress`, `CompanyKind`.
- Produces: the dispatcher of spec 0010 → Mails; test side `SignupApi.RequestPrivate(HttpClient, string email)`, `SignupTestBase.MailedPrivateAsync(string email)`; `SignupApi.CompleteWithoutPassword`, `SignupTestBase.RemovedMemberAsync` and `SignupTestBase.JoinACompanyAsync` are removed (Amendment 3 of spec 0009 is removed by spec 0010).

**Contract counts:** none. The tests of the comeback (spec 0009, Amendment 3) go: the files `SignupComebackTests.cs` (13 tests) and `SignupComebackDispatchTests.cs` (9 tests), and in `SignupCompleteRaceTests` the methods `Of_parallel_completions_for_an_account_without_a_company_exactly_one_is_200` and `A_comeback_and_an_invitation_acceptance_for_one_account_never_both_give_it_a_company`. `SignupOffDispatchTests.A_sign_up_request_queued_before_sign_up_was_turned_off_sends_nothing_and_is_dropped` takes a private account in place of the account without a company.

- [ ] **Step 1: Write the failing tests.**

In `tests/Auth.IntegrationTests/Infrastructure/SignupApi.cs` replace

```csharp
    /// <summary>A completion for an account that has no company: no password in the body (spec 0009, Amendment 3).</summary>
    public static Task<HttpResponseMessage> CompleteWithoutPassword(HttpClient client, string token, string companyName) =>
        client.PostAsJsonAsync(CompletePath, new { token, company_name = companyName });
```

with

```csharp
    /// <summary>A private sign-up (spec 0010): the address and the kind, no company name.</summary>
    public static Task<HttpResponseMessage> RequestPrivate(HttpClient client, string email) =>
        client.PostAsJsonAsync(RequestPath, new { email, kind = "private" });
```

and its summary `/// <summary>Drives the four endpoints of spec 0009 over HTTP.</summary>` becomes `/// <summary>Drives the four endpoints of specs 0009 and 0010 over HTTP.</summary>`.

In `tests/Auth.IntegrationTests/Infrastructure/SignupTestBase.cs` replace everything from

```csharp
    /// <summary>
    /// An account that lost its company (spec 0009, Amendment 3): it was a member of a company, with a password (<see cref="MailTestBase.UserPassword"/>),
```

to the closing brace of `JoinACompanyAsync` (the method's last `    }`) with

```csharp
    /// <summary>Asks for a private sign-up (spec 0010; asserting the 202), runs the dispatcher and returns the token of the newest mail to the address.</summary>
    protected async Task<string> MailedPrivateAsync(string email)
    {
        using var response = await SignupApi.RequestPrivate(Client, email);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        await DispatchAsync();
        return TokenIn(Mail.Sent.Last(m => string.Equals(m.To, email, StringComparison.Ordinal)));
    }
```

Delete `tests/Auth.IntegrationTests/SignupComebackTests.cs` and `tests/Auth.IntegrationTests/SignupComebackDispatchTests.cs`. In
`tests/Auth.IntegrationTests/SignupCompleteRaceTests.cs` delete the two methods named above, each from its `[Fact]` or `[Theory]` line (with its
`[InlineData]` lines) to its closing `    }`.

In `tests/Auth.IntegrationTests/SignupOffDispatchTests.cs` replace

```csharp
    [InlineData("account without a company")]
    public async Task A_sign_up_request_queued_before_sign_up_was_turned_off_sends_nothing_and_is_dropped(string address)
    {
        if (address == "account with a company")
        {
            await CreateUserAsync(Address, confirmed: true);
        }
        else if (address == "account without a company")
        {
            await RemovedMemberAsync();
        }
```

with

```csharp
    [InlineData("private account")]
    public async Task A_sign_up_request_queued_before_sign_up_was_turned_off_sends_nothing_and_is_dropped(string address)
    {
        if (address == "account with a company")
        {
            await CreateUserAsync(Address, confirmed: true);
        }
        else if (address == "private account")
        {
            await CreatePrivateAccountAsync(Address);
        }
```

In `tests/Auth.IntegrationTests/SignupMailComposerTests.cs` replace the comment line
`        // The link also serves an account that keeps its password (Amendment 3): the text does not promise a password to choose.` with
`        // The text does not promise a password to choose: the completion screen asks for it.`.

`tests/Auth.IntegrationTests/PrivateSignupDispatchTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Mails: the switch of the kind first, any account gets the notice, the private variant.</summary>
public sealed class PrivateSignupDispatchTests : SignupTestBase
{
    private const string Private = "ola@poczta.test";
    private const string Sentence = "If you want a new account on purpose, delete the one you have first, or sign up with another email address.";

    public PrivateSignupDispatchTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithPrivateSignup();
    }

    private Task<HttpResponseMessage> RequestAsync(string kind, string email = Private) =>
        kind == "private" ? SignupApi.RequestPrivate(Client, email) : SignupApi.Request(Client, email, CompanyName);

    /// <summary>Turns the two switches as a restart with another manifest would.</summary>
    private void Turn(bool organization, bool @private)
    {
        var current = Holder.Current;
        Holder.Set(
            new Manifest(current.Permissions, current.DefaultRoles, organization ? SignupMode.SelfService : SignupMode.InviteOnly, @private, current.PrivatePermissions),
            null);
    }

    private static void AssertNotice(ComposedMailView mail)
    {
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        Assert.Contains(Sentence, mail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("token=", mail.Text + mail.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("://", mail.Text + mail.Html, StringComparison.Ordinal);
    }

    private sealed record ComposedMailView(string Subject, string Text, string Html);

    private ComposedMailView OnlyMail()
    {
        var mail = Assert.Single(Mail.Sent);
        return new ComposedMailView(mail.Subject, mail.TextBody, mail.HtmlBody);
    }

    [Fact]
    public async Task A_private_request_gets_the_private_mail_with_a_link()   // criterion 4
    {
        using (var response = await RequestAsync("private"))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await DispatchAsync();

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Private, mail.To);
        Assert.Equal("Complete your sign-up for " + AuthAppFactory.DefaultAppName, mail.Subject);
        Assert.Contains(
            "Someone asked to create a private account on " + AuthAppFactory.DefaultAppName + " with this email address.",
            mail.TextBody,
            StringComparison.Ordinal);
        Assert.Contains(AuthAppFactory.DefaultCompleteSignupUrl + "?token=", mail.TextBody, StringComparison.Ordinal);
        Assert.NotNull(Assert.Single(await PendingAsync()).TokenHash);
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("private")]
    public async Task An_address_with_a_private_account_gets_the_account_exists_mail_with_the_new_sentence(string kind)   // criterion 5
    {
        await CreatePrivateAccountAsync(Private);

        using (var response = await RequestAsync(kind))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await DispatchAsync();

        AssertNotice(OnlyMail());
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task An_address_with_a_company_account_gets_the_notice_for_a_private_request_too()   // criterion 5
    {
        using (var response = await RequestAsync("private", Factory.SeedEmail))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await DispatchAsync();

        Assert.Equal(Factory.SeedEmail, Assert.Single(Mail.Sent).To);
        AssertNotice(OnlyMail());
        Assert.Empty(await PendingAsync());
    }

    [Theory]
    [InlineData("organization", false, true)]
    [InlineData("private", true, false)]
    [InlineData("private", false, false)]
    public async Task A_mail_whose_kinds_switch_was_turned_off_is_dropped_and_nothing_is_sent(string kind, bool organization, bool @private)   // Mails item 1; criterion 3
    {
        using (var response = await RequestAsync(kind))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Turn(organization, @private);
        await DispatchAsync();

        Assert.Empty(Mail.Sent);
        Assert.Empty(Mail.Attempted);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task The_notice_goes_out_while_either_switch_is_on_once_the_pending_sign_up_is_gone()   // Mails items 1 and 2
    {
        await CreatePrivateAccountAsync(Private);
        using (var response = await RequestAsync("private"))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await InDbAsync(db => db.PendingSignups.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        Turn(organization: true, @private: false);   // the kind's switch is off, but no pending sign-up is left to judge it by
        await DispatchAsync();

        AssertNotice(OnlyMail());
    }

    [Fact]
    public async Task With_both_switches_off_nothing_is_sent_even_to_an_account()   // Mails item 1
    {
        await CreatePrivateAccountAsync(Private);
        using (var response = await RequestAsync("private"))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Turn(organization: false, @private: false);
        await DispatchAsync();

        Assert.Empty(Mail.Sent);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task A_pending_sign_up_of_an_address_that_has_an_account_is_deleted_by_the_notice()   // criterion 5
    {
        var link = await MailedAsync(Private);   // an organization link, before any account
        await CreatePrivateAccountAsync(Private);
        Clock.Advance(PastTheLimit);
        using (var again = await RequestAsync("organization"))
        {
            Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        }

        await DispatchAsync();

        AssertNotice(new ComposedMailView(Mail.Sent[^1].Subject, Mail.Sent[^1].TextBody, Mail.Sent[^1].HtmlBody));
        Assert.Empty(await PendingAsync());
        using var preview = await SignupApi.Preview(Client, link);
        await AccountApi.AssertErrorAsync(preview, AccountApi.InvalidToken);
    }
}
```

`tests/Auth.IntegrationTests/PrivateSignupDispatchPolishTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Mails: the private mail and the new sentence in the instance's language.</summary>
public sealed class PrivateSignupDispatchPolishTests : SignupTestBase
{
    public PrivateSignupDispatchPolishTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithPrivateSignup().WithSetting(MailSettingsLoader.LocaleKey, "pl");
    }

    [Fact]
    public async Task The_private_mail_and_the_notice_are_in_polish()   // criteria 4, 5, 31
    {
        using (var request = await SignupApi.RequestPrivate(Client, "ola@poczta.test"))
        {
            Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        }

        await CreatePrivateAccountAsync("jan@poczta.test");
        using (var notice = await SignupApi.RequestPrivate(Client, "jan@poczta.test"))
        {
            Assert.Equal(HttpStatusCode.Accepted, notice.StatusCode);
        }

        await DispatchAsync();

        var link = Assert.Single(Mail.Sent, m => m.To == "ola@poczta.test");
        Assert.Contains("poprosił o założenie konta prywatnego w " + AuthAppFactory.DefaultAppName, link.TextBody, StringComparison.Ordinal);
        var exists = Assert.Single(Mail.Sent, m => m.To == "jan@poczta.test");
        Assert.Equal("Masz już konto w " + AuthAppFactory.DefaultAppName, exists.Subject);
        Assert.Contains("Jeśli celowo chcesz założyć nowe konto, najpierw usuń dotychczasowe albo zarejestruj się z innym adresem e-mail.", exists.TextBody, StringComparison.Ordinal);
    }
}
```

`tests/Auth.IntegrationTests/PrivateInvitationMailTests.cs`:

```csharp
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Invitations, the mail: the paragraph for an address that has a private account spelled the same up to ASCII case.</summary>
public sealed class PrivateInvitationMailTests(PostgresFixture postgres, KeyMaterialFixture keys) : TenancyTestBase(postgres, keys)
{
    private const string Paragraph = "You already have a private account on " + AuthAppFactory.DefaultAppName + " with this email address.";

    private async Task<ComposedMail> InvitationMailAsync(string address)
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, address, "user");
        await EnqueueInvitationAsync(invite, address);
        await DispatchAsync();
        return Mail.Sent.Last(m => m.To == address);
    }

    [Fact]
    public async Task An_invitation_to_an_address_with_a_private_account_has_the_changed_intro_and_the_paragraph()   // criterion 16
    {
        await CreatePrivateAccountAsync("ola@poczta.test");

        var mail = await InvitationMailAsync("ola@poczta.test");

        Assert.Contains(Paragraph, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(Paragraph, mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Use the link below to accept the invitation.", mail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("choose a password", mail.TextBody, StringComparison.Ordinal);
        Assert.Equal("You are invited to " + AuthAppFactory.DefaultAppName, mail.Subject);
    }

    [Fact]
    public async Task Other_ascii_capitals_of_the_address_get_the_paragraph_too()   // criterion 16: spelled the same up to ASCII case
    {
        await CreatePrivateAccountAsync("Ola@Poczta.test");

        var mail = await InvitationMailAsync("ola@POCZTA.test");

        Assert.Contains(Paragraph, mail.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_without_an_account_or_with_a_company_account_gets_the_mail_of_spec_0005()   // criterion 16
    {
        await AddMemberAsync(await DevCompanyIdAsync(), "member@poczta.test", "user");

        var unknown = await InvitationMailAsync("nobody@poczta.test");
        var member = await InvitationMailAsync("member@poczta.test");

        foreach (var mail in new[] { unknown, member })
        {
            Assert.DoesNotContain("private account", mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
            Assert.Contains("choose a password and accept the invitation", mail.TextBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_look_alike_spelling_of_a_private_account_gets_no_paragraph()   // criterion 16
    {
        // The Greek small mu and the micro sign: the normaliser folds them into one key, but they are two mailboxes.
        var account = char.ConvertFromUtf32(0x03BC) + "aria@poczta.test";
        var invited = char.ConvertFromUtf32(0x00B5) + "aria@poczta.test";
        await CreatePrivateAccountAsync(account);

        var mail = await InvitationMailAsync(invited);

        Assert.DoesNotContain("private account", mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: See them fail.** Build, then run the new classes (command of Step 6): the private request's mail fails to compose (no
company), a private account gets a link, and no invitation has the paragraph.

- [ ] **Step 3: The bulk delete and the choice of the next row.** In `src/Auth.Server/Email/MailDispatcher.cs`:

(a) In `DropUnmailableAsync` replace

```csharp
        var signup = (short)MailKind.Signup;
        var signupOn = gate.IsOn;
```

with

```csharp
        var signup = (short)MailKind.Signup;
        var organization = (short)SignupKind.Organization;
        var privateKind = (short)SignupKind.Private;
        var organizationOn = gate.IsOn;
        var privateOn = gate.PrivateOn;
        var anyOn = gate.AnyOn;
```

and replace

```csharp
                  AND ((r."Kind" = {signup}
                        AND (NOT {signupOn}
                             OR (NOT EXISTS (
                                     SELECT 1 FROM "PendingSignups" p
                                     WHERE p."NormalizedEmail" = r."NormalizedEmail" AND p."ExpiresAt" > {now})
                                 AND NOT EXISTS (
                                     SELECT 1 FROM "AspNetUsers" a
                                     WHERE a."NormalizedEmail" = r."NormalizedEmail" AND a."Email" IS NOT NULL
                                       AND EXISTS (SELECT 1 FROM "Memberships" m WHERE m."UserId" = a."Id")))))
```

with

```csharp
                  AND ((r."Kind" = {signup}
                        AND (EXISTS (
                                 SELECT 1 FROM "PendingSignups" p
                                 WHERE p."NormalizedEmail" = r."NormalizedEmail" AND p."ExpiresAt" > {now}
                                   AND ((p."Kind" = {organization} AND NOT {organizationOn}) OR (p."Kind" = {privateKind} AND NOT {privateOn})))
                             OR (NOT EXISTS (
                                     SELECT 1 FROM "PendingSignups" p
                                     WHERE p."NormalizedEmail" = r."NormalizedEmail" AND p."ExpiresAt" > {now})
                                 AND (NOT {anyOn}
                                      OR NOT EXISTS (
                                          SELECT 1 FROM "AspNetUsers" a
                                          WHERE a."NormalizedEmail" = r."NormalizedEmail" AND a."Email" IS NOT NULL)))))
```

and in its doc comment replace the two lines as they stand (the second sentence spans the line break)

```csharp
    /// already; a sign-up row is removed when sign-up is off, or when its address has neither an account that belongs to a company nor a live
    /// (unexpired) pending sign-up. Those requests were answered with 202 like any other, and end here.
```

with

```csharp
    /// already; a sign-up row is removed when its live pending sign-up's kind is switched off, or, with no live pending sign-up, when both
    /// switches are off or the address has no account (spec 0010 → Mails). Those requests were answered with 202 like any other, and end here.
```

(b) In `DispatchOneAsync` the pick carries no switch: a sign-up row whose kind was turned off, even during this pass, is still picked, and
`PrepareSignupMailAsync` (Step 4), which checks the switch first and returns `null`, drops it in the same pass (spec 0010 → Mails, item 1;
`SignupOffDispatchTests.A_sign_up_mail_that_comes_up_after_sign_up_was_turned_off_during_the_pass_is_dropped` stays as it is). The bulk delete
of (a) keeps its switch terms. Leave `var signup = (short)MailKind.Signup;` as it is and replace

```csharp
                            OR (r."Kind" = {signup}
                                AND (EXISTS (
                                         SELECT 1 FROM "PendingSignups" p
                                         WHERE p."NormalizedEmail" = r."NormalizedEmail" AND p."ExpiresAt" > {now})
                                     OR EXISTS (
                                         SELECT 1 FROM "AspNetUsers" a
                                         WHERE a."NormalizedEmail" = r."NormalizedEmail" AND a."Email" IS NOT NULL
                                           AND EXISTS (SELECT 1 FROM "Memberships" m WHERE m."UserId" = a."Id"))))
```

with

```csharp
                            OR (r."Kind" = {signup}
                                AND (EXISTS (
                                         SELECT 1 FROM "PendingSignups" p
                                         WHERE p."NormalizedEmail" = r."NormalizedEmail" AND p."ExpiresAt" > {now})
                                     OR (NOT EXISTS (
                                             SELECT 1 FROM "PendingSignups" p
                                             WHERE p."NormalizedEmail" = r."NormalizedEmail" AND p."ExpiresAt" > {now})
                                         AND EXISTS (
                                             SELECT 1 FROM "AspNetUsers" a
                                             WHERE a."NormalizedEmail" = r."NormalizedEmail" AND a."Email" IS NOT NULL))))
```

and the comment above it `// one still to confirm; for a sign-up, an account that belongs to a company or a live pending sign-up; an invitation always), or any` becomes
`// one still to confirm; for a sign-up, a live pending sign-up, or with none an account (the switch is checked when it is sent); an invitation always), or any`.

- [ ] **Step 4: The sign-up mail.** Replace the method `PrepareSignupMailAsync` with its doc comment (from the line
`    /// The mail of a sign-up (spec 0009): decided here, when it is sent, and never by the request. The account check comes first: with an account`
back to its `/// <summary>` line, through the method's closing brace) with

```csharp
    /// <summary>
    /// The mail of a sign-up (specs 0009 and 0010 → Mails): decided here, when it is sent, and never by the request. The live pending sign-up of
    /// the address is locked first (a completion waits for the mail; the deletion of an account deletes the mail requests before the pending
    /// sign-up, so the two never wait for each other the other way round). The switch is checked first: while the pending sign-up exists the
    /// switch of its kind must be on; when it is gone, at least one switch. Otherwise nothing is sent and the request is dropped. Then any account
    /// under the address, of either kind and under any spelling the normaliser folds together, read under the lock, gets the mail that says so,
    /// to the address as the account stores it, with no link, and the pending sign-up is deleted in the transaction of the send (a failed send
    /// keeps it). Otherwise a live pending sign-up gets a new link token (the earlier link stops working) and the mail in the variant of its kind,
    /// to the address as typed. With neither, there is nothing to send.
    /// </summary>
    private async Task<Func<Task<ComposedMail>>?> PrepareSignupMailAsync(
        AuthDbContext db, MailRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = await PendingSignups.LockUsableAsync(db, request.NormalizedEmail, now, cancellationToken);
        if (pending is null ? !gate.AnyOn : !gate.IsOnFor(pending.Kind))
        {
            return null;   // switched off after the request: no mail of it, the notice included
        }

        var account = await SignupAccounts.FindAsync(db, request.NormalizedEmail, cancellationToken);
        if (account is not null)
        {
            return AccountExistsMail(db, request.NormalizedEmail, account.Email, cancellationToken);
        }

        if (pending is null)
        {
            return null;
        }

        return async () =>
        {
            var token = await PendingSignups.IssueAsync(db, pending.NormalizedEmail, now, cancellationToken);
            return pending.Kind == SignupKind.Private
                ? composer.Compose(MailKind.Signup, pending.Email, token, privateAccount: true)
                : composer.Compose(MailKind.Signup, pending.Email, token, pending.CompanyName);
        };
    }
```

- [ ] **Step 5: The invitation paragraph.** In `PrepareInvitationAsync` replace

```csharp
        var roleName = await db.CompanyRoles.AsNoTracking().Where(r => r.Id == invite.RoleId).Select(r => r.Name).SingleAsync(cancellationToken);
        return async () =>
        {
            var token = await InviteTokens.IssueAsync(db, invite.Id, now, cancellationToken);
            return composer.Compose(MailKind.Invitation, invite.Email, token, companyName, roleName);
        };
```

with

```csharp
        var roleName = await db.CompanyRoles.AsNoTracking().Where(r => r.Id == invite.RoleId).Select(r => r.Name).SingleAsync(cancellationToken);

        // Spec 0010: an address that has a private account, spelled as the invitation spells it up to ASCII case, is told what accepting does to
        // it. Decided now, when the mail is composed; the preview decides again when the link is used.
        var account = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == invite.NormalizedEmail)
            .OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.Email })
            .FirstOrDefaultAsync(cancellationToken);
        var privateAccount = account is { Email: { } stored }
            && EmailInput.IsSameAddress(stored, invite.Email)
            && await (
                from membership in db.Memberships.AsNoTracking()
                join space in db.Companies.AsNoTracking() on membership.CompanyId equals space.Id
                where membership.UserId == account.Id && space.Kind == CompanyKind.Personal
                select membership.UserId).AnyAsync(cancellationToken);
        return async () =>
        {
            var token = await InviteTokens.IssueAsync(db, invite.Id, now, cancellationToken);
            return composer.Compose(MailKind.Invitation, invite.Email, token, companyName, roleName, privateAccount);
        };
```

and add `using Auth.Server.Requests;` to the file's `using`s.

(c) In the class summary, replace the sentences from
`/// A sign-up request (spec 0009) names a pending sign-up, not an account, and is decided here: an address whose account belongs to a company gets`
to `/// settings turned sign-up off is dropped when it comes up, so that an instance that has it off never mails about it.` with

```csharp
/// A sign-up request (specs 0009 and 0010) names a pending sign-up, not an account, and is decided here: the switch of its kind first, then any
/// account under the address gets the mail that says so (no link), and otherwise a live pending sign-up gets a new link in the variant of its kind;
/// with neither there is nothing to send. A request queued before the manifest or the settings turned its kind off is dropped when it comes up.
```

and in the comment of `DispatchOneAsync` that starts `// The account went away, or was confirmed, or the invitation was cancelled, or the pending sign-up expired or`, the next line
`// was completed, with no account that belongs to a company under the address, after the pass began, or sign-up is off.` becomes
`// was completed with no account under the address, after the pass began, or its kind of sign-up is off.`

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.PrivateSignupDispatchTests -class Auth.IntegrationTests.PrivateSignupDispatchPolishTests -class Auth.IntegrationTests.PrivateInvitationMailTests -class Auth.IntegrationTests.SignupDispatchTests -class Auth.IntegrationTests.SignupDispatchPolishTests -class Auth.IntegrationTests.SignupOffDispatchTests -class Auth.IntegrationTests.SignupWithoutAddressDispatchTests -class Auth.IntegrationTests.SignupCompleteRaceTests -class Auth.IntegrationTests.SignupCompleteTests -class Auth.IntegrationTests.SignupPreviewTests -class Auth.IntegrationTests.SignupRequestTests -class Auth.IntegrationTests.InvitationDispatchTests -class Auth.IntegrationTests.MailDispatcherTests -class Auth.IntegrationTests.SignupMailComposerTests`
passes. (`SignupDispatchPolishTests` is the existing class of spec 0009, declared in `SignupDispatchTests.cs`.)

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files, and the two deletions. Scan the touched files. Proposed subject:
`feat(mail): sign-up mails follow the switch of their kind and any account gets the notice; the invitation warns a private account`.

### Task 11: The preview and the completion of both kinds

**Files:**
- Modify: `src/Auth.Server/Signup/SignupService.cs` (replaced as a whole), `src/Auth.Server/Signup/SignupAccounts.cs` (replaced as a whole), `src/Auth.Server/Signup/SignupPreviewEndpoint.cs`, `src/Auth.Server/Signup/SignupCompleteEndpoint.cs`, `src/Auth.Server/Signup/SignupContracts.cs`, `src/Auth.Server/Api/SignupRoutes.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/SignupApi.cs`, `tests/Auth.IntegrationTests/SignupPreviewTests.cs`, `tests/Auth.IntegrationTests/SignupCompleteTests.cs`, `tests/Auth.IntegrationTests/SignupCompleteSignInTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/PrivateSignupCompleteTests.cs`, `tests/Auth.IntegrationTests/PrivateSignupSwitchTests.cs`

**Interfaces:**
- Consumes: `SignupGate.AnyOn`, `IsOnFor` (Task 2), `PersonalSpaces.Add` (Task 1), `AddressLock` (Task 8), `SignupKinds` (Task 9), `PendingSignupSeed` with a kind (Task 9).
- Produces: `SignupPreview(SignupKind Kind, string Email, string? CompanyName)`; `SignupService(..., ManifestHolder manifest, SignupGate gate, TimeProvider clock, AuditLog audit)`; `SignupService.CompleteAsync(string token, string? password, string? companyName, CancellationToken)` (refusals: `invalid_token`, `signup_disabled`, `invalid_request`, `already_registered`, `weak_password`); `SignupAccount(Guid Id, string Email)` (any account); `SignupPreviewResponse(string Kind, string Email, string? CompanyName)` (no `company_name` member when null); `SignupCompleteRequest(string Token, string? Password = null, string? CompanyName = null)`; test side `SignupApi.CompletePrivate(HttpClient, string token, string password)`.

**Contract counts:** `OpenApiTests.The_responses_are_described_in_snake_case`: `SignupPreviewResponse` becomes `["company_name", "email", "kind"]`; `OpenApiTests.The_completion_asks_for_no_password_of_an_account_without_a_company` (Amendment 3) is replaced by `The_completion_takes_the_token_an_optional_password_and_an_optional_company_name`. `SignupPreviewTests.Preview_answers_the_typed_address_and_the_company_and_changes_nothing` reads `kind` and never `has_account`; `SignupCompleteTests.A_completion_is_recorded_with_the_account_the_company_and_the_address_and_nothing_secret` reads `details.kind`. `SignupCompleteSignInTests.Every_refusal_is_what_it_was_and_issues_no_cookie_and_no_token`: the body `{"token":"x"}` (no company name, a token never issued) was `400 invalid_request` and is now `400 invalid_token`, the change the spec announces (Complete: the members the kind takes are judged after the token).

**Decisions the plan takes here:**
- The kind's switch and the members the kind takes are judged after the token is read and before the address lock and the use of the token, inside the transaction that is rolled back: the token stays usable (spec, Complete steps 4 and 5).
- `signup.completed` of a private sign-up names the personal space (`org_id`) and `private` (`org_name`), as its `login.succeeded` row does.

- [ ] **Step 1: Write the failing tests.**

In `tests/Auth.IntegrationTests/Infrastructure/SignupApi.cs`, after `RequestPrivate` add

```csharp

    /// <summary>The completion of a private sign-up (spec 0010): the token and the password, no company name.</summary>
    public static Task<HttpResponseMessage> CompletePrivate(HttpClient client, string token, string password) =>
        client.PostAsJsonAsync(CompletePath, new { token, password });
```

`tests/Auth.IntegrationTests/PrivateSignupCompleteTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → preview and complete of a private sign-up, and of an organization sign-up as the kind changes it.</summary>
public sealed class PrivateSignupCompleteTests : SignupTestBase
{
    private const string Private = "ola@poczta.test";
    private const string WeakRules = """{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}""";

    public PrivateSignupCompleteTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithPrivateSignup();
    }

    private Task<int> UsersWithAsync(string normalized) =>
        InDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == normalized, TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_private_person_signs_up_through_the_mail_and_is_signed_in_with_a_personal_space()   // criteria 8, 9
    {
        var token = await MailedPrivateAsync(Private);

        using (var preview = await SignupApi.Preview(Client, token))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Equal("""{"kind":"private","email":"ola@poczta.test"}""", await preview.Content.ReadAsStringAsync());
        }

        using var done = await SignupApi.CompletePrivate(Client, token, Password);
        var session = await SessionApi.ReadSessionAsync(done);   // the answer of a login: the access token and the refresh cookie

        var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "OLA@POCZTA.TEST", TestContext.Current.CancellationToken));
        Assert.True(user.EmailConfirmed);
        var space = await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.UserId == user.Id)
            .Join(db.Companies.AsNoTracking(), m => m.CompanyId, c => c.Id, (m, c) => c).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(CompanyKind.Personal, space.Kind);
        Assert.Equal(PersonalSpaces.Name, space.Name);
        Assert.Single(await InDbAsync(db => db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == space.Id).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.CompanyId == space.Id, TestContext.Current.CancellationToken)));

        Assert.Equal(space.Id.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal("personal", AccessTokens.Text(session.AccessToken, "org_kind"));
        Assert.Equal(["private"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(["reports:approve", "reports:read"], AccessTokens.Array(session.AccessToken, "permissions"));

        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal(user.Id, completed.SubjectUserId);
        Assert.Equal(space.Id, completed.OrgId);
        Assert.Equal(PersonalSpaces.Name, completed.OrgName);
        Assert.Equal("private", AuditApi.Text(completed, "kind"));
        Assert.Equal(["kind"], AuditApi.DetailNames(completed));
        Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded), row => row.SubjectUserId == user.Id && row.OrgId == space.Id);
        Assert.DoesNotContain(await AuditAsync(), row => row.Kind.StartsWith("org.", StringComparison.Ordinal) && row.OrgId == space.Id);
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task A_company_name_a_missing_password_or_a_weak_one_is_400_and_the_link_still_works()   // criterion 10
    {
        var token = await MailedPrivateAsync(Private);

        using (var named = await AccountApi.PostRaw(Client, SignupApi.CompletePath, $$"""{"token":"{{token}}","password":"{{Password}}","company_name":"Firma"}"""))
        {
            await AccountApi.AssertErrorAsync(named, AccountApi.InvalidRequest);
        }

        using (var missing = await AccountApi.PostRaw(Client, SignupApi.CompletePath, $$"""{"token":"{{token}}"}"""))
        {
            await AccountApi.AssertErrorAsync(missing, AccountApi.InvalidRequest);
        }

        using (var weak = await SignupApi.CompletePrivate(Client, token, "abc"))
        {
            await AccountApi.AssertErrorAsync(weak, WeakRules);
        }

        Assert.Equal(0, await UsersWithAsync("OLA@POCZTA.TEST"));
        using var good = await SignupApi.CompletePrivate(Client, token, Password);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("company")]
    public async Task An_address_that_has_any_account_by_then_is_409_and_the_pending_sign_up_is_gone(string account)   // criterion 10
    {
        var token = await MailedPrivateAsync(Private);
        if (account == "private")
        {
            await CreatePrivateAccountAsync(Private);
        }
        else
        {
            await AddMemberAsync(await DevCompanyIdAsync(), Private, "user");
        }

        using var response = await SignupApi.CompletePrivate(Client, token, Password);

        await SignupApi.AssertAlreadyRegisteredAsync(response);
        Assert.Empty(await PendingAsync());
        Assert.Equal(1, await UsersWithAsync("OLA@POCZTA.TEST"));
    }

    [Fact]
    public async Task Of_parallel_private_completions_with_one_token_exactly_one_succeeds()   // criterion 10
    {
        var token = await MailedPrivateAsync(Private);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => SignupApi.CompletePrivate(Client, token, Password))));
        try
        {
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            foreach (var refused in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            {
                await AccountApi.AssertErrorAsync(refused, AccountApi.InvalidToken);
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Assert.Equal(1, await UsersWithAsync("OLA@POCZTA.TEST"));
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Kind == CompanyKind.Personal, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task An_organization_completion_says_organization_in_the_token_and_its_kind_in_the_audit_row()   // criterion 11
    {
        var token = await MailedAsync();

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(done);

        Assert.Equal("organization", AccessTokens.Text(session.AccessToken, "org_kind"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal("organization", AuditApi.Text(completed, "kind"));
        Assert.Equal(["kind"], AuditApi.DetailNames(completed));
    }

    [Fact]
    public async Task An_organization_completion_without_a_company_name_is_400_and_the_token_stays_usable()   // spec 0010 → Complete, step 5
    {
        var token = await MailedAsync();

        using (var unnamed = await SignupApi.CompletePrivate(Client, token, Password))
        {
            await AccountApi.AssertErrorAsync(unnamed, AccountApi.InvalidRequest);
        }

        using var named = await SignupApi.Complete(Client, token, Password, CompanyName);
        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
    }

    [Fact]
    public async Task A_missing_company_name_or_password_with_an_unknown_token_is_invalid_token()   // spec 0010, Settled 5
    {
        var unknown = new string('x', 43);

        using var noName = await SignupApi.CompletePrivate(Client, unknown, Password);
        using var noPassword = await AccountApi.PostRaw(Client, SignupApi.CompletePath, $$"""{"token":"{{unknown}}"}""");

        await AccountApi.AssertErrorAsync(noName, AccountApi.InvalidToken);
        await AccountApi.AssertErrorAsync(noPassword, AccountApi.InvalidToken);
    }

    [Fact]
    public async Task A_private_link_after_private_sign_up_was_turned_off_is_403_and_stays_usable()   // criterion 3
    {
        var token = await MailedPrivateAsync(Private);
        var on = Holder.Current;
        Holder.Set(new Manifest(on.Permissions, on.DefaultRoles, on.Signup, privateSignup: false, on.PrivatePermissions), null);   // organization stays on

        using (var preview = await SignupApi.Preview(Client, token))
        {
            await SignupApi.AssertDisabledAsync(preview);
        }

        using (var refused = await SignupApi.CompletePrivate(Client, token, Password))
        {
            await SignupApi.AssertDisabledAsync(refused);
        }

        Assert.Single(await PendingAsync());
        Holder.Set(on, null);
        using var done = await SignupApi.CompletePrivate(Client, token, Password);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
    }
}
```

`tests/Auth.IntegrationTests/PrivateSignupSwitchTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010, criterion 3: the preview and the completion of a link whose kind is switched off, with the other kind on.</summary>
public sealed class PrivateSignupSwitchTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static async Task<int> PendingCountAsync(AuthAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().PendingSignups.CountAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task With_only_organization_sign_up_on_a_private_link_is_403_and_stays()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup();
        using var client = factory.CreateClient();
        var token = await PendingSignupSeed.IssueAsync(factory.Services, "ola@poczta.test", null, SignupKind.Private);

        using var preview = await SignupApi.Preview(client, token);
        using var complete = await SignupApi.CompletePrivate(client, token, "Chosen-Passw0rd");

        await SignupApi.AssertDisabledAsync(preview);
        await SignupApi.AssertDisabledAsync(complete);
        Assert.Equal(1, await PendingCountAsync(factory));
    }

    [Fact]
    public async Task With_only_private_sign_up_on_an_organization_link_is_403_and_stays()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup(organization: false);
        using var client = factory.CreateClient();
        var token = await PendingSignupSeed.IssueAsync(factory.Services, "anna@firma.test", "Firma");

        using var preview = await SignupApi.Preview(client, token);
        using var complete = await SignupApi.Complete(client, token, "Chosen-Passw0rd", "Firma");

        await SignupApi.AssertDisabledAsync(preview);
        await SignupApi.AssertDisabledAsync(complete);
        Assert.Equal(1, await PendingCountAsync(factory));
    }
}
```

Change the existing tests:

(a) `tests/Auth.IntegrationTests/SignupPreviewTests.cs`: replace

```csharp
            Assert.Equal(["company_name", "email", "has_account"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.False(body.GetProperty("has_account").GetBoolean());   // a new address
```

with

```csharp
            Assert.Equal(["kind", "email", "company_name"], body.EnumerateObject().Select(p => p.Name));   // spec 0010: the kind first, never has_account
            Assert.Equal("organization", body.GetProperty("kind").GetString());
```

(b) `tests/Auth.IntegrationTests/SignupCompleteTests.cs`: in `A_completion_is_recorded_with_the_account_the_company_and_the_address_and_nothing_secret` replace `        Assert.Null(completed.Details);` with

```csharp
        Assert.Equal("organization", AuditApi.Text(completed, "kind"));   // spec 0010: details.kind, no existing_account
        Assert.Equal(["kind"], AuditApi.DetailNames(completed));
```

(c) `tests/Auth.IntegrationTests/OpenApiTests.cs`: replace `        Assert.Equal(["company_name", "email", "has_account"], Properties("SignupPreviewResponse"));` with
`        Assert.Equal(["company_name", "email", "kind"], Properties("SignupPreviewResponse"));`, and replace the whole method
`The_completion_asks_for_no_password_of_an_account_without_a_company` (from its `[Fact]` to its closing `    }`) with

```csharp
    [Fact]
    public async Task The_completion_takes_the_token_an_optional_password_and_an_optional_company_name()   // spec 0010 → Complete
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var document = await DescriptionAsync(factory);
        var operation = Operations(document)["POST /auth/signup/complete"];
        var schema = operation["requestBody"]!["content"]!["application/json"]!["schema"]!.AsObject();
        if (schema["$ref"] is { } reference)
        {
            schema = document["components"]!["schemas"]![reference.GetValue<string>().Split('/')[^1]]!.AsObject();
        }

        Assert.NotNull(schema["properties"]!["password"]);
        Assert.NotNull(schema["properties"]!["company_name"]);
        Assert.Contains("private", operation["description"]!.GetValue<string>(), StringComparison.Ordinal);
        if (schema["required"] is System.Text.Json.Nodes.JsonArray required)
        {
            Assert.DoesNotContain("company_name", required.Select(node => node!.GetValue<string>()));
        }
    }
```

(d) `tests/Auth.IntegrationTests/SignupCompleteSignInTests.cs`: in `Every_refusal_is_what_it_was_and_issues_no_cookie_and_no_token` replace

```csharp
        using (var malformed = await AccountApi.PostRaw(Client, SignupApi.CompletePath, """{"token":"x"}"""))
        {
            await AccountApi.AssertErrorAsync(malformed, AccountApi.InvalidRequest);
        }
```

with

```csharp
        using (var malformed = await AccountApi.PostRaw(Client, SignupApi.CompletePath, """{"token":"x"}"""))
        {
            await AccountApi.AssertErrorAsync(malformed, AccountApi.InvalidToken);   // spec 0010: the token is judged before the company name
        }
```

- [ ] **Step 2: See them fail.** Build, then run `PrivateSignupCompleteTests` (command of Step 8): the preview of a private link answers a
company name, and its completion makes a company.

- [ ] **Step 3: Any account.** Replace the content of `src/Auth.Server/Signup/SignupAccounts.cs` with

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Signup;

/// <summary>
/// The account under the address of a sign-up (spec 0010 → Mails and Complete): any account, of either kind, under any spelling the normaliser
/// folds together; its id and its address as it stores it (the "you already have an account" mail goes to that address).
/// </summary>
public sealed record SignupAccount(Guid Id, string Email);

public static class SignupAccounts
{
    /// <summary>The first account (by id) under the normalised address; <see langword="null"/> when there is none.</summary>
    public static async Task<SignupAccount?> FindAsync(AuthDbContext db, string normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        var row = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == normalizedEmail)
            .OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.Email })
            .FirstOrDefaultAsync(cancellationToken);
        return row is { Email: { } email } ? new SignupAccount(row.Id, email) : null;
    }
}
```

- [ ] **Step 4: The service.** Replace the content of `src/Auth.Server/Signup/SignupService.cs` with

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Auth.Server.Signup;

/// <summary>
/// What the completion screen shows before it asks for anything (spec 0010 → preview): the kind, the address as it was typed and, for an
/// organization, the company name of the request.
/// </summary>
public sealed record SignupPreview(SignupKind Kind, string Email, string? CompanyName);

/// <summary>
/// Previews and completes a sign-up by its link token (specs 0009 and 0010). Completing is one transaction: it uses the pending sign-up up, makes
/// the account (email confirmed) and its space — for an organization the company with a copy of the default roles and the membership with the
/// first admin's role, for a private person the personal space with its role row and the membership — writes the audit rows, and leaves the
/// transaction open for the sign-in (spec 0009, Amendment 2). When anything is refused nothing changes, the pending sign-up included, except that
/// an account found under the address deletes it.
/// </summary>
public sealed class SignupService(
    AuthDbContext db, UserManager<ApplicationUser> users, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations,
    CompanyService companies, ManifestHolder manifest, SignupGate gate, TimeProvider clock, AuditLog audit)
{
    /// <summary>The kind, the address and (for an organization) the company name of a usable link; <see langword="null"/> for any other token. It changes nothing.</summary>
    public async Task<SignupPreview?> PreviewAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var pending = await PendingSignups.FindUsableAsync(db, EmailTokens.HashOf(token), StorableTime.Now(clock), cancellationToken);
        return pending is null
            ? null
            : new SignupPreview(pending.Kind, pending.Email, pending.Kind == SignupKind.Private ? null : pending.CompanyName);
    }

    /// <summary>
    /// Completes the sign-up as far as the database goes, in the order of spec 0010 → Complete: the token (<c>invalid_token</c>); the switch of
    /// its kind (<c>signup_disabled</c>); the members the kind takes, a company name for an organization and none for a private person
    /// (<c>invalid_request</c>; the token stays usable); then, under the address lock and with the token used up, any account under the address
    /// (<c>already_registered</c>, whatever the password; the pending sign-up is deleted); the password (<c>invalid_request</c> when there is none,
    /// <c>weak_password</c> with the rules in <see cref="SignupCompletion.BrokenRules"/>; the token stays usable). The caller has checked
    /// <paramref name="companyName"/>, when there is one, against the name rule and <paramref name="password"/>, when there is one, for NUL.
    /// On success nothing is committed: the account, the space and the audit rows are saved inside a transaction that is handed to the caller in
    /// <see cref="SignupCompletion.Transaction"/>, because the completion signs the person in and the sign-in is part of it.
    /// </summary>
    public async Task<SignupCompletion> CompleteAsync(string token, string? password, string? companyName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var hash = EmailTokens.HashOf(token);
        var now = StorableTime.Now(clock);

        // Leaving this method without handing the transaction over rolls everything back, the use of the token included.
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var handedOver = false;
        try
        {
            // Which address, and which kind, is this link for? Read first: the address is what is locked, and only the token knows the kind.
            var link = await db.PendingSignups.AsNoTracking()
                .Where(p => p.TokenHash == hash && p.TokenExpiresAt > now)
                .Select(p => new { p.NormalizedEmail, p.Kind })
                .FirstOrDefaultAsync(cancellationToken);
            if (link is null)
            {
                return Refused(TenancyErrors.InvalidToken);
            }

            if (!gate.IsOnFor(link.Kind))
            {
                return Refused(TenancyErrors.SignupDisabled);   // a request made while its kind was on, completed after it was turned off
            }

            if ((link.Kind == SignupKind.Organization) != (companyName is not null))
            {
                return Refused(TenancyErrors.InvalidRequest);   // an organization names its company; a private person names none
            }

            // One address, one account-maker at a time: the lock invitation acceptance and self-deletion take, so that whichever comes second sees
            // what the first one made.
            await AddressLock.AcquireAsync(db, link.NormalizedEmail, cancellationToken);

            // The token is used up by one statement: of parallel completions with one token one gets the row, and the others find nothing.
            var pending = await PendingSignups.ConsumeAsync(db, hash, now, cancellationToken);
            if (pending is null)
            {
                return Refused(TenancyErrors.InvalidToken);
            }

            // Any account under the address, of either kind and any spelling, read under the lock. Committed, not rolled back: the pending
            // sign-up is gone, as the contract says.
            if (await SignupAccounts.FindAsync(db, pending.NormalizedEmail, cancellationToken) is not null)
            {
                await transaction.CommitAsync(CancellationToken.None);
                return Refused(TenancyErrors.AlreadyRegistered);
            }

            if (password is null)
            {
                return Refused(TenancyErrors.InvalidRequest);   // nothing was changed for good
            }

            var account = new ApplicationUser { UserName = pending.Email, Email = pending.Email, EmailConfirmed = true };
            var broken = await PasswordRules.BrokenAsync(users, account, password);
            if (broken.Count > 0)
            {
                return new SignupCompletion(TenancyErrors.WeakPassword, broken);
            }

            // The link came through the mailbox of the address: that proves it, and it is why the email is confirmed and a password is set here.
            AccountSessions.Ensure(await users.CreateAsync(account, password), "Could not set up the account");

            // A new account has no sessions or links yet, but its address may have a login streak: streaks do not need an account.
            await AccountSessions.EndAllAsync(db, tokens, authorizations, account, cancellationToken);

            var (spaceId, spaceName) = pending.Kind == SignupKind.Private
                ? (PersonalSpaces.Add(db, account.Id, now), PersonalSpaces.Name)
                : await CreateCompanyAsync(account, companyName ?? throw new InvalidOperationException("An organization sign-up has a company name."), now, cancellationToken);

            // The person who holds the link is anonymous: no actor. Who, where and what kind; never the link or the password. A personal space
            // writes no org.* row (spec 0010): its life is in the rows of its account.
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.SignupCompleted,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = spaceId,
                OrgName = spaceName,
                Details = new Dictionary<string, object?> { ["kind"] = SignupKinds.NameOf(pending.Kind) },
            });
            await db.SaveChangesAsync(CancellationToken.None);

            // Not committed: the sign-in is written next, inside this transaction, by whoever receives it, and it commits.
            handedOver = true;
            return new SignupCompletion(Account: account, CompanyId: spaceId, Transaction: transaction);
        }
        finally
        {
            if (!handedOver)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>The company of an organization sign-up (spec 0009): a copy of the default roles, <c>org.created</c> with <c>via</c> <c>signup</c>, and the membership with the first admin's role.</summary>
    private async Task<(Guid Id, string Name)> CreateCompanyAsync(ApplicationUser account, string companyName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var created = await companies.CreateAsync(companyName, cancellationToken, via: "signup");
        if (!created.Succeeded)
        {
            throw new InvalidOperationException($"Could not create the company: {created.Error}.");
        }

        // The role is the company's own copy of the first admin's default role, found by its normalised name.
        var roleKey = NameInput.Normalize(manifest.Current.FirstAdminRole.Name);
        var roleId = await db.CompanyRoles.AsNoTracking()
            .Where(r => r.CompanyId == created.Value && r.NormalizedName == roleKey)
            .Select(r => r.Id)
            .SingleAsync(cancellationToken);
        db.Memberships.Add(new Membership { UserId = account.Id, CompanyId = created.Value, RoleId = roleId, JoinedAt = now });
        return (created.Value, companyName);
    }

    private static SignupCompletion Refused(string error) => new(error);
}
```

- [ ] **Step 5: The endpoints.** In `src/Auth.Server/Signup/SignupPreviewEndpoint.cs`:

(a) Replace

```csharp
        if (!gate.IsOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }
```

with

```csharp
        if (!gate.AnyOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }
```

(b) Replace

```csharp
        var preview = await signup.PreviewAsync(fields[0], http.RequestAborted);
        return preview is null
            ? AccountResults.InvalidToken()
            : ApiResults.Ok(new SignupPreviewResponse(preview.Email, preview.CompanyName, preview.HasAccount));
```

with

```csharp
        var preview = await signup.PreviewAsync(fields[0], http.RequestAborted);
        if (preview is null)
        {
            return AccountResults.InvalidToken();
        }

        // The kind is known only from the token: its switch is judged now (spec 0010 → preview, step 3).
        return gate.IsOnFor(preview.Kind)
            ? ApiResults.Ok(new SignupPreviewResponse(SignupKinds.NameOf(preview.Kind), preview.Email, preview.CompanyName))
            : ApiResults.Error(TenancyErrors.SignupDisabled);
```

and in its summary replace the two lines as they stand (the sentence spans the line break)

```csharp
/// <c>POST /auth/signup/preview</c> (spec 0009): what the link of a mail is for. No access token: the link token is the credential. While
/// sign-up is off it answers <c>403 signup_disabled</c> before it reads the body.
```

with

```csharp
/// <c>POST /auth/signup/preview</c> (specs 0009 and 0010): what the link of a mail is for. No access token: the link token is the credential.
/// While both switches are off it answers <c>403 signup_disabled</c> before it reads the body; while the switch of the link's kind is off,
/// after the token.
```

In `src/Auth.Server/Signup/SignupCompleteEndpoint.cs`:

(a) Replace

```csharp
    private static readonly string[] Required = ["token", "company_name"];
    private static readonly string[] Optional = ["password"];
```

with

```csharp
    private static readonly string[] Required = ["token"];
    private static readonly string[] Optional = ["password", "company_name"];
```

(b) Replace

```csharp
        if (!gate.IsOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        // [token, company_name, password]: the password is null when it is not in the body; when it is, it is a non-blank string like the others.
        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Required, Optional, http.RequestAborted);
        if (fields is not [{ } linkToken, { } name, var password] || password?.Contains('\0') == true || !NameInput.IsValid(name))
        {
            return AccountResults.InvalidRequest();
        }
```

with

```csharp
        if (!gate.AnyOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        // [token, password, company_name]: an optional member is null when it is not in the body; when it is, it is a non-blank string like the
        // others. Which of the two a link needs is known only from its token: the service judges it (spec 0010 → Complete, step 5).
        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Required, Optional, http.RequestAborted);
        if (fields is not [{ } linkToken, var password, var name] || password?.Contains('\0') == true || (name is not null && !NameInput.IsValid(name)))
        {
            return AccountResults.InvalidRequest();
        }
```

(c) In the class summary replace the two lines as they stand (the last sentence spans the line break)

```csharp
/// commits the transaction of the completion. If any of that fails, nothing of the completion stays. The password is an optional member of the
/// body (Amendment 3): an account without a company needs none, and a password that is sent for it is not used.
```

with

```csharp
/// commits the transaction of the completion. If any of that fails, nothing of the completion stays. The password and the company name are
/// optional members of the body: the order of spec 0010 → Complete judges them after the token, which says the kind.
```

and, on the line `/// credential. While sign-up is off it answers <c>403 signup_disabled</c> before it reads the body — also for a request that was made while it`,
replace `While sign-up is off it answers` with `While both switches are off it answers`.

(d) Replace `            var tenant = await memberships.ReadAsync(account.Id, CancellationToken.None)
                ?? throw new InvalidOperationException("The account that was just made has no company.");` with
`            var tenant = await memberships.ReadAsync(account.Id, CancellationToken.None)
                ?? throw new InvalidOperationException("The account that was just made has no space.");` and
`throw new InvalidOperationException("The membership read is not the company the completion made.");` with
`throw new InvalidOperationException("The membership read is not the space the completion made.");`.

- [ ] **Step 6: The contracts.** In `src/Auth.Server/Signup/SignupContracts.cs` add `using System.Text.Json.Serialization;` at the top and replace

```csharp
/// <summary>
/// Response of <c>POST /auth/signup/preview</c>: the address as typed, the company name of the request, and whether an account without a
/// company has the address (spec 0009, Amendment 3): then the completion needs no password.
/// </summary>
public sealed record SignupPreviewResponse(string Email, string CompanyName, bool HasAccount);

/// <summary>
/// Request of <c>POST /auth/signup/complete</c>. The company name is the person's last word; the one of the request is a suggestion. The
/// password is chosen here, except by an account that has no company (the preview says <c>has_account</c>): it may be left out, and when it
/// is sent it is not used.
/// </summary>
public sealed record SignupCompleteRequest(string Token, string CompanyName, string? Password = null);
```

with

```csharp
/// <summary>
/// Response of <c>POST /auth/signup/preview</c> (spec 0010): <c>{"kind":"organization","email","company_name"}</c> or
/// <c>{"kind":"private","email"}</c>.
/// </summary>
public sealed record SignupPreviewResponse(
    string Kind, string Email, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CompanyName = null);

/// <summary>
/// Request of <c>POST /auth/signup/complete</c>: the token and the password the person chooses, and for an organization the company name (the
/// person's last word; the one of the request is a suggestion). A private completion has no company name (spec 0010).
/// </summary>
public sealed record SignupCompleteRequest(string Token, string? Password = null, string? CompanyName = null);
```

In `src/Auth.Server/Api/SignupRoutes.cs` replace

```csharp
                "Shows what the link of the mail is for: the address as it was typed, the company name of the request and `has_account`, which is true "
                + "only when an account without a company has the address (then the completion needs no password). It changes nothing "
                + "and the link stays usable. A token that is unknown, used, expired or replaced by a newer mail is `invalid_token`.")
```

with

```csharp
                "Shows what the link of the mail is for: `kind` (`organization` or `private`), the address as it was typed and, for an organization, "
                + "the company name of the request. It changes nothing and the link stays usable. A token that is unknown, used, expired or replaced "
                + "by a newer mail is `invalid_token`; a link whose kind of sign-up is off is `403 signup_disabled`.")
```

and replace

```csharp
                "Finishes a sign-up and signs the person in: in one transaction the account (its email confirmed), the company (named as given here, "
                + "with a copy of the default roles) and the membership with the first admin's role are made, the pending sign-up is used up, and the "
                + "session is issued; if the session cannot be written nothing of this stays. The answer is that of a successful login: the access token "
                + "(carrying the new company and the first admin's role) and the refresh cookie. The checks are made in this order: the token, the "
                + "account (`409 already_registered`, whatever the password, when an account that belongs to a company, or one that only resembles the "
                + "typed address, has the address by now), the password (only where no account exists: a weak one is `400 weak_password` and leaves "
                + "the token usable; an account that has no company keeps the password it has, so for it the member may be left out and is not used if sent).")
```

with

```csharp
                "Finishes a sign-up and signs the person in: in one transaction the account (its email confirmed) and its space are made (an organization: "
                + "the company named as given here, with a copy of the default roles, and the membership with the first admin's role; a private person: "
                + "the personal space), the pending sign-up is used up, and the session is issued; if the session cannot be written nothing of this stays. "
                + "The answer is that of a successful login: the access token (carrying the new space, `org_kind` and the role) and the refresh cookie. "
                + "The checks are made in this order: the token, the switch of its kind (`403 signup_disabled`), the company name (required for an "
                + "organization, refused for a private sign-up), any account under the address (`409 already_registered`, whatever the password), the "
                + "password (missing: `400 invalid_request`; weak: `400 weak_password`; the token stays usable).")
```

- [ ] **Step 7: Nothing of the comeback is left.** `grep -rn "HasAccount\|has_account\|MayStartCompany\|BelongsToCompany\|existing_account" src tests` finds nothing.

- [ ] **Step 8: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.PrivateSignupCompleteTests -class Auth.IntegrationTests.PrivateSignupSwitchTests -class Auth.IntegrationTests.SignupPreviewTests -class Auth.IntegrationTests.SignupCompleteTests -class Auth.IntegrationTests.SignupCompleteSignInTests -class Auth.IntegrationTests.SignupCompleteRaceTests -class Auth.IntegrationTests.SignupCompleteDisabledTests -class Auth.IntegrationTests.SignupPreviewDisabledTests -class Auth.IntegrationTests.SignupFirstAdminTests -class Auth.IntegrationTests.SignupDispatchTests -class Auth.IntegrationTests.PrivateSignupDispatchTests -class Auth.IntegrationTests.OpenApiTests`
passes.

- [ ] **Step 9: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): the preview and the completion of a private sign-up; the comeback of accounts without a company is gone`.

---

## Day 5 — Wednesday 15.04: the invitation move and the self-deletion

When the day is done, an invited private person moves only on purpose, a private person deletes their own account, and the races of one
address are pinned. The server side of the slice is complete.

### Task 12: Invitations: `has_private_account`, `leave_private_account`, the move

**Files:**
- Modify: `src/Auth.Server/Tenancy/InviteAcceptance.cs` (replaced as a whole), `src/Auth.Server/Api/InviteEndpoints.cs`, `src/Auth.Server/Tenancy/Contracts.cs`, `src/Auth.Server/Tenancy/Outcome.cs`, `src/Auth.Server/Api/TenancyEndpoints.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/TenancyApi.cs`, `tests/Auth.IntegrationTests/InviteAcceptTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/PrivateInvitationTests.cs`

**Interfaces:**
- Consumes: `JsonObjectBody.ReadObjectAsync`, `JsonFields`, `JsonBoolean` (Task 9), `AddressLock` (Task 8), `CompanyKind` (Task 1), `TenancyTestBase.CreatePrivateAccountAsync`.
- Produces: `InvitePreview(string OrgName, string Email, string Role, bool HasPrivateAccount)`; `InviteAcceptance.AcceptAsync(string token, string? password, bool leavePrivateAccount, CancellationToken)`; `TenancyErrors.PrivateAccountExists = "private_account_exists"` (`409`); `InvitePreviewResponse(string OrgName, string Email, string Role, bool HasPrivateAccount)`; `AcceptInviteRequest(string Token, string? Password = null, bool? LeavePrivateAccount = null)`; test side `TenancyApi.AcceptLeaving(HttpClient, string token, string? password = null)`.

**Contract counts:** `OpenApiTests.The_responses_are_described_in_snake_case`: `InvitePreviewResponse` gains `has_private_account`; `OpenApiTests.Each_endpoint_that_reads_a_body_describes_it_in_snake_case`: `POST /auth/invites/accept` becomes `leave_private_account,password,token`; `OpenApiTests.The_error_codes_of_the_contract_are_in_the_descriptions`: the `409` of the acceptance names `private_account_exists`. In `InviteAcceptTests` the password path of an existing account without a company goes (spec 0010, Invitations): `Accepting_for_an_account_without_a_company_replaces_its_password_and_ends_every_session` becomes `An_account_without_a_space_joins_with_the_password_it_has_and_its_sessions_end`; `An_invitation_in_other_ascii_capitals_reaches_the_existing_account` and `Accepting_confirms_an_unconfirmed_account_lifts_a_lockout_and_removes_its_other_links` sign in with the password the account had; `Weak_password_names_the_broken_rules_changes_nothing_and_leaves_the_token_usable` loses its case of an existing account (a password is checked only where an account is made). `A_manager_invites_the_person_accepts_and_logs_in_with_the_company_and_the_role` reads the preview's members with `has_private_account` (`false`).

**Decisions the plan takes here:**
- An account with no membership at all, which only a direct change of the database leaves (the start-up step deletes such accounts), is
  treated at acceptance like a private account that moves: it becomes the member, keeps its password and confirms its email; a password that
  is sent is not used, and `leave_private_account` is not needed (there is no space to lose). The spec says such accounts do not exist; this
  is the safeguard's answer.
- The move (and the account without a space) also gets a new security stamp, so a login that raced the acceptance cannot refresh into the
  company; every session, link and the streak end as at any acceptance.
- `false` and an absent `leave_private_account` mean the same (spec, Settled 7).

- [ ] **Step 1: Write the failing tests.**

In `tests/Auth.IntegrationTests/Infrastructure/TenancyApi.cs`, after the method `Accept` add

```csharp

    /// <summary>Accepts for an address that has a private account, on purpose (spec 0010): <c>leave_private_account: true</c>.</summary>
    public static Task<HttpResponseMessage> AcceptLeaving(HttpClient client, string token, string? password = null) =>
        password is null
            ? client.PostAsJsonAsync(AcceptPath, new { token, leave_private_account = true })
            : client.PostAsJsonAsync(AcceptPath, new { token, password, leave_private_account = true });
```

`tests/Auth.IntegrationTests/PrivateInvitationTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → Invitations: inviting a private person is a warning, not a refusal.</summary>
public sealed class PrivateInvitationTests(PostgresFixture postgres, KeyMaterialFixture keys) : AuditTestBase(postgres, keys)
{
    private const string Private = "ola@poczta.test";
    private const string Ignored = "Ignored-Passw0rd";

    /// <summary>An invitation of a new company "Acme" to the address, with its mail sent: the company, the invitation and its token.</summary>
    private async Task<(Guid Company, Guid Invite, string Token)> InvitedAsync(string address = Private, string role = "user")
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, address, role);
        await EnqueueInvitationAsync(invite, address);
        await DispatchAsync();
        return (company, invite, TokenIn(Mail.Sent.Last(m => m.To == address)));
    }

    [Fact]
    public async Task Inviting_an_address_with_a_private_account_answers_as_inviting_an_unknown_address()   // criterion 16
    {
        var (company, _, token) = await CompanyWithAdminAsync();
        await CreatePrivateAccountAsync(Private);
        var role = await RoleIdAsync(company, "user");

        using var toPrivate = await TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", token, new { email = Private, role_id = role });
        using var toUnknown = await TenancyApi.Send(Client, HttpMethod.Post, "/auth/org/invites", token, new { email = "nobody@poczta.test", role_id = role });

        Assert.Equal(HttpStatusCode.Accepted, toPrivate.StatusCode);
        Assert.Equal(toUnknown.StatusCode, toPrivate.StatusCode);
        Assert.Equal(await toUnknown.Content.ReadAsStringAsync(), await toPrivate.Content.ReadAsStringAsync());
        Assert.Equal(LoginApi.HeaderNames(toUnknown), LoginApi.HeaderNames(toPrivate));
    }

    [Fact]
    public async Task The_preview_says_has_private_account_true_for_a_private_account_and_false_otherwise()   // criterion 17
    {
        await CreatePrivateAccountAsync(Private);
        var (_, _, privateToken) = await InvitedAsync(Private);
        var (_, _, newToken) = await InvitedAsync("nobody@poczta.test");

        using var forPrivate = await TenancyApi.Preview(Client, privateToken);
        using var forNew = await TenancyApi.Preview(Client, newToken);

        var mine = await TenancyApi.ReadOkAsync(forPrivate);
        Assert.Equal(["email", "has_private_account", "org_name", "role"], mine.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.True(mine.GetProperty("has_private_account").GetBoolean());
        Assert.Equal("Acme", mine.GetProperty("org_name").GetString());
        Assert.False((await TenancyApi.ReadOkAsync(forNew)).GetProperty("has_private_account").GetBoolean());
    }

    [Fact]
    public async Task A_company_members_address_is_still_already_member()   // criterion 17
    {
        await AddMemberAsync(await DevCompanyIdAsync(), "member@poczta.test", "user");
        var (_, invite, token) = await InvitedAsync("member@poczta.test");

        using var preview = await TenancyApi.Preview(Client, token);
        using var accept = await TenancyApi.AcceptLeaving(Client, token);

        await TenancyApi.AssertErrorAsync(preview, HttpStatusCode.Conflict, "already_member");
        await TenancyApi.AssertErrorAsync(accept, HttpStatusCode.Conflict, "already_member");
        Assert.NotNull(await InviteAsync(invite));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Accepting_without_leave_private_account_is_409_private_account_exists_and_changes_nothing(bool? leave)   // criterion 18
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var (_, invite, token) = await InvitedAsync();
        var hash = (await InviteAsync(invite))!.TokenHash;
        object body = leave is null
            ? new { token, password = Ignored }
            : new { token, password = Ignored, leave_private_account = leave.Value };

        using var response = await TenancyApi.Send(Client, HttpMethod.Post, TenancyApi.AcceptPath, null, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "private_account_exists");
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user && m.CompanyId == space, TestContext.Current.CancellationToken)));
        Assert.Equal(hash, (await InviteAsync(invite))!.TokenHash);   // the invitation still works
        Assert.Empty(await AuditAsync(AuditKinds.InviteAccepted));
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        Assert.Equal("personal", AccessTokens.Text(session.AccessToken, "org_kind"));
        using var moved = await TenancyApi.AcceptLeaving(Client, token);
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[true]")]
    public async Task A_leave_private_account_that_is_not_a_boolean_is_400_invalid_request(string value)   // criterion 18
    {
        await CreatePrivateAccountAsync(Private);
        var (_, invite, token) = await InvitedAsync();

        using var response = await AccountApi.PostRaw(Client, TenancyApi.AcceptPath, $$"""{"token":"{{token}}","leave_private_account":{{value}}}""");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.NotNull(await InviteAsync(invite));
    }

    [Fact]
    public async Task Accepting_with_leave_private_account_moves_the_account_and_deletes_its_space()   // criterion 19, Review Focus 2
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        var before = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var (company, invite, token) = await InvitedAsync(role: "user");

        using (var accepted = await TenancyApi.AcceptLeaving(Client, token, Ignored))
        {
            await TenancyApi.AssertEmptyAsync(accepted, HttpStatusCode.NoContent);
        }

        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == space, TestContext.Current.CancellationToken)));
        var membership = Assert.Single(await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.UserId == user).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(company, membership.CompanyId);
        Assert.Equal(await RoleIdAsync(company, "user"), membership.RoleId);
        Assert.Null(await InviteAsync(invite));

        using (var refresh = await SessionApi.Refresh(Client, before.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(refresh);   // every session ended
        }

        using (var sent = await LoginApi.Login(Client, Private, Ignored))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, sent.StatusCode);   // the password of the acceptance was not used
        }

        var after = await SessionApi.LoginAsync(Client, Private, UserPassword);   // the old password signs in, to the company
        Assert.Equal(user.ToString(), AccessTokens.Text(after.AccessToken, "sub"));
        Assert.Equal(company.ToString(), AccessTokens.Text(after.AccessToken, "org_id"));
        Assert.Equal("organization", AccessTokens.Text(after.AccessToken, "org_kind"));
        Assert.Equal(["user"], AccessTokens.Array(after.AccessToken, "roles"));

        var row = await SingleAsync(AuditKinds.InviteAccepted);
        Assert.Equal(user, row.SubjectUserId);
        Assert.Equal(company, row.OrgId);
        Assert.Equal("user", AuditApi.Text(row, "role"));
        Assert.Equal("true", AuditApi.Raw(row, "left_private_account"));
        Assert.Equal(space.ToString(), AuditApi.Text(row, "personal_org_id"));
        Assert.Equal(["left_private_account", "personal_org_id", "role"], AuditApi.DetailNames(row));
        await AssertNoSecretsAsync(Ignored, UserPassword, token);
    }

    [Fact]
    public async Task The_move_ends_the_mail_links_and_the_streak_of_the_account()   // criterion 19: as at any acceptance
    {
        var (user, _) = await CreatePrivateAccountAsync(Private);
        await EnqueueAsync(MailKind.PasswordReset, Private);
        await DispatchAsync();
        using (var wrong = await LoginApi.Login(Client, Private, WrongPassword))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        var (_, _, token) = await InvitedAsync();
        using (var accepted = await TenancyApi.AcceptLeaving(Client, token))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == user, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Accepting_for_an_address_without_an_account_still_needs_a_password()   // criterion 20
    {
        var (_, invite, token) = await InvitedAsync("nobody@poczta.test");

        using (var missing = await AccountApi.PostRaw(Client, TenancyApi.AcceptPath, $$"""{"token":"{{token}}"}"""))
        {
            await TenancyApi.AssertErrorAsync(missing, HttpStatusCode.BadRequest, "invalid_request");
        }

        Assert.NotNull(await InviteAsync(invite));
        using var good = await TenancyApi.Accept(Client, token, "Chosen-Passw0rd");
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
    }

    [Fact]
    public async Task A_missing_password_with_an_unknown_token_is_invalid_token()   // spec 0010 → Invitations
    {
        using var response = await AccountApi.PostRaw(Client, TenancyApi.AcceptPath, $$"""{"token":"{{new string('x', 43)}}"}""");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_token");
    }

    [Fact]
    public void The_new_error_is_a_409()
    {
        Assert.Equal(409, TenancyErrors.StatusOf(TenancyErrors.PrivateAccountExists));
        Assert.Equal("private_account_exists", TenancyErrors.PrivateAccountExists);
    }
}
```

Change `tests/Auth.IntegrationTests/InviteAcceptTests.cs`:

(a) Replace the whole method `Accepting_for_an_account_without_a_company_replaces_its_password_and_ends_every_session` (from its `[Fact]` to its closing `    }`) with

```csharp
    [Fact]
    public async Task An_account_without_a_space_joins_with_the_password_it_has_and_its_sessions_end()   // spec 0010: no password is set for an existing account
    {
        var company = await CreateCompanyAsync("Acme");
        var leaver = await AddMemberAsync(company, Address, "user");
        var session = await SessionApi.LoginAsync(Client, Address, UserPassword);
        await RemoveMembershipAsync(leaver);   // a state only a direct change of the database leaves; a start deletes such accounts
        var (_, token) = await MailedAsync(company);

        using (var accepted = await TenancyApi.Accept(Client, token, Password))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        using var refreshed = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refreshed);
        using var sent = await LoginApi.Login(Client, Address, Password);
        Assert.Equal(HttpStatusCode.Unauthorized, sent.StatusCode);   // the password of the acceptance is not used
        _ = await SessionApi.LoginAsync(Client, Address, UserPassword);
        Assert.Single(await NonSeedMembershipsAsync());
    }
```

(b) In `An_invitation_in_other_ascii_capitals_reaches_the_existing_account` replace
`        _ = await SessionApi.LoginAsync(Client, "Steve@acme.test", Password);` with
`        _ = await SessionApi.LoginAsync(Client, "Steve@acme.test", UserPassword);   // spec 0010: the account keeps its password`.

(c) In `Accepting_confirms_an_unconfirmed_account_lifts_a_lockout_and_removes_its_other_links` replace
`        _ = await SessionApi.LoginAsync(Client, Address, Password);   // first try` (the first of the two identical lines: the one in this method) with
`        _ = await SessionApi.LoginAsync(Client, Address, UserPassword);   // first try; spec 0010: the account keeps its password`.

(d) Replace the whole method `Weak_password_names_the_broken_rules_changes_nothing_and_leaves_the_token_usable` (from its `[Theory]` and the two
`[InlineData]` lines to its closing `    }`) with

```csharp
    [Fact]
    public async Task Weak_password_names_the_broken_rules_changes_nothing_and_leaves_the_token_usable()   // criterion 7; spec 0010: only where an account is made
    {
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
        Assert.Null(await UserAsync(Address));

        using var good = await TenancyApi.Accept(Client, token, Password);
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
    }
```

(e) In `A_manager_invites_the_person_accepts_and_logs_in_with_the_company_and_the_role` replace

```csharp
            Assert.Equal(["email", "org_name", "role"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
```

with

```csharp
            Assert.Equal(["email", "has_private_account", "org_name", "role"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.False(body.GetProperty("has_private_account").GetBoolean());   // spec 0010: an address without an account
```

Change `tests/Auth.IntegrationTests/OpenApiTests.cs`: replace `    [InlineData("POST /auth/invites/accept", "password,token")]` with
`    [InlineData("POST /auth/invites/accept", "leave_private_account,password,token")]`; replace
`        Assert.Equal(["email", "org_name", "role"], Properties("InvitePreviewResponse"));` with
`        Assert.Equal(["email", "has_private_account", "org_name", "role"], Properties("InvitePreviewResponse"));`; and after
`        Assert.Contains("already_member", Description(operations["POST /auth/invites/accept"], "409"));` add
`        Assert.Contains("private_account_exists", Description(operations["POST /auth/invites/accept"], "409"));`.

- [ ] **Step 2: See them fail.** The build fails: `TenancyErrors.PrivateAccountExists` does not exist.

- [ ] **Step 3: The error.** In `src/Auth.Server/Tenancy/Outcome.cs` replace

```csharp
    public const string AlreadyRegistered = "already_registered";
```

with

```csharp
    public const string AlreadyRegistered = "already_registered";
    public const string PrivateAccountExists = "private_account_exists";
```

and replace

```csharp
        AlreadyInOrg or AlreadyMember or InvitePending or LastManager or CannotChangeSelf or RoleInUse or RoleNameTaken or AlreadyRegistered
            => StatusCodes.Status409Conflict,
```

with

```csharp
        AlreadyInOrg or AlreadyMember or InvitePending or LastManager or CannotChangeSelf or RoleInUse or RoleNameTaken or AlreadyRegistered
            or PrivateAccountExists => StatusCodes.Status409Conflict,
```

- [ ] **Step 4: The acceptance.** Replace the content of `src/Auth.Server/Tenancy/InviteAcceptance.cs` with

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace Auth.Server.Tenancy;

/// <summary>
/// What the acceptance screen shows before it asks for anything: the company, the address, the role, and whether the address has a private
/// account (spec 0010), which accepting would move into the company.
/// </summary>
public sealed record InvitePreview(string OrgName, string Email, string Role, bool HasPrivateAccount);

/// <summary>
/// Previews and accepts an invitation by its link token (spec 0005 → Invitations — public, Effects; spec 0010 → Invitations). Accepting is how
/// anyone becomes a member of a company, and it is one transaction: it uses the invitation up, makes the account if there is none (with the
/// password sent) or takes the account there is (keeping its password; a private account only with <c>leave_private_account</c>, and its
/// personal space is deleted), confirms the email, ends every session of the account and makes it a member with the role of the invitation —
/// or, when anything is refused, changes nothing at all, the invitation included.
/// </summary>
public sealed class InviteAcceptance(
    AuthDbContext db, UserManager<ApplicationUser> users, IOpenIddictTokenManager tokens,
    IOpenIddictAuthorizationManager authorizations, TimeProvider clock, AuditLog audit)
{
    /// <summary>
    /// The company, address and role of a usable invitation, and whether the address has a private account; the token stays usable. Unknown,
    /// used, expired, cancelled and replaced tokens are all <c>invalid_token</c>, and so is an invitation whose address matches an existing
    /// account that is not spelled the same (<see cref="EmailInput.IsSameAddress"/>), or that
    /// <see cref="EmailInput.MayStandForAnotherAddress">may stand for another</see>. An address that belongs to a company is <c>already_member</c>.
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
            select new { invite.Email, CompanyName = company.Name, RoleName = role.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null || EmailInput.MayStandForAnotherAddress(row.Email))
        {
            return Outcome.Fail<InvitePreview>(TenancyErrors.InvalidToken);
        }

        // The account the acceptance would act on. Another spelling that only normalises to the invited address is not the invited mailbox:
        // acceptance refuses it, so the preview does too, and says nothing about that account.
        var user = await users.FindByEmailAsync(row.Email);
        if (user is not null && !EmailInput.IsSameAddress(user.Email, row.Email))
        {
            return Outcome.Fail<InvitePreview>(TenancyErrors.InvalidToken);
        }

        var space = user is null ? null : await SpaceOfAsync(user.Id, cancellationToken);
        return space is { Kind: CompanyKind.Organization }
            ? Outcome.Fail<InvitePreview>(TenancyErrors.AlreadyMember)
            : Outcome.Ok(new InvitePreview(row.CompanyName, row.Email, row.RoleName, space is { Kind: CompanyKind.Personal }));
    }

    /// <summary>
    /// Accepts the invitation, in the order of spec 0010 → Invitations: <c>invalid_token</c>; <c>already_member</c> (an account of a company; the
    /// invitation stays usable); <c>private_account_exists</c> (a private account and <paramref name="leavePrivateAccount"/> not set: nothing
    /// changes); the move of a private account; or, with no account, <c>invalid_request</c> without a password and <c>weak_password</c> (the
    /// broken rules are the value; the token stays usable). An existing account is used only when its address is the invited one up to ASCII
    /// case (<see cref="EmailInput.IsSameAddress"/>), and it keeps its password: a <paramref name="password"/> sent for it is not used.
    /// </summary>
    public async Task<Outcome<IReadOnlyList<string>>> AcceptAsync(
        string token, string? password, bool leavePrivateAccount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

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

        // Sending refuses such an address (defence in depth; the check that decides is the one on the account below).
        if (EmailInput.MayStandForAnotherAddress(invite.Email))
        {
            return Refused(TenancyErrors.InvalidToken);
        }

        // One address at a time, after the company lock (spec 0010 → Locks): two invitations accepted at once, a self-deletion and a sign-up
        // completion of the address wait for each other here.
        await AddressLock.AcquireAsync(db, invite.NormalizedEmail, cancellationToken);

        // The lookup matches by the normalised address, which folds look-alikes together (µ and μ, ς and σ…). The link
        // proves the invited mailbox only: an account under another spelling is not touched, and nothing changes.
        var user = await users.FindByEmailAsync(invite.Email);
        if (user is not null && !EmailInput.IsSameAddress(user.Email, invite.Email))
        {
            return Refused(TenancyErrors.InvalidToken);
        }

        var space = user is null ? null : await SpaceOfAsync(user.Id, cancellationToken);
        if (space is { Kind: CompanyKind.Organization })
        {
            return Refused(TenancyErrors.AlreadyMember);
        }

        if (space is { Kind: CompanyKind.Personal } && !leavePrivateAccount)
        {
            return Refused(TenancyErrors.PrivateAccountExists);   // nothing changes; the invitation stays usable
        }

        ApplicationUser account;
        if (user is null)
        {
            if (password is null)
            {
                return Refused(TenancyErrors.InvalidRequest);
            }

            account = new ApplicationUser { UserName = invite.Email, Email = invite.Email, EmailConfirmed = true };
            var broken = await PasswordRules.BrokenAsync(users, account, password);
            if (broken.Count > 0)
            {
                return new Outcome<IReadOnlyList<string>>(broken, TenancyErrors.WeakPassword);
            }

            // The link came through the mailbox of the address: that proves it, and it is why a password is set here.
            Ensure(await users.CreateAsync(account, password));
        }
        else
        {
            // The account keeps its password and its address. A private account moves on purpose (spec 0010): its personal space, the space's
            // role row and its membership are deleted, with everything the product keeps under that space's id left behind (spec 0010, Accepted
            // 16). An account with no space at all, which only a direct change of the database leaves, simply joins.
            if (space is { Kind: CompanyKind.Personal } personal)
            {
                await db.Memberships.Where(m => m.UserId == user.Id && m.CompanyId == personal.Id).ExecuteDeleteAsync(cancellationToken);
                await db.Companies.Where(c => c.Id == personal.Id && c.Kind == CompanyKind.Personal).ExecuteDeleteAsync(cancellationToken);
            }

            account = user;
            account.EmailConfirmed = true;
            // A new stamp: a login that raced this acceptance cannot refresh into the company.
            Ensure(await users.UpdateSecurityStampAsync(account));
        }

        // Every session, mail link and the login streak end, as at any acceptance.
        await AccountSessions.EndAllAsync(db, tokens, authorizations, account, cancellationToken);

        db.Memberships.Add(new Membership { UserId = account.Id, CompanyId = invite.CompanyId, RoleId = invite.RoleId, JoinedAt = now });
        // The person who holds the link is anonymous: no actor. Who joined, where, and as what; never the link or the password.
        var details = new Dictionary<string, object?>
        {
            ["role"] = await db.CompanyRoles.AsNoTracking().Where(r => r.Id == invite.RoleId).Select(r => r.Name).FirstOrDefaultAsync(cancellationToken),
        };
        if (space is { Kind: CompanyKind.Personal } left)
        {
            details["left_private_account"] = true;
            details["personal_org_id"] = left.Id;
        }

        audit.Stage(new AuditEntry
        {
            Kind = AuditKinds.InviteAccepted,
            SubjectUserId = account.Id,
            SubjectEmail = account.Email,
            OrgId = invite.CompanyId,
            OrgName = await audit.CompanyNameAsync(invite.CompanyId, cancellationToken),
            TargetId = invite.Id,
            Details = details,
        });
        await db.SaveChangesAsync(cancellationToken);

        // Not the request's token: a client that goes away now must not leave the outcome open.
        await transaction.CommitAsync(CancellationToken.None);
        return new Outcome<IReadOnlyList<string>>([]);
    }

    /// <summary>The space of the account and its kind; <see langword="null"/> when it has none.</summary>
    private Task<Space?> SpaceOfAsync(Guid userId, CancellationToken cancellationToken) =>
        (from membership in db.Memberships.AsNoTracking()
         join company in db.Companies.AsNoTracking() on membership.CompanyId equals company.Id
         where membership.UserId == userId
         orderby membership.JoinedAt
         select new Space(company.Id, company.Kind))
        .FirstOrDefaultAsync(cancellationToken);

    private static Outcome<IReadOnlyList<string>> Refused(string error) => new(null, error);

    private static void Ensure(IdentityResult result) => AccountSessions.Ensure(result, "Could not set up the account");

    private sealed record Space(Guid Id, CompanyKind Kind);
}
```

(The comment with the Greek letters is the file's own, unchanged: copy it from the old file rather than retyping it.)

- [ ] **Step 5: The endpoints and the contracts.** In `src/Auth.Server/Tenancy/Contracts.cs` replace

```csharp
/// <summary>Response of <c>POST /auth/invites/preview</c>.</summary>
public sealed record InvitePreviewResponse(string OrgName, string Email, string Role);

/// <summary>Request of <c>POST /auth/invites/accept</c>.</summary>
public sealed record AcceptInviteRequest(string Token, string Password);
```

with

```csharp
/// <summary>Response of <c>POST /auth/invites/preview</c>; <c>has_private_account</c> (spec 0010): the address has a private account.</summary>
public sealed record InvitePreviewResponse(string OrgName, string Email, string Role, bool HasPrivateAccount);

/// <summary>
/// Request of <c>POST /auth/invites/accept</c>: the password of a new account (not used for an account that exists), and
/// <c>leave_private_account</c>, a JSON boolean, which a private account needs to move (spec 0010).
/// </summary>
public sealed record AcceptInviteRequest(string Token, string? Password = null, bool? LeavePrivateAccount = null);
```

In `src/Auth.Server/Api/InviteEndpoints.cs`:

(a) Remove the line `    private static readonly string[] AcceptFields = ["token", "password"];`.

(b) Replace

```csharp
            ? ApiResults.Ok(new InvitePreviewResponse(preview.Value!.OrgName, preview.Value.Email, preview.Value.Role))
```

with

```csharp
            ? ApiResults.Ok(new InvitePreviewResponse(preview.Value!.OrgName, preview.Value.Email, preview.Value.Role, preview.Value.HasPrivateAccount))
```

(c) Replace

```csharp
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
```

with

```csharp
        // Spec 0010: the password is needed only where an account is made, so it is optional here and judged after the token; when it is there it
        // is a non-blank string with no NUL. leave_private_account is a JSON boolean when it is there.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields
            || !fields.TryString("token", out var token)
            || !fields.TryOptionalString("password", out var password)
            || password?.Contains('\0') == true)
        {
            return ApiResults.InvalidRequest();
        }

        var leave = fields.Boolean("leave_private_account");
        if (leave == JsonBoolean.Invalid)
        {
            return ApiResults.InvalidRequest();
        }

        var accepted = await acceptance.AcceptAsync(token, password, leave == JsonBoolean.True, http.RequestAborted);
        if (accepted.Succeeded)
        {
            return ApiResults.NoContent();
        }

        return accepted.Error == TenancyErrors.WeakPassword
            ? AccountResults.WeakPassword(accepted.Value!)
            : ApiResults.Error(accepted.Error!);
```

In `src/Auth.Server/Api/TenancyEndpoints.cs` replace

```csharp
        app.MapPost(InviteEndpoints.PreviewPath, InviteEndpoints.PreviewAsync)
            .WithTags("Invitations")
            .ReadsJson<PreviewInviteRequest>()
            .Produces<InvitePreviewResponse>()
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember);
        app.MapPost(InviteEndpoints.AcceptPath, InviteEndpoints.AcceptAsync)
            .WithTags("Invitations")
            .ReadsJson<AcceptInviteRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesPasswordError(TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken, TenancyErrors.WeakPassword)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember);
```

with

```csharp
        app.MapPost(InviteEndpoints.PreviewPath, InviteEndpoints.PreviewAsync)
            .WithTags("Invitations")
            .WithDescription(
                "The company, the address and the role of an invitation, and `has_private_account`: whether the address has a private account, "
                + "which accepting moves into the company (its personal space is deleted). An address of a company member is `409 already_member`.")
            .ReadsJson<PreviewInviteRequest>()
            .Produces<InvitePreviewResponse>()
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember);
        app.MapPost(InviteEndpoints.AcceptPath, InviteEndpoints.AcceptAsync)
            .WithTags("Invitations")
            .WithDescription(
                "Accepts an invitation. A new account needs `password`. An account that exists keeps its own password (a `password` sent is not "
                + "used); a private account moves only with `leave_private_account: true` (its personal space is deleted, every session ends), "
                + "and without it the answer is `409 private_account_exists` and nothing changes. A company member is `409 already_member`.")
            .ReadsJson<AcceptInviteRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesPasswordError(TenancyErrors.InvalidRequest, TenancyErrors.InvalidToken, TenancyErrors.WeakPassword)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyMember, TenancyErrors.PrivateAccountExists);
```

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.PrivateInvitationTests -class Auth.IntegrationTests.InviteAcceptTests -class Auth.IntegrationTests.AuditInvitationFlowsTests -class Auth.IntegrationTests.AuditAtomicityTests -class Auth.IntegrationTests.InvitationAddressTests -class Auth.IntegrationTests.OrgInviteTests -class Auth.IntegrationTests.SignupCompleteRaceTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.SignupErrorCodesTests`
passes.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files (the Greek letters of the copied
comment are meant). Proposed subject: `feat(server): an invitation warns a private account and moves it only on purpose`.

### Task 13: `DELETE /auth/account`, the races, the OpenAPI counts

**Files:**
- Create: `src/Auth.Server/Api/AccountDeletionEndpoint.cs`
- Modify: `src/Auth.Server/Tenancy/Contracts.cs`, `src/Auth.Server/Api/TenancyEndpoints.cs`
- Create (tests): `tests/Auth.IntegrationTests/Infrastructure/HeldLock.cs`, `tests/Auth.IntegrationTests/AccountDeletionEndpointTests.cs`, `tests/Auth.IntegrationTests/PrivateAccountRaceTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/OpenApiTests.cs`, `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`, `tests/Auth.IntegrationTests/RatePolicyTests.cs`

**Interfaces:**
- Consumes: `AccountDeletion` (Task 6), `AddressLock` (Task 8), `CompanyAccess`-style reading of the membership (`MembershipReader`), `TenantClaims.OrgKind` and `SpaceKinds` (Task 3), `LoginStreakStore`, `RefreshCookie`, `TenancyApi.AcceptLeaving` (Task 12), `SignupApi.CompletePrivate` (Task 11), `PendingSignupSeed` with a kind (Task 9).
- Produces: `DELETE /auth/account` (`AccountDeletionEndpoint.Path`, `DeleteAsync`); `DeleteAccountRequest(string Password)`; test side `HeldLock` (`AddressAsync`, `AuditTableAsync`, `ReleaseAsync`, `WaitForWaitersAsync`).

**Contract counts:** `OpenApiHardeningTests.Every_operation_can_say_429_too_many_requests_with_retry_after_the_two_added_by_hand_included`: **30 → 31** operations; `OpenApiHardeningTests.Exactly_the_operations_the_charset_guard_covers_say_415_unsupported_media_type`: **20 → 21**. `OpenApiTests`: `Endpoints` gains `DELETE /auth/account` (so `Every_429_carries_retry_after` counts 31), `Each_endpoint_describes_the_statuses_of_the_contract` gains `DELETE /auth/account` `204,400,401,403,415,429`, `Each_endpoint_that_reads_a_body_describes_it_in_snake_case` gains `password`, `The_answers_that_set_or_clear_the_refresh_cookie_say_so` gains `204`, `No_other_answer_says_it_sets_a_cookie` gains `DELETE /auth/account 204`, `The_company_endpoints_ask_for_a_bearer_token_and_the_public_ones_do_not` counts it as guarded, `The_error_codes_of_the_contract_are_in_the_descriptions` names its `403` codes. `RatePolicyTests` gains `DELETE /auth/account` → `General`.

**Decisions the plan takes here:**
- A token that does not say `personal` while the database shows a personal space is let through (the database decides; such a token cannot be issued by this version, which always writes `org_kind`).
- An account that is gone between the first read and the password check is `403 permissions_changed`, as the table says of an account that "is gone".
- The deletion's transaction does not use the request's cancellation token, so a client that goes away cannot leave it half done.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/Infrastructure/HeldLock.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// A lock that a test holds in a transaction of its own until <see cref="ReleaseAsync"/> (or, failing that, until it is disposed: rolled back),
/// and the wait for the requests queued behind locks, so that a race runs in the order the test names (the pattern of
/// <c>SignupCompleteRaceTests</c>).
/// </summary>
public sealed class HeldLock : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly IServiceScope _scope;
    private readonly IDbContextTransaction _transaction;

    private HeldLock(IServiceScope scope, IDbContextTransaction transaction)
    {
        _scope = scope;
        _transaction = transaction;
    }

    /// <summary>The advisory lock of an address, which completion, acceptance, self-deletion and the start-up step take.</summary>
    public static async Task<HeldLock> AddressAsync(AuthAppFactory factory, string normalizedEmail)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({normalizedEmail}, 0))", TestContext.Current.CancellationToken);
        return new HeldLock(scope, transaction);
    }

    /// <summary>A lock on the audit table that stops every write to it: a change that reaches its audit rows waits there, not committed.</summary>
    public static async Task<HeldLock> AuditTableAsync(AuthAppFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE audit_events IN SHARE MODE", TestContext.Current.CancellationToken);
        return new HeldLock(scope, transaction);
    }

    /// <summary>Waits until exactly this many backends of the factory's database are waiting for a lock.</summary>
    public static async Task WaitForWaitersAsync(AuthAppFactory factory, int count)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            long waiting;
            using (var scope = factory.Services.CreateScope())
            {
                waiting = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database
                    .SqlQuery<long>($"""SELECT count(*) AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""")
                    .SingleAsync(TestContext.Current.CancellationToken);
            }

            if (waiting == count)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Expected {count} backends waiting for a lock, saw {waiting}.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    public Task ReleaseAsync() => _transaction.CommitAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
        _scope.Dispose();
    }
}
```

`tests/Auth.IntegrationTests/AccountDeletionEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0010 → <c>DELETE /auth/account</c>.</summary>
public sealed class AccountDeletionEndpointTests : AuditTestBase
{
    private const string Private = "ola@poczta.test";
    private const string Path = "/auth/account";

    // Spelled out, so that the escape in a test body is not read as an escape of this file.
    private const char Backslash = (char)0x5C;

    public AccountDeletionEndpointTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithPrivateSignup();   // a new sign-up after the deletion
    }

    private Task<HttpResponseMessage> DeleteAsync(string? token, string password = UserPassword) =>
        TenancyApi.Send(Client, HttpMethod.Delete, Path, token, new { password });

    private async Task<HttpResponseMessage> RawAsync(string token, string body, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return await Client.SendAsync(request);
    }

    private async Task<(Guid User, Guid Space, SessionApi.Session Session)> PrivateAsync()
    {
        var (user, space) = await CreatePrivateAccountAsync(Private);
        return (user, space, await SessionApi.LoginAsync(Client, Private, UserPassword));
    }

    private Task<int> UsersAsync(Guid user) => InDbAsync(db => db.Users.CountAsync(u => u.Id == user, TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_private_account_deletes_itself_with_its_password_204_and_the_cookie_is_cleared()   // criterion 21
    {
        var (user, space, session) = await PrivateAsync();
        await PendingSignupSeed.IssueAsync(Factory.Services, Private, null, SignupKind.Private);
        await EnqueueAsync(MailKind.PasswordReset, Private);   // a queued request of the address
        var subject = user.ToString();
        var logins = (await AuditAsync(AuditKinds.LoginSucceeded)).Count;

        using var response = await SendFromCallerAsync(HttpMethod.Delete, Path, session.AccessToken, new { password = UserPassword });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        var cookie = SessionApi.RefreshCookieOf(response);
        Assert.Equal(TimeSpan.Zero, cookie.MaxAge);
        Assert.Equal("", cookie.Value.Value);

        var cancellation = TestContext.Current.CancellationToken;
        Assert.Equal(0, await UsersAsync(user));
        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == space, cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.CompanyRoles.CountAsync(r => r.CompanyId == space, cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user, cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreToken>().CountAsync(t => t.Subject == subject, cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>().CountAsync(a => a.Subject == subject, cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.PendingSignups.CountAsync(cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(cancellation)));
        Assert.Equal(logins, (await AuditAsync(AuditKinds.LoginSucceeded)).Count);   // the rows that name it remain

        var row = await SingleAsync(AuditKinds.AccountDeleted);
        Assert.Equal(user, row.ActorUserId);
        Assert.Equal(user, row.SubjectUserId);
        Assert.Equal(Private, row.SubjectEmail);
        Assert.Equal(space, row.OrgId);
        Assert.Equal(PersonalSpaces.Name, row.OrgName);
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Equal("self", AuditApi.Text(row, "reason"));

        using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refresh);
        await AssertNoSecretsAsync(UserPassword, session.AccessToken, session.RefreshToken);
    }

    [Fact]
    public async Task A_login_afterwards_is_the_401_of_an_unknown_address_and_a_new_sign_up_completes()   // criterion 21, Review Focus 3
    {
        var (user, _, session) = await PrivateAsync();
        using (var deleted = await DeleteAsync(session.AccessToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var login = await LoginApi.Login(Client, Private, UserPassword))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
            Assert.Equal("""{"error":"invalid_credentials"}""", await login.Content.ReadAsStringAsync());
        }

        var token = await PendingSignupSeed.IssueAsync(Factory.Services, Private, null, SignupKind.Private);
        using var again = await SignupApi.CompletePrivate(Client, token, "Chosen-Passw0rd");
        var fresh = await SessionApi.ReadSessionAsync(again);
        Assert.NotEqual(user.ToString(), AccessTokens.Text(fresh.AccessToken, "sub"));
        Assert.Equal("personal", AccessTokens.Text(fresh.AccessToken, "org_kind"));
    }

    [Fact]
    public async Task Without_a_token_it_is_401()   // criterion 22
    {
        using var response = await DeleteAsync(null);

        await TenancyApi.AssertUnauthorizedAsync(response);
    }

    [Fact]
    public async Task A_token_that_says_personal_after_the_account_moved_to_a_company_is_permissions_changed()   // criterion 22
    {
        var (user, _, session) = await PrivateAsync();
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, Private, "user");
        await EnqueueInvitationAsync(invite, Private);
        await DispatchAsync();
        using (var moved = await TenancyApi.AcceptLeaving(Client, TokenIn(Mail.Sent.Last(m => m.To == Private))))
        {
            Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
        }

        using var response = await DeleteAsync(session.AccessToken);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");   // never forbidden: the frontend refreshes
        Assert.Equal(1, await UsersAsync(user));
    }

    [Fact]
    public async Task A_token_that_says_personal_after_the_account_is_gone_is_permissions_changed()   // criterion 22
    {
        var (_, _, session) = await PrivateAsync();
        using (var first = await DeleteAsync(session.AccessToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        using var again = await DeleteAsync(session.AccessToken);   // the access token lives on for its minutes

        await TenancyApi.AssertErrorAsync(again, HttpStatusCode.Forbidden, "permissions_changed");
    }

    [Fact]
    public async Task A_company_member_is_403_forbidden_and_nothing_is_counted()   // criterion 22
    {
        var token = (await SessionApi.LoginAsync(Client, Factory)).AccessToken;

        using var response = await DeleteAsync(token, "Wrong-Password-1");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
        Assert.Equal(1, await UsersAsync(await Factory.SeedUserIdAsync()));
    }

    public static TheoryData<string> BadBodies => new()
    {
        "{}",
        """{"password":""}""",
        """{"password":"   "}""",
        """{"password":5}""",
        """{"password":null}""",
        """["password"]""",
        "not json",
        "{\"password\":\"Another" + Backslash + "u0000Passw0rd\"}",   // NUL in the password
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request_and_counts_for_nothing(string body)   // criterion 22
    {
        var (user, _, session) = await PrivateAsync();

        using var response = await RawAsync(session.AccessToken, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await UsersAsync(user));
    }

    [Fact]
    public async Task A_wrong_password_is_403_wrong_password_counted_and_recorded()   // criterion 22, Review Focus 3
    {
        var (user, space, session) = await PrivateAsync();

        using var response = await DeleteAsync(session.AccessToken, "Wrong-Password-1");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Equal(1, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));   // counted as a failed login
        Assert.Equal(1, await UsersAsync(user));
        var row = await SingleAsync(AuditKinds.AccountDeleteRefused);
        Assert.Equal(user, row.ActorUserId);
        Assert.Equal(user, row.SubjectUserId);
        Assert.Equal(Private, row.SubjectEmail);
        Assert.Equal(space, row.OrgId);
        Assert.Equal(PersonalSpaces.Name, row.OrgName);
        Assert.Equal("wrong_password", AuditApi.Text(row, "reason"));
        await AssertNoSecretsAsync("Wrong-Password-1", UserPassword);
    }

    [Fact]
    public async Task A_locked_identifier_is_429_too_many_attempts_and_recorded()   // criterion 22
    {
        var (user, _, session) = await PrivateAsync();
        await LockoutApi.FailAsync(Client, Clock, Private, 10);

        using var response = await DeleteAsync(session.AccessToken);   // even the right password

        await LockoutApi.AssertLockedAsync(response);
        Assert.Equal(1, await UsersAsync(user));
        Assert.Equal("locked", AuditApi.Text(await SingleAsync(AuditKinds.AccountDeleteRefused), "reason"));
    }

    [Fact]
    public async Task A_body_in_another_charset_is_415_and_one_that_is_not_json_is_400()   // criterion 22
    {
        var (_, _, session) = await PrivateAsync();

        using var utf16 = await RawAsync(session.AccessToken, """{"password":"x"}""", "application/json; charset=utf-16");
        using var plain = await RawAsync(session.AccessToken, """{"password":"x"}""", "text/plain");

        await TenancyApi.AssertErrorAsync(utf16, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        await TenancyApi.AssertErrorAsync(plain, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_deletion_that_finds_the_space_no_longer_personal_under_the_lock_is_permissions_changed()   // spec 0010 → Locks
    {
        var (user, space, session) = await PrivateAsync();
        var userRole = await RoleIdAsync(await DevCompanyIdAsync(), "user");
        Task<HttpResponseMessage> pending;

        await using (var held = await HeldLock.AddressAsync(Factory, "OLA@POCZTA.TEST"))
        {
            pending = DeleteAsync(session.AccessToken);
            await HeldLock.WaitForWaitersAsync(Factory, 1);   // checked, password right, waiting for the address

            // Meanwhile the account moves into a company (as an invitation would, which takes the address lock too).
            await InDbAsync(async db =>
            {
                await db.Memberships.Where(m => m.UserId == user).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
                await db.Companies.Where(c => c.Id == space).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
                db.Memberships.Add(new Membership { UserId = user, CompanyId = await DevCompanyIdAsync(), RoleId = userRole, JoinedAt = StorableTime.Now(Clock) });
                return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            });
            await held.ReleaseAsync();
        }

        using var response = await pending;
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permissions_changed");
        Assert.Equal(1, await UsersAsync(user));
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleted));
    }
}
```

`tests/Auth.IntegrationTests/PrivateAccountRaceTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0010, criterion 27: a self-deletion, an invitation acceptance and a sign-up completion for one address, in every order, never leave an
/// account without a space or two accounts; a removal beside an acceptance leaves one consistent outcome. Each race holds a lock of the test and
/// lets the requests queue behind it in the order named, as <c>SignupCompleteRaceTests</c> does.
/// </summary>
public sealed class PrivateAccountRaceTests : SignupTestBase
{
    private const string Private = "ola@poczta.test";
    private const string Normalized = "OLA@POCZTA.TEST";

    public PrivateAccountRaceTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithPrivateSignup();
    }

    private async Task AssertOneAccountWithOneSpaceAsync(string normalized)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var users = await InDbAsync(db => db.Users.AsNoTracking().Where(u => u.NormalizedEmail == normalized).Select(u => u.Id).ToListAsync(cancellation));
        var user = Assert.Single(users);
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user, cancellation)));
        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(
            c => c.Kind == CompanyKind.Personal && !db.Memberships.Any(m => m.CompanyId == c.Id), cancellation)));
    }

    [Theory]
    [InlineData("delete,accept,complete", 204, 204, 400)]
    [InlineData("delete,complete,accept", 204, 204, 400)]
    [InlineData("accept,delete,complete", 403, 204, 409)]
    [InlineData("accept,complete,delete", 403, 204, 409)]
    [InlineData("complete,delete,accept", 204, 204, 409)]
    [InlineData("complete,accept,delete", 403, 204, 409)]
    public async Task A_self_deletion_an_acceptance_and_a_completion_never_leave_an_account_without_a_space_or_two_accounts(
        string order, int deleted, int accepted, int completed)   // criterion 27, Review Focus 5
    {
        await CreatePrivateAccountAsync(Private);
        var session = await SessionApi.LoginAsync(Client, Private, UserPassword);
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, Private, "user");
        await EnqueueInvitationAsync(invite, Private);
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == Private));
        var signupToken = await PendingSignupSeed.IssueAsync(Factory.Services, Private, null, SignupKind.Private);

        var started = new Dictionary<string, Task<HttpResponseMessage>>(StringComparer.Ordinal);
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            var waiting = 0;
            foreach (var step in order.Split(','))
            {
                started[step] = step switch
                {
                    "delete" => TenancyApi.Send(Client, HttpMethod.Delete, "/auth/account", session.AccessToken, new { password = UserPassword }),
                    "accept" => TenancyApi.AcceptLeaving(Client, inviteToken, "Invited-Passw0rd"),
                    _ => SignupApi.CompletePrivate(Client, signupToken, Password),
                };
                await HeldLock.WaitForWaitersAsync(Factory, ++waiting);
            }

            await held.ReleaseAsync();
        }

        using var deletion = await started["delete"];
        using var acceptance = await started["accept"];
        using var completion = await started["complete"];
        Assert.Equal((HttpStatusCode)deleted, deletion.StatusCode);
        Assert.Equal((HttpStatusCode)accepted, acceptance.StatusCode);
        Assert.Equal((HttpStatusCode)completed, completion.StatusCode);
        await AssertOneAccountWithOneSpaceAsync(Normalized);
    }

    [Fact]
    public async Task A_removal_not_yet_committed_makes_a_parallel_acceptance_already_member_and_the_invitation_stays()   // criterion 27
    {
        var (acme, _, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var worker = await AddMemberAsync(acme, "worker@acme.test", "user");
        var globex = await CreateCompanyAsync("Globex");
        var invite = await AddInviteAsync(globex, "worker@acme.test", "user");
        await EnqueueInvitationAsync(invite, "worker@acme.test");
        await DispatchAsync();
        var token = TokenIn(Mail.Sent.Last(m => m.To == "worker@acme.test"));

        await using (var held = await HeldLock.AuditTableAsync(Factory))
        {
            var removal = TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", adminToken);
            await HeldLock.WaitForWaitersAsync(Factory, 1);   // the account's rows are deleted, not committed: the removal waits at its audit rows

            using (var early = await TenancyApi.Accept(Client, token, "Invited-Passw0rd"))
            {
                await TenancyApi.AssertErrorAsync(early, HttpStatusCode.Conflict, "already_member");   // it saw the committed state: a member
            }

            await held.ReleaseAsync();
            using var removed = await removal;
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var later = await TenancyApi.Accept(Client, token, "Invited-Passw0rd");   // the invitation stayed usable
        Assert.Equal(HttpStatusCode.NoContent, later.StatusCode);
        await AssertOneAccountWithOneSpaceAsync("WORKER@ACME.TEST");
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));   // a new account
    }

    [Fact]
    public async Task An_acceptance_that_waits_for_the_address_while_a_removal_commits_makes_a_new_account()   // criterion 27
    {
        var (acme, _, adminToken) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        var worker = await AddMemberAsync(acme, "worker@acme.test", "user");
        var globex = await CreateCompanyAsync("Globex");
        var invite = await AddInviteAsync(globex, "worker@acme.test", "user");
        await EnqueueInvitationAsync(invite, "worker@acme.test");
        await DispatchAsync();
        var token = TokenIn(Mail.Sent.Last(m => m.To == "worker@acme.test"));

        Task<HttpResponseMessage> acceptance;
        await using (var held = await HeldLock.AddressAsync(Factory, "WORKER@ACME.TEST"))
        {
            acceptance = TenancyApi.Accept(Client, token, "Invited-Passw0rd");
            await HeldLock.WaitForWaitersAsync(Factory, 1);   // the invitation used up, waiting for the address
            using (var removed = await TenancyApi.Send(Client, HttpMethod.Delete, $"/auth/org/members/{worker}", adminToken))
            {
                Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);   // a removal takes no address lock
            }

            await held.ReleaseAsync();
        }

        using var accepted = await acceptance;
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        await AssertOneAccountWithOneSpaceAsync("WORKER@ACME.TEST");
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == worker, TestContext.Current.CancellationToken)));
    }
}
```

Change the contract tests:

(a) `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`: replace `        Assert.Equal(30, operations.Count);` with `        Assert.Equal(31, operations.Count);   // spec 0010: DELETE /auth/account`
and `        Assert.Equal(20, covered);` with `        Assert.Equal(21, covered);   // spec 0010: DELETE /auth/account`; and after the method
`The_permission_org_delete_is_named_in_the_description_and_in_the_operation` add

```csharp

    [Fact]
    public async Task The_account_deletion_is_described_with_its_body_its_answers_and_the_cookie_it_clears()   // spec 0010, criterion 30
    {
        var (document, operations) = await DescriptionAsync();

        var operation = operations["DELETE /auth/account"];

        var schema = operation["requestBody"]!["content"]!["application/json"]!["schema"]!.AsObject();
        if (schema["$ref"] is { } reference)
        {
            schema = document["components"]!["schemas"]![reference.GetValue<string>().Split('/')[^1]]!.AsObject();
        }

        Assert.Equal(["password"], schema["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["204", "400", "401", "403", "415", "429"], operation["responses"]!.AsObject().Select(r => r.Key).Order(StringComparer.Ordinal));
        Assert.All(["forbidden", "permissions_changed", "wrong_password"], code => Assert.Contains(code, Description(operation, "403")));
        Assert.All(["too_many_attempts", "too_many_requests"], code => Assert.Contains(code, Description(operation, "429")));
        Assert.Contains("auth_rt", operation["responses"]!["204"]!["headers"]!["Set-Cookie"]!["description"]!.GetValue<string>());
        Assert.NotNull(operation["security"]);
    }
```

(b) `tests/Auth.IntegrationTests/OpenApiTests.cs`:
- in `Endpoints`, replace `        "GET /auth/me",` with `        "GET /auth/me",` followed by a new line `        "DELETE /auth/account",`;
- after `    [InlineData("GET /auth/me", "200,401,403,429")]` add `    [InlineData("DELETE /auth/account", "204,400,401,403,415,429")]`;
- after `    [InlineData("DELETE /auth/org", "name,password")]` add `    [InlineData("DELETE /auth/account", "password")]`;
- after `    [InlineData("POST /auth/logout", "204")]` (in `The_answers_that_set_or_clear_the_refresh_cookie_say_so`) add `    [InlineData("DELETE /auth/account", "204")]`;
- replace `        string[] setting = ["POST /auth/login 200", "POST /auth/signup/complete 200", "POST /auth/refresh 200", "POST /auth/logout 204"];` with
  `        string[] setting = ["POST /auth/login 200", "POST /auth/signup/complete 200", "POST /auth/refresh 200", "POST /auth/logout 204", "DELETE /auth/account 204"];`;
- replace `            var guarded = endpoint.Split(' ')[1].StartsWith("/auth/org", StringComparison.Ordinal) || endpoint.EndsWith("/auth/me", StringComparison.Ordinal);` with
  `            var guarded = endpoint.Split(' ')[1].StartsWith("/auth/org", StringComparison.Ordinal) || endpoint.EndsWith("/auth/me", StringComparison.Ordinal) || endpoint == "DELETE /auth/account";`;
- after `        Assert.Contains("private_account_exists", Description(operations["POST /auth/invites/accept"], "409"));` (Task 12) add
  `        Assert.All(["forbidden", "permissions_changed", "wrong_password"], code => Assert.Contains(code, Description(operations["DELETE /auth/account"], "403")));`;
- the comment `    // Every endpoint of specs 0001–0009, as "METHOD path".` becomes `    // Every endpoint of specs 0001–0010, as "METHOD path".` (copy the dash from the file: it is not an ASCII hyphen).

(c) `tests/Auth.IntegrationTests/RatePolicyTests.cs`: after `    [InlineData("PATCH", "/auth/org", RatePolicy.General)]` add `    [InlineData("DELETE", "/auth/account", RatePolicy.General)]   // spec 0010 → Limits, as DELETE /auth/org`.

- [ ] **Step 2: See them fail.** Build, then run the classes of Step 5: the route does not exist (`405`/`404`).

- [ ] **Step 3: The endpoint.** In `src/Auth.Server/Tenancy/Contracts.cs`, after the record `DeleteOrgRequest` add

```csharp

/// <summary>Request of <c>DELETE /auth/account</c> (spec 0010): the caller's own password.</summary>
public sealed record DeleteAccountRequest(string Password);
```

`src/Auth.Server/Api/AccountDeletionEndpoint.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Auth.Server.Requests;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Api;

/// <summary>
/// <c>DELETE /auth/account</c> (spec 0010): a private person deletes their own account. The checks run in this order, as <c>DELETE /auth/org</c>
/// does: the token's <c>org_kind</c> against the database (a token that says <c>personal</c> while the database shows a company or nothing is
/// <c>403 permissions_changed</c>, so that the frontend refreshes; a company member is <c>403 forbidden</c>: company accounts are managed by their
/// admins), the body, the lockout of the caller's identifier, the password (the attempt counted first, as a login counts it; a correct one ends
/// the streak). Then, under the address lock, the membership is read again (an invitation may have moved the account meanwhile) and the account
/// is deleted with everything it has (<see cref="AccountDeletion"/>, reason <c>self</c>); the answer clears the refresh cookie as logout does.
/// The product deletes its own data under the token's <c>org_id</c> before it calls this: Auth-Core does not tell it.
/// </summary>
public static class AccountDeletionEndpoint
{
    public const string Path = "/auth/account";

    private static readonly string[] Fields = ["password"];

    public static async Task<IResult> DeleteAsync(
        HttpContext http, MembershipReader memberships, UserManager<ApplicationUser> users, LoginStreakStore streaks, AuthDbContext db,
        AccountDeletion deletion, AuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(streaks);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(deletion);
        ArgumentNullException.ThrowIfNull(audit);

        // The sub of a token this service issued is always a user id; anything else is not a caller we know.
        if (!Guid.TryParse(http.User.FindFirst(Claims.Subject)?.Value, out var userId))
        {
            return ApiResults.Error(TenancyErrors.Forbidden);
        }

        var tenant = await memberships.ReadAsync(userId, http.RequestAborted);
        if (Refusal(http.User, tenant) is { } refused)
        {
            return refused;
        }

        var caller = tenant!;
        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, http.RequestAborted);
        if (fields is null || fields[0].Contains('\0'))
        {
            return ApiResults.InvalidRequest();
        }

        var user = await users.FindByIdAsync(userId.ToString());
        if (user?.Email is null)
        {
            return ApiResults.Error(TenancyErrors.PermissionsChanged);   // gone since the membership was read
        }

        var normalized = users.NormalizeEmail(user.Email);
        var identifier = LoginIdentifier.HashOf(normalized);
        var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
        if (!decision.Allowed)
        {
            await RecordRefusalAsync(audit, caller, user.Email, "locked");
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        if (!await users.CheckPasswordAsync(user, fields[0]))
        {
            await RecordRefusalAsync(audit, caller, user.Email, "wrong_password");
            return ApiResults.Error(TenancyErrors.WrongPassword);
        }

        // Not the request's token from here on: from the tenth attempt counting has started a cooldown, and a client that goes away must leave
        // neither the cooldown nor a half-done deletion behind.
        await streaks.ClearAsync(identifier, CancellationToken.None);

        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        await AddressLock.AcquireAsync(db, normalized, CancellationToken.None);
        var current = await memberships.ReadAsync(userId, CancellationToken.None);
        if (current is not { Kind: CompanyKind.Personal })
        {
            return ApiResults.Error(TenancyErrors.PermissionsChanged);   // moved into a company, or gone, while this request waited
        }

        await deletion.DeleteAsync(userId, AccountDeletionReason.Self, actor: null, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        RefreshCookie.Clear(http.Response);
        return ApiResults.NoContent();
    }

    /// <summary>
    /// The first rows of the table of spec 0010, the token's <c>org_kind</c> judged first; <see langword="null"/> for a private account. A token
    /// that does not say <c>personal</c> while the database shows a personal space is let through: the database decides.
    /// </summary>
    private static IResult? Refusal(System.Security.Claims.ClaimsPrincipal user, TenantContext? tenant)
    {
        if (tenant is { Kind: CompanyKind.Personal })
        {
            return null;
        }

        var saysPersonal = string.Equals(user.FindFirst(TenantClaims.OrgKind)?.Value, SpaceKinds.PersonalName, StringComparison.Ordinal);
        if (saysPersonal)
        {
            return ApiResults.Error(TenancyErrors.PermissionsChanged);   // it moved to a company, or it is gone: the frontend refreshes
        }

        if (tenant is null)
        {
            return ApiResults.Error(user.HasClaim(c => c.Type == TenantClaims.OrgId) ? TenancyErrors.PermissionsChanged : TenancyErrors.Forbidden);
        }

        return ApiResults.Error(TenancyErrors.Forbidden);   // a company member: company accounts are managed by their admins
    }

    private static Task RecordRefusalAsync(AuditLog audit, TenantContext caller, string email, string reason) =>
        audit.WriteAloneAsync(new AuditEntry
        {
            Kind = AuditKinds.AccountDeleteRefused,
            ActorUserId = caller.UserId,
            SubjectUserId = caller.UserId,
            SubjectEmail = email,
            OrgId = caller.CompanyId,
            OrgName = caller.CompanyName,
            Details = new Dictionary<string, object?> { ["reason"] = reason },
        });
}
```

- [ ] **Step 4: Map it.** In `src/Auth.Server/Api/TenancyEndpoints.cs`, add `using Auth.Server.Sessions;` and, after the mapping of
`OrgEndpoints.MePath` (the statement that ends `            .ProducesGuarded();` right after `.Produces<MeResponse>()`), add

```csharp

        // Spec 0010: a private account deletes itself. Behind the access token like the company API; its own path, outside /auth/org.
        app.MapDelete(AccountDeletionEndpoint.Path, AccountDeletionEndpoint.DeleteAsync)
            .RequireAuthorization()
            .WithTags("Account")
            .WithDescription(
                "Deletes the caller's own private account with everything Auth-Core holds of it: the personal space, the sessions, the mail links, "
                + "the queued mails, the pending sign-up and the login streak; the audit rows stay for their retention. Needs the caller's password. "
                + "The product deletes its own data under the token's `org_id` first: Auth-Core does not tell it. A token that says `org_kind` "
                + "`personal` while the account has moved to a company or is gone is `403 permissions_changed`; a company member is `403 forbidden` "
                + "(company accounts are managed by their admins). A wrong password counts in the login streak as a failed login does; while the "
                + "identifier is locked the answer is `429 too_many_attempts`.")
            .ReadsJson<DeleteAccountRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .SetsRefreshCookie(
                StatusCodes.Status204NoContent, $"`{RefreshCookie.Name}` emptied, with `Max-Age=0`: the browser drops the cookie, as at a logout.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.WrongPassword)
            .ProducesTooManyAttempts()
            .ProducesGuarded();
```

- [ ] **Step 5: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.AccountDeletionEndpointTests -class Auth.IntegrationTests.PrivateAccountRaceTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests -class Auth.IntegrationTests.RatePolicyTests -class Auth.IntegrationTests.JsonCharsetTests -class Auth.IntegrationTests.SecurityHeadersTests -class Auth.IntegrationTests.OrgEndpointTests`
passes.

- [ ] **Step 6: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): DELETE /auth/account deletes a private account under the address lock`.

---

## Day 6 — Thursday 16.04: the Python side and the sample frontend

When the day is done, a backend that uses the Python package knows the kind of space, the notes service deletes a private person's notes,
the shipped manifests turn private sign-up on, and the sample shows every screen of the spec.

### Task 14: The Python package's `org_kind`, `DELETE /api/me`, the shipped manifests

**Files:**
- Modify: `clients/python/src/auth_core_fastapi/_principal.py`, `clients/python/src/auth_core_fastapi/_verify.py`, `samples/notes-api/src/notes_api/app.py`, `samples/notes-api/tests/conftest.py`, `samples/notes-api/auth.yaml`, `deploy/auth.yaml`
- Modify (tests): `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`, `samples/notes-api/tests/test_shapes_and_limits.py`
- Create (tests): `clients/python/tests/test_org_kind.py`, `samples/notes-api/tests/test_delete_my_data.py`

**Interfaces:**
- Consumes: the `org_kind` claim (Task 3); `Manifest.PrivateSignup`, `PrivatePermissions` (Task 2).
- Produces: `Principal.org_kind: str = "organization"` (the last field); `DELETE /api/me` in the notes service (`204` for `org_kind` `personal`, its notes of the token's `org_id` deleted; `403 {"error":"forbidden"}` for a company member; `401` without a token); the `private` block in both shipped manifests.

**Contract counts:** `ShippedConfigurationTests.A_host_with_the_shipped_manifest_and_the_development_address_has_sign_up_on_and_is_healthy`: the options become `{"signup":"self-service","private_signup":"self-service"}`. The package's earlier tests are not edited (criterion 15). In the sample backend, `test_shapes_and_limits.py::test_the_allow_header_of_every_route_lists_all_the_methods_of_its_path` probed every `/api` path with `DELETE`; `/api/me` now has `DELETE`, so each path is probed with a method it does not have.

**Decisions the plan takes here:**
- The package reads the claim with `"org_kind" in claims`, so that a JSON `null` is a claim that is present, and refused.
- `DELETE /api/me` answers `204` with no body, never stored, also when the space has no notes; the package's own `401` covers a missing token.
- The version of the package is not changed: the release is part of `v0.2.0` (spec 0010 → Python package).

- [ ] **Step 1: Write the failing tests.** `clients/python/tests/test_org_kind.py`:

```python
"""Spec 0010, criterion 15: `Principal.org_kind`, read from the claim `org_kind`; a token of an earlier Auth-Core has none."""

import dataclasses

import pytest
from fastapi import Depends, FastAPI
from fastapi.testclient import TestClient

from auth_core_fastapi import AuthCore, Principal
from helpers import MISSING, assert_unauthorized, bearer


def _client(auth: AuthCore) -> TestClient:
    app = FastAPI()
    auth.install(app)

    @app.get("/kind")
    def kind(user: Principal = Depends(auth.current_user)):
        return {"org_kind": user.org_kind}

    return TestClient(app)


@pytest.mark.parametrize("value", ["organization", "personal"])
def test_the_claim_gives_the_kind(auth, key1, value):  # criterion 15
    response = _client(auth).get("/kind", headers=bearer(key1.token(org_kind=value)))

    assert response.status_code == 200
    assert response.json() == {"org_kind": value}


def test_a_token_without_the_claim_is_an_organization(auth, key1):  # criterion 15: the tokens of earlier instances
    response = _client(auth).get("/kind", headers=bearer(key1.token(org_kind=MISSING)))

    assert response.status_code == 200
    assert response.json() == {"org_kind": "organization"}


@pytest.mark.parametrize("value", [None, "", "Personal", "company", 1, True, ["personal"], {"kind": "personal"}])
def test_any_other_value_is_a_401(auth, key1, value):  # criterion 15
    assert_unauthorized(_client(auth).get("/kind", headers=bearer(key1.token(org_kind=value))))


def test_org_kind_is_the_last_field_and_defaults_to_organization():  # code that builds a Principal by position keeps working
    principal = Principal("user-1", "org-1", ("user",), frozenset())

    assert principal.org_kind == "organization"
    assert [field.name for field in dataclasses.fields(Principal)][-1] == "org_kind"
```

`samples/notes-api/tests/conftest.py`: replace

```python
    def token(self, org_id: str, permissions: list[str], sub: str = "user-1") -> str:
        now = int(time.time())
        claims = {
            "iss": ISSUER, "aud": AUDIENCE, "sub": sub, "org_id": org_id, "roles": [],
            "permissions": permissions, "iat": now, "exp": now + 600,
        }
        return jwt.encode(claims, self.key, algorithm="RS256", headers={"kid": "k1", "typ": "at+jwt"})
```

with

```python
    def token(self, org_id: str, permissions: list[str], sub: str = "user-1", org_kind: str | None = None) -> str:
        now = int(time.time())
        claims = {
            "iss": ISSUER, "aud": AUDIENCE, "sub": sub, "org_id": org_id, "roles": [],
            "permissions": permissions, "iat": now, "exp": now + 600,
        }
        if org_kind is not None:  # spec 0010; without it the token is a company member's, as Auth-Core's before 0010
            claims["org_kind"] = org_kind
        return jwt.encode(claims, self.key, algorithm="RS256", headers={"kid": "k1", "typ": "at+jwt"})
```

and replace

```python
def headers(signer: Signer, org_id: str = COMPANY_A, permissions=("notes:read", "notes:write"), sub: str = "user-1") -> dict:
    return {"Authorization": "Bearer " + signer.token(org_id, list(permissions), sub)}
```

with

```python
def headers(
    signer: Signer, org_id: str = COMPANY_A, permissions=("notes:read", "notes:write"), sub: str = "user-1", org_kind: str | None = None
) -> dict:
    return {"Authorization": "Bearer " + signer.token(org_id, list(permissions), sub, org_kind)}
```

`samples/notes-api/tests/test_delete_my_data.py`:

```python
"""Spec 0010: DELETE /api/me, the sample's "delete my data" (criterion 33)."""

import uuid

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from conftest import COMPANY_A, COMPANY_B, headers
from notes_api.db import Note


def add(client, signer, text, org_id, org_kind=None):
    response = client.post("/api/notes", json={"text": text}, headers=headers(signer, org_id, org_kind=org_kind))
    assert response.status_code == 201


def count(engine, org_id) -> int:
    with Session(engine) as session:
        return session.scalar(select(func.count()).select_from(Note).where(Note.org_id == uuid.UUID(org_id)))


def test_a_private_account_deletes_its_notes_and_no_other(client, signer, engine):  # criterion 33
    add(client, signer, "mine", COMPANY_A, "personal")
    add(client, signer, "mine too", COMPANY_A, "personal")
    add(client, signer, "theirs", COMPANY_B, "personal")

    response = client.delete("/api/me", headers=headers(signer, COMPANY_A, permissions=(), org_kind="personal"))

    assert response.status_code == 204
    assert response.content == b""
    assert response.headers["cache-control"] == "no-store"
    assert count(engine, COMPANY_A) == 0
    assert count(engine, COMPANY_B) == 1


def test_it_is_204_when_there_are_no_notes(client, signer):  # criterion 33
    response = client.delete("/api/me", headers=headers(signer, COMPANY_A, org_kind="personal"))

    assert response.status_code == 204


def test_a_company_member_is_403_forbidden_and_nothing_is_deleted(client, signer, engine):  # criterion 33
    add(client, signer, "company note", COMPANY_A)

    for kind in (None, "organization"):
        response = client.delete("/api/me", headers=headers(signer, COMPANY_A, org_kind=kind))
        assert response.status_code == 403
        assert response.json() == {"error": "forbidden"}
        assert response.headers["cache-control"] == "no-store"

    assert count(engine, COMPANY_A) == 1


def test_without_a_token_it_is_401(client):  # criterion 33
    response = client.delete("/api/me")

    assert response.status_code == 401


def test_a_token_of_another_kind_is_refused_before_anything_is_read(client, signer):  # the package refuses a value it does not know
    response = client.delete("/api/me", headers=headers(signer, COMPANY_A, org_kind="team"))

    assert response.status_code == 401
```

In `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`: in `The_shipped_manifests_turn_sign_up_on` replace
`        Assert.Equal("admin", result.Manifest.FirstAdminRole.Name);` with

```csharp
        Assert.Equal("admin", result.Manifest.FirstAdminRole.Name);
        Assert.True(result.Manifest.PrivateSignup);   // spec 0010 → Verification notes: the e2e checks need private sign-up on
        Assert.NotEmpty(result.Manifest.PrivatePermissions);
```

and in `A_host_with_the_shipped_manifest_and_the_development_address_has_sign_up_on_and_is_healthy` replace the line (the same line is in
another method of the class too: change only the one in this method)
`        Assert.Equal("""{"signup":"self-service","private_signup":"off"}""", await BodyAsync(client, "/auth/options"));` with
`        Assert.Equal("""{"signup":"self-service","private_signup":"self-service"}""", await BodyAsync(client, "/auth/options"));`.

- [ ] **Step 2: See them fail.** `cd clients/python && C:/p6v/Scripts/python.exe -m pytest -q tests/test_org_kind.py` fails (`Principal` has no
`org_kind`); `cd samples/notes-api && PYTHONPATH=../../clients/python/src C:/p6v/Scripts/python.exe -m pytest -q tests/test_delete_my_data.py`
fails (`405`/`404`); the build and `ShippedConfigurationTests` fail on the manifests.

- [ ] **Step 3: The package.** Replace the content of `clients/python/src/auth_core_fastapi/_principal.py` with

```python
from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class Principal:
    """The caller of a request, as its access token says: who, in which space, with what rights.

    `org_kind` is "organization" for a member of a company and "personal" for a private account, whose space nobody else can join
    (Auth-Core spec 0010). A token of an Auth-Core before that has no such claim, and is a company member's.
    """

    sub: str
    org_id: str
    roles: tuple[str, ...]
    permissions: frozenset[str]
    org_kind: str = "organization"
```

In `clients/python/src/auth_core_fastapi/_verify.py` replace

```python
def _principal(claims: dict) -> Principal:
    sub, org_id = claims.get("sub"), claims.get("org_id")
    roles, permissions = claims.get("roles"), claims.get("permissions")
    if not (isinstance(sub, str) and sub and isinstance(org_id, str) and org_id):
        raise TokenRejected("claims")
    if not (_is_list_of_text(roles) and _is_list_of_text(permissions)):
        raise TokenRejected("claims")
    return Principal(sub=sub, org_id=org_id, roles=tuple(roles), permissions=frozenset(permissions))
```

with

```python
_ORG_KINDS = ("organization", "personal")


def _principal(claims: dict) -> Principal:
    sub, org_id = claims.get("sub"), claims.get("org_id")
    roles, permissions = claims.get("roles"), claims.get("permissions")
    if not (isinstance(sub, str) and sub and isinstance(org_id, str) and org_id):
        raise TokenRejected("claims")
    if not (_is_list_of_text(roles) and _is_list_of_text(permissions)):
        raise TokenRejected("claims")
    # Absent: a token of an Auth-Core before spec 0010, a company member's. Present (a JSON null too) it must be one of the two names.
    org_kind = claims["org_kind"] if "org_kind" in claims else "organization"
    if not (isinstance(org_kind, str) and org_kind in _ORG_KINDS):
        raise TokenRejected("claims")
    return Principal(sub=sub, org_id=org_id, roles=tuple(roles), permissions=frozenset(permissions), org_kind=org_kind)
```

- [ ] **Step 4: `DELETE /api/me`.** In `samples/notes-api/src/notes_api/app.py`:

(a) Replace `from fastapi import Depends, FastAPI, Request` with `from fastapi import Depends, FastAPI, Request, Response` and
`from sqlalchemy import select, text` with `from sqlalchemy import delete, select, text`.

(b) Replace

```python
        return await run_in_threadpool(_insert, engine, org_id, user.sub, note_text)

    return app
```

with

```python
        return await run_in_threadpool(_insert, engine, org_id, user.sub, note_text)

    @app.delete("/api/me", status_code=204)
    def delete_my_data(user: Principal = Depends(auth.current_user)):
        """The sample's "delete my data" (Auth-Core spec 0010): a private person's notes go before the account does.

        No permission is needed: a person may always delete their own data. A company member is refused: the company's notes
        belong to the company, and a company account is not deleted by its owner.
        """
        if user.org_kind != "personal":
            return _error(403, "forbidden")
        org_id = _org_id(user)
        with Session(engine) as session:
            session.execute(delete(Note).where(Note.org_id == org_id))  # every note of the token's space, and no other
            session.commit()
        return Response(status_code=204)

    return app
```

(c) In `samples/notes-api/tests/test_shapes_and_limits.py`, in `test_the_allow_header_of_every_route_lists_all_the_methods_of_its_path`, replace

```python
        response = client.request("DELETE", path.replace("{note_id}", "x"))
```

with

```python
        probe = next(m for m in ("DELETE", "PATCH", "PUT") if m not in methods)  # /api/me has DELETE (spec 0010)
        response = client.request(probe, path.replace("{note_id}", "x"))
```

- [ ] **Step 5: The shipped manifests.** In `deploy/auth.yaml`, after the comment block's line
`#                "invite-only" (the default when the key is absent) means companies are made by the operator.` add

```yaml
# private        the block of spec 0010. "signup: self-service" lets a private person sign up alone, into a hidden one-person space
#                nobody else can join; it needs the same completion address. "permissions" is what the private role grants:
#                permissions of the catalog, never the four built-in ones or "*". Without the block private sign-up is off.
```

and after the line that is exactly `signup: self-service` (not the comment added above, which quotes the words) add

```yaml
private:
  signup: self-service
  permissions: [documents:read, documents:write]
```

In `samples/notes-api/auth.yaml`, after the comment line `#                role that holds "*" (admin). The overlay of the web sample sets the address of the completion screen.` add

```yaml
# private        a private person may sign up alone (spec 0010) and gets notes:read and notes:write in a space nobody else can join;
#                DELETE /api/me deletes that space's notes before the account is deleted.
```

and after the line that is exactly `signup: self-service` add

```yaml
private:
  signup: self-service
  permissions: [notes:read, notes:write]
```

- [ ] **Step 6: Run the tests.** `cd clients/python && C:/p6v/Scripts/python.exe -m pytest -q` (the whole package suite: its earlier tests pass
unchanged); `cd samples/notes-api && PYTHONPATH=../../clients/python/src C:/p6v/Scripts/python.exe -m pytest -q`; build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ShippedConfigurationTests`.
All pass.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(python): Principal.org_kind; notes-api deletes a private person's notes; the shipped manifests turn private sign-up on`.

### Task 15: The sample: the sign-up choice, the private completion, the sign-in link

**Files:**
- Modify: `samples/notes-web/src/app/account-api.ts`, `samples/notes-web/src/app/auth/auth.service.ts`, `samples/notes-web/src/app/texts.ts`, `samples/notes-web/src/app/pages/signup.ts` (replaced as a whole), `samples/notes-web/src/app/pages/complete-signup.ts` (replaced as a whole), `samples/notes-web/src/app/pages/login.ts`, `samples/notes-web/src/testing/helpers.ts`
- Modify (tests): `samples/notes-web/src/app/account-api.signup.spec.ts`, `samples/notes-web/src/app/auth/auth.service.signup.spec.ts`, `samples/notes-web/src/app/pages/complete-signup.spec.ts`, `samples/notes-web/src/app/pages/login.signup.spec.ts`, `samples/notes-web/src/app/pages/too-many-requests.spec.ts`
- Create (tests): `samples/notes-web/src/app/pages/signup.private.spec.ts`

**Interfaces:**
- Consumes: `GET /auth/options` with `private_signup`, `POST /auth/signup` with `kind`, the preview with `kind` (Tasks 2, 9, 11); `GET /auth/me` with `org_kind` (Task 3).
- Produces: `SiteOptions { signup; private_signup }`; `SignupKind = 'organization' | 'private'`; `SignupPreview { kind; email; company_name: string | null }` (no `has_account`); `AccountApi.signUp(email, companyName: string | null)` (`null`: a private request, `{email, kind: 'private'}`); `AuthService.completeSignup(token, password: string, companyName: string | null)` (`null`: no `company_name` member); `Me.org_kind`; test side `privateMe` in `src/testing/helpers.ts`, and `adminMe` gains `org_kind: 'organization'`.

**Contract counts:** none. The tests of Amendment 3 of spec 0009 go: `complete-signup.spec.ts` loses its block "for an account that exists without a company (has_account)", `account-api.signup.spec.ts` loses the three `has_account` tests, `auth.service.signup.spec.ts` loses "leaves the password out of the body"; `texts.completeSignup.keepsPassword` is removed.

- [ ] **Step 1: Write the failing tests.**

In `samples/notes-web/src/testing/helpers.ts` replace

```ts
export const adminMe: Me = {
  sub: '11111111-1111-1111-1111-111111111111',
  email: 'admin@example.test',
  org_id: '22222222-2222-2222-2222-222222222222',
  org_name: 'Acme',
  roles: ['admin'],
  permissions: ['notes:read', 'notes:write'],
};
```

with

```ts
export const adminMe: Me = {
  sub: '11111111-1111-1111-1111-111111111111',
  email: 'admin@example.test',
  org_id: '22222222-2222-2222-2222-222222222222',
  org_name: 'Acme',
  org_kind: 'organization',
  roles: ['admin'],
  permissions: ['notes:read', 'notes:write'],
};

/** A private account (spec 0010): its space is named "private", its role is "private". */
export const privateMe: Me = {
  sub: '33333333-3333-3333-3333-333333333333',
  email: 'ola@poczta.example',
  org_id: '44444444-4444-4444-4444-444444444444',
  org_name: 'private',
  org_kind: 'personal',
  roles: ['private'],
  permissions: ['notes:read', 'notes:write'],
};
```

`samples/notes-web/src/app/pages/signup.private.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { clickOn, has, pageText, settle, submitForm, typeInto } from '../../testing/helpers';
import { SignupPage } from './signup';

const status = (code: number) => ({ status: code, statusText: String(code) });
const sent = 'Check your inbox. We sent a message to this address.';
const both = { signup: 'self-service', private_signup: 'self-service' };

async function open(options: object) {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'signup', component: SignupPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/signup', SignupPage);
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne('/auth/options').flush(options);
  await settle(harness.fixture);
  return { fixture: harness.fixture, ctrl };
}

describe('SignupPage: organization or private person (spec 0010)', () => {
  it('offers the choice only when both are on, with the organization chosen first', async () => {
    const { fixture } = await open(both);
    expect(has(fixture, '[data-testid="signup-kind"]')).toBe(true);
    expect(pageText(fixture)).toContain('Organization');
    expect(pageText(fixture)).toContain('Private person');
    expect(has(fixture, '#company')).toBe(true);
  });

  it('with only private sign-up on there is no choice and no company name', async () => {
    const { fixture } = await open({ signup: 'invite-only', private_signup: 'self-service' });
    expect(has(fixture, '[data-testid="signup-kind"]')).toBe(false);
    expect(has(fixture, '#company')).toBe(false);
    expect(pageText(fixture)).toContain('Enter your email address. We will send you a link to finish.');
  });

  it('with only organization sign-up on there is no choice', async () => {
    const { fixture } = await open({ signup: 'self-service', private_signup: 'off' });
    expect(has(fixture, '[data-testid="signup-kind"]')).toBe(false);
    expect(has(fixture, '#company')).toBe(true);
  });

  it('a private person types only the email: the request says kind private and names no company, and the answer is the usual one', async () => {
    const { fixture, ctrl } = await open(both);
    clickOn(fixture, '[data-testid="kind-private"]');
    fixture.detectChanges();
    expect(has(fixture, '#company')).toBe(false);
    typeInto(fixture, '#email', 'ola@poczta.example');
    submitForm(fixture);
    const request = ctrl.expectOne('/auth/signup');
    expect(request.request.body).toEqual({ email: 'ola@poczta.example', kind: 'private' });
    request.flush(null, status(202));
    await settle(fixture);
    expect(pageText(fixture)).toContain(sent);
  });

  it('back to the organization: the company name is asked for and sent again', async () => {
    const { fixture, ctrl } = await open(both);
    clickOn(fixture, '[data-testid="kind-private"]');
    fixture.detectChanges();
    clickOn(fixture, '[data-testid="kind-organization"]');
    fixture.detectChanges();
    typeInto(fixture, '#email', 'anna@firma.example');
    typeInto(fixture, '#company', 'Firma');
    submitForm(fixture);
    expect(ctrl.expectOne('/auth/signup').request.body).toEqual({ email: 'anna@firma.example', company_name: 'Firma' });
  });

  it('400 invalid_request on a private request: says to check the email address', async () => {
    const { fixture, ctrl } = await open({ signup: 'invite-only', private_signup: 'self-service' });
    typeInto(fixture, '#email', 'ola@poczta.example');
    submitForm(fixture);
    ctrl.expectOne('/auth/signup').flush({ error: 'invalid_request' }, status(400));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Check the email address.');
  });
});
```

`samples/notes-web/src/app/pages/complete-signup.spec.ts`:

(a) replace

```ts
const preview = { email: 'anna@firma.example', company_name: 'Firma', has_account: false };
/** What the preview says of a person whose company is gone: the account exists and belongs to none (spec 0009, Amendment 3). */
const returning = { ...preview, has_account: true };
```

with

```ts
const preview = { kind: 'organization', email: 'anna@firma.example', company_name: 'Firma' };
/** What the preview says of a private sign-up (spec 0010): no company name. */
const privatePreview = { kind: 'private', email: 'ola@poczta.example' };
```

(b) replace `async function open(query = '?token=tok-s', previewAnswer: 'ok' | 'wait' = 'ok', answer: typeof preview = preview) {` with
`async function open(query = '?token=tok-s', previewAnswer: 'ok' | 'wait' = 'ok', answer: object = preview) {`;

(c) replace everything from the line `describe('CompleteSignupPage for an account that exists without a company (has_account)', () => {` to the end of
the file with

```ts
describe('CompleteSignupPage for a private sign-up (spec 0010)', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('shows the address (not editable) and the password twice, and no company name', async () => {
    const { fixture } = await open('?token=tok-s', 'ok', privatePreview);
    expect(valueOf(fixture, '#email')).toBe('ola@poczta.example');
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#email')?.readOnly).toBe(true);
    expect(has(fixture, '#company')).toBe(false);
    expect(has(fixture, '#password')).toBe(true);
    expect(has(fixture, '#repeat')).toBe(true);
    expect(pageText(fixture)).toContain('This sets the password for ola@poczta.example.');
    expect(pageText(fixture)).toContain('Create account');
  });

  it('200: posts the token and the password only, signs the person in and opens the notes screen in place of this one', async () => {
    const opened = await open('?token=tok-s', 'ok', privatePreview);
    choose(opened, 'Chosen-Passw0rd');
    const request = opened.ctrl.expectOne('/auth/signup/complete');
    expect(request.request.body).toEqual({ token: 'tok-s', password: 'Chosen-Passw0rd' });
    await signInBy(opened, request);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/notes', { replaceUrl: true });
  });

  it('two different passwords: a message, and no request', async () => {
    const opened = await open('?token=tok-s', 'ok', privatePreview);
    choose(opened, 'Chosen-Passw0rd', 'Other-Passw0rd');
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('The two passwords are not the same.');
    opened.ctrl.expectNone('/auth/signup/complete');
  });

  it('weak_password: the rules not met, and the form stays', async () => {
    const opened = await open('?token=tok-s', 'ok', privatePreview);
    choose(opened, 'short');
    opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'weak_password', rules: ['too_short'] }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('The password is too short.');
    expect(has(opened.fixture, '#password')).toBe(true);
  });

  it('409 already_registered: says the account exists and links to the sign-in; no form', async () => {
    const opened = await open('?token=tok-s', 'ok', privatePreview);
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'already_registered' }, status(409));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('An account with this address already exists.');
    expect(has(opened.fixture, 'form')).toBe(false);
    expect(has(opened.fixture, 'a[href="/login"]')).toBe(true);
  });

  it('400 invalid_request: says to check the password, and the form stays', async () => {
    const opened = await open('?token=tok-s', 'ok', privatePreview);
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'invalid_request' }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Check the password.');
    expect(has(opened.fixture, '#password')).toBe(true);
  });
});
```

`samples/notes-web/src/app/account-api.signup.spec.ts`:

(a) replace both `expect(await done).toEqual({ signup: 'self-service' });` with `expect(await done).toEqual({ signup: 'self-service', private_signup: 'off' });`,
and the three `expect(await done).toEqual({ signup: 'invite-only' });` with `expect(await done).toEqual({ signup: 'invite-only', private_signup: 'off' });`;
in the `it.each` title `'the answer %j means invite-only: the link is not offered'` keep the text;

(b) after the test `'ignores the members it does not know'` add

```ts

    it('reads private_signup: self-service, and anything else as off (spec 0010)', async () => {
      const done = api.options();
      ctrl.expectOne('/auth/options').flush({ signup: 'invite-only', private_signup: 'self-service' });
      expect(await done).toEqual({ signup: 'invite-only', private_signup: 'self-service' });

      const other = api.options();
      ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'on' });
      expect(await other).toEqual({ signup: 'self-service', private_signup: 'off' });
    });
```

(c) after the test `'signUp posts the email and the company name to its endpoint and is ok on the empty answer'` add

```ts

  it('signUp of a private person posts the email and the kind, and no company name (spec 0010)', async () => {
    const done = api.signUp('ola@poczta.example', null);
    const request = ctrl.expectOne('/auth/signup');
    expect(request.request.body).toEqual({ email: 'ola@poczta.example', kind: 'private' });
    request.flush(null, status(202));
    expect((await done).ok).toBe(true);
  });
```

(d) replace the three tests `'reads the answer to a preview: the address, the company name and whether the account exists'`,
`'reads has_account true: the person has an account and no company'` and the `it.each` `'an answer %j means has_account false: ...'` (from the
first one's `it(` to the closing `);` of the `it.each`) with

```ts
  it('reads the preview of an organization: the kind, the address and the company name', async () => {
    const done = api.previewSignup('tok');
    const request = ctrl.expectOne('/auth/signup/preview');
    expect(request.request.body).toEqual({ token: 'tok' });
    request.flush({ kind: 'organization', email: 'anna@firma.example', company_name: 'Firma' });
    expect(await done).toEqual({ ok: true, value: { kind: 'organization', email: 'anna@firma.example', company_name: 'Firma' } });
  });

  it('reads the preview of a private sign-up: the kind and the address, no company name (spec 0010)', async () => {
    const done = api.previewSignup('tok');
    ctrl.expectOne('/auth/signup/preview').flush({ kind: 'private', email: 'ola@poczta.example' });
    expect(await done).toEqual({ ok: true, value: { kind: 'private', email: 'ola@poczta.example', company_name: null } });
  });
```

`samples/notes-web/src/app/auth/auth.service.signup.spec.ts`: replace the test
`'leaves the password out of the body when there is none to send (an account that exists, without a company)'` (from its `it(` to its closing `});`) with

```ts
  it('leaves the company name out of the body of a private sign-up (spec 0010)', async () => {
    const done = auth.completeSignup('tok', 'Pw-1 x', null);
    const request = ctrl.expectOne('/auth/signup/complete');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok', password: 'Pw-1 x' });
    request.flush(signedIn);
    await settle();
    ctrl.expectOne('/auth/me').flush(adminMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('tok-c');
    ctrl.verify();
  });
```

`samples/notes-web/src/app/pages/login.signup.spec.ts`: after the test `'offers nothing when Auth-Core says invite-only'` add

```ts

  it('offers "Create an account" when only private sign-up is on (spec 0010)', async () => {
    const fixture = await open({ signup: 'invite-only', private_signup: 'self-service' });
    expect(has(fixture, 'a[href="/signup"][data-testid="create-account"]')).toBe(true);
  });
```

Change `samples/notes-web/src/app/pages/too-many-requests.spec.ts`: the new `SignupPage` asks `GET /auth/options` when it opens, and the
helper `expectNoticeOnly` ends with `opened.ctrl.verify()`, which fails on a request nobody answered. In the test
`SignupPage: leaves the words to the notice bar and does not say it sent anything` replace

```ts
    const opened = await open(SignupPage, '/signup');
    typeInto(opened.fixture, '#email', 'anna@firma.example');
```

with

```ts
    const opened = await open(SignupPage, '/signup');
    opened.ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'off' }); // spec 0010: the page asks first
    await settle(opened.fixture);
    typeInto(opened.fixture, '#email', 'anna@firma.example');
```

(No other spec that opens `/signup` calls `verify()`: `signup.spec.ts` and `app.routes.spec.ts` do not.)

- [ ] **Step 2: See them fail.** `cd samples/notes-web && npm test` (run `npm ci` first if `node_modules` is missing): the new tests fail, and
`complete-signup.spec.ts` fails to compile against `SignupPreview`.

- [ ] **Step 3: The calls.** In `samples/notes-web/src/app/account-api.ts`:

(a) Replace

```ts
/** What GET /auth/options answers, as far as the screens care: whether a visitor may start a company of their own. */
export interface SiteOptions {
  signup: 'self-service' | 'invite-only';
}

/**
 * What POST /auth/signup/preview answers: the address as it was typed, the company name of the request, and whether an account without a
 * company has the address (spec 0009, Amendment 3): then the completion asks for no password.
 */
export interface SignupPreview {
  email: string;
  company_name: string;
  has_account: boolean;
}
```

with

```ts
/** What GET /auth/options answers, as far as the screens care: whether a visitor may start a company, or sign up alone (spec 0010). */
export interface SiteOptions {
  signup: 'self-service' | 'invite-only';
  private_signup: 'self-service' | 'off';
}

/** A company of one's own, or a private account (spec 0010). */
export type SignupKind = 'organization' | 'private';

/** What POST /auth/signup/preview answers: the kind, the address as it was typed and, for an organization, the company name of the request. */
export interface SignupPreview {
  kind: SignupKind;
  email: string;
  company_name: string | null;
}
```

(b) Replace

```ts
  async options(): Promise<SiteOptions> {
    try {
      const body = await firstValueFrom(this.http.get<unknown>('/auth/options'));
      return { signup: isRecord(body) && body['signup'] === 'self-service' ? 'self-service' : 'invite-only' };
    } catch {
      return { signup: 'invite-only' };
    }
  }

  signUp(email: string, companyName: string): Promise<Outcome> {
    return this.postForm('/auth/signup', { email, company_name: companyName });
  }
```

with

```ts
  async options(): Promise<SiteOptions> {
    try {
      const body = await firstValueFrom(this.http.get<unknown>('/auth/options'));
      return {
        signup: isRecord(body) && body['signup'] === 'self-service' ? 'self-service' : 'invite-only',
        private_signup: isRecord(body) && body['private_signup'] === 'self-service' ? 'self-service' : 'off',
      };
    } catch {
      return { signup: 'invite-only', private_signup: 'off' };
    }
  }

  /** A company of one's own (`companyName`), or a private account (`null`: the kind is sent and no company name). */
  signUp(email: string, companyName: string | null): Promise<Outcome> {
    return this.postForm('/auth/signup', companyName === null ? { email, kind: 'private' } : { email, company_name: companyName });
  }
```

and the doc comment above `options()` gains, after its first sentence, `Each switch is read on its own (spec 0010).`.

(c) Replace

```ts
    const body = outcome.value;
    if (isRecord(body) && typeof body['email'] === 'string' && typeof body['company_name'] === 'string') {
      // Anything but a true boolean means "no account": the screen then asks for a password, which is the safe side.
      return { ok: true, value: { email: body['email'], company_name: body['company_name'], has_account: body['has_account'] === true } };
    }
    return { ok: false, failure: { kind: 'other' } };
```

with

```ts
    const body = outcome.value;
    if (isRecord(body) && typeof body['email'] === 'string') {
      if (body['kind'] === 'private') {
        return { ok: true, value: { kind: 'private', email: body['email'], company_name: null } };
      }
      if (typeof body['company_name'] === 'string') {
        return { ok: true, value: { kind: 'organization', email: body['email'], company_name: body['company_name'] } };
      }
    }
    return { ok: false, failure: { kind: 'other' } };
```

- [ ] **Step 4: The service.** In `samples/notes-web/src/app/auth/auth.service.ts`:

(a) In `Me`, after `  org_name: string;` add

```ts
  /** "personal" for a private account (spec 0010), whose space is named "private"; "organization" for a company member. */
  org_kind: 'organization' | 'personal';
```

(b) In `asMe`, after `      org_name: body['org_name'],` add `      org_kind: body['org_kind'] === 'personal' ? 'personal' : 'organization',`
(an answer without the member, from an Auth-Core before 0010, is a company member's).

(c) Replace

```ts
  /**
   * Finishes a self-service sign-up with the token of the mail link. Auth-Core answers as it answers a login (spec 0009, Amendment 2): the
   * access token in the body and the refresh cookie, so the person is signed in at once and the screen opens the product. A refusal leaves
   * the session that exists alone, as a failed login does. A person whose account exists and belongs to no company (the preview's
   * `has_account`, Amendment 3) chooses no password: pass `null` and the member is left out of the body.
   */
  async completeSignup(token: string, password: string | null, companyName: string): Promise<LoginResult> {
    let body: LoginBody | null;
    const request =
      password === null ? { token, company_name: companyName } : { token, password, company_name: companyName };
```

with

```ts
  /**
   * Finishes a self-service sign-up with the token of the mail link. Auth-Core answers as it answers a login (spec 0009, Amendment 2): the
   * access token in the body and the refresh cookie, so the person is signed in at once and the screen opens the product. A refusal leaves
   * the session that exists alone, as a failed login does. A private sign-up (spec 0010) names no company: pass `null` and the member is
   * left out of the body.
   */
  async completeSignup(token: string, password: string, companyName: string | null): Promise<LoginResult> {
    let body: LoginBody | null;
    const request = companyName === null ? { token, password } : { token, password, company_name: companyName };
```

- [ ] **Step 5: The texts.** In `samples/notes-web/src/app/texts.ts`:

(a) Replace

```ts
    intro: 'Enter your email address and the name of your company. We will send you a link to finish.',
    companyName: 'Company name',
```

with

```ts
    intro: 'Enter your email address and the name of your company. We will send you a link to finish.',
    introPrivate: 'Enter your email address. We will send you a link to finish.',
    kindLegend: 'Create an account for',
    kindOrganization: 'Organization',
    kindPrivate: 'Private person',
    companyName: 'Company name',
```

and `    invalidRequest: 'Check the email address and the company name.',` (in `signup`) with

```ts
    invalidRequest: 'Check the email address and the company name.',
    invalidRequestPrivate: 'Check the email address.',
```

(b) In `completeSignup` replace

```ts
    // The account exists and belongs to no company (its company was deleted, or the person was removed from it): only the company is new.
    keepsPassword: (email: string): string => `You already have an account as ${email}. Its password stays as it is; only the company is new.`,
    submit: 'Create company',
```

with

```ts
    submit: 'Create company',
    submitPrivate: 'Create account',
```

and `    invalidRequest: 'Check the company name.',` with

```ts
    invalidRequest: 'Check the company name.',
    invalidRequestPrivate: 'Check the password.',
```

- [ ] **Step 6: `/signup`.** Replace the content of `samples/notes-web/src/app/pages/signup.ts` with

```ts
import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AccountApi, SignupKind } from '../account-api';
import { texts } from '../texts';
import { requiredText } from './required-text';

@Component({
  selector: 'app-signup',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @if (closed()) {
        <p class="error" role="alert" data-testid="signup-closed">{{ t.closed }}</p>
      } @else {
        <p>{{ kind() === 'private' ? t.introPrivate : t.intro }}</p>
        <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
          @if (choice()) {
            <fieldset data-testid="signup-kind">
              <legend>{{ t.kindLegend }}</legend>
              <label>
                <input
                  type="radio"
                  name="kind"
                  value="organization"
                  data-testid="kind-organization"
                  [checked]="kind() === 'organization'"
                  (change)="choose('organization')"
                />
                {{ t.kindOrganization }}
              </label>
              <label>
                <input
                  type="radio"
                  name="kind"
                  value="private"
                  data-testid="kind-private"
                  [checked]="kind() === 'private'"
                  (change)="choose('private')"
                />
                {{ t.kindPrivate }}
              </label>
            </fieldset>
          }
          <label for="email">{{ common.email }}</label>
          <input id="email" type="email" formControlName="email" autocomplete="username" autocapitalize="none" />
          @if (kind() === 'organization') {
            <label for="company">{{ t.companyName }}</label>
            <input id="company" type="text" formControlName="company" maxlength="100" autocomplete="organization" />
          }
          <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
        </form>
        @if (sent()) {
          <p class="status" role="status" data-testid="signup-sent">{{ t.sent }}</p>
        }
        @if (error(); as text) {
          <p class="error" role="alert">{{ text }}</p>
        }
      }
      <p><a routerLink="/login">{{ common.backToSignIn }}</a></p>
    </main>
  `,
})
export class SignupPage implements OnInit {
  protected readonly t = texts.signup;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);

  // The password is not asked here: it is chosen at the link of the mail, so that only the reader of the mailbox ever sets it.
  protected readonly form = inject(NonNullableFormBuilder).group({ email: ['', requiredText], company: ['', requiredText] });
  protected readonly busy = signal(false);
  protected readonly sent = signal(false);
  protected readonly closed = signal(false);
  protected readonly error = signal<string | null>(null);
  /** An organization until the person, or the options, say otherwise. */
  protected readonly kind = signal<SignupKind>('organization');
  /** "Organization / Private person" is offered only while GET /auth/options says both are on (spec 0010). */
  protected readonly choice = signal(false);

  ngOnInit(): void {
    // With one kind on, the screen is that one; the form does not wait for the answer (a failed call leaves the organization form, whose
    // request is then answered by Auth-Core itself).
    void this.account.options().then((options) => {
      const organization = options.signup === 'self-service';
      const privatePerson = options.private_signup === 'self-service';
      this.choice.set(organization && privatePerson);
      if (privatePerson && !organization) {
        this.choose('private');
      }
    });
  }

  protected choose(kind: SignupKind): void {
    this.kind.set(kind);
    // A private person names no company: the field leaves the form and does not hold it invalid.
    if (kind === 'private') {
      this.form.controls.company.disable();
    } else {
      this.form.controls.company.enable();
    }
    this.error.set(null);
  }

  protected async submit(): Promise<void> {
    if (this.busy() || this.form.invalid) {
      return;
    }
    this.busy.set(true);
    this.sent.set(false);
    this.error.set(null);
    const { email, company } = this.form.getRawValue();
    const isPrivate = this.kind() === 'private';
    // Trimmed: Auth-Core refuses a name with white space at either end.
    const result = await this.account.signUp(email.trim(), isPrivate ? null : company.trim());
    this.busy.set(false);
    if (result.ok) {
      this.sent.set(true);
    } else if (result.failure.kind === 'too_many_attempts') {
      this.error.set(this.common.waitSeconds(result.failure.retryAfterSeconds));
    } else if (result.failure.kind === 'signup_disabled') {
      this.closed.set(true);
    } else if (result.failure.kind === 'invalid_request') {
      this.error.set(isPrivate ? this.t.invalidRequestPrivate : this.t.invalidRequest);
    } else if (result.failure.kind === 'too_many_requests') {
      // The notice bar above the screen says when to try again: the interceptor put it there.
    } else {
      this.error.set(this.common.somethingWrong);
    }
  }
}
```

- [ ] **Step 7: `/complete-signup`.** Replace the content of `samples/notes-web/src/app/pages/complete-signup.ts` with

```ts
import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AccountApi, SignupPreview } from '../account-api';
import { DEFAULT_RETURN_URL } from '../auth/auth.guard';
import { AuthService } from '../auth/auth.service';
import { texts } from '../texts';
import { PasswordRules } from './password-rules';
import { requiredText } from './required-text';
import { takeTokenFromUrl } from './token-from-url';

@Component({
  selector: 'app-complete-signup',
  imports: [ReactiveFormsModule, RouterLink, PasswordRules],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @switch (phase()) {
        @case ('loading') {
          <p class="status" role="status">{{ t.loading }}</p>
        }
        @case ('form') {
          @if (pending(); as signup) {
            <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
              <label for="email">{{ common.email }}</label>
              <input id="email" type="email" [value]="signup.email" readonly autocomplete="username" />
              @if (signup.kind === 'organization') {
                <label for="company">{{ shared.companyName }}</label>
                <input id="company" type="text" formControlName="company" maxlength="100" autocomplete="organization" />
              }
              <p class="status">{{ t.setsPassword(signup.email) }}</p>
              <label for="password">{{ common.newPassword }}</label>
              <input id="password" type="password" formControlName="password" autocomplete="new-password" />
              <label for="repeat">{{ common.repeatPassword }}</label>
              <input id="repeat" type="password" formControlName="repeat" autocomplete="new-password" />
              <button type="submit" [disabled]="busy()">{{ signup.kind === 'private' ? t.submitPrivate : t.submit }}</button>
            </form>
          }
          <app-password-rules [rules]="rules()" />
          @if (error(); as text) {
            <p class="error" role="alert">{{ text }}</p>
          }
        }
        @case ('invalid') {
          <p class="error" role="alert" data-testid="signup-invalid">{{ common.invalidLink }}</p>
          <p><a routerLink="/signup" data-testid="signup-again">{{ t.signUpAgain }}</a></p>
        }
        @case ('registered') {
          <p class="error" role="alert" data-testid="signup-registered">{{ t.alreadyRegistered }}</p>
          <p><a routerLink="/login">{{ common.signInLink }}</a></p>
        }
        @case ('closed') {
          <p class="error" role="alert" data-testid="signup-closed">{{ shared.closed }}</p>
          <p><a routerLink="/login">{{ common.backToSignIn }}</a></p>
        }
        @case ('limited') {
          <button type="button" data-testid="try-again" (click)="retry()">{{ common.tryAgain }}</button>
        }
        @case ('error') {
          <p class="error" role="alert">{{ common.somethingWrong }}</p>
          <button type="button" data-testid="try-again" (click)="retry()">{{ common.tryAgain }}</button>
        }
      }
    </main>
  `,
})
export class CompleteSignupPage implements OnInit {
  protected readonly t = texts.completeSignup;
  /** The words this screen shares with the sign-up form: the label of the company name and the sentence for a closed sign-up. */
  protected readonly shared = texts.signup;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  // Read once, here, and gone from the address bar from now on.
  private readonly token = takeTokenFromUrl(inject(ActivatedRoute), inject(Router));

  protected readonly form = inject(NonNullableFormBuilder).group({
    company: ['', requiredText],
    password: ['', Validators.required],
    repeat: ['', Validators.required],
  });
  protected readonly phase = signal<'loading' | 'form' | 'invalid' | 'registered' | 'closed' | 'limited' | 'error'>(
    this.token === null ? 'invalid' : 'loading',
  );
  protected readonly pending = signal<SignupPreview | null>(null);
  protected readonly busy = signal(false);
  protected readonly rules = signal<string[]>([]);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    if (this.token !== null) {
      void this.preview(this.token);
    }
  }

  /** The token is gone from the address bar but still held here: ask again with it. */
  protected retry(): void {
    if (this.token !== null) {
      this.phase.set('loading');
      void this.preview(this.token);
    }
  }

  protected async submit(): Promise<void> {
    const signup = this.pending();
    if (this.busy() || this.token === null || signup === null) {
      return;
    }
    const { company, password, repeat } = this.form.getRawValue();
    const isPrivate = signup.kind === 'private';
    this.rules.set([]);
    this.error.set(null);
    if (!isPrivate && company.trim() === '') {
      this.error.set(this.t.companyRequired);
      return;
    }
    if (this.form.invalid) {
      return;
    }
    if (password !== repeat) {
      this.error.set(this.common.passwordsDiffer);
      return;
    }
    this.busy.set(true);
    // The company name typed here is the one that counts (the name of the first request was only a suggestion); trimmed, because
    // Auth-Core refuses white space at either end. A private person names no company (spec 0010).
    const result = await this.auth.completeSignup(this.token, password, isPrivate ? null : company.trim());
    if (result.ok) {
      // Signed in (Auth-Core answered as it answers a login): the product opens at once, in place of this screen, so that Back does
      // not land on a link that is used up. The address is in no URL. The button stays off until the notes screen is open: the token
      // is used up, and a second Enter would only post it again. When the router cannot open it (it answers false or fails) the person
      // stays here, signed in, and the button is on again.
      try {
        await this.router.navigateByUrl(DEFAULT_RETURN_URL, { replaceUrl: true });
      } catch {
        // nothing to show: the account was made and the person is signed in
      }
    } else if (result.failure.kind === 'weak_password') {
      this.rules.set(result.failure.rules);
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else if (result.failure.kind === 'already_registered') {
      this.phase.set('registered');
    } else if (result.failure.kind === 'signup_disabled') {
      this.phase.set('closed');
    } else if (result.failure.kind === 'invalid_request') {
      this.error.set(isPrivate ? this.t.invalidRequestPrivate : this.t.invalidRequest);
    } else if (result.failure.kind === 'too_many_requests') {
      // The notice bar above the screen says when to try again: the interceptor put it there.
    } else {
      this.error.set(this.common.somethingWrong);
    }
    this.busy.set(false);
  }

  private async preview(token: string): Promise<void> {
    const result = await this.account.previewSignup(token);
    if (result.ok) {
      this.pending.set(result.value);
      if (result.value.kind === 'private') {
        // A private person names no company: the control must not hold the form invalid.
        this.form.controls.company.disable();
      } else {
        this.form.controls.company.enable();
        this.form.controls.company.setValue(result.value.company_name ?? '');
      }
      this.phase.set('form');
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else if (result.failure.kind === 'signup_disabled') {
      this.phase.set('closed');
    } else if (result.failure.kind === 'too_many_requests') {
      // The notice bar above the screen says when to try again: the interceptor put it there. Only the button is shown.
      this.phase.set('limited');
    } else {
      this.phase.set('error');
    }
  }
}
```

- [ ] **Step 8: The link on `/login`.** In `samples/notes-web/src/app/pages/login.ts` replace

```ts
  /** True while GET /auth/options says a visitor may start a company: then the screen offers "Create an account". */
  protected readonly signupOpen = signal(false);

  ngOnInit(): void {
    void this.account.options().then((options) => this.signupOpen.set(options.signup === 'self-service'));
  }
```

with

```ts
  /** True while GET /auth/options says a visitor may sign up, either kind (spec 0010): then the screen offers "Create an account". */
  protected readonly signupOpen = signal(false);

  ngOnInit(): void {
    void this.account
      .options()
      .then((options) => this.signupOpen.set(options.signup === 'self-service' || options.private_signup === 'self-service'));
  }
```

- [ ] **Step 9: Run the tests.** In `samples/notes-web`: `npm test` passes (all specs), then `npx ng build` and `npm run check:docs` pass.

- [ ] **Step 10: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files (the sample's texts are printable
ASCII). Proposed subject: `feat(sample): the sign-up choice and the private completion`.

### Task 16: The sample: the account bar, "Delete account", the invitation warning, `/team`

**Files:**
- Create: `samples/notes-web/src/app/pages/team.ts`
- Modify: `samples/notes-web/src/app/auth/auth.service.ts`, `samples/notes-web/src/app/auth/auth.interceptor.ts`, `samples/notes-web/src/app/account-api.ts`, `samples/notes-web/src/app/texts.ts`, `samples/notes-web/src/app/app.routes.ts`, `samples/notes-web/src/app/pages/notes.ts`, `samples/notes-web/src/app/pages/login.ts`, `samples/notes-web/src/app/pages/invite.ts`
- Modify (tests): `samples/notes-web/src/app/account-api.spec.ts`, `samples/notes-web/src/app/app.routes.spec.ts`
- Create (tests): `samples/notes-web/src/app/pages/notes.account.spec.ts`, `samples/notes-web/src/app/pages/invite.private.spec.ts`, `samples/notes-web/src/app/pages/team.spec.ts`, `samples/notes-web/src/app/pages/login.deleted.spec.ts`, `samples/notes-web/src/app/auth/auth.service.account.spec.ts`

**Interfaces:**
- Consumes: `DELETE /api/me` (Task 14), `DELETE /auth/account` (Task 13), the preview's `has_private_account` and `leave_private_account` (Task 12), `GET /auth/org/members` and `DELETE /auth/org/members/{id}` (spec 0005), `Me.org_kind` (Task 15).
- Produces: `Failure` gains `wrong_password` and `private_account_exists`; `AuthService.deleteAccount(password)`; `TOKEN_PATHS` gains `/auth/account`; `InvitePreview.has_private_account`; `AccountApi.acceptInvite(token, password: string | null, leavePrivateAccount = false)`; the route `/team` (guarded) and `TeamPage`; the texts of the account bar, the deletion, the invitation warning and the team screen.

**Contract counts:** none. `account-api.spec.ts`'s preview of an invitation reads `has_private_account: false`.

**Decisions the plan takes here:**
- The delete form is a section of the notes screen that appears after "Delete account" (no separate route): the warning, the password and "Delete my account", with "Cancel". It is placed after the notes, so the notes form stays the first form of the page.
- A wrong password after the notes are gone says both (spec, Accepted 17): "The password is wrong, so your account was not deleted. Your notes are already gone."
- The team screen's confirmation is inline (the row shows the sentence of the spec, "Remove" and "Cancel"); a `403` that the interceptor could not resolve reloads the person and the list.
- An acceptance that meets `409 private_account_exists` (the account became private after the preview) asks the preview again, and the screen shows the warning.

- [ ] **Step 1: Write the failing tests.**

`samples/notes-web/src/app/auth/auth.service.account.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { privateMe, signedInAs } from '../../testing/helpers';
import { wantsToken } from './auth.interceptor';
import { AuthService } from './auth.service';

const status = (code: number) => ({ status: code, statusText: String(code) });

describe('AuthService.deleteAccount (spec 0010)', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
  });

  it('sends DELETE /auth/account with the password and ends the session on 204', async () => {
    await signedInAs(auth, ctrl, privateMe);
    const done = auth.deleteAccount('Pw-1 x');
    const request = ctrl.expectOne('/auth/account');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.body).toEqual({ password: 'Pw-1 x' });
    request.flush(null, status(204));
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBeNull();
    expect(auth.me()).toBeNull();
  });

  it.each([
    [403, { error: 'wrong_password' }, { kind: 'wrong_password' }],
    [429, { error: 'too_many_attempts', retry_after_seconds: 90 }, { kind: 'too_many_attempts', retryAfterSeconds: 90 }],
    [403, { error: 'forbidden' }, { kind: 'other' }],
  ])('answer %s %j is the failure %j and the session stays', async (code, body, failure) => {
    await signedInAs(auth, ctrl, privateMe);
    const done = auth.deleteAccount('pw');
    ctrl.expectOne('/auth/account').flush(body, status(code));
    expect(await done).toEqual({ ok: false, failure });
    expect(auth.token()).not.toBeNull();
  });

  it('the interceptor gives the call the access token', () => {
    expect(wantsToken('/auth/account', 'https://app.example')).toBe(true);
  });
});
```

`samples/notes-web/src/app/pages/notes.account.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService, Me } from '../auth/auth.service';
import { adminMe, clickOn, has, pageText, privateMe, settle, signedInAs, submitForm, typeInto, viewerMe } from '../../testing/helpers';
import { NotesPage } from './notes';

const status = (code: number) => ({ status: code, statusText: String(code) });
const warning = 'This deletes your account and all your notes. It cannot be undone.';

async function open(me: Me) {
  TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(NotesPage);
  fixture.detectChanges();
  ctrl.expectOne('/api/notes').flush([]);
  await settle(fixture);
  return { fixture, ctrl, auth, navigateByUrl };
}

type Opened = Awaited<ReturnType<typeof open>>;

async function askToDelete(opened: Opened, password: string): Promise<void> {
  clickOn(opened.fixture, '[data-testid="delete-account"]');
  opened.fixture.detectChanges();
  typeInto(opened.fixture, '#delete-password', password);
  submitForm(opened.fixture, 'form[data-testid="delete-form"]');
  await settle(opened.fixture);
}

describe('NotesPage: the account bar (spec 0010)', () => {
  it('a private account sees its address, "Private account" and "Delete account", and no company, role or Team', async () => {
    const { fixture } = await open(privateMe);
    expect(pageText(fixture)).toContain('ola@poczta.example');
    expect(pageText(fixture)).toContain('Private account');
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(true);
    expect(has(fixture, '[data-testid="me-company"]')).toBe(false);
    expect(has(fixture, '[data-testid="me-role"]')).toBe(false);
    expect(has(fixture, '[data-testid="team-link"]')).toBe(false);
  });

  it('a company member who manages members sees "Team", and no "Delete account"', async () => {
    const { fixture } = await open({ ...adminMe, permissions: [...adminMe.permissions, 'members:manage'] });
    expect(has(fixture, 'a[href="/team"][data-testid="team-link"]')).toBe(true);
    expect(has(fixture, '[data-testid="me-company"]')).toBe(true);
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(false);
  });

  it('a company member without members:manage sees no "Team"', async () => {
    const { fixture } = await open(viewerMe);
    expect(has(fixture, '[data-testid="team-link"]')).toBe(false);
  });
});

describe('NotesPage: "Delete account" (spec 0010)', () => {
  it('shows the warning, the password and "Delete my account"', async () => {
    const opened = await open(privateMe);
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    expect(pageText(opened.fixture)).toContain(warning);
    expect(has(opened.fixture, '#delete-password')).toBe(true);
    expect(pageText(opened.fixture)).toContain('Delete my account');
  });

  it('deletes the notes first and only after their 204 the account, then opens /login with the notice', async () => {
    const opened = await open(privateMe);
    await askToDelete(opened, 'Chosen-Passw0rd');
    const data = opened.ctrl.expectOne('/api/me');
    expect(data.request.method).toBe('DELETE');
    opened.ctrl.expectNone('/auth/account'); // not before the product's data is gone
    data.flush(null, status(204));
    await settle(opened.fixture);
    const account = opened.ctrl.expectOne('/auth/account');
    expect(account.request.method).toBe('DELETE');
    expect(account.request.body).toEqual({ password: 'Chosen-Passw0rd' });
    account.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.auth.token()).toBeNull();
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/login', { state: { notice: 'deleted' } });
  });

  it('a failure of the notes service stops there: Auth-Core is not called', async () => {
    const opened = await open(privateMe);
    await askToDelete(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/api/me').flush({ error: 'database_unavailable' }, status(503));
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/account');
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(opened.auth.token()).not.toBeNull();
  });

  it('wrong_password: says that the password is wrong and that the notes are already gone', async () => {
    const opened = await open(privateMe);
    await askToDelete(opened, 'Wrong-Passw0rd');
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'wrong_password' }, status(403));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('The password is wrong, so your account was not deleted. Your notes are already gone.');
    expect(opened.navigateByUrl).not.toHaveBeenCalled();
  });

  it('429 too_many_attempts: shows the wait', async () => {
    const opened = await open(privateMe);
    await askToDelete(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'too_many_attempts', retry_after_seconds: 120 }, status(429));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Too many attempts. Try again in 2 minutes.');
  });

  it('an empty password sends nothing', async () => {
    const opened = await open(privateMe);
    await askToDelete(opened, '');
    opened.ctrl.expectNone('/api/me');
  });
});
```

`samples/notes-web/src/app/pages/login.deleted.spec.ts`:

```ts
import { Component } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText } from '../../testing/helpers';
import { LoginPage } from './login';

@Component({ template: '' })
class Blank {}

describe('LoginPage: after a deleted account (spec 0010)', () => {
  async function arrive(state?: object) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'start', component: Blank },
          { path: 'login', component: LoginPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    const harness = await RouterTestingHarness.create('/start');
    await TestBed.inject(Router).navigateByUrl('/login', state === undefined ? {} : { state });
    harness.detectChanges();
    return harness.fixture;
  }

  it('says "Your account was deleted." when the notes screen sent the person here after the deletion', async () => {
    const fixture = await arrive({ notice: 'deleted' });
    expect(pageText(fixture)).toContain('Your account was deleted.');
    expect(has(fixture, '[data-testid="login-notice"]')).toBe(true);
  });

  it('says nothing of the kind otherwise', async () => {
    const fixture = await arrive();
    expect(has(fixture, '[data-testid="login-notice"]')).toBe(false);
  });
});
```

`samples/notes-web/src/app/pages/invite.private.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { clickOn, has, pageText, realLocation, settle, submitForm, typeInto } from '../../testing/helpers';
import { InvitePage } from './invite';

const status = (code: number) => ({ status: code, statusText: String(code) });
const preview = { org_name: 'Acme', email: 'ola@poczta.example', role: 'user', has_private_account: true };
const warning =
  'You already have a private account with this email address. If you accept, you move to Acme: the space of your private account is ' +
  'deleted, with everything in it. Your account itself stays, with its email address and its password, and becomes a member of Acme.';

async function open(answer: object = preview) {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'invite', component: InvitePage }]), provideHttpClient(), provideHttpClientTesting(), realLocation],
  });
  history.replaceState(null, '', '/invite?token=tok-i');
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/invite?token=tok-i', InvitePage);
  await settle();
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne('/auth/invites/preview').flush(answer);
  await settle(harness.fixture);
  return { fixture: harness.fixture, ctrl, navigate };
}

describe('InvitePage for an address with a private account (spec 0010)', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('shows the warning in red and one button, and no password field', async () => {
    const { fixture } = await open();
    expect(pageText(fixture)).toContain(warning);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="invite-private-warning"]')?.className).toContain('error');
    expect(pageText(fixture)).toContain('Move me to the company and delete my private account');
    expect(has(fixture, '#password')).toBe(false);
    expect(has(fixture, '#repeat')).toBe(false);
  });

  it('the button sends leave_private_account true and no password, then opens the sign-in with the email', async () => {
    const opened = await open();
    clickOn(opened.fixture, '[data-testid="move"]');
    const request = opened.ctrl.expectOne('/auth/invites/accept');
    expect(request.request.body).toEqual({ token: 'tok-i', leave_private_account: true });
    request.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigate).toHaveBeenCalledWith(['/login'], { state: { email: 'ola@poczta.example' } });
  });

  it('without a private account the screen is unchanged', async () => {
    const { fixture } = await open({ ...preview, has_private_account: false });
    expect(pageText(fixture)).not.toContain('private account');
    expect(has(fixture, '#password')).toBe(true);
  });

  it('409 private_account_exists on an acceptance asks the preview again and shows the warning', async () => {
    const opened = await open({ ...preview, has_private_account: false });
    typeInto(opened.fixture, '#password', 'Joined-Passw0rd');
    typeInto(opened.fixture, '#repeat', 'Joined-Passw0rd');
    submitForm(opened.fixture);
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'private_account_exists' }, status(409));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/invites/preview').flush(preview);
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(warning);
  });
});
```

`samples/notes-web/src/app/pages/team.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService, Me } from '../auth/auth.service';
import { adminMe, clickOn, has, pageText, settle, signedInAs, viewerMe } from '../../testing/helpers';
import { TeamPage } from './team';

const status = (code: number) => ({ status: code, statusText: String(code) });
const manager: Me = { ...adminMe, permissions: [...adminMe.permissions, 'members:manage'] };
const members = {
  members: [
    { user_id: adminMe.sub, email: adminMe.email, role: { id: 'r1', name: 'admin' }, joined_at: '2026-04-01T10:00:00Z' },
    { user_id: '55555555-5555-5555-5555-555555555555', email: 'worker@example.test', role: { id: 'r2', name: 'user' }, joined_at: '2026-04-02T10:00:00Z' },
  ],
};

async function open(me: Me = manager) {
  TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
  const ctrl = TestBed.inject(HttpTestingController);
  await signedInAs(TestBed.inject(AuthService), ctrl, me);
  const fixture = TestBed.createComponent(TeamPage);
  fixture.detectChanges();
  return { fixture, ctrl };
}

async function openWithMembers() {
  const opened = await open();
  opened.ctrl.expectOne('/auth/org/members').flush(members);
  await settle(opened.fixture);
  return opened;
}

describe('TeamPage (spec 0010)', () => {
  it('lists the members with their roles, and "Remove" for each other member only', async () => {
    const { fixture } = await openWithMembers();
    expect(pageText(fixture)).toContain('admin@example.test');
    expect(pageText(fixture)).toContain('worker@example.test');
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="remove"]').length).toBe(1);
  });

  it('"Remove" asks first, with the sentence that the account will be deleted', async () => {
    const { fixture, ctrl } = await openWithMembers();
    clickOn(fixture, '[data-testid="remove"]');
    fixture.detectChanges();
    expect(pageText(fixture)).toContain("This person's account will be deleted.");
    ctrl.expectNone('/auth/org/members/55555555-5555-5555-5555-555555555555');
  });

  it('the confirmation removes the member, who leaves the list', async () => {
    const { fixture, ctrl } = await openWithMembers();
    clickOn(fixture, '[data-testid="remove"]');
    fixture.detectChanges();
    clickOn(fixture, '[data-testid="remove-confirm"]');
    const request = ctrl.expectOne('/auth/org/members/55555555-5555-5555-5555-555555555555');
    expect(request.request.method).toBe('DELETE');
    request.flush(null, status(204));
    await settle(fixture);
    expect(pageText(fixture)).not.toContain('worker@example.test');
  });

  it('"Cancel" leaves everything as it was', async () => {
    const { fixture, ctrl } = await openWithMembers();
    clickOn(fixture, '[data-testid="remove"]');
    fixture.detectChanges();
    clickOn(fixture, '[data-testid="remove-cancel"]');
    fixture.detectChanges();
    expect(pageText(fixture)).not.toContain("This person's account will be deleted.");
    ctrl.expectNone('/auth/org/members/55555555-5555-5555-5555-555555555555');
  });

  it.each([
    ['permission_not_held', 'You cannot remove someone who holds more permissions than you.'],
    ['last_manager', 'The company needs at least one person who manages its members.'],
    ['cannot_change_self', 'You cannot remove yourself.'],
  ])('%s: one sentence, and the member stays', async (code, sentence) => {
    const { fixture, ctrl } = await openWithMembers();
    clickOn(fixture, '[data-testid="remove"]');
    fixture.detectChanges();
    clickOn(fixture, '[data-testid="remove-confirm"]');
    ctrl.expectOne('/auth/org/members/55555555-5555-5555-5555-555555555555').flush({ error: code }, status(code === 'permission_not_held' ? 403 : 409));
    await settle(fixture);
    expect(pageText(fixture)).toContain(sentence);
    expect(pageText(fixture)).toContain('worker@example.test');
  });

  it('a 403 that is left after the refresh reloads the person and the list', async () => {
    const { fixture, ctrl } = await openWithMembers();
    clickOn(fixture, '[data-testid="remove"]');
    fixture.detectChanges();
    clickOn(fixture, '[data-testid="remove-confirm"]');
    ctrl.expectOne('/auth/org/members/55555555-5555-5555-5555-555555555555').flush({ error: 'forbidden' }, status(403));
    await settle(fixture);
    ctrl.expectOne('/auth/me').flush(viewerMe);
    await settle(fixture);
    expect(pageText(fixture)).toContain("You don't have access to this.");
  });

  it('a person without members:manage is told so and nothing is listed', async () => {
    const { fixture, ctrl } = await open(viewerMe);
    await settle(fixture);
    ctrl.expectNone('/auth/org/members');
    expect(pageText(fixture)).toContain("You don't have access to this.");
    expect(has(fixture, '[data-testid="member"]')).toBe(false);
  });
});
```

In `samples/notes-web/src/app/account-api.spec.ts` replace
`    expect(await done).toEqual({ ok: true, value: { org_name: 'Acme', email: 'a@b.example', role: 'viewer' } });` with
`    expect(await done).toEqual({ ok: true, value: { org_name: 'Acme', email: 'a@b.example', role: 'viewer', has_private_account: false } });`
(an answer without the member reads as no private account, which shows the screen of spec 0005).

In `samples/notes-web/src/app/app.routes.spec.ts`, after the test `'%s needs no token'` add

```ts

  it('an anonymous person who asks for /team lands on /login?returnUrl=%2Fteam', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/team');
    expect(TestBed.inject(Router).url).toBe('/login?returnUrl=%2Fteam');
  });
```

- [ ] **Step 2: See them fail.** `npm test` in `samples/notes-web`: the new specs fail (no `TeamPage`, no `deleteAccount`, no account bar).

- [ ] **Step 3: The service and the interceptor.** In `samples/notes-web/src/app/auth/auth.service.ts`:

(a) In `Failure`, after `  | { kind: 'signup_disabled' }` add

```ts
  | { kind: 'wrong_password' }
  | { kind: 'private_account_exists' }
```

(b) In `failureOf`, before `  return { kind: 'other' };` (the last line of the function) add

```ts
  if (error.status === 403 && code === 'wrong_password') {
    return { kind: 'wrong_password' };
  }
  if (error.status === 409 && code === 'private_account_exists') {
    return { kind: 'private_account_exists' };
  }
```

(c) After the method `logout()` add

```ts

  /**
   * Deletes the private account of the person who is signed in (spec 0010): DELETE /auth/account with the password. On `204` the session is
   * over here too (Auth-Core has cleared the cookie). The product deletes its own data before it calls this: Auth-Core does not tell it.
   */
  async deleteAccount(password: string): Promise<LoginResult> {
    try {
      await firstValueFrom(this.http.delete('/auth/account', { body: { password } }));
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
    this.dropSession();
    return { ok: true };
  }
```

In `samples/notes-web/src/app/auth/auth.interceptor.ts` replace
`export const TOKEN_PATHS: readonly string[] = ['/auth/me'];` with `export const TOKEN_PATHS: readonly string[] = ['/auth/me', '/auth/account'];`.

- [ ] **Step 4: The invitation calls.** In `samples/notes-web/src/app/account-api.ts`:

(a) Replace

```ts
/** What POST /auth/invites/preview answers: who invites, for whom, as what. */
export interface InvitePreview {
  org_name: string;
  email: string;
  role: string;
}
```

with

```ts
/** What POST /auth/invites/preview answers: who invites, for whom, as what, and whether the address has a private account (spec 0010). */
export interface InvitePreview {
  org_name: string;
  email: string;
  role: string;
  has_private_account: boolean;
}
```

(b) Replace `      return { ok: true, value: { org_name: body['org_name'], email: body['email'], role: body['role'] } };` with

```ts
      return {
        ok: true,
        // Anything but a true boolean means "no private account": the screen of spec 0005, which asks for a password.
        value: { org_name: body['org_name'], email: body['email'], role: body['role'], has_private_account: body['has_private_account'] === true },
      };
```

(c) Replace

```ts
  acceptInvite(token: string, password: string): Promise<Outcome> {
    return this.post('/auth/invites/accept', { token, password });
  }
```

with

```ts
  /** With `leavePrivateAccount` (spec 0010) the private account moves into the company on purpose, and no password is sent. */
  acceptInvite(token: string, password: string | null, leavePrivateAccount = false): Promise<Outcome> {
    return this.post('/auth/invites/accept', leavePrivateAccount ? { token, leave_private_account: true } : { token, password });
  }
```

- [ ] **Step 5: The texts.** In `samples/notes-web/src/app/texts.ts`:

(a) In `login`, after `    createAccount: 'Create an account',` add `    accountDeleted: 'Your account was deleted.',`.

(b) In `invite`, after `    alreadyMember: 'This account already belongs to a company.',` add

```ts
    // The paragraph of the invitation mail (spec 0010), for an address that has a private account.
    privateWarning: (company: string): string =>
      `You already have a private account with this email address. If you accept, you move to ${company}: the space of your private account ` +
      `is deleted, with everything in it. Your account itself stays, with its email address and its password, and becomes a member of ${company}.`,
    move: 'Move me to the company and delete my private account',
```

(c) Replace

```ts
  notes: {
    title: 'Notes',
    signOut: 'Sign out',
```

with

```ts
  notes: {
    title: 'Notes',
    signOut: 'Sign out',
    privateAccount: 'Private account',
    team: 'Team',
    deleteAccount: 'Delete account',
    deleteWarning: 'This deletes your account and all your notes. It cannot be undone.',
    deleteConfirm: 'Delete my account',
    cancel: 'Cancel',
    // The notes are deleted first (spec 0010): a wrong password then leaves the account without them, and the screen says so.
    wrongPassword: 'The password is wrong, so your account was not deleted. Your notes are already gone.',
```

(d) After the `notes` block (before the closing `};` of `texts`) add

```ts

  team: {
    title: 'Team',
    back: 'Back to the notes',
    remove: 'Remove',
    cancel: 'Cancel',
    // Spec 0010: removing a member deletes the account. A product's own team screen says the same.
    removeConfirm: "This person's account will be deleted.",
    permissionNotHeld: 'You cannot remove someone who holds more permissions than you.',
    lastManager: 'The company needs at least one person who manages its members.',
    cannotChangeSelf: 'You cannot remove yourself.',
    notAllowed: "You don't have access to this.",
  },
```

- [ ] **Step 6: The account bar and "Delete account".** In `samples/notes-web/src/app/pages/notes.ts`:

(a) Replace `import { Router } from '@angular/router';` with `import { Router, RouterLink } from '@angular/router';` and
`  imports: [ReactiveFormsModule, DatePipe],` with `  imports: [ReactiveFormsModule, DatePipe, RouterLink],`.

(b) Replace

```ts
        @if (me(); as person) {
          <p class="who">
            <strong data-testid="me-email">{{ person.email }}</strong>
            <span data-testid="me-company">{{ person.org_name }}</span>
            <span data-testid="me-role">{{ person.roles.join(', ') }}</span>
          </p>
        }
        <button type="button" data-testid="sign-out" [disabled]="signingOut()" (click)="signOut()">{{ t.signOut }}</button>
```

with

```ts
        @if (me(); as person) {
          <p class="who">
            <strong data-testid="me-email">{{ person.email }}</strong>
            @if (person.org_kind === 'personal') {
              <span data-testid="me-private">{{ t.privateAccount }}</span>
            } @else {
              <span data-testid="me-company">{{ person.org_name }}</span>
              <span data-testid="me-role">{{ person.roles.join(', ') }}</span>
              @if (canManageMembers()) {
                <a routerLink="/team" data-testid="team-link">{{ t.team }}</a>
              }
            }
          </p>
          @if (person.org_kind === 'personal') {
            <button type="button" data-testid="delete-account" (click)="askToDelete()">{{ t.deleteAccount }}</button>
          }
        }
        <button type="button" data-testid="sign-out" [disabled]="signingOut()" (click)="signOut()">{{ t.signOut }}</button>
```

(c) Replace

```ts
          </li>
        }
      </ul>
    </main>
```

with

```ts
          </li>
        }
      </ul>
      @if (deleting()) {
        <section data-testid="delete-section">
          <h2>{{ t.deleteAccount }}</h2>
          <p class="error" data-testid="delete-warning">{{ t.deleteWarning }}</p>
          <form [formGroup]="deleteForm" (ngSubmit)="deleteAccount()" novalidate data-testid="delete-form">
            <label for="delete-password">{{ password }}</label>
            <input id="delete-password" type="password" formControlName="password" autocomplete="current-password" />
            <button type="submit" data-testid="delete-confirm" [disabled]="deleteBusy()">{{ t.deleteConfirm }}</button>
            <button type="button" (click)="deleting.set(false)">{{ t.cancel }}</button>
          </form>
          @if (deleteError(); as text) {
            <p class="error" role="alert" data-testid="delete-error">{{ text }}</p>
          }
        </section>
      }
    </main>
```

(d) Replace

```ts
  protected readonly t = texts.notes;
  protected readonly somethingWrong = texts.common.somethingWrong;
```

with

```ts
  protected readonly t = texts.notes;
  protected readonly somethingWrong = texts.common.somethingWrong;
  protected readonly password = texts.common.password;
```

(e) Replace

```ts
  protected readonly canWrite = computed(() => this.me()?.permissions.includes('notes:write') ?? false);
```

with

```ts
  protected readonly canWrite = computed(() => this.me()?.permissions.includes('notes:write') ?? false);
  /** A company member who manages members gets the way to the team screen (spec 0010). */
  protected readonly canManageMembers = computed(() => this.me()?.permissions.includes('members:manage') ?? false);
  protected readonly deleteForm = inject(NonNullableFormBuilder).group({ password: ['', Validators.required] });
  protected readonly deleting = signal(false);
  protected readonly deleteBusy = signal(false);
  protected readonly deleteError = signal<string | null>(null);
```

(f) Before `  private async load(): Promise<void> {` add

```ts
  protected askToDelete(): void {
    this.deleteForm.reset();
    this.deleteError.set(null);
    this.deleting.set(true);
  }

  /**
   * Spec 0010, Decision 8: the product deletes its own data first (DELETE /api/me), and only after its 204 the account (DELETE /auth/account).
   * Auth-Core does not tell the product, so the other order could leave notes that nobody can reach.
   */
  protected async deleteAccount(): Promise<void> {
    const password = this.deleteForm.controls.password.value;
    if (this.deleteBusy() || password === '') {
      return;
    }
    this.deleteBusy.set(true);
    this.deleteError.set(null);
    try {
      await firstValueFrom(this.http.delete('/api/me'));
    } catch (error) {
      // auth_unavailable: the bar says it. Nothing was deleted: the person may try again.
      this.deleteError.set(isAuthUnavailable(error) ? null : this.somethingWrong);
      this.deleteBusy.set(false);
      return;
    }
    const result = await this.auth.deleteAccount(password);
    if (result.ok) {
      try {
        await this.router.navigateByUrl('/login', { state: { notice: 'deleted' } });
      } catch {
        // nothing to show: the account is gone and so is the session
      }
      return;
    }
    if (result.failure.kind === 'wrong_password') {
      this.deleteError.set(this.t.wrongPassword);
    } else if (result.failure.kind === 'too_many_attempts') {
      this.deleteError.set(texts.login.tooManyAttempts(result.failure.retryAfterSeconds));
    } else if (result.failure.kind === 'too_many_requests') {
      // The notice bar above the screen says when to try again: the interceptor put it there.
    } else {
      this.deleteError.set(this.somethingWrong);
    }
    this.deleteBusy.set(false);
  }

```

- [ ] **Step 7: The notice on `/login`.** In `samples/notes-web/src/app/pages/login.ts`:

(a) After the function `startEmail` add

```ts

/** The notice the notes screen passes on after a deleted account (spec 0010), in the navigation state. */
function startNotice(router: Router): string | null {
  return router.currentNavigation()?.extras.state?.['notice'] === 'deleted' ? texts.login.accountDeleted : null;
}
```

(b) Replace

```ts
      <h1>{{ t.title }}</h1>
      <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
```

with

```ts
      <h1>{{ t.title }}</h1>
      @if (notice(); as text) {
        <p class="status" role="status" data-testid="login-notice">{{ text }}</p>
      }
      <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
```

(c) After the line `  protected readonly resending = signal(false);` add `  protected readonly notice = signal<string | null>(startNotice(this.router));`.

- [ ] **Step 8: The invitation warning.** In `samples/notes-web/src/app/pages/invite.ts`:

(a) Replace

```ts
            <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
              <label for="email">{{ common.email }}</label>
              <input id="email" type="email" [value]="invitation.email" readonly autocomplete="username" />
              <p class="status">{{ t.setsPassword(invitation.email) }}</p>
              <label for="password">{{ common.newPassword }}</label>
              <input id="password" type="password" formControlName="password" autocomplete="new-password" />
              <label for="repeat">{{ common.repeatPassword }}</label>
              <input id="repeat" type="password" formControlName="repeat" autocomplete="new-password" />
              <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
            </form>
```

with

```ts
            @if (invitation.has_private_account) {
              <!-- Spec 0010: the address has a private account. Accepting moves it and deletes its space; it keeps its password. -->
              <p class="error" role="alert" data-testid="invite-private-warning">{{ t.privateWarning(invitation.org_name) }}</p>
              <label for="email">{{ common.email }}</label>
              <input id="email" type="email" [value]="invitation.email" readonly autocomplete="username" />
              <button type="button" data-testid="move" [disabled]="busy()" (click)="move()">{{ t.move }}</button>
            } @else {
              <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
                <label for="email">{{ common.email }}</label>
                <input id="email" type="email" [value]="invitation.email" readonly autocomplete="username" />
                <p class="status">{{ t.setsPassword(invitation.email) }}</p>
                <label for="password">{{ common.newPassword }}</label>
                <input id="password" type="password" formControlName="password" autocomplete="new-password" />
                <label for="repeat">{{ common.repeatPassword }}</label>
                <input id="repeat" type="password" formControlName="repeat" autocomplete="new-password" />
                <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
              </form>
            }
```

(b) Replace

```ts
    this.busy.set(true);
    const result = await this.account.acceptInvite(this.token, password);
    if (result.ok) {
```

with

```ts
    this.busy.set(true);
    await this.answer(await this.account.acceptInvite(this.token, password), invitation);
    this.busy.set(false);
  }

  /** The one button of an address with a private account: it moves on purpose, with no password (spec 0010). */
  protected async move(): Promise<void> {
    const invitation = this.invite();
    if (this.busy() || this.token === null || invitation === null) {
      return;
    }
    this.error.set(null);
    this.busy.set(true);
    await this.answer(await this.account.acceptInvite(this.token, null, true), invitation);
    this.busy.set(false);
  }

  private async answer(result: Outcome, invitation: InvitePreview): Promise<void> {
    if (result.ok) {
```

and replace (the end of the old `submit`)

```ts
    } else if (result.failure.kind === 'already_member') {
      this.phase.set('member');
    } else if (result.failure.kind === 'too_many_requests') {
      // The notice bar above the screen says when to try again: the interceptor put it there.
    } else {
      this.error.set(this.common.somethingWrong);
    }
    this.busy.set(false);
  }
```

with

```ts
    } else if (result.failure.kind === 'already_member') {
      this.phase.set('member');
    } else if (result.failure.kind === 'private_account_exists' && this.token !== null) {
      // The address has a private account by now: ask again, and the screen shows the warning and its button.
      await this.preview(this.token);
    } else if (result.failure.kind === 'too_many_requests') {
      // The notice bar above the screen says when to try again: the interceptor put it there.
    } else {
      this.error.set(this.common.somethingWrong);
    }
  }
```

and replace `import { AccountApi, InvitePreview } from '../account-api';` with `import { AccountApi, InvitePreview, Outcome } from '../account-api';`.

- [ ] **Step 9: `/team`.** `samples/notes-web/src/app/pages/team.ts`:

```ts
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService, errorCode, isRecord } from '../auth/auth.service';
import { texts } from '../texts';

interface Member {
  user_id: string;
  email: string;
  role: { id: string; name: string };
}

function isMember(value: unknown): value is Member {
  return (
    isRecord(value) &&
    typeof value['user_id'] === 'string' &&
    typeof value['email'] === 'string' &&
    isRecord(value['role']) &&
    typeof value['role']['name'] === 'string'
  );
}

/**
 * The company's members, for a member who holds members:manage (spec 0010, Settled 14): the address and the role of each, and "Remove" for
 * every other member, behind a confirmation that says what removing does: the person's account is deleted.
 */
@Component({
  selector: 'app-team',
  imports: [RouterLink],
  template: `
    <main class="wide">
      <h1>{{ t.title }}</h1>
      @if (!canManage()) {
        <p class="error" role="alert" data-testid="team-forbidden">{{ t.notAllowed }}</p>
      } @else {
        @if (loadFailed()) {
          <p class="error" role="alert">{{ somethingWrong }}</p>
        }
        <ul class="plain" data-testid="members">
          @for (member of members(); track member.user_id) {
            <li data-testid="member">
              <strong data-testid="member-email">{{ member.email }}</strong>
              <span data-testid="member-role">{{ member.role.name }}</span>
              @if (member.user_id !== me()?.sub) {
                @if (confirming() === member.user_id) {
                  <span class="error" data-testid="remove-confirmation">{{ t.removeConfirm }}</span>
                  <button type="button" data-testid="remove-confirm" [disabled]="busy()" (click)="remove(member)">{{ t.remove }}</button>
                  <button type="button" data-testid="remove-cancel" (click)="confirming.set(null)">{{ t.cancel }}</button>
                } @else {
                  <button type="button" data-testid="remove" (click)="ask(member)">{{ t.remove }}</button>
                }
              }
            </li>
          }
        </ul>
        @if (error(); as text) {
          <p class="error" role="alert" data-testid="team-error">{{ text }}</p>
        }
      }
      <p><a routerLink="/notes">{{ t.back }}</a></p>
    </main>
  `,
})
export class TeamPage implements OnInit {
  protected readonly t = texts.team;
  protected readonly somethingWrong = texts.common.somethingWrong;
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);

  protected readonly me = this.auth.me;
  protected readonly canManage = computed(() => this.me()?.permissions.includes('members:manage') ?? false);
  protected readonly members = signal<Member[]>([]);
  protected readonly loadFailed = signal(false);
  protected readonly confirming = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  async ngOnInit(): Promise<void> {
    if (this.me() === null) {
      await this.auth.loadMe();
    }
    if (this.canManage()) {
      await this.load();
    }
  }

  protected ask(member: Member): void {
    this.error.set(null);
    this.confirming.set(member.user_id);
  }

  protected async remove(member: Member): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      await firstValueFrom(this.http.delete(`/auth/org/members/${encodeURIComponent(member.user_id)}`));
      this.members.update((list) => list.filter((m) => m.user_id !== member.user_id));
    } catch (error) {
      const code = error instanceof HttpErrorResponse ? errorCode(error) : undefined;
      if (code === 'permission_not_held') {
        this.error.set(this.t.permissionNotHeld);
      } else if (code === 'last_manager') {
        this.error.set(this.t.lastManager);
      } else if (code === 'cannot_change_self') {
        this.error.set(this.t.cannotChangeSelf);
      } else if (code === 'permissions_changed' || code === 'forbidden') {
        // The interceptor has refreshed and tried again; what is left: the person's rights changed. Read them, and the list, again.
        await this.auth.loadMe();
        if (this.canManage()) {
          await this.load();
        }
      } else {
        this.error.set(this.somethingWrong);
      }
    } finally {
      this.confirming.set(null);
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      const body = await firstValueFrom(this.http.get<unknown>('/auth/org/members'));
      const list = isRecord(body) && Array.isArray(body['members']) ? body['members'].filter(isMember) : null;
      if (list === null) {
        throw new Error('not a member list');
      }
      this.members.set(list);
      this.loadFailed.set(false);
    } catch {
      this.members.set([]);
      this.loadFailed.set(true);
    }
  }
}
```

In `samples/notes-web/src/app/app.routes.ts` add `import { TeamPage } from './pages/team';` after the import of `SignupPage`, and before
`  { path: '**', redirectTo: toNotes },` add

```ts
  { path: 'team', component: TeamPage, canActivate: [authGuard], title: texts.team.title },
```

- [ ] **Step 10: Run the tests.** In `samples/notes-web`: `npm test` passes (all specs), then `npx ng build` and `npm run check:docs` pass.

- [ ] **Step 11: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(sample): the account bar, Delete account, the invitation warning and the team screen`.

---

## Day 7 — Friday 17.04: the live checks and the documents

When the day is done, the live checks of the slice exist (the verifiers run them), the earlier checks follow spec 0010, and every document
says what the slice built.

### Task 17: `scripts/e2e-private.sh`, the Playwright spec, the earlier e2e checks

**Files:**
- Create: `scripts/e2e-private.sh`, `samples/notes-web/e2e/10-private.spec.ts`, `samples/notes-web/e2e/support/accounts.ts`
- Modify: `scripts/e2e-signup.sh`, `scripts/e2e-tenancy.sh`, `scripts/e2e-hardening.sh`, `scripts/e2e-web.sh`, `samples/notes-web/e2e/09-signup.spec.ts`

**Interfaces:**
- Consumes: everything the earlier tasks built; `deploy/auth.yaml` and `samples/notes-api/auth.yaml` with private sign-up on (Task 14).
- Produces: the live checks of criteria 34 and 35.

**Contract counts:** none. The live checks that pinned the old behaviour change to this spec (criterion 35): `e2e-signup.sh` steps 1, 5 and 12, `e2e-tenancy.sh` steps 9 and 10, `e2e-hardening.sh` steps 5 and 6, `e2e-web.sh` (the COMEBACK seed goes), `09-signup.spec.ts` (the comeback test goes).

**Decisions the plan takes here:**
- `scripts/e2e-private.sh` runs on the stack of the other scripts, after `e2e-signup.sh` (limiter off), and copies the helpers of `e2e-signup.sh` (scripts here are self-contained).
- The Playwright spec makes what it needs itself, through the API (a private account through the mail, an invitation by the development admin): `e2e-web.sh` seeds nothing for it.
- The implementer checks the syntax and the types only (`bash -n`, `tsc`, `playwright test --list`); the verifiers run the live checks.

- [ ] **Step 1: The script.** `scripts/e2e-private.sh` (LF line endings):

```bash
#!/usr/bin/env bash
# Real-network end-to-end check of spec 0010 (private accounts) against the compose stack, with the mail catcher (Mailpit) in it. It drives
# the sequence of the spec's Goal - a private person signs up through the mail, is signed in to a space of their own and deletes the
# account - and the cases around it.
#
# What it checks, in order (PRIV = the private person, INV = a second private person whom the development company invites, SEED = the
# development seed user, the admin of the development company). Each step names the criteria it touches; the guarding tests are in
# docs/superpowers/plans/0010-acceptance-map.md.
#   1. health is Healthy; the mail catcher answers; GET /auth/options -> 200 {"signup":"self-service","private_signup":"self-service"}
#      (deploy/auth.yaml turns both on); the OpenAPI description names DELETE /auth/account - part of criteria 2 and 30.
#   2. PRIV asks for a private sign-up -> 202 with an empty body; the mail "Complete your sign-up" has the private intro and the same link in
#      its text and HTML parts; the preview -> 200 {"kind":"private","email":PRIV}; a weak password -> 400 weak_password; the password ->
#      200 with a login's body and the auth_rt cookie; the token says org_kind personal, roles ["private"] and the private permissions of
#      deploy/auth.yaml; signup.requested and signup.completed say kind private, and no org.* row names the space - criteria 4, 8, 9, 10.
#   3. GET /auth/me -> org_kind personal, org_name private; GET /auth/org and GET /auth/org/members -> 403 forbidden - criteria 12, 14.
#   4. a minute later an organization request for PRIV -> 202; the mail "You already have an account" holds the sentence about a new
#      account on purpose, and no link - criterion 5.
#   5. INV signs up privately through the mail; SEED invites INV through the company API; the invitation mail holds the paragraph about the
#      private account; the preview says has_private_account true; the acceptance without leave_private_account -> 409
#      private_account_exists; with it -> 204; INV's cookie of its sign-up -> 401 invalid_grant; INV signs in with its own password, into
#      the development company, as user, org_kind organization; invite.accepted holds left_private_account true and the old space's id -
#      criteria 16 to 19.
#   6. PRIV deletes the account: a wrong password -> 403 wrong_password and an account.delete_refused row; the right one -> 204 and the
#      auth_rt cookie cleared (Max-Age=0); PRIV's refresh cookie -> 401 invalid_grant; PRIV's login -> the 401 of an unknown address;
#      account.deleted with the reason self; PRIV signs up privately again through the mail and finishes -> 200: the address is free -
#      criteria 21, 22.
#   7. SEED's DELETE /auth/account -> 403 forbidden - criterion 22.
#   8. SEED removes INV through the company API -> 204; INV's login -> the 401 of an unknown address; account.deleted with the reason
#      member_removed - criterion 23.
#   9. no audit row of the run holds a password, a link or a session token of the run - criterion 29.
#
# Full sequence, from the repo root (the stack and .env of the other e2e scripts, with the per-IP limiter OFF as for them; run this one after
# e2e-signup.sh and before e2e-hardening.sh, which recreates the stack with the limits on):
#   cp .env.example .env                  # then set real local values (git-ignored)
#   scripts/dev-keys.sh                   # on Windows: MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh
#   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, never "auth-core" (the development stack)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build
#   scripts/e2e-login.sh; scripts/e2e-refresh.sh; scripts/e2e-lockout.sh; scripts/e2e-email.sh; scripts/e2e-tenancy.sh; scripts/e2e-signup.sh
#   scripts/e2e-private.sh                # this script (does NOT bring the stack up or down)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# Reads AUTH_DEV_SEED_EMAIL and AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced). The database is read with
# `docker compose exec postgres psql`. Needs: curl, python3 (standard library only; E2E_PYTHON=<interpreter> names another, for example
# E2E_PYTHON=C:/p6v/Scripts/python.exe), docker compose. Env: BASE_URL (default http://localhost:8080), MAILPIT_URL (default
# http://localhost:8025). The links of the mails start with http://localhost:4200 (appsettings.Development.json). Takes about three
# minutes: the mail limit of PRIV (one request a minute) is waited out twice. Re-runnable on the same stack: every address is made for the
# run. Exits non-zero on the first failure; prints "PASS <step>" per step; never prints a password, a token, a cookie or a mail body.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
PY="${E2E_PYTHON:-python3}"
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-hardening}"
[[ "$COMPOSE_PROJECT_NAME" != "auth-core" ]] || { echo "FAIL COMPOSE_PROJECT_NAME=auth-core is the development stack; use another name" >&2; exit 1; }
compose=(docker compose -f "$root/deploy/docker-compose.yml" --env-file "$root/.env")
SIGNUP_LINK="http://localhost:4200/complete-signup"
INVITE_LINK="http://localhost:4200/invite"

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
PRIV_EMAIL="ola-$run@poczta.test"
INV_EMAIL="jan-$run@poczta.test"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
export E2E_PRIV_EMAIL="$PRIV_EMAIL" E2E_INV_EMAIL="$INV_EMAIL" E2E_COMPANY="Firma $run"
export E2E_PASSWORD="E2e-Passw0rd-$run" E2E_INV_PASSWORD="E2e-Inv-Passw0rd-$run" E2E_AGAIN_PASSWORD="E2e-Again-Passw0rd-$run"
export E2E_IGNORED_PASSWORD="E2e-Ignored-Passw0rd-$run" E2E_WRONG_PASSWORD="E2e-Wrong-Passw0rd-$run" E2E_WEAK_PASSWORD="abc"
export E2E_PRIVATE_INTRO="Someone asked to create a private account on"
export E2E_SENTENCE="If you want a new account on purpose, delete the one you have first, or sign up with another email address."
export E2E_PARAGRAPH="You already have a private account on"

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

# call <method> <path> [body-file] [header-file] -> sets HTTP_CODE and BODY; response headers in $tmp/hdr
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

# A Windows python3 ends its lines with CR LF: the helpers that print a value strip the CR.
val() { "$PY" -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1" <<< "$BODY" | tr -d '\r'; }

jwt_payload() { # jwt_payload <auth-header-file>: the payload of the bearer token in the file, as JSON (the token is never printed)
  "$PY" -c '
import base64, json, sys
token = sys.stdin.read().split("Bearer ", 1)[1].strip().split(".")[1]
print(json.dumps(json.loads(base64.urlsafe_b64decode(token + "=" * (-len(token) % 4)))))
' < "$1" | tr -d '\r'
}

json_body() { # json_body <file> <python-expression>: writes the JSON of the expression, which may read os.environ
  "$PY" -c 'import json,os,sys; print(json.dumps(eval(sys.argv[1])))' "$2" > "$1"
}

expect_status() { # expect_status <what> <code> [exact-body]
  [[ "$HTTP_CODE" == "$2" ]] || fail "$1: HTTP $HTTP_CODE, expected $2"
  if [[ $# -ge 3 ]]; then [[ "$BODY" == "$3" ]] || fail "$1: unexpected body"; fi
}

expect_error() { expect_status "$1" "$2" "{\"error\":\"$3\"}"; }

expect_no_store() { [[ "$(header cache-control)" == *no-store* ]] || fail "$1: no Cache-Control: no-store"; }

expect_eq() { [[ "$2" == "$3" ]] || fail "$1: got '$2', expected '$3'"; }

# login <name> <email-env> <password-env>: logs in, keeps the bearer header in $tmp/<name>.auth
login() {
  json_body "$tmp/$1.login" "{'email': os.environ['$2'], 'password': os.environ['$3']}"
  call POST /auth/login "$tmp/$1.login"
  expect_status "login of $1" 200
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$1.auth"
}

set_cookie_line() { # the auth_rt Set-Cookie line of the last response, CR stripped (empty when there is none)
  { grep -i '^set-cookie: auth_rt=' "$tmp/hdr" || true; } | head -n1 | tr -d '\r'
}

cookie_value() { # value of the auth_rt cookie in the last response
  local line
  line="$(set_cookie_line)"
  line="${line#*: }"
  line="${line#auth_rt=}"
  printf '%s' "${line%%;*}"
}

has_attr() { # has_attr <set-cookie-line> <attribute>: exact, case-insensitive match of one "; "-separated attribute
  printf '%s' "$1" | tr ';' '\n' | sed 's/^ *//' | grep -qixF -- "$2"
}

# --- the mail catcher ---------------------------------------------------------------------------------------------

mail_count() {
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" -o "$tmp/search.json"
  "$PY" -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages_count"])' < "$tmp/search.json" | tr -d '\r'
}

# wait_mail <address> <subject-start> <seconds> [more-than]: waits until the catcher holds more than <more-than> (default 0) mails to the
# address and the newest whose subject starts so is to exactly this address; saves that one to $tmp/mail.json
wait_mail() {
  local i id
  for i in $(seq 1 "$3"); do
    if [[ "$(mail_count "$1")" -gt "${4:-0}" ]]; then
      id="$("$PY" -c '
import json, sys
for message in json.load(sys.stdin.buffer)["messages"]:
    if message["Subject"].startswith(sys.argv[1]) and any(t["Address"].lower() == sys.argv[2].lower() for t in (message["To"] or [])):
        print(message["ID"])
        break
' "$2" "$1" < "$tmp/search.json" | tr -d '\r')"
      if [[ -n "$id" ]]; then
        curl -sS --max-time 10 "$MAILPIT_URL/api/v1/message/$id" -o "$tmp/mail.json"
        return 0
      fi
    fi
    sleep 1
  done
  return 1
}

# token_body <link-prefix> <file> [password-env] [leave]: takes the token from the text part of $tmp/mail.json, checks that the HTML part
# holds the same link, and writes the request body ({"token"}, plus "password" when named, plus "leave_private_account": true when <leave>
# is "leave"). Never printed.
token_body() {
  "$PY" -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(re.escape(sys.argv[1]) + r"\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no link with a token")
if sys.argv[1] + "?token=" + match.group(1) not in mail["HTML"]:
    sys.exit("the HTML part does not hold the link of the text part")
body = {"token": match.group(1)}
if sys.argv[2]:
    body["password"] = os.environ[sys.argv[2]]
if sys.argv[3] == "leave":
    body["leave_private_account"] = True
print(json.dumps(body))
' "$1" "${3:-}" "${4:-}" < "$tmp/mail.json" > "$2"
}

token_of() { "$PY" -c 'import json,sys; print(json.load(sys.stdin)["token"])' < "$1" | tr -d '\r'; }   # never printed by the callers

mail_field() { "$PY" -c 'import json,sys; print(json.load(sys.stdin.buffer)[sys.argv[1]])' "$1" < "$tmp/mail.json" | tr -d '\r'; }

mail_holds() { # mail_holds <env-name>: whether the text part and the HTML part of $tmp/mail.json both hold the value of that variable
  "$PY" -c '
import json, os, sys
mail = json.load(sys.stdin.buffer)
value = os.environ[sys.argv[1]]
sys.exit(0 if value in mail["Text"] and value in mail["HTML"] else 1)
' "$1" < "$tmp/mail.json"
}

mail_has_no_link() { # neither part of $tmp/mail.json holds a token parameter or a URL
  "$PY" -c '
import json, sys
mail = json.load(sys.stdin.buffer)
sys.exit(0 if all("token=" not in part and "://" not in part for part in (mail["Text"], mail["HTML"])) else 1)
' < "$tmp/mail.json"
}

# psql_value <sql>: the value the query returns. The SQL is this script's own text: a value that came from the service under test is never put into it.
psql_value() {
  printf '%s\n' "$1" | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'
}

# secret_in_audit <secret>: how many rows of the audit log hold the text in any column; the secret goes on stdin only, and psql's standard
# error (which would echo a failed query) goes to a file that is never printed.
secret_in_audit() {
  local count
  [[ "$1" != *"'"* ]] || fail "a secret with a quote cannot be looked for"
  count="$(printf "SELECT count(*) FROM audit_events WHERE position('%s' in (kind || ' ' || coalesce(subject_email, '') || ' ' || coalesce(org_name, '') || ' ' || coalesce(client_ip, '') || ' ' || coalesce(details::text, ''))) > 0;\n" "$1" \
    | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA 2> "$tmp/psql.err" | tr -d '\r' || true)"
  [[ "$count" =~ ^[0-9]+$ ]] || fail "the audit log could not be searched for a secret (the database answer is not a number)"
  printf '%s\n' "$count"
}

# request_signup <body-file> <what>: POST /auth/signup; a 429 of the mail limit of the address is waited out once
request_signup() {
  call POST /auth/signup "$1"
  if [[ "$HTTP_CODE" == "429" ]]; then
    [[ "$BODY" =~ ^\{\"error\":\"too_many_attempts\",\"retry_after_seconds\":([0-9]+)\}$ ]] || fail "$2: unexpected 429 body"
    echo "waiting $((BASH_REMATCH[1] + 1)) s for the mail limit of the address..."
    sleep "$((BASH_REMATCH[1] + 1))"
    call POST /auth/signup "$1"
  fi
  expect_status "$2" 202 ""
}

# private_signup <email-env> <password-env> <name>: a private sign-up through the mail, to the end; the completion's bearer header is kept in
# $tmp/<name>.auth and its refresh cookie in $tmp/<name>.cookie (a header file for curl)
private_signup() {
  local before
  before="$(mail_count "${!1}")"
  json_body "$tmp/$3.signup" "{'email': os.environ['$1'], 'kind': 'private'}"
  request_signup "$tmp/$3.signup" "the private sign-up request of $3"
  wait_mail "${!1}" "Complete your sign-up" 90 "$before" || fail "no new sign-up mail for $3 within 90s"
  token_body "$SIGNUP_LINK" "$tmp/$3.complete" "$2" || fail "no usable sign-up link in the mail of $3"
  call POST /auth/signup/complete "$tmp/$3.complete"
  expect_status "the completion of $3" 200
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$3.auth"
  printf 'Cookie: auth_rt=%s\n' "$(cookie_value)" > "$tmp/$3.cookie"
}

# --- Step 1: health, the mail catcher, the options, the description (criteria 2, 30) ------------------------------------
wait_ok "$BASE_URL/auth/health" 90 || fail "step 1: $BASE_URL/auth/health did not return 200 within 90s"
wait_ok "$MAILPIT_URL/readyz" 60 || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s (is the mailpit service up?)"
call GET /auth/health
expect_status "step 1: health" 200 "Healthy"
call GET /auth/options
expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service"}'
expect_no_store "step 1: the options"
call GET /auth/openapi/v1.json
expect_status "step 1: the OpenAPI description" 200
expect_eq "step 1: DELETE /auth/account is described" "$(val "'delete' in d['paths'].get('/auth/account', {})")" "True"
pass "step 1: health is Healthy; GET /auth/options says both kinds of sign-up are on; the description names DELETE /auth/account"

# --- Step 2: a private sign-up through the mail (criteria 4, 8, 9, 10) --------------------------------------------------
START_TS="$(psql_value "SELECT now()")"
json_body "$tmp/priv.signup" "{'email': os.environ['E2E_PRIV_EMAIL'], 'kind': 'private'}"
call POST /auth/signup "$tmp/priv.signup"
expect_status "step 2: the private sign-up request" 202 ""
expect_no_store "step 2: the 202"
wait_mail "$PRIV_EMAIL" "Complete your sign-up" 90 || fail "step 2: no sign-up mail for PRIV within 90s"
mail_holds E2E_PRIVATE_INTRO || fail "step 2: the text and the HTML part do not both hold the private intro"
token_body "$SIGNUP_LINK" "$tmp/priv.preview" || fail "step 2: no usable sign-up link in the mail"
token_body "$SIGNUP_LINK" "$tmp/priv.weak" E2E_WEAK_PASSWORD || fail "step 2: the weak completion body could not be made"
token_body "$SIGNUP_LINK" "$tmp/priv.complete" E2E_PASSWORD || fail "step 2: the completion body could not be made"
PRIV_LINK="$(token_of "$tmp/priv.preview")"
call POST /auth/signup/preview "$tmp/priv.preview"
expect_status "step 2: preview" 200 "{\"kind\":\"private\",\"email\":\"$PRIV_EMAIL\"}"
call POST /auth/signup/complete "$tmp/priv.weak"
expect_status "step 2: a weak password" 400 '{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}'
call POST /auth/signup/complete "$tmp/priv.complete"
expect_status "step 2: the completion" 200
expect_eq "step 2: the body is a login's" "$(val 'sorted(d.keys())')" "['access_token', 'status']"
LINE="$(set_cookie_line)"
[[ -n "$LINE" ]] || fail "step 2: the completion set no auth_rt cookie"
for attr in HttpOnly Secure SameSite=Strict Path=/auth; do
  has_attr "$LINE" "$attr" || fail "step 2: the auth_rt cookie lacks the $attr attribute"
done
PRIV_ACCESS="$(val 'd["access_token"]')"
PRIV_COOKIE="$(cookie_value)"
printf 'Authorization: Bearer %s\n' "$PRIV_ACCESS" > "$tmp/priv.auth"
printf 'Cookie: auth_rt=%s\n' "$PRIV_COOKIE" > "$tmp/priv.cookie"
BODY="$(jwt_payload "$tmp/priv.auth")"
expect_eq "step 2: org_kind" "$(val 'd["org_kind"]')" "personal"
expect_eq "step 2: roles" "$(val 'd["roles"]')" "['private']"
expect_eq "step 2: permissions" "$(val 'd["permissions"]')" "['documents:read', 'documents:write']"
PRIV_SPACE="$(val 'd["org_id"]')"
PRIV_SUB="$(val 'd["sub"]')"
expect_eq "step 2: signup.requested says kind private" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.requested' AND subject_email = '$PRIV_EMAIL' AND details->>'kind' = 'private' AND occurred_at >= '$START_TS'")" "1"
expect_eq "step 2: signup.completed says kind private and names the space" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.completed' AND subject_user_id = '$PRIV_SUB' AND org_id = '$PRIV_SPACE' AND org_name = 'private' AND details->>'kind' = 'private'")" "1"
expect_eq "step 2: no org.* row names the space" "$(psql_value "SELECT count(*) FROM audit_events WHERE kind LIKE 'org.%' AND org_id = '$PRIV_SPACE'")" "0"
expect_eq "step 2: the space is personal" "$(psql_value "SELECT \"Kind\" FROM \"Companies\" WHERE \"Id\" = '$PRIV_SPACE'")" "2"
pass "step 2: a private sign-up through the mail: 202, the private mail, the preview of its kind, weak_password, then a login's answer with org_kind personal and the private role; the audit rows say kind private"

# --- Step 3: /auth/me and the company API (criteria 12, 14) -------------------------------------------------------------
call GET /auth/me "" "$tmp/priv.auth"
expect_status "step 3: GET /auth/me" 200
expect_eq "step 3: org_kind" "$(val 'd["org_kind"]')" "personal"
expect_eq "step 3: org_name" "$(val 'd["org_name"]')" "private"
expect_eq "step 3: roles" "$(val 'd["roles"]')" "['private']"
call GET /auth/org "" "$tmp/priv.auth"
expect_error "step 3: GET /auth/org" 403 forbidden
call GET /auth/org/members "" "$tmp/priv.auth"
expect_error "step 3: GET /auth/org/members" 403 forbidden
pass "step 3: /auth/me says personal and private; the company API answers 403 forbidden, GET /auth/org included"

# --- Step 4: an account that exists gets the notice (criterion 5) -------------------------------------------------------
MAILS="$(mail_count "$PRIV_EMAIL")"
json_body "$tmp/priv.org" "{'email': os.environ['E2E_PRIV_EMAIL'], 'company_name': os.environ['E2E_COMPANY']}"
request_signup "$tmp/priv.org" "step 4: an organization request for PRIV"
wait_mail "$PRIV_EMAIL" "You already have an account" 90 "$MAILS" || fail "step 4: no 'you already have an account' mail for PRIV within 90s"
mail_holds E2E_SENTENCE || fail "step 4: the mail does not hold the sentence about a new account on purpose"
mail_has_no_link || fail "step 4: the mail holds a link or a token"
pass "step 4: an organization request for an address with a private account -> 202 and the mail that says so, with the new sentence and no link"

# --- Step 5: the invitation of a private person (criteria 16 to 19) -----------------------------------------------------
private_signup E2E_INV_EMAIL E2E_INV_PASSWORD inv
BODY="$(jwt_payload "$tmp/inv.auth")"
INV_SPACE="$(val 'd["org_id"]')"
INV_SUB="$(val 'd["sub"]')"
login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD
call GET /auth/me "" "$tmp/seed.auth"
expect_status "step 5: GET /auth/me of SEED" 200
DEV_ORG="$(val 'd["org_id"]')"
call GET /auth/org/roles "" "$tmp/seed.auth"
expect_status "step 5: GET /auth/org/roles" 200
export E2E_USER_ROLE
E2E_USER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "user"][0]')"
MAILS="$(mail_count "$INV_EMAIL")"
json_body "$tmp/invite.json" "{'email': os.environ['E2E_INV_EMAIL'], 'role_id': os.environ['E2E_USER_ROLE']}"
call POST /auth/org/invites "$tmp/invite.json" "$tmp/seed.auth"
expect_status "step 5: SEED invites INV" 202 ""
wait_mail "$INV_EMAIL" "You are invited" 90 "$MAILS" || fail "step 5: no invitation mail for INV within 90s"
mail_holds E2E_PARAGRAPH || fail "step 5: the invitation mail does not hold the paragraph about the private account"
token_body "$INVITE_LINK" "$tmp/inv.preview" || fail "step 5: no usable invitation link"
token_body "$INVITE_LINK" "$tmp/inv.plain" E2E_IGNORED_PASSWORD || fail "step 5: the acceptance body could not be made"
token_body "$INVITE_LINK" "$tmp/inv.leave" E2E_IGNORED_PASSWORD leave || fail "step 5: the moving acceptance body could not be made"
INV_LINK="$(token_of "$tmp/inv.preview")"
call POST /auth/invites/preview "$tmp/inv.preview"
expect_status "step 5: preview" 200
expect_eq "step 5: has_private_account" "$(val 'd["has_private_account"]')" "True"
call POST /auth/invites/accept "$tmp/inv.plain"
expect_error "step 5: the acceptance without leave_private_account" 409 private_account_exists
call POST /auth/invites/accept "$tmp/inv.leave"
expect_status "step 5: the acceptance with leave_private_account true" 204 ""
call POST /auth/refresh "" "$tmp/inv.cookie"
expect_error "step 5: INV's cookie of its sign-up" 401 invalid_grant
json_body "$tmp/inv.ignored" "{'email': os.environ['E2E_INV_EMAIL'], 'password': os.environ['E2E_IGNORED_PASSWORD']}"
call POST /auth/login "$tmp/inv.ignored"
expect_error "step 5: the password sent with the acceptance was not used" 401 invalid_credentials
login inv E2E_INV_EMAIL E2E_INV_PASSWORD
BODY="$(jwt_payload "$tmp/inv.auth")"
expect_eq "step 5: INV is the same account" "$(val 'd["sub"]')" "$INV_SUB"
expect_eq "step 5: INV is in the development company" "$(val 'd["org_id"]')" "$DEV_ORG"
expect_eq "step 5: org_kind" "$(val 'd["org_kind"]')" "organization"
expect_eq "step 5: roles" "$(val 'd["roles"]')" "['user']"
expect_eq "step 5: the personal space is gone" "$(psql_value "SELECT count(*) FROM \"Companies\" WHERE \"Id\" = '$INV_SPACE'")" "0"
expect_eq "step 5: invite.accepted says the private account was left, and which space" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'invite.accepted' AND subject_user_id = '$INV_SUB' AND details->>'left_private_account' = 'true' AND details->>'personal_org_id' = '$INV_SPACE'")" "1"
pass "step 5: the invitation of a private person: the mail's paragraph, has_private_account, 409 private_account_exists, the move (204), the old session ended, the old password into the company"

# --- Step 6: the self-deletion (criteria 21, 22) ------------------------------------------------------------------------
json_body "$tmp/delete-wrong.json" "{'password': os.environ['E2E_WRONG_PASSWORD']}"
call DELETE /auth/account "$tmp/delete-wrong.json" "$tmp/priv.auth"
expect_error "step 6: a wrong password" 403 wrong_password
expect_eq "step 6: account.delete_refused with the reason wrong_password" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'account.delete_refused' AND subject_user_id = '$PRIV_SUB' AND details->>'reason' = 'wrong_password'")" "1"
json_body "$tmp/delete.json" "{'password': os.environ['E2E_PASSWORD']}"
call DELETE /auth/account "$tmp/delete.json" "$tmp/priv.auth"
expect_status "step 6: the deletion" 204 ""
expect_no_store "step 6: the 204"
LINE="$(set_cookie_line)"
[[ -n "$LINE" ]] || fail "step 6: the deletion did not clear the auth_rt cookie"
has_attr "$LINE" "Max-Age=0" || fail "step 6: the cleared cookie has no Max-Age=0"
[[ -z "$(cookie_value)" ]] || fail "step 6: the cleared cookie is not empty"
call POST /auth/refresh "" "$tmp/priv.cookie"
expect_error "step 6: PRIV's refresh cookie" 401 invalid_grant
json_body "$tmp/priv.login" "{'email': os.environ['E2E_PRIV_EMAIL'], 'password': os.environ['E2E_PASSWORD']}"
call POST /auth/login "$tmp/priv.login"
expect_error "step 6: PRIV's login is that of an unknown address" 401 invalid_credentials
expect_eq "step 6: account.deleted with the reason self" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'account.deleted' AND subject_user_id = '$PRIV_SUB' AND actor_user_id = '$PRIV_SUB' AND org_id = '$PRIV_SPACE' AND details->>'reason' = 'self'")" "1"
expect_eq "step 6: no account, no space" \
  "$(psql_value "SELECT (SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = upper('$PRIV_EMAIL')) + (SELECT count(*) FROM \"Companies\" WHERE \"Id\" = '$PRIV_SPACE')")" "0"
private_signup E2E_PRIV_EMAIL E2E_AGAIN_PASSWORD again
BODY="$(jwt_payload "$tmp/again.auth")"
[[ "$(val 'd["sub"]')" != "$PRIV_SUB" ]] || fail "step 6: the new sign-up has the deleted account's id"
pass "step 6: a wrong password -> 403 wrong_password (recorded); the right one -> 204 with the cookie cleared; the old session and the login are refused; the address signs up again"

# --- Step 7: a company member cannot delete their account (criterion 22) ------------------------------------------------
json_body "$tmp/seed-delete.json" "{'password': os.environ['E2E_SEED_PASSWORD']}"
call DELETE /auth/account "$tmp/seed-delete.json" "$tmp/seed.auth"
expect_error "step 7: SEED's DELETE /auth/account" 403 forbidden
pass "step 7: a company member's DELETE /auth/account -> 403 forbidden"

# --- Step 8: a removal deletes the account (criterion 23) ---------------------------------------------------------------
call GET /auth/org/members "" "$tmp/seed.auth"
expect_status "step 8: GET /auth/org/members" 200
INV_ID="$(val "[m['user_id'] for m in d['members'] if m['email'].lower() == '$INV_EMAIL'][0]")"
call DELETE "/auth/org/members/$INV_ID" "" "$tmp/seed.auth"
expect_status "step 8: SEED removes INV" 204 ""
json_body "$tmp/inv.login2" "{'email': os.environ['E2E_INV_EMAIL'], 'password': os.environ['E2E_INV_PASSWORD']}"
call POST /auth/login "$tmp/inv.login2"
expect_error "step 8: INV's login is that of an unknown address" 401 invalid_credentials
expect_eq "step 8: account.deleted with the reason member_removed" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'account.deleted' AND subject_user_id = '$INV_SUB' AND org_id = '$DEV_ORG' AND details->>'reason' = 'member_removed'")" "1"
pass "step 8: a removal through the company API -> 204, and the account is deleted with its row"

# --- Step 9: no secret in the audit log (criterion 29) ------------------------------------------------------------------
for secret in "$E2E_PASSWORD" "$E2E_INV_PASSWORD" "$E2E_AGAIN_PASSWORD" "$E2E_IGNORED_PASSWORD" "$E2E_WRONG_PASSWORD" "$PRIV_LINK" "$INV_LINK" "$PRIV_ACCESS" "$PRIV_COOKIE"; do
  [[ -n "$secret" ]] || fail "step 9: a secret to look for is empty"
  expect_eq "step 9: rows of the audit log that hold a password, a link or a session token of this run" "$(secret_in_audit "$secret")" "0"
done
pass "step 9: no audit row holds a password, a link or a session token of this run"

echo "ALL PASS"
```

- [ ] **Step 2: The earlier live checks follow spec 0010.**

(a) `scripts/e2e-signup.sh`:
- In the header, replace `GET /auth/options -> 200 {"signup":"self-service"}, never stored;` with `GET /auth/options -> 200 {"signup":"self-service","private_signup":"self-service"}, never stored;`.
- In the header, replace `#   5. preview with the token -> 200 with exactly the typed address, the company name and has_account false, twice (it changes nothing); an unknown token` with `#   5. preview with the token -> 200 with exactly the kind organization, the typed address and the company name, twice (it changes nothing); an unknown token`.
- In the header, replace the description of step 12 (the lines from `#  12. INVITED, a member of the development company after step 10, is removed from it through the operator CLI` to `#      the integration tests and the Playwright test).`) with

```bash
#  12. INVITED, a member of the development company after step 10, is removed from it through the operator CLI (`remove-member`): spec 0010
#      deletes the account with its only space (the CLI says so, a login is the 401 of an unknown address, and account.deleted says
#      member_removed and via cli). INVITED asks for a sign-up again (waiting out the mail limit if step 10 was less than a minute ago) and
#      finishes it as a new address: the usual mail with a new link, the preview of an organization, a new password, a new account (a new
#      id) that is the admin of a new company; the old password no longer works, and signup.completed says kind organization - criterion 11
#      of spec 0010.
```

- In the header's "Full sequence", after the line `#   scripts/e2e-signup.sh                 # this script (does NOT bring the stack up or down)` add
  `#   scripts/e2e-private.sh                # spec 0010 (private accounts), after this one`.
- Replace `export E2E_BACK_ASKED="Back Asked $run" E2E_BACK_COMPANY="Back Company $run"` with
  `export E2E_BACK_ASKED="Back Asked $run" E2E_BACK_COMPANY="Back Company $run" E2E_BACK_PASSWORD="E2e-Back-Passw0rd-$run"`.
- Replace `expect_status "step 1: the options" 200 '{"signup":"self-service"}'` with
  `expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service"}'`.
- Replace both `"{\"email\":\"$NEW_EMAIL\",\"company_name\":\"$COMPANY\",\"has_account\":false}"` with
  `"{\"kind\":\"organization\",\"email\":\"$NEW_EMAIL\",\"company_name\":\"$COMPANY\"}"`, and in the `pass "step 5: ...` line replace
  `with exactly the typed address, the company name and has_account false, twice` with `with exactly the kind, the typed address and the company name, twice`.
- Replace everything from the line `# --- Step 12: a person without a company comes back by sign-up (criterion 24) ---...` to the line before
  `echo "ALL PASS"` with

```bash
# --- Step 12: a removed member's address signs up as a new address (spec 0010: the removal deleted the account) ----------------
# After step 10 INVITED is a member of the development company. The operator removes it: the account is deleted with its only space.
# INVITED asks for a sign-up again and finishes it as any new address does.
login invited E2E_INVITED_EMAIL E2E_INVITED_PASSWORD
BODY="$(jwt_payload "$tmp/invited.auth")"
INVITED_SUB="$(val 'd["sub"]')"
cli remove-member --org "$DEV_ORG" --email "$INVITED_EMAIL"
[[ "$CLI_EXIT" == "0" ]] || fail "step 12: the operator CLI failed: admin remove-member"
expect_eq "step 12: what remove-member says" "$CLI_OUT" "Member removed; the account is deleted."
json_body "$tmp/invited.login" "{'email': os.environ['E2E_INVITED_EMAIL'], 'password': os.environ['E2E_INVITED_PASSWORD']}"
call POST /auth/login "$tmp/invited.login"
expect_error "step 12: INVITED's login is that of an unknown address" 401 invalid_credentials
expect_eq "step 12: no account under INVITED's address" "$(psql_value "SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = upper('$INVITED_EMAIL')")" "0"
expect_eq "step 12: account.deleted says member_removed, by the CLI" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'account.deleted' AND subject_user_id = '$INVITED_SUB' AND details->>'reason' = 'member_removed' AND details->>'via' = 'cli'")" "1"

# The link of step 10 (never printed) is told from the new one by its token: the catcher still holds the old mail.
OLD_LINK_TOKEN="$("$PY" -c 'import json,sys; print(json.load(sys.stdin)["token"])' < "$tmp/invited-complete.json" | tr -d '\r')"
MAILS_BEFORE="$(mail_count "$INVITED_EMAIL")"
json_body "$tmp/back-signup.json" "{'email': os.environ['E2E_INVITED_EMAIL'], 'company_name': os.environ['E2E_BACK_ASKED']}"
call POST /auth/signup "$tmp/back-signup.json"
if [[ "$HTTP_CODE" == "429" ]]; then   # step 10's request was less than a minute ago
  [[ "$BODY" =~ ^\{\"error\":\"too_many_attempts\",\"retry_after_seconds\":([0-9]+)\}$ ]] || fail "step 12: unexpected 429 body"
  echo "waiting $((BASH_REMATCH[1] + 1)) s for the mail limit of INVITED..."
  sleep "$((BASH_REMATCH[1] + 1))"
  call POST /auth/signup "$tmp/back-signup.json"
fi
expect_status "step 12: the sign-up request of INVITED's address" 202 ""
for _ in $(seq 1 90); do
  [[ "$(mail_count "$INVITED_EMAIL")" -gt "$MAILS_BEFORE" ]] && break
  sleep 1
done
[[ "$(mail_count "$INVITED_EMAIL")" -gt "$MAILS_BEFORE" ]] || fail "step 12: no new mail for INVITED within 90s"
wait_mail "$INVITED_EMAIL" "Complete your sign-up" 5 || fail "step 12: the address did not get a sign-up mail (the newest mail is another)"
token_body http://localhost:4200/complete-signup "$tmp/back-preview.json" || fail "step 12: no usable sign-up link in the mail"
token_body http://localhost:4200/complete-signup "$tmp/back-complete.json" E2E_BACK_PASSWORD E2E_BACK_COMPANY || fail "step 12: the completion body could not be made"
NEW_LINK_TOKEN="$("$PY" -c 'import json,sys; print(json.load(sys.stdin)["token"])' < "$tmp/back-preview.json" | tr -d '\r')"
[[ "$NEW_LINK_TOKEN" != "$OLD_LINK_TOKEN" ]] || fail "step 12: the newest sign-up mail is the one of step 10, not a new link"

call POST /auth/signup/preview "$tmp/back-preview.json"
expect_status "step 12: preview" 200 "{\"kind\":\"organization\",\"email\":\"$INVITED_EMAIL\",\"company_name\":\"$E2E_BACK_ASKED\"}"
call POST /auth/signup/complete "$tmp/back-complete.json"
expect_status "step 12: complete with a new password and a company name" 200
expect_eq "step 12: the body is a login's" "$(val 'sorted(d.keys())')" "['access_token', 'status']"
LINE="$(set_cookie_line)"
[[ -n "$LINE" ]] || fail "step 12: the completion set no auth_rt cookie"
printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/back.auth"
BODY="$(jwt_payload "$tmp/back.auth")"
expect_eq "step 12: roles" "$(val 'd["roles"]')" "['admin']"
expect_eq "step 12: org_kind" "$(val 'd["org_kind"]')" "organization"
BACK_SUB="$(val 'd["sub"]')"
[[ "$BACK_SUB" != "$INVITED_SUB" ]] || fail "step 12: the new account has the id of the deleted one"
BACK_ORG="$(val 'd["org_id"]')"
[[ "$BACK_ORG" != "$DEV_ORG" && "$BACK_ORG" != "$NEW_ORG" ]] || fail "step 12: the new company is another company's"
call GET /auth/me "" "$tmp/back.auth"
expect_status "step 12: GET /auth/me" 200
expect_eq "step 12: the company is the one named at the completion" "$(val 'd["org_name"]')" "$E2E_BACK_COMPANY"

call POST /auth/login "$tmp/invited.login"
expect_error "step 12: the old password belongs to the deleted account" 401 invalid_credentials
login back E2E_INVITED_EMAIL E2E_BACK_PASSWORD
expect_eq "step 12: one account under the address" "$(psql_value "SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = upper('$INVITED_EMAIL')")" "1"
expect_eq "step 12: the pending sign-up is gone" "$(psql_value "SELECT count(*) FROM \"PendingSignups\" WHERE \"NormalizedEmail\" = upper('$INVITED_EMAIL')")" "0"
expect_eq "step 12: signup.completed says kind organization and nothing of an existing account" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.completed' AND subject_user_id = '$BACK_SUB' AND org_id = '$BACK_ORG' AND details->>'kind' = 'organization' AND NOT (details ? 'existing_account')")" "1"
call POST /auth/signup/complete "$tmp/back-complete.json"
expect_error "step 12: the same completion again" 400 invalid_token
pass "step 12: a member removed by the operator is deleted (the CLI says so; login: the 401 of an unknown address; account.deleted member_removed via cli); the address signs up again as a new address, with a new account, a new password and a company of its own"
```

  (Step 7's check of the `signup.completed` row names no `details` and stays; the old step 12's check that NEW's row has no details is gone
  with that step.)

(b) `scripts/e2e-tenancy.sh`:
- In the header, replace `#   9. removal: WORKER's refresh cookie -> 401 with no cookie written, login with the right password -> 403` and the next line
  `#      no_membership with no cookie, the old access token -> permissions_changed - part of 11 and 13 (no wrong password,` with
  `#   9. removal: WORKER's refresh cookie -> 401 with no cookie written, login with the right password -> 401 invalid_credentials (spec 0010:` and
  `#      the removal deleted the account) with no cookie, the old access token -> permissions_changed - part of 11 and 13 (no wrong password,`;
  replace `#      are over - criterion 22; part of 16.` with `#      are over and its account is gone (spec 0010) - criterion 22; part of 16.`;
  after `#   scripts/e2e-signup.sh                 # spec 0009 (self-service sign-up), which runs after this one` add
  `#   scripts/e2e-private.sh                # spec 0010 (private accounts), after e2e-signup.sh`.
- Replace

```bash
expect_error "step 9: WORKER logs in" 403 no_membership
expect_no_store "step 9: no_membership"
[[ -z "$(header set-cookie)" ]] || fail "step 9: no_membership set a cookie"
```

  with

```bash
expect_error "step 9: WORKER logs in (spec 0010: the removal deleted the account)" 401 invalid_credentials
expect_no_store "step 9: the refused login"
[[ -z "$(header set-cookie)" ]] || fail "step 9: the refused login set a cookie"
```

  and in the `pass "step 9: ...` line replace `login -> 403 no_membership` with `login -> 401 invalid_credentials (the account is gone)`.
- Replace `expect_error "step 10: ADMIN logs in" 403 no_membership` with `expect_error "step 10: ADMIN logs in (spec 0010: the account is gone)" 401 invalid_credentials`,
  and in the `pass "step 10: ...` line replace `ADMIN's sessions are over` with `ADMIN's sessions and account are gone`.

(c) `scripts/e2e-hardening.sh`:
- In the header, replace `#      permissions_changed, the refresh 401 invalid_grant, the login 403 no_membership, the pending link invalid_token, the company's` with
  `#      permissions_changed, the refresh 401 invalid_grant, the login the 401 of an unknown address, the pending link invalid_token, the company's`
  and `#      rows and the queued mail are gone, and the accounts stay - criteria 10, 8. (That the queued mail is never sent is not checked here: the` with
  `#      rows and the queued mail are gone, and the accounts are deleted (spec 0010) - criteria 10, 8. (That the queued mail is never sent is not checked here: the`;
  replace `#   scripts/e2e-login.sh && scripts/e2e-refresh.sh && scripts/e2e-lockout.sh && scripts/e2e-email.sh && scripts/e2e-tenancy.sh && scripts/e2e-signup.sh` with
  `#   scripts/e2e-login.sh && scripts/e2e-refresh.sh && scripts/e2e-lockout.sh && scripts/e2e-email.sh && scripts/e2e-tenancy.sh && scripts/e2e-signup.sh && scripts/e2e-private.sh`.
- Replace `expect_error "step 5: a new login" 403 no_membership` with `expect_error "step 5: a new login (spec 0010: the account is gone)" 401 invalid_credentials`.
- Replace

```bash
expect_eq "step 5: the account of the admin stays" \
  "$(psql_value "SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = '${ADMIN_EMAIL^^}'")" "1"
```

  with

```bash
expect_eq "step 5: the account of the admin is deleted with its company (spec 0010)" \
  "$(psql_value "SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = '${ADMIN_EMAIL^^}'")" "0"
expect_eq "step 5: account.deleted says org_deleted" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'account.deleted' AND org_id = '$ORG' AND details->>'reason' = 'org_deleted'")" "1"
```

- In the `pass "step 5: ...` line replace `no_membership, an invalid link, no rows left, the queued mail gone, the account stays` with
  `the 401 of an unknown address, an invalid link, no rows left, the queued mail gone, the account deleted`.
- In step 6 replace `for kind in login.succeeded login.failed logout refresh.reuse_detected password.reset_requested invite.sent invite.accepted org.created org.deleted org.delete_refused rate_limit.hit; do` with
  `for kind in login.succeeded login.failed logout refresh.reuse_detected password.reset_requested invite.sent invite.accepted org.created org.deleted org.delete_refused rate_limit.hit account.deleted; do`.

(d) `scripts/e2e-web.sh`:
- In the header, delete the three lines from `# It also prepares COMEBACK (Amendment 3 of spec 0009): invited to the development company, the invitation accepted by the API once the mail has`
  to `# sign-up and finishes it with a company name only.`; after `# link into the past with one UPDATE of "PendingSignups"."TokenExpiresAt" (the database, because the test cannot move the server's clock).` add
  `# The tests of spec 0010 (e2e/10-private.spec.ts) make what they need themselves, through the API; nothing is seeded for them.`
- Delete the line `  COMEBACK_EMAIL="comeback-$run@e2e.test"`.
- Replace `  # Four invitations through the operator CLI (BOTH and COMEBACK are for the sign-up tests of spec 0009). The server sends the mails at its next poll.` with
  `  # Three invitations through the operator CLI (BOTH is for the sign-up tests of spec 0009). The server sends the mails at its next poll.`
  and delete the line `  cli invite --org "$org" --email "$COMEBACK_EMAIL" --role user`.
- Delete the block from `  # A person whose company is gone: accept the invitation by the API (the password is USER_PASSWORD), then remove the member through the CLI.`
  to `  cli remove-member --org "$org" --email "$COMEBACK_EMAIL"` (both lines included).
- Replace `  export E2E_BOTH_EMAIL="$BOTH_EMAIL" E2E_EXPIRED_EMAIL="$EXPIRED_EMAIL" E2E_COMEBACK_EMAIL="$COMEBACK_EMAIL"` with
  `  export E2E_BOTH_EMAIL="$BOTH_EMAIL" E2E_EXPIRED_EMAIL="$EXPIRED_EMAIL"`.
- Delete the function `mail_token` (from its line `mail_token() { # mail_token <address> <subject start> <path>: ...` to its closing `}`): nothing calls it any
  more. The variable `token` of `seed_stack` stays declared (the admin's sign-in uses it).

(e) `samples/notes-web/e2e/09-signup.spec.ts`: delete the test `'a person whose company is gone comes back by sign-up: only the company name is asked for, and the old password still works'`
(from its `test(` line to its closing `});`, the end of the file). If `signIn` and `signOut` are then unused imports, remove them from the import line.

- [ ] **Step 3: The Playwright spec.** `samples/notes-web/e2e/support/accounts.ts`:

```ts
import { apiUrl, setting } from './env';
import { waitForLink } from './mail';

async function postJson(path: string, body: unknown, token?: string): Promise<Response> {
  return fetch(`${apiUrl()}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(token === undefined ? {} : { Authorization: `Bearer ${token}` }) },
    body: JSON.stringify(body),
  });
}

/** The development admin's access token, by the API (the address and the password come from scripts/e2e-web.sh). */
async function adminToken(): Promise<string> {
  const response = await postJson('/auth/login', { email: setting('E2E_SEED_EMAIL'), password: setting('E2E_SEED_PASSWORD') });
  if (response.status !== 200) {
    throw new Error(`the development admin could not sign in by the API: ${response.status}`);
  }
  const body = (await response.json()) as { access_token?: unknown };
  if (typeof body.access_token !== 'string') {
    throw new Error('the sign-in of the development admin gave no token');
  }
  return body.access_token;
}

/** The development admin invites the address into the development company with the role named, through the company API (the mail goes out at once). */
export async function inviteByApi(email: string, roleName: string): Promise<void> {
  const token = await adminToken();
  const roles = await fetch(`${apiUrl()}/auth/org/roles`, { headers: { Authorization: `Bearer ${token}` } });
  const list = (await roles.json()) as { roles: { id: string; name: string }[] };
  const role = list.roles.find((candidate) => candidate.name === roleName);
  if (role === undefined) {
    throw new Error(`the development company has no role ${roleName}`);
  }
  const response = await postJson('/auth/org/invites', { email, role_id: role.id }, token);
  if (response.status !== 202) {
    throw new Error(`the invitation answered ${response.status}, not 202`);
  }
}

/** A private account (spec 0010) made by the API through the mail: the request, the link of the mail, the completion with the password. */
export async function signUpPrivatelyByApi(email: string, password: string, waitMs = 100_000): Promise<void> {
  const requested = await postJson('/auth/signup', { email, kind: 'private' });
  if (requested.status !== 202) {
    throw new Error(`the private sign-up answered ${requested.status}, not 202`);
  }
  const link = await waitForLink(email, 'complete-signup', waitMs);
  const token = new URL(link, 'http://placeholder.invalid').searchParams.get('token');
  if (token === null) {
    throw new Error('the sign-up link holds no token');
  }
  const completed = await postJson('/auth/signup/complete', { token, password });
  if (completed.status !== 200) {
    throw new Error(`the completion answered ${completed.status}, not 200`);
  }
}

/** Whether the address and the password still sign in, by the API: the status of POST /auth/login. */
export async function loginStatus(email: string, password: string): Promise<number> {
  return (await postJson('/auth/login', { email, password })).status;
}
```

`samples/notes-web/e2e/10-private.spec.ts`:

```ts
import { expect, test } from '@playwright/test';
import { acceptInviteByApi } from './support/api';
import { inviteByApi, loginStatus, signUpPrivatelyByApi } from './support/accounts';
import { setting } from './support/env';
import { openMailLink, waitForLink } from './support/mail';
import { expectCleanAddressBar, expectNoEmailInAddressBar } from './support/page';
import { signIn } from './support/session';

// The request and the API invitation wake the server's dispatcher, so a mail comes within seconds. Each wait is below the test's timeout.
const MAIL_WAIT_MS = 100_000;
const SENT = 'Check your inbox. We sent a message to this address.';

function unique(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1_000_000)}`;
}

test('a private person signs up through the mail, works alone, deletes the account and is back on the sign-in screen', async ({ page }) => {
  test.setTimeout(240_000);
  const email = `${unique('private')}@e2e.test`;
  const password = setting('E2E_USER_PASSWORD');
  const note = `Private note ${unique('n')}`;

  // Both kinds are on (samples/notes-api/auth.yaml): the sign-up screen offers the choice.
  await page.goto('/login');
  await page.getByRole('link', { name: 'Create an account' }).click();
  await expect(page).toHaveURL(/\/signup$/);
  await page.getByLabel('Private person').check();
  await expect(page.getByLabel('Company name')).toHaveCount(0);
  await page.getByLabel('Email').fill(email);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByTestId('signup-sent')).toHaveText(SENT);
  expectNoEmailInAddressBar(page);

  // The link opens the private completion: the address, the password twice, no company.
  const link = await waitForLink(email, 'complete-signup', MAIL_WAIT_MS);
  await openMailLink(page, link);
  await expect(page).toHaveURL(/\/complete-signup$/);
  expectCleanAddressBar(page);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  await expect(page.getByLabel('Company name')).toHaveCount(0);
  await page.getByLabel('New password').fill(password);
  await page.getByLabel('Repeat the password').fill(password);
  await page.getByRole('button', { name: 'Create account' }).click();

  // Signed in at once, alone: "Private account", no company, no Team.
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-private')).toHaveText('Private account');
  await expect(page.getByTestId('me-company')).toHaveCount(0);
  await expect(page.getByTestId('team-link')).toHaveCount(0);
  await page.getByLabel('New note').fill(note);
  await page.getByRole('button', { name: 'Add note' }).click();
  await expect(page.getByText(note)).toBeVisible();

  // "Delete account": the warning, the password, and the sign-in screen with the notice.
  await page.getByTestId('delete-account').click();
  await expect(page.getByTestId('delete-warning')).toHaveText('This deletes your account and all your notes. It cannot be undone.');
  await page.locator('#delete-password').fill(password);
  await page.getByRole('button', { name: 'Delete my account' }).click();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByTestId('login-notice')).toHaveText('Your account was deleted.');
  expect(await loginStatus(email, password), 'the deleted account still signs in').toBe(401);
});

test('a private person accepts an invitation through the warning and signs in to the company', async ({ page }) => {
  test.setTimeout(240_000);
  const email = `${unique('moving')}@e2e.test`;
  const password = setting('E2E_USER_PASSWORD');
  await signUpPrivatelyByApi(email, password, MAIL_WAIT_MS);
  await inviteByApi(email, 'user');

  const link = await waitForLink(email, 'invite', MAIL_WAIT_MS);
  await openMailLink(page, link);
  await expect(page).toHaveURL(/\/invite$/);
  expectCleanAddressBar(page);
  await expect(page.getByTestId('invite-private-warning')).toContainText('You already have a private account with this email address.');
  await expect(page.getByLabel('New password')).toHaveCount(0);
  await page.getByRole('button', { name: 'Move me to the company and delete my private account' }).click();

  // The account keeps its password: the sign-in screen, with the address filled in, signs it in to the company.
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-private')).toHaveCount(0);
  await expect(page.getByTestId('me-company')).toBeVisible();
  await expect(page.getByTestId('me-role')).toHaveText('user');
});

test('an admin removes a member on /team after the confirmation that names the deletion', async ({ page }) => {
  test.setTimeout(240_000);
  const email = `${unique('member')}@e2e.test`;
  const password = setting('E2E_USER_PASSWORD');
  await inviteByApi(email, 'user');
  await acceptInviteByApi(await waitForLink(email, 'invite', MAIL_WAIT_MS), password);

  await signIn(page, setting('E2E_SEED_EMAIL'), setting('E2E_SEED_PASSWORD'));
  await page.getByTestId('team-link').click();
  await expect(page).toHaveURL(/\/team$/);
  const row = page.getByTestId('member').filter({ hasText: email });
  await row.getByTestId('remove').click();
  await expect(row.getByTestId('remove-confirmation')).toHaveText("This person's account will be deleted.");
  await row.getByTestId('remove-confirm').click();
  await expect(page.getByTestId('member').filter({ hasText: email })).toHaveCount(0);
  expect(await loginStatus(email, password), 'the removed member still signs in').toBe(401);
});
```

- [ ] **Step 4: Check them without a stack.** `bash -n scripts/e2e-private.sh scripts/e2e-signup.sh scripts/e2e-tenancy.sh scripts/e2e-hardening.sh scripts/e2e-web.sh`;
`grep -c $'\r' scripts/e2e-private.sh` prints `0`; in `samples/notes-web`: `npx tsc -p e2e/tsconfig.json --noEmit` and
`npx playwright test --list` (it lists the tests and runs none) both succeed, and `npm test` still passes. Do not start a stack and do not run
the scripts or Playwright: the verifiers do (criteria 34 and 35).

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files; the orchestrator marks `scripts/e2e-private.sh` executable.
Scan the touched files. Proposed subject: `test(e2e): the live checks of private accounts; the earlier checks follow spec 0010`.

### Task 18: The documents

**Files:**
- Modify: `docs/security/threat-model.md`, `docs/integration/angular.md`, `docs/integration/python-fastapi.md`, `docs/operations/backup.md`, `docs/deployment/vps.md`, `docs/design.md`, `CHANGELOG.md`, `README.md`, `clients/python/README.md`, `samples/notes-web/README.md`

**Interfaces:**
- Consumes: what Tasks 1 to 17 built (the test names below exist once they are done).
- Produces: the documents of spec 0010 → Documents (criterion 36); `docs/superpowers/plans/0010-acceptance-map.md` is checked, not written (the plan wrote it).

**Contract counts:** none.

**Decisions the plan takes here:**
- Spec 0009 is not edited: spec 0010 says what it replaces (Decision 9), and an accepted spec changes only with the owner.
- The angular guide keeps its seven numbered steps (`npm run check:docs` counts them); spec 0010 gets a section of its own after them.

Every sentence below is written out; "replace" means the text between the backticks exactly as it stands in the file (read the file first:
a long line is quoted by its start and end where it is unique).

- [ ] **Step 1: The threat model.** In `docs/security/threat-model.md`:

(a) In the row that starts `| I | Sign-up used to learn who has an account |`, replace `SignupRequestTests.An_account_without_a_company_gets_the_same_status_headers_body_and_statements` with
`SignupRequestTests.A_private_account_gets_the_same_status_headers_body_and_statements`, `PrivateSignupRequestTests.A_private_request_runs_the_same_statements_and_answers_the_same_for_any_address`, and replace
`A holder of a valid link sees `409 already_registered` at the completion and `has_account` in the preview (spec 0009, Amendment 3); the link comes only from the mailbox.` with
`A holder of a valid link sees `409 already_registered` at the completion; the link comes only from the mailbox.`

(b) Replace the whole row that starts `| S | A sign-up link used to open an account that is someone else's (spec 0009, Amendment 3: a person without a company comes back by sign-up) |` with

```markdown
| S | A sign-up link used to reach an account that is someone else's | No link reaches an existing account (spec 0010, Decision 9): any account under the address, of either kind and under any spelling the normaliser folds together, gets the notice and no link (`PrivateSignupDispatchTests.An_address_with_a_private_account_gets_the_account_exists_mail_with_the_new_sentence`), and the completion is `409 already_registered` whatever the password (`PrivateSignupCompleteTests.An_address_that_has_any_account_by_then_is_409_and_the_pending_sign_up_is_gone`, `SignupCompleteTests.An_account_under_another_spelling_of_the_address_is_409_too`); a request that changes the kind, or types a look-alike spelling, voids the link already mailed (`PrivateSignupRequestTests.A_request_that_changes_the_kind_voids_the_earlier_link_at_once`, `SignupDispatchTests.A_request_for_a_look_alike_spelling_drops_the_earlier_link_even_when_its_mail_fails`) | Whoever can read the mailbox can already reset the password (spec 0004), so the link adds no capability |
```

(c) In the row that starts `| S | A mail link that opens a session (spec 0009, Amendment 2) |`, delete ` (a comeback chooses none: the account keeps its own, Amendment 3)`.

(d) In the row that starts `| T | Deleting a company by mistake or by a hijacked session |`, replace
`the deletion runs in one transaction under the company lock and revokes every member's sessions (`CompanyDeletionTests`)` with
`the deletion runs in one transaction under the company lock and deletes every member's account, the caller's included, with its sessions (spec 0010; `CompanyDeletionTests`, `AccountDeletionOnRemovalTests.Deleting_a_company_by_the_api_deletes_every_members_account_the_callers_included`)`,
and `A company admin holding every permission can delete the company with their password; there is no undo but the backup` with
`A company admin holding every permission can delete the company, and with it the accounts of its members, with their password; there is no undo but the backup`.

(e) In the row that starts `| R | Actions that leave no trace |`, replace `The audit log: 24 kinds,` with `The audit log: 26 kinds,`.

(f) In the row that starts `| D | Resource exhaustion through the database or the mail queue |`, replace
`(an address without an account; for a sign-up, one with neither a live pending sign-up nor an account that belongs to a company)` with
`(an address without an account; for a sign-up, one whose live pending sign-up is of a kind that is switched off, or that has no live pending sign-up and no account, or both switches off)`.

(g) In the row that starts `| E | A session survives its logout, or its company |`, replace
`the members of a deleted company are treated as removed members: `403 permissions_changed` at the company API, `401 invalid_grant` at refresh, `403 no_membership` at login (`OrgDeleteEndpointTests`)` with
`a removed member's account, and every account of a deleted company, is deleted (spec 0010): `403 permissions_changed` at `/auth/me` and at every company endpoint whose permission the token claims, `401 invalid_grant` at refresh, the `401` of an unknown address at login (`OrgDeleteEndpointTests`, `AccountDeletionOnRemovalTests`)`.

(h) After that row add

```markdown
| E | A private account reaches the company API, or the operator acts on a personal space | Every endpoint under `/auth/org` refuses a personal space, read from the database whatever the token says, `GET /auth/org` included (`PersonalSpaceRefusalTests`); the company lock locks companies only, so every company service answers `not_found` for a personal space, the CLI too, and `list-orgs` does not list them (`PersonalSpaceCliTests`); the private role never holds a built-in permission (`ManifestPrivateTests`) | |
| S | An access token alone deletes an account | `DELETE /auth/account` needs the password, which counts in the login streak like a failed login and is refused while the identifier is locked (`AccountDeletionEndpointTests`); it serves private accounts only, and a company account is managed by the company's admins; the account is read again under the address lock (`AccountDeletionEndpointTests.A_deletion_that_finds_the_space_no_longer_personal_under_the_lock_is_permissions_changed`, `PrivateAccountRaceTests`) | Whoever knows the password and holds a live access token can delete the account: that is what the endpoint is for |
| T | An invitation takes a private account over by surprise | Accepting needs `leave_private_account: true`; without it nothing changes (`409 private_account_exists`); the mail and the preview say what accepting does (`PrivateInvitationTests`, `PrivateInvitationMailTests`) | The product's data of the old space stays behind (risk 48) |
```

(i) Before the heading `## Residual risks` add

```markdown
## Account deletion and personal data

Spec 0010 deletes an account in four places: a private person deletes their own (`DELETE /auth/account`), an admin removes a member, a
company is deleted (every member's account, the admin's included), and a start deletes the accounts an earlier version left without a
company. One routine does it, in the transaction of the change (`AccountDeletionTests`).

- **What goes:** the account and what Identity holds for it (linked logins, claims, stored tokens); its membership, and a personal space
  with its role row; its refresh tokens and their authorizations (deleted, not only revoked); its mail links, the queued mail requests of its
  address other than invitations, the pending sign-up of its address and its login streak.
- **What stays, and why:** the audit rows that name the account, its address and the client addresses, for their retention (90 days by
  default), on the legitimate interest of security; they are not anonymised. The invitations of companies to the address belong to those
  companies. The mail-limit counters hold a hash of the address and expire within hours.
- **Backups** keep everything until they are rotated, and a restore brings deleted accounts back ([backup and restore](../operations/backup.md)).
- **The product's data is the product's to delete:** Auth-Core does not tell the product that an account is gone. The product's frontend
  calls the product's own "delete my data" endpoint first and `DELETE /auth/account` after it (the sample's `DELETE /api/me`;
  [Angular guide](../integration/angular.md), [Python guide](../integration/python-fastapi.md)). A wrong password after the data is gone
  leaves the account without its data; the person asked for the deletion.
- **The invitation move:** a private account that accepts an invitation with `leave_private_account: true` keeps its address and password
  and becomes a member; its personal space is deleted in Auth-Core, and the product's rows under that space's `org_id` stay (risk 48).
```

(j) In the paragraph under `## Residual risks`, replace
`Items 1 to 46 are the risks accepted by specs 0002 to 0009 (41 to 44 by Decision 15; 45 is what Decision 14 leaves; 46 is spec 0009, Decision 11).` with
`Items 1 to 50 are the risks accepted by specs 0002 to 0010 (41 to 44 by Decision 15; 45 is what Decision 14 leaves; 46 is spec 0009, Decision 11; 47 is spec 0009's; 48 to 50 are spec 0010's).`;
and after item 47 (the line that starts `47. **Addresses that the normaliser folds into one key can be squatted.**`) add

```markdown
48. **A product's data in a personal space that moved to a company stays behind.** It stays in the product's tables under an `org_id` that no token will carry again: Auth-Core has no deletion feed to tell the product, and the person is not signed in on the invitation screen (spec 0010, Decision 16).
49. **An admin who removes a member, or deletes a company, now deletes the person's account.** The person can be invited again or sign up, as a new account (spec 0010, Decision 9).
50. **A deleted person's personal data outlives the account for a while.** Audit rows keep the address and the client addresses for 90 days; backups keep everything until they are rotated; a restore brings deleted accounts back (spec 0010, Decision 10).
```

(k) In the row that starts `| E | Two ways to make one account |`, replace
`(`SignupCompleteRaceTests`, which also holds the comeback of an account without a company against an acceptance for it)` with
`(`SignupCompleteRaceTests`; for a self-deletion, an acceptance and a completion for one address, `PrivateAccountRaceTests`)` (the comeback's race test is deleted with Amendment 3 of spec 0009).

- [ ] **Step 2: The Angular guide.** In `docs/integration/angular.md`:

(a) Replace `A person whose account exists and belongs to no company (the preview's `has_account`, spec 0009 Amendment 3) chooses no password: pass `null` as the password and the body holds the token and the company name only.` with
`A private sign-up (spec 0010) names no company: pass `null` as the company name and the body holds the token and the password only.`

(b) Replace `` `AuthService.me()` (email, company, roles, permissions) `` with `` `AuthService.me()` (email, company, `org_kind`, roles, permissions) ``.

(c) Replace `export const TOKEN_PATHS: readonly string[] = ['/auth/me'];` with `export const TOKEN_PATHS: readonly string[] = ['/auth/me', '/auth/account'];`, and after the code block
that holds it add the sentence `` `/auth/account` is the self-deletion of a private account (spec 0010): it needs the token too. ``

(d) In the sentence before the table of step 4 that quotes `{"signup":"self-service"}`, replace that literal with `{"signup":"self-service","private_signup":"self-service"}` and add after the
sentence: `` `private_signup` says whether a private person may sign up alone (spec 0010). ``

(e) In the table of step 4, replace the row of `/signup` with

```markdown
| `/signup` | "Organization / Private person" when `GET /auth/options` says both are on (with one on, that one); the email, and for an organization the company name; always "Check your inbox"; the wait on a `429`; "closed" on `signup_disabled` | `POST /auth/signup` (`kind` `private` for a private person, with no `company_name`) |
```

the row of `/complete-signup` with

```markdown
| `/complete-signup?token=` | the address (read-only); for an organization the company name (editable); the password, twice; the rules it did not meet; "link used up" with a way to sign up again; "an account already exists" with a way to sign in; on success the person is signed in and the notes screen opens | `POST /auth/signup/preview` (it says `kind`), `POST /auth/signup/complete` (answers as `POST /auth/login` does: the access token and the refresh cookie) |
```

and the row of `/invite?token=` with

```markdown
| `/invite?token=` | "Join {company} as {role}" and the email; the password, twice; for an address with a private account (the preview's `has_private_account`) the warning in red instead and one button, "Move me to the company and delete my private account", which sends `leave_private_account: true` and no password | `POST /auth/invites/preview`, `POST /auth/invites/accept` |
```

(f) Before the heading `## The limits and an outage (spec 0008)` add

```markdown
## Private accounts (spec 0010)

When your manifest says `private.signup: self-service`, a person can sign up alone. Your screens tell the two kinds apart by `me().org_kind`:
`personal` for a private account (its `org_name` is `private` and its role `private`), `organization` for a company member. The sample shows
"Private account" and no company or role to a private account, and "Team" only to a company member who holds `members:manage`
([`pages/notes.ts`](../../samples/notes-web/src/app/pages/notes.ts)).

- **"Delete account"** is for a private account only. Delete your product's data first, with an endpoint of your own backend that deletes
  what belongs to the token's `org_id` (the sample's `DELETE /api/me`), and call `auth.deleteAccount(password)` (`DELETE /auth/account`) only
  after its `204`: Auth-Core does not tell your product that an account is gone. Warn first ("This deletes your account and all your notes. It
  cannot be undone."), ask for the password on the same screen, and on `403 wrong_password` say what happened: the data is gone, the account
  is not. A `429 too_many_attempts` is the lockout: show the wait. On success open the sign-in screen.
- **An invitation to a private account:** when the preview says `has_private_account`, show the warning (the paragraph of the mail) and one
  button that sends `leave_private_account: true` with no password: the account keeps its own. Without it the acceptance answers
  `409 private_account_exists` ([`pages/invite.ts`](../../samples/notes-web/src/app/pages/invite.ts)).
- **Removing a member deletes the account,** and deleting a company deletes every member's, the admin's included. Say so in the confirmation
  of your own team screen, with the sample's sentence: "This person's account will be deleted." ([`pages/team.ts`](../../samples/notes-web/src/app/pages/team.ts)).
  The person can be invited again, as a new account.
- **Personal data after deletion:** Auth-Core deletes the account and what it holds of it, keeps the audit rows that name it (its id, its
  address and the client addresses) for their 90 days on the legitimate interest of security, and backups keep everything until they are
  rotated ([threat model](../security/threat-model.md), "Account deletion and personal data"). Your product's data is yours to delete.
```

- [ ] **Step 3: The Python guide.** In `docs/integration/python-fastapi.md`:

(a) Replace `Two FastAPI dependencies, each giving a `Principal` (`sub`, `org_id`, `roles` as a tuple, `permissions` as a frozenset):` with
`Two FastAPI dependencies, each giving a `Principal` (`sub`, `org_id`, `roles` as a tuple, `permissions` as a frozenset, and `org_kind`, `"organization"` or `"personal"`):`

(b) After the sentence that ends `` `sub` and an `org_id` (non-empty strings) and `roles` and `permissions` (arrays of strings). No other algorithm is
accepted. A clock skew of 5 minutes is allowed, the same as Auth-Core allows itself. `` add the paragraph

```markdown
`org_kind` (spec 0010) may be absent: a token of an Auth-Core before it is a company member's, and reads as `"organization"`. When it is
there it must be `"organization"` or `"personal"`; anything else is a `401`.
```

(c) Before the heading `## Check that it works` add

````markdown
## Private accounts and a person's data (spec 0010)

A private account (`user.org_kind == "personal"`) has a space of its own, its `org_id`, which nobody else joins: filter by `org_id` as for a
company, and give it what your manifest's `private.permissions` grant. Before such a person deletes their account (`DELETE /auth/account`,
which your frontend calls), your frontend calls an endpoint of yours that deletes everything under the token's `org_id`: Auth-Core does not
tell your backend. The sample's ([`app.py`](../../samples/notes-api/src/notes_api/app.py)):

```python
@app.delete("/api/me", status_code=204)
def delete_my_data(user: Principal = Depends(auth.current_user)):
    if user.org_kind != "personal":
        return JSONResponse({"error": "forbidden"}, status_code=403)   # a company's data belongs to the company
    with Session(engine) as session:
        session.execute(delete(Note).where(Note.org_id == uuid.UUID(user.org_id)))
        session.commit()
    return Response(status_code=204)
```

No permission is needed: a person may always delete their own data. Answer `204` also when there is nothing to delete.

- An admin who removes a member, or deletes the company, deletes the person's account. A team screen of your product should say so: "This
  person's account will be deleted."
- A private account that accepts an invitation moves into the company; the data of its old space stays in your tables under an `org_id` no
  token will carry again. Auth-Core has no deletion feed yet.
- Personal data after deletion: Auth-Core keeps its audit rows, with the account's id, address and client addresses, for 90 days on the
  legitimate interest of security, and backups until they are rotated ([threat model](../security/threat-model.md), "Account deletion and
  personal data"). Your product deletes its own data.
````

- [ ] **Step 4: The runbooks.** In `docs/operations/backup.md`:

(a) Before the heading `## Reading the audit log` add

```markdown
**A restore brings deleted accounts back.** An account deleted after the backup was made (a self-deletion, a removal or a company deletion,
spec 0010) is there again after the restore, as it was at the backup. Tell the people concerned, or delete those accounts again (remove the
member, or have the person delete the account).

```

(b) Replace `, and `"existing_account": true` on the `signup.completed` row of a person who came back with an account that had no company` with
``, and `kind` (`organization` or `private`) on `signup.requested` and `signup.completed` (spec 0010)``;
replace `` `role` on `invite.sent`, `invite.resent`, `invite.accepted` and `invite.cancelled`; `` with
`` `role` on `invite.sent`, `invite.resent`, `invite.accepted` and `invite.cancelled`, and `left_private_account` and `personal_org_id` on the `invite.accepted` row of a private account that moved; ``;
and replace `` `reason` on `login.failed` and
`org.delete_refused`; `` (it spans a line break: keep the break) with `` `reason` on `login.failed`, `org.delete_refused`, `account.delete_refused` (`wrong_password`, `locked`) and
`account.deleted` (`self`, `member_removed`, `org_deleted`, `upgrade`); ``.

(c) In `docs/deployment/vps.md` (the sign-up section), replace
`An address whose account belongs to a company gets a mail that says so, with no link; a person whose account has no company (the company was deleted, or they were removed from it) gets the usual link, finishes with a company name only and keeps their password; every address gets the same answer.`
with
`Any account under the address, of either kind, gets a mail that says so, with no link; every address gets the same answer. The block `private` of the manifest (`signup: self-service` and `permissions`) turns private sign-up on (spec 0010): a person signs up alone, with an email address only, into a space nobody else joins; it needs the same `AUTH_FRONTEND_SIGNUP_URL`. Removing a member, or deleting a company, deletes the accounts of the people concerned (spec 0010).`

- [ ] **Step 5: The design.** In `docs/design.md`, after the paragraph that starts `*As built (spec 0009):*` add

```markdown
*Spec 0010:* a private person signs up with an email address only and works in a hidden one-person space; the access token says
`org_kind`. A private person deletes their own account (`DELETE /auth/account`) after the product has deleted its data; an invitation warns a
private person and moves the account only on purpose; an account that loses its only space is deleted (a removal, a company deletion, once
at upgrade). The comeback of spec 0009, Amendment 3, was removed before any release.
```

- [ ] **Step 6: The changelog.** In `CHANGELOG.md`:

(a) After the paragraph under `## Unreleased` (the two lines that start `Self-service sign-up ([spec 0009]`) add

```markdown

Private accounts ([spec 0010](docs/superpowers/specs/0010-private-accounts.md)): a product can let a private person sign up alone, and a
private person can delete their account. Off by default; an instance that does not turn it on sees `org_kind: "organization"` in its tokens and
`GET /auth/me`, `"private_signup":"off"` in `GET /auth/options`, and accounts deleted instead of kept when they lose their company.
```

(b) Delete the bullet that starts `- **A person without a company comes back by sign-up** (Amendment 3).` (it was never released).

(c) After the bullet that starts `- **`scripts/e2e-signup.sh`**:` add

```markdown
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
```

(d) After the line `- The guides, the threat model (residual risk 46: sign-up mails any well-formed address) and the audit runbook describe the feature.` add

```markdown
- **An account that loses its only space is deleted** (spec 0010): removing a member (`204` as before) and deleting a company (every member,
  the admin included) delete the accounts, with `account.deleted` rows; a login is then the `401` of an unknown address, and the address can
  be invited again or sign up. The CLI says so ("Member removed; the account is deleted.", "Company deleted; the accounts of its members are
  deleted.").
- **"You already have an account"** goes to any account under the address, of either kind, and gains a sentence: how to get a new account on
  purpose. The comeback of an account without a company by sign-up (spec 0009, Amendment 3: `has_account`, the completion without a password,
  `details.existing_account`) is removed before any release.
- `POST /auth/invites/accept`: an account that exists keeps its password (a `password` sent is not used); a missing password with an unknown
  token is now `400 invalid_token`. `POST /auth/signup/complete`: the company name is required for an organization and refused for a private
  sign-up, judged after the token.
```

(e) Replace the paragraph under `### Upgrading` with

```markdown
Two migrations: the first adds the table `PendingSignups`; the second adds `Kind` to companies and pending sign-ups (every existing row is a
company, or an organization sign-up). The service applies them at start. At the first start of this version every account without a
membership (one an earlier version kept after a removal or a company deletion) is deleted, each with an `account.deleted` row whose
`details.reason` is `upgrade`. Nothing else is required: without the manifest keys and the address, neither kind of sign-up is on and no
setting is new and required.
```

- [ ] **Step 7: The README files.**

`README.md`:
(a) After the paragraph that ends `so add the line to your manifest only when you have the address.` add
`` The block `private` (`signup: self-service` and `permissions`) lets a private person sign up alone, with an email address only, into a space nobody else joins; it needs the same address, and your backend tells the two kinds apart by `org_kind` (spec 0010). ``
(b) Replace `; a person whose company was deleted, or who was removed from it, comes back the same way and keeps their password` with nothing (delete it).
(c) In the line that starts `scripts/e2e-signup.sh`, replace `a member removed who comes back` with `a removed member's address signs up again as a new address`; after that line add
`scripts/e2e-private.sh          # private sign-up, /auth/me, the company API refused, the invitation move, self-deletion, removal (~3 min)`.
(d) In the operator commands, replace `# --force overrides last_manager` (the line of `auth remove-member`) with `# --force overrides last_manager; the account is deleted`,
and `# deletes the company; the name must be exact` (the line of `auth delete-org`) with `# deletes the company and its members' accounts`.

`clients/python/README.md`: replace `the claims `sub`, `org_id`, `roles` and `permissions`` with
`the claims `sub`, `org_id`, `roles`, `permissions` and, when present, `org_kind``, and after the code block of the file's first example add
`` `Principal.org_kind` is `"personal"` for a private account (Auth-Core spec 0010) and `"organization"` for a company member, also for a token without the claim. ``

`samples/notes-web/README.md`: in the line that starts `src/app/pages/`, replace `complete-signup, notes` with `complete-signup, notes, team`; replace
`There is no PWA and no screen for company admins; see the deferred list of the spec.` with
`There is no PWA, and the only screen for company admins is `/team` (the members, and "Remove" with the confirmation that the account will be deleted); see the deferred list of the spec.`
and add at the end of that bullet: `` With private sign-up on (spec 0010) the sign-up screen offers "Organization / Private person", and a private account gets "Delete account" on the notes screen (`DELETE /api/me` first, then `DELETE /auth/account`). ``

- [ ] **Step 8: Check them all.** From the repository root: `C:/p6v/Scripts/python.exe scripts/check-docs.py` (the default list),
`C:/p6v/Scripts/python.exe scripts/check-docs.py --self-test`,
`C:/p6v/Scripts/python.exe scripts/check-docs.py docs/superpowers/plans/0010-acceptance-map.md docs/integration/python-fastapi.md docs/operations/backup.md docs/deployment/vps.md samples/notes-web/README.md docs/design.md`
(every class, method and file named must resolve; a name the map gives that a task did not build is a finding: report it); in `samples/notes-web`:
`npm run check:docs`. `grep -rn "has_account\|existing_account\|comes back" README.md CHANGELOG.md docs/integration docs/security docs/operations docs/deployment samples/notes-web/README.md`
finds only the CHANGELOG's sentence that names what was removed.

- [ ] **Step 9: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`docs: private accounts in the guides, the threat model, the runbooks, the design and the changelog`.

---

## Self-review

Run against the spec, section by section, after the tasks above were written.

**1. Spec coverage.**

| Spec section | Task |
| --- | --- |
| Goal, the sequence of curl calls | 9, 10, 11 (sign-up), 3 (`org_kind`), 4 (`/auth/org` `403`), 13 (`DELETE /auth/account`); 17 (`scripts/e2e-private.sh`, steps 1 to 6) |
| General rules (two switches, blank password) | 2 (switches), 9, 11, 12, 13 (bodies) |
| Spaces | 1 (kinds, personal space), 3 (the private role's live permissions) |
| Manifest | 2 |
| `GET /auth/options` | 2 |
| `POST /auth/signup` (two-phase body, kind change voids the link) | 9 |
| Mails (switch first, any account, private variant, the new sentence) | 5 (texts), 10 (dispatcher) |
| `POST /auth/signup/preview`, `/complete` | 11 |
| Pending sign-ups | 1 (columns), 9 (the kind, the null company) |
| Access token and `GET /auth/me` | 3 |
| The company API and the operator CLI | 4 |
| Invitations (mail, preview, accept) | 5 and 10 (mail), 12 |
| `DELETE /auth/account` | 13 |
| Accounts are never left without a space; What an account deletion removes; Locks | 6 (routine), 7 (removal, company deletion), 8 (start-up), 12 (the move), 13 (self-deletion, races) |
| Upgrade | 8 |
| Limits | 9 (the shared mail limit: one counter), 13 (`general` policy) |
| Audit events | 6 (kinds), 7, 8, 9, 11, 12, 13 (rows) |
| OpenAPI | 2, 3, 9, 11, 12, 13 (31 operations, 21 under the charset guard) |
| Python package | 14 |
| Sample frontend | 15, 16 |
| Sample backend | 14 |
| Documents, residual risks 48 to 50 | 14 (`deploy/auth.yaml`), 18 |
| Acceptance criteria 1 to 36 | [`0010-acceptance-map.md`](0010-acceptance-map.md) names a test or a step for each |
| Decisions 1 to 12 | 1 (2), 2 (3, 4), 3 (1, 2), 4 (3), 9 to 11 (5, 6), 12 and 10 (7), 13 (8), 7, 8, 12 (9), 18 (10), 15 to 17 (11), 12 (12: the warning, not a refusal) |
| Settled 1 to 15 | 1 (1, 2), 9 (3, 4), 11 (5, 6), 12 (7), 6 and 11 (8), 13 (9, 12), 8 (10), 6 (11), 14 (13), 16 (14), 4 (15) |
| Accepted 16 and 17 | 12 (the space is deleted, the product's rows stay; the threat model's risk 48, Task 18), 16 (the text on `wrong_password`) |
| Amendment items 1 to 8 | 13 (1), 7 (2: `A_removed_persons_token_gets_permissions_changed_where_it_claims_and_forbidden_where_it_never_did`), 7, 12, 13 (3), 6 (4), 7 (5: rows counted, never sorted), 9, 11 to 13 (6), 4 (7), 9 (8: the two-phase reader) |
| To verify (settled) | start-up step: 8; deleted token entry: 6 (tokens before authorizations) and 7 (`invalid_grant`); lock order: 7, 8, 12, 13; Identity's delete: 6; the migration: 1; the dispatcher's two statements: 10; `JsonObjectBody`: 9, 12 |
| To verify (still open): the Amendment 3 removal list | 10 (tests and the dispatcher branch), 11 (the service, `has_account`, `existing_account`, `SignupAccount.MayStartCompany`), 12 (the password replacement in `InviteAcceptance`), 15 (the sample's `keepsPassword` and the screen), 17 (step 12 of `e2e-signup.sh`, the COMEBACK seed and test), 18 (the changelog item, the guides and the threat model) |
| Out of scope | nothing built: no providers, no deletion feed, no self-deletion of a company account, no operator deletion command, no kind conversion other than the invitation move |

**2. Placeholder scan.** No "TBD", "similar to", "as above" or step without its code. Edits of existing files quote the text to find and give
the text to put; two edits name a line by its start where the file's line is long (`README.md` script list, the threat model rows), and Task 18
writes every sentence it adds. Two steps name a fallback for a statement the plan could not prove on paper (the `byte[]` list of Task 6, the
untyped `NULL` of Task 9); each says to report it.

**3. Type consistency.** `CompanyKind`/`SignupKind` (Task 1) are used by Tasks 2 to 13 with the same members; `TenantContext.Kind` (Task 3) by
Tasks 4, 12 and 13; `SignupGate.IsOn/PrivateOn/AnyOn/IsOnFor/PrivateModeName` (Task 2) by Tasks 9 to 11; `Manifest(..., privateSignup,
privatePermissions)` by Tasks 3, 10 and 11; `AccountDeletion.DeleteAsync(Guid | IReadOnlyCollection<Guid>, AccountDeletionReason, Actor?,
CancellationToken)` (Task 6) by Tasks 7, 8 and 13; `AddressLock.AcquireAsync` (Task 8) by Tasks 11, 12 and 13; `JsonFields`/`JsonBoolean`
(Task 9) by Task 12; `PendingSignupSeed.IssueAsync(services, email, string?, SignupKind = Organization)` (Task 9) by Tasks 6, 11 and 13;
`SignupApi.RequestPrivate` (Task 10) and `CompletePrivate` (Task 11) by Tasks 11 and 13; `TenancyApi.AcceptLeaving` (Task 12) by Task 13;
`HeldLock` (Task 13); `SignupPreview` (C#: Task 11; TypeScript: Task 15); `InvitePreview.has_private_account` and `acceptInvite(token, password
| null, leave)` (Task 16); `Me.org_kind` (Task 15) by Task 16; `privateMe` (Task 15) by Task 16.

**4. Anchors.** Every "replace" of an existing file was checked against `272d9b4` while writing; the anchors of a file that an earlier task of
this plan changes are written against the text that task leaves (`ShippedConfigurationTests` in Task 14 after Task 2; `OpenApiTests` in
Tasks 3, 9, 11, 12 and 13 in that order; `SignupApi.cs` in Task 11 after Task 10; `auth.service.ts` in Task 16 after Task 15).

**5. Review Focus.** Each of the five has its named tests in the tasks it points at.

## Decisions taken in this plan

Apart from the owner's ruling on the audit rows (the spec now says the rows of a change are written in the same transaction, with no claim
of an equal time: Accounts are never left without a space, and Amendment item 5), the spec is not changed by this plan. One line each; none
of them changes the contract it states, and each is covered by a test.

1. **The CLR default of a new `Kind` is EF's sentinel** (`0`, said with `HasSentinel` so that EF logs no warning at each start, rather than silencing the warning): code that does not set the kind sends no value and the database's default
   applies; only a private sign-up sets `Personal`/`Private` (Task 1).
2. **The stored manifest writes the block `private` only when it says something,** so the stored form of a manifest of slice 0009 is unchanged (Task 2).
3. **Between Tasks 2 and 11 the endpoints still follow the organization switch**; each task leaves the tree green, and the kind reaches the
   request in Task 9, the mails in Task 10 and the preview and completion in Task 11.
4. **The routine is set-based and lock-free:** the caller owns the transaction and the locks; company deletion hands it every member at once,
   so a company of any size costs a fixed number of statements (Tasks 6 and 7).
5. **One address-lock helper** (`AddressLock`) for the start-up step, the completion, the acceptance and the self-deletion (Task 8 on).
6. **The CLI's success lines name the deletion of the accounts** (Task 7).
7. **An account with no membership at all** (only a direct change of the database leaves one) **joins at acceptance with the password it
   has,** like a private account that moves but with no space to delete and without `leave_private_account` (Task 12).
8. **The move and the account without a space get a new security stamp,** so a login that raced the acceptance cannot refresh into the
   company (Task 12).
9. **`DELETE /auth/account`:** a token that does not say `personal` while the database shows a personal space is let through (the database
   decides); an account gone between the first read and the password is `403 permissions_changed`; the deletion's transaction does not use the
   request's cancellation (Task 13).
10. **`signup.completed` of a private sign-up names the personal space** (`org_id`) and `private` (`org_name`), as its `login.succeeded` row
    does (Task 11).
11. **`kind` follows the string rule of every member:** `null`, `""`, a number or any other text is `400 invalid_request` (Task 9).
12. **Spec 0009 is not edited;** spec 0010 says what it replaces (Task 18).
13. **The sample:** the delete form is a section of the notes screen (after the notes, so the notes form stays the first form); the team
    screen's confirmation is inline; an acceptance that meets `409 private_account_exists` asks the preview again (Task 16).
14. **The live checks:** `scripts/e2e-private.sh` copies the helpers of `e2e-signup.sh` and runs after it on the same stack; the Playwright
    spec makes its own accounts and invitations through the API (Task 17).
15. **The Python package keeps its version;** the release is part of `v0.2.0` (Task 14).
16. **The dispatcher picks a sign-up row without the switches** and drops a row whose kind is off when it comes up, in the same pass; only
    the bulk delete at the start of a pass carries the switches (Task 10).
17. **A completion body with no company name and a token never issued is `400 invalid_token`:**
    `SignupCompleteSignInTests.Every_refusal_is_what_it_was_and_issues_no_cookie_and_no_token` expects it, rather than sending a body that is
    malformed whatever the token (Task 11).
18. **A doc comment that spans a line break is replaced as the lines stand,** quoted whole, rather than by the start of its sentence
    (Tasks 10 and 11).
19. **A line that is in two places is named by its method or by being exactly that line** (`ShippedConfigurationTests`, `deploy/auth.yaml`:
    Task 14; `AuditAtomicityTests`: Task 7).
20. **`PrivateSignupRequestTests` leaves out the two pruning services in its constructor,** as `SignupRequestTests` does, rather than moving
    its statement test into that class (Task 9).
21. **The sample backend's `405` test probes each path with a method it does not have,** the first of `DELETE`, `PATCH` and `PUT` (Task 14).

## Open questions for the owner

None.
