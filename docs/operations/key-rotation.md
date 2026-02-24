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
| A person with an access token | It keeps working at product backends until it expires (10 minutes at most) or until the backend fetches the new key set. The package drops the old key at its next fetch: at once if a token names the new `kid` (at most one fetch per 10 seconds), otherwise at the first request after its 5-minute TTL, or only after up to 24 hours while Auth-Core is unreachable |
| A product backend | Nothing to do for a planned rotation. It sees the new `kid` at its next key fetch (`/auth/.well-known/jwks.json`). After a leak, restart it (see the emergency rotation) |
| Accounts, companies, roles, invitations, the audit log | Untouched |
| An invitation link or a reset link | Still works: those tokens are hashes in the database, not signed |

## Generating production keys

On a machine you trust, in a new directory. RSA of at least 2048 bits is required (the service refuses a shorter key at start); use 3072.
The key is a PEM RSA private key (`openssl req` writes it as PKCS#8), and the certificate must carry the key usage OpenIddict checks
(`digitalSignature` for signing, `keyEncipherment` for encryption).

```bash
(
  set -euo pipefail
  mkdir -p keys-new && cd keys-new
  umask 077
  openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=auth-core-signing" \
    -addext "keyUsage=critical,digitalSignature" -keyout signing.key -out signing.crt
  openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=auth-core-encryption" \
    -addext "keyUsage=critical,keyEncipherment" -keyout encryption.key -out encryption.crt
  openssl pkey -in signing.key -noout && openssl pkey -in encryption.key -noout && echo "keys are readable"
)
```

(The block runs in a subshell, so a failure, or the `cd`, does not touch your own shell: you are still in the directory you started from, and the next block runs from there, with the paths of the new directory spelled out.)

(On Git Bash for Windows prefix the two `openssl req` lines with `MSYS2_ARG_CONV_EXCL='/CN='`.) The certificates are containers for the
public keys: their names are not checked, **but their dates are**: OpenIddict refuses to work when no certificate is within its dates. If the
service starts but a login answers `500` and its log says "at least one of the registered certificates must be valid", the certificates
have lapsed (or are not yet valid). The certificates above are valid for ten years (`-days 3650`): write the date they lapse in your
calendar, and rotate before it. (Whether new certificates for the same keys would be enough has not been tried: rotate the keys.) The
container runs as the user with uid `1654`, so the files must be readable by it and by nobody else:

```bash
sudo chown 1654:1654 keys-new/signing.key keys-new/signing.crt keys-new/encryption.key keys-new/encryption.crt
sudo chmod 0400 keys-new/signing.key keys-new/encryption.key
sudo chmod 0444 keys-new/signing.crt keys-new/encryption.crt
sudo chmod 0555 keys-new
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
3. **Put the new keys in place, with `sudo`** (the key files are owned by uid `1654` and readable by it only). Generate them as in
   "Generating production keys" (that gives `keys-new`, with the owner and modes the container needs, from the directory you ran it in), then either
   copy its four files over the ones in `AUTH_KEYS_DIR` (`sudo cp -p keys-new/* <AUTH_KEYS_DIR>/`, the path from `.env`; `-p` keeps the owner and modes), or move it
   beside the old directory (`sudo mv keys-new /etc/auth-core/keys-2`) and change `AUTH_KEYS_DIR` in `.env` to it. Keep the old directory.
4. **Recreate the service** so that it reads them (the compose file mounts the directory read-only, and a running container does not notice a
   change; `--force-recreate` also picks up a new `AUTH_KEYS_DIR`):

   ```bash
   $C up -d --force-recreate auth
   ```

5. **Check the key set** (the `kid` must be new, see "Checking the key set") and sign in. If the service does not start, its log names the
   setting of the key file it could not use (`$C logs --tail 30 auth`); if it starts but a login answers `500` and the log says "at least
   one of the registered certificates must be valid", the certificates have lapsed (or are not yet valid). Wait for a login rather than for `/auth/health`, which does not reach the
   database ([`backup.md`](backup.md), step 5 of the restore), then sign in as someone and call a product endpoint.
6. **Watch the backends.** For up to five minutes a backend may still believe the old key for tokens issued before the change, and answers `401` for the
   new ones until it has fetched the new key set; with the package it is at most ten seconds (it fetches at once on an unknown `kid`, not more
   than once every 10 seconds). Every person who was signed in is asked to sign in again.
7. **Keep the old keys for a day**, in case you must roll back (step 8), then delete them securely.
8. **Rolling back** is the same as rotating: put the old files back, recreate the service. Sessions that were started with the new keys are signed out again.

## Emergency rotation: a key may have leaked

Do the planned steps at once, and also:

- If the **signing key** leaked, anyone can make tokens that products accept until their key caches drop the old key. To end that **at once**
  everywhere: recreate Auth-Core with the new keys, check that the key set shows the new `kid`, then restart every product backend at once.
  Its key cache is in memory (`clients/python/src/auth_core_fastapi/_jwks.py`), so the restarted backend fetches only the new key, and tokens
  signed with the old key are refused from then on. Changing the audience does not help: the old key can sign any audience.
- If the **encryption key** leaked, the refresh tokens in the database can be read; the rotation makes them unreadable, so every session ends.
- If the **database or `.env`** leaked as well: change the passwords of **both** database roles, the operator's superuser `auth` (`POSTGRES_PASSWORD`)
  and Auth-Core's own role (`AUTH_DB_APP_USER`, `AUTH_DB_APP_PASSWORD`), inside PostgreSQL and then in `.env`; change the relay's password
  (`AUTH_SMTP_PASSWORD`) at the relay and in `.env`; and consider every password hash exposed: ask people to reset their passwords (the audit
  log shows who signed in meanwhile). Each command asks for the new password itself, twice, so it is in no argument and no shell history
  (an empty answer removes the password: type one):

  ```bash
  $C exec postgres psql -U auth -d postgres -c '\password auth'
  $C exec postgres sh -c 'psql -U auth -d postgres -c "\password $AUTH_DB_APP_USER"'
  ```

  (Letters and digits only, at least 16, because the value is part of a connection string. Put both in `.env` at once, then `$C up -d`: compose
  recreates the services whose environment changed. `postgres` reads the two passwords only when the volume is first made, so recreating it
  changes nothing in the database; `auth` gets the new connection string. Until it does, its new connections fail.)
- Look at the audit log for the period ([`backup.md`](backup.md), "Reading the audit log"): `login.succeeded` from addresses you do not know, `refresh.reuse_detected`,
  `member.role_changed`, `invite.sent`.
- Write down what happened and when; the old keys are evidence.
