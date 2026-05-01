# Getting the keys for Google and Microsoft

Auth-Core signs people in with Google and Microsoft when three things are in place: your manifest lists the providers
(`providers: [google, microsoft]`, see [`deploy/auth.yaml`](../../deploy/auth.yaml)), the return address of your frontend is set
(`AUTH_FRONTEND_PROVIDER_URL`), and each provider's two keys are in `.env`. This guide gets the keys. The values go into the `.env` of
[`vps.md`](vps.md) (the lines are in [`deploy/.env.prod.example`](../../deploy/.env.prod.example)); a secret never goes into the manifest, a
compose file or the repository.

The commands of this guide are for [`deploy/docker-compose.prod.yml`](../../deploy/docker-compose.prod.yml), run from the directory that holds
`deploy/` and `.env`; `$C` stands for the compose command with its project, as in [`vps.md`](vps.md):

```bash
C="docker compose -p auth-core-prod -f deploy/docker-compose.prod.yml --env-file .env"
```

## The addresses you register

Both providers send the browser back to Auth-Core, never to your frontend. The address is the **origin** of your return address followed by
`/auth/providers/{name}/callback`. With `AUTH_FRONTEND_PROVIDER_URL=https://app.example.com/provider-return`:

| Provider | Redirect URI to register |
| --- | --- |
| Google | `https://app.example.com/auth/providers/google/callback` |
| Microsoft | `https://app.example.com/auth/providers/microsoft/callback` |

Auth-Core builds the address from that setting only, never from the request, so it must match what you register character for character
(scheme, host, port, path; no trailing slash). Your reverse proxy must send `/auth` to Auth-Core on that origin, as for every other path of
[`vps.md`](vps.md). Keep the access logs of `/auth/providers/*/start` and `/auth/providers/*/callback` off, or scrub their query strings: they
hold an invitation token, a code and a state (residual risk 55 in the [threat model](../security/threat-model.md)).

## Google

In the [Google Cloud Console](https://console.cloud.google.com/):

1. **A project.** Create one for your product (or pick the product's existing one).
2. **The consent screen** ("Google Auth Platform", or "APIs & Services" then "OAuth consent screen"): the audience **External**; the app
   name the person will see; the support address; your developer contact address. Under the data access (scopes) add `openid`, `email`
   and `profile` only: they are the non-sensitive ones and need no verification by Google. Publish the app ("In production"): while it is
   "Testing" only the test users you list can sign in.
3. **The client** ("Clients", or "Credentials" then "Create credentials" then "OAuth client ID"): the type **Web application**, a name of
   your choice, and under "Authorized redirect URIs" the Google address of the table above. No JavaScript origin is needed: the browser never
   talks to Google with this client.
4. **The values.** Copy the client ID into `AUTH_GOOGLE_CLIENT_ID` and the client secret into `AUTH_GOOGLE_CLIENT_SECRET`
   (`AUTH_GOOGLE_CLIENT_SECRET=<the client secret>`). A Google client secret does not lapse.

### Check it with the sample

The repository's notes sample (`samples/notes-web` in front of `samples/notes-api`, at `https://localhost:8443`) lists Google and Microsoft in
its manifest (`samples/notes-api/auth.yaml`). To try your Google client with it before you deploy:

1. In the client of step 3, add the sample's redirect URI `https://localhost:8443/auth/providers/google/callback` beside your own (Google
   accepts a `localhost` address for a Web application client). Remove it once the check is done.
2. Put `AUTH_GOOGLE_CLIENT_ID` and `AUTH_GOOGLE_CLIENT_SECRET` into the repository's own `.env` (it is git-ignored), and write a compose overlay
   **outside the repository**, for example `~/auth-core-keys.yml`, with these lines:

   ```yaml
   services:
     auth:
       environment:
         Auth__App__FrontendUrls__ProviderReturn: https://localhost:8443/provider-return
         Auth__Providers__Google__ClientId: ${AUTH_GOOGLE_CLIENT_ID:-}
         Auth__Providers__Google__ClientSecret: ${AUTH_GOOGLE_CLIENT_SECRET:-}
         Auth__Providers__Microsoft__ClientId: ${AUTH_MICROSOFT_CLIENT_ID:-}
         Auth__Providers__Microsoft__ClientSecret: ${AUTH_MICROSOFT_CLIENT_SECRET:-}
   ```

3. Start the sample's stack with that overlay and **without** the fake provider's overlay (`tests/Auth.FakeOidc/compose.yml`, which would
   send the buttons to the fake), from the repository root:

   ```bash
   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f samples/notes-web/compose.yml \
     -f ~/auth-core-keys.yml --env-file .env up -d --build
   ```

   With Google's keys only, Microsoft is not offered and `/auth/health` says `Degraded`: that is expected.
4. Open `https://localhost:8443/login` (accept the certificate warning), press "Continue with Google" and sign in at Google: a new address
   reaches the screen that finishes the account (`/provider-finish`), an address of an existing password account the link screen
   (`/provider-link`). Anything else is in "When a value is wrong" below.

## Microsoft

In the [Microsoft Entra admin center](https://entra.microsoft.com/), "App registrations", then "New registration":

1. **The registration.** A name the person will see; "Supported account types": **Accounts in any organizational directory and personal
   Microsoft accounts**; "Redirect URI": the platform **Web** and the Microsoft address of the table above. Register.
2. **The client id.** The "Application (client) ID" of the overview goes into `AUTH_MICROSOFT_CLIENT_ID`.
3. **The secret.** "Certificates & secrets", "Client secrets", "New client secret": a description and the longest lapse offered (at most 24
   months). Copy its **Value** (not its "Secret ID") into `AUTH_MICROSOFT_CLIENT_SECRET` at once: Entra shows it only now. Write the
   "Expires" date into `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES` as `yyyy-mm-dd`: from 30 days before it `/auth/health` says `Degraded`
   ([`microsoft-secret.md`](../operations/microsoft-secret.md) is the runbook).
4. **The claims.** "Token configuration", "Add optional claim", the token type **ID**, and tick `email` and `xms_edov` (accept the
   Microsoft Graph `email` permission it offers to add). Without `email` a work account has no address in Auth-Core and its sign-in ends
   with "no address"; without `xms_edov` every work address reads as unverified, which is safe: such a person is never given a finished
   account on that address, only the link screen (the password of an account that has the address) or the usual sign-up by mail (spec 0011,
   "The provider identity and its address").
5. **The permissions.** "API permissions" holds Microsoft Graph `openid`, `email` and `profile` (delegated). No admin consent is needed for
   them.

### Check it with the sample

1. In the registration's "Authentication" page, add the Web redirect URI `https://localhost:8443/auth/providers/microsoft/callback` beside your
   own (Entra accepts a `localhost` address for the Web platform). Remove it once the check is done.
2. Put `AUTH_MICROSOFT_CLIENT_ID` and `AUTH_MICROSOFT_CLIENT_SECRET` into the repository's `.env`; the overlay of Google's check passes them
   already (write it as shown there if you skipped Google).
3. Start the stack as in Google's check, open `https://localhost:8443/login`, press "Continue with Microsoft" and sign in with a personal
   account: a new address reaches `/provider-finish`. A work account reaches `/provider-finish` only with the optional claim `xms_edov` of
   step 4; without it `/provider-link` says that the address is not verified and links to the sign-up screen, which is the safe answer for
   an unverified address.

## Turn it on and check it

1. Set the values in `.env` and `providers: [google, microsoft]` in your manifest (list only the ones you configured), then
   `$C up -d` (a restart does not read `.env` again).
2. `curl -s https://app.example.com/auth/options` lists the providers, in the order of the manifest; `curl -s https://app.example.com/auth/health`
   says `Healthy`.
3. On your product's sign-in screen, "Continue with Google" (and "with Microsoft") goes to the provider and back, and a new person reaches the
   screen that finishes the account (the sample's `/provider-finish`).

## When a value is wrong

| What you see | Why |
| --- | --- |
| A provider is missing from `GET /auth/options`, `/auth/health` says `Degraded`, and the start logged a warning naming settings (`$C logs auth \| grep -i provider`) | The manifest lists it, but a key or `AUTH_FRONTEND_PROVIDER_URL` is blank: the warning names the missing settings (`Auth:Providers:Google:ClientId`, `Auth:Providers:Google:ClientSecret`, ..., `Auth:App:FrontendUrls:ProviderReturn`), never their values |
| The `auth` container stops at start, naming `Auth:App:FrontendUrls:ProviderReturn` or `Auth:Providers:Microsoft:ClientSecretExpiresOn` | The return address is not an absolute `https` address without a fragment, user information or a `token` or `ticket` parameter, or the date is not `yyyy-mm-dd` |
| Google shows `Error 400: redirect_uri_mismatch`; Microsoft shows an `AADSTS50011` page | The registered redirect URI differs from the one Auth-Core sends: compare them with the table above |
| Google shows `Error 401: invalid_client`; Microsoft shows `AADSTS700016` | The client id is wrong, or belongs to another project or tenant |
| The person comes back to your screen with `#error=provider_error`, and `$C logs auth` names `invalid_client` (the line `The google callback ended in provider_error (the client recorded the OAuth error invalid_client).`, or `microsoft`) | The client secret is wrong, or the Microsoft secret has lapsed ([`microsoft-secret.md`](../operations/microsoft-secret.md)) |
| The person comes back with `#error=invalid_state` | The round trip took longer than 10 minutes, was started in another browser, or the browser did not send the request-forgery cookie back (a browser that blocks cookies for the product's origin); the person starts again |
| Microsoft sign-ins of work accounts always lead to the link screen with the sentence about an unverified address | The optional claim `xms_edov` is missing (step 4 of Microsoft) |
