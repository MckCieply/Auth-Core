# Angular Sample (notes-web) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Plan:** 0007
- **Author:** Alex
- **Spec:** [`docs/superpowers/specs/0007-angular-sample.md`](../specs/0007-angular-sample.md)
  - the plan argues from the spec; where they disagree, **the spec wins** and the
  disagreement is a finding (see [`docs/workflow.md`](../../workflow.md)). Executors read both.

**Goal:** A person signs in to a web app built on Auth-Core, keeps the session when the page is reloaded, does only what their role
allows, and can reset a forgotten password, confirm their email and join a company from the link in a mail: a small Angular app,
"notes-web", over the "notes" sample of spec 0006, in `samples/notes-web/`, with sign-in pieces (a service, an interceptor and a guard)
that a product copies, a third compose overlay that serves the built app from Caddy on the same origin as `/auth` and `/api`, Playwright
tests in Chromium and WebKit, and the integration guide `docs/integration/angular.md`.

**Architecture:** One Angular 21 app, zoneless, standalone components with signals and reactive forms, plain CSS. `AuthService` holds the
access token in one private signal and nowhere else, resumes a session at start with one `POST /auth/refresh`
(`provideAppInitializer`), and shares a running refresh between all callers; a functional interceptor adds the token to the app's own
`/api/`, `/auth/me` and `/auth/org*` requests only, refreshes once after a `401` or `403 permissions_changed`, and turns `403 forbidden` and
`503 auth_unavailable` into the bar of the shell; a guard sends an anonymous person to `/login?returnUrl=` and `safeReturnUrl` makes sure
the way back never leaves the app. The screens for the mail links read their token once and take it out of the address bar. In
production the build is served by `caddy:2` (built from `node:24-alpine`): the same routes on `http://localhost:8088` and on
`https://localhost:8443` (a certificate from Caddy's own authority, because WebKit does not send a `Secure` cookie back over plain HTTP),
a Content-Security-Policy with a nonce that Caddy writes into the header and into `index.html`, and no inline script or style. The
unit tests answer every call themselves; `scripts/e2e-web.sh` starts a clean stack per browser, seeds it, and runs Playwright against
`https://localhost:8443`.

**Tech Stack:** Angular 21.1.4 (core, common, compiler, platform-browser, router, forms, cli, build, compiler-cli), TypeScript 5.9.3,
RxJS 7.8.2, tslib 2.8.1; Vitest 4.0.18 with jsdom 28.0.0 (unit tests); Playwright 1.58.2 with its Chromium and WebKit (end to end);
Node 24; the images `node:24-alpine` and `caddy:2`; and, from plans 0001-0006, .NET 10 Auth-Core, PostgreSQL, Mailpit, the notes
sample and Docker Compose. **Every package here is on the owner's list** (Decision 8 of the spec, which also names the image `node:24-alpine`: approved). Three things are
downloaded: the npm packages and the Angular CLI (Task 1), the image `node:24-alpine`, which is not on this machine yet (Task 8, step 5), and
Playwright's browsers if they are not in its cache (Task 9, step 1: they normally are, from the technical trial; a missing one stops the task
until the owner says yes).

## Global Constraints

Values copied from the spec, one line each; every task includes them.

- **The app:** `samples/notes-web/`, project name `notes-web`, Angular 21; standalone components, signals, reactive forms; styles are plain
  CSS with no UI library; **no `zone.js`**, no `@angular/animations`.
- **Packages, pinned (Decision 8), exact versions with no caret:** Angular packages (core, common, compiler, platform-browser, router,
  forms, cli, build, compiler-cli) 21.1.4; TypeScript 5.9.3; RxJS 7.8.2; tslib 2.8.1; Vitest 4.0.18; jsdom 28.0.0; `@playwright/test` 1.58.2
  with its Chromium and WebKit; the `node:24-alpine` image. **No other direct dependency.**
- **Texts:** all English, all in `src/app/texts.ts`, so a product changes them in one place.
- **Routes:** `/login`, `/forgot`, `/reset?token=`, `/verify?token=`, `/invite?token=`, `/notes` (guarded); `/` and an unknown route redirect to
  `/notes`. The routes match the links Auth-Core puts in its mails.
- **The access token** is held in memory only, in a signal of `AuthService`. It is never written to `localStorage`, `sessionStorage`, a
  cookie or the URL. The app does not decode it: the email, company, role and permissions come from `GET /auth/me`.
- **At start**, before the first route is resolved (`provideAppInitializer`), the app calls `POST /auth/refresh` once: `200 {"access_token"}` keeps
  the token and asks `GET /auth/me`; `401` is anonymous, nothing shown; anything else or no network is anonymous and a bar says "Can't reach the
  server. Try again shortly."
- **The interceptor** adds `Authorization: Bearer <token>` only to requests for the app's own origin whose path starts with `/api/`, or is
  `/auth/me` or starts with `/auth/org`; never to another origin or to the other `/auth` endpoints. `401`: one refresh, then the request
  again, once; requests that get `401` while a refresh runs wait for it. `401` from that refresh: the token is dropped, the person goes to
  `/login?returnUrl=<the current path>`. `403 {"error":"forbidden"}`: "You don't have access to this.", no refresh. `403
  {"error":"permissions_changed"}`: one refresh, then the request again, once. `503 {"error":"auth_unavailable"}`: a bar "Try again shortly.",
  the person stays signed in. **No refresh ahead of expiry.**
- **The guard** lets a route through only when a token is held, else sends the person to `/login?returnUrl=<path>`. `returnUrl` is followed only
  when it starts with a single `/` and is not `//...` or `/\...`; anything else is replaced by `/notes`.
- **Sign out** calls `POST /auth/logout`, drops the token and goes to `/login`, whatever the answer. Tabs are not synchronised.
- **Mail links:** `/reset`, `/verify` and `/invite` read `token` from the URL once and then remove it from the address bar
  (`history.replaceState`). The email filled in on `/login` after an invitation is passed in the router's navigation state, never in the URL.
  Password rules are checked by Auth-Core only; the screens show its `rules`. A `400 invalid_request` or any other unexpected answer shows
  "Something went wrong. Try again."
- **The overlay:** `samples/notes-web/compose.yml` is used on top of `deploy/docker-compose.yml` and `samples/notes-api/compose.yml`, which stay
  **unchanged**, so the e2e scripts of specs 0001-0006 run as before. It replaces the `caddy` service with one built from
  `samples/notes-web/Dockerfile` under its own image name `notes-web-caddy:local`, gives it the sample's `Caddyfile` at the same path (so it
  replaces the notes sample's), listens on `http://localhost:8088` and on `https://localhost:8443` with `tls internal`, **both on loopback
  only**, with the same routes on both (`/auth/*` to Auth-Core, `/api/*` to the notes service, every other path to the built app, an unknown
  path answered by `index.html`), and sets the mail links to `http://localhost:8088/reset`, `/verify` and `/invite`
  (`Auth:App:FrontendUrls:ResetPassword`, `VerifyEmail`, `AcceptInvite`). The token issuer stays `http://localhost:8088/auth`.
- **Headers on the app's responses** (not on `/auth` and `/api`): `Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'
  'nonce-<n>'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'` with `<n>` new for every
  response, written by Caddy's `templates` into `<app-root ngCspNonce="...">` of `index.html` (with backticks inside the attribute) and into the
  header; `Referrer-Policy: no-referrer`; `X-Content-Type-Options: nosniff`; `Cache-Control: no-cache` on every response that is `index.html`,
  the answer to an unknown path included; no `'unsafe-inline'`; the build turns off critical-CSS inlining so `index.html` holds no inline script,
  style or event handler.
- **Development server:** `ng serve` on `http://localhost:4200`, `proxy.conf.json` sends `/auth` and `/api` to `http://localhost:8088`.
- **Tests:** unit tests with `ng test` (Vitest on jsdom), no Docker; Playwright in two projects, Chromium (Desktop Chrome) and WebKit (`iPhone 15`),
  against `https://localhost:8443` with `ignoreHTTPSErrors`; mail from Mailpit's API; a mail link points at `:8088` and a test opens the same path
  on the test origin; **a test that changes a password uses its own invited user, never the seeded admin**. `scripts/e2e-web.sh` starts the stack
  with the three files under its own compose project name (default `auth-core-web`), seeds what the tests need, runs Playwright, and stops the stack.
- **The guide** `docs/integration/angular.md` is written for the developer of any product and **names no product**; it has the seven steps of the
  spec, with the post-deployment phone checklist as step 7, and every file it points at exists in the sample.
- **Workflow** ([`docs/workflow.md`](../../workflow.md)): local only (no CI, no pull request), Conventional Commits, documentation in English.
  **The orchestrator makes the commits** (their author is its own): implementers leave their changes uncommitted in the working tree, and the
  orchestrator reads the diff, runs the gate and commits with the subject named in the task's last step. Tasks are in three groups, A, B and C;
  **no calendar date or time appears in this plan**.
- **No change** to `src/`, `tests/`, `deploy/docker-compose.yml`, `samples/notes-api/`, `clients/`, `.env.example` or the earlier scripts; the spec
  is the owner's and is not edited. The five e2e scripts of plans 0001-0005 and `scripts/e2e-notes.sh` pass unedited.
- **What a run may print:** only result lines. Typed passwords, access tokens and mail links (which hold a token) never go into a
  report, a log that is kept, or a trace: the Playwright traces stay off, `scripts/e2e-web.sh` hides `token=...` in what it passes
  on, and the end-to-end commands below filter their output to the result lines.
- **Windows and Git Bash:** run `npm ci` and `npx ng ...` inside `samples/notes-web`; always give Docker an explicit `COMPOSE_PROJECT_NAME`;
  never print a secret from `.env` (or a token from a mail); `scripts/dev-keys.sh` is run as `MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh`;
  `scripts/e2e-notes.sh` needs a `python3` that works (a shim that runs `C:/p6v/Scripts/python.exe`, Task 11, step 7).
- **Shell scripts** are LF and executable in the index: after `git add scripts/e2e-web.sh` the orchestrator runs `git update-index --chmod=+x
  scripts/e2e-web.sh`.
- **Backslashes and escapes stay safe.** Two things were seen on this machine. A tool parameter decodes a backslash followed by `u` and four hex
  digits into the character, so **no such escape is written in any file**: the code below uses `\x..` escapes and `String.fromCharCode`, and
  `grep -rInP ... '\x5c[uU][0-9a-fA-F]{4}|[^\x00-\x7F]'` over the sample, the script and the guide must find nothing. And the **shell tool turns a
  doubled backslash in a command into one**, while the file tools keep what is typed: so no shell snippet of this plan needs a doubled backslash
  (the checks use `\x5c`), and the TypeScript files below do depend on theirs (`'/\\evil.example'` is the string `/\evil.example`). Tasks 4, 5 and
  10 check that the files that hold doubled backslashes were written as typed.

## Probed before this plan was written

Single risky points were tried in throwaway files under `C:/p7` (the trial's own project and Caddy image, nothing in this repository). No
file of this plan was built or generated in a scratch copy: the code blocks were written on paper.

- **Caddy `tls internal` in the container** (`caddy:2`, the image already on this machine): with `auto_https disable_redirects`, `skip_install_trust`
  and a site `https://localhost:8443` the internal authority issued a certificate for `localhost` and the site answered over HTTPS with `curl -k`;
  no attempt to install a root. Task 8, step 2 repeats this with the real Caddyfile first, with a fallback note.
- **The cache rule and the nonce** in one Caddyfile with one `(routes)` snippet for both listeners: `@revalidate not { path *.js *.css *.ico; file }`
  gave `Cache-Control: no-cache` on `/`, `/login`, `/some/deep/path`, `/missing.js`, `/missing.css` and `/index.html` and none on a hashed file
  that exists; the nonce of the header and of the `ngCspNonce` attribute were the same UUID, and a different one for each request; `/Caddyfile`,
  `/etc/passwd`, `/../../etc/passwd` and `/.env` all answered `index.html`.
- **Compose, three files:** the `ports` of the third file were added to the second's (`8088` and `8443`, both `127.0.0.1`); the one `volumes` entry
  at `/etc/caddy/Caddyfile` replaced the earlier one; `image: notes-web-caddy:local` held next to `build:`; relative paths resolved from `deploy/`.
- **Angular 21.1.4 with Vitest 4.0.18 (jsdom), in the trial's project:** `ng test --watch=false` runs in about five seconds with no browser download. A
  routed component sees `router.currentNavigation()?.extras.state` in its constructor (the login screen's email); `history.replaceState` removes the
  query in jsdom; `TestBed.inject(ApplicationInitStatus)` runs the `provideAppInitializer` and its `donePromise` waits for it; a functional
  interceptor that returns `from(asyncFunction)` works with `HttpTestingController` (a request made by the interceptor is there synchronously, the
  one after an `await` is there after a macrotask: the tests use `settle()`); `HttpErrorResponse.error` is the parsed JSON body; the `timeout(10_000)`
  of RxJS ends under `vi.useFakeTimers()` with `advanceTimersByTimeAsync`; a reactive form is driven by `input.value = ...; dispatchEvent(new
  Event('input'))` and a `submit` event on the form, and its signals are drawn by `fixture.detectChanges()` after `await`.
- **Playwright 1.58.2:** a config with `devices['iPhone 15']` and `ignoreHTTPSErrors` runs in WebKit from the cache; `tsc -p e2e/tsconfig.json` passes with
  no `@types/node` when the tests declare `process` themselves.
- **Not probed, and made the first step of its task:** the image build in `node:24-alpine` (the image is not on this machine), the mail links through
  `Auth:App:FrontendUrls:*`, the seeded unverified user, and everything that needs the real stack. They are checked in Tasks 8 to 10.

## Review Focus

The spec does not name these, but a person using this software would hit them. Each line has a pinning test in the task named in brackets, and a
row in `docs/superpowers/plans/0007-acceptance-map.md`.

1. **A mail link that is opened twice, or with no token, an empty one, or another parameter** - "This link has expired or was already used." with the
   way to ask for a new one (on `/invite`: "This invitation has expired or was already used. Ask for a new one.", with no link), never a blank page and never a request without a token. [Task 6 (`reset`, `verify`, `invite` specs); the forgot test of Task 10]
2. **A `returnUrl` that is more than the spec's four evil values** - a tab or a newline inside `//`, a backslash inside the path, a 3000-character one,
   none at all: always `/notes`, never another site. [Task 4; Task 5 (login spec)]
3. **The server answers what the contract does not say** - a page of HTML from a proxy, a `200` with no token, a preview with half its fields, a list
   that is not a list, nothing at all for ten seconds: the person is anonymous or sees a plain message, the header stays, nothing crashes and no
   token is invented. [Tasks 2, 3, 5, 7]
4. **A note that is only spaces, has 1001 characters, or is HTML** - refused before it is sent, accepted at exactly 1000, and shown as text. [Task 7]
5. **A password with spaces in it, an email with spaces around it, and a form sent twice** - the password is sent exactly as typed, the email trimmed,
   and a double click or a double Enter is one request. [Tasks 5, 6, 7]

## File Structure

```
samples/notes-web/
  package.json  package-lock.json          exact versions of the spec's Decision 8 (Task 1)
  angular.json  tsconfig.json  tsconfig.app.json  tsconfig.spec.json   production build without critical-CSS inlining; the dev proxy
  proxy.conf.json                          ng serve: /auth and /api -> http://localhost:8088
  .gitignore  .dockerignore
  public/favicon.ico                       from the scaffold
  src/index.html  main.ts  styles.css      the page Caddy fills the nonce into; plain CSS
  src/app/
    app.ts  app.config.ts  app.routes.ts   the shell and its bar, the providers, the routes (Tasks 1-3, 5-7)
    texts.ts                               every text the person sees (Task 1)
    account-api.ts                         the calls of the mail-link screens (Task 5)
    auth/                                  copied by products: auth.service.ts (Task 2), auth.interceptor.ts (3), auth.guard.ts (4)
    pages/                                 login, forgot (5); token-from-url, reset, verify, invite (6); notes (7)
  src/testing/helpers.ts                   test helpers, outside the application's program (Tasks 2, 5)
  e2e/                                     Playwright tests: tsconfig.json, support/{env,session,mail,api}.ts, 01..07 specs (Tasks 9, 10)
  playwright.config.ts                     Chromium and WebKit (iPhone 15) on https://localhost:8443 (Task 9)
  Dockerfile  Caddyfile  compose.yml  README.md   the image, the proxy, the overlay (Task 8)
scripts/e2e-web.sh                         one clean stack per browser, seeded, Playwright, stopped (Task 9)
docs/integration/angular.md                the guide (Task 11)
docs/superpowers/plans/0007-acceptance-map.md   criteria -> tests (Task 11)
```

Test conventions used below: a spec file sits next to the code it tests and is named like it; `describe` is the unit and each `it` title says the
behavior, so that the acceptance map can name it. The unit tests of a page open the route with `RouterTestingHarness`, drive the form through the
DOM with the helpers of `src/testing/helpers.ts`, answer the calls with `HttpTestingController`, and read the text. The end-to-end tests are
`e2e/NN-name.spec.ts`; the number is the test group of the spec.

---

## Acceptance criteria of the spec, and where each is built and guarded

Every criterion 1-9 of the spec is built by the tasks named and guarded by the tests named (**the full table, test by test, is the content of
`docs/superpowers/plans/0007-acceptance-map.md`, which Task 11 produces**). A criterion with no guard would be a finding.

| # | Criterion (short) | Built in | Guarded by |
| - | ----------------- | -------- | ---------- |
| 1 | The Goal sequence and every Playwright test pass in both browsers on a clean stack; `scripts/e2e-notes.sh` and the earlier e2e scripts still pass | 8, 9, 10 | `scripts/e2e-web.sh`; the regression pass of Task 11, step 7 |
| 2 | Each screen gives each answer in its table the text and action written there | 5, 6, 7 | the page specs, row by row (the second table of the map); groups 3-5 of the e2e |
| 3 | The access token never appears in `localStorage`, `sessionStorage`, `document.cookie` or a URL | 2 | `auth.service.spec.ts`; `e2e/02-session.spec.ts` |
| 4 | A reload with a valid refresh cookie lands on the same page, signed in, with no login screen in between | 2 | `app.config.spec.ts`; `e2e/02-session.spec.ts` |
| 5 | The token goes only where written; one refresh for any number of simultaneous `401`s; at most one retry; no refresh on `403 forbidden` | 3 | `auth.interceptor.spec.ts`; `e2e/06-answers.spec.ts` |
| 6 | No `returnUrl` value leads off the app's origin | 4, 5 | `auth.guard.spec.ts`; `login.spec.ts` |
| 7 | Mail tokens leave the address bar; `Referrer-Policy: no-referrer` and the Content-Security-Policy on the app's responses | 6, 8 | the page specs; `e2e/07-headers.spec.ts` |
| 8 | `ng test` runs with no Docker | 1-7 | the whole unit suite, run with no container (Task 11, step 6) |
| 9 | The guide covers the seven steps, and every file it points at exists | 11 | the checks of Task 11, step 2 |

---

## Which tasks need what

| Group | Task | Needs Docker | Needs a download (the owner's yes) |
| ----- | ---- | ------------ | ---------------------------------- |
| A - the app and its sign-in pieces | 1 Scaffold, pinned packages, texts, shell | no | the npm packages and the Angular CLI (approved) |
| A | 2 `AuthService`, the silent refresh, the bar | no | no |
| A | 3 The interceptor | no | no |
| A | 4 The guard and the `returnUrl` sanitiser | no | no |
| B - the screens | 5 `AccountApi`, login, forgot | no | no |
| B | 6 Reset, verify, invite | no | no |
| B | 7 Notes, the final routes | no | no |
| C - the stack, the e2e, the guide | 8 The image, the Caddyfile, the overlay, the README | yes | the image `node:24-alpine` (approved) |
| C | 9 Playwright, the script, test groups 1, 2 and 7 | yes | Playwright's browsers, if not in its cache |
| C | 10 Test groups 3-6 | yes | no |
| C | 11 The guide, the acceptance map, the final gate | yes (the gate itself runs the unit tests with no container) | no |

Groups A and B need only Node. Group C needs the ports `8088`, `8443`, `8080` and `8025` free, a `.env` and `.secrets/`. Tasks follow one another
(each uses the files of the one before); the orchestrator commits after each.

---

## Group A - the app and its sign-in pieces

When the group is done, `cd samples/notes-web && npx ng test --watch=false` runs the unit tests of the shell, the texts, the
`AuthService`, the interceptor and the guard with no Docker, `npx ng build` produces an `index.html` with no inline script,
style or event handler, and the three files of `src/app/auth/` can be copied into a product. Every task leaves the suite
green and the build working. Nothing in this group needs a running Auth-Core: every answer is made up in the test with
`HttpTestingController`.

### Task 1: Scaffold, pinned packages, texts, shell

**Files:**
- Create (by the scaffold, then replaced or removed below): everything `ng new` writes into `samples/notes-web/`
- Create: `samples/notes-web/package.json`, `angular.json`, `tsconfig.json`, `tsconfig.app.json`, `tsconfig.spec.json`,
  `proxy.conf.json`, `.gitignore`, `package-lock.json` (made by `npm install`)
- Create: `samples/notes-web/src/index.html`, `src/main.ts`, `src/styles.css`, `src/app/texts.ts`, `src/app/app.ts`,
  `src/app/app.config.ts`, `src/app/app.routes.ts`
- Test: `samples/notes-web/src/app/texts.spec.ts`, `samples/notes-web/src/app/app.spec.ts`

**Interfaces:**
- Consumes: nothing.
- Produces: `texts` (every text of the screens, grouped as `common`, `notices`, `login`, `forgot`, `reset`, `verify`,
  `invite`, `notes`; the later tasks use exactly these keys), `routes: Routes` (empty here; each page task gives the whole new
  file), `appConfig: ApplicationConfig`, `App` (the shell, `selector: 'app-root'`). The project is named `notes-web`, so the
  production build lands in `dist/notes-web/browser`.

**Why the scaffold and then a rewrite:** `ng new` gives the project layout and `public/favicon.ico` (a binary this plan
cannot write), but it writes caret ranges and a demo page. The files below replace what it wrote, so that the versions are the
pinned ones of the spec (Decision 8), there is no `zone.js`, and `index.html` is the one Caddy fills in (Task 8).

- [ ] **Step 1: Check the tools and make the project.** Node 24 and npm 11 are installed. From the worktree root:

```bash
node --version && npm --version
cd samples
npx -y @angular/cli@21.1.4 new notes-web --zoneless --ssr=false --style css --standalone --test-runner vitest \
  --skip-git --package-manager npm --routing --ai-config none --skip-install --defaults
cd notes-web
rm -rf .vscode README.md src/app/app.css src/app/app.html src/app/app.spec.ts
ls
```

  Expected: `v24.x`, `11.x`; the scaffold prints its created files; `ls` shows `angular.json`, `package.json`, `public`, `src`,
  `tsconfig*.json`, `.editorconfig`, `.gitignore`. (`npx` may print `allow-scripts` warnings; they are harmless. The CLI is on the
  owner's list of approved downloads: the spec names Angular, its CLI and build at 21.1.4.)

- [ ] **Step 2: Pin the packages.** Replace `samples/notes-web/package.json` with exactly this. There is no `zone.js`, no
  `@angular/animations`, no UI library, and no other direct dependency:

```json
{
  "name": "notes-web",
  "version": "0.0.0",
  "private": true,
  "scripts": {
    "ng": "ng",
    "start": "ng serve",
    "build": "ng build",
    "test": "ng test --watch=false",
    "e2e": "playwright test"
  },
  "dependencies": {
    "@angular/common": "21.1.4",
    "@angular/compiler": "21.1.4",
    "@angular/core": "21.1.4",
    "@angular/forms": "21.1.4",
    "@angular/platform-browser": "21.1.4",
    "@angular/router": "21.1.4",
    "rxjs": "7.8.2",
    "tslib": "2.8.1"
  },
  "devDependencies": {
    "@angular/build": "21.1.4",
    "@angular/cli": "21.1.4",
    "@angular/compiler-cli": "21.1.4",
    "@playwright/test": "1.58.2",
    "jsdom": "28.0.0",
    "typescript": "5.9.3",
    "vitest": "4.0.18"
  }
}
```

- [ ] **Step 3: The workspace files.** `samples/notes-web/angular.json` (the one change from the scaffold that matters is
  `optimization.styles.inlineCritical: false` in the production configuration: without it the build puts an inline `onload`
  and a `<style>` into `index.html`, which the Content-Security-Policy of Task 8 refuses; the other two are the dev server's
  proxy and no analytics prompt):

```json
{
  "$schema": "./node_modules/@angular/cli/lib/config/schema.json",
  "version": 1,
  "cli": {
    "packageManager": "npm",
    "analytics": false
  },
  "newProjectRoot": "projects",
  "projects": {
    "notes-web": {
      "projectType": "application",
      "schematics": {},
      "root": "",
      "sourceRoot": "src",
      "prefix": "app",
      "architect": {
        "build": {
          "builder": "@angular/build:application",
          "options": {
            "browser": "src/main.ts",
            "tsConfig": "tsconfig.app.json",
            "assets": [
              {
                "glob": "**/*",
                "input": "public"
              }
            ],
            "styles": [
              "src/styles.css"
            ]
          },
          "configurations": {
            "production": {
              "budgets": [
                {
                  "type": "initial",
                  "maximumWarning": "500kB",
                  "maximumError": "1MB"
                },
                {
                  "type": "anyComponentStyle",
                  "maximumWarning": "4kB",
                  "maximumError": "8kB"
                }
              ],
              "outputHashing": "all",
              "optimization": {
                "styles": {
                  "inlineCritical": false
                }
              }
            },
            "development": {
              "optimization": false,
              "extractLicenses": false,
              "sourceMap": true
            }
          },
          "defaultConfiguration": "production"
        },
        "serve": {
          "builder": "@angular/build:dev-server",
          "options": {
            "proxyConfig": "proxy.conf.json"
          },
          "configurations": {
            "production": {
              "buildTarget": "notes-web:build:production"
            },
            "development": {
              "buildTarget": "notes-web:build:development"
            }
          },
          "defaultConfiguration": "development"
        },
        "test": {
          "builder": "@angular/build:unit-test"
        }
      }
    }
  }
}
```

  `samples/notes-web/tsconfig.json`:

```json
{
  "compileOnSave": false,
  "compilerOptions": {
    "strict": true,
    "noImplicitOverride": true,
    "noPropertyAccessFromIndexSignature": true,
    "noImplicitReturns": true,
    "noFallthroughCasesInSwitch": true,
    "skipLibCheck": true,
    "isolatedModules": true,
    "experimentalDecorators": true,
    "importHelpers": true,
    "target": "ES2022",
    "module": "preserve"
  },
  "angularCompilerOptions": {
    "enableI18nLegacyMessageIdFormat": false,
    "strictInjectionParameters": true,
    "strictInputAccessModifiers": true,
    "strictTemplates": true
  },
  "files": [],
  "references": [
    {
      "path": "./tsconfig.app.json"
    },
    {
      "path": "./tsconfig.spec.json"
    }
  ]
}
```

  `samples/notes-web/tsconfig.app.json` (the test helpers of `src/testing/` stay out of the application's program):

```json
{
  "extends": "./tsconfig.json",
  "compilerOptions": {
    "outDir": "./out-tsc/app",
    "types": []
  },
  "include": [
    "src/**/*.ts"
  ],
  "exclude": [
    "src/**/*.spec.ts",
    "src/testing/**"
  ]
}
```

  `samples/notes-web/tsconfig.spec.json`:

```json
{
  "extends": "./tsconfig.json",
  "compilerOptions": {
    "outDir": "./out-tsc/spec",
    "types": [
      "vitest/globals"
    ]
  },
  "include": [
    "src/**/*.d.ts",
    "src/**/*.spec.ts"
  ]
}
```

  `samples/notes-web/proxy.conf.json` (`ng serve` on `http://localhost:4200` sends `/auth` and `/api` to the stack's proxy, so
  the browser still sees one origin and the refresh cookie works in Chrome):

```json
{
  "/auth": {
    "target": "http://localhost:8088",
    "secure": false
  },
  "/api": {
    "target": "http://localhost:8088",
    "secure": false
  }
}
```

  `samples/notes-web/.gitignore`:

```
/node_modules
/dist
/.angular
/out-tsc
/coverage
/test-results
/playwright-report
/blob-report
npm-debug.log
.DS_Store
Thumbs.db
```

- [ ] **Step 4: Install, and check what was installed.**

```bash
cd samples/notes-web
npm install
npm ls --depth=0
grep -c '"node_modules/zone.js"' package-lock.json
npm ci
```

  Expected: `npm ls` lists exactly the 8 dependencies and 7 development dependencies above at those versions (and no
  `zone.js`); the `grep` prints `0`; `npm ci` succeeds (the lock file is complete). The lock file is committed; `node_modules`
  is not. This is the first download of the packages, all on the owner's list (Angular, CLI and build 21.1.4, TypeScript 5.9.3,
  RxJS 7.8.2, tslib, Vitest 4.0.18, jsdom 28.0.0, Playwright 1.58.2). `npm install` of Playwright does **not** download the
  browsers; Task 9 deals with them.

- [ ] **Step 5: Write the texts and their test first.** `samples/notes-web/src/app/texts.spec.ts`:

```ts
import { texts } from './texts';

function leaves(value: unknown, path: string, into: [string, string][]): [string, string][] {
  if (typeof value === 'string') {
    into.push([path, value]);
  } else if (typeof value === 'object' && value !== null) {
    for (const [key, inner] of Object.entries(value)) {
      leaves(inner, `${path}.${key}`, into);
    }
  }
  return into;
}

describe('texts', () => {
  it('rounds the wait of a lockout up to whole minutes', () => {
    expect(texts.login.tooManyAttempts(1)).toBe('Too many attempts. Try again in 1 minute.');
    expect(texts.login.tooManyAttempts(60)).toBe('Too many attempts. Try again in 1 minute.');
    expect(texts.login.tooManyAttempts(61)).toBe('Too many attempts. Try again in 2 minutes.');
    expect(texts.login.tooManyAttempts(90)).toBe('Too many attempts. Try again in 2 minutes.');
    expect(texts.login.tooManyAttempts(600)).toBe('Too many attempts. Try again in 10 minutes.');
  });

  it('gives the wait before another mail in seconds', () => {
    expect(texts.common.waitSeconds(42)).toBe('Wait 42 seconds before asking again.');
    expect(texts.common.waitSeconds(1)).toBe('Wait 1 second before asking again.');
  });

  it('has one sentence for a used-up link and another for a used-up invitation', () => {
    expect(texts.common.invalidLink).toBe('This link has expired or was already used.');
    expect(texts.invite.invalid).toBe('This invitation has expired or was already used. Ask for a new one.');
  });

  it('says to whom an invitation sets a password', () => {
    expect(texts.invite.setsPassword('a@b.example')).toBe('This sets the password for a@b.example.');
  });

  it('is plain English text: printable ASCII, no markup', () => {
    const all = leaves(texts, 'texts', []);
    expect(all.length).toBeGreaterThan(30);
    for (const [path, text] of all) {
      expect(text, path).toMatch(/^[\x20-\x7E]+$/);
      expect(text, path).not.toMatch(/[<>]/);
    }
  });
});
```

  Run it and see it fail (the module does not exist yet):

```bash
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -15
```

  Expected: a failure that names `./texts` (`Cannot find module` or `Failed to resolve import`). It may name more than that: the
  scaffold's `src/app/app.ts` still points at `./app.html` and `./app.css`, which step 1 removed, and `src/app/app.config.ts` and
  `src/main.ts` are still the scaffold's; the first test run fails to compile for those too (the missing `app.html` and
  `app.css` among the errors). That is expected here and is gone after step 7.

- [ ] **Step 6: Write `texts.ts`.** `samples/notes-web/src/app/texts.ts`. This is the only file with words in it; the pages never
  write a sentence of their own. Strings with an apostrophe use double quotes. The `rules` are the names Auth-Core gives to the
  password rules it did not meet (`too_short`, `requires_upper`, `requires_lower`, `requires_digit`); the screens show its names,
  not a copy of its policy.

```ts
// Every text the person sees. A product changes the wording of the screens here, in one place.
// Texts that carry a number or a name are functions. Plain printable ASCII: texts.spec.ts checks it.

function minutesFrom(seconds: number): number {
  return Math.max(1, Math.ceil(seconds / 60));
}

function counted(count: number, one: string, many: string): string {
  return count === 1 ? `${count} ${one}` : `${count} ${many}`;
}

export const texts = {
  appName: 'Notes',

  common: {
    email: 'Email',
    password: 'Password',
    newPassword: 'New password',
    repeatPassword: 'Repeat the password',
    passwordsDiffer: 'The two passwords are not the same.',
    somethingWrong: 'Something went wrong. Try again.',
    invalidLink: 'This link has expired or was already used.',
    askForNewLink: 'Ask for a new link',
    sendVerificationAgain: 'Send the verification link again',
    verificationSent: 'If this address needs confirming, we sent a new link.',
    waitSeconds: (seconds: number): string => `Wait ${counted(seconds, 'second', 'seconds')} before asking again.`,
    signInLink: 'Sign in.',
    backToSignIn: 'Back to sign in',
  },

  // The bar above every screen. The keys are the notices of AuthService.
  notices: {
    unreachable: "Can't reach the server. Try again shortly.",
    try_later: 'Try again shortly.',
    forbidden: "You don't have access to this.",
    dismiss: 'Dismiss',
  },

  login: {
    title: 'Sign in',
    submit: 'Sign in',
    forgot: 'Forgot your password?',
    wrongCredentials: 'Wrong email or password.',
    notVerified: 'Your email address is not confirmed yet.',
    noMembership: 'Your account does not belong to a company.',
    tooManyAttempts: (seconds: number): string =>
      `Too many attempts. Try again in ${counted(minutesFrom(seconds), 'minute', 'minutes')}.`,
  },

  forgot: {
    title: 'Forgot your password?',
    intro: 'Enter your email address and we will send you a link to choose a new password.',
    submit: 'Send the link',
    sent: 'If an account exists for this address, we sent a link.',
  },

  reset: {
    title: 'Choose a new password',
    submit: 'Change password',
    done: 'Password changed.',
    rules: {
      too_short: 'The password is too short.',
      requires_upper: 'The password needs an uppercase letter.',
      requires_lower: 'The password needs a lowercase letter.',
      requires_digit: 'The password needs a digit.',
    } as Record<string, string>,
    ruleOther: 'The password does not meet a rule.',
  },

  verify: {
    title: 'Confirm your email',
    working: 'Confirming your email...',
    done: 'Email confirmed.',
  },

  invite: {
    title: 'Join a company',
    loading: 'Opening your invitation...',
    joinPrefix: 'Join',
    joinMiddle: 'as',
    setsPassword: (email: string): string => `This sets the password for ${email}.`,
    submit: 'Join',
    alreadyMember: 'This account already belongs to a company.',
    // Not the link texts of the other screens: a person cannot get a new invitation from this app, only from the company.
    invalid: 'This invitation has expired or was already used. Ask for a new one.',
  },

  notes: {
    title: 'Notes',
    signOut: 'Sign out',
    newNote: 'New note',
    add: 'Add note',
    empty: 'No notes yet.',
    loadFailed: 'The notes could not be loaded.',
    addFailed: 'The note could not be saved.',
  },
};
```

- [ ] **Step 7: The rest of the shell.** `samples/notes-web/src/index.html` (the `ngCspNonce` attribute is a template that
  Caddy fills in with the request's id, Task 8; it is written with **backticks** inside, because the build rewrites double
  quotes inside the attribute. Under `ng serve` the text stays as it is, which is harmless: the dev server sets no policy):

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>Notes</title>
  <base href="/">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <link rel="icon" type="image/x-icon" href="favicon.ico">
</head>
<body>
  <app-root ngCspNonce="{{placeholder `http.request.uuid`}}"></app-root>
</body>
</html>
```

  `samples/notes-web/src/main.ts`:

```ts
import { bootstrapApplication } from '@angular/platform-browser';
import { App } from './app/app';
import { appConfig } from './app/app.config';

bootstrapApplication(App, appConfig).catch((error) => console.error(error));
```

  `samples/notes-web/src/app/app.ts` (the bar for the notices comes in Task 2):

```ts
import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
export class App {}
```

  `samples/notes-web/src/app/app.config.ts`:

```ts
import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [provideBrowserGlobalErrorListeners(), provideRouter(routes)],
};
```

  `samples/notes-web/src/app/app.routes.ts` (empty until the first page exists; Tasks 5, 6 and 7 each give the whole next
  version of this file):

```ts
import { Routes } from '@angular/router';

export const routes: Routes = [];
```

  `samples/notes-web/src/styles.css` (plain CSS, no UI library; mobile first, buttons at least 44 px high):

```css
:root {
  color-scheme: light dark;
  --accent: #1f6feb;
  --danger: #b42318;
  --muted: #667085;
}

* {
  box-sizing: border-box;
}

body {
  margin: 0;
  font-family: system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif;
  line-height: 1.5;
}

main {
  max-width: 32rem;
  margin: 0 auto;
  padding: 1rem;
}

main.wide {
  max-width: 44rem;
}

h1 {
  margin: 1rem 0;
  font-size: 1.5rem;
}

form {
  display: grid;
  gap: 0.5rem;
}

label {
  font-weight: 600;
}

input,
textarea {
  width: 100%;
  padding: 0.6rem;
  font: inherit;
  border: 1px solid #98a2b3;
  border-radius: 6px;
}

button {
  min-height: 44px;
  padding: 0 1rem;
  font: inherit;
  color: #fff;
  background: var(--accent);
  border: 0;
  border-radius: 6px;
  cursor: pointer;
}

button:disabled {
  opacity: 0.6;
  cursor: default;
}

button.link {
  padding: 0;
  color: var(--accent);
  text-decoration: underline;
  background: none;
}

a {
  color: var(--accent);
}

.error {
  color: var(--danger);
}

.status {
  color: var(--muted);
}

ul.plain {
  padding: 0;
  list-style: none;
}

.top {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem 1rem;
  align-items: center;
  justify-content: space-between;
}

.who {
  display: flex;
  flex-wrap: wrap;
  gap: 0.25rem 0.75rem;
  margin: 0;
}

.note {
  padding: 0.5rem 0;
  border-bottom: 1px solid #d0d5dd;
}

.note p {
  margin: 0;
  overflow-wrap: anywhere;
  white-space: pre-wrap;
}
```

  `samples/notes-web/src/app/app.spec.ts`:

```ts
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';

describe('App', () => {
  it('is a shell with a router outlet', () => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('router-outlet')).not.toBeNull();
  });
});
```

- [ ] **Step 8: Run the tests and the production build, and check `index.html`.**

```bash
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -12
npx ng build 2>&1 | grep -v allow-scripts | tail -12
ls dist/notes-web/browser
node -e '
const html = require("fs").readFileSync("dist/notes-web/browser/index.html", "utf8");
const bad = [];
if (/<style/i.test(html)) bad.push("inline style");
if (/\son[a-z]+=/i.test(html)) bad.push("event handler");
for (const m of html.matchAll(/<script\b[^>]*>/gi)) if (!/\ssrc=/.test(m[0])) bad.push("inline script");
if (!/ngcspnonce="\{\{placeholder `http\.request\.uuid`\}\}"/i.test(html)) bad.push("nonce template missing or rewritten");
console.log(bad.length ? "BAD: " + bad.join(", ") : "index.html is clean");'
```

  Expected: both spec files pass (no failures; the `texts` file has 4 tests, `app.spec.ts` 1); the build ends with
  `Application bundle generation complete` and may warn about nothing; `ls` shows `index.html`, `favicon.ico`, hashed
  `main-*.js` and `styles-*.css`; the last command prints `index.html is clean`. If the `texts` test complains about a
  character, fix the text, not the test.

- [ ] **Step 9: Hand over** - uncommitted. Check `git status --porcelain` shows only `samples/notes-web/` (no `node_modules`,
  `dist` or `.angular`). The orchestrator commits it as `feat(web): Angular app scaffold with pinned packages and texts`.

### Task 2: `AuthService`, the silent refresh at start, the bar

**Files:**
- Create: `samples/notes-web/src/app/auth/auth.service.ts`, `src/testing/helpers.ts`
- Modify: `samples/notes-web/src/app/app.ts`, `src/app/app.config.ts`
- Test: `samples/notes-web/src/app/auth/auth.service.spec.ts`, `src/app/app.spec.ts` (replaced), `src/app/app.config.spec.ts`

**Interfaces:**
- Consumes: `texts.notices` (Task 1).
- Produces, in `auth.service.ts`: `interface Me { sub; email; org_id; org_name; roles: string[]; permissions: string[] }`;
  `type Failure` (the kinds `invalid_credentials`, `email_not_verified`, `no_membership`, `invalid_token`, `weak_password` with
  `rules: string[]`, `already_member`, `too_many_attempts` with `retryAfterSeconds: number`, `other`); `failureOf(error:
  unknown): Failure`; `errorCode(error: HttpErrorResponse): string | undefined`; `isRecord(value: unknown)`; `type
  RefreshResult = 'ok' | 'rejected' | 'unavailable'`; `type AuthNotice = 'unreachable' | 'try_later' | 'forbidden'`; `type
  LoginResult = { ok: true } | { ok: false; failure: Failure }`; `class AuthService` with the readonly signals `token`
  (`string | null`), `me` (`Me | null`), `notice` (`AuthNotice | null`) and the methods `start(): Promise<void>`,
  `login(email, password): Promise<LoginResult>`, `logout(): Promise<void>`, `refresh(): Promise<RefreshResult>` (one at a
  time: callers that ask while one runs share it), `loadMe(giveUpAfterMs?: number): Promise<boolean>` (true only for an answer of the contract's shape),
  `dropSession(): void` (it also ends every answer still on its way),
  `showNotice(notice: AuthNotice): void`, `clearNotice(): void`.
- Produces, in `src/testing/helpers.ts` (test code only, outside the application's program): `settle(fixture?)`, `adminMe`,
  `viewerMe`, `holdToken(auth, ctrl, token)`, `signedInAs(auth, ctrl, me?, token?)`. Task 5 adds more to the same file.
- Produces: an application that calls `POST /auth/refresh` once before the first route is resolved.

**Choices:** The token lives in one private signal of `AuthService` and is never written anywhere. `refresh()` only exchanges
the cookie for a token; `/auth/me` is asked by the callers (`start`, `login`), because a refresh that waited for `/auth/me`
would wait for the interceptor, which waits for the refresh (Task 3). A refresh that does not answer within 10 seconds counts
as "no network": the app must not sit blank behind the initializer. `login` and the failures of every account screen share
`failureOf`, so the answers of Auth-Core are read in one place.

- [ ] **Step 1: The test helpers.** `samples/notes-web/src/testing/helpers.ts`:

```ts
import { ComponentFixture } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { AuthService, Me } from '../app/auth/auth.service';

/** Lets pending promises and timers run, then draws the component again. */
export async function settle(fixture?: ComponentFixture<unknown>): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve));
  fixture?.detectChanges();
}

export const adminMe: Me = {
  sub: '11111111-1111-1111-1111-111111111111',
  email: 'admin@example.test',
  org_id: '22222222-2222-2222-2222-222222222222',
  org_name: 'Acme',
  roles: ['admin'],
  permissions: ['notes:read', 'notes:write'],
};

export const viewerMe: Me = {
  ...adminMe,
  email: 'viewer@example.test',
  roles: ['viewer'],
  permissions: ['notes:read'],
};

/** Gives the service a token the way the app does: by a refresh that the test answers. */
export async function holdToken(auth: AuthService, ctrl: HttpTestingController, token: string): Promise<void> {
  const done = auth.refresh();
  ctrl.expectOne('/auth/refresh').flush({ access_token: token });
  await done;
}

/** A signed-in person: a token and the answer of /auth/me. */
export async function signedInAs(
  auth: AuthService,
  ctrl: HttpTestingController,
  me: Me = adminMe,
  token = 'tok',
): Promise<void> {
  await holdToken(auth, ctrl, token);
  const loaded = auth.loadMe();
  ctrl.expectOne('/auth/me').flush(me);
  await loaded;
}
```

- [ ] **Step 2: Write the failing tests.** `samples/notes-web/src/app/auth/auth.service.spec.ts`:

```ts
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { vi } from 'vitest';
import { adminMe, holdToken, settle, signedInAs } from '../../testing/helpers';
import { AuthService, failureOf } from './auth.service';

const status = (code: number) => ({ status: code, statusText: String(code) });

describe('AuthService', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    vi.useRealTimers();
    localStorage.clear();
    sessionStorage.clear();
  });

  describe('at start', () => {
    it('200: keeps the token, then asks /auth/me; the person is signed in', async () => {
      const started = auth.start();
      const refresh = ctrl.expectOne('/auth/refresh');
      expect(refresh.request.method).toBe('POST');
      refresh.flush({ access_token: 'tok-1' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      await started;
      expect(auth.token()).toBe('tok-1');
      expect(auth.me()).toEqual(adminMe);
      expect(auth.notice()).toBeNull();
      ctrl.verify();
    });

    it('401: the person is anonymous and nothing is shown', async () => {
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').flush({ error: 'invalid_grant' }, status(401));
      await started;
      expect(auth.token()).toBeNull();
      expect(auth.me()).toBeNull();
      expect(auth.notice()).toBeNull();
      ctrl.verify();
    });

    it('anything else: anonymous, and the bar says the server cannot be reached', async () => {
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').flush('<html>bad gateway</html>', status(502));
      await started;
      expect(auth.token()).toBeNull();
      expect(auth.notice()).toBe('unreachable');
      ctrl.verify();
    });

    it('no network: anonymous, and the bar says the server cannot be reached', async () => {
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').error(new ProgressEvent('error'));
      await started;
      expect(auth.token()).toBeNull();
      expect(auth.notice()).toBe('unreachable');
      ctrl.verify();
    });

    it('a 200 without a token is not a session', async () => {
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').flush({});
      await started;
      expect(auth.token()).toBeNull();
      expect(auth.notice()).toBe('unreachable');
      ctrl.verify();
    });

    it('a refresh that never answers is given up after ten seconds', async () => {
      vi.useFakeTimers();
      const started = auth.start();
      ctrl.expectOne('/auth/refresh');
      await vi.advanceTimersByTimeAsync(10_001);
      await started;
      expect(auth.token()).toBeNull();
      expect(auth.notice()).toBe('unreachable');
    });

    it('a token whose /auth/me cannot be read still counts as a session, with the bar', async () => {
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-1' });
      await settle();
      ctrl.expectOne('/auth/me').flush('oops', status(500));
      await started;
      expect(auth.token()).toBe('tok-1');
      expect(auth.me()).toBeNull();
      expect(auth.notice()).toBe('unreachable');
      ctrl.verify();
    });

    it('a /auth/me that never answers is given up after ten seconds too, with the bar', async () => {
      vi.useFakeTimers();
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-1' });
      await vi.advanceTimersByTimeAsync(1);
      ctrl.expectOne('/auth/me');
      await vi.advanceTimersByTimeAsync(10_001);
      await started;
      expect(auth.token()).toBe('tok-1');
      expect(auth.me()).toBeNull();
      expect(auth.notice()).toBe('unreachable');
    });

    it.each([
      ['a page of HTML', '<html>bad gateway</html>'],
      ['an object without roles', { sub: 's', email: 'a@b.example', org_id: 'o', org_name: 'Acme', permissions: [] }],
      ['roles that are not text', { sub: 's', email: 'a@b.example', org_id: 'o', org_name: 'Acme', roles: [1], permissions: [] }],
      ['null', null],
    ])('a 200 from /auth/me that is %s is not a person: no crash, no details, the bar', async (_name, body) => {
      const started = auth.start();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok-1' });
      await settle();
      ctrl.expectOne('/auth/me').flush(body);
      await started;
      expect(auth.me()).toBeNull();
      expect(auth.token()).toBe('tok-1');
      expect(auth.notice()).toBe('unreachable');
      ctrl.verify();
    });
  });

  describe('login', () => {
    it('keeps the token in memory and loads /auth/me', async () => {
      const done = auth.login('admin@example.test', 'pw');
      const request = ctrl.expectOne('/auth/login');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ email: 'admin@example.test', password: 'pw' });
      request.flush({ status: 'authenticated', access_token: 'tok-9' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      expect(await done).toEqual({ ok: true });
      expect(auth.token()).toBe('tok-9');
      expect(auth.me()).toEqual(adminMe);
      ctrl.verify();
    });

    it('never writes the token to localStorage, sessionStorage or a cookie', async () => {
      await signedInAs(auth, ctrl, adminMe, 'secret-token-value');
      expect(JSON.stringify({ ...localStorage })).not.toContain('secret-token-value');
      expect(JSON.stringify({ ...sessionStorage })).not.toContain('secret-token-value');
      expect(document.cookie).not.toContain('secret-token-value');
      expect(localStorage.length + sessionStorage.length).toBe(0);
    });

    it.each([
      [401, { error: 'invalid_credentials' }, 'invalid_credentials'],
      [403, { error: 'email_not_verified' }, 'email_not_verified'],
      [403, { error: 'no_membership' }, 'no_membership'],
      [429, { error: 'too_many_attempts', retry_after_seconds: 90 }, 'too_many_attempts'],
      [400, { error: 'invalid_request' }, 'other'],
      [500, null, 'other'],
    ])('answer %s %j is the failure %s and leaves the person anonymous', async (code, body, kind) => {
      const done = auth.login('a@b.example', 'pw');
      ctrl.expectOne('/auth/login').flush(body, status(code));
      const result = await done;
      expect(result.ok).toBe(false);
      if (!result.ok) {
        expect(result.failure.kind).toBe(kind);
        if (result.failure.kind === 'too_many_attempts') {
          expect(result.failure.retryAfterSeconds).toBe(90);
        }
      }
      expect(auth.token()).toBeNull();
      ctrl.verify();
    });

    it('a 200 that is not "authenticated" is not a session', async () => {
      const done = auth.login('a@b.example', 'pw');
      ctrl.expectOne('/auth/login').flush({ status: 'mfa_required' });
      expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
      expect(auth.token()).toBeNull();
    });

    it('clears an old notice when the person signs in', async () => {
      auth.showNotice('unreachable');
      const done = auth.login('a@b.example', 'pw');
      ctrl.expectOne('/auth/login').flush({ status: 'authenticated', access_token: 't' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      await done;
      expect(auth.notice()).toBeNull();
    });
  });

  describe('logout', () => {
    it.each([
      ['204', () => ({ body: null, init: status(204) })],
      ['a 500', () => ({ body: 'oops', init: status(500) })],
    ])('drops the token and the person after %s', async (_name, answer) => {
      await signedInAs(auth, ctrl);
      const done = auth.logout();
      const request = ctrl.expectOne('/auth/logout');
      expect(request.request.method).toBe('POST');
      const { body, init } = answer();
      request.flush(body, init);
      await done;
      expect(auth.token()).toBeNull();
      expect(auth.me()).toBeNull();
    });

    it('drops the token when the network fails', async () => {
      await signedInAs(auth, ctrl);
      const done = auth.logout();
      ctrl.expectOne('/auth/logout').error(new ProgressEvent('error'));
      await done;
      expect(auth.token()).toBeNull();
      expect(auth.me()).toBeNull();
    });
  });

  describe('refresh', () => {
    it('one request for any number of callers that ask while it runs', async () => {
      const first = auth.refresh();
      const second = auth.refresh();
      const third = auth.refresh();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      expect(await Promise.all([first, second, third])).toEqual(['ok', 'ok', 'ok']);
      expect(auth.token()).toBe('new');
      const later = auth.refresh();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'newer' });
      await later;
      expect(auth.token()).toBe('newer');
    });

    it('401 is rejected: the token is dropped', async () => {
      await holdToken(auth, ctrl, 'old');
      const done = auth.refresh();
      ctrl.expectOne('/auth/refresh').flush({ error: 'invalid_grant' }, status(401));
      expect(await done).toBe('rejected');
      expect(auth.token()).toBeNull();
    });

    it('any other failure is unavailable: the token is kept', async () => {
      await holdToken(auth, ctrl, 'old');
      const done = auth.refresh();
      ctrl.expectOne('/auth/refresh').flush('oops', status(503));
      expect(await done).toBe('unavailable');
      expect(auth.token()).toBe('old');
    });

    it('an answer that was on its way when the person signed out does not bring the session back', async () => {
      await signedInAs(auth, ctrl, adminMe, 'old');
      const refreshing = auth.refresh();
      const refreshRequest = ctrl.expectOne('/auth/refresh');
      const loading = auth.loadMe();
      const meRequest = ctrl.expectOne('/auth/me');
      const signingOut = auth.logout();
      ctrl.expectOne('/auth/logout').flush(null, status(204));
      await signingOut;
      refreshRequest.flush({ access_token: 'late' });
      meRequest.flush(adminMe);
      expect(await refreshing).toBe('rejected');
      expect(await loading).toBe(false);
      expect(auth.token()).toBeNull();
      expect(auth.me()).toBeNull();
    });
  });

  describe('notices', () => {
    it('can be shown and cleared', () => {
      expect(auth.notice()).toBeNull();
      auth.showNotice('try_later');
      expect(auth.notice()).toBe('try_later');
      auth.clearNotice();
      expect(auth.notice()).toBeNull();
    });
  });
});

describe('failureOf', () => {
  const failure = (code: number, body: unknown) =>
    failureOf(new HttpErrorResponse({ status: code, error: body }));

  it('reads the rules of a weak password and ignores what is not a rule name', () => {
    expect(failure(400, { error: 'weak_password', rules: ['too_short', 7, 'requires_digit'] })).toEqual({
      kind: 'weak_password',
      rules: ['too_short', 'requires_digit'],
    });
  });

  it('reads invalid_token and already_member', () => {
    expect(failure(400, { error: 'invalid_token' })).toEqual({ kind: 'invalid_token' });
    expect(failure(409, { error: 'already_member' })).toEqual({ kind: 'already_member' });
  });

  it('takes a minute when a 429 does not say how long to wait', () => {
    expect(failure(429, { error: 'too_many_attempts' })).toEqual({ kind: 'too_many_attempts', retryAfterSeconds: 60 });
  });

  it('is "other" for what it does not know: another status, a body that is not JSON, no HTTP error at all', () => {
    expect(failure(500, { error: 'invalid_token' })).toEqual({ kind: 'other' });
    expect(failure(400, '<html>bad request</html>')).toEqual({ kind: 'other' });
    expect(failure(0, new ProgressEvent('error'))).toEqual({ kind: 'other' });
    expect(failureOf(new Error('boom'))).toEqual({ kind: 'other' });
  });
});
```

  Run it and see it fail:

```bash
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -15
```

  Expected: failure to resolve `./auth.service`.

- [ ] **Step 3: Write the service.** `samples/notes-web/src/app/auth/auth.service.ts`. The only thing that holds the access
  token. Copy this file with the interceptor and the guard.

```ts
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom, timeout } from 'rxjs';

/** What GET /auth/me answers. The app never decodes the access token: it asks. */
export interface Me {
  sub: string;
  email: string;
  org_id: string;
  org_name: string;
  roles: string[];
  permissions: string[];
}

/** What went wrong with a call to an account endpoint, as far as a screen cares. */
export type Failure =
  | { kind: 'invalid_credentials' }
  | { kind: 'email_not_verified' }
  | { kind: 'no_membership' }
  | { kind: 'invalid_token' }
  | { kind: 'weak_password'; rules: string[] }
  | { kind: 'already_member' }
  | { kind: 'too_many_attempts'; retryAfterSeconds: number }
  | { kind: 'other' };

export type RefreshResult = 'ok' | 'rejected' | 'unavailable';

/** The messages of the bar above the screens. */
export type AuthNotice = 'unreachable' | 'try_later' | 'forbidden';

export type LoginResult = { ok: true } | { ok: false; failure: Failure };

const REFRESH_TIMEOUT_MS = 10_000;
const DEFAULT_WAIT_SECONDS = 60;

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** The "error" of a JSON error body (`{"error":"forbidden"}`), or undefined when the body is anything else. */
export function errorCode(error: HttpErrorResponse): string | undefined {
  const body: unknown = error.error;
  return isRecord(body) && typeof body['error'] === 'string' ? body['error'] : undefined;
}

/** Reads the answers of Auth-Core's account endpoints. Anything it does not know is "other". */
export function failureOf(error: unknown): Failure {
  if (!(error instanceof HttpErrorResponse)) {
    return { kind: 'other' };
  }
  const code = errorCode(error);
  if (error.status === 429 && code === 'too_many_attempts') {
    const seconds = isRecord(error.error) ? error.error['retry_after_seconds'] : undefined;
    return {
      kind: 'too_many_attempts',
      retryAfterSeconds: typeof seconds === 'number' && seconds > 0 ? seconds : DEFAULT_WAIT_SECONDS,
    };
  }
  if (error.status === 401 && code === 'invalid_credentials') {
    return { kind: 'invalid_credentials' };
  }
  if (error.status === 403 && code === 'email_not_verified') {
    return { kind: 'email_not_verified' };
  }
  if (error.status === 403 && code === 'no_membership') {
    return { kind: 'no_membership' };
  }
  if (error.status === 400 && code === 'invalid_token') {
    return { kind: 'invalid_token' };
  }
  if (error.status === 400 && code === 'weak_password') {
    const rules = isRecord(error.error) && Array.isArray(error.error['rules']) ? error.error['rules'] : [];
    return { kind: 'weak_password', rules: rules.filter((rule): rule is string => typeof rule === 'string') };
  }
  if (error.status === 409 && code === 'already_member') {
    return { kind: 'already_member' };
  }
  return { kind: 'other' };
}

interface LoginBody {
  status?: unknown;
  access_token?: unknown;
}

interface RefreshBody {
  access_token?: unknown;
}

function isStrings(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string');
}

/** The answer of GET /auth/me if it has the shape of the contract, else null: a page of HTML from a proxy is not a person. */
function asMe(body: unknown): Me | null {
  if (
    isRecord(body) &&
    typeof body['sub'] === 'string' &&
    typeof body['email'] === 'string' &&
    typeof body['org_id'] === 'string' &&
    typeof body['org_name'] === 'string' &&
    isStrings(body['roles']) &&
    isStrings(body['permissions'])
  ) {
    return {
      sub: body['sub'],
      email: body['email'],
      org_id: body['org_id'],
      org_name: body['org_name'],
      roles: body['roles'],
      permissions: body['permissions'],
    };
  }
  return null;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly accessToken = signal<string | null>(null);
  private readonly meState = signal<Me | null>(null);
  private readonly noticeState = signal<AuthNotice | null>(null);
  private refreshing: Promise<RefreshResult> | null = null;
  // Counts the sessions that ended in this tab: an answer that was on its way when the person signed out is never used.
  private generation = 0;

  /** The access token, or null when nobody is signed in. Held here, in memory, and nowhere else. */
  readonly token = this.accessToken.asReadonly();
  /** Who is signed in, as GET /auth/me says. */
  readonly me = this.meState.asReadonly();
  /** The message of the bar, if there is one. */
  readonly notice = this.noticeState.asReadonly();

  /** Run once before the first route is resolved (app.config.ts): is there a session to resume? */
  async start(): Promise<void> {
    const result = await this.refresh();
    if (result === 'unavailable') {
      this.noticeState.set('unreachable');
    } else if (result === 'ok' && !(await this.loadMe(REFRESH_TIMEOUT_MS)) && this.noticeState() === null) {
      // The same ten seconds as the refresh: the app must not sit blank behind the initializer.
      this.noticeState.set('unreachable');
    }
  }

  async login(email: string, password: string): Promise<LoginResult> {
    let body: LoginBody | null;
    try {
      body = await firstValueFrom(this.http.post<LoginBody | null>('/auth/login', { email, password }));
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
    const token = body?.access_token;
    if (body?.status !== 'authenticated' || typeof token !== 'string' || token === '') {
      return { ok: false, failure: { kind: 'other' } };
    }
    this.accessToken.set(token);
    this.noticeState.set(null);
    await this.loadMe();
    return { ok: true };
  }

  /** Ends the session: whatever the server answers, the token is dropped. */
  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.http.post('/auth/logout', null));
    } catch {
      // The answer does not matter: this tab is signed out either way.
    }
    this.dropSession();
  }

  /**
   * Exchanges the refresh cookie for a new access token. Callers that ask while a refresh runs get that refresh:
   * there is never more than one request at a time.
   */
  refresh(): Promise<RefreshResult> {
    this.refreshing ??= this.exchange().finally(() => {
      this.refreshing = null;
    });
    return this.refreshing;
  }

  /**
   * Asks GET /auth/me. True when an answer of the contract's shape was read. `giveUpAfterMs` ends the wait for an answer that
   * does not come (the start uses it); an answer that arrives after the person signed out is ignored.
   */
  async loadMe(giveUpAfterMs?: number): Promise<boolean> {
    const started = this.generation;
    try {
      const request = this.http.get<unknown>('/auth/me');
      const body = await firstValueFrom(giveUpAfterMs === undefined ? request : request.pipe(timeout(giveUpAfterMs)));
      const me = asMe(body);
      if (me === null || this.generation !== started) {
        return false;
      }
      this.meState.set(me);
      return true;
    } catch {
      return false;
    }
  }

  dropSession(): void {
    this.generation += 1;
    this.accessToken.set(null);
    this.meState.set(null);
  }

  showNotice(notice: AuthNotice): void {
    this.noticeState.set(notice);
  }

  clearNotice(): void {
    this.noticeState.set(null);
  }

  private async exchange(): Promise<RefreshResult> {
    const started = this.generation;
    try {
      const body = await firstValueFrom(
        this.http.post<RefreshBody | null>('/auth/refresh', null).pipe(timeout(REFRESH_TIMEOUT_MS)),
      );
      if (this.generation !== started) {
        // The person signed out while this was on its way: the session it would renew is gone. (The interceptor then goes
        // to /login, where the person already is or is on the way.)
        return 'rejected';
      }
      const token = body?.access_token;
      if (typeof token !== 'string' || token === '') {
        return 'unavailable';
      }
      this.accessToken.set(token);
      return 'ok';
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 401) {
        this.dropSession();
        return 'rejected';
      }
      return 'unavailable';
    }
  }
}
```

- [ ] **Step 4: Run the service tests.**

```bash
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -15
```

  Expected: `auth.service.spec.ts` passes in full. If a test of the first group fails because `ctrl.verify()` finds an open
  `/auth/me`, the test forgot to answer it, not the service.

- [ ] **Step 5: The bar, and the initializer.** Replace `samples/notes-web/src/app/app.ts`. The component style is on purpose: Angular
  adds it as a `<style>` element while the app runs, and that element carries the nonce of Task 8, so the end-to-end test
  of the headers proves the nonce works. The bar has no timer: it is dismissed by the person, or replaced by the next one, or
  cleared by a sign-in.

```ts
import { Component, computed, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { AuthService } from './auth/auth.service';
import { texts } from './texts';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: `
    @if (notice(); as text) {
      <div class="bar" role="alert" data-testid="notice">
        <span data-testid="notice-text">{{ text }}</span>
        <button type="button" (click)="dismiss()">{{ dismissLabel }}</button>
      </div>
    }
    <router-outlet />
  `,
  styles: [
    `
      .bar {
        display: flex;
        gap: 1rem;
        align-items: center;
        justify-content: space-between;
        padding: 0.5rem 1rem;
        color: #3b2f00;
        background: #fef3c7;
      }
      .bar button {
        min-height: 32px;
        color: inherit;
        text-decoration: underline;
        background: none;
      }
    `,
  ],
})
export class App {
  private readonly auth = inject(AuthService);
  protected readonly dismissLabel = texts.notices.dismiss;
  protected readonly notice = computed(() => {
    const kind = this.auth.notice();
    return kind === null ? null : texts.notices[kind];
  });

  protected dismiss(): void {
    this.auth.clearNotice();
  }
}
```

  Replace `samples/notes-web/src/app/app.config.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { AuthService } from './auth/auth.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(),
    // Before the first route is resolved: is there a session to resume? (a refresh with the cookie)
    provideAppInitializer(() => inject(AuthService).start()),
  ],
};
```

  Replace `samples/notes-web/src/app/app.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { settle } from '../testing/helpers';
import { App } from './app';
import { AuthService } from './auth/auth.service';

describe('App', () => {
  function open() {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    return { fixture, auth: TestBed.inject(AuthService), root: fixture.nativeElement as HTMLElement };
  }

  it('is a shell with a router outlet and no bar while there is nothing to say', () => {
    const { root } = open();
    expect(root.querySelector('router-outlet')).not.toBeNull();
    expect(root.querySelector('[data-testid="notice"]')).toBeNull();
  });

  it.each([
    ['unreachable', "Can't reach the server. Try again shortly."],
    ['try_later', 'Try again shortly.'],
    ['forbidden', "You don't have access to this."],
  ] as const)('shows the bar for the notice %s', async (notice, text) => {
    const { fixture, auth, root } = open();
    auth.showNotice(notice);
    await settle(fixture);
    expect(root.querySelector('[data-testid="notice-text"]')?.textContent).toBe(text);
  });

  it('lets the person dismiss the bar', async () => {
    const { fixture, auth, root } = open();
    auth.showNotice('try_later');
    await settle(fixture);
    root.querySelector<HTMLButtonElement>('[data-testid="notice"] button')?.click();
    await settle(fixture);
    expect(root.querySelector('[data-testid="notice"]')).toBeNull();
    expect(auth.notice()).toBeNull();
  });
});
```

  And `samples/notes-web/src/app/app.config.spec.ts` (the initializer is wired: it runs before the app is ready and asks
  for the refresh):

```ts
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { ApplicationInitStatus } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { adminMe, settle } from '../testing/helpers';
import { appConfig } from './app.config';
import { AuthService } from './auth/auth.service';

describe('appConfig', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [...appConfig.providers, provideHttpClientTesting()] });
  });

  it('resumes a session before the app is ready: one refresh, then /auth/me', async () => {
    const status = TestBed.inject(ApplicationInitStatus);
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne('/auth/refresh').flush({ access_token: 'tok' });
    await settle();
    ctrl.expectOne('/auth/me').flush(adminMe);
    await status.donePromise;
    expect(TestBed.inject(AuthService).token()).toBe('tok');
    ctrl.verify();
  });

  it('starts anonymous when the cookie is refused', async () => {
    const status = TestBed.inject(ApplicationInitStatus);
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne('/auth/refresh').flush({ error: 'invalid_grant' }, { status: 401, statusText: 'Unauthorized' });
    await status.donePromise;
    expect(TestBed.inject(AuthService).token()).toBeNull();
    ctrl.verify();
  });
});
```

- [ ] **Step 6: Run everything.**

```bash
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -15
npx ng build 2>&1 | grep -v allow-scripts | tail -5
grep -rn "localStorage\|sessionStorage\|document.cookie" src --include=*.ts | grep -v "\.spec\.ts"
```

  Expected: all tests pass; the build completes; the last `grep` prints nothing (the application code never touches the
  storage or the cookies).

- [ ] **Step 7: Hand over** - uncommitted; suggested message `feat(web): AuthService with silent refresh at start and the notice bar`.

### Task 3: The interceptor

**Files:**
- Create: `samples/notes-web/src/app/auth/auth.interceptor.ts`
- Modify: `samples/notes-web/src/app/app.config.ts`
- Test: `samples/notes-web/src/app/auth/auth.interceptor.spec.ts`

**Interfaces:**
- Consumes: `AuthService` (`token()`, `refresh()`, `loadMe()`, `showNotice()`) and `errorCode` from Task 2; Angular's `Router`.
- Produces: `authInterceptor: HttpInterceptorFn`; `wantsToken(url: string, origin?: string): boolean`; the two lists
  `TOKEN_PATH_PREFIXES` (`['/api/', '/auth/org']`) and `TOKEN_PATHS` (`['/auth/me']`), which a product edits (guide, step 3).

**The rules (spec, "Session"):** the token goes only to the app's own origin and only to a path that starts with `/api/`, is
`/auth/me` or starts with `/auth/org`; never to another origin and never to the other `/auth` endpoints. The answers to those
requests: `401` and `403 permissions_changed` get one refresh and the request again, once; `403 forbidden` gets the message and
no refresh; `503 auth_unavailable` gets the bar and the person stays signed in; a refresh that answers `401` ends the session
and sends the person to `/login?returnUrl=<the current path>`. The spec also says (under its table): a refresh that fails with
anything other than `401` (no network, `5xx`) keeps the token and shows "Can't reach the server. Try again shortly." and the caller
gets its original answer; a `401` on the retried request is passed to the caller as it is; a `401` on a request sent with a token
that has since been renewed is retried with the new token, without another refresh; and after a `permissions_changed` refresh
`/auth/me` is asked again before the request is sent again, so that the header and the add form follow the new role. One
reading of the plan's own: `/auth/me` is asked again at the same time as the request is sent again, not before it, and not for a
request that is itself `/auth/me`.

- [ ] **Step 1: Write the failing tests.** `samples/notes-web/src/app/auth/auth.interceptor.spec.ts`:

```ts
import { Component } from '@angular/core';
import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { lastValueFrom } from 'rxjs';
import { vi } from 'vitest';
import { adminMe, holdToken, settle } from '../../testing/helpers';
import { authInterceptor, wantsToken } from './auth.interceptor';
import { AuthService } from './auth.service';

@Component({ template: '' })
class Blank {}

const status = (code: number) => ({ status: code, statusText: String(code) });

describe('wantsToken', () => {
  const origin = 'https://app.example';

  it.each(['/api/notes', '/api/', '/auth/me', '/auth/org', '/auth/org/members', 'https://app.example/api/notes'])(
    'is true for %s',
    (url) => expect(wantsToken(url, origin)).toBe(true),
  );

  it.each([
    '/auth/login',
    '/auth/refresh',
    '/auth/logout',
    '/auth/password/forgot',
    '/auth/me/other',
    '/api',
    '/apiary',
    '/notes',
    'https://evil.example/api/notes',
    '//evil.example/api/notes',
    'https://app.example.evil.example/api/notes',
    '/api/../auth/login',
    'not a url at all %',
  ])('is false for %s', (url) => expect(wantsToken(url, origin)).toBe(false));
});

describe('authInterceptor', () => {
  let http: HttpClient;
  let ctrl: HttpTestingController;
  let auth: AuthService;
  let router: Router;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'notes', component: Blank },
          { path: 'login', component: Blank },
        ]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    ctrl = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthService);
    router = TestBed.inject(Router);
    await router.navigateByUrl('/notes');
    await holdToken(auth, ctrl, 'old');
  });

  afterEach(() => ctrl.verify());

  describe('which requests get the token', () => {
    it.each(['/api/notes', '/auth/me', '/auth/org', '/auth/org/members'])('sends it to %s', async (url) => {
      const done = lastValueFrom(http.get(url));
      const request = ctrl.expectOne(url);
      expect(request.request.headers.get('Authorization')).toBe('Bearer old');
      request.flush({});
      await done;
    });

    it('sends it to the app origin when the URL is absolute', async () => {
      const url = `${window.location.origin}/api/notes`;
      const done = lastValueFrom(http.get(url));
      const request = ctrl.expectOne(url);
      expect(request.request.headers.get('Authorization')).toBe('Bearer old');
      request.flush({});
      await done;
    });

    it.each([
      '/auth/login',
      '/auth/refresh',
      '/auth/logout',
      '/auth/password/forgot',
      '/auth/invites/accept',
      'https://evil.example/api/notes',
      '//evil.example/api/notes',
      '/api/../auth/login',
    ])('does not send it to %s', async (url) => {
      const done = lastValueFrom(http.get(url));
      const request = ctrl.expectOne(url);
      expect(request.request.headers.has('Authorization')).toBe(false);
      request.flush({});
      await done;
    });
  });

  describe('401', () => {
    it('renews the token once and sends the request again with it', async () => {
      const done = lastValueFrom(http.get('/api/notes'));
      ctrl.expectOne('/api/notes').flush(null, status(401));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      const again = ctrl.expectOne('/api/notes');
      expect(again.request.headers.get('Authorization')).toBe('Bearer new');
      again.flush([{ id: '1' }]);
      expect(await done).toEqual([{ id: '1' }]);
      expect(auth.token()).toBe('new');
    });

    it('one refresh for several 401s at once, then every request again', async () => {
      const urls = ['/api/a', '/api/b', '/api/c'];
      const calls = urls.map((url) => lastValueFrom(http.get(url)));
      const first = urls.map((url) => ctrl.expectOne(url));
      first.forEach((request) => request.flush(null, status(401)));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      for (const url of urls) {
        const again = ctrl.expectOne(url);
        expect(again.request.headers.get('Authorization')).toBe('Bearer new');
        again.flush(url);
      }
      expect(await Promise.all(calls)).toEqual(urls);
    });

    it('a 401 that arrives after another request renewed the token is sent again without a refresh', async () => {
      const a = lastValueFrom(http.get('/api/a'));
      const b = lastValueFrom(http.get('/api/b'));
      const firstA = ctrl.expectOne('/api/a');
      const firstB = ctrl.expectOne('/api/b');
      firstA.flush(null, status(401));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      ctrl.expectOne('/api/a').flush('a');
      firstB.flush(null, status(401));
      await settle();
      const againB = ctrl.expectOne('/api/b');
      expect(againB.request.headers.get('Authorization')).toBe('Bearer new');
      againB.flush('b');
      expect(await Promise.all([a, b])).toEqual(['a', 'b']);
      ctrl.expectNone('/auth/refresh');
    });

    it('401 from the refresh: the token is dropped and the person goes to /login with the page they were on', async () => {
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush(null, status(401));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ error: 'invalid_grant' }, status(401));
      const error = await done;
      expect(error).toBeInstanceOf(HttpErrorResponse);
      expect((error as HttpErrorResponse).status).toBe(401);
      expect(auth.token()).toBeNull();
      await vi.waitFor(() => expect(router.url).toBe('/login?returnUrl=%2Fnotes'));
    });

    it('does not send a second request, and no second refresh, when the retried request gets a 401', async () => {
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush(null, status(401));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      ctrl.expectOne('/api/notes').flush(null, status(401));
      const error = await done;
      expect((error as HttpErrorResponse).status).toBe(401);
      await settle();
      ctrl.expectNone('/api/notes');
      ctrl.expectNone('/auth/refresh');
      expect(auth.token()).toBe('new');
    });

    it('a refresh that fails for another reason shows the bar, keeps the token and hands over the original answer', async () => {
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush(null, status(401));
      await settle();
      ctrl.expectOne('/auth/refresh').flush('oops', status(502));
      const error = await done;
      expect((error as HttpErrorResponse).status).toBe(401);
      expect(auth.notice()).toBe('unreachable');
      expect(auth.token()).toBe('old');
      expect(router.url).toBe('/notes');
    });

    it('does nothing about a 401 when the session already ended', async () => {
      auth.dropSession();
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush(null, status(401));
      const error = await done;
      expect((error as HttpErrorResponse).status).toBe(401);
      await settle();
      ctrl.expectNone('/auth/refresh');
    });
  });

  describe('403', () => {
    it('forbidden: the message, no refresh, the same token', async () => {
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush({ error: 'forbidden' }, status(403));
      const error = await done;
      expect((error as HttpErrorResponse).status).toBe(403);
      await settle();
      ctrl.expectNone('/auth/refresh');
      expect(auth.notice()).toBe('forbidden');
      expect(auth.token()).toBe('old');
    });

    it('permissions_changed: one refresh, the request again once, and the person asked for again', async () => {
      const done = lastValueFrom(http.get('/api/notes'));
      ctrl.expectOne('/api/notes').flush({ error: 'permissions_changed' }, status(403));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      const again = ctrl.expectOne('/api/notes');
      expect(again.request.headers.get('Authorization')).toBe('Bearer new');
      again.flush([]);
      expect(await done).toEqual([]);
      await settle();
      expect(auth.me()).toEqual(adminMe);
      expect(auth.notice()).toBeNull();
    });

    it('permissions_changed on the retried request: no second refresh, no third request', async () => {
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush({ error: 'permissions_changed' }, status(403));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      ctrl.expectOne('/api/notes').flush({ error: 'permissions_changed' }, status(403));
      const error = await done;
      expect((error as HttpErrorResponse).status).toBe(403);
      await settle();
      ctrl.expectNone('/auth/refresh');
      ctrl.expectNone('/api/notes');
    });

    it('permissions_changed on /auth/me itself does not ask /auth/me again', async () => {
      const done = lastValueFrom(http.get('/auth/me'));
      ctrl.expectOne('/auth/me').flush({ error: 'permissions_changed' }, status(403));
      await settle();
      ctrl.expectOne('/auth/refresh').flush({ access_token: 'new' });
      await settle();
      ctrl.expectOne('/auth/me').flush(adminMe);
      expect(await done).toEqual(adminMe);
    });
  });

  describe('503', () => {
    it('auth_unavailable: the bar, and the person stays signed in', async () => {
      const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
      ctrl.expectOne('/api/notes').flush({ error: 'auth_unavailable' }, status(503));
      const error = await done;
      expect((error as HttpErrorResponse).status).toBe(503);
      await settle();
      ctrl.expectNone('/auth/refresh');
      expect(auth.notice()).toBe('try_later');
      expect(auth.token()).toBe('old');
    });
  });

  describe('other answers', () => {
    it('pass through untouched: a 404, a 500 with a page of HTML, a 503 that is not auth_unavailable', async () => {
      for (const [code, body] of [
        [404, { error: 'not_found' }],
        [500, '<html>oops</html>'],
        [503, 'down for maintenance'],
      ] as const) {
        const done = lastValueFrom(http.get('/api/notes')).catch((error: unknown) => error);
        ctrl.expectOne('/api/notes').flush(body, status(code));
        expect(((await done) as HttpErrorResponse).status).toBe(code);
      }
      await settle();
      ctrl.expectNone('/auth/refresh');
      expect(auth.notice()).toBeNull();
    });

    it('a request that is not for the token gets none of this: a 401 from /auth/login reaches its caller', async () => {
      const done = lastValueFrom(http.post('/auth/login', {})).catch((error: unknown) => error);
      ctrl.expectOne('/auth/login').flush({ error: 'invalid_credentials' }, status(401));
      expect(((await done) as HttpErrorResponse).status).toBe(401);
      await settle();
      ctrl.expectNone('/auth/refresh');
    });
  });
});
```

  Run it and see it fail:

```bash
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -15
```

  Expected: failure to resolve `./auth.interceptor`.

- [ ] **Step 2: Write the interceptor.** `samples/notes-web/src/app/auth/auth.interceptor.ts`. The two lists are the
  place to say which of a product's own requests carry the token.

```ts
import {
  HttpErrorResponse,
  HttpEvent,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { from, lastValueFrom } from 'rxjs';
import { AuthService, errorCode } from './auth.service';

/** Paths that start like this get the access token. A product edits these two lists (guide, step 3). */
export const TOKEN_PATH_PREFIXES: readonly string[] = ['/api/', '/auth/org'];
/** Paths that are exactly this get it too. */
export const TOKEN_PATHS: readonly string[] = ['/auth/me'];

/**
 * True for a request to the app's own origin whose path is one of the lists above. Anything else (another origin, the
 * other /auth endpoints, a path that only looks like one after `..` is resolved) never gets the token.
 */
export function wantsToken(url: string, origin: string = window.location.origin): boolean {
  let parsed: URL;
  try {
    parsed = new URL(url, origin);
  } catch {
    return false;
  }
  if (parsed.origin !== origin) {
    return false;
  }
  const path = parsed.pathname;
  return TOKEN_PATHS.includes(path) || TOKEN_PATH_PREFIXES.some((prefix) => path.startsWith(prefix));
}

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!wantsToken(req.url)) {
    return next(req);
  }
  // `from(promise)` loses cancellation: a caller that unsubscribes does not stop the request in flight. Acceptable for a sample
  // whose calls are short JSON requests; a product that needs cancellation writes this with RxJS operators instead.
  return from(send(req, next, inject(AuthService), inject(Router)));
};

async function send(
  req: HttpRequest<unknown>,
  next: HttpHandlerFn,
  auth: AuthService,
  router: Router,
): Promise<HttpEvent<unknown>> {
  const sentWith = auth.token();
  try {
    return await lastValueFrom(next(withToken(req, sentWith)));
  } catch (error) {
    if (!(error instanceof HttpErrorResponse)) {
      throw error;
    }
    const permissionsChanged = error.status === 403 && errorCode(error) === 'permissions_changed';
    if (error.status !== 401 && !permissionsChanged) {
      announce(error, auth);
      throw error;
    }
    if (!(await tokenIsRenewed(auth, router, sentWith))) {
      throw error;
    }
    if (permissionsChanged && !isMe(req)) {
      // The role changed: the header and the forms follow it.
      void auth.loadMe();
    }
    return await sendAgain(req, next, auth);
  }
}

/** True when the request may be sent again with the token the service holds now. */
async function tokenIsRenewed(auth: AuthService, router: Router, sentWith: string | null): Promise<boolean> {
  const current = auth.token();
  if (current === null) {
    return false; // the session ended while this request was away
  }
  if (current !== sentWith) {
    return true; // another request renewed the token meanwhile
  }
  const result = await auth.refresh(); // one refresh at a time: the service shares a running one
  if (result === 'ok') {
    return true;
  }
  if (result === 'rejected') {
    goToLogin(router);
  } else {
    auth.showNotice('unreachable');
  }
  return false;
}

/** The second and last try: whatever it answers is the answer. */
async function sendAgain(req: HttpRequest<unknown>, next: HttpHandlerFn, auth: AuthService): Promise<HttpEvent<unknown>> {
  try {
    return await lastValueFrom(next(withToken(req, auth.token())));
  } catch (error) {
    if (error instanceof HttpErrorResponse) {
      announce(error, auth);
    }
    throw error;
  }
}

function withToken(req: HttpRequest<unknown>, token: string | null): HttpRequest<unknown> {
  return token === null ? req : req.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
}

/** The answers that have a message of their own. */
function announce(error: HttpErrorResponse, auth: AuthService): void {
  const code = errorCode(error);
  if (error.status === 403 && code === 'forbidden') {
    auth.showNotice('forbidden');
  } else if (error.status === 503 && code === 'auth_unavailable') {
    auth.showNotice('try_later');
  }
}

function isMe(req: HttpRequest<unknown>): boolean {
  return new URL(req.url, window.location.origin).pathname === '/auth/me';
}

function goToLogin(router: Router): void {
  if (router.url.startsWith('/login')) {
    return;
  }
  void router.navigate(['/login'], { queryParams: { returnUrl: router.url } });
}
```

- [ ] **Step 3: Register it.** Replace `samples/notes-web/src/app/app.config.ts`:

```ts
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { routes } from './app.routes';
import { authInterceptor } from './auth/auth.interceptor';
import { AuthService } from './auth/auth.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor])),
    // Before the first route is resolved: is there a session to resume? (a refresh with the cookie)
    provideAppInitializer(() => inject(AuthService).start()),
  ],
};
```

- [ ] **Step 4: Run everything.**

```bash
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -15
npx ng build 2>&1 | grep -v allow-scripts | tail -5
```

  Expected: every test passes, `app.config.spec.ts` included (it now runs with the interceptor: the start still asks `/auth/refresh`
  and `/auth/me` as before). If a test of the table fails by an open request at `ctrl.verify()`, read which one: that is the
  interceptor sending a request the table does not allow.

- [ ] **Step 5: Hand over** - uncommitted; suggested message `feat(web): interceptor that adds the token, refreshes once and handles the answers`.

### Task 4: The guard and the `returnUrl` sanitiser

**Files:**
- Create: `samples/notes-web/src/app/auth/auth.guard.ts`
- Test: `samples/notes-web/src/app/auth/auth.guard.spec.ts`

**Interfaces:**
- Consumes: `AuthService.token()` (Task 2), `Router`.
- Produces: `authGuard: CanActivateFn`; `safeReturnUrl(value: string | null | undefined): string`; `DEFAULT_RETURN_URL = '/notes'`.

**The rule (spec, "Session"):** the guard lets a route through only when a token is held; otherwise the person goes to
`/login?returnUrl=<path>`. `returnUrl` is followed only when it starts with a single `/` and is not `//...` or `/\...`; anything
else is replaced by `/notes`. The sanitiser also refuses what a browser would read differently from the router: a control
character (a browser strips a tab or a newline inside a URL, so `/<tab>/evil.example` becomes `//evil.example`), a backslash
anywhere, a value that starts with `/%2f` or `/%5c` in either case (a slash or a backslash in disguise), and a value longer than
2048 characters. The result is for `router.navigateByUrl`, never for `location.href` or `window.open` (the guide says so).

- [ ] **Step 1: Write the failing tests.** `samples/notes-web/src/app/auth/auth.guard.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { holdToken } from '../../testing/helpers';
import { AuthService } from './auth.service';
import { authGuard, safeReturnUrl } from './auth.guard';

describe('safeReturnUrl', () => {
  it.each([
    '//evil.example',
    '/\\evil.example',
    'https://evil.example',
    'http://evil.example/notes',
    'javascript:alert(1)',
    'evil.example',
    'notes',
    '',
    '/a\\b',
    '/%2F%2Fevil.example',
    '/%2fevil.example',
    '/%5Cevil.example',
    '/%5cevil.example',
    '/\t/evil.example',
    '/\n/evil.example',
    '/notes\r\nSet-Cookie: x=1',
    '/' + 'a'.repeat(3000),
  ])('replaces %j by /notes', (value) => {
    expect(safeReturnUrl(value)).toBe('/notes');
  });

  it('replaces a missing value by /notes', () => {
    expect(safeReturnUrl(null)).toBe('/notes');
    expect(safeReturnUrl(undefined)).toBe('/notes');
  });

  it.each(['/notes', '/notes?x=1', '/notes/5?x=1&y=2#top', '/'])('keeps %s', (value) => {
    expect(safeReturnUrl(value)).toBe(value);
  });
});

describe('authGuard', () => {
  let auth: AuthService;
  let ctrl: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    auth = TestBed.inject(AuthService);
    ctrl = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  const run = (url: string) =>
    TestBed.runInInjectionContext(() =>
      authGuard({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot),
    );

  it('sends an anonymous person to /login with the page they asked for', () => {
    const result = run('/notes?page=2');
    expect(result).toBeInstanceOf(UrlTree);
    expect(router.serializeUrl(result as UrlTree)).toBe('/login?returnUrl=%2Fnotes%3Fpage%3D2');
  });

  it('lets a person through when a token is held', async () => {
    await holdToken(auth, ctrl, 'tok');
    expect(run('/notes')).toBe(true);
  });

  it('sends a person to /login again once the token is dropped', async () => {
    await holdToken(auth, ctrl, 'tok');
    auth.dropSession();
    expect(run('/notes')).toBeInstanceOf(UrlTree);
  });
});
```

  Run it and see it fail:

```bash
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -15
```

  Expected: failure to resolve `./auth.guard`.

- [ ] **Step 2: Write the guard.** `samples/notes-web/src/app/auth/auth.guard.ts`:

```ts
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth.service';

/** Where a person lands after signing in when there is no (safe) page to go back to. */
export const DEFAULT_RETURN_URL = '/notes';

const MAX_RETURN_URL_LENGTH = 2048;
const CONTROL_OR_BACKSLASH = /[\x00-\x1f\x7f\\]/;
// A slash or a backslash written as %2f or %5c at the start: a router or a server that decodes the path reads it as `//host`.
const ENCODED_SLASH_AT_START = /^\/%(2f|5c)/i;

/**
 * The page to go to after signing in: the value of `returnUrl` if it is a path of this app, else the default.
 * A path starts with one `/`; `//host`, `/\host`, `https://host` and `javascript:` are not paths of this app.
 * The result is for `router.navigateByUrl`; never give it to `location.href` or `window.open`.
 */
export function safeReturnUrl(value: string | null | undefined): string {
  if (typeof value !== 'string' || value.length === 0 || value.length > MAX_RETURN_URL_LENGTH) {
    return DEFAULT_RETURN_URL;
  }
  if (value[0] !== '/' || value[1] === '/' || value[1] === '\\') {
    return DEFAULT_RETURN_URL;
  }
  if (CONTROL_OR_BACKSLASH.test(value) || ENCODED_SLASH_AT_START.test(value)) {
    return DEFAULT_RETURN_URL;
  }
  return value;
}

/** Lets a route through only when a token is held. Otherwise: the login screen, which comes back here after. */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.token() !== null || router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};
```

- [ ] **Step 3: Run everything and check the whole group.**

```bash
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -15
npx ng build 2>&1 | grep -v allow-scripts | tail -5
grep -rInP --exclude-dir=node_modules --exclude-dir=dist --exclude-dir=.angular --exclude=package-lock.json '\x5c[uU][0-9a-fA-F]{4}|[^\x00-\x7F]' . | head
grep -cP '\x5c\x5c' src/app/auth/auth.guard.ts src/app/auth/auth.guard.spec.ts
git status --porcelain
```

  Expected: all tests pass; the build completes; the first `grep` prints nothing (only ASCII, and no backslash-u escape written
  out); the second prints `src/app/auth/auth.guard.ts:2` and `src/app/auth/auth.guard.spec.ts:2` (two lines of each file hold a
  doubled backslash: the character class and the comparison in the guard, `'/\\evil.example'` and `'/a\\b'` in the test; a count
  of `0` means the file tool did not keep what was typed, and the tests would be testing the wrong strings: write the file
  again); `git status` shows only files under `samples/notes-web/`. If the first `grep` finds a line, rewrite it with `\x..` or
  `String.fromCharCode`.

- [ ] **Step 4: Hand over** - uncommitted; suggested message `feat(web): route guard and a returnUrl that never leaves the app`.

---

## Group B - the screens

When the group is done, every route of the spec's screen table exists, `ng test` covers each row of each table, and the app is
usable in `ng serve` against a running stack. The pages are standalone components with signals and reactive forms; all
words come from `texts.ts`; all calls to Auth-Core that are not the session's go through `AccountApi`. Every page keeps its
state in signals (the app is zoneless: a plain field changed after an `await` would not be drawn). No page needs Docker to be
tested: the tests drive the real component through the DOM, with the real router, and answer the HTTP calls themselves.

The page tests share one pattern: open the route with `RouterTestingHarness` (so the page has a real `ActivatedRoute`),
spy on `router.navigateByUrl` or `router.navigate` where the page leaves, drive the form with the helpers of
`src/testing/helpers.ts`, answer the call with `HttpTestingController`, then `await settle(fixture)` and read the text.

### Task 5: `AccountApi`, the login screen and the forgot screen

**Files:**
- Create: `samples/notes-web/src/app/account-api.ts`, `src/app/pages/login.ts`, `src/app/pages/forgot.ts`
- Modify: `samples/notes-web/src/testing/helpers.ts` (whole file given), `src/app/app.routes.ts` (whole file given)
- Test: `samples/notes-web/src/app/account-api.spec.ts`, `src/app/pages/login.spec.ts`, `src/app/pages/forgot.spec.ts`

**Interfaces:**
- Consumes: `AuthService.login`, `failureOf`, `Failure` (Task 2); `safeReturnUrl` (Task 4); `texts` (Task 1).
- Produces: `type Outcome<T = void> = { ok: true; value: T } | { ok: false; failure: Failure }`; `interface InvitePreview {
  org_name: string; email: string; role: string }`; `AccountApi` with `forgotPassword(email)`, `requestVerification(email)`,
  `resetPassword(token, newPassword)`, `verifyEmail(token)`, `previewInvite(token): Promise<Outcome<InvitePreview>>`,
  `acceptInvite(token, password)`, all returning `Promise<Outcome>` unless said otherwise (Tasks 6 uses the last four);
  `LoginPage`, `ForgotPage`; routes `/login` and `/forgot`. In `helpers.ts`: `pageText(fixture)`, `typeInto(fixture,
  selector, value)`, `submitForm(fixture, selector?)`, `clickOn(fixture, selector)`, `has(fixture, selector)`,
  `valueOf(fixture, selector)`, `typeRaw(fixture, selector, value)` (like `typeInto`, but the field is made a plain text field
  first: jsdom and browsers strip the spaces around the value of an `email` input, so only `typeRaw` can test trimming).

**The screens (spec):** `/login`: email and password; `401 invalid_credentials` "Wrong email or password."; `403
email_not_verified` a message and a button "Send the verification link again" (`POST /auth/email/verify/request`); `403
no_membership` "Your account does not belong to a company."; `429 too_many_attempts` "Too many attempts. Try again in N
minutes." (N from `retry_after_seconds`, rounded up); success goes to the page named by `returnUrl` (after `safeReturnUrl`),
else `/notes`. `/forgot`: email; on `202` always "If an account exists for this address, we sent a link."; `429` "Wait N
seconds before asking again."; anything else "Something went wrong. Try again." The email an invitation passes to `/login` arrives
in the router's navigation state. Beyond the spec: the email is trimmed before it is sent; the password is sent exactly as
typed, spaces included; a form that is being sent cannot be sent again (one request for a double click or a double Enter).

- [ ] **Step 1: The test helpers.** Replace `samples/notes-web/src/testing/helpers.ts` with this (Task 2's content plus the
  DOM helpers):

```ts
import { ComponentFixture } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { AuthService, Me } from '../app/auth/auth.service';

/** Lets pending promises and timers run, then draws the component again. */
export async function settle(fixture?: ComponentFixture<unknown>): Promise<void> {
  await new Promise<void>((resolve) => setTimeout(resolve));
  fixture?.detectChanges();
}

export const adminMe: Me = {
  sub: '11111111-1111-1111-1111-111111111111',
  email: 'admin@example.test',
  org_id: '22222222-2222-2222-2222-222222222222',
  org_name: 'Acme',
  roles: ['admin'],
  permissions: ['notes:read', 'notes:write'],
};

export const viewerMe: Me = {
  ...adminMe,
  email: 'viewer@example.test',
  roles: ['viewer'],
  permissions: ['notes:read'],
};

/** Gives the service a token the way the app does: by a refresh that the test answers. */
export async function holdToken(auth: AuthService, ctrl: HttpTestingController, token: string): Promise<void> {
  const done = auth.refresh();
  ctrl.expectOne('/auth/refresh').flush({ access_token: token });
  await done;
}

/** A signed-in person: a token and the answer of /auth/me. */
export async function signedInAs(
  auth: AuthService,
  ctrl: HttpTestingController,
  me: Me = adminMe,
  token = 'tok',
): Promise<void> {
  await holdToken(auth, ctrl, token);
  const loaded = auth.loadMe();
  ctrl.expectOne('/auth/me').flush(me);
  await loaded;
}

function root(fixture: ComponentFixture<unknown>): HTMLElement {
  return fixture.nativeElement as HTMLElement;
}

function find<T extends Element>(fixture: ComponentFixture<unknown>, selector: string): T {
  const element = root(fixture).querySelector<T>(selector);
  if (element === null) {
    throw new Error(`nothing matches ${selector} in: ${pageText(fixture)}`);
  }
  return element;
}

/** All the text on the page, white space collapsed. */
export function pageText(fixture: ComponentFixture<unknown>): string {
  return (root(fixture).textContent ?? '').replace(/\s+/g, ' ').trim();
}

/** Types into an input or a text area the way a person does: the value, then the event the form listens for. */
export function typeInto(fixture: ComponentFixture<unknown>, selector: string, value: string): void {
  const field = find<HTMLInputElement | HTMLTextAreaElement>(fixture, selector);
  field.value = value;
  field.dispatchEvent(new Event('input'));
}

/**
 * Like typeInto, but the value arrives exactly as given. A browser (and jsdom) strips the white space around the value of an
 * input of type email, so a test of what the page does with spaces around an address must make the field a plain text field
 * first.
 */
export function typeRaw(fixture: ComponentFixture<unknown>, selector: string, value: string): void {
  const field = find<HTMLInputElement | HTMLTextAreaElement>(fixture, selector);
  if (field instanceof HTMLInputElement) {
    field.type = 'text';
  }
  field.value = value;
  field.dispatchEvent(new Event('input'));
}

export function submitForm(fixture: ComponentFixture<unknown>, selector = 'form'): void {
  find<HTMLFormElement>(fixture, selector).dispatchEvent(new Event('submit'));
}

export function clickOn(fixture: ComponentFixture<unknown>, selector: string): void {
  find<HTMLElement>(fixture, selector).click();
}

export function has(fixture: ComponentFixture<unknown>, selector: string): boolean {
  return root(fixture).querySelector(selector) !== null;
}

export function valueOf(fixture: ComponentFixture<unknown>, selector: string): string {
  return find<HTMLInputElement>(fixture, selector).value;
}
```

- [ ] **Step 2: Write the tests of `AccountApi`.** `samples/notes-web/src/app/account-api.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AccountApi } from './account-api';

const status = (code: number) => ({ status: code, statusText: String(code) });

describe('AccountApi', () => {
  let api: AccountApi;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(AccountApi);
    ctrl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => ctrl.verify());

  it.each([
    ['forgotPassword', () => api.forgotPassword('a@b.example'), '/auth/password/forgot', { email: 'a@b.example' }, 202],
    ['requestVerification', () => api.requestVerification('a@b.example'), '/auth/email/verify/request', { email: 'a@b.example' }, 202],
    ['resetPassword', () => api.resetPassword('tok', 'Pw-1 x'), '/auth/password/reset', { token: 'tok', new_password: 'Pw-1 x' }, 204],
    ['verifyEmail', () => api.verifyEmail('tok'), '/auth/email/verify', { token: 'tok' }, 204],
    ['acceptInvite', () => api.acceptInvite('tok', 'Pw-1 x'), '/auth/invites/accept', { token: 'tok', password: 'Pw-1 x' }, 204],
  ] as const)('%s posts to its endpoint with the body of the contract and is ok on the empty answer', async (_name, call, url, body, code) => {
    const done = call();
    const request = ctrl.expectOne(url);
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(body);
    request.flush(null, status(code));
    expect((await done).ok).toBe(true);
  });

  it('reads the answer to a preview: the company, the email and the role', async () => {
    const done = api.previewInvite('tok');
    const request = ctrl.expectOne('/auth/invites/preview');
    expect(request.request.body).toEqual({ token: 'tok' });
    request.flush({ org_name: 'Acme', email: 'a@b.example', role: 'viewer' });
    expect(await done).toEqual({ ok: true, value: { org_name: 'Acme', email: 'a@b.example', role: 'viewer' } });
  });

  it('a preview that is not what the contract says is a failure, not a half-filled screen', async () => {
    const done = api.previewInvite('tok');
    ctrl.expectOne('/auth/invites/preview').flush({ org_name: 'Acme' });
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
  });

  it.each([
    [400, { error: 'invalid_token' }, { kind: 'invalid_token' }],
    [400, { error: 'weak_password', rules: ['too_short'] }, { kind: 'weak_password', rules: ['too_short'] }],
    [409, { error: 'already_member' }, { kind: 'already_member' }],
    [429, { error: 'too_many_attempts', retry_after_seconds: 42 }, { kind: 'too_many_attempts', retryAfterSeconds: 42 }],
    [400, { error: 'invalid_request' }, { kind: 'other' }],
    [500, '<html>oops</html>', { kind: 'other' }],
  ])('answer %s %j is the failure %j', async (code, body, failure) => {
    const done = api.acceptInvite('tok', 'pw');
    ctrl.expectOne('/auth/invites/accept').flush(body, status(code));
    expect(await done).toEqual({ ok: false, failure });
  });

  it('no network is a failure', async () => {
    const done = api.verifyEmail('tok');
    ctrl.expectOne('/auth/email/verify').error(new ProgressEvent('error'));
    expect(await done).toEqual({ ok: false, failure: { kind: 'other' } });
  });
});
```

  Run it and see it fail (`Cannot find module './account-api'`):

```bash
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -15
```

- [ ] **Step 3: Write `AccountApi`.** `samples/notes-web/src/app/account-api.ts`. Each method answers with a result, never
  throws, so a page has one `if` for the answer it shows and one for everything else.

```ts
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Failure, failureOf, isRecord } from './auth/auth.service';

export type Outcome<T = void> = { ok: true; value: T } | { ok: false; failure: Failure };

/** What POST /auth/invites/preview answers: who invites, for whom, as what. */
export interface InvitePreview {
  org_name: string;
  email: string;
  role: string;
}

/** Auth-Core's endpoints for the mail links and the account, which need no access token. */
@Injectable({ providedIn: 'root' })
export class AccountApi {
  private readonly http = inject(HttpClient);

  forgotPassword(email: string): Promise<Outcome> {
    return this.post('/auth/password/forgot', { email });
  }

  requestVerification(email: string): Promise<Outcome> {
    return this.post('/auth/email/verify/request', { email });
  }

  resetPassword(token: string, newPassword: string): Promise<Outcome> {
    return this.post('/auth/password/reset', { token, new_password: newPassword });
  }

  verifyEmail(token: string): Promise<Outcome> {
    return this.post('/auth/email/verify', { token });
  }

  async previewInvite(token: string): Promise<Outcome<InvitePreview>> {
    const outcome = await this.post<unknown>('/auth/invites/preview', { token });
    if (!outcome.ok) {
      return outcome;
    }
    const body = outcome.value;
    if (
      isRecord(body) &&
      typeof body['org_name'] === 'string' &&
      typeof body['email'] === 'string' &&
      typeof body['role'] === 'string'
    ) {
      return { ok: true, value: { org_name: body['org_name'], email: body['email'], role: body['role'] } };
    }
    return { ok: false, failure: { kind: 'other' } };
  }

  acceptInvite(token: string, password: string): Promise<Outcome> {
    return this.post('/auth/invites/accept', { token, password });
  }

  private async post<T = void>(url: string, body: unknown): Promise<Outcome<T>> {
    try {
      return { ok: true, value: await firstValueFrom(this.http.post<T>(url, body)) };
    } catch (error) {
      return { ok: false, failure: failureOf(error) };
    }
  }
}
```

  Run the tests: `account-api.spec.ts` passes.

- [ ] **Step 4: Write the tests of the login screen.** `samples/notes-web/src/app/pages/login.spec.ts`. Every row of the
  screen table, and the checks beyond it:

```ts
import { Component } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { adminMe, clickOn, has, pageText, settle, submitForm, typeInto, typeRaw, valueOf } from '../../testing/helpers';
import { LoginPage } from './login';

@Component({ template: '' })
class Blank {}

const status = (code: number) => ({ status: code, statusText: String(code) });

async function open(url = '/login') {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'login', component: LoginPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url, LoginPage);
  const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  return { fixture: harness.fixture, navigateByUrl, ctrl: TestBed.inject(HttpTestingController) };
}

type Opened = Awaited<ReturnType<typeof open>>;

function signIn({ fixture }: Opened, email: string, password: string): void {
  typeInto(fixture, '#email', email);
  typeInto(fixture, '#password', password);
  submitForm(fixture);
}

describe('LoginPage', () => {
  it('shows the form and a link to the forgot screen', async () => {
    const { fixture } = await open();
    expect(pageText(fixture)).toContain('Sign in');
    expect(has(fixture, '#email')).toBe(true);
    expect(has(fixture, '#password')).toBe(true);
    expect(has(fixture, 'a[href="/forgot"]')).toBe(true);
  });

  it('fills in the email that an invitation passed in the navigation state', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'start', component: Blank },
          { path: 'login', component: LoginPage },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    const harness = await RouterTestingHarness.create('/start');
    await TestBed.inject(Router).navigateByUrl('/login', { state: { email: 'new@acme.example' } });
    harness.detectChanges();
    expect(valueOf(harness.fixture, '#email')).toBe('new@acme.example');
    expect(TestBed.inject(Router).url).toBe('/login');
  });

  describe('a successful sign-in goes to the page named by returnUrl, else /notes', () => {
    it.each([
      ['/login', '/notes'],
      [`/login?returnUrl=${encodeURIComponent('/notes?x=1')}`, '/notes?x=1'],
      [`/login?returnUrl=${encodeURIComponent('//evil.example')}`, '/notes'],
      [`/login?returnUrl=${encodeURIComponent('/\\evil.example')}`, '/notes'],
      [`/login?returnUrl=${encodeURIComponent('/%2F%2Fevil.example')}`, '/notes'],
      [`/login?returnUrl=${encodeURIComponent('https://evil.example')}`, '/notes'],
      [`/login?returnUrl=${encodeURIComponent('javascript:alert(1)')}`, '/notes'],
    ])('%s goes to %s', async (url, target) => {
      const opened = await open(url);
      signIn(opened, 'admin@example.test', 'pw');
      opened.ctrl.expectOne('/auth/login').flush({ status: 'authenticated', access_token: 'tok' });
      await settle();
      opened.ctrl.expectOne('/auth/me').flush(adminMe);
      await settle(opened.fixture);
      expect(opened.navigateByUrl).toHaveBeenCalledTimes(1);
      expect(opened.navigateByUrl).toHaveBeenCalledWith(target);
    });
  });

  it('401 invalid_credentials: "Wrong email or password."', async () => {
    const opened = await open();
    signIn(opened, 'admin@example.test', 'bad');
    opened.ctrl.expectOne('/auth/login').flush({ error: 'invalid_credentials' }, status(401));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Wrong email or password.');
    expect(has(opened.fixture, '[data-testid="resend"]')).toBe(false);
    expect(opened.navigateByUrl).not.toHaveBeenCalled();
  });

  it('403 no_membership: "Your account does not belong to a company."', async () => {
    const opened = await open();
    signIn(opened, 'admin@example.test', 'pw');
    opened.ctrl.expectOne('/auth/login').flush({ error: 'no_membership' }, status(403));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Your account does not belong to a company.');
  });

  it.each([
    [1, 'Try again in 1 minute.'],
    [60, 'Try again in 1 minute.'],
    [61, 'Try again in 2 minutes.'],
    [600, 'Try again in 10 minutes.'],
  ])('429 with retry_after_seconds %s: "Too many attempts. %s"', async (seconds, sentence) => {
    const opened = await open();
    signIn(opened, 'admin@example.test', 'pw');
    opened.ctrl.expectOne('/auth/login').flush({ error: 'too_many_attempts', retry_after_seconds: seconds }, status(429));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(`Too many attempts. ${sentence}`);
  });

  it.each([
    ['400 invalid_request', { error: 'invalid_request' }, 400],
    ['500 with a page of HTML', '<html>oops</html>', 500],
    ['502 from a proxy', '', 502],
  ])('%s: "Something went wrong. Try again."', async (_name, body, code) => {
    const opened = await open();
    signIn(opened, 'admin@example.test', 'pw');
    opened.ctrl.expectOne('/auth/login').flush(body, status(code));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
  });

  it('no network: "Something went wrong. Try again."', async () => {
    const opened = await open();
    signIn(opened, 'admin@example.test', 'pw');
    opened.ctrl.expectOne('/auth/login').error(new ProgressEvent('error'));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
  });

  describe('403 email_not_verified', () => {
    async function unverified() {
      const opened = await open();
      signIn(opened, 'new@example.test', 'pw');
      opened.ctrl.expectOne('/auth/login').flush({ error: 'email_not_verified' }, status(403));
      await settle(opened.fixture);
      return opened;
    }

    it('shows a message and the button "Send the verification link again"', async () => {
      const { fixture } = await unverified();
      expect(pageText(fixture)).toContain('Your email address is not confirmed yet.');
      expect(pageText(fixture)).toContain('Send the verification link again');
      expect(has(fixture, '[data-testid="resend"]')).toBe(true);
    });

    it('the button asks for a new link for the address in the form, and says so', async () => {
      const { fixture, ctrl } = await unverified();
      clickOn(fixture, '[data-testid="resend"]');
      const request = ctrl.expectOne('/auth/email/verify/request');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ email: 'new@example.test' });
      request.flush(null, status(202));
      await settle(fixture);
      expect(pageText(fixture)).toContain('If this address needs confirming, we sent a new link.');
    });

    it('a double click on the button sends one request', async () => {
      const { fixture, ctrl } = await unverified();
      clickOn(fixture, '[data-testid="resend"]');
      clickOn(fixture, '[data-testid="resend"]');
      ctrl.expectOne('/auth/email/verify/request').flush(null, status(202));
      await settle(fixture);
      ctrl.expectNone('/auth/email/verify/request');
    });

    it('429 on the button: "Wait N seconds before asking again."', async () => {
      const { fixture, ctrl } = await unverified();
      clickOn(fixture, '[data-testid="resend"]');
      ctrl.expectOne('/auth/email/verify/request').flush({ error: 'too_many_attempts', retry_after_seconds: 42 }, status(429));
      await settle(fixture);
      expect(pageText(fixture)).toContain('Wait 42 seconds before asking again.');
    });

    it('any other answer on the button: "Something went wrong. Try again."', async () => {
      const { fixture, ctrl } = await unverified();
      clickOn(fixture, '[data-testid="resend"]');
      ctrl.expectOne('/auth/email/verify/request').flush('oops', status(500));
      await settle(fixture);
      expect(pageText(fixture)).toContain('Something went wrong. Try again.');
    });
  });

  describe('what is sent', () => {
    it('trims the email and sends the password exactly as typed', async () => {
      const opened = await open();
      typeRaw(opened.fixture, '#email', '  Admin@Example.test ');
      typeInto(opened.fixture, '#password', '  pass word 1  ');
      submitForm(opened.fixture);
      const request = opened.ctrl.expectOne('/auth/login');
      expect(request.request.body).toEqual({ email: 'Admin@Example.test', password: '  pass word 1  ' });
      request.flush({ error: 'invalid_credentials' }, status(401));
    });

    it('sends nothing while a field is empty', async () => {
      const opened = await open();
      signIn(opened, '', 'pw');
      signIn(opened, 'admin@example.test', '');
      opened.ctrl.expectNone('/auth/login');
    });

    it('sends one request for a double click or a double Enter', async () => {
      const opened = await open();
      signIn(opened, 'admin@example.test', 'pw');
      submitForm(opened.fixture);
      submitForm(opened.fixture);
      opened.ctrl.expectOne('/auth/login').flush({ error: 'invalid_credentials' }, status(401));
      await settle(opened.fixture);
      opened.ctrl.expectNone('/auth/login');
    });

    it('allows another try after a failure', async () => {
      const opened = await open();
      signIn(opened, 'admin@example.test', 'bad');
      opened.ctrl.expectOne('/auth/login').flush({ error: 'invalid_credentials' }, status(401));
      await settle(opened.fixture);
      signIn(opened, 'admin@example.test', 'good');
      opened.ctrl.expectOne('/auth/login').flush({ error: 'invalid_credentials' }, status(401));
    });
  });
});
```

  Run it and see it fail (`Cannot find module './login'`).

- [ ] **Step 5: Write the login screen.** `samples/notes-web/src/app/pages/login.ts`:

```ts
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AccountApi } from '../account-api';
import { safeReturnUrl } from '../auth/auth.guard';
import { AuthService, Failure } from '../auth/auth.service';
import { texts } from '../texts';

/** The email an invitation screen passed on in the navigation state (it is never in the URL). */
function startEmail(router: Router): string {
  const email: unknown = router.currentNavigation()?.extras.state?.['email'];
  return typeof email === 'string' ? email : '';
}

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
        <label for="email">{{ common.email }}</label>
        <input id="email" type="email" formControlName="email" autocomplete="username" autocapitalize="none" />
        <label for="password">{{ common.password }}</label>
        <input id="password" type="password" formControlName="password" autocomplete="current-password" />
        <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
      </form>
      @if (message(); as text) {
        <p class="error" role="alert" data-testid="login-message">{{ text }}</p>
      }
      @if (canResend()) {
        <button type="button" class="link" data-testid="resend" (click)="resend()">{{ common.sendVerificationAgain }}</button>
      }
      @if (info(); as text) {
        <p class="status" role="status">{{ text }}</p>
      }
      <p><a routerLink="/forgot">{{ t.forgot }}</a></p>
    </main>
  `,
})
export class LoginPage {
  protected readonly t = texts.login;
  protected readonly common = texts.common;
  private readonly auth = inject(AuthService);
  private readonly account = inject(AccountApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly form = inject(NonNullableFormBuilder).group({
    email: [startEmail(this.router), Validators.required],
    password: ['', Validators.required],
  });
  protected readonly busy = signal(false);
  protected readonly message = signal<string | null>(null);
  protected readonly info = signal<string | null>(null);
  protected readonly canResend = signal(false);
  protected readonly resending = signal(false);

  protected async submit(): Promise<void> {
    if (this.busy() || this.form.invalid) {
      return;
    }
    this.busy.set(true);
    this.message.set(null);
    this.info.set(null);
    this.canResend.set(false);
    const { email, password } = this.form.getRawValue();
    const result = await this.auth.login(email.trim(), password);
    this.busy.set(false);
    if (result.ok) {
      await this.router.navigateByUrl(safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')));
    } else {
      this.show(result.failure);
    }
  }

  protected async resend(): Promise<void> {
    const email = this.form.controls.email.value.trim();
    if (email === '' || this.resending()) {
      return;
    }
    this.resending.set(true);
    this.message.set(null);
    this.info.set(null);
    const result = await this.account.requestVerification(email);
    this.resending.set(false);
    if (result.ok) {
      this.info.set(this.common.verificationSent);
    } else if (result.failure.kind === 'too_many_attempts') {
      this.info.set(this.common.waitSeconds(result.failure.retryAfterSeconds));
    } else {
      this.message.set(this.common.somethingWrong);
    }
  }

  private show(failure: Failure): void {
    switch (failure.kind) {
      case 'invalid_credentials':
        this.message.set(this.t.wrongCredentials);
        break;
      case 'email_not_verified':
        this.message.set(this.t.notVerified);
        this.canResend.set(true);
        break;
      case 'no_membership':
        this.message.set(this.t.noMembership);
        break;
      case 'too_many_attempts':
        this.message.set(this.t.tooManyAttempts(failure.retryAfterSeconds));
        break;
      default:
        this.message.set(this.common.somethingWrong);
    }
  }
}
```

  Run the tests: `login.spec.ts` passes. If the test of the navigation state fails with an empty email, the router has no
  current navigation at the moment the page is built; read `router.currentNavigation()` in `startEmail` before blaming the test.

- [ ] **Step 6: Write the tests of the forgot screen.** `samples/notes-web/src/app/pages/forgot.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, settle, submitForm, typeInto, typeRaw } from '../../testing/helpers';
import { ForgotPage } from './forgot';

const status = (code: number) => ({ status: code, statusText: String(code) });

async function open() {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'forgot', component: ForgotPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl('/forgot', ForgotPage);
  return { fixture: harness.fixture, ctrl: TestBed.inject(HttpTestingController) };
}

describe('ForgotPage', () => {
  const sent = 'If an account exists for this address, we sent a link.';

  it('asks for an email and links back to the sign-in', async () => {
    const { fixture } = await open();
    expect(has(fixture, '#email')).toBe(true);
    expect(has(fixture, 'a[href="/login"]')).toBe(true);
  });

  it.each(['user@example.test', 'nobody@nowhere.example'])('202 for %s: always the same sentence', async (email) => {
    const { fixture, ctrl } = await open();
    typeRaw(fixture, '#email', ` ${email} `);
    submitForm(fixture);
    const request = ctrl.expectOne('/auth/password/forgot');
    expect(request.request.body).toEqual({ email });
    request.flush(null, status(202));
    await settle(fixture);
    expect(pageText(fixture)).toContain(sent);
  });

  it('429: "Wait N seconds before asking again."', async () => {
    const { fixture, ctrl } = await open();
    typeInto(fixture, '#email', 'user@example.test');
    submitForm(fixture);
    ctrl.expectOne('/auth/password/forgot').flush({ error: 'too_many_attempts', retry_after_seconds: 42 }, status(429));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Wait 42 seconds before asking again.');
    expect(pageText(fixture)).not.toContain(sent);
  });

  it.each([
    [400, { error: 'invalid_request' }],
    [500, '<html>oops</html>'],
  ])('%s: "Something went wrong. Try again."', async (code, body) => {
    const { fixture, ctrl } = await open();
    typeInto(fixture, '#email', 'user@example.test');
    submitForm(fixture);
    ctrl.expectOne('/auth/password/forgot').flush(body, status(code));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Something went wrong. Try again.');
    expect(pageText(fixture)).not.toContain(sent);
  });

  it('sends nothing for an empty email, and one request for a double submit', async () => {
    const { fixture, ctrl } = await open();
    submitForm(fixture);
    ctrl.expectNone('/auth/password/forgot');
    typeInto(fixture, '#email', 'user@example.test');
    submitForm(fixture);
    submitForm(fixture);
    ctrl.expectOne('/auth/password/forgot').flush(null, status(202));
  });
});
```

- [ ] **Step 7: Write the forgot screen.** `samples/notes-web/src/app/pages/forgot.ts`:

```ts
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AccountApi } from '../account-api';
import { texts } from '../texts';

@Component({
  selector: 'app-forgot',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      <p>{{ t.intro }}</p>
      <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
        <label for="email">{{ common.email }}</label>
        <input id="email" type="email" formControlName="email" autocomplete="username" autocapitalize="none" />
        <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
      </form>
      @if (sent()) {
        <p class="status" role="status" data-testid="forgot-sent">{{ t.sent }}</p>
      }
      @if (error(); as text) {
        <p class="error" role="alert">{{ text }}</p>
      }
      <p><a routerLink="/login">{{ common.backToSignIn }}</a></p>
    </main>
  `,
})
export class ForgotPage {
  protected readonly t = texts.forgot;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);

  protected readonly form = inject(NonNullableFormBuilder).group({ email: ['', Validators.required] });
  protected readonly busy = signal(false);
  protected readonly sent = signal(false);
  protected readonly error = signal<string | null>(null);

  protected async submit(): Promise<void> {
    if (this.busy() || this.form.invalid) {
      return;
    }
    this.busy.set(true);
    this.sent.set(false);
    this.error.set(null);
    const result = await this.account.forgotPassword(this.form.getRawValue().email.trim());
    this.busy.set(false);
    if (result.ok) {
      this.sent.set(true);
    } else if (result.failure.kind === 'too_many_attempts') {
      this.error.set(this.common.waitSeconds(result.failure.retryAfterSeconds));
    } else {
      this.error.set(this.common.somethingWrong);
    }
  }
}
```

- [ ] **Step 8: The routes.** Replace `samples/notes-web/src/app/app.routes.ts` (the page titles are in `texts.ts` too):

```ts
import { Routes } from '@angular/router';
import { ForgotPage } from './pages/forgot';
import { LoginPage } from './pages/login';
import { texts } from './texts';

export const routes: Routes = [
  { path: 'login', component: LoginPage, title: texts.login.title },
  { path: 'forgot', component: ForgotPage, title: texts.forgot.title },
];
```

- [ ] **Step 9: Run everything.**

```bash
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -15
npx ng build 2>&1 | grep -v allow-scripts | tail -5
grep -cP '\x5c\x5c' src/app/pages/login.spec.ts
```

  Expected: all tests pass; the build completes; the `grep` prints `1` (the line `'/\\evil.example'` of the login test holds a
  doubled backslash, as typed). The `ng build` may warn that `ForgotPage` or `LoginPage` is unused if the routes file was not
  replaced: it is used by the routes above.

- [ ] **Step 10: Hand over** - uncommitted; suggested message `feat(web): login and forgot screens over an account API service`.

### Task 6: The reset, verify and invite screens

**Files:**
- Create: `samples/notes-web/src/app/pages/token-from-url.ts`, `src/app/pages/reset.ts`, `src/app/pages/verify.ts`, `src/app/pages/invite.ts`
- Modify: `samples/notes-web/src/app/app.routes.ts` (whole file given)
- Test: `samples/notes-web/src/app/pages/token-from-url.spec.ts`, `reset.spec.ts`, `verify.spec.ts`, `invite.spec.ts` (all in `src/app/pages/`)

**Interfaces:**
- Consumes: `AccountApi` (`resetPassword`, `verifyEmail`, `requestVerification`, `previewInvite`, `acceptInvite`), `Outcome`,
  `InvitePreview` (Task 5); the test helpers of Task 5; `texts` (`reset`, `verify`, `invite` with its own `invalid` sentence, `common`).
- Produces: `takeTokenFromUrl(route): string | null`; `ResetPage`, `VerifyPage`, `InvitePage`; routes `/reset`, `/verify`,
  `/invite`. `InvitePage` leaves with `router.navigate(['/login'], { state: { email } })`.

**The screens (spec):** `/reset?token=`: new password twice; `weak_password`: the rules not met, one line each;
`invalid_token`: "This link has expired or was already used." with a link to `/forgot`; `204`: "Password changed. Sign in." with a
link to `/login` (no sign-in follows: Auth-Core has ended every session). `/verify?token=`: calls `POST /auth/email/verify`
on opening; `204`: "Email confirmed." with a link to `/login`; `invalid_token`: the same sentence as above and a form to
send a new link. `/invite?token=`: first `POST /auth/invites/preview`: "Join **{org_name}** as **{role}**" and the email, not
editable; then the password twice and `POST /auth/invites/accept`; the screen always says "This sets the password for
{email}."; `409 already_member`: "This account already belongs to a company."; `invalid_token`: "This invitation has expired or was
already used. Ask for a new one." (no link: the person asks their company, not this app); `204`: `/login`
with the email filled in, passed in the navigation state. The three screens read `token` from the URL once and remove it from
the address bar with `history.replaceState`, keeping `history.state` (the router's own bookkeeping) as it is. A missing or empty
`token` is an invalid link (on `/invite`, an invalid invitation), with no request. Any other answer: "Something went wrong. Try again." Passwords are sent exactly as
typed.

- [ ] **Step 1: Write the test of the token reader.** `samples/notes-web/src/app/pages/token-from-url.spec.ts`:

```ts
import { convertToParamMap } from '@angular/router';
import { takeTokenFromUrl } from './token-from-url';

const route = (params: Record<string, string | string[]>) => ({ snapshot: { queryParamMap: convertToParamMap(params) } });

describe('takeTokenFromUrl', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('returns the token and takes it out of the address bar', () => {
    history.replaceState(null, '', '/reset?token=abc123');
    expect(takeTokenFromUrl(route({ token: 'abc123' }))).toBe('abc123');
    expect(window.location.search).toBe('');
    expect(window.location.pathname).toBe('/reset');
  });

  it('keeps the history state the router put there', () => {
    history.replaceState({ navigationId: 7 }, '', '/reset?token=abc123');
    takeTokenFromUrl(route({ token: 'abc123' }));
    expect(history.state).toEqual({ navigationId: 7 });
  });

  it('takes out a fragment too, and everything else in the query', () => {
    history.replaceState(null, '', '/verify?token=abc123&x=1#frag');
    takeTokenFromUrl(route({ token: 'abc123', x: '1' }));
    expect(window.location.href.endsWith('/verify')).toBe(true);
  });

  it.each([[{}], [{ token: '' }]])('is null for %j, and still cleans the address bar', (params) => {
    history.replaceState(null, '', '/reset?token=');
    expect(takeTokenFromUrl(route(params))).toBeNull();
    expect(window.location.search).toBe('');
  });

  it('uses the first of two token parameters', () => {
    expect(takeTokenFromUrl(route({ token: ['first', 'second'] }))).toBe('first');
  });
});
```

  Run it and see it fail (`Cannot find module './token-from-url'`).

- [ ] **Step 2: Write the token reader.** `samples/notes-web/src/app/pages/token-from-url.ts`:

```ts
import { ParamMap } from '@angular/router';

/**
 * The `token` of a mail link, read once. It is taken out of the address bar at once (`history.replaceState`), together with
 * everything else after the path, so it stays out of the history and of what a person copies. `history.state` is passed on
 * unchanged: the router keeps its own bookkeeping there. A missing or empty token is `null`.
 */
export function takeTokenFromUrl(route: { snapshot: { queryParamMap: ParamMap } }): string | null {
  const token = route.snapshot.queryParamMap.get('token');
  history.replaceState(history.state, '', window.location.pathname);
  return token === null || token === '' ? null : token;
}
```

  Run the tests: `token-from-url.spec.ts` passes.

- [ ] **Step 3: Write the tests of the reset screen.** `samples/notes-web/src/app/pages/reset.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, settle, submitForm, typeInto } from '../../testing/helpers';
import { ResetPage } from './reset';

const status = (code: number) => ({ status: code, statusText: String(code) });
const invalid = 'This link has expired or was already used.';

async function open(query = '?token=tok-123') {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'reset', component: ResetPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  history.replaceState(null, '', `/reset${query}`);
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/reset${query}`, ResetPage);
  return { fixture: harness.fixture, ctrl: TestBed.inject(HttpTestingController) };
}

type Opened = Awaited<ReturnType<typeof open>>;

function choose({ fixture }: Opened, password: string, repeat = password): void {
  typeInto(fixture, '#password', password);
  typeInto(fixture, '#repeat', repeat);
  submitForm(fixture);
}

describe('ResetPage', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('takes the token out of the address bar when it opens', async () => {
    await open();
    expect(window.location.search).toBe('');
    expect(window.location.href).not.toContain('tok-123');
  });

  it('sends the token it read and the new password exactly as typed', async () => {
    const opened = await open();
    choose(opened, '  New Passw0rd  ');
    const request = opened.ctrl.expectOne('/auth/password/reset');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-123', new_password: '  New Passw0rd  ' });
    request.flush(null, status(204));
  });

  it('204: "Password changed. Sign in." with a link to /login, and nobody is signed in', async () => {
    const opened = await open();
    choose(opened, 'New-Passw0rd');
    opened.ctrl.expectOne('/auth/password/reset').flush(null, status(204));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Password changed. Sign in.');
    expect(has(opened.fixture, 'a[href="/login"]')).toBe(true);
    expect(has(opened.fixture, 'form')).toBe(false);
    opened.ctrl.expectNone('/auth/login');
  });

  it('weak_password: the rules not met, one line each', async () => {
    const opened = await open();
    choose(opened, 'short');
    opened.ctrl
      .expectOne('/auth/password/reset')
      .flush({ error: 'weak_password', rules: ['too_short', 'requires_upper', 'requires_digit'] }, status(400));
    await settle(opened.fixture);
    const lines = Array.from(
      (opened.fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="rules"] li'),
      (item) => item.textContent?.trim(),
    );
    expect(lines).toEqual([
      'The password is too short.',
      'The password needs an uppercase letter.',
      'The password needs a digit.',
    ]);
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it('weak_password: a rule it has no text for is still a line, and the next try replaces the lines', async () => {
    const opened = await open();
    choose(opened, 'short');
    opened.ctrl.expectOne('/auth/password/reset').flush({ error: 'weak_password', rules: ['requires_symbol'] }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('The password does not meet a rule.');
    choose(opened, 'Another-Passw0rd');
    opened.ctrl.expectOne('/auth/password/reset').flush({ error: 'weak_password', rules: ['too_short'] }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).not.toContain('The password does not meet a rule.');
    expect(pageText(opened.fixture)).toContain('The password is too short.');
  });

  it('a rule named like a property of every object is a line with the fallback text, never a function', async () => {
    const opened = await open();
    choose(opened, 'short');
    opened.ctrl
      .expectOne('/auth/password/reset')
      .flush({ error: 'weak_password', rules: ['constructor', 'toString', '__proto__'] }, status(400));
    await settle(opened.fixture);
    const lines = Array.from(
      (opened.fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="rules"] li'),
      (item) => item.textContent?.trim(),
    );
    expect(lines).toEqual([
      'The password does not meet a rule.',
      'The password does not meet a rule.',
      'The password does not meet a rule.',
    ]);
  });

  it('invalid_token: "This link has expired or was already used." with a link to /forgot', async () => {
    const opened = await open();
    choose(opened, 'New-Passw0rd');
    opened.ctrl.expectOne('/auth/password/reset').flush({ error: 'invalid_token' }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(invalid);
    expect(has(opened.fixture, 'a[href="/forgot"]')).toBe(true);
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it.each([
    [400, { error: 'invalid_request' }],
    [500, '<html>oops</html>'],
  ])('%s: "Something went wrong. Try again."', async (code, body) => {
    const opened = await open();
    choose(opened, 'New-Passw0rd');
    opened.ctrl.expectOne('/auth/password/reset').flush(body, status(code));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it('two different passwords: a message, and no request', async () => {
    const opened = await open();
    choose(opened, 'New-Passw0rd', 'New-Passw0rd2');
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('The two passwords are not the same.');
    opened.ctrl.expectNone('/auth/password/reset');
  });

  it.each([['', 'a link without a token'], ['?token=', 'a link with an empty token'], ['?other=1', 'a link with another parameter']])(
    '%s (%s) is an invalid link, with no request',
    async (query) => {
      const opened = await open(query);
      expect(pageText(opened.fixture)).toContain(invalid);
      expect(has(opened.fixture, 'a[href="/forgot"]')).toBe(true);
      expect(has(opened.fixture, 'form')).toBe(false);
      opened.ctrl.expectNone('/auth/password/reset');
    },
  );

  it('sends one request for a double submit', async () => {
    const opened = await open();
    choose(opened, 'New-Passw0rd');
    submitForm(opened.fixture);
    opened.ctrl.expectOne('/auth/password/reset').flush(null, status(204));
  });
});
```

- [ ] **Step 4: Write the reset screen.** `samples/notes-web/src/app/pages/reset.ts`:

```ts
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AccountApi } from '../account-api';
import { texts } from '../texts';
import { takeTokenFromUrl } from './token-from-url';

@Component({
  selector: 'app-reset',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @switch (phase()) {
        @case ('form') {
          <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
            <label for="password">{{ common.newPassword }}</label>
            <input id="password" type="password" formControlName="password" autocomplete="new-password" />
            <label for="repeat">{{ common.repeatPassword }}</label>
            <input id="repeat" type="password" formControlName="repeat" autocomplete="new-password" />
            <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
          </form>
          @if (rules().length > 0) {
            <ul class="plain error" data-testid="rules">
              @for (rule of rules(); track rule) {
                <li>{{ ruleText(rule) }}</li>
              }
            </ul>
          }
          @if (error(); as text) {
            <p class="error" role="alert">{{ text }}</p>
          }
        }
        @case ('done') {
          <p role="status" data-testid="reset-done">{{ t.done }} <a routerLink="/login">{{ common.signInLink }}</a></p>
        }
        @case ('invalid') {
          <p class="error" role="alert">{{ common.invalidLink }}</p>
          <p><a routerLink="/forgot">{{ common.askForNewLink }}</a></p>
        }
      }
    </main>
  `,
})
export class ResetPage {
  protected readonly t = texts.reset;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);
  // Read once, here, and gone from the address bar from now on.
  private readonly token = takeTokenFromUrl(inject(ActivatedRoute));

  protected readonly form = inject(NonNullableFormBuilder).group({
    password: ['', Validators.required],
    repeat: ['', Validators.required],
  });
  protected readonly phase = signal<'form' | 'done' | 'invalid'>(this.token === null ? 'invalid' : 'form');
  protected readonly busy = signal(false);
  protected readonly rules = signal<string[]>([]);
  protected readonly error = signal<string | null>(null);

  protected ruleText(rule: string): string {
    // Only the rules this file names: a name like "constructor" is not a rule with a text.
    return Object.hasOwn(this.t.rules, rule) ? this.t.rules[rule] : this.t.ruleOther;
  }

  protected async submit(): Promise<void> {
    if (this.busy() || this.form.invalid || this.token === null) {
      return;
    }
    const { password, repeat } = this.form.getRawValue();
    this.rules.set([]);
    this.error.set(null);
    if (password !== repeat) {
      this.error.set(this.common.passwordsDiffer);
      return;
    }
    this.busy.set(true);
    const result = await this.account.resetPassword(this.token, password);
    this.busy.set(false);
    if (result.ok) {
      this.phase.set('done');
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else if (result.failure.kind === 'weak_password') {
      this.rules.set(result.failure.rules);
    } else {
      this.error.set(this.common.somethingWrong);
    }
  }
}
```

  Run the tests: `reset.spec.ts` passes.

- [ ] **Step 5: Write the tests of the verify screen.** `samples/notes-web/src/app/pages/verify.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { has, pageText, settle, submitForm, typeInto, typeRaw } from '../../testing/helpers';
import { VerifyPage } from './verify';

const status = (code: number) => ({ status: code, statusText: String(code) });
const invalid = 'This link has expired or was already used.';

async function open(query = '?token=tok-9') {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'verify', component: VerifyPage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  history.replaceState(null, '', `/verify${query}`);
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/verify${query}`, VerifyPage);
  return { fixture: harness.fixture, ctrl: TestBed.inject(HttpTestingController) };
}

describe('VerifyPage', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('calls POST /auth/email/verify with the token on opening, and takes the token out of the address bar', async () => {
    const { fixture, ctrl } = await open();
    const request = ctrl.expectOne('/auth/email/verify');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-9' });
    expect(window.location.search).toBe('');
    expect(pageText(fixture)).toContain('Confirming your email...');
    request.flush(null, status(204));
  });

  it('204: "Email confirmed." with a link to /login', async () => {
    const { fixture, ctrl } = await open();
    ctrl.expectOne('/auth/email/verify').flush(null, status(204));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Email confirmed.');
    expect(has(fixture, 'a[href="/login"]')).toBe(true);
  });

  describe('invalid_token', () => {
    async function expired() {
      const opened = await open();
      opened.ctrl.expectOne('/auth/email/verify').flush({ error: 'invalid_token' }, status(400));
      await settle(opened.fixture);
      return opened;
    }

    it('says so and offers a form to send a new link', async () => {
      const { fixture } = await expired();
      expect(pageText(fixture)).toContain(invalid);
      expect(has(fixture, '#email')).toBe(true);
      expect(pageText(fixture)).toContain('Send the verification link again');
    });

    it('the form asks for a new link for the address typed, and says so', async () => {
      const { fixture, ctrl } = await expired();
      typeRaw(fixture, '#email', ' new@example.test ');
      submitForm(fixture);
      const request = ctrl.expectOne('/auth/email/verify/request');
      expect(request.request.body).toEqual({ email: 'new@example.test' });
      request.flush(null, status(202));
      await settle(fixture);
      expect(pageText(fixture)).toContain('If this address needs confirming, we sent a new link.');
    });

    it('429 on the form: "Wait N seconds before asking again."', async () => {
      const { fixture, ctrl } = await expired();
      typeInto(fixture, '#email', 'new@example.test');
      submitForm(fixture);
      ctrl.expectOne('/auth/email/verify/request').flush({ error: 'too_many_attempts', retry_after_seconds: 30 }, status(429));
      await settle(fixture);
      expect(pageText(fixture)).toContain('Wait 30 seconds before asking again.');
    });

    it('sends nothing for an empty address', async () => {
      const { fixture, ctrl } = await expired();
      submitForm(fixture);
      ctrl.expectNone('/auth/email/verify/request');
    });
  });

  it.each([
    [400, { error: 'invalid_request' }],
    [500, '<html>oops</html>'],
  ])('%s: "Something went wrong. Try again."', async (code, body) => {
    const { fixture, ctrl } = await open();
    ctrl.expectOne('/auth/email/verify').flush(body, status(code));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Something went wrong. Try again.');
  });

  it.each(['', '?token=', '?other=1'])('a link with the query %j is an invalid link, with no request', async (query) => {
    const { fixture, ctrl } = await open(query);
    expect(pageText(fixture)).toContain(invalid);
    ctrl.expectNone('/auth/email/verify');
  });
});
```

- [ ] **Step 6: Write the verify screen.** `samples/notes-web/src/app/pages/verify.ts`:

```ts
import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AccountApi } from '../account-api';
import { texts } from '../texts';
import { takeTokenFromUrl } from './token-from-url';

@Component({
  selector: 'app-verify',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @switch (phase()) {
        @case ('working') {
          <p class="status" role="status">{{ t.working }}</p>
        }
        @case ('done') {
          <p role="status" data-testid="verify-done">{{ t.done }} <a routerLink="/login">{{ common.signInLink }}</a></p>
        }
        @case ('invalid') {
          <p class="error" role="alert">{{ common.invalidLink }}</p>
          <form [formGroup]="form" (ngSubmit)="resend()" novalidate>
            <label for="email">{{ common.email }}</label>
            <input id="email" type="email" formControlName="email" autocomplete="username" autocapitalize="none" />
            <button type="submit" [disabled]="busy()">{{ common.sendVerificationAgain }}</button>
          </form>
          @if (info(); as text) {
            <p class="status" role="status">{{ text }}</p>
          }
          @if (error(); as text) {
            <p class="error" role="alert">{{ text }}</p>
          }
        }
        @case ('error') {
          <p class="error" role="alert">{{ common.somethingWrong }}</p>
        }
      }
    </main>
  `,
})
export class VerifyPage implements OnInit {
  protected readonly t = texts.verify;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);
  // Read once, here, and gone from the address bar from now on.
  private readonly token = takeTokenFromUrl(inject(ActivatedRoute));

  protected readonly form = inject(NonNullableFormBuilder).group({ email: ['', Validators.required] });
  protected readonly phase = signal<'working' | 'done' | 'invalid' | 'error'>(this.token === null ? 'invalid' : 'working');
  protected readonly busy = signal(false);
  protected readonly info = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    if (this.token !== null) {
      void this.confirm(this.token);
    }
  }

  protected async resend(): Promise<void> {
    if (this.busy() || this.form.invalid) {
      return;
    }
    this.busy.set(true);
    this.info.set(null);
    this.error.set(null);
    const result = await this.account.requestVerification(this.form.getRawValue().email.trim());
    this.busy.set(false);
    if (result.ok) {
      this.info.set(this.common.verificationSent);
    } else if (result.failure.kind === 'too_many_attempts') {
      this.error.set(this.common.waitSeconds(result.failure.retryAfterSeconds));
    } else {
      this.error.set(this.common.somethingWrong);
    }
  }

  private async confirm(token: string): Promise<void> {
    const result = await this.account.verifyEmail(token);
    if (result.ok) {
      this.phase.set('done');
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else {
      this.phase.set('error');
    }
  }
}
```

  Run the tests: `verify.spec.ts` passes.

- [ ] **Step 7: Write the tests of the invite screen.** `samples/notes-web/src/app/pages/invite.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { has, pageText, settle, submitForm, typeInto, valueOf } from '../../testing/helpers';
import { InvitePage } from './invite';

const status = (code: number) => ({ status: code, statusText: String(code) });
const invalid = 'This invitation has expired or was already used. Ask for a new one.';
const preview = { org_name: 'Acme', email: 'new@acme.example', role: 'viewer' };

async function open(query = '?token=tok-i', previewAnswer: 'ok' | 'wait' = 'ok') {
  TestBed.configureTestingModule({
    providers: [provideRouter([{ path: 'invite', component: InvitePage }]), provideHttpClient(), provideHttpClientTesting()],
  });
  history.replaceState(null, '', `/invite${query}`);
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(`/invite${query}`, InvitePage);
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const ctrl = TestBed.inject(HttpTestingController);
  if (previewAnswer === 'ok') {
    ctrl.expectOne('/auth/invites/preview').flush(preview);
    await settle(harness.fixture);
  }
  return { fixture: harness.fixture, ctrl, navigate };
}

type Opened = Awaited<ReturnType<typeof open>>;

function choose({ fixture }: Opened, password: string, repeat = password): void {
  typeInto(fixture, '#password', password);
  typeInto(fixture, '#repeat', repeat);
  submitForm(fixture);
}

describe('InvitePage', () => {
  afterEach(() => history.replaceState(null, '', '/'));

  it('asks for the preview with the token on opening, and takes the token out of the address bar', async () => {
    const { ctrl } = await open('?token=tok-i', 'wait');
    const request = ctrl.expectOne('/auth/invites/preview');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-i' });
    expect(window.location.search).toBe('');
    request.flush(preview);
  });

  it('shows "Join {company} as {role}", the email (not editable) and who the password is for', async () => {
    const { fixture } = await open();
    expect(pageText(fixture)).toContain('Join Acme as viewer');
    expect(valueOf(fixture, '#email')).toBe('new@acme.example');
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#email')?.readOnly).toBe(true);
    expect(pageText(fixture)).toContain('This sets the password for new@acme.example.');
    expect(has(fixture, '#password')).toBe(true);
    expect(has(fixture, '#repeat')).toBe(true);
  });

  it('204: goes to /login with the email in the navigation state, not in the URL', async () => {
    const opened = await open();
    choose(opened, 'Joined-Passw0rd');
    const request = opened.ctrl.expectOne('/auth/invites/accept');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ token: 'tok-i', password: 'Joined-Passw0rd' });
    request.flush(null, status(204));
    await settle(opened.fixture);
    expect(opened.navigate).toHaveBeenCalledTimes(1);
    expect(opened.navigate).toHaveBeenCalledWith(['/login'], { state: { email: 'new@acme.example' } });
  });

  it('sends the password exactly as typed', async () => {
    const opened = await open();
    choose(opened, '  Joined Passw0rd  ');
    const request = opened.ctrl.expectOne('/auth/invites/accept');
    expect(request.request.body).toEqual({ token: 'tok-i', password: '  Joined Passw0rd  ' });
    request.flush(null, status(204));
  });

  it('weak_password: the rules not met, one line each', async () => {
    const opened = await open();
    choose(opened, 'short');
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'weak_password', rules: ['too_short', 'requires_digit'] }, status(400));
    await settle(opened.fixture);
    const lines = Array.from(
      (opened.fixture.nativeElement as HTMLElement).querySelectorAll('[data-testid="rules"] li'),
      (item) => item.textContent?.trim(),
    );
    expect(lines).toEqual(['The password is too short.', 'The password needs a digit.']);
    expect(opened.navigate).not.toHaveBeenCalled();
  });

  it('409 already_member on accept: "This account already belongs to a company."', async () => {
    const opened = await open();
    choose(opened, 'Joined-Passw0rd');
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'already_member' }, status(409));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('This account already belongs to a company.');
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it('409 already_member on the preview: the same sentence, and no form', async () => {
    const { fixture, ctrl } = await open('?token=tok-i', 'wait');
    ctrl.expectOne('/auth/invites/preview').flush({ error: 'already_member' }, status(409));
    await settle(fixture);
    expect(pageText(fixture)).toContain('This account already belongs to a company.');
    expect(has(fixture, 'form')).toBe(false);
  });

  it('invalid_token on the preview: "This invitation has expired or was already used. Ask for a new one." and no link to /forgot', async () => {
    const { fixture, ctrl } = await open('?token=tok-i', 'wait');
    ctrl.expectOne('/auth/invites/preview').flush({ error: 'invalid_token' }, status(400));
    await settle(fixture);
    expect(pageText(fixture)).toContain(invalid);
    expect(pageText(fixture)).not.toContain('This link has expired');
    expect(has(fixture, 'a')).toBe(false);
    expect(has(fixture, 'form')).toBe(false);
  });

  it('invalid_token on accept: the same sentence, and no link', async () => {
    const opened = await open();
    choose(opened, 'Joined-Passw0rd');
    opened.ctrl.expectOne('/auth/invites/accept').flush({ error: 'invalid_token' }, status(400));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain(invalid);
    expect(has(opened.fixture, 'a')).toBe(false);
    expect(has(opened.fixture, 'form')).toBe(false);
  });

  it.each([
    [400, { error: 'invalid_request' }],
    [500, '<html>oops</html>'],
  ])('%s on the preview: "Something went wrong. Try again."', async (code, body) => {
    const { fixture, ctrl } = await open('?token=tok-i', 'wait');
    ctrl.expectOne('/auth/invites/preview').flush(body, status(code));
    await settle(fixture);
    expect(pageText(fixture)).toContain('Something went wrong. Try again.');
  });

  it('500 on accept: "Something went wrong. Try again.", and the form stays', async () => {
    const opened = await open();
    choose(opened, 'Joined-Passw0rd');
    opened.ctrl.expectOne('/auth/invites/accept').flush('oops', status(500));
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('Something went wrong. Try again.');
    expect(has(opened.fixture, 'form')).toBe(true);
  });

  it('two different passwords: a message, and no request', async () => {
    const opened = await open();
    choose(opened, 'Joined-Passw0rd', 'Joined-Passw0rd2');
    await settle(opened.fixture);
    expect(pageText(opened.fixture)).toContain('The two passwords are not the same.');
    opened.ctrl.expectNone('/auth/invites/accept');
  });

  it.each(['', '?token=', '?other=1'])('a link with the query %j is an invalid invitation, with no request', async (query) => {
    const { fixture, ctrl } = await open(query, 'wait');
    expect(pageText(fixture)).toContain(invalid);
    expect(has(fixture, 'a')).toBe(false);
    ctrl.expectNone('/auth/invites/preview');
  });

  it('sends one request for a double submit', async () => {
    const opened = await open();
    choose(opened, 'Joined-Passw0rd');
    submitForm(opened.fixture);
    opened.ctrl.expectOne('/auth/invites/accept').flush(null, status(204));
  });
});
```

- [ ] **Step 8: Write the invite screen.** `samples/notes-web/src/app/pages/invite.ts`:

```ts
import { Component, OnInit, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AccountApi, InvitePreview } from '../account-api';
import { texts } from '../texts';
import { takeTokenFromUrl } from './token-from-url';

@Component({
  selector: 'app-invite',
  imports: [ReactiveFormsModule],
  template: `
    <main>
      <h1>{{ t.title }}</h1>
      @switch (phase()) {
        @case ('loading') {
          <p class="status" role="status">{{ t.loading }}</p>
        }
        @case ('form') {
          @if (invite(); as invitation) {
            <p data-testid="invite-join">
              {{ t.joinPrefix }} <strong>{{ invitation.org_name }}</strong> {{ t.joinMiddle }}
              <strong>{{ invitation.role }}</strong>
            </p>
            <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
              <label for="email">{{ common.email }}</label>
              <input id="email" type="email" [value]="invitation.email" readonly />
              <p class="status">{{ t.setsPassword(invitation.email) }}</p>
              <label for="password">{{ common.newPassword }}</label>
              <input id="password" type="password" formControlName="password" autocomplete="new-password" />
              <label for="repeat">{{ common.repeatPassword }}</label>
              <input id="repeat" type="password" formControlName="repeat" autocomplete="new-password" />
              <button type="submit" [disabled]="busy()">{{ t.submit }}</button>
            </form>
          }
          @if (rules().length > 0) {
            <ul class="plain error" data-testid="rules">
              @for (rule of rules(); track rule) {
                <li>{{ ruleText(rule) }}</li>
              }
            </ul>
          }
          @if (error(); as text) {
            <p class="error" role="alert">{{ text }}</p>
          }
        }
        @case ('member') {
          <p class="error" role="alert">{{ t.alreadyMember }}</p>
        }
        @case ('invalid') {
          <p class="error" role="alert" data-testid="invite-invalid">{{ t.invalid }}</p>
        }
        @case ('error') {
          <p class="error" role="alert">{{ common.somethingWrong }}</p>
        }
      }
    </main>
  `,
})
export class InvitePage implements OnInit {
  protected readonly t = texts.invite;
  protected readonly common = texts.common;
  private readonly account = inject(AccountApi);
  private readonly router = inject(Router);
  // Read once, here, and gone from the address bar from now on.
  private readonly token = takeTokenFromUrl(inject(ActivatedRoute));

  protected readonly form = inject(NonNullableFormBuilder).group({
    password: ['', Validators.required],
    repeat: ['', Validators.required],
  });
  protected readonly phase = signal<'loading' | 'form' | 'member' | 'invalid' | 'error'>(
    this.token === null ? 'invalid' : 'loading',
  );
  protected readonly invite = signal<InvitePreview | null>(null);
  protected readonly busy = signal(false);
  protected readonly rules = signal<string[]>([]);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    if (this.token !== null) {
      void this.preview(this.token);
    }
  }

  protected ruleText(rule: string): string {
    // Only the rules texts.ts names: a name like "constructor" is not a rule with a text.
    return Object.hasOwn(texts.reset.rules, rule) ? texts.reset.rules[rule] : texts.reset.ruleOther;
  }

  protected async submit(): Promise<void> {
    const invitation = this.invite();
    if (this.busy() || this.form.invalid || this.token === null || invitation === null) {
      return;
    }
    const { password, repeat } = this.form.getRawValue();
    this.rules.set([]);
    this.error.set(null);
    if (password !== repeat) {
      this.error.set(this.common.passwordsDiffer);
      return;
    }
    this.busy.set(true);
    const result = await this.account.acceptInvite(this.token, password);
    this.busy.set(false);
    if (result.ok) {
      // The email goes in the navigation state: it is never in the URL.
      await this.router.navigate(['/login'], { state: { email: invitation.email } });
    } else if (result.failure.kind === 'weak_password') {
      this.rules.set(result.failure.rules);
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else if (result.failure.kind === 'already_member') {
      this.phase.set('member');
    } else {
      this.error.set(this.common.somethingWrong);
    }
  }

  private async preview(token: string): Promise<void> {
    const result = await this.account.previewInvite(token);
    if (result.ok) {
      this.invite.set(result.value);
      this.phase.set('form');
    } else if (result.failure.kind === 'invalid_token') {
      this.phase.set('invalid');
    } else if (result.failure.kind === 'already_member') {
      this.phase.set('member');
    } else {
      this.phase.set('error');
    }
  }
}
```

  The invite screen reuses the rule texts of `texts.reset` on purpose: they are the same rules of the same policy.

- [ ] **Step 9: The routes.** Replace `samples/notes-web/src/app/app.routes.ts`:

```ts
import { Routes } from '@angular/router';
import { ForgotPage } from './pages/forgot';
import { InvitePage } from './pages/invite';
import { LoginPage } from './pages/login';
import { ResetPage } from './pages/reset';
import { VerifyPage } from './pages/verify';
import { texts } from './texts';

export const routes: Routes = [
  { path: 'login', component: LoginPage, title: texts.login.title },
  { path: 'forgot', component: ForgotPage, title: texts.forgot.title },
  { path: 'reset', component: ResetPage, title: texts.reset.title },
  { path: 'verify', component: VerifyPage, title: texts.verify.title },
  { path: 'invite', component: InvitePage, title: texts.invite.title },
];
```

- [ ] **Step 10: Run everything.**

```bash
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -15
npx ng build 2>&1 | grep -v allow-scripts | tail -5
grep -rn "token" src/app/pages/*.ts | grep -v "\.spec\.ts" | grep -i "console\|log(" | head
```

  Expected: all tests pass; the build completes; the last `grep` prints nothing (no page logs a token).

- [ ] **Step 11: Hand over** - uncommitted; suggested message `feat(web): reset, verify and invite screens that read the mail token once`.

### Task 7: The notes screen, the bars at work, the final routes

**Files:**
- Create: `samples/notes-web/src/app/pages/notes.ts`
- Modify: `samples/notes-web/src/app/app.routes.ts` (whole file given)
- Test: `samples/notes-web/src/app/pages/notes.spec.ts`, `src/app/app.routes.spec.ts`

**Interfaces:**
- Consumes: `AuthService` (`me`, `loadMe`, `logout`), `authGuard` (Task 4), the interceptor's behavior (Task 3; the page does
  not know about it: a failed call is just an error to it), `texts.notes`, the helpers of Tasks 2 and 5.
- Produces: `NotesPage`; the final routes: `/` and every unknown path go to `/notes`; `/notes` is guarded.

**The screen (spec):** `/notes` is guarded. A header with the email, company and role from `GET /auth/me`, and "Sign out". The
company's notes, newest first (as `GET /api/notes` sends them), and a form to add one, shown only when `/auth/me` lists
`notes:write`. Sign out calls `POST /auth/logout`, drops the token and goes to `/login`, whatever the answer. A note is shown
as text, never as HTML. A note of only spaces is not sent; the form allows at most 1000 characters (the service's limit). The
bars of the interceptor (`503 auth_unavailable`: "Try again shortly."; `403 forbidden`: "You don't have access to this.") are
drawn by the shell of Task 2; the screen shows only its own message for a list that could not be loaded, and keeps the
header.

- [ ] **Step 1: Write the tests.** `samples/notes-web/src/app/pages/notes.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { AuthService, Me } from '../auth/auth.service';
import { adminMe, clickOn, has, holdToken, pageText, settle, signedInAs, submitForm, typeInto, viewerMe } from '../../testing/helpers';
import { NotesPage } from './notes';

const status = (code: number) => ({ status: code, statusText: String(code) });

// A note's time, as the service sends it (ISO 8601, UTC): the n-th day after the epoch.
const day = (n: number): string => new Date(n * 86_400_000).toISOString();

const newer = { id: 'n2', text: 'second note', author_sub: 'u1', created_at: day(2) };
const older = { id: 'n1', text: 'first note', author_sub: 'u1', created_at: day(1) };

async function open(me: Me = adminMe, notes: unknown = [newer, older]) {
  TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
  const ctrl = TestBed.inject(HttpTestingController);
  const auth = TestBed.inject(AuthService);
  await signedInAs(auth, ctrl, me);
  const navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(NotesPage);
  fixture.detectChanges();
  ctrl.expectOne('/api/notes').flush(notes);
  await settle(fixture);
  return { fixture, ctrl, auth, navigateByUrl };
}

function textOf(fixture: { nativeElement: unknown }, selector: string): string | undefined {
  return (fixture.nativeElement as HTMLElement).querySelector(selector)?.textContent?.trim();
}

describe('NotesPage', () => {
  it('shows the email, company and role from /auth/me, and "Sign out"', async () => {
    const { fixture } = await open();
    expect(textOf(fixture, '[data-testid="me-email"]')).toBe('admin@example.test');
    expect(textOf(fixture, '[data-testid="me-company"]')).toBe('Acme');
    expect(textOf(fixture, '[data-testid="me-role"]')).toBe('admin');
    expect(textOf(fixture, '[data-testid="sign-out"]')).toBe('Sign out');
  });

  it('lists the notes in the order the service sends them, newest first', async () => {
    const { fixture } = await open();
    const items = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.note p'), (p) => p.textContent);
    expect(items).toEqual(['second note', 'first note']);
  });

  it('says so when there are no notes', async () => {
    const { fixture } = await open(adminMe, []);
    expect(pageText(fixture)).toContain('No notes yet.');
  });

  describe('the form to add a note', () => {
    it('is shown when /auth/me lists notes:write', async () => {
      const { fixture } = await open(adminMe);
      expect(has(fixture, '#text')).toBe(true);
      expect(pageText(fixture)).toContain('Add note');
    });

    it('is not shown to a viewer, who still reads the notes', async () => {
      const { fixture } = await open(viewerMe);
      expect(has(fixture, '#text')).toBe(false);
      expect(has(fixture, 'form')).toBe(false);
      expect(pageText(fixture)).toContain('second note');
      expect(textOf(fixture, '[data-testid="me-role"]')).toBe('viewer');
    });

    it('adds a note: the text is sent, the new note is on top and the field is empty again', async () => {
      const { fixture, ctrl } = await open();
      typeInto(fixture, '#text', 'a brand new note');
      submitForm(fixture);
      const request = ctrl.expectOne('/api/notes');
      expect(request.request.method).toBe('POST');
      expect(request.request.body).toEqual({ text: 'a brand new note' });
      request.flush({ id: 'n3', text: 'a brand new note', author_sub: 'u1', created_at: day(3) }, status(201));
      await settle(fixture);
      const items = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('.note p'), (p) => p.textContent);
      expect(items).toEqual(['a brand new note', 'second note', 'first note']);
      expect((fixture.nativeElement as HTMLElement).querySelector<HTMLTextAreaElement>('#text')?.value).toBe('');
    });

    it('does not send a note of only spaces or an empty one', async () => {
      const { fixture, ctrl } = await open();
      submitForm(fixture);
      typeInto(fixture, '#text', '   \n  ');
      submitForm(fixture);
      ctrl.expectNone('/api/notes');
    });

    it('does not send a note of more than 1000 characters', async () => {
      const { fixture, ctrl } = await open();
      typeInto(fixture, '#text', 'x'.repeat(1001));
      submitForm(fixture);
      ctrl.expectNone('/api/notes');
    });

    it('sends a note of exactly 1000 characters', async () => {
      const { fixture, ctrl } = await open();
      typeInto(fixture, '#text', 'x'.repeat(1000));
      submitForm(fixture);
      ctrl.expectOne('/api/notes').flush({ id: 'n3', text: 'x'.repeat(1000), author_sub: 'u1', created_at: day(3) }, status(201));
    });

    it('sends one request for a double submit', async () => {
      const { fixture, ctrl } = await open();
      typeInto(fixture, '#text', 'once');
      submitForm(fixture);
      submitForm(fixture);
      ctrl.expectOne('/api/notes').flush({ id: 'n3', text: 'once', author_sub: 'u1', created_at: day(3) }, status(201));
    });

    it('a note that cannot be saved says so, and keeps what was typed', async () => {
      const { fixture, ctrl } = await open();
      typeInto(fixture, '#text', 'precious words');
      submitForm(fixture);
      ctrl.expectOne('/api/notes').flush({ error: 'invalid_request' }, status(400));
      await settle(fixture);
      expect(pageText(fixture)).toContain('The note could not be saved.');
      expect((fixture.nativeElement as HTMLElement).querySelector<HTMLTextAreaElement>('#text')?.value).toBe('precious words');
    });
  });

  it('shows a note as text, never as HTML', async () => {
    const hostile = { id: 'n9', text: '<img src=x onerror="alert(1)"><b>bold</b>', author_sub: 'u', created_at: day(3) };
    const { fixture } = await open(adminMe, [hostile]);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.note img')).toBeNull();
    expect(root.querySelector('.note b')).toBeNull();
    expect(root.querySelector('.note p')?.textContent).toBe('<img src=x onerror="alert(1)"><b>bold</b>');
  });

  describe('a list that cannot be loaded keeps the header and says so', () => {
    it.each([
      ['a 503 auth_unavailable', { error: 'auth_unavailable' }, 503],
      ['a 403 forbidden', { error: 'forbidden' }, 403],
      ['a 502 page of HTML from a proxy', '<html>bad gateway</html>', 502],
      ['a 500', null, 500],
    ])('%s', async (_name, body, code) => {
      TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
      const ctrl = TestBed.inject(HttpTestingController);
      await signedInAs(TestBed.inject(AuthService), ctrl);
      const fixture = TestBed.createComponent(NotesPage);
      fixture.detectChanges();
      ctrl.expectOne('/api/notes').flush(body, status(code));
      await settle(fixture);
      expect(pageText(fixture)).toContain('The notes could not be loaded.');
      expect(textOf(fixture, '[data-testid="me-email"]')).toBe('admin@example.test');
      expect(pageText(fixture)).not.toContain('No notes yet.');
    });

    it('a 200 that is not a list is the same failure', async () => {
      const { fixture } = await open(adminMe, { not: 'a list' });
      expect(pageText(fixture)).toContain('The notes could not be loaded.');
    });
  });

  it('asks /auth/me when the person is not known yet', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    const ctrl = TestBed.inject(HttpTestingController);
    await holdToken(TestBed.inject(AuthService), ctrl, 'tok');
    const fixture = TestBed.createComponent(NotesPage);
    fixture.detectChanges();
    ctrl.expectOne('/api/notes').flush([]);
    ctrl.expectOne('/auth/me').flush(adminMe);
    await settle(fixture);
    expect(textOf(fixture, '[data-testid="me-email"]')).toBe('admin@example.test');
  });

  describe('Sign out', () => {
    it.each([
      ['204', null, 204],
      ['a 500', 'oops', 500],
    ])('calls POST /auth/logout, drops the token and goes to /login after %s', async (_name, body, code) => {
      const { fixture, ctrl, auth, navigateByUrl } = await open();
      clickOn(fixture, '[data-testid="sign-out"]');
      const request = ctrl.expectOne('/auth/logout');
      expect(request.request.method).toBe('POST');
      request.flush(body, status(code));
      await settle(fixture);
      expect(auth.token()).toBeNull();
      expect(auth.me()).toBeNull();
      expect(navigateByUrl).toHaveBeenCalledWith('/login');
    });

    it('goes to /login when the network fails', async () => {
      const { fixture, ctrl, auth, navigateByUrl } = await open();
      clickOn(fixture, '[data-testid="sign-out"]');
      ctrl.expectOne('/auth/logout').error(new ProgressEvent('error'));
      await settle(fixture);
      expect(auth.token()).toBeNull();
      expect(navigateByUrl).toHaveBeenCalledWith('/login');
    });
  });
});
```

  Run it and see it fail (`Cannot find module './notes'`).

- [ ] **Step 2: Write the notes screen.** `samples/notes-web/src/app/pages/notes.ts`:

```ts
import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { texts } from '../texts';

interface Note {
  id: string;
  text: string;
  author_sub: string;
  created_at: string;
}

const MAX_NOTE_CHARACTERS = 1000;

@Component({
  selector: 'app-notes',
  imports: [ReactiveFormsModule, DatePipe],
  template: `
    <main class="wide">
      <header class="top">
        @if (me(); as person) {
          <p class="who">
            <strong data-testid="me-email">{{ person.email }}</strong>
            <span data-testid="me-company">{{ person.org_name }}</span>
            <span data-testid="me-role">{{ person.roles.join(', ') }}</span>
          </p>
        }
        <button type="button" data-testid="sign-out" (click)="signOut()">{{ t.signOut }}</button>
      </header>
      <h1>{{ t.title }}</h1>
      @if (canWrite()) {
        <form [formGroup]="form" (ngSubmit)="add()" novalidate>
          <label for="text">{{ t.newNote }}</label>
          <textarea id="text" rows="3" formControlName="text" maxlength="1000"></textarea>
          <button type="submit" [disabled]="busy()">{{ t.add }}</button>
        </form>
        @if (addFailed()) {
          <p class="error" role="alert">{{ t.addFailed }}</p>
        }
      }
      @if (loadFailed()) {
        <p class="error" role="alert">{{ t.loadFailed }}</p>
      } @else if (loaded() && notes().length === 0) {
        <p class="status">{{ t.empty }}</p>
      }
      <ul class="plain">
        @for (note of notes(); track note.id) {
          <li class="note">
            <p>{{ note.text }}</p>
            <small class="status">{{ note.created_at | date: 'medium' }}</small>
          </li>
        }
      </ul>
    </main>
  `,
})
export class NotesPage implements OnInit {
  protected readonly t = texts.notes;
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly me = this.auth.me;
  protected readonly canWrite = computed(() => this.me()?.permissions.includes('notes:write') ?? false);
  protected readonly form = inject(NonNullableFormBuilder).group({
    text: ['', [Validators.required, Validators.maxLength(MAX_NOTE_CHARACTERS)]],
  });
  protected readonly notes = signal<Note[]>([]);
  protected readonly loaded = signal(false);
  protected readonly loadFailed = signal(false);
  protected readonly addFailed = signal(false);
  protected readonly busy = signal(false);

  ngOnInit(): void {
    if (this.me() === null) {
      void this.auth.loadMe();
    }
    void this.load();
  }

  protected async add(): Promise<void> {
    const text = this.form.controls.text.value.trim();
    if (this.busy() || text === '' || this.form.invalid) {
      return;
    }
    this.busy.set(true);
    this.addFailed.set(false);
    try {
      const note = await firstValueFrom(this.http.post<Note>('/api/notes', { text }));
      this.notes.update((list) => [note, ...list]);
      this.form.reset();
    } catch {
      this.addFailed.set(true);
    } finally {
      this.busy.set(false);
    }
  }

  protected async signOut(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }

  private async load(): Promise<void> {
    try {
      const list = await firstValueFrom(this.http.get<Note[]>('/api/notes'));
      if (!Array.isArray(list)) {
        throw new Error('not a list');
      }
      this.notes.set(list);
      this.loaded.set(true);
    } catch {
      this.loadFailed.set(true);
    }
  }
}
```

  Run the tests: `notes.spec.ts` passes. (`created_at` goes through the `date` pipe; the tests use ISO times, as the service sends.)

- [ ] **Step 3: The final routes, and their test.** Replace `samples/notes-web/src/app/app.routes.ts`:

```ts
import { Routes } from '@angular/router';
import { authGuard } from './auth/auth.guard';
import { ForgotPage } from './pages/forgot';
import { InvitePage } from './pages/invite';
import { LoginPage } from './pages/login';
import { NotesPage } from './pages/notes';
import { ResetPage } from './pages/reset';
import { VerifyPage } from './pages/verify';
import { texts } from './texts';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'notes' },
  { path: 'login', component: LoginPage, title: texts.login.title },
  { path: 'forgot', component: ForgotPage, title: texts.forgot.title },
  { path: 'reset', component: ResetPage, title: texts.reset.title },
  { path: 'verify', component: VerifyPage, title: texts.verify.title },
  { path: 'invite', component: InvitePage, title: texts.invite.title },
  { path: 'notes', component: NotesPage, canActivate: [authGuard], title: texts.notes.title },
  { path: '**', redirectTo: 'notes' },
];
```

  `samples/notes-web/src/app/app.routes.spec.ts`:

```ts
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { adminMe, settle, signedInAs } from '../testing/helpers';
import { routes } from './app.routes';
import { AuthService } from './auth/auth.service';
import { LoginPage } from './pages/login';
import { NotesPage } from './pages/notes';

describe('routes', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()] });
  });

  it.each(['/', '/notes', '/nowhere', '/notes/deeper/than/that'])(
    'an anonymous person who asks for %s lands on /login?returnUrl=%%2Fnotes',
    async (url) => {
      const harness = await RouterTestingHarness.create();
      await harness.navigateByUrl(url);
      expect(TestBed.inject(Router).url).toBe('/login?returnUrl=%2Fnotes');
      expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(LoginPage);
    },
  );

  it.each(['/', '/nowhere'])('a signed-in person who asks for %s lands on /notes', async (url) => {
    const ctrl = TestBed.inject(HttpTestingController);
    await signedInAs(TestBed.inject(AuthService), ctrl, adminMe);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    ctrl.expectOne('/api/notes').flush([]);
    await settle(harness.fixture);
    expect(TestBed.inject(Router).url).toBe('/notes');
    expect(harness.routeDebugElement?.componentInstance).toBeInstanceOf(NotesPage);
  });

  it.each(['/login', '/forgot', '/reset', '/verify', '/invite'])('%s needs no token', async (url) => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    expect(TestBed.inject(Router).url).toBe(url);
  });
});
```

  The `%%2Fnotes` in the title is how `it.each` prints a `%`.

  Note for the implementer: the last test opens `/reset`, `/verify` and `/invite` without a token, which the pages answer
  with "invalid link" and no request, so `HttpTestingController` has nothing to verify.

- [ ] **Step 4: Run everything and check the group.**

```bash
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -15
npx ng build 2>&1 | grep -v allow-scripts | tail -8
grep -rInP --exclude-dir=node_modules --exclude-dir=dist --exclude-dir=.angular --exclude=package-lock.json '\x5c[uU][0-9a-fA-F]{4}|[^\x00-\x7F]' . | head
grep -rn "innerHTML\|bypassSecurityTrust\|eval(" src --include=*.ts | grep -v "\.spec\.ts"
git status --porcelain
```

  Expected: all tests pass; the build completes (production, with the real routes); the first `grep` prints nothing; the
  second prints nothing (no raw HTML anywhere in the application); `git status` shows only `samples/notes-web/`.

- [ ] **Step 5: Optional look at the real thing** (needs the stack of Task 8; skip until then, and do not block on it): `npx ng
  serve` and `http://localhost:4200` with the stack up shows the sign-in screen. The end-to-end tests of Group C are the
  check that counts.

- [ ] **Step 6: Hand over** - uncommitted; suggested message `feat(web): notes screen with the person's header, final routes`.

---

## Group C - the stack, the end-to-end tests, the guide

When the group is done, the app is served by Caddy on the stack's own origin over HTTP (`:8088`) and HTTPS (`:8443`), the seven
test groups of the spec pass in Chromium and in WebKit through `scripts/e2e-web.sh`, the integration guide exists with every
file it names, and the verifiers have a map from every criterion to a test. Every task here needs Docker, the ports
`8088`, `8443`, `8080` and `8025` free, and a `.env` and `.secrets/` (made in Task 8, step 5).

### Task 8: The image, the Caddyfile, the overlay and the README

**Files:**
- Create: `samples/notes-web/Dockerfile`, `samples/notes-web/.dockerignore`, `samples/notes-web/Caddyfile`,
  `samples/notes-web/compose.yml`, `samples/notes-web/README.md`

**Interfaces:**
- Consumes: the production build of Groups A and B (`dist/notes-web/browser`), the base stack `deploy/docker-compose.yml` and
  the overlay `samples/notes-api/compose.yml` (both stay unchanged), the services `auth`, `notes-api`, `caddy`, `mailpit`.
- Produces: the third overlay, used as `docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f
  samples/notes-web/compose.yml --env-file .env up -d --build`; the image `notes-web-caddy:local`; the origins
  `http://localhost:8088` and `https://localhost:8443`, both on loopback only; the headers of the spec on the app's responses;
  the mail links `http://localhost:8088/reset`, `/verify` and `/invite`.

**Choices:**
- The build context is `samples/notes-web` itself (`context: ../samples/notes-web` from `deploy/`, where Compose resolves
  relative paths), not the repository root: the image needs nothing outside the sample, and the sample's own
  `.dockerignore` keeps `node_modules` and `dist` out of the context.
- The overlay sets its own `image: notes-web-caddy:local`. In a third file `build` does not replace the `image` of the
  second one (`caddy:2`), so without it the new build would be tagged `caddy:2`. A `volumes` entry with the same target
  as an earlier file replaces it, which is how this Caddyfile replaces the one of `samples/notes-api/compose.yml`; `ports` are
  added to; `environment` is merged.
- One Caddyfile, one snippet of routes for both listeners: `/auth/*` to Auth-Core, `/api/*` to the notes service, every other
  path to the built app, with `index.html` as the answer to a path that is not a file. The security headers are inside the
  app's block only: `/auth` and `/api` set their own.
- `Cache-Control: no-cache` goes on every answer that is `index.html`, the answer to an unknown path included: the matcher is
  "not an existing `.js`, `.css` or `.ico` file", so `/missing.js` is `index.html` and revalidated, while the hashed files that
  exist may be cached.
- The nonce is the request's own `{http.request.uuid}`, written into the header and, by `templates`, into the `ngCspNonce`
  attribute of `index.html`: the same value in both, new for every response. `'self'` stays next to it: the build's
  stylesheet is a file of the app.
- WebKit keeps a `Secure` cookie set over `http://localhost` but never sends it back, and Auth-Core always sets `Secure`: so Caddy
  also serves `https://localhost:8443` with `tls internal` (its own certificate authority, no new package), and the tests run
  there. `skip_install_trust` stops Caddy from trying to install its root into a trust store inside the container.
  `auto_https disable_redirects` keeps it from adding a redirect listener on port 80. `servers { protocols h1 h2 }` turns HTTP/3
  off: its UDP port is not published (probed: the site still answers over HTTP/1.1 and HTTP/2).

- [ ] **Step 1: The Caddyfile.** `samples/notes-web/Caddyfile`:

```text
# One origin for the browser, on two listeners with the same routes:
#   http://localhost:8088   development; the mail links point here
#   https://localhost:8443  the same app with a certificate from Caddy's own authority (not trusted by a browser: a
#                           person sees a warning, Playwright accepts it). WebKit needs HTTPS to send the refresh
#                           cookie back, which Auth-Core always sets Secure.
# /auth is Auth-Core, /api is the notes service, everything else is the built app. Development only.
{
	admin off
	auto_https disable_redirects
	skip_install_trust
	# No HTTP/3: its UDP port is not published, and a browser must not try it.
	servers {
		protocols h1 h2
	}
}

(routes) {
	handle /auth/* {
		reverse_proxy auth:8080
	}
	handle /api/* {
		reverse_proxy notes-api:8000
	}
	# The app. The headers are here and not on /auth and /api, which set their own.
	handle {
		root * /srv
		encode gzip
		header {
			Content-Security-Policy "default-src 'self'; script-src 'self'; style-src 'self' 'nonce-{http.request.uuid}'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'"
			Referrer-Policy no-referrer
			X-Content-Type-Options nosniff
		}
		# Every answer that is index.html is revalidated, an unknown path included. Only a .js, .css or .ico file that
		# exists (hashed by the build) may be cached.
		@revalidate not {
			path *.js *.css *.ico
			file
		}
		header @revalidate Cache-Control no-cache
		try_files {path} /index.html
		# Fills in {{placeholder `http.request.uuid`}} of index.html: the nonce of the style elements Angular adds.
		templates {
			mime text/html
		}
		file_server
	}
}

http://:8088 {
	import routes
}

https://localhost:8443 {
	tls internal
	import routes
}
```

  Prove it in step 2 before building anything on it.

- [ ] **Step 2: Prove `tls internal` with that Caddyfile, before anything else is built on it.** This
  is the one point of the spec that was not tried (its "Still to verify"). Build the app
  and run Caddy alone with the Caddyfile of step 1, with no compose, on two throwaway host ports:

```bash
cd samples/notes-web && npx ng build 2>&1 | grep -v allow-scripts | tail -2 && cd ../..
docker rm -f p8caddy >/dev/null 2>&1
MSYS_NO_PATHCONV=1 docker run -d --name p8caddy -p 127.0.0.1:18088:8088 -p 127.0.0.1:18443:8443 \
  -v "$(pwd -W)/samples/notes-web/Caddyfile:/etc/caddy/Caddyfile:ro" \
  -v "$(pwd -W)/samples/notes-web/dist/notes-web/browser:/srv:ro" caddy:2
sleep 4
docker logs p8caddy 2>&1 | grep -o '"msg":"[^"]*"' | sort | uniq -c
curl -ski http://localhost:18088/login | sed -n '1p;/^Cache-Control/p;/^Content-Security/p;/^Referrer/p;/^X-Content/p'
curl -ski https://localhost:18443/login | sed -n '1p;/^Cache-Control/p'
```

  Expected: the log has `certificate obtained successfully` (the internal authority made one for `localhost`),
  `root certificate trust store installation disabled; unconfigured clients may show warnings` and
  `serving initial configuration`, and no line that says it installed or could not install a root certificate; both `curl`s
  print `HTTP/1.1 200 OK` and `Cache-Control: no-cache`, the HTTP one also the CSP (with `'nonce-` and a UUID), `Referrer-Policy:
  no-referrer` and `X-Content-Type-Options: nosniff`. (`-k` because the certificate is not trusted: that is the point of
  Decision 9.) **If the certificate is not obtained or HTTPS does not answer: stop and report to the orchestrator.** The spec
  agreed Decision 9 with this as its fallback; the next fallback to try is a certificate made with `openssl` for `localhost`
  and mounted into the container with `tls /etc/caddy/local.crt /etc/caddy/local.key`, which the owner decides on.

- [ ] **Step 3: The Dockerfile and its ignore file.** `samples/notes-web/Dockerfile`:

```dockerfile
# syntax=docker/dockerfile:1
# The "notes" frontend: node builds the app, Caddy serves it. The compose overlay builds it with this directory as the
# context. By hand:  docker build -f samples/notes-web/Dockerfile samples/notes-web

FROM node:24-alpine AS build
ENV NG_CLI_ANALYTICS=false
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY angular.json tsconfig.json tsconfig.app.json tsconfig.spec.json ./
COPY public public
COPY src src
RUN npx ng build --configuration production

FROM caddy:2
COPY --from=build /app/dist/notes-web/browser /srv
```

  `samples/notes-web/.dockerignore`:

```
node_modules
dist
.angular
out-tsc
e2e
test-results
playwright-report
.git
.env
.env.*
*.md
```

  (The Caddyfile is mounted by the overlay, not copied. If the image build fails on Alpine in a native module of the Angular
  build such as `lmdb`, add `RUN npx ng cache disable` before the `ng build` line and say so in the report.)

- [ ] **Step 4: The overlay.** `samples/notes-web/compose.yml`:

```yaml
# The "notes" frontend on top of the development stack and the notes sample. Use it with both:
#
#   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f samples/notes-web/compose.yml \
#     --env-file .env up -d --build
#
# Relative paths in this file are resolved from deploy/ (the directory of the FIRST -f file), not from here.
# The browser's origin is http://localhost:8088 (and https://localhost:8443 with a certificate that a browser does not trust):
#   /auth -> Auth-Core, /api -> the notes service, everything else -> the built app. Both ports are on loopback only.
services:
  caddy:
    # Replaces the plain proxy of samples/notes-api/compose.yml. Without its own image name Compose would tag this build
    # "caddy:2", the name of the image it is built on.
    image: notes-web-caddy:local
    build:
      context: ../samples/notes-web
      dockerfile: Dockerfile
    ports:
      # Added to the 8088 of the notes overlay. Loopback only, like every other port of this stack.
      - "127.0.0.1:8443:8443"
    volumes:
      # The same container path as in the notes overlay: this mount replaces that Caddyfile.
      - ../samples/notes-web/Caddyfile:/etc/caddy/Caddyfile:ro

  auth:
    environment:
      # The links in Auth-Core's mails open the screens of this app (plain http is accepted in Development only).
      Auth__App__FrontendUrls__ResetPassword: http://localhost:8088/reset
      Auth__App__FrontendUrls__VerifyEmail: http://localhost:8088/verify
      Auth__App__FrontendUrls__AcceptInvite: http://localhost:8088/invite
```

  Stop the Caddy of step 1: `docker rm -f p8caddy`.

- [ ] **Step 5: Check the merge of the three files, then bring the stack up.** Needs a `.env` and `.secrets/`; a compose
  project name of its own. The first build pulls `node:24-alpine`, which is not on this machine yet; the owner approved it
  (Decision 8 of the spec names the image), so the build may pull it.

  This worktree may already have a `.env` (from an earlier slice) that lacks the variables of this one. The block below
  uses what exists, adds what is missing, and leaves a marker file in the git directory (outside the working tree) for each
  thing it made, so that Task 11 removes only those. Nothing it makes or reads is printed.

```bash
GD="$(git rev-parse --git-dir)"
[ -f .env ] || { cp .env.example .env; : > "$GD/web-made-env"; }
grep -q '^NOTES_DB_PASSWORD=' .env || printf '\nNOTES_DB_PASSWORD=local-%s\n' "$RANDOM$RANDOM" >> .env
grep -q '^AUTH_DEV_SEED_UNVERIFIED_EMAIL=' .env \
  || printf '\nAUTH_DEV_SEED_UNVERIFIED_EMAIL=new@example.com\nAUTH_DEV_SEED_UNVERIFIED_PASSWORD=Dev-Unverified-Passw0rd\n' >> .env
[ -d .secrets ] || { MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh; : > "$GD/web-made-secrets"; }
export COMPOSE_PROJECT_NAME=auth-core-web-t8
C="docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f samples/notes-web/compose.yml --env-file .env"
# The merged file holds the interpolated passwords: it goes to a temporary file that is removed at once.
MERGED="$(mktemp)"
$C config > "$MERGED"
sed -n '/^  caddy:/,/^  mailpit:/p' "$MERGED" | grep -n 'image:\|published:\|host_ip:\|source:.*Caddyfile'
grep -c 'target: /etc/caddy/Caddyfile' "$MERGED"
grep -n 'FrontendUrls' "$MERGED"
rm -f "$MERGED"
git diff --stat HEAD -- deploy/docker-compose.yml samples/notes-api src tests
```

  Expected: `image: notes-web-caddy:local`; two `published:` lines (`8088` and `8443`) each with `host_ip: 127.0.0.1`; one `source:`
  ending in `samples\notes-web\Caddyfile` (or `/`); `1` from the `grep -c` (one mount of that path, not two); the three
  `FrontendUrls` lines pointing at `http://localhost:8088/...`; and no output from `git diff --stat` (nothing of the base stack,
  the notes sample, `src/` or `tests/` changed).

```bash
$C up -d --build 2>&1 | tail -5
for i in $(seq 1 60); do [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:8088/auth/health)" = 200 ] && break; sleep 2; done
curl -s http://localhost:8088/auth/health; echo
curl -s -o /dev/null -w 'api health: %{http_code}\n' http://localhost:8088/api/health
curl -s -o /dev/null -w 'login over http: %{http_code}\n' http://localhost:8088/login
curl -sk -o /dev/null -w 'login over https: %{http_code}\n' https://localhost:8443/login
curl -sk https://localhost:8443/auth/health; echo
curl -sk -o /dev/null -w 'api over https, no token: %{http_code}\n' https://localhost:8443/api/notes
echo "caddy publishes:"; docker port "$($C ps -q caddy)"
```

  Expected: the build succeeds (the first one takes several minutes: it installs the packages in the image and builds the app);
  `Healthy`; `api health: 200`; `login over http: 200`; `login over https: 200`; `Healthy` again over HTTPS (`/auth` through
  the same Caddy); `api over https, no token: 401`; and `docker port` shows `8088/tcp -> 127.0.0.1:8088` and `8443/tcp ->
  127.0.0.1:8443`, nothing else.

- [ ] **Step 6: Check the nonce, the cache rule and what the file server serves.**

```bash
curl -si http://localhost:8088/login | node -e '
const text = require("fs").readFileSync(0, "utf8");
const header = /nonce-([0-9a-f-]{36})/.exec(text)?.[1];
const body = /ngCspNonce="([0-9a-f-]{36})"/i.exec(text)?.[1];
console.log(header && header === body ? "nonce in header and page agree" : "NONCE MISMATCH " + header + " " + body);'
for p in /login /notes /no/such/page /missing.js /index.html; do
  printf '%s -> ' "$p"; curl -s -D - -o /dev/null "http://localhost:8088$p" | tr -d '\r' | grep -i '^HTTP\|^cache-control' | tr '\n' ' '; echo
done
JS="$(ls samples/notes-web/dist/notes-web/browser | grep -m1 '^main-.*\.js$')"
printf '%s (a hashed file) -> ' "$JS"; curl -s -D - -o /dev/null "http://localhost:8088/$JS" | tr -d '\r' | grep -i '^HTTP\|^cache-control' | tr '\n' ' '; echo
echo "outside the build:"
for p in /Caddyfile /etc/passwd /../../etc/passwd /.env; do
  printf '%s -> ' "$p"; curl -s --path-as-is "http://localhost:8088$p" | head -c 15; echo
done
echo "the headers stay on the app, not on /auth and /api:"
for p in /auth/health /api/health; do
  printf '%s -> ' "$p"; curl -s -D - -o /dev/null "http://localhost:8088$p" | tr -d '\r' | grep -ic '^content-security-policy\|^referrer-policy'
done
```

  Expected: `nonce in header and page agree`; `/login`, `/notes`, `/no/such/page`, `/missing.js` and `/index.html` each `HTTP/1.1
  200 OK Cache-Control: no-cache`; the hashed `main-*.js` `HTTP/1.1 200 OK` with **no** `Cache-Control` of `no-cache`; and every
  path of the `outside the build` loop prints `<!doctype html>` (the app's `index.html`: nothing outside the build is served, the
  Caddyfile least of all); and `/auth/health -> 0` and `/api/health -> 0` (no `Content-Security-Policy` and no `Referrer-Policy`
  on what Caddy only proxies: the headers are inside the app's block).

- [ ] **Step 7: Check that a mail link reaches the app.** Ask for a reset mail for the seeded user (the address is read from
  `.env`, the token is never printed) and look at where the link points:

```bash
SEED="$(grep -E '^AUTH_DEV_SEED_EMAIL=' .env | tail -n1 | cut -d= -f2- | tr -d '\r')"
curl -s -o /dev/null -w 'forgot: %{http_code}\n' -X POST http://localhost:8088/auth/password/forgot \
  -H 'Content-Type: application/json' -d "{\"email\":\"$SEED\"}"
for i in $(seq 1 30); do
  COUNT="$(curl -s -G http://localhost:8025/api/v1/search --data-urlencode "query=to:$SEED" \
    | node -e 'console.log(JSON.parse(require("fs").readFileSync(0, "utf8")).messages_count)')"
  [ "${COUNT:-0}" -gt 0 ] && break
  sleep 1
done
ID="$(curl -s -G http://localhost:8025/api/v1/search --data-urlencode "query=to:$SEED" \
  | node -e 'console.log(JSON.parse(require("fs").readFileSync(0, "utf8")).messages[0].ID)')"
curl -s "http://localhost:8025/api/v1/message/$ID" | node -e '
const mail = JSON.parse(require("fs").readFileSync(0, "utf8"));
const link = /https?:\/\/\S+\?token=\S+/.exec(mail.Text)?.[0] ?? "";
const url = new URL(link);
console.log("the link points at", url.origin + url.pathname, "and carries a token of", url.searchParams.get("token")?.length, "characters");'
```

  Expected: `forgot: 202`; `the link points at http://localhost:8088/reset and carries a token of 43 characters`.

- [ ] **Step 8: Write the README.** `samples/notes-web/README.md`:

````markdown
# notes-web: the Angular sample

A small Angular 21 app over the [notes sample](../notes-api/): sign in, keep the session on reload, reset a forgotten
password, confirm an email and join a company from an invitation, and read and add the company's notes. It is the first
frontend of Auth-Core ([spec 0007](../../docs/superpowers/specs/0007-angular-sample.md)). Its sign-in pieces are written to be
copied into a product: see [`docs/integration/angular.md`](../../docs/integration/angular.md).

## What is here

```
src/app/auth/       auth.service.ts, auth.interceptor.ts, auth.guard.ts   (the pieces a product copies)
src/app/pages/      login, forgot, reset, verify, invite, notes           (the screens)
src/app/account-api.ts   the calls of the mail-link screens
src/app/texts.ts    every text the person sees, in one file (English)
e2e/                Playwright tests (Chromium and WebKit)
proxy.conf.json     ng serve: /auth and /api go to the stack on :8088
Dockerfile, Caddyfile, compose.yml   the image, the proxy and the overlay
```

Standalone components, signals and reactive forms; plain CSS, no UI library; no `zone.js`. The access token is held in
memory only; the refresh token is Auth-Core's `HttpOnly` cookie.

## Run it

With the development stack and the notes sample (see the top-level README for `.env` and `.secrets/`):

```bash
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f samples/notes-web/compose.yml \
  --env-file .env up -d --build
```

Open `http://localhost:8088` and sign in as the development user of your `.env`. The same app is on
`https://localhost:8443` with a certificate from Caddy's own authority: a browser warns, because it does not trust it. WebKit
(Safari's engine) needs that address: it keeps Auth-Core's `Secure` refresh cookie but does not send it back over plain HTTP.
Mails (reset, verification, invitation) are in the mail catcher at `http://localhost:8025`; their links open this app.

## Work on the screens

`ng serve` on `http://localhost:4200` with a proxy for `/auth` and `/api` to the stack on `:8088`, so the browser sees one
origin and the cookie works in Chrome. The mail links point at `:8088`; change the three `FrontendUrls` in
`compose.yml` if you want them to open the dev server. Under `ng serve` the nonce in `src/index.html` stays a literal
template and no Content-Security-Policy is set: that is the dev server, not a bug.

```bash
cd samples/notes-web
npm ci
npx ng serve
```

## Tests

```bash
cd samples/notes-web
npx ng test --watch=false      # unit tests: Vitest on jsdom, no Docker, no browser
npx ng build                   # the production build
```

The end-to-end tests run against the built app on `https://localhost:8443`, in Chromium and in WebKit (`iPhone 15`), with the
certificate accepted. They need Docker, Node 24 and Playwright's browsers (`npx playwright install chromium webkit` once):

```bash
scripts/e2e-web.sh        # starts the stack under its own compose project name, runs both projects, stops the stack
```

## Things to know

- The bar above the screens (`Try again shortly.`, `You don't have access to this.`, `Can't reach the server.`) has no timer:
  dismiss it, or it goes with the next sign-in.
- Other open tabs keep their token in memory until it expires (up to 10 minutes) after a sign-out in one tab; tabs are not
  synchronised.
- There is no PWA, no sign-up and no screen for company admins; see the deferred list of the spec.
````

- [ ] **Step 9: Tear down and check what is left to commit.**

```bash
export COMPOSE_PROJECT_NAME=auth-core-web-t8
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f samples/notes-web/compose.yml --env-file .env down -v
docker rm -f p8caddy >/dev/null 2>&1
git status --porcelain
```

  Leave `.env` and `.secrets/` in place for Tasks 9 to 11 (both are git-ignored; Task 11 removes them only if the marker files in
  the git directory say this plan made them). `git status` shows
  only the five files of this task under `samples/notes-web/` (plus what earlier tasks left uncommitted, if the orchestrator has
  not committed them yet).

- [ ] **Step 10: Hand over** - uncommitted; suggested message `feat(web): Caddy image, HTTP and HTTPS proxy, compose overlay and README for the Angular sample`.

### Task 9: Playwright, the script, and the first three test groups

**Files:**
- Create: `samples/notes-web/playwright.config.ts`, `samples/notes-web/e2e/tsconfig.json`, `e2e/support/env.ts`,
  `e2e/support/session.ts`, `e2e/01-sign-in.spec.ts`, `e2e/02-session.spec.ts`, `e2e/07-headers.spec.ts`,
  `scripts/e2e-web.sh`

**Interfaces:**
- Consumes: the stack of Task 8; the screens of Group B (their texts and `data-testid`s: `me-email`, `me-company`, `me-role`,
  `sign-out`, `notice-text`, `rules`); `.env` with `AUTH_DEV_SEED_EMAIL`, `AUTH_DEV_SEED_PASSWORD`,
  `AUTH_DEV_SEED_UNVERIFIED_EMAIL`, `AUTH_DEV_SEED_UNVERIFIED_PASSWORD`, `NOTES_DB_PASSWORD`.
- Produces: `scripts/e2e-web.sh`, which for each project in `E2E_PROJECTS` (default `chromium webkit`) starts a clean stack, seeds
  what the tests need, runs Playwright against `https://localhost:8443`, and stops the stack; and the environment the
  tests read (all `E2E_*`, below). In `e2e/support/env.ts`: `setting(name)`, `apiUrl()`, `mailpitUrl()`. In
  `e2e/support/session.ts`: `openLogin(page)`, `submitLogin(page, email, password)`, `signIn(page, email, password)`,
  `signOut(page)`. Task 10 adds `support/mail.ts` and `support/api.ts` and the other four groups.

**Choices (the spec leaves them open):**
- **One clean stack per project.** Some state cannot be shared by two runs: the seeded unverified user can be confirmed only
  once, and the mail limits are per address. So the script runs `down -v`, `up`, seed and `playwright test` once per project. The
  second start is faster than the first (the images are built).
- **The script seeds** what the tests need and prints no secret: one note by the admin (so that a viewer has something to
  read); the operator CLI queues two invitations in the development company, one for a `viewer` (group 4) and one for a `user`
  whose password the test sets and then resets (group 3, "its own invited user, never the seeded admin"). The CLI only queues
  the mail, the server sends it within a minute or two, so the script invites first and the tests wait for the mails.
- The tests run **one worker, in file order**, with `trace` off: a trace would write tokens and passwords of the run to disk.
- The mail link points at `:8088`; a test takes its `token` and opens the same path on the test origin (`baseURL`).
- Node's `fetch` talks to `http://localhost:8088` (the API, for accepting an invitation) and to Mailpit; only the browser uses
  HTTPS, with `ignoreHTTPSErrors`.

- [ ] **Step 1: Are the browsers there?** Playwright's Chromium and WebKit are a download of several hundred megabytes. They
  were fetched for the technical trial and are normally in the cache:

```bash
cd samples/notes-web
npx playwright install --dry-run chromium webkit 2>&1 | grep -i "install location\|browser:" | head
```

  Expected: each browser's `Install location` is a path that already exists (`ls` it). If one does not, **stop and ask the
  orchestrator**: `npx playwright install chromium webkit` downloads them (name them in the question: Playwright 1.58.2's
  Chromium and WebKit builds), and a download needs the owner's yes.

- [ ] **Step 2: The configuration.** `samples/notes-web/playwright.config.ts`:

```ts
import { defineConfig, devices } from '@playwright/test';

declare const process: { env: Record<string, string | undefined> };

// The tests run against the built app behind Caddy, on HTTPS: WebKit sends the Secure refresh cookie back over HTTPS only.
// The certificate comes from Caddy's own authority, which no browser trusts: ignoreHTTPSErrors.
export default defineConfig({
  testDir: './e2e',
  // One worker, in file order: the stack is shared, and so are the mail limits of an address.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list']],
  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'https://localhost:8443',
    ignoreHTTPSErrors: true,
    // No trace: it would write the tokens and passwords of the run to disk.
    trace: 'off',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'webkit', use: { ...devices['iPhone 15'] } },
  ],
});
```

  `samples/notes-web/e2e/tsconfig.json` (only for the editor and `tsc`: Playwright runs the tests without type-checking; there
  is no `@types/node` in this project, so the tests declare the little they use of `process`):

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ES2022",
    "moduleResolution": "bundler",
    "lib": ["ES2022", "DOM"],
    "strict": true,
    "noEmit": true,
    "skipLibCheck": true,
    "types": [],
    "isolatedModules": true
  },
  "include": ["./**/*.ts", "../playwright.config.ts"]
}
```

  `samples/notes-web/e2e/support/env.ts`:

```ts
declare const process: { env: Record<string, string | undefined> };

/** A setting that scripts/e2e-web.sh gives the tests. A missing one means the tests were not started by the script. */
export function setting(name: string): string {
  const value = process.env[name];
  if (value === undefined || value === '') {
    throw new Error(`${name} is not set: run the tests with scripts/e2e-web.sh`);
  }
  return value;
}

/** Auth-Core and the notes service over plain HTTP, for what the tests do without a browser. */
export function apiUrl(): string {
  return process.env['E2E_API_URL'] ?? 'http://localhost:8088';
}

export function mailpitUrl(): string {
  return process.env['E2E_MAILPIT_URL'] ?? 'http://localhost:8025';
}
```

  `samples/notes-web/e2e/support/session.ts`:

```ts
import { Page, expect } from '@playwright/test';

export async function openLogin(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
}

export async function submitLogin(page: Page, email: string, password: string): Promise<void> {
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password', { exact: true }).fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

/** Opens the sign-in, signs in, and waits for the notes screen with the person's header. */
export async function signIn(page: Page, email: string, password: string): Promise<void> {
  await openLogin(page);
  await submitLogin(page, email, password);
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
}

export async function signOut(page: Page): Promise<void> {
  await page.getByTestId('sign-out').click();
  await expect(page).toHaveURL(/\/login$/);
}
```

- [ ] **Step 3: Test group 1: sign in.** `samples/notes-web/e2e/01-sign-in.spec.ts` (the Goal sequence and a wrong password):

```ts
import { expect, test } from '@playwright/test';
import { setting } from './support/env';
import { openLogin, signIn, submitLogin } from './support/session';

const seed = () => ({ email: setting('E2E_SEED_EMAIL'), password: setting('E2E_SEED_PASSWORD') });

test('a page that needs a session sends an anonymous visitor to the sign-in, which comes back to it', async ({ page }) => {
  const { email, password } = seed();
  await page.goto('/notes');
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fnotes$/);
  await submitLogin(page, email, password);
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
});

test('an unknown path ends on the sign-in with /notes as the page to come back to', async ({ page }) => {
  await page.goto('/no/such/page');
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fnotes$/);
});

test('a wrong password shows the message and stays on the sign-in', async ({ page }) => {
  const { email } = seed();
  await openLogin(page);
  await submitLogin(page, email, 'not-the-password');
  await expect(page.getByText('Wrong email or password.')).toBeVisible();
  await expect(page).toHaveURL(/\/login$/);
});

test('the admin signs in, sees the header, the seeded note, and adds a note that is listed', async ({ page }) => {
  const { email, password } = seed();
  await signIn(page, email, password);
  await expect(page.getByTestId('me-company')).not.toBeEmpty();
  await expect(page.getByTestId('me-role')).toHaveText('admin');
  await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toBeVisible();

  const text = `a note written by the e2e test ${Date.now()}`;
  await page.getByLabel('New note').fill(text);
  await page.getByRole('button', { name: 'Add note' }).click();
  await expect(page.getByText(text)).toBeVisible();
  await expect(page.getByLabel('New note')).toHaveValue('');
});
```

- [ ] **Step 4: Test group 2: the session.** `samples/notes-web/e2e/02-session.spec.ts` (reload, sign-out, and where the token
  is and is not):

```ts
import { Page, expect, test } from '@playwright/test';
import { setting } from './support/env';
import { openLogin, signIn, signOut, submitLogin } from './support/session';

const seed = () => ({ email: setting('E2E_SEED_EMAIL'), password: setting('E2E_SEED_PASSWORD') });

async function expectTokenNowhere(page: Page, token: string): Promise<void> {
  const places = await page.evaluate(() =>
    JSON.stringify({
      local: { ...localStorage },
      session: { ...sessionStorage },
      cookie: document.cookie,
      url: location.href,
    }),
  );
  expect(places).not.toContain(token);
  // The refresh cookie is HttpOnly: script cannot read it either.
  expect(places).not.toContain('auth_rt');
}

test('a reload lands on the page that was open, signed in, with no sign-in screen in between', async ({ page }) => {
  const { email, password } = seed();
  await signIn(page, email, password);
  const visited: string[] = [];
  page.on('framenavigated', (frame) => {
    if (frame === page.mainFrame()) {
      visited.push(new URL(frame.url()).pathname);
    }
  });
  await page.reload();
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page).toHaveURL(/\/notes$/);
  expect(visited).not.toContain('/login');
});

test('after sign-out a reload shows the sign-in, and the notes ask for it again', async ({ page }) => {
  const { email, password } = seed();
  await signIn(page, email, password);
  await signOut(page);
  await page.reload();
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
  await page.goto('/notes');
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fnotes$/);
});

test('the access token is nowhere but in memory: not in storage, a cookie or the URL, after sign-in and after a refresh', async ({ page }) => {
  const { email, password } = seed();
  await openLogin(page);
  const loginAnswer = page.waitForResponse((r) => r.url().endsWith('/auth/login') && r.request().method() === 'POST');
  await submitLogin(page, email, password);
  const first = ((await (await loginAnswer).json()) as { access_token: string }).access_token;
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expectTokenNowhere(page, first);

  const refreshAnswer = page.waitForResponse((r) => r.url().endsWith('/auth/refresh') && r.request().method() === 'POST');
  await page.reload();
  const second = ((await (await refreshAnswer).json()) as { access_token: string }).access_token;
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expectTokenNowhere(page, second);
  await expectTokenNowhere(page, first);
});

test('the refresh cookie is HttpOnly, Secure, SameSite=Strict and for /auth only', async ({ page }) => {
  const { email, password } = seed();
  await signIn(page, email, password);
  const cookies = await page.context().cookies();
  const refresh = cookies.find((c) => c.name === 'auth_rt');
  expect(refresh).toBeDefined();
  expect(refresh).toMatchObject({ httpOnly: true, secure: true, sameSite: 'Strict', path: '/auth' });
});

test('signing out ends the session for good: the cookie is gone and the notes are not reachable by the old page', async ({ page }) => {
  const { email, password } = seed();
  await signIn(page, email, password);
  await signOut(page);
  const cookies = await page.context().cookies();
  expect(cookies.find((c) => c.name === 'auth_rt' && c.value !== '')).toBeUndefined();
  await page.goBack();
  // Going back to the notes screen asks for the session again and finds none.
  await expect(page).toHaveURL(/\/login(\?returnUrl=%2Fnotes)?$/);
});
```

- [ ] **Step 5: Test group 7: the headers, the nonce, the address bar.** `samples/notes-web/e2e/07-headers.spec.ts`:

```ts
import { expect, test } from '@playwright/test';
import { setting } from './support/env';
import { signIn, signOut } from './support/session';

const CSP =
  /^default-src 'self'; script-src 'self'; style-src 'self' 'nonce-([0-9a-f-]{36})'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'$/;

test('the app answers with the Content-Security-Policy and the other headers of the spec', async ({ page }) => {
  const response = await page.goto('/login');
  if (response === null) {
    throw new Error('no response for /login');
  }
  const headers = response.headers();
  expect(headers['content-security-policy']).toMatch(CSP);
  expect(headers['content-security-policy']).not.toContain('unsafe-inline');
  expect(headers['referrer-policy']).toBe('no-referrer');
  expect(headers['x-content-type-options']).toBe('nosniff');
  expect(headers['cache-control']).toBe('no-cache');
});

test('the nonce is new for every response and is the one in the page', async ({ page }) => {
  const nonceOf = (value: string | undefined) => CSP.exec(value ?? '')?.[1];
  const first = await page.goto('/login');
  const firstNonce = nonceOf(first?.headers()['content-security-policy']);
  expect(firstNonce).toBeDefined();
  await expect(page.locator('app-root')).toHaveAttribute('ngcspnonce', firstNonce as string);
  const second = await page.goto('/forgot');
  const secondNonce = nonceOf(second?.headers()['content-security-policy']);
  expect(secondNonce).toBeDefined();
  expect(secondNonce).not.toBe(firstNonce);
  await expect(page.locator('app-root')).toHaveAttribute('ngcspnonce', secondNonce as string);
});

test('every path that is index.html is revalidated, an unknown path and a missing .js file included', async ({ page }) => {
  for (const path of ['/', '/notes', '/no/such/page', '/missing.js', '/missing.css', '/index.html']) {
    const answer = await page.request.get(path);
    expect(answer.status(), path).toBe(200);
    expect(answer.headers()['cache-control'], path).toBe('no-cache');
    expect(await answer.text(), path).toContain('<app-root');
  }
});

test('the file server serves nothing outside the build', async ({ page }) => {
  for (const path of ['/Caddyfile', '/etc/passwd', '/.env', '/Dockerfile']) {
    const answer = await page.request.get(path);
    const text = await answer.text();
    expect(text, path).toContain('<app-root');
    expect(text, path).not.toContain('reverse_proxy');
    expect(text, path).not.toContain('root:');
  }
});

test('the headers of the app are not put on /auth and /api, which set their own', async ({ page }) => {
  for (const path of ['/auth/health', '/api/health']) {
    const answer = await page.request.get(path);
    expect(answer.status(), path).toBe(200);
    expect(answer.headers()['content-security-policy'], path).toBeUndefined();
    expect(answer.headers()['referrer-policy'], path).toBeUndefined();
  }
});

test('the style nonce works: every style element carries it, nothing is blocked, and the page has no inline script', async ({ page }) => {
  const violations: string[] = [];
  page.on('console', (message) => {
    if (/content security policy/i.test(message.text())) {
      violations.push(message.text());
    }
  });
  await page.addInitScript(() => {
    const seen: string[] = [];
    (window as unknown as { __csp: string[] }).__csp = seen;
    document.addEventListener('securitypolicyviolation', (event) => {
      seen.push(`${event.violatedDirective} ${event.blockedURI}`);
    });
  });
  const response = await page.goto('/login');
  const nonce = CSP.exec(response?.headers()['content-security-policy'] ?? '')?.[1];
  expect(nonce).toBeDefined();

  // Sign in and out: the screens, their styles and their calls all run under the policy.
  await signIn(page, setting('E2E_SEED_EMAIL'), setting('E2E_SEED_PASSWORD'));
  await signOut(page);

  const styles = await page.evaluate(() =>
    Array.from(document.querySelectorAll('style'), (style) => style.nonce || style.getAttribute('nonce')),
  );
  expect(styles.length).toBeGreaterThan(0);
  expect(new Set(styles)).toEqual(new Set([nonce]));
  expect(await page.evaluate(() => document.querySelectorAll('script:not([src])').length)).toBe(0);
  expect(await page.evaluate(() => (window as unknown as { __csp: string[] }).__csp)).toEqual([]);
  expect(violations).toEqual([]);
});

test.describe('a mail link leaves no token in the address bar once its screen is open', () => {
  const token = 'not-a-real-token-0123456789abcdefghijklmnopqrs';

  test('/reset', async ({ page }) => {
    await page.goto(`/reset?token=${token}`);
    await expect(page.getByRole('heading', { name: 'Choose a new password' })).toBeVisible();
    expect(new URL(page.url()).search).toBe('');
    expect(page.url()).not.toContain(token);
  });

  test('/verify', async ({ page }) => {
    await page.goto(`/verify?token=${token}`);
    await expect(page.getByText('This link has expired or was already used.')).toBeVisible();
    expect(new URL(page.url()).search).toBe('');
    expect(page.url()).not.toContain(token);
  });

  test('/invite', async ({ page }) => {
    await page.goto(`/invite?token=${token}`);
    await expect(page.getByText('This invitation has expired or was already used. Ask for a new one.')).toBeVisible();
    await expect(page.getByRole('link', { name: 'Ask for a new link' })).toHaveCount(0);
    expect(new URL(page.url()).search).toBe('');
    expect(page.url()).not.toContain(token);
  });
});
```

- [ ] **Step 6: The script.** `scripts/e2e-web.sh` (LF line endings; `.gitattributes` already makes `*.sh` LF):

```bash
#!/usr/bin/env bash
# Real-network end-to-end check of spec 0007 (the Angular sample "notes-web") against the compose stack with the notes
# sample and the web overlay: Auth-Core, PostgreSQL, the mail catcher (Mailpit), the notes service and Caddy. Playwright
# drives the built app in Chromium (desktop) and WebKit (iPhone 15) on https://localhost:8443, whose certificate comes from
# Caddy's own authority and is accepted by the tests. Auth-Core's mail links point at http://localhost:8088; a test opens the
# same path on the test origin.
#
# For each project (E2E_PROJECTS, default "chromium webkit") the script: removes the stack and its volumes, starts it clean
# (docker compose up -d --build), waits for it, seeds what the tests need, runs Playwright for that project, and at the end
# stops the stack (E2E_KEEP_STACK=1 keeps it). One clean stack per project, because some state is used up by a run: the
# seeded unverified user can be confirmed once, and the mail limits are per address.
#
# What it seeds (nothing printed): a note by the development admin; through the operator CLI two invitations in the
# development company, a viewer (test group 4) and a user (test group 3, who sets a password, forgets it and resets it).
# The CLI only queues the mail, the server sends it within a minute or two; the tests wait for the mails.
#
# Full sequence, from the repo root:
#   cp .env.example .env                  # then set real local values (git-ignored); NOTES_DB_PASSWORD and the unverified user too
#   MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh        # signing/encryption keys into .secrets/ (git-ignored)
#   scripts/e2e-web.sh
# Needs: node 24 (and `npm ci` in samples/notes-web, which the script runs when node_modules is missing), Playwright's
# Chromium and WebKit (npx playwright install chromium webkit, once), curl, docker compose, and the host ports 8088, 8443,
# 8080 and 8025 free. Reads AUTH_DEV_SEED_EMAIL, AUTH_DEV_SEED_PASSWORD, AUTH_DEV_SEED_UNVERIFIED_EMAIL,
# AUTH_DEV_SEED_UNVERIFIED_PASSWORD and NOTES_DB_PASSWORD from the repo-root .env (parsed, never sourced).
# Env: COMPOSE_PROJECT_NAME (default auth-core-web, not the "auth-core" of a development stack nor the notes e2e's),
# E2E_PROJECTS, E2E_KEEP_STACK, HTTP_URL, HTTPS_URL, MAILPIT_URL. Exits non-zero on the first failure; prints "PASS <project>"
# per project; never prints a password, a token, a cookie or a mail body.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
web="$root/samples/notes-web"
HTTP_URL="${HTTP_URL:-http://localhost:8088}"
HTTPS_URL="${HTTPS_URL:-https://localhost:8443}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
PROJECTS="${E2E_PROJECTS:-chromium webkit}"
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-web}"
compose=(docker compose -f "$root/deploy/docker-compose.yml" -f "$root/samples/notes-api/compose.yml" -f "$web/compose.yml" --env-file "$root/.env")

tmp="$(mktemp -d)"
cleanup() {
  if [[ "${E2E_KEEP_STACK:-0}" != "1" ]]; then "${compose[@]}" down -v >/dev/null 2>&1 || true; fi
  rm -rf "$tmp"
}
trap cleanup EXIT

fail() { echo "FAIL $*" >&2; exit 1; }
pass() { echo "PASS $*"; }

env_get() { # read KEY from .env without executing it; strips one pair of surrounding quotes
  local line
  line="$(grep -E "^$1=" "$root/.env" | tail -n1 || true)"
  line="${line#*=}"
  line="${line%$'\r'}"
  if [[ "$line" =~ ^\"(.*)\"$ || "$line" =~ ^\'(.*)\'$ ]]; then line="${BASH_REMATCH[1]}"; fi
  printf '%s' "$line"
}

[[ -f "$root/.env" ]] || fail "missing $root/.env (copy .env.example and set local values)"
[[ -d "$root/.secrets" ]] || fail "missing $root/.secrets (run: MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh)"
SEED_EMAIL="$(env_get AUTH_DEV_SEED_EMAIL)"
SEED_PASSWORD="$(env_get AUTH_DEV_SEED_PASSWORD)"
UNVERIFIED_EMAIL="$(env_get AUTH_DEV_SEED_UNVERIFIED_EMAIL)"
UNVERIFIED_PASSWORD="$(env_get AUTH_DEV_SEED_UNVERIFIED_PASSWORD)"
[[ -n "$SEED_EMAIL" && -n "$SEED_PASSWORD" ]] || fail "AUTH_DEV_SEED_EMAIL / AUTH_DEV_SEED_PASSWORD not set in .env"
[[ -n "$UNVERIFIED_EMAIL" && -n "$UNVERIFIED_PASSWORD" ]] \
  || fail "AUTH_DEV_SEED_UNVERIFIED_EMAIL / AUTH_DEV_SEED_UNVERIFIED_PASSWORD not set in .env (see .env.example)"
[[ -n "$(env_get NOTES_DB_PASSWORD)" ]] || fail "NOTES_DB_PASSWORD not set in .env (see .env.example)"
command -v node >/dev/null || fail "node is not on the PATH"
if [[ ! -d "$web/node_modules/@playwright/test" ]]; then
  echo "installing the packages of samples/notes-web (npm ci)..."
  (cd "$web" && npm ci > "$tmp/npm.log" 2>&1) || { tail -n 20 "$tmp/npm.log" >&2; fail "npm ci failed"; }
fi

wait_ok() { # wait_ok <url> <seconds> [curl option]: waits until the URL answers 200
  local i
  for i in $(seq 1 "$2"); do
    if [[ "$(curl -s ${3:-} -o /dev/null --max-time 3 -w '%{http_code}' "$1" || true)" == "200" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

json_field() { # json_field <name>: the string field <name> of the JSON on stdin (exit 1 when it is not a string)
  node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const v=JSON.parse(s)[process.argv[1]];if(typeof v!=="string")process.exit(1);process.stdout.write(v)})' "$1"
}

cli() { # cli <args...>: the operator CLI, in the service's own image; stdout and stderr are kept out of the log
  "${compose[@]}" run --rm -T --no-deps auth admin "$@" > "$tmp/cli.out" 2> "$tmp/cli.err" \
    || { cat "$tmp/cli.err" >&2; fail "the operator CLI failed: admin $1"; }
}

seed_stack() {
  local run="$RANDOM$RANDOM" code org token
  VIEWER_EMAIL="viewer-$run@e2e.test"
  RESETTER_EMAIL="resetter-$run@e2e.test"
  SEEDED_NOTE="Seeded by the e2e script, run $run"
  USER_PASSWORD="E2e-Passw0rd-$run"
  NEW_PASSWORD="E2e-N3w-Passw0rd-$run"

  # The development admin: a token (kept in a header file for curl, never printed), the company id, one note.
  E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD" \
    node -e 'console.log(JSON.stringify({email: process.env.E2E_SEED_EMAIL, password: process.env.E2E_SEED_PASSWORD}))' > "$tmp/login.json"
  curl -sS --max-time 20 -X POST "$HTTP_URL/auth/login" -H 'Content-Type: application/json' \
    --data-binary "@$tmp/login.json" -o "$tmp/login.out" || fail "seeding: the admin could not sign in"
  token="$(json_field access_token < "$tmp/login.out")" \
    || fail "seeding: the admin's sign-in gave no token (is the development user in .env the one the stack was started with?)"
  printf 'Authorization: Bearer %s\n' "$token" > "$tmp/admin.auth"
  token=""
  curl -sS --max-time 20 "$HTTP_URL/auth/me" -H "@$tmp/admin.auth" -o "$tmp/me.json" || fail "seeding: GET /auth/me failed"
  org="$(json_field org_id < "$tmp/me.json")" || fail "seeding: /auth/me gave no company"
  E2E_SEEDED_NOTE="$SEEDED_NOTE" node -e 'console.log(JSON.stringify({text: process.env.E2E_SEEDED_NOTE}))' > "$tmp/note.json"
  code="$(curl -sS --max-time 20 -o /dev/null -w '%{http_code}' -X POST "$HTTP_URL/api/notes" \
    -H 'Content-Type: application/json' -H "@$tmp/admin.auth" --data-binary "@$tmp/note.json")"
  [[ "$code" == "201" ]] || fail "seeding: adding the first note answered $code, not 201"

  # Two invitations through the operator CLI. The server sends the mails at its next poll.
  cli invite --org "$org" --email "$VIEWER_EMAIL" --role viewer
  cli invite --org "$org" --email "$RESETTER_EMAIL" --role user
  rm -f "$tmp/login.json" "$tmp/login.out" "$tmp/admin.auth" "$tmp/me.json" "$tmp/note.json"
}

for project in $PROJECTS; do
  echo "== $project: a clean stack (the first build takes a few minutes)..."
  "${compose[@]}" down -v > "$tmp/down.log" 2>&1 || true
  "${compose[@]}" up -d --build > "$tmp/up.log" 2>&1 || { cat "$tmp/up.log" >&2; fail "$project: docker compose up failed"; }
  wait_ok "$HTTP_URL/auth/health" 120 || fail "$project: $HTTP_URL/auth/health did not return 200 within 120s"
  wait_ok "$HTTP_URL/api/health" 60 || fail "$project: $HTTP_URL/api/health did not return 200 within 60s"
  wait_ok "$MAILPIT_URL/readyz" 60 || fail "$project: $MAILPIT_URL/readyz did not return 200 within 60s"
  wait_ok "$HTTPS_URL/login" 60 -k || fail "$project: $HTTPS_URL/login (the app over HTTPS) did not return 200 within 60s"
  seed_stack

  export E2E_BASE_URL="$HTTPS_URL" E2E_API_URL="$HTTP_URL" E2E_MAILPIT_URL="$MAILPIT_URL"
  export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
  export E2E_UNVERIFIED_EMAIL="$UNVERIFIED_EMAIL" E2E_UNVERIFIED_PASSWORD="$UNVERIFIED_PASSWORD"
  export E2E_VIEWER_EMAIL="$VIEWER_EMAIL" E2E_RESETTER_EMAIL="$RESETTER_EMAIL"
  export E2E_USER_PASSWORD="$USER_PASSWORD" E2E_NEW_PASSWORD="$NEW_PASSWORD" E2E_SEEDED_NOTE="$SEEDED_NOTE"
  # A failure message of Playwright can hold a mail link (the URL of a page.goto): its token is hidden on the way out.
  # (pipefail is on: the exit status is Playwright's.)
  (cd "$web" && npx playwright test --project="$project" 2>&1 | sed -E 's/token=[A-Za-z0-9_-]+/token=<hidden>/g') \
    || fail "$project: Playwright failed (E2E_KEEP_STACK=1 keeps the stack for a look)"
  pass "$project: every Playwright test passed on a clean stack"
done

echo "ALL PASS"
```

- [ ] **Step 7: Static checks.** Without the stack:

```bash
bash -n scripts/e2e-web.sh && echo "syntax ok"
cd samples/notes-web
node node_modules/typescript/bin/tsc -p e2e/tsconfig.json && echo "e2e types ok"
npx playwright test --list 2>&1 | tail -4
cd ../..
git ls-files --eol scripts/e2e-web.sh 2>/dev/null | head -1
grep -c $'\r' scripts/e2e-web.sh
```

  Expected: `syntax ok`; `e2e types ok` (no `tsc` output); `--list` ends with `Total: N tests in 3 files` (chromium and webkit
  both listed: `N` is twice the tests of the three files); `grep -c` prints `0` (no carriage return in the script).

- [ ] **Step 8: Run it.** Stop any other stack on the ports first (`docker ps`). First one project, then both:

```bash
# Only the result lines are shown: a failure message of Playwright may hold a mail link, which holds a token, and typed
# passwords must not reach a report. (The script hides tokens in what it prints; this filter is the second line of defence.)
FILTER='^(==|PASS|FAIL|ALL PASS)|passed|failed|skipped|^ +(ok|x|-) [0-9]+ \['
E2E_PROJECTS=chromium scripts/e2e-web.sh 2>&1 | grep -E "$FILTER"
scripts/e2e-web.sh 2>&1 | grep -E "$FILTER"
```

  The pipe hides the exit status: the run passed only if the last line is `ALL PASS`. To see why a test failed, send the whole
  output to a temporary file (`LOG="$(mktemp)"`; `scripts/e2e-web.sh > "$LOG" 2>&1`), read only the lines around the failure through
  `sed -E 's/token=[A-Za-z0-9_-]+/token=<hidden>/g'`, never print a typed password or a mail body, and remove the file. Traces stay
  off.

  Expected: `PASS chromium: every Playwright test passed on a clean stack`, then the same for `webkit`, then `ALL PASS`; Playwright
  lists every test of groups 1, 2 and 7 as passed in each project. The script fails fast if the stack does not come up or the
  seeding fails, with a message that names the step. A failure of a test: read the message, fix the app or the test, and say
  in the report which it was; do not loosen an assertion to make a failure go away. The seeding also queues two invitations whose
  mails are not read yet (Task 10 reads them).

- [ ] **Step 9: Hand over** - uncommitted. The orchestrator commits `samples/notes-web/playwright.config.ts`,
  `samples/notes-web/e2e/` and the script as `test(web): Playwright setup, e2e script, and the sign-in, session and header tests`, and
  after `git add scripts/e2e-web.sh` runs `git update-index --chmod=+x scripts/e2e-web.sh` (the mode must be `100755`).

### Task 10: Test groups 3 to 6: the mail flows and the answers

**Files:**
- Create: `samples/notes-web/e2e/support/mail.ts`, `e2e/support/api.ts`, `e2e/03-forgot.spec.ts`, `e2e/04-invite.spec.ts`,
  `e2e/05-verify.spec.ts`, `e2e/06-answers.spec.ts`

**Interfaces:**
- Consumes: Task 9 (`setting`, `apiUrl`, `mailpitUrl`, `signIn`, `openLogin`, `submitLogin`, the script's seeding and its
  `E2E_*` variables: `E2E_VIEWER_EMAIL`, `E2E_RESETTER_EMAIL`, `E2E_USER_PASSWORD`, `E2E_NEW_PASSWORD`, `E2E_UNVERIFIED_EMAIL`,
  `E2E_UNVERIFIED_PASSWORD`, `E2E_SEEDED_NOTE`), the screens of Group B.
- Produces: `waitForLink(to, kind, timeoutMs?): Promise<string>` (`kind` is `'reset' | 'verify' | 'invite'`; the answer is
  the link as a path with its query, `/reset?token=...`, to open on the test origin) and `acceptInviteByApi(invitePath,
  password): Promise<void>`.

**Choices:** Mailpit's API gives the mails of an address (`/api/v1/search?query=to:<address>`, newest first) and each mail
(`/api/v1/message/<id>`, with `Text` and `HTML`); the helper takes the newest mail of the kind (by the start of its subject:
"Reset your", "Confirm your email", "You are invited") and checks that the HTML part holds the same token as the text part.
Nothing it reads is printed. The invited resetter accepts its invitation through the API (a person with no account has nothing
to forget); everything else is done in the browser.

- [ ] **Step 1: The mail helper and the API helper.** `samples/notes-web/e2e/support/mail.ts`:

```ts
import { mailpitUrl } from './env';

export type LinkKind = 'reset' | 'verify' | 'invite';

const SUBJECT_STARTS: Record<LinkKind, string> = {
  reset: 'Reset your',
  verify: 'Confirm your email',
  invite: 'You are invited',
};

interface MailSummary {
  ID: string;
  Subject: string;
}

interface Mail {
  Text: string;
  HTML: string;
}

async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`the mail catcher answered ${response.status} for ${new URL(url).pathname}`);
  }
  return (await response.json()) as T;
}

/**
 * Waits for the newest mail of a kind to an address and returns its link as a path on the test origin. The link in the mail
 * points at http://localhost:8088; the tests run on https://localhost:8443, so the path and the token are taken over.
 * The result holds a token: never log it.
 */
export async function waitForLink(to: string, kind: LinkKind, timeoutMs = 150_000): Promise<string> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const link = await newestLink(to, kind);
    if (link !== null) {
      return link;
    }
    if (Date.now() > deadline) {
      throw new Error(`no ${kind} mail for ${to} within ${timeoutMs / 1000} seconds`);
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
}

async function newestLink(to: string, kind: LinkKind): Promise<string | null> {
  const found = await getJson<{ messages: MailSummary[] }>(
    `${mailpitUrl()}/api/v1/search?query=${encodeURIComponent(`to:${to}`)}`,
  );
  const summary = found.messages.find((message) => message.Subject.startsWith(SUBJECT_STARTS[kind]));
  if (summary === undefined) {
    return null;
  }
  const mail = await getJson<Mail>(`${mailpitUrl()}/api/v1/message/${summary.ID}`);
  const match = new RegExp(`https?://localhost:8088/${kind}\\?token=([A-Za-z0-9_-]+)`).exec(mail.Text);
  if (match === null) {
    throw new Error(`the ${kind} mail for ${to} holds no link of the form /${kind}?token=...`);
  }
  if (!mail.HTML.includes(match[1])) {
    throw new Error(`the HTML part of the ${kind} mail for ${to} does not hold the token of its text part`);
  }
  return `/${kind}?token=${match[1]}`;
}
```

  `samples/notes-web/e2e/support/api.ts`:

```ts
import { apiUrl } from './env';

/** Accepts an invitation by the API, without a browser: the account the test then forgets the password of. */
export async function acceptInviteByApi(invitePath: string, password: string): Promise<void> {
  const token = new URL(invitePath, 'http://placeholder.invalid').searchParams.get('token');
  if (token === null) {
    throw new Error('the invitation link holds no token');
  }
  const response = await fetch(`${apiUrl()}/auth/invites/accept`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token, password }),
  });
  if (response.status !== 204) {
    throw new Error(`accepting the invitation by the API answered ${response.status}, not 204`);
  }
}
```

- [ ] **Step 2: Test group 3: forgot and reset.** `samples/notes-web/e2e/03-forgot.spec.ts`:

```ts
import { expect, test } from '@playwright/test';
import { acceptInviteByApi } from './support/api';
import { setting } from './support/env';
import { waitForLink } from './support/mail';
import { submitLogin } from './support/session';

test('forgot, the link from the mail, a weak and then a good password, and a sign-in with it', async ({ page }) => {
  test.setTimeout(240_000);
  const email = setting('E2E_RESETTER_EMAIL');
  const oldPassword = setting('E2E_USER_PASSWORD');
  const newPassword = setting('E2E_NEW_PASSWORD');

  // The user is one the script invited, not the seeded admin: a person with no account has no password to forget.
  await acceptInviteByApi(await waitForLink(email, 'invite'), oldPassword);

  await page.goto('/forgot');
  await page.getByLabel('Email').fill(email);
  await page.getByRole('button', { name: 'Send the link' }).click();
  await expect(page.getByText('If an account exists for this address, we sent a link.')).toBeVisible();

  const link = await waitForLink(email, 'reset');
  await page.goto(link);
  await expect(page.getByRole('heading', { name: 'Choose a new password' })).toBeVisible();
  expect(new URL(page.url()).search).toBe('');

  // A weak password: the rules it breaks, one line each; the token is still good afterwards.
  await page.getByLabel('New password').fill('short');
  await page.getByLabel('Repeat the password').fill('short');
  await page.getByRole('button', { name: 'Change password' }).click();
  const rules = page.getByTestId('rules');
  await expect(rules).toContainText('The password is too short.');
  await expect(rules).toContainText('The password needs an uppercase letter.');
  await expect(rules).toContainText('The password needs a digit.');
  await expect(rules).not.toContainText('lowercase');

  await page.getByLabel('New password').fill(newPassword);
  await page.getByLabel('Repeat the password').fill(newPassword);
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page.getByText('Password changed.')).toBeVisible();
  await expect(page).toHaveURL(/\/reset$/);

  // No sign-in follows the reset: the person signs in with the new password.
  await page.getByRole('link', { name: 'Sign in.' }).click();
  await expect(page).toHaveURL(/\/login$/);
  await submitLogin(page, email, newPassword);
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);

  // The link is used up.
  await page.goto(link);
  await page.getByLabel('New password').fill(newPassword);
  await page.getByLabel('Repeat the password').fill(newPassword);
  await page.getByRole('button', { name: 'Change password' }).click();
  await expect(page.getByText('This link has expired or was already used.')).toBeVisible();
  await expect(page.getByRole('link', { name: 'Ask for a new link' })).toBeVisible();

  // The old password no longer works (in the same test: a separate one would depend on this one having run).
  await page.goto('/login');
  await submitLogin(page, email, oldPassword);
  await expect(page.getByText('Wrong email or password.')).toBeVisible();
});
```

- [ ] **Step 3: Test group 4: the invitation.** `samples/notes-web/e2e/04-invite.spec.ts`:

```ts
import { expect, test } from '@playwright/test';
import { setting } from './support/env';
import { waitForLink } from './support/mail';
import { submitLogin } from './support/session';

test('the viewer the operator invited accepts from the mail, signs in, reads the notes and cannot add one', async ({ page }) => {
  test.setTimeout(240_000);
  const email = setting('E2E_VIEWER_EMAIL');
  const password = setting('E2E_USER_PASSWORD');

  // The script asked the operator CLI to invite this address as a viewer; the server sends the mail within a minute or two.
  await page.goto(await waitForLink(email, 'invite'));
  await expect(page.getByTestId('invite-join')).toHaveText(/^Join .+ as viewer$/);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  await expect(page.getByLabel('Email')).not.toBeEditable();
  await expect(page.getByText(`This sets the password for ${email}.`)).toBeVisible();
  expect(new URL(page.url()).search).toBe('');

  await page.getByLabel('New password').fill(password);
  await page.getByLabel('Repeat the password').fill(password);
  await page.getByRole('button', { name: 'Join' }).click();

  // On the sign-in the email is filled in, and it was not put in the address bar.
  await expect(page).toHaveURL(/\/login$/);
  await expect(page.getByLabel('Email')).toHaveValue(email);
  expect(page.url()).not.toContain('@');
  expect(page.url()).not.toContain('%40');

  await submitLogin(page, email, password);
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page.getByTestId('me-role')).toHaveText('viewer');
  await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toBeVisible();
  await expect(page.getByLabel('New note')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Add note' })).toHaveCount(0);
});
```

- [ ] **Step 4: Test group 5: the unverified user.** `samples/notes-web/e2e/05-verify.spec.ts`:

```ts
import { expect, test } from '@playwright/test';
import { setting } from './support/env';
import { waitForLink } from './support/mail';
import { openLogin, submitLogin } from './support/session';

test('the unverified seeded user is told, sends the link again, confirms from the mail and signs in', async ({ page }) => {
  test.setTimeout(120_000);
  const email = setting('E2E_UNVERIFIED_EMAIL');
  const password = setting('E2E_UNVERIFIED_PASSWORD');

  await openLogin(page);
  await submitLogin(page, email, password);
  await expect(page.getByText('Your email address is not confirmed yet.')).toBeVisible();
  await expect(page).toHaveURL(/\/login$/);

  await page.getByRole('button', { name: 'Send the verification link again' }).click();
  await expect(page.getByText('If this address needs confirming, we sent a new link.')).toBeVisible();

  await page.goto(await waitForLink(email, 'verify'));
  await expect(page.getByText('Email confirmed.')).toBeVisible();
  expect(new URL(page.url()).search).toBe('');

  await page.getByRole('link', { name: 'Sign in.' }).click();
  await expect(page).toHaveURL(/\/login$/);
  await submitLogin(page, email, password);
  await expect(page).toHaveURL(/\/notes$/);
  await expect(page.getByTestId('me-email')).toHaveText(email);
});
```

- [ ] **Step 5: Test group 6: the answers, replaced with `page.route`.** `samples/notes-web/e2e/06-answers.spec.ts`. The calls of
  the page are answered by the test in place of the stack, in each of the four ways the interceptor's table has a row for
  that this suite can provoke. The routes are set before the sign-in, so that the first `/api/notes` of the notes screen is
  the one that is answered:

```ts
import { expect, test } from '@playwright/test';
import { setting } from './support/env';
import { openLogin, submitLogin } from './support/session';

const seed = () => ({ email: setting('E2E_SEED_EMAIL'), password: setting('E2E_SEED_PASSWORD') });

test('a 401 once on /api/notes leads to one refresh and the list', async ({ page }) => {
  const { email, password } = seed();
  let listCalls = 0;
  await page.route('**/api/notes', async (route) => {
    if (route.request().method() !== 'GET') {
      await route.continue();
      return;
    }
    listCalls += 1;
    if (listCalls === 1) {
      await route.fulfill({ status: 401, headers: { 'WWW-Authenticate': 'Bearer' }, body: '' });
    } else {
      await route.continue();
    }
  });
  await openLogin(page);
  const refreshes: string[] = [];
  page.on('request', (request) => {
    if (request.url().endsWith('/auth/refresh')) {
      refreshes.push(request.url());
    }
  });
  await submitLogin(page, email, password);
  await expect(page.getByText(setting('E2E_SEEDED_NOTE'))).toBeVisible();
  expect(listCalls).toBe(2);
  expect(refreshes).toHaveLength(1);
  await expect(page.getByTestId('notice')).toHaveCount(0);
});

test('a 401 from the refresh leads to /login?returnUrl=%2Fnotes', async ({ page }) => {
  const { email, password } = seed();
  await page.route('**/api/notes', (route) => route.fulfill({ status: 401, headers: { 'WWW-Authenticate': 'Bearer' }, body: '' }));
  await page.route('**/auth/refresh', (route) =>
    route.fulfill({ status: 401, contentType: 'application/json', body: JSON.stringify({ error: 'invalid_grant' }) }),
  );
  await openLogin(page);
  await submitLogin(page, email, password);
  await expect(page).toHaveURL(/\/login\?returnUrl=%2Fnotes$/);
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
});

test('a 503 auth_unavailable shows the bar and keeps the header', async ({ page }) => {
  const { email, password } = seed();
  await page.route('**/api/notes', (route) =>
    route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'auth_unavailable' }) }),
  );
  await openLogin(page);
  await submitLogin(page, email, password);
  await expect(page.getByTestId('notice-text')).toHaveText('Try again shortly.');
  await expect(page.getByTestId('me-email')).toHaveText(email);
  await expect(page).toHaveURL(/\/notes$/);
});

test('a 403 forbidden shows the message and does not refresh', async ({ page }) => {
  const { email, password } = seed();
  await page.route('**/api/notes', (route) =>
    route.fulfill({ status: 403, contentType: 'application/json', body: JSON.stringify({ error: 'forbidden' }) }),
  );
  await openLogin(page);
  const refreshes: string[] = [];
  page.on('request', (request) => {
    if (request.url().endsWith('/auth/refresh')) {
      refreshes.push(request.url());
    }
  });
  await submitLogin(page, email, password);
  await expect(page.getByTestId('notice-text')).toHaveText("You don't have access to this.");
  await expect(page.getByTestId('me-email')).toHaveText(email);
  expect(refreshes).toHaveLength(0);
});
```

- [ ] **Step 6: Static checks and the run.**

```bash
cd samples/notes-web
node node_modules/typescript/bin/tsc -p e2e/tsconfig.json && echo "e2e types ok"
npx playwright test --list 2>&1 | tail -3
grep -cP '\x5c\x5c' e2e/support/mail.ts
cd ../..
FILTER='^(==|PASS|FAIL|ALL PASS)|passed|failed|skipped|^ +(ok|x|-) [0-9]+ \['
E2E_PROJECTS=chromium scripts/e2e-web.sh 2>&1 | grep -E "$FILTER"
scripts/e2e-web.sh 2>&1 | grep -E "$FILTER"
```

  The pipe hides the exit status: a run passed only if its last line is `ALL PASS`; failures are read as in Task 9, step 8
  (a temporary log, tokens hidden by `sed`, nothing typed or mailed pasted into a report).

  Expected: `e2e types ok`; the list covers 7 files; the `grep` prints `1` (the `\\?` of the link pattern, as typed); then `PASS chromium ...`, `PASS webkit ...` and `ALL PASS`, with every test of the
  seven files passed in both projects. The tests of groups 3 and 4 wait for mails the server sends at its next poll, so a
  run takes several minutes. If a test of a mail flow fails on a text, compare it with `texts.ts` before changing the page.
  Never print a mail body to find out what is wrong: the link holds a token (read the subject, or the structure, of the
  Mailpit answer instead).

- [ ] **Step 7: Hand over** - uncommitted; suggested message `test(web): Playwright tests for reset, invitation, verification and the replaced answers`.

---

### Task 11: The integration guide, the acceptance map and the final gate

**Files:**
- Create: `docs/integration/angular.md`, `docs/superpowers/plans/0007-acceptance-map.md`

**Interfaces:**
- Consumes: every file and test of Tasks 1 to 10 (the guide points at the sample's files; the map names the tests by their file and
  title).
- Produces: the guide of the spec's "Integration guide" (seven steps, each pointing at the sample's files, naming no product, with the
  post-deployment phone checklist as step 7); the table from criteria to guards that the verifiers use (layer 2 of
  `docs/workflow.md`), the Review Focus table, and two sections the orchestrator fills in; the final gate of the slice.

- [ ] **Step 1: Write the guide.** `docs/integration/angular.md`. Every snippet is the sample's own code (shortened); every path in
  a code span exists in the sample:

````markdown
# Connecting an Angular app to Auth-Core

This guide takes an Angular 21 app from "no login" to "people sign in, keep the session when the page is reloaded, reset a
forgotten password, confirm their email and join a company from an invitation, and see only what their role allows", in seven
steps. It points at a small working app in this repository, the **notes** frontend in
[`samples/notes-web/`](../../samples/notes-web/), and every file it names exists there: when a step is unclear, read the file.

What you get: three files that you copy (a service, an interceptor and a guard) and the screens that the links in Auth-Core's mails
open. The access token lives in memory only; the refresh token is Auth-Core's `HttpOnly` cookie
([ADR 0004](../adr/0004-same-origin-cookie-refresh.md)), which the app never sees. The app does not decode the token: it asks
`GET /auth/me` who the person is and what they may do. Your backend checks the token itself
([`python-fastapi.md`](python-fastapi.md) is the guide for a Python one).

Needs: Angular 21 (standalone components; zoneless or not), `HttpClient`, the router, and Auth-Core mounted at `/auth`
([ADR 0002](../adr/0002-headless-rest-api.md)).

## 1. Put the app, `/auth` and `/api` on one origin

The refresh cookie is `Secure`, `SameSite=Strict` and set for the path `/auth`, so the browser sends it only to the origin that set
it. There is no CORS: the app, Auth-Core and your API are on one origin behind a proxy, and the app calls them with relative URLs
(`/auth/...`, `/api/...`).

The sample's proxy is Caddy: [`samples/notes-web/Caddyfile`](../../samples/notes-web/Caddyfile) sends `/auth/*` to Auth-Core,
`/api/*` to the product's service and every other path to the built app, with `index.html` as the answer to a path that is not a
file (the router owns those paths). The compose overlay that builds and runs it is
[`samples/notes-web/compose.yml`](../../samples/notes-web/compose.yml).

While you work on the screens, `ng serve` can stand in for the proxy:
[`samples/notes-web/proxy.conf.json`](../../samples/notes-web/proxy.conf.json) sends `/auth` and `/api` from the dev server to the
stack, so the browser still sees one origin.

**WebKit (Safari) and plain HTTP.** WebKit keeps a `Secure` cookie set over `http://localhost` but never sends it back, so on a
developer machine a session cannot survive a reload there. The sample's Caddyfile therefore also serves `https://localhost:8443`
with a certificate from Caddy's own authority; a browser warns about it, and the end-to-end tests accept it. In production you have
real HTTPS and this does not arise.

## 2. Copy `src/app/auth/` and register the initializer, the interceptor and the guard

Copy the three files of [`samples/notes-web/src/app/auth/`](../../samples/notes-web/src/app/auth/) into your app:
`auth.service.ts` (the token, the session, `login`, `logout`, `refresh`), `auth.interceptor.ts` and `auth.guard.ts`. They depend on
nothing else of the sample. Register them in your application config
([`app.config.ts`](../../samples/notes-web/src/app/app.config.ts)):

```ts
export const appConfig: ApplicationConfig = {
  providers: [
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor])),
    // Before the first route is resolved: is there a session to resume? (POST /auth/refresh with the cookie)
    provideAppInitializer(() => inject(AuthService).start()),
  ],
};
```

and put the guard on every route that needs a person
([`app.routes.ts`](../../samples/notes-web/src/app/app.routes.ts)):

```ts
{ path: 'notes', component: NotesPage, canActivate: [authGuard] },
```

How it behaves:

- **At start** the app calls `POST /auth/refresh` once. `200` keeps the token and asks `GET /auth/me`; `401` means nobody is
  signed in; anything else (or no network, or no answer within 10 seconds) also leaves nobody signed in, and the bar says the server
  cannot be reached.
- **A screen** reads `AuthService.me()` (email, company, roles, permissions) and `AuthService.token()` (a signal: `null` when nobody is
  signed in). Show a control only when `me().permissions` lists what it needs; the server still checks.
- **Sign-in** is `await auth.login(email, password)`, which answers `{ ok: true }` or `{ ok: false, failure }`; on success go to
  `safeReturnUrl(returnUrl)` (the guard file exports it: it follows only a path of your own app, never `//host`, `https://host` or
  a slash written as `%2f`). Give the result to `router.navigateByUrl`, never to `location.href` or `window.open`.
  See [`pages/login.ts`](../../samples/notes-web/src/app/pages/login.ts).
- **Sign-out** is `await auth.logout()` and then `/login`; the token is dropped whatever the server answers
  ([`pages/notes.ts`](../../samples/notes-web/src/app/pages/notes.ts)).
- There is no refresh ahead of expiry: a token is renewed only after a `401`, which costs one extra request about every 10 minutes.

## 3. Say which paths get the token

The token goes only to your app's own origin, and only to paths on two lists at the top of `auth.interceptor.ts`:

```ts
export const TOKEN_PATH_PREFIXES: readonly string[] = ['/api/', '/auth/org'];
export const TOKEN_PATHS: readonly string[] = ['/auth/me'];
```

Edit them to the paths of your own backend (`/api/` is the sample's). It never adds the token to another origin or to the other
`/auth` endpoints (`/auth/login`, `/auth/refresh`, the mail-link endpoints), and a path that only looks like one after `..` is
resolved does not count. [`src/app/auth/auth.interceptor.spec.ts`](../../samples/notes-web/src/app/auth/auth.interceptor.spec.ts)
shows the cases.

## 4. Build the screens for the mail links, and point Auth-Core at them

Auth-Core's mails carry a link to your app; you build the screens, it sends the mails. Three routes, and the three settings that say
where they are (`Auth:App:FrontendUrls:ResetPassword`, `VerifyEmail` and `AcceptInvite`; as environment variables
`Auth__App__FrontendUrls__ResetPassword` and so on, see the `auth` service in
[`compose.yml`](../../samples/notes-web/compose.yml)). Each link is that URL plus `?token=<43 characters>`. Outside Development the
URLs must be `https`.

| Route | The screen | Calls |
| --- | --- | --- |
| `/reset?token=` | new password, twice; the rules it did not meet; "link used up" | `POST /auth/password/reset` |
| `/verify?token=` | confirms on opening; a form to ask for a new link when it is used up | `POST /auth/email/verify`, `POST /auth/email/verify/request` |
| `/invite?token=` | "Join {company} as {role}" and the email; the password, twice | `POST /auth/invites/preview`, `POST /auth/invites/accept` |
| `/forgot` | email; always the same answer | `POST /auth/password/forgot` |

The screens are in [`src/app/pages/`](../../samples/notes-web/src/app/pages/); the calls are in
[`src/app/account-api.ts`](../../samples/notes-web/src/app/account-api.ts); the words are all in
[`src/app/texts.ts`](../../samples/notes-web/src/app/texts.ts), so that a product changes them (or translates them) in one place.

Three rules the sample follows, and you should too:

- **The token leaves the address bar at once.** A screen reads `token` from the URL once and removes it with `history.replaceState`
  ([`pages/token-from-url.ts`](../../samples/notes-web/src/app/pages/token-from-url.ts)), so it stays out of the browser's history and
  of what a person copies. The page that is served has `Referrer-Policy: no-referrer` (step 6), so the token is not sent on as a
  referrer either.
- **Password rules are Auth-Core's.** A `weak_password` answer names the rules not met (`too_short`, `requires_upper`,
  `requires_lower`, `requires_digit`); show one line each, and do not repeat the policy in your app.
- **Do not put an email in the URL.** After an invitation is accepted the sample opens `/login` with the address filled in, passed in
  the router's navigation state.

## 5. Handle the answers in the interceptor's table

The interceptor handles the answers to the requests that carry the token:

| Answer | Reaction |
| --- | --- |
| `401` | One refresh, then the request again, once. Requests that get `401` while a refresh runs wait for that refresh instead of starting another. |
| `401` from that refresh | The token is dropped; the person goes to `/login?returnUrl=<the current path>`. |
| `403 {"error":"forbidden"}` | The message "You don't have access to this." No refresh. |
| `403 {"error":"permissions_changed"}` | One refresh, so that the new token carries the current role, then the request again, once. The person's details are asked again too. |
| `503 {"error":"auth_unavailable"}` | A bar: "Try again shortly." The person stays signed in. |

The `403 permissions_changed` and `503 auth_unavailable` answers are what Auth-Core's company API and a product's backend (the Python
package, for one) answer; see the specs [0005](../superpowers/specs/0005-tenancy-and-rbac.md) and
[0006](../superpowers/specs/0006-python-consumer-package.md). The messages are drawn by the shell
([`src/app/app.ts`](../../samples/notes-web/src/app/app.ts)) from `AuthService.notice()`; the bar has no timer.

## 6. Set the headers on the app's responses

The app's own responses (not `/auth` and `/api`, which set theirs) carry:

| Header | Value |
| --- | --- |
| `Content-Security-Policy` | `default-src 'self'; script-src 'self'; style-src 'self' 'nonce-<n>'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'` |
| `Referrer-Policy` | `no-referrer`, because the mail links carry a token in the URL |
| `X-Content-Type-Options` | `nosniff` |
| `Cache-Control` | `no-cache` on every response that is `index.html`, including the answer to an unknown path; hashed `.js` and `.css` files may be cached |

`<n>` must be new for every response: Angular adds component styles as `<style>` elements while it runs, and they carry the nonce.
Three things make that work, all in the sample:

- `index.html` has `<app-root ngCspNonce="...">`, and the server writes the same nonce into that attribute and into the header
  ([`src/index.html`](../../samples/notes-web/src/index.html)). The sample's Caddy does it with its `templates` directive and the
  request's own id ([`Caddyfile`](../../samples/notes-web/Caddyfile)). Note the backticks in the template inside the attribute: the
  Angular build rewrites double quotes there.
- The build does not inline critical CSS, which would put an `onload` and a `<style>` into `index.html`
  (`optimization.styles.inlineCritical: false` in [`angular.json`](../../samples/notes-web/angular.json)), so `index.html` holds no
  inline script, style or event handler.
- `'self'` stays next to the nonce in `style-src`: the build's stylesheet is a file of the app. There is no `'unsafe-inline'`.

The test that checks all of it in a real browser is
[`e2e/07-headers.spec.ts`](../../samples/notes-web/e2e/07-headers.spec.ts).

## 7. Check on a real phone, once deployed

The sample's tests run WebKit with a phone's screen, not a phone. Once your app is deployed with a **real certificate**, check this
once on a real phone (iOS Safari and Android Chrome), because cookie handling differs between engines:

- [ ] Open the app and sign in. You land on the page you asked for (or the default page).
- [ ] Reload the page. You are still signed in, and the sign-in screen does not flash.
- [ ] Close the browser completely and open the app again. You are still signed in (the refresh cookie outlives the browser).
- [ ] Open a link from a reset mail (and an invitation, and a verification mail). The screen opens and the address bar does not show
  the token.
- [ ] Sign out. A reload shows the sign-in screen.
- [ ] Wait out the access token (10 minutes) with the page open, then do something that calls your API. You stay signed in: one
  refresh happens and the request goes through.
- [ ] **On iOS, also from the home screen:** add the app to the home screen, open it from there, and sign in, reload and sign out. An
  app opened from the home screen can have cookies of its own on iOS.

Known limits, in the spec's words: another open tab keeps its token until it expires (up to 10 minutes) after a sign-out in one tab,
because tabs are not synchronised; and a token in memory can be read by script running in the page, which the
`Content-Security-Policy` of step 6 limits to your own files.
````

- [ ] **Step 2: Check the guide** (criterion 9): seven steps, every link and every path in a code span resolves, no product is named.

```bash
grep -c '^## [1-7]\. ' docs/integration/angular.md
(cd docs/integration && grep -o '](\.\./[^)#]*' angular.md | sed 's/^](//' | sort -u | while read -r p; do test -e "$p" || echo "MISSING $p"; done)
grep -o '`[^` ]*`' docs/integration/angular.md | tr -d '`' | grep -E '^(samples/|scripts/|docs/|src/|e2e/|Caddyfile|compose\.yml|Dockerfile|proxy\.conf\.json|angular\.json|playwright\.config\.ts)' | grep -v '\*' | sort -u | while read -r p; do
  if [ -e "$p" ] || [ -e "samples/notes-web/$p" ]; then :; else echo "MISSING $p"; fi
done
grep -inE 'speech|stereo|investing' docs/integration/angular.md
grep -c 'samples/notes-web' docs/integration/angular.md
```

  Expected: `7`; no output from the next three commands (a path in a code span is checked from the repository root and from
  `samples/notes-web/`); a number above 10 from the last one. If a link is wrong, fix the guide, not the sample.

- [ ] **Step 3: Write the acceptance map.** `docs/superpowers/plans/0007-acceptance-map.md`. A test is written as
  `[file.spec.ts] its title`, entries separated by `;`, so that the check of the next step can find each one in its file:

````markdown
# Spec 0007 - acceptance map

Maps each acceptance criterion of
[spec 0007](../specs/0007-angular-sample.md) to the tests that guard it,
for verifier layer 2 ([`docs/workflow.md`](../../workflow.md#verification)).
The unit tests live next to the code in `samples/notes-web/src/` and run with `npx ng test --watch=false`, with no Docker; the
end-to-end tests are in `samples/notes-web/e2e/` and run through `scripts/e2e-web.sh` against the compose stack of
`deploy/docker-compose.yml`, `samples/notes-api/compose.yml` and `samples/notes-web/compose.yml`, on `https://localhost:8443`,
in Chromium and in WebKit. A test is named `[file] title`.

| # | Criterion | Guarding test(s) |
| - | --------- | ---------------- |
| 1 | The Goal sequence works and every Playwright test passes in Chromium and WebKit on a clean stack; `scripts/e2e-notes.sh` still passes with two overlay files, and the e2e scripts of specs 0001-0005 still pass on the stack without overlays | `scripts/e2e-web.sh` (all seven groups, once per project, each on a clean stack); the Goal sequence itself: [01-sign-in.spec.ts] a page that needs a session sends an anonymous visitor to the sign-in, which comes back to it; [01-sign-in.spec.ts] the admin signs in, sees the header, the seeded note, and adds a note that is listed; [02-session.spec.ts] a reload lands on the page that was open, signed in, with no sign-in screen in between; [02-session.spec.ts] after sign-out a reload shows the sign-in, and the notes ask for it again; regression: `scripts/e2e-notes.sh` with `deploy/docker-compose.yml` and `samples/notes-api/compose.yml`, and `scripts/e2e-login.sh`, `e2e-refresh.sh`, `e2e-lockout.sh`, `e2e-email.sh`, `e2e-tenancy.sh` on `deploy/docker-compose.yml` alone, all unedited (Task 11, step 7) |
| 2 | Each screen gives each answer in its table the text and action written there | the table "Criterion 2, row by row" below |
| 3 | The access token never appears in `localStorage`, `sessionStorage`, `document.cookie` or a URL (after sign-in and after a refresh) | [02-session.spec.ts] the access token is nowhere but in memory: not in storage, a cookie or the URL, after sign-in and after a refresh; [02-session.spec.ts] the refresh cookie is HttpOnly, Secure, SameSite=Strict and for /auth only; [auth.service.spec.ts] never writes the token to localStorage, sessionStorage or a cookie; the application code has no `localStorage`, `sessionStorage` or `document.cookie` (the `grep` of Task 2, step 6) |
| 4 | A reload with a valid refresh cookie lands on the page that was open, signed in, with no login screen in between | [02-session.spec.ts] a reload lands on the page that was open, signed in, with no sign-in screen in between; [app.config.spec.ts] resumes a session before the app is ready: one refresh, then /auth/me; [auth.service.spec.ts] 200: keeps the token, then asks /auth/me; the person is signed in |
| 5 | The interceptor sends the token only as written, refreshes once for any number of simultaneous 401s, retries a request at most once, and never refreshes on 403 forbidden | [auth.interceptor.spec.ts] is true for %s; [auth.interceptor.spec.ts] is false for %s; [auth.interceptor.spec.ts] sends it to %s; [auth.interceptor.spec.ts] sends it to the app origin when the URL is absolute; [auth.interceptor.spec.ts] does not send it to %s; [auth.interceptor.spec.ts] one refresh for several 401s at once, then every request again; [auth.interceptor.spec.ts] a 401 that arrives after another request renewed the token is sent again without a refresh; [auth.interceptor.spec.ts] does not send a second request, and no second refresh, when the retried request gets a 401; [auth.interceptor.spec.ts] permissions_changed on the retried request: no second refresh, no third request; [auth.interceptor.spec.ts] forbidden: the message, no refresh, the same token; [auth.service.spec.ts] one request for any number of callers that ask while it runs; [06-answers.spec.ts] a 401 once on /api/notes leads to one refresh and the list; [06-answers.spec.ts] a 403 forbidden shows the message and does not refresh |
| 6 | No `returnUrl` value leads off the app's origin | [auth.guard.spec.ts] replaces %j by /notes; [auth.guard.spec.ts] replaces a missing value by /notes; [auth.guard.spec.ts] keeps %s; [login.spec.ts] %s goes to %s; [01-sign-in.spec.ts] a page that needs a session sends an anonymous visitor to the sign-in, which comes back to it |
| 7 | The tokens from mail links are gone from the address bar after the screen opens, and the app's responses carry `Referrer-Policy: no-referrer` and the `Content-Security-Policy` above | [token-from-url.spec.ts] returns the token and takes it out of the address bar; [token-from-url.spec.ts] takes out a fragment too, and everything else in the query; [reset.spec.ts] takes the token out of the address bar when it opens; [verify.spec.ts] calls POST /auth/email/verify with the token on opening, and takes the token out of the address bar; [invite.spec.ts] asks for the preview with the token on opening, and takes the token out of the address bar; [07-headers.spec.ts] /reset; [07-headers.spec.ts] /verify; [07-headers.spec.ts] /invite; [03-forgot.spec.ts] forgot, the link from the mail, a weak and then a good password, and a sign-in with it; [04-invite.spec.ts] the viewer the operator invited accepts from the mail, signs in, reads the notes and cannot add one; [05-verify.spec.ts] the unverified seeded user is told, sends the link again, confirms from the mail and signs in; [07-headers.spec.ts] the app answers with the Content-Security-Policy and the other headers of the spec; [07-headers.spec.ts] the nonce is new for every response and is the one in the page; [07-headers.spec.ts] the style nonce works: every style element carries it, nothing is blocked, and the page has no inline script; [07-headers.spec.ts] every path that is index.html is revalidated, an unknown path and a missing .js file included; [07-headers.spec.ts] the file server serves nothing outside the build; [07-headers.spec.ts] the headers of the app are not put on /auth and /api, which set their own |
| 8 | `ng test` runs with no Docker | all of `samples/notes-web/src/**/*.spec.ts` (Vitest on jsdom; the gate of Task 11, step 6, runs them with no container running) |
| 9 | The integration guide covers the seven steps, and every file it points at exists in the sample | the commands of Task 11, step 2 (the seven headings; every link and every path in code spans resolves) |

## Criterion 2, row by row

| Screen / row | Guarding test(s) |
| ------------ | ---------------- |
| `/login` 401 `invalid_credentials` | [login.spec.ts] 401 invalid_credentials: "Wrong email or password."; [01-sign-in.spec.ts] a wrong password shows the message and stays on the sign-in |
| `/login` 403 `email_not_verified`: a message and the button | [login.spec.ts] shows a message and the button "Send the verification link again"; [login.spec.ts] the button asks for a new link for the address in the form, and says so; [login.spec.ts] 429 on the button: "Wait N seconds before asking again."; [login.spec.ts] any other answer on the button: "Something went wrong. Try again."; [05-verify.spec.ts] the unverified seeded user is told, sends the link again, confirms from the mail and signs in |
| `/login` 403 `no_membership` | [login.spec.ts] 403 no_membership: "Your account does not belong to a company." |
| `/login` 429 `too_many_attempts`, N rounded up | [login.spec.ts] 429 with retry_after_seconds %s: "Too many attempts. %s"; [texts.spec.ts] rounds the wait of a lockout up to whole minutes |
| `/login` success: `returnUrl`, else `/notes` | [login.spec.ts] %s goes to %s; [01-sign-in.spec.ts] a page that needs a session sends an anonymous visitor to the sign-in, which comes back to it |
| `/login` any other answer | [login.spec.ts] %s: "Something went wrong. Try again."; [login.spec.ts] no network: "Something went wrong. Try again." |
| `/login` email filled in after an invitation, in the navigation state | [login.spec.ts] fills in the email that an invitation passed in the navigation state; [invite.spec.ts] 204: goes to /login with the email in the navigation state, not in the URL; [04-invite.spec.ts] the viewer the operator invited accepts from the mail, signs in, reads the notes and cannot add one |
| `/forgot` 202: always the same answer | [forgot.spec.ts] 202 for %s: always the same sentence; [03-forgot.spec.ts] forgot, the link from the mail, a weak and then a good password, and a sign-in with it |
| `/forgot` 429 | [forgot.spec.ts] 429: "Wait N seconds before asking again."; [texts.spec.ts] gives the wait before another mail in seconds |
| `/forgot` any other answer | [forgot.spec.ts] %s: "Something went wrong. Try again." |
| `/reset` `weak_password`: the rules, one line each | [reset.spec.ts] weak_password: the rules not met, one line each; [reset.spec.ts] weak_password: a rule it has no text for is still a line, and the next try replaces the lines; [03-forgot.spec.ts] forgot, the link from the mail, a weak and then a good password, and a sign-in with it |
| `/reset` `invalid_token`: the sentence and a link to `/forgot` | [reset.spec.ts] invalid_token: "This link has expired or was already used." with a link to /forgot; [reset.spec.ts] (%s) is an invalid link, with no request; [03-forgot.spec.ts] forgot, the link from the mail, a weak and then a good password, and a sign-in with it |
| `/reset` 204: "Password changed. Sign in." and a link, no sign-in follows | [reset.spec.ts] 204: "Password changed. Sign in." with a link to /login, and nobody is signed in; [03-forgot.spec.ts] forgot, the link from the mail, a weak and then a good password, and a sign-in with it; that test ends by checking that the old password no longer works |
| `/reset` two passwords, other answers | [reset.spec.ts] two different passwords: a message, and no request; [reset.spec.ts] %s: "Something went wrong. Try again." |
| `/verify` calls the endpoint on opening; 204: "Email confirmed." and a link | [verify.spec.ts] calls POST /auth/email/verify with the token on opening, and takes the token out of the address bar; [verify.spec.ts] 204: "Email confirmed." with a link to /login; [05-verify.spec.ts] the unverified seeded user is told, sends the link again, confirms from the mail and signs in |
| `/verify` `invalid_token`: the sentence and a form to send a new one | [verify.spec.ts] says so and offers a form to send a new link; [verify.spec.ts] the form asks for a new link for the address typed, and says so; [verify.spec.ts] 429 on the form: "Wait N seconds before asking again."; [verify.spec.ts] sends nothing for an empty address; [verify.spec.ts] a link with the query %j is an invalid link, with no request |
| `/verify` any other answer | [verify.spec.ts] %s: "Something went wrong. Try again." |
| `/invite` the preview: "Join {org_name} as {role}", the email, not editable; the line "This sets the password for {email}." | [invite.spec.ts] shows "Join {company} as {role}", the email (not editable) and who the password is for; [04-invite.spec.ts] the viewer the operator invited accepts from the mail, signs in, reads the notes and cannot add one |
| `/invite` accept: 204 goes to `/login` with the email | [invite.spec.ts] 204: goes to /login with the email in the navigation state, not in the URL; [invite.spec.ts] sends the password exactly as typed |
| `/invite` `409 already_member` | [invite.spec.ts] 409 already_member on accept: "This account already belongs to a company."; [invite.spec.ts] 409 already_member on the preview: the same sentence, and no form |
| `/invite` `invalid_token`: "This invitation has expired or was already used. Ask for a new one.", no link; `weak_password`; other answers | [invite.spec.ts] invalid_token on the preview: "This invitation has expired or was already used. Ask for a new one." and no link to /forgot; [invite.spec.ts] invalid_token on accept: the same sentence, and no link; [texts.spec.ts] has one sentence for a used-up link and another for a used-up invitation; [07-headers.spec.ts] /invite; [invite.spec.ts] weak_password: the rules not met, one line each; [invite.spec.ts] %s on the preview: "Something went wrong. Try again."; [invite.spec.ts] 500 on accept: "Something went wrong. Try again.", and the form stays; [invite.spec.ts] two different passwords: a message, and no request; [invite.spec.ts] a link with the query %j is an invalid invitation, with no request |
| `/notes` guarded; `/` and an unknown route go to `/notes` | [app.routes.spec.ts] an anonymous person who asks for %s lands on /login?returnUrl=%%2Fnotes; [app.routes.spec.ts] a signed-in person who asks for %s lands on /notes; [app.routes.spec.ts] %s needs no token; [auth.guard.spec.ts] sends an anonymous person to /login with the page they asked for; [auth.guard.spec.ts] lets a person through when a token is held; [01-sign-in.spec.ts] an unknown path ends on the sign-in with /notes as the page to come back to |
| `/notes` header: email, company, role from `/auth/me`; "Sign out" | [notes.spec.ts] shows the email, company and role from /auth/me, and "Sign out"; [01-sign-in.spec.ts] the admin signs in, sees the header, the seeded note, and adds a note that is listed |
| `/notes` the notes, newest first; the add form only with `notes:write` | [notes.spec.ts] lists the notes in the order the service sends them, newest first; [notes.spec.ts] is shown when /auth/me lists notes:write; [notes.spec.ts] is not shown to a viewer, who still reads the notes; [notes.spec.ts] adds a note: the text is sent, the new note is on top and the field is empty again; [04-invite.spec.ts] the viewer the operator invited accepts from the mail, signs in, reads the notes and cannot add one |
| Session at start: 200 / 401 / anything else or no network | [auth.service.spec.ts] 200: keeps the token, then asks /auth/me; the person is signed in; [auth.service.spec.ts] 401: the person is anonymous and nothing is shown; [auth.service.spec.ts] anything else: anonymous, and the bar says the server cannot be reached; [auth.service.spec.ts] no network: anonymous, and the bar says the server cannot be reached; [app.spec.ts] shows the bar for the notice %s |
| Interceptor: `401` | [auth.interceptor.spec.ts] renews the token once and sends the request again with it; [auth.interceptor.spec.ts] one refresh for several 401s at once, then every request again |
| Interceptor: `401` from that refresh | [auth.interceptor.spec.ts] 401 from the refresh: the token is dropped and the person goes to /login with the page they were on; [06-answers.spec.ts] a 401 from the refresh leads to /login?returnUrl=%2Fnotes |
| Interceptor: `403 forbidden` | [auth.interceptor.spec.ts] forbidden: the message, no refresh, the same token; [06-answers.spec.ts] a 403 forbidden shows the message and does not refresh |
| Interceptor: `403 permissions_changed` | [auth.interceptor.spec.ts] permissions_changed: one refresh, the request again once, and the person asked for again; [auth.interceptor.spec.ts] permissions_changed on /auth/me itself does not ask /auth/me again |
| Interceptor: `503 auth_unavailable` | [auth.interceptor.spec.ts] auth_unavailable: the bar, and the person stays signed in; [06-answers.spec.ts] a 503 auth_unavailable shows the bar and keeps the header; [notes.spec.ts] a list that cannot be loaded keeps the header and says so |
| Guard: a token is held, else `/login?returnUrl=` | [auth.guard.spec.ts] sends an anonymous person to /login with the page they asked for; [auth.guard.spec.ts] sends a person to /login again once the token is dropped |
| Sign out: logout, token dropped, `/login`, whatever the answer | [notes.spec.ts] calls POST /auth/logout, drops the token and goes to /login after %s; [notes.spec.ts] goes to /login when the network fails; [auth.service.spec.ts] drops the token and the person after %s; [auth.service.spec.ts] drops the token when the network fails; [02-session.spec.ts] signing out ends the session for good: the cookie is gone and the notes are not reachable by the old page |

## How the tests run

- `cd samples/notes-web && npx ng test --watch=false` - the unit tests (Vitest on jsdom, no Docker, no browser).
- `scripts/e2e-web.sh` - the Playwright tests against a clean stack, once for Chromium and once for WebKit; it needs Docker, the
  images of the stack, Node 24 and Playwright's browsers.
- The tests that read mail (`03`, `04`, `05`) wait for the mails the server sends at its next poll, up to 150 seconds each.

## Review Focus (plan 0007)

| # | Failure mode | Guarding test(s) |
| - | ------------ | ---------------- |
| 1 | A mail link opened twice, or with no token, an empty one or another parameter: the "link used up" screen, no request without a token, never a blank page | [reset.spec.ts] (%s) is an invalid link, with no request; [verify.spec.ts] a link with the query %j is an invalid link, with no request; [invite.spec.ts] a link with the query %j is an invalid invitation, with no request; [03-forgot.spec.ts] forgot, the link from the mail, a weak and then a good password, and a sign-in with it |
| 2 | A `returnUrl` that is more than the four values of the spec: a control character (a tab inside `//`), a backslash inside the path, a very long one, a missing one | [auth.guard.spec.ts] replaces %j by /notes; [auth.guard.spec.ts] replaces a missing value by /notes; [login.spec.ts] %s goes to %s |
| 3 | The server answers something the contract does not say: a page of HTML from a proxy, a `200` with no token, nothing at all | [auth.service.spec.ts] a 200 without a token is not a session; [auth.service.spec.ts] a refresh that never answers is given up after ten seconds; [auth.service.spec.ts] anything else: anonymous, and the bar says the server cannot be reached; [auth.interceptor.spec.ts] pass through untouched: a 404, a 500 with a page of HTML, a 503 that is not auth_unavailable; [account-api.spec.ts] a preview that is not what the contract says is a failure, not a half-filled screen; [notes.spec.ts] a list that cannot be loaded keeps the header and says so; [notes.spec.ts] a 200 that is not a list is the same failure; [auth.service.spec.ts] a /auth/me that never answers is given up after ten seconds too, with the bar; [auth.service.spec.ts] a 200 from /auth/me that is %s is not a person: no crash, no details, the bar; [reset.spec.ts] a rule named like a property of every object is a line with the fallback text, never a function |
| 4 | A note that is only spaces, has 1001 characters, or is HTML | [notes.spec.ts] does not send a note of only spaces or an empty one; [notes.spec.ts] does not send a note of more than 1000 characters; [notes.spec.ts] sends a note of exactly 1000 characters; [notes.spec.ts] shows a note as text, never as HTML; [notes.spec.ts] a note that cannot be saved says so, and keeps what was typed |
| 5 | A password with spaces in it, an email with spaces around it, and a form sent twice | [login.spec.ts] trims the email and sends the password exactly as typed; [login.spec.ts] sends one request for a double click or a double Enter; [reset.spec.ts] sends the token it read and the new password exactly as typed; [reset.spec.ts] sends one request for a double submit; [invite.spec.ts] sends the password exactly as typed; [invite.spec.ts] sends one request for a double submit; [notes.spec.ts] sends one request for a double submit; [forgot.spec.ts] sends nothing for an empty email, and one request for a double submit; [login.spec.ts] a double click on the button sends one request |

## Contract sentences that are not acceptance criteria

| Sentence | Guarding test or check |
| -------- | ---------------------- |
| The access token is held in memory only, in a signal of `AuthService` | [auth.service.spec.ts] never writes the token to localStorage, sessionStorage or a cookie |
| The session ends for good when the person signs out, even with an answer on its way | [auth.service.spec.ts] an answer that was on its way when the person signed out does not bring the session back |
| There is no refresh ahead of expiry: a token is renewed only after a `401` | every test of `auth.interceptor.spec.ts` ends with `ctrl.verify()` (no request that the table does not allow); [auth.interceptor.spec.ts] pass through untouched: a 404, a 500 with a page of HTML, a 503 that is not auth_unavailable |
| All texts are English and live in `src/app/texts.ts` | [texts.spec.ts] is plain English text: printable ASCII, no markup |
| Password rules are checked by Auth-Core only; the screens show its `rules` | [reset.spec.ts] weak_password: the rules not met, one line each; [account-api.spec.ts] answer %s %j is the failure %j |
| The guard lets a route through only when a token is held | [auth.guard.spec.ts] lets a person through when a token is held |
| Components are standalone, signals and reactive forms, no UI library, no `zone.js` | Task 1, step 4 (`npm ls --depth=0`; no `zone.js` in `package-lock.json`) |
| The overlay replaces the `caddy` service under its own image; the base files stay unchanged | Task 8, step 5 (`image: notes-web-caddy:local`; one mount of the Caddyfile; `git diff` of `deploy/docker-compose.yml` and `samples/notes-api` is empty) |
| The same routes on both listeners; `tls internal` works in the container | Task 8, steps 2 and 5; every test of `e2e/` runs on `https://localhost:8443` |
| The mail links point at `http://localhost:8088/reset`, `/verify` and `/invite` | Task 8, step 7; [03-forgot.spec.ts] forgot, the link from the mail, a weak and then a good password, and a sign-in with it; [04-invite.spec.ts] the viewer the operator invited accepts from the mail, signs in, reads the notes and cannot add one; [05-verify.spec.ts] the unverified seeded user is told, sends the link again, confirms from the mail and signs in |
| `index.html` holds no inline script, style or event handler | Task 1, step 8 (the check of the build's output); [07-headers.spec.ts] the style nonce works: every style element carries it, nothing is blocked, and the page has no inline script |
| The development server proxies `/auth` and `/api` to `:8088` | Task 11, step 5 |
| Nothing secret in the built files | Task 11, step 6 |
| Other open tabs keep their token until it expires; tabs are not synchronised | not tested (a stated limit of the spec); named in `samples/notes-web/README.md` and in the guide, step 7 |

## Plan-vs-implementation notes

(Filled in by the orchestrator after implementation: every name or behaviour that differed from the plan, per task.)

## Local verification log

(Filled in by the orchestrator after the three verifiers have run.)
````

- [ ] **Step 4: Check that every test named in the map exists, and every spec file is in the map.** From the repository root:

```bash
node -e '
const fs = require("fs"), path = require("path");
const map = fs.readFileSync("docs/superpowers/plans/0007-acceptance-map.md", "utf8");
const files = new Map();
const walk = (dir) => {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (["node_modules", "dist", ".angular"].includes(entry.name)) continue;
    const p = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(p);
    else if (entry.name.endsWith(".spec.ts")) files.set(entry.name, fs.readFileSync(p, "utf8"));
  }
};
walk("samples/notes-web/src");
walk("samples/notes-web/e2e");
let checked = 0, bad = 0;
const used = new Set();
for (const match of map.matchAll(/\[([\w.-]+\.spec\.ts)\] ([^;|]+)/g)) {
  const file = match[1], title = match[2].trim();
  checked++;
  used.add(file);
  if (!files.has(file) || !files.get(file).includes(title)) { console.log("MISSING", file, "|", title); bad++; }
}
const unmapped = [...files.keys()].filter((f) => !used.has(f));
console.log("checked", checked, "entries; missing", bad, "; spec files not in the map:", unmapped.join(", ") || "none");
  process.exit(bad > 0 || unmapped.length > 0 || checked === 0 ? 1 : 0);'
echo "map check exit status: $?"
```

  Expected: `checked <N> entries; missing 0 ; spec files not in the map: none` and `map check exit status: 0` (`<N>` is the number of
  `[file] title` entries of the map; it is not fixed: only a missing title or a spec file that the map never names fails the check). A
  name that moved or was renamed during the work is fixed in the map, not in the test. (`%%2Fnotes` in the routes title is the way `it.each` is told to print a `%`; the map writes it
  the same way.)

- [ ] **Step 5: The development server.** The scope names a development server with a proxy. With the stack up, `ng serve` on
  `http://localhost:4200` must reach `/auth` and `/api` through `proxy.conf.json`. Bring the stack up first:

```bash
export COMPOSE_PROJECT_NAME=auth-core-web-t11
C="docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f samples/notes-web/compose.yml --env-file .env"
$C up -d --build 2>&1 | tail -2
for i in $(seq 1 60); do [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:8088/auth/health)" = 200 ] && break; sleep 2; done
```

  Then start the dev server as a **background command of the shell tool** (its `run_in_background` option: one command, in its own
  call, so that it keeps running while the next ones run), from `samples/notes-web`:

```bash
cd samples/notes-web && npx ng serve --port 4200
```

  and, in separate calls, wait for it and look through it:

```bash
for i in $(seq 1 60); do [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:4200/login)" = 200 ] && break; sleep 2; done
curl -s http://localhost:4200/auth/health; echo
curl -s -o /dev/null -w 'api through the dev server, no token: %{http_code}\n' http://localhost:4200/api/notes
curl -s -o /dev/null -w 'the page: %{http_code}\n' http://localhost:4200/login
```

  Expected: `Healthy` (Auth-Core through the dev server's proxy); `api through the dev server, no token: 401`; `the page: 200`.
  Then **stop the dev server explicitly**: end that background command with the shell tool's own facility for stopping a
  background command (do not look for the process with `ps`, and do not leave it running when the task ends: it holds port 4200
  and a watcher). Check that `curl -s -o /dev/null -w '%{http_code}' http://localhost:4200/login` now prints `000`, then stop
  the stack:

```bash
$C down -v
```

  (Nothing here needs a browser: the screens in a browser are the business of the Playwright tests.)

- [ ] **Step 6: The gate** (unit tests and build with **no container running**, so that "no Docker" is true; then what the diff must
  not contain):

```bash
docker ps --format '{{.Names}}' | head
cd samples/notes-web
npx ng test --watch=false 2>&1 | grep -v allow-scripts | tail -12
npx ng build 2>&1 | grep -v allow-scripts | tail -4
node -e '
const fs = require("fs"), path = require("path");
const env = fs.readFileSync("../../.env", "utf8");
const secrets = [];
for (const line of env.split(/\r?\n/)) {
  const m = /^(POSTGRES_PASSWORD|AUTH_DEV_SEED_EMAIL|AUTH_DEV_SEED_PASSWORD|AUTH_DEV_SEED_UNVERIFIED_EMAIL|AUTH_DEV_SEED_UNVERIFIED_PASSWORD|NOTES_DB_PASSWORD)=(.*)$/.exec(line);
  if (m && m[2].length >= 6) secrets.push(m[2].replace(/^["\x27]|["\x27]$/g, ""));
}
let hits = 0, scanned = 0;
const walk = (dir) => { for (const e of fs.readdirSync(dir, { withFileTypes: true })) { const p = path.join(dir, e.name); if (e.isDirectory()) walk(p); else { const t = fs.readFileSync(p, "latin1"); scanned++; for (const s of secrets) if (t.includes(s)) { hits++; console.log("SECRET VALUE IN", p); } if (/Bearer [A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}/.test(t)) { hits++; console.log("A TOKEN IN", p); } } } };
walk("dist/notes-web/browser");
console.log("scanned", scanned, "files of the build,", secrets.length, "values of .env, hits:", hits);'
cd ../..
grep -rInP --exclude-dir=node_modules --exclude-dir=dist --exclude-dir=.angular --exclude=package-lock.json '\x5c[uU][0-9a-fA-F]{4}|[^\x00-\x7F]' samples/notes-web scripts/e2e-web.sh docs/integration/angular.md | head
bash -n scripts/e2e-web.sh
git ls-files -s scripts/e2e-web.sh
git diff --stat HEAD -- deploy/docker-compose.yml src tests samples/notes-api clients .env.example
git status --porcelain
```

  Expected: `docker ps` shows no container of this stack (stop any that run: the unit tests and the build need none); all unit tests
  pass; the build completes; `scanned N files of the build, 6 values of .env, hits: 0` (no password and no token in the built files;
  the values are never printed); no output from the `grep` (only ASCII, no backslash-u escape written out); no output from `bash -n`;
  mode `100755` for the script (after the orchestrator's `git update-index --chmod=+x`); no output from `git diff --stat` (nothing
  under the base compose file, `src/`, `tests/`, the notes sample, `clients/` or `.env.example` changed); and `git status
  --porcelain` listing only this slice's files: `samples/notes-web/`, `scripts/e2e-web.sh`, `docs/integration/angular.md`,
  `docs/superpowers/plans/0007-*` and the spec (no `.env`, no `.secrets/`, no `node_modules`, no `dist`, no `test-results`).

- [ ] **Step 7: The end-to-end run and the regression pass.** Docker, a compose project name of its own for each run, the ports
  free. Make `.env` and `.secrets/` first if they are missing (the same block as Task 8, step 5: it keeps what exists, adds
  the variables that are missing, and marks what it made), and put the `python3` shim first on the `PATH` for the notes script
  (the real interpreter is `C:/p6v/Scripts/python.exe`). Run the end-to-end scripts with their output filtered as in the note
  below the block: a failure message of Playwright can hold a mail link, which holds a token.

```bash
mkdir -p /tmp/shim && printf '#!/bin/sh\nexec C:/p6v/Scripts/python.exe "$@"\n' > /tmp/shim/python3 && chmod +x /tmp/shim/python3
export PATH="/tmp/shim:$PATH"
GD="$(git rev-parse --git-dir)"
[ -f .env ] || { cp .env.example .env; : > "$GD/web-made-env"; }
grep -q '^NOTES_DB_PASSWORD=' .env || printf '\nNOTES_DB_PASSWORD=local-%s\n' "$RANDOM$RANDOM" >> .env
grep -q '^AUTH_DEV_SEED_UNVERIFIED_EMAIL=' .env \
  || printf '\nAUTH_DEV_SEED_UNVERIFIED_EMAIL=new@example.com\nAUTH_DEV_SEED_UNVERIFIED_PASSWORD=Dev-Unverified-Passw0rd\n' >> .env
[ -d .secrets ] || { MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh; : > "$GD/web-made-secrets"; }
# 1. this slice: both browsers, a clean stack each (the output is filtered: see the note below)
scripts/e2e-web.sh 2>&1 | sed -E 's/token=[A-Za-z0-9_-]+/token=<hidden>/g' | grep -E '^(==|PASS|FAIL|ALL PASS)|passed|failed|skipped|^ +(ok|x|-) [0-9]+ \['
# 2. the notes e2e on two overlay files, as before this slice
export COMPOSE_PROJECT_NAME=auth-core-notes
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
scripts/e2e-notes.sh
docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
# 3. the earlier scripts on the base stack, without overlays
export COMPOSE_PROJECT_NAME=auth-core-base7
docker compose -f deploy/docker-compose.yml --env-file .env down -v
docker compose -f deploy/docker-compose.yml --env-file .env up -d --build
scripts/e2e-login.sh && scripts/e2e-refresh.sh && scripts/e2e-lockout.sh && scripts/e2e-email.sh && scripts/e2e-tenancy.sh
docker compose -f deploy/docker-compose.yml --env-file .env down -v
unset COMPOSE_PROJECT_NAME
```

  Expected: `ALL PASS` from `e2e-web.sh` (with `PASS chromium` and `PASS webkit` before it), `ALL PASS` from `e2e-notes.sh` and from the
  five others, which are **unedited** (`e2e-login.sh` and `e2e-refresh.sh` need `PyJWT[crypto]` on the `python3` of the `PATH`: the
  venv of slice 6 has it). The five earlier scripts are not re-runnable on a used stack (they confirm users and change passwords):
  the `down -v` before them is part of the pass. Write the durations in the map's notes.

  The filter on the first command keeps the result lines of the script and of Playwright and hides the rest. `grep` also
  hides the exit status of the pipeline, so read the last line: `ALL PASS` means it passed, and no `ALL PASS` means it did not.
  To see why a test failed, run `scripts/e2e-web.sh > "$LOG" 2>&1` with `LOG="$(mktemp)"`, then read the lines around the failure
  with `sed -E 's/token=[A-Za-z0-9_-]+/token=<hidden>/g' "$LOG" | sed -n '...'`, and remove `$LOG` afterwards. Never paste an
  unfiltered failure into a report.

  Then remove what this plan made (and only that): 

```bash
GD="$(git rev-parse --git-dir)"
[ -f "$GD/web-made-env" ] && rm -f .env "$GD/web-made-env"
[ -f "$GD/web-made-secrets" ] && rm -rf .secrets "$GD/web-made-secrets"
git status --porcelain | grep -E '\.env|\.secrets' || echo "nothing of the run is left to commit"
```

  (A `.env` or `.secrets/` that was there before stays, with the lines this plan appended to it: the variables
  `NOTES_DB_PASSWORD` and the unverified user, which the base files and the notes script already read.)

- [ ] **Step 8: Hand over** - uncommitted: the two files, as
  `docs: integration guide for Angular products and the acceptance map for spec 0007`.

---

## Self-review against the spec

- **In scope, one by one.** The sample app in `samples/notes-web/` (Angular 21, the screens, the sign-in pieces in `src/app/auth/`): Tasks 1-7. The
  third compose overlay (Caddy on the same origin as `/auth` and `/api`, over HTTP on `:8088` and HTTPS on `:8443`): Task 8. The development
  server with its proxy: Tasks 1 (`proxy.conf.json`, `angular.json`) and 11 (the check). The unit tests of the sign-in pieces, with no Docker:
  Tasks 2-4 (and of the screens: 5-7). The Playwright tests in Chromium and WebKit and `scripts/e2e-web.sh`: Tasks 9 and 10. The integration guide:
  Task 11.
- **Contract, Screens table, row by row:** `/login` Task 5; `/forgot` Task 5; `/reset`, `/verify`, `/invite` Task 6; `/notes` Task 7; the three
  bullets under the table (`/` and unknown routes: Task 7; the email in the navigation state: Tasks 5 and 6; the token read once and removed: Task 6;
  the rules shown, not repeated: Task 6; "Something went wrong": every page). **Session:** the access token in memory and the three answers at start:
  Task 2; the interceptor and its table: Task 3; the guard and `returnUrl`: Task 4; sign out: Tasks 2 and 7. **Files:** Tasks 1-10. **Compose overlay
  and proxy** and the headers: Task 8, proved in a browser by group 7 of Task 9. **Development server:** Task 11, step 5. **Tests:** the unit lists of
  the spec are Tasks 2-4; the seven Playwright groups are Tasks 9 (1, 2, 7) and 10 (3, 4, 5, 6); the script is Task 9. **Integration guide:** Task 11.
- **Decisions 1-9 of the spec** are built as written: 1 (a sample in the repository: the whole plan), 2 (Angular 21: Task 1), 3 (no phone test: the
  checklist is step 7 of the guide), 4 (Chromium and WebKit: Task 9), 5 (English, one file: Task 1), 6 (no PWA: nothing in the plan builds one),
  7 (Caddy serves the build and the tests run against it: Tasks 8-10), 8 (pinned packages: Task 1), 9 (HTTPS for WebKit: Tasks 8-10).
- **No placeholder.** Every file of this plan is complete; the only text left to the orchestrator is the two sections at the end of the acceptance map.
- **Names.** `AuthService` (`token`, `me`, `notice`, `start`, `login`, `logout`, `refresh`, `loadMe`, `dropSession`, `showNotice`, `clearNotice`),
  `Failure`, `failureOf`, `errorCode`, `isRecord`, `Me`, `RefreshResult`, `AuthNotice`, `LoginResult` (Task 2; `asMe` and `isStrings` stay inside the file); `authInterceptor`, `wantsToken`,
  `TOKEN_PATH_PREFIXES`, `TOKEN_PATHS` (Task 3); `authGuard`, `safeReturnUrl`, `DEFAULT_RETURN_URL` (Task 4); `AccountApi`, `Outcome`, `InvitePreview`
  (Task 5); `takeTokenFromUrl` (Task 6); the `data-testid`s `me-email`, `me-company`, `me-role`, `sign-out`, `notice`, `notice-text`, `rules`,
  `resend`, `invite-join` are the same in the components, the unit tests and the Playwright tests; the `E2E_*` variables are the same in the script and in
  `support/env.ts` users (`E2E_SEED_EMAIL`, `E2E_SEED_PASSWORD`, `E2E_UNVERIFIED_EMAIL`, `E2E_UNVERIFIED_PASSWORD`, `E2E_VIEWER_EMAIL`,
  `E2E_RESETTER_EMAIL`, `E2E_USER_PASSWORD`, `E2E_NEW_PASSWORD`, `E2E_SEEDED_NOTE`, `E2E_BASE_URL`, `E2E_API_URL`, `E2E_MAILPIT_URL`); every key of
  `texts.ts` that a page or a test uses is defined in Task 1.

## Findings: where the plan adds to, or goes beyond, the spec

The spec wins. Each of these is a point the spec is silent on, or a choice the plan made inside what the spec allows. Those marked
**decided** were settled by the owner after the first version of this plan; the spec itself now says them (the invite sentence, the
cases under the interceptor's table, the Dismiss button, the singular).

1. **Singular and plural** - **decided, now in the spec.** "1 minute" and "1 second" for 1; "N minutes" and "N seconds" otherwise.
2. **An invitation that is `invalid_token`** - **decided, now in the spec.** "This invitation has expired or was already used. Ask for a
   new one." with no link: the person has to ask their company, and the forgotten-password screen cannot help. It is its own text
   (`texts.invite.invalid`); the reset and verify screens keep "This link has expired or was already used.". A missing or empty `token`
   on `/invite` shows the same sentence.
3. **The bar has a "Dismiss" button and is cleared by a sign-in** - **decided, now in the spec.** The `403 forbidden` message and the two
   other messages all use the bar.
4. **The cases under the interceptor's table are in the spec**: a refresh that fails with anything but `401` keeps the token and shows
   "Can't reach the server."; a `401` on the retried request goes to the caller as it is; a `401` on a request sent with a token that has
   since been renewed is retried without another refresh; after `permissions_changed` `/auth/me` is asked again. The plan builds them
   as written (Task 3). One reading of the plan's own: `/auth/me` is asked again at the same time as the request is sent again, and
   not for a request that is itself `/auth/me`.
5. **A refresh that does not answer in 10 seconds counts as "no network"**, and so does the `GET /auth/me` that follows it at start (the
   same ten seconds): the app must not sit blank behind the initializer.
6. **`login` answers `200` with a `status` other than `authenticated`** (a future step-up) as a failure: "Something went wrong."
7. **The header shows `roles` joined with ", "** (`/auth/me` returns an array; today it has one role).
8. **`scripts/e2e-web.sh` starts one clean stack per browser** - **decided.** The seeded unverified user can be confirmed only once
   and mail limits are per address.
9. **What "seeds what the tests need" means:** a note by the admin, and two invitations queued through the operator CLI (a viewer for
   group 4, a user for group 3). Group 4's "the operator invites a viewer with the CLI" is done by the script, and the test reads the mail.
10. **Files the spec's list does not name:** `.dockerignore`, `.gitignore`, `tsconfig*.json`, `account-api.ts`, `pages/token-from-url.ts`,
    `src/testing/helpers.ts`, `e2e/support/*`, `e2e/tsconfig.json`. The spec's list is of what a reader needs to find, not every file.
11. **The Docker build context is `samples/notes-web`**, not the repository root, with its own `.dockerignore`; the overlay's `build.context` is
    `../samples/notes-web` (relative to `deploy/`).
12. **The cache rule is "not an existing `.js`, `.css` or `.ico` file"**, so any other file that exists (an image, a font) is revalidated as well: the spec
    says only that hashed `.js` and `.css` files *may* be cached.
13. **Playwright has no trace**: a trace would put tokens and passwords on disk. Failures give a screenshot in `test-results/` (git-ignored). The
    script hides `token=...` in what it prints.
14. **Unit tests cannot see the browser's real history.** In the unit tests the router runs on a mock location, so the removal of the token from the
    address bar and the email in the navigation state are checked there on jsdom's `history` and the router's `currentNavigation()`, and in a real browser by
    groups 4 and 7 of the e2e tests. For the same reason the trimming of an email is tested with `typeRaw`: jsdom, like a browser, strips the spaces around the
    value of an `email` input before the page sees it, so the page's own `trim()` is a second line of defence that only a plain text field can exercise.
15. **The root `README.md` is not changed** - **decided.** Only `samples/notes-web/README.md` and the guide.
16. **WebKit in Playwright is the engine with a phone's screen, not Safari on a phone**; Decision 3 and the guide's step 7 say so.
17. **`docs/design.md`** gets its one-line note on Decisions 2, 3, 5 and 6 with "As built" (see "After the plan"), as the spec says.
18. **Hardening the plan added** (not in the spec; none changes a row of its tables):
    - `safeReturnUrl` also refuses a value that starts with `/%2f` or `/%5c` in either case, and the guide says to give the result to
      `router.navigateByUrl` only;
    - `AuthService` checks the shape of the `GET /auth/me` answer (a page of HTML from a proxy, or an object without `roles`, is "can't reach
      the server", not a crash);
    - `AuthService` counts the sessions that ended in the tab: a refresh or a `/auth/me` answer that was on its way when the person signed
      out is ignored and does not bring the session back;
    - a rule name from Auth-Core that is also a property of every object (`constructor`) shows the fallback line, not a function;
    - the "Send the verification link again" button on `/login` sends one request for a double click;
    - Caddy serves HTTP/1.1 and HTTP/2 only (`servers { protocols h1 h2 }`: the UDP port of HTTP/3 is not published);
    - the interceptor uses `from(promise)`, which loses the cancellation of a request in flight; a comment says so (acceptable for a sample).

## After the plan: verify, then merge

1. Dispatch the three local verifiers **in parallel** (Sonnet, fresh context, read-only), as defined in
   [`docs/workflow.md`](../../workflow.md#verification): realization vs **spec** (8 layers, using the acceptance map, criterion 2 row by row against the
   screen tables, and the five Review Focus lines), API/e2e (a clean stack with the three files and `scripts/e2e-web.sh`, taking each token from the mail
   as delivered; then `scripts/e2e-notes.sh` with two files and the earlier scripts without overlays as the regression pass), and security. Only one of
   them builds and tests in the tree; only one uses compose and the ports `8088`, `8443`, `8080` and `8025`, with a compose project name of its own.
2. What the security verifier is asked to prove, from the spec's notes: the token is held in memory only and sent only as the interceptor's rules say;
   no open redirect through `returnUrl`; mail tokens leave the address bar and are not sent as a referrer; the response headers; no inline script; nothing
   secret in the built files; and that the Caddyfile serves no file outside the build.
3. Each finding carries a `scope`. At most 2 fix rounds per verifier, then the issue goes to the owner.
4. Record the outcome: the plan-vs-implementation notes and the verification log in the acceptance map, and an
   `## As built (owner, date)` section in spec 0007. The note of `docs/design.md` on Decisions 2, 3, 5 and 6 is added in this step, with the
   as-built section, as the spec says: in the list of "Week 5: first consumer, frontend", **after** the line `  - Playwright end-to-end tests` insert

```
  - *As built (spec 0007):* the frontend is a sample in this repository, `samples/notes-web`, on Angular 21, not 19 (Decisions 1 and 2); the login on a phone moved to the first real deployment (Decision 3); the screens are in English, every text in one file (Decision 5); a plain web page, not a PWA (Decision 6).
```

5. When every verifier passes, merge the feature branch into `main` locally.

## Decisions taken after the first version of this plan, and preconditions

**Decided by the owner** (the spec says the first four of them now):

1. **Downloads.** `node:24-alpine` is approved (Decision 8 of the spec names it): the build of Task 8 may pull it. The npm packages and
   the Angular CLI at the pinned versions (Task 1) are on the same list. Playwright's Chromium and WebKit are normally in its cache from
   the technical trial; if one is missing, Task 9, step 1 stops and asks.
2. **The `invalid_token` of an invitation:** "This invitation has expired or was already used. Ask for a new one." with no link to `/forgot`
   (findings 2).
3. **"1 minute", not "1 minutes"** (finding 1).
4. **One clean stack per browser** in `scripts/e2e-web.sh` (finding 8).
5. **The root `README.md` is not changed** (finding 15).

No open question is left for the owner.

### Preconditions

- Slices 5 and 6 are merged (this branch starts from `main` after slice 6): the company API, `/auth/me`, the invitations, the operator CLI, the manifest
  mount in the base file and the notes sample are all there. Nothing in this plan waits for another branch.
- The ports `8088`, `8443`, `8080` and `8025` are free while a task of Group C runs, and no other stack of this clone uses the compose project names of the
  tasks (`auth-core-web`, `auth-core-web-t8`, `auth-core-web-t11`, `auth-core-notes`, `auth-core-base7`).
- This worktree may already have a `.env` and `.secrets/`. The plan keeps them, appends only the variables that are missing (never printing a
  value), and removes at the end only what it made itself (marker files in the git directory).

**Risks found and not fixed here:**

- A token in memory can be read by script running in the page; the Content-Security-Policy limits scripts to the app's own files (spec, residual risk).
- Another tab stays usable for up to 10 minutes after a sign-out in one tab (spec, residual risk).
- The certificate on `:8443` is not trusted by a browser: a person sees a warning, and Playwright accepts it. Safari on a real phone may treat cookies
  differently from WebKit in Playwright; the phone test after the first deployment covers it.
- The build in `node:24-alpine` was not tried before this plan was written (the image is not on the machine); native modules of the Angular build such as
  `lmdb` are the known risk, and Task 8, step 3 names the fallback.

## As built

(Written after implementation and verification, as in plans 0004, 0005 and 0006.)
