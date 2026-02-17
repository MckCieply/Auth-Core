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

- The bar above the screens (`Try again shortly.`, `You don't have access to this.`, `Can't reach the server. Try again shortly.`) has no timer:
  dismiss it, or it goes with the next sign-in.
- Other open tabs keep their token in memory until it expires (up to 10 minutes) after a sign-out in one tab; tabs are not
  synchronised.
- There is no PWA, no sign-up and no screen for company admins; see the deferred list of the spec.
