# Spec 0009 — Self-service sign-up: a visitor creates a company

- **Status:** Accepted
- **Date:** 2026-03-28
- **Author:** Alex
- **Milestone:** Phase 2, slice 0009 — *"a visitor creates a company by mail and signs in"*
  ([`docs/design.md`](../../design.md), "Phase 2: sign-up and social login").
- **Context:** specs [0004](0004-email-flows.md) (mail queue, link tokens, mail limit, password policy, no
  enumeration), [0005](0005-tenancy-and-rbac.md) (companies, default roles, memberships, one company per person,
  invitations, the manifest), [0007](0007-angular-sample.md) (the sample frontend) and
  [0008](0008-hardening-and-release.md) (per-IP limits, audit log, threat model).

## Goal

A product can let a visitor start on their own: the visitor gives an email address and a company name, proves
the address through a mail link, chooses a password, and is then the first admin of a new company of their own.
From there everything works as in spec 0005: they invite colleagues, manage roles, and their tokens carry the
company.

Sign-up is off by default. A product turns it on in its manifest; an instance that does not turn it on behaves
exactly as `v0.1.0`.

Concretely, this is met when this sequence works against a running instance with a mail catcher and sign-up on:

```
curl -i .../auth/options                                                        # -> 200 {"signup":"self-service"}
curl -i -X POST .../auth/signup -d '{"email":"anna@firma.test","company_name":"Firma"}'   # -> 202
# the mail holds  <complete_signup url>?token=<t>
curl -i -X POST .../auth/signup/preview  -d '{"token":"<t>"}'                   # -> 200 {"email":"anna@firma.test","company_name":"Firma"}
curl -i -X POST .../auth/signup/complete -d '{"token":"<t>","password":"<p>","company_name":"Firma"}'  # -> 204
curl -i -X POST .../auth/login -d '{"email":"anna@firma.test","password":"<p>"}'  # -> 200, token: org_id of Firma, roles ["admin"]

# the same address again: the same 202, and a mail that says the account exists
curl -i -X POST .../auth/signup -d '{"email":"anna@firma.test","company_name":"Other"}'  # -> 202
```

## In scope

- `GET /auth/options`: what a product's screens may offer (sign-up on or off).
- `POST /auth/signup`, `POST /auth/signup/preview`, `POST /auth/signup/complete`.
- Pending sign-ups: stored apart from accounts, one per address, expiring after 24 hours, removed by the hourly
  clean-up.
- Two mails in Polish and English, HTML and text: "complete your sign-up" and "you already have an account".
- The manifest key `signup`; the configuration key of the completion screen's address.
- The first admin's role; the company's creation, the account and the membership in one transaction.
- Per-IP limits, the mail limit, audit events, OpenAPI, the threat model.
- `samples/notes-web`: the sign-up screen, the completion screen, the link on the sign-in screen, unit tests and
  Playwright tests; `samples/notes-api/auth.yaml` turns sign-up on.
- `scripts/e2e-signup.sh`; the integration guide, the deployment guide and the environment examples.

## Out of scope

- Sign-up through Google, Microsoft or GitHub (slice 0010).
- An account that exists but has no company (its company was deleted, or it was removed from it) cannot start a
  new company by sign-up; an invitation still brings it back (Decision 9).
- CAPTCHA or any other human check (Decision 11).
- Unique company names: two companies may have the same name, as with `create-org` today.
- Several companies per person.

## Contract

### General rules

- Every rule of specs 0004, 0005 and 0008 holds: JSON bodies of at most 8 KiB in UTF-8, unknown members ignored,
  `400 invalid_request` for a body that is not a JSON object or a required member that is missing, empty or not a
  string; `Cache-Control: no-store` on every answer; the security headers of spec 0008.
- **Sign-up is on** when the manifest says `signup: self-service` **and** the completion address
  (`Auth:App:FrontendUrls:CompleteSignup`) is configured. Otherwise it is off.
- While sign-up is off, `POST /auth/signup`, `/auth/signup/preview` and `/auth/signup/complete` answer
  `403 {"error":"signup_disabled"}` before reading the body. A request made while it was on and completed after it
  was turned off is refused the same way.

### `GET /auth/options`

`200 {"signup":"self-service"}` or `200 {"signup":"invite-only"}`, following the rule above. No authentication.
Slice 0010 adds a `providers` member; a client ignores members it does not know.

### `POST /auth/signup` — `{"email", "company_name"}`

1. Sign-up off: `403 signup_disabled`.
2. `email` must pass the address rule of invitations (spec 0005: a mailbox address, no look-alike, IP-literal,
   dotless, numeric or hexadecimal domain; at most 254 characters); `company_name` must pass the name rule of
   spec 0005 (1 to 100 characters, no control, format or noncharacters, no leading or trailing space). Otherwise
   `400 invalid_request`.
3. The mail limit of spec 0004 applies to the address with its own kind, `signup` (one request a minute and five
   an hour): over it, the `429 too_many_attempts` with `retry_after_seconds` of spec 0004.
4. Otherwise the pending sign-up of the address is created or replaced (its typed address and company name; it
   expires 24 hours later), a mail request is queued, and the answer is `202` with an empty body.

The request never reads the accounts: an address with an account and one without get the same status, headers and
body and run the same database statements. Which mail goes out is decided when the mail is sent.

### Mails

When the mail is sent:

- **The address has an account** (any account, confirmed or not, with or without a company): the mail "you already
  have an account" goes out. It has no link and no token; it says that someone, perhaps the reader, tried to sign up
  with this address, that the account exists, and that the reader can sign in or use "forgot password" on the
  product's sign-in screen. The pending sign-up is deleted.
- **Otherwise**, if the pending sign-up still exists: a new link token is issued for it (the previous link of the
  address stops working), valid 24 hours from the sending, and the mail "complete your sign-up" goes out with the
  link `<CompleteSignup>?token=<43 characters>` and the company name.
- If the pending sign-up is gone (expired, completed or replaced by an account), nothing is sent.

Both mails follow spec 0004: the instance's locale (`pl` or `en`), HTML and text parts, the app name, the queue,
its retries and its delivery rules. A failed sending leaves the previous link valid.

### `POST /auth/signup/preview` — `{"token"}`

`200 {"email", "company_name"}` for a usable token: the typed address and the company name of the request.
Otherwise `400 invalid_token`. It changes nothing.

### `POST /auth/signup/complete` — `{"token", "password", "company_name"}`

1. Sign-up off: `403 signup_disabled`. A missing or non-string member, a password with a NUL character or a company
   name that breaks the name rule: `400 invalid_request`.
2. A token that is unknown, already used, expired or replaced by a newer mail: `400 invalid_token`.
3. A password that breaks the policy of spec 0004: `400 weak_password` with the broken rules. The token stays
   usable.
4. An account with the address exists by now (an invitation was accepted in the meantime, for example):
   `409 {"error":"already_registered"}`. The pending sign-up is deleted.
5. Otherwise, in one transaction:
   - the account is created with the typed address, the password, and its email confirmed;
   - the company is created with the name given here (the name of the request is only a suggestion the person may
     change on the screen), with a copy of the manifest's default roles, as `create-org` does;
   - the account becomes the company's member with the first admin's role (below);
   - the pending sign-up is deleted;
   - the audit events below are written.

   The answer is `204`. It does not sign in: the product's screen sends the person to its sign-in screen.

Of parallel requests with one token, exactly one gets past step 2. The account's address is locked for the
transaction with the lock that invitation acceptance uses, so a sign-up and an invitation for one address never
both create an account.

### The first admin's role

The first default role of the manifest, in the manifest's order, whose permissions are `["*"]`; if no role holds
`*`, the first one whose permissions include `members:manage`. The manifest rule of spec 0005 guarantees that one
exists.

### Pending sign-ups

One per address (the normalised address of spec 0004). It holds the typed address, the company name, the hash of
its current link token (none until the first mail is sent), and the time it expires. It is not an account: login,
`forgot password` and invitations do not see it. The hourly clean-up of spec 0004 deletes pending sign-ups whose
time has passed.

### Manifest

A new optional top-level key: `signup: invite-only` (the default when the key is absent) or `signup: self-service`.
Any other value makes the manifest invalid, with the rules of spec 0005 (the last valid manifest stays active and
`/auth/health` reports `Degraded`). A manifest stored by an earlier version, without the key, is read as
`invite-only`.

### Configuration

| Key                                    | Environment variable (`deploy/docker-compose.prod.yml`) | Rule |
| -------------------------------------- | ------------------------------------------------------- | ---- |
| `Auth:App:FrontendUrls:CompleteSignup` | `AUTH_FRONTEND_SIGNUP_URL` (optional, may be empty)      | When set and not empty, the rules of the other frontend addresses of spec 0004 (absolute; `https` outside Development; no fragment, user information or `token` parameter); a bad value stops the start, naming the key |

When the manifest says `self-service` but the address is not set, sign-up is off, the start logs a warning, and
`/auth/health` reports `Degraded`.

### Limits

Per-IP (spec 0008): `POST /auth/signup` counts against the mail policy (`Email`, 10 a minute by default);
`/auth/signup/preview` and `/auth/signup/complete` against the invitation policy (`Invite`, 20 a minute);
`GET /auth/options` against the general one.

### Audit events

Two additions to the list of spec 0008 (24 kinds in all):

| Kind                | Written when                                                     | Fields |
| ------------------- | ---------------------------------------------------------------- | ------ |
| `signup.requested`  | `POST /auth/signup` is accepted (`202`)                          | `subject_email` |
| `signup.completed`  | `POST /auth/signup/complete` succeeds, in its transaction         | `subject_user_id`, `subject_email`, `org_id`, `org_name` |

and `org.created` gains the value `signup` for `details.via`, written in the same transaction.

### OpenAPI

The four operations are in the OpenAPI document with their answers.

### Sample frontend (`samples/notes-web`)

- **`/signup`**: email and company name; on success the text "Check your inbox" whatever the address; `429` shows
  the wait; `403 signup_disabled` says that sign-up is closed.
- **`/complete-signup`** (the mail link): the token leaves the address bar as on the other link screens (spec 0007);
  a preview shows the address (read-only) and the company name (editable); the person types the password twice
  (the two must be equal, checked on the screen); success sends them to `/login` with the address filled in and not
  in the URL. `invalid_token` offers to sign up again; `already_registered` says the account exists and links to
  sign-in; `weak_password` lists the rules.
- **`/login`** shows "Create an account" when `GET /auth/options` says `self-service`.
- The new routes are public. Texts are English, as in spec 0007.

## Acceptance criteria (Done when)

1. With sign-up on, `GET /auth/options` answers `200 {"signup":"self-service"}`; with the manifest key absent, or
   `invite-only`, or with the completion address not set, it answers `{"signup":"invite-only"}`.
2. With sign-up off, the three sign-up endpoints answer `403 signup_disabled` and create nothing; an instance with
   the `v0.1.0` manifest and configuration starts and passes its earlier tests unchanged.
3. `POST /auth/signup` for a new address answers `202` with an empty body; a mail "complete your sign-up" arrives
   whose link is the configured address with a `token` parameter, and whose text holds the company name.
4. The same request for an address with an account gets a response with the same status, headers and body and runs
   the same database statements in the request (the accounts are never read); the mail that arrives says the
   account exists and holds no link and no token; the pending sign-up is gone afterwards.
5. A request with a malformed address, an address the invitation rule refuses, or a bad company name is
   `400 invalid_request` and queues nothing.
6. The mail limit: a second request for one address within 60 seconds and a sixth within the hour are refused with
   the `429` of spec 0004; the `signup` limit and those of `forgot` and `verify/request` do not affect each other.
7. A second accepted request for one address replaces the first: the newer mail's link works and the older one is
   `invalid_token`, and the company name shown is the newer one.
8. `POST /auth/signup/preview` with the mail's token answers `200` with the typed address and the company name and
   changes nothing; an unknown, used or expired token is `400 invalid_token`.
9. `POST /auth/signup/complete` with the token, an acceptable password and a company name answers `204`; then the
   account exists with its email confirmed, the company exists with the given name and a copy of the default
   roles, and the account is its only member with the first admin's role; a login returns a token whose `org_id`
   is the new company and whose `roles` and `permissions` are those of that role.
10. The first admin's role is the first default role holding `*`; with a manifest where no role holds `*`, it is
    the first role holding `members:manage`.
11. A completion with a weak password is `400 weak_password` with the rules, creates nothing, and the token still
    works afterwards.
12. A token works once: a second completion is `400 invalid_token`; of parallel completions with one token exactly
    one is `204`, and exactly one account and one company exist.
13. A link older than 24 hours is `400 invalid_token`, and the hourly clean-up removes pending sign-ups whose time
    has passed.
14. When an invitation for the address is accepted between the request and the completion, the completion is
    `409 already_registered`, creates no company, and the pending sign-up is gone; a sign-up completion and an
    invitation acceptance running at once for one address never both create an account.
15. Pending sign-ups are invisible elsewhere: a login with the address is the `401` of an unknown address, and
    `forgot password` sends nothing.
16. The manifest accepts `signup: self-service` and `signup: invite-only`; any other value is refused by the rules
    of spec 0005 (last valid manifest kept, `/auth/health` `Degraded`); a stored manifest without the key reads as
    `invite-only`.
17. With `signup: self-service` and no completion address, the start logs a warning, `/auth/health` reports
    `Degraded`, and sign-up answers `403 signup_disabled`; a bad completion address stops the start naming the key.
18. The per-IP limits classify the four paths as written above.
19. The audit log holds `signup.requested` for an accepted request and, for a completion, `signup.completed` and
    `org.created` with `details.via` `signup`, in the completion's transaction; no row holds a password or a token.
20. The OpenAPI document lists the four operations with their answers.
21. Both mails exist in Polish and English, in HTML and text, and the "complete" mail's HTML and text hold the same
    link.
22. The sample: a person signs up on `/signup`, opens the mail link, completes on `/complete-signup` and signs in,
    and sees the new company and the admin role on the notes screen — in Chromium and in WebKit; the token leaves
    the address bar; the address never appears in a URL; the "already have an account" case and an expired link
    show their texts.
23. `scripts/e2e-signup.sh` passes against a fresh stack, and every earlier e2e script and test still passes.

## Decisions (owner, 2026-03-28)

1. **Sign-up creates a company of the visitor's own**, and the visitor is its first admin (phase 2 brief). Not a
   shared product-wide company.
2. **Off by default, turned on in the manifest** (`signup: self-service`), so a product decides, and an instance
   that does not opt in sees no change.
3. **The password is chosen at the link, not on the sign-up form.** The phase brief first put the password on the
   sign-up form. A password stored with a pending sign-up lets anyone sign up with someone else's address and their
   own password; if the owner of the address then clicks that link (expecting their own), the account is created
   with the stranger's password. Choosing the password on the completion screen, as an invitation does, closes
   that: only the reader of the mailbox ever sets it. The sign-up form keeps the email and the company name; the
   completion screen has the password, its repetition and the company name.
4. **The repeated password is checked on the screen only.** The API takes one password, as reset and invitation
   acceptance do.
5. **Login only after the link**: the account does not exist until the completion, which needs the mailbox.
6. **The same answer for every address**: the request never reads the accounts; an address with an account gets a
   mail saying so, with no link, so that the owner learns about the attempt and nobody learns who has an account.
7. **Pending sign-ups are not accounts**: a table of their own, one row per address, expiring after 24 hours. No
   unconfirmed account ever exists, so nothing has to tell such an account apart from a real one, and the clean-up
   deletes rows that nothing references.
8. **The first admin's role** is the first default role holding `*`, else the first holding `members:manage`.
9. **An existing account never signs up again**, even without a company: it gets the "you already have an account"
   mail. Letting it attach a new company would need a sign-in-or-reset path inside the sign-up; an invitation
   covers the case.
10. **No sign-in on completion**: the person signs in on the next screen, as after an invitation.
11. **No CAPTCHA.** Sign-up mails any well-formed address, so it is limited three ways: one mail a minute and five an
    hour per address, ten sign-up requests a minute per IP, and the invitation address rule, which refuses
    addresses that could make the mail relay deliver to an internal host. The remaining risk — a spread-out sender
    using many IPs to send a few mails each to many addresses — is recorded in the threat model.
12. **The completion address is optional configuration**; sign-up stays off without it, with a `Degraded` health,
    so that upgrading an instance never fails for a missing key.
13. **`GET /auth/options`** tells a product's screens whether to offer sign-up, so a product does not repeat its
    manifest in its frontend.

## Verification notes (for the local verifiers)

- Criteria 4 and 7 need the dispatcher run by hand (the `MailTestBase` pattern); criterion 13 a controlled clock;
  criteria 12 and 14 parallel requests; criterion 2 a host with the `v0.1.0` manifest and settings.
- The e2e script and the Playwright test need sign-up on: `deploy/auth.yaml` and `samples/notes-api/auth.yaml`
  turn it on, and the development settings and the sample's compose file set the completion address.

## To verify during implementation

- The mail queue mails only addresses with an account today: its "unmailable" clean-up and its composer need the
  `signup` kind, and the request has no account to point at. The pending sign-up plays the part the invitation
  plays for invitation mails.
- A stored manifest of `v0.1.0` (JSON without `signup`) must read as `invite-only`.
- The EF migration that adds the table.
- The order of checks in the completion (token, then password, then account) and the lock that invitation
  acceptance uses.
