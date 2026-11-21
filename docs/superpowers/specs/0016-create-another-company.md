# Spec 0016 — Creating another company while signed in

- **Status:** Approved by the owner, 2026-11-21
- **Date:** 2026-11-21
- **Author:** Alex
- **Milestone:** the second of three slices that let one person work in several companies: 0015 (the base: memberships,
  the session's company, listing, switching), this one (a signed-in person creates another company), and 0017
  (accepting an invitation keeps the other memberships). Builds on [0015](0015-multiple-companies.md) and needs it
  merged first; it can be built in parallel with 0017.
- **Context:** specs [0005](0005-tenancy-and-rbac.md) (companies, roles, the company API, the manifest),
  [0008](0008-hardening-and-release.md) (per-IP limits, audit log, company deletion),
  [0009](0009-self-service-signup.md) (a visitor creates a company, the first admin's role),
  [0010](0010-private-accounts.md) (personal spaces, `DELETE /auth/account`),
  [0011](0011-google-and-microsoft-sign-in.md) (provider sign-in, `DELETE /auth/account` with a provider ticket) and
  [0015](0015-multiple-companies.md) (`GET /auth/orgs`, `POST /auth/switch`, the removal rules). This spec changes parts
  of 0010 and 0015; each change is named where it is made.

## Goal

A signed-in person creates a new company from inside the product ("New project") and becomes its first admin, with the
account they already have. The product then switches the person into it with `POST /auth/switch` (spec 0015).

The first product that needs it treats a company as a shared project: a person starts a project, invites people into
it, and keeps working in the others.

The product decides whether its people may do this: the manifest turns it on, and it is off by default. A product that
does not turn it on sees `GET /auth/options` gain one member, and no other change from this slice: its accounts never
hold a personal space and a company at once through this endpoint, so the new answer of `DELETE /auth/account` below
reaches it only through spec 0017.

This spec also settles, for itself and for 0017, who may delete their own account once an account can hold a personal
space and a company at once: still only an account whose only membership is its personal space.

Concretely, this is met when this sequence works against the development stack (its manifest turns creation on; its
seed is that of spec 0015, with `dev@acme.test` in `Acme` and `Beta`):

```
curl -i .../auth/options                                                                       # -> 200 {..., "org_creation":"self-service"}
curl -i -c jar -X POST .../auth/login -d '{"email":"dev@acme.test","password":"<p>"}'         # -> 200, org_id = Acme (or Beta, used last)
curl -i -H 'Authorization: Bearer <a>' -X POST .../auth/orgs -d '{"name":"Gamma"}'
#   -> 201 {"id":"<gamma>","name":"Gamma","kind":"organization","role":{"id":"…","name":"admin"},"current":false}
curl -i -b jar -c jar -X POST .../auth/switch -d '{"org_id":"<gamma>"}'                        # -> 200, org_id = Gamma, roles ["admin"]
curl -i -H 'Authorization: Bearer <g>' .../auth/org/members                                    # -> 200, one member: dev@acme.test
curl -i -H 'Authorization: Bearer <g>' -X DELETE .../auth/org -d '{"name":"Gamma","password":"<p>"}'  # -> 204, the account is kept
# a private person (signed up as in e2e-private.sh) creates a company, and cannot delete the account while in it
curl -i -H 'Authorization: Bearer <p>' -X POST .../auth/orgs -d '{"name":"Solo"}'             # -> 201, <solo>
curl -i -H 'Authorization: Bearer <p>' -X DELETE .../auth/account -d '{"password":"<pp>"}'    # -> 403 {"error":"company_member"}
curl -i -b pjar -c pjar -X POST .../auth/switch -d '{"org_id":"<solo>"}'                       # -> 200, org_id = Solo
curl -i -H 'Authorization: Bearer <s>' -X DELETE .../auth/org -d '{"name":"Solo","password":"<pp>"}'   # -> 204, the account is kept
curl -i -c pjar -X POST .../auth/login -d '{"email":"<private>","password":"<pp>"}'           # -> 200, org_kind = personal
curl -i -H 'Authorization: Bearer <p2>' -X DELETE .../auth/account -d '{"password":"<pp>"}'   # -> 204
```

## In scope

- `POST /auth/orgs` `{"name"}`: a signed-in person creates a company and becomes its first admin.
- The manifest switch `org_creation` and the member `org_creation` of `GET /auth/options`.
- A limit on companies per account, as configuration.
- The rule for `DELETE /auth/account` when an account holds a personal space and a company (for 0016 and 0017).
- Per-IP limit, audit, OpenAPI, the development manifests.
- `samples/notes-web`: "New company…" in the company picker, and the delete-account screen for a company member.
- `scripts/e2e-orgs-create.sh`, documents, changelog.

## Out of scope

- Accepting an invitation while a member of another company or while a private account (spec 0017).
- Leaving a company on one's own. A person leaves a company by deleting it (as its admin) or by being removed by its
  admin.
- Deleting an account together with its companies.
- Creating a company with the operator CLI or the development seed: both stay as they are (`create-org` is not limited
  and needs no switch).
- A limit on memberships made by invitations (0017 decides its own rules; this limit is judged only when a company is
  created through `POST /auth/orgs`).
- Unique company names: names stay free, as in spec 0009 (Amendment 3, item 2).
- Any change to the Python package's code (`clients/python`).
- A database migration: this slice changes no stored table.

## Contract

### General rules

Request bodies follow spec 0001: `application/json`, at most 8 KiB, a JSON object, unknown members ignored; anything
else, another media type included, is `400 {"error":"invalid_request"}`. `application/json` with a charset other than
UTF-8 is the `415` of spec 0008 (`POST /auth/orgs` is under the charset guard). The `415` and the per-IP `429` come
before every answer below, as everywhere. Answers carry `Cache-Control: no-store` and `Pragma: no-cache`, as every
answer under `/auth` does.

### Manifest (changes spec 0005, Manifest)

A new optional top-level key:

```yaml
org_creation: self-service
```

- `self-service` lets a signed-in person create companies through `POST /auth/orgs`. `off` (the default when the key is
  absent) does not.
- Any other value makes the manifest invalid, with the rules of spec 0005: the last valid manifest stays active and
  `/auth/health` reports `Degraded`. A key with no value counts as absent, as `signup:` does (spec 0009, Amendment 1,
  item 6); an empty string is invalid.
- A manifest stored by an earlier version has no `org_creation` and reads as `off`. The stored form writes the key only
  when it is `self-service`, so the stored form of a manifest without it stays that of earlier versions.
- Unlike sign-up, the switch needs no frontend address: creation sends no mail.
- Turning the switch off later refuses new creations; the companies already created stay, with their members.
- `deploy/auth.yaml` (the development manifest) and `samples/notes-api/auth.yaml` say `org_creation: self-service`, with
  a comment line in the style of the other keys.

### `GET /auth/options` (changes specs 0009, 0010 and 0011)

Gains one member: `org_creation`, `"self-service"` while the manifest switch is on and `"off"` otherwise.

```json
{ "signup": "self-service", "private_signup": "self-service", "providers": ["google"], "org_creation": "self-service" }
```

The new member comes last. A client ignores members it does not know (spec 0009), so a product that reads the options
today needs no change. The checks in this repository that compare the whole body are extended: step 1 of
`scripts/e2e-signup.sh`, `scripts/e2e-private.sh` and `scripts/e2e-providers.sh` (and the header comment of each that
quotes the body), the integration tests of the options, and the `samples/notes-web` unit specs that build an options
body (`account-api.signup.spec.ts`, `account-api.providers.spec.ts`, and the page specs that stub it).

### Configuration

| Key | Environment variable (`deploy/docker-compose.prod.yml`, `deploy/docker-compose.yml`) | Rule |
| --- | --- | --- |
| `Auth:Tenancy:MaxCompaniesPerAccount` | `AUTH_MAX_COMPANIES_PER_ACCOUNT` (optional, may be empty) | A whole number from 1 to 1000; absent or blank (empty or only white space) means 20. Any other value stops the start, naming the key, as `Auth:RateLimit:*:PermitPerMinute` does |

The setting is loaded by the service only (where the rate-limit settings are loaded), not by the services the CLI
shares with it: a bad value never stops an operator command, and the CLI does not use it. `deploy/.env.prod.example`, `.env.example` and the
configuration table of `docs/deployment/vps.md` list it.

### `POST /auth/orgs` — `{"name"}`

Authenticated with the access token (`Authorization: Bearer`). The checks, in this order:

1. No valid access token: `401`, as on the company API.
2. The manifest switch is off: `403 {"error":"org_creation_disabled"}`, before the body is read.
3. The body breaks the general rules, or `name` is missing, not a string, or breaks the name rule of spec 0005 (1 to
   100 characters, no control, format or noncharacters, no leading or trailing space): `400 invalid_request`.
4. The token's account no longer exists, or has no membership: `403 {"error":"permissions_changed"}`, as
   `GET /auth/orgs` answers. The token's company itself need not still be a membership: creating a company does not act
   on it.
5. In one transaction:
   - the address lock of the account is taken (the lock of specs 0009 and 0010, which sign-up completion, invitation
     acceptance and self-deletion take); the account and its memberships are read again under it; an account gone or
     without a membership by then is `403 permissions_changed`;
   - the account's company memberships (kind `organization`; the personal space does not count) are counted under the
     lock; when there are already at least `Auth:Tenancy:MaxCompaniesPerAccount` (invitations of spec 0017 may take
     the count above it), the answer is
     `409 {"error":"company_limit_reached"}` and nothing is written;
   - otherwise the company is created with the name of the request and a copy of the manifest's default roles, as
     `create-org` and sign-up do (spec 0005, Concepts; spec 0009);
   - the account becomes its member with the company's copy of the first admin's role (spec 0009, The first admin's
     role: the first default role holding `*`, else the first holding `members:manage`), `JoinedAt` the current time
     truncated to microseconds as every stored time is (spec 0015 compares it in whole microseconds), and
     `LastUsedAt` `null`;
   - `org.created` is written (below).
6. `201` with the new membership, in the shape of an entry of `GET /auth/orgs`:

```json
{ "id": "…", "name": "Gamma", "kind": "organization", "role": { "id": "…", "name": "admin" }, "current": false }
```

`current` is always `false`: the token's company is not the new one. The answer has no `Location` header (there is no
endpoint that reads one company by id).

Effects:

- **The session does not change.** The access token, the refresh cookie and the session's company (spec 0015) stay
  as they are, and no other session of the account changes. The product calls `POST /auth/switch` with the new id to
  work in the company (Decision 2).
- **`LastUsedAt` stays `null`** until a sign-in or a switch takes the company (spec 0015): a sign-in on another device
  lands in the new company only after a switch to it.
- The company is an ordinary company from then on: its admin renames, invites, edits roles and deletes it through the
  company API, with the rules of specs 0005, 0008 and 0015.
- Any account may create companies while the switch is on: a company member, and a private account (its personal space
  stays; the account then holds both, see `DELETE /auth/account` below).
- Of parallel requests of one account, the address lock lets them run one after the other, so the limit holds exactly.
- When the database fails, the answer is the `500` of spec 0008 and nothing is written.

### `DELETE /auth/account` (changes spec 0010 and spec 0015)

The rule of spec 0010 stays: **only an account whose only membership is its personal space may delete itself**
(Decision 4). A person who also belongs to a company leaves the company first: an admin deletes their own company
(`DELETE /auth/org`; spec 0015 keeps the account, which still has its personal space), and a member of another company
asks its admin to remove them. The answers of spec 0010, 0011 and 0015 stay, with one new code, judged on the database
as the first check after the token (before the body) and again under the address lock:

| Answer | When |
| --- | --- |
| `403 permissions_changed` | the token's company is no longer a membership of the account (spec 0015), or the account is gone |
| `403 {"error":"company_member"}` | **new:** the account has its personal space **and** at least one company membership, whatever the token's company (the personal space or one of the companies) |
| `403 forbidden` | the account has no personal space (company memberships only): company accounts are managed by their admins, as in spec 0010 |
| the answers of specs 0010 and 0011 | the personal space is the account's only membership |

- `company_member` replaces spec 0015's `403 forbidden` for a personal-space token of an account that is also a company
  member; it also covers a company token of such an account (spec 0010 answers a company token `forbidden`, but no
  account with both kinds existed then). The change is additive: no endpoint made such an account before this slice.
  A frontend lists the companies with `GET /auth/orgs` and tells the person what to do.
- Under the address lock the memberships are read again (spec 0010 reads the membership again there): a company
  membership found there is `403 company_member`, and the personal space gone is `403 permissions_changed`, as in spec
  0010. In either case nothing is counted, no password is evaluated and no row is written.
- Every path that gives an account with a personal space a company membership takes the account's address lock:
  `POST /auth/orgs` (above) and the acceptance of spec 0017. So a creation and a self-deletion of one account run one
  after the other: a creation committed first makes the deletion answer `company_member`; a deletion committed first
  makes the creation answer `403 permissions_changed`.

### Locks

`POST /auth/orgs` takes only the address lock of the account. It takes no company lock: the company it creates does not
exist yet, and no other flow can reach it before the commit. It changes no existing company, so the rule "the company
lock before the address lock" is kept (spec 0010, Locks).

### Limits

Per-IP (spec 0008): `POST /auth/orgs` counts against the `invite` policy (20 a minute by default), with the sign-up
completion, the other path that creates a company. `GET /auth/orgs` stays on `general` (spec 0015).

### Audit events

No new kind (29 in all, as after spec 0015). `org.created` gains the value `api` for `details.via`:

| Kind | Written when | Fields |
| --- | --- | --- |
| `org.created` | `POST /auth/orgs` creates a company, in its transaction | `actor_user_id` and `subject_user_id` (the creator), `subject_email`, `org_id`, `org_name`, `details.via` `api`, client address |

The rows of `create-org` (`cli`), the seed (`seed`) and sign-up (`signup`) are unchanged. A refused creation writes no
row. `DELETE /auth/account` refused with `company_member` writes no row (as `permissions_changed` writes none).

### OpenAPI

`POST /auth/orgs` is added with its body, its `201` body, its `400`, `401`, `403` (`org_creation_disabled`,
`permissions_changed`), `409 company_limit_reached`, `415` and `429`. `GET /auth/options` describes `org_creation`;
`DELETE /auth/account` lists `403 company_member` and says the rule above. The counts pinned by
`OpenApiHardeningTests` rise by one operation (39) and by one under the charset guard (26), counted on top of spec
0015; if spec 0017 adds operations and merges first, the counts add up.

### Python package (`clients/python`)

No change. A token of a company created this way is like any other company's token.

### Sample frontend (`samples/notes-web`)

- **The company picker** of the account bar (spec 0015) is shown also with a single entry when `GET /auth/options`
  says `org_creation` `self-service`, and gains the entry "New company…". It opens a small form with the company name
  (required, the name rule checked by the server). On `201` the sample calls `POST /auth/switch` with the new id and
  handles its answer as a switch from the picker (spec 0015); on `409 company_limit_reached` it shows "You have reached
  the limit of companies."; on `403 org_creation_disabled` it shows "Creating companies is turned off." and hides the
  entry; on `400` it shows the name rule; on `403 permissions_changed` it refreshes as for any company call.
- **"Delete account"** is still shown only while the current space is the personal one (`org_kind` `personal`, as
  today): a private person who created a company and switched into it picks "Private" in the picker first. On
  `403 company_member` it shows "You are still a member of {names}. Delete those companies or ask their admins to
  remove you, then delete your account." with the names of the entries of `GET /auth/orgs` of kind `organization`.
  Before it deletes the notes, on both paths (the password and the provider ticket), the sample reads
  `GET /auth/orgs` and shows the same text, without deleting anything, when a company is listed; so in the normal case
  the notes stay when the account cannot be deleted. A company created in another tab between that check and the call
  still loses the notes, as a wrong password does (threat model, risk 51); the server's answer stays the guard.
- Texts are English, as in spec 0007. Unit tests and a Playwright spec, numbered after the specs of the branch it is
  built on (`13-create-org.spec.ts` when spec 0015's picker spec is `12`): "New company…" creates a company and the
  sample shows it as current with no notes; a private person who created a company, back in "Private", sees the
  `company_member` text and keeps the notes; the test then deletes that company through the API with the company's
  token (the sample has no screen for `DELETE /auth/org`), and the deletion of the account succeeds.

### Sample backend (`samples/notes-api`)

No change, apart from its manifest (`org_creation: self-service`).

### Documents

- `docs/integration/angular.md` and `docs/integration/python-fastapi.md`: offering "New company" from
  `GET /auth/options`, creating, switching after the `201`, the product's per-company data starting empty under the new
  `org_id`; deleting an account that is still in a company (`company_member`).
- `docs/security/threat-model.md`: the residual risks below, and the sentence before the list ("Items 1 to 61 are the
  risks accepted by specs 0002 to 0011 …"), which names spec 0015's and this spec's items.
- `docs/operations/backup.md`: the list of `details.via` values gains `"via": "api"` on the `org.created` row of a
  company created through `POST /auth/orgs`.
- `CHANGELOG.md` (Unreleased): the endpoint, the manifest key, the option, the setting, the `DELETE /auth/account`
  answer. `docs/design.md`: the note for slices 0015–0017 names this slice.
- `README.md`: the manifest paragraph (beside `signup`, `private` and `providers`) gains `org_creation`; the list of
  e2e scripts gains `scripts/e2e-orgs-create.sh`, after spec 0015's `e2e-orgs.sh`, which comes after `e2e-private.sh`.
- `docs/deployment/vps.md`, `deploy/.env.prod.example`, `.env.example`: the setting.
- The comment of `CompanyService` ("only the operator does…") names the new path.

**Residual risks** added to the threat model's list, after the risks of spec 0015 (numbered 66 and 67; spec 0017
numbers its own after these):

66. **One account can create companies up to its limit** (20 by default), each with a copy of the default roles. The
    per-IP `invite` limit slows a script, the per-account limit bounds one account, and every account needs a
    confirmed mailbox or a provider; many accounts multiply the rows. Each such company has its creator as a member and
    manager, who can delete it.
67. **A member of another company cannot delete their own account alone.** No one leaves a company on their own, so a
    person who wants their account erased while they are a plain member of someone else's company needs that company's
    admin, or the operator (`remove-member`). Their own companies they can delete themselves.

## Acceptance criteria (Done when)

Manifest and options

1. A manifest with `org_creation: self-service` turns creation on; without the key, with `off`, or with the key and no
   value, it is off; any other value (an empty string included) makes the manifest invalid and the last valid one stays
   active with `/auth/health` `Degraded`. A manifest stored by an earlier version reads as off; a manifest without the
   key is stored in the form of earlier versions.
2. `GET /auth/options` answers `org_creation` `self-service` or `off`, following the switch, beside its other members
   unchanged.

`POST /auth/orgs`

3. With the switch on and a valid token: `201` with `id`, `name`, `kind` `organization`, `role {id, name}` of the first
   admin's role and `current` `false`; the company exists with a copy of the default roles; the account is its member
   with that role, `JoinedAt` set and `LastUsedAt` `null`; the access token and the refresh cookie are unchanged (no
   `Set-Cookie`), and the next refresh keeps the session's company.
4. After the `201`, `GET /auth/orgs` lists the new company with `current` `false`; `POST /auth/switch` to its id gives
   a token whose `org_id` is the new company and whose `roles` are the first admin's role; the company API with that
   token sees one member, the creator.
5. With the switch off: `403 org_creation_disabled`, also for a body that is not JSON (the body is not read).
6. A missing, non-string, empty, too long or badly formed `name`, a body that is not a JSON object, or another media
   type: `400 invalid_request`; `application/json` with another charset: `415`. Nothing is written.
7. Without a token: `401`. A token of an account deleted since, with a valid body: `403 permissions_changed`.
8. A token whose company membership was removed while the account keeps another membership: `201` (creation does not
   act on the token's company).
9. With `Auth:Tenancy:MaxCompaniesPerAccount` set to 2: an account with two company memberships (created or joined, the
   personal space not counted) gets `409 company_limit_reached` and nothing is written; after it deletes one of them, a
   creation succeeds. A private account with its personal space and one company can create a second.
10. Parallel creations of one account at the limit minus one (checked against PostgreSQL): exactly one `201`, the
    others `409 company_limit_reached`.
11. A private account (personal space only) creates a company: `201`; its personal space stays; `GET /auth/orgs` lists
    both.
12. `org.created` is written in the creation's transaction with `actor_user_id` and `subject_user_id` the creator,
    `org_id`, `org_name` and `details.via` `api`; a refused creation writes no row. The audit kinds are still 29.
13. `POST /auth/orgs` counts against the per-IP `invite` policy.
14. A value of `Auth:Tenancy:MaxCompaniesPerAccount` that is not an integer from 1 to 1000 stops the start, naming the
    key; absent or empty means 20.

`DELETE /auth/account`

15. An account with its personal space and a company: `403 company_member` with a token of the personal space and with
    a token of the company; nothing is counted (the login streak is unchanged) and no row is written.
16. The same account after the company is deleted by it (`DELETE /auth/org`): the account is kept, and with a new token
    of the personal space the deletion succeeds (`204`).
17. An account with company memberships only: `403 forbidden`, as before. An account with its personal space only: the
    answers of specs 0010 and 0011, unchanged. A token whose company is no longer a membership: `403
    permissions_changed`.
18. A creation and a self-deletion of one private account sent in parallel (checked against PostgreSQL): either the
    creation succeeds and the deletion answers `company_member`, or the deletion succeeds and the creation answers
    `403 permissions_changed`; never an account deleted with a company left behind, and never a company without a
    member.

Sample, scripts, docs

19. `samples/notes-web` unit tests and the Playwright spec of "New company…" pass; the existing Playwright specs pass.
20. `scripts/e2e-orgs-create.sh` runs the sequence of the Goal against the development stack and passes, also when run
    again on the same stack (it deletes the companies it created, and its private person is a new address each run);
    the run sequences of `e2e-tenancy.sh`, `e2e-hardening.sh` and `README.md` name it after `e2e-orgs.sh`. The step 1
    of `e2e-signup.sh`, `e2e-private.sh` and `e2e-providers.sh` expects the options with `org_creation`.
21. OpenAPI describes `POST /auth/orgs`, `org_creation` of the options and `company_member` of `DELETE /auth/account`;
    `OpenApiHardeningTests` pins 39 operations and 26 under the charset guard (on top of spec 0015).
22. The documents of "Documents" are updated; the threat model lists risks 66 and 67.

## Decisions (owner, 2026-11-21)

1. **Off by default; the product turns it on in its manifest** (`org_creation: self-service`), and `GET /auth/options`
   tells the product's screens whether to offer it. An instance that does not opt in sees no change, as with sign-up
   (spec 0009, Decision 2).
2. **Creating does not move the session.** `POST /auth/orgs` answers `201` with the new company; the frontend calls
   `POST /auth/switch` to work in it. One way to change the session's company (spec 0015), one credential per endpoint
   (the access token here, the refresh cookie at the switch), and a product may create a company without moving into it.
3. **A limit of companies per account, as configuration, 20 by default.** It counts every company membership of the
   account (created or joined by an invitation; not the personal space), judged only when a company is created through
   the API. An invitation is not refused by it.
4. **Who may delete their own account: the rule of spec 0010 stays.** Only an account whose only membership is its
   personal space. An account with a personal space and a company gets `403 company_member`, so the frontend can tell
   the person to delete their companies or ask their admins to remove them. No company is deleted with an account, and
   no company loses its admin by it. This rule holds for 0016 and 0017; spec 0017 cites this decision.
5. **Branch and dates:** this spec is written on `claude/0016-create-company-ba7596`, cut from
   `feat/0015-multiple-companies`; commits carry real dates, author `Alex`, no trailers.

### Settled in this spec

- The creator's role is the first admin's role of spec 0009 (`Manifest.FirstAdminRole`), the role a sign-up gives; the
  company gets a copy of the default roles, as every new company does.
- Any signed-in account may create, a private one included; its personal space stays.
- The token's company need not still be a membership: creation does not act on it (criterion 8).
- `LastUsedAt` is not written by a creation: it means "the last sign-in or switch" (spec 0015).
- The answer has the shape of an entry of `GET /auth/orgs`, so a frontend adds it to its picker as it is.
- The per-IP policy is `invite`, the policy of the sign-up completion, the other path that creates a company; `general`
  (300 a minute) would leave a script too much room.
- No new audit kind: `org.created` already records every creation, with `details.via` saying how.
- The limit is judged under the address lock, so parallel requests cannot pass it; the same lock orders a creation and a
  self-deletion of one account.
- `company_member` is judged on the database before the body and again under the address lock, and writes no row, as
  `permissions_changed` does at that step.
- Names are not unique, as for every company.

## Open questions for the owner

1. **Which release carries 0015–0017** (carried from spec 0015): decided at the merge of 0015.

## Verification notes (for the local verifiers)

- Every criterion needs spec 0015 merged into the branch: `GET /auth/orgs`, `POST /auth/switch` and the removal rules
  are its.
- Criteria 10 and 18 need parallel requests against PostgreSQL, as spec 0010's race tests do.
- Criterion 9 sets the limit low in the test host's configuration; the e2e script does not test the limit.
- Criterion 15 checks the login streak and the audit table before and after.
- `AccountDeletionEndpointTests.An_account_with_a_personal_space_and_a_company_is_a_company_member_at_sign_in_and_cannot_delete_itself`
  (a company token of an account with both kinds; `403 forbidden` today, listed by spec 0015 as a test that stays
  green) now expects `403 company_member`: this spec overrides 0015's list for that test. The tests of 0015 for a
  personal token of such an account expect `company_member` instead of 0015's `forbidden`. The test that expects
  `permissions_changed` for a personal token whose personal space is gone (the account moved to a company) is
  unchanged.

## To verify during implementation

- **`CompanyService.CreateAsync`** takes the `via` value `api` and the creator for the audit row (today it stages
  `org.created` with no actor, subject or address, and saves on its own). The creation of `POST /auth/orgs` runs
  inside the endpoint's transaction (the address lock first), as
  `SignupService.CreateCompanyAsync` runs it inside the sign-up completion; the membership with the first admin's role
  is added the way `CreateCompanyAsync` adds it (the company's own role row found by the normalised name).
- **The manifest parser** (`ManifestParser`, `StoredManifest`) refuses unknown keys today; `org_creation` is added as
  `signup` is, with the same "no value counts as absent" rule, and written to the stored form only when on (as
  `providers` is).
- **`GET /auth/options`** (`SignupOptionsEndpoint`, `OptionsResponse`) gains the member; the tests that compare its
  whole body are extended.
- **`RatePolicies.Classify`** maps `POST /auth/orgs` to `Invite`; `GET /auth/orgs` stays `General`.
- **`AccountDeletionEndpoint`** reads one membership through `MembershipReader` today (spec 0015 changes it to the
  token's company); it needs every membership of the account at the first check and under the address lock, to tell
  `company_member` from `forbidden`.
- **The error codes.** `TenancyErrors.StatusOf` (`Tenancy/Outcome.cs`) maps every code explicitly and throws on an
  unknown one: `org_creation_disabled` (403), `company_member` (403) and `company_limit_reached` (409) are added to it.
- **The `405`.** `POST` on `/auth/orgs` is mapped beside spec 0015's `GET`; any other method gets the framework's `405`
  with `Allow` naming both, still `no-store` (spec 0005, Responses: the path starts with `/auth/org`). A test pins it.
- **The sample's interceptor** (`samples/notes-web/src/app/auth/auth.interceptor.ts`, `TOKEN_PATH_PREFIXES`) attaches
  the token to paths that start with `/auth/org`, which covers `POST /auth/orgs`.
- **The development seed** of spec 0015 is unchanged; the e2e script's private person signs up as `e2e-private.sh` does.
