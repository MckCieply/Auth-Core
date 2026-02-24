# Threat model

Auth-Core is a self-hosted authentication service: one instance per product, behind one reverse proxy on the product's origin
([ADR 0001](../adr/0001-instance-per-project.md), [ADR 0004](../adr/0004-same-origin-cookie-refresh.md)). This is its threat model, made
with STRIDE for each element of the deployment. For every element it lists the threat, what stops it and where that is built, and the risk
that is accepted. The last section collects the risks that a spec accepted, with the decision that accepted each; the tables also name
risks of the trusted base (the host, the database, the relay) that no decision covers; they stay in the tables. It describes version
0.1.0 ([spec 0008](../superpowers/specs/0008-hardening-and-release.md)).

## What is protected, and from whom

| Asset | Why it matters |
| --- | --- |
| The accounts: passwords (hashed), addresses, memberships | Taking one over is taking over the person's place in a company |
| The sessions: the refresh cookie, the access token | The refresh cookie is the person for up to 14 days after its last use (a sliding window, capped at 30 days from the login); the access token is the person for 10 minutes |
| The signing key | Whoever holds it can make an access token for anyone |
| The company data: roles, members, invitations | A wrong role is a wrong permission in every product that trusts the token |
| The audit log | The only record of who did what |
| Availability of sign-in | A product that cannot sign people in is down |

The attackers considered: an anonymous person on the internet (guesses passwords, floods endpoints, sends hostile input); someone who has
a victim's mailbox for a moment, a stolen invitation link or a stolen refresh cookie; a member of a company who tries to reach beyond their
role or into another company; a malicious or compromised product backend or frontend; a person with read access to a backup. **Not
considered:** an attacker with root on the server (they hold the keys and the database), a compromised PostgreSQL or SMTP relay *operator*
beyond the listed effects, a nation-state, side channels of the hardware.

## The elements and their boundaries

```
 Browser / SPA ──https──> Reverse proxy ──http──> Auth-Core ──tcp──> PostgreSQL
 (product frontend)       (TLS, HSTS, headers,    (127.0.0.1:8080)      (private network, no published port)
                           client address)             │  └──smtp+TLS──> SMTP relay
 Product backend ──https GET /auth/.well-known/jwks.json──┘
 Operator ──docker compose run auth admin ...──> Auth-Core's database       Key files (read-only mount)
```

The boundaries: internet to proxy (TLS), proxy to Auth-Core (a trusted network, the client address is believed from there only), Auth-Core
to PostgreSQL and to the relay (private network; TLS to the relay), the host's file system to the container (keys, manifest; read-only).

## Browser and single-page application

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | Someone else signs in as the person with a stolen refresh cookie or access token | The cookie is `HttpOnly; Secure; SameSite=Strict; Path=/auth` ([spec 0002](../superpowers/specs/0002-refresh-and-logout.md), Contract); a rotated refresh token that is presented again after the 15-second leeway revokes the whole session (reuse detection, `RefreshReuseTests`; the leeway is exact: a replay at 15 seconds is a reuse, one a millisecond earlier is an honest retry, `AuditRefreshReuseTests`); it is audited as `refresh.reuse_detected`, and a failure of our own lookup for that row never changes OpenIddict's answer or stops the revocation; the access token lives 10 minutes and is held in memory only ([spec 0007](../superpowers/specs/0007-angular-sample.md), Session) | A just-rotated token stolen within the 15-second leeway can start a second chain that reuse detection does not see. A stolen cookie otherwise works until the 14-day window lapses, the 30-day cap ends the session, or the owner's next refresh makes the thief's next use a reuse |
| T | A script in the page reads or changes the token | The sample's Content-Security-Policy allows only the app's own scripts; no token is in storage, a cookie or a URL (`e2e/02-session.spec.ts`) | A token in memory can be read by script that runs in the page |
| T | Cross-site requests with the cookie | `SameSite=Strict`, same origin, a JSON-only API (a form post is `400`) | No CSRF token ([spec 0002](../superpowers/specs/0002-refresh-and-logout.md), Decision 6): `SameSite=Strict` on one origin is the whole defence |
| R | A person denies a sign-in or a deletion | The audit log records logins, failures, logouts, reuse, resets, invitations, role and member changes and company deletion, with the address ([spec 0008](../superpowers/specs/0008-hardening-and-release.md), Audit log) | Rows older than the retention (90 days by default) are pruned |
| I | The token or a link leaks through a URL, a log or the `Referer` | Links carry the token in the query only to the frontend, which reads it once and removes it from the address (`token-from-url.ts`); `Referrer-Policy: no-referrer`; mail tokens are stored hashed | A mail link in a mailbox is a credential for its lifetime |
| D | A person locked out by someone else | Per-identifier lockout with a cap of 30 minutes ([spec 0003](../superpowers/specs/0003-lockout-and-abuse-resistance.md), Decision 2) | An attacker can keep a chosen account locked: it costs one request per cooldown |
| D | The sample signs people out because the service is busy or down | A `429 too_many_requests` from any endpoint shows "Try again in N s." (the wait rounded up) and signs nobody out; a refresh answered `503` or `429` keeps the session (`too-many-requests.spec.ts`, spec 0008, Angular sample) | |
| E | A tab of a signed-out person stays usable | Access tokens expire in 10 minutes | Another tab stays usable for up to 10 minutes after sign-out |
| | Browser quirks on a phone | The proxy sends HSTS and the app's policy | Safari on a real phone may treat cookies differently from WebKit in Playwright ([spec 0007](../superpowers/specs/0007-angular-sample.md), Residual risks); to be tested on the first real deployment |

## Reverse proxy

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | A client forges `X-Forwarded-For` to hide, to frame another address, or to dodge the per-IP limits | The forwarded headers are read only from the proxies listed in `Auth:Proxy:KnownProxies` and `Auth:Proxy:KnownNetworks`; with none listed they are not read at all; every hop is walked and the client is the last address that is not itself a trusted proxy; `X-Forwarded-Proto` comes from a trusted proxy only and `X-Forwarded-Host` is never read (`ClientAddressSetupTests`, `RateLimitProxyTests`, `scripts/e2e-hardening.sh` step 2, `scripts/e2e-notes.sh` step 7, `scripts/e2e-prod.sh` step 4). The samples use a plain `reverse_proxy` and no `header_up`: Caddy 2.5 and later replaces `X-Forwarded-For` and `X-Forwarded-Proto` from an untrusted peer itself | A mistake in the list trusts too much: the deployment guide names the exact addresses |
| S | A mistyped list that trusts more than the operator wrote | Only plainly written entries are accepted, and the host refuses to start otherwise, naming the setting and never echoing the value: no short, hexadecimal or octal IPv4 form (`10.0.7`, `0x0a000007`, `010.0.0.7`), no IPv6 scope id, no IPv4-mapped IPv6 entry and no IPv6 range that holds the mapped range (`::/64`), no prefix length of 0, no network written with bits beyond its prefix (`10.250.0.5/24`); `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, which trusts every sender, stops the host (`ProxySettingsTests`, `ClientAddressGuardTests`) | An operator who trusts `0.0.0.0/1` and `128.0.0.0/1` trusts every IPv4 address: this misconfiguration is not refused |
| S | The production compose trusts the wrong sender | `deploy/docker-compose.prod.yml` trusts both bridge gateways (`10.250.0.1` and `10.250.1.1`) by default, because a proxy on the host reaches the published port from the gateway of whichever of the two networks Docker uses; an explicit blank `AUTH_PROXY_KNOWN_PROXIES=` trusts nothing, and leaving the variable out keeps the default; the subnets (`10.250.0.0/24`, `10.250.1.0/24`) lie outside Docker's default address pools | Which gateway a host proxy arrives from depends on the Docker engine; Docker Desktop is not a Linux VPS. `scripts/e2e-prod.sh` step 6 prints it and fails when it is neither gateway |
| T | Plain-HTTP downgrade | The proxy sends HSTS; the cookie is `Secure`; TLS ends at the proxy ([`vps.md`](../deployment/vps.md)) | The first visit before HSTS is cached is on the user's browser |
| I | The mail link's token is written to an access log | The sample's Caddy writes no access log; the integration guide (step 4) and the deployment guide say to keep the logs of `/reset`, `/verify` and `/invite` off, or to scrub `token=` ([spec 0007](../superpowers/specs/0007-angular-sample.md), As built) | A proxy or web server that logs the whole request line records a token that is still valid: such a log is as private as a password |
| I | Headers that reveal or help the attacker | `Server` is off; every answer carries `nosniff`, `X-Frame-Options`, a restrictive policy, `no-referrer`, `Cross-Origin-Resource-Policy`, `no-store`; this holds for the `500` of an unhandled exception, for a request the server rejected while its body was read (`invalid_request` with the server's own status; in practice the app's 8 KB body cap answers `400` first) and for the framework's `404` and `405` (`SecurityHeadersTests`, `ErrorHandlingMiddlewareTests`, `scripts/e2e-hardening.sh` step 1) | Kestrel's own errors before the pipeline (a malformed request line, headers too large) cannot carry the headers |
| D | Floods | Per-IP limits (30 logins, 60 refreshes, 10 mail requests, 20 invitation requests and 300 other requests a minute) answered `429` before any work is done (`RateLimitMiddlewareTests`); the limiter counts on the monotonic clock of the injected `TimeProvider`, so a step of the wall clock neither blocks nor frees a client (`SlidingWindowLimiterTests`); an IPv6 client is counted by its `/64`, so one subscriber cannot spread over its addresses | Many people behind one address share its limits; 30 logins a minute is the ceiling for an office behind one NAT. A distributed guesser below the limits on many addresses is stopped only by the per-identifier lockout; an attacker who holds several `/64` networks has a set of limits for each. The counters live in memory (one instance per product): a restart clears them, so whoever can make the service restart gets a fresh allowance |
| E | The proxy is bypassed: Auth-Core's port reached directly | The published port is on `127.0.0.1` only (`docker-compose.prod.yml`) | A process on the host reaches the port from a gateway address, which is trusted by default, so it can write any client address into `X-Forwarded-For`; a request that gets that far is taken as it comes |

Not applicable: R (the proxy is not where actions are recorded: Auth-Core's audit log records them with the client address the proxy forwards, and the sample's Caddy writes no access log).

## Auth-Core

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | Password guessing; account enumeration | Identical `401` for unknown and wrong; a decoy hash so both take about as long (close enough, not constant time: the medians are within 0.5 to 2 times of each other; [spec 0003](../superpowers/specs/0003-lockout-and-abuse-resistance.md), Decisions 5 and 12, `LoginTimingTests`); lockout per identifier ([spec 0003](../superpowers/specs/0003-lockout-and-abuse-resistance.md)); per-IP limits; the mail endpoints answer alike for every address ([spec 0004](../superpowers/specs/0004-email-flows.md), Decision 12) | A prober can see the login streak of an address reset by its owner's login (about eleven requests per probe). A correct password given during a cooldown extends it by a minute, like a wrong one, so an impatient person lengthens their own lock (Decision 7) |
| S | Forged access token | RS256 only; `typ at+jwt`; audience and issuer checked; the key set is the instance's own; the package refuses other algorithms ([spec 0006](../superpowers/specs/0006-python-consumer-package.md), Contract) | A product that sets `jwks_url` to an address an attacker controls accepts the attacker's tokens |
| T | Hostile input: oversize bodies, NUL, malformed JSON, a body in another charset, look-alike or internal-host addresses | A body is at most 8 KiB and a JSON object; a charset other than UTF-8 is `415` (`JsonCharsetTests`); NUL and control characters are refused; invitations to addresses that may stand for another account, whose domain is an address (`127.0x1`, `host.123`) or has no dot are refused in the API and the CLI (`InvitationDomainTests`); role and company names refuse format characters ([spec 0005](../superpowers/specs/0005-tenancy-and-rbac.md), As built; [spec 0008](../superpowers/specs/0008-hardening-and-release.md), Fixes) | A fullwidth or Cyrillic look-alike of an address can still be invited: it normalises to another address and never reaches the account it imitates |
| T | A member reaches beyond their role or another company | Permissions are read from the database on every call, never from the token; nobody grants, edits, removes or deletes more than they hold (rule 1); the company lock serialises changes; every company query is scoped to the caller's company ([spec 0005](../superpowers/specs/0005-tenancy-and-rbac.md), Decisions 7, 12, 17 and 19) | A removed or demoted member keeps the token's permissions at a product backend for up to 15 minutes (10 minutes of life, 5 of clock skew) |
| T | Deleting a company by mistake or by a hijacked session | `DELETE /auth/org` needs the permission `org:delete`, the company's exact name and the caller's password, and the password counts in the caller's login streak like a failed login (`OrgDeleteEndpointTests`); rule 1 over every member; the deletion runs in one transaction under the company lock and revokes every member's sessions (`CompanyDeletionTests`); the CLI needs the exact name (`DeleteOrgCommandTests`) | A company admin holding every permission can delete the company with their password; there is no undo but the backup |
| R | Actions that leave no trace | The audit log: 22 kinds, never holding a secret (`AssertNoSecretsAsync`, `scripts/e2e-hardening.sh` step 6). A change and its row are written in the same transaction (`AuditAtomicityTests`): `login.succeeded` and `logout` share the transaction of the session change, so when the row cannot be written the login or logout fails with `500` and no session is issued or ended (`AuditAccountFlowsTests.A_login_whose_row_cannot_be_written_issues_no_session`). An event that changes nothing (a failed login, a rate-limit hit, a reuse) is written on its own, and that write never throws except for the caller's own cancellation: on a failure EF Core logs its own error lines and one `AuditLog` warning, and the request is answered as usual (`AuditLogTests`). The pruner keeps a row at exactly the retention ("older than") (`AuditPruningTests`). `rate_limit.hit` is written at most once per partition and policy per minute, so at most one row per `/64` per policy per minute (`AuditRateLimitTests`) | Anyone can create `login.failed` and `password.reset_requested` rows; only the per-IP limits and the retention bound the table. The address typed into a failed or locked login is stored as typed and may be a password pasted into the wrong field; it is kept for the retention period. A failed audit write for an event on its own leaves a gap that only the log shows |
| R | A login to an account with no password yet (an invited account that has not accepted) | It is recorded as `login.failed` with the reason `unknown_address`, like an address nobody has, and takes as long (`AuditAccountFlowsTests`) | |
| I | Secrets in logs, answers or the audit log | Errors name settings, never values; the CLI prints no connection string and no exception text; an unhandled exception is `500 {"error":"internal_error"}` with no detail, in every environment (the developer exception page included), and is only logged (`ErrorHandlingTests`); the audit log holds no password, token, link, cookie or mail body | |
| I | Reset and invitation links in the wrong hands | Tokens are random, stored hashed, single-use, newest-only; a link works for the mailbox it was sent to ([spec 0004](../superpowers/specs/0004-email-flows.md), [spec 0005](../superpowers/specs/0005-tenancy-and-rbac.md)) | A stolen invitation link can join the company with the invited role (7 days; only the newest link works). A reset mail requested before a reset still goes out after it, with a new working link that reaches only the mailbox owner. A known address can be sent up to five reset and five verification mails an hour by anyone; only the newest link of each kind works |
| D | Resource exhaustion through the database or the mail queue | A request over the per-IP limit does no work; every attempt is one small write; a pass of the dispatcher first removes, in one statement, the requests that need no mail (an address without an account) within seconds, and drops a request an hour old at the first pass after it; link and limit rows are pruned | Every login attempt, refused ones included, is one write, and an unknown address costs one hash; parallel attempts for one identifier hold pooled connections for milliseconds, and a flood on one identifier can exhaust the pool |
| D | The database is down | A refresh answers `503 temporarily_unavailable` and keeps the session, for a transient database failure (PostgreSQL's `57P03`, starting up, and `57P01`, admin shutdown, included), a timeout or a socket error at any depth (`RefreshOutageTests`, `TransientFailureTests`); the health check does not reach the database ([spec 0008](../superpowers/specs/0008-hardening-and-release.md), Refresh outage) | A refresh interrupted between marking the old token redeemed and storing the new one is usable for the 15-second leeway only; retried later it is reuse and ends the session. Login, logout and the company API answer `500` |
| D | Concurrent changes of one account | Everything that changes a company runs under its lock; link use is one `DELETE ... RETURNING` | Two requests that change one account at the same instant with different tokens: one can fail with a `500`, nothing is left half-done and its token stays usable; a click on an earlier link waits up to 20 seconds while a mail for the account is sent; cancelling an invitation whose mail is being sent waits for the send, holding the company lock, and the other changes of that company wait with it |
| E | A session survives its password | A password change ends every session and bumps the security stamp ([spec 0004](../superpowers/specs/0004-email-flows.md), Decision 17) | A login or refresh that overlaps a reset gets a refresh token that is refused at once, but its access token lives up to 10 minutes; a verification racing a reset of the same account can deadlock in PostgreSQL and one of the two gets a `500` |
| E | A session survives its logout, or its company | Logout always completes once the cookie is read, even if the client disconnects, in the transaction of its row (`AuditAccountFlowsTests`); the members of a deleted company are treated as removed members: `403 permissions_changed` at the company API, `401 invalid_grant` at refresh, `403 no_membership` at login (`OrgDeleteEndpointTests`) | Issued access tokens stay valid until they expire, up to 10 minutes |
| E | Privilege through the container | Non-root user, read-only root file system, no capabilities, no privilege gain (`docker-compose.prod.yml`, proven by `scripts/e2e-prod.sh` step 2); the bind mounts do not create a missing host path, so a wrong path stops the start instead of leaving the service on the built-in manifest or without keys; the SMTP credentials must be present in `.env` (blank means a relay without authentication, not a forgotten value) | |

## PostgreSQL

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | Another container or host connects as `auth` or as Auth-Core's role | No published port; a private network; a long random password for each role from `.env`; Auth-Core's role may connect to its own database only (`CONNECT` is revoked from PUBLIC on it and on the maintenance databases, `deploy/postgres-init/10-auth-app-role.sh`) | The passwords are in the containers' environment. Inside the `postgres` container the local socket and the loopback addresses trust every role (which is why the operator's commands need no password): a process in that container is part of the trusted base |
| T | A stolen or tampered database | Passwords are hashed (ASP.NET Identity); link tokens and refresh tokens are stored as hashes or protected payloads | Anyone with write access to the database can make themselves a member of any company; PostgreSQL is part of the trusted base |
| R | Rows removed to hide actions | Deleting an audit row needs database access | The audit log is not tamper-evident |
| I | Addresses in the mail queue | A request is queued with the normalised address and nothing else; the row is removed within seconds when no account has the address, and at delivery or when the request is an hour old otherwise ([spec 0004](../superpowers/specs/0004-email-flows.md), Decision 12) | Whoever reads the database sees the addresses of the requests that are pending |
| I | A backup read by the wrong person | The runbook keeps the dump apart from the key files, and says to encrypt it off the host ([`backup.md`](../operations/backup.md)) | A dump holds every account's password hash |
| D | The disk fills, the server is lost | Pruning of expired rows, the audit log by its retention; a nightly dump and a restore that is tried ([`backup.md`](../operations/backup.md), `scripts/e2e-prod.sh` step 5) | Whatever happened after the last backup is lost, a deleted company included |
| E | Code in Auth-Core runs arbitrary SQL | EF Core parameterises every query; the few raw statements (`FromSql`, `ExecuteSqlAsync`, `SqlQuery`, all with interpolated values, which EF sends as parameters) never concatenate input into the text; **Auth-Core connects as a role of its own that is not a superuser** (none of `SUPERUSER`, `CREATEROLE`, `CREATEDB`, `REPLICATION`, `BYPASSRLS`; spec 0008, Decision 14; `scripts/e2e-prod.sh`, `check_app_role`, shows the attributes and that the role cannot connect to the maintenance database, run `COPY ... TO PROGRAM` or make a role, after the first start and after a restore) | A flaw that ran SQL reaches everything that role owns, which is all of Auth-Core's data (accounts, password hashes, sessions, companies, the audit log), but not the rest of PostgreSQL and not the host: it cannot run a program in the postgres container |

## SMTP relay

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | A mail that pretends to come from the product | The sender is a setting; the relay must offer TLS and the service refuses `none` outside Development (`scripts/e2e-prod.sh` step 1) | The product's domain needs SPF, DKIM and DMARC on its own DNS: outside this repository |
| T | A mail read or changed on the way | STARTTLS or TLS is required, the certificate is validated, its revocation status included (MailKit's default) | The relay sees the mail, links included |
| I | Names and mail content | Company and role names are encoded and limited to 100 characters | A hostile admin can word a company or role name as they like; every mail names the company |
| D | The relay is down | Mail goes through a queue with retries; the request is answered `202` at once (`MailDispatcherTests`) | A mail may arrive late. Delivery is at-least-once: a retry after a failure that came after the relay took the mail sends a second mail, with a new link ([spec 0004](../superpowers/specs/0004-email-flows.md), Decision 13) |

Not applicable: R (the relay's own delivery log belongs to its operator; Auth-Core audits the invitation, `invite.sent`, and never a mail body or a link), E (Auth-Core only connects out to the relay and takes nothing from it but an accepted or a refused send).

## Key files

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| I | The signing or encryption key is read | Read-only mount, a directory of its own, a mode that only the container's user can read ([`key-rotation.md`](../operations/key-rotation.md)); RSA of at least 2048 bits is enforced at start (`KeyMaterialTests`) | Anyone who reads the signing key can make tokens until the key is rotated |
| T | A key replaced by an attacker | Write access to the host is root | |
| D | A key lost or rotated | The service refuses to start without its keys; keys are backed up apart from the dump | A key change signs everyone out (Decision 7 of spec 0008): no previous keys are kept for verification or decryption |
| D | A certificate lapses | The runbook generates certificates valid for ten years and says to note the date: OpenIddict refuses to work when no certificate is within its dates, most likely as a `500` on the first login with its message in the log | After the date the service cannot issue or read tokens until new certificates (or new keys) are in place |

Not applicable: S (a file cannot be impersonated; a replaced file is the T row), R (a key file records nothing; sign-ins are in the audit log), E (a file has no authority of its own; what a reader of the signing key can do is the I row).

## Product backend

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | A token for another product or audience | The audience is checked; one instance per product ([ADR 0001](../adr/0001-instance-per-project.md)) | |
| T | A backend that trusts an attacker's key set | The package takes the key set from a configured URL only, without proxy or redirect, size and time limited | A product that sets `jwks_url` to an address an attacker controls accepts the attacker's tokens: use an internal or HTTPS address |
| I | A query without the company | The package gives `org_id`; the sample filters every query by it and has tests for it ([spec 0006](../superpowers/specs/0006-python-consumer-package.md)) | A product that forgets the filter leaks across companies: the guide says so in step 5 |
| D | Auth-Core unreachable | The package keeps the keys it holds for 24 hours and answers `503 auth_unavailable` when it has none | A key that Auth-Core has removed or rotated still works at a product backend until its next successful fetch, and for at most 24 hours while Auth-Core is down ([spec 0006](../superpowers/specs/0006-python-consumer-package.md), Decision 10) |
| E | A demoted member's token | Short life | 15 minutes at the product's endpoints at the extreme |

Not applicable: R (what a product's backend does is the product's to record; Auth-Core records its own flows, and the token names `sub` and `org_id` for the product's log).

## Operator CLI

| | Threat | Mitigation | Residual |
| --- | --- | --- | --- |
| S | Someone else runs it | It needs access to the host and the compose project: root-equivalent | |
| T | A command by mistake | `delete-org` needs the exact name; `remove-member` refuses the last manager without `--force`; every command is recorded with `"via": "cli"`, and a forced removal also with `"forced": true` (`AuditMemberFlowsTests`); the development seeder's company is recorded with `"via": "seed"` (`AuditCompanyFlowsTests`) | The operator is not bound by rule 1 |
| I | Secrets in its output | It prints no connection string or exception text, only `error: <code>` or `error: failed (<exception type>)` and the service's own warnings | |
| E | The CLI used to bypass the API's rules | It is the operator's own tool; the same services and rules apply except rule 1 | |

Not applicable: R (every command is recorded with "via": "cli"), D (the CLI is run by the operator, one command at a time; nobody else can reach it, and a failing command costs one run).

## Residual risks

Accepted, with the decision that accepted each. None of them is hidden by a mitigation above. Items 1 to 45 are the risks accepted by specs 0002 to 0008 (41 to 44 by Decision 15; 45 is what Decision 14 leaves).

1. **A key change signs everyone out.** No previous keys are kept for verification or decryption; rotation is a runbook (spec 0008, Decision 7).
2. **A distributed guesser below the per-IP limits** on many addresses is stopped only by the per-identifier lockout, which an attacker can use to keep a chosen account locked, at one request per cooldown (spec 0003, Decision 8 and Deferred / follow-ups; spec 0008, Residual risks).
3. **Many people behind one address share its limits;** 30 logins a minute is the ceiling for an office behind one NAT (spec 0008, Residual risks).
4. **A company admin holding every permission can delete the company** with their password; there is no undo but the backup (spec 0008, Decision 8 and Residual risks).
5. **A refresh interrupted by an outage** between marking the old token redeemed and storing the new one can be used for the 15-second leeway only; retried later it ends the session, and is recorded as `refresh.reuse_detected`; after an outage such rows are not evidence of theft (spec 0008, Refresh outage and Residual risks).
6. **The address typed into a failed login** (and into a locked one) is stored as typed and may be a password pasted into the wrong field; it is kept for the retention period (spec 0008, Residual risks).
7. **Anyone can create `login.failed` and `password.reset_requested` rows;** only the per-IP limits and the retention bound the table (spec 0008, Residual risks).
8. **The login streak reset a prober can observe** (about eleven requests per probe; it shows that the account exists and that its owner logged in) (spec 0003, As built, residual risks found in verification, escalation E2; accepted in spec 0008, Decision 3).
9. **Fullwidth and Cyrillic look-alike invitation addresses** can still be invited; they normalise to other addresses and never reach the account they imitate (spec 0005, As built, residual risks found in verification; accepted in spec 0008, Decision 3).
10. **A just-rotated refresh token stolen within the 15-second leeway** can start a second chain that reuse detection does not see (spec 0002, Decision 2 and As built, known gaps). A stolen refresh cookie is otherwise bounded by the 14-day sliding window and the 30-day cap, not removed (spec 0002, Decision 3).
11. **No CSRF token;** `SameSite=Strict` on one origin is the defence (spec 0002, Decision 6).
12. **Issued access tokens stay valid until they expire,** up to 10 minutes after logout, a password change or a removal (spec 0002, Decision 14; spec 0004, Deferred / follow-ups).
13. **A demoted or removed member keeps the token's permissions at a product's endpoints** for up to 15 minutes: the 10 minutes of the token and the 5 of clock skew the package allows (spec 0005, Decisions 4 and 12; spec 0006, Decision 9 and Residual risks).
14. **A product that sets `jwks_url` to an address an attacker controls** accepts the attacker's tokens (spec 0006, Residual risks).
15. **Another browser tab stays usable** for up to 10 minutes after sign-out: open tabs are not synchronised on sign-out, and a follow-up is named in the spec (spec 0007, Session, Residual risks and Deferred / follow-ups).
16. **A token in memory can be read by script running in the page;** the policy limits scripts to the app's own files (spec 0007, Residual risks).
17. **Safari on a real phone** may treat cookies differently from WebKit in Playwright; the test on a real phone is part of the first real deployment (spec 0007, Decision 3, Residual risks and Known limits).
18. **Every login attempt costs a database write and an unknown address one hash;** a flood on one identifier can exhaust the pool (spec 0003, As built, residual risks found in verification).
19. **Parallel correct-password logins:** six or more at the same instant for one identifier, the later ones are refused by the burst rule (spec 0003, Decisions 9 and 14 and As built).
20. **The decoy hash follows the current hasher settings;** after a settings change, accounts with older hashes cost differently until their next login (spec 0003, Decision 11 and As built, residual risks found in verification).
21. **Timing equalisation is close enough, not constant time:** over the network the median of an unknown-address login is within 0.5 to 2 times that of a wrong-password login (spec 0003, Decisions 5 and 12).
22. **A correct password given during a cooldown extends it** by a minute, like a wrong one, so that the answer does not tell a guesser whether the password was right; an impatient person lengthens their own lock (spec 0003, Decision 7).
23. **Mail limits and queue rows:** an anonymous client makes two small rows per request for any address (removed within seconds and two hours); a known address can be sent five reset and five verification mails an hour by anyone, and anyone can use up an address's hourly limit; only the newest link of each kind works and the owner still receives the mails (spec 0004, Deferred / follow-ups, residual risks until per-IP limiting lands; the per-IP limits of spec 0008 now bound the requests too).
24. **The mail queue holds the normalised address** of a request until it is processed: seconds for an address without an account, and until delivery or the one-hour limit for an account (spec 0004, Decision 12).
25. **Mail delivery is at-least-once:** a retry after a failure that came after the relay accepted the mail sends another mail, and every retry composes a new link (spec 0004, Decision 13 and the questions the owner accepted on plan 0004).
26. **Races accepted as they are:** a reset mail being sent at the instant another reset of the account commits may leave a working link that reaches only the mailbox owner; two requests that change one account at the same instant with different tokens: one fails with a `500`, nothing is left half-done and its token stays usable; a click on an earlier link waits up to 20 seconds while a mail for the account is sent (spec 0004, Deferred / follow-ups, "Accepted as they are").
27. **Races found in verification:** a reset mail requested before a reset still goes out after it, with a new working link (it reaches only the mailbox owner); a verification racing a reset of the same account can deadlock in PostgreSQL, one of the two gets a `500`, nothing is left half-done and its token stays usable (spec 0004, As built, residual risks found in verification).
28. **A login or refresh that overlaps a reset** gets a refresh token that is refused at once, but its access token lives up to 10 minutes (spec 0004, Decision 17 and As built, residual risks found in verification).
29. **A stolen invitation link** can join the company with the invited role: it lives 7 days and only the newest works (spec 0005, Residual risks).
30. **The invitation mail limit is per company and address,** so a manager can mail any number of distinct addresses, each with the company's own wording of its name and role; the names are set by members, appear in the mails, are encoded and limited to 100 characters, and a hostile admin can still word them as they like; each mail names the company (spec 0005, Decision 16 and Residual risks).
31. **An invitation reaches an existing account only under that account's own spelling** (apart from the case of `A` to `Z`): an account spelled `Żaneta@...` is not reached by an invitation to `żaneta@...`, and the inviter types the address as the account spells it (spec 0005, As built, behaviour added after verification).
32. **Cancelling an invitation** whose mail is being sent waits for the send (at most 20 seconds) while it holds the company's lock, and the other changes of that company wait with it (spec 0005, As built, residual risks found in verification).
33. **Package limit:** an asynchronous exception (a gevent or eventlet timeout) delivered between marking a fetch as running and the `try` that ends it can leave the key cache marked as fetching; while the cache is warm, a request that lacks its key is then answered `503` at once (spec 0006, As built, known limits).
34. **The refresh cookie is `Secure`,** so on a plain-HTTP origin other than `localhost` browsers drop it; HTTPS comes with the real deployment (spec 0006, As built, known limits).
35. **Angular, its CLI and its build stay at 21.1.4 with their known advisories:** six high advisories against the runtime packages (cross-site scripting through i18n bindings, sanitisation bypasses, denial of service in pipes and server-side rendering, leaks of the transfer cache), none reachable by what the sample uses except the date pipe, used with a fixed format and a validated date, and advisories in the development tooling that never reach the build; the content-security-policy is the second line. A product that copies the sample moves to a fixed release (spec 0007, Decision 13 and As built, known limits).
36. **An access log records the mail link's `?token=` query** unless it is turned off or scrubbed for `/reset`, `/verify` and `/invite`; a token that is still valid in a log is a way into someone's account (spec 0007, As built, the guide's step 4; `docs/integration/angular.md`).
37. **Kestrel's answers before the pipeline** (a malformed request line, headers too large) cannot carry the security headers (spec 0008, Unhandled errors).
38. **The per-IP counters are in memory:** a restart clears them (one instance per product, ADR 0001; spec 0008, Per-IP rate limiting).
39. **The audit log is not tamper-evident** and is bounded by the retention; it is read by SQL (spec 0008, Decision 5 and Deferred / follow-ups).
40. **A mail link in a mailbox** is a credential for its lifetime; whoever reads the mailbox can use it: a reset link lives 1 hour and a verification link 24 hours (spec 0004, Decision 11), an invitation link 7 days (spec 0005, Decision 11).

41. **An operator who trusts `0.0.0.0/1` and `128.0.0.0/1`** trusts every IPv4 address, and every client can then choose its own address; this misconfiguration is not refused (spec 0008, Client address and trusted proxies; `ProxySettings` refuses the other mistakes); accepted by Decision 15 (spec 0008).
42. **Which gateway a proxy on the host arrives from depends on the Docker engine,** so the default of `AUTH_PROXY_KNOWN_PROXIES` (both gateways) fits a Linux VPS and is checked there by `scripts/e2e-prod.sh` step 6; Docker Desktop is not a Linux VPS and may deliver the connection from another address (spec 0008, Production compose); accepted by Decision 15 (spec 0008).
43. **A process on the host can choose its client address:** it reaches the published port from a trusted gateway, so its `X-Forwarded-For` is believed (spec 0008, Production compose; the host is part of the trusted base); accepted by Decision 15 (spec 0008).
44. **A certificate lapses** (the generated ones after ten years): OpenIddict refuses to work when no certificate is within its dates, most
    likely as a `500` on the first login, and the service cannot issue or read tokens until new certificates (or new keys) are in place; new keys sign everyone out (whether new certificates for the same keys would is untried) (spec 0008, Decision 7; `docs/operations/key-rotation.md`). Accepted by Decision 15 (spec 0008).
45. **Auth-Core's database role owns all of Auth-Core's data.** A flaw that ran SQL reaches the accounts, the password hashes, the sessions, the companies and the audit log, but not the rest of PostgreSQL and not the host (spec 0008, Decision 14).

### Accepted earlier and closed by spec 0008

These items of specs 0003 to 0005, and one found while building spec 0008, no longer stand as they were written; they are named so that none is lost.

- Per-IP rate limiting and the trusted-proxy rule, deferred by spec 0003 (Decision 6), are built (spec 0008); what is left of them is items 2, 3, 38 and 41 to 43 above.
- `/auth/health` answering `POST`, and a JSON body declared `charset=utf-16` being read (spec 0005, As built): fixed by spec 0008, Decision 3 (`HealthMethodsTests`, `JsonCharsetTests`).
- A domain written as a hexadecimal IPv4 form (`127.0x1`) passing the invitation rule (spec 0005, As built): fixed by spec 0008, Decision 3 (`InvitationDomainTests`).
- Company deletion, a known gap of spec 0005: built by spec 0008, Decision 8; its own residual risk is item 4.
- Auth-Core connecting to PostgreSQL as its superuser, found while the production compose file was built: closed by spec 0008, Decision 14 (`deploy/postgres-init/10-auth-app-role.sh`, `scripts/e2e-prod.sh`); what is left of it is item 45.
