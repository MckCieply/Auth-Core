# Self-service Sign-up Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0009
- **Date:** 2026-03-29
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0009-self-service-signup.md`](../specs/0009-self-service-signup.md)
  — the plan argues from the spec; where they disagree, **the spec wins** and the
  disagreement is a finding (see [`docs/workflow.md`](../../workflow.md)). The spec is not changed by this plan (the owner amended it on 2026-04-03, Amendment 2: the completion signs the person in; the section "Amendment (2026-04-03)" below, Tasks 14 and 15, builds it; and on 2026-04-04, Amendment 3: a person without a company comes back by sign-up; the section "Amendment (2026-04-04)" below, Tasks 16 and 17, builds it); the points
  that cannot be built as written, or that the spec leaves open, are listed in "Open questions for the owner" at the end (there are none left: the choices the plan took are recorded in "Decisions taken in this plan"). **Amendment 4 (final review of the slice) supersedes the order of the checks in the completion.** The spec's Amendment 4 settled the order as **token, then account, then password** (an account that belongs to a company, or that only resembles the typed address, is `409` whatever the password; `400 weak_password` applies only when there is no such account), and added the look-alike voiding of the earlier link (item 1), the dropping of queued sign-up mails when sign-up is off (item 3) and the notice to the stored address (item 4). The passages of this plan that describe the old order "token, password, account" (the "Global Constraints" line for `POST /auth/signup/complete`, the "order inside the transaction" paragraph of Task 8, the Amendment (2026-04-04) item 4 and the test-edit step (b) that has a weak password beat the `409`, and the test name `The_order_of_the_checks_is_token_then_password_then_account`) are superseded; the code and the test `The_order_of_the_checks_is_token_then_account_then_password` follow the spec.
- **Based on:** `3feb9fd` (`main` at `85df18b` with the phase brief in `docs/design.md` and spec 0009 on top of it). Every anchor of an edit below was
  checked against this base.

**Goal:** A product can let a visitor start on their own: the visitor gives an email address and a company name, proves the address through a mail
link, chooses a password, and is then the first admin of a new company of their own. Sign-up is off unless the product's manifest turns it on, and an
instance that does not turn it on behaves exactly as `v0.1.0`.

**Architecture:** A **pending sign-up** is a row of its own (table `PendingSignups`, one per normalised address), not an account. `POST /auth/signup` runs
the existing mail limit under a new kind (`MailKind.Signup = 4`), upserts the pending row and queues a mail request in one transaction, and never reads
the accounts. The **dispatcher** decides what to send when the row is handled: the pending sign-up plays the part the invitation plays for invitation
mails (it is locked, it holds the hash of the current link token), and an account under the address turns the mail into "you already have an account"
with no link. `POST /auth/signup/complete` consumes the token with one `DELETE ... RETURNING`, takes the advisory lock that invitation acceptance uses,
checks the password and the account, and then in one transaction creates the account (email confirmed), the company (`CompanyService.CreateAsync`, `via`
`signup`) and the membership with the first admin's role, with the audit rows, and (Amendment 2, Tasks 14 and 15) signs the person in inside that same
transaction: the endpoint is a pass-through request of OpenIddict beside `/auth/login`, and `AtomicSignInResult` issues the session and writes `login.succeeded` before the one commit. A `SignupGate` (manifest key **and** completion address) decides whether
sign-up is on; `GET /auth/options` tells a product's screens. The sample frontend gets `/signup`, `/complete-signup` and a link on `/login`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, ASP.NET Identity, EF Core on PostgreSQL 16 (Npgsql), xUnit v3 with Testcontainers; YamlDotNet (the
manifest); Angular 21 (standalone components, signals, reactive forms), Vitest on jsdom and Playwright (Chromium and WebKit); bash and Python 3 (standard
library only) for the e2e script; Docker Compose. **No new NuGet, npm or pip package** is added (owner rule): everything below uses the framework and the
packages already referenced.

## Global Constraints

Values copied from the spec, one line each; every task includes them.

- **Sign-up is on** when the manifest says `signup: self-service` **and** `Auth:App:FrontendUrls:CompleteSignup` is configured; otherwise it is off. While it is off, `POST /auth/signup`, `/auth/signup/preview` and `/auth/signup/complete` answer `403 {"error":"signup_disabled"}` before reading the body; a request made while it was on and completed after it was turned off is refused the same way.
- **General rules:** JSON bodies of at most 8 KiB in UTF-8, unknown members ignored, `400 invalid_request` for a body that is not a JSON object or a required member that is missing, empty or not a string; `Cache-Control: no-store` on every answer; the security headers of spec 0008.
- **`GET /auth/options`:** `200 {"signup":"self-service"}` or `200 {"signup":"invite-only"}`, following the rule above; no authentication; a client ignores members it does not know (slice 0010 adds `providers`).
- **`POST /auth/signup` `{"email","company_name"}`:** off → `403 signup_disabled`; `email` must pass the address rule of invitations (a mailbox address, no look-alike, IP-literal, dotless, numeric or hexadecimal domain; at most 254 characters) and `company_name` the name rule of spec 0005 (1 to 100 characters, no control, format or noncharacters, no leading or trailing space), otherwise `400 invalid_request`; the mail limit of spec 0004 with its own kind `signup` (one request a minute and five an hour), over it the `429 too_many_attempts` with `retry_after_seconds`; otherwise the pending sign-up of the address is created or replaced (typed address and company name; expires 24 hours later), a mail request is queued and the answer is `202` with an empty body. The request never reads the accounts: an address with an account and one without get the same status, headers and body and run the same database statements; which mail goes out is decided when the mail is sent.
- **Mails (when sent):** the account check comes first (the owner's ruling): an address with an account that belongs to a company gets "you already have an account" (no link, no token; says someone, perhaps the reader, tried to sign up, that the account exists, and that the reader can sign in or use "forgot password" on the product's sign-in screen) **even when its pending sign-up is gone**, and any pending sign-up of the address is deleted; otherwise (no account, or, spec 0009 Amendment 3 of 2026-04-04, an account that belongs to **no** company), if the pending sign-up still exists, a new link token is issued (the previous link stops working), valid 24 hours from the sending, and "complete your sign-up" goes out with `<CompleteSignup>?token=<43 characters>` and the company name; with no live pending sign-up (expired, completed or replaced) and no account that belongs to a company nothing is sent. Both mails: the instance's locale (`pl` or `en`), HTML and text parts, the app name, the queue, its retries and its delivery rules; a failed sending leaves the previous link valid.
- **`POST /auth/signup/preview` `{"token"}`:** `200 {"email","company_name","has_account"}` (the typed address, the company name of the request, and whether an account without a company has the address: `true` only then, Amendment 3) for a usable token, otherwise `400 invalid_token`; it changes nothing.
- **`POST /auth/signup/complete` `{"token","password","company_name"}`:** off → `403`; a missing or non-string member, a password with a NUL character or a company name that breaks the name rule → `400 invalid_request`; a token that is unknown, used, expired or replaced → `400 invalid_token`; a password that breaks the policy of spec 0004 → `400 weak_password` with the rules (the token stays usable; **Amendment 3:** for an account that belongs to no company the password may be absent, is not used, and the account keeps the one it has); an account with the address that belongs to a company by now → `409 {"error":"already_registered"}` and the pending sign-up is deleted; otherwise in one transaction the account (typed address, the password, email confirmed; for an account without a company, that account as it is, its email confirmed), the company (the name of this request, a copy of the default roles, as `create-org` does), the membership with the first admin's role, the pending sign-up deleted and the audit events; **the answer is that of a successful `POST /auth/login`** (`200 {"status":"authenticated","access_token"}` and the refresh cookie; the token carries the new company and the first admin's role), and the sign-in is part of the transaction with its `login.succeeded` row: if it cannot be written nothing of the completion stays (spec 0009, Amendment 2 of 2026-04-03: Tasks 14 and 15 change what Tasks 8, 11 and 12 built, which answered `204`). The order of the checks is token, account, password (Amendment 4 of the spec; the plan first said token, password, account). Of parallel requests with one token exactly one gets past the token; the address is locked with the lock that invitation acceptance uses.
- **The first admin's role:** the first default role of the manifest, in the manifest's order, whose permissions are `["*"]`; if no role holds `*`, the first one whose permissions include `members:manage`.
- **Pending sign-ups:** one per normalised address; hold the typed address, the company name, the hash of the current link token (none until the first mail is sent), the time its link expires and the time the row expires (Amendment 2); not an account (login, `forgot password` and invitations do not see it); the hourly clean-up deletes those whose time has passed.
- **Manifest:** a new optional top-level key `signup: invite-only` (the default when absent) or `signup: self-service`; any other value makes the manifest invalid by the rules of spec 0005 (the last valid manifest stays active, `/auth/health` reports `Degraded`); a manifest stored by an earlier version without the key is read as `invite-only`.
- **Configuration:** `Auth:App:FrontendUrls:CompleteSignup`, environment variable `AUTH_FRONTEND_SIGNUP_URL` in `deploy/docker-compose.prod.yml` (optional, may be empty); when set and not empty, the rules of the other frontend addresses of spec 0004 (absolute; `https` outside Development; no fragment, user information or `token` parameter), a bad value stops the start naming the key; `self-service` without the address: sign-up is off, the start logs a warning and `/auth/health` reports `Degraded`.
- **Limits (spec 0008):** `POST /auth/signup` counts against the mail policy (`Email`, 10 a minute by default); `/auth/signup/preview` and `/auth/signup/complete` against the invitation policy (`Invite`, 20 a minute); `GET /auth/options` against the general one.
- **Audit (24 kinds in all):** `signup.requested` when `POST /auth/signup` is accepted (`202`; field `subject_email`); `signup.completed` when the completion succeeds, in its transaction (`subject_user_id`, `subject_email`, `org_id`, `org_name`; `details.existing_account` `true` for an account that existed, Amendment 3); `org.created` gains the value `signup` for `details.via`, written in the same transaction, and so is the `login.succeeded` row of the completion's sign-in (Amendment 2); no row holds a password or a token.
- **OpenAPI:** the four operations are in the document with their answers.
- **Sample (`samples/notes-web`):** `/signup` (email and company name; on success "Check your inbox" whatever the address; `429` shows the wait; `403 signup_disabled` says that sign-up is closed); `/complete-signup` (the token leaves the address bar; the preview shows the address read-only and the company name editable; the password twice, checked on the screen; success signs the person in and opens the notes screen at once (Amendment 2; the address in no URL); when the preview says `has_account` the password fields are not shown and only the company name is asked for (Amendment 3); `invalid_token` offers to sign up again; `already_registered` says the account exists and links to sign-in; `weak_password` lists the rules); `/login` shows "Create an account" when `GET /auth/options` says `self-service`; the new routes are public; texts are English.
- **Off by default, nothing changes for an instance that does not turn it on** (Toviq runs `v0.1.0` in production): no new required setting, the `v0.1.0` manifest, settings and database keep working, and the earlier tests pass; the tests that enumerate the contract get lines added and nothing else: `OpenApiTests` (lines added, each replacing the previous task's line by itself and the new one, and one `foreach` array extended), `AuditTableTests` (one list extended and the one test method renamed from `...twenty_two...` to `...twenty_four...`) and `ManifestActivationTests` (one assertion); the per-IP classification has a new test class beside `RatePolicyTests`.
- **Workflow** ([`docs/workflow.md`](../../workflow.md)): local only (no CI, no pull request), Conventional Commits in English, documentation in English. **The orchestrator makes the commits**: implementers leave their changes uncommitted in the working tree, and the orchestrator reads the diff, runs the gate and commits with the subject named in the task's last step ("Hand back"). Every task ends with that step; it lists files and a proposed subject, never a git command. One Sonnet implementer writes the code of one task, runs it and fixes what breaks; the code below is the plan's, written on paper, and the implementer's tests decide.
- **The EF migration** (Task 3) is created with `dotnet ef migrations add AddPendingSignups`; its id is the orchestrator's business afterwards, and no test or document may name it.
- **.NET:** run tests with `"C:/Program Files/dotnet/dotnet.exe" test tests/Auth.IntegrationTests` (at the repository root `dotnet test` discovers no tests: it can also be built and `tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe` run); the build has warnings as errors (`-warnaserror` is the gate, the analyzers are strict: `ArgumentNullException.ThrowIfNull`, explicit `StringComparison`, `CultureInfo.InvariantCulture`, `[LoggerMessage]` partial classes). Test classes follow the existing bases (`SessionTestBase`, `MailTestBase`, `TenancyTestBase`, `AuditTestBase`) and the injected `FakeTimeProvider` (`Clock`).
- **Angular and Node:** Node 24 is on `PATH`; the sample's packages are pinned in `samples/notes-web/package.json` and **no package is added**; run `npm ci` once in `samples/notes-web` when `node_modules` is missing, then `npx ng test --watch=false`, `npx ng build` and `npm run check:docs` there. Texts are plain printable ASCII (`texts.spec.ts` checks it).
- **Every compose command carries its own project name.** A script or an instruction that starts, stops, runs, execs in or removes a stack sets `COMPOSE_PROJECT_NAME` to a name of its own (`auth-core-hardening` for the older scripts and `scripts/e2e-signup.sh`, `auth-core-notes`, `auth-core-web` or `auth-core-web-<suffix>` for `scripts/e2e-web.sh`, `auth-core-prodtest`); never `auth-core`, the owner's development project, which a bare `down -v` would wipe.
- **No `docker compose up` of the Auth-Core stack and no stopping of containers by implementers** (another session holds ports 8080 and 8025): tests use Testcontainers; the live scripts (`scripts/e2e-signup.sh`, `scripts/e2e-web.sh`, the older ones) are run by the verifiers, one stack at a time.
- **Python on this machine:** `python3` on `PATH` is the Microsoft Store shim and does not work. Use `C:/p6v/Scripts/python.exe` (for `scripts/check-docs.py`, and for `scripts/e2e-signup.sh` through `E2E_PYTHON=C:/p6v/Scripts/python.exe`: the script does not read `PYTHON`, which other tools use).
- **Shell scripts** are LF; after the implementer creates one, the **orchestrator** marks it executable in the index; the implementer does not touch git state.
- **Unicode escapes stay out of the source.** A backslash followed by `u` and four hex digits typed in a tool parameter arrives as the character. The tests below build such text with `char.ConvertFromUtf32(...)`, `(char)0x...` or a `Backslash` constant; after writing files, `grep -rInP '\\u[0-9a-fA-F]{4}' src tests scripts docs samples/notes-web/src samples/notes-web/e2e --include=*.cs --include=*.sh --include=*.md --include=*.ts` must find nothing that is not meant.

## Verified before this plan was written

Probes in a scratch directory outside the repository (a file-based `dotnet run`), not a copy of the plan's code:

- **A stored manifest of `v0.1.0`:** `JsonSerializer` with `SnakeCaseLower` reads `{"permissions":[...],"default_roles":[...]}` into a record `StoredManifest(string[] Permissions, StoredRole[] DefaultRoles, string? Signup = null)` with `Signup` null, and writes `"signup":"invite-only"` for the new form. The read maps null (and any value that is not `self-service`) to `invite-only`.
- **YamlDotNet and the new key:** with the parser's naming convention, `signup:` and `signup: ~` give `null` (read as absent), `signup: ''` gives the empty string (invalid), `signup: true`, `signup: 1` and `signup: Self-Service` give the strings `true`, `1` and `Self-Service` (invalid by the exact comparison), and `signup: [a]` throws a `YamlException` ("No node deserializer was able to deserialize the node into type System.String"), which `ManifestParser.Parse` already turns into "the file is not valid YAML for a manifest".
- **The test host runs in `Development`**, so it reads `src/Auth.Server/appsettings.Development.json`: adding the completion address there would turn the address on in every test host. `AuthAppFactory` therefore pins `Auth:App:FrontendUrls:CompleteSignup` to blank (as it pins the other three addresses), and `WithSignup()` sets the manifest and the address together.
- **What `deploy/auth.yaml` with `signup: self-service` does to the live scripts:** the development stack reads the address from `appsettings.Development.json` (so `scripts/e2e-*.sh` still see `Healthy`, and `scripts/e2e-web.sh` overrides it through `samples/notes-web/compose.yml`), but `scripts/e2e-prod.sh` mounts `deploy/auth.yaml` into the **production** compose file, which has no address unless `AUTH_FRONTEND_SIGNUP_URL` is set: its step 2 expects `Healthy`, so Task 9 sets the variable in the script's environment file.
- **`scripts/check-docs.py` on `docs/integration/angular.md`** fails today on seven code spans (`src/app/auth/`, `src/app/pages/`, ... are paths from `samples/notes-web`, not from the repository root); `npm run check:docs` in the sample accepts them from either root. Task 13 writes them from the repository root (both tools pass) and adds the guide to the default list of `check-docs.py`.
- **The OpenAPI document and the rate limiter need no code for the new paths' common answers:** `415` (the charset guard covers every `POST` under `/auth/`) and `429` are added to every operation by `OpenApiSetup`; `RatePolicies.Classify` needs the three new `POST` paths and nothing for `GET /auth/options` (general already).
- **Not proved on this machine** (each has its test in the task named): what `dotnet ef migrations add` generates (Task 3 compares it with the expected columns); the interleavings of parallel completions with each other and with an invitation (Task 8's race tests loop over several addresses); that WebKit keeps the flow working (Task 12, run by the verifiers).

## Review Focus

The inputs and failure modes the spec implies that a person using this software is most likely to meet, most likely first; each has its test in the task named.

1. **An instance that never turns sign-up on, upgraded from `v0.1.0`** (Toviq): a database made by the earlier migrations, a manifest stored without the key, no completion address, a production compose file that passes an empty variable. The person expects nothing to change and the host to start (`UpgradeFromV010Tests.A_database_of_v0_1_0_gets_the_table_keeps_its_rows_and_reads_its_manifest_as_invite_only`, `ManifestSignupActivationTests.A_manifest_stored_by_v0_1_0_stays_invite_only_when_the_file_is_missing`, `SignupGateTests.Sign_up_is_off_unless_the_manifest_says_self_service_and_the_address_is_set`, `ShippedConfigurationTests.A_production_host_with_the_manifest_key_and_an_empty_address_starts_with_sign_up_off`; Tasks 1, 3 and 9).
2. **The same person under another spelling of the address** (capitals, trailing space in the form, a look-alike letter): `Anna@Firma.test` and `anna@firma.test` are one pending sign-up; an account spelled another way still means "you already have an account" and `409 already_registered` (narrowed by Amendment 3: an account that belongs to a company; an account without a company spelled another way in ASCII case only is a comeback, and one that the normaliser merely folds together with the typed address gets the notice and the `409`); the mail goes to the address as the account stores it (`SignupRequestTests.Spellings_of_one_address_share_one_pending_sign_up_and_one_limit`, `SignupDispatchTests.The_account_exists_mail_goes_to_the_stored_address_spelled_as_stored`, `SignupCompleteTests.An_account_under_another_spelling_of_the_address_is_409_too`; Tasks 5, 6 and 8).
3. **The person who asks twice, clicks an old link or presses the button twice:** the second request within a minute is the `429` with the wait, an older link after a newer mail is `invalid_token`, a used link is `invalid_token`, a double submit makes one account (`SignupRequestTests.A_second_request_within_a_minute_is_refused_and_asking_again_does_not_add_to_the_wait`, `SignupPreviewTests.The_older_link_of_an_address_is_invalid_and_the_newer_shows_the_newer_company`, `SignupCompleteRaceTests.Of_parallel_completions_with_one_token_exactly_one_is_204`, `complete-signup.spec.ts` "sends one request for a double submit"; Tasks 5, 7, 8 and 11).
4. **Company names people type:** Polish letters, exactly 100 characters, an emoji (a surrogate pair), a name with `&` and `<` in the HTML part of the mail, spaces around the name in the form (the screen trims; the API refuses) (`SignupRequestTests.Names_at_the_limit_with_polish_letters_and_pairs_are_kept_whole`, `SignupDispatchTests.Names_at_the_limit_with_polish_letters_and_pairs_reach_the_mail_whole`, `SignupMailComposerTests.The_company_name_is_encoded_in_the_html_and_is_not_in_the_subject`, `SignupCompleteTests.The_company_name_of_the_request_is_only_a_suggestion`; Tasks 4, 5, 8 and 11).
5. **The mail relay is down or slow when the person asks:** the request is still `202`, the mail is retried, the earlier link still works, and a failed send to an existing account keeps the pending sign-up until the mail has gone (`SignupDispatchTests.A_failed_send_leaves_the_previous_link_valid_and_the_retry_sends_a_new_one`, `SignupDispatchTests.A_failed_send_to_an_account_keeps_the_pending_sign_up_until_the_mail_is_sent`; Task 6).

## File Structure

New and changed files by responsibility. Everything under `src/Auth.Server/` unless said otherwise; tests under `tests/Auth.IntegrationTests/`.

| Area | Files | Responsibility |
| --- | --- | --- |
| Manifest and switch | `Tenancy/Manifest.cs`, `Tenancy/ManifestParser.cs`, `Signup/SignupGate.cs`, `Signup/SignupHealthCheck.cs`, `Email/MailSettings.cs`, `Tenancy/Outcome.cs` (`TenancyErrors`) | the `signup` key and its stored form, the first admin's role, the optional completion address, "is sign-up on", the `Degraded` health and the start warning (Task 1) |
| Options and limits | `Signup/SignupOptionsEndpoint.cs`, `Signup/SignupContracts.cs`, `Api/SignupRoutes.cs`, `RateLimiting/RatePolicy.cs` | `GET /auth/options`, the bodies of the four endpoints for OpenAPI, the per-IP classification of the four paths (Task 2) |
| Pending sign-ups | `src/Auth.Infrastructure/Persistence/PendingSignup.cs`, `AuthDbContext.cs`, `MailKind.cs`, the migration `AddPendingSignups`, `Signup/PendingSignups.cs`, `Email/EmailPruner.cs`, `Email/EmailPruningService.cs` | the table, the statements that make, lock, issue for, consume and delete a pending sign-up, the hourly clean-up (Task 3) |
| Mails | `Email/MailTexts.cs`, `Email/MailComposer.cs` | the two mails in Polish and English, HTML and text (Task 4) |
| Request | `Signup/SignupRequestEndpoint.cs`, `Email/MailRequestStore.cs`, `Audit/AuditKinds.cs` | `POST /auth/signup`: validation, the mail limit, the pending row, the audit row (Task 5) |
| Dispatch | `Email/MailDispatcher.cs` | what the queue does with a sign-up row: drop, lock, issue a token or say "you already have an account" (Task 6) |
| Preview and completion | `Signup/SignupService.cs`, `Signup/SignupPreviewEndpoint.cs`, `Signup/SignupCompleteEndpoint.cs` | the preview, the completion in one transaction (Tasks 7 and 8) |
| The completion signs in (Amendment 2) | `Sessions/SessionPrincipal.cs`, `Signup/SignupCompletion.cs`, `Signup/SignupCompleteRequestHandler.cs`, `Login/AtomicSignInResult.cs`, `Login/LoginEndpoint.cs`, `Login/LoginResponseShaper.cs`, `Tokens/OpenIddictSetup.cs`, `Signup/SignupService.cs`, `Signup/SignupCompleteEndpoint.cs`; the sample's `auth/auth.service.ts` and `pages/complete-signup.ts`; `samples/notes-web/e2e/09-signup.spec.ts`, `scripts/e2e-signup.sh` | the principal both sign-ins share, the third token endpoint, the transaction handed to the sign-in, the screen that opens the notes (Tasks 14 and 15) |
| The comeback of a person without a company (Amendment 3) | `Signup/SignupAccounts.cs`, `Signup/SignupService.cs`, `Signup/SignupContracts.cs`, `Signup/SignupPreviewEndpoint.cs`, `Signup/SignupCompleteEndpoint.cs`, `Requests/JsonObjectBody.cs`, `Email/MailDispatcher.cs`, `Api/SignupRoutes.cs`; the sample's `account-api.ts`, `auth/auth.service.ts`, `texts.ts`, `pages/complete-signup.ts`; `samples/notes-web/e2e/09-signup.spec.ts`, `scripts/e2e-signup.sh`, `scripts/e2e-web.sh` | which account a link may use, the optional password, `has_account`, the screen with the company name only (Tasks 16 and 17) |
| Shipping | `Program.cs`, `appsettings.Development.json`, `deploy/auth.yaml`, `samples/notes-api/auth.yaml`, `deploy/docker-compose.prod.yml`, `deploy/.env.prod.example`, `samples/notes-web/compose.yml`, `scripts/e2e-prod.sh` | the registrations, the shipped manifests and the address in every stack (Tasks 1, 2, 7 and 9) |
| Sample | `samples/notes-web/src/app/account-api.ts`, `texts.ts`, `auth/auth.service.ts`, `pages/signup.ts`, `pages/complete-signup.ts`, `pages/login.ts`, `app.routes.ts` and their specs, and the older `pages/login.too-many-requests.spec.ts` | the screens (Tasks 10 and 11) |
| Live checks | `samples/notes-web/e2e/09-signup.spec.ts`, `e2e/support/mail.ts`, `scripts/e2e-signup.sh`, `scripts/e2e-web.sh` (the seeding of two addresses), the run sequences in the headers of `scripts/e2e-tenancy.sh` and `scripts/e2e-hardening.sh` | the browser and API end-to-end checks (Task 12) |
| Documents | `docs/integration/angular.md`, `docs/deployment/vps.md`, `docs/security/threat-model.md`, `docs/operations/backup.md`, `README.md`, `samples/notes-web/README.md`, `CHANGELOG.md`, `scripts/check-docs.py`, `docs/superpowers/plans/0009-acceptance-map.md` | the guide, the deployment guide, the threat model, the audit kinds, the changelog, the acceptance map (Task 13) |

## The tasks

| Day | Task | Deliverable |
| --- | --- | --- |
| Mon 30.03 | 1 | The `signup` manifest key, the first admin's role, the optional completion address, the gate, `Degraded` health and the start warning |
| Mon 30.03 | 2 | `GET /auth/options` and the per-IP classification of the four paths |
| Mon 30.03 | 3 | Pending sign-ups: the table, the migration, `MailKind.Signup`, the statements and the hourly clean-up |
| Tue 31.03 | 4 | The two mails in Polish and English, HTML and text |
| Tue 31.03 | 5 | `POST /auth/signup`: validation, the mail limit, the pending row and the audit rows |
| Tue 31.03 | 6 | The dispatcher: the sign-up mail, the "you already have an account" mail, the retries |
| Wed 01.04 | 7 | `POST /auth/signup/preview` |
| Wed 01.04 | 8 | `POST /auth/signup/complete`: the account, the company, the membership, the audit, the races |
| Wed 01.04 | 9 | The shipped files: development settings, both manifests, the production compose file, the environment example, the sample overlay |
| Thu 02.04 | 10 | The sample: API calls, texts, `/signup`, the link on `/login` |
| Thu 02.04 | 11 | The sample: `/complete-signup`, the routes |
| Fri 03.04 | 12 | The Playwright test and `scripts/e2e-signup.sh` |
| Fri 03.04 | 14 | Amendment 2: the completion signs the person in: the server (the completion as a pass-through request of OpenIddict, one transaction with the sign-in) |
| Fri 03.04 | 15 | Amendment 2: the sample opens the notes screen after a completion; the Playwright test and `scripts/e2e-signup.sh` follow |
| Sat 04.04 | 16 | Amendment 3: a person without a company comes back by sign-up: the server (the dispatcher sends the link to an account without a company, the preview says `has_account`, the completion uses that account as it is, the `409` race with an invitation) |
| Sat 04.04 | 17 | Amendment 3: the completion screen asks only for the company name when the preview says `has_account`; unit tests, the Playwright test and Step 12 of `scripts/e2e-signup.sh` (the member removed through the operator CLI) |
| Sat 04.04 | 13 | The guides, the threat model, the changelog, `check-docs.py` and the acceptance map (24 criteria; documents the comeback too) |
| after Task 13 | verifiers | The three verifiers, the "As built", the merge (outside this plan, owner-gated) |

Tasks run in order inside a day and across days (the order is 12, 14, 15, 16, 17, 13: Task 13 documents what Tasks 14 to 17 changed); each leaves `dotnet build -warnaserror` and the tests of the files it touches green, and each task's tests are
written first and seen to fail.

---

## Day 1 — Monday 30.03: the switch, the options, the table

When the day is done, a product's manifest can say `signup: self-service`, the completion address can be configured, the service knows whether sign-up is on (and says `Degraded` and warns when the manifest asks for it without the address), `GET /auth/options` tells a screen, the four paths are classified for the per-IP limits, and the database has the table that holds pending sign-ups, with the clean-up that removes the expired ones. Three tasks; every task leaves `dotnet build -warnaserror` and the tests of the files it touches green.

### Task 1: The `signup` manifest key, the first admin's role, the completion address, the gate

**Files:**
- Create: `src/Auth.Server/Signup/SignupGate.cs`, `src/Auth.Server/Signup/SignupHealthCheck.cs`
- Modify: `src/Auth.Server/Tenancy/Manifest.cs` (replaced as a whole), `src/Auth.Server/Tenancy/ManifestParser.cs`, `src/Auth.Server/Tenancy/Outcome.cs` (`TenancyErrors`), `src/Auth.Server/Email/MailSettings.cs`, `src/Auth.Server/Program.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`
- Create (tests): `tests/Auth.IntegrationTests/Infrastructure/SignupVariants.cs`, `ManifestSignupTests.cs`, `ManifestSignupActivationTests.cs`, `CompleteSignupSettingsTests.cs`, `SignupGateTests.cs`, `SignupErrorCodesTests.cs`

**Interfaces:**
- Consumes: `ManifestParser`, `Manifest`, `ManifestHolder`, `MailSettingsLoader`, `AuthAppFactory` (`WithManifest`, `WithSetting`, `WithServices`), `CapturingLoggerProvider`.
- Produces:
  - `enum SignupMode { InviteOnly = 0, SelfService = 1 }` and `static class SignupModes` with `const string InviteOnlyName = "invite-only"`, `const string SelfServiceName = "self-service"`, `string NameOf(SignupMode)` and `bool TryParse(string? name, out SignupMode mode)` (exact, lower case), all in `Auth.Server.Tenancy`.
  - `Manifest(IReadOnlyList<string> permissions, IReadOnlyList<DefaultRole> defaultRoles, SignupMode signup = SignupMode.InviteOnly)`; `Manifest.Signup`; `Manifest.FirstAdminRole` (`DefaultRole`; the first default role holding `*`, else the first holding `members:manage`); `ToJson()`/`FromJson()` carry the mode (a stored form without it reads as `InviteOnly`).
  - `MailSettings.CompleteSignupUrl` (`Uri?`) and `MailSettingsLoader.CompleteSignupUrlKey = "Auth:App:FrontendUrls:CompleteSignup"`.
  - `Auth.Server.Signup.SignupGate` (singleton, `Program.cs` registers it): `bool IsOn`, `bool MissingAddress`, `string ModeName`, `void WarnIfMissingAddress()`; `SignupHealthCheck` (`Degraded` while `MissingAddress`).
  - `TenancyErrors.SignupDisabled = "signup_disabled"` (status 403) and `TenancyErrors.AlreadyRegistered = "already_registered"` (status 409).
  - Test side: `AuthAppFactory.DefaultCompleteSignupUrl`, `AuthAppFactory.SelfServiceManifest`, `AuthAppFactory.WithSignup()` (manifest `signup: self-service` and the address, together), the pin of the address to blank in the constructor; `SignupVariants` (`Off` theory data, `Apply(factory, variant)`, the four variant names).

**Decisions the plan takes here** (the spec leaves them open):
- `signup:` with no value (or `~`) counts as absent (`invite-only`), like `permissions:` with no value; `signup: ''` and every other scalar are invalid.
- A completion address that is only white space counts as not set, like a blank one (compose passes an empty variable).
- `Manifest.FirstAdminRole` throws `InvalidOperationException` for a manifest with no managing role; the parser already refuses such a manifest, and a stored one was once valid.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/Infrastructure/SignupVariants.cs`:

```csharp
using Auth.Server.Email;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// The ways an instance can have sign-up off (spec 0009, criteria 1, 2 and 17), as a name that a theory can carry and a way to make the
/// host of each. "no-key" is an instance of <c>v0.1.0</c>: its manifest and its settings.
/// </summary>
public static class SignupVariants
{
    public const string NoKey = "no-key";
    public const string NoKeyWithAddress = "no-key-with-address";
    public const string InviteOnlyWithAddress = "invite-only-with-address";
    public const string SelfServiceWithoutAddress = "self-service-without-address";

    public static TheoryData<string> Off => new() { NoKey, NoKeyWithAddress, InviteOnlyWithAddress, SelfServiceWithoutAddress };

    /// <summary>Call it before the host is built.</summary>
    public static AuthAppFactory Apply(AuthAppFactory factory, string variant)
    {
        ArgumentNullException.ThrowIfNull(factory);

        switch (variant)
        {
            case NoKey:
                return factory;
            case NoKeyWithAddress:
                return factory.WithSetting(MailSettingsLoader.CompleteSignupUrlKey, AuthAppFactory.DefaultCompleteSignupUrl);
            case InviteOnlyWithAddress:
                return factory
                    .WithManifest(AuthAppFactory.DefaultManifest + "\nsignup: invite-only\n")
                    .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, AuthAppFactory.DefaultCompleteSignupUrl);
            case SelfServiceWithoutAddress:
                return factory.WithManifest(AuthAppFactory.SelfServiceManifest);
            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown variant.");
        }
    }
}
```

`tests/Auth.IntegrationTests/ManifestSignupTests.cs`:

```csharp
using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

public sealed class ManifestSignupTests
{
    private const string Roles = """
        permissions: [reports:read]
        default_roles:
          admin: ["*"]
          user: [reports:read]
        """;

    private static Manifest Parsed(string yaml)
    {
        var result = ManifestParser.Parse(yaml);
        Assert.Null(result.Error);
        return Assert.IsType<Manifest>(result.Manifest);
    }

    [Fact]
    public void A_manifest_without_the_key_is_invite_only()   // criteria 1, 16
    {
        Assert.Equal(SignupMode.InviteOnly, Parsed(Roles).Signup);
    }

    [Theory]
    [InlineData("invite-only", SignupMode.InviteOnly)]
    [InlineData("self-service", SignupMode.SelfService)]
    public void The_key_accepts_invite_only_and_self_service(string value, SignupMode expected)   // criterion 16
    {
        Assert.Equal(expected, Parsed(Roles + "\nsignup: " + value + "\n").Signup);
    }

    [Theory]
    [InlineData("signup:")]
    [InlineData("signup: ~")]
    public void An_empty_value_counts_as_absent(string line)   // Decision of the plan: like `permissions:` with no value
    {
        Assert.Equal(SignupMode.InviteOnly, Parsed(Roles + "\n" + line + "\n").Signup);
    }

    [Theory]
    [InlineData("sometimes")]
    [InlineData("Self-Service")]
    [InlineData("INVITE-ONLY")]
    [InlineData("self_service")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("''")]
    [InlineData("'self-service '")]
    public void Any_other_scalar_makes_the_manifest_invalid_and_names_the_key(string value)   // criterion 16
    {
        var result = ManifestParser.Parse(Roles + "\nsignup: " + value + "\n");

        Assert.Null(result.Manifest);
        Assert.Contains("signup", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[self-service]")]
    [InlineData("{mode: self-service}")]
    public void A_list_or_a_map_makes_the_manifest_invalid(string value)   // criterion 16
    {
        var result = ManifestParser.Parse(Roles + "\nsignup: " + value + "\n");

        Assert.Null(result.Manifest);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void The_key_twice_is_a_duplicate_key()
    {
        var result = ManifestParser.Parse(Roles + "\nsignup: invite-only\nsignup: self-service\n");

        Assert.Null(result.Manifest);
    }

    [Theory]
    [InlineData(SignupMode.InviteOnly)]
    [InlineData(SignupMode.SelfService)]
    public void The_mode_is_kept_by_the_stored_form(SignupMode mode)   // criterion 16
    {
        var manifest = new Manifest(["reports:read"], [new DefaultRole("admin", ["*"])], mode);

        var back = Manifest.FromJson(manifest.ToJson());

        Assert.NotNull(back);
        Assert.Equal(mode, back.Signup);
        Assert.Contains($"\"signup\":\"{SignupModes.NameOf(mode)}\"", manifest.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_stored_by_v0_1_0_reads_as_invite_only()   // criterion 16: the stored JSON had no such member
    {
        const string stored = """{"permissions":["reports:read"],"default_roles":[{"name":"admin","permissions":["*"]},{"name":"user","permissions":["reports:read"]}]}""";

        var manifest = Manifest.FromJson(stored);

        Assert.NotNull(manifest);
        Assert.Equal(SignupMode.InviteOnly, manifest.Signup);
        Assert.Equal(["admin", "user"], manifest.DefaultRoles.Select(r => r.Name));
    }

    [Fact]
    public void A_stored_value_this_version_does_not_know_reads_as_invite_only()
    {
        const string stored = """{"permissions":[],"default_roles":[{"name":"admin","permissions":["*"]}],"signup":"open"}""";

        Assert.Equal(SignupMode.InviteOnly, Manifest.FromJson(stored)!.Signup);
    }

    [Fact]
    public void The_built_in_manifest_is_invite_only_and_its_first_admin_role_is_admin()
    {
        Assert.Equal(SignupMode.InviteOnly, Manifest.BuiltIn.Signup);
        Assert.Equal("admin", Manifest.BuiltIn.FirstAdminRole.Name);
    }

    [Fact]
    public void The_first_admin_role_is_the_first_default_role_that_holds_the_star()   // criterion 10
    {
        var manifest = Parsed("""
            permissions: [docs:read]
            default_roles:
              viewer: [docs:read]
              boss: ["*"]
              admin: ["*"]
            """);

        Assert.Equal("boss", manifest.FirstAdminRole.Name);
    }

    [Fact]
    public void Without_a_star_the_first_admin_role_is_the_first_that_holds_members_manage()   // criterion 10
    {
        var manifest = Parsed("""
            permissions: [docs:read]
            default_roles:
              viewer: [docs:read]
              lead: [members:manage, docs:read]
              deputy: [members:manage, roles:manage]
            """);

        Assert.Equal("lead", manifest.FirstAdminRole.Name);
    }

    [Fact]
    public void A_star_role_later_in_the_file_beats_an_earlier_members_manage_role()   // criterion 10
    {
        var manifest = Parsed("""
            permissions: [docs:read]
            default_roles:
              lead: [members:manage]
              admin: ["*"]
            """);

        Assert.Equal("admin", manifest.FirstAdminRole.Name);
    }

    [Fact]
    public void A_manifest_built_by_hand_without_a_managing_role_has_no_first_admin_role()
    {
        var manifest = new Manifest(["docs:read"], [new DefaultRole("viewer", ["docs:read"])]);

        Assert.Throws<InvalidOperationException>(() => manifest.FirstAdminRole);
    }
}
```

`tests/Auth.IntegrationTests/ManifestSignupActivationTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class ManifestSignupActivationTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
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

    [Fact]
    public async Task A_self_service_manifest_becomes_active_and_is_stored_with_its_mode()   // criterion 16
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup();

        var state = StateOf(factory);

        Assert.False(state.IsDegraded);
        Assert.Equal(SignupMode.SelfService, state.Manifest.Signup);
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().ActiveManifests.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SignupMode.SelfService, Manifest.FromJson(stored.Content)!.Signup);
    }

    [Fact]
    public async Task A_refused_value_keeps_the_last_valid_manifest_and_health_says_degraded()   // criterion 16
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database).WithSignup();
        _ = StateOf(first);

        await using var second = new AuthAppFactory(postgres, keys, database)
            .WithManifest(AuthAppFactory.DefaultManifest + "\nsignup: sometimes\n");
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.Contains("signup", state.DegradedReason, StringComparison.Ordinal);
        Assert.Equal(SignupMode.SelfService, state.Manifest.Signup);   // the last valid manifest stays active
        Assert.Equal("Degraded", await HealthAsync(second));
    }

    [Fact]
    public async Task A_manifest_stored_by_v0_1_0_stays_invite_only_when_the_file_is_missing()   // criterion 16, Review Focus 1
    {
        var database = UniqueDatabase();
        await using var first = new AuthAppFactory(postgres, keys, database);
        _ = StateOf(first);
        // What v0.1.0 stored: the same JSON without the new member.
        const string stored = """{"permissions":["reports:read"],"default_roles":[{"name":"admin","permissions":["*"]}]}""";
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await db.Database.ExecuteSqlAsync($"""UPDATE "ActiveManifests" SET "Content" = {stored}""", TestContext.Current.CancellationToken);
        }

        await using var second = new AuthAppFactory(postgres, keys, database).WithoutManifestFile();
        var state = StateOf(second);

        Assert.True(state.IsDegraded);
        Assert.Equal(SignupMode.InviteOnly, state.Manifest.Signup);
        Assert.Equal(["reports:read"], state.Manifest.Permissions);
    }
}
```

`tests/Auth.IntegrationTests/CompleteSignupSettingsTests.cs`:

```csharp
using Auth.Server.Email;
using Microsoft.Extensions.Configuration;

namespace Auth.IntegrationTests;

public sealed class CompleteSignupSettingsTests
{
    private static Dictionary<string, string?> Valid() => new()
    {
        [MailSettingsLoader.AppNameKey] = "speech-to-mail",
        [MailSettingsLoader.LocaleKey] = "pl",
        [MailSettingsLoader.ResetPasswordUrlKey] = "https://app.example.com/reset",
        [MailSettingsLoader.VerifyEmailUrlKey] = "https://app.example.com/verify",
        [MailSettingsLoader.AcceptInviteUrlKey] = "https://app.example.com/invite",
        [MailSettingsLoader.FromKey] = "no-reply@example.com",
        [MailSettingsLoader.SmtpHostKey] = "smtp.example.com",
        [MailSettingsLoader.SmtpPortKey] = "587",
        [MailSettingsLoader.SmtpSecurityKey] = "starttls",
    };

    private static MailSettings Load(Dictionary<string, string?> values, bool isDevelopment = false) =>
        MailSettingsLoader.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), isDevelopment);

    [Fact]
    public void The_key_is_the_one_of_the_contract()
    {
        Assert.Equal("Auth:App:FrontendUrls:CompleteSignup", MailSettingsLoader.CompleteSignupUrlKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void The_address_is_optional_and_blank_counts_as_unset(string? value)   // criteria 2, 17
    {
        var values = Valid();
        values[MailSettingsLoader.CompleteSignupUrlKey] = value;

        Assert.Null(Load(values).CompleteSignupUrl);
    }

    [Fact]
    public void A_valid_address_loads()
    {
        var values = Valid();
        values[MailSettingsLoader.CompleteSignupUrlKey] = "https://app.example.com/complete-signup";

        Assert.Equal(new Uri("https://app.example.com/complete-signup"), Load(values).CompleteSignupUrl);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/complete-signup")]
    [InlineData("ftp://app.example.com/complete-signup")]
    [InlineData("https://app.example.com/complete-signup#top")]
    [InlineData("https://user:secret@app.example.com/complete-signup")]
    [InlineData("https://app.example.com/complete-signup?token=abc")]
    [InlineData("https://app.example.com/complete-signup?a=1&token=abc")]
    public void A_bad_address_is_refused_naming_the_key_and_never_echoing_the_value(string value)   // criterion 17
    {
        var values = Valid();
        values[MailSettingsLoader.CompleteSignupUrlKey] = value;

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values, isDevelopment: true));

        Assert.Contains(MailSettingsLoader.CompleteSignupUrlKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Outside_Development_the_address_must_be_https()   // criterion 17: the rules of the other addresses
    {
        var values = Valid();
        values[MailSettingsLoader.CompleteSignupUrlKey] = "http://app.example.com/complete-signup";

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values, isDevelopment: false));

        Assert.Contains(MailSettingsLoader.CompleteSignupUrlKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(new Uri("http://app.example.com/complete-signup"), Load(values, isDevelopment: true).CompleteSignupUrl);
    }

    [Fact]
    public void The_other_three_addresses_stay_required()
    {
        var values = Valid();
        values[MailSettingsLoader.CompleteSignupUrlKey] = "https://app.example.com/complete-signup";
        values.Remove(MailSettingsLoader.AcceptInviteUrlKey);

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains(MailSettingsLoader.AcceptInviteUrlKey, ex.Message, StringComparison.Ordinal);
    }
}
```

`tests/Auth.IntegrationTests/SignupGateTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Signup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

public sealed class SignupGateTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static SignupGate GateOf(AuthAppFactory factory) => factory.Services.GetRequiredService<SignupGate>();

    private static async Task<string> HealthAsync(AuthAppFactory factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/auth/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static bool WarnsAboutTheAddress((LogLevel Level, string Category, string Message) entry) =>
        entry.Level == LogLevel.Warning && entry.Message.Contains(MailSettingsLoader.CompleteSignupUrlKey, StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(SignupVariants.Off), MemberType = typeof(SignupVariants))]
    public async Task Sign_up_is_off_unless_the_manifest_says_self_service_and_the_address_is_set(string variant)   // criteria 1, 2
    {
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), variant);

        var gate = GateOf(factory);

        Assert.False(gate.IsOn);
        Assert.Equal("invite-only", gate.ModeName);
    }

    [Fact]
    public async Task With_the_key_and_the_address_sign_up_is_on()   // criterion 1
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup();

        var gate = GateOf(factory);

        Assert.True(gate.IsOn);
        Assert.False(gate.MissingAddress);
        Assert.Equal("self-service", gate.ModeName);
        Assert.Equal("Healthy", await HealthAsync(factory));
    }

    [Fact]
    public async Task Self_service_without_the_address_warns_at_the_start_and_health_says_degraded()   // criterion 17
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), SignupVariants.SelfServiceWithoutAddress)
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        var gate = GateOf(factory);

        Assert.False(gate.IsOn);
        Assert.True(gate.MissingAddress);
        Assert.Equal("Degraded", await HealthAsync(factory));
        var warning = Assert.Single(logs.Entries, WarnsAboutTheAddress);
        Assert.Contains("sign-up is off", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SignupVariants.NoKey)]
    [InlineData(SignupVariants.NoKeyWithAddress)]
    [InlineData(SignupVariants.InviteOnlyWithAddress)]
    public async Task The_other_ways_to_be_off_are_healthy_and_log_no_warning(string variant)   // criterion 2: nothing changes for an instance that does not opt in
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), variant)
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        Assert.Equal("Healthy", await HealthAsync(factory));
        Assert.DoesNotContain(logs.Entries, WarnsAboutTheAddress);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/complete-signup")]
    [InlineData("https://app.example.com/complete-signup#top")]
    [InlineData("https://user:secret@app.example.com/complete-signup")]
    [InlineData("https://app.example.com/complete-signup?token=abc")]
    public async Task A_bad_address_stops_the_start_naming_the_key_and_not_the_value(string value)   // criterion 17
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup()
            .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, value);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(MailSettingsLoader.CompleteSignupUrlKey, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(value, ex.ToString(), StringComparison.Ordinal);
    }
}
```

`tests/Auth.IntegrationTests/SignupErrorCodesTests.cs`:

```csharp
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

public sealed class SignupErrorCodesTests
{
    [Fact]
    public void The_codes_and_their_statuses_are_those_of_the_contract()
    {
        Assert.Equal("signup_disabled", TenancyErrors.SignupDisabled);
        Assert.Equal(StatusCodes.Status403Forbidden, TenancyErrors.StatusOf(TenancyErrors.SignupDisabled));
        Assert.Equal("already_registered", TenancyErrors.AlreadyRegistered);
        Assert.Equal(StatusCodes.Status409Conflict, TenancyErrors.StatusOf(TenancyErrors.AlreadyRegistered));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail.** `"C:/Program Files/dotnet/dotnet.exe" build tests/Auth.IntegrationTests -warnaserror` fails to compile (`SignupMode`, `SignupGate`, `WithSignup`... do not exist). That is the red.

- [ ] **Step 3: The manifest.** Replace `src/Auth.Server/Tenancy/Manifest.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Auth.Server.Tenancy;

/// <summary>A role every new company starts with a copy of (spec 0005 → Manifest).</summary>
public sealed record DefaultRole(string Name, IReadOnlyList<string> Permissions);

/// <summary>Whether a visitor may start a company of their own (spec 0009 → Manifest). Invite-only is the default.</summary>
public enum SignupMode
{
    InviteOnly = 0,
    SelfService = 1,
}

/// <summary>The names of the modes in the manifest, in the stored form and in <c>GET /auth/options</c>.</summary>
public static class SignupModes
{
    public const string InviteOnlyName = "invite-only";
    public const string SelfServiceName = "self-service";

    public static string NameOf(SignupMode mode) => mode switch
    {
        SignupMode.InviteOnly => InviteOnlyName,
        SignupMode.SelfService => SelfServiceName,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown sign-up mode."),
    };

    /// <summary>Exactly one of the two names, in lower case. Anything else is not a mode.</summary>
    public static bool TryParse(string? name, out SignupMode mode)
    {
        if (string.Equals(name, SelfServiceName, StringComparison.Ordinal))
        {
            mode = SignupMode.SelfService;
            return true;
        }

        mode = SignupMode.InviteOnly;
        return string.Equals(name, InviteOnlyName, StringComparison.Ordinal);
    }
}

/// <summary>
/// What a product declares in its <c>auth.yaml</c>: its permissions, its default roles and whether a visitor may start a company
/// (<c>signup</c>). Always valid: it is made by <see cref="ManifestParser"/> or is <see cref="BuiltIn"/>, and what is stored in the
/// database was one of those.
/// </summary>
public sealed class Manifest
{
    private static readonly JsonSerializerOptions StoredJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public Manifest(IReadOnlyList<string> permissions, IReadOnlyList<DefaultRole> defaultRoles, SignupMode signup = SignupMode.InviteOnly)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(defaultRoles);

        Permissions = permissions;
        DefaultRoles = defaultRoles;
        Signup = signup;
        Catalog = new PermissionCatalog(permissions);
    }

    /// <summary>With no valid manifest ever stored: the built-in permissions and one default role, <c>admin</c>, holding everything.</summary>
    public static Manifest BuiltIn { get; } = new([], [new DefaultRole("admin", [PermissionCatalog.All])]);

    /// <summary>The permissions the product declares, in the order of the file.</summary>
    public IReadOnlyList<string> Permissions { get; }

    public IReadOnlyList<DefaultRole> DefaultRoles { get; }

    /// <summary>Whether a visitor may start a company; whether sign-up is on also needs the completion address (<c>SignupGate</c>).</summary>
    public SignupMode Signup { get; }

    public PermissionCatalog Catalog { get; }

    /// <summary>
    /// The role of the first admin of a company a visitor starts (spec 0009 → The first admin's role): the first default role, in the
    /// order of the manifest, that holds <c>*</c>; else the first that holds <c>members:manage</c>. The rule of the manifest (spec 0005)
    /// guarantees that one of them exists.
    /// </summary>
    public DefaultRole FirstAdminRole =>
        DefaultRoles.FirstOrDefault(r => r.Permissions.Contains(PermissionCatalog.All, StringComparer.Ordinal))
        ?? DefaultRoles.FirstOrDefault(r => r.Permissions.Contains(PermissionCatalog.MembersManage, StringComparer.Ordinal))
        ?? throw new InvalidOperationException("The manifest has no default role that holds '*' or 'members:manage'.");

    /// <summary>The form kept in the database.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new StoredManifest(
            [.. Permissions], [.. DefaultRoles.Select(r => new StoredRole(r.Name, [.. r.Permissions]))], SignupModes.NameOf(Signup)),
        StoredJson);

    /// <summary>
    /// The manifest kept in the database; <see langword="null"/> when the text is not one (which it never is, unless edited by hand).
    /// A form stored by an earlier version has no <c>signup</c>, and reads as invite-only.
    /// </summary>
    public static Manifest? FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            var stored = JsonSerializer.Deserialize<StoredManifest>(json, StoredJson);
            if (stored is null)
            {
                return null;
            }

            var signup = SignupModes.TryParse(stored.Signup, out var parsed) ? parsed : SignupMode.InviteOnly;
            return new Manifest(stored.Permissions, [.. stored.DefaultRoles.Select(r => new DefaultRole(r.Name, r.Permissions))], signup);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record StoredRole(string Name, [property: JsonPropertyName("permissions")] string[] Permissions);

    private sealed record StoredManifest(string[] Permissions, StoredRole[] DefaultRoles, string? Signup = null);
}
```

- [ ] **Step 4: The parser.** In `src/Auth.Server/Tenancy/ManifestParser.cs` make two edits.

(a) In `Validate`, replace the last line

```csharp
        return new ManifestParseResult(new Manifest(permissions, defaults), null);
```

with

```csharp
        // A missing value (`signup:` or `signup: ~`) is the same as a missing key. Any other text must be one of the two names exactly.
        if (!SignupModes.TryParse(document.Signup ?? SignupModes.InviteOnlyName, out var signup))
        {
            return Fail($"signup must be '{SignupModes.InviteOnlyName}' or '{SignupModes.SelfServiceName}'");
        }

        return new ManifestParseResult(new Manifest(permissions, defaults, signup), null);
```

(b) In `ManifestDocument`, replace

```csharp
        public List<string?>? Permissions { get; set; }
```

with

```csharp
        public List<string?>? Permissions { get; set; }

        // `invite-only` or `self-service` (spec 0009); a text so that any other value reaches the check above instead of a binding error.
        public string? Signup { get; set; }
```

- [ ] **Step 5: The error codes.** In `src/Auth.Server/Tenancy/Outcome.cs` (`TenancyErrors`): add after `public const string WrongPassword = "wrong_password";`

```csharp
    public const string SignupDisabled = "signup_disabled";
    public const string AlreadyRegistered = "already_registered";
```

and in `StatusOf` change two lines:

```csharp
        Forbidden or PermissionsChanged or PermissionNotHeld or WrongPassword or SignupDisabled => StatusCodes.Status403Forbidden,
```

```csharp
        AlreadyInOrg or AlreadyMember or InvitePending or LastManager or CannotChangeSelf or RoleInUse or RoleNameTaken or AlreadyRegistered
            => StatusCodes.Status409Conflict,
```

- [ ] **Step 6: The completion address.** In `src/Auth.Server/Email/MailSettings.cs`:

(a) in `MailSettings`, after `AcceptInviteUrl`:

```csharp
    /// <summary>
    /// The product's screen that finishes a sign-up (spec 0009): the link of the "complete your sign-up" mail is this URL plus
    /// <c>token</c>. <see langword="null"/> when it is not configured, and sign-up is then off.
    /// </summary>
    public Uri? CompleteSignupUrl { get; init; }
```

(b) in `MailSettingsLoader`, after `AcceptInviteUrlKey`:

```csharp
    public const string CompleteSignupUrlKey = "Auth:App:FrontendUrls:CompleteSignup";
```

(c) in the object initializer of `Load`, after `AcceptInviteUrl = ...`:

```csharp
            CompleteSignupUrl = OptionalFrontendUrl(configuration, CompleteSignupUrlKey, isDevelopment),
```

(d) before `private static string Required(...)`:

```csharp
    /// <summary>Not set (missing, empty or only white space): <see langword="null"/>. Set: the rules of every frontend address.</summary>
    private static Uri? OptionalFrontendUrl(IConfiguration configuration, string key, bool isDevelopment) =>
        string.IsNullOrWhiteSpace(configuration[key]) ? null : FrontendUrl(configuration, key, isDevelopment);
```

- [ ] **Step 7: The gate and its health check.** `src/Auth.Server/Signup/SignupGate.cs`:

```csharp
using Auth.Server.Email;
using Auth.Server.Tenancy;

namespace Auth.Server.Signup;

/// <summary>
/// Whether sign-up is on (spec 0009 → General rules): the manifest says <c>signup: self-service</c> <b>and</b> the completion address is
/// configured. It reads the manifest of the process at the moment of the call, as every request does. A manifest that asks for sign-up
/// without the address leaves it off, and that is a misconfiguration the start warns about and the health check reports.
/// </summary>
public sealed partial class SignupGate(ManifestHolder manifest, MailSettings mail, ILogger<SignupGate> logger)
{
    /// <summary>The manifest says <c>self-service</c>, whether or not the address is there.</summary>
    private bool Wanted => manifest.Current.Signup == SignupMode.SelfService;

    public bool IsOn => Wanted && mail.CompleteSignupUrl is not null;

    /// <summary>The manifest asks for sign-up but the completion address is not set.</summary>
    public bool MissingAddress => Wanted && mail.CompleteSignupUrl is null;

    /// <summary>What <c>GET /auth/options</c> says: <c>self-service</c> only while sign-up is on.</summary>
    public string ModeName => IsOn ? SignupModes.SelfServiceName : SignupModes.InviteOnlyName;

    /// <summary>Called once at start, after the manifest is active.</summary>
    public void WarnIfMissingAddress()
    {
        if (MissingAddress)
        {
            LogMissingAddress(logger, MailSettingsLoader.CompleteSignupUrlKey);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The manifest says signup: self-service, but {Key} is not set: sign-up is off.")]
    private static partial void LogMissingAddress(ILogger logger, string key);
}
```

`src/Auth.Server/Signup/SignupHealthCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Auth.Server.Signup;

/// <summary>
/// <c>Degraded</c> while the manifest asks for self-service sign-up and the completion address is not set (spec 0009 → Configuration).
/// The answer of <c>/auth/health</c> is still <c>200</c>; the reason is in the log, not in a public response.
/// </summary>
public sealed class SignupHealthCheck(SignupGate gate) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(gate.MissingAddress
            ? HealthCheckResult.Degraded("Sign-up is asked for by the manifest but its completion address is not set.")
            : HealthCheckResult.Healthy());
}
```

- [ ] **Step 8: `Program.cs`.** Four edits in `src/Auth.Server/Program.cs`:

(a) `using Auth.Server.Sessions;` → add the line `using Auth.Server.Signup;` after it.

(b) replace `builder.Services.AddHealthChecks().AddCheck<ManifestHealthCheck>("manifest");` with

```csharp
builder.Services.AddHealthChecks().AddCheck<ManifestHealthCheck>("manifest").AddCheck<SignupHealthCheck>("signup");
```

(c) after `builder.Services.AddSingleton<MailComposer>();` add

```csharp
builder.Services.AddSingleton<SignupGate>();
```

(d) after the line `await app.Services.GetRequiredService<ManifestActivator>().ActivateAsync(app.Lifetime.ApplicationStopping);` add

```csharp
// A manifest that asks for self-service sign-up without the completion address leaves it off: say so (spec 0009).
app.Services.GetRequiredService<SignupGate>().WarnIfMissingAddress();
```

- [ ] **Step 9: The test host.** In `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`:

(a) in the constructor, after `_settings[MailSettingsLoader.AcceptInviteUrlKey] = DefaultInviteUrl;` add

```csharp
        // Blank, like the three above: the Development settings file the test host reads would otherwise turn the address on in every
        // test, and a variable of the machine would change what a test sees. A test that wants sign-up on uses WithSignup().
        _settings[MailSettingsLoader.CompleteSignupUrlKey] = "";
```

(b) after `public const string DefaultInviteUrl = "https://app.example.com/invite";` add

```csharp
    public const string DefaultCompleteSignupUrl = "https://app.example.com/complete-signup";

    /// <summary>The default manifest with <c>signup: self-service</c>.</summary>
    public const string SelfServiceManifest = DefaultManifest + "\nsignup: self-service\n";
```

(c) before `/// <summary>Removes the manifest file, as if the product had not shipped one.</summary>` add

```csharp
    /// <summary>Turns sign-up on: the manifest says <c>signup: self-service</c> and the completion address is set. Call it before the host is built.</summary>
    public AuthAppFactory WithSignup()
    {
        WithManifest(SelfServiceManifest);
        return WithSetting(MailSettingsLoader.CompleteSignupUrlKey, DefaultCompleteSignupUrl);
    }

```

- [ ] **Step 10: Run the tests.** `"C:/Program Files/dotnet/dotnet.exe" test tests/Auth.IntegrationTests --filter "FullyQualifiedName~ManifestSignup|FullyQualifiedName~CompleteSignupSettings|FullyQualifiedName~SignupGate|FullyQualifiedName~SignupErrorCodes"` passes; then the whole project (`dotnet test tests/Auth.IntegrationTests`) is green with `-warnaserror` in the build.

- [ ] **Step 11: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(server): manifest key signup, the first admin's role and the optional completion address`.

### Task 2: `GET /auth/options` and the per-IP classification of the four paths

**Files:**
- Create: `src/Auth.Server/Signup/SignupContracts.cs`, `src/Auth.Server/Signup/SignupOptionsEndpoint.cs`, `src/Auth.Server/Api/SignupRoutes.cs`
- Modify: `src/Auth.Server/Program.cs`, `src/Auth.Server/RateLimiting/RatePolicy.cs`
- Modify (tests): `tests/Auth.IntegrationTests/OpenApiTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/Infrastructure/SignupApi.cs`, `SignupOptionsTests.cs`, `RatePolicySignupTests.cs`

**Interfaces:**
- Consumes: Task 1 (`SignupGate.ModeName`, `SignupVariants`, `AuthAppFactory.WithSignup`), `ApiResults.Ok`, `EndpointMetadata.ReadsJson/Produces...`.
- Produces:
  - `Auth.Server.Signup.OptionsResponse(string Signup)`, `SignupRequest(string Email, string CompanyName)`, `SignupPreviewRequest(string Token)`, `SignupPreviewResponse(string Email, string CompanyName)`, `SignupCompleteRequest(string Token, string Password, string CompanyName)` (the bodies, snake_case on the wire; the last four are used by the OpenAPI description of Tasks 5, 7 and 8).
  - `SignupOptionsEndpoint.Path = "/auth/options"` and `Handle(SignupGate)`; `SignupRoutes.MapSignupApi(this IEndpointRouteBuilder)` (later tasks add their routes to it), called from `Program.cs`.
  - `RatePolicies.Classify`: `POST /auth/signup` → `Email`; `POST /auth/signup/preview` and `/auth/signup/complete` → `Invite`.
  - Test side: `SignupApi` (`OptionsPath`, `RequestPath`, `PreviewPath`, `CompletePath`, `Options(client)`, `Request(client, email, companyName)`, `Preview(client, token)`, `Complete(client, token, password, companyName)`, all returning `Task<HttpResponseMessage>`).

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/Infrastructure/SignupApi.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Drives the four endpoints of spec 0009 over HTTP.</summary>
public static class SignupApi
{
    public const string OptionsPath = "/auth/options";
    public const string RequestPath = "/auth/signup";
    public const string PreviewPath = "/auth/signup/preview";
    public const string CompletePath = "/auth/signup/complete";

    public const string Disabled = """{"error":"signup_disabled"}""";
    public const string AlreadyRegistered = """{"error":"already_registered"}""";

    public static Task<HttpResponseMessage> Options(HttpClient client) => client.GetAsync(OptionsPath);

    public static Task<HttpResponseMessage> Request(HttpClient client, string email, string companyName) =>
        client.PostAsJsonAsync(RequestPath, new { email, company_name = companyName });

    public static Task<HttpResponseMessage> Preview(HttpClient client, string token) =>
        client.PostAsJsonAsync(PreviewPath, new { token });

    public static Task<HttpResponseMessage> Complete(HttpClient client, string token, string password, string companyName) =>
        client.PostAsJsonAsync(CompletePath, new { token, password, company_name = companyName });

    /// <summary>The <c>403</c> of a sign-up that is off: the exact body, never stored, no cookie.</summary>
    public static async Task AssertDisabledAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"Expected 403, got {(int)response.StatusCode}: {raw}");
        Assert.Equal(Disabled, raw);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }

    /// <summary>The <c>409 already_registered</c>: the exact body, never stored, no cookie.</summary>
    public static async Task AssertAlreadyRegisteredAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"Expected 409, got {(int)response.StatusCode}: {raw}");
        Assert.Equal(AlreadyRegistered, raw);
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }
}
```

`tests/Auth.IntegrationTests/SignupOptionsTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class SignupOptionsTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    [Theory]
    [MemberData(nameof(SignupVariants.Off), MemberType = typeof(SignupVariants))]
    public async Task Options_say_invite_only_while_sign_up_is_off(string variant)   // criterion 1
    {
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), variant);
        using var client = factory.CreateClient();

        using var response = await SignupApi.Options(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"signup":"invite-only"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Options_say_self_service_with_the_key_and_the_address()   // criterion 1
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup();
        using var client = factory.CreateClient();

        using var response = await SignupApi.Options(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"signup":"self-service"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Options_need_no_token_are_never_stored_set_no_cookie_and_are_json()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup();
        using var client = factory.CreateClient();

        using var response = await SignupApi.Options(client);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        AccountApi.AssertNeverStoredAndNoCookie(response);
    }

    [Fact]
    public async Task Options_follow_the_manifest_the_host_started_with()   // the same host answers the same until it is restarted
    {
        await using var on = new AuthAppFactory(postgres, keys).WithSignup();
        await using var off = new AuthAppFactory(postgres, keys);
        using var onClient = on.CreateClient();
        using var offClient = off.CreateClient();

        using var first = await SignupApi.Options(onClient);
        using var second = await SignupApi.Options(offClient);

        Assert.Contains("self-service", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("invite-only", await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
```

`tests/Auth.IntegrationTests/RatePolicySignupTests.cs`:

```csharp
using Auth.Server.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

public sealed class RatePolicySignupTests
{
    [Theory]
    [InlineData("POST", "/auth/signup", RatePolicy.Email)]
    [InlineData("POST", "/auth/signup/preview", RatePolicy.Invite)]
    [InlineData("POST", "/auth/signup/complete", RatePolicy.Invite)]
    [InlineData("GET", "/auth/options", RatePolicy.General)]
    public void The_four_paths_count_against_the_policies_of_the_contract(string method, string path, RatePolicy expected)   // criterion 18
    {
        Assert.Equal(expected, RatePolicies.Classify(method, new PathString(path)));
    }

    [Theory]
    [InlineData("/AUTH/Signup", RatePolicy.Email)]
    [InlineData("/auth/signup/", RatePolicy.Email)]
    [InlineData("/Auth/SignUp/Preview/", RatePolicy.Invite)]
    [InlineData("/auth/SIGNUP/complete", RatePolicy.Invite)]
    public void The_paths_are_compared_without_regard_to_case_or_a_trailing_slash(string path, RatePolicy expected)
    {
        Assert.Equal(expected, RatePolicies.Classify("POST", new PathString(path)));
    }

    [Theory]
    [InlineData("GET", "/auth/signup")]
    [InlineData("PUT", "/auth/signup/preview")]
    [InlineData("DELETE", "/auth/signup/complete")]
    [InlineData("POST", "/auth/signup/other")]
    [InlineData("POST", "/auth/options")]
    public void Any_other_method_or_path_is_an_ordinary_request(string method, string path)
    {
        Assert.Equal(RatePolicy.General, RatePolicies.Classify(method, new PathString(path)));
    }
}
```

In `tests/Auth.IntegrationTests/OpenApiTests.cs` (lines added):

(a) in `Endpoints`, replace the line `        "GET /auth/me",` (eight leading spaces) with

```csharp
        "GET /auth/me",
        "GET /auth/options",
```

(b) after `    [InlineData("GET /auth/me", "200,401,403,429")]` add

```csharp
    [InlineData("GET /auth/options", "200,429")]
```

(c) in `The_responses_are_described_in_snake_case`, after `        Assert.Equal(["catalog", "roles"], Properties("RolesResponse"));` add

```csharp
        Assert.Equal(["signup"], Properties("OptionsResponse"));
```

- [ ] **Step 2: Run the tests to see them fail.** The new classes do not compile or fail (`404` for `/auth/options`; the classification returns `General` for the three `POST` paths).

- [ ] **Step 3: The bodies.** `src/Auth.Server/Signup/SignupContracts.cs`:

```csharp
namespace Auth.Server.Signup;

// The bodies of the sign-up endpoints (spec 0009 → Contract). Property names become snake_case on the wire. The endpoints read and write
// these bodies by hand, so the types document the contract in OpenAPI; they do not enforce it.

/// <summary>Response of <c>GET /auth/options</c>. A client ignores members it does not know (slice 0010 adds <c>providers</c>).</summary>
public sealed record OptionsResponse(string Signup);

/// <summary>Request of <c>POST /auth/signup</c>.</summary>
public sealed record SignupRequest(string Email, string CompanyName);

/// <summary>Request of <c>POST /auth/signup/preview</c>.</summary>
public sealed record SignupPreviewRequest(string Token);

/// <summary>Response of <c>POST /auth/signup/preview</c>: the address as typed and the company name of the request.</summary>
public sealed record SignupPreviewResponse(string Email, string CompanyName);

/// <summary>Request of <c>POST /auth/signup/complete</c>. The company name is the person's last word; the one of the request is a suggestion.</summary>
public sealed record SignupCompleteRequest(string Token, string Password, string CompanyName);
```

- [ ] **Step 4: The endpoint and the routes.** `src/Auth.Server/Signup/SignupOptionsEndpoint.cs`:

```csharp
using Auth.Server.Api;

namespace Auth.Server.Signup;

/// <summary>
/// <c>GET /auth/options</c> (spec 0009): what a product's screens may offer, so that a product does not repeat its manifest in its
/// frontend. No authentication; the same answer for everyone.
/// </summary>
public static class SignupOptionsEndpoint
{
    public const string Path = "/auth/options";

    public static IResult Handle(SignupGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return ApiResults.Ok(new OptionsResponse(gate.ModeName));
    }
}
```

`src/Auth.Server/Api/SignupRoutes.cs`:

```csharp
using Auth.Server.Signup;

namespace Auth.Server.Api;

public static class SignupRoutes
{
    /// <summary>
    /// The endpoints of spec 0009: the options a product's screens read, and the three steps of a self-service sign-up. Mapped here, with
    /// what each takes and answers, so that the OpenAPI description says it all in one place.
    /// </summary>
    public static IEndpointRouteBuilder MapSignupApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(SignupOptionsEndpoint.Path, SignupOptionsEndpoint.Handle)
            .WithTags("Sign-up")
            .WithDescription(
                "What a product's screens may offer. `signup` is `self-service` while sign-up is on and `invite-only` otherwise. "
                + "A client ignores members it does not know.")
            .Produces<OptionsResponse>();

        return app;
    }
}
```

In `src/Auth.Server/Program.cs`, after `app.MapTenancyApi();` add `app.MapSignupApi();`.

- [ ] **Step 5: The classification.** In `src/Auth.Server/RateLimiting/RatePolicy.cs`, in `Classify`, replace the block

```csharp
        if (trimmed.Equals("/auth/password/forgot", StringComparison.OrdinalIgnoreCase)
```

with

```csharp
        if (trimmed.Equals("/auth/signup", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/password/forgot", StringComparison.OrdinalIgnoreCase)
```

and the block

```csharp
        if (trimmed.Equals("/auth/invites/preview", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/invites/accept", StringComparison.OrdinalIgnoreCase))
```

with

```csharp
        if (trimmed.Equals("/auth/invites/preview", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/invites/accept", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/signup/preview", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/signup/complete", StringComparison.OrdinalIgnoreCase))
```

Also update the summary of `RatePolicy` (the enum's) only if it names the paths; it does not.

Also in `RatePolicy.cs`, in the summary of `Classify`, change "Only a `<c>POST</c>` to the five specific paths is not "general"." to "Only a `<c>POST</c>` to the specific paths is not "general"." (the count was already out of date).

- [ ] **Step 6: Run the tests.** The three new classes and the whole of `OpenApiTests` pass; the whole project is green (`RatePolicyTests` is untouched and still passes).

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(server): GET /auth/options and the per-IP classification of the sign-up paths`.

### Task 3: Pending sign-ups: the table, the migration, `MailKind.Signup`, the statements and the clean-up

**Files:**
- Create: `src/Auth.Infrastructure/Persistence/PendingSignup.cs`, `src/Auth.Server/Signup/PendingSignups.cs`, the migration `AddPendingSignups` (generated: `src/Auth.Infrastructure/Persistence/Migrations/*_AddPendingSignups.cs`, `*_AddPendingSignups.Designer.cs`, and the changed `AuthDbContextModelSnapshot.cs`)
- Modify: `src/Auth.Infrastructure/Persistence/AuthDbContext.cs`, `src/Auth.Infrastructure/Persistence/MailKind.cs`, `src/Auth.Server/Email/EmailPruner.cs`, `src/Auth.Server/Email/EmailPruningService.cs`
- Create (tests): `tests/Auth.IntegrationTests/Infrastructure/PendingSignupSeed.cs`, `PendingSignupTableTests.cs`, `PendingSignupsTests.cs`, `MailKindTests.cs`, `PendingSignupPruningTests.cs`, `UpgradeFromV010Tests.cs`

**Interfaces:**
- Consumes: Task 1 (`SignupMode`, `Manifest.FromJson`), `EmailTokens.NewToken/HashOf`, `StorableTime.Now`, `MailTestBase` (`InDbAsync`, `Clock`), the pruning pattern of `EmailPruner`.
- Produces:
  - `Auth.Infrastructure.Persistence.PendingSignup` (`NormalizedEmail` key, `Email`, `CompanyName`, `RequestedAt`, `TokenHash?`, `TokenExpiresAt?`, `ExpiresAt`) and `AuthDbContext.PendingSignups`.
  - `MailKind.Signup = 4`.
  - `Auth.Server.Signup.PendingSignups` (static): `TimeSpan Lifetime` (24 hours); `Task UpsertAsync(AuthDbContext db, string normalizedEmail, string email, string companyName, DateTimeOffset now, CancellationToken ct)`; `Task<string> IssueAsync(AuthDbContext db, string normalizedEmail, DateTimeOffset now, CancellationToken ct)` (the token in clear); `Task<PendingSignup?> FindUsableAsync(AuthDbContext db, byte[] tokenHash, DateTimeOffset now, CancellationToken ct)`; `Task<PendingSignup?> LockUsableAsync(AuthDbContext db, string normalizedEmail, DateTimeOffset now, CancellationToken ct)` (row lock, by address); `Task<PendingSignup?> ConsumeAsync(AuthDbContext db, byte[] tokenHash, DateTimeOffset now, CancellationToken ct)` (one `DELETE ... RETURNING`); `Task DeleteAsync(AuthDbContext db, string normalizedEmail, CancellationToken ct)`.
  - `EmailPruner.PrunePendingSignupsAsync(CancellationToken)` (returns the number removed), run by the hourly `EmailPruningService`.
  - Test side: `PendingSignupSeed.IssueAsync(IServiceProvider services, string email, string companyName)` returns the link token of a pending sign-up that exists as if its mail had been sent.

**Decision the plan takes here** (the spec says a pending sign-up holds "the time it expires"): the row has **two** times. `ExpiresAt` is when the row goes (24 hours after the request, and 24 hours after the mail once there is one, never earlier than a link it holds); `TokenExpiresAt` is when the *current link* stops working (24 hours after the mail that carried it). With one time, a second request would either extend the life of an older link past its 24 hours or, if it did not, let the row expire before the new mail was sent. `TokenExpiresAt <= ExpiresAt` always (as the spec's Amendment 2 says). Everything the contract says (a link older than 24 hours is `invalid_token`; a failed sending leaves the previous link valid) follows from the pair.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/Infrastructure/PendingSignupSeed.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Auth.Server.Signup;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Makes a pending sign-up straight in the database, with its link already issued, for tests that are not about the mail.</summary>
public static class PendingSignupSeed
{
    /// <summary>The pending sign-up of the address as the dispatcher leaves it after sending; returns the token in clear.</summary>
    public static async Task<string> IssueAsync(IServiceProvider services, string email, string companyName)
    {
        ArgumentNullException.ThrowIfNull(services);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var normalized = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>().NormalizeEmail(email);
        var now = StorableTime.Now(services.GetRequiredService<TimeProvider>());
        var cancellation = TestContext.Current.CancellationToken;
        await PendingSignups.UpsertAsync(db, normalized, email, companyName, now, cancellation);
        return await PendingSignups.IssueAsync(db, normalized, now, cancellation);
    }
}
```

`tests/Auth.IntegrationTests/PendingSignupTableTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class PendingSignupTableTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private async Task<List<string>> StringsAsync(FormattableString sql)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Database.SqlQuery<string>(sql).ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_migration_makes_the_table()
    {
        var columns = await StringsAsync(
            $"""
            SELECT column_name || ':' || data_type || ':' || coalesce(character_maximum_length::text, '') || ':' || is_nullable AS "Value"
            FROM information_schema.columns WHERE table_name = 'PendingSignups' ORDER BY column_name
            """);

        Assert.Equal(
            [
                "CompanyName:character varying:100:NO",
                "Email:character varying:256:NO",
                "ExpiresAt:timestamp with time zone::NO",
                "NormalizedEmail:character varying:256:NO",
                "RequestedAt:timestamp with time zone::NO",
                "TokenExpiresAt:timestamp with time zone::YES",
                "TokenHash:bytea::YES",
            ],
            columns);
    }

    [Fact]
    public async Task The_table_has_the_indexes_the_clean_up_and_the_link_use()
    {
        var indexes = await StringsAsync(
            $"""SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'PendingSignups' ORDER BY indexname""");

        Assert.Equal(["IX_PendingSignups_ExpiresAt", "IX_PendingSignups_TokenHash", "PK_PendingSignups"], indexes);
    }

    [Fact]
    public async Task The_hash_of_a_link_is_unique()
    {
        var unique = await StringsAsync(
            $"""SELECT indexdef AS "Value" FROM pg_indexes WHERE indexname = 'IX_PendingSignups_TokenHash'""");

        Assert.Contains("UNIQUE", Assert.Single(unique), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_table_has_no_foreign_key_so_that_a_pending_sign_up_is_not_an_account()   // Decision 7
    {
        var keysOfTable = await StringsAsync(
            $"""SELECT conname AS "Value" FROM pg_constraint WHERE conrelid = '"PendingSignups"'::regclass AND contype = 'f'""");

        Assert.Empty(keysOfTable);
    }
}
```

`tests/Auth.IntegrationTests/MailKindTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;

namespace Auth.IntegrationTests;

public sealed class MailKindTests
{
    [Fact]
    public void The_values_of_the_kinds_are_stored_and_never_renumbered()
    {
        Assert.Equal((short)1, (short)MailKind.PasswordReset);
        Assert.Equal((short)2, (short)MailKind.EmailVerification);
        Assert.Equal((short)3, (short)MailKind.Invitation);
        Assert.Equal((short)4, (short)MailKind.Signup);
    }
}
```

`tests/Auth.IntegrationTests/PendingSignupsTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Signup;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class PendingSignupsTests : MailTestBase
{
    private const string Normalized = "ANNA@FIRMA.TEST";
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    public PendingSignupsTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The loop of the pruning service waits on the fake clock and Clock.Advance fires its timer: a background pass would delete the
        // rows these tests count after moving the clock by a day.
        Factory.WithoutHostedService<EmailPruningService>();
    }

    private Task<int> UpsertAsync(string typed = "Anna@Firma.test", string company = "Firma", string normalized = Normalized)
    {
        var now = StorableTime.Now(Clock);
        return InDbAsync(async db =>
        {
            await PendingSignups.UpsertAsync(db, normalized, typed, company, now, TestContext.Current.CancellationToken);
            return 0;
        });
    }

    private Task<string> IssueAsync(string normalized = Normalized)
    {
        var now = StorableTime.Now(Clock);
        return InDbAsync(db => PendingSignups.IssueAsync(db, normalized, now, TestContext.Current.CancellationToken));
    }

    private Task<PendingSignup> RowAsync() =>
        InDbAsync(db => db.PendingSignups.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Upsert_stores_the_typed_address_the_company_and_24_hours()
    {
        await UpsertAsync();

        var row = await RowAsync();

        Assert.Equal(Normalized, row.NormalizedEmail);
        Assert.Equal("Anna@Firma.test", row.Email);
        Assert.Equal("Firma", row.CompanyName);
        Assert.Equal(StorableTime.Now(Clock), row.RequestedAt);
        Assert.Equal(StorableTime.Now(Clock) + Day, row.ExpiresAt);
        Assert.Null(row.TokenHash);
        Assert.Null(row.TokenExpiresAt);
    }

    [Fact]
    public async Task Upsert_for_the_same_address_replaces_the_names_and_the_time_and_keeps_the_link()
    {
        await UpsertAsync();
        var token = await IssueAsync();
        var issued = await RowAsync();
        Clock.Advance(TimeSpan.FromHours(5));

        await UpsertAsync(typed: "ANNA@firma.test", company: "Second");

        var row = await RowAsync();
        Assert.Equal("ANNA@firma.test", row.Email);
        Assert.Equal("Second", row.CompanyName);
        Assert.Equal(StorableTime.Now(Clock) + Day, row.ExpiresAt);
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);            // a failed sending of the new mail leaves the old link valid
        Assert.Equal(issued.TokenExpiresAt, row.TokenExpiresAt);           // and it does not live a minute longer than 24 hours from its own mail
    }

    [Fact]
    public async Task Issue_stores_only_the_hash_and_24_hours_from_now_for_the_link_and_the_row()
    {
        await UpsertAsync();
        Clock.Advance(TimeSpan.FromHours(1));

        var token = await IssueAsync();

        var row = await RowAsync();
        Assert.Equal(43, token.Length);
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);
        Assert.Equal(StorableTime.Now(Clock) + Day, row.TokenExpiresAt);
        Assert.Equal(StorableTime.Now(Clock) + Day, row.ExpiresAt);
    }

    [Fact]
    public async Task Issue_replaces_the_earlier_link()
    {
        await UpsertAsync();
        var first = await IssueAsync();
        var second = await IssueAsync();

        Assert.NotEqual(first, second);
        Assert.Equal(EmailTokens.HashOf(second), (await RowAsync()).TokenHash);
        Assert.Null(await InDbAsync(db => PendingSignups.FindUsableAsync(db, EmailTokens.HashOf(first), StorableTime.Now(Clock), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Consume_uses_the_link_up_once_and_removes_the_row()
    {
        await UpsertAsync();
        var token = await IssueAsync();
        var hash = EmailTokens.HashOf(token);

        var first = await InDbAsync(db => PendingSignups.ConsumeAsync(db, hash, StorableTime.Now(Clock), TestContext.Current.CancellationToken));
        var second = await InDbAsync(db => PendingSignups.ConsumeAsync(db, hash, StorableTime.Now(Clock), TestContext.Current.CancellationToken));

        Assert.NotNull(first);
        Assert.Equal("Anna@Firma.test", first.Email);
        Assert.Null(second);
        Assert.Equal(0, await InDbAsync(db => db.PendingSignups.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_link_works_until_exactly_24_hours_after_it_was_issued()
    {
        await UpsertAsync();
        var token = await IssueAsync();
        var hash = EmailTokens.HashOf(token);

        Clock.Advance(Day - TimeSpan.FromSeconds(1));
        Assert.NotNull(await InDbAsync(db => PendingSignups.FindUsableAsync(db, hash, StorableTime.Now(Clock), TestContext.Current.CancellationToken)));

        Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await InDbAsync(db => PendingSignups.FindUsableAsync(db, hash, StorableTime.Now(Clock), TestContext.Current.CancellationToken)));
        Assert.Null(await InDbAsync(db => PendingSignups.ConsumeAsync(db, hash, StorableTime.Now(Clock), TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.PendingSignups.CountAsync(TestContext.Current.CancellationToken)));   // not consumed: the clean-up removes it
    }

    [Fact]
    public async Task LockUsable_finds_a_live_row_by_address_only()
    {
        await UpsertAsync();

        var live = await InDbAsync(db => PendingSignups.LockUsableAsync(db, Normalized, StorableTime.Now(Clock), TestContext.Current.CancellationToken));
        var other = await InDbAsync(db => PendingSignups.LockUsableAsync(db, "OTHER@FIRMA.TEST", StorableTime.Now(Clock), TestContext.Current.CancellationToken));
        Clock.Advance(Day);
        var expired = await InDbAsync(db => PendingSignups.LockUsableAsync(db, Normalized, StorableTime.Now(Clock), TestContext.Current.CancellationToken));

        Assert.NotNull(live);
        Assert.Null(other);
        Assert.Null(expired);
    }

    [Fact]
    public async Task Delete_removes_the_row_of_the_address_and_no_other()
    {
        await UpsertAsync();
        await UpsertAsync(normalized: "OTHER@FIRMA.TEST", typed: "other@firma.test");

        await InDbAsync(async db =>
        {
            await PendingSignups.DeleteAsync(db, Normalized, TestContext.Current.CancellationToken);
            return 0;
        });

        var left = await InDbAsync(db => db.PendingSignups.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal("OTHER@FIRMA.TEST", left.NormalizedEmail);
    }
}
```

`tests/Auth.IntegrationTests/PendingSignupPruningTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Signup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class PendingSignupPruningTests : MailTestBase
{
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    public PendingSignupPruningTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The loop of the service waits on the fake clock and Clock.Advance fires its timer: a background pass would race the explicit pass.
        Factory.WithoutHostedService<EmailPruningService>();
    }

    private Task<int> PruneAsync() =>
        Factory.Services.GetRequiredService<EmailPruner>().PrunePendingSignupsAsync(TestContext.Current.CancellationToken);

    private Task<int> CountAsync() => InDbAsync(db => db.PendingSignups.CountAsync(TestContext.Current.CancellationToken));

    private Task AddAsync(string normalized) =>
        InDbAsync(async db =>
        {
            await PendingSignups.UpsertAsync(db, normalized, normalized.ToLowerInvariant(), "Firma", StorableTime.Now(Clock), TestContext.Current.CancellationToken);
            return 0;
        });

    [Fact]
    public async Task Pruning_removes_pending_sign_ups_whose_time_has_passed_and_keeps_the_others()   // criterion 13
    {
        await AddAsync("OLD@FIRMA.TEST");
        Clock.Advance(TimeSpan.FromHours(1));
        await AddAsync("NEWER@FIRMA.TEST");
        Clock.Advance(Day - TimeSpan.FromHours(1));   // the first is exactly 24 hours old

        Assert.Equal(1, await PruneAsync());

        var left = await InDbAsync(db => db.PendingSignups.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal("NEWER@FIRMA.TEST", left.NormalizedEmail);
        Assert.Equal(0, await PruneAsync());
        Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await PruneAsync());
        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task A_row_with_a_live_link_survives_a_pass()
    {
        await AddAsync("ANNA@FIRMA.TEST");
        Clock.Advance(TimeSpan.FromHours(20));
        await InDbAsync(db => PendingSignups.IssueAsync(db, "ANNA@FIRMA.TEST", StorableTime.Now(Clock), TestContext.Current.CancellationToken));   // the mail was sent at 20 hours
        Clock.Advance(TimeSpan.FromHours(10));   // 30 hours after the request, 10 after the mail

        Assert.Equal(0, await PruneAsync());
        Assert.Equal(1, await CountAsync());
    }

    [Fact]
    public async Task Pruning_leaves_the_queue_the_limits_and_the_accounts_alone()
    {
        await AddAsync("ANNA@FIRMA.TEST");
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            db.MailRequests.Add(new MailRequest { Kind = MailKind.Signup, NormalizedEmail = "ANNA@FIRMA.TEST", RequestedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });
        Clock.Advance(Day);

        await PruneAsync();

        Assert.Equal(1, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Factory.SeedEmail, TestContext.Current.CancellationToken)));
    }
}

public sealed class PendingSignupPruningServiceTests(PostgresFixture postgres, KeyMaterialFixture keys) : MailTestBase(postgres, keys)
{
    [Fact]
    public async Task The_hourly_pass_of_the_service_removes_expired_pending_sign_ups()   // criterion 13
    {
        await InDbAsync(async db =>
        {
            await PendingSignups.UpsertAsync(db, "ANNA@FIRMA.TEST", "anna@firma.test", "Firma", StorableTime.Now(Clock), TestContext.Current.CancellationToken);
            return 0;
        });

        Clock.Advance(TimeSpan.FromHours(24) + EmailPruningService.Interval);   // the timer of the service fires on the fake clock

        await Poll.UntilAsync(
            () => InDbAsync(db => db.PendingSignups.CountAsync(TestContext.Current.CancellationToken)).GetAwaiter().GetResult() == 0,
            "the pruning service to remove the pending sign-up");
    }
}
```

`tests/Auth.IntegrationTests/UpgradeFromV010Tests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Auth.IntegrationTests;

/// <summary>Toviq runs v0.1.0: its database, made by the migrations up to <c>AddAuditEvents</c>, takes the new one and keeps what it holds.</summary>
public sealed class UpgradeFromV010Tests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string LastMigrationOfV010 = "20260221111842_AddAuditEvents";
    private const string StoredManifestOfV010 = """{"permissions":["a:b"],"default_roles":[{"name":"admin","permissions":["*"]}]}""";

    [Fact]
    public async Task A_database_of_v0_1_0_gets_the_table_keeps_its_rows_and_reads_its_manifest_as_invite_only()   // Review Focus 1
    {
        await using var factory = new AuthAppFactory(postgres, keys);   // never built: only its connection string is used
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(factory.ConnectionString).UseOpenIddict().Options;
        var cancellation = TestContext.Current.CancellationToken;

        await using (var old = new AuthDbContext(options))
        {
            await old.GetService<IMigrator>().MigrateAsync(LastMigrationOfV010, cancellation);
            Assert.Equal(0, await old.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM pg_tables WHERE tablename = 'PendingSignups'""").SingleAsync(cancellation));
            await old.Database.ExecuteSqlAsync(
                $"""INSERT INTO "Companies" ("Id", "Name", "CreatedAt") VALUES ({Guid.NewGuid()}, 'Toviq', now())""", cancellation);
            await old.Database.ExecuteSqlAsync(
                $"""INSERT INTO "ActiveManifests" ("Id", "Content", "StoredAt") VALUES (1, {StoredManifestOfV010}, now())""", cancellation);
        }

        await using (var upgraded = new AuthDbContext(options))
        {
            await upgraded.Database.MigrateAsync(cancellation);

            Assert.Equal(0, await upgraded.PendingSignups.CountAsync(cancellation));
            Assert.Equal("Toviq", await upgraded.Companies.Select(c => c.Name).SingleAsync(cancellation));
            var stored = await upgraded.ActiveManifests.SingleAsync(cancellation);
            Assert.Equal(SignupMode.InviteOnly, Manifest.FromJson(stored.Content)!.Signup);
        }
    }
}
```

- [ ] **Step 2: Run the tests to see them fail.** The project does not compile (`PendingSignup`, `PendingSignups`, `MailKind.Signup`, `PrunePendingSignupsAsync` do not exist).

- [ ] **Step 3: The entity and the kind.** `src/Auth.Infrastructure/Persistence/PendingSignup.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>
/// A sign-up that has been asked for and not finished (spec 0009 → Pending sign-ups). It is not an account: login, <c>forgot password</c> and
/// invitations do not see it, and it has no foreign key. There is one per normalised address; a new request replaces it.
/// <see cref="ExpiresAt"/> is when the row goes (24 hours after the request, and after the mail that carries its link);
/// <see cref="TokenExpiresAt"/> is when the current link stops working (24 hours after that mail), and is never later than
/// <see cref="ExpiresAt"/>.
/// </summary>
public sealed class PendingSignup
{
    /// <summary>The address as the account lookup normalises it: the key.</summary>
    public required string NormalizedEmail { get; init; }

    /// <summary>The address as the visitor typed it: the account is created with it, and the mail goes to it.</summary>
    public required string Email { get; set; }

    public required string CompanyName { get; set; }

    public required DateTimeOffset RequestedAt { get; set; }

    /// <summary>SHA-256 of the current link token; <see langword="null"/> until the first mail has been sent.</summary>
    public byte[]? TokenHash { get; set; }

    public DateTimeOffset? TokenExpiresAt { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }
}
```

In `src/Auth.Infrastructure/Persistence/MailKind.cs` add `Signup = 4,` after `Invitation = 3,` and extend the summary: `... Never renumber them.` stays.

- [ ] **Step 4: The context.** In `src/Auth.Infrastructure/Persistence/AuthDbContext.cs`:

(a) after `public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();` add

```csharp

    public DbSet<PendingSignup> PendingSignups => Set<PendingSignup>();
```

(b) after the block

```csharp
        builder.Entity<ActiveManifest>(manifest =>
        {
            manifest.HasKey(m => m.Id);
            manifest.Property(m => m.Id).ValueGeneratedNever();
        });
```

add

```csharp

        // Spec 0009. Not an account: no foreign key to one.
        builder.Entity<PendingSignup>(pending =>
        {
            pending.HasKey(p => p.NormalizedEmail);
            pending.Property(p => p.NormalizedEmail).HasMaxLength(256);
            pending.Property(p => p.Email).HasMaxLength(256);
            pending.Property(p => p.CompanyName).HasMaxLength(100);
            // A link is looked up by its hash when it is used; rows without a link yet (null) do not collide.
            pending.HasIndex(p => p.TokenHash).IsUnique();
            // The clean-up selects by expiry.
            pending.HasIndex(p => p.ExpiresAt);
        });
```

- [ ] **Step 5: The migration.** From the repository root:

```bash
"C:/Program Files/dotnet/dotnet.exe" tool restore
"C:/Program Files/dotnet/dotnet.exe" ef migrations add AddPendingSignups -p src/Auth.Infrastructure -s src/Auth.Server
```

Check that the generated `Up` creates the table `PendingSignups` with `NormalizedEmail` `character varying(256)` (the key), `Email` `character varying(256)`, `CompanyName` `character varying(100)`, `RequestedAt`, `TokenExpiresAt` (nullable) and `ExpiresAt` of `timestamp with time zone`, `TokenHash` `bytea` (nullable), the primary key `PK_PendingSignups` and the indexes `IX_PendingSignups_ExpiresAt` and `IX_PendingSignups_TokenHash` (unique), and nothing else; that `Down` drops the table; and that the snapshot gained the entity. Do not rename the files and do not name the migration's id anywhere (the orchestrator renames it).

- [ ] **Step 6: The statements.** `src/Auth.Server/Signup/PendingSignups.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Signup;

/// <summary>
/// The statements of pending sign-ups (spec 0009). Each runs in the caller's context, so the caller's transaction decides whether it
/// happened. The link tokens work as those of spec 0004 and of invitations: 32 random bytes as base64url, only the SHA-256 stored,
/// single use.
/// </summary>
public static class PendingSignups
{
    /// <summary>A pending sign-up lives this long from its request, and a link from its mail. A constant, as the lifetimes of spec 0004.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// Makes the pending sign-up of the address, or replaces the one that is there (its typed address, its company name and its time); one
    /// statement, so two requests at once leave one row. The current link and its time are left as they are: until the new mail has been
    /// sent, the earlier link is still the one that works.
    /// </summary>
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
                "Email" = EXCLUDED."Email",
                "CompanyName" = EXCLUDED."CompanyName",
                "RequestedAt" = EXCLUDED."RequestedAt",
                "ExpiresAt" = EXCLUDED."ExpiresAt"
            """,
            cancellationToken);
    }

    /// <summary>
    /// Gives the pending sign-up a new link token, replacing the one it had, and returns it in clear; the caller puts it into a mail and
    /// nowhere else. The 24 hours of the link start now, and the row lives at least as long. The row should be locked by the caller (the dispatcher does).
    /// </summary>
    public static async Task<string> IssueAsync(AuthDbContext db, string normalizedEmail, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        var token = EmailTokens.NewToken();
        var hash = EmailTokens.HashOf(token);
        var expiresAt = now + Lifetime;
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE "PendingSignups"
            SET "TokenHash" = {hash}, "TokenExpiresAt" = {expiresAt}, "ExpiresAt" = GREATEST("ExpiresAt", {expiresAt})
            WHERE "NormalizedEmail" = {normalizedEmail}
            """,
            cancellationToken);
        return token;
    }

    /// <summary>The pending sign-up whose current link this is, if the link is still good; nothing is changed.</summary>
    public static Task<PendingSignup?> FindUsableAsync(AuthDbContext db, byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokenHash);

        return db.PendingSignups.AsNoTracking()
            .Where(p => p.TokenHash == tokenHash && p.TokenExpiresAt > now)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The pending sign-up of the address if it has not expired, with its row locked until the transaction ends: whoever completes it,
    /// or replaces its link, waits for the mail that is being sent. <see langword="null"/> when there is none.
    /// </summary>
    public static async Task<PendingSignup?> LockUsableAsync(
        AuthDbContext db, string normalizedEmail, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        // ToListAsync, not SingleOrDefaultAsync: EF must send the statement as it is, not wrapped in a subquery.
        var rows = await db.PendingSignups
            .FromSql($"""SELECT * FROM "PendingSignups" WHERE "NormalizedEmail" = {normalizedEmail} AND "ExpiresAt" > {now} FOR UPDATE""")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return rows.Count == 1 ? rows[0] : null;
    }

    /// <summary>
    /// Uses a link up: removes the pending sign-up whose current link this is, if the link is still good, and returns it. One statement,
    /// so of parallel calls with one token exactly one gets the row.
    /// </summary>
    public static async Task<PendingSignup?> ConsumeAsync(AuthDbContext db, byte[] tokenHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokenHash);

        var removed = await db.PendingSignups
            .FromSql($"""DELETE FROM "PendingSignups" WHERE "TokenHash" = {tokenHash} AND "TokenExpiresAt" > {now} RETURNING *""")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return removed.Count == 1 ? removed[0] : null;
    }

    public static Task DeleteAsync(AuthDbContext db, string normalizedEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);

        return db.Database.ExecuteSqlAsync($"""DELETE FROM "PendingSignups" WHERE "NormalizedEmail" = {normalizedEmail}""", cancellationToken);
    }
}
```

- [ ] **Step 7: The clean-up.** In `src/Auth.Server/Email/EmailPruner.cs` insert, before the line `    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Tokens} link token and {Limits} mail limit entries.")]`:

```csharp
    /// <summary>
    /// Removes the pending sign-ups whose time has passed (spec 0009): an unused link leaves nothing behind. A queued mail for a removed
    /// one is dropped by the dispatcher. Returns the number removed.
    /// </summary>
    public async Task<int> PrunePendingSignupsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var pending = await db.PendingSignups.Where(p => p.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);

        LogPrunedPendingSignups(logger, pending);
        return pending;
    }

```

and at the end of the class, after `private static partial void LogPrunedInvites(ILogger logger, int invites);` add

```csharp

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Pending} expired pending sign-ups.")]
    private static partial void LogPrunedPendingSignups(ILogger logger, int pending);
```

In `src/Auth.Server/Email/EmailPruningService.cs`: after `await pruner.PruneInvitesAsync(stoppingToken);` add `await pruner.PrunePendingSignupsAsync(stoppingToken);`; change the summary sentence "the link tokens, the mail limits and\n/// the invitations." to "the link tokens, the mail limits, the\n/// invitations and the pending sign-ups (spec 0009)." and the message of `LogFailed` to `"Pruning of link tokens, mail limits, invitations and pending sign-ups failed; it will run again at the next interval."`.

- [ ] **Step 8: Run the tests.** `"C:/Program Files/dotnet/dotnet.exe" test tests/Auth.IntegrationTests --filter "FullyQualifiedName~PendingSignup|FullyQualifiedName~MailKindTests|FullyQualifiedName~UpgradeFromV010"` passes; then the whole project (every older test still passes: the model change adds a table and nothing else).

- [ ] **Step 9: Hand back** — uncommitted. Files: everything listed under Files (the generated migration files included, not renamed). Proposed subject: `feat(server): pending sign-ups: the table, the statements and the hourly clean-up`.

---

## Day 2 — Tuesday 31.03: the request and the mails

When the day is done, a visitor can ask for a sign-up, the answer is the same `202` for every address, the pending sign-up is stored, and the queue sends the right mail, in Polish or English: a link for a new address, "you already have an account" for an address with one. Three tasks; every task leaves `dotnet build -warnaserror` and the tests of the files it touches green.

### Task 4: The two mails, in Polish and English, HTML and text

**Files:**
- Modify: `src/Auth.Server/Email/MailTexts.cs`, `src/Auth.Server/Email/MailComposer.cs` (replaced as a whole)
- Create (tests): `tests/Auth.IntegrationTests/SignupMailComposerTests.cs`

**Interfaces:**
- Consumes: Task 1 (`MailSettings.CompleteSignupUrl`), Task 3 (`MailKind.Signup`).
- Produces:
  - `MailTexts.For(locale, MailKind.Signup)` (a `MailText`; `Intro` holds `{0}` for the application name and `{1}` for the company) and `MailTexts.AccountExists(locale)` returning the new record `AccountExistsText(Subject, Greeting, Intro, SignInHint, Ignore)` (`{0}` is the application name in `Subject`, `Intro` and `SignInHint`).
  - `MailComposer.Compose(MailKind.Signup, recipient, token, companyName)` — the "complete your sign-up" mail with `<CompleteSignup>?token=<token>`; it throws `ArgumentException` without a company name and `InvalidOperationException` when the address is not configured. `MailComposer.ComposeAccountExists(recipient)` — the "you already have an account" mail: no link, no token. An unknown kind is `ArgumentOutOfRangeException` (it no longer falls through to the invitation address).

**Texts the plan chooses** (the spec fixes the content, not the words): the subject of the complete mail starts `Complete your sign-up` / `Dokończ rejestrację`, and that of the other `You already have an account` / `Masz już konto`; neither subject holds the company name (it is typed by a visitor), the body does, encoded in the HTML part. `scripts/e2e-signup.sh` and the Playwright helper find the mails by these starts.

- [ ] **Step 1: Write the failing tests.** `tests/Auth.IntegrationTests/SignupMailComposerTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

public sealed class SignupMailComposerTests
{
    private const string Token = "abcDEF123_-abcDEF123_-abcDEF123_-abcDEF123_";
    private const string Link = "https://app.example.com/complete-signup?token=" + Token;

    private static MailComposer Composer(string locale = "en", string appName = "speech-to-mail", string? complete = "https://app.example.com/complete-signup") =>
        new(new MailSettings
        {
            AppName = appName,
            Locale = locale,
            ResetPasswordUrl = new Uri("https://app.example.com/reset"),
            VerifyEmailUrl = new Uri("https://app.example.com/verify"),
            AcceptInviteUrl = new Uri("https://app.example.com/invite"),
            CompleteSignupUrl = complete is null ? null : new Uri(complete),
            FromAddress = "no-reply@example.com",
            Smtp = new SmtpSettings { Host = "smtp.invalid", Port = 587, Security = SmtpSecurity.StartTls },
        });

    [Fact]
    public void The_complete_mail_in_english_names_the_application_the_company_the_link_and_the_lifetime()   // criteria 3, 21
    {
        var mail = Composer().Compose(MailKind.Signup, "anna@firma.test", Token, "Firma");

        Assert.Equal("anna@firma.test", mail.To);
        Assert.Equal("Complete your sign-up to speech-to-mail", mail.Subject);
        Assert.Contains("speech-to-mail", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("Firma", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(Link, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("valid for 24 hours", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("ignore this message", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains($"href=\"{Link}\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Firma", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("valid for 24 hours", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\">", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("{", mail.Subject + mail.TextBody + mail.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void The_complete_mail_in_polish_keeps_its_letters()   // criterion 21
    {
        var mail = Composer("pl").Compose(MailKind.Signup, "anna@firma.test", Token, "Żółw i Spółka");

        Assert.Equal("Dokończ rejestrację w speech-to-mail", mail.Subject);
        Assert.Contains("Żółw i Spółka", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("Żółw i Spółka", mail.HtmlBody, StringComparison.Ordinal);   // letters stay letters, not entities
        Assert.Contains(Link, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("ważny przez 24 godziny", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("zignoruj tę wiadomość", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"pl\">", mail.HtmlBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public void The_html_and_the_text_hold_the_same_link(string locale)   // criterion 21
    {
        var mail = Composer(locale).Compose(MailKind.Signup, "anna@firma.test", Token, "Firma");

        var inText = Regex.Matches(mail.TextBody, @"https://app\.example\.com/complete-signup\?token=[A-Za-z0-9_-]{43}");
        var inHtml = Regex.Matches(mail.HtmlBody, @"https://app\.example\.com/complete-signup\?token=[A-Za-z0-9_-]{43}");
        Assert.Single(inText);
        Assert.NotEmpty(inHtml);
        Assert.All(inHtml, link => Assert.Equal(inText[0].Value, link.Value));
    }

    [Fact]
    public void The_company_name_is_encoded_in_the_html_and_is_not_in_the_subject()   // Review Focus 4: a visitor typed it
    {
        var mail = Composer().Compose(MailKind.Signup, "anna@firma.test", Token, "Tom & <Jerry>");

        Assert.Contains("Tom & <Jerry>", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("Tom &amp; &lt;Jerry&gt;", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("<Jerry>", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Jerry", mail.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void A_company_name_with_braces_is_not_a_format_string()
    {
        var mail = Composer().Compose(MailKind.Signup, "anna@firma.test", Token, "{0} {1} {2}");

        Assert.Contains("{0} {1} {2}", mail.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void The_account_exists_mail_in_english_has_no_link_and_no_token()   // criterion 4
    {
        var mail = Composer().ComposeAccountExists("anna@firma.test");

        Assert.Equal("anna@firma.test", mail.To);
        Assert.Equal("You already have an account on speech-to-mail", mail.Subject);
        Assert.Contains("already exists", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("Forgot your password?", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("ignore this message", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"en\">", mail.HtmlBody, StringComparison.Ordinal);
        AssertNoLinkAndNoToken(mail);
    }

    [Fact]
    public void The_account_exists_mail_in_polish_has_no_link_and_no_token()   // criteria 4, 21
    {
        var mail = Composer("pl").ComposeAccountExists("anna@firma.test");

        Assert.Equal("Masz już konto w speech-to-mail", mail.Subject);
        Assert.Contains("już istnieje", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"pl\">", mail.HtmlBody, StringComparison.Ordinal);
        AssertNoLinkAndNoToken(mail);
    }

    private static void AssertNoLinkAndNoToken(ComposedMail mail)
    {
        foreach (var part in new[] { mail.Subject, mail.TextBody, mail.HtmlBody })
        {
            Assert.DoesNotContain("://", part, StringComparison.Ordinal);
            Assert.DoesNotContain("token", part, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href", part, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{", part, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    public void Every_language_has_every_text_of_both_mails(string locale)   // criterion 21
    {
        var complete = MailTexts.For(locale, MailKind.Signup);
        Assert.All(
            new[] { complete.Subject, complete.Greeting, complete.Intro, complete.Button, complete.Validity, complete.LinkHint, complete.Ignore },
            value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Contains("{0}", complete.Subject, StringComparison.Ordinal);
        Assert.Contains("{0}", complete.Intro, StringComparison.Ordinal);
        Assert.Contains("{1}", complete.Intro, StringComparison.Ordinal);

        var exists = MailTexts.AccountExists(locale);
        Assert.All(
            new[] { exists.Subject, exists.Greeting, exists.Intro, exists.SignInHint, exists.Ignore },
            value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Contains("{0}", exists.Subject, StringComparison.Ordinal);
        Assert.Contains("{0}", exists.Intro, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_mail_needs_the_company_and_the_configured_address()
    {
        Assert.Throws<ArgumentException>(() => Composer().Compose(MailKind.Signup, "anna@firma.test", Token));
        Assert.Throws<InvalidOperationException>(() => Composer(complete: null).Compose(MailKind.Signup, "anna@firma.test", Token, "Firma"));
    }

    [Fact]
    public void An_unknown_kind_of_mail_is_not_composed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Composer().Compose((MailKind)99, "anna@firma.test", Token, "Firma", "admin"));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail** (compilation: `AccountExists`, `ComposeAccountExists` do not exist).

- [ ] **Step 3: The texts.** In `src/Auth.Server/Email/MailTexts.cs`:

(a) after the line `public sealed record MailText(string Subject, string Greeting, string Intro, string Button, string Validity, string LinkHint, string Ignore);` add

```csharp

/// <summary>
/// The texts of the mail "you already have an account" (spec 0009). It has no link and no button: <see cref="Subject"/>, <see cref="Intro"/>
/// and <see cref="SignInHint"/> hold <c>{0}</c> for the application name. Nothing a visitor typed goes into it.
/// </summary>
public sealed record AccountExistsText(string Subject, string Greeting, string Intro, string SignInHint, string Ignore);
```

(b) in `For`, replace the last arm and the closing of the class, that is the text

```csharp
        _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No mail text for locale '{locale}' and kind '{kind}'."),
    };
}
```

with

```csharp
        ("en", MailKind.Signup) => new MailText(
            Subject: "Complete your sign-up to {0}",
            Greeting: "Hello,",
            Intro: "Someone asked to create the company {1} on {0} with this email address. If it was you, use the link below to choose a password and finish signing up.",
            Button: "Finish signing up",
            Validity: "The link is valid for 24 hours and works once.",
            LinkHint: "If the button does not work, copy this address into your browser:",
            Ignore: "If you did not ask for this, you can ignore this message. Nothing changes until the link is used."),
        ("pl", MailKind.Signup) => new MailText(
            Subject: "Dokończ rejestrację w {0}",
            Greeting: "Dzień dobry,",
            Intro: "Ktoś poprosił o założenie firmy {1} w {0} z tym adresem e-mail. Jeśli to Ty, użyj poniższego linku, aby ustawić hasło i dokończyć rejestrację.",
            Button: "Dokończ rejestrację",
            Validity: "Link jest ważny przez 24 godziny i działa jeden raz.",
            LinkHint: "Jeśli przycisk nie działa, skopiuj ten adres do przeglądarki:",
            Ignore: "Jeśli to nie Ty, zignoruj tę wiadomość. Nic się nie zmieni, dopóki link nie zostanie użyty."),
        _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No mail text for locale '{locale}' and kind '{kind}'."),
    };

    /// <summary>The mail an address with an account gets when someone signs up with it (spec 0009 → Mails).</summary>
    public static AccountExistsText AccountExists(string locale) => locale switch
    {
        "en" => new AccountExistsText(
            Subject: "You already have an account on {0}",
            Greeting: "Hello,",
            Intro: "Someone, perhaps you, tried to sign up for {0} with this email address. An account with this address already exists, so no new account was made.",
            SignInHint: "To use your account, sign in on the sign-in screen of {0}. If you forgot your password, use \"Forgot your password?\" on that screen.",
            Ignore: "If it was not you, you can ignore this message. Nothing has changed."),
        "pl" => new AccountExistsText(
            Subject: "Masz już konto w {0}",
            Greeting: "Dzień dobry,",
            Intro: "Ktoś, być może Ty, próbował zarejestrować się w {0} tym adresem e-mail. Konto z tym adresem już istnieje, więc nie utworzono nowego.",
            SignInHint: "Aby z niego skorzystać, zaloguj się na ekranie logowania {0}. Jeśli nie pamiętasz hasła, użyj na tym ekranie opcji przypomnienia hasła.",
            Ignore: "Jeśli to nie Ty, zignoruj tę wiadomość. Nic się nie zmieniło."),
        _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No mail text for locale '{locale}'."),
    };
}
```

- [ ] **Step 4: The composer.** Replace `src/Auth.Server/Email/MailComposer.cs`:

```csharp
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Auth.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;

namespace Auth.Server.Email;

/// <summary>A mail ready to send. A class, not a record: nothing should print a body, which holds the link token.</summary>
public sealed class ComposedMail
{
    public required string To { get; init; }

    public required string Subject { get; init; }

    public required string TextBody { get; init; }

    public required string HtmlBody { get; init; }
}

/// <summary>
/// Builds the mails of specs 0004, 0005 and 0009 in the configured language, each as plain text and as HTML with the same content. Only
/// configuration and the token go into a mail; nothing a requester submitted does. An invitation also names its company and role, and a
/// sign-up its company, which people typed: they are encoded in the HTML part and are never in a header.
/// </summary>
public sealed class MailComposer(MailSettings settings)
{
    // Every letter stays a letter (Polish diacritics included); only markup characters are escaped.
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    private const string BodyStyle = "font-family: Arial, Helvetica, sans-serif; font-size: 16px; line-height: 1.5; color: #1f2933;";

    /// <param name="companyName">
    /// The company of an invitation or of a sign-up; required for <see cref="MailKind.Invitation"/> and <see cref="MailKind.Signup"/>,
    /// ignored otherwise.
    /// </param>
    /// <param name="roleName">The role of an invitation; required for <see cref="MailKind.Invitation"/>, ignored otherwise.</param>
    public ComposedMail Compose(MailKind kind, string recipient, string token, string? companyName = null, string? roleName = null)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(token);
        if (kind == MailKind.Invitation && (companyName is null || roleName is null))
        {
            throw new ArgumentException("An invitation names its company and its role.", nameof(kind));
        }

        if (kind == MailKind.Signup && companyName is null)
        {
            throw new ArgumentException("A sign-up names its company.", nameof(companyName));
        }

        var text = MailTexts.For(settings.Locale, kind);
        var target = kind switch
        {
            MailKind.PasswordReset => settings.ResetPasswordUrl,
            MailKind.EmailVerification => settings.VerifyEmailUrl,
            MailKind.Invitation => settings.AcceptInviteUrl,
            MailKind.Signup => settings.CompleteSignupUrl
                ?? throw new InvalidOperationException("The completion address of sign-up is not configured."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown kind of mail."),
        };
        var link = QueryHelpers.AddQueryString(target.AbsoluteUri, "token", token);
        // The subject takes the application name only: the company and the role are typed by people, and a header is not the place
        // for that. They appear in the body, encoded in its HTML part.
        var subject = string.Format(CultureInfo.InvariantCulture, text.Subject, settings.AppName);
        var intro = string.Format(CultureInfo.InvariantCulture, text.Intro, settings.AppName, companyName, roleName);

        return new ComposedMail
        {
            To = recipient,
            Subject = subject,
            TextBody = string.Join(
                "\r\n",
                text.Greeting, "", intro, "", link, "", text.Validity, text.Ignore, ""),
            HtmlBody = $"""
                <!DOCTYPE html>
                <html lang="{settings.Locale}">
                <head><meta charset="utf-8"><title>{Html.Encode(subject)}</title></head>
                <body style="{BodyStyle}">
                <p>{Html.Encode(text.Greeting)}</p>
                <p>{Html.Encode(intro)}</p>
                <p><a href="{Html.Encode(link)}" style="display: inline-block; padding: 12px 20px; background-color: #1f6feb; color: #ffffff; text-decoration: none; border-radius: 6px;">{Html.Encode(text.Button)}</a></p>
                <p>{Html.Encode(text.LinkHint)}<br><a href="{Html.Encode(link)}">{Html.Encode(link)}</a></p>
                <p>{Html.Encode(text.Validity)}</p>
                <p style="font-size: 14px; color: #52606d;">{Html.Encode(text.Ignore)}</p>
                </body>
                </html>
                """,
        };
    }

    /// <summary>
    /// The mail an address with an account gets when someone signs up with it (spec 0009 → Mails): it says that someone, perhaps the
    /// reader, tried, that the account exists, and how to sign in or reset the password. It holds no link and no token, and nothing
    /// the requester typed.
    /// </summary>
    public ComposedMail ComposeAccountExists(string recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);

        var text = MailTexts.AccountExists(settings.Locale);
        var subject = string.Format(CultureInfo.InvariantCulture, text.Subject, settings.AppName);
        var intro = string.Format(CultureInfo.InvariantCulture, text.Intro, settings.AppName);
        var hint = string.Format(CultureInfo.InvariantCulture, text.SignInHint, settings.AppName);

        return new ComposedMail
        {
            To = recipient,
            Subject = subject,
            TextBody = string.Join("\r\n", text.Greeting, "", intro, "", hint, "", text.Ignore, ""),
            HtmlBody = $"""
                <!DOCTYPE html>
                <html lang="{settings.Locale}">
                <head><meta charset="utf-8"><title>{Html.Encode(subject)}</title></head>
                <body style="{BodyStyle}">
                <p>{Html.Encode(text.Greeting)}</p>
                <p>{Html.Encode(intro)}</p>
                <p>{Html.Encode(hint)}</p>
                <p style="font-size: 14px; color: #52606d;">{Html.Encode(text.Ignore)}</p>
                </body>
                </html>
                """,
        };
    }
}
```

- [ ] **Step 5: Run the tests.** `dotnet test tests/Auth.IntegrationTests --filter "FullyQualifiedName~MailComposerTests"` (this matches the three composer classes: the older two must still pass, the output of the older mails being unchanged); then the whole project.

- [ ] **Step 6: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(server): the mails of a self-service sign-up in Polish and English`.

### Task 5: `POST /auth/signup`

**Files:**
- Create: `src/Auth.Server/Signup/SignupRequestEndpoint.cs`
- Modify: `src/Auth.Server/Email/MailRequestStore.cs` (replaced as a whole), `src/Auth.Server/Audit/AuditKinds.cs`, `src/Auth.Server/Api/SignupRoutes.cs`
- Modify (tests): `tests/Auth.IntegrationTests/AuditTableTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/Infrastructure/SignupTestBase.cs`, `SignupRequestTests.cs`, `SignupDisabledRequestTests.cs`, `SignupRateLimitTests.cs`

**Interfaces:**
- Consumes: Task 1 (`SignupGate`, `TenancyErrors.SignupDisabled`), Task 2 (`SignupApi`, `SignupRoutes`, `SignupRequest`), Task 3 (`PendingSignups.UpsertAsync`, `MailKind.Signup`), `JsonObjectBody.ReadStringsAsync`, `EmailInput`, `NameInput`, `TooManyAttemptsResult`, `MailLimits`.
- Produces:
  - `MailRequestStore.SubmitAsync(MailKind kind, string normalizedEmail, CancellationToken ct, string? typedEmail = null, string? companyName = null)`: for `MailKind.Signup` both extra arguments are required, and in the same transaction as the limit and the queued request it upserts the pending sign-up and stages `signup.requested`.
  - `AuditKinds.SignupRequested = "signup.requested"` and `AuditKinds.SignupCompleted = "signup.completed"` (both of the 24 kinds are added here).
  - `SignupRequestEndpoint.Path = "/auth/signup"`, `HandleAsync(...)`.
  - Test side: `SignupTestBase : AuditTestBase` — a host with sign-up on, the dispatcher and the pruning service left out (`MailTestBase`, `EmailPruningService`), constants `Address = "anna@firma.test"`, `CompanyName = "Firma"`, `Password = "Chosen-Passw0rd"`, `PastTheLimit` (61 s), helpers `PendingAsync()`, `QueueCountAsync()`, `LimitCountAsync()` and `MailedAsync(email, company)` (asks, asserts `202`, runs the dispatcher, returns the token of the newest mail to the address).

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/Infrastructure/SignupTestBase.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Base of the sign-up tests: a host with sign-up on (<see cref="AuthAppFactory.WithSignup"/>), whose dispatcher does not run by itself
/// (<see cref="MailTestBase"/>) and whose hourly pruning pass is left out, so that a test which moves the clock by a day is not raced by it;
/// a test that needs the pass runs it by hand. The requests may name their client address (<see cref="AuditTestBase"/>).
/// </summary>
public abstract class SignupTestBase : AuditTestBase
{
    protected const string Address = "anna@firma.test";
    protected const string CompanyName = "Firma";
    protected const string Password = "Chosen-Passw0rd";

    protected static readonly TimeSpan PastTheLimit = TimeSpan.FromSeconds(61);

    protected SignupTestBase(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSignup().WithoutHostedService<EmailPruningService>();
    }

    protected Task<List<PendingSignup>> PendingAsync() =>
        InDbAsync(db => db.PendingSignups.AsNoTracking().OrderBy(p => p.NormalizedEmail).ToListAsync(TestContext.Current.CancellationToken));

    protected Task<int> QueueCountAsync() =>
        InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken));

    protected Task<int> LimitCountAsync() =>
        InDbAsync(db => db.MailRequestLimits.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>Asks for a sign-up (asserting the 202), runs the dispatcher and returns the token of the newest mail to the address.</summary>
    protected async Task<string> MailedAsync(string email = Address, string company = CompanyName)
    {
        using var response = await SignupApi.Request(Client, email, company);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        await DispatchAsync();
        return TokenIn(Mail.Sent.Last(m => string.Equals(m.To, email, StringComparison.Ordinal)));
    }
}
```

`tests/Auth.IntegrationTests/SignupRequestTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class SignupRequestTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    // Spelled out, so that the escapes written into a request body are not read as escapes of this file.
    private const string Backslash = "\\";

    public static TheoryData<string> MalformedBodies => new()
    {
        "{}",
        """{"email":"anna@firma.test"}""",
        """{"company_name":"Firma"}""",
        """{"email":"","company_name":"Firma"}""",
        """{"email":"   ","company_name":"Firma"}""",
        """{"email":"anna@firma.test","company_name":""}""",
        """{"email":"anna@firma.test","company_name":"   "}""",
        """{"email":123,"company_name":"Firma"}""",
        """{"email":"anna@firma.test","company_name":null}""",
        """{"email":"anna@firma.test","company_name":["Firma"]}""",
        """["anna@firma.test","Firma"]""",
        "not json",
        "",
        """{"email":"anna@firma.test","company_name":"Firma",""",
        "{\"email\":\"anna@firma.test\",\"company_name\":\"Fi" + Backslash + "u0000rma\"}",   // NUL
        "{\"email\":\"anna@firma.test\",\"company_name\":\"Fi" + Backslash + "ud800rma\"}",   // an unpaired surrogate escape
        "{\"email\":\"anna@firma" + Backslash + "u0000.test\",\"company_name\":\"Firma\"}",
    };

    public static TheoryData<string> RefusedAddresses => new()
    {
        "bob.firma.test",
        " anna@firma.test",
        "anna@firma.test ",
        "Anna <anna@firma.test>",
        "anna@firma.test, bob@firma.test",
        "anna@localhost",
        "anna@intranet",
        "anna@[10.0.0.5]",
        "anna@10.0.0.5",
        "anna@127.0x1",
        "anna@host.123",
        new string('a', 244) + "@firma.test",                                  // 255 characters
        char.ConvertFromUtf32(0x17F) + "nna@firma.test",                       // the long s: stands for another address
        "a" + (char)0xFFFE + "b@firma.test",                                   // a noncharacter
    };

    public static TheoryData<string> RefusedCompanyNames => new()
    {
        " Firma",
        "Firma ",
        new string('x', 101),
        "Fi" + (char)1 + "rma",
        "Fi" + (char)10 + "rma",
        "Fi" + char.ConvertFromUtf32(0x200B) + "rma",                          // a format character
        "Fi" + (char)0xFFFE + "rma",
    };

    /// <summary>The SQL of every database command the host has logged since <paramref name="mark"/> log entries.</summary>
    private List<string> StatementsSince(int mark) =>
        [.. Logs.Entries.Skip(mark)
            .Where(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command")
            .Select(e => e.Message[(e.Message.IndexOf('\n', StringComparison.Ordinal) + 1)..].Trim())];

    private async Task AssertNothingWasMadeAsync()
    {
        Assert.Empty(await PendingAsync());
        Assert.Equal(0, await QueueCountAsync());
        Assert.Equal(0, await LimitCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.SignupRequested));
    }

    [Fact]
    public async Task A_new_address_is_accepted_with_an_empty_body_and_a_pending_sign_up_is_stored()   // criterion 3
    {
        using var response = await SignupApi.Request(Client, "Anna@Firma.test", CompanyName);

        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        Assert.Empty(Mail.Sent);   // nothing is sent inside the request
        var pending = Assert.Single(await PendingAsync());
        Assert.Equal("ANNA@FIRMA.TEST", pending.NormalizedEmail);
        Assert.Equal("Anna@Firma.test", pending.Email);
        Assert.Equal(CompanyName, pending.CompanyName);
        Assert.Equal(Clock.GetUtcNow().AddHours(24).UtcDateTime, pending.ExpiresAt.UtcDateTime, TimeSpan.FromMilliseconds(1));
        Assert.Null(pending.TokenHash);
        var queued = await InDbAsync(db => db.MailRequests.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(MailKind.Signup, queued.Kind);
        Assert.Equal("ANNA@FIRMA.TEST", queued.NormalizedEmail);
        Assert.Null(queued.InviteId);
    }

    [Fact]
    public async Task An_address_with_an_account_gets_the_same_status_headers_and_body()   // criterion 4
    {
        using var known = await SignupApi.Request(Client, Factory.SeedEmail, CompanyName);
        using var unknown = await SignupApi.Request(Client, "nobody@firma.test", CompanyName);

        await AccountApi.AssertEmptyAsync(known, HttpStatusCode.Accepted);
        await AccountApi.AssertEmptyAsync(unknown, HttpStatusCode.Accepted);
        Assert.Equal(LoginApi.HeaderNames(known), LoginApi.HeaderNames(unknown));
        // The same work for both: one pending sign-up and one queued request each, alike but for the address.
        var rows = await InDbAsync(db => db.MailRequests.AsNoTracking().OrderBy(r => r.Id).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            (rows[0].Kind, rows[0].Attempts, rows[0].RequestedAt, rows[0].NextAttemptAt),
            (rows[1].Kind, rows[1].Attempts, rows[1].RequestedAt, rows[1].NextAttemptAt));
        Assert.Equal(2, (await PendingAsync()).Count);
        Assert.Equal(2, await LimitCountAsync());
    }

    [Fact]
    public async Task The_request_runs_the_same_statements_for_any_address_and_never_reads_the_accounts()   // criterion 4
    {
        var mark = Logs.Entries.Count;
        using var known = await SignupApi.Request(Client, Factory.SeedEmail, CompanyName);
        var forKnown = StatementsSince(mark);

        mark = Logs.Entries.Count;
        using var unknown = await SignupApi.Request(Client, "nobody@firma.test", CompanyName);
        var forUnknown = StatementsSince(mark);

        // A pass of the host may log a DELETE of its own in between; what the request runs is not one.
        static List<string> OfTheRequest(List<string> statements) =>
            [.. statements.Where(sql => !sql.StartsWith("DELETE", StringComparison.Ordinal)
                && (sql.Contains("PendingSignups", StringComparison.Ordinal)
                    || sql.Contains("MailRequest", StringComparison.Ordinal)
                    || sql.Contains("audit_events", StringComparison.Ordinal)))];

        Assert.NotEmpty(OfTheRequest(forKnown));
        Assert.Contains(forKnown, sql => sql.Contains("PendingSignups", StringComparison.Ordinal));
        Assert.Equal(OfTheRequest(forKnown), OfTheRequest(forUnknown));
        Assert.DoesNotContain(forKnown.Concat(forUnknown), sql => sql.Contains("AspNetUsers", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task Malformed_bodies_are_400_invalid_request_and_queue_nothing(string body)   // criterion 5
    {
        using var response = await AccountApi.PostRaw(Client, SignupApi.RequestPath, body);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        await AssertNothingWasMadeAsync();
    }

    [Fact]
    public async Task A_body_that_is_not_json_by_its_content_type_is_400_invalid_request()
    {
        using var response = await AccountApi.PostRaw(
            Client, SignupApi.RequestPath, """{"email":"anna@firma.test","company_name":"Firma"}""", "text/plain");

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        await AssertNothingWasMadeAsync();
    }

    [Theory]
    [MemberData(nameof(RefusedAddresses))]
    public async Task Addresses_the_invitation_rule_refuses_are_400_and_queue_nothing(string address)   // criterion 5
    {
        using var response = await SignupApi.Request(Client, address, CompanyName);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        await AssertNothingWasMadeAsync();
    }

    [Theory]
    [MemberData(nameof(RefusedCompanyNames))]
    public async Task Company_names_that_break_the_name_rule_are_400_and_queue_nothing(string name)   // criterion 5
    {
        using var response = await SignupApi.Request(Client, Address, name);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        await AssertNothingWasMadeAsync();
    }

    [Fact]
    public async Task Names_at_the_limit_with_polish_letters_and_pairs_are_kept_whole()   // Review Focus 4 (the half that reaches the mail is in SignupDispatchTests)
    {
        var factory = char.ConvertFromUtf32(0x1F3ED);
        string[] names =
        [
            new string('Ż', 100),
            "Żółw i Spółka",
            string.Concat(Enumerable.Repeat(factory, 50)),   // 100 UTF-16 characters in 50 pairs
            factory + " Fabryka & <Syn>",
        ];

        for (var i = 0; i < names.Length; i++)
        {
            var address = $"anna{i}@firma.test";
            using var response = await SignupApi.Request(Client, address, names[i]);
            await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
        }

        var stored = (await PendingAsync()).ToDictionary(p => p.Email, p => p.CompanyName);
        for (var i = 0; i < names.Length; i++)
        {
            Assert.Equal(names[i], stored[$"anna{i}@firma.test"]);
        }
    }

    [Fact]
    public async Task Spellings_of_one_address_share_one_pending_sign_up_and_one_limit()   // Review Focus 2
    {
        using var first = await SignupApi.Request(Client, "Anna@Firma.test", "First");
        await AccountApi.AssertEmptyAsync(first, HttpStatusCode.Accepted);

        using var second = await SignupApi.Request(Client, "ANNA@firma.TEST", "Second");
        Assert.Equal(60, await LockoutApi.AssertLockedAsync(second));   // the same address to the limit

        Clock.Advance(PastTheLimit);
        using var third = await SignupApi.Request(Client, "ANNA@firma.TEST", "Second");
        await AccountApi.AssertEmptyAsync(third, HttpStatusCode.Accepted);

        var pending = Assert.Single(await PendingAsync());
        Assert.Equal("ANNA@firma.TEST", pending.Email);
        Assert.Equal("Second", pending.CompanyName);
    }

    [Fact]
    public async Task A_second_request_within_a_minute_is_refused_and_asking_again_does_not_add_to_the_wait()   // criterion 6, Review Focus 3
    {
        using var first = await SignupApi.Request(Client, Address, CompanyName);
        await AccountApi.AssertEmptyAsync(first, HttpStatusCode.Accepted);

        using var second = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(60, await LockoutApi.AssertLockedAsync(second));

        Clock.Advance(TimeSpan.FromSeconds(20));
        using var third = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(40, await LockoutApi.AssertLockedAsync(third));

        Clock.Advance(TimeSpan.FromSeconds(40));
        using var fourth = await SignupApi.Request(Client, Address, CompanyName);
        await AccountApi.AssertEmptyAsync(fourth, HttpStatusCode.Accepted);
        Assert.Equal(2, await QueueCountAsync());
    }

    [Fact]
    public async Task The_sixth_request_within_the_hour_is_refused_until_the_hour_is_over()   // criterion 6
    {
        for (var i = 0; i < 5; i++)
        {
            using var accepted = await SignupApi.Request(Client, Address, CompanyName);
            await AccountApi.AssertEmptyAsync(accepted, HttpStatusCode.Accepted);
            Clock.Advance(PastTheLimit);
        }

        using var sixth = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(3600 - 305, await LockoutApi.AssertLockedAsync(sixth));

        Clock.Advance(TimeSpan.FromSeconds(3600 - 305));
        using var later = await SignupApi.Request(Client, Address, CompanyName);
        await AccountApi.AssertEmptyAsync(later, HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task The_sign_up_limit_and_the_limits_of_forgot_and_verify_do_not_affect_each_other()   // criterion 6
    {
        using var signup = await SignupApi.Request(Client, Address, CompanyName);
        await AccountApi.AssertEmptyAsync(signup, HttpStatusCode.Accepted);

        using var forgot = await AccountApi.Forgot(Client, Address);
        using var verify = await AccountApi.RequestVerification(Client, Address);
        await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        await AccountApi.AssertEmptyAsync(verify, HttpStatusCode.Accepted);

        using var otherForgot = await AccountApi.Forgot(Client, "other@firma.test");
        await AccountApi.AssertEmptyAsync(otherForgot, HttpStatusCode.Accepted);
        using var otherSignup = await SignupApi.Request(Client, "other@firma.test", CompanyName);
        await AccountApi.AssertEmptyAsync(otherSignup, HttpStatusCode.Accepted);

        Assert.Equal(5, await LimitCountAsync());   // three kinds for one address, two for the other
    }

    [Fact]
    public async Task A_second_accepted_request_replaces_the_first()   // criterion 7
    {
        using var first = await SignupApi.Request(Client, Address, "First");
        await AccountApi.AssertEmptyAsync(first, HttpStatusCode.Accepted);
        Clock.Advance(PastTheLimit);

        using var second = await SignupApi.Request(Client, Address, "Second");
        await AccountApi.AssertEmptyAsync(second, HttpStatusCode.Accepted);

        var pending = Assert.Single(await PendingAsync());
        Assert.Equal("Second", pending.CompanyName);
        Assert.Equal(Clock.GetUtcNow().AddHours(24).UtcDateTime, pending.ExpiresAt.UtcDateTime, TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, await QueueCountAsync());
    }

    [Fact]
    public async Task An_accepted_request_is_recorded_with_the_address_and_nothing_secret()   // criterion 19
    {
        var body = """{"email":"Anna@Firma.test","company_name":"Firma"}""";

        using var response = await RateLimitApi.SendAsync(Client, HttpMethod.Post, SignupApi.RequestPath, CallerAddress, null, body);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);

        var row = await SingleAsync(AuditKinds.SignupRequested);
        Assert.Equal("Anna@Firma.test", row.SubjectEmail);
        Assert.Equal(CallerAddress, row.ClientIp);
        Assert.Null(row.ActorUserId);
        Assert.Null(row.SubjectUserId);
        Assert.Null(row.OrgId);
        Assert.Null(row.OrgName);
        Assert.Null(row.TargetId);
        Assert.Null(row.Details);
        await AssertNoSecretsAsync(Password);
    }

    [Fact]
    public async Task A_refused_request_is_not_recorded()   // criterion 19: only an accepted request
    {
        using var accepted = await SignupApi.Request(Client, Address, CompanyName);
        using var limited = await SignupApi.Request(Client, Address, CompanyName);
        using var malformed = await AccountApi.PostRaw(Client, SignupApi.RequestPath, "{}");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Single(await AuditAsync(AuditKinds.SignupRequested));
    }
}
```

`tests/Auth.IntegrationTests/SignupDisabledRequestTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class SignupDisabledRequestTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static readonly string[] Bodies =
    [
        """{"email":"anna@firma.test","company_name":"Firma"}""",
        "not json",
        "",
        "{}",
    ];

    [Theory]
    [MemberData(nameof(SignupVariants.Off), MemberType = typeof(SignupVariants))]
    public async Task Sign_up_off_answers_403_before_the_body_is_read_and_creates_nothing(string variant)   // criteria 2, 17
    {
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), variant);
        using var client = factory.CreateClient();

        foreach (var body in Bodies)
        {
            using var response = await AccountApi.PostRaw(client, SignupApi.RequestPath, body);
            await SignupApi.AssertDisabledAsync(response);
        }

        using var wrongType = await AccountApi.PostRaw(client, SignupApi.RequestPath, Bodies[0], "text/plain");
        await SignupApi.AssertDisabledAsync(wrongType);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.Equal(0, await db.PendingSignups.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.MailRequests.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.MailRequestLimits.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.AuditEvents.CountAsync(a => a.Kind == AuditKinds.SignupRequested, TestContext.Current.CancellationToken));
    }
}
```

`tests/Auth.IntegrationTests/SignupRateLimitTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.RateLimiting;

namespace Auth.IntegrationTests;

public sealed class SignupRateLimitTests : SignupTestBase
{
    private const string Remote = "203.0.113.9";

    public SignupRateLimitTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(RateLimitSettings.EnabledKey, "true")
            .WithSetting(RateLimitSettings.PermitKey(RatePolicy.Email), "3");
    }

    private Task<HttpResponseMessage> RequestFrom(string remote, int number) =>
        RateLimitApi.SendAsync(
            Client, HttpMethod.Post, SignupApi.RequestPath, remote, null, $$"""{"email":"a{{number}}@firma.test","company_name":"Firma"}""");

    [Fact]
    public async Task The_request_counts_against_the_mail_policy_of_the_per_ip_limit()   // criterion 18
    {
        for (var i = 1; i <= 3; i++)
        {
            using var accepted = await RequestFrom(Remote, i);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var refused = await RequestFrom(Remote, 4);
        await RateLimitApi.AssertTooManyRequestsAsync(refused);
        Assert.Equal(3, (await PendingAsync()).Count);   // the request over the limit did no other work

        using var otherAddress = await RequestFrom("203.0.113.10", 5);
        Assert.Equal(HttpStatusCode.Accepted, otherAddress.StatusCode);
    }
}
```

Edits to existing tests (lines added, and one test method renamed):

(a) `tests/Auth.IntegrationTests/AuditTableTests.cs`: rename the method `The_kinds_are_the_twenty_two_of_the_contract` to `The_kinds_are_the_twenty_four_of_the_contract`, and after the line `                "org.created", "org.renamed", "org.deleted", "org.delete_refused", "rate_limit.hit",` add the line `                "signup.requested", "signup.completed",`.

(b) `tests/Auth.IntegrationTests/OpenApiTests.cs`: in `Endpoints`, replace `        "GET /auth/options",` with

```csharp
        "GET /auth/options",
        "POST /auth/signup",
```

after `    [InlineData("GET /auth/options", "200,429")]` add

```csharp
    [InlineData("POST /auth/signup", "202,400,403,415,429")]
```

and after `    [InlineData("POST /auth/invites/accept", "password,token")]` add

```csharp
    [InlineData("POST /auth/signup", "company_name,email")]
```

- [ ] **Step 2: Run the tests to see them fail** (the project does not compile: `AuditKinds.SignupRequested`, `SubmitAsync(... companyName)`).

- [ ] **Step 3: The audit kinds.** In `src/Auth.Server/Audit/AuditKinds.cs`: after `public const string RateLimitHit = "rate_limit.hit";` add

```csharp
    public const string SignupRequested = "signup.requested";
    public const string SignupCompleted = "signup.completed";
```

and in `All`, replace the line `        OrgCreated, OrgRenamed, OrgDeleted, OrgDeleteRefused, RateLimitHit,` with

```csharp
        OrgCreated, OrgRenamed, OrgDeleted, OrgDeleteRefused, RateLimitHit,
        SignupRequested, SignupCompleted,
```

Also change the summary of the class to say "(spec 0008 and spec 0009 → Audit log)" and nothing else.

- [ ] **Step 4: The queue.** Replace `src/Auth.Server/Email/MailRequestStore.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Auth.Server.Signup;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Email;

/// <summary>
/// Takes a request for a mail: applies the limit of its address and kind and, if the request is within it, puts it
/// on the queue — one short transaction with the limit row locked, so parallel requests are each judged in turn. It
/// does the same work for every address and never looks an account up (spec 0004, Decision 12; spec 0009 for a sign-up). It uses a
/// scope of its own, so the request's DbContext never tracks these rows.
/// </summary>
public sealed class MailRequestStore(IServiceScopeFactory scopes, TimeProvider clock)
{
    /// <param name="typedEmail">
    /// The address as it was typed. A password reset request is recorded in the audit log with it, in the same transaction as the
    /// queued request, and with no account: the request does not look the account up (spec 0004). A sign-up (spec 0009) needs it: it is
    /// the address of the pending sign-up and of its audit row.
    /// </param>
    /// <param name="companyName">The company name of a sign-up request; required for <see cref="MailKind.Signup"/>, ignored otherwise.</param>
    public async Task<MailLimitDecision> SubmitAsync(
        MailKind kind, string normalizedEmail, CancellationToken cancellationToken, string? typedEmail = null, string? companyName = null)
    {
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        if (kind == MailKind.Signup && (typedEmail is null || companyName is null))
        {
            throw new ArgumentException("A sign-up request carries the address as typed and the company name.", nameof(kind));
        }

        var now = StorableTime.Now(clock);
        var identifierHash = LoginIdentifier.HashOf(normalizedEmail);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The limit row is created or locked by one statement (MailLimits), and returned either way.
        var decision = await MailLimits.RegisterAsync(db, identifierHash, kind, now, cancellationToken);
        if (decision.Allowed)
        {
            db.MailRequests.Add(new MailRequest { Kind = kind, NormalizedEmail = normalizedEmail, RequestedAt = now, NextAttemptAt = now });
            var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();
            if (kind == MailKind.PasswordReset && typedEmail is not null)
            {
                audit.Stage(new AuditEntry { Kind = AuditKinds.PasswordResetRequested, SubjectEmail = typedEmail });
            }
            else if (kind == MailKind.Signup && typedEmail is not null && companyName is not null)
            {
                // The pending sign-up takes the place an account would have: it is what the mail, when it is sent, is about. One
                // statement, whether the address has an account or not (which the request never learns).
                await PendingSignups.UpsertAsync(db, normalizedEmail, typedEmail, companyName, now, cancellationToken);
                audit.Stage(new AuditEntry { Kind = AuditKinds.SignupRequested, SubjectEmail = typedEmail });
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return decision;
    }
}
```

- [ ] **Step 5: The endpoint.** `src/Auth.Server/Signup/SignupRequestEndpoint.cs`:

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
/// <c>POST /auth/signup</c> (spec 0009): takes a request to start a company. While sign-up is off it answers <c>403 signup_disabled</c>
/// before it reads the body. Otherwise it validates, applies the limit of the address, stores the pending sign-up, queues the request and
/// answers; it never looks an account up, so its work and its answer are the same for every address. Which mail follows is decided when
/// the mail is sent.
/// </summary>
public static class SignupRequestEndpoint
{
    public const string Path = "/auth/signup";

    private static readonly string[] Fields = ["email", "company_name"];

    public static async Task<IResult> HandleAsync(
        HttpContext http, SignupGate gate, ILookupNormalizer normalizer, MailRequestStore requests, MailDispatchSignal signal)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(signal);

        if (!gate.IsOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, http.RequestAborted);
        // The address rule of invitations (spec 0005), so that sign-up cannot make the mail relay deliver to an internal host, and the
        // name rule of companies.
        if (fields is null
            || fields[0].Length > EmailInput.MaxLength
            || !EmailInput.IsMailbox(fields[0])
            || !EmailInput.IsInvitable(fields[0])
            || !EmailInput.TryNormalize(fields[0], normalizer, out var normalized)
            || !NameInput.IsValid(fields[1]))
        {
            return AccountResults.InvalidRequest();
        }

        var decision = await requests.SubmitAsync(MailKind.Signup, normalized, http.RequestAborted, typedEmail: fields[0], companyName: fields[1]);
        if (!decision.Allowed)
        {
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        signal.Notify();
        return AccountResults.Accepted();
    }
}
```

In `src/Auth.Server/Api/SignupRoutes.cs`, before `        return app;` add

```csharp
        app.MapPost(SignupRequestEndpoint.Path, SignupRequestEndpoint.HandleAsync)
            .WithTags("Sign-up")
            .WithDescription(
                "Asks to start a company. The answer is the same `202` for every address; a mail follows: a link to finish when the address "
                + "has no account, a notice without a link when it has one. Off unless the manifest says `signup: self-service` and the "
                + "completion address is configured (`403 signup_disabled`). One request a minute and five an hour per address.")
            .ReadsJson<SignupRequest>()
            .Produces(StatusCodes.Status202Accepted)
            .ProducesError(StatusCodes.Status400BadRequest, AccountResults.InvalidRequestError)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.SignupDisabled)
            .ProducesTooManyAttempts();

```

and add the usings `using Auth.Server.Account;` and `using Auth.Server.Tenancy;` at the top of the file.

- [ ] **Step 6: Run the tests.** `dotnet test tests/Auth.IntegrationTests --filter "FullyQualifiedName~Signup|FullyQualifiedName~OpenApi|FullyQualifiedName~AuditTable"`; then the whole project. The older `MailRequestEndpointTests` and `MailRequestStoreTests` must pass unchanged.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(server): POST /auth/signup with the same answer for every address`.

### Task 6: The dispatcher: the sign-up mail, the "you already have an account" mail, the retries

**Files:**
- Modify: `src/Auth.Server/Email/MailDispatcher.cs`
- Create (tests): `tests/Auth.IntegrationTests/SignupDispatchTests.cs`

**Interfaces:**
- Consumes: Task 3 (`PendingSignups.LockUsableAsync/IssueAsync/DeleteAsync`), Task 4 (`MailComposer.Compose(MailKind.Signup, ...)`, `ComposeAccountExists`), Task 5 (`SignupTestBase`, `MailedAsync`).
- Produces: nothing new to call. Behaviour, **the account check first** (the owner's ruling): a queued `MailKind.Signup` row is mailable when the address has an account (an `AspNetUsers` row with a non-null `Email`, any account: confirmed or not, with or without a company) **or** a live pending sign-up of the address exists; with neither it is dropped (the bulk delete) like any request that needs no mail. When it is handled: with an account, the mail "you already have an account" goes to the address as the account stores it and any pending row of the address is deleted (live or not, in the transaction of the send, so the delete takes its row lock and a failed send keeps it); without an account, the live pending sign-up is locked (`FOR UPDATE`) and gets a new link token (the earlier link stops working) and the mail "complete your sign-up" goes to the typed address; if the lock waited for a completion that has just made the account, the account is looked up once more and the notice goes out. A failed send rolls back to the savepoint: the earlier link stays valid, the pending sign-up stays.

- [ ] **Step 1: Write the failing tests.** `tests/Auth.IntegrationTests/SignupDispatchTests.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class SignupDispatchTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    private Task<string> DumpAsync() =>
        InDbAsync(async db => string.Join(' ', await db.Database.SqlQuery<string>(
            $"""
            SELECT coalesce(string_agg(row_to_json(p)::text, ' '), '') AS "Value" FROM "PendingSignups" p
            UNION ALL SELECT coalesce(string_agg(row_to_json(m)::text, ' '), '') FROM "MailRequests" m
            UNION ALL SELECT coalesce(string_agg(row_to_json(t)::text, ' '), '') FROM "EmailTokens" t
            UNION ALL SELECT coalesce(string_agg(row_to_json(a)::text, ' '), '') FROM audit_events a
            """).ToListAsync(TestContext.Current.CancellationToken)));

    [Fact]
    public async Task A_new_address_gets_the_complete_mail_with_the_link_and_the_company_name()   // criteria 3, 21
    {
        var token = await MailedAsync();

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Address, mail.To);
        Assert.StartsWith("Complete your sign-up", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("Auth-Core Test", mail.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain(CompanyName, mail.Subject, StringComparison.Ordinal);
        Assert.Contains(AuthAppFactory.DefaultCompleteSignupUrl + "?token=" + token, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(CompanyName, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("valid for 24 hours", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains($"href=\"{AuthAppFactory.DefaultCompleteSignupUrl}?token={token}\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(CompanyName, mail.HtmlBody, StringComparison.Ordinal);
        Assert.Equal(0, await QueueCountAsync());
        // The same link in the text and in the HTML part.
        var links = Regex.Matches(mail.TextBody + mail.HtmlBody, @"complete-signup\?token=([A-Za-z0-9_-]{43})");
        Assert.All(links, link => Assert.Equal(token, link.Groups[1].Value));
    }

    [Fact]
    public async Task Only_the_hash_of_the_token_is_stored_and_the_24_hours_start_at_the_mail()   // criteria 3, 13
    {
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Clock.Advance(TimeSpan.FromMinutes(30));   // the request was made earlier than its mail (within the hour the queue keeps it)

        await DispatchAsync();

        var token = TokenIn(Assert.Single(Mail.Sent));
        var row = Assert.Single(await PendingAsync());
        Assert.Equal(43, token.Length);
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);
        Assert.Equal(StorableTime.Now(Clock) + TimeSpan.FromHours(24), row.TokenExpiresAt);
        Assert.Equal(StorableTime.Now(Clock) + TimeSpan.FromHours(24), row.ExpiresAt);
        Assert.DoesNotContain(token, await DumpAsync(), StringComparison.Ordinal);   // the token in clear is in no row of any table that could hold it
    }

    [Fact]
    public async Task Names_at_the_limit_with_polish_letters_and_pairs_reach_the_mail_whole()   // Review Focus 4
    {
        var factory = char.ConvertFromUtf32(0x1F3ED);
        string[] names =
        [
            new string('Ż', 100),
            "Żółw i Spółka",
            string.Concat(Enumerable.Repeat(factory, 50)),   // 100 UTF-16 characters in 50 pairs
            factory + " Fabryka & <Syn>",
        ];
        for (var i = 0; i < names.Length; i++)
        {
            using var response = await SignupApi.Request(Client, $"anna{i}@firma.test", names[i]);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await DispatchAsync();

        for (var i = 0; i < names.Length; i++)
        {
            var mail = Mail.Sent.Single(m => m.To == $"anna{i}@firma.test");
            Assert.Contains(names[i], mail.TextBody, StringComparison.Ordinal);
            Assert.Contains(names[i], WebUtility.HtmlDecode(mail.HtmlBody), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_second_mail_replaces_the_link_of_the_first()   // criterion 7
    {
        var first = await MailedAsync(company: "First");
        Clock.Advance(PastTheLimit);

        var second = await MailedAsync(company: "Second");

        Assert.NotEqual(first, second);
        Assert.Equal(2, Mail.Sent.Count);
        Assert.Contains("Second", Mail.Sent[1].TextBody, StringComparison.Ordinal);
        var row = Assert.Single(await PendingAsync());
        Assert.Equal(EmailTokens.HashOf(second), row.TokenHash);
        Assert.Equal("Second", row.CompanyName);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task An_address_with_an_account_gets_the_account_exists_mail_and_the_pending_sign_up_is_gone(bool confirmed, bool member)   // criterion 4
    {
        await CreateUserAsync(Address, confirmed, member: member);
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Address, mail.To);
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        foreach (var part in new[] { mail.Subject, mail.TextBody, mail.HtmlBody })
        {
            Assert.DoesNotContain("://", part, StringComparison.Ordinal);
            Assert.DoesNotContain("token", part, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(await PendingAsync());
        Assert.Equal(0, await QueueCountAsync());
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task The_account_exists_mail_goes_to_the_stored_address_spelled_as_stored()   // Review Focus 2
    {
        await CreateUserAsync("Anna.Firma@Firma.Test", confirmed: true, member: false);
        using var response = await SignupApi.Request(Client, "anna.firma@firma.test", CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await DispatchAsync();

        Assert.Equal("Anna.Firma@Firma.Test", Assert.Single(Mail.Sent).To);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("expired")]
    public async Task A_sign_up_whose_pending_sign_up_is_gone_and_whose_address_has_no_account_sends_nothing(string how)   // the mail bullets of spec 0009
    {
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        if (how == "expired")
        {
            Clock.Advance(TimeSpan.FromHours(25));   // the request is also past the hour after which the queue drops it
        }
        else
        {
            await InDbAsync(db => db.PendingSignups.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, await DispatchAsync());

        Assert.Empty(Mail.Sent);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task An_address_with_an_account_gets_the_notice_even_when_its_pending_sign_up_is_gone()   // the owner's ruling: the account check comes first
    {
        await CreateUserAsync("Anna.Firma@Firma.Test", confirmed: true, member: false);
        using var response = await SignupApi.Request(Client, "anna.firma@firma.test", CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await InDbAsync(db => db.PendingSignups.ExecuteDeleteAsync(TestContext.Current.CancellationToken));   // completed, or replaced by an account, since

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal("Anna.Firma@Firma.Test", mail.To);
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        Assert.Equal(0, await QueueCountAsync());
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task An_address_with_an_account_and_an_expired_pending_sign_up_gets_the_notice_and_loses_the_row()
    {
        await CreateUserAsync(Address, confirmed: true, member: false);
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            // A row that has expired but not yet been pruned, and the request that was queued while it was alive.
            db.PendingSignups.Add(new PendingSignup
            {
                NormalizedEmail = "ANNA@FIRMA.TEST",
                Email = Address,
                CompanyName = CompanyName,
                RequestedAt = now - TimeSpan.FromHours(30),
                ExpiresAt = now - TimeSpan.FromHours(6),
            });
            db.MailRequests.Add(new MailRequest { Kind = MailKind.Signup, NormalizedEmail = "ANNA@FIRMA.TEST", RequestedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        await DispatchAsync();

        Assert.StartsWith("You already have an account", Assert.Single(Mail.Sent).Subject, StringComparison.Ordinal);
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task A_request_for_a_gone_pending_sign_up_is_dropped_by_the_bulk_delete()
    {
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            db.MailRequests.Add(new MailRequest { Kind = MailKind.Signup, NormalizedEmail = "NOBODY@FIRMA.TEST", RequestedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        Assert.Equal(1, await DispatchAsync());

        Assert.Empty(Mail.Sent);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task A_failed_send_leaves_the_previous_link_valid_and_the_retry_sends_a_new_one()   // Review Focus 5
    {
        var first = await MailedAsync(company: "First");
        Clock.Advance(PastTheLimit);
        using var again = await SignupApi.Request(Client, Address, "Second");
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);

        Mail.Failing = true;
        Assert.Equal(1, await DispatchAsync());

        Assert.Single(Mail.Sent);   // nothing new went out
        var afterFailure = Assert.Single(await PendingAsync());
        Assert.Equal(EmailTokens.HashOf(first), afterFailure.TokenHash);   // the earlier link is still the one that works
        Assert.Equal("Second", afterFailure.CompanyName);
        Assert.Equal(1, await QueueCountAsync());

        Mail.Failing = false;
        Clock.Advance(TimeSpan.FromSeconds(5));   // the first retry
        Assert.Equal(1, await DispatchAsync());

        Assert.Equal(2, Mail.Sent.Count);
        var second = TokenIn(Mail.Sent[1]);
        Assert.NotEqual(first, second);
        Assert.Equal(EmailTokens.HashOf(second), Assert.Single(await PendingAsync()).TokenHash);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task A_failed_send_to_an_account_keeps_the_pending_sign_up_until_the_mail_is_sent()   // Review Focus 5
    {
        await CreateUserAsync(Address, confirmed: true, member: false);
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Mail.Failing = true;
        await DispatchAsync();
        Assert.Empty(Mail.Sent);
        Assert.Single(await PendingAsync());

        Mail.Failing = false;
        Clock.Advance(TimeSpan.FromSeconds(5));
        await DispatchAsync();

        Assert.StartsWith("You already have an account", Assert.Single(Mail.Sent).Subject, StringComparison.Ordinal);
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task Pending_sign_ups_are_invisible_to_login_and_forgot_password()   // criterion 15
    {
        using var request = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);

        using var pending = await LoginApi.Login(Client, Address, UserPassword);
        using var unknown = await LoginApi.Login(Client, "nobody@firma.test", UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, pending.StatusCode);
        Assert.Equal(unknown.StatusCode, pending.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await pending.Content.ReadAsStringAsync());
        Assert.Equal(LoginApi.HeaderNames(unknown), LoginApi.HeaderNames(pending));

        using var forgot = await AccountApi.Forgot(Client, Address);
        using var verify = await AccountApi.RequestVerification(Client, Address);
        Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, verify.StatusCode);
        await DispatchAsync();
        // Only the sign-up mail went out: the reset and the verification found no account.
        var mail = Assert.Single(Mail.Sent);
        Assert.StartsWith("Complete your sign-up", mail.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invitations_do_not_see_a_pending_sign_up()   // spec 0009 → Pending sign-ups
    {
        using var request = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, Address, "user");
        await EnqueueInvitationAsync(invite, Address);
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Single(m => m.Subject.StartsWith("You are invited", StringComparison.Ordinal)));

        using (var preview = await TenancyApi.Preview(Client, inviteToken))
        {
            var body = await TenancyApi.ReadOkAsync(preview);   // as for an address nobody has: no already_member, no invalid_token
            Assert.Equal("Acme", body.GetProperty("org_name").GetString());
        }

        using (var accepted = await TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd"))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        Assert.Single(await PendingAsync());   // the invitation neither used nor deleted it
    }
}

public sealed class SignupDispatchPolishTests : SignupTestBase
{
    public SignupDispatchPolishTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(MailSettingsLoader.LocaleKey, "pl");
    }

    [Fact]
    public async Task The_mails_are_in_the_configured_language()   // criterion 21
    {
        await MailedAsync(company: "Żółw i Spółka");
        await CreateUserAsync("ola@firma.test", confirmed: true, member: false);
        using var response = await SignupApi.Request(Client, "ola@firma.test", CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await DispatchAsync();

        var complete = Mail.Sent.Single(m => m.To == Address);
        Assert.Equal("Dokończ rejestrację w Auth-Core Test", complete.Subject);
        Assert.Contains("Żółw i Spółka", complete.TextBody, StringComparison.Ordinal);
        Assert.Contains("ważny przez 24 godziny", complete.TextBody, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"pl\">", complete.HtmlBody, StringComparison.Ordinal);
        var exists = Mail.Sent.Single(m => m.To == "ola@firma.test");
        Assert.Equal("Masz już konto w Auth-Core Test", exists.Subject);
        Assert.Contains("już istnieje", exists.TextBody, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail.** Today a `Signup` row is dropped as "no account" or composed as an account mail.

- [ ] **Step 3: The dispatcher.** In `src/Auth.Server/Email/MailDispatcher.cs` make these edits.

(a) `using Auth.Infrastructure.Persistence;` + `using Auth.Server.Tenancy;` become

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Signup;
using Auth.Server.Tenancy;
```

(b) at the end of the class summary, after the sentence that ends `... and the request is never dropped for want of an account.` add (a new line in the same `<summary>`):

```csharp
/// A sign-up request (spec 0009) names a pending sign-up, not an account, and is decided here: an address with an account gets the mail
/// that says so (no link) even when its pending sign-up is gone; otherwise a live pending sign-up gets a new link; with neither there is nothing to send.
```

(c) in `DropUnmailableAsync`, replace the block from `var now = StorableTime.Now(clock);` to the end of the `ExecuteSqlAsync` call:

```csharp
        var now = StorableTime.Now(clock);
        var passwordReset = (short)MailKind.PasswordReset;
        var invitation = (short)MailKind.Invitation;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM "MailRequests" WHERE "Id" IN (
                SELECT r."Id" FROM "MailRequests" r
                WHERE r."NextAttemptAt" <= {now}
                  AND r."Kind" <> {invitation}
                  AND NOT EXISTS (
                      SELECT 1 FROM "AspNetUsers" u
                      WHERE u."NormalizedEmail" = r."NormalizedEmail"
                        AND u."Email" IS NOT NULL
                        AND (r."Kind" = {passwordReset} OR NOT u."EmailConfirmed"))
                FOR UPDATE OF r SKIP LOCKED)
            """,
            cancellationToken);
```

with

```csharp
        var now = StorableTime.Now(clock);
        var passwordReset = (short)MailKind.PasswordReset;
        var invitation = (short)MailKind.Invitation;
        var signup = (short)MailKind.Signup;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM "MailRequests" WHERE "Id" IN (
                SELECT r."Id" FROM "MailRequests" r
                WHERE r."NextAttemptAt" <= {now}
                  AND r."Kind" <> {invitation}
                  AND ((r."Kind" = {signup}
                        AND NOT EXISTS (
                            SELECT 1 FROM "PendingSignups" p
                            WHERE p."NormalizedEmail" = r."NormalizedEmail" AND p."ExpiresAt" > {now})
                        AND NOT EXISTS (
                            SELECT 1 FROM "AspNetUsers" a
                            WHERE a."NormalizedEmail" = r."NormalizedEmail" AND a."Email" IS NOT NULL))
                       OR (r."Kind" <> {signup}
                           AND NOT EXISTS (
                               SELECT 1 FROM "AspNetUsers" u
                               WHERE u."NormalizedEmail" = r."NormalizedEmail"
                                 AND u."Email" IS NOT NULL
                                 AND (r."Kind" = {passwordReset} OR NOT u."EmailConfirmed"))))
                FOR UPDATE OF r SKIP LOCKED)
            """,
            cancellationToken);
```

(d) in `DispatchOneAsync`, replace

```csharp
        var passwordReset = (short)MailKind.PasswordReset;
        var invitation = (short)MailKind.Invitation;
        var due = await db.MailRequests
            .FromSql($"""
                SELECT r.* FROM "MailRequests" r
                WHERE (r."NextAttemptAt" <= {now}
                       AND (r."Kind" = {invitation}
                            OR EXISTS (
                                SELECT 1 FROM "AspNetUsers" u
                                WHERE u."NormalizedEmail" = r."NormalizedEmail"
                                  AND u."Email" IS NOT NULL
                                  AND (r."Kind" = {passwordReset} OR NOT u."EmailConfirmed"))))
                   OR r."RequestedAt" <= {givenUp}
```

with

```csharp
        var passwordReset = (short)MailKind.PasswordReset;
        var invitation = (short)MailKind.Invitation;
        var signup = (short)MailKind.Signup;
        var due = await db.MailRequests
            .FromSql($"""
                SELECT r.* FROM "MailRequests" r
                WHERE (r."NextAttemptAt" <= {now}
                       AND (r."Kind" = {invitation}
                            OR (r."Kind" = {signup}
                                AND (EXISTS (
                                         SELECT 1 FROM "PendingSignups" p
                                         WHERE p."NormalizedEmail" = r."NormalizedEmail" AND p."ExpiresAt" > {now})
                                     OR EXISTS (
                                         SELECT 1 FROM "AspNetUsers" a
                                         WHERE a."NormalizedEmail" = r."NormalizedEmail" AND a."Email" IS NOT NULL)))
                            OR (r."Kind" <> {signup}
                                AND EXISTS (
                                    SELECT 1 FROM "AspNetUsers" u
                                    WHERE u."NormalizedEmail" = r."NormalizedEmail"
                                      AND u."Email" IS NOT NULL
                                      AND (r."Kind" = {passwordReset} OR NOT u."EmailConfirmed")))))
                   OR r."RequestedAt" <= {givenUp}
```

(the lines after it, `ORDER BY ...` to `.ToListAsync(cancellationToken);`, stay).

(e) replace

```csharp
        var makeMail = request.Kind == MailKind.Invitation
            ? await PrepareInvitationAsync(db, request, now, cancellationToken)
            : await PrepareAccountMailAsync(db, request, now, cancellationToken);
```

with

```csharp
        var makeMail = request.Kind switch
        {
            MailKind.Invitation => await PrepareInvitationAsync(db, request, now, cancellationToken),
            MailKind.Signup => await PrepareSignupMailAsync(db, request, now, cancellationToken),
            _ => await PrepareAccountMailAsync(db, request, now, cancellationToken),
        };
```

(f) after `PrepareAccountMailAsync` (that is, after the text `            return composer.Compose(request.Kind, user.Email, token);\n        };\n    }`) add

```csharp

    /// <summary>
    /// The mail of a sign-up (spec 0009): decided here, when it is sent, and never by the request. The account check comes first: with an
    /// account under the address (any account) the mail says so and holds no link, even when the pending sign-up is gone, and any pending
    /// row of the address is deleted — in the transaction of the send, so a failed send keeps it. Without an account, the live pending
    /// sign-up (which plays the part the invitation plays for an invitation mail) is locked, so that a completion waits for the mail, and gets a
    /// new link token (the earlier link stops working); the mail goes to the address as typed. With no account and no live pending sign-up there
    /// is nothing to send.
    /// </summary>
    private async Task<Func<Task<ComposedMail>>?> PrepareSignupMailAsync(
        AuthDbContext db, MailRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = await StoredAddressOfAsync(db, request.NormalizedEmail, cancellationToken);
        if (stored is null)
        {
            var pending = await PendingSignups.LockUsableAsync(db, request.NormalizedEmail, now, cancellationToken);
            if (pending is not null)
            {
                return async () =>
                {
                    var token = await PendingSignups.IssueAsync(db, pending.NormalizedEmail, now, cancellationToken);
                    return composer.Compose(MailKind.Signup, pending.Email, token, pending.CompanyName);
                };
            }

            // The lock may have waited for a completion that has just committed: it made the account and removed the pending sign-up.
            stored = await StoredAddressOfAsync(db, request.NormalizedEmail, cancellationToken);
            if (stored is null)
            {
                return null;
            }
        }

        var address = stored;
        return async () =>
        {
            await PendingSignups.DeleteAsync(db, request.NormalizedEmail, cancellationToken);
            return composer.ComposeAccountExists(address);
        };
    }

    /// <summary>The address as the account under the normalised address stores it; <see langword="null"/> when there is no such account.</summary>
    private static async Task<string?> StoredAddressOfAsync(AuthDbContext db, string normalizedEmail, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking()
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);
        return user?.Email;
    }
```

- [ ] **Step 4: Run the tests.** `dotnet test tests/Auth.IntegrationTests --filter "FullyQualifiedName~SignupDispatch|FullyQualifiedName~MailDispatcher|FullyQualifiedName~InvitationDispatch|FullyQualifiedName~MailDispatchService"`; then the whole project (the older dispatcher tests, the flood test above all, must pass unchanged).

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(server): the queue mails a sign-up link, or says the account exists`.

---

## Day 3 — Wednesday 01.04: the preview, the completion, the shipped files

When the day is done, the whole flow works against the API: a visitor asks, opens the link, sees the preview, finishes with a password, and signs in as the first admin of a company of their own; the races are closed; and every file that ships (development settings, both manifests, the production compose file, the environment example, the sample overlay) knows about sign-up. Three tasks; every task leaves `dotnet build -warnaserror` and the tests of the files it touches green.

### Task 7: `POST /auth/signup/preview`

**Files:**
- Create: `src/Auth.Server/Signup/SignupService.cs`, `src/Auth.Server/Signup/SignupPreviewEndpoint.cs`
- Modify: `src/Auth.Server/Api/SignupRoutes.cs`, `src/Auth.Server/Program.cs`
- Modify (tests): `tests/Auth.IntegrationTests/OpenApiTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/SignupPreviewTests.cs`, `SignupPreviewDisabledTests.cs`

**Interfaces:**
- Consumes: Task 1 (`SignupGate`), Task 2 (`SignupApi`, `SignupPreviewRequest`, `SignupPreviewResponse`), Task 3 (`PendingSignups.FindUsableAsync`, `PendingSignupSeed`), Task 5 (`SignupTestBase`, `MailedAsync`), `AccountResults.InvalidToken/InvalidRequest`.
- Produces: `Auth.Server.Signup.SignupService` (scoped, registered in `Program.cs`) with `Task<SignupPreview?> PreviewAsync(string token, CancellationToken)` and `record SignupPreview(string Email, string CompanyName)`; Task 8 adds `CompleteAsync` to the same class. `SignupPreviewEndpoint.Path = "/auth/signup/preview"`.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/SignupPreviewTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;

namespace Auth.IntegrationTests;

public sealed class SignupPreviewTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    public static TheoryData<string> MalformedBodies => new()
    {
        "{}",
        """{"token":""}""",
        """{"token":"   "}""",
        """{"token":123}""",
        """{"token":null}""",
        """["token"]""",
        "not json",
        "",
    };

    [Fact]
    public async Task Preview_answers_the_typed_address_and_the_company_and_changes_nothing()   // criterion 8
    {
        var token = await MailedAsync("Anna@Firma.test", "Firma & Syn");
        var before = Assert.Single(await PendingAsync());
        var auditBefore = (await AuditAsync()).Count;

        for (var i = 0; i < 3; i++)
        {
            using var response = await SignupApi.Preview(Client, token);

            var body = await TenancyApi.ReadOkAsync(response);
            Assert.Equal(["company_name", "email"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal("Anna@Firma.test", body.GetProperty("email").GetString());
            Assert.Equal("Firma & Syn", body.GetProperty("company_name").GetString());
        }

        var after = Assert.Single(await PendingAsync());
        Assert.Equal(before.TokenHash, after.TokenHash);
        Assert.Equal(before.TokenExpiresAt, after.TokenExpiresAt);
        Assert.Equal(before.ExpiresAt, after.ExpiresAt);
        Assert.Equal(auditBefore, (await AuditAsync()).Count);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task Unknown_and_expired_tokens_are_one_400_invalid_token()   // criterion 8
    {
        var token = await MailedAsync();
        using var unknown = await SignupApi.Preview(Client, EmailTokens.NewToken());
        await AccountApi.AssertErrorAsync(unknown, AccountApi.InvalidToken);

        Clock.Advance(TimeSpan.FromHours(24));
        using var expired = await SignupApi.Preview(Client, token);
        await AccountApi.AssertErrorAsync(expired, AccountApi.InvalidToken);
    }

    [Fact]
    public async Task A_link_works_until_24_hours_after_the_mail_and_not_a_second_longer()   // criterion 13
    {
        var token = await MailedAsync();

        Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        using (var last = await SignupApi.Preview(Client, token))
        {
            Assert.Equal(HttpStatusCode.OK, last.StatusCode);
        }

        Clock.Advance(TimeSpan.FromSeconds(1));
        using var over = await SignupApi.Preview(Client, token);
        await AccountApi.AssertErrorAsync(over, AccountApi.InvalidToken);
    }

    [Fact]
    public async Task The_older_link_of_an_address_is_invalid_and_the_newer_shows_the_newer_company()   // criterion 7, Review Focus 3
    {
        var first = await MailedAsync(company: "First");
        Clock.Advance(PastTheLimit);
        var second = await MailedAsync(company: "Second");

        using var older = await SignupApi.Preview(Client, first);
        await AccountApi.AssertErrorAsync(older, AccountApi.InvalidToken);

        using var newer = await SignupApi.Preview(Client, second);
        var body = await TenancyApi.ReadOkAsync(newer);
        Assert.Equal("Second", body.GetProperty("company_name").GetString());
    }

    [Fact]
    public async Task A_link_of_another_kind_is_not_a_sign_up_link()
    {
        using var forgot = await AccountApi.Forgot(Client, Factory.SeedEmail);
        Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        await DispatchAsync();
        var resetToken = TokenIn(Assert.Single(Mail.Sent));

        using var response = await SignupApi.Preview(Client, resetToken);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task Malformed_bodies_are_400_invalid_request(string body)
    {
        using var response = await AccountApi.PostRaw(Client, SignupApi.PreviewPath, body);

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
    }
}
```

`tests/Auth.IntegrationTests/SignupPreviewDisabledTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class SignupPreviewDisabledTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static readonly string[] Bodies = ["""{"token":"x"}""", "not json", "", "{}"];

    [Theory]
    [MemberData(nameof(SignupVariants.Off), MemberType = typeof(SignupVariants))]
    public async Task Preview_off_answers_403_before_the_body_is_read(string variant)   // criterion 2
    {
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), variant);
        using var client = factory.CreateClient();

        foreach (var body in Bodies)
        {
            using var response = await AccountApi.PostRaw(client, SignupApi.PreviewPath, body);
            await SignupApi.AssertDisabledAsync(response);
        }
    }

    [Fact]
    public async Task A_preview_after_sign_up_was_turned_off_is_403_and_changes_nothing()   // spec 0009 → General rules
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        await using var on = new AuthAppFactory(postgres, keys, database).WithSignup();
        var token = await PendingSignupSeed.IssueAsync(on.Services, "anna@firma.test", "Firma");

        await using var off = new AuthAppFactory(postgres, keys, database);   // the same database, the manifest of v0.1.0 again
        using var client = off.CreateClient();
        using var response = await SignupApi.Preview(client, token);

        await SignupApi.AssertDisabledAsync(response);
        using var scope = off.Services.CreateScope();
        var pending = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().PendingSignups.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EmailTokens.HashOf(token), pending.TokenHash);
    }
}
```

In `tests/Auth.IntegrationTests/OpenApiTests.cs` (lines added): in `Endpoints` replace `        "POST /auth/signup",` with

```csharp
        "POST /auth/signup",
        "POST /auth/signup/preview",
```

after `    [InlineData("POST /auth/signup", "202,400,403,415,429")]` add

```csharp
    [InlineData("POST /auth/signup/preview", "200,400,403,415,429")]
```

after `    [InlineData("POST /auth/signup", "company_name,email")]` add

```csharp
    [InlineData("POST /auth/signup/preview", "token")]
```

and after `        Assert.Equal(["signup"], Properties("OptionsResponse"));` add

```csharp
        Assert.Equal(["company_name", "email"], Properties("SignupPreviewResponse"));
```

- [ ] **Step 2: Run the tests to see them fail** (`404`, and `SignupService` does not exist).

- [ ] **Step 3: The service and the endpoint.** `src/Auth.Server/Signup/SignupService.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;

namespace Auth.Server.Signup;

/// <summary>What the completion screen shows before it asks for a password: the address as it was typed and the company name of the request.</summary>
public sealed record SignupPreview(string Email, string CompanyName);

/// <summary>
/// Previews and completes a sign-up by its link token (spec 0009). Completing is how a visitor becomes the first admin of a company of their
/// own; it is added in the next task.
/// </summary>
public sealed class SignupService(AuthDbContext db, TimeProvider clock)
{
    /// <summary>
    /// The address and the company name of a usable link; the token stays usable. Unknown, used, expired and replaced tokens are all
    /// <see langword="null"/>. It changes nothing.
    /// </summary>
    public async Task<SignupPreview?> PreviewAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var pending = await PendingSignups.FindUsableAsync(db, EmailTokens.HashOf(token), StorableTime.Now(clock), cancellationToken);
        return pending is null ? null : new SignupPreview(pending.Email, pending.CompanyName);
    }
}
```

`src/Auth.Server/Signup/SignupPreviewEndpoint.cs`:

```csharp
using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Requests;
using Auth.Server.Tenancy;

namespace Auth.Server.Signup;

/// <summary>
/// <c>POST /auth/signup/preview</c> (spec 0009): what the link of a mail is for. No access token: the link token is the credential. While
/// sign-up is off it answers <c>403 signup_disabled</c> before it reads the body.
/// </summary>
public static class SignupPreviewEndpoint
{
    public const string Path = "/auth/signup/preview";

    private static readonly string[] Fields = ["token"];

    public static async Task<IResult> HandleAsync(HttpContext http, SignupGate gate, SignupService signup)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(signup);

        if (!gate.IsOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, http.RequestAborted);
        if (fields is null)
        {
            return AccountResults.InvalidRequest();
        }

        var preview = await signup.PreviewAsync(fields[0], http.RequestAborted);
        return preview is null
            ? AccountResults.InvalidToken()
            : ApiResults.Ok(new SignupPreviewResponse(preview.Email, preview.CompanyName));
    }
}
```

In `src/Auth.Server/Api/SignupRoutes.cs`, before `        return app;` add

```csharp
        app.MapPost(SignupPreviewEndpoint.Path, SignupPreviewEndpoint.HandleAsync)
            .WithTags("Sign-up")
            .WithDescription(
                "Shows what the link of the mail is for: the address as it was typed and the company name of the request. It changes nothing "
                + "and the link stays usable. A token that is unknown, used, expired or replaced by a newer mail is `invalid_token`.")
            .ReadsJson<SignupPreviewRequest>()
            .Produces<SignupPreviewResponse>()
            .ProducesError(StatusCodes.Status400BadRequest, AccountResults.InvalidRequestError, AccountResults.InvalidTokenError)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.SignupDisabled);

```

In `src/Auth.Server/Program.cs`, after `builder.Services.AddSingleton<SignupGate>();` add `builder.Services.AddScoped<SignupService>();`.

- [ ] **Step 4: Run the tests.** `dotnet test tests/Auth.IntegrationTests --filter "FullyQualifiedName~SignupPreview|FullyQualifiedName~OpenApi"`; then the whole project.

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(server): POST /auth/signup/preview`.

### Task 8: `POST /auth/signup/complete`

**Files:**
- Modify: `src/Auth.Server/Signup/SignupService.cs` (replaced as a whole), `src/Auth.Server/Api/SignupRoutes.cs`
- Create: `src/Auth.Server/Signup/SignupCompleteEndpoint.cs`
- Modify (tests): `tests/Auth.IntegrationTests/OpenApiTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/SignupCompleteTests.cs`, `SignupFirstAdminTests.cs`, `SignupCompleteRaceTests.cs`, `SignupCompleteDisabledTests.cs`

**Interfaces:**
- Consumes: Task 1 (`Manifest.FirstAdminRole`, `SignupGate`, `TenancyErrors.AlreadyRegistered`), Task 3 (`PendingSignups.ConsumeAsync`, `PendingSignupSeed`), Task 5 (`AuditKinds.SignupCompleted`, `SignupTestBase`), Task 7 (`SignupService`), `CompanyService.CreateAsync(name, ct, via)`, `PasswordRules.BrokenAsync`, `AccountSessions.EndAllAsync/Ensure`, `InviteAcceptance` (the advisory lock below is the one it takes).
- Produces: `SignupService.CompleteAsync(string token, string password, string companyName, CancellationToken)` returning `Outcome<IReadOnlyList<string>>` (the broken rules as the value of `TenancyErrors.WeakPassword`; the errors `InvalidToken`, `WeakPassword`, `AlreadyRegistered`); `SignupCompleteEndpoint.Path = "/auth/signup/complete"`.

**The order inside the transaction** (spec: token, account, password, as its Amendment 4 settled it; one transaction; one address lock). It begins by reading which address the token is for, takes `pg_advisory_xact_lock(hashtextextended(<normalised address>, 0))` — the statement `InviteAcceptance` uses, so a completion and an acceptance for one address never both create an account — then consumes the pending sign-up with one `DELETE ... RETURNING` (of parallel completions with one token one gets the row; the others find none after the lock), checks for an account under the address (found: commit, so that the pending sign-up is gone, and `409 already_registered`), checks the password (a weak one leaves the method without a commit: the delete is rolled back and the token stays usable), and only then creates the account (email confirmed), ends its sessions and clears its login streak (as an invitation does), creates the company with `CompanyService.CreateAsync(name, ct, via: "signup")` (which stages `org.created`), adds the membership with the first admin's role (read from the company's own copy by normalised name) and stages `signup.completed`. One save, one commit.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/SignupCompleteTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class SignupCompleteTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    // Spelled out, so that the escape written into a request body is not read as an escape of this file.
    private const string Backslash = "\\";
    private const string WeakRules = """{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}""";

    public static TheoryData<string> MalformedBodies => new()
    {
        """{"token":"TOKEN","password":"Chosen-Passw0rd"}""",
        """{"token":"TOKEN","company_name":"Firma"}""",
        """{"password":"Chosen-Passw0rd","company_name":"Firma"}""",
        """{"token":"TOKEN","password":"Chosen-Passw0rd","company_name":""}""",
        """{"token":"TOKEN","password":"Chosen-Passw0rd","company_name":"   "}""",
        """{"token":"TOKEN","password":"","company_name":"Firma"}""",
        """{"token":"TOKEN","password":123,"company_name":"Firma"}""",
        """{"token":"TOKEN","password":"Chosen-Passw0rd","company_name":null}""",
        """{"token":"TOKEN","password":"Chosen-Passw0rd","company_name":" Firma"}""",
        """{"token":"TOKEN","password":"Chosen-Passw0rd","company_name":"Firma "}""",
        "{\"token\":\"TOKEN\",\"password\":\"Chosen-Passw0rd\",\"company_name\":\"" + new string('x', 101) + "\"}",
        "{\"token\":\"TOKEN\",\"password\":\"Chosen" + Backslash + "u0000Passw0rd\",\"company_name\":\"Firma\"}",   // NUL in the password
        "{\"token\":\"TOKEN\",\"password\":\"Chosen-Passw0rd\",\"company_name\":\"Fi" + Backslash + "u0001rma\"}",   // a control character
        """["TOKEN"]""",
        "not json",
        "",
    };

    private Task<int> CountAsync<T>(Func<AuthDbContext, IQueryable<T>> set)
        where T : class =>
        InDbAsync(db => set(db).CountAsync(TestContext.Current.CancellationToken));

    private Task<int> UsersWithAsync(string email) =>
        InDbAsync(db => db.Users.CountAsync(u => u.Email == email, TestContext.Current.CancellationToken));

    private Task<int> CompaniesAsync() => CountAsync(db => db.Companies);

    [Fact]
    public async Task A_visitor_signs_up_finishes_and_logs_in_as_the_admin_of_a_company_of_their_own()   // criteria 1, 3, 8, 9
    {
        using (var options = await SignupApi.Options(Client))
        {
            Assert.Equal("""{"signup":"self-service"}""", await options.Content.ReadAsStringAsync());
        }

        var token = await MailedAsync("Anna@Firma.test");
        using (var preview = await SignupApi.Preview(Client, token))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        }

        using (var done = await SignupApi.Complete(Client, token, Password, CompanyName))
        {
            await AccountApi.AssertEmptyAsync(done, HttpStatusCode.NoContent);   // 204, never stored, and no cookie: it does not sign in
        }

        var session = await SessionApi.LoginAsync(Client, "Anna@Firma.test", Password);
        var company = await InDbAsync(db => db.Companies.Where(c => c.Name == CompanyName).Select(c => c.Id).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(
            ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task The_account_company_roles_and_membership_are_what_the_contract_says()   // criterion 9
    {
        var token = await MailedAsync("Anna@Firma.test");

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);

        var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken));
        Assert.True(user.EmailConfirmed);
        Assert.Equal("Anna@Firma.test", user.Email);
        Assert.Equal("Anna@Firma.test", user.UserName);
        Assert.NotNull(user.PasswordHash);

        Assert.Equal(2, await CompaniesAsync());   // the development company and this one
        var company = await InDbAsync(db => db.Companies.AsNoTracking().SingleAsync(c => c.Name == CompanyName, TestContext.Current.CancellationToken));
        var roles = await InDbAsync(db => db.CompanyRoles.AsNoTracking().Where(r => r.CompanyId == company.Id).OrderBy(r => r.Name).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["admin", "user"], roles.Select(r => r.Name));
        Assert.Equal(["*"], roles[0].Permissions);
        Assert.Equal(["reports:read", "reports:approve"], roles[1].Permissions);

        var membership = Assert.Single(await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.CompanyId == company.Id).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(roles[0].Id, membership.RoleId);   // admin
        Assert.Empty(await PendingAsync());
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task The_company_name_of_the_request_is_only_a_suggestion()   // Review Focus 4
    {
        var token = await MailedAsync(company: CompanyName);

        using var done = await SignupApi.Complete(Client, token, Password, "Żółw & Spółka <Ltd>");
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);

        Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == CompanyName, TestContext.Current.CancellationToken)));
        var company = await InDbAsync(db => db.Companies.AsNoTracking().SingleAsync(c => c.Name == "Żółw & Spółka <Ltd>", TestContext.Current.CancellationToken));
        var session = await SessionApi.LoginAsync(Client, Address, Password);
        Assert.Equal(company.Id.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
    }

    [Fact]
    public async Task A_weak_password_names_the_rules_creates_nothing_and_leaves_the_token_usable()   // criterion 11
    {
        var token = await MailedAsync();
        var before = Assert.Single(await PendingAsync());

        using (var weak = await SignupApi.Complete(Client, token, "short", CompanyName))
        {
            await AccountApi.AssertErrorAsync(weak, WeakRules);
        }

        Assert.Equal(0, await UsersWithAsync(Address));
        Assert.Equal(1, await CompaniesAsync());   // only the development company
        var after = Assert.Single(await PendingAsync());
        Assert.Equal(before.TokenHash, after.TokenHash);
        using var preview = await SignupApi.Preview(Client, token);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var good = await SignupApi.Complete(Client, token, Password, CompanyName);
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
    }

    [Fact]
    public async Task The_order_of_the_checks_is_token_then_password_then_account()   // spec 0009 → To verify
    {
        await CreateUserAsync(Address, confirmed: true, member: false);
        var token = await PendingSignupSeed.IssueAsync(Factory.Services, Address, CompanyName);

        using (var unknown = await SignupApi.Complete(Client, EmailTokens.NewToken(), "short", CompanyName))
        {
            await AccountApi.AssertErrorAsync(unknown, AccountApi.InvalidToken);   // the token first
        }

        using (var weak = await SignupApi.Complete(Client, token, "short", CompanyName))
        {
            await AccountApi.AssertErrorAsync(weak, WeakRules);   // then the password, whether or not an account exists
        }

        Assert.Single(await PendingAsync());   // a weak password consumed nothing

        using var good = await SignupApi.Complete(Client, token, Password, CompanyName);
        await SignupApi.AssertAlreadyRegisteredAsync(good);   // then the account
        Assert.Empty(await PendingAsync());
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task Malformed_bodies_are_400_invalid_request_and_leave_the_token_usable(string template)   // criterion 9
    {
        var token = await MailedAsync();

        using var response = await AccountApi.PostRaw(Client, SignupApi.CompletePath, template.Replace("TOKEN", token, StringComparison.Ordinal));

        await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        Assert.Equal(0, await UsersWithAsync(Address));
        Assert.Equal(1, await CompaniesAsync());
        using var good = await SignupApi.Complete(Client, token, Password, CompanyName);
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
    }

    [Fact]
    public async Task A_used_unknown_expired_or_replaced_token_is_400_invalid_token()   // criteria 12, 13
    {
        // Used.
        var used = await MailedAsync("used@firma.test");
        using (var first = await SignupApi.Complete(Client, used, Password, CompanyName))
        {
            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        }

        using (var second = await SignupApi.Complete(Client, used, Password, CompanyName))
        {
            await AccountApi.AssertErrorAsync(second, AccountApi.InvalidToken);
        }

        using (var preview = await SignupApi.Preview(Client, used))
        {
            await AccountApi.AssertErrorAsync(preview, AccountApi.InvalidToken);
        }

        Assert.Equal(1, await UsersWithAsync("used@firma.test"));

        // Unknown.
        using (var unknown = await SignupApi.Complete(Client, EmailTokens.NewToken(), Password, CompanyName))
        {
            await AccountApi.AssertErrorAsync(unknown, AccountApi.InvalidToken);
        }

        // Replaced by a newer mail.
        var older = await MailedAsync("replaced@firma.test", "First");
        Clock.Advance(PastTheLimit);
        var newer = await MailedAsync("replaced@firma.test", "Second");
        using (var replaced = await SignupApi.Complete(Client, older, Password, CompanyName))
        {
            await AccountApi.AssertErrorAsync(replaced, AccountApi.InvalidToken);
        }

        Assert.Equal(0, await UsersWithAsync("replaced@firma.test"));
        using (var current = await SignupApi.Complete(Client, newer, Password, "Second"))
        {
            Assert.Equal(HttpStatusCode.NoContent, current.StatusCode);
        }

        // Expired: 24 hours after the mail.
        var expiring = await MailedAsync("expired@firma.test");
        Clock.Advance(TimeSpan.FromHours(24));
        using var expired = await SignupApi.Complete(Client, expiring, Password, CompanyName);
        await AccountApi.AssertErrorAsync(expired, AccountApi.InvalidToken);
        Assert.Equal(0, await UsersWithAsync("expired@firma.test"));
    }

    [Fact]
    public async Task An_account_made_in_the_meantime_is_409_already_registered_and_the_pending_sign_up_is_gone()   // criterion 14
    {
        var signupToken = await MailedAsync();
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, Address, "user");
        await EnqueueInvitationAsync(invite, Address);
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == Address));
        using (var accepted = await TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd"))
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        }

        var companiesBefore = await CompaniesAsync();
        using var done = await SignupApi.Complete(Client, signupToken, Password, "Never Made");

        await SignupApi.AssertAlreadyRegisteredAsync(done);
        Assert.Equal(companiesBefore, await CompaniesAsync());   // creates no company
        Assert.Empty(await PendingAsync());
        using var again = await SignupApi.Complete(Client, signupToken, Password, "Never Made");
        await AccountApi.AssertErrorAsync(again, AccountApi.InvalidToken);
        // The account is the invited one, with the invited password and company.
        var session = await SessionApi.LoginAsync(Client, Address, "Invited-Passw0rd");
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
    }

    [Fact]
    public async Task An_account_under_another_spelling_of_the_address_is_409_too()   // Review Focus 2
    {
        await CreateUserAsync("ANNA@Firma.TEST", confirmed: true, member: false);
        var token = await PendingSignupSeed.IssueAsync(Factory.Services, "anna@firma.test", CompanyName);

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);

        await SignupApi.AssertAlreadyRegisteredAsync(done);
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken)));
        Assert.Equal(1, await CompaniesAsync());
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task Failed_logins_before_the_sign_up_do_not_lock_the_new_account()   // the streak of an address ends with its account's creation, as an invitation's
    {
        var token = await MailedAsync();
        await LockoutApi.FailAsync(Client, Clock, Address, 5);

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);

        _ = await SessionApi.LoginAsync(Client, Address, Password);
    }

    [Fact]
    public async Task Nothing_is_left_when_the_audit_row_cannot_be_written()   // criterion 19: the change and its rows, both or neither
    {
        var token = await MailedAsync();
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);

        Assert.Equal(HttpStatusCode.InternalServerError, done.StatusCode);
        Assert.Equal(0, await UsersWithAsync(Address));
        Assert.Equal(1, await CompaniesAsync());
        Assert.Equal(0, await InDbAsync(db => db.Memberships.CountAsync(
            m => db.Companies.Any(c => c.Id == m.CompanyId && c.Name == CompanyName), TestContext.Current.CancellationToken)));
        var pending = Assert.Single(await PendingAsync());
        Assert.Equal(EmailTokens.HashOf(token), pending.TokenHash);   // the use of the token was rolled back with the rest
    }

    [Fact]
    public async Task A_completion_is_recorded_with_the_account_the_company_and_the_address_and_nothing_secret()   // criterion 19
    {
        var token = await MailedAsync("Anna@Firma.test");

        using var done = await RateLimitApi.SendAsync(
            Client, HttpMethod.Post, SignupApi.CompletePath, CallerAddress, null,
            $$"""{"token":"{{token}}","password":"{{Password}}","company_name":"{{CompanyName}}"}""");
        await AccountApi.AssertEmptyAsync(done, HttpStatusCode.NoContent);

        var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken));
        var company = await InDbAsync(db => db.Companies.AsNoTracking().SingleAsync(c => c.Name == CompanyName, TestContext.Current.CancellationToken));
        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal(user.Id, completed.SubjectUserId);
        Assert.Equal("Anna@Firma.test", completed.SubjectEmail);
        Assert.Equal(company.Id, completed.OrgId);
        Assert.Equal(CompanyName, completed.OrgName);
        Assert.Equal(CallerAddress, completed.ClientIp);
        Assert.Null(completed.ActorUserId);
        Assert.Null(completed.TargetId);
        Assert.Null(completed.Details);

        var created = Assert.Single(await AuditAsync(AuditKinds.OrgCreated), row => row.OrgId == company.Id);
        Assert.Equal(CompanyName, created.OrgName);
        Assert.Equal("signup", AuditApi.Text(created, "via"));
        Assert.Equal(["via"], AuditApi.DetailNames(created));
        await AssertNoSecretsAsync(Password, token);
    }
}
```

`tests/Auth.IntegrationTests/SignupFirstAdminTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

public sealed class SignupFirstAdminTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string Password = "Chosen-Passw0rd";

    /// <summary>A host with this manifest (it must say <c>signup: self-service</c>), a completed sign-up, and the roles and permissions of the first admin's token.</summary>
    private async Task<(string[] Roles, string[] Permissions)> FirstAdminAsync(string manifest)
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSignup().WithManifest(manifest);
        using var client = SessionApi.CreateClient(factory);
        var token = await PendingSignupSeed.IssueAsync(factory.Services, "boss@firma.test", "Firma");

        using var done = await SignupApi.Complete(client, token, Password, "Firma");
        Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);

        var session = await SessionApi.LoginAsync(client, "boss@firma.test", Password);
        return (AccessTokens.Array(session.AccessToken, "roles"), AccessTokens.Array(session.AccessToken, "permissions"));
    }

    [Fact]
    public async Task The_first_admin_gets_the_first_default_role_that_holds_the_star()   // criterion 10
    {
        var (roles, permissions) = await FirstAdminAsync("""
            permissions: [docs:read]
            signup: self-service
            default_roles:
              viewer: [docs:read]
              boss: ["*"]
              admin: ["*"]
            """);

        Assert.Equal(["boss"], roles);
        Assert.Contains("members:manage", permissions);
        Assert.Contains("docs:read", permissions);
    }

    [Fact]
    public async Task Without_a_star_the_first_admin_gets_the_first_role_that_holds_members_manage()   // criterion 10
    {
        var (roles, permissions) = await FirstAdminAsync("""
            permissions: [docs:read]
            signup: self-service
            default_roles:
              viewer: [docs:read]
              lead: [members:manage, docs:read]
              deputy: [members:manage, roles:manage]
            """);

        Assert.Equal(["lead"], roles);
        Assert.Equal(["docs:read", "members:manage"], permissions);
    }

    [Fact]
    public async Task A_star_role_after_a_members_manage_role_still_wins()   // criterion 10
    {
        var (roles, _) = await FirstAdminAsync("""
            permissions: [docs:read]
            signup: self-service
            default_roles:
              lead: [members:manage]
              owner: ["*"]
            """);

        Assert.Equal(["owner"], roles);
    }
}
```

`tests/Auth.IntegrationTests/SignupCompleteRaceTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

public sealed class SignupCompleteRaceTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    [Fact]
    public async Task Of_parallel_completions_with_one_token_exactly_one_is_204()   // criterion 12, Review Focus 3
    {
        var token = await MailedAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SignupApi.Complete(Client, token, Password, CompanyName)));

        try
        {
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
            var refused = responses.Where(r => r.StatusCode != HttpStatusCode.NoContent).ToList();
            Assert.Equal(7, refused.Count);
            foreach (var response in refused)
            {
                await AccountApi.AssertErrorAsync(response, AccountApi.InvalidToken);
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Address, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == CompanyName, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(
            m => db.Companies.Any(c => c.Id == m.CompanyId && c.Name == CompanyName), TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_completion_and_an_invitation_acceptance_for_one_address_never_both_create_an_account()   // criterion 14
    {
        var company = await CreateCompanyAsync("Acme");

        for (var round = 0; round < 6; round++)
        {
            var address = $"round{round}@firma.test";
            var name = $"Firma {round}";
            var signupToken = await MailedAsync(address, name);
            var invite = await AddInviteAsync(company, address, "user");
            await EnqueueInvitationAsync(invite, address);
            await DispatchAsync();
            var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == address));

            var complete = SignupApi.Complete(Client, signupToken, Password, name);
            var accept = TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd");
            await Task.WhenAll(complete, accept);
            using var completed = await complete;
            using var accepted = await accept;

            var outcome = (completed.StatusCode, accepted.StatusCode);
            Assert.True(
                outcome is (HttpStatusCode.NoContent, HttpStatusCode.Conflict) or (HttpStatusCode.Conflict, HttpStatusCode.NoContent),
                $"Round {round}: completion {(int)completed.StatusCode}, acceptance {(int)accepted.StatusCode}.");
            if (completed.StatusCode == HttpStatusCode.NoContent)
            {
                Assert.Equal("""{"error":"already_member"}""", await accepted.Content.ReadAsStringAsync());
                Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == name, TestContext.Current.CancellationToken)));
            }
            else
            {
                await SignupApi.AssertAlreadyRegisteredAsync(completed);
                Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == name, TestContext.Current.CancellationToken)));
            }

            var normalized = address.ToUpperInvariant();
            var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == normalized, TestContext.Current.CancellationToken));
            Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == user.Id, TestContext.Current.CancellationToken)));
            Assert.Empty((await PendingAsync()).Where(p => p.NormalizedEmail == normalized));
        }
    }

    [Fact]
    public async Task A_mail_sent_while_the_completion_runs_never_leaves_a_working_link_to_a_used_sign_up()   // the dispatcher locks the pending sign-up and looks the account up again
    {
        var first = await MailedAsync();
        Clock.Advance(PastTheLimit);
        using (var again = await SignupApi.Request(Client, Address, CompanyName))
        {
            Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);   // a second mail is queued
        }

        var complete = SignupApi.Complete(Client, first, Password, CompanyName);
        var dispatch = DispatchAsync();
        await Task.WhenAll(complete, dispatch);
        using var done = await complete;

        if (done.StatusCode == HttpStatusCode.NoContent)
        {
            // The sign-up is used: the queued mail found the account and said so (the account check comes first), with no link.
            Assert.Equal(2, Mail.Sent.Count);
            Assert.StartsWith("You already have an account", Mail.Sent[1].Subject, StringComparison.Ordinal);
            Assert.DoesNotContain("token=", Mail.Sent[1].TextBody, StringComparison.Ordinal);
            Assert.Empty(await PendingAsync());
        }
        else
        {
            // The mail replaced the link first: the old token is refused, and the new one is the one that works, once.
            await AccountApi.AssertErrorAsync(done, AccountApi.InvalidToken);
            Assert.Equal(2, Mail.Sent.Count);
            using var second = await SignupApi.Complete(Client, TokenIn(Mail.Sent[1]), Password, CompanyName);
            Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        }

        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Address, TestContext.Current.CancellationToken)));
    }
}
```

`tests/Auth.IntegrationTests/SignupCompleteDisabledTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

public sealed class SignupCompleteDisabledTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static readonly string[] Bodies =
    [
        """{"token":"x","password":"Chosen-Passw0rd","company_name":"Firma"}""",
        "not json",
        "",
        "{}",
    ];

    [Theory]
    [MemberData(nameof(SignupVariants.Off), MemberType = typeof(SignupVariants))]
    public async Task Complete_off_answers_403_before_the_body_is_read_and_creates_nothing(string variant)   // criterion 2
    {
        await using var factory = SignupVariants.Apply(new AuthAppFactory(postgres, keys), variant);
        using var client = factory.CreateClient();

        foreach (var body in Bodies)
        {
            using var response = await AccountApi.PostRaw(client, SignupApi.CompletePath, body);
            await SignupApi.AssertDisabledAsync(response);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.Equal(1, await db.Companies.CountAsync(TestContext.Current.CancellationToken));   // the development company only
        Assert.Equal(0, await db.PendingSignups.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_completion_after_sign_up_was_turned_off_is_403_and_consumes_nothing()   // spec 0009 → General rules
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        await using var on = new AuthAppFactory(postgres, keys, database).WithSignup();
        var token = await PendingSignupSeed.IssueAsync(on.Services, "anna@firma.test", "Firma");

        await using var off = new AuthAppFactory(postgres, keys, database);   // the same database, the manifest of v0.1.0 again
        using var client = off.CreateClient();
        using var response = await SignupApi.Complete(client, token, "Chosen-Passw0rd", "Firma");

        await SignupApi.AssertDisabledAsync(response);
        using var scope = off.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var pending = await db.PendingSignups.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EmailTokens.HashOf(token), pending.TokenHash);
        Assert.Equal(0, await db.Users.CountAsync(u => u.Email == "anna@firma.test", TestContext.Current.CancellationToken));
    }
}
```

In `tests/Auth.IntegrationTests/OpenApiTests.cs` (lines added): in `Endpoints` replace `        "POST /auth/signup/preview",` with

```csharp
        "POST /auth/signup/preview",
        "POST /auth/signup/complete",
```

after `    [InlineData("POST /auth/signup/preview", "200,400,403,415,429")]` add

```csharp
    [InlineData("POST /auth/signup/complete", "204,400,403,409,415,429")]
```

after `    [InlineData("POST /auth/signup/preview", "token")]` add

```csharp
    [InlineData("POST /auth/signup/complete", "company_name,password,token")]
```

in `The_error_codes_of_the_contract_are_in_the_descriptions`, after the line `        Assert.Contains("not_found", Description(operations["DELETE /auth/org/invites/{id}"], "404"));` add

```csharp
        Assert.All(
            ["POST /auth/signup", "POST /auth/signup/preview", "POST /auth/signup/complete"],
            endpoint => Assert.Contains("signup_disabled", Description(operations[endpoint], "403")));
        Assert.All(["invalid_request", "invalid_token", "weak_password"], code => Assert.Contains(code, Description(operations["POST /auth/signup/complete"], "400")));
        Assert.Contains("already_registered", Description(operations["POST /auth/signup/complete"], "409"));
        Assert.Contains("invalid_token", Description(operations["POST /auth/signup/preview"], "400"));
        Assert.Contains("too_many_attempts", Description(operations["POST /auth/signup"], "429"));
```

and in `The_reset_and_accept_answers_name_the_password_rules` replace `foreach (var endpoint in new[] { "POST /auth/password/reset", "POST /auth/invites/accept" })` with

```csharp
        foreach (var endpoint in new[] { "POST /auth/password/reset", "POST /auth/invites/accept", "POST /auth/signup/complete" })
```

- [ ] **Step 2: Run the tests to see them fail** (`404` for the endpoint; `CompleteAsync` does not exist).

- [ ] **Step 3: The service.** Replace `src/Auth.Server/Signup/SignupService.cs`:

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

/// <summary>What the completion screen shows before it asks for a password: the address as it was typed and the company name of the request.</summary>
public sealed record SignupPreview(string Email, string CompanyName);

/// <summary>
/// Previews and completes a sign-up by its link token (spec 0009). Completing is how a visitor becomes the first admin of a company of their
/// own, and it is one transaction: it uses the pending sign-up up, makes the account (email confirmed), the company with a copy of the
/// default roles and the membership with the first admin's role, and writes the audit rows — or, when anything is refused, changes
/// nothing at all, the pending sign-up included (except that an account found under the address deletes it).
/// </summary>
public sealed class SignupService(
    AuthDbContext db, UserManager<ApplicationUser> users, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations,
    CompanyService companies, ManifestHolder manifest, TimeProvider clock, AuditLog audit)
{
    /// <summary>
    /// The address and the company name of a usable link; the token stays usable. Unknown, used, expired and replaced tokens are all
    /// <see langword="null"/>. It changes nothing.
    /// </summary>
    public async Task<SignupPreview?> PreviewAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var pending = await PendingSignups.FindUsableAsync(db, EmailTokens.HashOf(token), StorableTime.Now(clock), cancellationToken);
        return pending is null ? null : new SignupPreview(pending.Email, pending.CompanyName);
    }

    /// <summary>
    /// Completes the sign-up. Refused with <c>invalid_token</c>, with <c>weak_password</c> (the broken rules are the value; the token stays
    /// usable) or with <c>already_registered</c> (an account has the address by now: the pending sign-up is deleted). The checks are
    /// made in that order. The caller has checked <paramref name="companyName"/> against the name rule and <paramref name="password"/> for NUL.
    /// </summary>
    public async Task<Outcome<IReadOnlyList<string>>> CompleteAsync(
        string token, string password, string companyName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(companyName);

        var hash = EmailTokens.HashOf(token);
        var now = StorableTime.Now(clock);

        // Leaving this method without a commit rolls everything back, the use of the token included.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Which address is this link for? Read first, because the address is what is locked.
        var address = await db.PendingSignups.AsNoTracking()
            .Where(p => p.TokenHash == hash && p.TokenExpiresAt > now)
            .Select(p => p.NormalizedEmail)
            .FirstOrDefaultAsync(cancellationToken);
        if (address is null)
        {
            return Refused(TenancyErrors.InvalidToken);
        }

        // One address, one account-maker at a time: the lock that invitation acceptance takes, so that a completion and an acceptance
        // for one address never both create the account (the tables would allow it; this version does not).
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({address}, 0))", cancellationToken);

        // The token is used up by one statement: of parallel completions with one token one gets the row, and the others, which waited
        // for the lock, find nothing.
        var pending = await PendingSignups.ConsumeAsync(db, hash, now, cancellationToken);
        if (pending is null)
        {
            return Refused(TenancyErrors.InvalidToken);
        }

        var account = new ApplicationUser { UserName = pending.Email, Email = pending.Email, EmailConfirmed = true };
        var broken = await PasswordRules.BrokenAsync(users, account, password);
        if (broken.Count > 0)
        {
            return new Outcome<IReadOnlyList<string>>(broken, TenancyErrors.WeakPassword);
        }

        // Any account under the address, whatever its spelling or state: the lookup is by the normalised address, as the unique index is.
        if (await db.Users.AsNoTracking().AnyAsync(u => u.NormalizedEmail == pending.NormalizedEmail, cancellationToken))
        {
            // Committed, not rolled back: the pending sign-up is gone, as the contract says.
            await transaction.CommitAsync(CancellationToken.None);
            return Refused(TenancyErrors.AlreadyRegistered);
        }

        // The link came through the mailbox of the address: that proves it, and it is why the email is confirmed and a password is set here.
        AccountSessions.Ensure(await users.CreateAsync(account, password), "Could not set up the account");
        // A new account has no sessions or links yet, but its address may have a login streak: streaks do not need an account.
        await AccountSessions.EndAllAsync(db, tokens, authorizations, account, cancellationToken);

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

        // The person who holds the link is anonymous: no actor. Who, where and what company; never the link or the password.
        audit.Stage(new AuditEntry
        {
            Kind = AuditKinds.SignupCompleted,
            SubjectUserId = account.Id,
            SubjectEmail = account.Email,
            OrgId = created.Value,
            OrgName = companyName,
        });
        await db.SaveChangesAsync(CancellationToken.None);

        // Not the request's token: a client that goes away now must not leave the outcome open.
        await transaction.CommitAsync(CancellationToken.None);
        return new Outcome<IReadOnlyList<string>>([]);
    }

    private static Outcome<IReadOnlyList<string>> Refused(string error) => new(null, error);
}
```

- [ ] **Step 4: The endpoint.** `src/Auth.Server/Signup/SignupCompleteEndpoint.cs`:

```csharp
using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Requests;
using Auth.Server.Tenancy;

namespace Auth.Server.Signup;

/// <summary>
/// <c>POST /auth/signup/complete</c> (spec 0009): finishes a sign-up. No access token: the link token is the credential. While sign-up is
/// off it answers <c>403 signup_disabled</c> before it reads the body — also for a request that was made while it was on. The person is not
/// signed in: the product's screen sends them to its sign-in screen.
/// </summary>
public static class SignupCompleteEndpoint
{
    public const string Path = "/auth/signup/complete";

    private static readonly string[] Fields = ["token", "password", "company_name"];

    public static async Task<IResult> HandleAsync(HttpContext http, SignupGate gate, SignupService signup)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(signup);

        if (!gate.IsOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, http.RequestAborted);
        if (fields is null || fields[1].Contains('\0') || !NameInput.IsValid(fields[2]))
        {
            return AccountResults.InvalidRequest();
        }

        var done = await signup.CompleteAsync(fields[0], fields[1], fields[2], http.RequestAborted);
        if (done.Succeeded)
        {
            return ApiResults.NoContent();
        }

        return done.Error == TenancyErrors.WeakPassword
            ? AccountResults.WeakPassword(done.Value!)
            : ApiResults.Error(done.Error!);
    }
}
```

In `src/Auth.Server/Api/SignupRoutes.cs`, before `        return app;` add

```csharp
        app.MapPost(SignupCompleteEndpoint.Path, SignupCompleteEndpoint.HandleAsync)
            .WithTags("Sign-up")
            .WithDescription(
                "Finishes a sign-up: in one transaction the account (its email confirmed), the company (named as given here, with a copy of "
                + "the default roles) and the membership with the first admin's role are made, and the pending sign-up is used up. The person "
                + "is not signed in. The checks are made in this order: the token, the password (a weak one leaves the token usable), the "
                + "account (`409 already_registered` when one has the address by now).")
            .ReadsJson<SignupCompleteRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesPasswordError(AccountResults.InvalidRequestError, AccountResults.InvalidTokenError, AccountResults.WeakPasswordError)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.SignupDisabled)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyRegistered);

```

- [ ] **Step 5: Run the tests.** `dotnet test tests/Auth.IntegrationTests --filter "FullyQualifiedName~Signup|FullyQualifiedName~OpenApi"`; the race tests are run three times in a row (`--filter "FullyQualifiedName~SignupCompleteRace"`) to see that they do not depend on one interleaving; then the whole project (`InviteAcceptTests`, `AuditAtomicityTests` and the older audit tests unchanged and green).

- [ ] **Step 6: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(server): POST /auth/signup/complete creates the account, the company and the first admin in one transaction`.

### Task 9: The shipped files: development settings, both manifests, the production compose file, the environment example, the sample overlay

**Files:**
- Modify: `src/Auth.Server/appsettings.Development.json`, `deploy/auth.yaml`, `samples/notes-api/auth.yaml`, `deploy/docker-compose.prod.yml`, `deploy/.env.prod.example`, `samples/notes-web/compose.yml`, `scripts/e2e-prod.sh`
- Modify (tests): `tests/Auth.IntegrationTests/ManifestActivationTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`

**Interfaces:**
- Consumes: Task 1 (`SignupGate`, the optional address), Tasks 2 to 8 (the endpoints, for the host-level checks).
- Produces: the development address `http://localhost:4200/complete-signup` in `appsettings.Development.json` (the older e2e scripts keep a `Healthy` stack); `signup: self-service` in `deploy/auth.yaml` and `samples/notes-api/auth.yaml`; `AUTH_FRONTEND_SIGNUP_URL` (optional, blank by default) mapped to `Auth__App__FrontendUrls__CompleteSignup` in the production compose file and listed in `deploy/.env.prod.example`; `http://localhost:8088/complete-signup` in the sample overlay; the address in the environment of `scripts/e2e-prod.sh`.

- [ ] **Step 1: Write the failing tests.** `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Signup;
using Auth.Server.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>The files that ship, read as they are: the settings of Development, the two manifests, the production compose file and its environment.</summary>
public sealed class ShippedConfigurationTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Auth-Core.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, relative);
    }

    private static string DevelopmentAddress()
    {
        var settings = new ConfigurationBuilder().AddJsonFile(RepositoryFile("src/Auth.Server/appsettings.Development.json")).Build();
        return settings[MailSettingsLoader.CompleteSignupUrlKey]
            ?? throw new InvalidOperationException("appsettings.Development.json has no completion address.");
    }

    private static async Task<string> BodyAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Theory]
    [InlineData("deploy/auth.yaml")]
    [InlineData("samples/notes-api/auth.yaml")]
    public void The_shipped_manifests_turn_sign_up_on(string relative)   // spec 0009 → Verification notes
    {
        var result = ManifestParser.Parse(File.ReadAllText(RepositoryFile(relative)));

        Assert.Null(result.Error);
        Assert.Equal(SignupMode.SelfService, result.Manifest!.Signup);
        Assert.Equal("admin", result.Manifest.FirstAdminRole.Name);
    }

    [Fact]
    public void The_development_settings_name_the_completion_address_of_the_sample()
    {
        Assert.Equal("http://localhost:4200/complete-signup", DevelopmentAddress());
    }

    [Fact]
    public void The_production_compose_file_passes_the_optional_address_and_the_example_lists_it()
    {
        var compose = File.ReadAllText(RepositoryFile("deploy/docker-compose.prod.yml"));
        var example = File.ReadAllLines(RepositoryFile("deploy/.env.prod.example"));

        Assert.Contains("Auth__App__FrontendUrls__CompleteSignup: ${AUTH_FRONTEND_SIGNUP_URL:-}", compose, StringComparison.Ordinal);
        Assert.Contains("AUTH_FRONTEND_SIGNUP_URL=", example);   // a line of its own, blank: the example is a complete list of the variables
    }

    [Fact]
    public void The_sample_overlay_and_the_production_test_set_the_address_so_that_their_stacks_stay_healthy()
    {
        Assert.Contains(
            "Auth__App__FrontendUrls__CompleteSignup: http://localhost:8088/complete-signup",
            File.ReadAllText(RepositoryFile("samples/notes-web/compose.yml")),
            StringComparison.Ordinal);
        Assert.Contains(
            "AUTH_FRONTEND_SIGNUP_URL=$PROXY_URL/complete-signup",
            File.ReadAllText(RepositoryFile("scripts/e2e-prod.sh")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_with_the_shipped_manifest_and_the_development_address_has_sign_up_on_and_is_healthy()
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithSetting(ManifestSettings.PathKey, "../../deploy/auth.yaml")
            .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, DevelopmentAddress());
        using var client = factory.CreateClient();

        Assert.True(factory.Services.GetRequiredService<SignupGate>().IsOn);
        Assert.Equal("""{"signup":"self-service"}""", await BodyAsync(client, "/auth/options"));
        Assert.Equal("Healthy", await BodyAsync(client, "/auth/health"));
    }

    [Fact]
    public async Task A_production_host_with_the_manifest_key_and_an_empty_address_starts_with_sign_up_off()   // Review Focus 1: compose passes an empty variable
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithEnvironment("Production")
            .WithManifest(AuthAppFactory.SelfServiceManifest)
            .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, "");
        using var client = factory.CreateClient();

        Assert.False(factory.Services.GetRequiredService<SignupGate>().IsOn);
        Assert.Equal("""{"signup":"invite-only"}""", await BodyAsync(client, "/auth/options"));
        Assert.Equal("Degraded", await BodyAsync(client, "/auth/health"));
    }

    [Fact]
    public async Task A_production_host_with_the_v0_1_0_manifest_and_no_address_setting_at_all_stays_invite_only()   // criterion 2: the key is absent, not blank
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithEnvironment("Production")
            .WithoutSetting(MailSettingsLoader.CompleteSignupUrlKey);
        using var client = factory.CreateClient();

        Assert.False(factory.Services.GetRequiredService<SignupGate>().IsOn);
        Assert.Equal("""{"signup":"invite-only"}""", await BodyAsync(client, "/auth/options"));
        Assert.Equal("Healthy", await BodyAsync(client, "/auth/health"));
        using var request = await SignupApi.Request(client, "anna@firma.test", "Firma");
        await SignupApi.AssertDisabledAsync(request);
    }

    [Fact]
    public async Task A_production_host_with_the_manifest_key_and_an_https_address_has_sign_up_on()
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithEnvironment("Production").WithSignup();
        using var client = factory.CreateClient();

        Assert.Equal("""{"signup":"self-service"}""", await BodyAsync(client, "/auth/options"));
        Assert.Equal("Healthy", await BodyAsync(client, "/auth/health"));
    }
}
```

In `tests/Auth.IntegrationTests/ManifestActivationTests.cs`, in `Shipped_development_manifest_is_valid`, after the line `        Assert.Equal(["admin", "user"], state.Manifest.DefaultRoles.Select(r => r.Name));` that ends that test (the last assertion of the file; the line appears once with the preceding `documents:read` line) add

```csharp
        Assert.Equal(SignupMode.SelfService, state.Manifest.Signup);
```

- [ ] **Step 2: Run the tests to see them fail** (`ShippedConfigurationTests`; and the extended `Shipped_development_manifest_is_valid`).

- [ ] **Step 3: The development settings.** In `src/Auth.Server/appsettings.Development.json` replace the `FrontendUrls` block with

```json
      "FrontendUrls": {
        "ResetPassword": "http://localhost:4200/reset",
        "VerifyEmail": "http://localhost:4200/verify",
        "AcceptInvite": "http://localhost:4200/invite",
        "CompleteSignup": "http://localhost:4200/complete-signup"
      }
```

- [ ] **Step 4: The manifests.** Replace `deploy/auth.yaml`:

```yaml
# The development manifest: what the product declares to Auth-Core (spec 0005 -> Manifest).
#
# permissions    the permissions the product's code checks. Four more are built in and always in the catalog:
#                members:manage, roles:manage, org:manage and org:delete (they guard the company API itself).
# default_roles  the roles every new company starts with a copy of. After that its roles are its own.
#                "*" stands for every permission of the catalog. At least one default role must hold
#                members:manage or "*", so that the first admin of a company can manage it.
# signup         "self-service" lets a visitor start a company of their own (spec 0009): the first default role that holds "*",
#                else the first that holds members:manage, is the role of its first admin. It also needs the address of the
#                product's completion screen (Auth:App:FrontendUrls:CompleteSignup; AUTH_FRONTEND_SIGNUP_URL in production).
#                "invite-only" (the default when the key is absent) means companies are made by the operator.
#
# A broken file never stops the service: the last valid manifest stays active and /auth/health says "Degraded".
permissions: [documents:read, documents:write, documents:approve]
signup: self-service
default_roles:
  admin: ["*"]
  user: [documents:read, documents:write]
```

Replace `samples/notes-api/auth.yaml`:

```yaml
# The manifest of the "notes" sample: what the product declares to Auth-Core (spec 0005 -> Manifest).
#
# permissions    what the product's code checks. The package guards GET /api/notes with notes:read and
#                POST /api/notes with notes:write (src/notes_api/app.py).
# default_roles  the roles every new company starts with a copy of. "*" stands for every permission of the catalog.
# signup         "self-service": a visitor may start a company of their own (spec 0009) and is its first admin, with the first
#                role that holds "*" (admin). The overlay of the web sample sets the address of the completion screen.
permissions: [notes:read, notes:write]
signup: self-service
default_roles:
  admin:  ["*"]
  user:   [notes:read, notes:write]
  viewer: [notes:read]
```

- [ ] **Step 5: The production compose file and its environment.**

In `deploy/docker-compose.prod.yml`, after the line

```yaml
      Auth__App__FrontendUrls__AcceptInvite: ${AUTH_FRONTEND_INVITE_URL:?set AUTH_FRONTEND_INVITE_URL in .env}
```

add

```yaml
      # Optional (spec 0009): the screen of the frontend that finishes a self-service sign-up. Blank keeps sign-up off; it is on only with this and
      # `signup: self-service` in the manifest (a manifest that asks for it without this makes /auth/health say Degraded).
      Auth__App__FrontendUrls__CompleteSignup: ${AUTH_FRONTEND_SIGNUP_URL:-}
```

In `deploy/.env.prod.example`, after the line `AUTH_FRONTEND_INVITE_URL=https://app.example.com/invite` add

```
# Optional: the screen of YOUR frontend that finishes a self-service sign-up (https only; ?token=... is added). Leave it blank and sign-up stays off.
# Sign-up is on only when this is set AND your manifest says `signup: self-service`; a manifest that asks for it without this address makes
# /auth/health say Degraded and sign-up answer 403.
AUTH_FRONTEND_SIGNUP_URL=
```

- [ ] **Step 6: The sample overlay and the production test.** In `samples/notes-web/compose.yml`, after the line `      Auth__App__FrontendUrls__AcceptInvite: http://localhost:8088/invite` add

```yaml
      Auth__App__FrontendUrls__CompleteSignup: http://localhost:8088/complete-signup
```

In `scripts/e2e-prod.sh`, in the environment file written by the here-document, after the line `AUTH_FRONTEND_INVITE_URL=$PROXY_URL/invite` add

```
AUTH_FRONTEND_SIGNUP_URL=$PROXY_URL/complete-signup
```

(the production stack mounts `deploy/auth.yaml`, which now says `signup: self-service`; without the address its step 2 would read `Degraded`). Change nothing else of the script.

- [ ] **Step 7: Check the other scripts by reading, not by running.** `scripts/e2e-login.sh`, `e2e-refresh.sh`, `e2e-lockout.sh`, `e2e-email.sh`, `e2e-tenancy.sh`, `e2e-hardening.sh` and `e2e-notes.sh` run the development stack (Development, `appsettings.Development.json`: the address is there, so health is `Healthy`); `e2e-web.sh` overrides it with the overlay. Nothing in them reads the manifest's keys. `grep -n "signup\|Degraded" scripts/*.sh` must show no script that expects the absence of the key.

- [ ] **Step 8: Run the tests.** `dotnet test tests/Auth.IntegrationTests --filter "FullyQualifiedName~ShippedConfiguration|FullyQualifiedName~ManifestActivation"`; then the whole project. `docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env.prod.example config` is not run by the implementer (the example holds blank secrets and compose refuses it on purpose); the file is checked by the test above and by the verifiers' `e2e-prod.sh`.

- [ ] **Step 9: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(deploy): sign-up in the shipped manifests, settings and compose files`.

---

## Day 4 — Thursday 02.04: the sample frontend

When the day is done, `samples/notes-web` has the two new screens and the link on its sign-in screen, with unit tests, and still builds. Two tasks; every task leaves `npx ng test --watch=false` and `npx ng build` green in `samples/notes-web` (run `npm ci` there first if `node_modules` is missing).

### Task 10: The sample: the API calls, the texts, `/signup`, the link on `/login`

**Files (all under `samples/notes-web/src/app/`):**
- Modify: `account-api.ts` (replaced as a whole), `auth/auth.service.ts`, `texts.ts`, `pages/login.ts`, `app.routes.ts`, `app.routes.spec.ts`, and one older spec, `pages/login.too-many-requests.spec.ts`
- Create: `pages/signup.ts`
- Test (create): `account-api.signup.spec.ts`, `pages/signup.spec.ts`, `pages/login.signup.spec.ts`

**Interfaces:**
- Consumes: the contract of the endpoints (Tasks 2, 5, 7, 8): `GET /auth/options` → `{"signup": ...}`; `POST /auth/signup` `{email, company_name}` → `202`, `429 too_many_attempts`, `403 signup_disabled`; `POST /auth/signup/preview` `{token}` → `200 {email, company_name}`; `POST /auth/signup/complete` `{token, password, company_name}` → `204`, `400 weak_password`, `400 invalid_token`, `409 already_registered`, `403 signup_disabled`. The existing `takeTokenFromUrl`, `PasswordRules`, `requiredText`, `Outcome<T>`, `failureOf`.
- Produces:
  - `AccountApi.options(): Promise<SiteOptions>` (`SiteOptions = { signup: 'self-service' | 'invite-only' }`; any failure or unknown answer is `invite-only`), `AccountApi.signUp(email, companyName): Promise<Outcome>`, `AccountApi.previewSignup(token): Promise<Outcome<SignupPreview>>` (`SignupPreview = { email, company_name }`), `AccountApi.completeSignup(token, password, companyName): Promise<Outcome>`.
  - `Failure` gains `{ kind: 'signup_disabled' }` (`403` and the code), `{ kind: 'already_registered' }` (`409` and the code) and `{ kind: 'invalid_request' }` (`400` and the code; produced only by `signUp` and `completeSignup`, so that each sign-up screen can say what to check).
  - `texts.login.createAccount`, `texts.signup.*`; the route `/signup` (public) and `SignupPage`; `LoginPage` shows `<a routerLink="/signup" data-testid="create-account">` while `options().signup` is `self-service`.

- [ ] **Step 1: Write the failing tests.**

`src/app/account-api.signup.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AccountApi } from './account-api';

const status = (code: number) => ({ status: code, statusText: String(code) });

describe('AccountApi: sign-up', () => {
  let api: AccountApi;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(AccountApi);
    ctrl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => ctrl.verify());

  describe('options', () => {
    it('asks GET /auth/options and reads self-service', async () => {
      const done = api.options();
      const request = ctrl.expectOne('/auth/options');
      expect(request.request.method).toBe('GET');
      request.flush({ signup: 'self-service' });
      expect(await done).toEqual({ signup: 'self-service' });
    });

    it('ignores the members it does not know', async () => {
      const done = api.options();
      ctrl.expectOne('/auth/options').flush({ signup: 'self-service', providers: ['google'] });
      expect(await done).toEqual({ signup: 'self-service' });
    });

    it.each([[{ signup: 'invite-only' }], [{ signup: 'something-new' }], [{ signup: 1 }], [{}], [[]], ['<html>oops</html>'], [null]])(
      'the answer %j means invite-only: the link is not offered',
      async (body) => {
        const done = api.options();
        ctrl.expectOne('/auth/options').flush(body);
        expect(await done).toEqual({ signup: 'invite-only' });
      },
    );

    it.each([500, 502, 429, 403])('a %s means invite-only', async (code) => {
      const done = api.options();
      ctrl.expectOne('/auth/options').flush('oops', status(code));
      expect(await done).toEqual({ signup: 'invite-only' });
    });

    it('no network means invite-only', async () => {
      const done = api.options();
      ctrl.expectOne('/auth/options').error(new ProgressEvent('error'));
      expect(await done).toEqual({ signup: 'invite-only' });
    });
  });

  it('signUp posts the email and the company name to its endpoint and is ok on the empty answer', async () => {
    const done = api.signUp('anna@firma.example', 'Firma');
    const request = ctrl.expectOne('/auth/signup');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ email: 'anna@firma.example', company_name: 'Firma' });
    request.flush(null, status(202));
    expect((await done).ok).toBe(true);
  });

  it('completeSignup posts the token, the password and the company name', async () => {
    const done = api.completeSignup('tok', 'Pw-1 x', 'Firma');
    const request = ctrl.expectOne('/auth/signup/complete');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok', password: 'Pw-1 x', company_name: 'Firma' });
    request.flush(null, status(204));
    expect((await done).ok).toBe(true);
  });

  it('reads the answer to a preview: the address and the company name', async () => {
    const done = api.previewSignup('tok');
    const request = ctrl.expectOne('/auth/signup/preview');
    expect(request.request.body).toEqual({ token: 'tok' });
    request.flush({ email: 'anna@firma.example', company_name: 'Firma' });
    expect(await done).toEqual({ ok: true, value: { email: 'anna@firma.example', company_name: 'Firma' } });
  });

  it('a 400 invalid_request on a preview stays "other": that screen has the common text', async () => {
    const done = api.previewSignup('tok');
    ctrl.expectOne('/auth/signup/preview').flush({ error: 'invalid_request' }, status(400));
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
  });

  it('a preview that is not what the contract says is a failure, not a half-filled screen', async () => {
    const done = api.previewSignup('tok');
    ctrl.expectOne('/auth/signup/preview').flush({ email: 'anna@firma.example' });
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
  });

  it.each([
    [403, { error: 'signup_disabled' }, { kind: 'signup_disabled' }],
    [409, { error: 'already_registered' }, { kind: 'already_registered' }],
    [400, { error: 'invalid_token' }, { kind: 'invalid_token' }],
    [400, { error: 'weak_password', rules: ['too_short'] }, { kind: 'weak_password', rules: ['too_short'] }],
    [429, { error: 'too_many_attempts', retry_after_seconds: 42 }, { kind: 'too_many_attempts', retryAfterSeconds: 42 }],
    [403, { error: 'forbidden' }, { kind: 'other' }],
    [409, { error: 'something_else' }, { kind: 'other' }],
    [400, { error: 'invalid_request' }, { kind: 'invalid_request' }],
    [500, '<html>oops</html>', { kind: 'other' }],
  ])('answer %s %j is the failure %j', async (code, body, failure) => {
    const done = api.completeSignup('tok', 'pw', 'Firma');
    ctrl.expectOne('/auth/signup/complete').flush(body, status(code));
    expect(await done).toEqual({ ok: false, failure });
  });
});
```

`src/app/pages/signup.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, realLocation, settle, submitForm, typeInto, typeRaw } from '../../testing/helpers';
import { SignupPage } from './signup';

const status = (code: number) => ({ status: code, statusText: String(code) });
const sent = 'Check your inbox. We sent a message to this address.';
const closed = 'Sign-up is closed. Ask the company you want to join for an invitation.';

async function open() {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([{ path: 'signup', component: SignupPage }]),
      provideHttpClient(),
      provideHttpClientTesting(),
      realLocation, // the router writes to the real window.location, so the address-bar test below can fail
    ],
  });
  history.replaceState(null, '', '/signup');
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/signup', SignupPage);
  return { fixture: harness.fixture, ctrl: TestBed.inject(HttpTestingController) };
}

type Opened = Awaited<ReturnType<typeof open>>;

function ask({ fixture }: Opened, email: string, company: string): void {
  typeInto(fixture, '#email', email);
  typeInto(fixture, '#company', company);
  submitForm(fixture);
}

describe('SignupPage', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('asks for an email and a company name and links back to the sign-in', async () => {
    const { fixture } = await open();
    expect(has(fixture, '#email')).toBe(true);
    expect(has(fixture, '#company')).toBe(true);
    expect(has(fixture, '#password')).toBe(false); // the password is chosen at the link, never on this form
    expect(has(fixture, 'a[href="/login"]')).toBe(true);
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#company')?.maxLength).toBe(100);
  });

  it.each(['anna@firma.example', 'nobody@nowhere.example'])('202 for %s: always the same sentence, and the form sends the trimmed values', async (email) => {
    const opened = await open();
    typeRaw(opened.fixture, '#email', ` ${email} `);
    typeRaw(opened.fixture, '#company', ' Firma ');
    submitForm(opened.fixture);
    const request = opened.ctrl.expectOne('/auth/signup');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ email, company_name: 'Firma' });
    request.flush(null, status(202));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(sent);
    expect(has(opened.fixture, '[data-testid="signup-sent"]')).toBe(true);
  });

  it('429 too_many_attempts: "Wait N seconds before asking again."', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    opened.ctrl.expectOne('/auth/signup').flush({ error: 'too_many_attempts', retry_after_seconds: 42 }, status(429));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Wait 42 seconds before asking again.');
    expect(pageText(opened.fixture)).not.toContain(sent);
  });

  it('403 signup_disabled: says that sign-up is closed and shows no form', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    opened.ctrl.expectOne('/auth/signup').flush({ error: 'signup_disabled' }, status(403));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(closed);
    expect(has(opened.fixture, 'form')).toBe(false);
    expect(pageText(opened.fixture)).not.toContain(sent);
    expect(has(opened.fixture, 'a[href="/login"]')).toBe(true);
  });

  it('400 invalid_request: says what to check', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    opened.ctrl.expectOne('/auth/signup').flush({ error: 'invalid_request' }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Check the email address and the company name.');
    expect(pageText(opened.fixture)).not.toContain(sent);
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it('500 with a page of HTML: "Something went wrong. Try again."', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    opened.ctrl.expectOne('/auth/signup').flush('<html>oops</html>', status(500));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(pageText(opened.fixture)).not.toContain(sent);
  });

  it('no network: "Something went wrong. Try again."', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    opened.ctrl.expectOne('/auth/signup').error(new ProgressEvent('error'));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
  });

  it.each([
    ['', 'Firma'],
    ['   ', 'Firma'],
    ['anna@firma.example', ''],
    ['anna@firma.example', '   '],
  ])('sends nothing for the email %j and the company %j', async (email, company) => {
    const opened = await open();
    typeRaw(opened.fixture, '#email', email);
    typeRaw(opened.fixture, '#company', company);
    submitForm(opened.fixture);
    opened.ctrl.expectNone('/auth/signup');
  });

  it('sends one request for a double submit', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    submitForm(opened.fixture);
    opened.ctrl.expectOne('/auth/signup').flush(null, status(202));
  });

  it('puts neither the email nor the company in the address bar', async () => {
    const opened = await open();
    ask(opened, 'anna@firma.example', 'Firma');
    opened.ctrl.expectOne('/auth/signup').flush(null, status(202));
    await settle(opened.fixture);
    expect(TestBed.inject(Router).url).toBe('/signup');
    expect(window.location.pathname).toBe('/signup');
    expect(window.location.search).toBe('');
    expect(window.location.href).not.toContain('anna');
    expect(window.location.href).not.toContain('Firma');
  });
});
```

`src/app/pages/login.signup.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, settle } from '../../testing/helpers';
import { LoginPage } from './login';

const status = (code: number) => ({ status: code, statusText: String(code) });

async function open(answer: object | string, code = 200) {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'login', component: LoginPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/login', LoginPage);
  const ctrl = TestBed.inject(HttpTestingController);
  const request = ctrl.expectOne('/auth/options');
  expect(request.request.method).toBe('GET');
  request.flush(answer, code === 200 ? {} : status(code));
  await settle(harness.fixture);
  return harness.fixture;
}

describe('LoginPage: the link to sign-up', () => {
  it('offers "Create an account" when Auth-Core says self-service', async () => {
    const fixture = await open({ signup: 'self-service' });
    expect(has(fixture, 'a[href="/signup"][data-testid="create-account"]')).toBe(true);
    expect(pageText(fixture)).toContain('Create an account');
  });

  it('offers nothing when Auth-Core says invite-only', async () => {
    const fixture = await open({ signup: 'invite-only' });
    expect(has(fixture, 'a[href="/signup"]')).toBe(false);
    expect(pageText(fixture)).not.toContain('Create an account');
  });

  it.each([[500], [502], [429]])('offers nothing when the options cannot be read (%s)', async (code) => {
    const fixture = await open('oops', code);
    expect(has(fixture, 'a[href="/signup"]')).toBe(false);
  });

  it('keeps the form and the forgot link whatever the options say', async () => {
    const fixture = await open({ signup: 'self-service' });
    expect(has(fixture, '#email')).toBe(true);
    expect(has(fixture, '#password')).toBe(true);
    expect(has(fixture, 'a[href="/forgot"]')).toBe(true);
  });
});
```

In `src/app/app.routes.spec.ts` replace the line

```ts
  it.each(['/login', '/forgot', '/reset', '/verify', '/invite'])('%s needs no token', async (url) => {
```

with

```ts
  it.each(['/login', '/forgot', '/reset', '/verify', '/invite', '/signup'])('%s needs no token', async (url) => {
```

In `src/app/pages/login.too-many-requests.spec.ts` (an older spec that opens the real `LoginPage` and ends with `ctrl.verify()`, which fails on an open request), after the line

```ts
    const ctrl = TestBed.inject(HttpTestingController);
```

add

```ts
    ctrl.expectOne('/auth/options').flush({ signup: 'invite-only' }); // the sign-in screen asks what a visitor may do
```

- [ ] **Step 2: Run the tests to see them fail** (`npx ng test --watch=false` in `samples/notes-web`: the specs do not compile: `SignupPage`, `api.options` do not exist).

- [ ] **Step 3: The API calls.** Replace `src/app/account-api.ts`:

```ts
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Failure, errorCode, failureOf, isRecord } from './auth/auth.service';

export type Outcome<T = void> = { ok: true; value: T } | { ok: false; failure: Failure };

/** What POST /auth/invites/preview answers: who invites, for whom, as what. */
export interface InvitePreview {
  org_name: string;
  email: string;
  role: string;
}

/** What GET /auth/options answers, as far as the screens care: whether a visitor may start a company of their own. */
export interface SiteOptions {
  signup: 'self-service' | 'invite-only';
}

/** What POST /auth/signup/preview answers: the address as it was typed and the company name of the request. */
export interface SignupPreview {
  email: string;
  company_name: string;
}

/** Auth-Core's endpoints for the mail links and the account, which need no access token. */
@Injectable({ providedIn: 'root' })
export class AccountApi {
  private readonly http = inject(HttpClient);

  forgotPassword(email: string): Promise<Outcome> {
    return this.post('/auth/password/forgot', { email });
  }

  requestVerification(email: string): Promise<Outcome> {
    return this.post('/auth/email/verify/request', { email });
  }

  resetPassword(token: string, newPassword: string): Promise<Outcome> {
    return this.post('/auth/password/reset', { token, new_password: newPassword });
  }

  verifyEmail(token: string): Promise<Outcome> {
    return this.post('/auth/email/verify', { token });
  }

  async previewInvite(token: string): Promise<Outcome<InvitePreview>> {
    const outcome = await this.post<unknown>('/auth/invites/preview', { token });
    if (!outcome.ok) {
      return outcome;
    }
    const body = outcome.value;
    if (
      isRecord(body) &&
      typeof body['org_name'] === 'string' &&
      typeof body['email'] === 'string' &&
      typeof body['role'] === 'string'
    ) {
      return { ok: true, value: { org_name: body['org_name'], email: body['email'], role: body['role'] } };
    }
    return { ok: false, failure: { kind: 'other' } };
  }

  acceptInvite(token: string, password: string): Promise<Outcome> {
    return this.post('/auth/invites/accept', { token, password });
  }

  /**
   * Whether the product's screens may offer sign-up. A call that fails, and an answer this app does not know, mean "invite-only":
   * the link "Create an account" is then not offered, which is the safe side.
   */
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

  async previewSignup(token: string): Promise<Outcome<SignupPreview>> {
    const outcome = await this.post<unknown>('/auth/signup/preview', { token });
    if (!outcome.ok) {
      return outcome;
    }
    const body = outcome.value;
    if (isRecord(body) && typeof body['email'] === 'string' && typeof body['company_name'] === 'string') {
      return { ok: true, value: { email: body['email'], company_name: body['company_name'] } };
    }
    return { ok: false, failure: { kind: 'other' } };
  }

  completeSignup(token: string, password: string, companyName: string): Promise<Outcome> {
    return this.postForm('/auth/signup/complete', { token, password, company_name: companyName });
  }

  /** Like `post`, but a `400 invalid_request` is a failure of its own: each sign-up form has a text for it. */
  private async postForm<T = void>(url: string, body: unknown): Promise<Outcome<T>> {
    try {
      return { ok: true, value: await firstValueFrom(this.http.post<T>(url, body)) };
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 400 && errorCode(error) === 'invalid_request') {
        return { ok: false, failure: { kind: 'invalid_request' } };
      }
      return { ok: false, failure: failureOf(error) };
    }
  }

  private async post<T = void>(url: string, body: unknown): Promise<Outcome<T>> {
    try {
      return { ok: true, value: await firstValueFrom(this.http.post<T>(url, body)) };
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
  }
}
```

- [ ] **Step 4: The failures.** In `src/app/auth/auth.service.ts` make two edits.

(a) In the type `Failure`, after the line `  | { kind: 'already_member' }` add

```ts
  | { kind: 'already_registered' }
  | { kind: 'signup_disabled' }
  | { kind: 'invalid_request' }
```

(`failureOf` is not changed for `invalid_request`: the older screens and their specs keep reading it as `other`; the two sign-up forms read it through `AccountApi.postForm` below.)

(b) In `failureOf`, replace

```ts
  if (error.status === 409 && code === 'already_member') {
    return { kind: 'already_member' };
  }
  return { kind: 'other' };
```

with

```ts
  if (error.status === 409 && code === 'already_member') {
    return { kind: 'already_member' };
  }
  if (error.status === 409 && code === 'already_registered') {
    return { kind: 'already_registered' };
  }
  if (error.status === 403 && code === 'signup_disabled') {
    return { kind: 'signup_disabled' };
  }
  return { kind: 'other' };
```

- [ ] **Step 5: The texts.** In `src/app/texts.ts` make two edits.

(a) In `login`, replace `    noMembership: 'Your account does not belong to a company.',` with

```ts
    noMembership: 'Your account does not belong to a company.',
    createAccount: 'Create an account',
```

(b) Before the block `  notes: {` (the line `  notes: {` followed by `    title: 'Notes',`) insert

```ts
  signup: {
    title: 'Create an account',
    intro: 'Enter your email address and the name of your company. We will send you a link to finish.',
    companyName: 'Company name',
    submit: 'Create account',
    // The same sentence for every address: the answer must not tell whether the address has an account.
    sent: 'Check your inbox. We sent a message to this address.',
    closed: 'Sign-up is closed. Ask the company you want to join for an invitation.',
    invalidRequest: 'Check the email address and the company name.',
  },

```

- [ ] **Step 6: The sign-up screen.** `src/app/pages/signup.ts`:

```ts
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AccountApi } from '../account-api';
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
        <p>{{ t.intro }}</p>
        <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
          <label for="email">{{ common.email }}</label>
          <input id="email" type="email" formControlName="email" autocomplete="username" autocapitalize="none" />
          <label for="company">{{ t.companyName }}</label>
          <input id="company" type="text" formControlName="company" maxlength="100" autocomplete="organization" />
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
export class SignupPage {
  protected readonly t = texts.signup;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);

  // The password is not asked here: it is chosen at the link of the mail, so that only the reader of the mailbox ever sets it.
  protected readonly form = inject(NonNullableFormBuilder).group({ email: ['', requiredText], company: ['', requiredText] });
  protected readonly busy = signal(false);
  protected readonly sent = signal(false);
  protected readonly closed = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async submit(): Promise<void> {
    if (this.busy() || this.form.invalid) {
      return;
    }
    this.busy.set(true);
    this.sent.set(false);
    this.error.set(null);
    const { email, company } = this.form.getRawValue();
    // Trimmed: Auth-Core refuses a name with white space at either end.
    const result = await this.account.signUp(email.trim(), company.trim());
    this.busy.set(false);
    if (result.ok) {
      this.sent.set(true);
    } else if (result.failure.kind === 'too_many_attempts') {
      this.error.set(this.common.waitSeconds(result.failure.retryAfterSeconds));
    } else if (result.failure.kind === 'signup_disabled') {
      this.closed.set(true);
    } else if (result.failure.kind === 'invalid_request') {
      this.error.set(this.t.invalidRequest);
    } else {
      this.error.set(this.common.somethingWrong);
    }
  }
}
```

- [ ] **Step 7: The link on the sign-in screen.** In `src/app/pages/login.ts` make four edits.

(a) `import { Component, inject, signal } from '@angular/core';` becomes `import { Component, OnInit, inject, signal } from '@angular/core';`.

(b) After the line `      <p><a routerLink="/forgot">{{ t.forgot }}</a></p>` insert

```
      @if (signupOpen()) {
        <p><a routerLink="/signup" data-testid="create-account">{{ t.createAccount }}</a></p>
      }
```

(c) `export class LoginPage {` becomes `export class LoginPage implements OnInit {`.

(d) After the line `  protected readonly resending = signal(false);` insert

```ts
  /** True while GET /auth/options says a visitor may start a company: then the screen offers "Create an account". */
  protected readonly signupOpen = signal(false);

  ngOnInit(): void {
    void this.account.options().then((options) => this.signupOpen.set(options.signup === 'self-service'));
  }
```

- [ ] **Step 8: The route.** In `src/app/app.routes.ts`: after `import { ResetPage } from './pages/reset';` add `import { SignupPage } from './pages/signup';`, and after the route line `  { path: 'invite', component: InvitePage, title: texts.invite.title },` add

```ts
  { path: 'signup', component: SignupPage, title: texts.signup.title },
```

- [ ] **Step 9: Run the tests.** In `samples/notes-web`: `npx ng test --watch=false` (every older spec passes: the extra `GET /auth/options` of `LoginPage` is an open request that `ctrl.expectOne('/auth/login')` and the like never look at; the one older spec that calls `ctrl.verify()` with the real `LoginPage`, `login.too-many-requests.spec.ts`, answers it, as edited above; the other specs that call `verify()` use the service, the guard or `Blank` components), then `npx ng build` and `npm run check:docs`.

- [ ] **Step 10: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(samples): the sign-up screen and the link on the sign-in screen`.

### Task 11: The sample: `/complete-signup`

**Files (all under `samples/notes-web/src/app/`):**
- Create: `pages/complete-signup.ts`
- Modify: `texts.ts`, `app.routes.ts`, `app.routes.spec.ts`, `auth/auth.interceptor.ts` (one comment)
- Test (create): `pages/complete-signup.spec.ts`

**Interfaces:**
- Consumes: Task 10 (`AccountApi.previewSignup/completeSignup`, `Failure` kinds, `texts.signup.closed` and `texts.signup.companyName`, the route `/signup`), `takeTokenFromUrl`, `PasswordRules`, `requiredText`, `texts.common.*`.
- Produces: `CompleteSignupPage` at the public route `/complete-signup` (the mail path equals the kind name `complete-signup`, which the Playwright helper and `scripts/e2e-signup.sh` rely on); `texts.completeSignup.*`.

- [ ] **Step 1: Write the failing tests.** `src/app/pages/complete-signup.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { has, pageText, realLocation, settle, submitForm, typeInto, typeRaw, valueOf } from '../../testing/helpers';
import { CompleteSignupPage } from './complete-signup';

const status = (code: number) => ({ status: code, statusText: String(code) });
const invalid = 'This link has expired or was already used.';
const closed = 'Sign-up is closed. Ask the company you want to join for an invitation.';
const preview = { email: 'anna@firma.example', company_name: 'Firma' };

async function open(query = '?token=tok-s', previewAnswer: 'ok' | 'wait' = 'ok') {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([{ path: 'complete-signup', component: CompleteSignupPage }]),
      provideHttpClient(),
      provideHttpClientTesting(),
      realLocation,
    ],
  });
  history.replaceState(null, '', `/complete-signup${query}`);
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/complete-signup${query}`, CompleteSignupPage);
  await settle(); // the navigation that takes the token out of the address bar lands first
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const ctrl = TestBed.inject(HttpTestingController);
  if (previewAnswer === 'ok') {
    ctrl.expectOne('/auth/signup/preview').flush(preview);
    await settle(harness.fixture);
  }
  return { fixture: harness.fixture, ctrl, navigate };
}

type Opened = Awaited<ReturnType<typeof open>>;

function choose({ fixture }: Opened, password: string, repeat = password): void {
  typeInto(fixture, '#password', password);
  typeInto(fixture, '#repeat', repeat);
  submitForm(fixture);
}

function button(opened: Opened): HTMLButtonElement | null {
  return (opened.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button[type="submit"]');
}

describe('CompleteSignupPage', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('asks for the preview with the token on opening, once, and takes the token out of the address bar and the router', async () => {
    const { ctrl } = await open('?token=tok-s', 'wait');
    const request = ctrl.expectOne('/auth/signup/preview');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-s' });
    expect(window.location.search).toBe('');
    expect(window.location.href).not.toContain('tok-s');
    expect(TestBed.inject(Router).url).toBe('/complete-signup');
    request.flush(preview);
    await settle();
    ctrl.expectNone('/auth/signup/preview'); // the navigation that removed the token did not run the screen again
  });

  it('shows the address (not editable), the company name (editable) and who the password is for', async () => {
    const { fixture } = await open();
    expect(valueOf(fixture, '#email')).toBe('anna@firma.example');
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#email')?.readOnly).toBe(true);
    expect(valueOf(fixture, '#company')).toBe('Firma');
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#company')?.readOnly).toBe(false);
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#company')?.maxLength).toBe(100);
    expect(pageText(fixture)).toContain('This sets the password for anna@firma.example.');
    expect(has(fixture, '#password')).toBe(true);
    expect(has(fixture, '#repeat')).toBe(true);
  });

  it('204: goes to /login with the address in the navigation state, not in the URL', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    const request = opened.ctrl.expectOne('/auth/signup/complete');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-s', password: 'Chosen-Passw0rd', company_name: 'Firma' });
    request.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigate).toHaveBeenCalledTimes(1);
    expect(opened.navigate).toHaveBeenCalledWith(['/login'], { state: { email: 'anna@firma.example' } });
  });

  it('sends the company name the person typed, trimmed', async () => {
    const opened = await open();
    typeInto(opened.fixture, '#company', '  Firma Renamed  ');
    choose(opened, 'Chosen-Passw0rd');
    const request = opened.ctrl.expectOne('/auth/signup/complete');
    expect(request.request.body).toEqual({ token: 'tok-s', password: 'Chosen-Passw0rd', company_name: 'Firma Renamed' });
    request.flush(null, status(204));
  });

  it('sends the password exactly as typed', async () => {
    const opened = await open();
    choose(opened, '  Chosen Passw0rd  ');
    const request = opened.ctrl.expectOne('/auth/signup/complete');
    expect(request.request.body).toEqual({ token: 'tok-s', password: '  Chosen Passw0rd  ', company_name: 'Firma' });
    request.flush(null, status(204));
  });

  it('weak_password: the rules not met, one line each, and the form stays', async () => {
    const opened = await open();
    choose(opened, 'short');
    opened.ctrl
      .expectOne('/auth/signup/complete')
      .flush({ error: 'weak_password', rules: ['too_short', 'requires_digit'] }, status(400));
    await settle(opened.fixture);
    const lines = Array.from(
      (opened.fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="rules"] li'),
      (item) => item.textContent?.trim(),
    );
    expect(lines).toEqual(['The password is too short.', 'The password needs a digit.']);
    expect(has(opened.fixture, 'form')).toBe(true);
    expect(opened.navigate).not.toHaveBeenCalled();
  });

  it('409 already_registered: says the account exists and links to the sign-in; no form', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'already_registered' }, status(409));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('An account with this address already exists.');
    expect(has(opened.fixture, 'a[href="/login"]')).toBe(true);
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it.each(['preview', 'complete'])('invalid_token on the %s: the link is used up, with a link to sign up again; no form', async (where) => {
    const opened = await open('?token=tok-s', where === 'preview' ? 'wait' : 'ok');
    if (where === 'preview') {
      opened.ctrl.expectOne('/auth/signup/preview').flush({ error: 'invalid_token' }, status(400));
    } else {
      choose(opened, 'Chosen-Passw0rd');
      opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'invalid_token' }, status(400));
    }
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(invalid);
    expect(has(opened.fixture, 'a[href="/signup"][data-testid="signup-again"]')).toBe(true);
    expect(pageText(opened.fixture)).toContain('Sign up again');
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it.each(['preview', 'complete'])('403 signup_disabled on the %s: says that sign-up is closed; no form', async (where) => {
    const opened = await open('?token=tok-s', where === 'preview' ? 'wait' : 'ok');
    if (where === 'preview') {
      opened.ctrl.expectOne('/auth/signup/preview').flush({ error: 'signup_disabled' }, status(403));
    } else {
      choose(opened, 'Chosen-Passw0rd');
      opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'signup_disabled' }, status(403));
    }
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(closed);
    expect(has(opened.fixture, 'form')).toBe(false);
    expect(has(opened.fixture, 'a[href="/login"]')).toBe(true);
  });

  it.each([
    [400, { error: 'invalid_request' }],
    [500, '<html>oops</html>'],
  ])('%s on the preview: "Something went wrong. Try again."', async (code, body) => {
    const { fixture, ctrl } = await open('?token=tok-s', 'wait');
    ctrl.expectOne('/auth/signup/preview').flush(body, status(code));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Something went wrong. Try again.');
  });

  it('"Try again" after a server error on the preview asks again with the token still held, and the form follows', async () => {
    const { fixture, ctrl } = await open('?token=tok-s', 'wait');
    ctrl.expectOne('/auth/signup/preview').flush('oops', status(500));
    await settle(fixture);
    expect(window.location.search).toBe('');
    const again = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="try-again"]');
    expect(again?.textContent?.trim()).toBe('Try again');
    again?.click();
    fixture.detectChanges();
    expect(pageText(fixture)).toContain('Opening your sign-up...');
    const retry = ctrl.expectOne('/auth/signup/preview');
    expect(retry.request.body).toEqual({ token: 'tok-s' });
    retry.flush(preview);
    await settle(fixture);
    expect(has(fixture, 'form')).toBe(true);
    expect(has(fixture, '[data-testid="try-again"]')).toBe(false);
  });

  it('500 on complete: "Something went wrong. Try again.", and the form stays', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush('oops', status(500));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it('two different passwords: a message, and no request', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd', 'Chosen-Passw0rd2');
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('The two passwords are not the same.');
    opened.ctrl.expectNone('/auth/signup/complete');
  });

  it('a blank company name: a message, and no request', async () => {
    const opened = await open();
    typeRaw(opened.fixture, '#company', '   ');
    choose(opened, 'Chosen-Passw0rd');
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Enter the name of your company.');
    opened.ctrl.expectNone('/auth/signup/complete');
  });

  it('400 invalid_request on complete: says what to check, and the form stays', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'invalid_request' }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Check the company name.');
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it.each(['', '?token=', '?other=1'])('a link with the query %j is an invalid link, with no request', async (query) => {
    const { fixture, ctrl } = await open(query, 'wait');
    expect(pageText(fixture)).toContain(invalid);
    expect(has(fixture, 'a[href="/signup"]')).toBe(true);
    ctrl.expectNone('/auth/signup/preview');
  });

  it('sends one request for a double submit', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    submitForm(opened.fixture);
    opened.ctrl.expectOne('/auth/signup/complete').flush(null, status(204));
  });

  it.each([
    ['rejects', () => Promise.reject(new Error('navigation failed'))],
    ['answers false', () => Promise.resolve(false)],
  ])('the button is on again when the router %s', async (_name, answer) => {
    const opened = await open();
    opened.navigate.mockImplementation(answer);
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush(null, status(204));
    await settle(opened.fixture);
    expect(button(opened)?.disabled).toBe(false);
  });

  it('after 204 the button stays off until the login screen is open: a second Enter does not post the used token again', async () => {
    const opened = await open();
    let arrive: (opened: boolean) => void = () => undefined;
    opened.navigate.mockReturnValue(new Promise<boolean>((resolve) => (arrive = resolve)));
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush(null, status(204));
    await settle(opened.fixture);
    expect(button(opened)?.disabled).toBe(true);
    submitForm(opened.fixture);
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/signup/complete');
    arrive(true);
    await settle(opened.fixture);
    expect(button(opened)?.disabled).toBe(false);
  });
});
```

In `src/app/app.routes.spec.ts` replace

```ts
  it.each(['/login', '/forgot', '/reset', '/verify', '/invite', '/signup'])('%s needs no token', async (url) => {
```

with

```ts
  it.each(['/login', '/forgot', '/reset', '/verify', '/invite', '/signup', '/complete-signup'])('%s needs no token', async (url) => {
```

- [ ] **Step 2: Run the tests to see them fail** (`CompleteSignupPage` does not exist).

- [ ] **Step 3: The texts.** In `src/app/texts.ts`, before the block `  notes: {` insert

```ts
  completeSignup: {
    title: 'Finish creating your account',
    loading: 'Opening your sign-up...',
    setsPassword: (email: string): string => `This sets the password for ${email}.`,
    submit: 'Create company',
    signUpAgain: 'Sign up again',
    alreadyRegistered: 'An account with this address already exists.',
    companyRequired: 'Enter the name of your company.',
    invalidRequest: 'Check the company name.',
  },

```

(The "link used up" sentence is `texts.common.invalidLink`: the same words as the other link screens. The label "Company name" and the "closed" sentence are `texts.signup.companyName` and `texts.signup.closed`, which the screen reads as `shared`.)

- [ ] **Step 4: The screen.** `src/app/pages/complete-signup.ts`:

```ts
import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AccountApi, SignupPreview } from '../account-api';
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
              <label for="company">{{ shared.companyName }}</label>
              <input id="company" type="text" formControlName="company" maxlength="100" autocomplete="organization" />
              <p class="status">{{ t.setsPassword(signup.email) }}</p>
              <label for="password">{{ common.newPassword }}</label>
              <input id="password" type="password" formControlName="password" autocomplete="new-password" />
              <label for="repeat">{{ common.repeatPassword }}</label>
              <input id="repeat" type="password" formControlName="repeat" autocomplete="new-password" />
              <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
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
  private readonly router = inject(Router);
  // Read once, here, and gone from the address bar from now on.
  private readonly token = takeTokenFromUrl(inject(ActivatedRoute), inject(Router));

  protected readonly form = inject(NonNullableFormBuilder).group({
    company: ['', requiredText],
    password: ['', Validators.required],
    repeat: ['', Validators.required],
  });
  protected readonly phase = signal<'loading' | 'form' | 'invalid' | 'registered' | 'closed' | 'error'>(
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
    this.rules.set([]);
    this.error.set(null);
    if (company.trim() === '') {
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
    // The company name is the person's last word (the one of the request was a suggestion); trimmed, because Auth-Core refuses white space at either end.
    const result = await this.account.completeSignup(this.token, password, company.trim());
    if (result.ok) {
      // The address goes in the navigation state: it is never in the URL. The button stays off until the login is open: the token is
      // used up, and a second Enter would only post it again. When the router cannot open it (it answers false or fails) the person
      // stays here and the button is on again.
      try {
        await this.router.navigate(['/login'], { state: { email: signup.email } });
      } catch {
        // nothing to show: the company was made
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
      this.error.set(this.t.invalidRequest);
    } else {
      this.error.set(this.common.somethingWrong);
    }
    this.busy.set(false);
  }

  private async preview(token: string): Promise<void> {
    const result = await this.account.previewSignup(token);
    if (result.ok) {
      this.pending.set(result.value);
      this.form.controls.company.setValue(result.value.company_name);
      this.phase.set('form');
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else if (result.failure.kind === 'signup_disabled') {
      this.phase.set('closed');
    } else {
      this.phase.set('error');
    }
  }
}
```

- [ ] **Step 5: The route and a comment.** In `src/app/app.routes.ts`: before the line `import { ForgotPage } from './pages/forgot';` add `import { CompleteSignupPage } from './pages/complete-signup';`, and after the route line `  { path: 'signup', component: SignupPage, title: texts.signup.title },` add

```ts
  { path: 'complete-signup', component: CompleteSignupPage, title: texts.completeSignup.title },
```

In `src/app/auth/auth.interceptor.ts` change the comment text `(/reset, /verify, /invite)` to `(/reset, /verify, /invite, /complete-signup)` (one line, in `goToLogin`).

- [ ] **Step 6: Run the tests.** In `samples/notes-web`: `npx ng test --watch=false`, `npx ng build`, `npm run check:docs`.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(samples): the screen that finishes a sign-up`.

---

## Day 5 — Friday 03.04: the end-to-end checks, and the completion signs in

When the day is done, the browser test and the API script exist for the verifiers to run, and (the owner's Amendment 2 of 2026-04-03, below) a completed sign-up signs the person in: the server (Task 14), the sample and the two end-to-end checks (Task 15). Three tasks: Task 12 (written first), then the amendment's Tasks 14 and 15, which change what Tasks 8, 11 and 12 produced. The documents (Task 13) follow on Saturday.

### Task 12: The Playwright test and `scripts/e2e-signup.sh`

**Files:**
- Modify: `samples/notes-web/e2e/support/mail.ts` (replaced as a whole), `scripts/e2e-web.sh` (the seeding of two addresses), and the header comments of `scripts/e2e-tenancy.sh` and `scripts/e2e-hardening.sh` (one line each in their run sequences)
- Create: `samples/notes-web/e2e/09-signup.spec.ts`, `scripts/e2e-signup.sh`

**Interfaces:**
- Consumes: Tasks 4 to 11 (the mails and the screens), Task 9 (the overlay sets `Auth__App__FrontendUrls__CompleteSignup` to `http://localhost:8088/complete-signup`, the manifest of `samples/notes-api/auth.yaml` says `signup: self-service`), the existing `e2e/support/*` (`acceptInviteByApi` included), `E2E_SEED_EMAIL`, `E2E_USER_PASSWORD` and `E2E_SEEDED_NOTE` of `scripts/e2e-web.sh`.
- Produces: `LinkKind` gains `'complete-signup'` (the mail path equals the kind name: `/complete-signup?token=`); `waitForAccountExistsMail(to, timeoutMs?)` returning `{ text, html }`; five Playwright tests in file order after `08-proxy-headers.spec.ts`; two more settings that `scripts/e2e-web.sh` gives the tests, `E2E_BOTH_EMAIL` (an address the script invited through the operator CLI) and `E2E_EXPIRED_EMAIL` (an address that asked for a sign-up and whose link the script made expired); `scripts/e2e-signup.sh` for the development stack (the stack and the project name of the other `e2e-*.sh` scripts).

The Playwright run is `scripts/e2e-web.sh` (verifiers, Saturday); an implementer cannot start the stack. What they do check: `npx tsc --noEmit -p e2e/tsconfig.json` in `samples/notes-web` compiles the new spec, `bash -n scripts/e2e-signup.sh` parses the script, `bash -n scripts/e2e-web.sh` too, and `E2E_PYTHON=C:/p6v/Scripts/python.exe` is what `scripts/e2e-signup.sh` reads for its Python (default `python3`; `PYTHON` is not used, other tools read it).

- [ ] **Step 1: The mail helper.** Replace `samples/notes-web/e2e/support/mail.ts`:

```ts
import { Page } from '@playwright/test';
import { MAIL_LINK_ORIGIN, mailpitUrl } from './env';

export type LinkKind = 'reset' | 'verify' | 'invite' | 'complete-signup';

const SUBJECT_STARTS: Record<LinkKind, string> = {
  reset: 'Reset your',
  verify: 'Confirm your email',
  invite: 'You are invited',
  'complete-signup': 'Complete your sign-up',
};

// The mail an address with an account gets when someone signs up with it: no link, no token.
const ACCOUNT_EXISTS_SUBJECT = 'You already have an account';

interface MailSummary {
  ID: string;
  Subject: string;
  To: { Address: string }[] | null;
}

interface Mail {
  Text: string;
  HTML: string;
}

async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`the mail catcher answered ${response.status} for ${new URL(url).pathname}`);
  }
  return (await response.json()) as T;
}

/**
 * Waits for the newest mail of a kind to an address and returns its link as a path on the test origin. The link in the mail
 * points at http://localhost:8088; the tests run on https://localhost:8443, so the path and the token are taken over.
 * The result holds a token: never log it.
 */
export async function waitForLink(to: string, kind: LinkKind, timeoutMs = 150_000): Promise<string> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const link = await newestLink(to, kind);
    if (link !== null) {
      return link;
    }
    if (Date.now() > deadline) {
      throw new Error(`no ${kind} mail for ${to} within ${timeoutMs / 1000} seconds`);
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
}

/** The text and the HTML part of the newest "you already have an account" mail to an address. The caller checks that they hold no link. */
export async function waitForAccountExistsMail(to: string, timeoutMs = 150_000): Promise<{ text: string; html: string }> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const mail = await newestMail(to, ACCOUNT_EXISTS_SUBJECT);
    if (mail !== null) {
      return { text: mail.Text, html: mail.HTML };
    }
    if (Date.now() > deadline) {
      throw new Error(`no "you already have an account" mail for ${to} within ${timeoutMs / 1000} seconds`);
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
}

/**
 * Opens a link from waitForLink. A failed navigation must not print the link (Playwright puts the URL in its error, and the URL
 * holds a token): the error here says only that it failed.
 */
export async function openMailLink(page: Page, link: string): Promise<void> {
  try {
    await page.goto(link);
  } catch {
    throw new Error('opening the link of a mail failed (not printed: the link holds a token)');
  }
}

/** The newest mail to exactly this address whose subject starts so, or null. */
async function newestMail(to: string, subjectStart: string): Promise<Mail | null> {
  const found = await getJson<{ messages: MailSummary[] }>(
    `${mailpitUrl()}/api/v1/search?query=${encodeURIComponent(`to:${to}`)}`,
  );
  // The search matches part of an address: the mail must be to this address and no other.
  const summary = found.messages.find(
    (message) =>
      message.Subject.startsWith(subjectStart) &&
      (message.To ?? []).some((recipient) => recipient.Address.toLowerCase() === to.toLowerCase()),
  );
  return summary === undefined ? null : await getJson<Mail>(`${mailpitUrl()}/api/v1/message/${summary.ID}`);
}

async function newestLink(to: string, kind: LinkKind): Promise<string | null> {
  const mail = await newestMail(to, SUBJECT_STARTS[kind]);
  if (mail === null) {
    return null;
  }
  const match = new RegExp(`(https?://[^\\s/]+)/${kind}\\?token=([A-Za-z0-9_-]+)`).exec(mail.Text);
  if (match === null) {
    throw new Error(`the ${kind} mail for ${to} holds no link of the form /${kind}?token=...`);
  }
  if (match[1] !== MAIL_LINK_ORIGIN) {
    throw new Error(`the ${kind} mail for ${to} points at ${match[1]}, not at ${MAIL_LINK_ORIGIN}`);
  }
  if (!mail.HTML.includes(match[2])) {
    throw new Error(`the HTML part of the ${kind} mail for ${to} does not hold the token of its text part`);
  }
  return `/${kind}?token=${match[2]}`;
}
```

- [ ] **Step 2: The Playwright tests.** `samples/notes-web/e2e/09-signup.spec.ts`:

```ts
import { expect, test } from '@playwright/test';
import { acceptInviteByApi } from './support/api';
import { setting } from './support/env';
import { openMailLink, waitForAccountExistsMail, waitForLink } from './support/mail';
import { expectCleanAddressBar, expectNoEmailInAddressBar } from './support/page';
import { submitLogin } from './support/session';

// The request wakes the server's dispatcher, so a mail comes within seconds; an invitation queued by the operator CLI waits for the poll
// (a minute or two). Each wait is below the test's timeout.
const MAIL_WAIT_MS = 100_000;
const SENT = 'Check your inbox. We sent a message to this address.';
const LINK_USED_UP = 'This link has expired or was already used.';

function unique(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1_000_000)}`;
}

test('a visitor signs up, opens the mail link, finishes, signs in and is the admin of a company of their own', async ({ page }) => {
  test.setTimeout(240_000);
  const email = `${unique('signup')}@e2e.test`;
  const typedCompany = `Typed ${unique('co')}`;
  const company = `Final ${unique('co')}`;
  const password = setting('E2E_USER_PASSWORD');
  // The address of every request the page makes: the person's address is in a body only, never in an address (criterion 22).
  const requested: string[] = [];
  page.on('request', (request) => requested.push(request.url()));

  // The sign-in screen offers the way in because Auth-Core says that sign-up is on.
  await page.goto('/login');
  await page.getByRole('link', { name: 'Create an account' }).click();
  await expect(page).toHaveURL(/\/signup$/);
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Company name').fill(typedCompany);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByTestId('signup-sent')).toHaveText(SENT);
  expectNoEmailInAddressBar(page);

  // The link of the mail opens the completion screen; the token leaves the address bar at once.
  const link = await waitForLink(email, 'complete-signup', MAIL_WAIT_MS);
  await openMailLink(page, link);
  await expect(page).toHaveURL(/\/complete-signup$/); // the token has been taken out of the address
  await expect(page.getByRole('heading', { name: 'Finish creating your account' })).toBeVisible();
  expectCleanAddressBar(page);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  await expect(page.getByLabel('Email')).not.toBeEditable();
  await expect(page.getByLabel('Company name')).toHaveValue(typedCompany);

  // A weak password: the rules it breaks, one line each; the link is still good afterwards.
  await page.getByLabel('New password').fill('short');
  await page.getByLabel('Repeat the password').fill('short');
  await page.getByRole('button', { name: 'Create company' }).click();
  const rules = page.getByTestId('rules');
  await expect(rules).toContainText('The password is too short.');
  await expect(rules).toContainText('The password needs an uppercase letter.');
  await expect(rules).toContainText('The password needs a digit.');

  // The name of the request was a suggestion: the person changes it, chooses the password and finishes.
  await page.getByLabel('Company name').fill(company);
  await page.getByLabel('New password').fill(password);
  await page.getByLabel('Repeat the password').fill(password);
  await page.getByRole('button', { name: 'Create company' }).click();

  // No sign-in follows: the sign-in screen opens with the address filled in, and the address is not in the URL.
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  expectNoEmailInAddressBar(page);

  await submitLogin(page, email, password);
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-company')).toHaveText(company);
  await expect(page.getByTestId('me-role')).toHaveText('admin');
  // A company of their own: not the notes of the seeded company.
  await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toHaveCount(0);
  await expect(page.getByText('No notes yet.')).toBeVisible();

  // The link is used up.
  await openMailLink(page, link);
  await expect(page.getByText(LINK_USED_UP)).toBeVisible();
  await page.getByRole('link', { name: 'Sign up again' }).click();
  await expect(page).toHaveURL(/\/signup$/);

  // The address was in no URL the page asked for, plain or encoded.
  expect(requested.length, 'no request was seen').toBeGreaterThan(5);
  expect(
    requested.some((url) => url.includes(email) || url.includes(encodeURIComponent(email)) || url.includes('%40')),
    'the address is in the URL of a request',
  ).toBe(false);
});

test('an address that already has an account gets the same answer and a mail that says so, with no link', async ({ page }) => {
  test.setTimeout(240_000);
  // The development user has an account on every clean stack, whatever the other tests did.
  const email = setting('E2E_SEED_EMAIL');

  await page.goto('/signup');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Company name').fill(`Other ${unique('co')}`);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByTestId('signup-sent')).toHaveText(SENT); // the sentence a new address gets

  const mail = await waitForAccountExistsMail(email, MAIL_WAIT_MS);
  expect(mail.text.includes('already exists'), 'the mail does not say that the account exists').toBe(true);
  for (const part of [mail.text, mail.html]) {
    expect(part.includes('token='), 'the mail holds a token').toBe(false);
    expect(part.includes('://'), 'the mail holds a link').toBe(false);
  }
});

test('an account made between the request and the link: the completion screen says it exists and offers the sign-in', async ({ page }) => {
  test.setTimeout(300_000);
  // The script invited this address through the operator CLI; the invitation mail comes at the server's next poll.
  const email = setting('E2E_BOTH_EMAIL');
  const password = setting('E2E_USER_PASSWORD');

  await page.goto('/signup');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Company name').fill(`Both ${unique('co')}`);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByTestId('signup-sent')).toHaveText(SENT);
  const link = await waitForLink(email, 'complete-signup', MAIL_WAIT_MS);

  // The invitation is accepted by the API: the account exists now, and the sign-up link is still unused.
  await acceptInviteByApi(await waitForLink(email, 'invite', MAIL_WAIT_MS), password);

  await openMailLink(page, link);
  await page.getByLabel('New password').fill(password);
  await page.getByLabel('Repeat the password').fill(password);
  await page.getByRole('button', { name: 'Create company' }).click();
  await expect(page.getByText('An account with this address already exists.')).toBeVisible();
  await expect(page.getByLabel('New password')).toHaveCount(0); // no form: there is nothing left to finish
  await page.getByRole('link', { name: 'Sign in.' }).click();
  await expect(page).toHaveURL(/\/login$/);
});

test('a link that expired says so and offers to sign up again', async ({ page }) => {
  // The script asked for this sign-up and made the end of its link lie in the past (the database, not the clock of the test).
  const link = await waitForLink(setting('E2E_EXPIRED_EMAIL'), 'complete-signup', MAIL_WAIT_MS);

  await openMailLink(page, link);
  await expect(page.getByText(LINK_USED_UP)).toBeVisible();
  expectCleanAddressBar(page);

  await page.getByRole('link', { name: 'Sign up again' }).click();
  await expect(page).toHaveURL(/\/signup$/);
});

test('a link that never worked says so and offers to sign up again', async ({ page }) => {
  await page.goto(`/complete-signup?token=${'x'.repeat(43)}`);
  await expect(page.getByText(LINK_USED_UP)).toBeVisible();
  expectCleanAddressBar(page);

  await page.getByRole('link', { name: 'Sign up again' }).click();
  await expect(page).toHaveURL(/\/signup$/);
});
```

- [ ] **Step 2b: Two addresses for the browser tests, seeded by `scripts/e2e-web.sh`.** The tests of the "already registered" screen and of an expired link need state that a browser cannot make: an address that is invited (so that an account can appear between the request and the link) and a link whose end lies in the past. In `scripts/e2e-web.sh`:

(a) In the comment block at the top, after the line `# The CLI only queues the mail, the server sends it within a minute or two; the tests wait for the mails.` add

```bash
# For the sign-up tests (spec 0009) it also invites BOTH (the test signs it up, and accepts the invitation before it uses the link, so that
# the completion finds an account) and prepares EXPIRED: it asks for a sign-up through the API, waits for the mail, and moves the end of the
# link into the past with one UPDATE of "PendingSignups"."TokenExpiresAt" (the database, because the test cannot move the server's clock).
```

(b) Before the function `seed_stack() {` add

```bash
mail_total() { # mail_total <address>: how many mails the catcher holds for the address
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" \
    | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>process.stdout.write(String(JSON.parse(s).messages_count)))'
}

wait_mails() { # wait_mails <address> <count> <seconds>: waits until the catcher holds at least <count> mails for the address
  local i
  for i in $(seq 1 "$3"); do
    if [[ "$(mail_total "$1" || true)" -ge "$2" ]] 2>/dev/null; then
      return 0
    fi
    sleep 1
  done
  return 1
}

```

(c) In `seed_stack`, add `updated` to the `local` list of its first line (`local run="$RANDOM$RANDOM" code org token updated`), after the line `RESETTER_EMAIL="resetter-$run@e2e.test"` add

```bash
  BOTH_EMAIL="both-$run@e2e.test"
  EXPIRED_EMAIL="expired-$run@e2e.test"
```

and after the line `cli invite --org "$org" --email "$RESETTER_EMAIL" --role user` add

```bash
  cli invite --org "$org" --email "$BOTH_EMAIL" --role user
  # An expired sign-up link: ask through the API, wait for the mail, then move the end of the link into the past.
  E2E_X_EMAIL="$EXPIRED_EMAIL" node -e 'console.log(JSON.stringify({email: process.env.E2E_X_EMAIL, company_name: "Expired Company"}))' > "$tmp/expired.json"
  code="$(curl -sS --max-time 20 -o /dev/null -w '%{http_code}' -X POST "$HTTP_URL/auth/signup" \
    -H 'Content-Type: application/json' --data-binary "@$tmp/expired.json")" || fail "seeding: the sign-up request for the expired link failed"
  [[ "$code" == "202" ]] || fail "seeding: the sign-up request for the expired link answered $code, not 202"
  wait_mails "$EXPIRED_EMAIL" 1 150 || fail "seeding: no sign-up mail for the expired link within 150s"
  updated="$(printf '%s\n' "UPDATE \"PendingSignups\" SET \"TokenExpiresAt\" = now() - interval '1 minute' WHERE \"NormalizedEmail\" = upper('$EXPIRED_EMAIL')" \
    | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r')" || fail "seeding: psql could not move the end of the link"
  [[ "$updated" == "UPDATE 1" ]] || fail "seeding: the sign-up link was not made to expire"
  rm -f "$tmp/expired.json"
```

(d) After the line `export E2E_VIEWER_EMAIL="$VIEWER_EMAIL" E2E_RESETTER_EMAIL="$RESETTER_EMAIL"` add

```bash
  export E2E_BOTH_EMAIL="$BOTH_EMAIL" E2E_EXPIRED_EMAIL="$EXPIRED_EMAIL"
```

(`seed_stack` assigns its addresses as globals, as it does for `VIEWER_EMAIL`; the Playwright run reads them as settings.)

(e) In `scripts/e2e-tenancy.sh`, after the line `#   scripts/e2e-tenancy.sh                # this script (does NOT bring the stack up or down)` add `#   scripts/e2e-signup.sh                 # spec 0009 (self-service sign-up), which runs after this one`; in `scripts/e2e-hardening.sh` change the line `#   scripts/e2e-login.sh && scripts/e2e-refresh.sh && scripts/e2e-lockout.sh && scripts/e2e-email.sh && scripts/e2e-tenancy.sh` to end with `&& scripts/e2e-tenancy.sh && scripts/e2e-signup.sh`.

- [ ] **Step 3: The live script.** `scripts/e2e-signup.sh`:

```bash
#!/usr/bin/env bash
# Real-network end-to-end check of spec 0009 (self-service sign-up) against the compose stack, with the mail catcher (Mailpit) in it. It
# drives the sequence of the spec's Goal - a visitor asks for a sign-up, opens the link of the mail, chooses a password and signs in as the
# first admin of a company of their own - and the cases around it.
#
# What it checks, in order (NEW = the address that signs up, SEED = the development seed user, PENDING = an address that asks and never
# finishes, INVITED = an address that is invited and also signs up, EXPIRED = an address whose link is made to have expired). Each step
# names the criteria it touches; the ones it touches only in part are marked "part of", and the guarding tests are in the acceptance map.
#   1. health answers 200 "Healthy" (the development settings name the completion address and deploy/auth.yaml says self-service); the
#      mail catcher answers; GET /auth/options -> 200 {"signup":"self-service"}, never stored; the OpenAPI description names the four
#      paths - part of criteria 1 (the other answers of options) and 20 (the answers and bodies of the operations).
#   2. POST /auth/signup for NEW -> 202 with an empty body, never stored, no cookie; the same request at once -> 429 too_many_attempts
#      with Retry-After - part of criteria 3 (the mail is step 4) and 6 (the hourly limit and the independence of the kinds).
#   3. a malformed address, a look-alike letter (built with chr()), a domain that is an address or has no dot, a bad company name and a
#      body that is not an object -> 400 invalid_request; what was refused made nothing (no pending sign-up, no mail: checked after
#      step 4) - criterion 5.
#   4. the mail arrives: the subject names the application and does not hold the company; the text and the HTML part hold the same
#      link <complete_signup url>?token=<43 characters> and the company name - criteria 3, 21 (part of: Polish).
#   5. preview with the token -> 200 with exactly the typed address and the company name, twice (it changes nothing); an unknown token
#      -> 400 invalid_token - part of criterion 8 (an expired token is step 11).
#   6. complete with a weak password -> 400 weak_password naming the rules, and the token still works; then with a good password and
#      another company name -> 204 with an empty body and no cookie; again -> 400 invalid_token - criteria 11, 12 (part of: parallel
#      completions), 9 (part of: steps 7 and 8 finish it).
#   7. NEW logs in (the email is confirmed): roles ["admin"], the permissions of the role, the org_id of the new company, which is not
#      the development company's, and GET /auth/me names the company as given at the completion; the audit log holds signup.requested,
#      signup.completed (this account, this company, this address) and org.created with via signup, none holding the password, and the
#      pending sign-up is gone - criteria 9, 10 (part of: the other manifests), 19 (part of: atomicity).
#   8. a minute later the same request for NEW -> the same 202 and the same header names as in step 2, and a mail that says the account
#      exists, with no link and no token; the pending sign-up is gone; NEW's password still works; list-orgs shows the company with
#      exactly one member and no second company - criterion 4.
#   9. PENDING asks and never finishes: its login is the 401 of an unknown address, forgot password sends it nothing, and the limit of
#      forgot is not the limit of sign-up - criterion 15, part of 6.
#  10. INVITED is invited through the operator CLI and asks for a sign-up; the invitation is accepted first; the completion is then
#      409 already_registered, makes no company, and its pending sign-up is gone; INVITED is a member of the development company -
#      criterion 14 (part of: the parallel case).
#  11. EXPIRED asks; once its mail has arrived the end of its link is moved into the past (one UPDATE of "PendingSignups"."TokenExpiresAt",
#      as scripts/e2e-web.sh does for the browser test); preview and complete answer 400 invalid_token and no account is made - part of
#      criteria 8 and 13 (the 24 hours themselves, and the hourly clean-up, are guarded by the tests).
# Criteria 2, 7 (the replacement of a link), 16, 17, 18, 22 and 23's other halves are guarded by the integration tests and the Playwright
# tests: see docs/superpowers/plans/0009-acceptance-map.md.
#
# Full sequence, from the repo root (the same stack and .env as the other e2e scripts, with the per-IP limiter OFF as for them; run this
# one after e2e-tenancy.sh and before e2e-hardening.sh, which recreates the stack with the limits on):
#   cp .env.example .env                  # then set real local values (git-ignored)
#   scripts/dev-keys.sh                   # dev signing/encryption keys into .secrets/ (git-ignored); on Windows: MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh
#   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, never "auth-core" (the development stack); the scripts default to this name and refuse "auth-core"
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build
#   scripts/e2e-login.sh; scripts/e2e-refresh.sh; scripts/e2e-lockout.sh; scripts/e2e-email.sh; scripts/e2e-tenancy.sh
#   scripts/e2e-signup.sh                 # this script (does NOT bring the stack up or down)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# Reads AUTH_DEV_SEED_EMAIL and AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced). The operator commands run as
# `docker compose run --rm -T --no-deps auth admin ...`, and the database is read and, in step 11, changed with `docker compose exec postgres
# psql`. Needs: curl, python3 (standard library only; E2E_PYTHON=<interpreter> names another, for example E2E_PYTHON=C:/p6v/Scripts/python.exe on
# the owner's machine), docker compose. Env: BASE_URL (default http://localhost:8080),
# MAILPIT_URL (default http://localhost:8025). The links in the mails start with http://localhost:4200/complete-signup and
# http://localhost:4200/invite (appsettings.Development.json). Run it with the per-IP limits OFF, as the stack of the header is started: steps 2 and 3 alone make all ten mail-policy
# requests a minute that the default limit allows. Takes about five minutes: one wait of 62 seconds for the mail limit, and up to a minute for the server to pick
# up the invitation the CLI queued. Re-runnable on the same stack: every address and company is made for the run.
# Exits non-zero on the first failure; prints "PASS <step>" per step; never prints a password, a token, a cookie or a mail body. Request
# bodies are built into files in a mktemp -d directory (removed on exit) and curl reads them with --data-binary @file; tokens and cookies
# reach curl through header files.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
PY="${E2E_PYTHON:-python3}"
# The stack of these scripts is its own compose project, never "auth-core" (the development stack): the one-off containers below meet the same stack as the down -v of the header.
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-hardening}"
[[ "$COMPOSE_PROJECT_NAME" != "auth-core" ]] || { echo "FAIL COMPOSE_PROJECT_NAME=auth-core is the development stack; use another name" >&2; exit 1; }
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
NEW_EMAIL="anna-$run@firma.test"
INVITED_EMAIL="ola-$run@firma.test"
PENDING_EMAIL="pending-$run@firma.test"
UNKNOWN_EMAIL="nobody-$run@example.invalid"
COMPANY="Firma $run"
RENAMED="Firma Renamed $run"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
export E2E_NEW_EMAIL="$NEW_EMAIL" E2E_INVITED_EMAIL="$INVITED_EMAIL" E2E_PENDING_EMAIL="$PENDING_EMAIL" E2E_UNKNOWN_EMAIL="$UNKNOWN_EMAIL"
export E2E_COMPANY="$COMPANY" E2E_RENAMED="$RENAMED"
export E2E_PASSWORD="E2e-Passw0rd-$run" E2E_INVITED_PASSWORD="E2e-Invited-Passw0rd-$run" E2E_WEAK_PASSWORD="abc"

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

# The python3 of some machines is a Windows interpreter, which ends its lines with CR LF: the helpers below that print a value strip
# the CR, so that the value compares equal in bash.

# val <python-expression-of-d>: evaluates it against the JSON of $BODY and prints the result
val() { "$PY" -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1" <<< "$BODY" | tr -d '\r'; }

# jwt_payload <auth-header-file>: the payload of the bearer token in the file, as JSON (the token is never printed)
jwt_payload() {
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

expect_no_cookie() { [[ -z "$(header set-cookie)" ]] || fail "$1: the response set a cookie"; }

expect_eq() { [[ "$2" == "$3" ]] || fail "$1: got '$2', expected '$3'"; }

# login <name> <email-env> <password-env>: logs in, keeps the bearer header in $tmp/<name>.auth
login() {
  json_body "$tmp/$1.login" "{'email': os.environ['$2'], 'password': os.environ['$3']}"
  call POST /auth/login "$tmp/$1.login"
  expect_status "login of $1" 200
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$1.auth"
}

# --- the mail catcher ---------------------------------------------------------------------------------------------

mail_count() {
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" -o "$tmp/search.json"
  "$PY" -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages_count"])' < "$tmp/search.json" | tr -d '\r'
}

# wait_mail <address> <subject-start> <seconds>: waits until a mail to exactly this address has a subject that starts so, then saves the
# newest of them to $tmp/mail.json
wait_mail() {
  local i id
  for i in $(seq 1 "$3"); do
    curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" -o "$tmp/search.json"
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
    sleep 1
  done
  return 1
}

# token_body <link-prefix> <file> [password-env] [company-env]: takes the token from the text part of $tmp/mail.json, checks that the HTML
# part holds the same token, and writes the request body ({"token"}, plus "password" and "company_name" when named). Never printed.
token_body() {
  "$PY" -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(re.escape(sys.argv[1]) + r"\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no link with a token")
if match.group(1) not in mail["HTML"]:
    sys.exit("the HTML part does not hold the link of the text part")
body = {"token": match.group(1)}
if sys.argv[2]:
    body["password"] = os.environ[sys.argv[2]]
if sys.argv[3]:
    body["company_name"] = os.environ[sys.argv[3]]
print(json.dumps(body))
' "$1" "${3:-}" "${4:-}" < "$tmp/mail.json" > "$2"
}

mail_field() { "$PY" -c 'import json,sys; print(json.load(sys.stdin.buffer)[sys.argv[1]])' "$1" < "$tmp/mail.json" | tr -d '\r'; }

# mail_holds <env-name>: whether the text part and the HTML part of $tmp/mail.json both hold the value of that variable
mail_holds() {
  "$PY" -c '
import json, os, sys
mail = json.load(sys.stdin.buffer)
value = os.environ[sys.argv[1]]
sys.exit(0 if value in mail["Text"] and value in mail["HTML"] else 1)
' "$1" < "$tmp/mail.json"
}

# mail_has_no_link: neither part of $tmp/mail.json holds a token parameter or a URL
mail_has_no_link() {
  "$PY" -c '
import json, sys
mail = json.load(sys.stdin.buffer)
sys.exit(0 if all("token=" not in part and "://" not in part for part in (mail["Text"], mail["HTML"])) else 1)
' < "$tmp/mail.json"
}

# --- the operator CLI, in the service's own image ----------------------------------------------------------------

# cli <args...>: runs `auth-server admin <args>` in a one-off container; sets CLI_OUT (stdout), CLI_EXIT, and keeps stderr in $tmp/cli.err
cli() {
  CLI_EXIT=0
  CLI_OUT="$("${compose[@]}" run --rm -T --no-deps auth admin "$@" 2> "$tmp/cli.err")" || CLI_EXIT=$?
  CLI_OUT="${CLI_OUT//$'\r'/}"
}

# psql_value <sql>: the value the query returns. The SQL is this script's own text: a value that came from the service under test is never put into it.
psql_value() {
  printf '%s\n' "$1" | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'
}

# --- Step 1: health, the mail catcher, the options, the description (criteria 1, 20) ----------------------------------
wait_healthy || fail "step 1: $BASE_URL/auth/health did not return 200 within 90s"
wait_mailpit || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s (is the mailpit service up?)"
call GET /auth/health
expect_status "step 1: health" 200 "Healthy"
call GET /auth/options
expect_status "step 1: the options" 200 '{"signup":"self-service"}'
expect_no_store "step 1: the options"
expect_no_cookie "step 1: the options"
call GET /auth/openapi/v1.json
expect_status "step 1: the OpenAPI description" 200
for path in /auth/options /auth/signup /auth/signup/preview /auth/signup/complete; do
  [[ "$(val "'$path' in d['paths']")" == "True" ]] || fail "step 1: $path is not in the OpenAPI description"
done
pass "step 1: health is Healthy; the mail catcher answers; GET /auth/options says self-service (never stored); the OpenAPI description names the four paths"

# --- Step 2: the request, and its limit (criteria 3, 6) ---------------------------------------------------------------
json_body "$tmp/signup.json" "{'email': os.environ['E2E_NEW_EMAIL'], 'company_name': os.environ['E2E_COMPANY']}"
call POST /auth/signup "$tmp/signup.json"
expect_status "step 2: the sign-up request" 202 ""
expect_no_store "step 2: the 202"
expect_no_cookie "step 2: the 202"
NEW_HEADERS="$(header_names)"
call POST /auth/signup "$tmp/signup.json"
expect_status "step 2: the same request at once" 429
[[ "$BODY" =~ ^\{\"error\":\"too_many_attempts\",\"retry_after_seconds\":([0-9]+)\}$ ]] || fail "step 2: unexpected 429 body"
RETRY="${BASH_REMATCH[1]}"
[[ "$(header retry-after)" == "$RETRY" ]] || fail "step 2: Retry-After differs from retry_after_seconds"
# Up to 62, not 60: on Docker Desktop the container clock can step slightly, and a 60 s wait then reads 61 or 62.
(( RETRY >= 1 && RETRY <= 62 )) || fail "step 2: retry_after_seconds $RETRY is not between 1 and 62"
expect_no_store "step 2: the 429"
pass "step 2: the request -> 202 without a body (never stored, no cookie); the same request at once -> 429 too_many_attempts with Retry-After between 1 and 62"

# --- Step 3: what is refused (criterion 5) ---------------------------------------------------------------------------
JOE_EMAIL="joe-$run@firma.test"
for refused in "bob.firma.test|Firma" "joe@localhost|Firma" "joe@10.0.0.5|Firma" "joe@[10.0.0.5]|Firma" "$JOE_EMAIL| Leading space" "$JOE_EMAIL|Trailing space "; do
  json_body "$tmp/bad.json" "{'email': '${refused%%|*}', 'company_name': '${refused#*|}'}"
  call POST /auth/signup "$tmp/bad.json"
  expect_error "step 3: '${refused%%|*}' with the name '${refused#*|}'" 400 invalid_request
done
# The letter after the first one is the long s (U+017F), which stands for an ASCII "s" in some software: built here with chr().
json_body "$tmp/bad.json" "{'email': chr(0x17f) + 'nna-$run@firma.test', 'company_name': 'Firma'}"
call POST /auth/signup "$tmp/bad.json"
expect_error "step 3: a look-alike of an ASCII letter in the address" 400 invalid_request
printf '[]' > "$tmp/array.json"
call POST /auth/signup "$tmp/array.json"
expect_error "step 3: a body that is not an object" 400 invalid_request
pass "step 3: a malformed address, a look-alike letter, a domain that is an address or has no dot, a name with white space at an end and a body that is not an object -> 400 invalid_request (that nothing was made is checked after step 4)"

# --- Step 4: the mail (criteria 3, 21) --------------------------------------------------------------------------------
wait_mail "$NEW_EMAIL" "Complete your sign-up" 90 || fail "step 4: no sign-up mail for NEW within 90s"
[[ "$(mail_field Subject)" == *auth-core-dev* ]] || fail "step 4: the subject does not carry the application name"
[[ "$(mail_field Subject)" != *"$COMPANY"* ]] || fail "step 4: the subject holds the company name, which a visitor typed"
mail_holds E2E_COMPANY || fail "step 4: the text and the HTML part do not both hold the company name"
token_body http://localhost:4200/complete-signup "$tmp/preview.json" || fail "step 4: no usable sign-up link in the mail"
token_body http://localhost:4200/complete-signup "$tmp/complete-weak.json" E2E_WEAK_PASSWORD E2E_RENAMED || fail "step 4: the weak completion body could not be made"
token_body http://localhost:4200/complete-signup "$tmp/complete-good.json" E2E_PASSWORD E2E_RENAMED || fail "step 4: the good completion body could not be made"
# The requests of step 3 that were refused made nothing: the dispatcher has had its time (the mail of NEW went out) and JOE has no mail and no pending sign-up.
expect_eq "step 4: no mail for the address of the refused names" "$(mail_count "$JOE_EMAIL")" "0"
expect_eq "step 4: no pending sign-up for the address of the refused names" "$(psql_value "SELECT count(*) FROM \"PendingSignups\" WHERE \"NormalizedEmail\" = upper('$JOE_EMAIL')")" "0"
pass "step 4: the mail arrived (the subject names the application and not the company; the text and the HTML part hold the same link and the company name); the refused requests of step 3 made no mail and no pending sign-up"

# --- Step 5: the preview (criterion 8) --------------------------------------------------------------------------------
call POST /auth/signup/preview "$tmp/preview.json"
expect_status "step 5: preview" 200 "{\"email\":\"$NEW_EMAIL\",\"company_name\":\"$COMPANY\"}"
expect_no_store "step 5: preview"
call POST /auth/signup/preview "$tmp/preview.json"
expect_status "step 5: preview again" 200 "{\"email\":\"$NEW_EMAIL\",\"company_name\":\"$COMPANY\"}"
"$PY" -c 'import json; print(json.dumps({"token": "x" * 43}))' > "$tmp/unknown-token.json"
call POST /auth/signup/preview "$tmp/unknown-token.json"
expect_error "step 5: preview of an unknown token" 400 invalid_token
pass "step 5: preview -> 200 with exactly the typed address and the company name, twice; an unknown token -> 400 invalid_token"

# --- Step 6: the completion (criteria 9, 11, 12) ----------------------------------------------------------------------
call POST /auth/signup/complete "$tmp/complete-weak.json"
expect_status "step 6: complete with a weak password" 400 '{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}'
call POST /auth/signup/complete "$tmp/complete-good.json"
expect_status "step 6: complete with a good password and another company name" 204 ""
expect_no_store "step 6: the 204"
expect_no_cookie "step 6: the 204 (it does not sign in)"
call POST /auth/signup/complete "$tmp/complete-good.json"
expect_error "step 6: the same completion again" 400 invalid_token
call POST /auth/signup/preview "$tmp/preview.json"
expect_error "step 6: preview of the used token" 400 invalid_token
pass "step 6: a weak password -> 400 weak_password naming the rules (the token still works); a good one -> 204 with no body and no cookie; again -> 400 invalid_token"

# --- Step 7: the new admin signs in (criteria 9, 10) ------------------------------------------------------------------
login new E2E_NEW_EMAIL E2E_PASSWORD
BODY="$(jwt_payload "$tmp/new.auth")"
expect_eq "step 7: roles" "$(val 'd["roles"]')" "['admin']"
expect_eq "step 7: permissions" "$(val 'd["permissions"]')" "['documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:delete', 'org:manage', 'roles:manage']"
NEW_ORG="$(val 'd["org_id"]')"
call GET /auth/me "" "$tmp/new.auth"
expect_status "step 7: GET /auth/me" 200
expect_eq "step 7: the company is the one named at the completion" "$(val 'd["org_name"]')" "$RENAMED"
expect_eq "step 7: the address" "$(val 'd["email"]')" "$NEW_EMAIL"
expect_eq "step 7: org_id of /auth/me and of the token" "$(val 'd["org_id"]')" "$NEW_ORG"
login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD
call GET /auth/me "" "$tmp/seed.auth"
expect_status "step 7: GET /auth/me of SEED" 200
DEV_ORG="$(val 'd["org_id"]')"
[[ "$NEW_ORG" != "$DEV_ORG" ]] || fail "step 7: the new admin belongs to the development company"
expect_eq "step 7: the pending sign-up of NEW is gone" "$(psql_value "SELECT count(*) FROM \"PendingSignups\" WHERE \"NormalizedEmail\" = upper('$NEW_EMAIL')")" "0"
expect_eq "step 7: one signup.requested row for NEW" "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.requested' AND subject_email = '$NEW_EMAIL'")" "1"
expect_eq "step 7: one signup.completed row, for this account, this company and this address" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.completed' AND org_id = '$NEW_ORG' AND org_name = '$RENAMED' AND subject_email = '$NEW_EMAIL' AND subject_user_id IS NOT NULL")" "1"
expect_eq "step 7: org.created with via signup, in the same company" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'org.created' AND org_id = '$NEW_ORG' AND details->>'via' = 'signup'")" "1"
expect_eq "step 7: no audit row holds the password" "$(psql_value "SELECT count(*) FROM audit_events WHERE row_to_json(audit_events)::text LIKE '%$E2E_PASSWORD%'")" "0"
pass "step 7: NEW logs in (its email is confirmed): roles [admin] and the permissions of the role, a company of its own named as given at the completion, not the development company; the audit log holds the three rows of the contract and no password, and the pending sign-up is gone"

# --- Step 8: the same address again (criterion 4) ---------------------------------------------------------------------
echo "waiting 62 s for the mail limit of NEW..."
sleep 62
call POST /auth/signup "$tmp/signup.json"
expect_status "step 8: the request for an address with an account" 202 ""
[[ "$(header_names)" == "$NEW_HEADERS" ]] || fail "step 8: the response headers differ from those of a new address (step 2)"
wait_mail "$NEW_EMAIL" "You already have an account" 90 || fail "step 8: no 'you already have an account' mail for NEW within 90s"
mail_has_no_link || fail "step 8: the mail holds a link or a token"
login new E2E_NEW_EMAIL E2E_PASSWORD
# The pending sign-up goes in the transaction of the send, which commits just after the relay has the mail.
sleep 3
expect_eq "step 8: the pending sign-up is gone" "$(psql_value "SELECT count(*) FROM \"PendingSignups\" WHERE \"NormalizedEmail\" = upper('$NEW_EMAIL')")" "0"
cli list-orgs
[[ "$CLI_EXIT" == "0" ]] || fail "step 8: the operator CLI failed: admin list-orgs"
grep -qF "$NEW_ORG"$'\t'"$RENAMED"$'\t'"1" <<< "$CLI_OUT" || fail "step 8: list-orgs does not show the company of the completion with exactly one member"
expect_eq "step 8: no company was made for the second request" "$(grep -cF $'\t'"$COMPANY"$'\t' <<< "$CLI_OUT" || true)" "0"
pass "step 8: the same request for NEW -> the same 202 and header names; the mail says the account exists and holds no link; the pending sign-up is gone; its password works; the company has exactly one member and no second company exists"

# --- Step 9: a pending sign-up is not an account (criteria 6, 15) -----------------------------------------------------
json_body "$tmp/pending.json" "{'email': os.environ['E2E_PENDING_EMAIL'], 'company_name': os.environ['E2E_COMPANY']}"
call POST /auth/signup "$tmp/pending.json"
expect_status "step 9: the request of PENDING" 202 ""
wait_mail "$PENDING_EMAIL" "Complete your sign-up" 90 || fail "step 9: no sign-up mail for PENDING within 90s"
json_body "$tmp/pending.login" "{'email': os.environ['E2E_PENDING_EMAIL'], 'password': os.environ['E2E_PASSWORD']}"
call POST /auth/login "$tmp/pending.login"
expect_error "step 9: login of an address with only a pending sign-up" 401 invalid_credentials
json_body "$tmp/unknown.login" "{'email': os.environ['E2E_UNKNOWN_EMAIL'], 'password': os.environ['E2E_PASSWORD']}"
call POST /auth/login "$tmp/unknown.login"
expect_error "step 9: login of an unknown address" 401 invalid_credentials
json_body "$tmp/pending-email.json" "{'email': os.environ['E2E_PENDING_EMAIL']}"
call POST /auth/password/forgot "$tmp/pending-email.json"
expect_status "step 9: forgot password for PENDING (the sign-up limit is not the limit of forgot)" 202 ""
# The dispatcher removes the row of an address without an account within seconds; give it time, or the count below would only mean that nothing was sent yet.
sleep 8
expect_eq "step 9: PENDING got no mail but the sign-up mail" "$(mail_count "$PENDING_EMAIL")" "1"
pass "step 9: a pending sign-up is no account: login is the 401 of an unknown address, and forgot password sends it nothing"

# --- Step 10: an invitation and a sign-up for one address (criterion 14) ---------------------------------------------
cli invite --org "$DEV_ORG" --email "$INVITED_EMAIL" --role user
[[ "$CLI_EXIT" == "0" ]] || fail "step 10: the operator CLI failed: admin invite"
json_body "$tmp/invited.json" "{'email': os.environ['E2E_INVITED_EMAIL'], 'company_name': os.environ['E2E_COMPANY']}"
call POST /auth/signup "$tmp/invited.json"
expect_status "step 10: the sign-up request of INVITED" 202 ""
wait_mail "$INVITED_EMAIL" "Complete your sign-up" 90 || fail "step 10: no sign-up mail for INVITED within 90s"
token_body http://localhost:4200/complete-signup "$tmp/invited-complete.json" E2E_PASSWORD E2E_COMPANY || fail "step 10: no usable sign-up link in the mail"
wait_mail "$INVITED_EMAIL" "You are invited" 150 || fail "step 10: no invitation mail for INVITED within 150s"
token_body http://localhost:4200/invite "$tmp/invited-accept.json" E2E_INVITED_PASSWORD || fail "step 10: no usable invitation link in the mail"
call POST /auth/invites/accept "$tmp/invited-accept.json"
expect_status "step 10: the invitation is accepted" 204 ""
call POST /auth/signup/complete "$tmp/invited-complete.json"
expect_error "step 10: the completion after the acceptance" 409 already_registered
call POST /auth/signup/complete "$tmp/invited-complete.json"
expect_error "step 10: the completion again (the pending sign-up is gone)" 400 invalid_token
expect_eq "step 10: the pending sign-up of INVITED is gone" "$(psql_value "SELECT count(*) FROM \"PendingSignups\" WHERE \"NormalizedEmail\" = upper('$INVITED_EMAIL')")" "0"
login invited E2E_INVITED_EMAIL E2E_INVITED_PASSWORD
BODY="$(jwt_payload "$tmp/invited.auth")"
expect_eq "step 10: INVITED is a member of the development company" "$(val 'd["org_id"]')" "$DEV_ORG"
expect_eq "step 10: INVITED has the role of the invitation" "$(val 'd["roles"]')" "['user']"
cli list-orgs
[[ "$CLI_EXIT" == "0" ]] || fail "step 10: the operator CLI failed: admin list-orgs"
expect_eq "step 10: no company was made for the completion" "$(grep -cF $'\t'"$COMPANY"$'\t' <<< "$CLI_OUT" || true)" "0"
pass "step 10: the invitation was accepted first -> the completion is 409 already_registered, makes no company and its pending sign-up is gone"

# --- Step 11: a link that has expired (criteria 8, 13) -----------------------------------------------------------------
EXPIRED_EMAIL="expired-$run@firma.test"
export E2E_EXPIRED_EMAIL="$EXPIRED_EMAIL"
json_body "$tmp/expired.json" "{'email': os.environ['E2E_EXPIRED_EMAIL'], 'company_name': os.environ['E2E_COMPANY']}"
call POST /auth/signup "$tmp/expired.json"
expect_status "step 11: the sign-up request of EXPIRED" 202 ""
wait_mail "$EXPIRED_EMAIL" "Complete your sign-up" 90 || fail "step 11: no sign-up mail for EXPIRED within 90s"
token_body http://localhost:4200/complete-signup "$tmp/expired-preview.json" || fail "step 11: no usable sign-up link in the mail"
token_body http://localhost:4200/complete-signup "$tmp/expired-complete.json" E2E_PASSWORD E2E_COMPANY || fail "step 11: no usable sign-up link in the mail"
call POST /auth/signup/preview "$tmp/expired-preview.json"
expect_status "step 11: preview of the live link" 200
# The 24 hours cannot be waited for: the end of the link is moved into the past, as scripts/e2e-web.sh does for the browser test.
expect_eq "step 11: the link was moved into the past" \
  "$(psql_value "UPDATE \"PendingSignups\" SET \"TokenExpiresAt\" = now() - interval '1 minute' WHERE \"NormalizedEmail\" = upper('$EXPIRED_EMAIL')")" "UPDATE 1"
call POST /auth/signup/preview "$tmp/expired-preview.json"
expect_error "step 11: preview of the expired link" 400 invalid_token
call POST /auth/signup/complete "$tmp/expired-complete.json"
expect_error "step 11: completion with the expired link" 400 invalid_token
expect_eq "step 11: no account was made" "$(psql_value "SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = upper('$EXPIRED_EMAIL')")" "0"
pass "step 11: a link whose end lies in the past -> preview and completion are 400 invalid_token, and no account is made"

echo "ALL PASS"
```

- [ ] **Step 4: Check what can be checked here.** In `samples/notes-web`: `npx tsc --noEmit -p e2e/tsconfig.json` (the spec and the helper compile) and `npx ng build` (nothing in `src/` changed in this task). From the repository root: `bash -n scripts/e2e-signup.sh scripts/e2e-web.sh scripts/e2e-tenancy.sh scripts/e2e-hardening.sh`; `grep -n "python3" scripts/e2e-signup.sh` shows only the default of `PY` and the comments; `grep -InP '\\u[0-9a-fA-F]{4}' scripts/e2e-signup.sh samples/notes-web/e2e` finds nothing. Do not start a stack.

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files; `scripts/e2e-signup.sh` is a new shell script (LF): the orchestrator marks it executable. Proposed note for the verifiers: `scripts/e2e-web.sh` now waits for two mails while seeding, so its first start is a little slower. Proposed subject: `test(samples): Playwright and live checks of self-service sign-up`.

---

## Amendment (2026-04-03): the completion signs in

The owner's **Amendment 2** of [spec 0009](../specs/0009-self-service-signup.md) (2026-04-03) replaces Decision 10: **completing a sign-up signs the person in.** `POST /auth/signup/complete` answers exactly as a successful `POST /auth/login` does (specs 0001 and 0002): `200 {"status":"authenticated","access_token"}` and the refresh cookie `auth_rt`, the token carrying the new company and the first admin's role. The `login.succeeded` audit row is written in the completion's own transaction, and **if the sign-in cannot be written nothing of the completion stays.** The sample opens the product (the notes screen) at once instead of `/login`. Accepting an invitation is unchanged (`204`, then the sign-in screen).

Tasks 1 to 12 are implemented and committed (HEAD `9d87bb1`). The real files differ from this plan's text in places (review fixes): **the real files win**; the code below was written against them. This section adds **Task 14** (the server) and **Task 15** (the sample, the Playwright test and `scripts/e2e-signup.sh`) on **Friday 03.04**, and moves **Task 13** (the documents, which now also describe the sign-in) to **Saturday 04.04**. Where the text of Tasks 8, 11 and 12 (`204`, "it does not sign in", the sample going to `/login`) disagrees with this section, **this section wins**: those tasks are history, and Tasks 14 and 15 change the code and the tests they produced. Tasks run in this order: 12, 14, 15, then 16 and 17 (the section "Amendment (2026-04-04)" below), then 13.

### How `/auth/login` issues a session today, and the design chosen

**What was read.** `POST /auth/login` is not a plain endpoint. `OpenIddictSetup` registers the OpenIddict **token endpoint** at `auth/login` and `auth/refresh` in **pass-through** mode (`SetTokenEndpointUris`, `EnableTokenEndpointPassthrough`). A request goes through OpenIddict's pipeline first: `JsonLoginRequestHandler` takes the slot of the removed form extraction and turns the JSON body into a password-grant request; OpenIddict validates it; then the request is passed on to the routed minimal API `LoginEndpoint.HandleAsync`, which counts the attempt, checks the password, reads the membership and builds the principal (`sub`, `session_start`, `session_stamp`, `org_id`, `roles`, `permissions`, the audience, the destinations, the refresh-token lifetime) and returns an `AtomicSignInResult`. That result opens a transaction, runs `Results.SignIn(..., OpenIddict scheme)`, and OpenIddict's sign-in handlers do the rest: `RefreshTokenIssuanceHandler` asks for a refresh token (and so for an authorization, the token family), `AccessTokenClaimFilter` keeps the authorization id out of the access token and the tenancy claims out of the refresh token, `LoginResponseShaper` cuts the body to `{status, access_token}`, `SessionResponseHandler` moves the refresh token into the `auth_rt` cookie (its lifetime comes from `HttpContext.Items`). The result then stages `login.succeeded`, saves, commits, and only then lets the held body out.

**What was run (a probe, in a scratch copy of `src` and `tests` outside the repository; none of its code is in this plan).**

- A plain endpoint cannot sign in through OpenIddict: with the completion path left out of the token endpoint URIs, the same `Results.SignIn` fails with `InvalidOperationException: A sign-in response cannot be returned from this endpoint.` OpenIddict's server handler only signs in inside a request it recognised as one of its endpoints.
- With `auth/signup/complete` added as a **third token endpoint URI** (pass-through) and a small extraction handler that claims the path, the endpoint — otherwise the Task 8 endpoint, its transaction handed to `AtomicSignInResult` — answered `200`, a body with exactly `access_token` and `status`, one `Set-Cookie: auth_rt` (`HttpOnly; Secure; SameSite=Strict; Path=/auth`, 14 days), an access token with `org_id` of the new company and `roles ["admin"]`; `POST /auth/refresh` with that cookie answered `200`; the audit log held one `login.succeeded`, one `signup.completed` and one `org.created`.
- With a trigger that refuses every insert into `"OpenIddictTokens"`, and (second run) a trigger that refuses only the `login.succeeded` row of `audit_events`: the completion answered `500`, no cookie, and **no user, no company, no membership, no token, no authorization and no audit row stayed**; the pending sign-up was intact.
- `GET /auth/signup/complete` still answered `405`; a weak password still answered its `400` with the rules and no cookie.
- The existing test classes `Signup*`, `Login*`, `Refresh*`, `OpenApi*`, `Audit*`, `Rate*`, `JsonCharset*`, `Logout*`, `UnhandledTokenRequestGuard*`, `SessionResponseHandler*` and `UnverifiedLogin*` were run against it. The only failures that were not Npgsql connection time-outs (the machine was busy; those classes passed when run again alone, 104 tests) were the tests that expect `204` from the completion: `SignupCompleteTests`, `SignupCompleteRaceTests`, `SignupFirstAdminTests` and the `POST /auth/signup/complete` row of `OpenApiTests.Each_endpoint_describes_the_statuses_of_the_contract`. Step 3 of Task 14 updates exactly those. The plan's own code and tests below were written on paper afterwards and have **not** been run: the implementer's tests decide.

**The design: the completion is an additional OpenIddict pass-through request.**

1. `auth/signup/complete` becomes the **third token endpoint URI** (`OpenIddictSetup`). A new extraction handler, `SignupCompleteRequestHandler`, sits between `RefreshRequestHandler` and `UnhandledTokenRequestGuard`, claims the path and hands OpenIddict a **placeholder** password-grant request (`username` and `password` are fixed words: OpenIddict's own validation wants both non-empty). It **never reads the body**. The endpoint reads its JSON itself, exactly as Task 8 wrote it, so everything Task 8 pinned stays as it was: `403 signup_disabled` before the body is read, `415` for another charset, `400 invalid_request` for a body that is not what it takes, `405` for another method. Nothing a client sends reaches the OpenIddict request, so the path is no second way to log in (a test pins it).
2. One shared builder, **`SessionPrincipal.Create`**, makes the principal for both `LoginEndpoint` and the completion: the subject, the session start, the security stamp, the company with the role and its permissions, the audience, the destinations, the refresh-token lifetime and the lifetime the cookie lives for. They cannot differ.
3. `LoginResponseShaper` shapes the body for the completion path as for the login path; `SessionResponseHandler` already treats every path that is not the refresh as a sign-in. Cookie, authorization id (the family), security stamp and claim filter are therefore the same handlers' work, not copies.
4. **Atomic with the completion's transaction.** `SignupService.CompleteAsync` no longer commits on success: it saves the account, the company with its roles, the membership and the two audit rows (`signup.completed`, `org.created`) inside its transaction and **hands the open transaction back** in a `SignupCompletion`. The endpoint reads the membership (inside that transaction: the same `AuthDbContext`), builds the principal and returns `AtomicSignInResult` **given that transaction**: it issues the session in it, stages `login.succeeded`, saves and commits once. A refusal by OpenIddict, an exception of the token store or of the audit table, or a request that goes away leaves the transaction uncommitted: everything is rolled back, the link is still usable, and the person gets the one `500 internal_error` of every failure.

**Rejected.** *Sign in from a plain minimal API endpoint* — impossible (the probe above). *Two requests* (the completion answers `204` and the screen calls `/auth/login` with the password it still holds) — not atomic, the password sits in the page for a second request, and it goes through the lockout counter and the login limits. *Mint the tokens by hand* with the token and authorization managers — copies the claim handling, the reference-token format, the signing and the response shaping, and would drift from the login. *A "signup" mode inside `/auth/login`* — mixes two contracts and two sets of errors in the login pipeline. *An OpenIddict extension grant* — needs a custom grant type and its validation for no gain.

**Resource rules for the implementers of Tasks 14 and 15** (this machine is shared; they apply to every step):

- Build with `-maxcpucount:2`: `"C:/Program Files/dotnet/dotnet.exe" build tests/Auth.IntegrationTests -maxcpucount:2 -warnaserror`.
- Run only the classes the task names, with `-maxThreads 2`, through the test executable, one `-class` per class: `tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -maxThreads 2 -class Auth.IntegrationTests.SignupCompleteSignInTests -class Auth.IntegrationTests.SignupCompleteTests` (the form was run for the probe; unit tests of the sample run with `npx ng test --watch=false --include "<spec file>"`, one `--include` per file).
- **No full suite** (neither `dotnet test tests/Auth.IntegrationTests` without a filter nor `npx ng test` without `--include`), and **no live runs**: no `scripts/e2e-*.sh`, no Playwright, no `docker compose`. The verifiers run those at the end. The scripts and the Playwright test are checked by reading and by `bash -n` and `npx tsc -p e2e/tsconfig.json --noEmit`.
- A test that fails with an Npgsql "operation has timed out" while connecting is the machine being busy, not the code: run that class again, alone, before reading it as a failure.
- The implementers leave their changes uncommitted (see Global Constraints); the code below is the plan's, written on paper, and the implementer's tests decide.

**Review Focus of the amendment** (inputs the spec implies and a person is likely to meet; each has its test in the task named):

1. **A sign-in that cannot be written halfway** (the token store refuses, the audit log refuses the `login.succeeded` row): nothing of the completion stays, no cookie is set, and the same link still works afterwards (`SignupCompleteSignInTests.A_token_store_that_refuses_...`, `...An_audit_log_that_refuses_the_sign_in_...`, `...A_sign_in_that_OpenIddict_refuses_...`; Task 14).
2. **The link used twice, or the button pressed twice:** the second post is `invalid_token` with no cookie and no second session; the screen sends one request (`SignupCompleteSignInTests.The_same_body_posted_again_is_invalid_token_with_no_cookie_and_no_second_session`, `...Every_refusal_is_what_it_was_and_issues_no_cookie_and_no_token`; `complete-signup.spec.ts` "sends one request for a double submit"; Tasks 14 and 15).
3. **Someone is already signed in, in that browser, as another person, and opens the link:** the old session is dropped before the new one (`auth.service.signup.spec.ts`; Task 15).
4. **The Back button and a reload after completing:** Back must not land on a "link used up" page of the screen that was left (`replaceUrl`), and a reload keeps the person signed in through the cookie (`complete-signup.spec.ts` "200: ..."; `09-signup.spec.ts` reload step; Task 15).
5. **A client that posts a login-shaped or form-encoded body at the completion path:** `400 invalid_request`, no token, no cookie (`SignupCompleteSignInTests.A_form_or_a_credentials_body_is_refused_with_400_and_issues_nothing`, `SignupCompleteSignInTests.What_a_client_adds_to_a_valid_completion_never_reaches_the_OpenIddict_request`; Task 14).

---

### Task 14: The completion signs in: the server

**Files:**
- Create: `src/Auth.Server/Sessions/SessionPrincipal.cs`, `src/Auth.Server/Signup/SignupCompletion.cs`, `src/Auth.Server/Signup/SignupCompleteRequestHandler.cs`
- Modify: `src/Auth.Server/Login/LoginEndpoint.cs` (the principal comes from `SessionPrincipal`), `src/Auth.Server/Login/AtomicSignInResult.cs` (replaced as a whole), `src/Auth.Server/Login/LoginResponseShaper.cs`, `src/Auth.Server/Tokens/OpenIddictSetup.cs`, `src/Auth.Server/Signup/SignupService.cs` (`CompleteAsync`), `src/Auth.Server/Signup/SignupCompleteEndpoint.cs` (replaced as a whole), `src/Auth.Server/Api/SignupRoutes.cs` (the completion route), `src/Auth.Server/Api/AccountEndpoints.cs` (one word)
- Create (tests): `tests/Auth.IntegrationTests/SignupCompleteSignInTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/SessionApi.cs` (one word), `SignupCompleteTests.cs`, `SignupCompleteRaceTests.cs`, `SignupFirstAdminTests.cs`, `OpenApiTests.cs`, `LoginScopeTests.cs`

**Interfaces:**
- Consumes: Task 8's `SignupService`, `SignupCompleteEndpoint`, `PendingSignups`; `LoginEndpoint`, `AtomicSignInResult`, `LoginResponseShaper`, `OpenIddictSetup`, `RefreshCookie`, `SessionPolicy`, `TenantClaims`, `MembershipReader`; the test bases and helpers (`SignupTestBase`, `SessionApi`, `AccountApi`, `SignupApi`, `AccessTokens`, `PendingSignupSeed`, `RateLimitApi`).
- Produces: `SessionPrincipal.Create(HttpContext http, ApplicationUser user, TenantContext tenant, string audience, TimeProvider clock) : ClaimsPrincipal` (also records the refresh-token lifetime for the cookie); `AtomicSignInResult(ClaimsPrincipal principal, AuditEntry entry, IDbContextTransaction? opened = null)`; `SignupService.CompleteAsync(string token, string password, string companyName, CancellationToken cancellationToken) : Task<SignupCompletion>`; `SignupCompletion(string? Error, IReadOnlyList<string>? BrokenRules, ApplicationUser? Account, Guid CompanyId, IDbContextTransaction? Transaction)` with `Succeeded`; `SignupCompleteRequestHandler.Descriptor` and `IsCompletePath(PathString)`; `SessionApi.ReadSessionAsync` public; the completion answer `200 {"status":"authenticated","access_token"}` plus the cookie (Task 15 and the documents rely on it).

- [ ] **Step 1: The test helper.** In `tests/Auth.IntegrationTests/Infrastructure/SessionApi.cs` change `private static async Task<Session> ReadSessionAsync(HttpResponseMessage response)` to `public static async Task<Session> ReadSessionAsync(HttpResponseMessage response)` (it asserts the `200` and reads the access token and the cookie; the completion tests use it).

- [ ] **Step 2: Write the new tests.** Create `tests/Auth.IntegrationTests/SignupCompleteSignInTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0009, Amendment 2: completing a sign-up signs the person in, and answers as a successful <c>POST /auth/login</c> does. The sign-in is part of the
/// completion: if it cannot be written, nothing of the completion stays.
/// </summary>
public sealed class SignupCompleteSignInTests : SignupTestBase
{
    // While true, a handler of the test refuses every sign-in that OpenIddict is asked for: the completion must then fail as a whole.
    private bool refusing;

    private const string Mixed = "Anna@Firma.test";
    private const string WeakRules = """{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}""";

    // Triggers that make the database refuse one write, to fail the sign-in after the completion has done its own work.
    private const string RefuseEverything =
        "CREATE FUNCTION refuse_row() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'refused'; END $$ LANGUAGE plpgsql";
    private const string RefuseLoginSucceeded =
        "CREATE FUNCTION refuse_row() RETURNS trigger AS $$ BEGIN IF NEW.kind = 'login.succeeded' THEN RAISE EXCEPTION 'refused'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql";
    private const string RefuseTokens =
        """CREATE TRIGGER refuse_row BEFORE INSERT ON "OpenIddictTokens" FOR EACH ROW EXECUTE FUNCTION refuse_row()""";
    private const string RefuseAudit =
        "CREATE TRIGGER refuse_row BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION refuse_row()";
    private const string AllowTokens = """DROP TRIGGER refuse_row ON "OpenIddictTokens" """;
    private const string AllowAudit = "DROP TRIGGER refuse_row ON audit_events";

    // The claims of an access token as LoginTests pins them: the contract, OpenIddict's own metadata, and the three of spec 0005.
    private static readonly string[] AccessTokenClaims = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "permissions", "roles", "sub"];

    public SignupCompleteSignInTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.Configure<OpenIddictServerOptions>(options => options.Handlers.Add(
            OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
                .UseInlineHandler(context =>
                {
                    if (refusing)
                    {
                        context.Reject(OpenIddictConstants.Errors.ServerError, "Refused by the test.");
                    }

                    return ValueTask.CompletedTask;
                })
                .SetOrder(OpenIddictServerHandlers.EvaluateGeneratedTokens.Descriptor.Order + 600)   // after the handlers that decide the tokens, before any is made
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build())));
    }

    private Task<long> TokenRowsAsync() =>
        InDbAsync(db => db.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM "OpenIddictTokens" """).SingleAsync(TestContext.Current.CancellationToken));

    private Task<long> AuthorizationRowsAsync() =>
        InDbAsync(db => db.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM "OpenIddictAuthorizations" """).SingleAsync(TestContext.Current.CancellationToken));

    private Task<Guid> CompanyIdAsync() =>
        InDbAsync(db => db.Companies.Where(c => c.Name == CompanyName).Select(c => c.Id).SingleAsync(TestContext.Current.CancellationToken));

    /// <summary>The completion failed halfway: <c>500</c>, no cookie, nothing of it stays, and the link is as it was.</summary>
    private async Task AssertNothingStaysAsync(HttpResponseMessage failed, string token)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.False(failed.Headers.Contains("Set-Cookie"), "A completion that failed must not write the cookie.");
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Address, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));   // only the development company
        Assert.Equal(0L, await TokenRowsAsync());
        Assert.Equal(0L, await AuthorizationRowsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.SignupCompleted));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
        Assert.DoesNotContain(await AuditAsync(AuditKinds.OrgCreated), row => row.OrgName == CompanyName);
        var pending = Assert.Single(await PendingAsync());
        Assert.Equal(EmailTokens.HashOf(token), pending.TokenHash);   // the use of the link was rolled back with the rest
    }

    [Fact]
    public async Task The_completion_answers_as_a_login_does_with_the_status_and_the_access_token_only()   // criterion 9
    {
        var token = await MailedAsync(Mixed);

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);

        var raw = await done.Content.ReadAsStringAsync();
        Assert.True(done.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)done.StatusCode}: {raw}");
        Assert.Equal("application/json", done.Content.Headers.ContentType?.MediaType);
        Assert.True(done.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected");
        Assert.Contains(done.Headers.Pragma, p => p.Name == "no-cache");
        using var body = JsonDocument.Parse(raw);
        Assert.Equal(["access_token", "status"], body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("authenticated", body.RootElement.GetProperty("status").GetString());
        var cookie = SessionApi.RefreshCookieOf(done);
        Assert.DoesNotContain(cookie.Value.Value!, raw, StringComparison.Ordinal);   // the refresh token never reaches a body
    }

    [Fact]
    public async Task The_cookie_and_the_headers_are_those_of_a_login()   // criterion 9
    {
        var token = await MailedAsync(Mixed);

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        using var login = await LoginApi.Login(Client, Mixed, Password);

        Assert.Single(done.Headers.GetValues("Set-Cookie"));
        var cookie = SessionApi.RefreshCookieOf(done);
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.Equal("/auth", cookie.Path.Value);
        Assert.Equal(TimeSpan.FromDays(14), cookie.MaxAge);
        Assert.False(cookie.Domain.HasValue);
        Assert.Null(cookie.Expires);

        var reference = SessionApi.RefreshCookieOf(login);
        Assert.Equal(reference.HttpOnly, cookie.HttpOnly);
        Assert.Equal(reference.Secure, cookie.Secure);
        Assert.Equal(reference.SameSite, cookie.SameSite);
        Assert.Equal(reference.Path, cookie.Path);
        Assert.Equal(reference.MaxAge, cookie.MaxAge);
        Assert.NotEqual(reference.Value.Value, cookie.Value.Value);   // a session of its own
        Assert.Equal(LoginApi.HeaderNames(login), LoginApi.HeaderNames(done));
    }

    [Fact]
    public async Task The_token_carries_the_new_company_and_the_first_admins_role_and_the_claims_of_a_login()   // criterion 9
    {
        var token = await MailedAsync(Mixed);

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(done);

        var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken));
        var company = await CompanyIdAsync();
        var jwt = new JsonWebToken(session.AccessToken);
        Assert.Equal(AccessTokenClaims, jwt.Claims.Select(c => c.Type).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal("at+jwt", jwt.Typ);
        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(
            ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            AccessTokens.Array(session.AccessToken, "permissions"));

        // A login a moment later says the same.
        var later = await SessionApi.LoginAsync(Client, Mixed, Password);
        var laterJwt = new JsonWebToken(later.AccessToken);
        Assert.Equal(jwt.Subject, laterJwt.Subject);
        Assert.Equal(jwt.Audiences, laterJwt.Audiences);
        Assert.Equal(jwt.Issuer, laterJwt.Issuer);
        Assert.Equal(AccessTokens.Text(session.AccessToken, "org_id"), AccessTokens.Text(later.AccessToken, "org_id"));
        Assert.Equal(AccessTokens.Array(session.AccessToken, "roles"), AccessTokens.Array(later.AccessToken, "roles"));
        Assert.Equal(AccessTokens.Array(session.AccessToken, "permissions"), AccessTokens.Array(later.AccessToken, "permissions"));
    }

    [Fact]
    public async Task The_cookie_refreshes_like_a_logins_and_belongs_to_a_family()   // criterion 9
    {
        var token = await MailedAsync();

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(done);

        using (var scope = Factory.Services.CreateScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
            var entry = await tokens.FindByReferenceIdAsync(session.RefreshToken);
            Assert.NotNull(entry);
            Assert.Equal(OpenIddictConstants.TokenTypeIdentifiers.RefreshToken, await tokens.GetTypeAsync(entry));
            Assert.False(string.IsNullOrEmpty(await tokens.GetAuthorizationIdAsync(entry)), "The refresh token has no authorization (family).");
        }

        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);
        Assert.Equal(AccessTokens.Text(session.AccessToken, "org_id"), AccessTokens.Text(refreshed.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(refreshed.AccessToken, "roles"));
        Assert.NotEqual(session.RefreshToken, refreshed.RefreshToken);   // rotated

        using var loggedOut = await SessionApi.Logout(Client, refreshed.RefreshToken);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        using var after = await SessionApi.Refresh(Client, refreshed.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(after);   // the family ended with the logout
    }

    [Fact]
    public async Task The_session_starts_at_the_completion_and_ends_at_the_cap_of_a_login()   // spec 0002: the absolute cap counts from the start of the session
    {
        var token = await MailedAsync();
        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(done);

        Clock.Advance(TimeSpan.FromDays(13));
        var day13 = await SessionApi.RefreshOk(Client, session.RefreshToken);
        Clock.Advance(TimeSpan.FromDays(13));
        var day26 = await SessionApi.RefreshOk(Client, day13.RefreshToken);
        Assert.InRange(day26.Cookie.MaxAge!.Value, TimeSpan.FromDays(4) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(4));   // 4 days to the cap

        Clock.Advance(TimeSpan.FromDays(4) + TimeSpan.FromMinutes(1));
        using var response = await SessionApi.Refresh(Client, day26.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(response);
    }

    [Fact]
    public async Task The_session_carries_the_stamp_of_the_account_so_a_password_change_ends_it()   // spec 0004 -> Effects of a reset
    {
        var token = await MailedAsync();
        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(done);

        using (var scope = Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var account = await users.FindByEmailAsync(Address);
            Assert.NotNull(account);
            Assert.True((await users.UpdateSecurityStampAsync(account)).Succeeded);
        }

        using var refreshed = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refreshed);
    }

    [Fact]
    public async Task The_completion_writes_login_succeeded_beside_signup_completed_and_org_created()   // criterion 19, Amendment 2
    {
        var token = await MailedAsync(Mixed);

        using var done = await RateLimitApi.SendAsync(
            Client, HttpMethod.Post, SignupApi.CompletePath, CallerAddress, null,
            $$"""{"token":"{{token}}","password":"{{Password}}","company_name":"{{CompanyName}}"}""");
        var session = await SessionApi.ReadSessionAsync(done);

        var user = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken));
        var company = await CompanyIdAsync();
        var signedIn = await SingleAsync(AuditKinds.LoginSucceeded);
        Assert.Equal(user.Id, signedIn.ActorUserId);
        Assert.Equal(user.Id, signedIn.SubjectUserId);
        Assert.Equal(Mixed, signedIn.SubjectEmail);
        Assert.Equal(company, signedIn.OrgId);
        Assert.Equal(CompanyName, signedIn.OrgName);
        Assert.Equal(CallerAddress, signedIn.ClientIp);
        Assert.Null(signedIn.TargetId);

        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        var created = Assert.Single(await AuditAsync(AuditKinds.OrgCreated), row => row.OrgId == company);
        Assert.Equal("signup", AuditApi.Text(created, "via"));
        Assert.Equal(completed.OrgId, signedIn.OrgId);
        Assert.Empty(await AuditAsync(AuditKinds.LoginFailed));
        await AssertNoSecretsAsync(Password, token, session.AccessToken, session.RefreshToken);
    }

    [Fact]
    public async Task A_token_store_that_refuses_leaves_nothing_of_the_completion_and_the_link_still_works()   // Amendment 2: the sign-in is part of the completion
    {
        var token = await MailedAsync();
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseEverything, TestContext.Current.CancellationToken));
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseTokens, TestContext.Current.CancellationToken));

        using (var failed = await SignupApi.Complete(Client, token, Password, CompanyName))
        {
            await AssertNothingStaysAsync(failed, token);
        }

        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(AllowTokens, TestContext.Current.CancellationToken));
        using var again = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(again);
        Assert.NotEmpty(session.AccessToken);
    }

    [Fact]
    public async Task An_audit_log_that_refuses_the_sign_in_leaves_nothing_of_the_completion_and_the_link_still_works()   // Amendment 2: the sign-in is part of the completion
    {
        var token = await MailedAsync();
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseLoginSucceeded, TestContext.Current.CancellationToken));
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseAudit, TestContext.Current.CancellationToken));

        using (var failed = await SignupApi.Complete(Client, token, Password, CompanyName))
        {
            await AssertNothingStaysAsync(failed, token);   // the tokens were issued first and went with the rollback
        }

        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(AllowAudit, TestContext.Current.CancellationToken));
        using var again = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(again);
        Assert.NotEmpty(session.AccessToken);
        Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task A_sign_in_that_OpenIddict_refuses_leaves_nothing_of_the_completion_and_the_link_still_works()   // Amendment 2: an error of OpenIddict is not an answer that may leave the account behind
    {
        var token = await MailedAsync();
        refusing = true;

        using (var failed = await SignupApi.Complete(Client, token, Password, CompanyName))
        {
            await AssertNothingStaysAsync(failed, token);   // 500, no cookie, and nothing of the completion
        }

        refusing = false;
        using var again = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(again);
        Assert.NotEmpty(session.AccessToken);
    }

    [Fact]
    public async Task The_same_body_posted_again_is_invalid_token_with_no_cookie_and_no_second_session()   // criterion 12
    {
        var token = await MailedAsync();
        using (var first = await SignupApi.Complete(Client, token, Password, CompanyName))
        {
            _ = await SessionApi.ReadSessionAsync(first);
        }

        var tokens = await TokenRowsAsync();
        var authorizations = await AuthorizationRowsAsync();
        Assert.Equal(1L, authorizations);   // the one session of the first post

        using (var second = await SignupApi.Complete(Client, token, Password, CompanyName))
        {
            await AccountApi.AssertErrorAsync(second, AccountApi.InvalidToken);   // never stored, and no cookie
        }

        Assert.Equal(tokens, await TokenRowsAsync());
        Assert.Equal(authorizations, await AuthorizationRowsAsync());
        Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task Every_refusal_is_what_it_was_and_issues_no_cookie_and_no_token()   // criteria 11, 12, 14
    {
        const string Taken = "taken@firma.test";
        var token = await MailedAsync();
        await CreateUserAsync(Taken, confirmed: true, member: false);
        var takenToken = await PendingSignupSeed.IssueAsync(Factory.Services, Taken, CompanyName);

        using (var weak = await SignupApi.Complete(Client, token, "short", CompanyName))
        {
            await AccountApi.AssertErrorAsync(weak, WeakRules);   // 400 with the rules, never stored, no cookie
        }

        using (var unknown = await SignupApi.Complete(Client, EmailTokens.NewToken(), Password, CompanyName))
        {
            await AccountApi.AssertErrorAsync(unknown, AccountApi.InvalidToken);
        }

        using (var malformed = await AccountApi.PostRaw(Client, SignupApi.CompletePath, """{"token":"x"}"""))
        {
            await AccountApi.AssertErrorAsync(malformed, AccountApi.InvalidRequest);
        }

        using (var registered = await SignupApi.Complete(Client, takenToken, Password, CompanyName))
        {
            await SignupApi.AssertAlreadyRegisteredAsync(registered);
        }

        Assert.Equal(0L, await TokenRowsAsync());
        Assert.Equal(0L, await AuthorizationRowsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task The_path_is_no_second_way_to_log_in()   // the pass-through request is a placeholder: what the client sends is read by the endpoint alone
    {
        using var form = await Client.PostAsync(
            SignupApi.CompletePath,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = Factory.SeedEmail,
                ["password"] = Factory.SeedPassword,
            }));
        await AccountApi.AssertErrorAsync(form, AccountApi.InvalidRequest);

        using var credentials = await AccountApi.PostRaw(
            Client, SignupApi.CompletePath, JsonSerializer.Serialize(new { email = Factory.SeedEmail, password = Factory.SeedPassword }));
        await AccountApi.AssertErrorAsync(credentials, AccountApi.InvalidRequest);

        Assert.Equal(0L, await TokenRowsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Other_methods_are_still_405(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), SignupApi.CompletePath);

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
```

- [ ] **Step 3: Update the tests that expect `204`.** The completion answers `200` now; the invitation acceptance stays `204`.

(a) `tests/Auth.IntegrationTests/SignupCompleteTests.cs`. Replace the whole first test (`A_visitor_signs_up_finishes_and_logs_in_as_the_admin_of_a_company_of_their_own`) by

```csharp
    [Fact]
    public async Task A_visitor_signs_up_finishes_and_is_signed_in_as_the_admin_of_a_company_of_their_own()   // criteria 1, 3, 8, 9
    {
        using (var options = await SignupApi.Options(Client))
        {
            Assert.Equal("""{"signup":"self-service"}""", await options.Content.ReadAsStringAsync());
        }

        var token = await MailedAsync("Anna@Firma.test");
        using (var preview = await SignupApi.Preview(Client, token))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        }

        using var done = await SignupApi.Complete(Client, token, Password, CompanyName);
        var session = await SessionApi.ReadSessionAsync(done);   // 200, the access token and the refresh cookie, as a login answers

        var company = await InDbAsync(db => db.Companies.Where(c => c.Name == CompanyName).Select(c => c.Id).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(
            ["members:manage", "org:delete", "org:manage", "reports:approve", "reports:read", "roles:manage", "templates:manage"],
            AccessTokens.Array(session.AccessToken, "permissions"));

        // A login with the chosen password says the same.
        var login = await SessionApi.LoginAsync(Client, "Anna@Firma.test", Password);
        Assert.Equal(company.ToString(), AccessTokens.Text(login.AccessToken, "org_id"));
        Assert.Equal(AccessTokens.Array(session.AccessToken, "permissions"), AccessTokens.Array(login.AccessToken, "permissions"));
    }
```

In the same file every other `HttpStatusCode.NoContent` about the completion becomes `HttpStatusCode.OK`: in `The_account_company_roles_and_membership_are_what_the_contract_says` (`done`), `The_company_name_of_the_request_is_only_a_suggestion` (`done`), `A_weak_password_names_the_rules_creates_nothing_and_leaves_the_token_usable` (`good`), `Malformed_bodies_are_400_invalid_request_and_leave_the_token_usable` (`good`), `A_used_unknown_expired_or_replaced_token_is_400_invalid_token` (`first` and `current`), `Failed_logins_before_the_sign_up_do_not_lock_the_new_account` (`done`). **Not** the one in `An_account_made_in_the_meantime_is_409_already_registered_and_the_pending_sign_up_is_gone`: `accepted` is the invitation (`TenancyApi.Accept`) and stays `NoContent`.

In `A_completion_is_recorded_with_the_account_the_company_and_the_address_and_nothing_secret` replace the line `await AccountApi.AssertEmptyAsync(done, HttpStatusCode.NoContent);` by `var session = await SessionApi.ReadSessionAsync(done);` and the last line `await AssertNoSecretsAsync(Password, token);` by `await AssertNoSecretsAsync(Password, token, session.AccessToken, session.RefreshToken);`.

In `No_token_password_or_address_reaches_the_log` replace the block

```csharp
        using (await SignupApi.Complete(Client, token, Password, CompanyName))   // success
        {
        }
```

by

```csharp
        SessionApi.Session session;
        using (var signedIn = await SignupApi.Complete(Client, token, Password, CompanyName))   // success: it signs in
        {
            session = await SessionApi.ReadSessionAsync(signedIn);
        }
```

and the list `new[] { token, boomToken, takenToken, Password, Address, Taken, Boom }` by `new[] { token, boomToken, takenToken, Password, Address, Taken, Boom, session.AccessToken, session.RefreshToken }`. (If the log of a library does hold one of the two tokens, that is a finding for the owner, not a weaker assertion.)

(b) `SignupCompleteRaceTests.cs`: rename `Of_parallel_completions_with_one_token_exactly_one_is_204` to `Of_parallel_completions_with_one_token_exactly_one_is_200`; in it and in the other tests change `HttpStatusCode.NoContent` to `HttpStatusCode.OK` for `r.StatusCode` (both lines of the count and the `refused` list), `completed` (in `A_completion_and_an_invitation_acceptance_for_one_address_never_both_create_an_account`, the `completionFirst` branch), `second` (in `A_mail_that_replaces_the_link_while_a_completion_waits_for_it_makes_the_completion_invalid_token`) and `done` (in `A_mail_that_waits_for_a_completion_never_leaves_a_working_link_to_a_used_sign_up`). **Not** `accepted` (the invitation: `NoContent`). In `Of_parallel_completions_with_one_token_exactly_one_is_200` add, after its last assertion (`Assert.Empty(await PendingAsync());`), that the one winner made the one session:

```csharp
        Assert.Equal(1L, await InDbAsync(db => db.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM "OpenIddictAuthorizations" """).SingleAsync(TestContext.Current.CancellationToken)));
```

(c) `SignupFirstAdminTests.cs`: `Assert.Equal(HttpStatusCode.NoContent, done.StatusCode);` becomes `Assert.Equal(HttpStatusCode.OK, done.StatusCode);`.

(d) `OpenApiTests.cs`: in `Each_endpoint_describes_the_statuses_of_the_contract` change the line `[InlineData("POST /auth/signup/complete", "204,400,403,409,415,429")]` to `[InlineData("POST /auth/signup/complete", "200,400,403,409,415,429")]`; in `The_answers_that_set_or_clear_the_refresh_cookie_say_so` add `[InlineData("POST /auth/signup/complete", "200")]` after the login line; in `No_other_answer_says_it_sets_a_cookie` change the array to `string[] setting = ["POST /auth/login 200", "POST /auth/signup/complete 200", "POST /auth/refresh 200", "POST /auth/logout 204"];`; and add this test after `The_answers_that_set_or_clear_the_refresh_cookie_say_so`:

```csharp
    [Fact]
    public async Task The_completion_answers_with_the_body_of_the_login()   // criterion 20, Amendment 2
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var operations = Operations(await DescriptionAsync(factory));

        string Schema(string endpoint) =>
            operations[endpoint]["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>();

        Assert.Equal(Schema("POST /auth/login"), Schema("POST /auth/signup/complete"));
    }
```

(e) `LoginScopeTests.cs` (the rows that need nothing new): in `ShouldShape_requires_a_password_grant_on_the_login_path` add

```csharp
    [InlineData("password", "/auth/signup/complete", true)]
    [InlineData("password", "/auth/SIGNUP/complete/", true)]
    [InlineData("password", "/auth/signup/complete//", false)]
    [InlineData("refresh_token", "/auth/signup/complete", false)]
```

and in `IsLoginRequest_matches_the_login_path_only` add `[InlineData("/auth/signup/complete", false)]` (the login handler must not claim the completion's path). The theory for `SignupCompleteRequestHandler.IsCompletePath` itself is written in Step 9, because it names a type that does not exist yet.

- [ ] **Step 4: Build, and see the new tests fail.** `"C:/Program Files/dotnet/dotnet.exe" build tests/Auth.IntegrationTests -maxcpucount:2 -warnaserror` builds (the tests only use the helper of Step 1). Then run the classes of this step (the resource rules above): `-class Auth.IntegrationTests.SignupCompleteSignInTests -class Auth.IntegrationTests.SignupCompleteTests -class Auth.IntegrationTests.SignupCompleteRaceTests -class Auth.IntegrationTests.SignupFirstAdminTests -class Auth.IntegrationTests.LoginScopeTests`. Expected: the tests of the completion fail with `Expected: OK, Actual: NoContent` (the sign-in refusals, `The_same_body_posted_again_...` and the `ShouldShape` rows of `LoginScopeTests` fail as well; the `OpenApiTests` rows are not run yet). Three of the new tests **pass already**, because they pin behaviour that holds before the change and must go on holding: `Other_methods_are_still_405`, `The_path_is_no_second_way_to_log_in` and `Every_refusal_is_what_it_was_and_issues_no_cookie_and_no_token` (the second is the one that would turn red if the pass-through ever let a client's body reach OpenIddict). That is intended; do not make them fail to "see them red".

- [ ] **Step 5: `SessionPrincipal`, and the login uses it.** Create `src/Auth.Server/Sessions/SessionPrincipal.cs`:

```csharp
using System.Globalization;
using System.Security.Claims;
using Auth.Infrastructure.Identity;
using Auth.Server.Tenancy;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Sessions;

/// <summary>
/// The principal of a new session, which <c>POST /auth/login</c> and <c>POST /auth/signup/complete</c> hand to OpenIddict to sign in. One place
/// builds it, so that the two sign-ins cannot differ: the subject, the start of the session, the security stamp, the company with the role and
/// what it grants, the audience, the destinations, and the lifetime of the refresh token (which the cookie lives for as well).
/// </summary>
public static class SessionPrincipal
{
    public static ClaimsPrincipal Create(HttpContext http, ApplicationUser user, TenantContext tenant, string audience, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(audience);
        ArgumentNullException.ThrowIfNull(clock);

        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: Claims.Name,
            roleType: Claims.Role);

        // The access token carries `sub` and the tenancy claims below (`org_id`, `roles`, `permissions`), and nothing else:
        // no email, name or scope.
        identity.SetClaim(Claims.Subject, user.Id.ToString());
        identity.SetClaim(SessionPolicy.StartClaim, clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        // The stamp of the account as this sign-in read it. A session that began before a password change, even one
        // whose sign-in was still in flight when the change was committed, carries the old stamp and cannot refresh.
        identity.SetClaim(SessionPolicy.StampClaim, user.SecurityStamp);

        // The company, the role and what it grants, read from the database now.
        TenantClaims.Apply(identity, tenant);

        var principal = new ClaimsPrincipal(identity);
        principal.SetResources(audience);
        // `sub` and the tenancy claims make the access token; the session start stays in the refresh token only.
        principal.SetDestinations(TenantClaims.DestinationsOf);
        principal.SetRefreshTokenLifetime(SessionPolicy.SlidingLifetime);
        http.Items[RefreshCookie.LifetimeItemKey] = SessionPolicy.SlidingLifetime;
        return principal;
    }
}
```

In `src/Auth.Server/Login/LoginEndpoint.cs` replace everything from the line `var identity = new ClaimsIdentity(` up to and including the line `http.Items[RefreshCookie.LifetimeItemKey] = SessionPolicy.SlidingLifetime;` (the comments between them go with it) by

```csharp
        var principal = SessionPrincipal.Create(http, user, tenant, tokens.Value.Audience, clock);
```

and remove from the top of the file the five `using` lines that nothing in it uses any more: `using System.Globalization;`, `using System.Security.Claims;`, `using Microsoft.IdentityModel.Tokens;`, `using OpenIddict.Abstractions;` and `using static OpenIddict.Abstractions.OpenIddictConstants;` (checked: the file builds without them). Build, then run `-class Auth.IntegrationTests.LoginTests -class Auth.IntegrationTests.LoginCookieTests -class Auth.IntegrationTests.RefreshTests -class Auth.IntegrationTests.SessionLifetimeTests`: unchanged, green.

- [ ] **Step 6: `AtomicSignInResult` can take the transaction of a completion.** Replace `src/Auth.Server/Login/AtomicSignInResult.cs` by

```csharp
using System.Security.Claims;
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore.Storage;
using OpenIddict.Server.AspNetCore;

namespace Auth.Server.Login;

/// <summary>
/// Issues the session of a sign-in and writes the <c>login.succeeded</c> row in one transaction (spec 0008: a change and its row are
/// written together, or neither is). OpenIddict makes the authorization and the token entries, in several saves, on the request's own
/// <see cref="AuthDbContext"/>; the transaction holds all of them and the row. The body of the answer is held in memory
/// until the transaction has committed, so that no token leaves the server for a session that was rolled back; on any failure the
/// response is cleared (no cookie either) and the exception goes to the outermost error handler (<c>500 internal_error</c>).
/// An answer that is not a <c>200</c> (OpenIddict refused the sign-in) is passed on as it is and nothing is committed.
/// A login has the result open the transaction. A completed sign-up (spec 0009, Amendment 2) passes the one that still holds the account, the
/// company and the membership it has just made (<paramref name="opened"/>): the sign-in is then part of the completion, a sign-in that
/// cannot be written rolls the whole completion back, and so does an answer of OpenIddict that is not a <c>200</c> (it is a <c>500</c> then, not
/// an error that leaves the account behind). The result owns that transaction from the first line of <see cref="ExecuteAsync"/> and disposes it.
/// </summary>
public sealed class AtomicSignInResult(ClaimsPrincipal principal, AuditEntry entry, IDbContextTransaction? opened = null) : IResult
{
    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        // Ownership first: from this line on, whatever happens below, the transaction that was handed over is disposed (rolled back unless committed).
        var transaction = opened;
        try
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var db = httpContext.RequestServices.GetRequiredService<AuthDbContext>();
            var audit = httpContext.RequestServices.GetRequiredService<AuditLog>();
            var realBody = httpContext.Response.Body;
            await using var held = new MemoryStream();
            httpContext.Response.Body = held;
            try
            {
                transaction ??= await db.Database.BeginTransactionAsync(httpContext.RequestAborted);
                await Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)
                    .ExecuteAsync(httpContext);
                if (httpContext.Response.StatusCode == StatusCodes.Status200OK)
                {
                    // Staged only now that OpenIddict has not refused: a refused sign-in leaves no row tracked on the request's context.
                    audit.Stage(entry);
                    await db.SaveChangesAsync(httpContext.RequestAborted);
                    await transaction.CommitAsync(httpContext.RequestAborted);
                }
                else if (opened is not null)
                {
                    // An error of OpenIddict must not be the answer to a completion whose account would stay: roll it back, and say 500.
                    throw new InvalidOperationException("OpenIddict refused the sign-in of a completed sign-up; the completion is rolled back.");
                }
            }
            catch
            {
                httpContext.Response.Body = realBody;
                httpContext.Response.Clear();
                throw;
            }

            httpContext.Response.Body = realBody;
            held.Position = 0;
            await held.CopyToAsync(realBody, httpContext.RequestAborted);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }
}
```

- [ ] **Step 7: `SignupService.CompleteAsync` hands the transaction over.** Create `src/Auth.Server/Signup/SignupCompletion.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore.Storage;

namespace Auth.Server.Signup;

/// <summary>
/// How <see cref="SignupService.CompleteAsync"/> ended. A refusal has an <see cref="Error"/> (and, for a weak password, the rules it breaks in
/// <see cref="BrokenRules"/>). Success has the new <see cref="Account"/>, the id of the company it is the first admin of, and the
/// <see cref="Transaction"/> that still holds them, <b>not committed</b>: whoever receives it signs the person in inside it and commits (spec 0009,
/// Amendment 2); letting it go rolls everything back.
/// </summary>
public sealed record SignupCompletion(
    string? Error = null,
    IReadOnlyList<string>? BrokenRules = null,
    ApplicationUser? Account = null,
    Guid CompanyId = default,
    IDbContextTransaction? Transaction = null)
{
    public bool Succeeded => Error is null;
}
```

In `src/Auth.Server/Signup/SignupService.cs` replace the whole method `CompleteAsync` (its `<summary>` included) and the private `Refused` helper by:

```csharp
    /// <summary>
    /// Completes the sign-up as far as the database goes. Refused with <c>invalid_token</c>, with <c>weak_password</c> (the broken rules are
    /// <see cref="SignupCompletion.BrokenRules"/>; the token stays usable) or with <c>already_registered</c> (an account has the address by now:
    /// the pending sign-up is deleted). The checks are made in that order. The caller has checked <paramref name="companyName"/> against the
    /// name rule and <paramref name="password"/> for NUL.
    /// On success nothing is committed: the account, the company with its roles, the membership and the audit rows are saved inside a transaction
    /// that is handed to the caller in <see cref="SignupCompletion.Transaction"/>, because the completion signs the person in (spec 0009,
    /// Amendment 2) and the sign-in is part of it: if it cannot be written, nothing of the completion stays. Every refusal and every exception
    /// leaves the transaction rolled back (except that an account found under the address deletes the pending sign-up).
    /// </summary>
    public async Task<SignupCompletion> CompleteAsync(string token, string password, string companyName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(companyName);

        var hash = EmailTokens.HashOf(token);
        var now = StorableTime.Now(clock);

        // Leaving this method without handing the transaction over rolls everything back, the use of the token included.
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var handedOver = false;
        try
        {
            // Which address is this link for? Read first, because the address is what is locked.
            var address = await db.PendingSignups.AsNoTracking()
                .Where(p => p.TokenHash == hash && p.TokenExpiresAt > now)
                .Select(p => p.NormalizedEmail)
                .FirstOrDefaultAsync(cancellationToken);
            if (address is null)
            {
                return Refused(TenancyErrors.InvalidToken);
            }

            // One address, one account-maker at a time: the lock that invitation acceptance takes, so that whichever of a completion and an
            // acceptance comes second sees the account the first one made and answers `409`, instead of failing on the unique user name.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({address}, 0))", cancellationToken);

            // The token is used up by one statement: of parallel completions with one token one gets the row, and the others, which waited
            // for the lock, find nothing.
            var pending = await PendingSignups.ConsumeAsync(db, hash, now, cancellationToken);
            if (pending is null)
            {
                return Refused(TenancyErrors.InvalidToken);
            }

            var account = new ApplicationUser { UserName = pending.Email, Email = pending.Email, EmailConfirmed = true };
            var broken = await PasswordRules.BrokenAsync(users, account, password);
            if (broken.Count > 0)
            {
                return new SignupCompletion(TenancyErrors.WeakPassword, broken);
            }

            // Any account under the address, whatever its spelling or state: the lookup is by the normalised address (any account whose address normalises to it).
            if (await db.Users.AsNoTracking().AnyAsync(u => u.NormalizedEmail == pending.NormalizedEmail, cancellationToken))
            {
                // Committed, not rolled back: the pending sign-up is gone, as the contract says.
                await transaction.CommitAsync(CancellationToken.None);
                return Refused(TenancyErrors.AlreadyRegistered);
            }

            // The link came through the mailbox of the address: that proves it, and it is why the email is confirmed and a password is set here.
            AccountSessions.Ensure(await users.CreateAsync(account, password), "Could not set up the account");
            // A new account has no sessions or links yet, but its address may have a login streak: streaks do not need an account.
            await AccountSessions.EndAllAsync(db, tokens, authorizations, account, cancellationToken);

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

            // The person who holds the link is anonymous: no actor. Who, where and what company; never the link or the password.
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.SignupCompleted,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = created.Value,
                OrgName = companyName,
            });
            await db.SaveChangesAsync(CancellationToken.None);

            // Not committed: the sign-in is written next, inside this transaction, by whoever receives it, and it commits.
            handedOver = true;
            return new SignupCompletion(Account: account, CompanyId: created.Value, Transaction: transaction);
        }
        finally
        {
            if (!handedOver)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static SignupCompletion Refused(string error) => new(error);
```

In the class `<summary>` of `SignupService` change "makes the account (email confirmed), the company with a copy of the default roles and the membership with the first admin's role, and writes the audit rows" to end with "...and writes the audit rows, and leaves the transaction open for the sign-in (spec 0009, Amendment 2)".

- [ ] **Step 8: The extraction handler, the third token endpoint, the shaper.** Create `src/Auth.Server/Signup/SignupCompleteRequestHandler.cs`:

```csharp
using Auth.Server.Login;
using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Signup;

/// <summary>
/// Makes <c>POST /auth/signup/complete</c> a token-endpoint request of OpenIddict, in pass-through mode like the login, so that its endpoint can sign the
/// person in (spec 0009, Amendment 2): OpenIddict signs in only inside a request it recognised as one of its endpoints. The handler claims the path
/// and gives OpenIddict a <b>placeholder</b> password-grant request (its validation wants a user name and a password that are not empty). It never reads
/// the body: the endpoint does, exactly as it did before, so the order of its answers (<c>403</c> before the body is read, <c>400</c> for a body that is not
/// what it takes) is its own, and nothing a client sends can reach the OpenIddict request. Any method is claimed; the route answers <c>405</c> to all but POST.
/// </summary>
public sealed class SignupCompleteRequestHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ExtractTokenRequestContext>
{
    private const string Placeholder = "signup-complete";

    /// <summary>
    /// <see langword="true"/> for <c>/auth/signup/complete</c> and <c>/auth/signup/complete/</c>, case-insensitive: OpenIddict matches a token endpoint with an
    /// optional trailing slash, so this check must agree with it.
    /// </summary>
    internal static bool IsCompletePath(PathString path) =>
        path.Equals(SignupCompleteEndpoint.Path, StringComparison.OrdinalIgnoreCase)
        || path.Equals(SignupCompleteEndpoint.Path + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// After <see cref="JsonLoginRequestHandler"/> (which holds the slot of OpenIddict's removed form extraction) and before
    /// <see cref="UnhandledTokenRequestGuard"/> (+500), beside the refresh handler (+250): the three claim different paths.
    /// </summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ExtractTokenRequestContext>()
            .AddFilter<OpenIddictServerAspNetCoreHandlerFilters.RequireHttpRequest>()
            .UseSingletonHandler<SignupCompleteRequestHandler>()
            .SetOrder(JsonLoginRequestHandler.Descriptor.Order + 300)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Transaction.GetHttpRequest()
            ?? throw new InvalidOperationException("The token endpoint was reached without an ASP.NET Core request.");

        if (IsCompletePath(request.Path))
        {
            context.Request = new OpenIddictRequest { GrantType = GrantTypes.Password, Username = Placeholder, Password = Placeholder };
        }

        return ValueTask.CompletedTask;
    }
}
```

In `src/Auth.Server/Tokens/OpenIddictSetup.cs`: add `using Auth.Server.Signup;`; in `SetTokenEndpointUris(...)` add a third argument, so that it reads

```csharp
                    .SetTokenEndpointUris(
                        JsonLoginRequestHandler.LoginPath.TrimStart('/'),
                        RefreshRequestHandler.RefreshPath.TrimStart('/'),
                        SignupCompleteEndpoint.Path.TrimStart('/'))
```

add the handler after the refresh one:

```csharp
                options.RemoveEventHandler(OpenIddictServerAspNetCoreHandlers.ExtractPostRequest<OpenIddictServerEvents.ExtractTokenRequestContext>.Descriptor)
                    .AddEventHandler(JsonLoginRequestHandler.Descriptor)
                    .AddEventHandler(RefreshRequestHandler.Descriptor)
                    .AddEventHandler(SignupCompleteRequestHandler.Descriptor)
                    .AddEventHandler(UnhandledTokenRequestGuard.Descriptor);
```

and in the `<summary>` of `AddAuthOpenIddict` write "the token endpoint at `auth/login`, `auth/refresh` and `auth/signup/complete` in pass-through mode".

In `src/Auth.Server/Login/LoginResponseShaper.cs` change `ShouldShape` and the comment above it to

```csharp
    /// <summary>
    /// The response is shaped only for a password grant on the login path or on the sign-up completion, which signs the person in as a login does
    /// (spec 0009, Amendment 2); any other token-endpoint response (the refresh, which <see cref="Auth.Server.Sessions.SessionResponseHandler"/>
    /// shapes itself) passes through untouched.
    /// </summary>
    internal static bool ShouldShape(OpenIddictRequest? request, PathString path) =>
        request is not null
        && request.IsPasswordGrantType()
        && (JsonLoginRequestHandler.IsLoginPath(path) || SignupCompleteRequestHandler.IsCompletePath(path));
```

with `using Auth.Server.Signup;` added; the class summary stays as it is.

- [ ] **Step 9: The scope of the completion's handler.** In `LoginScopeTests.cs` add `using Auth.Server.Signup;` and the test

```csharp
    [Theory]
    [InlineData("/auth/signup/complete", true)]
    [InlineData("/auth/SIGNUP/COMPLETE", true)]
    [InlineData("/auth/signup/complete/", true)]
    [InlineData("/auth/signup/complete//", false)]
    [InlineData("/auth/signup/complete/extra", false)]
    [InlineData("/auth/signup", false)]
    [InlineData("/auth/login", false)]
    [InlineData("/", false)]
    public void IsCompletePath_matches_the_completion_path_only(string path, bool expected) =>
        Assert.Equal(expected, SignupCompleteRequestHandler.IsCompletePath(new PathString(path)));
```

Run `-class Auth.IntegrationTests.LoginScopeTests`: green (the `ShouldShape` rows of Step 3 turned green with Step 8).

- [ ] **Step 10: The endpoint signs in.** Replace `src/Auth.Server/Signup/SignupCompleteEndpoint.cs` by

```csharp
using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Login;
using Auth.Server.Requests;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.Extensions.Options;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Signup;

/// <summary>
/// <c>POST /auth/signup/complete</c> (spec 0009): finishes a sign-up and signs the person in (Amendment 2). No access token: the link token is the
/// credential. While sign-up is off it answers <c>403 signup_disabled</c> before it reads the body — also for a request that was made while it
/// was on. The answer is that of a successful login (<c>200 {"status":"authenticated","access_token"}</c> and the refresh cookie): the endpoint is a
/// pass-through request of OpenIddict (<see cref="SignupCompleteRequestHandler"/>), builds the principal the way the login does
/// (<see cref="SessionPrincipal"/>) and returns an <see cref="AtomicSignInResult"/> that issues the session, writes <c>login.succeeded</c> and
/// commits the transaction of the completion. If any of that fails, nothing of the completion stays.
/// </summary>
public static class SignupCompleteEndpoint
{
    public const string Path = "/auth/signup/complete";

    private static readonly string[] Fields = ["token", "password", "company_name"];

    public static async Task<IResult> HandleAsync(
        HttpContext http, SignupGate gate, SignupService signup, MembershipReader memberships, IOptions<TokenOptions> tokens, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(signup);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);

        // Without it OpenIddict cannot sign in here (it would say so only after the account was made, and roll that back): fail at once.
        if (http.GetOpenIddictServerRequest() is null)
        {
            throw new InvalidOperationException("The sign-up completion was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        }

        if (!gate.IsOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, http.RequestAborted);
        if (fields is null || fields[1].Contains('\0') || !NameInput.IsValid(fields[2]))
        {
            return AccountResults.InvalidRequest();
        }

        var done = await signup.CompleteAsync(fields[0], fields[1], fields[2], http.RequestAborted);
        if (!done.Succeeded)
        {
            return done.Error == TenancyErrors.WeakPassword
                ? AccountResults.WeakPassword(done.BrokenRules!)
                : ApiResults.Error(done.Error!);
        }

        try
        {
            // Read inside the transaction of the completion (the same context): it sees the membership that is not committed yet.
            var account = done.Account!;
            var tenant = await memberships.ReadAsync(account.Id, CancellationToken.None)
                ?? throw new InvalidOperationException("The account that was just made has no company.");
            var principal = SessionPrincipal.Create(http, account, tenant, tokens.Value.Audience, clock);
            return new AtomicSignInResult(
                principal,
                new AuditEntry
                {
                    Kind = AuditKinds.LoginSucceeded,
                    ActorUserId = account.Id,
                    SubjectUserId = account.Id,
                    SubjectEmail = account.Email,
                    OrgId = tenant.CompanyId,
                    OrgName = tenant.CompanyName,
                },
                done.Transaction);
        }
        catch
        {
            // The result would have owned the transaction; it does not exist, so this is the end of the completion: roll it back.
            await done.Transaction!.DisposeAsync();
            throw;
        }
    }
}
```

In `src/Auth.Server/Api/AccountEndpoints.cs` change `private static readonly string RefreshCookieSet =` to `internal static readonly string RefreshCookieSet =` (the completion's route documents the same cookie). In `src/Auth.Server/Api/SignupRoutes.cs` replace the whole `app.MapPost(SignupCompleteEndpoint.Path, ...)` statement by

```csharp
        app.MapPost(SignupCompleteEndpoint.Path, SignupCompleteEndpoint.HandleAsync)
            .WithTags("Sign-up")
            .WithDescription(
                "Finishes a sign-up and signs the person in: in one transaction the account (its email confirmed), the company (named as given here, "
                + "with a copy of the default roles) and the membership with the first admin's role are made, the pending sign-up is used up, and the "
                + "session is issued; if the session cannot be written nothing of this stays. The answer is that of a successful login: the access token "
                + "(carrying the new company and the first admin's role) and the refresh cookie. The checks are made in this order: the token, the "
                + "password (a weak one leaves the token usable), the account (`409 already_registered` when one has the address by now).")
            .ReadsJson<SignupCompleteRequest>()
            .Produces<LoginResponse>()
            .SetsRefreshCookie(StatusCodes.Status200OK, AccountEndpoints.RefreshCookieSet + " The person is signed in, as after a login.")
            .ProducesPasswordError(AccountResults.InvalidRequestError, AccountResults.InvalidTokenError, AccountResults.WeakPasswordError)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.SignupDisabled)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyRegistered);
```

- [ ] **Step 11: Run the classes of the task.** Build with `-warnaserror`, then run (resource rules): `-class Auth.IntegrationTests.SignupCompleteSignInTests -class Auth.IntegrationTests.SignupCompleteTests -class Auth.IntegrationTests.SignupCompleteRaceTests -class Auth.IntegrationTests.SignupFirstAdminTests -class Auth.IntegrationTests.SignupCompleteDisabledTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.LoginTests -class Auth.IntegrationTests.LoginCookieTests -class Auth.IntegrationTests.LoginRequestTests -class Auth.IntegrationTests.RefreshTests -class Auth.IntegrationTests.SessionResponseHandlerTests -class Auth.IntegrationTests.UnhandledTokenRequestGuardTests -class Auth.IntegrationTests.AuditAccountFlowsTests -class Auth.IntegrationTests.AuditAtomicityTests -class Auth.IntegrationTests.RatePolicySignupTests -class Auth.IntegrationTests.JsonCharsetTests -class Auth.IntegrationTests.LoginScopeTests -class Auth.IntegrationTests.SessionLifetimeTests -class Auth.IntegrationTests.InviteAcceptTests`. All pass (`SessionLifetimeTests` and `InviteAcceptTests` guard what the shared principal and the unchanged invitation `204` must keep). If a trigger test fails because the 500 is not what is asserted, look at what the host wrote to the log before changing the test.

- [ ] **Step 12: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `feat(server): completing a sign-up signs the person in`.

---

### Task 15: The completion signs in: the sample, the Playwright test and `scripts/e2e-signup.sh`

**Files:**
- Modify: `samples/notes-web/src/app/auth/auth.service.ts`, `samples/notes-web/src/app/account-api.ts`, `samples/notes-web/src/app/pages/complete-signup.ts`, `samples/notes-web/src/app/pages/login.ts` (one comment), `samples/notes-web/e2e/09-signup.spec.ts`, `scripts/e2e-signup.sh`, `scripts/e2e-web.sh` (one comment)
- Create (tests): `samples/notes-web/src/app/auth/auth.service.signup.spec.ts`
- Modify (tests): `samples/notes-web/src/app/pages/complete-signup.spec.ts` (replaced as a whole), `samples/notes-web/src/app/account-api.signup.spec.ts`, `samples/notes-web/src/app/pages/too-many-requests.spec.ts` (one line)

**Interfaces:**
- Consumes: Task 14's answer (`200 {"status":"authenticated","access_token"}` and the cookie); `AuthService.login` and its `LoginResult`, `loadMe`, `dropSession`; `failureOf`, `errorCode`, `Failure` (`auth.service.ts`); `DEFAULT_RETURN_URL` (`auth/auth.guard.ts`, `'/notes'`); `AccountApi.previewSignup` and `postForm` (Tasks 10 and 11); `testing/helpers.ts` (`adminMe`, `viewerMe`, `signedInAs`, `settle`, and the rest).
- Produces: `AuthService.completeSignup(token: string, password: string, companyName: string): Promise<LoginResult>` (a `LoginResult` is `{ ok: true } | { ok: false; failure: Failure }`; on `{ ok: true }` the token is held and `/auth/me` has been asked, as after `login`); the exported `formFailureOf(error: unknown): Failure` (`failureOf`, but a `400 invalid_request` is `{ kind: 'invalid_request' }`); `AccountApi.completeSignup` is **removed** (the sign-up forms keep `postForm` for `signUp`); the completion screen goes to `/notes` and no longer to `/login`.

- [ ] **Step 1: Write the unit tests first.**

(a) Create `samples/notes-web/src/app/auth/auth.service.signup.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { adminMe, settle, signedInAs, viewerMe } from '../../testing/helpers';
import { AuthService } from './auth.service';

const status = (code: number) => ({ status: code, statusText: String(code) });
const signedIn = { status: 'authenticated', access_token: 'tok-c' };

describe('AuthService.completeSignup', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    vi.useRealTimers();
    localStorage.clear();
    sessionStorage.clear();
  });

  it('posts the token, the password and the company name, keeps the token in memory and loads /auth/me', async () => {
    const done = auth.completeSignup('tok', 'Pw-1 x', 'Firma');
    const request = ctrl.expectOne('/auth/signup/complete');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok', password: 'Pw-1 x', company_name: 'Firma' });
    request.flush(signedIn);
    await settle();
    ctrl.expectOne('/auth/me').flush(adminMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('tok-c');
    expect(auth.me()).toEqual(adminMe);
    ctrl.verify();
  });

  it('never writes the token to localStorage, sessionStorage or a cookie', async () => {
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    const completion = ctrl.expectOne('/auth/signup/complete');
    completion.flush({ status: 'authenticated', access_token: 'secret-token-value' });
    await settle();
    const me = ctrl.expectOne('/auth/me');
    me.flush(adminMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('secret-token-value');
    expect(window.location.href).not.toContain('secret-token-value');
    expect(completion.request.urlWithParams).not.toContain('secret-token-value');
    expect(me.request.urlWithParams).not.toContain('secret-token-value');
    expect(JSON.stringify({ ...localStorage })).not.toContain('secret-token-value');
    expect(JSON.stringify({ ...sessionStorage })).not.toContain('secret-token-value');
    expect(document.cookie).not.toContain('secret-token-value');
    expect(localStorage.length + sessionStorage.length).toBe(0);
  });

  it.each([
    [400, { error: 'invalid_token' }, { kind: 'invalid_token' }],
    [400, { error: 'weak_password', rules: ['too_short'] }, { kind: 'weak_password', rules: ['too_short'] }],
    [409, { error: 'already_registered' }, { kind: 'already_registered' }],
    [403, { error: 'signup_disabled' }, { kind: 'signup_disabled' }],
    [400, { error: 'invalid_request' }, { kind: 'invalid_request' }],
    [429, { error: 'too_many_requests', retry_after_seconds: 17 }, { kind: 'too_many_requests', retryAfterSeconds: 17 }],
    [500, '<html>oops</html>', { kind: 'other' }],
  ])('answer %s %j is the failure %j and leaves the person anonymous', async (code, body, failure) => {
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    ctrl.expectOne('/auth/signup/complete').flush(body, status(code));
    expect(await done).toEqual({ ok: false, failure });
    expect(auth.token()).toBeNull();
    ctrl.verify();
  });

  it('a 200 that is not "authenticated" is not a session', async () => {
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    ctrl.expectOne('/auth/signup/complete').flush({ status: 'mfa_required' });
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
    expect(auth.token()).toBeNull();
  });

  it('is signed in even when /auth/me cannot be read: the notes screen asks for the person again', async () => {
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    ctrl.expectOne('/auth/signup/complete').flush(signedIn);
    await settle();
    ctrl.expectOne('/auth/me').flush('oops', status(500));
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('tok-c');
    expect(auth.me()).toBeNull();
  });

  it('opening the link while another person is signed in: the old person is gone, and the new one is shown', async () => {
    await signedInAs(auth, ctrl, adminMe, 'tok-a');
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    ctrl.expectOne('/auth/signup/complete').flush(signedIn);
    await settle();
    ctrl.expectOne('/auth/me').flush(viewerMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('tok-c');
    expect(auth.me()).toEqual(viewerMe);
    ctrl.verify();
  });

  it('a completion that fails leaves the session that exists alone', async () => {
    await signedInAs(auth, ctrl, adminMe, 'tok-a');
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    ctrl.expectOne('/auth/signup/complete').flush({ error: 'invalid_token' }, status(400));
    expect((await done).ok).toBe(false);
    expect(auth.token()).toBe('tok-a');
    expect(auth.me()).toEqual(adminMe);
  });

  it('clears an old notice when the person is signed in', async () => {
    auth.showNotice('unreachable');
    const done = auth.completeSignup('tok', 'pw', 'Firma');
    ctrl.expectOne('/auth/signup/complete').flush(signedIn);
    await settle();
    ctrl.expectOne('/auth/me').flush(adminMe);
    await done;
    expect(auth.notice()).toBeNull();
  });
});
```

(b) Replace `samples/notes-web/src/app/pages/complete-signup.spec.ts` as a whole by the file below (the screen now calls `AuthService.completeSignup`, so the answer to `/auth/signup/complete` is a login's, and `/auth/me` follows):

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { adminMe, has, pageText, realLocation, settle, submitForm, typeInto, typeRaw, valueOf } from '../../testing/helpers';
import { AuthService } from '../auth/auth.service';
import { CompleteSignupPage } from './complete-signup';

const status = (code: number) => ({ status: code, statusText: String(code) });
const invalid = 'This link has expired or was already used.';
const closed = 'Sign-up is closed. Ask the company you want to join for an invitation.';
const preview = { email: 'anna@firma.example', company_name: 'Firma' };
/** What Auth-Core answers a completion: a login's answer. */
const signedIn = { status: 'authenticated', access_token: 'tok-c' };

async function open(query = '?token=tok-s', previewAnswer: 'ok' | 'wait' = 'ok') {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([{ path: 'complete-signup', component: CompleteSignupPage }]),
      provideHttpClient(),
      provideHttpClientTesting(),
      realLocation,
    ],
  });
  history.replaceState(null, '', `/complete-signup${query}`);
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/complete-signup${query}`, CompleteSignupPage);
  await settle(); // the navigation that takes the token out of the address bar lands first
  const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const ctrl = TestBed.inject(HttpTestingController);
  if (previewAnswer === 'ok') {
    ctrl.expectOne('/auth/signup/preview').flush(preview);
    await settle(harness.fixture);
  }
  return { fixture: harness.fixture, ctrl, navigateByUrl };
}

type Opened = Awaited<ReturnType<typeof open>>;

function choose({ fixture }: Opened, password: string, repeat = password): void {
  typeInto(fixture, '#password', password);
  typeInto(fixture, '#repeat', repeat);
  submitForm(fixture);
}

function button(opened: Opened): HTMLButtonElement | null {
  return (opened.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button[type="submit"]');
}

/** Answers the completion as Auth-Core does, then the /auth/me that signing in asks for. */
async function signInBy(opened: Opened, completion: TestRequest): Promise<void> {
  completion.flush(signedIn);
  await settle();
  opened.ctrl.expectOne('/auth/me').flush(adminMe);
  await settle(opened.fixture);
}

describe('CompleteSignupPage', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('asks for the preview with the token on opening, once, and takes the token out of the address bar and the router', async () => {
    const { ctrl } = await open('?token=tok-s', 'wait');
    const request = ctrl.expectOne('/auth/signup/preview');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-s' });
    expect(window.location.search).toBe('');
    expect(window.location.href).not.toContain('tok-s');
    expect(TestBed.inject(Router).url).toBe('/complete-signup');
    request.flush(preview);
    await settle();
    ctrl.expectNone('/auth/signup/preview'); // the navigation that removed the token did not run the screen again
  });

  it('shows the address (not editable), the company name (editable) and who the password is for', async () => {
    const { fixture } = await open();
    expect(valueOf(fixture, '#email')).toBe('anna@firma.example');
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#email')?.readOnly).toBe(true);
    expect(valueOf(fixture, '#company')).toBe('Firma');
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#company')?.readOnly).toBe(false);
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#company')?.maxLength).toBe(100);
    expect(pageText(fixture)).toContain('This sets the password for anna@firma.example.');
    expect(has(fixture, '#password')).toBe(true);
    expect(has(fixture, '#repeat')).toBe(true);
  });

  it('200: signs the person in and opens the notes screen in place of this one; the address and the token are in no URL', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    const request = opened.ctrl.expectOne('/auth/signup/complete');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-s', password: 'Chosen-Passw0rd', company_name: 'Firma' });
    await signInBy(opened, request);
    const auth = TestBed.inject(AuthService);
    expect(auth.token()).toBe('tok-c');
    expect(auth.me()).toEqual(adminMe);
    // `replaceUrl`: Back from the notes screen must not land on this screen again, whose link is used up.
    expect(opened.navigateByUrl).toHaveBeenCalledTimes(1);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/notes', { replaceUrl: true });
    expect(window.location.href).not.toContain('anna@firma.example');
    expect(window.location.href).not.toContain('tok-c');
  });

  it('200 when /auth/me cannot be read: the person is signed in and the notes screen opens (it asks for the person again)', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush(signedIn);
    await settle();
    opened.ctrl.expectOne('/auth/me').flush('oops', status(500));
    await settle(opened.fixture);
    expect(TestBed.inject(AuthService).token()).toBe('tok-c');
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/notes', { replaceUrl: true });
  });

  it('a 200 that is not a sign-in: "Something went wrong. Try again.", the form stays, and nothing opens', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush({ status: 'mfa_required' });
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(has(opened.fixture, 'form')).toBe(true);
    expect(TestBed.inject(AuthService).token()).toBeNull();
    expect(opened.navigateByUrl).not.toHaveBeenCalled();
  });

  it('sends the company name the person typed, trimmed', async () => {
    const opened = await open();
    typeInto(opened.fixture, '#company', '  Firma Renamed  ');
    choose(opened, 'Chosen-Passw0rd');
    const request = opened.ctrl.expectOne('/auth/signup/complete');
    expect(request.request.body).toEqual({ token: 'tok-s', password: 'Chosen-Passw0rd', company_name: 'Firma Renamed' });
    await signInBy(opened, request);
  });

  it('sends the password exactly as typed', async () => {
    const opened = await open();
    choose(opened, '  Chosen Passw0rd  ');
    const request = opened.ctrl.expectOne('/auth/signup/complete');
    expect(request.request.body).toEqual({ token: 'tok-s', password: '  Chosen Passw0rd  ', company_name: 'Firma' });
    await signInBy(opened, request);
  });

  it('weak_password: the rules not met, one line each, and the form stays', async () => {
    const opened = await open();
    choose(opened, 'short');
    opened.ctrl
      .expectOne('/auth/signup/complete')
      .flush({ error: 'weak_password', rules: ['too_short', 'requires_digit'] }, status(400));
    await settle(opened.fixture);
    const lines = Array.from(
      (opened.fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="rules"] li'),
      (item) => item.textContent?.trim(),
    );
    expect(lines).toEqual(['The password is too short.', 'The password needs a digit.']);
    expect(has(opened.fixture, 'form')).toBe(true);
    expect(opened.navigateByUrl).not.toHaveBeenCalled();
    expect(TestBed.inject(AuthService).token()).toBeNull();
  });

  it('409 already_registered: says the account exists and links to the sign-in; no form', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'already_registered' }, status(409));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('An account with this address already exists.');
    expect(has(opened.fixture, 'a[href="/login"]')).toBe(true);
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it.each(['preview', 'complete'])('invalid_token on the %s: the link is used up, with a link to sign up again; no form', async (where) => {
    const opened = await open('?token=tok-s', where === 'preview' ? 'wait' : 'ok');
    if (where === 'preview') {
      opened.ctrl.expectOne('/auth/signup/preview').flush({ error: 'invalid_token' }, status(400));
    } else {
      choose(opened, 'Chosen-Passw0rd');
      opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'invalid_token' }, status(400));
    }
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(invalid);
    expect(has(opened.fixture, 'a[href="/signup"][data-testid="signup-again"]')).toBe(true);
    expect(pageText(opened.fixture)).toContain('Sign up again');
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it.each(['preview', 'complete'])('403 signup_disabled on the %s: says that sign-up is closed; no form', async (where) => {
    const opened = await open('?token=tok-s', where === 'preview' ? 'wait' : 'ok');
    if (where === 'preview') {
      opened.ctrl.expectOne('/auth/signup/preview').flush({ error: 'signup_disabled' }, status(403));
    } else {
      choose(opened, 'Chosen-Passw0rd');
      opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'signup_disabled' }, status(403));
    }
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(closed);
    expect(pageText(opened.fixture)).toContain('Back to sign in');
    expect(has(opened.fixture, 'form')).toBe(false);
    expect(has(opened.fixture, 'a[href="/login"]')).toBe(true);
  });

  it.each([
    [400, { error: 'invalid_request' }],
    [500, '<html>oops</html>'],
  ])('%s on the preview: "Something went wrong. Try again."', async (code, body) => {
    const { fixture, ctrl } = await open('?token=tok-s', 'wait');
    ctrl.expectOne('/auth/signup/preview').flush(body, status(code));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Something went wrong. Try again.');
  });

  it('"Try again" after a server error on the preview asks again with the token still held, and the form follows', async () => {
    const { fixture, ctrl } = await open('?token=tok-s', 'wait');
    ctrl.expectOne('/auth/signup/preview').flush('oops', status(500));
    await settle(fixture);
    expect(window.location.search).toBe('');
    const again = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="try-again"]');
    expect(again?.textContent?.trim()).toBe('Try again');
    again?.click();
    fixture.detectChanges();
    expect(pageText(fixture)).toContain('Opening your sign-up...');
    const retry = ctrl.expectOne('/auth/signup/preview');
    expect(retry.request.body).toEqual({ token: 'tok-s' });
    retry.flush(preview);
    await settle(fixture);
    expect(has(fixture, 'form')).toBe(true);
    expect(has(fixture, '[data-testid="try-again"]')).toBe(false);
  });

  it('500 on complete: "Something went wrong. Try again.", and the form stays', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush('oops', status(500));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it('two different passwords: a message, and no request', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd', 'Chosen-Passw0rd2');
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('The two passwords are not the same.');
    opened.ctrl.expectNone('/auth/signup/complete');
  });

  it('a blank company name: a message, and no request', async () => {
    const opened = await open();
    typeRaw(opened.fixture, '#company', '   ');
    choose(opened, 'Chosen-Passw0rd');
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Enter the name of your company.');
    opened.ctrl.expectNone('/auth/signup/complete');
  });

  it('400 invalid_request on complete: says what to check, and the form stays', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'invalid_request' }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Check the company name.');
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it.each(['', '?token=', '?other=1'])('a link with the query %j is an invalid link, with no request', async (query) => {
    const { fixture, ctrl } = await open(query, 'wait');
    expect(pageText(fixture)).toContain(invalid);
    expect(has(fixture, 'a[href="/signup"]')).toBe(true);
    ctrl.expectNone('/auth/signup/preview');
  });

  it('sends one request for a double submit', async () => {
    const opened = await open();
    choose(opened, 'Chosen-Passw0rd');
    submitForm(opened.fixture);
    await signInBy(opened, opened.ctrl.expectOne('/auth/signup/complete'));
  });

  it.each([
    ['rejects', () => Promise.reject(new Error('navigation failed'))],
    ['answers false', () => Promise.resolve(false)],
  ])('the button is on again when the router %s (the person stays here, signed in)', async (_name, answer) => {
    const opened = await open();
    opened.navigateByUrl.mockImplementation(answer);
    choose(opened, 'Chosen-Passw0rd');
    await signInBy(opened, opened.ctrl.expectOne('/auth/signup/complete'));
    expect(button(opened)?.disabled).toBe(false);
    expect(TestBed.inject(AuthService).token()).toBe('tok-c');
  });

  it('after 200 the button stays off until the notes screen is open: a second Enter does not post the used token again', async () => {
    const opened = await open();
    let arrive: (opened: boolean) => void = () => undefined;
    opened.navigateByUrl.mockReturnValue(new Promise<boolean>((resolve) => (arrive = resolve)));
    choose(opened, 'Chosen-Passw0rd');
    await signInBy(opened, opened.ctrl.expectOne('/auth/signup/complete'));
    expect(button(opened)?.disabled).toBe(true);
    submitForm(opened.fixture);
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/signup/complete');
    arrive(true);
    await settle(opened.fixture);
    expect(button(opened)?.disabled).toBe(false);
  });
});
```

(c) In `samples/notes-web/src/app/account-api.signup.spec.ts`: delete the test `completeSignup posts the token, the password and the company name` (the call moved to `AuthService`; `auth.service.signup.spec.ts` has it), and in the last table test (`it.each([...])('answer %s %j is the failure %j', ...)`) change its body to use the sign-up request, which shares `postForm` and so the mapping:

```ts
    const done = api.signUp('anna@firma.example', 'Firma');
    ctrl.expectOne('/auth/signup').flush(body, status(code));
    expect(await done).toEqual({ ok: false, failure });
```

(d) In `samples/notes-web/src/app/pages/too-many-requests.spec.ts`, in the test `CompleteSignupPage: keeps the form, leaves the words to the notice bar`, add `const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl');` just before its `submitForm(opened.fixture);`, and replace its last line `expect(TestBed.inject(Router).navigate).not.toHaveBeenCalledWith(['/login'], expect.anything());` by

```ts
    expect(opened.auth.token()).toBeNull(); // a refused completion signs nobody in
    expect(navigateByUrl).not.toHaveBeenCalled();
    expect(TestBed.inject(Router).url).toBe('/complete-signup'); // and the screen stays where it is
```

(`vi` and `Router` are imported in that file already: `open()` uses both.)

- [ ] **Step 2: Run them, and see them fail.** In `samples/notes-web` (resource rules above): `npx ng test --watch=false --include "src/app/auth/auth.service.signup.spec.ts" --include "src/app/pages/complete-signup.spec.ts" --include "src/app/account-api.signup.spec.ts" --include "src/app/pages/too-many-requests.spec.ts"`. Expected: the build fails or the tests fail (`completeSignup` is not a method of `AuthService`; the screen still goes to `/login`).

- [ ] **Step 3: `AuthService` signs in on a completion.** In `samples/notes-web/src/app/auth/auth.service.ts`:

(a) After `failureOf`, add

```ts
/**
 * Like `failureOf`, for the sign-up forms: a `400 invalid_request` is a failure of its own there, because each of those screens has a text
 * for it (the sign-in and the other mail-link screens read it as "other").
 */
export function formFailureOf(error: unknown): Failure {
  return error instanceof HttpErrorResponse && error.status === 400 && errorCode(error) === 'invalid_request'
    ? { kind: 'invalid_request' }
    : failureOf(error);
}
```

(b) Replace the method `login` by these three methods (the tail of `login` moves into the private `startSession`, which both sign-ins use):

```ts
  async login(email: string, password: string): Promise<LoginResult> {
    let body: LoginBody | null;
    try {
      body = await firstValueFrom(this.http.post<LoginBody | null>('/auth/login', { email, password }));
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
    return this.startSession(body);
  }

  /**
   * Finishes a self-service sign-up with the token of the mail link. Auth-Core answers as it answers a login (spec 0009, Amendment 2): the
   * access token in the body and the refresh cookie, so the person is signed in at once and the screen opens the product. A refusal leaves
   * the session that exists alone, as a failed login does.
   */
  async completeSignup(token: string, password: string, companyName: string): Promise<LoginResult> {
    let body: LoginBody | null;
    try {
      body = await firstValueFrom(
        this.http.post<LoginBody | null>('/auth/signup/complete', { token, password, company_name: companyName }),
      );
    } catch (error) {
      return { ok: false, failure: formFailureOf(error) };
    }
    return this.startSession(body);
  }

  /** What a sign-in that Auth-Core answered with `200` does: a session of its own, and who it is for. */
  private async startSession(body: LoginBody | null): Promise<LoginResult> {
    const token = body?.access_token;
    if (body?.status !== 'authenticated' || typeof token !== 'string' || token === '') {
      return { ok: false, failure: { kind: 'other' } };
    }
    // A session that exists ends first (/login has no guard, so a signed-in person can sign in as someone else): what the old
    // person asked for and is still on its way, an /auth/me or a request that gets a 401, is never used for the new one.
    this.dropSession();
    this.accessToken.set(token);
    this.noticeState.set(null);
    // Signed in once there is a token, even when this answer cannot be read: the notes screen asks for the person again.
    // Ten seconds, like the start: a sign-in must not hang behind an /auth/me that never answers.
    await this.loadMe(REFRESH_TIMEOUT_MS);
    return { ok: true };
  }
```

(c) In `samples/notes-web/src/app/account-api.ts`: delete the method `completeSignup`; make `postForm` read

```ts
  /** Like `post`, but a `400 invalid_request` is a failure of its own: the sign-up form has a text for it. */
  private async postForm<T = void>(url: string, body: unknown): Promise<Outcome<T>> {
    try {
      return { ok: true, value: await firstValueFrom(this.http.post<T>(url, body)) };
    } catch (error) {
      return { ok: false, failure: formFailureOf(error) };
    }
  }
```

and fix the imports: `import { HttpClient } from '@angular/common/http';` and `import { Failure, failureOf, formFailureOf, isRecord } from './auth/auth.service';` (`HttpErrorResponse` and `errorCode` are not used here any more).

- [ ] **Step 4: The screen opens the product.** In `samples/notes-web/src/app/pages/complete-signup.ts`: add the imports `import { DEFAULT_RETURN_URL } from '../auth/auth.guard';` and `import { AuthService } from '../auth/auth.service';`; add the field `private readonly auth = inject(AuthService);` after `account`; and replace the method `submit` by

```ts
  protected async submit(): Promise<void> {
    const signup = this.pending();
    if (this.busy() || this.token === null || signup === null) {
      return;
    }
    const { company, password, repeat } = this.form.getRawValue();
    this.rules.set([]);
    this.error.set(null);
    if (company.trim() === '') {
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
    // Auth-Core refuses white space at either end.
    const result = await this.auth.completeSignup(this.token, password, company.trim());
    if (result.ok) {
      // Signed in (Auth-Core answered as it answers a login): the product opens at once, in place of this screen, so that Back does
      // not land on a link that is used up. The address is in no URL. The button stays off until the notes screen is open: the token
      // is used up, and a second Enter would only post it again. When the router cannot open it (it answers false or fails) the person
      // stays here, signed in, and the button is on again.
      try {
        await this.router.navigateByUrl(DEFAULT_RETURN_URL, { replaceUrl: true });
      } catch {
        // nothing to show: the company was made and the person is signed in
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
      this.error.set(this.t.invalidRequest);
    } else if (result.failure.kind === 'too_many_requests') {
      // The notice bar above the screen says when to try again: the interceptor put it there.
    } else {
      this.error.set(this.common.somethingWrong);
    }
    this.busy.set(false);
  }
```

In `samples/notes-web/src/app/pages/login.ts` change the comment above `startEmail` to `/** The email an invitation screen passed on in the navigation state (it is never in the URL). */` (a completed sign-up signs the person in and no longer passes through the sign-in screen).

- [ ] **Step 5: Run the unit tests of this task.** `npx ng test --watch=false --include "src/app/auth/auth.service.spec.ts" --include "src/app/auth/auth.service.signup.spec.ts" --include "src/app/pages/complete-signup.spec.ts" --include "src/app/account-api.signup.spec.ts" --include "src/app/account-api.spec.ts" --include "src/app/pages/too-many-requests.spec.ts" --include "src/app/pages/login.spec.ts" --include "src/app/pages/signup.spec.ts"` passes, and `npx ng build` builds (the production build type-checks the templates). If `--include` is not accepted by this CLI, run the `src/app/auth` and `src/app/pages` folders by the same flag with a folder glob; do not run the whole suite.

- [ ] **Step 6: The Playwright test.** In `samples/notes-web/e2e/09-signup.spec.ts` (read by `tsc`, not run here):

(a) Remove the line `import { submitLogin } from './support/session';` (the first test no longer signs in by hand; nothing else in the file uses it).

(b) Rename the first test to `'a visitor signs up, opens the mail link, finishes, is signed in at once and is the admin of a company of their own'`.

(c) After `page.on('request', (request) => requested.push(request.url()));` add

```ts
  // The address of the page after every navigation, the router's included: the person must not pass through the sign-in screen
  // after finishing (Amendment 2 of spec 0009).
  const urls: string[] = [];
  page.on('framenavigated', (frame) => {
    if (frame === page.mainFrame()) {
      urls.push(frame.url());
    }
  });
```

(d) Replace the block from the comment `// The name of the request was a suggestion: ...` to the assertion `await expect(page.getByTestId('me-email')).toHaveText(email);` that follows `await submitLogin(page, email, password);` — that is, these lines —

```ts
  // The name of the request was a suggestion: the person changes it, chooses the password and finishes.
  await page.getByLabel('Company name').fill(company);
  await page.getByLabel('New password').fill(password);
  await page.getByLabel('Repeat the password').fill(password);
  await page.getByRole('button', { name: 'Create company' }).click();

  // No sign-in follows: the sign-in screen opens with the address filled in, and the address is not in the URL.
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  expectNoEmailInAddressBar(page);

  await submitLogin(page, email, password);
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
```

by

```ts
  // The name of the request was a suggestion: the person changes it, chooses the password and finishes.
  await page.getByLabel('Company name').fill(company);
  await page.getByLabel('New password').fill(password);
  await page.getByLabel('Repeat the password').fill(password);
  const beforeFinishing = urls.length;
  await page.getByRole('button', { name: 'Create company' }).click();

  // The completion signed the person in: the notes screen opens at once, with no sign-in screen on the way and the address in no URL.
  await expect(page).toHaveURL(/\/notes$/);
  expect(
    urls.slice(beforeFinishing).some((url) => /\/login(\?|$)/.test(url)),
    'the person went through the sign-in screen',
  ).toBe(false);
  // ...and the test did see the navigations, so that the line above says something.
  expect(
    urls.slice(beforeFinishing).some((url) => /\/notes$/.test(url)),
    'the notes screen was never reached by a navigation the test saw',
  ).toBe(true);
  expectNoEmailInAddressBar(page);
  await expect(page.getByTestId('me-email')).toHaveText(email);
```

(e) After the line `await expect(page.getByText('No notes yet.')).toBeVisible();` add

```ts

  // The refresh cookie came with the answer of the completion: a reload keeps the person signed in.
  await page.reload();
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-company')).toHaveText(company);
```

(f) In the fourth test (`'a link that expired says so and offers to sign up again'`) the mail was sent while `scripts/e2e-web.sh` seeded, so the wait is short: change `waitForLink(setting('E2E_EXPIRED_EMAIL'), 'complete-signup', MAIL_WAIT_MS)` to `waitForLink(setting('E2E_EXPIRED_EMAIL'), 'complete-signup', 10_000)` (a mail that is not there fails in 10 seconds with its own message, not at the 30-second limit of the test with none). In the fourth and the fifth test (`'a link that never worked says so and offers to sign up again'`) add `await expect(page).toHaveURL(/\/complete-signup$/);` before each `expectCleanAddressBar(page);`: the token leaves the address bar a moment after the screen opens, and the check must wait for it. The other tests are unchanged. Check the types: `npx tsc -p e2e/tsconfig.json --noEmit` in `samples/notes-web`.

- [ ] **Step 7: The API script.** In `scripts/e2e-signup.sh` (checked by `bash -n scripts/e2e-signup.sh` only; the verifiers run it). Besides the sign-in, this step repairs the findings of the review of Task 12 (`33c13f7`) in the same file. Line numbers are those of the file at `33c13f7`, before this step.

(a) **The header comment.** Replace the items 6 and 7 by

```bash
#   6. complete with a weak password -> 400 weak_password naming the rules, no cookie, and the token still works; then with a good password
#      and another company name -> 200 {"status":"authenticated","access_token"} and the auth_rt cookie (HttpOnly, Secure, SameSite=Strict,
#      Path=/auth), as a login answers (spec 0009, Amendment 2): the token has roles ["admin"] and the new company, GET /auth/me with it names
#      the company as given at the completion, the cookie refreshes (and rotates), and the audit log holds one login.succeeded row for NEW;
#      again -> 400 invalid_token and no cookie - criteria 11, 12 (part of: parallel completions), 9.
#   7. NEW logs in again (the email is confirmed): the token has the org_id of the completion's company, which is not the development
#      company's; GET /auth/org/roles lists the company's own copy of the two default roles (admin "*", user) with NEW the only member of
#      admin; the audit log holds signup.requested, signup.completed (this account by id, this company, this address) and org.created with
#      via signup, none of its rows holds the password, the token of the link, or the access token or the refresh cookie of the completion,
#      and the pending sign-up is gone - criteria 9, 10 (part of: the other manifests), 19 (part of: atomicity).
```

In item 4 add, before its `- criteria 3, 21 (part of: Polish).`, the words `exactly one signup.requested row for NEW has been written since the run began;`. At the end of item 8 (line 30) replace `- criterion 4.` by `- part of criterion 4 (that the second request runs the same statements as the first is guarded by the tests, not here).` In item 9 (line 31) after `its login is the 401 of an unknown address` add `, recorded as a failed login of an unknown address, and no account exists`.

(b) **Helpers**, after the function `psql_value` (they are used from Step 1 on): the three that read the cookie (the text of `scripts/e2e-refresh.sh`), a poll, and the secret search of `scripts/e2e-hardening.sh` (lines 290 to 294 there, the same text):

```bash
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

# wait_psql_value <sql> <value> <seconds>: polls the query once a second until it returns the value; false if it never does
wait_psql_value() {
  local i
  for i in $(seq 1 "$3"); do
    [[ "$(psql_value "$1")" == "$2" ]] && return 0
    sleep 1
  done
  return 1
}

# secret_in_audit <secret>: how many rows of the audit log hold the text in any column; the secret goes on stdin only
secret_in_audit() {
  [[ "$1" != *"'"* ]] || fail "a secret with a quote cannot be looked for"
  printf "SELECT count(*) FROM audit_events WHERE position('%s' in (kind || ' ' || coalesce(subject_email, '') || ' ' || coalesce(org_name, '') || ' ' || coalesce(client_ip, '') || ' ' || coalesce(details::text, ''))) > 0;\n" "$1" \
    | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'
}
```

(c) **Step 2 and Step 4.** Just before the line `json_body "$tmp/signup.json" ...` of Step 2 add

```bash
START_TS="$(psql_value "SELECT now()")"   # `occurred_at` is a timestamp with a time zone, as is this
```

and in Step 4, after the `expect_eq "step 4: no pending sign-up for the address of the refused names" ...` line, add

```bash
expect_eq "step 4: one signup.requested row for NEW, written since this run began" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.requested' AND subject_email = '$NEW_EMAIL' AND occurred_at >= '$START_TS'")" "1"
```

(d) **`token_body`** (lines 221 to 224 of the script): the HTML part must hold the whole link and not only the token. Replace

```python
if match.group(1) not in mail["HTML"]:
    sys.exit("the HTML part does not hold the link of the text part")
```

by

```python
if sys.argv[1] + "?token=" + match.group(1) not in mail["HTML"]:
    sys.exit("the HTML part does not hold the link of the text part")
```

(If the HTML part writes the link in another form, look at the mail before weakening the check.)

(e) **Step 6**: replace the whole of Step 6 (from the line `# --- Step 6: the completion (criteria 9, 11, 12) ...` to its `pass "step 6: ..."` line) by

```bash
# --- Step 6: the completion, which signs in (criteria 9, 11, 12) ------------------------------------------------------
call POST /auth/signup/complete "$tmp/complete-weak.json"
expect_status "step 6: complete with a weak password" 400 '{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}'
expect_no_cookie "step 6: the weak password"
call POST /auth/signup/complete "$tmp/complete-good.json"
expect_status "step 6: complete with a good password and another company name" 200
expect_no_store "step 6: the 200"
expect_eq "step 6: the body is a login's" "$(val 'sorted(d.keys())')" "['access_token', 'status']"
expect_eq "step 6: the status" "$(val 'd["status"]')" "authenticated"
LINE="$(set_cookie_line)"
[[ -n "$LINE" ]] || fail "step 6: the completion set no auth_rt cookie"
for attr in HttpOnly Secure SameSite=Strict Path=/auth; do
  has_attr "$LINE" "$attr" || fail "step 6: the auth_rt cookie lacks the $attr attribute"
done
RT0="$(cookie_value)"
[[ -n "$RT0" ]] || fail "step 6: the auth_rt cookie is empty"
COMPLETION_ACCESS="$(val 'd["access_token"]')"
printf 'Authorization: Bearer %s\n' "$COMPLETION_ACCESS" > "$tmp/completion.auth"
printf 'Cookie: auth_rt=%s\n' "$RT0" > "$tmp/completion.cookie"
BODY="$(jwt_payload "$tmp/completion.auth")"
expect_eq "step 6: roles of the completion's token" "$(val 'd["roles"]')" "['admin']"
COMPLETION_ORG="$(val 'd["org_id"]')"
call GET /auth/me "" "$tmp/completion.auth"
expect_status "step 6: GET /auth/me with the token of the completion" 200
expect_eq "step 6: the company is the one named at the completion" "$(val 'd["org_name"]')" "$RENAMED"
expect_eq "step 6: the address" "$(val 'd["email"]')" "$NEW_EMAIL"
expect_eq "step 6: org_id of /auth/me and of the token" "$(val 'd["org_id"]')" "$COMPLETION_ORG"
call POST /auth/refresh "" "$tmp/completion.cookie"
expect_status "step 6: refresh with the cookie of the completion" 200
[[ -n "$(cookie_value)" && "$(cookie_value)" != "$RT0" ]] || fail "step 6: the refresh did not rotate the cookie"
printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/refreshed.auth"
BODY="$(jwt_payload "$tmp/refreshed.auth")"
expect_eq "step 6: the refreshed token names the same company" "$(val 'd["org_id"]')" "$COMPLETION_ORG"
expect_eq "step 6: one login.succeeded row, written by the completion, for NEW and its company" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.succeeded' AND subject_email = '$NEW_EMAIL' AND org_name = '$RENAMED'")" "1"
call POST /auth/signup/complete "$tmp/complete-good.json"
expect_error "step 6: the same completion again" 400 invalid_token
expect_no_cookie "step 6: the same completion again"
call POST /auth/signup/preview "$tmp/preview.json"
expect_error "step 6: preview of the used token" 400 invalid_token
pass "step 6: a weak password -> 400 weak_password naming the rules (no cookie; the token still works); a good one -> 200 with a login's body and the auth_rt cookie (the attributes of ADR 0004), a token with roles [admin] and the new company, /auth/me names it, the cookie refreshes and rotates, one login.succeeded row; again -> 400 invalid_token, no cookie"
```

(f) **Step 7.** In Step 7, right after the line `NEW_ORG="$(val 'd["org_id"]')"` add

```bash
NEW_SUB="$(val 'd["sub"]')"
expect_eq "step 7: a login names the company of the completion's token" "$NEW_ORG" "$COMPLETION_ORG"
```

after the line `expect_eq "step 7: org_id of /auth/me and of the token" ...` add

```bash
call GET /auth/org/roles "" "$tmp/new.auth"
expect_status "step 7: GET /auth/org/roles" 200
expect_eq "step 7: the company's own copy of the default roles, and NEW the only member of admin" \
  "$(val 'sorted((r["name"], sorted(r["permissions"]), r["members"]) for r in d["roles"])')" \
  "[('admin', ['*'], 1), ('user', ['documents:read', 'documents:write'], 0)]"
```

(the value is what the manifest `deploy/auth.yaml` and the sole member say; as in `scripts/e2e-tenancy.sh`, lines 324 to 326 and 334 to 337, the first run shows the exact form), in the line `expect_eq "step 7: one signup.completed row, ..."` replace `subject_user_id IS NOT NULL` by `subject_user_id = '$NEW_SUB'` (the id comes from the payload of the token, which names the account), and replace the line `expect_eq "step 7: no audit row holds the password" ...` by

```bash
SIGNUP_TOKEN="$("$PY" -c 'import json,sys; print(json.load(sys.stdin)["token"])' < "$tmp/preview.json" | tr -d '\r')"   # never printed
expect_eq "step 7: no audit row holds the password" "$(secret_in_audit "$E2E_PASSWORD")" "0"
expect_eq "step 7: no audit row holds the token of the link" "$(secret_in_audit "$SIGNUP_TOKEN")" "0"
expect_eq "step 7: no audit row holds the access token of the completion" "$(secret_in_audit "$COMPLETION_ACCESS")" "0"
expect_eq "step 7: no audit row holds the refresh cookie of the completion" "$(secret_in_audit "$RT0")" "0"
```

and in its `pass` line replace "NEW logs in" by "NEW logs in again" and "no password" by "no password and no token".

(g) **Step 8.** Replace the lines

```bash
# The pending sign-up goes in the transaction of the send, which commits just after the relay has the mail.
sleep 3
expect_eq "step 8: the pending sign-up is gone" ...
```

by (the `expect_eq` line stays as it is, after the poll)

```bash
# The pending sign-up goes in the transaction of the send, which commits just after the relay has the mail: wait for it, do not guess.
wait_psql_value "SELECT count(*) FROM \"PendingSignups\" WHERE \"NormalizedEmail\" = upper('$NEW_EMAIL')" 0 20 || fail "step 8: the pending sign-up of NEW is still there after 20s"
expect_eq "step 8: the pending sign-up is gone" ...
```

and replace the line `grep -qF "$NEW_ORG"$'\t'"$RENAMED"$'\t'"1" <<< "$CLI_OUT" || fail ...` by the whole-line form and add the count of the company, so that

```bash
grep -qxF "$NEW_ORG"$'\t'"$RENAMED"$'\t'"1" <<< "$CLI_OUT" || fail "step 8: list-orgs does not show the company of the completion with exactly one member"
expect_eq "step 8: one company named as at the completion" "$(grep -cF $'\t'"$RENAMED"$'\t' <<< "$CLI_OUT" || true)" "1"
```

stand in its place (the line with `no company was made for the second request` stays).

(h) **Step 9.** After the line `expect_error "step 9: login of an address with only a pending sign-up" 401 invalid_credentials` add

```bash
expect_eq "step 9: that login is recorded as a failed login of an unknown address" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.failed' AND subject_email = '$PENDING_EMAIL' AND details->>'reason' = 'unknown_address'")" "1"
expect_eq "step 9: PENDING has no account" "$(psql_value "SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = upper('$PENDING_EMAIL')")" "0"
```

(`unknown_address` is the reason `LoginEndpoint` writes for an address with no account) and replace the comment and the line `sleep 8` before `expect_eq "step 9: PENDING got no mail but the sign-up mail" ...` by

```bash
# The dispatcher removes the request of an address without an account within seconds: wait until none of PENDING's is left, or the count below would only mean that nothing was sent yet.
wait_psql_value "SELECT count(*) FROM \"MailRequests\" WHERE \"NormalizedEmail\" = upper('$PENDING_EMAIL')" 0 30 || fail "step 9: the mail requests of PENDING were not handled within 30s"
```

(the `expect_eq "step 9: PENDING got no mail but the sign-up mail" ... "1"` stays after it).

- [ ] **Step 8: `scripts/e2e-web.sh`, one comment.** In `scripts/e2e-web.sh` (line 163 at the base) change the comment `# Two invitations through the operator CLI. The server sends the mails at its next poll.` to `# Three invitations through the operator CLI (BOTH is for the sign-up test of spec 0009). The server sends the mails at its next poll.` (it has said "Two" since the third `cli invite` was added by Task 12).

- [ ] **Step 9: Check and hand back.** `bash -n scripts/e2e-signup.sh scripts/e2e-web.sh`; `npx tsc -p e2e/tsconfig.json --noEmit` and `npx ng build` in `samples/notes-web`; `grep -rInP '\\u[0-9a-fA-F]{4}' samples/notes-web/src samples/notes-web/e2e scripts --include=*.ts --include=*.sh` finds nothing that is not meant. **Hand back** — uncommitted. Files: everything listed under Files. Proposed note for the verifiers: `scripts/e2e-web.sh` (Chromium and WebKit) and `scripts/e2e-signup.sh` have to be run against the new server; the cookie is `Secure` and the sample is served from `http://localhost`, as for the sign-in. Proposed subject: `feat(samples): completing a sign-up opens the notes screen`.

---

## Amendment (2026-04-04): a person without a company comes back by sign-up

The owner's **Amendment 3** of [spec 0009](../specs/0009-self-service-signup.md) (2026-04-04) replaces Decision 9: **someone whose company was deleted, or who was removed from it, has an account and no access (spec 0005), and may start a company of their own by signing up.** The contract the spec now states (Mails, `POST /auth/signup/preview`, `POST /auth/signup/complete` steps 3 to 5, criterion 24):

- At **send time** an address whose account belongs to **no company**, with a **live pending sign-up**, gets the ordinary "complete your sign-up" mail with its link (an account that belongs to a company still gets the "you already have an account" notice; with no live pending sign-up and no company, nothing is sent). The request itself is untouched: it still never reads the accounts.
- The **preview** answers `200 {"email","company_name","has_account"}`; `has_account` is `true` only for such an account.
- The **completion** for such an account does not use the password (it may be absent) and does not change it; the account keeps its password and linked sign-ins, its email is confirmed, the company is created with the account as its first admin, the person is signed in (Amendment 2), and the audit row `signup.completed` carries `details.existing_account` `true`. If the account has joined a company meanwhile: `409 already_registered` (the pending sign-up is deleted).
- The sample's completion screen asks only for the company name.

Tasks 1 to 15 are implemented (the working tree holds Tasks 14 and 15, uncommitted, as the code stands). The real files win over the text of the earlier tasks; the code below was written against them (read on 2026-04-04). This section adds **Task 16** (the server and its tests) and **Task 17** (the sample, the Playwright test, the live scripts) on **Saturday 04.04**, before **Task 13**, which moves after them on the same day and documents the amendment. Order: 12, 14, 15, 16, 17, 13. Where the text of Tasks 5, 6, 7, 8, 11 and 12 disagrees with this section (an address with an account always gets the notice; the preview has two members; the password is always required; Decision 9), **this section wins**: those tasks are history, and Tasks 16 and 17 change the code and the tests they produced.

### What the code does today, and the design chosen

**Read, not run** (the machine is busy and Docker is off; the plan's code below is written on paper, and the implementer's tests decide).

- `MailDispatcher.PrepareSignupMailAsync` looks the account up first (`StoredAddressOfAsync`: the first account by id under the normalised address) and, if there is any, sends the notice and deletes the pending sign-up; its two SQL statements (`DropUnmailableAsync`, the `due` select of `DispatchOneAsync`) also treat *any* account as a reason to keep or take a sign-up row. All three must learn that only an account **with a company** is a reason.
- `SignupService.CompleteAsync` consumes the token, takes the advisory lock of the address (`pg_advisory_xact_lock(hashtextextended(address, 0))`, the one `InviteAcceptance` takes), checks the password (`PasswordRules.BrokenAsync`), then answers `409 already_registered` (committing the deletion of the pending row) for *any* account under the normalised address, else creates the account, ends its sessions, creates the company, the membership and `signup.completed`, and hands the open transaction to `AtomicSignInResult`.
- `InviteAcceptance` has the same lock and, for an existing account, answers `already_member` when `db.Memberships.AnyAsync(m => m.UserId == user.Id)`; it also **replaces the password** of an existing account and ends its sessions. So a comeback and an acceptance for one company-less account cannot both win: whichever takes the lock second sees the membership of the first (the race test below pins both orders).
- `JsonObjectBody.ReadStringsAsync` treats every named member as required and non-blank; a completion for an account that needs no password therefore needs an optional member. `SignupCompleteEndpoint` reads `[token, password, company_name]` through it.
- `SignupPreviewResponse(Email, CompanyName)` and `SignupCompleteRequest(Token, Password, CompanyName)` are the OpenAPI shapes (`ReadsJson<T>()` documents the type; the endpoints read the bodies by hand).
- The existing tests make an "account that exists" with `CreateUserAsync(address, confirmed, member: false)` — an account **without a company**. Under Amendment 3 those are now the comeback accounts; the tests that meant "an account with a company" must say so (`member: true`, the helper's default). Task 16, Step 2 lists every one.

**The design.**

1. **One definition of "the account this link may use".** A new `SignupAccounts.FindAsync` returns the first account under the normalised address (id, stored address, whether it has a membership); `SignupAccount.MayStartCompany(typedEmail)` is true when it has **no company** and its stored address is the typed address **up to ASCII case** (`EmailInput.IsSameAddress`, the rule invitation acceptance uses: the account lookup folds some different addresses together — micro sign and Greek mu, final sigma and sigma — and a link proves only the mailbox it was sent to, so an account that merely *looks like* the typed address is never used; it is treated as an account the link cannot start a company for: notice mail, `409`). The dispatcher, the preview and the completion all use it, so they cannot disagree.
2. **Dispatcher.** An account that belongs to a company: notice (as today). Otherwise the live pending sign-up is locked as today, and **the account is read again once the lock is held** (the lock may have waited for a completion that has just committed, or for an invitation that has just been accepted): an account with a company now gets the notice, whether or not the pending sign-up survived; no pending sign-up: nothing; an account that is not usable by this link (the look-alike case): notice; otherwise the complete mail. The two SQL statements count only an account **with a membership** as a reason to keep a row: a company-less account with no live pending sign-up is dropped by the bulk delete like an address with no account, and a flood of such requests is never handled one by one.
3. **Preview.** `HasAccount = account?.MayStartCompany(pending.Email) == true`. An account that has joined a company since the mail, or that only looks like the address, is `false` (the screen then asks for a password and the completion answers `409`, which is the contract's `409` case).
4. **Completion.** After the token and the lock (unchanged): find the account. If it is usable (`MayStartCompany`): no password check, **a password in the body is ignored**, the tracked account gets `EmailConfirmed = true` through `UserManager.UpdateAsync`, any session or mail link it still has ends, as for a new account and as at an invitation (`AccountSessions.EndAllAsync`: refresh tokens, authorizations, other email links, login streak) — defence in depth: removing a member and deleting a company already revoke their sessions, and a login without a company is `403`, so normally there is nothing left to end, the company, the membership and the audit rows follow. If it is not usable: a password that is there is checked (`400 weak_password`, token usable); an account under the address (one that joined a company since the mail, or one that only looks like the address) is `409 already_registered` with the pending row gone, **whether or not a password came** (a screen that was told `has_account` sent none, and the contract says `409`); with no account the password is required (`400 invalid_request` when absent, the transaction rolled back so the token stays usable) and the account is made as before. The order is token, account, password (Amendment 4 of the spec; the plan first said it stays token, password, account).
5. **The body.** `password` becomes an optional member of the completion request (`JsonObjectBody.ReadStringsAsync` gets an overload with optional names): absent is fine; present it is held to the rule of every string member (a non-blank string without NUL), so `"password":123`, `""`, `null` and a NUL stay `400 invalid_request` whatever the account.
6. **Audit.** `signup.completed` carries `details.existing_account = true` for the comeback and **no details** for a new account (the existing test asserts `Details` is null for a new account, and the spec says the member is `true` "for an account that existed").
7. **OpenAPI.** `SignupPreviewResponse` gets `HasAccount` (`has_account`); `SignupCompleteRequest.Password` becomes `string? Password = null` (still a property; not required); the operation descriptions say so.

**Rejected.** *Sending the notice to a person without a company and letting an invitation cover the case* — that was Decision 9, replaced. *Asking for a password and replacing it* — the owner ruled the link adds no capability (it already allows a reset) and the password stays. *`has_account` true for any account* — the contract says `true` only for an account without a company; an account that joined a company meanwhile would show a screen with no password fields and still answer `409`, which no sample test (`BOTH` in `09-signup.spec.ts`) expects. *Deciding in the request* — it would read the accounts and break criterion 4.

**Resource rules for the implementers of Tasks 16 and 17** (this machine is shared and Docker may be off; they apply to every step):

- Build with `-maxcpucount:2`: `"C:/Program Files/dotnet/dotnet.exe" build tests/Auth.IntegrationTests -maxcpucount:2 -warnaserror`.
- Run **only the classes the task names**, with `-maxThreads 2`, through the test executable, one `-class` per class: `tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -maxThreads 2 -class Auth.IntegrationTests.SignupComebackTests -class Auth.IntegrationTests.SignupComebackDispatchTests` (they need Docker for Testcontainers: if Docker is off, stop after the build and say so in the hand-back; the orchestrator runs them). Unit tests of the sample: `npx ng test --watch=false --include "<spec file>"`, one `--include` per file.
- **No full suite** (neither `dotnet test tests/Auth.IntegrationTests` without a filter nor `npx ng test` without `--include`) and **no live runs**: no `scripts/e2e-*.sh`, no Playwright, no `docker compose`. The verifiers run those at the end. The scripts and the Playwright test are checked by reading, `bash -n` and `npx tsc -p e2e/tsconfig.json --noEmit`.
- A test that fails with an Npgsql "operation has timed out" while connecting is the machine being busy, not the code: run that class again, alone, before reading it as a failure.
- The implementers leave their changes uncommitted (see Global Constraints).

**Review Focus of the amendment** (inputs a person is likely to meet; each has its test in the task named):

1. **The person whose company is gone asks to sign up, opens the link and finishes with only a company name** — and signs in afterwards with the password they always had (`SignupComebackTests.A_person_without_a_company_comes_back_with_only_the_token_and_a_company_name`, `...A_password_in_the_body_is_not_used`; `complete-signup.spec.ts` "posts only the token and the company name"; `09-signup.spec.ts` "a person whose company is gone comes back by sign-up..."; Tasks 16 and 17).
2. **A person who still has a company never gets a link** (narrowed by Amendment 3: an account that belongs to a company) — not at the request, not when the mail is sent, not if they join a company while the mail waits for its lock, and not if they join between the mail and the click (`SignupComebackDispatchTests.An_account_that_belongs_to_a_company_gets_the_notice_and_no_link`, `...An_account_that_joins_a_company_while_the_dispatcher_waits_for_the_pending_sign_up_gets_the_notice`, `...An_account_that_joins_a_company_while_the_dispatcher_waits_and_the_pending_sign_up_survives_gets_the_notice`, `SignupComebackTests.An_account_that_joined_a_company_meanwhile_is_409_and_the_pending_sign_up_is_gone`; Task 16).
3. **An invitation to the same person arrives at the same moment** — one company, the other side `409`, in both orders (`SignupCompleteRaceTests.A_comeback_and_an_invitation_acceptance_for_one_account_never_both_give_it_a_company`; Task 16).
4. **A stranger asks to sign up with an address that only looks like someone's** — the link goes to the stranger's mailbox and must never open the other account (`SignupComebackTests.An_account_under_a_spelling_the_normaliser_folds_is_409_and_is_left_alone`, `SignupComebackDispatchTests.An_account_that_only_looks_like_the_typed_address_gets_the_notice_and_no_link`; Task 16).
5. **Nobody learns who has an account by asking to sign up** — an address with a company, one without, and an unknown one get the same answer and run the same statements (`SignupRequestTests.An_account_without_a_company_gets_the_same_status_headers_body_and_statements`; Task 16).
6. **The sign-in cannot be written halfway** — for a comeback too, the account keeps its unconfirmed address and gets no company (`SignupComebackTests.A_sign_in_that_cannot_be_written_leaves_the_account_as_it_was`; Task 16).

---

### Task 16: A person without a company comes back by sign-up: the server

**Files:**
- Create: `src/Auth.Server/Signup/SignupAccounts.cs`
- Modify: `src/Auth.Server/Requests/JsonObjectBody.cs` (an overload with optional members), `src/Auth.Server/Signup/SignupContracts.cs`, `src/Auth.Server/Signup/SignupService.cs` (replaced as a whole), `src/Auth.Server/Signup/SignupPreviewEndpoint.cs`, `src/Auth.Server/Signup/SignupCompleteEndpoint.cs` (replaced as a whole), `src/Auth.Server/Email/MailDispatcher.cs`, `src/Auth.Server/Api/SignupRoutes.cs` (three descriptions)
- Create (tests): `tests/Auth.IntegrationTests/SignupComebackTests.cs`, `tests/Auth.IntegrationTests/SignupComebackDispatchTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/SignupApi.cs`, `Infrastructure/SignupTestBase.cs`, `SignupDispatchTests.cs`, `SignupCompleteTests.cs`, `SignupCompleteSignInTests.cs`, `SignupCompleteRaceTests.cs`, `SignupPreviewTests.cs`, `SignupRequestTests.cs`, `OpenApiTests.cs`
- Modify: `scripts/e2e-signup.sh` (one expectation, in step 5 of the script)

**Interfaces:**
- Consumes: Task 8, 14's `SignupService`, `SignupCompletion`, `SignupCompleteEndpoint`, `AtomicSignInResult`, `SessionPrincipal`; Task 3's `PendingSignups` (`LockUsableAsync`, `IssueAsync`, `DeleteAsync`, `ConsumeAsync`, `FindUsableAsync`); `AccountSessions.EndAllAsync`/`Ensure`; `EmailInput.IsSameAddress`; `AuditEntry.Details`; the test bases and helpers (`SignupTestBase`, `SessionApi`, `TenancyApi`, `AccountApi`, `AuditApi`, `PendingSignupSeed`).
- Produces: `SignupAccount(Guid Id, string Email, bool BelongsToCompany)` with `MayStartCompany(string typedEmail)`; `SignupAccounts.FindAsync(AuthDbContext, string normalizedEmail, CancellationToken) : Task<SignupAccount?>`; `JsonObjectBody.ReadStringsAsync(HttpRequest, string[] required, string[] optional, CancellationToken) : Task<string?[]?>`; `SignupPreview(string Email, string CompanyName, bool HasAccount)`; `SignupService.CompleteAsync(string token, string? password, string companyName, CancellationToken)`; the wire: preview `200 {"email","company_name","has_account"}`, completion body `{"token","company_name"}` plus an optional `"password"`; the audit detail `existing_account`; test helpers `SignupApi.CompleteWithoutPassword`, `SignupTestBase.RemovedMemberAsync`, `SignupTestBase.JoinACompanyAsync` (Task 17 and the documents rely on the wire and the audit detail).

- [ ] **Step 1: The test helpers.** In `tests/Auth.IntegrationTests/Infrastructure/SignupApi.cs`, after `Complete(...)`, add:

```csharp
    /// <summary>A completion for an account that has no company: no password in the body (spec 0009, Amendment 3).</summary>
    public static Task<HttpResponseMessage> CompleteWithoutPassword(HttpClient client, string token, string companyName) =>
        client.PostAsJsonAsync(CompletePath, new { token, company_name = companyName });
```

In `tests/Auth.IntegrationTests/Infrastructure/SignupTestBase.cs`, before the closing brace of the class, add:

```csharp
    /// <summary>
    /// An account that lost its company (spec 0009, Amendment 3): it was a member of a company, with a password (<see cref="MailTestBase.UserPassword"/>),
    /// and the membership ended straight in the database, which ends no session. That is a state the product does not make (removing a member and
    /// deleting a company revoke the sessions); it is what a completion must still cope with. Returns the id of the account.
    /// </summary>
    protected async Task<Guid> RemovedMemberAsync(string email = Address, bool confirmed = true)
    {
        var company = await CreateCompanyAsync("Former company");
        var userId = await AddMemberAsync(company, email, "user", confirmed);
        await RemoveMembershipAsync(userId);
        return userId;
    }

    /// <summary>Gives the account a company again, straight in the database: the account that "joined a company meanwhile".</summary>
    protected async Task JoinACompanyAsync(Guid userId)
    {
        var company = await CreateCompanyAsync("Newer company");
        var roleId = await RoleIdAsync(company, "user");
        await InDbAsync(async db =>
        {
            db.Memberships.Add(new Membership { UserId = userId, CompanyId = company, RoleId = roleId, JoinedAt = StorableTime.Now(Clock) });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });
    }
```

- [ ] **Step 2: Correct the earlier tests that meant "an account with a company".** Under Amendment 3 an account made with `member: false` is a comeback account. Each edit below keeps what its test is about (an account that belongs to a company); `CreateUserAsync`'s default, `member: true`, puts the account into the development company with the role `user`.

(a) `tests/Auth.IntegrationTests/SignupDispatchTests.cs`:

- The theory `An_address_with_an_account_gets_the_account_exists_mail_and_the_pending_sign_up_is_gone` has four rows of `(confirmed, member)`; the two `member: false` rows now describe the comeback. Replace its attribute lines, signature and first line

```csharp
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task An_address_with_an_account_gets_the_account_exists_mail_and_the_pending_sign_up_is_gone(bool confirmed, bool member)   // criterion 4
    {
        await CreateUserAsync(Address, confirmed, member: member);
```

with

```csharp
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_address_with_an_account_gets_the_account_exists_mail_and_the_pending_sign_up_is_gone(bool confirmed)   // criterion 4: an account that belongs to a company
    {
        await CreateUserAsync(Address, confirmed);
```

- In `The_account_exists_mail_goes_to_the_stored_address_spelled_as_stored`, `An_address_with_an_account_gets_the_notice_even_when_its_pending_sign_up_is_gone`, `An_address_with_an_account_and_an_expired_pending_sign_up_gets_the_notice_and_loses_the_row`, `A_failed_send_to_an_account_keeps_the_pending_sign_up_until_the_mail_is_sent` and `The_mails_are_in_the_configured_language` (the `ola@firma.test` line), delete the argument `, member: false` from the call of `CreateUserAsync` (five calls: `CreateUserAsync("Anna.Firma@Firma.Test", confirmed: true)` twice, `CreateUserAsync(Address, confirmed: true)` twice, `CreateUserAsync("ola@firma.test", confirmed: true)`).
- In `An_account_made_while_the_dispatcher_waits_for_the_pending_sign_up_still_gets_the_notice`, change `await CreateUserAsync(Address, confirmed: true, member: false);   // the account the completion makes` to `await CreateUserAsync(Address, confirmed: true);   // the account the completion makes: it has a company`.

(b) `tests/Auth.IntegrationTests/SignupCompleteTests.cs`: in `The_order_of_the_checks_is_token_then_password_then_account`, `An_account_under_another_spelling_of_the_address_is_409_too` and `No_token_password_or_address_reaches_the_log` (the `Taken` line) delete `, member: false` from `CreateUserAsync(...)` (`CreateUserAsync(Address, confirmed: true)`, `CreateUserAsync("ANNA@Firma.TEST", confirmed: true)`, `CreateUserAsync(Taken, confirmed: true)`). The first of the three then also pins the order of Amendment 4: for an account that has a company the `409` beats a weak password (the plan first said the weak password wins).

(c) `tests/Auth.IntegrationTests/SignupCompleteSignInTests.cs`: in `Every_refusal_is_what_it_was_and_issues_no_cookie_and_no_token` change `await CreateUserAsync(Taken, confirmed: true, member: false);` to `await CreateUserAsync(Taken, confirmed: true);`.

(d) `tests/Auth.IntegrationTests/SignupPreviewTests.cs`, `Preview_answers_the_typed_address_and_the_company_and_changes_nothing`: replace

```csharp
            Assert.Equal(["company_name", "email"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
```

with

```csharp
            Assert.Equal(["company_name", "email", "has_account"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.False(body.GetProperty("has_account").GetBoolean());   // a new address
```

(e) `tests/Auth.IntegrationTests/OpenApiTests.cs`, `The_responses_are_described_in_snake_case`: change `Assert.Equal(["company_name", "email"], Properties("SignupPreviewResponse"));` to `Assert.Equal(["company_name", "email", "has_account"], Properties("SignupPreviewResponse"));`.

- [ ] **Step 3: Write the new tests.** Create `tests/Auth.IntegrationTests/SignupComebackDispatchTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0009, Amendment 3, mails: an address whose account belongs to no company gets the usual "complete your sign-up" mail when its pending
/// sign-up is live; an account that belongs to a company still gets the notice; the request itself is the same for every address.
/// </summary>
public sealed class SignupComebackDispatchTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    private static readonly string Mu = char.ConvertFromUtf32(0x3BC);
    private static readonly string Micro = char.ConvertFromUtf32(0xB5);
    private static readonly string FinalSigma = char.ConvertFromUtf32(0x3C2);
    private static readonly string Sigma = char.ConvertFromUtf32(0x3C3);

    /// <summary>Two different addresses that the account lookup folds into one (the pairs of <c>InviteAcceptTests</c>): the account's, and the typed one.</summary>
    public static TheoryData<string, string> FoldedPairs => new()
    {
        { Mu + "aria@firma.test", Micro + "aria@firma.test" },
        { "kosta" + FinalSigma + "@firma.test", "kosta" + Sigma + "@firma.test" },
    };

    /// <summary>The SQL of every database command the host has logged since <paramref name="mark"/> log entries.</summary>
    private List<string> StatementsSince(int mark) =>
        [.. Logs.Entries.Skip(mark)
            .Where(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command")
            .Select(e => e.Message[(e.Message.IndexOf('\n', StringComparison.Ordinal) + 1)..].Trim())];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_account_without_a_company_gets_the_complete_mail_with_the_link(bool confirmed)   // criterion 24
    {
        await RemovedMemberAsync(Address, confirmed);
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);

        Assert.Equal(1, await DispatchAsync());   // the bulk delete did not take the row

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Address, mail.To);
        Assert.StartsWith("Complete your sign-up", mail.Subject, StringComparison.Ordinal);
        var token = TokenIn(mail);
        Assert.Contains(AuthAppFactory.DefaultCompleteSignupUrl + "?token=" + token, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains($"href=\"{AuthAppFactory.DefaultCompleteSignupUrl}?token={token}\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(CompanyName, mail.TextBody, StringComparison.Ordinal);
        var row = Assert.Single(await PendingAsync());
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);
        Assert.Equal(StorableTime.Now(Clock) + TimeSpan.FromHours(24), row.TokenExpiresAt);
        Assert.Equal(0, await QueueCountAsync());
    }

    [Fact]
    public async Task The_complete_mail_goes_to_the_address_as_typed()   // Review Focus 2 of the plan: the link goes to the mailbox that asked
    {
        await RemovedMemberAsync("Anna.Back@Firma.Test");
        using var response = await SignupApi.Request(Client, "anna.back@firma.test", CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await DispatchAsync();

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal("anna.back@firma.test", mail.To);
        Assert.StartsWith("Complete your sign-up", mail.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_that_belongs_to_a_company_gets_the_notice_and_no_link()   // criterion 4 and 24
    {
        var userId = await RemovedMemberAsync();
        await JoinACompanyAsync(userId);   // it lost one company and has another
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        foreach (var part in new[] { mail.Subject, mail.TextBody, mail.HtmlBody })
        {
            Assert.DoesNotContain("://", part, StringComparison.Ordinal);
            Assert.DoesNotContain("token", part, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(await PendingAsync());
        Assert.Equal(0, await QueueCountAsync());
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("expired")]
    public async Task A_sign_up_for_an_account_without_a_company_whose_pending_sign_up_is_gone_sends_nothing(string how)   // the mail bullets of spec 0009, Amendment 3
    {
        await RemovedMemberAsync();
        if (how == "expired")
        {
            // A row that has expired but not yet been pruned, and a request queued just now: the queue must not take it for a live one.
            await InDbAsync(async db =>
            {
                var now = StorableTime.Now(Clock);
                db.PendingSignups.Add(new PendingSignup
                {
                    NormalizedEmail = "ANNA@FIRMA.TEST",
                    Email = Address,
                    CompanyName = CompanyName,
                    RequestedAt = now - TimeSpan.FromHours(30),
                    ExpiresAt = now - TimeSpan.FromHours(6),
                });
                db.MailRequests.Add(new MailRequest { Kind = MailKind.Signup, NormalizedEmail = "ANNA@FIRMA.TEST", RequestedAt = now, NextAttemptAt = now });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                return 0;
            });
        }
        else
        {
            using var response = await SignupApi.Request(Client, Address, CompanyName);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await InDbAsync(db => db.PendingSignups.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        }

        var mark = Logs.Entries.Count;
        Assert.Equal(1, await DispatchAsync());   // dropped by the bulk delete: no company, nothing to complete

        Assert.Empty(Mail.Sent);
        Assert.Equal(0, await QueueCountAsync());
        // The proof that it was the bulk delete and not the single-row path: that path locks the pending sign-up first (LockUsableAsync).
        Assert.DoesNotContain(StatementsSince(mark), sql => sql.StartsWith("SELECT * FROM \"PendingSignups\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_send_to_an_account_without_a_company_leaves_the_previous_link_valid()   // a failed sending leaves the previous link valid
    {
        await RemovedMemberAsync();
        var first = await MailedAsync(company: "First");
        Clock.Advance(PastTheLimit);
        using var again = await SignupApi.Request(Client, Address, "Second");
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);

        Mail.Failing = true;
        Assert.Equal(1, await DispatchAsync());

        Assert.Single(Mail.Sent);   // nothing new went out
        Assert.Equal(EmailTokens.HashOf(first), Assert.Single(await PendingAsync()).TokenHash);
        Assert.Equal(1, await QueueCountAsync());

        Mail.Failing = false;
        Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await DispatchAsync());

        var second = TokenIn(Mail.Sent[1]);
        Assert.NotEqual(first, second);
        Assert.Equal(EmailTokens.HashOf(second), Assert.Single(await PendingAsync()).TokenHash);
    }

    [Theory]
    [MemberData(nameof(FoldedPairs))]
    public async Task An_account_that_only_looks_like_the_typed_address_gets_the_notice_and_no_link(string account, string typed)   // the link proves the typed mailbox only
    {
        string normalized;
        using (var scope = Factory.Services.CreateScope())
        {
            var normalizer = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>();
            normalized = normalizer.NormalizeEmail(account);
            Assert.Equal(normalized, normalizer.NormalizeEmail(typed));   // the premise: the lookup folds the two together
        }

        await RemovedMemberAsync(account);
        // The pending sign-up and its queued request, made straight in the database: what the request would have left (the address rule of the
        // request is not what this test is about).
        await InDbAsync(async db =>
        {
            var now = StorableTime.Now(Clock);
            db.PendingSignups.Add(new PendingSignup
            {
                NormalizedEmail = normalized,
                Email = typed,
                CompanyName = CompanyName,
                RequestedAt = now,
                ExpiresAt = now + TimeSpan.FromHours(24),
            });
            db.MailRequests.Add(new MailRequest { Kind = MailKind.Signup, NormalizedEmail = normalized, RequestedAt = now, NextAttemptAt = now });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(account, mail.To);   // the notice goes to the account's own address
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("token", mail.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task An_account_that_joins_a_company_while_the_dispatcher_waits_for_the_pending_sign_up_gets_the_notice()   // the second look at the accounts
    {
        var userId = await RemovedMemberAsync();
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // A completion that is under way: it has used the pending sign-up up and not yet committed.
        Task<int> dispatch;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await using var completion = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await db.PendingSignups.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            dispatch = DispatchAsync();   // sees an account without a company, then waits for the row that the completion has locked

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (await InDbAsync(d => d.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""").SingleAsync(TestContext.Current.CancellationToken)) != 1)
            {
                Assert.True(DateTime.UtcNow < deadline, "The dispatcher never waited for the lock of the pending sign-up.");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.False(dispatch.IsCompleted);
            await JoinACompanyAsync(userId);   // what the completion gives the account
            await completion.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, await dispatch);

        var mail = Assert.Single(Mail.Sent);
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        Assert.Equal(0, await QueueCountAsync());
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task An_account_that_joins_a_company_while_the_dispatcher_waits_and_the_pending_sign_up_survives_gets_the_notice()   // the account is read again after the lock
    {
        var userId = await RemovedMemberAsync();
        using var response = await SignupApi.Request(Client, Address, CompanyName);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Something else holds the row of the pending sign-up and changes nothing in it (a second dispatcher on another request of the
        // address, say); the invitation of the account is accepted while the dispatcher waits for that lock.
        Task<int> dispatch;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await using var holder = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlAsync(
                $"""SELECT 1 FROM "PendingSignups" WHERE "NormalizedEmail" = 'ANNA@FIRMA.TEST' FOR UPDATE""", TestContext.Current.CancellationToken);

            dispatch = DispatchAsync();   // sees an account without a company, then waits for the row

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (await InDbAsync(d => d.Database.SqlQuery<long>($"""SELECT count(*) AS "Value" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'""").SingleAsync(TestContext.Current.CancellationToken)) != 1)
            {
                Assert.True(DateTime.UtcNow < deadline, "The dispatcher never waited for the lock of the pending sign-up.");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            Assert.False(dispatch.IsCompleted);
            await JoinACompanyAsync(userId);
            await holder.RollbackAsync(TestContext.Current.CancellationToken);   // nothing was changed in the row: it survives
        }

        Assert.Equal(1, await dispatch);

        var mail = Assert.Single(Mail.Sent);
        Assert.StartsWith("You already have an account", mail.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("token", mail.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await QueueCountAsync());
        Assert.Empty(await PendingAsync());   // the notice deletes it
    }
}
```

Create `tests/Auth.IntegrationTests/SignupComebackTests.cs`:

```csharp
using System.Net;
using Auth.Infrastructure.Identity;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0009, Amendment 3, preview and completion: a person whose account has no company (the company was deleted, or they were removed from it)
/// finishes a sign-up with only the token and a company name; the account keeps its password, its email is confirmed, and the person is signed in.
/// </summary>
public sealed class SignupComebackTests(PostgresFixture postgres, KeyMaterialFixture keys) : SignupTestBase(postgres, keys)
{
    private const string NewCompany = "Firma Again";
    private const string Backslash = "\\";

    // The triggers of SignupCompleteSignInTests: the database refuses the audit row of the sign-in, after the completion has done its own work.
    private const string RefuseLoginSucceeded =
        "CREATE FUNCTION refuse_row() RETURNS trigger AS $$ BEGIN IF NEW.kind = 'login.succeeded' THEN RAISE EXCEPTION 'refused'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql";
    private const string RefuseAudit =
        "CREATE TRIGGER refuse_row BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION refuse_row()";
    private const string AllowAudit = "DROP TRIGGER refuse_row ON audit_events";

    private static readonly string Mu = char.ConvertFromUtf32(0x3BC);
    private static readonly string Micro = char.ConvertFromUtf32(0xB5);
    private static readonly string FinalSigma = char.ConvertFromUtf32(0x3C2);
    private static readonly string Sigma = char.ConvertFromUtf32(0x3C3);

    public static TheoryData<string, string> FoldedPairs => new()
    {
        { Mu + "aria@firma.test", Micro + "aria@firma.test" },
        { "kosta" + FinalSigma + "@firma.test", "kosta" + Sigma + "@firma.test" },
    };

    public static TheoryData<string> MalformedPasswords => new()
    {
        """{"token":"TOKEN","company_name":"Firma Again","password":123}""",
        """{"token":"TOKEN","company_name":"Firma Again","password":""}""",
        """{"token":"TOKEN","company_name":"Firma Again","password":"   "}""",
        """{"token":"TOKEN","company_name":"Firma Again","password":null}""",
        "{\"token\":\"TOKEN\",\"company_name\":\"Firma Again\",\"password\":\"Chosen" + Backslash + "u0000Passw0rd\"}",   // NUL in the password
    };

    private Task<ApplicationUser> UserAsync(Guid id) =>
        InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id, TestContext.Current.CancellationToken));

    private Task<Guid> CompanyIdAsync(string name) =>
        InDbAsync(db => db.Companies.Where(c => c.Name == name).Select(c => c.Id).SingleAsync(TestContext.Current.CancellationToken));

    private Task<int> CompaniesNamedAsync(string name) =>
        InDbAsync(db => db.Companies.CountAsync(c => c.Name == name, TestContext.Current.CancellationToken));

    [Fact]
    public async Task Preview_says_has_account_for_an_account_without_a_company_and_only_for_it()   // criteria 8 and 24
    {
        // A new address.
        var fresh = await MailedAsync("new@firma.test");
        using (var preview = await SignupApi.Preview(Client, fresh))
        {
            var body = await TenancyApi.ReadOkAsync(preview);
            Assert.Equal(["company_name", "email", "has_account"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.False(body.GetProperty("has_account").GetBoolean());
        }

        // An account without a company, asked for in another case.
        await RemovedMemberAsync("Back@Firma.Test");
        var returning = await MailedAsync("back@firma.test", "Asked for");
        var before = Assert.Single(await PendingAsync(), p => p.NormalizedEmail == "BACK@FIRMA.TEST");
        for (var i = 0; i < 2; i++)
        {
            using var preview = await SignupApi.Preview(Client, returning);
            var body = await TenancyApi.ReadOkAsync(preview);
            Assert.True(body.GetProperty("has_account").GetBoolean());
            Assert.Equal("back@firma.test", body.GetProperty("email").GetString());   // the typed address
            Assert.Equal("Asked for", body.GetProperty("company_name").GetString());
        }

        var after = Assert.Single(await PendingAsync(), p => p.NormalizedEmail == "BACK@FIRMA.TEST");
        Assert.Equal(before.TokenHash, after.TokenHash);   // the preview changes nothing

        // An account that has joined a company since the mail: no longer "has_account".
        var userId = await RemovedMemberAsync("joined@firma.test");
        var joined = await MailedAsync("joined@firma.test");
        await JoinACompanyAsync(userId);
        using var last = await SignupApi.Preview(Client, joined);
        Assert.False((await TenancyApi.ReadOkAsync(last)).GetProperty("has_account").GetBoolean());
    }

    [Fact]
    public async Task A_person_without_a_company_comes_back_with_only_the_token_and_a_company_name()   // criterion 24
    {
        var userId = await RemovedMemberAsync();
        var before = await UserAsync(userId);
        var token = await MailedAsync();

        using var done = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        var session = await SessionApi.ReadSessionAsync(done);   // 200, the access token and the refresh cookie, as a login answers

        var company = await CompanyIdAsync(NewCompany);
        Assert.Equal(userId.ToString(), new JsonWebToken(session.AccessToken).Subject);
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        var membership = Assert.Single(await InDbAsync(db => db.Memberships.AsNoTracking().Where(m => m.CompanyId == company).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(userId, membership.UserId);
        Assert.Equal(1, await InDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == "ANNA@FIRMA.TEST", TestContext.Current.CancellationToken)));
        Assert.Empty(await PendingAsync());

        // The same account: its id, its address as it spells it and its password are as they were.
        var after = await UserAsync(userId);
        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.UserName, after.UserName);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.True(after.EmailConfirmed);

        // ...so the password it always had signs it in, to the new company.
        var login = await SessionApi.LoginAsync(Client, Address, UserPassword);
        Assert.Equal(company.ToString(), AccessTokens.Text(login.AccessToken, "org_id"));
        Assert.Equal(["admin"], AccessTokens.Array(login.AccessToken, "roles"));

        // The link is used up.
        using var again = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        await AccountApi.AssertErrorAsync(again, AccountApi.InvalidToken);
    }

    [Theory]
    [InlineData("Brand-New-Passw0rd")]
    [InlineData("x")]
    public async Task A_password_in_the_body_is_not_used(string sent)   // criterion 24: it may be absent, and when present it is ignored
    {
        await RemovedMemberAsync();
        var token = await MailedAsync();

        using var done = await SignupApi.Complete(Client, token, sent, NewCompany);   // even a weak one: it is not checked

        _ = await SessionApi.ReadSessionAsync(done);
        _ = await SessionApi.LoginAsync(Client, Address, UserPassword);   // the password the account had still works
        using var other = await LoginApi.Login(Client, Address, sent);
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);      // and the one that was sent was not set
    }

    [Fact]
    public async Task An_unconfirmed_address_is_confirmed_by_the_link()   // criterion 24
    {
        var userId = await RemovedMemberAsync(confirmed: false);
        Assert.False((await UserAsync(userId)).EmailConfirmed);
        var token = await MailedAsync();

        using var done = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        _ = await SessionApi.ReadSessionAsync(done);

        Assert.True((await UserAsync(userId)).EmailConfirmed);
        _ = await SessionApi.LoginAsync(Client, Address, UserPassword);   // an unconfirmed address could not log in
    }

    [Fact]
    public async Task The_completion_is_recorded_with_existing_account_true_beside_login_succeeded_and_org_created()   // criterion 19 and 24
    {
        var userId = await RemovedMemberAsync("Anna@Firma.test");
        var token = await MailedAsync("anna@firma.test");   // asked for in another case

        using var done = await RateLimitApi.SendAsync(
            Client, HttpMethod.Post, SignupApi.CompletePath, CallerAddress, null,
            $$"""{"token":"{{token}}","company_name":"{{NewCompany}}"}""");
        var session = await SessionApi.ReadSessionAsync(done);

        var company = await CompanyIdAsync(NewCompany);
        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal(userId, completed.SubjectUserId);
        Assert.Equal("Anna@Firma.test", completed.SubjectEmail);   // as the account stores it
        Assert.Equal(company, completed.OrgId);
        Assert.Equal(NewCompany, completed.OrgName);
        Assert.Equal(CallerAddress, completed.ClientIp);
        Assert.Null(completed.ActorUserId);
        Assert.Equal(["existing_account"], AuditApi.DetailNames(completed));
        Assert.Equal("true", AuditApi.Raw(completed, "existing_account"));

        var created = Assert.Single(await AuditAsync(AuditKinds.OrgCreated), row => row.OrgId == company);
        Assert.Equal("signup", AuditApi.Text(created, "via"));
        var signedIn = await SingleAsync(AuditKinds.LoginSucceeded);
        Assert.Equal(userId, signedIn.SubjectUserId);
        Assert.Equal(company, signedIn.OrgId);
        await AssertNoSecretsAsync(token, session.AccessToken, session.RefreshToken);
    }

    [Fact]
    public async Task Sessions_from_before_the_company_was_lost_end_with_the_completion()   // defence in depth, as at an invitation: the product's removal revokes sessions itself; this pins that the completion does too
    {
        var company = await CreateCompanyAsync("Former company");
        var userId = await AddMemberAsync(company, Address, "user");
        var old = await SessionApi.LoginAsync(Client, Address, UserPassword);   // a session of the former membership
        await RemoveMembershipAsync(userId);                                     // straight in the database: no session is ended (the product's removal would have ended it)
        var token = await MailedAsync();

        using var done = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        var fresh = await SessionApi.ReadSessionAsync(done);

        using (var stale = await SessionApi.Refresh(Client, old.RefreshToken))
        {
            await SessionApi.AssertInvalidGrantAsync(stale);
        }

        var refreshed = await SessionApi.RefreshOk(Client, fresh.RefreshToken);   // the session of the completion is the one that works
        Assert.Equal(AccessTokens.Text(fresh.AccessToken, "org_id"), AccessTokens.Text(refreshed.AccessToken, "org_id"));
    }

    [Theory]
    [MemberData(nameof(MalformedPasswords))]
    public async Task A_malformed_password_is_400_invalid_request_for_such_an_account_too_and_the_token_stays_usable(string template)   // criterion 24: a member that is present is held to the rule
    {
        await RemovedMemberAsync();
        var token = await MailedAsync();

        using (var response = await AccountApi.PostRaw(Client, SignupApi.CompletePath, template.Replace("TOKEN", token, StringComparison.Ordinal)))
        {
            await AccountApi.AssertErrorAsync(response, AccountApi.InvalidRequest);
        }

        Assert.Equal(0, await CompaniesNamedAsync(NewCompany));
        using var good = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Fact]
    public async Task A_new_address_without_a_password_is_400_invalid_request_and_the_token_stays_usable()   // the password is still required of everyone but an account without a company
    {
        var token = await MailedAsync();

        using (var without = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany))
        {
            await AccountApi.AssertErrorAsync(without, AccountApi.InvalidRequest);
        }

        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Email == Address, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await CompaniesNamedAsync(NewCompany));
        Assert.Equal(EmailTokens.HashOf(token), Assert.Single(await PendingAsync()).TokenHash);   // rolled back with the rest
        using var good = await SignupApi.Complete(Client, token, Password, NewCompany);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }

    [Fact]
    public async Task An_unknown_token_without_a_password_is_invalid_token()   // the order of the checks: the token first
    {
        using var unknown = await SignupApi.CompleteWithoutPassword(Client, EmailTokens.NewToken(), NewCompany);

        await AccountApi.AssertErrorAsync(unknown, AccountApi.InvalidToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_account_that_joined_a_company_meanwhile_is_409_and_the_pending_sign_up_is_gone(bool withPassword)   // criterion 24
    {
        var userId = await RemovedMemberAsync();
        var token = await MailedAsync();
        await JoinACompanyAsync(userId);   // an invitation accepted since the mail, say
        var companiesBefore = await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken));

        // The screen was told has_account and sent no password: the answer is the 409 all the same.
        using var done = withPassword
            ? await SignupApi.Complete(Client, token, Password, NewCompany)
            : await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);

        await SignupApi.AssertAlreadyRegisteredAsync(done);
        Assert.Equal(companiesBefore, await InDbAsync(db => db.Companies.CountAsync(TestContext.Current.CancellationToken)));   // creates no company
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == userId, TestContext.Current.CancellationToken)));
        Assert.Empty(await PendingAsync());
        using var again = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        await AccountApi.AssertErrorAsync(again, AccountApi.InvalidToken);
    }

    [Theory]
    [MemberData(nameof(FoldedPairs))]
    public async Task An_account_under_a_spelling_the_normaliser_folds_is_409_and_is_left_alone(string account, string typed)   // the link proves the typed mailbox only
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var normalizer = scope.ServiceProvider.GetRequiredService<ILookupNormalizer>();
            Assert.Equal(normalizer.NormalizeEmail(account), normalizer.NormalizeEmail(typed));   // the premise
        }

        var userId = await RemovedMemberAsync(account, confirmed: false);
        var before = await UserAsync(userId);
        var token = await PendingSignupSeed.IssueAsync(Factory.Services, typed, CompanyName);

        using (var preview = await SignupApi.Preview(Client, token))
        {
            Assert.False((await TenancyApi.ReadOkAsync(preview)).GetProperty("has_account").GetBoolean());
        }

        using var done = await SignupApi.Complete(Client, token, Password, NewCompany);

        await SignupApi.AssertAlreadyRegisteredAsync(done);
        var after = await UserAsync(userId);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.False(after.EmailConfirmed);   // the link did not confirm an address it was not sent to
        Assert.Equal(0, await CompaniesNamedAsync(NewCompany));
        Assert.Empty(await PendingAsync());
    }

    [Fact]
    public async Task A_sign_in_that_cannot_be_written_leaves_the_account_as_it_was()   // Amendment 2 for a comeback: the sign-in is part of the completion
    {
        var userId = await RemovedMemberAsync(confirmed: false);
        var token = await MailedAsync();
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseLoginSucceeded, TestContext.Current.CancellationToken));
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(RefuseAudit, TestContext.Current.CancellationToken));

        using (var failed = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.False(failed.Headers.Contains("Set-Cookie"), "A completion that failed must not write the cookie.");
        }

        var unchanged = await UserAsync(userId);
        Assert.False(unchanged.EmailConfirmed);   // the confirmation went with the rollback
        Assert.Equal(0, await CompaniesNamedAsync(NewCompany));
        Assert.Equal(0, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == userId, TestContext.Current.CancellationToken)));
        Assert.Empty(await AuditAsync(AuditKinds.SignupCompleted));
        Assert.Equal(EmailTokens.HashOf(token), Assert.Single(await PendingAsync()).TokenHash);   // the link is as it was

        await InDbAsync(db => db.Database.ExecuteSqlRawAsync(AllowAudit, TestContext.Current.CancellationToken));
        using var again = await SignupApi.CompleteWithoutPassword(Client, token, NewCompany);
        _ = await SessionApi.ReadSessionAsync(again);
        Assert.True((await UserAsync(userId)).EmailConfirmed);
    }
}
```

In `tests/Auth.IntegrationTests/SignupCompleteRaceTests.cs`, add after `A_completion_and_an_invitation_acceptance_for_one_address_never_both_create_an_account`:

```csharp
    [Fact]
    public async Task Of_parallel_completions_for_an_account_without_a_company_exactly_one_is_200()   // criteria 12 and 24
    {
        var userId = await RemovedMemberAsync();
        var token = await MailedAsync();

        Task<HttpResponseMessage>[] requests;
        await using (var held = await HoldAddressAsync("ANNA@FIRMA.TEST"))
        {
            requests = [.. Enumerable.Range(0, 6).Select(_ => SignupApi.CompleteWithoutPassword(Client, token, CompanyName))];
            await WaitForWaitersAsync(6);
            await held.ReleaseAsync();
        }

        var responses = await Task.WhenAll(requests);
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

        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == CompanyName, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == userId, TestContext.Current.CancellationToken)));
        Assert.Empty(await PendingAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_comeback_and_an_invitation_acceptance_for_one_account_never_both_give_it_a_company(bool completionFirst)   // criterion 24: the 409 race
    {
        var company = await CreateCompanyAsync("Acme");
        var address = completionFirst ? "back-first@firma.test" : "invite-first@firma.test";
        var normalized = address.ToUpperInvariant();
        const string Name = "Firma Race";
        var userId = await RemovedMemberAsync(address);
        var signupToken = await MailedAsync(address, "Asked for");   // an account without a company gets the link
        var invite = await AddInviteAsync(company, address, "user");
        await EnqueueInvitationAsync(invite, address);
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == address));

        // Both have done everything up to the lock of the address and wait for it, in the order the test names.
        Task<HttpResponseMessage> complete;
        Task<HttpResponseMessage> accept;
        await using (var held = await HoldAddressAsync(normalized))
        {
            if (completionFirst)
            {
                complete = SignupApi.CompleteWithoutPassword(Client, signupToken, Name);
                await WaitForWaitersAsync(1);
                accept = TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd");
            }
            else
            {
                accept = TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd");
                await WaitForWaitersAsync(1);
                complete = SignupApi.CompleteWithoutPassword(Client, signupToken, Name);
            }

            await WaitForWaitersAsync(2);
            await held.ReleaseAsync();
        }

        using var completed = await complete;
        using var accepted = await accept;

        if (completionFirst)
        {
            _ = await SessionApi.ReadSessionAsync(completed);
            Assert.Equal(HttpStatusCode.Conflict, accepted.StatusCode);
            Assert.Equal("""{"error":"already_member"}""", await accepted.Content.ReadAsStringAsync());
            Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == Name, TestContext.Current.CancellationToken)));
            _ = await SessionApi.LoginAsync(Client, address, UserPassword);   // the invitation did not touch the password
        }
        else
        {
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
            await SignupApi.AssertAlreadyRegisteredAsync(completed);
            Assert.Equal(0, await InDbAsync(db => db.Companies.CountAsync(c => c.Name == Name, TestContext.Current.CancellationToken)));
            var login = await SessionApi.LoginAsync(Client, address, "Invited-Passw0rd");   // the acceptance set its password, as it does for any existing account
            Assert.Equal(company.ToString(), AccessTokens.Text(login.AccessToken, "org_id"));
        }

        Assert.Equal(1, await InDbAsync(db => db.Memberships.CountAsync(m => m.UserId == userId, TestContext.Current.CancellationToken)));
        Assert.DoesNotContain(await PendingAsync(), p => p.NormalizedEmail == normalized);
    }
```

In `tests/Auth.IntegrationTests/SignupRequestTests.cs`, after `The_request_runs_the_same_statements_for_any_address_and_never_reads_the_accounts`, add:

```csharp
    [Fact]
    public async Task An_account_without_a_company_gets_the_same_status_headers_body_and_statements()   // criteria 4 and 24: the request still never reads the accounts
    {
        await RemovedMemberAsync("gone@firma.test");

        var mark = Logs.Entries.Count;
        using var withCompany = await SignupApi.Request(Client, Factory.SeedEmail, CompanyName);
        var forCompany = StatementsSince(mark);

        mark = Logs.Entries.Count;
        using var withoutCompany = await SignupApi.Request(Client, "gone@firma.test", CompanyName);
        var forNone = StatementsSince(mark);

        mark = Logs.Entries.Count;
        using var unknown = await SignupApi.Request(Client, "nobody@firma.test", CompanyName);
        var forUnknown = StatementsSince(mark);

        static List<string> OfTheRequest(List<string> statements) =>
            [.. statements.Where(sql => !sql.StartsWith("DELETE", StringComparison.Ordinal)
                && (sql.Contains("PendingSignups", StringComparison.Ordinal)
                    || sql.Contains("MailRequest", StringComparison.Ordinal)
                    || sql.Contains("audit_events", StringComparison.Ordinal)))];

        foreach (var response in new[] { withCompany, withoutCompany, unknown })
        {
            await AccountApi.AssertEmptyAsync(response, HttpStatusCode.Accepted);
            Assert.Equal(LoginApi.HeaderNames(withCompany), LoginApi.HeaderNames(response));
        }

        Assert.NotEmpty(OfTheRequest(forNone));
        Assert.Equal(OfTheRequest(forCompany), OfTheRequest(forNone));
        Assert.Equal(OfTheRequest(forUnknown), OfTheRequest(forNone));
        Assert.DoesNotContain(forCompany.Concat(forNone).Concat(forUnknown), sql => sql.Contains("AspNetUsers", StringComparison.Ordinal));
        Assert.DoesNotContain(forCompany.Concat(forNone).Concat(forUnknown), sql => sql.Contains("Memberships", StringComparison.Ordinal));
    }
```

In `tests/Auth.IntegrationTests/OpenApiTests.cs`, after `The_responses_are_described_in_snake_case`, add (the helper resolves a `$ref` the way `BodyProperties` does):

```csharp
    [Fact]
    public async Task The_completion_asks_for_no_password_of_an_account_without_a_company()   // criterion 24
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        var document = await DescriptionAsync(factory);
        var operation = Operations(document)["POST /auth/signup/complete"];
        var schema = operation["requestBody"]!["content"]!["application/json"]!["schema"]!.AsObject();
        if (schema["$ref"] is { } reference)
        {
            schema = document["components"]!["schemas"]![reference.GetValue<string>().Split('/')[^1]]!.AsObject();
        }

        Assert.NotNull(schema["properties"]!["password"]);   // still described

        // The sentence Step 11 adds to the description of the operation: it fails before the change whatever the generator does with `required`.
        Assert.Contains("may be left out", operation["description"]!.GetValue<string>(), StringComparison.Ordinal);
        if (schema["required"] is System.Text.Json.Nodes.JsonArray required)
        {
            Assert.DoesNotContain("password", required.Select(node => node!.GetValue<string>()));
        }
    }
```

First print the generated document once (a scratch `Console.WriteLine` or a debugger view, removed afterwards) and look at it: where the description of the operation sits (`operation["description"]`) and whether the request schema has a `required` array at all. If there is none, the `if` simply does not run and the sentence is what the test pins; if there is one, `password` must not be in it (before the change it is, so the test fails for that reason too). Adjust the two lookups to what the document really holds; do not weaken the sentence assertion.

(If the generator marks no member as required at all, drop the last two assertions and keep the first two: the contract is only that `password` is not required.)

- [ ] **Step 4: Build, and see the new tests fail.** `"C:/Program Files/dotnet/dotnet.exe" build tests/Auth.IntegrationTests -maxcpucount:2 -warnaserror` must succeed (the tests compile against what exists: `has_account` is read from JSON, `CompleteWithoutPassword` posts a body). Then run `tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -maxThreads 2 -class Auth.IntegrationTests.SignupComebackTests -class Auth.IntegrationTests.SignupComebackDispatchTests`: the tests of the comeback fail (the dispatcher sends the notice, the completion answers `409` or `400`, the preview has no `has_account`). The edited earlier tests of Step 2 that name `has_account` fail too; the rest passes.

- [ ] **Step 5: `JsonObjectBody` gets optional members.** In `src/Auth.Server/Requests/JsonObjectBody.cs` replace the method `ReadStringsAsync` (from its `<summary>` to its closing brace) with these two:

```csharp
    /// <summary>
    /// The values of the properties <paramref name="names"/>, in that order, or <see langword="null"/> when the
    /// request is not acceptable: wrong content type, too large, not a JSON object, or a named property that is
    /// missing, not a string, or blank.
    /// </summary>
    public static async Task<string[]?> ReadStringsAsync(HttpRequest request, string[] names, CancellationToken cancellationToken)
    {
        var values = await ReadStringsAsync(request, names, [], cancellationToken);
        return values is null ? null : Array.ConvertAll(values, value => value ?? string.Empty);   // none of the named ones is missing
    }

    /// <summary>
    /// Like the overload above, with <paramref name="optional"/> members after the <paramref name="required"/> ones: an optional member may
    /// be missing (its value is <see langword="null"/>); when it is there it is held to the rule of every other member (a non-blank
    /// string), otherwise the request is not acceptable.
    /// </summary>
    public static async Task<string?[]?> ReadStringsAsync(
        HttpRequest request, string[] required, string[] optional, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(optional);

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
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var values = new string?[required.Length + optional.Length];
            for (var i = 0; i < required.Length; i++)
            {
                if (!TryGetRequiredString(root, required[i], out var value))
                {
                    return null;
                }

                values[i] = value;
            }

            for (var i = 0; i < optional.Length; i++)
            {
                if (!root.TryGetProperty(optional[i], out _))
                {
                    continue;
                }

                if (!TryGetRequiredString(root, optional[i], out var value))
                {
                    return null;
                }

                values[required.Length + i] = value;
            }

            return values;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: GetString() on a string holding an unpaired surrogate escape.
            return null;
        }
    }
```

(Keep the comment the file already has in this place, whatever its exact words.)

- [ ] **Step 6: The account of a sign-up.** Create `src/Auth.Server/Signup/SignupAccounts.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Requests;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Signup;

/// <summary>
/// The account under the address of a sign-up, as far as a sign-up cares (spec 0009, Amendment 3): its id, its address as it stores it, and
/// whether it belongs to a company.
/// </summary>
public sealed record SignupAccount(Guid Id, string Email, bool BelongsToCompany)
{
    /// <summary>
    /// Whether a sign-up link sent to <paramref name="typedEmail"/> may start a company for this account: it belongs to none, and its address is
    /// the typed one up to ASCII case. The account lookup folds some different addresses into one (the micro sign and Greek mu, final sigma and
    /// sigma…); a link proves only the mailbox it was sent to, so an account that merely looks like the typed address is never used
    /// (<see cref="EmailInput.IsSameAddress"/>, the rule invitation acceptance applies).
    /// </summary>
    public bool MayStartCompany(string typedEmail)
    {
        ArgumentNullException.ThrowIfNull(typedEmail);

        return !BelongsToCompany && EmailInput.IsSameAddress(Email, typedEmail);
    }
}

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
            .Select(u => new { u.Id, u.Email, BelongsToCompany = db.Memberships.Any(m => m.UserId == u.Id) })
            .FirstOrDefaultAsync(cancellationToken);
        return row is { Email: { } email } ? new SignupAccount(row.Id, email, row.BelongsToCompany) : null;
    }
}
```

- [ ] **Step 7: The bodies of the contract.** In `src/Auth.Server/Signup/SignupContracts.cs` replace the two records

```csharp
/// <summary>Response of <c>POST /auth/signup/preview</c>: the address as typed and the company name of the request.</summary>
public sealed record SignupPreviewResponse(string Email, string CompanyName);
```

with

```csharp
/// <summary>
/// Response of <c>POST /auth/signup/preview</c>: the address as typed, the company name of the request, and whether an account without a
/// company has the address (spec 0009, Amendment 3): then the completion needs no password.
/// </summary>
public sealed record SignupPreviewResponse(string Email, string CompanyName, bool HasAccount);
```

and

```csharp
/// <summary>Request of <c>POST /auth/signup/complete</c>. The company name is the person's last word; the one of the request is a suggestion.</summary>
public sealed record SignupCompleteRequest(string Token, string Password, string CompanyName);
```

with

```csharp
/// <summary>
/// Request of <c>POST /auth/signup/complete</c>. The company name is the person's last word; the one of the request is a suggestion. The
/// password is chosen here, except by an account that has no company (the preview says <c>has_account</c>): it may be left out, and when it
/// is sent it is not used.
/// </summary>
public sealed record SignupCompleteRequest(string Token, string CompanyName, string? Password = null);
```

(Check the exact text of the two summaries in the file and replace those lines, keeping the file's other records.)

- [ ] **Step 8: The service.** Replace `src/Auth.Server/Signup/SignupService.cs` as a whole:

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
/// What the completion screen shows before it asks for anything: the address as it was typed, the company name of the request, and whether
/// the address has an account without a company (<see cref="SignupAccount.MayStartCompany"/>), which the link will use as it is.
/// </summary>
public sealed record SignupPreview(string Email, string CompanyName, bool HasAccount);

/// <summary>
/// Previews and completes a sign-up by its link token (spec 0009). Completing is how a visitor becomes the first admin of a company of their
/// own, and it is one transaction: it uses the pending sign-up up, makes the account (email confirmed) — or, for an account that has no company
/// (Amendment 3), takes that account as it is and confirms its email — makes the company with a copy of the default roles and the membership with
/// the first admin's role, and writes the audit rows, and leaves the transaction open for the sign-in (Amendment 2) — or, when anything is refused,
/// changes nothing at all, the pending sign-up included (except that an account found under the address deletes it).
/// </summary>
public sealed class SignupService(
    AuthDbContext db, UserManager<ApplicationUser> users, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations,
    CompanyService companies, ManifestHolder manifest, TimeProvider clock, AuditLog audit)
{
    /// <summary>
    /// The address, the company name and <c>HasAccount</c> of a usable link; the token stays usable. Unknown, used, expired and replaced tokens
    /// are all <see langword="null"/>. It changes nothing.
    /// </summary>
    public async Task<SignupPreview?> PreviewAsync(string token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var pending = await PendingSignups.FindUsableAsync(db, EmailTokens.HashOf(token), StorableTime.Now(clock), cancellationToken);
        if (pending is null)
        {
            return null;
        }

        var account = await SignupAccounts.FindAsync(db, pending.NormalizedEmail, cancellationToken);
        return new SignupPreview(pending.Email, pending.CompanyName, account?.MayStartCompany(pending.Email) == true);
    }

    /// <summary>
    /// Completes the sign-up as far as the database goes. Refused with <c>invalid_token</c>, with <c>invalid_request</c> (no password, and no
    /// account under the address either; the token stays usable), with <c>weak_password</c> (the broken rules are
    /// <see cref="SignupCompletion.BrokenRules"/>; the token stays usable) or with <c>already_registered</c> (an account that the link may not
    /// use has the address by now: the pending sign-up is deleted). The checks are made in that order: the token, the password, the account.
    /// The caller has checked <paramref name="companyName"/> against the name rule and <paramref name="password"/>, when there is one, for NUL.
    /// An account under the address that belongs to no company, and whose address is the typed one (<see cref="SignupAccount.MayStartCompany"/>),
    /// is used as it is: the password is neither required nor checked nor set, the email is confirmed, and whatever sessions it still has end as a new account's
    /// do. Every other address chooses a password here.
    /// On success nothing is committed: the account, the company with its roles, the membership and the audit rows are saved inside a transaction
    /// that is handed to the caller in <see cref="SignupCompletion.Transaction"/>, because the completion signs the person in (spec 0009,
    /// Amendment 2) and the sign-in is part of it: if it cannot be written, nothing of the completion stays. Every refusal and every exception
    /// leaves the transaction rolled back (except that an account found under the address deletes the pending sign-up).
    /// </summary>
    public async Task<SignupCompletion> CompleteAsync(string token, string? password, string companyName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(companyName);

        var hash = EmailTokens.HashOf(token);
        var now = StorableTime.Now(clock);

        // Leaving this method without handing the transaction over rolls everything back, the use of the token included.
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var handedOver = false;
        try
        {
            // Which address is this link for? Read first, because the address is what is locked.
            var address = await db.PendingSignups.AsNoTracking()
                .Where(p => p.TokenHash == hash && p.TokenExpiresAt > now)
                .Select(p => p.NormalizedEmail)
                .FirstOrDefaultAsync(cancellationToken);
            if (address is null)
            {
                return Refused(TenancyErrors.InvalidToken);
            }

            // One address, one account-maker at a time: the lock that invitation acceptance takes, so that whichever of a completion and an
            // acceptance comes second sees the membership or the account the first one made and answers `409`.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({address}, 0))", cancellationToken);

            // The token is used up by one statement: of parallel completions with one token one gets the row, and the others, which waited
            // for the lock, find nothing.
            var pending = await PendingSignups.ConsumeAsync(db, hash, now, cancellationToken);
            if (pending is null)
            {
                return Refused(TenancyErrors.InvalidToken);
            }

            // Any account under the address, whatever its spelling or state (the lookup is by the normalised address), read under the lock: a
            // membership an invitation gave it a moment ago is seen.
            var found = await SignupAccounts.FindAsync(db, pending.NormalizedEmail, cancellationToken);
            var usable = found is not null && found.MayStartCompany(pending.Email) ? found : null;

            ApplicationUser account;
            if (usable is null)
            {
                account = new ApplicationUser { UserName = pending.Email, Email = pending.Email, EmailConfirmed = true };

                // A password that is there is checked first (the order is the token, the password, the account).
                if (password is not null)
                {
                    var broken = await PasswordRules.BrokenAsync(users, account, password);
                    if (broken.Count > 0)
                    {
                        return new SignupCompletion(TenancyErrors.WeakPassword, broken);
                    }
                }

                if (found is not null)
                {
                    // An account the link may not use, a company it joined since the mail included. The answer does not depend on whether a
                    // password came: a screen that was told "has_account" sent none. Committed, not rolled back: the pending sign-up is gone,
                    // as the contract says.
                    await transaction.CommitAsync(CancellationToken.None);
                    return Refused(TenancyErrors.AlreadyRegistered);
                }

                if (password is null)
                {
                    return Refused(TenancyErrors.InvalidRequest);   // a new address chooses a password; nothing was changed for good
                }

                // The link came through the mailbox of the address: that proves it, and it is why the email is confirmed and a password is set here.
                AccountSessions.Ensure(await users.CreateAsync(account, password), "Could not set up the account");
            }
            else
            {
                // An account without a company comes back (Amendment 3): it keeps its password and its linked sign-ins, whatever was sent.
                account = await db.Users.SingleAsync(u => u.Id == usable.Id, cancellationToken);
                account.EmailConfirmed = true;
                AccountSessions.Ensure(await users.UpdateAsync(account), "Could not confirm the account");
            }

            // A new account has no sessions or links yet, but its address may have a login streak: streaks do not need an account. An account
            // that comes back has normally no session left (removing a member and deleting a company revoke them), but whatever is left ends
            // here, as it does at an invitation, so that nothing of the lost company refreshes into the new one: defence in depth.
            await AccountSessions.EndAllAsync(db, tokens, authorizations, account, cancellationToken);

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

            // The person who holds the link is anonymous: no actor. Who, where and what company; never the link or the password. Which path was
            // taken: `existing_account` is there, and true, only for an account that existed.
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.SignupCompleted,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = created.Value,
                OrgName = companyName,
                Details = usable is null ? null : new Dictionary<string, object?> { ["existing_account"] = true },
            });
            await db.SaveChangesAsync(CancellationToken.None);

            // Not committed: the sign-in is written next, inside this transaction, by whoever receives it, and it commits.
            handedOver = true;
            return new SignupCompletion(Account: account, CompanyId: created.Value, Transaction: transaction);
        }
        finally
        {
            if (!handedOver)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static SignupCompletion Refused(string error) => new(error);
}
```

- [ ] **Step 9: The preview endpoint and the completion endpoint.** In `src/Auth.Server/Signup/SignupPreviewEndpoint.cs` replace

```csharp
            : ApiResults.Ok(new SignupPreviewResponse(preview.Email, preview.CompanyName));
```

with

```csharp
            : ApiResults.Ok(new SignupPreviewResponse(preview.Email, preview.CompanyName, preview.HasAccount));
```

Replace `src/Auth.Server/Signup/SignupCompleteEndpoint.cs` as a whole:

```csharp
using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Login;
using Auth.Server.Requests;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.Extensions.Options;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Signup;

/// <summary>
/// <c>POST /auth/signup/complete</c> (spec 0009): finishes a sign-up and signs the person in (Amendment 2). No access token: the link token is the
/// credential. While sign-up is off it answers <c>403 signup_disabled</c> before it reads the body — also for a request that was made while it
/// was on. The answer is that of a successful login (<c>200 {"status":"authenticated","access_token"}</c> and the refresh cookie): the endpoint is a
/// pass-through request of OpenIddict (<see cref="SignupCompleteRequestHandler"/>), builds the principal the way the login does
/// (<see cref="SessionPrincipal"/>) and returns an <see cref="AtomicSignInResult"/> that issues the session, writes <c>login.succeeded</c> and
/// commits the transaction of the completion. If any of that fails, nothing of the completion stays. The password is an optional member of the
/// body (Amendment 3): an account without a company needs none, and a password that is sent for it is not used.
/// </summary>
public static class SignupCompleteEndpoint
{
    public const string Path = "/auth/signup/complete";

    private static readonly string[] Required = ["token", "company_name"];
    private static readonly string[] Optional = ["password"];

    public static async Task<IResult> HandleAsync(
        HttpContext http, SignupGate gate, SignupService signup, MembershipReader memberships, IOptions<TokenOptions> tokens, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(signup);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);

        // Without it OpenIddict cannot sign in here (it would say so only after the account was made, and roll that back): fail at once.
        if (http.GetOpenIddictServerRequest() is null)
        {
            throw new InvalidOperationException(
                "The sign-up completion was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        }

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

        var done = await signup.CompleteAsync(linkToken, password, name, http.RequestAborted);
        if (!done.Succeeded)
        {
            return done.Error switch
            {
                TenancyErrors.WeakPassword => AccountResults.WeakPassword(done.BrokenRules!),
                TenancyErrors.InvalidRequest => AccountResults.InvalidRequest(),
                _ => ApiResults.Error(done.Error!),
            };
        }

        try
        {
            // Read inside the transaction of the completion (the same context): it sees the membership that is not committed yet.
            var account = done.Account!;
            var tenant = await memberships.ReadAsync(account.Id, CancellationToken.None)
                ?? throw new InvalidOperationException("The account that was just made has no company.");
            if (tenant.CompanyId != done.CompanyId)
            {
                throw new InvalidOperationException("The membership read is not the company the completion made.");
            }

            var principal = SessionPrincipal.Create(http, account, tenant, tokens.Value.Audience, clock);
            return new AtomicSignInResult(
                principal,
                new AuditEntry
                {
                    Kind = AuditKinds.LoginSucceeded,
                    ActorUserId = account.Id,
                    SubjectUserId = account.Id,
                    SubjectEmail = account.Email,
                    OrgId = tenant.CompanyId,
                    OrgName = tenant.CompanyName,
                },
                done.Transaction);
        }
        catch
        {
            // The result would have owned the transaction; it does not exist, so this is the end of the completion: roll it back.
            await done.Transaction!.DisposeAsync();
            throw;
        }
    }
}
```

(The body from `try` to the end is the file's own; copy anything in the file that differs from the above, such as a changed `using`, and change only the `Fields`, the reading, the call and the refusal switch.)

- [ ] **Step 10: The dispatcher.** In `src/Auth.Server/Email/MailDispatcher.cs`:

(a) In the class summary, replace the last two lines ("A sign-up request (spec 0009) names a pending sign-up, not an account, and is decided here: ... with neither there is nothing to send.") with

```csharp
/// A sign-up request (spec 0009) names a pending sign-up, not an account, and is decided here: an address whose account belongs to a company gets
/// the mail that says so (no link) even when its pending sign-up is gone; otherwise a live pending sign-up gets a new link — also when the address
/// has an account without a company (Amendment 3), unless that account only looks like the typed address; with neither there is nothing to send.
```

(b) In `DropUnmailableAsync` replace its summary's last sentence and the account sub-query of the sign-up branch. The summary becomes: "Removes the due rows whose address has no account, or (for a verification) whose account is confirmed already; a sign-up row is removed only when its address has neither an account that belongs to a company nor a live (unexpired) pending sign-up. Those requests were answered with 202 like any other, and end here." In the SQL replace

```sql
                        AND NOT EXISTS (
                            SELECT 1 FROM "AspNetUsers" a
                            WHERE a."NormalizedEmail" = r."NormalizedEmail" AND a."Email" IS NOT NULL))
```

with

```sql
                        AND NOT EXISTS (
                            SELECT 1 FROM "AspNetUsers" a
                            WHERE a."NormalizedEmail" = r."NormalizedEmail" AND a."Email" IS NOT NULL
                              AND EXISTS (SELECT 1 FROM "Memberships" m WHERE m."UserId" = a."Id")))
```

(c) In `DispatchOneAsync`, in the `due` statement replace

```sql
                                     OR EXISTS (
                                         SELECT 1 FROM "AspNetUsers" a
                                         WHERE a."NormalizedEmail" = r."NormalizedEmail" AND a."Email" IS NOT NULL)))
```

with

```sql
                                     OR EXISTS (
                                         SELECT 1 FROM "AspNetUsers" a
                                         WHERE a."NormalizedEmail" = r."NormalizedEmail" AND a."Email" IS NOT NULL
                                           AND EXISTS (SELECT 1 FROM "Memberships" m WHERE m."UserId" = a."Id"))))
```

and in the comment above it change "for a sign-up, an account or a live pending sign-up" to "for a sign-up, an account that belongs to a company or a live pending sign-up".

(d) Replace the method `PrepareSignupMailAsync` and the method `StoredAddressOfAsync` (both, including their summaries) with:

```csharp
    /// <summary>
    /// The mail of a sign-up (spec 0009): decided here, when it is sent, and never by the request. The account check comes first: with an account
    /// under the address that belongs to a company the mail says so and holds no link, even when the pending sign-up is gone, and any pending row
    /// of the address is deleted — in the transaction of the send, so a failed send keeps it. Otherwise (no account, or an account without a
    /// company: Amendment 3) the live pending sign-up (which plays the part the invitation plays for an invitation mail) is locked, so that a
    /// completion waits for the mail, the account is read again once the lock is held, and the row gets a new link token (the earlier link stops working); the mail goes to the address as typed. An
    /// account that merely looks like the typed address (<see cref="SignupAccount.MayStartCompany"/>) is not one a link may start a company
    /// for: it gets the notice. With no live pending sign-up and no account that belongs to a company there is nothing to send.
    /// </summary>
    private async Task<Func<Task<ComposedMail>>?> PrepareSignupMailAsync(
        AuthDbContext db, MailRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var account = await SignupAccounts.FindAsync(db, request.NormalizedEmail, cancellationToken);
        if (account is { BelongsToCompany: true })
        {
            return AccountExistsMail(db, request.NormalizedEmail, account.Email, cancellationToken);
        }

        var pending = await PendingSignups.LockUsableAsync(db, request.NormalizedEmail, now, cancellationToken);

        // The lock may have waited, for a completion that has just committed or for an invitation that has just been accepted: the account is
        // read again, whether or not the pending sign-up survived.
        account = await SignupAccounts.FindAsync(db, request.NormalizedEmail, cancellationToken);
        if (account is { BelongsToCompany: true })
        {
            return AccountExistsMail(db, request.NormalizedEmail, account.Email, cancellationToken);
        }

        if (pending is null)
        {
            return null;
        }

        if (account is not null && !account.MayStartCompany(pending.Email))
        {
            return AccountExistsMail(db, request.NormalizedEmail, account.Email, cancellationToken);
        }

        return async () =>
        {
            var token = await PendingSignups.IssueAsync(db, pending.NormalizedEmail, now, cancellationToken);
            return composer.Compose(MailKind.Signup, pending.Email, token, pending.CompanyName);
        };
    }

    private Func<Task<ComposedMail>> AccountExistsMail(AuthDbContext db, string normalizedEmail, string storedAddress, CancellationToken cancellationToken) =>
        async () =>
        {
            await PendingSignups.DeleteAsync(db, normalizedEmail, cancellationToken);
            return composer.ComposeAccountExists(storedAddress);
        };
```

(`MailDispatcher` needs `using Auth.Server.Signup;` — it has it already for `PendingSignups`.)

- [ ] **Step 11: The operation descriptions.** In `src/Auth.Server/Api/SignupRoutes.cs` replace the description of the request (`POST /auth/signup`; lines 27 to 29 at the base) with

```csharp
            .WithDescription(
                "Asks to start a company. The answer is the same `202` for every address; a mail follows: a notice without a link when the address "
                + "has an account that belongs to a company, otherwise a link to finish (also when the account belongs to no company). Off unless the "
                + "manifest says `signup: self-service` and the completion address is configured (`403 signup_disabled`). One request a minute and "
                + "five an hour per address.")
```

replace the description of the preview with

```csharp
            .WithDescription(
                "Shows what the link of the mail is for: the address as it was typed, the company name of the request and `has_account`, which is true "
                + "only when an account without a company has the address (then the completion needs no password). It changes nothing "
                + "and the link stays usable. A token that is unknown, used, expired or replaced by a newer mail is `invalid_token`.")
```

and the description of the completion with

```csharp
            .WithDescription(
                "Finishes a sign-up and signs the person in: in one transaction the account (its email confirmed), the company (named as given here, "
                + "with a copy of the default roles) and the membership with the first admin's role are made, the pending sign-up is used up, and the "
                + "session is issued; if the session cannot be written nothing of this stays. The answer is that of a successful login: the access token "
                + "(carrying the new company and the first admin's role) and the refresh cookie. The checks are made in this order: the token, the "
                + "password (a weak one leaves the token usable; an account that has no company keeps the password it has, so for it the member may be "
                + "left out and is not used if sent), the account (`409 already_registered` when an account that belongs to a company has the address by now).")
```

- [ ] **Step 12: Run the classes.** Build as in Step 4, then run, with `-maxThreads 2`: `-class Auth.IntegrationTests.SignupComebackTests -class Auth.IntegrationTests.SignupComebackDispatchTests -class Auth.IntegrationTests.SignupCompleteTests -class Auth.IntegrationTests.SignupCompleteSignInTests -class Auth.IntegrationTests.SignupCompleteRaceTests -class Auth.IntegrationTests.SignupDispatchTests -class Auth.IntegrationTests.SignupDispatchPolishTests -class Auth.IntegrationTests.SignupPreviewTests -class Auth.IntegrationTests.SignupRequestTests -class Auth.IntegrationTests.SignupFirstAdminTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.MailDispatcherTests -class Auth.IntegrationTests.InvitationDispatchTests -class Auth.IntegrationTests.InviteAcceptTests` (`SignupDispatchPolishTests` holds the edited `The_mails_are_in_the_configured_language`; the last three are the other users of the dispatcher and of the lock; nothing in them was edited, and they must still pass). All green. A failure of the look-alike tests on their first assertion (the two spellings do not normalise alike on this host) means the pair is wrong for the host: take another pair from `InviteAcceptTests.An_invitation_for_another_spelling_of_an_existing_account_cannot_take_it_over` (the third row uses the theta symbol) in both test files. Do not run the rest of the suite.

- [ ] **Step 13: The live script's expectation.** In `scripts/e2e-signup.sh`, step 5, change the two expected bodies

```bash
expect_status "step 5: preview" 200 "{\"email\":\"$NEW_EMAIL\",\"company_name\":\"$COMPANY\"}"
```

and

```bash
expect_status "step 5: preview again" 200 "{\"email\":\"$NEW_EMAIL\",\"company_name\":\"$COMPANY\"}"
```

to carry the third member:

```bash
expect_status "step 5: preview" 200 "{\"email\":\"$NEW_EMAIL\",\"company_name\":\"$COMPANY\",\"has_account\":false}"
```

and

```bash
expect_status "step 5: preview again" 200 "{\"email\":\"$NEW_EMAIL\",\"company_name\":\"$COMPANY\",\"has_account\":false}"
```

and the header line and the `pass` text of step 5 from "exactly the typed address and the company name" to "exactly the typed address, the company name and has_account false". Check it with `bash -n scripts/e2e-signup.sh`; do not run it.

- [ ] **Step 14: Check and hand back.** `grep -rInP '\\u[0-9a-fA-F]{4}' src tests/Auth.IntegrationTests/*.cs tests/Auth.IntegrationTests/Infrastructure scripts --include=*.cs --include=*.sh` finds nothing that is not meant (the new tests build their characters with `char.ConvertFromUtf32` and `Backslash`). **Hand back** — uncommitted. Files: everything listed under Files. Proposed note for the verifiers: the live check of the comeback is Step 12 of `scripts/e2e-signup.sh` (Task 17); `OpenApiTests` and `SignupPreviewTests` gained the member `has_account`. Proposed subject: `feat(server): a person without a company comes back by sign-up`.

---
### Task 17: A person without a company comes back by sign-up: the sample, the Playwright test and `scripts/e2e-signup.sh`

**Files:**
- Modify: `samples/notes-web/src/app/account-api.ts`, `samples/notes-web/src/app/auth/auth.service.ts`, `samples/notes-web/src/app/texts.ts`, `samples/notes-web/src/app/pages/complete-signup.ts`
- Modify (tests): `samples/notes-web/src/app/account-api.signup.spec.ts`, `samples/notes-web/src/app/auth/auth.service.signup.spec.ts`, `samples/notes-web/src/app/pages/complete-signup.spec.ts`, `samples/notes-web/e2e/09-signup.spec.ts`
- Modify: `scripts/e2e-signup.sh` (Step 12 and its header), `scripts/e2e-web.sh` (the seeding of a returning person)

**Interfaces:**
- Consumes: Task 16's wire (`POST /auth/signup/preview` answers `has_account`; `POST /auth/signup/complete` takes `{"token","company_name"}` and an optional `"password"`; the audit detail `existing_account`); Tasks 10, 11, 12 and 15's `AccountApi`, `AuthService.completeSignup`, `CompleteSignupPage`, `texts`, the helpers of `samples/notes-web/src/testing/helpers.ts` and `e2e/support/*`; `scripts/e2e-signup.sh`'s helpers (`call`, `expect_*`, `login`, `wait_mail`, `token_body`, `cli`, `psql_value`, `mail_count`, `set_cookie_line`, `has_attr`, `jwt_payload`, `val`).
- Produces: `SignupPreview.has_account: boolean`; `AuthService.completeSignup(token: string, password: string | null, companyName: string)`; the text `texts.completeSignup.keepsPassword(email)`; the test id `keeps-password`; the setting `E2E_COMEBACK_EMAIL` of the Playwright run; Step 12 of `scripts/e2e-signup.sh`.

- [ ] **Step 1: Write the failing unit tests.**

(a) `samples/notes-web/src/app/account-api.signup.spec.ts`: replace the test `reads the answer to a preview: the address and the company name`

```ts
  it('reads the answer to a preview: the address and the company name', async () => {
    const done = api.previewSignup('tok');
    const request = ctrl.expectOne('/auth/signup/preview');
    expect(request.request.body).toEqual({ token: 'tok' });
    request.flush({ email: 'anna@firma.example', company_name: 'Firma' });
    expect(await done).toEqual({ ok: true, value: { email: 'anna@firma.example', company_name: 'Firma' } });
  });
```

with

```ts
  it('reads the answer to a preview: the address, the company name and whether the account exists', async () => {
    const done = api.previewSignup('tok');
    const request = ctrl.expectOne('/auth/signup/preview');
    expect(request.request.body).toEqual({ token: 'tok' });
    request.flush({ email: 'anna@firma.example', company_name: 'Firma', has_account: false });
    expect(await done).toEqual({ ok: true, value: { email: 'anna@firma.example', company_name: 'Firma', has_account: false } });
  });

  it('reads has_account true: the person has an account and no company', async () => {
    const done = api.previewSignup('tok');
    ctrl.expectOne('/auth/signup/preview').flush({ email: 'anna@firma.example', company_name: 'Firma', has_account: true });
    expect(await done).toEqual({ ok: true, value: { email: 'anna@firma.example', company_name: 'Firma', has_account: true } });
  });

  it.each([[{}], [{ has_account: 'true' }], [{ has_account: 1 }], [{ has_account: null }]])(
    'an answer %j means has_account false: a password is asked for, which is the safe side',
    async (extra) => {
      const done = api.previewSignup('tok');
      ctrl.expectOne('/auth/signup/preview').flush({ email: 'anna@firma.example', company_name: 'Firma', ...extra });
      expect(await done).toEqual({ ok: true, value: { email: 'anna@firma.example', company_name: 'Firma', has_account: false } });
    },
  );
```

(b) `samples/notes-web/src/app/auth/auth.service.signup.spec.ts`: after the first test of the file (`posts the token, the password and the company name, keeps the token in memory and loads /auth/me`) add

```ts
  it('leaves the password out of the body when there is none to send (an account that exists, without a company)', async () => {
    const done = auth.completeSignup('tok', null, 'Firma');
    const request = ctrl.expectOne('/auth/signup/complete');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok', company_name: 'Firma' });
    expect('password' in (request.request.body as object)).toBe(false);
    request.flush(signedIn);
    await settle();
    ctrl.expectOne('/auth/me').flush(adminMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('tok-c');
    ctrl.verify();
  });
```

(c) `samples/notes-web/src/app/pages/complete-signup.spec.ts`: replace the line

```ts
const preview = { email: 'anna@firma.example', company_name: 'Firma' };
```

with

```ts
const preview = { email: 'anna@firma.example', company_name: 'Firma', has_account: false };
/** What the preview says of a person whose company is gone: the account exists and belongs to none (spec 0009, Amendment 3). */
const returning = { ...preview, has_account: true };
```

replace the head of `open`

```ts
async function open(query = '?token=tok-s', previewAnswer: 'ok' | 'wait' = 'ok') {
```

with

```ts
async function open(query = '?token=tok-s', previewAnswer: 'ok' | 'wait' = 'ok', answer: typeof preview = preview) {
```

and, in the same function, `ctrl.expectOne('/auth/signup/preview').flush(preview);` with `ctrl.expectOne('/auth/signup/preview').flush(answer);`. Then append at the end of the file:

```ts
describe('CompleteSignupPage for an account that exists without a company (has_account)', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('shows the address, the company name and the sentence that the password stays; no password fields', async () => {
    const { fixture } = await open('?token=tok-s', 'ok', returning);
    expect(valueOf(fixture, '#email')).toBe('anna@firma.example');
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#email')?.readOnly).toBe(true);
    expect(valueOf(fixture, '#company')).toBe('Firma');
    expect(has(fixture, '[data-testid="keeps-password"]')).toBe(true);
    expect(pageText(fixture)).toContain('You already have an account as anna@firma.example. Its password stays as it is');
    expect(pageText(fixture)).not.toContain('This sets the password');
    expect(has(fixture, '#password')).toBe(false);
    expect(has(fixture, '#repeat')).toBe(false);
  });

  it('a person with no account still sees the password fields and not the sentence that it stays', async () => {
    const { fixture } = await open();
    expect(has(fixture, '#password')).toBe(true);
    expect(has(fixture, '[data-testid="keeps-password"]')).toBe(false);
  });

  it('200: posts only the token and the company name (trimmed), signs the person in and opens the notes screen in place of this one', async () => {
    const opened = await open('?token=tok-s', 'ok', returning);
    typeInto(opened.fixture, '#company', '  Firma Again  ');
    submitForm(opened.fixture);
    const request = opened.ctrl.expectOne('/auth/signup/complete');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-s', company_name: 'Firma Again' });
    expect('password' in (request.request.body as object)).toBe(false);
    await signInBy(opened, request);
    expect(TestBed.inject(AuthService).token()).toBe('tok-c');
    expect(opened.navigateByUrl).toHaveBeenCalledTimes(1);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/notes', { replaceUrl: true });
    expect(window.location.href).not.toContain('anna@firma.example');
  });

  it('a blank company name: a message, and no request', async () => {
    const opened = await open('?token=tok-s', 'ok', returning);
    typeRaw(opened.fixture, '#company', '   ');
    submitForm(opened.fixture);
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Enter the name of your company.');
    opened.ctrl.expectNone('/auth/signup/complete');
  });

  it('409 already_registered (it joined a company meanwhile): says the account exists and links to the sign-in; no form', async () => {
    const opened = await open('?token=tok-s', 'ok', returning);
    submitForm(opened.fixture);
    opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'already_registered' }, status(409));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('An account with this address already exists.');
    expect(has(opened.fixture, 'a[href="/login"]')).toBe(true);
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it('invalid_token on complete: the link is used up, with a link to sign up again; no form', async () => {
    const opened = await open('?token=tok-s', 'ok', returning);
    submitForm(opened.fixture);
    opened.ctrl.expectOne('/auth/signup/complete').flush({ error: 'invalid_token' }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(invalid);
    expect(has(opened.fixture, 'a[href="/signup"][data-testid="signup-again"]')).toBe(true);
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it('sends one request for a double submit', async () => {
    const opened = await open('?token=tok-s', 'ok', returning);
    submitForm(opened.fixture);
    submitForm(opened.fixture);
    await signInBy(opened, opened.ctrl.expectOne('/auth/signup/complete'));
  });
});
```

- [ ] **Step 2: Run them and see them fail.** In `samples/notes-web` (run `npm ci` once if `node_modules` is missing): `npx ng test --watch=false --include "src/app/account-api.signup.spec.ts" --include "src/app/auth/auth.service.signup.spec.ts" --include "src/app/pages/complete-signup.spec.ts"`. The new tests fail (`has_account` is not read, `completeSignup` takes a string, the fields are always there); the tests that were there pass or fail only where they name the preview.

- [ ] **Step 3: The API call reads `has_account`.** In `samples/notes-web/src/app/account-api.ts` replace

```ts
/** What POST /auth/signup/preview answers: the address as it was typed and the company name of the request. */
export interface SignupPreview {
  email: string;
  company_name: string;
}
```

with

```ts
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

and, in `previewSignup`, replace

```ts
      return { ok: true, value: { email: body['email'], company_name: body['company_name'] } };
```

with

```ts
      // Anything but a true boolean means "no account": the screen then asks for a password, which is the safe side.
      return { ok: true, value: { email: body['email'], company_name: body['company_name'], has_account: body['has_account'] === true } };
```

- [ ] **Step 4: The session call can leave the password out.** In `samples/notes-web/src/app/auth/auth.service.ts` replace the doc comment and the method `completeSignup`

```ts
  /**
   * Finishes a self-service sign-up with the token of the mail link. Auth-Core answers as it answers a login (spec 0009, Amendment 2): the
   * access token in the body and the refresh cookie, so the person is signed in at once and the screen opens the product. A refusal leaves
   * the session that exists alone, as a failed login does.
   */
  async completeSignup(token: string, password: string, companyName: string): Promise<LoginResult> {
    let body: LoginBody | null;
    try {
      body = await firstValueFrom(
        this.http.post<LoginBody | null>('/auth/signup/complete', { token, password, company_name: companyName }),
      );
    } catch (error) {
```

with

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
    try {
      body = await firstValueFrom(this.http.post<LoginBody | null>('/auth/signup/complete', request));
    } catch (error) {
```

(The lines after the `catch (error) {` are the file's own, unchanged.)

- [ ] **Step 5: The text.** In `samples/notes-web/src/app/texts.ts`, in `completeSignup`, after the `setsPassword` line add

```ts
    // The account exists and belongs to no company (its company was deleted, or the person was removed from it): only the company is new.
    keepsPassword: (email: string): string => `You already have an account as ${email}. Its password stays as it is; only the company is new.`,
```

- [ ] **Step 6: The screen.** In `samples/notes-web/src/app/pages/complete-signup.ts`:

(a) In the template replace

```html
              <p class="status">{{ t.setsPassword(signup.email) }}</p>
              <label for="password">{{ common.newPassword }}</label>
              <input id="password" type="password" formControlName="password" autocomplete="new-password" />
              <label for="repeat">{{ common.repeatPassword }}</label>
              <input id="repeat" type="password" formControlName="repeat" autocomplete="new-password" />
              <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
```

with

```html
              @if (signup.has_account) {
                <p class="status" data-testid="keeps-password">{{ t.keepsPassword(signup.email) }}</p>
              } @else {
                <p class="status">{{ t.setsPassword(signup.email) }}</p>
                <label for="password">{{ common.newPassword }}</label>
                <input id="password" type="password" formControlName="password" autocomplete="new-password" />
                <label for="repeat">{{ common.repeatPassword }}</label>
                <input id="repeat" type="password" formControlName="repeat" autocomplete="new-password" />
              }
              <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
```

(b) In `submit()` replace

```ts
    if (password !== repeat) {
      this.error.set(this.common.passwordsDiffer);
      return;
    }
    this.busy.set(true);
    // The company name typed here is the one that counts (the name of the first request was only a suggestion); trimmed, because
    // Auth-Core refuses white space at either end.
    const result = await this.auth.completeSignup(this.token, password, company.trim());
```

with

```ts
    if (!signup.has_account && password !== repeat) {
      this.error.set(this.common.passwordsDiffer);
      return;
    }
    this.busy.set(true);
    // The company name typed here is the one that counts (the name of the first request was only a suggestion); trimmed, because
    // Auth-Core refuses white space at either end. An account that exists without a company has no password to choose: none is sent.
    const result = await this.auth.completeSignup(this.token, signup.has_account ? null : password, company.trim());
```

(c) In `preview()` replace

```ts
      this.pending.set(result.value);
      this.form.controls.company.setValue(result.value.company_name);
      this.phase.set('form');
```

with

```ts
      this.pending.set(result.value);
      this.form.controls.company.setValue(result.value.company_name);
      if (result.value.has_account) {
        // No password is chosen here: the two controls must not hold the form invalid.
        this.form.controls.password.disable();
        this.form.controls.repeat.disable();
      }
      this.phase.set('form');
```

- [ ] **Step 7: Run the unit tests and build.** The command of Step 2 passes, and `npx ng build` in `samples/notes-web` succeeds. `npx ng test --watch=false --include "src/app/texts.spec.ts"` (the texts are plain printable ASCII) passes.

- [ ] **Step 8: The Playwright test.** In `samples/notes-web/e2e/09-signup.spec.ts` add, after the existing imports,

```ts
import { signIn, signOut } from './support/session';
```

and append at the end of the file:

```ts
test('a person whose company is gone comes back by sign-up: only the company name is asked for, and the old password still works', async ({ page }) => {
  test.setTimeout(240_000);
  // The script gave this address an account (an invitation to the development company, accepted with E2E_USER_PASSWORD) and then removed it
  // from the company through the operator CLI: an account, and no company. The test asks for the sign-up itself.
  const email = setting('E2E_COMEBACK_EMAIL');
  const password = setting('E2E_USER_PASSWORD');
  const company = `Back ${unique('co')}`;

  await page.goto('/signup');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Company name').fill(`Asked ${unique('co')}`);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page.getByTestId('signup-sent')).toHaveText(SENT); // the sentence every address gets

  // The mail carries the usual link (not the notice), and the screen it opens asks for the company name only.
  const link = await waitForLink(email, 'complete-signup', MAIL_WAIT_MS);
  await openMailLink(page, link);
  await expect(page).toHaveURL(/\/complete-signup$/);
  expectCleanAddressBar(page);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  await expect(page.getByLabel('Email')).not.toBeEditable();
  await expect(page.getByTestId('keeps-password')).toBeVisible();
  await expect(page.getByLabel('New password')).toHaveCount(0);
  await expect(page.getByLabel('Repeat the password')).toHaveCount(0);

  await page.getByLabel('Company name').fill(company);
  await page.getByRole('button', { name: 'Create company' }).click();

  // Signed in at once, as the first admin of a company of their own; nothing of the former company.
  await expect(page).toHaveURL(/\/notes$/);
  expectNoEmailInAddressBar(page);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-company')).toHaveText(company);
  await expect(page.getByTestId('me-role')).toHaveText('admin');
  await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toHaveCount(0);
  await expect(page.getByText('No notes yet.')).toBeVisible();

  // The password did not change: signing out and in again with the one the account always had lands in the new company.
  await signOut(page);
  await signIn(page, email, password);
  await expect(page.getByTestId('me-company')).toHaveText(company);
  await expect(page.getByTestId('me-role')).toHaveText('admin');
});
```

The third test of the file (an account made between the request and the link) is unchanged and still right: `BOTH` has a company, so the preview says `has_account` `false` and the screen still shows the password fields before the `409`.

- [ ] **Step 9: Seed the returning person in `scripts/e2e-web.sh`.**

(a) In the header comment, after the paragraph that ends "(the database, because the test cannot move the server's clock).", add:

```bash
# It also prepares COMEBACK (Amendment 3 of spec 0009): invited to the development company, the invitation accepted by the API once the mail has
# come, and the member then removed through the operator CLI (`remove-member`): an account with a password and no company. The test asks for the
# sign-up and finishes it with a company name only.
```

(b) After the function `wait_mails`, add:

```bash
mail_token() { # mail_token <address> <subject start> <path>: the token in the link <path>?token= of the newest such mail to the address (printed; the callers keep it in a variable and never show it)
  MAIL_TO="$1" MAIL_SUBJECT="$2" MAIL_PATH="$3" MAILPIT="$MAILPIT_URL" node -e '
const base = process.env.MAILPIT;
const to = process.env.MAIL_TO.toLowerCase();
(async () => {
  const found = await (await fetch(`${base}/api/v1/search?query=${encodeURIComponent("to:" + to)}`)).json();
  const hit = found.messages.find((m) => m.Subject.startsWith(process.env.MAIL_SUBJECT) && (m.To ?? []).some((r) => r.Address.toLowerCase() === to));
  if (!hit) process.exit(1);
  const mail = await (await fetch(`${base}/api/v1/message/${hit.ID}`)).json();
  const match = new RegExp(`/${process.env.MAIL_PATH}\\?token=([A-Za-z0-9_-]{43})`).exec(mail.Text);
  if (!match) process.exit(1);
  process.stdout.write(match[1]);
})().catch(() => process.exit(1));
'
}
```

(c) In `seed_stack`, after `EXPIRED_EMAIL="expired-$run@e2e.test"` add `COMEBACK_EMAIL="comeback-$run@e2e.test"`; change the comment and the invitations to

```bash
  # Four invitations through the operator CLI (BOTH and COMEBACK are for the sign-up tests of spec 0009). The server sends the mails at its next poll.
  cli invite --org "$org" --email "$VIEWER_EMAIL" --role viewer
  cli invite --org "$org" --email "$RESETTER_EMAIL" --role user
  cli invite --org "$org" --email "$BOTH_EMAIL" --role user
  cli invite --org "$org" --email "$COMEBACK_EMAIL" --role user
```

and, after the lines that check `[[ "$updated" == "UPDATE 1" ]] || fail "seeding: the sign-up link was not made to expire"` and before `rm -f "$tmp/expired.json"`, add:

```bash
  # A person whose company is gone: accept the invitation by the API (the password is USER_PASSWORD), then remove the member through the CLI.
  wait_mails "$COMEBACK_EMAIL" 1 150 || fail "seeding: no invitation mail for the returning person within 150s"
  token="$(mail_token "$COMEBACK_EMAIL" "You are invited" invite)" || fail "seeding: the invitation mail of the returning person holds no link"
  E2E_X_TOKEN="$token" E2E_X_PASSWORD="$USER_PASSWORD" node -e 'console.log(JSON.stringify({token: process.env.E2E_X_TOKEN, password: process.env.E2E_X_PASSWORD}))' > "$tmp/accept.json"
  token=""
  code="$(curl -sS --max-time 20 -o /dev/null -w '%{http_code}' -X POST "$HTTP_URL/auth/invites/accept" \
    -H 'Content-Type: application/json' --data-binary "@$tmp/accept.json")" || fail "seeding: accepting the invitation of the returning person failed"
  rm -f "$tmp/accept.json"
  [[ "$code" == "204" ]] || fail "seeding: accepting the invitation of the returning person answered $code, not 204"
  cli remove-member --org "$org" --email "$COMEBACK_EMAIL"
```

(d) In the `export` lines of the project loop change `export E2E_BOTH_EMAIL="$BOTH_EMAIL" E2E_EXPIRED_EMAIL="$EXPIRED_EMAIL"` to `export E2E_BOTH_EMAIL="$BOTH_EMAIL" E2E_EXPIRED_EMAIL="$EXPIRED_EMAIL" E2E_COMEBACK_EMAIL="$COMEBACK_EMAIL"`.

- [ ] **Step 10: Step 12 of `scripts/e2e-signup.sh`.**

(a) In the header, after item 11 (the one that ends "(the 24 hours themselves, and the hourly clean-up, are guarded by the tests).") add

```bash
#  12. INVITED, a member of the development company after step 10, is removed from it through the operator CLI (`remove-member`): an account
#      and no company, whose password still logs in as the 403 no_membership. INVITED asks for a sign-up again (waiting out the mail limit if
#      step 10 was less than a minute ago), the mail is the usual "complete your sign-up" with a new link, the preview says has_account true,
#      and the completion with only the token and a company name is 200 as a login's (the cookie, roles ["admin"], a company that is neither
#      the development company's nor NEW's); the password hash is as it was, the old password logs in to the new company, INVITED has
#      one account, the pending sign-up is gone, and the audit log holds signup.completed with details.existing_account true (NEW's row has no
#      details) - criterion 24.
```

and change "Takes about five minutes: one wait of 62 seconds for the mail limit, and up to a minute for the server to pick up the invitation the CLI queued." to "Takes about six minutes: one wait of 62 seconds for the mail limit (a second one in step 12 when step 10 was less than a minute before), and up to a minute for the server to pick up the invitation the CLI queued."

(b) After the line `export E2E_COMPANY="$COMPANY" E2E_RENAMED="$RENAMED"` add

```bash
export E2E_BACK_ASKED="Back Asked $run" E2E_BACK_COMPANY="Back Company $run"
```

(c) Before the final `echo "ALL PASS"` add:

```bash
# --- Step 12: a person without a company comes back by sign-up (criterion 24) ------------------------------------------
# After step 10 INVITED is a member of the development company with the password E2E_INVITED_PASSWORD. The operator removes the membership:
# an account and no company, as after the company was deleted. INVITED asks for a sign-up again, gets the usual link, and finishes with
# only a company name.
login invited E2E_INVITED_EMAIL E2E_INVITED_PASSWORD
BODY="$(jwt_payload "$tmp/invited.auth")"
INVITED_SUB="$(val 'd["sub"]')"
HASH_BEFORE="$(psql_value "SELECT md5(\"PasswordHash\") FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = upper('$INVITED_EMAIL')")"
[[ -n "$HASH_BEFORE" ]] || fail "step 12: INVITED has no password hash"
cli remove-member --org "$DEV_ORG" --email "$INVITED_EMAIL"
[[ "$CLI_EXIT" == "0" ]] || fail "step 12: the operator CLI failed: admin remove-member"
json_body "$tmp/invited.login" "{'email': os.environ['E2E_INVITED_EMAIL'], 'password': os.environ['E2E_INVITED_PASSWORD']}"
call POST /auth/login "$tmp/invited.login"
expect_error "step 12: INVITED has an account and no company" 403 no_membership

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
expect_status "step 12: the sign-up request of INVITED, who has an account and no company" 202 ""
for _ in $(seq 1 90); do
  [[ "$(mail_count "$INVITED_EMAIL")" -gt "$MAILS_BEFORE" ]] && break
  sleep 1
done
[[ "$(mail_count "$INVITED_EMAIL")" -gt "$MAILS_BEFORE" ]] || fail "step 12: no new mail for INVITED within 90s"
wait_mail "$INVITED_EMAIL" "Complete your sign-up" 5 || fail "step 12: INVITED did not get a sign-up mail (the newest mail is another)"
token_body http://localhost:4200/complete-signup "$tmp/back-preview.json" || fail "step 12: no usable sign-up link in the mail"
token_body http://localhost:4200/complete-signup "$tmp/back-complete.json" "" E2E_BACK_COMPANY || fail "step 12: the completion body could not be made"
NEW_LINK_TOKEN="$("$PY" -c 'import json,sys; print(json.load(sys.stdin)["token"])' < "$tmp/back-preview.json" | tr -d '\r')"
[[ "$NEW_LINK_TOKEN" != "$OLD_LINK_TOKEN" ]] || fail "step 12: the newest sign-up mail is the one of step 10, not a new link"

call POST /auth/signup/preview "$tmp/back-preview.json"
expect_status "step 12: preview" 200 "{\"email\":\"$INVITED_EMAIL\",\"company_name\":\"$E2E_BACK_ASKED\",\"has_account\":true}"
call POST /auth/signup/complete "$tmp/back-complete.json"   # no password in the body
expect_status "step 12: complete with only the token and a company name" 200
expect_no_store "step 12: the 200"
expect_eq "step 12: the body is a login's" "$(val 'sorted(d.keys())')" "['access_token', 'status']"
LINE="$(set_cookie_line)"
[[ -n "$LINE" ]] || fail "step 12: the completion set no auth_rt cookie"
for attr in HttpOnly Secure SameSite=Strict Path=/auth; do
  has_attr "$LINE" "$attr" || fail "step 12: the auth_rt cookie lacks the $attr attribute"
done
printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/back.auth"
BODY="$(jwt_payload "$tmp/back.auth")"
expect_eq "step 12: roles" "$(val 'd["roles"]')" "['admin']"
expect_eq "step 12: the token is INVITED's" "$(val 'd["sub"]')" "$INVITED_SUB"
BACK_ORG="$(val 'd["org_id"]')"
[[ "$BACK_ORG" != "$DEV_ORG" && "$BACK_ORG" != "$NEW_ORG" ]] || fail "step 12: the new company is another company's"
call GET /auth/me "" "$tmp/back.auth"
expect_status "step 12: GET /auth/me" 200
expect_eq "step 12: the company is the one named at the completion" "$(val 'd["org_name"]')" "$E2E_BACK_COMPANY"
expect_eq "step 12: the address" "$(val 'd["email"]')" "$INVITED_EMAIL"

# The account is the same one: its password is as it was, and works.
login invited E2E_INVITED_EMAIL E2E_INVITED_PASSWORD
BODY="$(jwt_payload "$tmp/invited.auth")"
expect_eq "step 12: the password it always had logs INVITED in to the new company" "$(val 'd["org_id"]')" "$BACK_ORG"
expect_eq "step 12: the password hash is as it was" \
  "$(psql_value "SELECT md5(\"PasswordHash\") FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = upper('$INVITED_EMAIL')")" "$HASH_BEFORE"
expect_eq "step 12: INVITED still has one account" "$(psql_value "SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = upper('$INVITED_EMAIL')")" "1"
expect_eq "step 12: the pending sign-up is gone" "$(psql_value "SELECT count(*) FROM \"PendingSignups\" WHERE \"NormalizedEmail\" = upper('$INVITED_EMAIL')")" "0"
expect_eq "step 12: signup.completed names the account and says existing_account true" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.completed' AND subject_user_id = '$INVITED_SUB' AND org_id = '$BACK_ORG' AND details->>'existing_account' = 'true'")" "1"
expect_eq "step 12: the signup.completed row of NEW has no details" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.completed' AND subject_email = '$NEW_EMAIL' AND details IS NULL")" "1"
call POST /auth/signup/complete "$tmp/back-complete.json"
expect_error "step 12: the same completion again" 400 invalid_token
expect_no_cookie "step 12: the same completion again"
pass "step 12: a member removed by the operator (login: 403 no_membership) asks for a sign-up and gets the usual link; the preview says has_account true; the completion with only the token and a company name is a login's answer with a new company of which INVITED is the admin; its password hash is as it was and logs in, INVITED has one account, and the audit row says existing_account true"
```

(The step is a plain extension: `login`, `call`, `expect_*`, `cli`, `token_body` and the rest are the script's own.)

- [ ] **Step 11: Check what can be checked here.** In `samples/notes-web`: `npx tsc --noEmit -p e2e/tsconfig.json` and `npx ng build`. From the repository root: `bash -n scripts/e2e-signup.sh scripts/e2e-web.sh`; `grep -n "python3" scripts/e2e-signup.sh` shows only the default of `PY` and the comments; `grep -rInP '\\u[0-9a-fA-F]{4}' scripts samples/notes-web/src samples/notes-web/e2e --include=*.sh --include=*.ts` finds nothing that is not meant. Do not start a stack and do not run Playwright.

- [ ] **Step 12: Hand back** — uncommitted. Files: everything listed under Files. Proposed note for the verifiers: `scripts/e2e-web.sh` now waits for a fourth invitation mail while seeding (the first start is a little slower), and `scripts/e2e-signup.sh` has a Step 12 (about a minute longer if step 10 was less than a minute before). Proposed subject: `feat(samples): a person without a company comes back by sign-up`.

---

## Day 6 — Saturday 04.04: the comeback, the guides, the acceptance map

When the day is done, a person without a company can come back by sign-up (Tasks 16 and 17, in the section "Amendment (2026-04-04)" above), the guides and the threat model describe the feature (the sign-in on completion and the comeback included), the changelog has an `Unreleased` section, and the acceptance map names a guarding test for each of the 24 criteria. Task 13 comes last because it documents what Tasks 14 to 17 changed.

### Task 13: The guides, the threat model, the audit runbook, the changelog, `check-docs.py` and the acceptance map

**Files:**
- Modify: `docs/integration/angular.md`, `docs/integration/python-fastapi.md`, `docs/deployment/vps.md`, `docs/security/threat-model.md`, `docs/operations/backup.md`, `README.md`, `samples/notes-web/README.md`, `CHANGELOG.md`, `scripts/check-docs.py`
- Create: `docs/superpowers/plans/0009-acceptance-map.md`

**Interfaces:**
- Consumes: every task above (the documents name its files, tests, settings and scripts; `scripts/check-docs.py` checks that each of them exists).
- Produces: the documents of the feature; `docs/integration/angular.md` listed in the default documents of `check-docs.py` and clean under it; `docs/superpowers/plans/0009-acceptance-map.md` (the verifiers' layer 2).

- [ ] **Step 1: The default list of `check-docs.py`.** In `scripts/check-docs.py`, in `DOCUMENTS`, after `"README.md",` add `"docs/integration/angular.md",`. `C:/p6v/Scripts/python.exe scripts/check-docs.py --self-test` still passes; run on the guide, it fails today on seven spans, which Step 2 repairs.

- [ ] **Step 2: The Angular guide.** In `docs/integration/angular.md`:

(a) **Make the seven code spans paths from the repository root** (what `scripts/check-docs.py` checks; `npm run check:docs` in the sample accepts either root). In the heading of step 2 and the text, replace each of these spans by the one in the second column:

| Where | Today | After |
| --- | --- | --- |
| step 2, the heading only (the first sentence already links the full path) | `` `src/app/auth/` `` | `` `samples/notes-web/src/app/auth/` `` |
| step 3, last sentence | `` `src/app/auth/auth.interceptor.spec.ts` `` | `` `samples/notes-web/src/app/auth/auth.interceptor.spec.ts` `` |
| step 4, "The screens are in" | `` `src/app/pages/` `` | `` `samples/notes-web/src/app/pages/` `` |
| step 4, "the calls are in" | `` `src/app/account-api.ts` `` | `` `samples/notes-web/src/app/account-api.ts` `` |
| step 4, "the words are all in" | `` `src/app/texts.ts` `` | `` `samples/notes-web/src/app/texts.ts` `` |
| step 5, last paragraph | `` `src/app/app.ts` `` | `` `samples/notes-web/src/app/app.ts` `` |
| step 6, the `index.html` bullet | `` `src/index.html` `` | `` `samples/notes-web/src/index.html` `` |

(The heading of step 2 must still contain `src/app/auth`: the guide check reads it.)

(b) **Step 4** (the heading and the number of steps stay: seven). Replace the paragraph that begins `Auth-Core's mails carry a link to your app;` and ends with the sentence "Outside Development the URLs must be `https`.", with

```markdown
Auth-Core's mails carry a link to your app; you build the screens, it sends the mails. Six screens (four of them open from a mail
link), and the settings that say where the links go (`Auth:App:FrontendUrls:ResetPassword`, `VerifyEmail` and `AcceptInvite`, and, only if your
product lets visitors start a company on their own, `CompleteSignup`; as environment variables `Auth__App__FrontendUrls__ResetPassword` and so on,
see the `auth` service in [`compose.yml`](../../samples/notes-web/compose.yml)). Each link is that URL plus `?token=<43 characters>`. Outside Development the
URLs must be `https`. Self-service sign-up is off until the product's manifest says `signup: self-service` **and** `CompleteSignup` is set
([`docs/deployment/vps.md`](../deployment/vps.md)); the sign-in screen asks `GET /auth/options` and offers "Create an account" only when the answer
is `{"signup":"self-service"}`, so a product does not repeat its manifest in its frontend.
```

and add two rows to the table of that step (after the `/invite?token=` row):

```markdown
| `/signup` | email and company name; always "Check your inbox"; the wait on a `429`; "closed" on `signup_disabled` | `POST /auth/signup` |
| `/complete-signup?token=` | the address (read-only) and the company name (editable); the password, twice (not for a person whose account exists and has no company, which the preview says with `has_account`: the sentence that the password stays as it is takes the place of the fields); the rules it did not meet; "link used up" with a way to sign up again; "an account already exists" with a way to sign in; on success the person is signed in and the notes screen opens | `POST /auth/signup/preview`, `POST /auth/signup/complete` (answers as `POST /auth/login` does: the access token and the refresh cookie) |
```

(c) In the list of three rules of step 4, after the bullet **Do not put an email in the URL.** add a sentence to it: `The same holds after a sign-up is completed: the completion signs the person in (Auth-Core answers as it answers a login) and the screen opens the notes in place of itself, so the address is in no URL and no sign-in screen is shown; the password was chosen at the link, never on the sign-up form.`
(d) In "Mind the logs", the list "for `/reset`, `/verify` and `/invite`" becomes "for `/reset`, `/verify`, `/invite` and `/complete-signup`".

(e) In step 3, in the sentence that says what the interceptor never adds the token to, change the end of the parenthesis from ``the mail-link endpoints)`` to ``the mail-link endpoints, `/auth/options`, `/auth/signup`)``.

(f) Three more sentences that name the mail screens: in the first rule of step 4, "the verify and invite screens offer "Try again"" becomes "the verify, invite and complete-signup screens offer "Try again""; in the second rule, "The reset and invite screens share one list of lines" becomes "The reset, invite and complete-signup screens share one list of lines"; in step 7, "Open a link from a reset mail (and an invitation, and a verification mail)" becomes "Open a link from a reset mail (and an invitation, a verification mail and a sign-up mail)".

(g) **The sign-in is two calls now** (`samples/notes-web/src/app/auth/auth.service.ts`, Task 15). In step 2, the sentence that lists what `auth.service.ts` holds, ``(the token, the session, `login`, `logout`, `refresh`)``, becomes ``(the token, the session, `login`, `completeSignup`, `logout`, `refresh`)``; and at the end of the **Sign-in** bullet of the list that follows it (the one that starts ``**Sign-in** is `await auth.login(email, password)` ``) add the sentences: ``A self-service sign-up ends in the same state: `await auth.completeSignup(token, password, companyName)` posts to `POST /auth/signup/complete`, which Auth-Core answers as it answers a login (the access token in the body and the refresh cookie), and it answers `{ ok: true }` or `{ ok: false, failure }` like `login`. On success open your product (the sample goes to `/notes`, replacing the screen of the used link, so that Back does not return to it). A person whose account exists and belongs to no company (the preview's `has_account`, spec 0009 Amendment 3) chooses no password: pass `null` as the password and the body holds the token and the company name only.``

Then `C:/p6v/Scripts/python.exe scripts/check-docs.py docs/integration/angular.md` and, in `samples/notes-web`, `npm run check:docs` both pass.

- [ ] **Step 3: The deployment guide.** In `docs/deployment/vps.md`:

(a) In the table of step 4, after the row that starts `` | `AUTH_FRONTEND_RESET_URL`, `` add

```markdown
| `AUTH_FRONTEND_SIGNUP_URL` | **optional**, https: the screen of your frontend that finishes a self-service sign-up. Leave it blank and sign-up stays off (the default; nothing changes for you). See "Self-service sign-up" below |
```

(b) In step 3 ("The manifest"), after the sentence that ends `**including the right to delete the company**.` add

```markdown
The optional key `signup: self-service` (the default is `invite-only`, and a manifest without the key behaves as in `0.1.0`) lets a visitor start a company of
their own: see "Self-service sign-up" in step 7.
```

(c) After the paragraph of step 7 that ends with the sentence "The commands never migrate the database: run them after step 5 says `401`.", add a subsection:

```markdown
### Self-service sign-up (optional)

Off unless you turn it on, and then it needs **two** things: `signup: self-service` in your manifest, and `AUTH_FRONTEND_SIGNUP_URL` in `.env`, the https address of
the screen of your frontend that finishes a sign-up (`https://app.example.com/complete-signup`; the sample's is `/complete-signup`). After changing `.env` run `$C up -d`
(a restart does not read it again); after changing the manifest run `$C restart auth`. A visitor then gives an email address and a company name; the mail holds a link to your screen, where the person chooses a password and
confirms the company's name; the company is made and the person is its first admin, with the first default role of your manifest that holds `"*"` (else the
first that holds `members:manage`), and is signed in at once: `POST /auth/signup/complete` answers as a login does (the access token and the refresh cookie), so your screen
opens the product and not the sign-in screen. An address whose account belongs to a company gets a mail that says so, with no link; a person whose account has no company (the company was deleted, or they were removed from it) gets the usual link, finishes with a company name only and keeps their password; every address gets the same answer.
`GET /auth/options` tells your screens whether to offer it. A manifest that asks for `self-service` without the address leaves sign-up off, logs a warning
at start and makes `/auth/health` say `Degraded`. Sign-up mails any well-formed address that passes the invitation rule, with the company name the visitor
typed in the body: it is limited to one mail a minute and five an hour per address and, counted with the other mail requests of a client address, ten a minute by default, there is no CAPTCHA, and the
risk that remains is in [`threat-model.md`](../security/threat-model.md). Keep the logs of `/complete-signup` as private as those of `/reset`: its
query holds a token.
```

(d) In the table "When something is wrong", after the row for `Degraded` add

```markdown
| `/auth/health` says `Degraded` after you turned sign-up on | The manifest says `signup: self-service` but `AUTH_FRONTEND_SIGNUP_URL` is empty: `$C logs auth \| grep "sign-up is off"`. Set the address in `.env` and `$C up -d` |
```

(e) In step 5 ("Start it"), the paragraph that begins "**`Healthy 200` is the answer you want" ends with the sentence "Restart the service (`$C restart auth`) after the fix." Add after it:

```markdown
`Degraded` also means that your manifest says `signup: self-service` while `AUTH_FRONTEND_SIGNUP_URL` is empty: `$C logs auth | grep "sign-up is off"` prints the line; set the address in `.env` and run `$C up -d`.
```

(f) In step 3 ("The manifest"), the parenthesis "(`deploy/auth.yaml` in the repository is a development example.)" becomes:

```markdown
(`deploy/auth.yaml` in the repository is a development example; it also says `signup: self-service`, which makes `/auth/health` say `Degraded` unless `AUTH_FRONTEND_SIGNUP_URL` is set, so do not copy that line without the address.)
```

(g) In "Upgrading to a new version", step 1: add at its end `A version that adds a table or a manifest key does not need you to change anything unless you turn the feature on; the changelog says so.`

- [ ] **Step 4: The threat model.** In `docs/security/threat-model.md`:

(a) Change `It describes version\n0.1.0 ([spec 0008](../superpowers/specs/0008-hardening-and-release.md)).` to read `It describes version\n0.1.0 ([spec 0008](../superpowers/specs/0008-hardening-and-release.md)) and the self-service sign-up that an instance may turn on ([spec 0009](../superpowers/specs/0009-self-service-signup.md)).`

(b) In the table of "Auth-Core", insert these rows right after the header row `| --- | --- | --- | --- |`:

```markdown
| I | Sign-up used to learn who has an account | `POST /auth/signup` never reads the accounts: an address with an account and one without get the same status, headers and body and run the same statements (`SignupRequestTests.The_request_runs_the_same_statements_for_any_address_and_never_reads_the_accounts`, `SignupRequestTests.An_address_with_an_account_gets_the_same_status_headers_and_body`, `SignupRequestTests.An_account_without_a_company_gets_the_same_status_headers_body_and_statements`); the difference is a mail, to the owner of the mailbox only (`SignupDispatchTests`) | A holder of a valid link sees `409 already_registered` at the completion and `has_account` in the preview (spec 0009, Amendment 3); the link comes only from the mailbox. Timing of the mail itself is not equalised |
| S | A stranger's password on an account (sign-up with someone else's address) | The password is chosen at the link, never on the sign-up form, so only the reader of the mailbox sets it (spec 0009, Decision 3); the account does not exist until then | |
| T | Hostile input in sign-up | The address passes the invitation rule (a mailbox, no look-alike of an ASCII address, no IP literal, dotless, numeric or hexadecimal domain: it cannot make the relay deliver to an internal host) and the company name the name rule (`SignupRequestTests`); a body of at most 8 KiB | |
| I | The company name a visitor typed goes into a mail to a stranger | It is in the body only, never in the subject, encoded in the HTML part and not a format string (`SignupMailComposerTests`) | See residual risk 46: the text is the visitor's, up to 100 characters, in a mail from the product's own address |
| D | Sign-up used to mail strangers or fill the queue | One mail a minute and five an hour per address (own kind `signup`), the per-IP limit of the mail policy (sign-up is counted with the other mail requests of a client address, 10 a minute by default), a pending sign-up per address that expires in 24 hours and is removed hourly (`SignupRateLimitTests`, `PendingSignupPruningTests`); a request that needs no mail is dropped by the bulk delete | See residual risk 46 |
| E | Two ways to make one account | The completion takes the advisory lock that invitation acceptance takes, and uses its token with one `DELETE ... RETURNING`: of parallel completions with one token one is `200`, and a completion and an acceptance for one address never both create the account (`SignupCompleteRaceTests`, which also holds the comeback of an account without a company against an acceptance for it) | A request for a sign-up whose mail is being sent waits for the send (at most 20 seconds): the dispatcher locks the pending sign-up |
| S | A sign-up link used to open an account that is someone else's (spec 0009, Amendment 3: a person without a company comes back by sign-up) | A link starts a company only for an account that belongs to no company and whose address is the typed one up to ASCII case (`EmailInput.IsSameAddress`, the rule invitation acceptance applies); an account that the lookup merely folds together with the typed address (the micro sign and Greek mu, final sigma and sigma) gets the notice and the `409`, never the link (`SignupComebackTests.An_account_under_a_spelling_the_normaliser_folds_is_409_and_is_left_alone`, `SignupComebackDispatchTests.An_account_that_only_looks_like_the_typed_address_gets_the_notice_and_no_link`); an account that has a company never gets a link (`SignupComebackDispatchTests.An_account_that_belongs_to_a_company_gets_the_notice_and_no_link`); any session still left from the lost company ends with the completion, as defence in depth: removing a member and deleting a company already revoke their sessions (`SignupComebackTests.Sessions_from_before_the_company_was_lost_end_with_the_completion`); the password is neither asked for nor changed (`SignupComebackTests.A_password_in_the_body_is_not_used`) | Whoever can read the mailbox can already reset the password (spec 0004), so the link adds no capability; the people of the former company keep their own (one company each, spec 0005), and the new company is a separate one |
| S | A mail link that opens a session (spec 0009, Amendment 2) | The link is single-use (one `DELETE ... RETURNING`), valid 24 hours and replaced by a newer mail, and the person chooses the password in the same step (a comeback chooses none: the account keeps its own, Amendment 3); the session it opens is a login's in every respect (one builder for the principal: the same lifetime, cap, family and security stamp, so a logout or a password change ends it) and is written in the completion's transaction (`SignupCompleteSignInTests.The_cookie_refreshes_like_a_logins_and_belongs_to_a_family`, `SignupCompleteSignInTests.The_session_carries_the_stamp_of_the_account_so_a_password_change_ends_it`, `SignupCompleteSignInTests.A_form_or_a_credentials_body_is_refused_with_400_and_issues_nothing`, `SignupCompleteSignInTests.What_a_client_adds_to_a_valid_completion_never_reaches_the_OpenIddict_request`) | Whoever can read the mailbox can already reset the password (spec 0004), so the link adds no capability |
```

(c) In the same table, in the row that starts `| R | Actions that leave no trace |`, change `The audit log: 22 kinds` to `The audit log: 24 kinds`.

(d) In "Residual risks": change `Items 1 to 45 are the risks accepted by specs 0002 to 0008 (41 to 44 by Decision 15; 45 is what Decision 14 leaves).` to `Items 1 to 46 are the risks accepted by specs 0002 to 0009 (41 to 44 by Decision 15; 45 is what Decision 14 leaves; 46 is spec 0009, Decision 11).` and add after item 45:

```markdown
46. **Sign-up mails any well-formed address.** With sign-up on, an anonymous person can make the product's mail relay send "complete your sign-up" to an address that is not theirs, with a company name of their choice (up to 100 characters, in the body, never in the subject) in a mail from the product's own address. It is limited three ways (one mail a minute and five an hour per address, the per-IP limit that counts sign-up with the other mail requests of a client address, 10 a minute by default, and the invitation address rule, which refuses addresses that could make the relay deliver to an internal host), and there is no CAPTCHA. A sender who spreads requests over many client addresses can still send a few mails each to many addresses; the cost to the product is its sender reputation. A product that cannot accept it leaves sign-up off, which is the default (spec 0009, Decision 11).
```

(e) In the residual column of the `R` row ("Anyone can create `login.failed` and `password.reset_requested` rows") write "`login.failed`, `password.reset_requested` and `signup.requested` rows"; in item 7 of the residual risks write the same list, and after its reference `(spec 0008, Residual risks)` add `; `signup.requested`, spec 0009` inside the parenthesis, so that it reads `(spec 0008, Residual risks; `signup.requested`, spec 0009)`.

Then `C:/p6v/Scripts/python.exe scripts/check-docs.py docs/security/threat-model.md` passes (every test name above exists).

- [ ] **Step 5: The Python guide.** In `docs/integration/python-fastapi.md` (it holds a copy of the sample's manifest, which now says `signup`):

(a) In the snippet of step 2, add the line `signup: self-service` after the `permissions:` line, and after the bullet that starts `- At least one default role must hold` add

```markdown
- `signup` is optional. `self-service` lets a visitor start a company of their own: they become its first admin, with the first default role that holds `"*"`
  (else the first that holds `members:manage`). It also needs the address of your frontend's completion screen
  (`Auth:App:FrontendUrls:CompleteSignup`; [the Angular guide](angular.md), step 4, and [the deployment guide](../deployment/vps.md)). The default,
  `invite-only`, means the operator makes the companies.
```

(b) Make the five code spans that are paths from the sample's folder paths from the repository root (what `scripts/check-docs.py` checks): `` `src/notes_api/settings.py` `` becomes `` `samples/notes-api/src/notes_api/settings.py` ``, `` `src/notes_api/app.py` `` (twice) `` `samples/notes-api/src/notes_api/app.py` ``, `` `tests/conftest.py` `` `` `samples/notes-api/tests/conftest.py` `` and `` `src/notes_api/db.py` `` `` `samples/notes-api/src/notes_api/db.py` ``. They failed before this slice. Run `C:/p6v/Scripts/python.exe scripts/check-docs.py docs/integration/python-fastapi.md`.

- [ ] **Step 6: The audit runbook.** In `docs/operations/backup.md`:

(a) In the paragraph "What `details` holds, where it is set", right after the words `(Development only)` in the clause about `"via": "seed"`, insert (with the comma):

```markdown
, and `"via": "signup"` on the `org.created` row of a company a visitor started, and `"existing_account": true` on the `signup.completed` row of a person who came back with an account that had no company
```

(b) In the last paragraph before the closing remark about the address typed into a failed login, the list of kinds ends `... `org.deleted`, `org.delete_refused` (reason: `wrong_password`, `locked`) and `rate_limit.hit` (at most one per address and policy per minute).` Replace that ending with

```markdown
`org.deleted`, `org.delete_refused` (reason: `wrong_password`, `locked`), `rate_limit.hit` (at most one per address and policy per minute), `signup.requested`
(an accepted request: the address as typed) and `signup.completed` (the account, the company and the address).
```

so that the list holds the 24 kinds. Run `C:/p6v/Scripts/python.exe scripts/check-docs.py docs/operations/backup.md`.

- [ ] **Step 7: The README and the sample's README.** In `README.md`: after the paragraph of "3. Create the first company" that ends with "(`POST /auth/org/invites`).", add

```markdown
Or let visitors start on their own: set `signup: self-service` in the manifest and give Auth-Core the address of your frontend's completion screen
(`AUTH_FRONTEND_SIGNUP_URL`). A visitor gives an email address and a company name, opens the link in the mail, chooses a password and is the first admin of a
new company; a person whose company was deleted, or who was removed from it, comes back the same way and keeps their password. It is off by default ([`docs/deployment/vps.md`](docs/deployment/vps.md), "Self-service sign-up").
```

and fix the manifest example of "1. Declare your product's permissions" (the yaml block that stands for `deploy/auth.yaml`; `README.md` lines 44 to 50 at the base of this plan), which lacks the key that the file it stands for now has: add the line `signup: self-service` after the `permissions:` line of the block, and after the closing fence of the block add the paragraph

```markdown
`signup: self-service` is optional (the default is `invite-only`, and a manifest without the key behaves as in `0.1.0`): it lets a visitor start a company of their own, and it
needs the address of your frontend's completion screen as well (`AUTH_FRONTEND_SIGNUP_URL`, see [`docs/deployment/vps.md`](docs/deployment/vps.md), "Self-service sign-up"). Without
that address sign-up stays off and `/auth/health` says `Degraded`, so leave the line out of a copy of this file until you have the address.
```

and in the list of scripts under "Develop it", after the line that starts `scripts/e2e-tenancy.sh`, add `scripts/e2e-signup.sh          # sign-up → link → company → first admin; an account that exists; an invitation first; a member removed who comes back (~6 min)`.
In `samples/notes-web/README.md`: write the code span `` `src/index.html` `` of the "Work on the screens" paragraph as `` `samples/notes-web/src/index.html` `` (it fails `scripts/check-docs.py` today), change "Mails (reset, verification, invitation)" to "Mails (reset, verification, invitation, sign-up)", in the tree under "What is here" change the line of `src/app/pages/` to list `login, forgot, reset, verify, invite, signup, complete-signup, notes`, change "the three `FrontendUrls` in `compose.yml`" (broken over two lines there) to "the four `FrontendUrls` in `compose.yml`", and in the opening paragraph add "start a company of their own," after "join a company from an invitation,". In the same file the last bullet of the list of known limits (`samples/notes-web/README.md` line 105 at the base of this plan) says `There is no PWA, no sign-up and no screen for company admins; see the deferred list of the spec.`, which is false since this slice: write it as `There is no PWA and no screen for company admins; see the deferred list of the spec. Sign-up (/signup and /complete-signup) works only while Auth-Core has it on: the manifest says signup: self-service and the completion address is set; finishing a sign-up signs the person in and opens the notes.` (a plain line of text, no code spans, so that nothing in it is checked as a path). Run `C:/p6v/Scripts/python.exe scripts/check-docs.py README.md samples/notes-web/README.md`.

- [ ] **Step 8: The changelog.** In `CHANGELOG.md`, between the introductory paragraph and `## 0.1.0 - 2026-02-24` insert

```markdown
## Unreleased

Self-service sign-up ([spec 0009](docs/superpowers/specs/0009-self-service-signup.md)): a product can let a visitor start a company of their own. Off by default; an
instance that does not turn it on behaves as `0.1.0`.

### Added

- **Sign-up.** `POST /auth/signup` (an email address and a company name; the same `202` for every address), `POST /auth/signup/preview` and
  `POST /auth/signup/complete` (the person chooses a password at the link of the mail, becomes the first admin of a new company, in one transaction, and is signed in:
  the answer is a login's, the access token and the refresh cookie, and the `login.succeeded` audit row is written in that transaction),
  and `GET /auth/options` (what a product's screens may offer). The first admin gets the first default role of the manifest that holds `*`, else the first that
  holds `members:manage`. An address whose account belongs to a company gets a mail that says so, with no link.
- **A person without a company comes back by sign-up** (Amendment 3). Someone whose company was deleted, or who was removed from it, has an account and no access; if they sign up with their address they get the usual link, `POST /auth/signup/preview` says `has_account` `true`, and `POST /auth/signup/complete` needs only the token and a company name (the `password` member is optional and, if sent, not used): the account keeps its password, its email is confirmed, its earlier sessions and mail links end (defence in depth), the company is made with the account as its first admin, the person is signed in, and the audit row `signup.completed` carries `details.existing_account` `true`. An account that belongs to a company still gets the "you already have an account" mail.
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

```

Run `C:/p6v/Scripts/python.exe scripts/check-docs.py CHANGELOG.md`.

- [ ] **Step 9: The acceptance map.** Create `docs/superpowers/plans/0009-acceptance-map.md`:

````markdown
# Spec 0009 — acceptance map

Maps each acceptance criterion of [spec 0009](../specs/0009-self-service-signup.md) to the tests and the e2e steps that guard it, for verifier layer 2
([`docs/workflow.md`](../../workflow.md#verification)). The .NET tests are in `tests/Auth.IntegrationTests/` (xUnit v3, PostgreSQL by Testcontainers: Docker must be
running; `dotnet test tests/Auth.IntegrationTests`); the Angular sample's unit tests are in `samples/notes-web/src/` (`npx ng test --watch=false`) and its Playwright tests in
`samples/notes-web/e2e/09-signup.spec.ts` (Chromium and WebKit, run by `scripts/e2e-web.sh`). The live API checks are `scripts/e2e-signup.sh` (the development stack; run it after
`scripts/e2e-tenancy.sh`), whose steps are named below; `scripts/e2e-prod.sh` keeps its stack `Healthy` with the address set.

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | `GET /auth/options` is `{"signup":"self-service"}` with sign-up on, `{"signup":"invite-only"}` with the key absent, `invite-only`, or the address not set | `SignupOptionsTests.Options_say_self_service_with_the_key_and_the_address`, `SignupOptionsTests.Options_say_invite_only_while_sign_up_is_off`, `SignupGateTests.Sign_up_is_off_unless_the_manifest_says_self_service_and_the_address_is_set`, `SignupGateTests.With_the_key_and_the_address_sign_up_is_on`; `scripts/e2e-signup.sh` step 1 |
| 2 | With sign-up off the three endpoints answer `403 signup_disabled` and create nothing; an instance with the `v0.1.0` manifest and configuration starts and passes its earlier tests unchanged | `SignupDisabledRequestTests.Sign_up_off_answers_403_before_the_body_is_read_and_creates_nothing`, `SignupPreviewDisabledTests.Preview_off_answers_403_before_the_body_is_read`, `SignupCompleteDisabledTests.Complete_off_answers_403_before_the_body_is_read_and_creates_nothing`, `SignupGateTests.The_other_ways_to_be_off_are_healthy_and_log_no_warning`, `UpgradeFromV010Tests.A_database_of_v0_1_0_gets_the_table_keeps_its_rows_and_reads_its_manifest_as_invite_only`, `ShippedConfigurationTests.A_production_host_with_the_manifest_key_and_an_empty_address_starts_with_sign_up_off`; the earlier tests are the rest of the project, unedited (`OpenApiTests`, `AuditTableTests` and `ManifestActivationTests` got lines for the contract they enumerate, and one method of `AuditTableTests` is renamed; `RatePolicySignupTests` is new); `ShippedConfigurationTests.A_production_host_with_the_v0_1_0_manifest_and_no_address_setting_at_all_stays_invite_only` is the configuration of `v0.1.0` itself (no key at all) |
| 3 | `POST /auth/signup` for a new address is `202` with an empty body; a mail "complete your sign-up" arrives with the configured address plus `token`, and the company name in its text | `SignupRequestTests.A_new_address_is_accepted_with_an_empty_body_and_a_pending_sign_up_is_stored`, `SignupDispatchTests.A_new_address_gets_the_complete_mail_with_the_link_and_the_company_name`, `SignupMailComposerTests.The_complete_mail_in_english_names_the_application_the_company_the_link_and_the_lifetime`; `scripts/e2e-signup.sh` steps 2 and 4 |
| 4 | The same request for an address with an account that belongs to a company has the same status, headers and body and runs the same statements, never reading the accounts; the mail says the account exists, with no link or token; the pending sign-up is gone | `SignupRequestTests.An_address_with_an_account_gets_the_same_status_headers_and_body`, `SignupRequestTests.The_request_runs_the_same_statements_for_any_address_and_never_reads_the_accounts`, `SignupDispatchTests.An_address_with_an_account_gets_the_account_exists_mail_and_the_pending_sign_up_is_gone`, `SignupDispatchTests.An_address_with_an_account_gets_the_notice_even_when_its_pending_sign_up_is_gone`, `SignupMailComposerTests.The_account_exists_mail_in_english_has_no_link_and_no_token`, `SignupComebackDispatchTests.An_account_that_belongs_to_a_company_gets_the_notice_and_no_link`, `SignupRequestTests.An_account_without_a_company_gets_the_same_status_headers_body_and_statements` (the request is the same for an account without a company); `scripts/e2e-signup.sh` step 8; `e2e/09-signup.spec.ts` "an address that already has an account..." |
| 5 | A malformed address, an address the invitation rule refuses, or a bad company name is `400 invalid_request` and queues nothing | `SignupRequestTests.Malformed_bodies_are_400_invalid_request_and_queue_nothing`, `SignupRequestTests.Addresses_the_invitation_rule_refuses_are_400_and_queue_nothing`, `SignupRequestTests.Company_names_that_break_the_name_rule_are_400_and_queue_nothing`; `scripts/e2e-signup.sh` step 3 |
| 6 | A second request for one address within 60 seconds and a sixth within the hour are the `429` of spec 0004; the `signup` limit and those of `forgot` and `verify/request` do not affect each other | `SignupRequestTests.A_second_request_within_a_minute_is_refused_and_asking_again_does_not_add_to_the_wait`, `SignupRequestTests.The_sixth_request_within_the_hour_is_refused_until_the_hour_is_over`, `SignupRequestTests.The_sign_up_limit_and_the_limits_of_forgot_and_verify_do_not_affect_each_other`; `scripts/e2e-signup.sh` steps 2 and 9 (part of) |
| 7 | A second accepted request replaces the first: the newer mail's link works, the older one is `invalid_token`, the company name shown is the newer one | `SignupRequestTests.A_second_accepted_request_replaces_the_first`, `SignupDispatchTests.A_second_mail_replaces_the_link_of_the_first`, `SignupPreviewTests.The_older_link_of_an_address_is_invalid_and_the_newer_shows_the_newer_company`, `SignupCompleteTests.A_used_unknown_expired_or_replaced_token_is_400_invalid_token` |
| 8 | `POST /auth/signup/preview` answers the typed address, the company name and `has_account` and changes nothing; an unknown, used or expired token is `400 invalid_token` | `SignupPreviewTests.Preview_answers_the_typed_address_and_the_company_and_changes_nothing`, `SignupComebackTests.Preview_says_has_account_for_an_account_without_a_company_and_only_for_it`, `SignupPreviewTests.Unknown_and_expired_tokens_are_one_400_invalid_token`, `SignupCompleteTests.A_used_unknown_expired_or_replaced_token_is_400_invalid_token` (the used token); `scripts/e2e-signup.sh` steps 5, 6 and 11 |
| 9 | `POST /auth/signup/complete` answers `200` with an access token and the refresh cookie, as a login does (Amendment 2); the account (email confirmed), the company (given name, a copy of the default roles) and the sole membership with the first admin's role exist; the token of the completion and a later login carry `org_id`, `roles` and `permissions` of that role, and the cookie refreshes like a login's | `SignupCompleteTests.A_visitor_signs_up_finishes_and_is_signed_in_as_the_admin_of_a_company_of_their_own`, `SignupCompleteTests.The_account_company_roles_and_membership_are_what_the_contract_says`, `SignupCompleteTests.The_company_name_of_the_request_is_only_a_suggestion`, `SignupCompleteSignInTests.The_completion_answers_as_a_login_does_with_the_status_and_the_access_token_only`, `SignupCompleteSignInTests.The_cookie_and_the_headers_are_those_of_a_login`, `SignupCompleteSignInTests.The_token_carries_the_new_company_and_the_first_admins_role_and_the_claims_of_a_login`, `SignupCompleteSignInTests.The_cookie_refreshes_like_a_logins_and_belongs_to_a_family`, `SignupCompleteSignInTests.The_session_starts_at_the_completion_and_ends_at_the_cap_of_a_login`, `SignupCompleteSignInTests.The_session_carries_the_stamp_of_the_account_so_a_password_change_ends_it`; `scripts/e2e-signup.sh` steps 6, 7 and 8 |
| 10 | The first admin's role is the first default role holding `*`; with no such role, the first holding `members:manage` | `SignupFirstAdminTests.The_first_admin_gets_the_first_default_role_that_holds_the_star`, `SignupFirstAdminTests.Without_a_star_the_first_admin_gets_the_first_role_that_holds_members_manage`, `SignupFirstAdminTests.A_star_role_after_a_members_manage_role_still_wins`, `ManifestSignupTests.The_first_admin_role_is_the_first_default_role_that_holds_the_star`, `ManifestSignupTests.Without_a_star_the_first_admin_role_is_the_first_that_holds_members_manage`; `scripts/e2e-signup.sh` step 7 |
| 11 | A completion with a weak password, for an address without an account, is `400 weak_password` with the rules, creates nothing, and the token still works | `SignupCompleteTests.A_weak_password_names_the_rules_creates_nothing_and_leaves_the_token_usable`, `SignupCompleteTests.The_order_of_the_checks_is_token_then_account_then_password`; `scripts/e2e-signup.sh` step 6 |
| 12 | A token works once; of parallel completions with one token exactly one is `200`, and one account and one company exist | `SignupCompleteTests.A_used_unknown_expired_or_replaced_token_is_400_invalid_token`, `SignupCompleteRaceTests.Of_parallel_completions_with_one_token_exactly_one_is_200`, `SignupCompleteSignInTests.Every_refusal_is_what_it_was_and_issues_no_cookie_and_no_token`, `SignupCompleteSignInTests.The_same_body_posted_again_is_invalid_token_with_no_cookie_and_no_second_session`, `PendingSignupsTests.Consume_uses_the_link_up_once_and_removes_the_row`; `scripts/e2e-signup.sh` step 6 |
| 13 | A link older than 24 hours is `400 invalid_token`, and the hourly clean-up removes pending sign-ups whose time has passed | `SignupPreviewTests.A_link_works_until_24_hours_after_the_mail_and_not_a_second_longer`, `SignupCompleteTests.A_used_unknown_expired_or_replaced_token_is_400_invalid_token` (expired), `PendingSignupsTests.A_link_works_until_exactly_24_hours_after_it_was_issued`, `PendingSignupPruningTests.Pruning_removes_pending_sign_ups_whose_time_has_passed_and_keeps_the_others`, `PendingSignupPruningServiceTests.The_hourly_pass_of_the_service_removes_expired_pending_sign_ups`; `scripts/e2e-signup.sh` step 11 (an expired link, live) |
| 14 | An invitation accepted between the request and the completion makes the completion `409 already_registered` with no company and the pending sign-up gone; a completion and an acceptance at once never both create an account | `SignupCompleteTests.An_account_made_in_the_meantime_is_409_already_registered_and_the_pending_sign_up_is_gone`, `SignupCompleteTests.An_account_under_another_spelling_of_the_address_is_409_too`, `SignupCompleteRaceTests.A_completion_and_an_invitation_acceptance_for_one_address_never_both_create_an_account`, `SignupComebackTests.An_account_that_joined_a_company_meanwhile_is_409_and_the_pending_sign_up_is_gone`, `SignupCompleteRaceTests.A_comeback_and_an_invitation_acceptance_for_one_account_never_both_give_it_a_company`; `scripts/e2e-signup.sh` step 10 |
| 15 | Pending sign-ups are invisible elsewhere: login is the `401` of an unknown address, `forgot password` sends nothing | `SignupDispatchTests.Pending_sign_ups_are_invisible_to_login_and_forgot_password`; `scripts/e2e-signup.sh` step 9 |
| 16 | The manifest accepts `signup: self-service` and `invite-only`; any other value is refused by the rules of spec 0005 (the last valid manifest kept, `Degraded`); a stored manifest without the key reads as `invite-only` | `ManifestSignupTests.The_key_accepts_invite_only_and_self_service`, `ManifestSignupTests.Any_other_scalar_makes_the_manifest_invalid_and_names_the_key`, `ManifestSignupTests.A_list_or_a_map_makes_the_manifest_invalid`, `ManifestSignupTests.A_manifest_stored_by_v0_1_0_reads_as_invite_only`, `ManifestSignupActivationTests.A_refused_value_keeps_the_last_valid_manifest_and_health_says_degraded`, `ManifestSignupActivationTests.A_manifest_stored_by_v0_1_0_stays_invite_only_when_the_file_is_missing` |
| 17 | With `self-service` and no address the start logs a warning, health is `Degraded` and sign-up answers `403`; a bad address stops the start naming the key | `SignupGateTests.Self_service_without_the_address_warns_at_the_start_and_health_says_degraded`, `SignupGateTests.A_bad_address_stops_the_start_naming_the_key_and_not_the_value`, `CompleteSignupSettingsTests.A_bad_address_is_refused_naming_the_key_and_never_echoing_the_value`, `CompleteSignupSettingsTests.Outside_Development_the_address_must_be_https`, `SignupDisabledRequestTests.Sign_up_off_answers_403_before_the_body_is_read_and_creates_nothing` (the variant without the address) |
| 18 | The per-IP limits classify the four paths as written | `RatePolicySignupTests.The_four_paths_count_against_the_policies_of_the_contract`, `RatePolicySignupTests.The_paths_are_compared_without_regard_to_case_or_a_trailing_slash`, `SignupRateLimitTests.The_request_counts_against_the_mail_policy_of_the_per_ip_limit` |
| 19 | The audit log holds `signup.requested`, and for a completion `signup.completed` and `org.created` with `details.via` `signup`, and `login.succeeded` for its sign-in (Amendment 2), in its transaction, and `details.existing_account` `true` on `signup.completed` for an account that existed (Amendment 3); no row holds a password or a token | `SignupRequestTests.An_accepted_request_is_recorded_with_the_typed_address_only`, `SignupRequestTests.A_refused_request_is_not_recorded`, `SignupCompleteTests.A_completion_is_recorded_with_the_account_the_company_and_the_address_and_nothing_secret`, `SignupCompleteTests.Nothing_is_left_when_the_audit_row_cannot_be_written`, `SignupCompleteSignInTests.The_completion_writes_login_succeeded_beside_signup_completed_and_org_created`, `SignupCompleteSignInTests.A_token_store_that_refuses_leaves_nothing_of_the_completion_and_the_link_still_works`, `SignupCompleteSignInTests.An_audit_log_that_refuses_the_sign_in_leaves_nothing_of_the_completion_and_the_link_still_works`, `SignupCompleteSignInTests.A_sign_in_that_OpenIddict_refuses_leaves_nothing_of_the_completion_and_the_link_still_works`, `AuditTableTests.The_kinds_are_the_twenty_four_of_the_contract`, `SignupComebackTests.The_completion_is_recorded_with_existing_account_true_beside_login_succeeded_and_org_created`, `SignupComebackTests.A_sign_in_that_cannot_be_written_leaves_the_account_as_it_was`; `scripts/e2e-signup.sh` steps 7 and 12 (the rows in a live database) |
| 20 | The OpenAPI document lists the four operations with their answers | `OpenApiTests.The_description_names_every_endpoint_of_the_service_and_nothing_else`, `OpenApiTests.Each_endpoint_describes_the_statuses_of_the_contract` (the completion is `200`, `400`, `403`, `409`, `415`, `429`), `OpenApiTests.The_completion_answers_with_the_body_of_the_login`, `OpenApiTests.The_answers_that_set_or_clear_the_refresh_cookie_say_so`, `OpenApiTests.Each_endpoint_that_reads_a_body_describes_it_in_snake_case`, `OpenApiTests.The_error_codes_of_the_contract_are_in_the_descriptions`, `OpenApiTests.The_responses_are_described_in_snake_case`, `OpenApiTests.Every_route_of_the_service_is_in_the_description`, `OpenApiTests.The_completion_asks_for_no_password_of_an_account_without_a_company` (and `has_account` in `The_responses_are_described_in_snake_case`); `scripts/e2e-signup.sh` step 1 (the four paths) |
| 21 | Both mails exist in Polish and English, in HTML and text; the "complete" mail's HTML and text hold the same link | `SignupMailComposerTests.The_complete_mail_in_polish_keeps_its_letters`, `SignupMailComposerTests.The_html_and_the_text_hold_the_same_link`, `SignupMailComposerTests.Every_language_has_every_text_of_both_mails`, `SignupMailComposerTests.The_account_exists_mail_in_polish_has_no_link_and_no_token`, `SignupDispatchPolishTests.The_mails_are_in_the_configured_language`; `scripts/e2e-signup.sh` step 4 |
| 22 | The sample: a person signs up on `/signup`, opens the mail link, completes on `/complete-signup`, is signed in at once (no sign-in screen on the way) and sees the company and the admin role, and a reload keeps them signed in — in Chromium and in WebKit; the token leaves the address bar; the address is in no URL; "already have an account" (the mail, and the completion screen when the account appeared meanwhile), a used link, an expired link and a link that never worked show their texts | `e2e/09-signup.spec.ts` (all six tests, both projects, by `scripts/e2e-web.sh`, which seeds the invited address, the expired link and the returning person; the first test collects every URL the page requests and asserts the address is in none); the unit tests `signup.spec.ts`, `complete-signup.spec.ts` (the token leaves the address bar and the router; the person is signed in and the notes screen opens in place of the screen), `auth.service.signup.spec.ts` (the token in memory, `/auth/me`, an old session dropped), `login.signup.spec.ts`, `account-api.signup.spec.ts`, `app.routes.spec.ts` (the routes are public) |
| 23 | `scripts/e2e-signup.sh` passes against a fresh stack, and every earlier e2e script and test still passes | `scripts/e2e-signup.sh` (all steps); the five older scripts and `scripts/e2e-hardening.sh`, `scripts/e2e-notes.sh`, `scripts/e2e-web.sh` and `scripts/e2e-prod.sh` (the last two with the address set: `ShippedConfigurationTests.The_sample_overlay_and_the_production_test_set_the_address_so_that_their_stacks_stay_healthy`); `dotnet build -warnaserror && dotnet test tests/Auth.IntegrationTests`; the Python and Angular suites |
| 24 | (Amendment 3) For an account without a company the sign-up mail carries a link; the preview says `has_account: true`; a completion with only the token and a company name answers as a login, creates the company with that account as its first admin, leaves its password working and unchanged, confirms its email and writes `signup.completed` with `details.existing_account` `true`; the sample's completion screen asks only for the company name; if the account joins a company before the completion, `409 already_registered` | `SignupComebackDispatchTests.An_account_without_a_company_gets_the_complete_mail_with_the_link`, `SignupComebackDispatchTests.The_complete_mail_goes_to_the_address_as_typed`, `SignupComebackDispatchTests.A_sign_up_for_an_account_without_a_company_whose_pending_sign_up_is_gone_sends_nothing`, `SignupComebackDispatchTests.A_failed_send_to_an_account_without_a_company_leaves_the_previous_link_valid`, `SignupComebackDispatchTests.An_account_that_joins_a_company_while_the_dispatcher_waits_for_the_pending_sign_up_gets_the_notice`, `SignupComebackDispatchTests.An_account_that_joins_a_company_while_the_dispatcher_waits_and_the_pending_sign_up_survives_gets_the_notice`, `SignupComebackDispatchTests.An_account_that_only_looks_like_the_typed_address_gets_the_notice_and_no_link`, `SignupComebackTests.Preview_says_has_account_for_an_account_without_a_company_and_only_for_it`, `SignupComebackTests.A_person_without_a_company_comes_back_with_only_the_token_and_a_company_name`, `SignupComebackTests.A_password_in_the_body_is_not_used`, `SignupComebackTests.An_unconfirmed_address_is_confirmed_by_the_link`, `SignupComebackTests.The_completion_is_recorded_with_existing_account_true_beside_login_succeeded_and_org_created`, `SignupComebackTests.Sessions_from_before_the_company_was_lost_end_with_the_completion`, `SignupComebackTests.A_malformed_password_is_400_invalid_request_for_such_an_account_too_and_the_token_stays_usable`, `SignupComebackTests.A_new_address_without_a_password_is_400_invalid_request_and_the_token_stays_usable`, `SignupComebackTests.An_unknown_token_without_a_password_is_invalid_token`, `SignupComebackTests.An_account_that_joined_a_company_meanwhile_is_409_and_the_pending_sign_up_is_gone`, `SignupComebackTests.An_account_under_a_spelling_the_normaliser_folds_is_409_and_is_left_alone`, `SignupComebackTests.A_sign_in_that_cannot_be_written_leaves_the_account_as_it_was`, `SignupCompleteRaceTests.Of_parallel_completions_for_an_account_without_a_company_exactly_one_is_200`, `SignupCompleteRaceTests.A_comeback_and_an_invitation_acceptance_for_one_account_never_both_give_it_a_company`, `SignupRequestTests.An_account_without_a_company_gets_the_same_status_headers_body_and_statements`, `OpenApiTests.The_completion_asks_for_no_password_of_an_account_without_a_company`; the unit tests `complete-signup.spec.ts` (the block "for an account that exists without a company (has_account)"), `auth.service.signup.spec.ts` ("leaves the password out of the body") and `account-api.signup.spec.ts` ("reads has_account true"); `e2e/09-signup.spec.ts` "a person whose company is gone comes back by sign-up..." (both projects; `scripts/e2e-web.sh` seeds the member removed through the operator CLI); `scripts/e2e-signup.sh` step 12 |

## Contract sentences that are not acceptance criteria

| Sentence of the contract | Guard |
| --- | --- |
| The password is chosen at the link, never on the sign-up form (Decision 3) | `signup.spec.ts` ("asks for an email and a company name..." asserts there is no password field); `SignupRequest` has no password member (`OpenApiTests.Each_endpoint_that_reads_a_body_describes_it_in_snake_case`) |
| The completion signs the person in, and the sign-in is part of it (Amendment 2, replacing Decision 10) | `SignupCompleteSignInTests` (all of it), `SignupCompleteTests.A_visitor_signs_up_finishes_and_is_signed_in_as_the_admin_of_a_company_of_their_own`; `scripts/e2e-signup.sh` step 6; `e2e/09-signup.spec.ts` first test; an invitation is unchanged (`InviteAcceptTests`: `204`, no cookie) |
| The address of the completion is locked with the lock invitation acceptance uses | `SignupCompleteRaceTests.A_completion_and_an_invitation_acceptance_for_one_address_never_both_create_an_account` |
| An account that belongs to a company never signs up again: it gets the "you already have an account" mail (Decision 9, narrowed by Amendment 3: an account without a company may start a new company) | `SignupDispatchTests.An_address_with_an_account_gets_the_account_exists_mail_and_the_pending_sign_up_is_gone` (confirmed or not), `SignupComebackDispatchTests.An_account_that_belongs_to_a_company_gets_the_notice_and_no_link` |
| A failed sending leaves the previous link valid | `SignupDispatchTests.A_failed_send_leaves_the_previous_link_valid_and_the_retry_sends_a_new_one`, `PendingSignupsTests.Upsert_for_the_same_address_replaces_the_names_and_the_time_and_keeps_the_link` |
| A completion after sign-up was turned off is refused the same way | `SignupPreviewDisabledTests.A_preview_after_sign_up_was_turned_off_is_403_and_changes_nothing`, `SignupCompleteDisabledTests.A_completion_after_sign_up_was_turned_off_is_403_and_consumes_nothing` |
| The account check comes first when a sign-up mail is sent: an address with an account that belongs to a company gets the notice even if its pending sign-up is gone; with no live pending sign-up and no such account nothing is sent (Amendment 3: an account without a company is treated as a new address) | `SignupDispatchTests.An_address_with_an_account_gets_the_notice_even_when_its_pending_sign_up_is_gone`, `SignupDispatchTests.An_address_with_an_account_and_an_expired_pending_sign_up_gets_the_notice_and_loses_the_row`, `SignupDispatchTests.A_sign_up_whose_pending_sign_up_is_gone_and_whose_address_has_no_account_sends_nothing`, `SignupCompleteRaceTests.A_mail_that_waits_for_a_completion_never_leaves_a_working_link_to_a_used_sign_up`, `SignupComebackDispatchTests.A_sign_up_for_an_account_without_a_company_whose_pending_sign_up_is_gone_sends_nothing` |
| The mail queue's clean-up and composer know the `signup` kind (To verify) | `SignupDispatchTests.A_request_for_a_gone_pending_sign_up_is_dropped_by_the_bulk_delete`, `SignupMailComposerTests.An_unknown_kind_of_mail_is_not_composed`, `MailKindTests.The_values_of_the_kinds_are_stored_and_never_renumbered` |
| The EF migration that adds the table (To verify) | `PendingSignupTableTests`, `UpgradeFromV010Tests.A_database_of_v0_1_0_gets_the_table_keeps_its_rows_and_reads_its_manifest_as_invite_only` |
| The shipped manifests turn sign-up on, and the development and production files name the address | `ShippedConfigurationTests` |
````

Run `C:/p6v/Scripts/python.exe scripts/check-docs.py docs/superpowers/plans/0009-acceptance-map.md`: every class, method and file named in it must resolve.

- [ ] **Step 10: Check them all.** From the repository root: `C:/p6v/Scripts/python.exe scripts/check-docs.py` (the default list, now with the guide), `C:/p6v/Scripts/python.exe scripts/check-docs.py --self-test`, `C:/p6v/Scripts/python.exe scripts/check-docs.py docs/superpowers/plans/0009-acceptance-map.md docs/operations/backup.md docs/integration/python-fastapi.md samples/notes-web/README.md docs/deployment/vps.md docs/security/threat-model.md CHANGELOG.md`; in `samples/notes-web`: `npm run check:docs`. Then `grep -rInP '\\u[0-9a-fA-F]{4}' src tests scripts docs samples/notes-web/src samples/notes-web/e2e --include=*.cs --include=*.sh --include=*.md --include=*.ts` finds nothing that is not meant, and `git status --porcelain` shows only the files of the 17 tasks (nothing else in the working tree).

- [ ] **Step 11: Hand back** — uncommitted. Files: everything listed under Files. Proposed subject: `docs: self-service sign-up in the guides, the threat model, the changelog and the acceptance map`.

---

## Self-review

Run against the spec, section by section, after the tasks above were written.

**1. Spec coverage.**

| Spec section | Task |
| --- | --- |
| Goal, the sequence of curl calls | 5, 7, 8 and 14 (the whole way: `SignupCompleteTests.A_visitor_signs_up_finishes_and_is_signed_in_as_the_admin_of_a_company_of_their_own`), 12 and 15 (`scripts/e2e-signup.sh`) |
| In scope: `GET /auth/options`, the three endpoints | 2, 5, 7, 8 |
| Pending sign-ups (stored apart, one per address, 24 hours, hourly clean-up) | 3 |
| Two mails, Polish and English, HTML and text | 4, 6 |
| The manifest key; the address key | 1, 9 |
| The first admin's role; one transaction | 1, 8 |
| Per-IP limits, mail limit, audit, OpenAPI, threat model | 2, 5, 8, 13 |
| `samples/notes-web`, `samples/notes-api/auth.yaml` | 9, 10, 11, 12 |
| `scripts/e2e-signup.sh`; guides and environment examples | 12, 13, 9 |
| Out of scope (providers, CAPTCHA, unique names, several companies) | nothing built |
| Contract: general rules, on/off, `403` before the body | 1, 5, 7, 8 (`Signup*DisabledTests`) |
| Mails (the bullets with the account check first, locale, failed sending) | 4, 6 |
| Manifest, Configuration, Limits, Audit events, OpenAPI | 1, 9, 2, 5/8, 2/5/7/8 |
| Sample frontend | 10, 11, 12, and 15 (the completion opens the notes screen) |
| Amendment 3: a person without a company comes back by sign-up (the link at send time, `has_account`, the completion without a password that keeps the account's password and confirms its email, `existing_account` in the audit row, `409` when the account joined a company meanwhile, the screen with the company name only; an account with a company still gets the notice) | 16 (server and tests, the `409` race with an invitation), 17 (sample, Playwright, `scripts/e2e-signup.sh` Step 12), 13 (guides, threat model, changelog, map: criterion 24) |
| Amendment 2: the completion signs in (the answer of a login, the cookie, the token's company and role, `login.succeeded` in the completion's transaction, nothing stays if the sign-in cannot be written, the sample opens the product, an invitation unchanged) | 14 (server), 15 (sample, Playwright, `scripts/e2e-signup.sh`), 13 (guides, threat model, changelog, map) |
| Acceptance criteria 1 to 24 | the map of Task 13 names a test or a step for each |
| Decisions 1 to 13 (9 replaced by Amendment 3, 10 by Amendment 2) | 1 (8, 12, 13), 3 and 5 (7), 5 and 6 (6, 9), 8 (3, 4, 5, 10), 5 and 13 (11) |
| To verify during implementation (four points) | the mail queue and composer: Tasks 4 and 6; the stored manifest: Task 1; the migration: Task 3; the order of checks and the lock: Task 8 |

**2. Placeholder scan.** No "TBD", "similar to", or step without its code. The edits to existing files give the text to find and the text to put; the four documents are edited by instruction with the sentences written out. Two edits say "the line that starts ..." where the file's line carries characters that a tool parameter would mangle (the arrow in the `README.md` script list); the implementer reads the file.

**3. Type consistency.** `SignupMode`/`SignupModes`, `Manifest.Signup`/`FirstAdminRole`, `SignupGate.IsOn/MissingAddress/ModeName/WarnIfMissingAddress`, `MailSettings.CompleteSignupUrl`, `MailSettingsLoader.CompleteSignupUrlKey`, `TenancyErrors.SignupDisabled/AlreadyRegistered`, `PendingSignup`, `PendingSignups.UpsertAsync/IssueAsync/FindUsableAsync/LockUsableAsync/ConsumeAsync/DeleteAsync`, `MailKind.Signup`, `MailRequestStore.SubmitAsync(..., typedEmail, companyName)`, `MailComposer.Compose(MailKind.Signup, ...)/ComposeAccountExists`, `MailTexts.AccountExists`, `AuditKinds.SignupRequested/SignupCompleted`, `SignupService.PreviewAsync/CompleteAsync`, `SignupApi`, `SignupVariants`, `SignupTestBase`, `PendingSignupSeed` have the same names and signatures wherever a later task uses them. The wire names (`company_name`, `signup`) match the contracts in `SignupContracts.cs`, the tests and the sample. The names of Amendment 3 are the same wherever they are used: `SignupAccount.MayStartCompany`, `SignupAccounts.FindAsync`, the optional-member overload `JsonObjectBody.ReadStringsAsync(request, required, optional, cancellationToken)`, `SignupPreview.HasAccount` and `SignupPreviewResponse.HasAccount` (`has_account` on the wire and in `SignupPreview.has_account` of the sample), `SignupService.CompleteAsync(token, string? password, companyName, cancellationToken)`, `SignupApi.CompleteWithoutPassword`, `SignupTestBase.RemovedMemberAsync` and `JoinACompanyAsync`, `AuthService.completeSignup(token, string | null, companyName)`, `texts.completeSignup.keepsPassword`, the test id `keeps-password`, the audit detail `existing_account` and the Playwright setting `E2E_COMEBACK_EMAIL`.

**4. Review Focus.** Each of the five has its named tests in the tasks it points at.

## Decisions taken in this plan

The spec is not changed by this plan (the owner amends it where a line below says so). One line each; the owner has ruled on every one of 1 to 10; 11 to 17 are the plan's own choices for the owner's Amendment 3 (none of them changes the contract it states, and each is covered by a test).

1. **The pending sign-up has two times** (`ExpiresAt`, when the row goes; `TokenExpiresAt`, when the current link stops working) as the spec's Amendment 2 says; the contract is unchanged.
2. **The account check comes first when a sign-up mail is sent:** an address with an account gets "you already have an account" even when its pending sign-up is gone (and any pending row of the address is deleted); nothing is sent only with no account and no live pending sign-up (the owner's ruling; Task 6) (narrowed by Amendment 3: an account that belongs to a company).
3. **Tests that enumerate the contract get lines added and nothing else:** `OpenApiTests` (lines added; each replaces the previous task's line by itself and the new one, and one `foreach` array is extended), `AuditTableTests` (one list extended and its one test method renamed from `...twenty_two...` to `...twenty_four...`) and `ManifestActivationTests` (one assertion); every other earlier test is untouched, which is how criterion 2's "earlier tests pass unchanged" is kept.
4. **The pipeline answers first:** the charset guard (`415`) and the per-IP limiter (`429`) run before the handlers, as for every endpoint, so a request with a UTF-16 charset or over the limit is answered by them even while sign-up is off; "`403` before the body is read" holds for the handlers.
5. **A sign-up request or a completion waits for a mail being sent to the same address:** the dispatcher locks the pending sign-up while it sends (as it locks an invitation), at most the 20 seconds noted for the other mails; recorded in the threat model.
6. **Documents that touch this slice and already failed `scripts/check-docs.py` are repaired:** `docs/integration/angular.md` (seven spans; it joins the default list), `docs/integration/python-fastapi.md` (five) and `samples/notes-web/README.md` (one) now name paths from the repository root; `npm run check:docs` in the sample accepts both forms.
7. **`scripts/e2e-prod.sh` sets `AUTH_FRONTEND_SIGNUP_URL`,** because `deploy/auth.yaml` now says `signup: self-service` and its step 2 expects `Healthy`; an operator who copies that manifest without the address gets `Degraded`, which the deployment guide says.
8. **`signup:` with no value counts as absent,** like `permissions:` with no value; `signup: ''` is invalid (probed).
9. **Smaller choices:** the subjects of the two mails start `Complete your sign-up` / `Dokończ rejestrację` and `You already have an account` / `Masz już konto` and never hold the company name; a completion address of only white space counts as not set; the account-exists mail goes to the address as the account stores it, the complete mail to the address as typed; a completion also clears the login streak of the address, as an invitation does; the sample trims the email and the company name before sending; `Sign-up` is the OpenAPI tag of the four operations; `400 invalid_request` has its own text on each sign-up screen ("Check the email address and the company name." and "Check the company name.").
10. **The completion signs in through OpenIddict, not around it (Amendment 2, Tasks 14 and 15):** `/auth/signup/complete` is a third pass-through token endpoint beside `/auth/login` and `/auth/refresh`, claimed by an extraction handler that hands OpenIddict a placeholder request and never reads the body; the principal of both sign-ins is built by `SessionPrincipal`; `SignupService.CompleteAsync` hands its open transaction to `AtomicSignInResult`, which commits it after the session and `login.succeeded` are written. The choice was probed (a plain endpoint cannot sign in through OpenIddict; the pass-through variant answered, rolled back on a refusing token store and on a refusing audit table, and kept `405` for the other methods). The sample drops the session that exists before it holds the new one, and opens the notes screen with `replaceUrl`.

11. **Amendment 3 (Tasks 16 and 17) replaces Decision 9.** An account that belongs to no company is, for a sign-up, a new address with a password it already has: the dispatcher sends the usual link, the preview says `has_account`, the completion neither asks for nor changes the password. `has_account` is `true` exactly when the account is the one the link would use as it is: it has no company and its stored address is the typed address up to ASCII case (the spec's "true only for an account without a company").
12. **An account the lookup merely folds together with the typed address** (the micro sign and Greek mu, final sigma and sigma: the normaliser folds them) is not used by a link: it gets the notice mail and the `409`, as invitation acceptance refuses such an account (`EmailInput.IsSameAddress`). A link proves only the mailbox it was sent to. Invisible for ordinary addresses: the typed address already passes the look-alike rule of the request.
13. **The password is an optional member of the completion body.** Absent is fine; present it follows the rule of every string member (non-blank, no NUL), so `123`, `""`, `null` and a NUL stay `400 invalid_request` for every account. For an address with no account at all an absent password is `400 invalid_request` after the token check, with the token left usable (the transaction is rolled back). For an account the link may not use, the answer is the `409` whether or not a password came: a screen that was told `has_account` sent none, and the contract says `409`.
14. **A comeback ends whatever sessions and mail links the account still has** (`AccountSessions.EndAllAsync`, as for a new account and at an invitation). This is defence in depth: removing a member and deleting a company already revoke their sessions and a login without a company is `403`, so normally nothing is left; if something is (a state the tests make straight in the database), it must not refresh into the new company. The spec does not say it; it costs the person nothing, because the completion has just signed them in.
15. **`details.existing_account` is written only when it is `true`.** A new account's `signup.completed` row has no details, as before (a test pins `Details` null), and the spec says the member is `true` "for an account that existed".
16. **The two SQL statements of the dispatcher (the bulk delete and the choice of the next row) count only an account with a membership** as a reason to keep a sign-up row, so that requests for company-less accounts whose pending sign-up is gone are dropped in bulk, like requests for addresses with no account, and a flood of them is never handled one by one.
17. **The tests of this slice that used `CreateUserAsync(..., member: false)` for "an account that exists" now say "an account that belongs to a company"** (the helper's default); their intent is unchanged. Decision 3's rule that earlier tests stay unedited concerns the tests of earlier specs.

## Open questions for the owner

None.
