# Spec 0004 — Email flows: password reset and email verification

- **Status:** Accepted
- **Date:** 2026-01-26
- **Author:** Alex
- **Milestone:** Week 2 (account lifecycle) — the email item: *"SMTP and templates:
  email verification, password forgot and reset."* Built in week 4.
- **Context:** [`docs/design.md`](../../design.md) (Tech stack, "Configuration
  gotchas", "Endpoint sketch", manifest example), ADR
  [0004](../../adr/0004-same-origin-cookie-refresh.md), and specs
  [0001](0001-login-and-token-issuance.md) (login),
  [0002](0002-refresh-and-logout.md) (sessions; Decision 11 defers "end every
  session on a password change" to this spec) and
  [0003](0003-lockout-and-abuse-resistance.md) (lockout; "As built" leaves the
  streak of a login refused after the password check to this spec).

## Goal

A user who forgot their password gets back into their account through a link sent
to their email address, and nobody else does. An account whose address is not
confirmed cannot log in until its owner clicks a link sent to that address. Neither
flow tells an outsider which addresses have an account — not by a response, and not
by its timing — and neither can be used to flood someone's mailbox.

This is also the first slice that sends email, so it builds the delivery path that
the invitations of week 3 will reuse.

Concretely, this is met when this sequence works against a running instance with a
mail catcher:

```
# ask for a reset link: the answer is the same for any address
curl -i -X POST .../auth/password/forgot -d '{"email":"user@example.com"}'   # -> 202

# the mail holds  <reset_password url>?token=<t>
curl -i -X POST .../auth/password/reset -d '{"token":"<t>","new_password":"<new>"}'  # -> 204

# the old password is gone, every earlier session is over, the new password works
curl -i -X POST .../auth/login   -d '{"email":"user@example.com","password":"<old>"}' # -> 401
curl -i -b jar.OLD.txt -X POST .../auth/refresh                                       # -> 401
curl -i -X POST .../auth/login   -d '{"email":"user@example.com","password":"<new>"}' # -> 200

# an account with an unconfirmed address
curl -i -X POST .../auth/login -d '{"email":"new@example.com","password":"<correct>"}' # -> 403
curl -i -X POST .../auth/email/verify/request -d '{"email":"new@example.com"}'         # -> 202
curl -i -X POST .../auth/email/verify -d '{"token":"<t>"}'                             # -> 204
curl -i -X POST .../auth/login -d '{"email":"new@example.com","password":"<correct>"}' # -> 200
```

## In scope

- `POST /auth/password/forgot` and `POST /auth/password/reset`.
- `POST /auth/email/verify/request` and `POST /auth/email/verify`.
- `POST /auth/login` **refuses an account whose email is not confirmed** (extends
  spec 0001; the lockout of spec 0003 is unchanged).
- **Single-use, expiring link tokens**, stored as hashes.
- **Mail delivery**: SMTP, a durable queue with retries, and four templates (reset
  and verification, each in Polish and English, each as HTML with a plain-text
  alternative).
- A **per-address limit** on the two endpoints that send mail.
- **Ending every session** of an account when its password is reset (picked up from
  spec 0002).
- A **password policy** for passwords set through the API.
- Request validation: an email that cannot be normalised is a `400` on login and on
  the two new endpoints that take an email (escalation E1 of slice 3).
- Mail settings in configuration, a second development seed user with an
  unconfirmed address, a mail catcher in the compose stack and an e2e script.

## Out of scope / Deferred

See [Deferred / follow-ups](#deferred--follow-ups). Not built here: sending a
verification mail automatically (there is no sign-up and no email change yet),
invitations, the `auth.yaml` manifest loader, the OpenAPI description, per-IP rate
limiting, a "change password while signed in" endpoint, and a notification mail
after a password change.

## Contract

All four endpoints are anonymous, accept `POST` only, and answer with
`Cache-Control: no-store` and `Pragma: no-cache`. None of them sets or clears the
refresh cookie.

**Request body rules** (the same as the login body of spec 0001): the content type
is `application/json`; the body is at most 8 KiB; it is a JSON object whose named
properties are non-empty strings. Anything else is
`400 {"error":"invalid_request"}`, as is:

- an `email` that contains a control character, a Unicode noncharacter or an
  unpaired surrogate, or that the host's email normaliser rejects (the same rule
  applies to login, see below);
- on the two endpoints that send mail, an `email` longer than 254 characters;
- a `new_password` that contains NUL.

A `token` that is a non-blank string is never an `invalid_request` for its
content: whatever it holds, if it is not a usable token it is an `invalid_token`.

### `POST /auth/password/forgot` — `{"email"}`

- `202 Accepted`, empty body, for every well-formed request within the limit —
  whether or not the address has an account.
- `429` when the address is over the [mail limit](#mail-limit).
- If the address has an account, a reset mail is sent to the account's address. The
  account's email does not have to be confirmed.

### `POST /auth/password/reset` — `{"token", "new_password"}`

- `204 No Content` when the token is usable and the password meets the
  [policy](#password-policy). See [Effects of a reset](#effects-of-a-reset).
- `400 {"error":"invalid_token"}` for a token that is unknown, used, expired or
  replaced by a newer one. The four cases are indistinguishable.
- `400 {"error":"weak_password","rules":[…]}` when the token is usable but the
  password is not acceptable. `rules` lists every rule the password breaks. The
  token stays usable, so the user can correct the password on the same screen.
- Checks run in this order: request shape, token, password.

### `POST /auth/email/verify/request` — `{"email"}`

- `202 Accepted`, empty body, for every well-formed request within the limit.
- `429` when the address is over the [mail limit](#mail-limit).
- A verification mail is sent only if the address has an account whose email is not
  confirmed. An unknown address and a confirmed account get the same `202` and no
  mail.

### `POST /auth/email/verify` — `{"token"}`

- `204 No Content` when the token is usable: the account's email is confirmed and
  its other verification tokens stop working.
- `400 {"error":"invalid_token"}` otherwise, as for a reset.

### `POST /auth/login` (changes to specs 0001 and 0003)

- A **correct** password for an account whose email is **not confirmed** is refused
  with `403 {"error":"email_not_verified"}`, the cache headers of the `401`, no
  token and no cookie.
- Nothing else about the order changes: the attempt is counted first, a running
  cooldown answers `429` before the account is looked up, and a wrong password gets
  the ordinary `401` whether or not the email is confirmed. Only someone who knows
  the password can learn that the address is unconfirmed.
- The refused login **ends the streak**, as a successful one does: the password was
  proven.
- An email that holds a Unicode noncharacter or an unpaired surrogate, or that the
  host's email normaliser rejects, is a `400 invalid_request` — on every host (it
  was a `500` on hosts with ICU and a `401` elsewhere). As before, a malformed
  request is not an attempt.

### Link tokens

- A token is 32 random bytes, sent as 43 characters of base64url. The link in a
  mail is the configured frontend URL with a `token` query parameter added.
- Only the SHA-256 of a token is stored. A token in clear exists in the mail and
  nowhere else — not in the database, not in the queue and not in the logs.
- A **reset token is valid for 1 hour**, a **verification token for 24 hours**,
  counted from the moment its mail is composed.
- A token works **once**. Of several concurrent requests with the same token, one
  succeeds.
- Composing a new mail for an account **replaces** the account's earlier tokens of
  the same kind: only the link in the newest mail works.

### Mail limit

The limit applies to `forgot` and `verify/request`, separately for each, per
submitted address — normalised as the account lookup normalises it, and counted
**whether or not the address has an account**, so that the limit says nothing about
account existence.

- At most **one accepted request per 60 seconds**.
- At most **five accepted requests per hour**: a fixed 60-minute window opens at an
  accepted request that falls outside any open window.
- A request over the limit is refused with
  `429 {"error":"too_many_attempts","retry_after_seconds":<n>}` and
  `Retry-After: <n>` — the shape of the lockout response of spec 0003. `<n>` is the
  time until a request would be accepted, in whole seconds, rounded up, at least 1.
- A refused request is not counted and does not extend anything.
- The limit is independent of the login lockout. A successful reset or verification
  does not clear it.

### Password policy

A password set through the API has **at least 8 characters, an uppercase letter, a
lowercase letter and a digit**. A non-alphanumeric character is not required.
Letters and digits of any script count: `Ż` is an uppercase letter. The
`rules` of `weak_password` are drawn from `too_short`, `requires_upper`,
`requires_lower` and `requires_digit`. The policy applies when a password is set;
it never blocks a login with an existing password. The new password may equal the
old one.

### Effects of a reset

A successful `POST /auth/password/reset`, as one unit:

1. sets the new password;
2. **ends every session of the account**: every refresh token issued before the
   reset is refused by `POST /auth/refresh` with the `401` of spec 0002 — and so
   is one issued by a login that verified the old password while the reset was
   under way;
3. ends the login streak of the account's address, so a running cooldown is gone;
4. confirms the account's email — the link came through that mailbox;
5. makes every other outstanding reset or verification link of the account unusable.

It does not sign the user in: the frontend sends them to the login screen. An
access token issued before the reset stays valid until it expires, at most
10 minutes: consumers verify tokens offline (design.md).

### Mails

- Two kinds — password reset and email verification — each in **Polish and
  English**. One language per instance, from configuration.
- Each mail is `multipart/alternative`: an HTML part with a button and the link
  written out, and a plain-text part with the same content.
- Content: the application's name, the link, how long the link is valid, and a
  sentence telling a recipient who did not ask for the mail to ignore it.
- The recipient is the address stored on the account, never the address as
  submitted. Nothing a requester submits appears in a mail.

### Delivery

- The request that asks for a mail only records the request and answers; the mail
  is composed and sent by a background process, normally within a few seconds.
- The record is **durable**: a mail requested before a restart is sent after it.
- A failed send is retried after 5 seconds, 30 seconds, 2 minutes and 10 minutes,
  then every 10 minutes. A request that could not be delivered within **1 hour** is
  dropped and logged as an error.
- Every attempt composes a fresh mail with a fresh token.
- Delivery is at-least-once: a crash between the SMTP server accepting a mail and
  the record being removed sends a second mail, whose link replaces the first.

### Configuration

| Key | Meaning |
| --- | --- |
| `Auth:App:Name` | Application name shown in mails |
| `Auth:App:Locale` | `pl` or `en` |
| `Auth:App:FrontendUrls:ResetPassword` | Absolute URL of the consumer's reset screen |
| `Auth:App:FrontendUrls:VerifyEmail` | Absolute URL of the consumer's verification screen |
| `Auth:Email:From` | Sender address |
| `Auth:Email:Smtp:Host`, `Port` | SMTP server |
| `Auth:Email:Smtp:Security` | `none`, `starttls` or `tls` |
| `Auth:Email:Smtp:Username`, `Password` | Optional credentials |
| `Auth:DevSeed:UnverifiedEmail`, `UnverifiedPassword` | Optional second seed user, `Development` only, created with an unconfirmed email |

The keys under `Auth:App` mirror `app.name`, `app.locale` and `app.frontend_urls`
of the manifest in design.md, so the manifest loader of week 3 fills the same
settings. A missing or invalid value stops the host at startup with a message that
names the key and never echoes a value. Outside `Development` the frontend URLs
must be `https` and `Smtp:Security` must not be `none`.

## Acceptance criteria (Done when)

1. `POST /auth/password/forgot` for an address with an account answers `202` with
   an empty body, and a mail arrives at the account's address whose link is the
   configured reset URL with a `token` parameter.
2. The same request for an address **without** an account gets a response with the
   same status, headers and body, sends no mail, and executes the same database
   statements in the request: the request never looks the account up.
3. `POST /auth/password/reset` with the token from the mail and an acceptable
   password answers `204`; afterwards a login with the old password is a `401` and
   a login with the new one succeeds.
4. After a reset, **every refresh token issued before it is refused** with the
   `401` of spec 0002 — for all of the account's sessions, not only the latest.
5. After a reset, a login cooldown that was running for the account's address is
   gone, the account's email is confirmed, and every other outstanding reset or
   verification link of the account is an `invalid_token`.
6. A link token works once. A token that is unknown, already used, older than its
   lifetime (1 hour for a reset, 24 hours for a verification) or replaced by a
   newer mail gets the same `400 invalid_token`. Of parallel requests with one
   token, exactly one succeeds.
7. A reset with a usable token and a password that breaks the policy answers
   `400 weak_password` listing each broken rule, changes nothing, and leaves the
   token usable.
8. The password policy is 8 characters, an uppercase letter, a lowercase letter and
   a digit; a password without a non-alphanumeric character is accepted, and so is
   one whose only uppercase (or lowercase) letter is not an ASCII one.
9. **Mail limit:** a second well-formed request for one address within
   60 seconds, and a sixth within the hour, are refused with the `429` contract
   above; the limit of `forgot` and that of `verify/request` do not affect each
   other; a refused request does not move `retry_after_seconds` up.
10. The `429` of criterion 9 is identical for an address without an account —
    nothing in criteria 1, 2 and 9 together distinguishes the two.
11. `POST /auth/email/verify/request` sends a verification mail only to an account
    whose email is not confirmed; a confirmed account and an unknown address get
    the same `202` and no mail.
12. `POST /auth/email/verify` with the token from the mail answers `204`, and the
    account can then log in.
13. A login with the **correct** password for an account whose email is not
    confirmed is a `403 email_not_verified` with no token and no cookie; with a
    wrong password it is the `401` of spec 0001; during a cooldown it is the `429`
    of spec 0003. The `403` ends the streak.
14. A mail requested before the host stops is sent after it starts again. A send
    that fails is retried on the schedule above, and dropped with an error log once
    the request is an hour old.
15. Each mail has an HTML and a plain-text part, is in the configured language,
    names the application, states the link's lifetime, and arrives with Polish
    diacritics intact. No token, link or mail body appears in the logs or in the
    database.
16. An email that holds a Unicode noncharacter (U+FFFE is the one that made the
    normaliser throw) is a `400 invalid_request` on `POST /auth/login`,
    `POST /auth/password/forgot` and `POST /auth/email/verify/request`, on every
    host — with or without ICU — and is not counted by the lockout or by the mail
    limit.
17. A host with a missing or invalid mail setting does not start; the error names
    the key and no value.
18. The e2e script drives both flows over the real network against the compose
    stack, reading the mails from the mail catcher's API, and the three existing
    e2e scripts still pass against the same stack.

## Decisions (owner, 2026-01-26)

1. **One spec for the mail infrastructure, forgot/reset and verification**, built
   in one week. The owner chose this over a smaller slice (forgot/reset only) and
   over two specs, knowing the estimate.
2. **Verification is triggered by a public "send again" endpoint**,
   `POST /auth/email/verify/request`. With `signup: invite-only` and no invitations
   yet, nothing creates an unconfirmed account except the second development seed
   user, which is temporary and goes away when invitations exist. **Target state:**
   accepting an invitation confirms the email by itself, with no second mail; a
   verification mail is sent automatically only where an address enters without
   proof of ownership (self-service sign-up, a change of email — both Beyond MVP);
   the "send again" endpoint stays for expired and lost links.
3. **An unconfirmed account is refused at login with a `403` of its own, after the
   password check.** Why not the uniform `401`: the user must learn why they cannot
   get in, and the frontend must know when to offer "send again". It is safe
   because the answer requires the correct password. The streak ends there, as on a
   success — this settles the question spec 0003 left open ("As built", known
   gaps).
4. **Link tokens are random values stored as hashes, not ASP.NET Identity tokens.**
   This departs from design.md, which planned Identity's token providers with Data
   Protection keys in the database (the "Key storage" row and the fourth
   configuration gotcha no longer apply). Why: a stored token is single-use and
   replaceable by construction, each kind has its own lifetime, the lifetime runs
   on the injected clock and is testable like the session lifetimes, and the link
   is 43 characters instead of several hundred. It needs no key ring, so a restart
   or a replica cannot break a link either. It is not a custom token format in the
   sense design.md rules out: the value carries no data and no cryptography of our
   own, exactly like the refresh token's reference.
5. **Mail requests are queued in the database.** The owner chose this over an
   in-memory queue: a mail must not be lost to a restart or to a mail-server
   outage, and invitations get the same guarantee for free.
6. **The mail limit is visible: a `429` with a timer**, the same contract as the
   lockout (spec 0003, Decision 4), not a silent drop behind a `202`. Numbers: one
   request per 60 seconds, five per hour, per address and per kind of mail. It is
   counted for every address, so it does not enumerate. design.md's "always 202"
   holds for every request the limit lets through.
7. **After a reset: every session ends, the lockout is lifted, the email is
   confirmed, and the user logs in on the login screen.** No automatic sign-in: it
   would issue tokens outside the token endpoint. Lifting the lockout is safe
   because only the mailbox owner can complete a reset, and without it an attacker
   who fails logins on purpose would keep the owner out even after a reset.
8. **Mails are HTML with a plain-text alternative, in Polish and English;** the
   application name, language, link targets and SMTP settings come from
   configuration until the manifest loader exists.
9. **Password policy: 8 characters, uppercase, lowercase, digit; no special
   character required.** It replaces the ASP.NET Identity defaults, which nobody
   had chosen.
10. **Escalation E1 of slice 3 is fixed here:** an email that cannot be normalised
    is a `400` on login as well as on the new endpoints.
11. **Lifetimes: 1 hour for a reset link, 24 hours for a verification link.**

Design choices that follow from the above, taken with the owner in the same
session:

12. **The request never looks the account up.** It validates, applies the limit,
    records "this kind of mail for this normalised address" and answers. Everything
    that depends on the account — whether it exists, whether its email is
    confirmed, creating the token, composing, sending — happens in the background.
    Why: a response whose work is the same for every address cannot leak existence
    through timing, whatever SMTP costs; equalising the timing of an in-request
    send was the alternative, and it is fragile. Accepted cost: the queue holds the
    normalised address until the entry is processed — seconds for an address
    without an account, and until delivery or the one-hour limit for an account.
13. **The token is created when the mail is composed, not when it is requested.**
    Why: otherwise the queue would have to hold the token in clear. A retry
    therefore sends a new link.
14. **The mail limit and the queue are tables of their own**, with the limit keyed
    on the SHA-256 of the normalised address like the lockout (spec 0003,
    Decision 10). design.md names the built-in rate limiter for this; it keeps its
    state in memory and partitions by what the request carries, so it would
    neither survive a restart nor share the lockout's no-enumeration property.
15. **Limits, lifetimes and the retry schedule are constants, not configuration** —
    the stance of specs 0001–0003.
16. **Which emails are refused is a rule of our own, not whatever the host's
    normaliser happens to reject.** Found while preparing the plan: with ICU the
    framework's normaliser throws for U+FFFE and for an unpaired surrogate; without
    ICU (the container image) it throws for nothing. A rule that follows the
    normaliser would make Decision 10 true on a developer machine and false in
    production. So an email holding a control character, a Unicode noncharacter or
    an unpaired surrogate is refused everywhere, and the normaliser's own refusal
    is kept only as a net.
17. **A session remembers the security stamp of its account, and a refresh is
    refused once the stamp has changed.** Found in the review of the plan:
    revoking an account's tokens ends the sessions that exist at that moment, but
    a login that checked the **old** password just before the reset committed
    issues its tokens just after — and someone who knows the old password can
    arrange that by logging in repeatedly while the owner resets. The stamp
    changes with the password, so such a session fails at its first refresh. This
    amends spec 0002, Decision 11 ("a refresh only checks that the user still
    exists") and adds one claim to the refresh token; the access token is
    unchanged.
18. **A link token is one row per account and kind.** A new token overwrites the
    row in one statement, so "only the link in the newest mail works" holds by
    construction, with any number of instances.
19. **Requests that will never become a mail are removed from the queue in one
    statement per pass**, before any mail is composed. Why: anyone can queue
    requests for addresses without an account; handled one by one they would
    stand in front of the mails that matter.
20. **"Uppercase", "lowercase" and "digit" mean any Unicode letter or digit**, not
    only A–Z, a–z and 0–9 (owner, on the plan's question). Why: ASP.NET Identity's
    built-in rule tells a user whose password is `Zażółć123Ż` that it has no
    uppercase letter, which is wrong to anyone who writes Polish.

The owner also accepted, on the plan's questions: mail texts kept in code rather
than `.resx` (the container image has no ICU, so a culture such as `pl` cannot be
created there); the queue row staying locked while its mail is sent; delivery
being at-least-once; and the three cases under "Accepted as they are" below.

## Deferred / follow-ups

- **Sending the verification mail automatically** when an account is created or its
  email changes → the specs that add those paths. Until then the only unconfirmed
  account is the development seed user.
- **Invitations** reuse the queue, the templates and the token store (week 3), and
  confirm the email on acceptance (Decision 2).
- **The manifest loader** fills `Auth:App:*` from `auth.yaml` (week 3).
- **The OpenAPI description** — the last open item of the week-2 milestone.
- **Immediate invalidation of access tokens** after a reset: they live up to
  10 minutes. Closing it needs a check the consumer makes per request, which
  design.md deliberately avoids.
- **A notification mail** to the account after its password changed, and a
  **change-password endpoint** for a signed-in user.
- **Per-IP rate limiting and trusted-proxy real-IP** (spec 0003, Decision 6) now
  also cover the four endpoints of this spec.
- **Deliverability** (SPF, DKIM, a real SMTP provider) → week 6, per design.md.
- **A shared base for the pruning services** (escalation E3 of slice 3): this slice
  adds another periodic clean-up in the existing pattern.

**Residual risks until per-IP limiting lands:**

- An anonymous client can make the service write two small rows per request, for
  any number of different addresses; queue rows for addresses without an account
  are removed within seconds, limit rows within two hours.
- A known address can be sent up to five reset mails and five verification mails
  per hour by anyone. Only the newest link of each kind works, and the mails say
  how to ignore them.
- Anyone can use up an address's hourly limit. The owner of the address still
  receives those mails, and the newest link works, so this does not keep them from
  resetting their password.

**Accepted as they are:**

- A reset mail that is being sent at the very instant another reset of the same
  account commits may leave a working link. It reaches only the mailbox owner.
- Two requests that change one account at the same instant with different tokens
  (a reset and a verification): one of them fails with a `500`, nothing is left
  half-done, and its token stays usable.
- While a mail for an account is being sent (at most 20 seconds), a click on the
  account's earlier link of the same kind waits for the outcome.

## Verification notes (for the local verifiers)

Per [`docs/workflow.md`](../../workflow.md) — verifiers run locally before merge.

- **realization vs spec:** map each criterion to its guarding test. The
  no-enumeration criteria (2, 10, 11) need tests that send the same request for an
  existing and a non-existent address and compare the complete responses.
  Lifetimes, the limit and the retry schedule (6, 9, 14) involve minutes to hours
  and are proven against real PostgreSQL with a controlled clock. Criterion 15
  needs at least one test through a real SMTP server.
- **API / e2e:** bring the stack up clean, with the mail catcher. Drive both flows
  end to end, taking each token from the mail as delivered. Confirm that a refresh
  cookie obtained before the reset is refused after it. Re-run the three existing
  e2e scripts as the regression pass.
- **security:** confirm that no clear token reaches the database, the queue or the
  logs; that the request path of `forgot` and `verify/request` has no branch on
  account existence; that the `403` cannot be obtained without the password; that
  a token cannot be used twice under parallel requests; that nothing submitted by a
  requester is placed in a mail or in a mail header; that the application name is
  encoded in the HTML part; that the SMTP password is never logged.

## To verify during implementation

- That OpenIddict can **revoke every token and authorization of one subject**, and
  that a refresh token revoked this way is answered with the same `401` as any
  other invalid refresh token.
- That the **effects of a reset can be one database transaction** across ASP.NET
  Identity, OpenIddict and the tables of this spec; if they cannot, the order that
  never leaves a usable token behind a changed password.
- That **consuming a token is atomic**: one statement that removes the row and says
  whether it was there.
- That **MailKit** sends a `multipart/alternative` mail with UTF-8 content through
  the mail catcher, and that the catcher's API returns both parts.
- That the background sender **works with the controlled clock** of the test host
  (the retry delays), and that a test can build a host without it.
- That **ASP.NET Identity reports each broken password rule** in a form that maps
  onto the four rule names, and that the development seed passwords satisfy the new
  policy.
- Whether any existing test relies on an **unconfirmed** user logging in, or on a
  password the new policy rejects.
