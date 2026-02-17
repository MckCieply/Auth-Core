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
npm run check:docs             # the integration guide: its seven steps, and every link and path in it exists (no Docker)
```

The end-to-end tests run against the built app on `https://localhost:8443`, in Chromium and in WebKit (`iPhone 15`), with the
certificate accepted. They need Docker, Node 24 and Playwright's browsers (`npx playwright install chromium webkit` once):

```bash
scripts/e2e-web.sh        # starts the stack under its own compose project name, runs both projects, stops the stack
```

## Known advisories

The Angular packages are pinned at 21.1.4 ([Decision 8 and 13](../../docs/superpowers/specs/0007-angular-sample.md)), and
`npm audit --omit=dev --json` reports 6 high-severity packages against them (seen on 2026-10-06; every advisory is fixed in the
21.2 line, 21.2.25 at the time). Per package, by advisory id (GHSA) and kind:

- `@angular/core`: GHSA-prjf-86w9-mfqv, GHSA-g93w-mfhg-p222 and GHSA-jj27-h5hq-8x99 (cross-site scripting through i18n, i18n
  attribute bindings and i18n event-handler attributes); GHSA-f3m7-gqxr-g87x (sanitisation bypass, template and attribute
  namespaces); GHSA-692r-grfm-v8x7 (the same for dynamic components); GHSA-hh8m-fm6v-7cvg (sanitisation bypass through directive
  host bindings); GHSA-rgjc-h3x7-9mwg (DOM clobbering and response-cache poisoning in client hydration).
- `@angular/compiler`: GHSA-g93w-mfhg-p222, GHSA-jj27-h5hq-8x99, GHSA-f3m7-gqxr-g87x and GHSA-hh8m-fm6v-7cvg (as above), and
  GHSA-58w9-8g37-x9v5 (sanitisation bypass in two-way property bindings).
- `@angular/common`: GHSA-48r7-hpm6-gfxm (denial of service by memory use in `formatDate`), GHSA-p3vc-36g9-x9gr (the same in
  `digitsInfo`, the number formatting); GHSA-39pv-4j6c-2g6v, GHSA-q6f4-qqrg-jv6x, GHSA-jhpw-976m-542j and GHSA-p297-fm68-3q8c
  (cache-key weakness, credentialed requests cached, cache-key ambiguity and a bypass in `HttpTransferCache`: information leaks and
  cache poisoning, only with server-side rendering).
- `@angular/router`: GHSA-ff3f-86qr-9cv3 (server-side rendering: denial of service by numeric matrix parameters in a URL), and it is
  flagged for `core`, `common` and `platform-browser` too.
- `@angular/platform-browser` and `@angular/forms`: no advisory of their own; flagged only because they depend on the packages
  above.

What the sample uses of that, and what it does not. It **does** use the date pipe (`formatDate`) in the notes list, with the fixed
format `'medium'`, on a `created_at` that the screen checks is a date before it shows the list; and one attribute binding,
`[attr.maxlength]` on the note field, bound to a constant (1000), never to data. It does **not** use i18n, server-side rendering
or hydration (so no `HttpTransferCache`), dynamic components, two-way property bindings, namespaced attributes, directive host
bindings or the number pipes; every text from a server goes through interpolation, never as HTML. The `Content-Security-Policy` of
the proxy is a second line. The versions are not changed here: moving to a fixed 21.2 release is a follow-up. A product that copies
the sign-in pieces should install a fixed release of its own.

## Things to know

- The bar above the screens (`Try again shortly.`, `You don't have access to this.`, `Can't reach the server. Try again shortly.`) has no timer:
  dismiss it, or it goes with the next sign-in.
- The proxy answers only to `localhost` and `127.0.0.1` (another Host name gets a 421), and a path whose last segment has a dot
  that is not a file of the build (`/missing.js`, and also a route such as `/notes/john.doe`) is a 404, not the app; `/notes/john-doe`
  and `/a.b/c` are routes. `/auth` and `/api` are matched in lower case only.
- `scripts/e2e-web.sh` removes `test-results/` (screenshots of failed tests show the page) when it ends; `E2E_KEEP_RESULTS=1` keeps it.
- Other open tabs keep their token in memory until it expires (up to 10 minutes) after a sign-out in one tab; tabs are not
  synchronised.
- There is no PWA, no sign-up and no screen for company admins; see the deferred list of the spec.
