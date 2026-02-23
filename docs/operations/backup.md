# Backup and restore

For a server run with [`deploy/docker-compose.prod.yml`](../../deploy/docker-compose.prod.yml). The commands below are the ones
`scripts/e2e-prod.sh` (step 5) runs against a throwaway production stack: a dump, the database dropped, a restore, the service started
again. After them a refresh cookie issued before the backup still refreshes. In the commands `$C` stands for

```bash
C="docker compose -p auth-core-prod -f deploy/docker-compose.prod.yml --env-file .env"
```

run from the directory that holds `deploy/` and `.env`. Every command names the project, `auth-core-prod` (the same as
`COMPOSE_PROJECT_NAME=auth-core-prod` in the comments of the compose file): the name finds the stack and its volume
`auth-core-prod_postgres-data`, so use it every time, and never the name of another stack on the same host.

## What to back up

| What | Where it lives | Why |
| --- | --- | --- |
| The database | the `postgres-data` volume | Accounts, companies, members, sessions, the audit log |
| The key files | the directory named by `AUTH_KEYS_DIR` | Without the signing key every issued token is void; without the encryption key no session can be read. A restore needs the **same** keys |
| The manifest | the file named by `AUTH_MANIFEST` | The product's permissions and default roles |
| `.env` | next to your compose command | The passwords, the issuer and audience, the URLs |

Nothing else of Auth-Core is state: the rate-limit counters are in memory and the Data Protection keys are in memory too.

Keep the key files and `.env` **apart from the dump**: a dump holds every account's password hash, and the keys sign tokens. Encrypt what
leaves the host (for example with `age` or `gpg`) and test a restore at least once a quarter.

## A nightly backup

```bash
#!/usr/bin/env bash
# /usr/local/bin/auth-core-backup: a dump of the database, kept for 14 days. Run from cron as the user that runs docker compose.
set -euo pipefail
cd /srv/auth-core                      # the directory with deploy/ and .env
C="docker compose -p auth-core-prod -f deploy/docker-compose.prod.yml --env-file .env"
mkdir -p backups
umask 077
$C exec -T postgres pg_dump -U auth -d auth --format=custom --no-owner > "backups/auth-$(date +%F).dump"
find backups -name 'auth-*.dump' -mtime +14 -delete
```

```cron
17 3 * * *  /usr/local/bin/auth-core-backup
```

The custom format is compressed and restores with `pg_restore`; `--no-owner` lets it restore into the role `auth` whatever the dump was
made as. A failed dump leaves a short or empty file: the script stops at the failed command (`set -e`), but look at the size of the newest
dump now and then. Copy `backups/` and (once, and after every key rotation) the key files, the manifest and `.env` off the host. Gather them
in a directory of their own, apart from the dump, then encrypt it and copy it away (the paths are those of `AUTH_KEYS_DIR`,
`AUTH_MANIFEST` and your `.env`):

```bash
umask 077
mkdir -p secrets-backup
cp -a /etc/auth-core/keys secrets-backup/keys      # the directory named by AUTH_KEYS_DIR
cp /etc/auth-core/auth.yaml secrets-backup/auth.yaml   # the file named by AUTH_MANIFEST
cp .env secrets-backup/env
```

## Restore, step by step

A database was lost or damaged. Take the newest dump you trust (`auth-DATE.dump`), and the key files, manifest and `.env` that were in
use when it was made.

1. **Stop the service**, so that nothing writes while you restore:

   ```bash
   $C stop auth
   ```

2. **Recreate the database** (the connection is to the `postgres` database, so the target can be dropped):

   ```bash
   $C exec -T postgres psql -U auth -d postgres -c 'DROP DATABASE IF EXISTS auth WITH (FORCE)'
   $C exec -T postgres psql -U auth -d postgres -c 'CREATE DATABASE auth OWNER auth'
   ```

3. **Restore the dump**:

   ```bash
   $C exec -T postgres pg_restore -U auth -d auth --no-owner --exit-on-error < backups/auth-DATE.dump
   ```

4. **Start the service.** It migrates at start; a restored database is already at its migration, so nothing is applied unless you restored
   a dump of an older version, which then migrates forward:

   ```bash
   $C start auth
   ```

5. **Wait for a login, not for `/auth/health`.** The health check does not reach the database, so it answers while the service
   cannot reach PostgreSQL. A login does: it writes the attempt, so it is `500` until the database answers and `401` after. Ask with an
   address nobody has, on the published port, until it says `401`:

   ```bash
   curl -s -o /dev/null -w '%{http_code}\n' -X POST -H 'Content-Type: application/json' \
     -d '{"email":"nobody@example.invalid","password":"not-a-password"}' http://127.0.0.1:8080/auth/login
   ```

   (Use your `AUTH_PORT` when it is not 8080. The failed attempt is one `login.failed` row.) Then check the data:

   ```bash
   $C run --rm -T --no-deps auth admin list-orgs      # the companies: id, name, members
   ```

   Then sign in as someone and refresh. **Every session still valid at the time of the backup is valid now** (the refresh tokens are in
   the dump, the keys are the same); sessions started after the backup are gone, and so is everything else that happened after it.

If the **keys are lost** too: put new keys in place ([`key-rotation.md`](key-rotation.md)). Everyone signs in again; accounts, companies and
roles are intact.

If you restored into a **new server**: install Docker, put `deploy/`, `.env`, the keys and the manifest in place, run `$C up -d postgres`,
wait until `$C exec -T postgres pg_isready -h 127.0.0.1 -U auth -d auth` says it accepts connections (over TCP: on a fresh volume the image first
runs a temporary server that listens on its socket only), then do steps 2 and 3, and step 5. Skip step 1: there is no `auth` container yet to
stop. In step 4 use `$C up -d auth` instead of `$C start auth`: it creates the container, which `start` cannot.

A company deleted by mistake (`DELETE /auth/org`, or `delete-org`) has no undo but this restore, which brings back the whole database as
it was at the dump. The audit log shows when it happened (`org.deleted`, below), so you know which dump to take.

## Reading the audit log

The audit log is the table `audit_events`; it is read with SQL. Define once:

```bash
auth_psql() { docker compose -p auth-core-prod -f deploy/docker-compose.prod.yml --env-file .env exec -T postgres psql -U auth -d auth "$@"; }
```

and run a query by giving it on the standard input, `auth_psql <<'SQL'` followed by the query and a line `SQL`, or with `auth_psql -c "..."`.

The columns: `occurred_at`, `kind`, `actor_user_id` (the account that acted; empty for the operator CLI, an anonymous request or a failed
login), `subject_user_id`, `subject_email` (as stored, or as typed for an address that has no account), `org_id`, `org_name` (its name at that
moment), `target_id` (the role or invitation), `client_ip` (the whole address; `unknown` when there was none), `details` (a small JSON
object). A row outlives the account, company, role or invitation it names. It never holds a password, a token, a link, a cookie or a mail
body. Rows older than `AUTH_AUDIT_RETENTION_DAYS` (90 by default) are deleted every hour; a row exactly that old is still there.

What `details` holds, where it is set: `"via": "cli"` for the operator CLI, and `"via": "seed"` on the `org.created` row of the
development seeder (Development only); `"forced": true` on a `member.removed` row when the operator passed `--force`; `reason` on
`login.failed` and `org.delete_refused`; `retry_after_seconds` on `login.locked`; `policy` and `limit` on `rate_limit.hit`; the counts of members,
invitations and roles on `org.deleted`.

Two things to know when a row seems to be missing:

- A login to an account that has no password yet (an invited person who has not accepted) is recorded as `login.failed` with the reason
  `unknown_address`, the same as an address nobody has.
- `login.succeeded` and `logout` are written in the transaction of the session change: when the row cannot be written, the login or the
  logout fails with `500` and no session is issued or ended, so there is no session without its row. Every other event that changes nothing
  by itself (a failed login, a lock, a rate-limit hit, a reuse) is written on its own and never fails the request: when the database cannot
  take the row, the log of the `auth` container has EF Core's own error lines and one warning, `The audit event <kind> could not be
  written`, and the row is not retried. `rate_limit.hit` is written at most once per address (an IPv6 address by its `/64`) and policy per
  minute.

**Everything about an account** (the address as you know it; the first line finds the account's id):

```sql
WITH account AS (SELECT "Id" FROM "AspNetUsers" WHERE "NormalizedEmail" = upper('boss@acme.example'))
SELECT occurred_at, kind, actor_user_id, subject_user_id, subject_email, org_name, client_ip, details
FROM audit_events
WHERE subject_user_id IN (SELECT "Id" FROM account)
   OR actor_user_id   IN (SELECT "Id" FROM account)
   OR upper(subject_email) = upper('boss@acme.example')
ORDER BY occurred_at;
```

(For an address with letters outside A to Z, take the id from `list-orgs` and the log instead: `upper` follows the database's locale.)

**Every change in a company** (the company may be gone: find its id by name from the log itself):

```sql
SELECT DISTINCT org_id, org_name FROM audit_events WHERE org_name ILIKE '%acme%';

SELECT occurred_at, kind, actor_user_id, subject_email, target_id, details
FROM audit_events
WHERE org_id = '00000000-0000-0000-0000-000000000000'     -- the id from the query above
ORDER BY occurred_at;
```

**Failed logins from an address** (an IPv6 address is recorded whole; one subscriber holds a whole `/64`, so ask for the network):

```sql
SELECT occurred_at, subject_email, details->>'reason' AS reason
FROM audit_events
WHERE kind = 'login.failed' AND client_ip = '203.0.113.9'
ORDER BY occurred_at DESC LIMIT 200;

SELECT client_ip, count(*) FROM audit_events
WHERE kind = 'login.failed'
  AND (CASE WHEN client_ip ~ '^[0-9a-f:.]+$' THEN client_ip::inet END) <<= inet '2001:db8:1:2::/64'
GROUP BY client_ip ORDER BY 2 DESC;
```

Two more that are asked for often: who deleted a company, and who is hitting the limits.

```sql
SELECT occurred_at, kind, actor_user_id, org_name, client_ip, details FROM audit_events WHERE kind IN ('org.deleted', 'org.delete_refused') ORDER BY occurred_at DESC;

SELECT client_ip, details->>'policy' AS policy, count(*) FROM audit_events
WHERE kind = 'rate_limit.hit' AND occurred_at > now() - interval '1 day' GROUP BY 1, 2 ORDER BY 3 DESC;
```

The kinds are: `login.succeeded`, `login.failed` (reason: `wrong_password`, `unknown_address`, `unconfirmed_address`, `no_company`), `login.locked`, `logout`,
`refresh.reuse_detected`, `password.reset_requested`, `password.reset`, `email.verified`, `invite.sent`, `invite.resent`, `invite.accepted`,
`invite.cancelled`, `member.removed`, `member.role_changed`, `role.created`, `role.updated`, `role.deleted`, `org.created`, `org.renamed`,
`org.deleted`, `org.delete_refused` (reason: `wrong_password`, `locked`) and `rate_limit.hit` (at most one per address and policy per minute).

The address typed into a failed or locked login is kept as typed, and a person may have pasted a password into that field: treat the
audit log, and a dump that holds it, as private.
