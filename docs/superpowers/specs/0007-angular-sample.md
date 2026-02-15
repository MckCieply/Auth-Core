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

## In scope

- **The sample app** in `samples/notes-web/`: Angular 21, the screens below, the
  sign-in pieces in `src/app/auth/`.
- **A third compose overlay**, `samples/notes-web/compose.yml`, that serves the built
  app from Caddy on the same origin as `/auth` and `/api`.
- **A development server setup**: `ng serve` on `http://localhost:4200` with a proxy
  for `/auth` and `/api` to the stack on `:8088`.
- **Unit tests** of the sign-in pieces, run with `ng test` and no Docker.
- **Playwright tests** in Chromium and WebKit, and the script `scripts/e2e-web.sh`
  that starts the stack and runs them.
- **The integration guide** `docs/integration/angular.md`.

## Out of scope / Deferred

See [Deferred / follow-ups](#deferred--follow-ups). Not built here: the test on a real
phone, HTTPS on the local proxy (unless WebKit needs it, see "To verify"), a PWA
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
| `/invite?token=` | First `POST /auth/invites/preview`: "Join **{org_name}** as **{role}**" and the email, not editable. Then the password, twice, and `POST /auth/invites/accept`. The screen always says "This sets the password for {email}.", because for an existing account the call replaces the password. `409 already_member`: "This account already belongs to a company." `invalid_token`: as on `/reset`. On `204`: `/login`, with the email filled in. |
| `/notes` | Guarded. A header with the email, company and role from `GET /auth/me`, and "Sign out". The company's notes, newest first, and a form to add one. The form is shown only when `/auth/me` lists `notes:write`. |

- `/` redirects to `/notes`. An unknown route redirects to `/notes`.
- The email filled in on `/login` after an invitation is passed in the router's
  navigation state, never in the URL.
- `/reset`, `/verify` and `/invite` read `token` from the URL once and then remove it
  from the address bar (`history.replaceState`), so it stays out of the history.
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
| `403 {"error":"permissions_changed"}` | One refresh, so the new token carries the current role, then the request again, once. |
| `503 {"error":"auth_unavailable"}` | A bar: "Try again shortly." The person stays signed in. |

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
`samples/notes-api/compose.yml`, which stay unchanged, so the e2e scripts of specs
0001–0006 run as before. The overlay:

- replaces the `caddy` service's image with one built from `samples/notes-web/Dockerfile`,
  still on `http://localhost:8088` (loopback only);
- gives it the sample's `Caddyfile`: `/auth/*` to Auth-Core, `/api/*` to the notes
  service, every other path to the built app, with an unknown path answered by
  `index.html`;
- sets Auth-Core's mail links to `http://localhost:8088/reset`, `/verify` and
  `/invite` (`Auth:App:FrontendUrls:*`).

**Headers on the app's responses** (not on `/auth` and `/api`, which set their own):

| Header | Value |
| --- | --- |
| `Content-Security-Policy` | `default-src 'self'; script-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'`, plus a `style-src` from "To verify" |
| `Referrer-Policy` | `no-referrer`, because the mail links carry a token in the URL |
| `X-Content-Type-Options` | `nosniff` |
| `Cache-Control` on `index.html` | `no-cache`; hashed files may be cached |

The build turns off critical-CSS inlining, so no inline script or style is needed by
the build itself.

### Development server

`ng serve` on `http://localhost:4200`, with `proxy.conf.json` sending `/auth` and
`/api` to `http://localhost:8088`. The browser still sees one origin, so the refresh
cookie works. It is for working on the screens; the tests run against the built app
on `:8088`.

### Tests

**Unit tests** (`ng test`, Vitest with jsdom, no Docker):

- `AuthService`: the three answers at start; login keeps the token; logout drops it
  whatever the answer.
- The interceptor: every row of its table; one refresh for several `401`s at once;
  no `Authorization` header to another origin or to `/auth/login` and
  `/auth/refresh`; no second retry after a `401` on the retried request.
- The guard: `returnUrl` values `//evil.example`, `/\evil.example`,
  `https://evil.example` and `javascript:…` all end on `/notes`; `/notes?x=1` is kept.

**Playwright** (`e2e/`), against `http://localhost:8088`, in two projects: Chromium
(desktop) and WebKit (an iPhone device profile). Mail is read from Mailpit's API.
A test that changes a password uses its own invited user, never the seeded admin.

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
- **design.md:** a one-line note on Decisions 2, 3, 5 and 6, added with "As built".

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

- **WebKit and the `Secure` cookie on `http://localhost`.** If Playwright's WebKit on
  Windows does not keep the `auth_rt` cookie over plain HTTP, the tests use
  `https://localhost` from Caddy's `tls internal` (no new package), with Playwright
  ignoring the local certificate; the issuer then follows the test origin.
- **`style-src`.** Angular adds component styles as `<style>` elements at run time.
  Find whether a nonce can be served from a static Caddy setup (`ngCspNonce` or
  Angular's `autoCsp`); if not, `style-src 'self' 'unsafe-inline'`, and the spec's
  "As built" says so.
- **Overriding the `caddy` service** from a third compose file: the image, the build
  and the mounted `Caddyfile` replace those of `samples/notes-api/compose.yml`.
- **The mail links** reach the app through `Auth:App:FrontendUrls:*` set by the
  overlay's environment, and Auth-Core accepts those URLs (no `token` parameter, no
  fragment).
- **The unverified seed user** (`AUTH_DEV_SEED_UNVERIFIED_*`) is created on the stack
  the e2e script starts, and Mailpit's API gives the latest mail for an address.
- **Angular 21.1.4 with Vitest 4.0.18:** the default unit-test builder runs with jsdom
  and no browser download.
- **Node 24** builds the app locally and in `node:24-alpine`.
