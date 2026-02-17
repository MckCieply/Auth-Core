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
`npm audit --omit=dev` reports 6 high advisories against them: `@angular/core`, `common`, `compiler`, `forms`,
`platform-browser` and `router` (the last three only through the first three). Seen on 2026-10-06, they are of these kinds:

- cross-site scripting through i18n attribute bindings and i18n event-handler attributes, and sanitisation bypasses in templates
  (two-way property bindings, attribute namespaces, directive host bindings), in `@angular/compiler` and `@angular/core`;
- denial of service through memory use in the date and number formatting of `@angular/common` (`formatDate`, `digitsInfo`);
- information leaks and cache poisoning in `HttpTransferCache`, which exists for server-side rendering.

This sample uses no i18n, no server-side rendering (so no `HttpTransferCache`), no dynamic attribute or host bindings, and
every text goes through interpolation; the `Content-Security-Policy` of the proxy is a second line. The versions are not
changed here: moving to a fixed 21.2 release is a follow-up. A product that copies the sign-in pieces should install a fixed
release of its own.

## Things to know

- The bar above the screens (`Try again shortly.`, `You don't have access to this.`, `Can't reach the server. Try again shortly.`) has no timer:
  dismiss it, or it goes with the next sign-in.
- The proxy answers only to `localhost` and `127.0.0.1` (another Host name gets a 421), and a path with a file extension that
  is not in the build (`/missing.js`) is a 404, not the app.
- `scripts/e2e-web.sh` removes `test-results/` (screenshots of failed tests show the page) when it ends; `E2E_KEEP_RESULTS=1` keeps it.
- Other open tabs keep their token in memory until it expires (up to 10 minutes) after a sign-out in one tab; tabs are not
  synchronised.
- There is no PWA, no sign-up and no screen for company admins; see the deferred list of the spec.
