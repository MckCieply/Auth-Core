# Spec 0005 — Tenancy and RBAC: companies, members, roles and invitations

- **Status:** Accepted
- **Date:** 2026-02-02
- **Author:** Alex
- **Milestone:** Week 3 (tenancy and RBAC) — *"invite, accept, then a token carrying
  `org_id` and permissions"*, with its three items: organizations, memberships,
  invites and the `org_id` claim; the permission catalog, roles and `permissions` in
  the access token; the admin CLI. Also the last open item of week 2, the OpenAPI
  description. Built in week 5.
- **Context:** [`docs/design.md`](../../design.md) ("Key decisions": Tenancy,
  Authorization, Configuration; "Access token claims"; "Endpoint sketch"; manifest
  example), ADR [0001](../../adr/0001-instance-per-project.md) (instance per product,
  organizations inside an instance), and specs
  [0001](0001-login-and-token-issuance.md) (login, the claim set of the access token),
  [0002](0002-refresh-and-logout.md) (sessions),
  [0003](0003-lockout-and-abuse-resistance.md) (login streaks) and
  [0004](0004-email-flows.md) (mail queue, link tokens, mail limit, password policy,
  ending every session of an account; Decision 2 leaves "accepting an invitation
  confirms the email" to this spec).

## Goal

A second company can use a product with its own users and roles, and run them
itself. Auth-Core is generic: it serves any product (one instance per product, ADR
0001); `speech-to-mail` is only the first. Nothing in this spec or its code names a
product: everything product-specific comes from that product's configuration.

The operator — whoever runs the instance — creates a company and invites its first
admin. From then on the company manages itself through the API, which the product's
own admin pages call: its admin invites members, changes their roles, removes them,
and defines the company's roles from the permissions the product declares. Every
access token says which company the user belongs to and what they may do there.

Concretely, this is met when this sequence works against a running instance with a
mail catcher:

```
# the operator creates a company and invites its first admin
auth-server admin create-org --name "Acme"                                   # -> prints the company id
auth-server admin invite --org <id> --email boss@acme.test --role admin      # -> invite sent

# the mail holds  <accept_invite url>?token=<t>
curl -i -X POST .../auth/invites/preview -d '{"token":"<t>"}'                # -> 200 company, email, role
curl -i -X POST .../auth/invites/accept  -d '{"token":"<t>","password":"<p>"}'  # -> 204
curl -i -X POST .../auth/login -d '{"email":"boss@acme.test","password":"<p>"}'  # -> 200, token has org_id, roles, permissions

# the admin invites a member, who accepts and logs in
curl -i -X POST .../auth/org/invites -H 'Authorization: Bearer <admin>' \
     -d '{"email":"worker@acme.test","role_id":"<user role>"}'               # -> 202
# ... accept as above, then login                                             # -> 200, roles ["user"]

# the admin removes the member: every session of the member is over
curl -i -X DELETE .../auth/org/members/<id> -H 'Authorization: Bearer <admin>'  # -> 204
curl -i -b worker.jar -X POST .../auth/refresh                                # -> 401
curl -i -X POST .../auth/login -d '{"email":"worker@acme.test","password":"<p>"}'  # -> 403 no_membership
```

## In scope

- **Companies** (organizations), **memberships** (one company per user), **roles**
  per company, and the **permission catalog** of the product.
- The **manifest** `auth.yaml`: the product's permissions and default roles, with a
  last-good fallback so that a broken file never stops the service.
- **Claims:** login and refresh put `org_id`, `roles` and `permissions` into the
  access token, read from the database at that moment. A user without a company is
  refused at login.
- **`GET /auth/me`.**
- **The company API** for the product's admin pages: the company, its members, its
  invitations and its roles, each behind a permission that is checked against the
  database on every call.
- **Invitations:** send, list, resend, cancel, preview, accept — with an invitation
  mail in Polish and English through the queue of spec 0004.
- **Safety rules** that keep a company manageable and keep anyone from granting more
  than they hold.
- **The operator CLI:** `create-org`, `invite`, `list-orgs`, `remove-member`.
- **The OpenAPI description** of every endpoint.
- The development seed users join a development company; a development manifest; an
  e2e script.

## Out of scope / Deferred

See [Deferred / follow-ups](#deferred--follow-ups). Not built here: more than one
company per user and switching between companies, self-service sign-up, leaving a
company on one's own, deleting a company, an admin panel inside Auth-Core, pagination
of the lists, the audit log, a sync of changed default roles into existing companies,
and per-IP rate limiting.

## Concepts

- **Company.** An id and a name. Created only by the operator. Its admin can rename it.
- **Member.** A user's membership in a company, with exactly one role. A user is a
  member of at most one company; the tables allow more, so switching companies later
  is additive.
- **Permission.** A string the product's code checks, such as `reports:approve`. The
  product declares its permissions in the manifest; nobody creates them through the
  API, because a permission no code checks means nothing.
- **Built-in permissions.** Three permissions guard the company API itself and are in
  every catalog, whatever the manifest says: `members:manage` (members and
  invitations), `roles:manage` (the company's roles) and `org:manage` (the company's
  name).
- **Catalog.** The built-in permissions plus the permissions of the manifest.
- **Role.** Belongs to one company: a name and a set of permissions from the catalog.
  A role may hold `*`, which means every permission of the catalog, now and after the
  catalog grows.
- **Default roles.** Declared in the manifest. A new company gets its own copy of
  them; after that its roles are its own, and later changes to the defaults do not
  touch it.
- **Manager.** A member whose role holds `members:manage` (directly or through `*`).
- **Invitation.** An email, a company, a role, who sent it (a member, or the operator)
  and an expiry. Accepting it is how anyone becomes a member.
- **Operator.** Whoever runs the instance and its CLI. Not a member of any company,
  and not a role.

## Contract

### General rules

**Request bodies** follow the rules of spec 0001: `application/json`, at most 8 KiB, a
JSON object. Its named properties are non-empty strings, except `permissions`, which
is an array of strings. Anything else is `400 {"error":"invalid_request"}`, as is:

- an `email` that breaks the rules of spec 0004 (control characters,
  noncharacters, unpaired surrogates, rejected by the normaliser, longer than 254
  characters);
- a company or role `name` that is longer than 100 characters, or holds a control
  character, a noncharacter or an unpaired surrogate, or has leading or trailing
  white space;
- an id in a path or body that is not a UUID;
- a `password` that contains NUL.

A `token` that is a non-blank string is never an `invalid_request` for its content,
as in spec 0004.

**Responses** carry `Cache-Control: no-store` and `Pragma: no-cache`. An error body
is `{"error":"<code>"}`. JSON property names are `snake_case`. No endpoint of this
spec sets or clears the refresh cookie.

**Authenticated endpoints** take the access token as `Authorization: Bearer <token>`.
The token is validated as a consumer validates it: signature against the instance's
keys, `iss`, `aud` and `exp`. A missing or invalid token is a `401` with an empty body
and `WWW-Authenticate: Bearer`; the frontend refreshes and retries.

**Permission checks of the company API** are made against the database on every
call, not against the token:

- The caller's membership, company and role are read at the time of the call.
- If the database grants the permission, the call proceeds.
- If it does not, but the token claims it (the caller was demoted or removed after
  the token was issued), the answer is `403 {"error":"permissions_changed"}`. The
  frontend refreshes and redraws the page with the new token.
- If neither grants it, the answer is `403 {"error":"forbidden"}`.

Every endpoint under `/auth/org` acts on the caller's own company, taken from the
database. The client never names a company, so no request can reach another
company's data. An id in a path that does not belong to the caller's company is a
`404`.

### Access token (changes spec 0001)

The access token gains three claims. Its contract otherwise stays as spec 0001 and
spec 0002 left it.

```json
{
  "sub": "user id",
  "org_id": "company id",
  "roles": ["admin"],
  "permissions": ["reports:approve", "reports:read", "members:manage", "org:manage", "roles:manage"]
}
```

- `roles` is an array with one name in this version.
- `permissions` is the role's permissions with `*` expanded to the whole catalog,
  without duplicates, sorted ordinally. It never contains `*`.
- Login and **every refresh** read the membership, the role and the catalog afresh,
  so a change to a member's role, or to the role itself, reaches the member's token
  at their next refresh — within the 10-minute life of an access token.

### `POST /auth/login` (changes specs 0001, 0003 and 0004)

After the correct password and a confirmed email, a user who is not a member of any
company is refused with `403 {"error":"no_membership"}`, with no token and no cookie.
As with `email_not_verified`, only someone who knows the password can see it, and it
ends the login streak. The order is: lockout (spec 0003), password, confirmed email,
membership.

### `POST /auth/refresh` (changes spec 0002)

A refresh re-reads the claims as above. A refresh by a user who is no longer a member
fails with the `401` of spec 0002: removing a member ends every session (see
[Effects](#effects)).

### `GET /auth/me`

Authenticated; any member. `200` with the caller's current data from the database:

```json
{
  "sub": "…", "email": "…",
  "org_id": "…", "org_name": "…",
  "roles": ["user"], "permissions": ["reports:approve", "reports:read"]
}
```

A caller who is no longer a member gets `403 permissions_changed`.

### Invitations — public

#### `POST /auth/invites/preview` — `{"token"}`

`200 {"org_name", "email", "role"}` for a usable invitation, so the acceptance screen
can say who is invited where before it asks for a password. If the invited address
already belongs to a member of another company: `409 {"error":"already_member"}`.
Unknown, used, expired, cancelled or replaced: `400 invalid_token`. The preview does
not use the token up.

#### `POST /auth/invites/accept` — `{"token", "password"}`

`204` once the invited person is a member. One screen serves everyone: whether or not
an account with this address exists, the person sets a password here. Answers:

- `400 invalid_token` — unknown, used, expired, cancelled or replaced;
- `400 weak_password` with `rules` — the policy of spec 0004; nothing changes and the
  token stays usable;
- `409 already_member` — the address belongs to a member of another company; nothing
  changes and the invitation stays usable until it expires.

It does not sign the user in: the frontend sends them to the login screen.

### Company API

All endpoints are authenticated and act on the caller's company.

| Endpoint | Permission | Success |
| --- | --- | --- |
| `GET /auth/org` | any member | `200 {"id", "name"}` |
| `PATCH /auth/org` `{"name"}` | `org:manage` | `204` |
| `GET /auth/org/members` | `members:manage` | `200 {"members": [{"user_id", "email", "role": {"id", "name"}, "joined_at"}]}` |
| `PUT /auth/org/members/{user_id}/role` `{"role_id"}` | `members:manage` | `204` |
| `DELETE /auth/org/members/{user_id}` | `members:manage` | `204` |
| `GET /auth/org/invites` | `members:manage` | `200 {"invites": [{"id", "email", "role": {"id", "name"}, "invited_at", "expires_at"}]}` |
| `POST /auth/org/invites` `{"email", "role_id"}` | `members:manage` | `202` |
| `POST /auth/org/invites/{id}/resend` | `members:manage` | `202` |
| `DELETE /auth/org/invites/{id}` | `members:manage` | `204` |
| `GET /auth/org/roles` | `roles:manage` or `members:manage` | `200 {"roles": [{"id", "name", "permissions", "members"}], "catalog": [...]}` |
| `POST /auth/org/roles` `{"name", "permissions"}` | `roles:manage` | `201` with the role |
| `PUT /auth/org/roles/{id}` `{"name", "permissions"}` | `roles:manage` | `204` |
| `DELETE /auth/org/roles/{id}` | `roles:manage` | `204` |

- `GET /auth/org/roles` is open to `members:manage` too, because inviting needs the
  list of roles. `members` is the number of members holding the role; `catalog` lists
  every permission a role may hold, `*` first.
- `PUT` on a role replaces its name and its permissions as a whole.
- Times are ISO 8601 in UTC. Lists are sorted: members and invitations by email,
  roles by name, ordinally. Lists are not paginated.
- `POST /auth/org/invites` answers `202` whether or not the address already has an
  account, and whether or not that account belongs to another company: the inviter
  never learns anything about addresses outside their company.

**Errors of the company API**, besides the general ones:

| Code | When |
| --- | --- |
| `409 already_in_org` | Inviting an address that belongs to a member of the caller's company |
| `409 invite_pending` | Inviting an address that has a pending invitation to the caller's company; resend or cancel it instead |
| `429 too_many_attempts` | Sending or resending over the invitation mail limit (below) |
| `403 permission_not_held` | Inviting with, or giving a member, a role that holds a permission the caller does not hold; or creating or editing a role so that it holds one |
| `409 last_manager` | A change that would leave the company without a manager |
| `409 cannot_change_self` | Changing one's own role, or removing oneself |
| `409 role_in_use` | Deleting a role held by a member or by a pending invitation |
| `409 role_name_taken` | A role name already used in the company, compared case-insensitively |
| `400 unknown_permission` | A permission that is not in the catalog |
| `404` | An id that does not exist in the caller's company |

### Safety rules

1. **Nobody grants more than they hold.** A caller can give a role to a member or an
   invitation, or create or edit a role, only if every permission that role ends up
   with is a permission the caller holds. A role with `*` can be given, created or
   edited only by a caller whose role holds `*`. The operator is exempt.
2. **A company always has a manager.** A role change, a removal or a role edit that
   would leave the company with no member holding `members:manage` is refused. The
   check and the change happen in one transaction, serialised per company, so two
   managers removing each other at the same instant cannot both succeed.
3. **Nobody changes their own role or removes themselves.**

### Invitations

- An invitation is **valid for 7 days** from the moment its mail is composed.
- Its token works like a link token of spec 0004: 32 random bytes as base64url, only
  the SHA-256 stored, single use (of parallel accepts, one succeeds), never in the
  database in clear, never in the logs.
- A company has **at most one pending invitation per address**. An expired invitation is not pending.
- **Resend** composes a new mail with a new token; the earlier link stops working.
  The expiry starts again.
- **Cancel** makes the link stop working at once.
- The role of a pending invitation cannot be changed; cancel it and invite again.
- Expired invitations disappear from the list and are removed within a day.

### Effects

**Accepting an invitation**, as one unit:

1. creates the account if the address has none;
2. sets the password, confirms the email, and ends every session of the account (as a
   reset of spec 0004 does: the security stamp changes) and its login streak;
3. makes the account a member of the company with the invitation's role;
4. uses up the invitation; other companies' invitations for the address stay as they
   are.

**Removing a member** ends their membership and every one of their sessions: every
refresh token issued before is refused with the `401` of spec 0002. The account stays,
so the person can be invited again later. Their access tokens stay valid until they
expire, at most 10 minutes; the company API refuses them at once.

**Changing a member's role, or editing a role,** ends no session; the member's next
refresh carries the change.

### Invitation mail

- One more kind of mail, in Polish and English, `multipart/alternative`, with the
  same rules as spec 0004 (one language per instance, HTML and plain text, the link
  written out, a sentence telling a recipient who did not expect it to ignore it).
- Content: the application's name, the company's name, the role's name, the link and
  its 7-day lifetime.
- The company and role names are set by members, so they are HTML-encoded in the HTML
  part and never placed in a mail header. The inviter's address is not in the mail.
- **Mail limit:** the limit of spec 0004 — one per 60 seconds and five per hour —
  counted per company and address, so that one company cannot block or observe
  another company's invitations to the same person.

### Manifest

The product's `auth.yaml`, read at startup from the path in `Auth:Manifest:Path`:

```yaml
permissions: [reports:read, reports:approve, templates:manage]
default_roles:
  admin: ["*"]
  user:  [reports:read, reports:approve]
```

- A permission is 1–64 characters of lowercase ASCII letters, digits, `_`, `-` and
  `:`, starting with a letter. The built-in permissions may be listed; they are in the
  catalog either way.
- Default roles use permissions from the catalog or `*`. At least one default role
  must hold `members:manage` or `*`, so that the first admin of a new company is a
  manager.
- **A broken manifest never stops the service.** At startup the file is validated as a
  whole. If it is valid, it becomes the active manifest and is stored in the database.
  If it is missing, unreadable or invalid, the **last valid manifest stored** stays
  active, the error is logged with the reason, and `GET /auth/health` reports
  `Degraded`. With no valid manifest ever stored, the catalog is the built-in
  permissions and the only default role is `admin: ["*"]`.
- A permission that leaves the catalog disappears from every token at the next
  refresh. Roles keep the name in the database; the role list shows only catalog
  permissions, and an edit drops the others.
- `create-org` copies the active default roles into the new company.

### Operator CLI

Subcommands of the same binary and image, working directly on the database. Output is
plain text; a refused command exits non-zero with the error code.

| Command | Does |
| --- | --- |
| `auth-server admin create-org --name <name>` | Creates the company with a copy of the default roles; prints its id |
| `auth-server admin invite --org <id> --email <email> --role <name>` | Sends an invitation through the queue, as the operator |
| `auth-server admin list-orgs` | Lists companies: id, name, number of members |
| `auth-server admin remove-member --org <id> --email <email> [--force]` | Removes a member as the API does; `--force` overrides `last_manager` |

The operator is not bound by safety rule 1; rules 2 and 3 apply, except that `--force`
lifts rule 2.

### OpenAPI

The service publishes an OpenAPI document describing every endpoint of specs
0001–0005 — requests, responses and error codes — at `GET /auth/openapi/v1.json` in
every environment. An interactive reference UI is served in `Development` only.

### Configuration

| Key | Meaning |
| --- | --- |
| `Auth:Manifest:Path` | Path of `auth.yaml` |
| `Auth:App:FrontendUrls:AcceptInvite` | Absolute URL of the product's invitation screen; `https` outside `Development` |
| `Auth:DevSeed:OrgName` | Name of the development company, `Development` only |

The rest of `Auth:App` (name, locale, the other frontend URLs) and the token settings
stay in configuration as spec 0004 set them; the manifest carries only permissions and
default roles. A missing or invalid configuration value stops the host as before; the
manifest is the exception above, because it comes from the product.

### Development setup

- The repository ships a development `auth.yaml` with neutral sample permissions.
- In `Development`, the seeder creates the development company with the default
  roles. The first seed user becomes its `admin`. The second seed user (unconfirmed
  email, spec 0004) becomes a member with a role that does not manage members; it
  stays, because no other path creates an account with an unconfirmed email, and the
  verification flow still needs one to be tested.

## Acceptance criteria (Done when)

1. `create-org` creates a company with a copy of the active default roles and prints
   its id; `list-orgs` shows it.
2. An invitation by the operator or by a manager sends a mail to the invited address,
   in the configured language, naming the application, the company and the role, with
   the configured invitation URL plus a `token` parameter, and stating the 7-day
   lifetime.
3. `preview` with that token returns the company name, the address and the role, and
   leaves the token usable.
4. `accept` with an acceptable password answers `204`. Afterwards the account exists,
   its email is confirmed, it is a member with the invitation's role, and a login
   returns an access token with `org_id`, `roles` and `permissions` as specified
   (`*` expanded, sorted, no duplicates).
5. Accepting for an address that already has an account without a company replaces
   its password and ends every session it had: refresh tokens issued before are
   refused with the `401` of spec 0002.
6. An invitation token that is unknown, used, older than 7 days, cancelled or replaced
   by a resend gets the same `400 invalid_token` from `preview` and `accept`. Of
   parallel accepts with one token, exactly one succeeds.
7. `accept` with a password that breaks the policy answers `400 weak_password`,
   changes nothing, and leaves the token usable.
8. For an address that belongs to a member of another company, `POST
   /auth/org/invites` answers `202` exactly as for an unknown address — same status,
   headers and body — and `preview` and `accept` answer `409 already_member`, leaving
   the invitation usable.
9. Inviting a member of one's own company is `409 already_in_org`; inviting an address
   with a pending invitation to the company is `409 invite_pending`.
10. Resend sends a new mail whose link works while the earlier link is an
    `invalid_token`; cancel makes the link an `invalid_token` and removes the
    invitation from the list; both are subject to the mail limit per company and
    address, with the `429` of spec 0004, and the limit of one company does not
    affect another's.
11. A login with the correct password by a confirmed user without a company is
    `403 no_membership` with no token and no cookie, and ends the streak; with a wrong
    password it is the `401` of spec 0001.
12. After a member's role is changed, or their role is edited, their next refresh
    returns an access token with the new `roles` and `permissions`, and no session is
    ended.
13. After a member is removed, every refresh token issued to them before is refused
    with the `401` of spec 0002, a login is `403 no_membership`, and an invitation
    to another company can make them a member again.
14. Every company API endpoint answers `401` without a valid token; `403 forbidden`
    when neither the token nor the database grants the permission; `403
    permissions_changed` when the token claims a permission the database no longer
    grants; and never reaches another company's data — an id from another company
    is a `404`.
15. Safety rule 1: giving or inviting with a role, or creating or editing a role, with
    a permission the caller does not hold is `403 permission_not_held`, and so is
    anything involving `*` by a caller whose role lacks `*`.
16. Safety rule 2: every change that would leave a company without a manager is
    `409 last_manager` — a role change, a removal and a role edit; two concurrent
    removals of the last two managers by each other leave one manager.
17. Safety rule 3: changing one's own role or removing oneself is `409
    cannot_change_self`.
18. Roles: create, rename, change permissions and delete work within the catalog;
    `role_name_taken`, `unknown_permission` and `role_in_use` are returned as
    specified.
19. `GET /auth/me` and `GET /auth/org` return the caller's current data; `PATCH
    /auth/org` renames the company for `org:manage`.
20. A valid manifest becomes active and stored. A missing, unreadable or invalid one
    leaves the last stored valid manifest active, logs the reason and makes `GET
    /auth/health` report `Degraded`; the host starts either way. With nothing ever
    stored, the catalog is the built-in permissions and the default roles are
    `admin: ["*"]`.
21. A permission removed from the manifest is gone from tokens at the next refresh;
    one added appears in the tokens of every role holding `*`.
22. `remove-member` removes a member as the API does; refused with `last_manager`
    unless `--force`.
23. No invitation token appears in the database in clear or in the logs; the company
    and role names are HTML-encoded in the invitation mail.
24. `GET /auth/openapi/v1.json` describes every endpoint of specs 0001–0005 with its
    request, its responses and its error codes.
25. The e2e script drives the sequence of the Goal and the role and safety cases over
    the real network against the compose stack, reading the mails from the mail
    catcher, and the four existing e2e scripts still pass against the same stack.

## Decisions (owner, 2026-02-02)

1. **One slice for the whole of week 3** — companies, members, invitations, roles,
   permissions, the manifest and the CLI — in six days, rather than two slices. The
   OpenAPI description is added to the end of this slice.
2. **Members are managed through the API** by the company's own admins, from the
   product's admin pages; the operator CLI is for the operator's tasks (creating a
   company, its first admin, emergencies). The product adds an "invite a colleague"
   screen in its frontend slice.
3. **Removing a member is in the MVP**, through the API and the CLI. It ends every
   session at once; a login afterwards is `403 no_membership`; the account stays so
   the person can be invited again.
4. **A role change reaches the member at their next refresh** (at most 10 minutes),
   ending no session. The frontend can refresh at once to make it immediate.
5. **Auth-Core is generic.** It serves many products; nothing product-specific is in
   its code. A product declares its permissions and **default roles** in its manifest;
   each new company gets a copy of the defaults and then manages its own roles
   through the API. This departs from design.md, where roles come from the manifest
   and an admin UI for roles is "Later".
6. **Roles are per company.** One company's edits never touch another's.
7. **Safety rules:** nobody grants a permission they do not hold; a company always
   keeps a member who can manage members; nobody changes their own role or removes
   themselves.
8. **The development default roles are `admin` (everything) and `user`.** The
   product's real roles are decided in its own integration slice.
9. **Inviting an address that belongs to another company looks like any other
   invitation** to the inviter; only the acceptance screen tells the invited person.
   **One acceptance screen** for everyone: an existing account without a company sets
   a new password there, as in a reset — the link proves ownership of the mailbox.
10. **A broken manifest never stops Auth-Core**: a product's mistake must not take the
    service down. The last valid manifest stays active and the health check reports
    the problem.
11. **Invitations last 7 days**; the company API can list, resend and cancel them; the
    role of a pending invitation cannot be changed.
12. **The company API checks permissions against the database** on every call and
    answers `403 permissions_changed` when the token is out of date, so the frontend
    knows to refresh. The product's own endpoints keep trusting the token, with the
    10-minute window of Decision 4.
13. **Defaults accepted with the design:** `roles` is an array with one entry; a
    company has only a name and cannot be deleted in the MVP; the CLI has the four
    commands above; `GET /auth/me` adds the email and the company name.
14. **The company API acts on the caller's own company** (`/auth/org/...`), not on a
    company id in the path as in design.md's sketch (`/auth/orgs/{id}/invites`): no
    request can name another company. Switching companies later keeps these paths —
    the token decides which company is the caller's.
15. **The manifest carries only permissions and default roles.** The application's
    name, locale and frontend URLs stay in configuration (spec 0004), and so do the
    audience and the clients. This departs from design.md's manifest example.
16. **The invitation mail limit counts per company and address**, unlike the
    per-address limit of spec 0004, so that companies on one instance cannot block or
    observe each other's invitations.

## Deferred / follow-ups

- **More than one company per user**, switching companies, and a company choice at
  login. The tables allow it.
- **Self-service sign-up** and **sending a verification mail** when it creates an
  account (spec 0004, Decision 2).
- **Leaving a company** on one's own, and **deleting a company**.
- **Pagination** of the member, invitation and role lists.
- **The audit log** of who invited, removed and changed what → week 6 (basic audit
  log, design.md).
- **Bringing changed default roles to existing companies.**
- **Immediate invalidation of access tokens** at the product's own endpoints, as in
  spec 0004.
- **Per-IP rate limiting** (spec 0003) also covers `preview` and `accept`.
- **The product's real roles** and its admin pages → the integration slices.

**Residual risks:**

- An attacker with a stolen invitation link can join the company with the invited
  role, as with a reset link. The link lives 7 days and only the newest one works.
- A manager can send up to five invitation mails per hour to any address. Only
  members can do it, and each mail names the company.
- The company and role names, set by members, appear in invitation mails. They are
  encoded and limited to 100 characters, but a hostile admin can still word them as
  they like.

## Verification notes (for the local verifiers)

Per [`docs/workflow.md`](../../workflow.md) — verifiers run locally before merge.

- **realization vs spec:** map each criterion to its guarding test. Criterion 8 needs
  a test comparing the complete responses for an address in another company and an
  unknown one. Criteria 6 and 16 need parallel requests against real PostgreSQL. The
  7-day lifetime and the claim refresh after a change are proven with the controlled
  clock.
- **API / e2e:** bring the stack up clean with the mail catcher and the development
  manifest. Drive the Goal sequence and the safety cases, taking each token from the
  mail as delivered. Re-run the four existing e2e scripts as the regression pass.
- **security:** confirm that no endpoint under `/auth/org` can read or change another
  company's data whatever ids it is given; that every company API check reads the
  database and not the token; that safety rule 1 cannot be bypassed through a role
  edit, an invitation or `*`; that the last-manager check holds under concurrency;
  that `invites` reveals nothing about addresses outside the caller's company; that
  invitation tokens are never stored or logged in clear; that names set by members are
  encoded in mails and kept out of headers; that a broken or hostile manifest cannot
  stop the host or grant a permission it does not declare.

## To verify during implementation

- How the service **validates its own access tokens** on the company API (OpenIddict
  validation in the same host, or the framework's JWT bearer handler), with the same
  rules a consumer applies.
- That **claims can be re-read on refresh** without breaking the session-stamp check
  of spec 0004, Decision 17.
- That **the last-manager check can be serialised per company** in PostgreSQL (a row
  lock on the company) inside the same transaction as the change.
- That **the invitation token fits the link-token store** of spec 0004, whose rows are
  one per user and kind, although an invitation may name an address without an
  account; or that it needs its own table.
- **New packages**, each needing the owner's approval before download: a YAML parser
  (design.md names YamlDotNet), a command-line parser (design.md names
  System.CommandLine; hand parsing of four commands is the alternative), the OpenAPI
  generator and the reference UI (design.md names Microsoft.AspNetCore.OpenApi and
  Scalar.AspNetCore), and possibly OpenIddict's validation package.
- That **the CLI runs from the same image** — the chiseled image has no shell — and
  reaches the database with the service's configuration.
- That the existing e2e scripts still pass once **the seed users are members** of the
  development company.
