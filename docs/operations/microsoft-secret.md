# The Microsoft client secret

A client secret of a Microsoft Entra app registration lapses, after 24 months at most. From that day every Microsoft sign-in fails: the
person comes back to the product's return screen with `#error=provider_error`, and `$C logs auth` names `invalid_client` in the callback's line
(`The microsoft callback ended in provider_error (the client recorded the OAuth error invalid_client).`). Nothing rotates it
for you. Google's client secret does not lapse.

The commands are for [`deploy/docker-compose.prod.yml`](../../deploy/docker-compose.prod.yml), run from the directory that holds `deploy/` and
`.env`; `$C` stands for the compose command with its project, as in [`backup.md`](backup.md):

```bash
C="docker compose -p auth-core-prod -f deploy/docker-compose.prod.yml --env-file .env"
```

## How you notice

- **Before:** with `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES` set to the secret's "Expires" date, `/auth/health` says `Degraded` from 30 days
  before that day (and after it), and each start in that time logs a warning naming the setting and the date
  (`$C logs auth | grep "client secret lapses"`). Watch `/auth/health` as you already do.
- **After:** `#error=provider_error` on every Microsoft round trip, and `invalid_client` in the log (`$C logs auth | grep callback`). Without the
  date setting this is the first sign.

## Rotate it

1. In the [Microsoft Entra admin center](https://entra.microsoft.com/), the app registration, "Certificates & secrets", "New client secret".
   Copy its **Value** at once and note its "Expires" date. The old secret keeps working until its own date: the two overlap.
2. In `.env` put the new value into `AUTH_MICROSOFT_CLIENT_SECRET` (`AUTH_MICROSOFT_CLIENT_SECRET=<the client secret>`) and the new date into
   `AUTH_MICROSOFT_CLIENT_SECRET_EXPIRES`.
3. `$C up -d` (a restart does not read `.env` again). `$C logs auth` shows no warning about the secret; `/auth/health` says `Healthy`.
4. Sign in once with Microsoft on the product.
5. Delete the old secret in Entra.

A person who is in the middle of a sign-in at the moment of the restart may have to start it again.

## Meanwhile

People who sign in only with Microsoft (no password) cannot sign in until the secret works again. They can use "Forgot your password?" on
the product's sign-in screen: the mail sets a password for their account, and they sign in with it. Their Microsoft login stays linked.
