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

Auth-Core's mails carry a link to your app; you build the screens, it sends the mails. Four screens (three of them open from a mail
link), and the three settings that say where the links go (`Auth:App:FrontendUrls:ResetPassword`, `VerifyEmail` and `AcceptInvite`; as environment variables
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

- **The token leaves the address bar at once.** A screen reads `token` from the URL once and removes it with a router navigation that replaces the
  history entry (`replaceUrl`, [`pages/token-from-url.ts`](../../samples/notes-web/src/app/pages/token-from-url.ts)), so it stays out of
  the browser's history, of what a person copies and of the router's own copy of the address (`router.url`). The navigation lands on the
  same route, so the router keeps the screen and it makes no second call. The page that is served has `Referrer-Policy: no-referrer` (step 6), so the token is not sent on as a
  referrer either. The token stays in memory, so after a server error the verify and invite screens offer "Try again" with it: a reload
  would find no token in the address bar and call the link used up.
- **Password rules are Auth-Core's.** A `weak_password` answer names the rules not met (`too_short`, `requires_upper`,
  `requires_lower`, `requires_digit`); show one line each, and do not repeat the policy in your app. The reset and invite screens
  share one list of lines, [`pages/password-rules.ts`](../../samples/notes-web/src/app/pages/password-rules.ts).
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

Every other answer reaches the screen that asked, untouched: no refresh, and the person stays signed in. That includes the notes
service's own errors (`503 {"error":"database_unavailable"}` means "try again later", also `500`, `404` and `405`); the notes screen
shows "The notes could not be loaded. Try again." or "The note could not be saved. Try again.". A call to your own origin that is not answered within 30
seconds (`REQUEST_TIMEOUT_MS` in `auth.interceptor.ts`) fails the same way, so that no screen waits for ever; raise it for calls that
take longer, such as an upload. Giving up cancels the request in flight and ends the whole chain (no late refresh, second try or
redirect to `/login`): the interceptor is built from observables, not promises, for that reason; keep it so if you change it.

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
