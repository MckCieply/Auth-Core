# Spec 0010 — Private accounts: a person signs up alone, and can leave

- **Status:** Accepted (owner, 2026-04-08)
- **Date:** 2026-04-08
- **Author:** Alex
- **Milestone:** Phase 2, slice 0010 — *"a private person signs up, uses the product alone, and deletes their account"*
  ([`docs/design.md`](../../design.md), "Phase 2: sign-up and social login").
- **Context:** specs [0004](0004-email-flows.md) (mail queue, link tokens, mail limit, password policy),
  [0005](0005-tenancy-and-rbac.md) (companies, memberships, roles, invitations, the manifest, the company API),
  [0008](0008-hardening-and-release.md) (per-IP limits, audit log, company deletion, `DELETE /auth/org`) and
  [0009](0009-self-service-signup.md) (sign-up, its amendments and As built). This spec changes parts of 0005 and 0009;
  each change is named where it is made.

## Goal

A product can let a private person use it alone, without a company. The person signs up with an email address only,
proves it through the mail link, chooses a password and is signed in. They get the same product features a company
member gets, in a one-person space nobody else can join. A product may still treat the two kinds differently: the
access token says which kind the account is.

A private person can delete their own account. A company can invite a private person; the person sees a clear warning
and decides. And no account is ever left without a space again: an account that loses its only space is deleted, and
its address is free for a new sign-up.

Private sign-up is off by default. A product turns it on in its manifest. An instance that does not turn it on gets
no new feature, and sees these changes: access tokens and `GET /auth/me` gain `org_kind: "organization"`,
`GET /auth/options` gains `"private_signup":"off"`, and accounts that lose their company are deleted instead of kept
without access (Decision 9), which also ends the comeback of spec 0009, Amendment 3.

Concretely, this is met when this sequence works against a running instance with a mail catcher and private sign-up on:

```
curl -i .../auth/options                                          # -> 200 {"signup":"self-service","private_signup":"self-service"}
curl -i -X POST .../auth/signup -d '{"email":"ola@poczta.test","kind":"private"}'   # -> 202
# the mail holds  <complete_signup url>?token=<t>
curl -i -X POST .../auth/signup/preview  -d '{"token":"<t>"}'    # -> 200 {"kind":"private","email":"ola@poczta.test"}
curl -i -X POST .../auth/signup/complete -d '{"token":"<t>","password":"<p>"}'
                                                                  # -> 200, signed in: org_kind "personal", roles ["private"]
curl -i .../auth/org -H 'Authorization: Bearer <a>'               # -> 403 {"error":"forbidden"}

# the person deletes the account (the product deleted its own data first)
curl -i -X DELETE .../auth/account -H 'Authorization: Bearer <a>' -d '{"password":"<p>"}'   # -> 204, cookie cleared
curl -i -X POST .../auth/login -d '{"email":"ola@poczta.test","password":"<p>"}'           # -> 401, as an unknown address
```

## In scope

- Spaces of two kinds: a company (`organization`) and a hidden one-person space (`personal`).
- The manifest block `private` (switch and permissions); `GET /auth/options` gains `private_signup`.
- `POST /auth/signup`, `/preview` and `/complete` gain the kind `private`; the pending sign-up records its kind.
- The access token and `GET /auth/me` gain `org_kind`; the private role `private` with live permissions.
- The company API and the operator CLI refuse or hide personal spaces.
- Invitations to an address that has a private account: the mail paragraph, `has_private_account`,
  `leave_private_account`, `409 private_account_exists`.
- `DELETE /auth/account` for private accounts.
- Accounts are deleted when they lose their only space: member removal, company deletion, self-deletion, and once at
  upgrade. Amendment 3 of spec 0009 is removed.
- Mail texts in Polish and English; audit kinds `account.deleted` and `account.delete_refused`; OpenAPI.
- The Python package's `Principal.org_kind`.
- `samples/notes-web`: the sign-up choice, the private completion, the account bar, "Delete account", the invitation
  warning, a small Team screen with the removal confirmation; unit and Playwright tests.
- `samples/notes-api`: `DELETE /api/me` and `org_kind`; its `auth.yaml` and `deploy/auth.yaml` turn private sign-up on.
- `scripts/e2e-private.sh`; the earlier e2e scripts that pin the old behaviour are updated.
- Documents: the threat model, both integration guides, the backup runbook, `docs/design.md`, the changelog.

## Out of scope

- Google, Microsoft and GitHub (slices 0011 and 0012). Accounts without a password get another confirmation for
  self-deletion there (Decision 8).
- A deletion feed that tells the product which spaces were deleted (Decision 8: a later idea).
- Deleting one's own account as a company member: company accounts are managed by the company's admins (Decision 8).
- An operator command that deletes an account.
- Turning a company account into a private one, or a private account into a company other than by an invitation.
- Several spaces per account; a company with private members; exporting a person's data.

## Contract

### General rules

- Every rule of specs 0004, 0005, 0008 and 0009 holds unless this spec changes it: JSON bodies of at most 8 KiB in
  UTF-8, unknown members ignored, `400 invalid_request` for a body that is not a JSON object or a member of the wrong
  type; `Cache-Control: no-store`; the security headers; the `415` of a body in another charset and the per-IP `429`
  come first.
- **Organization sign-up is on** when the manifest says `signup: self-service` and the completion address
  (`Auth:App:FrontendUrls:CompleteSignup`) is set, as in spec 0009. **Private sign-up is on** when the manifest says
  `private.signup: self-service` and the same address is set. The two switches are independent.
- **A blank `password`** (empty, or only white space) is `400 invalid_request`, like one that is not a string or holds
  NUL. The body reader refuses it, as it does today at sign-up completion, invitation acceptance and `DELETE /auth/org`.

### Spaces

- Every account belongs to exactly one **space**: a company (kind `organization`, as in spec 0005) or a **personal
  space** (kind `personal`). There is no account without a space (below, "Accounts are never left without a space").
- The kind is a column of the companies table, `Kind` (`smallint`: `1` organization, `2` personal). Every existing row
  is an organization.
- A personal space is created only by a private sign-up. It is named `private`, has one role row named `private` whose
  stored permission list is empty, and exactly one member, its account, with that role. Nobody can join it: the company
  API refuses it and invitations name companies only.
- **The private role.** Its name in the token is `private`. Its permissions are not stored per space: they are read
  from the active manifest (`private.permissions`) at every login and refresh, sorted ordinally, without duplicates.
  A manifest change reaches every private account at its next refresh, as a role change reaches a member (spec 0005).

### Manifest

A new optional top-level block:

```yaml
private:
  signup: self-service
  permissions: [documents:read, documents:write]
```

- `private.signup: self-service` turns private sign-up on. Any other value, no `signup` key, or no block leaves it off
  (Decision 3).
- `private.permissions` lists what the private role grants; absent means none. Each must be in the catalog. The
  built-in permissions `members:manage`, `roles:manage`, `org:manage`, `org:delete` and `*` are refused. At most 500,
  duplicates folded. A permission that breaks a rule, or any other key in the block, makes the manifest invalid with the
  rules of spec 0005: the last valid manifest stays active and `/auth/health` reports `Degraded`.
- A `private:` key with no value counts as absent, as `signup:` does (spec 0009, Amendment 1, item 6).
- A manifest stored by an earlier version has no `private` and reads as absent. A manifest that drops the block later
  leaves the private accounts in place: they still sign in, with `permissions: []`, and private sign-up is off.

### `GET /auth/options`

`200 {"signup": "self-service" | "invite-only", "private_signup": "self-service" | "off"}`. Each member follows its
switch above, so a screen never offers a sign-up that would answer `403`. With private sign-up wanted and no completion
address, private sign-up is off, the start logs a warning naming the key, and `/auth/health` reports `Degraded`, as for
organization sign-up.

### `POST /auth/signup` — `{"email", "kind"?, "company_name"?}` (changes spec 0009)

`kind` is `"organization"` (the default when it is absent) or `"private"`. The checks, in this order:

1. Both switches off: `403 signup_disabled` before the body is read.
2. A body that is not a JSON object, or a `kind` that is not one of the two strings: `400 invalid_request`.
3. The switch of the kind is off: `403 signup_disabled`, before the other members are judged.
4. `email` breaks the address rule of invitations: `400 invalid_request`. For `organization`, `company_name` is
   required and must pass the name rule, as in spec 0009. For `private`, a `company_name` member, whatever its value,
   is `400 invalid_request`.
5. The mail limit of the address, kind `signup`, one counter for both kinds (one request a minute, five an hour):
   `429 too_many_attempts` with `retry_after_seconds`.
6. Otherwise the pending sign-up of the address is created or replaced with its kind, its typed address and (for an
   organization) its company name, a mail request is queued, `signup.requested` is written with `details.kind`, and the
   answer is `202` with an empty body.

As in spec 0009, the request never reads the accounts: every address gets the same status, headers and body and runs
the same statements. The link already mailed stays usable until the new mail is sent, except when the typed address
differs from the stored one beyond ASCII case (Amendment 4, item 1) **or the kind changes**: then the earlier link is
void at once (`400 invalid_token`).

### Mails

When a sign-up mail is sent:

1. **The switch is checked first.** While the pending sign-up of the address exists, the switch of its kind must be
   on; when it is gone, at least one switch must be on. Otherwise the request is dropped and nothing is sent.
2. **The address has an account**, of any kind, under any spelling the normaliser folds together: the mail "you already
   have an account" goes out to the address stored on the account, and the pending sign-up is deleted. Every account
   now has a space, so the distinctions of Amendments 3 and 4 between accounts with and without a company are gone.
3. **Otherwise**, if the pending sign-up still exists: a new link token is issued for it and the mail "complete your
   sign-up" goes out in the variant of its kind. With neither, nothing is sent.

**"Complete your sign-up", private variant.** The subject, greeting, button, validity, link hint and ignore line are
those of spec 0009. The intro has no company:

- EN: "Someone asked to create a private account on {0} with this email address. If it was you, use the link below
  to finish signing up."
- PL: "Ktoś, podając ten adres e-mail, poprosił o założenie konta prywatnego w {0}. Jeśli to Ty, użyj poniższego
  linku, aby dokończyć rejestrację."

**"You already have an account"** goes out for both kinds and gains one sentence after the sign-in hint (Decision 6):

- EN: "If you want a new account on purpose, delete the one you have first, or sign up with another email address."
- PL: "Jeśli celowo chcesz założyć nowe konto, najpierw usuń dotychczasowe albo zarejestruj się z innym adresem
  e-mail."

Both mails keep the rules of spec 0004: the instance's locale, HTML and text parts, the queue and its retries.

### `POST /auth/signup/preview` — `{"token"}` (changes spec 0009)

1. Both switches off: `403 signup_disabled` before the body is read.
2. An unknown, used, expired or replaced token: `400 invalid_token`.
3. The switch of the pending sign-up's kind is off: `403 signup_disabled`.
4. Otherwise `200 {"kind":"organization","email","company_name"}` or `200 {"kind":"private","email"}`. It changes
   nothing. `has_account` is gone (Amendment 3 is removed).

### `POST /auth/signup/complete` — `{"token", "password"?, "company_name"?}` (changes spec 0009)

1. Both switches off: `403 signup_disabled` before the body is read.
2. A body that is not a JSON object; a `token` that is missing or not a string; a `password` that is present and not a
   string, is blank or holds NUL; a `company_name` that is present and breaks the name rule: `400 invalid_request`.
3. A token that is unknown, used, expired or replaced: `400 invalid_token`.
4. The switch of the pending sign-up's kind is off: `403 signup_disabled`.
5. **Organization:** no `company_name`: `400 invalid_request`. **Private:** a `company_name`: `400 invalid_request`.
   The token stays usable.
6. Any account under the address (any kind, also one that only resembles the typed address): `409
   already_registered`, whatever the password. The pending sign-up is deleted.
7. No `password`: `400 invalid_request`. A password that breaks the policy of spec 0004: `400 weak_password` with the
   rules. The token stays usable.
8. Otherwise, in one transaction, the account is created with the typed address, the password and its email confirmed,
   and:
   - **organization:** as spec 0009: the company with a copy of the default roles, the membership with the first
     admin's role, `org.created` with `details.via` `signup`;
   - **private:** the personal space, its `private` role row and the membership; no `org.*` row;
   - the pending sign-up is deleted, `signup.completed` is written with `details.kind`, and the person is signed in as
     a login signs in (spec 0009, Amendment 2): `200 {"status":"authenticated","access_token"}`, the refresh cookie and
     `login.succeeded`, in the same transaction.

The 24-hour link, the single use (of parallel completions one gets past step 3), and the address lock shared with
invitation acceptance stay as in spec 0009.

Two answers move because the kind is known only from the token: a missing `company_name` with an unknown token was
`400 invalid_request` and is now `400 invalid_token`; a missing `password` was already judged after the token (spec
0009, As built).

### Pending sign-ups

As spec 0009, with two changes: the row has a `Kind` (`smallint`: `1` organization, `2` private; existing rows are
organizations), and `CompanyName` is null exactly for a private sign-up.

### Access token and `GET /auth/me` (changes spec 0005)

The access token gains one claim:

```json
{ "sub": "…", "org_id": "space id", "org_kind": "personal", "roles": ["private"], "permissions": ["documents:read", "documents:write"] }
```

- `org_kind` is `"organization"` for a company member and `"personal"` for a private account. It is read with the
  membership at every login and refresh, like the other tenancy claims, and the refresh token never carries it.
- For a company member nothing else changes. A backend that verifies tokens today needs no change: a private account
  looks like the only member of a company whose role is `private`.
- `GET /auth/me` adds `org_kind`. For a private account it answers `org_name` `"private"`, `roles` `["private"]` and the
  manifest's private permissions.

### The company API and the operator CLI

- Every endpoint under `/auth/org` answers a caller whose space is personal, read from the database, with
  `403 {"error":"forbidden"}`, whatever the token says. This holds also for an endpoint that needs no permission
  (`GET /auth/org`): a check on permissions alone would let a personal account through. `GET /auth/me` stays open to
  every account.
- The CLI hides personal spaces: `list-orgs` does not list them, and `invite`, `remove-member` and `delete-org` given
  the id of one answer `error: not_found`.

### Invitations (changes spec 0005)

**Sending** is unchanged: `POST /auth/org/invites` answers `202` for an address with a private account exactly as for
any other address (status, headers, body), and the inviter learns nothing.

**The mail.** When the invitation mail is composed and the invited address has a private account spelled the same up
to ASCII case, the intro's last sentence and one paragraph change (`{0}` the application, `{1}` the company; the
company is HTML-encoded as before, and the mail does not name the role):

- EN intro: "You have been invited to join {1} on {0}. Use the link below to accept the invitation."
- EN paragraph: "You already have a private account on {0} with this email address. If you accept, you move to {1}:
  the space of your private account is deleted, with everything in it. Your account itself stays, with its email
  address and its password, and becomes a member of {1}."
- PL intro: "Zaproszono Cię do firmy {1} w {0}. Użyj poniższego linku, aby przyjąć zaproszenie."
- PL paragraph: "Masz już konto prywatne w {0} z tym adresem e-mail. Jeśli przyjmiesz zaproszenie, przejdziesz do
  firmy {1}: przestrzeń Twojego konta prywatnego zostanie usunięta razem z całą zawartością. Samo konto zostaje, z tym
  samym adresem e-mail i hasłem, i stanie się kontem członka firmy {1}."

Any other address gets the mail of spec 0005. The paragraph is decided when the mail is composed; the preview decides
again when the link is used.

**`POST /auth/invites/preview` — `{"token"}`.** `200 {"org_name", "email", "role", "has_private_account"}`; the last
member is `true` when the invited address has a private account (spelled the same), else `false`. An address of a
company member: `409 already_member`, as before. Unknown, used, expired, cancelled, replaced or look-alike:
`400 invalid_token`, as before.

**`POST /auth/invites/accept` — `{"token", "password"?, "leave_private_account"?}`.** The checks, in this order:

1. A body that is not a JSON object; a `token` that is missing or not a string; a `password` that is present and not
   a string, is blank or holds NUL; a `leave_private_account` that is present and not a JSON boolean: `400 invalid_request`.
2. Unknown, used, expired, cancelled, replaced or look-alike: `400 invalid_token`.
3. The address belongs to a company member: `409 already_member`; the invitation stays usable.
4. The address has a private account and `leave_private_account` is not `true`: `409
   {"error":"private_account_exists"}`. Nothing changes; the invitation stays usable.
5. The address has a private account and `leave_private_account` is `true`, in one transaction: the personal space,
   its role row and its membership are deleted; the account becomes a member with the invitation's role; its password
   is kept (a `password` sent is not used); every session of the account ends and its mail links and login streak go,
   as at any acceptance; the invitation is used up; `invite.accepted` is written with `details.role`,
   `details.left_private_account` `true` and `details.personal_org_id`. `204`.
6. No account: no `password` is `400 invalid_request`; a weak one is `400 weak_password`; the token stays usable.
   Otherwise the account is created, as before. `204`.

The path of spec 0005 where an existing account without a company sets a new password is gone: such accounts no longer
exist. As at sign-up, a missing `password` with an unknown token is now `400 invalid_token`.

### `DELETE /auth/account` — `{"password"}`

Authenticated with the access token. Only for a private account. The checks, in this order, mirror `DELETE /auth/org`
(spec 0008). The token's `org_kind` is judged first, against the database: an account that moved to a company is the
case where the frontend must refresh, so it never reads as `forbidden`.

| Answer | When |
| --- | --- |
| `401` | no token, or an invalid or expired one, as on the company API |
| `403 permissions_changed` | the token says `org_kind` `personal` but the database does not show a personal space for the account (it moved to a company, or is gone); also an account with no membership whose token carries an `org_id` |
| `403 forbidden` | the token does not say `personal` and the caller's space, read from the database, is a company: company accounts are managed by their admins; also an account with no membership and no `org_id` in the token |
| `400 invalid_request` | the body is not a JSON object, or `password` is missing, not a string, blank or holds NUL |
| `429 too_many_attempts` | the caller's identifier is locked (spec 0003), with `retry_after_seconds` |
| `403 wrong_password` | the password is wrong; this counts in the login streak as a failed login does |
| `204` | the account is deleted; the answer clears the refresh cookie as logout does |

- The password is evaluated once, as login does: the attempt is counted first, a locked identifier is `429`, a correct
  password ends the streak. A wrong password and a lock write `account.delete_refused` with `details.reason`
  `wrong_password` or `locked`.
- The deletion is that of the next section, with `details.reason` `self`.
- **The product deletes its own data first.** Auth-Core does not tell the product (Decision 8). The product's frontend
  calls the product's own "delete my data" endpoint, then this one.

### Accounts are never left without a space

An account that loses its only space is deleted, and its address is free. This happens in four places:

| When | Answer | Audit, in the same transaction |
| --- | --- | --- |
| An admin removes a member (API or `remove-member`) | unchanged, `204` | `member.removed` and `account.deleted` with `details.reason` `member_removed` |
| A company is deleted (`DELETE /auth/org` or `delete-org`) | unchanged, `204` | `account.deleted` with `details.reason` `org_deleted` for every member, and `org.deleted` |
| A private person deletes their account | `204` | `account.deleted` with `details.reason` `self` |
| At upgrade, once per account without a membership | — | `account.deleted` with `details.reason` `upgrade` |

- These rows are written in the same transaction as the change. The contract names no order among them and no time
  they share; tests count the rows and do not sort them.
- The admin who deletes a company is a member too: their account goes with the others.
- A removed or deleted person's access token gets `403 permissions_changed` at once at `/auth/me` and at every company
  endpoint whose permission the token claims (one that needs no permission included). An endpoint that needs a
  permission the token never claimed answers `403 forbidden`, as today (spec 0005). Their next refresh is `401
  invalid_grant`; a login is the `401` of an unknown address (spec 0001). An invitation to
  the address later creates a new account; so does a sign-up.
- `403 no_membership` at login (spec 0005) stays, as a safeguard for an account that should not exist.

### What an account deletion removes

One routine serves the four places. It runs in the caller's transaction, under the locks of "Locks" below, and deletes:

- the account, with what the identity tables hold for it (its linked logins, claims and stored tokens);
- its membership; for a personal space also the space and its role row;
- its refresh tokens and their authorizations (deleted, not only revoked);
- its mail links, the queued mail requests of its address other than invitations, the pending sign-up of its address,
  and its login streak.

The queued mail requests are deleted before the pending sign-up: the mail sender locks a mail request first and then
the pending sign-up, and the other order can deadlock. Refresh tokens are deleted before their authorizations. The
routine waits for a mail that is being sent; that is fine.

It keeps:

- the audit rows that name the account, its address and the client addresses, for their 90 days (Decision 10);
- invitations from companies to the address (they belong to those companies); invitations the account sent lose their
  sender, as before;
- the mail-limit counters, which hold a hash of the address and expire within hours.

**Locks.** A flow that changes a company takes the company lock before the address lock, as invitation acceptance does
today; no path takes them the other way round. Removal and company deletion hold the company lock and take **no
address lock**, not even one per member: the company lock already keeps out the flows that could change those
accounts, and a lock per member would stop a large company from being deleted (the database runs out of lock slots).
Self-deletion, the invitation move and the start-up step change an account that no company lock guards. They take the
address lock that sign-up completion takes, and read the membership again after taking it: a self-deletion that finds
the space is no longer personal answers `403 permissions_changed`. A personal space has no lock of its own: only its
account changes it, always under the address lock. The deletion removes the queued mail requests and the pending sign-up of
the addresses first, as the mail sender locks them, and then reads the accounts it deletes under a row lock; a sign-in or a
refresh reads its account again under a share lock before it writes a session, so no session of a deleted account survives.

### Upgrade

At start, after the migrations and the manifest, and before the development seeder and the first request, every account
without a membership is deleted by the routine above with `details.reason` `upgrade`, no actor and no client address.
A start that finds none changes nothing. The step runs in the service only, never in the CLI.

### Limits

Per-IP (spec 0008): the sign-up paths keep their policies (`email` for the request, `invite` for preview and
completion), the invitation paths keep `invite`, and `DELETE /auth/account` counts against `general`, as
`DELETE /auth/org` does. The mail limit `signup` is shared by both kinds.

### Audit events

Two new kinds (26 in all):

| Kind | Written when | Fields |
| --- | --- | --- |
| `account.deleted` | an account is deleted (above) | `actor_user_id` (the account itself for `self`, the admin, or none for the CLI and the upgrade), `subject_user_id`, `subject_email`, `org_id`, `org_name` (the space it lost; none for `upgrade`), `details.reason` |
| `account.delete_refused` | `DELETE /auth/account` refuses a wrong password or a lock | `actor_user_id`, `subject_user_id`, `subject_email`, `org_id`, `org_name`, `details.reason` |

- `signup.requested` and `signup.completed` gain `details.kind`; `signup.completed` loses `details.existing_account`.
- `invite.accepted` gains `details.left_private_account` and `details.personal_org_id` when a private account moves.
- The reason is `details.reason`, not `details.via`: the operator's rows already carry `details.via` `cli`.
- A personal space writes no `org.*` row: its life is in the rows of its account.

### OpenAPI

`DELETE /auth/account` is added with its answers, its `415` and the cookie it clears. The changed bodies and answers
are described: `GET /auth/options`, `POST /auth/signup`, `/preview`, `/complete`, `POST /auth/invites/preview`,
`/accept` (`409 private_account_exists`) and `GET /auth/me`. The counts pinned by `OpenApiHardeningTests` rise by one:
31 operations, 21 under the charset guard.

### Python package (`clients/python`)

`Principal` gains `org_kind: str`, the last field, defaulting to `"organization"`. An absent claim reads as
`"organization"`, so tokens of earlier instances keep working. A claim that is present and is not `"organization"` or
`"personal"` is refused like any other malformed claim (`401`). The release is part of `v0.2.0` (slice 0013).

### Sample frontend (`samples/notes-web`)

- **`/signup`:** "Organization / Private person" appears only when `GET /auth/options` says both are on; with one on,
  the screen is that one. A private person types only the email address. The answer is "Check your inbox" for every
  address.
- **`/complete-signup`:** for `kind` `private` the screen shows the address (read-only) and the password twice (equal,
  checked on the screen); success signs in and opens the notes. The "you already have an account" texts and the
  password-only path of Amendment 3 are removed.
- **`/login`:** "Create an account" appears when either switch is on.
- **The account bar** of the notes screen: for `org_kind` `personal` it shows the address and "Private account" (no
  company, no role) and a "Delete account" button; for a company member it shows what it shows today, and "Team" when
  the member holds `members:manage`.
- **"Delete account":** a warning ("This deletes your account and all your notes. It cannot be undone."), the password
  and the button "Delete my account". The sample first calls `DELETE /api/me`; only after its `204` it calls
  `DELETE /auth/account`. Success opens `/login` with the notice "Your account was deleted."; `wrong_password` says the
  password is wrong; a `429` shows the wait.
- **`/invite`:** when the preview says `has_private_account`, the screen shows a red warning (the mail paragraph's
  content) and one button, "Move me to the company and delete my private account", with no password field; it sends
  `leave_private_account: true`. Otherwise the screen is unchanged.
- **`/team`** (new, small): the company's members (address and role) for a member holding `members:manage`, each other
  member with "Remove". The confirmation says "This person's account will be deleted." `permission_not_held`,
  `last_manager` and `cannot_change_self` each show one sentence; `permissions_changed` refreshes and redraws.
- Texts are English, as in spec 0007.

### Sample backend (`samples/notes-api`)

- `DELETE /api/me`: authenticated; for `org_kind` `personal` it deletes every note of the token's `org_id` and answers
  `204` (also when there are none); for a company member `403 {"error":"forbidden"}`. No permission is needed: a person
  can always delete their own data.
- `auth.yaml` gains `private: {signup: self-service, permissions: [notes:read, notes:write]}`.

### Documents

- `docs/security/threat-model.md`: a section "Account deletion and personal data" (what a deletion removes and keeps;
  audit rows for 90 days on legitimate interest, not anonymised; backups until rotation; the product deletes its own
  data; the invitation move) and the residual risks below.
- `docs/integration/angular.md` and `docs/integration/python-fastapi.md`: `org_kind`; private sign-up screens; the
  order "product data first, then `DELETE /auth/account`"; a "delete my data" endpoint; the invitation warning; the
  removal confirmation text "This person's account will be deleted." for a product's own team screen; personal data
  after deletion.
- `docs/operations/backup.md`: a restore brings back accounts deleted after the backup.
- `deploy/auth.yaml` gains the `private` block with a comment; `CHANGELOG.md` (Unreleased): the private accounts, the
  deletions, and the comeback of Amendment 3 removed before any release; `docs/design.md`: the Phase 2 section.

**Residual risks** added to the threat model's list, after risk 47:

48. A product's data in a personal space that moved to a company by invitation stays in the product's tables, under an
    `org_id` that no token will carry again (Decision 16).
49. An admin who removes a member, or deletes a company, now deletes the person's account. The person can be invited
    again or sign up, as a new account.
50. Audit rows keep a deleted person's address and client addresses for 90 days; backups keep everything until they
    are rotated; a restore brings deleted accounts back.

## Acceptance criteria (Done when)

Manifest and options

1. The manifest accepts the `private` block with `signup: self-service` and catalog permissions. A built-in permission,
   `*`, a permission outside the catalog or another key in the block makes it invalid (last valid manifest kept,
   `/auth/health` `Degraded`). A stored manifest without the block reads as private sign-up off with no permissions.
2. `GET /auth/options` answers `private_signup` `self-service` only with the block's switch on and the completion
   address set, and `off` otherwise; each of the four combinations of the two switches gives its own pair of values.
   With the switch on and no address, the start warns and `/auth/health` reports `Degraded`.

Private sign-up

3. With both switches off, the three sign-up endpoints answer `403 signup_disabled` before reading the body. With only
   organization sign-up on, `kind` `private` is `403 signup_disabled`, and so are the preview and the completion of a
   private pending sign-up; the same the other way round. A request that is accepted and then completed after its
   kind was turned off is refused the same way.
4. `POST /auth/signup` with `kind` `private` and an address answers `202` with an empty body; a mail with the private
   intro and a link arrives; `signup.requested` holds `details.kind` `private`. A request without `kind` behaves as
   spec 0009. An unknown `kind`, a private request with a `company_name`, and an organization request without one are
   `400 invalid_request` and queue nothing.
5. For an address that has an account, of either kind, a request of either kind gets the same status, headers and
   body as for a new address and runs the same statements; the mail says the account exists, holds the new sentence in
   the instance's language, has no link, and the pending sign-up is gone afterwards.
6. The mail limit counts both kinds together: a private request less than a minute after an organization request for
   one address is `429`.
7. A second request that changes the kind, or types a look-alike spelling, makes the earlier link `400 invalid_token`
   at once; one that changes only the company name keeps it until the new mail is sent.
8. The preview answers `{"kind":"private","email"}` or `{"kind":"organization","email","company_name"}` and never
   `has_account`.
9. A private completion with the token and an acceptable password answers as a login: the access token carries the new
   space as `org_id`, `org_kind` `personal`, `roles` `["private"]` and the manifest's private permissions; the account
   exists with its email confirmed; the space is of kind `personal` with one role row and one member; `signup.completed`
   (`details.kind` `private`) and `login.succeeded` are written, and no `org.*` row.
10. A private completion with a `company_name` is `400 invalid_request`; without a password `400 invalid_request`; with
    a weak one `400 weak_password`; in all three the token still works afterwards. An address that has any account by
    then is `409 already_registered`, and the pending sign-up is gone. Of parallel completions exactly one succeeds.
11. An organization completion behaves as in spec 0009 apart from `org_kind` `organization` in the token,
    `details.kind` on `signup.completed`, and Amendment 3: an account without a company no longer exists to complete,
    and any account under the address is `409 already_registered`.

Claims and permissions

12. A company member's access token holds `org_kind` `organization` and otherwise exactly the claims it held before;
    `GET /auth/me` returns `org_kind` for both kinds, and for a private account `org_name` `private`.
13. After the manifest's `private.permissions` changes, a private account's next refresh carries the new list; after
    the block is removed, a private account still signs in, with `permissions` `[]`.
14. Every company API endpoint, `GET /auth/org` included, answers a private account's token with `403 forbidden`; `list-orgs` does not list
    personal spaces; `invite`, `remove-member` and `delete-org` with a personal space's id answer `error: not_found`.
15. The Python package's `Principal` has `org_kind`; a token without the claim gives `"organization"`; a token with
    another value is refused with `401`; the package's earlier tests pass.

Invitations

16. Inviting an address with a private account answers exactly as inviting an unknown address (status, headers, body).
    Its mail holds the changed intro and the paragraph, in the instance's language, in HTML and text; a mail to an
    address without an account, or to a look-alike spelling, does not.
17. The invitation preview says `has_private_account` `true` for an address with a private account and `false`
    otherwise; an address of a company member is still `409 already_member`.
18. Accepting for a private account without `leave_private_account` `true` is `409 private_account_exists` and changes
    nothing; the invitation still works. A `leave_private_account` that is not a boolean is `400 invalid_request`.
19. Accepting with `leave_private_account` `true` answers `204`; afterwards the personal space, its role row and its
    membership are gone; the account is a member with the invited role; the old password signs in and a password sent
    with the acceptance does not; a refresh token from before is refused with `401`; the new token holds `org_kind`
    `organization`; `invite.accepted` holds `left_private_account` `true` and the old space's id.
20. Accepting for an address without an account still needs a password (`400 invalid_request` without one, the token
    still usable afterwards).

Deletion

21. `DELETE /auth/account` by a private account with the right password answers `204` and clears the refresh cookie.
    Afterwards the account, its space, role row, membership, refresh tokens, authorizations, linked logins, mail links,
    queued non-invitation mail requests, pending sign-up and login streak are gone; the audit rows that name it remain;
    `account.deleted` (`reason` `self`) is written. A login with the address is the `401` of an unknown address, and a
    new sign-up with the address completes.
22. `DELETE /auth/account` answers as the table says: `401` without a token, `403 permissions_changed` for a token
    that says `personal` after the account moved to a company or is gone (never `403 forbidden`), `403 forbidden` for a
    company member whose token does not say `personal`, `400 invalid_request` for a bad
    body, `403 wrong_password` (counted in the login streak, `account.delete_refused` written), `429 too_many_attempts`
    when locked, `415` for another charset; it counts against the `general` per-IP policy.
23. Removing a member, by the API or by `remove-member`, still answers as before, and the member's account is deleted
    in the same transaction, with `member.removed` and `account.deleted` (`reason` `member_removed`; `via` `cli` from
    the CLI). The member's refresh is `401`, their login the `401` of an unknown address, and a later invitation
    creates a new account.
24. Deleting a company, by the API or by `delete-org`, deletes every member's account, the caller's included, with one
    `account.deleted` (`reason` `org_deleted`) per member and `org.deleted` as before.
25. A start on a database that holds accounts without a membership deletes each with `account.deleted` (`reason`
    `upgrade`); a second start changes nothing; the development seed users exist with their memberships afterwards.
26. An account whose membership a test deletes directly still gets `403 no_membership` at login.
27. A self-deletion, an invitation acceptance and a sign-up completion running at once for one address never leave an
    account without a space or two accounts; a removal and an acceptance at once leave one consistent outcome.

Compatibility, audit and API

28. An instance with the manifest and settings of spec 0009 and no `private` block starts; `private_signup` is `off`;
    tokens differ only by `org_kind` `organization`. Its earlier tests pass, except those that pin what this spec
    changes (the account kept after a removal or a company deletion, `no_membership` after a removal, the comeback of
    Amendment 3, `has_account`, a password for an existing account without a company), which are changed to this spec.
29. The audit log knows 26 kinds; each new row holds the fields above; no row holds a password or a token.
30. The OpenAPI document lists `DELETE /auth/account` and the changed bodies and answers; 31 operations, 21 under the
    charset guard.
31. Every new or changed mail text exists in Polish and English, in HTML and text, and the HTML encodes the company and
    role names.

Samples and end to end

32. `samples/notes-web` unit tests cover the sign-up choice, the private completion, the account bar for both kinds,
    "Delete account" (the call order, `wrong_password`, `429`), the invitation warning and the Team screen.
33. `samples/notes-api` tests cover `DELETE /api/me`: `204` for a private account (its notes gone, other spaces'
    notes untouched), `403 forbidden` for a company member, `401` without a token.
34. A Playwright spec (`samples/notes-web/e2e/10-private.spec.ts`) passes in Chromium and WebKit: a private person signs
    up through the mail, is signed in, sees "Private account" and no "Team", adds a note, deletes the account and is
    back on the sign-in screen; a private person accepts an invitation through the warning and signs in to the company;
    an admin removes a member on `/team` after the confirmation that names the deletion.
35. `scripts/e2e-private.sh` passes against a fresh stack: options, private sign-up through the mail, `/auth/me`, the
    company API refusal, the "already have an account" mail with its sentence, the invitation of a private person
    (preview, `409`, move, the old cookie refused), self-deletion (wrong password, then success, the address free),
    `403` for a company member, a removal deleting the account, and the audit rows read from the database. Every earlier
    e2e script passes, `e2e-signup.sh`, `e2e-tenancy.sh` and `e2e-hardening.sh` updated to this spec.
36. The documents of "Documents" exist and say what this spec says.

## Decisions (owner, 2026-04-08)

These replace spec 0009, Amendment 2, item 2 ("a private person gets an account without a company"): a private person
gets a personal space.

1. **A private person gets the same product features as a company member, alone.** A product may give private and
   company accounts different features.
2. **A private account owns a hidden one-person space of kind `personal`**, a company row of its own kind. The access
   token keeps `org_id` (the space), `roles` and `permissions` and gains `org_kind`, `"personal"` or `"organization"`;
   `GET /auth/me` returns it too. Company accounts get `"organization"` and nothing else in their token changes. A
   backend that verifies tokens today needs no change. The Python package's principal gains `org_kind`, an absent claim
   reading as `"organization"`.
3. **The manifest block `private`** holds the switch (`signup: self-service`; an absent block or another value means
   off) and the permissions. There is one private role per product, `private` in the token; its permissions come live
   from the active manifest, not copied per space, so a change reaches every private account at its next refresh. They
   must be in the catalog; `members:manage`, `roles:manage`, `org:manage`, `org:delete` and `*` are refused (the
   manifest is invalid and the last good one stays). The company API answers a personal space's token
   `403 forbidden`.
4. **Two independent switches**: organization sign-up (`signup`, spec 0009) and private sign-up (`private.signup`).
   Both need the completion address; with private sign-up wanted and no address it stays off and health says
   `Degraded`. `GET /auth/options` adds `private_signup`.
5. **`POST /auth/signup` gains `kind`**, `organization` by default or `private`. A private request carries no company
   name; one that does is `400 invalid_request`. The pending sign-up records the kind; the preview returns it, and the
   company name only for organizations; a private completion takes the token and the password (twice in the sample,
   checked on the screen), creates the account and the personal space and signs in as an organization completion does.
   Limits, look-alike rules, mail queue, link expiry and audit rows are those of spec 0009, with the kind recorded.
   With private sign-up off a private request is `403 signup_disabled` before the rest of the body matters.
6. **Any existing account gets "you already have an account"** for either kind, and the mail gains a sentence: to get a
   new account on purpose, delete the old one or use another address.
7. **Inviting a private person is a warning, not a refusal.** The inviter sees what they see today. The mail gains a
   paragraph; the preview says `has_private_account`; accepting needs `leave_private_account: true`, else
   `409 private_account_exists` and nothing changes. Accepting deletes the personal space and its data; the account,
   its address and its password stay and it becomes the member; every session ends; the audit records it. Members of
   another company keep `409 already_member`.
8. **`DELETE /auth/account`** with the password and the access token, for private accounts only; a company member
   gets `403 forbidden`. It mirrors `DELETE /auth/org` (wrong password counted toward lockout, lock, limits, `415`,
   OpenAPI), deletes everything of the account, frees the address, clears the refresh cookie and writes
   `account.deleted`. **The product deletes its own data under the `org_id` before calling it** (variant A); Auth-Core
   has no deletion feed (a later idea). Accounts without a password get another confirmation in a later slice.
9. **The account-without-a-company state ends.** An account that loses its only space is deleted and its address
   freed: when an admin removes it (the answer stays `204`), when its company is deleted (API or CLI), and when it
   deletes itself. **This replaces spec 0009, Amendment 3**: no comeback by sign-up, no `has_account`, no password-free
   completion. The invitation path where an existing account without a company sets a new password goes too. At
   upgrade, accounts without a membership are deleted, each with an `account.deleted` row. `403 no_membership` at
   login stays as a safeguard.
10. **Personal data after deletion.** Audit rows keep the id, the address and the client address for their 90 days, on
    the legitimate interest of security, and are not anonymised; backups keep data until they are rotated; the product
    deletes its own data. The threat model and both integration guides say so.
11. **The samples show the whole of it**: the sign-up choice, the private completion, the account bar, "Delete account"
    (product data first, then Auth-Core), the invitation warning and button, the removal confirmation "This person's
    account will be deleted."; `notes-api` deletes the caller's notes. The guides tell products to use the same
    confirmation text on their own team screens.
12. **Companies invite company-domain addresses; private persons use their own mailboxes** (gmail, wp, interia and the
    like). Collisions are rare, which is why Decision 7 warns instead of refusing.

### Settled in this spec

Choices the decisions left open; the contract above reads this way.

1. **The kind is a `Kind` column on the companies table**, and a personal space is named `private`.
2. **The private role is a hidden role row** named `private` with an empty stored list; the permissions come from the
   manifest when the membership is read. The membership keeps its required role, so the schema of memberships does not
   change.
3. **A change of kind voids the earlier link at once**, as a look-alike spelling does: a link mailed for one kind never
   completes the other.
4. **The `signup` mail limit is shared by both kinds**, so switching kinds does not double the mails an address gets.
5. **The kind's switch is judged after the token** in the preview and the completion, since only the token knows the
   kind; a missing `company_name` with an unknown token becomes `invalid_token`.
6. **A private completion with a `company_name` is `400 invalid_request`**, as the request is (Decision 5, strict).
7. **`leave_private_account` must be a JSON boolean** when present; `false` and absent mean not given.
8. **A personal space writes no `org.*` audit row**; its life is recorded in `signup.completed`, `invite.accepted` and
   `account.deleted`. The reason of a deletion is `details.reason`, because the operator's rows already use
   `details.via`.
9. **`account.delete_refused`** mirrors `org.delete_refused` for the refused password and the lock.
10. **The upgrade is a start-up step**, not a migration: it reuses the one deletion routine (so an upgraded account
    loses exactly what any deleted account loses, OpenIddict's rows included) and runs whether or not migrations run at
    start.
11. **Refresh tokens are deleted, not only revoked**, when an account is deleted: they name the account.
12. **`DELETE /auth/account` counts against the `general` per-IP policy**, as `DELETE /auth/org` does; the lockout
    guards the password.
13. **`DELETE /api/me`** is the sample's "delete my data", without a permission; the sample calls Auth-Core only after
    its `204`.
14. **The sample gains a small `/team` screen** (members and "Remove"), because it had none and Decision 11 needs the
    confirmation somewhere a test can reach.
15. **The CLI hides personal spaces** (`not_found`), so the operator cannot invite into or delete one by mistake.

### Accepted for this slice (owner, 2026-04-08)

16. **A product's data of a personal space that moves to a company stays behind.** Decision 7 deletes the space in
    Auth-Core, but the product is not told (no deletion feed, Decision 8) and the person is not signed in on the
    invitation screen, so the sample cannot delete the notes first. The rows stay in the product's tables under an
    `org_id` no token will carry again (residual risk 48). The case is rare (Decision 12); a later deletion feed would
    close it.
17. **A wrong password after the product's data is gone.** Variant A deletes the product's data before Auth-Core checks
    the password; a wrong password then leaves an account whose data is already deleted. The person asked for the
    deletion, so nothing is lost against their will; the sample asks for the password on the same screen and says what
    happened on `wrong_password`.

### Amendment (owner, 2026-04-09)

Found while planning; the contract above already reads this way.

1. **`DELETE /auth/account` judges the token's `org_kind` first.** A token that says `personal`, while the database
   shows a company or nothing, is `403 permissions_changed`, so the frontend refreshes. `403 forbidden` is for a company
   member whose token does not say `personal`. The table and criterion 22 follow.
2. **`permissions_changed` is narrower.** A removed or deleted person's token gets it at `/auth/me` and at every
   company endpoint whose permission the token claims. An endpoint that needs a permission the token never claimed
   stays `403 forbidden`, as in spec 0005.
3. **The address lock is taken only where no company lock guards the account**: self-deletion, the invitation move and
   the start-up step. Removal and company deletion take none, never one per member, or a large company could not be
   deleted. Self-deletion reads the membership again under the lock.
4. **The deletion routine deletes queued mail requests before the pending sign-up.** The mail sender locks them in
   that order; the other order can deadlock.
5. **No order among audit rows of one transaction.** They are written in the same transaction; no equal time is
   claimed. The table says "and", not "then"; tests count the rows.
6. **A blank `password` is `400 invalid_request`**, as the body reader already does. The general rules, sign-up
   completion, invitation acceptance and `DELETE /auth/account` say so.
7. **A personal space's token is refused on every company endpoint**, `GET /auth/org` included: it needs no
   permission, so only an explicit check on the space catches it. Criterion 14 names it.
8. **"To verify during implementation"** lists what was settled, each with its answer. Only the removal list is open.
   One answer is worth knowing: the sign-up body is read in two phases, the kind first, so `403 signup_disabled` still
   comes before `400` for a bad `email`.

## Verification notes (for the local verifiers)

- Criteria 5 and 16 need the dispatcher run by hand (the `MailTestBase` pattern); criterion 25 a database prepared
  with accounts without a membership and a host started on it; criterion 27 parallel requests against PostgreSQL;
  criterion 28 a host with the manifest and settings of spec 0009.
- The e2e script and the Playwright spec need private sign-up on: `deploy/auth.yaml` and `samples/notes-api/auth.yaml`
  turn it on; the completion address is already set by the development settings and the sample's compose.
- Criterion 21 is checked table by table, OpenIddict's tokens and authorizations included.

## To verify during implementation

Settled while planning (the contract above already reads this way):

- **The start-up step's place**: after the migrations, the manifest and the sign-up address warning, before
  `DevUserSeeder`. The CLI returns before it builds a host, so the step never runs there.
- **A deleted refresh-token entry** gives `401 invalid_grant` through the existing handlers, as a revoked one does.
  Delete the tokens before their authorizations (there is no cascade between them).
- **Lock order**: company lock, then address lock; no path takes them the other way round. Removal and company
  deletion take no address lock (see "Locks").
- **Identity's delete** removes the account's logins, claims, stored tokens, mail links and membership by cascade, and
  sets `Invites.InvitedBy` to null.
- **The EF migration**: `Companies.Kind` and `PendingSignups.Kind` need the default `1` set in the model, or existing
  rows would get `0`; `PendingSignups.CompanyName` becomes nullable. A model change without its migration stops the
  host from starting, so both go in one task.
- **The mail dispatcher's two bulk statements** follow the switch rule of "Mails", item 1, and the simpler account rule
  of item 2; checked against every combination of pending kind, both switches and an account or none. The invitation
  composer reads the account's space kind.
- **`JsonObjectBody`** reads strings only and rejects the whole body at its first bad member. The sign-up request needs
  a reader that returns the members one by one (the body is read in two phases: the kind first, so that its switch is
  judged before `email` and `company_name`). The accept body needs a reader for a JSON boolean. Completion and
  acceptance can keep reading optional strings.

Still open:

- **The Amendment 3 code paths to remove**: `SignupAccount.MayStartCompany`, `has_account` and `HasAccount`, the
  password-free completion, `details.existing_account`, the dispatcher's branch for accounts without a company, the
  password replacement in `InviteAcceptance`, the sample's `keepsPassword` text, step 12 of `e2e-signup.sh` (it becomes:
  a removed member's address signs up as a new address), and the changelog item.

## As built (owner, 2026-04-18)

Recorded after implementation, the final review and local verification (plan 0010 with Tasks 1 to 18,
[acceptance map](../plans/0010-acceptance-map.md)). What entered this stage stays in it; this section records
it rather than rewriting the contract above. Where a sentence above and this section differ, this section is how
the software behaves.

**Contract as built.** As specified, with the Amendment of 2026-04-09, the one of 2026-04-13 (the deletion order and the
share lock of sign-in and refresh, now in "Locks") and these readings:

- **A sign-in or a refresh reads its account again under a share lock** (`FOR KEY SHARE`, held to the end of its
  transaction) before it writes a session, and the deletion reads the accounts it deletes `FOR UPDATE`. A login that
  races a deletion is answered, and recorded, as an unknown address; a refresh is `401 invalid_grant`; no session of a
  deleted account is left behind. So `POST /auth/refresh` now writes its tokens in a transaction, through the result a
  login uses. The owner closed this race rather than accept it; the cost is one lock per sign-in.
- **The deletion order.** The routine deletes the queued mail requests of the addresses (not invitations) and then their
  pending sign-ups, with no account row locked, because the mail sender locks a request and then the pending sign-up or
  the account row and the other order can deadlock. It then reads the accounts `FOR UPDATE` in order of id, and deletes
  the login streaks, the sessions (tokens before authorizations, deleted and not revoked), the accounts and the
  personal spaces, and stages one `account.deleted` row per account. It refuses to run without the caller's
  transaction, and throws, so that the caller rolls back, when it removes another number of accounts than it read. Any
  number of accounts costs the same number of statements.
- **`DELETE /auth/account` takes the address lock before it counts the attempt or checks the password.** The order as
  built: the token's `org_kind` against the database, the body, the lock, the membership again, the lockout and the
  counted attempt, the password, the deletion in the same transaction. An account that moved to a company, or was
  deleted, while the request waited is `403 permissions_changed` with nothing counted. The password is checked without
  side effects: the hash is read without tracking and verified with `VerifyHashedPassword`, so a hash in an older format
  is accepted and not rewritten (no `UPDATE` on a row about to be deleted). The lock and the transaction are held across
  one hash; the lockout and the `general` limit bound that cost. The login streak is written on a connection of its own,
  so the transaction never holds a streak row while it waits for the address lock.
- **A company membership wins over a personal one** when an account holds both, a state only a direct change of the
  database leaves: `MembershipReader` (the token says `organization`, and `DELETE /auth/account` is `403 forbidden`),
  `InviteAcceptance` (`409 already_member`, nothing moves) and `AccountDeletion` (the `account.deleted` row names the
  company; the personal space goes with the account).
- **An acceptance that meets a removal between its two reads** (it found the account, and the removal took the account
  and its membership before the acceptance read the space) reads the account again `FOR UPDATE`; when the row is gone
  it takes the path of an address with no account: a password is needed and a new account is made. It never answers
  `500`.
- **An account with no membership at all** (the same kind of state; the start-up step deletes it at the next start) joins
  at an acceptance with the password it has, without `leave_private_account`. That is how "the path where an existing
  account without a company sets a new password is gone" reads for the one case that can still occur. The account that
  joins, and the one that moves, get a new security stamp, so a login that raced the acceptance cannot refresh into the
  company.
- **The start-up step** deletes each account without a membership in a transaction of its own, under the address lock,
  and reads the membership again under it. When a deletion fails, the step logs the account's id at the level Critical
  and throws: the service does not start, and the accounts it had already deleted stay deleted. Two replicas starting
  together delete each account once.
- **The sample waits at most 10 seconds for `GET /auth/options`.** A slower call reads as `invite-only` and
  `private_signup` off: the sign-up screen then shows the organization form, and the sign-in screen offers no "Create an
  account". A hung call never leaves the sign-up screen empty.
- **The shipped manifests** (`deploy/auth.yaml`, `samples/notes-api/auth.yaml`) turn private sign-up on. A host that
  copies them needs the completion address, or sign-up stays off and health says `Degraded`; the deployment guide and
  the
  changelog say so and say how to keep each kind off.
- **The `signup.completed` row of a private sign-up** names the personal space (`org_id`) and `private` (`org_name`),
  as its `login.succeeded` row does; there is no `org.*` row.
- **"Settled in this spec"** (1 to 15) holds as written. The Python package keeps its version; its release is part of
  `v0.2.0`.

**Residual risks accepted** (threat model, risks 48 to 51):

- **48**: a product's data in a personal space that moves to a company by invitation stays in the product's tables
  (Decision 16).
- **49**: an admin who removes a member, or deletes a company, deletes the person's account (Decision 9).
- **50**: a deleted person's address and client addresses stay in the audit rows for their 90 days, and in backups
  until they are rotated (Decision 10).
- **51**: a wrong password, or a lockout, at `DELETE /auth/account` after the product has deleted its own data leaves an
  account without its data. The person asked for the deletion and can try again (Decision 17). The list in the spec
  names
  48 to 50; the threat model names this one too, since an accepted risk belongs in its numbered list.

**Plan departures** (the real files win over the plan's text, as its own notes say):

- **Tasks 6 and 7, the deletion routine.** The plan calls it "set-based and lock-free". The routine built takes the row
  lock of the accounts and deletes the mail requests and pending sign-ups before it; sign-in and refresh gained the
  share
  lock, and refresh its transaction (the Amendment of 2026-04-13; "Decisions taken in this plan", item 4, is edited to
  say so).
- **Task 13, `DELETE /auth/account`.** The plan took the address lock after the password check and used
  `CheckPasswordAsync`; the code takes the lock first and verifies the hash without side effects (above).
- **Task 15, the options call** gained its 10-second wait in a fix round.
- **Task 17.** The Playwright test of the sign-up choice clicks the label of "Private person" and checks the size of
  each radio button, where the plan called `.check()` on the radio; the closing sentence of the header of
  `scripts/e2e-signup.sh` drops "and 24's other halves" (criterion 24 of spec 0009 is gone with step 12).
- **Task 18, the documents.** The plan said the acceptance map was "checked, not written"; two names in it that no task
  built were corrected to the real tests. The map of spec 0009 marks superseded every row that cited a deleted comeback
  test, not only rows 8, 19 and 24 that were noted first. The threat model, `docs/operations/backup.md` (its list of
  audit kinds), the changelog (the 0009 bullets) and the Angular guide (the 10-second wait) gained sentences the plan
  did not list.
- **Final review.** The organization-only `PendingSignups.UpsertAsync` that the plan kept in Task 9 has no caller left
  and is gone. The acceptance map of spec 0005 (rows 5 and 13 and the seed row) names the tests that exist now and marks
  the superseded behaviour.
- **Risks in the plan's text.** Its lines that say "items 1 to 50" and "residual risks 48 to 50" are the spec's list;
  the threat model says 1 to 51.
- **Test infrastructure (a note, not contract).** The full .NET suite ran the test container's PostgreSQL out of
  `/dev/shm` (64 MB), on `main` too: 5 of 1741 tests failed there, 65 to 70 of 1968 here. The test container now has
  512 MB of shared memory, each test's database is created and migrated before its host starts and dropped when the test
  ends, and the connect and command timeouts are 30 and 60 seconds. One test,
  `PersistenceTests.A_host_started_on_an_empty_database_migrates_it_and_answers_health`, still starts a host on an empty
  database. The whole suite passes in one run.

**Verified** on 2026-04-18 (final review and the re-review of its fix wave; three verifiers, two rounds); the log is in
the acceptance map. Live: all e2e scripts of specs 0001 to 0010 including `scripts/e2e-private.sh` (9 steps),
`scripts/e2e-notes.sh` (7 of 7) and the production compose (`scripts/e2e-prod.sh`, 6 of 6), and the sample's Playwright
tests (49 of 49 in Chromium and in WebKit, `10-private.spec.ts` among them). The whole .NET suite, 1968 tests, passes in
one run; the Python package (224), the sample backend (103) and the sample frontend (591) pass. The secret scan is clean
(the history and the files not committed yet).
