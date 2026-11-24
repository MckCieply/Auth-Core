# GitHub and Managing One's Own Logins Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0012
- **Date:** 2026-05-04
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0012-github-and-managing-logins.md`](../specs/0012-github-and-managing-logins.md) (with its Amendment of
  2026-05-04, after the probe) — the plan argues from the spec; where they disagree, **the spec wins** and the disagreement is a finding (see
  [`docs/workflow.md`](../../workflow.md)). The Amendment (items A1 to A13, owner Decision 19) was written together with this plan on the
  orchestrator's and the owner's rulings; nothing else in the spec is changed by the plan. The choices the spec leaves open are in "Decisions
  taken in this plan" at the end; "Open questions for the owner" lists what the plan could not settle.
- **Based on:** `75f5a0f` (`main` at `edff315` with spec 0012 on top of it). Every anchor of an edit below was checked against this base; an
  anchor in a file that an earlier task of this plan changes is written against the text that task leaves.

**Goal:** A person signs in with GitHub as with Google and Microsoft, and a signed-in person manages how they sign in on a settings screen:
they see their password and every provider, connect a provider, disconnect one (never the last way in) and change their password, each change
confirmed with what they have and mailed to the account; a company admin without a password deletes the company by confirming with a provider.

**Architecture:** GitHub is a third **provider name** (`github`) with its keys and health as in spec 0011. Its round trip runs through
OpenIddict's GitHub web provider (or, under the test issuer, a plain OAuth 2.0 registration of the fake's minimal discovery document); an event
handler of OpenIddict's client, **`GitHubReader`**, runs right after the token response, reads `/user` and `/user/emails` through a named
`HttpClient` of its own (one attempt, 10 seconds, 64 KiB), revokes the token in a `finally`, removes it from the context and hands the identity
to the callback in the client's authentication properties; **`GitHubIdentity`** applies the rules (the decimal `id`, the primary address
only). Every new session now has a row in **`SessionStates`** (keyed by its OpenIddict authorization, `ON DELETE CASCADE`), which
**`AtomicSignInResult`** writes when it creates the authorization itself: the start and the end of the **confirmation window** (5 minutes).
**`DeviceSessions`** finds the session of this device from the refresh cookie through OpenIddict's token manager, never redeeming it, and
re-reads it under the address lock. `GET /auth/me` says `confirmed_until`. Four settings endpoints: **`POST /auth/account/confirm`** and
**`POST /auth/providers/connect`** (plain `POST`s) and **`POST /auth/account/logins/unlink`** and **`POST /auth/account/password`**
(token-endpoint requests with a placeholder handler of their own, **`SettingsRequestHandler`**, which hand this device a renewed session through
**`SessionRenewal`**: every session ends, this device gets a new one with the old start and window). A ticket purpose `connect`. Three notice
mails (**`MailNotices`**: written in the transaction of their change, a limit of ten an hour per address, no link). `DELETE /auth/org` takes a
`reauth` ticket, used up inside `CompanyDeletionService` after the company lock. The fake provider gains GitHub (**`FakeGitHub`**); a
Development-only endpoint ends a window for browser tests. The sample gets `/settings`, the confirmation dialog, "Delete company" on `/team`, and
notes-api `DELETE /api/org`.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, ASP.NET Identity, OpenIddict 7.7.1 (server, validation and client; the GitHub web provider is
in `OpenIddict.Client.WebIntegration`, which the existing `OpenIddict.AspNetCore` metapackage brings, so **no `PackageReference` is added** to
`Auth.Server`; `IHttpClientFactory` is in the shared framework), EF Core on PostgreSQL 16 (Npgsql), xUnit v3 with Testcontainers; Angular 21
(standalone components, signals, reactive forms), Vitest on jsdom and Playwright (Chromium and WebKit); Python 3.12 with FastAPI and pytest for
notes-api; bash and Python 3 (standard library only) for the e2e script. **No new third-party NuGet, npm or pip package.**

## Global Constraints (changed by Amendment B at the end of this plan)

Values copied from the spec, one line each; every task includes them.

- **General rules:** JSON bodies of at most 8 KiB in UTF-8, unknown members ignored, `400 invalid_request` for a body that is not a JSON object or a member of the wrong type; `Cache-Control: no-store`; the security headers; the `415` of another charset and the per-IP `429` come first.
- **Provider names:** `google`, `microsoft` and `github`, lower case, in paths, the manifest, answers and audit rows; display names `Google`, `Microsoft`, `GitHub`.
- **Manifest:** `providers` accepts `github`; any other value makes the manifest invalid; duplicates folded, the order kept (the order of the buttons).
- **Configuration:** `Auth:Providers:GitHub:ClientId`/`ClientSecret` (`AUTH_GITHUB_CLIENT_ID`/`AUTH_GITHUB_CLIENT_SECRET`, optional; both set or not configured); `Auth:Testing:WindowEndpoint` (Development only; `true` maps `POST /auth/testing/end-confirmation`; set outside Development it stops the start naming the key).
- **Offered:** listed, both keys, the return address (spec 0011); a listed GitHub without its keys makes health `Degraded` with one warning naming the keys.
- **Callback:** `{origin of the return address}/auth/providers/github/callback`, registered as the "Authorization callback URL" of a GitHub **OAuth App**.
- **GitHub start:** code flow, `state`, PKCE `S256` in both modes (Amendment A7), scope `user:email`, no `openid`, no `nonce`, `prompt=select_account` for every purpose, never `max_age`; Google keeps `max_age=0` on a reauth, Microsoft `prompt=login`.
- **GitHub `iss`:** required on every authorization answer of the web registration (`https://github.com/login/oauth`); missing is `provider_error`, `access_denied` included (Amendment A6).
- **The reader:** after the token response, before the callback; `GET /user` and `GET /user/emails?per_page=100` with `Accept: application/vnd.github+json` and `User-Agent: Auth-Core`; one attempt per call, 10 seconds per call, 64 KiB per body; revocation `DELETE /applications/{client_id}/token` (Basic, `{"access_token"}`) in a `finally`, on a fresh 10-second bound, never the request's token; a revocation that is not `204` logs one warning naming the provider and the status; the token removed from the context afterwards (Amendment A3, A4).
- **GitHub identity:** key = `id` of `/user` as a decimal string (a positive integer, never `login`); address = the one entry of `/user/emails` with `primary` `true` (none or several = no address), verified when its `verified` is `true`, never `email` of `/user`; the invitation rule makes an address unverified; any answer that is not `200`, not the documented JSON, larger than 64 KiB, slower than 10 seconds, or a `/user` without `id`: `provider_error`; a token answer without `access_token`: `provider_error`.
- **Sessions:** table `SessionStates` (`AuthorizationId` → `OpenIddictAuthorizations`, cascade; `SessionStart`; `ConfirmedUntil` nullable); a new session: start = now, window = now + 5 minutes; a refresh keeps both and never moves the window; a session from before this version: start = its authorization's creation, window closed (also after a renewal).
- **The session of this device:** the refresh cookie's token, of the type refresh, `valid` or `redeemed` within 15 seconds, not expired, its authorization `valid`, its subject the access token's `sub`; read, never redeemed; otherwise `401 {"error":"session_required"}`, after the access token's plain `401`; no membership `403 no_membership`; re-read under the address lock.
- **The window:** 5 minutes from a sign-in that starts the session (login, provider sign-in, link, finish, invitation through a provider, sign-up completion) or from `POST /auth/account/confirm`; bound to one session; covers the connect and the unlink only; `GET /auth/me` `confirmed_until` (UTC, `yyyy-MM-ddTHH:mm:ssZ`) while in the future, else `null`.
- **Confirm:** `{"password"}` or `{"provider_ticket"}`: body; session and membership; fit (`password_required`, `reauth_required`, nothing counted); a password counted first, `429 too_many_attempts`/`403 wrong_password` with `account.confirm_refused` (`locked`/`wrong_password`, `action` `confirm`), right clears the streak; a ticket `reauth`, live, with `auth_pt`, of a login of this account, else `403 reauth_failed` with `account.confirm_refused` (`reauth_failed`), nothing used up, the lockout not consulted; then under the lock: the window opened (values written when missing), the ticket used up and `auth_pt` cleared, `account.confirmed` (`method`), `204`.
- **Connect:** `{"ticket"}`, the `connect` purpose (`smallint` 4, 10 minutes): body; session and membership; ticket (`400 invalid_ticket`); window (`403 confirmation_required`, ticket kept); identity of this account `204` (ticket used up, nothing else); of another `409 provider_already_linked`; this provider already linked `409 provider_in_use`; `403 no_email` / `email_not_verified` / `address_mismatch`; then under the lock (5 to 7 again): login, `provider.linked` (`via` `settings`), ticket used up, `auth_pt` cleared, mail "provider connected", `204`; sessions untouched.
- **Unlink:** `{"provider"}`: body; session and membership; window; `404 not_found` (no such name or no login of it); `409 last_login` (no password and no login of another offered provider would remain); then under the lock (2, 4, 5 again): every login of the provider deleted, every refresh token and authorization of the account revoked (not the email links, not the streak), a renewed session (old start and window), `provider.unlinked` and `login.succeeded` (`via` `session_renewal`), mail "provider disconnected"; `200` as a login.
- **Password:** `{"current_password","new_password"}`: body; session and membership; `403 password_not_set` (not counted); `400 weak_password` with `rules` (not counted); the current password counted, `429`/`403 wrong_password` with `account.confirm_refused` (`action` `password`); then under the lock in the order of Amendment A2: email links deleted, the hash read `FOR SHARE` and compared (changed meanwhile, a reset that committed while the change waited at the links: `403 wrong_password`, not counted again; a reset that committed before the change took the address lock ended its session: `401 session_required`, nothing written, decision D42), new hash, tokens and authorizations revoked, `password.changed`, `login.succeeded` (`via` `session_renewal`), mail "password changed", renewed session; `200` as a login. The streak is cleared when the right password is recognised, outside the transaction.
- **Renewal:** principal built after the change (the new stamp), `session_start` the stored start, refresh lifetime `SessionPolicy.RemainingLifetime(start, now)`; past the 30-day cap `401 session_required`, nothing changed; revoke first, then issue.
- **Link (spec 0011):** after the right password and step 7, an account with a login of the ticket's provider `409 provider_in_use` (ticket kept, nothing else written; again under the lock); a link queues "provider connected" and writes `provider.linked` `via` `sign_in`; the exchange and the link read the login row `FOR SHARE` in the sign-in's transaction (gone: `400 invalid_ticket`).
- **`DELETE /auth/org`:** `name` and exactly one of `password`/`provider_ticket`; fit before the lockout (`password_required`, `reauth_required`); a bad ticket `403 reauth_failed` with `org.delete_refused` (`reauth_failed`), nothing used up; the ticket used up inside `CompanyDeletionService` after the company lock and before the name check, with `provider.reauthenticated`; the answer clears `auth_pt`.
- **Notice mails:** `ProviderLinked`, `ProviderUnlinked`, `PasswordChanged`; Polish and English; to the account's address; the provider's name in the row (`MailRequests.Provider`, nullable, 20); written in the change's transaction, not through `MailRequestStore`; not counted by spec 0004's limit; a limit of their own: 10 per address in a fixed one-hour window, no gap, its own key (Amendment A10); beyond it the change happens, no row, one warning naming the kind; sent to an account of the address confirmed or not; no link, no token; none for a reset, a finish, an invitation through a provider, a connect of an identity already linked.
- **Limits:** confirm, connect and password against `login`; unlink and the testing endpoint against `general`; a password at confirm and at the password change counts in the login streak; a provider confirmation neither counts nor clears it.
- **Audit:** 32 kinds: `account.confirmed` (`method`), `account.confirm_refused` (`reason`, `action`), `provider.unlinked` (`provider`), `password.changed`; `provider.linked` gains `via`; `login.succeeded` gains `via` `session_renewal`; `provider.reauthenticated` also from `DELETE /auth/org`; `org.delete_refused` reason `reauth_failed`; no row holds a password, a ticket, a cookie, a GitHub code or token.
- **OpenAPI:** 40 operations, 28 under the charset guard; the testing endpoint is not listed.
- **Python package:** no change. **notes-api:** `DELETE /api/org` (`org_kind` `organization` and `org:delete`: `204`; else `403`); `auth.yaml` lists the three providers.
- **Workflow** ([`docs/workflow.md`](../../workflow.md)): local only, Conventional Commits in English, documentation in English. **The orchestrator makes the commits**: implementers leave their changes uncommitted, never run a git command that changes state, and end with the "Hand back" step (files and a proposed subject, never a git command). One Sonnet implementer per task writes the code of that task, runs it and fixes what breaks; the code below is the plan's, written on paper, and the implementer's tests decide.
- **The EF migration** (Task 1) is created with `dotnet ef migrations add AddSessionStatesAndNotificationMails` (the local tool); its id is the orchestrator's business afterwards, and no test or document names it.
- **.NET on this machine:** the build gate is `"C:/Program Files/dotnet/dotnet.exe" build Auth-Core.slnx -warnaserror -v q -nologo -maxcpucount:2`; tests run as `tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.<Class> [-class ...]` after that build (`dotnet test` at the root finds no tests). Docker must be running (Testcontainers). The analyzers are strict under `-warnaserror`: `ArgumentNullException.ThrowIfNull`, explicit `StringComparison` (CA1307/CA1309/CA1310), `CultureInfo.InvariantCulture` (CA1305), `[LoggerMessage]` for logs (CA1848), no constant array as an argument (CA1861: use a `static readonly` field), and `Properties` is ambiguous between OpenIddict's namespaces (CS0104: alias the client's as `ClientProperties`).
- **Implementers run only their task's test classes and the classes their task edits** (`-maxThreads 2`). They never run the whole .NET suite, a live e2e script or Playwright: the verifiers do.
- **Python:** `C:/p6v/Scripts/python.exe` (the `python3` on `PATH` is a Store shim); notes-api's tests: from `samples/notes-api`, `C:/p6v/Scripts/python.exe -m pytest -q`.
- **Angular:** in `samples/notes-web`: `npm ci` once when `node_modules` is missing, then `npm test`, `npx ng build` and `npm run check:docs`. Texts are plain printable ASCII (`texts.spec.ts` checks it).
- **No `docker compose up`/`down` of any stack and no stopping of containers by implementers**; no task builds the fake's image (the e2e verifier does); every compose command of a script carries its own project name, never `auth-core`.
- **Line endings:** shell scripts are LF (the orchestrator marks a new one executable in the index); `.cs`, `.ts` and `.md` files keep the endings each file has (the C# and TypeScript sources and the plans are CRLF, the YAML files, `deploy/docker-compose.prod.yml`, `samples/notes-api` and the spec 0012 file LF, `deploy/.env.prod.example` CRLF). Git Bash `sed -i` strips the CR of a CRLF file: edit with the Edit tool, never with `sed -i`. A new file takes the endings of its neighbours (CRLF for `.cs` and `.ts`, LF for `.py` and `.sh`).
- **Unicode escapes stay out of the source.** A backslash followed by `u` and four hex digits typed into a tool parameter arrives as the character. The code below builds such text with `(char)0x...` or `char.ConvertFromUtf32(...)`, or (in TypeScript) moves a line that holds one by cut and paste, never by retyping it. After writing, each task's "Hand back" step scans the files it touched ("scan the touched files") with
  `C:/p6v/Scripts/python.exe -c "import sys,unicodedata as u; bad=[(f,i,hex(ord(c))) for f in sys.argv[1:] for i,c in enumerate(open(f,encoding='utf-8').read()) if (u.category(c)=='Cc' and c not in '\t\r\n') or 0xFDD0<=ord(c)<=0xFDEF or (ord(c)&0xFFFE)==0xFFFE or ord(c)>0xFFFF]; print(bad or 'clean')" <files>`:
  it must print `clean`.
- **Code comments never cite a plan task, the plan or a probe item**: they state the fact (the spec's sections and amendments may be named, as the code does today).

## Checked on this code before the plan was written

A probe of OpenIddict 7.7.1 ran experiments on this code in a throw-away clone (`.superpowers/sdd/0012-github-and-managing-logins/probe-report.md`);
none of its code is copied here, and the plan does not re-run them. What the plan relies on, by the probe's item:

- **V1** — a `ProcessAuthenticationContext` handler at `ResolveValidatedBackchannelTokens.Descriptor.Order + 1_000` has
  `context.BackchannelAccessToken`, runs before every built-in handler that could reject a GitHub round trip, may `Reject(ServerError, ...,
  uri)` (the callback then sees `provider_error` with the state's items intact), and hands values over in `context.Properties`, which OpenIddict
  copies into `result.Properties.Items` on success and failure alike. Userinfo off and no identity token: the round trip still succeeds with an
  empty principal. The handler must filter on `context.Registration.ProviderName`.
- **V2, V3** — the web registration sends `code_challenge`+`S256` and `prompt` as given; a test registration sends PKCE only when its
  configuration lists `S256` (the discovery document does).
- **V4** — a plain registration needs `Issuer`, and through discovery a `jwks_uri` with at least one key; `response_types_supported:["code"]`
  suffices; `iss` is checked only when advertised.
- **V5** — the authorization of a sign-in is created inside the sign-in's transaction; `AtomicSignInResult` may create it itself before
  `Results.SignIn` (`IOpenIddictAuthorizationManager.CreateAsync`, then `principal.SetAuthorizationId`), OpenIddict then makes no ad-hoc one,
  and a refresh keeps it; a table keyed by the authorization's id with `ON DELETE CASCADE` goes with it in `TokenPruner` and in the deletion
  statements; `FindByReferenceIdAsync(cookie)` finds the token without redeeming it (it hashes the reference id itself), the leeway check is
  ours; `TimeProvider` is the clock of OpenIddict and of the endpoints.
- **V6** — `.RequireAuthorization()` on a token-endpoint route gives the plain `401` of the company API; a placeholder `ExtractTokenRequestContext`
  handler at `+400` claims the new paths; `LoginResponseShaper.ShouldShape` must learn them.
- **V7** — OpenIddict's per-registration `HttpClient` carries a retry pipeline (four attempts); a named client of our own makes one; the tests'
  routing reaches it once it matches the name.
- **V8** — `dotnet ef migrations add` makes the nullable `MailRequests.Provider` and the cascade table cleanly; `connect` needs no schema change.
- **U1** `iss` required at GitHub (Task 4: the fake sends it on `github.com`; Task 6: the test without it). **U2** the token reaches the callback's
  items unless removed (Task 5: both assignments after the revocation). **U3** a `200` token answer without `access_token` (Task 5: the reader
  refuses; Task 6: its test). **U4** `max_age=0` for any non-Microsoft reauth (Task 6: Google only). **U5** `ProviderSettings._missing` is a
  dictionary: the `github` entry in `Load` (Task 3). **U6** the retry pipeline (Task 5: the named client; Task 6: one `/user` and one revocation
  counted). **U7** the manager hashes the reference id: never compare `ReferenceId` in SQL (Task 2). **U8** the `Properties` approach needs the
  optimistic token: the table is used instead (Task 1). **U9** an unsupported 2xx token body is logged verbatim by OpenIddict at `fail:` (Task 4:
  the fake answers JSON when asked for JSON; Task 20: the residual risk 63 names it). **U10** the plain `401` has no body; `session_required` comes
  after it (Task 2). **U11** only the unlink and the password change are token-endpoint paths (Tasks 7, 9, 10, 12). **U12** the revocation never
  uses the request's token (Task 5). **U13** GitHub's documented shapes (Tasks 4 and 5). **U14** the deadlock of the spec's first order (Task
  12: Amendment A2's order and its three race tests). **U15** `AtomicSignInResult` takes the session values (Task 1).
- **I7, I13** — the counts: 28 audit kinds today, 36 operations and 24 under the charset guard; the charset guard covers the new `POST`s
  without a change.

Not proved on paper (each has its test in the task named): that OpenIddict's GitHub web provider asks for no scope of its own besides
`user:email` (Task 6, `GitHubStartTests`); that a real GitHub honours Basic authentication on the revocation endpoint (the owner's real-keys
test); that a `Lax` state cookie survives GitHub's redirect in Chromium and WebKit (Task 19).

## Review Focus

The inputs and failure modes the spec implies that a person using this software is most likely to meet, most likely first; each has its test in
the task named.

1. **A person who disconnects a provider and keeps working on this device while another device is signed out.** The answer hands this device a
   new session with the old start and window, and every other refresh fails
   (`ProviderUnlinkTests.An_unlink_with_an_open_window_deletes_the_login_signs_the_other_devices_out_and_renews_this_one`,
   `ProviderUnlinkTests.A_renewed_session_keeps_the_cap_of_its_start_and_one_past_its_cap_is_401_session_required`; Task 10).
2. **A person who changes the password while a reset of the same account is on its way** (an old mail link clicked in another tab). The first
   one wins, never a `500`
   (`AccountPasswordTests.A_reset_that_used_its_link_up_first_wins_and_the_change_is_403_wrong_password_not_counted_again`,
   `AccountPasswordTests.A_reset_that_committed_before_the_change_took_the_address_lock_ended_its_session_and_the_change_is_401_session_required`,
   `AccountPasswordTests.A_change_that_voided_the_links_first_wins_and_the_reset_is_400_invalid_token`; Task 12).
3. **GitHub answering slowly, oddly or not at all.** The callback answers `provider_error` within the bound and the token is still revoked once
   (`GitHubCallbackErrorTests.A_user_answer_slower_than_ten_seconds_is_provider_error_and_the_token_is_revoked`,
   `GitHubCallbackErrorTests.A_user_answer_that_is_not_200_is_provider_error_after_one_attempt_and_the_token_is_revoked`,
   `GitHubCallbackErrorTests.A_failed_revocation_still_makes_the_ticket_and_logs_one_warning_without_the_token`; Task 6).
4. **A person with several GitHub addresses whose primary one is not the invited or the account's address.** Only the primary address counts
   (`GitHubIdentityTests.Only_the_primary_entry_is_the_address_and_a_verified_secondary_is_never_used`, Task 5;
   `GitHubFlowTests.An_invitation_through_a_github_account_whose_primary_address_is_another_is_403_address_mismatch`, Task 14;
   `ProviderConnectTests.An_address_that_is_not_the_accounts_is_403_address_mismatch_and_the_ticket_stays`, Task 9).
5. **A person who confirms on one device and expects it to count on another, or whose window closes during a round trip.** The window is bound to
   the session, and a `connect` ticket survives a `confirmation_required`
   (`ConfirmationWindowTests.A_confirmation_in_one_session_leaves_another_session_unconfirmed`, Task 7;
   `ProviderConnectTests.A_closed_window_is_403_confirmation_required_and_the_ticket_stays_usable`, Task 9).

## File Structure

New and changed files by responsibility. Everything under `src/Auth.Server/` unless said otherwise; tests under `tests/Auth.IntegrationTests/`.

| Area | Files | Responsibility |
| --- | --- | --- |
| Session values | `src/Auth.Infrastructure/Persistence/SessionState.cs`, `AuthDbContext.cs`, `MailRequest.cs`, `MailKind.cs`, `ProviderTicketPurpose.cs`, the migration, `Sessions/SessionValues.cs`, `Login/AtomicSignInResult.cs`, `Sessions/SessionPrincipal.cs` | the table, the new values, a session that opens its window (Task 1) |
| The session of this device | `Sessions/DeviceSessions.cs`, `Api/OrgEndpoints.cs`, `Tenancy/Contracts.cs`, `Api/TenancyEndpoints.cs`, `Testing/TestingSettings.cs`, `Testing/TestingEndpoints.cs`, `Tenancy/Outcome.cs`, `Program.cs` | `confirmed_until`, the testing endpoint (Task 2) |
| GitHub as a name | `Providers/ProviderNames.cs`, `Providers/ProviderSettings.cs`, `Tenancy/ManifestParser.cs`, `tests/Auth.FakeOidc/compose.yml`, `samples/notes-api/auth.yaml`, `deploy/auth.yaml`, `deploy/docker-compose.prod.yml`, `deploy/.env.prod.example` | keys, health, manifest (Task 3) |
| The fake GitHub | `tests/Auth.FakeOidc/FakeGitHub.cs`, `FakeOidcServer.cs`, `FakePeople.cs` | GitHub's hosts and the test issuer's `/github` (Task 4) |
| The reader | `Providers/GitHubIdentity.cs`, `Providers/GitHubReader.cs`, `Providers/ProviderIdentities.cs`, `Providers/ProviderClientSetup.cs` | the identity, the reads, the revocation, both registrations (Task 5) |
| GitHub round trip | `Providers/ProviderStartEndpoint.cs`, `Providers/ProviderCallbackEndpoint.cs` | the start's prompt, the callback's identity (Task 6) |
| Confirm | `Account/SettingsCallers.cs`, `Account/AccountConfirmEndpoint.cs`, `Api/SettingsRoutes.cs`, `Api/AccountContracts.cs`, `Audit/AuditKinds.cs` | the window opened by a confirmation (Task 7) |
| Notices and the link | `Email/MailNotices.cs`, `Email/MailLimitPolicy.cs`, `Email/MailLimits.cs`, `Email/MailDispatcher.cs`, `Email/MailComposer.cs`, `Email/MailTexts.cs`, `Providers/ProviderLinkEndpoint.cs`, `Providers/ProviderSignIn.cs`, `Providers/ProviderLogins.cs` | the three mails, `provider_in_use` (Task 8) |
| Connect | `Providers/ProviderPurposes.cs`, `Providers/ProviderStartEndpoint.cs`, `Providers/ProviderConnectEndpoint.cs`, `Providers/ProviderPaths.cs`, `Providers/ProviderExchangeEndpoint.cs`, `Api/SettingsRoutes.cs`, `Api/ProviderRoutes.cs` | Task 9 |
| Unlink | `Account/SettingsRequestHandler.cs`, `Account/SessionRenewal.cs`, `Account/LoginUnlinkEndpoint.cs`, `Login/LoginResponseShaper.cs`, `Tokens/OpenIddictSetup.cs` | Task 10 |
| A sign-in holds its login | `Providers/ProviderSignIn.cs`, `Providers/ProviderLogins.cs` | Task 11 |
| Password | `Account/AccountPasswordEndpoint.cs` | Task 12 |
| Company deletion | `Api/OrgEndpoints.cs`, `Api/TenancyEndpoints.cs`, `Tenancy/CompanyDeletionService.cs`, `Tenancy/Contracts.cs` | Task 13 |
| Limits | `RateLimiting/RatePolicy.cs` | Task 14 |
| End to end | `scripts/e2e-providers.sh`, `scripts/e2e-settings.sh`, `scripts/e2e-web.sh` (comment only) | Task 15 |
| Sample frontend | `samples/notes-web/src/app/account-api.ts`, `auth/auth.service.ts`, `auth/auth.interceptor.ts`, `texts.ts`, `app.routes.ts`, `pages/settings.ts`, `pages/confirm-dialog.ts`, `pages/delete-account.ts`, `pages/delete-company.ts`, `pages/provider-buttons.ts`, `pages/provider-return.ts`, `pages/provider-link.ts`, `pages/notes.ts`, `pages/team.ts`, `pages/ticket-history.ts`, `pages/login.ts`, `pages/password-input.ts`, `src/testing/helpers.ts` and their specs | Tasks 16, 17, 18 |
| Sample backend | `samples/notes-api/src/notes_api/app.py`, `samples/notes-api/tests/test_delete_org_data.py` | Task 18 |
| Browser check | `samples/notes-web/e2e/12-settings.spec.ts`, `e2e/support/*.ts`, `e2e/10-private.spec.ts`, `e2e/11-providers.spec.ts` | Task 19 |
| Documents | `docs/deployment/provider-keys.md`, `docs/security/threat-model.md`, `docs/integration/angular.md`, `docs/deployment/vps.md`, `docs/design.md`, `CHANGELOG.md`, `samples/notes-web/README.md` | Task 20 |

## The tasks

| Day | Task | Deliverable |
| --- | --- | --- |
| Wed 06.05 | 1 | Session values in the schema; every new session records its start and opens its window |
| Wed 06.05 | 2 | The session of this device; `confirmed_until`; the Development testing endpoint; the upgrade from spec 0011 |
| Wed 06.05 | 3 | GitHub as a provider name: keys, health, manifest, the shipped configuration |
| Wed 06.05 | 4 | The fake GitHub |
| Fri 08.05 | 5 | The GitHub identity rules, the reader and the two registrations |
| Fri 08.05 | 6 | The GitHub start and callback, and every error path |
| Fri 08.05 | 7 | `POST /auth/account/confirm`; the new error codes and audit kinds |
| Fri 08.05 | 8 | The notice mails; the link's `provider_in_use`, mail and `via` |
| Sat 09.05 | 9 | Connect: the `connect` purpose and `POST /auth/providers/connect` |
| Sat 09.05 | 10 | `POST /auth/account/logins/unlink` and the renewed session |
| Sat 09.05 | 11 | A sign-in holds the login it signs in with; the races with the unlink |
| Sat 09.05 | 12 | `POST /auth/account/password` |
| Sat 09.05 | 13 | `DELETE /auth/org` with a provider ticket |
| Mon 11.05 | 14 | The limits, the audit flows, GitHub through every flow, the last API checks |
| Mon 11.05 | 15 | `scripts/e2e-settings.sh` and the earlier e2e script |
| Mon 11.05 | 16 | The sample: GitHub buttons, the settings calls, `/settings` and its password section |
| Mon 11.05 | 17 | The sample: the confirmation dialog, connect, disconnect, the return screen's targets |
| Wed 13.05 | 18 | The sample: "Delete account" on `/settings`, "Delete company" on `/team`; notes-api `DELETE /api/org` |
| Wed 13.05 | 19 | The Playwright spec `12-settings.spec.ts` and the moved steps of 0010 and 0011 |
| Wed 13.05 | 20 | The documents and the acceptance map check |
| Fri 15.05 | — | Final review and fixes (outside this plan) |
| Sat 16.05 | — | The three verifiers, the "As built", the merge (outside this plan, owner-gated) |

Tue 05, Thu 07, Sun 10, Tue 12, Thu 14 and Sun 17 are off. Tasks run in order inside a day and across days; each leaves the build gate and the
tests of the files it touches green, and each task's tests are written first and seen to fail.

**Contract counts that change** (tests that enumerate the contract; each task that changes one says so in its "Contract counts" line):
`OpenApiTests` and `OpenApiHardeningTests` (Task 2: `MeResponse` and the cookie of `GET /auth/me`; Task 7: 36 → 37 operations, 24 → 25 under
the charset guard; Task 9: 38 and 26; Task 10: 39 and 27; Task 12: 40 and 28; Task 13: the body and the cookie of `DELETE /auth/org`),
`AuditTableTests` and `AuditProviderFlowsTests` (Task 7: 28 → 32 kinds; Task 8: `provider.linked` gains `via`), `OrgEndpointTests` (Task 2:
`/auth/me` members), `MailKindTests` (Task 1), `ProviderErrorCodesTests` (Tasks 2 and 7), the manifest's names (`ManifestProvidersTests`,
`ProviderOptionsTests`, `ProviderPathsTests`, `ProviderStartTests`, `ProviderCallbackErrorTests`, `FakeOidcTests`: Tasks 3 and 4),
`ShippedConfigurationTests` (Task 3), `RatePolicyTests`, `RateLimitMiddlewareTests` and `JsonCharsetTests` (Task 14), `scripts/e2e-providers.sh`
(Task 15: three providers), the sample's specs (Tasks 16 to 18) and the Playwright specs of 0010 and 0011 (Task 19).

---

## Day 1 — Wednesday 06.05: the session values, the session of this device, GitHub as a name, the fake GitHub

When the day is done, every new session records its start and its confirmation window, `GET /auth/me` says `confirmed_until` for the session of
this device, the Development stacks can end a window by an endpoint of their own, the manifest and the settings know GitHub, and the fake
provider answers as GitHub. Nothing signs in with GitHub yet.

### Task 1: Session values in the schema; every new session records its start and opens its window

**Files:**
- Create: `src/Auth.Infrastructure/Persistence/SessionState.cs`, `src/Auth.Server/Sessions/SessionValues.cs`, the migration
  `AddSessionStatesAndNotificationMails` (generated)
- Modify: `src/Auth.Infrastructure/Persistence/AuthDbContext.cs`, `src/Auth.Infrastructure/Persistence/MailRequest.cs`,
  `src/Auth.Infrastructure/Persistence/MailKind.cs`, `src/Auth.Infrastructure/Persistence/ProviderTicketPurpose.cs`,
  `src/Auth.Server/Login/AtomicSignInResult.cs`, `src/Auth.Server/Sessions/SessionPrincipal.cs`
- Create (tests): `tests/Auth.IntegrationTests/SessionStateTableTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/MailKindTests.cs`, `tests/Auth.IntegrationTests/ProviderTicketTableTests.cs`

**Interfaces:**
- Consumes: `IOpenIddictAuthorizationManager`, `StorableTime`, `SessionPolicy.StartClaim`, `SessionPolicy.RemainingLifetime`.
- Produces: the entity `SessionState` (`AuthorizationId`, `SessionStart`, `ConfirmedUntil`) and `AuthDbContext.SessionStates`;
  `MailRequest.Provider`; `MailKind.ProviderLinked` (5), `ProviderUnlinked` (6), `PasswordChanged` (7), `NoticeLimit` (100);
  `ProviderTicketPurpose.Connect` (4); `SessionValues(DateTimeOffset Start, DateTimeOffset? ConfirmedUntil)` with
  `SessionValues.Opened(DateTimeOffset start, DateTimeOffset now)`; `ConfirmationWindow.Lifetime` (5 minutes) and
  `ConfirmationWindow.Format(DateTimeOffset)`; `AtomicSignInResult(..., SessionValues? session = null)`;
  `SessionPrincipal.Create(..., DateTimeOffset? start = null)`.

**Contract counts:** `MailKindTests` (the stored values).

**Decisions the plan takes here:**
- The table, not the authorization's `Properties` (Amendment A8; probe U8): typed values, one `UPDATE` to open a window, and the cascade is the
  database's. `AuthorizationId` is `text`, as OpenIddict's key.
- `AtomicSignInResult` creates the authorization when the principal carries none (every new session; a refresh carries its own), after the
  account is read under its share lock and before `Results.SignIn`: a sign-in that is refused or rolled back takes the authorization and the
  row with it. Without values from the caller the start is the principal's `session_start` (seconds, as the refresh token says it) and the
  window ends five minutes after the clock of the sign-in; a renewal passes the values it keeps (Task 10).
- `MailKind.NoticeLimit` (100) is the key of the notice limit (Amendment A10) in `MailRequestLimits`, never the kind of a queued row; the enum
  says so. The three notice kinds are 5 to 7, after the four that are stored.
- `SessionPrincipal.Create` takes the start of a renewal; with it the refresh lifetime is what is left before the cap, and a session past the
  cap is a caller's error (the endpoints answer `401 session_required` before they get here, Task 10).

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/SessionStateTableTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → Sessions (Amendment A8): the table <c>SessionStates</c> as the migration makes it, and the row every new session writes.</summary>
public sealed class SessionStateTableTests : TenancyTestBase
{
    private const string Ola = "ola@poczta.test";

    public SessionStateTableTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The pruning loops wait on the fake clock: a test that moves it by two weeks drives the pass by hand.
        Factory.WithoutHostedService<TokenPruningService>().WithoutHostedService<LockoutPruningService>();
    }

    private sealed record SessionRow(string Id, string? Status, string? Type, DateTimeOffset? Start, DateTimeOffset? Until);

    private Task<List<string>> StringsAsync(FormattableString sql) =>
        InDbAsync(db => db.Database.SqlQuery<string>(sql).ToListAsync(TestContext.Current.CancellationToken));

    /// <summary>The authorizations of the account, oldest first, each with its row of <c>SessionStates</c> (null values without one).</summary>
    private async Task<List<SessionRow>> SessionsOfAsync(Guid userId)
    {
        var subject = userId.ToString();
        var rows = await InDbAsync(db => (
            from authorization in db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking()
            where authorization.Subject == subject
            orderby authorization.CreationDate
            select new
            {
                authorization.Id,
                authorization.Status,
                authorization.Type,
                Start = db.SessionStates.Where(s => s.AuthorizationId == authorization.Id).Select(s => (DateTimeOffset?)s.SessionStart).FirstOrDefault(),
                Until = db.SessionStates.Where(s => s.AuthorizationId == authorization.Id).Select(s => s.ConfirmedUntil).FirstOrDefault(),
            }).ToListAsync(TestContext.Current.CancellationToken));
        return [.. rows.Select(r => new SessionRow(r.Id!, r.Status, r.Type, r.Start, r.Until))];
    }

    private Task<int> RowCountAsync() => InDbAsync(db => db.SessionStates.CountAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task The_migration_makes_the_table()   // Amendment A8
    {
        var columns = await StringsAsync(
            $"""
            SELECT column_name || ':' || data_type || ':' || is_nullable AS "Value"
            FROM information_schema.columns WHERE table_name = 'SessionStates' ORDER BY column_name
            """);

        Assert.Equal(
            ["AuthorizationId:text:NO", "ConfirmedUntil:timestamp with time zone:YES", "SessionStart:timestamp with time zone:NO"],
            columns);
    }

    [Fact]
    public async Task The_row_names_its_authorization_and_goes_with_it()   // spec 0012 → Sessions: the values cannot outlive their session
    {
        var keys = await StringsAsync(
            $"""
            SELECT confdeltype::text || ':' || confrelid::regclass::text AS "Value"
            FROM pg_constraint WHERE conrelid = '"SessionStates"'::regclass AND contype = 'f'
            """);
        var primary = await StringsAsync($"""SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'SessionStates' ORDER BY indexname""");

        Assert.Equal(["c:\"OpenIddictAuthorizations\""], keys);   // ON DELETE CASCADE
        Assert.Equal(["PK_SessionStates"], primary);
    }

    [Fact]
    public async Task The_mail_requests_have_a_nullable_provider_of_twenty_characters()   // spec 0012 → Mails, the queue
    {
        var column = await StringsAsync(
            $"""
            SELECT data_type || ':' || character_maximum_length::text || ':' || is_nullable AS "Value"
            FROM information_schema.columns WHERE table_name = 'MailRequests' AND column_name = 'Provider'
            """);

        Assert.Equal(["character varying:20:YES"], column);
    }

    [Fact]
    public async Task A_login_makes_the_sessions_authorization_with_a_row_of_its_start_and_a_window_of_five_minutes()   // criterion 8
    {
        var now = StorableTime.Now(Clock);

        await SessionApi.LoginAsync(Client, Factory);

        var session = Assert.Single(await SessionsOfAsync(await Factory.SeedUserIdAsync()));
        Assert.Equal(OpenIddictConstants.Statuses.Valid, session.Status);
        Assert.Equal(OpenIddictConstants.AuthorizationTypes.AdHoc, session.Type);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()), session.Start);   // the refresh token's session_start
        Assert.Equal(now + TimeSpan.FromMinutes(5), session.Until);
        Assert.Equal(TimeSpan.FromMinutes(5), ConfirmationWindow.Lifetime);
    }

    [Fact]
    public async Task A_refresh_keeps_the_authorization_its_start_and_its_window_and_never_extends_it()   // criterion 8
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        var before = Assert.Single(await SessionsOfAsync(await Factory.SeedUserIdAsync()));

        Clock.Advance(TimeSpan.FromMinutes(2));
        var refreshed = await SessionApi.RefreshOk(Client, login.RefreshToken);
        Clock.Advance(TimeSpan.FromMinutes(2));
        await SessionApi.RefreshOk(Client, refreshed.RefreshToken);

        Assert.Equal(before, Assert.Single(await SessionsOfAsync(await Factory.SeedUserIdAsync())));
    }

    [Fact]
    public async Task Two_logins_are_two_sessions_with_two_windows()   // criterion 9: bound to the session, not to the account
    {
        await SessionApi.LoginAsync(Client, Factory);
        Clock.Advance(TimeSpan.FromMinutes(3));
        await SessionApi.LoginAsync(Client, Factory);

        var sessions = await SessionsOfAsync(await Factory.SeedUserIdAsync());

        Assert.Equal(2, sessions.Count);
        Assert.Equal(TimeSpan.FromMinutes(3), sessions[1].Until - sessions[0].Until);
    }

    [Fact]
    public async Task A_logout_revokes_the_authorization_and_the_pruner_removes_it_with_its_row()   // spec 0012 → Sessions
    {
        var login = await SessionApi.LoginAsync(Client, Factory);
        using (var logout = await SessionApi.Logout(Client, login.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        Assert.Equal(OpenIddictConstants.Statuses.Revoked, Assert.Single(await SessionsOfAsync(await Factory.SeedUserIdAsync())).Status);
        Assert.Equal(1, await RowCountAsync());

        Clock.Advance(TimeSpan.FromDays(15));
        await Factory.Services.GetRequiredService<TokenPruner>().PruneOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await SessionsOfAsync(await Factory.SeedUserIdAsync()));
        Assert.Equal(0, await RowCountAsync());
    }

    [Fact]
    public async Task A_deleted_account_takes_its_sessions_and_their_rows_with_it()   // spec 0012 → Sessions: an account deletion deletes it
    {
        var (userId, _) = await CreatePrivateAccountAsync(Ola);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        Assert.Equal(1, await RowCountAsync());

        using var deleted = await TenancyApi.Send(Client, HttpMethod.Delete, "/auth/account", session.AccessToken, new { password = UserPassword });

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await SessionsOfAsync(userId));
        Assert.Equal(0, await RowCountAsync());
    }

    [Fact]
    public async Task A_wrong_password_makes_no_authorization_and_no_row()   // the row is written only with a session
    {
        using var login = await LoginApi.Login(Client, Factory.SeedEmail, "Wrong-Passw0rd-1");

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Empty(await SessionsOfAsync(await Factory.SeedUserIdAsync()));
        Assert.Equal(0, await RowCountAsync());
    }

    [Fact]
    public void A_window_is_written_in_utc_to_the_second()   // spec 0012 → GET /auth/me: ISO 8601 with seconds
    {
        var instant = new DateTimeOffset(2026, 5, 4, 20, 35, 7, 123, TimeSpan.FromHours(2));

        Assert.Equal("2026-05-04T18:35:07Z", ConfirmationWindow.Format(instant));
        Assert.Equal(new SessionValues(instant, instant + TimeSpan.FromMinutes(5)), SessionValues.Opened(instant, instant));
    }
}
```

In `tests/Auth.IntegrationTests/MailKindTests.cs`, replace

```csharp
        Assert.Equal((short)4, (short)MailKind.Signup);
    }
```

with

```csharp
        Assert.Equal((short)4, (short)MailKind.Signup);
        Assert.Equal((short)5, (short)MailKind.ProviderLinked);   // spec 0012 → Mails: the three notices
        Assert.Equal((short)6, (short)MailKind.ProviderUnlinked);
        Assert.Equal((short)7, (short)MailKind.PasswordChanged);
    }

    [Fact]
    public void The_key_of_the_notice_limit_is_a_value_of_its_own()   // spec 0012, Amendment A10: never the kind of a queued mail
    {
        Assert.Equal((short)100, (short)MailKind.NoticeLimit);
        Assert.Equal(8, Enum.GetValues<MailKind>().Length);
    }
```

In `tests/Auth.IntegrationTests/ProviderTicketTableTests.cs`, add after the method
`The_table_has_no_foreign_key_so_that_a_ticket_names_no_account`:

```csharp

    [Fact]
    public void The_purposes_are_stored_as_numbers_and_connect_is_the_fourth()   // spec 0012 → Provider tickets: `connect` (smallint 4)
    {
        Assert.Equal(
            [(short)1, (short)2, (short)3, (short)4],
            Enum.GetValues<ProviderTicketPurpose>().Select(p => (short)p));
        Assert.Equal((short)4, (short)ProviderTicketPurpose.Connect);
    }
```

- [ ] **Step 2: See them fail.** Build: the build fails (`SessionStates`, `ConfirmationWindow`, `SessionValues`, the new enum values do not exist).
That is the failure of this step.

- [ ] **Step 3: The entity and the values.** `src/Auth.Infrastructure/Persistence/SessionState.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>
/// What a session records beside its OpenIddict authorization (spec 0012 → Sessions, Amendment A8): when it began, which the 30-day cap counts
/// from and a renewal keeps, and until when it is confirmed (the confirmation window; <see langword="null"/>: closed). The row is keyed by the
/// authorization's id and goes with it (<c>ON DELETE CASCADE</c>): a pruned or deleted authorization takes its values along.
/// </summary>
public sealed class SessionState
{
    public required string AuthorizationId { get; init; }

    public required DateTimeOffset SessionStart { get; init; }

    public DateTimeOffset? ConfirmedUntil { get; set; }
}
```

In `src/Auth.Infrastructure/Persistence/MailRequest.cs`, replace

```csharp
    public Guid? InviteId { get; init; }
```

with

```csharp
    public Guid? InviteId { get; init; }

    /// <summary>
    /// The provider a notice names (spec 0012 → Mails: "provider connected" and "provider disconnected"); <see langword="null"/> for every other
    /// kind. Never a token: a notice carries no link.
    /// </summary>
    public string? Provider { get; init; }
```

In `src/Auth.Infrastructure/Persistence/MailKind.cs`, replace

```csharp
    Signup = 4,
}
```

with

```csharp
    Signup = 4,

    // Spec 0012 → Mails: the notices of a change to how the account signs in. No link, no token.
    ProviderLinked = 5,
    ProviderUnlinked = 6,
    PasswordChanged = 7,

    /// <summary>Never the kind of a queued mail: the key under which the three notices share their limit in <c>MailRequestLimits</c> (Amendment A10).</summary>
    NoticeLimit = 100,
}
```

In `src/Auth.Infrastructure/Persistence/ProviderTicketPurpose.cs`, replace

```csharp
    /// <summary>The confirmation of a self-deletion.</summary>
    Reauth = 3,
}
```

with

```csharp
    /// <summary>The confirmation of a self-deletion, of a company deletion and of the settings screen (spec 0012).</summary>
    Reauth = 3,

    /// <summary>Spec 0012: an identity to connect to the account that is signed in.</summary>
    Connect = 4,
}
```

In `src/Auth.Infrastructure/Persistence/AuthDbContext.cs`, replace

```csharp
    public DbSet<ProviderTicket> ProviderTickets => Set<ProviderTicket>();
```

with

```csharp
    public DbSet<ProviderTicket> ProviderTickets => Set<ProviderTicket>();

    public DbSet<SessionState> SessionStates => Set<SessionState>();
```

replace

```csharp
            request.HasKey(r => r.Id);
            // The dispatcher takes the rows that are due, oldest first.
            request.HasIndex(r => r.NextAttemptAt);
```

with

```csharp
            request.HasKey(r => r.Id);
            // The dispatcher takes the rows that are due, oldest first.
            request.HasIndex(r => r.NextAttemptAt);
            // Spec 0012: the provider a notice names, by its name (at most 20 characters, as a ticket's).
            request.Property(r => r.Provider).HasMaxLength(20);
```

and replace

```csharp
        // Spec 0008 → Audit log. The names are the contract's (snake_case): the backup runbook queries them. No foreign keys on purpose.
```

with

```csharp
        // Spec 0012 → Sessions: the values of a session, with its authorization and gone with it.
        builder.Entity<SessionState>(session =>
        {
            session.HasKey(s => s.AuthorizationId);
            session.HasOne<OpenIddictEntityFrameworkCoreAuthorization>().WithMany().HasForeignKey(s => s.AuthorizationId).OnDelete(DeleteBehavior.Cascade);
        });

        // Spec 0008 → Audit log. The names are the contract's (snake_case): the backup runbook queries them. No foreign keys on purpose.
```

and add `using OpenIddict.EntityFrameworkCore.Models;` after `using Microsoft.EntityFrameworkCore;` at the top of the file.

`src/Auth.Server/Sessions/SessionValues.cs`:

```csharp
using System.Globalization;

namespace Auth.Server.Sessions;

/// <summary>The confirmation window of spec 0012: 5 minutes, the lifetime of a <c>reauth</c> ticket (Settled 2).</summary>
public static class ConfirmationWindow
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>The end of a window as <c>GET /auth/me</c> says it: UTC, ISO 8601 with seconds, <c>2026-05-04T18:35:00Z</c>.</summary>
    public static string Format(DateTimeOffset until) =>
        until.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>What a session records with its authorization (spec 0012 → Sessions): its start and the end of its window (null: closed).</summary>
public sealed record SessionValues(DateTimeOffset Start, DateTimeOffset? ConfirmedUntil)
{
    /// <summary>A session a sign-in starts: it begins at <paramref name="start"/> and is confirmed for five minutes from <paramref name="now"/>.</summary>
    public static SessionValues Opened(DateTimeOffset start, DateTimeOffset now) => new(start, now + ConfirmationWindow.Lifetime);
}
```

- [ ] **Step 4: The migration.** From the repository root:
`"C:/Program Files/dotnet/dotnet.exe" tool run dotnet-ef migrations add AddSessionStatesAndNotificationMails --project src/Auth.Infrastructure --startup-project src/Auth.Server`.
Read the generated `Up`: one `AddColumn<string>` `Provider` on `MailRequests` (`character varying(20)`, nullable) and one `CreateTable` of
`SessionStates` with the three columns, the key `PK_SessionStates` and the foreign key to `OpenIddictAuthorizations` with
`ReferentialAction.Cascade`, and nothing else (a change to another table means the model and the snapshot disagree: stop and report). Keep the
generated files' line endings (CRLF, as the earlier migrations).

- [ ] **Step 5: A new session opens its window.** In `src/Auth.Server/Login/AtomicSignInResult.cs`:

(a) replace

```csharp
using Auth.Server.Audit;
```

with

```csharp
using System.Globalization;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Sessions;
```

(b) replace

```csharp
/// <param name="whenCommitted">
/// Runs once the sign-in is committed, before the held answer is written: the answer has not started, so it may still set a header (a sign-in
/// through a provider clears <c>auth_pt</c> there, spec 0011). It never runs for an answer that is rolled back.
/// </param>
public sealed class AtomicSignInResult(
    ClaimsPrincipal principal, AuditEntry? entry, IDbContextTransaction? opened = null, Func<Task<IResult>>? whenGone = null,
    Action<HttpContext>? whenCommitted = null) : IResult
```

with

```csharp
/// <param name="whenCommitted">
/// Runs once the sign-in is committed, before the held answer is written: the answer has not started, so it may still set a header (a sign-in
/// through a provider clears <c>auth_pt</c> there, spec 0011). It never runs for an answer that is rolled back.
/// </param>
/// <param name="session">
/// Spec 0012 → Sessions: the values a new session records. A sign-in leaves it out (the session starts at the principal's start and is confirmed
/// for five minutes); a renewal passes the start and the window of the session it replaces. A refresh, whose principal carries its
/// authorization, records nothing.
/// </param>
public sealed class AtomicSignInResult(
    ClaimsPrincipal principal, AuditEntry? entry, IDbContextTransaction? opened = null, Func<Task<IResult>>? whenGone = null,
    Action<HttpContext>? whenCommitted = null, SessionValues? session = null) : IResult
```

(c) replace

```csharp
                else
                {
                    await Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)
                        .ExecuteAsync(httpContext);
```

with

```csharp
                else
                {
                    // Spec 0012 → Sessions: a new session gets its authorization here, with its row, inside this transaction; OpenIddict then
                    // makes no ad-hoc one. A refresh carries the authorization of its session and keeps it.
                    if (string.IsNullOrEmpty(principal.GetAuthorizationId()))
                    {
                        await OpenSessionAsync(httpContext, db, principal, session, httpContext.RequestAborted);
                    }

                    await Results.SignIn(principal, properties: null, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)
                        .ExecuteAsync(httpContext);
```

(d) replace

```csharp
    private static Guid AccountIdOf(ClaimsPrincipal principal) =>
```

with

```csharp
    /// <summary>
    /// The authorization of a new session and its row in <c>SessionStates</c> (Amendment A8). The row is saved with the tokens, in the
    /// transaction of the sign-in: a sign-in that is refused or rolled back leaves neither.
    /// </summary>
    private static async Task OpenSessionAsync(
        HttpContext http, AuthDbContext db, ClaimsPrincipal principal, SessionValues? given, CancellationToken cancellationToken)
    {
        var clock = http.RequestServices.GetRequiredService<TimeProvider>();
        var authorizations = http.RequestServices.GetRequiredService<IOpenIddictAuthorizationManager>();
        var now = StorableTime.Now(clock);
        var values = given ?? SessionValues.Opened(StartOf(principal), now);
        var authorization = await authorizations.CreateAsync(
            new OpenIddictAuthorizationDescriptor
            {
                CreationDate = now,
                Status = OpenIddictConstants.Statuses.Valid,
                Subject = principal.GetClaim(OpenIddictConstants.Claims.Subject),
                Type = OpenIddictConstants.AuthorizationTypes.AdHoc,
            },
            cancellationToken);
        var id = await authorizations.GetIdAsync(authorization, cancellationToken)
            ?? throw new InvalidOperationException("The authorization of the session has no id.");
        db.SessionStates.Add(new SessionState { AuthorizationId = id, SessionStart = values.Start, ConfirmedUntil = values.ConfirmedUntil });
        principal.SetAuthorizationId(id);
    }

    /// <summary>The start the principal carries for the refresh token (Unix seconds).</summary>
    private static DateTimeOffset StartOf(ClaimsPrincipal principal) =>
        long.TryParse(principal.GetClaim(SessionPolicy.StartClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : throw new InvalidOperationException("The principal of a new session has no start.");

    private static Guid AccountIdOf(ClaimsPrincipal principal) =>
```

- [ ] **Step 6: The start of a renewal.** In `src/Auth.Server/Sessions/SessionPrincipal.cs`, replace

```csharp
    public static ClaimsPrincipal Create(HttpContext http, ApplicationUser user, TenantContext tenant, string audience, TimeProvider clock)
    {
```

with

```csharp
    /// <param name="start">
    /// Spec 0012 → Sessions: the start of the session a renewal replaces, so that the 30-day cap counts from there; the refresh token then lives what
    /// is left before the cap. Left out, the session starts now.
    /// </param>
    /// <exception cref="InvalidOperationException">The session of <paramref name="start"/> is past its cap: it cannot be renewed.</exception>
    public static ClaimsPrincipal Create(
        HttpContext http, ApplicationUser user, TenantContext tenant, string audience, TimeProvider clock, DateTimeOffset? start = null)
    {
```

replace

```csharp
        identity.SetClaim(SessionPolicy.StartClaim, clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
```

with

```csharp
        var now = clock.GetUtcNow();
        var begun = start ?? now;
        var lifetime = start is null
            ? SessionPolicy.SlidingLifetime
            : SessionPolicy.RemainingLifetime(begun, now) ?? throw new InvalidOperationException("A session past its cap cannot be renewed.");
        identity.SetClaim(SessionPolicy.StartClaim, begun.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
```

and replace

```csharp
        principal.SetRefreshTokenLifetime(SessionPolicy.SlidingLifetime);
        http.Items[RefreshCookie.LifetimeItemKey] = SessionPolicy.SlidingLifetime;
```

with

```csharp
        principal.SetRefreshTokenLifetime(lifetime);
        http.Items[RefreshCookie.LifetimeItemKey] = lifetime;
```

- [ ] **Step 7: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.SessionStateTableTests -class Auth.IntegrationTests.MailKindTests -class Auth.IntegrationTests.ProviderTicketTableTests -class Auth.IntegrationTests.LoginTests -class Auth.IntegrationTests.RefreshTests -class Auth.IntegrationTests.RefreshReuseTests -class Auth.IntegrationTests.LogoutTests -class Auth.IntegrationTests.TokenPruningTests -class Auth.IntegrationTests.SessionLifetimeTests -class Auth.IntegrationTests.SignInRaceTests -class Auth.IntegrationTests.SignupCompleteSignInTests -class Auth.IntegrationTests.ProviderExchangeTests -class Auth.IntegrationTests.ProviderExchangeRaceTests -class Auth.IntegrationTests.AccountDeletionTests -class Auth.IntegrationTests.AuditAtomicityTests -class Auth.IntegrationTests.UpgradeFromSpec0010Tests`
passes. `ProviderExchangeRaceTests` counts the authorizations of a sign-in that was rolled back: it must still find none (the authorization of
this step is rolled back with the transaction). A class among them that counts authorizations or reads an authorization's `Type` and fails is
a finding about this step: stop and report it, do not change the test.

- [ ] **Step 8: Hand back** — uncommitted. Files: everything listed under Files, the three generated migration files included. Scan the touched
files. Proposed subject: `feat(server): every new session records its start and its confirmation window`.

---

### Task 2: The session of this device; `confirmed_until`; the Development testing endpoint; the upgrade from spec 0011

**Files:**
- Create: `src/Auth.Server/Sessions/DeviceSessions.cs`, `src/Auth.Server/Testing/TestingSettings.cs`, `src/Auth.Server/Testing/TestingEndpoints.cs`
- Modify: `src/Auth.Server/Api/OrgEndpoints.cs`, `src/Auth.Server/Tenancy/Contracts.cs`, `src/Auth.Server/Api/TenancyEndpoints.cs`,
  `src/Auth.Server/Tenancy/Outcome.cs`, `src/Auth.Server/Program.cs`
- Create (tests): `tests/Auth.IntegrationTests/Infrastructure/SettingsApi.cs`, `tests/Auth.IntegrationTests/ConfirmationWindowTests.cs`,
  `tests/Auth.IntegrationTests/TestingEndpointTests.cs`, `tests/Auth.IntegrationTests/UpgradeFromSpec0011Tests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`, `tests/Auth.IntegrationTests/MeProvidersTests.cs`,
  `tests/Auth.IntegrationTests/OrgEndpointTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`, `tests/Auth.IntegrationTests/ProviderErrorCodesTests.cs`

**Interfaces:**
- Consumes: `SessionState`, `ConfirmationWindow` (Task 1), `RefreshCookie`, `SessionPolicy.ReuseLeeway`, `IOpenIddictTokenManager`.
- Produces: `DeviceSession(string TokenId, string AuthorizationId, Guid UserId, DateTimeOffset Start, DateTimeOffset? ConfirmedUntil)` with
  `IsConfirmedAt(DateTimeOffset)`; `DeviceSessions.FindAsync(HttpContext, AuthDbContext, IOpenIddictTokenManager, Guid userId, DateTimeOffset
  now, CancellationToken)`, `DeviceSessions.ReadAsync(AuthDbContext, string tokenId, Guid userId, DateTimeOffset now, CancellationToken)` (the
  re-read under a lock), `DeviceSessions.EndWindowAsync(AuthDbContext, DeviceSession, CancellationToken)`; `MeResponse.ConfirmedUntil`;
  `TenancyErrors.SessionRequired` (`401`); `TestingSettings.WindowEndpointKey`, `TestingSettings.Load`, `TestingEndpoints.EndConfirmationPath`,
  `TestingEndpoints.MapTestingApi`; test side `SettingsApi` (every path of the settings screen, `Me`, `ConfirmedUntilAsync`, `Post`, `PostRaw`,
  `Confirm`, `Connect`, `Unlink`, `ChangePassword`, `EndConfirmation`).

**Contract counts:** `OpenApiTests`: `MeResponse` gains `confirmed_until`; `GET /auth/me` reads `auth_rt` (the cookie test is rewritten as a
table of the cookies each operation reads). `OrgEndpointTests`: the members of `/auth/me`. `ProviderErrorCodesTests`: `session_required`.

**Decisions the plan takes here:**
- The session of this device is found in two steps: the token manager finds the token of the cookie (it hashes the reference id itself: the
  column is never compared with the cookie, probe U7), then one query without tracking reads the token, its authorization and the row of
  `SessionStates` afresh. The re-read under a lock (Tasks 7 to 12) repeats only the second step, by the token's id, so that it sees what other
  requests committed meanwhile and nothing the request's context or the manager's cache holds.
- `GET /auth/me` never fails for the cookie: no cookie, a cookie of no session of this account, a revoked or expired one, or a window that ended,
  all give `confirmed_until: null`. It is written to the second, in UTC.
- The testing endpoint needs the access token and the cookie (spec 0012 → Testing endpoint), answers `204`, `401` plain or `401
  session_required`, ends the window by writing `null`, and is mapped only when the setting is `true`; it is excluded from the description.
  The setting accepts `true` or `false` in Development; any value outside Development stops the start, as the test issuer does.

- [ ] **Step 1: Write the failing tests.**

In `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`, replace

```csharp
        _settings[ProviderSettings.TestIssuerKey] = "";
```

with

```csharp
        _settings[ProviderSettings.TestIssuerKey] = "";
        // Spec 0012: the testing endpoint is off unless a test turns it on (TestingEndpointTests).
        _settings[TestingSettings.WindowEndpointKey] = "";
```

and add `using Auth.Server.Testing;` after `using Auth.Server.Tenancy;`.

`tests/Auth.IntegrationTests/Infrastructure/SettingsApi.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Auth.Server.Sessions;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Drives the endpoints of spec 0012's settings screen as the browser sends them: the access token, the refresh cookie of the session of this
/// device and, for a ticket, its <c>auth_pt</c>, all carried by hand (the cookies are <c>Secure</c> and the test server speaks plain HTTP).
/// </summary>
public static class SettingsApi
{
    public const string MePath = "/auth/me";
    public const string ConfirmPath = "/auth/account/confirm";
    public const string ConnectPath = "/auth/providers/connect";
    public const string UnlinkPath = "/auth/account/logins/unlink";
    public const string PasswordPath = "/auth/account/password";
    public const string EndConfirmationPath = "/auth/testing/end-confirmation";

    public static HttpRequestMessage Request(
        HttpMethod method, string path, string? accessToken, string? refreshToken, string? binding = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        List<string> cookies = [];
        if (refreshToken is not null)
        {
            cookies.Add($"{SessionApi.CookieName}={refreshToken}");
        }

        if (binding is not null)
        {
            cookies.Add($"{ProviderApi.BindingCookie}={binding}");
        }

        if (cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        }

        return request;
    }

    public static async Task<HttpResponseMessage> Me(HttpClient client, string accessToken, string? refreshToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = Request(HttpMethod.Get, MePath, accessToken, refreshToken);
        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> Me(HttpClient client, SessionApi.Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return Me(client, session.AccessToken, session.RefreshToken);
    }

    /// <summary><c>confirmed_until</c> of <c>GET /auth/me</c>, after asserting its <c>200</c>: the end of the window, or null.</summary>
    public static async Task<string?> ConfirmedUntilAsync(HttpClient client, string accessToken, string? refreshToken)
    {
        using var response = await Me(client, accessToken, refreshToken);
        var me = await TenancyApi.ReadOkAsync(response);
        var value = me.GetProperty("confirmed_until");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    public static Task<string?> ConfirmedUntilAsync(HttpClient client, SessionApi.Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return ConfirmedUntilAsync(client, session.AccessToken, session.RefreshToken);
    }

    /// <summary>The window as the contract writes it, for an instant of the test clock.</summary>
    public static string Window(DateTimeOffset until) => ConfirmationWindow.Format(until);

    public static async Task<HttpResponseMessage> PostAs(
        HttpClient client, string path, string? accessToken, string? refreshToken, object? body, string? binding = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = Request(HttpMethod.Post, path, accessToken, refreshToken, binding, body is null ? null : JsonContent.Create(body));
        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> Post(HttpClient client, string path, SessionApi.Session session, object? body, string? binding = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        return PostAs(client, path, session.AccessToken, session.RefreshToken, body, binding);
    }

    /// <summary>A body as it is (for the bodies a serializer would not write), with the session's token and cookie.</summary>
    public static async Task<HttpResponseMessage> PostRaw(HttpClient client, string path, SessionApi.Session session, string body, string? binding = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(session);

        using var request = Request(
            HttpMethod.Post, path, session.AccessToken, session.RefreshToken, binding, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> Confirm(HttpClient client, SessionApi.Session session, object body, string? binding = null) =>
        Post(client, ConfirmPath, session, body, binding);

    public static Task<HttpResponseMessage> ConfirmWithTicket(HttpClient client, SessionApi.Session session, ProviderApi.Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return Post(client, ConfirmPath, session, new { provider_ticket = ticket.Value }, ticket.Binding);
    }

    public static Task<HttpResponseMessage> Connect(HttpClient client, SessionApi.Session session, ProviderApi.Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return Post(client, ConnectPath, session, new { ticket = ticket.Value }, ticket.Binding);
    }

    public static Task<HttpResponseMessage> Unlink(HttpClient client, SessionApi.Session session, string provider) =>
        Post(client, UnlinkPath, session, new { provider });

    public static Task<HttpResponseMessage> ChangePassword(HttpClient client, SessionApi.Session session, string current, string next) =>
        Post(client, PasswordPath, session, new { current_password = current, new_password = next });

    public static Task<HttpResponseMessage> EndConfirmation(HttpClient client, SessionApi.Session session) =>
        Post(client, EndConfirmationPath, session, null);
}
```

`tests/Auth.IntegrationTests/ConfirmationWindowTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Sessions;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0012 → Sessions and The confirmation window (criteria 8 and 9): every sign-in that starts a session opens a window of five minutes on
/// it; a refresh keeps it; it is bound to the session of the refresh cookie, and <c>GET /auth/me</c> says when it ends.
/// </summary>
public sealed class ConfirmationWindowTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;

    private string OpenUntil() => SettingsApi.Window(StorableTime.Now(Clock) + ConfirmationWindow.Lifetime);

    /// <summary>A session started by one of the sign-ins of criterion 8, each with an account of its own.</summary>
    private async Task<SessionApi.Session> SignInAsync(string how)
    {
        switch (how)
        {
            case "login":
                await CreateUserAsync("login@firma.test", confirmed: true);
                return await SessionApi.LoginAsync(Client, "login@firma.test", UserPassword);
            case "exchange":
            {
                var user = await CreateUserAsync(Ola, confirmed: true);
                await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
                using var exchanged = await ProviderApi.Exchange(Client, await TicketAsync("google", GooglePerson()));
                return await SessionApi.ReadSessionAsync(exchanged);
            }
            case "link":
            {
                await CreateUserAsync(Ola, confirmed: true);
                using var linked = await ProviderApi.Link(Client, await TicketAsync("google", GooglePerson()), UserPassword);
                return await SessionApi.ReadSessionAsync(linked);
            }
            case "finish":
                return (await PrivateThroughProviderAsync("google", GooglePerson(email: "finish@gmail.test", subject: "700000000000000000007"))).Session;
            case "invitation":
            {
                var company = await CreateCompanyAsync("Acme");
                var invite = await AddInviteAsync(company, "jan@gmail.test", "user");
                await EnqueueInvitationAsync(invite, "jan@gmail.test");
                await DispatchAsync();
                var token = TokenIn(Mail.Sent.Last(m => m.To == "jan@gmail.test"));
                var ticket = await TicketAsync("google", GooglePerson(email: "jan@gmail.test", subject: "900000000000000000009"), "?invite=" + token);
                using var accepted = await ProviderApi.Exchange(Client, ticket);
                return await SessionApi.ReadSessionAsync(accepted);
            }
            default:
            {
                var token = await MailedPrivateAsync("complete@firma.test");
                using var completed = await SignupApi.CompletePrivate(Client, token, Password);
                return await SessionApi.ReadSessionAsync(completed);
            }
        }
    }

    [Theory]
    [InlineData("login")]
    [InlineData("exchange")]
    [InlineData("link")]
    [InlineData("finish")]
    [InlineData("invitation")]
    [InlineData("completion")]
    public async Task Every_sign_in_that_starts_a_session_opens_its_window_for_five_minutes(string how)   // criterion 8
    {
        var session = await SignInAsync(how);

        Assert.Equal(OpenUntil(), await SettingsApi.ConfirmedUntilAsync(Client, session));
        Assert.Equal(1, await InDbAsync(db => db.SessionStates.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_password_acceptance_of_an_invitation_opens_no_window()   // criterion 8: the 204 of spec 0005 signs nobody in
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, "anna@acme.test", "user");
        await EnqueueInvitationAsync(invite, "anna@acme.test");
        await DispatchAsync();
        var states = await InDbAsync(db => db.SessionStates.CountAsync(TestContext.Current.CancellationToken));

        using var accepted = await TenancyApi.Accept(Client, TokenIn(Mail.Sent.Last(m => m.To == "anna@acme.test")), Password);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(states, await InDbAsync(db => db.SessionStates.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task At_five_minutes_and_one_second_the_window_is_closed()   // criterion 8: a controlled clock
    {
        var session = await SignInAsync("login");
        var until = OpenUntil();

        Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.Equal(until, await SettingsApi.ConfirmedUntilAsync(Client, session));

        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));
    }

    [Fact]
    public async Task A_refresh_keeps_the_window_and_never_extends_it()   // criterion 8
    {
        var session = await SignInAsync("login");
        var until = OpenUntil();

        Clock.Advance(TimeSpan.FromMinutes(3));
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);
        Assert.Equal(until, await SettingsApi.ConfirmedUntilAsync(Client, refreshed));

        Clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        var again = await SessionApi.RefreshOk(Client, refreshed.RefreshToken);
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, again));
    }

    [Fact]
    public async Task Two_sessions_of_one_account_have_two_windows()   // criterion 9: bound to the session, not to the account
    {
        var first = await SignInAsync("login");
        Clock.Advance(TimeSpan.FromMinutes(4));
        var second = await SessionApi.LoginAsync(Client, "login@firma.test", UserPassword);
        Clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, first));
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, second));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, second.AccessToken, first.RefreshToken));   // the cookie decides, not the token
    }

    [Fact]
    public async Task A_sign_out_closes_the_window_of_its_session()   // criterion 9
    {
        var session = await SignInAsync("login");

        using (var logout = await SessionApi.Logout(Client, session.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));   // the access token still works for its ten minutes
    }

    [Fact]
    public async Task After_a_reset_or_an_account_deletion_the_old_session_names_nothing()   // criterion 9: EndAllAsync, an account deletion
    {
        var session = await SignInAsync("login");
        using (var forgot = await AccountApi.Forgot(Client, "login@firma.test"))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        await DispatchAsync();
        using (var reset = await AccountApi.Reset(Client, TokenIn(Mail.Sent.Last(m => m.To == "login@firma.test")), "Chosen-Passw0rd-2"))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));

        await CreatePrivateAccountAsync("alone@poczta.test");
        var privateSession = await SessionApi.LoginAsync(Client, "alone@poczta.test", UserPassword);
        var authorizationIds = await InDbAsync(db => db.SessionStates.Select(s => s.AuthorizationId).ToListAsync(TestContext.Current.CancellationToken));
        using (var deleted = await ProviderApi.DeleteAccount(Client, privateSession.AccessToken, new { password = UserPassword }, null))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var left = await InDbAsync(db => db.SessionStates.Select(s => s.AuthorizationId).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(authorizationIds.Count - 1, left.Count);   // the deleted account's session went, with its row
    }

    [Fact]
    public async Task Without_the_cookie_with_another_accounts_a_revoked_one_or_garbage_me_says_null()   // criterion 9
    {
        var mine = await SignInAsync("login");
        await CreateUserAsync("other@firma.test", confirmed: true);
        var theirs = await SessionApi.LoginAsync(Client, "other@firma.test", UserPassword);

        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, mine));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, mine.AccessToken, null));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, mine.AccessToken, theirs.RefreshToken));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, mine.AccessToken, "not-a-token-at-all"));
        using (var logout = await SessionApi.Logout(Client, theirs.RefreshToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, theirs));
    }

    [Fact]
    public async Task A_refresh_cookie_redeemed_within_the_leeway_still_names_the_session()   // criterion 9: 15 seconds
    {
        var session = await SignInAsync("login");
        var refreshed = await SessionApi.RefreshOk(Client, session.RefreshToken);

        Clock.Advance(TimeSpan.FromSeconds(14));
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, refreshed.AccessToken, session.RefreshToken));

        Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, refreshed.AccessToken, session.RefreshToken));
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, refreshed));
    }

    [Fact]
    public async Task A_session_without_a_row_has_a_closed_window()   // spec 0012 → Sessions: a session that began before this version
    {
        var session = await SignInAsync("login");
        await InDbAsync(db => db.SessionStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));
        Assert.Equal(1, await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>().CountAsync(TestContext.Current.CancellationToken)));
    }
}
```

`tests/Auth.IntegrationTests/TestingEndpointTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Testing;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → Testing endpoint (criterion 24): Development only, off by default, it ends the window of this device's session.</summary>
public sealed class TestingEndpointTests : TenancyTestBase
{
    private const string Ola = "ola@firma.test";

    public TestingEndpointTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // The host of this class has the endpoint; a test that needs it off starts a host of its own.
        Factory.WithSetting(TestingSettings.WindowEndpointKey, "true");
    }

    [Fact]
    public async Task With_the_setting_on_it_ends_the_window_of_this_devices_session_and_no_other()   // criterion 24, Settled 22
    {
        await CreateUserAsync(Ola, confirmed: true);
        var here = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);

        using var ended = await SettingsApi.EndConfirmation(Client, here);

        await TenancyApi.AssertEmptyAsync(ended, HttpStatusCode.NoContent);
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, here));
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, there));
        var refreshed = await SessionApi.RefreshOk(Client, here.RefreshToken);   // the session itself goes on
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, refreshed));
    }

    [Fact]
    public async Task Without_the_cookie_it_is_401_session_required_and_without_a_token_the_plain_401()   // spec 0012 → General rules
    {
        await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);

        using var noCookie = await SettingsApi.PostAs(Client, SettingsApi.EndConfirmationPath, session.AccessToken, null, null);
        using var noToken = await SettingsApi.PostAs(Client, SettingsApi.EndConfirmationPath, null, session.RefreshToken, null);

        Assert.Equal(HttpStatusCode.Unauthorized, noCookie.StatusCode);
        Assert.Equal("""{"error":"session_required"}""", await noCookie.Content.ReadAsStringAsync());
        await TenancyApi.AssertUnauthorizedAsync(noToken);
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, session));
    }

    [Fact]
    public async Task With_the_setting_off_it_is_404()   // criterion 24
    {
        await using var off = new AuthAppFactory(Postgres, Keys);
        using var client = SessionApi.CreateClient(off);
        var session = await SessionApi.LoginAsync(client, off);

        using var response = await SettingsApi.EndConfirmation(client, session);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(client, session));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task The_setting_outside_development_stops_the_start_naming_the_key(string value)   // criterion 24
    {
        await using var production = new AuthAppFactory(Postgres, Keys)
            .WithEnvironment("Production")
            .WithSetting(TestingSettings.WindowEndpointKey, value);

        var ex = Assert.ThrowsAny<Exception>(() => production.CreateClient());

        Assert.Contains(TestingSettings.WindowEndpointKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_that_is_not_true_or_false_stops_the_start_naming_the_key()
    {
        await using var development = new AuthAppFactory(Postgres, Keys).WithSetting(TestingSettings.WindowEndpointKey, "yes");

        var ex = Assert.ThrowsAny<Exception>(() => development.CreateClient());

        Assert.Contains(TestingSettings.WindowEndpointKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_is_that_of_the_contract() => Assert.Equal("Auth:Testing:WindowEndpoint", TestingSettings.WindowEndpointKey);
}
```

`tests/Auth.IntegrationTests/UpgradeFromSpec0011Tests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012, criterion 27: an instance of spec 0011 upgrades without noticing.</summary>
public sealed class UpgradeFromSpec0011Tests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string LastMigrationOfSlice0011 = "20260424164100_AddProviderTickets";
    private static readonly string[] LoginClaims = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"];

    [Fact]
    public async Task A_database_of_slice_0011_takes_the_session_table_and_the_provider_column_and_keeps_what_it_holds()   // criterion 27
    {
        await using var factory = new AuthAppFactory(postgres, keys);   // never built: only its connection string is used
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(factory.ConnectionString).UseOpenIddict().Options;
        var cancellation = TestContext.Current.CancellationToken;

        await using (var old = new AuthDbContext(options))
        {
            await old.GetService<IMigrator>().MigrateAsync(LastMigrationOfSlice0011, cancellation);
            Assert.False(await old.Database.SqlQuery<bool>($"""SELECT to_regclass('"SessionStates"') IS NOT NULL AS "Value" """).SingleAsync(cancellation));
            await old.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "MailRequests" ("Kind", "NormalizedEmail", "RequestedAt", "Attempts", "NextAttemptAt")
                VALUES (1, 'OLA@EXAMPLE.TEST', now(), 0, now())
                """,
                cancellation);
        }

        await using (var upgraded = new AuthDbContext(options))
        {
            await upgraded.Database.MigrateAsync(cancellation);

            Assert.Equal(0, await upgraded.SessionStates.CountAsync(cancellation));
            var row = await upgraded.MailRequests.AsNoTracking().SingleAsync(cancellation);
            Assert.Equal((MailKind.PasswordReset, "OLA@EXAMPLE.TEST", (string?)null), (row.Kind, row.NormalizedEmail, row.Provider));
            Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync(cancellation));
        }
    }

    [Fact]
    public async Task An_instance_with_the_settings_of_spec_0011_starts_healthy_offers_its_providers_and_issues_the_same_tokens()   // criterion 27
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup().WithProviders();
        using var client = SessionApi.CreateClient(factory);

        using (var health = await client.GetAsync("/auth/health", TestContext.Current.CancellationToken))
        {
            Assert.Equal("Healthy", await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (var options = await client.GetAsync("/auth/options", TestContext.Current.CancellationToken))
        {
            Assert.Equal(
                """{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft"]}""",
                await options.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        var session = await SessionApi.LoginAsync(client, factory);
        Assert.Equal(LoginClaims, AccessTokens.Payload(session.AccessToken).Select(p => p.Key).Order(StringComparer.Ordinal));
        using var me = await SettingsApi.Me(client, session);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.String, body.RootElement.GetProperty("confirmed_until").ValueKind);
    }

    [Fact]
    public async Task A_session_from_before_this_version_refreshes_as_before_and_its_window_is_closed()   // criterion 27, spec 0012 → Sessions
    {
        await using var factory = new AuthAppFactory(postgres, keys);
        using var client = SessionApi.CreateClient(factory);
        var session = await SessionApi.LoginAsync(client, factory);
        using (var scope = factory.Services.CreateScope())
        {
            // What a session of spec 0011 is: an authorization with no row of values.
            await scope.ServiceProvider.GetRequiredService<AuthDbContext>().SessionStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var refreshed = await SessionApi.RefreshOk(client, session.RefreshToken);

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(client, refreshed));
        using var scope2 = factory.Services.CreateScope();
        Assert.Equal(0, await scope2.ServiceProvider.GetRequiredService<AuthDbContext>().SessionStates.CountAsync(TestContext.Current.CancellationToken));
    }
}
```

In `tests/Auth.IntegrationTests/MeProvidersTests.cs`, add before the last closing brace of the class:

```csharp

    [Fact]
    public async Task Confirmed_until_is_the_end_of_the_window_of_this_devices_session_in_utc_with_seconds()   // spec 0012, criterion 23
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        using var response = await SettingsApi.Me(Client, session);

        var me = await TenancyApi.ReadOkAsync(response);
        var until = me.GetProperty("confirmed_until").GetString()!;
        Assert.Matches("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", until);
        Assert.Equal(SettingsApi.Window(Auth.Server.Email.StorableTime.Now(Clock) + Auth.Server.Sessions.ConfirmationWindow.Lifetime), until);
    }

    [Fact]
    public async Task Confirmed_until_is_null_without_the_refresh_cookie()   // spec 0012, criterion 23
    {
        var session = await SessionApi.LoginAsync(Client, Factory);

        var me = await MeAsync(session.AccessToken);   // TenancyApi sends no cookie

        Assert.Equal(JsonValueKind.Null, me.GetProperty("confirmed_until").ValueKind);
    }
```

In `tests/Auth.IntegrationTests/OrgEndpointTests.cs`, in `Me_returns_the_callers_current_data_from_the_database`, replace

```csharp
            ["email", "has_password", "linked_providers", "org_id", "org_kind", "org_name", "permissions", "roles", "sub"],
            me.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
```

with

```csharp
            ["confirmed_until", "email", "has_password", "linked_providers", "org_id", "org_kind", "org_name", "permissions", "roles", "sub"],
            me.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, me.GetProperty("confirmed_until").ValueKind);   // spec 0012: no refresh cookie, no window
```

In `tests/Auth.IntegrationTests/OpenApiTests.cs`:

(a) in `The_responses_are_described_in_snake_case`, replace
`Assert.Equal(["email", "has_password", "linked_providers", "org_id", "org_kind", "org_name", "permissions", "roles", "sub"], Properties("MeResponse"));`
with
`Assert.Equal(["confirmed_until", "email", "has_password", "linked_providers", "org_id", "org_kind", "org_name", "permissions", "roles", "sub"], Properties("MeResponse"));`.

(b) replace the field (`TicketEndpoints` goes: the test of (c) keeps what it asserted for the ticket endpoints, the `auth_pt` cookie of type string and a request body,
for every endpoint of `CookiesRead` that reads `auth_pt`)

```csharp
    // The endpoints that take a provider ticket and its auth_pt cookie (spec 0011).
    private static readonly string[] TicketEndpoints = ["POST /auth/providers/exchange", "POST /auth/providers/link", "POST /auth/providers/finish", "DELETE /auth/account"];
```

with

```csharp
    // The cookies each operation reads as its request (specs 0002, 0011 and 0012); every other operation reads none.
    private static readonly Dictionary<string, string[]> CookiesRead = new(StringComparer.Ordinal)
    {
        ["POST /auth/refresh"] = ["auth_rt"],
        ["POST /auth/logout"] = ["auth_rt"],
        ["GET /auth/me"] = ["auth_rt"],   // spec 0012: confirmed_until
        ["POST /auth/providers/exchange"] = ["auth_pt"],
        ["POST /auth/providers/link"] = ["auth_pt"],
        ["POST /auth/providers/finish"] = ["auth_pt"],
        ["DELETE /auth/account"] = ["auth_pt"],
    };
```

(c) replace the whole method `Refresh_and_logout_take_the_refresh_cookie_and_nothing_else_does` (from its `[Fact]` to its closing brace) with

```csharp
    [Fact]
    public async Task Each_endpoint_reads_the_cookies_of_the_contract_and_no_other()   // criterion 24; spec 0012: GET /auth/me reads auth_rt
    {
        await using var factory = new AuthAppFactory(postgres, keys);

        foreach (var (endpoint, operation) in Operations(await DescriptionAsync(factory)))
        {
            var declared = (operation["parameters"] as JsonArray ?? [])
                .Where(p => p!["in"]!.GetValue<string>() == "cookie")
                .ToList();
            var cookies = declared.Select(p => p!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal);
            Assert.Equal(CookiesRead.GetValueOrDefault(endpoint, []), cookies);
            Assert.All(declared, p => Assert.Equal("string", p!["schema"]!["type"]!.GetValue<string>()));
            if (endpoint is "POST /auth/refresh" or "POST /auth/logout")
            {
                Assert.Null(operation["requestBody"]);   // the cookie is their whole request
            }
            else if (cookies.Contains("auth_pt"))
            {
                // Spec 0011: the endpoints that take a provider ticket read its binding cookie beside their body (spec 0012: the connect, the
                // confirmation and the company deletion do too).
                Assert.NotNull(operation["requestBody"]);
            }
        }
    }
```

In `tests/Auth.IntegrationTests/ProviderErrorCodesTests.cs`, after
`    [InlineData(TenancyErrors.NoMembership, "no_membership", StatusCodes.Status403Forbidden)]` add
`    [InlineData(TenancyErrors.SessionRequired, "session_required", StatusCodes.Status401Unauthorized)]   // spec 0012`.

- [ ] **Step 2: See them fail.** Build: it fails (`TestingSettings`, `TenancyErrors.SessionRequired` do not exist). That is the failure of this
step; once Step 3 compiles, `ConfirmationWindowTests` fail on the missing `confirmed_until`.

- [ ] **Step 3: The session of this device.** `src/Auth.Server/Sessions/DeviceSessions.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.Server.Sessions;

/// <summary>
/// The session of this device (spec 0012 → General rules): the session the request's refresh cookie belongs to, with its start and its window.
/// </summary>
/// <param name="TokenId">The id of the cookie's refresh token, by which a re-read under a lock finds the session again.</param>
/// <param name="Start">The stored start, or for a session from before this version the creation of its authorization.</param>
/// <param name="ConfirmedUntil">The end of the window; <see langword="null"/> for one that was closed or never opened.</param>
public sealed record DeviceSession(string TokenId, string AuthorizationId, Guid UserId, DateTimeOffset Start, DateTimeOffset? ConfirmedUntil)
{
    public bool IsConfirmedAt(DateTimeOffset now) => ConfirmedUntil is { } until && until > now;
}

/// <summary>
/// Finds the session of this device without redeeming the cookie (Amendment A8): its token must be a refresh token, <c>valid</c> or redeemed
/// within the reuse leeway of spec 0002 (15 seconds), not expired, of a <c>valid</c> authorization, and of the account the access token names.
/// Anything else is no session. The token manager finds the token (it hashes the cookie as the column holds it); everything else is read afresh,
/// without tracking, so that a re-read under a lock sees what other requests committed meanwhile.
/// </summary>
public static class DeviceSessions
{
    public static async Task<DeviceSession?> FindAsync(
        HttpContext http, AuthDbContext db, IOpenIddictTokenManager tokens, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokens);

        if (!RefreshCookie.TryRead(http.Request, out var reference))
        {
            return null;
        }

        var token = await tokens.FindByReferenceIdAsync(reference, cancellationToken);
        if (token is null || await tokens.GetIdAsync(token, cancellationToken) is not { } tokenId)
        {
            return null;
        }

        return await ReadAsync(db, tokenId, userId, now, cancellationToken);
    }

    /// <summary>The session of the refresh token <paramref name="tokenId"/>, by the rules above, read now; <see langword="null"/> when it names none.</summary>
    public static async Task<DeviceSession?> ReadAsync(AuthDbContext db, string tokenId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokenId);

        var row = await db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking()
            .Where(t => t.Id == tokenId && t.Authorization != null)
            .Select(t => new
            {
                t.Type,
                t.Status,
                t.RedemptionDate,
                t.ExpirationDate,
                t.Subject,
                AuthorizationId = t.Authorization!.Id,
                AuthorizationStatus = t.Authorization.Status,
                Created = t.Authorization.CreationDate,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is not { AuthorizationId: { } authorizationId }
            || !string.Equals(row.Type, OpenIddictConstants.TokenTypeIdentifiers.RefreshToken, StringComparison.Ordinal)
            || !string.Equals(row.Subject, userId.ToString(), StringComparison.Ordinal)
            || !string.Equals(row.AuthorizationStatus, OpenIddictConstants.Statuses.Valid, StringComparison.Ordinal)
            || row.ExpirationDate is not { } expires || Utc(expires) <= now
            || !(string.Equals(row.Status, OpenIddictConstants.Statuses.Valid, StringComparison.Ordinal)
                || (string.Equals(row.Status, OpenIddictConstants.Statuses.Redeemed, StringComparison.Ordinal)
                    && row.RedemptionDate is { } redeemed && Utc(redeemed) + SessionPolicy.ReuseLeeway > now)))
        {
            return null;
        }

        var stored = await db.SessionStates.AsNoTracking().FirstOrDefaultAsync(s => s.AuthorizationId == authorizationId, cancellationToken);
        if (stored is not null)
        {
            return new DeviceSession(tokenId, authorizationId, userId, stored.SessionStart, stored.ConfirmedUntil);
        }

        // A session from before this version: it began when its authorization was made (a refresh keeps it), and its window is closed.
        return row.Created is { } created ? new DeviceSession(tokenId, authorizationId, userId, Utc(created), null) : null;
    }

    /// <summary>Ends the window of the session (the testing endpoint); a session without a row has none to end.</summary>
    public static Task<int> EndWindowAsync(AuthDbContext db, DeviceSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(session);

        return db.SessionStates
            .Where(s => s.AuthorizationId == session.AuthorizationId)
            .ExecuteUpdateAsync(s => s.SetProperty(state => state.ConfirmedUntil, (DateTimeOffset?)null), cancellationToken);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
```

- [ ] **Step 4: `confirmed_until`.** In `src/Auth.Server/Tenancy/Contracts.cs`, replace

```csharp
/// <c>linked_providers</c>: the providers of its logins, sorted ordinally (spec 0011).
/// </summary>
public sealed record MeResponse(
    Guid Sub, string Email, Guid OrgId, string OrgName, string OrgKind, string[] Roles, string[] Permissions, bool HasPassword, string[] LinkedProviders);
```

with

```csharp
/// <c>linked_providers</c>: the providers of its logins, sorted ordinally (spec 0011). <c>confirmed_until</c>: the end of the confirmation window of
/// the session of this device, while it is in the future (spec 0012); <see langword="null"/> otherwise.
/// </summary>
public sealed record MeResponse(
    Guid Sub, string Email, Guid OrgId, string OrgName, string OrgKind, string[] Roles, string[] Permissions, bool HasPassword, string[] LinkedProviders,
    string? ConfirmedUntil);
```

In `src/Auth.Server/Api/OrgEndpoints.cs`, replace

```csharp
    public static async Task<IResult> MeAsync(HttpContext http, MembershipReader memberships, UserManager<ApplicationUser> users)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(users);
```

with

```csharp
    public static async Task<IResult> MeAsync(
        HttpContext http, MembershipReader memberships, UserManager<ApplicationUser> users, AuthDbContext db, IOpenIddictTokenManager tokens,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);
```

replace

```csharp
        var linked = (await users.GetLoginsAsync(user)).Select(l => l.LoginProvider).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return ApiResults.Ok(new MeResponse(
            caller.UserId, user.Email, caller.CompanyId, caller.CompanyName, SpaceKinds.NameOf(caller.Kind), [caller.RoleName], [.. caller.Permissions],
            user.PasswordHash is not null, [.. linked]));
```

with

```csharp
        var linked = (await users.GetLoginsAsync(user)).Select(l => l.LoginProvider).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

        // Spec 0012: the window of the session of this device, read from the refresh cookie, never redeemed; no session, no window.
        var now = StorableTime.Now(clock);
        var session = await DeviceSessions.FindAsync(http, db, tokens, caller.UserId, now, http.RequestAborted);
        var until = session is not null && session.IsConfirmedAt(now) ? ConfirmationWindow.Format(session.ConfirmedUntil!.Value) : null;
        return ApiResults.Ok(new MeResponse(
            caller.UserId, user.Email, caller.CompanyId, caller.CompanyName, SpaceKinds.NameOf(caller.Kind), [caller.RoleName], [.. caller.Permissions],
            user.PasswordHash is not null, [.. linked], until));
```

and replace the usings at the top

```csharp
using Auth.Infrastructure.Identity;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
```

with

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Requests;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
```

In `src/Auth.Server/Api/TenancyEndpoints.cs`, replace

```csharp
        app.MapGet(OrgEndpoints.MePath, OrgEndpoints.MeAsync)
            .RequireAuthorization()
            .WithTags("Company")
            .Produces<MeResponse>()
            .ProducesGuarded();
```

with

```csharp
        app.MapGet(OrgEndpoints.MePath, OrgEndpoints.MeAsync)
            .RequireAuthorization()
            .WithTags("Company")
            .ReadsRefreshCookie(
                "Spec 0012: the session of this device, for `confirmed_until`; read, never redeemed. Missing, or of no session of this account: "
                + "`confirmed_until` is null.")
            .Produces<MeResponse>()
            .ProducesGuarded();
```

In `src/Auth.Server/Tenancy/Outcome.cs`, replace

```csharp
    public const string NoMembership = "no_membership";

    public static int StatusOf(string error) => error switch
    {
```

with

```csharp
    public const string NoMembership = "no_membership";

    // Spec 0012.
    public const string SessionRequired = "session_required";

    public static int StatusOf(string error) => error switch
    {
        SessionRequired => StatusCodes.Status401Unauthorized,
```

- [ ] **Step 5: The testing endpoint.** `src/Auth.Server/Testing/TestingSettings.cs`:

```csharp
namespace Auth.Server.Testing;

/// <summary>
/// Spec 0012 → Configuration: <c>Auth:Testing:WindowEndpoint</c>, Development only. <c>true</c> maps the testing endpoint, so that a browser test
/// reaches a closed window without moving the server's clock (Settled 22). Any value outside Development stops the start, naming the key, as the
/// test issuer does: a production instance must never offer it.
/// </summary>
public sealed class TestingSettings
{
    public const string WindowEndpointKey = "Auth:Testing:WindowEndpoint";

    public bool WindowEndpoint { get; private init; }

    /// <exception cref="InvalidOperationException">The key is set outside Development, or to something other than true or false.</exception>
    public static TestingSettings Load(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var text = configuration[WindowEndpointKey];
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TestingSettings();
        }

        if (!isDevelopment)
        {
            throw new InvalidOperationException($"Configuration value '{WindowEndpointKey}' is for the Development environment only: remove it.");
        }

        return bool.TryParse(text.Trim(), out var on)
            ? new TestingSettings { WindowEndpoint = on }
            : throw new InvalidOperationException($"Configuration value '{WindowEndpointKey}' must be true or false.");
    }
}
```

`src/Auth.Server/Testing/TestingEndpoints.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Email;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Testing;

/// <summary>
/// <c>POST /auth/testing/end-confirmation</c> (spec 0012 → Testing endpoint): with the access token and the refresh cookie, and no body, it ends
/// the window of this device's session. Mapped only with <see cref="TestingSettings.WindowEndpoint"/>, and never in the OpenAPI description.
/// </summary>
public static class TestingEndpoints
{
    public const string EndConfirmationPath = "/auth/testing/end-confirmation";

    public static IEndpointRouteBuilder MapTestingApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(EndConfirmationPath, EndConfirmationAsync).RequireAuthorization().ExcludeFromDescription();
        return app;
    }

    public static async Task<IResult> EndConfirmationAsync(HttpContext http, AuthDbContext db, IOpenIddictTokenManager tokens, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);

        if (!Guid.TryParse(http.User.FindFirst(Claims.Subject)?.Value, out var userId)
            || await DeviceSessions.FindAsync(http, db, tokens, userId, StorableTime.Now(clock), http.RequestAborted) is not { } session)
        {
            return ApiResults.Error(TenancyErrors.SessionRequired);
        }

        await DeviceSessions.EndWindowAsync(db, session, http.RequestAborted);
        return ApiResults.NoContent();
    }
}
```

In `src/Auth.Server/Program.cs`, replace

```csharp
// Fail fast on a bad proxy list, a bad rate limit or a bad audit retention, naming the key (spec 0008).
```

with

```csharp
// Spec 0012: the Development-only testing endpoint; set elsewhere it stops the start naming its key.
var testingSettings = TestingSettings.Load(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddSingleton(testingSettings);
// Fail fast on a bad proxy list, a bad rate limit or a bad audit retention, naming the key (spec 0008).
```

replace

```csharp
app.MapProviderApi();
app.MapAuthOpenApi();
```

with

```csharp
app.MapProviderApi();
if (testingSettings.WindowEndpoint)
{
    app.MapTestingApi();
}

app.MapAuthOpenApi();
```

and add `using Auth.Server.Testing;` after `using Auth.Server.Tenancy;`.

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ConfirmationWindowTests -class Auth.IntegrationTests.TestingEndpointTests -class Auth.IntegrationTests.UpgradeFromSpec0011Tests -class Auth.IntegrationTests.MeProvidersTests -class Auth.IntegrationTests.OrgEndpointTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests -class Auth.IntegrationTests.ProviderErrorCodesTests -class Auth.IntegrationTests.UpgradeFromSpec0010Tests -class Auth.IntegrationTests.ProviderPasswordResetTests -class Auth.IntegrationTests.HostHardeningTests`
passes. Mutate twice and see a test fail, then restore (guard lesson 7): make `ReadAsync` accept a redeemed token at any age
(`A_refresh_cookie_redeemed_within_the_leeway_still_names_the_session` fails), and drop the subject comparison
(`Without_the_cookie_with_another_accounts_a_revoked_one_or_garbage_me_says_null` fails).

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): GET /auth/me says confirmed_until for the session of this device`.

---

### Task 3: GitHub as a provider name: keys, health, manifest, the shipped configuration

**Files:**
- Modify: `src/Auth.Server/Providers/ProviderNames.cs`, `src/Auth.Server/Providers/ProviderSettings.cs`, `src/Auth.Server/Tenancy/ManifestParser.cs`,
  `tests/Auth.FakeOidc/compose.yml`, `samples/notes-api/auth.yaml`, `deploy/auth.yaml`, `deploy/docker-compose.prod.yml`, `deploy/.env.prod.example`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`, `tests/Auth.IntegrationTests/ManifestProvidersTests.cs`,
  `tests/Auth.IntegrationTests/ProviderOptionsTests.cs`, `tests/Auth.IntegrationTests/ProviderSettingsTests.cs`,
  `tests/Auth.IntegrationTests/ProviderPathsTests.cs`, `tests/Auth.IntegrationTests/ProviderStartTests.cs`,
  `tests/Auth.IntegrationTests/ProviderCallbackErrorTests.cs`, `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`,
  `tests/Auth.IntegrationTests/UpgradeFromSpec0011Tests.cs`

**Interfaces:**
- Consumes: `ProviderCatalog` (unchanged: it reads `MissingKeysOf` for every listed name), `TestingSettings.WindowEndpointKey` (Task 2).
- Produces: `ProviderNames.GitHub` (`"github"`, display `GitHub`, third in `All`); `ProviderSettings.GitHubClientIdKey`,
  `ProviderSettings.GitHubClientSecretKey`, `ProviderSettings.GitHub`; test side `AuthAppFactory.GitHubClientId`, `AuthAppFactory.GitHubClientSecret`,
  `AuthAppFactory.AllProvidersLine`, `AuthAppFactory.WithProviders(bool github = false)` (and `WithProviderKeys()` gives GitHub its keys too).

**Contract counts:** the names the manifest accepts (`ManifestProvidersTests`, `ProviderOptionsTests`); the callback paths (`ProviderPathsTests`);
the shipped manifests and the production variables (`ShippedConfigurationTests`).

**Decisions the plan takes here:**
- The earlier tests that used `github` as "a name that is no provider" now use `gitlab`: the rule they pin (any other name is not a provider)
  stands, only the example changes. Each such test is named in Step 1.
- `WithProviderKeys()` gives all three providers their keys and the return address, and the manifest decides what is offered, as in production;
  `WithProviders(github: true)` lists all three. A host whose manifest lists two providers registers GitHub's client too and never uses it.
- The overlay of the fake (`tests/Auth.FakeOidc/compose.yml`) gets GitHub's keys and `Auth__Testing__WindowEndpoint: "true"` now, so that every
  later task's stack is the one of the end: the end-to-end stacks are the only Development stacks that set it. The test that reads the overlay
  takes a value without its surrounding quotes, as Compose does.

- [ ] **Step 1: Write the failing tests and change the ones that pin "github is no provider".**

In `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`:

(a) replace

```csharp
        _settings[ProviderSettings.MicrosoftSecretExpiresOnKey] = "";
```

with

```csharp
        _settings[ProviderSettings.MicrosoftSecretExpiresOnKey] = "";
        _settings[ProviderSettings.GitHubClientIdKey] = "";
        _settings[ProviderSettings.GitHubClientSecretKey] = "";
```

(b) replace

```csharp
    /// <summary>The line that lists both providers (spec 0011 → Manifest).</summary>
    public const string ProvidersLine = "\nproviders: [google, microsoft]\n";
```

with

```csharp
    /// <summary>A GitHub OAuth App's client id has this shape; nothing at GitHub knows this one.</summary>
    public const string GitHubClientId = "Ov23liGitHubClientIdTest";
    public const string GitHubClientSecret = "github-client-secret-of-the-tests";

    /// <summary>The line that lists Google and Microsoft (spec 0011 → Manifest).</summary>
    public const string ProvidersLine = "\nproviders: [google, microsoft]\n";

    /// <summary>The line that lists the three providers (spec 0012 → Manifest).</summary>
    public const string AllProvidersLine = "\nproviders: [google, microsoft, github]\n";
```

(c) replace

```csharp
    /// <summary>
    /// Lists both providers in the manifest file as it stands and gives both their keys and the return address (spec 0011). Call it after
    /// <see cref="WithManifest"/>, <see cref="WithSignup"/> or <see cref="WithPrivateSignup"/>, which write the file anew.
    /// </summary>
    public AuthAppFactory WithProviders()
    {
        File.AppendAllText(ManifestPath, ProvidersLine);
        return WithProviderKeys();
    }

    /// <summary>Both providers' keys and the return address, without listing them in the manifest.</summary>
    public AuthAppFactory WithProviderKeys() =>
        WithSetting(ProviderSettings.ReturnUrlKey, DefaultProviderReturnUrl)
            .WithSetting(ProviderSettings.GoogleClientIdKey, GoogleClientId)
            .WithSetting(ProviderSettings.GoogleClientSecretKey, GoogleClientSecret)
            .WithSetting(ProviderSettings.MicrosoftClientIdKey, MicrosoftClientId)
            .WithSetting(ProviderSettings.MicrosoftClientSecretKey, MicrosoftClientSecret);
```

with

```csharp
    /// <summary>
    /// Lists Google and Microsoft (and with <paramref name="github"/> GitHub too) in the manifest file as it stands and gives every provider its keys
    /// and the return address (specs 0011 and 0012). Call it after <see cref="WithManifest"/>, <see cref="WithSignup"/> or
    /// <see cref="WithPrivateSignup"/>, which write the file anew.
    /// </summary>
    public AuthAppFactory WithProviders(bool github = false)
    {
        File.AppendAllText(ManifestPath, github ? AllProvidersLine : ProvidersLine);
        return WithProviderKeys();
    }

    /// <summary>Every provider's keys and the return address, without listing them in the manifest: the manifest decides what is offered.</summary>
    public AuthAppFactory WithProviderKeys() =>
        WithSetting(ProviderSettings.ReturnUrlKey, DefaultProviderReturnUrl)
            .WithSetting(ProviderSettings.GoogleClientIdKey, GoogleClientId)
            .WithSetting(ProviderSettings.GoogleClientSecretKey, GoogleClientSecret)
            .WithSetting(ProviderSettings.MicrosoftClientIdKey, MicrosoftClientId)
            .WithSetting(ProviderSettings.MicrosoftClientSecretKey, MicrosoftClientSecret)
            .WithSetting(ProviderSettings.GitHubClientIdKey, GitHubClientId)
            .WithSetting(ProviderSettings.GitHubClientSecretKey, GitHubClientSecret);
```

In `tests/Auth.IntegrationTests/ManifestProvidersTests.cs`:

(a) in the theory `The_key_lists_the_providers_in_the_order_of_the_file`, after `    [InlineData("[google]", "google")]` add
`    [InlineData("[github, google, microsoft]", "github,google,microsoft")]   // spec 0012, criterion 1` and
`    [InlineData("[github]", "github")]`;

(b) in the theory `Any_other_value_makes_the_manifest_invalid`, replace `    [InlineData("[github]")]` with
`    [InlineData("[gitlab]")]   // spec 0012: github is a provider now; any other name still is not` and add after it
`    [InlineData("[GitHub]")]`;

(c) replace the body of `A_refused_name_is_named_in_the_reason`

```csharp
        var result = ManifestParser.Parse(Roles + "\nproviders: [google, github]\n");

        Assert.Contains("providers", result.Error, StringComparison.Ordinal);
        Assert.Contains("github", result.Error, StringComparison.Ordinal);
```

with

```csharp
        var result = ManifestParser.Parse(Roles + "\nproviders: [google, gitlab]\n");

        Assert.Contains("providers", result.Error, StringComparison.Ordinal);
        Assert.Contains("'gitlab'", result.Error, StringComparison.Ordinal);
        Assert.Contains("'github'", result.Error, StringComparison.Ordinal);   // the reason names the three names it accepts
```

(d) replace the body of `The_names_are_those_of_the_contract`

```csharp
        Assert.Equal(["google", "microsoft"], ProviderNames.All);
```

with

```csharp
        Assert.Equal(["google", "microsoft", "github"], ProviderNames.All);   // spec 0012
        Assert.True(ProviderNames.IsName("github"));
        Assert.False(ProviderNames.IsName("GitHub"));
        Assert.Equal("GitHub", ProviderNames.DisplayNameOf("github"));
```

(e) after `Duplicates_are_folded_and_the_first_place_is_kept` add

```csharp

    [Fact]
    public void A_github_listed_twice_is_folded_and_keeps_its_first_place()   // spec 0012, criterion 1
    {
        var manifest = Parsed(Roles + "\nproviders: [github, google, github]\n");

        Assert.Equal(["github", "google"], manifest.Providers);
        Assert.Equal(["github", "google"], Manifest.FromJson(manifest.ToJson())!.Providers);
    }
```

In `tests/Auth.IntegrationTests/ProviderOptionsTests.cs`:

(a) in `A_manifest_with_an_unknown_provider_keeps_the_last_valid_one_and_health_says_degraded`, replace
`            .WithManifest(AuthAppFactory.DefaultManifest + "\nproviders: [google, github]\n")` with
`            .WithManifest(AuthAppFactory.DefaultManifest + "\nproviders: [google, gitlab]\n")   // spec 0012: github is a provider now`;

(b) add before the class's last closing brace:

```csharp

    [Theory]
    [InlineData("", "github,google")]
    [InlineData(ProviderSettings.GitHubClientIdKey, "google")]
    [InlineData(ProviderSettings.GitHubClientSecretKey, "google")]
    public async Task GitHub_is_listed_in_the_order_of_the_manifest_only_with_both_its_keys(string blank, string expected)   // spec 0012, criterion 1
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + "\nproviders: [github, google]\n")
            .WithProviderKeys();
        if (blank.Length > 0)
        {
            factory.WithSetting(blank, "");
        }

        Assert.Equal(expected.Split(','), await ProvidersAsync(factory));
        Assert.Equal(blank.Length > 0 ? "Degraded" : "Healthy", await BodyAsync(factory, "/auth/health"));
    }

    [Fact]
    public async Task GitHub_without_the_return_address_is_not_offered()   // spec 0012 → Configuration: offered as spec 0011 says
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + "\nproviders: [github]\n")
            .WithProviderKeys()
            .WithSetting(ProviderSettings.ReturnUrlKey, "");

        Assert.Empty(await ProvidersAsync(factory));
        Assert.Equal("Degraded", await BodyAsync(factory, "/auth/health"));
    }

    [Fact]
    public async Task A_github_with_its_keys_that_is_not_listed_is_not_offered_and_health_is_healthy()   // spec 0012, criterion 1
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.ProvidersLine).WithProviderKeys();
        using var client = factory.CreateClient();

        Assert.False(factory.Services.GetRequiredService<ProviderCatalog>().IsOffered(ProviderNames.GitHub));
        Assert.Equal(["google", "microsoft"], await ProvidersAsync(factory));
        Assert.Equal("Healthy", await BodyAsync(factory, "/auth/health"));
    }
```

(The helpers `ProvidersAsync` and `BodyAsync` of the class are used as the class's other tests use them.)

In `tests/Auth.IntegrationTests/ProviderSettingsTests.cs`:

(a) in `Configured()`, after `        [ProviderSettings.MicrosoftClientSecretKey] = AuthAppFactory.MicrosoftClientSecret,` add
`        [ProviderSettings.GitHubClientIdKey] = AuthAppFactory.GitHubClientId,` and
`        [ProviderSettings.GitHubClientSecretKey] = AuthAppFactory.GitHubClientSecret,`;

(b) in `The_keys_are_the_ones_of_the_contract`, after the line of `MicrosoftSecretExpiresOnKey` add
`        Assert.Equal("Auth:Providers:GitHub:ClientId", ProviderSettings.GitHubClientIdKey);   // spec 0012` and
`        Assert.Equal("Auth:Providers:GitHub:ClientSecret", ProviderSettings.GitHubClientSecretKey);`;

(c) in `Nothing_set_is_valid_and_names_every_missing_key`, after `        Assert.Null(settings.Microsoft);` add `        Assert.Null(settings.GitHub);`, and
after the assertion of the missing keys of Google add

```csharp
        Assert.Equal(
            [ProviderSettings.GitHubClientIdKey, ProviderSettings.GitHubClientSecretKey, ProviderSettings.ReturnUrlKey],
            settings.MissingKeysOf(ProviderNames.GitHub));
```

(d) add before the class's last closing brace:

```csharp

    [Fact]
    public void GitHubs_keys_are_used_as_configured()   // spec 0012 → Configuration
    {
        var settings = Load(Configured());

        Assert.Equal(AuthAppFactory.GitHubClientId, settings.GitHub!.ClientId);
        Assert.Equal(AuthAppFactory.GitHubClientSecret, settings.KeysOf(ProviderNames.GitHub)!.ClientSecret);
        Assert.Empty(settings.MissingKeysOf(ProviderNames.GitHub));
        Assert.Equal(new Uri("https://app.example.com/auth/providers/github/callback"), settings.CallbackUriOf(ProviderNames.GitHub));
    }

    [Fact]
    public async Task A_listed_github_without_its_keys_warns_once_naming_them_and_health_says_degraded()   // spec 0012, criterion 1
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.AllProvidersLine)
            .WithProviderKeys()
            .WithSetting(ProviderSettings.GitHubClientIdKey, "")
            .WithSetting(ProviderSettings.GitHubClientSecretKey, "")
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        Assert.Equal("""{"signup":"invite-only","private_signup":"off","providers":["google","microsoft"]}""", await BodyAsync(factory, "/auth/options"));
        Assert.Equal("Degraded", await BodyAsync(factory, "/auth/health"));
        var warning = Assert.Single(ProviderWarnings(logs));
        Assert.Contains("github", warning.Message, StringComparison.Ordinal);
        Assert.Contains(ProviderSettings.GitHubClientIdKey, warning.Message, StringComparison.Ordinal);
        Assert.Contains(ProviderSettings.GitHubClientSecretKey, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(AuthAppFactory.GoogleClientSecret, StringComparison.Ordinal));
    }
```

In `tests/Auth.IntegrationTests/ProviderPathsTests.cs`, replace `    [InlineData("/auth/providers/github/callback", false)]` with
`    [InlineData("/auth/providers/github/callback", true)]   // spec 0012` and add after it `    [InlineData("/auth/providers/gitlab/callback", false)]`.

In `tests/Auth.IntegrationTests/ProviderStartTests.cs`:

(a) in the theory `An_unknown_name_is_404_not_found`, replace `    [InlineData("github")]` with `    [InlineData("gitlab")]   // spec 0012: github is a name now`;

(b) in the theory `An_unknown_name_with_a_bad_query_is_404_not_400`, replace `    [InlineData("github", "?purpose=login")]` with
`    [InlineData("gitlab", "?purpose=login")]`;

(c) after `A_provider_that_is_not_offered_is_404_not_found` add

```csharp

    [Fact]
    public async Task GitHub_that_the_manifest_does_not_list_is_404_not_found()   // spec 0012 → Configuration: keyed is not offered
    {
        using var response = await Browser.GetAsync("/auth/providers/github/start");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
    }
```

In `tests/Auth.IntegrationTests/ProviderCallbackErrorTests.cs`, in `A_name_that_is_no_provider_is_404_and_a_post_is_405`, replace
`"/auth/providers/github/callback?code=x&state=y"` with `"/auth/providers/gitlab/callback?code=x&state=y"`.

In `tests/Auth.IntegrationTests/UpgradeFromSpec0011Tests.cs`, in
`An_instance_with_the_settings_of_spec_0011_starts_healthy_offers_its_providers_and_issues_the_same_tokens`, replace

```csharp
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup().WithProviders();
```

with

```csharp
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup().WithProviders()
            .WithSetting(ProviderSettings.GitHubClientIdKey, "")   // spec 0011's settings: no GitHub keys
            .WithSetting(ProviderSettings.GitHubClientSecretKey, "");
```

and add `using Auth.Server.Providers;` to its usings.

In `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`:

(a) in `OverlaySettings`, replace

```csharp
                settings[trimmed[..colon].Replace("__", ":", StringComparison.Ordinal)] = trimmed[(colon + 2)..];
```

with

```csharp
                // A quoted value ("true") is the text inside the quotes, as Compose reads it.
                settings[trimmed[..colon].Replace("__", ":", StringComparison.Ordinal)] = trimmed[(colon + 2)..].Trim('"');
```

(b) replace the whole method `The_notes_manifest_lists_both_providers_and_the_development_manifest_none` with

```csharp
    public void The_notes_manifest_lists_the_three_providers_and_the_development_manifest_none()   // spec 0012 → Sample backend
    {
        var notes = ManifestParser.Parse(File.ReadAllText(RepositoryFile("samples/notes-api/auth.yaml")));
        var development = ManifestParser.Parse(File.ReadAllText(RepositoryFile("deploy/auth.yaml")));

        Assert.Null(notes.Error);
        Assert.Equal(["google", "microsoft", "github"], notes.Manifest!.Providers);
        Assert.Null(development.Error);
        Assert.Empty(development.Manifest!.Providers);
        Assert.Contains("# providers: [google, microsoft, github]", File.ReadAllText(RepositoryFile("deploy/auth.yaml")), StringComparison.Ordinal);
    }
```

(keep its `[Fact]` line above it);

(c) replace the whole method `The_fake_overlay_names_the_test_issuer_both_keys_and_the_return_address_and_the_notes_stacks_use_it` with

```csharp
    public void The_fake_overlay_names_the_test_issuer_the_keys_the_return_address_and_the_testing_endpoint_and_the_notes_stacks_use_it()   // specs 0011 and 0012
    {
        var settings = OverlaySettings();

        Assert.Equal("http://fake-oidc:8099", settings[ProviderSettings.TestIssuerKey]);
        Assert.Equal("https://localhost:8443/provider-return", settings[ProviderSettings.ReturnUrlKey]);
        Assert.All(
            [
                ProviderSettings.GoogleClientIdKey, ProviderSettings.GoogleClientSecretKey, ProviderSettings.MicrosoftClientIdKey,
                ProviderSettings.MicrosoftClientSecretKey, ProviderSettings.GitHubClientIdKey, ProviderSettings.GitHubClientSecretKey,
            ],
            key => Assert.False(string.IsNullOrWhiteSpace(settings[key])));
        Assert.Equal("true", settings[TestingSettings.WindowEndpointKey]);   // spec 0012: the browser tests end a window by it
        Assert.All(
            ["scripts/e2e-web.sh", "scripts/e2e-notes.sh", "scripts/e2e-providers.sh"],
            script => Assert.Contains("tests/Auth.FakeOidc/compose.yml", ComposeCommand(script), StringComparison.Ordinal));
    }
```

(d) in `A_development_host_with_the_notes_manifest_and_the_overlay_offers_both_providers_and_is_healthy`, rename the method to
`A_development_host_with_the_notes_manifest_and_the_overlay_offers_the_three_providers_and_is_healthy` and replace

```csharp
        Assert.Equal(["google", "microsoft"], factory.Services.GetRequiredService<ProviderCatalog>().Offered);
        Assert.Equal(
            """{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft"]}""",
```

with

```csharp
        Assert.Equal(["google", "microsoft", "github"], factory.Services.GetRequiredService<ProviderCatalog>().Offered);
        Assert.Equal(
            """{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft","github"]}""",
```

(e) in the theory `The_production_compose_file_passes_each_provider_variable_as_optional_and_the_example_lists_it`, after the row of
`AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES` add
`    [InlineData("Auth__Providers__GitHub__ClientId", "AUTH_GITHUB_CLIENT_ID")]   // spec 0012` and
`    [InlineData("Auth__Providers__GitHub__ClientSecret", "AUTH_GITHUB_CLIENT_SECRET")]`;

(f) rename `A_production_host_with_the_six_provider_variables_blank_is_healthy_and_offers_no_provider` to
`A_production_host_with_the_eight_provider_variables_blank_is_healthy_and_offers_no_provider`, and in it replace
`            .WithSetting(ProviderSettings.MicrosoftSecretExpiresOnKey, "");` with

```csharp
            .WithSetting(ProviderSettings.MicrosoftSecretExpiresOnKey, "")
            .WithSetting(ProviderSettings.GitHubClientIdKey, "")
            .WithSetting(ProviderSettings.GitHubClientSecretKey, "");
```

(only in that method: the next method keeps its six lines; the factory's pins blank GitHub's two anyway);

(g) add `using Auth.Server.Testing;` to the usings.

- [ ] **Step 2: See them fail.** Build: it fails (`ProviderSettings.GitHubClientIdKey` and `ProviderNames.GitHub` do not exist).

- [ ] **Step 3: The name.** In `src/Auth.Server/Providers/ProviderNames.cs`, replace the class body

```csharp
    public const string Google = "google";
    public const string Microsoft = "microsoft";

    public static IReadOnlyList<string> All { get; } = [Google, Microsoft];

    /// <summary>Exactly one of the two names, in lower case. Anything else is not a provider.</summary>
    public static bool IsName(string? name) =>
        string.Equals(name, Google, StringComparison.Ordinal) || string.Equals(name, Microsoft, StringComparison.Ordinal);

    /// <summary>The name a person reads: <c>Google</c> or <c>Microsoft</c>.</summary>
    public static string DisplayNameOf(string name) => name switch
    {
        Google => "Google",
        Microsoft => "Microsoft",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a provider name."),
    };
```

with

```csharp
    public const string Google = "google";
    public const string Microsoft = "microsoft";
    public const string GitHub = "github";

    /// <summary>The providers in a fixed order (the redirection paths of OpenIddict's client follow it); the manifest orders the buttons.</summary>
    public static IReadOnlyList<string> All { get; } = [Google, Microsoft, GitHub];

    /// <summary>Exactly one of the three names, in lower case (spec 0012). Anything else is not a provider.</summary>
    public static bool IsName(string? name) => name is not null && All.Contains(name, StringComparer.Ordinal);

    /// <summary>The name a person reads: <c>Google</c>, <c>Microsoft</c> or <c>GitHub</c>.</summary>
    public static string DisplayNameOf(string name) => name switch
    {
        Google => "Google",
        Microsoft => "Microsoft",
        GitHub => "GitHub",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a provider name."),
    };
```

and replace the class summary `/// <summary>The providers of spec 0011 by the names the contract uses everywhere: lower case, in paths, the manifest, answers and audit rows.</summary>`
with `/// <summary>The providers of specs 0011 and 0012 by the names the contract uses everywhere: lower case, in paths, the manifest, answers and audit rows.</summary>`.

- [ ] **Step 4: The keys.** In `src/Auth.Server/Providers/ProviderSettings.cs`:

(a) replace

```csharp
    public const string MicrosoftSecretExpiresOnKey = "Auth:Providers:Microsoft:ClientSecretExpiresOn";
```

with

```csharp
    public const string MicrosoftSecretExpiresOnKey = "Auth:Providers:Microsoft:ClientSecretExpiresOn";
    public const string GitHubClientIdKey = "Auth:Providers:GitHub:ClientId";
    public const string GitHubClientSecretKey = "Auth:Providers:GitHub:ClientSecret";
```

(b) replace

```csharp
    public ProviderKeys? Microsoft { get; private init; }
```

with

```csharp
    public ProviderKeys? Microsoft { get; private init; }

    /// <summary>The client id and secret of the GitHub OAuth App (spec 0012).</summary>
    public ProviderKeys? GitHub { get; private init; }
```

(c) replace

```csharp
        ProviderNames.Microsoft => Microsoft,
        _ => null,
```

with

```csharp
        ProviderNames.Microsoft => Microsoft,
        ProviderNames.GitHub => GitHub,
        _ => null,
```

(d) replace

```csharp
        missing[ProviderNames.Microsoft] = microsoftMissing;

        return new ProviderSettings(missing)
        {
            ReturnUrl = returnUrl,
            Google = google,
            Microsoft = microsoft,
```

with

```csharp
        missing[ProviderNames.Microsoft] = microsoftMissing;
        // Every name of ProviderNames.All needs its entry: MissingKeysOf is a lookup, and the catalog asks it for each listed provider.
        var gitHub = KeysAt(configuration, GitHubClientIdKey, GitHubClientSecretKey, returnUrl is null, out var gitHubMissing);
        missing[ProviderNames.GitHub] = gitHubMissing;

        return new ProviderSettings(missing)
        {
            ReturnUrl = returnUrl,
            Google = google,
            Microsoft = microsoft,
            GitHub = gitHub,
```

(e) replace the summary line `/// <summary>The issuer of a provider under the test issuer: <c>{TestIssuer}/google</c> or <c>{TestIssuer}/microsoft</c>.</summary>` with
`/// <summary>The issuer of a provider under the test issuer: <c>{TestIssuer}/google</c>, <c>{TestIssuer}/microsoft</c> or <c>{TestIssuer}/github</c>.</summary>`.

- [ ] **Step 5: The manifest's reason.** In `src/Auth.Server/Tenancy/ManifestParser.cs`, replace

```csharp
        // Spec 0011: the key `providers`. `providers:` with no value is none; every name must be one of the two exactly; duplicates fold and the
        // first place is kept, because the order is the order of the buttons.
        var providersListed = (document.Providers ?? []).Select(p => p ?? "").ToList();
        if (providersListed.FirstOrDefault(p => !ProviderNames.IsName(p)) is { } refused)
        {
            return Fail($"providers holds '{refused}', which is not '{ProviderNames.Google}' or '{ProviderNames.Microsoft}'");
        }
```

with

```csharp
        // Specs 0011 and 0012: the key `providers`. `providers:` with no value is none; every name must be one of the three exactly; duplicates
        // fold and the first place is kept, because the order is the order of the buttons.
        var providersListed = (document.Providers ?? []).Select(p => p ?? "").ToList();
        if (providersListed.FirstOrDefault(p => !ProviderNames.IsName(p)) is { } refused)
        {
            return Fail($"providers holds '{refused}', which is not one of {string.Join(", ", ProviderNames.All.Select(name => "'" + name + "'"))}");
        }
```

- [ ] **Step 6: The shipped configuration.** In `tests/Auth.FakeOidc/compose.yml`, replace

```yaml
      Auth__Providers__Microsoft__ClientSecret: fake-microsoft-secret
```

with

```yaml
      Auth__Providers__Microsoft__ClientSecret: fake-microsoft-secret
      # Spec 0012: GitHub through the fake's /github test issuer.
      Auth__Providers__GitHub__ClientId: fake-github-client-id
      Auth__Providers__GitHub__ClientSecret: fake-github-secret
      # Spec 0012 (Development only): POST /auth/testing/end-confirmation ends a confirmation window, for the browser tests and
      # scripts/e2e-settings.sh, so that the real 5 minutes need not pass.
      Auth__Testing__WindowEndpoint: "true"
```

and in its header comment replace `# Auth-Core trusts it as its test issuer (Development only; any other environment refuses to start with it): the two providers are`
`# http://fake-oidc:8099/google and http://fake-oidc:8099/microsoft on the compose network, and a browser reaches the fake's sign-in page`
with `# Auth-Core trusts it as its test issuer (Development only; any other environment refuses to start with it): the three providers are`
`# http://fake-oidc:8099/google, /microsoft and /github on the compose network, and a browser reaches the fake's sign-in page`
(two lines each; the third line of that sentence stays).

In `samples/notes-api/auth.yaml`, replace `providers: [google, microsoft]` with `providers: [google, microsoft, github]` and
`# providers      the sign-in providers of spec 0011, in the order of the buttons.` with
`# providers      the sign-in providers of specs 0011 and 0012, in the order of the buttons.`.

In `deploy/auth.yaml`, replace `# providers      the sign-in providers of spec 0011 (google, microsoft), offered in the order of the list. Each needs its keys and the`
with `# providers      the sign-in providers of specs 0011 and 0012 (google, microsoft, github), offered in the order of the list. Each needs its keys and the`
and `# providers: [google, microsoft]` with `# providers: [google, microsoft, github]`.

In `deploy/docker-compose.prod.yml`, replace

```yaml
      Auth__Providers__Microsoft__ClientSecretExpiresOn: ${AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES:-}
```

with

```yaml
      Auth__Providers__Microsoft__ClientSecretExpiresOn: ${AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES:-}
      # Optional (spec 0012): sign-in with GitHub, through a GitHub OAuth App. The manifest lists `github`; register
      # <origin of the return address>/auth/providers/github/callback as the app's "Authorization callback URL" (docs/deployment/provider-keys.md).
      Auth__Providers__GitHub__ClientId: ${AUTH_GITHUB_CLIENT_ID:-}
      Auth__Providers__GitHub__ClientSecret: ${AUTH_GITHUB_CLIENT_SECRET:-}
```

In `deploy/.env.prod.example` (CRLF: use the Edit tool), replace

```
AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES=
```

with

```
AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES=
# Optional (spec 0012): GitHub. GitHub -> Settings -> Developer settings -> OAuth Apps: the app's "Client ID" and a client secret it generated.
AUTH_GITHUB_CLIENT_ID=
AUTH_GITHUB_CLIENT_SECRET=
```

- [ ] **Step 7: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ManifestProvidersTests -class Auth.IntegrationTests.ProviderOptionsTests -class Auth.IntegrationTests.ProviderSettingsTests -class Auth.IntegrationTests.ProviderPathsTests -class Auth.IntegrationTests.ProviderStartTests -class Auth.IntegrationTests.ProviderCallbackErrorTests -class Auth.IntegrationTests.ShippedConfigurationTests -class Auth.IntegrationTests.UpgradeFromSpec0011Tests -class Auth.IntegrationTests.ProviderTestIssuerTests -class Auth.IntegrationTests.ProviderCallbackTests -class Auth.IntegrationTests.ManifestParserTests -class Auth.IntegrationTests.SignupOptionsTests`
passes. `ProviderTestIssuerTests` and `ProviderCallbackTests` run with GitHub's keys now and must still pass (GitHub is not listed there).

- [ ] **Step 8: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files (the YAML files and the `.cs` files).
Proposed subject: `feat(server): the manifest and the settings know GitHub`.

---

### Task 4: The fake GitHub

**Files:**
- Create: `tests/Auth.FakeOidc/FakeGitHub.cs`
- Modify: `tests/Auth.FakeOidc/FakeOidcServer.cs`, `tests/Auth.FakeOidc/FakePeople.cs`
- Create (tests): `tests/Auth.IntegrationTests/FakeGitHubTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/FakeOidcTests.cs`

**Interfaces:**
- Consumes: `FakeSignIn`, `FakeOidcOptions`, the key set of `FakeOidcServer`.
- Produces: `FakeOidcServer.GitHub` (a `FakeGitHub`): `IssuedTokens`, `Revocations` (`GitHubRevocation(string ClientId, string Token, int Status)`),
  `Requests` (`"GET /user"`, `"GET /user/emails?per_page=100"`, `"POST /login/oauth/access_token"`, `"DELETE /applications/{id}/token"`, in order),
  `SlowRequestArrived`; `FakeGitHub.WebIssuer`, `FakeGitHub.InspectionPath` (`/_fake/github/revocations`); `FakeKinds.GitHub`; the people
  `FakePeople.GitHub` (`ola@github.test`, primary and verified, a verified secondary `ola.work@firma.test`, the public `/user` address
  `ola.public@github.test`, id `70000001`), `FakePeople.GitHubUnverified` (`kim@github.test`, primary, not verified, `70000002`),
  `FakePeople.GitHubNoPrimary` (two verified entries, neither primary, `70000003`), `FakePeople.GitHubIdOf(string email)` (the id of a free
  address); the defects `FakeDefects.NoIss`, `NoToken`, `UserFails`, `UserNotJson`, `UserNoId`, `UserBadId`, `UserTooLarge`, `UserSlow`,
  `EmailsFails`, `EmailsNotJson`, `EmailsBadEntry`, `EmailsTooLarge`, `EmailsSlow`, `RevokeFails`, `RevokeSlow` (with `RefuseCode`, `ProviderError` and the
  person `Cancel` of spec 0011), and `FakeDefects.IsGitHubOnly` / `IsOpenIdOnly` (each fake refuses the other kind's defects).

**Contract counts:** `FakeOidcTests.An_unknown_path_is_404` (`/github/authorize` is served now).

**Decisions the plan takes here:**
- One class of its own, `FakeGitHub`, beside `FakeOidcServer`, which hands it every request first: GitHub is OAuth 2.0 (no identity token, no
  nonce) and shares nothing with the two OpenID providers but the key set of the test issuer's document.
- It answers as GitHub by host (`github.com`: the authorization and token endpoints; `api.github.com`: `/user`, `/user/emails`, the revocation)
  and under `/github/...` on any other host: the minimal discovery document of Amendment A5 (the authorization endpoint at the browser's origin,
  the rest at the issuer's), the key set, and the same endpoints. `iss` goes on the answers of the `github.com` host only (Amendment A6).
- The token endpoint answers as GitHub does: `200` with `{"error":"bad_verification_code"}` for a refused code, JSON when the request asks for it
  (OpenIddict does) and form-encoded otherwise; tokens look like GitHub's (`gho_...`). `/user` and `/user/emails` need the bearer token, a
  `User-Agent` and GitHub's `Accept`, or answer `403`/`401` as GitHub does; a revoked token is unknown afterwards.
- Every revocation is recorded with its status, the token included (the tests compare it with the issued ones); the inspection endpoint for the
  stack lists the client id and the status only, never a token.
- `user_slow`, `emails_slow` and `revoke_slow` hold the answer until the request is aborted or the fake is disposed; `SlowRequestArrived` lets a test move
  the clock then (a test uses one slow defect). A held revocation is recorded at once with the status `0` (no answer) and the token stays valid.
- A defect that cannot apply is refused, never ignored: the OpenID fakes refuse GitHub's defects (`FakeDefects.IsGitHubOnly`) and the fake GitHub
  refuses the claim and identity-token defects (`FakeDefects.IsOpenIdOnly`), so that a typo of the e2e script (`defect=revoke_fails` on a Google
  sign-in) never yields a good identity.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/FakeGitHubTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012, criterion 32: the fake provider as GitHub, by host and under the test issuer's <c>/github</c>.</summary>
public sealed class FakeGitHubTests : IAsyncDisposable
{
    private const string Verifier = "a-verifier-of-at-least-forty-three-characters-0123456789";
    private const string ClientId = "the-github-client";
    private const string Callback = "https://app.example.com/auth/providers/github/callback";
    private const string Authorize = "https://github.com/login/oauth/authorize";
    private const string TokenEndpoint = "https://github.com/login/oauth/access_token";
    private const string Api = "https://api.github.com";

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FakeOidcHost _fake;

    public FakeGitHubTests()
    {
        _fake = new FakeOidcHost(_clock);
    }

    private FakeGitHub GitHub => _fake.Server.GitHub;

    public async ValueTask DisposeAsync()
    {
        await _fake.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static string Challenge(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string AuthorizeUrl(string endpoint = Authorize, string scope = "user:email", string? method = "S256") =>
        QueryHelpers.AddQueryString(endpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = Callback,
            ["scope"] = scope,
            ["state"] = "the-state",
            ["code_challenge"] = Challenge(Verifier),
            ["code_challenge_method"] = method,
        });

    /// <summary>The query of the fake's redirect back to the callback.</summary>
    private async Task<Dictionary<string, string>> AuthorizeAsync(FakeSignIn person, string endpoint = Authorize)
    {
        using var response = await _fake.Client.GetAsync(person.AppendTo(AuthorizeUrl(endpoint)), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(Callback + "?", location.AbsoluteUri, StringComparison.Ordinal);
        return QueryHelpers.ParseQuery(location.Query).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
    }

    private async Task<HttpResponseMessage> RedeemAsync(string code, string verifier = Verifier, bool json = true, string endpoint = TokenEndpoint)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = Callback,
                ["client_id"] = ClientId,
                ["client_secret"] = "any-secret",
                ["code_verifier"] = verifier,
            }),
        };
        if (json)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        return await _fake.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>A whole round trip at the fake: the access token GitHub would give for this person.</summary>
    private async Task<string> AccessTokenAsync(FakeSignIn person)
    {
        using var redeemed = await RedeemAsync((await AuthorizeAsync(person))["code"]);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        return JsonNode.Parse(await redeemed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["access_token"]!.GetValue<string>();
    }

    private async Task<HttpResponseMessage> ApiAsync(string path, string? token, bool asGitHubAsks = true, string origin = Api, CancellationToken? cancel = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, origin + path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (asGitHubAsks)
        {
            request.Headers.UserAgent.ParseAdd("Auth-Core");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
        }

        return await _fake.Client.SendAsync(request, cancel ?? TestContext.Current.CancellationToken);
    }

    private async Task<JsonNode> JsonOfAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    private async Task<HttpResponseMessage> RevokeAsync(string token, string clientId = ClientId, string? basicId = ClientId, string origin = Api, CancellationToken? cancel = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{origin}/applications/{clientId}/token")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { access_token = token }), Encoding.UTF8, "application/json"),
        };
        if (basicId is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(basicId + ":any-secret")));
        }

        return await _fake.Client.SendAsync(request, cancel ?? TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_authorization_answers_at_once_with_a_code_the_state_and_githubs_iss()   // criterion 32, Amendment A6
    {
        var answer = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub));

        Assert.Equal(32, answer["code"].Length);
        Assert.Equal("the-state", answer["state"]);
        Assert.Equal(FakeGitHub.WebIssuer, answer["iss"]);
    }

    [Fact]
    public async Task The_test_issuer_answers_without_iss_and_the_no_iss_defect_leaves_it_out_at_github()   // Amendment A6
    {
        var issuer = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub), "http://issuer.test/github/authorize");
        var withoutIss = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.NoIss));

        Assert.False(issuer.ContainsKey("iss"));
        Assert.False(withoutIss.ContainsKey("iss"));
        Assert.True(withoutIss.ContainsKey("code"));
    }

    [Theory]
    [InlineData(FakePeople.Cancel, null, "access_denied")]
    [InlineData(FakePeople.GitHub, FakeDefects.ProviderError, "server_error")]
    public async Task Cancel_and_the_provider_error_defect_answer_their_error_with_the_state_and_iss(string person, string? defect, string error)   // criterion 32
    {
        var answer = await AuthorizeAsync(new FakeSignIn(person, Defect: defect));

        Assert.Equal(error, answer["error"]);
        Assert.Equal(FakeOidcServer.RefusalDescription, answer["error_description"]);
        Assert.Equal("the-state", answer["state"]);
        Assert.Equal(FakeGitHub.WebIssuer, answer["iss"]);
        Assert.False(answer.ContainsKey("code"));
    }

    [Theory]
    [InlineData("openid email profile", "S256")]
    [InlineData("user:email", null)]
    [InlineData("user:email", "plain")]
    public async Task An_authorization_request_without_pkce_s256_or_the_scope_user_email_is_refused(string scope, string? method)   // spec 0012 → start
    {
        using var response = await _fake.Client.GetAsync(
            new FakeSignIn(FakePeople.GitHub).AppendTo(AuthorizeUrl(scope: scope, method: method)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_person_the_page_offers_the_three_people_a_free_address_and_cancel()   // criterion 32: what a browser sees
    {
        using var response = await _fake.Client.GetAsync(AuthorizeUrl(), TestContext.Current.CancellationToken);
        var page = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1>Sign in to the fake GitHub</h1>", page, StringComparison.Ordinal);
        Assert.Contains("person=github\"", page, StringComparison.Ordinal);   // the link ends with the person: the closing quote tells github from github-unverified
        Assert.Contains("person=github-unverified\"", page, StringComparison.Ordinal);
        Assert.Contains("person=github-no-primary\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"person\" value=\"custom\"", page, StringComparison.Ordinal);
        Assert.Contains("The provider verified this address", page, StringComparison.Ordinal);
        Assert.Contains("person=cancel", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_endpoint_redeems_a_code_once_with_its_verifier_in_json_or_as_a_form()   // criterion 32: as GitHub answers
    {
        var json = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub));
        var form = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub));

        using var asJson = await RedeemAsync(json["code"]);
        using var again = await RedeemAsync(json["code"]);
        using var asForm = await RedeemAsync(form["code"], json: false);

        var body = await JsonOfAsync(asJson);
        Assert.StartsWith("gho_", body["access_token"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("bearer", body["token_type"]!.GetValue<string>());
        Assert.Equal("user:email", body["scope"]!.GetValue<string>());
        Assert.Equal("bad_verification_code", (await JsonOfAsync(again))["error"]!.GetValue<string>());   // used once
        Assert.StartsWith("access_token=gho_", await asForm.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(2, GitHub.IssuedTokens.Count);
    }

    [Fact]
    public async Task A_wrong_verifier_or_a_refused_code_is_a_200_with_bad_verification_code_and_no_token()   // criterion 32: GitHub answers 200
    {
        var wrong = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub));
        var refused = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.RefuseCode));

        using var byVerifier = await RedeemAsync(wrong["code"], "another-verifier-of-at-least-forty-three-characters-000");
        using var byDefect = await RedeemAsync(refused["code"]);

        Assert.All([await JsonOfAsync(byVerifier), await JsonOfAsync(byDefect)], body =>
        {
            Assert.Equal("bad_verification_code", body["error"]!.GetValue<string>());
            Assert.Null(body["access_token"]);
        });
        Assert.Empty(GitHub.IssuedTokens);
    }

    [Fact]
    public async Task The_no_token_defect_answers_200_without_an_access_token()   // Amendment A3's input
    {
        var answer = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.NoToken));

        using var redeemed = await RedeemAsync(answer["code"]);

        var body = await JsonOfAsync(redeemed);
        Assert.Null(body["access_token"]);
        Assert.Equal("bearer", body["token_type"]!.GetValue<string>());
    }

    [Fact]
    public async Task User_and_emails_answer_the_documented_json_for_the_bearer_token()   // criterion 32, U13
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub));

        using var user = await ApiAsync("/user", token);
        using var emails = await ApiAsync("/user/emails?per_page=100", token);

        var me = await JsonOfAsync(user);
        Assert.Equal(JsonValueKind.Number, me["id"]!.GetValueKind());
        Assert.Equal(long.Parse(FakePeople.GitHubId, System.Globalization.CultureInfo.InvariantCulture), me["id"]!.GetValue<long>());
        Assert.Equal("ola-gh", me["login"]!.GetValue<string>());
        Assert.Equal(FakePeople.GitHubPublicEmail, me["email"]!.GetValue<string>());
        var entries = (await JsonOfAsync(emails)).AsArray();
        Assert.Equal(
            [(FakePeople.GitHubEmail, true, true), (FakePeople.GitHubSecondaryEmail, false, true)],
            entries.Select(e => (e!["email"]!.GetValue<string>(), e["primary"]!.GetValue<bool>(), e["verified"]!.GetValue<bool>())));
        Assert.Equal(["GET /user", "GET /user/emails?per_page=100"], GitHub.Requests.Where(r => r.StartsWith("GET", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Without_a_user_agent_and_githubs_accept_it_is_403_and_without_a_known_token_401()   // as GitHub answers
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub));

        using var bare = await ApiAsync("/user", token, asGitHubAsks: false);
        using var unknown = await ApiAsync("/user", "gho_unknown");
        using var none = await ApiAsync("/user", null);

        Assert.Equal(HttpStatusCode.Forbidden, bare.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
    }

    [Theory]
    [InlineData(FakePeople.GitHubUnverified, FakePeople.GitHubUnverifiedId, "kim@github.test", true, false)]
    [InlineData(FakePeople.GitHubNoPrimary, FakePeople.GitHubNoPrimaryId, "lee@github.test", false, true)]
    public async Task The_other_two_people_of_the_spec_are_there(string person, string id, string firstEmail, bool primary, bool verified)   // criterion 32
    {
        var token = await AccessTokenAsync(new FakeSignIn(person));

        using var user = await ApiAsync("/user", token);
        using var emails = await ApiAsync("/user/emails?per_page=100", token);

        Assert.Equal(id, (await JsonOfAsync(user))["id"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture));
        var first = (await JsonOfAsync(emails)).AsArray()[0]!;
        Assert.Equal((firstEmail, primary, verified), (first["email"]!.GetValue<string>(), first["primary"]!.GetValue<bool>(), first["verified"]!.GetValue<bool>()));
        if (person == FakePeople.GitHubNoPrimary)
        {
            Assert.All((await JsonOfAsync(await ApiAsync("/user/emails?per_page=100", token))).AsArray(), e => Assert.False(e!["primary"]!.GetValue<bool>()));
        }
    }

    [Fact]
    public async Task A_free_address_is_the_same_github_identity_on_every_round_trip_and_verified_only_when_ticked()   // criterion 32: free-address mode
    {
        var once = await AccessTokenAsync(new FakeSignIn(FakePeople.Custom, "anna@firma.test", Verified: true));
        var twice = await AccessTokenAsync(new FakeSignIn(FakePeople.Custom, "ANNA@firma.test"));

        var first = await JsonOfAsync(await ApiAsync("/user", once));
        var second = await JsonOfAsync(await ApiAsync("/user", twice));
        var ticked = (await JsonOfAsync(await ApiAsync("/user/emails?per_page=100", once))).AsArray().Single()!;
        var unticked = (await JsonOfAsync(await ApiAsync("/user/emails?per_page=100", twice))).AsArray().Single()!;

        Assert.Equal(first["id"]!.GetValue<long>(), second["id"]!.GetValue<long>());   // derived from the address, whatever its case
        Assert.Equal(FakePeople.GitHubIdOf("anna@firma.test"), first["id"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(first["id"]!.GetValue<long>() > 0);
        Assert.Equal(("anna@firma.test", true, true), (ticked["email"]!.GetValue<string>(), ticked["primary"]!.GetValue<bool>(), ticked["verified"]!.GetValue<bool>()));
        Assert.False(unticked["verified"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(FakeDefects.UserFails, "/user", 500)]
    [InlineData(FakeDefects.EmailsFails, "/user/emails?per_page=100", 500)]
    public async Task A_failing_read_answers_500_on_demand(string defect, string path, int status)   // criterion 32
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: defect));

        using var response = await ApiAsync(path, token);

        Assert.Equal(status, (int)response.StatusCode);
    }

    [Theory]
    [InlineData(FakeDefects.UserNotJson, "/user")]
    [InlineData(FakeDefects.EmailsNotJson, "/user/emails?per_page=100")]
    public async Task An_answer_that_is_not_json_is_html_on_demand(string defect, string path)
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: defect));

        using var response = await ApiAsync(path, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_shape_defects_change_what_user_and_emails_say()   // the inputs of criterion 5
    {
        var noId = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.UserNoId));
        var badId = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.UserBadId));
        var large = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.UserTooLarge));
        var badEntry = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.EmailsBadEntry));
        var largeEmails = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.EmailsTooLarge));

        Assert.Null((await JsonOfAsync(await ApiAsync("/user", noId)))["id"]);
        Assert.Equal(JsonValueKind.String, (await JsonOfAsync(await ApiAsync("/user", badId)))["id"]!.GetValueKind());
        using var largeAnswer = await ApiAsync("/user", large);
        Assert.True((await largeAnswer.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length > 64 * 1024);
        Assert.Equal(JsonValueKind.String, (await JsonOfAsync(await ApiAsync("/user/emails?per_page=100", badEntry))).AsArray()[0]!["primary"]!.GetValueKind());
        using var largeEmailsAnswer = await ApiAsync("/user/emails?per_page=100", largeEmails);
        var largeBytes = await largeEmailsAnswer.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Assert.True(largeBytes.Length > 64 * 1024);
        Assert.True(JsonNode.Parse(largeBytes)!.AsArray()[0]!["primary"]!.GetValue<bool>());   // documented JSON, only too large
    }

    [Theory]
    [InlineData(FakeDefects.UserSlow, "/user")]
    [InlineData(FakeDefects.EmailsSlow, "/user/emails?per_page=100")]
    public async Task The_slow_defects_hold_the_answer_until_the_request_is_aborted(string defect, string path)   // the input of the 10-second bound
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: defect));
        using var abort = new CancellationTokenSource();

        var pending = ApiAsync(path, token, cancel: abort.Token);
        await GitHub.SlowRequestArrived.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task A_slow_revocation_is_held_until_the_request_is_aborted_recorded_without_an_answer_and_the_token_stays()   // the input of the revocation's own bound
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.RevokeSlow));
        using var abort = new CancellationTokenSource();

        var pending = RevokeAsync(token, cancel: abort.Token);
        await GitHub.SlowRequestArrived.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(new GitHubRevocation(ClientId, token, 0), Assert.Single(GitHub.Revocations));
        using var after = await ApiAsync("/user", token);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task A_revocation_with_the_clients_basic_credentials_answers_204_records_the_token_and_ends_it()   // criterion 32
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub));

        using var revoked = await RevokeAsync(token);
        using var after = await ApiAsync("/user", token);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(new GitHubRevocation(ClientId, token, 204), Assert.Single(GitHub.Revocations));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task A_failed_revocation_is_recorded_with_its_status_and_the_token_stays()   // criterion 32: a failing revocation on demand
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub, Defect: FakeDefects.RevokeFails));

        using var revoked = await RevokeAsync(token);
        using var after = await ApiAsync("/user", token);

        Assert.Equal(HttpStatusCode.InternalServerError, revoked.StatusCode);
        Assert.Equal(500, Assert.Single(GitHub.Revocations).Status);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task A_revocation_without_the_clients_credentials_is_401_and_recorded()
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub));

        using var anonymous = await RevokeAsync(token, basicId: null);
        using var another = await RevokeAsync(token, basicId: "another-client");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, another.StatusCode);
        Assert.Equal([401, 401], GitHub.Revocations.Select(r => r.Status));
    }

    [Fact]
    public async Task The_test_issuer_serves_a_minimal_discovery_document_and_a_key_set_under_github()   // Amendment A5
    {
        await using var stack = new FakeOidcHost(_clock, new FakeOidcOptions
        {
            IssuerOrigin = new Uri("http://fake-oidc:8099"),
            BrowserOrigin = new Uri("http://localhost:8099"),
        });
        using var discovery = await stack.Client.GetAsync("http://fake-oidc:8099/github/.well-known/openid-configuration", TestContext.Current.CancellationToken);
        var document = JsonNode.Parse(await discovery.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
        using var keys = await stack.Client.GetAsync("http://fake-oidc:8099/github/keys", TestContext.Current.CancellationToken);

        Assert.Equal("http://fake-oidc:8099/github", document["issuer"]!.GetValue<string>());
        Assert.Equal("http://localhost:8099/github/authorize", document["authorization_endpoint"]!.GetValue<string>());
        Assert.Equal("http://fake-oidc:8099/github/token", document["token_endpoint"]!.GetValue<string>());
        Assert.Equal("http://fake-oidc:8099/github/keys", document["jwks_uri"]!.GetValue<string>());
        Assert.Equal(["code"], document["response_types_supported"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(["S256"], document["code_challenge_methods_supported"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Null(document["authorization_response_iss_parameter_supported"]);
        Assert.Null(document["userinfo_endpoint"]);
        Assert.Single(JsonNode.Parse(await keys.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["keys"]!.AsArray());
    }

    [Fact]
    public async Task The_test_issuer_serves_the_token_the_api_and_the_revocation_under_github()   // Amendment A5, criterion 32
    {
        var answer = await AuthorizeAsync(new FakeSignIn(FakePeople.GitHub), "http://issuer.test/github/authorize");
        using var redeemed = await RedeemAsync(answer["code"], endpoint: "http://issuer.test/github/token");
        var token = (await JsonOfAsync(redeemed))["access_token"]!.GetValue<string>();

        using var user = await ApiAsync("/user", token, origin: "http://issuer.test/github");
        using var emails = await ApiAsync("/user/emails?per_page=100", token, origin: "http://issuer.test/github");
        using var revoked = await RevokeAsync(token, origin: "http://issuer.test/github");

        Assert.Equal(HttpStatusCode.OK, user.StatusCode);
        Assert.Equal(HttpStatusCode.OK, emails.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
    }

    [Fact]
    public async Task The_inspection_endpoint_lists_the_revocations_without_the_tokens()   // scripts/e2e-settings.sh reads it
    {
        var token = await AccessTokenAsync(new FakeSignIn(FakePeople.GitHub));
        using (var revoked = await RevokeAsync(token))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }

        using var listed = await _fake.Client.GetAsync("http://localhost:8099" + FakeGitHub.InspectionPath, TestContext.Current.CancellationToken);
        var text = await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal($$"""{"revocations":[{"client_id":"{{ClientId}}","status":204}]}""", text);
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("user_fail")]
    [InlineData(FakeDefects.NoSubject)]    // a defect of the OpenID fakes cannot apply to GitHub: refused, not ignored
    [InlineData(FakeDefects.WrongNonce)]
    [InlineData(FakeDefects.RefuseClient)]
    public async Task A_defect_the_fake_does_not_know_or_that_cannot_apply_is_refused_not_ignored(string defect)
    {
        using var response = await _fake.Client.GetAsync(
            new FakeSignIn(FakePeople.GitHub, Defect: defect).AppendTo(AuthorizeUrl()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

In `tests/Auth.IntegrationTests/FakeOidcTests.cs`, in the theory `An_unknown_path_is_404`, replace `    [InlineData("http://issuer.test/github/authorize")]`
with `    [InlineData("http://issuer.test/gitlab/authorize")]   // spec 0012: /github is served now`; and in the theory
`A_defect_the_fake_does_not_know_or_that_cannot_apply_is_refused_not_ignored` replace `    [InlineData(true, FakePeople.Cancel, "typo")]` with

```csharp
    [InlineData(true, FakePeople.Cancel, "typo")]
    [InlineData(true, FakePeople.Google, FakeDefects.RevokeFails)]   // spec 0012: a GitHub defect on a Google sign-in is refused, not ignored
    [InlineData(false, FakePeople.MicrosoftWork, FakeDefects.NoIss)]
```

- [ ] **Step 2: See them fail.** Build: it fails (`FakeOidcServer.GitHub`, `FakeGitHub`, the people and the defects do not exist).

- [ ] **Step 3: The people and the defects.** In `tests/Auth.FakeOidc/FakePeople.cs`:

(a) replace

```csharp
/// <summary>The two providers the fake plays.</summary>
public static class FakeKinds
{
    public const string Google = "google";
    public const string Microsoft = "microsoft";
}
```

with

```csharp
/// <summary>The providers the fake plays.</summary>
public static class FakeKinds
{
    public const string Google = "google";
    public const string Microsoft = "microsoft";

    /// <summary>Spec 0012: GitHub, played by <see cref="FakeGitHub"/>.</summary>
    public const string GitHub = "github";
}
```

(b) replace

```csharp
    /// <summary>Whether this is one of the defects above; the fake refuses any other name, so a typo never yields a good identity token.</summary>
    public static bool IsKnown(string defect) => defect is NoSubject or NoTenant or NoObjectId or NoEmail or WrongAudience or WrongNonce
        or WrongIssuer or BadSignature or RefuseCode or RefuseClient or ProviderError;
```

with

```csharp
    // Spec 0012: GitHub. The authorization answer without `iss`; a token answer without `access_token`; /user and /user/emails failing, not
    // JSON, without `id`, with an `id` that is not a number, larger than 64 KiB or held until the request is aborted (each of the last two for
    // /user and for /user/emails); a revocation answering 500 or held until the request is aborted.
    public const string NoIss = "no_iss";
    public const string NoToken = "no_token";
    public const string UserFails = "user_fails";
    public const string UserNotJson = "user_not_json";
    public const string UserNoId = "user_no_id";
    public const string UserBadId = "user_bad_id";
    public const string UserTooLarge = "user_too_large";
    public const string UserSlow = "user_slow";
    public const string EmailsFails = "emails_fails";
    public const string EmailsNotJson = "emails_not_json";
    public const string EmailsBadEntry = "emails_bad_entry";
    public const string EmailsTooLarge = "emails_too_large";
    public const string EmailsSlow = "emails_slow";
    public const string RevokeFails = "revoke_fails";
    public const string RevokeSlow = "revoke_slow";

    /// <summary>Whether this is one of the defects above; the fake refuses any other name, so a typo never yields a good identity token.</summary>
    public static bool IsKnown(string defect) => defect is NoSubject or NoTenant or NoObjectId or NoEmail or WrongAudience or WrongNonce
        or WrongIssuer or BadSignature or RefuseCode or RefuseClient or ProviderError || IsGitHubOnly(defect);

    /// <summary>The defects only the fake GitHub can apply (it has no identity token): the OpenID fakes refuse them instead of ignoring them.</summary>
    public static bool IsGitHubOnly(string defect) => defect is NoIss or NoToken or UserFails or UserNotJson or UserNoId or UserBadId
        or UserTooLarge or UserSlow or EmailsFails or EmailsNotJson or EmailsBadEntry or EmailsTooLarge or EmailsSlow or RevokeFails or RevokeSlow;

    /// <summary>The defects only the OpenID fakes can apply (claims, the identity token, the client): the fake GitHub refuses them instead of ignoring them.</summary>
    public static bool IsOpenIdOnly(string defect) => defect is NoSubject or NoTenant or NoObjectId or NoEmail or WrongAudience or WrongNonce
        or WrongIssuer or BadSignature or RefuseClient;
```

(c) replace

```csharp
    public const string WorkUserName = "bo@firma.onmicrosoft.test";
```

with

```csharp
    public const string WorkUserName = "bo@firma.onmicrosoft.test";

    // Spec 0012, criterion 32: the three GitHub people. Ola: a verified primary address, a verified secondary one and a public address on /user
    // (neither of which may be read as the address); Kim: an unverified primary address; Lee: two verified addresses, neither primary.
    public const string GitHub = "github";
    public const string GitHubUnverified = "github-unverified";
    public const string GitHubNoPrimary = "github-no-primary";
    public const string GitHubId = "70000001";
    public const string GitHubLogin = "ola-gh";
    public const string GitHubEmail = "ola@github.test";
    public const string GitHubSecondaryEmail = "ola.work@firma.test";
    public const string GitHubPublicEmail = "ola.public@github.test";
    public const string GitHubUnverifiedId = "70000002";
    public const string GitHubUnverifiedEmail = "kim@github.test";
    public const string GitHubNoPrimaryId = "70000003";
    public const string GitHubNoPrimaryEmail = "lee@github.test";
    public const string GitHubNoPrimaryOtherEmail = "lee@work.test";

    /// <summary>The GitHub id of a free address: a positive number derived from the address, whatever its case, the same on every round trip.</summary>
    public static string GitHubIdOf(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        var number = (System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(Hash(email)) & 0x3FFF_FFFF_FFFF_FFFF) + 1;
        return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
```

- [ ] **Step 4: The fake GitHub.** `tests/Auth.FakeOidc/FakeGitHub.cs`:

```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace Auth.FakeOidc;

/// <summary>
/// A revocation the fake was asked for: the client id of its path, the token of its body (empty when it had none) and the answer's status
/// (<c>0</c>: the <c>revoke_slow</c> defect held the request and gave no answer).
/// </summary>
public sealed record GitHubRevocation(string ClientId, string Token, int Status);

/// <summary>
/// The fake provider as GitHub (spec 0012, criterion 32): OAuth 2.0 with PKCE, no identity token, no nonce. By host it answers as GitHub does
/// (<c>github.com</c>: the authorization and the token endpoints; <c>api.github.com</c>: <c>/user</c>, <c>/user/emails</c> and the revocation),
/// with <c>iss</c> on every authorization answer (Amendment A6); under <c>/github/...</c> on any other host it is the test issuer of
/// Amendment A5, with a minimal discovery document, a key set and the same endpoints, and no <c>iss</c>. It records every token it issues,
/// every request to its API and every revocation.
/// </summary>
public sealed class FakeGitHub : IDisposable
{
    public const string WebIssuer = "https://github.com/login/oauth";

    /// <summary>For the end-to-end script: the revocations as JSON, the client id and the status of each, never a token.</summary>
    public const string InspectionPath = "/_fake/github/revocations";

    private const string Prefix = "/github";
    private const int LargeBodyBytes = 70 * 1024;

    private static readonly string[] CodeOnly = ["code"];
    private static readonly string[] S256Only = ["S256"];
    private static readonly string[] AuthorizationCodeOnly = ["authorization_code"];
    private static readonly string[] AuthMethods = ["client_secret_post", "client_secret_basic"];

    private readonly ConcurrentDictionary<string, Grant> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Grant> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _issued = new();
    private readonly ConcurrentQueue<GitHubRevocation> _revocations = new();
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _slowArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FakeOidcOptions _options;
    private readonly Func<Dictionary<string, object>> _keySet;

    public FakeGitHub(FakeOidcOptions options, Func<Dictionary<string, object>> keySet)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keySet);

        _options = options;
        _keySet = keySet;
    }

    /// <summary>Every access token the token endpoint issued, oldest first.</summary>
    public IReadOnlyCollection<string> IssuedTokens => [.. _issued];

    public IReadOnlyCollection<GitHubRevocation> Revocations => [.. _revocations];

    /// <summary>The token requests and the API requests, as <c>"METHOD path?query"</c> of GitHub's paths, oldest first.</summary>
    public IReadOnlyCollection<string> Requests => [.. _requests];

    /// <summary>Completes when a request of a slow defect (<c>user_slow</c>, <c>emails_slow</c>, <c>revoke_slow</c>) has arrived and is being held.</summary>
    public Task SlowRequestArrived => _slowArrived.Task;

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }

    /// <summary>Answers the request when it is GitHub's; <see langword="false"/> otherwise (the OpenID providers answer it then).</summary>
    public async Task<bool> TryHandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        var path = request.Path.Value ?? "/";
        if (Is(request.Host.Host, "github.com"))
        {
            switch (path)
            {
                case "/login/oauth/authorize":
                    await AuthorizeAsync(context, web: true);
                    return true;
                case "/login/oauth/access_token":
                    await TokenAsync(context, "/login/oauth/access_token");
                    return true;
                default:
                    return false;
            }
        }

        if (Is(request.Host.Host, "api.github.com"))
        {
            return await ApiAsync(context, path);
        }

        if (path == InspectionPath && HttpMethods.IsGet(request.Method))
        {
            await WriteJsonAsync(
                context.Response,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["revocations"] = _revocations.Select(r => new Dictionary<string, object> { ["client_id"] = r.ClientId, ["status"] = r.Status }).ToArray(),
                });
            return true;
        }

        if (!path.StartsWith(Prefix + "/", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = path[Prefix.Length..];
        switch (rest)
        {
            case "/.well-known/openid-configuration":
                await WriteJsonAsync(context.Response, Discovery(request));
                return true;
            case "/keys":
                await WriteJsonAsync(context.Response, _keySet());
                return true;
            case "/authorize":
                await AuthorizeAsync(context, web: false);
                return true;
            case "/token":
                await TokenAsync(context, "/login/oauth/access_token");
                return true;
            default:
                return await ApiAsync(context, rest);
        }
    }

    private Dictionary<string, object> Discovery(HttpRequest request)
    {
        var origin = request.Scheme + "://" + request.Host.Value;
        var issuer = (_options.IssuerOrigin?.GetLeftPart(UriPartial.Authority) ?? origin) + Prefix;
        var browser = (_options.BrowserOrigin?.GetLeftPart(UriPartial.Authority) ?? origin) + Prefix;

        // Amendment A5: what OpenIddict needs and no more. No `iss` parameter is advertised, so the plain registration does not require it.
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = browser + "/authorize",
            ["token_endpoint"] = issuer + "/token",
            ["jwks_uri"] = issuer + "/keys",
            ["response_types_supported"] = CodeOnly,
            ["grant_types_supported"] = AuthorizationCodeOnly,
            ["code_challenge_methods_supported"] = S256Only,
            ["token_endpoint_auth_methods_supported"] = AuthMethods,
        };
    }

    private async Task AuthorizeAsync(HttpContext context, bool web)
    {
        var query = context.Request.Query;
        var clientId = One(query, "client_id");
        var redirectUri = One(query, "redirect_uri");
        var state = One(query, "state");
        var challenge = One(query, "code_challenge");
        var scopes = (One(query, "scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!string.Equals(One(query, "response_type"), "code", StringComparison.Ordinal)
            || !string.Equals(One(query, "code_challenge_method"), "S256", StringComparison.Ordinal)
            || clientId is null || state is null || challenge is null || !scopes.Contains("user:email", StringComparer.Ordinal)
            || redirectUri is null || !Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
        {
            await PlainAsync(context.Response, "This is not an authorization request of GitHub's code flow with PKCE (S256), a state and the scope user:email.");
            return;
        }

        if (FakeSignIn.From(query) is not { } signIn)
        {
            await PageAsync(context);
            return;
        }

        if (signIn.Defect is { } defect && (!FakeDefects.IsKnown(defect) || FakeDefects.IsOpenIdOnly(defect)))
        {
            await PlainAsync(context.Response, "This is not a defect the fake GitHub knows.");   // the claim defects of the OpenID fakes cannot apply here
            return;
        }

        var answer = new List<KeyValuePair<string, string?>>();
        if (signIn.Person == FakePeople.Cancel || signIn.Defect == FakeDefects.ProviderError)
        {
            answer.Add(new("error", signIn.Person == FakePeople.Cancel ? "access_denied" : "server_error"));
            answer.Add(new("error_description", FakeOidcServer.RefusalDescription));
        }
        else
        {
            if (Person.Of(signIn) is not { } person)
            {
                await PlainAsync(context.Response, "GitHub has no such person.");
                return;
            }

            var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            _codes[code] = new Grant(clientId, redirectUri, challenge, person, signIn.Defect);
            answer.Add(new("code", code));
        }

        answer.Add(new("state", state));
        if (web && signIn.Defect != FakeDefects.NoIss)
        {
            answer.Add(new("iss", WebIssuer));   // GitHub sends it since April 2026, and OpenIddict's GitHub provider requires it
        }

        context.Response.Redirect(QueryHelpers.AddQueryString(redirectUri, answer));
    }

    private async Task TokenAsync(HttpContext context, string name)
    {
        var request = context.Request;
        if (!HttpMethods.IsPost(request.Method) || !request.HasFormContentType)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        _requests.Enqueue("POST " + name);
        var form = await request.ReadFormAsync(context.RequestAborted);
        var clientId = form.TryGetValue("client_id", out var formClient) ? formClient.ToString() : BasicOf(request)?.Id;
        if (!string.Equals(form["grant_type"].ToString(), "authorization_code", StringComparison.Ordinal)
            || !_codes.TryRemove(form["code"].ToString(), out var grant)
            || grant.Defect == FakeDefects.RefuseCode
            || !string.Equals(clientId, grant.ClientId, StringComparison.Ordinal)
            || !string.Equals(form["redirect_uri"].ToString(), grant.RedirectUri, StringComparison.Ordinal)
            || !string.Equals(S256(form["code_verifier"].ToString()), grant.Challenge, StringComparison.Ordinal))
        {
            // GitHub answers a refused code with 200 and an error.
            await AnswerTokenAsync(context, new() { ["error"] = "bad_verification_code", ["error_description"] = FakeOidcServer.RefusalDescription });
            return;
        }

        if (grant.Defect == FakeDefects.NoToken)
        {
            await AnswerTokenAsync(context, new() { ["token_type"] = "bearer", ["scope"] = "user:email" });
            return;
        }

        var token = "gho_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(18));
        _tokens[token] = grant;
        _issued.Enqueue(token);
        await AnswerTokenAsync(context, new() { ["access_token"] = token, ["token_type"] = "bearer", ["scope"] = "user:email" });
    }

    /// <summary>GitHub answers the token endpoint form-encoded unless the request asks for JSON (OpenIddict does).</summary>
    private static async Task AnswerTokenAsync(HttpContext context, Dictionary<string, string> body)
    {
        if (context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(context.Response.Body, body, cancellationToken: context.RequestAborted);
            return;
        }

        context.Response.ContentType = "application/x-www-form-urlencoded";
        await context.Response.WriteAsync(
            string.Join('&', body.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))), context.RequestAborted);
    }

    private async Task<bool> ApiAsync(HttpContext context, string path)
    {
        var request = context.Request;
        if (path.StartsWith("/applications/", StringComparison.Ordinal) && path.EndsWith("/token", StringComparison.Ordinal) && HttpMethods.IsDelete(request.Method))
        {
            await RevokeAsync(context, path);
            return true;
        }

        if (path is not ("/user" or "/user/emails") || !HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        _requests.Enqueue("GET " + path + request.QueryString.Value);
        if (string.IsNullOrEmpty(request.Headers.UserAgent.ToString())
            || !request.Headers.Accept.ToString().Contains("application/vnd.github+json", StringComparison.Ordinal))
        {
            await MessageAsync(context.Response, StatusCodes.Status403Forbidden, "Request forbidden by administrative rules. Please make sure your request has a User-Agent header.");
            return true;
        }

        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || !_tokens.TryGetValue(header["Bearer ".Length..], out var grant))
        {
            await MessageAsync(context.Response, StatusCodes.Status401Unauthorized, "Bad credentials");
            return true;
        }

        if (path == "/user")
        {
            await UserAsync(context, grant);
        }
        else
        {
            await EmailsAsync(context, grant);
        }

        return true;
    }

    private async Task UserAsync(HttpContext context, Grant grant)
    {
        switch (grant.Defect)
        {
            case FakeDefects.UserFails:
                await MessageAsync(context.Response, StatusCodes.Status500InternalServerError, "Server Error");
                return;
            case FakeDefects.UserNotJson:
                await HtmlAsync(context.Response);
                return;
            case FakeDefects.UserSlow:
                await HoldAsync(context);
                return;
        }

        var person = grant.Person;
        await WriteWithAsync(context.Response, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("login", person.Login);
            if (grant.Defect != FakeDefects.UserNoId)
            {
                if (grant.Defect != FakeDefects.UserBadId && long.TryParse(person.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    writer.WriteNumber("id", id);
                }
                else
                {
                    writer.WriteString("id", grant.Defect == FakeDefects.UserBadId ? "not-a-number" : person.Id);
                }
            }

            writer.WriteString("email", person.PublicEmail);
            writer.WriteString("name", "Ola Test");
            if (grant.Defect == FakeDefects.UserTooLarge)
            {
                writer.WriteString("bio", new string('x', LargeBodyBytes));
            }

            writer.WriteEndObject();
        });
    }

    private async Task EmailsAsync(HttpContext context, Grant grant)
    {
        switch (grant.Defect)
        {
            case FakeDefects.EmailsFails:
                await MessageAsync(context.Response, StatusCodes.Status500InternalServerError, "Server Error");
                return;
            case FakeDefects.EmailsNotJson:
                await HtmlAsync(context.Response);
                return;
            case FakeDefects.EmailsSlow:
                await HoldAsync(context);
                return;
        }

        await WriteWithAsync(context.Response, writer =>
        {
            writer.WriteStartArray();
            foreach (var entry in grant.Person.Emails)
            {
                writer.WriteStartObject();
                writer.WriteString("email", entry.Address);
                if (grant.Defect == FakeDefects.EmailsTooLarge)
                {
                    writer.WriteString("note", new string('x', LargeBodyBytes));   // documented JSON, larger than 64 KiB
                }

                if (grant.Defect == FakeDefects.EmailsBadEntry)
                {
                    writer.WriteString("primary", entry.Primary ? "yes" : "no");
                }
                else
                {
                    writer.WriteBoolean("primary", entry.Primary);
                }

                writer.WriteBoolean("verified", entry.Verified);
                writer.WriteString("visibility", entry.Primary ? "private" : null);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private async Task RevokeAsync(HttpContext context, string path)
    {
        var request = context.Request;
        var clientId = Uri.UnescapeDataString(path["/applications/".Length..^"/token".Length]);
        _requests.Enqueue("DELETE /applications/" + clientId + "/token");
        string? token = null;
        try
        {
            using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: context.RequestAborted);
            token = body.RootElement.TryGetProperty("access_token", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            // no body, or not JSON: GitHub answers 422; here the answer below is 401 or 422
        }

        int status;
        if (!string.Equals(BasicOf(request)?.Id, clientId, StringComparison.Ordinal))
        {
            status = StatusCodes.Status401Unauthorized;
        }
        else if (token is null)
        {
            status = StatusCodes.Status422UnprocessableEntity;
        }
        else if (_tokens.TryGetValue(token, out var slow) && slow.Defect == FakeDefects.RevokeSlow)
        {
            _revocations.Enqueue(new GitHubRevocation(clientId, token, 0));   // no answer: the request is held until it is aborted; the token stays valid
            await HoldAsync(context);
            return;
        }
        else if (_tokens.TryGetValue(token, out var grant) && grant.Defect == FakeDefects.RevokeFails)
        {
            status = StatusCodes.Status500InternalServerError;   // the token stays valid, as at GitHub when the call fails
        }
        else
        {
            _tokens.TryRemove(token, out _);
            status = StatusCodes.Status204NoContent;
        }

        _revocations.Enqueue(new GitHubRevocation(clientId, token ?? "", status));
        context.Response.StatusCode = status;
    }

    /// <summary>The answer of the slow defects: nothing, until the request is aborted or the fake is disposed.</summary>
    private async Task HoldAsync(HttpContext context)
    {
        _slowArrived.TrySetResult();
        using var held = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _closing.Token);
        try
        {
            await Task.Delay(Timeout.Infinite, held.Token);
        }
        catch (OperationCanceledException)
        {
            // the caller gave up, or the test is over
        }
    }

    private static async Task PageAsync(HttpContext context)
    {
        var request = context.Request;
        var here = (request.Path.Value ?? "/") + request.QueryString.Value;
        (string Person, string Label)[] people =
        [
            (FakePeople.GitHub, $"Ola ({FakePeople.GitHubEmail}, primary and verified)"),
            (FakePeople.GitHubUnverified, $"Kim ({FakePeople.GitHubUnverifiedEmail}, primary, not verified)"),
            (FakePeople.GitHubNoPrimary, "Lee (no primary address)"),
        ];

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Fake GitHub</title></head><body>");
        html.Append("<h1>Sign in to the fake GitHub</h1><p>The test provider of Auth-Core. Choose who signs in.</p><ul>");
        foreach (var (person, label) in people)
        {
            html.Append("<li><a href=\"").Append(Encode(new FakeSignIn(person).AppendTo(here))).Append("\">").Append(Encode(label)).Append("</a></li>");
        }

        html.Append("</ul><form method=\"get\" action=\"").Append(Encode(request.Path.Value ?? "/")).Append("\">");
        foreach (var (key, values) in request.Query)
        {
            foreach (var value in values)
            {
                html.Append("<input type=\"hidden\" name=\"").Append(Encode(key)).Append("\" value=\"").Append(Encode(value ?? "")).Append("\">");
            }
        }

        html.Append("<input type=\"hidden\" name=\"person\" value=\"").Append(FakePeople.Custom).Append("\">");
        html.Append("<p><label for=\"email\">Email</label> <input id=\"email\" name=\"email\" type=\"email\" required></p>");
        html.Append("<p><label><input type=\"checkbox\" name=\"verified\" value=\"true\"> The provider verified this address</label></p>");
        html.Append("<p><button type=\"submit\">Sign in</button></p></form>");
        html.Append("<p><a href=\"").Append(Encode(new FakeSignIn(FakePeople.Cancel).AppendTo(here))).Append("\">Cancel</a></p></body></html>");

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html.ToString(), context.RequestAborted);
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string S256(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static (string Id, string Secret)? BasicOf(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
            var colon = pair.IndexOf(':', StringComparison.Ordinal);
            return colon < 0 ? null : (WebUtility.UrlDecode(pair[..colon]), WebUtility.UrlDecode(pair[(colon + 1)..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool Is(string host, string name) => string.Equals(host, name, StringComparison.OrdinalIgnoreCase);

    private static string? One(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var values) && values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;

    private static async Task WriteJsonAsync(HttpResponse response, Dictionary<string, object> body)
    {
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.Body, body);
    }

    private static async Task WriteWithAsync(HttpResponse response, Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        response.ContentType = "application/json; charset=utf-8";
        await response.Body.WriteAsync(buffer.ToArray());
    }

    private static async Task MessageAsync(HttpResponse response, int status, string message)
    {
        response.StatusCode = status;
        await WriteJsonAsync(response, new Dictionary<string, object>(StringComparer.Ordinal) { ["message"] = message });
    }

    private static async Task HtmlAsync(HttpResponse response)
    {
        response.ContentType = "text/html; charset=utf-8";
        await response.WriteAsync("<!doctype html><html><body>GitHub is having a moment.</body></html>");
    }

    private static async Task PlainAsync(HttpResponse response, string text)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        response.ContentType = "text/plain; charset=utf-8";
        await response.WriteAsync(text);
    }

    /// <summary>One address of <c>/user/emails</c>.</summary>
    private sealed record MailEntry(string Address, bool Primary, bool Verified);

    /// <summary>A GitHub person: the id (a decimal number, or the text a script asked for), the login, the public address of <c>/user</c>, the addresses.</summary>
    private sealed record Person(string Id, string Login, string? PublicEmail, IReadOnlyList<MailEntry> Emails)
    {
        /// <summary>The person of a sign-in at the fake; an address, a "verified" or a subject the sign-in names replaces the primary entry's or the id.</summary>
        public static Person? Of(FakeSignIn signIn) => signIn.Person switch
        {
            FakePeople.GitHub => new Person(
                signIn.Subject ?? FakePeople.GitHubId,
                FakePeople.GitHubLogin,
                FakePeople.GitHubPublicEmail,
                [new MailEntry(signIn.Email ?? FakePeople.GitHubEmail, true, signIn.Verified ?? true), new MailEntry(FakePeople.GitHubSecondaryEmail, false, true)]),
            FakePeople.GitHubUnverified => new Person(
                signIn.Subject ?? FakePeople.GitHubUnverifiedId, "kim-gh", null,
                [new MailEntry(signIn.Email ?? FakePeople.GitHubUnverifiedEmail, true, signIn.Verified ?? false)]),
            FakePeople.GitHubNoPrimary => new Person(
                signIn.Subject ?? FakePeople.GitHubNoPrimaryId, "lee-gh", null,
                [new MailEntry(FakePeople.GitHubNoPrimaryEmail, false, true), new MailEntry(FakePeople.GitHubNoPrimaryOtherEmail, false, true)]),
            FakePeople.Custom when signIn.Email is { } email => new Person(
                signIn.Subject ?? FakePeople.GitHubIdOf(email), "custom-gh", null, [new MailEntry(email, true, signIn.Verified ?? false)]),
            _ => null,
        };
    }

    private sealed record Grant(string ClientId, string RedirectUri, string Challenge, Person Person, string? Defect);
}
```

- [ ] **Step 5: GitHub first.** In `tests/Auth.FakeOidc/FakeOidcServer.cs`:

(a) replace

```csharp
        _options = options;
        _clock = clock;
    }
```

with

```csharp
        _options = options;
        _clock = clock;
        GitHub = new FakeGitHub(options, KeySet);
    }

    /// <summary>Spec 0012: the fake as GitHub; it answers its hosts and the test issuer's <c>/github</c> paths before anything else here.</summary>
    public FakeGitHub GitHub { get; }
```

(b) replace

```csharp
    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Route(context.Request) is not { } site)
```

with

```csharp
    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (await GitHub.TryHandleAsync(context))
        {
            return;
        }

        if (Route(context.Request) is not { } site)
```

(c) replace

```csharp
    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
    }
```

with

```csharp
    public void Dispose()
    {
        GitHub.Dispose();
        _key.Dispose();
        _otherKey.Dispose();
    }
```

(d) in `AuthorizeAsync`, replace

```csharp
        if (signIn.Defect is { } defect && !FakeDefects.IsKnown(defect))
        {
            await PlainAsync(context.Response, "This is not a defect the fake knows.");
```

with

```csharp
        if (signIn.Defect is { } defect && (!FakeDefects.IsKnown(defect) || FakeDefects.IsGitHubOnly(defect)))
        {
            await PlainAsync(context.Response, "This is not a defect the fake knows.");   // GitHub's defects cannot apply to an identity token
```

and in the class summary replace `/// The fake OpenID provider of spec 0011 (Settled 22). It answers by host and path: the real host names of Google and Microsoft (which only the`
with `/// The fake OpenID provider of spec 0011 (Settled 22), and through <see cref="FakeGitHub"/> GitHub (spec 0012). It answers by host and path: the real host names of Google and Microsoft (which only the`.

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.FakeGitHubTests -class Auth.IntegrationTests.FakeOidcTests -class Auth.IntegrationTests.ProviderTestIssuerTests -class Auth.IntegrationTests.ProviderCallbackTests`
passes (the last two show that Google and Microsoft are untouched).

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`test: the fake provider answers as GitHub`.

---

## Day 2 — Friday 08.05: the reader, the GitHub round trip, the confirmation, the notices

When the day is done, a person signs in with GitHub through the fake in both modes, every failure of the round trip ends in its fragment code
with the token revoked, a signed-in person confirms who they are and opens the window of this device, and the three notice mails exist, sent by
the link of spec 0011 among others.

### Task 5: The GitHub identity rules, the reader and the two registrations

**Files:**
- Create: `src/Auth.Server/Providers/GitHubIdentity.cs`, `src/Auth.Server/Providers/GitHubReader.cs`
- Modify: `src/Auth.Server/Providers/ProviderIdentities.cs`, `src/Auth.Server/Providers/ProviderClientSetup.cs`
- Create (tests): `tests/Auth.IntegrationTests/GitHubIdentityTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/FakeOidcHost.cs`

**Interfaces:**
- Consumes: `ProviderNames.GitHub`, `ProviderSettings.GitHub`, `ProviderSettings.TestIssuerOf` (Task 3); the fake GitHub (Task 4, through the tests
  of Task 6).
- Produces: `GitHubAccount(string Key, string? Email, bool EmailVerified)`; `GitHubIdentity.Read(JsonElement user, JsonElement emails) :
  GitHubAccount?` (null: not the documented JSON); `GitHubItems.Key`, `.Email`, `.Verified`, `GitHubItems.Write(IDictionary<string,string?>,
  GitHubAccount)`, `GitHubItems.Read(IDictionary<string,string?>?) : GitHubAccount?`; `ProviderIdentities.FromGitHub(GitHubAccount,
  ILookupNormalizer) : ProviderIdentity`; `GitHubReader` (the handler) with `HttpClientName` (`"auth-core-github"`), `CallBound` (10 s),
  `MaxBodyBytes` (65 536), `WebApiBase` (`https://api.github.com`), `ErrorUri`, `Descriptor`; the GitHub registrations of OpenIddict's client.

**Contract counts:** none.

**Decisions the plan takes here:**
- The rules are split where the services allow: `GitHubIdentity.Read` (the reader, a singleton) applies the rules of the documents (the `id` a
  positive integer as its decimal text; exactly one `primary` entry is the address, with its `verified`; any entry of the wrong shape refuses
  the whole answer), and the callback applies the invitation rule through `ProviderIdentities.FromGitHub` with the request's normaliser, as it
  does for Google and Microsoft. So the reader hands over only `id`, the primary address and its flag, in three items of its own.
- The reader runs at `ResolveValidatedBackchannelTokens + 1000` on the redirection endpoint of the `github` registration only, in both modes; its
  API base is `https://api.github.com`, or `{TestIssuer}/github` under the test issuer (Amendment A5).
- Each read and the revocation are bounded by a `CancellationTokenSource` on the service's `TimeProvider` (10 seconds), the reads also by the
  request; the body is read as a stream into a buffer of 64 KiB plus one byte. The revocation has a bound of its own and never the request's
  token (probe U12), runs in a `finally`, and only then is the token removed from the context (both assignments, probe U2).
- A failed read or a refused document is `Reject(server_error, ..., urn:auth-core:github-read)`: the callback's existing mapping makes it
  `provider_error`, with the state's items intact. A token answer without `access_token` is the same refusal (Amendment A3) before any read.
- The Basic credentials of the revocation are the registration's own client id and secret: the same in both modes.
- The named client's primary handler follows no redirect (`AllowAutoRedirect = false`), so that "one attempt per call" holds against a `301` too. The
  tests' routing puts the fake in as an additional handler that replaces the primary one, so no test can reach this setting: the review of this
  step is its guard (the `3xx` as "not 200" is the same code path as the `500` the tests send).

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/GitHubIdentityTests.cs`:

```csharp
using System.Text.Json;
using Auth.Server.Providers;
using Microsoft.AspNetCore.Identity;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → The identity (criterion 4), one test per rule: the key is <c>id</c>; the address is the primary entry only.</summary>
public sealed class GitHubIdentityTests
{
    private const string User = """{"login":"ola-gh","id":70000001,"email":"ola.public@github.test"}""";
    private const string Primary = """[{"email":"ola@github.test","primary":true,"verified":true,"visibility":"private"}]""";

    private static readonly UpperInvariantLookupNormalizer Normalizer = new();

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static GitHubAccount? Read(string user, string emails) => GitHubIdentity.Read(Json(user), Json(emails));

    [Fact]
    public void The_key_is_the_id_as_a_decimal_string_and_never_the_login()   // criterion 4
    {
        Assert.Equal("70000001", Read(User, Primary)!.Key);
        Assert.Equal("7", Read("""{"login":"12345","id":7}""", Primary)!.Key);
        Assert.Equal("9007199254740993", Read("""{"id":9007199254740993}""", Primary)!.Key);   // int64, not a double
    }

    [Fact]
    public void Only_the_primary_entry_is_the_address_and_a_verified_secondary_is_never_used()   // criterion 4, Decision 2, Review Focus 4
    {
        var account = Read(
            User,
            """
            [{"email":"ola.work@firma.test","primary":false,"verified":true},
             {"email":"ola@github.test","primary":true,"verified":false},
             {"email":"ola.old@github.test","primary":false,"verified":true}]
            """);

        Assert.Equal(new GitHubAccount("70000001", "ola@github.test", false), account);
    }

    [Fact]
    public void A_primary_entry_with_verified_false_is_unverified_and_true_is_verified()   // criterion 4
    {
        Assert.False(Read(User, """[{"email":"kim@github.test","primary":true,"verified":false}]""")!.EmailVerified);
        Assert.True(Read(User, Primary)!.EmailVerified);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"email":"lee@github.test","primary":false,"verified":true},{"email":"lee@work.test","primary":false,"verified":true}]""")]
    [InlineData("""[{"email":"a@github.test","primary":true,"verified":true},{"email":"b@github.test","primary":true,"verified":true}]""")]
    public void No_primary_entry_or_more_than_one_is_no_address(string emails)   // criterion 4
    {
        Assert.Equal(new GitHubAccount("70000001", null, false), Read(User, emails));
    }

    [Fact]
    public void The_email_of_user_is_never_the_address()   // spec 0012 → The identity: never `email` of /user
    {
        var account = Read(User, """[{"email":"lee@github.test","primary":false,"verified":true}]""");

        Assert.Null(account!.Email);
        Assert.NotEqual("ola.public@github.test", Read(User, Primary)!.Email);
    }

    [Theory]
    [InlineData("ola@localhost")]
    [InlineData("ola@10.0.0.1")]
    [InlineData("ola@intranet.123")]
    public void An_address_that_breaks_the_invitation_rule_is_kept_and_unverified(string email)   // criterion 4
    {
        var identity = ProviderIdentities.FromGitHub(new GitHubAccount("70000001", email, true), Normalizer);

        Assert.Equal((ProviderNames.GitHub, "70000001", email, false), (identity.Provider, identity.Key, identity.Email, identity.EmailVerified));
        Assert.True(ProviderIdentities.FromGitHub(new GitHubAccount("70000001", "ola@github.test", true), Normalizer).EmailVerified);
        Assert.Null(ProviderIdentities.FromGitHub(new GitHubAccount("70000001", null, false), Normalizer).Email);
    }

    [Theory]
    [InlineData("""{"login":"ola-gh"}""")]
    [InlineData("""{"id":0}""")]
    [InlineData("""{"id":-1}""")]
    [InlineData("""{"id":1.5}""")]
    [InlineData("""{"id":1e3}""")]
    [InlineData("""{"id":"70000001"}""")]
    [InlineData("""{"id":null}""")]
    [InlineData("""{"id":99999999999999999999}""")]
    public void A_user_without_an_id_or_with_one_that_is_not_a_positive_integer_is_refused(string user)   // criterion 5's input
    {
        Assert.Null(Read(user, Primary));
    }

    [Theory]
    [InlineData("""[{"email":5,"primary":true,"verified":true}]""")]
    [InlineData("""[{"email":null,"primary":true,"verified":true}]""")]
    [InlineData("""[{"primary":true,"verified":true}]""")]
    [InlineData("""[{"email":"a@github.test","primary":"yes","verified":true}]""")]
    [InlineData("""[{"email":"a@github.test","verified":true}]""")]
    [InlineData("""[{"email":"a@github.test","primary":true,"verified":"true"}]""")]
    [InlineData("""[{"email":"a@github.test","primary":true}]""")]
    [InlineData("""["a@github.test"]""")]
    public void An_entry_of_the_wrong_shape_refuses_the_whole_answer(string emails)   // spec 0012 → The identity: not the documented JSON
    {
        Assert.Null(Read(User, emails));
    }

    [Fact]
    public void Answers_that_are_not_an_object_and_an_array_are_refused()
    {
        Assert.Null(Read("[]", Primary));
        Assert.Null(Read(User, "{}"));
        Assert.Null(Read("\"ola\"", Primary));
    }

    [Fact]
    public void The_items_carry_the_identity_and_read_back_only_a_decimal_key()   // Amendment A3: the hand-over to the callback
    {
        var items = new Dictionary<string, string?>(StringComparer.Ordinal) { ["purpose"] = "sign_in" };

        GitHubItems.Write(items, new GitHubAccount("70000001", "ola@github.test", true));

        Assert.Equal(new GitHubAccount("70000001", "ola@github.test", true), GitHubItems.Read(items));
        Assert.Equal(4, items.Count);
        GitHubItems.Write(items, new GitHubAccount("70000002", null, false));
        Assert.Equal(new GitHubAccount("70000002", null, false), GitHubItems.Read(items));
        items[GitHubItems.Key] = "ola-gh";
        Assert.Null(GitHubItems.Read(items));
        Assert.Null(GitHubItems.Read(null));
        Assert.Null(GitHubItems.Read(new Dictionary<string, string?>(StringComparer.Ordinal)));
    }
}
```

In `tests/Auth.IntegrationTests/Infrastructure/FakeOidcHost.cs`, replace

```csharp
/// <summary>
/// Sends every request of OpenIddict's named HTTP clients, whose names start with the name of their assembly, to the fake. The
/// primary handler must stay an <c>HttpClientHandler</c>, so the fake is put in as an additional handler that never calls the inner one.
/// </summary>
internal sealed class FakeOidcRouting(HttpMessageHandler fake) : IPostConfigureOptions<HttpClientFactoryOptions>
{
    private const string OpenIddictClients = "OpenIddict.Client.SystemNetHttp";

    public void PostConfigure(string? name, HttpClientFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (name is null || !name.StartsWith(OpenIddictClients, StringComparison.Ordinal))
        {
            return;
        }
```

with

```csharp
/// <summary>
/// Sends every request of OpenIddict's named HTTP clients, whose names start with the name of their assembly, and of the GitHub reader's own
/// client (spec 0012, Amendment A4) to the fake. The primary handler must stay an <c>HttpClientHandler</c>, so the fake is put in as an
/// additional handler that never calls the inner one.
/// </summary>
internal sealed class FakeOidcRouting(HttpMessageHandler fake) : IPostConfigureOptions<HttpClientFactoryOptions>
{
    private const string OpenIddictClients = "OpenIddict.Client.SystemNetHttp";

    public void PostConfigure(string? name, HttpClientFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (name is null
            || !(name.StartsWith(OpenIddictClients, StringComparison.Ordinal) || string.Equals(name, GitHubReader.HttpClientName, StringComparison.Ordinal)))
        {
            return;
        }
```

and add `using Auth.Server.Providers;` after `using Auth.FakeOidc;`.

- [ ] **Step 2: See them fail.** Build: it fails (`GitHubAccount`, `GitHubIdentity`, `GitHubItems`, `ProviderIdentities.FromGitHub`,
`GitHubReader` do not exist).

- [ ] **Step 3: The identity.** `src/Auth.Server/Providers/GitHubIdentity.cs`:

```csharp
using System.Globalization;
using System.Text.Json;

namespace Auth.Server.Providers;

/// <summary>
/// What the GitHub reader read (spec 0012 → The identity): the key (<c>id</c> of <c>/user</c>, a positive integer, as its decimal text), the primary
/// address of <c>/user/emails</c> or none, and whether GitHub verified it. The invitation rule is applied after it, by the callback.
/// </summary>
public sealed record GitHubAccount(string Key, string? Email, bool EmailVerified);

/// <summary>The rules of GitHub's two documents. Anything that is not the documented JSON is <see langword="null"/>: the callback answers <c>provider_error</c>.</summary>
public static class GitHubIdentity
{
    /// <param name="user">The answer of <c>GET /user</c>: an object whose <c>id</c> is a positive integer. <c>login</c> and <c>email</c> are never read.</param>
    /// <param name="emails">The answer of <c>GET /user/emails?per_page=100</c>: an array of objects with <c>email</c> (a string), <c>primary</c> and
    /// <c>verified</c> (booleans). Exactly one entry with <c>primary</c> <c>true</c> is the address; none or several is no address.</param>
    public static GitHubAccount? Read(JsonElement user, JsonElement emails)
    {
        if (user.ValueKind != JsonValueKind.Object
            || !user.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.Number
            || !id.TryGetInt64(out var number)
            || number <= 0
            || emails.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? primary = null;
        var verified = false;
        var primaries = 0;
        foreach (var entry in emails.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("email", out var email) || email.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("primary", out var isPrimary) || isPrimary.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !entry.TryGetProperty("verified", out var isVerified) || isVerified.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            if (isPrimary.ValueKind == JsonValueKind.True)
            {
                primaries++;
                primary = Text(email);
                verified = isVerified.ValueKind == JsonValueKind.True;
            }
        }

        var key = number.ToString(CultureInfo.InvariantCulture);
        return primaries == 1 && primary is not null ? new GitHubAccount(key, primary, verified) : new GitHubAccount(key, null, false);
    }

    /// <summary>A JSON string with an unpaired surrogate escape cannot be read as text: it is not an address.</summary>
    private static string? Text(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// How the reader hands the identity to the callback (Amendment A3): three items of the client's authentication properties, which OpenIddict copies
/// into the callback's outcome. The start never writes them, so only the reader can.
/// </summary>
public static class GitHubItems
{
    public const string Key = "auth_core.github.key";
    public const string Email = "auth_core.github.email";
    public const string Verified = "auth_core.github.verified";

    public static void Write(IDictionary<string, string?> items, GitHubAccount account)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(account);

        items[Key] = account.Key;
        items[Email] = account.Email;
        items[Verified] = account.EmailVerified ? "true" : "false";
    }

    /// <summary>The identity the reader handed over; <see langword="null"/> when there is none, or its key is not a decimal number.</summary>
    public static GitHubAccount? Read(IDictionary<string, string?>? items)
    {
        if (items is null
            || !items.TryGetValue(Key, out var key)
            || key is not { Length: > 0 and <= 19 }
            || !key.All(char.IsAsciiDigit))
        {
            return null;
        }

        var email = items.TryGetValue(Email, out var address) ? address : null;
        var verified = items.TryGetValue(Verified, out var flag) && string.Equals(flag, "true", StringComparison.Ordinal);
        return new GitHubAccount(key, email, email is not null && verified);
    }
}
```

In `src/Auth.Server/Providers/ProviderIdentities.cs`, replace

```csharp
    private static ProviderIdentity? ReadGoogle(ClaimsPrincipal token, ILookupNormalizer normalizer)
```

with

```csharp
    /// <summary>
    /// Spec 0012: the identity of GitHub, from what the reader read (GitHub has no identity token), with the invitation rule of the other two: an
    /// address that breaks it is kept and unverified.
    /// </summary>
    public static ProviderIdentity FromGitHub(GitHubAccount account, ILookupNormalizer normalizer)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(normalizer);

        return WithAddress(ProviderNames.GitHub, account.Key, account.Email, account.EmailVerified, normalizer);
    }

    private static ProviderIdentity? ReadGoogle(ClaimsPrincipal token, ILookupNormalizer normalizer)
```

(`Read` keeps answering `null` for `github`: GitHub never has an identity token. `ProviderIdentityTests.An_unknown_provider_or_no_token_is_refused`
pins that and stays as it is.)

- [ ] **Step 4: The reader.** `src/Auth.Server/Providers/GitHubReader.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenIddict.Abstractions;
using OpenIddict.Client;

namespace Auth.Server.Providers;

/// <summary>
/// Auth-Core's GitHub reader (spec 0012 → GitHub, step 2): an event handler of OpenIddict's client that runs on the callback of the <c>github</c>
/// registration right after the token response, before anything that could refuse the round trip and before the callback endpoint. With the
/// access token it reads <c>GET /user</c> and <c>GET /user/emails?per_page=100</c> through a client of its own (one attempt per call, Amendment
/// A4), each bounded to 10 seconds and 64 KiB; then, in a <c>finally</c>, it revokes the token on a bound of its own, never the request's, and
/// removes it from the context, so that the callback never sees it (Amendment A3). It hands over the identity in three items
/// (<see cref="GitHubItems"/>); a read that fails, a document that is not GitHub's, and a token answer without a token are refused as
/// <c>server_error</c>, which the callback answers with <c>provider_error</c>. A revocation that fails logs one warning with the provider and the
/// status, never the token, and changes nothing else.
/// </summary>
public sealed partial class GitHubReader(IHttpClientFactory clients, ProviderSettings settings, TimeProvider clock, ILogger<GitHubReader> logger)
    : IOpenIddictClientHandler<OpenIddictClientEvents.ProcessAuthenticationContext>
{
    public const string HttpClientName = "auth-core-github";

    /// <summary>The error address of a refusal of the reader, for the log of the callback.</summary>
    public const string ErrorUri = "urn:auth-core:github-read";

    public const int MaxBodyBytes = 64 * 1024;

    public static readonly TimeSpan CallBound = TimeSpan.FromSeconds(10);

    public static readonly Uri WebApiBase = new("https://api.github.com");

    /// <summary>After <c>ResolveValidatedBackchannelTokens</c>, which gives the token, and long before the first handler that could refuse the round trip.</summary>
    public static OpenIddictClientHandlerDescriptor Descriptor { get; } =
        OpenIddictClientHandlerDescriptor.CreateBuilder<OpenIddictClientEvents.ProcessAuthenticationContext>()
            .UseSingletonHandler<GitHubReader>()
            .SetOrder(OpenIddictClientHandlers.ResolveValidatedBackchannelTokens.Descriptor.Order + 1_000)
            .SetType(OpenIddictClientHandlerType.Custom)
            .Build();

    public async ValueTask HandleAsync(OpenIddictClientEvents.ProcessAuthenticationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.EndpointType != OpenIddictClientEndpointType.Redirection
            || !string.Equals(context.Registration?.ProviderName, ProviderNames.GitHub, StringComparison.Ordinal)
            || context.TokenResponse is null)
        {
            return;
        }

        var token = context.BackchannelAccessToken;
        if (string.IsNullOrEmpty(token))
        {
            // A token answer without access_token: OpenIddict alone would answer it as a state it cannot read (Amendment A3).
            context.Reject(error: OpenIddictConstants.Errors.ServerError, description: "GitHub gave no access token.", uri: ErrorUri);
            return;
        }

        var client = clients.CreateClient(HttpClientName);
        var api = settings.TestIssuer is null ? WebApiBase : settings.TestIssuerOf(ProviderNames.GitHub);
        GitHubAccount? account;
        try
        {
            account = await ReadAsync(client, api, token, context.CancellationToken);
        }
        finally
        {
            await RevokeAsync(client, api, context.Registration, token);
            // Setting only the first would make OpenIddict refuse the round trip for a missing token.
            context.BackchannelAccessToken = null;
            context.RequireBackchannelAccessToken = false;
        }

        if (account is null)
        {
            context.Reject(error: OpenIddictConstants.Errors.ServerError, description: "The GitHub account could not be read.", uri: ErrorUri);
            return;
        }

        GitHubItems.Write(context.Properties, account);
    }

    private async Task<GitHubAccount?> ReadAsync(HttpClient client, Uri api, string token, CancellationToken aborted)
    {
        try
        {
            using var user = await GetJsonAsync(client, Combine(api, "/user"), token, aborted);
            if (user is null)
            {
                return null;
            }

            using var emails = await GetJsonAsync(client, Combine(api, "/user/emails?per_page=100"), token, aborted);
            return emails is null ? null : GitHubIdentity.Read(user.RootElement, emails.RootElement);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is OperationCanceledException && !aborted.IsCancellationRequested))
        {
            return null;   // unreachable, slower than the bound, or not JSON: provider_error
        }
    }

    /// <summary>One <c>GET</c>, its answer a <c>200</c> with at most <see cref="MaxBodyBytes"/>; <see langword="null"/> otherwise.</summary>
    private async Task<JsonDocument?> GetJsonAsync(HttpClient client, Uri url, string token, CancellationToken aborted)
    {
        using var bound = new CancellationTokenSource(CallBound, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, bound.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        var body = await ReadBoundedAsync(response.Content, linked.Token);
        return body is null ? null : JsonDocument.Parse(body);
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxBodyBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0)
            {
                return buffer[..read];
            }

            read += count;
        }

        return null;   // larger than 64 KiB
    }

    /// <summary><c>DELETE /applications/{client_id}/token</c>, Basic with the registration's id and secret, the token in the body (spec 0012, step 4).</summary>
    private async Task RevokeAsync(HttpClient client, Uri api, OpenIddictClientRegistration registration, string token)
    {
        var clientId = registration.ClientId ?? throw new InvalidOperationException("The GitHub registration has no client id.");
        string status;
        try
        {
            // A bound of its own: a client that goes away must not leave the token alive.
            using var bound = new CancellationTokenSource(CallBound, clock);
            using var request = new HttpRequestMessage(HttpMethod.Delete, Combine(api, "/applications/" + Uri.EscapeDataString(clientId) + "/token"))
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal) { ["access_token"] = token }),
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + registration.ClientSecret)));
            using var response = await client.SendAsync(request, bound.Token);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return;
            }

            status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        }
        catch (OperationCanceledException)
        {
            status = "timeout";
        }
        catch (HttpRequestException)
        {
            status = "unreachable";
        }

        LogNotRevoked(logger, ProviderNames.GitHub, status);
    }

    private static Uri Combine(Uri api, string pathAndQuery) => new(api.AbsoluteUri.TrimEnd('/') + pathAndQuery);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Provider} access token could not be revoked (the answer was {Status}); the round trip goes on.")]
    private static partial void LogNotRevoked(ILogger logger, string provider, string status);
}
```

- [ ] **Step 5: The registrations.** In `src/Auth.Server/Providers/ProviderClientSetup.cs`:

(a) replace

```csharp
        return builder.AddClient(options =>
        {
```

with

```csharp
        // Spec 0012, Amendment A4: the GitHub reader's own client, one attempt per call (OpenIddict's own clients retry), with GitHub's headers.
        // It follows no redirect: a 3xx is "not 200" (provider_error), not a second request, and a revocation is never repeated at another address.
        builder.Services.AddHttpClient(GitHubReader.HttpClientName, client =>
        {
            client.Timeout = GitHubReader.CallBound;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Auth-Core");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        return builder.AddClient(options =>
        {
```

(b) replace

```csharp
            options.AddEventHandler(UserInfoOff.Descriptor);
```

with

```csharp
            options.AddEventHandler(UserInfoOff.Descriptor);
            options.AddEventHandler(GitHubReader.Descriptor);
```

(c) replace

```csharp
                    if (settings.KeysOf(provider) is { } testKeys)
                    {
                        options.AddRegistration(new OpenIddictClientRegistration
                        {
                            Issuer = settings.TestIssuerOf(provider),
                            ProviderName = provider,
                            ProviderDisplayName = ProviderNames.DisplayNameOf(provider),
                            ClientId = testKeys.ClientId,
                            ClientSecret = testKeys.ClientSecret,
                            RedirectUri = settings.CallbackUriOf(provider),
                            Scopes = { Scopes.OpenId, Scopes.Email, Scopes.Profile },
                        });
                    }
```

with

```csharp
                    if (settings.KeysOf(provider) is { } testKeys)
                    {
                        var registration = new OpenIddictClientRegistration
                        {
                            Issuer = settings.TestIssuerOf(provider),
                            ProviderName = provider,
                            ProviderDisplayName = ProviderNames.DisplayNameOf(provider),
                            ClientId = testKeys.ClientId,
                            ClientSecret = testKeys.ClientSecret,
                            RedirectUri = settings.CallbackUriOf(provider),
                        };

                        // GitHub is OAuth 2.0 (spec 0012, Amendment A5): the scope user:email and no openid, so no identity token is asked for.
                        foreach (var scope in provider == ProviderNames.GitHub ? GitHubScopes : OpenIdScopes)
                        {
                            registration.Scopes.Add(scope);
                        }

                        options.AddRegistration(registration);
                    }
```

(d) replace

```csharp
                    .SetProviderDisplayName(ProviderNames.DisplayNameOf(ProviderNames.Microsoft)));
            }
        });
    }
```

with

```csharp
                    .SetProviderDisplayName(ProviderNames.DisplayNameOf(ProviderNames.Microsoft)));
            }

            if (settings.GitHub is { } gitHub)
            {
                // Spec 0012: an OAuth App; OpenIddict's provider requires GitHub's `iss` on the answer (Amendment A6) and sends PKCE (A7).
                web.AddGitHub(registration => registration
                    .SetClientId(gitHub.ClientId)
                    .SetClientSecret(gitHub.ClientSecret)
                    .SetRedirectUri(settings.CallbackUriOf(ProviderNames.GitHub))
                    .AddScopes(GitHubScopes)
                    .SetProviderName(ProviderNames.GitHub)
                    .SetProviderDisplayName(ProviderNames.DisplayNameOf(ProviderNames.GitHub)));
            }
        });
    }

    private static readonly string[] OpenIdScopes = [Scopes.OpenId, Scopes.Email, Scopes.Profile];

    private static readonly string[] GitHubScopes = ["user:email"];
```

and in the class summary replace `/// providers of Google and Microsoft (tenant <c>common</c>), or, with the test issuer (Development), plain OpenID Connect registrations of it.`
with `/// providers of Google, Microsoft (tenant <c>common</c>) and GitHub, or, with the test issuer (Development), plain registrations of it (OpenID Connect for the`
`/// first two, OAuth 2.0 for GitHub, spec 0012). <see cref="GitHubReader"/> reads GitHub's identity in both modes.` (the summary ends with the line after it, as before).

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.GitHubIdentityTests -class Auth.IntegrationTests.ProviderIdentityTests -class Auth.IntegrationTests.ProviderStartTests -class Auth.IntegrationTests.ProviderCallbackTests -class Auth.IntegrationTests.ProviderCallbackErrorTests -class Auth.IntegrationTests.ProviderTestIssuerTests -class Auth.IntegrationTests.FakeGitHubTests`
passes. The round trip of GitHub is driven by the tests of Task 6; this task leaves Google and Microsoft unchanged.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): Auth-Core reads GitHub's identity and revokes its token`.

---

### Task 6: The GitHub start and callback, and every error path

**Files:**
- Modify: `src/Auth.Server/Providers/ProviderStartEndpoint.cs`, `src/Auth.Server/Providers/ProviderCallbackEndpoint.cs`, `src/Auth.Server/Api/ProviderRoutes.cs`,
  `src/Auth.Server/Api/SignupRoutes.cs`
- Create (tests): `tests/Auth.IntegrationTests/GitHubStartTests.cs`, `tests/Auth.IntegrationTests/GitHubCallbackTests.cs`,
  `tests/Auth.IntegrationTests/GitHubCallbackErrorTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/ProviderTestBase.cs`, `tests/Auth.IntegrationTests/ProviderTestIssuerTests.cs`,
  `tests/Auth.IntegrationTests/MeProvidersTests.cs`

**Interfaces:**
- Consumes: `GitHubItems`, `ProviderIdentities.FromGitHub`, the reader and the registrations (Task 5); `FakeOidcServer.GitHub` and the people and
  defects (Task 4).
- Produces: GitHub tickets of every purpose; test side `ProviderTestBase(postgres, keys, bool github = false)` (lists the three providers when
  set) and `ProviderTestBase.GitHubPerson(email, verified, subject, defect)`.

**Contract counts:** `OpenApiTests` and `OpenApiHardeningTests`: none (the descriptions of start and callback name GitHub in their text only).

**Decisions the plan takes here:**
- The start keeps its rules and adds one: `max_age=0` goes to Google's reauth only (probe U4); GitHub gets `prompt=select_account` for every
  purpose, Microsoft `prompt=login` for a reauth as before.
- The callback reads GitHub's identity from the reader's items and every other provider's from its identity token; nothing else of the callback
  changes, so `invalid_state`, `cancelled`, `provider_unavailable` and `provider_error` mean for GitHub what they mean for the others.
- Each error test asserts the whole refusal: the fragment code and purpose, no ticket, no `auth_pt`, `no-store`, and for each path where GitHub
  issued a token exactly one revocation of that very token (Amendment A4: one attempt); a refused read is also one request to the fake.

- [ ] **Step 1: Write the failing tests.**

In `tests/Auth.IntegrationTests/Infrastructure/ProviderTestBase.cs`:

(a) replace

```csharp
    private HttpClient? _browser;

    protected ProviderTestBase(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Fake = new FakeOidcHost(Clock);
        Factory.WithPrivateSignup()
            .WithProviders()
            .WithServices(services => services.AddSingleton(Fake.Routing));
    }
```

with

```csharp
    private readonly string _providersLine;
    private HttpClient? _browser;

    /// <param name="github">Spec 0012: list GitHub too, after Google and Microsoft. Every provider has its keys either way.</param>
    protected ProviderTestBase(PostgresFixture postgres, KeyMaterialFixture keys, bool github = false)
        : base(postgres, keys)
    {
        Fake = new FakeOidcHost(Clock);
        _providersLine = github ? AuthAppFactory.AllProvidersLine : AuthAppFactory.ProvidersLine;
        Factory.WithPrivateSignup()
            .WithProviders(github)
            .WithServices(services => services.AddSingleton(Fake.Routing));
    }
```

(b) replace

```csharp
    /// <summary>
    /// Makes this manifest active in the running host at once (the switches and the offered providers follow it), as a start with another file
    /// would; both providers stay listed unless <paramref name="withProviders"/> is <see langword="false"/>.
    /// </summary>
    protected void SetManifest(string yaml, bool withProviders = true)
    {
        var parsed = ManifestParser.Parse(yaml + (withProviders ? AuthAppFactory.ProvidersLine : ""));
```

with

```csharp
    /// <summary>
    /// Makes this manifest active in the running host at once (the switches and the offered providers follow it), as a start with another file
    /// would; the providers of this host stay listed unless <paramref name="withProviders"/> is <see langword="false"/>.
    /// </summary>
    protected void SetManifest(string yaml, bool withProviders = true)
    {
        var parsed = ManifestParser.Parse(yaml + (withProviders ? _providersLine : ""));
```

(c) replace

```csharp
    /// <summary>The fake's answer to the start's redirect for this person: the path and query of the callback, with the code and the state.</summary>
```

with

```csharp
    /// <summary>The GitHub person of the fake (Ola: a verified primary address), or another address, verification, id or defect (spec 0012).</summary>
    protected static FakeSignIn GitHubPerson(string? email = null, bool? verified = null, string? subject = null, string? defect = null) =>
        new(FakePeople.GitHub, email, verified, subject, Defect: defect);

    /// <summary>The fake's answer to the start's redirect for this person: the path and query of the callback, with the code and the state.</summary>
```

`tests/Auth.IntegrationTests/GitHubStartTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → GitHub, step 1 (criterion 2): the start of a GitHub round trip, for every purpose.</summary>
public sealed class GitHubStartTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys, github: true)
{
    private const string Invite43 = "AAAAAAAAAA" + "AAAAAAAAAA" + "AAAAAAAAAA" + "AAAAAAAAAA" + "AAA";

    [Fact]
    public async Task The_start_answers_302_to_githubs_authorization_endpoint_with_the_code_flow_pkce_a_state_and_user_email()   // criterion 2
    {
        var start = await StartAsync("github");

        Assert.Equal("https://github.com/login/oauth/authorize", start.Location.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", start.Query["response_type"]);
        Assert.Equal("S256", start.Query["code_challenge_method"]);   // Amendment A7
        Assert.Equal(43, start.Query["code_challenge"].Length);
        Assert.NotEmpty(start.Query["state"]);
        Assert.Equal("user:email", start.Query["scope"]);   // nothing of OpenID Connect
        Assert.False(start.Query.ContainsKey("nonce"));
        Assert.Equal(AuthAppFactory.GitHubClientId, start.Query["client_id"]);
        Assert.Equal("https://app.example.com/auth/providers/github/callback", start.Query["redirect_uri"]);
        Assert.Equal("select_account", start.Query["prompt"]);
        Assert.False(start.Query.ContainsKey("max_age"));
        Assert.False(start.Query.ContainsKey("response_mode"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?purpose=reauth")]
    [InlineData("?invite=" + Invite43)]
    public async Task Every_purpose_asks_github_for_select_account_and_never_max_age(string query)   // criterion 2, Amendment A7
    {
        var start = await StartAsync("github", query);

        Assert.Equal("select_account", start.Query["prompt"]);
        Assert.False(start.Query.ContainsKey("max_age"));
        Assert.Equal("S256", start.Query["code_challenge_method"]);
    }

    [Fact]
    public async Task Google_alone_gets_max_age_0_on_a_reauth()   // the rule of spec 0011, for Google only
    {
        var google = await StartAsync("google", "?purpose=reauth");
        var microsoft = await StartAsync("microsoft", "?purpose=reauth");

        Assert.Equal("0", google.Query["max_age"]);
        Assert.False(microsoft.Query.ContainsKey("max_age"));
        Assert.Equal("login", microsoft.Query["prompt"]);
    }

    [Fact]
    public async Task The_callback_address_comes_from_the_return_address_whatever_the_host_headers_say()   // spec 0012 → Configuration
    {
        var start = await ProviderApi.StartAsync(Browser, "github", configure: request =>
        {
            request.Headers.Host = "evil.test";
            request.Headers.Add("X-Forwarded-Host", "evil.test");
        });

        Assert.Equal("https://app.example.com/auth/providers/github/callback", start.Query["redirect_uri"]);
    }

    [Fact]
    public async Task The_request_forgery_cookie_is_the_one_of_the_other_providers()   // spec 0012, step 1: the state bound to the browser
    {
        var cookie = (await StartAsync("github")).StateCookieHeader;

        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, cookie.SameSite);
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("/auth/providers", cookie.Path.Value);
    }

    [Fact]
    public async Task A_malformed_query_is_400_and_a_github_dropped_from_the_manifest_is_404()   // spec 0011's rules, unchanged
    {
        using var other = await Browser.GetAsync("/auth/providers/github/start?purpose=login");
        await TenancyApi.AssertErrorAsync(other, HttpStatusCode.BadRequest, "invalid_request");

        SetManifest(AuthAppFactory.SelfServiceManifest + AuthAppFactory.PrivateBlock + AuthAppFactory.ProvidersLine, withProviders: false);
        using var dropped = await Browser.GetAsync("/auth/providers/github/start");
        await TenancyApi.AssertErrorAsync(dropped, HttpStatusCode.NotFound, "not_found");
    }
}
```

`tests/Auth.IntegrationTests/GitHubCallbackTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Client.AspNetCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → GitHub, steps 2 to 4 (criteria 3 and 7): the callback reads, revokes, and makes the ticket of the identity.</summary>
public sealed class GitHubCallbackTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys, github: true)
{
    private string RevocationRequest => $"DELETE /applications/{AuthAppFactory.GitHubClientId}/token";

    [Fact]
    public async Task A_good_code_reads_user_and_emails_revokes_the_token_and_makes_a_ticket_of_the_primary_address()   // criterion 3
    {
        var ticket = await TicketAsync("github", GitHubPerson());

        Assert.Equal("sign_in", ticket.Purpose);
        Assert.Equal(43, ticket.Value.Length);
        var row = await InDbAsync(db => db.ProviderTickets.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("github", FakePeople.GitHubId, FakePeople.GitHubEmail, true), (row.Provider, row.IdentityKey, row.Email, row.EmailVerified));
        Assert.Equal(
            ["POST /login/oauth/access_token", "GET /user", "GET /user/emails?per_page=100", RevocationRequest],
            Fake.Server.GitHub.Requests);
        var token = Assert.Single(Fake.Server.GitHub.IssuedTokens);
        Assert.Equal(new GitHubRevocation(AuthAppFactory.GitHubClientId, token, 204), Assert.Single(Fake.Server.GitHub.Revocations));
    }

    [Theory]
    [InlineData(FakePeople.GitHubUnverified, FakePeople.GitHubUnverifiedId, FakePeople.GitHubUnverifiedEmail, false)]
    [InlineData(FakePeople.GitHubNoPrimary, FakePeople.GitHubNoPrimaryId, null, false)]
    public async Task An_unverified_primary_address_and_none_come_back_as_such(string person, string key, string? email, bool verified)   // criterion 3
    {
        await TicketAsync("github", new FakeSignIn(person));

        var row = await InDbAsync(db => db.ProviderTickets.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal((key, email, verified), (row.IdentityKey, row.Email, row.EmailVerified));
    }

    [Fact]
    public async Task A_free_address_is_one_github_identity_derived_from_it()   // criterion 32: a GitHub person and a Google person with one address
    {
        await TicketAsync("github", new FakeSignIn(FakePeople.Custom, "anna@firma.test", Verified: true));
        await TicketAsync("github", new FakeSignIn(FakePeople.Custom, "anna@firma.test", Verified: true));

        var keys = await InDbAsync(db => db.ProviderTickets.AsNoTracking().Select(t => t.IdentityKey).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal([FakePeople.GitHubIdOf("anna@firma.test"), FakePeople.GitHubIdOf("anna@firma.test")], keys);
    }

    [Fact]
    public async Task An_invitation_and_a_reauth_come_back_with_their_purpose_and_the_reauth_ticket_lives_five_minutes()   // criterion 3
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, FakePeople.GitHubEmail, "user");
        await EnqueueInvitationAsync(invite, FakePeople.GitHubEmail);
        await DispatchAsync();
        var token = TokenIn(Mail.Sent.Last(m => m.To == FakePeople.GitHubEmail));

        Assert.Equal("invite", (await TicketAsync("github", GitHubPerson(), "?invite=" + token)).Purpose);
        Assert.Equal("reauth", (await TicketAsync("github", GitHubPerson(), "?purpose=reauth")).Purpose);
        var reauth = await InDbAsync(db => db.ProviderTickets.AsNoTracking().SingleAsync(t => t.Purpose == Auth.Infrastructure.Persistence.ProviderTicketPurpose.Reauth, TestContext.Current.CancellationToken));
        Assert.Equal(Auth.Server.Email.StorableTime.Now(Clock) + TimeSpan.FromMinutes(5), reauth.ExpiresAt);
        Assert.Equal(2, Fake.Server.GitHub.Revocations.Count);
    }

    [Fact]
    public async Task No_code_token_state_ticket_or_client_secret_reaches_the_log()   // criterion 7
    {
        var start = await StartAsync("github");
        var back = await AuthorizeAsync(start, GitHubPerson());

        using var callback = await CallbackAsync(back, start.StateCookie);

        var ticket = ProviderApi.TicketOf(callback);
        var code = QueryHelpers.ParseQuery(new Uri("https://app.example.com" + back).Query)["code"].ToString();
        var text = Logs.Text;
        Assert.DoesNotContain(code, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Assert.Single(Fake.Server.GitHub.IssuedTokens), text, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthAppFactory.GitHubClientSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(start.Query["state"], text, StringComparison.Ordinal);
        Assert.DoesNotContain("code_verifier", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket.Value, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket.Binding, text, StringComparison.Ordinal);
        Assert.Contains(Logs.Entries, e => e.Message.Contains("github", StringComparison.Ordinal) && e.Message.Contains("sign_in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenIddict_reads_no_userinfo_and_the_reader_reads_each_document_once()   // spec 0012, step 2: userinfo stays off
    {
        await TicketAsync("github", GitHubPerson());

        Assert.Single(Fake.Server.GitHub.Requests, r => r == "GET /user");
        Assert.Single(Fake.Server.GitHub.Requests, r => r == "GET /user/emails?per_page=100");
        Assert.Equal(0, Fake.Server.UserInfoRequests);
    }

    [Fact]
    public async Task A_callback_that_reaches_the_service_under_another_host_still_makes_a_ticket()   // Amendment A2 of spec 0011, for GitHub
    {
        var start = await StartAsync("github");
        var back = await AuthorizeAsync(start, GitHubPerson());

        using var callback = await ProviderApi.CallbackAsync(Browser, back, start.StateCookie, request => request.Headers.Host = "auth-core:8080");

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(FakeDefects.UserFails, false)]
    public async Task The_callback_never_sees_the_github_token_after_a_good_read_or_a_failed_one(string? defect, bool good)   // Amendment A3, criterion 7
    {
        var seen = new List<IDictionary<string, string?>>();
        Factory.WithServices(services => services.AddScoped<IAuthenticationService>(
            provider => ActivatorUtilities.CreateInstance<RecordedAuthentication>(provider, seen)));
        var start = await StartAsync("github");
        var back = await AuthorizeAsync(start, GitHubPerson(defect: defect));

        using var callback = await CallbackAsync(back, start.StateCookie);

        var token = Assert.Single(Fake.Server.GitHub.IssuedTokens);
        var items = Assert.Single(seen);   // the outcome of the client's authentication, as the callback reads it
        Assert.DoesNotContain(items.Keys, key => key.Contains("access_token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items.Values, value => value is not null && value.Contains(token, StringComparison.Ordinal));
        Assert.Equal(good, items.ContainsKey(GitHubItems.Key));   // not vacuous: the identity is there on a good read, and only then
    }

    /// <summary>Keeps the authentication properties of the OpenIddict client's outcome, which is what the callback endpoint receives.</summary>
    private sealed class RecordedAuthentication(
        IAuthenticationSchemeProvider schemes, IAuthenticationHandlerProvider handlers, IClaimsTransformation transformation,
        IOptions<AuthenticationOptions> options, List<IDictionary<string, string?>> seen)
        : AuthenticationService(schemes, handlers, transformation, options)
    {
        public override async Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            var result = await base.AuthenticateAsync(context, scheme);
            if (string.Equals(scheme, OpenIddictClientAspNetCoreDefaults.AuthenticationScheme, StringComparison.Ordinal) && result.Properties is { } properties)
            {
                seen.Add(new Dictionary<string, string?>(properties.Items, StringComparer.Ordinal));
            }

            return result;
        }
    }
}
```

`tests/Auth.IntegrationTests/GitHubCallbackErrorTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0012 → GitHub (criterion 5), one test per path: every failure of a GitHub round trip is one of the four codes of spec 0011, makes no ticket
/// and sets no <c>auth_pt</c>, and every token GitHub issued is revoked once. A failed revocation alone still makes the ticket.
/// </summary>
public sealed class GitHubCallbackErrorTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys, github: true)
{
    /// <summary>The start and the fake's answer for this person; the callback is the caller's.</summary>
    private async Task<(ProviderApi.Start Start, string Back)> RoundTripAsync(FakeSignIn person, string query = "")
    {
        var start = await StartAsync("github", query);
        return (start, await AuthorizeAsync(start, person));
    }

    private async Task<HttpResponseMessage> CallbackOfAsync(FakeSignIn person)
    {
        var (start, back) = await RoundTripAsync(person);
        return await CallbackAsync(back, start.StateCookie);
    }

    private async Task AssertRefusedAsync(HttpResponseMessage callback, string code, string? purpose = "sign_in")
    {
        var fragment = ProviderApi.FragmentOf(callback);
        Assert.Equal(code, fragment["error"]);
        Assert.Equal(purpose, fragment.GetValueOrDefault("purpose"));
        Assert.False(fragment.ContainsKey("ticket"));
        Assert.Null(ProviderApi.BindingCookieOf(callback));
        Assert.Contains("no-store", callback.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await TicketCountAsync());
    }

    /// <summary>GitHub issued one token, and it was revoked once (one attempt: Amendment A4).</summary>
    private void AssertRevokedOnce()
    {
        var token = Assert.Single(Fake.Server.GitHub.IssuedTokens);
        var revocation = Assert.Single(Fake.Server.GitHub.Revocations);
        Assert.Equal(token, revocation.Token);
    }

    [Fact]
    public async Task Access_denied_with_a_valid_state_is_cancelled()   // criterion 5
    {
        using var callback = await CallbackOfAsync(new FakeSignIn(FakePeople.Cancel));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.Cancelled);
        Assert.Empty(Fake.Server.GitHub.IssuedTokens);
    }

    [Theory]
    [InlineData(FakePeople.GitHub)]
    [InlineData(FakePeople.Cancel)]
    public async Task An_answer_without_iss_is_provider_error_even_for_access_denied(string person)   // Amendment A6
    {
        using var callback = await CallbackOfAsync(new FakeSignIn(person, Defect: FakeDefects.NoIss));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        Assert.Empty(Fake.Server.GitHub.IssuedTokens);   // refused before the code is redeemed
    }

    [Fact]
    public async Task A_missing_or_forged_state_or_no_request_forgery_cookie_is_invalid_state()   // criterion 5: as for the others
    {
        var (start, back) = await RoundTripAsync(GitHubPerson());

        using var noCookie = await CallbackAsync(back, null);
        using var noState = await CallbackAsync("/auth/providers/github/callback?code=abc", start.StateCookie);

        await AssertRefusedAsync(noCookie, ProviderCallbackErrors.InvalidState);
        await AssertRefusedAsync(noState, ProviderCallbackErrors.InvalidState, purpose: null);
        Assert.Empty(Fake.Server.GitHub.IssuedTokens);
    }

    [Fact]
    public async Task A_refused_code_is_provider_error_and_no_token_exists_to_revoke()   // criterion 5
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.RefuseCode));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        Assert.Empty(Fake.Server.GitHub.IssuedTokens);
        Assert.Empty(Fake.Server.GitHub.Revocations);
    }

    [Fact]
    public async Task A_token_answer_without_an_access_token_is_provider_error()   // Amendment A3
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.NoToken));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        Assert.DoesNotContain(Fake.Server.GitHub.Requests, r => r.StartsWith("GET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_provider_error_at_github_is_provider_error()   // criterion 5
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.ProviderError));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
    }

    [Fact]
    public async Task A_user_answer_that_is_not_200_is_provider_error_after_one_attempt_and_the_token_is_revoked()   // criterion 5, Review Focus 3
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.UserFails));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        Assert.Single(Fake.Server.GitHub.Requests, r => r == "GET /user");   // no retry
        Assert.DoesNotContain("GET /user/emails?per_page=100", Fake.Server.GitHub.Requests);
        AssertRevokedOnce();
    }

    [Fact]
    public async Task A_user_answer_that_is_not_json_is_provider_error_and_the_token_is_revoked()   // criterion 5
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.UserNotJson));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertRevokedOnce();
    }

    [Theory]
    [InlineData(FakeDefects.UserNoId)]
    [InlineData(FakeDefects.UserBadId)]
    public async Task A_user_without_an_id_or_with_one_that_is_no_number_is_provider_error_and_the_token_is_revoked(string defect)   // criterion 5
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: defect));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertRevokedOnce();
    }

    [Fact]
    public async Task A_user_answer_larger_than_64_kib_is_provider_error_and_the_token_is_revoked()   // spec 0012, step 3
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.UserTooLarge));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertRevokedOnce();
    }

    [Fact]
    public async Task A_user_answer_slower_than_ten_seconds_is_provider_error_and_the_token_is_revoked()   // spec 0012, step 3; Review Focus 3
    {
        var (start, back) = await RoundTripAsync(GitHubPerson(defect: FakeDefects.UserSlow));

        var pending = CallbackAsync(back, start.StateCookie);
        await Fake.Server.GitHub.SlowRequestArrived.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Clock.Advance(GitHubReader.CallBound + TimeSpan.FromSeconds(1));
        using var callback = await pending;

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertRevokedOnce();
    }

    [Fact]
    public async Task An_emails_answer_that_is_not_200_is_provider_error_after_one_attempt_and_the_token_is_revoked()   // criterion 5
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.EmailsFails));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        Assert.Single(Fake.Server.GitHub.Requests, r => r == "GET /user/emails?per_page=100");
        AssertRevokedOnce();
    }

    [Theory]
    [InlineData(FakeDefects.EmailsNotJson)]
    [InlineData(FakeDefects.EmailsBadEntry)]
    public async Task An_emails_answer_that_is_not_the_documented_json_is_provider_error_and_the_token_is_revoked(string defect)   // criterion 5
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: defect));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertRevokedOnce();
    }

    [Fact]
    public async Task An_emails_answer_larger_than_64_kib_is_provider_error_and_the_token_is_revoked()   // spec 0012, step 3: the bound of the second call
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.EmailsTooLarge));

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        AssertRevokedOnce();
    }

    [Fact]
    public async Task An_emails_answer_slower_than_ten_seconds_is_provider_error_and_the_token_is_revoked()   // spec 0012, step 3: the bound of the second call
    {
        var (start, back) = await RoundTripAsync(GitHubPerson(defect: FakeDefects.EmailsSlow));

        var pending = CallbackAsync(back, start.StateCookie);
        await Fake.Server.GitHub.SlowRequestArrived.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Clock.Advance(GitHubReader.CallBound + TimeSpan.FromSeconds(1));
        using var callback = await pending;

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderError);
        Assert.Single(Fake.Server.GitHub.Requests, r => r == "GET /user");
        AssertRevokedOnce();
    }

    [Fact]
    public async Task A_failed_revocation_still_makes_the_ticket_and_logs_one_warning_without_the_token()   // criterion 5, Settled 20
    {
        using var callback = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.RevokeFails));

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);
        Assert.Equal(500, Assert.Single(Fake.Server.GitHub.Revocations).Status);   // tried once
        var warning = Assert.Single(Logs.Entries, e => e.Level == LogLevel.Warning && e.Category == typeof(GitHubReader).FullName);
        Assert.Contains("github", warning.Message, StringComparison.Ordinal);
        Assert.Contains("500", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Assert.Single(Fake.Server.GitHub.IssuedTokens), Logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revocation_slower_than_ten_seconds_still_makes_the_ticket_and_logs_one_warning_naming_the_timeout()   // spec 0012, step 4: the revocation's own bound
    {
        var (start, back) = await RoundTripAsync(GitHubPerson(defect: FakeDefects.RevokeSlow));

        var pending = CallbackAsync(back, start.StateCookie);
        await Fake.Server.GitHub.SlowRequestArrived.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Clock.Advance(GitHubReader.CallBound + TimeSpan.FromSeconds(1));
        using var callback = await pending;

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);
        Assert.Equal(0, Assert.Single(Fake.Server.GitHub.Revocations).Status);   // tried once, never answered
        var warning = Assert.Single(Logs.Entries, e => e.Level == LogLevel.Warning && e.Category == typeof(GitHubReader).FullName);
        Assert.Contains("github", warning.Message, StringComparison.Ordinal);
        Assert.Contains("timeout", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Assert.Single(Fake.Server.GitHub.IssuedTokens), Logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_dropped_from_the_manifest_during_the_round_trip_is_provider_unavailable_and_the_token_is_revoked()   // spec 0011's rule
    {
        var (start, back) = await RoundTripAsync(GitHubPerson());
        SetManifest(AuthAppFactory.SelfServiceManifest + AuthAppFactory.PrivateBlock + AuthAppFactory.ProvidersLine, withProviders: false);

        using var callback = await CallbackAsync(back, start.StateCookie);

        await AssertRefusedAsync(callback, ProviderCallbackErrors.ProviderUnavailable);
        AssertRevokedOnce();
    }

    [Fact]
    public async Task No_refusal_writes_an_audit_row()   // spec 0011: a failed callback writes no row
    {
        var before = (await AuditAsync()).Count;

        using (var refused = await CallbackOfAsync(GitHubPerson(defect: FakeDefects.UserFails)))
        {
            await AssertRefusedAsync(refused, ProviderCallbackErrors.ProviderError);
        }

        Assert.Equal(before, (await AuditAsync()).Count);
        Assert.Equal(0, await InDbAsync(db => db.ProviderTickets.CountAsync(TestContext.Current.CancellationToken)));
    }
}
```

In `tests/Auth.IntegrationTests/ProviderTestIssuerTests.cs`, replace `        : base(postgres, keys)` with `        : base(postgres, keys, github: true)` and add
before the class's last closing brace:

```csharp

    [Fact]
    public async Task GitHub_runs_through_the_test_issuers_discovery_document_with_pkce_and_the_same_reader()   // criterion 2, Amendment A5
    {
        var start = await StartAsync("github");
        Assert.Equal("http://fake-oidc.test/github/authorize", start.Location.GetLeftPart(UriPartial.Path));
        Assert.Equal("S256", start.Query["code_challenge_method"]);
        Assert.Equal("user:email", start.Query["scope"]);
        Assert.False(start.Query.ContainsKey("nonce"));
        Assert.Equal("select_account", start.Query["prompt"]);

        using var callback = await CallbackAsync(await AuthorizeAsync(start, GitHubPerson()), start.StateCookie);

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);   // no iss required: the document does not advertise it (Amendment A6)
        var row = await InDbAsync(db => db.ProviderTickets.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("github", FakePeople.GitHubId, FakePeople.GitHubEmail), (row.Provider, row.IdentityKey, row.Email));
        Assert.Contains("GET /user", Fake.Server.GitHub.Requests);
        Assert.Equal(204, Assert.Single(Fake.Server.GitHub.Revocations).Status);
        Assert.Equal(0, Fake.Server.UserInfoRequests);
    }
```

In `tests/Auth.IntegrationTests/MeProvidersTests.cs`, add before the class's last closing brace:

```csharp

    [Fact]
    public async Task A_github_login_is_listed_among_the_providers_sorted()   // spec 0012, criterion 23
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        await LinkAsync(user.Id, "github", FakePeople.GitHubId);

        var me = await MeAsync((await SessionApi.LoginAsync(Client, Ola, UserPassword)).AccessToken);

        Assert.Equal(["github", "google"], Providers(me));
    }
```

- [ ] **Step 2: See them fail.** Build, then run `GitHubCallbackTests`, `GitHubStartTests` and `GitHubCallbackErrorTests`: a good GitHub code
answers `#error=provider_error` (the callback reads no identity for GitHub yet), and GitHub's reauth start still carries `max_age=0`
(`Every_purpose_asks_github_for_select_account_and_never_max_age` fails on its reauth row). The error tests that pass already guard the reader
of Task 5: mutate it four times and see them fail, then restore (guard lesson 7) — drop the revocation of the `finally`
(`A_user_answer_that_is_not_200_is_provider_error_after_one_attempt_and_the_token_is_revoked` fails), drop the 64 KiB bound
(`A_user_answer_larger_than_64_kib_is_provider_error_and_the_token_is_revoked` fails), drop the 10-second bound of the revocation
(`A_revocation_slower_than_ten_seconds_still_makes_the_ticket_and_logs_one_warning_naming_the_timeout` hangs until its timeout), and drop both
assignments after the revocation, `BackchannelAccessToken = null` and `RequireBackchannelAccessToken = false`
(`The_callback_never_sees_the_github_token_after_a_good_read_or_a_failed_one` fails on both rows; with the first assignment alone the good round
trip fails instead).

- [ ] **Step 3: The start.** In `src/Auth.Server/Providers/ProviderStartEndpoint.cs`, replace

```csharp
        else
        {
            properties.Parameters["prompt"] = "select_account";
            if (purpose == ProviderTicketPurpose.Reauth)
            {
                // Google: the account chooser stays, and max_age=0 asks for a fresh sign-in (Google has no prompt=login).
                properties.Parameters["max_age"] = 0;
            }
        }
```

with

```csharp
        else
        {
            properties.Parameters["prompt"] = "select_account";
            if (purpose == ProviderTicketPurpose.Reauth && name == ProviderNames.Google)
            {
                // Google: the account chooser stays, and max_age=0 asks for a fresh sign-in (Google has no prompt=login). GitHub can be asked
                // for neither and gets no max_age for any purpose (spec 0012, Amendment A7): the account picker alone.
                properties.Parameters["max_age"] = 0;
            }
        }
```

- [ ] **Step 4: The callback.** In `src/Auth.Server/Providers/ProviderCallbackEndpoint.cs`, replace

```csharp
        var identityToken = result.Properties!.GetParameter<ClaimsPrincipal>(ClientProperties.BackchannelIdentityTokenPrincipal);
        if (ProviderIdentities.Read(name, identityToken, normalizer, checkMicrosoftIssuer: settings.TestIssuer is null) is not { } identity)
        {
            return Refuse(logger, returnUrl, name, ProviderCallbackErrors.ProviderError, purposeName);   // a claim the identity needs is missing
        }
```

with

```csharp
        // GitHub has no identity token: its reader handed over what it read (spec 0012, Amendment A3). The others' identity is their token's.
        var identity = name == ProviderNames.GitHub
            ? (GitHubItems.Read(items) is { } account ? ProviderIdentities.FromGitHub(account, normalizer) : null)
            : ProviderIdentities.Read(
                name, result.Properties!.GetParameter<ClaimsPrincipal>(ClientProperties.BackchannelIdentityTokenPrincipal), normalizer,
                checkMicrosoftIssuer: settings.TestIssuer is null);
        if (identity is null)
        {
            return Refuse(logger, returnUrl, name, ProviderCallbackErrors.ProviderError, purposeName);   // a claim the identity needs is missing
        }
```

and in the class summary replace `/// with the PKCE verifier), the identity token (signature, issuer, audience, nonce, expiry). This reads the outcome and always answers a redirect to`
with `/// with the PKCE verifier), the identity token (signature, issuer, audience, nonce, expiry), or for GitHub the reader's reads (spec 0012). This reads the outcome and always answers a redirect to`.

- [ ] **Step 5: The descriptions.** In `src/Auth.Server/Api/ProviderRoutes.cs`, in the description of the start replace
`"Starts a sign-in with Google or Microsoft: a product's button is a link here (a navigation, not a call). The answer is `302` to the "`
with `"Starts a sign-in with Google, Microsoft or GitHub: a product's button is a link here (a navigation, not a call). The answer is `302` to the "`,
replace `+ "provider's authorization endpoint: the authorization code flow with PKCE (`S256`), a `state`, a `nonce`, the scopes `openid email "`
`+ "profile` and the callback address built from the configured return address, never from the request. No query: a sign-in, a link or a "`
with `+ "provider's authorization endpoint: the authorization code flow with PKCE (`S256`), a `state`, a `nonce` and the scopes `openid email "`
`+ "profile` (GitHub: no `nonce` and the scope `user:email`), and the callback address built from the configured return address, never from the request. No query: a sign-in, a link or a "`,
and replace `+ "self-deletion, with a fresh sign-in asked for. It reads no account and no invitation. A name that is not `google` or `microsoft`, or a "`
with `+ "self-deletion, with a fresh sign-in asked for where the provider can be asked. It reads no account and no invitation. A name that is not `google`, `microsoft` or `github`, or a "`.

In the same file, in the summary of `ProviderRoutes`, replace `the round trip with Google or Microsoft and what a product's screens do with its ticket` with
`the round trip with Google, Microsoft or GitHub and what a product's screens do with its ticket`. In `src/Auth.Server/Api/SignupRoutes.cs`, in the description of
`GET /auth/options`, replace ``offer as buttons (`google`, `microsoft`), in the manifest's order`` with ``offer as buttons (`google`, `microsoft`, `github`), in the manifest's order``
(the line is `+ "offer as buttons (`google`, `microsoft`), in the manifest's order, `[]` when none. A client ignores members it does not know."`; edit the span only).

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.GitHubStartTests -class Auth.IntegrationTests.GitHubCallbackTests -class Auth.IntegrationTests.GitHubCallbackErrorTests -class Auth.IntegrationTests.ProviderTestIssuerTests -class Auth.IntegrationTests.MeProvidersTests -class Auth.IntegrationTests.ProviderStartTests -class Auth.IntegrationTests.ProviderCallbackTests -class Auth.IntegrationTests.ProviderCallbackErrorTests -class Auth.IntegrationTests.ProviderIdentityTests -class Auth.IntegrationTests.OpenApiTests`
passes. The slow test takes about a second, not ten: the bound is the test clock's.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): sign in with GitHub through the reader, every failure a fragment code`.

---

### Task 7: `POST /auth/account/confirm`; the new error codes and audit kinds

**Files:**
- Create: `src/Auth.Server/Account/SettingsCallers.cs`, `src/Auth.Server/Account/AccountConfirmEndpoint.cs`, `src/Auth.Server/Api/SettingsRoutes.cs`
- Modify: `src/Auth.Server/Sessions/DeviceSessions.cs`, `src/Auth.Server/Api/AccountContracts.cs`, `src/Auth.Server/Audit/AuditKinds.cs`,
  `src/Auth.Server/Tenancy/Outcome.cs`, `src/Auth.Server/Program.cs`
- Create (tests): `tests/Auth.IntegrationTests/AccountConfirmTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/ConfirmationWindowTests.cs`, `tests/Auth.IntegrationTests/ProviderErrorCodesTests.cs`,
  `tests/Auth.IntegrationTests/AuditTableTests.cs`, `tests/Auth.IntegrationTests/AuditProviderFlowsTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`,
  `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`

**Interfaces:**
- Consumes: `DeviceSessions`, `DeviceSession` (Task 2), `ConfirmationWindow` (Task 1), `ProviderTickets`, `ProviderLogins.IsLoginOfAsync`,
  `ProviderBindingCookie`, `LoginStreakStore`, `LoginIdentifier`, `TooManyAttemptsResult`, `AddressLock`, `AuditLog`.
- Produces: `SettingsCaller(Guid UserId, string Email, string NormalizedEmail, string? PasswordHash, TenantContext Tenant, DeviceSession Session)`,
  `SettingsRead(SettingsCaller? Caller, IResult? Refusal)`, `SettingsCallers.ReadAsync(HttpContext, AuthDbContext, IOpenIddictTokenManager,
  MembershipReader, TimeProvider, CancellationToken)`, `SettingsCallers.ReadAgainAsync(AuthDbContext, MembershipReader, SettingsCaller,
  DateTimeOffset now)`, `SettingsCallers.PasswordIsRight`, `SettingsCallers.RefuseAsync(AuditLog, SettingsCaller, string reason, string action)`,
  `SettingsCallers.Entry(string kind, SettingsCaller, IReadOnlyDictionary<string, object?>? details)`, `SettingsCallers.ConfirmAction`
  (`confirm`), `SettingsCallers.PasswordAction` (`password`); `DeviceSessions.ConfirmAsync(AuthDbContext, DeviceSession, DateTimeOffset until,
  CancellationToken)`; `AccountConfirmEndpoint.Path`; `ConfirmRequest`; `SettingsRoutes.MapSettingsApi`, `SettingsRoutes.RefreshCookieRead`;
  `AuditKinds.AccountConfirmed`, `AccountConfirmRefused`, `ProviderUnlinked`, `PasswordChanged`; `TenancyErrors.ConfirmationRequired` (403),
  `ProviderInUse` (409), `LastLogin` (409), `PasswordNotSet` (403).

**Contract counts:** `OpenApiTests`: `Endpoints` gains `POST /auth/account/confirm`; statuses `204,400,401,403,415,429`; body
`password,provider_ticket`; cookies `auth_pt`, `auth_rt`; it needs the bearer token; its `204` says it sets a cookie. `OpenApiHardeningTests`:
**36 → 37** operations, **24 → 25** under the charset guard. `AuditTableTests`, `AuditProviderFlowsTests`: **28 → 32** kinds (all four of
spec 0012 now, so that the later tasks only use them). `ProviderErrorCodesTests`: four codes.

**Decisions the plan takes here:**
- One helper reads the caller of every settings endpoint (`SettingsCallers`): the token's `sub`, the session of this device, the account
  (email, normalised email, hash; an account gone has no session: `401 session_required`) and the membership (`403 no_membership`). The re-read
  under the lock is the same helper with the token's id, so the four endpoints check the same things the same way.
- The password is judged outside the lock and its hash is not compared again under it: every change of the hash ends every session in its own
  transaction (a reset, a password change), so the re-read of the session under the lock answers it (`401 session_required`). What is checked
  again under the lock is the fit of the confirmation to the password the account has now (`password_required`, `reauth_required`, step 3),
  because the spec says every check that reads the password is made again there; the hash read by the re-read is the one it uses. The ticket is
  used up under the lock with the clock read after the lock, and its identity checked again there (guard lessons 1 and 11).
- `account.confirm_refused` is written alone (`WriteAloneAsync`) with `reason` and `action`; the four audit kinds are added at once, with
  their order in `AuditKinds.All` after spec 0011's.
- The `204` clears `auth_pt` when a ticket was used; a password confirmation sets no cookie.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/AccountConfirmTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Sessions;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → <c>POST /auth/account/confirm</c> (criteria 10 and 11), in the order of its checks.</summary>
public sealed class AccountConfirmTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string OlaNormalized = "OLA@GMAIL.TEST";

    // Spelled out, so that the escape in a test body is not read as an escape of this file.
    private const char Backslash = (char)0x5C;

    private Task<int> AttemptsAsync() =>
        InDbAsync(db => db.LoginStreaks.Where(s => s.IdentifierHash == LoginIdentifier.HashOf(OlaNormalized)).Select(s => s.AttemptCount)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken));

    private string OpenUntil() => SettingsApi.Window(StorableTime.Now(Clock) + ConfirmationWindow.Lifetime);

    /// <summary>An account with a password, signed in on this device, its window closed.</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> PasswordAccountAsync()
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        Clock.Advance(TimeSpan.FromMinutes(6));
        return (user.Id, session);
    }

    /// <summary>A private account made with Google (no password), signed in on this device, its window closed.</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> PasswordlessAccountAsync()
    {
        var made = await PrivateThroughProviderAsync("google", GooglePerson());
        Clock.Advance(TimeSpan.FromMinutes(6));
        return made;
    }

    private async Task FailConfirmsAsync(SessionApi.Session session, int times)
    {
        for (var i = 0; i < times; i++)
        {
            Clock.Advance(LockoutApi.HumanPace);
            using var response = await SettingsApi.Confirm(Client, session, new { password = LockoutApi.WrongPassword });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_right_password_opens_the_window_writes_account_confirmed_and_clears_the_streak()   // criterion 10
    {
        var (userId, session) = await PasswordAccountAsync();
        await FailConfirmsAsync(session, 2);
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));

        using var response = await SettingsApi.Confirm(Client, session, new { password = UserPassword });

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        Assert.Equal(OpenUntil(), await SettingsApi.ConfirmedUntilAsync(Client, session));
        Assert.Equal(0, await AttemptsAsync());
        var row = await SingleAsync(AuditKinds.AccountConfirmed);
        Assert.Equal((userId, userId, Ola), (row.ActorUserId!.Value, row.SubjectUserId!.Value, row.SubjectEmail));
        Assert.Equal(await DevCompanyIdAsync(), row.OrgId);
        Assert.Equal(["method"], AuditApi.DetailNames(row));
        Assert.Equal("password", AuditApi.Text(row, "method"));
    }

    [Fact]
    public async Task A_wrong_password_is_403_wrong_password_counted_with_account_confirm_refused()   // criterion 10
    {
        var (userId, session) = await PasswordAccountAsync();

        using var response = await SettingsApi.Confirm(Client, session, new { password = "Wrong-Passw0rd-9" });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Equal(1, await AttemptsAsync());
        var row = await SingleAsync(AuditKinds.AccountConfirmRefused);
        Assert.Equal(userId, row.SubjectUserId);
        Assert.Equal(["action", "reason"], AuditApi.DetailNames(row));
        Assert.Equal(("wrong_password", "confirm"), (AuditApi.Text(row, "reason"), AuditApi.Text(row, "action")));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
    }

    [Fact]
    public async Task After_ten_failures_it_is_429_too_many_attempts_with_account_confirm_refused_locked()   // criterion 10
    {
        var (_, session) = await PasswordAccountAsync();
        await FailConfirmsAsync(session, 10);
        Clock.Advance(LockoutApi.HumanPace);

        using var locked = await SettingsApi.Confirm(Client, session, new { password = UserPassword });   // even the right one

        await LockoutApi.AssertLockedAsync(locked);
        var rows = await AuditAsync(AuditKinds.AccountConfirmRefused);
        Assert.Equal("locked", AuditApi.Text(rows[^1], "reason"));
        Assert.Equal("confirm", AuditApi.Text(rows[^1], "action"));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));
    }

    [Fact]
    public async Task Failures_at_the_confirmation_and_at_the_login_share_one_streak()   // spec 0012 → Limits: counted as at DELETE /auth/account
    {
        var (_, session) = await PasswordAccountAsync();
        await FailConfirmsAsync(session, 5);
        await LockoutApi.FailAsync(Client, Clock, Ola, 5);

        using var locked = await LoginApi.Login(Client, Ola, UserPassword);

        await LockoutApi.AssertLockedAsync(locked);
    }

    [Fact]
    public async Task A_reauth_ticket_of_a_linked_identity_uses_it_up_clears_auth_pt_and_writes_account_confirmed_with_the_provider()   // criterion 11
    {
        var (userId, session) = await PasswordlessAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await SettingsApi.ConfirmWithTicket(Client, session, ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal(0, await TicketCountAsync());
        Assert.Equal(OpenUntil(), await SettingsApi.ConfirmedUntilAsync(Client, session));
        var row = await SingleAsync(AuditKinds.AccountConfirmed);
        Assert.Equal((userId, "google"), (row.SubjectUserId!.Value, AuditApi.Text(row, "method")));
    }

    [Theory]
    [InlineData("sign_in")]
    [InlineData("expired")]
    [InlineData("used")]
    [InlineData("foreign")]
    [InlineData("no-binding")]
    [InlineData("another-binding")]
    [InlineData("blank")]
    public async Task A_ticket_that_is_not_a_live_reauth_ticket_of_this_account_is_403_reauth_failed_and_is_left_alone(string how)   // criterion 11
    {
        var (_, session) = await PasswordlessAccountAsync();
        var ticket = await TicketAsync(
            "google",
            how == "foreign" ? GooglePerson(email: "someone@gmail.test", subject: "100000000000000000099") : GooglePerson(),
            how == "sign_in" ? "" : "?purpose=reauth");
        var value = how == "blank" ? "" : ticket.Value;
        string? binding = ticket.Binding;
        switch (how)
        {
            case "expired":
                Clock.Advance(ProviderTicketsReauthLifetime() + TimeSpan.FromSeconds(1));
                break;
            case "used":
                using (var first = await SettingsApi.ConfirmWithTicket(Client, session, ticket))
                {
                    Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
                }

                break;
            case "no-binding":
                binding = null;
                break;
            case "another-binding":
                binding = (await TicketAsync("microsoft", PersonalMicrosoft())).Binding;
                break;
        }

        var tickets = await TicketCountAsync();
        using var response = await SettingsApi.Confirm(Client, session, new { provider_ticket = value }, binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(tickets, await TicketCountAsync());   // nothing used up
        var row = (await AuditAsync(AuditKinds.AccountConfirmRefused))[^1];
        Assert.Equal(("reauth_failed", "confirm"), (AuditApi.Text(row, "reason"), AuditApi.Text(row, "action")));
    }

    private static TimeSpan ProviderTicketsReauthLifetime() => Auth.Server.Providers.ProviderTickets.ReauthLifetime;

    [Fact]
    public async Task The_confirmation_must_fit_the_account_before_anything_is_counted()   // criterion 11, Settled 10; check order: fit before lockout
    {
        var (_, withPassword) = await PasswordAccountAsync();
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
        await LockoutApi.FailAsync(Client, Clock, Ola, 10);
        var attempts = await AttemptsAsync();

        using var ticketForPassword = await SettingsApi.ConfirmWithTicket(Client, withPassword, reauth);
        await TenancyApi.AssertErrorAsync(ticketForPassword, HttpStatusCode.Forbidden, "password_required");   // not 429: the fit comes first

        var (_, passwordless) = await PrivateThroughProviderAsync("microsoft", PersonalMicrosoft());
        using var passwordForPasswordless = await SettingsApi.Confirm(Client, passwordless, new { password = "Any-Passw0rd-1" });
        await TenancyApi.AssertErrorAsync(passwordForPasswordless, HttpStatusCode.Forbidden, "reauth_required");

        Assert.Equal(attempts, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task A_ticket_neither_consults_nor_clears_the_lockout()   // criterion 11, spec 0012 → Limits
    {
        var (_, session) = await PasswordlessAccountAsync();
        await LockoutApi.FailAsync(Client, Clock, Ola, 10);   // a password login of an account without one counts as an unknown address
        var attempts = await AttemptsAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await SettingsApi.ConfirmWithTicket(Client, session, ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(attempts, await AttemptsAsync());
    }

    public static TheoryData<string> BadBodies => new()
    {
        "not json",
        "{}",
        """["password"]""",
        """{"password":"Another-Passw0rd","provider_ticket":"x"}""",
        """{"password":""}""",
        """{"password":"   "}""",
        """{"password":null}""",
        """{"password":5}""",
        """{"provider_ticket":5}""",
        """{"provider_ticket":null}""",
        "{\"password\":\"Another" + Backslash + "u0000Passw0rd\"}",   // NUL in the password
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request_before_the_session_and_counts_for_nothing(string body)   // criterion 11, step 1
    {
        var (_, session) = await PasswordAccountAsync();

        using var response = await SettingsApi.PostRaw(Client, SettingsApi.ConfirmPath, session, body);
        using var noCookie = await SettingsApi.PostRaw(Client, SettingsApi.ConfirmPath, session with { RefreshToken = "" }, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.BadRequest, "invalid_request");   // the body is judged first
        Assert.Equal(0, await AttemptsAsync());
    }

    [Fact]
    public async Task Without_the_refresh_cookie_it_is_401_session_required_and_without_a_token_the_plain_401()   // criterion 11
    {
        var (_, session) = await PasswordAccountAsync();

        using var noCookie = await SettingsApi.PostAs(Client, SettingsApi.ConfirmPath, session.AccessToken, null, new { password = UserPassword });
        using var noToken = await SettingsApi.PostAs(Client, SettingsApi.ConfirmPath, null, session.RefreshToken, new { password = UserPassword });

        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.Unauthorized, "session_required");
        await TenancyApi.AssertUnauthorizedAsync(noToken);
        Assert.Equal(0, await AttemptsAsync());   // the session comes before the password
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
    }

    [Fact]
    public async Task An_account_whose_membership_was_removed_is_403_no_membership_and_nothing_is_written()   // criterion 11
    {
        var (userId, session) = await PasswordAccountAsync();
        await RemoveMembershipAsync(userId);

        using var response = await SettingsApi.Confirm(Client, session, new { password = UserPassword });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
    }

    [Fact]
    public async Task A_session_from_before_this_version_gets_its_values_written_by_the_confirmation()   // spec 0012 → Sessions
    {
        var (userId, session) = await PasswordAccountAsync();
        await InDbAsync(db => db.SessionStates.ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        using var response = await SettingsApi.Confirm(Client, session, new { password = UserPassword });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var subject = userId.ToString();
        var created = await InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>().Where(a => a.Subject == subject)
            .Select(a => a.CreationDate).SingleAsync(TestContext.Current.CancellationToken));
        var row = await InDbAsync(db => db.SessionStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(created!.Value, DateTimeKind.Utc)), row.SessionStart);
        Assert.Equal(StorableTime.Now(Clock) + ConfirmationWindow.Lifetime, row.ConfirmedUntil);
    }

    /// <summary>Sends the confirmation while the address lock is held, waits until it is queued behind it, runs <paramref name="change"/>, releases.</summary>
    private async Task<HttpResponseMessage> ConfirmWhileLockedAsync(Func<Task<HttpResponseMessage>> confirm, Func<Task> change)
    {
        await using var held = await HeldLock.AddressAsync(Factory, OlaNormalized);
        var pending = confirm();
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await change();
        await held.ReleaseAsync();
        return await pending;
    }

    [Fact]
    public async Task A_ticket_used_up_while_the_confirmation_waited_for_the_lock_is_403_reauth_failed()   // guard lesson 1
    {
        var (_, session) = await PasswordlessAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await ConfirmWhileLockedAsync(
            () => SettingsApi.ConfirmWithTicket(Client, session, ticket),
            () => InDbAsync(db => db.ProviderTickets.ExecuteDeleteAsync(TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
    }

    [Fact]
    public async Task A_ticket_that_expired_while_the_confirmation_waited_is_refused()   // guard lesson 11: the clock after the locks
    {
        var (_, session) = await PasswordlessAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await ConfirmWhileLockedAsync(
            () => SettingsApi.ConfirmWithTicket(Client, session, ticket),
            () =>
            {
                Clock.Advance(TimeSpan.FromMinutes(5));
                return Task.CompletedTask;
            });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task A_membership_removed_while_the_confirmation_waited_is_403_no_membership()   // spec 0012 → General rules: under the lock
    {
        var (userId, session) = await PasswordAccountAsync();

        using var response = await ConfirmWhileLockedAsync(
            () => SettingsApi.Confirm(Client, session, new { password = UserPassword }),
            () => RemoveMembershipAsync(userId));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
    }

    [Fact]
    public async Task A_password_set_while_a_provider_confirmation_waited_is_403_password_required_and_the_ticket_stays()   // spec 0012 → General rules: the fit, again under the lock
    {
        var (userId, session) = await PasswordlessAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await ConfirmWhileLockedAsync(
            () => SettingsApi.ConfirmWithTicket(Client, session, ticket),
            () => InDbAsync(db => db.Users.Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, "set-meanwhile"), TestContext.Current.CancellationToken)));

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "password_required");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, session));
    }

    [Fact]
    public async Task A_session_ended_while_the_confirmation_waited_is_401_session_required_and_opens_nothing()   // under the lock
    {
        var (_, session) = await PasswordAccountAsync();

        using var response = await ConfirmWhileLockedAsync(
            () => SettingsApi.Confirm(Client, session, new { password = UserPassword }),
            async () =>
            {
                using var logout = await SessionApi.Logout(Client, session.RefreshToken);
                Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "session_required");
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Null((await InDbAsync(db => db.SessionStates.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken))).ConfirmedUntil
            is { } until && until > StorableTime.Now(Clock) ? (DateTimeOffset?)until : null);
    }

    [Fact]
    public async Task No_password_ticket_binding_or_cookie_reaches_the_audit_log_or_the_log()   // guard lesson 4
    {
        var (_, session) = await PasswordAccountAsync();
        using (var wrong = await SettingsApi.Confirm(Client, session, new { password = "Wrong-Passw0rd-9" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        }

        using (var right = await SettingsApi.Confirm(Client, session, new { password = UserPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        }

        var (_, passwordless) = await PrivateThroughProviderAsync("microsoft", PersonalMicrosoft());
        var ticket = await TicketAsync("microsoft", PersonalMicrosoft(), "?purpose=reauth");
        using (var confirmed = await SettingsApi.ConfirmWithTicket(Client, passwordless, ticket))
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        string[] secrets = ["Wrong-Passw0rd-9", UserPassword, ticket.Value, ticket.Binding, session.RefreshToken, passwordless.RefreshToken, session.AccessToken];
        await AssertNoSecretsAsync(secrets);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal));
    }
}
```

In `tests/Auth.IntegrationTests/ConfirmationWindowTests.cs`, add before the class's last closing brace:

```csharp

    [Fact]
    public async Task A_confirmation_in_one_session_leaves_another_session_unconfirmed()   // criterion 9, Review Focus 5
    {
        var here = await SignInAsync("login");
        var there = await SessionApi.LoginAsync(Client, "login@firma.test", UserPassword);
        Clock.Advance(TimeSpan.FromMinutes(6));

        using var confirmed = await SettingsApi.Confirm(Client, here, new { password = UserPassword });

        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.Equal(OpenUntil(), await SettingsApi.ConfirmedUntilAsync(Client, here));
        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, there));
    }
```

In `tests/Auth.IntegrationTests/ProviderErrorCodesTests.cs`, in the theory `The_codes_and_their_statuses_are_those_of_the_contract`, after the row
of `SessionRequired` add

```csharp
    [InlineData(TenancyErrors.ConfirmationRequired, "confirmation_required", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.ProviderInUse, "provider_in_use", StatusCodes.Status409Conflict)]
    [InlineData(TenancyErrors.LastLogin, "last_login", StatusCodes.Status409Conflict)]
    [InlineData(TenancyErrors.PasswordNotSet, "password_not_set", StatusCodes.Status403Forbidden)]
```

and change the class summary to `/// <summary>The error codes of specs 0011 and 0012 and their statuses.</summary>`.

In `tests/Auth.IntegrationTests/AuditTableTests.cs`, replace

```csharp
    public void The_kinds_are_the_twenty_eight_of_the_contract()   // spec 0011, criterion 28
```

with

```csharp
    public void The_kinds_are_the_thirty_two_of_the_contract()   // spec 0012, criterion 25
```

and in it replace

```csharp
                "provider.linked", "provider.reauthenticated",
            ],
```

with

```csharp
                "provider.linked", "provider.reauthenticated",
                "account.confirmed", "account.confirm_refused", "provider.unlinked", "password.changed",
            ],
```

In `tests/Auth.IntegrationTests/AuditProviderFlowsTests.cs`, replace

```csharp
    public void The_audit_log_knows_twenty_eight_kinds()   // criterion 28
    {
        Assert.Equal(28, AuditKinds.All.Count);
```

with

```csharp
    public void The_audit_log_knows_thirty_two_kinds()   // spec 0011, criterion 28; spec 0012, criterion 25
    {
        Assert.Equal(32, AuditKinds.All.Count);
```

In `tests/Auth.IntegrationTests/OpenApiTests.cs`:

(a) in `Endpoints` (the list of `Every_route_of_the_service_is_in_the_description` and
`The_description_names_every_endpoint_of_the_service_and_nothing_else`), replace `        "DELETE /auth/account",` with
`        "DELETE /auth/account", "POST /auth/account/confirm",`;

(b) add the field after `CookiesRead`:

```csharp

    // Spec 0012: the endpoints of the settings screen, behind the access token.
    private static readonly string[] SettingsEndpoints =
        ["POST /auth/account/confirm", "POST /auth/providers/connect", "POST /auth/account/logins/unlink", "POST /auth/account/password"];
```

(c) in `CookiesRead`, after `        ["DELETE /auth/account"] = ["auth_pt"],` add `        ["POST /auth/account/confirm"] = ["auth_pt", "auth_rt"],`;

(d) in the theory `Each_endpoint_describes_the_statuses_of_the_contract`, after `    [InlineData("DELETE /auth/account", "204,400,401,403,415,429")]` add `    [InlineData("POST /auth/account/confirm", "204,400,401,403,415,429")]`;

(e) in the theory `Each_endpoint_that_reads_a_body_describes_it_in_snake_case`, after `    [InlineData("DELETE /auth/account", "password,provider_ticket")]` add `    [InlineData("POST /auth/account/confirm", "password,provider_ticket")]`;

(f) in `The_company_endpoints_ask_for_a_bearer_token_and_the_public_ones_do_not`, replace
`            var guarded = endpoint.Split(' ')[1].StartsWith("/auth/org", StringComparison.Ordinal) || endpoint.EndsWith("/auth/me", StringComparison.Ordinal) || endpoint == "DELETE /auth/account";`
with
`            var guarded = endpoint.Split(' ')[1].StartsWith("/auth/org", StringComparison.Ordinal) || endpoint.EndsWith("/auth/me", StringComparison.Ordinal) || endpoint == "DELETE /auth/account" || SettingsEndpoints.Contains(endpoint);`;

(g) in `No_other_answer_says_it_sets_a_cookie`, after `"POST /auth/providers/finish 200"` add `, "POST /auth/account/confirm 204"`;

(h) at the end of `The_error_codes_of_the_contract_are_in_the_descriptions` add

```csharp
        Assert.Contains("session_required", Description(operations["POST /auth/account/confirm"], "401"));   // spec 0012
        Assert.All(
            ["no_membership", "password_required", "reauth_required", "wrong_password", "reauth_failed"],
            code => Assert.Contains(code, Description(operations["POST /auth/account/confirm"], "403")));
        Assert.Contains("too_many_attempts", Description(operations["POST /auth/account/confirm"], "429"));
```

In `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`, in `Every_operation_can_say_429_too_many_requests_with_retry_after_the_two_added_by_hand_included`
replace `        Assert.Equal(36, operations.Count);   // spec 0011: the five provider operations`
with `        Assert.Equal(37, operations.Count);   // spec 0012: the confirmation`, and in
`Exactly_the_operations_the_charset_guard_covers_say_415_unsupported_media_type` `        Assert.Equal(24, covered);   // spec 0011: the exchange, the link and the finish`
with `        Assert.Equal(25, covered);   // spec 0012: the confirmation`.

- [ ] **Step 2: See them fail.** Build: it fails (`AuditKinds.AccountConfirmed` and the codes do not exist); once Step 3 compiles,
`AccountConfirmTests` fail with `404` (no route).

- [ ] **Step 3: The codes and the kinds.** In `src/Auth.Server/Tenancy/Outcome.cs`, replace

```csharp
    // Spec 0012.
    public const string SessionRequired = "session_required";

    public static int StatusOf(string error) => error switch
    {
        SessionRequired => StatusCodes.Status401Unauthorized,
        InvalidRequest or InvalidToken or WeakPassword or UnknownPermission or InvalidTicket => StatusCodes.Status400BadRequest,
        Forbidden or PermissionsChanged or PermissionNotHeld or WrongPassword or SignupDisabled
            or NoEmail or NoAccount or AddressMismatch or PasswordRequired or ReauthRequired or ReauthFailed or EmailNotVerified or NoMembership
            => StatusCodes.Status403Forbidden,
        NotFound => StatusCodes.Status404NotFound,
        UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
        AlreadyInOrg or AlreadyMember or InvitePending or LastManager or CannotChangeSelf or RoleInUse or RoleNameTaken or AlreadyRegistered
            or PrivateAccountExists or ProviderAlreadyLinked => StatusCodes.Status409Conflict,
```

with

```csharp
    // Spec 0012.
    public const string SessionRequired = "session_required";
    public const string ConfirmationRequired = "confirmation_required";
    public const string ProviderInUse = "provider_in_use";
    public const string LastLogin = "last_login";
    public const string PasswordNotSet = "password_not_set";

    public static int StatusOf(string error) => error switch
    {
        SessionRequired => StatusCodes.Status401Unauthorized,
        InvalidRequest or InvalidToken or WeakPassword or UnknownPermission or InvalidTicket => StatusCodes.Status400BadRequest,
        Forbidden or PermissionsChanged or PermissionNotHeld or WrongPassword or SignupDisabled
            or NoEmail or NoAccount or AddressMismatch or PasswordRequired or ReauthRequired or ReauthFailed or EmailNotVerified or NoMembership
            or ConfirmationRequired or PasswordNotSet
            => StatusCodes.Status403Forbidden,
        NotFound => StatusCodes.Status404NotFound,
        UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
        AlreadyInOrg or AlreadyMember or InvitePending or LastManager or CannotChangeSelf or RoleInUse or RoleNameTaken or AlreadyRegistered
            or PrivateAccountExists or ProviderAlreadyLinked or ProviderInUse or LastLogin => StatusCodes.Status409Conflict,
```

In `src/Auth.Server/Audit/AuditKinds.cs`, replace

```csharp
    public const string ProviderReauthenticated = "provider.reauthenticated";
```

with

```csharp
    public const string ProviderReauthenticated = "provider.reauthenticated";

    // Spec 0012 → Audit events.
    public const string AccountConfirmed = "account.confirmed";
    public const string AccountConfirmRefused = "account.confirm_refused";
    public const string ProviderUnlinked = "provider.unlinked";
    public const string PasswordChanged = "password.changed";
```

replace

```csharp
        ProviderLinked, ProviderReauthenticated,
    ];
```

with

```csharp
        ProviderLinked, ProviderReauthenticated,
        AccountConfirmed, AccountConfirmRefused, ProviderUnlinked, PasswordChanged,
    ];
```

and replace `/// <summary>The kinds of the audit log (specs 0008 to 0011 → Audit log). The names are the contract's; the runbook queries them.</summary>`
with `/// <summary>The kinds of the audit log (specs 0008 to 0012 → Audit log). The names are the contract's; the runbook queries them.</summary>`.

- [ ] **Step 4: The window opened.** In `src/Auth.Server/Sessions/DeviceSessions.cs`, replace

```csharp
    /// <summary>Ends the window of the session (the testing endpoint); a session without a row has none to end.</summary>
```

with

```csharp
    /// <summary>
    /// Opens the window of the session until <paramref name="until"/>, in the caller's transaction (spec 0012 → <c>POST /auth/account/confirm</c>, step
    /// 6). A session from before this version gets its row now, with the start it has (its authorization's creation).
    /// </summary>
    public static Task<int> ConfirmAsync(AuthDbContext db, DeviceSession session, DateTimeOffset until, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(session);

        return db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "SessionStates" ("AuthorizationId", "SessionStart", "ConfirmedUntil")
            VALUES ({session.AuthorizationId}, {session.Start}, {until})
            ON CONFLICT ("AuthorizationId") DO UPDATE SET "ConfirmedUntil" = EXCLUDED."ConfirmedUntil"
            """,
            cancellationToken);
    }

    /// <summary>Ends the window of the session (the testing endpoint); a session without a row has none to end.</summary>
```

- [ ] **Step 5: The caller of a settings endpoint.** `src/Auth.Server/Account/SettingsCallers.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Account;

/// <summary>The caller of a settings endpoint (spec 0012 → General rules): the account, its space and the session of this device, as read now.</summary>
internal sealed record SettingsCaller(
    Guid UserId, string Email, string NormalizedEmail, string? PasswordHash, TenantContext Tenant, DeviceSession Session);

/// <summary>The caller, or the answer that refuses them.</summary>
internal readonly record struct SettingsRead(SettingsCaller? Caller, IResult? Refusal);

/// <summary>
/// Step 2 of the four settings endpoints, in one place: the session of this device (<c>401 session_required</c>, after the access token's own
/// <c>401</c>), the account (an account deleted meanwhile has no session), and its membership (<c>403 no_membership</c>, nothing written). The
/// endpoints read it first, then again inside their transaction under the address lock, where its answer is the one that counts.
/// </summary>
internal static class SettingsCallers
{
    public const string ConfirmAction = "confirm";
    public const string PasswordAction = "password";

    public static async Task<SettingsRead> ReadAsync(
        HttpContext http, AuthDbContext db, IOpenIddictTokenManager tokens, MembershipReader memberships, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // The sub of a token this service issued is always a user id; without one there is no session of it either.
        if (!Guid.TryParse(http.User.FindFirst(Claims.Subject)?.Value, out var userId))
        {
            return Refused(TenancyErrors.SessionRequired);
        }

        var session = await DeviceSessions.FindAsync(http, db, tokens, userId, StorableTime.Now(clock), cancellationToken);
        return session is null ? Refused(TenancyErrors.SessionRequired) : await CallerOfAsync(db, memberships, userId, session, cancellationToken);
    }

    /// <summary>The same checks under the caller's lock: the session by its token, read afresh, the account and the membership.</summary>
    public static async Task<SettingsRead> ReadAgainAsync(AuthDbContext db, MembershipReader memberships, SettingsCaller caller, DateTimeOffset now)
    {
        var session = await DeviceSessions.ReadAsync(db, caller.Session.TokenId, caller.UserId, now, CancellationToken.None);
        return session is null
            ? Refused(TenancyErrors.SessionRequired)
            : await CallerOfAsync(db, memberships, caller.UserId, session, CancellationToken.None);
    }

    /// <summary>
    /// Whether the password is the account's. The hash is only verified: <c>UserManager.CheckPasswordAsync</c> would also store a new hash when the
    /// old one is out of date, an update of a row that a change under the lock is about to write.
    /// </summary>
    public static bool PasswordIsRight(UserManager<ApplicationUser> users, string hash, string password) =>
        users.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), hash, password) != PasswordVerificationResult.Failed;

    /// <summary>An audit row of the caller's own change: the caller is actor and subject, in their space.</summary>
    public static AuditEntry Entry(string kind, SettingsCaller caller, IReadOnlyDictionary<string, object?>? details = null) => new()
    {
        Kind = kind,
        ActorUserId = caller.UserId,
        SubjectUserId = caller.UserId,
        SubjectEmail = caller.Email,
        OrgId = caller.Tenant.CompanyId,
        OrgName = caller.Tenant.CompanyName,
        Details = details,
    };

    /// <summary><c>account.confirm_refused</c> (spec 0012 → Audit events), written alone: the refusal changes nothing else.</summary>
    public static Task RefuseAsync(AuditLog audit, SettingsCaller caller, string reason, string action) =>
        audit.WriteAloneAsync(Entry(
            AuditKinds.AccountConfirmRefused, caller, new Dictionary<string, object?> { ["reason"] = reason, ["action"] = action }));

    private static async Task<SettingsRead> CallerOfAsync(
        AuthDbContext db, MembershipReader memberships, Guid userId, DeviceSession session, CancellationToken cancellationToken)
    {
        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.NormalizedEmail, u.PasswordHash })
            .FirstOrDefaultAsync(cancellationToken);
        if (account is not { Email: { } email, NormalizedEmail: { } normalized })
        {
            return Refused(TenancyErrors.SessionRequired);
        }

        return await memberships.ReadAsync(userId, cancellationToken) is { } tenant
            ? new SettingsRead(new SettingsCaller(userId, email, normalized, account.PasswordHash, tenant, session), null)
            : Refused(TenancyErrors.NoMembership);
    }

    private static SettingsRead Refused(string code) => new(null, ApiResults.Error(code));
}
```

- [ ] **Step 6: The confirmation.** `src/Auth.Server/Account/AccountConfirmEndpoint.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Providers;
using Auth.Server.Requests;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;

namespace Auth.Server.Account;

/// <summary>
/// <c>POST /auth/account/confirm</c> (spec 0012): the person confirms who they are on this device, with their password or, without one, a
/// <c>reauth</c> ticket of a linked identity, and the session of this device is confirmed for five minutes. The checks run in the order of the
/// contract, which mirrors <c>DELETE /auth/account</c>: the body; the session and the membership; the fit (<c>password_required</c>,
/// <c>reauth_required</c>, nothing counted); the password, counted first in the account's login streak, or the ticket, never counted; then, in one
/// transaction under the address lock where the session and the membership are read again, the window, the ticket used up and
/// <c>account.confirmed</c>.
/// </summary>
public static class AccountConfirmEndpoint
{
    public const string Path = "/auth/account/confirm";

    private const string PasswordField = "password";
    private const string TicketField = "provider_ticket";

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, LoginStreakStore streaks,
        IOpenIddictTokenManager tokens, AuditLog audit, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(streaks);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);

        // 1. Exactly one of the two: a password that is a non-blank string with no NUL, or a ticket that is a string (a blank one is no live ticket).
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields || fields.Has(PasswordField) == fields.Has(TicketField))
        {
            return ApiResults.InvalidRequest();
        }

        string? password = null;
        string? ticketText = null;
        var readable = fields.Has(PasswordField)
            ? fields.TryString(PasswordField, out password) && !password.Contains('\0', StringComparison.Ordinal)
            : fields.TryAnyString(TicketField, out ticketText);
        if (!readable)
        {
            return ApiResults.InvalidRequest();
        }

        // 2.
        var read = await SettingsCallers.ReadAsync(http, db, tokens, memberships, clock, http.RequestAborted);
        if (read.Caller is not { } caller)
        {
            return read.Refusal!;
        }

        // 3. The confirmation must fit the account; nothing is counted and no row is written.
        if (caller.PasswordHash is not null && ticketText is not null)
        {
            return ApiResults.Error(TenancyErrors.PasswordRequired);
        }

        if (caller.PasswordHash is null && password is not null)
        {
            return ApiResults.Error(TenancyErrors.ReauthRequired);
        }

        string method;
        string? binding = null;
        if (password is not null)
        {
            // 4. As login judges it: counted first, in the streak of the account's address.
            var identifier = LoginIdentifier.HashOf(caller.NormalizedEmail);
            var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
            if (!decision.Allowed)
            {
                await SettingsCallers.RefuseAsync(audit, caller, "locked", SettingsCallers.ConfirmAction);
                return new TooManyAttemptsResult(decision.RetryAfter);
            }

            if (!SettingsCallers.PasswordIsRight(users, caller.PasswordHash!, password))
            {
                await SettingsCallers.RefuseAsync(audit, caller, "wrong_password", SettingsCallers.ConfirmAction);
                return ApiResults.Error(TenancyErrors.WrongPassword);
            }

            // Not the request's token: a client that goes away after its right password must not be left with a cooldown.
            await streaks.ClearAsync(identifier, CancellationToken.None);
            method = "password";
        }
        else
        {
            // 5. A live reauth ticket of this browser whose identity is a login of this account. Nothing is used up; the lockout is not consulted.
            ProviderBindingCookie.TryRead(http.Request, out binding);
            var ticket = await ProviderTickets.FindAsync(db, ticketText!, binding, StorableTime.Now(clock), http.RequestAborted);
            if (ticket is not { Purpose: ProviderTicketPurpose.Reauth }
                || !await ProviderLogins.IsLoginOfAsync(db, caller.UserId, ticket.Provider, ticket.IdentityKey, http.RequestAborted))
            {
                await SettingsCallers.RefuseAsync(audit, caller, "reauth_failed", SettingsCallers.ConfirmAction);
                return ApiResults.Error(TenancyErrors.ReauthFailed);
            }

            method = ticket.Provider;
        }

        // 6. Not the request's token from here on: a client that goes away must not leave the window half opened.
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        await AddressLock.AcquireAsync(db, caller.NormalizedEmail, CancellationToken.None);
        var now = StorableTime.Now(clock);   // after the lock: a ticket that ran out while this request waited is refused
        var again = await SettingsCallers.ReadAgainAsync(db, memberships, caller, now);
        if (again.Caller is not { } current)
        {
            return again.Refusal!;
        }

        // 3, again: the fit of the confirmation to the password that the account has now. A reset or a change of the hash ends the session first, which
        // the re-read above answers; this keeps the rule of the spec literal, and a refusal here writes nothing and uses nothing up.
        if (current.PasswordHash is not null && ticketText is not null)
        {
            return ApiResults.Error(TenancyErrors.PasswordRequired);
        }

        if (current.PasswordHash is null && password is not null)
        {
            return ApiResults.Error(TenancyErrors.ReauthRequired);
        }

        if (ticketText is not null)
        {
            var used = await ProviderTickets.ConsumeAsync(db, ticketText, binding!, ProviderTicketPurpose.Reauth, now, CancellationToken.None);
            if (used is null || !await ProviderLogins.IsLoginOfAsync(db, current.UserId, used.Provider, used.IdentityKey, CancellationToken.None))
            {
                await transaction.DisposeAsync();
                await SettingsCallers.RefuseAsync(audit, current, "reauth_failed", SettingsCallers.ConfirmAction);
                return ApiResults.Error(TenancyErrors.ReauthFailed);
            }
        }

        await DeviceSessions.ConfirmAsync(db, current.Session, now + ConfirmationWindow.Lifetime, CancellationToken.None);
        audit.Stage(SettingsCallers.Entry(AuditKinds.AccountConfirmed, current, new Dictionary<string, object?> { ["method"] = method }));
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        if (ticketText is not null)
        {
            ProviderBindingCookie.Clear(http.Response);
        }

        return ApiResults.NoContent();
    }
}
```

In `src/Auth.Server/Api/AccountContracts.cs`, add at the end:

```csharp

/// <summary>Request of <c>POST /auth/account/confirm</c> (spec 0012): exactly one of the password and a <c>reauth</c> ticket of a provider.</summary>
public sealed record ConfirmRequest(string? Password = null, string? ProviderTicket = null);
```

`src/Auth.Server/Api/SettingsRoutes.cs`:

```csharp
using Auth.Server.Account;
using Auth.Server.Providers;
using Auth.Server.Tenancy;

namespace Auth.Server.Api;

/// <summary>The endpoints of spec 0012's settings screen, behind the access token and the refresh cookie of the session of this device.</summary>
public static class SettingsRoutes
{
    internal const string RefreshCookieRead =
        "The session of this device: the refresh token of the cookie, read and never redeemed. Missing, or of no live session of the token's "
        + "account: `401 session_required` (a missing or invalid access token is the plain `401` before it).";

    public static IEndpointRouteBuilder MapSettingsApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(AccountConfirmEndpoint.Path, AccountConfirmEndpoint.HandleAsync)
            .RequireAuthorization()
            .WithTags("Account")
            .WithDescription(
                "Confirms who the caller is on this device: the session of the refresh cookie is confirmed for five minutes, so that connecting "
                + "and disconnecting a provider act without asking again (`GET /auth/me` says `confirmed_until`). The body holds exactly one of "
                + "`password` and `provider_ticket` (both or neither: `400 invalid_request`): an account with a password needs it (`403 "
                + "password_required` for a ticket), an account without one confirms with a provider, by a ticket of a round trip with "
                + "`purpose=reauth` and the browser's `auth_pt` (`403 reauth_required` for a password); neither is counted. The password counts in "
                + "the login streak as a failed login does (`403 wrong_password`; while the identifier is locked `429 too_many_attempts`); a "
                + "ticket that is unknown, used, expired (5 minutes), of another purpose, without its `auth_pt` or of an identity that is not a "
                + "login of this account is `403 reauth_failed`, and nothing is used up. An account with no membership is `403 no_membership`.")
            .ReadsJson<ConfirmRequest>()
            .ReadsRefreshCookie(RefreshCookieRead)
            .ReadsCookie(ProviderBindingCookie.Name, "With `provider_ticket`: the binding of the ticket, set by the callback that made it.")
            .Produces(StatusCodes.Status204NoContent)
            .SetsHeader(StatusCodes.Status204NoContent, "Set-Cookie", $"With `provider_ticket`: `{ProviderBindingCookie.Name}` emptied, with `Max-Age=0`.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status401Unauthorized, TenancyErrors.SessionRequired)
            .ProducesError(
                StatusCodes.Status403Forbidden, TenancyErrors.NoMembership, TenancyErrors.PasswordRequired, TenancyErrors.ReauthRequired,
                TenancyErrors.WrongPassword, TenancyErrors.ReauthFailed)
            .ProducesTooManyAttempts();

        return app;
    }
}
```

In `src/Auth.Server/Program.cs`, replace

```csharp
app.MapProviderApi();
if (testingSettings.WindowEndpoint)
```

with

```csharp
app.MapProviderApi();
app.MapSettingsApi();
if (testingSettings.WindowEndpoint)
```

- [ ] **Step 7: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.AccountConfirmTests -class Auth.IntegrationTests.ConfirmationWindowTests -class Auth.IntegrationTests.ProviderErrorCodesTests -class Auth.IntegrationTests.AuditTableTests -class Auth.IntegrationTests.AuditProviderFlowsTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests -class Auth.IntegrationTests.JsonCharsetTests`
passes. Mutate and see a test fail, then restore: swap steps 3 and 4 (`The_confirmation_must_fit_the_account_before_anything_is_counted` fails);
read the clock before the lock (`A_ticket_that_expired_while_the_confirmation_waited_is_refused` fails); skip the re-read under the lock
(`A_session_ended_while_the_confirmation_waited_is_401_session_required_and_opens_nothing` fails); skip the fit check under the lock
(`A_password_set_while_a_provider_confirmation_waited_is_403_password_required_and_the_ticket_stays` fails).

- [ ] **Step 8: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): POST /auth/account/confirm opens the confirmation window of this device`.

---

### Task 8: The notice mails; the link's `provider_in_use`, mail and `via`

**Files:**
- Create: `src/Auth.Server/Email/MailNotices.cs`
- Modify: `src/Auth.Server/Email/MailLimitPolicy.cs`, `src/Auth.Server/Email/MailLimits.cs`, `src/Auth.Server/Email/MailDispatcher.cs`,
  `src/Auth.Server/Email/MailComposer.cs`, `src/Auth.Server/Email/MailTexts.cs`, `src/Auth.Server/Providers/ProviderLogins.cs`,
  `src/Auth.Server/Providers/ProviderSignIn.cs`, `src/Auth.Server/Providers/ProviderLinkEndpoint.cs`, `src/Auth.Server/Api/ProviderRoutes.cs`,
  `src/Auth.Server/Program.cs`
- Create (tests): `tests/Auth.IntegrationTests/SettingsMailTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/MailComposerTests.cs`, `tests/Auth.IntegrationTests/MailLimitPolicyTests.cs`,
  `tests/Auth.IntegrationTests/ProviderLinkTests.cs`, `tests/Auth.IntegrationTests/AuditProviderFlowsTests.cs`,
  `tests/Auth.IntegrationTests/ProviderPasswordResetTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`

**Interfaces:**
- Consumes: `MailKind.ProviderLinked`/`ProviderUnlinked`/`PasswordChanged`/`NoticeLimit`, `MailRequest.Provider` (Task 1), `MailDispatchSignal`,
  `LoginIdentifier`, `ProviderNames.DisplayNameOf` (Task 3 adds `GitHub`), `TenancyErrors.ProviderInUse` (Task 7).
- Produces: `MailLimitRule(TimeSpan MinimumGap, int WindowLimit, TimeSpan Window)` with `MailLimitRule.Requests`;
  `MailLimitPolicy.Register(MailLimitState, DateTimeOffset, MailLimitRule)`; `MailLimits.RegisterAsync(..., MailLimitRule? rule = null)`;
  `MailNotices` (singleton): `MailNotices.Rule`, `QueueAsync(AuthDbContext db, MailKind kind, string normalizedEmail, string? provider,
  DateTimeOffset now, CancellationToken cancellationToken)` (returns whether a row was added), `Notify()`; `MailComposer.ComposeNotice(MailKind
  kind, string recipient, string? providerDisplayName)`; `MailTexts.Notice(string locale, MailKind kind)` and `NoticeText`;
  `ProviderLogins.HasLoginOfProviderAsync(AuthDbContext, Guid userId, string provider, CancellationToken)`;
  `ProviderSignIn.Session(..., Func<Task<IResult>>? whenGone = null, Action<HttpContext>? alsoCommitted = null)`.

**Contract counts:** `OpenApiTests`: `POST /auth/providers/link` statuses `200,400,401,403,409,415,429` and `provider_in_use` in its `409`.
`AuditProviderFlowsTests`: `provider.linked` details `provider`, `via`.

**Decisions the plan takes here:**
- The notice limit reuses spec 0004's store and rule engine: `MailLimitPolicy` takes a `MailLimitRule` (spec 0004's is
  `MailLimitRule.Requests`: 60 seconds, 5, one hour; the notices' is no gap, 10, one hour) and the key is `(hash of the address,
  MailKind.NoticeLimit)`. The pure function stays one function, and `MailLimitPolicyTests` keep passing unchanged.
- `MailNotices.QueueAsync` works on the caller's context inside the caller's transaction (the limit row locked by `MailLimits`' one
  statement, the row added to the context), and the caller saves; `Notify()` runs after the commit. Beyond the limit: no row, one warning
  naming the kind (never the address), and the change goes on.
- The three kinds join the reset's branch of both dispatcher statements: `r."Kind" IN (reset, linked, unlinked, changed)` where the statements
  say `r."Kind" = {passwordReset}`. A row of a provider notice without a provider is dropped (it is never written, but the column is nullable).
- The link's `provider_in_use` comes after step 7 (the streak is cleared by then; no audit row) and again under the lock after the membership
  re-read. The exchange is unchanged: it still answers `next` `link` (the contract says so).

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/SettingsMailTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0012 → Mails (criterion 22): the notices of a change to how an account signs in, written in the change's transaction, limited on their
/// own, sent by the dispatcher (run by hand here) to an account of the address whatever its confirmation, with no link and no token.
/// </summary>
public sealed class SettingsMailTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string OlaNormalized = "OLA@GMAIL.TEST";
    private const string LimitWarning = "notice limit";

    private static readonly MailKind[] NoticeKinds = [MailKind.ProviderLinked, MailKind.ProviderUnlinked, MailKind.PasswordChanged];

    private Task<List<MailRequest>> NoticesAsync() =>
        InDbAsync(db => db.MailRequests.AsNoTracking().Where(r => NoticeKinds.Contains(r.Kind)).OrderBy(r => r.Id)
            .ToListAsync(TestContext.Current.CancellationToken));

    private Task<MailRequestLimit?> LimitAsync(MailKind key) =>
        InDbAsync(db => db.MailRequestLimits.AsNoTracking()
            .SingleOrDefaultAsync(l => l.IdentifierHash == LoginIdentifier.HashOf(OlaNormalized) && l.Kind == key, TestContext.Current.CancellationToken));

    private int LimitWarnings() =>
        Logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains(LimitWarning, StringComparison.Ordinal));

    /// <summary>A notice queued as the endpoints queue it: in a transaction of the caller's, saved and committed, then the dispatcher woken.</summary>
    private async Task<bool> QueueAsync(MailKind kind, string normalizedEmail = OlaNormalized, string? provider = "google")
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var notices = Factory.Services.GetRequiredService<MailNotices>();
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var queued = await notices.QueueAsync(db, kind, normalizedEmail, provider, StorableTime.Now(Clock), TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        return queued;
    }

    private static void AssertNoLink(ComposedMail mail)
    {
        foreach (var body in new[] { mail.TextBody, mail.HtmlBody })
        {
            Assert.DoesNotContain("http", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<a ", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_link_queues_provider_connected_in_its_transaction_and_the_mail_names_the_provider_with_no_link()   // criterion 22
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using (var linked = await ProviderApi.Link(Client, ticket, UserPassword))
        {
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        }

        var row = Assert.Single(await NoticesAsync());
        Assert.Equal((MailKind.ProviderLinked, OlaNormalized, "google"), (row.Kind, row.NormalizedEmail, row.Provider));
        Assert.Equal(row.RequestedAt, (await SingleAsync(AuditKinds.ProviderLinked)).OccurredAt);   // one transaction, one clock reading
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == user.Id, TestContext.Current.CancellationToken)));

        await DispatchAsync();

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Ola, mail.To);
        Assert.Contains("Google", mail.Subject, StringComparison.Ordinal);
        Assert.Contains(Ola, mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("Forgot your password?", mail.TextBody, StringComparison.Ordinal);
        AssertNoLink(mail);
        Assert.Empty(await NoticesAsync());
    }

    [Fact]
    public async Task A_refused_link_queues_nothing()   // criterion 22: in the change's transaction, or not at all
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using var wrong = await ProviderApi.Link(Client, ticket, "Wrong-Passw0rd-9");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Empty(await NoticesAsync());
        Assert.Null(await LimitAsync(MailKind.NoticeLimit));
    }

    [Fact]
    public async Task The_notices_are_not_counted_by_the_limit_of_spec_0004_and_do_not_count_against_it()   // criterion 22
    {
        await CreateUserAsync(Ola, confirmed: true);
        using (var forgot = await AccountApi.Forgot(Client, Ola))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        Assert.True(await QueueAsync(MailKind.ProviderLinked));
        Assert.True(await QueueAsync(MailKind.PasswordChanged, provider: null));

        Assert.Equal(1, (await LimitAsync(MailKind.PasswordReset))!.WindowCount);
        Assert.Equal(2, (await LimitAsync(MailKind.NoticeLimit))!.WindowCount);
        Clock.Advance(PastTheLimit);
        using var again = await AccountApi.Forgot(Client, Ola);
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal(2, (await LimitAsync(MailKind.PasswordReset))!.WindowCount);
    }

    [Fact]
    public async Task The_eleventh_notice_in_an_hour_is_not_queued_the_change_still_happens_and_one_warning_names_the_kind()   // criterion 22
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        for (var i = 0; i < MailNotices.Rule.WindowLimit; i++)
        {
            Assert.True(await QueueAsync(i % 2 == 0 ? MailKind.ProviderUnlinked : MailKind.PasswordChanged, provider: i % 2 == 0 ? "github" : null));
        }

        var ticket = await TicketAsync("google", GooglePerson());
        using (var linked = await ProviderApi.Link(Client, ticket, UserPassword))
        {
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);   // the change happens
        }

        Assert.Single(await InDbAsync(db => db.UserLogins.Where(l => l.UserId == user.Id).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(10, (await NoticesAsync()).Count);
        Assert.DoesNotContain(await NoticesAsync(), r => r.Kind == MailKind.ProviderLinked);
        Assert.Equal(1, LimitWarnings());
        var warning = Logs.Entries.Single(e => e.Message.Contains(LimitWarning, StringComparison.Ordinal)).Message;
        Assert.Contains(nameof(MailKind.ProviderLinked), warning, StringComparison.Ordinal);
        Assert.DoesNotContain(Ola, warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_window_is_fixed_an_hour_from_the_first_notice_with_no_gap_between_two()   // Amendment A10
    {
        await CreateUserAsync(Ola, confirmed: true);
        Assert.True(await QueueAsync(MailKind.PasswordChanged, provider: null));
        Clock.Advance(TimeSpan.FromMinutes(30));
        for (var i = 1; i < MailNotices.Rule.WindowLimit; i++)
        {
            Assert.True(await QueueAsync(MailKind.PasswordChanged, provider: null));   // no minimum gap
        }

        Assert.False(await QueueAsync(MailKind.PasswordChanged, provider: null));
        Clock.Advance(TimeSpan.FromMinutes(30));   // an hour after the first: a new window
        Assert.True(await QueueAsync(MailKind.PasswordChanged, provider: null));
        Assert.Equal(1, (await LimitAsync(MailKind.NoticeLimit))!.WindowCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_notice_goes_to_an_account_of_the_address_confirmed_or_not_and_never_as_a_verification(bool confirmed)   // criterion 22
    {
        var user = await CreateUserAsync(Ola, confirmed);
        Assert.True(await QueueAsync(MailKind.PasswordChanged, provider: null));

        Assert.Equal(1, await DispatchAsync());

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal(Ola, mail.To);
        AssertNoLink(mail);
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == user.Id, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_notice_whose_account_was_deleted_before_dispatch_is_dropped()   // criterion 22
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        Assert.True(await QueueAsync(MailKind.ProviderUnlinked, provider: "github"));
        await InDbAsync(db => db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken));

        await DispatchAsync();

        Assert.Empty(Mail.Sent);
        Assert.Empty(await NoticesAsync());
    }

    [Fact]
    public async Task A_notice_is_mailed_to_the_address_as_the_account_stores_it_and_names_the_display_name()   // spec 0012 → Mails
    {
        await CreateUserAsync("Ola.Nowak@Firma.test", confirmed: true);
        Assert.True(await QueueAsync(MailKind.ProviderUnlinked, "OLA.NOWAK@FIRMA.TEST", "github"));

        await DispatchAsync();

        var mail = Assert.Single(Mail.Sent);
        Assert.Equal("Ola.Nowak@Firma.test", mail.To);
        Assert.Contains("GitHub", mail.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("github", mail.TextBody, StringComparison.Ordinal);   // the display name, never the provider name
    }

    [Fact]
    public async Task A_finish_an_invitation_through_a_provider_and_a_reset_queue_no_notice()   // criterion 22
    {
        await PrivateThroughProviderAsync("google", GooglePerson());   // the finish

        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, "jan@firma.test", "user");
        await EnqueueInvitationAsync(invite, "jan@firma.test");
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == "jan@firma.test"));
        var invited = await TicketAsync("google", GooglePerson(email: "jan@firma.test", subject: "900000000000000000009"), "?invite=" + inviteToken);
        using (var accepted = await ProviderApi.Exchange(Client, invited))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using (var forgot = await AccountApi.Forgot(Client, Ola))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        await DispatchAsync();
        var resetToken = TokenIn(Mail.Sent.Last(m => m.To == Ola));
        using (var reset = await AccountApi.Reset(Client, resetToken, "Chosen-Passw0rd"))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        Assert.Empty(await NoticesAsync());
        Assert.Null(await LimitAsync(MailKind.NoticeLimit));
    }

    [Fact]
    public async Task A_link_and_its_notice_leave_no_token_and_no_ticket_in_the_log()   // spec 0012 → General rules
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        using (var linked = await ProviderApi.Link(Client, ticket, UserPassword))
        {
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        }

        await DispatchAsync();

        Assert.DoesNotContain(ticket.Value, Logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket.Binding, Logs.Text, StringComparison.Ordinal);
        Assert.Empty(await InDbAsync(db => db.EmailTokens.ToListAsync(TestContext.Current.CancellationToken)));
    }
}
```

In `tests/Auth.IntegrationTests/MailComposerTests.cs`, add before the class's last closing brace:

```csharp

    public static TheoryData<string, MailKind, string?> Notices => new()
    {
        { "en", MailKind.ProviderLinked, "GitHub" },
        { "en", MailKind.ProviderUnlinked, "Google" },
        { "en", MailKind.PasswordChanged, null },
        { "pl", MailKind.ProviderLinked, "Microsoft" },
        { "pl", MailKind.ProviderUnlinked, "GitHub" },
        { "pl", MailKind.PasswordChanged, null },
    };

    [Theory]
    [MemberData(nameof(Notices))]
    public void A_notice_names_the_provider_and_the_address_in_both_parts_and_holds_no_link(string locale, MailKind kind, string? provider)   // spec 0012, criterion 22
    {
        var mail = Composer(locale).ComposeNotice(kind, "user@example.com", provider);

        Assert.Equal("user@example.com", mail.To);
        Assert.Contains("speech-to-mail", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("<html lang=\"" + locale + "\">", mail.HtmlBody, StringComparison.Ordinal);
        foreach (var body in new[] { mail.TextBody, mail.HtmlBody })
        {
            Assert.Contains("user@example.com", body, StringComparison.Ordinal);
            if (provider is not null)
            {
                Assert.Contains(provider, body, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("http", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("app.example.com", body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("en", MailKind.ProviderLinked)]
    [InlineData("en", MailKind.ProviderUnlinked)]
    [InlineData("en", MailKind.PasswordChanged)]
    [InlineData("pl", MailKind.ProviderLinked)]
    [InlineData("pl", MailKind.ProviderUnlinked)]
    [InlineData("pl", MailKind.PasswordChanged)]
    public void Every_language_and_notice_has_every_text(string locale, MailKind kind)   // spec 0012 → Mails: Polish and English
    {
        var text = MailTexts.Notice(locale, kind);

        Assert.All(
            [text.Subject, text.Greeting, text.Intro, text.Advice, text.NoLink],
            value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Contains("{0}", text.Subject, StringComparison.Ordinal);
        Assert.Contains("{2}", text.Intro, StringComparison.Ordinal);
    }

    [Fact]
    public void A_notice_encodes_the_address_and_the_application_in_the_html_part_only()   // spec 0012 → Mails
    {
        var mail = Composer(appName: "A&B <Notes>").ComposeNotice(MailKind.ProviderLinked, "a&b@example.com", "GitHub");

        Assert.Contains("a&amp;b@example.com", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("A&amp;B &lt;Notes&gt;", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("a&b@example.com", mail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", mail.Subject, StringComparison.Ordinal);   // the address stays out of the header
    }

    [Fact]
    public void A_provider_notice_without_a_provider_and_another_kind_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Composer().ComposeNotice(MailKind.ProviderLinked, "user@example.com", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => Composer().ComposeNotice(MailKind.PasswordReset, "user@example.com", null));
    }
```

In `tests/Auth.IntegrationTests/MailLimitPolicyTests.cs`, add before the class's last closing brace:

```csharp

    [Fact]
    public void The_notice_rule_has_no_gap_and_ten_in_a_fixed_hour()   // spec 0012, Amendment A10
    {
        var rule = MailNotices.Rule;
        var start = DateTimeOffset.Parse("2026-05-04T18:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var state = MailLimitState.Fresh(start);
        for (var i = 0; i < 10; i++)
        {
            (state, var accepted) = MailLimitPolicy.Register(state, start + TimeSpan.FromSeconds(i), rule);
            Assert.True(accepted.Allowed);
        }

        var (_, refused) = MailLimitPolicy.Register(state, start + TimeSpan.FromMinutes(59), rule);
        Assert.False(refused.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(1), refused.RetryAfter);
        Assert.True(MailLimitPolicy.Register(state, start + TimeSpan.FromHours(1), rule).Decision.Allowed);
        Assert.Equal(MailLimitRule.Requests, new MailLimitRule(MailLimitPolicy.MinimumGap, MailLimitPolicy.WindowLimit, MailLimitPolicy.Window));
    }
```

In `tests/Auth.IntegrationTests/ProviderLinkTests.cs`, in `The_right_password_links_and_signs_in_and_the_provider_then_signs_in_alone`, replace

```csharp
        Assert.Equal(["provider"], AuditApi.DetailNames(row));
        Assert.Equal("google", AuditApi.Text(row, "provider"));
```

with

```csharp
        Assert.Equal(["provider", "via"], AuditApi.DetailNames(row));   // spec 0012 → Audit events
        Assert.Equal(("google", "sign_in"), (AuditApi.Text(row, "provider"), AuditApi.Text(row, "via")));
```

and add before the class's last closing brace:

```csharp

    private async Task<Guid> AccountWithGoogleLoginAsync()
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", "100000000000000000077");
        return user.Id;
    }

    [Fact]
    public async Task A_right_password_for_an_account_with_a_login_of_the_provider_is_409_provider_in_use_and_nothing_is_written()   // spec 0012, criterion 15
    {
        var userId = await AccountWithGoogleLoginAsync();
        await LockoutApi.FailAsync(Client, Clock, Ola, 2);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "provider_in_use");
        Assert.Equal(1, await TicketCountAsync());   // the ticket is kept
        Assert.Equal("100000000000000000077", Assert.Single(await LoginsAsync()).ProviderKey);
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
        Assert.Equal(0, await AttemptsOfAsync("OLA@GMAIL.TEST"));   // the streak is cleared, as for a right password
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Null(ProviderApi.BindingCookieOf(response));
        _ = userId;
    }

    [Fact]
    public async Task A_wrong_password_for_such_an_account_is_still_401_invalid_credentials()   // check order: the password before provider_in_use
    {
        await AccountWithGoogleLoginAsync();
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Link(Client, ticket, "Wrong-Passw0rd-9");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, await AttemptsOfAsync("OLA@GMAIL.TEST"));
    }

    [Fact]
    public async Task The_exchange_still_answers_link_for_an_account_with_a_login_of_the_provider()   // spec 0012 → Sign-in of a linked identity, and the link
    {
        await AccountWithGoogleLoginAsync();
        var ticket = await TicketAsync("google", GooglePerson());

        using var exchanged = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal("link", (await TenancyApi.ReadOkAsync(exchanged)).GetProperty("next").GetString());
    }

    [Fact]
    public async Task A_login_of_the_provider_added_while_the_link_waited_for_the_lock_is_409_provider_in_use()   // checked again under the lock
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        await using var held = await HeldLock.AddressAsync(Factory, "OLA@GMAIL.TEST");
        var pending = ProviderApi.Link(Client, ticket, UserPassword);
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await LinkAsync(user.Id, "google", "100000000000000000077");
        await held.ReleaseAsync();

        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "provider_in_use");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(TestContext.Current.CancellationToken)));
    }
```

(`LinkAsync` of `ProviderTestBase` writes the login row directly.)

In `tests/Auth.IntegrationTests/AuditProviderFlowsTests.cs`, replace `        Assert.True(Names(link, "provider"));` with
`        Assert.True(Names(link, "provider", "via"));   // spec 0012: via sign_in`.

In `tests/Auth.IntegrationTests/ProviderPasswordResetTests.cs`, add before the class's last closing brace:

```csharp

    [Fact]
    public async Task A_reset_keeps_the_logins_and_queues_no_password_changed_mail()   // spec 0012, criterion 20
    {
        var (userId, _) = await PrivateThroughProviderAsync("google", GooglePerson());
        var token = await ResetTokenAsync(Ola);
        var sent = Mail.Sent.Count;

        using (var reset = await AccountApi.Reset(Client, token, Chosen))
        {
            await AccountApi.AssertEmptyAsync(reset, HttpStatusCode.NoContent);
        }

        await DispatchAsync();
        Assert.Equal(sent, Mail.Sent.Count);
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.PasswordChanged, TestContext.Current.CancellationToken)));
        Assert.Single(await InDbAsync(db => db.UserLogins.Where(l => l.UserId == userId).ToListAsync(TestContext.Current.CancellationToken)));
    }
```

(add `using Auth.Infrastructure.Persistence;` at the top of the file if it is not there).

In `tests/Auth.IntegrationTests/OpenApiTests.cs`, replace `    [InlineData("POST /auth/providers/link", "200,400,401,403,415,429")]` with
`    [InlineData("POST /auth/providers/link", "200,400,401,403,409,415,429")]`, and after
`        Assert.Contains("too_many_attempts", Description(operations["POST /auth/providers/link"], "429"));` add
`        Assert.Contains("provider_in_use", Description(operations["POST /auth/providers/link"], "409"));   // spec 0012`.

- [ ] **Step 2: See them fail.** Build: it fails (`MailNotices`, `ComposeNotice`, `MailLimitRule` do not exist). Once Step 3 compiles,
`SettingsMailTests` fail (no row queued) and the link tests fail (`200` instead of `409`).

- [ ] **Step 3: The rule of a limit.** In `src/Auth.Server/Email/MailLimitPolicy.cs`, replace

```csharp
/// <summary>Accept the request, or refuse it and say when one would be accepted.</summary>
public readonly record struct MailLimitDecision(bool Allowed, TimeSpan RetryAfter);
```

with

```csharp
/// <summary>Accept the request, or refuse it and say when one would be accepted.</summary>
public readonly record struct MailLimitDecision(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// The numbers of one limit: at most one accepted request per <paramref name="MinimumGap"/> (none: <see cref="TimeSpan.Zero"/>) and at most
/// <paramref name="WindowLimit"/> per fixed <paramref name="Window"/>.
/// </summary>
public sealed record MailLimitRule(TimeSpan MinimumGap, int WindowLimit, TimeSpan Window)
{
    /// <summary>The limit of the mail requests of spec 0004.</summary>
    public static readonly MailLimitRule Requests = new(MailLimitPolicy.MinimumGap, MailLimitPolicy.WindowLimit, MailLimitPolicy.Window);
}
```

and replace

```csharp
    public static (MailLimitState Next, MailLimitDecision Decision) Register(MailLimitState current, DateTimeOffset now)
    {
        var accepted = new MailLimitDecision(true, TimeSpan.Zero);
        if (current.WindowCount == 0)
        {
            return (new MailLimitState(now, 1, now), accepted);
        }

        var windowOpen = now - current.WindowStartedAt < Window;
        var gapWait = current.LastAcceptedAt + MinimumGap - now;
        var windowWait = windowOpen && current.WindowCount >= WindowLimit
            ? current.WindowStartedAt + Window - now
            : TimeSpan.Zero;
```

with

```csharp
    public static (MailLimitState Next, MailLimitDecision Decision) Register(MailLimitState current, DateTimeOffset now) =>
        Register(current, now, MailLimitRule.Requests);

    /// <summary>The same transition under <paramref name="rule"/> (spec 0012: the notices' limit has no gap and its own numbers).</summary>
    public static (MailLimitState Next, MailLimitDecision Decision) Register(MailLimitState current, DateTimeOffset now, MailLimitRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var accepted = new MailLimitDecision(true, TimeSpan.Zero);
        if (current.WindowCount == 0)
        {
            return (new MailLimitState(now, 1, now), accepted);
        }

        var windowOpen = now - current.WindowStartedAt < rule.Window;
        var gapWait = current.LastAcceptedAt + rule.MinimumGap - now;
        var windowWait = windowOpen && current.WindowCount >= rule.WindowLimit
            ? current.WindowStartedAt + rule.Window - now
            : TimeSpan.Zero;
```

(`MailLimitRule.Requests` reads the three constants, which are initialised before it: `MailLimitPolicy` is another class, so the order of
static initialisation is by first use, and the constants are `const`/`static readonly` of `MailLimitPolicy`.)

In `src/Auth.Server/Email/MailLimits.cs`, replace

```csharp
    public static async Task<MailLimitDecision> RegisterAsync(
        AuthDbContext db, byte[] identifierHash, MailKind kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
```

with

```csharp
    /// <param name="rule">The numbers of the limit; spec 0004's (<see cref="MailLimitRule.Requests"/>) when left out.</param>
    public static async Task<MailLimitDecision> RegisterAsync(
        AuthDbContext db, byte[] identifierHash, MailKind kind, DateTimeOffset now, CancellationToken cancellationToken, MailLimitRule? rule = null)
    {
```

and replace

```csharp
        var (next, decision) = MailLimitPolicy.Register(
            new MailLimitState(row.WindowStartedAt, row.WindowCount, row.LastAcceptedAt), now);
```

with

```csharp
        var (next, decision) = MailLimitPolicy.Register(
            new MailLimitState(row.WindowStartedAt, row.WindowCount, row.LastAcceptedAt), now, rule ?? MailLimitRule.Requests);
```

- [ ] **Step 4: The notices.** `src/Auth.Server/Email/MailNotices.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Lockout;

namespace Auth.Server.Email;

/// <summary>
/// The notices of spec 0012 → Mails: "provider connected", "provider disconnected" and "password changed". A notice is queued by the change that
/// causes it, on the change's own context and inside its transaction (never through <see cref="MailRequestStore"/>, and not counted by its
/// limit), and the caller saves and commits; <see cref="Notify"/> wakes the dispatcher after the commit. The three kinds share one limit per
/// address (Amendment A10): ten in a fixed hour opened by the first, with no gap, under the key <see cref="MailKind.NoticeLimit"/> in the store
/// of spec 0004's limits. Beyond it the change still happens: no row, and one warning naming the kind, never the address.
/// </summary>
public sealed partial class MailNotices(MailDispatchSignal signal, ILogger<MailNotices> logger)
{
    public static readonly MailLimitRule Rule = new(TimeSpan.Zero, 10, TimeSpan.FromHours(1));

    /// <summary>Adds the notice to <paramref name="db"/> when the limit lets it through; the caller saves. Returns whether it was added.</summary>
    /// <param name="provider">The provider's name for a provider notice (required there); <see langword="null"/> for "password changed".</param>
    public async Task<bool> QueueAsync(
        AuthDbContext db, MailKind kind, string normalizedEmail, string? provider, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizedEmail);
        if (kind is not (MailKind.ProviderLinked or MailKind.ProviderUnlinked or MailKind.PasswordChanged))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a notice.");
        }

        if ((kind == MailKind.PasswordChanged) != (provider is null))
        {
            throw new ArgumentException("A provider notice names its provider, and only a provider notice does.", nameof(provider));
        }

        var decision = await MailLimits.RegisterAsync(db, LoginIdentifier.HashOf(normalizedEmail), MailKind.NoticeLimit, now, cancellationToken, Rule);
        if (!decision.Allowed)
        {
            LogLimitReached(logger, kind);
            return false;
        }

        db.MailRequests.Add(new MailRequest { Kind = kind, NormalizedEmail = normalizedEmail, Provider = provider, RequestedAt = now, NextAttemptAt = now });
        return true;
    }

    /// <summary>Wakes the dispatcher; called once the change's transaction has committed.</summary>
    public void Notify() => signal.Notify();

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {Kind} mail was not queued: the address reached the notice limit of the hour.")]
    private static partial void LogLimitReached(ILogger logger, MailKind kind);
}
```

In `src/Auth.Server/Program.cs`, replace

```csharp
builder.Services.AddSingleton<MailDispatchSignal>();
```

with

```csharp
builder.Services.AddSingleton<MailDispatchSignal>();
builder.Services.AddSingleton<MailNotices>();
```

- [ ] **Step 5: The texts and the composer.** In `src/Auth.Server/Email/MailTexts.cs`, replace

```csharp
public sealed record AccountExistsText(string Subject, string Greeting, string Intro, string SignInHint, string Deliberate, string Ignore);
```

with

```csharp
public sealed record AccountExistsText(string Subject, string Greeting, string Intro, string SignInHint, string Deliberate, string Ignore);

/// <summary>
/// The texts of a notice (spec 0012 → Mails). No link and no button. <c>{0}</c> is the application, <c>{1}</c> the provider's display name,
/// <c>{2}</c> the account's address (in the body only: a header names the application and the provider, never the address).
/// </summary>
/// <param name="NoLink">Says that the mail holds no link, so that a copy of it that holds one is known for a forgery.</param>
public sealed record NoticeText(string Subject, string Greeting, string Intro, string Advice, string NoLink);
```

and add before the class's last closing brace (after `InvitationToPrivateAccount`):

```csharp

    /// <summary>The notices of spec 0012 → Mails, Polish and English.</summary>
    public static NoticeText Notice(string locale, MailKind kind) => (locale, kind) switch
    {
        ("en", MailKind.ProviderLinked) => new NoticeText(
            Subject: "{1} was connected to your {0} account",
            Greeting: "Hello,",
            Intro: "{1} was connected to your {0} account {2}. From now on it signs you in.",
            Advice: "If it was not you, reset your password with \"Forgot your password?\" on the sign-in screen of {0}, then disconnect {1} in your settings.",
            NoLink: "This message holds no link: to act on it, open {0} yourself."),
        ("en", MailKind.ProviderUnlinked) => new NoticeText(
            Subject: "{1} was disconnected from your {0} account",
            Greeting: "Hello,",
            Intro: "{1} was disconnected from your {0} account {2}. It no longer signs you in, and your other devices were signed out.",
            Advice: "If it was not you, reset your password with \"Forgot your password?\" on the sign-in screen of {0} and check your settings.",
            NoLink: "This message holds no link: to act on it, open {0} yourself."),
        ("en", MailKind.PasswordChanged) => new NoticeText(
            Subject: "The password of your {0} account was changed",
            Greeting: "Hello,",
            Intro: "The password of your {0} account {2} was changed, and your other devices were signed out.",
            Advice: "If it was not you, reset it with \"Forgot your password?\" on the sign-in screen of {0}.",
            NoLink: "This message holds no link: to act on it, open {0} yourself."),
        ("pl", MailKind.ProviderLinked) => new NoticeText(
            Subject: "Do Twojego konta w {0} podłączono {1}",
            Greeting: "Dzień dobry,",
            Intro: "Do Twojego konta {2} w {0} podłączono {1}. Od teraz możesz się nim logować.",
            Advice: "Jeśli to nie Ty, ustaw nowe hasło, korzystając z opcji ustawienia nowego hasła na ekranie logowania {0}, a potem odłącz {1} w ustawieniach.",
            NoLink: "Ta wiadomość nie zawiera linku: aby coś zmienić, otwórz {0} samodzielnie."),
        ("pl", MailKind.ProviderUnlinked) => new NoticeText(
            Subject: "Od Twojego konta w {0} odłączono {1}",
            Greeting: "Dzień dobry,",
            Intro: "Od Twojego konta {2} w {0} odłączono {1}. Nie służy już do logowania, a Twoje pozostałe urządzenia zostały wylogowane.",
            Advice: "Jeśli to nie Ty, ustaw nowe hasło, korzystając z opcji ustawienia nowego hasła na ekranie logowania {0}, i sprawdź swoje ustawienia.",
            NoLink: "Ta wiadomość nie zawiera linku: aby coś zmienić, otwórz {0} samodzielnie."),
        ("pl", MailKind.PasswordChanged) => new NoticeText(
            Subject: "Hasło do Twojego konta w {0} zostało zmienione",
            Greeting: "Dzień dobry,",
            Intro: "Hasło do Twojego konta {2} w {0} zostało zmienione, a Twoje pozostałe urządzenia zostały wylogowane.",
            Advice: "Jeśli to nie Ty, ustaw nowe hasło, korzystając z opcji ustawienia nowego hasła na ekranie logowania {0}.",
            NoLink: "Ta wiadomość nie zawiera linku: aby coś zmienić, otwórz {0} samodzielnie."),
        _ => throw new ArgumentOutOfRangeException(nameof(locale), $"No notice text for locale '{locale}' and kind '{kind}'."),
    };
```

(The Polish letters are typed as letters, as in the texts above; the file has no escape.)

In `src/Auth.Server/Email/MailComposer.cs`, add after the method `ComposeAccountExists` (before the class's last closing brace):

```csharp

    /// <summary>
    /// A notice of spec 0012 → Mails: what changed on the account, and what to do if it was not the reader. No link and no token: it names the
    /// product's sign-in screen in words only. The address and the provider's display name are in the body, encoded in its HTML part; the subject
    /// names the application and the provider only.
    /// </summary>
    public ComposedMail ComposeNotice(MailKind kind, string recipient, string? providerDisplayName)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        if (kind is not (MailKind.ProviderLinked or MailKind.ProviderUnlinked or MailKind.PasswordChanged))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a notice.");
        }

        if (kind != MailKind.PasswordChanged && providerDisplayName is null)
        {
            throw new ArgumentException("A provider notice names its provider.", nameof(providerDisplayName));
        }

        var text = MailTexts.Notice(settings.Locale, kind);
        string Format(string template) => string.Format(CultureInfo.InvariantCulture, template, settings.AppName, providerDisplayName, recipient);
        var subject = Format(text.Subject);
        var intro = Format(text.Intro);
        var advice = Format(text.Advice);
        var noLink = Format(text.NoLink);

        return new ComposedMail
        {
            To = recipient,
            Subject = subject,
            TextBody = string.Join("\r\n", text.Greeting, "", intro, "", advice, "", noLink, ""),
            HtmlBody = $"""
                <!DOCTYPE html>
                <html lang="{settings.Locale}">
                <head><meta charset="utf-8"><title>{Html.Encode(subject)}</title></head>
                <body style="{BodyStyle}">
                <p>{Html.Encode(text.Greeting)}</p>
                <p>{Html.Encode(intro)}</p>
                <p>{Html.Encode(advice)}</p>
                <p style="font-size: 14px; color: #52606d;">{Html.Encode(noLink)}</p>
                </body>
                </html>
                """,
        };
    }
```

and replace `/// Builds the mails of specs 0004, 0005, 0009 and 0010 in the configured language, each as plain text and as HTML with the same`
with `/// Builds the mails of specs 0004, 0005, 0009, 0010 and 0012 in the configured language, each as plain text and as HTML with the same`.

- [ ] **Step 6: The dispatcher.** In `src/Auth.Server/Email/MailDispatcher.cs`:

(a) in `DropUnmailableAsync`, replace

```csharp
        var passwordReset = (short)MailKind.PasswordReset;
        var invitation = (short)MailKind.Invitation;
        var signup = (short)MailKind.Signup;
        var organization = (short)SignupKind.Organization;
```

with

```csharp
        var passwordReset = (short)MailKind.PasswordReset;
        var providerLinked = (short)MailKind.ProviderLinked;
        var providerUnlinked = (short)MailKind.ProviderUnlinked;
        var passwordChanged = (short)MailKind.PasswordChanged;
        var invitation = (short)MailKind.Invitation;
        var signup = (short)MailKind.Signup;
        var organization = (short)SignupKind.Organization;
```

and replace

```csharp
                                 AND u."Email" IS NOT NULL
                                 AND (r."Kind" = {passwordReset} OR NOT u."EmailConfirmed"))))
                FOR UPDATE OF r SKIP LOCKED)
```

with

```csharp
                                 AND u."Email" IS NOT NULL
                                 AND (r."Kind" IN ({passwordReset}, {providerLinked}, {providerUnlinked}, {passwordChanged}) OR NOT u."EmailConfirmed"))))
                FOR UPDATE OF r SKIP LOCKED)
```

(b) in `DispatchOneAsync`, replace

```csharp
        var passwordReset = (short)MailKind.PasswordReset;
        var invitation = (short)MailKind.Invitation;
        var signup = (short)MailKind.Signup;
        var due = await db.MailRequests
```

with

```csharp
        var passwordReset = (short)MailKind.PasswordReset;
        var providerLinked = (short)MailKind.ProviderLinked;
        var providerUnlinked = (short)MailKind.ProviderUnlinked;
        var passwordChanged = (short)MailKind.PasswordChanged;
        var invitation = (short)MailKind.Invitation;
        var signup = (short)MailKind.Signup;
        var due = await db.MailRequests
```

and replace

```csharp
                                      AND u."Email" IS NOT NULL
                                      AND (r."Kind" = {passwordReset} OR NOT u."EmailConfirmed")))))
                   OR r."RequestedAt" <= {givenUp}
```

with

```csharp
                                      AND u."Email" IS NOT NULL
                                      AND (r."Kind" IN ({passwordReset}, {providerLinked}, {providerUnlinked}, {passwordChanged}) OR NOT u."EmailConfirmed")))))
                   OR r."RequestedAt" <= {givenUp}
```

(c) replace

```csharp
            MailKind.Signup => await PrepareSignupMailAsync(db, request, now, cancellationToken),
            _ => await PrepareAccountMailAsync(db, request, now, cancellationToken),
```

with

```csharp
            MailKind.Signup => await PrepareSignupMailAsync(db, request, now, cancellationToken),
            MailKind.ProviderLinked or MailKind.ProviderUnlinked or MailKind.PasswordChanged => await PrepareNoticeAsync(db, request, cancellationToken),
            _ => await PrepareAccountMailAsync(db, request, now, cancellationToken),
```

(d) add after `PrepareAccountMailAsync`:

```csharp

    /// <summary>
    /// A notice (spec 0012 → Mails): to the stored address of the account, confirmed or not, with the provider of the row and no token. A row whose
    /// address has no account any more (deleted meanwhile), or a provider notice without a provider, is dropped.
    /// </summary>
    private async Task<Func<Task<ComposedMail>>?> PrepareNoticeAsync(AuthDbContext db, MailRequest request, CancellationToken cancellationToken)
    {
        var email = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail == request.NormalizedEmail)
            .OrderBy(u => u.Id)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);
        if (email is null || (request.Kind != MailKind.PasswordChanged && request.Provider is null))
        {
            return null;
        }

        var provider = request.Provider is null ? null : ProviderNames.DisplayNameOf(request.Provider);
        return () => Task.FromResult(composer.ComposeNotice(request.Kind, email, provider));
    }
```

and add `using Auth.Server.Providers;` to the file's usings. Extend the class summary's last sentence with: `A notice (spec 0012) needs an account of
the address, whatever its confirmation, and carries no token.`

- [ ] **Step 7: The link.** In `src/Auth.Server/Providers/ProviderLogins.cs`, add after `IsLoginOfAsync`:

```csharp

    /// <summary>Whether the account has a login of <paramref name="provider"/> (spec 0012: one login per provider per account).</summary>
    public static Task<bool> HasLoginOfProviderAsync(AuthDbContext db, Guid userId, string provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.UserLogins.AsNoTracking().AnyAsync(l => l.UserId == userId && l.LoginProvider == provider, cancellationToken);
    }
```

In `src/Auth.Server/Providers/ProviderSignIn.cs`, replace

```csharp
    /// <summary>The answer of a sign-in through a provider, in <paramref name="transaction"/>, which the result owns from now on.</summary>
    public static IResult Session(
        HttpContext http, ApplicationUser account, TenantContext tenant, string provider, IDbContextTransaction transaction, TokenOptions tokens,
        TimeProvider clock, Func<Task<IResult>>? whenGone = null) =>
```

with

```csharp
    /// <summary>The answer of a sign-in through a provider, in <paramref name="transaction"/>, which the result owns from now on.</summary>
    /// <param name="alsoCommitted">Runs after the commit too (the link wakes the dispatcher for the notice it queued).</param>
    public static IResult Session(
        HttpContext http, ApplicationUser account, TenantContext tenant, string provider, IDbContextTransaction transaction, TokenOptions tokens,
        TimeProvider clock, Func<Task<IResult>>? whenGone = null, Action<HttpContext>? alsoCommitted = null) =>
```

and replace

```csharp
            whenCommitted: context => ProviderBindingCookie.Clear(context.Response));
```

with

```csharp
            whenCommitted: context =>
            {
                ProviderBindingCookie.Clear(context.Response);
                alsoCommitted?.Invoke(context);
            });
```

In `src/Auth.Server/Providers/ProviderLinkEndpoint.cs`:

(a) replace

```csharp
    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, LoginStreakStore streaks,
        DecoyPasswordHash decoy, AuditLog audit, IOptions<TokenOptions> tokens, TimeProvider clock)
    {
```

with

```csharp
    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, LoginStreakStore streaks,
        DecoyPasswordHash decoy, AuditLog audit, MailNotices notices, IOptions<TokenOptions> tokens, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(notices);
```

(b) replace

```csharp
            await RecordFailureAsync(audit, "no_company", account, email, ticket.Provider);
            return ApiResults.Error(TenancyErrors.NoMembership);
        }

        return await LinkAndSignInAsync(http, db, users, memberships, audit, tokens.Value, clock, account, ticket, ticketText, binding!);
    }
```

with

```csharp
            await RecordFailureAsync(audit, "no_company", account, email, ticket.Provider);
            return ApiResults.Error(TenancyErrors.NoMembership);
        }

        // Spec 0012: one login per provider per account. The ticket is kept and nothing is written (the streak is already cleared).
        if (await ProviderLogins.HasLoginOfProviderAsync(db, account.Id, ticket.Provider, http.RequestAborted))
        {
            return ApiResults.Error(TenancyErrors.ProviderInUse);
        }

        return await LinkAndSignInAsync(http, db, users, memberships, audit, notices, tokens.Value, clock, account, ticket, ticketText, binding!);
    }
```

(c) replace

```csharp
    /// <summary>8. In one transaction under the address lock: the login, <c>provider.linked</c>, the ticket used up, the sign-in.</summary>
    private static async Task<IResult> LinkAndSignInAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, AuditLog audit, TokenOptions tokens,
        TimeProvider clock, ApplicationUser account, ProviderTicket ticket, string ticketText, string binding)
    {
```

with

```csharp
    /// <summary>
    /// 8. In one transaction under the address lock: the login, <c>provider.linked</c> (<c>via</c> <c>sign_in</c>), the notice "provider
    /// connected", the ticket used up, the sign-in.
    /// </summary>
    private static async Task<IResult> LinkAndSignInAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, AuditLog audit, MailNotices notices,
        TokenOptions tokens, TimeProvider clock, ApplicationUser account, ProviderTicket ticket, string ticketText, string binding)
    {
```

(d) replace

```csharp
            ProviderLogins.Add(db, account.Id, ticket.Provider, ticket.IdentityKey);
            account.EmailConfirmed = true;   // the password proves the account, the provider the mailbox (Settled 13)
```

with

```csharp
            // Spec 0012: a login of this provider added meanwhile (a connect, another link) is the answer of step 7's check; the ticket is
            // given back by the rollback.
            if (await ProviderLogins.HasLoginOfProviderAsync(db, account.Id, ticket.Provider, CancellationToken.None))
            {
                await transaction.DisposeAsync();
                return ApiResults.Error(TenancyErrors.ProviderInUse);
            }

            ProviderLogins.Add(db, account.Id, ticket.Provider, ticket.IdentityKey);
            account.EmailConfirmed = true;   // the password proves the account, the provider the mailbox (Settled 13)
            await notices.QueueAsync(db, MailKind.ProviderLinked, account.NormalizedEmail!, ticket.Provider, StorableTime.Now(clock), CancellationToken.None);
```

(e) replace

```csharp
                Details = new Dictionary<string, object?> { ["provider"] = ticket.Provider },
            });
            await db.SaveChangesAsync(CancellationToken.None);
            return ProviderSignIn.Session(http, account, tenant, ticket.Provider, transaction, tokens, clock, InvalidCredentialsAsync);
```

with

```csharp
                Details = new Dictionary<string, object?> { ["provider"] = ticket.Provider, ["via"] = "sign_in" },
            });
            await db.SaveChangesAsync(CancellationToken.None);
            return ProviderSignIn.Session(
                http, account, tenant, ticket.Provider, transaction, tokens, clock, InvalidCredentialsAsync, alsoCommitted: _ => notices.Notify());
```

(f) in the class summary, replace `/// login, <c>provider.linked</c>, the ticket used up and the sign-in.` with
`/// login, <c>provider.linked</c>, the notice "provider connected", the ticket used up and the sign-in. Spec 0012: an account that already has a login
/// of the ticket's provider is <c>409 provider_in_use</c> after the password is judged right, and again under the lock.`

The notice and `provider.linked` read the same clock (`AuditLog` stamps its rows with `StorableTime.Now` at the save), so under the
test clock they carry the same instant.

In `src/Auth.Server/Api/ProviderRoutes.cs`, in the link's description replace
`+ "`403 no_membership`. Then the login, `provider.linked`, the use of the ticket and the session are one change: `200` as a login "`
with
`+ "`403 no_membership`; an account that already has a login of the ticket's provider `409 provider_in_use`, the ticket kept (spec 0012). Then "
+ "the login, `provider.linked` (`via` `sign_in`), the mail \"provider connected\", the use of the ticket and the session are one change: `200` as a login "`,
and replace

```csharp
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.NoEmail, TenancyErrors.EmailNotVerified, TenancyErrors.NoMembership)
            .ProducesTooManyAttempts();

        app.MapPost(ProviderPaths.Finish, ProviderFinishEndpoint.HandleAsync)
```

with

```csharp
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.NoEmail, TenancyErrors.EmailNotVerified, TenancyErrors.NoMembership)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.ProviderInUse)
            .ProducesTooManyAttempts();

        app.MapPost(ProviderPaths.Finish, ProviderFinishEndpoint.HandleAsync)
```

- [ ] **Step 8: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.SettingsMailTests -class Auth.IntegrationTests.MailComposerTests -class Auth.IntegrationTests.MailLimitPolicyTests -class Auth.IntegrationTests.MailDispatcherTests -class Auth.IntegrationTests.ProviderLinkTests -class Auth.IntegrationTests.ProviderLinkRaceTests -class Auth.IntegrationTests.ProviderLinkLockoutTests -class Auth.IntegrationTests.AuditProviderFlowsTests -class Auth.IntegrationTests.ProviderPasswordResetTests -class Auth.IntegrationTests.MailRequestEndpointTests -class Auth.IntegrationTests.MailRequestStoreTests -class Auth.IntegrationTests.OpenApiTests`
passes. A test of spec 0011 that dispatches after a link and asserts a single mail to the address now sees the notice too: it is a pinning
test of this change (the link's mail); change its assertion to the mail it means (by subject) and name it in the hand-back. Mutate and see a
test fail, then restore: drop the kinds from `DropUnmailableAsync` only (`A_notice_goes_to_an_account_of_the_address_confirmed_or_not_and_never_as_a_verification(False)`
fails); queue before the limit check (`The_eleventh_notice_...` fails).

- [ ] **Step 9: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): notice mails for a change of logins; the link refuses a second login of a provider`.

---

## Day 3 — Saturday 09.05: connect, unlink, the hold of a sign-in, the password, the company deletion

### Task 9: Connect: the `connect` purpose and `POST /auth/providers/connect`

**Files:**
- Create: `src/Auth.Server/Providers/ProviderConnectEndpoint.cs`
- Modify: `src/Auth.Server/Providers/ProviderPurposes.cs`, `src/Auth.Server/Providers/ProviderStartEndpoint.cs`,
  `src/Auth.Server/Providers/ProviderExchangeEndpoint.cs`, `src/Auth.Server/Providers/ProviderPaths.cs`, `src/Auth.Server/Api/SettingsRoutes.cs`,
  `src/Auth.Server/Api/ProviderRoutes.cs`
- Create (tests): `tests/Auth.IntegrationTests/ProviderConnectTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/ProviderTicketTests.cs`, `tests/Auth.IntegrationTests/ProviderStartTests.cs`,
  `tests/Auth.IntegrationTests/ConfirmationWindowTests.cs`, `tests/Auth.IntegrationTests/AccountConfirmTests.cs`,
  `tests/Auth.IntegrationTests/SettingsMailTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`, `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`

**Interfaces:**
- Consumes: `SettingsCallers` (Task 7), `DeviceSession.IsConfirmedAt` (Task 2), `MailNotices` and `ProviderLogins.HasLoginOfProviderAsync`
  (Task 8), `ProviderTicketPurpose.Connect` (Task 1).
- Produces: `ProviderPurposes.ConnectName` (`connect`); `ProviderPaths.Connect` (`/auth/providers/connect`); `ProviderConnectEndpoint.HandleAsync`. The body
  is documented with the existing `ProviderTicketRequest(string Ticket)`.

**Contract counts:** `OpenApiTests`: `Endpoints` gains `POST /auth/providers/connect`; statuses `204,400,401,403,409,415,429`; body `ticket`;
cookies `auth_pt`, `auth_rt` (`CookiesRead`; an endpoint that reads `auth_pt` must describe a body); the bearer token; its `204` sets a cookie. `OpenApiHardeningTests`:
**37 → 38** operations, **25 → 26** under the charset guard. The start's `purpose` description names `connect`.

**Decisions the plan takes here:**
- The connect is a plain `POST` (not a token-endpoint request): it hands out no session (Amendment A9).
- Step 5's `204` (the identity is already this account's login) uses the ticket up in one statement outside the lock and clears `auth_pt`
  too, as every answer that uses a ticket up does; nothing else is written.
- Under the lock the window is read again with the session (the same `SettingsCallers.ReadAgainAsync`); a window ended meanwhile is `403
  confirmation_required` and the ticket is given back by the rollback. Steps 5 to 7 are read again after the ticket is used up, so that two
  connects at once answer by those steps.
- The address lock is the account's, which is the ticket's address (step 8 requires the same address): every flow that adds a login takes the
  lock of the ticket's address, so the identity cannot be added to two accounts at once.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/ProviderConnectTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → <c>POST /auth/providers/connect</c> (criteria 12 and 13), in the order of its checks.</summary>
public sealed class ProviderConnectTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string OlaNormalized = "OLA@GMAIL.TEST";
    private const string OtherGoogleSubject = "100000000000000000077";

    private Task<List<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>> LoginsOfAsync(Guid userId) =>
        InDbAsync(db => db.UserLogins.AsNoTracking().Where(l => l.UserId == userId).ToListAsync(TestContext.Current.CancellationToken));

    private Task<int> NoticeCountAsync() =>
        InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.ProviderLinked, TestContext.Current.CancellationToken));

    /// <summary>An account with a password, signed in on this device a moment ago: its window is open.</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> AccountAsync()
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        return (user.Id, await SessionApi.LoginAsync(Client, Ola, UserPassword));
    }

    private Task<ProviderApi.Ticket> ConnectTicketAsync(FakeSignIn? person = null, string provider = "google") =>
        TicketAsync(provider, person ?? GooglePerson(), "?purpose=connect");

    [Fact]
    public async Task An_open_window_and_a_ticket_of_the_accounts_verified_address_adds_the_login_and_signs_nobody_out()   // criterion 12
    {
        var (userId, here) = await AccountAsync();
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var ticket = await ConnectTicketAsync();

        using var response = await SettingsApi.Connect(Client, here, ticket);

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal(0, await TicketCountAsync());
        var login = Assert.Single(await LoginsOfAsync(userId));
        Assert.Equal(("google", FakePeople.GoogleSubject), (login.LoginProvider, login.ProviderKey));
        var row = await SingleAsync(AuditKinds.ProviderLinked);
        Assert.Equal((userId, userId, Ola), (row.ActorUserId!.Value, row.SubjectUserId!.Value, row.SubjectEmail));
        Assert.Equal(await DevCompanyIdAsync(), row.OrgId);
        Assert.Equal(["provider", "via"], AuditApi.DetailNames(row));
        Assert.Equal(("google", "settings"), (AuditApi.Text(row, "provider"), AuditApi.Text(row, "via")));
        Assert.Equal(1, await NoticeCountAsync());
        Assert.Equal(2, (await AuditAsync(AuditKinds.LoginSucceeded)).Count);   // the two logins: the connect issued no session

        // Every session lives on, and the provider alone signs in from now on.
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, here.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, there.RefreshToken)).StatusCode);
        using var exchanged = await ProviderApi.Exchange(Client, await TicketAsync("google", GooglePerson()));
        Assert.Equal(userId.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(exchanged)).AccessToken, "sub"));
    }

    [Theory]
    [InlineData("sign_in")]
    [InlineData("reauth")]
    [InlineData("unknown")]
    [InlineData("used")]
    [InlineData("expired")]
    [InlineData("no-binding")]
    [InlineData("another-binding")]
    [InlineData("blank")]
    public async Task A_ticket_that_is_not_a_live_connect_ticket_of_this_browser_is_400_invalid_ticket(string how)   // criterion 13
    {
        var (userId, session) = await AccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(), how switch { "sign_in" => "", "reauth" => "?purpose=reauth", _ => "?purpose=connect" });
        var value = ticket.Value;
        string? binding = ticket.Binding;
        switch (how)
        {
            case "unknown":
                value = "unknownunknown" + ticket.Value[14..];
                break;
            case "used":
                using (var first = await SettingsApi.Connect(Client, session, ticket))
                {
                    Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
                }

                await InDbAsync(db => db.UserLogins.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
                break;
            case "expired":
                Clock.Advance(TimeSpan.FromMinutes(10));
                session = await SessionApi.RefreshOk(Client, session.RefreshToken);
                using (var confirmed = await SettingsApi.Confirm(Client, session, new { password = UserPassword }))
                {
                    Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);   // the window is open again: only the ticket is dead
                }

                break;
            case "no-binding":
                binding = null;
                break;
            case "another-binding":
                binding = (await TicketAsync("microsoft", PersonalMicrosoft())).Binding;
                break;
            case "blank":
                value = "";
                break;
        }

        var tickets = await TicketCountAsync();
        using var response = await SettingsApi.Post(Client, SettingsApi.ConnectPath, session, new { ticket = value }, binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(tickets, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(userId));
    }

    [Fact]
    public async Task A_closed_window_is_403_confirmation_required_and_the_ticket_stays_usable()   // criterion 13, Review Focus 5
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();
        Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        using var refused = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.Forbidden, "confirmation_required");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Null(ProviderApi.BindingCookieOf(refused));

        using (var confirmed = await SettingsApi.Confirm(Client, session, new { password = UserPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        using var again = await SettingsApi.Connect(Client, session, ticket);   // the same ticket, sent again
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Single(await LoginsOfAsync(userId));
    }

    [Fact]
    public async Task An_identity_that_is_already_this_accounts_login_is_204_the_ticket_used_up_and_nothing_written()   // criterion 13, step 5
    {
        var (userId, session) = await AccountAsync();
        await LinkAsync(userId, "google", FakePeople.GoogleSubject);
        var ticket = await ConnectTicketAsync();

        using var response = await SettingsApi.Connect(Client, session, ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal(0, await TicketCountAsync());
        Assert.Single(await LoginsOfAsync(userId));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal(0, await NoticeCountAsync());   // no mail for a connect of an identity already linked
    }

    [Fact]
    public async Task An_identity_of_another_account_is_409_provider_already_linked()   // criterion 13, step 6
    {
        var (userId, session) = await AccountAsync();
        var kim = await CreateUserAsync("kim@firma.test", confirmed: true);
        await LinkAsync(kim.Id, "google", FakePeople.GoogleSubject);
        await LinkAsync(userId, "google", OtherGoogleSubject);   // step 6 comes before step 7
        var ticket = await ConnectTicketAsync();

        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "provider_already_linked");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
    }

    [Fact]
    public async Task An_account_with_a_login_of_the_provider_is_409_provider_in_use()   // criterion 13, step 7
    {
        var (userId, session) = await AccountAsync();
        await LinkAsync(userId, "google", OtherGoogleSubject);
        var ticket = await ConnectTicketAsync(GooglePerson(verified: false));   // step 7 comes before step 8

        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "provider_in_use");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal(OtherGoogleSubject, Assert.Single(await LoginsOfAsync(userId)).ProviderKey);
    }

    [Theory]
    [InlineData("no-email", "no_email")]
    [InlineData("unverified", "email_not_verified")]
    public async Task A_ticket_without_a_verified_address_is_refused_and_the_ticket_stays(string how, string code)   // criterion 13, step 8
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync(how == "no-email" ? GooglePerson(defect: FakeDefects.NoEmail) : GooglePerson(verified: false));

        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, code);
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(userId));
    }

    [Theory]
    [InlineData("ola.work@gmail.test")]
    [InlineData("o1a@gmail.test")]
    [InlineData("ola@gmail.test.")]
    public async Task An_address_that_is_not_the_accounts_is_403_address_mismatch_and_the_ticket_stays(string address)   // criterion 13, Review Focus 4
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync(GooglePerson(email: address, subject: OtherGoogleSubject));

        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "address_mismatch");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(userId));
    }

    [Fact]
    public async Task An_unverified_address_that_is_also_not_the_accounts_is_email_not_verified_not_address_mismatch()   // criterion 13, step 8: the order of its checks
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync(GooglePerson(email: "ola.work@gmail.test", verified: false, subject: OtherGoogleSubject));

        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "email_not_verified");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(userId));
    }

    [Fact]
    public async Task The_same_address_in_another_ascii_case_connects()   // EmailInput.IsSameAddress
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync(GooglePerson(email: "OLA@Gmail.Test"));

        using var response = await SettingsApi.Connect(Client, session, ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(await LoginsOfAsync(userId));
    }

    [Fact]
    public async Task The_checks_run_in_the_order_of_the_contract()   // guard lesson 9: one pair of adjacent steps at a time
    {
        var (userId, session) = await AccountAsync();
        var kim = await CreateUserAsync("kim@firma.test", confirmed: true);
        await LinkAsync(kim.Id, "google", FakePeople.GoogleSubject);
        var ticket = await ConnectTicketAsync();

        // 3 before 4: a dead ticket and a closed window are 400.
        Clock.Advance(TimeSpan.FromMinutes(6));
        session = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using (var dead = await SettingsApi.Post(Client, SettingsApi.ConnectPath, session, new { ticket = "nope" }, ticket.Binding))
        {
            await TenancyApi.AssertErrorAsync(dead, HttpStatusCode.BadRequest, "invalid_ticket");
        }

        // 4 before 6: another account's identity and a closed window are 403 confirmation_required.
        using (var closed = await SettingsApi.Connect(Client, session, ticket))
        {
            await TenancyApi.AssertErrorAsync(closed, HttpStatusCode.Forbidden, "confirmation_required");
        }

        // 2 before 3: no membership and a dead ticket are 403 no_membership.
        await RemoveMembershipAsync(userId);
        using var noMembership = await SettingsApi.Post(Client, SettingsApi.ConnectPath, session, new { ticket = "nope" }, null);
        await TenancyApi.AssertErrorAsync(noMembership, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(1, await TicketCountAsync());
    }

    public static TheoryData<string> BadBodies => new() { "not json", "{}", """["ticket"]""", """{"ticket":5}""", """{"ticket":null}""" };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request_before_the_session(string body)   // criterion 13, step 1
    {
        var (_, session) = await AccountAsync();

        using var response = await SettingsApi.PostRaw(Client, SettingsApi.ConnectPath, session with { RefreshToken = "" }, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Without_the_refresh_cookie_it_is_401_session_required_and_without_a_token_the_plain_401()   // step 2
    {
        var (_, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();

        using var noCookie = await SettingsApi.PostAs(Client, SettingsApi.ConnectPath, session.AccessToken, null, new { ticket = ticket.Value }, ticket.Binding);
        using var noToken = await SettingsApi.PostAs(Client, SettingsApi.ConnectPath, null, session.RefreshToken, new { ticket = ticket.Value }, ticket.Binding);

        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.Unauthorized, "session_required");
        await TenancyApi.AssertUnauthorizedAsync(noToken);
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task A_connect_ticket_of_a_provider_dropped_after_its_callback_is_honoured()   // criterion 13, spec 0012 → Provider tickets
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();
        SetManifest(AuthAppFactory.SelfServiceManifest + AuthAppFactory.PrivateBlock + "\nproviders: [microsoft]\n", withProviders: false);   // Google is no longer offered

        using var response = await SettingsApi.Connect(Client, session, ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Single(await LoginsOfAsync(userId));
    }

    [Fact]
    public async Task Two_connects_of_one_identity_at_once_add_one_login_and_both_answer_204()   // criterion 13, with parallel requests
    {
        var (userId, session) = await AccountAsync();
        var first = await ConnectTicketAsync();
        var second = await ConnectTicketAsync();   // another browser of the same person: its own auth_pt

        Task<HttpResponseMessage>[] pending;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            pending = [SettingsApi.Connect(Client, session, first), SettingsApi.Connect(Client, session, second)];
            await HeldLock.WaitForWaitersAsync(Factory, 2, pending);
            await held.ReleaseAsync();
        }

        var answers = await Task.WhenAll(pending);
        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode));
        Assert.Single(await LoginsOfAsync(userId));
        Assert.Single(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal(1, await NoticeCountAsync());
        Assert.Equal(0, await TicketCountAsync());
        foreach (var answer in answers)
        {
            answer.Dispose();
        }
    }

    [Fact]
    public async Task Two_connects_of_two_identities_of_one_provider_at_once_add_one_and_the_other_is_409_provider_in_use()   // criterion 13
    {
        var (userId, session) = await AccountAsync();
        var first = await ConnectTicketAsync();
        var second = await ConnectTicketAsync(GooglePerson(subject: OtherGoogleSubject));

        Task<HttpResponseMessage>[] pending;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            pending = [SettingsApi.Connect(Client, session, first), SettingsApi.Connect(Client, session, second)];
            await HeldLock.WaitForWaitersAsync(Factory, 2, pending);
            await held.ReleaseAsync();
        }

        var answers = await Task.WhenAll(pending);
        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], answers.Select(a => a.StatusCode).Order());
        await TenancyApi.AssertErrorAsync(answers.Single(a => a.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, "provider_in_use");
        Assert.Single(await LoginsOfAsync(userId));
        Assert.Equal(1, await TicketCountAsync());   // the refused one is given back
        foreach (var answer in answers)
        {
            answer.Dispose();
        }
    }

    [Fact]
    public async Task A_ticket_used_up_while_the_connect_waited_for_the_lock_is_400_invalid_ticket()   // guard lesson 1
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();
        await using var held = await HeldLock.AddressAsync(Factory, OlaNormalized);
        var pending = SettingsApi.Connect(Client, session, ticket);
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await InDbAsync(db => db.ProviderTickets.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        await held.ReleaseAsync();

        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Empty(await LoginsOfAsync(userId));
    }

    [Fact]
    public async Task A_ticket_that_expired_while_the_connect_waited_for_the_lock_is_400_invalid_ticket()   // guard lesson 11: the clock is read after the lock
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();   // lives 10 minutes; the window of the login ends sooner, so the window is opened again below
        Clock.Advance(TimeSpan.FromMinutes(9));
        using (var confirmed = await SettingsApi.Confirm(Client, session, new { password = UserPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        await using var held = await HeldLock.AddressAsync(Factory, OlaNormalized);
        var pending = SettingsApi.Connect(Client, session, ticket);   // the ticket and the window are live when the request arrives
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));   // the ticket ends while the request waits; the window (until minute 14) does not
        await held.ReleaseAsync();

        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Empty(await LoginsOfAsync(userId));
        Assert.Equal(1, await TicketCountAsync());   // a refusal deletes nothing
    }

    [Fact]
    public async Task A_membership_removed_while_the_connect_waited_is_403_no_membership_and_the_ticket_stays()   // under the lock
    {
        var (userId, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();
        await using var held = await HeldLock.AddressAsync(Factory, OlaNormalized);
        var pending = SettingsApi.Connect(Client, session, ticket);
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await RemoveMembershipAsync(userId);
        await held.ReleaseAsync();

        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Empty(await LoginsOfAsync(userId));
    }

    [Fact]
    public async Task No_ticket_binding_cookie_or_token_reaches_the_audit_log_or_the_log()   // guard lesson 4
    {
        var (_, session) = await AccountAsync();
        var ticket = await ConnectTicketAsync();
        using (var response = await SettingsApi.Connect(Client, session, ticket))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        string[] secrets = [ticket.Value, ticket.Binding, session.RefreshToken, session.AccessToken];
        await AssertNoSecretsAsync(secrets);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal));
    }
}
```

In `tests/Auth.IntegrationTests/ProviderTicketTests.cs`, add `using Microsoft.EntityFrameworkCore;` to its usings (the file has none, and
`A_connect_ticket_lives_ten_minutes` needs `SingleAsync`), and add before the class's last closing brace:

```csharp

    [Fact]
    public async Task A_connect_ticket_is_refused_by_the_exchange_the_link_and_the_finish_as_invalid_ticket()   // spec 0012, criterion 14
    {
        await CreateUserAsync(FakePeople.GoogleEmail, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=connect");
        Assert.Equal("connect", ticket.Purpose);   // the fragment says purpose=connect

        using var exchanged = await ProviderApi.Exchange(Client, ticket);
        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);
        using var finished = await ProviderApi.Finish(Client, ticket, "private");

        await TenancyApi.AssertErrorAsync(exchanged, HttpStatusCode.BadRequest, "invalid_ticket");
        await TenancyApi.AssertErrorAsync(linked, HttpStatusCode.BadRequest, "invalid_ticket");
        await TenancyApi.AssertErrorAsync(finished, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task A_connect_ticket_is_refused_by_the_account_deletion_and_the_confirmation_as_reauth_failed()   // spec 0012, criterion 14
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=connect");

        using var confirm = await SettingsApi.ConfirmWithTicket(Client, session, ticket);
        using var deleteAccount = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = ticket.Value }, ticket.Binding);

        await TenancyApi.AssertErrorAsync(confirm, HttpStatusCode.Forbidden, "reauth_failed");
        await TenancyApi.AssertErrorAsync(deleteAccount, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task A_connect_ticket_lives_ten_minutes()   // spec 0012 → Provider tickets
    {
        var before = StorableTimeNow();
        await TicketAsync("google", GooglePerson(), "?purpose=connect");

        var expires = await InDbAsync(db => db.ProviderTickets.Select(t => t.ExpiresAt).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(before + TimeSpan.FromMinutes(10), expires);
    }

    private DateTimeOffset StorableTimeNow() => Auth.Server.Email.StorableTime.Now(Clock);
```

(The company deletion refuses a `connect` ticket from Task 13 on, which adds its test to this class.)

In `tests/Auth.IntegrationTests/ProviderStartTests.cs`, in the list of bad queries (the theory with `"?purpose=login"`), after
`        "?purpose=reauth&purpose=reauth",` add

```csharp
        "?purpose=CONNECT",
        "?invite=" + Invite43 + "&purpose=connect",
        "?purpose=connect&purpose=reauth",
```

and in `The_start_reads_no_account_and_no_invitation`, after `    [InlineData("?purpose=reauth")]` add `    [InlineData("?purpose=connect")]`.
Add before the class's last closing brace:

```csharp

    [Theory]
    [InlineData("google")]
    [InlineData("microsoft")]
    public async Task A_connect_start_asks_for_the_account_chooser_and_its_callback_says_purpose_connect(string provider)   // spec 0012, criterion 14
    {
        var start = await StartAsync(provider, "?purpose=connect");

        Assert.Equal("select_account", start.Query["prompt"]);
        Assert.False(start.Query.ContainsKey("max_age"));
        var callback = await CallbackAsync(
            await AuthorizeAsync(start, provider == "google" ? GooglePerson() : PersonalMicrosoft()), start.StateCookie);
        Assert.Equal("connect", ProviderApi.FragmentOf(callback)["purpose"]);
    }
```

In `tests/Auth.IntegrationTests/ConfirmationWindowTests.cs`, add before the class's last closing brace:

```csharp

    [Fact]
    public async Task At_five_minutes_and_one_second_the_connect_is_403_confirmation_required_and_changes_nothing()   // criterion 8
    {
        var session = await SignInAsync("login");
        var ticket = await TicketAsync("google", GooglePerson(email: "login@firma.test", subject: "100000000000000000066"), "?purpose=connect");
        Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        using var response = await SettingsApi.Connect(Client, session, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "confirmation_required");
        Assert.Equal(0, await InDbAsync(db => db.UserLogins.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await TicketCountAsync());
    }
```

(`SignInAsync("login")` makes `login@firma.test` with a password and signs in, as the class's other tests do.)

In `tests/Auth.IntegrationTests/AccountConfirmTests.cs`, in the theory
`A_ticket_that_is_not_a_live_reauth_ticket_of_this_account_is_403_reauth_failed_and_is_left_alone`, add `    [InlineData("connect")]` after
`    [InlineData("sign_in")]`, and replace `            how == "sign_in" ? "" : "?purpose=reauth");` with
`            how switch { "sign_in" => "", "connect" => "?purpose=connect", _ => "?purpose=reauth" });`.

In `tests/Auth.IntegrationTests/SettingsMailTests.cs`, add before the class's last closing brace:

```csharp

    [Fact]
    public async Task A_connect_queues_provider_connected_and_a_connect_of_a_linked_identity_queues_nothing()   // criterion 22
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        using (var connected = await SettingsApi.Connect(Client, session, await TicketAsync("google", GooglePerson(), "?purpose=connect")))
        {
            Assert.Equal(HttpStatusCode.NoContent, connected.StatusCode);
        }

        using (var again = await SettingsApi.Connect(Client, session, await TicketAsync("google", GooglePerson(), "?purpose=connect")))
        {
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }

        var row = Assert.Single(await NoticesAsync());
        Assert.Equal((MailKind.ProviderLinked, "google"), (row.Kind, row.Provider));
        await DispatchAsync();
        Assert.Contains("Google", Assert.Single(Mail.Sent).Subject, StringComparison.Ordinal);
        _ = user;
    }
```

In `tests/Auth.IntegrationTests/OpenApiTests.cs`: in `Endpoints` replace `"POST /auth/providers/finish",` with
`"POST /auth/providers/finish", "POST /auth/providers/connect",`; in `CookiesRead` add `        ["POST /auth/providers/connect"] = ["auth_pt", "auth_rt"],`;
add `    [InlineData("POST /auth/providers/connect", "204,400,401,403,409,415,429")]` to the statuses theory and
`    [InlineData("POST /auth/providers/connect", "ticket")]` to the bodies theory; in `No_other_answer_says_it_sets_a_cookie` add
`, "POST /auth/providers/connect 204"` to `setting`; at the end of `The_error_codes_of_the_contract_are_in_the_descriptions` add

```csharp
        Assert.All(["invalid_request", "invalid_ticket"], code => Assert.Contains(code, Description(operations["POST /auth/providers/connect"], "400")));
        Assert.All(
            ["no_membership", "confirmation_required", "no_email", "email_not_verified", "address_mismatch"],
            code => Assert.Contains(code, Description(operations["POST /auth/providers/connect"], "403")));
        Assert.All(["provider_already_linked", "provider_in_use"], code => Assert.Contains(code, Description(operations["POST /auth/providers/connect"], "409")));
```

In `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`, replace `        Assert.Equal(37, operations.Count);   // spec 0012: the confirmation`
with `        Assert.Equal(38, operations.Count);   // spec 0012: the confirmation and the connect` and
`        Assert.Equal(25, covered);   // spec 0012: the confirmation` with `        Assert.Equal(26, covered);   // spec 0012: the confirmation and the connect`.

- [ ] **Step 2: See them fail.** Build, run `ProviderConnectTests`: `404` for every request (no route); the ticket tests: the start answers `400`
for `purpose=connect`.

- [ ] **Step 3: The purpose.** In `src/Auth.Server/Providers/ProviderPurposes.cs`, replace

```csharp
/// <summary>The purposes of a round trip as the fragment names them (spec 0011 → Settled 1): <c>sign_in</c>, <c>invite</c>, <c>reauth</c>.</summary>
public static class ProviderPurposes
{
    public const string SignInName = "sign_in";
    public const string InviteName = "invite";
    public const string ReauthName = "reauth";

    public static string NameOf(ProviderTicketPurpose purpose) => purpose switch
    {
        ProviderTicketPurpose.SignIn => SignInName,
        ProviderTicketPurpose.Invite => InviteName,
        ProviderTicketPurpose.Reauth => ReauthName,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown purpose."),
    };

    /// <summary>Exactly one of the three names.</summary>
```

with

```csharp
/// <summary>
/// The purposes of a round trip as the fragment names them (spec 0011 → Settled 1; spec 0012 → Provider tickets): <c>sign_in</c>, <c>invite</c>,
/// <c>reauth</c>, <c>connect</c>.
/// </summary>
public static class ProviderPurposes
{
    public const string SignInName = "sign_in";
    public const string InviteName = "invite";
    public const string ReauthName = "reauth";
    public const string ConnectName = "connect";

    public static string NameOf(ProviderTicketPurpose purpose) => purpose switch
    {
        ProviderTicketPurpose.SignIn => SignInName,
        ProviderTicketPurpose.Invite => InviteName,
        ProviderTicketPurpose.Reauth => ReauthName,
        ProviderTicketPurpose.Connect => ConnectName,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown purpose."),
    };

    /// <summary>Exactly one of the four names.</summary>
```

and replace

```csharp
            ReauthName => (true, ProviderTicketPurpose.Reauth),
            _ => (false, ProviderTicketPurpose.SignIn),
```

with

```csharp
            ReauthName => (true, ProviderTicketPurpose.Reauth),
            ConnectName => (true, ProviderTicketPurpose.Connect),
            _ => (false, ProviderTicketPurpose.SignIn),
```

(The callback names the purpose with `NameOf` and the ticket's lifetime is `ProviderTickets.Lifetime` for every purpose but `reauth`: a
`connect` ticket lives 10 minutes and its fragment says `purpose=connect` with no other change.)

In `src/Auth.Server/Providers/ProviderStartEndpoint.cs`, replace

```csharp
        else if (purposeText.Count == 1)
        {
            if (!string.Equals(purposeText[0], ProviderPurposes.ReauthName, StringComparison.Ordinal))
            {
                return ApiResults.InvalidRequest();
            }

            purpose = ProviderTicketPurpose.Reauth;
        }
```

with

```csharp
        else if (purposeText.Count == 1)
        {
            // reauth or connect (spec 0012); sign_in and invite are never named by this parameter.
            if (string.Equals(purposeText[0], ProviderPurposes.ReauthName, StringComparison.Ordinal))
            {
                purpose = ProviderTicketPurpose.Reauth;
            }
            else if (string.Equals(purposeText[0], ProviderPurposes.ConnectName, StringComparison.Ordinal))
            {
                purpose = ProviderTicketPurpose.Connect;
            }
            else
            {
                return ApiResults.InvalidRequest();
            }
        }
```

In `src/Auth.Server/Providers/ProviderExchangeEndpoint.cs`, replace

```csharp
        if (ticket is null || ticket.Purpose == ProviderTicketPurpose.Reauth)
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);   // unknown, used, expired, without its binding, or a re-authentication
        }
```

with

```csharp
        if (ticket is null || ticket.Purpose is ProviderTicketPurpose.Reauth or ProviderTicketPurpose.Connect)
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);   // unknown, used, expired, without its binding, a re-authentication or a connect
        }
```

In `src/Auth.Server/Api/ProviderRoutes.cs`, replace

```csharp
            .ReadsQuery(ProviderStartEndpoint.PurposeParameter, "`reauth`: the round trip confirms the deletion of the caller's private account.")
```

with

```csharp
            .ReadsQuery(
                ProviderStartEndpoint.PurposeParameter,
                "`reauth`: the round trip confirms who the caller is (a self-deletion, a company deletion, `POST /auth/account/confirm`); `connect`: "
                + "it connects a provider on the settings screen (spec 0012). Not together with `invite`.")
```

In the same file, in the description of the callback replace the span `&purpose=<sign_in|invite|reauth>` with `&purpose=<sign_in|invite|reauth|connect>` (the span
`(5 for `reauth`)` stays true: a `connect` ticket lives 10 minutes), and in the description of the exchange replace the span ``expired, of the purpose `reauth` or without its `auth_pt` is `400 invalid_ticket`.``
with ``expired, of the purpose `reauth` or `connect`, or without its `auth_pt` is `400 invalid_ticket`.``

- [ ] **Step 4: The path and the endpoint.** In `src/Auth.Server/Providers/ProviderPaths.cs`, replace

```csharp
    public const string Finish = "/auth/providers/finish";
```

with

```csharp
    public const string Finish = "/auth/providers/finish";

    /// <summary>Spec 0012: a plain POST behind the access token, never a token-endpoint request.</summary>
    public const string Connect = "/auth/providers/connect";
```

`src/Auth.Server/Providers/ProviderConnectEndpoint.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using OpenIddict.Abstractions;

namespace Auth.Server.Providers;

/// <summary>
/// <c>POST /auth/providers/connect</c> (spec 0012): the identity of a <c>connect</c> ticket becomes a login of the caller's account, inside the
/// confirmation window of this device. The checks run in the order of the contract: the body; the session and the membership; the ticket (a live
/// <c>connect</c> ticket of this browser); the window (the ticket stays usable); an identity that is this account's login already (<c>204</c>, the
/// ticket used up, nothing else); another account's (<c>409 provider_already_linked</c>); a login of this provider already
/// (<c>409 provider_in_use</c>); the ticket's address (none, unverified, another); then, in one transaction under the address lock where the session,
/// the membership and steps 5 to 7 are read again, the login, <c>provider.linked</c> (<c>via</c> <c>settings</c>), the ticket used up and the notice
/// "provider connected". Sessions are not touched.
/// </summary>
public static class ProviderConnectEndpoint
{
    private const string TicketField = "ticket";

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, MembershipReader memberships, IOpenIddictTokenManager tokens, AuditLog audit, MailNotices notices,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(clock);

        // 1. A ticket that is a string; a blank one is unknown at step 3.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields || !fields.TryAnyString(TicketField, out var ticketText))
        {
            return ApiResults.InvalidRequest();
        }

        // 2.
        var read = await SettingsCallers.ReadAsync(http, db, tokens, memberships, clock, http.RequestAborted);
        if (read.Caller is not { } caller)
        {
            return read.Refusal!;
        }

        // 3.
        ProviderBindingCookie.TryRead(http.Request, out var binding);
        var now = StorableTime.Now(clock);
        var ticket = await ProviderTickets.FindAsync(db, ticketText, binding, now, http.RequestAborted);
        if (ticket is not { Purpose: ProviderTicketPurpose.Connect })
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);
        }

        // 4. Nothing is used up: the ticket stays usable for a confirmation and a second try.
        if (!caller.Session.IsConfirmedAt(now))
        {
            return ApiResults.Error(TenancyErrors.ConfirmationRequired);
        }

        // 5. Connected already: the ticket is used up and nothing else is written (no row, no mail).
        if (await ProviderLogins.IsLoginOfAsync(db, caller.UserId, ticket.Provider, ticket.IdentityKey, http.RequestAborted))
        {
            return await UseUpAsync(http, db, ticketText, binding!, clock);
        }

        // 6, 7, 8.
        if (await RefusalOfLoginsAsync(db, caller.UserId, ticket, http.RequestAborted) is { } refusal)
        {
            return refusal;
        }

        if (ticket.Email is not { } email)
        {
            return ApiResults.Error(TenancyErrors.NoEmail);
        }

        if (!ticket.EmailVerified)
        {
            return ApiResults.Error(TenancyErrors.EmailNotVerified);
        }

        if (!EmailInput.IsSameAddress(caller.Email, email))
        {
            return ApiResults.Error(TenancyErrors.AddressMismatch);
        }

        return await ConnectAsync(http, db, memberships, audit, notices, clock, caller, ticketText, binding!);
    }

    /// <summary>9. Not the request's token from here on: a client that goes away must not leave the connect half done.</summary>
    private static async Task<IResult> ConnectAsync(
        HttpContext http, AuthDbContext db, MembershipReader memberships, AuditLog audit, MailNotices notices, TimeProvider clock, SettingsCaller caller,
        string ticketText, string binding)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        await AddressLock.AcquireAsync(db, caller.NormalizedEmail, CancellationToken.None);
        var now = StorableTime.Now(clock);   // after the lock: a ticket that ran out while this request waited is refused
        var again = await SettingsCallers.ReadAgainAsync(db, memberships, caller, now);
        if (again.Caller is not { } current)
        {
            return again.Refusal!;
        }

        if (!current.Session.IsConfirmedAt(now))
        {
            return ApiResults.Error(TenancyErrors.ConfirmationRequired);   // the rollback gives the ticket back: nothing was used up yet
        }

        var used = await ProviderTickets.ConsumeAsync(db, ticketText, binding, ProviderTicketPurpose.Connect, now, CancellationToken.None);
        if (used is null)
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);
        }

        // Steps 5 to 7 again: a second connect of the same identity is the 204 of step 5, of another identity of the provider the 409 of step 7.
        if (await ProviderLogins.IsLoginOfAsync(db, current.UserId, used.Provider, used.IdentityKey, CancellationToken.None))
        {
            await transaction.CommitAsync(CancellationToken.None);   // the ticket is used up, nothing else
            ProviderBindingCookie.Clear(http.Response);
            return ApiResults.NoContent();
        }

        if (await RefusalOfLoginsAsync(db, current.UserId, used, CancellationToken.None) is { } refusal)
        {
            return refusal;   // rolled back: the ticket stays
        }

        ProviderLogins.Add(db, current.UserId, used.Provider, used.IdentityKey);
        await notices.QueueAsync(db, MailKind.ProviderLinked, current.NormalizedEmail, used.Provider, now, CancellationToken.None);
        audit.Stage(SettingsCallers.Entry(
            AuditKinds.ProviderLinked, current, new Dictionary<string, object?> { ["provider"] = used.Provider, ["via"] = "settings" }));
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        ProviderBindingCookie.Clear(http.Response);
        notices.Notify();
        return ApiResults.NoContent();
    }

    /// <summary>Steps 6 and 7: the identity is another account's login, or this account has a login of the provider.</summary>
    private static async Task<IResult?> RefusalOfLoginsAsync(AuthDbContext db, Guid userId, ProviderTicket ticket, CancellationToken cancellationToken)
    {
        if (await ProviderLogins.FindAccountAsync(db, ticket.Provider, ticket.IdentityKey, cancellationToken) is { } owner && owner != userId)
        {
            return ApiResults.Error(TenancyErrors.ProviderAlreadyLinked);
        }

        return await ProviderLogins.HasLoginOfProviderAsync(db, userId, ticket.Provider, cancellationToken)
            ? ApiResults.Error(TenancyErrors.ProviderInUse)
            : null;
    }

    private static async Task<IResult> UseUpAsync(HttpContext http, AuthDbContext db, string ticketText, string binding, TimeProvider clock)
    {
        var used = await ProviderTickets.ConsumeAsync(
            db, ticketText, binding, ProviderTicketPurpose.Connect, StorableTime.Now(clock), CancellationToken.None);
        if (used is null)
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);   // used by another request meanwhile
        }

        ProviderBindingCookie.Clear(http.Response);
        return ApiResults.NoContent();
    }
}
```

`ProviderTickets.ConsumeAsync(db, ticket, binding, purpose, now, cancellationToken)` outside a transaction is one statement; inside the
transaction above its row stays deleted only if the transaction commits.

In `src/Auth.Server/Api/SettingsRoutes.cs`, add before `        return app;`:

```csharp
        app.MapPost(ProviderPaths.Connect, ProviderConnectEndpoint.HandleAsync)
            .RequireAuthorization()
            .WithTags("Providers")
            .WithDescription(
                "Connects the identity of a ticket of a round trip with `purpose=connect` to the caller's account, while the session of this device "
                + "is confirmed (`GET /auth/me` `confirmed_until`; else `403 confirmation_required`, and the ticket stays usable for its 10 minutes "
                + "as long as no other round trip replaces `auth_pt` in this browser). The checks, in this order: a bad body `400 invalid_request`; "
                + "the session `401 session_required`, no membership `403 no_membership`; a ticket that is unknown, used, expired, not of the purpose "
                + "`connect` or without its `auth_pt` `400 invalid_ticket`; the window; an identity that is already this account's login `204`, the "
                + "ticket used up and nothing else; another account's `409 provider_already_linked`; an account that already has a login of the "
                + "provider `409 provider_in_use` (one per provider: disconnect the old one first); no address `403 no_email`, an unverified one "
                + "`403 email_not_verified`, another address than the account's `403 address_mismatch`. Otherwise the login, `provider.linked` "
                + "(`via` `settings`), the ticket used up and the mail \"provider connected\" are one change; sessions are not touched.")
            .ReadsJson<ProviderTicketRequest>()
            .ReadsRefreshCookie(RefreshCookieRead)
            .ReadsCookie(ProviderBindingCookie.Name, "The binding of the ticket, set by the callback that made it.")
            .Produces(StatusCodes.Status204NoContent)
            .SetsHeader(StatusCodes.Status204NoContent, "Set-Cookie", $"`{ProviderBindingCookie.Name}` emptied, with `Max-Age=0`.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidTicket)
            .ProducesError(StatusCodes.Status401Unauthorized, TenancyErrors.SessionRequired)
            .ProducesError(
                StatusCodes.Status403Forbidden, TenancyErrors.NoMembership, TenancyErrors.ConfirmationRequired, TenancyErrors.NoEmail,
                TenancyErrors.EmailNotVerified, TenancyErrors.AddressMismatch)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.ProviderAlreadyLinked, TenancyErrors.ProviderInUse)
            .ProducesTooManyAttempts();
```

(`ProducesTooManyAttempts` documents the per-IP `429` of the `login` policy, which Task 14 assigns; the statuses theory lists `429` now.)

- [ ] **Step 5: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ProviderConnectTests -class Auth.IntegrationTests.ProviderTicketTests -class Auth.IntegrationTests.ProviderStartTests -class Auth.IntegrationTests.ProviderCallbackTests -class Auth.IntegrationTests.ProviderExchangeTests -class Auth.IntegrationTests.ConfirmationWindowTests -class Auth.IntegrationTests.AccountConfirmTests -class Auth.IntegrationTests.SettingsMailTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests -class Auth.IntegrationTests.JsonCharsetTests`
passes. Mutate and see a test fail, then restore: skip the window check under the lock and move step 4 after step 6
(`The_checks_run_in_the_order_of_the_contract` fails); swap the `email_not_verified` and `address_mismatch` checks of step 8
(`An_unverified_address_that_is_also_not_the_accounts_is_email_not_verified_not_address_mismatch` fails); read the clock before the lock
(`A_ticket_that_expired_while_the_connect_waited_for_the_lock_is_400_invalid_ticket` fails with a `204`); drop the re-read of step 7 under the lock
(`Two_connects_of_two_identities_of_one_provider_at_once_add_one_and_the_other_is_409_provider_in_use` fails with a second login).

- [ ] **Step 6: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): connect a provider from the settings screen with a connect ticket`.

---

### Task 10: `POST /auth/account/logins/unlink` and the renewed session

**Files:**
- Create: `src/Auth.Server/Account/SettingsRequestHandler.cs`, `src/Auth.Server/Account/SessionRenewal.cs`, `src/Auth.Server/Account/LoginUnlinkEndpoint.cs`
- Modify: `src/Auth.Server/Tokens/OpenIddictSetup.cs`, `src/Auth.Server/Login/LoginResponseShaper.cs`, `src/Auth.Server/Api/SettingsRoutes.cs`,
  `src/Auth.Server/Api/AccountContracts.cs`
- Create (tests): `tests/Auth.IntegrationTests/ProviderUnlinkTests.cs`, `tests/Auth.IntegrationTests/Infrastructure/SessionRowFault.cs`
- Modify (tests): `tests/Auth.IntegrationTests/ConfirmationWindowTests.cs`, `tests/Auth.IntegrationTests/SettingsMailTests.cs`,
  `tests/Auth.IntegrationTests/LoginScopeTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`, `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`

**Interfaces:**
- Consumes: `SettingsCallers` (Task 7), `MailNotices` (Task 8), `AtomicSignInResult(..., session)` and `SessionPrincipal.Create(..., start)`
  (Task 1), `DeviceSession` (Task 2), `ProviderCatalog.IsOffered`.
- Produces: `SettingsRequestHandler` (`UnlinkPath`, `PasswordPath`, `Paths`, `IsSettingsTokenPath(PathString)`, `Descriptor` at the login's order
  `+ 400`); `SessionRenewal.Via` (`session_renewal`), `SessionRenewal.CanRenew(DeviceSession, DateTimeOffset)`,
  `SessionRenewal.EndSessionsAsync(IOpenIddictTokenManager, IOpenIddictAuthorizationManager, Guid, CancellationToken)`,
  `SessionRenewal.Issue(HttpContext, ApplicationUser, SettingsCaller, IDbContextTransaction, string audience, TimeProvider, Action<HttpContext>?)`;
  `LoginUnlinkEndpoint.HandleAsync`; `UnlinkRequest(string Provider)`.

**Contract counts:** `OpenApiTests`: `Endpoints` gains `POST /auth/account/logins/unlink`; statuses `200,400,401,403,404,409,415,429`; body
`provider`; cookie `auth_rt`; the bearer token; its `200` sets the refresh cookie. `OpenApiHardeningTests`: **38 → 39** operations,
**26 → 27** under the charset guard. `LoginScopeTests`: the two settings paths are shaped as a login.

**Decisions the plan takes here:**
- One placeholder handler (`SettingsRequestHandler`, at `+400`, between the provider sign-ins' `+350` and the guard's `+500`) claims both token
  paths of the settings screen, the password change's included (Task 12 maps its route); the shaper learns both now (U11).
- The renewal is one helper for the unlink and the password change: the cap is checked first (`SessionRenewal.CanRenew`, so that a session past
  it changes nothing), every refresh token and authorization of the account is revoked, then `AtomicSignInResult` issues the new session in the
  same transaction with the old start and window and `login.succeeded` (`via` `session_renewal`). Revoking first is required: a revocation by
  subject would also revoke what the transaction issued before it.
- The lock order of the unlink is: address lock, the login rows (deleted), the refresh tokens and authorizations (revoked), then the account's
  row under `FOR KEY SHARE` (the sign-in). A reset updates the account's row with a `FOR NO KEY UPDATE` lock, which does not conflict with
  `FOR KEY SHARE`, so the two never wait for each other in a cycle.
- `provider` is read with `TryAnyString`: a blank string is "not a provider name", `404 not_found`, like any unknown one. The names compare
  ordinally (`GOOGLE` is unknown).

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/Infrastructure/SessionRowFault.cs` (used again by Task 12):

```csharp
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Throws on the insert of a session's row in <c>SessionStates</c> while armed: the last write of the sign-in's transaction, so that a renewal
/// fails after its logins are deleted (or its hash set) and its sessions revoked. Register it with
/// <c>Factory.WithServices(services => services.ConfigureDbContext&lt;AuthDbContext&gt;(options => options.AddInterceptors(fault)))</c> before the host starts.
/// </summary>
internal sealed class SessionRowFault : DbCommandInterceptor
{
    public bool Armed { get; set; }

    public bool Fired { get; private set; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Fail(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Fail(command);
        return ValueTask.FromResult(result);
    }

    private void Fail(DbCommand command)
    {
        if (Armed && command.CommandText.Contains("INSERT INTO \"SessionStates\"", StringComparison.Ordinal))
        {
            Fired = true;
            throw new InvalidOperationException("the renewed session cannot be written, for the test");
        }
    }
}
```

`tests/Auth.IntegrationTests/ProviderUnlinkTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → <c>POST /auth/account/logins/unlink</c> (criteria 16 and 17), in the order of its checks.</summary>
public sealed class ProviderUnlinkTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string OlaNormalized = "OLA@GMAIL.TEST";

    private Task<List<string>> ProvidersOfAsync(Guid userId) =>
        InDbAsync(db => db.UserLogins.AsNoTracking().Where(l => l.UserId == userId).Select(l => l.LoginProvider).Order()
            .ToListAsync(TestContext.Current.CancellationToken));

    /// <summary>The values of the one session whose authorization is still valid.</summary>
    private Task<SessionState> LiveSessionStateAsync() =>
        InDbAsync(db => (
            from state in db.SessionStates.AsNoTracking()
            join authorization in db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking() on state.AuthorizationId equals authorization.Id
            where authorization.Status == OpenIddictConstants.Statuses.Valid
            select state).SingleAsync(TestContext.Current.CancellationToken));

    /// <summary>An account with a password and a Google login, signed in on this device a moment ago (its window open).</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> PasswordAccountAsync()
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        return (user.Id, await SessionApi.LoginAsync(Client, Ola, UserPassword));
    }

    /// <summary>A private account made with Google, then given a Microsoft login, signed in by the finish (its window open).</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> PasswordlessWithTwoAsync()
    {
        var made = await PrivateThroughProviderAsync("google", GooglePerson());
        await LinkAsync(made.UserId, "microsoft", FakePeople.PersonalTenant + ":" + FakePeople.PersonalObjectId);
        return made;
    }

    [Fact]
    public async Task An_unlink_with_an_open_window_deletes_the_login_signs_the_other_devices_out_and_renews_this_one()   // criterion 16, Review Focus 1
    {
        var (userId, here) = await PasswordAccountAsync();
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        await LockoutApi.FailAsync(Client, Clock, Ola, 2);
        using (var forgot = await AccountApi.Forgot(Client, Ola))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        await DispatchAsync();   // an email link of the account exists now
        var before = await InDbAsync(db => db.SessionStates.AsNoTracking().OrderBy(s => s.SessionStart).FirstAsync(TestContext.Current.CancellationToken));
        var window = await SettingsApi.ConfirmedUntilAsync(Client, here);

        using var response = await SettingsApi.Unlink(Client, here, "google");

        var renewed = await SessionApi.ReadSessionAsync(response);
        Assert.Equal(["access_token", "status"], await JsonNamesAsync(response));
        Assert.Equal(userId.ToString(), AccessTokens.Text(renewed.AccessToken, "sub"));
        Assert.Empty(await ProvidersOfAsync(userId));
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, there.RefreshToken));
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, here.RefreshToken));
        Assert.Equal(window, await SettingsApi.ConfirmedUntilAsync(Client, renewed));   // the old window, unchanged
        var live = await LiveSessionStateAsync();
        Assert.Equal((before.SessionStart, before.ConfirmedUntil), (live.SessionStart, live.ConfirmedUntil));

        var unlinked = await SingleAsync(AuditKinds.ProviderUnlinked);
        Assert.Equal((userId, userId, Ola), (unlinked.ActorUserId!.Value, unlinked.SubjectUserId!.Value, unlinked.SubjectEmail));
        Assert.Equal(await DevCompanyIdAsync(), unlinked.OrgId);
        Assert.Equal(["provider"], AuditApi.DetailNames(unlinked));
        Assert.Equal("google", AuditApi.Text(unlinked, "provider"));
        var renewal = (await AuditAsync(AuditKinds.LoginSucceeded))[^1];
        Assert.Equal(["via"], AuditApi.DetailNames(renewal));
        Assert.Equal("session_renewal", AuditApi.Text(renewal, "via"));
        Assert.Equal(1, await InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.ProviderUnlinked, TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == userId, TestContext.Current.CancellationToken)));   // left alone
        Assert.Equal(1, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));   // left alone

        // The new session works, and the Google identity no longer signs in to the account.
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, renewed.RefreshToken)).StatusCode);
        using var exchanged = await ProviderApi.Exchange(Client, await TicketAsync("google", GooglePerson()));
        Assert.Equal("link", (await TenancyApi.ReadOkAsync(exchanged)).GetProperty("next").GetString());
    }

    private static async Task<string[]> JsonNamesAsync(HttpResponseMessage response)
    {
        using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task A_renewed_session_keeps_the_cap_of_its_start_and_one_past_its_cap_is_401_session_required()   // criterion 17, Review Focus 1
    {
        var (userId, session) = await PasswordAccountAsync();
        Clock.Advance(TimeSpan.FromDays(13));
        session = await SessionApi.RefreshOk(Client, session.RefreshToken);
        Clock.Advance(TimeSpan.FromDays(13));
        session = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using (var confirmed = await SettingsApi.Confirm(Client, session, new { password = UserPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        using var response = await SettingsApi.Unlink(Client, session, "google");

        var renewed = await SessionApi.ReadSessionAsync(response);
        Assert.InRange(renewed.Cookie.MaxAge!.Value, TimeSpan.FromDays(4) - TimeSpan.FromSeconds(2), TimeSpan.FromDays(4));   // 30 days from the start

        // One past its cap: the stored start is more than 30 days ago.
        await LinkAsync(userId, "google", FakePeople.GoogleSubject);
        await InDbAsync(db => db.SessionStates.ExecuteUpdateAsync(
            s => s.SetProperty(state => state.SessionStart, StorableTime.Now(Clock) - TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken));
        var rows = (await AuditAsync()).Count;

        using var past = await SettingsApi.Unlink(Client, renewed, "google");

        await TenancyApi.AssertErrorAsync(past, HttpStatusCode.Unauthorized, "session_required");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Equal(rows, (await AuditAsync()).Count);
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, renewed.RefreshToken)).StatusCode);   // nothing was revoked
    }

    [Fact]
    public async Task A_failure_after_the_sessions_ended_rolls_the_whole_unlink_back()   // guard lesson 9: the sign-in's transaction owns the change
    {
        var fault = new SessionRowFault();
        Factory.WithServices(services => services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(fault)));
        var (userId, here) = await PasswordAccountAsync();
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        fault.Armed = true;

        using var failed = await SettingsApi.Unlink(Client, here, "google");

        Assert.True(fault.Fired, "The renewed session never reached its write.");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.False(failed.Headers.Contains("Set-Cookie"));   // the refresh cookie is not replaced
        Assert.Equal(["google"], await ProvidersOfAsync(userId));   // the login row is still there
        Assert.Empty(await AuditAsync(AuditKinds.ProviderUnlinked));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.ProviderUnlinked, TestContext.Current.CancellationToken)));
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, there.RefreshToken)).StatusCode);   // no session was revoked
        fault.Armed = false;
        here = await SessionApi.RefreshOk(Client, here.RefreshToken);

        using var again = await SettingsApi.Unlink(Client, here, "google");   // nothing was used up: the same request, now that nothing fails

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Empty(await ProvidersOfAsync(userId));
    }

    [Fact]
    public async Task A_closed_window_is_403_confirmation_required_and_changes_nothing()   // criterion 17
    {
        var (userId, session) = await PasswordAccountAsync();
        Clock.Advance(TimeSpan.FromMinutes(6));

        using var response = await SettingsApi.Unlink(Client, session, "google");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "confirmation_required");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderUnlinked));
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, session.RefreshToken)).StatusCode);
    }

    [Theory]
    [InlineData("gitlab")]
    [InlineData("GOOGLE")]
    [InlineData("")]
    [InlineData("github")]   // a provider name the account has no login of
    [InlineData("microsoft")]
    public async Task A_name_that_is_no_provider_or_no_login_of_the_account_is_404_not_found(string provider)   // criterion 17
    {
        var (userId, session) = await PasswordAccountAsync();

        using var response = await SettingsApi.Unlink(Client, session, provider);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
    }

    [Fact]
    public async Task The_only_login_of_an_account_without_a_password_is_409_last_login()   // criterion 17
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        using var response = await SettingsApi.Unlink(Client, session, "google");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "last_login");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderUnlinked));
    }

    [Fact]
    public async Task The_last_offered_login_is_409_last_login_and_a_login_no_longer_offered_can_go_while_a_way_in_remains()   // criterion 17
    {
        var (userId, session) = await PasswordlessWithTwoAsync();
        SetManifest(AuthAppFactory.SelfServiceManifest + AuthAppFactory.PrivateBlock + "\nproviders: [google]\n", withProviders: false);   // Microsoft is no longer offered

        using (var last = await SettingsApi.Unlink(Client, session, "google"))
        {
            await TenancyApi.AssertErrorAsync(last, HttpStatusCode.Conflict, "last_login");
        }

        using var gone = await SettingsApi.Unlink(Client, session, "microsoft");

        Assert.Equal(HttpStatusCode.OK, gone.StatusCode);
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
    }

    [Fact]
    public async Task Two_logins_of_one_provider_from_before_this_version_both_go()   // criterion 17
    {
        var (userId, session) = await PasswordAccountAsync();
        await LinkAsync(userId, "google", "100000000000000000077");

        using var response = await SettingsApi.Unlink(Client, session, "google");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ProvidersOfAsync(userId));
        Assert.Single(await AuditAsync(AuditKinds.ProviderUnlinked));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_unlinks_of_the_two_logins_at_once_leave_one_login(bool twoSessions)   // criterion 17, with parallel requests
    {
        var (userId, here) = await PasswordlessWithTwoAsync();
        var there = here;
        if (twoSessions)
        {
            using var signedIn = await ProviderApi.Exchange(Client, await TicketAsync("microsoft", PersonalMicrosoft(email: Ola)));
            there = await SessionApi.ReadSessionAsync(signedIn);
        }

        Task<HttpResponseMessage>[] pending;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            pending = [SettingsApi.Unlink(Client, here, "google"), SettingsApi.Unlink(Client, there, "microsoft")];
            await HeldLock.WaitForWaitersAsync(Factory, 2, pending);
            await held.ReleaseAsync();
        }

        var answers = await Task.WhenAll(pending);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Unauthorized], answers.Select(a => a.StatusCode).Order());
        await TenancyApi.AssertErrorAsync(answers.Single(a => a.StatusCode == HttpStatusCode.Unauthorized), HttpStatusCode.Unauthorized, "session_required");
        Assert.Single(await ProvidersOfAsync(userId));
        Assert.Single(await AuditAsync(AuditKinds.ProviderUnlinked));
        foreach (var answer in answers)
        {
            answer.Dispose();
        }
    }

    [Fact]
    public async Task Without_the_refresh_cookie_it_is_401_session_required_and_without_a_token_the_plain_401()   // criterion 17
    {
        var (userId, session) = await PasswordAccountAsync();

        using var noCookie = await SettingsApi.PostAs(Client, SettingsApi.UnlinkPath, session.AccessToken, null, new { provider = "google" });
        using var noToken = await SettingsApi.PostAs(Client, SettingsApi.UnlinkPath, null, session.RefreshToken, new { provider = "google" });

        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.Unauthorized, "session_required");
        await TenancyApi.AssertUnauthorizedAsync(noToken);
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
    }

    [Fact]
    public async Task A_removed_membership_is_403_no_membership_and_nothing_is_written()   // criterion 17
    {
        var (userId, session) = await PasswordAccountAsync();
        await RemoveMembershipAsync(userId);

        using var response = await SettingsApi.Unlink(Client, session, "google");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderUnlinked));
    }

    [Fact]
    public async Task A_membership_removed_while_the_unlink_waited_is_403_no_membership()   // under the lock
    {
        var (userId, session) = await PasswordAccountAsync();
        await using var held = await HeldLock.AddressAsync(Factory, OlaNormalized);
        var pending = SettingsApi.Unlink(Client, session, "google");
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await RemoveMembershipAsync(userId);
        await held.ReleaseAsync();

        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
    }

    [Fact]
    public async Task A_password_removed_while_the_unlink_waited_makes_it_409_last_login()   // step 5 again under the lock
    {
        var (userId, session) = await PasswordAccountAsync();
        await using var held = await HeldLock.AddressAsync(Factory, OlaNormalized);
        var pending = SettingsApi.Unlink(Client, session, "google");
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await InDbAsync(db => db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken));
        await held.ReleaseAsync();

        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "last_login");
        Assert.Equal(["google"], await ProvidersOfAsync(userId));
    }

    [Fact]
    public async Task The_checks_run_in_the_order_of_the_contract()   // guard lesson 9
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        using (var unknown = await SettingsApi.Unlink(Client, session, "gitlab"))
        {
            await TenancyApi.AssertErrorAsync(unknown, HttpStatusCode.NotFound, "not_found");   // 4 before 5
        }

        Clock.Advance(TimeSpan.FromMinutes(6));
        session = await SessionApi.RefreshOk(Client, session.RefreshToken);
        using var closed = await SettingsApi.Unlink(Client, session, "gitlab");
        await TenancyApi.AssertErrorAsync(closed, HttpStatusCode.Forbidden, "confirmation_required");   // 3 before 4
    }

    public static TheoryData<string> BadBodies => new() { "not json", "{}", """["provider"]""", """{"provider":5}""", """{"provider":null}""" };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request_before_the_session(string body)   // criterion 17, step 1
    {
        var (_, session) = await PasswordAccountAsync();

        using var response = await SettingsApi.PostRaw(Client, SettingsApi.UnlinkPath, session with { RefreshToken = "" }, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task No_token_or_cookie_reaches_the_audit_log_or_the_log()   // guard lesson 4
    {
        var (_, session) = await PasswordAccountAsync();

        using var response = await SettingsApi.Unlink(Client, session, "google");
        var renewed = await SessionApi.ReadSessionAsync(response);

        string[] secrets = [session.RefreshToken, session.AccessToken, renewed.RefreshToken, renewed.AccessToken];
        await AssertNoSecretsAsync(secrets);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal));
    }
}
```

In `tests/Auth.IntegrationTests/ConfirmationWindowTests.cs`, add before the class's last closing brace:

```csharp

    [Fact]
    public async Task At_five_minutes_and_one_second_the_unlink_is_403_confirmation_required_and_changes_nothing()   // criterion 8
    {
        var session = await SignInAsync("login");
        var userId = await InDbAsync(db => db.Users.Where(u => u.NormalizedEmail == "LOGIN@FIRMA.TEST").Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken));
        await LinkAsync(userId, "google", "100000000000000000066");
        Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        using var response = await SettingsApi.Unlink(Client, session, "google");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "confirmation_required");
        Assert.Equal(1, await InDbAsync(db => db.UserLogins.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task After_a_renewal_the_old_session_names_nothing()   // criterion 9
    {
        var session = await SignInAsync("login");
        var userId = await InDbAsync(db => db.Users.Where(u => u.NormalizedEmail == "LOGIN@FIRMA.TEST").Select(u => u.Id).SingleAsync(TestContext.Current.CancellationToken));
        await LinkAsync(userId, "google", "100000000000000000066");

        using var response = await SettingsApi.Unlink(Client, session, "google");
        var renewed = await SessionApi.ReadSessionAsync(response);

        Assert.Null(await SettingsApi.ConfirmedUntilAsync(Client, renewed.AccessToken, session.RefreshToken));
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, renewed));
    }
```

In `tests/Auth.IntegrationTests/SettingsMailTests.cs`, add before the class's last closing brace:

```csharp

    [Fact]
    public async Task An_unlink_queues_provider_disconnected_naming_the_provider()   // criterion 22
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "github", "70000001");
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);

        using (var unlinked = await SettingsApi.Unlink(Client, session, "github"))
        {
            Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        }

        var row = Assert.Single(await NoticesAsync());
        Assert.Equal((MailKind.ProviderUnlinked, "github"), (row.Kind, row.Provider));
        await DispatchAsync();
        var mail = Assert.Single(Mail.Sent);
        Assert.Contains("GitHub", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("disconnected", mail.TextBody, StringComparison.Ordinal);
    }
```

In `tests/Auth.IntegrationTests/LoginScopeTests.cs`, in the theory `ShouldShape_requires_a_password_grant_on_the_login_or_completion_path`, after
`    [InlineData("password", "/auth/providers/google/callback", false)]` add

```csharp
    [InlineData("password", "/auth/account/logins/unlink", true)]   // spec 0012: the renewals answer as a login
    [InlineData("password", "/auth/account/password", true)]
    [InlineData("password", "/AUTH/ACCOUNT/PASSWORD/", true)]
    [InlineData("password", "/auth/account/confirm", false)]
    [InlineData("password", "/auth/providers/connect", false)]
```

In `tests/Auth.IntegrationTests/OpenApiTests.cs`: in `Endpoints` replace `"POST /auth/account/confirm",` with
`"POST /auth/account/confirm", "POST /auth/account/logins/unlink",`; in `CookiesRead` add `        ["POST /auth/account/logins/unlink"] = ["auth_rt"],`;
add `    [InlineData("POST /auth/account/logins/unlink", "200,400,401,403,404,409,415,429")]` and `    [InlineData("POST /auth/account/logins/unlink", "provider")]`
to the statuses and bodies theories; add `    [InlineData("POST /auth/account/logins/unlink", "200")]` to the theory
`The_answers_that_set_or_clear_the_refresh_cookie_say_so`; add `, "POST /auth/account/logins/unlink 200"` to `setting` in `No_other_answer_says_it_sets_a_cookie`; at
the end of `The_error_codes_of_the_contract_are_in_the_descriptions` add

```csharp
        Assert.Contains("session_required", Description(operations["POST /auth/account/logins/unlink"], "401"));
        Assert.All(["no_membership", "confirmation_required"], code => Assert.Contains(code, Description(operations["POST /auth/account/logins/unlink"], "403")));
        Assert.Contains("not_found", Description(operations["POST /auth/account/logins/unlink"], "404"));
        Assert.Contains("last_login", Description(operations["POST /auth/account/logins/unlink"], "409"));
```

In `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`, replace the two counts `38` and `26` (the lines Task 9 left) with `39` and `27`, the
comments `// spec 0012: the confirmation, the connect and the unlink`.

- [ ] **Step 2: See them fail.** Build, run `ProviderUnlinkTests`: `404` for every request.

- [ ] **Step 3: The placeholder.** `src/Auth.Server/Account/SettingsRequestHandler.cs`:

```csharp
using Auth.Server.Login;
using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Account;

/// <summary>
/// Makes <c>POST /auth/account/logins/unlink</c> and <c>POST /auth/account/password</c> (spec 0012) token-endpoint requests of OpenIddict in
/// pass-through mode with a placeholder request, as the provider sign-ins are, so that their answer can hand this device a renewed session. It
/// never reads the body; each endpoint does, and nothing a client sends reaches the OpenIddict request. Their routes require the access token.
/// </summary>
public sealed class SettingsRequestHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ExtractTokenRequestContext>
{
    public const string UnlinkPath = "/auth/account/logins/unlink";
    public const string PasswordPath = "/auth/account/password";

    private const string Placeholder = "session-renewal";

    public static IReadOnlyList<string> Paths { get; } = [UnlinkPath, PasswordPath];

    /// <summary>The two paths, case-insensitive and with an optional trailing slash, as OpenIddict matches a token endpoint.</summary>
    internal static bool IsSettingsTokenPath(PathString path) =>
        Paths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.Equals(p + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>After the provider sign-ins (+350) and before <see cref="UnhandledTokenRequestGuard"/> (+500).</summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ExtractTokenRequestContext>()
            .AddFilter<OpenIddictServerAspNetCoreHandlerFilters.RequireHttpRequest>()
            .UseSingletonHandler<SettingsRequestHandler>()
            .SetOrder(JsonLoginRequestHandler.Descriptor.Order + 400)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Transaction.GetHttpRequest()
            ?? throw new InvalidOperationException("The token endpoint was reached without an ASP.NET Core request.");

        if (IsSettingsTokenPath(request.Path))
        {
            context.Request = new OpenIddictRequest { GrantType = GrantTypes.Password, Username = Placeholder, Password = Placeholder };
        }

        return ValueTask.CompletedTask;
    }
}
```

In `src/Auth.Server/Tokens/OpenIddictSetup.cs`, replace

```csharp
                        ProviderPaths.Link.TrimStart('/'),
                        ProviderPaths.Finish.TrimStart('/'))
```

with

```csharp
                        ProviderPaths.Link.TrimStart('/'),
                        ProviderPaths.Finish.TrimStart('/'),
                        SettingsRequestHandler.UnlinkPath.TrimStart('/'),
                        SettingsRequestHandler.PasswordPath.TrimStart('/'))
```

and replace

```csharp
                    .AddEventHandler(ProviderRequestHandler.Descriptor)
                    .AddEventHandler(UnhandledTokenRequestGuard.Descriptor);
```

with

```csharp
                    .AddEventHandler(ProviderRequestHandler.Descriptor)
                    .AddEventHandler(SettingsRequestHandler.Descriptor)
                    .AddEventHandler(UnhandledTokenRequestGuard.Descriptor);
```

(add `using Auth.Server.Account;` if the file lacks it). In `src/Auth.Server/Login/LoginResponseShaper.cs`, replace

```csharp
            || ProviderRequestHandler.IsProviderTokenPath(path));   // spec 0011: the sign-ins through a provider answer as a login
```

with

```csharp
            || ProviderRequestHandler.IsProviderTokenPath(path)   // spec 0011: the sign-ins through a provider answer as a login
            || SettingsRequestHandler.IsSettingsTokenPath(path));   // spec 0012: so do the renewals of the unlink and the password change
```

(add `using Auth.Server.Account;`).

- [ ] **Step 4: The renewal.** `src/Auth.Server/Account/SessionRenewal.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Login;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore.Storage;
using OpenIddict.Abstractions;

namespace Auth.Server.Account;

/// <summary>
/// A renewal (spec 0012 → Sessions): the unlink and the password change end every session of the account and hand this device a new one, whose
/// principal is built after the change, with the stored start (so that the 30-day cap counts from there) and the old window. A session past its
/// cap cannot be renewed: the endpoint checks <see cref="CanRenew"/> before it changes anything.
/// </summary>
internal static class SessionRenewal
{
    public const string Via = "session_renewal";

    public static bool CanRenew(DeviceSession session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);

        return SessionPolicy.RemainingLifetime(session.Start, now) is not null;
    }

    /// <summary>
    /// Every refresh token and authorization of the account revoked, in the caller's transaction: what <c>AccountSessions.EndAllAsync</c> does to
    /// the sessions, without the email links and the streak. It runs before the new session is issued, which it would revoke too.
    /// </summary>
    public static async Task EndSessionsAsync(
        IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(authorizations);

        var subject = userId.ToString();
        await tokens.RevokeBySubjectAsync(subject, cancellationToken);
        await authorizations.RevokeBySubjectAsync(subject, cancellationToken);
    }

    /// <summary>
    /// The answer of a renewal, in <paramref name="transaction"/>, which the result owns from now on: a login's answer, <c>login.succeeded</c>
    /// with <c>via</c> <c>session_renewal</c>, the new session with the start and the window of <paramref name="caller"/>'s. An account gone
    /// meanwhile is <c>401 session_required</c>.
    /// </summary>
    public static IResult Issue(
        HttpContext http, ApplicationUser account, SettingsCaller caller, IDbContextTransaction transaction, string audience, TimeProvider clock,
        Action<HttpContext>? whenCommitted)
    {
        ArgumentNullException.ThrowIfNull(caller);

        return new AtomicSignInResult(
            SessionPrincipal.Create(http, account, caller.Tenant, audience, clock, caller.Session.Start),
            SettingsCallers.Entry(AuditKinds.LoginSucceeded, caller, new Dictionary<string, object?> { ["via"] = Via }),
            transaction,
            whenGone: () => Task.FromResult(ApiResults.Error(TenancyErrors.SessionRequired)),
            whenCommitted,
            new SessionValues(caller.Session.Start, caller.Session.ConfirmedUntil));
    }
}
```

`SessionPrincipal.Create` reads the clock once more; under the endpoint's lock the time between `CanRenew` and it is milliseconds, and a
session that crosses its cap in them throws (`500`). That is accepted (a 30-day session's last millisecond); do not add a second check.

- [ ] **Step 5: The unlink.** In `src/Auth.Server/Api/AccountContracts.cs`, add at the end:

```csharp

/// <summary>Request of <c>POST /auth/account/logins/unlink</c> (spec 0012): the provider whose logins the account loses.</summary>
public sealed record UnlinkRequest(string Provider);
```

`src/Auth.Server/Account/LoginUnlinkEndpoint.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Providers;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Account;

/// <summary>
/// <c>POST /auth/account/logins/unlink</c> (spec 0012): the account loses its logins of one provider, inside the confirmation window of this device,
/// never its last way in (the password, or a login of an offered provider). The checks run in the order of the contract: the body; the session
/// and the membership; the window; a provider name the account has a login of (<c>404 not_found</c>); a way in left (<c>409 last_login</c>);
/// then, in one transaction under the address lock where the session, the membership and steps 4 and 5 are read again and the cap of the
/// session is checked, the login rows deleted (waiting for a sign-in that holds them), every session ended, <c>provider.unlinked</c>, the notice
/// "provider disconnected" and a renewed session for this device, answered as a login.
/// </summary>
public static class LoginUnlinkEndpoint
{
    private const string ProviderField = "provider";

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, IOpenIddictTokenManager tokens,
        IOpenIddictAuthorizationManager authorizations, ProviderCatalog catalog, AuditLog audit, MailNotices notices, IOptions<TokenOptions> tokenOptions,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(authorizations);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(tokenOptions);
        ArgumentNullException.ThrowIfNull(clock);

        // Without it OpenIddict cannot sign in here: fail at once, as the exchange does.
        if (http.GetOpenIddictServerRequest() is null)
        {
            throw new InvalidOperationException("The unlink was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        }

        // 1.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields || !fields.TryAnyString(ProviderField, out var provider))
        {
            return ApiResults.InvalidRequest();
        }

        // 2.
        var read = await SettingsCallers.ReadAsync(http, db, tokens, memberships, clock, http.RequestAborted);
        if (read.Caller is not { } caller)
        {
            return read.Refusal!;
        }

        // 3.
        if (!caller.Session.IsConfirmedAt(StorableTime.Now(clock)))
        {
            return ApiResults.Error(TenancyErrors.ConfirmationRequired);
        }

        // 4 and 5.
        if (await RefusalAsync(db, catalog, caller, provider, http.RequestAborted) is { } refusal)
        {
            return refusal;
        }

        // 6. Not the request's token from here on: a client that goes away must not leave the unlink half done.
        var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        try
        {
            await AddressLock.AcquireAsync(db, caller.NormalizedEmail, CancellationToken.None);
            var now = StorableTime.Now(clock);
            var again = await SettingsCallers.ReadAgainAsync(db, memberships, caller, now);
            if (again.Caller is not { } current)
            {
                await transaction.DisposeAsync();
                return again.Refusal!;   // a second unlink at once finds its session ended by the first
            }

            var late = await RefusalAsync(db, catalog, current, provider, CancellationToken.None);
            var account = late is null && SessionRenewal.CanRenew(current.Session, now) ? await users.FindByIdAsync(current.UserId.ToString()) : null;
            if (late is not null || account is null)
            {
                await transaction.DisposeAsync();
                return late ?? ApiResults.Error(TenancyErrors.SessionRequired);
            }

            // Waits for a sign-in that holds one of these rows (spec 0012 → Sign-in of a linked identity); that sign-in's session ends below.
            await db.UserLogins.Where(l => l.UserId == current.UserId && l.LoginProvider == provider).ExecuteDeleteAsync(CancellationToken.None);
            await SessionRenewal.EndSessionsAsync(tokens, authorizations, current.UserId, CancellationToken.None);
            await notices.QueueAsync(db, MailKind.ProviderUnlinked, current.NormalizedEmail, provider, now, CancellationToken.None);
            audit.Stage(SettingsCallers.Entry(AuditKinds.ProviderUnlinked, current, new Dictionary<string, object?> { ["provider"] = provider }));
            return SessionRenewal.Issue(http, account, current, transaction, tokenOptions.Value.Audience, clock, _ => notices.Notify());
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Steps 4 and 5: the name must be a provider's and the account must have a login of it (a login of a provider no longer offered included);
    /// afterwards the password or a login of another offered provider must remain.
    /// </summary>
    private static async Task<IResult?> RefusalAsync(
        AuthDbContext db, ProviderCatalog catalog, SettingsCaller caller, string provider, CancellationToken cancellationToken)
    {
        var providers = await db.UserLogins.AsNoTracking()
            .Where(l => l.UserId == caller.UserId)
            .Select(l => l.LoginProvider)
            .ToListAsync(cancellationToken);
        if (!ProviderNames.All.Contains(provider, StringComparer.Ordinal) || !providers.Contains(provider, StringComparer.Ordinal))
        {
            return ApiResults.NotFound();
        }

        var wayIn = caller.PasswordHash is not null
            || providers.Any(p => !string.Equals(p, provider, StringComparison.Ordinal) && catalog.IsOffered(p));
        return wayIn ? null : ApiResults.Error(TenancyErrors.LastLogin);
    }
}
```

(The account read here is tracked: the renewal's principal reads its claims, and nothing is written to it.)

In `src/Auth.Server/Api/SettingsRoutes.cs`, add before `        return app;`:

```csharp
        app.MapPost(SettingsRequestHandler.UnlinkPath, LoginUnlinkEndpoint.HandleAsync)
            .RequireAuthorization()
            .WithTags("Account")
            .WithDescription(
                "Disconnects a provider from the caller's account while the session of this device is confirmed (`GET /auth/me` "
                + "`confirmed_until`; else `403 confirmation_required`): every login of that provider is deleted, every session of the account "
                + "ends (its email links and its login streak stay), and this device gets a new session with the start and the window of the old "
                + "one, answered as a login answers. The checks, in this order: a bad body `400 invalid_request`; the session `401 "
                + "session_required`, no membership `403 no_membership`; the window; a name that is not a provider's, or a provider the account has "
                + "no login of, `404 not_found` (a login of a provider no longer offered can be disconnected); the last way in (the password or a "
                + "login of an offered provider) `409 last_login`. A session past its 30-day cap is `401 session_required` and nothing changes. The "
                + "mail \"provider disconnected\" is sent.")
            .ReadsJson<UnlinkRequest>()
            .ReadsRefreshCookie(RefreshCookieRead)
            .Produces<LoginResponse>()
            .SetsRefreshCookie(StatusCodes.Status200OK, AccountEndpoints.RefreshCookieSet)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status401Unauthorized, TenancyErrors.SessionRequired)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.NoMembership, TenancyErrors.ConfirmationRequired)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.LastLogin);
```

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ProviderUnlinkTests -class Auth.IntegrationTests.ConfirmationWindowTests -class Auth.IntegrationTests.SettingsMailTests -class Auth.IntegrationTests.LoginScopeTests -class Auth.IntegrationTests.UnhandledTokenRequestGuardTests -class Auth.IntegrationTests.ProviderExchangeTests -class Auth.IntegrationTests.LoginTests -class Auth.IntegrationTests.RefreshTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests -class Auth.IntegrationTests.JsonCharsetTests`
passes. Mutate and see a test fail, then restore: issue the session before the revocation
(`An_unlink_with_an_open_window_deletes_the_login_signs_the_other_devices_out_and_renews_this_one` fails: the renewed refresh is
`invalid_grant`); pass no `session` to `AtomicSignInResult` (the window test fails); drop the cap check (the past-cap test answers `500`).

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): disconnect a provider, never the last way in, with a renewed session for this device`.

---

### Task 11: A sign-in holds the login it signs in with; the races with the unlink

**Files:**
- Modify: `src/Auth.Server/Providers/ProviderLogins.cs`, `src/Auth.Server/Providers/ProviderSignIn.cs`
- Create (tests): `tests/Auth.IntegrationTests/ProviderUnlinkRaceTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/ShareLockInterceptor.cs`

**Interfaces:**
- Consumes: the unlink (Task 10); `ShareLockInterceptor`, `HeldLock`.
- Produces: `ProviderLogins.HoldAsync(AuthDbContext, Guid userId, string provider, string key, CancellationToken)` (the login row read
  `FOR SHARE`; `false` when it is gone); `ShareLockInterceptor.Pattern` (the SQL text it stops before; `FOR KEY SHARE` by default).

**Contract counts:** none.

**Decisions the plan takes here:**
- The `FOR SHARE` read goes into `ProviderSignIn.LinkedAsync`, the one place where the exchange (step 3.1) and the link (step 3 and the
  "linked meanwhile" branch under its lock) sign in a linked identity, after the ticket is used up and before the session: a login gone is
  `400 invalid_ticket` and the rollback keeps the ticket used up only if the transaction commits (it does not: nothing is written).
- The race tests stop a sign-in at its `FOR SHARE` read with the interceptor (a second pattern of it), then run the unlink, so that both
  orders are forced: the sign-in first (the unlink waits for its share lock, then ends its session) and the unlink first (the sign-in answers
  `400 invalid_ticket`).

- [ ] **Step 1: Write the failing tests.** In `tests/Auth.IntegrationTests/Infrastructure/ShareLockInterceptor.cs`, replace

```csharp
/// <summary>Runs a step once, just before the first command that reads a row under a share lock.</summary>
public sealed class ShareLockInterceptor : DbCommandInterceptor
{
    public Func<Task>? Before { get; set; }
```

with

```csharp
/// <summary>Runs a step once, just before the first command that reads a row under a share lock.</summary>
public sealed class ShareLockInterceptor : DbCommandInterceptor
{
    public Func<Task>? Before { get; set; }

    /// <summary>The text of the command to stop before: <c>FOR KEY SHARE</c> (the account's row at a sign-in) unless set.</summary>
    public string Pattern { get; set; } = "FOR KEY SHARE";
```

and replace `        if (step is not null && command.CommandText.Contains("FOR KEY SHARE", StringComparison.Ordinal))` with
`        if (step is not null && command.CommandText.Contains(Pattern, StringComparison.Ordinal))`.

`tests/Auth.IntegrationTests/ProviderUnlinkRaceTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0012 → Sign-in of a linked identity, and the link (criterion 15): a sign-in of a linked identity that races the unlink of its login either
/// ends first, and the unlink then ends its session, or answers <c>400 invalid_ticket</c>; no session of the identity outlives the unlink. Both
/// orders are forced: the sign-in is stopped before its account read (it holds the login row by then) or before its login read.
/// </summary>
public sealed class ProviderUnlinkRaceTests : ProviderTestBase
{
    private const string Ola = FakePeople.GoogleEmail;

    // The login read of a sign-in is the only statement of these flows with this text ("FOR KEY SHARE" does not contain it).
    private const string LoginHold = "FOR SHARE";

    private readonly ShareLockInterceptor _race = new();

    public ProviderUnlinkRaceTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(_race)));
    }

    /// <summary>An account with a password and a Google login; this device signed in with the password a moment ago (its window open).</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> AccountAsync()
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        return (user.Id, await SessionApi.LoginAsync(Client, Ola, UserPassword));
    }

    private Task<int> LiveAuthorizationsAsync() =>
        InDbAsync(db => db.Set<OpenIddictEntityFrameworkCoreAuthorization>()
            .CountAsync(a => a.Status == OpenIddictConstants.Statuses.Valid, TestContext.Current.CancellationToken));

    /// <summary>
    /// Stops the next sign-in before its account read, when it already holds its login row: the unlink is sent then, and the sign-in goes on
    /// only once the unlink waits for that row.
    /// </summary>
    private Func<Task<HttpResponseMessage>> UnlinkWhileTheSignInHoldsTheLogin(SessionApi.Session session)
    {
        Task<HttpResponseMessage>? unlink = null;
        _race.Pattern = "FOR KEY SHARE";
        _race.Before = async () =>
        {
            unlink = SettingsApi.Unlink(Client, session, "google");
            await HeldLock.WaitForWaitersAsync(Factory, 1, unlink);
        };
        return () => unlink ?? throw new InvalidOperationException("The sign-in never reached its account read.");
    }

    [Fact]
    public async Task An_exchange_that_holds_the_login_first_ends_first_and_the_unlink_then_ends_its_session()   // criterion 15
    {
        var (_, session) = await AccountAsync();
        var ticket = await TicketAsync("google", GooglePerson());
        var unlink = UnlinkWhileTheSignInHoldsTheLogin(session);

        using var exchanged = await ProviderApi.Exchange(Client, ticket);

        var signedIn = await SessionApi.ReadSessionAsync(exchanged);
        Assert.True(_race.Fired);
        using var unlinked = await unlink();
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, signedIn.RefreshToken));   // its session ended with the others
        Assert.Equal(1, await LiveAuthorizationsAsync());   // the renewed session of the unlinking device, and nothing else
    }

    [Fact]
    public async Task An_exchange_after_an_unlink_that_committed_first_is_400_invalid_ticket()   // criterion 15
    {
        var (userId, session) = await AccountAsync();
        var ticket = await TicketAsync("google", GooglePerson());
        _race.Pattern = LoginHold;
        _race.Before = async () =>
        {
            using var unlinked = await SettingsApi.Unlink(Client, session, "google");   // commits before the sign-in reads the login again
            Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        };

        using var exchanged = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(exchanged, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.True(_race.Fired);
        Assert.Empty(await InDbAsync(db => db.UserLogins.Where(l => l.UserId == userId).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.DoesNotContain(await AuditAsync(AuditKinds.LoginSucceeded), row => AuditApi.Text(row, "provider") == "google");
        Assert.Equal(1, await LiveAuthorizationsAsync());
    }

    [Fact]
    public async Task A_link_whose_identity_is_a_login_signs_in_first_and_the_unlink_then_ends_its_session()   // criterion 15: the link's step 3
    {
        var (_, session) = await AccountAsync();
        var ticket = await TicketAsync("google", GooglePerson());
        var unlink = UnlinkWhileTheSignInHoldsTheLogin(session);

        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);   // the identity is a login: step 3 signs in without the password

        var signedIn = await SessionApi.ReadSessionAsync(linked);
        using var unlinked = await unlink();
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, signedIn.RefreshToken));
        Assert.Equal(1, await LiveAuthorizationsAsync());
    }

    [Fact]
    public async Task A_link_after_an_unlink_that_committed_first_is_400_invalid_ticket()   // criterion 15: the link's step 3
    {
        var (userId, session) = await AccountAsync();
        var ticket = await TicketAsync("google", GooglePerson());
        _race.Pattern = LoginHold;
        _race.Before = async () =>
        {
            using var unlinked = await SettingsApi.Unlink(Client, session, "google");
            Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        };

        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);

        await TenancyApi.AssertErrorAsync(linked, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Empty(await InDbAsync(db => db.UserLogins.Where(l => l.UserId == userId).ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await LiveAuthorizationsAsync());
    }
}
```

The interceptor's `Before` runs on the sign-in's own command, before that command is sent. In the "holds first" tests the sign-in already
holds the login row (its `FOR SHARE` read runs before its account read), so the unlink, which takes the address lock and its re-reads first,
queues at the delete of the login rows, and `WaitForWaitersAsync` sees it waiting; the sign-in then commits and the unlink ends its session.
In the "unlink first" tests the sign-in has used its ticket up but holds nothing the unlink needs, so the unlink commits inside `Before`.
`ProviderExchangeRaceTests` keeps the default pattern and passes unchanged.

- [ ] **Step 2: See them fail.** Build and run `ProviderUnlinkRaceTests`: `_interceptor.Fired` is false (no such statement yet), and in the
second test the exchange answers `200` (a session of an unlinked identity).

- [ ] **Step 3: The hold.** In `src/Auth.Server/Providers/ProviderLogins.cs`, add after `IsLoginOfAsync`:

```csharp

    /// <summary>
    /// Reads the identity's login row of the account under a share lock held to the end of the caller's transaction (spec 0012 → Sign-in of a
    /// linked identity): an unlink that comes second waits for the sign-in and then ends its session. <see langword="false"/> when the row is gone
    /// (an unlink committed first).
    /// </summary>
    public static async Task<bool> HoldAsync(AuthDbContext db, Guid userId, string provider, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        var rows = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM "AspNetUserLogins" AS l WHERE l."UserId" = {userId} AND l."LoginProvider" = {provider} AND l."ProviderKey" = {key} FOR SHARE""")
            .ToListAsync(cancellationToken);
        return rows.Count == 1;
    }
```

In `src/Auth.Server/Providers/ProviderSignIn.cs`, replace

```csharp
            if (await ProviderTickets.ConsumeAsync(db, ticketText, binding, ticket.Purpose, StorableTime.Now(clock), http.RequestAborted) is null)
            {
                await transaction.DisposeAsync();
                return ApiResults.Error(TenancyErrors.InvalidTicket);
            }

            return Session(http, account, tenant, ticket.Provider, transaction, tokens, clock, InvalidTicketAsync);
```

with

```csharp
            if (await ProviderTickets.ConsumeAsync(db, ticketText, binding, ticket.Purpose, StorableTime.Now(clock), http.RequestAborted) is null)
            {
                await transaction.DisposeAsync();
                return ApiResults.Error(TenancyErrors.InvalidTicket);
            }

            // Spec 0012: the login this sign-in signs in with, held to the commit. An unlink that committed first leaves none: nothing is signed in.
            if (!await ProviderLogins.HoldAsync(db, account.Id, ticket.Provider, ticket.IdentityKey, http.RequestAborted))
            {
                await transaction.DisposeAsync();
                return ApiResults.Error(TenancyErrors.InvalidTicket);
            }

            return Session(http, account, tenant, ticket.Provider, transaction, tokens, clock, InvalidTicketAsync);
```

and extend the method's summary with: `The login row is read again under a share lock in that transaction (spec 0012): one deleted meanwhile is
<c>400 invalid_ticket</c>.`

- [ ] **Step 4: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ProviderUnlinkRaceTests -class Auth.IntegrationTests.ProviderExchangeTests -class Auth.IntegrationTests.ProviderExchangeRaceTests -class Auth.IntegrationTests.ProviderLinkTests -class Auth.IntegrationTests.ProviderLinkRaceTests -class Auth.IntegrationTests.ProviderUnlinkTests -class Auth.IntegrationTests.AccountDeletionProviderTests`
passes. `ProviderExchangeRaceTests` uses the interceptor with its default pattern: it must pass unchanged. Mutate and see a test fail, then
restore: drop `FOR SHARE` from the hold (`An_exchange_that_holds_the_login_first_ends_first_and_the_unlink_then_ends_its_session` fails:
the unlink never waits); drop the hold (`An_exchange_after_an_unlink_that_committed_first_is_400_invalid_ticket` fails with a `200`).

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): a provider sign-in holds its login row against an unlink`.

---

### Task 12: `POST /auth/account/password`

**Files:**
- Create: `src/Auth.Server/Account/AccountPasswordEndpoint.cs`
- Modify: `src/Auth.Server/Api/SettingsRoutes.cs`, `src/Auth.Server/Api/AccountContracts.cs`
- Create (tests): `tests/Auth.IntegrationTests/AccountPasswordTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/SettingsMailTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`,
  `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`

**Interfaces:**
- Consumes: `SettingsCallers` (Task 7), `SettingsRequestHandler.PasswordPath`, `SessionRenewal` (Task 10), `MailNotices` (Task 8),
  `PasswordRules.BrokenAsync`, `AccountResults.WeakPassword`, `LoginStreakStore`, `ShareLockInterceptor.Pattern` (Task 11).
- Produces: `AccountPasswordEndpoint.HandleAsync`; `ChangePasswordRequest(string CurrentPassword, string NewPassword)`.

**Contract counts:** `OpenApiTests`: `Endpoints` gains `POST /auth/account/password`; statuses `200,400,401,403,415,429`; body
`current_password,new_password`; cookie `auth_rt`; the bearer token; its `200` sets the refresh cookie. `OpenApiHardeningTests`: **39 → 40**
operations, **27 → 28** under the charset guard (the spec's final counts).

**Decisions the plan takes here:**
- The statement order of Amendment A2, under the address lock: the email links deleted (`EmailTokens` of the account); the account's hash read
  `FOR SHARE` and compared with the hash that was judged; the new hash set through Identity (`RemovePasswordAsync`, `AddPasswordAsync`: the
  security stamp changes, as at a reset); every refresh token and authorization revoked (`SessionRenewal.EndSessionsAsync`); `password.changed`,
  the notice, then the renewal. A reset holds its link row from its first statement; the change asks for that row first, so the two meet at one
  row and the first one wins (U14, Amendment A1).
- The streak is cleared where the right password is recognised (`LoginStreakStore.ClearAsync`, on a connection of its own, before the
  transaction), and not again inside the transaction: the rule of `AccountDeletionEndpoint` is that the streak is never written on the request's own
  context inside an address-lock transaction. (`AccountSessions.EndAllAsync` writes it inside a reset because a reset takes no address lock.) A
  failed attempt that another request counts between that clear and the commit stays counted, as it should.
- The policy is checked with a copy of the account read without tracking, and the account that the change updates is read after the
  `FOR SHARE` read, so that Identity's concurrency stamp is the committed one.
- `new_password` is read with `TryAnyString`: blank is `400 weak_password` with its rules (step 4), not `invalid_request`; `current_password`
  must be a non-blank string with no NUL (step 1).
- The refusal under the lock (a hash changed meanwhile) is written alone after the rollback, not counted again.
- Under the lock the session and the membership are read first (step 2 again), then the links and the hash (Amendment A2). So a second
  change of the account, which waits for the address lock, finds its session ended (`401 session_required`); a reset that holds its link when
  the change arrives makes the change wait at that link and then find the hash changed (`403 wrong_password`), as criterion 19 says; a reset
  that committed before the change took the address lock ended every session, the change's included, so the change answers `401 session_required`
  with nothing written (the third outcome of Amendment A1, the orchestrator's decision D42). None of them is a `500`. The three race tests force
  each order: `ShareLockInterceptor` stopped before the reset's account read (it holds its link then), `ShareLockInterceptor` stopped before the
  change's `FOR SHARE` read (the links are deleted then), and a held address lock under which the reset commits first.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/AccountPasswordTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → <c>POST /auth/account/password</c> (criteria 18 and 19), in the order of its checks, and Amendment A1.</summary>
public sealed class AccountPasswordTests : ProviderTestBase
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string OlaNormalized = "OLA@GMAIL.TEST";
    private const string NewPassword = "Fresh-Passw0rd-2";

    // Spelled out, so that the escape in a test body is not read as an escape of this file.
    private const char Backslash = (char)0x5C;

    private readonly ShareLockInterceptor _race = new();
    private readonly SessionRowFault _fault = new();

    public AccountPasswordTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithServices(services => services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(_race, _fault)));
    }

    private Task<int> AttemptsAsync() =>
        InDbAsync(db => db.LoginStreaks.Where(s => s.IdentifierHash == LoginIdentifier.HashOf(OlaNormalized)).Select(s => s.AttemptCount)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken));

    /// <summary>An account with a password and a Google login, signed in on this device; its window closed (the change never needs it).</summary>
    private async Task<(Guid UserId, SessionApi.Session Session)> AccountAsync()
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        Clock.Advance(TimeSpan.FromMinutes(6));
        return (user.Id, await SessionApi.RefreshOk(Client, session.RefreshToken));
    }

    /// <summary>The values of the one session whose authorization is still valid.</summary>
    private Task<SessionState> LiveSessionStateAsync() =>
        InDbAsync(db => (
            from state in db.SessionStates.AsNoTracking()
            join authorization in db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking() on state.AuthorizationId equals authorization.Id
            where authorization.Status == OpenIddictConstants.Statuses.Valid
            select state).SingleAsync(TestContext.Current.CancellationToken));

    private async Task<string> ResetTokenAsync()
    {
        using (var forgot = await AccountApi.Forgot(Client, Ola))
        {
            Assert.Equal(HttpStatusCode.Accepted, forgot.StatusCode);
        }

        await DispatchAsync();
        return TokenIn(Mail.Sent.Last(m => m.To == Ola));
    }

    [Fact]
    public async Task The_right_current_password_changes_it_and_this_device_alone_keeps_a_session()   // criterion 18
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        var userId = user.Id;
        var here = await SessionApi.LoginAsync(Client, Ola, UserPassword);   // the window of this session is open: the renewal must carry it over
        Clock.Advance(LockoutApi.HumanPace);
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        Clock.Advance(LockoutApi.HumanPace);
        await ResetTokenAsync();   // an email link of the account
        await LockoutApi.FailAsync(Client, Clock, Ola, 1);
        var window = await SettingsApi.ConfirmedUntilAsync(Client, here);
        Assert.NotNull(window);   // an open window: two closed ones would be equal below whatever the renewal did
        var before = await InDbAsync(db => db.SessionStates.AsNoTracking().OrderBy(s => s.SessionStart).FirstAsync(TestContext.Current.CancellationToken));
        var stamp = await InDbAsync(db => db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync(TestContext.Current.CancellationToken));

        using var response = await SettingsApi.ChangePassword(Client, here, UserPassword, NewPassword);

        var renewed = await SessionApi.ReadSessionAsync(response);
        Assert.Equal(userId.ToString(), AccessTokens.Text(renewed.AccessToken, "sub"));
        Assert.NotEqual(stamp, await InDbAsync(db => db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync(TestContext.Current.CancellationToken)));
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, there.RefreshToken));
        await SessionApi.AssertInvalidGrantAsync(await SessionApi.Refresh(Client, here.RefreshToken));
        Assert.Equal(window, await SettingsApi.ConfirmedUntilAsync(Client, renewed));   // the old window
        var live = await LiveSessionStateAsync();
        Assert.Equal((before.SessionStart, before.ConfirmedUntil), (live.SessionStart, live.ConfirmedUntil));   // and the old start
        Assert.Equal(0, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == userId, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
        Assert.Single(await InDbAsync(db => db.UserLogins.Where(l => l.UserId == userId).ToListAsync(TestContext.Current.CancellationToken)));   // the logins stay

        var changed = await SingleAsync(AuditKinds.PasswordChanged);
        Assert.Equal((userId, userId, Ola), (changed.ActorUserId!.Value, changed.SubjectUserId!.Value, changed.SubjectEmail));
        Assert.Equal(await DevCompanyIdAsync(), changed.OrgId);
        Assert.Empty(AuditApi.DetailNames(changed));
        Assert.Equal("session_renewal", AuditApi.Text((await AuditAsync(AuditKinds.LoginSucceeded))[^1], "via"));
        Assert.Equal(1, await InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.PasswordChanged, TestContext.Current.CancellationToken)));

        // Afterwards the new password signs in and the old one does not.
        Clock.Advance(LockoutApi.HumanPace);
        using (var old = await LoginApi.Login(Client, Ola, UserPassword))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        }

        Clock.Advance(LockoutApi.HumanPace);
        await SessionApi.LoginAsync(Client, Ola, NewPassword);
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, renewed.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task An_open_window_alone_never_lets_it_act()   // criterion 18
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);   // the window is open
        Assert.NotNull(await SettingsApi.ConfirmedUntilAsync(Client, session));

        using var response = await SettingsApi.ChangePassword(Client, session, "Wrong-Passw0rd-9", NewPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        _ = user;
    }

    public static TheoryData<string> BadBodies => new()
    {
        "not json",
        "{}",
        """["current_password","new_password"]""",
        """{"current_password":"Another-Passw0rd"}""",
        """{"new_password":"Fresh-Passw0rd-2"}""",
        """{"current_password":"Another-Passw0rd","new_password":5}""",
        """{"current_password":"Another-Passw0rd","new_password":null}""",
        """{"current_password":"","new_password":"Fresh-Passw0rd-2"}""",
        """{"current_password":"  ","new_password":"Fresh-Passw0rd-2"}""",
        """{"current_password":5,"new_password":"Fresh-Passw0rd-2"}""",
        "{\"current_password\":\"Another-Passw0rd\",\"new_password\":\"Fresh" + Backslash + "u0000Passw0rd\"}",
        "{\"current_password\":\"Another" + Backslash + "u0000Passw0rd\",\"new_password\":\"Fresh-Passw0rd-2\"}",
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request_before_the_session_and_counts_for_nothing(string body)   // criterion 19, step 1
    {
        var (_, session) = await AccountAsync();

        using var response = await SettingsApi.PostRaw(Client, SettingsApi.PasswordPath, session with { RefreshToken = "" }, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, await AttemptsAsync());
    }

    [Fact]
    public async Task An_account_without_a_password_is_403_password_not_set_not_counted_and_nothing_written()   // criterion 19, step 3
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        using var response = await SettingsApi.ChangePassword(Client, session, "Any-Passw0rd-1", "short");   // 3 before 4

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "password_not_set");
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
    }

    [Theory]
    [InlineData("short", new[] { "too_short", "requires_upper", "requires_digit" })]
    [InlineData("", new[] { "too_short", "requires_upper", "requires_lower", "requires_digit" })]
    public async Task A_new_password_that_breaks_the_policy_is_400_weak_password_with_its_rules_before_the_current_one_is_judged(string next, string[] rules)   // criterion 19, step 4
    {
        var (_, session) = await AccountAsync();

        using var response = await SettingsApi.ChangePassword(Client, session, "Wrong-Passw0rd-9", next);   // 4 before 5

        var body = await TenancyApi.ReadAsync(response, HttpStatusCode.BadRequest);
        using var json = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("weak_password", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(rules, json.RootElement.GetProperty("rules").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
    }

    [Fact]
    public async Task A_wrong_current_password_is_403_wrong_password_counted_with_account_confirm_refused()   // criterion 19, step 5
    {
        var (userId, session) = await AccountAsync();

        using var response = await SettingsApi.ChangePassword(Client, session, "Wrong-Passw0rd-9", NewPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Equal(1, await AttemptsAsync());
        var row = await SingleAsync(AuditKinds.AccountConfirmRefused);
        Assert.Equal(userId, row.SubjectUserId);
        Assert.Equal(("wrong_password", "password"), (AuditApi.Text(row, "reason"), AuditApi.Text(row, "action")));
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, session.RefreshToken)).StatusCode);   // nothing ended
    }

    [Fact]
    public async Task After_ten_failures_it_is_429_too_many_attempts_with_account_confirm_refused_locked()   // criterion 19, step 5
    {
        var (_, session) = await AccountAsync();
        await LockoutApi.FailAsync(Client, Clock, Ola, 10);
        Clock.Advance(LockoutApi.HumanPace);

        using var response = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);   // even the right one

        await LockoutApi.AssertLockedAsync(response);
        var row = (await AuditAsync(AuditKinds.AccountConfirmRefused))[^1];
        Assert.Equal(("locked", "password"), (AuditApi.Text(row, "reason"), AuditApi.Text(row, "action")));
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
    }

    [Fact]
    public async Task A_reset_that_used_its_link_up_first_wins_and_the_change_is_403_wrong_password_not_counted_again()   // criterion 19, Amendment A1, Review Focus 2
    {
        var (userId, session) = await AccountAsync();
        var token = await ResetTokenAsync();
        Task<HttpResponseMessage>? change = null;
        _race.Pattern = "FROM \"AspNetUsers\"";
        _race.Before = async () =>
        {
            // The reset holds its link (its first statement used it up) and is about to read the account: the change is sent now, passes
            // its checks and its re-reads under the address lock (the session is still alive), and waits for that link row.
            change = SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);
            await HeldLock.WaitForWaitersAsync(Factory, 1, change);
        };

        using (var reset = await AccountApi.Reset(Client, token, "Reset-Passw0rd-3"))
        {
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        }

        using var response = await change!;

        Assert.True(_race.Fired);
        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "wrong_password");
        Assert.Equal(0, await AttemptsAsync());   // counted once at step 5 and cleared there; not counted again
        var row = await SingleAsync(AuditKinds.AccountConfirmRefused);
        Assert.Equal(("wrong_password", "password"), (AuditApi.Text(row, "reason"), AuditApi.Text(row, "action")));
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.PasswordChanged, TestContext.Current.CancellationToken)));
        Clock.Advance(LockoutApi.HumanPace);
        await SessionApi.LoginAsync(Client, Ola, "Reset-Passw0rd-3");   // the reset's password is the one; neither answered 500
        _ = userId;
    }

    [Fact]
    public async Task A_reset_that_committed_before_the_change_took_the_address_lock_ended_its_session_and_the_change_is_401_session_required()   // criterion 19, Amendment A1: the third outcome
    {
        var (_, session) = await AccountAsync();
        var token = await ResetTokenAsync();
        Task<HttpResponseMessage> change;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            change = SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);   // passes its checks and waits for the address lock
            await HeldLock.WaitForWaitersAsync(Factory, 1, change);
            using (var reset = await AccountApi.Reset(Client, token, "Reset-Passw0rd-3"))   // the reset takes no address lock: it commits first
            {
                Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
            }

            await held.ReleaseAsync();
        }

        using var response = await change;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "session_required");   // the reset ended every session, this one's too
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        Assert.Empty(await AuditAsync(AuditKinds.AccountConfirmRefused));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.PasswordChanged, TestContext.Current.CancellationToken)));
        Clock.Advance(LockoutApi.HumanPace);
        await SessionApi.LoginAsync(Client, Ola, "Reset-Passw0rd-3");   // the reset's password is the one; nothing answered 500
    }

    [Fact]
    public async Task A_failure_after_the_sessions_ended_rolls_the_whole_change_back()   // guard lesson 9: the sign-in's transaction owns the change
    {
        var (userId, here) = await AccountAsync();
        var there = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var stamp = await InDbAsync(db => db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync(TestContext.Current.CancellationToken));
        await ResetTokenAsync();   // an email link of the account
        _fault.Armed = true;

        using var failed = await SettingsApi.ChangePassword(Client, here, UserPassword, NewPassword);

        Assert.True(_fault.Fired, "The renewed session never reached its write.");
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.False(failed.Headers.Contains("Set-Cookie"));   // the refresh cookie is not replaced
        Assert.Equal(stamp, await InDbAsync(db => db.Users.Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await InDbAsync(db => db.EmailTokens.CountAsync(t => t.UserId == userId, TestContext.Current.CancellationToken)));   // the links are still there
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        Assert.Equal(0, await InDbAsync(db => db.MailRequests.CountAsync(r => r.Kind == MailKind.PasswordChanged, TestContext.Current.CancellationToken)));
        Assert.Equal(HttpStatusCode.OK, (await SessionApi.Refresh(Client, there.RefreshToken)).StatusCode);   // no session was revoked
        _fault.Armed = false;
        Clock.Advance(LockoutApi.HumanPace);
        await SessionApi.LoginAsync(Client, Ola, UserPassword);   // the old password still signs in
    }

    [Fact]
    public async Task A_change_that_voided_the_links_first_wins_and_the_reset_is_400_invalid_token()   // criterion 19, Amendment A1, Review Focus 2
    {
        var (_, session) = await AccountAsync();
        var token = await ResetTokenAsync();
        Task<HttpResponseMessage>? reset = null;
        _race.Pattern = "FOR SHARE";
        _race.Before = async () =>
        {
            // The change has deleted the account's links and is about to read its hash: the reset now waits for the change's delete.
            reset = AccountApi.Reset(Client, token, "Reset-Passw0rd-3");
            await HeldLock.WaitForWaitersAsync(Factory, 1, reset);
        };

        using var response = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(_race.Fired);
        using var late = await reset!;
        await AccountApi.AssertErrorAsync(late, """{"error":"invalid_token"}""");
        Clock.Advance(LockoutApi.HumanPace);
        await SessionApi.LoginAsync(Client, Ola, NewPassword);   // the change's password is the one; neither answered 500
    }

    [Fact]
    public async Task Two_changes_at_once_give_one_200_and_one_401_session_required()   // criterion 19, with parallel requests
    {
        var (_, session) = await AccountAsync();

        Task<HttpResponseMessage>[] pending;
        await using (var held = await HeldLock.AddressAsync(Factory, OlaNormalized))
        {
            pending = [SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword), SettingsApi.ChangePassword(Client, session, UserPassword, "Other-Passw0rd-4")];
            await HeldLock.WaitForWaitersAsync(Factory, 2, pending);
            await held.ReleaseAsync();
        }

        var answers = await Task.WhenAll(pending);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Unauthorized], answers.Select(a => a.StatusCode).Order());
        await TenancyApi.AssertErrorAsync(answers.Single(a => a.StatusCode == HttpStatusCode.Unauthorized), HttpStatusCode.Unauthorized, "session_required");
        Assert.Single(await AuditAsync(AuditKinds.PasswordChanged));
        foreach (var answer in answers)
        {
            answer.Dispose();
        }
    }

    [Fact]
    public async Task Without_the_refresh_cookie_it_is_401_session_required_and_without_a_token_the_plain_401()   // criterion 19
    {
        var (_, session) = await AccountAsync();

        using var noCookie = await SettingsApi.PostAs(
            Client, SettingsApi.PasswordPath, session.AccessToken, null, new { current_password = UserPassword, new_password = NewPassword });
        using var noToken = await SettingsApi.PostAs(
            Client, SettingsApi.PasswordPath, null, session.RefreshToken, new { current_password = UserPassword, new_password = NewPassword });

        await TenancyApi.AssertErrorAsync(noCookie, HttpStatusCode.Unauthorized, "session_required");
        await TenancyApi.AssertUnauthorizedAsync(noToken);
        Assert.Equal(0, await AttemptsAsync());
    }

    [Fact]
    public async Task A_removed_membership_is_403_no_membership_and_nothing_is_counted()   // criterion 19
    {
        var (userId, session) = await AccountAsync();
        await RemoveMembershipAsync(userId);

        using var response = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(0, await AttemptsAsync());
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
    }

    [Fact]
    public async Task A_session_past_its_cap_is_401_session_required_and_nothing_changes()   // spec 0012 → Sessions
    {
        var (userId, session) = await AccountAsync();
        await InDbAsync(db => db.SessionStates.ExecuteUpdateAsync(
            s => s.SetProperty(state => state.SessionStart, StorableTime.Now(Clock) - TimeSpan.FromDays(31)), TestContext.Current.CancellationToken));

        using var response = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Unauthorized, "session_required");
        Assert.Empty(await AuditAsync(AuditKinds.PasswordChanged));
        Clock.Advance(LockoutApi.HumanPace);
        await SessionApi.LoginAsync(Client, Ola, UserPassword);   // the password did not change
        _ = userId;
    }

    [Fact]
    public async Task No_password_token_or_cookie_reaches_the_audit_log_or_the_log()   // guard lesson 4
    {
        var (_, session) = await AccountAsync();
        using (var wrong = await SettingsApi.ChangePassword(Client, session, "Wrong-Passw0rd-9", NewPassword))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        }

        using var response = await SettingsApi.ChangePassword(Client, session, UserPassword, NewPassword);
        var renewed = await SessionApi.ReadSessionAsync(response);

        string[] secrets = ["Wrong-Passw0rd-9", UserPassword, NewPassword, session.RefreshToken, renewed.RefreshToken, renewed.AccessToken];
        await AssertNoSecretsAsync(secrets);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal));
    }
}
```

In `tests/Auth.IntegrationTests/SettingsMailTests.cs`, add before the class's last closing brace:

```csharp

    [Fact]
    public async Task A_password_change_queues_password_changed_with_no_provider()   // criterion 22
    {
        await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);

        using (var changed = await SettingsApi.ChangePassword(Client, session, UserPassword, "Fresh-Passw0rd-2"))
        {
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        }

        var row = Assert.Single(await NoticesAsync());
        Assert.Equal((MailKind.PasswordChanged, (string?)null), (row.Kind, row.Provider));
        await DispatchAsync();
        var mail = Assert.Single(Mail.Sent);
        Assert.Contains("password", mail.Subject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fresh-Passw0rd-2", mail.TextBody, StringComparison.Ordinal);
    }
```

In `tests/Auth.IntegrationTests/OpenApiTests.cs`: in `Endpoints` replace `"POST /auth/account/logins/unlink",` with
`"POST /auth/account/logins/unlink", "POST /auth/account/password",`; in `CookiesRead` add `        ["POST /auth/account/password"] = ["auth_rt"],`;
add `    [InlineData("POST /auth/account/password", "200,400,401,403,415,429")]` and
`    [InlineData("POST /auth/account/password", "current_password,new_password")]` to the statuses and bodies theories; add `    [InlineData("POST /auth/account/password", "200")]` to the theory
`The_answers_that_set_or_clear_the_refresh_cookie_say_so`; add `, "POST /auth/account/password 200"` to `setting`; at the end of `The_error_codes_of_the_contract_are_in_the_descriptions` add

```csharp
        Assert.All(["invalid_request", "weak_password"], code => Assert.Contains(code, Description(operations["POST /auth/account/password"], "400")));
        Assert.Contains("session_required", Description(operations["POST /auth/account/password"], "401"));
        Assert.All(["no_membership", "password_not_set", "wrong_password"], code => Assert.Contains(code, Description(operations["POST /auth/account/password"], "403")));
        Assert.Contains("too_many_attempts", Description(operations["POST /auth/account/password"], "429"));
```

In `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`, replace the counts `39` and `27` with `40` and `28`, the comments
`// spec 0012: the four settings endpoints`.

- [ ] **Step 2: See them fail.** Build, run `AccountPasswordTests`: `404` (the path is a token endpoint since Task 10, but no route is
mapped), except the bad-body rows, which fail the same way.

- [ ] **Step 3: The endpoint.** In `src/Auth.Server/Api/AccountContracts.cs`, add at the end:

```csharp

/// <summary>Request of <c>POST /auth/account/password</c> (spec 0012): the members are named as at <c>POST /auth/password/reset</c>.</summary>
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
```

`src/Auth.Server/Account/AccountPasswordEndpoint.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Account;

/// <summary>
/// <c>POST /auth/account/password</c> (spec 0012): changes the password of an account that has one, against the current password, every time
/// (the window plays no part). The checks run in the order of the contract: the body; the session and the membership; an account without a
/// password (<c>403 password_not_set</c>); the policy (<c>400 weak_password</c>); the current password, counted first in the login streak; then, in
/// one transaction under the address lock, in the order of Amendment A2: the session, the membership and the password's presence read again; the
/// account's email links deleted; its hash read under a share lock, which must be the one that was judged (a reset committed meanwhile:
/// <c>403 wrong_password</c>, not counted again); the new hash and stamp; every session ended; <c>password.changed</c>, the notice "password
/// changed" and a renewed session for this device, answered as a login. The login streak is cleared where the right password is recognised, on
/// the connection of its own that <see cref="LoginStreakStore"/> uses: it is never written on the request's context inside this transaction.
/// A reset that committed before this transaction took the address lock ended every session, this one's too: <c>401 session_required</c>.
/// </summary>
public static class AccountPasswordEndpoint
{
    private const string CurrentField = "current_password";
    private const string NewField = "new_password";

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, LoginStreakStore streaks,
        IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, AuditLog audit, MailNotices notices,
        IOptions<TokenOptions> tokenOptions, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(streaks);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(authorizations);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(notices);
        ArgumentNullException.ThrowIfNull(tokenOptions);
        ArgumentNullException.ThrowIfNull(clock);

        if (http.GetOpenIddictServerRequest() is null)
        {
            throw new InvalidOperationException("The password change was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        }

        // 1. The new password a string with no NUL (blank is judged by the policy), the current one a non-blank string with no NUL.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields
            || !fields.TryAnyString(NewField, out var next)
            || next.Contains('\0', StringComparison.Ordinal)
            || !fields.TryString(CurrentField, out var current)
            || current.Contains('\0', StringComparison.Ordinal))
        {
            return ApiResults.InvalidRequest();
        }

        // 2.
        var read = await SettingsCallers.ReadAsync(http, db, tokens, memberships, clock, http.RequestAborted);
        if (read.Caller is not { } caller)
        {
            return read.Refusal!;
        }

        // 3. A first password goes through the mailbox (Decision 17); nothing is counted.
        if (caller.PasswordHash is not { } judged)
        {
            return ApiResults.Error(TenancyErrors.PasswordNotSet);
        }

        // 4. Judged on a copy that nothing tracks: the account the change updates is read again under the lock.
        var copy = await db.Users.AsNoTracking().SingleAsync(u => u.Id == caller.UserId, http.RequestAborted);
        var broken = await PasswordRules.BrokenAsync(users, copy, next);
        if (broken.Count > 0)
        {
            return AccountResults.WeakPassword(broken);
        }

        // 5. As POST /auth/account/confirm judges a password: counted first.
        var identifier = LoginIdentifier.HashOf(caller.NormalizedEmail);
        var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
        if (!decision.Allowed)
        {
            await SettingsCallers.RefuseAsync(audit, caller, "locked", SettingsCallers.PasswordAction);
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        if (!SettingsCallers.PasswordIsRight(users, judged, current))
        {
            await SettingsCallers.RefuseAsync(audit, caller, "wrong_password", SettingsCallers.PasswordAction);
            return ApiResults.Error(TenancyErrors.WrongPassword);
        }

        await streaks.ClearAsync(identifier, CancellationToken.None);

        // 6. Not the request's token from here on: a client that goes away must not leave the change half done.
        var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        try
        {
            await AddressLock.AcquireAsync(db, caller.NormalizedEmail, CancellationToken.None);
            var now = StorableTime.Now(clock);
            var again = await SettingsCallers.ReadAgainAsync(db, memberships, caller, now);
            if (again.Caller is not { } owner || owner.PasswordHash is null || !SessionRenewal.CanRenew(owner.Session, now))
            {
                await transaction.DisposeAsync();
                return again.Refusal
                    ?? ApiResults.Error(again.Caller is { PasswordHash: null } ? TenancyErrors.PasswordNotSet : TenancyErrors.SessionRequired);
            }

            // Amendment A2: the links first, so that a reset meets this change at its link row and the first one wins.
            await db.EmailTokens.Where(t => t.UserId == owner.UserId).ExecuteDeleteAsync(CancellationToken.None);
            var hashes = await db.Database
                .SqlQuery<string?>($"""SELECT "PasswordHash" AS "Value" FROM "AspNetUsers" WHERE "Id" = {owner.UserId} FOR SHARE""")
                .ToListAsync(CancellationToken.None);
            if (hashes is not [{ } held] || !string.Equals(held, judged, StringComparison.Ordinal))
            {
                await transaction.DisposeAsync();
                await SettingsCallers.RefuseAsync(audit, owner, "wrong_password", SettingsCallers.PasswordAction);   // not counted again
                return ApiResults.Error(TenancyErrors.WrongPassword);
            }

            var account = await users.FindByIdAsync(owner.UserId.ToString())
                ?? throw new InvalidOperationException("The account held under a share lock is gone.");
            Ensure(await users.RemovePasswordAsync(account));
            Ensure(await users.AddPasswordAsync(account, next));   // the security stamp changes, as at a reset

            await SessionRenewal.EndSessionsAsync(tokens, authorizations, owner.UserId, CancellationToken.None);
            await notices.QueueAsync(db, MailKind.PasswordChanged, owner.NormalizedEmail, null, now, CancellationToken.None);
            audit.Stage(SettingsCallers.Entry(AuditKinds.PasswordChanged, owner));
            return SessionRenewal.Issue(http, account, owner, transaction, tokenOptions.Value.Audience, clock, _ => notices.Notify());
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private static void Ensure(IdentityResult result) => AccountSessions.Ensure(result, "Could not set the password");
}
```

(`SessionRenewal.Issue` builds the principal from `account` after the change, so the access token and the refresh token carry the new stamp.
`ReadAgainAsync` answers with its refusal when the session or the membership is gone; a hash removed meanwhile is `password_not_set`, and a
session past its cap `session_required`, both with nothing changed.)

In `src/Auth.Server/Api/SettingsRoutes.cs`, add before `        return app;`:

```csharp
        app.MapPost(SettingsRequestHandler.PasswordPath, AccountPasswordEndpoint.HandleAsync)
            .RequireAuthorization()
            .WithTags("Account")
            .WithDescription(
                "Changes the password of the caller's account against the current one, every time (the confirmation window plays no part); an "
                + "account without a password sets its first one through `POST /auth/password/forgot`. Every session of the account ends, its email "
                + "links are voided and its login streak cleared, and this device gets a new session with the start and the window of the old one, "
                + "answered as a login answers; the mail \"password changed\" is sent. The checks, in this order: a bad body `400 invalid_request` "
                + "(`new_password` missing, not a string or holding NUL; `current_password` missing, not a string, blank or holding NUL); the "
                + "session `401 session_required`, no membership `403 no_membership`; no password `403 password_not_set`; a new password that "
                + "breaks the policy `400 weak_password` with `rules` (neither is counted); the current password counts in the login streak as a "
                + "failed login does (`403 wrong_password`; while the identifier is locked `429 too_many_attempts`). A reset of the same account at "
                + "the same moment: one that used its link up and committed while the change waited is `403 wrong_password`, not counted again; "
                + "one that committed before the change took the address lock ended every session, so the change is `401 session_required` and "
                + "nothing is written; a change that voided the links first makes the reset's link `400 invalid_token`. None is a `500`.")
            .ReadsJson<ChangePasswordRequest>()
            .ReadsRefreshCookie(RefreshCookieRead)
            .Produces<LoginResponse>()
            .SetsRefreshCookie(StatusCodes.Status200OK, AccountEndpoints.RefreshCookieSet)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, AccountResults.WeakPasswordError)
            .ProducesError(StatusCodes.Status401Unauthorized, TenancyErrors.SessionRequired)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.NoMembership, TenancyErrors.PasswordNotSet, TenancyErrors.WrongPassword)
            .ProducesTooManyAttempts();
```

(If `ProducesError` documents the body of `weak_password` without `rules`, use the existing `ProducesPasswordError` of `EndpointMetadata` for
the `400`, as `POST /auth/password/reset` does: open `Api/AccountEndpoints.cs`, copy the reset's `400` line and add `invalid_request` to it.)

- [ ] **Step 4: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.AccountPasswordTests -class Auth.IntegrationTests.SettingsMailTests -class Auth.IntegrationTests.ResetPasswordTests -class Auth.IntegrationTests.ProviderPasswordResetTests -class Auth.IntegrationTests.LoginScopeTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests -class Auth.IntegrationTests.JsonCharsetTests`
passes. Mutate and see a test fail, then restore: read the hash `FOR SHARE` before deleting the links (the spec's first order:
`A_change_that_voided_the_links_first_wins_and_the_reset_is_400_invalid_token` deadlocks into a `500` or times out — the reason for A2); skip
the hash comparison (`A_reset_that_used_its_link_up_first_wins_and_the_change_is_403_wrong_password_not_counted_again` answers `200`); compare the hash
before the session is read again (`A_reset_that_committed_before_the_change_took_the_address_lock_ended_its_session_and_the_change_is_401_session_required`
answers `403 wrong_password`).

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): change the password against the current one, with a renewed session for this device`.

---

### Task 13: `DELETE /auth/org` with a provider ticket

**Files:**
- Modify: `src/Auth.Server/Api/OrgEndpoints.cs`, `src/Auth.Server/Tenancy/CompanyDeletionService.cs`, `src/Auth.Server/Tenancy/Contracts.cs`,
  `src/Auth.Server/Api/TenancyEndpoints.cs`
- Create (tests): `tests/Auth.IntegrationTests/OrgDeletionProviderTests.cs`, `tests/Auth.IntegrationTests/Infrastructure/OrgApi.cs`
- Modify (tests): `tests/Auth.IntegrationTests/OrgDeleteEndpointTests.cs`, `tests/Auth.IntegrationTests/ProviderTicketTests.cs`,
  `tests/Auth.IntegrationTests/OpenApiTests.cs`, `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`

**Interfaces:**
- Consumes: `ProviderTickets`, `ProviderLogins.IsLoginOfAsync`, `ProviderBindingCookie`, `SettingsCallers.PasswordIsRight` (Task 7).
- Produces: `CompanyDeletionService.DeleteAsync(Actor actor, Guid companyId, string confirmName, CancellationToken cancellationToken,
  Func<CancellationToken, Task<string?>>? confirmInside = null)`; `DeleteOrgRequest(string Name, string? Password = null, string? ProviderTicket = null)`;
  test side `OrgApi.Delete`, `OrgApi.DeleteWithTicket`.

**Contract counts:** `OpenApiTests`: `DELETE /auth/org` reads `auth_pt` (`CookiesRead`), its `204` sets a cookie, body
`name,password,provider_ticket`. `OpenApiHardeningTests`: the schema `["name","password","provider_ticket"]` and the `403` codes
`password_required`, `reauth_failed`; the operation count stays 40.

**Decisions the plan takes here:**
- `CompanyDeletionService.DeleteAsync` takes an optional step that runs inside its transaction right after the company lock and before the
  name check (Amendment A12): it returns `null` to go on or an error code to refuse, and the refusal rolls back whatever it did (the ticket
  stays). The operator's `delete-org` passes none.
- The step uses the ticket up with the clock read inside the step (after the company lock) and stages `provider.reauthenticated`, which the
  service's save writes with `org.deleted`. A refusal by the step is `403 reauth_failed` with `org.delete_refused` (`reauth_failed`), written
  alone after the rollback.
- The body is read as `DELETE /auth/account` reads it: `name` a non-blank string, exactly one of `password` (non-blank, no NUL) and
  `provider_ticket` (a string). The fit is judged before the lockout, as at `DELETE /auth/account`.
- The password is now verified with `SettingsCallers.PasswordIsRight` (hash only), as `DELETE /auth/account` does, instead of
  `CheckPasswordAsync`, which can rewrite an outdated hash of an account the deletion is about to remove.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/Infrastructure/OrgApi.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary><c>DELETE /auth/org</c> as the sample sends it: the access token, the body and, for a ticket, its <c>auth_pt</c>.</summary>
public static class OrgApi
{
    public const string Path = "/auth/org";

    public static async Task<HttpResponseMessage> Delete(HttpClient client, string accessToken, object body, string? binding = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Delete, Path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (binding is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{ProviderApi.BindingCookie}={binding}");
        }

        return await client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> DeleteWithTicket(HttpClient client, string accessToken, string name, ProviderApi.Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return Delete(client, accessToken, new { name, provider_ticket = ticket.Value }, ticket.Binding);
    }
}
```

`tests/Auth.IntegrationTests/OrgDeletionProviderTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012 → <c>DELETE /auth/org</c> with a provider ticket (criterion 21; Amendment A12). The answers for a password are spec 0008's.</summary>
public sealed class OrgDeletionProviderTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Boss = "boss@acme.test";
    private const string BossNormalized = "BOSS@ACME.TEST";
    private const string BossSubject = "100000000000000000055";

    private Task<int> CompaniesAsync(Guid company) =>
        InDbAsync(db => db.Companies.CountAsync(c => c.Id == company, TestContext.Current.CancellationToken));

    private Task<int> AttemptsAsync() =>
        InDbAsync(db => db.LoginStreaks.Where(s => s.IdentifierHash == LoginIdentifier.HashOf(BossNormalized)).Select(s => s.AttemptCount)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken));

    /// <summary>An admin of Acme made through Google: no password, a Google login.</summary>
    private async Task<(Guid Company, Guid AdminId, string Token)> AdminWithoutPasswordAsync()
    {
        var (company, adminId, token) = await CompanyWithAdminAsync("Acme", Boss);
        await InDbAsync(db => db.Users.Where(u => u.Id == adminId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken));
        await LinkAsync(adminId, "google", BossSubject);
        return (company, adminId, token);
    }

    private Task<ProviderApi.Ticket> ReauthTicketAsync(string query = "?purpose=reauth", string subject = BossSubject, string email = Boss) =>
        TicketAsync("google", GooglePerson(email: email, subject: subject), query);

    [Fact]
    public async Task An_admin_without_a_password_deletes_the_company_with_a_reauth_ticket()   // criterion 21
    {
        var (company, adminId, token) = await AdminWithoutPasswordAsync();
        var ticket = await ReauthTicketAsync();

        using var response = await OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);

        await TenancyApi.AssertEmptyAsync(response, HttpStatusCode.NoContent);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal(0, await CompaniesAsync(company));
        Assert.Equal(0, await InDbAsync(db => db.Users.CountAsync(u => u.Id == adminId, TestContext.Current.CancellationToken)));
        Assert.Equal(0, await TicketCountAsync());
        var reauthenticated = await SingleAsync(AuditKinds.ProviderReauthenticated);
        Assert.Equal((adminId, company), (reauthenticated.ActorUserId!.Value, reauthenticated.OrgId!.Value));
        Assert.Equal(["provider"], AuditApi.DetailNames(reauthenticated));
        Assert.Equal("google", AuditApi.Text(reauthenticated, "provider"));
        Assert.Single(await AuditAsync(AuditKinds.OrgDeleted));
        Assert.True(reauthenticated.OccurredAt <= (await SingleAsync(AuditKinds.OrgDeleted)).OccurredAt);
    }

    [Fact]
    public async Task A_password_is_still_403_reauth_required_and_nothing_is_counted()   // criterion 21
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();

        using var response = await OrgApi.Delete(Client, token, new { name = "Acme", password = "Any-Passw0rd-1" });

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_required");
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal(0, await AttemptsAsync());
    }

    [Fact]
    public async Task An_admin_with_a_password_and_a_ticket_is_403_password_required_before_the_lockout()   // criterion 21
    {
        var (company, adminId, token) = await CompanyWithAdminAsync("Acme", Boss);
        await LinkAsync(adminId, "google", BossSubject);
        var ticket = await ReauthTicketAsync();
        await LockoutApi.FailAsync(Client, Clock, Boss, 10);
        var attempts = await AttemptsAsync();

        using var response = await OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "password_required");   // not 429: the fit comes first
        Assert.Equal(attempts, await AttemptsAsync());
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleteRefused));
    }

    [Theory]
    [InlineData("sign_in")]
    [InlineData("connect")]
    [InlineData("expired")]
    [InlineData("foreign")]
    [InlineData("no-binding")]
    [InlineData("another-binding")]
    [InlineData("blank")]
    public async Task A_ticket_that_is_not_a_live_reauth_ticket_of_the_admin_is_403_reauth_failed_and_nothing_is_deleted(string how)   // criterion 21
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();
        var ticket = how switch
        {
            "sign_in" => await ReauthTicketAsync(""),
            "connect" => await ReauthTicketAsync("?purpose=connect"),
            "foreign" => await ReauthTicketAsync(subject: "100000000000000000099", email: "someone@acme.test"),
            _ => await ReauthTicketAsync(),
        };
        var value = how == "blank" ? "" : ticket.Value;
        string? binding = ticket.Binding;
        switch (how)
        {
            case "expired":
                Clock.Advance(Auth.Server.Providers.ProviderTickets.ReauthLifetime + TimeSpan.FromSeconds(1));
                break;
            case "no-binding":
                binding = null;
                break;
            case "another-binding":
                binding = (await TicketAsync("microsoft", PersonalMicrosoft())).Binding;
                break;
        }

        var tickets = await TicketCountAsync();
        using var response = await OrgApi.Delete(Client, token, new { name = "Acme", provider_ticket = value }, binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal(tickets, await TicketCountAsync());
        var refusal = await SingleAsync(AuditKinds.OrgDeleteRefused);
        Assert.Equal("reauth_failed", AuditApi.Text(refusal, "reason"));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
    }

    [Fact]
    public async Task A_ticket_neither_consults_nor_clears_the_lockout()   // spec 0012 → DELETE /auth/org: the lockout is not consulted
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();
        await LockoutApi.FailAsync(Client, Clock, Boss, 10);
        var ticket = await ReauthTicketAsync();

        using var response = await OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await CompaniesAsync(company));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("rule1")]
    public async Task A_deletion_refused_after_the_ticket_was_used_up_gives_the_ticket_back(string how)   // Amendment A12: the rollback keeps the ticket
    {
        Guid company;
        string token;
        ProviderApi.Ticket ticket;
        if (how == "name")
        {
            (company, _, token) = await AdminWithoutPasswordAsync();
            ticket = await ReauthTicketAsync();
        }
        else
        {
            // Rule 1, as OrgDeleteEndpointTests sets it up: the admin holds *, and a member who holds org:delete only deletes; this one has no password.
            (company, _, _) = await CompanyWithAdminAsync("Acme", Boss);
            await AddRoleAsync(company, "Deleter", "org:delete");
            var deleter = await AddMemberAsync(company, "deleter@acme.test", "Deleter");
            token = (await SessionApi.LoginAsync(Client, "deleter@acme.test", UserPassword)).AccessToken;
            await InDbAsync(db => db.Users.Where(u => u.Id == deleter)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken));
            await LinkAsync(deleter, "google", "100000000000000000044");
            ticket = await ReauthTicketAsync(subject: "100000000000000000044", email: "deleter@acme.test");
        }

        using var response = await OrgApi.DeleteWithTicket(Client, token, how == "name" ? "acme" : "Acme", ticket);

        if (how == "name")
        {
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        }
        else
        {
            await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "permission_not_held");
        }

        Assert.Equal(1, await TicketCountAsync());   // used up inside the deletion's transaction, which was rolled back
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
    }

    [Fact]
    public async Task Two_deletions_with_one_ticket_delete_once()   // criterion 21, with parallel requests
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();
        var ticket = await ReauthTicketAsync();

        Task<HttpResponseMessage>[] pending;
        await using (var held = await HeldLock.CompanyAsync(Factory, company))
        {
            pending = [OrgApi.DeleteWithTicket(Client, token, "Acme", ticket), OrgApi.DeleteWithTicket(Client, token, "Acme", ticket)];
            await HeldLock.WaitForWaitersAsync(Factory, 2, pending);
            await held.ReleaseAsync();
        }

        var answers = await Task.WhenAll(pending);
        Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Forbidden], answers.Select(a => a.StatusCode).Order());
        Assert.Single(await AuditAsync(AuditKinds.OrgDeleted));
        Assert.Single(await AuditAsync(AuditKinds.ProviderReauthenticated));
        Assert.Equal(0, await CompaniesAsync(company));
        foreach (var answer in answers)
        {
            answer.Dispose();
        }
    }

    [Fact]
    public async Task A_ticket_used_up_by_the_confirmation_while_the_deletion_waited_for_the_company_lock_deletes_nothing()   // criterion 21: one ticket, one use, inside the deletion's transaction
    {
        var (company, adminId, token) = await CompanyWithAdminAsync("Acme", Boss);
        var session = await SessionApi.LoginAsync(Client, Boss, UserPassword);   // the confirmation needs the refresh cookie
        await InDbAsync(db => db.Users.Where(u => u.Id == adminId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken));
        await LinkAsync(adminId, "google", BossSubject);
        var ticket = await ReauthTicketAsync();
        await using var held = await HeldLock.CompanyAsync(Factory, company);
        var pending = OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);   // passed its checks, waits for the company lock before it uses the ticket
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);

        using (var confirmed = await SettingsApi.ConfirmWithTicket(Client, session, ticket))   // the other consumer of the same ticket takes it first
        {
            Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        }

        await held.ReleaseAsync();
        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal(0, await TicketCountAsync());   // used once, by the confirmation
        Assert.Single(await AuditAsync(AuditKinds.AccountConfirmed));
        Assert.Empty(await AuditAsync(AuditKinds.OrgDeleted));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderReauthenticated));
    }

    [Fact]
    public async Task A_ticket_used_up_while_the_deletion_waited_for_the_company_lock_is_403_reauth_failed()   // guard lesson 1
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();
        var ticket = await ReauthTicketAsync();
        await using var held = await HeldLock.CompanyAsync(Factory, company);
        var pending = OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        await InDbAsync(db => db.ProviderTickets.ExecuteDeleteAsync(TestContext.Current.CancellationToken));
        await held.ReleaseAsync();

        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await CompaniesAsync(company));
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.OrgDeleteRefused), "reason"));
    }

    [Fact]
    public async Task A_ticket_that_expired_while_the_deletion_waited_is_refused()   // guard lesson 11: the clock after the locks
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();
        var ticket = await ReauthTicketAsync();
        await using var held = await HeldLock.CompanyAsync(Factory, company);
        var pending = OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);
        await HeldLock.WaitForWaitersAsync(Factory, 1, pending);
        Clock.Advance(TimeSpan.FromMinutes(5));
        await held.ReleaseAsync();

        using var response = await pending;

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await CompaniesAsync(company));
    }

    public static TheoryData<string> BadBodies => new()
    {
        """{"name":"Acme","password":"Another-Passw0rd","provider_ticket":"x"}""",
        """{"name":"Acme","provider_ticket":5}""",
        """{"name":"Acme","provider_ticket":null}""",
        """{"provider_ticket":"x"}""",
        """{"name":"  ","provider_ticket":"x"}""",
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task Both_members_neither_or_one_of_the_wrong_type_is_400_invalid_request(string body)   // criterion 21
    {
        var (company, _, token) = await AdminWithoutPasswordAsync();

        using var request = new HttpRequestMessage(HttpMethod.Delete, OrgApi.Path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await Client.SendAsync(request);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(1, await CompaniesAsync(company));
    }

    [Fact]
    public async Task No_ticket_binding_or_token_reaches_the_audit_log_or_the_log()   // guard lesson 4
    {
        var (_, _, token) = await AdminWithoutPasswordAsync();
        var ticket = await ReauthTicketAsync();
        using (var response = await OrgApi.DeleteWithTicket(Client, token, "Acme", ticket))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        string[] secrets = [ticket.Value, ticket.Binding, token];
        await AssertNoSecretsAsync(secrets);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, Logs.Text, StringComparison.Ordinal));
    }
}
```

In `tests/Auth.IntegrationTests/ProviderTicketTests.cs`, add before the class's last closing brace:

```csharp

    [Fact]
    public async Task A_connect_ticket_is_refused_by_the_company_deletion_as_reauth_failed()   // spec 0012, criterion 14
    {
        var (company, adminId, token) = await CompanyWithAdminAsync("Acme", "boss@acme.test");
        await InDbAsync(db => db.Users.Where(u => u.Id == adminId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken));
        await LinkAsync(adminId, "google", "100000000000000000055");
        var ticket = await TicketAsync("google", GooglePerson(email: "boss@acme.test", subject: "100000000000000000055"), "?purpose=connect");

        using var response = await OrgApi.DeleteWithTicket(Client, token, "Acme", ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal(1, await InDbAsync(db => db.Companies.CountAsync(c => c.Id == company, TestContext.Current.CancellationToken)));
    }
```

(the file has `using Microsoft.EntityFrameworkCore;` since Task 9.)

In `tests/Auth.IntegrationTests/OrgDeleteEndpointTests.cs`, in the theory `A_malformed_body_is_400_invalid_request_and_counts_for_nothing`, add
after `    [InlineData("""{"name":"Acme","password":["Another-Passw0rd"]}""")]`:

```csharp
    [InlineData("""{"name":"Acme","password":"Another-Passw0rd","provider_ticket":"x"}""")]   // spec 0012: exactly one of the two
    [InlineData("""{"name":"Acme","provider_ticket":1}""")]
```

In `tests/Auth.IntegrationTests/OpenApiTests.cs`: in `CookiesRead` add `        ["DELETE /auth/org"] = ["auth_pt"],`;
replace `    [InlineData("DELETE /auth/org", "name,password")]` with `    [InlineData("DELETE /auth/org", "name,password,provider_ticket")]`; add
`, "DELETE /auth/org 204"` to `setting` in `No_other_answer_says_it_sets_a_cookie`.

In `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`, in `The_company_deletion_is_described_with_its_body_and_its_answers`, replace

```csharp
        Assert.Equal(["name", "password"], schema["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
```

with

```csharp
        Assert.Equal(["name", "password", "provider_ticket"], schema["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));   // spec 0012
```

and replace

```csharp
        Assert.All(["forbidden", "permissions_changed", "permission_not_held", "wrong_password", "reauth_required"], code => Assert.Contains(code, Description(operation, "403")));
```

with

```csharp
        Assert.All(
            ["forbidden", "permissions_changed", "permission_not_held", "wrong_password", "reauth_required", "password_required", "reauth_failed"],
            code => Assert.Contains(code, Description(operation, "403")));
```

- [ ] **Step 2: See them fail.** Build, run `OrgDeletionProviderTests`: the ticket is an unknown member, so every ticket body is `400
invalid_request` (no `password`), and the admin without a password never gets past `reauth_required`.

- [ ] **Step 3: The step inside the deletion.** In `src/Auth.Server/Tenancy/CompanyDeletionService.cs`, replace

```csharp
    public async Task<Outcome> DeleteAsync(Actor actor, Guid companyId, string confirmName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(confirmName);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.OrgDelete, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
        }

        var current = entered.Value!;
```

with

```csharp
    /// <param name="confirmInside">
    /// Spec 0012, Amendment A12: the caller's confirmation that must be used up inside this transaction (a provider ticket), run right after the
    /// company lock and before the name check. It returns <see langword="null"/> to go on, or the error that refuses the deletion; a refusal of
    /// it or of any later check rolls back what it did.
    /// </param>
    public async Task<Outcome> DeleteAsync(
        Actor actor, Guid companyId, string confirmName, CancellationToken cancellationToken, Func<CancellationToken, Task<string?>>? confirmInside = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(confirmName);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entered = await guard.EnterAsync(actor, companyId, PermissionCatalog.OrgDelete, cancellationToken);
        if (!entered.Succeeded)
        {
            return entered.Without;
        }

        if (confirmInside is not null && await confirmInside(cancellationToken) is { } refused)
        {
            return Outcome.Fail(refused);
        }

        var current = entered.Value!;
```

(`Outcome.Fail(string)` is the existing factory the name check uses.)

- [ ] **Step 4: The endpoint.** In `src/Auth.Server/Tenancy/Contracts.cs`, replace

```csharp
public sealed record DeleteOrgRequest(string Name, string Password);
```

with

```csharp
public sealed record DeleteOrgRequest(string Name, string? Password = null, string? ProviderTicket = null);
```

and in its summary add the sentence `Spec 0012: exactly one of <c>password</c> and <c>provider_ticket</c>.`

In `src/Auth.Server/Api/OrgEndpoints.cs`:

(a) replace `    private static readonly string[] DeleteFields = ["name", "password"];` with

```csharp
    private const string NameField = "name";
    private const string PasswordField = "password";
    private const string TicketField = "provider_ticket";
```

(b) replace the signature and the body of `DeleteAsync` from

```csharp
    public static async Task<IResult> DeleteAsync(
        HttpContext http, MembershipReader memberships, UserManager<ApplicationUser> users, LoginStreakStore streaks,
        CompanyDeletionService deletion, AuditLog audit)
    {
```

down to (and including) the line

```csharp
        return outcome.Error == TenancyErrors.NotFound ? ApiResults.Error(TenancyErrors.PermissionsChanged) : ApiResults.Refused(outcome);
    }
```

with

```csharp
    public static async Task<IResult> DeleteAsync(
        HttpContext http, AuthDbContext db, MembershipReader memberships, UserManager<ApplicationUser> users, LoginStreakStore streaks,
        CompanyDeletionService deletion, AuditLog audit, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(streaks);
        ArgumentNullException.ThrowIfNull(deletion);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);

        var access = await CompanyAccess.AuthorizeAsync(http, memberships, PermissionCatalog.OrgDelete);
        if (access.Caller is not { } caller)
        {
            return access.Failure!;
        }

        // The name, and exactly one confirmation (spec 0012): a password that is a non-blank string with no NUL, or a provider ticket that is a
        // string (a blank one is no live ticket: reauth_failed).
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields || !fields.TryString(NameField, out var name) || fields.Has(PasswordField) == fields.Has(TicketField))
        {
            return ApiResults.InvalidRequest();
        }

        string? password = null;
        string? ticketText = null;
        var readable = fields.Has(PasswordField)
            ? fields.TryString(PasswordField, out password) && !password.Contains('\0', StringComparison.Ordinal)
            : fields.TryAnyString(TicketField, out ticketText);
        if (!readable)
        {
            return ApiResults.InvalidRequest();
        }

        var user = await users.FindByIdAsync(caller.UserId.ToString());
        if (user?.Email is null)
        {
            return ApiResults.Error(TenancyErrors.Forbidden);
        }

        // The confirmation must fit the account, before anything is counted and with no row written (spec 0011, Amendment A9; spec 0012).
        if (user.PasswordHash is not null && ticketText is not null)
        {
            return ApiResults.Error(TenancyErrors.PasswordRequired);
        }

        if (user.PasswordHash is null && password is not null)
        {
            return ApiResults.Error(TenancyErrors.ReauthRequired);
        }

        Func<CancellationToken, Task<string?>>? confirmInside = null;
        string? binding = null;
        if (password is not null)
        {
            var identifier = LoginIdentifier.HashOf(users.NormalizeEmail(user.Email));
            var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
            if (!decision.Allowed)
            {
                await RecordRefusalAsync(audit, caller, "locked");
                return new TooManyAttemptsResult(decision.RetryAfter);
            }

            if (!SettingsCallers.PasswordIsRight(users, user.PasswordHash!, password))
            {
                await RecordRefusalAsync(audit, caller, "wrong_password");
                return ApiResults.Error(TenancyErrors.WrongPassword);
            }

            // Not the request's token: from the tenth attempt on, counting has already started a cooldown, and a client that goes away
            // right after its correct password was verified must not be left with it (as login does).
            await streaks.ClearAsync(identifier, CancellationToken.None);
        }
        else
        {
            // A live reauth ticket of this browser whose identity is a login of the caller; nothing is used up here, the lockout is not consulted.
            ProviderBindingCookie.TryRead(http.Request, out binding);
            var ticket = await ProviderTickets.FindAsync(db, ticketText!, binding, StorableTime.Now(clock), http.RequestAborted);
            if (ticket is not { Purpose: ProviderTicketPurpose.Reauth }
                || !await ProviderLogins.IsLoginOfAsync(db, user.Id, ticket.Provider, ticket.IdentityKey, http.RequestAborted))
            {
                await RecordRefusalAsync(audit, caller, "reauth_failed");
                return ApiResults.Error(TenancyErrors.ReauthFailed);
            }

            // Amendment A12: used up inside the deletion's transaction, after the company lock, with the clock read there.
            confirmInside = async cancellationToken =>
            {
                var used = await ProviderTickets.ConsumeAsync(
                    db, ticketText!, binding!, ProviderTicketPurpose.Reauth, StorableTime.Now(clock), cancellationToken);
                if (used is null || !await ProviderLogins.IsLoginOfAsync(db, user.Id, used.Provider, used.IdentityKey, cancellationToken))
                {
                    return TenancyErrors.ReauthFailed;
                }

                audit.Stage(new AuditEntry
                {
                    Kind = AuditKinds.ProviderReauthenticated,
                    ActorUserId = user.Id,
                    SubjectUserId = user.Id,
                    SubjectEmail = user.Email,
                    OrgId = caller.CompanyId,
                    OrgName = caller.CompanyName,
                    Details = new Dictionary<string, object?> { ["provider"] = used.Provider },
                });
                return null;
            };
        }

        var outcome = await deletion.DeleteAsync(Actor.Of(caller), caller.CompanyId, name, http.RequestAborted, confirmInside);
        if (outcome.Succeeded)
        {
            if (ticketText is not null)
            {
                ProviderBindingCookie.Clear(http.Response);
            }

            return ApiResults.NoContent();
        }

        if (outcome.Error == TenancyErrors.ReauthFailed)
        {
            await RecordRefusalAsync(audit, caller, "reauth_failed");   // the deletion was rolled back; the row is written alone
            return ApiResults.Error(TenancyErrors.ReauthFailed);
        }

        // A company that has vanished while this request waited for its lock is a membership that is gone: the token no longer
        // matches the database, which is what the contract says for that case.
        return outcome.Error == TenancyErrors.NotFound ? ApiResults.Error(TenancyErrors.PermissionsChanged) : ApiResults.Refused(outcome);
    }
```

and add to the usings `using Auth.Infrastructure.Persistence;`, `using Auth.Server.Account;`, `using Auth.Server.Email;`,
`using Auth.Server.Providers;`. Update the method's summary: replace `/// the database), the body, an account that has no password (<c>403 reauth_required</c> for any password: spec 0011, Amendment A9; nothing is`
with `/// the database), the body (spec 0012: the name and exactly one of a password and a provider ticket), the fit of the confirmation (<c>403
/// password_required</c>, <c>403 reauth_required</c>: spec 0011, Amendment A9; spec 0012; nothing is` and add at its end: `A provider ticket
/// (spec 0012) must be a live <c>reauth</c> ticket of a login of the caller; it is used up inside the deletion's transaction, after the company
/// lock, with <c>provider.reauthenticated</c> (Amendment A12).`

The address lock is not taken by this endpoint (spec 0008 takes the company lock); the ticket's use under the company lock is what the
amendment asks. `ApiResults.Refused(outcome)` still maps `invalid_request` and `permission_not_held` as before.

(c) In `src/Auth.Server/Api/TenancyEndpoints.cs`, replace

```csharp
                + "An admin whose account has no password (made through Google or Microsoft) gets `403 reauth_required` for any password: nothing is counted or written and "
                + "the company stays; they set a password with \"Forgot your password?\" first.")
            .ReadsJson<DeleteOrgRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.WrongPassword, TenancyErrors.PermissionNotHeld, TenancyErrors.ReauthRequired)
```

with

```csharp
                + "The body holds the name and exactly one of `password` and `provider_ticket` (spec 0012; both, neither or a member of the wrong "
                + "type is `400 invalid_request`). An admin with a password confirms with it (`403 password_required` for a ticket); an admin whose "
                + "account has no password (made through a provider) confirms with a ticket of a round trip with `purpose=reauth` and the browser's "
                + "`auth_pt` (`403 reauth_required` for a password); neither is counted or written. A ticket that is unknown, used, expired (5 "
                + "minutes), of another purpose, without its `auth_pt` or of an identity that is not a login of the caller is `403 reauth_failed`, "
                + "recorded, and nothing is deleted; it is used up inside the deletion, so a deletion refused afterwards keeps it, and the answer "
                + "clears `auth_pt`.")
            .ReadsJson<DeleteOrgRequest>()
            .ReadsCookie(ProviderBindingCookie.Name, "With `provider_ticket`: the binding of the ticket, set by the callback that made it.")
            .Produces(StatusCodes.Status204NoContent)
            .SetsHeader(StatusCodes.Status204NoContent, "Set-Cookie", $"With `provider_ticket`: `{ProviderBindingCookie.Name}` emptied, with `Max-Age=0`.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(
                StatusCodes.Status403Forbidden, TenancyErrors.WrongPassword, TenancyErrors.PermissionNotHeld, TenancyErrors.ReauthRequired,
                TenancyErrors.PasswordRequired, TenancyErrors.ReauthFailed)
```

and in the same description replace `"caller's own password. An access token still held gets` with `"caller's confirmation. An access token still held gets`.

- [ ] **Step 5: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.OrgDeletionProviderTests -class Auth.IntegrationTests.OrgDeleteEndpointTests -class Auth.IntegrationTests.OrgDeletePermissionTests -class Auth.IntegrationTests.ProviderTicketTests -class Auth.IntegrationTests.AuditProviderFlowsTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests -class Auth.IntegrationTests.DeleteOrgCommandTests`
passes.
The tests of spec 0008 must pass unchanged but for the two body rows added. Mutate and see a test fail, then restore: use the ticket up before
`deletion.DeleteAsync` (`A_deletion_refused_after_the_ticket_was_used_up_gives_the_ticket_back` and
`A_ticket_used_up_by_the_confirmation_while_the_deletion_waited_for_the_company_lock_deletes_nothing` fail); read the clock in the endpoint and
pass it in (`A_ticket_that_expired_while_the_deletion_waited_is_refused` fails); skip the use of the ticket in `confirmInside`
(`A_ticket_used_up_by_the_confirmation_while_the_deletion_waited_for_the_company_lock_deletes_nothing` deletes the company with a ticket the
confirmation had used).

- [ ] **Step 6: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): delete a company with a provider confirmation`.

---

## Day 4 — Monday 11.05: the limits and the audit flows, the e2e script, the settings screen

### Task 14: The limits, the audit flows, GitHub through every flow, the last API checks (changed by Amendment B at the end of this plan: read it first)

**Files:**
- Modify: `src/Auth.Server/RateLimiting/RatePolicy.cs`
- Create (tests): `tests/Auth.IntegrationTests/AuditSettingsFlowsTests.cs`, `tests/Auth.IntegrationTests/GitHubFlowTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/RatePolicyTests.cs`, `tests/Auth.IntegrationTests/RateLimitMiddlewareTests.cs`,
  `tests/Auth.IntegrationTests/JsonCharsetTests.cs`, `tests/Auth.IntegrationTests/TestingEndpointTests.cs`

**Interfaces:**
- Consumes: every endpoint of Tasks 7 to 13; `ProviderTestBase(postgres, keys, github: true)` and `GitHubPerson` (Task 6);
  `FakeOidcServer.GitHub.IssuedTokens` (Task 4); `AuthAppFactory.GitHubClientSecret` (Task 3); `OrgApi` (Task 13).
- Produces: `RatePolicies.Classify` sends `POST /auth/account/confirm`, `/auth/providers/connect` and `/auth/account/password` to `login`.

**Contract counts:** `RatePolicyTests` and `RateLimitMiddlewareTests` gain the five settings paths; `JsonCharsetTests` gains the four settings
`POST`s (the guard already covers them: Amendment A9). Nothing else.

**Decisions the plan takes here:**
- The three `login` paths join the existing `login` branch by exact path (as the provider token paths do); the unlink and the testing endpoint
  need no change, they fall through to `general`. `ProviderPaths.TokenPaths` is not touched (the unlink must not become `login`).
- `AuditSettingsFlowsTests` drives every new row kind and every new `details` member once, through the endpoints, and runs
  `AssertNoSecretsAsync` over every row with every secret the flows handled (passwords, tickets, bindings, both tokens of every session, the
  fake GitHub's tokens and the GitHub client secret). `GitHubFlowTests` runs spec 0011's flows with a GitHub ticket.

- [ ] **Step 1: Write the failing tests.**

In `tests/Auth.IntegrationTests/RatePolicyTests.cs`, in the theory `A_request_counts_against_one_policy_by_its_method_and_path`, after
`    [InlineData("POST", "/auth/providers/finish", RatePolicy.Login)]` add

```csharp
    [InlineData("POST", "/auth/account/confirm", RatePolicy.Login)]   // spec 0012 → Limits: each judges a password or ends a provider round trip
    [InlineData("POST", "/auth/providers/connect", RatePolicy.Login)]
    [InlineData("POST", "/auth/account/password", RatePolicy.Login)]
    [InlineData("POST", "/AUTH/Account/Password/", RatePolicy.Login)]
    [InlineData("POST", "/auth/account/logins/unlink", RatePolicy.General)]
    [InlineData("POST", "/auth/testing/end-confirmation", RatePolicy.General)]
    [InlineData("GET", "/auth/providers/github/callback", RatePolicy.Login)]
    [InlineData("GET", "/auth/account/confirm", RatePolicy.General)]
```

In `tests/Auth.IntegrationTests/RateLimitMiddlewareTests.cs`, in the theory `Each_policy_refuses_at_its_own_number`, after
`    [InlineData("POST", "/auth/providers/finish", 3)]` add

```csharp
    [InlineData("POST", "/auth/account/confirm", 3)]   // spec 0012 → Limits
    [InlineData("POST", "/auth/providers/connect", 3)]
    [InlineData("POST", "/auth/account/password", 3)]
    [InlineData("POST", "/auth/account/logins/unlink", 5)]
    [InlineData("POST", "/auth/testing/end-confirmation", 5)]   // the setting is off here: a 404, counted as general
```

and add before the class's last closing brace:

```csharp

    [Fact]
    public async Task The_confirmation_and_the_login_share_one_limit()   // spec 0012 → Limits: one login bucket per address
    {
        for (var i = 1; i <= 2; i++)
        {
            using var login = await UnknownLogin(i);
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        }

        using (var confirm = await Send(HttpMethod.Post, "/auth/account/confirm"))
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, confirm.StatusCode);   // the third request of the bucket of three
        }

        using var over = await Send(HttpMethod.Post, "/auth/account/password");

        await RateLimitApi.AssertTooManyRequestsAsync(over);
    }
```

(The class's host sets the `login` limit to 3 and counts the requests of `Remote`, which `Send` and `UnknownLogin` carry: the same two helpers as
`The_provider_steps_and_the_login_share_one_limit`, which stays as it is.)

In `tests/Auth.IntegrationTests/JsonCharsetTests.cs`, in the theory
`A_body_declared_utf_16_is_a_415_on_every_endpoint_that_reads_one_and_before_authentication`, after `    [InlineData("POST", "/auth/providers/finish")]` add

```csharp
    [InlineData("POST", "/auth/account/confirm")]   // spec 0012, criterion 26: the guard covers every POST under /auth/
    [InlineData("POST", "/auth/providers/connect")]
    [InlineData("POST", "/auth/account/logins/unlink")]
    [InlineData("POST", "/auth/account/password")]
```

In `tests/Auth.IntegrationTests/TestingEndpointTests.cs`, add before the class's last closing brace:

```csharp

    [Fact]
    public async Task With_the_setting_on_the_endpoint_is_still_not_in_the_openapi_document()   // criterion 26
    {
        using var response = await Client.GetAsync("/auth/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("/auth/testing", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
```

`tests/Auth.IntegrationTests/GitHubFlowTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0012, criterion 6: a GitHub ticket through the exchange, the link, the finish and the invitation, as spec 0011 says for Google.</summary>
public sealed class GitHubFlowTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys, github: true)
{
    private const string Ola = FakePeople.GitHubEmail;

    private Task<List<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>> GitHubLoginsAsync() =>
        InDbAsync(db => db.UserLogins.AsNoTracking().Where(l => l.LoginProvider == "github").ToListAsync(TestContext.Current.CancellationToken));

    private static async Task<string?> NextOfAsync(HttpResponseMessage exchanged) =>
        (await TenancyApi.ReadOkAsync(exchanged)).GetProperty("next").GetString();

    [Fact]
    public async Task A_linked_github_identity_signs_in_through_the_exchange_with_provider_github()   // spec 0011, criterion 10
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "github", FakePeople.GitHubId);

        using var exchanged = await ProviderApi.Exchange(Client, await TicketAsync("github", GitHubPerson()));

        var session = await SessionApi.ReadSessionAsync(exchanged);
        Assert.Equal(user.Id.ToString(), AccessTokens.Text(session.AccessToken, "sub"));
        Assert.Equal("github", AuditApi.Text(await SingleAsync(AuditKinds.LoginSucceeded), "provider"));
    }

    [Fact]
    public async Task An_unlinked_identity_with_the_address_of_an_account_is_next_link_and_the_link_adds_the_decimal_id()   // spec 0011, criteria 11 and 15
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("github", GitHubPerson());

        using (var exchanged = await ProviderApi.Exchange(Client, ticket))
        {
            Assert.Equal("link", await NextOfAsync(exchanged));
        }

        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var login = Assert.Single(await GitHubLoginsAsync());
        Assert.Equal((user.Id, FakePeople.GitHubId, "GitHub"), (login.UserId, login.ProviderKey, login.ProviderDisplayName));
        var row = await SingleAsync(AuditKinds.ProviderLinked);
        Assert.Equal(("github", "sign_in"), (AuditApi.Text(row, "provider"), AuditApi.Text(row, "via")));
    }

    [Theory]
    [InlineData("private")]
    [InlineData("organization")]
    public async Task A_github_identity_with_no_account_finishes_the_sign_up(string kind)   // spec 0011, criteria 12, 13 and 19
    {
        var ticket = await TicketAsync("github", GitHubPerson());

        using (var exchanged = await ProviderApi.Exchange(Client, ticket))
        {
            Assert.Equal("finish_signup", await NextOfAsync(exchanged));
        }

        using var finished = await ProviderApi.Finish(Client, ticket, kind, kind == "organization" ? "Firma" : null);

        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Equal(FakePeople.GitHubId, Assert.Single(await GitHubLoginsAsync()).ProviderKey);
        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal(("github", kind), (AuditApi.Text(completed, "provider"), AuditApi.Text(completed, "kind")));
        Assert.Equal("github", AuditApi.Text(await SingleAsync(AuditKinds.LoginSucceeded), "provider"));
    }

    [Fact]
    public async Task A_github_person_without_a_primary_address_is_403_no_email()   // spec 0011, criterion 11; spec 0012, criterion 4
    {
        using var exchanged = await ProviderApi.Exchange(Client, await TicketAsync("github", new FakeSignIn(FakePeople.GitHubNoPrimary)));

        await TenancyApi.AssertErrorAsync(exchanged, HttpStatusCode.Forbidden, "no_email");
    }

    [Fact]
    public async Task An_unverified_primary_address_is_next_link_and_never_a_finish()   // spec 0011, Settled 8
    {
        var ticket = await TicketAsync("github", new FakeSignIn(FakePeople.GitHubUnverified));

        using (var exchanged = await ProviderApi.Exchange(Client, ticket))
        {
            Assert.Equal("link", await NextOfAsync(exchanged));
        }

        using var finished = await ProviderApi.Finish(Client, ticket, "private");
        await TenancyApi.AssertErrorAsync(finished, HttpStatusCode.Forbidden, "email_not_verified");
    }

    private async Task<string> InvitationTokenAsync(string address)
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, address, "user");
        await EnqueueInvitationAsync(invite, address);
        await DispatchAsync();
        return TokenIn(Mail.Sent.Last(m => m.To == address));
    }

    [Fact]
    public async Task An_invitation_is_accepted_through_github_with_provider_github()   // spec 0011, criterion 20
    {
        var token = await InvitationTokenAsync(Ola);

        using var accepted = await ProviderApi.Exchange(Client, await TicketAsync("github", GitHubPerson(), "?invite=" + token));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var row = await SingleAsync(AuditKinds.InviteAccepted);
        Assert.Equal("github", AuditApi.Text(row, "provider"));
        Assert.Equal(FakePeople.GitHubId, Assert.Single(await GitHubLoginsAsync()).ProviderKey);
    }

    [Fact]
    public async Task An_invitation_through_a_github_account_whose_primary_address_is_another_is_403_address_mismatch()   // Review Focus 4
    {
        // The invited address is the person's verified secondary entry at GitHub: only the primary entry counts.
        var token = await InvitationTokenAsync(FakePeople.GitHubSecondaryEmail);

        using var refused = await ProviderApi.Exchange(Client, await TicketAsync("github", GitHubPerson(), "?invite=" + token));

        await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.Forbidden, "address_mismatch");
        Assert.Empty(await GitHubLoginsAsync());
    }

    [Fact]
    public async Task A_github_connect_and_unlink_run_as_for_the_other_providers()   // spec 0012, criteria 12 and 16 with GitHub
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);

        using (var connected = await SettingsApi.Connect(Client, session, await TicketAsync("github", GitHubPerson(), "?purpose=connect")))
        {
            Assert.Equal(HttpStatusCode.NoContent, connected.StatusCode);
        }

        using var unlinked = await SettingsApi.Unlink(Client, session, "github");

        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        Assert.Empty(await GitHubLoginsAsync());
        Assert.Equal("github", AuditApi.Text(await SingleAsync(AuditKinds.ProviderUnlinked), "provider"));
        _ = user;
    }
}
```

(`ProviderApi.Finish(client, ticket, kind, companyName)` is spec 0011's helper; the organization finish needs `company_name`.)

`tests/Auth.IntegrationTests/AuditSettingsFlowsTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0012 → Audit events (criteria 25 and 7): every new kind and every new <c>details</c> member, written through the endpoints, with its
/// fields, and no row holds a password, a ticket, a cookie, a token, or GitHub's token or secret.
/// </summary>
public sealed class AuditSettingsFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys, github: true)
{
    private const string Pat = "pat@firma.test";
    private const string PatPassword = UserPassword;
    private const string PatNewPassword = "Fresh-Passw0rd-2";

    private readonly List<string> _secrets = [PatPassword, PatNewPassword, "Wrong-Passw0rd-9", AuthAppFactory.GitHubClientSecret];

    private async Task<ProviderApi.Ticket> TrackedTicketAsync(string provider, FakeSignIn person, string query = "")
    {
        var ticket = await TicketAsync(provider, person, query);
        _secrets.Add(ticket.Value);
        _secrets.Add(ticket.Binding);
        return ticket;
    }

    private SessionApi.Session Track(SessionApi.Session session)
    {
        _secrets.Add(session.AccessToken);
        _secrets.Add(session.RefreshToken);
        return session;
    }

    private async Task<SessionApi.Session> SessionOfAsync(HttpResponseMessage response)
    {
        using (response)
        {
            return Track(await SessionApi.ReadSessionAsync(response));
        }
    }

    private static async Task ExpectAsync(Task<HttpResponseMessage> sent, HttpStatusCode status)
    {
        using var response = await sent;
        Assert.Equal(status, response.StatusCode);
    }

    private static void AssertOwnRow(AuditEvent row, Guid userId, string email)
    {
        Assert.Equal((userId, userId, email), (row.ActorUserId!.Value, row.SubjectUserId!.Value, row.SubjectEmail));
        Assert.NotNull(row.OrgId);
    }

    [Fact]
    public void The_four_new_kinds_are_in_the_contract()   // criterion 25
    {
        Assert.Equal(32, AuditKinds.All.Count);
        Assert.Contains(AuditKinds.AccountConfirmed, AuditKinds.All);
        Assert.Contains(AuditKinds.AccountConfirmRefused, AuditKinds.All);
        Assert.Contains(AuditKinds.ProviderUnlinked, AuditKinds.All);
        Assert.Contains(AuditKinds.PasswordChanged, AuditKinds.All);
    }

    [Fact]
    public async Task Every_row_of_the_settings_flows_holds_its_fields_and_no_secret()   // criteria 25 and 7
    {
        // A GitHub sign-up: signup.completed and login.succeeded name github.
        var gitHub = await TrackedTicketAsync("github", GitHubPerson());
        var ola = await SessionOfAsync(await ProviderApi.Finish(Client, gitHub, "private"));
        var olaId = Guid.Parse(AccessTokens.Text(ola.AccessToken, "sub"));

        // The confirmation with GitHub (account.confirmed, method github), a refused one (reauth_failed), the connect of Google (via settings),
        // the unlink of GitHub (provider.unlinked, and login.succeeded via session_renewal).
        Clock.Advance(TimeSpan.FromMinutes(6));
        ola = Track(await SessionApi.RefreshOk(Client, ola.RefreshToken));
        await ExpectAsync(SettingsApi.Post(Client, SettingsApi.ConfirmPath, ola, new { provider_ticket = "nope" }), HttpStatusCode.Forbidden);
        await ExpectAsync(SettingsApi.ConfirmWithTicket(Client, ola, await TrackedTicketAsync("github", GitHubPerson(), "?purpose=reauth")), HttpStatusCode.NoContent);
        await ExpectAsync(
            SettingsApi.Connect(Client, ola, await TrackedTicketAsync("google", GooglePerson(email: FakePeople.GitHubEmail, subject: "100000000000000000088"), "?purpose=connect")),
            HttpStatusCode.NoContent);
        ola = await SessionOfAsync(await SettingsApi.Unlink(Client, ola, "github"));

        // A password account: a wrong and a right confirmation, a wrong and a right password change.
        var pat = await CreateUserAsync(Pat, confirmed: true);
        var patSession = Track(await SessionApi.LoginAsync(Client, Pat, PatPassword));
        Clock.Advance(LockoutApi.HumanPace);
        await ExpectAsync(SettingsApi.Confirm(Client, patSession, new { password = "Wrong-Passw0rd-9" }), HttpStatusCode.Forbidden);
        Clock.Advance(LockoutApi.HumanPace);
        await ExpectAsync(SettingsApi.Confirm(Client, patSession, new { password = PatPassword }), HttpStatusCode.NoContent);
        Clock.Advance(LockoutApi.HumanPace);
        await ExpectAsync(SettingsApi.ChangePassword(Client, patSession, "Wrong-Passw0rd-9", PatNewPassword), HttpStatusCode.Forbidden);
        Clock.Advance(LockoutApi.HumanPace);
        patSession = await SessionOfAsync(await SettingsApi.ChangePassword(Client, patSession, PatPassword, PatNewPassword));

        // The link of spec 0011 (via sign_in), an invitation through GitHub, and a company deleted with a ticket after a refused one.
        var anna = await CreateUserAsync("anna@firma.test", confirmed: true);
        await SessionOfAsync(await ProviderApi.Link(
            Client, await TrackedTicketAsync("google", GooglePerson(email: "anna@firma.test", subject: "100000000000000000033")), UserPassword));
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, "jan@firma.test", "user");
        await EnqueueInvitationAsync(invite, "jan@firma.test");
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == "jan@firma.test"));
        _secrets.Add(inviteToken);
        await SessionOfAsync(await ProviderApi.Exchange(
            Client, await TrackedTicketAsync("github", GitHubPerson(email: "jan@firma.test", subject: "70000077"), "?invite=" + inviteToken)));
        var (beta, bossId, bossToken) = await CompanyWithAdminAsync("Beta", "boss@beta.test");
        _secrets.Add(bossToken);
        await InDbAsync(db => db.Users.Where(u => u.Id == bossId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.PasswordHash, (string?)null), TestContext.Current.CancellationToken));
        await LinkAsync(bossId, "github", "70000055");
        await ExpectAsync(OrgApi.Delete(Client, bossToken, new { name = "Beta", provider_ticket = "nope" }), HttpStatusCode.Forbidden);
        await ExpectAsync(
            OrgApi.DeleteWithTicket(Client, bossToken, "Beta", await TrackedTicketAsync("github", GitHubPerson(email: "boss@beta.test", subject: "70000055"), "?purpose=reauth")),
            HttpStatusCode.NoContent);

        // The fields of each new row and each new member.
        var confirmed = await AuditAsync(AuditKinds.AccountConfirmed);
        Assert.Equal(2, confirmed.Count);
        Assert.All(confirmed, row => Assert.Equal(["method"], AuditApi.DetailNames(row)));
        Assert.Equal(["github", "password"], confirmed.Select(r => AuditApi.Text(r, "method")!).Order(StringComparer.Ordinal));
        AssertOwnRow(confirmed.Single(r => AuditApi.Text(r, "method") == "github"), olaId, FakePeople.GitHubEmail);
        AssertOwnRow(confirmed.Single(r => AuditApi.Text(r, "method") == "password"), pat.Id, Pat);

        var refused = await AuditAsync(AuditKinds.AccountConfirmRefused);
        Assert.All(refused, row => Assert.Equal(["action", "reason"], AuditApi.DetailNames(row)));
        Assert.Equal(
            ["confirm:reauth_failed", "confirm:wrong_password", "password:wrong_password"],
            refused.Select(r => AuditApi.Text(r, "action") + ":" + AuditApi.Text(r, "reason")).Order(StringComparer.Ordinal));

        var unlinked = await SingleAsync(AuditKinds.ProviderUnlinked);
        Assert.Equal(["provider"], AuditApi.DetailNames(unlinked));
        Assert.Equal("github", AuditApi.Text(unlinked, "provider"));
        AssertOwnRow(unlinked, olaId, FakePeople.GitHubEmail);

        var changed = await SingleAsync(AuditKinds.PasswordChanged);
        Assert.Empty(AuditApi.DetailNames(changed));
        AssertOwnRow(changed, pat.Id, Pat);

        var linked = await AuditAsync(AuditKinds.ProviderLinked);
        Assert.All(linked, row => Assert.Equal(["provider", "via"], AuditApi.DetailNames(row)));
        Assert.Equal(["google:settings", "google:sign_in"], linked.Select(r => AuditApi.Text(r, "provider") + ":" + AuditApi.Text(r, "via")).Order(StringComparer.Ordinal));

        var logins = await AuditAsync(AuditKinds.LoginSucceeded);
        var renewals = logins.Where(r => AuditApi.Text(r, "via") == "session_renewal").ToList();
        Assert.Equal(2, renewals.Count);
        Assert.All(renewals, row => Assert.Equal(["via"], AuditApi.DetailNames(row)));
        Assert.Contains(logins, r => AuditApi.Text(r, "provider") == "github");
        Assert.Equal("github", AuditApi.Text(await SingleAsync(AuditKinds.SignupCompleted), "provider"));
        Assert.Equal("github", AuditApi.Text(await SingleAsync(AuditKinds.InviteAccepted), "provider"));

        var reauthenticated = await SingleAsync(AuditKinds.ProviderReauthenticated);
        Assert.Equal(("github", beta), (AuditApi.Text(reauthenticated, "provider"), reauthenticated.OrgId!.Value));
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.OrgDeleteRefused), "reason"));

        // No row holds a secret of these flows.
        _secrets.AddRange(Fake.Server.GitHub.IssuedTokens);
        Assert.NotEmpty(Fake.Server.GitHub.IssuedTokens);
        await AssertNoSecretsAsync([.. _secrets]);
        _ = anna;
        _ = company;
    }
}
```

- [ ] **Step 2: See them fail.** Build and run the classes above: the `RatePolicyTests` rows for confirm, connect and password say
`General`; the middleware rows refuse at 5; everything else passes already (the endpoints exist): that is expected — those tests are the
contract's net over Tasks 7 to 13, and any of them failing is a finding about the task that wrote the endpoint (stop and report it).

- [ ] **Step 3: The limits.** In `src/Auth.Server/RateLimiting/RatePolicy.cs`, replace

```csharp
        if (trimmed.Equals("/auth/login", StringComparison.OrdinalIgnoreCase)
            || ProviderPaths.TokenPaths.Any(tokenPath => trimmed.Equals(tokenPath, StringComparison.OrdinalIgnoreCase)))
        {
            return RatePolicy.Login;
        }
```

with

```csharp
        if (trimmed.Equals("/auth/login", StringComparison.OrdinalIgnoreCase)
            || ProviderPaths.TokenPaths.Any(tokenPath => trimmed.Equals(tokenPath, StringComparison.OrdinalIgnoreCase))
            || LoginPolicyPaths.Any(loginPath => trimmed.Equals(loginPath, StringComparison.OrdinalIgnoreCase)))
        {
            return RatePolicy.Login;
        }
```

and add to the class, before `Classify`:

```csharp
    /// <summary>
    /// Spec 0012 → Limits: the settings endpoints that judge a password or end a provider round trip. The unlink and the testing endpoint are
    /// general, as every other POST.
    /// </summary>
    private static readonly string[] LoginPolicyPaths =
        [AccountConfirmEndpoint.Path, ProviderPaths.Connect, SettingsRequestHandler.PasswordPath];
```

(add `using Auth.Server.Account;`). If the class is `static` and holds no fields yet, the `static readonly` field is the first; place it at the
top of the class body.

- [ ] **Step 4: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.RatePolicyTests -class Auth.IntegrationTests.RateLimitMiddlewareTests -class Auth.IntegrationTests.RatePolicySignupTests -class Auth.IntegrationTests.RateLimitAuditTests -class Auth.IntegrationTests.JsonCharsetTests -class Auth.IntegrationTests.TestingEndpointTests -class Auth.IntegrationTests.GitHubFlowTests -class Auth.IntegrationTests.AuditSettingsFlowsTests`
passes. Mutate and see a test fail, then restore: add the unlink to `LoginPolicyPaths` (its rows fail); put the refresh cookie's value into the
`details` of the renewal's `login.succeeded` in `SessionRenewal` (`Every_row_of_the_settings_flows_holds_its_fields_and_no_secret` fails at
`AssertNoSecretsAsync`).

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): limits of the settings endpoints; the audit and GitHub flows end to end in the suite`.

---

### Task 15: `scripts/e2e-settings.sh` and the earlier e2e script (changed by Amendment B at the end of this plan: read it first)

**Files:**
- Create: `scripts/e2e-settings.sh` (LF; the orchestrator marks it executable in the index)
- Modify: `scripts/e2e-providers.sh`, `scripts/e2e-web.sh` (its header comment only)

**Interfaces:**
- Consumes: the stack of `scripts/e2e-providers.sh` (the web sample with the fake's overlay, which since Task 3 sets the GitHub keys and
  `Auth__Testing__WindowEndpoint: "true"`, and whose notes manifest lists the three providers); the fake's inspection endpoint
  `/_fake/github/revocations` (Task 4); every endpoint of Tasks 7 to 13.
- Produces: nothing other tasks use.

**Contract counts:** `scripts/e2e-providers.sh` expects three providers and six provider paths.

**Decisions the plan takes here:**
- The new script copies the helper block of `scripts/e2e-providers.sh` unchanged (from `wait_ok()` to `secrets_in_audit()`) and adds the
  helpers of a settings call; no shared library is made, so that the earlier script stays as its verifiers know it.
- **The three mails are read from the mail catcher, and their count from the notice limit's row** (`MailRequestLimits`, kind `100`), not from
  the queue table: on a running stack the dispatcher sends a queued row within a second of the commit and deletes it, so a read of
  `MailRequests` after the answer races it. Criterion 31 and Amendment A13 of the spec say so (the orchestrator's decision D41); the queue
  rows themselves are asserted by `SettingsMailTests`, with the dispatcher run by hand.
- The window is closed only through the testing endpoint (`POST /auth/testing/end-confirmation`), never by waiting or a shorter window.
- Every person is made with a fresh address of the fake's free-address mode (`person=custom`), so that the script runs again on the same stack.

- [ ] **Step 1: The earlier script.** In `scripts/e2e-providers.sh` (LF; edit with the Edit tool):

(a) replace

```bash
#   1. health is Healthy; the fake answers; GET /auth/options -> {"signup":"self-service","private_signup":"self-service",
#      "providers":["google","microsoft"]}; the OpenAPI description names the five provider paths - criteria 2, 29.
#   2. the start: an unknown name -> 404 not_found; another purpose -> 400 invalid_request; Google and Microsoft -> 302 to the fake with the
#      client id, the callback address on this origin, the code flow, the scopes, S256, a state, a nonce and prompt=select_account; the
#      request-forgery cookie is HttpOnly, Secure, SameSite=Lax, Path=/auth/providers - criteria 5, 6.
```

with

```bash
#   1. health is Healthy; the fake answers; GET /auth/options -> {"signup":"self-service","private_signup":"self-service",
#      "providers":["google","microsoft","github"]} (spec 0012); the OpenAPI description names the six provider paths (spec 0012 adds the
#      connect) - criteria 2, 29.
#   2. the start: an unknown name -> 404 not_found; another purpose -> 400 invalid_request; Google and Microsoft -> 302 to the fake with the
#      client id, the callback address on this origin, the code flow, the scopes, S256, a state, a nonce and prompt=select_account; GitHub ->
#      302 with user:email, S256, a state and no nonce (spec 0012); the request-forgery cookie is HttpOnly, Secure, SameSite=Lax,
#      Path=/auth/providers - criteria 5, 6.
```

(b) replace

```bash
expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft"]}'
```

with

```bash
expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft","github"]}'
```

(c) replace

```bash
expect_eq "step 1: the five provider paths are described" \
  "$(val "sorted(p for p in d['paths'] if p.startswith('/auth/providers/'))")" \
  "['/auth/providers/exchange', '/auth/providers/finish', '/auth/providers/link', '/auth/providers/{name}/callback', '/auth/providers/{name}/start']"
pass "step 1: health is Healthy; the fake answers; GET /auth/options offers google and microsoft; the description names the five provider paths"
```

with

```bash
expect_eq "step 1: the six provider paths are described" \
  "$(val "sorted(p for p in d['paths'] if p.startswith('/auth/providers/'))")" \
  "['/auth/providers/connect', '/auth/providers/exchange', '/auth/providers/finish', '/auth/providers/link', '/auth/providers/{name}/callback', '/auth/providers/{name}/start']"
pass "step 1: health is Healthy; the fake answers; GET /auth/options offers google, microsoft and github; the description names the six provider paths"
```

(d) replace

```bash
call GET /auth/providers/github/start
expect_error "step 2: an unknown name" 404 not_found
```

with

```bash
call GET /auth/providers/gitlab/start
expect_error "step 2: an unknown name" 404 not_found
```

(e) replace

```bash
pass "step 2: an unknown name -> 404, another purpose -> 400; each start -> 302 to the fake with the code flow, PKCE, a state, a nonce and the callback on this origin; the cookie is Lax"
```

with

```bash
# Spec 0012: GitHub's start, through the fake's minimal discovery document under /github.
call GET /auth/providers/github/start
expect_status "step 2: the start of github" 302
header location > "$tmp/start.url"
[[ "$(cat "$tmp/start.url")" == "$FAKE_URL/github/authorize?"* ]] || fail "step 2: the start of github does not go to the fake"
expect_eq "step 2: github: redirect_uri" "$(url_param "$tmp/start.url" query redirect_uri)" "$BASE_URL/auth/providers/github/callback"
expect_eq "step 2: github: scope" "$(url_param "$tmp/start.url" query scope)" "user:email"
expect_eq "step 2: github: code_challenge_method" "$(url_param "$tmp/start.url" query code_challenge_method)" "S256"
expect_eq "step 2: github: prompt" "$(url_param "$tmp/start.url" query prompt)" "select_account"
expect_eq "step 2: github: no nonce" "$(url_param "$tmp/start.url" query nonce)" ""
for member in state code_challenge; do keep_secret "$(url_param "$tmp/start.url" query "$member")"; done
keep_secret "$(pair_of "$(set_cookie_line 'OpenIddict\.Client\.State[^=]*')" | cut -d= -f2-)"
pass "step 2: an unknown name -> 404, another purpose -> 400; each start -> 302 to the fake with the code flow, PKCE, a state and the callback on this origin (a nonce for OpenID Connect, user:email for GitHub); the cookie is Lax"
```

The script's other steps are unchanged: its mail waits match subjects ("Complete your sign-up", "You are invited"), which the notice of PW's
link ("Microsoft was connected to …") does not start with, and its audit queries match `provider.linked` whatever its `via`.

In `scripts/e2e-web.sh` (LF; a comment only, no code), replace

```bash
# The stack includes the fake OpenID provider of spec 0011 (tests/Auth.FakeOidc/compose.yml, http://localhost:8099): the sample's manifest
# lists Google and Microsoft, e2e/11-providers.spec.ts signs in through it, and Auth-Core trusts it as its test issuer.
```

with

```bash
# The stack includes the fake provider of specs 0011 and 0012 (tests/Auth.FakeOidc/compose.yml, http://localhost:8099): the sample's manifest
# lists Google, Microsoft and GitHub, e2e/11-providers.spec.ts and e2e/12-settings.spec.ts sign in through it (the second also uses the testing
# endpoint, which the overlay turns on with Auth__Testing__WindowEndpoint), and Auth-Core trusts it as its test issuer.
```

- [ ] **Step 2: The new script.** `scripts/e2e-settings.sh` — the header, the set-up, the helper block copied, the settings helpers, then
eleven steps:

```bash
#!/usr/bin/env bash
# Real-network end-to-end check of spec 0012 (GitHub, and managing one's own logins) against the web stack with the fake provider: Auth-Core,
# PostgreSQL, the mail catcher (Mailpit), the notes service, Caddy (https://localhost:8443) and the fake (http://localhost:8099), which serves
# Google, Microsoft and, under /github, GitHub with its minimal discovery document (tests/Auth.FakeOidc/compose.yml, with GitHub's keys and
# Auth__Testing__WindowEndpoint "true"). The script plays the browser with curl, as scripts/e2e-providers.sh does; the confirmation window is
# the real one (5 minutes), and the testing endpoint closes it where a step needs it closed.
#
# What it checks, in order (NEW = a person who signs up with GitHub and has no password; OTHER = a person with a Microsoft account; PW = a person
# with a password account; ORG = a person who makes a company with GitHub). The guarding tests are in docs/superpowers/plans/0012-acceptance-map.md.
#   1. health is Healthy; GET /auth/options offers google, microsoft and github; the description names the four settings endpoints and not the
#      testing endpoint - criteria 1, 26.
#   2. NEW's GitHub round trip by the fake's non-interactive mode: the start asks for user:email with S256 and no nonce; the fragment carries a
#      ticket for sign_in; the fake saw the token revoked once (its inspection endpoint) - criteria 2, 3, 32.
#   3. NEW finishes privately and is signed in; /auth/me says confirmed_until; NEW connects Google at once, without being asked to confirm;
#      /auth/me lists github and google - criteria 8, 12, 23.
#   4. the connect's refusals: a sign_in ticket -> 400 invalid_ticket; a second Google identity -> 409 provider_in_use; OTHER's Microsoft
#      identity -> 409 provider_already_linked; another address -> 403 address_mismatch; an unverified one -> 403 email_not_verified; none ->
#      403 no_email; no refresh cookie -> 401 session_required; the window ended by the testing endpoint -> 403 confirmation_required -
#      criteria 13, 24.
#   5. NEW confirms: a password -> 403 reauth_required; a sign_in ticket -> 403 reauth_failed; a reauth ticket of GitHub -> 204, the window
#      open again - criteria 10, 11.
#   6. NEW disconnects: an unknown name -> 404 not_found; GitHub -> 200 with a renewed session, the old refresh cookie -> 401 invalid_grant;
#      the last login (Google) -> 409 last_login; a password change -> 403 password_not_set - criteria 16, 17, 19.
#   7. NEW sets a first password through "forgot password" and the reset mail, and signs in with it - criterion 20.
#   8. PW, with a password: a ticket -> 403 password_required; a wrong password -> 403 wrong_password; the right one -> 204; a weak new password
#      -> 400 weak_password; the password changed -> 200 with a renewed session; the new password signs in, the old one does not - criteria
#      10, 11, 18, 19.
#   9. ORG makes a company with GitHub and deletes it with "Confirm with GitHub": a reauth ticket -> 204, auth_pt cleared - criterion 21.
#  10. the three mails, read from the mail catcher: "Google was connected", "GitHub was disconnected" (NEW) and "the password was changed"
#      (PW), in the stack's language, with no link; the notice limit of NEW's address counted two - criterion 22.
#  11. the audit rows of the run, read from the database, and no row holds a secret of the run - criteria 25, 7.
#
# Full sequence, from the repo root (the stack of scripts/e2e-providers.sh; the per-IP limiter is OFF):
#   cp .env.example .env                  # then set real local values (git-ignored); NOTES_DB_PASSWORD too
#   MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh        # signing/encryption keys into .secrets/ (git-ignored)
#   export COMPOSE_PROJECT_NAME=auth-core-settings        # a project of its own, never "auth-core" (the development stack)
#   files="-f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f samples/notes-web/compose.yml -f tests/Auth.FakeOidc/compose.yml"
#   docker compose $files --env-file .env down -v                                   # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose $files --env-file .env up -d --build
#   scripts/e2e-settings.sh               # this script (does NOT bring the stack up or down)
#   docker compose $files --env-file .env down -v                                   # tear down
#
# Needs: curl, python3 (standard library only; E2E_PYTHON=<interpreter> names another), docker compose, and host ports 8088, 8443, 8080, 8025
# and 8099 free. Env: BASE_URL (default https://localhost:8443), FAKE_URL (default http://localhost:8099), MAILPIT_URL (default
# http://localhost:8025). Takes about a minute. Re-runnable on the same stack. Exits non-zero on the first failure; prints "PASS <step>" per
# step; never prints a password, a token, a ticket, a cookie, a state, a code or a mail body.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-https://localhost:8443}"
FAKE_URL="${FAKE_URL:-http://localhost:8099}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
RETURN_URL="$BASE_URL/provider-return"
PY="${E2E_PYTHON:-python3}"
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-settings}"
[[ "$COMPOSE_PROJECT_NAME" != "auth-core" ]] || { echo "FAIL COMPOSE_PROJECT_NAME=auth-core is the development stack; use another name" >&2; exit 1; }
compose=(docker compose -f "$root/deploy/docker-compose.yml" -f "$root/samples/notes-api/compose.yml" -f "$root/samples/notes-web/compose.yml"
  -f "$root/tests/Auth.FakeOidc/compose.yml" --env-file "$root/.env")
SIGNUP_LINK="http://localhost:8088/complete-signup"
RESET_LINK="http://localhost:8088/reset"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
: > "$tmp/secrets"

fail() { echo "FAIL $*" >&2; exit 1; }
pass() { echo "PASS $*"; }

keep_secret() {
  if [[ -n "$1" ]]; then printf '%s\n' "$1" >> "$tmp/secrets"; fi
}

[[ -f "$root/.env" ]] || fail "missing $root/.env (copy .env.example and set local values)"

run="$RANDOM$RANDOM"
NEW_EMAIL="new-$run@github.test"
OTHER_EMAIL="other-$run@outlook.test"
STRANGER_EMAIL="stranger-$run@outlook.test"
PW_EMAIL="pw-$run@poczta.test"
ORG_EMAIL="org-$run@github.test"
ORG_NAME="Firma $run"
OTHER_OID="$("$PY" -c 'import uuid; print(uuid.uuid4())' | tr -d '\r')"
export E2E_PW_EMAIL="$PW_EMAIL" E2E_NEW_EMAIL="$NEW_EMAIL" E2E_ORG_NAME="$ORG_NAME"
export E2E_PASSWORD="E2e-Passw0rd-$run" E2E_WRONG_PASSWORD="E2e-Wrong-Passw0rd-$run" E2E_NEW_PASSWORD="E2e-Fresh-Passw0rd-$run"
export E2E_FIRST_PASSWORD="E2e-First-Passw0rd-$run"
for secret in "$E2E_PASSWORD" "$E2E_WRONG_PASSWORD" "$E2E_NEW_PASSWORD" "$E2E_FIRST_PASSWORD"; do keep_secret "$secret"; done

# --- helpers copied from scripts/e2e-providers.sh -----------------------------------------------------------------------------
# COPY HERE, unchanged, the lines of scripts/e2e-providers.sh from the line that starts `wait_ok() {` through the closing brace of
# `secrets_in_audit()` (the last line before `# --- Step 1: health, the fake, the options, the description`).

# --- helpers of the settings screen -------------------------------------------------------------------------------------------

# keep_session <who>: after a login's answer, keeps the bearer header in $tmp/<who>.auth and the refresh cookie's pair in $tmp/<who>.rt
keep_session() {
  local line
  expect_eq "$1: the body is a login's" "$(val 'sorted(d.keys())')" "['access_token', 'status']"
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$1.auth"
  keep_secret "$(val 'd["access_token"]')"
  line="$(set_cookie_line auth_rt)"
  [[ -n "$line" ]] || fail "$1: no auth_rt cookie"
  pair_of "$line" > "$tmp/$1.rt"
  keep_secret "$(cookie_value auth_rt)"
}

# settings <method> <path> <body-file|""> <who> [round-trip-name] [norefresh]: a settings call with <who>'s bearer token and one Cookie header
# holding <who>'s auth_rt (left out with "norefresh") and, with a round trip's name, that browser's auth_pt.
settings() {
  local cookie=""
  if [[ "${6:-}" != "norefresh" ]]; then cookie="$(cat "$tmp/$4.rt")"; fi
  if [[ -n "${5:-}" ]]; then cookie="${cookie:+$cookie; }$(sed 's/^Cookie: //' "$tmp/$5.pt" | tr -d '\r')"; fi
  if [[ -n "$cookie" ]]; then
    printf 'Cookie: %s\n' "$cookie" > "$tmp/cookie.hdr"
    call "$1" "$2" "$3" "$tmp/$4.auth" "$tmp/cookie.hdr"
  else
    call "$1" "$2" "$3" "$tmp/$4.auth"
  fi
}

confirmed_until() { # confirmed_until <who>: confirmed_until of GET /auth/me with <who>'s token and cookie ("None" when null)
  settings GET /auth/me "" "$1"
  expect_status "$1: GET /auth/me" 200
  val 'd["confirmed_until"]'
}

revocations() { # the number of revocations the fake has recorded, and the status of the last ("<n> <status>")
  curl -sS --max-time 10 "$FAKE_URL/_fake/github/revocations" -o "$tmp/revocations.json"
  "$PY" -c 'import json,sys; r=json.load(sys.stdin.buffer)["revocations"]; print(len(r), r[-1]["status"] if r else "-")' < "$tmp/revocations.json" | tr -d '\r'
}

# mail_without_link <address> <subject-start>: the newest such mail to the address names no link and no token (its text and HTML parts)
mail_without_link() {
  wait_mail "$1" "$2" 30 || fail "no mail '$2' for $1 within 30s"
  "$PY" -c '
import json, sys
mail = json.load(sys.stdin.buffer)
text = (mail.get("Text") or "") + (mail.get("HTML") or "")
if "http" in text.lower() or "token" in text.lower() or "<a " in text.lower():
    sys.exit("the mail holds a link")
' < "$tmp/mail.json" || fail "the mail '$2' to $1 holds a link"
}

notice_count() { # notice_count <address>: WindowCount of the notice limit of the address (kind 100), 0 when it has none
  local value
  value="$(psql_value "SELECT coalesce((SELECT \"WindowCount\" FROM \"MailRequestLimits\" WHERE \"Kind\" = 100 AND \"IdentifierHash\" = sha256(convert_to(upper('$1'), 'UTF8'))), 0)")"
  printf '%s' "$value"
}

# --- Step 1: health, the options, the description (criteria 1, 26) ------------------------------------------------------------
wait_ok "$BASE_URL/auth/health" 120 || fail "step 1: $BASE_URL/auth/health did not return 200 within 120s (is the stack up? see the header)"
wait_ok "$FAKE_URL/github/.well-known/openid-configuration" 60 || fail "step 1: the fake's GitHub ($FAKE_URL/github) did not answer within 60s"
wait_ok "$MAILPIT_URL/readyz" 60 || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s"
START_TS="$(psql_value "SELECT now()")"
call GET /auth/health
expect_status "step 1: health" 200 "Healthy"
call GET /auth/options
expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft","github"]}'
call GET /auth/openapi/v1.json
expect_status "step 1: the OpenAPI description" 200
expect_eq "step 1: the settings endpoints are described" \
  "$(val "sorted(p for p in d['paths'] if p in ('/auth/account/confirm', '/auth/providers/connect', '/auth/account/logins/unlink', '/auth/account/password'))")" \
  "['/auth/account/confirm', '/auth/account/logins/unlink', '/auth/account/password', '/auth/providers/connect']"
expect_eq "step 1: the testing endpoint is not" "$(val "[p for p in d['paths'] if p.startswith('/auth/testing')]")" "[]"
pass "step 1: health is Healthy; the options offer the three providers; the description names the four settings endpoints and not the testing endpoint"

# --- Step 2: NEW's GitHub round trip and the revocation (criteria 2, 3, 32) ---------------------------------------------------
BEFORE="$(revocations)"
round_trip new github "" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
expect_eq "step 2: the scope" "$(url_param "$tmp/new.start" query scope)" "user:email"
expect_eq "step 2: PKCE" "$(url_param "$tmp/new.start" query code_challenge_method)" "S256"
expect_eq "step 2: no nonce" "$(url_param "$tmp/new.start" query nonce)" ""
expect_eq "step 2: no max_age" "$(url_param "$tmp/new.start" query max_age)" ""
expect_eq "step 2: the fragment" "$(fragment_of new)" '{"purpose":"sign_in","ticket":43}'
AFTER="$(revocations)"
expect_eq "step 2: the fake saw one revocation more, answered 204" "$AFTER" "$(( ${BEFORE%% *} + 1 )) 204"
pass "step 2: GitHub's round trip asks for user:email with S256 and no nonce; the ticket is for sign_in; the token was revoked once"

# --- Step 3: NEW signs up with GitHub and connects Google at once (criteria 8, 12, 23) -----------------------------------------
ticket_json new ticket "$tmp/new.finish" "{'kind': 'private'}"
call POST /auth/providers/finish "$tmp/new.finish" "$tmp/new.pt"
expect_status "step 3: the finish" 200
keep_session new
[[ "$(confirmed_until new)" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]] || fail "step 3: confirmed_until is not a UTC time with seconds"
round_trip gconnect google "?purpose=connect" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
expect_eq "step 3: the connect fragment" "$(fragment_of gconnect)" '{"purpose":"connect","ticket":43}'
ticket_json gconnect ticket "$tmp/gconnect.body"
settings POST /auth/providers/connect "$tmp/gconnect.body" new gconnect
expect_status "step 3: the connect" 204 ""
expect_attrs "step 3: the cleared auth_pt" "$(set_cookie_line auth_pt)" "Max-Age=0"
settings GET /auth/me "" new
expect_eq "step 3: linked_providers" "$(val 'd["linked_providers"]')" "['github', 'google']"
expect_eq "step 3: has_password" "$(val 'd["has_password"]')" "False"
NEW_SUB="$(val 'd["sub"]')"
check_guid "NEW's sub" "$NEW_SUB"
pass "step 3: the GitHub finish signs NEW in with an open window; Google connects without a confirmation; /auth/me lists github and google"

# --- Step 4: the connect's refusals (criteria 13, 24) ------------------------------------------------------------------------
round_trip signin google "" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
ticket_json signin ticket "$tmp/signin.body"
settings POST /auth/providers/connect "$tmp/signin.body" new signin
expect_error "step 4: a sign_in ticket" 400 invalid_ticket
round_trip second google "?purpose=connect" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true&subject=second-$run"
ticket_json second ticket "$tmp/second.body"
settings POST /auth/providers/connect "$tmp/second.body" new second
expect_error "step 4: a second Google identity" 409 provider_in_use
round_trip other microsoft "" "person=custom&email=$(enc "$OTHER_EMAIL")&verified=true&subject=$OTHER_OID"
ticket_json other ticket "$tmp/other.finish" "{'kind': 'private'}"
call POST /auth/providers/finish "$tmp/other.finish" "$tmp/other.pt"
expect_status "step 4: OTHER's finish" 200
keep_session other
round_trip taken microsoft "?purpose=connect" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true&subject=$OTHER_OID"
ticket_json taken ticket "$tmp/taken.body"
settings POST /auth/providers/connect "$tmp/taken.body" new taken
expect_error "step 4: OTHER's Microsoft identity" 409 provider_already_linked
round_trip stranger microsoft "?purpose=connect" "person=custom&email=$(enc "$STRANGER_EMAIL")&verified=true"
ticket_json stranger ticket "$tmp/stranger.body"
settings POST /auth/providers/connect "$tmp/stranger.body" new stranger
expect_error "step 4: another address" 403 address_mismatch
round_trip unverified microsoft "?purpose=connect" "person=custom&email=$(enc "$NEW_EMAIL")&verified=false"
ticket_json unverified ticket "$tmp/unverified.body"
settings POST /auth/providers/connect "$tmp/unverified.body" new unverified
expect_error "step 4: an unverified address" 403 email_not_verified
round_trip noemail microsoft "?purpose=connect" "person=microsoft-personal&defect=no_email"
ticket_json noemail ticket "$tmp/noemail.body"
settings POST /auth/providers/connect "$tmp/noemail.body" new noemail
expect_error "step 4: no address" 403 no_email
round_trip later microsoft "?purpose=connect" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
ticket_json later ticket "$tmp/later.body"
settings POST /auth/providers/connect "$tmp/later.body" new later norefresh
expect_error "step 4: no refresh cookie" 401 session_required
settings POST /auth/testing/end-confirmation "" new
expect_status "step 4: the testing endpoint ends the window" 204 ""
expect_eq "step 4: the window is closed" "$(confirmed_until new)" "None"
settings POST /auth/providers/connect "$tmp/later.body" new later
expect_error "step 4: a closed window" 403 confirmation_required
pass "step 4: invalid_ticket, provider_in_use, provider_already_linked, address_mismatch, email_not_verified, no_email, session_required, confirmation_required"

# --- Step 5: NEW confirms (criteria 10, 11) ---------------------------------------------------------------------------------
json_body "$tmp/new.password" "{'password': os.environ['E2E_PASSWORD']}"
settings POST /auth/account/confirm "$tmp/new.password" new
expect_error "step 5: a password for an account without one" 403 reauth_required
ticket_json signin provider_ticket "$tmp/signin.confirm"
settings POST /auth/account/confirm "$tmp/signin.confirm" new signin
expect_error "step 5: a sign_in ticket" 403 reauth_failed
round_trip reauth github "?purpose=reauth" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
ticket_json reauth provider_ticket "$tmp/reauth.confirm"
settings POST /auth/account/confirm "$tmp/reauth.confirm" new reauth
expect_status "step 5: the GitHub confirmation" 204 ""
expect_attrs "step 5: the cleared auth_pt" "$(set_cookie_line auth_pt)" "Max-Age=0"
[[ "$(confirmed_until new)" != "None" ]] || fail "step 5: the window did not open"
pass "step 5: a password -> reauth_required; a sign_in ticket -> reauth_failed; GitHub's reauth ticket opens the window"

# --- Step 6: NEW disconnects (criteria 16, 17, 19) ---------------------------------------------------------------------------
json_body "$tmp/unlink.gitlab" "{'provider': 'gitlab'}"
settings POST /auth/account/logins/unlink "$tmp/unlink.gitlab" new
expect_error "step 6: an unknown name" 404 not_found
cp "$tmp/new.rt" "$tmp/old.rt"
json_body "$tmp/unlink.github" "{'provider': 'github'}"
settings POST /auth/account/logins/unlink "$tmp/unlink.github" new
expect_status "step 6: the unlink" 200
keep_session new
printf 'Cookie: %s\n' "$(cat "$tmp/old.rt")" > "$tmp/old.cookie"
call POST /auth/refresh "" "$tmp/old.cookie"
expect_status "step 6: the refresh cookie the unlink replaced" 401
expect_eq "step 6: invalid_grant" "$(val 'd["error"]')" "invalid_grant"
settings GET /auth/me "" new
expect_eq "step 6: linked_providers" "$(val 'd["linked_providers"]')" "['google']"
[[ "$(val 'd["confirmed_until"]')" != "None" ]] || fail "step 6: the renewed session lost the window"
json_body "$tmp/unlink.google" "{'provider': 'google'}"
settings POST /auth/account/logins/unlink "$tmp/unlink.google" new
expect_error "step 6: the last way in" 409 last_login
json_body "$tmp/new.change" "{'current_password': os.environ['E2E_PASSWORD'], 'new_password': os.environ['E2E_NEW_PASSWORD']}"
settings POST /auth/account/password "$tmp/new.change" new
expect_error "step 6: a password change without a password" 403 password_not_set
pass "step 6: not_found; GitHub disconnected with a renewed session (the old cookie is dead, the window kept); last_login; password_not_set"

# --- Step 7: NEW's first password through "forgot password" (criterion 20) ------------------------------------------------------
BEFORE_MAILS="$(mail_count "$NEW_EMAIL")"
json_body "$tmp/new.forgot" "{'email': os.environ['E2E_NEW_EMAIL']}"
call POST /auth/password/forgot "$tmp/new.forgot"
expect_status "step 7: forgot password" 202 ""
wait_mail "$NEW_EMAIL" "Reset your" 90 "$BEFORE_MAILS" || fail "step 7: no reset mail for NEW within 90s"
token_body "$RESET_LINK" "$tmp/new.reset.token" || fail "step 7: no usable reset link in NEW's mail"
keep_secret "$(token_of "$tmp/new.reset.token")"
"$PY" -c 'import json,os,sys; b=json.load(open(sys.argv[1])); b["new_password"]=os.environ["E2E_FIRST_PASSWORD"]; print(json.dumps(b))' \
  "$tmp/new.reset.token" > "$tmp/new.reset"
call POST /auth/password/reset "$tmp/new.reset"
expect_status "step 7: the reset" 204 ""
json_body "$tmp/new.login" "{'email': os.environ['E2E_NEW_EMAIL'], 'password': os.environ['E2E_FIRST_PASSWORD']}"
call POST /auth/login "$tmp/new.login"
expect_status "step 7: NEW signs in with the first password" 200
keep_session new
pass "step 7: a first password through the reset mail; NEW signs in with it"

# --- Step 8: PW, with a password (criteria 10, 11, 18, 19) ------------------------------------------------------------------
json_body "$tmp/pw.signup" "{'email': os.environ['E2E_PW_EMAIL'], 'kind': 'private'}"
call POST /auth/signup "$tmp/pw.signup"
expect_status "step 8: PW's private sign-up request" 202 ""
wait_mail "$PW_EMAIL" "Complete your sign-up" 90 || fail "step 8: no sign-up mail for PW within 90s"
token_body "$SIGNUP_LINK" "$tmp/pw.complete" E2E_PASSWORD || fail "step 8: no usable sign-up link in PW's mail"
keep_secret "$(token_of "$tmp/pw.complete")"
call POST /auth/signup/complete "$tmp/pw.complete"
expect_status "step 8: PW's completion" 200
keep_session pw
json_body "$tmp/pw.ticket" "{'provider_ticket': 'not-a-ticket'}"
settings POST /auth/account/confirm "$tmp/pw.ticket" pw
expect_error "step 8: a ticket for an account with a password" 403 password_required
json_body "$tmp/pw.wrong" "{'password': os.environ['E2E_WRONG_PASSWORD']}"
settings POST /auth/account/confirm "$tmp/pw.wrong" pw
expect_error "step 8: a wrong password" 403 wrong_password
json_body "$tmp/pw.right" "{'password': os.environ['E2E_PASSWORD']}"
settings POST /auth/account/confirm "$tmp/pw.right" pw
expect_status "step 8: the right password" 204 ""
json_body "$tmp/pw.weak" "{'current_password': os.environ['E2E_PASSWORD'], 'new_password': 'short'}"
settings POST /auth/account/password "$tmp/pw.weak" pw
expect_status "step 8: a weak new password" 400
expect_eq "step 8: weak_password" "$(val 'd["error"]')" "weak_password"
json_body "$tmp/pw.change" "{'current_password': os.environ['E2E_PASSWORD'], 'new_password': os.environ['E2E_NEW_PASSWORD']}"
settings POST /auth/account/password "$tmp/pw.change" pw
expect_status "step 8: the password change" 200
keep_session pw
json_body "$tmp/pw.old" "{'email': os.environ['E2E_PW_EMAIL'], 'password': os.environ['E2E_PASSWORD']}"
call POST /auth/login "$tmp/pw.old"
expect_status "step 8: the old password" 401
expect_eq "step 8: invalid_credentials" "$(val 'd["error"]')" "invalid_credentials"
json_body "$tmp/pw.new" "{'email': os.environ['E2E_PW_EMAIL'], 'password': os.environ['E2E_NEW_PASSWORD']}"
call POST /auth/login "$tmp/pw.new"
expect_status "step 8: the new password" 200
pass "step 8: password_required; wrong_password; the right password confirms; weak_password; the change renews the session; the new password signs in"

# --- Step 9: ORG deletes its company with GitHub (criterion 21) --------------------------------------------------------------
round_trip org github "" "person=custom&email=$(enc "$ORG_EMAIL")&verified=true"
ticket_json org ticket "$tmp/org.finish" "{'kind': 'organization', 'company_name': os.environ['E2E_ORG_NAME']}"
call POST /auth/providers/finish "$tmp/org.finish" "$tmp/org.pt"
expect_status "step 9: ORG's finish" 200
keep_session org
round_trip orgreauth github "?purpose=reauth" "person=custom&email=$(enc "$ORG_EMAIL")&verified=true"
ticket_json orgreauth provider_ticket "$tmp/org.delete" "{'name': os.environ['E2E_ORG_NAME']}"
call DELETE /auth/org "$tmp/org.delete" "$tmp/org.auth" "$tmp/orgreauth.pt"
expect_status "step 9: the company deletion" 204 ""
expect_attrs "step 9: the cleared auth_pt" "$(set_cookie_line auth_pt)" "Max-Age=0"
expect_eq "step 9: the company is gone" "$(psql_value "SELECT count(*) FROM \"Companies\" WHERE \"Name\" = '$ORG_NAME'")" "0"
pass "step 9: an admin without a password deletes the company with GitHub's reauth ticket"

# --- Step 10: the three mails (criterion 22) ---------------------------------------------------------------------------------
mail_without_link "$NEW_EMAIL" "Google was connected"
mail_without_link "$NEW_EMAIL" "GitHub was disconnected"
mail_without_link "$PW_EMAIL" "The password of your"
expect_eq "step 10: the notices NEW's address counted" "$(notice_count "$NEW_EMAIL")" "2"
expect_eq "step 10: the notices PW's address counted" "$(notice_count "$PW_EMAIL")" "1"
expect_eq "step 10: none for the finishes and the reset" "$(notice_count "$ORG_EMAIL")" "0"
pass "step 10: provider connected, provider disconnected and password changed were mailed, with no link; the notice limit counted them"

# --- Step 11: the audit rows and no secret (criteria 25, 7) ------------------------------------------------------------------
since="occurred_at >= '$START_TS'"
expect_eq "step 11: account.confirmed (github, password)" \
  "$(psql_value "SELECT string_agg(details->>'method', ',' ORDER BY details->>'method') FROM audit_events WHERE kind = 'account.confirmed' AND $since")" "github,password"
expect_eq "step 11: account.confirm_refused (reason:action)" \
  "$(psql_value "SELECT string_agg((details->>'reason') || ':' || (details->>'action'), ',' ORDER BY (details->>'reason') || ':' || (details->>'action')) FROM audit_events WHERE kind = 'account.confirm_refused' AND $since")" \
  "reauth_failed:confirm,wrong_password:confirm"
expect_eq "step 11: provider.linked via settings" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'provider.linked' AND subject_user_id = '$NEW_SUB' AND details->>'via' = 'settings' AND details->>'provider' = 'google'")" "1"
expect_eq "step 11: provider.unlinked" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'provider.unlinked' AND subject_user_id = '$NEW_SUB' AND details->>'provider' = 'github'")" "1"
expect_eq "step 11: password.changed" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'password.changed' AND subject_email = '$PW_EMAIL' AND $since")" "1"
expect_eq "step 11: login.succeeded via session_renewal (the unlink, the change)" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.succeeded' AND details->>'via' = 'session_renewal' AND $since")" "2"
expect_eq "step 11: signup.completed with github (NEW, ORG)" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.completed' AND details->>'provider' = 'github' AND $since")" "2"
expect_eq "step 11: provider.reauthenticated of the company deletion" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'provider.reauthenticated' AND details->>'provider' = 'github' AND org_name = '$ORG_NAME'")" "1"
expect_eq "step 11: org.deleted" "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'org.deleted' AND org_name = '$ORG_NAME'")" "1"
COUNT="$(grep -c '' "$tmp/secrets")"
[[ "$COUNT" -ge 40 ]] || fail "step 11: only $COUNT secrets were collected"
printf '%s\n' "$PW_EMAIL" > "$tmp/control"
[[ "$(secrets_in_audit "$tmp/control")" -ge 1 ]] || fail "step 11: the search of the audit log does not find a value it holds"
expect_eq "step 11: rows of the audit log that hold a secret of this run" "$(secrets_in_audit "$tmp/secrets")" "0"
pass "step 11: the audit rows of the run hold their fields; none of the $COUNT secrets of the run is in the audit log"

echo "ALL PASS"
```

Notes for the implementer:
- The `settings` helper sends one `Cookie` header with both pairs, as a browser does (`auth_rt=…; auth_pt=…`); two `Cookie` headers could be
  merged or dropped by the proxy.
- The reset mail's subject starts "Reset your" in English (spec 0004's text); the notice subjects are Task 8's English texts ("Google was
  connected to your … account", "GitHub was disconnected from …", "The password of your … account was changed"). The stack's locale is
  English (`Auth__App__Locale` of the web stack); if it is Polish, use the Polish starts of the same texts.
- `fragment_of`, `round_trip` and `ticket_json` are the copied helpers; `round_trip`'s check of the start's `Location` covers GitHub, whose
  authorization endpoint is `$FAKE_URL/github/authorize` (the fake's document, Amendment A5).
- Run it only as a dry syntax check: `bash -n scripts/e2e-settings.sh` and `bash -n scripts/e2e-providers.sh` must print nothing. Never bring a
  stack up (Global Constraints): the e2e verifier runs both scripts on a fresh stack.

- [ ] **Step 3: Check.** `bash -n scripts/e2e-settings.sh`, `bash -n scripts/e2e-providers.sh`, `bash -n scripts/e2e-web.sh`; `file scripts/e2e-settings.sh` says no CRLF;
`C:/p6v/Scripts/python.exe scripts/check-docs.py` passes (it does not read scripts). The root `README.md` lists none of the settings or provider scripts, and
this task leaves it as it is.

- [ ] **Step 4: Hand back** — uncommitted. Files: `scripts/e2e-settings.sh` (new, LF, to be marked executable), `scripts/e2e-providers.sh`,
`scripts/e2e-web.sh` (comment only).
Scan the touched files. Proposed subject: `test: e2e of the settings endpoints, GitHub and the notice mails against the fake provider`.

---

### Task 16: The sample: GitHub buttons, the settings calls, `/settings` and its password section (changed by Amendment B at the end of this plan: read it first)

**Files** (all under `samples/notes-web/src/`):
- Create: `app/pages/settings.ts`, `app/pages/password-input.ts`, `app/pages/settings.spec.ts`, `app/auth/auth.service.settings.spec.ts`
- Modify: `app/account-api.ts`, `app/auth/auth.service.ts`, `app/auth/auth.interceptor.ts`, `app/texts.ts`, `app/app.routes.ts`, `app/pages/notes.ts`,
  `app/pages/login.ts`, `testing/helpers.ts`
- Modify (specs): `app/account-api.providers.spec.ts`, `app/auth/auth.service.providers.spec.ts`, `app/auth/auth.interceptor.spec.ts`,
  `app/pages/providers-on-screens.spec.ts`, `app/pages/provider-buttons.spec.ts`, `app/pages/provider-link.spec.ts`,
  `app/pages/provider-return.spec.ts`, `app/pages/notes.spec.ts`, `app/pages/login.deleted.spec.ts`

**Interfaces:**
- Consumes: the four settings endpoints, `GET /auth/me` `confirmed_until` (Tasks 2, 7, 9, 10, 12).
- Produces: `PROVIDER_NAMES` with `github`; `providerStartUrl(name, { connect: true })`; `Me.confirmed_until: string | null`; the
  `Failure` kinds `session_required`, `confirmation_required`, `provider_in_use`, `last_login`, `password_not_set`, `not_found`; `RENEWAL`
  (an `HttpContextToken<boolean>`); `AuthService.confirmPassword(password)`, `confirmWithTicket(ticket)`, `connect(ticket)`, `unlink(provider)`,
  `changePassword(current, next)` (each `Promise<LoginResult>`), `AuthService.renewal` (the promise of an unlink or a change in flight, or
  null), `AuthService.refresh(forRenewal = false)` (true only for the refresh that the 401 of a renewal's own request starts); `TOKEN_PATHS` with the four paths; `acceptablePassword(password)` in `pages/password-input.ts`; `SettingsPage` at `/settings` with the
  test ids `settings-title`, `row-password`, `row-<provider>`, `connect-<provider>`, `disconnect-<provider>`, `disconnect-blocked`,
  `password-form`, `#current-password`, `#settings-new-password`, `#settings-repeat-password`, `password-submit`, `password-message`,
  `password-error`, `send-link`, `link-sent`; texts `texts.settings`, `texts.login.sessionEnded`, `texts.notes.settings`; test helper
  `withWindow(me, until)`.

**Contract counts:** the sample knows three provider names; `TOKEN_PATHS` has six entries.

**Decisions the plan takes here:**
- The settings calls live on `AuthService`, because two of them replace the session: `unlink` and `changePassword` send their request with
  `RENEWAL` set, hold `renewal` until the new access token is stored, and the interceptor makes every other request to `/auth/` (the path of
  the refresh cookie) wait for it; so no refresh and no other settings call carries the cookie that the answer replaces. The one refresh that is
  let through is the one that the 401 of the renewal's own request starts (an expired access token): held behind that request, it would
  deadlock until its own timeout (`AuthService.refresh(forRenewal)`).
- `401 session_required` is handled once, in the interceptor: the session is dropped and the person goes to `/login` with the notice
  "Your session ended. Sign in again."; no refresh is started (it is not the access token's `401`).
- The three password-check helpers of the notes screen move to `pages/password-input.ts`, moved by a script that copies the lines (they hold
  Unicode escapes: never retype them); the notes screen imports them.
- The provider rows: every offered provider, in the order of `GET /auth/options`, then the account's logins of providers no longer offered.
  "Disconnect" is disabled when the account would keep no way in. Task 17 gives "Connect" and "Disconnect" their actions; this task draws them.
- The texts that named "Google or Microsoft" for an unknown provider now say "Google, Microsoft or GitHub", and the generic provider error
  names no provider ("The provider could not sign you in. Try again."); the specs that pin those sentences change with them.

- [ ] **Step 1: Move the password helpers.** From `samples/notes-web`, run (it copies the lines without retyping them):

```bash
C:/p6v/Scripts/python.exe - <<'EOF'
import re
notes = open('src/app/pages/notes.ts', encoding='utf-8', newline='').read()
start = notes.index('// What .NET calls white space')
end = notes.index('/**\r\n * True for a 503') if '\r\n' in notes else notes.index('/**\n * True for a 503')
block = notes[start:end]
nl = '\r\n' if '\r\n' in notes else '\n'
header = '// Shared by the screens that send a password to Auth-Core (spec 0010, spec 0012): what it refuses as a malformed request.' + nl + nl
block = block.replace('function acceptablePassword', 'export function acceptablePassword')
open('src/app/pages/password-input.ts', 'w', encoding='utf-8', newline='').write(header + block.rstrip() + nl)
notes = notes[:start] + notes[end:]
notes = notes.replace("import { keepTicketOutOfHistory } from './ticket-history';", "import { acceptablePassword } from './password-input';" + nl + "import { keepTicketOutOfHistory } from './ticket-history';")
open('src/app/pages/notes.ts', 'w', encoding='utf-8', newline='').write(notes)
EOF
```

Then check: `src/app/pages/password-input.ts` holds `BLANK`, `LONE_SURROGATE`, `MAX_BODY_BYTES` and `export function acceptablePassword`, with
the same escapes as before (`git diff --stat` shows the lines moved; `grep -c 'u00A0' src/app/pages/password-input.ts` prints `1`), and
`notes.ts` compiles (`npx ng build`).

- [ ] **Step 2: Write the failing specs.**

In `src/testing/helpers.ts`, replace

```ts
  has_password: true,
  linked_providers: [],
};

/** A private account (spec 0010): its space is named "private", its role is "private". */
```

with

```ts
  has_password: true,
  linked_providers: [],
  confirmed_until: null,
};

/** A private account (spec 0010): its space is named "private", its role is "private". */
```

and

```ts
  has_password: true,
  linked_providers: [],
};

/** A private account made through Google (spec 0011): no password, Google signs it in. */
```

with

```ts
  has_password: true,
  linked_providers: [],
  confirmed_until: null,
};

/** A private account made through Google (spec 0011): no password, Google signs it in. */
```

and add at the end:

```ts

/** The person with a confirmation window that ends at `until` (spec 0012: GET /auth/me says it; null means closed). */
export function withWindow(me: Me, until: Date | null): Me {
  return { ...me, confirmed_until: until === null ? null : until.toISOString().replace(/\.\d{3}Z$/, 'Z') };
}
```

In `src/app/account-api.providers.spec.ts`: replace `    [['google', 'github', 'microsoft'], ['google', 'microsoft']],` with
`    [['google', 'gitlab', 'github', 'microsoft'], ['google', 'github', 'microsoft']],`; replace

```ts
  it('knows Google and Microsoft', () => {
    expect(PROVIDER_NAMES).toEqual(['google', 'microsoft']);
    expect(isProviderName('google')).toBe(true);
    expect(isProviderName('github')).toBe(false);
```

with

```ts
  it('knows Google, Microsoft and GitHub', () => {   // spec 0012
    expect(PROVIDER_NAMES).toEqual(['google', 'microsoft', 'github']);
    expect(isProviderName('google')).toBe(true);
    expect(isProviderName('github')).toBe(true);
    expect(isProviderName('gitlab')).toBe(false);
    expect(isProviderName('GitHub')).toBe(false);
```

and add inside its `describe`, after that test:

```ts

  it('builds the start of a connect (spec 0012)', () => {
    expect(providerStartUrl('github', { connect: true })).toBe('/auth/providers/github/start?purpose=connect');
    expect(providerStartUrl('google', { reauth: true })).toBe('/auth/providers/google/start?purpose=reauth');
  });
```

(import `providerStartUrl` if the file does not yet).

In `src/app/auth/auth.service.providers.spec.ts`, replace `    [['google', 'github'], ['google']],` with
`    [['google', 'gitlab', 'github'], ['google', 'github']],   // spec 0012: github is a name`, and add inside its `describe`:

```ts

  it.each([
    ['2026-05-04T18:35:00Z', '2026-05-04T18:35:00Z'],
    [null, null],
    [undefined, null],
    [42, null],
  ])('confirmed_until %j reads as %j (spec 0012)', async (value, expected) => {
    expect((await meFor({ ...privateMe, confirmed_until: value }))?.confirmed_until).toBe(expected);
  });
```

`src/app/auth/auth.service.settings.spec.ts`:

```ts
import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { lastValueFrom } from 'rxjs';
import { vi } from 'vitest';
import { privateMe, settle, signedInAs } from '../../testing/helpers';
import { authInterceptor } from './auth.interceptor';
import { AuthService, failureOf } from './auth.service';

@Component({ template: '' })
class Blank {}

const status = (code: number) => ({ status: code, statusText: String(code) });

function setUp() {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'settings', component: Blank },
        { path: 'login', component: Blank },
      ]),
      provideHttpClient(withInterceptors([authInterceptor])),
      provideHttpClientTesting(),
    ],
  });
  return { auth: TestBed.inject(AuthService), ctrl: TestBed.inject(HttpTestingController), http: TestBed.inject(HttpClient), router: TestBed.inject(Router) };
}

describe('AuthService: the settings calls (spec 0012)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it.each([
    ['/auth/account/confirm', 'confirmPassword', ['pw'], { password: 'pw' }],
    ['/auth/account/confirm', 'confirmWithTicket', ['T'], { provider_ticket: 'T' }],
    ['/auth/providers/connect', 'connect', ['T'], { ticket: 'T' }],
  ] as const)('%s: %s sends its body with the token, then reads the person again', async (url, method, args, body) => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'tok');
    const done = (auth[method] as (...a: string[]) => Promise<unknown>)(...args);
    const request = ctrl.expectOne(url);
    expect(request.request.body).toEqual(body);
    expect(request.request.headers.get('Authorization')).toBe('Bearer tok');
    request.flush(null, status(204));
    await settle();
    ctrl.expectOne('/auth/me').flush({ ...privateMe, confirmed_until: '2026-05-04T18:35:00Z' });
    expect(await done).toEqual({ ok: true });
    expect(auth.me()?.confirmed_until).toBe('2026-05-04T18:35:00Z');
  });

  it.each([
    ['unlink', ['google'], '/auth/account/logins/unlink', { provider: 'google' }],
    ['changePassword', ['Old-Passw0rd', 'New-Passw0rd'], '/auth/account/password', { current_password: 'Old-Passw0rd', new_password: 'New-Passw0rd' }],
  ] as const)('%s stores the new access token of the answer and keeps the person signed in', async (method, args, url, body) => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const session = auth.session;
    const done = (auth[method] as (...a: string[]) => Promise<unknown>)(...args);
    const request = ctrl.expectOne(url);
    expect(request.request.body).toEqual(body);
    request.flush({ status: 'authenticated', access_token: 'renewed' });
    await settle();
    ctrl.expectOne('/auth/me').flush(privateMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('renewed');
    expect(auth.session).toBe(session);   // the same session in this tab: nothing the person was doing is dropped
  });

  it.each([
    ['unlink', ['google'], '/auth/account/logins/unlink'],
    ['changePassword', ['Old-Passw0rd', 'New-Passw0rd'], '/auth/account/password'],
  ] as const)('while %s is in flight no other request to /auth/ leaves: the refresh and another settings call wait for it', async (method, args, url) => {
    const { auth, ctrl, http } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const renewal = (auth[method] as (...a: string[]) => Promise<unknown>)(...args);
    const refresh = auth.refresh();
    const other = lastValueFrom(http.post('/auth/account/confirm', { password: 'x' }));
    const notes = lastValueFrom(http.get('/api/notes'));   // not under /auth: it is not held
    ctrl.expectOne('/api/notes').flush([]);
    await notes;
    ctrl.expectNone('/auth/refresh');
    ctrl.expectNone('/auth/account/confirm');
    ctrl.expectOne(url).flush({ status: 'authenticated', access_token: 'renewed' });
    await settle();
    const confirm = ctrl.expectOne('/auth/account/confirm');
    expect(confirm.request.headers.get('Authorization')).toBe('Bearer renewed');
    confirm.flush(null, status(204));
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'later' });
    ctrl.match('/auth/me').forEach((request) => request.flush(privateMe));
    await Promise.all([renewal, refresh, other]);
  });

  it('a refused renewal keeps the old token and lets the held requests go', async () => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const unlink = auth.unlink('google');
    const refresh = auth.refresh();
    ctrl.expectOne('/auth/account/logins/unlink').flush({ error: 'last_login' }, status(409));
    expect(await unlink).toEqual({ ok: false, failure: { kind: 'last_login' } });
    await settle();
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'old2' });
    await refresh;
    expect(auth.token()).toBe('old2');
  });

  it.each([
    ['unlink', ['google'], '/auth/account/logins/unlink'],
    ['changePassword', ['Old-Passw0rd', 'New-Passw0rd'], '/auth/account/password'],
  ] as const)('an expired access token during %s: the refresh its 401 starts is not held behind it, and the call goes through with no timeout', async (method, args, url) => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const done = (auth[method] as (...a: string[]) => Promise<unknown>)(...args);
    ctrl.expectOne(url).flush(null, status(401));   // the plain 401 of an expired token
    await settle();
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'fresh' });   // sent at once: the call waits for it, it does not wait for the call
    await settle();
    const again = ctrl.expectOne(url);
    expect(again.request.headers.get('Authorization')).toBe('Bearer fresh');
    again.flush({ status: 'authenticated', access_token: 'renewed' });
    await settle();
    ctrl.expectOne('/auth/me').flush(privateMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('renewed');
  });

  it('a plain refresh() started during a renewal does not hold up the refresh that the renewal\'s own 401 starts', async () => {
    const { auth, ctrl } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'old');
    const unlink = auth.unlink('google');
    const plain = auth.refresh();   // its request is held behind the renewal; its promise is out
    ctrl.expectNone('/auth/refresh');
    ctrl.expectOne('/auth/account/logins/unlink').flush(null, status(401));   // an expired access token
    await settle();
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'fresh' });   // the renewal's own refresh: not the plain one's promise, so not waiting for it
    await settle();
    ctrl.expectOne('/auth/account/logins/unlink').flush({ status: 'authenticated', access_token: 'renewed' });
    await settle();
    ctrl.expectOne('/auth/me').flush(privateMe);
    expect(await unlink).toEqual({ ok: true });
    await settle();
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'later' });   // the held plain refresh goes out once the renewal is over
    expect(await plain).toBe('ok');
  });

  it('401 session_required signs the person out to /login with the notice and never starts a refresh', async () => {
    const { auth, ctrl, router } = setUp();
    await signedInAs(auth, ctrl, privateMe, 'tok');
    const navigate = vi.spyOn(router, 'navigate');
    const done = auth.confirmPassword('pw');
    ctrl.expectOne('/auth/account/confirm').flush({ error: 'session_required' }, status(401));
    expect(await done).toEqual({ ok: false, failure: { kind: 'session_required' } });
    ctrl.expectNone('/auth/refresh');
    expect(auth.token()).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/login'], { state: { notice: 'session' } });
  });
});

// Its own describe: no TestBed is configured here, so the HTTP controller of the describe above is not asked for.
describe('failureOf: the answers of the settings endpoints (spec 0012)', () => {
  it.each([
    [401, 'session_required', { kind: 'session_required' }],
    [403, 'confirmation_required', { kind: 'confirmation_required' }],
    [403, 'password_not_set', { kind: 'password_not_set' }],
    [409, 'provider_in_use', { kind: 'provider_in_use' }],
    [409, 'last_login', { kind: 'last_login' }],
    [404, 'not_found', { kind: 'not_found' }],
    [403, 'last_login', { kind: 'other' }],
    [409, 'confirmation_required', { kind: 'other' }],
  ])('%i %s reads as %j', (code, error, expected) => {
    expect(failureOf(new HttpErrorResponse({ status: code, error: { error } }))).toEqual(expected);
  });
});
```

In `src/app/auth/auth.interceptor.spec.ts`, in `which requests get the token`, replace
`    it.each(['/api/notes', '/auth/me', '/auth/org', '/auth/org/members'])('sends it to %s', async (url) => {` with

```ts
    it.each([
      '/api/notes',
      '/auth/me',
      '/auth/org',
      '/auth/org/members',
      '/auth/account/confirm',   // spec 0012: the settings screen
      '/auth/providers/connect',
      '/auth/account/logins/unlink',
      '/auth/account/password',
    ])('sends it to %s', async (url) => {
```

and in the `wantsToken` block add `'/auth/account/confirm/x'`, `'/auth/providers/exchange'` and `'/auth/testing/end-confirmation'` to the
`is false for %s` list.

In `src/app/pages/providers-on-screens.spec.ts`, replace `{ ...both, providers: ['github', 'google', 'google', 'Microsoft'] }` with
`{ ...both, providers: ['gitlab', 'google', 'google', 'Microsoft'] }` and `expect(pageText(fixture)).not.toContain('github');` with
`expect(pageText(fixture)).not.toContain('gitlab');`, and add after that test:

```ts

  it('/login, /signup and /invite: GitHub gets its button beside the others, in the order of the options (spec 0012)', async () => {
    const providers = ['google', 'microsoft', 'github'];
    const login = await open('login', LoginPage, { ...both, providers });
    expect(before(login.fixture, '#email')).toBe('Sign in Continue with Google Continue with Microsoft Continue with GitHub or Email');
    expect(has(login.fixture, 'a[href="/auth/providers/github/start"]')).toBe(true);
  });
```

and, in the same file, beside the existing `/signup` and `/invite` button tests, one test each that opens them with the three providers and
asserts `Sign up with GitHub` and `Accept with GitHub` (copy the shape of the file's `/signup` and `/invite` tests; the invitation's start is
`/auth/providers/github/start?invite=<token>`).

In `src/app/pages/provider-buttons.spec.ts`, in `reads nothing it does not know, and survives storage that throws`, replace
`    sessionStorage.setItem('notes-web.provider', 'github');` with `    sessionStorage.setItem('notes-web.provider', 'gitlab');`.

In `src/app/pages/provider-link.spec.ts`: replace `it.each(['github', 'constructor', '__proto__', 42, undefined])` with
`it.each(['gitlab', 'constructor', '__proto__', 42, undefined])`, and replace every `Google or Microsoft` in the file with
`Google, Microsoft or GitHub` (the Edit tool's replace-all).

In `src/app/pages/provider-return.spec.ts`, replace `const GENERIC = 'Google or Microsoft could not sign you in. Try again.';` with
`const GENERIC = 'The provider could not sign you in. Try again.';`. Search the other specs for `Google or Microsoft could not` and
`'Google or Microsoft'` (`grep -rn "Google or Microsoft" src`) and change each pinned sentence to the new text in the same way; name each file in
the hand-back.

In `src/app/pages/notes.spec.ts`, add:

```ts

describe('NotesPage: the way to the settings (spec 0012)', () => {
  it('the header has "Settings" for a private account and for a company member', async () => {
    for (const me of [privateMe, adminMe]) {
      TestBed.resetTestingModule();
      const { fixture } = await open(me);
      const link = (fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="settings-link"]');
      expect(link?.getAttribute('href')).toBe('/settings');
      expect(link?.textContent?.trim()).toBe('Settings');
    }
  });
});
```

(`open` is `notes.spec.ts`'s own helper: it signs in as the person given and opens the notes screen. Add `privateMe` to the import from
`../../testing/helpers` of that file, whose import line has `adminMe` and `viewerMe` but not `privateMe`.)

In `src/app/pages/login.deleted.spec.ts`, add a test beside the deleted-account one: the navigation state `{ notice: 'session' }` shows
`Your session ended. Sign in again.` in `[data-testid="login-notice"]` (copy the shape of the file's test for `{ notice: 'deleted' }`).

`src/app/pages/settings.spec.ts`:

```ts
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Me } from '../auth/auth.service';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService } from '../auth/auth.service';
import { clickOn, has, pageText, privateMe, providerMe, settle, signedInAs, submitForm, typeInto, adminMe } from '../../testing/helpers';
import { SettingsPage } from './settings';

const status = (code: number) => ({ status: code, statusText: String(code) });
const ALL = ['google', 'microsoft', 'github'];

async function open(me: Me, providers: string[] = ALL) {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
  });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const fixture = TestBed.createComponent(SettingsPage);
  fixture.detectChanges();
  ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'self-service', providers });
  ctrl.expectOne('/auth/me').flush(me);
  await settle(fixture);
  return { fixture, ctrl, auth };
}

function row(fixture: Awaited<ReturnType<typeof open>>['fixture'], name: string): string {
  const element = (fixture.nativeElement as HTMLElement).querySelector(`[data-testid="row-${name}"]`);
  return (element?.textContent ?? '').replace(/\s+/g, ' ').trim();
}

function disabled(fixture: Awaited<ReturnType<typeof open>>['fixture'], selector: string): boolean {
  return (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(selector)!.disabled;
}

describe('SettingsPage: sign-in methods (spec 0012)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('a password and no login: "Set", and "Connect" for each offered provider in the order of the options', async () => {
    const { fixture } = await open(privateMe, ['github', 'google']);
    expect(row(fixture, 'password')).toBe('Password Set');
    expect(row(fixture, 'github')).toBe('GitHub Connect');
    expect(row(fixture, 'google')).toBe('Google Connect');
    expect(has(fixture, '[data-testid="row-microsoft"]')).toBe(false);
    const rows = [...(fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid^="row-"]')].map((r) => r.getAttribute('data-testid'));
    expect(rows).toEqual(['row-password', 'row-github', 'row-google']);
  });

  it('no password and one login: "Not set", "Connected" with a disabled "Disconnect" and its sentence', async () => {
    const { fixture } = await open(providerMe);
    expect(row(fixture, 'password')).toBe('Password Not set');
    expect(row(fixture, 'google')).toContain('Connected');
    expect(disabled(fixture, '[data-testid="disconnect-google"]')).toBe(true);
    expect(pageText(fixture)).toContain('Set a password or connect another provider before you disconnect this one.');
  });

  it('no password and two logins of offered providers: both can be disconnected', async () => {
    const { fixture } = await open({ ...providerMe, linked_providers: ['github', 'google'] });
    expect(disabled(fixture, '[data-testid="disconnect-google"]')).toBe(false);
    expect(disabled(fixture, '[data-testid="disconnect-github"]')).toBe(false);
    expect(has(fixture, '[data-testid="disconnect-blocked"]')).toBe(false);
  });

  it('a password and a login: "Disconnect" is enabled', async () => {
    const { fixture } = await open({ ...privateMe, linked_providers: ['microsoft'] });
    expect(disabled(fixture, '[data-testid="disconnect-microsoft"]')).toBe(false);
    expect(row(fixture, 'google')).toBe('Google Connect');
  });

  it('a login of a provider no longer offered gets a row with "Disconnect" only, after the offered ones', async () => {
    const { fixture } = await open({ ...providerMe, linked_providers: ['google', 'microsoft'] }, ['google']);
    expect(row(fixture, 'microsoft')).toBe('Microsoft Connected Disconnect');
    expect(has(fixture, '[data-testid="connect-microsoft"]')).toBe(false);
    // Google is the only offered way in: it cannot go; Microsoft can, while Google stays.
    expect(disabled(fixture, '[data-testid="disconnect-google"]')).toBe(true);
    expect(disabled(fixture, '[data-testid="disconnect-microsoft"]')).toBe(false);
  });

  it('a failed options call still lists the logins, each with "Disconnect" (no provider is known to be offered)', async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    const ctrl = TestBed.inject(HttpTestingController);
    await signedInAs(TestBed.inject(AuthService), ctrl, { ...privateMe, linked_providers: ['google'] });
    const fixture = TestBed.createComponent(SettingsPage);
    fixture.detectChanges();
    ctrl.expectOne('/auth/options').flush('oops', status(500));
    ctrl.expectOne('/auth/me').flush({ ...privateMe, linked_providers: ['google'] });
    await settle(fixture);
    expect(row(fixture, 'google')).toBe('Google Connected Disconnect');
  });
});

describe('SettingsPage: the password (spec 0012)', () => {
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  async function change(opened: Awaited<ReturnType<typeof open>>, current: string, next: string, repeat = next): Promise<void> {
    typeInto(opened.fixture, '#current-password', current);
    typeInto(opened.fixture, '#settings-new-password', next);
    typeInto(opened.fixture, '#settings-repeat-password', repeat);
    submitForm(opened.fixture, 'form[data-testid="password-form"]');
    await settle(opened.fixture);
  }

  it('with a password: "Change password" sends the current and the new one, stores the new token and says so', async () => {
    const opened = await open(privateMe);
    await change(opened, 'Old-Passw0rd', 'New-Passw0rd-1');
    const request = opened.ctrl.expectOne('/auth/account/password');
    expect(request.request.body).toEqual({ current_password: 'Old-Passw0rd', new_password: 'New-Passw0rd-1' });
    request.flush({ status: 'authenticated', access_token: 'renewed' });
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(privateMe);
    await settle(opened.fixture);
    expect(opened.auth.token()).toBe('renewed');
    expect(pageText(opened.fixture)).toContain('Password changed.');
  });

  it('two different new passwords: nothing is sent', async () => {
    const opened = await open(privateMe);
    await change(opened, 'Old-Passw0rd', 'New-Passw0rd-1', 'New-Passw0rd-2');
    opened.ctrl.expectNone('/auth/account/password');
    expect(pageText(opened.fixture)).toContain('The two passwords are not the same.');
  });

  it('a blank current password: nothing is sent', async () => {
    const opened = await open(privateMe);
    await change(opened, '   ', 'New-Passw0rd-1');
    opened.ctrl.expectNone('/auth/account/password');
    expect(pageText(opened.fixture)).toContain('Enter your current password.');
  });

  it.each([
    [{ error: 'wrong_password' }, 403, 'The current password is wrong.'],
    [{ error: 'too_many_attempts', retry_after_seconds: 120 }, 429, 'Too many attempts. Try again in 2 minutes.'],
    [{ error: 'weak_password', rules: ['too_short'] }, 400, 'The password is too short.'],
    [{ error: 'no_membership' }, 403, 'Your account does not belong to a company.'],
  ])('the answer %j is said in one sentence', async (answer, code, sentence) => {
    const opened = await open(privateMe);
    await change(opened, 'Old-Passw0rd', 'New-Passw0rd-1');
    opened.ctrl.expectOne('/auth/account/password').flush(answer, status(code));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(sentence);
    expect(opened.auth.token()).toBe('tok');
  });

  it('403 password_not_set redraws the section from a fresh GET /auth/me', async () => {
    const opened = await open(privateMe);
    await change(opened, 'Old-Passw0rd', 'New-Passw0rd-1');
    opened.ctrl.expectOne('/auth/account/password').flush({ error: 'password_not_set' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settle(opened.fixture);
    expect(has(opened.fixture, 'form[data-testid="password-form"]')).toBe(false);
    expect(has(opened.fixture, '[data-testid="send-link"]')).toBe(true);
  });

  it('the button is disabled while the change is in flight', async () => {
    const opened = await open(privateMe);
    await change(opened, 'Old-Passw0rd', 'New-Passw0rd-1');
    expect(disabled(opened.fixture, '[data-testid="password-submit"]')).toBe(true);
    opened.ctrl.expectOne('/auth/account/password').flush({ error: 'wrong_password' }, status(403));
    await settle(opened.fixture);
    expect(disabled(opened.fixture, '[data-testid="password-submit"]')).toBe(false);
  });

  it('without a password: "Send me a link" asks POST /auth/password/forgot with the account\'s address and says what the link does', async () => {
    const opened = await open(providerMe);
    expect(has(opened.fixture, 'form[data-testid="password-form"]')).toBe(false);
    clickOn(opened.fixture, '[data-testid="send-link"]');
    await settle(opened.fixture);
    const request = opened.ctrl.expectOne('/auth/password/forgot');
    expect(request.request.body).toEqual({ email: providerMe.email });
    expect(disabled(opened.fixture, '[data-testid="send-link"]')).toBe(true);
    request.flush(null, status(202));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Check your inbox: the link sets your password. Setting it signs you out everywhere.');
  });

  it('a company member sees the same sections', async () => {
    const opened = await open(adminMe);
    expect(row(opened.fixture, 'password')).toBe('Password Set');
    expect(has(opened.fixture, 'form[data-testid="password-form"]')).toBe(true);
  });
});
```

(`signedInAs` uses the token `tok`; `has_password: true` with no logins is `privateMe`.)

- [ ] **Step 3: See them fail.** From `samples/notes-web`: `npm test` — the new specs fail (no `SettingsPage`, no settings calls, three
names unknown); the changed specs fail on the old names and texts.

- [ ] **Step 4: Names and the start.** In `src/app/account-api.ts`, replace

```ts
/** The sign-in providers this app has buttons for (spec 0011). */
export const PROVIDER_NAMES = ['google', 'microsoft'] as const;

export type ProviderName = (typeof PROVIDER_NAMES)[number];

export function isProviderName(value: unknown): value is ProviderName {
  return value === 'google' || value === 'microsoft';
}
```

with

```ts
/** The sign-in providers this app has buttons for (spec 0011; GitHub since spec 0012). */
export const PROVIDER_NAMES = ['google', 'microsoft', 'github'] as const;

export type ProviderName = (typeof PROVIDER_NAMES)[number];

export function isProviderName(value: unknown): value is ProviderName {
  return value === 'google' || value === 'microsoft' || value === 'github';
}
```

and replace

```ts
 * The address of a provider button: Auth-Core's start, plain, with the invitation token, or for the confirmation of a deletion (spec 0011).
 * A link, not a call: the browser leaves the app for the provider and comes back to /provider-return.
 */
export function providerStartUrl(name: ProviderName, how: { invite: string } | { reauth: true } | null = null): string {
  const start = `/auth/providers/${name}/start`;
  if (how === null) {
    return start;
  }
  return 'invite' in how ? `${start}?invite=${encodeURIComponent(how.invite)}` : `${start}?purpose=reauth`;
}
```

with

```ts
 * The address of a provider button: Auth-Core's start, plain, with the invitation token, for a confirmation (spec 0011; spec 0012: also of the
 * settings screen), or for a connect (spec 0012). A link, not a call: the browser leaves the app for the provider and comes back to
 * /provider-return.
 */
export function providerStartUrl(
  name: ProviderName,
  how: { invite: string } | { reauth: true } | { connect: true } | null = null,
): string {
  const start = `/auth/providers/${name}/start`;
  if (how === null) {
    return start;
  }
  if ('invite' in how) {
    return `${start}?invite=${encodeURIComponent(how.invite)}`;
  }
  return 'connect' in how ? `${start}?purpose=connect` : `${start}?purpose=reauth`;
}
```

- [ ] **Step 5: The person, the failures and the calls.** In `src/app/auth/auth.service.ts`:

(a) replace `import { HttpClient, HttpErrorResponse } from '@angular/common/http';` with
`import { HttpClient, HttpContext, HttpContextToken, HttpErrorResponse } from '@angular/common/http';`;

(b) replace

```ts
  /** The providers that sign in to this account, sorted (spec 0011). */
  linked_providers: ProviderName[];
}
```

with

```ts
  /** The providers that sign in to this account, sorted (spec 0011). */
  linked_providers: ProviderName[];
  /** Spec 0012: the end of the confirmation window of this device's session, UTC with seconds, or null when it is closed. */
  confirmed_until: string | null;
}

/** Marks the request of an unlink or a password change: it is the one request to /auth/ that is not held while it runs (spec 0012). */
export const RENEWAL = new HttpContextToken<boolean>(() => false);
```

(c) replace

```ts
  | { kind: 'reauth_required' }
  | { kind: 'too_many_attempts'; retryAfterSeconds: number }
```

with

```ts
  | { kind: 'reauth_required' }
  | { kind: 'session_required' }
  | { kind: 'confirmation_required' }
  | { kind: 'provider_in_use' }
  | { kind: 'last_login' }
  | { kind: 'password_not_set' }
  | { kind: 'not_found' }
  | { kind: 'too_many_attempts'; retryAfterSeconds: number }
```

(d) replace

```ts
  if (status === 409 && code === 'provider_already_linked') {
    return { kind: 'provider_already_linked' };
  }
  if (status !== 403) {
    return { kind: 'other' };
  }
```

with

```ts
  if (status === 409 && code === 'provider_already_linked') {
    return { kind: 'provider_already_linked' };
  }
  // Spec 0012: the settings screen.
  if (status === 409 && (code === 'provider_in_use' || code === 'last_login')) {
    return { kind: code };
  }
  if (status === 401 && code === 'session_required') {
    return { kind: 'session_required' };
  }
  if (status === 404 && code === 'not_found') {
    return { kind: 'not_found' };
  }
  if (status !== 403) {
    return { kind: 'other' };
  }
  if (code === 'confirmation_required' || code === 'password_not_set') {
    return { kind: code };
  }
```

and replace `/** The answers of spec 0011, each with the status of the contract; anything else is "other". */` with
`/** The answers of specs 0011 and 0012, each with the status of the contract; anything else is "other". */`;

(e) in `asMe`, replace

```ts
      linked_providers: providerNamesOf(body['linked_providers']),
    };
```

with

```ts
      linked_providers: providerNamesOf(body['linked_providers']),
      // Anything but a string is a closed window: an Auth-Core before spec 0012 does not send the member.
      confirmed_until: typeof body['confirmed_until'] === 'string' ? body['confirmed_until'] : null,
    };
```

(f) replace

```ts
  private refreshing: Promise<RefreshResult> | null = null;
```

with

```ts
  private refreshing: Promise<RefreshResult> | null = null;
  // Spec 0012: an unlink or a password change in flight. Its answer replaces the refresh cookie, so nothing else that carries it may leave.
  private renewing: Promise<void> | null = null;
```

(g) add after the method `deleteAccountWithTicket` (before the comment of `refresh()`):

```ts
  /** The unlink or the password change in flight, or null: the interceptor holds every other request to /auth/ until it settles. */
  get renewal(): Promise<void> | null {
    return this.renewing;
  }

  /** POST /auth/account/confirm with the password (spec 0012): the window of this device's session opens for five minutes. */
  confirmPassword(password: string): Promise<LoginResult> {
    return this.settingsCall('/auth/account/confirm', { password });
  }

  /** The same with the ticket of a `reauth` round trip; the auth_pt cookie goes with it by itself. */
  confirmWithTicket(ticket: string): Promise<LoginResult> {
    return this.settingsCall('/auth/account/confirm', { provider_ticket: ticket });
  }

  /** POST /auth/providers/connect with the ticket of a `connect` round trip. */
  connect(ticket: string): Promise<LoginResult> {
    return this.settingsCall('/auth/providers/connect', { ticket });
  }

  /** POST /auth/account/logins/unlink: every other device is signed out, and this one gets a new session in the answer. */
  unlink(provider: string): Promise<LoginResult> {
    return this.renew('/auth/account/logins/unlink', { provider });
  }

  /** POST /auth/account/password, against the current password: as the unlink, a new session for this device. */
  changePassword(current: string, next: string): Promise<LoginResult> {
    return this.renew('/auth/account/password', { current_password: current, new_password: next });
  }

  /** A settings call that answers 204: then the person is read again (the window, the logins). */
  private async settingsCall(url: string, body: unknown): Promise<LoginResult> {
    try {
      await firstValueFrom(this.http.post(url, body));
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
    await this.loadMe(REFRESH_TIMEOUT_MS);
    return { ok: true };
  }

  /**
   * A call whose answer is a login's (spec 0012 → Sessions): the new access token is stored in the same session of this tab (the refresh cookie
   * is already the new one). Until it is stored, every other request to /auth/ waits, so that none carries the cookie the answer replaces.
   */
  private async renew(url: string, body: unknown): Promise<LoginResult> {
    const started = this.generation;
    let release = (): void => undefined;
    this.renewing = new Promise<void>((resolve) => (release = resolve));
    let result: LoginResult;
    try {
      const answer = await firstValueFrom(
        this.http.post<LoginBody | null>(url, body, { context: new HttpContext().set(RENEWAL, true) }),
      );
      const token = answer?.access_token;
      if (answer?.status !== 'authenticated' || typeof token !== 'string' || token === '' || this.generation !== started) {
        result = { ok: false, failure: { kind: 'other' } };
      } else {
        this.accessToken.set(token);
        result = { ok: true };
      }
    } catch (error) {
      result = { ok: false, failure: failureOf(error) };
    } finally {
      this.renewing = null;
      release();
    }
    if (result.ok) {
      await this.loadMe(REFRESH_TIMEOUT_MS);
    }
    return result;
  }
```

(h) `refresh()` goes through the interceptor, which holds it while a renewal runs (Step 6), with one exception: the refresh that the 401 of the
renewal's own request starts. An expired access token makes the unlink or the password change answer the plain `401`; the refresh that follows
carries the cookie and is not a renewal, so the gate would hold it behind the very request that waits for it (a deadlock until the refresh's own
10 seconds, then "unreachable"). Replace

```ts
  refresh(): Promise<RefreshResult> {
    this.refreshing ??= this.exchange().finally(() => {
      this.refreshing = null;
    });
    return this.refreshing;
  }
```

with

```ts
  refresh(forRenewal = false): Promise<RefreshResult> {
    if (forRenewal) {
      // Never shared with a plain refresh: one started while the renewal runs has its request held behind the renewal, and the renewal's
      // own refresh would wait for it (until its 10 seconds) if it joined it. Its own promise, its own request, marked as part of the renewal.
      this.renewalRefreshing ??= this.exchange(true).finally(() => {
        this.renewalRefreshing = null;
      });
      return this.renewalRefreshing;
    }
    this.refreshing ??= this.exchange(false).finally(() => {
      this.refreshing = null;
    });
    return this.refreshing;
  }
```

and add after `private refreshing: Promise<RefreshResult> | null = null;` the member `  private renewalRefreshing: Promise<RefreshResult> | null = null;`.

and replace

```ts
  private async exchange(): Promise<RefreshResult> {
    const started = this.generation;
    try {
      const body = await firstValueFrom(
        this.http.post<RefreshBody | null>('/auth/refresh', null).pipe(timeout(REFRESH_TIMEOUT_MS)),
      );
```

with

```ts
  private async exchange(forRenewal: boolean): Promise<RefreshResult> {
    const started = this.generation;
    try {
      // The refresh that the 401 of a renewal's own request starts is marked as part of the renewal, so that the interceptor does not hold it
      // behind that request (spec 0012). No other caller passes `forRenewal`: theirs wait for the renewal as before.
      const options = forRenewal ? { context: new HttpContext().set(RENEWAL, true) } : {};
      const body = await firstValueFrom(
        this.http.post<RefreshBody | null>('/auth/refresh', null, options).pipe(timeout(REFRESH_TIMEOUT_MS)),
      );
```

- [ ] **Step 6: The interceptor.** In `src/app/auth/auth.interceptor.ts`:

(a) replace `import { AuthService, errorCode, retryAfterOf } from './auth.service';` with
`import { AuthService, RENEWAL, errorCode, retryAfterOf } from './auth.service';`;

(b) replace

```ts
/** Paths that are exactly this get it too. */
export const TOKEN_PATHS: readonly string[] = ['/auth/me', '/auth/account'];
```

with

```ts
/** Paths that are exactly this get it too (spec 0012: the four endpoints of the settings screen). */
export const TOKEN_PATHS: readonly string[] = [
  '/auth/me',
  '/auth/account',
  '/auth/account/confirm',
  '/auth/providers/connect',
  '/auth/account/logins/unlink',
  '/auth/account/password',
];
```

(c) replace

```ts
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const answer = wantsToken(req.url) ? send(req, next, auth, inject(Router)) : next(req);
```

with

```ts
/** True for a request that carries the refresh cookie: the cookie's path is /auth (spec 0002). */
function carriesRefreshCookie(req: HttpRequest<unknown>): boolean {
  const path = ownUrl(req.url)?.pathname;
  return path !== undefined && (path === '/auth' || path.startsWith('/auth/'));
}

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const sent = wantsToken(req.url) ? send(req, next, auth, router) : next(req);
  // Spec 0012: while an unlink or a password change is in flight, no other request with the refresh cookie leaves (its answer replaces it).
  const answer =
    carriesRefreshCookie(req) && !req.context.get(RENEWAL)
      ? defer(() => {
          const renewal = auth.renewal;
          return renewal === null ? sent : from(renewal).pipe(switchMap(() => sent));
        })
      : sent;
```

(d) in `send`, replace

```ts
        const permissionsChanged = error.status === 403 && errorCode(error) === 'permissions_changed';
        if (error.status !== 401 && !permissionsChanged) {
```

with

```ts
        if (error.status === 401 && errorCode(error) === 'session_required') {
          // Spec 0012: the session of this device is over. Not the access token's 401: no refresh, the person signs in again.
          auth.dropSession();
          void router.navigate(['/login'], { state: { notice: 'session' } });
          return throwError(() => error);
        }
        const permissionsChanged = error.status === 403 && errorCode(error) === 'permissions_changed';
        if (error.status !== 401 && !permissionsChanged) {
```

(e) in `send`, replace `        return tokenIsRenewed(auth, router, sentWith, sentIn).pipe(` with
`        return tokenIsRenewed(auth, router, sentWith, sentIn, req.context.get(RENEWAL)).pipe(`;

(f) replace

```ts
function tokenIsRenewed(auth: AuthService, router: Router, sentWith: string | null, sentIn: number): Observable<boolean> {
```

with

```ts
function tokenIsRenewed(
  auth: AuthService,
  router: Router,
  sentWith: string | null,
  sentIn: number,
  forRenewal: boolean,
): Observable<boolean> {
```

and replace `    const renewed = auth.refresh().then((result) => {` with
`    const renewed = auth.refresh(forRenewal).then((result) => {   // an unlink or a password change that met an expired token: its refresh is not held (spec 0012)`.

- [ ] **Step 7: The texts.** In `src/app/texts.ts`:

(a) replace

```ts
    names: { google: 'Google', microsoft: 'Microsoft' } as Record<string, string>,
    either: 'Google or Microsoft',
```

with

```ts
    names: { google: 'Google', microsoft: 'Microsoft', github: 'GitHub' } as Record<string, string>,
    either: 'Google, Microsoft or GitHub',
```

(b) replace `      provider_error: 'Google or Microsoft could not sign you in. Try again.',` with
`      provider_error: 'The provider could not sign you in. Try again.',`;

(c) replace `    accountDeleted: 'Your account was deleted.',` with

```ts
    accountDeleted: 'Your account was deleted.',
    // Spec 0012: 401 session_required at a settings endpoint.
    sessionEnded: 'Your session ended. Sign in again.',
```

(d) replace `    team: 'Team',
    deleteAccount: 'Delete account',` (in `notes`) with

```ts
    team: 'Team',
    settings: 'Settings',
    deleteAccount: 'Delete account',
```

(e) add before `  team: {`:

```ts
  // Spec 0012: the settings screen. {Provider} is the provider's name, {email} the account's address.
  settings: {
    title: 'Settings',
    loading: 'Loading...',
    back: 'Back to the notes',
    methods: 'Sign-in methods',
    password: 'Password',
    set: 'Set',
    notSet: 'Not set',
    connected: 'Connected',
    connect: 'Connect',
    disconnect: 'Disconnect',
    disconnectBlocked: 'Set a password or connect another provider before you disconnect this one.',
    changePassword: 'Change password',
    currentPassword: 'Current password',
    currentRequired: 'Enter your current password.',
    wrongCurrent: 'The current password is wrong.',
    passwordChanged: 'Password changed.',
    setPassword: 'Set a password',
    sendLink: 'Send me a link',
    linkSent: 'Check your inbox: the link sets your password. Setting it signs you out everywhere.',
    connectedNow: (provider: string): string => `${provider} connected.`,
    disconnectedNow: (provider: string): string => `${provider} disconnected.`,
    lastLogin: 'Set a password or connect another provider first.',
    providerInUse: (provider: string): string => `You already have a ${provider} account connected. Disconnect it first.`,
    providerAlreadyLinked: (provider: string): string => `This ${provider} account belongs to another account.`,
    addressMismatch: (provider: string, email: string): string => `This ${provider} account has another address. Connect the one with ${email}.`,
    emailNotVerified: (provider: string): string => `${provider} has not verified this address.`,
    noEmail: (provider: string): string => `This ${provider} account has no email address we can use.`,
    invalidTicket: 'This took too long. Start again.',
    reauthFailed: 'The confirmation did not work or took too long. Confirm again.',
    wrongPassword: 'The password is wrong.',
    notFound: 'This sign-in method is no longer connected.',
    connectAgain: (provider: string): string => `Confirmed. Connect ${provider} again.`,
    // The confirmation dialog.
    confirmTitle: "Confirm it's you",
    confirm: 'Confirm',
    cancel: 'Cancel',
    noMethod: 'None of your sign-in methods can confirm here. Set a password first.',
  },

```

- [ ] **Step 8: The settings screen.** `src/app/pages/settings.ts`:

```ts
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AccountApi, ProviderName } from '../account-api';
import { AuthService, Failure } from '../auth/auth.service';
import { texts } from '../texts';
import { acceptablePassword } from './password-input';
import { providerTitle } from './provider-buttons';
import { PasswordRules } from './password-rules';

/** One row of "Sign-in methods": a provider the app offers, or one the account still has a login of. */
interface ProviderRow {
  name: ProviderName;
  title: string;
  connected: boolean;
  offered: boolean;
  /** False when the account would keep no way in after it: no password and no login of another offered provider. */
  canDisconnect: boolean;
}

/**
 * Spec 0012 → Sample frontend: the person's own settings. "Sign-in methods" (the password and one row per provider), "Password" (change it
 * against the current one, or set a first one through the mail). Every answer is read from Auth-Core (GET /auth/me, GET /auth/options).
 */
@Component({
  selector: 'app-settings',
  imports: [ReactiveFormsModule, RouterLink, PasswordRules],
  template: `
    <main class="wide">
      <h1 tabindex="-1" data-testid="settings-title">{{ t.title }}</h1>
      @if (me(); as person) {
        <section data-testid="methods">
          <h2>{{ t.methods }}</h2>
          <ul class="plain">
            <li data-testid="row-password">
              {{ t.password }} <span>{{ person.has_password ? t.set : t.notSet }}</span>
            </li>
            @for (row of rows(); track row.name) {
              <li [attr.data-testid]="'row-' + row.name">
                {{ row.title }}
                @if (row.connected) {
                  <span>{{ t.connected }}</span>
                  <button type="button" [attr.data-testid]="'disconnect-' + row.name" [disabled]="!row.canDisconnect || busy()">
                    {{ t.disconnect }}
                  </button>
                  @if (!row.canDisconnect) {
                    <p class="status" data-testid="disconnect-blocked">{{ t.disconnectBlocked }}</p>
                  }
                } @else if (row.offered) {
                  <button type="button" [attr.data-testid]="'connect-' + row.name" [disabled]="busy()">{{ t.connect }}</button>
                }
              </li>
            }
          </ul>
        </section>
        <section data-testid="password-section">
          <h2>{{ t.password }}</h2>
          @if (person.has_password) {
            <form [formGroup]="passwordForm" (ngSubmit)="changePassword()" novalidate data-testid="password-form">
              <label for="current-password">{{ t.currentPassword }}</label>
              <input id="current-password" type="password" formControlName="current" autocomplete="current-password" />
              <label for="settings-new-password">{{ common.newPassword }}</label>
              <input id="settings-new-password" type="password" formControlName="next" autocomplete="new-password" />
              <label for="settings-repeat-password">{{ common.repeatPassword }}</label>
              <input id="settings-repeat-password" type="password" formControlName="repeat" autocomplete="new-password" />
              <button type="submit" data-testid="password-submit" [disabled]="passwordBusy()">{{ t.changePassword }}</button>
            </form>
            <app-password-rules [rules]="rules()" />
          } @else {
            <p>{{ t.setPassword }}</p>
            <button type="button" data-testid="send-link" [disabled]="linkBusy()" (click)="sendLink()">{{ t.sendLink }}</button>
            @if (linkSent()) {
              <p class="status" role="status" data-testid="link-sent">{{ t.linkSent }}</p>
            }
          }
          @if (passwordMessage(); as text) {
            <p class="status" role="status" data-testid="password-message">{{ text }}</p>
          }
          @if (passwordError(); as text) {
            <p class="error" role="alert" data-testid="password-error">{{ text }}</p>
          }
        </section>
      } @else {
        <p class="status" role="status">{{ t.loading }}</p>
      }
      <p><a routerLink="/notes">{{ t.back }}</a></p>
    </main>
  `,
})
export class SettingsPage implements OnInit {
  protected readonly t = texts.settings;
  protected readonly common = texts.common;
  protected readonly auth = inject(AuthService);
  private readonly account = inject(AccountApi);

  protected readonly me = this.auth.me;
  /** The providers GET /auth/options offers, in its order; empty until it answers, and when it fails. */
  protected readonly offered = signal<ProviderName[]>([]);
  /** A connect or a disconnect in flight (spec 0012: each button is disabled while its request is). */
  protected readonly busy = signal(false);
  protected readonly passwordBusy = signal(false);
  protected readonly linkBusy = signal(false);
  protected readonly linkSent = signal(false);
  protected readonly rules = signal<readonly string[]>([]);
  protected readonly passwordMessage = signal<string | null>(null);
  protected readonly passwordError = signal<string | null>(null);
  protected readonly passwordForm = inject(NonNullableFormBuilder).group({ current: [''], next: [''], repeat: [''] });

  protected readonly rows = computed<ProviderRow[]>(() => {
    const person = this.me();
    if (person === null) {
      return [];
    }
    const offered = this.offered();
    const linked = person.linked_providers;
    const names = [...offered, ...linked.filter((name) => !offered.includes(name))];
    return names.map((name) => ({
      name,
      title: providerTitle(name),
      connected: linked.includes(name),
      offered: offered.includes(name),
      canDisconnect: person.has_password || linked.some((other) => other !== name && offered.includes(other)),
    }));
  });

  ngOnInit(): void {
    void this.account.options().then((options) => this.offered.set(options.providers));
    // Always read afresh: the window and the logins are what the screen acts on.
    void this.auth.loadMe();
  }

  protected async changePassword(): Promise<void> {
    if (this.passwordBusy()) {
      return;
    }
    const { current, next, repeat } = this.passwordForm.getRawValue();
    this.passwordMessage.set(null);
    this.passwordError.set(null);
    this.rules.set([]);
    if (!acceptablePassword(current)) {
      this.passwordError.set(this.t.currentRequired);
      return;
    }
    if (next !== repeat) {
      this.passwordError.set(this.common.passwordsDiffer);
      return;
    }
    this.passwordBusy.set(true);
    const result = await this.auth.changePassword(current, next);
    this.passwordBusy.set(false);
    if (result.ok) {
      this.passwordForm.reset();
      this.passwordMessage.set(this.t.passwordChanged);
      return;
    }
    await this.passwordRefused(result.failure);
  }

  private async passwordRefused(failure: Failure): Promise<void> {
    switch (failure.kind) {
      case 'wrong_password':
        this.passwordError.set(this.t.wrongCurrent);
        break;
      case 'too_many_attempts':
        this.passwordError.set(texts.login.tooManyAttempts(failure.retryAfterSeconds));
        break;
      case 'weak_password':
        this.rules.set(failure.rules);
        break;
      case 'password_not_set':
        // The account is not what the screen thought: draw the section that fits it now.
        await this.auth.loadMe();
        break;
      case 'no_membership':
        this.passwordError.set(texts.login.noMembership);
        break;
      case 'too_many_requests':
      case 'session_required':
        // The bar says when to try again; or the interceptor has already signed the person out.
        break;
      default:
        this.passwordError.set(this.common.somethingWrong);
    }
  }

  protected async sendLink(): Promise<void> {
    const person = this.me();
    if (this.linkBusy() || person === null) {
      return;
    }
    this.linkBusy.set(true);
    this.passwordError.set(null);
    const outcome = await this.account.forgotPassword(person.email);
    this.linkBusy.set(false);
    if (outcome.ok) {
      this.linkSent.set(true);
    } else if (outcome.failure.kind !== 'too_many_requests') {
      this.passwordError.set(this.common.somethingWrong);
    }
  }
}
```

In `src/app/app.routes.ts`, replace

```ts
import { SignupPage } from './pages/signup';
```

with

```ts
import { SettingsPage } from './pages/settings';
import { SignupPage } from './pages/signup';
```

and

```ts
  { path: 'team', component: TeamPage, canActivate: [authGuard], title: texts.team.title },
```

with

```ts
  { path: 'team', component: TeamPage, canActivate: [authGuard], title: texts.team.title },
  { path: 'settings', component: SettingsPage, canActivate: [authGuard], title: texts.settings.title },
```

In `src/app/pages/notes.ts`, replace

```ts
        <button type="button" data-testid="sign-out" [disabled]="signingOut()" (click)="signOut()">{{ t.signOut }}</button>
```

with

```ts
        @if (me()) {
          <a routerLink="/settings" data-testid="settings-link">{{ t.settings }}</a>
        }
        <button type="button" data-testid="sign-out" [disabled]="signingOut()" (click)="signOut()">{{ t.signOut }}</button>
```

In `src/app/pages/login.ts`, replace

```ts
/** The notice the notes screen passes on after a deleted account (spec 0010), in the navigation state. */
function startNotice(router: Router): string | null {
  return router.currentNavigation()?.extras.state?.['notice'] === 'deleted' ? texts.login.accountDeleted : null;
}
```

with

```ts
/** The notice another screen passes on in the navigation state: a deleted account (spec 0010), a session that ended (spec 0012). */
function startNotice(router: Router): string | null {
  const notice: unknown = router.currentNavigation()?.extras.state?.['notice'];
  if (notice === 'deleted') {
    return texts.login.accountDeleted;
  }
  return notice === 'session' ? texts.login.sessionEnded : null;
}
```

- [ ] **Step 9: Run.** From `samples/notes-web`: `npm test`, `npx ng build`, `npm run check:docs` pass. `texts.spec.ts` checks the new texts are
printable ASCII (the apostrophe of "Confirm it's you" is one). Mutate and see a spec fail, then restore: drop the gate of the interceptor
(`while %s is in flight no other request to /auth/ leaves: …` fails, for both rows); let `session_required` fall through to the refresh (`401
session_required signs the person out …` fails); pass no `forRenewal` to `auth.refresh` in `tokenIsRenewed` (`an expired access token during %s: …`
fails by the 10-second timeout of the held refresh); let `refresh(true)` join the plain refresh's promise (`a plain refresh() started during a renewal does not hold up ...` fails the same way).

- [ ] **Step 10: Hand back** — uncommitted. Files: everything listed under Files, and each spec whose "Google or Microsoft" sentence changed.
Scan the touched files (`password-input.ts` holds the moved escapes, which the scan reads as text: it must print `clean`). Proposed subject:
`feat(sample): GitHub buttons, the settings calls and the settings screen with its password section`.

---

### Task 17: The sample: the confirmation dialog, connect, disconnect, the return screen's targets (changed by Amendment B at the end of this plan: read it first)

**Files** (all under `samples/notes-web/src/app/`):
- Create: `pages/confirm-dialog.ts`, `pages/settings.actions.spec.ts`, `pages/provider-return.settings.spec.ts`
- Modify: `pages/provider-buttons.ts`, `pages/provider-return.ts`, `pages/settings.ts`, `pages/provider-link.ts`, `texts.ts`
- Modify (specs): `pages/provider-buttons.spec.ts`, `pages/provider-return.spec.ts`, `pages/provider-link.spec.ts`

**Interfaces:**
- Consumes: `AuthService.confirmPassword`, `confirmWithTicket`, `connect`, `unlink`, `Me.confirmed_until`, `providerStartUrl(name, { connect: true })`,
  `SettingsPage`, `texts.settings` (Task 16).
- Produces: `ReauthTarget` (`'delete_account' | 'delete_company' | 'confirm' | 'connect'`); `armReauth(target: ReauthTarget)` and
  `takeReauth(): ReauthTarget | null` (the flag is `<target>:<epoch ms>`); `rememberPendingConnect(name)`, `takePendingConnect()`;
  `ProviderButtons` input `target` (default `'delete_account'`); `ASSIGN_LOCATION` (an `InjectionToken<(url: string) => void>`);
  `ConfirmDialog` (`app-confirm-dialog`; inputs `providers`; outputs `confirmed`, `cancelled`, `sendLink`; test ids `confirm-dialog`,
  `#confirm-password`, `confirm-submit`, `confirm-cancel`, `confirm-error`, `confirm-none`); the settings test ids `settings-notice`,
  `settings-error`; `/provider-return` sends `connectTicket` and `provider`, `notice`, or `reauthTicket` in the navigation state.

**Contract counts:** none.

**Decisions the plan takes here:**
- The flag's value is `<target>:<epoch ms>` (`delete_account:1777700000000`): one key, one shape; a value of another shape (the old bare time
  included) is no flag. A return whose purpose does not fit the target (`reauth` needs `delete_account`, `delete_company` or `confirm`;
  `connect` needs `connect`) goes to `/login` and sends nothing. The flag still holds no ticket, address or kind.
- A connect starts only with the window open (read from a fresh `GET /auth/me`): with it closed the dialog opens first. The round trip is
  started by `ASSIGN_LOCATION` (`window.location.assign`), so that the specs can see the address without leaving the page.
- When the dialog opens for a connect, the provider to connect is kept in `sessionStorage` (`notes-web.pending-connect`, `<name>:<epoch ms>`, read once
  and only within 10 minutes, like the flag): a provider confirmation leaves the page, and the screen it comes back to says "Confirmed. Connect
  {Provider} again."; the ticket of the connect itself never goes to storage (only to the navigation state, kept out of the history by
  `keepTicketOutOfHistory`). The key is also set when the return screen hands back a refused connect ticket (the path the spec describes), and
  cleared when a password confirmation goes on, the dialog is cancelled or a disconnect opens it, so that a dialog the person left does not make
  a later confirmation say "Connect X again" for a connect nobody started.
- The return screen's messages for a connect go to `/settings` in the navigation state as a sentence (`notice`), never a code the page could be
  told to show.

- [ ] **Step 1: Write the failing specs.**

In `pages/provider-buttons.spec.ts`, with the Edit tool's replace-all: `armReauth()` → `armReauth('delete_account')`;
`takeReauth()).toBe(true)` → `takeReauth()).toBe('delete_account')`; `takeReauth()).toBe(false)` → `takeReauth()).toBeNull()`;
`takeReauth(), value).toBe(false)` → `takeReauth(), value).toBeNull()`; and replace
`      'notes-web.reauth': String(Date.parse('2026-05-02T12:00:00Z')),` with
`      'notes-web.reauth': 'delete_account:' + String(Date.parse('2026-05-02T12:00:00Z')),`. In the test
`a time ahead of this clock, a text and an old-style 1 are no flag`, replace
`      for (const value of [String(Date.now() + MINUTE), 'yes', '1', '', '12:00', '-5']) {` with

```ts
      for (const value of [
        `delete_account:${Date.now() + MINUTE}`,
        String(Date.now()),   // the shape of spec 0011: no target
        `other:${Date.now()}`,
        'confirm:',
        'yes',
        '1',
        '',
        '12:00',
        '-5',
      ]) {
```

and add to the file's `describe`:

```ts

  it('a confirmation for the settings screen arms its target; each target is read back once (spec 0012)', () => {
    for (const target of ['delete_account', 'delete_company', 'confirm', 'connect'] as const) {
      armReauth(target);
      expect(sessionStorage.getItem('notes-web.reauth')).toMatch(new RegExp(`^${target}:\\d+$`));
      expect(takeReauth()).toBe(target);
      expect(takeReauth()).toBeNull();
    }
  });

  it('the target input of the buttons arms that target', () => {
    const fixture = draw((host) => {
      host.providers.set(['github']);
      host.action.set('confirm');
      host.reauth.set(true);
      host.target.set('confirm');
    });
    const link = (fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="provider-github"]')!;
    link.addEventListener('click', (event) => event.preventDefault());
    link.click();
    expect(takeReauth()).toBe('confirm');
    expect(rememberedProvider()).toBe('github');
  });

  it('keeps the provider of a pending connect, the name and the time only, once, and for ten minutes', () => {
    rememberPendingConnect('github');
    expect(sessionStorage.getItem('notes-web.pending-connect')).toMatch(/^github:\d+$/);
    expect(takePendingConnect()).toBe('github');
    expect(takePendingConnect()).toBeNull();
    for (const value of [
      `gitlab:${Date.now()}`,
      'github',   // the bare name: no time, no pending connect
      `github:${Date.now() + 60_000}`,
      `github:${Date.now() - 10 * 60_000 - 1000}`,
    ]) {
      sessionStorage.setItem('notes-web.pending-connect', value);
      expect(takePendingConnect()).toBeNull();
    }
  });
```

(The file's `draw` helper builds a host with signals for the inputs: add `target = signal<ReauthTarget>('delete_account')` to that host and
bind `[target]="target()"` in its template, and import `ReauthTarget`, `rememberPendingConnect`, `takePendingConnect`.)

In `pages/provider-return.spec.ts`, replace every `armReauth()` with `armReauth('delete_account')` (replace-all). The tests keep their names
and their expectations (`/notes` with `{ reauthTicket }` — Task 18 moves the deletion and changes them).

`pages/provider-return.settings.spec.ts` — copy the set-up of `provider-return.spec.ts` (its `Landing` component, `addressAtRequest`, the
`open(fragment)` helper, `stored`, `settled`, `expectNoTicketAnywhere`) to the top of this file, add `{ path: 'settings', component: Landing }`
and `{ path: 'team', component: Landing }` to the routes of its `provideRouter`, then:

```ts
describe('ProviderReturnPage: the targets of the settings screen (spec 0012)', () => {
  afterEach(() => {
    history.replaceState(null, '', '/');
    sessionStorage.clear();
    localStorage.clear();
  });

  it('connect with the connect flag: POST /auth/providers/connect with the ticket, then /settings saying it is connected', async () => {
    rememberProvider('github');
    armReauth('connect');
    const opened = await open(`ticket=${TICKET}&purpose=connect`);
    const request = opened.ctrl.expectOne('/auth/providers/connect');
    expect(request.request.body).toEqual({ ticket: TICKET });
    request.flush(null, status(204));
    await settled(opened);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settled(opened);
    expect(opened.router.url).toBe('/settings');
    expect(Landing.state).toEqual({ notice: 'GitHub connected.' });
    expectNoTicketAnywhere(opened);
    expect(history.length).toBe(opened.entries);
  });

  it.each([null, 'confirm', 'delete_account'] as const)('connect with the flag %s: /login, no call', async (target) => {
    if (target !== null) {
      armReauth(target);
    }
    const opened = await open(`ticket=${TICKET}&purpose=connect`);
    opened.ctrl.expectNone('/auth/providers/connect');
    expect(opened.router.url).toBe('/login');
    expect(Landing.state).toBeUndefined();
    expectNoTicketAnywhere(opened);
  });

  it.each(['connect', null] as const)('reauth with the flag %s: /login, no call', async (target) => {
    if (target !== null) {
      armReauth(target);
    }
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectNone('/auth/account/confirm');
    expect(opened.router.url).toBe('/login');
  });

  it('a connect refused for a closed window: /settings with the ticket and the provider in the navigation state only', async () => {
    rememberProvider('google');
    armReauth('connect');
    const opened = await open(`ticket=${TICKET}&purpose=connect`);
    opened.ctrl.expectOne('/auth/providers/connect').flush({ error: 'confirmation_required' }, status(403));
    await settled(opened);
    expect(opened.router.url).toBe('/settings');
    expect(Landing.state).toEqual({ connectTicket: TICKET, provider: 'google' });
    expect(window.location.href).not.toContain(TICKET);
    expect(stored()).not.toContain(TICKET);
  });

  it.each([
    [409, 'provider_in_use', 'You already have a Google account connected. Disconnect it first.'],
    [409, 'provider_already_linked', 'This Google account belongs to another account.'],
    [403, 'email_not_verified', 'Google has not verified this address.'],
    [403, 'no_email', 'This Google account has no email address we can use.'],
    [400, 'invalid_ticket', 'This took too long. Start again.'],
  ])('a connect refused with %i %s: /settings with its sentence', async (code, error, sentence) => {
    rememberProvider('google');
    armReauth('connect');
    const opened = await open(`ticket=${TICKET}&purpose=connect`);
    opened.ctrl.expectOne('/auth/providers/connect').flush({ error }, status(code));
    await settled(opened);
    expect(opened.router.url).toBe('/settings');
    expect(Landing.state).toEqual({ notice: sentence });
  });

  it('address_mismatch names the account\'s address, read from GET /auth/me', async () => {
    rememberProvider('github');
    armReauth('connect');
    const opened = await open(`ticket=${TICKET}&purpose=connect`);
    opened.ctrl.expectOne('/auth/providers/connect').flush({ error: 'address_mismatch' }, status(403));
    await settled(opened);
    opened.ctrl.match('/auth/me').forEach((request) => request.flush(providerMe));
    await settled(opened);
    expect(Landing.state).toEqual({ notice: `This GitHub account has another address. Connect the one with ${providerMe.email}.` });
  });

  it('reauth with the confirm flag: POST /auth/account/confirm with the ticket, then /settings', async () => {
    armReauth('confirm');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    const request = opened.ctrl.expectOne('/auth/account/confirm');
    expect(request.request.body).toEqual({ provider_ticket: TICKET });
    request.flush(null, status(204));
    await settled(opened);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settled(opened);
    expect(opened.router.url).toBe('/settings');
    expect(Landing.state).toBeUndefined();
    expectNoTicketAnywhere(opened);
  });

  it('reauth for a pending connect: /settings says "Confirmed. Connect GitHub again."', async () => {
    rememberPendingConnect('github');
    armReauth('confirm');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settled(opened);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settled(opened);
    expect(Landing.state).toEqual({ notice: 'Confirmed. Connect GitHub again.' });
    expect(sessionStorage.getItem('notes-web.pending-connect')).toBeNull();
  });

  it('a confirmation refused with 403 reauth_failed: /settings with its sentence', async () => {
    armReauth('confirm');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectOne('/auth/account/confirm').flush({ error: 'reauth_failed' }, status(403));
    await settled(opened);
    expect(Landing.state).toEqual({ notice: 'The confirmation did not work or took too long. Confirm again.' });
  });

  it('reauth with the delete_company flag: /team with the ticket in the navigation state, and no call', async () => {
    armReauth('delete_company');
    const opened = await open(`ticket=${TICKET}&purpose=reauth`);
    opened.ctrl.expectNone('/auth/account/confirm');
    expect(opened.router.url).toBe('/team');
    expect(Landing.state).toEqual({ reauthTicket: TICKET });
  });
});
```

(The screen reads the account's address from `GET /auth/me` when the connect says `address_mismatch` and no person is held yet.)

`pages/settings.actions.spec.ts`:

```ts
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { Me } from '../auth/auth.service';
import { authInterceptor } from '../auth/auth.interceptor';
import { AuthService } from '../auth/auth.service';
import { clickOn, has, pageText, privateMe, providerMe, settle, signedInAs, submitForm, typeInto, withWindow } from '../../testing/helpers';
import { ASSIGN_LOCATION, rememberPendingConnect, takePendingConnect, takeReauth } from './provider-buttons';
import { SettingsPage } from './settings';

const status = (code: number) => ({ status: code, statusText: String(code) });
const ALL = ['google', 'microsoft', 'github'];
const open5 = () => new Date(Date.now() + 5 * 60 * 1000);
const TICKET = 'TICKET-abcdefghijklmnopqrstuvwxyz-0123456789A';

async function open(me: Me, state: Record<string, unknown> | null = null) {
  const assign = vi.fn();
  TestBed.configureTestingModule({
    providers: [
      provideRouter([]),
      provideHttpClient(withInterceptors([authInterceptor])),
      provideHttpClientTesting(),
      { provide: ASSIGN_LOCATION, useValue: assign },
    ],
  });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const router = TestBed.inject(Router);
  if (state !== null) {
    vi.spyOn(router, 'currentNavigation').mockReturnValue({ extras: { state } } as never);
  }
  vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(SettingsPage);
  fixture.detectChanges();
  ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'self-service', providers: ALL });
  ctrl.expectOne('/auth/me').flush(me);
  await settle(fixture);
  return { fixture, ctrl, auth, assign };
}

type Opened = Awaited<ReturnType<typeof open>>;

/** The click reads the person again: the test answers with the window it wants. */
async function click(opened: Opened, selector: string, me: Me): Promise<void> {
  clickOn(opened.fixture, selector);
  await settle(opened.fixture);
  opened.ctrl.expectOne('/auth/me').flush(me);
  await settle(opened.fixture);
}

async function confirmWithPassword(opened: Opened, password: string): Promise<void> {
  typeInto(opened.fixture, '#confirm-password', password);
  submitForm(opened.fixture, 'form[data-testid="confirm-form"]');
  await settle(opened.fixture);
}

describe('SettingsPage: connect and disconnect (spec 0012)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    sessionStorage.clear();
  });

  it('Connect with an open window: no dialog, the round trip starts with purpose=connect and the connect flag', async () => {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-github"]', withWindow(privateMe, open5()));
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(false);
    expect(opened.assign).toHaveBeenCalledWith('/auth/providers/github/start?purpose=connect');
    expect(takeReauth()).toBe('connect');
  });

  it('Connect with the window closed: the dialog first; after the password the round trip starts', async () => {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-google"]', privateMe);
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    expect(pageText(opened.fixture)).toContain("Confirm it's you");
    expect(opened.assign).not.toHaveBeenCalled();
    await confirmWithPassword(opened, 'Another-Passw0rd');
    const request = opened.ctrl.expectOne('/auth/account/confirm');
    expect(request.request.body).toEqual({ password: 'Another-Passw0rd' });
    request.flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(withWindow(privateMe, open5()));
    await settle(opened.fixture);
    expect(opened.assign).toHaveBeenCalledWith('/auth/providers/google/start?purpose=connect');
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(false);
  });

  it('a window whose time has passed is closed, whatever GET /auth/me says it was', async () => {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-google"]', withWindow(privateMe, new Date(Date.now() - 1000)));
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
  });

  it('the dialog without a password: one "Confirm with ..." per linked offered provider, armed for the settings screen', async () => {
    const me: Me = { ...providerMe, linked_providers: ['github', 'google'] };
    const opened = await open(me);
    await click(opened, '[data-testid="connect-microsoft"]', me);
    const links = [...(opened.fixture.nativeElement as HTMLElement).querySelectorAll<HTMLAnchorElement>('[data-testid="confirm-dialog"] a')];
    expect(links.map((a) => a.textContent?.trim())).toEqual(['Confirm with Google', 'Confirm with GitHub']);
    expect(links.map((a) => a.getAttribute('href'))).toEqual([
      '/auth/providers/google/start?purpose=reauth',
      '/auth/providers/github/start?purpose=reauth',
    ]);
    expect(takePendingConnect()).toBe('microsoft');   // what the return screen will name: "Connect Microsoft again"
    links[0].addEventListener('click', (event) => event.preventDefault());
    links[0].click();
    expect(takeReauth()).toBe('confirm');
  });

  it('the dialog with no usable method: its sentence and "Send me a link"', async () => {
    // No password and no login of an offered provider (its only login's provider was dropped from the manifest, which the rows read as none).
    const me: Me = { ...providerMe, linked_providers: [] };
    const opened = await open(me);
    await click(opened, '[data-testid="connect-github"]', me);
    expect(pageText(opened.fixture)).toContain('None of your sign-in methods can confirm here. Set a password first.');
    clickOn(opened.fixture, '[data-testid="confirm-dialog"] [data-testid="send-link"]');
    await settle(opened.fixture);
    expect(opened.ctrl.expectOne('/auth/password/forgot').request.body).toEqual({ email: me.email });
  });

  it.each([
    [{ error: 'wrong_password' }, 403, 'The password is wrong.'],
    [{ error: 'too_many_attempts', retry_after_seconds: 60 }, 429, 'Too many attempts. Try again in 1 minute.'],
  ])('a refused confirmation (%j) keeps the dialog open with its sentence', async (answer, code, sentence) => {
    const opened = await open(privateMe);
    await click(opened, '[data-testid="connect-google"]', privateMe);
    await confirmWithPassword(opened, 'Wrong-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(answer, status(code));
    await settle(opened.fixture);
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    expect(pageText(opened.fixture)).toContain(sentence);
    expect(opened.assign).not.toHaveBeenCalled();
  });

  it('Disconnect with an open window: the unlink, the new token stored, and its sentence', async () => {
    const me = withWindow({ ...privateMe, linked_providers: ['google'] }, open5());
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);
    const request = opened.ctrl.expectOne('/auth/account/logins/unlink');
    expect(request.request.body).toEqual({ provider: 'google' });
    request.flush({ status: 'authenticated', access_token: 'renewed' });
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush({ ...me, linked_providers: [] });
    await settle(opened.fixture);
    expect(opened.auth.token()).toBe('renewed');
    expect(pageText(opened.fixture)).toContain('Google disconnected.');
  });

  it('Disconnect answered 403 confirmation_required (the window closed meanwhile): the dialog, then the unlink again', async () => {
    const me = withWindow({ ...privateMe, linked_providers: ['google'] }, open5());
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);
    opened.ctrl.expectOne('/auth/account/logins/unlink').flush({ error: 'confirmation_required' }, status(403));
    await settle(opened.fixture);
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    await confirmWithPassword(opened, 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(me);
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/account/logins/unlink').flush({ status: 'authenticated', access_token: 'renewed' });
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush({ ...me, linked_providers: [] });
    await settle(opened.fixture);
    expect(opened.auth.token()).toBe('renewed');
  });

  it.each([
    [409, 'last_login', 'Set a password or connect another provider first.'],
    [404, 'not_found', 'This sign-in method is no longer connected.'],
  ])('Disconnect answered %i %s: its sentence, and the rows read again', async (code, error, sentence) => {
    const me = withWindow({ ...privateMe, linked_providers: ['google'] }, open5());
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);
    opened.ctrl.expectOne('/auth/account/logins/unlink').flush({ error }, status(code));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(me);
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(sentence);
  });

  it('the buttons are disabled while a disconnect is in flight', async () => {
    const me = withWindow({ ...privateMe, linked_providers: ['google', 'github'] }, open5());
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);
    const buttons = [...(opened.fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('[data-testid^="disconnect-"], [data-testid^="connect-"]')];
    expect(buttons.every((button) => button.disabled)).toBe(true);
    opened.ctrl.expectOne('/auth/account/logins/unlink').flush({ error: 'last_login' }, status(409));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(me);
    await settle(opened.fixture);
  });

  it('a connect ticket handed back for a closed window: the dialog opens; after the password the ticket is sent again', async () => {
    const opened = await open(privateMe, { connectTicket: TICKET, provider: 'github' });
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    expect(JSON.stringify({ ...sessionStorage })).not.toContain(TICKET);
    await confirmWithPassword(opened, 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(withWindow(privateMe, open5()));
    await settle(opened.fixture);
    const connect = opened.ctrl.expectOne('/auth/providers/connect');
    expect(connect.request.body).toEqual({ ticket: TICKET });
    connect.flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush({ ...privateMe, linked_providers: ['github'] });
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('GitHub connected.');
    expect(pageText(opened.fixture)).not.toContain(TICKET);
  });

  it('a connect ticket handed back for a closed window, then "Confirm with ...": the provider is kept for "Connect GitHub again" and the confirmation is armed', async () => {
    const me: Me = { ...providerMe, linked_providers: ['google'] };
    const opened = await open(me, { connectTicket: TICKET, provider: 'github' });
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    expect(sessionStorage.getItem('notes-web.pending-connect')).toMatch(/^github:\d+$/);   // the name and the time, never the ticket
    expect(JSON.stringify({ ...sessionStorage })).not.toContain(TICKET);
    const link = (opened.fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="confirm-dialog"] a')!;
    link.addEventListener('click', (event) => event.preventDefault());
    link.click();
    expect(takeReauth()).toBe('confirm');
    expect(takePendingConnect()).toBe('github');   // what /provider-return reads when the confirmation comes back
  });

  it('a pending connect that was left is not named by the confirmation of a later disconnect', async () => {
    const me: Me = { ...providerMe, linked_providers: ['github', 'google'] };
    rememberPendingConnect('microsoft');   // the dialog of a connect that the person left by navigating away
    const opened = await open(me);
    await click(opened, '[data-testid="disconnect-google"]', me);   // the window is closed: the dialog opens for the disconnect
    expect(has(opened.fixture, '[data-testid="confirm-dialog"]')).toBe(true);
    expect(takePendingConnect()).toBeNull();
  });

  it('a password confirmation of a handed-back ticket forgets the pending connect', async () => {
    const opened = await open(privateMe, { connectTicket: TICKET, provider: 'github' });
    await confirmWithPassword(opened, 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(withWindow(privateMe, open5()));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/providers/connect').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush({ ...privateMe, linked_providers: ['github'] });
    await settle(opened.fixture);
    expect(takePendingConnect()).toBeNull();
  });

  it('a sentence the return screen handed on is shown', async () => {
    const opened = await open(privateMe, { notice: 'GitHub connected.' });
    expect((opened.fixture.nativeElement as HTMLElement).querySelector('[data-testid="settings-notice"]')?.textContent?.trim()).toBe('GitHub connected.');
  });
});
```

In `pages/provider-link.spec.ts`, add to its `describe`:

```ts

  it('409 provider_in_use: the account already has a login of this provider (spec 0012)', async () => {
    const { harness, ctrl } = await open({ ...verified, provider: 'github' });
    typeInto(harness.fixture, '#password', 'Another-Passw0rd');
    submitForm(harness.fixture);
    await settle(harness.fixture);
    ctrl.expectOne('/auth/providers/link').flush({ error: 'provider_in_use' }, { status: 409, statusText: '409' });
    await settle(harness.fixture);
    expect(pageText(harness.fixture)).toContain('This account already has a GitHub account connected. Sign in with it or with your password.');
  });
```

(use the file's own names for the password field and the submit helper; `open` returns what the file's other tests destructure).

- [ ] **Step 2: See them fail.** `npm test`: the flag has no target; the settings buttons do nothing; the return screen sends `connect` to
`/login`.

- [ ] **Step 3: The flag and the location.** In `pages/provider-buttons.ts`:

(a) replace

```ts
import { Component, input } from '@angular/core';
import { ProviderName, isProviderName, providerStartUrl } from '../account-api';
import { texts } from '../texts';

const PROVIDER_KEY = 'notes-web.provider';
const REAUTH_KEY = 'notes-web.reauth';
```

with

```ts
import { Component, InjectionToken, input } from '@angular/core';
import { ProviderName, isProviderName, providerStartUrl } from '../account-api';
import { texts } from '../texts';

const PROVIDER_KEY = 'notes-web.provider';
const REAUTH_KEY = 'notes-web.reauth';
const PENDING_CONNECT_KEY = 'notes-web.pending-connect';

/** What a confirmation round trip is for (spec 0012): the screen it comes back to, and what that screen does with the ticket. */
export type ReauthTarget = 'delete_account' | 'delete_company' | 'confirm' | 'connect';

const FLAG = /^(delete_account|delete_company|confirm|connect):(\d{1,15})$/;
const PENDING_CONNECT = /^([a-z]{1,20}):(\d{1,15})$/;

/** Opens a page outside the app (a provider's start): a function of its own, so that a spec can see the address without leaving. */
export const ASSIGN_LOCATION = new InjectionToken<(url: string) => void>('ASSIGN_LOCATION', {
  providedIn: 'root',
  factory: () => (url: string) => window.location.assign(url),
});
```

(b) replace the comment and both functions from `/**\n * A one-shot flag, set when the person follows "Confirm with ..." in the delete section` down to the closing brace of
`takeReauth()` with:

```ts
/**
 * A one-shot flag, set when this tab starts a round trip whose ticket comes back to the app (spec 0011, As built D30; spec 0012: with its
 * target): `<target>:<the time it was set, in milliseconds>`, and nothing else, never a ticket, an address or a kind. Without a flag of the
 * fitting target that is at most 10 minutes old, /provider-return hands nothing on and sends nothing, so a forged link does nothing, and a
 * confirmation that was abandoned cannot arm a link later. Storage that is off changes nothing here but that the ticket is then not used.
 */
export function armReauth(target: ReauthTarget): void {
  try {
    sessionStorage.setItem(REAUTH_KEY, `${target}:${Date.now()}`);
  } catch {
    // the round trip then ends at the sign-in screen
  }
}

/**
 * Reads the flag and clears it, whether it is fresh or not: its target once per `armReauth`, and only within 10 minutes of it. A value of
 * another shape (the bare time of spec 0011 included), or a time that is not in the past, is no flag. Storage that throws counts as no flag.
 */
export function takeReauth(): ReauthTarget | null {
  try {
    const value = sessionStorage.getItem(REAUTH_KEY);
    sessionStorage.removeItem(REAUTH_KEY);
    const match = value === null ? null : FLAG.exec(value);
    if (match === null) {
      return null;
    }
    const age = Date.now() - Number(match[2]);
    return age >= 0 && age <= REAUTH_LIFETIME_MS ? (match[1] as ReauthTarget) : null;
  } catch {
    return null;
  }
}

/**
 * The provider a connect waits for while the person confirms with another one (spec 0012): `<name>:<the time it was set, in milliseconds>`, the name
 * and the time only. Like the flag it is read once and only within 10 minutes, so that a dialog the person left does not name a connect that
 * nobody started in the sentence of a later confirmation.
 */
export function rememberPendingConnect(name: ProviderName): void {
  try {
    sessionStorage.setItem(PENDING_CONNECT_KEY, `${name}:${Date.now()}`);
  } catch {
    // the settings screen then says only that the confirmation worked
  }
}

/** Reads and clears the provider of a pending connect, or null: no such value, one of another shape, a name this app does not know, or one older than 10 minutes. */
export function takePendingConnect(): ProviderName | null {
  try {
    const value = sessionStorage.getItem(PENDING_CONNECT_KEY);
    sessionStorage.removeItem(PENDING_CONNECT_KEY);
    const match = value === null ? null : PENDING_CONNECT.exec(value);
    if (match === null) {
      return null;
    }
    const name = match[1];
    const age = Date.now() - Number(match[2]);
    return age >= 0 && age <= REAUTH_LIFETIME_MS && isProviderName(name) ? name : null;
  } catch {
    return null;
  }
}
```

(c) replace

```ts
  /** The confirmation of a deletion: the start asks for purpose=reauth. */
  readonly reauth = input(false);
```

with

```ts
  /** A confirmation: the start asks for purpose=reauth. */
  readonly reauth = input(false);
  /** Spec 0012: the screen a confirmation comes back to (the flag's target). */
  readonly target = input<ReauthTarget>('delete_account');
```

and

```ts
    if (this.reauth()) {
      armReauth();
    }
```

with

```ts
    if (this.reauth()) {
      armReauth(this.target());
    }
```

and in the component's summary replace `/**\n * The buttons of spec 0011:` with `/**\n * The buttons of specs 0011 and 0012:` (one word, the Edit tool).

- [ ] **Step 4: The return screen.** In `pages/provider-return.ts`:

(a) replace `import { rememberedProvider, takeReauth } from './provider-buttons';` with
`import { providerTitle, rememberedProvider, takePendingConnect, takeReauth } from './provider-buttons';`;

(b) replace

```ts
    } else if (purpose === 'reauth') {
      // The notes screen opens "Delete account" ready to confirm: the ticket is spent there, not here. Only for a confirmation this tab started.
      await (armed ? this.go(DEFAULT_RETURN_URL, { reauthTicket: this.ticket }) : this.go('/login'));
    } else if (purpose === 'sign_in' || purpose === 'invite') {
```

with

```ts
    } else if (purpose === 'reauth') {
      // Only for a confirmation this tab started, and to the screen it was started for (spec 0012: the flag's target).
      if (armed === 'delete_account') {
        // The notes screen opens "Delete account" ready to confirm: the ticket is spent there, not here.
        await this.go(DEFAULT_RETURN_URL, { reauthTicket: this.ticket });
      } else if (armed === 'delete_company') {
        await this.go('/team', { reauthTicket: this.ticket });
      } else if (armed === 'confirm') {
        await this.confirm();
      } else {
        await this.go('/login');
      }
    } else if (purpose === 'connect') {
      await (armed === 'connect' ? this.connect() : this.go('/login'));
    } else if (purpose === 'sign_in' || purpose === 'invite') {
```

(c) add after the method `exchange()`:

```ts

  /** Spec 0012: the confirmation of the settings screen. Then /settings, which says so when a connect waits for it. */
  private async confirm(): Promise<void> {
    const result = await this.auth.confirmWithTicket(this.ticket);
    const pending = takePendingConnect();
    if (result.ok) {
      await this.go('/settings', pending === null ? undefined : { notice: texts.settings.connectAgain(providerTitle(pending)) });
    } else if (result.failure.kind !== 'session_required') {
      await this.go('/settings', { notice: await this.sentenceOf(result.failure) });
    }
  }

  /**
   * Spec 0012: the connect of the settings screen. A closed window hands the ticket back to /settings in the navigation state, which asks for
   * a confirmation and sends it again; every other answer is a sentence there.
   */
  private async connect(): Promise<void> {
    const provider = rememberedProvider();
    const result = await this.auth.connect(this.ticket);
    if (result.ok) {
      await this.go('/settings', { notice: texts.settings.connectedNow(providerTitle(provider)) });
    } else if (result.failure.kind === 'confirmation_required') {
      await this.go('/settings', { connectTicket: this.ticket, provider });
    } else if (result.failure.kind !== 'session_required') {
      await this.go('/settings', { notice: await this.sentenceOf(result.failure, provider) });
    }
  }

  private async sentenceOf(failure: Failure, provider: ProviderName | null = null): Promise<string> {
    const s = texts.settings;
    const title = providerTitle(provider);
    switch (failure.kind) {
      case 'provider_in_use':
        return s.providerInUse(title);
      case 'provider_already_linked':
        return s.providerAlreadyLinked(title);
      case 'address_mismatch': {
        if (this.auth.me() === null) {
          await this.auth.loadMe();
        }
        return s.addressMismatch(title, this.auth.me()?.email ?? '');
      }
      case 'email_not_verified':
        return s.emailNotVerified(title);
      case 'no_email':
        return s.noEmail(title);
      case 'invalid_ticket':
        return s.invalidTicket;
      case 'reauth_failed':
        return s.reauthFailed;
      case 'no_membership':
        return texts.login.noMembership;
      default:
        return this.common.somethingWrong;
    }
  }
```

and import `ProviderName` from `'../account-api'`. (`session_required`: the interceptor has already signed the person out to `/login`; the
screen does not navigate again.)

- [ ] **Step 5: The dialog.** `pages/confirm-dialog.ts`:

```ts
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { ProviderName } from '../account-api';
import { AuthService } from '../auth/auth.service';
import { texts } from '../texts';
import { acceptablePassword } from './password-input';
import { ProviderButtons } from './provider-buttons';

/**
 * Spec 0012 → Confirming a connect or a disconnect: "Confirm it's you". The password and "Confirm" for an account with a password; one
 * "Confirm with {Provider}" per linked offered provider otherwise (the browser leaves, and comes back to /settings with the window open);
 * with neither, the sentence and "Send me a link".
 */
@Component({
  selector: 'app-confirm-dialog',
  imports: [ReactiveFormsModule, ProviderButtons],
  template: `
    <section role="dialog" aria-modal="true" aria-labelledby="confirm-title" data-testid="confirm-dialog">
      <h2 id="confirm-title" tabindex="-1">{{ t.confirmTitle }}</h2>
      @if (me()?.has_password) {
        <form [formGroup]="form" (ngSubmit)="confirm()" novalidate data-testid="confirm-form">
          <label for="confirm-password">{{ password }}</label>
          <input id="confirm-password" type="password" formControlName="password" autocomplete="current-password" />
          <button type="submit" data-testid="confirm-submit" [disabled]="busy()">{{ t.confirm }}</button>
        </form>
      } @else if (usable().length > 0) {
        <app-provider-buttons [providers]="usable()" action="confirm" [reauth]="true" target="confirm" [separator]="false" />
      } @else {
        <p data-testid="confirm-none">{{ t.noMethod }}</p>
        <button type="button" data-testid="send-link" (click)="sendLink.emit()">{{ t.sendLink }}</button>
      }
      @if (error(); as text) {
        <p class="error" role="alert" data-testid="confirm-error">{{ text }}</p>
      }
      <button type="button" data-testid="confirm-cancel" [disabled]="busy()" (click)="cancelled.emit()">{{ t.cancel }}</button>
    </section>
  `,
})
export class ConfirmDialog {
  protected readonly t = texts.settings;
  protected readonly password = texts.common.password;
  private readonly auth = inject(AuthService);

  /** The providers the app offers now: a login of another one cannot confirm here. */
  readonly providers = input.required<readonly ProviderName[]>();
  readonly confirmed = output<void>();
  readonly cancelled = output<void>();
  readonly sendLink = output<void>();

  protected readonly me = this.auth.me;
  protected readonly usable = computed(() => {
    const offered = this.providers();
    return (this.me()?.linked_providers ?? []).filter((name) => offered.includes(name)).sort((a, b) => offered.indexOf(a) - offered.indexOf(b));
  });
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(NonNullableFormBuilder).group({ password: [''] });

  protected async confirm(): Promise<void> {
    const password = this.form.controls.password.value;
    if (this.busy()) {
      return;
    }
    if (!acceptablePassword(password)) {
      this.error.set(texts.notes.passwordRequired);
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const result = await this.auth.confirmPassword(password);
    this.busy.set(false);
    if (result.ok) {
      this.form.reset();
      this.confirmed.emit();
      return;
    }
    switch (result.failure.kind) {
      case 'wrong_password':
        this.error.set(this.t.wrongPassword);
        break;
      case 'too_many_attempts':
        this.error.set(texts.login.tooManyAttempts(result.failure.retryAfterSeconds));
        break;
      case 'reauth_required':
      case 'password_required':
        await this.auth.loadMe();   // the account is not what the dialog thought: draw what fits it now
        break;
      case 'no_membership':
        this.error.set(texts.login.noMembership);
        break;
      case 'too_many_requests':
      case 'session_required':
        break;
      default:
        this.error.set(texts.common.somethingWrong);
    }
  }
}
```

The test `the dialog without a password: one "Confirm with ..." per linked offered provider` expects the order of the options (Google, then
GitHub) whatever the order of `linked_providers` (sorted by name): `usable()` sorts by the options' order.

- [ ] **Step 6: The actions of the settings screen.** In `pages/settings.ts`:

(a) replace the imports with

```ts
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AccountApi, ProviderName, isProviderName, providerStartUrl } from '../account-api';
import { AuthService, Failure } from '../auth/auth.service';
import { texts } from '../texts';
import { ConfirmDialog } from './confirm-dialog';
import { acceptablePassword } from './password-input';
import { ASSIGN_LOCATION, armReauth, providerTitle, rememberPendingConnect, rememberProvider, takePendingConnect } from './provider-buttons';
import { PasswordRules } from './password-rules';
import { keepTicketOutOfHistory } from './ticket-history';

/** What waits for the confirmation: a connect to start, a disconnect, or a connect ticket the return screen handed back. */
type Pending =
  | { kind: 'connect'; provider: ProviderName }
  | { kind: 'disconnect'; provider: ProviderName }
  | { kind: 'resend'; provider: ProviderName | null; ticket: string };

/** What /provider-return handed on in the navigation state (spec 0012): a sentence, or a connect ticket refused for a closed window. */
function handedOn(router: Router): { notice: string | null; pending: Pending | null } {
  const state = router.currentNavigation()?.extras.state ?? {};
  const notice: unknown = state['notice'];
  const ticket: unknown = state['connectTicket'];
  const provider: unknown = state['provider'];
  return {
    notice: typeof notice === 'string' ? notice : null,
    pending: typeof ticket === 'string' && ticket !== '' ? { kind: 'resend', provider: isProviderName(provider) ? provider : null, ticket } : null,
  };
}
```

and add `ConfirmDialog` to the component's `imports`.

(b) in the template, replace

```html
                  <button type="button" [attr.data-testid]="'disconnect-' + row.name" [disabled]="!row.canDisconnect || busy()">
```

with

```html
                  <button
                    type="button"
                    [attr.data-testid]="'disconnect-' + row.name"
                    [disabled]="!row.canDisconnect || busy()"
                    (click)="disconnect(row.name)"
                  >
```

and

```html
                  <button type="button" [attr.data-testid]="'connect-' + row.name" [disabled]="busy()">{{ t.connect }}</button>
```

with

```html
                  <button type="button" [attr.data-testid]="'connect-' + row.name" [disabled]="busy()" (click)="connect(row.name)">
                    {{ t.connect }}
                  </button>
```

and replace

```html
        <section data-testid="methods">
          <h2>{{ t.methods }}</h2>
```

with

```html
        @if (notice(); as text) {
          <p class="status" role="status" data-testid="settings-notice">{{ text }}</p>
        }
        @if (actionError(); as text) {
          <p class="error" role="alert" data-testid="settings-error">{{ text }}</p>
        }
        @if (pending() !== null) {
          <app-confirm-dialog [providers]="offered()" (confirmed)="confirmed()" (cancelled)="cancelPending()" (sendLink)="sendLink()" />
        }
        <section data-testid="methods">
          <h2>{{ t.methods }}</h2>
```

(c) in the class, replace

```ts
  protected readonly t = texts.settings;
  protected readonly common = texts.common;
  protected readonly auth = inject(AuthService);
  private readonly account = inject(AccountApi);
```

with

```ts
  protected readonly t = texts.settings;
  protected readonly common = texts.common;
  protected readonly auth = inject(AuthService);
  private readonly account = inject(AccountApi);
  private readonly router = inject(Router);
  private readonly assign = inject(ASSIGN_LOCATION);
  private readonly fromReturn = handedOn(this.router);

  /** A sentence of the last action, or the one the return screen handed on. */
  protected readonly notice = signal<string | null>(this.fromReturn.notice);
  protected readonly actionError = signal<string | null>(null);
  /** Opens the dialog while it is set. */
  protected readonly pending = signal<Pending | null>(this.fromReturn.pending);
```

(d) replace

```ts
  ngOnInit(): void {
    void this.account.options().then((options) => this.offered.set(options.providers));
    // Always read afresh: the window and the logins are what the screen acts on.
    void this.auth.loadMe();
  }
```

with

```ts
  ngOnInit(): void {
    void this.account.options().then((options) => this.offered.set(options.providers));
    // Always read afresh: the window and the logins are what the screen acts on.
    void this.auth.loadMe();
    if (this.fromReturn.pending !== null) {
      // The ticket lives in this component's memory only: the history entry is replaced without it.
      void keepTicketOutOfHistory(this.router);
      if (this.fromReturn.pending.kind === 'resend' && this.fromReturn.pending.provider !== null) {
        // A confirmation with a provider from this dialog leaves the page, and the ticket with it (one auth_pt): the screen it comes back to
        // names the provider to connect again (spec 0012, "Confirmed. Connect {Provider} again.").
        rememberPendingConnect(this.fromReturn.pending.provider);
      }
    }
  }

  /** The window of this device's session, as the last GET /auth/me said, is still open. */
  private windowOpen(): boolean {
    const until = this.me()?.confirmed_until;
    return typeof until === 'string' && Date.parse(until) > Date.now();
  }

  protected async connect(name: ProviderName): Promise<void> {
    if (!(await this.readWindow())) {
      rememberPendingConnect(name);
      this.pending.set({ kind: 'connect', provider: name });
      return;
    }
    this.startConnect(name);
  }

  /** A connect starts its round trip only with the window open (spec 0012). */
  private startConnect(name: ProviderName): void {
    rememberProvider(name);
    armReauth('connect');
    this.assign(providerStartUrl(name, { connect: true }));
  }

  protected async disconnect(name: ProviderName): Promise<void> {
    if (!(await this.readWindow())) {
      this.forgetPendingConnect();   // a connect that was left open must not be named by the confirmation of this disconnect
      this.pending.set({ kind: 'disconnect', provider: name });
      return;
    }
    await this.unlink(name);
  }

  /** Reads the person again, with the buttons disabled meanwhile; true when the window is open. */
  private async readWindow(): Promise<boolean> {
    if (this.busy()) {
      return false;
    }
    this.busy.set(true);
    this.notice.set(null);
    this.actionError.set(null);
    await this.auth.loadMe();
    this.busy.set(false);
    return this.windowOpen();
  }

  private async unlink(name: ProviderName): Promise<void> {
    this.busy.set(true);
    const result = await this.auth.unlink(name);
    this.busy.set(false);
    if (result.ok) {
      this.notice.set(this.t.disconnectedNow(providerTitle(name)));
    } else if (result.failure.kind === 'confirmation_required') {
      this.forgetPendingConnect();
      this.pending.set({ kind: 'disconnect', provider: name });
    } else {
      await this.refusedAction(result.failure, name);
    }
  }

  private async resend(ticket: string, provider: ProviderName | null): Promise<void> {
    this.busy.set(true);
    const result = await this.auth.connect(ticket);
    this.busy.set(false);
    if (result.ok) {
      this.notice.set(this.t.connectedNow(providerTitle(provider)));
    } else {
      await this.refusedAction(result.failure, provider);
    }
  }

  /** The dialog's password confirmation worked: what waited goes on at once. */
  protected async confirmed(): Promise<void> {
    const waiting = this.pending();
    this.pending.set(null);
    this.forgetPendingConnect();   // a password confirmation goes on here: no round trip comes back to name the connect
    if (waiting === null) {
      return;
    }
    if (waiting.kind === 'connect') {
      this.startConnect(waiting.provider);
    } else if (waiting.kind === 'disconnect') {
      await this.unlink(waiting.provider);
    } else {
      await this.resend(waiting.ticket, waiting.provider);
    }
  }

  protected cancelPending(): void {
    this.pending.set(null);
    this.forgetPendingConnect();
  }

  /** Whatever connect was waiting is over: it goes on here after a password, the person cancelled, or another action opened the dialog. */
  private forgetPendingConnect(): void {
    takePendingConnect();
  }

  /** The answers of a connect or a disconnect, one sentence each; the rows are read again, since they may have changed. */
  private async refusedAction(failure: Failure, provider: ProviderName | null): Promise<void> {
    const title = providerTitle(provider);
    const sentences: Partial<Record<Failure['kind'], string>> = {
      last_login: this.t.lastLogin,
      not_found: this.t.notFound,
      provider_in_use: this.t.providerInUse(title),
      provider_already_linked: this.t.providerAlreadyLinked(title),
      email_not_verified: this.t.emailNotVerified(title),
      no_email: this.t.noEmail(title),
      invalid_ticket: this.t.invalidTicket,
      no_membership: texts.login.noMembership,
    };
    if (failure.kind === 'session_required' || failure.kind === 'too_many_requests') {
      return;   // signed out by the interceptor, or the bar says when to try again
    }
    if (failure.kind === 'address_mismatch') {
      this.actionError.set(this.t.addressMismatch(title, this.me()?.email ?? ''));
    } else {
      this.actionError.set(sentences[failure.kind] ?? this.common.somethingWrong);
    }
    await this.auth.loadMe();
  }
```


(e) `sendLink()` (Task 16) is reused by the dialog's "Send me a link": no change.

- [ ] **Step 7: The link screen and the text.** In `texts.ts`, replace
`    wrongPassword: "Wrong password. No password yet? Use 'Forgot your password?' to set one.",` (in `providerLink`) with

```ts
    wrongPassword: "Wrong password. No password yet? Use 'Forgot your password?' to set one.",
    // Spec 0012: one login per provider per account.
    providerInUse: (provider: string): string =>
      `This account already has a ${provider} account connected. Sign in with it or with your password.`,
```

In `pages/provider-link.ts`, replace

```ts
        case 'no_email':
          this.error.set(this.returnTexts.noEmail);
          break;
```

with

```ts
        case 'no_email':
          this.error.set(this.returnTexts.noEmail);
          break;
        case 'provider_in_use':
          this.error.set(this.t.providerInUse(this.provider));
          break;
```

- [ ] **Step 8: Run.** `npm test`, `npx ng build`, `npm run check:docs` pass. Mutate and see a spec fail, then restore: let `takeReauth`
accept the bare time (`a time ahead of this clock …` fails on the second value); start the connect without reading the window (`Connect with
the window closed …` fails); leave out `rememberPendingConnect` in `ngOnInit` (`a connect ticket handed back for a closed window, then "Confirm
with ...": …` fails); let `takePendingConnect` ignore the time (`keeps the provider of a pending connect, …` fails).

- [ ] **Step 9: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(sample): confirm, connect and disconnect on the settings screen, and the return screen's targets`.

---

## Day 5 — Wednesday 13.05: the deletions in the sample, the browser test, the documents

### Task 18: The sample: "Delete account" on `/settings`, "Delete company" on `/team`; notes-api `DELETE /api/org` (changed by Amendment B at the end of this plan: read it first)

**Files:**
- Create: `samples/notes-web/src/app/pages/delete-account.ts`, `samples/notes-web/src/app/pages/delete-company.ts`,
  `samples/notes-web/src/app/pages/delete-account.spec.ts`, `samples/notes-web/src/app/pages/delete-account.provider.spec.ts`,
  `samples/notes-web/src/app/pages/delete-company.spec.ts`, `samples/notes-api/tests/test_delete_org_data.py`
- Modify: `samples/notes-web/src/app/pages/notes.ts`, `pages/settings.ts`, `pages/team.ts`, `pages/ticket-history.ts`, `pages/provider-return.ts`,
  `pages/login.ts`, `auth/auth.service.ts`, `texts.ts`; `samples/notes-api/src/notes_api/app.py`
- Modify (specs): `pages/notes.account.spec.ts`, `pages/notes.spec.ts`, `pages/provider-return.spec.ts`, `pages/team.spec.ts`
- Delete: `samples/notes-web/src/app/pages/notes.provider.spec.ts` (its tests move to `delete-account.provider.spec.ts`)

**Interfaces:**
- Consumes: `ConfirmDialog`'s neighbours: `ProviderButtons` with `target` (Task 17), `AuthService.confirmPassword` (Task 16),
  `acceptablePassword` (Task 16), `DELETE /auth/org` with a ticket (Task 13).
- Produces: `DeleteAccountSection` (`app-delete-account`, the test ids of the notes screen's section of spec 0010/0011 unchanged:
  `delete-account`, `delete-section`, `delete-warning`, `delete-form`, `#delete-password`, `delete-confirm`, `delete-cancel`, `delete-error`,
  `account-moved`, plus `delete-company-member`); `DeleteCompanySection` (`app-delete-company`; `delete-company`, `delete-company-section`,
  `#company-name`, `#company-password`, `delete-company-confirm`, `delete-company-cancel`, `delete-company-error`);
  `AuthService.deleteCompany(name, confirmation: { password: string } | { provider_ticket: string })`; `reauthTicketOf(router)` in
  `pages/ticket-history.ts` (read by `SettingsPage` and `TeamPage`, passed to the sections as their `ticket` input); `texts.team` delete texts,
  `texts.login.companyDeleted`; notes-api `DELETE /api/org`.

**Contract counts:** none.

**Decisions the plan takes here:**
- The deletion of spec 0010/0011 moves out of `NotesPage` into a component of its own (`DeleteAccountSection`), drawn by `SettingsPage`,
  with the same template, test ids, texts and order of calls; what it did to the notes list (emptying it, reading the list again) is gone with
  the list. Its specs move with it: `notes.account.spec.ts`'s describe `NotesPage: "Delete account" (spec 0010)` becomes
  `delete-account.spec.ts`, `notes.provider.spec.ts` becomes `delete-account.provider.spec.ts`, both opening `/settings`; the tests that
  asserted the notes list are changed by name below.
- "Delete company" is a component on `/team` (`DeleteCompanySection`), shown to a holder of `org:delete` whether or not they manage members
  (the notes header shows "Team" to such a person too). With a password it calls `POST /auth/account/confirm` before any data goes; with a
  provider it is armed `delete_company` and comes back to `/team` with the ticket in the navigation state. Then `DELETE /api/org`, and only
  after its `204` `DELETE /auth/org`; then it signs out to `/login` with "The company was deleted."
- The ticket that `/provider-return` hands on is read by the routed page (`SettingsPage`, `TeamPage`), in a field initializer, while the navigation
  is current, and passed down as an input: both sections are created later (the company section only once the page is ready), when
  `currentNavigation()` is null, so a section that read it itself would never find its ticket. The page also takes it out of the history entry.
- `403 permissions_changed` from `DELETE /auth/org` (after the interceptor's refresh and second try) signs the person out to `/login`, as the
  company is not the one the screen thought: the other company screens read the person again; here the data is already gone.

- [ ] **Step 1: notes-api.** In `samples/notes-api/src/notes_api/app.py`, replace

```python
        with Session(engine) as session:
            session.execute(delete(Note).where(Note.org_id == org_id))  # every note of the token's space, and no other
            session.commit()
        return Response(status_code=204)

    return app
```

with

```python
        with Session(engine) as session:
            session.execute(delete(Note).where(Note.org_id == org_id))  # every note of the token's space, and no other
            session.commit()
        return Response(status_code=204)

    @app.delete("/api/org", status_code=204)
    def delete_company_data(user: Principal = Depends(auth.current_user)):
        """The sample's "delete the company's data" (Auth-Core spec 0012): the company's notes go before Auth-Core deletes the company.

        For a company member whose token holds org:delete; a private account and any other member are refused. Any such holder wipes the
        company's notes before Auth-Core has judged the confirmation (the threat model's risk 51): the web sample confirms the password first.
        """
        if user.org_kind != "organization" or "org:delete" not in user.permissions:
            return _error(403, "forbidden")
        org_id = _org_id(user)
        with Session(engine) as session:
            session.execute(delete(Note).where(Note.org_id == org_id))  # every note of the token's company, and no other
            session.commit()
        return Response(status_code=204)

    return app
```

`samples/notes-api/tests/test_delete_org_data.py` (LF):

```python
"""Spec 0012: DELETE /api/org, the sample's "delete the company's data" (criterion 29)."""

import uuid

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from conftest import COMPANY_A, COMPANY_B, headers
from notes_api.db import Note

DELETER = ("notes:read", "notes:write", "org:delete")


def add(client, signer, text, org_id, org_kind=None):
    response = client.post("/api/notes", json={"text": text}, headers=headers(signer, org_id, org_kind=org_kind))
    assert response.status_code == 201


def count(engine, org_id) -> int:
    with Session(engine) as session:
        return session.scalar(select(func.count()).select_from(Note).where(Note.org_id == uuid.UUID(org_id)))


def test_a_holder_of_org_delete_deletes_the_notes_of_the_company_and_no_other(client, signer, engine):  # criterion 29
    add(client, signer, "ours", COMPANY_A)
    add(client, signer, "ours too", COMPANY_A)
    add(client, signer, "theirs", COMPANY_B)

    response = client.delete("/api/org", headers=headers(signer, COMPANY_A, permissions=DELETER, org_kind="organization"))

    assert response.status_code == 204
    assert response.content == b""
    assert response.headers["cache-control"] == "no-store"
    assert count(engine, COMPANY_A) == 0
    assert count(engine, COMPANY_B) == 1


def test_a_token_without_org_kind_is_an_organization_and_may_delete(client, signer, engine):  # the package's default kind
    add(client, signer, "ours", COMPANY_A)

    response = client.delete("/api/org", headers=headers(signer, COMPANY_A, permissions=DELETER))

    assert response.status_code == 204
    assert count(engine, COMPANY_A) == 0


def test_a_member_without_org_delete_is_403_and_nothing_is_deleted(client, signer, engine):  # criterion 29
    add(client, signer, "ours", COMPANY_A)

    response = client.delete("/api/org", headers=headers(signer, COMPANY_A, org_kind="organization"))

    assert response.status_code == 403
    assert response.json() == {"error": "forbidden"}
    assert response.headers["cache-control"] == "no-store"
    assert count(engine, COMPANY_A) == 1


def test_a_private_token_is_403_even_with_org_delete(client, signer, engine):  # criterion 29
    add(client, signer, "mine", COMPANY_A, "personal")

    response = client.delete("/api/org", headers=headers(signer, COMPANY_A, permissions=DELETER, org_kind="personal"))

    assert response.status_code == 403
    assert count(engine, COMPANY_A) == 1


def test_without_a_token_it_is_401(client):  # criterion 29
    assert client.delete("/api/org").status_code == 401


def test_it_is_204_when_there_are_no_notes_and_again_after(client, signer):  # criterion 29
    first = client.delete("/api/org", headers=headers(signer, COMPANY_A, permissions=DELETER))
    second = client.delete("/api/org", headers=headers(signer, COMPANY_A, permissions=DELETER))

    assert (first.status_code, second.status_code) == (204, 204)
```

Run from `samples/notes-api`: `C:/p6v/Scripts/python.exe -m pytest -q` passes.

- [ ] **Step 2: The calls and the texts.** In `samples/notes-web/src/app/auth/auth.service.ts`, add after `deleteAccountWithTicket`:

```ts

  /**
   * Spec 0012: DELETE /auth/org with the company's name and the caller's confirmation (the password, or the ticket of a `reauth` round trip).
   * On `204` the session is over here too: the caller's account went with the company. The product deletes the company's data before it
   * calls this.
   */
  async deleteCompany(name: string, confirmation: { password: string } | { provider_ticket: string }): Promise<LoginResult> {
    try {
      await firstValueFrom(this.http.delete('/auth/org', { body: { name, ...confirmation } }));
    } catch (error) {
      return { ok: false, failure: companyFailureOf(error) };
    }
    this.dropSession();
    return { ok: true };
  }
```

and add after the function `formFailureOf`:

```ts

/**
 * The answers of DELETE /auth/org (spec 0008, spec 0012): those of the forms (a name that is not the company's is `400 invalid_request`), and
 * the two refusals of the company API that the "Delete company" section names.
 */
function companyFailureOf(error: unknown): Failure {
  if (error instanceof HttpErrorResponse && error.status === 403) {
    const code = errorCode(error);
    if (code === 'permission_not_held' || code === 'permissions_changed') {
      return { kind: code };
    }
  }
  return formFailureOf(error);
}
```

and to `Failure` add `  | { kind: 'permission_not_held' }` and `  | { kind: 'permissions_changed' }` (after `{ kind: 'not_found' }`). `failureOf`
itself is unchanged, so no other screen reads these answers differently.

In `samples/notes-web/src/app/texts.ts`:

(a) replace `    sessionEnded: 'Your session ended. Sign in again.',` with

```ts
    sessionEnded: 'Your session ended. Sign in again.',
    companyDeleted: 'The company was deleted.',
```

(b) in `team`, replace `    notAllowed: "You don't have access to this.",` with

```ts
    notAllowed: "You don't have access to this.",
    // Spec 0012: "Delete company", for a holder of org:delete.
    deleteCompany: 'Delete company',
    deleteCompanyWarning: "This deletes the company, every member's account and all the company's notes. It cannot be undone.",
    companyName: "Type the company's name",
    nameMismatch: "Type the company's name exactly.",
    permissionNotHeldDelete: 'A member has permissions you do not have, so you cannot delete the company.',
    wrongPassword: 'The password is wrong.',
    reauthFailed:
      'The confirmation did not work or took too long, so the company was not deleted. Its notes are already gone. Confirm again.',
    notesGone: "The company's notes are already gone.",
```

(c) in `settings`, add `    companyMember: "A company account is deleted by the company's admins.",` after `    noMethod: …,`.

In `pages/login.ts`, in `startNotice`, replace `  return notice === 'session' ? texts.login.sessionEnded : null;` with

```ts
  if (notice === 'company_deleted') {
    return texts.login.companyDeleted;
  }
  return notice === 'session' ? texts.login.sessionEnded : null;
```

- [ ] **Step 3: "Delete account" moves.** `pages/delete-account.ts` — the section of the notes screen as a component. Its code is the notes
screen's deletion, moved; the differences are marked in the comments:

```ts
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, Injector, OnInit, afterNextRender, inject, input, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService, LoginResult, errorCode } from '../auth/auth.service';
import { texts } from '../texts';
import { acceptablePassword } from './password-input';
import { ProviderButtons } from './provider-buttons';

function isAuthUnavailable(error: unknown): boolean {
  return error instanceof HttpErrorResponse && error.status === 503 && errorCode(error) === 'auth_unavailable';
}

/**
 * "Delete account" (spec 0010; spec 0011 for an account without a password; spec 0012: on the settings screen). The product's notes go first
 * (DELETE /api/me), then the account (DELETE /auth/account), with the password or a provider ticket. A company account is deleted by the
 * company's admins: the section says so and has no button.
 */
@Component({
  selector: 'app-delete-account',
  imports: [ReactiveFormsModule, ProviderButtons],
  template: `
    <section data-testid="delete-account-section">
      <h2>{{ t.deleteAccount }}</h2>
      @if (me()?.org_kind === 'organization') {
        @if (moved()) {
          <p class="status" role="status" data-testid="account-moved">{{ t.movedToCompany }}</p>
        }
        <p data-testid="delete-company-member">{{ settings.companyMember }}</p>
      } @else if (me() !== null) {
        @if (!deleting()) {
          <button type="button" data-testid="delete-account" (click)="askToDelete()">{{ t.deleteAccount }}</button>
        } @else {
          <div data-testid="delete-section">
            <p class="error" data-testid="delete-warning">{{ t.deleteWarning }}</p>
            @if (me()?.has_password !== false) {
              <form [formGroup]="deleteForm" (ngSubmit)="deleteAccount()" novalidate data-testid="delete-form">
                <label for="delete-password">{{ password }}</label>
                <input id="delete-password" type="password" formControlName="password" autocomplete="current-password" />
                <button type="submit" data-testid="delete-confirm" [disabled]="deleteBusy()">{{ t.deleteConfirm }}</button>
                <button type="button" data-testid="delete-cancel" [disabled]="deleteBusy()" (click)="cancelDelete()">{{ t.cancel }}</button>
              </form>
            } @else if (reauthTicket() === null) {
              <app-provider-buttons
                [providers]="me()?.linked_providers ?? []"
                action="confirm"
                [reauth]="true"
                target="delete_account"
                [separator]="false"
              />
              <button type="button" data-testid="delete-cancel" [disabled]="deleteBusy()" (click)="cancelDelete()">{{ t.cancel }}</button>
            } @else {
              <button type="button" data-testid="delete-confirm" [disabled]="deleteBusy()" (click)="deleteWithTicket()">{{ t.deleteConfirm }}</button>
              <button type="button" data-testid="delete-cancel" [disabled]="deleteBusy()" (click)="cancelDelete()">{{ t.cancel }}</button>
            }
            @if (deleteError(); as text) {
              <p class="error" role="alert" data-testid="delete-error">{{ text }}</p>
            }
          </div>
        }
      }
    </section>
  `,
})
export class DeleteAccountSection implements OnInit {
  protected readonly t = texts.notes;
  protected readonly settings = texts.settings;
  protected readonly password = texts.common.password;
  private readonly somethingWrong = texts.common.somethingWrong;
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly me = this.auth.me;
  protected readonly deleteForm = inject(NonNullableFormBuilder).group({ password: ['', Validators.required] });
  protected readonly deleting = signal(false);
  protected readonly deleteBusy = signal(false);
  protected readonly deleteError = signal<string | null>(null);
  protected readonly moved = signal(false);
  /**
   * The ticket of "Confirm with {Provider}" that /provider-return handed on, read by the routed page (`SettingsPage`) while the navigation that
   * brought it is current, and passed down: this section is created later, when `currentNavigation()` is already null. Memory only; the page
   * also takes it out of the history entry.
   */
  readonly handedTicket = input<string | null>(null, { alias: 'ticket' });
  protected readonly reauthTicket = signal<string | null>(null);

  ngOnInit(): void {
    const handed = this.handedTicket();
    if (handed !== null) {
      this.reauthTicket.set(handed);
      this.deleting.set(true);
      this.focusDeleteSection();
    }
  }

  protected askToDelete(): void {
    if (this.deleteBusy()) {
      return;
    }
    this.deleteForm.reset();
    this.deleteError.set(null);
    this.moved.set(false);
    this.deleting.set(true);
    this.focusDeleteSection();
  }

  /** The person keeps the account (the notes that were deleted stay deleted). */
  protected cancelDelete(): void {
    this.deleting.set(false);
    this.reauthTicket.set(null);
  }

  protected async deleteWithTicket(): Promise<void> {
    const ticket = this.reauthTicket();
    if (this.deleteBusy() || ticket === null) {
      return;
    }
    if (!(await this.deleteNotes())) {
      return;
    }
    const result = await this.auth.deleteAccountWithTicket(ticket);
    if (result.ok) {
      await this.leave();
      return;
    }
    if (result.failure.kind === 'reauth_failed') {
      this.reauthTicket.set(null);
      this.deleteError.set(this.t.reauthFailed);
      this.deleteBusy.set(false);
      return;
    }
    await this.refused(result);
  }

  protected async deleteAccount(): Promise<void> {
    const password = this.deleteForm.controls.password.value;
    if (this.deleteBusy()) {
      return;
    }
    if (!acceptablePassword(password)) {
      this.deleteError.set(this.t.passwordRequired);
      return;
    }
    if (!(await this.deleteNotes())) {
      return;
    }
    const result = await this.auth.deleteAccount(password);
    if (result.ok) {
      await this.leave();
      return;
    }
    await this.refused(result);
  }

  /** DELETE /api/me, the first half of every deletion (spec 0010, Decision 8). True when the notes are gone. */
  private async deleteNotes(): Promise<boolean> {
    this.deleteBusy.set(true);
    this.deleteError.set(null);
    try {
      await firstValueFrom(this.http.delete('/api/me'));
    } catch (error) {
      this.deleteError.set(isAuthUnavailable(error) ? null : this.somethingWrong);
      this.deleteBusy.set(false);
      return false;
    }
    return true;
  }

  private async leave(): Promise<void> {
    try {
      await this.router.navigateByUrl('/login', { state: { notice: 'deleted' } });
    } catch {
      // nothing to show: the account is gone and so is the session
    }
  }

  private async refused(result: LoginResult & { ok: false }): Promise<void> {
    if (result.failure.kind === 'password_required' || result.failure.kind === 'reauth_required') {
      this.reauthTicket.set(null);
      this.deleteForm.reset();
      await this.auth.loadMe();
      this.deleteError.set(this.t.notesGone);
    } else if (result.failure.kind === 'wrong_password') {
      this.deleteError.set(this.t.wrongPassword);
    } else if (result.failure.kind === 'too_many_attempts') {
      this.deleteError.set(`${texts.login.tooManyAttempts(result.failure.retryAfterSeconds)} ${this.t.notesGone}`);
    } else if (result.failure.kind === 'too_many_requests') {
      this.deleteError.set(this.t.notesGone);
    } else if (await this.leftThePrivateSpace()) {
      // the section now says that a company account is deleted by its admins
    } else {
      this.deleteError.set(`${this.somethingWrong} ${this.t.notesGone}`);
    }
    this.deleteBusy.set(false);
  }

  /** True when the person turns out to belong to a company (an invitation was accepted meanwhile). */
  private async leftThePrivateSpace(): Promise<boolean> {
    await this.auth.loadMe();
    const person = this.me();
    if (person === null || person.org_kind === 'personal') {
      return false;
    }
    this.deleting.set(false);
    this.deleteError.set(null);
    this.moved.set(true);
    return true;
  }

  private focusDeleteSection(): void {
    afterNextRender(
      () => {
        const root = this.host.nativeElement;
        const target =
          root.querySelector<HTMLElement>('#delete-password') ??
          root.querySelector<HTMLElement>('[data-testid="delete-section"] a') ??
          root.querySelector<HTMLElement>('[data-testid="delete-confirm"]');
        target?.focus();
      },
      { injector: this.injector },
    );
  }
}
```

(An account that turned out to belong to a company meanwhile shows the sentence of spec 0010 (`account-moved`) and the company member's
sentence, and no form.)

In `pages/ticket-history.ts` (it imports `Router` already), add at the end:

```ts

/**
 * The ticket of a confirmation that /provider-return handed on in the navigation state (spec 0011; "Delete account" and "Delete company"), or
 * null. Only in memory: never in a URL or in storage. It is read by the routed page, in a field initializer or its constructor, while the
 * navigation that brought it is current; a component created later by the page's template finds no navigation, so the page passes it down.
 */
export function reauthTicketOf(router: Router): string | null {
  const ticket: unknown = router.currentNavigation()?.extras.state?.['reauthTicket'];
  return typeof ticket === 'string' && ticket !== '' ? ticket : null;
}
```

In `pages/settings.ts`: add `DeleteAccountSection` (from `'./delete-account'`) to `imports`; replace
`import { keepTicketOutOfHistory } from './ticket-history';` with `import { keepTicketOutOfHistory, reauthTicketOf } from './ticket-history';`; after
`  private readonly fromReturn = handedOn(this.router);` add

```ts
  /** The ticket of "Confirm with {Provider}" for "Delete account", read now and handed to the section (it is created later). */
  protected readonly reauthTicket = reauthTicketOf(this.router);
```

in `ngOnInit` replace

```ts
    if (this.fromReturn.pending !== null) {
      // The ticket lives in this component's memory only: the history entry is replaced without it.
      void keepTicketOutOfHistory(this.router);
```

with

```ts
    if (this.fromReturn.pending !== null || this.reauthTicket !== null) {
      // A ticket lives in memory only: the history entry is replaced without it.
      void keepTicketOutOfHistory(this.router);
    }
    if (this.fromReturn.pending !== null) {
```

and, in the template, after the closing `</section>` of `password-section`, add `        <app-delete-account [ticket]="reauthTicket" />`.

In `pages/notes.ts`, remove what moved:
- the template block from `          @if (person.org_kind === 'personal') {\n            <button type="button" data-testid="delete-account"` to its
  closing `}`, the block `      @if (moved()) { … }`, and the block `      @if (deleting() && me()?.org_kind === 'personal') { … }` (the whole
  delete section);
- the conditions on `notesGone()` in the template: `@if (canWrite() && !notesGone())` becomes `@if (canWrite())`;
- the members `deleteForm`, `deleting`, `deleteBusy`, `deleteError`, `notesGone`, `moved`, `reauthTicket`, and the methods `askToDelete`,
  `cancelDelete`, `deleteWithTicket`, `deleteAccount`, `deleteNotes`, `leave`, `refused`, `leftThePrivateSpace`, `focusDeleteSection`;
- in `ngOnInit`, the `if (this.reauthTicket() !== null) { … }` block; in `load`, the two `if (this.notesGone()) { return; }` guards;
- the function `reauthTicketOf`, and the imports nothing uses any more: `acceptablePassword`, `ProviderButtons`, `keepTicketOutOfHistory`,
  `LoginResult`, `Validators`, `ElementRef`, `Injector`, `afterNextRender` (keep any that the remaining code still uses).

In `pages/provider-return.ts`, replace

```ts
      if (armed === 'delete_account') {
        // The notes screen opens "Delete account" ready to confirm: the ticket is spent there, not here.
        await this.go(DEFAULT_RETURN_URL, { reauthTicket: this.ticket });
```

with

```ts
      if (armed === 'delete_account') {
        // The settings screen opens "Delete account" ready to confirm: the ticket is spent there, not here.
        await this.go('/settings', { reauthTicket: this.ticket });
```

(`DEFAULT_RETURN_URL` stays imported: the exchange's sign-in still goes there.)

- [ ] **Step 4: "Delete company".** `pages/delete-company.ts`:

```ts
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService, Failure, errorCode } from '../auth/auth.service';
import { texts } from '../texts';
import { acceptablePassword } from './password-input';
import { ProviderButtons } from './provider-buttons';

/**
 * Spec 0012: "Delete company" on /team, for a holder of org:delete. The name is typed again and compared with the company's name first; then
 * the password, which is confirmed with POST /auth/account/confirm before any data goes, or "Confirm with {Provider}" for an admin without one.
 * Then the product's data (DELETE /api/org), and only after its 204 the company (DELETE /auth/org); then the person is signed out.
 */
@Component({
  selector: 'app-delete-company',
  imports: [ReactiveFormsModule, ProviderButtons],
  template: `
    <section data-testid="delete-company-section">
      <h2>{{ t.deleteCompany }}</h2>
      @if (!open()) {
        <button type="button" data-testid="delete-company" (click)="start()">{{ t.deleteCompany }}</button>
      } @else {
        <p class="error">{{ t.deleteCompanyWarning }}</p>
        <form [formGroup]="form" (ngSubmit)="submit()" novalidate data-testid="delete-company-form">
          <label for="company-name">{{ t.companyName }}</label>
          <input id="company-name" type="text" formControlName="name" autocomplete="off" />
          @if (me()?.has_password !== false) {
            <label for="company-password">{{ password }}</label>
            <input id="company-password" type="password" formControlName="password" autocomplete="current-password" />
            <button type="submit" data-testid="delete-company-confirm" [disabled]="busy()">{{ t.deleteCompany }}</button>
          } @else if (ticket() !== null) {
            <button type="submit" data-testid="delete-company-confirm" [disabled]="busy()">{{ t.deleteCompany }}</button>
          }
        </form>
        @if (me()?.has_password === false && ticket() === null) {
          <app-provider-buttons
            [providers]="me()?.linked_providers ?? []"
            action="confirm"
            [reauth]="true"
            target="delete_company"
            [separator]="false"
          />
        }
        <button type="button" data-testid="delete-company-cancel" [disabled]="busy()" (click)="cancel()">{{ t.cancel }}</button>
        @if (error(); as text) {
          <p class="error" role="alert" data-testid="delete-company-error">{{ text }}</p>
        }
      }
    </section>
  `,
})
export class DeleteCompanySection implements OnInit {
  protected readonly t = texts.team;
  protected readonly password = texts.common.password;
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly me = this.auth.me;
  /**
   * The ticket of "Confirm with {Provider}", handed back by /provider-return in the navigation state: read by the routed page (`TeamPage`) while
   * that navigation is current and passed down, because this section is created later (after the page has read the members), when
   * `currentNavigation()` is null. Memory only; the page also takes it out of the history entry.
   */
  readonly handedTicket = input<string | null>(null, { alias: 'ticket' });
  protected readonly ticket = signal<string | null>(null);
  protected readonly open = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = inject(NonNullableFormBuilder).group({ name: [''], password: [''] });
  protected readonly companyName = computed(() => this.me()?.org_name ?? null);

  ngOnInit(): void {
    const handed = this.handedTicket();
    if (handed !== null) {
      this.ticket.set(handed);
      this.open.set(true);
    }
  }

  protected start(): void {
    this.form.reset();
    this.error.set(null);
    this.open.set(true);
  }

  protected cancel(): void {
    this.open.set(false);
    this.ticket.set(null);
    this.error.set(null);
  }

  protected async submit(): Promise<void> {
    if (this.busy()) {
      return;
    }
    const { name, password } = this.form.getRawValue();
    this.error.set(null);
    // The name first, exactly: nothing is sent for a mismatch.
    if (name !== this.companyName()) {
      this.error.set(this.t.nameMismatch);
      return;
    }
    const ticket = this.ticket();
    const withPassword = this.me()?.has_password !== false;
    if (withPassword && !acceptablePassword(password)) {
      this.error.set(texts.notes.passwordRequired);
      return;
    }
    this.busy.set(true);
    try {
      if (withPassword) {
        // A wrong password is caught before any data goes (spec 0012, risk 51).
        const confirmed = await this.auth.confirmPassword(password);
        if (!confirmed.ok) {
          await this.refused(confirmed.failure, false);
          return;
        }
      } else if (ticket === null) {
        return;
      }
      if (!(await this.deleteData())) {
        return;
      }
      const result = await this.auth.deleteCompany(name, withPassword ? { password } : { provider_ticket: ticket! });
      if (result.ok) {
        await this.router.navigateByUrl('/login', { state: { notice: 'company_deleted' } }).catch(() => false);
        return;
      }
      await this.refused(result.failure, true);
    } finally {
      this.busy.set(false);
    }
  }

  /** DELETE /api/org. True when the company's notes are gone. */
  private async deleteData(): Promise<boolean> {
    try {
      await firstValueFrom(this.http.delete('/api/org'));
      return true;
    } catch (error) {
      const unavailable = error instanceof HttpErrorResponse && error.status === 503 && errorCode(error) === 'auth_unavailable';
      this.error.set(unavailable ? null : texts.common.somethingWrong);
      return false;
    }
  }

  /** The answers of the confirmation and of DELETE /auth/org; `gone` once the company's notes are deleted. */
  private async refused(failure: Failure, gone: boolean): Promise<void> {
    const after = gone ? ` ${this.t.notesGone}` : '';
    switch (failure.kind) {
      case 'invalid_request':
        this.error.set(this.t.nameMismatch + after);
        break;
      case 'permission_not_held':
        this.error.set(this.t.permissionNotHeldDelete + after);
        break;
      case 'permissions_changed':
        // The company is not the one this screen thought, after the interceptor's refresh and second try: sign out.
        await this.auth.logout();
        await this.router.navigateByUrl('/login').catch(() => false);
        break;
      case 'wrong_password':
        this.error.set(this.t.wrongPassword + after);
        break;
      case 'too_many_attempts':
        this.error.set(texts.login.tooManyAttempts(failure.retryAfterSeconds) + after);
        break;
      case 'reauth_failed':
        this.ticket.set(null);
        this.error.set(this.t.reauthFailed);
        break;
      case 'password_required':
      case 'reauth_required':
        this.ticket.set(null);
        await this.auth.loadMe();
        this.error.set(gone ? this.t.notesGone : null);
        break;
      case 'too_many_requests':
      case 'session_required':
        if (gone) {
          this.error.set(this.t.notesGone);
        }
        break;
      default:
        this.error.set(texts.common.somethingWrong + after);
    }
  }
}
```

In `pages/team.ts`: change `import { RouterLink } from '@angular/router';` to `import { Router, RouterLink } from '@angular/router';`, add
`import { keepTicketOutOfHistory, reauthTicketOf } from './ticket-history';`, add `DeleteCompanySection` (from `'./delete-company'`) to `imports`;
add after `private readonly injector = inject(Injector);` the two members

```ts
  private readonly router = inject(Router);
  /** The ticket of "Confirm with {Provider}" for "Delete company", read now, while the navigation that brought it is current, and handed to the section. */
  protected readonly reauthTicket = reauthTicketOf(this.router);
```

replace

```ts
  ngOnInit(): Promise<void> {
    return this.start();
  }
```

with

```ts
  ngOnInit(): Promise<void> {
    if (this.reauthTicket !== null) {
      void keepTicketOutOfHistory(this.router);   // the ticket lives in memory only: the history entry is replaced without it
    }
    return this.start();
  }
```

add the member
`  protected readonly canDeleteCompany = computed(() => (this.me()?.permissions.includes('org:delete') ?? false) && this.me()?.org_kind === 'organization');`;
replace

```html
      } @else if (!canManage()) {
        <p class="error" role="alert" data-testid="team-forbidden">{{ t.notAllowed }}</p>
      } @else {
```

with

```html
      } @else if (!canManage()) {
        @if (!canDeleteCompany()) {
          <p class="error" role="alert" data-testid="team-forbidden">{{ t.notAllowed }}</p>
        }
      } @else {
```

and replace

```html
      <p><a routerLink="/notes">{{ t.back }}</a></p>
    </main>
```

with

```html
      @if (ready() && canDeleteCompany()) {
        <app-delete-company [ticket]="reauthTicket" />
      }
      <p><a routerLink="/notes">{{ t.back }}</a></p>
    </main>
```

In `pages/notes.ts`, replace

```ts
  protected readonly canManageMembers = computed(() => this.me()?.permissions.includes('members:manage') ?? false);
```

with

```ts
  protected readonly canManageMembers = computed(() => this.me()?.permissions.includes('members:manage') ?? false);
  /** Spec 0012: "Delete company" is on the team screen too. */
  private readonly canDeleteCompany = computed(() => this.me()?.permissions.includes('org:delete') ?? false);
  protected readonly canOpenTeam = computed(() => this.canManageMembers() || this.canDeleteCompany());
```

and in the template `@if (canManageMembers()) {` (around the team link) with `@if (canOpenTeam()) {`.

- [ ] **Step 5: The specs.**

(a) `pages/delete-account.spec.ts`: move the describe `NotesPage: "Delete account" (spec 0010)` of `notes.account.spec.ts` here, renamed
`DeleteAccountSection on /settings (spec 0010; spec 0012)`, with the file's helpers (`status`, `warning`, `gone`, `wrongPassword`, `askToDelete`,
`refusedByAuthCore`) and an `open(me)` that opens `SettingsPage` instead of `NotesPage`:

```ts
async function open(me: Me) {
  TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(SettingsPage);
  fixture.detectChanges();
  ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'self-service', providers: ['google'] });
  ctrl.expectOne('/auth/me').flush(me);
  await settle(fixture);
  return { fixture, ctrl, auth, navigateByUrl };
}
```

The moved tests keep their names and assertions, except these, which asserted the notes list (the list is not on `/settings`):
`a failure of the notes service stops there: Auth-Core is not called, and the notes are still shown` becomes
`a failure of the notes service stops there: Auth-Core is not called` (drop the assertion of the shown note);
`once the notes are gone the page shows no notes and takes no new one` is removed (the notes screen deletes nothing now);
`"Cancel" closes the form, keeps the account and takes notes again` becomes `"Cancel" closes the form and keeps the account` (drop the note
form assertion); `an account that moved into a company meanwhile: the form is reset, one sentence says so, and the notes of the company are read`
becomes `an account that moved into a company meanwhile: the form is reset, one sentence says so, and the section says a company account is
deleted by its admins` (assert `account-moved`, `[data-testid="delete-company-member"]` holding `A company account is deleted by the company's
admins.` and no `delete-form`, and drop the `/api/notes` expectation). Add:

```ts
  it('a company member sees the sentence and no button (spec 0012)', async () => {
    const { fixture } = await open(adminMe);
    expect(pageText(fixture)).toContain("A company account is deleted by the company's admins.");
    expect(has(fixture, '[data-testid="delete-account"]')).toBe(false);
  });
```

(b) `pages/delete-account.provider.spec.ts`: the whole of `notes.provider.spec.ts`, with its `open(me, reauthTicket)` navigating to
`/settings` (route `{ path: 'settings', component: SettingsPage }` in its `configure()`, in place of `notes`) and flushing `/auth/options` and
`/auth/me` in place of `/api/notes`; its describe renamed `DeleteAccountSection without a password (spec 0011; spec 0012: on /settings)`. Its
`open` keeps the real router (`RouterTestingHarness`, `router.navigateByUrl('/settings', { state: { reauthTicket } })`, as the file does today): the
section is a child of `SettingsPage` now and finds its ticket only because the page reads it while that navigation is current and passes it
down, which these tests keep true (no spec of this slice mocks `currentNavigation` for a section). Then
delete `notes.provider.spec.ts`. Its tests keep their names; the one whose name says "a reload of the entry does not open the section again"
navigates to `/settings` again.

(c) In `pages/notes.account.spec.ts`: delete the moved describe; in `NotesPage: the account bar (spec 0010)` replace the test
`a private account sees its address, "Private account" and "Delete account", and no company, role or Team` with
`a private account sees its address, "Private account" and "Settings", and no "Delete account", company, role or Team (spec 0012)`, asserting
`has(fixture, '[data-testid="settings-link"]')` true and `has(fixture, '[data-testid="delete-account"]')` false; and add:

```ts
  it('a holder of org:delete sees "Team" without members:manage (spec 0012)', async () => {
    const { fixture } = await open({ ...adminMe, permissions: [...adminMe.permissions, 'org:delete'] });
    expect(has(fixture, '[data-testid="team-link"]')).toBe(true);
  });
```

(d) In `pages/provider-return.spec.ts`: add `{ path: 'settings', component: Landing }` to the routes; in the tests
`reauth after "Confirm with ...": the notes screen with the ticket in the navigation state, and no call` (rename it
`reauth after "Confirm with ..." for a deletion: the settings screen with the ticket in the navigation state, and no call`),
`the flag is one-shot: …` and `a flag just inside 10 minutes still hands the ticket on`, replace `expect(opened.router.url).toBe('/notes');`
and `expect(first.router.url).toBe('/notes');` with `/settings`.

(e) `pages/delete-company.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { AuthService, Me } from '../auth/auth.service';
import { adminMe, clickOn, has, pageText, settle, signedInAs, submitForm, typeInto } from '../../testing/helpers';
import { takeReauth } from './provider-buttons';
import { TeamPage } from './team';

const status = (code: number) => ({ status: code, statusText: String(code) });
const deleter: Me = { ...adminMe, permissions: [...adminMe.permissions, 'org:delete'] };
const deleterWithoutPassword: Me = { ...deleter, has_password: false, linked_providers: ['github'] };
const TICKET = 'TICKET-abcdefghijklmnopqrstuvwxyz-0123456789A';

/**
 * Opens /team through a real navigation, with the state that /provider-return hands on: the page reads the ticket while that navigation is
 * current and passes it to the section, which is created later, when the page is ready and the navigation is over. (A mocked
 * `currentNavigation` for the whole run would hide a section that reads it too late.)
 */
async function open(me: Me, state: Record<string, unknown> | null = null) {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'team', component: TeamPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const router = TestBed.inject(Router);
  const harness = await RouterTestingHarness.create();
  await router.navigateByUrl('/team', state === null ? undefined : { state });
  const fixture = harness.fixture;
  await settle(fixture);
  const navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);   // from here on, the way out to /login goes nowhere
  return { fixture, ctrl, auth, navigateByUrl };
}

type Opened = Awaited<ReturnType<typeof open>>;

async function deleteWith(opened: Opened, name: string, password?: string): Promise<void> {
  if (!has(opened.fixture, '#company-name')) {
    clickOn(opened.fixture, '[data-testid="delete-company"]');
    opened.fixture.detectChanges();
  }
  typeInto(opened.fixture, '#company-name', name);
  if (password !== undefined) {
    typeInto(opened.fixture, '#company-password', password);
  }
  submitForm(opened.fixture, 'form[data-testid="delete-company-form"]');
  await settle(opened.fixture);
}

describe('DeleteCompanySection on /team (spec 0012)', () => {
  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    sessionStorage.clear();
  });

  it('is shown to a holder of org:delete and to nobody else', async () => {
    const shown = await open(deleter);
    expect(has(shown.fixture, '[data-testid="delete-company"]')).toBe(true);
    TestBed.resetTestingModule();
    const hidden = await open(adminMe);
    expect(has(hidden.fixture, '[data-testid="delete-company"]')).toBe(false);
  });

  it('a name that is not the company\'s exactly: the sentence, and nothing is sent', async () => {
    const opened = await open(deleter);
    await deleteWith(opened, 'acme', 'Another-Passw0rd');
    opened.ctrl.expectNone('/auth/account/confirm');
    opened.ctrl.expectNone('/api/org');
    expect(pageText(opened.fixture)).toContain("Type the company's name exactly.");
  });

  it('with a password: confirm first, then DELETE /api/org, then DELETE /auth/org, then /login saying so', async () => {
    const opened = await open(deleter);
    await deleteWith(opened, 'Acme', 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(deleter);
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/org').flush(null, status(204));
    await settle(opened.fixture);
    const request = opened.ctrl.expectOne('/auth/org');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.body).toEqual({ name: 'Acme', password: 'Another-Passw0rd' });
    request.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/login', { state: { notice: 'company_deleted' } });
    expect(opened.auth.token()).toBeNull();
  });

  it('a wrong password at the confirmation: nothing is deleted', async () => {
    const opened = await open(deleter);
    await deleteWith(opened, 'Acme', 'Wrong-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush({ error: 'wrong_password' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectNone('/api/org');
    expect(pageText(opened.fixture)).toContain('The password is wrong.');
  });

  it('a failed DELETE /api/org sends nothing to Auth-Core', async () => {
    const opened = await open(deleter);
    await deleteWith(opened, 'Acme', 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(deleter);
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/org').flush('oops', status(500));
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/org');
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
  });

  it.each([
    [{ error: 'invalid_request' }, 400, "Type the company's name exactly. The company's notes are already gone."],
    [{ error: 'permission_not_held' }, 403, "A member has permissions you do not have, so you cannot delete the company. The company's notes are already gone."],
    [{ error: 'too_many_attempts', retry_after_seconds: 60 }, 429, "Too many attempts. Try again in 1 minute. The company's notes are already gone."],
  ])('DELETE /auth/org answered %j: its sentence', async (answer, code, sentence) => {
    const opened = await open(deleter);
    await deleteWith(opened, 'Acme', 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(deleter);
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/org').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/org').flush(answer, status(code));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(sentence);
    expect(opened.auth.token()).toBe('tok');
  });

  it('without a password: "Confirm with GitHub", armed for the team screen, and no password field', async () => {
    const opened = await open(deleterWithoutPassword);
    clickOn(opened.fixture, '[data-testid="delete-company"]');
    opened.fixture.detectChanges();
    expect(has(opened.fixture, '#company-password')).toBe(false);
    const link = (opened.fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="provider-github"]')!;
    expect(link.getAttribute('href')).toBe('/auth/providers/github/start?purpose=reauth');
    link.addEventListener('click', (event) => event.preventDefault());
    link.click();
    expect(takeReauth()).toBe('delete_company');
  });

  it('back with the ticket: the name, then DELETE /api/org and DELETE /auth/org with the ticket, and no confirmation call', async () => {
    const opened = await open(deleterWithoutPassword, { reauthTicket: TICKET });
    await deleteWith(opened, 'Acme');
    opened.ctrl.expectNone('/auth/account/confirm');
    opened.ctrl.expectOne('/api/org').flush(null, status(204));
    await settle(opened.fixture);
    const request = opened.ctrl.expectOne('/auth/org');
    expect(request.request.body).toEqual({ name: 'Acme', provider_ticket: TICKET });
    request.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/login', { state: { notice: 'company_deleted' } });
    expect(JSON.stringify({ ...sessionStorage })).not.toContain(TICKET);
  });

  it('403 reauth_failed: the sentence, and the confirm button again', async () => {
    const opened = await open(deleterWithoutPassword, { reauthTicket: TICKET });
    await deleteWith(opened, 'Acme');
    opened.ctrl.expectOne('/api/org').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/org').flush({ error: 'reauth_failed' }, status(403));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(
      'The confirmation did not work or took too long, so the company was not deleted. Its notes are already gone. Confirm again.',
    );
    expect(has(opened.fixture, '[data-testid="provider-github"]')).toBe(true);
  });

  it.each(['password_required', 'reauth_required'])('403 %s redraws from a fresh GET /auth/me', async (error) => {
    const opened = await open(deleterWithoutPassword, { reauthTicket: TICKET });
    await deleteWith(opened, 'Acme');
    opened.ctrl.expectOne('/api/org').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/org').flush({ error }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(deleter);
    await settle(opened.fixture);
    expect(has(opened.fixture, '#company-password')).toBe(true);
  });

  it('403 permissions_changed signs the person out to /login', async () => {
    const opened = await open(deleter);
    await deleteWith(opened, 'Acme', 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/account/confirm').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(deleter);
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/org').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/org').flush({ error: 'permissions_changed' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/logout').flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/login');
    expect(opened.auth.token()).toBeNull();
  });

  it('the button is disabled while the deletion is in flight', async () => {
    const opened = await open(deleter);
    await deleteWith(opened, 'Acme', 'Another-Passw0rd');
    expect((opened.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="delete-company-confirm"]')!.disabled).toBe(true);
    opened.ctrl.expectOne('/auth/account/confirm').flush({ error: 'wrong_password' }, status(403));
    await settle(opened.fixture);
  });
});
```

(e2) In `pages/team.spec.ts`, its tests open the page with `manager` (no `org:delete`): they pass unchanged. Add one test: a person with
`org:delete` and without `members:manage` sees no `team-forbidden` and sees `delete-company`.

- [ ] **Step 6: Run.** From `samples/notes-web`: `npm test`, `npx ng build`, `npm run check:docs`; from `samples/notes-api`:
`C:/p6v/Scripts/python.exe -m pytest -q`. All pass. Mutate and see a spec fail, then restore: call `DELETE /auth/org` before `DELETE /api/org`
(`with a password: confirm first, then …` fails); skip the confirmation (`a wrong password at the confirmation: nothing is deleted` fails); read
the ticket in `DeleteCompanySection` itself with `reauthTicketOf(this.router)` instead of the input (`back with the ticket: …` fails, and so do the
tests of `delete-account.provider.spec.ts` if `DeleteAccountSection` does the same).

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files (and `notes.provider.spec.ts` deleted). Scan the touched files.
Proposed subject: `feat(sample): delete the account on the settings screen and the company on the team screen; notes-api DELETE /api/org`.

---

### Task 19: The Playwright spec `12-settings.spec.ts` and the moved steps of 0010 and 0011 (changed by Amendment B at the end of this plan: read it first)

**Files** (all under `samples/notes-web/e2e/`):
- Create: `12-settings.spec.ts`, `support/settings.ts`
- Modify: `support/fake-provider.ts`, `10-private.spec.ts`, `11-providers.spec.ts`

**Interfaces:**
- Consumes: the screens of Tasks 16 to 18; the testing endpoint (Task 2) on the stack of `scripts/e2e-web.sh`, whose overlay sets
  `Auth__Testing__WindowEndpoint: "true"` and GitHub's keys (Task 3).
- Produces: `FakeProvider` with `'GitHub'`; `endConfirmationWindow(page)`; `openSettings(page)`.

**Contract counts:** `11-providers.spec.ts` counts three buttons.

**Decisions the plan takes here:**
- The window is ended by `endConfirmationWindow(page)`: in the browser's own cookie jar (`page.request`), a refresh gives an access token
  (and rotates the cookie the app will use next), and `POST /auth/testing/end-confirmation` with it ends the window of that session; the app's
  access token in memory stays valid. Nothing moves the server's clock.
- Every person is made with a fresh address of the fake's free-address mode, so that the spec runs in Chromium and WebKit and again on the same
  stack.
- The implementer writes and type-checks the spec (`npx tsc -p e2e/tsconfig.json --noEmit`); it is run in both browsers by the e2e verifier
  (Global Constraints: no stack is brought up by an implementer).

- [ ] **Step 1: The helpers.** In `support/fake-provider.ts`, replace `export type FakeProvider = 'Google' | 'Microsoft';` with
`export type FakeProvider = 'Google' | 'Microsoft' | 'GitHub';` (the fake's GitHub page has the heading "Sign in to the fake GitHub", the
"Email" field and the box "The provider verified this address", as the others).

`support/settings.ts`:

```ts
import { Page, expect } from '@playwright/test';

/** Opens the settings screen by the header's link, as a person does. */
export async function openSettings(page: Page): Promise<void> {
  await page.getByTestId('settings-link').click();
  await expect(page).toHaveURL(/\/settings$/);
  await expect(page.getByRole('heading', { name: 'Settings', level: 1 })).toBeVisible();
}

/**
 * Spec 0012 → Testing endpoint: ends the confirmation window of the browser's session. The browser's own cookie jar (`page.request`) holds
 * the refresh cookie: a refresh gives an access token of that session (and the next cookie, which the app then uses), and the testing
 * endpoint ends the window with it. The server's clock is not moved. Nothing of it is printed.
 */
export async function endConfirmationWindow(page: Page): Promise<void> {
  const refreshed = await page.request.post('/auth/refresh');
  expect(refreshed.status(), 'the refresh before ending the window').toBe(200);
  const body = (await refreshed.json()) as { access_token?: unknown };
  expect(typeof body.access_token, 'the refresh gave no access token').toBe('string');
  const ended = await page.request.post('/auth/testing/end-confirmation', {
    headers: { Authorization: `Bearer ${String(body.access_token)}` },
  });
  expect(ended.status(), 'the testing endpoint (is Auth__Testing__WindowEndpoint on?)').toBe(204);
}
```

- [ ] **Step 2: The spec.** `12-settings.spec.ts`:

```ts
import { Page, expect, test } from '@playwright/test';
import { signInAtFake, expectNoTicketInAddressBar } from './support/fake-provider';
import { openMailLink, waitForLink } from './support/mail';
import { signIn, signOut } from './support/session';
import { endConfirmationWindow, openSettings } from './support/settings';

// Spec 0012, criterion 30: the settings screen against the fake provider, on the stack of scripts/e2e-web.sh with the testing endpoint on.
const MAIL_WAIT_MS = 100_000;

function unique(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1_000_000)}`;
}

/** A new person signs up with a provider as a private person, through the finish screen, and lands on the notes. */
async function signUpPrivately(page: Page, provider: 'Google' | 'GitHub', email: string): Promise<void> {
  await page.goto('/signup');
  await page.getByRole('link', { name: `Sign up with ${provider}` }).click();
  await signInAtFake(page, provider, email, true);
  await expect(page).toHaveURL(/\/provider-finish$/);
  expectNoTicketInAddressBar(page);
  await page.locator('label').filter({ hasText: 'Private person' }).click();
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
}

test('a person signs up with GitHub, connects Google, sets a password by mail, disconnects GitHub after confirming, and changes the password', async ({ page }) => {
  test.setTimeout(300_000);
  const email = `${unique('settings')}@github.e2e.test`;
  const first = `E2e-First-${unique('p')}-A1`;
  const second = `E2e-Second-${unique('p')}-B2`;

  // GitHub is a button beside the others, and a new person signs up with it.
  await page.goto('/login');
  await expect(page.getByTestId('provider-buttons').getByRole('link')).toHaveText([
    'Continue with Google',
    'Continue with Microsoft',
    'Continue with GitHub',
  ]);
  await signUpPrivately(page, 'GitHub', email);

  // The settings: no password, GitHub connected; Google connects at once, without a confirmation (the sign-up opened the window).
  await openSettings(page);
  await expect(page.getByTestId('row-password')).toContainText('Not set');
  await expect(page.getByTestId('row-github')).toContainText('Connected');
  await page.getByTestId('connect-google').click();
  await signInAtFake(page, 'Google', email, true);
  await expect(page).toHaveURL(/\/settings$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('settings-notice')).toHaveText('Google connected.');
  await expect(page.getByTestId('row-google')).toContainText('Connected');
  await expect(page.getByTestId('confirm-dialog')).toHaveCount(0);

  // A first password through "Send me a link" and the reset mail; setting it signs out everywhere, so the person signs in with it.
  await page.getByTestId('send-link').click();
  await expect(page.getByTestId('link-sent')).toHaveText(
    'Check your inbox: the link sets your password. Setting it signs you out everywhere.',
  );
  const link = await waitForLink(email, 'reset', MAIL_WAIT_MS);
  await openMailLink(page, link);
  await page.getByLabel('New password', { exact: true }).fill(first);
  await page.getByLabel('Repeat the password').fill(first);
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page.getByText('Password changed.')).toBeVisible();
  await signIn(page, email, first);

  // The window ended by the testing endpoint: "Disconnect" GitHub asks "Confirm it's you" first, with the password.
  await openSettings(page);
  await expect(page.getByTestId('row-password')).toContainText('Set');
  await endConfirmationWindow(page);
  await page.getByTestId('disconnect-github').click();
  const dialog = page.getByTestId('confirm-dialog');
  await expect(dialog.getByRole('heading', { name: "Confirm it's you" })).toBeVisible();
  await dialog.getByLabel('Password').fill(first);
  await dialog.getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByTestId('settings-notice')).toHaveText('GitHub disconnected.');
  await expect(page.getByTestId('row-github')).toContainText('Connect');
  await expect(page.getByTestId('disconnect-github')).toHaveCount(0);

  // The password changed with the current one; the new one signs in after.
  await page.locator('#current-password').fill(first);
  await page.locator('#settings-new-password').fill(second);
  await page.locator('#settings-repeat-password').fill(second);
  await page.getByTestId('password-submit').click();
  await expect(page.getByTestId('password-message')).toHaveText('Password changed.');
  await page.goto('/notes');
  await signOut(page);
  await signIn(page, email, second);
});

test('a person with only one provider sees "Disconnect" disabled, with the sentence', async ({ page }) => {
  test.setTimeout(120_000);
  const email = `${unique('single')}@gmail.e2e.test`;
  await signUpPrivately(page, 'Google', email);

  await openSettings(page);
  await expect(page.getByTestId('disconnect-google')).toBeDisabled();
  await expect(page.getByTestId('disconnect-blocked')).toHaveText(
    'Set a password or connect another provider before you disconnect this one.',
  );
});

test('an admin without a password deletes the company with "Confirm with Google"', async ({ page }) => {
  test.setTimeout(180_000);
  const email = `${unique('owner')}@gmail.e2e.test`;
  const company = `Firma ${unique('c')}`;

  await page.goto('/signup');
  await page.getByRole('link', { name: 'Sign up with Google' }).click();
  await signInAtFake(page, 'Google', email, true);
  await expect(page).toHaveURL(/\/provider-finish$/);
  await page.locator('label').filter({ hasText: 'Organization' }).click();
  await page.getByLabel('Company name').fill(company);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-company')).toHaveText(company);

  await page.getByTestId('team-link').click();
  await expect(page).toHaveURL(/\/team$/);
  await page.getByTestId('delete-company').click();
  await expect(page.locator('#company-password')).toHaveCount(0);
  await page.getByRole('link', { name: 'Confirm with Google' }).click();
  await signInAtFake(page, 'Google', email, true);

  // Back on the team screen, ready to confirm; the ticket is in no URL.
  await expect(page).toHaveURL(/\/team$/);
  expectNoTicketInAddressBar(page);
  await page.locator('#company-name').fill('not the name');
  await page.getByTestId('delete-company-confirm').click();
  await expect(page.getByTestId('delete-company-error')).toHaveText("Type the company's name exactly.");
  await page.locator('#company-name').fill(company);
  await page.getByTestId('delete-company-confirm').click();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByTestId('login-notice')).toHaveText('The company was deleted.');

  // The account went with the company: the same Google identity is a new person now.
  await page.getByRole('link', { name: 'Continue with Google' }).click();
  await signInAtFake(page, 'Google', email, true);
  await expect(page).toHaveURL(/\/provider-finish$/);
});
```

(`openMailLink(page, link)` is `support/mail.ts`'s helper that opens a mail link on the test origin.)

- [ ] **Step 3: The moved steps.** In `10-private.spec.ts`, replace

```ts
  // "Delete account": the warning, the password, and the sign-in screen with the notice.
  await page.getByTestId('delete-account').click();
```

with

```ts
  // "Delete account" (on the settings screen since spec 0012): the warning, the password, and the sign-in screen with the notice.
  await openSettings(page);
  await page.getByTestId('delete-account').click();
```

and add `import { openSettings } from './support/settings';`. The note added before it stays on the notes screen (it is added first, then the
person goes to the settings).

In `11-providers.spec.ts`:

(a) replace `  await expect(buttons).toHaveText(['Continue with Google', 'Continue with Microsoft']);` with
`  await expect(buttons).toHaveText(['Continue with Google', 'Continue with Microsoft', 'Continue with GitHub']);   // spec 0012: three`;

(b) in `a private person without a password deletes the account with "Confirm with Google"`, replace

```ts
  await page.getByTestId('delete-account').click();
  await expect(page.getByTestId('delete-warning')).toHaveText('This deletes your account and all your notes. It cannot be undone.');
  await expect(page.locator('#delete-password')).toHaveCount(0);
  await page.getByRole('link', { name: 'Confirm with Google' }).click();
  await signInAtFake(page, 'Google', email, true);

  // Back on the notes with the section open, ready to confirm; the ticket is in no URL.
  await expect(page).toHaveURL(/\/notes$/);
```

with

```ts
  await openSettings(page);
  await page.getByTestId('delete-account').click();
  await expect(page.getByTestId('delete-warning')).toHaveText('This deletes your account and all your notes. It cannot be undone.');
  await expect(page.locator('#delete-password')).toHaveCount(0);
  await page.getByRole('link', { name: 'Confirm with Google' }).click();
  await signInAtFake(page, 'Google', email, true);

  // Back on the settings with the section open, ready to confirm; the ticket is in no URL.
  await expect(page).toHaveURL(/\/settings$/);
```

and add `import { openSettings } from './support/settings';`. Search the two files for any other `delete-account` or `/notes$/` after a
confirmation (`grep -n "delete-account\|Confirm with" e2e/*.spec.ts`) and move it the same way; name each in the hand-back.

- [ ] **Step 4: Check.** From `samples/notes-web`: `npx tsc -p e2e/tsconfig.json --noEmit` (the Playwright specs type-check) and `npm test`
(unchanged) pass. Do not run Playwright: the e2e verifier runs it in Chromium and WebKit on the stack of `scripts/e2e-web.sh`.

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`test(sample): the settings screen in the browser, and the 0010 and 0011 specs on /settings`.

---

### Task 20: The documents and the acceptance map check (changed by Amendment B at the end of this plan: read it first)

**Files:**
- Modify: `docs/deployment/provider-keys.md`, `docs/security/threat-model.md`, `docs/integration/angular.md`, `docs/deployment/vps.md`,
  `docs/design.md`, `CHANGELOG.md`, `samples/notes-web/README.md`
- Read only: `docs/superpowers/plans/0012-acceptance-map.md`

**Interfaces:** none (documents). `scripts/check-docs.py` checks the links, paths and named identifiers of the documents it lists.

**Contract counts:** none.

**Decisions the plan takes here:**
- The keys guide keeps its structure and gains a "GitHub" part between "Microsoft" and "Turn it on and check it"; its title names the three.
- The threat model's numbered list keeps its numbers: 51, 52, 53, 55, 56, 57, 58 and 2 are widened in place, 59 is rewritten, 62 to 64 are
  added after 61 (the spec's numbers).

- [ ] **Step 1: The keys guide.** In `docs/deployment/provider-keys.md`:

(a) replace `# Getting the keys for Google and Microsoft` with `# Getting the keys for Google, Microsoft and GitHub`, and in the first paragraph
`Auth-Core signs people in with Google and Microsoft when three things are in place: your manifest lists the providers` with
`Auth-Core signs people in with Google, Microsoft and GitHub when three things are in place: your manifest lists the providers`, and
`(`providers: [google, microsoft]`, see` with `(`providers: [google, microsoft, github]`, see`;

(b) replace `Both providers send the browser back to Auth-Core, never to your frontend.` with
`Every provider sends the browser back to Auth-Core, never to your frontend.`, and after the table row
`| Microsoft | `https://app.example.com/auth/providers/microsoft/callback` |` add
`| GitHub | `https://app.example.com/auth/providers/github/callback` (GitHub calls it the "Authorization callback URL") |`;

(c) in Google's "Check it with the sample", in the overlay, after
`         Auth__Providers__Microsoft__ClientSecret: ${AUTH_MICROSOFT_CLIENT_SECRET:-}` add

```yaml
         Auth__Providers__GitHub__ClientId: ${AUTH_GITHUB_CLIENT_ID:-}
         Auth__Providers__GitHub__ClientSecret: ${AUTH_GITHUB_CLIENT_SECRET:-}
```

and replace `The repository's notes sample (`samples/notes-web` in front of `samples/notes-api`, at `https://localhost:8443`) lists Google and Microsoft in`
with `The repository's notes sample (`samples/notes-web` in front of `samples/notes-api`, at `https://localhost:8443`) lists Google, Microsoft and GitHub in`
and `   With Google's keys only, Microsoft is not offered and `/auth/health` says `Degraded`: that is expected.` with
`   With Google's keys only, Microsoft and GitHub are not offered and `/auth/health` says `Degraded`: that is expected.`;

(d) add before `## Turn it on and check it`:

```markdown
## GitHub

On GitHub, signed in as the account or organization that will own the app: **Settings**, **Developer settings**, **OAuth Apps**, **New OAuth
App** (an organization's apps are under the organization's settings, "Developer settings", "OAuth Apps"):

1. **The app.** The "Application name" the person will see; the "Homepage URL", your product's address (`https://app.example.com`); the
   "Authorization callback URL", the GitHub address of the table above. Leave "Enable Device Flow" off. Register.
2. **The client id.** The "Client ID" of the app's page goes into `AUTH_GITHUB_CLIENT_ID`.
3. **The secret.** "Generate a new client secret", and copy it into `AUTH_GITHUB_CLIENT_SECRET` at once: GitHub shows it only now. A GitHub
   OAuth App secret does not lapse.
4. **Nothing else.** GitHub has no consent screen to set up and no scopes to register: Auth-Core asks for `user:email` at each sign-in, which
   lets it read the person's addresses, and GitHub shows that on its authorization page. Auth-Core uses the person's **primary** address only,
   and only when GitHub has verified it; after reading it Auth-Core revokes the token at once, so the app holds no access to the account.

A GitHub sign-in cannot be asked for a fresh password: a confirmation with GitHub ("Confirm with GitHub") is usually one click, or none (the
threat model's risk 57).

### Check it with the sample

1. Register a second OAuth App for the check (GitHub allows one callback address per OAuth App) with the "Homepage URL"
   `https://localhost:8443` and the "Authorization callback URL" `https://localhost:8443/auth/providers/github/callback`. Delete it once the
   check is done.
2. Put its `AUTH_GITHUB_CLIENT_ID` and `AUTH_GITHUB_CLIENT_SECRET` into the repository's `.env`; the overlay of Google's check passes them.
3. Start the stack as in Google's check, open `https://localhost:8443/login`, press "Continue with GitHub" and authorize the app at GitHub: a
   GitHub account whose primary address is verified and new to the sample reaches `/provider-finish`; one whose primary address is not
   verified reaches `/provider-link` with the sentence about an unverified address.

```

(e) in "Turn it on and check it", replace `1. Set the values in `.env` and `providers: [google, microsoft]` in your manifest (list only the ones you configured), then`
with `1. Set the values in `.env` and `providers: [google, microsoft, github]` in your manifest (list only the ones you configured), then`, and
`3. On your product's sign-in screen, "Continue with Google" (and "with Microsoft") goes to the provider and back, and a new person reaches the`
with `3. On your product's sign-in screen, "Continue with Google" (and "with Microsoft", "with GitHub") goes to the provider and back, and a new person reaches the`;

(f) in "When a value is wrong", add the rows:

```markdown
| GitHub shows "The redirect_uri is not associated with this application" | The "Authorization callback URL" of the OAuth App differs from the one Auth-Core sends: compare it with the table above |
| A GitHub sign-in comes back with `#error=provider_error`, and `$C logs auth` names `incorrect_client_credentials` or `bad_verification_code` | The GitHub client secret is wrong (or was regenerated), or the code was used already: check `AUTH_GITHUB_CLIENT_SECRET` |
| A GitHub sign-in always reaches the link screen with the sentence about an unverified address | The account's primary address is not verified at GitHub (GitHub, Settings, Emails), or the account keeps its address private and has a verified secondary one: only the primary address counts |
| `$C logs auth` warns that the GitHub token could not be revoked | GitHub did not answer the revocation with `204`: the token stays valid at GitHub, unused and not stored, until the person revokes the app (risk 63); check the client secret |
```

(and in the first row's settings list, `(`Auth:Providers:Google:ClientId`, `Auth:Providers:Google:ClientSecret`, ..., `Auth:App:FrontendUrls:ProviderReturn`)`
stays: `...` covers GitHub's keys).

- [ ] **Step 2: The threat model.** In `docs/security/threat-model.md`:

(a) in the diagram, replace `Auth-Core ──https (discovery, keys, token endpoint)──> Google, Microsoft     Browser ──https round trip──> Google, Microsoft (spec 0011)`
with

```
 Auth-Core ──https (discovery, keys, token endpoint)──> Google, Microsoft     Browser ──https round trip──> Google, Microsoft, GitHub
 Auth-Core ──https (token endpoint; api.github.com /user, /user/emails, the token's revocation)──> GitHub (spec 0012)
```

and in the next paragraph `with providers on, Auth-Core to Google and Microsoft (outbound HTTPS, the client secret)` with
`with providers on, Auth-Core to Google, Microsoft and GitHub (outbound HTTPS, the client secret)`;

(b) replace `## Sign-in with Google and Microsoft` with `## Sign-in with Google, Microsoft and GitHub`, and after its first paragraph (the one
ending `comes from configuration only.`) add:

```markdown

**GitHub** (spec 0012) is an OAuth 2.0 provider, not OpenID Connect: no identity token and no nonce. Auth-Core redeems the code (PKCE `S256`,
the client secret), requires `iss` on the authorization answer, then reads `GET /user` and `GET /user/emails` with the token through an HTTP
client of its own (one attempt per call, 10 seconds, 64 KiB per answer) and revokes the token (`DELETE /applications/{client_id}/token`) in
every case, success or failure, before the callback answers; the token is never stored and never reaches a log. The identity is the decimal
`id` of `/user`, never `login`; the address is the one entry of `/user/emails` marked `primary`, verified only when GitHub says so.
```

and add rows to its table, after the row that starts `| S | Someone signs in as an account by owning a provider account with its address`:

```markdown
| S | Someone signs in with a GitHub account whose secondary address is the victim's | Only the primary entry of `/user/emails` is the address, and only when verified; `email` of `/user` is never read (`GitHubIdentityTests`) | |
| I | The GitHub token in a log, a ticket or the audit log | The reader keeps it in memory only, revokes it in a `finally`, removes it from OpenIddict's context, and logs a failed revocation without it (`GitHubCallbackTests`, `GitHubCallbackErrorTests`) | A token GitHub would not revoke stays valid at GitHub (risk 63) |
| D | GitHub answers slowly or with a huge body | 10 seconds and 64 KiB per call, one attempt, then `provider_error` (`GitHubCallbackErrorTests`) | |
```

(c) add a section before `## Account deletion and personal data`:

```markdown
## Managing logins

Spec 0012. The settings screen of a product calls four endpoints with the access token **and** the refresh cookie, which names the session of
this device. A session is **confirmed** for 5 minutes after a sign-in that starts it or after `POST /auth/account/confirm` (the password, or a
`reauth` ticket of a linked identity); the window is bound to that session and a refresh never extends it. Within it, `POST
/auth/providers/connect` adds a provider identity whose verified address is the account's (one login per provider), and `POST
/auth/account/logins/unlink` removes a provider's logins, never the last way in, ends every other session and hands this device a renewed
one. `POST /auth/account/password` asks the current password every time, ends every other session, voids the mail links and renews this
device's session. Each connect, unlink and password change mails the account ("provider connected", "provider disconnected", "password
changed"), at most 10 an hour per address, with no link. `DELETE /auth/org` also takes a `reauth` ticket, used up inside the deletion's
transaction.

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | Script in the page (XSS) connects the attacker's provider to the account | The connect needs a ticket of this browser whose **verified** address is the account's; another address is `403 address_mismatch` (`ProviderConnectTests`) | |
| S | A stolen access token alone acts on the settings | Every settings endpoint also needs the refresh cookie of a live session of the same account (`401 session_required`, `ConfirmationWindowTests`, `ProviderUnlinkTests`) | |
| T | Script in the page disconnects a provider during the window | The window lasts 5 minutes, is bound to one session, and the last way in cannot go (`409 last_login`); every disconnect is mailed (`ProviderUnlinkTests`, `SettingsMailTests`) | Risk 62 |
| T | A reset and a password change race | Both meet at the account's mail link row; the first wins and neither answers `500`: the change answers `403 wrong_password` when the reset commits while it waits at the link, and `401 session_required` when the reset committed before it took the address lock (`AccountPasswordTests`, Amendment A1) | |
| R | A person denies a confirmation, a connect, a disconnect or a password change | `account.confirmed`, `account.confirm_refused`, `provider.linked` (`via`), `provider.unlinked`, `password.changed` and `login.succeeded` (`via session_renewal`) (`AuditSettingsFlowsTests`) | |
| I | A password, a ticket, a cookie or a token in a log, a mail or the audit log | None is logged or written; the mails hold no link and no token (`AssertNoSecretsAsync` in every class of this spec, `MailComposerTests`) | |
| D | The password at the confirmation and the change guessed | Both count in the account's login streak and its lockout, and against the per-IP `login` limit (`AccountConfirmTests`, `AccountPasswordTests`, `RateLimitMiddlewareTests`) | The lockout a distributed guesser triggers blocks them too (risk 2) |
| D | The mails used as noise | 10 an hour per address for the three kinds together; beyond it the change happens unmailed (`SettingsMailTests`) | Risk 64 |
| E | A session outlives the unlink of the identity it signed in with | A sign-in holds its login row under a share lock; an unlink that comes second ends the session it issued (`ProviderUnlinkRaceTests`) | |
| E | A renewal extends a session past its 30-day cap | The renewed session keeps the old start; past the cap the endpoint is `401 session_required` (`ProviderUnlinkTests`) | |
```

(d) in "Residual risks", replace these items whole (their current first words, then the text that replaces the item):

- `2. **A distributed guesser below the per-IP limits**` … → append to the item, before its final `(spec 0003, …)`: `It also blocks the owner's
  `POST /auth/account/confirm` and password change while it lasts (spec 0012).` (keep the rest of the line);
- `51.` → `51. **The product deletes its data first.** The product's frontend deletes its data and calls `DELETE /auth/account` or `DELETE
  /auth/org` after it, so a wrong password, a lockout or an expired ticket at the second step leaves the account or the company in place with
  no product data. For a company, any holder of `org:delete` in the product can wipe the company's data before Auth-Core judges the
  confirmation; the sample checks the name and the password first (`POST /auth/account/confirm`), but an admin without a password, or a ticket
  that expires between the wipe and the deletion, still leaves a company with no data (spec 0010, Decision 17; spec 0012).`;
- `52.` → the same sentences with `Whoever takes over a linked Google, Microsoft or GitHub account`, and add `GitHub's OAuth App secret does not
  lapse.` at its end; `53.`, `55.`, `56.`, `58.`: replace `Google and Microsoft` and `Google or Microsoft` by `Google, Microsoft and GitHub`
  (`… or GitHub`) and end each with `(spec 0011; spec 0012 for GitHub)`;
- `57.` → `57. **Re-authentication is as strong as the provider makes it.** A person still signed in at the provider may confirm with one click;
  whoever holds a live access token and the refresh cookie in that browser can then confirm a deletion or open the confirmation window. Auth-Core
  asks for a fresh sign-in (`prompt=login` at Microsoft, `prompt=select_account` and `max_age=0` at Google) but does not check that one
  happened; GitHub cannot be asked for one at all (`prompt=select_account` only). Script in the product's page that opens such a round trip in a
  window of the same origin can read the ticket there (risk 62) (spec 0011; spec 0012).`;
- `59.` → `59. **A provider login can be unwanted.** A provider login is now mailed when it is added and can be disconnected on the settings
  screen, and a disconnect signs the other devices out (their access tokens still work for up to 10 minutes). What remains: a person who ignores
  the mail keeps the intruder's login until they act; and a stale "verified" flag at a provider can still create the account of an address
  nobody has used yet (the mailbox owner then sees the login in their settings after "forgot password" and disconnects it) (spec 0011; spec
  0012).`;
- add after `61.`:

```markdown
62. **The confirmation window.** For 5 minutes after a sign-in or a confirmation, script running in the product's page (an XSS) can disconnect a provider of the account, as long as another way in remains; each disconnect mails the account. It cannot connect its own provider (the connect needs a verified address equal to the account's), cannot change the password (the current one is asked every time) and cannot set a first one (that needs the mailbox). A script that can open a `reauth` round trip in a window of the same origin, at a provider that asks for no fresh sign-in (GitHub, risk 57), can read the ticket from the return screen and reopen the window, or confirm a deletion, as often as that provider lets it (spec 0012).
63. **A GitHub token that could not be revoked** stays valid at GitHub with the scope `user:email`, unused and not stored, until the person revokes the app in their GitHub settings or GitHub expires it after a year of disuse. The log names the failure, never the token (spec 0012).
64. **Notification mails as noise.** Script in the page that can drive silent GitHub round trips can connect and disconnect GitHub in turn; each change mails the account, up to the limit of 10 an hour, after which the changes go on unmailed (spec 0012, Decision 18).
```

- [ ] **Step 3: The Angular guide.** In `docs/integration/angular.md`:

(a) replace `## Google and Microsoft (spec 0011)` with `## Google, Microsoft and GitHub (specs 0011 and 0012)` and
`When `GET /auth/options` lists `providers` (`["google","microsoft"]`, in the manifest's order)` with
`When `GET /auth/options` lists `providers` (`["google","microsoft","github"]`, in the manifest's order)`;

(b) in the bullet `**"Delete account" without a password.**`, replace `([`pages/notes.ts`](../../samples/notes-web/src/app/pages/notes.ts)).` with
`([`pages/delete-account.ts`](../../samples/notes-web/src/app/pages/delete-account.ts); since spec 0012 the section is on `/settings`).`, and
replace the bullet that starts `- **Deleting a company needs a password too.**` with:

```markdown
- **Deleting a company** (spec 0012). `DELETE /auth/org` takes `{"name","password"}` or, for an admin without a password,
  `{"name","provider_ticket"}` with a `reauth` ticket (the round trip armed for the team screen). The sample compares the typed name with
  `org_name` of `GET /auth/me` first, confirms the password with `POST /auth/account/confirm` before any data goes, then deletes its own data
  (`DELETE /api/org` of notes-api) and only after its `204` calls `DELETE /auth/org`, then signs out
  ([`pages/delete-company.ts`](../../samples/notes-web/src/app/pages/delete-company.ts)). **Warning (risk 51):** a product's own deletion of a
  company's data must be limited to holders of `org:delete` and should confirm the person first; an admin without a password, or a ticket
  that expires between your deletion and Auth-Core's, still leaves a company with no data.
```

and in the paragraph about the flag replace `and, only after "Confirm with {provider}" in the delete section, a one-shot flag (`notes-web.reauth`, the time it was set)`
with `and, only after a button that starts a round trip whose ticket comes back to the app ("Confirm with {provider}", "Connect"), a one-shot
flag (`notes-web.reauth`, `<target>:<the time it was set>`, the target being `delete_account`, `delete_company`, `confirm` or `connect`)`, and
`it hands a `reauth` ticket to the notes screen only when the flag is there` with `it hands a ticket on only when a flag of the fitting target is there`;

(c) add before `## The limits and an outage (spec 0008)`:

```markdown
## The settings screen (spec 0012)

The sample's `/settings` ([`pages/settings.ts`](../../samples/notes-web/src/app/pages/settings.ts)) shows the person's sign-in methods (the
password, and one row per provider: "Connected" with "Disconnect", or "Connect"), the password ("Change password", or "Set a password" with
"Send me a link", which calls `POST /auth/password/forgot` with the account's address) and "Delete account".

- **The four endpoints** need the access token **and** the refresh cookie: `POST /auth/account/confirm` (`{"password"}` or
  `{"provider_ticket"}`), `POST /auth/providers/connect` (`{"ticket"}` of a round trip with `purpose=connect`), `POST
  /auth/account/logins/unlink` (`{"provider"}`) and `POST /auth/account/password` (`{"current_password","new_password"}`). Add the four
  paths to the interceptor's exact token paths (step 3). The refresh cookie goes along by itself (`Path=/auth`).
- **The window.** `GET /auth/me` says `confirmed_until`: the end of the 5-minute confirmation window of this device's session, or `null`.
  Before a connect or a disconnect, read it; when it is `null` or past, open "Confirm it's you" ([`pages/confirm-dialog.ts`](../../samples/notes-web/src/app/pages/confirm-dialog.ts)):
  the password, or one "Confirm with {Provider}" per linked offered provider (the round trip comes back to `/settings` with the window open,
  and the person clicks again), or, with neither, "Set a password first" and "Send me a link". A `403 confirmation_required` (the window
  closed meanwhile) opens the dialog too; a connect ticket refused that way is sent again after a password confirmation.
- **The renewed session.** The unlink and the password change answer as a login answers: store the new access token (the refresh cookie is
  already the new one). While one of them is in flight, send no other request with the refresh cookie (no refresh, no other settings call):
  the sample's interceptor holds every request to `/auth/` until it settles ([`auth/auth.interceptor.ts`](../../samples/notes-web/src/app/auth/auth.interceptor.ts)).
- **`401 session_required`** means the session of this device is over: sign the person out to the sign-in screen ("Your session ended. Sign
  in again.") and do not refresh.
- **The answers**, one sentence each: `409 last_login`, `409 provider_in_use`, `409 provider_already_linked`, `403 address_mismatch`,
  `403 email_not_verified`, `403 no_email`, `400 invalid_ticket`, `403 reauth_failed`, `403 wrong_password`, `429` with the wait, `400
  weak_password` with the rules; `403 password_not_set` redraws from a fresh `GET /auth/me`. The link screen gains `409 provider_in_use`.
- **The return screen** handles `purpose=connect` (it sends the connect and opens `/settings` with the sentence) and a `reauth` return for the
  settings screen (it sends the confirmation and opens `/settings`), each only with the flag of its target.

```

- [ ] **Step 4: The deployment guide, the design, the changelog, the sample's readme.**

In `docs/deployment/vps.md`: replace `stay blank unless you turn on sign-in with Google and Microsoft: see "Sign-in with Google and Microsoft" in step 7.`
with `stay blank unless you turn on sign-in with a provider: see "Sign-in with Google, Microsoft and GitHub" in step 7. The two GitHub
variables (`AUTH_GITHUB_CLIENT_ID`, `AUTH_GITHUB_CLIENT_SECRET`) are optional in the same way (spec 0012).`, replace
`The six provider variables` with `The eight provider variables` and append `, `AUTH_GITHUB_CLIENT_ID`, `AUTH_GITHUB_CLIENT_SECRET`` inside its
parenthesis; replace `### Sign-in with Google and Microsoft (optional)` with `### Sign-in with Google, Microsoft and GitHub (optional)`,
`It needs your manifest to list the providers (`providers: [google, microsoft]`)` with
`It needs your manifest to list the providers (`providers: [google, microsoft, github]`)`, and
`followed by `/auth/providers/google/callback` (or `/microsoft/callback`),` with
`followed by `/auth/providers/google/callback` (or `/microsoft/callback`, `/github/callback`: GitHub's "Authorization callback URL"),`. In the
troubleshooting table replace `| A Microsoft or Google sign-in comes back with `#error=provider_error` |` with
`| A Microsoft, Google or GitHub sign-in comes back with `#error=provider_error` |`.

(`deploy/docker-compose.prod.yml` and `deploy/.env.prod.example` got the two GitHub variables in Task 3; check that they are there and
commented as optional.)

In `docs/design.md`, after the paragraph that begins `*As built (spec 0011):*`, add:

```markdown

*Spec 0012:* GitHub is the third provider (an OAuth App: the code flow with PKCE, `GET /user` and `/user/emails` read and the token revoked
at once, the decimal `id` as the identity, the primary verified address only). A settings screen manages one's own logins: a five-minute
confirmation window bound to the session of the device (the refresh cookie names it), connect and disconnect (never the last way in; a
disconnect signs the other devices out and renews this one), the password changed against the current one, and three notice mails without a
link. `DELETE /auth/org` takes a provider confirmation. With it the phase's milestone is complete for the three providers.
```

In `CHANGELOG.md`, after the paragraph under `## Unreleased` that begins `Sign-in with Google and Microsoft ([spec 0011]`, add:

```markdown

GitHub, and managing one's own logins ([spec 0012](docs/superpowers/specs/0012-github-and-managing-logins.md)): "Continue with GitHub", and a
settings screen to connect and disconnect providers and change the password. An instance that does not list GitHub sees no change in sign-in;
`GET /auth/me` gains `confirmed_until`, the link refuses a second login of one provider, and the link queues a mail.
```

and under `### Added` (at the end of its list) add:

```markdown
- **GitHub sign-in** (spec 0012): the provider name `github`, `AUTH_GITHUB_CLIENT_ID`/`AUTH_GITHUB_CLIENT_SECRET`, the round trip through
  OpenIddict's GitHub provider, `GET /user` and `/user/emails` read and the token revoked at once.
- **The confirmation window** (spec 0012): five minutes after a sign-in or `POST /auth/account/confirm`, bound to the session of the device;
  `GET /auth/me` `confirmed_until`.
- **`POST /auth/account/confirm`, `POST /auth/providers/connect`, `POST /auth/account/logins/unlink`, `POST /auth/account/password`**, and the
  `connect` purpose of `GET /auth/providers/{name}/start`.
- **Three notice mails** (provider connected, provider disconnected, password changed), Polish and English, no link, 10 an hour per address.
- **Four audit kinds** (32 in all): `account.confirmed`, `account.confirm_refused`, `provider.unlinked`, `password.changed`; `provider.linked`
  and `login.succeeded` gain `details.via`.
- **`DELETE /auth/org` with a provider ticket** for an admin without a password.
- **The sample**: GitHub buttons, `/settings`, "Delete company" on `/team`, notes-api `DELETE /api/org`; `scripts/e2e-settings.sh`.
- A Development-only endpoint, `POST /auth/testing/end-confirmation` (`Auth:Testing:WindowEndpoint`), for browser tests.
```

and under `### Changed` add:

```markdown
- **The link** (`POST /auth/providers/link`) answers `409 provider_in_use` for an account that already has a login of the ticket's provider,
  and queues the mail "provider connected" (spec 0012).
- **A provider sign-in holds its login** against an unlink in the same moment (spec 0012).
```

and under `### Upgrading` add:

```markdown
- Spec 0012: one migration adds the table `SessionStates` and the column `MailRequests.Provider`. Sessions from before the upgrade keep working;
  their confirmation window is closed until the person confirms. An account that the link of spec 0011 gave two logins of one provider keeps
  both; `GET /auth/me` lists the provider once, and a disconnect removes both.
```

In `samples/notes-web/README.md`, where it lists the screens (or after the sentence that names `/provider-return`), add one sentence:
`Spec 0012 adds `/settings` (sign-in methods, the password, "Delete account") and "Delete company" on `/team`; `scripts/e2e-settings.sh` and
`e2e/12-settings.spec.ts` drive them against the fake provider.` (Read the file first and put it where the screens are named.)

- [ ] **Step 5: Check the documents.** `C:/p6v/Scripts/python.exe scripts/check-docs.py` passes (links, paths and identifiers of the listed
documents); `C:/p6v/Scripts/python.exe scripts/check-docs.py --self-test` passes. From `samples/notes-web`, `npm run check:docs` passes.

- [ ] **Step 6: The acceptance map against the code.** Every `Class.Method` in `docs/superpowers/plans/0012-acceptance-map.md` must be a test
the code now has. From the repository root:

```bash
C:/p6v/Scripts/python.exe - <<'EOF'
import re, pathlib
text = pathlib.Path('docs/superpowers/plans/0012-acceptance-map.md').read_text(encoding='utf-8')
names = sorted(set(re.findall(r'`([A-Z][A-Za-z0-9]+Tests)\.([A-Za-z0-9_]+)`', text)))
sources = {p: p.read_text(encoding='utf-8') for p in pathlib.Path('tests').rglob('*.cs')}
missing = []
for cls, method in names:
    if not any(f'class {cls}' in s and re.search(rf'\b{re.escape(method)}\s*\(', s) for s in sources.values()):
        missing.append(f'{cls}.{method}')
print('\n'.join(missing) or f'all {len(names)} named tests exist')
EOF
```

It prints `all N named tests exist`; a name it lists is a finding (a test renamed in a task, or a map entry that drifted): report it, do not
rename tests to fit.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`docs: GitHub keys, the settings screen, the threat model and the changelog of spec 0012`.

---

## Self-review

**Spec coverage, criterion by criterion** (the tests are in [`0012-acceptance-map.md`](0012-acceptance-map.md)):

| # | Where | # | Where |
| - | ----- | - | ----- |
| 1 | Task 3 (manifest, options, settings, health) | 18 | Task 12 |
| 2 | Task 6 (`GitHubStartTests`), Task 5 (the registrations) | 19 | Task 12 (with Amendment A1's three outcomes) |
| 3 | Tasks 5 and 6 | 20 | Task 8 (`ProviderPasswordResetTests`) |
| 4 | Task 5 (`GitHubIdentityTests`, one test per rule) | 21 | Task 13 |
| 5 | Task 6 (`GitHubCallbackErrorTests`, one test per path) | 22 | Task 8, with rows in Tasks 9, 10 and 12 |
| 6 | Task 14 (`GitHubFlowTests`) | 23 | Tasks 2 and 6 |
| 7 | Tasks 6 and 14 | 24 | Tasks 2 and 14 |
| 8 | Task 2, with rows in Tasks 9 and 10 | 25 | Tasks 7 and 14 |
| 9 | Task 2, with rows in Tasks 7 and 10 | 26 | Tasks 7, 9, 10, 12, 13 (counts and bodies) and 14 |
| 10, 11 | Task 7 (the `connect` row in Task 9) | 27 | Task 2 (`UpgradeFromSpec0011Tests`) and every task's changed pins |
| 12, 13 | Task 9 | 28 | Tasks 16 to 18 |
| 14 | Tasks 9 and 13 | 29 | Task 18 |
| 15 | Tasks 8 and 11 | 30 | Task 19 |
| 16, 17 | Task 10 | 31 | Task 15 (mails from the mail catcher: Amendment A13) |
| 32 | Task 4, used by every test above | 33, 34 | the suites (final review, verifiers); Task 20 |

**The probe's traps**, each where it is handled: U1 Tasks 4 and 6; U2, U3, U6, U12 Task 5 (tests in Task 6); U4 Task 6; U5 Task 3; U7 Task 2; U8
Task 1; U9 Task 4 (residual risk 63 in Task 20); U10 Task 2; U11 Tasks 7, 9, 10, 12; U13 Tasks 4 and 5; U14 Task 12; U15 Task 1.

**Earlier tests that pin what this spec changes**, each changed by name in the task that changes the behaviour: `/auth/me`'s members
(`OrgEndpointTests.Me_returns_the_callers_current_data_from_the_database`, `OpenApiTests`' `MeResponse`, Task 2); the cookie test of
`OpenApiTests` (Task 2); the counts of audit kinds (`AuditTableTests`, `AuditProviderFlowsTests`, Task 7) and of OpenAPI operations
(`OpenApiHardeningTests`, Tasks 7, 9, 10, 12; the deletion's schema, Task 13); the provider names (`ManifestProvidersTests`,
`ProviderOptionsTests`, `ProviderPathsTests`, `ProviderStartTests`, `ProviderCallbackErrorTests`, `FakeOidcTests`,
Tasks 3 and 4); the link's mail and `via` (`ProviderLinkTests`, `AuditProviderFlowsTests`, Task 8); the start's purposes (`ProviderStartTests`,
Task 9); the token-endpoint paths (`LoginScopeTests`, Task 10); `scripts/e2e-providers.sh` (Task 15); the sample's specs that used `github` as an
unknown name or the "Google or Microsoft" sentences (Task 16), the flag's shape (Task 17), the deletion on the notes screen (Task 18) and the
Playwright specs of 0010 and 0011 (Task 19).

**Placeholder scan.** No step says "TBD", "similar to" or "add error handling". A few steps tell the implementer to follow an existing helper
whose exact name this plan could not see from the anchors (the notes spec's `open`, the `draw` host of `provider-buttons.spec.ts`, the
`/signup` and `/invite` tests of `providers-on-screens.spec.ts`, `samples/notes-web/README.md`'s list of screens): each names the file and the
shape to copy. No code comment cites a task or the plan.

**Type consistency across tasks.** `SessionValues`, `ConfirmationWindow` (Task 1) → `DeviceSession`, `DeviceSessions.FindAsync/ReadAsync/
EndWindowAsync` (Task 2) → `DeviceSessions.ConfirmAsync`, `SettingsCaller`, `SettingsCallers.ReadAsync/ReadAgainAsync/PasswordIsRight/
RefuseAsync/Entry` (Task 7) → used by Tasks 9, 10, 12, 13; `MailNotices.QueueAsync/Notify`, `MailLimitRule` (Task 8) → Tasks 9, 10, 12;
`ProviderLogins.HasLoginOfProviderAsync` (Task 8) → Tasks 9, 10; `SettingsRequestHandler.UnlinkPath/PasswordPath`, `SessionRenewal.CanRenew/
EndSessionsAsync/Issue` (Task 10) → Tasks 12 and 14; `ProviderLogins.HoldAsync` (Task 11); `ShareLockInterceptor.Pattern` (Task 11) → Task 12; `SessionRowFault` (Task 10) → Task 12; `reauthTicketOf` (Task 18);
`OrgApi` (Task 13) → Task 14; the test helpers `SettingsApi` (Task 2), `GitHubPerson`, `ProviderTestBase(..., github: true)` (Task 6). In the
sample: `RENEWAL`, the settings calls and `Me.confirmed_until` (Task 16) → Tasks 17 and 18; `armReauth(target)`/`takeReauth()`,
`rememberPendingConnect`/`takePendingConnect`, `ASSIGN_LOCATION` (Task 17) → Task 18; `withWindow` (Task 16) → Task 17.

**The Unicode-escape rule.** The two test bodies that need a NUL escape build it from `(char)0x5C` (`AccountConfirmTests`,
`AccountPasswordTests`); the sample's password helpers with their escapes are moved by a script that copies the lines (Task 16). Every task's
hand-back scans its files.

## Decisions taken in this plan

Each has its reason in the task named; the list is for the reviewer.

1. **The order** follows the brief's, with these moves: the upgrade test sits with the session of this device (Task 2), since both read the
   stored values; the link's `provider_in_use`, mail and `via` go with the notices (Task 8), the first change that queues one; the `FOR SHARE`
   hold of a sign-in comes after the unlink (Task 11), because its race tests need the unlink; GitHub through the exchange, link, finish and
   invitation (`GitHubFlowTests`) joins the limits and the audit flows (Task 14), so that Fri 08.05 keeps four tasks; the e2e script comes
   before the sample (Task 15), since it needs no screen; notes-api's `DELETE /api/org` goes with "Delete company" (Task 18). 20 tasks.
2. **The session values** are a table written by `AtomicSignInResult`, which creates the authorization of a new session itself; the session of
   this device is found by the token manager, then read afresh without tracking, and read again by the token's id under a lock (Tasks 1, 2).
3. **One helper reads the caller of every settings endpoint** (`SettingsCallers`), first and again under the address lock, where its answer
   counts (Task 7). The password is judged outside the lock; a ticket is used up under it with the clock read after it (Tasks 7, 9, 13).
4. **The connect reads the window again under the lock** with the session, besides steps 5 to 7 (Task 9): a window ended while the request
   waited is `403 confirmation_required` and the ticket is given back. The `204` of step 5 clears `auth_pt` too, as every answer that uses a
   ticket up does.
5. **The notices** share spec 0004's limit store and rule engine with a rule of their own (`MailLimitRule`: no gap, 10, one hour) under the key
   `MailKind.NoticeLimit` (100); they are written on the change's context and the dispatcher is woken after the commit; the three kinds join the
   reset's branch of both dispatcher statements (Task 8).
6. **The renewal** is one helper for the unlink and the password change: the cap is checked before anything changes, every refresh token and
   authorization is revoked, then the new session is issued with the old start and window (Task 10). One placeholder handler at `+400` claims
   both token paths, and the shaper learns both.
7. **The lock orders**: the unlink takes the address lock, deletes the login rows, revokes, then reads the account `FOR KEY SHARE`; the
   password change takes the address lock, re-reads the session and the membership, deletes the mail links, reads the hash `FOR SHARE`, updates
   (Amendment A2). A reset and either of them never wait for each other in a cycle (Tasks 10, 12).
8. **A sign-in holds its login** in `ProviderSignIn.LinkedAsync`, the one place where the exchange and the link sign a linked identity in
   (Task 11).
9. **`DELETE /auth/org`** passes its ticket step into `CompanyDeletionService` as a delegate run after the company lock and before the name
   check, and verifies the password by its hash only, as `DELETE /auth/account` does (Task 13).
10. **The limits**: the three `login` paths are named in `RatePolicy` beside the provider token paths, which stay as they are (Task 14).
11. **The e2e script** copies the helper block of `scripts/e2e-providers.sh` and reads the mails from the mail catcher and their count from the
    notice limit's row (Task 15; decision 15).
12. **The sample's settings calls** live on `AuthService`; the interceptor holds every request to `/auth/` while an unlink or a password change is
    in flight, and handles `401 session_required` once (Task 16). The flag of a round trip is `<target>:<epoch ms>`; a connect starts only with
    the window open; the provider of a pending connect is kept by name and time in `sessionStorage`, for 10 minutes (Task 17). The deletion moves into a component of its
    own on `/settings`; "Delete company" is a component on `/team` for holders of `org:delete` (Task 18). The sentences for an unknown provider
    name the three providers.
13. **The browser test** ends a window through the browser's own cookie jar and the testing endpoint (Task 19).
14. **The earlier tests that used `github` as an unknown name** use `gitlab`; the overlay of the fake carries GitHub's keys and the testing
    setting from Task 3 on; the fake GitHub is a class of its own beside the fake OpenID server (Tasks 3, 4).
15. **Criterion 31 reads the mails from the mail catcher** (the orchestrator's decision D41, Amendment A13 of the spec). The dispatcher of a
    live stack sends a queued row within a second of the commit and deletes it, so a read of `MailRequests` after the answer races it:
    `scripts/e2e-settings.sh` reads the three mails (recipient, subject, no link) from Mailpit and counts them through the notice limit's row
    (`MailRequestLimits`, kind 100); the queue rows themselves are asserted by `SettingsMailTests` with the dispatcher run by hand (Task 15).
16. **A reset that commits before a password change takes the address lock makes the change `401 session_required`** (the orchestrator's decision
    D42, the third outcome of Amendment A1): the reset ended the change's session, and the session re-read comes first under the lock. The first of
    the two wins in every order and none is a `500`; two changes at once still give one `200` and one `401` (criterion 19; Task 12, with a race
    test for each of the three outcomes).
17. **The streak of a password change is cleared where the right password is recognised**, on the connection of `LoginStreakStore`, and not
    again inside the address-lock transaction: the rule of `AccountDeletionEndpoint` is that the streak is never written on the request's own
    context there (Task 12). Both the spec's "cleared" and criterion 18 are met.
18. **The sample's "Delete account" and "Delete company" sections get the reauth ticket from their routed page** (`SettingsPage`, `TeamPage`), which
    reads the navigation state in a field initializer and passes it down as an input (Task 18), because a child created later no longer sees the
    navigation; their specs open the page through a real navigation with state.

## Open questions for the owner

1. **The real-keys test** (owner item, as for spec 0011): a real GitHub OAuth App on the deployed origin: the round trip, `iss` on the answer,
   the primary-address rule, the revocation with Basic credentials (`204`), the token gone from the app's authorized list, and the state cookie
   across GitHub's redirect in Safari on a phone.

---

## Amendment B (24.11.2026, after specs 0015-0017)

Plan file: `docs/superpowers/plans/0012-github-and-managing-logins.md`. Line numbers are those of the plan as it stands now (16,577 lines).
"Replaces lines a-b" means: delete those plan lines and put the new text there. Everything quoted as "main" was read in `C:/Users/mwppl/code/Auth-Core`
(v0.3.0). Where main's code and the probe report (`reconcile-probe.md`) disagree, the code wins and the line says so.

Tasks 1 to 13 are done code. They change in the merge worker's commits (fix commit A before Task 14, see the "Merge worker" list at the end). This
amendment changes Tasks 14 to 20 and the Global Constraints.

Counts: tasks changed 7 (14 to 20; Task 17 only by two run-time rules, no changed step); Global Constraints changed (5 places).

---

### Global Constraints, added (replaces nothing; insert as a new block after line 69, before "OpenAPI")

Edit the plan's Global Constraints (line 43 on) as follows.

1. **Lock order.** Replaces the Password/Renewal text at lines 63-64 by the same text plus this sentence:
   "Lock order for every path that takes more than one lock (owner decision F1, 24.11.2026): the company lock, the address lock, `IdentityLock`
   (only where a path uses it), the membership row(s), the account row, the login rows. A settings endpoint takes the caller's membership row
   (`SpaceLocks`, `SpaceUse.Hold`) before the account row. `ProviderLogins.HoldAsync` keeps its order (account `FOR KEY SHARE`, then the login row
   `FOR SHARE`); the sign-in that calls it already holds the membership first."
2. **The token's company.** Insert: "A settings endpoint acts in the company of the access token's `org_id` (`MembershipReader.ReadInCompanyAsync(userId,
   companyId)`); a token without `org_id` is `403 no_membership`. `ReadSignInSpaceAsync` is for sign-in only, never for a settings endpoint."
3. **The renewed session keeps the company** (owner decision F2). Insert: "`SessionRenewal.Issue` copies the claims `session_org_id` and
   `session_org_joined` of the caller's token, the start and the window of the old session, so after an unlink or a password change the new
   tokens are in the company the caller is in now. It does not read `caller.Tenant` for this."
4. **Counts, measured.** Line 69 "Audit: 32 kinds" becomes "Audit: 33 kinds" and line 70 "OpenAPI: 40 operations, 28 under the charset guard" becomes
   "OpenAPI: 43 operations, 30 under the charset guard". Baselines at lines 119-120 (I7, I13): replace the two baseline counts with the same pair
   (main is 29 kinds, 39 operations, 26 covered; spec 0012 adds 4 kinds and 4 operations, and 0 covered operations of its own beyond the 4 POSTs).
   Rule: a task that writes a count first runs the count test and writes the number it printed; the numbers above are the expected result, not a
   substitute for the run.
5a. **The schedule block is old-world.** The schedule table (plan lines 183-206), "Contract counts that change" (lines 209-217: 28 -> 32, 36 -> 40, 24 -> 28) and the backdated day slots (lines 196-204 and the "off days") are replaced by one
   sentence: "After the merge: 33 audit kinds, 43 OpenAPI operations, 30 under the charset guard, each measured when written; tasks 14 to 20 run in order with real dates."
5. **Dates.** Insert: "From Task 14 on, commits carry real dates. The headings 'Day 4' (line 11156) and 'Day 5' (line 14740) are deleted; tasks run in
   order. The earlier text of the plan that says 'Mon 11.05' or 'Wed 13.05' is history."
6. **Names that changed on main** (insert as a table the implementer reads first):

| Plan says | Main has |
| --- | --- |
| `12-settings.spec.ts` | `14-settings.spec.ts` (main has `12-orgs`, `13-create-org`) |
| `settings.companyMember` text | `texts.orgs.companyMember(names)` |
| risks 62, 63, 64 of the threat model | risks 71, 72, 73 (main ends at 70) |
| `CompanyDeletionService.DeleteAsync(actor, id, name, ct, confirmInside)` deletes every member's account | main deletes only accounts with no other space ("doomed"); the others ("keepers") stay |
| `RatePolicy` class | `RatePolicies` (static) with `Classify`; `RatePolicy` is the enum |
| plan line 115 "residual risk 63" (the Checked section) | "residual risk 72" |
| `CHANGELOG.md` `## Unreleased` | main has no `## Unreleased` heading: the top heading is `## 0.3.0 - 2026-11-23`; Task 20 adds the heading |

---

### Task 14 (plan lines 11158-11633): changed, 3 steps (1, 3, 4)

**Replaces lines 11171-11172** ("Contract counts"): "`RatePolicyTests` and `RateLimitMiddlewareTests` gain the five settings paths;
`JsonCharsetTests` gains the four settings `POST`s (covered: 26 + 4 = 30 after the merge, measured); `AuditSettingsFlowsTests` pins 33 kinds."

**Files, added:** Modify (tests) `tests/Auth.IntegrationTests/AuditProviderFlowsTests.cs` (line 40 `Assert.Equal(29, AuditKinds.All.Count);`),
`tests/Auth.IntegrationTests/OrgCreateTests.cs` (line 134 `Assert.Equal(29, AuditKinds.All.Count);   // no new kind`), `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`
(lines 119 `Assert.Equal(39, operations.Count);` and 153 `Assert.Equal(26, covered);`), `tests/Auth.IntegrationTests/OrgDeletionProviderTests.cs`,
`tests/Auth.IntegrationTests/AccountDeletionProviderTests.cs` (main's class for `DELETE /auth/account` with a ticket; its `The_provider_ticket_branch_answers_company_member_too` already pins private-plus-company with the ticket kept).

**Step 1 (tests), changes:**

(a) In `AuditSettingsFlowsTests.The_four_new_kinds_are_in_the_contract` (plan line 11476) replace `Assert.Equal(32, AuditKinds.All.Count);` with
`Assert.Equal(33, AuditKinds.All.Count);`. Before writing 33, run `AuditKinds.All.Count` once (the count test run of Step 2); write the printed value.

(b) New step block "The pins that count": run `grep -rn "AuditKinds.All.Count" tests/`. Every hit that says 29 becomes the measured number (expected 33):
`tests/Auth.IntegrationTests/AuditProviderFlowsTests.cs` line 40 (`Assert.Equal(29, AuditKinds.All.Count);`) and `tests/Auth.IntegrationTests/OrgCreateTests.cs` line 134 (its comment `// no new kind` is correct there and stays: the test says 0016 made no kind;
change the number only if the test still passes for that meaning, otherwise rename the test `The_audit_kinds_are_the_ones_of_the_contract` and drop
the comment; stop and report if you are unsure). `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs` line 119 becomes 43 and line 153 becomes 30 (measure first).

(c) **F12, company deletion with a ticket against the 0015 rule.** In `OrgDeletionProviderTests` add two tests (they use `CompanyWithAdminAsync`,
`OrgApi.DeleteWithTicket` and the helpers of Task 13; a second membership is made the way main's
`AccountDeletionOnRemovalTests.A_member_with_another_membership_keeps_the_account_and_the_other_session` does it: `CreateCompanyAsync` plus `AddMembershipAsync`):
- `A_member_who_keeps_another_space_keeps_the_account_when_the_company_is_deleted_with_a_ticket`: company "Beta" has an admin without a password (GitHub
  identity) and a second member M who also has a personal space; the admin deletes Beta with a ticket; expected 204, M's `AspNetUsers` row still exists,
  M's membership in Beta is gone, M's other membership remains, and `provider.reauthenticated` has 1 row (the delete endpoint writes it).
- `Only_accounts_with_no_other_space_are_deleted_with_the_company_and_a_ticket`: Beta has the admin (no other space) and M2 (no other space): both rows
  gone (`org.deleted` 1 row; main's details are `members`, `invitations`, `roles`, there is no `keepers`: assert `members` = 2).
- Not added: a test of "bad ticket and bad name" and a test of "a wrong name does not use the ticket up". Task 13 already has `A_bad_ticket_is_refused_before_the_name_is_checked` and
  `A_deletion_refused_after_the_ticket_was_used_up_gives_the_ticket_back("name"/"rule1")`; the hand-back says "exists: <name>".

(d) **F12, the description.** No test under `tests/` pins the description of `DELETE /auth/org` (`grep -rn "every member" tests/` is empty), so the premise "the class that pins it" is false. Add
`The_delete_org_description_names_the_accounts_that_stay` to `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs` (it already reads `operations["DELETE /auth/org"]["description"]`
at about line 56): the description contains `who has no other space` and does not contain `the account of every member`. The text, verbatim, so that test and description agree: main's sentence
at `src/Auth.Server/Api/TenancyEndpoints.cs` line 140 (`... every member who has no other space (the caller's included when this was their last). Members with another space keep their ...`, read
the rest of the sentence there) stays, and the merge worker appends exactly: ` With a provider ticket in place of the password, the ticket is checked inside the deletion, after the company lock and before the name.`
The p12 sentence ("the account of every member, the caller's included", p12 `TenancyEndpoints.cs` line 89) is dropped in the merge. `OpenApiHardeningTests` is in the Step 4 run list.

(e) **F17, the order on `DELETE /auth/account`.** Main's `AccountDeletionEndpoint.JudgeAsync` answers `403 company_member` (private space plus a company)
and `403 forbidden` (companies only) before it reads the body and before any ticket is looked at. The probe asked for "ticket first"; the code is the
other way and the owner's F11 text relies on `company_member` being the last guard. Keep the code. Main already pins the private-plus-company case with a ticket
(`AccountDeletionProviderTests.The_provider_ticket_branch_answers_company_member_too`: refused, ticket not used up, no `provider.reauthenticated` row, the same ticket deletes later) and the
company-only case without a ticket (`AccountDeletionCompanyMemberTests.An_account_with_companies_only_is_403_forbidden`). Add one test to `AccountDeletionProviderTests`:
- `A_company_only_account_is_403_forbidden_for_a_ticket_and_the_ticket_is_kept`: an account with a company membership only, with a fresh `reauth` ticket, sends `{"provider_ticket": ...}`
  to `DELETE /auth/account`: `403 forbidden`; `TicketCountAsync()` is still 1; `provider.reauthenticated` has 0 rows; then `POST /auth/account/confirm` with the same ticket answers 204 and writes
  `account.confirmed` (1 row, method provider) and still no `provider.reauthenticated` row (that kind is written only by `DELETE /auth/account` and `DELETE /auth/org`).

(f) **F16.** No plan step adds an `AccountDeletion` overload (checked with grep over the whole plan: the account deletion with a ticket uses
`AccountDeletionEndpoint` as main has it, with the ticket read after `JudgeAsync`). Nothing to change; the hand-back says "F16: no overload".

**Step 3 (limits), replaces lines 11589-11623.** The anchor in `src/Auth.Server/RateLimiting/RatePolicy.cs` is main's lines 68-72:

```csharp
        if (trimmed.Equals("/auth/login", StringComparison.OrdinalIgnoreCase)
            || ProviderPaths.TokenPaths.Any(tokenPath => trimmed.Equals(tokenPath, StringComparison.OrdinalIgnoreCase)))
        {
            return RatePolicy.Login;
        }
```

It is unchanged from the baseline, so the plan's Edit works as written. Differences to apply: the class is `public static class RatePolicies` (line 17), so the
field `LoginPolicyPaths` goes at the top of the class body (`NameOf` is already there at line 19, so it is not the first member); `RatePolicy` is the enum (do not rename). The `Refresh` branch on main also holds
`SessionPaths.Switch`: leave it. Main adds `OrgListEndpoint.Path` to the `Invite` branch (line 92): leave it. After the edit run
`RatePolicyTests` and read the number of `[InlineData]` rows; the policy-count test (F15) is the class `RatePolicyTests`: if a test counts the policies or
paths, write its measured value. The unlink (`/auth/account/logins/unlink`) stays `General`.

**Step 4 (run), replaces lines 11624-11629:** the class list gains `-class Auth.IntegrationTests.OrgDeletionProviderTests -class
Auth.IntegrationTests.AccountDeletionProviderTests -class Auth.IntegrationTests.AccountDeletionCompanyMemberTests -class Auth.IntegrationTests.OpenApiHardeningTests -class Auth.IntegrationTests.OrgCreateTests -class
Auth.IntegrationTests.AuditProviderFlowsTests`. Mutate and see a test fail, then restore (added): make `confirmInside` run before the company lock (Task 13's `A_ticket_gone_when_the_lock_is_taken_is_refused_before_the_name_is_checked` and `A_caller_who_lost_org_delete_while_the_deletion_waited_for_the_company_lock_..._the_ticket_stays` fail: they are the order guards, the two tests this amendment does not add would not fail); delete every member's account in
`CompanyDeletionService` instead of the doomed ones (`A_member_who_keeps_another_space_keeps_the_account...` fails).

New guard tests, Task 14: `A_member_who_keeps_another_space_keeps_the_account_when_the_company_is_deleted_with_a_ticket`,
`Only_accounts_with_no_other_space_are_deleted_with_the_company_and_a_ticket`, `The_delete_org_description_names_the_accounts_that_stay`,
`A_company_only_account_is_403_forbidden_for_a_ticket_and_the_ticket_is_kept`. That is 4 (main and Task 13 already own the other order guards).

---

### Task 15 (plan lines 11635-12173): changed, 4 steps (1, 2, 3, 4)

**Step 1, `scripts/e2e-providers.sh` (replaces lines 11659-11768 piece by piece; edit with the Edit tool, LF).** Main's file differs from the baseline the plan
quotes, so each anchor is re-derived:

(a) Header, main lines 10-11 read
`#   1. health is Healthy; the fake answers; GET /auth/options -> {"signup":"self-service","private_signup":"self-service",` and
`#      "providers":["google","microsoft"],"org_creation":"self-service"}; the OpenAPI description names the five provider paths - criteria 2, 29.`
Replace the second line's `"providers":["google","microsoft"]` with `"providers":["google","microsoft","github"]` and `names the five provider paths`
with `names the six provider paths` only if the check in (c) says six (see (c)).

(b) The options check, main line 380: replace

`expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft"],"org_creation":"self-service"}'`

with

`expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft","github"],"org_creation":"self-service"}'`

(the key `org_creation` stays last; main's server writes `providers` before it, as the old string shows). The next line `expect_no_store "step 1: the options"` stays.

(c) The provider-paths check and its pass line, main lines 384-387: read the lines first (`sed -n 380,390p scripts/e2e-providers.sh`), apply the plan's
piece (c) to what is there; the count word in the pass line follows the number of paths the check lists.

(d) Step 2 (main lines 389-413) begins `call GET /auth/providers/github/start` then `expect_error "step 2: an unknown name" 404 not_found`: change `github` to
`gitlab` in the path and, if the message names it, in the label (plan piece (d)). The pass line at 413 is as the plan quotes.

(e) The new GitHub start block and its pass line go after the check of step 2's pass line as the plan says, with the label text unchanged.

(f) **Step 11 and 12 of main's script are not touched by this task, and their counts are derived, not guessed.** Main's step 11 (lines ~670-715): the
unlinked identity is `already_registered`, then a "pwjoin" join with PW's own Microsoft identity (memberships 2, `invite.accepted`). Main's step 12
(lines 720-721) counts `login.succeeded` rows with `details ? 'provider'` as 8. The 0012 edits to this script add no provider sign-in (the new GitHub
block is a start request only, with no callback), so the count stays 8. Derivation to put in the commit message: "8 (main) + 0 (the GitHub start is not
a sign-in)". If the implementer's run (the e2e verifier's) says another number, that is a finding.

**Step 2, `scripts/e2e-settings.sh` (replaces lines 11769-12166 where listed; the rest stands).**

(a) Header comment: the sentence "(NEW...ORG...)" is rewritten for what the script does now: "ORG's company is deleted with a GitHub ticket; ORG's account goes with
it (its only space), and the account row is gone (step 9)".

(b) Step 1 options string, replaces `{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft","github"]}` with
`{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft","github"],"org_creation":"self-service"}`. No manifest edit: `scripts/e2e-orgs-create.sh`
only comments (line 12) that `deploy/auth.yaml` turns `org_creation` on, and the stack manifests `deploy/auth.yaml` line 26 and `samples/notes-api/auth.yaml` line 17 already say `org_creation: self-service`;
`e2e-settings.sh` writes no manifest. Do not copy that script's `"providers":[]`.

(c) **F2 step (new).** After the plan's step 8 (PW's sign-up, which creates PW only there) and before step 9, add "step 8b: the renewed session stays in the company". (In the plan script
step 6 is NEW's disconnect with the renewed session and step 7 is NEW's first password through the reset mail; step 8 is PW's sign-up. The header list "eleven steps" at plan line 11770
becomes "twelve steps", and the header comment of the script lists 8b.) The person is a new account "SW", made inside 8b by copying step 8's inline sign-up code (no helper exists): a
`json_body` with `E2E_SW_EMAIL` (declared with the other addresses in the set-up, a fresh address of the same domain), `POST /auth/signup`, `wait_mail "$SW_EMAIL" "Complete your sign-up" 90`,
`token_body "$SIGNUP_LINK" "$tmp/sw.complete" E2E_PASSWORD`, `POST /auth/signup/complete`, `keep_session sw`. Then:
1. `POST /auth/orgs` with `{"name":"Switch Co"}` through `settings`-style call with SW's bearer (201, entry id C).
2. `POST /auth/switch` with `{"org_id": C}` and SW's refresh cookie. The answer's shape is checked first: `keep_session` requires the body keys to be exactly `['access_token','status']`;
   if the switch answer has other keys (read `Sessions/SwitchEndpoint.cs`), keep the token with a plain `printf 'Authorization: Bearer %s
'` into `$tmp/sw.auth` and the cookie with `pair_of "$(set_cookie_line auth_rt)" > "$tmp/sw.rt"`.
   Either way the rotated cookie is kept again after the switch.
3. `GET /auth/me` with SW's new token: `org_id` is C; record `confirmed_until` as W1. The sign-up completion opened the window, so W1 is not `None`; if it is `None`, `fail` (do NOT confirm with the password: a
   third `account.confirmed` row would break step 11's `github,password` assertion).
4. `POST /auth/account/password` with SW's token and cookie: `{"current_password": E2E_PASSWORD, "new_password": a new value}`: 200; keep the new token and re-keep the rotated cookie (again with the shape check).
5. `GET /auth/orgs` with the new token: the entry with `"current":true` has id C (and not the personal space). `GET /auth/me`: `org_id` is C and `confirmed_until` equals W1.
6. Pass line: "step 8b: after a password change in a switched company the renewed session is still in that company, with the same window".
Counts that change because of this step: `login.succeeded` with `details.via = 'session_renewal'`: 2 -> 3 (SW's password change renews once); `password.changed`: +1 (derive from the query step 11 uses);
`org.created`: +1 if step 11 counts it. SW's notice mails: 1 (password changed) on SW's own limit row, so step 10 gains the assertion "SW = 1"; NEW = 2, PW = 1 and ORG = 0 do not change. Update step 11's expected
`login.succeeded via session_renewal` from 2 to 3 and say so in the comment above the query.

(d) Step 9 (ORG deletes the company with a GitHub ticket): add one assertion after the 204. Under spec 0015 the account is deleted only if the company was its
only space, which is true for ORG, so the account's row is gone: `GET /auth/me` with ORG's old token is `403 permissions_changed` (the old token of a deleted account, as main's `scripts/e2e-tenancy.sh` step 7 expects) and the script's `psql` helper counts `"AspNetUsers"`
rows with `"NormalizedEmail" = upper('$ORG_EMAIL')` = 0 (use the same helper step 11 uses for audit queries). Add a second company holder in the same step? No: it would change step 10's notice
counts. The keeper case is covered by `A_member_who_keeps_another_space_keeps_the_account_when_the_company_is_deleted_with_a_ticket` (Task 14).

(e) Step 10 notice counts: NEW = 2, PW = 1, ORG = 0 stay (the switch and the new company send no mail; 0017's join mail is not in this script, and the kind of
the notice limit is 100, not the 0017 mail kinds: F14 is closed by Task 14's check below).

(f) The helper block copied from `scripts/e2e-providers.sh` is the MERGED main version. The wait for the mail limit is not in that block: it is inline in step 11 of that script (about lines 644-645, "the mail limit of PW's second
invitation"). Where `e2e-settings.sh` needs a wait of that kind, copy the inline lines; there is no helper to grep for.

(g) The header text of the plan (lines 11677-11680, the sentence about GitHub's 302 with `user:email`, S256, a state and no nonce) is applied as the plan has it; it is item 2 of the header list. The pass line says "six provider
paths" always (the connect path is the sixth), not "if (c) says six". `12-settings` also appears in the plan at lines 176, 201, 11764-11766 (the `e2e-web.sh` header comment) and 16435 (the README text of Task 20): each becomes `14-settings`.

**Step 3 (check), replaces lines 12167-12170:** `bash -n scripts/e2e-settings.sh`, `bash -n scripts/e2e-providers.sh`, `bash -n scripts/e2e-web.sh`, and
also `bash -n scripts/e2e-orgs.sh scripts/e2e-orgs-create.sh` (unchanged; to be sure nothing was edited by mistake); `file scripts/e2e-settings.sh` says no CRLF.
F14: the enum is `src/Auth.Infrastructure/Persistence/MailKind.cs`; main has the values 1 to 4 and no 0017 kind, and p12 adds `NoticeLimit = 100`. `grep -n "= 100\|NoticeLimit" src/Auth.Infrastructure/Persistence/MailKind.cs` must show 100 once; if a
0017 kind equals 100 after the merge, stop and report.

**Step 4 (hand back):** the file list gains nothing; the e2e web header comment of `scripts/e2e-web.sh` names `e2e/14-settings.spec.ts`.

New guard assertions, Task 15: step 8b (`after a password change in a switched company the renewed session is still in that company, with the same window`),
step 9's row-gone assertion. That is 2.

---

### Task 16 (plan lines 12177-13564): changed, 5 steps (2, 5, 7, 8, 10)

All anchors below were checked against main's `samples/notes-web`. Every Edit anchor of Steps 4, 6 and 8's file creation matches except the two named in Step 5.

**Step 2 (failing specs), added:** in `auth.service.settings.spec.ts` add

`it('a settings call asked while a switch is on its way waits for it and is sent with the switched token', ...)`: the test starts `auth.switchTo(otherId)`
(held open at `POST /auth/switch`), then calls `auth.changePassword(...)` and `auth.unlink('github')`; expect no request to `/auth/account/password` or
`/auth/account/logins/unlink` until the switch answer is flushed; after it, each request carries the new token. Reason: main's `switchTo` spends the same refresh
cookie as a refresh, and an unlink or a password change renews the session with it (the F2 rule, in the browser).

`it('a switch asked during a renewal is sent after the renewal answered', ...)`: an unlink is held at `POST /auth/account/logins/unlink`, `auth.switchTo(otherId)` is called, expect no `/auth/switch` request until the unlink answer is flushed.
`it('a renewal asked while a refresh is on its way waits for it', ...)` exists in the plan's Task 16 text as `a refresh asked while a renewal is on its way...`
(the interceptor gate holds `/auth/` calls while a renewal runs); the new test is its mirror. Name it exactly as above.

The settings spec's `open()` helper must expect no call it does not make: where `verify()` names `/auth/orgs`, call `answerOrgs(ctrl)` (main's
`src/testing/helpers.ts`, added by 0015) right after the `/auth/me` flush. The page of Task 16 does not read the list itself (the delete section does, in Task 18), but
only a sign-in and the resume of a session call `loadOrgs` (`auth.service.ts` lines 300 and 388); `signedInAs` (= `holdToken` + `loadMe`) makes no orgs call. So the trigger is exactly: where a `verify()` names `/auth/orgs`.

**Step 5, the person, the failures and the calls (replaces lines 12850-13100 where listed).**

(c) The `Failure` union. Main's lines 25-50 already hold `company_member`, `company_limit_reached`, `org_creation_disabled`, `permissions_changed`,
`private_account_exists`. The plan's anchor (`| { kind: 'reauth_required' }` followed by `| { kind: 'too_many_attempts' ...`) MISSES: main has
`| { kind: 'reauth_required' }` (line 43) followed by `| { kind: 'org_creation_disabled' }`. Replace

```ts
  | { kind: 'reauth_required' }
  | { kind: 'org_creation_disabled' }
```

with

```ts
  | { kind: 'reauth_required' }
  | { kind: 'session_required' }
  | { kind: 'confirmation_required' }
  | { kind: 'provider_in_use' }
  | { kind: 'last_login' }
  | { kind: 'password_not_set' }
  | { kind: 'not_found' }
  | { kind: 'org_creation_disabled' }
```

(read the two lines in the file first; keep their exact spelling and the `\r\n`). Do not add `permissions_changed` or `company_member`: they exist.

(h) `refresh()` and `exchange()`. The plan's anchors (`this.refreshing ??= this.exchange().finally`) MISS. Main has `refresh()` at about lines 437-441:
`this.refreshing ??= this.renew().finally(...)`, where `renew()` waits for `this.switching` in a loop and then calls `this.exchange()`. Re-derive in this order:
1. Read `refresh`, `renew`, `exchange`, `switchTo`, `callSwitch` and the `switching` and `refreshing` fields (about lines 160-260 and 430-520).
2. `exchange(forRenewal = false)` takes the flag as the plan says and sets the `RENEWAL` HttpContextToken on the `POST /auth/refresh` call when it is true.
3. Write exactly this (main's `refresh()` calls `renew()`, which loops on `this.switching` and then calls `this.exchange()`; the loop stays in `renew`, unchanged):
```ts
refresh(forRenewal = false): Promise<RefreshResult> {
  if (forRenewal) {
    this.renewalRefreshing ??= this.renew(true).finally(() => { this.renewalRefreshing = null; });
    return this.renewalRefreshing;
  }
  this.refreshing ??= this.renew(false).finally(() => { this.refreshing = null; });
  return this.refreshing;
}
private async renew(forRenewal: boolean) { /* the switching-wait loop of main, unchanged */ return this.exchange(forRenewal); }
```
   `renewalRefreshing` is a new field (`Promise<RefreshResult> | null`); a plain refresh in flight is not mistaken for a renewal, and the renewal's flag reaches `exchange`.
   The converse race (a switch asked while a renewal is in flight): `switchTo` waits only on `refreshing` and `switching`, and the interceptor gate (Task 16 Step 6) holds every `/auth/` request, `/auth/switch` included,
   while `auth.renewal` is set; that gate is the guard, named by the test `a switch asked during a renewal is sent after the renewal answered` (added to Step 2, in `auth.service.settings.spec.ts`).
4. The settings calls (`unlink`, `changePassword`) call `await this.waitForSwitch()` first, a small private method that is `while (this.switching) await this.switching;`
   (the same loop `renew` uses; extract it from `renew` and make `renew` call it, so one place holds the rule). Then they send.
5. `logout` and `deleteAccount` are not changed.

**Step 7 (texts), replaces lines 13202-13283 where listed.**
- The grep rule "replace `Google or Microsoft` in every sample spec and text" names these places on main: `texts.ts` line 42-43 (`providers.names`, `either`),
  line 143 (`provider_error` becomes `Google, Microsoft or GitHub could not sign you in. Try again.`, and the `GENERIC` constant of `provider-return.spec.ts` line 14 gets the same sentence), line 149 (`addressMismatch`), line 153 (`providerAlreadyLinked`), the comments in `provider-buttons.ts` lines 18 and 64 and in `auth.service.ts` line 17 (all three: say `a provider`), and the specs `provider-return.spec.ts` lines 14, 398 and 401, `provider-link.spec.ts` lines 85-93,
  `providers-on-screens.spec.ts` line 49 (anchor `['github', 'google', 'google', 'Microsoft']` matches), `providers-on-screens.spec.ts` for the invite text. After the edit,
  `grep -rn "Google or Microsoft" samples/notes-web/src` returns nothing, and `grep -rn "Google, Microsoft or GitHub"` returns exactly the places edited. Each pinned
  sentence changes to the same sentence with `Google, Microsoft or GitHub`.
- `providers.names` (main line 42) becomes `{ google: 'Google', microsoft: 'Microsoft', github: 'GitHub' } as Record<string, string>` and `either` becomes
  `'Google, Microsoft or GitHub'` (check `either`'s uses first: a sentence with two providers named uses `either`).
- Text (c) `settings.companyMember: "A company account is deleted by the company's admins."` is DROPPED. Do not add the key. The delete section (Task 18) uses
  `texts.orgs.companyMember(names)` of main (`texts.ts` lines 218-219). No other key of `settings` duplicates a key of `orgs` or `team`; `texts.spec.ts` checks the
  printable-ASCII rule: keep it.

**Step 8 (`pages/settings.ts`), replaces lines 13284-13552 where listed.** The page shows three sections in Task 16 and 17; "Delete account" is added in Task 18
as a component. The `settings-link` goes in `notes.ts` before the sign-out button: the anchor is main's line 107
`<button type="button" data-testid="sign-out" [disabled]="signingOut()" (click)="signOut()">{{ t.signOut }}</button>` (still matches the plan's anchor at 13518).
`app.routes.ts` anchors (line 38 `{ path: 'team', ...` and `import { SignupPage } from './pages/signup';`) match; `login.ts` `startNotice` matches.

**Step 10 (hand back):** the list of specs whose "Google or Microsoft" sentence changed is the one above (names, not "each spec").

New guard tests, Task 16: `a settings call asked while a switch is on its way waits for it and is sent with the switched token`,
`a renewal asked while a refresh is on its way waits for it`. That is 2.

---

### Task 17 (plan lines 13565-14739): unchanged, with two run-time rules

Its Edit anchors were checked on main for the files main has (`provider-buttons.ts`, `provider-return.ts`, `texts.ts`; `confirm-dialog.ts` is new). Anchors in files that tasks 8 to 13 and 16 created
(`sessionEnded`, `startNotice`, the settings page blocks) are not on main: grep misses on main at plan lines 13603 (a false positive: main has `expect(takeReauth(), value).toBe(false)` at `provider-buttons.spec.ts` line 139),
14224, 14468, 14499, 14522, 14549 and 14701 are checked after commit A and the Task 16 hand-back, not now; Task 18 lines 14945, 14970, 15230, 15239 and 15270 likewise.
- Rule 1: in its specs, where a `verify()` names `/auth/orgs`, call `answerOrgs(ctrl)` (see Task 16).
- Rule 2: the settings screen of Task 17 opens `confirm-dialog` for a window ended by a switch or a refresh: nothing to add, because F5 (refresh and switch keep the
  window) is true on main (`NextPrincipal` copies the authorization id).
- The `reauth` target `delete_account` is routed to `/settings` in Task 18 (Step 3 below), not here.

---

### Task 18 (plan lines 14742-15861): changed, 6 steps (2, 3, 4, 5, 6, 7)

The task moves "Delete account" from the notes screen to `/settings`. Main's notes screen has more in it than the baseline: the 0016 pre-check and the
`company_member` handling. The owner decided (F11): the section on `/settings` shows main's text `companyMember(names)` and no button for an account that is a member of any company; a private-only account deletes as main does today.

**Step 2 (calls and texts), replaces lines 14901-14978 where listed:**
- `auth.service.ts`: add `deleteCompany` after `deleteAccountWithTicket` as the plan says. Its doc comment sentence "the caller's account went with the company" is wrong after
  0015: it reads "the company's devices are signed out; the account stays when it has another space (spec 0015)". `permission_not_held` is added to `Failure` after
  `{ kind: 'not_found' }` (added in Task 16); `permissions_changed` already exists on main: do not add it again.
- Texts (a) and (b) as the plan has them, with this change: `deleteCompanyWarning` reads
  `"This deletes the company and all its notes. A member who has no other space loses the account too. It cannot be undone."` (owner-approved text, spec B15). Text (c) `settings.companyMember` is dropped (see Task 16).
- Text (b)'s anchor `    notAllowed: "You don't have access to this.",` still matches in main's `team` block (about line 222).

**Step 3 (`pages/delete-account.ts`), replaces lines 14979-15287. Rewrite the component from main's `notes.ts`, not from the plan's copy.** Read in this order:
`notes.ts` lines 24-45 and `reauthTicketOf` (the helpers; Task 16 moved the password helpers to `pages/password-input.ts`), the template block 160-182
(`delete-section`, `delete-warning`, `delete-form`, `delete-confirm`, `delete-cancel`, `delete-error`), the `moved()` block at about 131-133 (`account-moved`), the members at
about 213-224 and the methods `askToDelete`, `cancelDelete`, `deleteWithTicket`, `deleteAccount`, `stillInACompany`, `deleteNotes`, `leave`, `refused`,
`focusDeleteSection`. The component keeps all of them with these differences:
1. The test ids stay: `delete-account`, `delete-section`, `delete-warning`, `delete-form`, `#delete-password`, `delete-confirm`, `delete-cancel`, `delete-error`,
   `account-moved`, and `delete-company-member` for the text of an account that is in a company.
2. On creation the component calls `account.listOrgs()` once (`AccountApi`, main's `GET /auth/orgs`) and keeps `companyNames()` (main's helper: names of entries with
   `kind === 'organization'`). `@if (companyNames().length > 0)`: show `<p data-testid="delete-company-member">{{ texts.orgs.companyMember(names) }}</p>` and NO
   button (also for an account with companies only: owner decision, it does not use spec 0012's old sentence). `@else`: the button and the flows below. A list that cannot be
   read shows the button (the pre-check below catches it at the click).
3. `stillInACompany` stays as main has it as the pre-check of `deleteAccount` and `deleteWithTicket` (`GET /auth/orgs` before anything is deleted, a list that cannot
   be read deletes nothing and says something went wrong), and `refusedAsCompanyMember` stays in `refused` for the server's `403 company_member` after the notes went.
   The plan's version of both lacks them: do not copy it.
4. `leave` navigates to `/login` with `{ notice: 'deleted' }`.
5. The `ticket` input and `keepTicketOutOfHistory` are as the plan says (`reauthTicketOf(router)` moves to `pages/ticket-history.ts`; remove it from `notes.ts`).
6. `provider-return.ts`: the target `delete_account` goes to `/settings` (plan edit as written; the constant `DEFAULT_RETURN_URL` is not touched).

**Step 4 (`delete-company.ts`, TeamPage edits, `notes.ts` edits), replaces lines 15288-15566 where listed.** The `notes.ts` removals are re-derived from main's file:
remove the header button at line 92 (`<button type="button" data-testid="delete-account" (click)="askToDelete()">{{ t.deleteAccount }}</button>`, inside
`@if (person.org_kind === 'personal')`), the template block 160-182, the `moved()` block (lines ~131-133), the members `deleteForm`, `deleting`, `deleteBusy`,
`deleteError`, `notesGone`, `moved`, `reauthTicket` (lines ~213-224), the methods listed in Step 3 above, the helpers at the top that only the section used
(`BLANK`, `LONE_SURROGATE`, `MAX_BODY_BYTES`, `acceptablePassword` and `reauthTicketOf`: check each with grep before removing; the ones moved by Task 16 are already gone), and the imports
they leave unused. The org picker, `creating`/`companyForm`, `orgs`, the `orgChanged` effect and `orgCreation` stay. `texts.notes` keys `deleteAccount`, `deleteWarning`,
`deleteConfirm`, `cancel`, `wrongPassword`, `notesGone`, `movedToCompany`, `passwordRequired`, `reauthFailed` are used by the component now: leave them in `texts.notes`
(do not move them; the component reads `texts.notes`), and `texts.spec.ts` stays green. `canOpenTeam` edits are as the plan has them (anchors checked).

**Step 5 (specs), replaces lines 15567-15850.**
(a) `pages/delete-account.spec.ts`: move the two describes of main's `notes.account.spec.ts`: `NotesPage: "Delete account" (spec 0010)` (line 80) and
`NotesPage: "Delete account" and the companies of the account (spec 0016)` (line 300). The first is moved as the plan says (the four renamed tests keep the plan's new names).
The second is moved whole, with the helper `submitPassword`, `text`, `personal`, `company`; its four tests keep their names:
`delete_account_with_a_company_listed_shows_the_company_member_text_and_deletes_no_notes` (now: the section shows `delete-company-member` with the names when the page
loads and has no `delete-account` button; the test then needs no click, and it is renamed `a_company_member_sees_the_company_member_text_and_no_button`; keep the old
name as a second test that clicks through a list that arrives late: `delete_account_with_a_company_listed_shows_the_company_member_text_and_deletes_no_notes` (the list
is empty at load, the company appears at the click: the pre-check text appears, no `/api/me`, no `/auth/account`)), `delete_account_with_only_the_personal_space_still_deletes_the_notes_first`,
`a list that cannot be read deletes nothing and says something went wrong`, `delete_account_on_403_company_member_shows_the_text_with_the_names`. The plan's
test `a company member sees the sentence and no button (spec 0012)` is replaced by these:
- `a_company_member_sees_the_company_member_text_and_no_button`: open with a list of one company (Acme): `pageText` contains
  "You are still a member of Acme. Delete those companies or ask their admins to remove you, then delete your account." and `[data-testid="delete-account"]` is absent.
- `an_account_with_companies_only_sees_the_same_text_and_no_button`: the list holds a company and no personal space: the same text, no button.
- `a_private_only_account_sees_the_button`: the list holds the personal space only: `delete-account` is present, `delete-company-member` absent.
- `the_old_sentence_is_gone`: `pageText` never contains "A company account is deleted by the company's admins." (guards F11).
The `open(me)` helper opens `SettingsPage` as the plan has it, and flushes `/auth/orgs` after `/auth/me`
(`ctrl.expectOne('/auth/orgs').flush({ orgs })`, the list a parameter, default `{ orgs: [] }`).
(b) `pages/delete-account.provider.spec.ts`: the whole of main's `notes.provider.spec.ts` (66 lines changed on main since the baseline; read it, do not use the plan's
description), its `open(me, reauthTicket)` navigating to `/settings`, flushing `/auth/options`, `/auth/me` and `/auth/orgs`. Its two 0016 tests (lines 464 and 480 on main) move
with it and keep their names. Then delete `notes.provider.spec.ts`.
(c) `notes.account.spec.ts`: delete the two moved describes; the test at line 57 is replaced as the plan says; add the `org:delete` test as the plan says. `notes.orgs.spec.ts`
line 96 asserts `delete-account` is absent: it stays true.
(d) `provider-return.spec.ts` edits as the plan has them (anchors checked); the sentence edits of Task 16 already touch lines 14, 398 and 401.
(e) `pages/delete-company.spec.ts` as the plan has it. Add one test, `the_warning_names_the_members_who_lose_the_account`: the dialog text contains `A member who has no other space loses the account too` (guard of spec B15). Tests named there that assert `permissions_changed` use main's existing `Failure` kind.
(e2) `team.spec.ts` as the plan says.
(f) Add `notes.account.spec.ts` (or `notes.spec.ts`) one test: `the_notes_screen_has_no_delete_account_button_for_a_private_account`.

**Step 6 (run), replaces lines 15851-15856:** as the plan, plus: `grep -rn "delete-section\|delete-account" samples/notes-web/src/app/pages/notes*.ts` returns nothing; the e2e
specs (Task 19) are not run here. Mutate and see a spec fail, then restore (added): remove `stillInACompany` from `deleteAccount` (`a list that cannot be read deletes nothing...` fails); make the
component show the button when the list has a company (`a_company_member_sees_the_company_member_text_and_no_button` fails).

**Step 7 (hand back):** files gain `texts.ts` (not duplicated keys) and `provider-return.ts`; deleted: `notes.provider.spec.ts`.

New guard tests, Task 18: `a_company_member_sees_the_company_member_text_and_no_button`, `an_account_with_companies_only_sees_the_same_text_and_no_button`,
`a_private_only_account_sees_the_button`, `the_old_sentence_is_gone`, `the_notes_screen_has_no_delete_account_button_for_a_private_account`. That is 5 (the moved
tests keep their names).

---

### Task 19 (plan lines 15862-16124): changed, 4 steps (1-title, 2, 3, 5)

The file is `14-settings.spec.ts` (main has `12-orgs.spec.ts` and `13-create-org.spec.ts`). Every `12-settings` in the task, in Task 15's header, in Task 20's
README text and in the plan's file lists is replaced; run `grep -n "12-settings" plan` and change each.

**Heading and Files (lines 15862-15866):** `### Task 19: The Playwright spec 14-settings.spec.ts and the moved steps of 0010, 0011 and 0016`; Files: Create `14-settings.spec.ts`,
`support/settings.ts`; Modify `support/fake-provider.ts`, `support/accounts.ts` (exports `createCompany` and `picker`), `10-private.spec.ts`, `11-providers.spec.ts`, `13-create-org.spec.ts`.

**Step 2, added tests in `14-settings.spec.ts` (after the three of the plan):**
- `after a password change in a switched company the person is still in that company, with the window open` (F2, owner): sign up a private person with a password
  (`signUpPrivatelyByApi` and `signIn` of `support/accounts.ts` and `support/session.ts`, as `13-create-org.spec.ts` does), create a company with the picker's "New company..."
  (`createCompany(page, name)`: today a local function of `13-create-org.spec.ts` (line 19), as is `picker`; export both to `e2e/support/accounts.ts` and import them in `13-create-org.spec.ts` and `14-settings.spec.ts`), see `me-company` with the
  company's name, open `/settings`, change the password (`#current-password`, `#settings-new-password`, `#settings-repeat-password`, `password-submit`), see
  "Password changed.", then `page.goto('/notes')` and expect `me-company` to hold the company's name (not "Private account") and `picker(page)` to have the company selected;
  the window part of F2 is checked in `e2e-settings.sh` step 8b, not here. Both new tests leave a company behind: clean up in a `finally` with `deleteCompanyByApi(email, <the password in force: the NEW one after a change>, name)` of `support/accounts.ts`.
- `a person who is in a company sees the company_member text on /settings and no button`: from the same set-up, `openSettings(page)` then
  `expect(page.getByTestId('delete-company-member')).toContainText("You are still a member of ${name}.")` and `expect(page.getByTestId('delete-account')).toHaveCount(0)`.

The first test of the plan (`a person signs up with GitHub...`): after `signUpPrivately`, the notes screen may show the picker (a person with GitHub as the only login has one space):
no change. The third test (`an admin without a password deletes the company with "Confirm with Google"`) ends with "The account went with the company: the same Google identity is
a new person now": under 0015 this holds because the admin's only space was the company (doomed). Keep the assertion and add the comment `// the company was the account's only space`.
Add a fourth: `an admin who is also in another company keeps the account when the first company is deleted with "Confirm with Google"` is NOT added: it needs a Google identity with
two companies and `POST /auth/orgs` by API; it is covered by the API test of Task 14. Say so in the hand-back.

**Step 3 (moved steps), replaces lines 16064-16116, adds:** `13-create-org.spec.ts` line 74-110: the test `a private person who created a company, back in Private, sees the company_member
text and keeps the notes` clicks `delete-account` on the notes screen (line 100) and expects `delete-error` there. After the move there is no button on `/settings` for an account in a
company. The test is rewritten: after `picker(page).selectOption({ label: 'Private' })` and the note, `await openSettings(page)`; expect
`delete-company-member` to contain `You are still a member of ${name}.` and `delete-account` to have count 0; `await page.goBack()` (or `page.goto('/notes')`) and expect the note
visible. Then `deleteCompanyByApi(...)` as now, `await page.reload()`, `await openSettings(page)`, `delete-account` visible, click it, fill `#delete-password`, `delete-confirm`, expect
`/login`. The test name stays; the comment about "the sample has no screen for DELETE /auth/org" is changed to say the company is removed by the API for set-up. Add `import { openSettings } from './support/settings';`.
`10-private.spec.ts` (line 67-68) and `11-providers.spec.ts` (131-143) edits are as the plan says (anchors match main). After them, run `grep -n "delete-account\|Confirm with" e2e/*.spec.ts` (main's hits:
`10-private` 67-68, `11-providers` 131-143, `13-create-org` 74-100) and name each.

**Step 5 (hand back):** Proposed subject unchanged.

New guard tests, Task 19: `after a password change in a switched company the person is still in that company, with the window open`,
`a person who is in a company sees the company_member text on /settings and no button`, and the rewritten `a private person who created a company, back in Private, sees the
company_member text and keeps the notes`. That is 2 new and 1 rewritten.

---

### Task 20 (plan lines 16125-16465): changed, 4 steps (2, 3, 4, 6)

**Step 1 (keys guide):** unchanged, anchors checked on main (`docs/deployment/provider-keys.md` lines 1, 3, 4, 48, 112 match).

**Step 2 (threat model), replaces lines 16215-16306 where listed:**
- The risk numbers: the new residual risks are 71, 72 and 73 (main ends at 70). In the whole plan `risk 62` becomes `risk 71`, `risk 63` becomes `risk 72` and `risk 64` becomes `risk 73`
  (run `grep -n "risk 6[2-4]\|Risk 6[2-4]" plan`: the places are the table of "Managing logins" (rows 'Risk 62', 'risk 63', 'Risk 64'), item 57's `(risk 62)`, the self-review line 16488 `residual risk 63`).
  Add them "after `70.`" (not after `61.`).
- The audit row on main's threat model (line 88) says `The audit log: 29 kinds`: replace `29 kinds` with `33 kinds` (measured); `docs/operations/backup.md` line 252 lists the audit kinds and ends `That makes 29 kinds.`: add the four kinds (`account.confirmed`, `account.confirm_refused`, `provider.unlinked`, `password.changed`) and write the measured number; and the row of the contract counts in `docs/design.md` if it names 29.
- (a)-(c): the diagram anchor (line 35 on main) and `## Sign-in with Google and Microsoft` (line 160) and `## Account deletion and personal data` (line 191) match. The anchor
  `with providers on, Auth-Core to Google and Microsoft (outbound HTTPS, the client secret)` is at line 40.
- Items 2, 51, 52, 53, 55, 56, 57, 58 (lines 242, 293-300) are widened as the plan says; item 59 (line 301) is rewritten as the plan says.
- The `Managing logins` table row `E | A session outlives...` gains one row: `| E | A renewed session lands in a company the person did not choose | The renewed session keeps the
  company claims of the token that asked (`session_org_id`, `session_org_joined`) and the old start and window (`AccountPasswordTests`, `ProviderUnlinkTests`, ...)`: name the two tests the merge worker's fix commit
  writes (they are in the "Merge worker" list below). A second row: `| D | Two settings calls and a switch race for one refresh cookie | Every settings call that renews waits for a switch in flight in
  the sample; on the server the membership row is locked before the account row (the lock order) | |`.
- "Managing logins" first paragraph gains: "Settings endpoints act in the company of the access token; a token without a company is `403 no_membership`."

**Step 3 (Angular guide), replaces lines 16307-16362 where listed.** Main's `docs/integration/angular.md` has "Several companies (spec 0015)" (line 214) and "Creating another company (spec 0016)" (line 246);
the new section goes before `## The limits and an outage (spec 0008)` as the plan says (after `## Google and Microsoft (spec 0011)` at line 308, which the plan renames), and its text says:
- (b) The bullet `**"Delete account" without a password.**` is at line 342 and `**Deleting a company needs a password too.**` at line 349 (the anchors match). The `notes-web.reauth` flag text
  (line 324) quoted in the plan matches. Add to the account bullet: "A private-only account deletes as before; an account that is in any company shows 'You are still a member of ...' and no button (the
  `403 company_member` of spec 0016 stays the last guard)". The "Deleting an account that is still in a company" bullet of spec 0016 (line 268) is edited: the sample's section is on `/settings`, not on the notes screen.
- (c) The new section "The settings screen (spec 0012)" gains a bullet: "**The company.** A settings endpoint acts in the company of the access token's `org_id`; after an unlink or a password change the new
  token is in the same company, with the same window. A client must not call a settings endpoint while a switch is on its way (they share the refresh cookie): the sample's `AuthService` waits."

**Step 4 (deployment guide, design, changelog, README), replaces lines 16363-16435 where listed:**
- `docs/deployment/vps.md` anchors match main (lines 89, 91, 255, 257, 260, 327). `docs/design.md` anchor `*As built (spec 0011):*` is at line 376.
- **CHANGELOG.** Main has no `## Unreleased` heading: add it above `## 0.3.0 - 2026-11-23`, i.e. replace the line `## 0.3.0 - 2026-11-23` with
  `## Unreleased` + a blank line + the paragraph, `### Added`, `### Changed`, `### Upgrading` blocks (the plan's text, with `32 in all` changed to `33 in all`, and the first paragraph
  starting without the anchor `Sign-in with Google and Microsoft ([spec 0011]...` (that sentence is in the 0.2.0 block on main, line 85, and is not repeated)) + a blank line + `## 0.3.0 - 2026-11-23`.
  The sample line "**The sample**: GitHub buttons, `/settings`, ..." adds "Delete account moves to `/settings`; the notes screen has no account section".
  Add to `### Changed`: "**`DELETE /auth/org` with a provider ticket** deletes only the accounts that have no other space (spec 0015 rule), as the password path does."
- `samples/notes-web/README.md`: line 76 names `e2e/12-orgs.spec.ts`; the new sentence reads `e2e/14-settings.spec.ts` and goes after the sentence that names `/provider-return`;
  read the file first. The sample README also lists `notes.account.spec.ts` ... : after Task 18 the list gains `delete-account.spec.ts` and `delete-account.provider.spec.ts`.

**Step 5 (check):** unchanged.

**Step 6 (acceptance map):** the map `docs/superpowers/plans/0012-acceptance-map.md` names tests: add every guard test of this amendment that a criterion needs (criteria 21 and 28-31); the script
of the step stays. The names that Tasks 14, 16, 18, 19 add are listed above; the criterion rows are the orchestrator's to write if criteria are added to the spec (Amendment B items of the spec).

New guard tests, Task 20: none (documents), plus the acceptance map rows.

---

### Merge worker (not a plan task; the plan needs these tests, the worker writes the code)

The merge worker's commit A (before Task 14) changes done code: F1 lock order (`ProviderLogins.HoldAsync`, `AccountPasswordEndpoint`, the unlink, `SettingsCallers` with
`MembershipReader.ReadInCompanyAsync`), F2 (`SessionRenewal.Issue` keeps `session_org_id` and `session_org_joined`), F4 (`IdentityLock` between the address lock and the account row in
`AcceptThroughProviderAsync`), F12 (`confirmInside` after `guard.EnterAsync`, before the name check, in main's `CompanyDeletionService.DeleteAsync`, with main's keepers kept; description sentence).
The tests of tasks 15 and 19 that see F2 are named above. The tests the worker adds in commit A (names for the threat model row in Task 20):
`AccountPasswordTests.The_renewed_session_stays_in_the_company_the_caller_is_in` and `ProviderUnlinkTests.The_renewed_session_stays_in_the_company_the_caller_is_in`
(both: the account has a personal space and a company, the caller's token is for the company, the new tokens carry the company's `org_id` and the same start and window).
F3 is decided (owner): commit A also adds the lock to `ProviderConnectEndpoint.ConnectAsync` (the membership row, then the account row, in the lock order) and its race test
`ProviderConnectTests.A_connect_asked_while_the_account_is_being_deleted_answers_a_clean_4xx` (two connections, the account deletion held before commit; the connect answers a 4xx, never a 500). Task 14 does not touch it.
Commit A also changes the done test of Task 3 that pins the options JSON without `org_creation` (plan line 2327): its expected string gains `"org_creation":"self-service"` where the stack's manifest turns it on (read the test first; stop if the
test's factory does not set the manifest key).

---

### Open questions (one line each, owner)

None open.

Answered and removed: the company-only account (owner F11: same text, no button; the server answers `403 forbidden`); the `/team` warning text (owner: "This deletes the company and all its notes. A member who has no other space loses the account too. It cannot be undone."; spec B15); SW in step 8b (accepted); F3 (decided: lock in commit A).

### Problems found (code against the probe)

- F17: main refuses `company_member` before it reads the body and the ticket; the probe said "ticket first". The code stays; one guard test added in Task 14 (main already pins the private-plus-company case).
- Task 16 Step 5 anchors (c) and (h) miss on main; re-derived above.
- Task 18's `DeleteAccountSection` in the plan lacks main's 0016 pre-check and `company_member` handling; re-derived above.
- Task 14 `AuditProviderFlowsTests` and `OrgCreateTests` pin 29 kinds on main (the probe named only the OpenAPI counts); added to Task 14.
- `13-create-org.spec.ts` (main) clicks "Delete account" on the notes screen; added to Task 19.
