# Spec 0015 — Multiple companies: one account in several companies, and switching between them

- **Status:** Approved (design and written spec approved by the owner, 2026-11-21)
- **Date:** 2026-11-21
- **Author:** Alex
- **Milestone:** the first of three slices that let one person work in several companies: this one (the base), then
  0016 (a signed-in person creates another company) and 0017 (accepting an invitation keeps the other memberships).
  The item "Multiple orgs per user with org switching" of [`docs/design.md`](../../design.md) ("Beyond MVP").
- **Context:** specs [0001](0001-login-and-token-issuance.md) (login, the access token),
  [0002](0002-refresh-and-logout.md) (refresh, rotation, reuse detection, the `401 invalid_grant`),
  [0005](0005-tenancy-and-rbac.md) (companies, memberships, the company API; its Concepts keep a user to one company and
  its Decision 14 keeps the `/auth/org` paths for switching later), [0008](0008-hardening-and-release.md) (per-IP limits,
  audit log, company deletion), [0010](0010-private-accounts.md) (personal spaces, `org_kind`, accounts deleted when they
  lose their only space) and [0011](0011-google-and-microsoft-sign-in.md) (provider sign-in). This spec changes parts of
  0002, 0005 and 0010; each change is named where it is made.

## Goal

A person can belong to several companies with one account and move between them without signing in again. A product
shows the person's companies (a "project picker") and switches the company the person works in; every access token
still names exactly one company, the one the device works in now.

The first product that needs it treats a company as a shared project: a person runs several projects, each with its own
members and roles. That product keeps each project's own settings under the token's `org_id`; Auth-Core keeps no
product data.

A product that never switches companies sees no change: every person has one company, the token carries the same
claims, and every endpoint answers as it does today. This slice gives no new way to join a second company (accepting an
invitation as a member of another company is still `409 already_member`): specs 0016 and 0017 add those. Until then a
second membership exists only in tests and in the development seed.

Concretely, this is met when this sequence works against a running instance whose development seed puts one user in two
companies: `Acme` as its admin and `Beta` as a plain member (see "Development seed"):

```
curl -i -c jar -X POST .../auth/login -d '{"email":"dev@acme.test","password":"<p>"}'        # -> 200, org_id = Acme
curl -i -H 'Authorization: Bearer <a>' .../auth/orgs
#   -> 200 {"orgs":[{"id":"<acme>","name":"Acme","kind":"organization","role":{"id":"…","name":"admin"},"current":true},
#                   {"id":"<beta>","name":"Beta","kind":"organization","role":{"id":"…","name":"user"},"current":false}]}
curl -i -b jar -c jar -X POST .../auth/switch -d '{"org_id":"<beta>"}'                        # -> 200, org_id = Beta
curl -i -b jar -c jar -X POST .../auth/refresh                                                 # -> 200, org_id = Beta
# a new sign-in on another device lands in the company used last
curl -i -c jar2 -X POST .../auth/login -d '{"email":"dev@acme.test","password":"<p>"}'       # -> 200, org_id = Beta
# the operator removes the person from Beta: the account stays, devices that work in Beta are signed out at their next refresh
auth-server admin remove-member --org <beta> --email dev@acme.test                            # -> Member removed; the account is kept.
curl -i -b jar -X POST .../auth/refresh                                                        # -> 401 invalid_grant
curl -i -c jar3 -X POST .../auth/login -d '{"email":"dev@acme.test","password":"<p>"}'       # -> 200, org_id = Acme
```

## In scope

- A user may hold several memberships: companies and at most one personal space (spec 0010), each with its own role.
- **The session's company:** every session (a refresh-token family) works in one company; refresh keeps it.
- **`GET /auth/orgs`**: the person's companies and personal space.
- **`POST /auth/switch`**: a new access token and refresh cookie for another company of the person, without a new
  sign-in.
- **The company used last**, remembered per membership; a sign-in lands there.
- `GET /auth/me`, every endpoint under `/auth/org` and `DELETE /auth/account` act on the token's company.
- Removing a member and deleting a company delete an account only when it loses its last space.
- Audit kind `org.switched`; limits; OpenAPI.
- The development seed puts its first user in two companies.
- `samples/notes-web`: a company picker in the account bar.
- `scripts/e2e-orgs.sh`, documents, changelog.

## Out of scope

- A signed-in person creating another company (spec 0016).
- Accepting an invitation as a member of another company or as a private account without leaving it (spec 0017). The
  invitation paths keep `already_member` and `private_account_exists` exactly as today.
- Deleting one's own account while one is also a member of a company (decided in 0016 and 0017, which create such
  accounts; here the rule of spec 0010 stays).
- Leaving a company on one's own.
- Choosing the company in the login request itself, a limit on the number of companies per person, an order of
  companies chosen by the person.
- Ending the other devices' sessions when a person switches: a switch changes only the device that asks.
- Any change to the Python package's code (`clients/python`): its `Principal.org_id` is already the token's company.

## Contract

### General rules

Request bodies follow spec 0001: `application/json`, at most 8 KiB, a JSON object; anything else, another media type
included, is `400 {"error":"invalid_request"}`. `application/json` with a charset other than UTF-8 is the `415` of spec
0008 (`POST /auth/switch` is under the charset guard; `/auth/refresh` is not, because it reads no body). An `org_id`
that is missing, not a string, or not a UUID in its usual `8-4-4-4-12` form is `400 invalid_request` (spec 0005's rule
for ids). Answers carry `Cache-Control: no-store` and `Pragma: no-cache`, as every answer under `/auth` does.

### Memberships (changes spec 0005, Concepts, and spec 0010, Spaces)

- A user is a member of any number of companies and of at most one personal space, each membership with exactly one
  role. Spec 0005's sentence "A user is a member of at most one company" and spec 0010's "Every account belongs to
  exactly one space" are replaced by: **every account belongs to at least one space**.
- The paths that make memberships do not change in this slice: sending an invitation to a member of another company
  still answers `202` (only a member of the same company is `409 already_in_org`, as today), while its preview and
  acceptance are still `409 already_member`; a private account still has to leave its personal space to accept; sign-up
  still refuses an address with an account. The database lock per address that keeps two acceptances from both creating
  an account stays.
- A membership gains **`LastUsedAt`** (`timestamptz`, nullable; `null` for every existing row): the last time a session
  started in or switched to that space. A refresh does not write it.

### The session's company (changes spec 0002 and spec 0005, Access token)

- Every session works in one space, **the session's company**. A sign-in sets it; `POST /auth/switch` changes it;
  `POST /auth/refresh` keeps it.
- The refresh token carries the session's company and the `JoinedAt` of the membership it came from, as two internal
  claims of their own: `session_org_id` (the space id) and `session_org_joined` (`JoinedAt` as whole Unix microseconds,
  the precision the database stores). Neither is one of the tenancy claims (`org_id`, `org_kind`, `roles`,
  `permissions`), so spec 0005's rule "the refresh token never carries the tenancy claims" stays as it is, and the
  access token never carries the two. They live in the server's token store (refresh tokens are reference tokens), never
  in a form the browser can read.
- **A refresh** reads the membership of the token's user **in the session's company**. It answers the uniform
  `401 {"error":"invalid_grant"}` of spec 0002 when that membership is gone, or when its `JoinedAt` differs from the one
  the refresh token carries (the person was removed and joined again: an old session does not come back by itself).
  Otherwise it issues the next pair with the company, kind, role and permissions read now, as today.
- **A refresh token issued before this version** carries no company. Its session's company is the space a sign-in would
  take (below), wherever a session's company is needed (a refresh, a switch and its `details.from_org_id`, the audit row
  of a reuse), and the next refresh token carries it.
- The access token's claims do not change: `sub`, `org_id`, `org_kind`, `roles` (one name), `permissions`, as specs 0005
  and 0010 left them. `org_id` is the session's company.

### Which space a sign-in takes

Every sign-in — `POST /auth/login`, a provider sign-in or link (spec 0011), `POST /auth/signup/complete`,
`POST /auth/providers/finish`, an invitation accepted through a provider — takes one of the account's memberships,
ordered by these keys, the first that differs deciding:

1. `LastUsedAt`, latest first; a membership with no `LastUsedAt` comes after every one that has one;
2. the kind: a company before a personal space (today's order);
3. `JoinedAt`, earliest first;
4. the space id, ordinally.

A user who never switched therefore lands where they land today. A flow that creates or joins a space (sign-up
completion, finish, invitation through a provider) has that space as the account's only membership in this slice, so it
takes that space. An account with no membership is refused as today (`403 no_membership` at login; the paths of spec
0011 as they are).

The transaction that writes the session (today it reads the account again under a share lock) also reads the chosen
membership again, under a row lock that lets no one delete it until the commit, and writes its `LastUsedAt`. When that
membership is gone by then (a removal committed between the read and the write), the sign-in answers as each path
answers today for an account deleted in the same window, and writes no session; the next attempt takes the next space.

### `GET /auth/orgs`

Authenticated with the access token (`Authorization: Bearer`); counts against the `general` limit.

`200`:

```json
{
  "orgs": [
    { "id": "…", "name": "Acme", "kind": "organization", "role": { "id": "…", "name": "admin" }, "current": true },
    { "id": "…", "name": "private", "kind": "personal", "role": { "id": "…", "name": "private" }, "current": false }
  ]
}
```

- Every membership of the token's user, read from the database now: companies and the personal space. A personal
  space is listed with its stored name `private` and its role `private`; a product that does not want to show it hides
  the entry of `kind` `personal`.
- Ordered by `name`, ordinal and ignoring case, then by `id`.
- `current` is `true` for the entry whose `id` is the token's `org_id`, and `false` for every other; no entry is
  `current` when the token's company is no longer a membership (the person was removed from it since).
- The list is always an array, never paged.
- `401` without a valid access token, as on the company API. An account that no longer exists, or has no membership:
  `403 {"error":"permissions_changed"}`, as `GET /auth/me` answers a caller who is no longer a member.

### `POST /auth/switch` — `{"org_id"}`

The refresh cookie `auth_rt` (spec 0002) is the credential; no access token is needed. It counts against the `refresh`
limit, together with `/auth/refresh`. The checks, in this order:

1. The body breaks the general rules: `400 invalid_request` (or the `415` of spec 0008); the refresh token is not used
   up. The request still counts against the `refresh` limit.
2. The cookie is missing, unknown, expired, revoked or reused, or the session would not survive a refresh (its account is
   gone, its security stamp changed, its absolute cap passed, its company's membership is gone or has another join time):
   the uniform `401 {"error":"invalid_grant"}` of spec 0002, with the same consequences (a reuse past the grace window
   revokes the family and writes `refresh.reuse_detected`).
3. The `org_id` is not a space the account is a member of (unknown, a company of others, or a personal space of
   another account): `404 {"error":"not_found"}`. The refresh token is **not** used up; the cookie keeps working.
4. Otherwise, in one transaction: the refresh token is redeemed as a refresh redeems it (rotation, the same grace
   window, the same family), the next pair is issued for the chosen space with its kind, role and permissions read now,
   the membership's `LastUsedAt` is set, and `org.switched` is written when the chosen space differs from the session's
   company. `200` with exactly the body and the cookie of a successful `/auth/refresh`.

When the database or the token store fails, the switch answers as `/auth/refresh` does (spec 0002): `503` with no body
and no cookie, the refresh token not used up.

A switch to the session's own company is a refresh with `LastUsedAt` written and no audit row. A switch never changes
another session of the account. The grace window of spec 0002 applies to the redeemed token as at a refresh: within it,
an earlier tab that refreshes with the old cookie gets a pair for the old company, and the device works in the company
of the cookie the browser kept last.

### `GET /auth/me` (changes spec 0005 and spec 0010)

Unchanged shape (`sub`, `email`, `org_id`, `org_name`, `org_kind`, `roles`, `permissions`, `has_password`,
`linked_providers`). Its company data are those of **the token's `org_id`**, read now. A caller whose membership in that
company is gone gets `403 permissions_changed`, as today for a caller who is no longer a member.

### The company API (changes spec 0005, General rules, and spec 0010)

Every endpoint under `/auth/org` acts on **the token's company**: the membership of the token's `sub` in the token's
`org_id`, read from the database on every call. Spec 0005's sentence "acts on the caller's own company, taken from the
database" reads: taken from the token's `org_id` and checked against the database. The client still never names a
company in a path or body.

- No membership of the caller in the token's company: today's rule of spec 0010, judged in that company. The answer
  is `403 permissions_changed` (the token is out of date; the frontend refreshes) when the token claims the permission
  the endpoint needs, or, for an endpoint that needs none, when it carries an `org_id`; otherwise `403 forbidden`.
- A personal space as the token's company: `403 forbidden`, as in spec 0010.
- Permissions, safety rules, locks and answers are otherwise those of specs 0005, 0008 and 0010, judged in the token's
  company. Under the company lock the membership is read again in that company; a change in the meantime is
  `403 permissions_changed`, as today.

### `DELETE /auth/account` (changes spec 0010)

Judged on the token's company. The rule of spec 0010 stays: only an account whose **only** membership is its personal
space may delete itself.

- The token's company is the account's personal space and the account has no other membership: as in spec 0010.
- The token's company is the personal space but the account is also a member of a company: `403 forbidden`. This
  **changes** spec 0010, which answers this case `403 permissions_changed` (its pick takes the company first, so the
  token looks out of date). In this slice the token is not out of date: a refresh keeps the personal space, so
  `permissions_changed` would send the frontend into a loop of refreshes. The case cannot arise through an endpoint in
  this slice (no path makes a second membership); 0016 and 0017 decide what such an account may do.
- The token's company is a company: `403 forbidden`, as in spec 0010.
- The token's company is no longer a membership of the account: `403 permissions_changed`, as in spec 0010.

### Removing a member and deleting a company (changes spec 0005, Effects, and spec 0010)

- **Removing a member** (`DELETE /auth/org/members/{user_id}`, `remove-member`): the membership is deleted. When the
  account has no other membership left, the account is deleted as in spec 0010 (`account.deleted`, reason
  `member_removed`). When it has another, the account stays: its sessions are not ended at once; each session whose
  company was this one gets `401 invalid_grant` at its next refresh or switch, and every other session goes on.
  `member.removed` is written either way. The removed person's tokens get `403 permissions_changed` on the company API
  and `GET /auth/me` at once; a product's own API trusts the access token until it expires (at most 10 minutes), as
  today (spec 0005, Decision 12).
- **Deleting a company** (`DELETE /auth/org`, `delete-org`): every member's membership goes with the company. A member
  with no other membership is deleted (reason `org_deleted`), the deleting admin included; a member with another keeps
  the account, with the same effect on sessions as a removal. `org.deleted` as today. The kept accounts' membership
  rows are deleted before the company's roles (a membership's role is a restricting foreign key); only the accounts
  that lose their last space go through the account deletion of spec 0010.
- The operator command `remove-member` says which happened: `Member removed; the account is deleted.` (today's line)
  or `Member removed; the account is kept.` The `README.md` line on `remove-member` ("the account is deleted") says the
  same rule.
- The `account.deleted` row of an account deleted by a removal or a company deletion names, in `org_id`, the space it
  lost (today it names the first space of the account).
- `LastUsedAt` of the removed membership goes with it: the next sign-in takes the next space by the order above.
- The start-up step of spec 0010 (an account without a membership is deleted) is unchanged.

### Locks

Unchanged in kind: a flow that changes a company takes the company lock, then the address lock. A switch and a sign-in
take no company lock: in the transaction that writes the session they read the chosen membership again under a row lock
that blocks its deletion (and write its `LastUsedAt`), so a removal committed first makes them fail and a removal
committed later is caught at the next refresh. The account read under a share lock before a session is written (spec
0010) stays. A switch whose chosen membership vanished in that window answers `404 not_found`; one whose session company
vanished answers `401 invalid_grant`.

### Limits

`POST /auth/switch` counts against the per-IP policy `refresh` (60 a minute), with `/auth/refresh`. `GET /auth/orgs`
counts against `general`.

### Audit events

One new kind, `org.switched` (29 in all):

| Kind | Written when | Fields |
| --- | --- | --- |
| `org.switched` | a switch moves a session to another space | `actor_user_id` and `subject_user_id` (the account), `subject_email`, `org_id` and `org_name` (the space switched to), `details.from_org_id` (the session's company before), client address |

No row for a switch to the session's own company. A sign-in's `login.succeeded` names the space it took in `org_id`,
as today. `refresh.reuse_detected` names in `org_id` the session's company from the reused refresh token (for a token
issued before this version, the space a sign-in would take). The documents that count the kinds
(`docs/security/threat-model.md`, `docs/operations/backup.md`) say 29.

### OpenAPI

`GET /auth/orgs` and `POST /auth/switch` are added with their answers (`POST /auth/switch` with the cookie it reads and
sets, its `404 not_found`, its `415` and its `503`). `GET /auth/me`, the company API and `DELETE /auth/account` say "the
token's company". The counts pinned by `OpenApiHardeningTests` rise by two operations (38) and by one under the charset
guard (25), and its check that only `/auth/refresh` documents a `503` names `/auth/switch` too.

### Python package (`clients/python`)

No code change. Its documentation says that `Principal.org_id` is the company the person works in now, which can change
between two requests of one person after a switch.

### Sample frontend (`samples/notes-web`)

- **The account bar** reads `GET /auth/orgs` after a sign-in. With more than one entry it shows a company picker: each
  company by name, the personal space as "Private", the current one marked. Choosing another calls `POST /auth/switch`;
  on `200` the sample keeps the new access token as the same session (a renewed token, as after a refresh), reads
  `GET /auth/me` again and reloads the notes; on `404` it reads the list again; on `401` it opens `/login` and does not
  run its refresh-and-retry (the switch is itself a refresh).
- **After every refresh** the sample reads `GET /auth/me`; when `org_id` changed (another tab switched), it reloads the
  notes and shows "Switched to {name} in another tab."
- "Team" and "Delete account" follow the current company as they do today.
- Texts are English, as in spec 0007. Unit tests and a Playwright spec (two companies from the development seed: the
  picker lists both, a switch shows the other company's notes, a refresh keeps it, a new sign-in lands in it).

### Sample backend (`samples/notes-api`)

No change: it already filters every note by the token's `org_id`.

### Development seed

`Auth:DevSeed:SecondOrgName` (optional, set in `appsettings.Development.json` and the sample's compose): when present,
the seed also creates that company, with the manifest's default roles, and makes the first seed user a plain member of
it: the first default role that holds neither `*` nor `members:manage` (when the manifest has none, a new empty role
`member`). The seed stays idempotent per company: a seed user already a member of a company is left as is in that
company (today the seed skips a user who has any membership; it now checks the membership in each company).

### Documents

- `docs/integration/angular.md` and `docs/integration/python-fastapi.md`: listing companies, switching, the company
  picker; a product keeps its per-company data under `org_id` and reloads it when `org_id` changes after a switch or a
  refresh (another tab); a removal signs out only the devices that work in that company.
- `docs/security/threat-model.md`: the session's company and the residual risks below.
- `CHANGELOG.md` (Unreleased): the multiple companies, the two endpoints, the changed rule for removals and company
  deletions. `docs/design.md`: the item moves out of "Still out" and "Beyond MVP", with a note for slices 0015–0017.
- `README.md` (the `remove-member` line) and `docs/operations/backup.md` (the count of audit kinds).

**Residual risks** added to the threat model's list, after risk 61:

62. **A product's own API trusts a removed member's access token** for up to 10 minutes in that company, as it does after
    a removal today; the company API and `GET /auth/me` refuse it at once.
63. **Tabs of one browser share the session's company.** A switch in one tab moves the others at their next refresh;
    within the 15-second grace window a tab refreshing with the old cookie can put the device back in the old company.
    A product that shows company data must reload it when `org_id` changes.
64. **The list of companies names every company of the person** to whoever holds an access token of theirs; a stolen
    token shows the names, as `GET /auth/me` shows the current one.
65. **A forged switch from another site** is held off by the refresh cookie's `SameSite=Strict`, by the absence of CORS,
    and by the JSON-only body (a form post is `400`). A sibling host of the same site is not covered, as for
    `/auth/refresh` (risk 60).

## Acceptance criteria (Done when)

Memberships and sign-in

1. The migration adds `Memberships.LastUsedAt` (nullable); existing rows read `null`; nothing else in the stored data
   changes.
2. A user with one membership: login, provider sign-in, refresh, `GET /auth/me`, the company API and `DELETE
   /auth/account` answer exactly as before (the existing suite passes unchanged, apart from the tests this spec
   names as changed).
3. A user with two companies and no `LastUsedAt`: a login takes the earlier joined company; with a personal space and a
   company, the company (today's order).
4. After a switch to company B, a new login on another device takes B; after B's membership is removed, a login takes
   the remaining space by the order of "Which space a sign-in takes".
5. Every sign-in path writes `LastUsedAt` of the membership it took, in the transaction of the session: a sign-in whose
   session is not written leaves it unchanged.

Session's company and refresh

6. A refresh keeps the session's company, also when another membership has a later `LastUsedAt`.
7. Two devices of one person: a switch on device 1 does not change the `org_id` of device 2's next refresh.
8. A refresh after the session's company membership was removed is `401 invalid_grant`, and a session of the same
   person in another company refreshes normally.
9. Removed and re-added to the same company (a new membership row with a new `JoinedAt`): the old session's refresh is
   `401 invalid_grant`; a new sign-in works.
10. A refresh token stored before this version (no company in it) refreshes into the space a sign-in would take, and the
    next refresh token carries that company.
11. The refresh token carries `session_org_id` and `session_org_joined` and none of the tenancy claims; the access token
    carries neither of the two nor any claim beyond those of specs 0005 and 0010 (checked on the decoded JWT and on the
    stored token).

`GET /auth/orgs`

12. Lists every membership with `id`, `name`, `kind`, `role {id, name}` and `current`, ordered by name ignoring case,
    then id; the personal space appears as `private`, kind `personal`.
13. `current` follows the token's `org_id`; after a removal from the token's company no entry is `current`.
14. Without a token `401`; for a deleted account `403 permissions_changed`; it counts against `general`.

`POST /auth/switch`

15. With a valid cookie and a company of the person: `200` with the body and cookie of a refresh; the access token's
    `org_id`, `org_kind`, `roles` and `permissions` are those of the chosen space; `LastUsedAt` is set; `org.switched`
    is written with `details.from_org_id`.
16. The old refresh cookie after a switch behaves as a consumed one: accepted within the grace window, after it the
    `401` of reuse that revokes the family and writes `refresh.reuse_detected`.
17. A company of others, an unknown id and another account's personal space each answer `404 not_found` with the same
    status, headers and body, and the cookie still refreshes afterwards.
18. A malformed body or `org_id`, or a body of another media type (a form post included): `400 invalid_request`;
    `application/json` with a charset other than UTF-8: `415`. In each case the cookie still refreshes afterwards.
    A failing database or token store: `503` with no cookie, and the cookie still refreshes afterwards.
19. No cookie, an expired, revoked or reused one, a changed security stamp, a passed cap, or a removed session company:
    the uniform `401 invalid_grant`.
20. A switch to the session's own company: `200`, `LastUsedAt` written, no `org.switched` row.
21. A switch to the personal space gives `org_kind` `personal`, `roles` `["private"]` and the private permissions; the
    company API then answers `403 forbidden`.
22. `POST /auth/switch` counts against the `refresh` per-IP policy together with `/auth/refresh`.

Token's company on the other endpoints

23. `GET /auth/me` and every endpoint under `/auth/org` act on the token's `org_id`: with tokens of company A and of
    company B, each reads and changes only its own company (members, invitations, roles, name, deletion).
24. A token whose company membership is gone: `403 permissions_changed` on `GET /auth/me` and the company API, also when
    the person is still a member of another company.
25. `DELETE /auth/account`: allowed for an account whose only membership is its personal space; `403 forbidden` for a
    token of the personal space of an account that is also a company member (changed from spec 0010's
    `permissions_changed`), and for a company token; `403 permissions_changed` when the token's company is no longer a
    membership.

Removals and deletions

26. Removing a member who has another membership: the membership goes, the account stays, `member.removed` is written and
    no `account.deleted`; the person's session in the other company refreshes.
27. Removing a member who has no other membership: as spec 0010 (the account is deleted, `account.deleted` with reason
    `member_removed`).
28. Deleting a company: members with another membership keep their accounts; members without one are deleted with
    reason `org_deleted`; the deleting admin follows the same rule. Each `account.deleted` row names the space the
    account lost; `remove-member` prints whether the account was kept or deleted.
29. A removal committed before a switch or a sign-in in that company makes it fail; one committed after is caught at
    the next refresh (checked with parallel requests against PostgreSQL).

Sample, seed, docs

30. The development seed with `SecondOrgName` puts the first seed user in both companies; without the setting it seeds
    as today.
31. `samples/notes-web` unit tests and the Playwright spec of the picker pass; the existing Playwright specs pass.
32. `scripts/e2e-orgs.sh` runs the sequence of the Goal against the development stack and passes; the run sequences of
    `e2e-tenancy.sh` and `e2e-hardening.sh` name it.
33. OpenAPI describes both endpoints and the changed texts; `OpenApiHardeningTests` pins 38 operations and 25 under the
    charset guard.
34. The documents of "Documents" are updated; the threat model lists risks 62 to 65; the threat model and
    `docs/operations/backup.md` count 29 audit kinds.

## Decisions (owner, 2026-11-21)

1. **Three specs, not one.** 0015 is the base (memberships, the session's company, listing, switching, the removal rule);
   0016 (a signed-in person creates another company) and 0017 (accepting an invitation keeps other memberships) follow
   it and can be built in parallel once 0015 is merged.
2. **A sign-in lands in the company used last.** A switch on one device changes only that device; the account remembers
   the last company for the next sign-in. A person who never switched lands where they land today.
3. **A removal signs out only the devices that work in that company**, at their next refresh; the person signs in again
   and lands in the company used last among the rest. Devices in other companies go on. A session never moves to another
   company by itself.
4. **`samples/notes-web` gets the company picker in this slice**, with its tests, as every slice ships its sample
   screens.
5. **The API, as approved:** `GET /auth/orgs` (every space, the personal one included with its kind, ordered by name,
   `current` flag); `POST /auth/switch` with the refresh cookie, answering as `/auth/refresh`, `404` for a space that is
   not the person's; login, refresh, `GET /auth/me` and the company API follow the token's company; the access token
   gains no claim.
6. **The rules, as approved:** an account is deleted only when it loses its last space; a removed and re-added member's
   old session does not come back; a role change reaches the devices in that company at their next refresh;
   `DELETE /auth/account` keeps spec 0010's rule until 0016 and 0017 decide it; `org.switched` in the audit log; a
   switch counts with refresh against the limit.
7. **Numbering and dates:** this spec is 0015 (0012 and 0013 stay reserved for GitHub sign-in and the `v0.2.0` release,
   0014 is the AI-agent kit); its branch is `feat/0015-multiple-companies` from `main`; commits carry real dates, author
   `Alex`, no trailers.

### Settled in this spec

- The list includes the personal space; a product hides it by `kind`.
- A switch needs only the refresh cookie, as a refresh does; it is not a sign-in and asks for no password.
- `404 not_found` for every space that is not the person's, so the answer does not tell whether a company exists.
- A failed switch (`400`, `404`) does not use the refresh token up; a session that would not survive a refresh cannot
  switch either.
- A refresh does not write `LastUsedAt` (no write every 10 minutes per device).
- `DELETE /auth/account` with a personal token of an account that is also a company member answers `403 forbidden`, not
  spec 0010's `permissions_changed`: the rule of who may delete stays, only the answer changes, so the frontend does
  not refresh in a loop.
- A switch and the refresh path answer the same way on an outage (`503`), so the frontend has one rule for both.
- `/auth/orgs` is plural: spec 0016 adds `POST /auth/orgs` (create) on the same path; the existing `/auth/org` keeps
  meaning "the token's company".

## Open questions for the owner

1. **Which release carries 0015–0017.** `CHANGELOG.md` already holds specs 0009–0011 under Unreleased, and `v0.2.0` was
   planned after 0012 (GitHub) and 0013. The product that needs these slices bumps its image once they ship. To be
   decided at the merge of 0015.

## Verification notes (for the local verifiers)

- Every multi-company criterion needs a second membership that no endpoint can create in this slice: tests insert the
  membership row directly (with its role from the company's roles); the e2e script and the Playwright spec use the
  development seed's `SecondOrgName`.
- Criterion 9 needs a membership deleted and inserted again with a later `JoinedAt`; criterion 10 a refresh token
  written by the code of `main` before this slice (or one whose stored payload has the company claim removed).
- Criterion 29 needs parallel requests against PostgreSQL, as spec 0010's race tests do.
- Criterion 2 is the whole suite of `main`, run unchanged, on Windows and with `scripts/test-linux.sh`.

## To verify during implementation

- **The switch through OpenIddict.** `/auth/switch` becomes a token-endpoint URI in pass-through mode, turned into a
  refresh-token request from the cookie (as `RefreshRequestHandler` does for `/auth/refresh`) with the `org_id` of the
  body as a request parameter. Confirm that a rejection in the pass-through endpoint (the `404`, the `400`) leaves the
  refresh token unredeemed, and that redemption, rotation, the grace window and reuse detection run exactly as for a
  refresh.
- **The claim filter.** `AccessTokenClaimFilter` strips the tenancy claims from the refresh token today and stays as it
  is; `session_org_id` and `session_org_joined` get destinations of the refresh token only. Confirm they never reach the
  access token, and keep `TenantClaimsTests.The_refresh_token_never_carries_the_company_the_role_or_the_permissions`
  unchanged and green.
- **The session pieces that change.** `SessionResponseHandler` (the uniform `401`, the compact body and the `503` are
  chosen for the refresh path) and `ErrorHandling` (the `503` for the refresh path only) must treat `/auth/switch` as
  the refresh path. `JsonCharsetGuard` leaves the refresh path out because it reads no body: `/auth/switch` stays under
  the guard, so it must not reuse the refresh-path predicate there. `RatePolicy` adds `/auth/switch` to `refresh`.
- **The atomic sign-in step.** `AtomicSignInResult` re-reads only the account row today; it takes the chosen membership,
  re-reads it under a row lock (`FOR NO KEY UPDATE`) and writes `LastUsedAt` in the same transaction. Each path's answer
  when the membership vanished is the answer it gives today when the account vanished.
- **The membership's join time.** `TenantContext` gains `JoinedAt` (and `LastUsedAt` where the order needs it). Stored
  times are truncated to microseconds, so the comparison at a refresh uses whole Unix microseconds on both sides.
- **`GET /auth/orgs` is not a company endpoint.** It must not go through `CompanyAccess.AuthorizeAnyKindAsync`, which
  refuses when the token's company is gone; the list answers `200` with no entry `current` then.
- **The places that pick one membership** and must take a company: `MembershipReader.ReadAsync` (split into "the
  sign-in's space" and "the membership in this company"), `CompanyAccess`, `CompanyGuard`, `OrgEndpoints.MeAsync`,
  `AccountDeletionEndpoint`, `RefreshEndpoint`, the sign-in paths of `LoginEndpoint`, `ProviderSignIn`,
  `ProviderLinkEndpoint`, `SignupCompleteEndpoint`, `ProviderFinishEndpoint` and `ProviderExchangeEndpoint`.
- **The removal paths** that delete the account unconditionally today: `MemberService.RemoveAsync`,
  `CompanyDeletionService` and `AccountDeletion` (its audit row takes `g.First()` of a user's spaces), and the operator
  commands that call them. `CompanyDeletionService` deletes the roles before the company, and a membership's role is a
  restricting foreign key: the kept accounts' memberships are deleted explicitly before the roles.
- **The reuse audit.** `RefreshReuseAuditHandler` names the company of `refresh.reuse_detected` from a membership pick;
  it takes `session_org_id` from the reused token.
- **The development seed.** `DevUserSeeder.EnsureMemberAsync` returns early when the user has any membership; it must
  check the membership in the company it seeds.
- **The sample's session.** `samples/notes-web` treats a renewed token as the same session (`auth.service.ts`); a
  switch's token is handled the same way, and a `401` from `/auth/switch` must not enter the refresh-and-retry path.
- **Tests that pin today's behaviour and stay green:** the invitation tests that expect `already_member` and
  `private_account_exists`; `AccountDeletionEndpointTests.An_account_with_a_personal_space_and_a_company_is_a_company_member_at_sign_in_and_cannot_delete_itself`;
  `AccountDeletionTests.An_account_with_a_personal_space_and_a_company_is_recorded_with_the_company` (which space the
  audit row names when an account with two spaces is deleted: the token's company, else the order of sign-in);
  the `AccountDeletionEndpointTests` that expect `permissions_changed` for a personal token whose personal space is
  gone; `TenantClaimsTests`; `AccountDeletionOnRemovalTests` and `CompanyDeletionTests` for members with one
  membership; `OrgMemberTests`.
- **The 404/405 fallback** of spec 0005 ("outside `/auth/me`, `/auth/org` and `/auth/invites`") must cover the two new
  paths.
- **The sample's interceptor** attaches the token to paths that start with `/auth/org`, which covers `/auth/orgs`;
  `/auth/switch` goes with the cookie, as `/auth/refresh` does.
