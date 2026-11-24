# Create Another Company Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0016
- **Date:** 2026-11-22
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0016-create-another-company.md`](../specs/0016-create-another-company.md) — the plan argues from the spec; where they
  disagree, **the spec wins** and the disagreement is a finding (see [`docs/workflow.md`](../../workflow.md)). The choices the spec leaves open
  are in "Decisions taken in this plan" at the end.
- **Based on:** `6b12d25` (`main`, with spec 0015 built and merged by PR #7). Every anchor below was checked against this base.
- **Acceptance map:** [`0016-acceptance-map.md`](0016-acceptance-map.md).
- **Branch:** `feat/0016-create-another-company` from `main`. Spec 0017 is built on top of this branch (it needs the `company_member` answer and
  the recount of Task 1).

**Goal:** A signed-in person creates another company with `POST /auth/orgs` and becomes its first admin, when the product's manifest turns it on
(`org_creation: self-service`); an account that holds a personal space and a company can no longer delete itself (`403 company_member`).

**Architecture:** A new manifest key (`org_creation`, off by default) is read into `Manifest` and shown in `GET /auth/options`. A per-account
limit (`Auth:Tenancy:MaxCompaniesPerAccount`, 20) is a settings object loaded by the service only. `POST /auth/orgs` is one endpoint that checks
the token, the switch and the body, then in **one transaction** takes the account's address lock, locks the account row `FOR UPDATE`, re-reads and
counts the memberships, and creates the company, the membership and the `org.created` row through a new method of `CompanyService` that runs in
the caller's transaction. The session is not touched. `AccountDeletion.DeleteAsync` counts memberships again under the account row lock for
`member_removed` and `org_deleted` and keeps an account that has one (spec 0017's recount, added here first). `DELETE /auth/account` judges the
account's memberships (not only the token's company) and answers the new `403 company_member`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, ASP.NET Identity, OpenIddict (untouched), EF Core on PostgreSQL 16 (Npgsql), xUnit v3 with
Testcontainers; Angular 21 (standalone components, signals), Vitest on jsdom and Playwright; bash with curl and Python 3 (standard library only)
for the e2e script. **No new package, no migration** (spec: "this slice changes no stored table").

## Global Constraints

Values copied from the spec, one line each; every task includes them.

- **Bodies:** `application/json`, at most 8 KiB, a JSON object, unknown members ignored; anything else, another media type included, is `400 {"error":"invalid_request"}`; `application/json` with a charset other than UTF-8 is the `415` of spec 0008 (`POST /auth/orgs` is under the charset guard). The `415` and the per-IP `429` come before every other answer.
- **Answers** carry `Cache-Control: no-store` and `Pragma: no-cache`, as every answer under `/auth` does.
- **Manifest key `org_creation`:** `self-service` = on; absent, a key with no value (`org_creation:` or `~`) and `off` = off; any other value (an empty string included) makes the manifest invalid (last valid manifest stays, `/auth/health` `Degraded`). A manifest stored by an earlier version reads as off; the stored form writes the key only when on. Needs no frontend address.
- **`GET /auth/options`:** gains the member `org_creation` (`"self-service"` or `"off"`), **last**; the other members unchanged.
- **Setting:** `Auth:Tenancy:MaxCompaniesPerAccount`, env `AUTH_MAX_COMPANIES_PER_ACCOUNT` (optional, may be empty): a whole number 1 to 1000; absent or blank (empty or only white space) = 20; anything else stops the start naming the key. Loaded by the service only (where `RateLimitSettings` is loaded), never by the CLI.
- **`POST /auth/orgs` `{"name"}`, checks in this order:** 1. no valid access token `401`; 2. switch off `403 org_creation_disabled` (before the body is read); 3. body or `name` rule broken (spec 0005: 1 to 100 characters, no control, format or noncharacters, no leading or trailing space) `400 invalid_request`; 4. account gone or no membership `403 permissions_changed` (the token's company need not still be a membership); 5. one transaction: address lock, account row `FOR UPDATE`, memberships read again under both locks (account gone or none: `403 permissions_changed`), company memberships (kind `organization`) counted, `>= MaxCompaniesPerAccount` = `409 company_limit_reached` and nothing written, else the company with a copy of the manifest's default roles, the membership with the first admin's role (`Manifest.FirstAdminRole`; `JoinedAt` = now truncated to microseconds via `StorableTime`; `LastUsedAt` `null`), and `org.created`; 6. `201 {"id","name","kind":"organization","role":{"id","name"},"current":false}`, no `Location`.
- **Effects:** the session does not change (no `Set-Cookie`, no other session changes); `LastUsedAt` stays `null`; any account may create (a private one included; its personal space stays); a refused creation writes no row; a database failure is the `500` of spec 0008 and nothing is written.
- **Rate policy:** `POST /auth/orgs` counts against `invite` (20 a minute by default); `GET /auth/orgs` stays `general`.
- **Audit:** no new kind (29). `org.created` gains `details.via` = `api`, with `actor_user_id` and `subject_user_id` the creator, `subject_email`, `org_id`, `org_name`, client address; the rows of `create-org` (`cli`), the seed (`seed`) and sign-up (`signup`) are unchanged. `company_member` refusals write no row.
- **`DELETE /auth/account`:** only an account whose only membership is its personal space may delete itself. Judged on the database as the first check after the token (before the body) and again under the address lock: token's company no longer a membership, or account gone, `403 permissions_changed`; personal space **and** at least one company membership, whatever the token's company, `403 {"error":"company_member"}`; no personal space `403 forbidden`; personal space only: the answers of specs 0010 and 0011. In every refusal nothing is counted, no password is evaluated and no row is written.
- **Recount (shared with spec 0017):** `AccountDeletion.DeleteAsync` for reasons `member_removed` and `org_deleted` counts each account's memberships again after it has read the accounts `FOR UPDATE` and keeps an account that has one: no `account.deleted` row, the returned number counts only the deleted accounts.
- **OpenAPI:** 39 operations and 26 under the charset guard; `POST /auth/orgs` with its body, `201`, `400`, `401`, `403` (`org_creation_disabled`, `permissions_changed`), `409 company_limit_reached`, `415`, `429`; `GET /auth/options` describes `org_creation`; `DELETE /auth/account` lists `403 company_member`.
- **Locks:** company lock, address lock, membership rows, account row. The creation takes the address lock then the account row; no company lock; no mail-request lock.
- **Python package:** no change. **Sample backend:** no change except its manifest. **Migration:** none.
- **Workflow** ([`docs/workflow.md`](../../workflow.md)): Conventional Commits in English, documentation in English. **The orchestrator makes the commits** (real dates, author `Alex`, no trailers, no Co-Authored-By): implementers leave their changes uncommitted and propose a subject.
- **.NET on this machine:** the build gate is `"C:/Program Files/dotnet/dotnet.exe" build Auth-Core.slnx -warnaserror -v q -nologo -maxcpucount:2`; integration tests run through the built test assembly with a filter (`-class`), `-maxThreads 2`, in PowerShell with `$env:Path += ';C:\Program Files\dotnet'` and `$env:DOCKER_HOST='npipe://./pipe/dockerDesktopLinuxEngine'` (exactly this form).
- **Implementers run only their task's test classes and the classes their task edits.** They never run the whole .NET suite, a live e2e script or Playwright. No `docker compose up/down`, no stopping of containers.
- **Python:** `python` (the `python3` on `PATH` is a Store shim). **Angular:** in `samples/notes-web`: `npm ci` once when `node_modules` is missing, then `npm test`, `npx ng build` and `npm run check:docs`. Texts are plain printable ASCII (`texts.spec.ts` checks it).
- **Line endings:** shell scripts are LF (`.gitattributes`; the orchestrator marks a new one executable in the index); `.cs`, `.ts`, `.md` files keep the endings each file has (CRLF in the working tree); the YAML files are LF. Edit existing files with the Edit tool; Git Bash `sed -i` can strip a CRLF.
- **Unicode escapes stay out of the source.** A backslash followed by `u` and four hex digits typed into a tool parameter arrives as the character. Build such text with `(char)0x...`. Each task's "Hand back" step scans the touched files:
  `python -c "import sys,unicodedata as u; bad=[(f,i,hex(ord(c))) for f in sys.argv[1:] for i,c in enumerate(open(f,encoding='utf-8').read()) if (u.category(c)=='Cc' and c not in '\t\r\n') or 0xFDD0<=ord(c)<=0xFDEF or (ord(c)&0xFFFE)==0xFFFE or ord(c)>0xFFFF]; print(bad or 'clean')" <files>`; it must print `clean`.
- **Every test is written first and seen to fail** for the reason the task names (not a compile error alone where a behaviour test is possible: add the new names as stubs that return the old behaviour first, then see the assertion fail). For the tests the acceptance map marks "teeth", the implementer also breaks the guarded line, sees the test fail, and restores it; the report says so.

## Checked on this code before the plan was written

Every subsystem the spec touches was read at `6b12d25`; the plan relies on these facts.

- **`CompanyService.CreateAsync(string name, CancellationToken, string via = "cli")`** (`Tenancy/CompanyService.cs`) validates with `NameInput.IsValid`, adds the `Company` and one `CompanyRole` per `manifest.Current.DefaultRoles`, stages `org.created` with no actor and **saves on its own**. Callers: `OperatorCommands`, `DevUserSeeder`, `SignupService.CreateCompanyAsync` (`via: "signup"`, then finds the first admin's role by `NameInput.Normalize(manifest.Current.FirstAdminRole.Name)` + `CompanyId` + `NormalizedName` and adds the `Membership`). Their behaviour must not change.
- **`SignupService.CreateCompanyAsync`** (`Signup/SignupService.cs:228-244`) is the model for the membership; `AddressLock.AcquireAsync(db, normalizedEmail, ct)` and `StorableTime.Now(clock)` are used there.
- **`AuditLog.Stage(AuditEntry, Actor?)`** puts the actor and the request's client address on the row; `AccountDeletion` stages `account.deleted` with `actor` as given.
- **`Manifest`** (`Tenancy/Manifest.cs`): constructor `(permissions, defaultRoles, signup = InviteOnly, privateSignup = false, privatePermissions = null, providers = null)`; `ToJson()` builds a private `StoredManifest(Permissions, DefaultRoles, Signup, Private, Providers)` serialised snake_case with `WhenWritingNull`; `FromJson` reads it back. **`ManifestParser`** (`Tenancy/ManifestParser.cs`): `ManifestDocument` (public setters, unknown keys are an error), the signup check at :135 (a missing value is absent, any other text must be an exact name), `new Manifest(...)` at :170.
- **Options:** `SignupOptionsEndpoint.Handle(SignupGate gate, ProviderCatalog providers)` returns `new OptionsResponse(gate.ModeName, gate.PrivateModeName, providers.Offered)`; `OptionsResponse` is in `Signup/SignupContracts.cs:12`; mapped in `Api/SignupRoutes.cs:17-23` with `.Produces<OptionsResponse>()`. Tests that compare the whole options body: `SignupOptionsTests`, `ProviderOptionsTests`, `ProviderSettingsTests`, `ShippedConfigurationTests`, `OpenApiTests`, `PrivateSignupGateTests`, `SignupCompleteTests`, `UpgradeFromSpec0010Tests` (found by grepping `private_signup`).
- **`RateLimitSettings.Load(IConfiguration)`** (`RateLimiting/RateLimitSettings.cs:36`) is the pattern for a loader: strings, blank = default, `NumberStyles.None` and `CultureInfo.InvariantCulture`, `InvalidOperationException` naming the key. Registered in `Program.cs:53` (`AddSingleton(RateLimitSettings.Load(builder.Configuration))`); `RateLimitSettingsTests` is the model for the test.
- **`TenancyErrors`** (`Tenancy/Outcome.cs`) maps each code in `StatusOf` and throws on an unknown one.
- **`RatePolicies.Classify`** (`RateLimiting/RatePolicy.cs:~72-100`): POSTs are matched by path; `/auth/invites/preview|accept` and `/auth/signup/preview|complete` are `Invite`; every other `/auth` POST not named is `General`; every `/auth` GET is `General`.
- **`OrgListEndpoint`** (`Api/OrgListEndpoint.cs`): path `/auth/orgs`, reads `Claims.Subject` and `TenantClaims.OrgId`, `OrgListItem(id, name, kind, RoleRef(id, name), current)`; mapped in `TenancyEndpoints.cs` with `.RequireAuthorization()`, `.Produces<OrgListResponse>()`, `.ProducesGuarded()`.
- **`AccountDeletionEndpoint`** (`Api/AccountDeletionEndpoint.cs`): reads the token's company (`TenantClaims.OrgId`), `memberships.ReadInCompanyAsync(userId, space, ct)`, and `memberships.CountAsync(userId, ct)`; its `Refusal(...)` gives `Forbidden` or `PermissionsChanged`; under the address lock it re-reads (`caller is not Personal` = `PermissionsChanged`, `CountAsync > 1` = `Forbidden`); `DeleteWithTicketAsync` is the provider branch. The 0015 tests of it: `AccountDeletionEndpointTests` (and `AccountDeletionProviderTests`).
- **`AccountDeletion.DeleteAsync`** (`Tenancy/AccountDeletion.cs:73`): deletes mail requests and pending sign-ups, locks memberships `FOR UPDATE`, reads the accounts `FOR UPDATE` (`accounts`), reads the spaces, deletes streaks/tokens/authorizations/users, personal companies, stages `account.deleted`. It has **no recount**. Callers: `MemberService.RemoveAsync` (after its own plain `AnyAsync` of the remaining memberships, `MemberService.cs:165-172`), `CompanyDeletionService` (`doomed` = members with no other membership, :74-83), `AccountDeletionEndpoint` (`Self`), start-up step (`Upgrade`). `MemberRemoval(bool AccountDeleted)` is what `remove-member` prints (`Admin/AdminCli.cs:120`: "the account is deleted" / "the account is kept").
- **Tests and helpers:** `TenancyTestBase` (`CreateCompanyAsync`, `CompanyWithAdminAsync`, `AddMemberAsync`, `AddMembershipAsync`, `CreatePrivateAccountAsync`, `RemoveMembershipAsync`, `InDbAsync`); `TenancyApi` (`Send`, `Get`, `ReadOkAsync`, `ReadCreatedAsync`, `AssertErrorAsync`, `AssertEmptyAsync`, `AssertUnauthorizedAsync`); `SessionApi.LoginAsync`; `HeldLock` (`AddressAsync`, `CompanyAsync`, `AuditTableAsync`, `WaitForWaitersAsync`); `AuthAppFactory` (`DefaultManifest`, `SelfServiceManifest`, `WithManifest(yaml)`, a settings dictionary the rate-limit tests use). Models: `OrgListTests` (an endpoint's tests), `ManifestSignupActivationTests` (activation and `Degraded`), `RateLimitSettingsTests`, `PrivateAccountRaceTests`/`SignupCompleteRaceTests` (races).
- **OpenAPI:** pinned by `OpenApiHardeningTests` (`Assert.Equal(38, operations.Count)` at :119, the charset-guard predicate below it, 25 covered).
- **Sample:** `samples/notes-web/src/app/` — `account-api.ts`, `auth/auth.service.ts`, `auth/auth.interceptor.ts` (`TOKEN_PATH_PREFIXES = ['/api/', '/auth/org']` covers `/auth/orgs`), `pages/notes.ts`, `texts.ts`, specs `account-api.orgs.spec.ts`, `auth/auth.service.orgs.spec.ts`, `pages/notes.orgs.spec.ts`, Playwright `e2e/12-orgs.spec.ts`.
- **Scripts:** `scripts/e2e-orgs.sh` (spec 0015) is the model for `scripts/e2e-orgs-create.sh`; run sequences in `scripts/e2e-tenancy.sh` and `scripts/e2e-hardening.sh`; step 1 of `e2e-signup.sh`, `e2e-private.sh`, `e2e-providers.sh` compares the options body.

## Review Focus

Inputs and failure modes the spec implies that a person is most likely to meet and no acceptance criterion names, most likely first; each has its test in the task named.

1. **The order of the first two checks.** A product that never turned creation on, called without a token, must say `401`, not `403`; with a token and a body that is not JSON it must say `403 org_creation_disabled` (`OrgCreateRefusalTests.Without_a_token_the_answer_is_401_also_when_creation_is_off`, Task 7).
2. **The switch changes while the service runs.** A manifest edit that turns creation on (or off) reaches the next request with no restart; companies made while it was on stay and work when it is turned off (`OrgCreateTests.A_manifest_change_reaches_creation_at_once_and_a_company_made_stays_when_it_is_turned_off`, Task 7).
3. **Names as people write them.** A name of exactly 100 characters, a non-ASCII name and a name that another company already has are all created as sent (names are not unique); the answer and `GET /auth/orgs` show the name as sent (`OrgCreateTests.A_name_of_100_characters_a_non_ascii_name_and_a_name_in_use_are_created_as_sent`, Task 7).
4. **Over the limit by invitations.** An account that joined more companies by invitation than the limit allows (count above the limit) still gets `409 company_limit_reached`, and an invitation is never refused by the limit (`OrgCreateLimitTests.An_account_above_the_limit_by_invitations_gets_409`, Task 8).
5. **The person who double-clicks "Create".** The sample's form disables its button while the request runs, so one click makes one company (`notes.orgs.spec.ts` "the new-company form sends one request when submitted twice", Task 11).
6. **A first admin's role that has no `*`.** With default roles where the first role holds only `members:manage`, the creator gets that role (spec 0009's rule), and a manifest whose first role holds `*` gives that one (`OrgCreateTests.The_creator_gets_the_first_role_that_holds_all_else_the_first_that_manages_members`, Task 7).

## Lock order (binding for every task)

The order of locks in one transaction is: **company lock, address lock, membership rows, account row** (plan 0015). Consequences here:

- `POST /auth/orgs` takes the address lock, then the account row `FOR UPDATE`, then only plain reads of memberships and inserts of new rows. It takes no existing membership row lock and no company lock, so it cannot deadlock with a sign-in (a sign-in holds its membership row and then waits for the account row; the creation holds the account row and waits for no membership row).
- `AccountDeletion.DeleteAsync` already locks the membership rows `FOR UPDATE` and then the account rows `FOR UPDATE`; the new recount (Task 1) is a plain read **after** both locks: a creation that holds the account row finishes first (its new membership is then counted), or it waits for the account row and, finding the account gone, answers `403 permissions_changed`.
- `DELETE /auth/account` takes the address lock first (as today), so a creation and a self-deletion of one account run one after the other.

## File Structure

New and changed files by responsibility. Everything under `src/Auth.Server/` unless said otherwise; tests under `tests/Auth.IntegrationTests/`.

| Area | Files | Responsibility |
| --- | --- | --- |
| Recount | `Tenancy/AccountDeletion.cs`, `Tenancy/MemberService.cs` | keep an account that gained a membership (Task 1) |
| Setting | `Tenancy/CompanyLimitSettings.cs` (new), `Program.cs`, `deploy/docker-compose.yml`, `deploy/docker-compose.prod.yml`, `deploy/.env.prod.example`, `.env.example`, `docs/deployment/vps.md` | `MaxCompaniesPerAccount` (Task 2) |
| Manifest | `Tenancy/Manifest.cs`, `Tenancy/ManifestParser.cs`, `deploy/auth.yaml`, `samples/notes-api/auth.yaml`, `tests/.../Infrastructure/AuthAppFactory.cs` | `org_creation` (Task 3) |
| Options | `Signup/SignupContracts.cs`, `Signup/SignupOptionsEndpoint.cs`, `Api/SignupRoutes.cs` (text) | the member `org_creation` (Task 4) |
| Codes and limit | `Tenancy/Outcome.cs`, `RateLimiting/RatePolicy.cs` | three error codes, the `Invite` policy (Task 5) |
| Creation | `Tenancy/CompanyService.cs` | creation in the caller's transaction (Task 6) |
| Endpoint | `Api/OrgCreateEndpoint.cs` (new), `Tenancy/Contracts.cs` (request record), `Api/TenancyEndpoints.cs` | `POST /auth/orgs` (Tasks 7 and 8) |
| Self-deletion | `Api/AccountDeletionEndpoint.cs`, `Api/TenancyEndpoints.cs` (text) | `company_member` (Task 9) |
| Sample | `samples/notes-web/src/app/account-api.ts`, `auth/auth.service.ts`, `texts.ts`, `pages/notes.ts`, their specs | the API calls and the form (Tasks 10 and 11) |
| Browser check | `samples/notes-web/e2e/13-create-org.spec.ts` (new) | Playwright (Task 12) |
| Live check | `scripts/e2e-orgs-create.sh` (new), `scripts/e2e-tenancy.sh`, `scripts/e2e-hardening.sh`, `scripts/e2e-signup.sh`, `scripts/e2e-private.sh`, `scripts/e2e-providers.sh` | the Goal over the network (Task 13) |
| Documents | `docs/integration/angular.md`, `docs/integration/python-fastapi.md`, `docs/security/threat-model.md`, `docs/operations/backup.md`, `docs/design.md`, `README.md`, `CHANGELOG.md` | Task 14 |

Shared test helpers (the task that first needs one adds it; later tasks use it):
- `AuthAppFactory.OrgCreationManifest` (a `const`: `DefaultManifest` + `"\norg_creation: self-service\n"`) and `AuthAppFactory.WithOrgCreation()` (writes it, as `WithSignup()` writes `SelfServiceManifest`) (Task 3).
- `TenancyApi.CreateOrg(HttpClient client, string? token, object? body, string contentType = "application/json")` → the response of `POST /auth/orgs`; `TenancyApi.CreateOrgPath = "/auth/orgs"` (Task 7).
- `TenancyTestBase.CompanyCountOfAsync(Guid userId)` → the number of company memberships (kind organization) of the account; `TenancyTestBase.AuditRowsAsync(string kind)` if none exists (the audit tests have a reader: reuse it) (Task 7).
- A way to set `Auth:Tenancy:MaxCompaniesPerAccount` for one test host: `AuthAppFactory.WithSetting(string key, string value)` if the factory has none (the rate-limit tests set their keys through its settings dictionary; reuse that) (Task 8).

## The tasks

| Task | Deliverable |
| --- | --- |
| 1 | The recount in `AccountDeletion.DeleteAsync` and the kept account in `MemberService` |
| 2 | `Auth:Tenancy:MaxCompaniesPerAccount` and its documentation |
| 3 | The manifest key `org_creation`, the stored form, the shipped manifests |
| 4 | `GET /auth/options` says `org_creation` |
| 5 | The three error codes and the `Invite` rate policy |
| 6 | `CompanyService`: creation in the caller's transaction, with its creator |
| 7 | `POST /auth/orgs`, part 1: route, the checks, the `201`, the audit row, OpenAPI |
| 8 | `POST /auth/orgs`, part 2: the limit and the races |
| 9 | `DELETE /auth/account` answers `company_member` |
| 10 | The sample: API calls, options, the delete-account guard |
| 11 | The sample: "New company..." in the picker |
| 12 | The Playwright spec `13-create-org.spec.ts` |
| 13 | `scripts/e2e-orgs-create.sh` and the run sequences |
| 14 | The documents and the changelog |
| 15 | The final check (the orchestrator) |

Tasks run in order. 10 and 11 need 7 and 9; 12 to 14 need 11 but not each other. Each task leaves the build gate and the tests of the files it touches green.

**Contract counts that change** (tests that enumerate the contract; the task that changes one says so): `OpenApiHardeningTests` (Task 7: 38 → 39 operations, 25 → 26 under the charset guard), `OpenApiTests` (Tasks 4, 7, 9: texts), `RatePolicyTests` (Task 5), the options-body tests (Task 4), `AccountDeletionEndpointTests` (Task 9).

---

### Task 1: The recount in `AccountDeletion.DeleteAsync`

**Files:**
- Modify: `src/Auth.Server/Tenancy/AccountDeletion.cs` (the routine and its class comment: it now keeps an account that has a membership for the reasons `MemberRemoved` and `OrgDeleted`), `src/Auth.Server/Tenancy/MemberService.cs` (`RemoveAsync`: `deleted` is what the routine reports)
- Create (tests): `tests/Auth.IntegrationTests/AccountDeletionRecountTests.cs`
- Modify (tests, only if a test fails for a reason that this task changes): `AccountDeletionTests.cs`, `AccountDeletionOnRemovalTests.cs`, `CompanyDeletionTests.cs`, `AdminCliTests.cs`, `Infrastructure/HeldLock.cs` (one new helper, see Step 1)
- **Known effect on existing tests:** `AccountDeletionTests` calls the routine directly with `MemberRemoved` or `OrgDeleted` for accounts that still hold memberships (around lines 208, 228, 288, 305, 508, through its helper at :32). With the recount those accounts are kept. Fix such a test by deleting the account's memberships first in its setup, as the real callers (`MemberService`, `CompanyDeletionService`) do; never weaken what it asserts about the deletion.

**Interfaces:**
- Consumes: the existing overloads of `AccountDeletion.DeleteAsync`.
- Produces: the same signatures; the returned `int` counts only the accounts deleted. For `MemberRemoved` and `OrgDeleted`, an account found to hold at least one membership after the `FOR UPDATE` read of the accounts is left out of `accounts` and `found` (no deletes, no `account.deleted` row, no streak, token or mail-request deletion beyond what the routine already did before the lock, see the Ruling below). `Self` and `Upgrade` are unchanged (a self-deletion is judged by its endpoint under the address lock; the start-up step deletes accounts that have no membership by definition).

- [ ] **Step 1: Write the failing tests** in `AccountDeletionRecountTests` (derive `TenancyTestBase`; call the routine in a scope with a transaction as `AccountDeletionTests` does; read its `AccountDeletionTests` helper first):
  - `A_member_removed_account_that_still_has_a_membership_is_kept`: an account in two companies; call `DeleteAsync(userId, MemberRemoved, actor, companyId, ct)` without removing either membership: returns `0`, the account row, both memberships, its refresh tokens and its login streak are all still there, and **no** `account.deleted` row is written.
  - `An_org_deleted_batch_keeps_only_the_accounts_that_have_a_membership`: two accounts, one holds a membership, one holds none; `DeleteAsync([a, b], OrgDeleted, ...)` returns `1`, deletes `b` (one `account.deleted` row, for `b`), keeps `a`.
  - `A_self_deletion_is_not_recounted`: `DeleteAsync(userId, Self, ...)` of an account that holds its personal membership still deletes it (returns `1`).
  - `Removing_the_last_membership_still_deletes_the_account_and_reports_it`: through `MemberService.RemoveAsync` (admin removes the only member with one membership): `MemberRemoval.AccountDeleted` is `true` and one `account.deleted` row is written; through the same call for an account that has a second membership: `AccountDeleted` is `false`, no `account.deleted` row, one `member.removed` row (this one is green before the change: it pins the path).
  - `A_membership_inserted_while_the_routine_waits_keeps_the_account` (the race this recount exists for), driven over HTTP so `HeldLock.WaitForWaitersAsync` can be used: an account is a plain member of company A only (company A has its own admin) and is not a member of company B. Open a new static `HeldLock.AccountRowAsync(AuthAppFactory factory, Guid userId)` (modelled on `MembershipRowAsync`; it locks the account row `FOR UPDATE` in its own transaction, so the removal waits exactly where the recount matters; `HeldLock.MembershipsAsync` would block it earlier and is not suitable). Send `DELETE /auth/org/members/{userId}` as A's admin; `HeldLock.WaitForWaitersAsync(factory, 1, removal)`; inside the held transaction insert a membership of the account in company B through a new instance helper `HeldLock.InsertMembershipAsync(Guid userId, Guid companyId, Guid roleId)` (modelled on `RemakeMembershipAsync`); `ReleaseAsync()`. The removal answers `204`; the account and its B membership remain; no `account.deleted` row; one `member.removed` row. (Teeth: remove the recount, the test fails because the account is deleted.)
- [ ] **Step 2: See them fail:** the first, second and fifth fail because the routine deletes what it is given; the others are green.
- [ ] **Step 3: Implement.** In `DeleteAsync`, after `accounts` is read and before `found` is built: for `reason` `MemberRemoved` or `OrgDeleted`, read the ids among `accounts` that appear in `db.Memberships` (one query, plain read; the membership rows are already locked by the routine) and remove them from `accounts`; if none is left return `0` (before the "spaces" read and every delete below it). `MemberService.RemoveAsync` keeps its plain `AnyAsync` (it avoids calling the routine when it is sure) and sets `deleted` from the routine's return (`await deletion.DeleteAsync(...) > 0`), so the answer is true to what happened. Update the class comment of `AccountDeletion` (one sentence on the recount and why it is under the row lock).
- [ ] **Step 4: Run** `AccountDeletionRecountTests`, `AccountDeletionTests`, `AccountDeletionOnRemovalTests`, `CompanyDeletionTests`, `OrgMemberTests`, `AdminCliTests`: green.
- [ ] **Step 5: Hand back** — uncommitted. Scan the touched files. Proposed subject: `fix(tenancy): keep an account that gained a membership while it was being removed`.

**Ruling recorded in this plan:** the routine deletes the account's non-invitation mail requests (a queued reset or verification mail) and pending sign-ups **before** it can lock the account (mail sender's lock order), so for the rare kept account those requests are lost: the person asks again. Cost if wrong: one lost reset mail in a rare race.

### Task 2: `Auth:Tenancy:MaxCompaniesPerAccount`

**Files:**
- Create: `src/Auth.Server/Tenancy/CompanyLimitSettings.cs`
- Modify: `src/Auth.Server/Program.cs` (register beside `RateLimitSettings`), `deploy/docker-compose.yml`, `deploy/docker-compose.prod.yml` (`Auth__Tenancy__MaxCompaniesPerAccount: ${AUTH_MAX_COMPANIES_PER_ACCOUNT:-}`), `deploy/.env.prod.example`, `.env.example`, `docs/deployment/vps.md` (the configuration table)
- Create (tests): `tests/Auth.IntegrationTests/CompanyLimitSettingsTests.cs`

**Interfaces:**
- Produces: `public sealed record CompanyLimitSettings(int MaxCompaniesPerAccount)` in namespace `Auth.Server.Tenancy`, with `public const string Key = "Auth:Tenancy:MaxCompaniesPerAccount"`, `public const int Default = 20`, `public static CompanyLimitSettings Load(IConfiguration configuration)`.

- [ ] **Step 1: Write the failing tests** in `CompanyLimitSettingsTests` (plain class, as `RateLimitSettingsTests`): `Absent_blank_and_white_space_mean_20` (no key, `""`, `"   "`); `A_whole_number_from_1_to_1000_is_taken` (`"1"`, `"2"`, `"1000"`, `" 7 "` trimmed); `Anything_else_stops_the_load_and_names_the_key` (theory: `"0"`, `"1001"`, `"-1"`, `"+5"`, `"20.5"`, `"abc"`, `"1e2"`, `"٣"` (a non-ASCII digit), a 12-digit number): `InvalidOperationException` whose message contains `Auth:Tenancy:MaxCompaniesPerAccount`; `The_service_stops_at_start_for_a_bad_value`: a test host built with `"0"` throws at start naming the key (model: the rate-limit start test in `RateLimitMiddlewareTests`/`RateLimitDefaultsTests`: reuse its way of building a failing host); `The_shipped_compose_files_pass_the_variable_and_the_examples_list_it` (read `deploy/docker-compose.yml`, `deploy/docker-compose.prod.yml`, `deploy/.env.prod.example`, `.env.example`, `docs/deployment/vps.md`: each contains `AUTH_MAX_COMPANIES_PER_ACCOUNT`; the model is `ShippedConfigurationTests`).
- [ ] **Step 2: See them fail** (the type does not exist: add the type as a stub whose `Load` returns 20 for every input, so the assertions fail, not the compile).
- [ ] **Step 3: Implement** the loader on the pattern of `RateLimitSettings.Load` (blank = default, `NumberStyles.None` after trimming, invariant culture, range check, message names the key); register it with `builder.Services.AddSingleton(CompanyLimitSettings.Load(builder.Configuration))` in `Program.cs` only (never in `AddTenancy`, which the CLI shares); the compose lines (empty default, like the rate-limit lines), the env examples and the vps table row (key, variable, rule).
- [ ] **Step 4: Run** `CompanyLimitSettingsTests`, `ShippedConfigurationTests`, `RateLimitSettingsTests`: green.
- [ ] **Step 5: Hand back** — uncommitted. Scan. Subject: `feat(tenancy): configure how many companies one account may create`.

### Task 3: The manifest key `org_creation`

**Files:**
- Modify: `src/Auth.Server/Tenancy/Manifest.cs`, `src/Auth.Server/Tenancy/ManifestParser.cs`, `deploy/auth.yaml`, `samples/notes-api/auth.yaml`, `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`
- Create (tests): `tests/Auth.IntegrationTests/ManifestOrgCreationTests.cs`

**Interfaces:**
- Produces: `Manifest.OrgCreation` (`bool`; constructor parameter `bool orgCreation = false`, the **last** optional parameter, so every existing call stays valid); `OrgCreationModes` (static class beside `SignupModes`: `SelfServiceName` = `"self-service"`, `OffName` = `"off"`); `AuthAppFactory.OrgCreationManifest` and `AuthAppFactory.WithOrgCreation()`.

- [ ] **Step 1: Write the failing tests** in `ManifestOrgCreationTests` (model: `ManifestSignupTests`, `ManifestPrivateTests`, `ManifestSignupActivationTests`):
  - `The_key_is_off_when_absent_empty_valued_or_off` (no key; `org_creation:`; `org_creation: ~`; `org_creation: off`): `Manifest.OrgCreation` false.
  - `self_service_turns_it_on`.
  - `Any_other_value_is_invalid` (theory: `""` (an empty quoted string), `"on"`, `"Self-Service"`, `"true"`, `"self-service "`, `"invite-only"`): `ManifestParser.Parse` fails with a reason naming `org_creation`.
  - `The_stored_form_writes_the_key_only_when_on`: `ToJson()` of an off manifest does not contain `org_creation` and equals the stored form of the same manifest built before this change (compare against a literal JSON string in the test); an on manifest contains `"org_creation":"self-service"`; `FromJson` round-trips both; a stored form without the key reads as off (also `A_stored_manifest_from_an_earlier_version_reads_as_off` through the activation path, as `ManifestSignupActivationTests` line 55-75 does).
  - `A_bad_value_keeps_the_last_valid_manifest_and_health_is_degraded` (activation test; model line 40-52 of `ManifestSignupActivationTests`).
  - `A_manifest_with_the_key_is_activated_and_stored` (the state's manifest has `OrgCreation`; the `ActiveManifests` row parses back with it).
  - `The_shipped_development_manifests_turn_it_on`: `deploy/auth.yaml` and `samples/notes-api/auth.yaml` parse and have `OrgCreation` true, each with a comment line on the key in the style of its neighbours (read `ShippedConfigurationTests` for how it loads them).
- [ ] **Step 2: See them fail** (stub the property as always false and the parser as accepting nothing new, so the "on" and "invalid" assertions fail by assertion).
- [ ] **Step 3: Implement.** `ManifestDocument.OrgCreation` (`string?`, snake_case key `org_creation` through the naming convention; a text, so that any other value reaches the check); in `Validate`, right after the signup check: absent or `off` = false, `self-service` = true, else `Fail($"org_creation must be '{OffName}' or '{SelfServiceName}'")`. **An empty string is invalid**, and `null` (no value, `~`) is absent: the signup check does the same with `document.Signup ?? ...`, so do not trim and do not treat `""` as absent. `StoredManifest` gains `string? OrgCreation = null` (last), written only when on; `FromJson` reads `stored.OrgCreation == "self-service"`. `deploy/auth.yaml` and `samples/notes-api/auth.yaml` get `org_creation: self-service` with a comment line in the style of the other keys. `AuthAppFactory`: the constant and `WithOrgCreation()`.
- [ ] **Step 4: Run** `ManifestOrgCreationTests`, `ManifestParserTests`, `ManifestSignupTests`, `ManifestPrivateTests`, `ManifestProvidersTests`, `ManifestSignupActivationTests`, `ManifestActivationTests`, `ShippedConfigurationTests`: green.
- [ ] **Step 5: Hand back** — uncommitted. Scan. Subject: `feat(manifest): org_creation lets a product turn on creating another company`.

### Task 4: `GET /auth/options` says `org_creation`

**Files:**
- Modify: `src/Auth.Server/Signup/SignupContracts.cs`, `src/Auth.Server/Signup/SignupOptionsEndpoint.cs`, `src/Auth.Server/Api/SignupRoutes.cs` (the description), and the tests that compare the whole body: `SignupOptionsTests`, `ProviderOptionsTests`, `ProviderSettingsTests`, `ShippedConfigurationTests`, `OpenApiTests`, `PrivateSignupGateTests`, `SignupCompleteTests`, `UpgradeFromSpec0010Tests` (as far as each one compares the body, not merely reads a member)

**Interfaces:**
- Consumes: `Manifest.OrgCreation` (Task 3), `ManifestHolder.Current`.
- Produces: `OptionsResponse(string Signup, string PrivateSignup, IReadOnlyList<string> Providers, string OrgCreation)` (new member **last**, serialised `org_creation`); `SignupOptionsEndpoint.Handle` takes `ManifestHolder` too.

- [ ] **Step 1: Write the failing tests**: in `SignupOptionsTests`: `Options_say_org_creation_off_by_default` (the default manifest: `"org_creation":"off"` and the other three members unchanged); `Options_say_org_creation_self_service_when_the_manifest_does` (`WithOrgCreation()`); `The_member_comes_last` (the property order of the body is `signup`, `private_signup`, `providers`, `org_creation`); `A_manifest_change_reaches_the_options_at_once` (off, then on, then off). In `OpenApiTests`: `The_options_describe_org_creation` (the description of `GET /auth/options` names `org_creation` and both values; the schema of the `200` has the property). Extend the listed tests that compare the whole body to expect the new member (they fail first for that reason).
- [ ] **Step 2: See them fail** (the whole-body tests fail on the missing member).
- [ ] **Step 3: Implement** the record member, the handler (`manifest.Current.OrgCreation ? OrgCreationModes.SelfServiceName : OrgCreationModes.OffName`), the route description (one sentence). Keep the member order.
- [ ] **Step 4: Run** the classes above plus `ProviderOptionsTests`: green.
- [ ] **Step 5: Hand back** — uncommitted. Scan. Subject: `feat(signup): GET /auth/options says whether a signed-in person may create a company`.

### Task 5: The three error codes and the `Invite` rate policy

**Files:**
- Modify: `src/Auth.Server/Tenancy/Outcome.cs`, `src/Auth.Server/RateLimiting/RatePolicy.cs`
- Modify (tests): `tests/Auth.IntegrationTests/RatePolicyTests.cs`; the test that enumerates the codes (find it: grep `StatusOf` in `tests`; add to it or create `TenancyErrorsTests.cs`)

**Interfaces:**
- Produces: `TenancyErrors.OrgCreationDisabled` (`"org_creation_disabled"`, 403), `TenancyErrors.CompanyMember` (`"company_member"`, 403), `TenancyErrors.CompanyLimitReached` (`"company_limit_reached"`, 409); `RatePolicies.Classify("POST", "/auth/orgs")` = `Invite`.

- [ ] **Step 1: Write the failing tests**: `The_three_codes_have_their_statuses` (`StatusOf` gives 403, 403, 409; an unknown code still throws); in `RatePolicyTests`: `POST_auth_orgs_is_invite` (also with a trailing slash and in another case, as the neighbouring rows are written), `GET_auth_orgs_stays_general`, `PUT_and_DELETE_auth_orgs_are_general`.
- [ ] **Step 2: See them fail** (constants do not exist: add them as constants first so `StatusOf` throws for them; the policy test fails by assertion).
- [ ] **Step 3: Implement** the constants, the `StatusOf` arms, the `Classify` line beside the other `Invite` paths (use a shared constant for the path if `OrgListEndpoint.Path` is visible: `OrgListEndpoint.Path`).
- [ ] **Step 4: Run** `RatePolicyTests`, `RatePolicySignupTests`, the codes test: green.
- [ ] **Step 5: Hand back** — uncommitted. Scan. Subject: `feat(tenancy): error codes and per-IP policy of creating a company`.

### Task 6: `CompanyService`: creation in the caller's transaction, with its creator

**Files:**
- Modify: `src/Auth.Server/Tenancy/CompanyService.cs` (the new method, the class comment: "only the operator does..." names the new path)
- Modify (tests): `tests/Auth.IntegrationTests/CompanyServiceTests.cs`

**Interfaces:**
- Consumes: `NameInput.IsValid`, `Manifest.FirstAdminRole`, `StorableTime.Now`, `AuditLog.Stage(AuditEntry, Actor?)`.
- Produces: `public sealed record CreatedCompany(Guid CompanyId, string Name, Guid RoleId, string RoleName)` and `public async Task<Outcome<CreatedCompany>> CreateForAccountAsync(Guid userId, string? email, string name, CancellationToken cancellationToken)` on `CompanyService`. It **requires the caller's transaction** (throws `InvalidOperationException` without one, as `AccountDeletion` does), creates the company with a copy of the default roles, adds the creator's `Membership` with the company's own role row of the first admin's name (`CompanyId` + `NormalizedName`), `JoinedAt = StorableTime.Now(clock)`, `LastUsedAt = null`, stages `org.created` with `details.via = "api"`, `ActorUserId = SubjectUserId = userId`, `SubjectEmail = email`, `OrgId`, `OrgName`, and **saves** (`db.SaveChangesAsync`) but does not commit. It returns `invalid_request` for a name that breaks the rule, with nothing staged. The existing `CreateAsync` is unchanged for its callers; the two share one private helper that stages the company, the roles and returns the entities (no duplicated loop).

- [ ] **Step 1: Write the failing tests** in `CompanyServiceTests`: `CreateForAccount_creates_the_company_the_roles_the_membership_and_the_row_in_the_callers_transaction` (inside a transaction the test opens: after the call and before the commit, another connection sees nothing; after commit: one company with the default roles, one membership whose role is that company's own role named like `FirstAdminRole` (not another company's), `JoinedAt` has no sub-microsecond part, `LastUsedAt` null; one `org.created` row with the creator as actor and subject, `subject_email` the email, `details.via` `api`); `CreateForAccount_without_a_transaction_throws`; `CreateForAccount_with_a_bad_name_writes_nothing` (`invalid_request`, no company, no row); `CreateForAccount_rolls_back_with_the_callers_transaction` (the caller rolls back: no company, no membership, no row); `CreateAsync_still_writes_the_cli_row_with_no_actor` (pins the old path: `via` `cli`, no actor; `signup` and `seed` rows unchanged are pinned by their existing tests).
- [ ] **Step 2: See them fail** (stub returns `invalid_request`).
- [ ] **Step 3: Implement.** Refactor the loop of `CreateAsync` into a private helper; the new method as above. Update the class summary.
- [ ] **Step 4: Run** `CompanyServiceTests`, `SignupFirstAdminTests`, `SignupCompleteTests`, `DevCompanySeedTests`, `AdminCliTests` (they create companies): green.
- [ ] **Step 5: Hand back** — uncommitted. Scan. Subject: `feat(tenancy): create a company for a signed-in account in the caller's transaction`.

### Task 7: `POST /auth/orgs`, part 1

**Files:**
- Create: `src/Auth.Server/Api/OrgCreateEndpoint.cs`
- Modify: `src/Auth.Server/Tenancy/Contracts.cs` (the request record, beside `RenameOrgRequest`; the response is `OrgListItem`), `src/Auth.Server/Api/TenancyEndpoints.cs` (the route beside the `GET`, its description, `.ReadsJson<CreateOrgRequest>()`, `.Produces<OrgListItem>(StatusCodes.Status201Created)`, `.ProducesError(400/403/409 ...)`, `.ProducesGuarded()`), `tests/Auth.IntegrationTests/Infrastructure/TenancyApi.cs`, `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`, `OpenApiTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/OrgCreateTests.cs`, `tests/Auth.IntegrationTests/OrgCreateRefusalTests.cs`

**Interfaces:**
- Consumes: Tasks 2 (settings, used in Task 8), 3, 5, 6; `OrgListItem`, `RoleRef`, `SpaceKinds.NameOf`; `AddressLock.AcquireAsync`; `ApiResults.Error`.
- Produces: `OrgCreateEndpoint.Path` (= `OrgListEndpoint.Path`) and `OrgCreateEndpoint.CreateAsync(HttpContext http, ManifestHolder manifest, ...)`; `record CreateOrgRequest(string Name)` beside `RenameOrgRequest` — an OpenAPI type only (for `.ReadsJson<CreateOrgRequest>()`), as `RenameOrgRequest` is; the body is read by `JsonObjectBody`, never bound; `TenancyApi.CreateOrg`.

**Endpoint flow (binding):** 1. `Claims.Subject` not a GUID: `forbidden` (as `GET /auth/orgs`). 2. `!manifest.Current.OrgCreation`: `403 org_creation_disabled` **before the body is read**. 3. Read the body with `JsonObjectBody.ReadStringsAsync(http.Request, ["name"], ct)` (the reader every JSON endpoint uses: object only, 8 KiB, unknown members ignored; read `Account/MailRequestEndpoint.cs` for how its result is checked) and check the name with `NameInput.IsValid` (`400 invalid_request`). 4. `MembershipReader.CountAsync(userId) == 0` (account gone or no membership): `403 permissions_changed`. 5. One plain transaction (`db.Database.BeginTransactionAsync`; this repo uses no EF retry strategy): read the account's normalised email, `AddressLock.AcquireAsync`, lock the account row `FOR UPDATE` and read it (gone: `permissions_changed`), re-read memberships (none: `permissions_changed`), count company memberships (kind organization) and refuse with `409 company_limit_reached` when the count is `>= CompanyLimitSettings.MaxCompaniesPerAccount` (this task writes the check; Task 8 tests it), `CompanyService.CreateForAccountAsync`, commit. 6. `201` with `OrgListItem(id, name, "organization", RoleRef(roleId, roleName), current: false)`, no `Location`, no cookie.

- [ ] **Step 1: Write the failing tests.** `OrgCreateTests` (derive `TenancyTestBase`, host with `WithOrgCreation()`; logs in with `SessionApi.LoginAsync`; helpers as `OrgListTests`):
  - `A_signed_in_account_creates_a_company_and_gets_201_with_the_entry` (criterion 3: status `201`; the body members are exactly `current`, `id`, `kind`, `name`, `role`; `kind` `organization`; `role` the first admin's role of the new company (id of the new company's role row, name `admin`); `current` false; no `Location` header; no `Set-Cookie` header; `Cache-Control: no-store`; the company has one role per default role; the account is a member with `JoinedAt` set (microsecond-exact) and `LastUsedAt` null; the old access token still works against `GET /auth/org` of its own company; the refresh with the old cookie answers a pair for the **same** company as before).
  - `The_new_company_is_listed_and_can_be_switched_to` (criterion 4: `GET /auth/orgs` lists it, `current` false; `POST /auth/switch` with its id gives a token whose `org_id` is the new company and whose roles are `["admin"]`; `GET /auth/org/members` with it shows one member, the creator).
  - `A_token_whose_company_membership_was_removed_still_creates` (criterion 8: the account is in two companies, the token is for the first, the first membership is removed in the database: `201`).
  - `A_private_account_creates_a_company_and_keeps_its_personal_space` (criterion 11: `201`; `GET /auth/orgs` lists both, the personal one kind `personal`).
  - `org_created_is_written_in_the_creations_transaction` (criterion 12: exactly one new `org.created` row with `actor_user_id` = `subject_user_id` = the creator, `subject_email`, `org_id`, `org_name`, `details.via` `api` and the client address; the number of audit kinds is still 29: reuse the assertion of `AuditTableTests` by name, do not add one); `A_failed_save_writes_no_company_and_no_row` (model: `AuditAtomicityTests`: drop the table `audit_events` through `InDbAsync`, send a valid creation, expect the `500` of spec 0008, and no company and no membership written).
  - `A_manifest_change_reaches_creation_at_once_and_a_company_made_stays_when_it_is_turned_off` (Review Focus 2).
  - `A_name_of_100_characters_a_non_ascii_name_and_a_name_in_use_are_created_as_sent` (Review Focus 3).
  - `The_creator_gets_the_first_role_that_holds_all_else_the_first_that_manages_members` (Review Focus 6: two manifests).
  - `OrgCreateRefusalTests`: `Without_a_token_the_answer_is_401_also_when_creation_is_off` (Review Focus 1; `TenancyApi.AssertUnauthorizedAsync`); `With_the_switch_off_the_answer_is_403_org_creation_disabled_also_for_a_body_that_is_not_json` (criterion 5: a valid body; text/plain; `not json`; an empty body; each `403`); `A_name_that_breaks_the_rule_is_400` (criterion 6: theory: missing, `null`, a number, `""`, 101 characters, a leading space, a trailing space, a control character (build it with `(char)0x07`), a format character `(char)0x200B`, a noncharacter `(char)0xFDD0`; a body that is a JSON array; a text/plain body; unknown members alongside a valid name are ignored and give `201`; nothing written each time: no company, no row); `Another_charset_is_415` (criterion 6: `application/json; charset=utf-16`); `A_token_of_a_deleted_account_with_a_valid_body_is_403_permissions_changed` (criterion 7); `Other_methods_are_405_with_Allow_naming_both` (PUT, PATCH, DELETE on `/auth/orgs`: `405`, `Allow` holds `GET` and `POST`, `Cache-Control: no-store`); `A_refused_creation_writes_no_row` (every refusal above leaves the audit table as it was).
  - `OpenApiHardeningTests`: `38` → `39`; the charset-guard test's count `25` → `26`; keep the neighbouring comments in the style "spec 0016: POST /auth/orgs". `OpenApiTests`: `POST_auth_orgs_is_described` (request body schema with `name`, `201` with `OrgListItem`, `400`, `401`, `403` naming `org_creation_disabled` and `permissions_changed`, `409` naming `company_limit_reached`, `415`, `429`, the description says the session does not change and the product calls `POST /auth/switch`).
- [ ] **Step 2: See them fail** (the route does not exist: `404`/`405`; add the route first as a handler returning `501`-like stub only if needed so the tests fail by assertion).
- [ ] **Step 3: Implement** the endpoint as "Endpoint flow", the request record, the route with its description (state the order of the checks, the first admin's role, `current` always false, no session change), `TenancyApi.CreateOrg`. The limit check is written here as step 5 of the flow; Task 8's tests exercise it.
- [ ] **Step 4: Run** `OrgCreateTests`, `OrgCreateRefusalTests`, `OrgListTests`, `OrgEndpointTests`, `OpenApiHardeningTests`, `OpenApiTests`, `RatePolicyTests`, `CompanyServiceTests`, `ErrorHandlingTests`: green.
- [ ] **Step 5: Hand back** — uncommitted. Scan. Subject: `feat(sessions): POST /auth/orgs creates another company for a signed-in account`.

### Task 8: `POST /auth/orgs`, part 2: the limit and the races

**Files:**
- Modify: `src/Auth.Server/Api/OrgCreateEndpoint.cs` (only if a test shows a gap), `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs` (a way to set `Auth:Tenancy:MaxCompaniesPerAccount` for one host, if none exists)
- Create (tests): `tests/Auth.IntegrationTests/OrgCreateLimitTests.cs`, `tests/Auth.IntegrationTests/OrgCreateRaceTests.cs`

**Interfaces:**
- Consumes: Tasks 2 and 7; `HeldLock`, `PrivateAccountRaceTests` as the model of a race test.

- [ ] **Step 1: Write the failing tests.**
  - `OrgCreateLimitTests` (limit set to 2 through the factory): `An_account_at_the_limit_gets_409_and_nothing_is_written` (criterion 9: two company memberships, created or joined; `409 company_limit_reached`; no company, no membership, no row); `The_personal_space_is_not_counted` (a private account with its personal space and one company creates a second: `201`; a third: `409`); `After_deleting_one_company_a_creation_succeeds` (the account deletes one of its two companies with `DELETE /auth/org`, then creates: `201`); `An_account_above_the_limit_by_invitations_gets_409` (Review Focus 4: the account joined three companies through `AddMembershipAsync` with the limit 2: `409`; and acceptance of an invitation by it is not refused: pinned by `Invitation_acceptance_is_not_limited`, a green test of the existing path with the same setup: it uses the acceptance endpoint as `PrivateInvitationTests` do); `The_default_limit_is_20` (a host with no setting: an account with 19 company memberships creates one, the 21st gets `409`; build the memberships in one database round trip, not by 19 requests).
  - `OrgCreateRaceTests` (against PostgreSQL, real concurrency, `HeldLock.WaitForWaitersAsync` where a test must hold a request behind a lock): `Parallel_creations_at_the_limit_minus_one_make_exactly_one_company` (criterion 10: limit 2, the account holds one company; five parallel `POST /auth/orgs`: exactly one `201`, four `409 company_limit_reached`; the account holds exactly two company memberships) (teeth: take both locks out of the endpoint, the address lock and the account row lock, the test fails with more than one `201`; one of them alone still serializes the creations, and the address lock is the teeth of the self-deletion tests below, where the creation queues behind a held address lock); `A_creation_and_a_self_deletion_of_one_private_account_never_leave_a_company_without_a_member` (criterion 18, first half: hold the address lock with `HeldLock.AddressAsync`; send the first request, `HeldLock.WaitForWaitersAsync(factory, 1, first)`; send the second, `HeldLock.WaitForWaitersAsync(factory, 2, first, second)`; release; a second test method sends them in the swapped order, so both orders of arrival are covered: the outcomes are either `201` + `company_member`, or `204` + `403 permissions_changed`; afterwards: either the account, its personal space and the new company all exist, or none of the three, and no company has zero members); `A_creation_and_the_removal_of_the_accounts_last_company_membership_never_500` (criterion 18, second half: an account with one company membership (company A, the account is a plain member, its admin removes it); hold the account row with `HeldLock.AccountRowAsync` (Task 1); send the first request, wait for 1 waiter, send the second, wait for 2 waiters (as above); release; outcomes: the account is kept with the new company (`201`, removal `204`, account row exists), or it is deleted and the creation says `403 permissions_changed`; never a `500`; run both orders as two methods; teeth: remove Task 1's recount, the first order fails).
- [ ] **Step 2: See them fail** where the endpoint lacks the limit or the order (Task 7 wrote the check: if a test is already green, break the line, see it fail, restore, and say so in the report: "teeth checked").
- [ ] **Step 3: Implement** only what a failing test shows missing (expected: nothing or the factory setting helper).
- [ ] **Step 4: Run** `OrgCreateLimitTests`, `OrgCreateRaceTests`, `OrgCreateTests`, `AccountDeletionRecountTests`, `PrivateAccountRaceTests`: green.
- [ ] **Step 5: Hand back** — uncommitted. Scan. Subject: `test(sessions): the company limit and the races of POST /auth/orgs`.

### Task 9: `DELETE /auth/account` answers `company_member`

**Files:**
- Modify: `src/Auth.Server/Api/AccountDeletionEndpoint.cs` (`Refusal`, the check under the address lock, the provider branch), `src/Auth.Server/Api/TenancyEndpoints.cs` (the description and `.ProducesError(403, ..., TenancyErrors.CompanyMember)`), tests: `AccountDeletionEndpointTests.cs`, `AccountDeletionProviderTests.cs`, `OpenApiTests.cs`, and any 0015 test that expects `forbidden` for a personal token of an account that also holds a company (find them: grep `forbidden` in the tests of the self-deletion and `SessionCompany*`, `SwitchTests`)
- Create (tests): `tests/Auth.IntegrationTests/AccountDeletionCompanyMemberTests.cs`

**Interfaces:**
- Consumes: Task 5 (`TenancyErrors.CompanyMember`), `MembershipReader.ListAsync`/`CountAsync`.
- Produces: the table of the spec, judged on the database as the first check after the token (before the body) and again under the address lock; `403 company_member` writes no row, counts nothing in the login streak, evaluates no password.

**Unchanged:** a token with no `org_id` claim answers `forbidden`, as today.

**Judgement (binding):** read the account's memberships once (`MembershipReader.ListAsync`). None, or the token's company absent from them: `permissions_changed`. Else: personal space present and at least one `organization` membership: `company_member`; no personal space (companies only): `forbidden`; personal space only: continue as today (the token's company must then be that personal space: a token of a company that is no longer a membership was already `permissions_changed`). Under the lock, the same on a fresh read: company membership found = `company_member`; the personal space gone = `permissions_changed`.

- [ ] **Step 1: Write the failing tests** in `AccountDeletionCompanyMemberTests` (derive `TenancyTestBase`; models: `AccountDeletionEndpointTests`): `A_personal_token_of_an_account_with_a_company_is_403_company_member` and `A_company_token_of_an_account_with_both_kinds_is_403_company_member` (criterion 15; each also asserts: the login streak row unchanged, the audit table unchanged, the account and both memberships still there; the password sent is **wrong** in one variant and the answer is still `company_member`, not `wrong_password`, and the streak is untouched: this proves "no password is evaluated"); `The_answer_does_not_wait_for_the_body` (a body that is not JSON still gets `403 company_member`: the check precedes the body); `After_the_company_is_deleted_by_the_account_the_deletion_succeeds` (criterion 16: `DELETE /auth/org` with the company token, then a **new login** (personal space) and `DELETE /auth/account` with the password: `204`; the account is gone); `An_account_with_companies_only_is_403_forbidden` (criterion 17); `A_personal_only_account_gets_the_answers_of_spec_0010` (one representative: `204` with the right password; `wrong_password`); `A_token_whose_company_is_no_longer_a_membership_is_403_permissions_changed` (criterion 17; the personal space gone; the company gone); `A_company_membership_added_while_the_request_waits_for_the_lock_is_403_company_member` (the under-lock check: hold the address lock, send the deletion with a valid personal token and password, wait for the waiter, insert a company membership for the account, release: `403 company_member`, nothing deleted); `A_personal_space_removed_while_the_request_waits_is_403_permissions_changed` (the same with the personal membership deleted); `The_provider_ticket_branch_answers_company_member_too` (in `AccountDeletionProviderTests`: an account with both kinds and no password sends a valid reauth ticket: `403 company_member`, because the judgement runs before `DeleteWithTicketAsync` consumes the ticket; then the test removes the company membership through `RemoveMembershipAsync` (the account has no password, so it cannot call `DELETE /auth/org`), and the **same** ticket now deletes the account: `204`, which proves the ticket was not used up). Update the existing tests (each flips from `forbidden` to `company_member`): `AccountDeletionEndpointTests.An_account_with_a_personal_space_and_a_company_is_a_company_member_at_sign_in_and_cannot_delete_itself` (:192), `.A_personal_token_of_an_account_that_is_also_a_company_member_is_forbidden` (:336, rename to `..._is_company_member`), `.A_company_member_that_gains_a_personal_space_meanwhile_is_forbidden_under_the_lock` (:356, rename to `..._is_company_member_under_the_lock`). These stay as they are: `.A_company_member_is_403_forbidden_and_nothing_is_counted` (:223, companies only) and `.A_token_of_a_space_the_account_left_is_permissions_changed_even_with_another_membership` (:376). `OpenApiTests.DELETE_auth_account_lists_403_company_member`: the `403` description names `company_member` and the rule.
- [ ] **Step 2: See them fail** (they get `forbidden` today).
- [ ] **Step 3: Implement** the judgement in the endpoint (one helper used by both checks), the provider branch, the texts. Do not change the order of the existing checks beyond the new one.
- [ ] **Step 4: Run** `AccountDeletionCompanyMemberTests`, `AccountDeletionEndpointTests`, `AccountDeletionProviderTests`, `AccountDeletionTests`, `SessionCompanyRefreshTests`, `SwitchTests`, `OpenApiTests`: green.
- [ ] **Step 5: Hand back** — uncommitted. Scan. Subject: `feat(account): an account in a company and a personal space cannot delete itself (company_member)`.

### Task 10: The sample: API calls, options, the delete-account guard

**Files:**
- Modify: `samples/notes-web/src/app/account-api.ts`, `src/app/auth/auth.service.ts`, `src/app/texts.ts`, `src/app/pages/notes.ts` (the delete-account path only), and the specs that build an options body: `account-api.signup.spec.ts`, `account-api.providers.spec.ts`, `pages/login.signup.spec.ts`, `pages/providers-on-screens.spec.ts`, `pages/signup.private.spec.ts`, `pages/signup.spec.ts`, `pages/too-many-requests.spec.ts` (each only as far as it builds an options body; leave a spec alone that does not)
- Modify (tests): `account-api.orgs.spec.ts`, `auth/auth.service.orgs.spec.ts`, `pages/notes.account.spec.ts`, `pages/notes.provider.spec.ts`

**Interfaces:**
- Produces: `AccountApi.createOrg(name: string): Promise<Outcome<OrgEntry>>` (POST `/auth/orgs` with the token, via the interceptor's `/auth/org` prefix; same promise-and-`Outcome` style as `listOrgs` and `switchOrg` in `account-api.ts`; the entry checked with the existing `asOrgEntry`) and the error codes `org_creation_disabled`, `company_limit_reached`, `company_member` carried in `Outcome` as the other codes are; `SiteOptions` gains `orgCreation: boolean` read from `org_creation` in `AccountApi.options()` (`false` when the member is absent: an old server). **`AuthService` does not read the options today** (only `login.ts` and `signup.ts` call `account.options()`): `pages/notes.ts` reads `account.options()` once when it starts and keeps `orgCreation` in a signal of its own (no change to `AuthService` for the options). `AuthService.createCompany(name: string): Promise<'created' | 'limit' | 'disabled' | 'invalid' | 'gone' | 'failed'>` calls `createOrg`, and on success the existing `AuthService.switchTo(orgId)` (spec 0015, `Promise<SwitchResult>`); a `switchTo` result other than `'switched'` maps to `'gone'` / `'failed'` as `switchTo`'s callers already do; `permissions_changed` maps to `'gone'` (the caller refreshes); the texts of the spec: "You have reached the limit of companies.", "Creating companies is turned off.", the name rule text, and the `company_member` text with the names ("You are still a member of {names}. Delete those companies or ask their admins to remove you, then delete your account.").
- **Delete-account guard:** before the notes are deleted (both paths, password and provider ticket) the sample reads `GET /auth/orgs`; when an entry of kind `organization` is listed it shows the `company_member` text with those names and deletes nothing; on a server `403 company_member` it shows the same text from a fresh `GET /auth/orgs`.

- [ ] **Step 1: Write the failing tests** (Vitest, the style of the neighbouring specs): `account-api.orgs.spec.ts`: `createOrg_posts_the_name_with_the_token_and_returns_the_entry`, `createOrg_carries_the_three_error_codes`, `the_options_body_with_org_creation_is_read_and_an_old_body_without_it_means_off`; `auth.service.orgs.spec.ts`: `createCompany_creates_then_switches_and_ends_in_the_new_company`, `createCompany_on_409_returns_limit_and_does_not_switch`, `createCompany_on_org_creation_disabled_returns_disabled_and_does_not_switch`, `createCompany_on_permissions_changed_returns_gone`; `notes.account.spec.ts`: `delete_account_with_a_company_listed_shows_the_company_member_text_and_deletes_no_notes` (the password path), the same for the provider path in `notes.provider.spec.ts`, and `delete_account_on_403_company_member_shows_the_text_with_the_names`, `delete_account_with_only_the_personal_space_still_deletes_the_notes_first` (unchanged behaviour). Extend the listed options-body specs to include `org_creation`.
- [ ] **Step 2: See them fail** (`npm test -- <file>` per spec).
- [ ] **Step 3: Implement.** Read `account-api.ts` and `auth.service.ts` for how spec 0015 added `listOrgs` and the switch; follow them. No new dependency.
- [ ] **Step 4: Run** in `samples/notes-web`: `npm test`, `npx ng build`, `npm run check:docs`: green.
- [ ] **Step 5: Hand back** — uncommitted. Scan. Subject: `feat(samples): notes-web creates a company and guards account deletion`.

### Task 11: The sample: "New company..." in the picker

**Files:**
- Modify: `samples/notes-web/src/app/pages/notes.ts` (the picker and the small form), `src/app/texts.ts`, `src/app/pages/notes.orgs.spec.ts`

**Interfaces:**
- Consumes: Task 10's `AuthService.createCompany` and the `orgCreation` signal of `notes.ts`.
- Produces: the picker (a native `<select id="org-picker">`) is shown also with a single entry when `orgCreation` is true; it gains a last option "New company..." (three ASCII dots: see "Decisions taken"). The select's change handler treats that option apart: it does **not** call `switchTo`, it resets the select to the current company and opens a form with the company name (required; the server checks the rule); the button is disabled while the request runs; on success the sample is in the new company with no notes; on `company_limit_reached` it shows "You have reached the limit of companies."; on `org_creation_disabled` it shows "Creating companies is turned off." and hides the entry; on `400` it shows the name rule; on `permissions_changed` it refreshes.

- [ ] **Step 1: Write the failing tests** in `notes.orgs.spec.ts`: `the_picker_is_shown_with_one_entry_when_creation_is_on_and_hidden_when_off` (one entry + off: no picker, as today); `new_company_opens_the_form_and_a_created_company_becomes_current_with_no_notes`; `the_new_company_form_sends_one_request_when_submitted_twice` (Review Focus 5); `the_form_requires_a_name`; `409_shows_the_limit_text_and_keeps_the_current_company`; `org_creation_disabled_shows_its_text_and_hides_the_entry`; `400_shows_the_name_rule`; `permissions_changed_refreshes`; `the_form_can_be_cancelled_and_leaves_the_picker_as_it_was`.
- [ ] **Step 2: See them fail.**
- [ ] **Step 3: Implement** in the style of the existing picker (signals, standalone, plain ASCII texts, `texts.spec.ts` rules).
- [ ] **Step 4: Run** `npm test`, `npx ng build`, `npm run check:docs`: green.
- [ ] **Step 5: Hand back** — uncommitted. Scan. Subject: `feat(samples): New company in the notes-web company picker`.

### Task 12: The Playwright spec `13-create-org.spec.ts`

**Files:**
- Create: `samples/notes-web/e2e/13-create-org.spec.ts`
- Modify (only if a support helper is needed): `samples/notes-web/e2e/support/*`

**Interfaces:**
- Consumes: the stack of `12-orgs.spec.ts` (read it first: how it logs in the seed user, signs up a private person, and calls the API with a token).

- [ ] **Step 1: Write the spec** (it cannot be run by the implementer: the live stack is the verifiers'): a test "New company... creates a company and shows it as current with no notes" (the seed user picks "New company...", types a name, the account bar shows it, the notes list is empty); a test "a private person who created a company, back in Private, sees the company_member text and keeps the notes" (signs up as `10-private.spec.ts` does, creates a company, switches back to "Private", adds a note, tries to delete the account, sees the text with the company's name, the note is still there); the same test then deletes the company through the API with the company's token (the sample has no screen for `DELETE /auth/org`), and the deletion of the account succeeds; every test cleans up what it made so the spec runs again on the same stack, and ends with the seed user in the company where it started (as `12-orgs.spec.ts` does).
- [ ] **Step 2: Check it compiles and lints:** `npx tsc -p e2e/tsconfig.json --noEmit` and `npx playwright test --list` list the new tests.
- [ ] **Step 3: Hand back** — uncommitted; say plainly that the spec was not run. Scan. Subject: `test(samples): Playwright spec for creating a company`.

### Task 13: `scripts/e2e-orgs-create.sh` and the run sequences

**Files:**
- Create: `scripts/e2e-orgs-create.sh` (LF; the orchestrator marks it executable in the index)
- Modify: `scripts/e2e-tenancy.sh`, `scripts/e2e-hardening.sh` (run sequences: name it after `e2e-orgs.sh`), `scripts/e2e-signup.sh`, `scripts/e2e-private.sh`, `scripts/e2e-providers.sh` (step 1 and the header comment that quotes the options body expect `org_creation`), `README.md` (the list of e2e scripts: after `e2e-orgs.sh`)

**Interfaces:**
- Consumes: `scripts/e2e-orgs.sh` (helpers, the stack, the seed user, the private person's sign-up as `e2e-private.sh` does).

- [ ] **Step 1: Write the script** to run the sequence of the spec's Goal against the development stack: options say `org_creation` `self-service`; log in `dev@acme.test`; `POST /auth/orgs` `{"name":"Gamma"}` gives `201` with the entry shape; `POST /auth/switch` into it; `GET /auth/org/members` shows one member; `DELETE /auth/org` with the name and the password gives `204` and the account is kept; a private person (a **new address each run**) signs up, creates "Solo" (`201`), `DELETE /auth/account` gives `403 company_member`, switches into Solo, deletes it with `DELETE /auth/org` (`204`), logs in again (`org_kind` personal), `DELETE /auth/account` gives `204`. Cleanup: it deletes the companies it created even when a step fails (a `trap`), so it passes when run again on the same stack. No `docker compose` command may touch a stack other than the script's own project name.
- [ ] **Step 2: Edit** the other scripts and the README as listed (read each for how it quotes the body); mention where the script is run in the sequences.
- [ ] **Step 3: Static checks** (the implementer cannot run it live): `bash -n scripts/e2e-orgs-create.sh` and on each edited script; `shellcheck` if present on the machine.
- [ ] **Step 4: Hand back** — uncommitted; say plainly that no script was run. Scan. Subject: `test(scripts): e2e-orgs-create.sh and the options member in the e2e steps`.

### Task 14: The documents and the changelog

**Files:**
- Modify: `docs/integration/angular.md`, `docs/integration/python-fastapi.md`, `docs/security/threat-model.md`, `docs/operations/backup.md`, `docs/design.md`, `README.md` (the manifest paragraph), `CHANGELOG.md` (an `Unreleased` section: the main branch's changelog has none, add the heading above the newest release)

- [ ] **Step 1: Write** as the spec's "Documents" lists: `angular.md` and `python-fastapi.md` (offering "New company" from `GET /auth/options`, creating, switching after the `201`, the product's per-company data starting empty under the new `org_id`; deleting an account that is still in a company: `company_member`); the threat model: risks 66 and 67 verbatim from the spec after the risks of spec 0015, and the sentence before the list names spec 0015's and this spec's items (read it first; do not renumber 62 to 65); `backup.md` (`"via": "api"`); `CHANGELOG.md` (the endpoint, the manifest key, the option, the setting, the `DELETE /auth/account` answer); `design.md` (the note for slices 0015 to 0017 names this slice); `README.md` manifest paragraph (beside `signup`, `private`, `providers`).
- [ ] **Step 2: Check:** every number and name in the text against the code (`grep` the codes, the setting, the key); `npm run check:docs` in `samples/notes-web` if it reads these files.
- [ ] **Step 3: Hand back** — uncommitted. Scan. Subject: `docs: creating another company (POST /auth/orgs)`.

### Task 15: The final check (the orchestrator, not an implementer)

- [ ] The whole .NET suite once (build gate, then every test), in the background; the Angular suite and build; `npm run check:docs`.
- [ ] The whole-branch review on the most capable model available (an auth-code security review: lock order, the company limit under concurrency, `company_member` before the body), one fix dispatch, one scoped re-review.
- [ ] Ask the owner once whether to run `scripts/test-linux.sh`, `scripts/e2e-*.sh` and Playwright.
- [ ] Ask before pushing; open the PR only after the owner's yes.

## Decisions taken in this plan

- **The recount is built here (Task 1), not left to 0017.** Spec 0016 says whichever slice is built first adds it; this one is first. 0017's plan then only adds what is its own.
- **`CompanyService.CreateForAccountAsync` is a new method; `CreateAsync` keeps its signature.** The three existing callers keep their behaviour and their rows; the spec's "takes the `via` value `api` and the creator" is met by the new method that shares one private helper.
- **The limit is checked in the endpoint, with the settings object injected,** not inside `CompanyService`: the CLI shares the service and must not know the setting (spec: "the CLI does not use it").
- **The mail requests deleted before the recount are not restored** (see Task 1).
- **No audit kind, no migration, no change to `clients/python`**: as the spec says.
- **"New company..." with three ASCII dots.** The spec writes the entry with a Unicode ellipsis; the sample's `texts.spec.ts` requires printable ASCII, so the sample (texts, Playwright spec, guide) writes three dots. Users see the same entry.
- **The sample reads `GET /auth/options` in `notes.ts`,** not in `AuthService` (which never read it): one call when the notes page starts, as `login.ts` and `signup.ts` already do.
