# Spec 0011 — Google and Microsoft: sign in, link and sign up with a provider

- **Status:** Draft, for the owner's review
- **Date:** 2026-04-23
- **Author:** Alex
- **Milestone:** Phase 2, slice 0011 — *"Continue with Google" works end to end against a test provider*
  ([`docs/design.md`](../../design.md), "Phase 2: sign-up and social login").
- **Context:** specs [0003](0003-lockout-and-abuse-resistance.md) (lockout), [0004](0004-email-flows.md) (link tokens,
  password reset), [0005](0005-tenancy-and-rbac.md) (companies, invitations, the manifest, `GET /auth/me`),
  [0008](0008-hardening-and-release.md) (per-IP limits, audit log, threat model), [0009](0009-self-service-signup.md)
  (sign-up, the completion that signs in) and [0010](0010-private-accounts.md) (private accounts, `DELETE /auth/account`,
  accounts never left without a space). This spec changes parts of 0005, 0008, 0009 and 0010; each change is named where
  it is made.

## Goal

A person signs in with their Google or Microsoft account instead of a password. The product shows "Continue with
Google" and "Continue with Microsoft" first, on the sign-in, sign-up and invitation screens. The person signs in at the
provider and comes back to the product's own screen. A provider account that is already linked signs them in. A
provider account whose address matches an existing account asks for that account's password once, links, and from then
on the provider alone signs in. A new person finishes creating their account (company or private, when sign-up is on)
and is signed in at once. An invitation can be accepted with a provider whose address is the invited one.

A person who has no password confirms the deletion of their private account by signing in with the provider again, and
can set a password through "forgot password".

Providers are off by default. A product lists them in its manifest and the operator gives the keys in the environment.
An instance that does neither sees these changes only: `GET /auth/options` gains `"providers":[]`, `GET /auth/me` gains
`has_password` and `linked_providers`, and `DELETE /auth/account` accepts a second kind of confirmation it never gets.

Concretely, this is met when this sequence works against a running instance with the fake provider, both providers
listed and private sign-up on:

```
curl -i .../auth/options          # -> 200 {"signup":"self-service","private_signup":"self-service","providers":["google","microsoft"]}

# browser: the button opens GET /auth/providers/google/start
#   -> 302 to the provider (code flow, PKCE, state, nonce) -> the person signs in there
#   -> GET /auth/providers/google/callback?code=..&state=..
#   -> 302 <provider return screen>#ticket=<t>&purpose=sign_in, and the cookie auth_pt
curl -i -X POST .../auth/providers/exchange -b 'auth_pt=<b>' -d '{"ticket":"<t>"}'
                                  # a new address -> 200 {"next":"finish_signup","email":"ola@gmail.test","kinds":["organization","private"]}
curl -i -X POST .../auth/providers/finish -b 'auth_pt=<b>' -d '{"ticket":"<t>","kind":"private"}'
                                  # -> 200 {"status":"authenticated","access_token"}, the refresh cookie; the account has no password

# the next time: the same button -> exchange -> 200 {"status":"authenticated","access_token"} and the refresh cookie

# an address that already has an account with a password:
#   exchange -> 200 {"next":"link","email":"anna@firma.test","email_verified":true}
curl -i -X POST .../auth/providers/link -b 'auth_pt=<b>' -d '{"ticket":"<t2>","password":"<p>"}'   # -> 200, signed in and linked

# a private person without a password deletes the account:
#   GET /auth/providers/google/start?purpose=reauth -> ... -> <provider return screen>#ticket=<r>&purpose=reauth
curl -i -X DELETE .../auth/account -H 'Authorization: Bearer <a>' -b 'auth_pt=<b>' -d '{"provider_ticket":"<r>"}'   # -> 204
```

## In scope

- The manifest key `providers`; the provider settings and the return address in configuration; the health check.
- The round trip: `GET /auth/providers/{name}/start` and `GET /auth/providers/{name}/callback`, with OpenIddict's
  client and its web providers for Google and Microsoft (personal and work accounts of any tenant).
- Provider tickets: random, single use, stored as a hash, 10 minutes (5 for a re-authentication), bound to the browser.
- `POST /auth/providers/exchange`, `POST /auth/providers/link`, `POST /auth/providers/finish`.
- Sign-in, linking with the password, sign-up (organization or private) and invitation acceptance through a provider.
- `DELETE /auth/account` with a provider confirmation for an account without a password.
- `GET /auth/options` gains `providers`; `GET /auth/me` gains `has_password` and `linked_providers`.
- "Forgot password" for an account without a password.
- Per-IP limits, audit events, OpenAPI.
- A fake OpenID provider for the tests and the end-to-end stack; integration, unit, Angular and Playwright tests.
- `samples/notes-web`: provider buttons first on the sign-in, sign-up and invitation screens; the return screen; the
  link screen; the finish screen; the provider confirmation of "Delete account".
- `scripts/e2e-providers.sh`.
- Documents: the guide to getting Google and Microsoft keys, the runbook for the Microsoft secret, the threat model, the
  Angular and Python guides, the deployment guide, `docs/design.md`, the changelog, the environment examples.

## Out of scope

- GitHub, the "linked logins" screen (listing, linking and unlinking from a signed-in session), and the full settings of
  an account without a password: slice 0012. Linking a second provider to an account without a password needs a
  password first (Settled 14).
- A Google-style account switcher: a later separate item.
- Real Google and Microsoft keys and a live test with them: an open item for the owner after this slice (Decision 16).
- Any provider other than Google and Microsoft; Apple; a hosted login page.
- Acting on the person's behalf at the provider: Auth-Core asks for no API scope and keeps no provider token.
- Following a change of address at the provider: the account keeps the address it was made with.
- Limiting Microsoft sign-in to one tenant, or Google sign-in to one Workspace domain.
- A mail that tells the person a provider was linked (a later idea).
- A product that turns password sign-in off.

## Contract

### General rules

- Every rule of specs 0004, 0005, 0008, 0009 and 0010 holds unless this spec changes it: JSON bodies of at most 8 KiB
  in UTF-8, unknown members ignored, `400 invalid_request` for a body that is not a JSON object or a member of the wrong
  type; `Cache-Control: no-store`; the security headers; the `415` of a body in another charset and the per-IP `429`
  come first.
- **Provider names** are `google` and `microsoft`, lower case, in paths, in the manifest, in answers and in audit rows.
- **A provider is offered** when the active manifest lists it, its client id and its client secret are set (not
  blank), and the return address (`Auth:App:FrontendUrls:ProviderReturn`) is set. A provider that is not offered has no
  button: `GET /auth/options` leaves it out and its `start` answers `404`.
- **No answer of the round trip names a value of a secret**, a code, a state, an identity token or a ticket in a log or
  an audit row.

### Manifest (changes spec 0005)

A new optional top-level key:

```yaml
providers: [google, microsoft]
```

- A list of provider names. Each must be `google` or `microsoft`; any other value (`github`, `password` included) makes
  the manifest invalid with the rules of spec 0005: the last valid manifest stays active and `/auth/health` reports
  `Degraded`. Duplicates are folded; the order is kept (it is the order of the buttons).
- `providers:` with no value, an empty list, or no key means no provider. A manifest stored by an earlier version has
  no key and reads as no provider.

### Configuration

| Key | Environment variable (`deploy/docker-compose.prod.yml`) | Rule |
| --- | --- | --- |
| `Auth:App:FrontendUrls:ProviderReturn` | `AUTH_FRONTEND_PROVIDER_URL` (optional, may be empty) | The product's return screen. When set, the rules of `CompleteSignup` (spec 0009): absolute, `https` outside Development, no fragment, no user information, no `token` or `ticket` parameter; a bad value stops the start, naming the key |
| `Auth:Providers:Google:ClientId`, `Auth:Providers:Google:ClientSecret` | `AUTH_GOOGLE_CLIENT_ID`, `AUTH_GOOGLE_CLIENT_SECRET` (optional) | Both set, or the provider is not configured |
| `Auth:Providers:Microsoft:ClientId`, `Auth:Providers:Microsoft:ClientSecret` | `AUTH_MICROSOFT_CLIENT_ID`, `AUTH_MICROSOFT_CLIENT_SECRET` (optional) | Both set, or the provider is not configured |
| `Auth:Providers:Microsoft:ClientSecretExpiresOn` | `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES` (optional) | A date `yyyy-mm-dd`: the day the secret lapses at Microsoft. A value that is not such a date stops the start, naming the key |
| `Auth:Providers:TestIssuer` | none (development stacks only) | Development only: the fake provider's address (below). Set outside Development, it stops the start, naming the key |

- **The callback address** that the operator registers at the provider is the origin of the return address followed by
  `/auth/providers/{name}/callback`, for example `https://app.example.com/auth/providers/google/callback`. It is built
  from configuration only, never from the request (`Host`, `X-Forwarded-Host`).
- **Missing keys never stop the start.** A provider listed by the manifest that is not offered (a key or the return
  address missing) makes the start log one warning naming the missing keys, never their values, and `/auth/health`
  reports `Degraded` while it lasts. A provider that is configured and not listed is not offered, and that is healthy.
- **The Microsoft secret's date.** From 30 days before `ClientSecretExpiresOn`, and after it, `/auth/health` reports
  `Degraded` and the start logs a warning naming the key and the date. Without the setting nothing is checked.
- **The test issuer.** With `Auth:Providers:TestIssuer` set (Development only), both providers are registered as plain
  OpenID Connect clients of `{TestIssuer}/google` and `{TestIssuer}/microsoft` instead of OpenIddict's web providers.
  Everything else (the claims read, the rules below, the endpoints) is the same code.

### `GET /auth/options` (changes specs 0009 and 0010)

`200 {"signup", "private_signup", "providers"}`. `providers` lists the offered providers in the manifest's order, `[]`
when none. A client ignores members it does not know.

### `GET /auth/providers/{name}/start`

No authentication. The browser navigates here (the product's button is a link, not a call).

1. `{name}` is not a provider name, or the provider is not offered: `404 {"error":"not_found"}`.
2. The query says what the round trip is for:
   - nothing: `sign_in` (sign-in, linking and sign-up; the exchange decides which);
   - `invite=<the invitation's link token>`: `invite`. The token must be 43 base64url characters;
   - `purpose=reauth`: `reauth`, the confirmation of a self-deletion.

   A malformed `invite`, another `purpose`, both `invite` and `purpose`, or a parameter given twice: `400
   invalid_request`. Other parameters are ignored. The start reads no account and no invitation.
3. Otherwise `302` to the provider's authorization endpoint: the authorization code flow with PKCE (`S256`), a `state`
   and a `nonce`, the scopes `openid email profile`, the callback address above, and `prompt=select_account` (for
   `reauth`: Microsoft `prompt=login`, Google `max_age=0`, so that the provider asks for a fresh sign-in). OpenIddict
   keeps the state (purpose, provider, and for `invite` the SHA-256 hash of the invitation token, never the token)
   and sets its request-forgery cookie, which binds the state to this browser. The state lives 10 minutes.

### `GET /auth/providers/{name}/callback`

The provider sends the browser here. GET only (the response mode is `query`). OpenIddict checks the state (its
signature, its age, its single use, the request-forgery cookie of the browser that started), redeems the code with the
PKCE verifier, and validates the identity token (signature against the provider's keys, issuer, audience, nonce,
expiry). Then:

- **Success:** the identity is read (below); a ticket is made (below); the answer is `302` to
  `<ProviderReturn>#ticket=<43 characters>&purpose=<sign_in|invite|reauth>` and sets the cookie `auth_pt` (below).
- **Failure:** `302` to `<ProviderReturn>#error=<code>&purpose=<purpose>` (`purpose` left out when the state could
  not be read), and nothing is stored:

| `error` | When |
| --- | --- |
| `cancelled` | the provider answered `access_denied` (the person cancelled or refused) |
| `invalid_state` | no state, a state that is forged, expired or already used, or the request-forgery cookie is missing or another browser's |
| `provider_unavailable` | the provider is no longer offered (the manifest changed during the round trip) |
| `provider_error` | any other error: another error from the provider, the code could not be redeemed, the identity token is not valid, or a claim the identity needs is missing |

- Auth-Core never shows a page of its own and never passes on the provider's `error_description`. The answer carries
  `Cache-Control: no-store`. One log line at the level Information names the provider and the code.
- The ticket and the error travel in the fragment: a browser never sends it to a server, and `Referrer-Policy:
  no-referrer` already holds.

### The provider identity and its address

From the claims of the identity token the callback takes three things: the **identity key**, the **address** and
whether the address is **verified**.

| | Identity key (stored as `AspNetUserLogins.ProviderKey`) | Address | Verified when |
| --- | --- | --- | --- |
| Google | `sub` | `email` | `email_verified` is `true` |
| Microsoft | `{tid}:{oid}`, both GUIDs in lower case | `email` (never `preferred_username`, `upn` or `unique_name`) | `tid` is the personal-accounts tenant `9188040d-6c67-4c5b-b112-36a304b66dad` and `email` is present, **or** `xms_edov` is `true` |

- **The identity key is the person.** An identity whose key is a login of an account signs in to that account, whatever
  its address says now. The address is never used to find a linked account.
- **A Microsoft work address is unverified unless the tenant proves the domain.** A work tenant can give a person any
  `email` it likes (the "nOAuth" issue). `xms_edov` ("email domain owner verified") is an optional claim the product's
  app registration must ask for; without it every work address reads as unverified, which is safe.
- **The address must pass the invitation rule** (`EmailInput.IsInvitable`, spec 0005). An address that does not is
  treated as unverified. An identity with no `email` claim has **no address**.
- An unverified address never links without the password and never skips the mail of sign-up (Decision 12).

### Provider tickets

- A ticket is 32 random bytes as 43 base64url characters. Only its SHA-256 hash is stored, in a table of its own,
  `ProviderTickets`: the hash, the hash of the browser binding (below), the purpose (`smallint`: `1` sign_in, `2`
  invite, `3` reauth), the provider, the identity key, the address as the provider gave it (or none), whether it is
  verified, for `invite` the hash of the invitation token, and the time it expires.
- It expires 10 minutes after the callback; a `reauth` ticket after 5 minutes. The hourly clean-up of spec 0004 deletes
  expired tickets.
- It is used up by the request that signs in or deletes (one `DELETE ... RETURNING`, in that request's transaction): of
  parallel requests with one ticket, one gets it. A request that refuses changes nothing, and the ticket stays usable
  until it expires (so that a person can retype a wrong password on the link screen).
- An exchange that answers `next` does not use the ticket up: the link or the finish uses it.
- A ticket is not tied to an account. An account deletion leaves the tickets alone; they expire.

### The browser binding: the cookie `auth_pt`

The callback that makes a ticket also sets `auth_pt`: 32 random bytes, `HttpOnly; Secure; SameSite=Strict;
Path=/auth; Max-Age=600`. The ticket stores the SHA-256 hash of its value. Every endpoint that takes a ticket
(exchange, link, finish, and `DELETE /auth/account` with `provider_ticket`) needs the cookie, and a cookie that is
missing or does not match the ticket is the answer for an unknown ticket. A request that uses the ticket up clears the
cookie.

This stops a ticket from being pushed into someone else's browser (a link to `.../provider-return#ticket=<the
attacker's ticket>` would sign the victim in to the attacker's account). A browser holds one `auth_pt`, so a second
round trip in the same browser makes the ticket of the first one unusable (Settled 9).

### `POST /auth/providers/exchange` — `{"ticket"}`

A token-endpoint request of OpenIddict in pass-through mode with a placeholder request, as `POST /auth/signup/complete`
is (spec 0009, Amendment 2). The checks, in this order:

1. A body that is not a JSON object, or a `ticket` that is missing or not a string: `400 invalid_request`.
2. A ticket that is unknown, used, expired, of the purpose `reauth`, or without its `auth_pt`: `400
   {"error":"invalid_ticket"}`.
3. **Purpose `sign_in`:**
   1. **The identity is linked** to an account: the person is signed in to that account. An account with no membership
      is `403 no_membership`, as at login. Otherwise, in one transaction, the ticket is used up and the session is
      issued with `login.succeeded` (`details.provider`): `200 {"status":"authenticated","access_token"}` and the
      refresh cookie, as `POST /auth/login` answers. A lock on the address's login streak does not stop it (the lockout
      guards passwords). An account deleted between the read and the sign-in: `400 invalid_ticket`.
   2. **No address:** `403 {"error":"no_email"}`.
   3. **The address is unverified:** `200 {"next":"link","email","email_verified":false}`, whether or not an account
      has the address (so that the answer says nothing about accounts to someone who did not prove the mailbox).
   4. **An account has the address**, spelled the same up to ASCII case (`EmailInput.IsSameAddress`): `200
      {"next":"link","email","email_verified":true}`.
   5. **An account has an address that only resembles it** (the normaliser folds the two together): `409
      {"error":"already_registered"}`.
   6. **No account, and at least one sign-up switch on** (specs 0009 and 0010): `200
      {"next":"finish_signup","email","kinds"}`, `kinds` listing the kinds whose switch is on, `organization` first.
   7. **No account, both switches off:** `403 {"error":"no_account"}`.
4. **Purpose `invite`** (spec 0010's acceptance, with the provider in place of the password):
   1. The invitation (found by the hash the ticket holds) is unknown, used, expired, cancelled, replaced or a
      look-alike: `400 invalid_token`, as at acceptance.
   2. The address is missing, unverified, or not the invited address (`EmailInput.IsSameAddress`): `403
      {"error":"address_mismatch"}`. The invitation stays usable.
   3. The address belongs to a company member: `409 already_member`. It has a private account: `409
      private_account_exists` (the move of spec 0010 is the screen's own button, below). The identity is already a
      login of an account: `409 {"error":"provider_already_linked"}`. In all three the invitation stays usable.
   4. Otherwise, in one transaction, under the company lock and then the address lock (spec 0010, "Locks"): the
      invitation is used up; the account is made with the invited address, **no password**, its email confirmed; the
      identity becomes its login; it becomes a member with the invited role; `invite.accepted` is written with
      `details.role` and `details.provider`; the ticket is used up; and the person is signed in (`login.succeeded`
      with `details.provider`): `200 {"status":"authenticated","access_token"}` and the refresh cookie.

The answers with `next` are plain JSON; only the sign-ins go through OpenIddict's token response.

### `POST /auth/providers/link` — `{"ticket", "password"}`

A token-endpoint request like the exchange. The checks, in this order:

1. A body that is not a JSON object; a `ticket` that is missing or not a string; a `password` that is missing, not a
   string, blank or holds NUL: `400 invalid_request`.
2. A ticket that is unknown, used, expired, not of the purpose `sign_in`, or without its `auth_pt`: `400
   invalid_ticket`.
3. The identity is a login of an account by now (another tab linked it): the person is signed in to that account as
   the exchange signs in (step 3.1), and the password is not judged.
4. The ticket has no address: `403 no_email`.
5. The attempt is counted in the login streak of the ticket's address (spec 0003), before anything is looked up: while
   the identifier is locked, `429 too_many_attempts` with `retry_after_seconds`, and `login.locked`
   (`details.provider`).
6. No account with the address (spelled the same up to ASCII case), or an account without a password: the decoy hash is
   evaluated and the answer is `401 {"error":"invalid_credentials"}`, exactly as for a wrong password; `login.failed`
   (`unknown_address`, `details.provider`). A wrong password: the same `401`; `login.failed` (`wrong_password`,
   `details.provider`).
7. The password is right: the streak is cleared. Then:
   - the account's email is not confirmed: if the ticket's address is verified, it is confirmed now; otherwise `403
     email_not_verified`, as at login;
   - the account has no membership: `403 no_membership`.
8. Otherwise, in one transaction under the address lock: the identity becomes a login of the account; `provider.linked`
   is written; the ticket is used up; and the person is signed in (`login.succeeded` with `details.provider`): `200
   {"status":"authenticated","access_token"}` and the refresh cookie. From then on the exchange signs this identity in
   without a password.

### `POST /auth/providers/finish` — `{"ticket", "kind"?, "company_name"?}`

A token-endpoint request like the exchange. It mirrors `POST /auth/signup/complete` (spec 0010) with the ticket in
place of the mail link and no password. The checks, in this order:

1. Both sign-up switches off: `403 signup_disabled` before the body is read.
2. A body that is not a JSON object; a `ticket` that is missing or not a string; a `kind` that is present and not
   `organization` or `private`; a `company_name` that is present and breaks the name rule: `400 invalid_request`.
   `kind` absent means `organization`, as at `POST /auth/signup`.
3. A ticket that is unknown, used, expired, not of the purpose `sign_in`, or without its `auth_pt`: `400
   invalid_ticket`.
4. The ticket has no address, or an unverified one: `403 email_not_verified`. The exchange never offers the finish for
   such a ticket; the person signs up through the mail of spec 0009 instead.
5. The switch of the kind is off: `403 signup_disabled`.
6. **Organization:** no `company_name`: `400 invalid_request`. **Private:** a `company_name`: `400 invalid_request`.
   The ticket stays usable.
7. Any account under the address (any kind, also one that only resembles it), or the identity is a login of an account
   by now: `409 already_registered`.
8. Otherwise, in one transaction under the address lock: the account is made with the provider's address, **no
   password**, its email confirmed (the provider verified it, Decision 8); the identity becomes its login; the space is
   made as at a sign-up completion (an organization: the company with a copy of the default roles, the first admin's
   role, `org.created` with `details.via` `signup`; a private person: the personal space); the pending sign-up of the
   address, if any, is deleted; `signup.completed` is written with `details.kind` and `details.provider`; the ticket is
   used up; and the person is signed in (`login.succeeded` with `details.provider`): `200
   {"status":"authenticated","access_token"}` and the refresh cookie.

Of a finish, a sign-up completion and an invitation acceptance running at once for one address, at most one makes the
account: they take the same address lock.

### `DELETE /auth/account` — `{"password"}` or `{"provider_ticket"}` (changes spec 0010)

The checks of spec 0010 stay, in its order, with these changes:

- **The body** holds exactly one of `password` (a string, not blank, no NUL) and `provider_ticket` (a string). Both,
  neither, or a member of the wrong type: `400 invalid_request`.
- **Under the address lock**, after the membership is read again and before anything is counted, the confirmation must
  fit the account. An account **with** a password and a body with `provider_ticket`: `403
  {"error":"password_required"}`. An account **without** a password and a body with `password`: `403
  {"error":"reauth_required"}`. Nothing is counted and no row is written.
- **The password** is judged as spec 0010 says.
- **The provider ticket** must be a ticket of the purpose `reauth`, unused, not expired (5 minutes),
  with its `auth_pt`, whose identity is a login of **this** account. Anything else: `403 {"error":"reauth_failed"}`,
  and `account.delete_refused` with `details.reason` `reauth_failed`. The lockout is not consulted (a ticket cannot be
  guessed). The ticket is used up by the deletion.
- **The deletion** is that of spec 0010 (`details.reason` `self`). For a ticket, `provider.reauthenticated` is written
  in the same transaction. The answer clears the refresh cookie and `auth_pt`.

| Answer | When (new rows only; the table of spec 0010 holds) |
| --- | --- |
| `400 invalid_request` | both `password` and `provider_ticket`, or neither, or one of the wrong type |
| `403 password_required` | the account has a password and the body holds `provider_ticket` |
| `403 reauth_required` | the account has no password and the body holds `password` |
| `403 reauth_failed` | the ticket is unknown, used, expired, of another purpose, without its `auth_pt`, or of an identity that is not a login of this account |

### `GET /auth/me` (changes specs 0005 and 0010)

Adds two members:

```json
{ "sub": "…", "email": "…", "org_id": "…", "org_name": "…", "org_kind": "personal", "roles": ["private"],
  "permissions": ["…"], "has_password": false, "linked_providers": ["google"] }
```

- `has_password` is `true` when the account has a password hash.
- `linked_providers` lists the providers of the account's logins, sorted ordinally, `[]` when none.

The access token gains no claim: a provider sign-in issues exactly the token a password sign-in issues.

### Accounts without a password

- An account made by the finish or by an invitation through a provider has no password. A password login with its
  address is the `401 invalid_credentials` of an unknown address (spec 0001, already so in code).
- **"Forgot password" sets a password** (Decision 15). `POST /auth/password/forgot` already queues the reset mail for
  any account with the address, and `POST /auth/password/reset` adds a password when there is none. The reset keeps its
  effects of spec 0004 (every session ends, the streak is cleared). The account then has a password and its logins.
- The deletion routine of spec 0010 already removes an account's logins (`AspNetUserLogins`); nothing changes there.

### Mails

No new mail and no changed text. The reset mail, the invitation mail (with the paragraph of spec 0010 for a private
account) and the "you already have an account" mail read well for an account without a password: each points at "forgot
password" or at the product's sign-in screen, where the provider buttons are.

### Limits (changes spec 0008)

Per-IP: `GET /auth/providers/{name}/start`, `GET /auth/providers/{name}/callback`, `POST /auth/providers/exchange`,
`POST /auth/providers/link` and `POST /auth/providers/finish` count against the `login` policy (30 a minute by
default): each is a step of a sign-in. They are the only `GET` requests that do not count against `general`.
`DELETE /auth/account` stays on `general`; `GET /auth/options` and `GET /auth/me` too.

The lockout of spec 0003: a password typed at the link counts in the streak of the ticket's address, as a login does. A
provider sign-in neither counts nor clears it.

### Audit events (changes specs 0008, 0009 and 0010)

Two new kinds (28 in all):

| Kind | Written when | Fields |
| --- | --- | --- |
| `provider.linked` | `POST /auth/providers/link` links an identity, in its transaction | `actor_user_id`, `subject_user_id`, `subject_email`, `org_id`, `org_name`, `details.provider` |
| `provider.reauthenticated` | `DELETE /auth/account` accepts a provider ticket, in the deletion's transaction | `actor_user_id`, `subject_user_id`, `subject_email`, `org_id`, `org_name`, `details.provider` |

- `login.succeeded` gains `details.provider` for every sign-in through a provider (exchange, link, finish, invitation);
  a password sign-in has none. One kind for every sign-in keeps the runbook's queries of logins whole.
- `login.failed` and `login.locked` gain `details.provider` when they come from the link.
- `signup.completed` gains `details.provider` for a finish; `invite.accepted` gains `details.provider` for an acceptance
  through a provider.
- `account.delete_refused` gains the reason `reauth_failed`.
- A round trip that fails at the callback writes no row: no account is known there, and the log line says what
  happened. No row ever holds a code, a state, an identity token, a ticket or the cookie.

### OpenAPI

Added: `GET /auth/providers/{name}/start` (`302`, `400`, `404`), `GET /auth/providers/{name}/callback` (`302`; the
fragment's members described), `POST /auth/providers/exchange`, `/link` and `/finish` (each answer above, the refresh
cookie and `auth_pt`). Changed: `GET /auth/options`, `GET /auth/me`, `DELETE /auth/account`. The counts pinned by
`OpenApiHardeningTests` rise: 36 operations, 24 under the charset guard.

### Python package (`clients/python`)

No change. The package reads only the access token's claims (`_principal.py`, `_verify.py`); nothing in it reads
`GET /auth/options` or `GET /auth/me`, and a provider sign-in issues the same claims as a password sign-in.

### Sample frontend (`samples/notes-web`)

- **The buttons.** "Continue with Google" and "Continue with Microsoft", in the order of `GET /auth/options`, are plain
  links to `/auth/providers/{name}/start` (a full page navigation, not a call). Only offered providers get a button.
- **`/login`:** the buttons first, then "or", then the email and password form.
- **`/signup`:** the buttons first ("Sign up with Google", "Sign up with Microsoft"), then the form of spec 0010.
- **`/invite`:** after the preview, when `has_private_account` is `false`, the buttons first ("Accept with Google",
  "Accept with Microsoft"), linking to `start?invite=<token>`, then the password form. When it is `true`, the screen is
  that of spec 0010 (the warning and the move button), with no provider buttons.
- **`/provider-return`** (new): reads the fragment once and removes it from the address bar (a navigation that replaces
  the history entry). Then:
  - `#error=`: `cancelled` "Sign-in was cancelled."; `invalid_state` "The sign-in took too long or was started in
    another browser. Try again."; `provider_unavailable` "This way to sign in is not available."; `provider_error`
    "Google or Microsoft could not sign you in. Try again." Each with a link back to `/login`.
  - `purpose` `sign_in` or `invite`: the exchange. Signed in: the notes open (replacing this screen). `next` `link`:
    `/provider-link`; `next` `finish_signup`: `/provider-finish` (the ticket, the address and the kinds in the router's
    navigation state, never in a URL or in storage). `invalid_ticket` "This sign-in took too long. Start again.";
    `no_account` "You have no account. Ask your company for an invitation."; `no_email` "Your Microsoft or Google
    account has no email address we can use. Sign in with your password."; `already_registered` "An account with this
    address exists. Sign in with your password."; `address_mismatch` "Use the Google or Microsoft account of the invited
    address, or open the invitation link again and choose a password."; `already_member`, `private_account_exists`,
    `provider_already_linked` and `invalid_token` one sentence each.
  - `purpose` `reauth`: the notes screen, with the ticket in the navigation state; it opens "Delete account" ready to
    confirm.
  - No fragment: `/login`.
- **`/provider-link`** (new): "An account with {email} exists. Enter its password once to connect {Google}. After
  that {Google} alone signs you in." When `email_verified` is `false`: "If you have an account with {email}, enter its
  password once to connect {Microsoft}. If you do not, create an account with your email address." with a link to
  `/signup`. The password, "Connect and sign in", and "Forgot your password?". `invalid_credentials` "Wrong password.
  No password yet? Use 'Forgot your password?' to set one."; `429` shows the wait; `invalid_ticket` as above.
- **`/provider-finish`** (new): "Finish creating your account". The address (read-only); "Organization / Private
  person" only when `kinds` holds both; the company name for an organization; "Create account". Success opens the notes.
  `already_registered`, `signup_disabled` and `invalid_ticket` one sentence each.
- **"Delete account"** on the notes screen: for `has_password` `false` there is no password field; the warning and one
  button per linked provider, "Confirm with Google". It starts the round trip with `purpose=reauth`. Back with the
  ticket, the section shows the warning and "Delete my account"; the sample calls `DELETE /api/me` and, after its `204`,
  `DELETE /auth/account` with `provider_ticket`. `reauth_failed` "The confirmation did not work or took too long, so
  your account was not deleted. Your notes are already gone. Confirm again."; `password_required` and `reauth_required`
  redraw the section from a fresh `GET /auth/me`. An account with a password is unchanged (spec 0010).
- The calls are in `account-api.ts` and `auth.service.ts`; the words in `texts.ts`. The interceptor's token paths do
  not change: the three provider calls need no access token. Texts are English, as in spec 0007.

### Sample backend (`samples/notes-api`)

No code change. Its `auth.yaml` gains `providers: [google, microsoft]`; the stacks that use it set the fake provider's
keys, the test issuer and the return address (Settled 22), so that health stays `Healthy`.

### Documents

- **`docs/deployment/provider-keys.md`** (new): getting the keys, step by step. Google Cloud Console: a project, the
  OAuth consent screen (external, the app name, the support address, the scopes `openid`, `email`, `profile`), an OAuth
  client of the type "Web application" with the callback address as its authorized redirect URI, and the client id and
  secret into `.env`. Microsoft Entra admin center: an app registration with "Accounts in any organizational directory
  and personal Microsoft accounts", the platform "Web" with the callback address as its redirect URI, a client secret
  (its lapse date noted, into `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES`), the optional claims `email` and `xms_edov` on the
  ID token, and the client id (the "Application (client) ID") and secret into `.env`. For each: how to check it with
  the sample, and what the operator sees when a value is wrong.
- **`docs/operations/microsoft-secret.md`** (new runbook): a Microsoft client secret lapses after at most 24 months.
  How to notice (the date in `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES` makes health `Degraded` 30 days before; after the
  lapse every Microsoft callback ends in `#error=provider_error` and the log names `invalid_client`); how to rotate
  (make a second secret in Entra, put it in `.env` with its date, restart, check a sign-in, delete the old secret); what
  people without a password do meanwhile ("forgot password").
- **`docs/security/threat-model.md`**: a section "Sign-in with Google and Microsoft" (the round trip and its
  boundaries, the identity key, verified addresses, tickets and their binding, the return address from configuration,
  reauth) with STRIDE rows; the Google and Microsoft endpoints in the diagram; the residual risks below.
- **`docs/integration/angular.md`**: a section on the providers (the buttons are links; the return screen reads and
  removes the fragment; the exchange and its answers; the link and finish screens; the ticket only in memory; the
  provider confirmation of "Delete account"; `has_password` and `linked_providers`); step 4 adds
  `/auth/providers/*/start` and `/auth/providers/*/callback` to the paths whose access logs are kept off or scrubbed.
- **`docs/integration/python-fastapi.md`**: one sentence: a provider sign-in issues the same token; nothing changes.
- **`docs/deployment/vps.md`**: the provider variables, the callback address to register, the access-log rule, a link
  to the keys guide and the runbook. **`deploy/docker-compose.prod.yml`** and **`deploy/.env.prod.example`**: the six
  variables, optional, with comments. **`deploy/auth.yaml`**: a commented `providers` line explaining the key.
- **`docs/design.md`**: the Phase 2 section (a paragraph for this spec; "forgot password sets a password" moves from
  slice 0012 to this one). **`CHANGELOG.md`** (Unreleased): providers, the endpoints, the cookie, the new members, the
  audit kinds.

**Residual risks** added to the threat model's list, after risk 51:

52. **A provider account is the product account.** Whoever takes over a linked Google or Microsoft account signs in to
    the product without its password. Whoever administers a Microsoft tenant or a Google Workspace domain controls the
    provider accounts of its people, and so their product accounts.
53. **A person without a password depends on the provider.** A provider outage, the product's keys revoked or lapsed,
    the provider no longer offered, or the provider account lost (a person who leaves a company loses its Microsoft
    account) locks them out until "forgot password" sets a password, which needs the mailbox.
54. **The Microsoft client secret lapses** after at most 24 months, and every Microsoft sign-in fails until it is
    rotated. The date setting and the runbook are the warning; nothing rotates it.
55. **Codes and invitation tokens in request lines.** The invitation token travels in the query of the start address,
    and the provider's code and state in that of the callback. A proxy or web server that logs query strings keeps
    them. The code is single use and needs the PKCE verifier; the invitation token works until it is used (7 days).
56. **A ticket is in the address bar** until the return screen removes it, readable by a browser extension or by script
    in the page. It is single use, bound to the browser's `auth_pt` cookie and lives 10 minutes (5 for `reauth`).
57. **Re-authentication is as strong as the provider makes it.** A person still signed in at the provider may confirm
    with one click; whoever holds a live access token in that browser can then delete a private account without a
    password. Auth-Core asks for a fresh sign-in (`prompt=login`, `max_age=0`) but does not check that one happened.
58. **The provider learns about sign-ins.** Google and Microsoft see when a person signs in to the product, from which
    address, and the product's name.

## Acceptance criteria (Done when)

Each criterion names the kind of test that guards it. "Integration" is a .NET test against PostgreSQL with the fake
provider in process; "unit" is a .NET test without a host.

Configuration and options

1. The manifest accepts `providers` with `google` and `microsoft`, folds duplicates and keeps the order; `github`,
   `password` or any other value makes it invalid (last valid manifest kept, `/auth/health` `Degraded`); a stored
   manifest without the key reads as no provider. Guard: unit (`ManifestProvidersTests`).
2. `GET /auth/options` lists exactly the offered providers in the manifest's order: a listed provider without its id,
   without its secret, or without the return address is left out; a configured provider that is not listed is left
   out; with no key `providers` is `[]`. Guard: integration (`ProviderOptionsTests`).
3. A listed provider that is not offered makes the start log one warning naming the missing keys and no value, and
   `/auth/health` report `Degraded`; the service starts. A bad return address stops the start naming the key; a bad
   secret date stops it naming the key; `Auth:Providers:TestIssuer` outside Development stops it naming the key.
   Guard: integration (`ProviderSettingsTests`).
4. With `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES` 31 days ahead health is `Healthy`; 30 days ahead, and after the date, it
   is `Degraded`. Guard: integration with a controlled clock (`ProviderSettingsTests`).

The round trip

5. `start` for an offered provider answers `302` to the provider's authorization endpoint with `response_type=code`,
   `code_challenge_method=S256`, a `state`, a `nonce`, the scopes `openid email profile` and the callback address built
   from the return address's origin (a request with another `Host` or `X-Forwarded-Host` gets the same one). `reauth`
   adds `prompt=login` (Microsoft) or `max_age=0` (Google). Guard: integration (`ProviderStartTests`).
6. `start` with an unknown name or a provider that is not offered is `404 not_found`; a malformed `invite`, another
   `purpose`, both, or a repeated parameter is `400 invalid_request`; the start reads no account and no invitation
   (statement capture). Guard: integration (`ProviderStartTests`).
7. A callback with a good code for each of the three fake people (Google; Microsoft personal; Microsoft work with an
   unverified address) answers `302` to the return address with `#ticket=` (43 characters) and the purpose, sets
   `auth_pt` with the attributes above, and stores the ticket's hash only (no column holds the ticket or the cookie in
   clear). Guard: integration (`ProviderCallbackTests`).
8. The callback answers `#error=cancelled` for `access_denied`; `#error=invalid_state` for a missing, forged, expired or
   replayed state and for a callback without the request-forgery cookie of the browser that started;
   `#error=provider_error` for a code the provider refuses, an identity token with a bad signature, another audience,
   another nonce, or a missing `sub` (Google) or `tid`/`oid` (Microsoft); `#error=provider_unavailable` after the
   provider was dropped from the manifest during the round trip. None stores a ticket or sets `auth_pt`, and no answer
   holds the provider's `error_description`. Guard: integration, one test per code (`ProviderCallbackErrorTests`).
9. The identity rules: a Google identity is keyed by `sub` and verified only with `email_verified` `true`; a Microsoft
   identity is keyed by `{tid}:{oid}` and never by its address; a personal Microsoft address is verified; a work address
   is verified only with `xms_edov` `true`; `preferred_username` and `upn` are never read as the address; an address
   that breaks the invitation rule is unverified. Guard: unit (`ProviderIdentityTests`) for each row of the table.

Exchange

10. A linked identity signs in: `200 {"status":"authenticated","access_token"}` and a refresh cookie that refreshes like
    a login's; the token's claims equal those of a password login of the same account; `login.succeeded` holds
    `details.provider`; the ticket is used up and `auth_pt` cleared. A locked login streak does not stop it and is not
    cleared by it. Guard: integration (`ProviderExchangeTests`).
11. A linked identity whose provider address has changed still signs in to its account; an unlinked identity whose
    address equals a linked account's address does not sign in (it gets `next` `link`). Guard: integration
    (`ProviderExchangeTests`).
12. For a verified address: an account with the address gives `next` `link` with `email_verified` `true`; an account
    under a look-alike spelling gives `409 already_registered`; no account with sign-up on gives `next`
    `finish_signup` with the kinds whose switch is on (each of the three combinations); no account with both off gives
    `403 no_account`. None uses the ticket up. Guard: integration (`ProviderExchangeTests`).
13. For an unverified address the exchange answers `next` `link` with `email_verified` `false`, with the same status,
    headers and body for an address with an account and one without; for no address it answers `403 no_email`.
    Guard: integration (`ProviderExchangeTests`).
14. A ticket that is unknown, used, expired (a controlled clock at 10 minutes), of the purpose `reauth`, sent without
    `auth_pt` or with another browser's `auth_pt` is `400 invalid_ticket`; of parallel exchanges with one ticket
    exactly one signs in. Guard: integration (`ProviderTicketTests`).

Link

15. A link with the right password answers as a login, adds the identity as a login of the account, writes
    `provider.linked` and `login.succeeded` (`details.provider`) in one transaction, and uses the ticket up; afterwards
    an exchange with a new ticket for the identity signs in without a password, and the password still signs in too.
    Guard: integration (`ProviderLinkTests`).
16. A wrong password and an address without an account (or with an account without a password) give the same `401
    invalid_credentials` with the same headers and body; each counts in the address's streak; after ten the link is
    `429 too_many_attempts` with `login.locked`; the ticket stays usable after a wrong password. Guard: integration
    (`ProviderLinkTests`, `ProviderLinkLockoutTests`).
17. A link for an account whose email is not confirmed confirms it when the ticket's address is verified and is `403
    email_not_verified` otherwise; a bad body is `400 invalid_request`; a ticket of another purpose is `400
    invalid_ticket`; an identity linked meanwhile signs in without judging the password. Guard: integration
    (`ProviderLinkTests`).

Finish

18. A private finish makes an account with the provider's address, no password hash, its email confirmed, the identity
    as its login and a personal space, deletes the pending sign-up of the address, writes `signup.completed`
    (`details.kind` `private`, `details.provider`) and `login.succeeded`, and signs in with `org_kind` `personal`. An
    organization finish makes the company with the default roles and the first admin's role and writes `org.created`
    (`via` `signup`). Guard: integration (`ProviderFinishTests`).
19. The finish answers `403 signup_disabled` before reading the body with both switches off and for a kind whose switch
    is off; `400 invalid_request` for a bad body, an organization without a company name and a private finish with one
    (the ticket usable afterwards); `403 email_not_verified` for an unverified or missing address; `409
    already_registered` for an address with any account, a look-alike included. Guard: integration
    (`ProviderFinishTests`).
20. A finish, a sign-up completion and an invitation acceptance running at once for one address make one account; two
    finishes with two tickets for one address make one account. Guard: integration with parallel requests
    (`ProviderFinishRaceTests`).

Invitations

21. An exchange of an `invite` ticket whose verified address is the invited one makes the account without a password,
    as a member with the invited role, uses the invitation and the ticket up, writes `invite.accepted`
    (`details.provider`) and `login.succeeded`, and signs in with `org_kind` `organization`. Guard: integration
    (`ProviderInvitationTests`).
22. An `invite` ticket answers `400 invalid_token` for an invitation that is used, expired, cancelled or replaced; `403
    address_mismatch` for another address, an unverified one or none; `409 already_member`, `409
    private_account_exists` and `409 provider_already_linked` in their cases. In every case the invitation still works
    afterwards (except the first). Guard: integration (`ProviderInvitationTests`).

Deletion, reset and `/auth/me`

23. A private account without a password deletes itself with a `reauth` ticket of a linked identity: `204`, the
    refresh cookie and `auth_pt` cleared, the account and its logins gone, `provider.reauthenticated` and
    `account.deleted` (`reason` `self`) written. Guard: integration (`AccountDeletionProviderTests`).
24. `DELETE /auth/account` answers `400 invalid_request` for both members or neither; `403 password_required` for an
    account with a password and a ticket; `403 reauth_required` for an account without a password and a password
    (neither counted, no row); `403 reauth_failed` with `account.delete_refused` (`reauth_failed`) for a ticket that is
    a `sign_in` ticket, expired (5 minutes), used, without its `auth_pt`, or of an identity linked to another account.
    The answers of spec 0010 for a password are unchanged. Guard: integration (`AccountDeletionProviderTests`; the
    tests of spec 0010 pass).
25. "Forgot password" for an account without a password sends the reset mail; the reset sets a password; afterwards a
    password login and the provider both sign in, and `GET /auth/me` says `has_password` `true`. Guard: integration with
    the dispatcher run by hand (`ProviderPasswordResetTests`).
26. `GET /auth/me` returns `has_password` and `linked_providers` (sorted) for an account with a password and no
    provider, one with both, and one with a provider only. A password login for an account without a password is the
    `401` of an unknown address. Guard: integration (`MeProvidersTests`).

Limits, audit and API

27. The five provider paths count against the `login` policy and the other `GET` paths still against `general`. Guard:
    unit (`RatePolicyTests`) and integration (`RateLimitMiddlewareTests`).
28. The audit log knows 28 kinds; each new row and each new `details` member holds the fields above; no row holds a
    code, a state, an identity token, a ticket, a cookie or a password (`AssertNoSecretsAsync`). Guard: integration
    (`AuditProviderFlowsTests`).
29. The OpenAPI document lists the five new operations and the changed bodies and answers; 36 operations, 24 under the
    charset guard; a provider POST in another charset is `415`. Guard: integration (`OpenApiHardeningTests`,
    `JsonCharsetTests`).
30. An instance with the manifest and settings of spec 0010 and no provider keys starts `Healthy`, offers `providers`
    `[]`, issues the same tokens, and passes its earlier tests, except those that pin what this spec changes (the
    members of `/auth/options` and `/auth/me`, the counts of audit kinds and OpenAPI operations), which are changed to
    this spec. Guard: integration (`UpgradeFromSpec0010Tests`) and the whole suite.

Samples and end to end

31. `samples/notes-web` unit tests cover: the buttons first on `/login`, `/signup` and `/invite` (none when `providers`
    is `[]`; none on `/invite` with `has_private_account`); `/provider-return` for each error code, each exchange answer
    and `reauth`, removing the fragment from the address bar and keeping the ticket out of every URL and storage;
    `/provider-link` (both texts, `invalid_credentials`, `429`); `/provider-finish` (the choice only with both kinds,
    the company name); "Delete account" without a password (the call order: `DELETE /api/me`, then `DELETE
    /auth/account` with `provider_ticket`; `reauth_failed`). Guard: Angular unit tests.
32. A Playwright spec (`samples/notes-web/e2e/11-providers.spec.ts`) passes in Chromium and WebKit against the fake
    provider: a new person signs up privately with "Google" and is signed in; signs out and in again with the button
    alone; a person with a password account links "Microsoft" with the password, then signs in with the button alone;
    a person with an unverified Microsoft work address sees the link screen with the sign-up sentence; an invited
    person accepts with "Google" and lands in the company; a person cancels at the provider and sees the message; a
    private person without a password deletes the account with "Confirm with Google". Guard: Playwright.
33. `scripts/e2e-providers.sh` passes against a fresh stack with the fake provider: options, start (`302`, the
    parameters), a full round trip by the fake's non-interactive mode, each exchange answer, link, finish, an
    invitation, reauth deletion, the `auth_pt` refusal, and the audit rows read from the database. Every earlier e2e
    script passes. Guard: e2e script.
34. The fake provider serves Google's and Microsoft's discovery, authorization, token, key-set endpoints, and issues
    identity tokens for the three people (and on demand for a missing claim, a wrong audience, a wrong nonce and a bad
    signature); the integration tests run OpenIddict's real web-provider registrations against it, the end-to-end stack
    through the test issuer. Guard: the tests above run against it.
35. The whole .NET suite passes in one run on Windows, and on Linux through `scripts/test-linux.sh` if that script is
    on `main` when this slice is implemented; the Python package's and the samples' suites pass unchanged except where
    this spec changes them. Guard: the suites.
36. The documents of "Documents" exist and say what this spec says; the keys guide names every value the operator
    copies and every address they register. Guard: `scripts/check-docs.py` and review.

## Decisions (owner, 2026-04-23)

From the phase-2 brainstorm, still valid:

1. **Google and Microsoft in this slice.** GitHub, the "linked logins" screen and the full settings of an account
   without a password are slice 0012. A Google-style account switcher is a later separate item.
2. **OpenIddict's client and its web providers** (`OpenIddict.Client.WebIntegration`), at the version of the server,
   7.7.1. Logins are stored in Identity's `AspNetUserLogins`.
3. **Tickets** are random, live 10 minutes, are stored as a hash only and are used once.
4. **Return addresses come from configuration only**, never from the request.
5. **The manifest lists the providers** (`providers: [google, microsoft]`); secrets are only in the environment. A
   listed provider whose secrets or return address are missing is not offered and `/auth/health` says `Degraded`; the
   service never fails to start over it.
6. **Tests use a fake OpenID provider**, never real Google or Microsoft.
7. **An address that matches an existing account asks for the password once to link**; from then on the provider alone
   signs in. Only the same address links.
8. **A sign-up through a provider skips the mail** of spec 0009: the provider verified the address.
9. **An invitation is accepted through a provider only when the provider's address is the invited address**: a
   company's invitation is bound to the address it named.
10. **Private accounts keep the rules of spec 0010**: the personal space, `org_kind`, and an invitation that moves a
    private person into a company only after the same warning, on the screen and in the mail.
11. **The sign-up switches stay as in specs 0009 and 0010**: organization and private sign-up are independent manifest
    switches; the choice appears only when both are on.

From the questions of 2026-04-16:

12. **Microsoft accepts personal and work accounts of any tenant.** A Microsoft person is identified by tenant id and
    object id, never by address. An address Microsoft did not verify never links without the password and never skips
    the mail of sign-up.
13. **The provider buttons come first** on every screen (sign-in, sign-up, invitation). An unknown address with sign-up
    on gets "Finish creating your account" (the kind when both switches are on, the company name for an organization)
    and is signed in at once, as in spec 0009, Amendment 2. With sign-up off it gets "You have no account. Ask for an
    invitation."
14. **An account without a password confirms its deletion with the provider**: a round trip with `purpose=reauth`, a
    ticket valid 5 minutes, usable only to delete that account. An account with a password keeps the password.
15. **"Forgot password" works for an account without a password** and sets one; the account then has both.
16. **No real keys in this slice.** A guide says how to get Google Cloud and Microsoft Entra keys; the live test with
    real keys is an open item for the owner.

The approved design:

17. **The flow.** The button opens `GET /auth/providers/{name}/start` (with `?invite=` or `?purpose=reauth`); the
    provider sends the browser to `GET /auth/providers/{name}/callback`; Auth-Core sends it to the product's return
    screen with the ticket or an error code in the fragment. The app calls `POST /auth/providers/exchange`, which signs
    in or answers `next` (`link`, `finish_signup`) or an error; `POST /auth/providers/link` and `POST
    /auth/providers/finish` sign in; `DELETE /auth/account` takes `password` or `provider_ticket`. PKCE, state and nonce
    through OpenIddict. Link passwords go through the lockout. The new endpoints have per-IP limits.
18. **Configuration.** The manifest's `providers`; `AUTH_GOOGLE_CLIENT_ID`, `AUTH_GOOGLE_CLIENT_SECRET`,
    `AUTH_MICROSOFT_CLIENT_ID`, `AUTH_MICROSOFT_CLIENT_SECRET` and `AUTH_FRONTEND_PROVIDER_URL`, mapped to `Auth:...`
    keys as the other settings are. `GET /auth/options` gains `providers`; `GET /auth/me` says whether the account has a
    password and which providers it signs in with.
19. **The samples.** Provider buttons on the sign-in, sign-up and invitation screens, the return screen, the link
    screen, the finish screen, and "Confirm with Google/Microsoft" in "Delete account" for an account without a
    password. The Python package and `notes-api` do not change.
20. **Tests, security and documents.** A fake provider covering Google, Microsoft personal and Microsoft work with an
    unverified address; one test per outcome and per error; Angular unit tests; Playwright at the end; the .NET suite
    on Linux too. The identity by subject; tickets hashed, single use, in the fragment; configured return addresses;
    the reauth ticket's limits; audit rows for the provider sign-in, the link, the sign-up and the reauth; new residual
    risks. A keys guide, the integration guides, a runbook for the Microsoft secret, OpenAPI, `docs/design.md` and the
    changelog.

### Settled in this spec

Choices the decisions left open; the contract above reads this way.

1. **Paths and fragment.** `/auth/providers/{name}/start` and `/callback`; the fragment members `ticket`, `purpose`
   (`sign_in`, `invite`, `reauth`) and `error`, so that one return screen serves every purpose.
2. **The callback address is the return address's origin plus `/auth/providers/{name}/callback`**, not the issuer's:
   the return screen and `/auth` share one origin (ADR 0004), the browser's, while a development issuer may name
   another port.
3. **A table of its own, `ProviderTickets`**, not the link tokens of spec 0004: those are one row per account and kind,
   and a ticket has no account until the exchange.
4. **One error code, `invalid_ticket`, for a ticket that is unknown, used, expired, of another purpose or without its
   binding** (the brief's `ticket_expired` is folded in), as `invalid_token` covers every dead link: the screen says
   "start again" for all of them.
5. **An exchange that answers `next` leaves the ticket usable** for the link or the finish, and a refusal leaves it
   usable: a wrong password can be retyped; the 10 minutes bound it.
6. **The browser binding cookie `auth_pt`** (`SameSite=Strict`, `Path=/auth`, 10 minutes), set by the callback and
   required with every ticket: without it a ticket pushed into another browser would sign that browser in to someone
   else's account. Found while designing; not in the brief.
7. **The exchange signs in through OpenIddict's token endpoint with a placeholder request**, as the sign-up completion
   does, so no new grant type is needed and the session is a login's in every respect.
8. **An unverified address always gets `next` `link`**, with an account or without: the answer must not tell someone
   who did not prove the mailbox whether an account exists. The link answers a missing account as a wrong password.
9. **Starting twice in one browser:** each round trip has its own ticket; the later callback replaces `auth_pt`, so the
   earlier ticket becomes `invalid_ticket` and that tab starts again. Tickets are not "newest only" per identity: the
   binding already settles it without another write.
10. **A linked identity signs in whatever its current address**, and the address of the account does not follow the
    provider's: the identity key is the person (Decision 12).
11. **A look-alike spelling of an existing account is `409 already_registered`** at the exchange and the finish, as at
    a sign-up completion (spec 0009, Amendment 4; residual risk 47).
12. **Linking when the identifier is locked is `429 too_many_attempts`**: the link is a password attempt. A provider
    sign-in of a linked identity ignores the lock: the lockout guards passwords, and a provider sign-in proves the
    person.
13. **The link confirms an unconfirmed address** when the ticket's address is verified: the password proves the account
    and the provider proves the mailbox. With an unverified address it is `403 email_not_verified`, as a login is.
14. **An account without a password cannot link a second provider** by the link (it has no password to give): the link
    answers `401 invalid_credentials` and the screen points at "forgot password". Linking from a signed-in session is
    slice 0012.
15. **An invitation for an address with a private account is not accepted through a provider** (`409
    private_account_exists`); the screen shows only the move of spec 0010, which needs no password. The provider would
    add nothing there, and linking it without the password would break Decision 7.
16. **An invitation through a provider signs in at once** (`200`, tokens), unlike a password acceptance (`204`): the
    provider has just proved the person, as the finish signs in (Decision 13).
17. **A person removed from a company, or whose company was deleted, has no account** (spec 0010 deletes it, with its
    logins). Their identity is unknown again: it gets `finish_signup` or `no_account` like any new person.
18. **Audit names.** A provider sign-in is `login.succeeded` with `details.provider` (one kind for every sign-in, so
    the queries of logins stay whole); the sign-up is `signup.completed` and the invitation `invite.accepted`, each with
    `details.provider`; the link and the reauth get kinds of their own, `provider.linked` and
    `provider.reauthenticated`, because no existing kind says what happened. A failed callback writes no row: no
    account is known there.
19. **The five provider paths count against the `login` policy** (30 a minute): each step is part of a sign-in, and a
    person makes one round trip at a time. The start writes a state row, so `general` (300) would be too loose.
20. **`DELETE /auth/account` takes exactly one of `password` and `provider_ticket`, and the account decides which**:
    `password_required` and `reauth_required` tell the screen to redraw, judged under the lock before anything is
    counted. A ticket is not counted in the lockout: 32 random bytes cannot be guessed.
21. **Field names.** `/auth/options` `providers` (the offered ones, in manifest order); `/auth/me` `has_password` and
    `linked_providers` (sorted), so that the two lists are never confused.
22. **The fake provider is a small ASP.NET Core project, `tests/Auth.FakeOidc`**, with no new third-party package. The
    integration tests run it in process behind OpenIddict's HTTP client, so the real web-provider registrations are
    tested; the end-to-end stack runs it as a container (`tests/Auth.FakeOidc/compose.yml`, loopback port 8099)
    through `Auth:Providers:TestIssuer`, because a browser cannot be sent to a fake `accounts.google.com`. Its page
    offers the three people, a free address with a "verified" box, and "Cancel"; a `person` query parameter answers at
    once for scripts.
23. **The test issuer is Development only**, and setting it elsewhere stops the start: a production instance must never
    trust a provider an operator typed by mistake.
24. **A bad return address or a bad secret date stops the start; a missing one does not**, as for the completion
    address of spec 0009: a typo is caught at once, an absent key only turns the feature off.
25. **The Microsoft secret's date is an optional setting that makes health `Degraded` 30 days ahead**: health is where
    an operator already looks, and the secret's lapse is otherwise silent until sign-ins fail.
26. **Reauth asks the provider for a fresh sign-in** (`prompt=login`, `max_age=0`) and Auth-Core does not check
    `auth_time`: the providers differ in what they send, and the residual is named (risk 57).
27. **The ticket of a provider dropped from the manifest after the callback is still honoured** for its 10 minutes:
    the provider was offered when the person proved who they are.
28. **No new mail and no changed text**: every existing mail already points at "forgot password" or the sign-in screen.
29. **The sample's provider buttons are links**, not calls: the round trip is a navigation, and the CSP of spec 0007
    needs no change.
30. **The provider confirmation comes before the product's data is deleted**: the sample confirms with the provider,
    then calls `DELETE /api/me` and `DELETE /auth/account`. A ticket that expires in between is the only way to reach
    risk 51 here.
31. **The Playwright spec runs in Chromium and WebKit on `https://localhost:8443`**, the origin of the existing tests:
    the return address of the provider stack is on that origin, so `auth_pt` (`Secure`) works in WebKit too.
32. **The guide's file is `docs/deployment/provider-keys.md`** (setting up a deployment) and the runbook's
    `docs/operations/microsoft-secret.md` (beside `key-rotation.md`).

## Verification notes (for the local verifiers)

- Criteria 7, 8 and 10 to 24 need the fake provider in process: the test reads the `Location` of the start, asks the
  fake for a code bound to its `state`, `nonce` and PKCE challenge, and calls the callback with the request-forgery
  cookie of the start. Criteria 4 and 14 need a controlled clock; criterion 20 parallel requests against PostgreSQL;
  criterion 25 the dispatcher run by hand (the `MailTestBase` pattern); criterion 30 a host with the manifest and
  settings of spec 0010.
- Criteria 32 and 33 need the stack with `tests/Auth.FakeOidc/compose.yml`: the fake's keys, the test issuer and
  `Auth__App__FrontendUrls__ProviderReturn=https://localhost:8443/provider-return` are set there. An e2e script that
  waits for `Healthy` on a stack whose manifest lists providers without that overlay is updated.
- Criterion 9 is a table test; criterion 28 reads every audit row written by the provider tests.
- No step uses a real Google or Microsoft key. The live test with real keys is the owner's open item (Decision 16).

## To verify during implementation

Each item has the answer to take if the check confirms it, and what to do if it does not.

- **OpenIddict's client beside its server in one host.** The client's redirection endpoint for both registrations in
  pass-through mode, with errors passed through too, so that Auth-Core always answers the callback itself (never
  OpenIddict's error page). The client's handlers must not touch the server's token endpoints, and the server's must
  not touch the callback. If pass-through of errors is not available in 7.7.1, stop and report.
- **The response mode is `query`** for both web providers in 7.7.1 (the callback is `GET` only; a `form_post` would be
  a cross-site form post, which the JSON-only rule refuses and `SameSite=Lax` cookies do not follow). If a provider
  forces `form_post`, stop and report.
- **Microsoft's `common` tenant.** The web provider's issuer check accepts identity tokens of any tenant
  (`https://login.microsoftonline.com/{tid}/v2.0`) and personal accounts.
- **Where the claims are.** `tid`, `oid`, `email`, `xms_edov` (Microsoft) and `sub`, `email`, `email_verified` (Google)
  are read from the identity token's principal, never from userinfo. `xms_edov` may come as a JSON boolean or as the
  string `"1"`/`"true"`; accept those and nothing else. `oid` must be present for personal Microsoft accounts in v2.0
  tokens; if it is not, key personal accounts by `9188040d-6c67-4c5b-b112-36a304b66dad:{sub}` and record it as an
  amendment.
- **The request-forgery cookie** of OpenIddict's client: its name, `SameSite=Lax`, its path covering
  `/auth/providers/`, and that it reaches the `GET` callback after a cross-site redirect in Chromium and WebKit.
- **State tokens** are stored in OpenIddict's token table, live 10 minutes (`SetStateTokenLifetime`), and are pruned by
  the existing `TokenPruner`.
- **The client's signing and encryption credentials** are the instance's key files (`KeyMaterial`), so a key change
  voids only the round trips in flight.
- **A `200` that is not a token response** from a pass-through token endpoint (the exchange's `next`) is written as it
  is; `LoginResponseShaper` and `SessionResponseHandler` act on sign-ins only.
- **The placeholder request handler** for the three provider paths is ordered before `UnhandledTokenRequestGuard`,
  like the one of the sign-up completion, and claims the paths with and without a trailing slash.
- **The fake behind OpenIddict's HTTP client.** The web providers fetch their configuration and keys through
  `OpenIddict.Client.SystemNetHttp`; the tests replace its handler so that `accounts.google.com`,
  `oauth2.googleapis.com`, `www.googleapis.com` and `login.microsoftonline.com` reach the fake. Whether 7.7.1 uses
  discovery or static configuration for each provider decides which addresses the fake answers.
- **The fake on plain HTTP in the end-to-end stack.** OpenIddict's client may refuse an `http` issuer; if it does, the
  fake is served through the sample's Caddy with `tls internal` and the Auth-Core container of the development stack
  trusts that authority. The fake advertises its authorization endpoint on the browser's address
  (`http://localhost:8099`) and its token and key endpoints on the container network.
- **The EF migration**: `ProviderTickets` with a unique index on the ticket hash and an index on the expiry; model and
  migration in one task (a model change without its migration stops the host).
- **The deletion routine** of spec 0010 deletes `AspNetUserLogins` by cascade (spec 0010 says Identity's delete does);
  a test pins it for an account with two logins.
- **`scripts/test-linux.sh`** arrives from a parallel branch; criterion 35 uses it only if it is on `main` when this
  slice is implemented.
