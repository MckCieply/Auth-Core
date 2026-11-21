# Multiple Companies Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0015
- **Date:** 2026-11-21
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0015-multiple-companies.md`](../specs/0015-multiple-companies.md) — the plan argues from the spec; where they
  disagree, **the spec wins** and the disagreement is a finding (see [`docs/workflow.md`](../../workflow.md)). The choices the spec leaves open
  are in "Decisions taken in this plan" at the end; "Open questions for the owner" lists what the plan could not settle.
- **Based on:** `1d9129b` (`main`, with specs 0015, 0016 and 0017 merged). Every anchor below was checked against this base; an anchor in a file
  that an earlier task of this plan changes is written against the text that task leaves.
- **Acceptance map:** [`0015-acceptance-map.md`](0015-acceptance-map.md).
- **Branch:** `feat/0015-multiple-companies` from `main` (spec Decision 7).

**Goal:** One account belongs to several companies (and at most one personal space), every session works in one of them, a sign-in lands in
the company used last, `GET /auth/orgs` lists the person's spaces, `POST /auth/switch` moves the device to another one with the refresh cookie
alone, and a removal or a company deletion deletes the account only when it loses its last space.

**Architecture:** A membership gains `LastUsedAt`. `MembershipReader` is split into **the sign-in's space** (the order of the spec) and **the
membership in one company**; every caller that needs "the caller's company" now reads the token's `org_id` (company API, `GET /auth/me`,
`DELETE /auth/account`) or the session's company (refresh, switch). The session's company lives in the refresh token as two claims with no
destination, `session_org_id` and `session_org_joined`, set by one helper (`SessionCompany`) that `SessionPrincipal.Create`, the refresh and the
switch call. `AtomicSignInResult` reads those claims and, in the transaction that writes the session, locks that membership row before it
re-reads the account (taking it: `FOR NO KEY UPDATE` and `LastUsedAt` written; holding it at a refresh: `FOR KEY SHARE`). `/auth/switch` is a
fourth pass-through token-endpoint path: a new extraction handler checks the JSON body and turns it into a refresh-token request from the cookie
with an `org_id` parameter; the endpoint shares the refresh's checks, answers `404` before the sign-in (so the token is never redeemed) and
otherwise signs in through `AtomicSignInResult` with `org.switched`. Removals and company deletions delete membership rows first and hand only
the accounts left with none to `AccountDeletion`, which names the lost space.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, ASP.NET Identity, OpenIddict 7.7.1 (server in pass-through mode for the token endpoints,
reference refresh tokens), EF Core on PostgreSQL 16 (Npgsql), xUnit v3 with Testcontainers; Angular 21 (standalone components, signals), Vitest
on jsdom and Playwright (Chromium and WebKit); bash with curl and Python 3 (standard library only) for the e2e script. **No new package** of any
kind.

## Global Constraints

Values copied from the spec, one line each; every task includes them.

- **Bodies:** `application/json`, at most 8 KiB, a JSON object; anything else, another media type included, is `400 {"error":"invalid_request"}`; `application/json` with a charset other than UTF-8 is the `415` of spec 0008. `POST /auth/switch` is under the charset guard; `/auth/refresh` is not.
- **Ids:** an `org_id` that is missing, not a string, or not a UUID in its usual `8-4-4-4-12` form is `400 invalid_request` (spec 0005's rule; `IdInput.TryParse`).
- **Answers** carry `Cache-Control: no-store` and `Pragma: no-cache`, as every answer under `/auth` does.
- **Memberships:** any number of companies and at most one personal space per account, each with exactly one role; every account belongs to at least one space. The paths that make memberships do not change: invitation preview and acceptance stay `409 already_member` / `private_account_exists`; sign-up still refuses an address with an account.
- **`Memberships.LastUsedAt`:** `timestamptz`, nullable, `null` for every existing row; written by every sign-in and every switch in the session's transaction; **never by a refresh**.
- **Session claims:** `session_org_id` (the space id) and `session_org_joined` (`JoinedAt` as whole Unix microseconds); no destination: refresh token only; neither is a tenancy claim; the access token's claims stay `sub`, `org_id`, `org_kind`, `roles`, `permissions`.
- **Sign-in order:** `LastUsedAt` latest first, a `null` after every non-null; then a company before a personal space; then `JoinedAt` earliest first; then the space id ordinally.
- **Refresh:** reads the membership in the session's company; gone or another `JoinedAt` is the uniform `401 {"error":"invalid_grant"}`; a token without the claims takes the sign-in's space and the next token carries it.
- **`GET /auth/orgs`:** bearer token; `general` limit; `200 {"orgs":[{"id","name","kind","role":{"id","name"},"current"}]}`, every membership, ordered by `name` ordinal ignoring case, then `id`; `current` iff `id` is the token's `org_id`; `401` without a valid token; account gone or no membership `403 permissions_changed`.
- **`POST /auth/switch` `{"org_id"}`:** refresh cookie only; `refresh` limit with `/auth/refresh`; order: 1. body `400`/`415` (token not used up), 2. cookie or session would not survive a refresh `401 invalid_grant`, 3. not a space of the account `404 {"error":"not_found"}` (token not used up), 4. one transaction: redeemed as a refresh, next pair for the chosen space, `LastUsedAt` set, `org.switched` when the space differs; `200` with the body and cookie of `/auth/refresh`; outage `503` with no body and no cookie, token not used up.
- **Company API and `GET /auth/me`:** act on the token's `org_id`, checked against the database on every call; no membership there: `403 permissions_changed` when the token claims the needed permission (or, needing none, carries an `org_id`), else `403 forbidden`; personal space `403 forbidden`.
- **`DELETE /auth/account`:** personal token and no other membership: as spec 0010; personal token and also a company member: `403 forbidden`; company token `403 forbidden`; token's company no longer a membership `403 permissions_changed`.
- **Removal / company deletion:** delete the membership(s); an account left with none is deleted as in spec 0010 (`member_removed`, `org_deleted`); `account.deleted.org_id` names the space it lost; `remove-member` prints `Member removed; the account is deleted.` or `Member removed; the account is kept.`
- **Audit:** 29 kinds; `org.switched` with `actor_user_id` = `subject_user_id` = the account, `subject_email`, `org_id`/`org_name` of the space switched to, `details.from_org_id`; none for a switch to the session's own company; `refresh.reuse_detected` names the session's company of the reused token.
- **OpenAPI:** 38 operations, 25 under the charset guard; only `/auth/refresh` and `/auth/switch` document a `503`.
- **Python package:** no code change. **Sample backend:** no change.
- **Dev seed:** `Auth:DevSeed:SecondOrgName` optional; when set, that company with the manifest's default roles, and the first seed user a plain member of it.
- **Workflow** ([`docs/workflow.md`](../../workflow.md)): local only, Conventional Commits in English, documentation in English. **The orchestrator makes the commits** (real dates, author `Alex`, no trailers): implementers leave their changes uncommitted, never run a git command that changes state, and end with the "Hand back" step (files and a proposed subject, never a git command). One Sonnet implementer per task writes the code of that task from this plan's description, runs it and fixes what breaks. **This plan holds no finished code**: it gives behaviour, names, signatures, test names and their assertions; the implementer writes the code and the tests decide.
- **The EF migration** (Task 1) is created with `"C:/Program Files/dotnet/dotnet.exe" tool run dotnet-ef migrations add AddMembershipLastUsedAt --project src/Auth.Infrastructure --startup-project src/Auth.Server` (`dotnet-ef` 10.0.12 from `.config/dotnet-tools.json`; `tool restore` first if it is missing); no test or document names its id.
- **.NET on this machine:** the build gate is `"C:/Program Files/dotnet/dotnet.exe" build Auth-Core.slnx -warnaserror -v q -nologo -maxcpucount:2`; tests run as `tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.<Class> [-class ...]` after that build (`dotnet test` at the root finds no tests). Docker must be running (Testcontainers). The analyzers are strict under `-warnaserror`: `ArgumentNullException.ThrowIfNull`, explicit `StringComparison` (CA1307/CA1309/CA1310), `CultureInfo.InvariantCulture` (CA1305), `[LoggerMessage]` for logs (CA1848), no constant array as an argument (CA1861: a `static readonly` field), the `CancellationToken` last (CA1068).
- **Implementers run only their task's test classes and the classes their task edits** (`-maxThreads 2`). They never run the whole .NET suite, a live e2e script or Playwright: the verifiers do.
- **Python:** `python` (Python 3.10 on this machine; the `python3` on `PATH` is a Store shim; the `C:/p6v` virtual environment of older plans does not exist here).
- **Angular:** in `samples/notes-web`: `npm ci` once when `node_modules` is missing, then `npm test`, `npx ng build` and `npm run check:docs`. Texts are plain printable ASCII (`texts.spec.ts` checks it).
- **No `docker compose up`/`down` of any stack and no stopping of containers by implementers**; every compose command of a script carries its own project name, never `auth-core`.
- **Line endings:** shell scripts are LF (the orchestrator marks a new one executable in the index); `.cs`, `.ts` and `.md` files keep the endings each file has (the C# and TypeScript sources and the plans are CRLF, the YAML files LF). Git Bash `sed -i` strips the CR of a CRLF file: edit with the Edit tool, never with `sed -i`.
- **Unicode escapes stay out of the source.** A backslash followed by `u` and four hex digits typed into a tool parameter arrives as the character. Build such text with `(char)0x...`. Each task's "Hand back" step scans the files it touched ("scan the touched files") with
  `python -c "import sys,unicodedata as u; bad=[(f,i,hex(ord(c))) for f in sys.argv[1:] for i,c in enumerate(open(f,encoding='utf-8').read()) if (u.category(c)=='Cc' and c not in '\t\r\n') or 0xFDD0<=ord(c)<=0xFDEF or (ord(c)&0xFFFE)==0xFFFE or ord(c)>0xFFFF]; print(bad or 'clean')" <files>`:
  it must print `clean`.
- **Every test is written first and seen to fail** for the reason the task names (not a compile error alone where a behaviour test is possible: build the new names first as stubs that throw or return the old behaviour, then see the assertion fail).

## Checked on this code before the plan was written

Every subsystem the spec touches was read at `1d9129b`; the plan relies on these facts.

- **`MembershipReader.ReadAsync`** (`src/Auth.Server/Tenancy/MembershipReader.cs`) is the single pick of "the user's space" (`orderby company.Kind, membership.JoinedAt`). Callers: `CompanyAccess`, `CompanyGuard.EnterAsync`, `RefreshEndpoint`, `RefreshReuseAuditHandler`, `AccountDeletionEndpoint` (twice), `LoginEndpoint`, `ProviderSignIn`, `ProviderLinkEndpoint` (twice), `ProviderFinishEndpoint`, `ProviderExchangeEndpoint`, `SignupCompleteEndpoint`; in the tests `TenancyTestBase.TenantOfAsync` (`Infrastructure/TenancyTestBase.cs:48`). `TenantContext` is positional and also built by hand in `AuditLogTests.cs:105`.
- **`Membership`** (`src/Auth.Infrastructure/Persistence/Membership.cs`): key `(UserId, CompanyId)`, `RoleId`, `JoinedAt`; cascade from user and company; the role is a restricting foreign key `(CompanyId, RoleId)`.
- **Sessions.** `SessionPrincipal.Create` builds every sign-in's principal (`sub`, `session_start`, `session_stamp`, `TenantClaims.Apply`, destinations from `TenantClaims.DestinationsOf`, which gives the access token only `sub` and the tenancy claims). `RefreshEndpoint` rebuilds from `result.Principal.Claims` and returns `new AtomicSignInResult(principal, null, whenGone: InvalidGrant)`. `RefreshTokenIssuanceHandler` forces a refresh token on every sign-in; `AccessTokenClaimFilter` strips the tenancy claims from the refresh token.
- **`AtomicSignInResult`** (`src/Auth.Server/Login/AtomicSignInResult.cs`): constructor `(ClaimsPrincipal principal, AuditEntry? entry, IDbContextTransaction? opened = null, Func<Task<IResult>>? whenGone = null, Action<HttpContext>? whenCommitted = null)`; it holds the body in memory, begins (or uses) the transaction, re-reads the account with `FOR KEY SHARE` (`AccountExistsAsync`, internal static, also used by the provider link), signs in, stages the entry, saves and commits on `200`. Redemption of the refresh token happens inside `Results.SignIn`, so **an `IResult` returned by the endpoint before the sign-in leaves the token unredeemed** (to be confirmed by a test, Task 11).
- **Token-endpoint paths** (`Tokens/OpenIddictSetup.cs:64`): login, refresh, signup complete, provider exchange/link/finish; extraction handlers: `JsonLoginRequestHandler` (in `ExtractPostRequest`'s slot), `RefreshRequestHandler` (+250), `SignupCompleteRequestHandler` (+300), `ProviderRequestHandler` (+350), `UnhandledTokenRequestGuard` (+500).
- **Refresh-path predicates:** `RefreshRequestHandler.IsRefreshPath` is used by `AccountEndpoints.cs:41`, `ErrorHandling.cs:49` (the `503` with `Retry-After`), `JsonCharsetGuard.cs:35` (left out of the guard), `SessionResponseHandler.cs:41` (uniform `401`, compact body, `503`) and `OpenIddictSetup.cs:66`.
- **Removals.** `MemberService.RemoveAsync` returns `Outcome` and calls `AccountDeletion.DeleteAsync(userId, MemberRemoved, current, ct)` unconditionally (line 160); callers `OrgMemberEndpoints.cs:49`, `OperatorCommands.RemoveMemberAsync` (line 70), `OrgMemberTests.cs:533` and `:537`; `AdminCli.cs:113-121` prints `Member removed; the account is deleted.` unconditionally. `CompanyDeletionService.DeleteAsync` deletes invitations, then hands every member to `AccountDeletion` (line 70), then deletes the roles and the company.
- **`AccountDeletion`**: two overloads (one id, a collection); deletes mail requests and pending sign-ups, reads the accounts `FOR UPDATE` (line 83), reads their spaces ordered `Kind, JoinedAt` and names `g.First()` in `account.deleted` (lines 95-102), then streaks, tokens, authorizations, the users (memberships by cascade), the personal companies. Other caller: `AccountsWithoutSpaceCleanup.cs:64`.
- **Locks in use:** company lock (`Companies` row `FOR UPDATE`), address lock (advisory), account row `FOR KEY SHARE` (sign-in), `FOR SHARE` (provider link, `ProviderLinkEndpoint.cs:162`), `FOR UPDATE` (`AccountDeletion`, `InviteAcceptance.cs:288`). A membership row is locked today only by its deletion (by cascade or directly).
- **Reuse audit:** `RefreshReuseAuditHandler` runs at `ValidateTokenEntry.Order - 1`, reads the token entry by `context.TokenId`, and names the company from `memberships.ReadAsync`.
- **Rate policy:** `RatePolicies.Classify` maps `/auth/refresh` to `Refresh` (`RatePolicy.cs:72`); other `/auth` POSTs not named are `General`; every `/auth` GET is `General`.
- **Audit kinds:** 28 (`AuditKinds.All`); pinned by `AuditProviderFlowsTests.The_audit_log_knows_twenty_eight_kinds` and `AuditTableTests.The_kinds_are_the_twenty_eight_of_the_contract`.
- **OpenAPI:** pinned by `OpenApiHardeningTests` (36 operations at :119, the charset-guard predicate at :139, 24 at :153, the single `503` at :157-164).
- **Dev seed:** `DevUserSeeder` (keys `Auth:DevSeed:*`, `OrgNameKey`, `DefaultOrgName = "Development"`); `EnsureMemberAsync` returns early when the user has any membership; `AuthAppFactory` blanks the dev settings it does not want (`UnverifiedEmailKey` at :96-97). `appsettings.Development.json` has no `DevSeed` section; `deploy/docker-compose.yml:56-60` passes `Auth__DevSeed__*`; `.env.example` lists the variables. The notes manifest's default roles are `admin ["*"]`, `user`, `viewer`, in that order.
- **Sample:** `auth.service.ts` (`startSession` :334, `loadMe` :404, `dropSession` :440, `exchange` :455-485 renews the token after a refresh and does **not** read `/auth/me`), `auth.interceptor.ts` (`TOKEN_PATH_PREFIXES = ['/api/', '/auth/org']` covers `/auth/orgs`; `/auth/switch` is in neither list, so it gets no bearer token and no refresh-and-retry), the account bar in `pages/notes.ts:74-93`, texts in `texts.ts`; Playwright runs one worker, specs `01`–`11`, Chromium then WebKit, on one stack.
- **Scripts:** run sequences in `scripts/e2e-tenancy.sh:51-54` and `scripts/e2e-hardening.sh:33` (ends `&& scripts/e2e-private.sh`); helpers are local to each script (`e2e-tenancy.sh:77-240`).
- **Documents:** `docs/security/threat-model.md` (risk 61 at :292, "28 kinds" at :88), `docs/operations/backup.md:252`, `README.md:147`, `docs/integration/angular.md` (:214-235), `docs/integration/python-fastapi.md` (:166, step 8 at :251), `docs/design.md` (:35, :111, the Beyond MVP row, "Still out" :381-382), `clients/python/README.md`; `CHANGELOG.md` has **no** `Unreleased` section (its first is `## 0.2.0 - 2026-11-21`).

## Review Focus

The inputs and failure modes the spec implies that a person using this software is most likely to meet and that no acceptance criterion names,
most likely first; each has its test in the task named.

1. **Two tabs of one browser race a switch.** The person switches in tab 1 while tab 2 refreshes with the cookie it read before: within the
   grace window tab 2 gets a pair for the old company and the device may end up there; the sample must notice the change of `org_id` and say
   so instead of showing one company's notes under the other's name
   (`SwitchTests.The_old_cookie_within_the_grace_window_gets_a_pair_for_the_old_company`, Task 11; `auth.service.orgs.spec.ts`
   "a refresh that brings another org_id reloads the notes and shows the notice", Task 13).
2. **Ids as clients write them.** An `org_id` in upper case is a UUID and switches; one with braces, without hyphens or with spaces is
   `400` (`Guid.TryParse` accepts those, `IdInput.TryParse` must decide)
   (`SwitchTests.An_org_id_in_upper_case_switches`, `SwitchRequestTests.An_org_id_with_braces_without_hyphens_or_with_spaces_is_400`,
   Tasks 10 and 11).
3. **Company names that differ only in case, or are not ASCII.** The list stays stable: `acme` and `Acme` are ordered by id, `Äpfel` sorts by
   its code points after `Zeta` (ordinal ignoring case), never by culture
   (`OrgListTests.Names_that_differ_only_in_case_are_ordered_by_id`, `OrgListTests.Names_are_ordered_ordinally_not_by_culture`, Task 9).
4. **Membership times finer than a microsecond.** A row written by code or a test helper with a .NET time finer than PostgreSQL keeps (it
   rounds) must still refresh: both sides of the `JoinedAt` comparison come from the database
   (`SessionCompanyRefreshTests.A_membership_written_with_a_time_finer_than_a_microsecond_still_refreshes`, Task 4).
5. **The browser tests and the e2e sequence run again on the same stack.** The Playwright spec ends with the seed user back in the first
   company (WebKit runs after Chromium on the same stack), and `scripts/e2e-orgs.sh` ends with the seed user removed from the second company,
   which a restart of `auth` adds back (the seed is idempotent per company)
   (`12-orgs.spec.ts` "switching back leaves the seed user where it started", Task 14; `scripts/e2e-orgs.sh` step 9, Task 15;
   `DevCompanySeedTests.A_restart_puts_a_removed_seed_user_back_in_the_second_company`, Task 12).

## Lock order (binding for every task)

The order of locks in one transaction is: **company lock, address lock, membership rows, account row.** Today the paths that touch a
membership row do so before the account row (a removal deletes the membership and only then lets `AccountDeletion` lock the account, as spec
0017 "Locks" keeps; `InviteAcceptance.cs:145` deletes a moving account's personal membership before `UpdateSecurityStampAsync` at :152 writes
the account row; its `LockAccountAsync` at :257 runs only for an account with no membership), while every sign-in locks the account row and no
membership. A sign-in that held the account row while it waited for a membership would deadlock with them. So:

- `AtomicSignInResult` locks the membership row(s) **before** it re-reads the account with `FOR KEY SHARE` (Task 3).
- `AccountDeletion` locks the memberships of the accounts it deletes (`FOR UPDATE`, ordered by user id then company id) **before** it reads the
  accounts `FOR UPDATE` (Task 8); its deletion of the users then deletes rows it already holds.
- `ProviderLinkEndpoint` (the only path that holds an account row lock before the sign-in: `FOR SHARE` on the hash) picks and locks the
  sign-in's membership **before** that `FOR SHARE` (Task 3).
- `InviteAcceptance` does not change (its order already is membership, then account row).
- Several membership rows in one transaction: only a switch holds two (the session's company `FOR KEY SHARE`, then the target
  `FOR NO KEY UPDATE`); those two modes do not conflict with each other, so two switches in opposite directions cannot deadlock; a removal
  locks one row; `AccountDeletion` locks its rows ordered by user id then company id.

A test per path proves that the path holds no account row lock while it waits for a membership row (the test holds the membership row, the path
waits, the test then locks the account row with `lock_timeout` set and must get it).

## Overrides by 0016/0017 (not planned here)

Specs 0016 and 0017 change parts of this contract after it ships. This plan builds 0015 as written and does not build these; the notes say where
a later plan will touch what this one builds.

- **0016, `DELETE /auth/account`:** `403 company_member` replaces this plan's `403 forbidden` for a personal-space token of an account that is
  also a company member, and answers any token of an account with a personal space and a company membership (0016 :194, :198). The check this plan adds in `AccountDeletionEndpoint` (Task 7) is the one 0016 re-labels.
- **0016, `POST /auth/orgs`** (create a company) on the path of this plan's `GET /auth/orgs`; **0016's picker** shows also with a single entry
  when the options allow creation; OpenAPI counts rise again (39 and 26); `13-create-org.spec.ts` follows this plan's `12-orgs.spec.ts`;
  `scripts/e2e-orgs-create.sh` follows `scripts/e2e-orgs.sh`.
- **0017, `AccountDeletion.DeleteAsync`:** under the account row lock it counts each account's memberships again for `member_removed` and
  `org_deleted` and keeps an account that has one. This plan's removal counts the remaining memberships **without** an account lock (Task 8),
  as 0017 requires ("No removal path takes an account row lock before the routine does"); the recount is 0017's.
- **0017, acceptance keeps memberships:** an acceptance adds one membership and keeps the others; `409 already_member` narrows to "already a
  member of the invited company" (0017 :86); an acceptance writes the new membership's `LastUsedAt`; its answer gains the invited `org_id`. Until then this plan's tests that pin `already_member` stay green.
- **Note for the 0017 plan (lock order):** 0017's move deletes the personal membership after it locks the account row `FOR UPDATE`. With this
  plan's order (membership rows before the account row), a sign-in into that personal space and the move can deadlock unless the 0017 plan
  locks the personal membership row before the account row.

## File Structure

New and changed files by responsibility. Everything under `src/Auth.Server/` unless said otherwise; tests under `tests/Auth.IntegrationTests/`.

| Area | Files | Responsibility |
| --- | --- | --- |
| Schema | `src/Auth.Infrastructure/Persistence/Membership.cs`, `AuthDbContext.cs` (comments only), the migration | `LastUsedAt` (Task 1) |
| Reading memberships | `Tenancy/MembershipReader.cs` and every caller of `ReadAsync` | the sign-in's space, the membership in one company, the list, the count (Task 2) |
| The session's company | `Sessions/SessionCompany.cs` (new), `Sessions/SpaceLocks.cs` (new), `Sessions/SessionPrincipal.cs`, `Login/AtomicSignInResult.cs`, `Providers/ProviderLinkEndpoint.cs` | the two claims, the row lock, `LastUsedAt` at sign-in (Task 3) |
| Refresh | `Sessions/RefreshEndpoint.cs` | keeps the session's company; old tokens (Task 4) |
| Reuse audit | `Sessions/RefreshReuseAuditHandler.cs` | the reused token's company (Task 5) |
| Token's company | `Tenancy/CompanyAccess.cs`, `Tenancy/CompanyGuard.cs`, `Api/TenancyEndpoints.cs` (texts) | company API and `/auth/me` (Task 6) |
| Self-deletion | `Api/AccountDeletionEndpoint.cs`, `Api/TenancyEndpoints.cs` (text) | the token's company, the new `403 forbidden` (Task 7) |
| Removals | `Tenancy/MemberService.cs`, `Tenancy/CompanyDeletionService.cs`, `Tenancy/AccountDeletion.cs`, `Tenancy/OperatorCommands.cs`, `Admin/AdminCli.cs`, `Api/OrgMemberEndpoints.cs`, `README.md` | delete the account only with its last space (Task 8) |
| List | `Api/OrgListEndpoint.cs` (new), `Tenancy/Contracts.cs`, `Api/TenancyEndpoints.cs` | `GET /auth/orgs` (Task 9) |
| Switch plumbing | `Sessions/SwitchRequestHandler.cs` (new), `Sessions/SessionPaths.cs` (new), `Tokens/OpenIddictSetup.cs`, `Sessions/SessionResponseHandler.cs`, `Api/ErrorHandling.cs`, `RateLimiting/RatePolicy.cs`, `Api/AccountEndpoints.cs`, `Sessions/SwitchEndpoint.cs` (new, a stub) | the path, its body, its answers, its limit (Task 10) |
| Switch | `Sessions/SwitchEndpoint.cs`, `Sessions/RefreshEndpoint.cs`, `Audit/AuditKinds.cs`, `Api/AccountEndpoints.cs` (OpenAPI) | the target, `404`, `org.switched` (Task 11) |
| Dev seed | `Seeding/DevUserSeeder.cs`, `appsettings.Development.json`, `deploy/docker-compose.yml`, `.env.example`, `tests/.../Infrastructure/AuthAppFactory.cs` | two companies (Task 12) |
| Sample | `samples/notes-web/src/app/account-api.ts`, `auth/auth.service.ts`, `pages/notes.ts`, `texts.ts`, `app.ts` and their specs | the picker, the notice (Task 13) |
| Browser check | `samples/notes-web/e2e/12-orgs.spec.ts`, existing specs if a picker breaks one | Playwright (Task 14) |
| Live check | `scripts/e2e-orgs.sh` (new), `scripts/e2e-tenancy.sh`, `scripts/e2e-hardening.sh`, the headers of the other scripts that list the sequence | the Goal over the network (Task 15) |
| Documents | `docs/integration/angular.md`, `docs/integration/python-fastapi.md`, `clients/python/README.md`, `docs/security/threat-model.md`, `docs/operations/backup.md`, `docs/design.md`, `CHANGELOG.md` | Task 16 |

Shared test helpers (Tasks 1 to 4 add them; later tasks use them): in `tests/Auth.IntegrationTests/Infrastructure/`:
- `TenancyTestBase.AddMembershipAsync(Guid userId, Guid companyId, string role, DateTimeOffset? joinedAt = null)` — inserts a membership with the
  company's role of that name (default `JoinedAt` = the factory clock's now plus one hour, as `AddLaterMembershipAsync` does, which then
  forwards to it); `TenancyTestBase.RemoveMembershipAsync(Guid userId, Guid companyId)` (the existing one-argument overload, which deletes
  all of a user's memberships, stays); `TenancyTestBase.SetMemberRoleAsync(Guid userId, Guid companyId, string role)` (the existing overload,
  which assumes one membership, stays); `TenancyTestBase.LastUsedAtAsync(Guid userId, Guid companyId)` → `DateTimeOffset?`.
- `StoredTokens.RefreshClaimsAsync(AuthAppFactory factory, string refreshToken)` → the claims of the stored refresh token (moved out of the
  private `StoredRefreshTokenClaimsAsync` of `TenantClaimsTests.cs:137`, which then calls it); `StoredTokens.StatusAsync(factory, refreshToken)`
  → the stored token's status (`valid`, `redeemed`, `revoked`).
- `HeldLock.MembershipRowAsync(AuthAppFactory factory, Guid userId, Guid companyId)` — a transaction holding that one membership row
  `FOR UPDATE`; on any `HeldLock`: `LockAccountAsync(Guid userId)` (`SET LOCAL lock_timeout = '2s'`, then the account row `FOR UPDATE`; throws on
  the timeout) and `DeleteMembershipAsync(Guid userId, Guid companyId)`. Waiting for a blocked request is the existing static
  `HeldLock.WaitForWaitersAsync(AuthAppFactory factory, int count, params Task<HttpResponseMessage>[] pending)`: every "wait for N waiters"
  below means `HeldLock.WaitForWaitersAsync(Factory, N, <the pending requests>)`.
- `ShareLockInterceptor` (Task 3) fires on the first row lock of a sign-in: a command holding `FOR NO KEY UPDATE` or `FOR KEY SHARE`.
- `SessionApi.Switch(HttpClient client, string? refreshToken, string body, string contentType = "application/json")` → the response of
  `POST /auth/switch` with the cookie as `SessionApi.Refresh` sends it; `SessionApi.SwitchTo(HttpClient client, string? refreshToken,
  Guid orgId)` (the body `{"org_id":"<id>"}`); `SessionApi.SwitchPath = "/auth/switch"` (Task 10).

## The tasks

| Task | Deliverable |
| --- | --- |
| 1 | `Memberships.LastUsedAt` and the shared membership helpers |
| 2 | `MembershipReader` split: the sign-in's space, the membership in one company, the list, the count; `TenantContext.JoinedAt` |
| 3 | The session's company claims; `AtomicSignInResult` locks the membership and writes `LastUsedAt`; the link's lock order |
| 4 | The refresh keeps the session's company; tokens issued before this version |
| 5 | `refresh.reuse_detected` names the reused token's company |
| 6 | The company API and `GET /auth/me` act on the token's company |
| 7 | `DELETE /auth/account` judged on the token's company |
| 8 | Removals and company deletions keep accounts with another space; `AccountDeletion` names the lost space; the CLI line |
| 9 | `GET /auth/orgs` |
| 10 | `POST /auth/switch`, part 1: the path, its body checks, its answers, its limit |
| 11 | `POST /auth/switch`, part 2: the target, `404`, `LastUsedAt`, `org.switched`, OpenAPI |
| 12 | The development seed's second company |
| 13 | The sample: the picker, the switch, the notice after a refresh |
| 14 | The Playwright spec `12-orgs.spec.ts` |
| 15 | `scripts/e2e-orgs.sh` and the run sequences |
| 16 | The documents and the changelog |
| 17 | The final check (verifiers) |

Tasks run in order; 13 to 16 need 9 to 12 but not each other, and may run in parallel once 12 is in. Each task leaves the build gate and the
tests of the files it touches green.

**Contract counts that change** (tests that enumerate the contract; the task that changes one says so): `OpenApiHardeningTests` and
`OpenApiTests` (Task 6: texts; Task 7: text; Task 9: 36 → 37 operations; Task 11: 38, 24 → 25 under the charset guard, the `503` check names
`/auth/switch`), `AuditProviderFlowsTests` and `AuditTableTests` (Task 11: 28 → 29 kinds), `RatePolicyTests` (Tasks 9 and 10), `AuditLogTests`
(Task 2: the hand-built `TenantContext`), `AdminCliTests` (Task 8: the second line of `remove-member`).

---

### Task 1: `Memberships.LastUsedAt` and the shared membership helpers

**Files:**
- Modify: `src/Auth.Infrastructure/Persistence/Membership.cs` (the property and the class comment: "a user belongs to several companies and at most one personal space"), `src/Auth.Infrastructure/Persistence/AuthDbContext.cs` (the comment at :95 only)
- Create: the migration (generated: three files)
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs` (`AddLaterMembershipAsync` at :163), `tests/Auth.IntegrationTests/Infrastructure/HeldLock.cs`
- Create (tests): `tests/Auth.IntegrationTests/MembershipLastUsedTests.cs`

**Interfaces:**
- Produces: `Membership.LastUsedAt` (`DateTimeOffset?`, settable); the helpers of "Shared test helpers" above except `StoredTokens` (Task 3).

- [ ] **Step 1: Write the failing tests** in `MembershipLastUsedTests`:
  - `The_column_is_a_nullable_timestamptz`: reads `information_schema.columns` for `Memberships.LastUsedAt`: `data_type` is `timestamp with
    time zone`, `is_nullable` is `YES`, no default.
  - `Existing_memberships_read_null_after_the_migration`: the development company's seeded membership (written before any sign-in) has
    `LastUsedAt` `null`; nothing else of the row changes (`RoleId`, `JoinedAt` as seeded).
  - `The_helpers_add_and_remove_one_membership_of_several`: `AddMembershipAsync` adds a second membership with the named role;
    `RemoveMembershipAsync(user, company)` removes only that one; `SetMemberRoleAsync(user, company, role)` changes only that one.
  - `A_held_membership_row_lets_the_account_row_be_locked`: with `HeldLock.MembershipRowAsync` held, a second `HeldLock` can
    `LockAccountAsync` the same user at once (proves the helpers themselves take no account lock).
- [ ] **Step 2: See them fail:** the column does not exist; the helper overloads do not exist (add them as stubs first, so the column
  tests fail on their assertions).
- [ ] **Step 3: Implement:** the property, the migration (generated; check that its `Up` adds exactly one nullable column and `Down` drops it,
  nothing else — the model snapshot changes by that column only), the helpers.
- [ ] **Step 4: Run** `MembershipLastUsedTests`, `AccountDeletionTests`, `AccountDeletionEndpointTests` (they use the old helpers): green.
- [ ] **Step 5: Hand back** — uncommitted. Files as listed. Scan the touched files. Proposed subject:
  `feat(tenancy): memberships remember when they were last used`.

### Task 2: `MembershipReader` split; `TenantContext.JoinedAt`

**Files:**
- Modify: `src/Auth.Server/Tenancy/MembershipReader.cs`, every caller of `ReadAsync` (mechanical rename to `ReadSignInSpaceAsync`; the callers are listed in "Checked on this code"), `tests/Auth.IntegrationTests/AuditLogTests.cs:105`, `tests/Auth.IntegrationTests/Infrastructure/TenancyTestBase.cs:48` (`TenantOfAsync` calls `ReadSignInSpaceAsync`)
- Create (tests): `tests/Auth.IntegrationTests/MembershipReaderTests.cs`

**Interfaces:**
- Consumes: `Membership.LastUsedAt` (Task 1).
- Produces:
  - `TenantContext` gains `DateTimeOffset JoinedAt` as its **last** positional member (after `Kind`).
  - `MembershipReader.ReadSignInSpaceAsync(Guid userId, CancellationToken)` → `TenantContext?`: the sign-in order of the spec.
  - `MembershipReader.ReadInCompanyAsync(Guid userId, Guid companyId, CancellationToken)` → `TenantContext?`: the membership in that space
    (company or personal), `null` when there is none.
  - `MembershipReader.ListAsync(Guid userId, CancellationToken)` → `IReadOnlyList<TenantContext>`: every membership, in no promised order
    (the caller orders).
  - `MembershipReader.CountAsync(Guid userId, CancellationToken)` → `int`.
  - `ReadAsync` is removed (every caller now calls `ReadSignInSpaceAsync`; behaviour unchanged for one membership).

**Behaviour:**
- One private projection turns a row into a `TenantContext` as `ReadAsync` does today (the personal space's permissions from the manifest's
  `private.permissions`, never `*` or a company permission; `HoldsAll` never for a personal space) — the three reads share it.
- The sign-in order is done **in SQL**: first whether `LastUsedAt` is null (non-null first — PostgreSQL sorts `DESC` with nulls **first** by
  default, so a plain descending order is wrong), then `LastUsedAt` descending, then `Kind` (organization `1` before personal `2`), then
  `JoinedAt`, then the company id (PostgreSQL's `uuid` order compares the 16 bytes in the order of the canonical text, so it equals the
  ordinal order of the lowercase text; an order of `Guid.ToByteArray()` bytes is not, since its first three groups are little-endian).

- [ ] **Step 1: Write the failing tests** in `MembershipReaderTests` (a user with memberships inserted by the helpers, times set explicitly):
  - `The_sign_in_space_is_the_latest_used_first`: A used at t1, B at t2 > t1 → B.
  - `A_membership_never_used_comes_after_every_used_one`: A never used, B used → B, also when A joined earlier and is a company and B personal.
  - `Without_use_a_company_comes_before_the_personal_space`: personal joined first, company later, both unused → the company (today's order).
  - `Without_use_the_earlier_joined_company_comes_first`: two companies, unused → the earlier `JoinedAt`.
  - `With_equal_times_the_lower_id_comes_first`: two companies with the same `JoinedAt`, unused, with the ids
    `00000001-0000-0000-0000-000000000000` and `01000000-0000-0000-0000-000000000000` (text order puts the first first; `ToByteArray()`
    order puts the second first) → the first. The companies are inserted with those ids directly.
  - `The_membership_in_a_company_is_that_one_or_none`: `ReadInCompanyAsync` for each of two companies gives that company's role and
    `JoinedAt`; for a company the user is not in, `null`; for a personal space it gives the private permissions.
  - `The_list_holds_every_membership_and_the_count_matches`: `ListAsync` has both, `CountAsync` is 2; for a user with none, empty and 0.
  - `The_context_carries_the_stored_join_time`: `JoinedAt` equals the stored value to the microsecond.
- [ ] **Step 2: See them fail** (stubs that call the old pick).
- [ ] **Step 3: Implement**; rename every caller (`grep -rn "memberships.ReadAsync\|\.ReadAsync(" src/Auth.Server` and check each hit is a
  `MembershipReader`); fix `AuditLogTests.cs:105` (pass a `JoinedAt`).
- [ ] **Step 4: Run** `MembershipReaderTests`, `TenantClaimsTests`, `LoginTests`, `RefreshTests`, `OrgEndpointTests`, `AuditLogTests`,
  `AccountDeletionEndpointTests`, `ProviderLinkTests`, `ProviderExchangeTests`: green, unchanged.
- [ ] **Step 5: Hand back.** Scan the touched files. Proposed subject: `refactor(tenancy): read the sign-in's space or the membership in one company`.

### Task 3: The session's company; `AtomicSignInResult` locks the membership and writes `LastUsedAt`

**Files:**
- Create: `src/Auth.Server/Sessions/SessionCompany.cs`, `src/Auth.Server/Sessions/SpaceLocks.cs`
- Modify: `src/Auth.Server/Sessions/SessionPrincipal.cs`, `src/Auth.Server/Sessions/SessionPolicy.cs` (the two claim names, with comments as `StartClaim` has), `src/Auth.Server/Login/AtomicSignInResult.cs`, `src/Auth.Server/Providers/ProviderLinkEndpoint.cs`
- Create (tests): `tests/Auth.IntegrationTests/Infrastructure/StoredTokens.cs`, `tests/Auth.IntegrationTests/SessionCompanySignInTests.cs`, `tests/Auth.IntegrationTests/SignInLockOrderTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/TenantClaimsTests.cs` (its private helper calls `StoredTokens`; no assertion changes),
  `tests/Auth.IntegrationTests/Infrastructure/ShareLockInterceptor.cs` (see below)

**Interfaces:**
- Consumes: `TenantContext.JoinedAt`, `MembershipReader.ReadSignInSpaceAsync` (Task 2), `Membership.LastUsedAt` (Task 1).
- Produces:
  - `SessionPolicy.CompanyClaim = "session_org_id"`, `SessionPolicy.JoinedClaim = "session_org_joined"`.
  - `readonly record struct SessionSpace(Guid CompanyId, long JoinedMicros)`; `static class SessionCompany` with
    `long MicrosOf(DateTimeOffset)` (whole Unix microseconds of the UTC instant, rounding down), `void Apply(ClaimsIdentity, TenantContext)`
    (sets both claims, replacing any present, values as invariant strings, no destination) and `SessionSpace? Read(ClaimsPrincipal)` (`null`
    when either claim is missing or does not parse).
  - `enum SpaceUse { Take, Hold }`; `static class SpaceLocks` with `Task<bool> TakeAsync(AuthDbContext db, Guid userId, SessionSpace space,
    DateTimeOffset now, CancellationToken)` (the row `FOR NO KEY UPDATE`; `false` when it is gone or its `JoinedAt` in micros differs; else
    sets `LastUsedAt = now` and returns `true`) and `Task<bool> HoldAsync(AuthDbContext db, Guid userId, SessionSpace space, CancellationToken)`
    (the row `FOR KEY SHARE`, the same `false` cases, no write). The time comparison is done on whole microseconds of the value read from the
    database, never by a SQL equality on a parameter.
  - `AtomicSignInResult` gains two optional constructor parameters after the existing ones: `SpaceUse use = SpaceUse.Take` and
    `Func<Task<IResult>>? whenSpaceGone = null` (defaults to `whenGone`), and one more for the switch (Task 11):
    `SessionSpace? alsoHold = null` (a second membership held `FOR KEY SHARE` first; gone → `whenGone`).
  - `SessionPrincipal.Create` calls `SessionCompany.Apply` with its tenant.

**Behaviour of `AtomicSignInResult` after this task** (in its transaction, before `Results.SignIn`): read `SessionCompany.Read(principal)`
(missing: throw `InvalidOperationException` — every principal of this service carries it from now on); if `alsoHold` is set, hold it (gone →
`whenGone`, or throw when `whenGone` is null, as today for an account gone); then take or hold the session's space by `use` (gone →
`whenSpaceGone ?? whenGone`); **then** the account's `FOR KEY SHARE` as today. The `LastUsedAt` written is `StorableTime.Now(clock)` (resolve
`TimeProvider` from the request's services, as the result already resolves the context); it is saved by the same `SaveChanges` and commit as
the session, and a non-`200` sign-in rolls it back with everything else.

**`ShareLockInterceptor`:** `SignInRaceTests` and `ProviderExchangeRaceTests` delete the account from inside the interceptor just before the
command that contains `FOR KEY SHARE` (today the account re-read). With the membership locked first, that delete would wait for the
sign-in's membership lock while the sign-in waits for the delete: the test hangs. The interceptor fires instead before the first command
that contains `FOR NO KEY UPDATE` or `FOR KEY SHARE` (the membership lock of a login and of a provider exchange, the membership hold of a
refresh), and its comment says so. The delete then removes the membership by cascade before the lock is taken, the path finds its space gone,
and `whenSpaceGone` (= `whenGone`) gives the answer the tests already assert: their assertions do not change.

**`ProviderLinkEndpoint.LinkAndSignInAsync`:** after the address lock and the ticket checks, and **before** the `FOR SHARE` read of the hash,
read `ReadSignInSpaceAsync` and lock it with `SpaceLocks.TakeAsync` (gone: the `no_company` answer and audit row it gives today when the
re-read finds no membership); the re-read after the hash goes. `AtomicSignInResult` then takes the same row again (same transaction: no wait).

- [ ] **Step 1: Write the failing tests.** In `SessionCompanySignInTests`:
  - `A_login_stores_the_session_company_in_the_refresh_token_only`: after a login, `StoredTokens.RefreshClaimsAsync` holds
    `session_org_id` = the company id and `session_org_joined` = the membership's `JoinedAt` in micros; the decoded access token
    (`AccessTokens.Payload`) has neither and exactly the claims of today's contract (compare with the list `TenantClaimsTests` uses); the
    stored refresh token has none of `org_id`, `org_kind`, `roles`, `permissions`.
  - `Every_sign_in_path_writes_last_used_at_of_the_space_it_took` (theory over login, provider sign-in, provider link, sign-up completion,
    provider finish, invitation accepted through a provider — reuse each path's existing arrange helpers from its test class): after the `200`,
    `LastUsedAtAsync` of the taken membership equals the factory clock's now (micro-truncated); the other membership (where the path allows
    one) is untouched.
  - `A_refused_sign_in_leaves_last_used_at_unchanged`: a wrong password and an unverified address each leave `LastUsedAt` `null`. (A sign-in
    that fails after it took the lock is `SignInLockOrderTests.A_removal_of_the_last_membership_while_a_login_waits_commits_and_the_login_takes_nothing`.)
  - `A_login_after_a_switch_elsewhere_lands_in_the_company_used_last` (criterion 4, first half, by setting `LastUsedAt` with the helper — the
    switch itself is Task 11's): B used later than A → the login's `org_id` is B.
  - `Removed_from_the_used_company_a_login_takes_the_next_space` (criterion 4, second half): B used last, B removed → A.
  - `Two_companies_without_use_land_in_the_earlier_joined_and_a_company_beats_the_personal_space` (criterion 3).
  In `SignInLockOrderTests` (parallel requests against PostgreSQL):
  - `A_removal_committed_before_a_login_makes_it_fail_without_a_deadlock` (criterion 29): hold the login's chosen membership row
    (`MembershipRowAsync`); start the login; wait for 1 waiter; in the held transaction `LockAccountAsync` succeeds (the login holds no
    account lock while it waits), delete the membership and the account, release; the login answers the `401` of an unknown address (today's
    answer for an account deleted in that window) and no refresh token is stored for it.
  - `A_removal_of_the_last_membership_while_a_login_waits_commits_and_the_login_takes_nothing`: as above but the account keeps a second
    membership and only the chosen one is deleted: the login answers as for an account gone (the path's `whenGone`), writes no session and no
    `LastUsedAt`; a second login then lands in the other space.
  - `A_provider_link_waits_for_the_membership_before_it_locks_the_account`: the link of `ProviderLinkRaceTests`' arrangement with the chosen
    membership held: `LockAccountAsync` succeeds while the link waits; delete the membership, release → `403 no_membership`.
- [ ] **Step 2: See them fail:** no claims in the stored token; `LastUsedAt` stays `null`; `LockAccountAsync` times out (today the account is
  locked first) — that timeout is the failure the lock-order tests must show before the change.
- [ ] **Step 3: Implement** `SessionCompany`, `SpaceLocks`, the changes to `SessionPrincipal`, `AtomicSignInResult` and the link endpoint;
  update `AtomicSignInResult`'s class comment (the order of its locks and why).
- [ ] **Step 4: Run** the two new classes, `TenantClaimsTests`, `LoginTests`, `LoginCookieTests`, `ProviderLinkTests`, `ProviderLinkRaceTests`,
  `ProviderExchangeTests`, `ProviderFinishTests`, `ProviderFinishRaceTests`, `ProviderInvitationTests`, `SignupCompleteSignInTests`,
  `SignupCompleteRaceTests`, `PrivateAccountRaceTests`, `SignInRaceTests`, `ProviderExchangeRaceTests` (both unchanged; they hang until a
  timeout if the interceptor still fires at the account re-read), `RefreshTests` (refresh still uses `Take` and writes `LastUsedAt` until
  Task 4; no test of this task asserts otherwise): green.
- [ ] **Step 5: Hand back.** Scan the touched files. Proposed subject: `feat(sessions): a sign-in records its company in the session and locks its membership`.

### Task 4: The refresh keeps the session's company; tokens issued before this version

**Files:**
- Modify: `src/Auth.Server/Sessions/RefreshEndpoint.cs`
- Create (tests): `tests/Auth.IntegrationTests/SessionCompanyRefreshTests.cs`, `tests/Auth.IntegrationTests/Infrastructure/OldRefreshTokens.cs`

**Interfaces:**
- Consumes: `SessionCompany`, `SpaceUse.Hold` (Task 3); `ReadInCompanyAsync`, `ReadSignInSpaceAsync` (Task 2).
- Produces (for Task 11): the refresh's checks as one internal method, `RefreshEndpoint.ReadSessionAsync(HttpContext http, …the services
  the refresh uses…)` → `SessionCheck`, a record holding either `Failure` (`IResult`: the refresh's `InvalidGrant()`) or the authenticated
  `Principal`, the `UserId`, the next token's lifetime, and `Space` (`TenantContext` of the session's company); `RefreshEndpoint.InvalidGrant()`
  internal. The refresh itself is `ReadSessionAsync` plus building the identity and returning the `AtomicSignInResult`.

**Behaviour:** after the checks of today (account, stamp, start and cap), read `SessionCompany.Read(result.Principal)`: present →
`ReadInCompanyAsync(userId, CompanyId)`; `null` or `MicrosOf(JoinedAt) != JoinedMicros` → `InvalidGrant`; absent (a token of the code before
this version) → `ReadSignInSpaceAsync`, `null` → `InvalidGrant`. Build the identity as today, then `SessionCompany.Apply` with the space read
(overwriting the claims copied from the old principal), and return `AtomicSignInResult(..., whenGone: InvalidGrant, use: SpaceUse.Hold)`.

**Minting an old token (criterion 10):** `OldRefreshTokens` registers, through `AuthAppFactory.WithServices`, an OpenIddict server handler on
`ProcessSignInContext` ordered just before the token generation that removes the two session claims from `context.Principal` while a static
`AsyncLocal<bool>` (or a singleton switch) is on; the test turns it on for the one login, off afterwards. Name the OpenIddict handler it runs
before in a comment, after checking it in OpenIddict 7.7.1 (the claims must be gone from the stored payload: the test asserts it).

- [ ] **Step 1: Write the failing tests** in `SessionCompanyRefreshTests`:
  - `A_refresh_keeps_the_session_company_when_another_membership_was_used_later` (criterion 6): login lands in A; set B's `LastUsedAt` to
    now + 1 h; refresh → `org_id` A.
  - `A_refresh_does_not_write_last_used_at`: login, note A's `LastUsedAt`, advance the clock 5 minutes, refresh → unchanged.
  - `A_refresh_after_the_session_company_was_removed_is_invalid_grant_and_a_session_in_the_other_company_refreshes` (criterion 8): sessions
    in A and in B (B by `LastUsedAt` before the second login); remove A → A's refresh `401 invalid_grant` (`SessionApi.AssertInvalidGrantAsync`),
    B's refresh `200` with `org_id` B.
  - `A_member_removed_and_added_again_does_not_get_the_old_session_back` (criterion 9): session in A; remove A's membership and add it again
    with a later `JoinedAt`; refresh → `401 invalid_grant`; a new login → `200` in A.
  - `A_token_without_the_session_company_refreshes_into_the_sign_in_space_and_the_next_token_carries_it` (criterion 10): a user in A and B;
    the old-token login lands in A; assert the stored token lacks both claims; then set B's `LastUsedAt` to now + 1 h (the sign-in order now
    says B); refresh → `org_id` B, the new stored token carries B and B's `JoinedAt`; a second refresh → B.
  - `A_token_without_the_session_company_of_an_account_with_no_membership_is_invalid_grant`.
  - `The_next_refresh_token_carries_the_company_and_join_time` (criterion 11, refresh side): after a refresh the stored token holds both claims
    and no tenancy claim; the access token holds neither.
  - `A_membership_written_with_a_time_finer_than_a_microsecond_still_refreshes` (Review Focus 4): add a membership with a `JoinedAt` of
    `…:00.1234567` (100 ns ticks), log in there, refresh → `200`.
  - `A_refresh_waits_for_a_removal_holding_its_membership_and_then_is_invalid_grant`: hold the session's membership row; refresh waits
    (wait for 1 waiter); delete it, release → `401 invalid_grant`; the refresh token is not redeemed (`StoredTokens.StatusAsync` is
    `valid`).
- [ ] **Step 2: See them fail** (today the refresh re-picks the space).
- [ ] **Step 3: Implement** the method split and the behaviour.
- [ ] **Step 4: Run** the new class, `RefreshTests`, `RefreshReuseTests`, `RefreshOutageTests`, `SessionLifetimeTests`, `SignInRaceTests`, `TenantClaimsTests`
  (`Refresh_of_someone_who_is_no_longer_a_member_is_the_401_of_spec_0002` and the role-change tests stay green unchanged),
  `SessionCompanySignInTests`: green.
- [ ] **Step 5: Hand back.** Scan the touched files. Proposed subject: `feat(sessions): a refresh keeps the session's company`.

### Task 5: `refresh.reuse_detected` names the reused token's company

**Files:**
- Modify: `src/Auth.Server/Sessions/RefreshReuseAuditHandler.cs`
- Modify (tests): `tests/Auth.IntegrationTests/AuditRefreshReuseTests.cs`

**Interfaces:** Consumes `SessionCompany.Read`, `ReadSignInSpaceAsync`, the old-token helper (Task 4).

**Behaviour:** the company is `SessionCompany.Read(context.Principal)`'s `CompanyId` when the principal is set and carries it (its name read
from `Companies` now; `null` name when the company is gone); otherwise, as for a token of the code before this version, the sign-in's space
of the subject. A probe test first proves that `context.Principal` is set at this handler's order (`ValidateTokenEntry.Order - 1`); **if it is
not**, keep the order (it must stay before `ValidateTokenEntry`) and stop: report it in the Hand back with what the context holds, do not
invent another source.

- [ ] **Step 1: Write the failing tests** in `AuditRefreshReuseTests`:
  - `The_principal_of_the_reused_token_is_known_where_the_reuse_is_recorded` (probe): a reuse past the leeway; a test-only handler at the same
    order (registered by `WithServices`) records whether `context.Principal` carries `session_org_id` — `true`.
  - `A_reuse_names_the_session_company_of_the_reused_token`: a session in A; B used later; reuse A's cookie past the leeway →
    `refresh.reuse_detected` has `org_id` A and A's name.
  - `A_reuse_of_a_token_without_the_session_company_names_the_sign_in_space`: an old token (Task 4's helper) → the sign-in's space.
  - The existing tests of the class stay green unchanged.
- [ ] **Step 2: See the second fail** (today: the sign-in pick, B).
- [ ] **Step 3: Implement.** Update the class comment.
- [ ] **Step 4: Run** `AuditRefreshReuseTests`, `RefreshReuseTests`: green.
- [ ] **Step 5: Hand back.** Scan the touched files. Proposed subject: `feat(audit): a refresh reuse names the session's company`.

### Task 6: The company API and `GET /auth/me` act on the token's company

**Files:**
- Modify: `src/Auth.Server/Tenancy/CompanyAccess.cs`, `src/Auth.Server/Tenancy/CompanyGuard.cs`, `src/Auth.Server/Api/TenancyEndpoints.cs` (OpenAPI descriptions of `GET /auth/me` and the `/auth/org` group: "the token's company")
- Create (tests): `tests/Auth.IntegrationTests/TokenCompanyTests.cs`

**Interfaces:** Consumes `ReadInCompanyAsync` (Task 2), `AddMembershipAsync` (Task 1). Produces: `CompanyAccess` and `CompanyGuard` keep
their signatures.

**Behaviour:**
- `CompanyAccess.AuthorizeCoreAsync`: after the `sub`, read the token's `org_id` claim; missing or not a Guid → no tenant; else
  `ReadInCompanyAsync(userId, orgId)`. The rest stays as it is (personal → `forbidden` where not allowed; permission held → proceed; else
  `permissions_changed` when the token claims it — needing none, when it carries an `org_id` — else `forbidden`). `GET /auth/me` already goes
  through `AuthorizeAnyKindAsync`, so it follows.
- `CompanyGuard.EnterAsync`: `ReadInCompanyAsync(actor.Member.UserId, companyId)`; `null` or without the permission → `permissions_changed`
  (the `CompanyId != companyId` comparison goes: the read is in that company).
- Every endpoint under `/auth/org` already passes the caller's `CompanyId` to its service; check each (`grep -n "caller.CompanyId\|Caller"`
  in `Api/Org*.cs`) and that none re-reads "the user's company" another way.

- [ ] **Step 1: Write the failing tests** in `TokenCompanyTests` (a user admin in A and admin in B; tokens of A and of B obtained by setting
  `LastUsedAt` before each login):
  - `Me_answers_for_the_token_company` (criterion 23): `/auth/me` with A's token names A, with B's token B.
  - `Each_token_reads_and_changes_only_its_own_company` (criterion 23; a theory or one test per area): with A's token, `GET /auth/org` is A,
    `PATCH /auth/org` renames A only, `GET /auth/org/members` lists A's members, an invitation goes to A, a role created lands in A, a member
    removed is A's; B is unchanged; and the same with B's token for B.
  - `A_token_company_left_is_permissions_changed_on_me_and_the_company_api_even_with_another_membership` (criterion 24): remove the user from
    A; A's token → `403 permissions_changed` on `/auth/me`, `GET /auth/org`, `GET /auth/org/members`; B's token still `200`.
  - `A_company_token_whose_permission_was_lost_in_that_company_is_permissions_changed_while_the_other_company_grants_it`: demote in A only
    (`SetMemberRoleAsync(user, A, "user")`), B still admin; A's token → `403 permissions_changed` on `GET /auth/org/members`.
  - `The_guard_reads_the_actor_in_the_company_it_locks`: with A's token, a member removal in A while the user is still admin of B but was
    demoted in A between the endpoint's check and the lock (hold `HeldLock.CompanyAsync(A)`, demote, release) → `403 permissions_changed`.
- [ ] **Step 2: See them fail** (today every token acts on the earliest company).
- [ ] **Step 3: Implement.** Update both class comments ("the token's company").
- [ ] **Step 4: Run** `TokenCompanyTests`, `OrgEndpointTests`, `OrgMemberTests`, `MeProvidersTests`, `AuditCompanyFlowsTests`,
  `AuditMemberFlowsTests`, `AuditRoleFlowsTests`, `AuditInvitationFlowsTests`, `CompanyDeletionTests`, `OpenApiTests`, `OpenApiHardeningTests`:
  green.
- [ ] **Step 5: Hand back.** Scan the touched files. Proposed subject: `feat(tenancy): the company API acts on the token's company`.

### Task 7: `DELETE /auth/account` judged on the token's company

**Files:**
- Modify: `src/Auth.Server/Api/AccountDeletionEndpoint.cs`, `src/Auth.Server/Api/TenancyEndpoints.cs` (the description of `DELETE /auth/account`)
- Modify (tests): `tests/Auth.IntegrationTests/AccountDeletionEndpointTests.cs`

**Interfaces:** Consumes `ReadInCompanyAsync`, `CountAsync` (Task 2); `AccountDeletion.DeleteAsync` with `lostSpaceId` (Task 8 adds it — in
this task pass nothing; Task 8 passes the caller's space).

**Behaviour:** the first read becomes `ReadInCompanyAsync(userId, <token org_id>)` (missing or unparseable `org_id`: no tenant). `Refusal` keeps
its cases and gains one: the tenant is the personal space but `CountAsync(userId) > 1` → `forbidden`. Under the address lock the re-read is the
same `ReadInCompanyAsync` in the token's company, followed by the same count: not personal any more → `permissions_changed` (today's line);
personal but another membership now → `forbidden`.

- [ ] **Step 1: Write the failing tests** in `AccountDeletionEndpointTests`:
  - `A_personal_token_of_an_account_that_is_also_a_company_member_is_forbidden` (criterion 25): private account, login (personal token), then
    `AddMembershipAsync` into a company → `DELETE /auth/account` with the right password → `403 forbidden`; the account and both memberships
    remain; an `account.delete_refused` row only if today's refusals write one for `forbidden` (check and assert what the code does today for
    a company token: the same).
  - `A_company_member_that_gains_a_personal_space_meanwhile_is_forbidden_under_the_lock`: the address lock held (`HeldLock.AddressAsync`), the
    request waits, a company membership is added, release → `403 forbidden`.
  - `A_token_of_a_space_the_account_left_is_permissions_changed_even_with_another_membership`: personal token; the personal membership deleted
    and a company membership added → `403 permissions_changed` (spec 0010's line, judged on the token's company).
  - The existing tests named by the spec stay green **unchanged**: `A_token_that_says_personal_after_the_account_moved_to_a_company_is_permissions_changed`,
    `An_account_with_a_personal_space_and_a_company_is_a_company_member_at_sign_in_and_cannot_delete_itself`,
    `A_token_that_says_personal_after_the_account_is_gone_is_permissions_changed`,
    `A_deletion_that_finds_the_space_no_longer_personal_under_the_lock_is_permissions_changed`.
- [ ] **Step 2: See the first two fail** (today: `permissions_changed` — the pick takes the company).
- [ ] **Step 3: Implement.** Update the endpoint's comments and the OpenAPI description ("judged on the token's company").
- [ ] **Step 4: Run** `AccountDeletionEndpointTests`, `AccountDeletionProviderTests`, `PrivateAccountRaceTests`, `OpenApiTests`: green.
- [ ] **Step 5: Hand back.** Scan the touched files. Proposed subject: `feat(account): self-deletion is judged on the token's company`.

### Task 8: Removals and company deletions keep accounts with another space

**Files:**
- Modify: `src/Auth.Server/Tenancy/MemberService.cs`, `src/Auth.Server/Tenancy/CompanyDeletionService.cs`, `src/Auth.Server/Tenancy/AccountDeletion.cs`, `src/Auth.Server/Tenancy/OperatorCommands.cs`, `src/Auth.Server/Admin/AdminCli.cs`, `src/Auth.Server/Api/OrgMemberEndpoints.cs`, `src/Auth.Server/Api/AccountDeletionEndpoint.cs` (pass the lost space), `README.md:147`
- Modify (tests): `tests/Auth.IntegrationTests/AccountDeletionOnRemovalTests.cs`, `CompanyDeletionTests.cs`, `AccountDeletionTests.cs`, `AdminCliTests.cs`, `OrgMemberTests.cs` (only where the return type changes a line)

**Interfaces:**
- Produces:
  - `sealed record MemberRemoval(bool AccountDeleted)`; `MemberService.RemoveAsync` returns `Task<Outcome<MemberRemoval>>`;
    `OperatorCommands.RemoveMemberAsync` returns `Task<Outcome<MemberRemoval>>`.
  - `AccountDeletion.DeleteAsync(Guid userId, AccountDeletionReason reason, Actor? actor, Guid? lostSpaceId, CancellationToken)` and the same
    for a collection; the existing overloads stay and forward `lostSpaceId: null` (callers that do not change: `AccountsWithoutSpaceCleanup`,
    the tests).

**Behaviour:**
- **`RemoveAsync`:** the checks stay in their order. Then stage `member.removed` (read the email and the company name first, as today), delete
  the membership row (`ExecuteDeleteAsync` on `(companyId, userId)` in the open transaction), count the user's remaining memberships with a
  plain read (**no account lock**: spec 0017), and only when none is left call `DeleteAsync(userId, MemberRemoved, current, companyId, ct)`.
  Save, commit, return `MemberRemoval(deleted)`. The class comment of `RemoveAsync` says the new rule.
- **`CompanyDeletionService.DeleteAsync`:** after the invitations, delete **every** membership of the company (`ExecuteDeleteAsync`), then
  find the former members with no membership left (one query), hand those to `DeleteAsync(list, OrgDeleted, current, companyId, ct)` (none:
  no call), then delete the roles and the company as today. `org.deleted`'s `details.members` keeps counting every former member.
- **`AccountDeletion`:** before the accounts' `FOR UPDATE` (and after the mail requests and pending sign-ups, whose order spec 0017 keeps),
  lock the memberships of the accounts `FOR UPDATE` ordered by user id and company id (the lock order of this plan). With `lostSpaceId` set,
  every `account.deleted` names that company's id and name (read before anything is deleted); without it, the space the sign-in would take
  (the order of Task 2, in SQL) — the spaces query at :95-102 takes that order. The personal companies are deleted as today.
- **`AccountDeletionEndpoint`:** both calls pass the caller's space (`caller.CompanyId`) as `lostSpaceId`.
- **`OrgMemberEndpoints`:** `204` on success as today (the value is not shown).
- **`AdminCli`:** `remove-member` prints `Member removed; the account is deleted.` or `Member removed; the account is kept.` by the value.
- **`README.md:147`:** the `remove-member` line says the account is deleted only when that was its last space, else kept.

- [ ] **Step 1: Write the failing tests:**
  - `AccountDeletionOnRemovalTests.A_member_with_another_membership_keeps_the_account_and_the_other_session` (criterion 26): sessions in A and
    B; A removes the user → `204`; the account exists with B's membership only; `member.removed` written, no `account.deleted`; B's session
    refreshes `200`; A's session `401 invalid_grant`.
  - `AccountDeletionOnRemovalTests.A_member_without_another_membership_is_deleted_as_before` (criterion 27): `account.deleted` reason
    `member_removed`, `org_id` the company removed from.
  - `CompanyDeletionTests.Members_with_another_membership_keep_their_accounts_and_the_others_are_deleted` (criterion 28): three members of A —
    one also in B, one only in A, the deleting admin also in B; `DELETE /auth/org` → `204`; the first and the admin keep their accounts (B's
    membership, B's sessions refresh); the second is deleted with `org_deleted`; each `account.deleted` names A.
  - `CompanyDeletionTests.An_admin_with_no_other_membership_is_deleted_with_the_company`: as today.
  - `CompanyDeletionTests.A_kept_members_role_does_not_stop_the_company_deletion`: the restricting foreign key does not fire (a regression test
    for the order: memberships before roles).
  - `AccountDeletionTests.An_account_deleted_by_a_removal_names_the_space_it_lost`: an account in A and B with A's `LastUsedAt` set (the
    sign-in would take A); remove A (the account is kept), then remove B → the `account.deleted` row has `org_id` B and B's name. A second
    case calls `DeleteAsync` with `lostSpaceId` B on an account still in A and B → `org_id` B, not the sign-in's A.
  - `AccountDeletionTests.An_account_with_a_personal_space_and_a_company_is_recorded_with_the_company` stays green unchanged (no `lostSpaceId`:
    the sign-in order, company first when unused).
  - `AccountDeletionTests.The_deletion_locks_the_memberships_before_the_account`: hold one of the account's membership rows; start a
    self-deletion of a private account through `DELETE /auth/account`; wait for 1 waiter; `LockAccountAsync` from another `HeldLock`
    succeeds (before the change the deletion holds the account row `FOR UPDATE` while its cascade waits for the membership, so this lock
    times out and the test fails); release both → `204`.
  - `AdminCliTests.Remove_member_says_whether_the_account_was_kept_or_deleted`: both lines (the existing test of the deleted line stays).
- [ ] **Step 2: See them fail** (today the account is always deleted; the CLI has one line).
- [ ] **Step 3: Implement.** Fix the compile breaks in `OrgMemberTests.cs:533`, `:537` (the result is now `Outcome<MemberRemoval>`; assert as
  before on the error).
- [ ] **Step 4: Run** the five classes above, `OrgMemberTests`, `AuditMemberFlowsTests`, `AuditCompanyFlowsTests`, `AuditAccountFlowsTests`,
  `PersonalSpaceCliTests`, `PrivateAccountRaceTests`, `AccountDeletionEndpointTests`: green.
- [ ] **Step 5: Hand back.** Scan the touched files. Proposed subject: `feat(tenancy): a removal deletes the account only with its last space`.

### Task 9: `GET /auth/orgs`

**Files:**
- Create: `src/Auth.Server/Api/OrgListEndpoint.cs`
- Modify: `src/Auth.Server/Tenancy/Contracts.cs` (the response records), `src/Auth.Server/Api/TenancyEndpoints.cs` (mapping and OpenAPI)
- Create (tests): `tests/Auth.IntegrationTests/OrgListTests.cs`
- Modify (tests): `OpenApiHardeningTests.cs` (36 → 37), `RatePolicyTests.cs` (a row: `GET /auth/orgs` → `general`), `RateLimitMiddlewareTests.cs` (a row)

**Interfaces:** Produces `record OrgListResponse(IReadOnlyList<OrgListItem> Orgs)` and `record OrgListItem(Guid Id, string Name, string Kind,
RoleRef Role, bool Current)` (snake_case as every contract; `Kind` from `SpaceKinds`' names as `/auth/me`'s `org_kind`).

**Behaviour:** the bearer-token authorization of `/auth/me` (not `CompanyAccess`: the list answers when the token's company is gone);
`sub` not a Guid → `403 forbidden` (as `CompanyAccess`); `ListAsync` empty (account gone or no membership) → `403 permissions_changed`; else
the entries ordered by `Name` with `StringComparer.OrdinalIgnoreCase`, then by the id's lowercase text with `StringComparer.Ordinal`;
`Current` = the entry's id equals the token's `org_id`. Counted against `general` (a GET: no rate-policy change; the test pins it).

- [ ] **Step 1: Write the failing tests** in `OrgListTests`:
  - `The_list_holds_every_membership_with_its_role_and_the_current_flag` (criterion 12): a user admin in `Acme`, user in `beta`, with a personal
    space → three entries with id, name, kind, `role {id, name}`; the personal space `name` `private`, `kind` `personal`, role `private`;
    exactly one `current`.
  - `The_order_is_by_name_ignoring_case_then_by_id` (criterion 12): `Acme`, `beta`, `Zeta` → that order.
  - `Names_that_differ_only_in_case_are_ordered_by_id` and `Names_are_ordered_ordinally_not_by_culture` (Review Focus 3; build `Ä` with
    `(char)0xC4`).
  - `Current_follows_the_token_company` (criterion 13): A's token marks A, B's token marks B.
  - `After_a_removal_from_the_token_company_no_entry_is_current` (criterion 13): remove from A; A's token → `200`, B listed, none current.
  - `Without_a_token_it_is_401` and `For_a_deleted_account_it_is_403_permissions_changed` (criterion 14).
  - `It_counts_against_general` (criterion 14): a `RatePolicyTests` row (`GET /auth/orgs` → `General`) and an `InlineData` row of
    `RateLimitMiddlewareTests.Each_policy_refuses_at_its_own_number` for `GET /auth/orgs`.
  - `The_answer_is_never_stored`: `Cache-Control: no-store`, `Pragma: no-cache`.
  - `A_post_to_the_list_is_405_and_a_path_under_it_is_404` (spec 0005's fallback for the new path).
  - The JSON is an object with one member `orgs`, an array also with one entry (assert the raw JSON).
- [ ] **Step 2: See them fail** (the path is `404`).
- [ ] **Step 3: Implement**, with an OpenAPI summary, its `401`, `403 permissions_changed`/`forbidden`.
- [ ] **Step 4: Run** `OrgListTests`, `OpenApiTests`, `OpenApiHardeningTests`, `RatePolicyTests`, `RateLimitMiddlewareTests`, `SecurityHeadersTests`: green.
- [ ] **Step 5: Hand back.** Scan the touched files. Proposed subject: `feat(tenancy): GET /auth/orgs lists the person's companies`.

### Task 10: `POST /auth/switch`, part 1 — the path, its body checks, its answers, its limit

**Files:**
- Create: `src/Auth.Server/Sessions/SessionPaths.cs`, `src/Auth.Server/Sessions/SwitchRequestHandler.cs`, `src/Auth.Server/Sessions/SwitchEndpoint.cs` (this task: a refresh in the session's company, ignoring `org_id`)
- Modify: `src/Auth.Server/Tokens/OpenIddictSetup.cs`, `src/Auth.Server/Sessions/SessionResponseHandler.cs`, `src/Auth.Server/Api/ErrorHandling.cs`, `src/Auth.Server/RateLimiting/RatePolicy.cs`, `src/Auth.Server/Api/AccountEndpoints.cs`
- Create (tests): `tests/Auth.IntegrationTests/SwitchRequestTests.cs`
- Modify (tests): `RatePolicyTests.cs`, `RateLimitMiddlewareTests.cs`, `JsonCharsetTests.cs`, `Infrastructure/SessionApi.cs` (`Switch`, `SwitchTo`, `SwitchPath`: see "Shared test helpers")

**Interfaces:**
- Consumes: `RefreshEndpoint.ReadSessionAsync`, `InvalidGrant` (Task 4).
- Produces: `SessionPaths.Switch = "/auth/switch"`, `SessionPaths.IsSwitchPath(PathString)`, `SessionPaths.IsSessionPath(PathString)` (refresh
  or switch: the predicate of `SessionResponseHandler` and `ErrorHandling`; **not** of `JsonCharsetGuard`); `SwitchRequestHandler` (an
  `ExtractTokenRequestContext` handler at `RefreshRequestHandler`'s order + 25, i.e. +275); the request parameter name
  `SwitchRequestHandler.OrgIdParameter = "org_id"`; `SwitchEndpoint.SwitchAsync(HttpContext, …)` mapped as `POST /auth/switch`.

**Behaviour of `SwitchRequestHandler`** (only on the switch path; the pattern of `JsonLoginRequestHandler` for the body, of
`RefreshRequestHandler` for the cookie): not `POST` → reject `invalid_request`; not `JsonObjectBody.IsJson` → `invalid_request`; over
`JsonObjectBody.MaxBytes` → `invalid_request`; read bounded; not a JSON object, `org_id` missing or not a string, or not `IdInput.TryParse` →
`invalid_request` (a fixed description, as login's); **then** no `auth_rt` cookie → reject `invalid_grant`; else
`context.Request = new OpenIddictRequest { GrantType = refresh_token, RefreshToken = <cookie> }` with the `org_id` parameter set to the text as
sent (the endpoint parses it again). Every reject happens before OpenIddict reads the token, so nothing is redeemed.
Register the path in `SetTokenEndpointUris`, the handler beside the others. `SessionResponseHandler` and `ErrorHandling` use
`IsSessionPath`; `JsonCharsetGuard` is **not** changed (the switch reads a body, so it stays under the guard). `RatePolicies.Classify` maps
the switch path to `Refresh`.

- [ ] **Step 1: Write the failing tests** in `SwitchRequestTests` (a valid session's cookie; after each refusal the cookie still refreshes —
  `SessionApi.RefreshOk` — and its stored token is `valid`; a `400` is asserted on its status, its `error` member and the absence of
  `Set-Cookie`, as `RefreshTests.Get_returns_400_invalid_request` does, never on the whole body, which may carry an `error_description`):
  - `A_malformed_body_or_org_id_is_400_invalid_request_and_the_cookie_still_refreshes` (criterion 18; theory): not JSON, an array, `{}`,
    `{"org_id":1}`, `{"org_id":null}`, `{"org_id":"x"}`, a body over 8 KiB.
  - `A_body_of_another_media_type_is_400_and_a_form_post_too` (criterion 18): `text/plain`, `application/x-www-form-urlencoded`
    (`org_id=<uuid>`), no content type.
  - `Json_with_another_charset_is_415_and_the_cookie_still_refreshes` (criterion 18).
  - `An_org_id_with_braces_without_hyphens_or_with_spaces_is_400` (Review Focus 2; theory): `{<uuid>}`, `(<uuid>)`, the 32 hex digits
    without hyphens (the `N` form), the UUID with a leading or a trailing space, and an empty string (`IdInput` accepts only the 36-character
    `D` form).
  - `A_get_is_400_invalid_request` (as `RefreshTests.Get_returns_400_invalid_request`).
  - `Without_a_cookie_it_is_the_uniform_401` (criterion 19).
  - `An_expired_revoked_or_reused_cookie_a_changed_stamp_a_passed_cap_or_a_removed_session_company_is_the_uniform_401` (criterion 19; reuse
    the arrangements of `RefreshTests`, `SessionLifetimeTests` and Task 4's removal): each `401 {"error":"invalid_grant"}` with the compact
    body of a refresh's `401`.
  - `A_failing_database_is_503_with_no_body_and_no_cookie_and_the_cookie_still_refreshes_afterwards` (criterion 18; the arrangement of
    `RefreshOutageTests`): `503`, `Retry-After` as the refresh's, no `Set-Cookie`.
  - `A_valid_switch_answers_as_a_refresh` (this task's stub): `200` with exactly the body members and cookie attributes of `/auth/refresh`.
  - `RatePolicyTests`: an `InlineData` row of `A_request_counts_against_one_policy_by_its_method_and_path`: `POST /auth/switch` → `Refresh`.
  - `RateLimitMiddlewareTests.A_switch_and_refreshes_share_the_refresh_budget` (criterion 22): with the `refresh` limit that
    `Each_policy_refuses_at_its_own_number` uses (3), two refreshes and one switch from one address pass the limiter, and a fourth request
    on either path is `429`.
  - `Answers_are_never_stored`: `no-store`, `no-cache` on `200`, `400`, `401`.
- [ ] **Step 2: See them fail** (the path is `404`).
- [ ] **Step 3: Implement.** The stub `SwitchEndpoint` is the refresh: `ReadSessionAsync`, then sign in to the session's company (`Hold`).
- [ ] **Step 4: Run** `SwitchRequestTests`, `RefreshTests`, `RefreshOutageTests`, `SessionResponseHandlerTests`, `RatePolicyTests`,
  `RateLimitMiddlewareTests`, `JsonCharsetTests` (with `/auth/switch` added to the theory of endpoints that read a body), `LoginRequestTests`, `SignupCompleteTests`,
  `ProviderExchangeTests` (the other token paths): green.
- [ ] **Step 5: Hand back.** Scan the touched files. Proposed subject: `feat(sessions): POST /auth/switch takes the refresh cookie and a JSON body`.

### Task 11: `POST /auth/switch`, part 2 — the target, `404`, `LastUsedAt`, `org.switched`, OpenAPI

**Files:**
- Modify: `src/Auth.Server/Sessions/SwitchEndpoint.cs`, `src/Auth.Server/Audit/AuditKinds.cs`, `src/Auth.Server/Api/AccountEndpoints.cs` (OpenAPI of the switch)
- Create (tests): `tests/Auth.IntegrationTests/SwitchTests.cs`, `tests/Auth.IntegrationTests/SwitchRaceTests.cs`
- Modify (tests): `AuditProviderFlowsTests.cs` (28 → 29, the test renamed `The_audit_log_knows_twenty_nine_kinds`), `AuditTableTests.cs` (the ordered list ends with `org.switched`; renamed `The_kinds_are_the_twenty_nine_of_the_contract`), `OpenApiHardeningTests.cs` (37 → 38, 24 → 25, the `503` check allows exactly `/auth/refresh` and `/auth/switch`)

**Interfaces:** Produces `AuditKinds.OrgSwitched = "org.switched"`, last in `AuditKinds.All`.

**Behaviour of `SwitchAsync`:** `ReadSessionAsync` (failure → its `IResult`); parse the `org_id` parameter with `IdInput.TryParse` (it was
checked by the handler; a failure here is `400 invalid_request` through `ApiResults`); `ReadInCompanyAsync(userId, target)`; `null` →
`ApiResults.Error(TenancyErrors.NotFound)` returned **before** any sign-in (so the token stays unredeemed). Build the identity as the refresh
does with the target as tenant; when the target differs from the session's company, an `org.switched` entry (actor and subject the account
as `login.succeeded` sets them, `subject_email`, `org_id`/`org_name` the target, `details.from_org_id` the session's company id as text);
return `AtomicSignInResult(principal, entry, whenGone: InvalidGrant, use: SpaceUse.Take, whenSpaceGone: NotFound,
alsoHold: <the session's space when it differs from the target>)`. When the target is the session's company: no entry, no `alsoHold`, and a
vanished membership is `whenGone` (`401`, the session's company is gone) — `whenSpaceGone` is `InvalidGrant` then.
OpenAPI: request body `{"org_id"}`, `200` as refresh's, `400`, `401`, `404 not_found`, `415`, `503`, the cookie it reads and sets (the
metadata of `/auth/refresh`).

- [ ] **Step 1: Write the failing tests** in `SwitchTests` (a user admin in A, plain member in B, with tokens from a login in A):
  - `A_switch_to_another_company_answers_as_a_refresh_for_that_company` (criterion 15): `200`; body and cookie as a refresh's; the access
    token's `org_id`, `org_kind`, `roles`, `permissions` are B's; B's `LastUsedAt` is the clock's now; `org.switched` with
    `actor_user_id` = `subject_user_id` = the user, `subject_email`, `org_id` and `org_name` B, `details.from_org_id` A, a client address.
  - `The_next_refresh_stays_in_the_company_switched_to_and_a_new_login_lands_there` (criteria 4, 6): refresh with the new cookie → B; a login
    on a second client → B.
  - `A_switch_on_one_device_leaves_the_other_device_in_its_company` (criterion 7): two logins in A; device 1 switches to B; device 2's refresh
    → A.
  - `The_old_cookie_after_a_switch_is_a_consumed_refresh_token` (criterion 16): within the leeway the old cookie refreshes `200`; past it
    (advance the clock) `401 invalid_grant`, the family revoked (the new cookie also `401`), `refresh.reuse_detected` written.
  - `The_old_cookie_within_the_grace_window_gets_a_pair_for_the_old_company` (Review Focus 1): the leeway refresh's `org_id` is A.
  - `A_company_of_others_an_unknown_id_and_another_accounts_personal_space_are_the_same_404` (criterion 17): status, headers (minus `Date`),
    body byte for byte equal across the three; `{"error":"not_found"}`; the cookie refreshes afterwards (in A); the stored token is `valid`
    (not redeemed — this is the probe the spec asks for).
  - `A_switch_to_the_session_company_is_a_refresh_that_writes_last_used_at_and_no_row` (criterion 20).
  - `A_switch_to_the_personal_space_gives_its_claims_and_the_company_api_refuses` (criterion 21): `org_kind` `personal`, `roles`
    `["private"]`, the manifest's private permissions; then `GET /auth/org` with that token → `403 forbidden`.
  - `An_org_id_in_upper_case_switches` (Review Focus 2).
  - `A_token_without_the_session_company_switches_and_names_the_sign_in_space_as_from` (spec: old tokens; Task 4's helper).
  - `A_switch_with_a_changed_role_reads_the_role_now`: the role in B changed before the switch → the new role's permissions.
  In `SwitchRaceTests` (criterion 29, parallel requests against PostgreSQL):
  - `A_removal_committed_before_a_switch_makes_it_404_and_the_cookie_still_refreshes`: hold B's membership row; switch waits; the held
    transaction can `LockAccountAsync`; delete B's row, release → `404`; the token is `valid`; the cookie refreshes in A.
  - `A_removal_of_the_session_company_committed_before_the_switch_makes_it_401`: hold A's row (the session's company); switch to B waits;
    delete A's row, release → `401 invalid_grant`; B's `LastUsedAt` unchanged.
  - `A_removal_committed_after_a_switch_is_caught_at_the_next_refresh`: hold the audit table (`HeldLock.AuditTableAsync`) so the switch holds
    B's row and waits at its `org.switched` insert; start `DELETE /auth/org/members/{user}` in B with an admin of B (it waits for B's row);
    wait for 2 waiters; release → switch `200` in B, removal `204`; the switch's cookie then refreshes `401 invalid_grant`.
  - `A_removal_committed_after_a_login_is_caught_at_the_next_refresh`: the same with a login that takes B.
- [ ] **Step 2: See them fail** (the stub ignores `org_id`; 28 kinds).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `SwitchTests`, `SwitchRaceTests`, `SwitchRequestTests`, `RefreshTests`, `RefreshReuseTests`, `AuditRefreshReuseTests`,
  `AuditProviderFlowsTests`, `AuditTableTests`, `OpenApiTests`, `OpenApiHardeningTests`: green.
- [ ] **Step 5: Hand back.** Scan the touched files. Proposed subject: `feat(sessions): POST /auth/switch moves the session to another company`.

### Task 12: The development seed's second company

**Files:**
- Modify: `src/Auth.Server/Seeding/DevUserSeeder.cs`, `src/Auth.Server/appsettings.Development.json`, `deploy/docker-compose.yml`, `.env.example`, `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`
- Modify (tests): `tests/Auth.IntegrationTests/DevCompanySeedTests.cs`

**Interfaces:** Produces `DevUserSeeder.SecondOrgNameKey = "Auth:DevSeed:SecondOrgName"`.

**Behaviour:** after today's steps, when `SecondOrgName` is set (not blank) and differs from the first company's name (ordinal, after the
same trim the first name gets): `EnsureCompanyAsync(second)` (found by name or created with the manifest's default roles through
`CompanyService.CreateAsync(..., via: "seed")`, as the first); the first seed user becomes a member of it with the **plain role**: the first of
the manifest's default roles, **in the manifest's order**, whose expanded permissions hold neither `*` nor `members:manage`, found in the
company by its normalized name; when none qualifies, a new role `member` with no permission. `EnsureMemberAsync` checks the membership in
**the company it seeds** (today: any membership), so a restart changes nothing and a seed user removed from one company is added back to it
at the next start. Config: `appsettings.Development.json` gets `"DevSeed": { "SecondOrgName": "Beta" }` merged into its existing `Auth`
section; `deploy/docker-compose.yml` gets `Auth__DevSeed__SecondOrgName: ${AUTH_DEV_SEED_SECOND_ORG_NAME:-Beta}` beside the other seed lines,
with a one-line comment; `.env.example` gets `AUTH_DEV_SEED_SECOND_ORG_NAME=Beta` with a comment ("a second company the first seed user is
a plain member of; blank for none"). `AuthAppFactory` blanks the key by default (as it blanks `UnverifiedEmailKey`), so no existing test sees a
second company.

- [ ] **Step 1: Write the failing tests** in `DevCompanySeedTests`:
  - `With_a_second_name_the_first_seed_user_is_admin_of_the_first_and_a_plain_member_of_the_second` (criterion 30): with the notes-like
    manifest of the existing tests, the second company exists with every default role, the seed user's role there is `user`; the first
    company as today.
  - `Without_the_setting_it_seeds_as_today` (criterion 30): one company, one membership.
  - `A_manifest_without_a_plain_default_role_gives_a_new_empty_member_role`.
  - `The_same_name_twice_makes_one_company`.
  - `A_restart_changes_nothing` (the existing test, with the setting on too).
  - `A_restart_puts_a_removed_seed_user_back_in_the_second_company` (Review Focus 5).
  - `The_unverified_seed_user_is_not_put_in_the_second_company`.
- [ ] **Step 2: See them fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `DevCompanySeedTests`, `DevSeedTests`, `ShippedConfigurationTests` (if it reads the development settings; check), the
  `docker compose -f deploy/docker-compose.yml config` of the file **without** bringing anything up (it must parse): green.
- [ ] **Step 5: Hand back.** Scan the touched files. Proposed subject: `feat(seed): the development seed puts its user in a second company`.

### Task 13: The sample — the picker, the switch, the notice after a refresh

**Files:**
- Modify: `samples/notes-web/src/app/account-api.ts`, `samples/notes-web/src/app/auth/auth.service.ts`, `samples/notes-web/src/app/pages/notes.ts`, `samples/notes-web/src/app/texts.ts`, `samples/notes-web/src/app/app.ts` (only if the notice bar needs a new kind)
- Create (tests): `samples/notes-web/src/app/auth/auth.service.orgs.spec.ts`, `samples/notes-web/src/app/pages/notes.orgs.spec.ts`, `samples/notes-web/src/app/account-api.orgs.spec.ts`

**Interfaces:**
- `AccountApi.listOrgs()` → `GET /auth/orgs` (the interceptor adds the token: `/auth/org` prefix); `AccountApi.switchOrg(orgId)` →
  `POST /auth/switch` with `{"org_id"}` and credentials, as `refresh` is sent.
- `AuthService`: a signal `orgs` (the last list, `[]` before), a signal `orgChanges` (a counter the notes page watches to reload), methods
  `loadOrgs()` and `switchTo(orgId)` returning a result the page shows (`'switched' | 'gone' | 'signed-out'`).

**Behaviour** (spec "Sample frontend"):
- After a sign-in (where `loadMe` runs today) the service also loads the list.
- `switchTo`: `200` → keep the new token as the same session (the path `exchange()` uses for a renewed token; the `generation` does not change),
  `loadMe`, `loadOrgs`, bump `orgChanges`; `404` → `loadOrgs` and `'gone'`; `401` → `dropSession` and open `/login`, **no** refresh-and-retry
  (`/auth/switch` is in neither interceptor list; a unit test pins that the interceptor does not retry it).
- After every refresh (`exchange()`), read `/auth/me`; when its `org_id` differs from the one before and this tab did not just switch, bump
  `orgChanges`, reload the list, and show the notice `Switched to {name} in another tab.` (`{name}`: the new `org_name`; the personal space:
  `Private`).
- The account bar shows the picker when the list has more than one entry: a `<select>` (`data-testid="org-picker"`) with one option per entry,
  the company's name, the personal space as `Private`, the current one selected; choosing another calls `switchTo`. "Team" and "Delete
  account" follow `/auth/me` as today. The notes page reloads its notes when `orgChanges` changes.
- Texts in `texts.ts` (plain ASCII): the picker's label `Company`, `Private`, the notice with `{name}`, and for `'gone'`
  `That company is no longer yours. The list is up to date.`

- [ ] **Step 1: Write the failing tests** (Vitest; the existing spec files' helpers):
  - `account-api.orgs.spec.ts`: the two calls (method, path, body, credentials).
  - `auth.service.orgs.spec.ts`: after a sign-in the list is loaded; a `200` switch keeps the session (same generation), reloads `me` and the
    list and bumps `orgChanges`; a `404` reloads the list; a `401` drops the session, opens `/login` and sends no refresh; "a refresh that
    brings another org_id reloads the notes and shows the notice" (Review Focus 1); a refresh with the same `org_id` shows nothing; the
    interceptor sends `/auth/switch` without a bearer token and does not retry its `401`.
  - `notes.orgs.spec.ts`: one entry → no picker; two → the picker with both names and `Private` for the personal space, the current selected;
    choosing the other calls `switchTo`; `orgChanges` reloads the notes.
- [ ] **Step 2: See them fail** (`npm test`).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `npm test` (all specs, the earlier ones included), `npx ng build` (no error, no warning), `npm run check:docs`.
- [ ] **Step 5: Hand back.** Scan the touched files. Proposed subject: `feat(notes-web): a company picker in the account bar`.

### Task 14: The Playwright spec `12-orgs.spec.ts`

**Files:**
- Create: `samples/notes-web/e2e/12-orgs.spec.ts`
- Modify: an existing spec of `e2e/` only where the picker breaks an assertion (grep `01`–`11` for assertions on the account bar's children or
  on a `select`; list each change in the Hand back)

**Behaviour:** with the stack of `scripts/e2e-web.sh` (the seed's second company `Beta`): sign in as the seed user (`support/session.ts`
`signIn`) → the picker lists the first company and `Beta`; add a note; switch to `Beta` → the account bar names `Beta`, the note is not listed;
add a note in `Beta`; reload the page (a refresh, `support/page.ts` `watchRefreshes`) → still `Beta`, its note listed; sign out, sign in → lands
in `Beta`; **at the end, in a `finally` or `afterAll`, switch back to the first company** so WebKit (which runs after Chromium on the same
stack) and a re-run start where the other specs expect (Review Focus 5): a test "switching back leaves the seed user where it started" signs
out and in and asserts the first company.

- [ ] **Step 1: Write the spec** (it cannot run for the implementer: the verifier runs it).
- [ ] **Step 2: Type-check:** `npx tsc -p e2e/tsconfig.json --noEmit` passes.
- [ ] **Step 3: Hand back** with the exact command the verifier runs (`scripts/e2e-web.sh`). Scan the touched files. Proposed subject:
  `test(notes-web): the company picker end to end`.

### Task 15: `scripts/e2e-orgs.sh` and the run sequences

**Files:**
- Create: `scripts/e2e-orgs.sh` (LF; the orchestrator marks it executable)
- Modify: `scripts/e2e-tenancy.sh` (the run sequence at :51-54: `scripts/e2e-orgs.sh` after `scripts/e2e-private.sh`), `scripts/e2e-hardening.sh:33` (append `&& scripts/e2e-orgs.sh`), and every other script header that lists the full sequence (`grep -ln "e2e-private.sh" scripts/`)

**Behaviour:** the header in the style of `scripts/e2e-private.sh` (what it checks, step by step, with the criteria; the full sequence; the
stack and `.env` of the other scripts; it does not bring the stack up or down; it needs the seed's second company, so a fresh stack or a
restart of `auth`). It reads `AUTH_DEV_SEED_EMAIL`, `AUTH_DEV_SEED_PASSWORD` and `AUTH_DEV_SEED_SECOND_ORG_NAME` (default `Beta`) from `.env`
(parsed, never sourced), copies the small helpers it needs from `e2e-tenancy.sh` (call, val, jwt_payload, cli, expect_*), and runs the Goal:
1. login (jar) → `200`, `org_id` the first company;
2. `GET /auth/orgs` → two entries, roles `admin` and the plain role, `current` true on the first, the second named as the setting;
   without a token `401`;
3. switch (jar) → `200`, `org_id` the second; `org.switched` in the audit (through the CLI the other scripts use for the audit);
4. refresh (jar) → second;
5. a new login (jar2) → second;
6. switch to an unknown UUID → `404 {"error":"not_found"}` and jar still refreshes; `{"org_id":"x"}` → `400`; a form post → `400`;
7. `admin remove-member --org <second> --email <seed>` → `Member removed; the account is kept.`;
8. refresh (jar) → `401 invalid_grant`;
9. a new login (jar3) → the first company (the seed user is left as the next scripts expect).
It prints one line per step and exits non-zero at the first failure, as the other scripts.

- [ ] **Step 1: Write the script and the sequence edits.**
- [ ] **Step 2: Check:** `bash -n scripts/e2e-orgs.sh`; `shellcheck` if it is installed (say if not).
- [ ] **Step 3: Hand back** (the verifier runs it). Scan the touched files. Proposed subject: `test(e2e): scripts/e2e-orgs.sh runs the multi-company sequence`.

### Task 16: The documents and the changelog

**Files:** `docs/integration/angular.md`, `docs/integration/python-fastapi.md`, `clients/python/README.md`, `docs/security/threat-model.md`,
`docs/operations/backup.md`, `docs/design.md`, `CHANGELOG.md`

**Behaviour** (spec "Documents"):
- `angular.md`: listing companies, switching, the picker, reloading per-company data when `org_id` changes after a switch or a refresh; a
  removal signs out only the devices that work in that company; the private-accounts part (:214-235) — removal deletes the account only with
  its last space (:229).
- `python-fastapi.md`: the `org_id` rule (:166) — the company the person works in now, which can change between two requests after a switch;
  step 8 (:251) as needed.
- `clients/python/README.md`: one sentence on `Principal.org_id` (no code change; `_principal.py` is not touched).
- `threat-model.md`: the session's company; risks 62 to 65 after risk 61, word for word from the spec; the count of audit kinds 29 (:88).
- `backup.md:252`: 29 kinds.
- `design.md`: the item "Multiple orgs per user with org switching" moves out of "Still out" and "Beyond MVP", with a note naming slices
  0015–0017 (0015 built; 0016 and 0017 specified).
- `CHANGELOG.md`: a new `## Unreleased` section above `## 0.2.0 - 2026-11-21`: several companies per account, `GET /auth/orgs`,
  `POST /auth/switch`, the session's company, the sign-in lands in the company used last, the changed rule for removals and company deletions
  (the account is kept with another space), `org.switched` (29 kinds), `remove-member`'s two lines, the seed's `SecondOrgName`, the sample's
  picker.

- [ ] **Step 1: Write.** **Step 2:** `python scripts/check-docs.py` passes (and `npm run check:docs` in the sample if a
  sample document changed). **Step 3: Hand back.** Scan the touched files. Proposed subject: `docs: several companies per account`.

### Task 17: The final check (verifiers; no implementer)

- [ ] The whole .NET suite on Windows after the build gate (criterion 2: every test of `main` green, apart from the ones this plan names as
  changed: the two audit-count tests, the OpenAPI counts, `AuditLogTests`' constructor call, the CLI line, `OrgMemberTests`' return type).
- [ ] `scripts/test-linux.sh` (criterion 2).
- [ ] The e2e sequence of `scripts/e2e-hardening.sh`'s header, with `scripts/e2e-orgs.sh` in it (criterion 32), then `scripts/e2e-web.sh`
  (criteria 31: `12-orgs.spec.ts` and the existing specs, Chromium and WebKit).
- [ ] The acceptance map: every row's tests exist under these names and pass.

## Decisions taken in this plan

The spec is not changed by this plan. One line each; each is covered by a test.

1. **Lock order** company → address → membership rows → account row; `AtomicSignInResult` locks the membership before the account,
   `AccountDeletion` locks the memberships before the accounts, the provider link locks its membership before its hash read (Tasks 3, 8).
2. **A refresh holds its membership `FOR KEY SHARE`** in the session's transaction, so a removal waits for it, and writes nothing (Task 4).
3. **A switch to another company also holds the session's company's membership**, so "the session's company vanished in that window" is
   `401` as the spec's Locks say; a switch to its own company that finds the row gone is `401` (the session's company is gone, check 2 before
   check 3) (Task 11).
4. **The session claims** are `session_org_id` (the id as text) and `session_org_joined` (whole Unix microseconds, rounding down), set by one
   helper; a principal without them reaching `AtomicSignInResult` is a programming error (Tasks 3, 4).
5. **An old token** (no claims) takes the sign-in's space at a refresh and a switch; its reuse audit names that space too (Tasks 4, 5, 11).
6. **`GET /auth/orgs`** is authorized like `/auth/me` but not through `CompanyAccess`; an unparseable `sub` is `403 forbidden`; the id order
   is the ordinal order of the lowercase text (Task 9).
7. **`DELETE /auth/account`** counts the memberships with the token's space read, before and again under the address lock (Task 7).
8. **The removal counts the remaining memberships with a plain read and no account lock**, as spec 0017 requires; two parallel removals of
   one person from their last two companies can leave an account with no space, which the start-up step of spec 0010 deletes and which a
   login answers `403 no_membership` until then — accepted, recorded here, and closed by 0017's recount (Task 8).
9. **`account.deleted` without a lost space** (the start-up cleanup) names the space the sign-in would take, in the order of Task 2
   (Task 8).
10. **`MemberService.RemoveAsync` returns `Outcome<MemberRemoval>`**; the company API shows nothing of it, the CLI prints it (Task 8).
11. **`/auth/switch` is a token-endpoint path** with its own extraction handler at +275; its body is checked before the cookie; the shared
    predicate `IsSessionPath` serves the session's answers and the outage, never the charset guard (Task 10).
12. **The seed's plain role** is chosen in the manifest's order of default roles (the unverified seed user's choice, by name, stays as it is);
    `AuthAppFactory` blanks `SecondOrgName` (Task 12).
13. **The sample** loads the list after a sign-in and after a switch, reads `/auth/me` after every refresh, and does not treat a switch's
    `401` as a reason to refresh (Task 13).
14. **`clients/python/src/.../_principal.py` is not touched** (its docstring "in which space" stays true); only the README says what changed
    (Task 16).

## Open questions for the owner

1. **Which release carries 0015–0017** (spec 0015, open question 1): decided at the merge of 0015. This plan writes `## Unreleased` in the
   changelog and tags nothing.
