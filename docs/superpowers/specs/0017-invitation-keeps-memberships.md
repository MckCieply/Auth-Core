# Spec 0017 — Accepting an invitation keeps the other memberships

- **Status:** Draft (design approved by the owner, 2026-11-21; written spec awaiting review)
- **Date:** 2026-11-21
- **Author:** Alex
- **Milestone:** the third of three slices that let one person work in several companies: 0015 (the base), 0016 (a
  signed-in person creates another company), and this one. It is built once 0015 is merged and reads 0015's contract as
  the code it changes.
- **Context:** specs [0005](0005-tenancy-and-rbac.md) (invitations, their preview and acceptance, the effects of an
  acceptance), [0010](0010-private-accounts.md) (private accounts, the invitation of a private account and its move),
  [0011](0011-google-and-microsoft-sign-in.md) (the acceptance through a provider) and
  [0015](0015-multiple-companies.md) (several memberships, the session's company, `LastUsedAt`, `GET /auth/orgs`,
  `POST /auth/switch`, the removal rule). This spec changes parts of 0005, 0010, 0011 and 0015; each change is named
  where it is made.

## Goal

A person who already has an account accepts an invitation to another company and keeps everything the account has: its
other companies, its personal space, its password, its linked providers and its signed-in devices. The account gains one
membership. Today a member of another company is refused (`409 already_member`), a private account has to give up its
personal space, and every acceptance signs the person out everywhere.

The first product that needs it treats a company as a shared project: a person is invited to the projects of others and
keeps their own. Accepting is one click on the invitation screen; the next sign-in lands in the new company, and a browser
that is already signed in moves to it at once.

Concretely, this is met when this sequence works against the development stack of 0015, where `dev@acme.test` is a
member of `Acme` and `Beta`, and the admin of a third company `Gamma` (made with the operator command `create-org`)
invites that address:

```
curl -i -c jar -X POST .../auth/login -d '{"email":"dev@acme.test","password":"<p>"}'           # -> 200, org_id = Acme
# Gamma's admin invites dev@acme.test; the mail says the account joins Gamma and keeps what it has
curl -i -X POST .../auth/invites/preview -d '{"token":"<t>"}'
#   -> 200 {"org_name":"Gamma","email":"dev@acme.test","role":"user","has_private_account":false,"has_account":true,"org_id":"<gamma>"}
curl -i -X POST .../auth/invites/accept -d '{"token":"<t>"}'                                      # -> 204
curl -i -b jar -c jar -X POST .../auth/refresh                                                    # -> 200, org_id = Acme (the device stays)
curl -i -H 'Authorization: Bearer <a>' .../auth/orgs                                              # -> Acme (current), Beta, Gamma
curl -i -b jar -c jar -X POST .../auth/switch -d '{"org_id":"<gamma>"}'                           # -> 200, org_id = Gamma
curl -i -c jar2 -X POST .../auth/login -d '{"email":"dev@acme.test","password":"<p>"}'          # -> 200, org_id = Gamma
```

## In scope

- `POST /auth/invites/preview` and `POST /auth/invites/accept` for an address that has an account of any kind: the
  account joins with the link alone and keeps its other memberships.
- A private account keeps its personal space; `leave_private_account: true` still moves it (deletes the space), so that
  screens built for spec 0010 keep working.
- An acceptance ends no session; the joined company becomes the company used last.
- The acceptance through a provider (spec 0011) for an existing account whose identity is already its login.
- The invitation mail for an address that has an account.
- The locks between an acceptance and a removal that could delete the account.
- OpenAPI, `samples/notes-web` (`/invite`, `/provider-return`), the e2e scripts, documents, changelog.

## Out of scope

- A signed-in person creating another company (spec 0016).
- Who may delete their own account while they have a personal space and a company (`DELETE /auth/account`): spec 0016
  decides it (`403 company_member`). This spec creates such accounts and cites the rule (see "Deleting one's own
  account").
- Linking a provider without the password (spec 0011, Decision 7, stays), and linking from a signed-in session
  (slice 0012).
- Inviting a person by anything but their address; accepting an invitation for another account than the address's.
- Sending: `POST /auth/org/invites` answers as today (`202` for an address of any account, `409 already_in_org` for a
  member of the same company).
- Leaving a company on one's own.
- Any change to the Python package's code (`clients/python`) or to `samples/notes-api`.

## Contract

### General rules

Request bodies, the charset guard, the error shape and the cache headers are those of specs 0001, 0005, 0008 and 0010,
unchanged. "The address's account" is the account whose address is the invited one spelled the same up to ASCII case
(`EmailInput.IsSameAddress`); an account under a look-alike spelling makes the invitation `400 invalid_token`, as today.
Every refusal below changes nothing, the invitation (and, on the provider path, the ticket) included.

### Memberships (changes spec 0005, Effects; spec 0010, Invitations; spec 0015, Memberships)

- An invitation is the way into a company for every address: one with no account (as today), one whose account is a
  member of other companies, one whose account has a personal space, or both.
- Accepting **adds one membership** (the invited company, the invited role, `JoinedAt` the time of the acceptance). No
  other membership of the account changes, except at the move (below).
- Spec 0015's sentence "The paths that make memberships do not change in this slice" no longer holds for the
  invitation paths; sign-up still refuses an address with an account.
- `409 already_member` keeps its name and narrows its meaning: **the address's account is already a member of the
  invited company**. Through the API it does not arise: sending refuses a member (`already_in_org`), and the only way
  into the company is its own invitation, of which an address has one at a time. The check stays as a guard against a
  membership made outside the API (the operator, a direct change of the database); tests make that membership directly.

### `LastUsedAt` at an acceptance (changes spec 0015)

Spec 0015 defines `LastUsedAt` as "the last time a session started in or switched to that space". It now reads: the
last time a session started in or switched to that space, **or the time the invitation that made the membership was
accepted**. Every acceptance, the one that makes a new account included, writes the new membership's `LastUsedAt` with
its `JoinedAt`, in its transaction (spec 0015 leaves a new membership's `LastUsedAt` empty until a sign-in writes it;
for an acceptance it no longer does). On the provider path the sign-in in the same transaction then writes it again, as
every sign-in does, with a time not earlier than `JoinedAt`. By the order of spec 0015 ("Which space a sign-in
takes"), the next sign-in of the account on any device takes the company just joined, until the person works in
another space.

### `POST /auth/invites/preview` — `{"token"}` (changes spec 0005 and spec 0010)

`200`:

```json
{ "org_name": "Gamma", "email": "dev@acme.test", "role": "user",
  "has_private_account": false, "has_account": true, "org_id": "…" }
```

- `org_name`, `email` and `role` as today. `has_private_account` is `true` when the address's account has a personal
  space, **whether or not it is also a member of a company** (today a company membership wins and makes it `false`; no
  account had both before slices 0016 and 0017, so no answer seen so far changes). It tells an old screen that the move
  is possible: the move deletes the personal space and keeps the companies.
- **`has_account`** (new): `true` when the address has an account, of any kind; `false` otherwise. A screen asks for a
  password only when it is `false`; a screen reading an older server, which sends no `has_account`, treats anything but
  `true` as `false`. The name is not the `has_account` of spec 0009's sign-up answer, which spec 0010 removed; it
  belongs to the preview only.
- **`org_id`** (new): the id of the invited company, which a screen passes to `POST /auth/switch` after the acceptance.
- `409 already_member` only when the address's account is a member of the invited company. `400 invalid_token` as
  today. The preview never uses the invitation.

### `POST /auth/invites/accept` — `{"token", "password"?, "leave_private_account"?}` (changes spec 0005 and spec 0010)

The checks, in this order:

1. The body breaks the rules of spec 0010, step 1 (not a JSON object; `token` missing or not a string; a `password` that
   is present and not a string, blank, or holds NUL; a `leave_private_account` that is present and not a JSON boolean):
   `400 invalid_request`.
2. Unknown, used, expired, cancelled, replaced or look-alike: `400 invalid_token`.
3. The address's account is a member of the invited company: `409 already_member`; the invitation stays usable.
4. The address has an account and `leave_private_account` is `true` and the account has a personal space: **the move**
   (below). `204`.
5. The address has an account otherwise: **the account joins**. A `password` sent is not used and not judged (as
   today for a private account), and a `leave_private_account` of `true` is ignored when the account has no personal
   space. `204`.
6. No account: no `password` is `400 invalid_request`; a weak one is `400 weak_password` with the broken rules; the token
   stays usable. Otherwise the account is made with that password, as today (its address's login streak and mail
   links end, `AccountSessions.EndAllAsync` as today, since an account that did not exist has no sessions to keep),
   with the membership's `LastUsedAt` set as above. `204`.

`409 private_account_exists` is no longer answered: a private account without `leave_private_account` joins at step 5.
An account with no membership at all (a state only a direct change of the database leaves) joins at step 5, as today.

**What a join does**, in one transaction under the locks below: the invitation is used up; the membership is added with
`JoinedAt` and `LastUsedAt` set to the time of the acceptance; an unconfirmed email is confirmed (the link came through
the mailbox); `invite.accepted` is written. **Nothing else changes**: the account's other memberships, its personal
space, its password, its linked providers, its security stamp, its sessions, its mail links and its login streak (a
lockout included) stay as they are. This changes spec 0005 (Effects: every session, mail link and the login streak end
at an acceptance) and spec 0010 (Invitations, steps 5 and 6) for an existing account; an acceptance that makes a new
account (step 6) is unchanged.

**The move** (`leave_private_account: true`, kept for screens built for spec 0010): a join, plus, in the same
transaction, the deletion of the personal space, its role row and its membership, as spec 0010 step 5 deletes them.
What the product keeps under that space's id stays behind (spec 0010, Accepted 16; residual risk 48). Sessions whose
company was the personal space get the uniform `401 invalid_grant` at their next refresh or switch (spec 0015: their
membership is gone); every other session goes on. `invite.accepted` carries `details.left_private_account` `true` and
`details.personal_org_id`, as today.

### The acceptance through a provider (changes spec 0011, exchange, purpose `invite`)

`POST /auth/providers/exchange` with a ticket of purpose `invite`. The checks, in this order:

1. The invitation (found by the hash the ticket holds) is unknown, used, expired, cancelled or replaced, or its address
   may stand for another: `400 invalid_token`, as today.
2. The ticket's address is missing, unverified, or not the invited address: `403 address_mismatch`, as today.
3. An account exists under a look-alike spelling of the address (not spelled the same up to ASCII case):
   `400 invalid_token`, as today, after `address_mismatch` (the test
   `A_mismatching_address_is_403_before_an_account_under_a_look_alike_is_judged` keeps that order).
4. The address's account is a member of the invited company: `409 already_member`.
5. The identity is a login of an account other than the address's account (or the address has no account and the
   identity is a login of some account): `409 provider_already_linked`, as today.
6. The address has an account and the identity is not one of its logins: `409 {"error":"already_registered"}`. The
   identity is not linked (spec 0011, Decision 7: linking needs the password once); the screen sends the person to the
   invitation's own "Accept" button. The code already exists; at `/auth/providers/finish` and at sign-up it means "an
   account with this address exists", and at this exchange it has so far meant only an account under a look-alike
   spelling. It is now also this case, for purpose `invite`.
7. The address has an account and the identity is one of its logins: the account **joins** as at
   `POST /auth/invites/accept` step 5 (never the move: the exchange has no `leave_private_account`), the ticket is used
   up, and the person is signed in **to the invited company**: `login.succeeded` with `details.provider`,
   `200 {"status":"authenticated","access_token"}` and the refresh cookie, as spec 0011 answers. The session's company
   is the invited company; the sign-in takes that membership directly, not through the order of spec 0015, and writes
   its `LastUsedAt` as every sign-in does. Today `ProviderExchangeEndpoint.AcceptAsync` reads the sign-in's space
   (`MembershipReader.ReadAsync`) and checks it is the invited company; with several memberships that read may pick
   another one, so the exchange reads the membership in the invited company instead (spec 0015, "To verify during
   implementation", splits `MembershipReader.ReadAsync` into the sign-in's space and the membership in one company).
8. No account: as today (the account is made with no password, the identity its login, a member with the invited role,
   signed in).

In steps 3 to 6 the invitation and the ticket stay usable. `409 private_account_exists` is no longer answered here
either. This changes spec 0011 (exchange, purpose `invite`, step 3; Settled 15, which kept private accounts off this
path because the move needed its warning) and spec 0015 (its sentence that a flow which joins a space has that space as
the account's only membership).

### Sessions

An acceptance ends no session, on either path. A device signed in before the acceptance keeps working in its session's
company; `GET /auth/orgs` lists the new company at once, with `current` `false`. The only sessions an acceptance stops are
those of the move's deleted personal space, at their next refresh, by the rule of spec 0015.

One exception, by spec 0015's rule for old tokens: a refresh token issued before spec 0015's version carries no
company, and its session's company is "the space a sign-in would take". After an acceptance that is the new company,
so such a session moves into it at its next refresh (and its next refresh token carries it). It stays signed in, as
Decision 3 wants; only the company it works in changes, to one the person is a member of.

### Locks (changes spec 0010, Locks, and spec 0015, Removal)

The order stays: the company lock (of the invited company), the address lock, then the account row. Spec 0010 "Locks"
says that a removal and a company deletion need no address lock because the company lock keeps out every flow that
could change the accounts; with accounts in several companies that no longer holds (an acceptance into another company
holds another company's lock), and the rules below take its place. Removal and company deletion still take no address
lock. Under the locks:

- The acceptance **locks the address's account row** (`FOR UPDATE`) and reads it afresh **before** it reads the
  account's memberships, whenever the address has an account (today it does so only for an account with no space).
  When the row is gone by then (a removal deleted the account between the lookup and the lock), the acceptance takes the
  path of an address with no account, as spec 0010 "As built" reads.
- **A removal of a member and a company deletion** (spec 0015) first delete the membership (or memberships) and pick the
  accounts that seem to have no membership left, as spec 0015 says. They hand those to the deletion routine
  (`AccountDeletion.DeleteAsync`), which keeps its order: it deletes the addresses' queued mail requests (not
  invitations) and pending sign-ups first, with no account row locked (the mail sender locks a mail request and then the
  account row, so the other order can deadlock), then reads the accounts `FOR UPDATE`. **Under that row lock it counts
  each account's memberships again**, for the reasons `member_removed` and `org_deleted`, and leaves out every account
  that has one: such an account is kept, with no `account.deleted` row, and the routine's check of "read as many as
  deleted" counts only the accounts it still deletes. No removal path takes an account row lock before the routine
  does.
- So an acceptance that commits first keeps the account (the removal's second count sees the new membership), and a
  removal that commits first deletes it (the acceptance, waiting on the account row, finds it gone and makes a new
  account). No acceptance adds a membership to an account that is being deleted, and no removal deletes an account
  together with a membership it never saw.
- In the first order, the kept account has lost its queued reset and verification mails and its pending sign-up (there
  is none for an address with an account), which the routine deleted before the row lock. The person asks for a new
  mail; this happens only when an acceptance and the removal of the account's last other membership meet.
- Both acceptance paths (the link and the provider) hold the address lock while they add the membership, which is what
  spec 0016's self-deletion rule asks of a path that gives a private account a company (see "Deleting one's own
  account").
- Two acceptances of one address into two companies wait for each other at the address lock and both join (today the
  later one is `already_member`).
- A sign-in or a switch takes no company lock, as in spec 0015, and reads its chosen membership again under a row lock.
  A join only inserts and never meets it. The move deletes the personal membership: a sign-in or switch into the
  personal space that holds the row lock first makes the move wait and its session then gets `401 invalid_grant` at the
  next refresh; one that comes after the move does not find the membership, and answers as spec 0015 says for a
  membership that vanished (a switch `404 not_found`).
- The acceptance no longer deletes mail links or login streaks of an existing account, so its account row lock never
  waits on the mail sender's locks in the other order.

### Mails (changes spec 0010, Invitations: the mail)

When the invitation mail is composed and the invited address has an account (of any kind, spelled the same up to ASCII
case), the intro's last sentence asks for no password and one paragraph is added (`{0}` the application, `{1}` the
company, HTML-encoded as before; the mail names no role, as since `0.1.2`):

- EN intro: "You have been invited to join {1} on {0}. Use the link below to accept the invitation."
- EN paragraph: "You already have an account on {0} with this email address. If you accept, your account also joins
  {1} and keeps everything it has now, with the same email address and the same way of signing in."
- PL intro: "Zaproszono Cię do firmy {1} w {0}. Użyj poniższego linku, aby przyjąć zaproszenie."
- PL paragraph: "Masz już konto w {0} z tym adresem e-mail. Jeśli przyjmiesz zaproszenie, Twoje konto dołączy także do
  firmy {1} i zachowa wszystko, co ma teraz, z tym samym adresem e-mail i tym samym sposobem logowania."

The paragraph of spec 0010 about the deletion of the private account's space is removed: no mail announces the move,
which only a screen built for spec 0010 offers, with its own warning. Any address without an account gets the mail of
spec 0005, as today. The variant is decided when the mail is composed; the preview decides again when the link is used,
in both directions: a mail sent to an address with no account says to choose a password, and if the person has made
an account before using the link, the screen shows the button of an existing account; a mail sent to an account
whose account was deleted before the link is used asks for no password, and the screen shows the password form.

The private-account variant of the invitation mail (`MailComposer.Compose`'s `privateAccount` flag for
`MailKind.Invitation`, `MailTexts.InvitationToPrivateAccount`) is replaced by this one; the flag gets a name of its
own (for example `accountExists`) so it is not read as the private sign-up's flag. The intros are today's texts.

### Deleting one's own account

This spec does not change who may call `DELETE /auth/account`; spec 0016 sets the rule (owner, 2026-11-21) for both
slices. A private person who accepts an invitation now has a personal space and a company, and by spec 0016 such an
account may not delete itself: `DELETE /auth/account` answers `403 {"error":"company_member"}` until the person has
left every company (the company deletes itself or its admin removes them). An account with only its personal space
deletes itself as in spec 0010.

The race between the two is closed by the address lock: an acceptance holds the invited address's lock (see "Locks")
while it adds the membership, and a self-deletion reads the account's memberships again under the same lock. So a
self-deletion that comes second sees the new company membership and answers `403 company_member`; an acceptance that
comes second finds no account and makes a new one.

### Audit events

No new kind. `invite.accepted` is written for every acceptance, as today: `subject_user_id`, `subject_email`, `org_id`
and `org_name` (the invited company), `target_id` (the invitation), `details.role`; `details.provider` on the provider
path; `details.left_private_account` and `details.personal_org_id` at the move. The provider path's sign-in writes
`login.succeeded` with `details.provider` and the invited company in `org_id`. The documents that count the kinds do not
change for this slice.

### Limits

Unchanged: the preview and the acceptance count against the policies they count against today, and the exchange
against its own.

### OpenAPI

- `POST /auth/invites/preview`: the response gains `has_account` and `org_id`; `409 already_member` is described as "a
  member of this company".
- `POST /auth/invites/accept`: an existing account joins with the token alone and keeps its other memberships;
  `leave_private_account: true` moves a private account (its personal space is deleted); `409 private_account_exists` is
  removed; no session ends.
- `POST /auth/providers/exchange`: for purpose `invite`, an existing account whose identity is its login joins and is
  signed in; the description of `409 already_registered` gains the case of purpose `invite` (an existing account whose
  identity is not its login; the code is already listed); `409 private_account_exists` is removed from the description
  and from the listed errors.
- The texts live in `src/Auth.Server/Api/InviteEndpoints.cs` and `src/Auth.Server/Api/TenancyEndpoints.cs` (preview and
  accept: today they name `already_member` for any company and the move) and in `src/Auth.Server/Api/ProviderRoutes.cs`
  (the exchange's description and its `ProducesError` list). The response schema comes from `InvitePreviewResponse`
  (`src/Auth.Server/Tenancy/Contracts.cs`), which gains `HasAccount` and `OrgId` at the end, as the service type
  `InvitePreview` (`InviteAcceptance.cs`) does.
- The counts pinned by `OpenApiHardeningTests` do not change (no operation is added).

### Python package (`clients/python`)

No change.

### Sample frontend (`samples/notes-web`)

- **`/invite`**, after the preview:
  - `has_account` `false`: as today (the provider buttons, then the password form).
  - `has_account` `true`: the line "You already have an account with {email}. Accepting adds {company} to it; everything
    else stays as it is.", the button "Accept the invitation" (it sends `{"token"}` only), and below it the provider
    buttons ("Accept with Google", "Accept with Microsoft"). No password field.
  - The private-account warning and the button "Move me to the company and delete my private account" are removed; the
    sample never sends `leave_private_account`.
  - After `204` with `has_account` `true`, the sample moves an open session of the same person into the new company.
    It first restores its session as every page does (`auth.service`'s refresh at start). When it has a session and
    `GET /auth/me` names the preview's address (equal up to ASCII case), it calls `POST /auth/switch` with the
    preview's `org_id`; on `200` it keeps the token as the session (as after a switch in spec 0015), reads
    `GET /auth/me` and opens the notes of the new company. When it has no session, the session is another person's
    (that person might also be a member of the invited company, and must not be moved), or the switch answers `401` or
    `404`, it opens `/login` with the address in the navigation state, as today. With `has_account` `false` it opens
    `/login` as today.
  - `account-api.ts` reads `has_account` and `org_id` from the preview (`has_account` is `true` only when the answer
    says exactly `true`, as `has_private_account` is read today) and `acceptInvite` loses its `leavePrivateAccount`
    argument; `account-api.spec.ts` follows.
  - `already_member` shows "You are already a member of this company. Sign in to open it."
- **`/provider-return`**, purpose `invite`: `already_registered` shows "You already have an account with this address.
  Open the invitation link again, choose Accept the invitation, then sign in." (purpose `sign_in` keeps today's
  sentence); `already_member` shows the sentence of `/invite`; the sentence for `private_account_exists` is removed. The
  purpose is today a local variable of `handle()`; the page keeps it in a field, so the message shown after "try again"
  also knows it.
- `auth.service.ts` keeps `private_account_exists` among the codes it knows (an older server still sends it); the page
  shows the generic error for it.
- Texts are English, as in spec 0007. Unit tests for each branch above, and Playwright: an account with a company
  accepts an invitation to another company with the button while signed in and lands in the new company's notes, the
  picker of spec 0015 lists both; a private account accepts and keeps its private notes, reachable through the picker.
  The existing invitation specs (`04-invite`, `10-private`) and `providers-on-screens.spec.ts` follow the new screens.

### e2e scripts

- `scripts/e2e-tenancy.sh` step 8: the preview for a member of another company is `200` with `has_account` `true`; the
  acceptance is `204`; the person is a member of both; their earlier session still refreshes in its company; their next
  login lands in the invited company. Because that person now stays a member of the inviting company, step 10's
  expectation of the company's member count changes with it, and the header comment and step 8's pass message
  ("only the acceptance screen says already_member") are rewritten.
- `scripts/e2e-private.sh` (its header comment, the `E2E_PARAGRAPH` mail check and the "personal space is gone" checks
  follow): step 5 becomes a private account that accepts without `leave_private_account` (`204`, its
  space kept, its sign-up cookie still refreshes in the personal space, the mail holds the paragraph of this spec, the
  next login lands in the company), and a second private account that moves with `leave_private_account: true` (as step
  5 checks today, except that only its personal sessions stop). Step 8 removes the account that moved (it is deleted,
  `member_removed`), and the account that kept its space is removed from the company and kept. The second private
  account needs its own sign-up and its own waits for the mail limit.
- `scripts/e2e-providers.sh` step 11: an invitation of a private account's address accepted with an identity that is
  not its login is `409 already_registered`; `already_member` only for a member of the inviting company; and an
  existing account whose identity is its login accepts and is signed in to the invited company (the account with a
  linked Microsoft identity serves; another invitation to its address waits for the mail limit through the existing
  `429` retry). Step 12's count of `login.succeeded` rows with `details.provider` grows by that sign-in.
- `scripts/e2e-orgs.sh` (spec 0015) runs the sequence of the Goal. The development seed of spec 0015 makes only `Acme`
  and `Beta`; the script makes `Gamma` and its admin with the operator command `create-org` and invites the address
  from there, as `e2e-tenancy.sh` makes its companies.

### Documents

- `docs/integration/angular.md`: the invitation screen for an existing account (`has_account`, no password, the switch
  to `org_id` after `204`), and `already_registered` on the provider path.
- `docs/integration/python-fastapi.md`: an invitation can add a company to a person who already has one; a product's API
  sees it as a new `org_id` in that person's tokens.
- `docs/security/threat-model.md`: the changed invitation and the residual risks below.
- `CHANGELOG.md` (Unreleased): invitations keep the other memberships; `has_account` and `org_id`; no session ends at an
  acceptance; `private_account_exists` no longer answered; the provider path for existing accounts.
- `docs/design.md`: the note on slices 0015–0017 names this one done.

**Residual risks**, added to the threat model's list after the last one at the time this slice is built (spec 0015 adds
62 to 65):

- **The link alone adds an existing account to a company.** Whoever reads the person's mailbox can make the account a
  member of the inviting company. It gives them no sign-in (that still needs the password or a linked provider), and the
  person sees the company in their list. The move of spec 0010 already worked on the link alone.
- **An acceptance no longer ends sessions, mail links or the login streak.** A session stolen before an acceptance
  keeps working until it expires, the person changes the password, or the person is removed; a reset or verification
  link sent before it stays usable until it expires; a lockout runs its course. An acceptance was never meant as a way
  to end them.
- **The preview names the company's id** to whoever holds the link, as it names the company. The id grants nothing
  without a membership.

### Compatibility for screens built before this slice

- A screen built for spec 0010 shows the move for `has_private_account` `true` and sends `leave_private_account: true`:
  the space is deleted, as its warning says.
- A screen built for spec 0005 shows a member of another company the password form, since the preview is now `200` with
  `has_private_account` `false`: the password sent is not used, the account joins, and the screen's text about setting a
  password is wrong for that person. The screen keeps working.
- A client that treats `409 already_member` as "a member of some company" now sees it only for the invited company.
- No field is removed and no status of a success changes; the new fields are additions.

## Acceptance criteria (Done when)

Preview

1. The preview answers `has_account` `true` for an address with an account (a company member, a private account, both,
   or an account with no membership) and `false` for an address without one; `org_id` is the invited company's id.
2. `has_private_account` is `true` for an account with a personal space, also when it is a member of a company too.
3. The preview is `409 already_member` only for a member of the invited company (a membership the test makes directly);
   a member of another company gets `200`.

Acceptance with the link

4. A member of company A accepts an invitation of company B with `{"token"}`: `204`; the account is a member of A and
   B with its roles; its password, its logins and its security stamp are unchanged; a refresh of its earlier session
   (in A) answers `200` with `org_id` A; `invite.accepted` is written.
5. A private account accepts without `leave_private_account`: `204`; its personal space, its role row and its
   membership stay; its personal session still refreshes; it is a member of the company.
6. A private account accepts with `leave_private_account: true`: `204`; its personal space, role row and membership are
   gone; its personal session's refresh is `401 invalid_grant`; a session of the same account in another company still
   refreshes; `invite.accepted` holds `left_private_account` `true` and the old space's id.
7. `leave_private_account: true` for an account without a personal space is ignored (the account joins);
   `leave_private_account` that is not a boolean is `400 invalid_request`, as today.
8. A `password` sent with the acceptance of an existing account, weak or not, is not used: the old password still signs
   in, the sent one does not, and no `weak_password` is answered.
9. An acceptance by a member of the invited company (a membership the test makes directly) is `409 already_member` and
   changes nothing; the invitation still works afterwards.
10. An acceptance ends no session and leaves the account's mail links and its login streak (a lockout included) as they
    were; an unconfirmed email becomes confirmed.
11. An acceptance with the link, for an existing account and for a new one, writes the new membership's `LastUsedAt`
    equal to its `JoinedAt`; through a provider it is not earlier than `JoinedAt`. The next login of the account takes
    the invited company, also when another membership had a `LastUsedAt` before the acceptance.
12. An address without an account: as before (a password is needed, `weak_password`, the account is made).
13. `409 private_account_exists` is answered by neither acceptance.

Acceptance through a provider

14. An existing account whose identity is its login accepts through the exchange: `200` with tokens whose `org_id` is
    the invited company; the account keeps its other memberships and sessions; `invite.accepted` and `login.succeeded`
    carry `details.provider`; the invitation and the ticket are used up.
15. An existing account (a company member or a private account) whose identity is not its login: `409
    already_registered`; the invitation and the ticket stay usable; no login is linked.
16. An identity that is another account's login: `409 provider_already_linked`; a member of the invited company: `409
    already_member`; in each the invitation and the ticket stay usable.
17. An address without an account: as before.

Locks and races (parallel requests against PostgreSQL)

18. A removal that would delete the account (its last membership) and an acceptance of the same account into another
    company, run together: either the acceptance commits first and the account stays with the new membership (the
    removal writes `member.removed` and no `account.deleted`, and `remove-member` says the account is kept), or the
    removal commits first and the acceptance makes a new account (it needs a password and answers
    `400 invalid_request` without one). The same holds for a company deletion. Never a `500`, never a deadlock, never an
    account deleted with the new membership.
19. Two invitations of one address from two companies accepted at once: both `204`, and the account is a member of both.
20. A self-deletion of a private account (`DELETE /auth/account`) and an acceptance of an invitation for its address,
    run together: either the acceptance commits first and the self-deletion answers `403 company_member` (spec 0016) with
    nothing deleted, or the self-deletion commits first and the acceptance makes a new account. Never a `500`.

Mail, sample, documents

21. The invitation mail to an address with an account (company member, private, or both) has the intro and paragraph of
    this spec in EN and PL; the mail to an address without one is unchanged; no mail holds the paragraph of spec 0010.
22. OpenAPI describes the new preview fields and the changed answers; `OpenApiHardeningTests` counts are unchanged.
23. `samples/notes-web` unit tests and Playwright specs of "Sample frontend" pass; the existing Playwright specs pass.
24. The e2e scripts of "e2e scripts" pass against the development stack.
25. The documents of "Documents" are updated; the threat model lists the three risks.

## Decisions (owner, 2026-11-21)

1. **An existing account joins with the link alone.** The screen shows the company and one "Accept" button; no
   password. The link proves the mailbox, as the move of spec 0010 already does.
2. **A screen built for spec 0010 keeps working.** `leave_private_account: true` still deletes the personal space, so
   that screen's warning stays true. Without the field (every screen from now on) the space stays and the person joins.
   What the move no longer does is end every session, mail link and the login streak (Decision 3): only the sessions
   in the deleted space stop.
3. **An acceptance signs no device out.** The new company is added; devices keep working where they work; the company
   appears in the list of companies.
4. **The person lands in the new company.** The acceptance counts as the company used last, so the next sign-in lands
   there; a browser already signed in is switched to it at once by the screen. For that the preview gives the company's
   id (`org_id`).
5. **Google or Microsoft accepts for an existing account when that identity is already its login**, and signs the
   person in to the new company. An identity that is not linked gets a refusal that sends the person to the "Accept"
   button; linking without the password stays impossible (spec 0011, Decision 7).
6. **Scope and numbering:** this spec is 0017, the third of the slices the owner split (0015 base, 0016 create a company
   while signed in, 0017 this); every API change is additive and backward compatible, because a product integrates the
   current `main`.
7. **Deleting one's own account** (decided in spec 0016, binds this spec): only an account whose only membership is its
   personal space deletes itself; with a personal space and any company it is `403 company_member`. A path that gives
   such an account a company membership, this acceptance included, holds the address lock, and the self-deletion
   re-reads the memberships under it.

### Settled in this spec

- **`409 already_registered`** for an existing account whose identity is not its login on the provider path: an existing
  code, which at the finish and at sign-up means "an account with this address exists" and at this exchange has meant an
  account under a look-alike spelling. The new case is the first meaning, which is what the person needs to hear; the
  exchange already lists the code, so old screens handle it.
- **`already_member` keeps its name** and now means "a member of this company"; the invitation stays usable after it,
  as after every refusal.
- **`private_account_exists` is no longer answered**: no path refuses a private account any more. The code stays known
  to clients; the server never sends it.
- **Nothing but the membership changes at a join**: no new security stamp (a new stamp would end the sessions that
  Decision 3 keeps; a session reaches the new company only through `POST /auth/switch`, which checks the membership),
  the mail links and the login streak stay.
  The move keeps only what makes it a move, the deletion of the space; its other effects follow the join.
- **`leave_private_account: true` without a personal space is ignored**, not refused: an old screen whose preview was
  stale still gets the join.
- **The account row lock before reading memberships, and the deletion routine's second count under its row lock**,
  close the race in which a removal deletes an account that an acceptance is joining to another company. The second
  count sits in `AccountDeletion`, after the mail requests are gone, so no removal path locks an account row before the
  mail sender's order allows. It covers every path that adds a membership, spec 0016's creation of a company included
  (adding a membership takes a key-share lock on the account row, which the routine's `FOR UPDATE` waits for).
- **No new audit kind and no new `details` field**: `invite.accepted` and `login.succeeded` already say who joined where
  and through what.
- **One mail variant for every address with an account**, of any kind: the person only needs to know that the account
  joins and keeps what it has.
- **The sample switches only a session of the invited address**, after the acceptance: it compares `GET /auth/me`'s
  address with the preview's before it calls `POST /auth/switch`, so it never moves another person's session (who
  could also be a member of the invited company); with no session, or on `401` or `404`, it opens `/login`.

## Open questions for the owner

1. **Which release carries 0015–0017** stays the open question of spec 0015, to be decided at its merge.

## Verification notes (for the local verifiers)

- Criteria 4 to 11 and 14 to 16 need accounts with several memberships, which spec 0015's endpoints and this spec's
  acceptance now make; tests may still insert membership rows directly where that is shorter.
- Criteria 18 to 20 need parallel requests against PostgreSQL, as spec 0010's race tests do. Criterion 18 needs, in
  one order, the acceptance held after it locked the account row while the removal reaches its `FOR UPDATE` read, and
  in the other the removal held after that read (`HeldLock.AuditTableAsync` holds it after the deletes) while the
  acceptance waits at the account row.
- Criterion 21 needs the dispatcher run by hand (the `MailTestBase` pattern).
- Criterion 14 needs the fake OpenID provider of spec 0011 and an identity linked beforehand.

## To verify during implementation

- **`InviteAcceptance`.** `SpaceOfAsync` picks one space with a company first; the acceptance (and the preview) needs
  instead: whether the account is a member of the invited company, and whether it has a personal space.
  `LockAccountAsync` runs for every existing account, before those reads. For an existing account
  `AccountSessions.EndAllAsync` and `UpdateSecurityStampAsync` are no longer called; for a new account (step 6)
  `EndAllAsync` stays. The email confirmation stays: today `UpdateSecurityStampAsync` also saves `EmailConfirmed`
  through `UserManager`; without it the confirmation is saved by `UserManager.UpdateAsync` or by the tracked entity in
  the transaction's `SaveChangesAsync`, and a test checks it is stored.
- **The provider path.** `AcceptThroughProviderAsync` checks the identity's account against the address's account
  (`ProviderLogins.FindAccountAsync`) in the order of the contract, and returns the existing account for the sign-in.
  `ProviderExchangeEndpoint.AcceptAsync` reads the membership in the invited company (the half of
  `MembershipReader.ReadAsync` that spec 0015, "To verify during implementation", calls "the membership in this
  company"), not the sign-in's space; its check that the membership read is the company joined stays.
- **The removal paths of spec 0015.** `MemberService.RemoveAsync` and `CompanyDeletionService` pick the accounts as
  spec 0015 says and call `AccountDeletion.DeleteAsync`, which re-counts memberships after its `FOR UPDATE` read and
  skips the accounts that have one (see "Locks"); neither caller locks an account row first. The lock order stays the
  company lock, then the account rows, with no address lock on the removal paths (spec 0010, "Locks" and the
  amendment of 2026-04-09, item 3). The operator command `remove-member` prints "the account is kept" when the routine
  kept it.
- **The mail.** `MailDispatcher.PrepareInvitationAsync` asks whether the address has an account spelled the same (the
  personal-space join goes); `MailComposer.Compose` and `MailTexts` replace `InvitationToPrivateAccount` with the variant
  of this spec.
- **Contracts and OpenAPI.** `InvitePreviewResponse` gains `HasAccount` and `OrgId` at the end; the descriptions in
  `TenancyEndpoints` and `ProviderRoutes` follow the contract.
- **Tests that pin today's behaviour and change** (names as on `main`; each is changed to the new contract, not
  deleted, so that the case it guards stays covered):
  `InviteAcceptTests` — `Another_spelling_of_a_member_of_another_company_is_not_told_already_member`,
  `For_a_member_of_another_company_preview_and_accept_say_already_member_and_the_invitation_stays`,
  `A_weak_password_for_an_address_that_is_already_a_member_elsewhere_is_already_member`,
  `Accepting_one_invitation_leaves_the_others_of_the_address_as_they_are_and_they_then_say_already_member`,
  `Two_invitations_of_two_companies_accepted_at_once_make_one_membership`,
  `An_account_without_a_space_joins_with_the_password_it_has_and_its_sessions_end`,
  `Accepting_confirms_an_unconfirmed_account_lifts_a_lockout_and_removes_its_other_links`;
  `PrivateInvitationTests` — `A_company_members_address_is_still_already_member`,
  `Accepting_without_leave_private_account_is_409_private_account_exists_and_changes_nothing`,
  `Accepting_with_leave_private_account_moves_the_account_and_deletes_its_space`,
  `The_move_ends_the_mail_links_and_the_streak_of_the_account`,
  `An_account_with_a_company_membership_besides_the_personal_one_is_already_member`;
  `PrivateInvitationTests.The_new_error_is_a_409` (it pins `private_account_exists`);
  `ProviderInvitationTests` — `A_company_members_address_is_409_already_member_and_the_invitation_stays`,
  `A_private_accounts_address_is_409_private_account_exists_and_the_invitation_stays`,
  `An_address_with_an_account_is_409_before_the_identity_is_judged`,
  `An_account_made_while_the_exchange_waits_for_the_address_is_409_and_the_invitation_and_ticket_stay`,
  `A_sign_up_completion_ahead_of_the_exchange_makes_the_account_and_the_exchange_is_409_already_member_with_the_invitation_kept`;
  `ProviderFinishRaceTests.A_finish_a_completion_and_an_acceptance_for_one_address_make_one_account`;
  `SignupCompleteRaceTests.A_completion_and_an_invitation_acceptance_for_one_address_never_both_create_an_account`
  (with `completionFirst` the acceptance now joins the completed account);
  `PrivateAccountRaceTests.A_removal_not_yet_committed_makes_a_parallel_acceptance_already_member_and_the_invitation_stays`
  (the acceptance now waits at the account row and, once the removal commits, makes a new account).
- **Race tests whose hook deadlocks under the new lock order and change:**
  `ProviderInvitationTests.An_account_that_a_removal_deletes_between_the_read_of_it_and_the_read_of_its_space_is_made_anew`
  and `PrivateAccountRaceTests.A_removal_that_commits_between_the_read_of_the_account_and_the_read_of_its_space_makes_a_new_account`.
  Both hold `HeldLock.MembershipsAsync` (an `ACCESS EXCLUSIVE` lock on the memberships table) and delete the account in
  that transaction; the acceptance now holds the account row before it reads memberships, so the two wait on each other.
  They change to a hook that holds the account's row lock (a new `HeldLock` helper taking `FOR UPDATE` on the account
  row), deletes the account in that transaction and releases it while the acceptance waits; the case they guard (the
  account is gone when the acceptance gets to it, and a new one is made) stays.
- **Mail tests that change:** `PrivateInvitationMailTests` (all four: the paragraph of spec 0010 becomes the one of this
  spec, an address with a company account now gets it too, a look-alike still gets none);
  `PrivateMailComposerTests.The_invitation_to_a_private_account_in_english_has_the_changed_intro_and_the_paragraph`,
  `The_invitation_to_a_private_account_in_polish_has_the_changed_intro_and_the_paragraph`,
  `The_company_is_encoded_in_the_html_of_the_paragraph_and_never_in_the_subject`,
  `Any_other_invitation_is_the_mail_of_spec_0005` and `Every_language_has_every_new_text`;
  `InvitationMailComposerTests.No_text_of_an_invitation_has_a_place_for_the_role` (its `privateAccount` parameter takes
  the flag's new name). `InvitationDispatchTests.Invitation_to_an_address_that_has_an_account_is_mailed_too` stays if
  it checks only that the mail is sent, and changes if it reads the paragraph.
- **`OpenApiTests` that change:** `Each_endpoint_describes_the_statuses_of_the_contract` and
  `The_error_codes_of_the_contract_are_in_the_descriptions` for the preview, the acceptance and the exchange, and
  `The_responses_are_described_in_snake_case` for the two new fields.
- **Sample tests that change:** `invite.spec.ts`, `invite.private.spec.ts`, `provider-return.spec.ts`,
  `account-api.spec.ts`, `pages/providers-on-screens.spec.ts`, and `auth.service.provider-flows.spec.ts` where it maps
  `private_account_exists`; the Playwright specs `04-invite` and `10-private`.
- **Tests that stay green unchanged:**
  `ProviderInvitationTests.An_acceptance_through_a_provider_ends_no_session_and_leaves_the_login_streak_of_the_address_alone`,
  `ProviderInvitationTests.A_mismatching_address_is_403_before_an_account_under_a_look_alike_is_judged`,
  `InviteAcceptTests.Accepting_for_an_address_without_an_account_ends_the_login_streak_of_the_address`, and spec 0015's
  removal and deletion tests.
