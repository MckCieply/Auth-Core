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
`/api/*` to the product's service and every other path to the built app, with `index.html` as the answer to a path with no file
extension (the router owns those paths). A path with an extension that is not in the build (`/missing.js`) is a `404`: a broken
build then shows as an error, and a browser never runs HTML as a script. The rule looks at the last segment of the path: a route of your
app whose last segment has a dot (`/notes/john.doe`) is a `404` too, so keep dots out of the last segment of a route (put the value in
a query, `/notes?name=john.doe`, or end the path in a slash); a dot in an earlier segment (`/a.b/c`) is fine. `/auth` and `/api` are
matched in lower case only. The sample's proxy answers only to the host names it is reached by (`localhost` and `127.0.0.1`) and turns
off the file server's canonical-URI redirects, which could be made to redirect to another site; your proxy should answer only to the
names of your product, and not redirect on a path it has rewritten. The compose overlay that builds and runs it is
[`samples/notes-web/compose.yml`](../../samples/notes-web/compose.yml).

While you work on the screens, `ng serve` can stand in for the proxy:
[`samples/notes-web/proxy.conf.json`](../../samples/notes-web/proxy.conf.json) sends `/auth` and `/api` from the dev server to the
stack, so the browser still sees one origin.

**WebKit (Safari) and plain HTTP.** WebKit keeps a `Secure` cookie set over `http://localhost` but never sends it back, so on a
developer machine a session cannot survive a reload there. The sample's Caddyfile therefore also serves `https://localhost:8443`
with a certificate from Caddy's own authority; a browser warns about it, and the end-to-end tests accept it. In production you have
real HTTPS and this does not arise.

## 2. Copy `samples/notes-web/src/app/auth/` and register the initializer, the interceptor and the guard

Copy the three files of [`samples/notes-web/src/app/auth/`](../../samples/notes-web/src/app/auth/) into your app:
`auth.service.ts` (the token, the session, `login`, `completeSignup`, `logout`, `refresh`), `auth.interceptor.ts` and `auth.guard.ts`. They depend on
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
- **A screen** reads `AuthService.me()` (email, company, `org_kind`, roles, permissions) and `AuthService.token()` (a signal: `null` when nobody is
  signed in). Show a control only when `me().permissions` lists what it needs; the server still checks.
- **Sign-in** is `await auth.login(email, password)`, which answers `{ ok: true }` or `{ ok: false, failure }`; on success go to
  `safeReturnUrl(returnUrl)` (the guard file exports it: it follows only a path of your own app, never `//host`, `https://host` or
  a slash written as `%2f`). Give the result to `router.navigateByUrl`, never to `location.href` or `window.open`.
  See [`pages/login.ts`](../../samples/notes-web/src/app/pages/login.ts). A self-service sign-up ends in the same state: `await auth.completeSignup(token, password, companyName)` posts to `POST /auth/signup/complete`, which Auth-Core answers as it answers a login (the access token in the body and the refresh cookie), and it answers `{ ok: true }` or `{ ok: false, failure }` like `login`. On success open your product (the sample goes to `/notes`, replacing the screen of the used link, so that Back does not return to it). A private sign-up (spec 0010) names no company: pass `null` as the company name and the body holds the token and the password only.
- **Sign-out** is `await auth.logout()` and then `/login`; the token is dropped whatever the server answers
  ([`pages/notes.ts`](../../samples/notes-web/src/app/pages/notes.ts)).
- There is no refresh ahead of expiry: a token is renewed only after a `401`, which costs one extra request about every 10 minutes.

## 3. Say which paths get the token

The token goes only to your app's own origin, and only to paths on two lists at the top of `auth.interceptor.ts`:

```ts
export const TOKEN_PATH_PREFIXES: readonly string[] = ['/api/', '/auth/org'];
export const TOKEN_PATHS: readonly string[] = ['/auth/me', '/auth/account'];
```

`/auth/account` is the self-deletion of a private account (spec 0010): it needs the token too.

Edit them to the paths of your own backend (`/api/` is the sample's). It never adds the token to another origin or to the other
`/auth` endpoints (`/auth/login`, `/auth/refresh`, the mail-link endpoints, `/auth/options`, `/auth/signup`), and a path that only looks like one after `..` is
resolved does not count. [`samples/notes-web/src/app/auth/auth.interceptor.spec.ts`](../../samples/notes-web/src/app/auth/auth.interceptor.spec.ts)
shows the cases.

## 4. Build the screens for the mail links, and point Auth-Core at them

Auth-Core's mails carry a link to your app; you build the screens, it sends the mails. Six screens (four of them open from a mail
link), and the settings that say where the links go (`Auth:App:FrontendUrls:ResetPassword`, `VerifyEmail` and `AcceptInvite`, and, only if your
product lets visitors start a company on their own, `CompleteSignup`; as environment variables `Auth__App__FrontendUrls__ResetPassword` and so on,
see the `auth` service in [`compose.yml`](../../samples/notes-web/compose.yml)). Each link is that URL plus `?token=<43 characters>`. Outside Development the
URLs must be `https`. Self-service sign-up is off until the product's manifest says `signup: self-service` **and** `CompleteSignup` is set
([`docs/deployment/vps.md`](../deployment/vps.md)); the sign-in screen asks `GET /auth/options` and offers "Create an account" when the answer says `self-service` for `signup` or for `private_signup` (for example `{"signup":"self-service","private_signup":"off"}`
for a product with company sign-up only), so a product does not repeat its manifest in its frontend. `private_signup` says whether a private person may sign up alone (spec 0010); the sign-up screen draws the choice "Organization / Private person" only when both are on. The sample waits at most 10 seconds for this call and reads a slower one as "off", so a hung call never leaves the sign-up screen empty.

| Route | The screen | Calls |
| --- | --- | --- |
| `/reset?token=` | new password, twice; the rules it did not meet; "link used up" | `POST /auth/password/reset` |
| `/verify?token=` | confirms on opening; a form to ask for a new link when it is used up | `POST /auth/email/verify`, `POST /auth/email/verify/request` |
| `/invite?token=` | "Join {company} as {role}" and the email; the password, twice; for an address with a private account (the preview's `has_private_account`) the warning in red instead and one button, "Move me to the company and delete my private account", which sends `leave_private_account: true` and no password | `POST /auth/invites/preview`, `POST /auth/invites/accept` |
| `/signup` | "Organization / Private person" when `GET /auth/options` says both are on (with one on, that one); the email, and for an organization the company name; always "Check your inbox"; the wait on a `429`; "closed" on `signup_disabled` | `POST /auth/signup` (`kind` `private` for a private person, with no `company_name`) |
| `/complete-signup?token=` | the address (read-only); for an organization the company name (editable); the password, twice; the rules it did not meet; "link used up" with a way to sign up again; "an account already exists" with a way to sign in; on success the person is signed in and the notes screen opens | `POST /auth/signup/preview` (it says `kind`), `POST /auth/signup/complete` (answers as `POST /auth/login` does: the access token and the refresh cookie) |
| `/forgot` | email; always the same answer | `POST /auth/password/forgot` |

The screens are in [`samples/notes-web/src/app/pages/`](../../samples/notes-web/src/app/pages/); the calls are in
[`samples/notes-web/src/app/account-api.ts`](../../samples/notes-web/src/app/account-api.ts); the words are all in
[`samples/notes-web/src/app/texts.ts`](../../samples/notes-web/src/app/texts.ts), so that a product changes them (or translates them) in one place.

Three rules the sample follows, and you should too:

- **The token leaves the address bar at once.** A screen reads `token` from the URL once and removes it with a router navigation that replaces the
  history entry (`replaceUrl`, [`pages/token-from-url.ts`](../../samples/notes-web/src/app/pages/token-from-url.ts)), so it stays out of
  the browser's history, of what a person copies and of the router's own copy of the address (`router.url`). The navigation lands on the
  same route, so the router keeps the screen and it makes no second call. The page that is served has `Referrer-Policy: no-referrer` (step 6), so the token is not sent on as a
  referrer either. The token stays in memory, so after a server error the verify, invite and complete-signup screens offer "Try again" with it: a reload
  would find no token in the address bar and call the link used up.
- **Password rules are Auth-Core's.** A `weak_password` answer names the rules not met (`too_short`, `requires_upper`,
  `requires_lower`, `requires_digit`); show one line each, and do not repeat the policy in your app. The reset, invite and complete-signup screens
  share one list of lines, [`pages/password-rules.ts`](../../samples/notes-web/src/app/pages/password-rules.ts).
- **Do not put an email in the URL.** After an invitation is accepted the sample opens `/login` with the address filled in, passed in
  the router's navigation state. The same holds after a sign-up is completed: the completion signs the person in (Auth-Core answers as it answers a login) and the screen opens the notes in place of itself, so the address is in no URL and no sign-in screen is shown; the password was chosen at the link, never on the sign-up form.

**Mind the logs.** The address bar and the referrer are covered, but the request itself, `GET /reset?token=...`, still reaches your
proxy and your web server, and most of them write the whole line to an access log, token included. Keep the proxy's and the server's
access logs off for `/reset`, `/verify`, `/invite`, `/complete-signup`, `/auth/providers/*/start` and `/auth/providers/*/callback` (spec 0011: the start's query holds an invitation
token as `invite=`, the callback's holds `code=` and `state=`), or have them scrub the query string from what they write (`token=` on the first four paths, the
whole query on the provider paths), and keep any log
that does hold one as private as a password. A token is single-use and expires, but one that is still valid in a log is a way into
someone's account. (The sample's Caddy writes no access log.)

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
shows the common "Something went wrong. Try again." for a list or a save that failed. A call to your own origin that is not answered within 30
seconds (`REQUEST_TIMEOUT_MS` in `auth.interceptor.ts`) fails the same way, so that no screen waits for ever; raise it for calls that
take longer, such as an upload. Giving up cancels the request in flight and ends the whole chain (no second try; a refresh that was
already running goes on, and if it ends the session the person still goes to `/login`): the interceptor is built from observables, not promises, for that reason; keep it so if you change it.

The `403 permissions_changed` and `503 auth_unavailable` answers are what Auth-Core's company API and a product's backend (the Python
package, for one) answer; see the specs [0005](../superpowers/specs/0005-tenancy-and-rbac.md) and
[0006](../superpowers/specs/0006-python-consumer-package.md). The messages are drawn by the shell
([`samples/notes-web/src/app/app.ts`](../../samples/notes-web/src/app/app.ts)) from `AuthService.notice()`; the bar has no timer.

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
  ([`samples/notes-web/src/index.html`](../../samples/notes-web/src/index.html)). The sample's Caddy does it with its `templates` directive and the
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
- [ ] Open a link from a reset mail (and an invitation, a verification mail and a sign-up mail). The screen opens and the address bar does not show
  the token.
- [ ] Sign out. A reload shows the sign-in screen.
- [ ] Wait out the access token (10 minutes) with the page open, then do something that calls your API. You stay signed in: one
  refresh happens and the request goes through.
- [ ] **On iOS, also from the home screen:** add the app to the home screen, open it from there, and sign in, reload and sign out. An
  app opened from the home screen can have cookies of its own on iOS.

Known limits, in the spec's words: another open tab keeps its token until it expires (up to 10 minutes) after a sign-out in one tab,
because tabs are not synchronised; and a token in memory can be read by script running in the page, which the
`Content-Security-Policy` of step 6 limits to your own files.

## Private accounts (spec 0010)

When your manifest says `private.signup: self-service`, a person can sign up alone. Your screens tell the two kinds apart by `me().org_kind`:
`personal` for a private account (its `org_name` is `private` and its role `private`), `organization` for a company member. The sample shows
"Private account" and no company or role to a private account, and "Team" only to a company member who holds `members:manage`
([`pages/notes.ts`](../../samples/notes-web/src/app/pages/notes.ts)).

- **"Delete account"** is for a private account only. Delete your product's data first, with an endpoint of your own backend that deletes
  what belongs to the token's `org_id` (the sample's `DELETE /api/me`), and call `auth.deleteAccount(password)` (`DELETE /auth/account`) only
  after its `204`: Auth-Core does not tell your product that an account is gone. Warn first ("This deletes your account and all your notes. It
  cannot be undone."), ask for the password on the same screen, and on `403 wrong_password` say what happened: the data is gone, the account
  is not. A `429 too_many_attempts` is the lockout: show the wait. On success open the sign-in screen.
- **An invitation to a private account:** when the preview says `has_private_account`, show the warning (the paragraph of the mail) and one
  button that sends `leave_private_account: true` with no password: the account keeps its own. Without it the acceptance answers
  `409 private_account_exists` ([`pages/invite.ts`](../../samples/notes-web/src/app/pages/invite.ts)).
- **Removing a member deletes the account,** and deleting a company deletes every member's, the admin's included. Say so in the confirmation
  of your own team screen, with the sample's sentence: "This person's account will be deleted." ([`pages/team.ts`](../../samples/notes-web/src/app/pages/team.ts)).
  The person can be invited again, as a new account.
- **Personal data after deletion:** Auth-Core deletes the account and what it holds of it, keeps the audit rows that name it (its id, its
  address and the client addresses) for their 90 days by default on the legitimate interest of security, and backups keep everything until they are
  rotated ([threat model](../security/threat-model.md), "Account deletion and personal data"). Your product's data is yours to delete.

## Google and Microsoft (spec 0011)

When `GET /auth/options` lists `providers` (`["google","microsoft"]`, in the manifest's order), draw one button per name, first on the
sign-in, sign-up and invitation screens, then "or" and the form. **A button is a link, not a call**: `<a href="/auth/providers/google/start">`
(on `/invite` with `?invite=<the token>`, for an address without a private account only). The browser leaves for the provider and comes back
to your return screen ([`pages/provider-buttons.ts`](../../samples/notes-web/src/app/pages/provider-buttons.ts)).

- **The return screen** (the sample's `/provider-return`, the address of `Auth:App:FrontendUrls:ProviderReturn`) reads the fragment once,
  `#ticket=...&purpose=...` or `#error=...&purpose=...`, and removes it from the address bar with a navigation that replaces the history
  entry **before** it calls anything; when it cannot remove it, it sends nothing and says that the sign-in failed
  ([`pages/provider-return.ts`](../../samples/notes-web/src/app/pages/provider-return.ts)). The errors are `cancelled`, `invalid_state`,
  `provider_unavailable` and `provider_error`: each gets a sentence and the way back to the sign-in screen, and a value you do not know is
  read as `provider_error`. Keep the ticket in memory and in the router's navigation state only: never in a URL, `localStorage` or
  `sessionStorage`. The router keeps that state in the browser's history entry of the next screen, so a screen that has spent the ticket, or
  no longer needs it, replaces its entry without the state (the sample's notes screen does for a `reauth` ticket). The sample keeps one
  thing in `sessionStorage`: the name of the provider the person chose (the fragment does not carry it), for the sentence of the link
  screen; never the ticket, the address or the kinds.
- **The exchange.** `POST /auth/providers/exchange` `{"ticket"}` answers as a login does (the token and the refresh cookie), or
  `{"next":"link","email","email_verified"}` (open the link screen), or `{"next":"finish_signup","email","kinds"}` (open the finish
  screen), or an error that the sample words one by one: `invalid_ticket` (too late, used, or another browser: start again), `no_email`,
  `no_account`, `already_registered`, `no_membership`, and for an invitation `invalid_token`, `address_mismatch`, `already_member`,
  `private_account_exists` and `provider_already_linked`. A `429` keeps the ticket and shows "Try again". The cookie `auth_pt` goes along by
  itself; the call needs no access token, so leave the provider paths out of the interceptor's token paths.
- **The link screen** asks the password of the existing account once (`POST /auth/providers/link`); when `email_verified` is `false` it
  says so and adds a link to sign-up above the same password form, because the provider did not prove the address and the person may have
  no account ([`pages/provider-link.ts`](../../samples/notes-web/src/app/pages/provider-link.ts)). A wrong password is `401` and the ticket stays
  usable for a retry; after ten failed attempts at the sign-in and the link together the address answers `429 too_many_attempts`. **The finish screen** shows the
  address read-only, the choice when `kinds` holds both, the company name for an organization, and no password
  (`POST /auth/providers/finish`, [`pages/provider-finish.ts`](../../samples/notes-web/src/app/pages/provider-finish.ts)).
- **"Delete account" without a password.** `GET /auth/me` says `has_password` and `linked_providers`. For `has_password: false` show one
  "Confirm with {provider}" link per linked provider to `start?purpose=reauth`; back with the ticket (purpose `reauth`), delete your data
  and then call `DELETE /auth/account` with `{"provider_ticket"}` (the ticket is good for 5 minutes and once). `reauth_failed` means
  confirm again; `password_required` and `reauth_required` mean the account changed: ask `GET /auth/me` again and redraw
  ([`pages/notes.ts`](../../samples/notes-web/src/app/pages/notes.ts)).
- A person who signed up with a provider has no password; "Forgot your password?" sets one.
- **Deleting a company needs a password too.** `DELETE /auth/org` keeps the password confirmation. An admin whose account has no password (an
  organization made through a provider, or an invitation accepted through one) gets `403 reauth_required` for any password; nothing is counted
  and nothing is deleted. Tell such an admin to set a password with "Forgot your password?" first, and then delete the company (spec 0011,
  Amendment A9). A provider confirmation for deleting a company is left to a later spec.

## The limits and an outage (spec 0008)

Auth-Core limits requests per address (30 sign-ins, 60 refreshes, 10 requests for a mail, 20 for invitation and sign-up link requests and 300 others a minute; the start and the callback of a provider round trip, and its exchange, link and finish, count as sign-ins) and
answers `429 {"error":"too_many_requests","retry_after_seconds":n}` with `Retry-After` when a limit is reached. The sample's interceptor
shows "Try again in N s." for such an answer from any endpoint of your origin and **does not sign the person out**: only a `401 invalid_grant`
from the refresh ends the session. During a database outage a refresh answers `503 temporarily_unavailable` with `Retry-After: 5` instead of
`401`; the cookie is neither cleared nor rotated, so the same cookie works once the database is back, and the sample keeps the session too.
Keep both rules in your own interceptor: a `429` or a `503` is never a reason to sign out. People behind one shared address (a NAT) share these
limits. The lockout of one account after repeated wrong passwords (`429 too_many_attempts`, spec 0003) is a different answer, which the sign-in screen words itself.
