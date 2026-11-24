# Spec 0012 — GitHub, and managing one's own logins

- **Status:** Approved (owner, 2026-05-04), with the Amendment of 2026-05-04 and Amendment B of 2026-11-24 (after specs 0015 to 0017)
- **Date:** 2026-05-04
- **Author:** Alex
- **Milestone:** Phase 2, slice 0012 — *"a person manages their linked logins"*
  ([`docs/design.md`](../../design.md), "Phase 2: sign-up and social login").
- **Context:** specs [0002](0002-refresh-and-logout.md) (sessions, the refresh cookie, the security stamp),
  [0003](0003-lockout-and-abuse-resistance.md) (lockout), [0004](0004-email-flows.md) (mail queue, password policy,
  reset), [0008](0008-hardening-and-release.md) (per-IP limits, audit log, `DELETE /auth/org`),
  [0010](0010-private-accounts.md) (`DELETE /auth/account`) and [0011](0011-google-and-microsoft-sign-in.md) (providers,
  tickets, `auth_pt`, reauth, its Amendment A9 and its As built). This spec changes parts of 0002, 0008, 0010 and 0011;
  each change is named where it is made.

## Goal

A person signs in with GitHub as they do with Google and Microsoft: "Continue with GitHub" on the sign-in, sign-up and
invitation screens.

A signed-in person manages how they sign in, on a settings screen of the product: they see their password (set or not)
and every provider, connect a provider, disconnect one (never the last way in), and set or change their password.

- Connecting and disconnecting a provider ask the person to confirm who they are — with their password, or, without a
  password, by signing in at a linked provider again — unless they signed in or confirmed on this device in the last 5
  minutes.
- Changing the password always asks for the current password. A person without a password sets their first one
  through the mail of "forgot password" (spec 0004), which the settings screen sends for them. The 5 minutes never cover
  the password.
- Each of these changes mails the account's address, so that a change the person did not make does not go unnoticed.
- Disconnecting a provider and changing the password sign every other device out; this device stays signed in. A first
  password set through the mail is a reset, which signs every device out (spec 0004).

A company admin without a password deletes the company by confirming with a provider (closes Amendment A9 of spec
0011).

A product that does not list `github` and calls none of the new endpoints sees these changes only: `GET /auth/me` gains
`confirmed_until`; `DELETE /auth/org` accepts a second kind of confirmation it never gets; the link of spec 0011 mails
the account and refuses a second login of one provider; the manifest accepts `github`; audit rows gain `details.via`;
and every sign-in records its session's start and confirmation window (one migration).

Concretely, this is met when this sequence works against a running instance with the fake provider, all three providers
listed and private sign-up on:

```
curl -i .../auth/options          # -> 200 {..., "providers":["google","microsoft","github"]}

# browser: "Continue with GitHub" -> GET /auth/providers/github/start -> GitHub -> /callback
#   -> Auth-Core reads /user and /user/emails, revokes the GitHub token
#   -> 302 <provider return screen>#ticket=<t>&purpose=sign_in -> exchange -> finish -> signed in, no password

# settings, the same person, signed in less than 5 minutes ago, so connecting asks for no confirmation:
curl -i .../auth/me -H 'Authorization: Bearer <a>' -b 'auth_rt=<r>'
                                  # -> 200 {..., "has_password":false, "linked_providers":["github"], "confirmed_until":"…"}
# browser: "Connect Google" -> GET /auth/providers/google/start?purpose=connect -> ... -> #ticket=<c>&purpose=connect
curl -i -X POST .../auth/providers/connect -H 'Authorization: Bearer <a>' -b 'auth_rt=<r>; auth_pt=<b>' -d '{"ticket":"<c>"}'
                                  # -> 204; the mail "Google was connected to your account" is queued

# a first password goes through the mail ("Set a password" on the settings screen sends it):
curl -i -X POST .../auth/password/forgot -d '{"email":"ola@gmail.test"}'                # -> 202, the reset mail is queued
curl -i -X POST .../auth/password/reset -d '{"token":"<m>","new_password":"<p>"}'       # -> 204, every session ends
curl -i -X POST .../auth/login -d '{"email":"ola@gmail.test","password":"<p>"}'         # -> 200, a new session <a2>, <r2>

# six minutes later, disconnecting GitHub:
curl -i -X POST .../auth/account/logins/unlink -H 'Authorization: Bearer <a2>' -b 'auth_rt=<r2>' -d '{"provider":"github"}'
                                  # -> 403 {"error":"confirmation_required"}
curl -i -X POST .../auth/account/confirm -H 'Authorization: Bearer <a2>' -b 'auth_rt=<r2>' -d '{"password":"<p>"}'
                                  # -> 204
curl -i -X POST .../auth/account/logins/unlink -H 'Authorization: Bearer <a2>' -b 'auth_rt=<r2>' -d '{"provider":"github"}'
                                  # -> 200, signed in afresh on this device; the mail "GitHub was disconnected" is queued
```

## In scope

- GitHub as a third provider: the manifest name `github`, its keys in the environment, the round trip through
  OpenIddict's GitHub web provider, the identity read by Auth-Core from `/user` and `/user/emails`, the GitHub token
  revoked at once on every path.
- Sessions record their start and the end of their confirmation window (changes spec 0002); a sign-in opens the window.
- The confirmation window: `POST /auth/account/confirm`, 5 minutes bound to the session of this device; `GET
  /auth/me` gains `confirmed_until`.
- Connecting a provider from a signed-in session: `start?purpose=connect`, `POST /auth/providers/connect`.
- Disconnecting a provider: `POST /auth/account/logins/unlink`, never the last way in.
- Changing the password while signed in: `POST /auth/account/password`, against the current password every time; a
  first password through "forgot password", sent from the settings screen.
- Signing every other device out on a disconnect and on a password change, this device kept signed in.
- Three notification mails: a provider connected (by the link of spec 0011 and by the connect), a provider
  disconnected, the password changed. The link of spec 0011 refuses a second login of one provider.
- `DELETE /auth/org` with a provider confirmation for an admin without a password (spec 0011, Amendment A9).
- Per-IP limits, lockout, audit events, OpenAPI.
- The fake provider gains GitHub; a Development-only endpoint that ends a window for browser tests; integration, unit,
  Angular and Playwright tests; `scripts/e2e-settings.sh`.
- `samples/notes-web`: "Continue with GitHub"; a `/settings` screen (sign-in methods, password, "Delete account" moved
  here from the notes screen); "Delete company" on `/team`. `samples/notes-api`: `DELETE /api/org`.
- Documents: the keys guide gains GitHub, the threat model, the Angular guide, the deployment guide, `docs/design.md`,
  the changelog, the environment examples.

## Out of scope

- Hardening and the `v0.2.0` release: slice 0013.
- A Google-style account switcher: a later separate item.
- More than one login of one provider per account (two GitHub accounts on one product account).
- Removing the password of an account.
- A list of sessions or devices, and a separate "sign out every other device" button.
- Confirming by a code sent by mail.
- GitHub Enterprise Server, or GitHub organisations limiting who signs in.
- Following a change of address at a provider (as spec 0011).
- Real GitHub, Google and Microsoft keys and a live test with them: an open item for the owner (as spec 0011,
  Decision 16).

## Contract

### General rules

- Every rule of specs 0002 to 0011 holds unless this spec changes it: JSON bodies of at most 8 KiB in UTF-8, unknown
  members ignored, `400 invalid_request` for a body that is not a JSON object or a member of the wrong type;
  `Cache-Control: no-store`; the security headers; the `415` of a body in another charset and the per-IP `429` come
  first.
- **Provider names** are `google`, `microsoft` and `github` (changes spec 0011).
- **The session of this device** is the session (spec 0002) that the request's refresh cookie `auth_rt` belongs to. The
  four endpoints of the settings screen need both the access token and the refresh cookie (`Path=/auth`, so the browser
  sends it). The cookie names a session when its refresh token is of the type refresh, valid or redeemed within the
  reuse leeway of spec 0002 (15 seconds), not expired, its authorization valid, and its subject equal to the access
  token's `sub`; it is read, never redeemed. Anything else, or no cookie: `401 {"error":"session_required"}`, after the
  access token's own `401`.
- **The account must still have a membership** when a settings endpoint acts (it reads the membership again under the
  address lock, as the sign-in does): an account without one is `403 no_membership`, nothing written. An account deleted
  meanwhile has no session: `401 session_required`.
- **Checks under the lock.** Every check of an endpoint below that reads the account's logins, password or membership is
  made again inside its transaction under the address lock (spec 0010, "Locks"), and the answer is the one that check
  gives there. A refusal writes nothing and uses nothing up, unless its row says otherwise.
- **No log line and no audit row** holds a password, a ticket, a cookie, a GitHub token or code.

### Manifest (changes spec 0011)

`providers` accepts `github` beside `google` and `microsoft`; any other value still makes the manifest invalid. The
order is the order of the buttons.

### Configuration

| Key | Environment variable (`deploy/docker-compose.prod.yml`) | Rule |
| --- | --- | --- |
| `Auth:Providers:GitHub:ClientId`, `Auth:Providers:GitHub:ClientSecret` | `AUTH_GITHUB_CLIENT_ID`, `AUTH_GITHUB_CLIENT_SECRET` (optional) | Both set, or the provider is not configured |
| `Auth:Testing:WindowEndpoint` | none (development stacks only) | Development only: `true` maps `POST /auth/testing/end-confirmation` (below). Set outside Development, it stops the start, naming the key |

- **A provider is offered** as spec 0011 says (listed, both keys, the return address); the health rule of spec 0011
  (`Degraded` for a listed provider that is not offered, one warning naming the missing keys) covers GitHub.
- **The callback address** for GitHub is the return address's origin followed by `/auth/providers/github/callback`, as
  for the others. The operator registers it as the "Authorization callback URL" of a GitHub **OAuth App**.
- **The test issuer.** With `Auth:Providers:TestIssuer` set (Development only), GitHub is registered as a plain OAuth 2.0
  client of the fake under `{TestIssuer}/github` instead of OpenIddict's GitHub web provider: the fake serves a minimal
  discovery document there (Amendment A5), the scope is `user:email` with no `openid`, PKCE is sent, and Auth-Core's
  GitHub reader (below) calls the fake's `/user`, `/user/emails` and revocation under the same address. As a plain
  OAuth 2.0 client it has no identity token and no `nonce`; the document does not advertise the `iss` parameter, so
  OpenIddict checks the state and not `iss` (Amendment A6). The reader, the identity rules and everything after them are
  the same code in both modes.

### GitHub: the round trip and the identity

GitHub is OAuth 2.0, not OpenID Connect: there is no identity token, no `nonce` and no discovery document (the test
issuer's fake serves a minimal one, Amendment A5).

1. **`start`** (spec 0011, unchanged rules) sends the browser to `https://github.com/login/oauth/authorize` with the
   code flow, the `state` bound to the browser by OpenIddict's request-forgery cookie, PKCE (`S256`, which GitHub accepts,
   Amendment A7), the scope `user:email`, the callback address, and `prompt=select_account` for every purpose (GitHub
   has nothing like `prompt=login`, and no `max_age` is sent to GitHub for any purpose, Amendment A7).
   GitHub sends `iss` (`https://github.com/login/oauth`) on every authorization answer and OpenIddict's GitHub provider
   requires it: an answer without it is `provider_error`, `access_denied` included (Amendment A6).
2. **`callback`** checks the state as for the others and redeems the code with the client secret; GitHub answers with
   an access token. OpenIddict's own userinfo step stays off for every provider (spec 0011, userinfo off). Instead
   **Auth-Core's GitHub reader**, an event handler of OpenIddict's client that runs after the token response and before
   Auth-Core's callback handler, calls through a named HTTP client of Auth-Core's own, without OpenIddict's retry
   pipeline, one attempt per call (the tests route it to the fake as they route OpenIddict's clients, Amendment A4),
   with `Accept: application/vnd.github+json` and a `User-Agent` naming Auth-Core (GitHub refuses a request without
   one), each call bounded to 10 seconds and its body to 64 KiB:
   - `GET https://api.github.com/user`, and
   - `GET https://api.github.com/user/emails?per_page=100`;

   then it revokes the token (step 4) in a `finally`, and hands the callback handler only the identity below, through
   the client's authentication properties (`context.Properties`), never as a principal. After the revocation it removes
   the token from the context, so that the callback handler never sees it (Amendment A3). A token answer without
   `access_token` is `provider_error`.
3. **The identity:**

| | Identity key (`AspNetUserLogins.ProviderKey`) | Address | Verified when |
| --- | --- | --- | --- |
| GitHub | `id` of `/user`, the decimal number as a string (never `login`, which the person can change) | the entry of `/user/emails` with `primary` `true` (never another entry, never `email` of `/user`) | that entry has `verified` `true` |

   - No entry with `primary` `true` among the first 100, or more than one: the identity has **no address** (`no_email` at
     the exchange, as spec 0011).
   - The address must pass the invitation rule (spec 0005); an address that does not is treated as unverified, as spec
     0011 says.
   - `/user` without an `id`, a `/user` or `/user/emails` answer that is not `200`, not the documented JSON (an `id` that
     is not a positive integer, an entry whose `email` is not a string or whose `primary` or `verified` is not a boolean),
     larger than 64 KiB, or slower than 10 seconds: `#error=provider_error`.
   - `access_denied` from GitHub with a valid state and cookie: `#error=cancelled`, as spec 0011.
4. **The GitHub token is revoked on every path once it exists**, success or failure (a failed read, a missing `id`), by
   the reader right after its reads and before anything else of the callback runs: `DELETE
   https://api.github.com/applications/{client_id}/token`, basic authentication with the client id and secret, body
   `{"access_token"}`. It is never stored. A revocation that fails (any status but `204`, a timeout) logs one warning
   naming the provider and the status, never the token, and the round trip goes on as it would have.
5. From the ticket on, GitHub is the same as the others: the exchange, the link, the finish, the invitation, reauth and
   connect, with the rules of spec 0011 and this spec.

### Sessions (changes spec 0002)

- **Each session records its start and the end of its confirmation window**, in the table `SessionStates` keyed by its
  OpenIddict authorization and removed with it (Amendment A8). The routine that every sign-in uses to write the session
  (`AtomicSignInResult`: the login, the provider sign-ins of spec 0011, the sign-up completion) creates the authorization
  of a new session itself and writes both values in the same transaction: the start is now, the window ends 5 minutes
  from now. The clock is the service's `TimeProvider`.
- A refresh keeps the authorization, so it keeps both values, and it never moves the window.
- The values cannot outlive their session: a sign-out, the end of the session, `AccountSessions.EndAllAsync` and a
  renewal revoke the authorization, and a revoked authorization names no session (General rules); an account deletion
  deletes it, and `TokenPruner` removes it with its values.
- **A renewal** (the unlink and the password change, below) ends every session and issues a new one to this device. Its
  principal is built after the change (for a password change, after the new security stamp), with the stored start as
  `session_start` and the refresh lifetime of spec 0002 counted from that start (`SessionPolicy.RemainingLifetime`), so
  that the 30-day cap holds. The new authorization gets the old start and the old window. A session past its cap cannot
  be renewed: the endpoint answers `401 session_required` and changes nothing.
- **A session that began before this version** has no stored values. Its start is its authorization's creation time (a
  refresh keeps the authorization, so that is when the session began); its window is closed, also after a renewal.
  `POST /auth/account/confirm` writes both values when they are missing.

### Provider tickets (changes spec 0011)

- A fourth purpose, `connect` (`smallint` `4`), made by `start?purpose=connect`. The fragment of the callback carries
  `purpose=connect`. A `connect` ticket lives 10 minutes, as a `sign_in` ticket.
- `start` accepts `purpose=connect`; `purpose` other than `reauth` and `connect`, or both `invite` and `purpose`, is
  still `400 invalid_request`. The start still reads no account.
- `POST /auth/providers/exchange`, `/link` and `/finish` answer a `connect` ticket as a ticket of another purpose
  (`400 invalid_ticket`); `DELETE /auth/account`, `DELETE /auth/org` and `POST /auth/account/confirm` answer it as `403
  reauth_failed`.
- A `connect` ticket of a provider dropped from the manifest after its callback is honoured for its 10 minutes, as a
  `sign_in` ticket is (spec 0011, Settled 27).
- A browser holds one `auth_pt` (spec 0011, Settled 9): a later round trip in the same browser — a `reauth` for a
  confirmation included — makes an earlier ticket unusable.

### The confirmation window

- **What it is.** The session of this device is **confirmed until** a time. While that time is in the future, the
  connect and the unlink act without asking again. Otherwise they answer `403 {"error":"confirmation_required"}` and
  change nothing.
- **What opens it**, for 5 minutes from that moment:
  - a sign-in that starts the session: a login (spec 0001), a provider sign-in, link, finish or invitation acceptance
    (spec 0011), a sign-up completion (spec 0009). An invitation accepted with a password (`204`, no sign-in, spec 0005)
    opens nothing;
  - `POST /auth/account/confirm` (below).
- **What it is not.** It is bound to one session, not to the account: a confirmation on one device opens nothing on
  another. A refresh keeps it and never extends it. A sign-out or the end of the session closes it. The new session that
  the unlink and the password change hand to this device carries the window of the old one, unchanged.
- **What it never covers.** `POST /auth/account/password` (the current password), `DELETE /auth/account` and `DELETE
  /auth/org` take their own confirmation in the body every time, whatever the window says; a first password needs the
  mailbox.

### `POST /auth/account/confirm` — `{"password"}` or `{"provider_ticket"}`

Authenticated with the access token and the refresh cookie. The checks, in this order (they mirror `DELETE
/auth/account`, spec 0011):

1. A body that is not a JSON object; both `password` and `provider_ticket`, or neither; `password` not a string, blank
   or holding NUL; `provider_ticket` not a string: `400 invalid_request`.
2. The session of this device: `401 session_required`. No membership: `403 no_membership`.
3. The confirmation must fit the account: an account **with** a password and a body with `provider_ticket`: `403
   password_required`; an account **without** a password and a body with `password`: `403 reauth_required`. Nothing is
   counted and no row is written.
4. **The password** is judged as login judges it: the attempt is counted in the account's login streak first; while the
   identifier is locked `429 too_many_attempts` with `retry_after_seconds` and `account.confirm_refused`
   (`details.reason` `locked`); a wrong password `403 wrong_password` and `account.confirm_refused` (`details.reason`
   `wrong_password`); a right one clears the streak.
5. **The provider ticket** must be a ticket of the purpose `reauth`, unused, not expired (5 minutes), with its
   `auth_pt`, whose identity is a login of **this** account. Anything else: `403 reauth_failed` and
   `account.confirm_refused` (`details.reason` `reauth_failed`); nothing is used up. The lockout is not consulted.
6. Otherwise, in one transaction under the address lock, where step 2 is checked again: the session of this device is
   confirmed until now plus 5 minutes (its stored values are written if it has none); the ticket, if any, is used up and
   `auth_pt` cleared; `account.confirmed` is written (`details.method` `password` or the provider's name); the answer is
   `204`.

### `POST /auth/providers/connect` — `{"ticket"}`

Authenticated with the access token and the refresh cookie, and `auth_pt`. The checks, in this order:

1. A body that is not a JSON object, or a `ticket` that is missing or not a string: `400 invalid_request`.
2. The session of this device: `401 session_required`. No membership: `403 no_membership`.
3. A ticket that is unknown, used, expired, not of the purpose `connect`, or without its `auth_pt`: `400
   invalid_ticket`.
4. The window is closed: `403 confirmation_required`. The ticket stays usable until it expires, as long as no other
   round trip replaces `auth_pt` in this browser (Provider tickets).
5. The identity is already a login of **this** account: `204`, nothing written, no mail; the ticket is used up.
6. The identity is a login of another account: `409 provider_already_linked`.
7. This account already has a login of this provider: `409 {"error":"provider_in_use"}` (one login per provider per
   account; the person disconnects the old one first).
8. The ticket has no address: `403 no_email`. Its address is unverified: `403 email_not_verified`. Its address is not
   the account's address (`EmailInput.IsSameAddress`): `403 address_mismatch`.
9. Otherwise, in one transaction under the address lock, where steps 5 to 7 are checked again (two connects of one
   provider at once: the second answers by those steps, `204` for the same identity, `409 provider_in_use` for another):
   the identity becomes a
   login of the account; `provider.linked` is written with `details.via` `settings`; the ticket is used up and `auth_pt`
   cleared; the mail "provider connected" is queued. The answer is `204`. Sessions are not touched: adding a way in signs
   nobody out.

### `POST /auth/account/logins/unlink` — `{"provider"}`

A token-endpoint request of OpenIddict in pass-through mode with a placeholder request, as the exchange of spec 0011
is, so that the answer can hand this device a new session. The route requires the access token
(`.RequireAuthorization()`), as the company API does, and answers its plain `401` (Amendment A9); its path is added to
the token endpoint's paths with a placeholder handler of its own, so that `UnhandledTokenRequestGuard` lets it through,
and `LoginResponseShaper` shapes its answer as a login's. The charset guard already covers it, as every `POST` under
`/auth/`. Authenticated with the access token and the refresh cookie. The checks, in this order:

1. A body that is not a JSON object, or a `provider` that is missing or not a string: `400 invalid_request`.
2. The session of this device: `401 session_required`. No membership: `403 no_membership`.
3. The window is closed: `403 confirmation_required`.
4. `provider` is not a provider name, or the account has no login of it: `404 not_found`. A login of a provider that is
   no longer offered can be disconnected. An account that has more than one login of the provider (the link of spec
   0011 could add a second before this version) loses all of them.
5. **Never the last way in.** A way in is the password or a login of an offered provider. When the account would have
   no way in after the unlink: `409 {"error":"last_login"}`. So a login of a provider that is no longer offered can be
   disconnected only while the password or a login of an offered provider remains.
6. Otherwise, in one transaction under the address lock, where steps 2, 4 and 5 are checked again (two unlinks of one
   account at once: the first ends every session, so the second finds its own session gone, `401 session_required`, and
   the account keeps a way in): the login rows are deleted, which waits for any sign-in that holds them (below); every
   session of the account ends — its refresh tokens and authorizations (spec 0002, Decision 11) only, not its email links
   or its login streak, so this is not `AccountSessions.EndAllAsync` but a part of it; a renewed session is issued to this
   device (Sessions); `provider.unlinked` and
   `login.succeeded` (`details.via` `session_renewal`) are written; the mail "provider disconnected" is queued. The
   answer is that of a login: `200 {"status":"authenticated","access_token"}` and the refresh cookie.

### `POST /auth/account/password` — `{"current_password", "new_password"}`

A token-endpoint request like the unlink, authenticated the same way. Authenticated with the access token and the
refresh cookie. It changes the password of an account that has one, against the current password, every time; the window plays no part. An account
without a password sets its first one through "forgot password" (spec 0004), never here (Decision 17). The members are
named as at `POST /auth/password/reset`. The checks, in this order:

1. A body that is not a JSON object; `new_password` missing, not a string or holding NUL; `current_password` missing,
   not a string, blank or holding NUL: `400 invalid_request`.
2. The session of this device: `401 session_required`. No membership: `403 no_membership`.
3. The account has no password: `403 {"error":"password_not_set"}`. Nothing is counted and no row is written.
4. The new password breaks the policy of spec 0004: `400 weak_password` with `rules`, as at a reset. Nothing is counted.
5. **The current password** is judged as at `POST /auth/account/confirm`, step 4: counted first; `429
   too_many_attempts` and `403 wrong_password` with `account.confirm_refused` (`details.reason` `locked` or
   `wrong_password`, `details.action` `password`); the streak cleared when right.
6. Otherwise, in one transaction under the address lock, where steps 2 and 3 are checked again, in this order
   (Amendment A2): every email link of the account is voided; the account's row is read again `FOR SHARE`, as the link
   of spec 0011 does, and its hash must be the one that was judged: a reset that used its link up and committed while the
   change waited for that link is `403 wrong_password` with `account.confirm_refused` (`wrong_password`, `details.action`
   `password`), not counted again, nothing else written. (A reset that committed before the change took the address lock
   ended every session, this one's too: step 2, checked again first, answers `401 session_required`, nothing written.) Then: the password hash is set (the security stamp changes, as at a reset); every session of the
   account ends (its refresh tokens and authorizations: the effect of `AccountSessions.EndAllAsync`, in an order that
   cannot deadlock with a reset); its login streak is cleared; a new session is issued to this device with the start and
   the window of the old one; `password.changed` and `login.succeeded` (`details.via` `session_renewal`) are written; the
   mail "password changed" is queued. The answer is that of a login: `200 {"status":"authenticated","access_token"}` and
   the refresh cookie. Two changes of one account at once: the first ends every session, so the second is `401
   session_required`.

**A reset and a password change of one account at once** (Amendment A1, Decision 19): the first one wins and neither
answers `500`. A reset that used its link up first and commits while the change waits for that link: the change then
finds the hash changed and answers `403 wrong_password` as step 6 says. A reset that committed before the change took the
address lock ended every session, the change's too: the change answers `401 session_required` (step 6 checks step 2
again first), nothing written. A change that voided the links first makes the reset's link invalid (`400 invalid_token`,
spec 0004). `POST /auth/password/reset` itself is unchanged.

The account keeps its logins (spec 0011, Decision 15, unchanged).

### `DELETE /auth/org` — `{"name", "password"}` or `{"name", "provider_ticket"}` (changes spec 0008 and spec 0011, Amendment A9)

The checks of spec 0008 stay, in its order, with these changes, which mirror `DELETE /auth/account` (spec 0011):

- **The body** holds `name` and exactly one of `password` (a string, not blank, no NUL) and `provider_ticket` (a
  string). Both, neither, or a member of the wrong type: `400 invalid_request`.
- **Before the lockout is consulted**, the confirmation must fit the caller's account: an account with a password and a
  body with `provider_ticket`: `403 password_required`; an account without a password and a body with `password`: `403
  reauth_required` (as Amendment A9 answers today). Nothing is counted and no row is written.
- **The provider ticket** must be a ticket of the purpose `reauth`, unused, not expired, with its `auth_pt`, whose
  identity is a login of the caller's account: else `403 reauth_failed` and `org.delete_refused` with `details.reason`
  `reauth_failed`, nothing deleted and nothing used up. The lockout is not consulted.
- **The ticket is used up inside the deletion's transaction** (`CompanyDeletionService`, after the company lock and
  before the name check, so that a refused deletion rolls it back, Amendment A12), with `provider.reauthenticated`; a
  ticket used up meanwhile by another request is `403 reauth_failed`, nothing deleted. The answer clears `auth_pt`.
- The name check and safety rule 1 of spec 0008 come after the confirmation, as today.

### `GET /auth/me` (changes spec 0011)

Adds one member:

```json
{ "...": "…", "has_password": false, "linked_providers": ["github", "google"], "confirmed_until": "2026-05-04T18:35:00Z" }
```

- `confirmed_until` is the end of the window of this device's session, in UTC, ISO 8601 with seconds, while it is in the
  future; `null` when it has passed, when the request has no refresh cookie, or one that names no session of this
  account (General rules).
- `linked_providers` may now hold `github`. It holds each provider name once, sorted, even for an account that the link
  of spec 0011 gave two logins of one provider before this version; from this version the connect and the link refuse a
  second login of one provider (`provider_in_use`).

### Sign-in of a linked identity, and the link (changes spec 0011)

- **A sign-in holds the login it signs in with.** The exchange (spec 0011, step 3.1) and the link (step 3, an identity
  linked meanwhile) read the identity's login row again `FOR SHARE` inside the transaction that issues the session. A
  login deleted meanwhile — an unlink that committed first — is `400 invalid_ticket`, nothing written; an unlink that
  comes second waits for the sign-in and then ends the session it issued. So no session of an unlinked identity outlives
  the unlink.

- **One login per provider.** After the password is judged right and after the checks of spec 0011's step 7 (the
  email confirmed, a membership), an account that already has a login of the ticket's provider is `409
  {"error":"provider_in_use"}`; the ticket is kept and nothing else is written (the streak is cleared, as for a right
  password). This is checked again under the address lock in step 8. The exchange still answers `next` `link` in that
  case: it does not tell whether the account has such a login before the password is given.
- **A link queues the mail "provider connected"** in its transaction (step 8), and `provider.linked` gains `details.via`
  `sign_in`.

### Mails (changes spec 0004 and spec 0011, Settled 28)

Three new mail kinds, in Polish and English (`Auth:App:Locale`), sent to the account's address:

| Mail kind | Queued by | Says |
| --- | --- | --- |
| `ProviderLinked` | `POST /auth/providers/link` and `POST /auth/providers/connect` | `{Provider}` was connected to the account `{email}`; if it was not you, reset your password with "Forgot your password?" on the sign-in screen, then disconnect `{Provider}` in your settings |
| `ProviderUnlinked` | `POST /auth/account/logins/unlink` | `{Provider}` was disconnected from the account; if it was not you, reset your password and check your settings |
| `PasswordChanged` | `POST /auth/account/password` | the password of the account was changed; if it was not you, reset it with "Forgot your password?" |

- **The queue.** A queued row of these kinds holds the address and the provider's name (a new nullable column of
  `MailRequests`; never a token). It is written in the transaction of the change that causes it, not through the mail
  request path of spec 0004 (`MailRequestStore.SubmitAsync`), and is not counted by the per-address limit of spec 0004.
  `MailDispatchSignal.Notify` runs after that transaction commits.
- **A limit of its own** (Decision 18): at most 10 rows of these three kinds together per address in a fixed window of
  one hour, opened by the first accepted notice of the address, with no minimum gap between two (the window rule of spec
  0004's limit, Amendment A10), counted in the store of the mail limits of spec 0004 under a key of its own. Beyond it
  the change still happens, no row is written, and one warning names the kind, never the address.
- **The dispatcher.** Both of its statements (`DropUnmailableAsync` and `DispatchOneAsync`) take these kinds into the
  reset's branch (Amendment A11): such a row needs an account with the address, whatever the state of its email
  confirmation. It has a prepare step of its own, which issues no link token and takes the provider's name from the row,
  and a composer method without a link; a row whose address has no account any more (deleted meanwhile) is dropped. They are never treated as a verification mail. The give-up of spec 0004 (a row not sent within an hour is
  dropped) holds for them.
- **No mail carries a link or a token**: each names the product's sign-in screen in words only, so that a forged copy of
  it cannot point anywhere.
- **No mail** for a password reset (the person used their own mailbox), for an account made through a provider (the
  finish, the invitation), or for a connect of an identity that is already this account's login.

### Limits (changes spec 0008 and spec 0011)

Per-IP: `POST /auth/account/confirm`, `POST /auth/providers/connect` and `POST /auth/account/password` count against the
`login` policy (each judges a password or ends a provider round trip); `POST /auth/account/logins/unlink` and the
testing endpoint against `general`. `GET /auth/providers/{name}/start` and `/callback` keep the `login` policy for every provider and purpose.

The lockout of spec 0003: a password at `POST /auth/account/confirm` and a current password at `POST
/auth/account/password` count in the account's streak, as at `DELETE /auth/account`. A provider confirmation neither
counts nor clears it.

### Testing endpoint (Development only)

With `Auth:Testing:WindowEndpoint` `true` (Development only, Configuration), `POST /auth/testing/end-confirmation`,
authenticated with the access token and the refresh cookie and without a body, ends the window of this device's
session (`204`). It is not in the OpenAPI document and is `404` when the setting is off; the tests that check every
`/auth/` route against the OpenAPI document run with the setting off. It exists so that a browser test can reach the
closed window without moving the server's clock.

### Audit events (changes specs 0008, 0010 and 0011)

Four new kinds (32 in all):

| Kind | Written when | Fields |
| --- | --- | --- |
| `account.confirmed` | `POST /auth/account/confirm` opens the window | `actor_user_id`, `subject_user_id`, `subject_email`, `org_id`, `org_name`, `details.method` (`password`, `google`, `microsoft`, `github`) |
| `account.confirm_refused` | a confirmation at `POST /auth/account/confirm`, or the current password at `POST /auth/account/password`, is refused for a wrong password, a lock, or a bad ticket | the same, `details.reason` (`wrong_password`, `locked`, `reauth_failed`), `details.action` (`confirm`, `password`) |
| `provider.unlinked` | `POST /auth/account/logins/unlink` deletes a login, in its transaction | the same, `details.provider` |
| `password.changed` | `POST /auth/account/password` sets the new hash, in its transaction | the same |

- `provider.linked` gains `details.via`: `sign_in` for the link of spec 0011, `settings` for the connect.
- `login.succeeded` gains `details.via` `session_renewal` for the session that the unlink and the password change hand
  to this device (the runbook's queries of logins can leave them out).
- `provider.reauthenticated` is also written by `DELETE /auth/org` with a ticket; `org.delete_refused` gains the reason
  `reauth_failed`.
- `login.succeeded`, `signup.completed` and `invite.accepted` carry `details.provider` `github` for GitHub.
- No row holds a password, a ticket, a cookie, a GitHub code or token (`AssertNoSecretsAsync`).

### OpenAPI

Added: `POST /auth/account/confirm`, `POST /auth/providers/connect`, `POST /auth/account/logins/unlink`, `POST
/auth/account/password`, each with its answers, the refresh cookie and `auth_pt`. Changed: `GET /auth/me`, `DELETE
/auth/org`, `POST /auth/providers/link`, the `purpose` of `GET /auth/providers/{name}/start`, the provider names. The
testing endpoint is not listed. The counts pinned by `OpenApiHardeningTests` rise: 40 operations, 28 under the charset
guard.

### Python package (`clients/python`)

No change: a GitHub sign-in and a renewed session issue the same claims as a password login.

### Sample frontend (`samples/notes-web`)

- **The buttons.** "Continue with GitHub" (and "Sign up with GitHub", "Accept with GitHub") beside the others, in the
  order of `GET /auth/options`, on `/login`, `/signup` and `/invite`, by the rules of spec 0011.
- **The header** gains a link "Settings" for a signed-in person.
- **`/settings`** (new, behind the auth guard), three sections:
  - **"Sign-in methods".** A row "Password" ("Set" or "Not set") and one row per offered provider: "Connected" with
    "Disconnect", or "Connect". A login of a provider that is no longer offered gets a row too, with "Disconnect" only.
    "Disconnect" is disabled when the account would have no way in after it (no password and no other login of an
    offered provider), with the sentence "Set a password or connect another provider before you disconnect this one."
  - **"Password".** With a password: "Change password" — the current password, the new password twice, the rules shown
    as at the reset screen. Without one: "Set a password" — one button "Send me a link", which calls `POST
    /auth/password/forgot` with the account's address and shows "Check your inbox: the link sets your password. Setting
    it signs you out everywhere." The link opens the existing `/reset` screen.
  - **"Delete account"**, moved here from the notes screen, unchanged in behaviour (specs 0010 and 0011); the `reauth`
    return of its "Confirm with {Provider}" now lands on `/settings`. For a company member the section says "A company
    account is deleted by the company's admins." and has no button (`DELETE /auth/account` is for private accounts, spec
    0010). The notes screen keeps no account section.
- **Confirming a connect or a disconnect.** Before either, the screen reads `GET /auth/me`. When `confirmed_until` is
  `null` or past, a dialog "Confirm it's you" opens: the password field and "Confirm" for an account with a password;
  one button "Confirm with {Provider}" per linked offered provider otherwise; with neither, "None of your sign-in methods
  can confirm here. Set a password first." with "Send me a link". After a password confirmation the action
  goes on at once. After a provider confirmation the browser comes back to `/settings` with the window open, and the
  person clicks the action again; the sample carries no secret across the round trip, only the flag below.
  - A connect starts its round trip only after the window is open. A `403 confirmation_required` at the connect (the
    window closed during the round trip) opens the dialog: after a password confirmation the sample sends the ticket
    again; after a provider confirmation that ticket is gone (one `auth_pt`), and the screen says "Confirmed. Connect
    {Provider} again."
- **The return screen** (`/provider-return`) handles `purpose=connect` (sends `POST /auth/providers/connect`, then
  `/settings` with "{Provider} connected." or the sentence of the error) and a `reauth` return for the settings screen
  (sends `POST /auth/account/confirm`, then `/settings`). The one-shot flag of spec 0011's As built (D30) gains its
  target: `delete_account`, `delete_company`, `confirm` or `connect`; a return whose purpose does not fit the flag's target goes
  to `/login` and sends nothing. The flag still holds no ticket, address or kind.
- **Answers.** `401 session_required` signs the person out to `/login` with "Your session ended. Sign in again." (it
  never starts a refresh). While an unlink or a password change is in flight the sample sends no other request with the
  refresh cookie (no refresh, no other settings call), so that none of them carries the cookie the answer replaces.
  `403 password_not_set` redraws the section from a fresh `GET /auth/me`. `409 last_login` "Set a password or connect another provider first."; `409 provider_in_use`
  "You already have a {Provider} account connected. Disconnect it first."; `409 provider_already_linked` "This {Provider}
  account belongs to another account."; `403 address_mismatch` "This {Provider} account has another address. Connect
  the one with {email}."; `403 email_not_verified` "{Provider} has not verified this address."; `403 no_email`, `400
  invalid_ticket`, `403 reauth_failed` and `403 wrong_password` one sentence each; `429` shows the wait; `400
  weak_password` the broken rules; `403 no_membership` as at login. Each button is disabled while its request is in
  flight. The link screen of spec 0011 (`/provider-link`) gains `409 provider_in_use`: "This account already has a
  {Provider} account connected. Sign in with it or with your password."
- **After an unlink or a password change** the sample stores the new access token from the answer; the refresh cookie
  is already the new one.
- **`/team`, for a member holding `org:delete`:** "Delete company": the company's name typed again, then the password,
  or "Confirm with {Provider}" for an admin without a password. The sample compares the typed name with `org_name` of
  `GET /auth/me` first (a mismatch: "Type the company's name exactly.", nothing sent). With a password it then calls
  `POST /auth/account/confirm` with it, so that a wrong password is caught before any data goes. Then it calls `DELETE
  /api/org` of notes-api and, after its `204`, `DELETE /auth/org`; then it signs out and shows "The company was
  deleted." Answers of `DELETE /auth/org`: `400 invalid_request` "Type the company's name exactly."; `403
  permission_not_held` "A member has permissions you do not have, so you cannot delete the company."; `403
  permissions_changed` signs out as on the other company screens; `403 wrong_password` and `429` as at the dialog; `403
  reauth_failed` "The confirmation did not work or took too long, so the company was not deleted. Its notes are already
  gone. Confirm again."; `403 password_required` and `403 reauth_required` redraw from a fresh `GET /auth/me`.
- **Earlier sample tests that this changes** follow it: `notes.account.spec.ts`, `notes.provider.spec.ts` and the delete
  steps of the Playwright specs of 0010 and 0011 move to `/settings`; `11-providers.spec.ts` counts three buttons.
- Texts in `texts.ts`, English, as in spec 0007. Tickets stay in memory and the router's navigation state only, and
  out of the history entry (`ticket-history.ts`, spec 0011, As built), on `/settings` and `/team` as on the other
  screens.

### Sample backend (`samples/notes-api`)

- **`DELETE /api/org`** (new): for a token whose `org_kind` is `organization` and whose `permissions` hold `org:delete`,
  deletes every note of the token's `org_id` and answers `204`; `403` otherwise. It mirrors `DELETE /api/me` of spec
  0010, with a larger reach: any holder of `org:delete` wipes the company's notes before Auth-Core has judged the
  confirmation (risk 51).
- Its `auth.yaml` lists `providers: [google, microsoft, github]`; the stacks that use it set the fake's GitHub keys.
  `deploy/auth.yaml`'s commented `providers` line names `github` too.

### Documents

- **`docs/deployment/provider-keys.md`**: a GitHub part: GitHub → Settings → Developer settings → OAuth Apps → New, the
  homepage URL, the callback address as the "Authorization callback URL", a client secret, and the client id and secret
  into `.env`; that GitHub asks for no consent screen setup and grants `user:email`; how to check it with the sample.
- **`docs/security/threat-model.md`**: the section "Sign-in with Google and Microsoft" becomes "Sign-in with Google,
  Microsoft and GitHub" (GitHub's round trip, the two API reads, the revocation); a section "Managing logins" (the
  confirmation window, connect, unlink, the password change, the mails) with STRIDE rows; GitHub's endpoints in the
  diagram; the residual risks below.
- **`docs/integration/angular.md`**: the settings screen (the four endpoints, `confirmed_until`, the dialog, the new
  session in the answer of an unlink and a password change, the flag's target, the answers) and "Delete company", with
  the warning of risk 51 for a product's own company-data deletion.
- **`docs/deployment/vps.md`**, **`deploy/docker-compose.prod.yml`**, **`deploy/.env.prod.example`**: the two GitHub
  variables, optional, with comments; the callback address to register.
- **`docs/design.md`**: the Phase 2 section (a paragraph for this spec). **`CHANGELOG.md`** (Unreleased): GitHub, the four
  endpoints, the window, the mails, the audit kinds, `DELETE /auth/org`, the link's `provider_in_use`, `confirmed_until`.

**Residual risks** in the threat model's list:

- **51** (widened): the product deletes its data first. For a company, any holder of `org:delete` in the product can
  wipe the company's data before Auth-Core judges the confirmation. The sample checks the name and a password first
  (`POST /auth/account/confirm`); an admin without a password, or a ticket that expires between the wipe and the
  deletion, still leaves a company with no data.
- **52, 53, 55, 56, 58** (widened): they hold for GitHub as for Google and Microsoft; GitHub's OAuth App secret does not
  lapse (risk 54 stays Microsoft's).
- **2** (widened): the per-identifier lockout that a distributed guesser can trigger now also blocks the owner's
  `POST /auth/account/confirm` and password change while it lasts, as it blocks their login.
- **57** (changed): re-authentication is as strong as the provider makes it; for GitHub it is usually one click, or
  none, because GitHub cannot be asked for a fresh sign-in. Script in the product's page that opens such a round trip in a
  window of the same origin can read the ticket there (risk 62).
- **59** (rewritten): a provider login is now mailed when it is added and can be disconnected from the settings screen,
  and a disconnect signs the other devices out (their access tokens still work for up to 10 minutes). What remains: a
  person who ignores the mail keeps the intruder's login until they act; and a stale "verified" flag at a provider can
  still create the account of an address nobody has used yet (the mailbox owner then sees the login in their settings
  after "forgot password" and disconnects it).
- **62** (new): **the confirmation window.** For 5 minutes after a sign-in or a confirmation, script running in the
  product's page (an XSS) can disconnect a provider of the account, as long as another way in remains. Each disconnect
  mails the account. It cannot connect its own provider (the connect needs a verified address equal to the account's),
  cannot change the password (the current one is asked every time) and cannot set a first one (that needs the mailbox).
  A script that can open a `reauth` round trip in a window of the same origin, at a provider that asks for no fresh
  sign-in (GitHub, risk 57), can read the ticket from the return screen and reopen the window, or confirm a deletion, as
  often as that provider lets it.
- **63** (new): **a GitHub token that could not be revoked** stays valid at GitHub with the scope `user:email`, unused
  and not stored, until the person revokes the app in their GitHub settings or GitHub expires it after a year of
  disuse. The log names the failure.
- **64** (new): **notification mails as noise.** Script in the page that can drive silent GitHub round trips can
  connect and disconnect GitHub in turn; each change mails the account, up to the limit of 10 an hour (Decision 18),
  after which the changes go on unmailed.

## Acceptance criteria (Done when)

Each criterion names the kind of test that guards it. "Integration" is a .NET test against PostgreSQL with the fake
provider in process; "unit" is a .NET test without a host.

GitHub

1. The manifest accepts `github` beside `google` and `microsoft`, keeps the order and folds duplicates; any other value
   is still invalid. `GET /auth/options` lists `github` only when it is listed and both its keys and the return address
   are set; a listed GitHub without its keys makes health `Degraded` with one warning naming the keys. Guard: unit
   (`ManifestProvidersTests`) and integration (`ProviderOptionsTests`, `ProviderSettingsTests`).
2. `start` for GitHub answers `302` to GitHub's authorization endpoint with `response_type=code`, a `state`, the scope
   `user:email`, `prompt=select_account` and the callback address from the return address's origin, for every purpose;
   with `code_challenge_method=S256` (Amendment A7) in both modes, and never `max_age`. Guard: integration
   (`GitHubStartTests`).
3. A GitHub callback with a good code reads `/user` and `/user/emails?per_page=100`, revokes the token (the fake records
   the revocation), and answers `302` with `#ticket=` and the purpose; the ticket holds the identity key `id` and the
   primary address with its `verified` flag. Guard: integration (`GitHubCallbackTests`).
4. The GitHub identity rules: the key is `id`, never `login`; the address is the primary entry only (a verified
   secondary entry is never used); a primary entry with `verified` `false` is unverified; no primary entry is no
   address; an address that breaks the invitation rule is unverified. Guard: unit (`GitHubIdentityTests`), one test per
   rule.
5. The GitHub callback answers `#error=cancelled` for `access_denied`, `#error=invalid_state` as for the others, and
   `#error=provider_error` for a refused code, a `/user` or `/user/emails` answer that is not `200` or not the documented
   JSON, a `/user` without `id`, an authorization answer without `iss` (Amendment A6, `access_denied` included) and a
   token answer without `access_token` (Amendment A3); none makes a ticket, and in each case where a token was issued
   the fake records its revocation, once (Amendment A4). A failed revocation still makes the ticket and logs one warning
   without the token. Guard: integration (`GitHubCallbackErrorTests`), one test per path.
6. With a GitHub ticket, the exchange, the link, the finish and the invitation acceptance behave as spec 0011's
   criteria 10 to 22 say for Google (sign-in of a linked identity, `next` `link` and `finish_signup`, `no_email`,
   `address_mismatch`, a private and an organization finish); `details.provider` is `github`. Guard: integration
   (`GitHubFlowTests`).
7. No log line and no audit row holds the GitHub code, token or client secret. Guard: integration with a captured log
   (`GitHubCallbackTests`, `AuditSettingsFlowsTests`).

Sessions and the confirmation window

8. A login, a provider sign-in, a link, a finish, an invitation acceptance through a provider and a sign-up completion
   each record the session's start and open the window for 5 minutes; a password acceptance of an invitation opens
   none. At 5 minutes and one second (a controlled clock) `GET /auth/me` says `confirmed_until` `null` and the connect
   and the unlink answer `403 confirmation_required` and change nothing. A refresh keeps the start and the window and
   does not extend it. Guard: integration (`ConfirmationWindowTests`).
9. The window is bound to the session: a confirmation in one session leaves another session of the same account
   unconfirmed; a sign-out closes it; after `EndAllAsync`, a renewal or an account deletion the old session's values
   name nothing. `GET /auth/me`
   without the refresh cookie, with another account's, or with a revoked one says `null`; a refresh cookie redeemed
   within the 15-second leeway still names the session. Guard: integration (`ConfirmationWindowTests`).
10. `POST /auth/account/confirm` with the right password answers `204`, opens the window, writes `account.confirmed`
    (`password`) and clears the streak; a wrong password is `403 wrong_password`, counted, with `account.confirm_refused`
    (`wrong_password`); after ten, `429 too_many_attempts` with `account.confirm_refused` (`locked`). Guard: integration
    (`AccountConfirmTests`).
11. `POST /auth/account/confirm` with a `reauth` ticket of a linked identity of this account answers `204`, uses the
    ticket up, clears `auth_pt`, writes `account.confirmed` with the provider; a `sign_in` or `connect` ticket, an
    expired, used or foreign one, or one without `auth_pt` is `403 reauth_failed` with `account.confirm_refused` and the
    ticket untouched. `password_required` and `reauth_required` as in spec 0011; `400 invalid_request` for both members
    or neither; `401 session_required` without the refresh cookie; `403 no_membership` for an account whose membership
    was removed. Guard: integration (`AccountConfirmTests`).

Connect and link

12. A connect with an open window and a `connect` ticket whose verified address is the account's adds the login, writes
    `provider.linked` (`via` `settings`), uses the ticket up, clears `auth_pt`, queues the "provider connected" mail,
    and leaves every session alive; afterwards the provider alone signs in. Guard: integration (`ProviderConnectTests`).
13. The connect answers `400 invalid_ticket` for a ticket of another purpose, unknown, used, expired or without
    `auth_pt`; `403 confirmation_required` with the ticket still usable; `204` with nothing written for an identity that
    is already this account's login; `409 provider_already_linked`; `409 provider_in_use`; `403 no_email`, `403
    email_not_verified` and `403 address_mismatch`; two connects of one provider to one account at once add one login
    (the second `204` for the same identity, `409 provider_in_use` for another); a `connect` ticket of a provider dropped
    after its callback is honoured. Guard: integration (`ProviderConnectTests`, with parallel requests).
14. A `connect` ticket is refused by the exchange, the link and the finish (`invalid_ticket`) and by both deletions and
    the confirmation (`reauth_failed`); `start?purpose=connect` reads no account; the fragment
    says `purpose=connect`. Guard: integration (`ProviderTicketTests`, `ProviderStartTests`).
15. The link of spec 0011 refuses an account that already has a login of the ticket's provider with `409
    provider_in_use` after a right password, keeps the ticket and writes nothing; a successful link queues the "provider
    connected" mail and writes `provider.linked` with `via` `sign_in`. A sign-in of a linked identity (exchange, link)
    racing the unlink of its login either ends before the unlink, whose end of sessions then ends its session, or answers
    `400 invalid_ticket`; no session of the identity survives. Guard: integration (`ProviderLinkTests`, extended;
    `ProviderUnlinkRaceTests`, with parallel requests and a held lock).

Unlink

16. An unlink with an open window deletes the login, ends every other session (their refresh is `401 invalid_grant`),
    answers `200` with a new access token and refresh cookie for this device whose session keeps its start and its
    window, writes `provider.unlinked` and `login.succeeded` (`via` `session_renewal`), queues the "provider
    disconnected" mail, and leaves the email links and the streak alone; afterwards that provider identity no longer
    signs in to the account. Guard: integration (`ProviderUnlinkTests`).
17. The unlink answers `403 confirmation_required`, `404 not_found` for an unknown name and for a provider the account
    has no login of, and `409 last_login` when no way in would remain (the only login of an account without a password;
    the last offered login when the other login's provider is no longer offered); a login of a provider no longer
    offered can be disconnected while a way in remains; two logins of one provider from before this version both go;
    a renewed session keeps the 30-day cap of its start, and one past its cap is `401 session_required`; two unlinks of the two logins of an account without a password, sent at once
    from one session or from two, leave one login (one `200`, the other `401 session_required`); `401 session_required`
    without the refresh cookie; `403 no_membership` for a removed membership, nothing written. Guard: integration
    (`ProviderUnlinkTests`, with parallel requests).

Password

18. `POST /auth/account/password` with the right `current_password` changes the password (`password.changed`), ends
    every other session, voids the account's email links, clears the streak, answers `200` with a new session for this
    device (keeping its start and window), queues the "password changed" mail and keeps the logins; afterwards the new
    password signs in and the old one does not. An open window alone never lets it act. Guard: integration
    (`AccountPasswordTests`).
19. The password endpoint answers `400 invalid_request` for a bad body; `403 password_not_set` for an account without a
    password, not counted; `400 weak_password` with the broken rules, not counted; `403 wrong_password` (counted) and
    `429 too_many_attempts` with `account.confirm_refused` (`action` `password`); `403 wrong_password` with
    `account.confirm_refused` and not counted again when a reset that used its link up first committed while it waited
    for that link; `401 session_required`, nothing written, when a reset committed before it took the address lock (the
    reset ended its session); and a change that voided the links first makes the reset's link `400 invalid_token` (none
    of the three answers `500`, Amendment A1); two changes at once give one `200` and one `401 session_required`; `401
    session_required` without the refresh cookie; `403 no_membership`. Guard: integration (`AccountPasswordTests`, with
    parallel requests).
20. A reset (spec 0004) still keeps the logins and sends no "password changed" mail; on an account without a password
    it sets the first one, as spec 0011 says. Guard: integration (`ProviderPasswordResetTests`, extended).

Company deletion

21. An admin without a password deletes the company with `{"name", "provider_ticket"}` and a `reauth` ticket of a linked
    identity: `204`, the company gone as spec 0008 says, `provider.reauthenticated`, `auth_pt` cleared. A password is
    still `403 reauth_required`; an admin with a password and a ticket is `403 password_required`; a bad ticket is `403
    reauth_failed` with `org.delete_refused` (`reauth_failed`) and nothing deleted; two deletions with one ticket delete
    once; both members or neither is `400 invalid_request`. The answers of spec 0008 for a password are unchanged.
    Guard: integration (`OrgDeletionProviderTests`; the tests of spec 0008 pass).

Mails, `/auth/me`, limits, audit, API

22. The three mails are sent in the instance's locale (Polish and English tested) to the account's address, name the
    provider, hold no link and no token, are written in the transaction of their change, and are not counted by the
    per-address mail limit of spec 0004; the eleventh in an hour for one address is not queued and the change still
    happens, with one warning; a row of a confirmed account is sent, not dropped as a verification mail; a row whose
    account was deleted before dispatch is dropped; the link of spec 0011 queues
    "provider connected"; a finish, an invitation through a provider and a reset queue none of them. Guard: integration
    with the dispatcher run by hand (`SettingsMailTests`) and unit (`MailComposerTests`, extended).
23. `GET /auth/me` returns `confirmed_until` as above and `linked_providers` with `github`. Guard: integration
    (`MeProvidersTests`, extended).
24. Confirm, connect and password count against the `login` policy, and unlink and the testing endpoint against
    `general`; the testing endpoint is `404` with its setting off, and the setting outside Development stops the start naming the key. Guard: unit
    (`RatePolicyTests`) and integration (`RateLimitMiddlewareTests`, `TestingEndpointTests`).
25. The audit log knows 32 kinds; each new row and `details` member holds the fields above; `AssertNoSecretsAsync`
    passes for every row written by the tests of this spec. Guard: integration (`AuditSettingsFlowsTests`).
26. The OpenAPI document lists the four new operations and the changed ones, and not the testing endpoint; 40
    operations, 28 under the charset guard; a POST of this spec in another charset is `415`. Guard: integration
    (`OpenApiHardeningTests`, `JsonCharsetTests`).
27. An instance with the manifest and settings of spec 0011 and no GitHub keys starts `Healthy`, issues the same tokens,
    keeps the sessions it had before the upgrade (a session without a stored start or window refreshes as before, its
    window closed), lists an account's two logins of one provider once in `linked_providers`, and passes its earlier
    tests, except those that pin what this spec changes (the members of `/auth/me`, the counts of audit kinds and OpenAPI
    operations, the provider names the manifest accepts, the mail and the `provider_in_use` of the link), which are
    changed to this spec. Guard: integration (`UpgradeFromSpec0011Tests`)
    and the whole suite.

Samples and end to end

28. `samples/notes-web` unit tests cover: the GitHub buttons on the three screens; `/settings` (the rows for each
    combination of password and logins, the disabled "Disconnect" and its sentence, a login of a provider no longer
    offered); the dialog (password, one button per linked offered provider, opened by `confirmed_until` and by a `403
    confirmation_required`, the ticket resent after a password confirmation, "Connect {Provider} again" after a provider
    one); "Change password" (the current password) and "Set a password" (the call of `POST /auth/password/forgot` with
    the account's address and its sentence);
    `/provider-return` for `connect` and for each `reauth` target, and the flag's target refusing a mismatch; every
    answer of the "Answers" list, `401 session_required` signing out without a refresh; no request with the refresh
    cookie while an unlink or a password change is in flight; the buttons disabled in flight; the link screen's
    `provider_in_use`;
    the new access token stored after an unlink and a password change; "Delete account" on `/settings` and its text for
    a company member; the dialog with no usable method; "Delete company" (the name compared first, the password confirmed
    before any deletion, the call order `DELETE /api/org`, then `DELETE /auth/org`, the password or the provider, each
    answer). The earlier tests that this changes are changed with it. Guard: Angular unit tests.
29. `samples/notes-api` tests cover `DELETE /api/org` (`204` and the notes of the company gone, other companies'
    untouched; `403` for a private token and for a token without `org:delete`). Guard: pytest
    (`test_delete_org_data.py`).
30. A Playwright spec (`samples/notes-web/e2e/12-settings.spec.ts`) passes in Chromium and WebKit against the fake
    provider, on the development stack with `Auth:Testing:WindowEndpoint` on, every person made with a fresh address of
    the fake's free-address mode so that the spec runs in both browsers and again on the same stack: a new person signs
    up with "GitHub" and is
    signed in; on `/settings` connects "Google" without being asked to confirm; sets a password through "Send me a link"
    and the reset mail (read from the development mail catcher) and signs in with it; after the testing endpoint ends the
    window, disconnects "GitHub" after confirming with the password; changes the password with the current one; a
    person with only one provider sees "Disconnect" disabled; an admin without a password deletes the company with
    "Confirm with Google". The Playwright specs of 0010 and 0011 pass with their delete steps on `/settings` and three
    provider buttons. Guard: Playwright.
31. `scripts/e2e-settings.sh` passes against a fresh stack with the fake provider and the real 5-minute window (the
    testing endpoint is used to close it, never a shorter window): options with `github`, a GitHub round trip by the
    fake's non-interactive mode, the token revocation seen by the fake, confirm (password and ticket), connect, unlink,
    the password change, the first password through "forgot password", these refusals — `confirmation_required`,
    `session_required`, `wrong_password`, `password_required`, `reauth_required`, `reauth_failed`, `invalid_ticket`,
    `provider_in_use`, `provider_already_linked`, `address_mismatch`, `email_not_verified`, `no_email`, `last_login`,
    `not_found`, `password_not_set`, `weak_password` — company deletion with a ticket, the three mails read from the
    development mail catcher and counted through the notification-limit row (Amendment A13), and the audit rows read
    from the database. Every earlier e2e script passes, `scripts/e2e-providers.sh`
    with its options step expecting three providers. Guard: e2e script.
32. The fake provider serves GitHub's authorization, token, `/user`, `/user/emails` and revocation endpoints, by host
    name behind the HTTP clients of OpenIddict and of the reader in the tests (`github.com`, `api.github.com`, with `iss`
    on the authorization answers, Amendment A6) and under `{TestIssuer}/github` in the stack, with its minimal discovery
    document (Amendment A5), for three people (a verified primary address, an unverified primary address, no primary address) and on
    demand for a failing `/user`, a failing `/user/emails` and a failing revocation, and records each revocation. Its
    free-address mode of spec 0011 serves GitHub too (an address, a "verified" box, an identity key derived from the
    address), so that a test can make a GitHub person and a Google person with one fresh address. Guard: the tests above
    run against it.
33. The whole .NET suite passes in one run on Windows and through `scripts/test-linux.sh` (a red Linux run that only the
    machine's Docker causes is the owner's call, as spec 0011's As built, D33, records); the Python package's and the
    samples' suites pass unchanged except where this spec changes them. Guard: the suites.
34. The documents of "Documents" exist and say what this spec says; the keys guide names every value the operator copies
    and every address they register for GitHub. Guard: `scripts/check-docs.py` and review.

## Decisions (owner, 2026-05-04)

From the brainstorm of this slice:

1. **Scope.** GitHub; the settings screen (list, connect, disconnect, never the last way in); the answer to risk 59; the
   provider confirmation for deleting a company (Amendment A9); and setting or changing the password while signed in.
   Hardening and `v0.2.0` stay in slice 0013.
2. **GitHub's address is the primary address only, and only when GitHub verified it.** One GitHub account is one
   address, as with Google and Microsoft. An invitation is not accepted through a GitHub account whose primary address
   differs from the invited one, even when another of its verified addresses is the invited one.
3. **Every sensitive change is confirmed with what the person has**: their password, or, without one, a fresh sign-in at
   a linked provider. A stolen access token alone changes nothing.
4. **GitHub counts as a confirmation**, although GitHub cannot be asked for a fresh sign-in; the residual is named
   (risk 57).
5. **A provider connected from the settings screen must have the account's address**, as linking at sign-in already
   requires (spec 0011, Decision 7: only the same address links): a company keeps control of access through the address
   it gave.
6. **Mails.** Connecting any provider (by the link at sign-in and from the settings screen), disconnecting a provider,
   and changing the password each mail the account's address (a first password is a reset, Decision 17). A reset and an
   account made through a provider send none. A reset keeps the logins (spec 0011, Decision 15, unchanged); the mail plus the settings screen answer
   risk 59.
7. **A password cannot be removed**, only changed; only providers are disconnected, and never the last way in.
8. **Changing the password signs every other device out**; this device stays signed in (a first password is a reset,
   which signs every device out, Decision 17).
9. **Disconnecting a provider signs every other device out**; this device stays signed in (a session does not know
   which provider opened it, so all others end).
10. **The sample gets a `/settings` screen** (sign-in methods, password, "Delete account" moved there) **and "Delete
    company" on `/team`**, with the provider confirmation, so that Amendment A9 is visible and tested end to end;
    notes-api gets `DELETE /api/org` for the company's notes.
11. **The confirmation window (the common pattern).** Accounts without a password stay, as in most products that offer
    "Continue with Google". Connecting and disconnecting a provider need a sign-in or a confirmation on this device in
    the last 5 minutes, the lifetime of a `reauth` ticket, as GitHub's "sudo mode", Google's "confirm it's you" and the
    `max_age` of Keycloak and Auth0 work. A confirmation for every change and a password required for every account
    were weighed and set aside: the first needs two provider round trips for a person without a password, the second
    takes away what "Continue with Google" offers.
12. **One login per provider per account**, and **`GET /auth/me` says `confirmed_until`**, so that the screen asks for
    the confirmation before it acts.
13. **GitHub's round trip** reads `/user` and `/user/emails` with the token and revokes it at once; the fake provider
    gains GitHub; the residuals 57, 59, 62 and 63 are as above.
14. **Samples, tests and documents** as in the sections above; the Python package does not change.

Added to the draft (owner, 2026-05-04):

15. **The password always asks, like a deletion**: changing it needs the current password every time; the window
    covers only connecting and disconnecting providers. Without this, script in the page could set its own password
    during the 5 minutes and take the account over.
16. **No limit of its own on the notification mails** — replaced by Decision 18: it rested on the belief that a script
    could cause only a few of them, which silent GitHub round trips make wrong.

17. **A first password is set through the mail**, as Slack and Notion do: "Set a password" on the settings screen sends
    the reset mail of "forgot password" (spec 0004) to the account's address. A fresh sign-in at GitHub is not fresh
    (risk 57), so script in the page could otherwise obtain a `reauth` ticket silently and set its own password; it
    cannot read the mailbox. `POST /auth/account/password` only changes an existing password. A first password set this
    way is a reset: it signs every device out and sends no "password changed" mail (Decisions 6 and 8 read so).
18. **A small limit on the notification mails**: at most 10 of them per address in an hour. Script that can drive silent
    GitHub round trips could otherwise connect and disconnect GitHub in turn and send about 30 a minute, bounded only by
    the per-IP limits. Beyond the limit the change still happens and the mail is dropped; the first mails have already
    told the person.

Added after the probe (owner, 2026-05-04):

19. **A reset and a password change of one account at once: the first one wins**, and neither answers `500` (Amendment
    A1). A reset that used its link up first and commits while the change waits for that link: the change answers `403
    wrong_password`; a reset that committed before the change took the address lock ended every session, so the change
    answers `401 session_required` and writes nothing; a change that voided the account's links first makes the reset's
    link `400 invalid_token`. Making the reset win in every case would need
    the change to leave alive the links it cannot lock, so a link would outlive a password change.

### Settled in this spec

Choices the decisions left open; the contract above reads this way.

1. **The connect needs a verified address**, not only an equal one: otherwise script in the window could connect a
   Microsoft work identity that claims the person's address unverified. A person whose provider address is unverified
   links at sign-in with the password, as spec 0011 allows.
2. **The window lives 5 minutes**, the lifetime of a `reauth` ticket (spec 0011), so one number covers both.
3. **The window is bound to the session of the refresh cookie**, not to the access token (10 minutes, and renewed by
   every refresh) nor to the account (a confirmation on one device must open nothing on another). So the settings
   endpoints and `GET /auth/me` read the refresh cookie, which `Path=/auth` already sends; they never redeem it.
4. **A sign-in opens the window**: a person who signed in a minute ago has just proved who they are. A refresh does not:
   it proves only that the cookie is held.
5. **The session's start and window are stored in a table keyed by its authorization** (Amendment A8), removed with it,
   written by the one routine every sign-in uses and copied by a renewal: the refresh token cannot be read without redeeming it, and the new authorization's creation
   time would reset the 30-day cap at every renewal. A session from before this version takes its start from its
   authorization's creation time, which is right for it because no renewal has happened yet.
6. **Paths and methods.** `POST /auth/account/confirm`, `POST /auth/providers/connect`, `POST
   /auth/account/logins/unlink` and `POST /auth/account/password`. The unlink and the password change hand this device a
   new session, so they are token-endpoint requests in pass-through mode, which OpenIddict accepts only as `POST`; the
   other two are plain `POST`s for symmetry.
7. **"Every other device signed out, this one kept" is "every session ends, this device gets a new one"**: a password
   change moves the security stamp, which ends every refresh of the account (spec 0004), so the current session cannot
   simply be spared. The new session keeps the old one's start, so that a change never stretches the 30-day cap.
8. **An unlink ends the sessions but not the email links or the streak**: a pending reset link of the person must not be
   voided by an intruder's disconnect. A password change ends all three, as a reset does.
9. **The connect signs nobody out**: adding a way in harms no session, and the mail reports it.
10. **The confirmation mirrors `DELETE /auth/account`** (`password_required`, `reauth_required`, `wrong_password`,
    `reauth_failed`, the lockout for a password, none for a ticket, nothing used up by a refusal), and reuses the
    `reauth` purpose of spec 0011. The password endpoint judges the current password as the confirmation does, but first
    the new password's policy, so that a weak new password costs no attempt; its members are named as at the reset, and
    `password_not_set` sends a person without a password to the mail.
11. **A new ticket purpose `connect`**, not `sign_in`: a `sign_in` ticket must never connect to whoever is signed in in
    that browser, and a `connect` ticket must never sign anyone in.
12. **Notification mails carry no link and no token**, are written in the transaction of their change with the
    provider's name in the queue row, and the dispatcher sends them to any account of the address, confirmed or not.
13. **`account.confirmed` and `account.confirm_refused`** are kinds of their own, as `account.deleted` and
    `account.delete_refused` are; `provider.unlinked` and `password.changed` because no existing kind says what
    happened (`password.reset` is the mail-link flow). The renewed session is a `login.succeeded` with `details.via`
    `session_renewal`, so a query of sign-ins can count it or leave it out.
14. **Confirm, connect and password on the `login` policy, unlink on `general`**: the first three judge a password or
    end a round trip; the unlink acts on the person's own account behind an open window.
15. **A way in is the password or a login of an offered provider**: a login of a provider dropped from the manifest
    signs nobody in, so it cannot be what keeps an account reachable. It can still be disconnected while a way in
    remains, and is kept otherwise, for the day the provider is offered again.
16. **Checks that read logins, the password or the membership are made again under the address lock**, so that two
    unlinks at once cannot leave an account without a way in and two connects cannot add two logins of one provider.
17. **A GitHub OAuth App**, not a GitHub App: it is what OpenIddict's GitHub provider and GitHub's `user:email` scope
    expect, and its setup is one form.
18. **Auth-Core reads GitHub's identity itself**, in one reader used by both registrations: OpenIddict's GitHub provider
    reads `/user` only, and its userinfo step is off for every provider (spec 0011).
19. **`prompt=select_account` for every GitHub purpose**, so that a person with several GitHub accounts picks the linked
    one at a reauth and the right one at a connect.
20. **The revocation runs on every path and never fails the round trip**: the token must not outlive the request
    however it ends, and a failed revocation leaves a named residual (risk 63) rather than a person who cannot sign in.
21. **The flag of D30 carries its target**, so that a `reauth` return can serve the dialog, "Delete account" and
    "Delete company" without a crafted link reaching any of them. Nothing secret crosses a round trip: the
    person clicks the action again after a provider confirmation.
22. **Browser tests close the window by a Development-only endpoint**, not by a shorter window: a short window would
    make "connects without being asked" race the fake's round trip, and the e2e script must see the real 5 minutes.
23. **`DELETE /auth/org` uses its ticket up inside the deletion's transaction**, so that one ticket deletes once.
24. **While an unlink or a password change is in flight, the sample sends nothing else with the refresh cookie**: the
    answer replaces the cookie, and a request that carried the old one would answer `401 session_required` and sign the
    person out of the session they just kept. Another tab of the same browser can still send a refresh in that moment
    and be signed out; that tab signs in again.
25. *(Withdrawn by Amendment A1: the reset does not retry; the password change voids the links first, and the first of
    the two wins.)*
26. **File names.** `scripts/e2e-settings.sh`, `samples/notes-web/e2e/12-settings.spec.ts`,
    `samples/notes-api/tests/test_delete_org_data.py`.

### Amendment (2026-05-04, after the probe)

A probe of OpenIddict 7.7.1 on this code (`.superpowers/sdd/0012-github-and-managing-logins/probe-report.md`) answered the
"To verify" items before the plan was written. Two items change what a consumer or a person sees: A1, the owner's
decision, and A10, the window of the notice limit; A13 changes how criterion 31 is checked; the others say how the
contract is built. Each item names what it changes, and the sentences of the contract, the criteria and the Settled
items it touches now read so.

- **A1 (owner, Decision 19) — a reset and a password change at once.** The order of the password change as first
  written deadlocks with a reset, which holds the link it used up while it waits for the share lock: the reset would
  answer `500`. Now the first one wins and neither answers `500`. The change voids the account's email links first (A2),
  so a reset that used its link up first and commits while the change waits for that link: the change then finds the
  hash changed and answers `403 wrong_password` with `account.confirm_refused` (`wrong_password`, `details.action`
  `password`), not counted again, nothing else written; a change that voided the links first makes the reset's link
  invalid (`400 invalid_token`, spec 0004). **The third outcome (orchestrator decision D42):** a reset that committed
  before the change took the address lock ended every session, the change's too, so the change's second read of its
  session (step 2 again, which comes first under the lock) answers `401 session_required`, nothing written. None of the
  three outcomes is a `500`. Withdrawn: the paragraph "A reset that waited for the share lock" and its change of spec 0004, the reset's retry
  and Settled 25. Criteria 19 and 27 read so.
- **A2 — the order of the password change.** In one transaction under the address lock: the account's email links are
  deleted; the account's row is read `FOR SHARE` and its hash compared with the one that was judged; the new hash is set
  (the security stamp changes); the account's refresh tokens and authorizations are revoked (the effect of
  `AccountSessions.EndAllAsync`, in an order that cannot deadlock with a reset, not that method itself); the login streak
  is cleared; then the renewed session.
- **A3 — the GitHub reader's hand-over.** The reader hands the identity to the callback through the client's
  authentication properties (`context.Properties`), never as a principal. After the revocation it removes the token from
  the context (`BackchannelAccessToken = null`, `RequireBackchannelAccessToken = false`), so that the token is never in
  the callback's properties. A token answer without `access_token` is `provider_error`: the reader refuses it
  (OpenIddict alone would make it `invalid_state`).
- **A4 — the reader's HTTP client.** A named `HttpClient` of Auth-Core's own, without OpenIddict's retry pipeline: one
  attempt per call (OpenIddict's client sends a failed read, and a failed revocation, up to four times), each call bounded
  to 10 seconds and its body to 64 KiB. The tests route it to the fake as they route OpenIddict's clients. It is the
  fallback that "To verify" named.
- **A5 — the test issuer for GitHub.** The fake serves a minimal discovery document under `{TestIssuer}/github`
  (`issuer`, `authorization_endpoint` at the browser's origin, `token_endpoint`, `jwks_uri` with one throwaway key,
  `response_types_supported: ["code"]`, `code_challenge_methods_supported: ["S256"]`), so that the stack's two origins
  work: one static address cannot be both. The registration has the scope `user:email` and no `openid`; PKCE is sent in
  both modes. It replaces "static endpoints".
- **A6 — `iss` at GitHub.** OpenIddict's GitHub provider requires `iss=https://github.com/login/oauth` on every
  authorization answer (GitHub sends it since April 2026): an answer without it is `provider_error`, `access_denied`
  included. The fake sends it on GitHub's hosts; the test issuer's document does not advertise the parameter, so the
  plain registration does not require it.
- **A7 — PKCE and `prompt` at GitHub.** GitHub accepts PKCE with `S256` (only), and criterion 2 asserts
  `code_challenge_method=S256` in both modes. GitHub documents `prompt=select_account`. No `max_age` is sent to GitHub
  for any purpose: the `max_age=0` of a re-authentication is Google's alone.
- **A8 — the session values.** A table `SessionStates` keyed by the authorization's id, `ON DELETE CASCADE`, with
  `SessionStart` and `ConfirmedUntil`. For a new session `AtomicSignInResult` creates the authorization itself and
  writes the row in the same transaction. The settings endpoints and `GET /auth/me` find the session of this device
  through OpenIddict's token manager (`FindByReferenceIdAsync`), never by redeeming the cookie. The clock is
  `TimeProvider`.
- **A9 — the access token at the four settings endpoints.** Each route requires it (`.RequireAuthorization()`), which
  gives the same plain `401` as the company API. Only the unlink and the password change are token-endpoint paths, and
  `LoginResponseShaper` shapes their answers. The charset guard already covers every `POST` under `/auth/`: nothing is
  added to it, and the count of 28 operations under it (`OpenApiHardeningTests`) stands.
- **A10 — the window of the notice limit.** A fixed window of one hour, opened by the first accepted notice of the
  address, at most 10 notices in it, no minimum gap between two, under a limit key of its own in the store of spec
  0004's limits: the window rule of spec 0004's limit. It replaces "a sliding hour" (Mails); Decision 18 keeps the
  owner's words, "in an hour".
- **A11 — the dispatcher and the composer.** The three kinds join the reset's branch of both statements of the
  dispatcher (an account of the address, confirmed or not), have a prepare step of their own (no token; the provider
  from the row) and a composer method without a link, in Polish and English.
- **A12 — `DELETE /auth/org` with a ticket.** The ticket is used up inside `CompanyDeletionService`'s transaction, after
  the company lock and before the name check, so that a refused deletion (a wrong name, safety rule 1) rolls it back.
- **A13 (orchestrator, D41) — how criterion 31 reads the mails.** On a running stack the dispatcher sends a queued
  notification row and removes it within about a second, so a read of the queue table after the answer races it.
  `scripts/e2e-settings.sh` therefore reads the three notification mails from the development mail catcher (Mailpit):
  the recipient, the subject in the instance's language, and that the text and HTML parts hold no link; and it checks
  their count through the notification-limit row of the store of the mail limits (`MailRequestLimits`, the key of
  Amendment A10). The queue rows themselves (kind, address, provider name, no token) are asserted by the integration
  tests with the dispatcher run by hand (criterion 22). Criterion 31 reads so.

### Amendment B (2026-11-24, after specs 0015, 0016 and 0017)

Specs 0015 (several companies per account, `POST /auth/switch`, `GET /auth/orgs`), 0016 (`POST /auth/orgs`, `403 company_member` on `DELETE /auth/account`) and 0017
(an invitation joins an existing account) shipped in v0.3.0 after this spec was written. A probe of this spec against them
(`reconcile-probe.md`) found two items that change what a consumer or a person sees (B3 and B5, owner decisions) and several that say how the contract is built.
Each item names the sentences of the spec it changes. Items A1 to A13 above stand; none contradicts 0015 to 0017.

- **B1 - the lock order.** Every path of this spec that takes more than one lock takes them in this order: the company lock, the address lock, the identity lock
  (`IdentityLock`, only where a path uses it), the membership row(s), the account row, the login rows. A settings endpoint takes the caller's membership row before
  the account row, as sign-in and `AccountDeletion` do on 0015. The sign-in's hold of its login (account `FOR KEY SHARE`, then the login row `FOR SHARE`) keeps its order.
  The identity lock in a provider invitation sits after the address lock and before the account row's `FOR UPDATE`, before the `already_registered` and
  `provider_already_linked` decisions. This changes Decisions 43 to 45 and Settled 25 and 26 only in order, not in what they judge.
- **B2 - the token's company.** A settings endpoint (`confirm`, `connect`, `unlink`, `password`) acts in the company of the access token's `org_id`. A token without
  `org_id` is `403 no_membership`. The `confirmed_until` of `GET /auth/me` is the window of the session, whatever company the token is for. Changes the "caller" rule of the
  four endpoints and criterion 8.
- **B3 - the renewed session keeps the company (owner).** After an unlink or a password change the renewed session is in the company the caller's token is in now: the new tokens
  carry `session_org_id` and `session_org_joined` of the old token, and the old start and the old window. Nothing jumps. Visible: a person who switched to company B and changes the
  password is still in B. Changes the paragraphs of the renewed session in the unlink and the password change (and criteria 16 and 17).
  Guard: `scripts/e2e-settings.sh` step 8b and the `14-settings.spec.ts` test "after a password change in a switched company the person is still in that company, with the window open".
- **B4 - the window across a switch and a refresh.** A refresh and a switch keep the session's start and its window (`NextPrincipal` copies the authorization id): a switch inside
  the window does not close it, and does not extend it. A settings call and a switch spend the same refresh cookie, so the sample sends a settings call that renews only after a
  switch in flight has answered. Adds to the "Answers" list of the sample frontend.
- **B5 - "Delete account" on `/settings` (owner).** Replaces the spec's lines about 503 to 506 (the bullet **"Delete account"**) with:

  > - **"Delete account"**, moved here from the notes screen. An account that is a member of any company (`GET /auth/orgs` lists a space of kind `organization`) sees the text
  >   "You are still a member of {names}. Delete those companies or ask their admins to remove you, then delete your account." (the text of spec 0016, the names from the list) and
  >   no button. An account with a personal space only deletes as before (specs 0010 and 0011): the pre-check of `GET /auth/orgs`, the notes, then `DELETE /auth/account`,
  >   and the server's `403 company_member` stays the last guard. The `reauth` return of "Confirm with {Provider}" lands on `/settings`. The notes screen keeps no account section.

  The sentence "A company account is deleted by the company's admins." is withdrawn. Changes the sample sections of the spec, criterion 28 and criterion 29, and the tests that moved.
- **B6 - `DELETE /auth/account` and a ticket.** `DELETE /auth/account` still judges the space first: a personal space plus a company is `403 company_member`, companies only is
  `403 forbidden`, before the body and the ticket are read. A provider ticket sent in either case is not used up (a later confirmation with it works). Changes the order the probe
  recommended ("ticket first"): the code of 0016 is kept.
- **B7 - `DELETE /auth/org` with a ticket after 0015.** The ticket step runs inside the deletion's transaction, after the company lock and before the name check, as before.
  The deletion keeps spec 0015's rule: only the accounts that have no other space are deleted with the company; a member with another space keeps the account and loses the membership.
  The caller's account is deleted when the company was its last space. The sentence of the OpenAPI description "the account of every member, the caller's included" is withdrawn; main's
  sentence ("every member who has no other space (the caller's included when this was their last). Members with another space keep their ...") stands and gains the ticket sentence.
  A wrong name does not use the ticket up (the transaction is rolled back before any change). Changes the `DELETE /auth/org` contract and criterion 21.
- **B8 - the names of 0015 to 0017 this spec uses.** `MembershipReader.ReadInCompanyAsync(userId, companyId)` for a settings endpoint, `ReadSignInSpaceAsync` for sign-in only;
  the text `texts.orgs.companyMember(names)` of the sample; `RatePolicies.Classify` (the static class; `RatePolicy` is the enum); `company_member`, `company_limit_reached`,
  `org_creation_disabled`, `permissions_changed` already exist in the sample's failures.
- **B9 - the counts after the merge.** Audit kinds 33 (29 on main plus 4), OpenAPI operations 43 (39 plus 4), operations under the charset guard 30 (26 plus 4). Every place that pins a count
  is measured when it is changed, and the number printed by the count test is the one written. Changes the counts of "Audit events", "OpenAPI" and criteria 26 and 27.
- **B10 - the browser spec.** The Playwright spec is `e2e/14-settings.spec.ts` (main has `12-orgs.spec.ts` and `13-create-org.spec.ts`); wherever this spec says `12-settings.spec.ts` it reads
  `14-settings.spec.ts`. `13-create-org.spec.ts` gains the move of its "Delete account" step to `/settings`, and the notes of the earlier specs `10-private` and `11-providers` move as listed.
  `scripts/e2e-settings.sh` expects `"org_creation":"self-service"` in the options and gains a step for B3.
- **B11 - the notice mails.** The notice mails keep the limit key `MailKind.NoticeLimit` (100); no mail kind of 0017 equals it, and the 0017 join mail does not count against it.
  Criterion 31 (the count of mails from the notice row) stands (A13).
- **B12 - the threat model numbers.** The residual risks of this spec are numbered 71, 72 and 73 (0015 to 0017 end at 70), and wherever this spec or the plan says "risk 62", "risk 63" or "risk 64" it
  means 71, 72 and 73. The widened items (2, 51, 52, 53, 55 to 59) keep their numbers.
- **B13 - the changelog.** The entry goes under a new `## Unreleased` heading above `## 0.3.0`; v0.3.0 has none.
- **B14 - risks accepted from the probe.** (a) Connect takes the membership row, then the account row lock (F3, owner decision); a concurrent account deletion gets a clean 4xx, never a 500.
  Guard: `ProviderConnectTests.A_connect_asked_while_the_account_is_being_deleted_answers_a_clean_4xx`. (b) A settings call asked while a switch is in flight is sent late, not refused.
  (c) The company deletion warning on `/team` is decided: see B15.
- **B15 - the company deletion warning (owner).** The warning on `/team` reads: "This deletes the company and all its notes. A member who has no other space loses the account too. It cannot be undone."
  Visible: an owner sees which members lose the account. Changes the sample text only (no server contract). Guard: `pages/delete-company.spec.ts` test `the_warning_names_the_members_who_lose_the_account`.

## Verification notes (for the local verifiers)

- Criteria 3 to 7, 11 to 21 need the fake provider in process; GitHub's hosts are answered by the fake behind
  OpenIddict's HTTP client and the reader's own (Amendment A4), as Google's and Microsoft's are in spec 0011. Criteria 8 and 9 need a controlled clock;
  criteria 13, 17 and 21 parallel requests against PostgreSQL; criterion 22 the dispatcher run by hand (the
  `MailTestBase` pattern); criterion 27 a host with the manifest, settings and sessions of spec 0011.
- Criteria 30 and 31 need the stack with `tests/Auth.FakeOidc/compose.yml`, which sets the fake's GitHub keys and
  `Auth:Testing:WindowEndpoint`.
- No step uses a real GitHub, Google or Microsoft key.

## To verify during implementation

Each item has the answer to take if the check confirms it, and what to do if it does not. The probe of this slice
answered every item before the plan was written; the Amendment above records the answers (A3 to A8).

- **The GitHub reader in OpenIddict 7.7.1's client pipeline.** A handler of `ProcessAuthenticationContext` after the
  token response has the backchannel access token and can call GitHub through OpenIddict's HTTP client, so the tests
  reach the fake. If the token is not available to a handler there, stop and report.
- **PKCE at GitHub.** Whether OpenIddict sends a `code_challenge` to GitHub in 7.7.1 and GitHub accepts it. If both,
  criterion 2 asserts `code_challenge_method=S256`; if not, it asserts its absence and the threat model says the round
  trip relies on the state and the client secret.
- **`prompt=select_account` at GitHub.** Whether GitHub honours it for OAuth Apps. If it does not, the parameter is
  harmless and stays; the keys guide says what the person sees.
- **The test issuer for GitHub.** OpenIddict's client registered as a plain OAuth 2.0 client with static endpoints (no
  discovery) under `{TestIssuer}/github`. If 7.7.1 needs a discovery document even then, the fake serves a minimal one
  for GitHub in the stack only.
- **The session's stored values.** Start and window as properties of the OpenIddict authorization or in a table keyed
  by its id, removed with it; either is the contract. A refresh keeps the authorization (spec 0002, Decision 11); if a
  case is found where it does not, the refresh copies both values in its transaction.
- **A token-endpoint request that needs the access token.** The unlink and the password change authenticate the
  `Authorization: Bearer` token with OpenIddict's validation scheme inside their pass-through endpoint, as the company
  API does; OpenIddict's token endpoint does not read it. If the validation handler refuses to run on a token-endpoint
  path, the endpoint validates the token with OpenIddict's validation service directly; either is the contract.
- **The HTTP client of the GitHub reader.** OpenIddict's `SystemNetHttp` integration exposes the client it uses per
  registration; the reader takes that one, so the tests' handler that sends GitHub's hosts to the fake applies. If 7.7.1
  does not expose it, the reader uses a named `HttpClient` registered beside it with the same test handler.
- **The EF migration**: the session values (above) and the nullable provider column of `MailRequests`; the `connect`
  purpose needs no schema change (`smallint`); model and migration in one task.
