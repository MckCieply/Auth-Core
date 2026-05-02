# Google and Microsoft Sign-in Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0011
- **Date:** 2026-04-23
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0011-google-and-microsoft-sign-in.md`](../specs/0011-google-and-microsoft-sign-in.md) (with its Amendment of 2026-04-23,
  after the probe) — the plan argues from the spec; where they disagree, **the spec wins** and the disagreement is a finding (see
  [`docs/workflow.md`](../../workflow.md)). The Amendment (items A1 to A8, Q14 and "userinfo is off"; A8 and criterion 33 after the plan's review) was
  written together with this plan on the orchestrator's rulings; nothing else in the spec is changed by the plan. The choices the spec leaves open are in "Decisions taken in this plan" at
  the end; "Open questions for the owner" lists what the plan could not settle.
- **Based on:** `44b5ffc` (`main` at `b6c3d25` with spec 0011 on top of it). Every anchor of an edit below was checked against this base; an anchor
  in a file that an earlier task of this plan changes is written against the text that task leaves.

**Goal:** A person signs in to a product with Google or Microsoft instead of a password: a linked identity signs in at once, an address that
belongs to an account with a password links once with that password, a new person finishes creating an organization or a private account and is
signed in, an invited person accepts with the provider of the invited address, and a person without a password confirms the deletion of a private
account with the provider. Providers are off until a product lists them in its manifest and the operator gives the keys.

**Architecture:** OpenIddict's **client** joins the existing server and validation in one host (`AddProviderClient`): the web providers for
Google and Microsoft (`common` tenant), or plain OpenID Connect registrations of a **test issuer** in Development, in pass-through mode for the
redirection endpoint and its errors, with userinfo off and the instance's own key files protecting the state. **`ProviderCatalog`** decides which
providers are *offered* (listed by the manifest's new key `providers`, both keys set, the return address set) and feeds `GET /auth/options`, the
`404` of the start and `/auth/health`. **`GET /auth/providers/{name}/start`** challenges OpenIddict with the purpose (`sign_in`, `invite` with the
SHA-256 of the invitation token, `reauth`) in the encrypted state; **`GET /auth/providers/{name}/callback`** reads OpenIddict's result (the scheme
and host of the callback request are rewritten to the return address's origin before authentication, Amendment A2), maps every failure to one of
four fragment codes, reads the identity from the identity token alone (**`ProviderIdentities`**), stores a **provider ticket** (hash only, table
`ProviderTickets`) bound to the browser by the cookie **`auth_pt`**, and redirects to the product's return screen with the ticket in the fragment.
The three POST endpoints (**exchange, link, finish**) are token-endpoint requests of OpenIddict in pass-through mode with a placeholder request
(**`ProviderRequestHandler`**, as the sign-up completion does): an answer with `next` is plain JSON; a sign-in goes through `AtomicSignInResult` in
the transaction that uses the ticket up, and clears `auth_pt` once committed. Identities are Identity's `AspNetUserLogins` rows
(**`ProviderLogins`**). The account-making flows reuse the locks and services of specs 0009 and 0010: `SignupService.FinishThroughProviderAsync`
and `InviteAcceptance.AcceptThroughProviderAsync` hand their open transaction to the sign-in. `DELETE /auth/account` takes exactly one of
`password` and `provider_ticket`. A fake OpenID provider, **`tests/Auth.FakeOidc`**, serves Google's, Microsoft's and a generic issuer's endpoints;
the integration tests run it in process behind OpenIddict's HTTP client, the end-to-end stack as a container.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, ASP.NET Identity, OpenIddict 7.7.1 (server, validation and now the client:
`OpenIddict.Client`, `OpenIddict.Client.AspNetCore`, `OpenIddict.Client.SystemNetHttp` and `OpenIddict.Client.WebIntegration` all come with the
existing `OpenIddict.AspNetCore` metapackage through `OpenIddict` 7.7.1, so **no `PackageReference` is added** to `Auth.Server`), EF Core on
PostgreSQL 16 (Npgsql), xUnit v3 with Testcontainers; the fake provider uses ASP.NET Core and `Microsoft.IdentityModel.JsonWebTokens` 8.23.0
(already in `Directory.Packages.props`, used by the tests); Angular 21 (standalone components, signals, reactive forms), Vitest on jsdom and
Playwright (Chromium and WebKit); bash and Python 3 (standard library only) for the e2e script; Docker Compose. **No new third-party NuGet, npm or
pip package** (Settled 22).

## Global Constraints

Values copied from the spec, one line each; every task includes them.

- **Off by default.** A provider is offered when the active manifest lists it, its client id and its client secret are set (not blank) and `Auth:App:FrontendUrls:ProviderReturn` is set. An instance that does neither sees only: `GET /auth/options` gains `"providers":[]`, `GET /auth/me` gains `has_password` and `linked_providers`, `DELETE /auth/account` accepts `provider_ticket`.
- **General rules:** JSON bodies of at most 8 KiB in UTF-8, unknown members ignored, `400 invalid_request` for a body that is not a JSON object or a member of the wrong type; `Cache-Control: no-store`; the security headers; the `415` of another charset and the per-IP `429` come first.
- **Provider names:** `google` and `microsoft`, lower case, in paths, the manifest, answers and audit rows.
- **No secret value, code, state, identity token or ticket** in a log line or an audit row; the OpenIddict client's dispatcher logs at `Warning` (Amendment A1).
- **Manifest `providers: [google, microsoft]`:** each must be `google` or `microsoft` (any other value, `github` and `password` included, makes the manifest invalid: last valid one stays, `/auth/health` `Degraded`); duplicates folded, order kept (the order of the buttons); `providers:`, `[]` or no key is none; a stored manifest without the key is none.
- **Configuration:** `Auth:App:FrontendUrls:ProviderReturn` (`AUTH_FRONTEND_PROVIDER_URL`, optional; when set the rules of `CompleteSignup` and no `token` or `ticket` parameter; a bad value stops the start naming the key); `Auth:Providers:Google:ClientId`/`ClientSecret` (`AUTH_GOOGLE_CLIENT_ID`/`AUTH_GOOGLE_CLIENT_SECRET`); `Auth:Providers:Microsoft:ClientId`/`ClientSecret` (`AUTH_MICROSOFT_CLIENT_ID`/`AUTH_MICROSOFT_CLIENT_SECRET`); `Auth:Providers:Microsoft:ClientSecretExpiresOn` (`AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES`, `yyyy-mm-dd`, a bad value stops the start naming the key); `Auth:Providers:TestIssuer` (Development only; set elsewhere it stops the start naming the key).
- **Missing keys never stop the start:** a listed provider that is not offered logs one start warning naming the missing keys, never their values, and `/auth/health` is `Degraded`; configured and not listed is not offered and healthy. From 30 days before `ClientSecretExpiresOn`, and after it, `Degraded` and a start warning naming the key and the date.
- **The callback address** is the return address's origin plus `/auth/providers/{name}/callback`, from configuration only; the callback request's scheme and host are read from that origin (Amendment A2).
- **Start:** unknown or not offered `404 {"error":"not_found"}`; nothing = `sign_in`, `invite=<43 base64url>` = `invite`, `purpose=reauth` = `reauth`; a malformed `invite`, another `purpose`, both, or one of the two given twice `400 invalid_request`; other parameters ignored; reads no account and no invitation; `302` with code flow, PKCE `S256`, `state`, `nonce`, scopes `openid email profile`, `prompt=select_account` (reauth: Microsoft `prompt=login`, Google `max_age=0`); the state lives 10 minutes; the request-forgery cookie `OpenIddict.Client.State.<…>` is `SameSite=Lax`, `Secure`, `HttpOnly`, `Path=/auth/providers` (Amendment A4).
- **Callback:** `GET` only; success `302 <ProviderReturn>#ticket=<43>&purpose=<sign_in|invite|reauth>` and `auth_pt`; failure `302 <ProviderReturn>#error=<code>&purpose=<purpose>` (`purpose` left out when the state could not be read), nothing stored; `cancelled` (access_denied with a valid state and cookie, A3), `invalid_state`, `provider_unavailable`, `provider_error`; never a page of its own, never the provider's `error_description`; one log line at `Information` naming the provider and the code (a refusal also the OAuth error code the client recorded, such as `invalid_client`, for the runbook).
- **Identity:** Google key `sub`, address `email`, verified when `email_verified` is `true`; Microsoft key `{tid}:{oid}` (GUIDs in lower case), address `email` only, verified when `tid` is `9188040d-6c67-4c5b-b112-36a304b66dad` and `email` is present or `xms_edov` is `true` (JSON boolean, or the string `"1"`/`"true"`); the web-provider registration requires `iss` = `https://login.microsoftonline.com/{tid}/v2.0` (A6); an address that breaks `EmailInput.IsInvitable` is unverified; no `email` is no address; claims from the identity token only, userinfo off.
- **Tickets:** 32 random bytes, 43 base64url characters, SHA-256 only stored in `ProviderTickets` (hash, binding hash, purpose `smallint` 1/2/3, provider, identity key, address or none, verified, invitation hash, expiry); 10 minutes, `reauth` 5; the hourly clean-up deletes expired ones; used up by the request that signs in or deletes (one `DELETE ... RETURNING` in its transaction); a refusal and a `next` leave it usable; not tied to an account.
- **`auth_pt`:** 32 random bytes, `HttpOnly; Secure; SameSite=Strict; Path=/auth; Max-Age=600`; its SHA-256 on the ticket; missing or not matching is the answer for an unknown ticket; a request that uses the ticket up clears it.
- **Exchange / link / finish:** the orders of checks of the spec, word for word (Tasks 8 to 11); sign-ins answer `200 {"status":"authenticated","access_token"}` and the refresh cookie as `POST /auth/login` does; `next` answers are plain JSON.
- **`DELETE /auth/account`:** exactly one of `password` and `provider_ticket`; under the address lock after the membership is read again and before anything is counted: password account + ticket `403 password_required`, no password + password `403 reauth_required`; a ticket must be `reauth`, unused, live, with its `auth_pt`, of an identity linked to this account, else `403 reauth_failed` with `account.delete_refused` (`reauth_failed`); `provider.reauthenticated` in the deletion's transaction; the answer clears `auth_rt` and `auth_pt`.
- **`GET /auth/me`:** adds `has_password` and `linked_providers` (sorted ordinally, `[]` when none); the access token gains no claim.
- **Limits:** the two GET and three POST provider paths count against `login`; they are the only GETs that do not count against `general`.
- **Audit:** 28 kinds; `provider.linked`, `provider.reauthenticated`; `details.provider` on `login.succeeded` of every provider sign-in, on `login.failed`/`login.locked` of the link, on `signup.completed` of a finish and `invite.accepted` of an acceptance through a provider; `account.delete_refused` reason `reauth_failed`; a failed callback writes no row.
- **OpenAPI:** 36 operations, 24 under the charset guard.
- **Python package:** no change. **Sample backend:** no code change; `samples/notes-api/auth.yaml` lists both providers.
- **Workflow** ([`docs/workflow.md`](../../workflow.md)): local only, Conventional Commits in English, documentation in English. **The orchestrator makes the commits**: implementers leave their changes uncommitted, never run a git command that changes state, and end with the "Hand back" step (files and a proposed subject, never a git command). One Sonnet implementer per task writes the code of that task, runs it and fixes what breaks; the code below is the plan's, written on paper, and the implementer's tests decide.
- **The EF migration** (Task 1) is created with `dotnet ef migrations add AddProviderTickets`; its id is the orchestrator's business afterwards, and no test or document names it.
- **.NET on this machine:** the build gate is `"C:/Program Files/dotnet/dotnet.exe" build Auth-Core.slnx -warnaserror -v q -nologo -maxcpucount:2`; tests run as `tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.<Class> [-class ...]` after that build (`dotnet test` at the root finds no tests). Docker must be running (Testcontainers). The analyzers are strict under `-warnaserror`: `ArgumentNullException.ThrowIfNull`, explicit `StringComparison` (CA1307/CA1309/CA1310), `CultureInfo.InvariantCulture` (CA1305), `[LoggerMessage]` for logs (CA1848), no constant array as an argument (CA1861: use a `static readonly` field).
- **Implementers run only their task's test classes and the classes their task edits** (`-maxThreads 2`). They never run the whole .NET suite, a live e2e script or Playwright: the verifiers do.
- **Python:** `C:/p6v/Scripts/python.exe` (the `python3` on `PATH` is a Store shim).
- **Angular:** in `samples/notes-web`: `npm ci` once when `node_modules` is missing, then `npm test`, `npx ng build` and `npm run check:docs`. Texts are plain printable ASCII (`texts.spec.ts` checks it).
- **No `docker compose up`/`down` of any stack and no stopping of containers by implementers**; no task builds the fake's image (the e2e verifier does); every compose command of a script carries its own project name, never `auth-core`.
- **Line endings:** shell scripts are LF (the orchestrator marks a new one executable in the index); `.cs`, `.ts` and `.md` files keep the endings each file has (the C# and TypeScript sources and the plans are CRLF, the YAML files LF). Git Bash `sed -i` strips the CR of a CRLF file: edit with the Edit tool, never with `sed -i`.
- **Unicode escapes stay out of the source.** A backslash followed by `u` and four hex digits typed into a tool parameter arrives as the character. The code below builds such text with `(char)0x...` or `char.ConvertFromUtf32(...)`. After writing, each task's "Hand back" step scans the files it touched ("scan the touched files") with
  `C:/p6v/Scripts/python.exe -c "import sys,unicodedata as u; bad=[(f,i,hex(ord(c))) for f in sys.argv[1:] for i,c in enumerate(open(f,encoding='utf-8').read()) if (u.category(c)=='Cc' and c not in '\t\r\n') or 0xFDD0<=ord(c)<=0xFDEF or (ord(c)&0xFFFE)==0xFFFE or ord(c)>0xFFFF]; print(bad or 'clean')" <files>`:
  it must print `clean`.

## Checked on this code before the plan was written

A probe of OpenIddict 7.7.1 ran experiments on this code in a throw-away clone (`.superpowers/sdd/0011-google-and-microsoft/probe-report.md`);
none of its code is copied here, and the plan does not re-run them. What the plan relies on, by the probe's question:

- **Q1** — `EnableRedirectionEndpointPassthrough` plus `EnableErrorPassthrough` bring every success and failure of the callback to our endpoint;
  `AuthenticateAsync(OpenIddictClientAspNetCoreDefaults.AuthenticationScheme)` gives the outcome; the mapping to the four codes is
  `invalid_state` when the state is unreadable (no properties or no `purpose` item) or `.error_uri` ends `/ID2129` or `/ID2163`, else `cancelled`
  for `access_denied`, else `provider_error`. OpenIddict presets status `400` on a failure, so the endpoint always returns its own redirect (T10);
  `Items` hold raw `.Token.*` values on success and are never logged (T11).
- **Q2** — both web providers use response mode `query`; nothing to force.
- **Q3** — the client beside the server disturbs no existing test; the server's handlers never run the callback and the client's never the token
  endpoints; `UnhandledTokenRequestGuard` still guards unclaimed token paths.
- **Q4** — `UseWebProviders().AddGoogle/AddMicrosoft` with `SetProviderName("google"/"microsoft")` (case-sensitive, T8) and absolute
  `SetRedirectUri`; registrations are static, so "offered" is decided by our code before the challenge.
- **Q5** — both providers use discovery; userinfo is on by default and is turned off by a handler before `EvaluateUserInfoRequest` (T7);
  Microsoft `common` turns the token issuer check off (A6).
- **Q6** — tests reach the fake by an `IPostConfigureOptions<HttpClientFactoryOptions>` that adds a `DelegatingHandler` to every named client whose
  name starts `OpenIddict.Client.SystemNetHttp`.
- **Q7** — a plain `http` test issuer works, with the authorization endpoint on the browser's origin and the token and key endpoints on the
  container network.
- **Q8** — the identity token's claims are `result.Properties.GetParameter<ClaimsPrincipal>(...Properties.BackchannelIdentityTokenPrincipal)`,
  raw JWT names; a JSON `true` reads as `"true"`.
- **Q9** — the state is a reference token in `OpenIddictTokens` (`SetStateTokenLifetime(10 min)`), pruned by `TokenPruner` with its lag (A7); the
  cookie is configurable through `UseAspNetCore().Configure(o => o.CookieBuilder...)`.
- **Q10** — the client needs signing and encryption credentials: the instance's `KeyMaterial`.
- **Q11** — `AuthenticationProperties.Parameters` become authorization parameters, `Items` travel in the encrypted state; `RedirectUri` must be
  set to `"/"` or it carries the query (the invitation token) into the state (T6).
- **Q12** — a pass-through token-endpoint handler that answers plain JSON is written untouched; `LoginResponseShaper.ShouldShape` must learn the
  three sign-in paths (T13).
- **Q13** — the code anchors used below; **Q14** — `oid` of personal Microsoft accounts is not documented either way (the owner's real-keys test).
- **T1** — `Properties` is ambiguous between `OpenIddictConstants` and the client's constants: the client's is aliased `ClientProperties`.
- **T4, T5** — `SetClientUri(<return origin>)` and the callback-origin rewrite are both required, or every callback behind a proxy fails.
- **T9** — Google's discovery advertises `authorization_response_iss_parameter_supported`, so the fake sends `iss` on the Google callback (A5).
- **T12** — at `Information` the client's dispatcher logs the state and the PKCE verifier (A1). **T14** — `RatePolicies.Classify` returns
  `General` for every non-POST first: the two GET paths need a branch above it. **T15, T16** — state rows live about 14 days (threat model); the
  state cookie is `Secure`, so the browser tests run on `https://localhost:8443`.

Not proved on paper (each has its test in the task named): that `AtomicSignInResult` may still set a cookie after its commit (Task 8 — the held
answer is in memory, so the response has not started; the test of the cleared `auth_pt` decides, and the task names the fallback); that a `Lax`
cookie survives the provider's cross-site redirect in Chromium and WebKit (Task 19, the Playwright spec); whether real personal Microsoft accounts
send `oid` (the owner's open item).

## Review Focus

The inputs and failure modes the spec implies that a person using this software is most likely to meet, most likely first; each has its test in
the task named.

1. **A deployment behind a proxy that does not keep the browser's scheme or host.** The person expects the provider round trip to work wherever
   the proxy terminates TLS (`ProviderCallbackTests.A_callback_that_reaches_the_service_under_another_host_and_scheme_still_makes_a_ticket`,
   `ProviderStartTests.The_callback_address_comes_from_the_return_address_whatever_the_host_headers_say`; Tasks 5 and 7).
2. **A person who starts in two tabs, or comes back in another browser.** The later round trip wins and the earlier tab is told to start again;
   a ticket never works in a browser that did not make it
   (`ProviderTicketTests.A_second_round_trip_in_the_same_browser_makes_the_first_ticket_unusable`,
   `ProviderTicketTests.A_ticket_that_is_unknown_used_expired_of_another_purpose_or_without_its_binding_is_invalid_ticket`; Task 8).
3. **An operator who lists the providers and forgets a key, or whose Microsoft secret lapses.** The service starts, the button of that provider is
   not shown, health says `Degraded`, the log names the key and never its value
   (`ProviderSettingsTests.A_listed_provider_without_its_keys_warns_once_naming_them_and_health_says_degraded`,
   `ProviderSettingsTests.The_secret_date_makes_health_degraded_from_thirty_days_before`; Task 3).
4. **A person with a password account who presses "Continue with Google" for the first time and mistypes the password.** The ticket stays usable,
   the next try links, and from then on Google alone signs in
   (`ProviderLinkTests.A_wrong_password_leaves_the_ticket_usable_and_the_right_one_then_links`; Task 9).
5. **A Microsoft work tenant that hands out somebody else's address (nOAuth).** It never signs in to that account, never links without the
   password, never skips the mail of sign-up
   (`ProviderExchangeTests.An_unverified_work_address_of_an_account_gets_link_without_a_word_about_the_account`,
   `ProviderFinishTests.An_unverified_or_missing_address_is_403_email_not_verified`,
   `ProviderInvitationTests.Another_address_an_unverified_one_or_none_is_403_address_mismatch_and_the_invitation_stays`; Tasks 8, 10, 11).

## File Structure

New and changed files by responsibility. Everything under `src/Auth.Server/` unless said otherwise; tests under `tests/Auth.IntegrationTests/`.

| Area | Files | Responsibility |
| --- | --- | --- |
| Tickets in the schema | `src/Auth.Infrastructure/Persistence/ProviderTicket.cs`, `ProviderTicketPurpose.cs`, `AuthDbContext.cs`, the migration `AddProviderTickets`, `Providers/ProviderIdentity.cs`, `Providers/ProviderTickets.cs`, `Email/EmailPruner.cs`, `Email/EmailPruningService.cs` | the table, issue/find/consume, the hourly clean-up (Task 1) |
| Manifest | `Providers/ProviderNames.cs`, `Tenancy/Manifest.cs`, `Tenancy/ManifestParser.cs` | the key `providers` (Task 2) |
| Settings and options | `Providers/ProviderSettings.cs`, `Providers/ProviderCatalog.cs`, `Providers/ProviderHealthCheck.cs`, `Email/MailSettings.cs`, `Signup/SignupContracts.cs`, `Signup/SignupOptionsEndpoint.cs`, `Api/SignupRoutes.cs`, `Program.cs` | what is offered, health, `GET /auth/options` (Task 3) |
| The fake provider | `tests/Auth.FakeOidc/*` (project, server, people, entry), `Auth-Core.slnx`, `tests/Auth.IntegrationTests/Auth.IntegrationTests.csproj`, `Infrastructure/FakeOidcHost.cs` | Google, Microsoft and a test issuer for the tests (Task 4) |
| Client and start | `Providers/ProviderPaths.cs`, `Providers/ProviderPurposes.cs`, `Providers/ProviderClientSetup.cs`, `Providers/ProviderCallbackOrigin.cs`, `Providers/ProviderStartEndpoint.cs`, `Api/ProviderRoutes.cs`, `Api/EndpointMetadata.cs`, `Api/OpenApiSetup.cs`, `Tokens/OpenIddictSetup.cs`, `appsettings.json`, `Program.cs` | the client registration and `start` (Task 5) |
| Identity rules | `Providers/ProviderIdentities.cs` | key, address, verified (Task 6) |
| Callback | `Providers/ProviderBindingCookie.cs`, `Providers/ProviderCallbackEndpoint.cs` | tickets, `auth_pt`, the four codes (Task 7) |
| Exchange | `Providers/ProviderRequestHandler.cs`, `Providers/ProviderLogins.cs`, `Providers/ProviderSignIn.cs`, `Providers/ProviderContracts.cs`, `Providers/ProviderExchangeEndpoint.cs`, `Login/LoginResponseShaper.cs`, `Login/AtomicSignInResult.cs`, `Tenancy/Outcome.cs`, `Audit/AuditKinds.cs` | sign-in of a linked identity and the `next` answers (Task 8) |
| Link | `Providers/ProviderLinkEndpoint.cs`, `Login/LoginEndpoint.cs` | the password once, the lockout (Task 9) |
| Finish | `Providers/ProviderFinishEndpoint.cs`, `Signup/SignupService.cs` | sign-up through a provider (Task 10) |
| Invitation | `Tenancy/InviteAcceptance.cs`, `Providers/ProviderExchangeEndpoint.cs` | acceptance through a provider (Task 11) |
| Deletion, `/auth/me` | `Api/AccountDeletionEndpoint.cs`, `Tenancy/Contracts.cs`, `Api/OrgEndpoints.cs`, `Api/TenancyEndpoints.cs` | `provider_ticket`, `has_password`, `linked_providers` (Task 12) |
| Limits, audit, upgrade | `RateLimiting/RatePolicy.cs` | the `login` policy for five paths (Task 13) |
| The fake as a container | `tests/Auth.FakeOidc/Dockerfile`, `Dockerfile.dockerignore`, `compose.yml`, `samples/notes-api/auth.yaml`, `deploy/auth.yaml`, `scripts/e2e-web.sh`, `scripts/e2e-notes.sh`, `scripts/e2e-signup.sh`, `scripts/e2e-private.sh` | the e2e stack (Task 14) |
| Live check | `scripts/e2e-providers.sh` | the round trip over the network (Task 15) |
| Sample frontend | `samples/notes-web/src/app/account-api.ts`, `auth/auth.service.ts`, `texts.ts`, `app.routes.ts`, `pages/provider-buttons.ts`, `pages/login.ts`, `pages/signup.ts`, `pages/invite.ts`, `pages/provider-return.ts`, `pages/provider-link.ts`, `pages/provider-finish.ts`, `pages/notes.ts`, `src/testing/helpers.ts` and their specs | the screens (Tasks 16, 17, 18) |
| Browser check | `samples/notes-web/e2e/11-providers.spec.ts`, `e2e/support/fake-provider.ts` | Playwright (Task 19) |
| Documents | `docs/deployment/provider-keys.md`, `docs/operations/microsoft-secret.md`, `docs/security/threat-model.md`, `docs/integration/angular.md`, `docs/integration/python-fastapi.md`, `docs/deployment/vps.md`, `deploy/docker-compose.prod.yml`, `deploy/.env.prod.example`, `docs/design.md`, `CHANGELOG.md`, `samples/notes-web/README.md` | Task 20 |

## The tasks

| Day | Task | Deliverable |
| --- | --- | --- |
| Fri 24.04 | 1 | Provider tickets in the schema: the table, the store, the hourly clean-up |
| Fri 24.04 | 2 | The manifest key `providers` |
| Fri 24.04 | 3 | Provider settings, what is offered, health with the secret's date, `providers` in `GET /auth/options` |
| Sat 25.04 | 4 | The fake OpenID provider `tests/Auth.FakeOidc` and its in-process host |
| Sat 25.04 | 5 | OpenIddict's client and `GET /auth/providers/{name}/start` |
| Sat 25.04 | 6 | The identity rules |
| Sat 25.04 | 7 | `GET /auth/providers/{name}/callback`: tickets, `auth_pt`, the four error codes |
| Mon 27.04 | 8 | `POST /auth/providers/exchange` for sign-in tickets; the ticket rules; the two audit kinds |
| Mon 27.04 | 9 | `POST /auth/providers/link` and its lockout |
| Mon 27.04 | 10 | `POST /auth/providers/finish` and its races |
| Tue 28.04 | 11 | An invitation through a provider |
| Tue 28.04 | 12 | `DELETE /auth/account` with `provider_ticket`, `/auth/me`'s new members, "forgot password" for an account without one |
| Tue 28.04 | 13 | The per-IP limits, the audit flows, the last OpenAPI checks, the upgrade from spec 0010 |
| Wed 29.04 | 14 | The fake as a container, the shipped manifests, the earlier e2e scripts |
| Wed 29.04 | 15 | `scripts/e2e-providers.sh` |
| Wed 29.04 | 16 | The sample: the API calls, the buttons on `/login`, `/signup` and `/invite` |
| Thu 30.04 | 17 | The sample: `/provider-return`, `/provider-link`, `/provider-finish` |
| Thu 30.04 | 18 | The sample: "Delete account" confirmed with the provider |
| Thu 30.04 | 19 | The Playwright spec `11-providers.spec.ts` |
| Fri 01.05 | 20 | The documents and the acceptance map check |
| Sat 02.05 | — | Final review, the three verifiers, the "As built", the merge (outside this plan, owner-gated) |

Sun 26.04 and Sun 03.05 are off. Tasks run in order inside a day and across days; each leaves the build gate and the tests of the files it touches
green, and each task's tests are written first and seen to fail.

**Contract counts that change** (tests that enumerate the contract; each task that changes one says so in its "Contract counts" line):
`OpenApiTests` and `OpenApiHardeningTests` (Task 3: `OptionsResponse`; Task 5: 31 → 32 operations; Task 7: 33; Task 8: 34 and 21 → 22 under the
charset guard; Task 9: 35 and 23; Task 10: 36 and 24; Task 12: `MeResponse`, `DELETE /auth/account`), `AuditTableTests` (Task 8: 26 → 28 kinds),
the literal `GET /auth/options` bodies of `SignupOptionsTests`, `PrivateSignupGateTests`, `ShippedConfigurationTests` and `SignupCompleteTests`
(Task 3), `OrgEndpointTests` (Task 12: `/auth/me` members), `RatePolicyTests` and `RateLimitMiddlewareTests` (Task 13), `JsonCharsetTests`
(Task 13), `ShippedConfigurationTests` (Task 14: the notes manifest lists the providers), `scripts/e2e-signup.sh` and `scripts/e2e-private.sh`
(Task 14: the options body).

---

## Day 1 — Friday 24.04: the table, the manifest key, the settings

When the day is done, the database can hold provider tickets, a manifest can list the providers, and the service knows from its settings which
providers it offers: `GET /auth/options` says so and `/auth/health` says `Degraded` when a listed provider lacks a key or the Microsoft secret is
about to lapse. Nothing talks to a provider yet.

### Task 1: Provider tickets in the schema

**Files:**
- Create: `src/Auth.Infrastructure/Persistence/ProviderTicketPurpose.cs`, `src/Auth.Infrastructure/Persistence/ProviderTicket.cs`, `src/Auth.Server/Providers/ProviderIdentity.cs`, `src/Auth.Server/Providers/ProviderTickets.cs`, the migration (generated)
- Modify: `src/Auth.Infrastructure/Persistence/AuthDbContext.cs`, `src/Auth.Server/Email/EmailPruner.cs`, `src/Auth.Server/Email/EmailPruningService.cs`
- Create (tests): `tests/Auth.IntegrationTests/ProviderTicketTableTests.cs`, `tests/Auth.IntegrationTests/ProviderTicketStoreTests.cs`

**Interfaces:**
- Consumes: `AuthDbContext`, `EmailTokens.NewToken`, `EmailTokens.HashOf`, `StorableTime.Now`, `EmailPruner`.
- Produces: `enum ProviderTicketPurpose : short { SignIn = 1, Invite = 2, Reauth = 3 }` and the entity `ProviderTicket` (`Id`, `TicketHash`, `BindingHash`, `Purpose`, `Provider`, `IdentityKey`, `Email`, `EmailVerified`, `InviteTokenHash`, `ExpiresAt`) in `Auth.Infrastructure.Persistence`; `AuthDbContext.ProviderTickets`; in `Auth.Server.Providers`: `record ProviderIdentity(string Provider, string Key, string? Email, bool EmailVerified)`, `record ProviderTicketDraft(ProviderTicketPurpose Purpose, ProviderIdentity Identity, byte[]? InviteTokenHash)`, `record IssuedTicket(string Ticket, string Binding)`, and `static class ProviderTickets` with `Lifetime` (10 min), `ReauthLifetime` (5 min), `StateLifetime` (10 min), `TimeSpan LifetimeOf(ProviderTicketPurpose)`, `Task<IssuedTicket> IssueAsync(AuthDbContext db, ProviderTicketDraft draft, DateTimeOffset now, CancellationToken)`, `Task<ProviderTicket?> FindAsync(AuthDbContext db, string ticket, string? binding, DateTimeOffset now, CancellationToken)` and `Task<ProviderTicket?> ConsumeAsync(AuthDbContext db, string ticket, string binding, ProviderTicketPurpose purpose, DateTimeOffset now, CancellationToken)`; `EmailPruner.PruneProviderTicketsAsync(CancellationToken)`.

**Contract counts:** none.

**Decisions the plan takes here:**
- The table has a surrogate key `Id` and a unique index on `TicketHash` (the spec's "unique index on the ticket hash"), as `Invites` has; no
  foreign key, as `PendingSignups` has none: a ticket names no account.
- `FindAsync` reads the ticket by its hash **and** its binding hash and its expiry in one query, so that a ticket without its cookie, with another
  browser's, unknown, used or expired is the same `null` (one `invalid_ticket`, Settled 4); the purpose is judged by the caller, which knows which
  purposes it takes. `ConsumeAsync` repeats every condition in its `DELETE ... RETURNING`, so of parallel requests one gets the row.
- The column sizes: `Provider` 20, `IdentityKey` 255 (Google's `sub` is at most 255 ASCII characters; a Microsoft key is 73); `Email` is `text`
  with no limit, because the spec keeps every `email` claim as the address (unverified when it breaks the invitation rule) and only an identity
  with no `email` claim has none, so the column takes whatever the identity token holds (the token's own size bounds it).

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/ProviderTicketTableTests.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Provider tickets: the table <c>ProviderTickets</c> as the migration makes it.</summary>
public sealed class ProviderTicketTableTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
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
            FROM information_schema.columns WHERE table_name = 'ProviderTickets' ORDER BY column_name
            """);

        Assert.Equal(
            [
                "BindingHash:bytea::NO",
                "Email:text::YES",
                "EmailVerified:boolean::NO",
                "ExpiresAt:timestamp with time zone::NO",
                "Id:uuid::NO",
                "IdentityKey:character varying:255:NO",
                "InviteTokenHash:bytea::YES",
                "Provider:character varying:20:NO",
                "Purpose:smallint::NO",
                "TicketHash:bytea::NO",
            ],
            columns);
    }

    [Fact]
    public async Task The_table_has_a_unique_index_on_the_hash_and_one_on_the_expiry()
    {
        var indexes = await StringsAsync($"""SELECT indexname AS "Value" FROM pg_indexes WHERE tablename = 'ProviderTickets' ORDER BY indexname""");
        var unique = await StringsAsync($"""SELECT indexdef AS "Value" FROM pg_indexes WHERE indexname = 'IX_ProviderTickets_TicketHash'""");

        Assert.Equal(["IX_ProviderTickets_ExpiresAt", "IX_ProviderTickets_TicketHash", "PK_ProviderTickets"], indexes);
        Assert.Contains("UNIQUE", Assert.Single(unique), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_table_has_no_foreign_key_so_that_a_ticket_names_no_account()   // spec 0011 → Provider tickets: "not tied to an account"
    {
        var foreignKeys = await StringsAsync(
            $"""SELECT conname AS "Value" FROM pg_constraint WHERE conrelid = '"ProviderTickets"'::regclass AND contype = 'f'""");

        Assert.Empty(foreignKeys);
    }
}
```

`tests/Auth.IntegrationTests/ProviderTicketStoreTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Provider tickets: what the store keeps, finds and uses up. The endpoints' answers are in <c>ProviderTicketTests</c>.</summary>
public sealed class ProviderTicketStoreTests(PostgresFixture postgres, KeyMaterialFixture keys) : SessionTestBase(postgres, keys)
{
    private static readonly ProviderIdentity Ola = new("google", "100000000000000000001", "ola@gmail.test", true);

    private async Task<T> InDbAsync<T>(Func<AuthDbContext, Task<T>> work)
    {
        using var scope = Factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    private Task<IssuedTicket> IssueAsync(ProviderTicketPurpose purpose, byte[]? inviteHash = null) =>
        InDbAsync(db => ProviderTickets.IssueAsync(db, new ProviderTicketDraft(purpose, Ola, inviteHash), StorableTime.Now(Clock), TestContext.Current.CancellationToken));

    private static byte[] Sha256(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Issuing_stores_the_hashes_only_and_the_expiry_of_the_purpose()
    {
        var now = StorableTime.Now(Clock);
        var invite = Sha256("an-invitation-token");

        var signIn = await IssueAsync(ProviderTicketPurpose.SignIn);
        var reauth = await IssueAsync(ProviderTicketPurpose.Reauth);
        var invited = await IssueAsync(ProviderTicketPurpose.Invite, invite);

        var rows = await InDbAsync(db => db.ProviderTickets.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, rows.Count);
        foreach (var issued in new[] { signIn, reauth, invited })
        {
            Assert.Equal(43, issued.Ticket.Length);
            Assert.Equal(43, issued.Binding.Length);
            Assert.NotEqual(issued.Ticket, issued.Binding);
            var row = Assert.Single(rows, r => r.TicketHash.AsSpan().SequenceEqual(Sha256(issued.Ticket)));
            Assert.Equal(Sha256(issued.Binding), row.BindingHash);
            Assert.Equal("google", row.Provider);
            Assert.Equal("100000000000000000001", row.IdentityKey);
            Assert.Equal("ola@gmail.test", row.Email);
            Assert.True(row.EmailVerified);
        }

        Assert.Equal(now + TimeSpan.FromMinutes(10), rows.Single(r => r.Purpose == ProviderTicketPurpose.SignIn).ExpiresAt);
        Assert.Equal(now + TimeSpan.FromMinutes(5), rows.Single(r => r.Purpose == ProviderTicketPurpose.Reauth).ExpiresAt);
        Assert.Equal(invite, rows.Single(r => r.Purpose == ProviderTicketPurpose.Invite).InviteTokenHash);
        Assert.Null(rows.Single(r => r.Purpose == ProviderTicketPurpose.SignIn).InviteTokenHash);
    }

    [Fact]
    public async Task A_ticket_is_found_only_with_its_binding_and_before_its_expiry()
    {
        var issued = await IssueAsync(ProviderTicketPurpose.SignIn);
        var other = await IssueAsync(ProviderTicketPurpose.SignIn);
        var cancellation = TestContext.Current.CancellationToken;

        Assert.NotNull(await InDbAsync(db => ProviderTickets.FindAsync(db, issued.Ticket, issued.Binding, StorableTime.Now(Clock), cancellation)));
        Assert.Null(await InDbAsync(db => ProviderTickets.FindAsync(db, issued.Ticket, null, StorableTime.Now(Clock), cancellation)));
        Assert.Null(await InDbAsync(db => ProviderTickets.FindAsync(db, issued.Ticket, other.Binding, StorableTime.Now(Clock), cancellation)));
        Assert.Null(await InDbAsync(db => ProviderTickets.FindAsync(db, "unknown-ticket", issued.Binding, StorableTime.Now(Clock), cancellation)));

        Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(await InDbAsync(db => ProviderTickets.FindAsync(db, issued.Ticket, issued.Binding, StorableTime.Now(Clock), cancellation)));
    }

    [Fact]
    public async Task A_ticket_is_used_up_once_and_only_for_its_purpose()
    {
        var issued = await IssueAsync(ProviderTicketPurpose.SignIn);
        var cancellation = TestContext.Current.CancellationToken;

        Assert.Null(await InDbAsync(db => ProviderTickets.ConsumeAsync(db, issued.Ticket, issued.Binding, ProviderTicketPurpose.Reauth, StorableTime.Now(Clock), cancellation)));
        var used = await InDbAsync(db => ProviderTickets.ConsumeAsync(db, issued.Ticket, issued.Binding, ProviderTicketPurpose.SignIn, StorableTime.Now(Clock), cancellation));
        var again = await InDbAsync(db => ProviderTickets.ConsumeAsync(db, issued.Ticket, issued.Binding, ProviderTicketPurpose.SignIn, StorableTime.Now(Clock), cancellation));

        Assert.NotNull(used);
        Assert.Equal("ola@gmail.test", used.Email);
        Assert.Null(again);
        Assert.Equal(0, await InDbAsync(db => db.ProviderTickets.CountAsync(cancellation)));
    }

    [Fact]
    public async Task The_hourly_pass_deletes_the_expired_tickets_and_keeps_the_others()   // spec 0011 → Provider tickets: the clean-up of spec 0004
    {
        await IssueAsync(ProviderTicketPurpose.Reauth);   // 5 minutes
        await IssueAsync(ProviderTicketPurpose.SignIn);   // 10 minutes
        Clock.Advance(TimeSpan.FromMinutes(6));

        var pruned = await Factory.Services.GetRequiredService<EmailPruner>().PruneProviderTicketsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, pruned);
        var left = await InDbAsync(db => db.ProviderTickets.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ProviderTicketPurpose.SignIn, left.Purpose);
    }
}
```

- [ ] **Step 2: See them fail.** Build: `ProviderTicket`, `ProviderTickets` and `PruneProviderTicketsAsync` do not exist.

- [ ] **Step 3: The entity.** `src/Auth.Infrastructure/Persistence/ProviderTicketPurpose.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>What a provider round trip was for (spec 0011 → Provider tickets), stored as a <c>smallint</c>.</summary>
public enum ProviderTicketPurpose : short
{
    /// <summary>Sign-in, linking and sign-up: the exchange decides which.</summary>
    SignIn = 1,

    /// <summary>The acceptance of an invitation.</summary>
    Invite = 2,

    /// <summary>The confirmation of a self-deletion.</summary>
    Reauth = 3,
}
```

`src/Auth.Infrastructure/Persistence/ProviderTicket.cs`:

```csharp
namespace Auth.Infrastructure.Persistence;

/// <summary>
/// A provider ticket (spec 0011 → Provider tickets): what a successful callback proved, waiting for the request that uses it. Only hashes are
/// stored: of the ticket, which travels in the fragment, and of the value of the browser's <c>auth_pt</c> cookie, which binds it to that browser.
/// It names no account (no foreign key): the exchange decides which account, if any.
/// </summary>
public sealed class ProviderTicket
{
    public Guid Id { get; init; }

    /// <summary>SHA-256 of the ticket.</summary>
    public required byte[] TicketHash { get; init; }

    /// <summary>SHA-256 of the value of the <c>auth_pt</c> cookie set with the ticket.</summary>
    public required byte[] BindingHash { get; init; }

    public required ProviderTicketPurpose Purpose { get; init; }

    /// <summary><c>google</c> or <c>microsoft</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>The identity key (Google <c>sub</c>, Microsoft <c>{tid}:{oid}</c>): the <c>ProviderKey</c> of a login.</summary>
    public required string IdentityKey { get; init; }

    /// <summary>The address as the provider gave it; <see langword="null"/> when it gave none.</summary>
    public string? Email { get; init; }

    public bool EmailVerified { get; init; }

    /// <summary>For an invitation: SHA-256 of its link token. Never the token.</summary>
    public byte[]? InviteTokenHash { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}
```

In `src/Auth.Infrastructure/Persistence/AuthDbContext.cs`, replace

```csharp
    public DbSet<PendingSignup> PendingSignups => Set<PendingSignup>();
```

with

```csharp
    public DbSet<PendingSignup> PendingSignups => Set<PendingSignup>();

    public DbSet<ProviderTicket> ProviderTickets => Set<ProviderTicket>();
```

and replace

```csharp
        // Spec 0008 → Audit log. The names are the contract's (snake_case): the backup runbook queries them. No foreign keys on purpose.
```

with

```csharp
        // Spec 0011. A ticket names no account until it is used: no foreign key.
        builder.Entity<ProviderTicket>(ticket =>
        {
            ticket.HasKey(t => t.Id);
            ticket.Property(t => t.Provider).HasMaxLength(20);
            ticket.Property(t => t.IdentityKey).HasMaxLength(255);
            ticket.Property(t => t.Email).HasColumnType("text");   // any `email` claim is kept (spec 0011 → the identity): no length limit
            // A ticket is looked up by its hash, and one statement uses it up.
            ticket.HasIndex(t => t.TicketHash).IsUnique();
            // The clean-up selects by expiry.
            ticket.HasIndex(t => t.ExpiresAt);
        });

        // Spec 0008 → Audit log. The names are the contract's (snake_case): the backup runbook queries them. No foreign keys on purpose.
```

- [ ] **Step 4: The migration.** From the repository root:
`"C:/Program Files/dotnet/dotnet.exe" tool run dotnet-ef migrations add AddProviderTickets --project src/Auth.Infrastructure --startup-project src/Auth.Server`.
Read the generated `Up`: one `CreateTable` of `ProviderTickets` with the ten columns of the test, `CreateIndex` of `IX_ProviderTickets_ExpiresAt`
and of `IX_ProviderTickets_TicketHash` (`unique: true`), and nothing else (a change to another table means the model and the snapshot disagree:
stop and report). Keep the generated files' line endings (CRLF, as the earlier migrations).

- [ ] **Step 5: The store.** `src/Auth.Server/Providers/ProviderIdentity.cs`:

```csharp
namespace Auth.Server.Providers;

/// <summary>
/// What a callback read from the provider's identity token (spec 0011 → The provider identity and its address): the provider, the identity key
/// (Google <c>sub</c>, Microsoft <c>{tid}:{oid}</c>), the address as the provider gave it (<see langword="null"/>: no address), and whether the
/// address counts as verified. <c>ProviderIdentities</c> (Task 6) makes it.
/// </summary>
public sealed record ProviderIdentity(string Provider, string Key, string? Email, bool EmailVerified);
```

`src/Auth.Server/Providers/ProviderTickets.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Providers;

/// <summary>What a callback stores: the purpose, the identity and, for an invitation, the hash of its link token.</summary>
public sealed record ProviderTicketDraft(ProviderTicketPurpose Purpose, ProviderIdentity Identity, byte[]? InviteTokenHash);

/// <summary>
/// A new ticket and the value of its <c>auth_pt</c> cookie, both in clear: the callback puts the first into the fragment and the second into the
/// cookie, and nothing stores either.
/// </summary>
public sealed record IssuedTicket(string Ticket, string Binding);

/// <summary>
/// The provider tickets of spec 0011: 32 random bytes as 43 base64url characters, like the link tokens of spec 0004, stored as SHA-256 only, with
/// the SHA-256 of the browser's binding cookie beside them. Every operation runs in the caller's context, so the caller's transaction decides
/// whether it happened: a sign-in that is rolled back gives the ticket back.
/// </summary>
public static class ProviderTickets
{
    /// <summary>A ticket of a sign-in or an invitation lives 10 minutes after the callback.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>A ticket of a re-authentication lives 5 minutes.</summary>
    public static readonly TimeSpan ReauthLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The state of a round trip (OpenIddict's) lives 10 minutes, from the start to the callback.</summary>
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public static TimeSpan LifetimeOf(ProviderTicketPurpose purpose) =>
        purpose == ProviderTicketPurpose.Reauth ? ReauthLifetime : Lifetime;

    /// <summary>Adds the ticket and saves it at once; returns the ticket and the binding in clear.</summary>
    public static async Task<IssuedTicket> IssueAsync(AuthDbContext db, ProviderTicketDraft draft, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(draft);

        var ticket = EmailTokens.NewToken();
        var binding = EmailTokens.NewToken();
        db.ProviderTickets.Add(new ProviderTicket
        {
            Id = Guid.NewGuid(),
            TicketHash = EmailTokens.HashOf(ticket),
            BindingHash = EmailTokens.HashOf(binding),
            Purpose = draft.Purpose,
            Provider = draft.Identity.Provider,
            IdentityKey = draft.Identity.Key,
            Email = draft.Identity.Email,
            EmailVerified = draft.Identity.EmailVerified,
            InviteTokenHash = draft.InviteTokenHash,
            ExpiresAt = now + LifetimeOf(draft.Purpose),
        });
        await db.SaveChangesAsync(cancellationToken);
        return new IssuedTicket(ticket, binding);
    }

    /// <summary>
    /// The live ticket of this browser, of any purpose; <see langword="null"/> for a ticket that is unknown, used or expired, and for one presented
    /// without its binding or with another browser's (spec 0011: one answer for all of them). It changes nothing.
    /// </summary>
    public static Task<ProviderTicket?> FindAsync(AuthDbContext db, string ticket, string? binding, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ticket);

        if (binding is null)
        {
            return Task.FromResult<ProviderTicket?>(null);
        }

        var hash = EmailTokens.HashOf(ticket);
        var bindingHash = EmailTokens.HashOf(binding);
        return db.ProviderTickets.AsNoTracking()
            .Where(t => t.TicketHash == hash && t.BindingHash == bindingHash && t.ExpiresAt > now)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Uses the ticket up: removes it if it is this browser's, of this purpose and live, and returns it; <see langword="null"/> otherwise. One
    /// statement, so of parallel requests with one ticket exactly one gets it; the others wait for its row and then find nothing.
    /// </summary>
    public static async Task<ProviderTicket?> ConsumeAsync(
        AuthDbContext db, string ticket, string binding, ProviderTicketPurpose purpose, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(binding);

        var hash = EmailTokens.HashOf(ticket);
        var bindingHash = EmailTokens.HashOf(binding);
        var purposeValue = (short)purpose;

        // ToListAsync, not SingleOrDefaultAsync: EF must send the statement as it is, not wrapped in a subquery.
        var removed = await db.ProviderTickets
            .FromSql($"""
                DELETE FROM "ProviderTickets"
                WHERE "TicketHash" = {hash} AND "BindingHash" = {bindingHash} AND "Purpose" = {purposeValue} AND "ExpiresAt" > {now}
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return removed.Count == 1 ? removed[0] : null;
    }
}
```

- [ ] **Step 6: The clean-up.** In `src/Auth.Server/Email/EmailPruner.cs`, replace

```csharp
    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Tokens} link token and {Limits} mail limit entries.")]
```

with

```csharp
    /// <summary>Removes the provider tickets whose minutes are over (spec 0011): they are refused already, so this only keeps the table small.</summary>
    public async Task<int> PruneProviderTicketsAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var tickets = await db.ProviderTickets.Where(t => t.ExpiresAt <= now).ExecuteDeleteAsync(cancellationToken);

        LogPrunedProviderTickets(logger, tickets);
        return tickets;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Tickets} expired provider tickets.")]
    private static partial void LogPrunedProviderTickets(ILogger logger, int tickets);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Tokens} link token and {Limits} mail limit entries.")]
```

In `src/Auth.Server/Email/EmailPruningService.cs`, replace `            await pruner.PrunePendingSignupsAsync(stoppingToken);` with

```csharp
            await pruner.PrunePendingSignupsAsync(stoppingToken);
            await pruner.PruneProviderTicketsAsync(stoppingToken);
```

- [ ] **Step 7: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ProviderTicketTableTests -class Auth.IntegrationTests.ProviderTicketStoreTests -class Auth.IntegrationTests.EmailPruningTests -class Auth.IntegrationTests.PendingSignupPruningTests -class Auth.IntegrationTests.PersistenceTests`
passes.

- [ ] **Step 8: Hand back** — uncommitted. Files: everything listed under Files, the three generated migration files included. Scan the touched
files. Proposed subject: `feat(server): provider tickets in their own table, hashed and pruned hourly`.

---

### Task 2: The manifest key `providers`

**Files:**
- Create: `src/Auth.Server/Providers/ProviderNames.cs`
- Modify: `src/Auth.Server/Tenancy/Manifest.cs`, `src/Auth.Server/Tenancy/ManifestParser.cs`
- Create (tests): `tests/Auth.IntegrationTests/ManifestProvidersTests.cs`

**Interfaces:**
- Consumes: `ManifestParser.Parse`, `Manifest.ToJson`, `Manifest.FromJson`.
- Produces: `static class ProviderNames` (`Google = "google"`, `Microsoft = "microsoft"`, `IReadOnlyList<string> All`, `bool IsName(string?)`, `string DisplayNameOf(string)`); `Manifest(..., IReadOnlyList<string>? providers = null)` (the last parameter) and `Manifest.Providers` (`IReadOnlyList<string>`, never null, the order of the file, no duplicates).

**Contract counts:** none (a stored manifest without providers is written exactly as before).

- [ ] **Step 1: Write the failing tests.** `tests/Auth.IntegrationTests/ManifestProvidersTests.cs`:

```csharp
using Auth.Server.Providers;
using Auth.Server.Tenancy;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Manifest: the key <c>providers</c>.</summary>
public sealed class ManifestProvidersTests
{
    private const string Roles = """
        permissions: [docs:read]
        default_roles:
          admin: ["*"]
        """;

    private static Manifest Parsed(string yaml)
    {
        var result = ManifestParser.Parse(yaml);
        Assert.Null(result.Error);
        return Assert.IsType<Manifest>(result.Manifest);
    }

    [Theory]
    [InlineData("[google, microsoft]", "google,microsoft")]
    [InlineData("[microsoft, google]", "microsoft,google")]
    [InlineData("[google]", "google")]
    [InlineData("\n  - microsoft\n  - google", "microsoft,google")]
    public void The_key_lists_the_providers_in_the_order_of_the_file(string list, string expected)   // criterion 1: the order of the buttons
    {
        var manifest = Parsed(Roles + "\nproviders: " + list + "\n");

        Assert.Equal(expected.Split(','), manifest.Providers);
    }

    [Fact]
    public void Duplicates_are_folded_and_the_first_place_is_kept()   // criterion 1
    {
        var manifest = Parsed(Roles + "\nproviders: [microsoft, google, microsoft, google]\n");

        Assert.Equal(["microsoft", "google"], manifest.Providers);
    }

    [Theory]
    [InlineData("[github]")]
    [InlineData("[password]")]
    [InlineData("[google, apple]")]
    [InlineData("[Google]")]
    [InlineData("[\"google \"]")]
    [InlineData("[\"\"]")]
    [InlineData("[~]")]
    [InlineData("google")]
    [InlineData("{google: yes}")]
    public void Any_other_value_makes_the_manifest_invalid(string list)   // criterion 1
    {
        var result = ManifestParser.Parse(Roles + "\nproviders: " + list + "\n");

        Assert.Null(result.Manifest);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void A_refused_name_is_named_in_the_reason()
    {
        var result = ManifestParser.Parse(Roles + "\nproviders: [google, github]\n");

        Assert.Contains("providers", result.Error, StringComparison.Ordinal);
        Assert.Contains("github", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\nproviders:\n")]
    [InlineData("\nproviders: ~\n")]
    [InlineData("\nproviders: []\n")]
    public void No_key_an_empty_list_or_a_key_without_a_value_is_no_provider(string block)   // criterion 1
    {
        Assert.Empty(Parsed(Roles + block).Providers);
    }

    [Fact]
    public void The_stored_form_keeps_the_list_and_its_order()
    {
        var manifest = Parsed(Roles + "\nproviders: [microsoft, google]\n");

        var stored = Manifest.FromJson(manifest.ToJson());

        Assert.NotNull(stored);
        Assert.Equal(["microsoft", "google"], stored.Providers);
    }

    [Fact]
    public void A_manifest_without_providers_is_stored_as_before()   // criterion 30: the stored form of spec 0010 does not change
    {
        Assert.DoesNotContain("providers", Parsed(Roles).ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_stored_by_slice_0010_reads_as_no_provider()   // criterion 1
    {
        const string stored = """{"permissions":["docs:read"],"default_roles":[{"name":"admin","permissions":["*"]}],"signup":"self-service","private":{"signup":"self-service","permissions":["docs:read"]}}""";

        var manifest = Manifest.FromJson(stored);

        Assert.NotNull(manifest);
        Assert.Empty(manifest.Providers);
        Assert.True(manifest.PrivateSignup);
    }

    [Fact]
    public void The_names_are_those_of_the_contract()
    {
        Assert.Equal(["google", "microsoft"], ProviderNames.All);
        Assert.True(ProviderNames.IsName("google"));
        Assert.False(ProviderNames.IsName("Google"));
        Assert.False(ProviderNames.IsName(null));
        Assert.Equal("Microsoft", ProviderNames.DisplayNameOf("microsoft"));
    }
}
```

- [ ] **Step 2: See them fail.** Build: `Manifest.Providers` and `ProviderNames` do not exist.

- [ ] **Step 3: The names.** `src/Auth.Server/Providers/ProviderNames.cs`:

```csharp
namespace Auth.Server.Providers;

/// <summary>The providers of spec 0011 by the names the contract uses everywhere: lower case, in paths, the manifest, answers and audit rows.</summary>
public static class ProviderNames
{
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
}
```

- [ ] **Step 4: The manifest.** In `src/Auth.Server/Tenancy/Manifest.cs`:

(a) Replace

```csharp
    public Manifest(
        IReadOnlyList<string> permissions, IReadOnlyList<DefaultRole> defaultRoles, SignupMode signup = SignupMode.InviteOnly,
        bool privateSignup = false, IReadOnlyList<string>? privatePermissions = null)
    {
```

with

```csharp
    /// <param name="providers">The providers the product lists (spec 0011): provider names, the order of the file, distinct. None when absent.</param>
    public Manifest(
        IReadOnlyList<string> permissions, IReadOnlyList<DefaultRole> defaultRoles, SignupMode signup = SignupMode.InviteOnly,
        bool privateSignup = false, IReadOnlyList<string>? privatePermissions = null, IReadOnlyList<string>? providers = null)
    {
```

and replace `        PrivatePermissions = privatePermissions ?? [];` with

```csharp
        PrivatePermissions = privatePermissions ?? [];
        Providers = providers ?? [];
```

(b) Replace

```csharp
    public IReadOnlyList<string> PrivatePermissions { get; }

    public PermissionCatalog Catalog { get; }
```

with

```csharp
    public IReadOnlyList<string> PrivatePermissions { get; }

    /// <summary>
    /// The providers the product lists (spec 0011 → Manifest), in the order of its buttons. Listing is not offering: a provider is offered only
    /// with its keys and the return address too (<c>ProviderCatalog</c>).
    /// </summary>
    public IReadOnlyList<string> Providers { get; }

    public PermissionCatalog Catalog { get; }
```

(c) Replace

```csharp
            PrivateSignup || PrivatePermissions.Count > 0
                ? new StoredPrivate(PrivateSignup ? SignupModes.SelfServiceName : SignupModes.OffName, [.. PrivatePermissions])
                : null),
        StoredJson);
```

with

```csharp
            PrivateSignup || PrivatePermissions.Count > 0
                ? new StoredPrivate(PrivateSignup ? SignupModes.SelfServiceName : SignupModes.OffName, [.. PrivatePermissions])
                : null,
            // Written only when there is one, so that the stored form of a manifest without providers is the one of slice 0010.
            Providers.Count > 0 ? [.. Providers] : null),
        StoredJson);
```

(d) Replace

```csharp
                privateSignup,
                stored.Private?.Permissions ?? []);
```

with

```csharp
                privateSignup,
                stored.Private?.Permissions ?? [],
                stored.Providers ?? []);   // a form stored before spec 0011 has no providers: none is listed
```

(e) Replace

```csharp
    private sealed record StoredManifest(
        string[] Permissions, StoredRole[] DefaultRoles, string? Signup = null, StoredPrivate? Private = null);
```

with

```csharp
    private sealed record StoredManifest(
        string[] Permissions, StoredRole[] DefaultRoles, string? Signup = null, StoredPrivate? Private = null, string[]? Providers = null);
```

- [ ] **Step 5: The parser.** In `src/Auth.Server/Tenancy/ManifestParser.cs`:

(a) Add `using Auth.Server.Providers;` after `using Auth.Server.Requests;`.

(b) Replace

```csharp
        List<string> privatePermissions = [.. privateListed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return new ManifestParseResult(new Manifest(permissions, defaults, signup, privateSignup, privatePermissions), null);
```

with

```csharp
        List<string> privatePermissions = [.. privateListed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // Spec 0011: the key `providers`. `providers:` with no value is none; every name must be one of the two exactly; duplicates fold and the
        // first place is kept, because the order is the order of the buttons.
        var providersListed = (document.Providers ?? []).Select(p => p ?? "").ToList();
        if (providersListed.FirstOrDefault(p => !ProviderNames.IsName(p)) is { } unknown)
        {
            return Fail($"providers holds '{unknown}', which is not '{ProviderNames.Google}' or '{ProviderNames.Microsoft}'");
        }

        List<string> providers = [.. providersListed.Distinct(StringComparer.Ordinal)];
        return new ManifestParseResult(new Manifest(permissions, defaults, signup, privateSignup, privatePermissions, providers), null);
```

(c) Replace

```csharp
        // The block of spec 0010. Its own keys only: any other is an error, like a typo at the top level.
        public PrivateDocument? Private { get; set; }
```

with

```csharp
        // The block of spec 0010. Its own keys only: any other is an error, like a typo at the top level.
        public PrivateDocument? Private { get; set; }

        // Spec 0011: the providers a product offers. A list of texts, so that any other value reaches the check above.
        public List<string?>? Providers { get; set; }
```

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ManifestProvidersTests -class Auth.IntegrationTests.ManifestParserTests -class Auth.IntegrationTests.ManifestPrivateTests -class Auth.IntegrationTests.ManifestSignupTests -class Auth.IntegrationTests.ManifestActivationTests -class Auth.IntegrationTests.UpgradeFromV010Tests`
passes.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): the manifest lists the providers a product offers`.

---

### Task 3: Provider settings, what is offered, health, `providers` in `GET /auth/options`

**Files:**
- Create: `src/Auth.Server/Providers/ProviderSettings.cs`, `src/Auth.Server/Providers/ProviderCatalog.cs`, `src/Auth.Server/Providers/ProviderHealthCheck.cs`
- Modify: `src/Auth.Server/Email/MailSettings.cs` (one word), `src/Auth.Server/Signup/SignupContracts.cs`, `src/Auth.Server/Signup/SignupOptionsEndpoint.cs`, `src/Auth.Server/Api/SignupRoutes.cs`, `src/Auth.Server/Program.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`, `tests/Auth.IntegrationTests/SignupOptionsTests.cs`, `tests/Auth.IntegrationTests/PrivateSignupGateTests.cs`, `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`, `tests/Auth.IntegrationTests/SignupCompleteTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/ProviderSettingsTests.cs`, `tests/Auth.IntegrationTests/ProviderOptionsTests.cs`

**Interfaces:**
- Consumes: `ManifestHolder`, `Manifest.Providers`, `ProviderNames` (Task 2), `MailSettingsLoader.OptionalFrontendUrl` (made `internal` here), `SignupGate`.
- Produces: `sealed class ProviderKeys(string clientId, string clientSecret)` (`ClientId`, `ClientSecret`); `sealed class ProviderSettings` with the seven key constants (`ReturnUrlKey`, `GoogleClientIdKey`, `GoogleClientSecretKey`, `MicrosoftClientIdKey`, `MicrosoftClientSecretKey`, `MicrosoftSecretExpiresOnKey`, `TestIssuerKey`), `ReturnUrl` (`Uri?`), `ReturnOrigin` (`Uri?`), `Google`/`Microsoft` (`ProviderKeys?`), `MicrosoftSecretExpiresOn` (`DateOnly?`), `TestIssuer` (`Uri?`), `ProviderKeys? KeysOf(string)`, `IReadOnlyList<string> MissingKeysOf(string)`, `static ProviderSettings Load(IConfiguration, bool isDevelopment)`; `sealed class ProviderCatalog` (singleton) with `SecretWarningDays = 30`, `Offered`, `bool IsOffered(string)`, `ListedButNotOffered`, `SecretLapsesSoon`, `IsDegraded`, `void WarnAtStart()`; `ProviderHealthCheck`; `OptionsResponse(string Signup, string PrivateSignup, IReadOnlyList<string> Providers)`; the local `providerSettings` of `Program.cs` (Task 5 passes it on); test side `AuthAppFactory.WithProviders()`, `WithProviderKeys()`, `ProvidersLine`, `DefaultProviderReturnUrl`, `GoogleClientId`, `GoogleClientSecret`, `MicrosoftClientId`, `MicrosoftClientSecret`.

**Contract counts:** `GET /auth/options` gains `"providers":[]` in every literal body of `SignupOptionsTests`, `PrivateSignupGateTests`,
`ShippedConfigurationTests` and `SignupCompleteTests`; `OpenApiTests.The_responses_are_described_in_snake_case` reads `OptionsResponse` as
`private_signup`, `providers`, `signup`.

**Decisions the plan takes here:**
- Missing keys are worked out once, at start (`MissingKeysOf`), from the settings; what is *offered* is worked out at every call from the manifest
  of the process, so that the callback can answer `provider_unavailable` for a provider dropped from the manifest during a round trip.
- An id or a secret is used exactly as configured (no trimming): a value that differs from what the provider issued fails at the provider, and the
  keys guide says so.
- The return address follows the rules of `CompleteSignup` through the same loader method (made `internal`), and also refuses a `ticket`
  parameter, with its own message naming the key.
- The secret's date is compared by calendar day in UTC: `Degraded` while the days left are 30 or fewer, and after the date.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/ProviderSettingsTests.cs`:

```csharp
using System.Globalization;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Configuration: the provider settings, the warning, health and the secret's date.</summary>
public sealed class ProviderSettingsTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static ProviderSettings Load(Dictionary<string, string?> values, bool isDevelopment = false) =>
        ProviderSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), isDevelopment);

    private static Dictionary<string, string?> Configured() => new()
    {
        [ProviderSettings.ReturnUrlKey] = "https://app.example.com/provider-return",
        [ProviderSettings.GoogleClientIdKey] = AuthAppFactory.GoogleClientId,
        [ProviderSettings.GoogleClientSecretKey] = AuthAppFactory.GoogleClientSecret,
        [ProviderSettings.MicrosoftClientIdKey] = AuthAppFactory.MicrosoftClientId,
        [ProviderSettings.MicrosoftClientSecretKey] = AuthAppFactory.MicrosoftClientSecret,
    };

    private static async Task<string> BodyAsync(AuthAppFactory factory, string path)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public void The_keys_are_the_ones_of_the_contract()
    {
        Assert.Equal("Auth:App:FrontendUrls:ProviderReturn", ProviderSettings.ReturnUrlKey);
        Assert.Equal("Auth:Providers:Google:ClientId", ProviderSettings.GoogleClientIdKey);
        Assert.Equal("Auth:Providers:Google:ClientSecret", ProviderSettings.GoogleClientSecretKey);
        Assert.Equal("Auth:Providers:Microsoft:ClientId", ProviderSettings.MicrosoftClientIdKey);
        Assert.Equal("Auth:Providers:Microsoft:ClientSecret", ProviderSettings.MicrosoftClientSecretKey);
        Assert.Equal("Auth:Providers:Microsoft:ClientSecretExpiresOn", ProviderSettings.MicrosoftSecretExpiresOnKey);
        Assert.Equal("Auth:Providers:TestIssuer", ProviderSettings.TestIssuerKey);
    }

    [Fact]
    public void Nothing_set_is_valid_and_names_every_missing_key()   // criterion 3: missing keys never stop the start
    {
        var settings = Load([]);

        Assert.Null(settings.ReturnUrl);
        Assert.Null(settings.Google);
        Assert.Null(settings.Microsoft);
        Assert.Null(settings.MicrosoftSecretExpiresOn);
        Assert.Null(settings.TestIssuer);
        Assert.Equal(
            [ProviderSettings.GoogleClientIdKey, ProviderSettings.GoogleClientSecretKey, ProviderSettings.ReturnUrlKey],
            settings.MissingKeysOf(ProviderNames.Google));
    }

    [Fact]
    public void Both_keys_and_the_return_address_leave_nothing_missing()
    {
        var settings = Load(Configured());

        Assert.Empty(settings.MissingKeysOf(ProviderNames.Google));
        Assert.Empty(settings.MissingKeysOf(ProviderNames.Microsoft));
        Assert.Equal(AuthAppFactory.GoogleClientId, settings.KeysOf(ProviderNames.Google)?.ClientId);
        Assert.Equal(AuthAppFactory.MicrosoftClientSecret, settings.KeysOf(ProviderNames.Microsoft)?.ClientSecret);
        Assert.Equal(new Uri("https://app.example.com"), settings.ReturnOrigin);
    }

    [Theory]
    [InlineData("", "secret", ProviderSettings.GoogleClientIdKey)]
    [InlineData("   ", "secret", ProviderSettings.GoogleClientIdKey)]
    [InlineData("id", "", ProviderSettings.GoogleClientSecretKey)]
    [InlineData("id", null, ProviderSettings.GoogleClientSecretKey)]
    public void A_blank_id_or_secret_is_missing_and_named(string? id, string? secret, string named)   // criterion 3
    {
        var values = Configured();
        values[ProviderSettings.GoogleClientIdKey] = id;
        values[ProviderSettings.GoogleClientSecretKey] = secret;

        var settings = Load(values);

        Assert.Null(settings.Google);
        Assert.Equal([named], settings.MissingKeysOf(ProviderNames.Google));
        Assert.Empty(settings.MissingKeysOf(ProviderNames.Microsoft));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/provider-return")]
    [InlineData("ftp://app.example.com/provider-return")]
    [InlineData("http://app.example.com/provider-return")]   // http outside Development
    [InlineData("https://app.example.com/provider-return#top")]
    [InlineData("https://user:secret@app.example.com/provider-return")]
    [InlineData("https://app.example.com/provider-return?token=abc")]
    [InlineData("https://app.example.com/provider-return?a=1&ticket=abc")]
    public void A_bad_return_address_is_refused_naming_the_key_and_never_echoing_the_value(string value)   // criterion 3
    {
        var values = Configured();
        values[ProviderSettings.ReturnUrlKey] = value;

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains(ProviderSettings.ReturnUrlKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_http_return_address_is_accepted_in_development()
    {
        var values = Configured();
        values[ProviderSettings.ReturnUrlKey] = "http://localhost:8088/provider-return";

        Assert.Equal(new Uri("http://localhost:8088"), Load(values, isDevelopment: true).ReturnOrigin);
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("01.05.2026")]
    [InlineData("tomorrow")]
    [InlineData("2026-5-1")]
    [InlineData("2026-05-01T00:00")]
    public void A_secret_date_that_is_not_yyyy_mm_dd_is_refused_naming_the_key(string value)   // criterion 3
    {
        var values = Configured();
        values[ProviderSettings.MicrosoftSecretExpiresOnKey] = value;

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains(ProviderSettings.MicrosoftSecretExpiresOnKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_date_loads()
    {
        var values = Configured();
        values[ProviderSettings.MicrosoftSecretExpiresOnKey] = "2027-04-30";

        Assert.Equal(new DateOnly(2027, 4, 30), Load(values).MicrosoftSecretExpiresOn);
    }

    [Fact]
    public void The_test_issuer_outside_development_is_refused_naming_the_key()   // criterion 3, Settled 23
    {
        var values = Configured();
        values[ProviderSettings.TestIssuerKey] = "http://fake-oidc:8099";

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values));

        Assert.Contains(ProviderSettings.TestIssuerKey, ex.Message, StringComparison.Ordinal);
        Assert.Equal(new Uri("http://fake-oidc:8099"), Load(values, isDevelopment: true).TestIssuer);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://fake-oidc:8099")]
    [InlineData("http://fake-oidc:8099/?a=1")]
    [InlineData("http://user:pw@fake-oidc:8099")]
    public void A_test_issuer_that_is_not_an_http_address_is_refused_naming_the_key(string value)
    {
        var values = Configured();
        values[ProviderSettings.TestIssuerKey] = value;

        var ex = Assert.Throws<InvalidOperationException>(() => Load(values, isDevelopment: true));

        Assert.Contains(ProviderSettings.TestIssuerKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_listed_provider_without_its_keys_warns_once_naming_them_and_health_says_degraded()   // criterion 3, Review Focus 3
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + AuthAppFactory.ProvidersLine)
            .WithProviderKeys()
            .WithSetting(ProviderSettings.GoogleClientIdKey, "")
            .WithSetting(ProviderSettings.GoogleClientSecretKey, "")
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        Assert.Equal("""{"signup":"invite-only","private_signup":"off","providers":["microsoft"]}""", await BodyAsync(factory, "/auth/options"));
        Assert.Equal("Degraded", await BodyAsync(factory, "/auth/health"));
        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("google", StringComparison.Ordinal));
        Assert.Contains(ProviderSettings.GoogleClientIdKey, warning.Message, StringComparison.Ordinal);
        Assert.Contains(ProviderSettings.GoogleClientSecretKey, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(AuthAppFactory.MicrosoftClientSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains(AuthAppFactory.MicrosoftClientId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bad_return_address_stops_the_start_naming_the_key()   // criterion 3
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSetting(ProviderSettings.ReturnUrlKey, "https://app.example.com/back#x");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(ProviderSettings.ReturnUrlKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_secret_date_stops_the_start_naming_the_key()   // criterion 3
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithSetting(ProviderSettings.MicrosoftSecretExpiresOnKey, "30.04.2027");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(ProviderSettings.MicrosoftSecretExpiresOnKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_test_issuer_outside_development_stops_the_start_naming_the_key()   // criterion 3
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithEnvironment("Production")
            .WithSetting(ProviderSettings.TestIssuerKey, "http://fake-oidc:8099");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(ProviderSettings.TestIssuerKey, ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(31, "Healthy")]
    [InlineData(30, "Degraded")]
    [InlineData(1, "Degraded")]
    [InlineData(0, "Degraded")]
    [InlineData(-1, "Degraded")]
    public async Task The_secret_date_makes_health_degraded_from_thirty_days_before(int daysAhead, string health)   // criterion 4, Review Focus 3
    {
        var now = DateTimeOffset.UtcNow;
        var date = DateOnly.FromDateTime(now.UtcDateTime).AddDays(daysAhead);
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithClock(new FakeTimeProvider(now))
            .WithSetting(ProviderSettings.MicrosoftSecretExpiresOnKey, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        Assert.Equal(health, await BodyAsync(factory, "/auth/health"));
    }

    [Fact]
    public async Task The_secret_date_warns_at_the_start_naming_the_key_and_the_date()   // spec 0011 → Configuration
    {
        var logs = new CapturingLoggerProvider();
        var now = DateTimeOffset.UtcNow;
        var date = DateOnly.FromDateTime(now.UtcDateTime).AddDays(10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithClock(new FakeTimeProvider(now))
            .WithSetting(ProviderSettings.MicrosoftSecretExpiresOnKey, date)
            .WithServices(services => services.AddSingleton<ILoggerProvider>(logs));

        Assert.Equal("Degraded", await BodyAsync(factory, "/auth/health"));
        Assert.Single(
            logs.Entries,
            e => e.Level == LogLevel.Warning
                && e.Message.Contains(ProviderSettings.MicrosoftSecretExpiresOnKey, StringComparison.Ordinal)
                && e.Message.Contains(date, StringComparison.Ordinal));
    }
}
```

`tests/Auth.IntegrationTests/ProviderOptionsTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Providers;
using Auth.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>GET /auth/options</c>: <c>providers</c> lists exactly the offered providers, in the manifest's order.</summary>
public sealed class ProviderOptionsTests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private static async Task<string> BodyAsync(AuthAppFactory factory, string path)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string[]> ProvidersAsync(AuthAppFactory factory)
    {
        using var body = JsonDocument.Parse(await BodyAsync(factory, "/auth/options"));
        return [.. body.RootElement.GetProperty("providers").EnumerateArray().Select(e => e.GetString()!)];
    }

    [Fact]
    public async Task With_no_key_providers_is_an_empty_list()   // criterion 2
    {
        await using var factory = new AuthAppFactory(postgres, keys);

        Assert.Equal("""{"signup":"invite-only","private_signup":"off","providers":[]}""", await BodyAsync(factory, "/auth/options"));
        Assert.Equal("Healthy", await BodyAsync(factory, "/auth/health"));
    }

    [Theory]
    [InlineData("[google, microsoft]", "google,microsoft")]
    [InlineData("[microsoft, google]", "microsoft,google")]
    [InlineData("[microsoft]", "microsoft")]
    public async Task The_offered_providers_are_listed_in_the_order_of_the_manifest(string list, string expected)   // criterion 2
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest + "\nproviders: " + list + "\n")
            .WithProviderKeys();

        Assert.Equal(expected.Split(','), await ProvidersAsync(factory));
        Assert.Equal("Healthy", await BodyAsync(factory, "/auth/health"));
    }

    [Theory]
    [InlineData(ProviderSettings.GoogleClientIdKey, "microsoft")]
    [InlineData(ProviderSettings.GoogleClientSecretKey, "microsoft")]
    [InlineData(ProviderSettings.MicrosoftClientSecretKey, "google")]
    [InlineData(ProviderSettings.ReturnUrlKey, "")]
    public async Task A_listed_provider_without_its_id_its_secret_or_the_return_address_is_left_out(string blank, string expected)   // criterion 2
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithManifest(AuthAppFactory.DefaultManifest)
            .WithProviders()
            .WithSetting(blank, "");

        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), await ProvidersAsync(factory));
        Assert.Equal("Degraded", await BodyAsync(factory, "/auth/health"));
    }

    [Fact]
    public async Task A_configured_provider_that_is_not_listed_is_not_offered_and_health_is_healthy()   // criterion 2
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithProviderKeys();

        Assert.Empty(await ProvidersAsync(factory));
        Assert.Equal("Healthy", await BodyAsync(factory, "/auth/health"));
    }

    [Fact]
    public async Task A_manifest_with_an_unknown_provider_keeps_the_last_valid_one_and_health_says_degraded()   // criterion 1
    {
        var database = "auth_" + Guid.NewGuid().ToString("N");
        await using (var first = new AuthAppFactory(postgres, keys, database)
            .WithManifest(AuthAppFactory.DefaultManifest + "\nproviders: [google]\n")
            .WithProviderKeys())
        {
            Assert.Equal(["google"], await ProvidersAsync(first));
        }

        await using var second = new AuthAppFactory(postgres, keys, database)
            .WithManifest(AuthAppFactory.DefaultManifest + "\nproviders: [google, github]\n")
            .WithProviderKeys();

        Assert.Equal(["google"], await ProvidersAsync(second));   // the stored manifest of the first start
        Assert.Equal("Degraded", await BodyAsync(second, "/auth/health"));
        Assert.True(second.Services.GetRequiredService<ManifestHolder>().State.IsDegraded);
    }
}
```

Change the literal bodies of the options (each line is replaced as it stands; a line that is in a file twice is replaced in both places):
- `tests/Auth.IntegrationTests/SignupOptionsTests.cs`: `"""{"signup":"invite-only","private_signup":"off"}"""` becomes
  `"""{"signup":"invite-only","private_signup":"off","providers":[]}"""`, and `"""{"signup":"self-service","private_signup":"off"}"""` becomes
  `"""{"signup":"self-service","private_signup":"off","providers":[]}"""`.
- `tests/Auth.IntegrationTests/PrivateSignupGateTests.cs`: the four `[InlineData(...)]` lines of
  `The_four_combinations_of_the_two_switches_give_their_own_options` and the two lines
  `        Assert.Equal("""{"signup":"invite-only","private_signup":"off"}""", await BodyAsync(factory, "/auth/options"));`: in each literal,
  `}"""` at its end becomes `,"providers":[]}"""` (for example `"""{"signup":"invite-only","private_signup":"off","providers":[]}"""`).
- `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`: the four literals (`self-service`/`self-service`, twice `invite-only`/`off`,
  `self-service`/`off`) the same way.
- `tests/Auth.IntegrationTests/SignupCompleteTests.cs`: `"""{"signup":"self-service","private_signup":"off"}"""` the same way.
- `tests/Auth.IntegrationTests/OpenApiTests.cs`: replace `        Assert.Equal(["private_signup", "signup"], Properties("OptionsResponse"));` with
  `        Assert.Equal(["private_signup", "providers", "signup"], Properties("OptionsResponse"));`.

In `tests/Auth.IntegrationTests/Infrastructure/AuthAppFactory.cs`:

(a) Add `using Auth.Server.Providers;` after `using Auth.Server.Network;`.

(b) Replace

```csharp
        _settings[MailSettingsLoader.CompleteSignupUrlKey] = "";
```

with

```csharp
        _settings[MailSettingsLoader.CompleteSignupUrlKey] = "";
        // Spec 0011: no provider setting unless a test asks for one (WithProviders): a variable of the machine cannot turn a provider on, or
        // stop a host that is not about providers.
        _settings[ProviderSettings.ReturnUrlKey] = "";
        _settings[ProviderSettings.GoogleClientIdKey] = "";
        _settings[ProviderSettings.GoogleClientSecretKey] = "";
        _settings[ProviderSettings.MicrosoftClientIdKey] = "";
        _settings[ProviderSettings.MicrosoftClientSecretKey] = "";
        _settings[ProviderSettings.MicrosoftSecretExpiresOnKey] = "";
        _settings[ProviderSettings.TestIssuerKey] = "";
```

(c) Replace

```csharp
    public const string DefaultFrom = "no-reply@example.com";
```

with

```csharp
    public const string DefaultFrom = "no-reply@example.com";

    /// <summary>The return screen of the provider round trip (spec 0011) in a test host; its origin is the callback's.</summary>
    public const string DefaultProviderReturnUrl = "https://app.example.com/provider-return";

    public const string GoogleClientId = "google-client-id.apps.test";
    public const string GoogleClientSecret = "google-client-secret-of-the-tests";
    public const string MicrosoftClientId = "6f1a2b3c-0000-4000-8000-00000000c11e";
    public const string MicrosoftClientSecret = "microsoft-client-secret-of-the-tests";

    /// <summary>The line that lists both providers (spec 0011 → Manifest).</summary>
    public const string ProvidersLine = "\nproviders: [google, microsoft]\n";
```

(d) Replace

```csharp
    /// <summary>Removes the manifest file, as if the product had not shipped one.</summary>
```

with

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

    /// <summary>Removes the manifest file, as if the product had not shipped one.</summary>
```

- [ ] **Step 2: See them fail.** Build: `ProviderSettings` does not exist.

- [ ] **Step 3: The settings.** In `src/Auth.Server/Email/MailSettings.cs`, replace
`    private static Uri? OptionalFrontendUrl(IConfiguration configuration, string key, bool isDevelopment) =>` with
`    internal static Uri? OptionalFrontendUrl(IConfiguration configuration, string key, bool isDevelopment) =>`.

`src/Auth.Server/Providers/ProviderSettings.cs`:

```csharp
using System.Globalization;
using Auth.Server.Email;
using Microsoft.AspNetCore.WebUtilities;

namespace Auth.Server.Providers;

/// <summary>The client id and the client secret a provider issued to the product. A class, not a record: a record would print the secret.</summary>
public sealed class ProviderKeys(string clientId, string clientSecret)
{
    public string ClientId { get; } = clientId;

    public string ClientSecret { get; } = clientSecret;
}

/// <summary>
/// The settings of spec 0011 → Configuration: the product's return screen, the keys of each provider, the day the Microsoft secret lapses and, in
/// Development only, the test issuer. Read once, at start: a bad value stops the start naming its key and never echoing the value; a missing one
/// never does, it only leaves its provider unoffered (<see cref="MissingKeysOf"/>).
/// </summary>
public sealed class ProviderSettings
{
    public const string ReturnUrlKey = "Auth:App:FrontendUrls:ProviderReturn";
    public const string GoogleClientIdKey = "Auth:Providers:Google:ClientId";
    public const string GoogleClientSecretKey = "Auth:Providers:Google:ClientSecret";
    public const string MicrosoftClientIdKey = "Auth:Providers:Microsoft:ClientId";
    public const string MicrosoftClientSecretKey = "Auth:Providers:Microsoft:ClientSecret";
    public const string MicrosoftSecretExpiresOnKey = "Auth:Providers:Microsoft:ClientSecretExpiresOn";
    public const string TestIssuerKey = "Auth:Providers:TestIssuer";

    private const string DateFormat = "yyyy-MM-dd";

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _missing;

    private ProviderSettings(IReadOnlyDictionary<string, IReadOnlyList<string>> missing) => _missing = missing;

    /// <summary>The product's return screen; <see langword="null"/> when it is not set, and then no provider is offered.</summary>
    public Uri? ReturnUrl { get; private init; }

    public ProviderKeys? Google { get; private init; }

    public ProviderKeys? Microsoft { get; private init; }

    /// <summary>The day the Microsoft client secret lapses at Microsoft; <see langword="null"/>: nothing is checked.</summary>
    public DateOnly? MicrosoftSecretExpiresOn { get; private init; }

    /// <summary>The fake provider's address (Development only): both providers are then plain OpenID Connect clients of it.</summary>
    public Uri? TestIssuer { get; private init; }

    /// <summary>The origin of the return screen: the origin of every callback address, and the client's own URI (Amendment A2).</summary>
    public Uri? ReturnOrigin => ReturnUrl is null ? null : new Uri(ReturnUrl.GetLeftPart(UriPartial.Authority));

    public ProviderKeys? KeysOf(string provider) => provider switch
    {
        ProviderNames.Google => Google,
        ProviderNames.Microsoft => Microsoft,
        _ => null,
    };

    /// <summary>The keys of configuration a provider still needs before it can be offered, in the order of the contract; empty when none.</summary>
    public IReadOnlyList<string> MissingKeysOf(string provider) => _missing[provider];

    /// <summary>Reads and checks the settings. Errors name the key and never echo a value.</summary>
    /// <param name="isDevelopment">Development accepts an <c>http</c> return address and the test issuer.</param>
    /// <exception cref="InvalidOperationException">A value is set and invalid.</exception>
    public static ProviderSettings Load(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // The rules of the completion address (spec 0009), and no `ticket` parameter: the ticket is added in the fragment.
        var returnUrl = MailSettingsLoader.OptionalFrontendUrl(configuration, ReturnUrlKey, isDevelopment);
        if (returnUrl is not null && QueryHelpers.ParseQuery(returnUrl.Query).ContainsKey("ticket"))
        {
            throw new InvalidOperationException($"Configuration value '{ReturnUrlKey}' must not hold a 'ticket' parameter.");
        }

        var missing = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var google = KeysAt(configuration, GoogleClientIdKey, GoogleClientSecretKey, returnUrl is null, out var googleMissing);
        missing[ProviderNames.Google] = googleMissing;
        var microsoft = KeysAt(configuration, MicrosoftClientIdKey, MicrosoftClientSecretKey, returnUrl is null, out var microsoftMissing);
        missing[ProviderNames.Microsoft] = microsoftMissing;

        return new ProviderSettings(missing)
        {
            ReturnUrl = returnUrl,
            Google = google,
            Microsoft = microsoft,
            MicrosoftSecretExpiresOn = SecretDate(configuration),
            TestIssuer = TestIssuerAt(configuration, isDevelopment),
        };
    }

    /// <summary>Both keys set (not blank): the keys, used exactly as configured; otherwise <see langword="null"/>, and the missing ones named.</summary>
    private static ProviderKeys? KeysAt(IConfiguration configuration, string idKey, string secretKey, bool noReturnAddress, out IReadOnlyList<string> missing)
    {
        var id = configuration[idKey];
        var secret = configuration[secretKey];
        var absent = new List<string>(3);
        if (string.IsNullOrWhiteSpace(id))
        {
            absent.Add(idKey);
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            absent.Add(secretKey);
        }

        if (noReturnAddress)
        {
            absent.Add(ReturnUrlKey);
        }

        missing = absent;
        return string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) ? null : new ProviderKeys(id, secret);
    }

    private static DateOnly? SecretDate(IConfiguration configuration)
    {
        var text = configuration[MicrosoftSecretExpiresOnKey];
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateOnly.TryParseExact(text.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new InvalidOperationException($"Configuration value '{MicrosoftSecretExpiresOnKey}' must be a date written yyyy-mm-dd.");
    }

    private static Uri? TestIssuerAt(IConfiguration configuration, bool isDevelopment)
    {
        var text = configuration[TestIssuerKey];
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Settled 23: a production instance must never trust a provider an operator typed by mistake.
        if (!isDevelopment)
        {
            throw new InvalidOperationException($"Configuration value '{TestIssuerKey}' is for the Development environment only: remove it.");
        }

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var issuer)
            || (issuer.Scheme != Uri.UriSchemeHttp && issuer.Scheme != Uri.UriSchemeHttps)
            || issuer.Query.Length > 0
            || issuer.Fragment.Length > 0
            || issuer.UserInfo.Length > 0)
        {
            throw new InvalidOperationException(
                $"Configuration value '{TestIssuerKey}' must be an absolute http or https URL without a query, a fragment or credentials.");
        }

        return issuer;
    }
}
```

- [ ] **Step 4: What is offered, and health.** `src/Auth.Server/Providers/ProviderCatalog.cs`:

```csharp
using System.Globalization;
using Auth.Server.Tenancy;

namespace Auth.Server.Providers;

/// <summary>
/// Which providers are offered (spec 0011 → General rules): listed by the active manifest, both keys set and the return address set. It reads the
/// manifest of the process at every call, as every request does, so a provider dropped from the manifest is not offered to the next request
/// (the callback then answers <c>provider_unavailable</c>). It also judges health: a listed provider that is not offered, and the Microsoft secret
/// from 30 days before its date on.
/// </summary>
public sealed partial class ProviderCatalog(ManifestHolder manifest, ProviderSettings settings, TimeProvider clock, ILogger<ProviderCatalog> logger)
{
    /// <summary>From this many days before the secret's date, and after it, health says <c>Degraded</c>.</summary>
    public const int SecretWarningDays = 30;

    /// <summary>The offered providers, in the manifest's order: the buttons of <c>GET /auth/options</c>.</summary>
    public IReadOnlyList<string> Offered => [.. manifest.Current.Providers.Where(p => settings.MissingKeysOf(p).Count == 0)];

    public bool IsOffered(string provider) =>
        manifest.Current.Providers.Contains(provider, StringComparer.Ordinal) && settings.MissingKeysOf(provider).Count == 0;

    /// <summary>A provider the manifest lists lacks a key or the return address: a misconfiguration.</summary>
    public bool ListedButNotOffered => manifest.Current.Providers.Any(p => settings.MissingKeysOf(p).Count > 0);

    /// <summary>The Microsoft secret lapses within <see cref="SecretWarningDays"/> days, or has lapsed. Without the setting nothing is checked.</summary>
    public bool SecretLapsesSoon =>
        settings.MicrosoftSecretExpiresOn is { } date
        && date.DayNumber - DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).DayNumber <= SecretWarningDays;

    public bool IsDegraded => ListedButNotOffered || SecretLapsesSoon;

    /// <summary>Called once at start, after the manifest is active: one warning for each listed provider that is not offered, and one for the secret.</summary>
    public void WarnAtStart()
    {
        foreach (var provider in manifest.Current.Providers)
        {
            var missing = settings.MissingKeysOf(provider);
            if (missing.Count > 0)
            {
                LogNotOffered(logger, provider, string.Join(", ", missing));
            }
        }

        if (SecretLapsesSoon && settings.MicrosoftSecretExpiresOn is { } date)
        {
            LogSecretLapses(logger, ProviderSettings.MicrosoftSecretExpiresOnKey, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The manifest lists {Provider}, but these settings are not set: {Keys}. The provider is not offered.")]
    private static partial void LogNotOffered(ILogger logger, string provider, string keys);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Microsoft client secret lapses on {Date} ({Key}): rotate it before then (docs/operations/microsoft-secret.md).")]
    private static partial void LogSecretLapses(ILogger logger, string key, string date);
}
```

`src/Auth.Server/Providers/ProviderHealthCheck.cs`:

```csharp
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Auth.Server.Providers;

/// <summary>
/// <c>Degraded</c> while a provider the manifest lists is not offered, or the Microsoft secret lapses within 30 days or has lapsed (spec 0011 →
/// Configuration). The answer of <c>/auth/health</c> is still <c>200</c>; the reason is in the start's log, not in a public response.
/// </summary>
public sealed class ProviderHealthCheck(ProviderCatalog catalog) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(catalog.IsDegraded
            ? HealthCheckResult.Degraded("A listed provider is not offered, or the Microsoft client secret lapses within 30 days.")
            : HealthCheckResult.Healthy());
}
```

- [ ] **Step 5: The options.** In `src/Auth.Server/Signup/SignupContracts.cs`, replace

```csharp
/// <summary>
/// Response of <c>GET /auth/options</c>: <c>signup</c> (spec 0009) and <c>private_signup</c> (spec 0010), each following its switch. A client
/// ignores members it does not know.
/// </summary>
public sealed record OptionsResponse(string Signup, string PrivateSignup);
```

with

```csharp
/// <summary>
/// Response of <c>GET /auth/options</c>: <c>signup</c> (spec 0009) and <c>private_signup</c> (spec 0010), each following its switch, and
/// <c>providers</c> (spec 0011), the offered providers in the manifest's order, <c>[]</c> when none. A client ignores members it does not know.
/// </summary>
public sealed record OptionsResponse(string Signup, string PrivateSignup, IReadOnlyList<string> Providers);
```

In `src/Auth.Server/Signup/SignupOptionsEndpoint.cs`, replace

```csharp
    public static IResult Handle(SignupGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return ApiResults.Ok(new OptionsResponse(gate.ModeName, gate.PrivateModeName));
    }
```

with

```csharp
    public static IResult Handle(SignupGate gate, ProviderCatalog providers)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(providers);

        return ApiResults.Ok(new OptionsResponse(gate.ModeName, gate.PrivateModeName, providers.Offered));
    }
```

and add `using Auth.Server.Providers;` after `using Auth.Server.Api;`.

In `src/Auth.Server/Api/SignupRoutes.cs`, replace

```csharp
                + "`private_signup` is `self-service` while private sign-up is on and `off` otherwise. A client ignores members it does not know.")
```

with

```csharp
                + "`private_signup` is `self-service` while private sign-up is on and `off` otherwise; `providers` lists the providers a screen may "
                + "offer as buttons (`google`, `microsoft`), in the manifest's order, `[]` when none. A client ignores members it does not know.")
```

- [ ] **Step 6: The start.** In `src/Auth.Server/Program.cs`:

(a) Add `using Auth.Server.Providers;` after `using Auth.Server.Network;`.

(b) Replace

```csharp
builder.Services.AddSingleton(MailSettingsLoader.Load(builder.Configuration, builder.Environment.IsDevelopment()));
```

with

```csharp
builder.Services.AddSingleton(MailSettingsLoader.Load(builder.Configuration, builder.Environment.IsDevelopment()));
// Spec 0011: the providers' settings. A bad value stops the start naming its key; a missing one only leaves its provider unoffered.
var providerSettings = ProviderSettings.Load(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddSingleton(providerSettings);
```

(c) Replace

```csharp
builder.Services.AddHealthChecks().AddCheck<ManifestHealthCheck>("manifest").AddCheck<SignupHealthCheck>("signup");
```

with

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<ManifestHealthCheck>("manifest")
    .AddCheck<SignupHealthCheck>("signup")
    .AddCheck<ProviderHealthCheck>("providers");
```

(d) Replace `builder.Services.AddSingleton<SignupGate>();` with

```csharp
builder.Services.AddSingleton<SignupGate>();
builder.Services.AddSingleton<ProviderCatalog>();
```

(e) Replace

```csharp
app.Services.GetRequiredService<SignupGate>().WarnIfMissingAddress();
```

with

```csharp
app.Services.GetRequiredService<SignupGate>().WarnIfMissingAddress();

// A provider the manifest lists without its keys or the return address is not offered: say so, naming the keys, never their values (spec 0011).
app.Services.GetRequiredService<ProviderCatalog>().WarnAtStart();
```

- [ ] **Step 7: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ProviderSettingsTests -class Auth.IntegrationTests.ProviderOptionsTests -class Auth.IntegrationTests.SignupOptionsTests -class Auth.IntegrationTests.PrivateSignupGateTests -class Auth.IntegrationTests.ShippedConfigurationTests -class Auth.IntegrationTests.SignupCompleteTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.CompleteSignupSettingsTests -class Auth.IntegrationTests.MailSettingsTests -class Auth.IntegrationTests.HealthMethodsTests`
passes.

- [ ] **Step 8: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): provider settings, the offered providers in /auth/options and health`.

---

## Day 2 — Saturday 25.04: the fake provider, the client, the round trip

When the day is done, a browser (or a test playing one) can press a button, sign in at the fake Google or Microsoft and come back to the
product's return screen with a ticket in the fragment and `auth_pt` in its cookies, or with one of the four error codes. Nothing uses a ticket yet.

### Task 4: The fake OpenID provider `tests/Auth.FakeOidc`

**Files:**
- Create: `tests/Auth.FakeOidc/Auth.FakeOidc.csproj`, `tests/Auth.FakeOidc/FakeOidcEntry.cs`, `tests/Auth.FakeOidc/FakeOidcOptions.cs`, `tests/Auth.FakeOidc/FakeSignIn.cs`, `tests/Auth.FakeOidc/FakePeople.cs`, `tests/Auth.FakeOidc/FakeOidcServer.cs`
- Modify: `Auth-Core.slnx`, `tests/Auth.IntegrationTests/Auth.IntegrationTests.csproj`
- Create (tests): `tests/Auth.IntegrationTests/Infrastructure/FakeOidcHost.cs`, `tests/Auth.IntegrationTests/FakeOidcTests.cs`

**Interfaces:**
- Consumes: nothing of Auth-Core (the fake is a project of its own; the tests reference it).
- Produces (namespace `Auth.FakeOidc`): `FakeOidcServer(FakeOidcOptions options, TimeProvider clock)` with `Task HandleAsync(HttpContext)`, `IReadOnlyCollection<string> IssuedIdentityTokens`, constants `GoogleIssuer`, `MicrosoftIssuerTemplate`, `RefusalDescription`, `OtherTenant`; `FakeOidcOptions` (`IssuerOrigin`, `BrowserOrigin`, `IssuerOriginKey`, `BrowserOriginKey`, `From(IConfiguration)`); `record FakeSignIn(string Person, string? Email = null, bool? Verified = null, string? Subject = null, string? Tenant = null, string? Edov = null, string? Defect = null)` with `string AppendTo(string authorizationUrl)` and `static FakeSignIn? From(IQueryCollection)`; `FakePeople` (`Google`, `MicrosoftPersonal`, `MicrosoftWork`, `Custom`, `Cancel`, `PersonalTenant`, `WorkTenant`, `GoogleSubject`, `GoogleEmail`, `PersonalObjectId`, `PersonalEmail`, `WorkObjectId`, `WorkEmail`, `Dictionary<string, object>? ClaimsFor(string kind, FakeSignIn)`); `FakeKinds` (`Google`, `Microsoft`); `FakeDefects` (`NoSubject`, `NoTenant`, `NoObjectId`, `NoEmail`, `WrongAudience`, `WrongNonce`, `WrongIssuer`, `BadSignature`, `RefuseCode`, `RefuseClient`, `ProviderError`); test side `FakeOidcHost(TimeProvider clock, FakeOidcOptions? options = null)` with `Server`, `Client` (plays the browser at the fake; never follows a redirect) and `Routing` (`IPostConfigureOptions<HttpClientFactoryOptions>` to register in a host).

**Contract counts:** none.

**Decisions the plan takes here:**
- One process serves the web providers' real host names (`accounts.google.com`, `oauth2.googleapis.com`, `www.googleapis.com`,
  `login.microsoftonline.com`; reached only through the test routing) and the test issuer's paths `/{google|microsoft}/...` on any other host
  (the container). Its discovery documents mirror the real ones where OpenIddict reads them (probe Q5, Q6): Google advertises
  `authorization_response_iss_parameter_supported` and the fake sends `iss` on every Google answer of the authorization endpoint (A5); Microsoft's
  issuer is the `{tenantid}` placeholder and its document names no PKCE methods.
- The fake's own query parameters (`person`, `email`, `verified`, `subject`, `tenant`, `edov`, `defect`) are read only on its authorization
  endpoint; the page builds them, scripts and tests write them (`FakeSignIn`). Without `person` the endpoint answers the page.
- A "custom" person (the page's free address) gets an identity derived from the address (Google `sub` `custom-` and 20 hex digits of SHA-256 of
  the upper-cased address; Microsoft `oid` the first 16 bytes of it as a GUID), so the same address is the same identity on the next round trip
  (the Playwright spec signs in again with the button alone). Its address is verified only when the box is ticked (Microsoft: `xms_edov` `true`).
- The fake checks what a real provider checks at its token endpoint (the code once, the client id, the redirect URI, the PKCE verifier) and
  accepts any client secret; it signs with one RSA key made at start (`kid` `fake-oidc-1`), and with a second key for `bad_signature`.
- The fake writes no log in the tests (its host clears the log providers) and never prints a code or a token.

- [ ] **Step 1: The project, so the tests can build.** `tests/Auth.FakeOidc/Auth.FakeOidc.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- The fake OpenID provider of spec 0011: Google, Microsoft and a test issuer, for the tests and the end-to-end stack. Never shipped. -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <RootNamespace>Auth.FakeOidc</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" />
  </ItemGroup>

</Project>
```

In `Auth-Core.slnx`, replace

```xml
    <Project Path="tests/Auth.IntegrationTests/Auth.IntegrationTests.csproj" />
```

with

```xml
    <Project Path="tests/Auth.FakeOidc/Auth.FakeOidc.csproj" />
    <Project Path="tests/Auth.IntegrationTests/Auth.IntegrationTests.csproj" />
```

In `tests/Auth.IntegrationTests/Auth.IntegrationTests.csproj`, replace

```xml
    <ProjectReference Include="..\..\src\Auth.Server\Auth.Server.csproj" />
```

with

```xml
    <ProjectReference Include="..\..\src\Auth.Server\Auth.Server.csproj" />
    <ProjectReference Include="..\Auth.FakeOidc\Auth.FakeOidc.csproj" />
```

The fake has an explicit `Main` in a namespaced class and no top-level statements, so it declares no global `Program` that would clash with
the `Program` of `Auth.Server` that `WebApplicationFactory<Program>` names; it has no `appsettings.json`, so nothing of it is copied over the
server's settings in the test output.

- [ ] **Step 2: Write the failing tests.** `tests/Auth.IntegrationTests/Infrastructure/FakeOidcHost.cs`:

```csharp
using Auth.FakeOidc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// The fake provider in process, behind a test server. OpenIddict's HTTP clients reach it through <see cref="Routing"/> whatever host they ask
/// for (accounts.google.com, login.microsoftonline.com, a test issuer), so nothing leaves the process; a test plays the browser at the fake with
/// <see cref="Client"/>, which never follows a redirect. It shares the test's clock, so the identity tokens it signs are valid at the time the host
/// sees.
/// </summary>
public sealed class FakeOidcHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpMessageHandler _handler;

    public FakeOidcHost(TimeProvider clock, FakeOidcOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        Server = new FakeOidcServer(options ?? new FakeOidcOptions(), clock);
        _app.Run(Server.HandleAsync);
        // A test server starts at once; the fake must be up before the first host of the test asks it for anything.
        _app.StartAsync().GetAwaiter().GetResult();
        _handler = _app.GetTestServer().CreateHandler();
        Client = new HttpClient(_handler, disposeHandler: false);
        Routing = new FakeOidcRouting(_handler);
    }

    public FakeOidcServer Server { get; }

    public HttpClient Client { get; }

    /// <summary>Register it in a host (<c>services.AddSingleton(Routing)</c>) before the host is built.</summary>
    public IPostConfigureOptions<HttpClientFactoryOptions> Routing { get; }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        _handler.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Server.Dispose();
    }
}

/// <summary>
/// Sends every request of OpenIddict's named HTTP clients, whose names start with the name of their assembly, to the fake (probe Q6). The
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

        options.HttpMessageHandlerBuilderActions.Add(builder => builder.AdditionalHandlers.Add(new ToTheFake(fake)));
    }

    private sealed class ToTheFake(HttpMessageHandler fake) : DelegatingHandler
    {
        private readonly HttpMessageInvoker _invoker = new(fake, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _invoker.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _invoker.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
```

`tests/Auth.IntegrationTests/FakeOidcTests.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Time.Testing;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011, criterion 34: the fake provider serves discovery, authorization, token and key-set endpoints and the three people.</summary>
public sealed class FakeOidcTests : IAsyncDisposable
{
    private const string Verifier = "a-verifier-of-at-least-forty-three-characters-0123456789";
    private const string ClientId = "the-client";
    private const string GoogleCallback = "https://app.example.com/auth/providers/google/callback";
    private const string MicrosoftCallback = "https://app.example.com/auth/providers/microsoft/callback";
    private const string GoogleAuthorize = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string MicrosoftAuthorize = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly FakeOidcHost _fake;

    public FakeOidcTests()
    {
        _fake = new FakeOidcHost(_clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _fake.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static string Challenge(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string AuthorizeUrl(string endpoint, string redirect, string clientId = ClientId) =>
        QueryHelpers.AddQueryString(endpoint, new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["scope"] = "openid email profile",
            ["state"] = "the-state",
            ["nonce"] = "the-nonce",
            ["code_challenge"] = Challenge(Verifier),
            ["code_challenge_method"] = "S256",
        });

    private async Task<JsonObject> JsonAsync(string url)
    {
        using var response = await _fake.Client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
    }

    /// <summary>The query of the fake's redirect back to the callback.</summary>
    private async Task<Dictionary<string, string>> AuthorizeAsync(string endpoint, string redirect, FakeSignIn person)
    {
        using var response = await _fake.Client.GetAsync(person.AppendTo(AuthorizeUrl(endpoint, redirect)), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(redirect + "?", location.AbsoluteUri, StringComparison.Ordinal);
        return QueryHelpers.ParseQuery(location.Query).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
    }

    private async Task<HttpResponseMessage> RedeemAsync(string tokenEndpoint, string code, string redirect, string verifier = Verifier, string clientId = ClientId)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["client_id"] = clientId,
            ["client_secret"] = "any-secret",
            ["code_verifier"] = verifier,
        });
        return await _fake.Client.PostAsync(tokenEndpoint, form, TestContext.Current.CancellationToken);
    }

    /// <summary>The identity token of a full round trip for this person, its payload as JSON.</summary>
    private async Task<(string Token, JsonObject Payload)> IdentityTokenAsync(bool google, FakeSignIn person)
    {
        var answer = await AuthorizeAsync(google ? GoogleAuthorize : MicrosoftAuthorize, google ? GoogleCallback : MicrosoftCallback, person);
        using var redeemed = await RedeemAsync(
            google ? "https://oauth2.googleapis.com/token" : "https://login.microsoftonline.com/common/oauth2/v2.0/token",
            answer["code"],
            google ? GoogleCallback : MicrosoftCallback);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var token = JsonNode.Parse(await redeemed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["id_token"]!.GetValue<string>();
        return (token, JsonNode.Parse(Base64UrlEncoder.Decode(token.Split('.')[1]))!.AsObject());
    }

    [Fact]
    public async Task Google_discovery_names_googles_endpoints_and_the_iss_parameter()   // criterion 34, Amendment A5
    {
        var document = await JsonAsync("https://accounts.google.com/.well-known/openid-configuration");

        Assert.Equal(FakeOidcServer.GoogleIssuer, document["issuer"]!.GetValue<string>());
        Assert.Equal(GoogleAuthorize, document["authorization_endpoint"]!.GetValue<string>());
        Assert.Equal("https://oauth2.googleapis.com/token", document["token_endpoint"]!.GetValue<string>());
        Assert.Equal("https://www.googleapis.com/oauth2/v3/certs", document["jwks_uri"]!.GetValue<string>());
        Assert.True(document["authorization_response_iss_parameter_supported"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Microsoft_discovery_names_the_common_endpoints_and_the_tenant_placeholder()   // criterion 34
    {
        var document = await JsonAsync("https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration");

        Assert.Equal(FakeOidcServer.MicrosoftIssuerTemplate, document["issuer"]!.GetValue<string>());
        Assert.Equal(MicrosoftAuthorize, document["authorization_endpoint"]!.GetValue<string>());
        Assert.Equal("https://login.microsoftonline.com/common/discovery/v2.0/keys", document["jwks_uri"]!.GetValue<string>());
        Assert.Null(document["code_challenge_methods_supported"]);
        Assert.Null(document["authorization_response_iss_parameter_supported"]);
    }

    [Fact]
    public async Task The_test_issuer_names_the_browser_origin_for_the_authorization_and_its_own_for_the_rest()   // criterion 34, probe Q7
    {
        await using var container = new FakeOidcHost(
            _clock, new FakeOidcOptions { IssuerOrigin = new Uri("http://fake-oidc:8099"), BrowserOrigin = new Uri("http://localhost:8099") });
        using var response = await container.Client.GetAsync("http://fake-oidc:8099/microsoft/.well-known/openid-configuration", TestContext.Current.CancellationToken);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();

        Assert.Equal("http://fake-oidc:8099/microsoft", document["issuer"]!.GetValue<string>());
        Assert.Equal("http://localhost:8099/microsoft/authorize", document["authorization_endpoint"]!.GetValue<string>());
        Assert.Equal("http://fake-oidc:8099/microsoft/token", document["token_endpoint"]!.GetValue<string>());
        Assert.Equal("http://fake-oidc:8099/microsoft/keys", document["jwks_uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_person_answers_at_once_with_a_code_the_state_and_for_google_iss()   // criterion 34: the non-interactive mode
    {
        var google = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google));
        var microsoft = await AuthorizeAsync(MicrosoftAuthorize, MicrosoftCallback, new FakeSignIn(FakePeople.MicrosoftWork));

        Assert.Equal(["code", "iss", "state"], google.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("the-state", google["state"]);
        Assert.Equal(FakeOidcServer.GoogleIssuer, google["iss"]);
        Assert.Equal(["code", "state"], microsoft.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Without_a_person_the_page_offers_the_people_a_free_address_and_cancel()   // Settled 22
    {
        using var google = await _fake.Client.GetAsync(AuthorizeUrl(GoogleAuthorize, GoogleCallback), TestContext.Current.CancellationToken);
        using var microsoft = await _fake.Client.GetAsync(AuthorizeUrl(MicrosoftAuthorize, MicrosoftCallback), TestContext.Current.CancellationToken);
        var googlePage = await google.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var microsoftPage = await microsoft.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, google.StatusCode);
        Assert.Equal("text/html", google.Content.Headers.ContentType?.MediaType);
        Assert.Contains(FakePeople.GoogleEmail, googlePage, StringComparison.Ordinal);
        Assert.Contains("name=\"email\"", googlePage, StringComparison.Ordinal);
        Assert.Contains("name=\"verified\"", googlePage, StringComparison.Ordinal);
        Assert.Contains(">Cancel<", googlePage, StringComparison.Ordinal);
        Assert.Contains(FakePeople.PersonalEmail, microsoftPage, StringComparison.Ordinal);
        Assert.Contains(FakePeople.WorkEmail, microsoftPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_answers_access_denied_with_the_state()   // criterion 8: what a person who cancels causes
    {
        var answer = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Cancel));

        Assert.Equal("access_denied", answer["error"]);
        Assert.Equal("the-state", answer["state"]);
        Assert.Equal(FakeOidcServer.RefusalDescription, answer["error_description"]);
        Assert.False(answer.ContainsKey("code"));
    }

    [Fact]
    public async Task The_token_endpoint_redeems_a_code_once_with_its_verifier_and_signs_with_the_published_key()   // criterion 34
    {
        var answer = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google));
        using var first = await RedeemAsync("https://oauth2.googleapis.com/token", answer["code"], GoogleCallback);
        using var second = await RedeemAsync("https://oauth2.googleapis.com/token", answer["code"], GoogleCallback);
        var keys = new JsonWebKeySet((await JsonAsync("https://www.googleapis.com/oauth2/v3/certs")).ToJsonString());
        var token = JsonNode.Parse(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["id_token"]!.GetValue<string>();

        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = FakeOidcServer.GoogleIssuer,
            ValidAudience = ClientId,
            IssuerSigningKeys = keys.GetSigningKeys(),
            LifetimeValidator = (_, _, _, _) => true,
        });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(validated.IsValid, validated.Exception?.Message);
        Assert.Equal(FakePeople.GoogleSubject, validated.Claims["sub"]);
        Assert.Equal(FakePeople.GoogleEmail, validated.Claims["email"]);
        Assert.Equal(true, validated.Claims["email_verified"]);
        Assert.Equal("the-nonce", validated.Claims["nonce"]);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Single(_fake.Server.IssuedIdentityTokens);
    }

    [Fact]
    public async Task A_wrong_verifier_another_client_or_a_refused_code_is_invalid_grant_with_a_description()   // criterion 8: the code the provider refuses
    {
        var one = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google));
        var two = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google));
        var three = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google, Defect: FakeDefects.RefuseCode));

        using var wrongVerifier = await RedeemAsync("https://oauth2.googleapis.com/token", one["code"], GoogleCallback, verifier: Verifier + "x");
        using var otherClient = await RedeemAsync("https://oauth2.googleapis.com/token", two["code"], GoogleCallback, clientId: "another-client");
        using var refused = await RedeemAsync("https://oauth2.googleapis.com/token", three["code"], GoogleCallback);

        foreach (var response in new[] { wrongVerifier, otherClient, refused })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal("invalid_grant", body["error"]!.GetValue<string>());
            Assert.Equal(FakeOidcServer.RefusalDescription, body["error_description"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task A_refused_client_is_invalid_client_with_a_description()   // spec 0011 → the runbook: a wrong or lapsed client secret
    {
        var answer = await AuthorizeAsync(GoogleAuthorize, GoogleCallback, new FakeSignIn(FakePeople.Google, Defect: FakeDefects.RefuseClient));

        using var refused = await RedeemAsync("https://oauth2.googleapis.com/token", answer["code"], GoogleCallback);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = JsonNode.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("invalid_client", body["error"]!.GetValue<string>());
        Assert.Equal(FakeOidcServer.RefusalDescription, body["error_description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(FakeDefects.NoSubject, "sub", null)]
    [InlineData(FakeDefects.NoEmail, "email", null)]
    [InlineData(FakeDefects.WrongAudience, "aud", "another-client")]
    [InlineData(FakeDefects.WrongNonce, "nonce", "another-nonce")]
    [InlineData(FakeDefects.WrongIssuer, "iss", "https://evil.example")]
    public async Task A_defect_changes_what_a_google_identity_token_says(string defect, string claim, string? value)   // criterion 34: on demand
    {
        var (_, payload) = await IdentityTokenAsync(google: true, new FakeSignIn(FakePeople.Google, Defect: defect));

        Assert.Equal(value, payload[claim]?.GetValue<string>());
    }

    [Theory]
    [InlineData(FakeDefects.NoTenant, "tid")]
    [InlineData(FakeDefects.NoObjectId, "oid")]
    public async Task A_defect_leaves_a_claim_out_of_a_microsoft_identity_token(string defect, string claim)   // criterion 34
    {
        var (_, payload) = await IdentityTokenAsync(google: false, new FakeSignIn(FakePeople.MicrosoftWork, Defect: defect));

        Assert.Null(payload[claim]);
    }

    [Fact]
    public async Task A_bad_signature_is_made_with_a_key_that_is_not_published()   // criterion 34
    {
        var (token, _) = await IdentityTokenAsync(google: true, new FakeSignIn(FakePeople.Google, Defect: FakeDefects.BadSignature));
        var keys = new JsonWebKeySet((await JsonAsync("https://www.googleapis.com/oauth2/v3/certs")).ToJsonString());

        var validated = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = FakeOidcServer.GoogleIssuer,
            ValidAudience = ClientId,
            IssuerSigningKeys = keys.GetSigningKeys(),
        });

        Assert.False(validated.IsValid);
    }

    [Fact]
    public async Task The_three_people_of_the_spec_are_there()   // criterion 34: Google, Microsoft personal, Microsoft work with an unverified address
    {
        var (_, google) = await IdentityTokenAsync(google: true, new FakeSignIn(FakePeople.Google));
        var (_, personal) = await IdentityTokenAsync(google: false, new FakeSignIn(FakePeople.MicrosoftPersonal));
        var (_, work) = await IdentityTokenAsync(google: false, new FakeSignIn(FakePeople.MicrosoftWork));

        Assert.True(google["email_verified"]!.GetValue<bool>());
        Assert.Equal(FakePeople.PersonalTenant, personal["tid"]!.GetValue<string>());
        Assert.Equal(FakePeople.PersonalObjectId, personal["oid"]!.GetValue<string>());
        Assert.Equal(FakePeople.PersonalEmail, personal["email"]!.GetValue<string>());
        Assert.Equal($"https://login.microsoftonline.com/{FakePeople.PersonalTenant}/v2.0", personal["iss"]!.GetValue<string>());
        Assert.Equal(FakePeople.WorkTenant, work["tid"]!.GetValue<string>());
        Assert.Equal(FakePeople.WorkEmail, work["email"]!.GetValue<string>());
        Assert.Null(work["xms_edov"]);
        Assert.NotEqual(FakePeople.WorkEmail, work["preferred_username"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("true", JsonValueKind.True)]
    [InlineData("false", JsonValueKind.False)]
    [InlineData("1", JsonValueKind.String)]
    public async Task The_work_person_carries_xms_edov_as_asked(string edov, JsonValueKind kind)   // criterion 9's inputs
    {
        var (_, work) = await IdentityTokenAsync(google: false, new FakeSignIn(FakePeople.MicrosoftWork, Edov: edov));

        Assert.Equal(kind, work["xms_edov"]!.GetValueKind());
    }

    [Fact]
    public async Task A_free_address_is_the_same_identity_on_every_round_trip_and_verified_only_when_ticked()   // Settled 22
    {
        var (_, first) = await IdentityTokenAsync(google: true, new FakeSignIn(FakePeople.Custom, Email: "new@e2e.test"));
        var (_, again) = await IdentityTokenAsync(google: true, new FakeSignIn(FakePeople.Custom, Email: "NEW@e2e.test", Verified: true));
        var (_, microsoft) = await IdentityTokenAsync(google: false, new FakeSignIn(FakePeople.Custom, Email: "new@e2e.test", Verified: true));

        Assert.Equal(first["sub"]!.GetValue<string>(), again["sub"]!.GetValue<string>());
        Assert.False(first["email_verified"]!.GetValue<bool>());
        Assert.True(again["email_verified"]!.GetValue<bool>());
        Assert.True(microsoft["xms_edov"]!.GetValue<bool>());
        Assert.True(Guid.TryParse(microsoft["oid"]!.GetValue<string>(), out _));
    }
}
```

- [ ] **Step 3: See them fail.** Build: the types of `Auth.FakeOidc` do not exist.

- [ ] **Step 4: The fake.** `tests/Auth.FakeOidc/FakeOidcOptions.cs`:

```csharp
using Microsoft.Extensions.Configuration;

namespace Auth.FakeOidc;

/// <summary>
/// Where the test issuer says its endpoints are. Both are optional; without them every address is the origin of the request. In the end-to-end
/// stack Auth-Core reaches the fake on the container network (<see cref="IssuerOrigin"/>, http://fake-oidc:8099: the issuer, the token and key
/// endpoints) and the browser reaches it on the host (<see cref="BrowserOrigin"/>, http://localhost:8099: the authorization endpoint).
/// </summary>
public sealed class FakeOidcOptions
{
    public const string IssuerOriginKey = "FakeOidc:IssuerOrigin";
    public const string BrowserOriginKey = "FakeOidc:BrowserOrigin";

    public Uri? IssuerOrigin { get; init; }

    public Uri? BrowserOrigin { get; init; }

    public static FakeOidcOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new FakeOidcOptions
        {
            IssuerOrigin = Origin(configuration[IssuerOriginKey], IssuerOriginKey),
            BrowserOrigin = Origin(configuration[BrowserOriginKey], BrowserOriginKey),
        };
    }

    private static Uri? Origin(string? text, string key)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            ? new Uri(uri.GetLeftPart(UriPartial.Authority))
            : throw new InvalidOperationException($"Configuration value '{key}' must be an absolute URL.");
    }
}
```

`tests/Auth.FakeOidc/FakeSignIn.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Auth.FakeOidc;

/// <summary>
/// Who signs in at the fake, and how: the fake's own query parameters on its authorization endpoint. <c>person</c> is one of
/// <see cref="FakePeople"/>; the others are optional: <c>email</c>, <c>verified</c> (<c>true</c> or <c>false</c>), <c>subject</c> (Google's
/// <c>sub</c> or Microsoft's <c>oid</c>), <c>tenant</c> (Microsoft's <c>tid</c>), <c>edov</c> (Microsoft's <c>xms_edov</c>: <c>true</c> and
/// <c>false</c> become JSON booleans, anything else a string) and <c>defect</c> (<see cref="FakeDefects"/>). The page builds them; a script or a
/// test writes them.
/// </summary>
public sealed record FakeSignIn(
    string Person, string? Email = null, bool? Verified = null, string? Subject = null, string? Tenant = null, string? Edov = null,
    string? Defect = null)
{
    /// <summary>The authorization address (absolute, or a path with its query) with these parameters added.</summary>
    public string AppendTo(string authorizationUrl)
    {
        ArgumentNullException.ThrowIfNull(authorizationUrl);

        var parameters = new List<KeyValuePair<string, string?>> { new("person", Person) };
        Add(parameters, "email", Email);
        Add(parameters, "verified", Verified switch { true => "true", false => "false", null => null });
        Add(parameters, "subject", Subject);
        Add(parameters, "tenant", Tenant);
        Add(parameters, "edov", Edov);
        Add(parameters, "defect", Defect);
        return QueryHelpers.AddQueryString(authorizationUrl, parameters);
    }

    /// <summary>The sign-in a request names; <see langword="null"/> when it names no person (the page is shown then).</summary>
    public static FakeSignIn? From(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (One(query, "person") is not { } person)
        {
            return null;
        }

        var verified = One(query, "verified");
        return new FakeSignIn(
            person,
            One(query, "email"),
            verified is null ? null : string.Equals(verified, "true", StringComparison.Ordinal),
            One(query, "subject"),
            One(query, "tenant"),
            One(query, "edov"),
            One(query, "defect"));
    }

    private static void Add(List<KeyValuePair<string, string?>> parameters, string name, string? value)
    {
        if (value is not null)
        {
            parameters.Add(new(name, value));
        }
    }

    private static string? One(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var values) && values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;
}
```

`tests/Auth.FakeOidc/FakePeople.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Auth.FakeOidc;

/// <summary>The two providers the fake plays.</summary>
public static class FakeKinds
{
    public const string Google = "google";
    public const string Microsoft = "microsoft";
}

/// <summary>What can go wrong on demand (<c>defect</c>): the cases of spec 0011, criterion 8.</summary>
public static class FakeDefects
{
    public const string NoSubject = "no_sub";
    public const string NoTenant = "no_tid";
    public const string NoObjectId = "no_oid";
    public const string NoEmail = "no_email";
    public const string WrongAudience = "wrong_audience";
    public const string WrongNonce = "wrong_nonce";
    public const string WrongIssuer = "wrong_issuer";
    public const string BadSignature = "bad_signature";
    public const string RefuseCode = "refuse_code";

    /// <summary>The token endpoint answers <c>invalid_client</c>, as a provider does for a wrong or lapsed client secret (spec 0011 → the runbook).</summary>
    public const string RefuseClient = "refuse_client";

    /// <summary>The authorization endpoint answers another error than <c>access_denied</c> (<c>server_error</c>).</summary>
    public const string ProviderError = "provider_error";
}

/// <summary>
/// The people of spec 0011 (Decision 20): a Google person, a Microsoft personal account and a Microsoft work account whose address the tenant did not
/// verify; a free address (<see cref="Custom"/>); and <see cref="Cancel"/>.
/// </summary>
public static class FakePeople
{
    public const string Google = "google";
    public const string MicrosoftPersonal = "microsoft-personal";
    public const string MicrosoftWork = "microsoft-work";
    public const string Custom = "custom";
    public const string Cancel = "cancel";

    public const string PersonalTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";
    public const string WorkTenant = "5c1a8d3e-0000-4000-8000-00000000f001";

    public const string GoogleSubject = "100000000000000000001";
    public const string GoogleEmail = "ola@gmail.test";
    public const string PersonalObjectId = "00000000-0000-0000-0000-00000000a001";
    public const string PersonalEmail = "ola@outlook.test";
    public const string WorkObjectId = "00000000-0000-0000-0000-00000000b001";
    public const string WorkEmail = "bo@firma.test";
    public const string WorkUserName = "bo@firma.onmicrosoft.test";

    /// <summary>
    /// The claims of the identity token of this person at this provider (<see cref="FakeKinds"/>), before the fake adds <c>iss</c>, <c>aud</c>,
    /// <c>nonce</c> and the times; <see langword="null"/> for a person this provider does not have.
    /// </summary>
    public static Dictionary<string, object>? ClaimsFor(string kind, FakeSignIn signIn)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(signIn);

        var claims = (kind, signIn.Person) switch
        {
            (FakeKinds.Google, Google) => GooglePerson(signIn.Subject ?? GoogleSubject, signIn.Email ?? GoogleEmail, signIn.Verified ?? true),
            (FakeKinds.Google, Custom) when signIn.Email is { } email =>
                GooglePerson(signIn.Subject ?? "custom-" + Digest(email)[..20], email, signIn.Verified ?? false),
            (FakeKinds.Microsoft, MicrosoftPersonal) =>
                MicrosoftPerson(PersonalTenant, signIn.Subject ?? PersonalObjectId, signIn.Email ?? PersonalEmail, signIn.Email ?? PersonalEmail, signIn.Edov),
            (FakeKinds.Microsoft, MicrosoftWork) =>
                MicrosoftPerson(signIn.Tenant ?? WorkTenant, signIn.Subject ?? WorkObjectId, signIn.Email ?? WorkEmail, WorkUserName, signIn.Edov),
            (FakeKinds.Microsoft, Custom) when signIn.Email is { } email =>
                MicrosoftPerson(signIn.Tenant ?? WorkTenant, signIn.Subject ?? GuidOf(email), email, email, signIn.Verified == true ? "true" : signIn.Edov),
            _ => null,
        };
        if (claims is null)
        {
            return null;
        }

        var missing = signIn.Defect switch
        {
            FakeDefects.NoSubject => "sub",
            FakeDefects.NoTenant => "tid",
            FakeDefects.NoObjectId => "oid",
            FakeDefects.NoEmail => "email",
            _ => null,
        };
        if (missing is not null)
        {
            claims.Remove(missing);
        }

        return claims;
    }

    private static Dictionary<string, object> GooglePerson(string subject, string email, bool verified) => new(StringComparer.Ordinal)
    {
        ["sub"] = subject,
        ["email"] = email,
        ["email_verified"] = verified,
        ["name"] = "Ola Test",
    };

    private static Dictionary<string, object> MicrosoftPerson(string tenant, string objectId, string email, string userName, string? edov)
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = "pairwise-" + objectId,
            ["oid"] = objectId,
            ["tid"] = tenant,
            ["email"] = email,
            ["preferred_username"] = userName,
            ["name"] = "Test Person",
            ["ver"] = "2.0",
        };
        if (!string.Equals(tenant, PersonalTenant, StringComparison.Ordinal))
        {
            claims["upn"] = userName;
        }

        if (edov is not null)
        {
            claims["xms_edov"] = edov switch
            {
                "true" => true,
                "false" => false,
                _ => edov,
            };
        }

        return claims;
    }

    private static byte[] Hash(string email) => SHA256.HashData(Encoding.UTF8.GetBytes(email.ToUpperInvariant()));

    private static string Digest(string email) => Convert.ToHexStringLower(Hash(email));

    private static string GuidOf(string email) => new Guid(Hash(email).AsSpan(0, 16)).ToString("D");
}
```

`tests/Auth.FakeOidc/FakeOidcServer.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Auth.FakeOidc;

/// <summary>
/// The fake OpenID provider of spec 0011 (Settled 22). It answers by host and path: the real host names of Google and Microsoft (which only the
/// tests' routing sends to it) with documents that mirror the real ones where OpenIddict reads them, and on any other host the test issuer's
/// paths <c>/{google|microsoft}/.well-known/openid-configuration</c>, <c>/authorize</c>, <c>/token</c> and <c>/keys</c>. The authorization endpoint
/// answers at once for a <see cref="FakeSignIn"/> in its query and shows a page otherwise.
/// </summary>
public sealed class FakeOidcServer : IDisposable
{
    public const string GoogleIssuer = "https://accounts.google.com";
    public const string MicrosoftIssuerTemplate = "https://login.microsoftonline.com/{tenantid}/v2.0";

    /// <summary>The <c>error_description</c> of every refusal: a test asserts it never reaches a person (spec 0011 → the callback).</summary>
    public const string RefusalDescription = "FAKE-PROVIDER-DESCRIPTION";

    /// <summary>The tenant whose issuer a Microsoft token with <c>wrong_issuer</c> names.</summary>
    public const string OtherTenant = "0b0b0b0b-0000-4000-8000-0000000000aa";

    private const string KeyId = "fake-oidc-1";

    private static readonly string[] CodeOnly = ["code"];
    private static readonly string[] ResponseTypes = ["code", "id_token", "code id_token"];
    private static readonly string[] ResponseModes = ["query", "fragment", "form_post"];
    private static readonly string[] PublicSubjects = ["public"];
    private static readonly string[] PairwiseSubjects = ["pairwise"];
    private static readonly string[] Rs256 = ["RS256"];
    private static readonly string[] Scopes = ["openid", "email", "profile"];
    private static readonly string[] AuthMethods = ["client_secret_post", "client_secret_basic"];
    private static readonly string[] S256Only = ["S256"];
    private static readonly string[] GoogleChallengeMethods = ["plain", "S256"];
    private static readonly string[] GoogleClaims = ["aud", "email", "email_verified", "exp", "iat", "iss", "name", "sub"];
    private static readonly string[] MicrosoftClaims = ["sub", "iss", "aud", "exp", "iat", "name", "email", "tid", "oid"];

    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Grant> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _identityTokens = new();
    private readonly FakeOidcOptions _options;
    private readonly TimeProvider _clock;

    public FakeOidcServer(FakeOidcOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _options = options;
        _clock = clock;
    }

    private enum Endpoint
    {
        Discovery,
        Authorize,
        Token,
        Keys,
    }

    /// <summary>Every identity token the token endpoint issued, oldest first: a test searches the audit log and the logs for them.</summary>
    public IReadOnlyCollection<string> IssuedIdentityTokens => [.. _identityTokens];

    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Route(context.Request) is not { } site)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        switch (site.Endpoint)
        {
            case Endpoint.Discovery:
                await WriteJsonAsync(context.Response, Discovery(site, context.Request), context.RequestAborted);
                break;
            case Endpoint.Authorize:
                await AuthorizeAsync(context, site);
                break;
            case Endpoint.Token:
                await TokenAsync(context);
                break;
            default:
                await WriteJsonAsync(context.Response, KeySet(), context.RequestAborted);
                break;
        }
    }

    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
    }

    private static Site? Route(HttpRequest request)
    {
        var host = request.Host.Host;
        var path = request.Path.Value ?? "/";
        if (Is(host, "accounts.google.com"))
        {
            return path switch
            {
                "/.well-known/openid-configuration" => new Site(FakeKinds.Google, true, Endpoint.Discovery),
                "/o/oauth2/v2/auth" => new Site(FakeKinds.Google, true, Endpoint.Authorize),
                _ => null,
            };
        }

        if (Is(host, "oauth2.googleapis.com"))
        {
            return path == "/token" ? new Site(FakeKinds.Google, true, Endpoint.Token) : null;
        }

        if (Is(host, "www.googleapis.com"))
        {
            return path == "/oauth2/v3/certs" ? new Site(FakeKinds.Google, true, Endpoint.Keys) : null;
        }

        if (Is(host, "login.microsoftonline.com"))
        {
            return path switch
            {
                "/common/v2.0/.well-known/openid-configuration" => new Site(FakeKinds.Microsoft, true, Endpoint.Discovery),
                "/common/oauth2/v2.0/authorize" => new Site(FakeKinds.Microsoft, true, Endpoint.Authorize),
                "/common/oauth2/v2.0/token" => new Site(FakeKinds.Microsoft, true, Endpoint.Token),
                "/common/discovery/v2.0/keys" => new Site(FakeKinds.Microsoft, true, Endpoint.Keys),
                _ => null,
            };
        }

        // The test issuer: /{google|microsoft}/... on any other host.
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || segments[0] is not (FakeKinds.Google or FakeKinds.Microsoft))
        {
            return null;
        }

        Endpoint? endpoint = string.Join('/', segments[1..]) switch
        {
            ".well-known/openid-configuration" => Endpoint.Discovery,
            "authorize" => Endpoint.Authorize,
            "token" => Endpoint.Token,
            "keys" => Endpoint.Keys,
            _ => null,
        };
        return endpoint is { } found ? new Site(segments[0], false, found) : null;
    }

    private static bool Is(string host, string name) => string.Equals(host, name, StringComparison.OrdinalIgnoreCase);

    private static string RequestOrigin(HttpRequest request) => request.Scheme + "://" + request.Host.Value;

    /// <summary>The test issuer of a kind: its configured origin (the container network) or the request's, then <c>/google</c> or <c>/microsoft</c>.</summary>
    private string IssuerOf(string kind, HttpRequest request) =>
        (_options.IssuerOrigin?.GetLeftPart(UriPartial.Authority) ?? RequestOrigin(request)) + "/" + kind;

    private string BrowserOf(string kind, HttpRequest request) =>
        (_options.BrowserOrigin?.GetLeftPart(UriPartial.Authority) ?? RequestOrigin(request)) + "/" + kind;

    private Dictionary<string, object> Discovery(Site site, HttpRequest request)
    {
        if (site.Web && site.Kind == FakeKinds.Google)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["issuer"] = GoogleIssuer,
                ["authorization_endpoint"] = "https://accounts.google.com/o/oauth2/v2/auth",
                ["token_endpoint"] = "https://oauth2.googleapis.com/token",
                ["userinfo_endpoint"] = "https://openidconnect.googleapis.com/v1/userinfo",
                ["jwks_uri"] = "https://www.googleapis.com/oauth2/v3/certs",
                ["response_types_supported"] = ResponseTypes,
                ["response_modes_supported"] = ResponseModes,
                ["subject_types_supported"] = PublicSubjects,
                ["id_token_signing_alg_values_supported"] = Rs256,
                ["scopes_supported"] = Scopes,
                ["token_endpoint_auth_methods_supported"] = AuthMethods,
                ["claims_supported"] = GoogleClaims,
                ["code_challenge_methods_supported"] = GoogleChallengeMethods,
                ["authorization_response_iss_parameter_supported"] = true,
            };
        }

        if (site.Web)
        {
            // As Microsoft's: the issuer names the {tenantid} placeholder, and no PKCE method is listed (OpenIddict adds S256 itself).
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["issuer"] = MicrosoftIssuerTemplate,
                ["authorization_endpoint"] = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
                ["token_endpoint"] = "https://login.microsoftonline.com/common/oauth2/v2.0/token",
                ["jwks_uri"] = "https://login.microsoftonline.com/common/discovery/v2.0/keys",
                ["userinfo_endpoint"] = "https://graph.microsoft.com/oidc/userinfo",
                ["response_types_supported"] = ResponseTypes,
                ["response_modes_supported"] = ResponseModes,
                ["subject_types_supported"] = PairwiseSubjects,
                ["id_token_signing_alg_values_supported"] = Rs256,
                ["scopes_supported"] = Scopes,
                ["token_endpoint_auth_methods_supported"] = AuthMethods,
                ["claims_supported"] = MicrosoftClaims,
            };
        }

        var issuer = IssuerOf(site.Kind, request);
        var document = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = BrowserOf(site.Kind, request) + "/authorize",
            ["token_endpoint"] = issuer + "/token",
            ["jwks_uri"] = issuer + "/keys",
            ["response_types_supported"] = CodeOnly,
            ["subject_types_supported"] = PublicSubjects,
            ["id_token_signing_alg_values_supported"] = Rs256,
            ["scopes_supported"] = Scopes,
            ["token_endpoint_auth_methods_supported"] = AuthMethods,
            ["code_challenge_methods_supported"] = S256Only,
        };
        if (site.Kind == FakeKinds.Google)
        {
            document["authorization_response_iss_parameter_supported"] = true;
        }

        return document;
    }

    private async Task AuthorizeAsync(HttpContext context, Site site)
    {
        var query = context.Request.Query;
        var clientId = One(query, "client_id");
        var redirectUri = One(query, "redirect_uri");
        var state = One(query, "state");
        var nonce = One(query, "nonce");
        var challenge = One(query, "code_challenge");
        if (!string.Equals(One(query, "response_type"), "code", StringComparison.Ordinal)
            || !string.Equals(One(query, "code_challenge_method"), "S256", StringComparison.Ordinal)
            || clientId is null || state is null || nonce is null || challenge is null
            || redirectUri is null || !Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
        {
            await PlainAsync(context.Response, "This is not an authorization request of the code flow with PKCE (S256), a state and a nonce.");
            return;
        }

        if (FakeSignIn.From(query) is not { } signIn)
        {
            await PageAsync(context, site);
            return;
        }

        var answer = new List<KeyValuePair<string, string?>>();
        if (signIn.Person == FakePeople.Cancel || signIn.Defect == FakeDefects.ProviderError)
        {
            answer.Add(new("error", signIn.Person == FakePeople.Cancel ? "access_denied" : "server_error"));
            answer.Add(new("error_description", RefusalDescription));
        }
        else
        {
            if (FakePeople.ClaimsFor(site.Kind, signIn) is not { } claims)
            {
                await PlainAsync(context.Response, "This provider has no such person.");
                return;
            }

            var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            _codes[code] = new Grant(clientId, redirectUri, nonce, challenge, claims, TokenIssuerOf(site, context.Request, claims, signIn.Defect), signIn.Defect);
            answer.Add(new("code", code));
        }

        answer.Add(new("state", state));
        if (site.Kind == FakeKinds.Google)
        {
            // Google advertises authorization_response_iss_parameter_supported: OpenIddict then requires iss on every answer (Amendment A5).
            answer.Add(new("iss", site.Web ? GoogleIssuer : IssuerOf(site.Kind, context.Request)));
        }

        context.Response.Redirect(QueryHelpers.AddQueryString(redirectUri, answer));
    }

    private string TokenIssuerOf(Site site, HttpRequest request, Dictionary<string, object> claims, string? defect)
    {
        var wrong = defect == FakeDefects.WrongIssuer;
        if (!site.Web)
        {
            return IssuerOf(site.Kind, request) + (wrong ? "-other" : "");
        }

        if (site.Kind == FakeKinds.Google)
        {
            return wrong ? "https://evil.example" : GoogleIssuer;
        }

        var tenant = wrong ? OtherTenant : claims.TryGetValue("tid", out var tid) ? (string)tid : FakePeople.WorkTenant;
        return $"https://login.microsoftonline.com/{tenant}/v2.0";
    }

    private async Task TokenAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !context.Request.HasFormContentType)
        {
            await RefuseAsync(context.Response, "invalid_request", context.RequestAborted);
            return;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var clientId = form.TryGetValue("client_id", out var formClient) ? formClient.ToString() : ClientIdOfBasic(context.Request);
        if (!string.Equals(form["grant_type"].ToString(), "authorization_code", StringComparison.Ordinal)
            || !_codes.TryRemove(form["code"].ToString(), out var grant)
            || grant.Defect == FakeDefects.RefuseCode
            || !string.Equals(clientId, grant.ClientId, StringComparison.Ordinal)
            || !string.Equals(form["redirect_uri"].ToString(), grant.RedirectUri, StringComparison.Ordinal)
            || !string.Equals(S256(form["code_verifier"].ToString()), grant.Challenge, StringComparison.Ordinal))
        {
            await RefuseAsync(context.Response, "invalid_grant", context.RequestAborted);
            return;
        }

        if (grant.Defect == FakeDefects.RefuseClient)
        {
            await RefuseAsync(context.Response, "invalid_client", context.RequestAborted);   // a wrong or lapsed client secret
            return;
        }

        var identityToken = Mint(grant);
        _identityTokens.Enqueue(identityToken);
        await WriteJsonAsync(
            context.Response,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["access_token"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                ["token_type"] = "Bearer",
                ["expires_in"] = 3600,
                ["scope"] = "openid email profile",
                ["id_token"] = identityToken,
            },
            context.RequestAborted);
    }

    private string Mint(Grant grant)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var claims = new Dictionary<string, object>(grant.Claims, StringComparer.Ordinal)
        {
            ["nonce"] = grant.Defect == FakeDefects.WrongNonce ? "another-nonce" : grant.Nonce,
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = grant.Issuer,
            Audience = grant.Defect == FakeDefects.WrongAudience ? "another-client" : grant.ClientId,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(grant.Defect == FakeDefects.BadSignature ? _otherKey : _key) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private Dictionary<string, object> KeySet()
    {
        var key = _key.ExportParameters(includePrivateParameters: false);
        var published = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["kid"] = KeyId,
            ["n"] = Base64UrlEncoder.Encode(key.Modulus!),   // RSAParameters holds both for a key made here; `!` for the nullable annotation
            ["e"] = Base64UrlEncoder.Encode(key.Exponent!),
        };
        return new Dictionary<string, object>(StringComparer.Ordinal) { ["keys"] = new object[] { published } };
    }

    /// <summary>The page of a browser that came without a person: the people, a free address with a "verified" box, and Cancel.</summary>
    private static async Task PageAsync(HttpContext context, Site site)
    {
        var request = context.Request;
        var here = (request.Path.Value ?? "/") + request.QueryString.Value;
        var name = site.Kind == FakeKinds.Google ? "Google" : "Microsoft";
        (string Person, string Label)[] people = site.Kind == FakeKinds.Google
            ? [(FakePeople.Google, $"Ola ({FakePeople.GoogleEmail}, verified)")]
            : [(FakePeople.MicrosoftPersonal, $"Ola, a personal account ({FakePeople.PersonalEmail})"),
               (FakePeople.MicrosoftWork, $"Bo, a work account ({FakePeople.WorkEmail}, not verified)")];

        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Fake ").Append(name).Append("</title></head><body>");
        html.Append("<h1>Sign in to the fake ").Append(name).Append("</h1><p>The test provider of Auth-Core. Choose who signs in.</p><ul>");
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

    /// <summary>The client id of <c>client_secret_basic</c> (RFC 6749 2.3.1: both parts form-urlencoded, then Base64); <see langword="null"/> when there is none.</summary>
    private static string? ClientIdOfBasic(HttpRequest request)
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
            return colon < 0 ? null : WebUtility.UrlDecode(pair[..colon]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? One(IQueryCollection query, string name) =>
        query.TryGetValue(name, out var values) && values.Count == 1 && !string.IsNullOrEmpty(values[0]) ? values[0] : null;

    private static async Task WriteJsonAsync(HttpResponse response, Dictionary<string, object> body, CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.Body, body, cancellationToken: cancellationToken);
    }

    private static async Task RefuseAsync(HttpResponse response, string error, CancellationToken cancellationToken)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        await WriteJsonAsync(
            response,
            new Dictionary<string, object>(StringComparer.Ordinal) { ["error"] = error, ["error_description"] = RefusalDescription },
            cancellationToken);
    }

    private static async Task PlainAsync(HttpResponse response, string text)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        response.ContentType = "text/plain; charset=utf-8";
        await response.WriteAsync(text);
    }

    private sealed record Site(string Kind, bool Web, Endpoint Endpoint);

    private sealed record Grant(
        string ClientId, string RedirectUri, string Nonce, string Challenge, Dictionary<string, object> Claims, string Issuer, string? Defect);
}
```

`tests/Auth.FakeOidc/FakeOidcEntry.cs`:

```csharp
using Microsoft.AspNetCore.Builder;

namespace Auth.FakeOidc;

/// <summary>
/// The fake provider as a process: the end-to-end stack runs it as a container (<c>tests/Auth.FakeOidc/compose.yml</c>). Every request goes to
/// <see cref="FakeOidcServer"/>. The port comes from <c>ASPNETCORE_HTTP_PORTS</c> (8099 in the image), the two origins from
/// <c>FakeOidc__IssuerOrigin</c> and <c>FakeOidc__BrowserOrigin</c> (<see cref="FakeOidcOptions"/>).
/// </summary>
public static class FakeOidcEntry
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        using var server = new FakeOidcServer(FakeOidcOptions.From(app.Configuration), TimeProvider.System);
        app.Run(server.HandleAsync);
        await app.RunAsync();
    }
}
```

- [ ] **Step 5: Run the tests.** Build the solution (the build gate builds the fake too), then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.FakeOidcTests`
passes. Also start the fake as its own process, in the background (the Bash tool with `run_in_background: true`):
`"C:/Program Files/dotnet/dotnet.exe" run --project tests/Auth.FakeOidc --urls http://127.0.0.1:8099`; when it listens, fetch
`http://127.0.0.1:8099/google/.well-known/openid-configuration` once (`curl -s`): the issuer is `http://127.0.0.1:8099/google`. Then stop that
process (`taskkill //F //IM Auth.FakeOidc.exe` in Git Bash) and check that port 8099 is free again (`netstat -ano | grep :8099` prints nothing).

- [ ] **Step 6: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`test: a fake OpenID provider for Google, Microsoft and a test issuer`.

---

### Task 5: OpenIddict's client and `GET /auth/providers/{name}/start`

**Files:**
- Create: `src/Auth.Server/Providers/ProviderPaths.cs`, `src/Auth.Server/Providers/ProviderPurposes.cs`, `src/Auth.Server/Providers/ProviderClientSetup.cs`, `src/Auth.Server/Providers/ProviderCallbackOrigin.cs`, `src/Auth.Server/Providers/ProviderStartEndpoint.cs`, `src/Auth.Server/Api/ProviderRoutes.cs`
- Modify: `src/Auth.Server/Providers/ProviderSettings.cs`, `src/Auth.Server/Tokens/OpenIddictSetup.cs`, `src/Auth.Server/Api/EndpointMetadata.cs`, `src/Auth.Server/Api/OpenApiSetup.cs`, `src/Auth.Server/appsettings.json`, `src/Auth.Server/Program.cs`
- Create (tests): `tests/Auth.IntegrationTests/Infrastructure/ProviderTestBase.cs`, `tests/Auth.IntegrationTests/Infrastructure/ProviderApi.cs`, `tests/Auth.IntegrationTests/ProviderStartTests.cs`, `tests/Auth.IntegrationTests/ProviderPathsTests.cs`
- Modify (tests): `tests/Auth.IntegrationTests/OpenApiTests.cs`, `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`

**Interfaces:**
- Consumes: `ProviderSettings`, `ProviderCatalog` (Task 3), `ProviderNames` (Task 2), `ProviderTickets.StateLifetime`, `ProviderTicketPurpose` (Task 1), `KeyMaterial`, `EmailTokens.HashOf`, `ApiResults`, `FakeOidcHost` (Task 4), `SignupTestBase`.
- Produces: `ProviderPaths` (`Prefix`, `Start`, `Callback`, `Exchange`, `Link`, `Finish`, `TokenPaths`, `CallbackPathOf(string)`, `StartPathOf(string)`, `IsCallbackPath(PathString)`); `ProviderPurposes` (`SignInName`, `InviteName`, `ReauthName`, `NameOf`, `TryParse`); `ProviderStateItems` (`Purpose`, `Provider`, `InviteHash`); `ProviderSettings.CallbackUriOf(string)`, `ProviderSettings.TestIssuerOf(string)`; `OpenIddictBuilder AddProviderClient(this OpenIddictBuilder, ProviderSettings, KeyMaterial)` and its handler `ProviderClientSetup.UserInfoOff`; `IApplicationBuilder UseProviderCallbackOrigin(this IApplicationBuilder, ProviderSettings)`; `ProviderStartEndpoint.Handle(HttpContext, string name, ProviderCatalog)` (`InviteParameter`, `PurposeParameter`, `IsLinkToken`); `ProviderRoutes.MapProviderApi(this IEndpointRouteBuilder)`; `EndpointMetadata.ReadsQuery(name, description)`, `EndpointMetadata.SetsHeader(status, name, description)` and the record `QueryParameterMetadata`; `AddAuthOpenIddict(..., KeyMaterial keys, ProviderSettings providers, bool isDevelopment)`; test side `ProviderTestBase` (`Fake`, `Browser`, `SetManifest(string yaml, bool withProviders = true)`, `StartAsync(string provider, string query = "")`) and `ProviderApi` (`Start` record, `StartAsync(HttpClient browser, string provider, string query = "", Action<HttpRequestMessage>? configure = null)`, `StateCookiePrefix`).

**Contract counts:** `OpenApiTests`: `Endpoints` gains `GET /auth/providers/{name}/start` (so `Every_429_carries_retry_after` counts 32), `Each_endpoint_describes_the_statuses_of_the_contract` gains `GET /auth/providers/{name}/start` `302,400,404,429`, `No_other_answer_says_it_sets_a_cookie` gains `GET /auth/providers/{name}/start 302` (OpenIddict's request-forgery cookie). `OpenApiHardeningTests.Every_operation_can_say_429_too_many_requests_with_retry_after_the_two_added_by_hand_included`: **31 → 32**.

**Decisions the plan takes here:**
- The client is registered whatever the configuration (it costs nothing without registrations, probe Q10 and the client's own checks); a
  registration is added only for a provider whose two keys and the return address are set. With the test issuer, both are plain OpenID Connect
  registrations of `{TestIssuer}/google` and `{TestIssuer}/microsoft`; otherwise the web providers, Microsoft with the tenant `common`.
- "Given twice" applies to `invite` and `purpose`; any other parameter, given once or twice, is ignored (spec: "Other parameters are ignored").
- The state carries the invitation token's SHA-256 in upper-case hex (`invite_hash`), the purpose and the provider; `RedirectUri` is `/` (T6).
- The start's `302` describes OpenIddict's request-forgery cookie in OpenAPI: it is a cookie this answer sets.

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/Infrastructure/ProviderApi.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>Drives the provider endpoints of spec 0011 as a browser and a product's screens would, carrying the cookies by hand.</summary>
public static class ProviderApi
{
    /// <summary>The start of OpenIddict's request-forgery cookie: its name ends with a value of each round trip.</summary>
    public const string StateCookiePrefix = "OpenIddict.Client.State.";

    /// <summary>What the start answered: the redirect to the provider, its query, and the request-forgery cookie as a browser sends it back.</summary>
    public sealed record Start(Uri Location, IReadOnlyDictionary<string, string> Query, string StateCookie, SetCookieHeaderValue StateCookieHeader);

    /// <summary>Starts a round trip and asserts the <c>302</c>.</summary>
    public static async Task<Start> StartAsync(HttpClient browser, string provider, string query = "", Action<HttpRequestMessage>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(browser);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/auth/providers/{provider}/start{query}");
        configure?.Invoke(request);
        using var response = await browser.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Found, $"Expected 302, got {(int)response.StatusCode}: {raw}");
        var location = response.Headers.Location ?? throw new InvalidOperationException("The start has no Location.");
        var cookie = Assert.Single(
            SetCookieHeaderValue.ParseList([.. response.Headers.GetValues("Set-Cookie")]),
            c => c.Name.Value!.StartsWith(StateCookiePrefix, StringComparison.Ordinal));
        return new Start(
            location,
            QueryHelpers.ParseQuery(location.Query).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal),
            $"{cookie.Name}={cookie.Value}",
            cookie);
    }
}
```

`tests/Auth.IntegrationTests/Infrastructure/ProviderTestBase.cs`:

```csharp
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests.Infrastructure;

/// <summary>
/// Base of the provider tests (spec 0011): the sign-up host of <see cref="SignupTestBase"/> with both kinds of sign-up on, both providers listed
/// and configured, and the fake provider in process behind OpenIddict's HTTP client, on the test's clock. <see cref="Browser"/> plays the browser
/// at Auth-Core (it never follows a redirect and sends no cookie by itself); <c>Fake.Client</c> plays it at the provider.
/// </summary>
public abstract class ProviderTestBase : SignupTestBase
{
    private HttpClient? _browser;

    protected ProviderTestBase(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Fake = new FakeOidcHost(Clock);
        Factory.WithPrivateSignup()
            .WithProviders()
            .WithServices(services => services.AddSingleton(Fake.Routing));
    }

    protected FakeOidcHost Fake { get; }

    protected HttpClient Browser =>
        _browser ??= Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    public override async ValueTask DisposeAsync()
    {
        _browser?.Dispose();
        await base.DisposeAsync();
        await Fake.DisposeAsync();
    }

    /// <summary>
    /// Makes this manifest active in the running host at once (the switches and the offered providers follow it), as a start with another file
    /// would; both providers stay listed unless <paramref name="withProviders"/> is <see langword="false"/>.
    /// </summary>
    protected void SetManifest(string yaml, bool withProviders = true)
    {
        var parsed = ManifestParser.Parse(yaml + (withProviders ? AuthAppFactory.ProvidersLine : ""));
        Assert.Null(parsed.Error);
        Holder.Set(parsed.Manifest!, null);
    }

    protected Task<ProviderApi.Start> StartAsync(string provider, string query = "") => ProviderApi.StartAsync(Browser, provider, query);
}
```

`tests/Auth.IntegrationTests/ProviderStartTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Lockout;
using Auth.Server.Sessions;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>GET /auth/providers/{name}/start</c>.</summary>
public sealed class ProviderStartTests : ProviderTestBase
{
    private const string Invite43 = "AAAAAAAAAA" + "AAAAAAAAAA" + "AAAAAAAAAA" + "AAAAAAAAAA" + "AAA";
    private static readonly string[] OpenIdScopes = ["email", "openid", "profile"];

    public ProviderStartTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        // As in PrivateSignupRequestTests: no background pass may log statements of its own while a test reads what a request ran.
        Factory.WithoutHostedService<TokenPruningService>().WithoutHostedService<LockoutPruningService>();
    }

    private List<string> StatementsSince(int mark) =>
        [.. Logs.Entries.Skip(mark).Where(e => e.Category == "Microsoft.EntityFrameworkCore.Database.Command").Select(e => e.Message)];

    [Theory]
    [InlineData("google", "https://accounts.google.com/o/oauth2/v2/auth", AuthAppFactory.GoogleClientId)]
    [InlineData("microsoft", "https://login.microsoftonline.com/common/oauth2/v2.0/authorize", AuthAppFactory.MicrosoftClientId)]
    public async Task The_start_answers_302_to_the_authorization_endpoint_with_the_code_flow_pkce_state_nonce_and_the_scopes(
        string provider, string endpoint, string clientId)   // criterion 5
    {
        var start = await StartAsync(provider);

        Assert.Equal(endpoint, start.Location.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", start.Query["response_type"]);
        Assert.Equal("S256", start.Query["code_challenge_method"]);
        Assert.Equal(43, start.Query["code_challenge"].Length);
        Assert.NotEmpty(start.Query["state"]);
        Assert.NotEmpty(start.Query["nonce"]);
        Assert.Equal(OpenIdScopes, start.Query["scope"].Split(' ').Order(StringComparer.Ordinal));
        Assert.Equal(clientId, start.Query["client_id"]);
        Assert.Equal($"https://app.example.com/auth/providers/{provider}/callback", start.Query["redirect_uri"]);
        Assert.Equal("select_account", start.Query["prompt"]);
        Assert.False(start.Query.ContainsKey("max_age"));
        Assert.False(start.Query.ContainsKey("response_mode"));   // query, the default of the code flow (probe Q2)
    }

    [Fact]
    public async Task The_request_forgery_cookie_is_lax_secure_http_only_and_scoped_to_the_provider_paths()   // Amendment A4
    {
        var cookie = (await StartAsync("google")).StateCookieHeader;

        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, cookie.SameSite);
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.Equal("/auth/providers", cookie.Path.Value);
        Assert.NotEqual(RefreshCookie.Name, cookie.Name.Value);
    }

    [Fact]
    public async Task The_callback_address_comes_from_the_return_address_whatever_the_host_headers_say()   // criterion 5, Review Focus 1
    {
        var start = await ProviderApi.StartAsync(Browser, "google", configure: request =>
        {
            request.Headers.Host = "evil.test";
            request.Headers.Add("X-Forwarded-Host", "evil.test");
            request.Headers.Add("X-Forwarded-Proto", "http");
        });

        Assert.Equal("https://app.example.com/auth/providers/google/callback", start.Query["redirect_uri"]);
    }

    [Theory]
    [InlineData("microsoft", "prompt", "login", "max_age")]
    [InlineData("google", "max_age", "0", "prompt")]
    public async Task Reauth_asks_microsoft_for_a_login_and_google_for_a_fresh_sign_in(string provider, string parameter, string value, string absent)   // criterion 5, Settled 26
    {
        var start = await StartAsync(provider, "?purpose=reauth");

        Assert.Equal(value, start.Query[parameter]);
        Assert.False(start.Query.ContainsKey(absent));
    }

    [Fact]
    public async Task An_invitation_start_carries_neither_the_token_nor_the_parameter_to_the_provider()   // spec 0011 → start, step 3
    {
        var start = await StartAsync("google", "?invite=" + Invite43);

        Assert.DoesNotContain(Invite43, start.Location.AbsoluteUri, StringComparison.Ordinal);
        Assert.False(start.Query.ContainsKey("invite"));
        Assert.Equal("select_account", start.Query["prompt"]);
    }

    [Theory]
    [InlineData("github")]
    [InlineData("Google")]
    [InlineData("password")]
    public async Task An_unknown_name_is_404_not_found(string name)   // criterion 6
    {
        using var response = await Browser.GetAsync($"/auth/providers/{name}/start");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task A_provider_that_is_not_offered_is_404_not_found()   // criterion 6
    {
        SetManifest(AuthAppFactory.DefaultManifest + "\nproviders: [google]\n", withProviders: false);

        using var response = await Browser.GetAsync("/auth/providers/microsoft/start");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    public static TheoryData<string> BadQueries => new()
    {
        "?invite=abc",
        "?invite=" + Invite43 + "A",
        "?invite=" + Invite43[..42] + ".",
        "?invite=" + Invite43[..42] + "%2B",
        "?invite=",
        "?purpose=login",
        "?purpose=",
        "?purpose=REAUTH",
        "?invite=" + Invite43 + "&purpose=reauth",
        "?purpose=reauth&purpose=reauth",
        "?invite=" + Invite43 + "&invite=" + Invite43,
    };

    [Theory]
    [MemberData(nameof(BadQueries))]
    public async Task A_malformed_invite_another_purpose_both_or_one_given_twice_is_400_invalid_request(string query)   // criterion 6
    {
        using var response = await Browser.GetAsync("/auth/providers/google/start" + query);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Other_parameters_are_ignored_once_or_twice()   // spec 0011 → start, step 2
    {
        var start = await StartAsync("google", "?utm_source=mail&utm_source=again&lang=pl");

        Assert.Equal("select_account", start.Query["prompt"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?purpose=reauth")]
    [InlineData("?invite=" + Invite43)]
    public async Task The_start_reads_no_account_and_no_invitation(string query)   // criterion 6: statement capture
    {
        var mark = Logs.Entries.Count;

        await StartAsync("google", query);

        var statements = StatementsSince(mark);
        Assert.Contains(statements, sql => sql.Contains("\"OpenIddictTokens\"", StringComparison.Ordinal));   // the state, and nothing else
        Assert.DoesNotContain(statements, sql =>
            sql.Contains("\"AspNetUsers\"", StringComparison.Ordinal)
            || sql.Contains("\"Invites\"", StringComparison.Ordinal)
            || sql.Contains("\"PendingSignups\"", StringComparison.Ordinal)
            || sql.Contains("\"Memberships\"", StringComparison.Ordinal));
    }
}
```

Change the contract tests:

(a) `tests/Auth.IntegrationTests/OpenApiTests.cs`:
- the comment `    // Every endpoint of specs 0001–0010, as "METHOD path".` becomes `    // Every endpoint of specs 0001–0011, as "METHOD path".` (copy the dash from the file: it is not an ASCII hyphen);
- in `Endpoints`, replace `        "POST /auth/signup/complete",` with `        "POST /auth/signup/complete",` followed by a new line `        "GET /auth/providers/{name}/start",`;
- after `    [InlineData("POST /auth/signup/complete", "200,400,403,409,415,429")]` add `    [InlineData("GET /auth/providers/{name}/start", "302,400,404,429")]`;
- replace `        string[] setting = ["POST /auth/login 200", "POST /auth/signup/complete 200", "POST /auth/refresh 200", "POST /auth/logout 204", "DELETE /auth/account 204"];` with
  `        string[] setting = ["POST /auth/login 200", "POST /auth/signup/complete 200", "POST /auth/refresh 200", "POST /auth/logout 204", "DELETE /auth/account 204", "GET /auth/providers/{name}/start 302"];`.

(b) `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`: replace `        Assert.Equal(31, operations.Count);   // spec 0010: DELETE /auth/account` with
`        Assert.Equal(32, operations.Count);   // spec 0011: the start of a provider round trip`.

`tests/Auth.IntegrationTests/ProviderPathsTests.cs` (the matcher of the Amendment A2 step: it must agree with the paths OpenIddict's redirection
endpoint answers, as `LoginScopeTests` pins the matchers of the login and the completion):

```csharp
using Auth.Server.Providers;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011, Amendment A2: the callback paths, matched as OpenIddict matches its redirection endpoints.</summary>
public sealed class ProviderPathsTests
{
    [Theory]
    [InlineData("/auth/providers/google/callback", true)]
    [InlineData("/auth/providers/microsoft/callback", true)]
    [InlineData("/AUTH/Providers/GOOGLE/Callback", true)]
    [InlineData("/auth/providers/google/callback/", true)]
    [InlineData("/auth/providers/google/callback//", false)]
    [InlineData("/auth/providers/google/callback/extra", false)]
    [InlineData("/auth/providers/github/callback", false)]
    [InlineData("/auth/providers/google/start", false)]
    [InlineData("/auth/providers/exchange", false)]
    [InlineData("/", false)]
    public void IsCallbackPath_matches_the_callback_paths_only(string path, bool expected) =>
        Assert.Equal(expected, ProviderPaths.IsCallbackPath(new PathString(path)));

    [Fact]
    public void The_paths_are_those_of_the_contract()
    {
        Assert.Equal("/auth/providers/google/start", ProviderPaths.StartPathOf("google"));
        Assert.Equal("/auth/providers/microsoft/callback", ProviderPaths.CallbackPathOf("microsoft"));
        Assert.Equal(["/auth/providers/exchange", "/auth/providers/link", "/auth/providers/finish"], ProviderPaths.TokenPaths);
    }
}
```

- [ ] **Step 2: See them fail.** Build, then run `ProviderStartTests`: the route does not exist (`404` with no body where a `302` is expected).

- [ ] **Step 3: Paths and purposes.** `src/Auth.Server/Providers/ProviderPaths.cs`:

```csharp
namespace Auth.Server.Providers;

/// <summary>The paths of spec 0011 → Settled 1.</summary>
public static class ProviderPaths
{
    public const string Prefix = "/auth/providers";
    public const string Start = "/auth/providers/{name}/start";
    public const string Callback = "/auth/providers/{name}/callback";
    public const string Exchange = "/auth/providers/exchange";
    public const string Link = "/auth/providers/link";
    public const string Finish = "/auth/providers/finish";

    /// <summary>The three POSTs that are token-endpoint requests of OpenIddict, as the sign-up completion is.</summary>
    public static IReadOnlyList<string> TokenPaths { get; } = [Exchange, Link, Finish];

    public static string StartPathOf(string provider) => Prefix + "/" + provider + "/start";

    public static string CallbackPathOf(string provider) => Prefix + "/" + provider + "/callback";

    /// <summary>The callback of a provider, case-insensitive and with an optional trailing slash: the paths OpenIddict's redirection endpoint answers.</summary>
    public static bool IsCallbackPath(PathString path) =>
        ProviderNames.All.Any(provider =>
            path.Equals(CallbackPathOf(provider), StringComparison.OrdinalIgnoreCase)
            || path.Equals(CallbackPathOf(provider) + "/", StringComparison.OrdinalIgnoreCase));
}
```

`src/Auth.Server/Providers/ProviderPurposes.cs`:

```csharp
using Auth.Infrastructure.Persistence;

namespace Auth.Server.Providers;

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
    public static bool TryParse(string? name, out ProviderTicketPurpose purpose)
    {
        (var known, purpose) = name switch
        {
            SignInName => (true, ProviderTicketPurpose.SignIn),
            InviteName => (true, ProviderTicketPurpose.Invite),
            ReauthName => (true, ProviderTicketPurpose.Reauth),
            _ => (false, ProviderTicketPurpose.SignIn),
        };
        return known;
    }
}

/// <summary>
/// What Auth-Core puts into the state of a round trip, beside OpenIddict's own: it travels encrypted (the client's credentials are the instance's
/// key files) and comes back at the callback whenever the state could be read (probe Q11). Never the invitation token, only its hash.
/// </summary>
public static class ProviderStateItems
{
    public const string Purpose = "purpose";
    public const string Provider = "provider";

    /// <summary>The SHA-256 of the invitation's link token, in hexadecimal.</summary>
    public const string InviteHash = "invite_hash";
}
```

In `src/Auth.Server/Providers/ProviderSettings.cs`, replace

```csharp
    public IReadOnlyList<string> MissingKeysOf(string provider) => _missing[provider];
```

with

```csharp
    public IReadOnlyList<string> MissingKeysOf(string provider) => _missing[provider];

    /// <summary>The callback address the operator registers at the provider: the return address's origin and the callback path (Settled 2).</summary>
    public Uri CallbackUriOf(string provider) =>
        new(ReturnOrigin ?? throw new InvalidOperationException("The return address is not set."), ProviderPaths.CallbackPathOf(provider));

    /// <summary>The issuer of a provider under the test issuer: <c>{TestIssuer}/google</c> or <c>{TestIssuer}/microsoft</c>.</summary>
    public Uri TestIssuerOf(string provider) =>
        new((TestIssuer ?? throw new InvalidOperationException("No test issuer is set.")).GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + provider);
```

- [ ] **Step 4: The client.** `src/Auth.Server/Providers/ProviderClientSetup.cs`:

```csharp
using Auth.Server.Keys;
using OpenIddict.Client;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Providers;

/// <summary>
/// OpenIddict's client beside the server in the one host (spec 0011, Decision 2; probe Q3): the authorization code flow with PKCE, the two
/// callback paths as its redirection endpoint in pass-through mode for successes and errors alike (so that Auth-Core always answers the callback
/// itself, probe Q1), the state for 10 minutes, protected by the instance's own key files (probe Q10), the request-forgery cookie
/// <c>SameSite=Lax</c> on <c>/auth/providers</c> (Amendment A4), userinfo off (the claims come from the identity token alone), and the client's URI
/// the return address's origin (Amendment A2, T4). A provider is registered only when its two keys and the return address are set: the web
/// providers of Google and Microsoft (tenant <c>common</c>), or, with the test issuer (Development), plain OpenID Connect registrations of it.
/// </summary>
public static class ProviderClientSetup
{
    public static OpenIddictBuilder AddProviderClient(this OpenIddictBuilder builder, ProviderSettings settings, KeyMaterial keys)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(keys);

        return builder.AddClient(options =>
        {
            options.AllowAuthorizationCodeFlow();
            options.SetRedirectionEndpointUris([.. ProviderNames.All.Select(p => ProviderPaths.CallbackPathOf(p).TrimStart('/'))]);
            options.SetStateTokenLifetime(ProviderTickets.StateLifetime);
            options.AddSigningCertificate(keys.Signing).AddEncryptionCertificate(keys.Encryption);
            options.DisableWebServicesFederationClaimMapping();
            options.AddEventHandler(UserInfoOff.Descriptor);
            if (settings.ReturnOrigin is { } origin)
            {
                options.SetClientUri(origin);
            }

            // TLS ends at the reverse proxy, as for the server; the callback's scheme and host are those of the return address (ProviderCallbackOrigin).
            options.UseAspNetCore()
                .EnableRedirectionEndpointPassthrough()
                .EnableErrorPassthrough()
                .DisableTransportSecurityRequirement()
                .Configure(aspNetCore =>
                {
                    aspNetCore.CookieBuilder.SameSite = SameSiteMode.Lax;
                    aspNetCore.CookieBuilder.Path = ProviderPaths.Prefix;
                });

            options.UseSystemNetHttp();

            if (settings.ReturnOrigin is null)
            {
                return;   // no provider can be offered without the return address
            }

            if (settings.TestIssuer is not null)
            {
                foreach (var provider in ProviderNames.All)
                {
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
                }

                return;
            }

            var web = options.UseWebProviders();
            if (settings.Google is { } google)
            {
                web.AddGoogle(registration => registration
                    .SetClientId(google.ClientId)
                    .SetClientSecret(google.ClientSecret)
                    .SetRedirectUri(settings.CallbackUriOf(ProviderNames.Google))
                    .AddScopes(Scopes.Email, Scopes.Profile)
                    .SetProviderName(ProviderNames.Google)
                    .SetProviderDisplayName(ProviderNames.DisplayNameOf(ProviderNames.Google)));
            }

            if (settings.Microsoft is { } microsoft)
            {
                web.AddMicrosoft(registration => registration
                    .SetClientId(microsoft.ClientId)
                    .SetClientSecret(microsoft.ClientSecret)
                    .SetRedirectUri(settings.CallbackUriOf(ProviderNames.Microsoft))
                    .SetTenant("common")
                    .AddScopes(Scopes.Email, Scopes.Profile)
                    .SetProviderName(ProviderNames.Microsoft)
                    .SetProviderDisplayName(ProviderNames.DisplayNameOf(ProviderNames.Microsoft)));
            }
        });
    }

    /// <summary>
    /// Turns userinfo off before OpenIddict decides to call it (probe Q5, T7): the identity rules read the identity token only, and a userinfo
    /// answer must never shadow one of its claims.
    /// </summary>
    public sealed class UserInfoOff : IOpenIddictClientHandler<OpenIddictClientEvents.ProcessAuthenticationContext>
    {
        public static OpenIddictClientHandlerDescriptor Descriptor { get; } =
            OpenIddictClientHandlerDescriptor.CreateBuilder<OpenIddictClientEvents.ProcessAuthenticationContext>()
                .UseSingletonHandler<UserInfoOff>()
                .SetOrder(OpenIddictClientHandlers.EvaluateUserInfoRequest.Descriptor.Order - 500)
                .SetType(OpenIddictClientHandlerType.Custom)
                .Build();

        public ValueTask HandleAsync(OpenIddictClientEvents.ProcessAuthenticationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            context.DisableUserInfoRetrieval = true;
            return ValueTask.CompletedTask;
        }
    }
}
```

`src/Auth.Server/Providers/ProviderCallbackOrigin.cs`:

```csharp
namespace Auth.Server.Providers;

/// <summary>
/// OpenIddict compares the URL of a callback request with the callback address it stored in the state, and refuses any difference (probe T5).
/// Behind a proxy that does not keep the browser's scheme and host, every callback would fail; and the address must never come from the request
/// (Settled 2). So for the two callback paths only, before authentication, the scheme and the host of the request are those of the return
/// address's origin (Amendment A2). Nothing else of the request changes, and no other path is touched.
/// </summary>
public static class ProviderCallbackOrigin
{
    public static IApplicationBuilder UseProviderCallbackOrigin(this IApplicationBuilder app, ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.ReturnOrigin is not { } origin)
        {
            return app;   // no provider is offered, and no round trip ever starts
        }

        var host = HostString.FromUriComponent(origin);
        return app.Use((context, next) =>
        {
            if (ProviderPaths.IsCallbackPath(context.Request.Path))
            {
                context.Request.Scheme = origin.Scheme;
                context.Request.Host = host;
            }

            return next(context);
        });
    }
}
```

In `src/Auth.Server/Tokens/OpenIddictSetup.cs`:

(a) Add `using Auth.Server.Providers;` after `using Auth.Server.Login;`.

(b) Replace

```csharp
    public static IServiceCollection AddAuthOpenIddict(this IServiceCollection services, IConfiguration configuration, KeyMaterial keys, bool isDevelopment)
```

with

```csharp
    /// <param name="providers">The provider settings of spec 0011: OpenIddict's client registers the providers they configure.</param>
    public static IServiceCollection AddAuthOpenIddict(
        this IServiceCollection services, IConfiguration configuration, KeyMaterial keys, ProviderSettings providers, bool isDevelopment)
```

(c) Replace

```csharp
                options.AddAudiences(tokens.Audience);
            });
```

with

```csharp
                options.AddAudiences(tokens.Audience);
            })
            // Spec 0011: the client of Google and Microsoft beside the server, in the same host (probe Q3).
            .AddProviderClient(providers, keys);
```

- [ ] **Step 5: The start.** `src/Auth.Server/Providers/ProviderStartEndpoint.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Email;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Client.AspNetCore;
using ClientProperties = OpenIddict.Client.AspNetCore.OpenIddictClientAspNetCoreConstants.Properties;

namespace Auth.Server.Providers;

/// <summary>
/// <c>GET /auth/providers/{name}/start</c> (spec 0011): the product's button is a link here. It decides what the round trip is for from the query,
/// reads no account and no invitation, and challenges OpenIddict's client, which answers <c>302</c> to the provider and sets its request-forgery
/// cookie. The purpose, the provider and the hash of an invitation token travel in the encrypted state.
/// </summary>
public static class ProviderStartEndpoint
{
    public const string InviteParameter = "invite";
    public const string PurposeParameter = "purpose";

    public static IResult Handle(HttpContext http, string name, ProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(catalog);

        // OpenIddict has no registration for a provider that is not configured, and must never be asked for one (probe Q4).
        if (!ProviderNames.IsName(name) || !catalog.IsOffered(name))
        {
            return ApiResults.NotFound();
        }

        var query = http.Request.Query;
        var invite = query[InviteParameter];
        var purposeText = query[PurposeParameter];
        if (invite.Count > 1 || purposeText.Count > 1 || (invite.Count == 1 && purposeText.Count == 1))
        {
            return ApiResults.InvalidRequest();
        }

        var purpose = ProviderTicketPurpose.SignIn;
        string? inviteHash = null;
        if (invite.Count == 1)
        {
            if (!IsLinkToken(invite[0]))
            {
                return ApiResults.InvalidRequest();
            }

            purpose = ProviderTicketPurpose.Invite;
            inviteHash = Convert.ToHexString(EmailTokens.HashOf(invite[0]!));
        }
        else if (purposeText.Count == 1)
        {
            if (!string.Equals(purposeText[0], ProviderPurposes.ReauthName, StringComparison.Ordinal))
            {
                return ApiResults.InvalidRequest();
            }

            purpose = ProviderTicketPurpose.Reauth;
        }

        // RedirectUri is set on purpose: left out, OpenIddict keeps the address of this request, the invitation token included, in the state (T6).
        var properties = new AuthenticationProperties { RedirectUri = "/" };
        properties.Items[ClientProperties.ProviderName] = name;
        properties.Items[ProviderStateItems.Purpose] = ProviderPurposes.NameOf(purpose);
        properties.Items[ProviderStateItems.Provider] = name;
        if (inviteHash is not null)
        {
            properties.Items[ProviderStateItems.InviteHash] = inviteHash;
        }

        if (purpose == ProviderTicketPurpose.Reauth)
        {
            // A fresh sign-in at the provider (Settled 26); Auth-Core does not check that one happened (residual risk 57).
            if (name == ProviderNames.Microsoft)
            {
                properties.Parameters["prompt"] = "login";
            }
            else
            {
                properties.Parameters["max_age"] = 0;
            }
        }
        else
        {
            properties.Parameters["prompt"] = "select_account";
        }

        return Results.Challenge(properties, [OpenIddictClientAspNetCoreDefaults.AuthenticationScheme]);
    }

    /// <summary>A link token of spec 0004: 43 base64url characters.</summary>
    internal static bool IsLinkToken(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
```

- [ ] **Step 6: The route and its description.** In `src/Auth.Server/Api/EndpointMetadata.cs`:

(a) Replace

```csharp
/// <summary>A header an endpoint sets on the response of a status, for the OpenAPI description only.</summary>
```

with

```csharp
/// <summary>A query parameter an endpoint reads itself, for the OpenAPI description only.</summary>
public sealed record QueryParameterMetadata(string Name, string Description);

/// <summary>A header an endpoint sets on the response of a status, for the OpenAPI description only.</summary>
```

(b) Replace

```csharp
    /// <summary>Documents the <c>Set-Cookie</c> of the refresh cookie on the response of <paramref name="status"/>.</summary>
```

with

```csharp
    /// <summary>Documents a query parameter the handler reads itself (it binds none).</summary>
    public static RouteHandlerBuilder ReadsQuery(this RouteHandlerBuilder builder, string name, string description)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithMetadata(new QueryParameterMetadata(name, description));
    }

    /// <summary>Documents a header the handler sets on the response of <paramref name="status"/> (a <c>Location</c>, a cookie).</summary>
    public static RouteHandlerBuilder SetsHeader(this RouteHandlerBuilder builder, int status, string name, string description)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithMetadata(new ResponseHeaderMetadata(status, name, JsonSchemaType.String, description));
    }

    /// <summary>Documents the <c>Set-Cookie</c> of the refresh cookie on the response of <paramref name="status"/>.</summary>
```

In `src/Auth.Server/Api/OpenApiSetup.cs`, replace

```csharp
        // The codes of each error status, from every place that declared the status, and two answers that come from the pipeline and
```

with

```csharp
        // The query parameters the handler reads itself (the start of a provider round trip, spec 0011).
        foreach (var parameter in metadata.OfType<QueryParameterMetadata>())
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = parameter.Name,
                In = ParameterLocation.Query,
                Required = false,
                Description = parameter.Description,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }

        // The codes of each error status, from every place that declared the status, and two answers that come from the pipeline and
```

`src/Auth.Server/Api/ProviderRoutes.cs`:

```csharp
using Auth.Server.Providers;
using Auth.Server.Tenancy;

namespace Auth.Server.Api;

public static class ProviderRoutes
{
    /// <summary>
    /// The endpoints of spec 0011: the round trip with Google or Microsoft and what a product's screens do with its ticket. Mapped here, with what
    /// each takes and answers, so that the OpenAPI description says it all in one place.
    /// </summary>
    public static IEndpointRouteBuilder MapProviderApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(ProviderPaths.Start, ProviderStartEndpoint.Handle)
            .WithTags("Providers")
            .WithDescription(
                "Starts a sign-in with Google or Microsoft: a product's button is a link here (a navigation, not a call). The answer is `302` to the "
                + "provider's authorization endpoint: the authorization code flow with PKCE (`S256`), a `state`, a `nonce`, the scopes `openid email "
                + "profile` and the callback address built from the configured return address, never from the request. No query: a sign-in, a link or a "
                + "sign-up (the exchange decides); `invite=<link token>`: the acceptance of that invitation; `purpose=reauth`: the confirmation of a "
                + "self-deletion, with a fresh sign-in asked for. It reads no account and no invitation. A name that is not `google` or `microsoft`, or a "
                + "provider that is not offered, is `404 not_found`; a malformed `invite`, another `purpose`, both, or one of them given twice is "
                + "`400 invalid_request`. Other parameters are ignored.")
            .ReadsQuery(ProviderStartEndpoint.InviteParameter, "The link token of an invitation (43 base64url characters): the round trip accepts it.")
            .ReadsQuery(ProviderStartEndpoint.PurposeParameter, "`reauth`: the round trip confirms the deletion of the caller's private account.")
            .Produces(StatusCodes.Status302Found)
            .SetsHeader(StatusCodes.Status302Found, "Location", "The provider's authorization endpoint with the request of the round trip.")
            .SetsHeader(
                StatusCodes.Status302Found, "Set-Cookie",
                "`OpenIddict.Client.State.<...>`: binds the state of the round trip to this browser; `HttpOnly`, `Secure`, `SameSite=Lax`, path "
                + "`/auth/providers`, for the 10 minutes of the state.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound);

        return app;
    }
}
```

- [ ] **Step 7: The host.** In `src/Auth.Server/appsettings.json`, replace

```json
      "OpenIddict.Server.OpenIddictServerDispatcher": "Warning"
```

with

```json
      "OpenIddict.Server.OpenIddictServerDispatcher": "Warning",
      "OpenIddict.Client.OpenIddictClientDispatcher": "Warning"
```

(at `Information` the client's dispatcher writes the state and the PKCE verifier, Amendment A1; Task 7 tests it).

In `src/Auth.Server/Program.cs`:

(a) Replace `builder.Services.AddAuthOpenIddict(builder.Configuration, keys, builder.Environment.IsDevelopment());` with
`builder.Services.AddAuthOpenIddict(builder.Configuration, keys, providerSettings, builder.Environment.IsDevelopment());`.

(b) Replace

```csharp
app.UseJsonCharsetGuard();
app.UseAuthentication();
```

with

```csharp
app.UseJsonCharsetGuard();
// Spec 0011, Amendment A2: a provider's callback is read with the scheme and host of the return address, before OpenIddict's client judges it.
app.UseProviderCallbackOrigin(providerSettings);
app.UseAuthentication();
```

(c) Replace `app.MapSignupApi();` with

```csharp
app.MapSignupApi();
app.MapProviderApi();
```

- [ ] **Step 8: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ProviderStartTests -class Auth.IntegrationTests.ProviderPathsTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests -class Auth.IntegrationTests.LoginTests -class Auth.IntegrationTests.RefreshTests -class Auth.IntegrationTests.SignupCompleteSignInTests -class Auth.IntegrationTests.JwksTests -class Auth.IntegrationTests.TokenPruningTests -class Auth.IntegrationTests.BearerValidationTests -class Auth.IntegrationTests.UnhandledTokenRequestGuardTests -class Auth.IntegrationTests.HostHardeningTests`
passes (the client beside the server changes none of the earlier answers, probe Q3).

- [ ] **Step 9: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): OpenIddict's client for Google and Microsoft and the start of a round trip`.

---

### Task 6: The identity rules

**Files:**
- Create: `src/Auth.Server/Providers/ProviderIdentities.cs`
- Create (tests): `tests/Auth.IntegrationTests/ProviderIdentityTests.cs`

**Interfaces:**
- Consumes: `ProviderIdentity` (Task 1), `ProviderNames` (Task 2), `EmailInput.IsWellFormed`, `EmailInput.TryNormalize`, `EmailInput.IsMailbox`, `EmailInput.IsInvitable`, `EmailInput.MaxLength`, `ILookupNormalizer`.
- Produces: `static class ProviderIdentities` with `PersonalTenant`, `MaxKeyLength` (255) and `ProviderIdentity? Read(string provider, ClaimsPrincipal? identityToken, ILookupNormalizer normalizer, bool checkMicrosoftIssuer)` (`null`: the callback answers `provider_error`).

**Contract counts:** none.

**Decisions the plan takes here:**
- A claim counts only when the token holds it once: a claim given twice is treated as missing (`sub`, `tid`, `oid`: `provider_error`; `email`: no
  `email` claim, so no address: two values are not "an `email` claim" the identity can take).
- As the spec says, only an identity with no `email` claim has **no address**. Every `email` claim is kept as the address as the provider gave
  it; one that breaks the invitation rule (empty, a control character, longer than 254 characters, refused by the normaliser, not a mailbox, or
  not invitable: `EmailInput.TryNormalize`, `EmailInput.IsMailbox` and `EmailInput.IsInvitable`, as sign-up applies them) is **unverified**, so
  it never links without the password and never finishes an account (Task 1 makes its column `text` so that any value fits).
- A Google `sub` longer than 255 characters (the column) or holding a control character is `provider_error`; `tid` and `oid` must parse as GUIDs
  and are written in their lower-case form (`D`), so the key is the same whatever case Microsoft sends.
- The Microsoft issuer check (A6) compares exactly with `https://login.microsoftonline.com/{tid}/v2.0`, `tid` in lower case; it runs for the
  web-provider registration only (`checkMicrosoftIssuer`), because under the test issuer OpenIddict checks the issuer itself.

- [ ] **Step 1: Write the failing tests.** `tests/Auth.IntegrationTests/ProviderIdentityTests.cs`:

```csharp
using System.Security.Claims;
using Auth.Server.Providers;
using Microsoft.AspNetCore.Identity;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011, criterion 9: the table of the identity, its address and whether it is verified, row by row.</summary>
public sealed class ProviderIdentityTests
{
    private const string Personal = "9188040d-6c67-4c5b-b112-36a304b66dad";
    private const string Work = "5c1a8d3e-0000-4000-8000-00000000f001";
    private const string ObjectId = "00000000-0000-0000-0000-00000000b001";

    private static readonly UpperInvariantLookupNormalizer Normalizer = new();

    private static (string Type, string Value, string ValueType) Text(string type, string value) => (type, value, ClaimValueTypes.String);

    private static (string Type, string Value, string ValueType) Flag(string type, bool value) =>
        (type, value ? "true" : "false", ClaimValueTypes.Boolean);

    private static (string Type, string Value, string ValueType) Issuer(string tenant) =>
        Text("iss", $"https://login.microsoftonline.com/{tenant}/v2.0");

    private static ClaimsPrincipal Token(params (string Type, string Value, string ValueType)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value, c.ValueType)), "test"));

    private static ProviderIdentity? Google(params (string Type, string Value, string ValueType)[] claims) =>
        ProviderIdentities.Read(ProviderNames.Google, Token(claims), Normalizer, checkMicrosoftIssuer: true);

    private static ProviderIdentity? Microsoft(params (string Type, string Value, string ValueType)[] claims) =>
        ProviderIdentities.Read(ProviderNames.Microsoft, Token(claims), Normalizer, checkMicrosoftIssuer: true);

    private static ProviderIdentity? WorkAccount(string email, (string Type, string Value, string ValueType)? edov = null)
    {
        var claims = new List<(string Type, string Value, string ValueType)> { Issuer(Work), Text("tid", Work), Text("oid", ObjectId), Text("email", email) };
        if (edov is { } given)
        {
            claims.Add(given);
        }

        return Microsoft([.. claims]);
    }

    [Fact]
    public void A_google_identity_is_keyed_by_sub_and_verified_with_email_verified_true()   // criterion 9, Google row
    {
        var identity = Google(Text("sub", "100000000000000000001"), Text("email", "ola@gmail.test"), Flag("email_verified", true));

        Assert.Equal(new ProviderIdentity("google", "100000000000000000001", "ola@gmail.test", true), identity);
    }

    [Theory]
    [InlineData("false", ClaimValueTypes.Boolean)]
    [InlineData("yes", ClaimValueTypes.String)]
    [InlineData("1", ClaimValueTypes.String)]
    public void A_google_address_is_verified_only_with_email_verified_true(string value, string valueType)   // criterion 9
    {
        Assert.False(Google(Text("sub", "1"), Text("email", "ola@gmail.test"), ("email_verified", value, valueType))!.EmailVerified);
        Assert.False(Google(Text("sub", "1"), Text("email", "ola@gmail.test"))!.EmailVerified);
    }

    [Fact]
    public void A_google_identity_without_a_usable_sub_is_refused()   // criterion 8: a missing sub is provider_error
    {
        Assert.Null(Google(Text("email", "ola@gmail.test"), Flag("email_verified", true)));
        Assert.Null(Google(Text("sub", new string('1', 256)), Text("email", "ola@gmail.test")));
        Assert.Null(Google(Text("sub", "1"), Text("sub", "2"), Text("email", "ola@gmail.test")));
        Assert.NotNull(Google(Text("sub", new string('1', 255)), Text("email", "ola@gmail.test")));
    }

    [Fact]
    public void An_identity_without_an_email_claim_has_no_address()   // spec 0011 → the identity: no email is no address
    {
        Assert.Equal(new ProviderIdentity("google", "1", null, false), Google(Text("sub", "1"), Flag("email_verified", true)));
        Assert.Equal(
            new ProviderIdentity("microsoft", $"{Personal}:{ObjectId}", null, false),
            Microsoft(Issuer(Personal), Text("tid", Personal), Text("oid", ObjectId)));
    }

    [Fact]
    public void A_microsoft_identity_is_keyed_by_tenant_and_object_id_in_lower_case_and_never_by_its_address()   // criterion 9, Microsoft row
    {
        var upper = Work.ToUpperInvariant();
        var first = Microsoft(Issuer(Work), Text("tid", upper), Text("oid", ObjectId.ToUpperInvariant()), Text("email", "bo@firma.test"));
        var second = Microsoft(Issuer(Work), Text("tid", Work), Text("oid", ObjectId), Text("email", "bo.new@firma.test"));

        Assert.Equal($"{Work}:{ObjectId}", first!.Key);
        Assert.Equal(first.Key, second!.Key);
        Assert.NotEqual(first.Email, second.Email);
    }

    [Fact]
    public void A_personal_microsoft_address_is_verified()   // criterion 9
    {
        var identity = Microsoft(Issuer(Personal), Text("tid", Personal), Text("oid", ObjectId), Text("email", "ola@outlook.test"));

        Assert.Equal(new ProviderIdentity("microsoft", $"{Personal}:{ObjectId}", "ola@outlook.test", true), identity);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("true", ClaimValueTypes.Boolean, true)]
    [InlineData("true", ClaimValueTypes.String, true)]
    [InlineData("1", ClaimValueTypes.String, true)]
    [InlineData("false", ClaimValueTypes.Boolean, false)]
    [InlineData("yes", ClaimValueTypes.String, false)]
    [InlineData("True", ClaimValueTypes.String, false)]
    [InlineData("0", ClaimValueTypes.String, false)]
    public void A_work_address_is_verified_only_with_xms_edov_true(string? value, string? valueType, bool verified)   // criterion 9, nOAuth
    {
        var identity = WorkAccount("bo@firma.test", value is null ? null : ("xms_edov", value, valueType!));

        Assert.Equal("bo@firma.test", identity!.Email);
        Assert.Equal(verified, identity.EmailVerified);
    }

    [Fact]
    public void Preferred_username_upn_and_unique_name_are_never_read_as_the_address()   // criterion 9
    {
        var identity = Microsoft(
            Issuer(Personal), Text("tid", Personal), Text("oid", ObjectId),
            Text("preferred_username", "ola@outlook.test"), Text("upn", "ola@outlook.test"), Text("unique_name", "ola@outlook.test"));

        Assert.Null(identity!.Email);
        Assert.False(identity.EmailVerified);
    }

    [Theory]
    [InlineData(null, ObjectId)]
    [InlineData(Work, null)]
    [InlineData("not-a-guid", ObjectId)]
    [InlineData(Work, "12345")]
    public void A_microsoft_identity_without_a_tenant_or_an_object_id_is_refused(string? tenant, string? objectId)   // criterion 8: tid/oid missing
    {
        var claims = new List<(string Type, string Value, string ValueType)> { Issuer(tenant ?? Work), Text("email", "bo@firma.test") };
        if (tenant is not null)
        {
            claims.Add(Text("tid", tenant));
        }

        if (objectId is not null)
        {
            claims.Add(Text("oid", objectId));
        }

        Assert.Null(Microsoft([.. claims]));
    }

    [Fact]
    public void A_microsoft_token_whose_issuer_is_not_that_of_its_tenant_is_refused_for_the_web_provider()   // Amendment A6, criterion 9
    {
        (string Type, string Value, string ValueType)[] claims =
            [Issuer("0b0b0b0b-0000-4000-8000-0000000000aa"), Text("tid", Work), Text("oid", ObjectId), Text("email", "bo@firma.test")];

        Assert.Null(ProviderIdentities.Read(ProviderNames.Microsoft, Token(claims), Normalizer, checkMicrosoftIssuer: true));
        Assert.Null(ProviderIdentities.Read(ProviderNames.Microsoft, Token(claims[1..]), Normalizer, checkMicrosoftIssuer: true));
        Assert.NotNull(ProviderIdentities.Read(ProviderNames.Microsoft, Token(claims), Normalizer, checkMicrosoftIssuer: false));   // the test issuer: OpenIddict checks it
    }

    [Theory]
    [InlineData("ola@localhost")]
    [InlineData("ola@10.0.0.1")]
    [InlineData("Ola <ola@gmail.test>")]
    [InlineData("ola@intranet.123")]
    public void An_address_that_breaks_the_invitation_rule_is_kept_and_unverified(string email)   // criterion 9
    {
        var identity = Google(Text("sub", "1"), Text("email", email), Flag("email_verified", true));

        Assert.Equal(email, identity!.Email);
        Assert.False(identity.EmailVerified);
    }

    [Fact]
    public void An_email_claim_that_is_no_mailbox_is_kept_and_unverified()   // spec 0011: only no `email` claim is no address
    {
        var control = "ola" + (char)0x01 + "@gmail.test";
        var tooLong = new string('a', 250) + "@gmail.test";

        foreach (var email in new[] { control, tooLong, "" })
        {
            var identity = Google(Text("sub", "1"), Text("email", email), Flag("email_verified", true))!;
            Assert.Equal(email, identity.Email);
            Assert.False(identity.EmailVerified);
        }
    }

    [Fact]
    public void An_unknown_provider_or_no_token_is_refused()
    {
        Assert.Null(ProviderIdentities.Read("github", Token(Text("sub", "1")), Normalizer, checkMicrosoftIssuer: true));
        Assert.Null(ProviderIdentities.Read(ProviderNames.Google, null, Normalizer, checkMicrosoftIssuer: true));
    }
}
```

- [ ] **Step 2: See them fail.** Build: `ProviderIdentities` does not exist.

- [ ] **Step 3: The rules.** `src/Auth.Server/Providers/ProviderIdentities.cs`:

```csharp
using System.Globalization;
using System.Security.Claims;
using Auth.Server.Requests;
using Microsoft.AspNetCore.Identity;

namespace Auth.Server.Providers;

/// <summary>
/// The identity a callback takes from the claims of the provider's identity token, never from userinfo (spec 0011 → The provider identity and
/// its address; Amendment). Google: the key is <c>sub</c>, the address <c>email</c>, verified when <c>email_verified</c> is <c>true</c>.
/// Microsoft: the key is <c>{tid}:{oid}</c> in lower case, the address <c>email</c> (never <c>preferred_username</c>, <c>upn</c> or
/// <c>unique_name</c>), verified when the tenant is that of personal accounts and the address is there, or <c>xms_edov</c> is <c>true</c>; the
/// web-provider registration also requires the issuer of the token's own tenant (Amendment A6). An address that breaks the invitation rule is
/// unverified. <see langword="null"/> means a claim the identity needs is missing: the callback answers <c>provider_error</c>.
/// </summary>
public static class ProviderIdentities
{
    /// <summary>The tenant of personal Microsoft accounts.</summary>
    public const string PersonalTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

    /// <summary>The longest identity key the table holds; Google's <c>sub</c> is at most 255 ASCII characters.</summary>
    public const int MaxKeyLength = 255;

    public static ProviderIdentity? Read(string provider, ClaimsPrincipal? identityToken, ILookupNormalizer normalizer, bool checkMicrosoftIssuer)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(normalizer);

        if (identityToken is null)
        {
            return null;
        }

        return provider switch
        {
            ProviderNames.Google => ReadGoogle(identityToken, normalizer),
            ProviderNames.Microsoft => ReadMicrosoft(identityToken, normalizer, checkMicrosoftIssuer),
            _ => null,
        };
    }

    private static ProviderIdentity? ReadGoogle(ClaimsPrincipal token, ILookupNormalizer normalizer)
    {
        var subject = Single(token, "sub");
        if (string.IsNullOrEmpty(subject) || subject.Length > MaxKeyLength || subject.Any(char.IsControl))
        {
            return null;
        }

        var verified = string.Equals(Single(token, "email_verified"), "true", StringComparison.Ordinal);
        return WithAddress(ProviderNames.Google, subject, Single(token, "email"), verified, normalizer);
    }

    private static ProviderIdentity? ReadMicrosoft(ClaimsPrincipal token, ILookupNormalizer normalizer, bool checkIssuer)
    {
        if (!Guid.TryParse(Single(token, "tid"), out var tenantId) || !Guid.TryParse(Single(token, "oid"), out var objectId))
        {
            return null;
        }

        var tenant = tenantId.ToString("D");
        if (checkIssuer && !string.Equals(
                Single(token, "iss"),
                string.Create(CultureInfo.InvariantCulture, $"https://login.microsoftonline.com/{tenant}/v2.0"),
                StringComparison.Ordinal))
        {
            return null;   // OpenIddict does not check the issuer of the `common` tenant (Amendment A6)
        }

        var email = Single(token, "email");
        var edov = Single(token, "xms_edov");
        var verified = (string.Equals(tenant, PersonalTenant, StringComparison.Ordinal) && !string.IsNullOrEmpty(email))
            || string.Equals(edov, "true", StringComparison.Ordinal)
            || string.Equals(edov, "1", StringComparison.Ordinal);
        return WithAddress(ProviderNames.Microsoft, tenant + ":" + objectId.ToString("D"), email, verified, normalizer);
    }

    /// <summary>
    /// The address as the provider gave it (none only without an <c>email</c> claim), verified only when the provider verified it and it passes the
    /// invitation rule as sign-up applies it; an address that breaks the rule is kept and unverified (spec 0011 → the identity).
    /// </summary>
    private static ProviderIdentity WithAddress(string provider, string key, string? email, bool verified, ILookupNormalizer normalizer)
    {
        if (email is null)
        {
            return new ProviderIdentity(provider, key, null, false);
        }

        var invitable = email.Length is > 0 and <= EmailInput.MaxLength
            && EmailInput.TryNormalize(email, normalizer, out _)
            && EmailInput.IsMailbox(email)
            && EmailInput.IsInvitable(email);
        return new ProviderIdentity(provider, key, email, verified && invitable);
    }

    /// <summary>The value of a claim the token holds exactly once; <see langword="null"/> when it holds none or several.</summary>
    private static string? Single(ClaimsPrincipal token, string type)
    {
        string? found = null;
        foreach (var claim in token.FindAll(type))
        {
            if (found is not null)
            {
                return null;
            }

            found = claim.Value;
        }

        return found;
    }
}
```

- [ ] **Step 4: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ProviderIdentityTests -class Auth.IntegrationTests.EmailInputTests`
passes.

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): the identity rules of Google and Microsoft`.

---

### Task 7: `GET /auth/providers/{name}/callback`: tickets, `auth_pt`, the four error codes

**Files:**
- Create: `src/Auth.Server/Providers/ProviderBindingCookie.cs`, `src/Auth.Server/Providers/ProviderCallbackEndpoint.cs`
- Modify: `src/Auth.Server/Api/ProviderRoutes.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/ProviderTestBase.cs`, `tests/Auth.IntegrationTests/Infrastructure/ProviderApi.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`, `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/ProviderCallbackTests.cs`, `tests/Auth.IntegrationTests/ProviderCallbackErrorTests.cs`, `tests/Auth.IntegrationTests/ProviderTestIssuerTests.cs`

**Interfaces:**
- Consumes: `ProviderIdentities.Read` (Task 6), `ProviderTickets.IssueAsync`, `ProviderTicketDraft` (Task 1), `ProviderCatalog.IsOffered`, `ProviderSettings` (`ReturnUrl`, `TestIssuer`), `ProviderPurposes`, `ProviderStateItems`, `ProviderPaths` (Task 5), `FakeSignIn`, `FakePeople`, `FakeDefects`, `FakeOidcServer` (Task 4).
- Produces: `ProviderBindingCookie` (`Name = "auth_pt"`, `Path = "/auth"`, `Lifetime` 10 min, `Append(HttpResponse, string)`, `Clear(HttpResponse)`, `bool TryRead(HttpRequest, out string?)`); `ProviderCallbackErrors` (`Cancelled`, `InvalidState`, `ProviderUnavailable`, `ProviderError`); `ProviderCallbackEndpoint.HandleAsync`; test side `ProviderTestBase.GooglePerson(...)`, `PersonalMicrosoft(...)`, `WorkMicrosoft(...)`, `AuthorizeAsync(ProviderApi.Start, FakeSignIn)`, `CallbackAsync(string pathAndQuery, string? cookie)`, `TicketAsync(string provider, FakeSignIn person, string query = "")`, and `ProviderApi.Ticket(string Value, string Purpose, string Binding)`, `ProviderApi.CallbackAsync`, `ProviderApi.FragmentOf`, `ProviderApi.BindingCookieOf`, `ProviderApi.TicketOf`, `ProviderApi.BindingCookie`.

**Contract counts:** `OpenApiTests`: `Endpoints` gains `GET /auth/providers/{name}/callback`, `Each_endpoint_describes_the_statuses_of_the_contract` gains it with `302,404,429`, `No_other_answer_says_it_sets_a_cookie` gains `GET /auth/providers/{name}/callback 302`. `OpenApiHardeningTests`: **32 → 33** operations.

**Decisions the plan takes here:**
- A callback for a name that is not a provider, or on an instance without the return address (no round trip can have started), is `404
  not_found`: there is no screen to send the browser to. The OpenAPI description says so.
- The ticket is issued on its own save, outside any transaction (nothing else changes at the callback); a failed save is a `500` like any failure
  of the database, and no `auth_pt` is set then.
- The code is judged before the purpose and the manifest (the order of the probe's mapping): an outcome OpenIddict refused is reported as such even
  when the provider was dropped meanwhile; a valid outcome for a provider no longer offered is `provider_unavailable` (probe: the code is redeemed
  by then, which is harmless).
- One line at `Information` for every answer: the provider and the code, or the provider and the purpose of the ticket made. A refusal also
  names the OAuth error code the client recorded (`invalid_client`, `invalid_grant`, `access_denied`, ...; a short lower-case code only, never
  its description or a value of the request), so that after a wrong or lapsed client secret the log names `invalid_client`, as the runbook of
  the spec says (`A_refused_client_secret_is_provider_error_and_the_log_names_invalid_client`, with the fake's defect `refuse_client` of Task 4).

- [ ] **Step 1: Write the failing tests.**

In `tests/Auth.IntegrationTests/Infrastructure/ProviderApi.cs`:

(a) Replace

```csharp
    /// <summary>The start of OpenIddict's request-forgery cookie: its name ends with a value of each round trip.</summary>
    public const string StateCookiePrefix = "OpenIddict.Client.State.";
```

with

```csharp
    /// <summary>The start of OpenIddict's request-forgery cookie: its name ends with a value of each round trip.</summary>
    public const string StateCookiePrefix = "OpenIddict.Client.State.";

    /// <summary>The cookie that binds a ticket to the browser of its round trip.</summary>
    public const string BindingCookie = "auth_pt";

    /// <summary>The return screen of the test hosts: every callback answers a redirect to it.</summary>
    public const string ReturnUrl = AuthAppFactory.DefaultProviderReturnUrl;

    /// <summary>A ticket of a callback: the value of the fragment, its purpose, and the value of the <c>auth_pt</c> cookie set with it.</summary>
    public sealed record Ticket(string Value, string Purpose, string Binding);

    /// <summary>Calls the callback as the provider's redirect makes a browser do, with the cookie line given (the request-forgery cookie).</summary>
    public static async Task<HttpResponseMessage> CallbackAsync(
        HttpClient browser, string pathAndQuery, string? cookie, Action<HttpRequestMessage>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(browser);

        using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        configure?.Invoke(request);
        return await browser.SendAsync(request);
    }

    /// <summary>The members of the fragment of a callback's redirect to the return screen; asserts the <c>302</c> to that screen.</summary>
    public static IReadOnlyDictionary<string, string> FragmentOf(HttpResponseMessage callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        var location = callback.Headers.Location?.OriginalString ?? throw new InvalidOperationException("The callback has no Location.");
        Assert.StartsWith(ReturnUrl + "#", location, StringComparison.Ordinal);
        return QueryHelpers.ParseQuery(location[(ReturnUrl.Length + 1)..]).ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
    }

    /// <summary>The <c>auth_pt</c> cookie a response sets (or clears); <see langword="null"/> when it sets none.</summary>
    public static SetCookieHeaderValue? BindingCookieOf(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Headers.TryGetValues("Set-Cookie", out var values)
            ? SetCookieHeaderValue.ParseList([.. values]).SingleOrDefault(c => c.Name == BindingCookie)
            : null;
    }

    /// <summary>The ticket and its binding of a successful callback.</summary>
    public static Ticket TicketOf(HttpResponseMessage callback)
    {
        var fragment = FragmentOf(callback);
        Assert.True(fragment.TryGetValue("ticket", out var ticket), "The callback answered: " + string.Join('&', fragment.Select(p => p.Key + "=" + p.Value)));
        var cookie = BindingCookieOf(callback) ?? throw new InvalidOperationException("The callback set no auth_pt.");
        return new Ticket(ticket, fragment["purpose"], cookie.Value.Value!);
    }
```

In `tests/Auth.IntegrationTests/Infrastructure/ProviderTestBase.cs`:

(a) Replace `using Auth.Server.Tenancy;` with

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;
```

(b) Replace

```csharp
    protected Task<ProviderApi.Start> StartAsync(string provider, string query = "") => ProviderApi.StartAsync(Browser, provider, query);
```

with

```csharp
    protected Task<ProviderApi.Start> StartAsync(string provider, string query = "") => ProviderApi.StartAsync(Browser, provider, query);

    /// <summary>The Google person of the fake (verified by default), or another address, subject or defect.</summary>
    protected static FakeSignIn GooglePerson(string? email = null, bool? verified = null, string? subject = null, string? defect = null) =>
        new(FakePeople.Google, email, verified, subject, Defect: defect);

    /// <summary>The personal Microsoft account of the fake (verified when it has an address).</summary>
    protected static FakeSignIn PersonalMicrosoft(string? email = null, string? subject = null, string? defect = null) =>
        new(FakePeople.MicrosoftPersonal, email, Subject: subject, Defect: defect);

    /// <summary>The Microsoft work account of the fake: unverified unless <paramref name="edov"/> says so.</summary>
    protected static FakeSignIn WorkMicrosoft(string? email = null, string? edov = null, string? subject = null, string? defect = null) =>
        new(FakePeople.MicrosoftWork, email, Subject: subject, Edov: edov, Defect: defect);

    /// <summary>The fake's answer to the start's redirect for this person: the path and query of the callback, with the code and the state.</summary>
    protected async Task<string> AuthorizeAsync(ProviderApi.Start start, FakeSignIn person)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(person);

        using var answer = await Fake.Client.GetAsync(person.AppendTo(start.Location.AbsoluteUri));
        Assert.Equal(HttpStatusCode.Found, answer.StatusCode);
        var back = answer.Headers.Location ?? throw new InvalidOperationException("The fake did not redirect back.");
        Assert.Equal(start.Query["redirect_uri"], back.GetLeftPart(UriPartial.Path));
        return back.PathAndQuery;
    }

    protected Task<HttpResponseMessage> CallbackAsync(string pathAndQuery, string? cookie) => ProviderApi.CallbackAsync(Browser, pathAndQuery, cookie);

    /// <summary>The tickets in the table.</summary>
    protected Task<int> TicketCountAsync() => InDbAsync(db => db.ProviderTickets.CountAsync(TestContext.Current.CancellationToken));

    /// <summary>A whole round trip in one browser: the start, the provider, the callback; the ticket and its binding.</summary>
    protected async Task<ProviderApi.Ticket> TicketAsync(string provider, FakeSignIn person, string query = "")
    {
        var start = await StartAsync(provider, query);
        using var callback = await CallbackAsync(await AuthorizeAsync(start, person), start.StateCookie);
        return ProviderApi.TicketOf(callback);
    }
```

`tests/Auth.IntegrationTests/ProviderCallbackTests.cs`:

```csharp
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>GET /auth/providers/{name}/callback</c>: success.</summary>
public sealed class ProviderCallbackTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Invite43 = "BBBBBBBBBB" + "BBBBBBBBBB" + "BBBBBBBBBB" + "BBBBBBBBBB" + "BBB";

    private static byte[] Sha256(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private Task<List<ProviderTicket>> TicketRowsAsync() =>
        InDbAsync(db => db.ProviderTickets.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    public static TheoryData<string, string, string, string, bool> People => new()
    {
        { "google", FakePeople.Google, FakePeople.GoogleSubject, FakePeople.GoogleEmail, true },
        { "microsoft", FakePeople.MicrosoftPersonal, FakePeople.PersonalTenant + ":" + FakePeople.PersonalObjectId, FakePeople.PersonalEmail, true },
        { "microsoft", FakePeople.MicrosoftWork, FakePeople.WorkTenant + ":" + FakePeople.WorkObjectId, FakePeople.WorkEmail, false },
    };

    [Theory]
    [MemberData(nameof(People))]
    public async Task A_good_code_answers_302_with_a_ticket_sets_auth_pt_and_stores_hashes_only(
        string provider, string person, string key, string email, bool verified)   // criterion 7
    {
        var start = await StartAsync(provider);

        using var callback = await CallbackAsync(await AuthorizeAsync(start, new FakeSignIn(person)), start.StateCookie);

        var fragment = ProviderApi.FragmentOf(callback);
        Assert.Equal(["purpose", "ticket"], fragment.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("sign_in", fragment["purpose"]);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", fragment["ticket"]);
        Assert.True(callback.Headers.CacheControl?.NoStore);
        Assert.Equal("", await callback.Content.ReadAsStringAsync());
        var cookie = ProviderApi.BindingCookieOf(callback)!;
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Strict, cookie.SameSite);
        Assert.Equal("/auth", cookie.Path.Value);
        Assert.Equal(TimeSpan.FromSeconds(600), cookie.MaxAge);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", cookie.Value.Value!);

        var row = Assert.Single(await TicketRowsAsync());
        Assert.Equal(Sha256(fragment["ticket"]), row.TicketHash);
        Assert.Equal(Sha256(cookie.Value.Value!), row.BindingHash);
        Assert.Equal(ProviderTicketPurpose.SignIn, row.Purpose);
        Assert.Equal(provider, row.Provider);
        Assert.Equal(key, row.IdentityKey);
        Assert.Equal(email, row.Email);
        Assert.Equal(verified, row.EmailVerified);
        Assert.Null(row.InviteTokenHash);
        Assert.Equal(StorableTime.Now(Clock) + TimeSpan.FromMinutes(10), row.ExpiresAt);
        // No column holds the ticket or the cookie in clear.
        var text = row.Provider + row.IdentityKey + row.Email;
        Assert.DoesNotContain(fragment["ticket"], text, StringComparison.Ordinal);
        Assert.DoesNotContain(cookie.Value.Value!, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invitation_and_a_reauth_come_back_with_their_purpose_and_their_ticket_with_its_lifetime()   // criterion 7
    {
        var invite = await TicketAsync("google", GooglePerson(), "?invite=" + Invite43);
        var reauth = await TicketAsync("microsoft", PersonalMicrosoft(), "?purpose=reauth");

        Assert.Equal("invite", invite.Purpose);
        Assert.Equal("reauth", reauth.Purpose);
        var rows = await TicketRowsAsync();
        Assert.Equal(Sha256(Invite43), rows.Single(r => r.Purpose == ProviderTicketPurpose.Invite).InviteTokenHash);
        Assert.Equal(StorableTime.Now(Clock) + TimeSpan.FromMinutes(5), rows.Single(r => r.Purpose == ProviderTicketPurpose.Reauth).ExpiresAt);
    }

    [Fact]
    public async Task No_state_value_and_no_code_verifier_reaches_the_log()   // criterion 7, Amendment A1
    {
        var start = await StartAsync("google");
        var back = await AuthorizeAsync(start, GooglePerson());

        using var callback = await CallbackAsync(back, start.StateCookie);

        var ticket = ProviderApi.TicketOf(callback);
        var code = QueryHelpers.ParseQuery(new Uri("https://app.example.com" + back).Query)["code"].ToString();
        var text = Logs.Text;
        Assert.DoesNotContain(start.Query["state"], text, StringComparison.Ordinal);
        Assert.DoesNotContain(code, text, StringComparison.Ordinal);
        Assert.DoesNotContain("code_verifier", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket.Value, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket.Binding, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Assert.Single(Fake.Server.IssuedIdentityTokens), text, StringComparison.Ordinal);
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("google", StringComparison.Ordinal) && e.Message.Contains("sign_in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_callback_that_reaches_the_service_under_another_host_and_scheme_still_makes_a_ticket()   // Review Focus 1, Amendment A2
    {
        var start = await StartAsync("google");

        using var callback = await ProviderApi.CallbackAsync(Browser, await AuthorizeAsync(start, GooglePerson()), start.StateCookie, request =>
        {
            request.Headers.Host = "auth:8080";
            request.Headers.Add("X-Forwarded-Host", "evil.test");
            request.Headers.Add("X-Forwarded-Proto", "http");
        });

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);
    }

    [Fact]
    public async Task The_state_still_works_after_nine_minutes()   // spec 0011 → start: the state lives 10 minutes
    {
        var start = await StartAsync("microsoft");
        Clock.Advance(TimeSpan.FromMinutes(9));

        using var callback = await CallbackAsync(await AuthorizeAsync(start, PersonalMicrosoft()), start.StateCookie);

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);
    }

    [Fact]
    public async Task A_second_round_trip_replaces_the_cookie_of_the_browser()   // Settled 9
    {
        var first = await TicketAsync("google", GooglePerson());
        var second = await TicketAsync("google", GooglePerson());

        Assert.NotEqual(first.Value, second.Value);
        Assert.NotEqual(first.Binding, second.Binding);
        Assert.Equal(2, (await TicketRowsAsync()).Count);
    }
}
```

`tests/Auth.IntegrationTests/ProviderCallbackErrorTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011, criterion 8: every failure of the callback, one test per code; none stores a ticket or sets <c>auth_pt</c>.</summary>
public sealed class ProviderCallbackErrorTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    /// <summary>Runs the callback and asserts the refusal of the contract: the redirect with the code (and the purpose), nothing stored, no cookie.</summary>
    private async Task AssertRefusedAsync(string provider, Func<Task<HttpResponseMessage>> callback, string code, string? purpose)
    {
        var before = await TicketCountAsync();

        using var response = await callback();

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.Equal(ProviderApi.ReturnUrl + "#error=" + code + (purpose is null ? "" : "&purpose=" + purpose), location);
        Assert.DoesNotContain(FakeOidcServer.RefusalDescription, location, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeOidcServer.RefusalDescription, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(ProviderApi.BindingCookieOf(response));
        Assert.Equal(before, await TicketCountAsync());
        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains(code, StringComparison.Ordinal) && e.Message.Contains(provider, StringComparison.Ordinal));
    }

    private async Task<Func<Task<HttpResponseMessage>>> RoundTripAsync(string provider, FakeSignIn person, bool withCookie = true)
    {
        var start = await StartAsync(provider);
        var back = await AuthorizeAsync(start, person);
        return () => CallbackAsync(back, withCookie ? start.StateCookie : null);
    }

    [Fact]
    public async Task Access_denied_with_a_valid_state_and_its_cookie_is_cancelled()   // criterion 8: cancelled
    {
        await AssertRefusedAsync("google", await RoundTripAsync("google", new FakeSignIn(FakePeople.Cancel)), "cancelled", "sign_in");
    }

    [Fact]
    public async Task Access_denied_without_the_request_forgery_cookie_is_invalid_state()   // criterion 8, Amendment A3
    {
        await AssertRefusedAsync("google", await RoundTripAsync("google", new FakeSignIn(FakePeople.Cancel), withCookie: false), "invalid_state", "sign_in");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("forged")]
    [InlineData("expired")]
    [InlineData("replayed")]
    public async Task A_missing_forged_expired_or_replayed_state_is_invalid_state_without_a_purpose(string how)   // criterion 8: invalid_state
    {
        var start = await StartAsync("google");
        var back = await AuthorizeAsync(start, GooglePerson());
        var path = back;
        switch (how)
        {
            case "missing":
                path = "/auth/providers/google/callback?code=abc&iss=" + Uri.EscapeDataString(FakeOidcServer.GoogleIssuer);
                break;
            case "forged":
                path = back.Replace(start.Query["state"], "ZZZZZZZZZZ" + "ZZZZZZZZZZ" + "ZZZZZZZZZZ" + "ZZZZZZZZZZ" + "ZZZ", StringComparison.Ordinal);
                break;
            case "expired":
                Clock.Advance(TimeSpan.FromMinutes(11));
                break;
            default:
                using (var first = await CallbackAsync(back, start.StateCookie))
                {
                    ProviderApi.TicketOf(first);
                }

                break;
        }

        await AssertRefusedAsync("google", () => CallbackAsync(path, start.StateCookie), "invalid_state", null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_valid_state_without_its_cookie_or_with_another_browsers_is_invalid_state_with_its_purpose(bool another)   // criterion 8
    {
        var start = await StartAsync("microsoft", "?purpose=reauth");
        var back = await AuthorizeAsync(start, PersonalMicrosoft());
        var other = await StartAsync("microsoft");

        await AssertRefusedAsync("microsoft", () => CallbackAsync(back, another ? other.StateCookie : null), "invalid_state", "reauth");
    }

    [Theory]
    [InlineData("google", FakePeople.Google, FakeDefects.RefuseCode)]
    [InlineData("google", FakePeople.Google, FakeDefects.BadSignature)]
    [InlineData("google", FakePeople.Google, FakeDefects.WrongAudience)]
    [InlineData("google", FakePeople.Google, FakeDefects.WrongNonce)]
    [InlineData("google", FakePeople.Google, FakeDefects.WrongIssuer)]
    [InlineData("google", FakePeople.Google, FakeDefects.NoSubject)]
    [InlineData("google", FakePeople.Google, FakeDefects.ProviderError)]
    [InlineData("microsoft", FakePeople.MicrosoftPersonal, FakeDefects.BadSignature)]
    [InlineData("microsoft", FakePeople.MicrosoftWork, FakeDefects.WrongAudience)]
    [InlineData("microsoft", FakePeople.MicrosoftWork, FakeDefects.WrongIssuer)]
    [InlineData("microsoft", FakePeople.MicrosoftWork, FakeDefects.NoTenant)]
    [InlineData("microsoft", FakePeople.MicrosoftWork, FakeDefects.NoObjectId)]
    public async Task A_refused_code_an_identity_token_that_is_not_valid_or_a_missing_claim_is_provider_error(
        string provider, string person, string defect)   // criterion 8: provider_error; Amendment A6 for the Microsoft issuer
    {
        await AssertRefusedAsync(provider, await RoundTripAsync(provider, new FakeSignIn(person, Defect: defect)), "provider_error", "sign_in");
    }

    [Fact]
    public async Task A_refused_client_secret_is_provider_error_and_the_log_names_invalid_client()   // spec 0011 → the runbook: invalid_client in the log
    {
        await AssertRefusedAsync(
            "microsoft", await RoundTripAsync("microsoft", new FakeSignIn(FakePeople.MicrosoftPersonal, Defect: FakeDefects.RefuseClient)), "provider_error", "sign_in");

        Assert.Contains(Logs.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("provider_error", StringComparison.Ordinal) && e.Message.Contains("invalid_client", StringComparison.Ordinal));
        Assert.DoesNotContain(Logs.Entries, e => e.Message.Contains(FakeOidcServer.RefusalDescription, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_provider_dropped_from_the_manifest_during_the_round_trip_is_provider_unavailable()   // criterion 8: provider_unavailable
    {
        var callback = await RoundTripAsync("google", GooglePerson());
        SetManifest(AuthAppFactory.SelfServiceManifest + AuthAppFactory.PrivateBlock + "\nproviders: [microsoft]\n", withProviders: false);

        await AssertRefusedAsync("google", callback, "provider_unavailable", "sign_in");
    }

    [Fact]
    public async Task A_name_that_is_no_provider_is_404_and_a_post_is_405()
    {
        using var unknown = await Browser.GetAsync("/auth/providers/github/callback?code=x&state=y");
        using var posted = await Browser.PostAsync("/auth/providers/google/callback", new FormUrlEncodedContent([new("code", "x"), new("state", "y")]));

        await TenancyApi.AssertErrorAsync(unknown, HttpStatusCode.NotFound, "not_found");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, posted.StatusCode);
    }
}
```

`tests/Auth.IntegrationTests/ProviderTestIssuerTests.cs`:

```csharp
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Providers;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Configuration, the test issuer (criterion 34): both providers as plain OpenID Connect clients of the fake, same code.</summary>
public sealed class ProviderTestIssuerTests : ProviderTestBase
{
    public ProviderTestIssuerTests(PostgresFixture postgres, KeyMaterialFixture keys)
        : base(postgres, keys)
    {
        Factory.WithSetting(ProviderSettings.TestIssuerKey, "http://fake-oidc.test");
    }

    [Theory]
    [InlineData("google", FakePeople.Google, FakePeople.GoogleSubject)]
    [InlineData("microsoft", FakePeople.MicrosoftPersonal, FakePeople.PersonalTenant + ":" + FakePeople.PersonalObjectId)]
    public async Task Both_providers_run_through_the_plain_registration_of_the_test_issuer(string provider, string person, string key)
    {
        var start = await StartAsync(provider);
        Assert.Equal($"http://fake-oidc.test/{provider}/authorize", start.Location.GetLeftPart(UriPartial.Path));

        using var callback = await CallbackAsync(await AuthorizeAsync(start, new FakeSignIn(person)), start.StateCookie);

        Assert.Equal("sign_in", ProviderApi.TicketOf(callback).Purpose);
        var row = await InDbAsync(db => db.ProviderTickets.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(provider, row.Provider);
        Assert.Equal(key, row.IdentityKey);
        Assert.True(row.EmailVerified);
    }

    [Fact]
    public async Task Under_the_test_issuer_openiddict_checks_the_issuer_itself()   // Amendment A6
    {
        var start = await StartAsync("microsoft");

        using var callback = await CallbackAsync(
            await AuthorizeAsync(start, new FakeSignIn(FakePeople.MicrosoftPersonal, Defect: FakeDefects.WrongIssuer)), start.StateCookie);

        Assert.Equal("provider_error", ProviderApi.FragmentOf(callback)["error"]);
        Assert.Equal(0, await InDbAsync(db => db.ProviderTickets.CountAsync(TestContext.Current.CancellationToken)));
    }
}
```

Change the contract tests:

(a) `tests/Auth.IntegrationTests/OpenApiTests.cs`: in `Endpoints`, replace `        "GET /auth/providers/{name}/start",` with
`        "GET /auth/providers/{name}/start", "GET /auth/providers/{name}/callback",`; after
`    [InlineData("GET /auth/providers/{name}/start", "302,400,404,429")]` add `    [InlineData("GET /auth/providers/{name}/callback", "302,404,429")]`;
in the `setting` array of `No_other_answer_says_it_sets_a_cookie`, after `"GET /auth/providers/{name}/start 302"` add
`, "GET /auth/providers/{name}/callback 302"`.

(b) `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`: replace `        Assert.Equal(32, operations.Count);   // spec 0011: the start of a provider round trip`
with `        Assert.Equal(33, operations.Count);   // spec 0011: the start and the callback of a provider round trip`.

- [ ] **Step 2: See them fail.** Build, then run `ProviderCallbackTests`: the callback route does not exist; OpenIddict's pass-through gives the
request to no endpoint (`404`).

- [ ] **Step 3: The cookie.** `src/Auth.Server/Providers/ProviderBindingCookie.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;

namespace Auth.Server.Providers;

/// <summary>
/// The cookie <c>auth_pt</c> of spec 0011 → The browser binding: 32 random bytes that the callback sets with a ticket, whose SHA-256 the ticket
/// stores. Every request that takes a ticket needs it, so a ticket pushed into another browser is worth nothing there. The only place that knows
/// its name and attributes.
/// </summary>
public static class ProviderBindingCookie
{
    public const string Name = "auth_pt";
    public const string Path = "/auth";

    /// <summary>Longest value treated as a binding; a binding is 43 characters.</summary>
    public const int MaxValueLength = 256;

    /// <summary><c>Max-Age=600</c>: the 10 minutes of a ticket (a re-authentication's 5 are judged on the ticket).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public static void Append(HttpResponse response, string value)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Append(Name, value, Options(Lifetime));
    }

    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Cookies.Append(Name, string.Empty, Options(TimeSpan.Zero));
    }

    /// <summary>Reads the cookie; <see langword="false"/> when it is missing, empty, too long or holds a control character.</summary>
    public static bool TryRead(HttpRequest request, [NotNullWhen(true)] out string? value)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Cookies.TryGetValue(Name, out var found) && found.Length is > 0 and <= MaxValueLength && !found.Any(char.IsControl))
        {
            value = found;
            return true;
        }

        value = null;
        return false;
    }

    // Secure is unconditional: TLS ends at the reverse proxy, so the request scheme seen here says nothing.
    private static CookieOptions Options(TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        MaxAge = lifetime,
        IsEssential = true,
    };
}
```

- [ ] **Step 4: The callback.** `src/Auth.Server/Providers/ProviderCallbackEndpoint.cs`:

```csharp
using System.Security.Claims;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Email;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Client.AspNetCore;
using ClientProperties = OpenIddict.Client.AspNetCore.OpenIddictClientAspNetCoreConstants.Properties;

namespace Auth.Server.Providers;

/// <summary>The codes of a failed callback, in the fragment (spec 0011 → the callback's table).</summary>
public static class ProviderCallbackErrors
{
    public const string Cancelled = "cancelled";
    public const string InvalidState = "invalid_state";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string ProviderError = "provider_error";
}

/// <summary>
/// <c>GET /auth/providers/{name}/callback</c> (spec 0011). By the time it runs, OpenIddict's client has judged the request in the authentication
/// step (probe T3): the state (its signature, its age, its single use, the request-forgery cookie of the browser that started), the code (redeemed
/// with the PKCE verifier), the identity token (signature, issuer, audience, nonce, expiry). This reads the outcome and always answers a redirect to
/// the product's return screen, never a page of its own and never the provider's <c>error_description</c>: a ticket and <c>auth_pt</c> on success,
/// one of four codes otherwise, and nothing stored then. Neither the outcome's items (they hold the raw tokens, T11) nor the query is logged.
/// </summary>
public static partial class ProviderCallbackEndpoint
{
    private const string LogCategory = "Auth.Server.Providers.ProviderCallback";

    public static async Task<IResult> HandleAsync(
        HttpContext http, string name, ProviderCatalog catalog, ProviderSettings settings, AuthDbContext db, ILookupNormalizer normalizer,
        TimeProvider clock, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggers);

        if (!ProviderNames.IsName(name) || settings.ReturnUrl is not { } returnUrl)
        {
            return ApiResults.NotFound();   // no provider of that name, or no screen to send anyone to
        }

        var logger = loggers.CreateLogger(LogCategory);
        var result = await http.AuthenticateAsync(OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
        var items = result.Properties?.Items;
        var purposeName = items is not null && items.TryGetValue(ProviderStateItems.Purpose, out var stated) ? stated : null;

        // OpenIddict presets a 400 on a failure (T10): the answer is always this redirect, whatever it set.
        if (!result.Succeeded)
        {
            return Refuse(logger, returnUrl, name, CodeOf(items, purposeName), purposeName, ErrorOf(items));
        }

        if (!ProviderPurposes.TryParse(purposeName, out var purpose)
            || !items!.TryGetValue(ProviderStateItems.Provider, out var stateProvider)
            || !string.Equals(stateProvider, name, StringComparison.Ordinal))
        {
            return Refuse(logger, returnUrl, name, ProviderCallbackErrors.ProviderError, purposeName);   // never: the state is ours, and OpenIddict matched its path
        }

        if (!catalog.IsOffered(name))
        {
            return Refuse(logger, returnUrl, name, ProviderCallbackErrors.ProviderUnavailable, purposeName);   // dropped from the manifest meanwhile
        }

        var identityToken = result.Properties!.GetParameter<ClaimsPrincipal>(ClientProperties.BackchannelIdentityTokenPrincipal);
        if (ProviderIdentities.Read(name, identityToken, normalizer, checkMicrosoftIssuer: settings.TestIssuer is null) is not { } identity)
        {
            return Refuse(logger, returnUrl, name, ProviderCallbackErrors.ProviderError, purposeName);   // a claim the identity needs is missing
        }

        byte[]? inviteHash = null;
        if (purpose == ProviderTicketPurpose.Invite && !TryInviteHash(items!, out inviteHash))   // a successful outcome always has its properties
        {
            return Refuse(logger, returnUrl, name, ProviderCallbackErrors.ProviderError, purposeName);
        }

        var issued = await ProviderTickets.IssueAsync(
            db, new ProviderTicketDraft(purpose, identity, inviteHash), StorableTime.Now(clock), CancellationToken.None);
        ProviderBindingCookie.Append(http.Response, issued.Binding);
        LogTicketMade(logger, name, ProviderPurposes.NameOf(purpose));
        return Results.Redirect(WithFragment(returnUrl, $"ticket={issued.Ticket}&purpose={ProviderPurposes.NameOf(purpose)}"));
    }

    /// <summary>
    /// The code of a failed outcome (probe Q1): <c>invalid_state</c> when the state could not be read, or the request-forgery cookie is missing or
    /// another browser's (ID2129, ID2163: the cookie is judged before the provider's answer, Amendment A3); <c>cancelled</c> for
    /// <c>access_denied</c>; <c>provider_error</c> for everything else. Never by the error alone: <c>invalid_request</c> and <c>invalid_token</c>
    /// each cover both families.
    /// </summary>
    private static string CodeOf(IDictionary<string, string?>? items, string? purposeName)
    {
        if (items is null || purposeName is null)
        {
            return ProviderCallbackErrors.InvalidState;
        }

        var uri = items.TryGetValue(ClientProperties.ErrorUri, out var found) ? found : null;
        if (uri is not null && (uri.EndsWith("/ID2129", StringComparison.Ordinal) || uri.EndsWith("/ID2163", StringComparison.Ordinal)))
        {
            return ProviderCallbackErrors.InvalidState;
        }

        return items.TryGetValue(ClientProperties.Error, out var error) && string.Equals(error, "access_denied", StringComparison.Ordinal)
            ? ProviderCallbackErrors.Cancelled
            : ProviderCallbackErrors.ProviderError;
    }

    /// <summary>
    /// The OAuth error the client recorded for a failed outcome (<c>access_denied</c>, <c>invalid_grant</c>, <c>invalid_client</c>, ...): a code of
    /// the protocol, so that the log line of a lapsed secret names <c>invalid_client</c> (spec 0011 → the runbook). Never its description, never
    /// the request; anything that is not a short lower-case code (the error of an authorization answer is the caller's text) is "none".
    /// </summary>
    private static string ErrorOf(IDictionary<string, string?>? items) =>
        items is not null && items.TryGetValue(ClientProperties.Error, out var error)
            && error is { Length: > 0 and <= 64 } && error.All(c => c is (>= 'a' and <= 'z') or '_')
            ? error
            : "none";

    private static IResult Refuse(ILogger logger, Uri returnUrl, string provider, string code, string? purposeName, string error = "none")
    {
        LogRefused(logger, provider, code, error);
        var fragment = ProviderPurposes.TryParse(purposeName, out var purpose)
            ? $"error={code}&purpose={ProviderPurposes.NameOf(purpose)}"
            : "error=" + code;
        return Results.Redirect(WithFragment(returnUrl, fragment));
    }

    private static bool TryInviteHash(IDictionary<string, string?> items, out byte[]? hash)
    {
        hash = null;
        if (!items.TryGetValue(ProviderStateItems.InviteHash, out var hex) || hex is not { Length: 64 })
        {
            return false;
        }

        try
        {
            hash = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The return address with the fragment: a browser never sends a fragment to a server (spec 0011 → the callback).</summary>
    private static string WithFragment(Uri returnUrl, string fragment) => returnUrl.AbsoluteUri + "#" + fragment;

    [LoggerMessage(Level = LogLevel.Information, Message = "The {Provider} callback ended in {Code} (the client recorded the OAuth error {Error}).")]
    private static partial void LogRefused(ILogger logger, string provider, string code, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "The {Provider} callback made a ticket for {Purpose}.")]
    private static partial void LogTicketMade(ILogger logger, string provider, string purpose);
}
```

- [ ] **Step 5: Map it.** In `src/Auth.Server/Api/ProviderRoutes.cs`, replace

```csharp
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound);

        return app;
```

with

```csharp
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound);

        // GET only: the response mode is `query` (probe Q2); a form post would be a cross-site form, which the JSON-only rule refuses.
        app.MapGet(ProviderPaths.Callback, ProviderCallbackEndpoint.HandleAsync)
            .WithTags("Providers")
            .WithDescription(
                "Where the provider sends the browser back. OpenIddict checks the state (its signature, its age, its single use, the request-forgery "
                + "cookie of the browser that started), redeems the code with the PKCE verifier and validates the identity token. The answer is always "
                + "`302` to the product's return screen (`Auth:App:FrontendUrls:ProviderReturn`), never a page: on success "
                + "`#ticket=<43 characters>&purpose=<sign_in|invite|reauth>` and the cookie `auth_pt`; on failure `#error=<code>&purpose=<purpose>` "
                + "(`purpose` left out when the state could not be read) with the codes `cancelled`, `invalid_state`, `provider_unavailable` and "
                + "`provider_error`, and nothing stored. The ticket lives 10 minutes (5 for `reauth`) and works only with the `auth_pt` of this browser. "
                + "A name that is not a provider, or an instance without the return address, is `404 not_found`.")
            .Produces(StatusCodes.Status302Found)
            .SetsHeader(
                StatusCodes.Status302Found, "Location",
                "The return screen with the fragment: `ticket` and `purpose` on success, `error` and `purpose` on failure.")
            .SetsHeader(
                StatusCodes.Status302Found, "Set-Cookie",
                $"`{ProviderBindingCookie.Name}` on success: binds the ticket to this browser; `HttpOnly`, `Secure`, `SameSite=Strict`, path "
                + $"`{ProviderBindingCookie.Path}`, `Max-Age=600`. OpenIddict's request-forgery cookie is deleted.")
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound);

        return app;
```

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ProviderCallbackTests -class Auth.IntegrationTests.ProviderCallbackErrorTests -class Auth.IntegrationTests.ProviderTestIssuerTests -class Auth.IntegrationTests.ProviderStartTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests -class Auth.IntegrationTests.SecurityHeadersTests`
passes. If `No_state_value_and_no_code_verifier_reaches_the_log` finds the state in a log category other than the client's dispatcher, set that
category to `Warning` in `appsettings.json` too, and name it in the hand-back (it is the same rule as Amendment A1). If
`A_refused_client_secret_is_provider_error_and_the_log_names_invalid_client` fails because OpenIddict records another error than
`invalid_client` for the token endpoint's `invalid_client` answer, stop and report what it records: the runbook's sentence (Task 20) depends on
it, and the orchestrator decides.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): the provider callback makes a ticket bound to the browser, or names what failed`.

---

## Day 3 — Monday 27.04: the exchange, the link, the finish

When the day is done, a ticket does what the spec's Goal says: a linked identity signs in, an address of a password account links once with the
password, and a new person finishes creating an organization or a private account and is signed in. Invitation tickets are refused until Task 11.

### Task 8: `POST /auth/providers/exchange` for sign-in tickets; the ticket rules; the two audit kinds

**Files:**
- Create: `src/Auth.Server/Providers/ProviderRequestHandler.cs`, `src/Auth.Server/Providers/ProviderLogins.cs`, `src/Auth.Server/Providers/ProviderSignIn.cs`, `src/Auth.Server/Providers/ProviderContracts.cs`, `src/Auth.Server/Providers/ProviderExchangeEndpoint.cs`
- Modify: `src/Auth.Server/Tokens/OpenIddictSetup.cs`, `src/Auth.Server/Login/LoginResponseShaper.cs`, `src/Auth.Server/Login/AtomicSignInResult.cs`, `src/Auth.Server/Tenancy/Outcome.cs`, `src/Auth.Server/Audit/AuditKinds.cs`, `src/Auth.Server/Api/EndpointMetadata.cs`, `src/Auth.Server/Api/ProviderRoutes.cs`, `src/Auth.Server/Requests/JsonFields.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/ProviderApi.cs`, `tests/Auth.IntegrationTests/Infrastructure/ProviderTestBase.cs`, `tests/Auth.IntegrationTests/AuditTableTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`, `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`, `tests/Auth.IntegrationTests/LoginScopeTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/ProviderExchangeTests.cs`, `tests/Auth.IntegrationTests/ProviderTicketTests.cs`, `tests/Auth.IntegrationTests/ProviderErrorCodesTests.cs`

**Interfaces:**
- Consumes: `ProviderTickets.FindAsync`/`ConsumeAsync` (Task 1), `ProviderBindingCookie` (Task 7), `ProviderPaths.TokenPaths` (Task 5), `SessionPrincipal.Create`, `AtomicSignInResult`, `MembershipReader`, `SignupGate`, `SignupKinds`, `EmailInput.IsSameAddress`, `AccountEndpoints.RefreshCookieSet`, `JsonLoginRequestHandler.Descriptor`.
- Produces: `JsonFields.TryAnyString(string name, out string? value)` (any JSON string, a blank one included; Tasks 9, 10 and 12 read their ticket with it); `TenancyErrors.InvalidTicket`, `NoEmail`, `NoAccount`, `AddressMismatch`, `ProviderAlreadyLinked`, `PasswordRequired`, `ReauthRequired`, `ReauthFailed`, `EmailNotVerified`, `NoMembership` (with their statuses); `AuditKinds.ProviderLinked`, `AuditKinds.ProviderReauthenticated` (28 kinds); `AtomicSignInResult(..., Action<HttpContext>? whenCommitted = null)`; `ProviderRequestHandler` (`IsProviderTokenPath(PathString)`, `Descriptor`); `ProviderLogins` (`Task<Guid?> FindAccountAsync(AuthDbContext, string provider, string key, CancellationToken)`, `void Add(AuthDbContext, Guid userId, string provider, string key)`, `Task<bool> IsLoginOfAsync(AuthDbContext, Guid userId, string provider, string key, CancellationToken)`); `ProviderSignIn` (`Task<IResult> LinkedAsync(HttpContext, AuthDbContext, UserManager<ApplicationUser>, MembershipReader, TokenOptions, TimeProvider, Guid accountId, ProviderTicket ticket, string ticketText, string binding)`, `IResult Session(HttpContext, ApplicationUser, TenantContext, string provider, IDbContextTransaction, TokenOptions, TimeProvider, Func<Task<IResult>>? whenGone = null)`); the contracts `ProviderTicketRequest`, `ProviderLinkRequest`, `ProviderFinishRequest`, `ProviderNext` (`Link`, `FinishSignup`), `ProviderLinkNext`, `ProviderFinishNext`, `ProviderExchangeResponse`; `ProviderExchangeEndpoint.HandleAsync`; `EndpointMetadata.ReadsCookie(name, description)`; test side `ProviderApi.ExchangePath`, `ProviderApi.Exchange(HttpClient, Ticket)`, `ProviderApi.Exchange(HttpClient, string ticket, string? binding)`, `ProviderApi.PostWithBinding(HttpClient, string path, object body, string? binding)`, `ProviderApi.AssertBindingCleared(HttpResponseMessage)`, `ProviderTestBase.LinkAsync(Guid userId, string provider, string key)`.

**Contract counts:** `AuditTableTests`: `The_kinds_are_the_twenty_six_of_the_contract` becomes `The_kinds_are_the_twenty_eight_of_the_contract` (26 → 28).
`OpenApiTests`: `Endpoints` gains `POST /auth/providers/exchange`; statuses `200,400,403,409,415,429`; body `ticket`;
`The_answers_that_set_or_clear_the_refresh_cookie_say_so` and `No_other_answer_says_it_sets_a_cookie` gain `POST /auth/providers/exchange 200`;
`Refresh_and_logout_take_the_refresh_cookie_and_nothing_else_does` lets the exchange read `auth_pt`; `The_responses_are_described_in_snake_case`
reads `ProviderExchangeResponse`; `The_error_codes_of_the_contract_are_in_the_descriptions` names the exchange's codes. `OpenApiHardeningTests`:
**33 → 34** operations, **21 → 22** under the charset guard.

**Decisions the plan takes here:**
- The three provider paths are made token-endpoint paths of OpenIddict here, at once (`SetTokenEndpointUris`), and one handler gives all three
  their placeholder request (ordered at `+350`, between the refresh's `+250` and the guard's `+500`, and not the completion's `+300`); the link and
  the finish get their routes in Tasks 9 and 10 (until then those paths answer `404`, as any path without a route).
- **Between this task and Task 11 the exchange answers an `invite` ticket `400 invalid_ticket`**; Task 11 gives the purpose its branch. A `reauth`
  ticket is always `invalid_ticket` at the exchange.
- A refusal of the exchange writes no audit row (the spec names rows only for the link): `no_membership` of a linked identity included.
- `auth_pt` is cleared by `AtomicSignInResult` after its commit (`whenCommitted`): the held answer is still in memory then, so the response has not
  started and a cookie can be set; an answer that is rolled back (the account gone meanwhile: `400 invalid_ticket`) leaves the cookie alone.
  **Fallback** if the test `A_linked_identity_signs_in_as_a_login_does` finds the headers read-only at that point: clear the cookie before the
  sign-in and remove every `Set-Cookie` in the `gone` branch of `AtomicSignInResult` (nothing else sets one there); report it.
- A linked identity whose account was deleted between the read of the login and the sign-in is `400 invalid_ticket` (spec, step 3.1).

- [ ] **Step 1: Write the failing tests.**

In `tests/Auth.IntegrationTests/Infrastructure/ProviderApi.cs`:

(a) Add `using System.Net.Http.Json;` after `using System.Net;`.

(b) Replace

```csharp
    /// <summary>The ticket and its binding of a successful callback.</summary>
```

with

```csharp
    public const string ExchangePath = "/auth/providers/exchange";

    /// <summary>A JSON body to a provider endpoint with the <c>auth_pt</c> cookie (none when <paramref name="binding"/> is null).</summary>
    public static async Task<HttpResponseMessage> PostWithBinding(HttpClient client, string path, object body, string? binding)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (binding is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{BindingCookie}={binding}");
        }

        return await client.SendAsync(request);
    }

    /// <summary><c>POST /auth/providers/exchange</c> with the ticket and the binding of its own round trip.</summary>
    public static Task<HttpResponseMessage> Exchange(HttpClient client, Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return Exchange(client, ticket.Value, ticket.Binding);
    }

    public static Task<HttpResponseMessage> Exchange(HttpClient client, string ticket, string? binding) =>
        PostWithBinding(client, ExchangePath, new { ticket }, binding);

    /// <summary>A request that used the ticket up clears <c>auth_pt</c>: emptied, <c>Max-Age=0</c>, with its attributes.</summary>
    public static void AssertBindingCleared(HttpResponseMessage response)
    {
        var cookie = BindingCookieOf(response) ?? throw new InvalidOperationException("The answer does not clear auth_pt.");
        Assert.Equal("", cookie.Value.Value);
        Assert.Equal(TimeSpan.Zero, cookie.MaxAge);
        Assert.Equal("/auth", cookie.Path.Value);
    }

    /// <summary>The ticket and its binding of a successful callback.</summary>
```

In `tests/Auth.IntegrationTests/Infrastructure/ProviderTestBase.cs`:

(a) Add `using Auth.Server.Providers;` after `using Auth.FakeOidc;`.

(b) Replace

```csharp
    /// <summary>A whole round trip in one browser: the start, the provider, the callback; the ticket and its binding.</summary>
```

with

```csharp
    /// <summary>Makes the identity a login of the account straight in the database, as a link leaves it.</summary>
    protected Task LinkAsync(Guid userId, string provider, string key) =>
        InDbAsync(async db =>
        {
            ProviderLogins.Add(db, userId, provider, key);
            return await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    /// <summary>A whole round trip in one browser: the start, the provider, the callback; the ticket and its binding.</summary>
```

`tests/Auth.IntegrationTests/ProviderErrorCodesTests.cs`:

```csharp
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Auth.IntegrationTests;

/// <summary>The error codes of spec 0011 and their statuses.</summary>
public sealed class ProviderErrorCodesTests
{
    [Theory]
    [InlineData(TenancyErrors.InvalidTicket, "invalid_ticket", StatusCodes.Status400BadRequest)]
    [InlineData(TenancyErrors.NoEmail, "no_email", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.NoAccount, "no_account", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.AddressMismatch, "address_mismatch", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.ProviderAlreadyLinked, "provider_already_linked", StatusCodes.Status409Conflict)]
    [InlineData(TenancyErrors.PasswordRequired, "password_required", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.ReauthRequired, "reauth_required", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.ReauthFailed, "reauth_failed", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.EmailNotVerified, "email_not_verified", StatusCodes.Status403Forbidden)]
    [InlineData(TenancyErrors.NoMembership, "no_membership", StatusCodes.Status403Forbidden)]
    public void The_codes_and_their_statuses_are_those_of_the_contract(string code, string name, int status)
    {
        Assert.Equal(name, code);
        Assert.Equal(status, TenancyErrors.StatusOf(code));
    }
}
```

`tests/Auth.IntegrationTests/ProviderExchangeTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>POST /auth/providers/exchange</c> for a sign-in ticket (criteria 10 to 13).</summary>
public sealed class ProviderExchangeTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;
    private const char Mu = (char)0x03BC;
    private const char Micro = (char)0x00B5;

    private static readonly string[] SessionClaims = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"];
    private static readonly string[] SameClaims = ["aud", "iss", "org_id", "org_kind", "sub"];

    private async Task<Guid> LinkedAccountAsync(bool member = true)
    {
        var user = await CreateUserAsync(Ola, confirmed: true, member: member);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        return user.Id;
    }

    [Fact]
    public async Task A_linked_identity_signs_in_as_a_login_does()   // criterion 10
    {
        var userId = await LinkedAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Exchange(Client, ticket);

        var session = await SessionApi.ReadSessionAsync(response);
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            Assert.Equal(["access_token", "status"], body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.Equal("authenticated", body.RootElement.GetProperty("status").GetString());
        }

        ProviderApi.AssertBindingCleared(response);
        var password = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        Assert.Equal(SessionClaims, AccessTokens.Payload(session.AccessToken).Select(p => p.Key).Order(StringComparer.Ordinal));
        foreach (var claim in SameClaims)
        {
            Assert.Equal(AccessTokens.Payload(password.AccessToken)[claim]!.ToJsonString(), AccessTokens.Payload(session.AccessToken)[claim]!.ToJsonString());
        }

        Assert.Equal(AccessTokens.Array(password.AccessToken, "roles"), AccessTokens.Array(session.AccessToken, "roles"));
        Assert.Equal(AccessTokens.Array(password.AccessToken, "permissions"), AccessTokens.Array(session.AccessToken, "permissions"));
        _ = await SessionApi.RefreshOk(Client, session.RefreshToken);   // the cookie refreshes like a login's

        var row = Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded), r => AuditApi.Text(r, "provider") is not null);
        Assert.Equal("google", AuditApi.Text(row, "provider"));
        Assert.Equal(userId, row.SubjectUserId);
        Assert.Equal(userId, row.ActorUserId);
        Assert.Equal(0, await TicketCountAsync());
    }

    [Fact]
    public async Task A_locked_login_streak_does_not_stop_a_linked_identity_and_is_not_cleared()   // criterion 10, Settled 12
    {
        await LinkedAccountAsync();
        await LockoutApi.FailAsync(Client, Clock, Ola, 10);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stillLocked = await LoginApi.Login(Client, Ola, UserPassword);
        await LockoutApi.AssertLockedAsync(stillLocked);
    }

    [Fact]
    public async Task A_linked_identity_whose_provider_address_changed_still_signs_in_to_its_account()   // criterion 11, Settled 10
    {
        var userId = await LinkedAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(email: "ola.new@gmail.test"));

        using var response = await ProviderApi.Exchange(Client, ticket);

        var session = await SessionApi.ReadSessionAsync(response);
        Assert.Equal(userId.ToString(), AccessTokens.Text(session.AccessToken, "sub"));
        var account = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken));
        Assert.Equal(Ola, account.Email);   // the account keeps the address it was made with
    }

    [Fact]
    public async Task An_unlinked_identity_with_the_address_of_a_linked_account_gets_link()   // criterion 11: the address never finds a linked account
    {
        await LinkedAccountAsync();
        var ticket = await TicketAsync("google", GooglePerson(subject: "200000000000000000002"));

        using var response = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal("""{"next":"link","email":"ola@gmail.test","email_verified":true}""", await TenancyApi.ReadAsync(response, HttpStatusCode.OK));
    }

    [Fact]
    public async Task A_verified_address_of_an_account_gives_link_and_the_ticket_stays_usable()   // criterion 12
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using var first = await ProviderApi.Exchange(Client, ticket);
        using var again = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal("""{"next":"link","email":"ola@gmail.test","email_verified":true}""", await TenancyApi.ReadAsync(first, HttpStatusCode.OK));
        Assert.Equal("""{"next":"link","email":"ola@gmail.test","email_verified":true}""", await TenancyApi.ReadAsync(again, HttpStatusCode.OK));
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task An_account_under_a_look_alike_spelling_is_409_already_registered_and_the_ticket_stays()   // criterion 12, Settled 11
    {
        await CreateUserAsync(Mu + "aria@firma.test", confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson(email: Micro + "aria@firma.test", subject: "300000000000000000003"));

        using var response = await ProviderApi.Exchange(Client, ticket);

        await SignupApi.AssertAlreadyRegisteredAsync(response);
        Assert.Equal(1, await TicketCountAsync());
    }

    [Theory]
    [InlineData(true, true, "organization,private")]
    [InlineData(true, false, "organization")]
    [InlineData(false, true, "private")]
    public async Task No_account_with_sign_up_on_gives_finish_signup_with_the_kinds_whose_switch_is_on(bool organization, bool @private, string kinds)   // criterion 12
    {
        SetManifest((organization ? AuthAppFactory.SelfServiceManifest : AuthAppFactory.DefaultManifest) + (@private ? AuthAppFactory.PrivateBlock : ""));
        var ticket = await TicketAsync("google", GooglePerson(email: "new@gmail.test"));

        using var response = await ProviderApi.Exchange(Client, ticket);

        var body = await TenancyApi.ReadOkAsync(response);
        Assert.Equal(["email", "kinds", "next"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("finish_signup", body.GetProperty("next").GetString());
        Assert.Equal("new@gmail.test", body.GetProperty("email").GetString());
        Assert.Equal(kinds.Split(','), body.GetProperty("kinds").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task No_account_with_both_switches_off_is_403_no_account()   // criterion 12
    {
        SetManifest(AuthAppFactory.DefaultManifest);
        var ticket = await TicketAsync("google", GooglePerson(email: "new@gmail.test"));

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_account");
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task An_unverified_work_address_of_an_account_gets_link_without_a_word_about_the_account()   // criterion 13, Review Focus 5
    {
        await CreateUserAsync(FakePeople.WorkEmail, confirmed: true);
        var withAccount = await TicketAsync("microsoft", WorkMicrosoft());
        var withoutAccount = await TicketAsync("microsoft", WorkMicrosoft(email: "nobody@firma.test", subject: "00000000-0000-0000-0000-00000000b002"));

        using var one = await ProviderApi.Exchange(Client, withAccount);
        using var two = await ProviderApi.Exchange(Client, withoutAccount);

        Assert.Equal("""{"next":"link","email":"bo@firma.test","email_verified":false}""", await TenancyApi.ReadAsync(one, HttpStatusCode.OK));
        Assert.Equal("""{"next":"link","email":"nobody@firma.test","email_verified":false}""", await TenancyApi.ReadAsync(two, HttpStatusCode.OK));
        Assert.Equal(LoginApi.HeaderNames(one), LoginApi.HeaderNames(two));
        Assert.Empty(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task No_address_is_403_no_email()   // criterion 13
    {
        var ticket = await TicketAsync("google", GooglePerson(defect: FakeDefects.NoEmail));

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_email");
    }

    [Fact]
    public async Task A_linked_account_without_a_membership_is_403_no_membership_and_the_ticket_stays()   // spec 0011 → exchange, step 3.1
    {
        await LinkedAccountAsync(member: false);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal(1, await TicketCountAsync());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"ticket":5}""")]
    [InlineData("""{"ticket":null}""")]
    [InlineData("""["ticket"]""")]
    public async Task A_body_that_is_not_an_object_with_a_ticket_string_is_400_invalid_request(string body)   // spec 0011 → exchange, step 1
    {
        using var response = await AccountApi.PostRaw(Client, ProviderApi.ExchangePath, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_ticket_is_a_string_and_so_an_unknown_ticket(string ticket)   // spec 0011 → exchange: only "missing or not a string" is step 1
    {
        var live = await TicketAsync("google", GooglePerson());   // a live ticket and its binding: the blank one is still unknown

        using var response = await ProviderApi.Exchange(Client, ticket, live.Binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task An_invitation_ticket_is_invalid_ticket_until_invitations_are_built()   // Task 11 replaces this test
    {
        var ticket = await TicketAsync("google", GooglePerson(), "?invite=" + "CCCCCCCCCC" + "CCCCCCCCCC" + "CCCCCCCCCC" + "CCCCCCCCCC" + "CCC");

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
    }
}
```

`tests/Auth.IntegrationTests/ProviderTicketTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011, criterion 14: one answer for every dead ticket, and one sign-in of parallel exchanges.</summary>
public sealed class ProviderTicketTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private async Task<ProviderApi.Ticket> LinkedTicketAsync(string query = "")
    {
        var user = await CreateUserAsync(FakePeople.GoogleEmail, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        return await TicketAsync("google", GooglePerson(), query);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("used")]
    [InlineData("expired")]
    [InlineData("reauth")]
    [InlineData("no-binding")]
    [InlineData("another-binding")]
    public async Task A_ticket_that_is_unknown_used_expired_of_another_purpose_or_without_its_binding_is_invalid_ticket(string how)   // criterion 14, Review Focus 2
    {
        var ticket = await LinkedTicketAsync(how == "reauth" ? "?purpose=reauth" : "");
        var value = ticket.Value;
        string? binding = ticket.Binding;
        switch (how)
        {
            case "unknown":
                value = "unknownunknown" + ticket.Value[14..];
                break;
            case "used":
                using (var first = await ProviderApi.Exchange(Client, ticket))
                {
                    Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                }

                break;
            case "expired":
                Clock.Advance(TimeSpan.FromMinutes(10));
                break;
            case "no-binding":
                binding = null;
                break;
            case "another-binding":
                binding = (await TicketAsync("microsoft", PersonalMicrosoft())).Binding;
                break;
        }

        using var response = await ProviderApi.Exchange(Client, value, binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
    }

    [Fact]
    public async Task Of_parallel_exchanges_with_one_ticket_exactly_one_signs_in()   // criterion 14
    {
        var ticket = await LinkedTicketAsync();

        var answers = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ProviderApi.Exchange(Client, ticket)));
        try
        {
            Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
            Assert.Equal(4, answers.Count(a => a.StatusCode == HttpStatusCode.BadRequest));
        }
        finally
        {
            foreach (var answer in answers)
            {
                answer.Dispose();
            }
        }

        Assert.Single(await AuditAsync(AuditKinds.LoginSucceeded));
    }

    [Fact]
    public async Task A_second_round_trip_in_the_same_browser_makes_the_first_ticket_unusable()   // Settled 9, Review Focus 2
    {
        var first = await LinkedTicketAsync();
        var second = await TicketAsync("google", GooglePerson());

        // The browser holds the auth_pt of the second round trip only: the callback replaced it.
        using var older = await ProviderApi.Exchange(Client, first.Value, second.Binding);
        using var newer = await ProviderApi.Exchange(Client, second);

        await TenancyApi.AssertErrorAsync(older, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(HttpStatusCode.OK, newer.StatusCode);
    }
}
```

Change the contract tests:

(a) `tests/Auth.IntegrationTests/AuditTableTests.cs`: replace `    public void The_kinds_are_the_twenty_six_of_the_contract()   // spec 0010, criterion 29` with
`    public void The_kinds_are_the_twenty_eight_of_the_contract()   // spec 0011, criterion 28`, and replace
`                "account.deleted", "account.delete_refused",` with

```csharp
                "account.deleted", "account.delete_refused",
                "provider.linked", "provider.reauthenticated",
```

(b) `tests/Auth.IntegrationTests/OpenApiTests.cs`:
- in `Endpoints`, replace `        "GET /auth/providers/{name}/start", "GET /auth/providers/{name}/callback",` with
  `        "GET /auth/providers/{name}/start", "GET /auth/providers/{name}/callback", "POST /auth/providers/exchange",`;
- after `    [InlineData("GET /auth/providers/{name}/callback", "302,404,429")]` add `    [InlineData("POST /auth/providers/exchange", "200,400,403,409,415,429")]`;
- after `    [InlineData("DELETE /auth/account", "password")]` add `    [InlineData("POST /auth/providers/exchange", "ticket")]`;
- after `    [InlineData("DELETE /auth/account", "204")]` (in `The_answers_that_set_or_clear_the_refresh_cookie_say_so`) add `    [InlineData("POST /auth/providers/exchange", "200")]`;
- in the `setting` array of `No_other_answer_says_it_sets_a_cookie`, after `"GET /auth/providers/{name}/callback 302"` add `, "POST /auth/providers/exchange 200"`;
- after `        Assert.Equal(["error"], Properties("ErrorBody"));` add
  `        Assert.Equal(["access_token", "email", "email_verified", "kinds", "next", "status"], Properties("ProviderExchangeResponse"));`;
- after `        Assert.Contains("too_many_attempts", Description(operations["POST /auth/signup"], "429"));` add

```csharp
        Assert.All(["invalid_request", "invalid_ticket"], code => Assert.Contains(code, Description(operations["POST /auth/providers/exchange"], "400")));
        Assert.All(["no_account", "no_email", "no_membership"], code => Assert.Contains(code, Description(operations["POST /auth/providers/exchange"], "403")));
        Assert.Contains("already_registered", Description(operations["POST /auth/providers/exchange"], "409"));
```

- replace the body of `Refresh_and_logout_take_the_refresh_cookie_and_nothing_else_does` — the lines

```csharp
            if (endpoint is "POST /auth/refresh" or "POST /auth/logout")
            {
                var cookie = Assert.Single(cookies)!;
                Assert.Equal("auth_rt", cookie["name"]!.GetValue<string>());
                Assert.Equal("string", cookie["schema"]!["type"]!.GetValue<string>());
                Assert.Null(operation["requestBody"]);
            }
            else
```

  with

```csharp
            if (endpoint is "POST /auth/refresh" or "POST /auth/logout")
            {
                var cookie = Assert.Single(cookies)!;
                Assert.Equal("auth_rt", cookie["name"]!.GetValue<string>());
                Assert.Equal("string", cookie["schema"]!["type"]!.GetValue<string>());
                Assert.Null(operation["requestBody"]);
            }
            else if (TicketEndpoints.Contains(endpoint))
            {
                // Spec 0011: the endpoints that take a provider ticket read its binding cookie beside their body.
                var cookie = Assert.Single(cookies)!;
                Assert.Equal("auth_pt", cookie["name"]!.GetValue<string>());
                Assert.NotNull(operation["requestBody"]);
            }
            else
```

  and put the new field between the end of `Endpoints` and `DescriptionAsync`: replace

```csharp
    ];

    private static async Task<JsonObject> DescriptionAsync(AuthAppFactory factory)
```

  with

```csharp
    ];

    // The endpoints that take a provider ticket and its auth_pt cookie (spec 0011).
    private static readonly string[] TicketEndpoints = ["POST /auth/providers/exchange"];

    private static async Task<JsonObject> DescriptionAsync(AuthAppFactory factory)
```

(c) `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`: replace
`        Assert.Equal(33, operations.Count);   // spec 0011: the start and the callback of a provider round trip` with
`        Assert.Equal(34, operations.Count);   // spec 0011: the start, the callback and the exchange`, and
`        Assert.Equal(21, covered);   // spec 0010: DELETE /auth/account` with `        Assert.Equal(22, covered);   // spec 0011: the exchange`.

`tests/Auth.IntegrationTests/LoginScopeTests.cs` (the spec's "To verify": the placeholder handler claims the three paths with and without a
trailing slash, and the shaper acts on them as on the login; the matchers must agree with OpenIddict's own):

(a) add `using Auth.Server.Providers;` after `using Auth.Server.Login;`;

(b) after `    [InlineData("refresh_token", "/auth/signup/complete", false)]` add

```csharp
    [InlineData("password", "/auth/providers/exchange", true)]   // spec 0011: the sign-ins through a provider answer as a login (T13)
    [InlineData("password", "/auth/providers/link", true)]
    [InlineData("password", "/auth/providers/finish", true)]
    [InlineData("password", "/AUTH/Providers/EXCHANGE", true)]
    [InlineData("password", "/auth/providers/link/", true)]
    [InlineData("password", "/auth/providers/exchange//", false)]
    [InlineData("password", "/auth/providers/finish/extra", false)]
    [InlineData("refresh_token", "/auth/providers/exchange", false)]
    [InlineData("password", "/auth/providers/google/callback", false)]
```

(c) after the method `IsCompletePath_matches_the_completion_path_only` (its last line, `        Assert.Equal(expected, SignupCompleteRequestHandler.IsCompletePath(new PathString(path)));`) add

```csharp

    [Theory]
    [InlineData("/auth/providers/exchange", true)]
    [InlineData("/auth/providers/link", true)]
    [InlineData("/auth/providers/finish", true)]
    [InlineData("/AUTH/PROVIDERS/Finish", true)]
    [InlineData("/auth/providers/exchange/", true)]
    [InlineData("/auth/providers/exchange//", false)]
    [InlineData("/auth/providers/exchange/extra", false)]
    [InlineData("/auth/providers/google/start", false)]
    [InlineData("/auth/providers", false)]
    [InlineData("/auth/login", false)]
    public void IsProviderTokenPath_matches_the_three_provider_token_paths_only(string path, bool expected) =>   // spec 0011
        Assert.Equal(expected, ProviderRequestHandler.IsProviderTokenPath(new PathString(path)));
```

- [ ] **Step 2: See them fail.** Build: the error codes, the audit kinds and the exchange do not exist.

- [ ] **Step 3: Codes and kinds.** In `src/Auth.Server/Tenancy/Outcome.cs`, replace

```csharp
    public const string PrivateAccountExists = "private_account_exists";
```

with

```csharp
    public const string PrivateAccountExists = "private_account_exists";

    // Spec 0011.
    public const string InvalidTicket = "invalid_ticket";
    public const string NoEmail = "no_email";
    public const string NoAccount = "no_account";
    public const string AddressMismatch = "address_mismatch";
    public const string ProviderAlreadyLinked = "provider_already_linked";
    public const string PasswordRequired = "password_required";
    public const string ReauthRequired = "reauth_required";
    public const string ReauthFailed = "reauth_failed";
    public const string EmailNotVerified = "email_not_verified";
    public const string NoMembership = "no_membership";
```

and replace

```csharp
        InvalidRequest or InvalidToken or WeakPassword or UnknownPermission => StatusCodes.Status400BadRequest,
        Forbidden or PermissionsChanged or PermissionNotHeld or WrongPassword or SignupDisabled => StatusCodes.Status403Forbidden,
```

with

```csharp
        InvalidRequest or InvalidToken or WeakPassword or UnknownPermission or InvalidTicket => StatusCodes.Status400BadRequest,
        Forbidden or PermissionsChanged or PermissionNotHeld or WrongPassword or SignupDisabled
            or NoEmail or NoAccount or AddressMismatch or PasswordRequired or ReauthRequired or ReauthFailed or EmailNotVerified or NoMembership
            => StatusCodes.Status403Forbidden,
```

and replace

```csharp
            or PrivateAccountExists => StatusCodes.Status409Conflict,
```

with

```csharp
            or PrivateAccountExists or ProviderAlreadyLinked => StatusCodes.Status409Conflict,
```

In `src/Auth.Server/Audit/AuditKinds.cs`, replace

```csharp
    public const string AccountDeleteRefused = "account.delete_refused";
```

with

```csharp
    public const string AccountDeleteRefused = "account.delete_refused";
    public const string ProviderLinked = "provider.linked";
    public const string ProviderReauthenticated = "provider.reauthenticated";
```

and replace

```csharp
        AccountDeleted, AccountDeleteRefused,
    ];
```

with

```csharp
        AccountDeleted, AccountDeleteRefused,
        ProviderLinked, ProviderReauthenticated,
    ];
```

and in its summary replace `(specs 0008, 0009 and 0010 → Audit log)` with `(specs 0008 to 0011 → Audit log)`.

- [ ] **Step 4: The token endpoint.** `src/Auth.Server/Providers/ProviderRequestHandler.cs`:

```csharp
using Auth.Server.Login;
using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Auth.Server.Providers;

/// <summary>
/// Makes <c>POST /auth/providers/exchange</c>, <c>/link</c> and <c>/finish</c> token-endpoint requests of OpenIddict in pass-through mode with a
/// placeholder request, as <c>SignupCompleteRequestHandler</c> does for the sign-up completion (spec 0009, Amendment 2; spec 0011, Settled 7): a
/// sign-in there is a login's in every respect. It never reads the body; each endpoint does, in the order of its contract, and nothing a client
/// sends reaches the OpenIddict request.
/// </summary>
public sealed class ProviderRequestHandler : IOpenIddictServerHandler<OpenIddictServerEvents.ExtractTokenRequestContext>
{
    private const string Placeholder = "provider-sign-in";

    /// <summary>The three paths, case-insensitive and with an optional trailing slash, as OpenIddict matches a token endpoint.</summary>
    internal static bool IsProviderTokenPath(PathString path) =>
        ProviderPaths.TokenPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.Equals(p + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>After the login's slot and before <see cref="UnhandledTokenRequestGuard"/> (+500), apart from the refresh (+250) and the completion (+300).</summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ExtractTokenRequestContext>()
            .AddFilter<OpenIddictServerAspNetCoreHandlerFilters.RequireHttpRequest>()
            .UseSingletonHandler<ProviderRequestHandler>()
            .SetOrder(JsonLoginRequestHandler.Descriptor.Order + 350)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(OpenIddictServerEvents.ExtractTokenRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Transaction.GetHttpRequest()
            ?? throw new InvalidOperationException("The token endpoint was reached without an ASP.NET Core request.");

        if (IsProviderTokenPath(request.Path))
        {
            context.Request = new OpenIddictRequest { GrantType = GrantTypes.Password, Username = Placeholder, Password = Placeholder };
        }

        return ValueTask.CompletedTask;
    }
}
```

In `src/Auth.Server/Tokens/OpenIddictSetup.cs`:

(a) Replace

```csharp
                        SignupCompleteEndpoint.Path.TrimStart('/'))
```

with

```csharp
                        SignupCompleteEndpoint.Path.TrimStart('/'),
                        ProviderPaths.Exchange.TrimStart('/'),
                        ProviderPaths.Link.TrimStart('/'),
                        ProviderPaths.Finish.TrimStart('/'))
```

(b) Replace

```csharp
                    .AddEventHandler(SignupCompleteRequestHandler.Descriptor)
```

with

```csharp
                    .AddEventHandler(SignupCompleteRequestHandler.Descriptor)
                    .AddEventHandler(ProviderRequestHandler.Descriptor)
```

In `src/Auth.Server/Login/LoginResponseShaper.cs`, add `using Auth.Server.Providers;` after `using Auth.Server.Signup;`, and replace

```csharp
        && (JsonLoginRequestHandler.IsLoginPath(path) || SignupCompleteRequestHandler.IsCompletePath(path));
```

with

```csharp
        && (JsonLoginRequestHandler.IsLoginPath(path) || SignupCompleteRequestHandler.IsCompletePath(path)
            || ProviderRequestHandler.IsProviderTokenPath(path));   // spec 0011: the sign-ins through a provider answer as a login (T13)
```

In `src/Auth.Server/Login/AtomicSignInResult.cs`:

(a) Replace

```csharp
public sealed class AtomicSignInResult(
    ClaimsPrincipal principal, AuditEntry? entry, IDbContextTransaction? opened = null, Func<Task<IResult>>? whenGone = null) : IResult
```

with

```csharp
/// <param name="whenCommitted">
/// Runs once the sign-in is committed, before the held answer is written: the answer has not started, so it may still set a header (a sign-in
/// through a provider clears <c>auth_pt</c> there, spec 0011). It never runs for an answer that is rolled back.
/// </param>
public sealed class AtomicSignInResult(
    ClaimsPrincipal principal, AuditEntry? entry, IDbContextTransaction? opened = null, Func<Task<IResult>>? whenGone = null,
    Action<HttpContext>? whenCommitted = null) : IResult
```

(b) Replace

```csharp
                        await db.SaveChangesAsync(httpContext.RequestAborted);
                        await transaction.CommitAsync(httpContext.RequestAborted);
                    }
```

with

```csharp
                        await db.SaveChangesAsync(httpContext.RequestAborted);
                        await transaction.CommitAsync(httpContext.RequestAborted);
                        whenCommitted?.Invoke(httpContext);
                    }
```

(c) Replace `                        throw new InvalidOperationException("OpenIddict refused the sign-in of a completed sign-up; the completion is rolled back.");`
with `                        throw new InvalidOperationException("OpenIddict refused a sign-in that completes a change; the change is rolled back.");`.

- [ ] **Step 5: Logins, contracts and the sign-in.** `src/Auth.Server/Providers/ProviderLogins.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Providers;

/// <summary>
/// The identities of spec 0011 are Identity's logins (<c>AspNetUserLogins</c>, Decision 2): <c>LoginProvider</c> is the provider's name,
/// <c>ProviderKey</c> the identity key. The primary key of the table is the two together, so an identity is the login of one account at most; an
/// account deletion removes its logins by cascade (spec 0010).
/// </summary>
public static class ProviderLogins
{
    /// <summary>The account the identity is a login of; <see langword="null"/> when it is none's.</summary>
    public static Task<Guid?> FindAccountAsync(AuthDbContext db, string provider, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.UserLogins.AsNoTracking()
            .Where(l => l.LoginProvider == provider && l.ProviderKey == key)
            .Select(l => (Guid?)l.UserId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Adds the identity as a login of the account to the context; the caller saves, inside its transaction.</summary>
    public static void Add(AuthDbContext db, Guid userId, string provider, string key)
    {
        ArgumentNullException.ThrowIfNull(db);

        db.UserLogins.Add(new IdentityUserLogin<Guid>
        {
            LoginProvider = provider,
            ProviderKey = key,
            ProviderDisplayName = ProviderNames.DisplayNameOf(provider),
            UserId = userId,
        });
    }

    public static Task<bool> IsLoginOfAsync(AuthDbContext db, Guid userId, string provider, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.UserLogins.AnyAsync(l => l.UserId == userId && l.LoginProvider == provider && l.ProviderKey == key, cancellationToken);
    }
}
```

`src/Auth.Server/Providers/ProviderContracts.cs`:

```csharp
namespace Auth.Server.Providers;

// The bodies of the provider endpoints (spec 0011 → Contract). Property names become snake_case on the wire. The endpoints read and write them by
// hand; the types document the contract in OpenAPI.

/// <summary>Request of <c>POST /auth/providers/exchange</c>.</summary>
public sealed record ProviderTicketRequest(string Ticket);

/// <summary>Request of <c>POST /auth/providers/link</c>: the ticket and the password of the account with the address.</summary>
public sealed record ProviderLinkRequest(string Ticket, string Password);

/// <summary>Request of <c>POST /auth/providers/finish</c>: <c>kind</c> as at <c>POST /auth/signup</c> (absent: organization), and the company name of an organization.</summary>
public sealed record ProviderFinishRequest(string Ticket, string? Kind = null, string? CompanyName = null);

/// <summary>What the exchange says comes next.</summary>
public static class ProviderNext
{
    public const string Link = "link";
    public const string FinishSignup = "finish_signup";
}

/// <summary><c>{"next":"link","email","email_verified"}</c>.</summary>
public sealed record ProviderLinkNext(string Next, string Email, bool EmailVerified);

/// <summary><c>{"next":"finish_signup","email","kinds"}</c>, <c>organization</c> first.</summary>
public sealed record ProviderFinishNext(string Next, string Email, IReadOnlyList<string> Kinds);

/// <summary>
/// For the OpenAPI description only: the <c>200</c> of the exchange is either a sign-in (<c>status</c>, <c>access_token</c>, as a login answers)
/// or what comes next (<c>next</c>, <c>email</c>, and <c>email_verified</c> or <c>kinds</c>).
/// </summary>
public sealed record ProviderExchangeResponse(
    string? Status = null, string? AccessToken = null, string? Next = null, string? Email = null, bool? EmailVerified = null,
    IReadOnlyList<string>? Kinds = null);
```

`src/Auth.Server/Providers/ProviderSignIn.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Login;
using Auth.Server.Sessions;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Storage;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Providers;

/// <summary>
/// The sign-ins through a provider (spec 0011): the session is a login's in every respect (<see cref="SessionPrincipal"/>,
/// <see cref="AtomicSignInResult"/>), <c>login.succeeded</c> names the provider, the ticket is used up in the transaction of the sign-in, and
/// <c>auth_pt</c> is cleared once it is committed.
/// </summary>
internal static class ProviderSignIn
{
    /// <summary>
    /// Signs in to the account the identity is a login of (the exchange, step 3.1; the link, step 3): an account with no membership is
    /// <c>403 no_membership</c>, as at login; otherwise, in one transaction, the ticket is used up and the session issued. A ticket used meanwhile,
    /// and an account deleted meanwhile, are <c>400 invalid_ticket</c>. The login streak is neither consulted nor cleared (Settled 12).
    /// </summary>
    public static async Task<IResult> LinkedAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, TokenOptions tokens, TimeProvider clock,
        Guid accountId, ProviderTicket ticket, string ticketText, string binding)
    {
        var account = await users.FindByIdAsync(accountId.ToString());
        if (account is null)
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);
        }

        if (await memberships.ReadAsync(account.Id, http.RequestAborted) is not { } tenant)
        {
            return ApiResults.Error(TenancyErrors.NoMembership);
        }

        var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted);
        try
        {
            if (await ProviderTickets.ConsumeAsync(db, ticketText, binding, ticket.Purpose, StorableTime.Now(clock), http.RequestAborted) is null)
            {
                await transaction.DisposeAsync();
                return ApiResults.Error(TenancyErrors.InvalidTicket);
            }

            return Session(http, account, tenant, ticket.Provider, transaction, tokens, clock, InvalidTicketAsync);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>The answer of a sign-in through a provider, in <paramref name="transaction"/>, which the result owns from now on.</summary>
    public static IResult Session(
        HttpContext http, ApplicationUser account, TenantContext tenant, string provider, IDbContextTransaction transaction, TokenOptions tokens,
        TimeProvider clock, Func<Task<IResult>>? whenGone = null) =>
        new AtomicSignInResult(
            SessionPrincipal.Create(http, account, tenant, tokens.Audience, clock),
            new AuditEntry
            {
                Kind = AuditKinds.LoginSucceeded,
                ActorUserId = account.Id,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = tenant.CompanyId,
                OrgName = tenant.CompanyName,
                Details = new Dictionary<string, object?> { ["provider"] = provider },
            },
            transaction,
            whenGone,
            whenCommitted: context => ProviderBindingCookie.Clear(context.Response));

    private static Task<IResult> InvalidTicketAsync() => Task.FromResult(ApiResults.Error(TenancyErrors.InvalidTicket));
}
```

- [ ] **Step 6: The exchange.** First the reader of a ticket member. The spec refuses a ticket that is "missing or not a string" with `400
invalid_request`; a blank string is a string, and is judged at the next step as an unknown ticket (`400 invalid_ticket`). `JsonFields.TryString`
refuses a blank string, so in `src/Auth.Server/Requests/JsonFields.cs`, after

```csharp
        return !Has(name) || TryString(name, out value);
    }
```

add

```csharp

    /// <summary>
    /// The member as any JSON string, a blank one included (spec 0011: a ticket that is a string is judged as a ticket, and a blank one is
    /// unknown); <see langword="false"/> when it is missing, not a string, or holds an unpaired surrogate escape.
    /// </summary>
    public bool TryAnyString(string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!Root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            value = element.GetString()!;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;   // a string holding an unpaired surrogate escape
        }
    }
```

Then `src/Auth.Server/Providers/ProviderExchangeEndpoint.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Signup;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Providers;

/// <summary>
/// <c>POST /auth/providers/exchange</c> (spec 0011): what a ticket leads to, in the order of the contract. A linked identity signs in (the ticket
/// used up in the transaction of the sign-in); otherwise the answer says what comes next, as plain JSON, and the ticket stays usable for the link
/// or the finish. A ticket of the purpose <c>reauth</c> is never good here.
/// </summary>
public static class ProviderExchangeEndpoint
{
    private const string TicketField = "ticket";

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, SignupGate gate,
        IOptions<TokenOptions> tokens, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);

        // Without it OpenIddict cannot sign in here: fail at once, as the sign-up completion does.
        if (http.GetOpenIddictServerRequest() is null)
        {
            throw new InvalidOperationException("The exchange was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        }

        // 1. A JSON object whose ticket is a string; a blank one is a string too, judged at step 2 as an unknown ticket.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields || !fields.TryAnyString(TicketField, out var ticketText))
        {
            return ApiResults.InvalidRequest();
        }

        ProviderBindingCookie.TryRead(http.Request, out var binding);
        var ticket = await ProviderTickets.FindAsync(db, ticketText, binding, StorableTime.Now(clock), http.RequestAborted);
        if (ticket is not { Purpose: ProviderTicketPurpose.SignIn })
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);   // unknown, used, expired, without its binding; reauth (and, until Task 11, invite)
        }

        // 3.1: the identity key is the person; the address is never used to find a linked account.
        if (await ProviderLogins.FindAccountAsync(db, ticket.Provider, ticket.IdentityKey, http.RequestAborted) is { } accountId)
        {
            return await ProviderSignIn.LinkedAsync(http, db, users, memberships, tokens.Value, clock, accountId, ticket, ticketText, binding!);
        }

        // 3.2 and 3.3: no address, or one the provider did not prove; the latter says nothing about accounts (Settled 8).
        if (ticket.Email is not { } email)
        {
            return ApiResults.Error(TenancyErrors.NoEmail);
        }

        if (!ticket.EmailVerified)
        {
            return ApiResults.Ok(new ProviderLinkNext(ProviderNext.Link, email, false));
        }

        // 3.4 and 3.5: an account with the address spelled the same up to ASCII case links; one that only resembles it is a conflict.
        if (await users.FindByEmailAsync(email) is { } account)
        {
            return EmailInput.IsSameAddress(account.Email, email)
                ? ApiResults.Ok(new ProviderLinkNext(ProviderNext.Link, email, true))
                : ApiResults.Error(TenancyErrors.AlreadyRegistered);
        }

        // 3.6 and 3.7: a new person, with the kinds whose switch is on, organization first.
        List<string> kinds = [];
        if (gate.IsOn)
        {
            kinds.Add(SignupKinds.OrganizationName);
        }

        if (gate.PrivateOn)
        {
            kinds.Add(SignupKinds.PrivateName);
        }

        return kinds.Count == 0
            ? ApiResults.Error(TenancyErrors.NoAccount)
            : ApiResults.Ok(new ProviderFinishNext(ProviderNext.FinishSignup, email, kinds));
    }
}
```

- [ ] **Step 7: Map it.** In `src/Auth.Server/Api/EndpointMetadata.cs`, replace

```csharp
    /// <summary>Documents a query parameter the handler reads itself (it binds none).</summary>
```

with

```csharp
    /// <summary>Documents a cookie the handler reads beside its body (<c>auth_pt</c>, spec 0011).</summary>
    public static RouteHandlerBuilder ReadsCookie(this RouteHandlerBuilder builder, string name, string description)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithMetadata(new CookieRequestMetadata(name, description));
    }

    /// <summary>Documents a query parameter the handler reads itself (it binds none).</summary>
```

In `src/Auth.Server/Api/ProviderRoutes.cs`:

(a) Add `using Auth.Server.Account;` before `using Auth.Server.Providers;`.

(b) Replace the last

```csharp
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound);

        return app;
```

(the one after the callback) with

```csharp
            .ProducesError(StatusCodes.Status404NotFound, TenancyErrors.NotFound);

        app.MapPost(ProviderPaths.Exchange, ProviderExchangeEndpoint.HandleAsync)
            .WithTags("Providers")
            .WithDescription(
                "What a ticket of the callback leads to; the product's return screen calls it with the ticket of the fragment and the browser's "
                + "`auth_pt` cookie. An identity that is a login of an account signs in: `200` as a login answers (the access token, the refresh "
                + "cookie), the ticket used up and `auth_pt` cleared; an account with no membership is `403 no_membership`. Otherwise the ticket stays "
                + "usable and the answer says what comes next: no address `403 no_email`; an address the provider did not verify "
                + "`{\"next\":\"link\",\"email\",\"email_verified\":false}`, whether or not an account has it; an account with the address "
                + "`{\"next\":\"link\",\"email\",\"email_verified\":true}`; an account under a look-alike spelling `409 already_registered`; no account "
                + "`{\"next\":\"finish_signup\",\"email\",\"kinds\"}` while sign-up is on, else `403 no_account`. A ticket that is unknown, used, "
                + "expired, of the purpose `reauth` or without its `auth_pt` is `400 invalid_ticket`.")
            .ReadsJson<ProviderTicketRequest>()
            .ReadsCookie(ProviderBindingCookie.Name, "The binding of the ticket, set by the callback that made it. Without it, or with another browser's, the ticket is unknown.")
            .Produces<ProviderExchangeResponse>()
            .SetsRefreshCookie(
                StatusCodes.Status200OK,
                AccountEndpoints.RefreshCookieSet + $" On a sign-in only; `{ProviderBindingCookie.Name}` is emptied then, with `Max-Age=0`.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidTicket)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.NoMembership, TenancyErrors.NoEmail, TenancyErrors.NoAccount)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyRegistered);

        return app;
```

- [ ] **Step 8: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ProviderExchangeTests -class Auth.IntegrationTests.ProviderTicketTests -class Auth.IntegrationTests.ProviderErrorCodesTests -class Auth.IntegrationTests.LoginScopeTests -class Auth.IntegrationTests.AuditTableTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests -class Auth.IntegrationTests.LoginTests -class Auth.IntegrationTests.SignupCompleteSignInTests -class Auth.IntegrationTests.SignInRaceTests -class Auth.IntegrationTests.UnhandledTokenRequestGuardTests -class Auth.IntegrationTests.SignupErrorCodesTests`
passes. If `EmailInput.IsMailbox` refuses the micro sign of `An_account_under_a_look_alike_spelling_is_409_already_registered_and_the_ticket_stays`
(the ticket's address is then unverified and the answer `next` `link`), use the sigma pair of `SignupCompleteTests.FoldedAccounts` instead and
say so in the hand-back.

- [ ] **Step 9: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): the exchange signs a linked identity in or says what comes next`.

---

### Task 9: `POST /auth/providers/link` and its lockout

**Files:**
- Create: `src/Auth.Server/Providers/ProviderLinkEndpoint.cs`
- Modify: `src/Auth.Server/Login/LoginEndpoint.cs` (one word), `src/Auth.Server/Api/ProviderRoutes.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/ProviderApi.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`, `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/ProviderLinkTests.cs`, `tests/Auth.IntegrationTests/ProviderLinkLockoutTests.cs`

**Interfaces:**
- Consumes: `ProviderTickets`, `ProviderBindingCookie`, `ProviderLogins`, `ProviderSignIn` (Task 8), `LoginStreakStore`, `LoginIdentifier`, `TooManyAttemptsResult`, `DecoyPasswordHash`, `AddressLock`, `AuditLog`, `AccountResults.EmailNotVerified`, `AccountResults.NoMembership`, `EmailInput.IsSameAddress`.
- Produces: `ProviderLinkEndpoint.HandleAsync`; `LoginEndpoint.InvalidCredentialsResult` (now `internal`, so the link answers exactly as a login does); test side `ProviderApi.LinkPath`, `ProviderApi.Link(HttpClient, Ticket, string password)`, `ProviderApi.PostRawWithBinding(HttpClient, string path, string body, string? binding)`.

**Contract counts:** `OpenApiTests`: `Endpoints` gains `POST /auth/providers/link`; statuses `200,400,401,403,415,429`; body `password,ticket`;
`The_answers_that_set_or_clear_the_refresh_cookie_say_so` and `No_other_answer_says_it_sets_a_cookie` gain `POST /auth/providers/link 200`;
`TicketEndpoints` gains it; `The_error_codes_of_the_contract_are_in_the_descriptions` names its codes. `OpenApiHardeningTests`: **34 → 35**
operations, **22 → 23** under the charset guard.

**Decisions the plan takes here:**
- The refusals after a right password (`email_not_verified`, `no_membership`) write `login.failed` with the reasons a login writes
  (`unconfirmed_address`, `no_company`) and `details.provider`, as the link's other failures do ("as at login").
- The link is written under the address lock of the account, after the ticket is used up; the account's row is then read again under a share
  lock (as `AtomicSignInResult` does), so that an account removed meanwhile is linked to nothing and answers as an unknown address. An identity
  linked meanwhile by another request (it took the same lock) gives the ticket back and signs in as step 3 says.
- The address is confirmed in the same save as the link, the `provider.linked` row and, by the result, the session and `login.succeeded`.

- [ ] **Step 1: Write the failing tests.**

In `tests/Auth.IntegrationTests/Infrastructure/ProviderApi.cs`, replace

```csharp
    /// <summary>A request that used the ticket up clears <c>auth_pt</c>: emptied, <c>Max-Age=0</c>, with its attributes.</summary>
```

with

```csharp
    public const string LinkPath = "/auth/providers/link";

    public static Task<HttpResponseMessage> Link(HttpClient client, Ticket ticket, string password)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return PostWithBinding(client, LinkPath, new { ticket = ticket.Value, password }, ticket.Binding);
    }

    /// <summary>A body as it is (for the bodies a serializer would not write), with the binding cookie.</summary>
    public static async Task<HttpResponseMessage> PostRawWithBinding(HttpClient client, string path, string body, string? binding)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        if (binding is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{BindingCookie}={binding}");
        }

        return await client.SendAsync(request);
    }

    /// <summary>A request that used the ticket up clears <c>auth_pt</c>: emptied, <c>Max-Age=0</c>, with its attributes.</summary>
```

`tests/Auth.IntegrationTests/ProviderLinkTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Seeding;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>POST /auth/providers/link</c> (criteria 15 to 17).</summary>
public sealed class ProviderLinkTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;

    // Spelled out, so that the escape in a test body is not read as an escape of this file.
    private const char Backslash = (char)0x5C;

    private Task<List<IdentityUserLogin<Guid>>> LoginsAsync() =>
        InDbAsync(db => db.UserLogins.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));

    private Task<int> AttemptsOfAsync(string normalizedEmail) =>
        InDbAsync(db => db.LoginStreaks.Where(s => s.IdentifierHash == LoginIdentifier.HashOf(normalizedEmail)).Select(s => s.AttemptCount)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken));

    /// <summary>An account with no password (as a finish leaves it), a member of the development company.</summary>
    private async Task<Guid> PasswordlessAccountAsync(string email)
    {
        var user = await CreateUserAsync(email, confirmed: true);
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var found = await users.FindByIdAsync(user.Id.ToString());
        Assert.True((await users.RemovePasswordAsync(found!)).Succeeded);
        return user.Id;
    }

    [Fact]
    public async Task The_right_password_links_and_signs_in_and_the_provider_then_signs_in_alone()   // criterion 15
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);

        var session = await SessionApi.ReadSessionAsync(linked);
        Assert.Equal(user.Id.ToString(), AccessTokens.Text(session.AccessToken, "sub"));
        ProviderApi.AssertBindingCleared(linked);
        Assert.Equal(0, await TicketCountAsync());
        var login = Assert.Single(await LoginsAsync());
        Assert.Equal(("google", FakePeople.GoogleSubject, user.Id), (login.LoginProvider, login.ProviderKey, login.UserId));
        var row = await SingleAsync(AuditKinds.ProviderLinked);
        Assert.Equal(user.Id, row.ActorUserId);
        Assert.Equal(user.Id, row.SubjectUserId);
        Assert.Equal(Ola, row.SubjectEmail);
        Assert.Equal(await DevCompanyIdAsync(), row.OrgId);
        Assert.Equal(DevUserSeeder.DefaultOrgName, row.OrgName);
        Assert.Equal(["provider"], AuditApi.DetailNames(row));
        Assert.Equal("google", AuditApi.Text(row, "provider"));
        Assert.Equal("google", AuditApi.Text(await SingleAsync(AuditKinds.LoginSucceeded), "provider"));

        // From then on the provider alone signs in, and the password still does.
        var again = await TicketAsync("google", GooglePerson());
        using (var exchanged = await ProviderApi.Exchange(Client, again))
        {
            Assert.Equal(user.Id.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(exchanged)).AccessToken, "sub"));
        }

        _ = await SessionApi.LoginAsync(Client, Ola, UserPassword);
    }

    [Fact]
    public async Task A_link_whose_rows_cannot_be_written_links_nothing_and_leaves_the_ticket()   // criterion 15: one transaction
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        await InDbAsync(db => db.Database.ExecuteSqlRawAsync("DROP TABLE audit_events", TestContext.Current.CancellationToken));

        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(HttpStatusCode.InternalServerError, linked.StatusCode);
        Assert.Empty(await LoginsAsync());
        Assert.Equal(1, await TicketCountAsync());
        Assert.Null(ProviderApi.BindingCookieOf(linked));
    }

    [Fact]
    public async Task A_wrong_password_and_an_address_without_an_account_answer_alike_and_both_count()   // criterion 16
    {
        await CreateUserAsync(Ola, confirmed: true);
        var known = await TicketAsync("google", GooglePerson());
        var unknown = await TicketAsync("google", GooglePerson(email: "nobody@gmail.test", subject: "400000000000000000004"));

        using var wrong = await ProviderApi.Link(Client, known, "Wrong-Passw0rd");
        using var nobody = await ProviderApi.Link(Client, unknown, UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, nobody.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await wrong.Content.ReadAsStringAsync());
        Assert.Equal("""{"error":"invalid_credentials"}""", await nobody.Content.ReadAsStringAsync());
        Assert.Equal(LoginApi.HeaderNames(wrong), LoginApi.HeaderNames(nobody));
        Assert.Equal(1, await AttemptsOfAsync("OLA@GMAIL.TEST"));
        Assert.Equal(1, await AttemptsOfAsync("NOBODY@GMAIL.TEST"));
        var failures = await AuditAsync(AuditKinds.LoginFailed);
        Assert.Contains(failures, r => AuditApi.Text(r, "reason") == "wrong_password" && AuditApi.Text(r, "provider") == "google" && r.SubjectEmail == Ola);
        Assert.Contains(failures, r => AuditApi.Text(r, "reason") == "unknown_address" && AuditApi.Text(r, "provider") == "google" && r.SubjectUserId is null);
        Assert.Empty(await LoginsAsync());
    }

    [Fact]
    public async Task An_account_without_a_password_answers_as_an_unknown_address()   // criterion 16, Settled 14
    {
        await PasswordlessAccountAsync(Ola);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal("unknown_address", AuditApi.Text(await SingleAsync(AuditKinds.LoginFailed), "reason"));
    }

    [Fact]
    public async Task A_wrong_password_leaves_the_ticket_usable_and_the_right_one_then_links()   // criterion 16, Review Focus 4
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());

        using var wrong = await ProviderApi.Link(Client, ticket, "Wrong-Passw0rd");
        using var right = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Null(ProviderApi.BindingCookieOf(wrong));
        Assert.Equal(user.Id.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(right)).AccessToken, "sub"));
        Assert.Equal(0, await AttemptsOfAsync("OLA@GMAIL.TEST"));   // the right password ends the streak
    }

    [Fact]
    public async Task An_unconfirmed_account_is_confirmed_when_the_address_is_verified()   // criterion 17, Settled 13
    {
        var user = await CreateUserAsync(Ola, confirmed: false);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await InDbAsync(db => db.Users.Where(u => u.Id == user.Id).Select(u => u.EmailConfirmed).SingleAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task An_unconfirmed_account_with_an_unverified_address_is_403_email_not_verified()   // criterion 17
    {
        var user = await CreateUserAsync(FakePeople.WorkEmail, confirmed: false);
        var ticket = await TicketAsync("microsoft", WorkMicrosoft());

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "email_not_verified");
        Assert.False(await InDbAsync(db => db.Users.Where(u => u.Id == user.Id).Select(u => u.EmailConfirmed).SingleAsync(TestContext.Current.CancellationToken)));
        Assert.Empty(await LoginsAsync());
        Assert.Equal(1, await TicketCountAsync());
        var row = await SingleAsync(AuditKinds.LoginFailed);
        Assert.Equal("unconfirmed_address", AuditApi.Text(row, "reason"));
        Assert.Equal("microsoft", AuditApi.Text(row, "provider"));
    }

    public static TheoryData<string> BadBodies => new()
    {
        "not json",
        "{}",
        """["ticket","password"]""",
        """{"password":"Another-Passw0rd"}""",
        """{"ticket":"x"}""",
        """{"ticket":"x","password":""}""",
        """{"ticket":"x","password":"   "}""",
        """{"ticket":"x","password":null}""",
        """{"ticket":"x","password":5}""",
        """{"ticket":5,"password":"Another-Passw0rd"}""",
        "{\"ticket\":\"x\",\"password\":\"Another" + Backslash + "u0000Passw0rd\"}",   // NUL in the password
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request_and_counts_for_nothing(string body)   // criterion 17
    {
        using var response = await ProviderApi.PostRawWithBinding(Client, ProviderApi.LinkPath, body, "any-binding");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("""{"ticket":"","password":"Another-Passw0rd"}""")]
    [InlineData("""{"ticket":"   ","password":"Another-Passw0rd"}""")]
    public async Task A_blank_ticket_is_400_invalid_ticket_and_counts_for_nothing(string body)   // spec 0011 → link: a blank ticket is a string, so step 2
    {
        using var response = await ProviderApi.PostRawWithBinding(Client, ProviderApi.LinkPath, body, "any-binding");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
        Assert.Equal(0, await InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_ticket_of_another_purpose_is_400_invalid_ticket()   // criterion 17
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
    }

    [Fact]
    public async Task An_identity_linked_meanwhile_signs_in_without_judging_the_password()   // criterion 17, spec 0011 → link, step 3
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);   // another tab linked it

        using var response = await ProviderApi.Link(Client, ticket, "Wrong-Passw0rd");

        Assert.Equal(user.Id.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(response)).AccessToken, "sub"));
        Assert.Empty(await AuditAsync(AuditKinds.LoginFailed));
        Assert.Empty(await AuditAsync(AuditKinds.ProviderLinked));
        Assert.Equal(0, await AttemptsOfAsync("OLA@GMAIL.TEST"));
    }

    [Fact]
    public async Task A_ticket_without_an_address_is_403_no_email()   // spec 0011 → link, step 4
    {
        var ticket = await TicketAsync("google", GooglePerson(defect: FakeDefects.NoEmail));

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_email");
    }

    [Fact]
    public async Task An_account_without_a_membership_is_403_no_membership()   // spec 0011 → link, step 7
    {
        await CreateUserAsync(Ola, confirmed: true, member: false);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Link(Client, ticket, UserPassword);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "no_membership");
        Assert.Equal("no_company", AuditApi.Text(await SingleAsync(AuditKinds.LoginFailed), "reason"));
        Assert.Empty(await LoginsAsync());
    }
}
```

`tests/Auth.IntegrationTests/ProviderLinkLockoutTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Lockout;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Limits: a password typed at the link counts in the streak of the ticket's address, as a login does (criterion 16).</summary>
public sealed class ProviderLinkLockoutTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;

    private async Task FailLinksAsync(ProviderApi.Ticket ticket, int times)
    {
        for (var i = 0; i < times; i++)
        {
            Clock.Advance(LockoutApi.HumanPace);
            using var response = await ProviderApi.Link(Client, ticket, LockoutApi.WrongPassword);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task After_ten_failures_the_link_is_429_too_many_attempts_and_recorded_as_login_locked_with_the_provider()   // criterion 16
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        await FailLinksAsync(ticket, 10);

        using var locked = await ProviderApi.Link(Client, ticket, UserPassword);   // even the right password

        var seconds = await LockoutApi.AssertLockedAsync(locked);
        var row = await SingleAsync(AuditKinds.LoginLocked);
        Assert.Equal(Ola, row.SubjectEmail);
        Assert.Equal("google", AuditApi.Text(row, "provider"));
        Assert.Equal(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), AuditApi.Text(row, "retry_after_seconds"));
        Assert.Equal(1, await TicketCountAsync());   // a refusal leaves the ticket
    }

    [Fact]
    public async Task Failures_at_the_login_and_at_the_link_share_one_streak()   // criterion 16
    {
        await CreateUserAsync(Ola, confirmed: true);
        var ticket = await TicketAsync("google", GooglePerson());
        await LockoutApi.FailAsync(Client, Clock, Ola, 5);
        await FailLinksAsync(ticket, 5);

        using var locked = await LoginApi.Login(Client, Ola, MailTestBase.UserPassword);

        await LockoutApi.AssertLockedAsync(locked);
    }

    [Fact]
    public async Task A_provider_sign_in_neither_counts_nor_clears_the_streak()   // spec 0011 → Limits
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);
        await LockoutApi.FailAsync(Client, Clock, Ola, 3);
        var ticket = await TicketAsync("google", GooglePerson());

        using var response = await ProviderApi.Exchange(Client, ticket);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var attempts = await InDbAsync(db => db.LoginStreaks.Where(s => s.IdentifierHash == LoginIdentifier.HashOf("OLA@GMAIL.TEST"))
            .Select(s => s.AttemptCount).SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, attempts);
    }
}
```

Change the contract tests:

(a) `tests/Auth.IntegrationTests/OpenApiTests.cs`:
- in `Endpoints`, replace `"POST /auth/providers/exchange",` with `"POST /auth/providers/exchange", "POST /auth/providers/link",`;
- after `    [InlineData("POST /auth/providers/exchange", "200,400,403,409,415,429")]` add `    [InlineData("POST /auth/providers/link", "200,400,401,403,415,429")]`;
- after `    [InlineData("POST /auth/providers/exchange", "ticket")]` add `    [InlineData("POST /auth/providers/link", "password,ticket")]`;
- after `    [InlineData("POST /auth/providers/exchange", "200")]` add `    [InlineData("POST /auth/providers/link", "200")]`;
- in the `setting` array, after `"POST /auth/providers/exchange 200"` add `, "POST /auth/providers/link 200"`;
- replace `    private static readonly string[] TicketEndpoints = ["POST /auth/providers/exchange"];` with
  `    private static readonly string[] TicketEndpoints = ["POST /auth/providers/exchange", "POST /auth/providers/link"];`;
- after `        Assert.Contains("already_registered", Description(operations["POST /auth/providers/exchange"], "409"));` add

```csharp
        Assert.Contains("invalid_credentials", Description(operations["POST /auth/providers/link"], "401"));
        Assert.All(["email_not_verified", "no_email", "no_membership"], code => Assert.Contains(code, Description(operations["POST /auth/providers/link"], "403")));
        Assert.Contains("too_many_attempts", Description(operations["POST /auth/providers/link"], "429"));
```

(b) `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`: `Assert.Equal(34, operations.Count);   // spec 0011: the start, the callback and the exchange` becomes
`Assert.Equal(35, operations.Count);   // spec 0011: the start, the callback, the exchange and the link`, and
`Assert.Equal(22, covered);   // spec 0011: the exchange` becomes `Assert.Equal(23, covered);   // spec 0011: the exchange and the link`.

- [ ] **Step 2: See them fail.** Build, then run `ProviderLinkTests`: the route does not exist (`404`).

- [ ] **Step 3: The answer of a login.** In `src/Auth.Server/Login/LoginEndpoint.cs`, replace `    private sealed class InvalidCredentialsResult : IResult` with
`    internal sealed class InvalidCredentialsResult : IResult` (the link answers a refused password with this very result, so status, body and
headers are those of a login).

- [ ] **Step 4: The link.** `src/Auth.Server/Providers/ProviderLinkEndpoint.cs`:

```csharp
using Auth.Infrastructure.Identity;
using Auth.Infrastructure.Persistence;
using Auth.Server.Account;
using Auth.Server.Api;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Lockout;
using Auth.Server.Login;
using Auth.Server.Requests;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Providers;

/// <summary>
/// <c>POST /auth/providers/link</c> (spec 0011): the password of the account with the ticket's address, once (Decision 7), and from then on the
/// provider alone signs in. The checks run in the order of the contract: the body; the ticket (a live sign-in ticket of this browser); an identity
/// linked meanwhile signs in without the password being judged; no address; the attempt counted in the login streak of the address before anything
/// is looked up; an unknown address, an account under another spelling or without a password, and a wrong password are one
/// <c>401 invalid_credentials</c> after a password hash; then, the password right, the streak cleared, the address confirmed when the provider
/// verified it (else <c>403 email_not_verified</c>), no membership <c>403 no_membership</c>; and in one transaction under the address lock the
/// login, <c>provider.linked</c>, the ticket used up and the sign-in.
/// </summary>
public static class ProviderLinkEndpoint
{
    private const string TicketField = "ticket";
    private const string PasswordField = "password";

    public static async Task<IResult> HandleAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, LoginStreakStore streaks,
        DecoyPasswordHash decoy, AuditLog audit, IOptions<TokenOptions> tokens, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(streaks);
        ArgumentNullException.ThrowIfNull(decoy);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);

        if (http.GetOpenIddictServerRequest() is null)
        {
            throw new InvalidOperationException("The link was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        }

        // 1. A ticket that is a string (a blank one is unknown at step 2), and a password that is a non-blank string with no NUL.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields
            || !fields.TryAnyString(TicketField, out var ticketText)
            || !fields.TryString(PasswordField, out var password)
            || password.Contains('\0'))
        {
            return ApiResults.InvalidRequest();
        }

        // 2. A live sign-in ticket of this browser.
        ProviderBindingCookie.TryRead(http.Request, out var binding);
        var ticket = await ProviderTickets.FindAsync(db, ticketText, binding, StorableTime.Now(clock), http.RequestAborted);
        if (ticket is not { Purpose: ProviderTicketPurpose.SignIn })
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);
        }

        // 3. Linked by now (another tab): sign in as the exchange does; the password is not judged.
        if (await ProviderLogins.FindAccountAsync(db, ticket.Provider, ticket.IdentityKey, http.RequestAborted) is { } linked)
        {
            return await ProviderSignIn.LinkedAsync(http, db, users, memberships, tokens.Value, clock, linked, ticket, ticketText, binding!);
        }

        // 4.
        if (ticket.Email is not { } email)
        {
            return ApiResults.Error(TenancyErrors.NoEmail);
        }

        // 5. Counted before anything is looked up, in the streak of the address (spec 0003): a password typed here is a login attempt.
        var identifier = LoginIdentifier.HashOf(users.NormalizeEmail(email));
        var decision = await streaks.RegisterAttemptAsync(identifier, http.RequestAborted);
        if (!decision.Allowed)
        {
            await audit.WriteAloneAsync(new AuditEntry
            {
                Kind = AuditKinds.LoginLocked,
                SubjectEmail = email,
                Details = new Dictionary<string, object?>
                {
                    ["retry_after_seconds"] = TooManyAttemptsResult.SecondsOf(decision.RetryAfter),
                    ["provider"] = ticket.Provider,
                },
            });
            return new TooManyAttemptsResult(decision.RetryAfter);
        }

        // 6. One answer for no account, an account under another spelling, an account without a password, and a wrong password.
        var account = await users.FindByEmailAsync(email);
        if (account is null || !EmailInput.IsSameAddress(account.Email, email) || account.PasswordHash is null)
        {
            _ = users.PasswordHasher.VerifyHashedPassword(account ?? new ApplicationUser(), decoy.Value, password);
            await RecordFailureAsync(audit, "unknown_address", null, email, ticket.Provider);
            return new LoginEndpoint.InvalidCredentialsResult();
        }

        if (!await users.CheckPasswordAsync(account, password))
        {
            await RecordFailureAsync(audit, "wrong_password", account, email, ticket.Provider);
            return new LoginEndpoint.InvalidCredentialsResult();
        }

        // 7. The password proves the account: the streak is over (not the request's token, as at login).
        await streaks.ClearAsync(identifier, CancellationToken.None);
        if (!account.EmailConfirmed && !ticket.EmailVerified)
        {
            await RecordFailureAsync(audit, "unconfirmed_address", account, email, ticket.Provider);
            return AccountResults.EmailNotVerified();
        }

        if (await memberships.ReadAsync(account.Id, http.RequestAborted) is null)
        {
            await RecordFailureAsync(audit, "no_company", account, email, ticket.Provider);
            return AccountResults.NoMembership();
        }

        return await LinkAndSignInAsync(http, db, users, memberships, audit, tokens.Value, clock, account, ticket, ticketText, binding!);
    }

    /// <summary>8. In one transaction under the address lock: the login, <c>provider.linked</c>, the ticket used up, the sign-in.</summary>
    private static async Task<IResult> LinkAndSignInAsync(
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, AuditLog audit, TokenOptions tokens,
        TimeProvider clock, ApplicationUser account, ProviderTicket ticket, string ticketText, string binding)
    {
        // Not the request's token from here on: a client that goes away must not leave the link half done.
        var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        try
        {
            await AddressLock.AcquireAsync(db, account.NormalizedEmail!, CancellationToken.None);
            if (await ProviderTickets.ConsumeAsync(db, ticketText, binding, ProviderTicketPurpose.SignIn, StorableTime.Now(clock), CancellationToken.None) is null)
            {
                await transaction.DisposeAsync();
                return ApiResults.Error(TenancyErrors.InvalidTicket);
            }

            // Another request linked the identity while this one checked the password (it took the same lock): give the ticket back and sign in.
            if (await ProviderLogins.FindAccountAsync(db, ticket.Provider, ticket.IdentityKey, CancellationToken.None) is { } linked)
            {
                await transaction.DisposeAsync();
                return await ProviderSignIn.LinkedAsync(http, db, users, memberships, tokens, clock, linked, ticket, ticketText, binding);
            }

            // The account's row under a share lock to the end: one removed meanwhile is linked to nothing (the answer of an unknown address).
            if (!await AccountIsThereAsync(db, account.Id) || await memberships.ReadAsync(account.Id, CancellationToken.None) is not { } tenant)
            {
                await transaction.DisposeAsync();
                return new LoginEndpoint.InvalidCredentialsResult();
            }

            ProviderLogins.Add(db, account.Id, ticket.Provider, ticket.IdentityKey);
            account.EmailConfirmed = true;   // the password proves the account, the provider the mailbox (Settled 13)
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.ProviderLinked,
                ActorUserId = account.Id,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = tenant.CompanyId,
                OrgName = tenant.CompanyName,
                Details = new Dictionary<string, object?> { ["provider"] = ticket.Provider },
            });
            await db.SaveChangesAsync(CancellationToken.None);
            return ProviderSignIn.Session(http, account, tenant, ticket.Provider, transaction, tokens, clock, InvalidCredentialsAsync);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private static async Task<bool> AccountIsThereAsync(AuthDbContext db, Guid userId)
    {
        var found = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM "AspNetUsers" WHERE "Id" = {userId} FOR KEY SHARE""")
            .ToListAsync(CancellationToken.None);
        return found.Count == 1;
    }

    private static Task<IResult> InvalidCredentialsAsync() => Task.FromResult<IResult>(new LoginEndpoint.InvalidCredentialsResult());

    /// <summary>A failed link, as a failed login is written, with the provider.</summary>
    private static Task RecordFailureAsync(AuditLog audit, string reason, ApplicationUser? user, string email, string provider) =>
        audit.WriteAloneAsync(new AuditEntry
        {
            Kind = AuditKinds.LoginFailed,
            SubjectUserId = user?.Id,
            SubjectEmail = user?.Email ?? email,
            Details = new Dictionary<string, object?> { ["reason"] = reason, ["provider"] = provider },
        });
}
```

- [ ] **Step 5: Map it.** In `src/Auth.Server/Api/ProviderRoutes.cs`, add `using Auth.Server.Login;` after `using Auth.Server.Account;` and replace

```csharp
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyRegistered);

        return app;
```

with

```csharp
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyRegistered);

        app.MapPost(ProviderPaths.Link, ProviderLinkEndpoint.HandleAsync)
            .WithTags("Providers")
            .WithDescription(
                "Links the identity of a sign-in ticket to the account with the ticket's address, with that account's password, once; from then on "
                + "the provider alone signs in. The password counts in the login streak of the address as a login does: while the identifier is "
                + "locked the answer is `429 too_many_attempts`. No account with the address (spelled the same up to ASCII case), an account without a "
                + "password, and a wrong password are the same `401 invalid_credentials`, and the ticket stays usable. With the right password an "
                + "unconfirmed address is confirmed when the provider verified it (else `403 email_not_verified`), and an account with no membership is "
                + "`403 no_membership`. Then the login, `provider.linked`, the use of the ticket and the session are one change: `200` as a login "
                + "answers, `auth_pt` cleared. An identity linked meanwhile signs in without the password being judged. A ticket without an address is "
                + "`403 no_email`; one that is unknown, used, expired, not of the purpose `sign_in` or without its `auth_pt` is `400 invalid_ticket`.")
            .ReadsJson<ProviderLinkRequest>()
            .ReadsCookie(ProviderBindingCookie.Name, "The binding of the ticket, set by the callback that made it.")
            .Produces<LoginResponse>()
            .SetsRefreshCookie(
                StatusCodes.Status200OK, AccountEndpoints.RefreshCookieSet + $" `{ProviderBindingCookie.Name}` is emptied, with `Max-Age=0`.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidTicket)
            .ProducesError(StatusCodes.Status401Unauthorized, LoginEndpoint.InvalidCredentialsError)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.NoEmail, TenancyErrors.EmailNotVerified, TenancyErrors.NoMembership)
            .ProducesTooManyAttempts();

        return app;
```

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ProviderLinkTests -class Auth.IntegrationTests.ProviderLinkLockoutTests -class Auth.IntegrationTests.ProviderExchangeTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests -class Auth.IntegrationTests.LoginTests -class Auth.IntegrationTests.LoginTimingTests -class Auth.IntegrationTests.LockoutTests`
passes.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): a provider links to an account with its password once`.

---

### Task 10: `POST /auth/providers/finish` and its races

**Files:**
- Create: `src/Auth.Server/Providers/ProviderFinishEndpoint.cs`
- Modify: `src/Auth.Server/Signup/SignupService.cs`, `src/Auth.Server/Api/ProviderRoutes.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/ProviderApi.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`, `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/ProviderFinishTests.cs`, `tests/Auth.IntegrationTests/ProviderFinishRaceTests.cs`

**Interfaces:**
- Consumes: `ProviderTickets`, `ProviderBindingCookie`, `ProviderLogins`, `ProviderSignIn.Session` (Task 8), `SignupGate`, `SignupKinds.TryRead`, `NameInput.IsValid`, `JsonObjectBody.ReadObjectAsync`, `SignupAccounts.FindAsync`, `PersonalSpaces.Add`, `AddressLock`, `HeldLock`, `PendingSignupSeed`.
- Produces: `record ProviderFinish(ProviderTicket Ticket, string TicketText, string Binding, SignupKind Kind, string? CompanyName)` and `SignupService.FinishThroughProviderAsync(ProviderFinish, CancellationToken)` returning `SignupCompletion` (the transaction handed over, as `CompleteAsync`); `ProviderFinishEndpoint.HandleAsync`; test side `ProviderApi.FinishPath`, `ProviderApi.Finish(HttpClient, Ticket, string? kind = null, string? companyName = null)`.

**Contract counts:** `OpenApiTests`: `Endpoints` gains `POST /auth/providers/finish`; statuses `200,400,403,409,415,429`; body
`company_name,kind,ticket`; the two cookie tests gain `POST /auth/providers/finish 200`; `TicketEndpoints` gains it; the error codes test names
its codes. `OpenApiHardeningTests`: **35 → 36** operations, **23 → 24** under the charset guard (the counts of the spec).

**Decisions the plan takes here:**
- The finish mirrors `SignupService.CompleteAsync` in the same class: the address lock, the ticket used up after it, the account, the space as a
  completion makes it, the transaction handed over to the sign-in. It does not end sessions or clear the streak of the address (a new account
  has no session, and a provider sign-in neither counts nor clears the streak, spec 0011 → Limits).
- A refusal under the lock (`already_registered`, a ticket used meanwhile) rolls everything back, the use of the ticket included (spec: "a request
  that refuses changes nothing"), unlike a mail completion, whose pending sign-up is deleted by an `already_registered`.
- The pending sign-up of the address, of either kind, is deleted in the transaction; a mail of it still queued is then dropped or answered by the
  dispatcher as for a completion through the mail.

- [ ] **Step 1: Write the failing tests.**

In `tests/Auth.IntegrationTests/Infrastructure/ProviderApi.cs`, replace

```csharp
    /// <summary>A body as it is (for the bodies a serializer would not write), with the binding cookie.</summary>
```

with

```csharp
    public const string FinishPath = "/auth/providers/finish";

    /// <summary><c>POST /auth/providers/finish</c>; a member left null is left out of the body.</summary>
    public static Task<HttpResponseMessage> Finish(HttpClient client, Ticket ticket, string? kind = null, string? companyName = null)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var body = new Dictionary<string, object?> { ["ticket"] = ticket.Value };
        if (kind is not null)
        {
            body["kind"] = kind;
        }

        if (companyName is not null)
        {
            body["company_name"] = companyName;
        }

        return PostWithBinding(client, FinishPath, body, ticket.Binding);
    }

    /// <summary>A body as it is (for the bodies a serializer would not write), with the binding cookie.</summary>
```

`tests/Auth.IntegrationTests/ProviderFinishTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>POST /auth/providers/finish</c> (criteria 18 and 19).</summary>
public sealed class ProviderFinishTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string NewPerson = "new@gmail.test";
    private const string NewSubject = "500000000000000000005";
    private const char Mu = (char)0x03BC;
    private const char Micro = (char)0x00B5;

    private Task<ProviderApi.Ticket> NewTicketAsync(string email = NewPerson) => TicketAsync("google", GooglePerson(email: email, subject: NewSubject));

    private Task<int> AccountsAsync(string normalized) =>
        InDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == normalized, TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_private_finish_makes_an_account_without_a_password_with_its_login_and_a_personal_space_and_signs_in()   // criterion 18
    {
        await PendingSignupSeed.IssueAsync(Factory.Services, NewPerson, null, SignupKind.Private);   // a sign-up through the mail, asked for too
        var ticket = await NewTicketAsync();
        var cancellation = TestContext.Current.CancellationToken;

        using var response = await ProviderApi.Finish(Client, ticket, kind: "private");

        var session = await SessionApi.ReadSessionAsync(response);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal("personal", AccessTokens.Text(session.AccessToken, "org_kind"));
        var account = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "NEW@GMAIL.TEST", cancellation));
        Assert.Equal(account.Id.ToString(), AccessTokens.Text(session.AccessToken, "sub"));
        Assert.Null(account.PasswordHash);
        Assert.True(account.EmailConfirmed);
        Assert.Equal(NewPerson, account.Email);
        var login = Assert.Single(await InDbAsync(db => db.UserLogins.AsNoTracking().ToListAsync(cancellation)));
        Assert.Equal(("google", NewSubject, account.Id), (login.LoginProvider, login.ProviderKey, login.UserId));
        var space = await InDbAsync(db => (
            from membership in db.Memberships
            join company in db.Companies on membership.CompanyId equals company.Id
            where membership.UserId == account.Id
            select company).AsNoTracking().SingleAsync(cancellation));
        Assert.Equal(CompanyKind.Personal, space.Kind);
        Assert.Empty(await PendingAsync());
        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal(["kind", "provider"], AuditApi.DetailNames(completed));
        Assert.Equal("private", AuditApi.Text(completed, "kind"));
        Assert.Equal("google", AuditApi.Text(completed, "provider"));
        Assert.Equal(account.Id, completed.SubjectUserId);
        Assert.Equal(space.Id, completed.OrgId);
        Assert.Equal(PersonalSpaces.Name, completed.OrgName);
        Assert.Equal("google", AuditApi.Text(await SingleAsync(AuditKinds.LoginSucceeded), "provider"));
        Assert.Empty(await AuditAsync(AuditKinds.OrgCreated));
        Assert.Equal(0, await TicketCountAsync());
        using var password = await LoginApi.Login(Client, NewPerson, "Any-Passw0rd-1");
        Assert.Equal(HttpStatusCode.Unauthorized, password.StatusCode);   // no password: the 401 of an unknown address
    }

    [Fact]
    public async Task An_organization_finish_makes_the_company_with_the_default_roles_and_the_first_admins_role()   // criterion 18
    {
        var ticket = await NewTicketAsync();
        var cancellation = TestContext.Current.CancellationToken;

        using var response = await ProviderApi.Finish(Client, ticket, kind: "organization", companyName: "Firma Nowa");

        var session = await SessionApi.ReadSessionAsync(response);
        Assert.Equal("organization", AccessTokens.Text(session.AccessToken, "org_kind"));
        Assert.Equal(["admin"], AccessTokens.Array(session.AccessToken, "roles"));
        var company = await InDbAsync(db => db.Companies.AsNoTracking().SingleAsync(c => c.Name == "Firma Nowa", cancellation));
        Assert.Equal(company.Id.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(
            ["admin", "user"],
            await InDbAsync(db => db.CompanyRoles.Where(r => r.CompanyId == company.Id).Select(r => r.Name).OrderBy(n => n).ToListAsync(cancellation)));
        Assert.Equal("signup", AuditApi.Text(await SingleAsync(AuditKinds.OrgCreated), "via"));
        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.Equal("organization", AuditApi.Text(completed, "kind"));
        Assert.Equal("google", AuditApi.Text(completed, "provider"));
    }

    [Fact]
    public async Task Without_kind_it_is_an_organization()   // spec 0011 → finish, step 2
    {
        var ticket = await NewTicketAsync();

        using var response = await ProviderApi.Finish(Client, ticket, companyName: "Firma Bez Rodzaju");

        Assert.Equal("organization", AccessTokens.Text((await SessionApi.ReadSessionAsync(response)).AccessToken, "org_kind"));
    }

    [Fact]
    public async Task With_both_switches_off_the_finish_is_403_before_the_body_is_read()   // criterion 19
    {
        SetManifest(AuthAppFactory.DefaultManifest);

        using var response = await ProviderApi.PostRawWithBinding(Client, ProviderApi.FinishPath, "not json", null);

        await SignupApi.AssertDisabledAsync(response);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("organization")]
    public async Task A_kind_whose_switch_is_off_is_403_signup_disabled_and_the_ticket_stays(string kind)   // criterion 19
    {
        var ticket = await NewTicketAsync();
        SetManifest(kind == "private" ? AuthAppFactory.SelfServiceManifest : AuthAppFactory.DefaultManifest + AuthAppFactory.PrivateBlock);

        using var response = await ProviderApi.Finish(Client, ticket, kind, kind == "organization" ? "Firma" : null);

        await SignupApi.AssertDisabledAsync(response);
        Assert.Equal(1, await TicketCountAsync());
    }

    public static TheoryData<string> BadBodies => new()
    {
        "not json",
        "{}",
        """["ticket"]""",
        """{"ticket":5}""",
        """{"ticket":"x","kind":"company"}""",
        """{"ticket":"x","kind":"Private"}""",
        """{"ticket":"x","kind":5}""",
        """{"ticket":"x","kind":null}""",
        """{"ticket":"x","company_name":" Firma"}""",
        """{"ticket":"x","company_name":""}""",
        """{"ticket":"x","company_name":5}""",
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task A_bad_body_is_400_invalid_request(string body)   // criterion 19
    {
        using var response = await ProviderApi.PostRawWithBinding(Client, ProviderApi.FinishPath, body, "any-binding");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Theory]
    [InlineData("""{"ticket":"","kind":"private"}""")]
    [InlineData("""{"ticket":"   ","kind":"private"}""")]
    public async Task A_blank_ticket_is_400_invalid_ticket(string body)   // spec 0011 → finish: a blank ticket is a string, so step 3
    {
        using var response = await ProviderApi.PostRawWithBinding(Client, ProviderApi.FinishPath, body, "any-binding");

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_ticket");
    }

    [Theory]
    [InlineData("organization", null)]
    [InlineData("private", "Firma")]
    public async Task An_organization_without_a_company_name_or_a_private_finish_with_one_is_400_and_the_ticket_stays(string kind, string? name)   // criterion 19
    {
        var ticket = await NewTicketAsync();

        using var refused = await ProviderApi.Finish(Client, ticket, kind, name);
        using var afterwards = await ProviderApi.Finish(Client, ticket, "private");

        await TenancyApi.AssertErrorAsync(refused, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(HttpStatusCode.OK, afterwards.StatusCode);
    }

    [Fact]
    public async Task An_unverified_or_missing_address_is_403_email_not_verified()   // criterion 19, Review Focus 5
    {
        var unverified = await TicketAsync("microsoft", WorkMicrosoft(email: "new@firma.test", subject: "00000000-0000-0000-0000-00000000b003"));
        var none = await TicketAsync("google", GooglePerson(defect: FakeDefects.NoEmail));

        using var one = await ProviderApi.Finish(Client, unverified, "private");
        using var two = await ProviderApi.Finish(Client, none, "private");

        await TenancyApi.AssertErrorAsync(one, HttpStatusCode.Forbidden, "email_not_verified");
        await TenancyApi.AssertErrorAsync(two, HttpStatusCode.Forbidden, "email_not_verified");
        Assert.Equal(0, await AccountsAsync("NEW@FIRMA.TEST"));
    }

    [Theory]
    [InlineData("company")]
    [InlineData("private")]
    [InlineData("look-alike")]
    public async Task An_address_with_any_account_is_409_already_registered_and_nothing_changes(string which)   // criterion 19
    {
        var typed = NewPerson;
        switch (which)
        {
            case "company":
                await CreateUserAsync(NewPerson, confirmed: true);
                break;
            case "private":
                await CreatePrivateAccountAsync(NewPerson);
                break;
            default:
                await CreateUserAsync(Mu + "aria@firma.test", confirmed: true);
                typed = Micro + "aria@firma.test";
                break;
        }

        var ticket = await NewTicketAsync(typed);

        using var response = await ProviderApi.Finish(Client, ticket, "private");

        await SignupApi.AssertAlreadyRegisteredAsync(response);
        Assert.Empty(await InDbAsync(db => db.UserLogins.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task An_identity_linked_meanwhile_is_409_already_registered()   // criterion 19, spec 0011 → finish, step 7
    {
        var other = await CreateUserAsync("someone@gmail.test", confirmed: true);
        var ticket = await NewTicketAsync();
        await LinkAsync(other.Id, "google", NewSubject);

        using var response = await ProviderApi.Finish(Client, ticket, "private");

        await SignupApi.AssertAlreadyRegisteredAsync(response);
        Assert.Equal(0, await AccountsAsync("NEW@GMAIL.TEST"));
    }

    [Fact]
    public async Task A_ticket_of_another_purpose_or_without_its_binding_is_invalid_ticket()   // spec 0011 → finish, step 3
    {
        var reauth = await TicketAsync("google", GooglePerson(email: NewPerson, subject: NewSubject), "?purpose=reauth");
        var unbound = await NewTicketAsync();

        using var one = await ProviderApi.Finish(Client, reauth, "private");
        using var two = await ProviderApi.Finish(Client, unbound with { Binding = reauth.Binding }, "private");

        await TenancyApi.AssertErrorAsync(one, HttpStatusCode.BadRequest, "invalid_ticket");
        await TenancyApi.AssertErrorAsync(two, HttpStatusCode.BadRequest, "invalid_ticket");
    }
}
```

`tests/Auth.IntegrationTests/ProviderFinishRaceTests.cs`:

```csharp
using System.Net;
using Auth.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0011, criterion 20: of a finish, a sign-up completion and an invitation acceptance running at once for one address, at most one makes the
/// account: they take the same address lock. Each race holds that lock in the test and lets the requests queue behind it in the order named, as
/// <c>PrivateAccountRaceTests</c> does.
/// </summary>
public sealed class ProviderFinishRaceTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Racer = "race@gmail.test";
    private const string Normalized = "RACE@GMAIL.TEST";

    private Task<int> AccountsAsync() => InDbAsync(db => db.Users.CountAsync(u => u.NormalizedEmail == Normalized, TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_finish_a_completion_and_an_acceptance_for_one_address_make_one_account()   // criterion 20
    {
        var ticket = await TicketAsync("google", GooglePerson(email: Racer, subject: "600000000000000000006"));
        var signupToken = await PendingSignupSeed.IssueAsync(Factory.Services, Racer, "Firma");
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, Racer, "user");
        await EnqueueInvitationAsync(invite, Racer);
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == Racer));

        Task<HttpResponseMessage> finish, completion, acceptance;
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            finish = ProviderApi.Finish(Client, ticket, "private");
            await HeldLock.WaitForWaitersAsync(Factory, 1);
            completion = SignupApi.Complete(Client, signupToken, Password, "Firma");
            await HeldLock.WaitForWaitersAsync(Factory, 2);
            acceptance = TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd");
            await HeldLock.WaitForWaitersAsync(Factory, 3);
            await held.ReleaseAsync();
        }

        using var finished = await finish;
        using var completed = await completion;
        using var accepted = await acceptance;
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        // The finish deleted the pending sign-up of the address: the link of the mail is used up by then.
        await TenancyApi.AssertErrorAsync(completed, HttpStatusCode.BadRequest, "invalid_token");
        await TenancyApi.AssertErrorAsync(accepted, HttpStatusCode.Conflict, "private_account_exists");
        Assert.Equal(1, await AccountsAsync());
    }

    [Theory]
    [InlineData("completion")]
    [InlineData("acceptance")]
    public async Task A_completion_or_an_acceptance_ahead_of_a_finish_makes_the_account_and_the_finish_is_409_with_its_ticket_kept(string first)   // criterion 20, the other order
    {
        var ticket = await TicketAsync("google", GooglePerson(email: Racer, subject: "600000000000000000006"));
        Func<Task<HttpResponseMessage>> ahead;
        if (first == "completion")
        {
            var signupToken = await PendingSignupSeed.IssueAsync(Factory.Services, Racer, "Firma");
            ahead = () => SignupApi.Complete(Client, signupToken, Password, "Firma");
        }
        else
        {
            var company = await CreateCompanyAsync("Acme");
            var invite = await AddInviteAsync(company, Racer, "user");
            await EnqueueInvitationAsync(invite, Racer);
            await DispatchAsync();
            var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == Racer));
            ahead = () => TenancyApi.Accept(Client, inviteToken, "Invited-Passw0rd");
        }

        Task<HttpResponseMessage> made, finish;
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            made = ahead();
            await HeldLock.WaitForWaitersAsync(Factory, 1);
            finish = ProviderApi.Finish(Client, ticket, "private");
            await HeldLock.WaitForWaitersAsync(Factory, 2);
            await held.ReleaseAsync();
        }

        using var madeAnswer = await made;
        using var finished = await finish;
        Assert.True(madeAnswer.IsSuccessStatusCode);   // 200 for the completion, 204 for the acceptance
        await SignupApi.AssertAlreadyRegisteredAsync(finished);
        Assert.Equal(1, await AccountsAsync());
        Assert.Equal(1, await TicketCountAsync());   // the refused finish leaves its ticket (spec 0011 → tickets: a refusal changes nothing)
        Assert.Empty(await InDbAsync(db => db.UserLogins.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Two_finishes_with_two_tickets_for_one_address_make_one_account()   // criterion 20
    {
        var first = await TicketAsync("google", GooglePerson(email: Racer, subject: "600000000000000000006"));
        var second = await TicketAsync("microsoft", PersonalMicrosoft(email: Racer));

        Task<HttpResponseMessage> one, two;
        await using (var held = await HeldLock.AddressAsync(Factory, Normalized))
        {
            one = ProviderApi.Finish(Client, first, "private");
            await HeldLock.WaitForWaitersAsync(Factory, 1);
            two = ProviderApi.Finish(Client, second, "organization", "Firma");
            await HeldLock.WaitForWaitersAsync(Factory, 2);
            await held.ReleaseAsync();
        }

        using var made = await one;
        using var refused = await two;
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        await SignupApi.AssertAlreadyRegisteredAsync(refused);
        Assert.Equal(1, await AccountsAsync());
        Assert.Single(await InDbAsync(db => db.UserLogins.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)));
        Assert.Equal(1, await TicketCountAsync());   // the refused ticket stays until it expires
    }
}
```

Change the contract tests:

(a) `tests/Auth.IntegrationTests/OpenApiTests.cs`:
- in `Endpoints`, replace `"POST /auth/providers/exchange", "POST /auth/providers/link",` with `"POST /auth/providers/exchange", "POST /auth/providers/link", "POST /auth/providers/finish",`;
- after `    [InlineData("POST /auth/providers/link", "200,400,401,403,415,429")]` add `    [InlineData("POST /auth/providers/finish", "200,400,403,409,415,429")]`;
- after `    [InlineData("POST /auth/providers/link", "password,ticket")]` add `    [InlineData("POST /auth/providers/finish", "company_name,kind,ticket")]`;
- after `    [InlineData("POST /auth/providers/link", "200")]` add `    [InlineData("POST /auth/providers/finish", "200")]`;
- in the `setting` array, after `"POST /auth/providers/link 200"` add `, "POST /auth/providers/finish 200"`;
- `TicketEndpoints` becomes `["POST /auth/providers/exchange", "POST /auth/providers/link", "POST /auth/providers/finish"]`;
- after `        Assert.Contains("too_many_attempts", Description(operations["POST /auth/providers/link"], "429"));` add

```csharp
        Assert.All(["email_not_verified", "signup_disabled"], code => Assert.Contains(code, Description(operations["POST /auth/providers/finish"], "403")));
        Assert.Contains("already_registered", Description(operations["POST /auth/providers/finish"], "409"));
        Assert.All(["invalid_request", "invalid_ticket"], code => Assert.Contains(code, Description(operations["POST /auth/providers/finish"], "400")));
```

(b) `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`: replace
`        Assert.Equal(35, operations.Count);   // spec 0011: the start, the callback, the exchange and the link` with
`        Assert.Equal(36, operations.Count);   // spec 0011: the five provider operations`, and
`        Assert.Equal(23, covered);   // spec 0011: the exchange and the link` with
`        Assert.Equal(24, covered);   // spec 0011: the exchange, the link and the finish`.

- [ ] **Step 2: See them fail.** Build, then run `ProviderFinishTests`: the route does not exist (`404`).

- [ ] **Step 3: The service.** In `src/Auth.Server/Signup/SignupService.cs`:

(a) Add `using Auth.Server.Providers;` after `using Auth.Server.Email;`.

(b) Replace

```csharp
public sealed record SignupPreview(SignupKind Kind, string Email, string? CompanyName);
```

with

```csharp
public sealed record SignupPreview(SignupKind Kind, string Email, string? CompanyName);

/// <summary>A finish through a provider (spec 0011): the ticket as read, the ticket and its binding as sent, and what the body asked for.</summary>
public sealed record ProviderFinish(ProviderTicket Ticket, string TicketText, string Binding, SignupKind Kind, string? CompanyName);
```

(c) Replace

```csharp
    /// <summary>The company of an organization sign-up (spec 0009): a copy of the default roles, <c>org.created</c> with <c>via</c> <c>signup</c>, and the membership with the first admin's role.</summary>
```

with

```csharp
    /// <summary>
    /// Finishes a sign-up through a provider (spec 0011 → finish, steps 7 and 8; the endpoint has judged steps 1 to 6). Under the address lock the
    /// ticket is used up; any account under the address (of either kind, under any spelling the normaliser folds together), or the identity a
    /// login by now, is <c>already_registered</c> and nothing changes, the ticket included. Otherwise the account is made with the provider's
    /// address and no password, its email confirmed (the provider verified it, Decision 8), the identity becomes its login, the space is made as
    /// at a completion, the pending sign-up of the address is deleted and <c>signup.completed</c> is written with the kind and the provider. As in
    /// <see cref="CompleteAsync"/>, nothing is committed: the transaction is handed over, because the sign-in is part of the finish.
    /// </summary>
    public async Task<SignupCompletion> FinishThroughProviderAsync(ProviderFinish finish, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finish);

        var ticket = finish.Ticket;
        var email = ticket.Email ?? throw new InvalidOperationException("A finish needs the address of its ticket.");
        var normalized = users.NormalizeEmail(email);
        var now = StorableTime.Now(clock);

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var handedOver = false;
        try
        {
            // The lock of the completion and of the acceptance (spec 0011 → finish): of those running at once for one address, one makes the account.
            await AddressLock.AcquireAsync(db, normalized, cancellationToken);
            if (await ProviderTickets.ConsumeAsync(db, finish.TicketText, finish.Binding, ProviderTicketPurpose.SignIn, now, cancellationToken) is null)
            {
                return Refused(TenancyErrors.InvalidTicket);
            }

            if (await SignupAccounts.FindAsync(db, normalized, cancellationToken) is not null
                || await ProviderLogins.FindAccountAsync(db, ticket.Provider, ticket.IdentityKey, cancellationToken) is not null)
            {
                return Refused(TenancyErrors.AlreadyRegistered);
            }

            var account = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            AccountSessions.Ensure(await users.CreateAsync(account), "Could not set up the account");
            ProviderLogins.Add(db, account.Id, ticket.Provider, ticket.IdentityKey);

            var (spaceId, spaceName) = finish.Kind == SignupKind.Private
                ? (PersonalSpaces.Add(db, account.Id, now), PersonalSpaces.Name)
                : await CreateCompanyAsync(
                    account, finish.CompanyName ?? throw new InvalidOperationException("An organization finish has a company name."), now, cancellationToken);

            await db.PendingSignups.Where(p => p.NormalizedEmail == normalized).ExecuteDeleteAsync(cancellationToken);
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.SignupCompleted,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = spaceId,
                OrgName = spaceName,
                Details = new Dictionary<string, object?> { ["kind"] = SignupKinds.NameOf(finish.Kind), ["provider"] = ticket.Provider },
            });
            await db.SaveChangesAsync(CancellationToken.None);

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
```

- [ ] **Step 4: The endpoint.** `src/Auth.Server/Providers/ProviderFinishEndpoint.cs`:

```csharp
using Auth.Infrastructure.Persistence;
using Auth.Server.Api;
using Auth.Server.Email;
using Auth.Server.Requests;
using Auth.Server.Signup;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore;
using Microsoft.Extensions.Options;
using TokenOptions = Auth.Server.Tokens.TokenOptions;

namespace Auth.Server.Providers;

/// <summary>
/// <c>POST /auth/providers/finish</c> (spec 0011): a new person finishes creating their account with a verified provider address and is signed in
/// at once (Decision 13). It mirrors <c>POST /auth/signup/complete</c> with the ticket in place of the mail link and no password; the checks run in
/// the order of the contract.
/// </summary>
public static class ProviderFinishEndpoint
{
    private const string TicketField = "ticket";
    private const string CompanyNameField = "company_name";

    public static async Task<IResult> HandleAsync(
        HttpContext http, SignupGate gate, AuthDbContext db, SignupService signup, MembershipReader memberships, IOptions<TokenOptions> tokens,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(signup);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(clock);

        if (http.GetOpenIddictServerRequest() is null)
        {
            throw new InvalidOperationException("The finish was reached without an OpenIddict request; the token endpoint passthrough is misconfigured.");
        }

        // 1. Both switches off: before the body is read.
        if (!gate.AnyOn)
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        // 2. The body: a ticket; `kind` absent (an organization) or one of the two; a company name that keeps the name rule when it is there.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields
            || !fields.TryAnyString(TicketField, out var ticketText)   // a blank ticket is a string: unknown at step 3
            || !SignupKinds.TryRead(fields, out var kind)
            || !fields.TryOptionalString(CompanyNameField, out var companyName)
            || (companyName is not null && !NameInput.IsValid(companyName)))
        {
            return ApiResults.InvalidRequest();
        }

        // 3. A live sign-in ticket of this browser.
        ProviderBindingCookie.TryRead(http.Request, out var binding);
        var ticket = await ProviderTickets.FindAsync(db, ticketText, binding, StorableTime.Now(clock), http.RequestAborted);
        if (ticket is not { Purpose: ProviderTicketPurpose.SignIn })
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);
        }

        // 4. Only an address the provider verified skips the mail of sign-up (Decision 12).
        if (ticket.Email is null || !ticket.EmailVerified)
        {
            return ApiResults.Error(TenancyErrors.EmailNotVerified);
        }

        // 5 and 6.
        if (!gate.IsOnFor(kind))
        {
            return ApiResults.Error(TenancyErrors.SignupDisabled);
        }

        if ((kind == SignupKind.Organization) != (companyName is not null))
        {
            return ApiResults.InvalidRequest();   // an organization names its company; a private person names none; the ticket stays usable
        }

        // 7 and 8.
        var done = await signup.FinishThroughProviderAsync(new ProviderFinish(ticket, ticketText, binding!, kind, companyName), http.RequestAborted);
        if (!done.Succeeded)
        {
            return ApiResults.Error(done.Error!);
        }

        try
        {
            // Read inside the transaction of the finish: it sees the membership that is not committed yet.
            var account = done.Account!;
            var tenant = await memberships.ReadAsync(account.Id, CancellationToken.None)
                ?? throw new InvalidOperationException("The account that was just made has no space.");
            return ProviderSignIn.Session(http, account, tenant, ticket.Provider, done.Transaction!, tokens.Value, clock);
        }
        catch
        {
            await done.Transaction!.DisposeAsync();
            throw;
        }
    }
}
```

- [ ] **Step 5: Map it.** In `src/Auth.Server/Api/ProviderRoutes.cs`, replace

```csharp
            .ProducesTooManyAttempts();

        return app;
```

with

```csharp
            .ProducesTooManyAttempts();

        app.MapPost(ProviderPaths.Finish, ProviderFinishEndpoint.HandleAsync)
            .WithTags("Providers")
            .WithDescription(
                "A new person finishes creating their account with the verified address of a sign-in ticket, with no password and no mail, and is "
                + "signed in at once: `200` as a login answers, `auth_pt` cleared. In one transaction the account (its email confirmed), the identity "
                + "as its login, the space (`kind` `organization` with `company_name`: the company with a copy of the default roles and the first "
                + "admin's role; `private`: the personal space; absent: an organization), the deletion of the address's pending sign-up and "
                + "`signup.completed` are made. The checks: both sign-up switches off `403 signup_disabled` before the body is read; a bad body "
                + "`400 invalid_request`; a ticket that is unknown, used, expired, not of the purpose `sign_in` or without its `auth_pt` "
                + "`400 invalid_ticket`; no address or an unverified one `403 email_not_verified`; the switch of the kind off `403 signup_disabled`; an "
                + "organization without, or a private finish with, `company_name` `400 invalid_request` (the ticket stays usable); any account under "
                + "the address, or the identity linked meanwhile, `409 already_registered`.")
            .ReadsJson<ProviderFinishRequest>()
            .ReadsCookie(ProviderBindingCookie.Name, "The binding of the ticket, set by the callback that made it.")
            .Produces<LoginResponse>()
            .SetsRefreshCookie(
                StatusCodes.Status200OK, AccountEndpoints.RefreshCookieSet + $" `{ProviderBindingCookie.Name}` is emptied, with `Max-Age=0`.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidTicket)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.SignupDisabled, TenancyErrors.EmailNotVerified)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyRegistered);

        return app;
```

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ProviderFinishTests -class Auth.IntegrationTests.ProviderFinishRaceTests -class Auth.IntegrationTests.SignupCompleteTests -class Auth.IntegrationTests.PrivateSignupCompleteTests -class Auth.IntegrationTests.SignupCompleteRaceTests -class Auth.IntegrationTests.PrivateAccountRaceTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests`
passes.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): a new person finishes signing up through a provider and is signed in`.

---

## Day 4 — Tuesday 28.04: the invitation, the deletion, the limits and the audit

When the day is done, an invited person accepts with Google or Microsoft, a person without a password deletes a private account with the
provider and sets a password with "forgot password", `GET /auth/me` says `has_password` and `linked_providers`, the five provider paths count
against the `login` limit, and an instance of spec 0010 upgrades without noticing.

### Task 11: An invitation through a provider

**Files:**
- Modify: `src/Auth.Server/Tenancy/InviteAcceptance.cs`, `src/Auth.Server/Providers/ProviderExchangeEndpoint.cs`, `src/Auth.Server/Api/ProviderRoutes.cs`
- Modify (tests): `tests/Auth.IntegrationTests/ProviderExchangeTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/ProviderInvitationTests.cs`

**Interfaces:**
- Consumes: `ProviderTickets.ConsumeAsync`, `ProviderLogins` (Tasks 1, 8), `ProviderSignIn.Session` (Task 8), `CompanyLock`, `AddressLock`, `EmailInput.IsSameAddress`, `EmailInput.MayStandForAnotherAddress`, `InviteTokens.IssueAsync`, `TenancyTestBase.AddInviteAsync`/`EnqueueInvitationAsync`/`InviteAsync`.
- Produces: in `Auth.Server.Tenancy`: `record ProviderInvite(ProviderTicket Ticket, string TicketText, string Binding)`, `record ProviderAcceptance(string? Error, ApplicationUser? Account = null, Guid CompanyId = default, IDbContextTransaction? Transaction = null)` (`Succeeded`), and `InviteAcceptance.AcceptThroughProviderAsync(ProviderInvite, CancellationToken)`; the exchange's branch for `invite` tickets.

**Contract counts:** `OpenApiTests.The_error_codes_of_the_contract_are_in_the_descriptions` names the invitation codes of the exchange
(`invalid_token`, `address_mismatch`, `already_member`, `private_account_exists`, `provider_already_linked`). The placeholder test
`ProviderExchangeTests.An_invitation_ticket_is_invalid_ticket_until_invitations_are_built` of Task 8 is removed.

**Decisions the plan takes here:**
- The acceptance through a provider lives in `InviteAcceptance` beside the acceptance with a password: one class reads, locks and uses an
  invitation (the company lock, then the address lock, spec 0010 → Locks), and its private helpers (`SpaceOfAsync`, `LockAccountAsync`) serve both.
- An account under the invited address with no space at all (only a direct change of the database leaves one) is `409 already_member` here: an
  account exists, and a provider cannot take it over without its password (Decision 7).
- The ticket is used up after every check that can refuse, in the same transaction; every refusal rolls back the use of the invitation too, so the
  invitation stays usable (criterion 22).

- [ ] **Step 1: Write the failing tests.** `tests/Auth.IntegrationTests/ProviderInvitationTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Email;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → exchange, purpose <c>invite</c> (criteria 21 and 22).</summary>
public sealed class ProviderInvitationTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Invited = "jan@firma.test";
    private const string JanSubject = "700000000000000000007";

    private async Task<(Guid Company, Guid Invite, string Token)> InvitationAsync()
    {
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, Invited, "user");
        await EnqueueInvitationAsync(invite, Invited);
        await DispatchAsync();
        return (company, invite, TokenIn(Mail.Sent.Last(m => m.To == Invited)));
    }

    private static FakeSignIn Jan(string email = Invited, bool verified = true) => GooglePerson(email: email, verified: verified, subject: JanSubject);

    private Task<ProviderApi.Ticket> InviteTicketAsync(string token, FakeSignIn person) => TicketAsync("google", person, "?invite=" + token);

    private async Task AssertInvitationStaysAsync(Guid invite, string token)
    {
        var row = await InviteAsync(invite);
        Assert.NotNull(row);
        Assert.Equal(EmailTokens.HashOf(token), row.TokenHash);
        Assert.Equal(1, await TicketCountAsync());
    }

    [Fact]
    public async Task An_invitation_through_google_makes_the_account_without_a_password_and_signs_in_to_the_company()   // criterion 21
    {
        var (company, invite, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());
        var cancellation = TestContext.Current.CancellationToken;

        using var response = await ProviderApi.Exchange(Client, ticket);

        var session = await SessionApi.ReadSessionAsync(response);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal("organization", AccessTokens.Text(session.AccessToken, "org_kind"));
        Assert.Equal(company.ToString(), AccessTokens.Text(session.AccessToken, "org_id"));
        Assert.Equal(["user"], AccessTokens.Array(session.AccessToken, "roles"));
        var account = await InDbAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == "JAN@FIRMA.TEST", cancellation));
        Assert.Null(account.PasswordHash);
        Assert.True(account.EmailConfirmed);
        Assert.Equal(Invited, account.Email);
        var login = Assert.Single(await InDbAsync(db => db.UserLogins.AsNoTracking().ToListAsync(cancellation)));
        Assert.Equal(("google", JanSubject, account.Id), (login.LoginProvider, login.ProviderKey, login.UserId));
        Assert.Null(await InviteAsync(invite));
        Assert.Equal(0, await TicketCountAsync());
        var accepted = await SingleAsync(AuditKinds.InviteAccepted);
        Assert.Equal(["provider", "role"], AuditApi.DetailNames(accepted));
        Assert.Equal("user", AuditApi.Text(accepted, "role"));
        Assert.Equal("google", AuditApi.Text(accepted, "provider"));
        Assert.Equal(account.Id, accepted.SubjectUserId);
        Assert.Equal(company, accepted.OrgId);
        Assert.Equal("Acme", accepted.OrgName);
        Assert.Equal(invite, accepted.TargetId);
        Assert.Equal("google", AuditApi.Text(await SingleAsync(AuditKinds.LoginSucceeded), "provider"));
    }

    [Theory]
    [InlineData("used")]
    [InlineData("expired")]
    [InlineData("cancelled")]
    [InlineData("replaced")]
    public async Task A_used_expired_cancelled_or_replaced_invitation_is_400_invalid_token(string how)   // criterion 22
    {
        var (_, invite, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());
        var cancellation = TestContext.Current.CancellationToken;
        switch (how)
        {
            case "used":
                using (var accepted = await TenancyApi.Accept(Client, token, "Invited-Passw0rd"))
                {
                    Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
                }

                break;
            case "expired":
                var past = StorableTime.Now(Clock).AddSeconds(-1);
                await InDbAsync(db => db.Invites.Where(i => i.Id == invite).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAt, past), cancellation));
                break;
            case "cancelled":
                await InDbAsync(db => db.Invites.Where(i => i.Id == invite).ExecuteDeleteAsync(cancellation));
                break;
            default:
                await InDbAsync(async db => (await InviteTokens.IssueAsync(db, invite, StorableTime.Now(Clock), cancellation)).Length);
                break;
        }

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_token");
        Assert.Equal(1, await TicketCountAsync());
    }

    [Theory]
    [InlineData("another")]
    [InlineData("unverified")]
    [InlineData("none")]
    public async Task Another_address_an_unverified_one_or_none_is_403_address_mismatch_and_the_invitation_stays(string which)   // criterion 22, Review Focus 5
    {
        var (_, invite, token) = await InvitationAsync();
        var person = which switch
        {
            "another" => Jan(email: "other@firma.test"),
            "unverified" => Jan(verified: false),
            _ => GooglePerson(subject: JanSubject, defect: FakeDefects.NoEmail),
        };
        var ticket = await InviteTicketAsync(token, person);

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "address_mismatch");
        await AssertInvitationStaysAsync(invite, token);
        using var accepted = await TenancyApi.Accept(Client, token, "Invited-Passw0rd");   // it still works
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    [Fact]
    public async Task A_company_members_address_is_409_already_member_and_the_invitation_stays()   // criterion 22
    {
        var (_, invite, token) = await InvitationAsync();
        await CreateUserAsync(Invited, confirmed: true);   // a member of the development company
        var ticket = await InviteTicketAsync(token, Jan());

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "already_member");
        await AssertInvitationStaysAsync(invite, token);
    }

    [Fact]
    public async Task A_private_accounts_address_is_409_private_account_exists_and_the_invitation_stays()   // criterion 22, Settled 15
    {
        var (_, invite, token) = await InvitationAsync();
        await CreatePrivateAccountAsync(Invited);
        var ticket = await InviteTicketAsync(token, Jan());

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "private_account_exists");
        await AssertInvitationStaysAsync(invite, token);
        using var moved = await TenancyApi.AcceptLeaving(Client, token);   // the move of spec 0010 still works
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
    }

    [Fact]
    public async Task An_identity_already_linked_is_409_provider_already_linked_and_the_invitation_stays()   // criterion 22
    {
        var (_, invite, token) = await InvitationAsync();
        var other = await CreateUserAsync("jan.old@firma.test", confirmed: true);
        await LinkAsync(other.Id, "google", JanSubject);
        var ticket = await InviteTicketAsync(token, Jan());

        using var response = await ProviderApi.Exchange(Client, ticket);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Conflict, "provider_already_linked");
        await AssertInvitationStaysAsync(invite, token);
    }

    [Fact]
    public async Task An_invitation_ticket_is_not_good_at_the_link_or_the_finish()   // spec 0011: the link and the finish take sign-in tickets only
    {
        var (_, _, token) = await InvitationAsync();
        var ticket = await InviteTicketAsync(token, Jan());

        using var linked = await ProviderApi.Link(Client, ticket, UserPassword);
        using var finished = await ProviderApi.Finish(Client, ticket, "private");

        await TenancyApi.AssertErrorAsync(linked, HttpStatusCode.BadRequest, "invalid_ticket");
        await TenancyApi.AssertErrorAsync(finished, HttpStatusCode.BadRequest, "invalid_ticket");
    }
}
```

In `tests/Auth.IntegrationTests/ProviderExchangeTests.cs`, delete the method `An_invitation_ticket_is_invalid_ticket_until_invitations_are_built`
(from its `[Fact]` to its closing brace, and the blank line before it).

In `tests/Auth.IntegrationTests/OpenApiTests.cs`, after
`        Assert.Contains("already_registered", Description(operations["POST /auth/providers/exchange"], "409"));` add

```csharp
        Assert.Contains("invalid_token", Description(operations["POST /auth/providers/exchange"], "400"));
        Assert.Contains("address_mismatch", Description(operations["POST /auth/providers/exchange"], "403"));
        Assert.All(
            ["already_member", "private_account_exists", "provider_already_linked"],
            code => Assert.Contains(code, Description(operations["POST /auth/providers/exchange"], "409")));
```

- [ ] **Step 2: See them fail.** Build, then run `ProviderInvitationTests`: an invitation ticket is `400 invalid_ticket` (Task 8's stage).

- [ ] **Step 3: The acceptance.** In `src/Auth.Server/Tenancy/InviteAcceptance.cs`:

(a) Add `using Auth.Server.Providers;` after `using Auth.Server.Email;`, and `using Microsoft.EntityFrameworkCore.Storage;` after `using Microsoft.EntityFrameworkCore;`.

(b) Replace

```csharp
public sealed record InvitePreview(string OrgName, string Email, string Role, bool HasPrivateAccount);
```

with

```csharp
public sealed record InvitePreview(string OrgName, string Email, string Role, bool HasPrivateAccount);

/// <summary>An acceptance through a provider (spec 0011): the ticket as read, and the ticket and its binding as sent.</summary>
public sealed record ProviderInvite(ProviderTicket Ticket, string TicketText, string Binding);

/// <summary>
/// How <see cref="InviteAcceptance.AcceptThroughProviderAsync"/> ended: a refusal has its <see cref="Error"/>; success has the new account, its
/// company and the transaction that still holds them, not committed (the sign-in is written in it).
/// </summary>
public sealed record ProviderAcceptance(string? Error, ApplicationUser? Account = null, Guid CompanyId = default, IDbContextTransaction? Transaction = null)
{
    public bool Succeeded => Error is null;
}
```

(c) Replace

```csharp
    private static Outcome<IReadOnlyList<string>> Refused(string error) => new(null, error);
```

with

```csharp
    /// <summary>
    /// Accepts an invitation through a provider (spec 0011 → exchange, purpose <c>invite</c>; spec 0010's acceptance with the provider in place of
    /// the password), in the order of the contract: the invitation found by the hash the ticket holds (unknown, used, expired, cancelled, replaced
    /// or a look-alike: <c>invalid_token</c>); the ticket's address missing, unverified or not the invited one (<c>address_mismatch</c>,
    /// Decision 9); a company member's address (<c>already_member</c>) or a private account's (<c>private_account_exists</c>: the move of spec 0010
    /// is the screen's own button, Settled 15); the identity a login already (<c>provider_already_linked</c>). Every refusal changes nothing, the
    /// invitation and the ticket included. Otherwise, under the company lock and then the address lock, the invitation and the ticket are used up,
    /// the account is made with the invited address and no password, its email confirmed, the identity its login, a member with the invited role,
    /// and <c>invite.accepted</c> names the role and the provider. Nothing is committed: the transaction is handed over, because the acceptance
    /// signs the person in (Settled 16).
    /// </summary>
    public async Task<ProviderAcceptance> AcceptThroughProviderAsync(ProviderInvite request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ticket = request.Ticket;
        var hash = ticket.InviteTokenHash ?? throw new InvalidOperationException("An invitation ticket holds the hash of its invitation.");
        var now = StorableTime.Now(clock);

        // Leaving this method without handing the transaction over rolls everything back, the use of the invitation included.
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var handedOver = false;
        try
        {
            // The company first, as every change to a company's members does; then the invitation, as AcceptAsync does.
            var companyId = await db.Invites.Where(i => i.TokenHash == hash && i.ExpiresAt > now)
                .Select(i => (Guid?)i.CompanyId).FirstOrDefaultAsync(cancellationToken);
            if (companyId is not { } company || !await CompanyLock.AcquireAsync(db, company, cancellationToken))
            {
                return ProviderRefused(TenancyErrors.InvalidToken);
            }

            // ToListAsync, not SingleOrDefaultAsync: EF must send the statement as it is, not wrapped in a subquery.
            var consumed = await db.Invites
                .FromSql($"""DELETE FROM "Invites" WHERE "TokenHash" = {hash} AND "ExpiresAt" > {now} RETURNING *""")
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            if (consumed.Count != 1 || EmailInput.MayStandForAnotherAddress(consumed[0].Email))
            {
                return ProviderRefused(TenancyErrors.InvalidToken);
            }

            var invite = consumed[0];

            // The invitation is bound to the address it named: only a verified provider address spelled the same up to ASCII case accepts it.
            if (ticket.Email is null || !ticket.EmailVerified || !EmailInput.IsSameAddress(ticket.Email, invite.Email))
            {
                return ProviderRefused(TenancyErrors.AddressMismatch);
            }

            await AddressLock.AcquireAsync(db, invite.NormalizedEmail, cancellationToken);
            var user = await users.FindByEmailAsync(invite.Email);
            if (user is not null && !EmailInput.IsSameAddress(user.Email, invite.Email))
            {
                return ProviderRefused(TenancyErrors.InvalidToken);
            }

            var space = user is null ? null : await SpaceOfAsync(user.Id, cancellationToken);
            if (user is not null && space is null && !await LockAccountAsync(user, cancellationToken))
            {
                user = null;   // removed between the two reads: the address belongs to no account now
            }

            if (space is { Kind: CompanyKind.Organization })
            {
                return ProviderRefused(TenancyErrors.AlreadyMember);
            }

            if (space is { Kind: CompanyKind.Personal })
            {
                return ProviderRefused(TenancyErrors.PrivateAccountExists);
            }

            if (user is not null)
            {
                return ProviderRefused(TenancyErrors.AlreadyMember);   // an account with no space: a provider never takes an account over
            }

            if (await ProviderLogins.FindAccountAsync(db, ticket.Provider, ticket.IdentityKey, cancellationToken) is not null)
            {
                return ProviderRefused(TenancyErrors.ProviderAlreadyLinked);
            }

            if (await ProviderTickets.ConsumeAsync(db, request.TicketText, request.Binding, ProviderTicketPurpose.Invite, now, cancellationToken) is null)
            {
                return ProviderRefused(TenancyErrors.InvalidTicket);
            }

            // The provider proved the invited mailbox: the email is confirmed, and no password is set (spec 0011 → Accounts without a password).
            var account = new ApplicationUser { UserName = invite.Email, Email = invite.Email, EmailConfirmed = true };
            Ensure(await users.CreateAsync(account));
            ProviderLogins.Add(db, account.Id, ticket.Provider, ticket.IdentityKey);
            db.Memberships.Add(new Membership { UserId = account.Id, CompanyId = invite.CompanyId, RoleId = invite.RoleId, JoinedAt = now });
            audit.Stage(new AuditEntry
            {
                Kind = AuditKinds.InviteAccepted,
                SubjectUserId = account.Id,
                SubjectEmail = account.Email,
                OrgId = invite.CompanyId,
                OrgName = await audit.CompanyNameAsync(invite.CompanyId, cancellationToken),
                TargetId = invite.Id,
                Details = new Dictionary<string, object?>
                {
                    ["role"] = await db.CompanyRoles.AsNoTracking().Where(r => r.Id == invite.RoleId).Select(r => r.Name).FirstOrDefaultAsync(cancellationToken),
                    ["provider"] = ticket.Provider,
                },
            });
            await db.SaveChangesAsync(CancellationToken.None);

            handedOver = true;
            return new ProviderAcceptance(null, account, invite.CompanyId, transaction);
        }
        finally
        {
            if (!handedOver)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static ProviderAcceptance ProviderRefused(string error) => new(error);

    private static Outcome<IReadOnlyList<string>> Refused(string error) => new(null, error);
```

- [ ] **Step 4: The exchange's branch.** In `src/Auth.Server/Providers/ProviderExchangeEndpoint.cs`:

(a) Replace

```csharp
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, SignupGate gate,
        IOptions<TokenOptions> tokens, TimeProvider clock)
    {
```

with

```csharp
        HttpContext http, AuthDbContext db, UserManager<ApplicationUser> users, MembershipReader memberships, SignupGate gate,
        InviteAcceptance acceptance, IOptions<TokenOptions> tokens, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(acceptance);
```

(b) Replace

```csharp
        if (ticket is not { Purpose: ProviderTicketPurpose.SignIn })
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);   // unknown, used, expired, without its binding; reauth (and, until Task 11, invite)
        }
```

with

```csharp
        if (ticket is null || ticket.Purpose == ProviderTicketPurpose.Reauth)
        {
            return ApiResults.Error(TenancyErrors.InvalidTicket);   // unknown, used, expired, without its binding, or a re-authentication
        }

        // 4: an invitation, accepted with the provider's address in place of a password.
        if (ticket.Purpose == ProviderTicketPurpose.Invite)
        {
            return await AcceptAsync(http, acceptance, memberships, tokens.Value, clock, ticket, ticketText, binding!);
        }
```

(c) Replace the last closing lines of the class

```csharp
        return kinds.Count == 0
            ? ApiResults.Error(TenancyErrors.NoAccount)
            : ApiResults.Ok(new ProviderFinishNext(ProviderNext.FinishSignup, email, kinds));
    }
}
```

with

```csharp
        return kinds.Count == 0
            ? ApiResults.Error(TenancyErrors.NoAccount)
            : ApiResults.Ok(new ProviderFinishNext(ProviderNext.FinishSignup, email, kinds));
    }

    /// <summary>Step 4 of the exchange: the acceptance, then the sign-in in its transaction (an invitation through a provider signs in at once).</summary>
    private static async Task<IResult> AcceptAsync(
        HttpContext http, InviteAcceptance acceptance, MembershipReader memberships, TokenOptions tokens, TimeProvider clock,
        ProviderTicket ticket, string ticketText, string binding)
    {
        var accepted = await acceptance.AcceptThroughProviderAsync(new ProviderInvite(ticket, ticketText, binding), http.RequestAborted);
        if (!accepted.Succeeded)
        {
            return ApiResults.Error(accepted.Error!);
        }

        try
        {
            var account = accepted.Account!;
            var tenant = await memberships.ReadAsync(account.Id, CancellationToken.None)
                ?? throw new InvalidOperationException("The account that was just made has no membership.");
            return ProviderSignIn.Session(http, account, tenant, ticket.Provider, accepted.Transaction!, tokens, clock);
        }
        catch
        {
            await accepted.Transaction!.DisposeAsync();
            throw;
        }
    }
}
```

and in its summary replace `A ticket of the purpose <c>reauth</c> is never good here.` with
`An invitation ticket accepts its invitation and signs in; a ticket of the purpose <c>reauth</c> is never good here.`

- [ ] **Step 5: The description.** In `src/Auth.Server/Api/ProviderRoutes.cs`, replace

```csharp
                + "expired, of the purpose `reauth` or without its `auth_pt` is `400 invalid_ticket`.")
```

with

```csharp
                + "expired, of the purpose `reauth` or without its `auth_pt` is `400 invalid_ticket`. A ticket of an invitation (`invite`) accepts it "
                + "and signs in, with the account made without a password: the invitation unknown, used, expired, cancelled or replaced is "
                + "`400 invalid_token`; the ticket's address missing, unverified or not the invited one `403 address_mismatch`; a company member's address "
                + "`409 already_member`, a private account's `409 private_account_exists`, an identity linked already `409 provider_already_linked`; the "
                + "invitation stays usable after each of them.")
```

and replace

```csharp
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidTicket)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.NoMembership, TenancyErrors.NoEmail, TenancyErrors.NoAccount)
            .ProducesError(StatusCodes.Status409Conflict, TenancyErrors.AlreadyRegistered);
```

with

```csharp
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest, TenancyErrors.InvalidTicket, TenancyErrors.InvalidToken)
            .ProducesError(
                StatusCodes.Status403Forbidden, TenancyErrors.NoMembership, TenancyErrors.NoEmail, TenancyErrors.NoAccount, TenancyErrors.AddressMismatch)
            .ProducesError(
                StatusCodes.Status409Conflict, TenancyErrors.AlreadyRegistered, TenancyErrors.AlreadyMember, TenancyErrors.PrivateAccountExists,
                TenancyErrors.ProviderAlreadyLinked);
```

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ProviderInvitationTests -class Auth.IntegrationTests.ProviderExchangeTests -class Auth.IntegrationTests.InviteAcceptTests -class Auth.IntegrationTests.PrivateInvitationTests -class Auth.IntegrationTests.PrivateAccountRaceTests -class Auth.IntegrationTests.OpenApiTests`
passes.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): an invitation accepted through a provider signs the person in`.

---

### Task 12: `DELETE /auth/account` with `provider_ticket`, `/auth/me`'s new members, "forgot password" without a password

**Files:**
- Modify: `src/Auth.Server/Api/AccountDeletionEndpoint.cs`, `src/Auth.Server/Tenancy/Contracts.cs`, `src/Auth.Server/Api/OrgEndpoints.cs`, `src/Auth.Server/Api/TenancyEndpoints.cs`
- Modify (tests): `tests/Auth.IntegrationTests/Infrastructure/ProviderApi.cs`, `tests/Auth.IntegrationTests/Infrastructure/ProviderTestBase.cs`, `tests/Auth.IntegrationTests/OrgEndpointTests.cs`, `tests/Auth.IntegrationTests/OpenApiTests.cs`, `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/AccountDeletionProviderTests.cs`, `tests/Auth.IntegrationTests/ProviderPasswordResetTests.cs`, `tests/Auth.IntegrationTests/MeProvidersTests.cs`

**Interfaces:**
- Consumes: `ProviderTickets.ConsumeAsync`, `ProviderLogins.IsLoginOfAsync`, `ProviderBindingCookie` (Tasks 1, 7, 8), `AccountDeletion`, `AddressLock`, `JsonObjectBody.ReadObjectAsync`, `JsonFields`, `UserManager.GetLoginsAsync`, `ProviderApi.Finish` (Task 10).
- Produces: `DeleteAccountRequest(string? Password = null, string? ProviderTicket = null)`; `MeResponse(..., bool HasPassword, string[] LinkedProviders)`; test side `ProviderApi.DeleteAccount(HttpClient, string accessToken, object body, string? binding)`, `ProviderApi.DeleteAccountRaw(HttpClient, string accessToken, string body)`, `ProviderTestBase.PrivateThroughProviderAsync(string provider, FakeSignIn person)` returning `(Guid UserId, SessionApi.Session Session)`.

**Contract counts:** `OpenApiTests`: `Each_endpoint_that_reads_a_body_describes_it_in_snake_case` reads `DELETE /auth/account` as
`password,provider_ticket`; `TicketEndpoints` gains `DELETE /auth/account`; `The_responses_are_described_in_snake_case` reads `MeResponse` with
`has_password` and `linked_providers`; the error codes test names the three new `403` codes. `OpenApiHardeningTests.The_account_deletion_is_described_with_its_body_its_answers_and_the_cookie_it_clears`:
the body is `password`, `provider_ticket`, the `403` names the new codes. `OrgEndpointTests.Me_returns_the_callers_current_data_from_the_database`:
the members of `/auth/me` gain `has_password` and `linked_providers`.

**Decisions the plan takes here:**
- `provider_ticket` is "a string", as the spec says: a blank one is a string, so it is a ticket that is not live, `403 reauth_failed` (with
  `account.delete_refused`), and only a member of another type is `400 invalid_request`. The password keeps its rule (not blank, no NUL).
- A refused ticket rolls the deletion's transaction back, so the ticket is as it was (unused if it was live), and only `account.delete_refused`
  is written (alone, as for a wrong password).
- Every `204` of the endpoint clears `auth_pt` beside the refresh cookie (the spec's last line of the deletion; for a password deletion it is
  harmless).
- `provider.reauthenticated` is staged before the deletion's own rows, in the same save.

- [ ] **Step 1: Write the failing tests.**

In `tests/Auth.IntegrationTests/Infrastructure/ProviderApi.cs`:

(a) Add `using System.Net.Http.Headers;` after `using System.Net.Http.Json;`.

(b) Replace

```csharp
    /// <summary>A request that used the ticket up clears <c>auth_pt</c>: emptied, <c>Max-Age=0</c>, with its attributes.</summary>
```

with

```csharp
    /// <summary><c>DELETE /auth/account</c> with the bearer token, a JSON body, and the binding cookie when given.</summary>
    public static async Task<HttpResponseMessage> DeleteAccount(HttpClient client, string accessToken, object body, string? binding)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/auth/account") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (binding is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"{BindingCookie}={binding}");
        }

        return await client.SendAsync(request);
    }

    /// <summary><c>DELETE /auth/account</c> with a body as it is.</summary>
    public static async Task<HttpResponseMessage> DeleteAccountRaw(HttpClient client, string accessToken, string body)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/auth/account") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    /// <summary>A request that used the ticket up clears <c>auth_pt</c>: emptied, <c>Max-Age=0</c>, with its attributes.</summary>
```

In `tests/Auth.IntegrationTests/Infrastructure/ProviderTestBase.cs`, replace

```csharp
    /// <summary>The tickets in the table.</summary>
```

with

```csharp
    /// <summary>A private account made through a provider as the finish makes it (no password), and its session.</summary>
    protected async Task<(Guid UserId, SessionApi.Session Session)> PrivateThroughProviderAsync(string provider, FakeSignIn person)
    {
        var ticket = await TicketAsync(provider, person);
        using var finished = await ProviderApi.Finish(Client, ticket, "private");
        var session = await SessionApi.ReadSessionAsync(finished);
        return (Guid.Parse(AccessTokens.Text(session.AccessToken, "sub")), session);
    }

    /// <summary>The tickets in the table.</summary>
```

`tests/Auth.IntegrationTests/AccountDeletionProviderTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>DELETE /auth/account</c> with a provider confirmation (criteria 23 and 24).</summary>
public sealed class AccountDeletionProviderTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;
    private const string PersonalKey = FakePeople.PersonalTenant + ":" + FakePeople.PersonalObjectId;

    private Task<int> UsersAsync(Guid userId) => InDbAsync(db => db.Users.CountAsync(u => u.Id == userId, TestContext.Current.CancellationToken));

    private Task<int> StreaksAsync() => InDbAsync(db => db.LoginStreaks.CountAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_private_account_without_a_password_deletes_itself_with_a_reauth_ticket()   // criterion 23
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        await LinkAsync(userId, "microsoft", PersonalKey);   // a second login: the deletion removes both (spec 0011 → To verify)
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
        var cancellation = TestContext.Current.CancellationToken;

        using var response = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal(TimeSpan.Zero, SessionApi.RefreshCookieOf(response).MaxAge);
        ProviderApi.AssertBindingCleared(response);
        Assert.Equal(0, await UsersAsync(userId));
        Assert.Equal(0, await InDbAsync(db => db.UserLogins.CountAsync(l => l.UserId == userId, cancellation)));
        Assert.Equal(0, await TicketCountAsync());
        var reauthenticated = await SingleAsync(AuditKinds.ProviderReauthenticated);
        Assert.Equal(userId, reauthenticated.ActorUserId);
        Assert.Equal(userId, reauthenticated.SubjectUserId);
        Assert.Equal(Ola, reauthenticated.SubjectEmail);
        Assert.NotNull(reauthenticated.OrgId);
        Assert.Equal(PersonalSpaces.Name, reauthenticated.OrgName);
        Assert.Equal(["provider"], AuditApi.DetailNames(reauthenticated));
        Assert.Equal("google", AuditApi.Text(reauthenticated, "provider"));
        Assert.Equal("self", AuditApi.Text(await SingleAsync(AuditKinds.AccountDeleted), "reason"));
        using var refresh = await SessionApi.Refresh(Client, session.RefreshToken);
        await SessionApi.AssertInvalidGrantAsync(refresh);
    }

    public static TheoryData<string> BadBodies => new()
    {
        "{}",
        """{"password":"Another-Passw0rd","provider_ticket":"x"}""",
        """{"provider_ticket":5}""",
        """{"provider_ticket":null}""",
        """{"provider_ticket":["x"]}""",
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task Both_members_neither_or_one_of_the_wrong_type_is_400_invalid_request(string body)   // criterion 24
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        using var response = await ProviderApi.DeleteAccountRaw(Client, session.AccessToken, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
        Assert.Equal(1, await UsersAsync(userId));
    }

    [Theory]
    [InlineData("""{"provider_ticket":""}""")]
    [InlineData("""{"provider_ticket":"   "}""")]
    public async Task A_blank_provider_ticket_is_a_string_and_so_403_reauth_failed(string body)   // criterion 24: provider_ticket is "a string"
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        using var response = await ProviderApi.DeleteAccountRaw(Client, session.AccessToken, body);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await UsersAsync(userId));
        Assert.Equal("reauth_failed", AuditApi.Text(await SingleAsync(AuditKinds.AccountDeleteRefused), "reason"));
    }

    [Fact]
    public async Task An_account_with_a_password_and_a_ticket_is_403_password_required_and_nothing_is_counted()   // criterion 24
    {
        var (userId, _) = await CreatePrivateAccountAsync(Ola);
        await LinkAsync(userId, "google", FakePeople.GoogleSubject);
        var session = await SessionApi.LoginAsync(Client, Ola, UserPassword);
        var reauth = await TicketAsync("google", GooglePerson(), "?purpose=reauth");

        using var response = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "password_required");
        Assert.Equal(0, await StreaksAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
        Assert.Equal(1, await TicketCountAsync());
        Assert.Equal(1, await UsersAsync(userId));
    }

    [Fact]
    public async Task An_account_without_a_password_and_a_password_is_403_reauth_required_and_nothing_is_counted()   // criterion 24
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        using var response = await ProviderApi.DeleteAccount(Client, session.AccessToken, new { password = "Any-Passw0rd-1" }, null);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_required");
        Assert.Equal(0, await StreaksAsync());
        Assert.Empty(await AuditAsync(AuditKinds.AccountDeleteRefused));
        Assert.Equal(1, await UsersAsync(userId));
    }

    [Theory]
    [InlineData("sign_in")]
    [InlineData("expired")]
    [InlineData("used")]
    [InlineData("no-binding")]
    [InlineData("another-account")]
    public async Task A_ticket_that_is_not_a_live_reauth_ticket_of_this_account_is_403_reauth_failed(string how)   // criterion 24
    {
        var (userId, session) = await PrivateThroughProviderAsync("google", GooglePerson());
        ProviderApi.Ticket ticket;
        switch (how)
        {
            case "sign_in":
                ticket = await TicketAsync("google", GooglePerson());
                break;
            case "expired":
                ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
                Clock.Advance(TimeSpan.FromMinutes(5));
                break;
            case "used":
                // Another private person deletes their account with their own re-authentication; this account then shows that used ticket.
                var (_, other) = await PrivateThroughProviderAsync("microsoft", PersonalMicrosoft());
                ticket = await TicketAsync("microsoft", PersonalMicrosoft(), "?purpose=reauth");
                using (var deleted = await ProviderApi.DeleteAccount(Client, other.AccessToken, new { provider_ticket = ticket.Value }, ticket.Binding))
                {
                    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
                }

                break;
            case "another-account":
                await PrivateThroughProviderAsync("microsoft", PersonalMicrosoft());
                ticket = await TicketAsync("microsoft", PersonalMicrosoft(), "?purpose=reauth");   // an identity linked to the other account
                break;
            default:
                ticket = await TicketAsync("google", GooglePerson(), "?purpose=reauth");
                break;
        }

        using var response = await ProviderApi.DeleteAccount(
            Client, session.AccessToken, new { provider_ticket = ticket.Value }, how == "no-binding" ? null : ticket.Binding);

        await TenancyApi.AssertErrorAsync(response, HttpStatusCode.Forbidden, "reauth_failed");
        Assert.Equal(1, await UsersAsync(userId));
        var refusal = await SingleAsync(AuditKinds.AccountDeleteRefused);
        Assert.Equal("reauth_failed", AuditApi.Text(refusal, "reason"));
        Assert.Equal(userId, refusal.SubjectUserId);
        Assert.Equal(0, await StreaksAsync());   // the lockout is not consulted
    }
}
```

`tests/Auth.IntegrationTests/ProviderPasswordResetTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → Accounts without a password: "forgot password" sets one (criterion 25, Decision 15).</summary>
public sealed class ProviderPasswordResetTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;

    [Fact]
    public async Task Forgot_password_sets_a_password_for_an_account_without_one_and_both_then_sign_in()   // criterion 25
    {
        var (userId, _) = await PrivateThroughProviderAsync("google", GooglePerson());

        using (var forgot = await AccountApi.Forgot(Client, Ola))
        {
            await AccountApi.AssertEmptyAsync(forgot, HttpStatusCode.Accepted);
        }

        await DispatchAsync();
        using (var reset = await AccountApi.Reset(Client, TokenIn(Mail.Sent.Last(m => m.To == Ola)), "Chosen-Passw0rd"))
        {
            await AccountApi.AssertEmptyAsync(reset, HttpStatusCode.NoContent);
        }

        var password = await SessionApi.LoginAsync(Client, Ola, "Chosen-Passw0rd");
        Assert.Equal(userId.ToString(), AccessTokens.Text(password.AccessToken, "sub"));
        var ticket = await TicketAsync("google", GooglePerson());
        using (var exchanged = await ProviderApi.Exchange(Client, ticket))
        {
            Assert.Equal(userId.ToString(), AccessTokens.Text((await SessionApi.ReadSessionAsync(exchanged)).AccessToken, "sub"));
        }

        using var me = await TenancyApi.Get(Client, "/auth/me", password.AccessToken);
        var body = await TenancyApi.ReadOkAsync(me);
        Assert.True(body.GetProperty("has_password").GetBoolean());
        Assert.Equal(["google"], body.GetProperty("linked_providers").EnumerateArray().Select(e => e.GetString()));
    }
}
```

`tests/Auth.IntegrationTests/MeProvidersTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Auth.FakeOidc;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011 → <c>GET /auth/me</c>: <c>has_password</c> and <c>linked_providers</c> (criterion 26).</summary>
public sealed class MeProvidersTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private const string Ola = FakePeople.GoogleEmail;
    private static readonly string[] SessionClaims = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"];

    private async Task<JsonElement> MeAsync(string accessToken)
    {
        using var response = await TenancyApi.Get(Client, "/auth/me", accessToken);
        return await TenancyApi.ReadOkAsync(response);
    }

    private static string[] Providers(JsonElement me) => [.. me.GetProperty("linked_providers").EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public async Task An_account_with_a_password_and_no_provider_has_a_password_and_no_providers()   // criterion 26
    {
        var me = await MeAsync((await SessionApi.LoginAsync(Client, Factory)).AccessToken);

        Assert.True(me.GetProperty("has_password").GetBoolean());
        Assert.Empty(Providers(me));
    }

    [Fact]
    public async Task An_account_with_a_password_and_both_providers_lists_them_sorted()   // criterion 26
    {
        var user = await CreateUserAsync(Ola, confirmed: true);
        await LinkAsync(user.Id, "microsoft", FakePeople.PersonalTenant + ":" + FakePeople.PersonalObjectId);
        await LinkAsync(user.Id, "google", FakePeople.GoogleSubject);

        var me = await MeAsync((await SessionApi.LoginAsync(Client, Ola, UserPassword)).AccessToken);

        Assert.True(me.GetProperty("has_password").GetBoolean());
        Assert.Equal(["google", "microsoft"], Providers(me));
    }

    [Fact]
    public async Task An_account_with_a_provider_only_has_no_password()   // criterion 26
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        var me = await MeAsync(session.AccessToken);

        Assert.False(me.GetProperty("has_password").GetBoolean());
        Assert.Equal(["google"], Providers(me));
    }

    [Fact]
    public async Task A_password_login_for_an_account_without_a_password_is_the_401_of_an_unknown_address()   // criterion 26
    {
        await PrivateThroughProviderAsync("google", GooglePerson());

        using var login = await LoginApi.Login(Client, Ola, "Any-Passw0rd-1");

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal("""{"error":"invalid_credentials"}""", await login.Content.ReadAsStringAsync());
        Assert.Equal("unknown_address", AuditApi.Text(await SingleAsync(AuditKinds.LoginFailed), "reason"));
    }

    [Fact]
    public async Task A_provider_sign_in_issues_exactly_the_claims_of_a_password_sign_in()   // spec 0011 → /auth/me: the access token gains no claim
    {
        var (_, session) = await PrivateThroughProviderAsync("google", GooglePerson());

        Assert.Equal(SessionClaims, AccessTokens.Payload(session.AccessToken).Select(p => p.Key).Order(StringComparer.Ordinal));
    }
}
```

Change the contract tests:

(a) `tests/Auth.IntegrationTests/OrgEndpointTests.cs`: replace
`        Assert.Equal(["email", "org_id", "org_kind", "org_name", "permissions", "roles", "sub"], me.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));` with

```csharp
        Assert.Equal(
            ["email", "has_password", "linked_providers", "org_id", "org_kind", "org_name", "permissions", "roles", "sub"],
            me.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.True(me.GetProperty("has_password").GetBoolean());
        Assert.Equal(0, me.GetProperty("linked_providers").GetArrayLength());
```

(b) `tests/Auth.IntegrationTests/OpenApiTests.cs`:
- replace `    [InlineData("DELETE /auth/account", "password")]` with `    [InlineData("DELETE /auth/account", "password,provider_ticket")]`;
- `TicketEndpoints` becomes `["POST /auth/providers/exchange", "POST /auth/providers/link", "POST /auth/providers/finish", "DELETE /auth/account"]`;
- replace `        Assert.Equal(["email", "org_id", "org_kind", "org_name", "permissions", "roles", "sub"], Properties("MeResponse"));` with
  `        Assert.Equal(["email", "has_password", "linked_providers", "org_id", "org_kind", "org_name", "permissions", "roles", "sub"], Properties("MeResponse"));`;
- after `        Assert.All(["forbidden", "permissions_changed", "wrong_password"], code => Assert.Contains(code, Description(operations["DELETE /auth/account"], "403")));` add
  `        Assert.All(["password_required", "reauth_failed", "reauth_required"], code => Assert.Contains(code, Description(operations["DELETE /auth/account"], "403")));`.

(c) `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`, in `The_account_deletion_is_described_with_its_body_its_answers_and_the_cookie_it_clears`:
replace `        Assert.Equal(["password"], schema["properties"]!.AsObject().Select(p => p.Key));` with
`        Assert.Equal(["password", "provider_ticket"], schema["properties"]!.AsObject().Select(p => p.Key));`, and after
`        Assert.All(["forbidden", "permissions_changed", "wrong_password"], code => Assert.Contains(code, Description(operation, "403")));` add
`        Assert.All(["password_required", "reauth_failed", "reauth_required"], code => Assert.Contains(code, Description(operation, "403")));   // spec 0011`
and after `        Assert.Contains("auth_rt", operation["responses"]!["204"]!["headers"]!["Set-Cookie"]!["description"]!.GetValue<string>());` add
`        Assert.Contains("auth_pt", operation["responses"]!["204"]!["headers"]!["Set-Cookie"]!["description"]!.GetValue<string>());   // spec 0011`.

- [ ] **Step 2: See them fail.** Build, then run `AccountDeletionProviderTests` and `MeProvidersTests`: a body with `provider_ticket` is `400`; `/auth/me` has
no `has_password`.

- [ ] **Step 3: The contracts.** In `src/Auth.Server/Tenancy/Contracts.cs`, replace

```csharp
/// <summary>
/// Response of <c>GET /auth/me</c>: the caller as the database says now. <c>org_kind</c> is <c>organization</c> or <c>personal</c> (spec
/// 0010); a private account's space is named <c>private</c> and its role is <c>private</c>.
/// </summary>
public sealed record MeResponse(Guid Sub, string Email, Guid OrgId, string OrgName, string OrgKind, string[] Roles, string[] Permissions);
```

with

```csharp
/// <summary>
/// Response of <c>GET /auth/me</c>: the caller as the database says now. <c>org_kind</c> is <c>organization</c> or <c>personal</c> (spec
/// 0010); a private account's space is named <c>private</c> and its role is <c>private</c>. <c>has_password</c>: the account has a password;
/// <c>linked_providers</c>: the providers of its logins, sorted ordinally (spec 0011).
/// </summary>
public sealed record MeResponse(
    Guid Sub, string Email, Guid OrgId, string OrgName, string OrgKind, string[] Roles, string[] Permissions, bool HasPassword, string[] LinkedProviders);
```

and replace

```csharp
/// <summary>Request of <c>DELETE /auth/account</c> (spec 0010): the caller's own password.</summary>
public sealed record DeleteAccountRequest(string Password);
```

with

```csharp
/// <summary>
/// Request of <c>DELETE /auth/account</c>: exactly one of the caller's own password (spec 0010) and a re-authentication ticket of a provider
/// (spec 0011); the account decides which it needs.
/// </summary>
public sealed record DeleteAccountRequest(string? Password = null, string? ProviderTicket = null);
```

In `src/Auth.Server/Api/OrgEndpoints.cs`, replace

```csharp
        return ApiResults.Ok(new MeResponse(
            caller.UserId, user.Email, caller.CompanyId, caller.CompanyName, SpaceKinds.NameOf(caller.Kind), [caller.RoleName], [.. caller.Permissions]));
```

with

```csharp
        // Spec 0011: whether the account has a password, and the providers it signs in with, sorted ordinally.
        var linked = (await users.GetLoginsAsync(user)).Select(l => l.LoginProvider).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return ApiResults.Ok(new MeResponse(
            caller.UserId, user.Email, caller.CompanyId, caller.CompanyName, SpaceKinds.NameOf(caller.Kind), [caller.RoleName], [.. caller.Permissions],
            user.PasswordHash is not null, [.. linked]));
```

- [ ] **Step 4: The deletion.** In `src/Auth.Server/Api/AccountDeletionEndpoint.cs`:

(a) Add `using Auth.Server.Email;` after `using Auth.Server.Audit;`, `using Auth.Server.Providers;` after `using Auth.Server.Lockout;`, and
`using Microsoft.EntityFrameworkCore.Storage;` after `using Microsoft.EntityFrameworkCore;`.

(b) Replace `    private static readonly string[] Fields = ["password"];` with

```csharp
    private const string PasswordField = "password";
    private const string TicketField = "provider_ticket";
```

(c) Replace

```csharp
        AccountDeletion deletion, AuditLog audit)
    {
```

with

```csharp
        AccountDeletion deletion, AuditLog audit, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
```

(d) Replace

```csharp
        var fields = await JsonObjectBody.ReadStringsAsync(http.Request, Fields, http.RequestAborted);
        if (fields is null || fields[0].Contains('\0'))
        {
            return ApiResults.InvalidRequest();
        }
```

with

```csharp
        // Spec 0011: exactly one of the two confirmations: a password that is a non-blank string with no NUL, or a provider ticket that is a string
        // (a blank one is a ticket that is not live: reauth_failed). Which one the account needs is judged under the lock, below.
        var body = await JsonObjectBody.ReadObjectAsync(http.Request, http.RequestAborted);
        if (body is not { } fields || fields.Has(PasswordField) == fields.Has(TicketField))
        {
            return ApiResults.InvalidRequest();
        }

        string? password = null;
        string? ticketText = null;
        var readable = fields.Has(PasswordField)
            ? fields.TryString(PasswordField, out password) && !password.Contains('\0')
            : fields.TryAnyString(TicketField, out ticketText);
        if (!readable)
        {
            return ApiResults.InvalidRequest();
        }
```

(e) Replace

```csharp
        if (stored is null)
        {
            return ApiResults.Error(TenancyErrors.PermissionsChanged);
        }

        var identifier = LoginIdentifier.HashOf(normalized);
```

with

```csharp
        if (stored is null)
        {
            return ApiResults.Error(TenancyErrors.PermissionsChanged);
        }

        // Spec 0011: the confirmation must fit the account, judged under the lock and before anything is counted; nothing is written.
        if (stored.PasswordHash is not null && ticketText is not null)
        {
            return ApiResults.Error(TenancyErrors.PasswordRequired);
        }

        if (stored.PasswordHash is null && password is not null)
        {
            return ApiResults.Error(TenancyErrors.ReauthRequired);
        }

        if (ticketText is not null)
        {
            return await DeleteWithTicketAsync(http, db, transaction, deletion, audit, clock, caller, email, userId, ticketText);
        }

        var identifier = LoginIdentifier.HashOf(normalized);
```

(f) Replace `        if (!PasswordIsRight(users, stored.PasswordHash, fields[0]))` with `        if (!PasswordIsRight(users, stored.PasswordHash, password!))`.

(g) Replace

```csharp
        RefreshCookie.Clear(http.Response);
        return ApiResults.NoContent();
    }
```

with

```csharp
        RefreshCookie.Clear(http.Response);
        ProviderBindingCookie.Clear(http.Response);
        return ApiResults.NoContent();
    }

    /// <summary>
    /// The provider confirmation of spec 0011, under the address lock of <paramref name="transaction"/>: a live <c>reauth</c> ticket of this browser
    /// whose identity is a login of this account, used up by the deletion. Anything else is <c>403 reauth_failed</c> with
    /// <c>account.delete_refused</c> (<c>reauth_failed</c>), and the transaction is rolled back, the ticket as it was. The lockout is not consulted:
    /// 32 random bytes cannot be guessed (Settled 20). The deletion is that of spec 0010, with <c>provider.reauthenticated</c> in its transaction.
    /// </summary>
    private static async Task<IResult> DeleteWithTicketAsync(
        HttpContext http, AuthDbContext db, IDbContextTransaction transaction, AccountDeletion deletion, AuditLog audit, TimeProvider clock,
        TenantContext caller, string email, Guid userId, string ticketText)
    {
        var ticket = ProviderBindingCookie.TryRead(http.Request, out var binding)
            ? await ProviderTickets.ConsumeAsync(db, ticketText, binding, ProviderTicketPurpose.Reauth, StorableTime.Now(clock), CancellationToken.None)
            : null;
        if (ticket is null || !await ProviderLogins.IsLoginOfAsync(db, userId, ticket.Provider, ticket.IdentityKey, CancellationToken.None))
        {
            await RecordRefusalAsync(audit, caller, email, "reauth_failed");
            return ApiResults.Error(TenancyErrors.ReauthFailed);
        }

        audit.Stage(new AuditEntry
        {
            Kind = AuditKinds.ProviderReauthenticated,
            ActorUserId = userId,
            SubjectUserId = userId,
            SubjectEmail = email,
            OrgId = caller.CompanyId,
            OrgName = caller.CompanyName,
            Details = new Dictionary<string, object?> { ["provider"] = ticket.Provider },
        });
        await deletion.DeleteAsync(userId, AccountDeletionReason.Self, actor: null, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        RefreshCookie.Clear(http.Response);
        ProviderBindingCookie.Clear(http.Response);
        return ApiResults.NoContent();
    }
```

(h) In the class summary, replace `/// <c>DELETE /auth/account</c> (spec 0010): a private person deletes their own account.` with
`/// <c>DELETE /auth/account</c> (spec 0010): a private person deletes their own account, with the password or, for an account without one, a re-authentication ticket of a provider (spec 0011: the confirmation must fit the account, judged under the lock before anything is counted).`
(one line, as the summary's other lines).

- [ ] **Step 5: The description.** In `src/Auth.Server/Api/TenancyEndpoints.cs`:

(a) Add `using Auth.Server.Providers;` after `using Auth.Server.Sessions;`.

(b) Replace

```csharp
                + "the queued mails, the pending sign-up and the login streak; the audit rows stay for their retention. Needs the caller's password. "
```

with

```csharp
                + "the queued mails, the pending sign-up and the login streak; the audit rows stay for their retention. Needs the caller's "
                + "confirmation: the password, or for an account without one a provider ticket (below). "
```

(c) Replace

```csharp
                + "(company accounts are managed by their admins). A wrong password counts in the login streak as a failed login does; while the "
                + "identifier is locked the answer is `429 too_many_attempts`.")
            .ReadsJson<DeleteAccountRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .SetsRefreshCookie(
                StatusCodes.Status204NoContent, $"`{RefreshCookie.Name}` emptied, with `Max-Age=0`: the browser drops the cookie, as at a logout.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(StatusCodes.Status403Forbidden, TenancyErrors.WrongPassword)
```

with

```csharp
                + "(company accounts are managed by their admins). A wrong password counts in the login streak as a failed login does; while the "
                + "identifier is locked the answer is `429 too_many_attempts`. The body holds exactly one of `password` and `provider_ticket` (spec "
                + "0011; both or neither is `400 invalid_request`): an account with a password needs it (`403 password_required` for a ticket), an "
                + "account without one confirms with a provider, by a ticket of a round trip with `purpose=reauth` and the browser's `auth_pt` cookie "
                + "(`403 reauth_required` for a password); a ticket that is unknown, used, expired (5 minutes), of another purpose, without its "
                + "`auth_pt` or of an identity that is not a login of this account is `403 reauth_failed`. Neither of the first two is counted.")
            .ReadsJson<DeleteAccountRequest>()
            .ReadsCookie(ProviderBindingCookie.Name, "With `provider_ticket`: the binding of the ticket, set by the callback that made it.")
            .Produces(StatusCodes.Status204NoContent)
            .SetsRefreshCookie(
                StatusCodes.Status204NoContent,
                $"`{RefreshCookie.Name}` emptied, with `Max-Age=0`: the browser drops the cookie, as at a logout; `{ProviderBindingCookie.Name}` emptied too.")
            .ProducesError(StatusCodes.Status400BadRequest, TenancyErrors.InvalidRequest)
            .ProducesError(
                StatusCodes.Status403Forbidden, TenancyErrors.WrongPassword, TenancyErrors.PasswordRequired, TenancyErrors.ReauthRequired,
                TenancyErrors.ReauthFailed)
```

- [ ] **Step 6: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.AccountDeletionProviderTests -class Auth.IntegrationTests.ProviderPasswordResetTests -class Auth.IntegrationTests.MeProvidersTests -class Auth.IntegrationTests.AccountDeletionEndpointTests -class Auth.IntegrationTests.AccountDeletionTests -class Auth.IntegrationTests.PrivateAccountRaceTests -class Auth.IntegrationTests.OrgEndpointTests -class Auth.IntegrationTests.PrivateAccountClaimsTests -class Auth.IntegrationTests.ResetPasswordTests -class Auth.IntegrationTests.OpenApiTests -class Auth.IntegrationTests.OpenApiHardeningTests`
passes (the tests of spec 0010 for a password unchanged).

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): a private account without a password confirms its deletion with the provider`.

---

### Task 13: The per-IP limits, the audit flows, the last OpenAPI checks, the upgrade from spec 0010

**Files:**
- Modify: `src/Auth.Server/RateLimiting/RatePolicy.cs`
- Modify (tests): `tests/Auth.IntegrationTests/RatePolicyTests.cs`, `tests/Auth.IntegrationTests/RateLimitMiddlewareTests.cs`, `tests/Auth.IntegrationTests/JsonCharsetTests.cs`, `tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`
- Create (tests): `tests/Auth.IntegrationTests/AuditProviderFlowsTests.cs`, `tests/Auth.IntegrationTests/UpgradeFromSpec0010Tests.cs`

**Interfaces:**
- Consumes: everything of Tasks 1 to 12 (no new production type).
- Produces: `RatePolicies.Classify` with the five provider paths on `Login`.

**Contract counts:** `RatePolicyTests.A_request_counts_against_one_policy_by_its_method_and_path` gains the provider rows;
`RateLimitMiddlewareTests.Each_policy_refuses_at_its_own_number` gains the five paths; `JsonCharsetTests.A_body_declared_utf_16_is_a_415_on_every_endpoint_that_reads_one_and_before_authentication`
gains the three POSTs.

**Decisions the plan takes here:**
- A GET of `/auth/providers/{anything}/start` or `/callback` counts against `login` whatever the name: the limit is by path, before the name is
  judged, as every policy is; any other method there is `general`.
- `AuditProviderFlowsTests` runs every provider flow on one host and reads every row they wrote; `login.locked` with `details.provider` is read by
  `ProviderLinkLockoutTests` (ten failures are its own test).

- [ ] **Step 1: Write the failing tests.**

`tests/Auth.IntegrationTests/RatePolicyTests.cs`:
- after `    [InlineData("DELETE", "/auth/account", RatePolicy.General)]   // spec 0010 → Limits, as DELETE /auth/org` add

```csharp
    [InlineData("GET", "/auth/providers/google/start", RatePolicy.Login)]   // spec 0011 → Limits: each is a step of a sign-in
    [InlineData("GET", "/auth/providers/microsoft/callback", RatePolicy.Login)]
    [InlineData("GET", "/auth/providers/github/start", RatePolicy.Login)]   // by path, before the name is judged
    [InlineData("POST", "/auth/providers/exchange", RatePolicy.Login)]
    [InlineData("POST", "/auth/providers/link", RatePolicy.Login)]
    [InlineData("POST", "/auth/providers/finish", RatePolicy.Login)]
    [InlineData("GET", "/auth/providers/google/start/more", RatePolicy.General)]
    [InlineData("GET", "/auth/providers", RatePolicy.General)]
    [InlineData("GET", "/auth/options", RatePolicy.General)]
```

- after `    [InlineData("DELETE", "/auth/invites/accept")]` add

```csharp
    [InlineData("POST", "/auth/providers/google/start")]   // spec 0011: the round trip's policy is for the GET
    [InlineData("HEAD", "/auth/providers/google/callback")]
    [InlineData("GET", "/auth/providers/exchange")]
```

- after the method `The_path_is_compared_without_regard_to_case_or_a_trailing_slash` add

```csharp

    [Theory]
    [InlineData("GET", "/AUTH/Providers/Google/START")]
    [InlineData("GET", "/auth/providers/google/callback/")]
    [InlineData("POST", "/auth/providers/Exchange/")]
    public void The_provider_paths_are_compared_without_regard_to_case_or_a_trailing_slash(string method, string path) =>   // spec 0011 → Limits
        Assert.Equal(RatePolicy.Login, RatePolicies.Classify(method, new PathString(path)));
```

`tests/Auth.IntegrationTests/RateLimitMiddlewareTests.cs`:
- after `    [InlineData("POST", "/auth/invites/accept", 3)]` (in `Each_policy_refuses_at_its_own_number`) add

```csharp
    [InlineData("GET", "/auth/providers/google/start", 3)]   // spec 0011 → Limits
    [InlineData("GET", "/auth/providers/google/callback", 3)]
    [InlineData("POST", "/auth/providers/exchange", 3)]
    [InlineData("POST", "/auth/providers/link", 3)]
    [InlineData("POST", "/auth/providers/finish", 3)]
```

- after the method `A_request_counts_against_one_policy_only` add

```csharp

    [Fact]
    public async Task The_provider_steps_and_the_login_share_one_limit()   // spec 0011 → Limits: one login bucket per address
    {
        using (var start = await Send(HttpMethod.Get, "/auth/providers/google/start"))
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, start.StatusCode);
        }

        using (var exchange = await Send(HttpMethod.Post, "/auth/providers/exchange"))
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, exchange.StatusCode);
        }

        using (var login = await UnknownLogin(1))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        }

        using var refused = await UnknownLogin(2);

        await RateLimitApi.AssertTooManyRequestsAsync(refused);
    }
```

`tests/Auth.IntegrationTests/JsonCharsetTests.cs`: after `    [InlineData("DELETE", "/auth/org/roles/" + SomeId)]` add

```csharp
    [InlineData("POST", "/auth/providers/exchange")]   // spec 0011, criterion 29
    [InlineData("POST", "/auth/providers/link")]
    [InlineData("POST", "/auth/providers/finish")]
```

`tests/Auth.IntegrationTests/OpenApiHardeningTests.cs`: after the method `The_account_deletion_is_described_with_its_body_its_answers_and_the_cookie_it_clears` add

```csharp

    [Fact]
    public async Task The_provider_operations_are_described_with_their_parameters_answers_and_cookies()   // spec 0011, criterion 29
    {
        var (_, operations) = await DescriptionAsync();

        var start = operations["GET /auth/providers/{name}/start"];
        var query = start["parameters"]!.AsArray().Where(p => p!["in"]!.GetValue<string>() == "query").Select(p => p!["name"]!.GetValue<string>());
        Assert.Equal(["invite", "purpose"], query.Order(StringComparer.Ordinal));
        Assert.NotNull(start["responses"]!["302"]!["headers"]!["Location"]);
        Assert.Contains("OpenIddict.Client.State", start["responses"]!["302"]!["headers"]!["Set-Cookie"]!["description"]!.GetValue<string>());

        var callback = operations["GET /auth/providers/{name}/callback"]["responses"]!["302"]!["headers"]!;
        Assert.All(["ticket", "purpose", "error"], member => Assert.Contains(member, callback["Location"]!["description"]!.GetValue<string>()));
        Assert.Contains("auth_pt", callback["Set-Cookie"]!["description"]!.GetValue<string>());

        foreach (var endpoint in new[] { "POST /auth/providers/exchange", "POST /auth/providers/link", "POST /auth/providers/finish" })
        {
            var operation = operations[endpoint];
            var cookie = Assert.Single(operation["parameters"]!.AsArray(), p => p!["in"]!.GetValue<string>() == "cookie")!;
            Assert.Equal("auth_pt", cookie["name"]!.GetValue<string>());
            var setCookie = operation["responses"]!["200"]!["headers"]!["Set-Cookie"]!["description"]!.GetValue<string>();
            Assert.Contains("auth_rt", setCookie);
            Assert.Contains("auth_pt", setCookie);
            Assert.Contains("too_many_requests", Description(operation, "429"));
            Assert.Contains("unsupported_media_type", Description(operation, "415"));
        }

        Assert.EndsWith(
            "ProviderExchangeResponse",
            operations["POST /auth/providers/exchange"]["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>());
    }
```

`tests/Auth.IntegrationTests/AuditProviderFlowsTests.cs`:

```csharp
using System.Net;
using Auth.FakeOidc;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Audit;
using Auth.Server.Tenancy;
using Microsoft.AspNetCore.WebUtilities;

namespace Auth.IntegrationTests;

/// <summary>
/// Spec 0011, criterion 28: the audit log knows 28 kinds; every row and every <c>details</c> member the provider flows add holds the fields of the
/// contract; no row holds a code, a state, an identity token, a ticket, a cookie or a password; a failed callback writes no row.
/// </summary>
public sealed class AuditProviderFlowsTests(PostgresFixture postgres, KeyMaterialFixture keys) : ProviderTestBase(postgres, keys)
{
    private readonly List<string> _secrets = [];

    /// <summary>A round trip that keeps every secret it saw: the state, the challenge, the code, the request-forgery cookie, the ticket, its binding.</summary>
    private async Task<ProviderApi.Ticket> TrackedTicketAsync(string provider, FakeSignIn person, string query = "")
    {
        var start = await StartAsync(provider, query);
        var back = await AuthorizeAsync(start, person);
        using var callback = await CallbackAsync(back, start.StateCookie);
        var ticket = ProviderApi.TicketOf(callback);
        var answer = QueryHelpers.ParseQuery(new Uri("https://app.example.com" + back).Query);
        _secrets.AddRange(
        [
            start.Query["state"], start.Query["code_challenge"], start.Query["nonce"], answer["code"].ToString(),
            start.StateCookieHeader.Value.Value!, ticket.Value, ticket.Binding,
        ]);
        return ticket;
    }

    private static bool Names(AuditEvent row, params string[] names) => AuditApi.DetailNames(row).SequenceEqual(names);

    [Fact]
    public void The_audit_log_knows_twenty_eight_kinds()   // criterion 28
    {
        Assert.Equal(28, AuditKinds.All.Count);
        Assert.Contains("provider.linked", AuditKinds.All);
        Assert.Contains("provider.reauthenticated", AuditKinds.All);
    }

    [Fact]
    public async Task Every_provider_flow_writes_the_rows_of_the_contract_and_no_secret()   // criterion 28
    {
        // A link, after a wrong password, and then a sign-in of the linked identity.
        var anna = await CreateUserAsync("anna@firma.test", confirmed: true);
        var linkTicket = await TrackedTicketAsync("google", GooglePerson(email: "anna@firma.test", subject: "800000000000000000008"));
        using (var wrong = await ProviderApi.Link(Client, linkTicket, WrongPassword))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using (var linked = await ProviderApi.Link(Client, linkTicket, UserPassword))
        {
            Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        }

        using (var signedIn = await ProviderApi.Exchange(Client, await TrackedTicketAsync("google", GooglePerson(email: "anna@firma.test", subject: "800000000000000000008"))))
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        }

        // A private finish, then a refused and an accepted re-authentication of its deletion.
        var finishTicket = await TrackedTicketAsync("microsoft", PersonalMicrosoft());
        using var finished = await ProviderApi.Finish(Client, finishTicket, "private");
        var ola = await SessionApi.ReadSessionAsync(finished);
        _secrets.AddRange([ola.AccessToken, ola.RefreshToken]);
        var signInTicket = await TrackedTicketAsync("microsoft", PersonalMicrosoft());
        using (var refused = await ProviderApi.DeleteAccount(Client, ola.AccessToken, new { provider_ticket = signInTicket.Value }, signInTicket.Binding))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        var reauth = await TrackedTicketAsync("microsoft", PersonalMicrosoft(), "?purpose=reauth");
        using (var deleted = await ProviderApi.DeleteAccount(Client, ola.AccessToken, new { provider_ticket = reauth.Value }, reauth.Binding))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        // An invitation accepted with Google.
        var company = await CreateCompanyAsync("Acme");
        var invite = await AddInviteAsync(company, "jan@firma.test", "user");
        await EnqueueInvitationAsync(invite, "jan@firma.test");
        await DispatchAsync();
        var inviteToken = TokenIn(Mail.Sent.Last(m => m.To == "jan@firma.test"));
        _secrets.Add(inviteToken);
        using (var accepted = await ProviderApi.Exchange(Client, await TrackedTicketAsync("google", GooglePerson(email: "jan@firma.test", subject: "900000000000000000009"), "?invite=" + inviteToken)))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        // A round trip that fails at the callback writes no row.
        var before = (await AuditAsync()).Count;
        var cancelled = await StartAsync("google");
        using (var callback = await CallbackAsync(await AuthorizeAsync(cancelled, new FakeSignIn(FakePeople.Cancel)), cancelled.StateCookie))
        {
            Assert.Equal("cancelled", ProviderApi.FragmentOf(callback)["error"]);
        }

        Assert.Equal(before, (await AuditAsync()).Count);

        // The rows.
        var providerLogins = (await AuditAsync(AuditKinds.LoginSucceeded)).Where(r => AuditApi.Text(r, "provider") is not null).ToList();
        Assert.Equal(4, providerLogins.Count);   // the link, the sign-in, the finish, the invitation
        Assert.All(providerLogins, row => Assert.True(Names(row, "provider")));

        var failed = Assert.Single(await AuditAsync(AuditKinds.LoginFailed));
        Assert.True(Names(failed, "provider", "reason"));
        Assert.Equal(anna.Id, failed.SubjectUserId);

        var link = await SingleAsync(AuditKinds.ProviderLinked);
        Assert.Equal((anna.Id, anna.Id, "anna@firma.test"), (link.ActorUserId!.Value, link.SubjectUserId!.Value, link.SubjectEmail));
        Assert.NotNull(link.OrgId);
        Assert.NotNull(link.OrgName);
        Assert.True(Names(link, "provider"));

        var completed = await SingleAsync(AuditKinds.SignupCompleted);
        Assert.True(Names(completed, "kind", "provider"));
        Assert.Equal("microsoft", AuditApi.Text(completed, "provider"));

        var refusal = await SingleAsync(AuditKinds.AccountDeleteRefused);
        Assert.Equal("reauth_failed", AuditApi.Text(refusal, "reason"));

        var reauthenticated = await SingleAsync(AuditKinds.ProviderReauthenticated);
        Assert.True(Names(reauthenticated, "provider"));
        Assert.Equal(reauthenticated.SubjectUserId, reauthenticated.ActorUserId);
        Assert.Equal(FakePeople.PersonalEmail, reauthenticated.SubjectEmail);
        Assert.NotNull(reauthenticated.OrgId);
        Assert.Equal(PersonalSpaces.Name, reauthenticated.OrgName);
        Assert.Equal("self", AuditApi.Text(await SingleAsync(AuditKinds.AccountDeleted), "reason"));

        var acceptance = await SingleAsync(AuditKinds.InviteAccepted);
        Assert.True(Names(acceptance, "provider", "role"));
        Assert.Equal(company, acceptance.OrgId);

        _secrets.AddRange(Fake.Server.IssuedIdentityTokens);
        await AssertNoSecretsAsync([.. _secrets, UserPassword, WrongPassword]);
    }
}
```

`tests/Auth.IntegrationTests/UpgradeFromSpec0010Tests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Auth.Infrastructure.Persistence;
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.IntegrationTests;

/// <summary>Spec 0011, criterion 30: an instance of spec 0010 upgrades without noticing.</summary>
public sealed class UpgradeFromSpec0010Tests(PostgresFixture postgres, KeyMaterialFixture keys)
{
    private const string LastMigrationOfSlice0010 = "20260410163512_AddSpaceKinds";
    private static readonly string[] LoginClaims = ["aud", "exp", "iat", "iss", "jti", "oi_tkn_id", "org_id", "org_kind", "permissions", "roles", "sub"];

    [Fact]
    public async Task A_database_of_slice_0010_takes_the_ticket_table_and_keeps_what_it_holds()   // criterion 30
    {
        await using var factory = new AuthAppFactory(postgres, keys);   // never built: only its connection string is used
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(factory.ConnectionString).UseOpenIddict().Options;
        var cancellation = TestContext.Current.CancellationToken;
        var company = Guid.NewGuid();

        await using (var old = new AuthDbContext(options))
        {
            await old.GetService<IMigrator>().MigrateAsync(LastMigrationOfSlice0010, cancellation);
            await old.Database.ExecuteSqlAsync(
                $"""INSERT INTO "Companies" ("Id", "Name", "Kind", "CreatedAt") VALUES ({company}, 'Toviq', 1, now())""", cancellation);
        }

        await using (var upgraded = new AuthDbContext(options))
        {
            await upgraded.Database.MigrateAsync(cancellation);

            Assert.Equal("Toviq", await upgraded.Companies.Where(c => c.Id == company).Select(c => c.Name).SingleAsync(cancellation));
            Assert.Equal(0, await upgraded.ProviderTickets.CountAsync(cancellation));
            Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync(cancellation));
        }
    }

    [Fact]
    public async Task An_instance_with_the_settings_of_spec_0010_starts_healthy_offers_no_provider_and_issues_the_same_tokens()   // criterion 30
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithPrivateSignup();
        using var client = factory.CreateClient();

        using (var health = await client.GetAsync("/auth/health"))
        {
            Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());
        }

        using (var options = await client.GetAsync("/auth/options"))
        {
            Assert.Equal("""{"signup":"self-service","private_signup":"self-service","providers":[]}""", await options.Content.ReadAsStringAsync());
        }

        Assert.Empty(factory.Services.GetRequiredService<ProviderCatalog>().Offered);
        var token = await LoginApi.LoginToken(client, factory);
        Assert.Equal(LoginClaims, AccessTokens.Payload(token).Select(p => p.Key).Order(StringComparer.Ordinal));
        using var me = await TenancyApi.Get(client, "/auth/me", token);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("has_password").GetBoolean());
        Assert.Equal(0, body.RootElement.GetProperty("linked_providers").GetArrayLength());
    }
}
```

- [ ] **Step 2: See them fail.** Build, then run `RatePolicyTests` and `RateLimitMiddlewareTests`: the provider paths count against `general`.

- [ ] **Step 3: The policy.** In `src/Auth.Server/RateLimiting/RatePolicy.cs`, replace

```csharp
    /// <summary>
    /// The policy of a request, chosen by method and path (the path compared without regard to case or one trailing slash), or
    /// <see langword="null"/> for a request outside <c>/auth/</c>. Only a <c>POST</c> to the specific paths is not "general".
    /// </summary>
```

with

```csharp
    /// <summary>
    /// The policy of a request, chosen by method and path (the path compared without regard to case or one trailing slash), or
    /// <see langword="null"/> for a request outside <c>/auth/</c>. Only a <c>POST</c> to the specific paths, and a <c>GET</c> of the start or the
    /// callback of a provider round trip (spec 0011), is not "general".
    /// </summary>
```

and replace

```csharp
        if (!HttpMethods.IsPost(method))
        {
            return RatePolicy.General;
        }

        var text = path.Value ?? "";
        var trimmed = text.Length > 1 && text[^1] == '/' ? text[..^1] : text;
        if (trimmed.Equals("/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Login;
        }
```

with

```csharp
        var text = path.Value ?? "";
        var trimmed = text.Length > 1 && text[^1] == '/' ? text[..^1] : text;

        // Spec 0011: the start and the callback of a provider round trip are steps of a sign-in, the only GETs that are not general (T14).
        if (HttpMethods.IsGet(method) && IsProviderRoundTrip(trimmed))
        {
            return RatePolicy.Login;
        }

        if (!HttpMethods.IsPost(method))
        {
            return RatePolicy.General;
        }

        if (trimmed.Equals("/auth/login", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/providers/exchange", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/providers/link", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/auth/providers/finish", StringComparison.OrdinalIgnoreCase))
        {
            return RatePolicy.Login;
        }
```

and replace the last lines of the class

```csharp
        return RatePolicy.General;
    }
}
```

with

```csharp
        return RatePolicy.General;
    }

    /// <summary><c>/auth/providers/{any name}/start</c> or <c>/callback</c>, without regard to case: the name is judged later, by the endpoint.</summary>
    private static bool IsProviderRoundTrip(string path)
    {
        var segments = path.Split('/');
        return segments.Length == 5
            && segments[0].Length == 0
            && segments[1].Equals("auth", StringComparison.OrdinalIgnoreCase)
            && segments[2].Equals("providers", StringComparison.OrdinalIgnoreCase)
            && segments[3].Length > 0
            && (segments[4].Equals("start", StringComparison.OrdinalIgnoreCase) || segments[4].Equals("callback", StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 4: Run the tests.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.RatePolicyTests -class Auth.IntegrationTests.RatePolicySignupTests -class Auth.IntegrationTests.RateLimitMiddlewareTests -class Auth.IntegrationTests.JsonCharsetTests -class Auth.IntegrationTests.OpenApiHardeningTests -class Auth.IntegrationTests.AuditProviderFlowsTests -class Auth.IntegrationTests.UpgradeFromSpec0010Tests -class Auth.IntegrationTests.UpgradeToSpaceKindsTests -class Auth.IntegrationTests.AuditTableTests`
passes.

- [ ] **Step 5: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(server): the provider paths count against the login limit; the audit and upgrade checks`.

---

## Day 5 — Wednesday 29.04: the stack with the fake, the live check, the buttons

When the day is done, the end-to-end stack runs the fake provider as a container and Auth-Core trusts it as its test issuer, every earlier
script still passes, `scripts/e2e-providers.sh` drives every provider flow over the network, and the sample's sign-in, sign-up and invitation
screens start with the provider buttons.

### Task 14: The fake as a container, the manifests, the earlier end-to-end scripts

**Files:**
- Create: `tests/Auth.FakeOidc/Dockerfile`, `tests/Auth.FakeOidc/Dockerfile.dockerignore`, `tests/Auth.FakeOidc/compose.yml`
- Modify: `samples/notes-api/auth.yaml`, `samples/notes-api/compose.yml`, `samples/notes-web/compose.yml`, `deploy/auth.yaml`, `scripts/e2e-web.sh`, `scripts/e2e-notes.sh`, `scripts/e2e-signup.sh`, `scripts/e2e-private.sh`
- Modify (tests): `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`

**Interfaces:**
- Consumes: `FakeOidcOptions.IssuerOriginKey`/`BrowserOriginKey` (Task 4: `FakeOidc:IssuerOrigin`, `FakeOidc:BrowserOrigin`), `ProviderSettings`
  key constants (Task 3), `Manifest.Providers` (Task 2), `ProviderCatalog.Offered` (Task 3).
- Produces: the image `auth-core-fake-oidc:local` and the compose service `fake-oidc` (port `127.0.0.1:8099`); the overlay
  `tests/Auth.FakeOidc/compose.yml`, which Tasks 15 and 19 run on (Task 15 adds its script to the list the second new test reads); the
  notes manifest listing `google, microsoft`.

**Contract counts:** `ShippedConfigurationTests` gains three tests; the `GET /auth/options` literal of `scripts/e2e-signup.sh` and
`scripts/e2e-private.sh` gains `"providers":[]`.

**Decisions the plan takes here:**
- The root `.dockerignore` excludes `tests/` (the server image must not carry them), so the fake's build reads its own
  `tests/Auth.FakeOidc/Dockerfile.dockerignore` (BuildKit takes `<Dockerfile>.dockerignore` beside the Dockerfile before the root file); it lets
  in only the three build files of the root and the fake's folder. The base images are the server's, pinned by the same digests.
- The overlay is an extra `-f` file for the stacks whose manifest lists the providers (the notes sample: `scripts/e2e-web.sh`,
  `scripts/e2e-notes.sh`, `scripts/e2e-providers.sh`), so that their `/auth/health` stays `Healthy`. `deploy/auth.yaml` keeps the providers
  as a comment: the development stack, `scripts/e2e-prod.sh` and the earlier scripts need no keys and stay as they are (`"providers":[]`).
- The keys in the overlay are the fake's (it accepts any secret): no real provider knows them, and the secret scan sees plain words.
- The return address is `https://localhost:8443/provider-return`: the state and `auth_pt` cookies are `Secure`, and WebKit sends a `Secure`
  cookie over HTTPS only (the reason the web sample already has 8443). The notes stack has no 8443; it only needs the keys to stay healthy.

- [ ] **Step 1: Write the failing tests.** In `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`:

(a) replace

```csharp
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
```

with

```csharp
using Auth.IntegrationTests.Infrastructure;
using Auth.Server.Email;
using Auth.Server.Providers;
```

(b) after the method `BodyAsync` (its closing `    }`, before `    [Theory]`) add

```csharp

    /// <summary>
    /// The settings the fake provider's overlay gives Auth-Core: the <c>Auth__...</c> lines of its <c>auth</c> service, as configuration keys.
    /// </summary>
    private static Dictionary<string, string> OverlaySettings()
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(RepositoryFile("tests/Auth.FakeOidc/compose.yml")))
        {
            var trimmed = line.Trim();
            var colon = trimmed.IndexOf(": ", StringComparison.Ordinal);
            if (trimmed.StartsWith("Auth__", StringComparison.Ordinal) && colon > 0)
            {
                settings[trimmed[..colon].Replace("__", ":", StringComparison.Ordinal)] = trimmed[(colon + 2)..];
            }
        }

        return settings;
    }
```

(c) after the method `The_shipped_manifests_turn_sign_up_on` add

```csharp

    [Fact]
    public void The_notes_manifest_lists_both_providers_and_the_development_manifest_none()   // spec 0011 → Samples
    {
        var notes = ManifestParser.Parse(File.ReadAllText(RepositoryFile("samples/notes-api/auth.yaml")));
        var development = ManifestParser.Parse(File.ReadAllText(RepositoryFile("deploy/auth.yaml")));

        Assert.Null(notes.Error);
        Assert.Equal(["google", "microsoft"], notes.Manifest!.Providers);
        Assert.Null(development.Error);
        Assert.Empty(development.Manifest!.Providers);
        Assert.Contains("# providers: [google, microsoft]", File.ReadAllText(RepositoryFile("deploy/auth.yaml")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_fake_overlay_names_the_test_issuer_both_keys_and_the_return_address_and_the_notes_stacks_use_it()   // spec 0011 → Samples
    {
        var settings = OverlaySettings();

        Assert.Equal("http://fake-oidc:8099", settings[ProviderSettings.TestIssuerKey]);
        Assert.Equal("https://localhost:8443/provider-return", settings[ProviderSettings.ReturnUrlKey]);
        Assert.All(
            [ProviderSettings.GoogleClientIdKey, ProviderSettings.GoogleClientSecretKey, ProviderSettings.MicrosoftClientIdKey, ProviderSettings.MicrosoftClientSecretKey],
            key => Assert.False(string.IsNullOrWhiteSpace(settings[key])));
        Assert.All(
            ["scripts/e2e-web.sh", "scripts/e2e-notes.sh"],
            script => Assert.Contains("tests/Auth.FakeOidc/compose.yml", File.ReadAllText(RepositoryFile(script)), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_development_host_with_the_notes_manifest_and_the_overlay_offers_both_providers_and_is_healthy()   // spec 0011 → Samples
    {
        await using var factory = new AuthAppFactory(postgres, keys)
            .WithSetting(ManifestSettings.PathKey, "../../samples/notes-api/auth.yaml")
            .WithSetting(MailSettingsLoader.CompleteSignupUrlKey, DevelopmentAddress());
        foreach (var (key, value) in OverlaySettings())
        {
            factory.WithSetting(key, value);
        }

        using var client = factory.CreateClient();

        Assert.Equal(["google", "microsoft"], factory.Services.GetRequiredService<ProviderCatalog>().Offered);
        Assert.Equal(
            """{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft"]}""",
            await BodyAsync(client, "/auth/options"));
        Assert.Equal("Healthy", await BodyAsync(client, "/auth/health"));
    }
```

- [ ] **Step 2: See them fail.** Build, then run `ShippedConfigurationTests`: the three new tests fail (the overlay file is missing, the notes
manifest lists no provider).

- [ ] **Step 3: The image.** `tests/Auth.FakeOidc/Dockerfile`:

```dockerfile
# syntax=docker/dockerfile:1
# The fake OpenID provider of spec 0011, for the end-to-end stack only (tests/Auth.FakeOidc/compose.yml builds it). Never shipped.
# Build from the repository root; this folder's Dockerfile.dockerignore replaces the root .dockerignore, which leaves tests/ out:
#   docker build -f tests/Auth.FakeOidc/Dockerfile -t auth-core-fake-oidc:local .
#
# The base images are the server's (src/Auth.Server/Dockerfile), pinned by the same digests: update them together.

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY tests/Auth.FakeOidc/Auth.FakeOidc.csproj tests/Auth.FakeOidc/
RUN dotnet restore tests/Auth.FakeOidc/Auth.FakeOidc.csproj

COPY tests/Auth.FakeOidc/ tests/Auth.FakeOidc/
RUN dotnet publish tests/Auth.FakeOidc/Auth.FakeOidc.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:48e51f2f6798897be7ac4e775c049ed8fe60d3190f637e1f9c9dc7513efa659c AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8099
EXPOSE 8099

# Chiseled images ship a non-root "app" user; run as it explicitly.
USER $APP_UID
ENTRYPOINT ["dotnet", "Auth.FakeOidc.dll"]
```

`tests/Auth.FakeOidc/Dockerfile.dockerignore`:

```
# The build context of tests/Auth.FakeOidc/Dockerfile (BuildKit reads this file instead of the root .dockerignore): the three build
# files of the root and this folder, nothing else.
*
!global.json
!Directory.Build.props
!Directory.Packages.props
!tests/Auth.FakeOidc/
tests/Auth.FakeOidc/bin/
tests/Auth.FakeOidc/obj/
```

`tests/Auth.FakeOidc/compose.yml`:

```yaml
# The fake OpenID provider of spec 0011 (tests/Auth.FakeOidc) on top of the development stack, for the end-to-end checks only. Use it
# with the base file and the overlays of the samples:
#
#   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f samples/notes-web/compose.yml \
#     -f tests/Auth.FakeOidc/compose.yml --env-file .env up -d --build
#
# Relative paths in this file are resolved from deploy/ (the directory of the FIRST -f file), not from here.
# Auth-Core trusts it as its test issuer (Development only; any other environment refuses to start with it): the two providers are
# http://fake-oidc:8099/google and http://fake-oidc:8099/microsoft on the compose network, and a browser reaches the fake's sign-in page
# at http://localhost:8099 (loopback only). The keys below are the fake's own: it accepts any secret, and no real provider knows them.
services:
  fake-oidc:
    # Without its own image name Compose would name the build after the project.
    image: auth-core-fake-oidc:local
    build:
      context: ..
      dockerfile: tests/Auth.FakeOidc/Dockerfile
    environment:
      # The issuer in the fake's tokens and its token and key endpoints: the compose network, where Auth-Core reaches it.
      FakeOidc__IssuerOrigin: http://fake-oidc:8099
      # Its authorization endpoint: where the browser is sent.
      FakeOidc__BrowserOrigin: http://localhost:8099
    ports:
      - "127.0.0.1:8099:8099"
    # No healthcheck: the chiseled image has no shell or curl. The scripts poll the discovery document from the host.

  auth:
    depends_on:
      fake-oidc:
        condition: service_started
    environment:
      Auth__Providers__TestIssuer: http://fake-oidc:8099
      Auth__Providers__Google__ClientId: fake-google-client.apps.test
      Auth__Providers__Google__ClientSecret: fake-google-secret
      Auth__Providers__Microsoft__ClientId: 6f1a2b3c-0000-4000-8000-0000000e2e01
      Auth__Providers__Microsoft__ClientSecret: fake-microsoft-secret
      # Where the round trip ends (the web sample's screen); the callback is this origin's /auth/providers/{name}/callback (spec 0011).
      Auth__App__FrontendUrls__ProviderReturn: https://localhost:8443/provider-return
```

- [ ] **Step 4: The manifests.** In `samples/notes-api/auth.yaml`, after

```yaml
#                DELETE /api/me deletes that space's notes before the account is deleted.
```

add

```yaml
# providers      the sign-in providers of spec 0011, in the order of the buttons. Their keys come from the environment: the end-to-end
#                stack takes them from the fake provider's overlay (tests/Auth.FakeOidc/compose.yml); a listed provider without its
#                keys is not offered and /auth/health says "Degraded".
```

and replace

```yaml
private:
  signup: self-service
  permissions: [notes:read, notes:write]
default_roles:
```

with

```yaml
private:
  signup: self-service
  permissions: [notes:read, notes:write]
providers: [google, microsoft]
default_roles:
```

In `deploy/auth.yaml`, after

```yaml
#                permissions of the catalog, never the four built-in ones or "*". Without the block private sign-up is off.
```

add

```yaml
# providers      the sign-in providers of spec 0011 (google, microsoft), offered in the order of the list. Each needs its keys and the
#                return address in the environment (docs/deployment/provider-keys.md); a listed provider without them is not offered
#                and /auth/health says "Degraded". Off here, so that the development stack needs no keys; the line to turn them on:
# providers: [google, microsoft]
```

The two compose files of the samples say how to start the stack by hand; after this task their manifest lists the providers. In
`samples/notes-api/compose.yml` replace

```yaml
#   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env up -d --build
#
```

with

```yaml
#   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env up -d --build
#
# The sample's manifest lists Google and Microsoft (spec 0011): add -f tests/Auth.FakeOidc/compose.yml for the fake provider's keys. Without it
# the sample's health says Degraded and the start logs that Google and Microsoft have no keys; nothing else changes.
#
```

and in `samples/notes-web/compose.yml` replace

```yaml
#     --env-file .env up -d --build
#
```

with

```yaml
#     --env-file .env up -d --build
#
# The sample's manifest lists Google and Microsoft (spec 0011): add -f tests/Auth.FakeOidc/compose.yml for the fake provider, whose page the
# buttons then open. Without it the sample's health says Degraded and the start logs that Google and Microsoft have no keys; nothing else changes.
#
```

- [ ] **Step 5: The earlier scripts.** `scripts/e2e-signup.sh`: replace

```bash
#      mail catcher answers; GET /auth/options -> 200 {"signup":"self-service","private_signup":"self-service"}, never stored; the OpenAPI description names the four
```

with

```bash
#      mail catcher answers; GET /auth/options -> 200 {"signup":"self-service","private_signup":"self-service","providers":[]}, never stored; the OpenAPI description names the four
```

and replace

```bash
expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service"}'
```

with

```bash
expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service","providers":[]}'
```

`scripts/e2e-private.sh`: replace

```bash
#   1. health is Healthy; the mail catcher answers; GET /auth/options -> 200 {"signup":"self-service","private_signup":"self-service"}
```

with

```bash
#   1. health is Healthy; the mail catcher answers; GET /auth/options -> 200 {"signup":"self-service","private_signup":"self-service",
#      "providers":[]}
```

and replace

```bash
expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service"}'
```

with

```bash
expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service","providers":[]}'
```

`scripts/e2e-web.sh`: replace

```bash
# Chromium and WebKit (npx playwright install chromium webkit, once), curl, docker compose, and the host ports 8088, 8443,
# 8080 and 8025 free. Reads AUTH_DEV_SEED_EMAIL, AUTH_DEV_SEED_PASSWORD, AUTH_DEV_SEED_UNVERIFIED_EMAIL,
```

with

```bash
# Chromium and WebKit (npx playwright install chromium webkit, once), curl, docker compose, and the host ports 8088, 8443,
# 8080, 8025 and 8099 free. Reads AUTH_DEV_SEED_EMAIL, AUTH_DEV_SEED_PASSWORD, AUTH_DEV_SEED_UNVERIFIED_EMAIL,
```

replace

```bash
# The tests of spec 0010 (e2e/10-private.spec.ts) make what they need themselves, through the API; nothing is seeded for them.
```

with

```bash
# The tests of spec 0010 (e2e/10-private.spec.ts) make what they need themselves, through the API; nothing is seeded for them.
# The stack includes the fake OpenID provider of spec 0011 (tests/Auth.FakeOidc/compose.yml, http://localhost:8099): the sample's manifest
# lists Google and Microsoft, e2e/11-providers.spec.ts signs in through it, and Auth-Core trusts it as its test issuer.
```

replace

```bash
compose=(docker compose -f "$root/deploy/docker-compose.yml" -f "$root/samples/notes-api/compose.yml" -f "$web/compose.yml" --env-file "$root/.env")
```

with

```bash
compose=(docker compose -f "$root/deploy/docker-compose.yml" -f "$root/samples/notes-api/compose.yml" -f "$web/compose.yml"
  -f "$root/tests/Auth.FakeOidc/compose.yml" --env-file "$root/.env")
```

replace

```bash
hide() { sed -E -e 's/token=[A-Za-z0-9_-]+/token=<hidden>/g' -e 's/fill\("([^"\\]|\\.)*"\)/fill("<hidden>")/g'; }
```

with

```bash
# A ticket (spec 0011) and an invitation token on a provider start are credentials too: Playwright can print the URL that holds them.
hide() {
  sed -E -e 's/token=[A-Za-z0-9_-]+/token=<hidden>/g' -e 's/ticket=[A-Za-z0-9_-]+/ticket=<hidden>/g' \
    -e 's/invite=[A-Za-z0-9_-]+/invite=<hidden>/g' -e 's/fill\("([^"\\]|\\.)*"\)/fill("<hidden>")/g'
}
```

and replace

```bash
  wait_ok "$MAILPIT_URL/readyz" 60 || fail "$project: $MAILPIT_URL/readyz did not return 200 within 60s"
```

with

```bash
  wait_ok "$MAILPIT_URL/readyz" 60 || fail "$project: $MAILPIT_URL/readyz did not return 200 within 60s"
  wait_ok "http://localhost:8099/google/.well-known/openid-configuration" 60 \
    || fail "$project: the fake provider (http://localhost:8099) did not answer within 60s"
```

`scripts/e2e-notes.sh`: replace

```bash
#   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
#   scripts/e2e-notes.sh                  # this script: brings the stack up (builds the images), then drives it
#   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
```

with

```bash
#   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f tests/Auth.FakeOidc/compose.yml --env-file .env down -v
#   scripts/e2e-notes.sh                  # this script: brings the stack up (builds the images), then drives it
#   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f tests/Auth.FakeOidc/compose.yml --env-file .env down -v
# The fake OpenID provider's overlay (spec 0011) is part of the stack: the sample's manifest lists Google and Microsoft, and without their
# keys /auth/health would say "Degraded". Nothing here signs in through it (scripts/e2e-providers.sh does).
```

replace

```bash
# (standard library only), docker compose, and host ports 8088 (Caddy), 8080 (Auth-Core) and 8025 (Mailpit) free.
```

with

```bash
# (standard library only), docker compose, and host ports 8088 (Caddy), 8080 (Auth-Core), 8025 (Mailpit) and 8099 (the fake provider) free.
```

and replace

```bash
compose=(docker compose -f "$root/deploy/docker-compose.yml" -f "$root/samples/notes-api/compose.yml" --env-file "$root/.env")
```

with

```bash
compose=(docker compose -f "$root/deploy/docker-compose.yml" -f "$root/samples/notes-api/compose.yml" -f "$root/tests/Auth.FakeOidc/compose.yml"
  --env-file "$root/.env")
```

- [ ] **Step 6: Run the tests and check the files.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ShippedConfigurationTests -class Auth.IntegrationTests.ManifestProvidersTests`
passes. `bash -n scripts/e2e-web.sh scripts/e2e-notes.sh scripts/e2e-signup.sh scripts/e2e-private.sh`
prints nothing. `docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f samples/notes-web/compose.yml -f tests/Auth.FakeOidc/compose.yml --env-file .env config --quiet`
exits 0 (it reads the files only; with no `.env` in the worktree, copy `.env.example` to a temporary `.env` and remove it afterwards). That
is the only compose command of this task: no image build (Global Constraints: the e2e verifier builds the fake's image with its first
`up --build`, which is also the first check of the Dockerfile and its `Dockerfile.dockerignore`), no `up`, no `down`.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files; the shell scripts keep LF, the
`.cs` file CRLF, the YAML and Dockerfile files LF (as `deploy/` and `src/Auth.Server/Dockerfile`). Proposed subject:
`test(e2e): the fake provider as a container; the notes manifest lists Google and Microsoft`.

---

### Task 15: `scripts/e2e-providers.sh`

**Files:**
- Create: `scripts/e2e-providers.sh` (LF, executable: `git update-index --chmod=+x` is the orchestrator's; the implementer runs `chmod +x`)
- Modify (tests): `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`

**Interfaces:**
- Consumes: the stack of Task 14 (the web sample, the notes sample and `tests/Auth.FakeOidc/compose.yml`); the fake's non-interactive mode
  (Task 4: `person`, `email`, `verified`, `subject`, `defect` on its authorization endpoint; `custom` derives the identity from the address,
  `cancel` answers `access_denied`); every endpoint of Tasks 5 to 12.
- Produces: the live check of criterion 33.

**Contract counts:** none.

**Decisions the plan takes here:**
- The script runs on the web stack (the base file, the notes overlay, the web overlay and the fake's overlay), at `https://localhost:8443`
  with `curl -k`: the return address and so the callback are on that origin, and the cookies are `Secure`. It does not bring the stack up or
  down (as `scripts/e2e-private.sh`); its header gives the commands, with the limiter off as for the other scripts that sign in many times.
- Every address is made for the run, and the fake's `custom` person derives the identity from the address, so the script runs again on the
  same stack. A second identity with an address that has an account is a `custom` Microsoft person with an explicit `subject`.
- It drives every exchange answer the development stack can reach (spec 0011, Amendment A8 and criterion 33): `authenticated`, `next: link`
  (verified and not), `next: finish_signup`, `no_email`, `invalid_ticket`, `already_registered` (the micro sign and the Greek mu, built with
  `chr()` and handed to the fake percent-encoded, the second with an explicit `subject` so that it is another identity); for an invitation
  `address_mismatch`, `provider_already_linked` (PW's Microsoft identity, whose object id the script fixes), `invalid_token` (the invitation
  cancelled through the company API), `private_account_exists` and `authenticated`; `no_membership` and `already_member` after one SQL
  statement each over the `psql` the script already uses (no API leaves an account without a membership, and a company refuses to invite its
  own member). Only `no_account` is left to the integration tests (`ProviderExchangeTests.No_account_with_both_switches_off_is_403_no_account`):
  it needs both sign-up switches off, and the stack's manifest has them on.
- Every write to the secrets file goes through `keep_secret`, which skips an empty value (a cancelled round trip has no code); the last step
  still refuses an empty line and checks that the file is not empty.
- Every secret the script sees (the state, the nonce, the challenge, the code, the request-forgery cookie, each ticket, each `auth_pt`, each
  access token and refresh cookie, the invitation token, the passwords) goes to a file in the `mktemp -d` directory, never to the screen, and
  the last step searches the audit log for each of them. URLs that hold a state or a code reach curl through a config file (`-K`), never the
  command line.

- [ ] **Step 1: Write the failing test.** In `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`, in the method
`The_fake_overlay_names_the_test_issuer_both_keys_and_the_return_address_and_the_notes_stacks_use_it`, replace

```csharp
            ["scripts/e2e-web.sh", "scripts/e2e-notes.sh"],
```

with

```csharp
            ["scripts/e2e-web.sh", "scripts/e2e-notes.sh", "scripts/e2e-providers.sh"],
```

Build, run `ShippedConfigurationTests`: that test fails (the script is missing).

- [ ] **Step 2: The script.** `scripts/e2e-providers.sh`:

```bash
#!/usr/bin/env bash
# Real-network end-to-end check of spec 0011 (Google and Microsoft sign-in) against the web stack with the fake OpenID provider: Auth-Core,
# PostgreSQL, the mail catcher (Mailpit), the notes service, Caddy (https://localhost:8443) and the fake (http://localhost:8099), which
# Auth-Core trusts as its test issuer (tests/Auth.FakeOidc/compose.yml). The script plays the browser with curl: it follows each redirect
# itself, keeps the cookies in files, and asks the fake for an answer at once (its non-interactive mode: person=...).
#
# What it checks, in order (NEW = a person who signs up with Google; PW = a person with a password account who links Microsoft; INV = a person
# the development company invites; SEED = the development admin). Each step names the criteria it touches; the guarding tests are in
# docs/superpowers/plans/0011-acceptance-map.md.
#   1. health is Healthy; the fake answers; GET /auth/options -> {"signup":"self-service","private_signup":"self-service",
#      "providers":["google","microsoft"]}; the OpenAPI description names the five provider paths - criteria 3, 29.
#   2. the start: an unknown name -> 404 not_found; another purpose -> 400 invalid_request; Google and Microsoft -> 302 to the fake with the
#      client id, the callback address on this origin, the code flow, the scopes, S256, a state, a nonce and prompt=select_account; the
#      request-forgery cookie is HttpOnly, Secure, SameSite=Lax, Path=/auth/providers - criteria 5, 6.
#   3. NEW's round trip with Google: the return address with a ticket and purpose sign_in; auth_pt HttpOnly, Secure, SameSite=Strict,
#      Path=/auth, Max-Age=600; the exchange without auth_pt or with another browser's -> 400 invalid_ticket; with its own -> next
#      finish_signup with both kinds - criteria 7, 10, 11, 14.
#   4. NEW finishes privately -> a login's answer, auth_rt set, auth_pt cleared, org_kind personal; the used ticket -> invalid_ticket; NEW
#      signs in again with the button alone -> authenticated, the same account; /auth/me -> has_password false, linked_providers [google]
#      - criteria 12, 19, 25.
#   5. PW signs up privately through the mail with a password; PW's round trip with Microsoft (a verified address) -> next link, verified;
#      a wrong password -> 401 invalid_credentials; the right one -> authenticated, PW's account; the next round trip -> authenticated
#      without a password; /auth/me -> has_password true, linked_providers [microsoft] - criteria 15, 16, 25.
#   6. a second Microsoft identity with PW's address, unverified (a work address), and one with an address no account has -> the same
#      next link with email_verified false; a Google identity without an address -> 403 no_email - criteria 9, 13, 17.
#   7. SEED invites INV; a round trip that starts with the invitation and comes back with another address -> 403 address_mismatch; with INV's
#      address -> authenticated, in the development company, as user - criteria 21, 22.
#   8. NEW deletes the account: a password -> 403 reauth_required; a sign-in ticket -> 403 reauth_failed; the reauth start asks Google for
#      max_age=0; the reauth ticket without auth_pt -> 403 reauth_failed; with it -> 204, auth_rt and auth_pt cleared; NEW's next round trip
#      -> next finish_signup (the account is gone) - criteria 23, 24.
#   9. a cancel at the provider -> the return address with error=cancelled and purpose sign_in and no auth_pt; the same without the
#      request-forgery cookie -> error=invalid_state - criterion 8.
#  10. a look-alike: an account under the micro-sign spelling of an address (made by a Google finish), then the Greek-mu spelling, verified,
#      as another identity -> 409 already_registered - criterion 12.
#  11. the refusals of the exchange the stack reaches only with some set-up (spec 0011, Amendment A8): an invitation accepted with PW's
#      Microsoft identity -> 409 provider_already_linked; the same invitation cancelled by SEED -> 400 invalid_token; an invitation of PW's
#      address (a private account) -> 409 private_account_exists; INV's membership deleted by one SQL statement -> 403 no_membership;
#      INV invited again and its membership put back by one SQL statement -> 409 already_member - criteria 10, 22. Only no_account is out
#      of the stack's reach (both sign-up switches are on): the integration tests guard it.
#  12. the audit rows of the run, read from the database: login.succeeded with details.provider, login.failed, provider.linked,
#      signup.completed, invite.accepted, account.delete_refused, provider.reauthenticated, account.deleted; and no row holds a state, a
#      nonce, a challenge, a code, a request-forgery cookie, a ticket, an auth_pt, a session token, an invitation token or a password of the
#      run - criteria 28, 33.
#
# Full sequence, from the repo root (the stack is the web sample's with the fake's overlay; the per-IP limiter is OFF, as for the other scripts
# that sign in many times through the one proxy address):
#   cp .env.example .env                  # then set real local values (git-ignored); NOTES_DB_PASSWORD too
#   MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh        # signing/encryption keys into .secrets/ (git-ignored)
#   export COMPOSE_PROJECT_NAME=auth-core-providers       # a project of its own, never "auth-core" (the development stack)
#   files="-f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f samples/notes-web/compose.yml -f tests/Auth.FakeOidc/compose.yml"
#   docker compose $files --env-file .env down -v                                   # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose $files --env-file .env up -d --build
#   scripts/e2e-providers.sh              # this script (does NOT bring the stack up or down)
#   docker compose $files --env-file .env down -v                                   # tear down
#
# Reads AUTH_DEV_SEED_EMAIL and AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced). The database is read with
# `docker compose exec postgres psql`. Needs: curl, python3 (standard library only; E2E_PYTHON=<interpreter> names another, for example
# E2E_PYTHON=C:/p6v/Scripts/python.exe), docker compose, and host ports 8088, 8443, 8080, 8025 and 8099 free. Env: BASE_URL (default
# https://localhost:8443), FAKE_URL (default http://localhost:8099), MAILPIT_URL (default http://localhost:8025). Takes about four minutes, most
# of it the wait for the mails. Re-runnable on the same stack: every address is made for the run. Exits non-zero on the first failure; prints
# "PASS <step>" per step; never prints a password, a token, a ticket, a cookie, a state, a code or a mail body.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-https://localhost:8443}"
FAKE_URL="${FAKE_URL:-http://localhost:8099}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
RETURN_URL="$BASE_URL/provider-return"
PY="${E2E_PYTHON:-python3}"
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-providers}"
[[ "$COMPOSE_PROJECT_NAME" != "auth-core" ]] || { echo "FAIL COMPOSE_PROJECT_NAME=auth-core is the development stack; use another name" >&2; exit 1; }
compose=(docker compose -f "$root/deploy/docker-compose.yml" -f "$root/samples/notes-api/compose.yml" -f "$root/samples/notes-web/compose.yml"
  -f "$root/tests/Auth.FakeOidc/compose.yml" --env-file "$root/.env")
# The mail links of this stack (samples/notes-web/compose.yml).
SIGNUP_LINK="http://localhost:8088/complete-signup"
INVITE_LINK="http://localhost:8088/invite"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
: > "$tmp/secrets"

fail() { echo "FAIL $*" >&2; exit 1; }
pass() { echo "PASS $*"; }

# keep_secret <value>: a secret the run saw, for the search of the audit log at the end. An empty value (a member an answer did not have, such
# as the code of a cancelled round trip) is skipped: it is no secret, and the search of the last step refuses an empty one.
keep_secret() {
  if [[ -n "$1" ]]; then printf '%s\n' "$1" >> "$tmp/secrets"; fi
}

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
NEW_EMAIL="new-$run@gmail.test"
PW_EMAIL="pw-$run@poczta.test"
NOBODY_EMAIL="nobody-$run@firma.test"
INV_EMAIL="inv-$run@gmail.test"
OTHER_EMAIL="other-$run@gmail.test"
LINKED_EMAIL="linked-$run@gmail.test"
# PW's Microsoft identity has a fixed object id, so that step 11 can present the same identity with another address.
PW_OID="$("$PY" -c 'import uuid; print(uuid.uuid4())' | tr -d '\r')"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD" E2E_PW_EMAIL="$PW_EMAIL" E2E_INV_EMAIL="$INV_EMAIL"
export E2E_LINKED_EMAIL="$LINKED_EMAIL"
export E2E_PASSWORD="E2e-Passw0rd-$run" E2E_WRONG_PASSWORD="E2e-Wrong-Passw0rd-$run"
keep_secret "$E2E_PASSWORD"
keep_secret "$E2E_WRONG_PASSWORD"
keep_secret "$SEED_PASSWORD"

wait_ok() { # wait_ok <url> <seconds>: waits until the URL answers 200 (any certificate)
  local i
  for i in $(seq 1 "$2"); do
    if [[ "$(curl -s -k -o /dev/null --max-time 3 -w '%{http_code}' "$1" || true)" == "200" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

# call <method> <path> [body-file] [header-file ...] -> sets HTTP_CODE and BODY; response headers in $tmp/hdr. The URL reaches curl through a
# config file, so that a state or a code in it never shows in the process list.
call() {
  local file
  printf 'url = "%s"\n' "$BASE_URL$2" > "$tmp/url.cfg"
  local args=(-sS -k --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' -X "$1" -K "$tmp/url.cfg")
  if [[ -n "${3:-}" ]]; then args+=(-H 'Content-Type: application/json' --data-binary "@$3"); fi
  for file in "${@:4}"; do
    if [[ -n "$file" ]]; then args+=(-H "@$file"); fi
  done
  HTTP_CODE="$(curl "${args[@]}")"
  BODY="$(cat "$tmp/body")"
}

header() { # value of a response header of the last response, CR stripped (empty when absent)
  { grep -i "^$1:" "$tmp/hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//'
}

# A Windows python3 ends its lines with CR LF: the helpers that print a value strip the CR.
val() { "$PY" -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1" <<< "$BODY" | tr -d '\r'; }

enc() { "$PY" -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$1" | tr -d '\r'; }

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

set_cookie_line() { # set_cookie_line <name-pattern>: the Set-Cookie line of that cookie in the last response, CR stripped (empty when none)
  { grep -iE "^set-cookie: $1=" "$tmp/hdr" || true; } | head -n1 | tr -d '\r'
}

pair_of() { local line="${1#*: }"; printf '%s' "${line%%;*}"; }   # pair_of <set-cookie-line>: name=value

cookie_value() { local pair; pair="$(pair_of "$(set_cookie_line "$1")")"; printf '%s' "${pair#*=}"; }

has_attr() { # has_attr <set-cookie-line> <attribute>: exact, case-insensitive match of one "; "-separated attribute
  printf '%s' "$1" | tr ';' '\n' | sed 's/^ *//' | grep -qixF -- "$2"
}

expect_attrs() { # expect_attrs <what> <set-cookie-line> <attribute> ...
  local what="$1" line="$2" attr
  [[ -n "$line" ]] || fail "$what: the cookie was not set"
  for attr in "${@:3}"; do
    has_attr "$line" "$attr" || fail "$what: the cookie lacks the $attr attribute"
  done
}

url_param() { # url_param <file-with-a-url> <query|fragment> <name>: one parameter of the URL in the file (empty when absent); never printed by the callers
  "$PY" -c '
import sys, urllib.parse
parts = urllib.parse.urlsplit(open(sys.argv[1], encoding="utf-8").read().strip())
text = parts.query if sys.argv[2] == "query" else parts.fragment
print(urllib.parse.parse_qs(text, keep_blank_values=True).get(sys.argv[3], [""])[0])
' "$1" "$2" "$3" | tr -d '\r'
}

fragment_of() { # fragment_of <name>: the members of the fragment of <name>'s return address as JSON, a ticket replaced by its length
  "$PY" -c '
import json, re, sys, urllib.parse
fragment = urllib.parse.urlsplit(open(sys.argv[1], encoding="utf-8").read().strip()).fragment
members = {k: v[0] if len(v) == 1 else v for k, v in urllib.parse.parse_qs(fragment, keep_blank_values=True).items()}
if "ticket" in members:
    members["ticket"] = len(members["ticket"]) if re.fullmatch(r"[A-Za-z0-9_-]+", members["ticket"]) else "not base64url"
print(json.dumps(members, sort_keys=True, separators=(",", ":")))
' "$tmp/$1.return" | tr -d '\r'
}

# round_trip <name> <provider> <start-query> <fake-parameters> [nocookie]: one browser's round trip, the fake answering at once. Keeps the start's
# Location in $tmp/<name>.start, the callback's Location in $tmp/<name>.return, the ticket (when there is one) in $tmp/<name>.ticket, the auth_pt
# header file in $tmp/<name>.pt and the Set-Cookie line of auth_pt in AUTH_PT_LINE; every secret it sees goes to $tmp/secrets. With "nocookie"
# the callback is sent without the request-forgery cookie.
round_trip() {
  local name="$1" provider="$2" line member back state_header
  call GET "/auth/providers/$provider/start$3"
  expect_status "$name: the start" 302
  header location > "$tmp/$name.start"
  [[ "$(cat "$tmp/$name.start")" == "$FAKE_URL/$provider/authorize?"* ]] || fail "$name: the start does not send the browser to the fake's $provider"
  line="$(set_cookie_line 'OpenIddict\.Client\.State[^=]*')"
  [[ -n "$line" ]] || fail "$name: the start set no request-forgery cookie"
  printf 'Cookie: %s\n' "$(pair_of "$line")" > "$tmp/$name.state"
  keep_secret "$(pair_of "$line" | cut -d= -f2-)"
  for member in state nonce code_challenge; do keep_secret "$(url_param "$tmp/$name.start" query "$member")"; done

  printf 'url = "%s&%s"\n' "$(cat "$tmp/$name.start")" "$4" > "$tmp/fake.cfg"
  [[ "$(curl -sS --max-time 20 -K "$tmp/fake.cfg" -o /dev/null -D "$tmp/fake.hdr" -w '%{http_code}')" == "302" ]] \
    || fail "$name: the fake did not answer 302"
  { grep -i '^location:' "$tmp/fake.hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//' > "$tmp/$name.back"
  back="$(cat "$tmp/$name.back")"
  [[ "$back" == "$BASE_URL/auth/providers/$provider/callback?"* ]] || fail "$name: the fake did not send the browser to the callback"
  keep_secret "$(url_param "$tmp/$name.back" query code)"

  state_header="$tmp/$name.state"
  if [[ "${5:-}" == "nocookie" ]]; then state_header=""; fi
  call GET "${back#"$BASE_URL"}" "" "$state_header"
  expect_status "$name: the callback" 302
  expect_no_store "$name: the callback"
  header location > "$tmp/$name.return"
  [[ "$(cat "$tmp/$name.return")" == "$RETURN_URL#"* ]] || fail "$name: the callback did not send the browser to the return address"
  rm -f "$tmp/$name.ticket" "$tmp/$name.pt"
  AUTH_PT_LINE="$(set_cookie_line auth_pt)"
  if [[ -n "$(url_param "$tmp/$name.return" fragment ticket)" ]]; then
    url_param "$tmp/$name.return" fragment ticket > "$tmp/$name.ticket"
    keep_secret "$(cat "$tmp/$name.ticket")"
    [[ -n "$AUTH_PT_LINE" ]] || fail "$name: the callback made a ticket and set no auth_pt"
    printf 'Cookie: %s\n' "$(pair_of "$AUTH_PT_LINE")" > "$tmp/$name.pt"
    keep_secret "$(pair_of "$AUTH_PT_LINE" | cut -d= -f2-)"
  fi
}

ticket_json() { # ticket_json <name> <member> <out-file> [python dict of more members, may read os.environ]: a body with the ticket of <name>
  "$PY" -c '
import json, os, sys
body = {sys.argv[2]: open(sys.argv[1], encoding="utf-8").read().strip()}
body.update(eval(sys.argv[3]))
print(json.dumps(body))
' "$tmp/$1.ticket" "$2" "${4:-dict()}" > "$3"
}

expect_signed_in() { # expect_signed_in <what> <who>: a login's answer, auth_rt set, auth_pt cleared; keeps the bearer header in $tmp/<who>.auth
  local line
  expect_status "$1" 200
  expect_eq "$1: the body is a login's" "$(val 'sorted(d.keys())')" "['access_token', 'status']"
  expect_eq "$1: status" "$(val 'd["status"]')" "authenticated"
  line="$(set_cookie_line auth_rt)"
  [[ -n "$line" ]] || fail "$1: no auth_rt cookie"
  line="$(set_cookie_line auth_pt)"
  expect_attrs "$1: the cleared auth_pt" "$line" "Max-Age=0" "Path=/auth"
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$2.auth"
  keep_secret "$(val 'd["access_token"]')"
  keep_secret "$(cookie_value auth_rt)"
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

# token_body <link-prefix> <file> [password-env]: takes the token from the text part of $tmp/mail.json and writes {"token"} (plus "password"
# when named). Never printed.
token_body() {
  "$PY" -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(re.escape(sys.argv[1]) + r"\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no link with a token")
body = {"token": match.group(1)}
if sys.argv[2]:
    body["password"] = os.environ[sys.argv[2]]
print(json.dumps(body))
' "$1" "${3:-}" < "$tmp/mail.json" > "$2"
}

token_of() { "$PY" -c 'import json,sys; print(json.load(sys.stdin)["token"])' < "$1" | tr -d '\r'; }   # never printed by the callers

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

# --- Step 1: health, the fake, the options, the description (criteria 3, 29) ------------------------------------------
wait_ok "$BASE_URL/auth/health" 120 || fail "step 1: $BASE_URL/auth/health did not return 200 within 120s (is the stack up? see the header)"
wait_ok "$FAKE_URL/google/.well-known/openid-configuration" 60 || fail "step 1: the fake provider ($FAKE_URL) did not answer within 60s"
wait_ok "$MAILPIT_URL/readyz" 60 || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s"
START_TS="$(psql_value "SELECT now()")"
call GET /auth/health
expect_status "step 1: health" 200 "Healthy"
call GET /auth/options
expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft"]}'
expect_no_store "step 1: the options"
call GET /auth/openapi/v1.json
expect_status "step 1: the OpenAPI description" 200
expect_eq "step 1: the five provider paths are described" \
  "$(val "sorted(p for p in d['paths'] if p.startswith('/auth/providers/'))")" \
  "['/auth/providers/exchange', '/auth/providers/finish', '/auth/providers/link', '/auth/providers/{name}/callback', '/auth/providers/{name}/start']"
pass "step 1: health is Healthy; the fake answers; GET /auth/options offers google and microsoft; the description names the five provider paths"

# --- Step 2: the start (criteria 5, 6) --------------------------------------------------------------------------------------
call GET /auth/providers/github/start
expect_error "step 2: an unknown name" 404 not_found
call GET "/auth/providers/google/start?purpose=other"
expect_error "step 2: another purpose" 400 invalid_request
for provider in google microsoft; do
  call GET "/auth/providers/$provider/start"
  expect_status "step 2: the start of $provider" 302
  header location > "$tmp/start.url"
  [[ "$(cat "$tmp/start.url")" == "$FAKE_URL/$provider/authorize?"* ]] || fail "step 2: the start of $provider does not go to the fake"
  [[ -n "$(url_param "$tmp/start.url" query client_id)" ]] || fail "step 2: $provider: no client_id"
  expect_eq "step 2: $provider: redirect_uri" "$(url_param "$tmp/start.url" query redirect_uri)" "$BASE_URL/auth/providers/$provider/callback"
  expect_eq "step 2: $provider: response_type" "$(url_param "$tmp/start.url" query response_type)" "code"
  expect_eq "step 2: $provider: scope" "$(url_param "$tmp/start.url" query scope | tr ' ' '\n' | sort | tr '\n' ' ')" "email openid profile "
  expect_eq "step 2: $provider: code_challenge_method" "$(url_param "$tmp/start.url" query code_challenge_method)" "S256"
  expect_eq "step 2: $provider: prompt" "$(url_param "$tmp/start.url" query prompt)" "select_account"
  for member in state nonce code_challenge; do
    [[ -n "$(url_param "$tmp/start.url" query "$member")" ]] || fail "step 2: $provider: no $member"
    keep_secret "$(url_param "$tmp/start.url" query "$member")"
  done
  LINE="$(set_cookie_line 'OpenIddict\.Client\.State[^=]*')"
  expect_attrs "step 2: $provider: the request-forgery cookie" "$LINE" HttpOnly Secure SameSite=Lax Path=/auth/providers
  keep_secret "$(pair_of "$LINE" | cut -d= -f2-)"
done
pass "step 2: an unknown name -> 404, another purpose -> 400; each start -> 302 to the fake with the code flow, PKCE, a state, a nonce and the callback on this origin; the cookie is Lax"

# --- Step 3: NEW's round trip and the browser binding (criteria 7, 10, 11, 14) ---------------------------------------------
round_trip new google "" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
expect_eq "step 3: the fragment" "$(fragment_of new)" '{"purpose":"sign_in","ticket":43}'
expect_attrs "step 3: auth_pt" "$AUTH_PT_LINE" HttpOnly Secure SameSite=Strict Path=/auth Max-Age=600
round_trip other google "" "person=custom&email=$(enc "$OTHER_EMAIL")&verified=true"
ticket_json new ticket "$tmp/new.exchange"
call POST /auth/providers/exchange "$tmp/new.exchange"
expect_error "step 3: the exchange without auth_pt" 400 invalid_ticket
call POST /auth/providers/exchange "$tmp/new.exchange" "$tmp/other.pt"
expect_error "step 3: the exchange with another browser's auth_pt" 400 invalid_ticket
call POST /auth/providers/exchange "$tmp/new.exchange" "$tmp/new.pt"
expect_status "step 3: the exchange" 200 "{\"next\":\"finish_signup\",\"email\":\"$NEW_EMAIL\",\"kinds\":[\"organization\",\"private\"]}"
pass "step 3: a round trip -> the return address with a 43-character ticket and auth_pt (Strict, 10 minutes); without it or with another browser's -> invalid_ticket; with it -> next finish_signup"

# --- Step 4: NEW finishes privately and signs in again (criteria 12, 19, 25) -----------------------------------------------
ticket_json new ticket "$tmp/new.finish" "{'kind': 'private'}"
call POST /auth/providers/finish "$tmp/new.finish" "$tmp/new.pt"
expect_signed_in "step 4: the finish" new
BODY="$(jwt_payload "$tmp/new.auth")"
expect_eq "step 4: org_kind" "$(val 'd["org_kind"]')" "personal"
expect_eq "step 4: roles" "$(val 'd["roles"]')" "['private']"
NEW_SUB="$(val 'd["sub"]')"
call POST /auth/providers/exchange "$tmp/new.exchange" "$tmp/new.pt"
expect_error "step 4: the used ticket" 400 invalid_ticket
round_trip new2 google "" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
ticket_json new2 ticket "$tmp/new2.exchange"
call POST /auth/providers/exchange "$tmp/new2.exchange" "$tmp/new2.pt"
expect_signed_in "step 4: the button alone" new
BODY="$(jwt_payload "$tmp/new.auth")"
expect_eq "step 4: the same account" "$(val 'd["sub"]')" "$NEW_SUB"
call GET /auth/me "" "$tmp/new.auth"
expect_status "step 4: GET /auth/me" 200
expect_eq "step 4: has_password" "$(val 'd["has_password"]')" "False"
expect_eq "step 4: linked_providers" "$(val 'd["linked_providers"]')" "['google']"
pass "step 4: the private finish signs NEW in (personal space, auth_pt cleared); the ticket is used up; the button alone signs NEW in again; /auth/me says no password and google"

# --- Step 5: PW links Microsoft with the password (criteria 15, 16, 25) -----------------------------------------------------
json_body "$tmp/pw.signup" "{'email': os.environ['E2E_PW_EMAIL'], 'kind': 'private'}"
call POST /auth/signup "$tmp/pw.signup"
expect_status "step 5: PW's private sign-up request" 202 ""
wait_mail "$PW_EMAIL" "Complete your sign-up" 90 || fail "step 5: no sign-up mail for PW within 90s"
token_body "$SIGNUP_LINK" "$tmp/pw.complete" E2E_PASSWORD || fail "step 5: no usable sign-up link in PW's mail"
keep_secret "$(token_of "$tmp/pw.complete")"
call POST /auth/signup/complete "$tmp/pw.complete"
expect_status "step 5: PW's completion" 200
printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/pw.auth"
BODY="$(jwt_payload "$tmp/pw.auth")"
PW_SUB="$(val 'd["sub"]')"
round_trip pw microsoft "" "person=custom&email=$(enc "$PW_EMAIL")&verified=true&subject=$PW_OID"
ticket_json pw ticket "$tmp/pw.exchange"
call POST /auth/providers/exchange "$tmp/pw.exchange" "$tmp/pw.pt"
expect_status "step 5: the exchange" 200 "{\"next\":\"link\",\"email\":\"$PW_EMAIL\",\"email_verified\":true}"
ticket_json pw ticket "$tmp/pw.wrong" "{'password': os.environ['E2E_WRONG_PASSWORD']}"
call POST /auth/providers/link "$tmp/pw.wrong" "$tmp/pw.pt"
expect_error "step 5: a wrong password" 401 invalid_credentials
ticket_json pw ticket "$tmp/pw.link" "{'password': os.environ['E2E_PASSWORD']}"
call POST /auth/providers/link "$tmp/pw.link" "$tmp/pw.pt"
expect_signed_in "step 5: the link" pw
BODY="$(jwt_payload "$tmp/pw.auth")"
expect_eq "step 5: PW's account" "$(val 'd["sub"]')" "$PW_SUB"
round_trip pw2 microsoft "" "person=custom&email=$(enc "$PW_EMAIL")&verified=true&subject=$PW_OID"
ticket_json pw2 ticket "$tmp/pw2.exchange"
call POST /auth/providers/exchange "$tmp/pw2.exchange" "$tmp/pw2.pt"
expect_signed_in "step 5: the button alone after the link" pw
BODY="$(jwt_payload "$tmp/pw.auth")"
expect_eq "step 5: still PW's account" "$(val 'd["sub"]')" "$PW_SUB"
call GET /auth/me "" "$tmp/pw.auth"
expect_status "step 5: GET /auth/me" 200
expect_eq "step 5: has_password" "$(val 'd["has_password"]')" "True"
expect_eq "step 5: linked_providers" "$(val 'd["linked_providers"]')" "['microsoft']"
pass "step 5: a verified address of a password account -> next link; a wrong password -> 401; the right one links and signs in; the button alone signs in next time"

# --- Step 6: unverified addresses and no address (criteria 9, 13, 17) ------------------------------------------------------
SECOND_OID="$("$PY" -c 'import uuid; print(uuid.uuid4())' | tr -d '\r')"
round_trip work microsoft "" "person=custom&email=$(enc "$PW_EMAIL")&subject=$SECOND_OID"
ticket_json work ticket "$tmp/work.exchange"
call POST /auth/providers/exchange "$tmp/work.exchange" "$tmp/work.pt"
expect_status "step 6: an unverified address that has an account" 200 "{\"next\":\"link\",\"email\":\"$PW_EMAIL\",\"email_verified\":false}"
round_trip nobody microsoft "" "person=custom&email=$(enc "$NOBODY_EMAIL")"
ticket_json nobody ticket "$tmp/nobody.exchange"
call POST /auth/providers/exchange "$tmp/nobody.exchange" "$tmp/nobody.pt"
expect_status "step 6: an unverified address that has none" 200 "{\"next\":\"link\",\"email\":\"$NOBODY_EMAIL\",\"email_verified\":false}"
round_trip noemail google "" "person=google&subject=noemail-$run&defect=no_email"
ticket_json noemail ticket "$tmp/noemail.exchange"
call POST /auth/providers/exchange "$tmp/noemail.exchange" "$tmp/noemail.pt"
expect_error "step 6: an identity without an address" 403 no_email
pass "step 6: an unverified work address -> the same next link (email_verified false) whether or not an account has it; no address -> 403 no_email"

# --- Step 7: an invitation accepted with Google (criteria 21, 22) -----------------------------------------------------------
json_body "$tmp/seed.login" "{'email': os.environ['E2E_SEED_EMAIL'], 'password': os.environ['E2E_SEED_PASSWORD']}"
call POST /auth/login "$tmp/seed.login"
expect_status "step 7: SEED's login" 200
printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/seed.auth"
keep_secret "$(val 'd["access_token"]')"
call GET /auth/me "" "$tmp/seed.auth"
expect_status "step 7: GET /auth/me of SEED" 200
DEV_ORG="$(val 'd["org_id"]')"
call GET /auth/org/roles "" "$tmp/seed.auth"
expect_status "step 7: GET /auth/org/roles" 200
export E2E_USER_ROLE
E2E_USER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "user"][0]')"
MAILS="$(mail_count "$INV_EMAIL")"
json_body "$tmp/invite.json" "{'email': os.environ['E2E_INV_EMAIL'], 'role_id': os.environ['E2E_USER_ROLE']}"
call POST /auth/org/invites "$tmp/invite.json" "$tmp/seed.auth"
expect_status "step 7: SEED invites INV" 202 ""
wait_mail "$INV_EMAIL" "You are invited" 90 "$MAILS" || fail "step 7: no invitation mail for INV within 90s"
token_body "$INVITE_LINK" "$tmp/inv.token" || fail "step 7: no usable invitation link"
INVITE_TOKEN="$(token_of "$tmp/inv.token")"
keep_secret "$INVITE_TOKEN"
round_trip mismatch google "?invite=$INVITE_TOKEN" "person=custom&email=$(enc "$OTHER_EMAIL")&verified=true"
expect_eq "step 7: the fragment" "$(fragment_of mismatch)" '{"purpose":"invite","ticket":43}'
ticket_json mismatch ticket "$tmp/mismatch.exchange"
call POST /auth/providers/exchange "$tmp/mismatch.exchange" "$tmp/mismatch.pt"
expect_error "step 7: another address" 403 address_mismatch
round_trip inv google "?invite=$INVITE_TOKEN" "person=custom&email=$(enc "$INV_EMAIL")&verified=true"
ticket_json inv ticket "$tmp/inv.exchange"
call POST /auth/providers/exchange "$tmp/inv.exchange" "$tmp/inv.pt"
expect_signed_in "step 7: the acceptance" inv
BODY="$(jwt_payload "$tmp/inv.auth")"
expect_eq "step 7: INV is in the development company" "$(val 'd["org_id"]')" "$DEV_ORG"
expect_eq "step 7: org_kind" "$(val 'd["org_kind"]')" "organization"
expect_eq "step 7: roles" "$(val 'd["roles"]')" "['user']"
INV_SUB="$(val 'd["sub"]')"
pass "step 7: an invitation started with Google: another address -> 403 address_mismatch; the invited address -> signed in to the company as user"

# --- Step 8: NEW deletes the account with Google (criteria 23, 24) -----------------------------------------------------------
json_body "$tmp/new.password" "{'password': os.environ['E2E_PASSWORD']}"
call DELETE /auth/account "$tmp/new.password" "$tmp/new.auth"
expect_error "step 8: a password for an account without one" 403 reauth_required
round_trip signin google "" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
ticket_json signin provider_ticket "$tmp/signin.delete"
call DELETE /auth/account "$tmp/signin.delete" "$tmp/new.auth" "$tmp/signin.pt"
expect_error "step 8: a sign-in ticket" 403 reauth_failed
call GET "/auth/providers/google/start?purpose=reauth"
expect_status "step 8: the reauth start" 302
header location > "$tmp/reauth.url"
expect_eq "step 8: Google is asked for a fresh sign-in" "$(url_param "$tmp/reauth.url" query max_age)" "0"
expect_eq "step 8: and not for an account choice" "$(url_param "$tmp/reauth.url" query prompt)" ""
keep_secret "$(url_param "$tmp/reauth.url" query state)"
round_trip reauth google "?purpose=reauth" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
expect_eq "step 8: the fragment" "$(fragment_of reauth)" '{"purpose":"reauth","ticket":43}'
ticket_json reauth provider_ticket "$tmp/reauth.delete"
call DELETE /auth/account "$tmp/reauth.delete" "$tmp/new.auth"
expect_error "step 8: the reauth ticket without auth_pt" 403 reauth_failed
call DELETE /auth/account "$tmp/reauth.delete" "$tmp/new.auth" "$tmp/reauth.pt"
expect_status "step 8: the deletion" 204 ""
expect_attrs "step 8: the cleared auth_rt" "$(set_cookie_line auth_rt)" "Max-Age=0"
expect_attrs "step 8: the cleared auth_pt" "$(set_cookie_line auth_pt)" "Max-Age=0"
round_trip gone google "" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
ticket_json gone ticket "$tmp/gone.exchange"
call POST /auth/providers/exchange "$tmp/gone.exchange" "$tmp/gone.pt"
expect_status "step 8: the deleted account's identity" 200 "{\"next\":\"finish_signup\",\"email\":\"$NEW_EMAIL\",\"kinds\":[\"organization\",\"private\"]}"
pass "step 8: a password -> reauth_required; a sign-in ticket or no auth_pt -> reauth_failed; the reauth ticket deletes the account (204, both cookies cleared); the identity is free again"

# --- Step 9: a cancel at the provider (criterion 8) ----------------------------------------------------------------------------
round_trip cancel google "" "person=cancel"
expect_eq "step 9: the fragment" "$(fragment_of cancel)" '{"error":"cancelled","purpose":"sign_in"}'
[[ -z "$AUTH_PT_LINE" ]] || fail "step 9: a cancel set auth_pt"
round_trip forged google "" "person=cancel" nocookie
expect_eq "step 9: the fragment without the request-forgery cookie" "$(fragment_of forged)" '{"error":"invalid_state","purpose":"sign_in"}'
pass "step 9: a cancel -> error=cancelled with its purpose and no auth_pt; without the request-forgery cookie -> error=invalid_state"

# --- Step 10: a look-alike address (criterion 12) ----------------------------------------------------------------------------
# The micro sign and the Greek mu upper-case to one letter: an account under one spelling makes the other a look-alike. Python builds them with
# chr() and hands the fake only their percent-encoded form, so that the script holds no character outside ASCII.
lookalike() { # lookalike <code point in hex>: the percent-encoded address "<that character>aria-<run>@gmail.test"
  "$PY" -c 'import sys, urllib.parse; print(urllib.parse.quote(chr(int(sys.argv[1], 16)) + "aria-" + sys.argv[2] + "@gmail.test", safe=""))' "$1" "$run" \
    | tr -d '\r'
}
round_trip micro google "" "person=custom&email=$(lookalike B5)&verified=true"
ticket_json micro ticket "$tmp/micro.exchange"
call POST /auth/providers/exchange "$tmp/micro.exchange" "$tmp/micro.pt"
expect_status "step 10: the micro-sign spelling" 200
expect_eq "step 10: the micro-sign spelling has no account yet" "$(val 'd["next"]')" "finish_signup"
ticket_json micro ticket "$tmp/micro.finish" "{'kind': 'private'}"
call POST /auth/providers/finish "$tmp/micro.finish" "$tmp/micro.pt"
expect_signed_in "step 10: the account of the micro-sign spelling" micro
# The fake derives a custom person's identity from the upper-cased address: an explicit subject makes the Greek mu another identity.
round_trip mu google "" "person=custom&email=$(lookalike 3BC)&verified=true&subject=mu-$run"
ticket_json mu ticket "$tmp/mu.exchange"
call POST /auth/providers/exchange "$tmp/mu.exchange" "$tmp/mu.pt"
expect_error "step 10: the Greek-mu spelling of that address" 409 already_registered
pass "step 10: an address that only resembles an account's (micro sign and Greek mu) -> 409 already_registered"

# --- Step 11: the refusals that need a set-up (criteria 10, 22; Amendment A8) --------------------------------------------------
# invite <email-env> <name>: SEED invites the address as user; the token of the invitation mail goes to $tmp/<name>.invite (never printed)
invite() {
  local before
  before="$(mail_count "${!1}")"
  json_body "$tmp/$2.invite.json" "{'email': os.environ['$1'], 'role_id': os.environ['E2E_USER_ROLE']}"
  call POST /auth/org/invites "$tmp/$2.invite.json" "$tmp/seed.auth"
  expect_status "step 11: SEED invites $2" 202 ""
  wait_mail "${!1}" "You are invited" 90 "$before" || fail "step 11: no invitation mail for $2 within 90s"
  token_body "$INVITE_LINK" "$tmp/$2.invite.body" || fail "step 11: no usable invitation link for $2"
  token_of "$tmp/$2.invite.body" > "$tmp/$2.invite"
  keep_secret "$(cat "$tmp/$2.invite")"
}

# provider_already_linked: a new address invited, accepted with PW's Microsoft identity (the same object id, another address).
invite E2E_LINKED_EMAIL linked
round_trip linked microsoft "?invite=$(cat "$tmp/linked.invite")" "person=custom&email=$(enc "$LINKED_EMAIL")&verified=true&subject=$PW_OID"
ticket_json linked ticket "$tmp/linked.exchange"
call POST /auth/providers/exchange "$tmp/linked.exchange" "$tmp/linked.pt"
expect_error "step 11: an identity that is already a login of an account" 409 provider_already_linked

# invalid_token: SEED cancels that invitation; a round trip with its token (the start reads no invitation) is refused at the exchange.
call GET /auth/org/invites "" "$tmp/seed.auth"
expect_status "step 11: GET /auth/org/invites" 200
LINKED_INVITE="$(val "[i['id'] for i in d['invites'] if i['email'].lower() == '$LINKED_EMAIL'][0]")"
call DELETE "/auth/org/invites/$LINKED_INVITE" "" "$tmp/seed.auth"
expect_status "step 11: SEED cancels the invitation" 204 ""
round_trip cancelled google "?invite=$(cat "$tmp/linked.invite")" "person=custom&email=$(enc "$LINKED_EMAIL")&verified=true"
ticket_json cancelled ticket "$tmp/cancelled.exchange"
call POST /auth/providers/exchange "$tmp/cancelled.exchange" "$tmp/cancelled.pt"
expect_error "step 11: a cancelled invitation" 400 invalid_token

# private_account_exists: PW's address (a private account) invited, accepted with a Google identity of that address.
invite E2E_PW_EMAIL pwinvite
round_trip pwinvite google "?invite=$(cat "$tmp/pwinvite.invite")" "person=custom&email=$(enc "$PW_EMAIL")&verified=true"
ticket_json pwinvite ticket "$tmp/pwinvite.exchange"
call POST /auth/providers/exchange "$tmp/pwinvite.exchange" "$tmp/pwinvite.pt"
expect_error "step 11: an invitation of a private account's address" 409 private_account_exists

# no_membership: INV's membership deleted by one SQL statement (no API leaves an account without one); INV's linked identity is refused.
expect_eq "step 11: INV's membership is deleted" \
  "$(psql_value "WITH gone AS (DELETE FROM \"Memberships\" WHERE \"UserId\" = '$INV_SUB' RETURNING 1) SELECT count(*) FROM gone")" "1"
round_trip nomember google "" "person=custom&email=$(enc "$INV_EMAIL")&verified=true"
ticket_json nomember ticket "$tmp/nomember.exchange"
call POST /auth/providers/exchange "$tmp/nomember.exchange" "$tmp/nomember.pt"
expect_error "step 11: a linked identity whose account has no membership" 403 no_membership

# already_member: INV invited again (SEED's company refuses only its own members), the membership put back by one SQL statement, and the
# invitation accepted with another Google identity of INV's address.
invite E2E_INV_EMAIL again
expect_eq "step 11: INV's membership is back" \
  "$(psql_value "WITH made AS (INSERT INTO \"Memberships\" (\"UserId\", \"CompanyId\", \"RoleId\", \"JoinedAt\") SELECT '$INV_SUB', \"CompanyId\", \"Id\", now() FROM \"CompanyRoles\" WHERE \"CompanyId\" = '$DEV_ORG' AND \"Name\" = 'user' RETURNING 1) SELECT count(*) FROM made")" "1"
round_trip again google "?invite=$(cat "$tmp/again.invite")" "person=custom&email=$(enc "$INV_EMAIL")&verified=true&subject=again-$run"
ticket_json again ticket "$tmp/again.exchange"
call POST /auth/providers/exchange "$tmp/again.exchange" "$tmp/again.pt"
expect_error "step 11: an invitation of a company member's address" 409 already_member
pass "step 11: provider_already_linked, invalid_token (a cancelled invitation), private_account_exists, no_membership and already_member"

# --- Step 12: the audit rows and no secret (criteria 28, 33) -------------------------------------------------------------------
[[ -s "$tmp/secrets" ]] || fail "step 12: no secret was collected"
since="occurred_at >= '$START_TS'"
expect_eq "step 12: login.succeeded with details.provider (finish, button, link, button, invitation, the micro-sign finish)" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.succeeded' AND details ? 'provider' AND $since")" "6"
expect_eq "step 12: login.failed of the wrong password" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.failed' AND subject_email = '$PW_EMAIL' AND details->>'reason' = 'wrong_password' AND details->>'provider' = 'microsoft' AND $since")" "1"
expect_eq "step 12: provider.linked" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'provider.linked' AND subject_user_id = '$PW_SUB' AND actor_user_id = '$PW_SUB' AND details->>'provider' = 'microsoft'")" "1"
expect_eq "step 12: signup.completed with the provider" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.completed' AND subject_user_id = '$NEW_SUB' AND details->>'kind' = 'private' AND details->>'provider' = 'google'")" "1"
expect_eq "step 12: invite.accepted with the provider" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'invite.accepted' AND subject_email = '$INV_EMAIL' AND org_id = '$DEV_ORG' AND details->>'provider' = 'google' AND details->>'role' = 'user'")" "1"
expect_eq "step 12: account.delete_refused (the sign-in ticket, the missing auth_pt)" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'account.delete_refused' AND subject_user_id = '$NEW_SUB' AND details->>'reason' = 'reauth_failed'")" "2"
expect_eq "step 12: provider.reauthenticated" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'provider.reauthenticated' AND subject_user_id = '$NEW_SUB' AND actor_user_id = '$NEW_SUB' AND details->>'provider' = 'google'")" "1"
expect_eq "step 12: account.deleted" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'account.deleted' AND subject_user_id = '$NEW_SUB' AND details->>'reason' = 'self'")" "1"
COUNT=0
while IFS= read -r secret; do
  [[ -n "$secret" ]] || fail "step 12: a secret to look for is empty"
  expect_eq "step 12: rows of the audit log that hold a secret of this run" "$(secret_in_audit "$secret")" "0"
  COUNT=$((COUNT + 1))
done < "$tmp/secrets"
[[ "$COUNT" -ge 60 ]] || fail "step 12: only $COUNT secrets were collected"
pass "step 12: the audit rows of the run hold the provider; none of the $COUNT secrets of the run (states, codes, cookies, tickets, tokens, passwords) is in the audit log"

echo "ALL PASS"
```

- [ ] **Step 3: Check the script.** `chmod +x scripts/e2e-providers.sh`; `bash -n scripts/e2e-providers.sh` prints nothing; the file has LF endings
(`file scripts/e2e-providers.sh` says no CRLF); `grep -n 'echo.*\$\(E2E_PASSWORD\|INVITE_TOKEN\|secret\)' scripts/e2e-providers.sh` finds no line
that prints a secret. Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ShippedConfigurationTests`
passes. Do not run the script: the live run is the verifiers' (it needs the stack of the header).

- [ ] **Step 4: Hand back** — uncommitted. Files: `scripts/e2e-providers.sh`, `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`. Scan the
touched files. Proposed subject: `test(e2e): scripts/e2e-providers.sh drives the provider flows over the network`.

---

### Task 16: The sample: the options, `/auth/me`, the answers, and the provider buttons

**Files:**
- Modify: `samples/notes-web/src/app/account-api.ts`, `samples/notes-web/src/app/auth/auth.service.ts`, `samples/notes-web/src/app/texts.ts`,
  `samples/notes-web/src/app/pages/login.ts`, `samples/notes-web/src/app/pages/signup.ts`, `samples/notes-web/src/app/pages/invite.ts`,
  `samples/notes-web/src/styles.css`, `samples/notes-web/src/testing/helpers.ts`
- Create: `samples/notes-web/src/app/pages/provider-buttons.ts`
- Modify (tests): `samples/notes-web/src/app/account-api.signup.spec.ts`
- Create (tests): `samples/notes-web/src/app/account-api.providers.spec.ts`, `samples/notes-web/src/app/auth/auth.service.providers.spec.ts`,
  `samples/notes-web/src/app/pages/provider-buttons.spec.ts`, `samples/notes-web/src/app/pages/providers-on-screens.spec.ts`

**Interfaces:**
- Consumes: `GET /auth/options` `providers` (Task 3), `GET /auth/me` `has_password` and `linked_providers` (Task 12), the error codes of Tasks 7
  to 12.
- Produces (later tasks use them): in `account-api.ts` `PROVIDER_NAMES`, `type ProviderName = 'google' | 'microsoft'`,
  `isProviderName(value: unknown): value is ProviderName`, `SiteOptions.providers: ProviderName[]`,
  `providerStartUrl(name: ProviderName, how?: { invite: string } | { reauth: true } | null): string`; in `auth.service.ts` `Me.has_password: boolean`,
  `Me.linked_providers: ProviderName[]` and the `Failure` kinds `invalid_ticket`, `no_email`, `no_account`, `address_mismatch`,
  `provider_already_linked`, `reauth_failed`, `password_required`, `reauth_required`; in `pages/provider-buttons.ts` the component
  `ProviderButtons` (`app-provider-buttons`; inputs `providers`, `action: 'continue' | 'signup' | 'accept' | 'confirm'`, `invite`, `reauth`,
  `separator`), `rememberProvider(name: ProviderName): void` and `rememberedProvider(): ProviderName | null`; `texts.providers`; in
  `testing/helpers.ts` the two new members on `adminMe`/`privateMe`/`viewerMe` and `providerMe` (a private account with no password, linked to
  Google).

**Contract counts:** `account-api.signup.spec.ts` pins the shape of `SiteOptions`: each expected object gains `providers: []`.

**Decisions the plan takes here:**
- The buttons are links (`<a href>`), a full page navigation (spec 0011 → Sample frontend). Only names this app knows become buttons, in the
  order of the answer, each once; a failed options call means no button, as it means sign-up off.
- Which provider the person chose is kept in `sessionStorage` (`notes-web.provider`, the name only, never a ticket): the return screen of
  Task 17 needs it for "connect {Google}", and the fragment does not carry it. Storage that throws or is empty gives "Google or Microsoft".
- `has_password` that is not `false` reads as `true` (an older Auth-Core, or a body without the member, keeps the screen of spec 0010:
  the password field); `linked_providers` keeps the known names only.
- The invitation screen asks `GET /auth/options` beside the preview; its buttons are drawn only for `has_private_account` `false`.

- [ ] **Step 1: Write the failing tests.**

In `samples/notes-web/src/app/account-api.signup.spec.ts` replace every `toEqual({ signup: 'self-service', private_signup: 'off' })` with
`toEqual({ signup: 'self-service', private_signup: 'off', providers: [] })` (three places), every
`toEqual({ signup: 'invite-only', private_signup: 'off' })` with `toEqual({ signup: 'invite-only', private_signup: 'off', providers: [] })` (four
places) and the one `toEqual({ signup: 'invite-only', private_signup: 'self-service' })` with
`toEqual({ signup: 'invite-only', private_signup: 'self-service', providers: [] })`. In the test `'ignores the members it does not know'`
replace

```ts
      ctrl.expectOne('/auth/options').flush({ signup: 'self-service', providers: ['google'] });
```

with

```ts
      ctrl.expectOne('/auth/options').flush({ signup: 'self-service', theme: 'dark' });
```

`samples/notes-web/src/app/account-api.providers.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AccountApi, PROVIDER_NAMES, isProviderName, providerStartUrl } from './account-api';

describe('AccountApi: the providers of GET /auth/options (spec 0011)', () => {
  let api: AccountApi;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(AccountApi);
    ctrl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => ctrl.verify());

  it('reads the offered providers in the order of the answer', async () => {
    const done = api.options();
    ctrl.expectOne('/auth/options').flush({ signup: 'self-service', private_signup: 'off', providers: ['microsoft', 'google'] });
    expect((await done).providers).toEqual(['microsoft', 'google']);
  });

  it.each([
    [['google', 'github', 'microsoft'], ['google', 'microsoft']],
    [['google', 'google'], ['google']],
    [['Google', 'MICROSOFT'], []],
    [[1, null, 'microsoft'], ['microsoft']],
    ['google', []],
    [null, []],
  ])('keeps only the names it knows, once each: %j -> %j', async (providers, expected) => {
    const done = api.options();
    ctrl.expectOne('/auth/options').flush({ signup: 'self-service', providers });
    expect((await done).providers).toEqual(expected);
  });

  it('a failed call means no provider', async () => {
    const done = api.options();
    ctrl.expectOne('/auth/options').flush('oops', { status: 500, statusText: '500' });
    expect(await done).toEqual({ signup: 'invite-only', private_signup: 'off', providers: [] });
  });

  it('knows Google and Microsoft', () => {
    expect(PROVIDER_NAMES).toEqual(['google', 'microsoft']);
    expect(isProviderName('google')).toBe(true);
    expect(isProviderName('github')).toBe(false);
    expect(isProviderName(undefined)).toBe(false);
  });

  it('builds the start addresses of the buttons: plain, with an invitation, for a confirmation', () => {
    expect(providerStartUrl('google')).toBe('/auth/providers/google/start');
    expect(providerStartUrl('microsoft', { invite: 'abc_DEF-123' })).toBe('/auth/providers/microsoft/start?invite=abc_DEF-123');
    expect(providerStartUrl('google', { invite: 'a b&c' })).toBe('/auth/providers/google/start?invite=a%20b%26c');
    expect(providerStartUrl('google', { reauth: true })).toBe('/auth/providers/google/start?purpose=reauth');
  });
});
```

`samples/notes-web/src/app/auth/auth.service.providers.spec.ts`:

```ts
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { adminMe, holdToken, privateMe, providerMe } from '../../testing/helpers';
import { AuthService, failureOf } from './auth.service';

function answer(status: number, error: string): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: { error } });
}

describe('AuthService: /auth/me and the answers of spec 0011', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
  });

  async function meFor(body: unknown) {
    await holdToken(auth, ctrl, 'tok');
    const loaded = auth.loadMe();
    ctrl.expectOne('/auth/me').flush(body);
    await loaded;
    return auth.me();
  }

  it('reads has_password and linked_providers', async () => {
    expect(await meFor(providerMe)).toEqual(providerMe);
    expect(providerMe.has_password).toBe(false);
    expect(providerMe.linked_providers).toEqual(['google']);
  });

  it('the helpers of the earlier specs have a password and no provider', () => {
    expect([adminMe.has_password, privateMe.has_password]).toEqual([true, true]);
    expect([adminMe.linked_providers, privateMe.linked_providers]).toEqual([[], []]);
  });

  it.each([[undefined], [null], ['false'], [0]])('a has_password of %j reads as true: the screen keeps its password field', async (value) => {
    const body: Record<string, unknown> = { ...privateMe, has_password: value };
    if (value === undefined) {
      delete body['has_password'];
    }
    expect((await meFor(body))?.has_password).toBe(true);
  });

  it.each([
    [['google', 'github'], ['google']],
    [['microsoft', 'google'], ['microsoft', 'google']],
    ['google', []],
    [undefined, []],
  ])('linked_providers %j reads as %j', async (value, expected) => {
    expect((await meFor({ ...privateMe, linked_providers: value }))?.linked_providers).toEqual(expected);
  });

  it.each([
    [400, 'invalid_ticket'],
    [403, 'no_email'],
    [403, 'no_account'],
    [403, 'address_mismatch'],
    [409, 'provider_already_linked'],
    [403, 'reauth_failed'],
    [403, 'password_required'],
    [403, 'reauth_required'],
  ])('reads %s %s as a failure of its own', (status, code) => {
    expect(failureOf(answer(status, code))).toEqual({ kind: code });
  });

  it.each([
    [409, 'invalid_ticket'],
    [400, 'no_email'],
    [500, 'reauth_failed'],
  ])('reads %s %s as other: the status is part of the answer', (status, code) => {
    expect(failureOf(answer(status, code))).toEqual({ kind: 'other' });
  });
});
```

`samples/notes-web/src/app/pages/provider-buttons.spec.ts`:

```ts
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { ProviderName } from '../account-api';
import { has, pageText } from '../../testing/helpers';
import { ProviderButtons, rememberProvider, rememberedProvider } from './provider-buttons';

@Component({
  imports: [ProviderButtons],
  template: `<app-provider-buttons
    [providers]="providers()"
    [action]="action()"
    [invite]="invite()"
    [reauth]="reauth()"
    [separator]="separator()"
  />`,
})
class Host {
  readonly providers = signal<ProviderName[]>(['google', 'microsoft']);
  readonly action = signal<'continue' | 'signup' | 'accept' | 'confirm'>('continue');
  readonly invite = signal<string | null>(null);
  readonly reauth = signal(false);
  readonly separator = signal(true);
}

function draw(change: (host: Host) => void = () => undefined) {
  const fixture = TestBed.createComponent(Host);
  change(fixture.componentInstance);
  fixture.detectChanges();
  return fixture;
}

function hrefs(fixture: ReturnType<typeof draw>): (string | null)[] {
  return [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')].map((a) => a.getAttribute('href'));
}

describe('ProviderButtons (spec 0011)', () => {
  afterEach(() => sessionStorage.clear());

  it('draws one link per provider, in the given order, to the start, with "or" after them', () => {
    const fixture = draw((host) => host.providers.set(['microsoft', 'google']));
    expect(hrefs(fixture)).toEqual(['/auth/providers/microsoft/start', '/auth/providers/google/start']);
    expect(pageText(fixture)).toBe('Continue with Microsoft Continue with Google or');
    expect(has(fixture, 'button')).toBe(false);
  });

  it('draws nothing at all without a provider', () => {
    const fixture = draw((host) => host.providers.set([]));
    expect(pageText(fixture)).toBe('');
    expect(has(fixture, '[data-testid="provider-buttons"]')).toBe(false);
  });

  it.each([
    ['signup', 'Sign up with Google Sign up with Microsoft or'],
    ['accept', 'Accept with Google Accept with Microsoft or'],
  ] as const)('the words of %s', (action, text) => {
    expect(pageText(draw((host) => host.action.set(action)))).toBe(text);
  });

  it('an invitation goes with the start, and a confirmation asks for reauth, without the "or"', () => {
    expect(hrefs(draw((host) => host.invite.set('tok-i')))).toEqual([
      '/auth/providers/google/start?invite=tok-i',
      '/auth/providers/microsoft/start?invite=tok-i',
    ]);
    const confirm = draw((host) => {
      host.providers.set(['google']);
      host.action.set('confirm');
      host.reauth.set(true);
      host.separator.set(false);
    });
    expect(hrefs(confirm)).toEqual(['/auth/providers/google/start?purpose=reauth']);
    expect(pageText(confirm)).toBe('Confirm with Google');
  });

  it('remembers the provider the person chose, the name only', () => {
    const fixture = draw();
    const link = (fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('[data-testid="provider-microsoft"]')!;
    link.addEventListener('click', (event) => event.preventDefault()); // jsdom does not navigate
    link.click();
    expect(rememberedProvider()).toBe('microsoft');
    expect(Object.keys(sessionStorage)).toEqual(['notes-web.provider']);
  });

  it('reads nothing it does not know, and survives storage that throws', () => {
    sessionStorage.setItem('notes-web.provider', 'github');
    expect(rememberedProvider()).toBeNull();
    const failing = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    expect(rememberedProvider()).toBeNull();
    failing.mockRestore();
    const writing = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });
    expect(() => rememberProvider('google')).not.toThrow();
    writing.mockRestore();
  });
});
```

`samples/notes-web/src/app/pages/providers-on-screens.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, realLocation, settle } from '../../testing/helpers';
import { InvitePage } from './invite';
import { LoginPage } from './login';
import { SignupPage } from './signup';

const both = { signup: 'self-service', private_signup: 'self-service' };
const preview = { org_name: 'Acme', email: 'new@acme.example', role: 'viewer' };

async function open(path: string, page: Type<unknown>, options: object, query = '') {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path, component: page }]), provideHttpClient(), provideHttpClientTesting(), realLocation],
  });
  history.replaceState(null, '', `/${path}${query}`);
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/${path}${query}`, page);
  await settle();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne('/auth/options').flush(options);
  await settle(harness.fixture);
  return { fixture: harness.fixture, ctrl };
}

/** The text of the page up to the first form field: what comes first. */
function before(fixture: Parameters<typeof pageText>[0], selector: string): string {
  const root = fixture.nativeElement as HTMLElement;
  const field = root.querySelector(selector)!;
  const range = document.createRange();
  range.setStart(root, 0);
  range.setEndBefore(field);
  return range.toString().replace(/\s+/g, ' ').trim();
}

describe('the provider buttons on the screens (spec 0011)', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('/login: the buttons first, in the order of the options, then "or", then the form', async () => {
    const { fixture } = await open('login', LoginPage, { ...both, providers: ['microsoft', 'google'] });
    expect(before(fixture, '#email')).toBe('Sign in Continue with Microsoft Continue with Google or Email');
    expect(has(fixture, 'a[href="/auth/providers/google/start"]')).toBe(true);
  });

  it('/login: no button when providers is empty', async () => {
    const { fixture } = await open('login', LoginPage, { ...both, providers: [] });
    expect(has(fixture, '[data-testid="provider-buttons"]')).toBe(false);
    expect(pageText(fixture)).not.toContain('Continue with');
  });

  it('/signup: "Sign up with ..." first, then the form of spec 0010', async () => {
    const { fixture } = await open('signup', SignupPage, { ...both, providers: ['google', 'microsoft'] });
    expect(before(fixture, '[data-testid="signup-kind"]')).toContain('Sign up with Google Sign up with Microsoft or');
    expect(has(fixture, '#company')).toBe(true);
  });

  it('/signup: no button when providers is empty', async () => {
    const { fixture } = await open('signup', SignupPage, { ...both, providers: [] });
    expect(pageText(fixture)).not.toContain('Sign up with');
  });

  it('/invite: "Accept with ..." with the invitation token, before the password form', async () => {
    const { fixture, ctrl } = await open('invite', InvitePage, { ...both, providers: ['google', 'microsoft'] }, '?token=tok-i');
    ctrl.expectOne('/auth/invites/preview').flush({ ...preview, has_private_account: false });
    await settle(fixture);
    expect(before(fixture, '#password')).toContain('Accept with Google Accept with Microsoft or');
    expect(has(fixture, 'a[href="/auth/providers/google/start?invite=tok-i"]')).toBe(true);
    expect(window.location.href).not.toContain('tok-i'); // the token left the address bar; it is only in the link
  });

  it('/invite: no button for an address with a private account: the screen of spec 0010', async () => {
    const { fixture, ctrl } = await open('invite', InvitePage, { ...both, providers: ['google'] }, '?token=tok-i');
    ctrl.expectOne('/auth/invites/preview').flush({ ...preview, has_private_account: true });
    await settle(fixture);
    expect(has(fixture, '[data-testid="move"]')).toBe(true);
    expect(pageText(fixture)).not.toContain('Accept with');
  });

  it('/invite: no button when providers is empty', async () => {
    const { fixture, ctrl } = await open('invite', InvitePage, { ...both, providers: [] }, '?token=tok-i');
    ctrl.expectOne('/auth/invites/preview').flush({ ...preview, has_private_account: false });
    await settle(fixture);
    expect(pageText(fixture)).not.toContain('Accept with');
  });
});
```

- [ ] **Step 2: See them fail.** From `samples/notes-web`: `npm test` fails (the new names do not exist; the expected objects have
`providers`).

- [ ] **Step 3: The options and the start addresses.** In `samples/notes-web/src/app/account-api.ts` replace

```ts
/** What GET /auth/options answers, as far as the screens care: whether a visitor may start a company, or sign up alone (spec 0010). */
export interface SiteOptions {
  signup: 'self-service' | 'invite-only';
  private_signup: 'self-service' | 'off';
}
```

with

```ts
/** The sign-in providers this app has buttons for (spec 0011). */
export const PROVIDER_NAMES = ['google', 'microsoft'] as const;

export type ProviderName = (typeof PROVIDER_NAMES)[number];

export function isProviderName(value: unknown): value is ProviderName {
  return value === 'google' || value === 'microsoft';
}

/** The names of a JSON value that this app knows, in their order, each once: anything else is dropped. */
export function providerNamesOf(value: unknown): ProviderName[] {
  if (!Array.isArray(value)) {
    return [];
  }
  return value.filter(isProviderName).filter((name, index, all) => all.indexOf(name) === index);
}

/**
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

/**
 * What GET /auth/options answers, as far as the screens care: whether a visitor may start a company, or sign up alone (spec 0010), and the
 * providers that have a button, in Auth-Core's order (spec 0011).
 */
export interface SiteOptions {
  signup: 'self-service' | 'invite-only';
  private_signup: 'self-service' | 'off';
  providers: ProviderName[];
}
```

and replace

```ts
      return {
        signup: isRecord(body) && body['signup'] === 'self-service' ? 'self-service' : 'invite-only',
        private_signup: isRecord(body) && body['private_signup'] === 'self-service' ? 'self-service' : 'off',
      };
    } catch {
      return { signup: 'invite-only', private_signup: 'off' };
    }
```

with

```ts
      return {
        signup: isRecord(body) && body['signup'] === 'self-service' ? 'self-service' : 'invite-only',
        private_signup: isRecord(body) && body['private_signup'] === 'self-service' ? 'self-service' : 'off',
        providers: isRecord(body) ? providerNamesOf(body['providers']) : [],
      };
    } catch {
      return { signup: 'invite-only', private_signup: 'off', providers: [] };
    }
```

and in the summary of `options()` replace

```ts
   * app does not know, and one that takes longer than ten seconds, mean "invite-only" and "off": the link "Create an account" is then
   * not offered, which is the safe side.
```

with

```ts
   * app does not know, and one that takes longer than ten seconds, mean "invite-only", "off" and no provider: the link "Create an account"
   * and the provider buttons are then not offered, which is the safe side.
```

- [ ] **Step 4: `/auth/me` and the answers.** In `samples/notes-web/src/app/auth/auth.service.ts`:

(a) replace

```ts
import { firstValueFrom, timeout } from 'rxjs';
```

with

```ts
import { firstValueFrom, timeout } from 'rxjs';
import { ProviderName, providerNamesOf } from '../account-api';
```

(`account-api.ts` already imports from this file; the cycle is of functions used at run time only, never at load, which ES modules allow.
If the build reports a cycle error, move `ProviderName`, `isProviderName` and `providerNamesOf` to a new file `src/app/providers.ts`,
re-export them from `account-api.ts`, and import them here from `'../providers'`; say so in the hand-back.)

(b) replace

```ts
  roles: string[];
  permissions: string[];
}

/** What went wrong with a call to an account endpoint, as far as a screen cares. */
```

with

```ts
  roles: string[];
  permissions: string[];
  /** Spec 0011: false for an account made through Google or Microsoft that has never set a password. */
  has_password: boolean;
  /** The providers that sign in to this account, sorted (spec 0011). */
  linked_providers: ProviderName[];
}

/** What went wrong with a call to an account endpoint, as far as a screen cares. */
```

(c) replace

```ts
  | { kind: 'invalid_request' }
  | { kind: 'too_many_attempts'; retryAfterSeconds: number }
```

with

```ts
  | { kind: 'invalid_request' }
  | { kind: 'invalid_ticket' }
  | { kind: 'no_email' }
  | { kind: 'no_account' }
  | { kind: 'address_mismatch' }
  | { kind: 'provider_already_linked' }
  | { kind: 'reauth_failed' }
  | { kind: 'password_required' }
  | { kind: 'reauth_required' }
  | { kind: 'too_many_attempts'; retryAfterSeconds: number }
```

(d) replace

```ts
  if (error.status === 409 && code === 'private_account_exists') {
    return { kind: 'private_account_exists' };
  }
  return { kind: 'other' };
}
```

with

```ts
  if (error.status === 409 && code === 'private_account_exists') {
    return { kind: 'private_account_exists' };
  }
  return providerFailureOf(error.status, code);
}

/** The answers of spec 0011, each with the status of the contract; anything else is "other". */
function providerFailureOf(status: number, code: string | undefined): Failure {
  if (status === 400 && code === 'invalid_ticket') {
    return { kind: 'invalid_ticket' };
  }
  if (status === 409 && code === 'provider_already_linked') {
    return { kind: 'provider_already_linked' };
  }
  if (status !== 403) {
    return { kind: 'other' };
  }
  if (code === 'no_email') {
    return { kind: 'no_email' };
  }
  if (code === 'no_account') {
    return { kind: 'no_account' };
  }
  if (code === 'address_mismatch') {
    return { kind: 'address_mismatch' };
  }
  if (code === 'reauth_failed') {
    return { kind: 'reauth_failed' };
  }
  if (code === 'password_required') {
    return { kind: 'password_required' };
  }
  if (code === 'reauth_required') {
    return { kind: 'reauth_required' };
  }
  return { kind: 'other' };
}
```

(e) in `asMe` replace

```ts
      roles: body['roles'],
      permissions: body['permissions'],
    };
  }
  return null;
}
```

with

```ts
      roles: body['roles'],
      permissions: body['permissions'],
      // Anything but false keeps the password screens of spec 0010: an Auth-Core before spec 0011 does not send the member.
      has_password: body['has_password'] !== false,
      linked_providers: providerNamesOf(body['linked_providers']),
    };
  }
  return null;
}
```

- [ ] **Step 5: The texts, the helpers and the style.** In `samples/notes-web/src/app/texts.ts`, before

```ts
  login: {
    title: 'Sign in',
```

add

```ts
  // The provider buttons (spec 0011). The names are the providers' own; the words around them say what the button does.
  providers: {
    names: { google: 'Google', microsoft: 'Microsoft' } as Record<string, string>,
    either: 'Google or Microsoft',
    continueWith: (name: string): string => `Continue with ${name}`,
    signUpWith: (name: string): string => `Sign up with ${name}`,
    acceptWith: (name: string): string => `Accept with ${name}`,
    confirmWith: (name: string): string => `Confirm with ${name}`,
    or: 'or',
  },

```

In `samples/notes-web/src/testing/helpers.ts` replace

```ts
  roles: ['admin'],
  permissions: ['notes:read', 'notes:write'],
};

/** A private account (spec 0010): its space is named "private", its role is "private". */
```

with

```ts
  roles: ['admin'],
  permissions: ['notes:read', 'notes:write'],
  has_password: true,
  linked_providers: [],
};

/** A private account (spec 0010): its space is named "private", its role is "private". */
```

replace

```ts
  roles: ['private'],
  permissions: ['notes:read', 'notes:write'],
};
```

with

```ts
  roles: ['private'],
  permissions: ['notes:read', 'notes:write'],
  has_password: true,
  linked_providers: [],
};

/** A private account made through Google (spec 0011): no password, Google signs it in. */
export const providerMe: Me = {
  ...privateMe,
  sub: '55555555-5555-5555-5555-555555555555',
  email: 'ola@gmail.example',
  has_password: false,
  linked_providers: ['google'],
};
```

(`viewerMe` spreads `adminMe` and so has the two members.)

In `samples/notes-web/src/styles.css`, after the block `button.link { ... }` (its closing `}`) add

```css

/* The provider buttons (spec 0011) are links that look like buttons: a full page navigation, not a call. */
ul.providers a {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 44px;
  margin-bottom: 0.5rem;
  border: 1px solid var(--accent);
  border-radius: 6px;
  text-decoration: none;
}

.or {
  text-align: center;
  color: var(--muted);
}
```

- [ ] **Step 6: The buttons.** `samples/notes-web/src/app/pages/provider-buttons.ts`:

```ts
import { Component, input } from '@angular/core';
import { ProviderName, isProviderName, providerStartUrl } from '../account-api';
import { texts } from '../texts';

const PROVIDER_KEY = 'notes-web.provider';

/**
 * Keeps the provider the person chose, the name only, for the return screen ("connect Google"): the fragment that comes back does not
 * name it. Storage that is off or full changes nothing but that sentence.
 */
export function rememberProvider(name: ProviderName): void {
  try {
    sessionStorage.setItem(PROVIDER_KEY, name);
  } catch {
    // the return screen then says "Google or Microsoft"
  }
}

/** The provider of the last button this tab followed, or null. */
export function rememberedProvider(): ProviderName | null {
  try {
    const name = sessionStorage.getItem(PROVIDER_KEY);
    return isProviderName(name) ? name : null;
  } catch {
    return null;
  }
}

/** The provider's name for a sentence, or "Google or Microsoft" when it is not known. */
export function providerTitle(name: ProviderName | null): string {
  return name === null ? texts.providers.either : (texts.providers.names[name] ?? texts.providers.either);
}

/**
 * The buttons of spec 0011: one link per offered provider to Auth-Core's start, in the order of GET /auth/options. A link and not a call,
 * because the browser leaves for the provider. Nothing at all is drawn without a provider.
 */
@Component({
  selector: 'app-provider-buttons',
  template: `
    @if (providers().length > 0) {
      <ul class="plain providers" data-testid="provider-buttons">
        @for (name of providers(); track name) {
          <li>
            <a [href]="hrefOf(name)" [attr.data-testid]="'provider-' + name" (click)="chose(name)">{{ labelOf(name) }}</a>
          </li>
        }
      </ul>
      @if (separator()) {
        <p class="or" data-testid="provider-or">{{ t.or }}</p>
      }
    }
  `,
})
export class ProviderButtons {
  protected readonly t = texts.providers;
  readonly providers = input.required<readonly ProviderName[]>();
  readonly action = input<'continue' | 'signup' | 'accept' | 'confirm'>('continue');
  /** The invitation token of /invite: the start carries it, so that the round trip accepts the invitation. */
  readonly invite = input<string | null>(null);
  /** The confirmation of a deletion: the start asks for purpose=reauth. */
  readonly reauth = input(false);
  /** "or" after the buttons, before a form. */
  readonly separator = input(true);

  protected hrefOf(name: ProviderName): string {
    const invite = this.invite();
    if (this.reauth()) {
      return providerStartUrl(name, { reauth: true });
    }
    return providerStartUrl(name, invite === null ? null : { invite });
  }

  protected labelOf(name: ProviderName): string {
    const title = providerTitle(name);
    switch (this.action()) {
      case 'signup':
        return this.t.signUpWith(title);
      case 'accept':
        return this.t.acceptWith(title);
      case 'confirm':
        return this.t.confirmWith(title);
      default:
        return this.t.continueWith(title);
    }
  }

  protected chose(name: ProviderName): void {
    rememberProvider(name);
  }
}
```

- [ ] **Step 7: The three screens.** `samples/notes-web/src/app/pages/login.ts`:

(a) replace

```ts
import { AccountApi } from '../account-api';
```

with

```ts
import { AccountApi, ProviderName } from '../account-api';
```

and replace

```ts
import { requiredText } from './required-text';
```

with

```ts
import { ProviderButtons } from './provider-buttons';
import { requiredText } from './required-text';
```

(b) replace

```ts
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @if (notice(); as text) {
        <p class="status" role="status" data-testid="login-notice">{{ text }}</p>
      }
      <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
```

with

```ts
  imports: [ReactiveFormsModule, RouterLink, ProviderButtons],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @if (notice(); as text) {
        <p class="status" role="status" data-testid="login-notice">{{ text }}</p>
      }
      <app-provider-buttons [providers]="providers()" />
      <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
```

(c) replace

```ts
  protected readonly signupOpen = signal(false);

  ngOnInit(): void {
    void this.account
      .options()
      .then((options) => this.signupOpen.set(options.signup === 'self-service' || options.private_signup === 'self-service'));
  }
```

with

```ts
  protected readonly signupOpen = signal(false);
  /** The offered providers (spec 0011): their buttons come first. */
  protected readonly providers = signal<ProviderName[]>([]);

  ngOnInit(): void {
    void this.account.options().then((options) => {
      this.signupOpen.set(options.signup === 'self-service' || options.private_signup === 'self-service');
      this.providers.set(options.providers);
    });
  }
```

`samples/notes-web/src/app/pages/signup.ts`:

(a) replace

```ts
import { AccountApi, SignupKind } from '../account-api';
import { texts } from '../texts';
import { requiredText } from './required-text';
```

with

```ts
import { AccountApi, ProviderName, SignupKind } from '../account-api';
import { texts } from '../texts';
import { ProviderButtons } from './provider-buttons';
import { requiredText } from './required-text';
```

(b) replace

```ts
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @if (closed()) {
        <p class="error" role="alert" data-testid="signup-closed">{{ t.closed }}</p>
      } @else if (ready()) {
        <p>{{ kind() === 'private' ? t.introPrivate : t.intro }}</p>
```

with

```ts
  imports: [ReactiveFormsModule, RouterLink, ProviderButtons],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @if (closed()) {
        <p class="error" role="alert" data-testid="signup-closed">{{ t.closed }}</p>
      } @else if (ready()) {
        <app-provider-buttons [providers]="providers()" action="signup" />
        <p>{{ kind() === 'private' ? t.introPrivate : t.intro }}</p>
```

(c) replace

```ts
  protected readonly ready = signal(false);

  ngOnInit(): void {
```

with

```ts
  protected readonly ready = signal(false);
  /** The offered providers (spec 0011): "Sign up with ..." comes before the form. */
  protected readonly providers = signal<ProviderName[]>([]);

  ngOnInit(): void {
```

and replace

```ts
      this.choice.set(organization && privatePerson);
```

with

```ts
      this.choice.set(organization && privatePerson);
      this.providers.set(options.providers);
```

`samples/notes-web/src/app/pages/invite.ts`:

(a) replace

```ts
import { AccountApi, InvitePreview, Outcome } from '../account-api';
import { texts } from '../texts';
import { PasswordRules } from './password-rules';
```

with

```ts
import { AccountApi, InvitePreview, Outcome, ProviderName } from '../account-api';
import { texts } from '../texts';
import { PasswordRules } from './password-rules';
import { ProviderButtons } from './provider-buttons';
```

(b) replace

```ts
  imports: [ReactiveFormsModule, PasswordRules],
```

with

```ts
  imports: [ReactiveFormsModule, PasswordRules, ProviderButtons],
```

(c) replace

```ts
            } @else {
              <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
```

with

```ts
            } @else {
              <!-- Spec 0011: the invitation can be accepted with a provider; the token rides on the start, never in this page's URL. -->
              <app-provider-buttons [providers]="providers()" action="accept" [invite]="token" />
              <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
```

(d) replace

```ts
  private readonly token = takeTokenFromUrl(inject(ActivatedRoute), inject(Router));
```

with

```ts
  protected readonly token = takeTokenFromUrl(inject(ActivatedRoute), inject(Router));
```

(e) replace

```ts
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    if (this.token !== null) {
      void this.preview(this.token);
    }
  }
```

with

```ts
  protected readonly error = signal<string | null>(null);
  /** The offered providers (spec 0011): "Accept with ..." for an address without a private account. */
  protected readonly providers = signal<ProviderName[]>([]);

  ngOnInit(): void {
    if (this.token !== null) {
      void this.account.options().then((options) => this.providers.set(options.providers));
      void this.preview(this.token);
    }
  }
```

- [ ] **Step 8: Run the tests and the build.** From `samples/notes-web`: `npm test` passes (all specs, the earlier ones included); `npx ng build`
ends without an error or a warning. If an earlier spec fails because it flushes a `/auth/me` body without the two new members and compares
the result with a helper object, add the two members to that body (`has_password: true, linked_providers: []`) and list the spec in the
hand-back; change no expectation.

- [ ] **Step 9: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files (CRLF kept; texts plain ASCII). Proposed
subject: `feat(sample): the provider buttons on sign-in, sign-up and invitation`.

---

## Day 6 — Thursday 30.04: the sample's provider screens and the Playwright spec

When the day is done, the sample handles every answer of a round trip on its own screens, a person without a password deletes a private
account with the provider, and Playwright drives all of it in Chromium and WebKit against the fake.

### Task 17: The sample: `/provider-return`, `/provider-link`, `/provider-finish`

**Files:**
- Modify: `samples/notes-web/src/app/auth/auth.service.ts`, `samples/notes-web/src/app/app.routes.ts`, `samples/notes-web/src/app/texts.ts`
- Create: `samples/notes-web/src/app/pages/provider-return.ts`, `samples/notes-web/src/app/pages/provider-link.ts`,
  `samples/notes-web/src/app/pages/provider-finish.ts`
- Modify (tests): `samples/notes-web/src/app/app.routes.spec.ts`
- Create (tests): `samples/notes-web/src/app/auth/auth.service.provider-flows.spec.ts`, `samples/notes-web/src/app/pages/provider-return.spec.ts`,
  `samples/notes-web/src/app/pages/provider-link.spec.ts`, `samples/notes-web/src/app/pages/provider-finish.spec.ts`

**Interfaces:**
- Consumes: `ProviderName`, `isProviderName`, `rememberedProvider`, `providerTitle`, the `Failure` kinds (Task 16); `POST /auth/providers/exchange`,
  `/link`, `/finish` (Tasks 8 to 11).
- Produces: `AuthService.exchangeTicket(ticket: string): Promise<ExchangeResult>`, `AuthService.linkProvider(ticket: string, password: string):
  Promise<LoginResult>`, `AuthService.finishWithProvider(ticket: string, kind: 'organization' | 'private', companyName: string | null):
  Promise<LoginResult>`, the types `ProviderNext` and `ExchangeResult`; the routes `provider-return`, `provider-link`, `provider-finish`; the
  navigation state `{ reauthTicket: string }` that `/provider-return` hands the notes screen (Task 18 reads it); `texts.providerReturn`,
  `texts.providerLink`, `texts.providerFinish`.

**Contract counts:** none.

**Decisions the plan takes here:**
- `/provider-return` reads the fragment in its constructor, then first replaces the address with `/provider-return` (no fragment, no new
  history entry) and only then calls Auth-Core: the ticket is never in the address bar while a request runs. Every way out is a navigation
  with `replaceUrl`, so the back button never returns to a used ticket.
- The ticket, the address and the kinds travel in the router's navigation state only, never in a URL or in storage. A link or finish screen
  opened without that state (a typed address, another tab) goes to `/login`. The router keeps the navigation state in the browser's history
  entry (`history.state`), so a reload of that entry brings it back: harmless, because the ticket is single use, bound to `auth_pt` and lives
  10 minutes (5 for `reauth`). The tests that open a screen "without its state" cover a typed address.
- An unknown `error` code reads as `provider_error`; a fragment without a ticket or with an unknown `purpose` is "no fragment": `/login`.
- A `429` at the exchange keeps the ticket in memory and shows "Try again" (the bar says how long), as the invitation screen does.

- [ ] **Step 1: Write the failing tests.**

`samples/notes-web/src/app/auth/auth.service.provider-flows.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { privateMe, providerMe, settle } from '../../testing/helpers';
import { AuthService } from './auth.service';

const status = (code: number) => ({ status: code, statusText: String(code) });

describe('AuthService: the provider calls (spec 0011)', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => ctrl.verify());

  it('the exchange that signs in starts a session, as a login does', async () => {
    const done = auth.exchangeTicket('t-1');
    const request = ctrl.expectOne('/auth/providers/exchange');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ ticket: 't-1' });
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({ status: 'authenticated', access_token: 'tok-p' });
    await settle();
    ctrl.expectOne('/auth/me').flush(providerMe);
    expect(await done).toEqual({ ok: true, next: null });
    expect(auth.token()).toBe('tok-p');
    expect(auth.me()).toEqual(providerMe);
  });

  it('reads next link and next finish_signup, and starts no session', async () => {
    const link = auth.exchangeTicket('t-1');
    ctrl.expectOne('/auth/providers/exchange').flush({ next: 'link', email: 'a@b.example', email_verified: false });
    expect(await link).toEqual({ ok: true, next: { next: 'link', email: 'a@b.example', emailVerified: false } });
    const finish = auth.exchangeTicket('t-2');
    ctrl.expectOne('/auth/providers/exchange').flush({ next: 'finish_signup', email: 'a@b.example', kinds: ['private', 'organization', 'x'] });
    expect(await finish).toEqual({ ok: true, next: { next: 'finish_signup', email: 'a@b.example', kinds: ['organization', 'private'] } });
    expect(auth.token()).toBeNull();
  });

  it.each([[{ next: 'link', email: 'a@b.example' }], [{ next: 'finish_signup', email: 'a@b.example', kinds: [] }], [{ next: 'other' }], ['<html>']])(
    'an answer %j that is neither is other',
    async (body) => {
      const done = auth.exchangeTicket('t-1');
      ctrl.expectOne('/auth/providers/exchange').flush(body);
      expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
    },
  );

  it('a refusal is a failure and leaves the session alone', async () => {
    const done = auth.exchangeTicket('t-1');
    ctrl.expectOne('/auth/providers/exchange').flush({ error: 'invalid_ticket' }, status(400));
    expect(await done).toEqual({ ok: false, failure: { kind: 'invalid_ticket' } });
  });

  it('the link sends the ticket and the password and signs in', async () => {
    const done = auth.linkProvider('t-1', 'Pw-1 x');
    const request = ctrl.expectOne('/auth/providers/link');
    expect(request.request.body).toEqual({ ticket: 't-1', password: 'Pw-1 x' });
    request.flush({ status: 'authenticated', access_token: 'tok-l' });
    await settle();
    ctrl.expectOne('/auth/me').flush(privateMe);
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBe('tok-l');
  });

  it('the finish sends the kind, and the company name only for an organization', async () => {
    const organization = auth.finishWithProvider('t-1', 'organization', 'Firma');
    const first = ctrl.expectOne('/auth/providers/finish');
    expect(first.request.body).toEqual({ ticket: 't-1', kind: 'organization', company_name: 'Firma' });
    first.flush({ error: 'already_registered' }, status(409));
    expect(await organization).toEqual({ ok: false, failure: { kind: 'already_registered' } });
    const privatePerson = auth.finishWithProvider('t-2', 'private', null);
    const second = ctrl.expectOne('/auth/providers/finish');
    expect(second.request.body).toEqual({ ticket: 't-2', kind: 'private' });
    second.flush({ error: 'invalid_request' }, status(400));
    expect(await privatePerson).toEqual({ ok: false, failure: { kind: 'invalid_request' } });
  });
});
```

`samples/notes-web/src/app/pages/provider-return.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, inject } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, providerMe, realLocation, settle } from '../../testing/helpers';
import { rememberProvider } from './provider-buttons';
import { ProviderReturnPage } from './provider-return';

const status = (code: number) => ({ status: code, statusText: String(code) });
const TICKET = 'TICKET-abcdefghijklmnopqrstuvwxyz-0123456789A';

/** The screens the return screen sends the person to; each keeps the navigation state it was opened with. */
@Component({ template: '<p>landed</p>' })
class Landing {
  static state: Record<string, unknown> | undefined;
  constructor() {
    Landing.state = inject(Router).currentNavigation()?.extras.state;
  }
}

async function open(fragment: string | null) {
  Landing.state = undefined;
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'provider-return', component: ProviderReturnPage },
        { path: 'notes', component: Landing },
        { path: 'login', component: Landing },
        { path: 'provider-link', component: Landing },
        { path: 'provider-finish', component: Landing },
      ]),
      provideHttpClient(),
      provideHttpClientTesting(),
      realLocation,
    ],
  });
  const url = fragment === null ? '/provider-return' : `/provider-return#${fragment}`;
  history.replaceState(null, '', url);
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url);
  await settle(harness.fixture);
  await settle(harness.fixture); // the navigation that removes the fragment lands first
  return { harness, ctrl: TestBed.inject(HttpTestingController), router: TestBed.inject(Router) };
}

function stored(): string {
  return JSON.stringify({ ...sessionStorage }) + JSON.stringify({ ...localStorage });
}

describe('ProviderReturnPage (spec 0011)', () => {
  afterEach(() => {
    history.replaceState(null, '', '/');
    sessionStorage.clear();
  });

  it('removes the fragment from the address bar and the router before it calls Auth-Core', async () => {
    const { ctrl, router } = await open(`ticket=${TICKET}&purpose=sign_in`);
    expect(window.location.hash).toBe('');
    expect(window.location.href).not.toContain(TICKET);
    expect(router.url).toBe('/provider-return');
    const request = ctrl.expectOne('/auth/providers/exchange');
    expect(request.request.body).toEqual({ ticket: TICKET });
    expect(stored()).not.toContain(TICKET);
  });

  it('signed in: the notes open, replacing this screen', async () => {
    const { harness, ctrl, router } = await open(`ticket=${TICKET}&purpose=sign_in`);
    ctrl.expectOne('/auth/providers/exchange').flush({ status: 'authenticated', access_token: 'tok-p' });
    await settle();
    ctrl.expectOne('/auth/me').flush(providerMe);
    await settle(harness.fixture);
    await settle(harness.fixture);
    expect(router.url).toBe('/notes');
    expect(window.location.pathname).toBe('/notes');
  });

  it('an invitation round trip is exchanged too', async () => {
    const { ctrl } = await open(`ticket=${TICKET}&purpose=invite`);
    expect(ctrl.expectOne('/auth/providers/exchange').request.body).toEqual({ ticket: TICKET });
  });

  it('next link: /provider-link with the ticket, the address, whether it is verified and the provider, in the navigation state only', async () => {
    rememberProvider('microsoft');
    const { harness, ctrl, router } = await open(`ticket=${TICKET}&purpose=sign_in`);
    ctrl.expectOne('/auth/providers/exchange').flush({ next: 'link', email: 'anna@firma.example', email_verified: true });
    await settle(harness.fixture);
    await settle(harness.fixture);
    expect(router.url).toBe('/provider-link');
    expect(Landing.state).toMatchObject({ ticket: TICKET, email: 'anna@firma.example', emailVerified: true, provider: 'microsoft' });
    expect(window.location.href).not.toContain(TICKET);
    expect(stored()).not.toContain(TICKET);
  });

  it('next finish_signup: /provider-finish with the ticket, the address and the kinds', async () => {
    const { harness, ctrl, router } = await open(`ticket=${TICKET}&purpose=sign_in`);
    ctrl.expectOne('/auth/providers/exchange').flush({ next: 'finish_signup', email: 'new@gmail.example', kinds: ['organization', 'private'] });
    await settle(harness.fixture);
    await settle(harness.fixture);
    expect(router.url).toBe('/provider-finish');
    expect(Landing.state).toMatchObject({ ticket: TICKET, email: 'new@gmail.example', kinds: ['organization', 'private'] });
  });

  it('reauth: the notes screen with the ticket in the navigation state, and no call', async () => {
    const { ctrl, router } = await open(`ticket=${TICKET}&purpose=reauth`);
    ctrl.expectNone('/auth/providers/exchange');
    expect(router.url).toBe('/notes');
    expect(Landing.state).toMatchObject({ reauthTicket: TICKET });
  });

  it.each([null, 'purpose=sign_in', `ticket=${TICKET}&purpose=other`, `ticket=${TICKET}`])('%s: /login', async (fragment) => {
    const { ctrl, router } = await open(fragment);
    ctrl.expectNone('/auth/providers/exchange');
    expect(router.url).toBe('/login');
  });

  it.each([
    ['cancelled', 'Sign-in was cancelled.'],
    ['invalid_state', 'The sign-in took too long or was started in another browser. Try again.'],
    ['provider_unavailable', 'This way to sign in is not available.'],
    ['provider_error', 'Google or Microsoft could not sign you in. Try again.'],
    ['something_new', 'Google or Microsoft could not sign you in. Try again.'],
    ['constructor', 'Google or Microsoft could not sign you in. Try again.'],
    ['__proto__', 'Google or Microsoft could not sign you in. Try again.'],
    ['toString', 'Google or Microsoft could not sign you in. Try again.'],
  ])('error=%s says "%s" with a link back to sign in', async (code, text) => {
    const { harness, ctrl } = await open(`error=${code}&purpose=sign_in`);
    ctrl.expectNone('/auth/providers/exchange');
    expect(pageText(harness.fixture)).toContain(text);
    expect(has(harness.fixture, 'a[href="/login"]')).toBe(true);
    expect(window.location.hash).toBe('');
  });

  it.each([
    [400, 'invalid_ticket', 'This sign-in took too long. Start again.'],
    [403, 'no_account', 'You have no account. Ask your company for an invitation.'],
    [403, 'no_email', 'Your Microsoft or Google account has no email address we can use. Sign in with your password.'],
    [409, 'already_registered', 'An account with this address exists. Sign in with your password.'],
    [403, 'address_mismatch', 'Use the Google or Microsoft account of the invited address, or open the invitation link again and choose a password.'],
    [409, 'already_member', 'This account already belongs to a company.'],
    [409, 'private_account_exists', 'You have a private account with this address. Open the invitation link again to move it into the company.'],
    [409, 'provider_already_linked', 'This Google or Microsoft account already signs in to another account.'],
    [400, 'invalid_token', 'This invitation has expired or was already used. Ask for a new one.'],
    [403, 'no_membership', 'Your account does not belong to a company.'],
  ])('the exchange answers %s %s: "%s"', async (code, error, text) => {
    const { harness, ctrl } = await open(`ticket=${TICKET}&purpose=invite`);
    ctrl.expectOne('/auth/providers/exchange').flush({ error }, status(code));
    await settle(harness.fixture);
    expect(pageText(harness.fixture)).toContain(text);
    expect(has(harness.fixture, 'a[href="/login"]')).toBe(true);
  });

  it('a 429 keeps the ticket in memory and offers "Try again", which asks again with it', async () => {
    const { harness, ctrl } = await open(`ticket=${TICKET}&purpose=sign_in`);
    ctrl.expectOne('/auth/providers/exchange').flush({ error: 'too_many_requests', retry_after_seconds: 3 }, status(429));
    await settle(harness.fixture);
    (harness.fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="try-again"]')!.click();
    expect(ctrl.expectOne('/auth/providers/exchange').request.body).toEqual({ ticket: TICKET });
  });
});
```

`samples/notes-web/src/app/pages/provider-link.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, privateMe, settle, submitForm, typeInto } from '../../testing/helpers';
import { ProviderLinkPage } from './provider-link';

const status = (code: number) => ({ status: code, statusText: String(code) });

@Component({ template: '<p>elsewhere</p>' })
class Elsewhere {}

async function open(state: Record<string, unknown> | null) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'start', component: Elsewhere },
        { path: 'provider-link', component: ProviderLinkPage },
        { path: 'login', component: Elsewhere },
        { path: 'notes', component: Elsewhere },
      ]),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  });
  const harness = await RouterTestingHarness.create('/start');
  const router = TestBed.inject(Router);
  await harness.navigateByUrl('/start');
  await router.navigateByUrl('/provider-link', state === null ? {} : { state });
  await settle(harness.fixture);
  return { harness, router, ctrl: TestBed.inject(HttpTestingController) };
}

const verified = { ticket: 't-1', email: 'anna@firma.example', emailVerified: true, provider: 'google' };

async function link(opened: Awaited<ReturnType<typeof open>>, password: string) {
  typeInto(opened.harness.fixture, '#password', password);
  submitForm(opened.harness.fixture);
  await settle(opened.harness.fixture);
}

describe('ProviderLinkPage (spec 0011)', () => {
  it('says which account and which provider, with the password, "Connect and sign in" and "Forgot your password?"', async () => {
    const { harness } = await open(verified);
    expect(pageText(harness.fixture)).toContain(
      'An account with anna@firma.example exists. Enter its password once to connect Google. After that Google alone signs you in.',
    );
    expect(pageText(harness.fixture)).toContain('Connect and sign in');
    expect(has(harness.fixture, 'a[href="/forgot"]')).toBe(true);
    expect(has(harness.fixture, 'a[href="/signup"]')).toBe(false);
  });

  it('an unverified address: the other sentence and the way to sign up', async () => {
    const { harness } = await open({ ...verified, emailVerified: false, provider: 'microsoft' });
    expect(pageText(harness.fixture)).toContain(
      'If you have an account with anna@firma.example, enter its password once to connect Microsoft. If you do not, create an account with your email address.',
    );
    expect(has(harness.fixture, 'a[href="/signup"]')).toBe(true);
  });

  it('without a remembered provider it says "Google or Microsoft"', async () => {
    const { harness } = await open({ ...verified, provider: null });
    expect(pageText(harness.fixture)).toContain('to connect Google or Microsoft');
  });

  it('the right password signs in and opens the notes', async () => {
    const opened = await open(verified);
    await link(opened, 'Pw-1 x');
    const request = opened.ctrl.expectOne('/auth/providers/link');
    expect(request.request.body).toEqual({ ticket: 't-1', password: 'Pw-1 x' });
    request.flush({ status: 'authenticated', access_token: 'tok-l' });
    await settle();
    opened.ctrl.expectOne('/auth/me').flush(privateMe);
    await settle(opened.harness.fixture);
    await settle(opened.harness.fixture);
    expect(opened.router.url).toBe('/notes');
  });

  it.each([
    [401, { error: 'invalid_credentials' }, "Wrong password. No password yet? Use 'Forgot your password?' to set one."],
    [429, { error: 'too_many_attempts', retry_after_seconds: 90 }, 'Too many attempts. Try again in 2 minutes.'],
    [400, { error: 'invalid_ticket' }, 'This sign-in took too long. Start again.'],
    [403, { error: 'email_not_verified' }, 'Your email address is not confirmed yet.'],
    [403, { error: 'no_membership' }, 'Your account does not belong to a company.'],
    [500, 'oops', 'Something went wrong. Try again.'],
  ])('%s %j says "%s"', async (code, body, text) => {
    const opened = await open(verified);
    await link(opened, 'Pw-1 x');
    opened.ctrl.expectOne('/auth/providers/link').flush(body, status(code));
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).toContain(text);
    expect(opened.router.url).toBe('/provider-link');
  });

  it.each([null, { email: 'a@b.example' }, { ...verified, ticket: '' }])('opened without its state (%j): /login', async (state) => {
    const { router } = await open(state);
    expect(router.url).toBe('/login');
  });
});
```

`samples/notes-web/src/app/pages/provider-finish.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { clickOn, has, pageText, privateMe, settle, submitForm, typeInto, valueOf } from '../../testing/helpers';
import { ProviderFinishPage } from './provider-finish';

const status = (code: number) => ({ status: code, statusText: String(code) });

@Component({ template: '<p>elsewhere</p>' })
class Elsewhere {}

async function open(state: Record<string, unknown> | null) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'start', component: Elsewhere },
        { path: 'provider-finish', component: ProviderFinishPage },
        { path: 'login', component: Elsewhere },
        { path: 'notes', component: Elsewhere },
      ]),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  });
  const harness = await RouterTestingHarness.create('/start');
  const router = TestBed.inject(Router);
  await router.navigateByUrl('/provider-finish', state === null ? {} : { state });
  await settle(harness.fixture);
  return { harness, router, ctrl: TestBed.inject(HttpTestingController) };
}

const both = { ticket: 't-1', email: 'new@gmail.example', kinds: ['organization', 'private'] };

describe('ProviderFinishPage (spec 0011)', () => {
  it('shows the address read-only, the choice when both kinds are on, organization first, with the company name', async () => {
    const { harness } = await open(both);
    expect(pageText(harness.fixture)).toContain('Finish creating your account');
    expect(valueOf(harness.fixture, '#email')).toBe('new@gmail.example');
    expect((harness.fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#email')?.readOnly).toBe(true);
    expect(has(harness.fixture, '[data-testid="finish-kind"]')).toBe(true);
    expect(has(harness.fixture, '#company')).toBe(true);
    expect(has(harness.fixture, '#password')).toBe(false);
  });

  it.each([
    [['private'], false, false],
    [['organization'], false, true],
  ])('kinds %j: no choice; company name %s', async (kinds, choice, company) => {
    const { harness } = await open({ ...both, kinds });
    expect(has(harness.fixture, '[data-testid="finish-kind"]')).toBe(choice);
    expect(has(harness.fixture, '#company')).toBe(company);
  });

  it('an organization sends its name and opens the notes', async () => {
    const opened = await open(both);
    typeInto(opened.harness.fixture, '#company', '  Firma  ');
    submitForm(opened.harness.fixture);
    await settle(opened.harness.fixture);
    const request = opened.ctrl.expectOne('/auth/providers/finish');
    expect(request.request.body).toEqual({ ticket: 't-1', kind: 'organization', company_name: 'Firma' });
    request.flush({ status: 'authenticated', access_token: 'tok-f' });
    await settle();
    opened.ctrl.expectOne('/auth/me').flush(privateMe);
    await settle(opened.harness.fixture);
    await settle(opened.harness.fixture);
    expect(opened.router.url).toBe('/notes');
  });

  it('a private person sends no company name', async () => {
    const opened = await open(both);
    clickOn(opened.harness.fixture, '[data-testid="finish-private"]');
    opened.harness.fixture.detectChanges();
    expect(has(opened.harness.fixture, '#company')).toBe(false);
    submitForm(opened.harness.fixture);
    await settle(opened.harness.fixture);
    expect(opened.ctrl.expectOne('/auth/providers/finish').request.body).toEqual({ ticket: 't-1', kind: 'private' });
  });

  it('an organization without a name is not sent', async () => {
    const opened = await open(both);
    submitForm(opened.harness.fixture);
    await settle(opened.harness.fixture);
    opened.ctrl.expectNone('/auth/providers/finish');
    expect(pageText(opened.harness.fixture)).toContain('Enter the name of your company.');
  });

  it.each([
    [409, 'already_registered', 'An account with this address already exists. Sign in with your password.'],
    [403, 'signup_disabled', 'Sign-up is closed. To join a company, ask it for an invitation.'],
    [400, 'invalid_ticket', 'This sign-in took too long. Start again.'],
    [400, 'invalid_request', 'Check the company name.'],
  ])('%s %s says "%s"', async (code, error, text) => {
    const opened = await open(both);
    typeInto(opened.harness.fixture, '#company', 'Firma');
    submitForm(opened.harness.fixture);
    await settle(opened.harness.fixture);
    opened.ctrl.expectOne('/auth/providers/finish').flush({ error }, status(code));
    await settle(opened.harness.fixture);
    expect(pageText(opened.harness.fixture)).toContain(text);
  });

  it.each([null, { ...both, kinds: [] }, { ticket: 't-1', email: 'a@b.example' }])('opened without its state (%j): /login', async (state) => {
    const { router } = await open(state);
    expect(router.url).toBe('/login');
  });
});
```

In `samples/notes-web/src/app/app.routes.spec.ts`, after the test `'%s needs no token'` (its closing `);`) add

```ts

  it.each(['/provider-return', '/provider-link', '/provider-finish'])(
    '%s needs no token, and without a fragment or a navigation state it opens /login (spec 0011)',
    async (url) => {
      const harness = await RouterTestingHarness.create();
      await harness.navigateByUrl(url);
      await settle(harness.fixture);
      await settle(harness.fixture);
      expect(TestBed.inject(Router).url).toBe('/login');
    },
  );
```

- [ ] **Step 2: See them fail.** `npm test` fails: the pages and the three service methods do not exist.

- [ ] **Step 3: The service.** In `samples/notes-web/src/app/auth/auth.service.ts`:

(a) replace

```ts
export type LoginResult = { ok: true } | { ok: false; failure: Failure };
```

with

```ts
export type LoginResult = { ok: true } | { ok: false; failure: Failure };

/** The next screen of a provider round trip, from POST /auth/providers/exchange (spec 0011). */
export type ProviderNext =
  | { next: 'link'; email: string; emailVerified: boolean }
  | { next: 'finish_signup'; email: string; kinds: ('organization' | 'private')[] };

/** `next: null`: the exchange signed the person in. */
export type ExchangeResult = { ok: true; next: ProviderNext | null } | { ok: false; failure: Failure };

/** A `next` answer of the exchange in the contract's shape, or null. `kinds` keeps the known kinds, organization first. */
function asNext(body: unknown): ProviderNext | null {
  if (!isRecord(body) || typeof body['email'] !== 'string') {
    return null;
  }
  if (body['next'] === 'link' && typeof body['email_verified'] === 'boolean') {
    return { next: 'link', email: body['email'], emailVerified: body['email_verified'] };
  }
  const kinds: unknown = body['kinds'];
  if (body['next'] === 'finish_signup' && Array.isArray(kinds)) {
    const known = (['organization', 'private'] as const).filter((kind) => kinds.includes(kind));
    return known.length === 0 ? null : { next: 'finish_signup', email: body['email'], kinds: known };
  }
  return null;
}
```

(b) after the method `completeSignup` (its closing `  }`) add

```ts

  /**
   * POST /auth/providers/exchange (spec 0011): the ticket of the return screen. Either the person is signed in, as at a login, or the answer
   * names the next screen. The auth_pt cookie goes with the request by itself (same origin); no access token is needed.
   */
  async exchangeTicket(ticket: string): Promise<ExchangeResult> {
    let body: unknown;
    try {
      body = await firstValueFrom(this.http.post<unknown>('/auth/providers/exchange', { ticket }));
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
    if (isRecord(body) && 'next' in body) {
      const next = asNext(body);
      return next === null ? { ok: false, failure: { kind: 'other' } } : { ok: true, next };
    }
    const signedIn = await this.startSession(isRecord(body) ? body : null);
    return signedIn.ok ? { ok: true, next: null } : signedIn;
  }

  /** POST /auth/providers/link: the password of the account once, then the provider alone signs in (spec 0011). */
  async linkProvider(ticket: string, password: string): Promise<LoginResult> {
    let body: LoginBody | null;
    try {
      body = await firstValueFrom(this.http.post<LoginBody | null>('/auth/providers/link', { ticket, password }));
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
    return this.startSession(body);
  }

  /** POST /auth/providers/finish: a new account with the provider's address and no password (spec 0011). */
  async finishWithProvider(ticket: string, kind: 'organization' | 'private', companyName: string | null): Promise<LoginResult> {
    let body: LoginBody | null;
    const request = companyName === null ? { ticket, kind } : { ticket, kind, company_name: companyName };
    try {
      body = await firstValueFrom(this.http.post<LoginBody | null>('/auth/providers/finish', request));
    } catch (error) {
      return { ok: false, failure: formFailureOf(error) };
    }
    return this.startSession(body);
  }
```

- [ ] **Step 4: The texts.** In `samples/notes-web/src/app/texts.ts`, before

```ts
  notes: {
    title: 'Notes',
```

add

```ts
  // Spec 0011: the screen a provider round trip comes back to. The ticket is never shown.
  providerReturn: {
    title: 'Signing in',
    working: 'Signing you in...',
    errors: {
      cancelled: 'Sign-in was cancelled.',
      invalid_state: 'The sign-in took too long or was started in another browser. Try again.',
      provider_unavailable: 'This way to sign in is not available.',
      provider_error: 'Google or Microsoft could not sign you in. Try again.',
    } as Record<string, string>,
    invalidTicket: 'This sign-in took too long. Start again.',
    noAccount: 'You have no account. Ask your company for an invitation.',
    noEmail: 'Your Microsoft or Google account has no email address we can use. Sign in with your password.',
    alreadyRegistered: 'An account with this address exists. Sign in with your password.',
    addressMismatch: 'Use the Google or Microsoft account of the invited address, or open the invitation link again and choose a password.',
    alreadyMember: 'This account already belongs to a company.',
    privateAccountExists: 'You have a private account with this address. Open the invitation link again to move it into the company.',
    providerAlreadyLinked: 'This Google or Microsoft account already signs in to another account.',
    invalidToken: 'This invitation has expired or was already used. Ask for a new one.',
  },

  providerLink: {
    title: 'Connect your account',
    intro: (email: string, provider = 'Google'): string =>
      `An account with ${email} exists. Enter its password once to connect ${provider}. After that ${provider} alone signs you in.`,
    introUnverified: (email: string, provider = 'Microsoft'): string =>
      `If you have an account with ${email}, enter its password once to connect ${provider}. If you do not, create an account with your email address.`,
    createAccount: 'Create an account',
    submit: 'Connect and sign in',
    wrongPassword: "Wrong password. No password yet? Use 'Forgot your password?' to set one.",
  },

  providerFinish: {
    title: 'Finish creating your account',
    submit: 'Create account',
    alreadyRegistered: 'An account with this address already exists. Sign in with your password.',
  },

```

(The defaults of `provider` only serve `texts.spec.ts`, which calls each text with one argument.)

- [ ] **Step 5: The return screen.** `samples/notes-web/src/app/pages/provider-return.ts`:

```ts
import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DEFAULT_RETURN_URL } from '../auth/auth.guard';
import { AuthService, Failure } from '../auth/auth.service';
import { texts } from '../texts';
import { rememberedProvider } from './provider-buttons';

/**
 * Spec 0011: Auth-Core sends the browser here after the provider, with `#ticket=...&purpose=...` or `#error=...&purpose=...`. The fragment is
 * read once, in the constructor; the address is replaced by /provider-return before anything else, and every way out of this screen is a
 * navigation that replaces the history entry: the ticket is never in the address bar while a request runs, never in the history, never in
 * storage. It lives in this component's memory and in the navigation state of the next screen.
 */
@Component({
  selector: 'app-provider-return',
  imports: [RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @if (message(); as text) {
        <p class="error" role="alert" data-testid="provider-message">{{ text }}</p>
        <p><a routerLink="/login" data-testid="provider-back">{{ common.backToSignIn }}</a></p>
      } @else if (limited()) {
        <button type="button" data-testid="try-again" (click)="exchange()">{{ common.tryAgain }}</button>
      } @else {
        <p class="status" role="status">{{ t.working }}</p>
      }
    </main>
  `,
})
export class ProviderReturnPage implements OnInit {
  protected readonly t = texts.providerReturn;
  protected readonly common = texts.common;
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly fragment = new URLSearchParams(this.route.snapshot.fragment ?? '');
  private readonly ticket = this.fragment.get('ticket') ?? '';

  protected readonly message = signal<string | null>(null);
  protected readonly limited = signal(false);

  ngOnInit(): void {
    void this.handle();
  }

  private async handle(): Promise<void> {
    // After the navigation that is building this screen: the same route, no fragment, the history entry replaced. The screen is kept.
    await Promise.resolve();
    try {
      await this.router.navigate([], { relativeTo: this.route, replaceUrl: true });
    } catch {
      // the address keeps the fragment; nothing else changes
    }
    const error = this.fragment.get('error');
    const purpose = this.fragment.get('purpose');
    if (error !== null) {
      // An own member only: the fragment is the visitor's text, and `constructor` or `__proto__` must not reach Object.prototype.
      this.message.set(Object.hasOwn(this.t.errors, error) ? this.t.errors[error] : this.t.errors['provider_error']);
    } else if (this.ticket === '') {
      await this.go('/login');
    } else if (purpose === 'reauth') {
      // The notes screen opens "Delete account" ready to confirm (Task 18 of the plan): the ticket is spent there.
      await this.go(DEFAULT_RETURN_URL, { reauthTicket: this.ticket });
    } else if (purpose === 'sign_in' || purpose === 'invite') {
      await this.exchange();
    } else {
      await this.go('/login');
    }
  }

  protected async exchange(): Promise<void> {
    this.limited.set(false);
    const result = await this.auth.exchangeTicket(this.ticket);
    if (!result.ok) {
      this.show(result.failure);
    } else if (result.next === null) {
      await this.go(DEFAULT_RETURN_URL);
    } else if (result.next.next === 'link') {
      const { email, emailVerified } = result.next;
      await this.go('/provider-link', { ticket: this.ticket, email, emailVerified, provider: rememberedProvider() });
    } else {
      const { email, kinds } = result.next;
      await this.go('/provider-finish', { ticket: this.ticket, email, kinds });
    }
  }

  private async go(url: string, state?: Record<string, unknown>): Promise<void> {
    try {
      await this.router.navigateByUrl(url, { replaceUrl: true, state });
    } catch {
      // the person stays on this screen
    }
  }

  private show(failure: Failure): void {
    const messages: Partial<Record<Failure['kind'], string>> = {
      invalid_ticket: this.t.invalidTicket,
      no_account: this.t.noAccount,
      no_email: this.t.noEmail,
      already_registered: this.t.alreadyRegistered,
      address_mismatch: this.t.addressMismatch,
      already_member: this.t.alreadyMember,
      private_account_exists: this.t.privateAccountExists,
      provider_already_linked: this.t.providerAlreadyLinked,
      invalid_token: this.t.invalidToken,
      no_membership: texts.login.noMembership,
    };
    if (failure.kind === 'too_many_requests') {
      // The bar above the screen says when to try again; the ticket is still good for a few minutes.
      this.limited.set(true);
    } else {
      this.message.set(messages[failure.kind] ?? this.common.somethingWrong);
    }
  }
}
```

- [ ] **Step 6: The link screen.** `samples/notes-web/src/app/pages/provider-link.ts`:

```ts
import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ProviderName, isProviderName } from '../account-api';
import { DEFAULT_RETURN_URL } from '../auth/auth.guard';
import { AuthService } from '../auth/auth.service';
import { texts } from '../texts';
import { providerTitle } from './provider-buttons';

interface LinkState {
  ticket: string;
  email: string;
  emailVerified: boolean;
  provider: ProviderName | null;
}

/** What /provider-return handed on in the navigation state, or null (no navigation state, e.g. a typed address or a new tab): then the screen goes to /login. */
function linkState(router: Router): LinkState | null {
  const state = router.currentNavigation()?.extras.state;
  if (
    state === undefined ||
    typeof state['ticket'] !== 'string' ||
    state['ticket'] === '' ||
    typeof state['email'] !== 'string' ||
    typeof state['emailVerified'] !== 'boolean'
  ) {
    return null;
  }
  const provider: unknown = state['provider'];
  return { ticket: state['ticket'], email: state['email'], emailVerified: state['emailVerified'], provider: isProviderName(provider) ? provider : null };
}

/** Spec 0011: the password of an existing account, once, to connect the provider. */
@Component({
  selector: 'app-provider-link',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @if (link; as state) {
        @if (state.emailVerified) {
          <p data-testid="link-intro">{{ t.intro(state.email, provider) }}</p>
        } @else {
          <p data-testid="link-intro">{{ t.introUnverified(state.email, provider) }}</p>
          <p><a routerLink="/signup" data-testid="link-signup">{{ t.createAccount }}</a></p>
        }
        @if (expired()) {
          <p class="error" role="alert">{{ returnTexts.invalidTicket }}</p>
          <p><a routerLink="/login">{{ common.backToSignIn }}</a></p>
        } @else {
          <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
            <label for="email">{{ common.email }}</label>
            <input id="email" type="email" [value]="state.email" readonly autocomplete="username" />
            <label for="password">{{ common.password }}</label>
            <input id="password" type="password" formControlName="password" autocomplete="current-password" />
            <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
          </form>
          @if (error(); as text) {
            <p class="error" role="alert" data-testid="link-error">{{ text }}</p>
          }
          <p><a routerLink="/forgot">{{ login.forgot }}</a></p>
        }
      }
    </main>
  `,
})
export class ProviderLinkPage implements OnInit {
  protected readonly t = texts.providerLink;
  protected readonly returnTexts = texts.providerReturn;
  protected readonly login = texts.login;
  protected readonly common = texts.common;
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly link = linkState(this.router);
  protected readonly provider = providerTitle(this.link?.provider ?? null);

  protected readonly form = inject(NonNullableFormBuilder).group({ password: ['', Validators.required] });
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly expired = signal(false);

  ngOnInit(): void {
    if (this.link === null) {
      void this.router.navigateByUrl('/login', { replaceUrl: true }).catch(() => undefined);
    }
  }

  protected async submit(): Promise<void> {
    if (this.busy() || this.form.invalid || this.link === null) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const result = await this.auth.linkProvider(this.link.ticket, this.form.controls.password.value);
    if (result.ok) {
      try {
        await this.router.navigateByUrl(DEFAULT_RETURN_URL, { replaceUrl: true });
      } catch {
        // signed in all the same
      }
    } else {
      switch (result.failure.kind) {
        case 'invalid_credentials':
          this.error.set(this.t.wrongPassword);
          break;
        case 'too_many_attempts':
          this.error.set(this.login.tooManyAttempts(result.failure.retryAfterSeconds));
          break;
        case 'too_many_requests':
          // The notice bar above the screen says when to try again: the interceptor put it there.
          break;
        case 'invalid_ticket':
          this.expired.set(true);
          break;
        case 'email_not_verified':
          this.error.set(this.login.notVerified);
          break;
        case 'no_membership':
          this.error.set(this.login.noMembership);
          break;
        case 'no_email':
          this.error.set(this.returnTexts.noEmail);
          break;
        default:
          this.error.set(this.common.somethingWrong);
      }
    }
    this.busy.set(false);
  }
}
```

- [ ] **Step 7: The finish screen.** `samples/notes-web/src/app/pages/provider-finish.ts`:

```ts
import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { SignupKind } from '../account-api';
import { DEFAULT_RETURN_URL } from '../auth/auth.guard';
import { AuthService } from '../auth/auth.service';
import { texts } from '../texts';
import { requiredText } from './required-text';

interface FinishState {
  ticket: string;
  email: string;
  kinds: SignupKind[];
}

function finishState(router: Router): FinishState | null {
  const state = router.currentNavigation()?.extras.state;
  const kinds: unknown = state?.['kinds'];
  if (state === undefined || typeof state['ticket'] !== 'string' || state['ticket'] === '' || typeof state['email'] !== 'string' || !Array.isArray(kinds)) {
    return null;
  }
  const known = (['organization', 'private'] as const).filter((kind) => kinds.includes(kind));
  return known.length === 0 ? null : { ticket: state['ticket'], email: state['email'], kinds: known };
}

/** Spec 0011: a new account with the provider's verified address and no password. */
@Component({
  selector: 'app-provider-finish',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @if (finish; as state) {
        @switch (phase()) {
          @case ('form') {
            <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
              <label for="email">{{ common.email }}</label>
              <input id="email" type="email" [value]="state.email" readonly autocomplete="username" />
              @if (state.kinds.length > 1) {
                <fieldset class="choice" data-testid="finish-kind">
                  <legend>{{ signup.kindLegend }}</legend>
                  <label>
                    <input type="radio" name="kind" data-testid="finish-organization" [checked]="kind() === 'organization'" (change)="choose('organization')" />
                    {{ signup.kindOrganization }}
                  </label>
                  <label>
                    <input type="radio" name="kind" data-testid="finish-private" [checked]="kind() === 'private'" (change)="choose('private')" />
                    {{ signup.kindPrivate }}
                  </label>
                </fieldset>
              }
              @if (kind() === 'organization') {
                <label for="company">{{ signup.companyName }}</label>
                <input id="company" type="text" formControlName="company" maxlength="100" autocomplete="organization" />
              }
              <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
            </form>
            @if (error(); as text) {
              <p class="error" role="alert" data-testid="finish-error">{{ text }}</p>
            }
          }
          @case ('registered') {
            <p class="error" role="alert">{{ t.alreadyRegistered }}</p>
            <p><a routerLink="/login">{{ common.signInLink }}</a></p>
          }
          @case ('closed') {
            <p class="error" role="alert">{{ signup.closed }}</p>
            <p><a routerLink="/login">{{ common.backToSignIn }}</a></p>
          }
          @case ('expired') {
            <p class="error" role="alert">{{ returnTexts.invalidTicket }}</p>
            <p><a routerLink="/login">{{ common.backToSignIn }}</a></p>
          }
        }
      }
    </main>
  `,
})
export class ProviderFinishPage implements OnInit {
  protected readonly t = texts.providerFinish;
  protected readonly signup = texts.signup;
  protected readonly returnTexts = texts.providerReturn;
  protected readonly common = texts.common;
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly finish = finishState(this.router);

  protected readonly form = inject(NonNullableFormBuilder).group({ company: ['', requiredText] });
  protected readonly kind = signal<SignupKind>(this.finish?.kinds[0] ?? 'organization');
  protected readonly phase = signal<'form' | 'registered' | 'closed' | 'expired'>('form');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    if (this.finish === null) {
      void this.router.navigateByUrl('/login', { replaceUrl: true }).catch(() => undefined);
    } else {
      this.choose(this.kind());
    }
  }

  protected choose(kind: SignupKind): void {
    this.kind.set(kind);
    if (kind === 'private') {
      this.form.controls.company.disable();
    } else {
      this.form.controls.company.enable();
    }
    this.error.set(null);
  }

  protected async submit(): Promise<void> {
    if (this.busy() || this.finish === null) {
      return;
    }
    const isPrivate = this.kind() === 'private';
    if (!isPrivate && this.form.invalid) {
      this.error.set(texts.completeSignup.companyRequired);
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const result = await this.auth.finishWithProvider(this.finish.ticket, this.kind(), isPrivate ? null : this.form.controls.company.value.trim());
    if (result.ok) {
      try {
        await this.router.navigateByUrl(DEFAULT_RETURN_URL, { replaceUrl: true });
      } catch {
        // signed in all the same
      }
    } else if (result.failure.kind === 'already_registered') {
      this.phase.set('registered');
    } else if (result.failure.kind === 'signup_disabled') {
      this.phase.set('closed');
    } else if (result.failure.kind === 'invalid_ticket') {
      this.phase.set('expired');
    } else if (result.failure.kind === 'invalid_request') {
      this.error.set(texts.completeSignup.invalidRequest);
    } else if (result.failure.kind !== 'too_many_requests') {
      this.error.set(this.common.somethingWrong);
    }
    this.busy.set(false);
  }
}
```

- [ ] **Step 8: The routes.** In `samples/notes-web/src/app/app.routes.ts` replace

```ts
import { NotesPage } from './pages/notes';
```

with

```ts
import { NotesPage } from './pages/notes';
import { ProviderFinishPage } from './pages/provider-finish';
import { ProviderLinkPage } from './pages/provider-link';
import { ProviderReturnPage } from './pages/provider-return';
```

and replace

```ts
  { path: 'complete-signup', component: CompleteSignupPage, title: texts.completeSignup.title },
```

with

```ts
  { path: 'complete-signup', component: CompleteSignupPage, title: texts.completeSignup.title },
  // Spec 0011: no guard. The return screen needs only its fragment, the other two their navigation state.
  { path: 'provider-return', component: ProviderReturnPage, title: texts.providerReturn.title },
  { path: 'provider-link', component: ProviderLinkPage, title: texts.providerLink.title },
  { path: 'provider-finish', component: ProviderFinishPage, title: texts.providerFinish.title },
```

- [ ] **Step 9: Run the tests and the build.** From `samples/notes-web`: `npm test` passes; `npx ng build` ends without an error or a warning.

- [ ] **Step 10: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files (CRLF kept; texts plain ASCII). Proposed
subject: `feat(sample): the provider return, link and finish screens`.

---

### Task 18: The sample: "Delete account" confirmed with the provider

**Files:**
- Modify: `samples/notes-web/src/app/auth/auth.service.ts`, `samples/notes-web/src/app/pages/notes.ts`, `samples/notes-web/src/app/texts.ts`
- Create (tests): `samples/notes-web/src/app/pages/notes.provider.spec.ts`, `samples/notes-web/src/app/auth/auth.service.reauth.spec.ts`

**Interfaces:**
- Consumes: `Me.has_password`, `Me.linked_providers`, `ProviderButtons`, `providerMe`, the `Failure` kinds `reauth_failed`, `password_required`,
  `reauth_required` (Task 16); the navigation state `{ reauthTicket }` of `/provider-return` (Task 17); `DELETE /auth/account` with
  `provider_ticket` (Task 12).
- Produces: `AuthService.deleteAccountWithTicket(ticket: string): Promise<LoginResult>`; `texts.notes.reauthFailed`.

**Contract counts:** none.

**Decisions the plan takes here:**
- For an account without a password the section holds the warning and one "Confirm with {provider}" link per linked provider (a start with
  `purpose=reauth`); nothing is deleted before the person comes back. Back with the ticket the section opens by itself with the warning and
  "Delete my account"; only that click deletes the notes and then the account (spec 0010, Decision 8, unchanged).
- `reauth_failed` drops the ticket: the section shows the confirm links again ("Confirm again"). `password_required` and `reauth_required`
  drop it too, ask `GET /auth/me` again and draw the section the fresh answer calls for, with "Your notes are already gone.".
- A failed `DELETE /api/me` keeps the ticket: nothing was deleted and the person may press the button again within the five minutes.

- [ ] **Step 1: Write the failing tests.**

`samples/notes-web/src/app/auth/auth.service.reauth.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { providerMe, signedInAs } from '../../testing/helpers';
import { AuthService } from './auth.service';

describe('AuthService.deleteAccountWithTicket (spec 0011)', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
    await signedInAs(auth, ctrl, providerMe);
  });

  afterEach(() => ctrl.verify());

  it('sends DELETE /auth/account with the provider ticket only, and the session ends on 204', async () => {
    const done = auth.deleteAccountWithTicket('r-1');
    const request = ctrl.expectOne('/auth/account');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.body).toEqual({ provider_ticket: 'r-1' });
    request.flush(null, { status: 204, statusText: '204' });
    expect(await done).toEqual({ ok: true });
    expect(auth.token()).toBeNull();
    expect(auth.me()).toBeNull();
  });

  it.each(['reauth_failed', 'password_required', 'reauth_required'])('a 403 %s is a failure, and the session stays', async (code) => {
    const done = auth.deleteAccountWithTicket('r-1');
    ctrl.expectOne('/auth/account').flush({ error: code }, { status: 403, statusText: '403' });
    expect(await done).toEqual({ ok: false, failure: { kind: code } });
    expect(auth.token()).toBe('tok');
  });
});
```

`samples/notes-web/src/app/pages/notes.provider.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { AuthService, Me } from '../auth/auth.service';
import { clickOn, has, pageText, privateMe, providerMe, settle, signedInAs, submitForm, typeInto } from '../../testing/helpers';
import { NotesPage } from './notes';

const status = (code: number) => ({ status: code, statusText: String(code) });
const warning = 'This deletes your account and all your notes. It cannot be undone.';
const reauthFailed =
  'The confirmation did not work or took too long, so your account was not deleted. Your notes are already gone. Confirm again.';

@Component({ template: '' })
class Elsewhere {}

/** The notes screen, opened as /provider-return opens it: with `reauthTicket` in the navigation state, or without. */
async function open(me: Me, reauthTicket: string | null = null) {
  TestBed.configureTestingModule({
    providers: [
      provideRouter([
        { path: 'notes', component: NotesPage },
        { path: 'login', component: Elsewhere },
      ]),
      provideHttpClient(),
      provideHttpClientTesting(),
    ],
  });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const harness = await RouterTestingHarness.create();
  const router = TestBed.inject(Router);
  await router.navigateByUrl('/notes', reauthTicket === null ? {} : { state: { reauthTicket } });
  ctrl.expectOne('/api/notes').flush([]);
  await settle(harness.fixture);
  const navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
  return { fixture: harness.fixture, ctrl, router, navigateByUrl };
}

type Opened = Awaited<ReturnType<typeof open>>;

function links(opened: Opened): (string | null)[] {
  return [...(opened.fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="delete-section"] a')].map((a) => a.getAttribute('href'));
}

/** "Delete my account" with the ticket: the notes service says 204, then Auth-Core answers. */
async function confirmDeletion(opened: Opened): Promise<void> {
  clickOn(opened.fixture, '[data-testid="delete-confirm"]');
  await settle(opened.fixture);
  opened.ctrl.expectOne('/api/me').flush(null, status(204));
  await settle(opened.fixture);
}

describe('NotesPage: "Delete account" without a password (spec 0011)', () => {
  it('shows the warning and "Confirm with Google" to the reauth start, and no password field', async () => {
    const opened = await open(providerMe);
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    expect(pageText(opened.fixture)).toContain(warning);
    expect(pageText(opened.fixture)).toContain('Confirm with Google');
    expect(links(opened)).toEqual(['/auth/providers/google/start?purpose=reauth']);
    expect(has(opened.fixture, '#delete-password')).toBe(false);
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(false);
    opened.ctrl.expectNone('/api/me'); // nothing is deleted before the confirmation
    await settle(opened.fixture);
    // The section opens far from the button: the keyboard and a screen reader follow it to the first confirm link.
    expect((document.activeElement as HTMLElement | null)?.getAttribute('href')).toBe('/auth/providers/google/start?purpose=reauth');
  });

  it('one button per linked provider, in the order of /auth/me', async () => {
    const opened = await open({ ...providerMe, linked_providers: ['google', 'microsoft'] });
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    expect(links(opened)).toEqual(['/auth/providers/google/start?purpose=reauth', '/auth/providers/microsoft/start?purpose=reauth']);
  });

  it('an account with a password keeps the form of spec 0010', async () => {
    const opened = await open({ ...privateMe, linked_providers: ['google'] });
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    expect(has(opened.fixture, '#delete-password')).toBe(true);
    expect(pageText(opened.fixture)).not.toContain('Confirm with');
  });

  it('back with the ticket: the section is open with the warning and "Delete my account", and the ticket is in no URL', async () => {
    const opened = await open(providerMe, 'r-1');
    expect(has(opened.fixture, '[data-testid="delete-section"]')).toBe(true);
    expect(pageText(opened.fixture)).toContain(warning);
    expect(pageText(opened.fixture)).toContain('Delete my account');
    expect(links(opened)).toEqual([]);
    expect(opened.router.url).toBe('/notes');
    await settle(opened.fixture);
    expect((document.activeElement as HTMLElement | null)?.getAttribute('data-testid')).toBe('delete-confirm');
  });

  it('deletes the notes first, then the account with the ticket, and opens /login with the notice', async () => {
    const opened = await open(providerMe, 'r-1');
    clickOn(opened.fixture, '[data-testid="delete-confirm"]');
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/account'); // not before the notes service said 204
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    const request = opened.ctrl.expectOne('/auth/account');
    expect(request.request.method).toBe('DELETE');
    expect(request.request.body).toEqual({ provider_ticket: 'r-1' });
    request.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigateByUrl).toHaveBeenCalledWith('/login', { state: { notice: 'deleted' } });
  });

  it('reauth_failed: the sentence, and the confirm buttons again', async () => {
    const opened = await open(providerMe, 'r-1');
    await confirmDeletion(opened);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'reauth_failed' }, status(403));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(reauthFailed);
    expect(links(opened)).toEqual(['/auth/providers/google/start?purpose=reauth']);
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(false);
  });

  it('password_required: /auth/me is asked again and the password form is drawn', async () => {
    const opened = await open(providerMe, 'r-1');
    await confirmDeletion(opened);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'password_required' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush({ ...providerMe, has_password: true });
    await settle(opened.fixture);
    expect(has(opened.fixture, '#delete-password')).toBe(true);
    expect(pageText(opened.fixture)).toContain('Your notes are already gone.');
  });

  it('reauth_required after a password (a stale /auth/me): /auth/me is asked again and the confirm buttons are drawn', async () => {
    const opened = await open({ ...providerMe, has_password: true });
    clickOn(opened.fixture, '[data-testid="delete-account"]');
    opened.fixture.detectChanges();
    typeInto(opened.fixture, '#delete-password', 'Pw-1 x');
    submitForm(opened.fixture, 'form[data-testid="delete-form"]');
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/me').flush(null, status(204));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/account').flush({ error: 'reauth_required' }, status(403));
    await settle(opened.fixture);
    opened.ctrl.expectOne('/auth/me').flush(providerMe);
    await settle(opened.fixture);
    expect(links(opened)).toEqual(['/auth/providers/google/start?purpose=reauth']);
    expect(has(opened.fixture, '#delete-password')).toBe(false);
  });

  it('a failed DELETE /api/me sends nothing to Auth-Core and keeps the ticket for another try', async () => {
    const opened = await open(providerMe, 'r-1');
    clickOn(opened.fixture, '[data-testid="delete-confirm"]');
    await settle(opened.fixture);
    opened.ctrl.expectOne('/api/me').flush('oops', status(500));
    await settle(opened.fixture);
    opened.ctrl.expectNone('/auth/account');
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(has(opened.fixture, '[data-testid="delete-confirm"]')).toBe(true);
  });
});
```

- [ ] **Step 2: See them fail.** `npm test` fails: no confirm links, no `deleteAccountWithTicket`.

- [ ] **Step 3: The service.** In `samples/notes-web/src/app/auth/auth.service.ts`, after the method `deleteAccount` (its closing `  }`) add

```ts

  /**
   * Spec 0011: the same deletion for an account without a password, confirmed by a provider round trip with purpose=reauth. The ticket is
   * spent by this call; the auth_pt cookie goes with it by itself.
   */
  async deleteAccountWithTicket(ticket: string): Promise<LoginResult> {
    try {
      await firstValueFrom(this.http.delete('/auth/account', { body: { provider_ticket: ticket } }));
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
    this.dropSession();
    return { ok: true };
  }
```

- [ ] **Step 4: The text.** In `samples/notes-web/src/app/texts.ts` replace

```ts
    passwordRequired: 'Enter your password.',
```

with

```ts
    passwordRequired: 'Enter your password.',
    // Spec 0011: the provider confirmation of an account without a password did not hold (wrong account, too late, used).
    reauthFailed:
      'The confirmation did not work or took too long, so your account was not deleted. Your notes are already gone. Confirm again.',
```

- [ ] **Step 5: The screen.** In `samples/notes-web/src/app/pages/notes.ts`:

(a) replace

```ts
import { AuthService, errorCode } from '../auth/auth.service';
import { texts } from '../texts';
```

with

```ts
import { AuthService, LoginResult, errorCode } from '../auth/auth.service';
import { texts } from '../texts';
import { ProviderButtons } from './provider-buttons';
```

(b) after the function `isNote` (its closing `}`) add

```ts

/** The ticket of a confirmation that /provider-return handed on (spec 0011), or null. Only in memory: never in a URL or in storage. */
function reauthTicketOf(router: Router): string | null {
  const ticket: unknown = router.currentNavigation()?.extras.state?.['reauthTicket'];
  return typeof ticket === 'string' && ticket !== '' ? ticket : null;
}
```

(c) replace

```ts
  imports: [ReactiveFormsModule, DatePipe, RouterLink],
```

with

```ts
  imports: [ReactiveFormsModule, DatePipe, RouterLink, ProviderButtons],
```

(d) replace

```ts
          <p class="error" data-testid="delete-warning">{{ t.deleteWarning }}</p>
          <form [formGroup]="deleteForm" (ngSubmit)="deleteAccount()" novalidate data-testid="delete-form">
            <label for="delete-password">{{ password }}</label>
            <input id="delete-password" type="password" formControlName="password" autocomplete="current-password" />
            <button type="submit" data-testid="delete-confirm" [disabled]="deleteBusy()">{{ t.deleteConfirm }}</button>
            <button type="button" data-testid="delete-cancel" [disabled]="deleteBusy()" (click)="cancelDelete()">{{ t.cancel }}</button>
          </form>
```

with

```ts
          <p class="error" data-testid="delete-warning">{{ t.deleteWarning }}</p>
          @if (me()?.has_password !== false) {
            <form [formGroup]="deleteForm" (ngSubmit)="deleteAccount()" novalidate data-testid="delete-form">
              <label for="delete-password">{{ password }}</label>
              <input id="delete-password" type="password" formControlName="password" autocomplete="current-password" />
              <button type="submit" data-testid="delete-confirm" [disabled]="deleteBusy()">{{ t.deleteConfirm }}</button>
              <button type="button" data-testid="delete-cancel" [disabled]="deleteBusy()" (click)="cancelDelete()">{{ t.cancel }}</button>
            </form>
          } @else if (reauthTicket() === null) {
            <!-- Spec 0011: no password to ask for; the provider confirms. Nothing is deleted before the person comes back. -->
            <app-provider-buttons [providers]="me()?.linked_providers ?? []" action="confirm" [reauth]="true" [separator]="false" />
            <button type="button" data-testid="delete-cancel" (click)="cancelDelete()">{{ t.cancel }}</button>
          } @else {
            <button type="button" data-testid="delete-confirm" [disabled]="deleteBusy()" (click)="deleteWithTicket()">{{ t.deleteConfirm }}</button>
            <button type="button" data-testid="delete-cancel" [disabled]="deleteBusy()" (click)="cancelDelete()">{{ t.cancel }}</button>
          }
```

(e) replace

```ts
  // The account turned out to belong to a company while it was being deleted: the page says so, once.
  protected readonly moved = signal(false);
```

with

```ts
  // The account turned out to belong to a company while it was being deleted: the page says so, once.
  protected readonly moved = signal(false);
  // Spec 0011: the ticket of a provider confirmation. Its arrival opens the section ready to confirm.
  protected readonly reauthTicket = signal<string | null>(reauthTicketOf(this.router));
```

(`router` is declared above `me`, so it exists when this field is initialised.)

(f) replace

```ts
  ngOnInit(): void {
    if (this.me() === null) {
      void this.auth.loadMe();
    }
    void this.load();
  }
```

with

```ts
  ngOnInit(): void {
    if (this.me() === null) {
      void this.auth.loadMe();
    }
    void this.load();
    if (this.reauthTicket() !== null) {
      this.deleting.set(true);
      this.focusDeleteSection();
    }
  }
```

(g) replace

```ts
  /** The person keeps the account, so the notes can be added again (the ones that were deleted stay deleted). */
  protected cancelDelete(): void {
    this.deleting.set(false);
    this.notesGone.set(false);
  }
```

with

```ts
  /** The person keeps the account, so the notes can be added again (the ones that were deleted stay deleted). */
  protected cancelDelete(): void {
    this.deleting.set(false);
    this.notesGone.set(false);
    this.reauthTicket.set(null);
  }

  /** Spec 0011: "Delete my account" after a provider confirmation. The notes first, then the account, as with a password. */
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
```

(h) In `deleteAccount`, replace

```ts
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
    // The notes are gone: the page shows it, whatever the account call answers.
    this.added = [];
    this.notes.set([]);
    this.loadFailed.set(false);
    this.authUnavailable.set(false);
    this.loaded.set(true);
    this.notesGone.set(true);
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
```

with

```ts
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

  /**
   * DELETE /api/me, the first half of every deletion (spec 0010, Decision 8). True when the notes are gone; false when nothing was deleted
   * (the person may try again, and the section says so).
   */
  private async deleteNotes(): Promise<boolean> {
    this.deleteBusy.set(true);
    this.deleteError.set(null);
    try {
      await firstValueFrom(this.http.delete('/api/me'));
    } catch (error) {
      // auth_unavailable: the bar says it. Nothing was deleted: the person may try again.
      this.deleteError.set(isAuthUnavailable(error) ? null : this.somethingWrong);
      this.deleteBusy.set(false);
      return false;
    }
    // The notes are gone: the page shows it, whatever the account call answers.
    this.added = [];
    this.notes.set([]);
    this.loadFailed.set(false);
    this.authUnavailable.set(false);
    this.loaded.set(true);
    this.notesGone.set(true);
    return true;
  }

  private async leave(): Promise<void> {
    try {
      await this.router.navigateByUrl('/login', { state: { notice: 'deleted' } });
    } catch {
      // nothing to show: the account is gone and so is the session
    }
  }

  /** The refusals both deletions share, after the notes are gone. */
  private async refused(result: LoginResult & { ok: false }): Promise<void> {
    if (result.failure.kind === 'password_required' || result.failure.kind === 'reauth_required') {
      // Spec 0011: the account is not what this screen thought (a password was set, or it has none): draw the section that fits it now.
      this.reauthTicket.set(null);
      this.deleteForm.reset();
      await this.auth.loadMe();
      this.deleteError.set(this.t.notesGone);
    } else if (result.failure.kind === 'wrong_password') {
      this.deleteError.set(this.t.wrongPassword);
    } else if (result.failure.kind === 'too_many_attempts') {
```

The rest of the old `deleteAccount` body stays as it is and is now the body of `refused` (it ends with `this.deleteBusy.set(false);` and the
closing `  }`). Read the result once: `deleteAccount` now ends right after `await this.refused(result);` with its own `  }`, and `refused`
ends where `deleteAccount` used to.

(i) The section opens far from the button, and for an account without a password it has no password field: the focus goes to the first
thing to press. Replace

```ts
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLInputElement>('#delete-password')?.focus(), { injector: this.injector });
```

with

```ts
    this.focusDeleteSection();
```

and at the end of the class, after the method `load` (the last method; before the class's closing `}`), add

```ts

  /**
   * The keyboard and a screen reader follow the section that opens below the notes: to the password field, else the first "Confirm with ..."
   * link (spec 0011), else "Delete my account" (back from the provider with the ticket).
   */
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
```

- [ ] **Step 6: Run the tests and the build.** From `samples/notes-web`: `npm test` passes, `notes.account.spec.ts` and `notes.spec.ts`
unchanged; `npx ng build` ends without an error or a warning.

- [ ] **Step 7: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files. Proposed subject:
`feat(sample): delete an account without a password with the provider's confirmation`.

---

### Task 19: The Playwright spec `11-providers.spec.ts`

**Files:**
- Create: `samples/notes-web/e2e/11-providers.spec.ts`, `samples/notes-web/e2e/support/fake-provider.ts`

**Interfaces:**
- Consumes: the stack of Task 14 (`scripts/e2e-web.sh` runs it, with the fake at `http://localhost:8099`); the fake's page (Task 4): the heading
  "Sign in to the fake {Google|Microsoft}", the people as links ("Bo, a work account (bo@firma.test, not verified)"), the field "Email", the
  box "The provider verified this address", the button "Sign in", the link "Cancel"; the screens of Tasks 16 to 18; `inviteByApi`,
  `signUpPrivatelyByApi`, `loginStatus`, `waitForLink`, `openMailLink`, `expectCleanAddressBar`, `signOut` (existing support files).
- Produces: criterion 32's guard.

**Contract counts:** none.

**Decisions the plan takes here:**
- One test per sentence of criterion 32, each with its own addresses (`unique(...)`), so that the two browsers and a second run never meet.
  The fake derives the identity from the address it is given (`custom`), so "the button alone" is: the provider button, the fake's page
  (the provider's own step), and no Auth-Core password.
- The unverified work address is the fake's fixed person Bo (`bo@firma.test`): the test only looks at the link screen and links nothing, so
  it changes no state.
- After every round trip the test checks that the address bar holds no ticket: no query, no fragment.

- [ ] **Step 1: The fake's page.** `samples/notes-web/e2e/support/fake-provider.ts`:

```ts
import { Page, expect } from '@playwright/test';

declare const process: { env: Record<string, string | undefined> };

export type FakeProvider = 'Google' | 'Microsoft';

/** The fake OpenID provider of spec 0011 (tests/Auth.FakeOidc/compose.yml): its sign-in page is where the browser lands. */
export function fakeUrl(): string {
  return process.env['E2E_FAKE_URL'] ?? 'http://localhost:8099';
}

async function atFake(page: Page, provider: FakeProvider): Promise<void> {
  expect(page.url().startsWith(`${fakeUrl()}/`), 'the browser is not at the fake provider').toBe(true);
  await expect(page.getByRole('heading', { name: `Sign in to the fake ${provider}` })).toBeVisible();
}

/** Signs in at the fake as the person of this address; `verified` ticks "The provider verified this address". */
export async function signInAtFake(page: Page, provider: FakeProvider, email: string, verified: boolean): Promise<void> {
  await atFake(page, provider);
  await page.getByLabel('Email').fill(email);
  if (verified) {
    await page.getByLabel('The provider verified this address').check();
  }
  await page.getByRole('button', { name: 'Sign in' }).click();
}

/** Chooses one of the fake's fixed people by the text of its link. */
export async function chooseAtFake(page: Page, provider: FakeProvider, person: string): Promise<void> {
  await atFake(page, provider);
  await page.getByRole('link', { name: person }).click();
}

export async function cancelAtFake(page: Page, provider: FakeProvider): Promise<void> {
  await atFake(page, provider);
  await page.getByRole('link', { name: 'Cancel' }).click();
}

/** Back in the app with no ticket in the address bar: no query and no fragment. Fixed messages: the URL is never printed. */
export function expectNoTicketInAddressBar(page: Page): void {
  const url = new URL(page.url());
  expect(url.search === '' && url.hash === '', 'the address bar still holds a query or a fragment').toBe(true);
  expect(page.url().includes('ticket'), 'the address bar holds a ticket').toBe(false);
}
```

- [ ] **Step 2: The spec.** `samples/notes-web/e2e/11-providers.spec.ts`:

```ts
import { Page, expect, test } from '@playwright/test';
import { inviteByApi, loginStatus, signUpPrivatelyByApi } from './support/accounts';
import { setting } from './support/env';
import { cancelAtFake, chooseAtFake, expectNoTicketInAddressBar, signInAtFake } from './support/fake-provider';
import { openMailLink, waitForLink } from './support/mail';
import { expectCleanAddressBar } from './support/page';
import { signOut } from './support/session';

// Spec 0011, criterion 32: the provider buttons against the fake provider, which Auth-Core trusts as its test issuer on this stack.
const MAIL_WAIT_MS = 100_000;

function unique(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1_000_000)}`;
}

/** A new person signs up privately with Google, through the finish screen, and lands on the notes. */
async function signUpWithGoogle(page: Page, email: string): Promise<void> {
  await page.goto('/signup');
  await page.getByRole('link', { name: 'Sign up with Google' }).click();
  await signInAtFake(page, 'Google', email, true);
  await expect(page).toHaveURL(/\/provider-finish$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByRole('heading', { name: 'Finish creating your account' })).toBeVisible();
  await expect(page.getByLabel('Email')).toHaveValue(email);
  await page.locator('label').filter({ hasText: 'Private person' }).click();
  await expect(page.getByLabel('Company name')).toHaveCount(0);
  await page.getByRole('button', { name: 'Create account' }).click();
  await expect(page).toHaveURL(/\/notes$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-private')).toHaveText('Private account');
}

test('a new person signs up privately with Google, signs out, and signs in again with the button alone', async ({ page }) => {
  test.setTimeout(120_000);
  const email = `${unique('google')}@gmail.e2e.test`;

  // The buttons come first on the sign-in screen, in the order of the manifest, then "or" and the form.
  await page.goto('/login');
  const buttons = page.getByTestId('provider-buttons').getByRole('link');
  await expect(buttons).toHaveText(['Continue with Google', 'Continue with Microsoft']);

  await signUpWithGoogle(page, email);
  await signOut(page);

  await page.getByRole('link', { name: 'Continue with Google' }).click();
  await signInAtFake(page, 'Google', email, true);
  await expect(page).toHaveURL(/\/notes$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('me-email')).toHaveText(email);
});

test('a person with a password account links Microsoft with the password, then signs in with the button alone', async ({ page }) => {
  test.setTimeout(240_000);
  const email = `${unique('linker')}@e2e.test`;
  const password = setting('E2E_USER_PASSWORD');
  await signUpPrivatelyByApi(email, password, MAIL_WAIT_MS);

  await page.goto('/login');
  await page.getByRole('link', { name: 'Continue with Microsoft' }).click();
  await signInAtFake(page, 'Microsoft', email, true);
  await expect(page).toHaveURL(/\/provider-link$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('link-intro')).toHaveText(
    `An account with ${email} exists. Enter its password once to connect Microsoft. After that Microsoft alone signs you in.`,
  );
  await page.getByLabel('Password', { exact: true }).fill(`${password}-wrong`);
  await page.getByRole('button', { name: 'Connect and sign in' }).click();
  await expect(page.getByTestId('link-error')).toHaveText("Wrong password. No password yet? Use 'Forgot your password?' to set one.");
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Connect and sign in' }).click();
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);

  await signOut(page);
  await page.getByRole('link', { name: 'Continue with Microsoft' }).click();
  await signInAtFake(page, 'Microsoft', email, true);
  await expect(page).toHaveURL(/\/notes$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('me-email')).toHaveText(email);
});

test('a person with an unverified Microsoft work address sees the link screen with the sign-up sentence', async ({ page }) => {
  await page.goto('/login');
  await page.getByRole('link', { name: 'Continue with Microsoft' }).click();
  await chooseAtFake(page, 'Microsoft', 'Bo, a work account (bo@firma.test, not verified)');
  await expect(page).toHaveURL(/\/provider-link$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('link-intro')).toHaveText(
    'If you have an account with bo@firma.test, enter its password once to connect Microsoft. If you do not, create an account with your email address.',
  );
  await expect(page.getByTestId('link-signup')).toHaveAttribute('href', '/signup');
});

test('an invited person accepts with Google and lands in the company', async ({ page }) => {
  test.setTimeout(240_000);
  const email = `${unique('invited')}@gmail.e2e.test`;
  await inviteByApi(email, 'user');
  await openMailLink(page, await waitForLink(email, 'invite', MAIL_WAIT_MS));
  await expect(page).toHaveURL(/\/invite$/);
  expectCleanAddressBar(page);
  await page.getByRole('link', { name: 'Accept with Google' }).click();
  await signInAtFake(page, 'Google', email, true);
  await expect(page).toHaveURL(/\/notes$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-company')).toBeVisible();
  await expect(page.getByTestId('me-role')).toHaveText('user');
});

test('a person who cancels at the provider sees the message and the way back', async ({ page }) => {
  await page.goto('/login');
  await page.getByRole('link', { name: 'Continue with Google' }).click();
  await cancelAtFake(page, 'Google');
  await expect(page).toHaveURL(/\/provider-return$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('provider-message')).toHaveText('Sign-in was cancelled.');
  await page.getByTestId('provider-back').click();
  await expect(page).toHaveURL(/\/login$/);
});

test('a private person without a password deletes the account with "Confirm with Google"', async ({ page }) => {
  test.setTimeout(120_000);
  const email = `${unique('deleter')}@gmail.e2e.test`;
  await signUpWithGoogle(page, email);
  await page.getByLabel('New note').fill(`Note ${unique('n')}`);
  await page.getByRole('button', { name: 'Add note' }).click();

  await page.getByTestId('delete-account').click();
  await expect(page.getByTestId('delete-warning')).toHaveText('This deletes your account and all your notes. It cannot be undone.');
  await expect(page.locator('#delete-password')).toHaveCount(0);
  await page.getByRole('link', { name: 'Confirm with Google' }).click();
  await signInAtFake(page, 'Google', email, true);

  // Back on the notes with the section open, ready to confirm; the ticket is in no URL.
  await expect(page).toHaveURL(/\/notes$/);
  expectNoTicketInAddressBar(page);
  await expect(page.getByTestId('delete-section')).toBeVisible();
  await page.getByRole('button', { name: 'Delete my account' }).click();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByTestId('login-notice')).toHaveText('Your account was deleted.');
  expect(await loginStatus(email, 'Any-Passw0rd-1'), 'an address without an account answers as an unknown one').toBe(401);
});
```

- [ ] **Step 3: Check the spec without a stack.** From `samples/notes-web`: `npx tsc -p e2e/tsconfig.json --noEmit` prints nothing;
`npx playwright test --list e2e/11-providers.spec.ts` lists the six tests for each of the two projects (twelve lines). Do not run them: the
live run is the verifiers' (`scripts/e2e-web.sh`).

- [ ] **Step 4: Hand back** — uncommitted. Files: the two new files. Scan them (CRLF, as the other specs). Proposed subject:
`test(sample): Playwright signs in, links, accepts, cancels and deletes with the fake provider`.

---

## Day 7 — Friday 01.05: the documents

When the day is done, the operator's guide gets the keys step by step, the runbook covers the Microsoft secret, the threat model, the guides,
the design brief and the changelog say what this spec says, and `scripts/check-docs.py` resolves every name of the acceptance map.

### Task 20: The documents and the acceptance map check

**Files:**
- Create: `docs/deployment/provider-keys.md`, `docs/operations/microsoft-secret.md`
- Modify: `deploy/docker-compose.prod.yml`, `deploy/.env.prod.example`, `docs/security/threat-model.md`, `docs/integration/angular.md`,
  `docs/integration/python-fastapi.md`, `docs/deployment/vps.md`, `docs/design.md`, `CHANGELOG.md`, `samples/notes-web/README.md`,
  `scripts/check-docs.py`
- Modify (tests): `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`

**Interfaces:**
- Consumes: every name of Tasks 1 to 19 (the documents cite them; `scripts/check-docs.py` resolves the test names, the paths and the
  variables).
- Produces: criterion 36; the six production variables (`AUTH_FRONTEND_PROVIDER_URL`, `AUTH_GOOGLE_CLIENT_ID`, `AUTH_GOOGLE_CLIENT_SECRET`,
  `AUTH_MICROSOFT_CLIENT_ID`, `AUTH_MICROSOFT_CLIENT_SECRET`, `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES`).

**Contract counts:** `scripts/check-docs.py` checks two more documents by default.

**Decisions the plan takes here:**
- The six variables are optional in the production compose file (`${...:-}`): blank is "not configured", which is healthy until the manifest
  lists a provider (spec 0011 → Configuration). A test pins the lines and a production host with all six blank.
- The two new documents join the default list of `scripts/check-docs.py`, so that a later rename of a test or a variable they name fails the
  check.
- No document shows a real or realistic secret: the guide writes `<the client secret>`.

- [ ] **Step 1: Write the failing tests.** In `tests/Auth.IntegrationTests/ShippedConfigurationTests.cs`, after the method
`The_production_compose_file_passes_the_optional_address_and_the_example_lists_it` add

```csharp

    [Theory]
    [InlineData("Auth__App__FrontendUrls__ProviderReturn", "AUTH_FRONTEND_PROVIDER_URL")]
    [InlineData("Auth__Providers__Google__ClientId", "AUTH_GOOGLE_CLIENT_ID")]
    [InlineData("Auth__Providers__Google__ClientSecret", "AUTH_GOOGLE_CLIENT_SECRET")]
    [InlineData("Auth__Providers__Microsoft__ClientId", "AUTH_MICROSOFT_CLIENT_ID")]
    [InlineData("Auth__Providers__Microsoft__ClientSecret", "AUTH_MICROSOFT_CLIENT_SECRET")]
    [InlineData("Auth__Providers__Microsoft__ClientSecretExpiresOn", "AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES")]
    public void The_production_compose_file_passes_each_provider_variable_as_optional_and_the_example_lists_it(string setting, string variable)   // spec 0011 → Documents
    {
        var compose = File.ReadAllText(RepositoryFile("deploy/docker-compose.prod.yml"));
        var example = File.ReadAllLines(RepositoryFile("deploy/.env.prod.example"));

        Assert.Contains($"{setting}: ${{{variable}:-}}", compose, StringComparison.Ordinal);
        Assert.Contains($"{variable}=", example);   // a line of its own, blank
    }

    [Fact]
    public async Task A_production_host_with_the_six_provider_variables_blank_is_healthy_and_offers_no_provider()   // spec 0011, criterion 30
    {
        await using var factory = new AuthAppFactory(postgres, keys).WithEnvironment("Production").WithSignup()
            .WithSetting(ProviderSettings.ReturnUrlKey, "")
            .WithSetting(ProviderSettings.GoogleClientIdKey, "")
            .WithSetting(ProviderSettings.GoogleClientSecretKey, "")
            .WithSetting(ProviderSettings.MicrosoftClientIdKey, "")
            .WithSetting(ProviderSettings.MicrosoftClientSecretKey, "")
            .WithSetting(ProviderSettings.MicrosoftSecretExpiresOnKey, "");
        using var client = factory.CreateClient();

        Assert.Equal("""{"signup":"self-service","private_signup":"off","providers":[]}""", await BodyAsync(client, "/auth/options"));
        Assert.Equal("Healthy", await BodyAsync(client, "/auth/health"));
    }
```

Build, run `ShippedConfigurationTests`: the theory fails on every row (the lines are missing); the fact passes already (Task 3 reads a blank
as absent) and pins it.

- [ ] **Step 2: The production files.** In `deploy/docker-compose.prod.yml`, after

```yaml
      Auth__App__FrontendUrls__CompleteSignup: ${AUTH_FRONTEND_SIGNUP_URL:-}
```

add

```yaml
      # Optional (spec 0011): sign-in with Google and Microsoft. The manifest lists the providers (`providers: [google, microsoft]`); each needs
      # its two keys and the return address below. A listed provider without them is not offered and /auth/health says Degraded; all blank keeps
      # them off. Register <origin of the return address>/auth/providers/google/callback (and .../microsoft/callback) at the provider:
      # docs/deployment/provider-keys.md.
      Auth__App__FrontendUrls__ProviderReturn: ${AUTH_FRONTEND_PROVIDER_URL:-}
      Auth__Providers__Google__ClientId: ${AUTH_GOOGLE_CLIENT_ID:-}
      Auth__Providers__Google__ClientSecret: ${AUTH_GOOGLE_CLIENT_SECRET:-}
      Auth__Providers__Microsoft__ClientId: ${AUTH_MICROSOFT_CLIENT_ID:-}
      Auth__Providers__Microsoft__ClientSecret: ${AUTH_MICROSOFT_CLIENT_SECRET:-}
      # The day the Microsoft secret lapses (yyyy-mm-dd): /auth/health says Degraded from 30 days before (docs/operations/microsoft-secret.md).
      Auth__Providers__Microsoft__ClientSecretExpiresOn: ${AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES:-}
```

In `deploy/.env.prod.example`, after the line `AUTH_FRONTEND_SIGNUP_URL=` add

```
# Optional (spec 0011): sign-in with Google and Microsoft. Your manifest lists them (`providers: [google, microsoft]`); docs/deployment/provider-keys.md
# says where each value comes from. All blank: no provider is offered, which is the default.
# The screen of YOUR frontend that a provider round trip comes back to (https only; Auth-Core adds #ticket=... or #error=...). Its origin plus
# /auth/providers/google/callback (and /microsoft/callback) is the address you register at the provider.
AUTH_FRONTEND_PROVIDER_URL=
# Google Cloud Console: the OAuth client of the type "Web application".
AUTH_GOOGLE_CLIENT_ID=
AUTH_GOOGLE_CLIENT_SECRET=
# Microsoft Entra admin center: the app registration's "Application (client) ID" and the Value of its client secret.
AUTH_MICROSOFT_CLIENT_ID=
AUTH_MICROSOFT_CLIENT_SECRET=
# The day that secret lapses, yyyy-mm-dd (Entra shows it): /auth/health says Degraded from 30 days before. See docs/operations/microsoft-secret.md.
AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES=
```

- [ ] **Step 3: The keys guide.** `docs/deployment/provider-keys.md`:

````markdown
# Getting the keys for Google and Microsoft

Auth-Core signs people in with Google and Microsoft when three things are in place: your manifest lists the providers
(`providers: [google, microsoft]`, see [`deploy/auth.yaml`](../../deploy/auth.yaml)), the return address of your frontend is set
(`AUTH_FRONTEND_PROVIDER_URL`), and each provider's two keys are in `.env`. This guide gets the keys. The values go into the `.env` of
[`vps.md`](vps.md) (the lines are in [`deploy/.env.prod.example`](../../deploy/.env.prod.example)); a secret never goes into the manifest, a
compose file or the repository.

The commands of this guide are for [`deploy/docker-compose.prod.yml`](../../deploy/docker-compose.prod.yml), run from the directory that holds
`deploy/` and `.env`; `$C` stands for the compose command with its project, as in [`vps.md`](vps.md):

```bash
C="docker compose -p auth-core-prod -f deploy/docker-compose.prod.yml --env-file .env"
```

## The addresses you register

Both providers send the browser back to Auth-Core, never to your frontend. The address is the **origin** of your return address followed by
`/auth/providers/{name}/callback`. With `AUTH_FRONTEND_PROVIDER_URL=https://app.example.com/provider-return`:

| Provider | Redirect URI to register |
| --- | --- |
| Google | `https://app.example.com/auth/providers/google/callback` |
| Microsoft | `https://app.example.com/auth/providers/microsoft/callback` |

Auth-Core builds the address from that setting only, never from the request, so it must match what you register character for character
(scheme, host, port, path; no trailing slash). Keep the access logs of `/auth/providers/*/start` and `/auth/providers/*/callback` off, or
scrub their query strings: they hold an invitation token, a code and a state (residual risk 55 in the
[threat model](../security/threat-model.md)).

## Google

In the [Google Cloud Console](https://console.cloud.google.com/):

1. **A project.** Create one for your product (or pick the product's existing one).
2. **The consent screen** ("Google Auth Platform", or "APIs & Services" then "OAuth consent screen"): the audience **External**; the app
   name the person will see; the support address; your developer contact address. Under the data access (scopes) add `openid`, `email`
   and `profile` only: they are the non-sensitive ones and need no verification by Google. Publish the app ("In production"): while it is
   "Testing" only the test users you list can sign in.
3. **The client** ("Clients", or "Credentials" then "Create credentials" then "OAuth client ID"): the type **Web application**, a name of
   your choice, and under "Authorized redirect URIs" the Google address of the table above. No JavaScript origin is needed: the browser never
   talks to Google with this client.
4. **The values.** Copy the client ID into `AUTH_GOOGLE_CLIENT_ID` and the client secret into `AUTH_GOOGLE_CLIENT_SECRET`
   (`AUTH_GOOGLE_CLIENT_SECRET=<the client secret>`). A Google client secret does not lapse.

### Check it with the sample

The repository's notes sample (`samples/notes-web` in front of `samples/notes-api`, at `https://localhost:8443`) lists Google and Microsoft in
its manifest (`samples/notes-api/auth.yaml`). To try your Google client with it before you deploy:

1. In the client of step 3, add the sample's redirect URI `https://localhost:8443/auth/providers/google/callback` beside your own (Google
   accepts a `localhost` address for a Web application client). Remove it once the check is done.
2. Put `AUTH_GOOGLE_CLIENT_ID` and `AUTH_GOOGLE_CLIENT_SECRET` into the repository's own `.env` (it is git-ignored), and write a compose overlay
   **outside the repository**, for example `~/auth-core-keys.yml`, with these lines:

   ```yaml
   services:
     auth:
       environment:
         Auth__App__FrontendUrls__ProviderReturn: https://localhost:8443/provider-return
         Auth__Providers__Google__ClientId: ${AUTH_GOOGLE_CLIENT_ID:-}
         Auth__Providers__Google__ClientSecret: ${AUTH_GOOGLE_CLIENT_SECRET:-}
         Auth__Providers__Microsoft__ClientId: ${AUTH_MICROSOFT_CLIENT_ID:-}
         Auth__Providers__Microsoft__ClientSecret: ${AUTH_MICROSOFT_CLIENT_SECRET:-}
   ```

3. Start the sample's stack with that overlay and **without** the fake provider's overlay (`tests/Auth.FakeOidc/compose.yml`, which would
   send the buttons to the fake), from the repository root:

   ```bash
   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f samples/notes-web/compose.yml \
     -f ~/auth-core-keys.yml --env-file .env up -d --build
   ```

   With Google's keys only, Microsoft is not offered and `/auth/health` says `Degraded`: that is expected.
4. Open `https://localhost:8443/login` (accept the certificate warning), press "Continue with Google" and sign in at Google: a new address
   reaches the screen that finishes the account (`/provider-finish`), an address of an existing password account the link screen
   (`/provider-link`). Anything else is in "When a value is wrong" below.

## Microsoft

In the [Microsoft Entra admin center](https://entra.microsoft.com/), "App registrations", then "New registration":

1. **The registration.** A name the person will see; "Supported account types": **Accounts in any organizational directory and personal
   Microsoft accounts**; "Redirect URI": the platform **Web** and the Microsoft address of the table above. Register.
2. **The client id.** The "Application (client) ID" of the overview goes into `AUTH_MICROSOFT_CLIENT_ID`.
3. **The secret.** "Certificates & secrets", "Client secrets", "New client secret": a description and the longest lapse offered (at most 24
   months). Copy its **Value** (not its "Secret ID") into `AUTH_MICROSOFT_CLIENT_SECRET` at once: Entra shows it only now. Write the
   "Expires" date into `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES` as `yyyy-mm-dd`: from 30 days before it `/auth/health` says `Degraded`
   ([`microsoft-secret.md`](../operations/microsoft-secret.md) is the runbook).
4. **The claims.** "Token configuration", "Add optional claim", the token type **ID**, and tick `email` and `xms_edov` (accept the
   Microsoft Graph `email` permission it offers to add). Without `email` a work account has no address in Auth-Core; without `xms_edov`
   every work address reads as unverified, which is safe but asks such a person for a password to link (spec 0011, "The provider identity
   and its address").
5. **The permissions.** "API permissions" holds Microsoft Graph `openid`, `email` and `profile` (delegated). No admin consent is needed for
   them.

### Check it with the sample

1. In the registration's "Authentication" page, add the Web redirect URI `https://localhost:8443/auth/providers/microsoft/callback` beside your
   own (Entra accepts a `localhost` address for the Web platform). Remove it once the check is done.
2. Put `AUTH_MICROSOFT_CLIENT_ID` and `AUTH_MICROSOFT_CLIENT_SECRET` into the repository's `.env`; the overlay of Google's check passes them
   already (write it as shown there if you skipped Google).
3. Start the stack as in Google's check, open `https://localhost:8443/login`, press "Continue with Microsoft" and sign in with a personal
   account: a new address reaches `/provider-finish`. A work account reaches `/provider-finish` only with the optional claim `xms_edov` of
   step 4; without it the link screen asks for a password (`/provider-link`), which is the safe answer for an unverified address.

## Turn it on and check it

1. Set the values in `.env` and `providers: [google, microsoft]` in your manifest (list only the ones you configured), then
   `$C up -d` (a restart does not read `.env` again).
2. `curl -s https://app.example.com/auth/options` lists the providers, in the order of the manifest; `curl -s https://app.example.com/auth/health`
   says `Healthy`.
3. On your product's sign-in screen, "Continue with Google" (and "with Microsoft") goes to the provider and back, and a new person reaches the
   screen that finishes the account (the sample's `/provider-finish`).

## When a value is wrong

| What you see | Why |
| --- | --- |
| A provider is missing from `GET /auth/options`, `/auth/health` says `Degraded`, and the start logged a warning naming keys | The manifest lists it, but a key or `AUTH_FRONTEND_PROVIDER_URL` is blank: the warning names the missing keys (never their values) |
| The `auth` container stops at start, naming `Auth:App:FrontendUrls:ProviderReturn` or `Auth:Providers:Microsoft:ClientSecretExpiresOn` | The return address is not an absolute `https` address without a fragment, or the date is not `yyyy-mm-dd` |
| Google shows `Error 400: redirect_uri_mismatch`; Microsoft shows an `AADSTS50011` page | The registered redirect URI differs from the one Auth-Core sends: compare them with the table above |
| Google shows `Error 401: invalid_client`; Microsoft shows `AADSTS700016` | The client id is wrong, or belongs to another project or tenant |
| The person comes back to your screen with `#error=provider_error`, and `$C logs auth` names `invalid_client` (the line `The google callback ended in provider_error (the client recorded the OAuth error invalid_client).`, or `microsoft`) | The client secret is wrong, or the Microsoft secret has lapsed ([`microsoft-secret.md`](../operations/microsoft-secret.md)) |
| Microsoft sign-ins of work accounts always ask for a password to link | The optional claim `xms_edov` is missing (step 4 of Microsoft) |
````

- [ ] **Step 4: The runbook.** `docs/operations/microsoft-secret.md`:

````markdown
# The Microsoft client secret

A client secret of a Microsoft Entra app registration lapses, after 24 months at most. From that day every Microsoft sign-in fails: the
person comes back to the product's return screen with `#error=provider_error`, and `$C logs auth` names `invalid_client` in the callback's line
(`The microsoft callback ended in provider_error (the client recorded the OAuth error invalid_client).`). Nothing rotates it
for you. Google's client secret does not lapse.

The commands are for [`deploy/docker-compose.prod.yml`](../../deploy/docker-compose.prod.yml), run from the directory that holds `deploy/` and
`.env`; `$C` stands for the compose command with its project, as in [`backup.md`](backup.md):

```bash
C="docker compose -p auth-core-prod -f deploy/docker-compose.prod.yml --env-file .env"
```

## How you notice

- **Before:** with `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES` set to the secret's "Expires" date, `/auth/health` says `Degraded` from 30 days
  before that day (and after it), and each start logs a warning naming the setting and the date. Watch `/auth/health` as you already do.
- **After:** `#error=provider_error` on every Microsoft round trip, and `invalid_client` in the log. Without the date setting this is the
  first sign.

## Rotate it

1. In the [Microsoft Entra admin center](https://entra.microsoft.com/), the app registration, "Certificates & secrets", "New client secret".
   Copy its **Value** at once and note its "Expires" date. The old secret keeps working until its own date: the two overlap.
2. In `.env` put the new value into `AUTH_MICROSOFT_CLIENT_SECRET` and the new date into `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES`.
3. `$C up -d` (a restart does not read `.env` again). `$C logs auth` shows no warning about the secret; `/auth/health` says `Healthy`.
4. Sign in once with Microsoft on the product.
5. Delete the old secret in Entra.

A round trip that was in flight during the restart ends with `#error=invalid_state` or `provider_error`; the person tries again.

## Meanwhile

People who sign in only with Microsoft (no password) cannot sign in until the secret works again. They can use "Forgot your password?" on
the product's sign-in screen: the mail sets a password for their account, and they sign in with it. Their Microsoft login stays linked.
````

- [ ] **Step 5: The threat model.** In `docs/security/threat-model.md`:

(a) replace

```
 Product backend ──https GET /auth/.well-known/jwks.json──┘
 Operator ──docker compose run auth admin ...──> Auth-Core's database       Key files (read-only mount)
```

with

```
 Product backend ──https GET /auth/.well-known/jwks.json──┘
 Operator ──docker compose run auth admin ...──> Auth-Core's database       Key files (read-only mount)
 Auth-Core ──https (discovery, keys, token endpoint)──> Google, Microsoft     Browser ──https round trip──> Google, Microsoft (spec 0011)
```

and in the paragraph after the diagram replace

```
to PostgreSQL and to the relay (private network; TLS to the relay), the host's file system to the container (keys, manifest; read-only).
```

with

```
to PostgreSQL and to the relay (private network; TLS to the relay), the host's file system to the container (keys, manifest; read-only), and,
with providers on, Auth-Core to Google and Microsoft (outbound HTTPS, the client secret) and the browser's round trip through them (spec 0011).
```

(b) before `## Account deletion and personal data` add

```markdown
## Sign-in with Google and Microsoft

Spec 0011. The browser follows a link to `GET /auth/providers/{name}/start`; Auth-Core answers `302` to the provider with the authorization
code flow, PKCE (`S256`), a state and a nonce, and sets a request-forgery cookie (`SameSite=Lax`, `Path=/auth/providers`) that binds the
state to that browser. The provider sends the browser to `GET /auth/providers/{name}/callback`; Auth-Core redeems the code with the PKCE
verifier and its client secret over HTTPS, validates the identity token (signature, issuer, audience, nonce, expiry), reads the identity
(Google `sub`; Microsoft `{tid}:{oid}`), and answers `302` to the product's return screen with a ticket in the **fragment** and the cookie
`auth_pt` (`HttpOnly; Secure; SameSite=Strict; Path=/auth`). The screen spends the ticket at `POST /auth/providers/exchange`, `link` or
`finish`, or at `DELETE /auth/account` (`provider_ticket`). A ticket is 32 random bytes, stored as a hash with the hash of its `auth_pt`,
used once, and lives 10 minutes (5 for `reauth`). The return address, and so the callback address, comes from configuration only.

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | An attacker's code or state is fed into a victim's browser (login CSRF), or a state is replayed | The state is single use, lives 10 minutes and needs the request-forgery cookie of the browser that started; PKCE; the nonce (`ProviderCallbackErrorTests`) | |
| S | Someone signs in as an account by owning a provider account with its address (a work tenant can set any `email`: "nOAuth") | The identity key is `sub` or `tid:oid`, never the address; a Microsoft work address is unverified unless `xms_edov` is true; an unverified address never links and never skips the mail; linking an account with the address always asks for its password once (`ProviderIdentityTests`, `ProviderLinkTests`) | Whoever controls a linked provider account controls the product account (risk 52) |
| S | A ticket is pushed into someone else's browser (a link to the return screen with the attacker's ticket) | Every use needs the `auth_pt` of the browser the callback answered; a second round trip in a browser makes the first ticket unusable (`ProviderTicketTests`) | |
| T | The browser is sent to an address an attacker chose | The return and callback addresses come from configuration only, never from `Host` or `X-Forwarded-Host` (`ProviderStartTests.The_callback_address_comes_from_the_return_address_whatever_the_host_headers_say`, `ProviderCallbackTests.A_callback_that_reaches_the_service_under_another_host_and_scheme_still_makes_a_ticket`) | |
| R | A person denies a provider sign-in, a link or a deletion | `login.succeeded` and `login.failed` name the provider; `provider.linked`, `signup.completed`, `invite.accepted` and `provider.reauthenticated` record the flows (`AuditProviderFlowsTests`) | |
| I | The state, the PKCE verifier, the code or a ticket in a log, a URL or the audit log | The client's dispatcher logs at `Warning` (`ProviderCallbackTests.No_state_value_and_no_code_verifier_reaches_the_log`); tickets and bindings are stored as hashes only; the ticket travels in the fragment, which no server receives; no audit row holds any of them (`AuditProviderFlowsTests`) | The code and an invitation token are in request lines (risk 55); a ticket is in the address bar until the screen removes it (risk 56) |
| I | The exchange or the link used to learn which addresses have accounts | An unverified address gets the same `next: link` whether or not an account has it; a wrong password and an unknown address get the same `401` after the same hash work (`ProviderExchangeTests`, `ProviderLinkTests`) | A verified address that has an account is answered `next: link`: only the owner of that mailbox at the provider learns it |
| D | Passwords guessed through the link screen | The link counts in the login streak of the address and its lockout; the five provider paths count against the `login` limit (`ProviderLinkLockoutTests`, `RateLimitMiddlewareTests`) | |
| D | The provider is down, the keys are revoked, or the Microsoft secret lapses | Health says `Degraded` for missing keys and 30 days before the secret's date; "forgot password" sets a password (`ProviderSettingsTests`, `ProviderPasswordResetTests`; [`microsoft-secret.md`](../operations/microsoft-secret.md)) | People without a password depend on the provider (risks 53, 54) |
| E | A live access token used to delete a private account without a password | The deletion needs a `reauth` ticket of an identity linked to this account, used once within 5 minutes; Auth-Core asks for a fresh sign-in (`prompt=login`, `max_age=0`) (`AccountDeletionProviderTests`) | The provider decides how fresh "fresh" is (risk 57) |

The providers themselves learn of each sign-in (risk 58).
```

(c) after item 51 of "Residual risks" (the line that starts `51. **A wrong password after the product has deleted its data`) add

```markdown
52. **A provider account is the product account.** Whoever takes over a linked Google or Microsoft account signs in to the product without its password. Whoever administers a Microsoft tenant or a Google Workspace domain controls the provider accounts of its people, and so their product accounts (spec 0011).
53. **A person without a password depends on the provider.** A provider outage, the product's keys revoked or lapsed, the provider no longer offered, or the provider account lost (a person who leaves a company loses its Microsoft account) locks them out until "forgot password" sets a password, which needs the mailbox (spec 0011).
54. **The Microsoft client secret lapses** after at most 24 months, and every Microsoft sign-in fails until it is rotated. The date setting and the runbook ([`microsoft-secret.md`](../operations/microsoft-secret.md)) are the warning; nothing rotates it (spec 0011).
55. **Codes and invitation tokens in request lines.** The invitation token travels in the query of the start address, and the provider's code and state in that of the callback. A proxy or web server that logs query strings keeps them. The code is single use and needs the PKCE verifier; the invitation token works until it is used (7 days) (spec 0011).
56. **A ticket is in the address bar** until the return screen removes it, readable by a browser extension or by script in the page. It is single use, bound to the browser's `auth_pt` cookie and lives 10 minutes (5 for `reauth`) (spec 0011).
57. **Re-authentication is as strong as the provider makes it.** A person still signed in at the provider may confirm with one click; whoever holds a live access token in that browser can then delete a private account without a password. Auth-Core asks for a fresh sign-in (`prompt=login`, `max_age=0`) but does not check that one happened (spec 0011).
58. **The provider learns about sign-ins.** Google and Microsoft see when a person signs in to the product, from which address, and the product's name (spec 0011).
```

- [ ] **Step 6: The Angular guide.** In `docs/integration/angular.md`:

(a) replace

```
access logs off for `/reset`, `/verify`, `/invite` and `/complete-signup`, or have them scrub `token=` (the query string) from what they write, and keep any log
```

with

```
access logs off for `/reset`, `/verify`, `/invite`, `/complete-signup`, `/auth/providers/*/start` and `/auth/providers/*/callback` (spec 0011: an invitation
token, a code and a state), or have them scrub `token=` (the query string) from what they write, and keep any log
```

(b) before `## The limits and an outage (spec 0008)` add

```markdown
## Google and Microsoft (spec 0011)

When `GET /auth/options` lists `providers` (`["google","microsoft"]`, in the manifest's order), draw one button per name, first on the
sign-in, sign-up and invitation screens, then "or" and the form. **A button is a link, not a call**: `<a href="/auth/providers/google/start">`
(on `/invite` with `?invite=<the token>`, for an address without a private account only). The browser leaves for the provider and comes back
to your return screen ([`pages/provider-buttons.ts`](../../samples/notes-web/src/app/pages/provider-buttons.ts)).

- **The return screen** (the sample's `/provider-return`, the address of `Auth:App:FrontendUrls:ProviderReturn`) reads the fragment once,
  `#ticket=...&purpose=...` or `#error=...&purpose=...`, and removes it from the address bar with a navigation that replaces the history
  entry **before** it calls anything ([`pages/provider-return.ts`](../../samples/notes-web/src/app/pages/provider-return.ts)). Keep the
  ticket in memory and in the router's navigation state only: never in a URL, `localStorage` or `sessionStorage`. The router keeps that state
  in the browser's history entry, so a reload of the next screen still has the ticket until it is used or expires (10 minutes). The sample
  keeps one thing in `sessionStorage`: the name of the provider the person chose (the fragment does not carry it), for the sentence of the
  link screen; never the ticket, the address or the kinds.
- **The exchange.** `POST /auth/providers/exchange` `{"ticket"}` answers as a login does (the token and the refresh cookie), or
  `{"next":"link",...}` (open the link screen), or `{"next":"finish_signup","email","kinds"}` (open the finish screen), or an error with a
  sentence of its own. The cookie `auth_pt` goes along by itself; the call needs no access token, so leave the provider paths out of the
  interceptor's token paths.
- **The link screen** asks the password of the existing account once (`POST /auth/providers/link`); when `email_verified` is `false` it
  says so and offers sign-up instead ([`pages/provider-link.ts`](../../samples/notes-web/src/app/pages/provider-link.ts)). **The finish
  screen** shows the address read-only, the choice when `kinds` holds both, the company name for an organization, and no password
  (`POST /auth/providers/finish`, [`pages/provider-finish.ts`](../../samples/notes-web/src/app/pages/provider-finish.ts)).
- **"Delete account" without a password.** `GET /auth/me` says `has_password` and `linked_providers`. For `has_password: false` show one
  "Confirm with {provider}" link per linked provider to `start?purpose=reauth`; back with the ticket (purpose `reauth`), delete your data
  and then call `DELETE /auth/account` with `{"provider_ticket"}`. `reauth_failed` means confirm again; `password_required` and
  `reauth_required` mean the account changed: ask `GET /auth/me` again and redraw ([`pages/notes.ts`](../../samples/notes-web/src/app/pages/notes.ts)).
- A person who signed up with a provider has no password; "Forgot your password?" sets one.
```

- [ ] **Step 7: The Python guide.** In `docs/integration/python-fastapi.md`, replace

```
Your backend never answers `permissions_changed`: it does not read the database.
```

with

```
Your backend never answers `permissions_changed`: it does not read the database.

A person who signs in with Google or Microsoft (spec 0011) gets exactly the access token a password sign-in gives: nothing changes for your
backend or for this package.
```

- [ ] **Step 8: The deployment guide.** In `docs/deployment/vps.md`:

(a) replace

```
**Optional.** `AUTH_FRONTEND_SIGNUP_URL` is the https address of the screen of your frontend that finishes a self-service sign-up. Leave it blank and sign-up
stays off (the default; nothing changes for you). See "Self-service sign-up" in step 7.
```

with

```
**Optional.** `AUTH_FRONTEND_SIGNUP_URL` is the https address of the screen of your frontend that finishes a self-service sign-up. Leave it blank and sign-up
stays off (the default; nothing changes for you). See "Self-service sign-up" in step 7. The six provider variables (`AUTH_FRONTEND_PROVIDER_URL`,
`AUTH_GOOGLE_CLIENT_ID`, `AUTH_GOOGLE_CLIENT_SECRET`, `AUTH_MICROSOFT_CLIENT_ID`, `AUTH_MICROSOFT_CLIENT_SECRET`, `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES`)
stay blank unless you turn on sign-in with Google and Microsoft: see "Sign-in with Google and Microsoft" in step 7.
```

(b) before `## 8. Check it` add

```markdown
### Sign-in with Google and Microsoft (optional)

Off unless you turn it on (spec 0011). It needs your manifest to list the providers (`providers: [google, microsoft]`), the https address of
your frontend's return screen in `AUTH_FRONTEND_PROVIDER_URL` (`https://app.example.com/provider-return`; the sample's is `/provider-return`),
and each provider's two keys in `.env`. [`provider-keys.md`](provider-keys.md) gets the keys step by step. At each provider you register the
**callback address**: the origin of `AUTH_FRONTEND_PROVIDER_URL` followed by `/auth/providers/google/callback` (or `/microsoft/callback`),
for example `https://app.example.com/auth/providers/google/callback`; Auth-Core builds it from that setting only. Set
`AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES` to the Microsoft secret's lapse date, and keep [`microsoft-secret.md`](../operations/microsoft-secret.md)
at hand for the rotation. After changing `.env` run `$C up -d`; after changing the manifest `$C restart auth`. A listed provider without its
keys or the return address is not offered, the start logs a warning naming the missing keys, and `/auth/health` says `Degraded`. Keep the
access logs of `/auth/providers/*/start` and `/auth/providers/*/callback` off, or scrub their query strings: they hold an invitation token, a
code and a state.
```

(c) in the table of "When something is wrong", after the row that starts `| \`/auth/health\` says \`Degraded\` after you turned sign-up on |` add

```
| `/auth/health` says `Degraded` after you listed providers | A listed provider is missing a key or `AUTH_FRONTEND_PROVIDER_URL`: `$C logs auth \| grep -i provider` names the keys; or the Microsoft secret lapses within 30 days ([`microsoft-secret.md`](../operations/microsoft-secret.md)) |
| A Microsoft or Google sign-in comes back with `#error=provider_error` | A wrong client secret, or the Microsoft secret lapsed: `$C logs auth \| grep callback` shows `provider_error` with the OAuth error `invalid_client`; see the table of [`provider-keys.md`](provider-keys.md) |
```

- [ ] **Step 9: The design, the changelog, the sample's readme.** In `docs/design.md` replace

```
| 0011  | Google and Microsoft: the provider round trip, sign-in, linking with the password, sign-up and invitations through a provider; sample buttons and screens | "Continue with Google" works end to end against a test provider |
| 0012  | GitHub; listing, linking and unlinking providers; accounts without a password; sample settings screen    | A person manages their linked logins                             |
```

with

```
| 0011  | Google and Microsoft: the provider round trip, sign-in, linking with the password, sign-up and invitations through a provider, accounts without a password ("forgot password" sets one), deletion confirmed with the provider; sample buttons and screens | "Continue with Google" works end to end against a test provider |
| 0012  | GitHub; listing, linking and unlinking providers; the full settings of an account without a password; sample settings screen | A person manages their linked logins                             |
```

and after the paragraph that starts `*As built (spec 0010):*` (its last line ends `space that moved to a company, the audit rows and the backups) is in the threat model (risks 48 to 51).`) add

```

*Spec 0011:* "Continue with Google" and "Continue with Microsoft" through OpenIddict's client at the version of the server. The manifest
lists the providers and the environment holds their keys; the round trip comes back to Auth-Core, which hands the product's return screen a
single-use ticket in the fragment, bound to the browser by the cookie `auth_pt`. An identity that is linked signs in; an existing account
links with its password once; a new person finishes an account without a password, and "forgot password" sets one; an invitation can be
accepted with a provider; a private person without a password confirms a deletion with the provider. The tests run the real web-provider
registrations against a fake provider (`tests/Auth.FakeOidc`); what remains is in the threat model (risks 52 to 58).
```

In `CHANGELOG.md`, after the paragraph that starts `Private accounts ([spec 0010]` (it ends with the words "when they lose their company.") add

```

Sign-in with Google and Microsoft ([spec 0011](docs/superpowers/specs/0011-google-and-microsoft-sign-in.md)): a product can offer "Continue with
Google" and "Continue with Microsoft". Off by default; an instance that does not list providers sees `"providers":[]` in `GET /auth/options`
and two new members in `GET /auth/me`, and nothing else changes.
```

and after the first line `### Added` (the one under `## Unreleased`; the file has another under `## 0.1.0`) add

```
- **Google and Microsoft sign-in.** The manifest key `providers`; `GET /auth/providers/{name}/start` and `GET /auth/providers/{name}/callback`
  (the round trip, with PKCE, a state bound to the browser and a nonce), `POST /auth/providers/exchange`, `POST /auth/providers/link` and
  `POST /auth/providers/finish`; single-use tickets stored as hashes (the table `ProviderTickets`) and the browser-binding cookie `auth_pt`;
  `DELETE /auth/account` with `provider_ticket`; `GET /auth/options` `providers`; `GET /auth/me` `has_password` and `linked_providers`; the
  audit kinds `provider.linked` and `provider.reauthenticated` (28 in all) and `details.provider` on the sign-in, sign-up and invitation rows;
  the optional variables `AUTH_FRONTEND_PROVIDER_URL`, `AUTH_GOOGLE_CLIENT_ID`, `AUTH_GOOGLE_CLIENT_SECRET`, `AUTH_MICROSOFT_CLIENT_ID`,
  `AUTH_MICROSOFT_CLIENT_SECRET` and `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES`; the guide `docs/deployment/provider-keys.md` and the runbook
  `docs/operations/microsoft-secret.md`; the sample's buttons and its `/provider-return`, `/provider-link` and `/provider-finish` screens;
  the fake provider `tests/Auth.FakeOidc` and `scripts/e2e-providers.sh`.
```

In `samples/notes-web/README.md`, after the bullet that starts `- There is no PWA, and the only screen for company admins is` (one line)
add

```
- With providers on (spec 0011; the notes manifest lists Google and Microsoft) the sign-in, sign-up and invitation screens start with
  "Continue with ..." links, the round trip comes back to `/provider-return`, and `/provider-link` and `/provider-finish` follow; an account
  without a password confirms "Delete account" with its provider. `scripts/e2e-web.sh` runs the stack with the fake provider of
  `tests/Auth.FakeOidc` (http://localhost:8099). Started by hand without `-f tests/Auth.FakeOidc/compose.yml`, the sample's health says
  `Degraded` and the start logs that Google and Microsoft have no keys; nothing else changes.
```

- [ ] **Step 10: The documents check.** In `scripts/check-docs.py` replace

```python
    "docs/operations/key-rotation.md",
    "clients/python/README.md",
]
```

with

```python
    "docs/operations/key-rotation.md",
    "docs/operations/microsoft-secret.md",
    "docs/deployment/provider-keys.md",
    "clients/python/README.md",
]
```

- [ ] **Step 11: Run every check.** Build, then
`tests/Auth.IntegrationTests/bin/Debug/net10.0/Auth.IntegrationTests.exe -noLogo -maxThreads 2 -class Auth.IntegrationTests.ShippedConfigurationTests`
passes. `C:/p6v/Scripts/python.exe scripts/check-docs.py` prints `all links, paths, files and tests resolve`;
`C:/p6v/Scripts/python.exe scripts/check-docs.py docs/superpowers/plans/0011-acceptance-map.md`
prints the same (every test the map names exists by now: a name it cannot resolve is a test a task did not write, which is a stop
condition: report it, do not rename the map); `C:/p6v/Scripts/python.exe scripts/check-docs.py --self-test` passes; from `samples/notes-web`
`npm run check:docs` passes (the guide keeps its seven numbered steps). `bash scripts/secret-scan.sh` (if `gitleaks` is installed) finds
nothing in the touched files.

- [ ] **Step 12: Hand back** — uncommitted. Files: everything listed under Files. Scan the touched files (Markdown CRLF as the other
documents; YAML and the env example LF). Proposed subject: `docs: the provider keys guide, the Microsoft secret runbook, the threat model and
the guides for spec 0011`.

---

## Self-review

Run against the spec, section by section, after the tasks above were written.

**1. Spec coverage.**

| Spec section | Task |
| --- | --- |
| Goal, the sequence of calls | 5, 7, 8, 9, 12; live: 15 (`scripts/e2e-providers.sh`, steps 2 to 8) |
| General rules | 8 to 12 (bodies, `invalid_request`), 13 (limits, charset) |
| Manifest | 2 |
| Configuration (the keys, the callback address, logging, missing keys, the secret's date, the test issuer) | 3 (settings, health, warnings), 5 (callback address, client registration, the dispatcher at `Warning`, the test issuer's registrations), 7 (the origin step of A2) |
| `GET /auth/options` | 3 |
| `GET /auth/providers/{name}/start` | 5 |
| `GET /auth/providers/{name}/callback` | 7 |
| The provider identity and its address | 6 |
| Provider tickets | 1 (table, store, clean-up), 7 (issue), 8 to 12 (use) |
| The browser binding `auth_pt` | 7 (set), 8 (cleared after commit), 12 (cleared on deletion) |
| `POST /auth/providers/exchange` | 8 (sign-in), 11 (invite) |
| `POST /auth/providers/link` | 9 |
| `POST /auth/providers/finish` | 10 |
| `DELETE /auth/account` | 12 |
| `GET /auth/me` | 12 |
| Accounts without a password | 12 (`ProviderPasswordResetTests`, the `401` of an unknown address) |
| Mails | none to build (no new mail); 12 checks the reset mail for an account without a password |
| Limits | 13 |
| Audit events | 8 (kinds), 9 to 12 (rows), 13 (all flows, no secret) |
| OpenAPI | 3, 5, 7, 8, 9, 10, 11, 12, 13 (36 operations, 24 under the charset guard) |
| Python package | nothing changes; 20 (the sentence of the guide) |
| Sample frontend | 16, 17, 18; Playwright 19 |
| Sample backend | 14 (`samples/notes-api/auth.yaml`, the fake's overlay) |
| Documents and residual risks 52 to 58 | 14 (`deploy/auth.yaml`), 20 |
| Acceptance criteria 1 to 36 | [`0011-acceptance-map.md`](0011-acceptance-map.md) names a test or a step for each |
| Decisions 1 to 20 and Settled 1 to 32 | each is the rule of the task that builds its section above; Settled 22 (the fake) is Tasks 4 and 14, Settled 9 (one `auth_pt` per browser) Tasks 7 and 8 |
| Amendment A1 to A8, Q14, userinfo | A1: 5 (appsettings) and 7 (`No_state_value_and_no_code_verifier_reaches_the_log`); A2: 5 (`ProviderPathsTests`) and 7; A3: 7; A4: 5; A5: 4; A6: 6 and 7; A7: 5 and 7; A8: 15 (steps 10 and 11 of `scripts/e2e-providers.sh`); Q14: nothing to build (open question 1); userinfo off: 5 (`UserInfoOff`) |
| Out of scope | nothing built: no GitHub, no listing or unlinking of providers, no settings screen, no account switcher, no hosted page |

**2. Placeholder scan.** No "TBD", "similar to", "as above" or step without its code. Edits of existing files quote the text to find and give
the text to put; a few edits name a long line by its start (the threat model's item 51, the README bullet, the changelog paragraph), and every
sentence they add is written out. Four steps name a fallback or a stop for something the plan could not prove on paper: the import cycle of
Task 16 (move three functions to `providers.ts`), an earlier spec of the sample that flushes a `/auth/me` body without the two new members
(Task 16), the OAuth error OpenIddict records for a token endpoint's `invalid_client` (Task 7: a stop condition, because the runbook's sentence
depends on it), and a test name of the acceptance map that does not resolve (Task 20: a stop condition, not a rename). Each says to report it.

**3. Type consistency.** `ProviderTicketPurpose`, `ProviderIdentity`, `ProviderTicketDraft`, `IssuedTicket` and `ProviderTickets` (Task 1) are used
by Tasks 7 to 12 with the same members; `ProviderNames` (Task 2) by Tasks 3, 5, 6 and 7; `ProviderSettings` and `ProviderCatalog` (Task 3) by
Tasks 5, 7, 13 and 14; `FakeSignIn`, `FakePeople`, `FakeDefects`, `FakeOidcServer` and `FakeOidcHost` (Task 4) by Tasks 5 to 13 and 19;
`ProviderPaths`, `ProviderPurposes`, `ProviderStateItems` (Task 5) by Tasks 7 and 8; `ProviderIdentities.Read` (Task 6) by Task 7;
`ProviderBindingCookie` (Task 7) by Tasks 8 to 12; `ProviderSignIn`, `ProviderLogins`, `LoginResponseShaper` and the three `ProviderNext`
records (Task 8) by Tasks 9 to 12; `ProviderTestBase` and `ProviderApi` grow in Tasks 5, 7, 8, 9, 10 and 12 and every later task uses the
names those tasks give. On the sample: `ProviderName`, `providerStartUrl`, `Me.has_password`, `Me.linked_providers`, the `Failure` kinds,
`ProviderButtons`, `rememberedProvider`, `providerTitle` and `providerMe` (Task 16) by Tasks 17 and 18; `exchangeTicket`, `linkProvider`,
`finishWithProvider` and the `reauthTicket` navigation state (Task 17) by Task 18; the fake's page texts (Task 4) by Task 19.

**4. Anchors.** Every "replace" of an existing file was checked against `44b5ffc` while writing; the anchors of a file that an earlier task of
this plan changes are written against the text that task leaves (`OpenApiTests` and `OpenApiHardeningTests` in Tasks 3, 5, 7 to 13 in that
order; `ShippedConfigurationTests` in Tasks 14, 15 and 20 after Task 3; `ProviderTestBase` and `ProviderApi` from Task 5 on; `auth.service.ts`
in Tasks 17 and 18 after Task 16; `notes.ts` in Task 18 as Task 16 leaves it, which does not touch it).

**5. Review Focus.** Each of the five has its named tests in the tasks it points at.

## Decisions taken in this plan

The spec is not changed by this plan beyond its Amendment (A1 to A8, Q14, userinfo; A8 with criterion 33). One line each; none of them changes the contract it
states, and each is covered by a test.

1. **Tickets** have a surrogate key and a unique index on the hash; one query finds a ticket by its hash, its binding hash and its expiry, so a
   ticket without its cookie is indistinguishable from an unknown one (Task 1).
2. **Settings:** missing keys are worked out once at start; what is offered is worked out at every call from the active manifest; ids and secrets
   are used as configured; the return address goes through the loader of `CompleteSignup` and also refuses `ticket`; the secret's date is
   compared by calendar day in UTC (Task 3).
3. **The fake** serves the providers' real host names in the tests and `/{google|microsoft}/...` on any other host for the stack; a `custom`
   person's identity is derived from the address, so a second round trip is the same person (Task 4).
4. **The client is always registered;** "given twice" applies to `invite` and `purpose` only (the spec's next sentence ignores other parameters; orchestrator decision D25); the state carries the invitation token's hash;
   `RedirectUri` is `/` (Task 5).
5. **A claim given twice is a missing claim;** every `email` claim is kept as the address, unverified when it breaks the invitation rule, and
   only no `email` claim is no address (the column is `text`); `tid` and `oid` must be GUIDs (Tasks 1 and 6).
6. **The callback** of a name that is no provider, or on an instance without a return address, is `404`; the code is judged before the purpose
   and the manifest; one `Information` line per answer (Task 7).
7. **The three ticket endpoints are OpenIddict token paths from Task 8 on;** between Tasks 8 and 11 an `invite` ticket at the exchange is
   `invalid_ticket`; a refusal of the exchange writes no row; `auth_pt` is cleared after the commit (Task 8).
8. **The link** writes `login.failed` with the login's reasons for the refusals after a right password; it is written under the address lock
   after the ticket is used up (Task 9).
9. **The finish** lives beside the mail completion in `SignupService` and deletes the pending sign-up of the address, of either kind (Task 10).
10. **The provider acceptance** lives in `InviteAcceptance`; an account with no space at all is `409 already_member` there (Task 11).
11. **A blank `provider_ticket` is `403 reauth_failed`** (a string, so it is judged as a ticket, plan review A-04); every `204` of `DELETE /auth/account` clears `auth_pt`; `provider.reauthenticated` is staged before
    the deletion's rows (Task 12).
12. **The rate policy is by path, before the name is judged:** a GET of any `/auth/providers/{name}/start` or `/callback` counts against `login`
    (Task 13).
13. **The fake's image** builds with its own `Dockerfile.dockerignore`, on the server's base images; its overlay is an extra `-f` file for the
    stacks of the notes manifest; the return address of the stack is `https://localhost:8443/provider-return` (Task 14).
14. **`scripts/e2e-providers.sh`** runs on the web stack and does not bring it up; it drives every exchange answer the stack can reach, with
    one SQL statement each for `no_membership` and `already_member`, and leaves only `no_account` to the integration tests (spec 0011,
    Amendment A8); it searches the audit log for every non-empty secret it saw (Task 15).
15. **The sample** keeps the chosen provider's name, and nothing else, in `sessionStorage` (`notes-web.provider`) for the link screen's
    sentence "connect {Google}": the spec's storage rule names the ticket, the address and the kinds, which stay in the navigation state, and
    the fragment carries no provider name (orchestrator's ruling D24). A `has_password` that is not `false` reads as `true`; the return screen
    removes the fragment before any call and leaves by `replaceUrl` only; the link and finish screens without their navigation state go to
    `/login` (a reload keeps the state in the history entry, which is harmless); a failed `DELETE /api/me` keeps the reauth ticket
    (Tasks 16 to 18).
16. **Playwright** uses the fake's fixed work person for the unverified screen only, and fresh addresses everywhere else (Task 19).
17. **The six production variables are optional** (`${...:-}`) and the two new documents join the default list of `scripts/check-docs.py`
    (Task 20).

## Open questions for the owner

1. **A test with real keys** before the release (slice 0013's `v0.2.0`): one sign-in with a real Google client and one with a real Microsoft
   registration (a personal and a work account), against a deployed instance. It settles what the fake cannot: Q14 (that a personal Microsoft
   account's identity token carries `oid` and `tid` as the plan reads them), the consent screens, and `xms_edov` as Entra sends it. Who runs it,
   and when?
2. **The `SameSite=Lax` request-forgery cookie on a real phone.** Playwright proves the cross-site return in Chromium and WebKit; Safari on an
   iPhone with its tracking prevention is not proven (the threat model says the same of the refresh cookie). Add it to the first real
   deployment's phone check (step 7 of the Angular guide)?
