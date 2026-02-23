# Key rotation

Auth-Core signs its access tokens with an RSA key and protects its refresh tokens with another; both live in four files that the
container mounts read-only (`AUTH_KEYS_DIR` in `.env`): `signing.crt`, `signing.key`, `encryption.crt`, `encryption.key`. **There is no
rotation without signing everyone out.** The service keeps no previous keys for verification or decryption (spec 0008, Decision 7), and
rotation is not automatic. Plan it for a quiet hour; the cost is that every person signs in again.

The commands are for [`deploy/docker-compose.prod.yml`](../../deploy/docker-compose.prod.yml), run from the directory that holds `deploy/` and
`.env`; `$C` stands for the compose command with its project, as in [`backup.md`](backup.md):

```bash
C="docker compose -p auth-core-prod -f deploy/docker-compose.prod.yml --env-file .env"
```

## What a key change does

| Who | What happens |
| --- | --- |
| Everyone with a session | Their refresh token was signed and encrypted with the old keys and cannot be read with the new ones: the next refresh is `401 invalid_grant`, and the app signs them out. They sign in again with their password |
| A person with an access token | It keeps working at product backends until it expires (10 minutes at most) or until the backend fetches the new key set. The package fetches it at most every 10 seconds when it meets an unknown `kid` and every 5 minutes anyway, so its old key stops being believed within 5 minutes of its next fetch |
| A product backend | Nothing to do. It sees the new `kid` at its next key fetch (`/auth/.well-known/jwks.json`) |
| Accounts, companies, roles, invitations, the audit log | Untouched |
| An invitation link or a reset link | Still works: those tokens are hashes in the database, not signed |

## Generating production keys

On a machine you trust, in a new directory. RSA of at least 2048 bits is required (the service refuses a shorter key at start); use 3072.
The key is a PEM RSA private key (`openssl req` writes it as PKCS#8), and the certificate must carry the key usage OpenIddict checks
(`digitalSignature` for signing, `keyEncipherment` for encryption).

```bash
set -euo pipefail
mkdir -p keys-new && cd keys-new
umask 077
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=auth-core-signing" \
  -addext "keyUsage=critical,digitalSignature" -keyout signing.key -out signing.crt
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=auth-core-encryption" \
  -addext "keyUsage=critical,keyEncipherment" -keyout encryption.key -out encryption.crt
openssl pkey -in signing.key -noout && openssl pkey -in encryption.key -noout && echo "keys are readable"
```

(On Git Bash for Windows prefix the two `openssl req` lines with `MSYS2_ARG_CONV_EXCL='/CN='`.) The certificates are containers for the
public keys: their names are not checked, **but their dates are**: OpenIddict refuses a certificate that is not yet valid or has expired,
so the service cannot use keys whose certificates have lapsed. The certificates above are valid for ten years (`-days 3650`): write the
date they lapse in your calendar, and rotate before it. The container runs as the user with uid `1654`, so the files must be readable by
it and by nobody else:

```bash
sudo chown 1654:1654 signing.key signing.crt encryption.key encryption.crt
sudo chmod 0400 signing.key encryption.key
sudo chmod 0444 signing.crt encryption.crt
sudo chmod 0555 .
```

Never use the development keys of `scripts/dev-keys.sh` (they are world-readable on purpose). Keep a copy of every set of keys somewhere safe, apart from
the database dumps ([`backup.md`](backup.md)): a restore needs the keys that were in use when the dump was made.

## Checking the key set

The key set of a running service lists the `kid` of the signing key. A new key pair has a new `kid`. Read it from the service itself, and from
the public origin to see that the proxy passes it:

```bash
curl -s http://127.0.0.1:8080/auth/.well-known/jwks.json | grep -o '"kid" *: *"[^"]*"'
curl -s https://app.example.com/auth/.well-known/jwks.json | grep -o '"kid" *: *"[^"]*"'
```

(Use your `AUTH_PORT` and your own origin.) Both lines must show the same `kid`.

## Planned rotation

1. **Back up first**: the database ([`backup.md`](backup.md)), and the current key directory. Record the `kid` the key set shows now (see
   "Checking the key set").
2. **Announce it.** People are signed out within the access token's life, 10 minutes, or at their next refresh.
3. **Put the new keys in place.** Either replace the four files in `AUTH_KEYS_DIR`, or generate them in a new directory and change `AUTH_KEYS_DIR`
   in `.env` to it. Keep the old directory.
4. **Recreate the service** so that it reads them (the compose file mounts the directory read-only, and a running container does not notice a
   change; `--force-recreate` also picks up a new `AUTH_KEYS_DIR`):

   ```bash
   $C up -d --force-recreate auth
   ```

5. **Check the key set** (the `kid` must be new, see "Checking the key set") and sign in. If the service does not start, its log names the
   setting of the key file it could not use (`$C logs --tail 30 auth`). Wait for a login rather than for `/auth/health`, which does not reach the
   database ([`backup.md`](backup.md), step 5 of the restore), then sign in as someone and call a product endpoint.
6. **Watch the backends.** For up to five minutes a backend may still believe the old key for tokens issued before the change, and answers `401` for the
   new ones until it has fetched the new key set; with the package it is at most ten seconds (it fetches at once on an unknown `kid`, not more
   than once every 10 seconds). Every person who was signed in is asked to sign in again.
7. **Keep the old keys for a day**, in case you must roll back (step 8), then delete them securely.
8. **Rolling back** is the same as rotating: put the old files back, recreate the service. Sessions that were started with the new keys are signed out again.

## Emergency rotation: a key may have leaked

Do the planned steps at once, and also:

- If the **signing key** leaked, anyone can make tokens that products accept until their key caches drop the old key. To end that **at once**
  everywhere, change the audience too: set a new `AUTH_AUDIENCE` in `.env` and in every backend that checks it (the package's `audience`),
  and restart both. Tokens with the old audience are refused by backends whatever their signature.
- If the **encryption key** leaked, the refresh tokens in the database can be read; the rotation makes them unreadable, so every session ends.
- If the **database or `.env`** leaked as well: change `POSTGRES_PASSWORD` (inside PostgreSQL, then in `.env`) and
  consider every password hash exposed: ask people to reset their passwords (the audit log shows who signed in meanwhile).

  ```bash
  $C exec -T postgres psql -U auth -d postgres <<'SQL'
  ALTER ROLE auth PASSWORD 'the-new-value';
  SQL
  ```

  (Letters and digits only, because the value is part of a connection string. Then put it in `.env` and recreate both services.)
- Look at the audit log for the period ([`backup.md`](backup.md), "Reading the audit log"): `login.succeeded` from addresses you do not know, `refresh.reuse_detected`,
  `member.role_changed`, `invite.sent`.
- Write down what happened and when; the old keys are evidence.
