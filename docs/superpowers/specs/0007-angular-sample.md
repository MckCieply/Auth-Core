# Spec 0007 — First consumer, frontend: an Angular sample over "notes"

- **Status:** Accepted
- **Date:** 2026-02-15
- **Author:** Alex
- **Milestone:** Week 5 (first consumer, frontend) — the screens, the interceptor, the
  guard, the silent refresh at start and the Playwright tests. The milestone's
  *"login on a phone works end to end"* moves to the first real deployment
  (Decision 3). The frontend is a sample inside this repository, over the "notes"
  sample of spec 0006 (Decision 1).
- **Context:** [`docs/design.md`](../../design.md) (Tech stack: Angular frontend and
  E2E; "Request flow"; Week 5), ADR [0002](../../adr/0002-headless-rest-api.md)
  (no CORS, the service mounted at `/auth`), ADR
  [0004](../../adr/0004-same-origin-cookie-refresh.md) (refresh token in an `HttpOnly`
  cookie, access token in memory, silent refresh on start), and specs
  [0002](0002-refresh-and-logout.md) (refresh, rotation, logout),
  [0004](0004-email-flows.md) (reset, verification, the links in mails),
  [0005](0005-tenancy-and-rbac.md) (invitations, `/auth/me`, the company API and its
  error codes) and [0006](0006-python-consumer-package.md) (the "notes" sample, the
  Caddy overlay, the answers a frontend must handle).

## Goal

A person signs in to a web app built on Auth-Core, keeps the session when the page
is reloaded, and does only what their role allows. They can reset a forgotten
password, confirm their email and join a company from an invitation, each from the
link in the mail.

The work is Auth-Core's alone. It ships a small Angular app, "notes-web", over the
"notes" sample of spec 0006. Its sign-in pieces (a service, an interceptor and a
guard) are written to be copied into a real product, and the integration guide
`docs/integration/angular.md` explains how.

Concretely, this is met when this sequence works on a clean stack started from the
repository:

```
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml \
  -f samples/notes-web/compose.yml --env-file .env up -d --build

# in a browser, at http://localhost:8088
/notes                 -> redirected to /login?returnUrl=%2Fnotes
sign in as the admin   -> /notes, the header shows the email, company and role
add a note             -> it is listed
reload the page        -> still signed in, no login screen
sign out, reload       -> /login

scripts/e2e-web.sh     -> every Playwright test passes in Chromium and WebKit
```

The same app is also served on `https://localhost:8443`, with a certificate from
Caddy's internal authority. WebKit (Safari's engine) needs it: it keeps the refresh
cookie, which is always `Secure`, but does not send it back over plain HTTP, not even
to `localhost` (Decision 9).

## In scope

- **The sample app** in `samples/notes-web/`: Angular 21, the screens below, the
  sign-in pieces in `src/app/auth/`.
- **A third compose overlay**, `samples/notes-web/compose.yml`, that serves the built
  app from Caddy on the same origin as `/auth` and `/api`, over HTTP on `:8088` and
  over HTTPS on `:8443`.
- **A development server setup**: `ng serve` on `http://localhost:4200` with a proxy
  for `/auth` and `/api` to the stack on `:8088`.
- **Unit tests** of the sign-in pieces, run with `ng test` and no Docker.
- **Playwright tests** in Chromium and WebKit, and the script `scripts/e2e-web.sh`
  that starts the stack and runs them.
- **The integration guide** `docs/integration/angular.md`.

## Out of scope / Deferred

See [Deferred / follow-ups](#deferred--follow-ups). Not built here: the test on a real
phone, a certificate trusted by the browser for the local proxy, a PWA
(manifest, service worker, install to the home screen), self-service sign-up (Auth-Core
has no endpoint for it), screens for company admins (invitations, members, roles),
switching companies, a reusable npm package, Polish texts, and any change to
speech-to-mail.

## Contract

### Screens

All texts are English and live in one file, `src/app/texts.ts`, so a product changes
them in one place. The routes match the links Auth-Core puts in its mails.

| Route | What the person sees and what happens |
| --- | --- |
| `/login` | Email and password. `401 invalid_credentials`: "Wrong email or password." `403 email_not_verified`: a message and a button "Send the verification link again" (`POST /auth/email/verify/request`). `403 no_membership`: "Your account does not belong to a company." `429 too_many_attempts`: "Too many attempts. Try again in N minutes.", N from `retry_after_seconds` rounded up. On success: the page named by `returnUrl`, else `/notes`. |
| `/forgot` | Email. On `202` always the same answer: "If an account exists for this address, we sent a link." `429`: "Wait N seconds before asking again." |
| `/reset?token=` | New password, twice. `weak_password`: the rules not met, one line each. `invalid_token`: "This link has expired or was already used." with a link to `/forgot`. On `204`: "Password changed. Sign in." with a link to `/login`. No sign-in follows: Auth-Core has ended every session. |
| `/verify?token=` | Calls `POST /auth/email/verify` on opening. `204`: "Email confirmed." with a link to `/login`. `invalid_token`: "This link has expired or was already used." and a form to send a new one. |
| `/invite?token=` | First `POST /auth/invites/preview`: "Join **{org_name}** as **{role}**" and the email, not editable. Then the password, twice, and `POST /auth/invites/accept`. The screen always says "This sets the password for {email}.", because for an existing account the call replaces the password. `409 already_member`: "This account already belongs to a company." `invalid_token`: "This invitation has expired or was already used. Ask for a new one." On `204`: `/login`, with the email filled in. |
| `/notes` | Guarded. A header with the email, company and role from `GET /auth/me`, and "Sign out". The company's notes, newest first, and a form to add one. The form is shown only when `/auth/me` lists `notes:write`. |

- `/` redirects to `/notes`. An unknown route redirects to `/notes`.
- The email filled in on `/login` after an invitation is passed in the router's
  navigation state, never in the URL.
- `/reset`, `/verify` and `/invite` read `token` from the URL once and then remove it
  from the address bar with a router navigation that replaces the history entry
  (`replaceUrl`), so it stays out of the history and out of the router's own copy of
  the URL (Decision 11).
- Password rules are checked by Auth-Core only. The screens show its `rules`; they do
  not repeat the policy.
- A `400 invalid_request` or any other unexpected answer shows "Something went wrong.
  Try again."

### Session

**The access token** is held in memory only, in a signal of `AuthService`. It is never
written to `localStorage`, `sessionStorage`, a cookie or the URL. The app does not
decode it: the email, company, role and permissions come from `GET /auth/me`.

**At start**, before the first route is resolved (`provideAppInitializer`), the app
calls `POST /auth/refresh` once:

| Answer | Result |
| --- | --- |
| `200 {"access_token"}` | The token is kept, then `GET /auth/me`. The person is signed in. |
| `401` | The person is anonymous. Nothing is shown. |
| Anything else, or no network | The person is anonymous, and a bar says "Can't reach the server. Try again shortly." |

**The interceptor** adds `Authorization: Bearer <token>` only to requests for the
app's own origin whose path starts with `/api/`, or is `/auth/me` or starts with
`/auth/org`. It never adds it to another origin or to the other `/auth` endpoints.
It handles the answers to those requests:

| Answer | Reaction |
| --- | --- |
| `401` | One refresh, then the request again, once. Requests that get `401` while a refresh is running wait for that refresh instead of starting another. |
| `401` from that refresh | The token is dropped; the person goes to `/login?returnUrl=<the current path>`. |
| `403 {"error":"forbidden"}` | "You don't have access to this." No refresh. |
| `403 {"error":"permissions_changed"}` | One refresh, so the new token carries the current role, then `/auth/me` again and, at the same time, the request again, once. |
| `503 {"error":"auth_unavailable"}` | A bar: "Try again shortly." The person stays signed in. |

A refresh that fails with anything other than `401` (no network, `5xx`) keeps the token and shows "Can't reach the server. Try again shortly." A `401` on the retried request is passed to the caller as it is. A `401` on a request sent with a token that has since been renewed is retried with the new token, without another refresh.

The bar has a "Dismiss" button and is cleared by a sign-in. Counts in texts use the singular for 1 ("1 minute").

Every request to the app's own origin gives up after 30 seconds and is treated like no
network (Decision 10); giving up cancels the request. The refresh and `/auth/me` at
start keep their own 10 seconds.

There is no refresh ahead of expiry: a token is renewed only after a `401`, which
costs one extra request about every 10 minutes.

**The guard** lets a route through only when a token is held. Otherwise it sends the
person to `/login?returnUrl=<path>`. `returnUrl` is followed only when it starts with
a single `/` and is not `//…` or `/\…`; anything else is replaced by `/notes`, so the
login screen never redirects to another site.

**Sign out** calls `POST /auth/logout`, drops the token and goes to `/login`, whatever
the answer. Other open tabs keep their token in memory until it expires (up to 10
minutes, as Auth-Core works) and then go to `/login` on their next refresh. Tabs are
not synchronised.

### Files

```
samples/notes-web/
  src/app/auth/       auth.service.ts, auth.interceptor.ts, auth.guard.ts  (copied by products)
  src/app/pages/      login, forgot, reset, verify, invite, notes
  src/app/texts.ts    every text the person sees
  e2e/                Playwright tests
  proxy.conf.json     ng serve: /auth and /api -> http://localhost:8088
  playwright.config.ts
  Dockerfile          node:24-alpine builds; caddy:2 serves the build
  Caddyfile
  compose.yml
  README.md
```

Components are standalone and use signals and reactive forms. Styles are plain CSS,
with no UI library. The app needs no `zone.js`.

### Compose overlay and proxy

`samples/notes-web/compose.yml` is used on top of `deploy/docker-compose.yml` and
`samples/notes-api/compose.yml`, which stay unchanged except for one fix in the base file
(Decision 12), so the e2e scripts of specs 0001–0006 run as before. The overlay:

- replaces the `caddy` service with one built from `samples/notes-web/Dockerfile`, under
  its own image name (`notes-web-caddy:local`; without an `image` of its own, Compose
  would tag the build as `caddy:2`);
- gives it the sample's `Caddyfile`, mounted at the same path, so it replaces the one
  of `samples/notes-api/compose.yml`. The same routes on both listeners: `/auth/*` to
  Auth-Core, `/api/*` to the notes service, every other path to the built app, with an
  unknown path answered by `index.html`, except a path with a file extension that is
  not in the build, which is a `404` (Decision 14);
- listens on `http://localhost:8088` as before, and on `https://localhost:8443` with a
  certificate from Caddy's internal authority (`tls internal`), both on loopback only;
- sets Auth-Core's mail links to `http://localhost:8088/reset`, `/verify` and
  `/invite` (`Auth:App:FrontendUrls:ResetPassword`, `VerifyEmail`, `AcceptInvite`).

The token issuer stays `http://localhost:8088/auth`, as in spec 0006. It is a name the
notes service compares with the token, not the address the browser used, so a session
started on `:8443` works the same.

**Headers on the app's responses** (not on `/auth` and `/api`, which set their own):

| Header | Value |
| --- | --- |
| `Content-Security-Policy` | `default-src 'self'; script-src 'self'; style-src 'self' 'nonce-<n>'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'` |
| `Referrer-Policy` | `no-referrer`, because the mail links carry a token in the URL |
| `X-Content-Type-Options` | `nosniff` |
| `Cache-Control` | `no-cache` on every response that is `index.html`, including the answer to an unknown path; hashed `.js` and `.css` files may be cached |

`<n>` is new for every response. Angular adds component styles as `<style>` elements
while it runs; they carry the nonce, which Caddy writes into `index.html`
(`<app-root ngCspNonce="…">`, filled by Caddy's `templates` from the request's id) and
into the header. `'self'` is still needed for the build's stylesheet. There is no
`'unsafe-inline'`. The build turns off critical-CSS inlining, so `index.html` holds no
inline script, style or event handler.

### Development server

`ng serve` on `http://localhost:4200`, with `proxy.conf.json` sending `/auth` and
`/api` to `http://localhost:8088`. The browser still sees one origin, so the refresh
cookie works in Chrome. It is for working on the screens; the tests run against the
built app on `:8443`.

### Tests

**Unit tests** (`ng test`, Vitest with jsdom, no Docker):

- `AuthService`: the three answers at start; login keeps the token; logout drops it
  whatever the answer.
- The interceptor: every row of its table; one refresh for several `401`s at once;
  no `Authorization` header to another origin or to `/auth/login` and
  `/auth/refresh`; no second retry after a `401` on the retried request.
- The guard: `returnUrl` values `//evil.example`, `/\evil.example`,
  `https://evil.example` and `javascript:…` all end on `/notes`; `/notes?x=1` is kept.

**Playwright** (`e2e/`), against `https://localhost:8443` with the local certificate
accepted (`ignoreHTTPSErrors`), in two projects: Chromium (desktop) and WebKit
(`iPhone 15`). Mail is read from Mailpit's API. A mail link points at `:8088`; a test
takes its `token` and opens the same path on the test origin. A test that changes a
password uses its own invited user, never the seeded admin.

1. Sign in as the admin; a wrong password shows the message.
2. A reload keeps the session; after sign-out a reload shows `/login`.
3. `/forgot`, the link from the mail, a new password, sign in with it.
4. The operator invites a `viewer` with the CLI; the link from the mail, accept, sign
   in; the notes are listed and the add form is not shown.
5. The seeded unverified user signs in, sees the message, sends the link again,
   confirms from the mail and signs in.
6. With answers replaced by `page.route`: a `401` once on `/api/notes` leads to one
   refresh and the list; a `401` from the refresh leads to `/login?returnUrl=%2Fnotes`;
   a `503 auth_unavailable` shows the bar and keeps the header; a `403 forbidden` shows
   the message.
7. The app's responses carry the headers above, and after opening a mail link the
   address bar holds no `token`.

`scripts/e2e-web.sh` starts the stack with the three files under its own compose
project name (default `auth-core-web`), seeds what the tests need, runs Playwright,
and stops the stack.

### Integration guide

`docs/integration/angular.md`, written for the developer of any product. It names no
product. Steps, each pointing at the sample's file:

1. Put the app, `/auth` and `/api` on one origin behind the proxy.
2. Copy `src/app/auth/` and register the initializer, interceptor and guard.
3. Configure which paths get the token.
4. Build the screens for the mail links and set `Auth:App:FrontendUrls:*` to them.
5. Handle the answers in the interceptor's table.
6. Set the headers above on the app's responses.
7. Check on a real phone once deployed with a real certificate: a checklist (sign in,
   close and reopen the browser, reload, sign out; on iOS, also from the home screen).

## Acceptance criteria (Done when)

1. The Goal sequence works and every Playwright test passes in Chromium and WebKit on
   a clean stack; `scripts/e2e-notes.sh` still passes with two overlay files, and the
   e2e scripts of specs 0001–0005 still pass on the stack without overlays.
2. Each screen gives each answer in its table the text and action written there.
3. The access token never appears in `localStorage`, `sessionStorage`, `document.cookie`
   or a URL (checked in Playwright after sign-in and after a refresh).
4. A reload with a valid refresh cookie lands on the page that was open, signed in,
   with no login screen in between.
5. The interceptor sends the token only as written, refreshes once for any number of
   simultaneous `401`s, retries a request at most once, and never refreshes on
   `403 forbidden`.
6. No `returnUrl` value leads off the app's origin.
7. The tokens from mail links are gone from the address bar after the screen opens,
   and the app's responses carry `Referrer-Policy: no-referrer` and the
   `Content-Security-Policy` above.
8. `ng test` runs with no Docker.
9. The integration guide covers the seven steps, and every file it points at exists in
   the sample.

## Decisions (owner, 2026-02-15)

1. **The frontend is a sample in this repository**, `samples/notes-web/`, over
   `samples/notes-api/`. The sign-in pieces are copied by products, not installed as a
   package. design.md lists a shared package as stretch, and puts the screens inside
   speech-to-mail.
2. **Angular 21**, the current version when this slice starts. design.md names
   Angular 19.
3. **No test on a real phone in this slice.** It happens on the first real deployment,
   with a real certificate, following the checklist in the integration guide. A local
   phone test would need HTTPS on the proxy and a local certificate authority trusted
   by the phone, and would still differ from a deployment. WebKit in Playwright stands
   in for Safari meanwhile. This departs from design.md, whose Week 5 milestone is a
   login on a phone, and from spec 0006 (Decision 4), which moved the phone test here.
4. **Playwright runs in Chromium and WebKit.**
5. **The screens are in English**, with every text in one file. design.md says Polish;
   a product translates the file.
6. **A plain web page, not a PWA.** The silent refresh runs whenever the app starts:
   on opening, on reload. A manifest and a service worker are left to the product,
   where they can be tested on a phone.
7. **Caddy serves the built app** on the stack's origin, and the tests run against it;
   `ng serve` with a proxy is for development.
8. **Packages, pinned:** Angular, its CLI and build 21.1.4; TypeScript 5.9.3; RxJS
   7.8.2; tslib; Vitest 4.0.18 and jsdom 28.0.0; Playwright 1.58.2 with its Chromium
   and WebKit; the `node:24-alpine` image.
9. **The local proxy also speaks HTTPS, for WebKit.** The technical trial showed that
   WebKit keeps a `Secure` cookie set over `http://localhost` but never sends it back,
   so a session cannot survive a reload there. Auth-Core always sets `Secure`, and that
   stays. Caddy therefore also serves `https://localhost:8443` with `tls internal`
   (no new package), and the tests run there. The certificate is not trusted by the
   browser; Playwright accepts it, a person sees a warning. This was agreed with
   Decision 4 as the fallback if WebKit refused the cookie.
10. **Requests give up after 30 seconds** (owner, 2026-02-17). A server that never
    answers would otherwise leave a screen busy for good; after 30 seconds the person
    sees the same message as for no network and can try again.
11. **The router forgets the mail token too** (owner, 2026-02-17). Removing it with a
    router navigation (`replaceUrl`) instead of `history.replaceState` leaves no copy
    in the router's state.
12. **PostgreSQL is reported healthy only when it accepts network connections**
    (owner, 2026-02-17). On a fresh volume the image's temporary init server answered
    the local health check before the real server listened, so a service that connects
    over the network at once (the notes sample's `notes-db-init`) could fail and stop
    `docker compose up`. The health check in `deploy/docker-compose.yml` now checks over
    TCP. This fixes a fault of slice 6 found here.
13. **Angular stays at 21.1.4, with its known advisories written down** (owner,
    2026-02-17). `npm audit` lists advisories against 21.1.4 (cross-site scripting
    through i18n bindings, sanitisation bypasses in templates, denial of service in
    the date and number pipes and in server-side rendering). The sample uses no i18n
    and no server-side rendering. The versions stay as Decision 8 pins them; the
    sample's README names the advisories, and moving to a fixed 21.2 release is a
    follow-up.
14. **A missing file is a `404`, a missing route is the app** (owner, 2026-02-17). A
    path with a file extension that is not in the build (`/missing.js`) is answered
    `404`, so a broken build shows as an error and the browser never runs HTML as a
    script. A path without an extension is a route of the app and gets `index.html`.

Design choices made with them, not departures:

- The token is renewed only after a `401`, never ahead of expiry.
- Open tabs are not synchronised on sign-out.
- The app reads the person's details from `/auth/me` and never decodes the token.

## Deferred / follow-ups

- **The test on a real phone**, on the first deployment with a real certificate,
  including iOS from the home screen.
- **A PWA** (manifest, service worker that never caches `/auth` and `/api`) → the
  product.
- **Screens for company admins:** invitations, members, roles, on the company API of
  spec 0005.
- **A shared npm package** of the sign-in pieces → Beyond MVP ("SDKs: NuGet, npm").
- **Synchronising sign-out across tabs** (for example with `BroadcastChannel`).
- **Angular 21.2 with the security fixes** (Decision 13).
- **design.md:** a one-line note on Decisions 1, 2, 3, 5 and 6, added with "As built". *Done: it is in the
  list of "Week 5: first consumer, frontend".*

**Residual risks:**

- Another tab stays usable for up to 10 minutes after sign-out (Auth-Core keeps issued
  access tokens valid until they expire, spec 0002).
- A token in memory can be read by script running in the page. The
  `Content-Security-Policy` limits scripts to the app's own files.
- Safari on a real phone may treat cookies differently from WebKit in Playwright;
  the phone test after deployment covers it.

## Verification notes (for the local verifiers)

Per [`docs/workflow.md`](../../workflow.md) — verifiers run locally before merge.

- **realization vs spec:** map each criterion to its guarding test; criterion 2 row by
  row against the screen tables.
- **API / e2e:** bring the stack up clean with the three files and run
  `scripts/e2e-web.sh`, taking each token from the mail as delivered. Then run
  `scripts/e2e-notes.sh` with two files and the earlier e2e scripts without overlays as
  the regression pass.
- **security:** confirm the token is held in memory only and sent only as the
  interceptor's rules say; no open redirect through `returnUrl`; mail tokens leave the
  address bar and are not sent as a referrer; the response headers; no inline script;
  nothing secret in the built files; and that the Caddyfile serves no file outside the
  build.

## To verify during implementation

Settled by the technical trial (2026-02-15), recorded for the plan:

- WebKit drops the `Secure` cookie over plain HTTP on `localhost` and `127.0.0.1`, and
  keeps it over HTTPS with an untrusted certificate (Decision 9). Chromium keeps it on
  both.
- The style nonce works from static Caddy: `templates` on `text/html`, the request's
  `{http.request.uuid}` in the header, and in `index.html` the template written with
  backticks (`` {{placeholder `http.request.uuid`}} ``), because the build rewrites double
  quotes inside the attribute. With `style-src 'self'` alone Angular's styles are
  refused in both browsers; with the nonce there is no violation.
- In a third compose file `build` does not replace `image`, so the overlay names its
  own image; a volume at the same target replaces the earlier one; `environment` is
  merged; relative paths resolve from `deploy/`.
- `ng new` 21.1.4 with `--zoneless --ssr=false --style css --test-runner vitest` gives
  no `zone.js`; `ng test` runs on jsdom with no browser download. The scaffold writes
  caret ranges older than Decision 8, so the versions are pinned by hand.

Still to verify:

- **Caddy's `tls internal` in the container** for the site `localhost`, published on
  `127.0.0.1:8443`, with no attempt to install its root into a trust store.
- **The mail links** reach the app through `Auth:App:FrontendUrls:*` set by the
  overlay's environment (plain `http` is accepted in Development only).
- **The unverified seed user** (`AUTH_DEV_SEED_UNVERIFIED_*`) is created on the stack
  the e2e script starts, and Mailpit's API gives the latest mail for an address.
- **Angular 21.1.4 with Vitest 4.0.18:** the default unit-test builder runs with jsdom
  and no browser download.
- **Node 24** builds the app locally and in `node:24-alpine`.

## As built (owner, 2026-02-17)

Recorded after implementation and local verification (plan 0007,
[acceptance map](../plans/0007-acceptance-map.md)). What entered this stage stays in it;
this section records it rather than rewriting the decisions above. Decisions 10 to 14 were
taken during the build and are in the list above; the sentences of the Contract that they
change were amended with them.

**What was built.** The sample as specified, in `samples/notes-web/`: the six screens, the
sign-in pieces in `src/app/auth/` (service, interceptor, guard), the third compose overlay
with Caddy serving the built app on `http://localhost:8088` and `https://localhost:8443`, the
development server setup, the unit tests (361 tests in 17 files, no Docker), the Playwright
tests (37 per browser, in Chromium and in WebKit with the `iPhone 15` profile),
`scripts/e2e-web.sh` and the guide `docs/integration/angular.md`. Besides what the spec lists,
`samples/notes-web/tools/check-guide.mjs` (run as `npm run check:docs`) is the check of
criterion 9: the seven step headings, every link, every path and file name in a code span, and
no product named. The one change outside the sample is the health check of PostgreSQL in
`deploy/docker-compose.yml` (Decision 12). Every acceptance criterion is mapped to its tests
in the acceptance map; the e2e runs and the regression scripts pass on clean stacks.

**Settled by the build** (the items of "To verify during implementation"): `tls internal`
works for `localhost` in the container, with a read-only root filesystem and `/data` in memory
(the authority is new at each start); the mail links reach the app through
`Auth:App:FrontendUrls:*` set by the overlay; the unverified seed user exists on the stack the
e2e script starts, and Mailpit's API gives the latest mail for an address; Vitest on jsdom runs
with no browser download; Node 24 builds the app locally and in `node:24-alpine`.

**Decisions taken during the build.** Decisions 10 to 14 above: the 30-second limit that cancels
the request (the interceptor is built from observables), the router forgetting the mail token
(`replaceUrl`), the PostgreSQL health check over TCP, the Angular 21.1.4 advisories written down,
and a `404` for a missing file with an extension. They came from the owner's rulings on what the
reviews and the verifiers found. One more ruling, not a Decision: the notes screen says the
common sentence "Something went wrong. Try again." for a failed load or save (the `/notes` row
of the screens table does not name an error text).

**Behaviour in the code that the spec does not state:**

- **A sign-in ends the session that exists.** `login()` drops the old session before it keeps the
  new token, so the old person's details and a late `/auth/me` answer of the old session never
  show under the new person; a failed sign-in leaves the old session alone. `/login` has no guard,
  so a signed-in person can open it and sign in again.
- **A request of an ended session is not replayed** with the next person's token after a `401`
  or a `403 permissions_changed`.
- **The guard drops `token`.** The guard and the redirects of `/` and of an unknown route drop a
  `token` from the query, and the interceptor's redirect to `/login` never carries a target that
  has one, so a mail link that points at a route the app does not have does not put the token
  into `returnUrl`. `returnUrl` can then be `/notes` where the spec says "the current path".
- **The notes screen.**
  - A list that holds anything that is not a note, or a note whose `created_at` cannot be parsed,
    is a failed load. A `2xx` answer to an add whose body is not a note makes the page load the
    list again, because the note was saved. A list answer that arrives after an add is merged
    with the notes the page added.
  - A note of more than 1000 characters is not sent and the screen says the limit; the field stops
    at 1000.
  - **On `503 auth_unavailable` the screen adds nothing of its own**: no list, no "No notes yet",
    no sentence and no retry. The interceptor's bar is the whole message, so that the person never
    sees two. After the bar is dismissed, a list that was answered this way shows nothing, with the
    add form below, until the page is loaded again; an add keeps what was
    typed and can be sent again.
  - `503 database_unavailable`, `500 internal_error`, `404` and `405` of the notes service reach
    the screen as they are: no refresh, no sign-out, no bar.
- **The mail-link screens.** After a server error `/verify` and `/invite` show a "Try again"
  button that asks again with the token held in memory; the token is no longer in the address bar,
  so a reload would say the link was used up.
- **The forms.** An email is trimmed and a password is sent as typed; an email of only spaces sends
  nothing; a double click or a double Enter sends one request; the button stays off until the next
  page is open.
- **Caddy.**
  - Only `localhost` and `127.0.0.1` are answered; any other `Host` gets a `421` on both
    listeners, `/auth` and `/api` included, and a client that asks for another TLS name gets no
    certificate. `http://[::1]:8088` does not work.
  - A method other than `GET` and `HEAD` gets `405` with `Allow: GET, HEAD` on every path of the
    app, a missing file included; `HEAD` is answered as `GET`, with the length of the page.
  - A conditional request or a `Range` request for a page of the app is answered in full (`200`,
    never `304` or `206`): the page holds a nonce that is new for every response, and the nonce makes
    it longer than the file. The hashed `.js` and `.css` keep `ETag`, `304` and `206`.
  - The file server's canonical-URI redirect is off, because `/%5cevil.example/index.html/` turned
    into a `Location` that a browser reads as another site. No answer of the app is a redirect.
  - `/auth` and `/api` go to their services when the path is `/auth` or `/api` or goes on after a
    slash, in lower case only; `/AUTH/x` and `/authx` are paths of the app.
  - A path whose last segment has a dot is a `404` (the spec says "a path with a file extension"),
    so a route such as `/notes/john.doe` cannot be served. `/x.` and `/a.b/c` are routes; a query
    or a slash at the end is the way out. The guide and the README say so.
  - The `node` and `caddy` images are pinned by digest and the image runs as `10002:10002`. The
    overlay inherits the hardening of the notes overlay of spec 0006: a read-only root filesystem,
    `/data` and `/config` in memory, every capability dropped but `NET_BIND_SERVICE`, and
    `no-new-privileges`. The ports are published on `127.0.0.1` only.
- **`scripts/e2e-web.sh`.** The compose project must be `auth-core-web` or `auth-core-web-<suffix>`
  (a-z, 0-9, `-`), checked before any Docker command, and the script refuses to start when a
  container of that project is running, because its clean start is a `down -v`. `test-results/` is
  removed when the script ends (`E2E_KEEP_RESULTS=1` keeps it). It hides `token=...` and typed
  passwords in every output it passes on, and exports `PLAYWRIGHT_NO_COPY_PROMPT=1` and no trace, so
  that a failed test leaves no page snapshot with a typed password on disk.
- **The README and the guide.** The README's "Known advisories" lists the Angular advisories per
  package, with their ids and kinds, what the sample uses (the date pipe with the fixed format
  `'medium'` on a validated `created_at`, `[attr.maxlength]` bound to a constant) and what it does not
  (i18n, server-side rendering and hydration, dynamic components, two-way bindings, host bindings,
  number pipes). The guide's step 4 warns that an access log of the app's host records the mail
  link's `?token=` query.

**Known limits:**

- Angular, its CLI and build stay at 21.1.4 (Decision 8). `npm audit --omit=dev` lists 6 high
  advisories against the six runtime packages (cross-site scripting through i18n bindings,
  sanitisation bypasses in templates and host bindings, denial of service in the date and number
  pipes and in server-side rendering, leaks of the transfer cache). The sample uses none of the
  features they concern except the date pipe, with a fixed format and a validated date
  (Decision 13). `npm audit` also lists advisories in the development dependencies (`vitest`,
  `piscina`, `vite` and others); none reaches the built app or the image, which holds only Caddy and
  the build output. The `Content-Security-Policy` is the second line of defence.
- WebKit in Playwright is the engine with a phone's screen, not Safari on a phone: Safari may treat
  cookies differently. Playwright's WebKit on Windows also reports `sameSite: "None"` for the Strict
  refresh cookie, so the tests read `SameSite=Strict` from the `Set-Cookie` header of the sign-in
  answer and check the other attributes on the browser's cookie.
- The test on a real phone has not been done. The checklist of the guide's step 7 awaits the first
  deployment with a real certificate.
- The certificate on `https://localhost:8443` is not trusted by a browser. A person sees a
  warning; Playwright accepts it. WebKit needs this origin, because it does not send the `Secure`
  refresh cookie back over plain HTTP (Decision 9), so a person who tries the stack in Safari opens
  `https://localhost:8443` and accepts the warning once.
- Right after Auth-Core starts again, the notes service answers `503 auth_unavailable` for a few
  seconds (its key cache asks again at most every 10 seconds, spec 0006). The app shows the bar
  "Try again shortly." and the next request works.
- The unit tests run on jsdom: the tests of the mail-link screens use the browser's own location, the others a
  mock; what only a real browser shows is checked by the Playwright tests.
- Chromium and WebKit are the only browsers tried. `tools/check-guide.mjs` also reads untracked
  and ignored files of a developer's tree, which a clean checkout does not have.

**Follow-ups** (in addition to "Deferred / follow-ups" above):

- Move Angular to a fixed 21.2 release, in a later commit that also refreshes `package-lock.json`
  and the README's advisory list (Decision 13).
- The test on a real phone, on the first deployment, with the guide's checklist.
- A product that needs routes with a dot in their last segment changes the `@missing` rule in the
  `Caddyfile`.
