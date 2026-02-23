# Deploying Auth-Core on a server

From nothing to a first sign-in on a small Linux server (a VPS), from the published image. It is written for a server that runs Docker and a
reverse proxy; the proxy gives you HTTPS. Most of it is tried on a throwaway stack by `scripts/e2e-prod.sh` (the production compose
file, an SMTP relay that requires STARTTLS, a proxy in a container with HTTPS, the backup and the restore); the Caddyfile for a proxy on the host and the address
it arrives from are checked on your own server, in step 8.

Every compose command below names the project, `auth-core-prod`, the same as the `name:` of the file. The name finds the stack and its volume
(`auth-core-prod_postgres-data`), so use it every time and never the name of another stack on the same host. The commands are run from the
directory that holds `deploy/` and `.env`, and `$C` stands for

```bash
C="docker compose -p auth-core-prod -f deploy/docker-compose.prod.yml --env-file .env"
```

## What you need

- A Linux server with Docker and the Compose plugin (`docker compose version`), and `openssl`.
- A domain name for the product, say `app.example.com`, pointing at the server. Auth-Core lives at `/auth` of **that** origin, next to your product's
  backend at `/api` and its frontend ([ADR 0004](../adr/0004-same-origin-cookie-refresh.md)).
- An SMTP relay that offers TLS (STARTTLS on 587 or TLS on 465) and a sender address on a domain you control (SPF, DKIM and DMARC are the relay's and
  the domain's business). Auth-Core refuses to start with a relay that does not use TLS.
- A manifest for your product: its permissions and the default roles of a new company ([`docs/integration/python-fastapi.md`](../integration/python-fastapi.md), step 2).

## 1. Get the files

You need two files and nothing else from the repository: the compose file and the example of its environment. At a tag:

```bash
sudo mkdir -p /srv/auth-core && cd /srv/auth-core
sudo chown "$USER" /srv/auth-core
mkdir -p deploy
curl -fsSL https://raw.githubusercontent.com/MckCieply/Auth-Core/v0.1.0/deploy/docker-compose.prod.yml -o deploy/docker-compose.prod.yml
curl -fsSL https://raw.githubusercontent.com/MckCieply/Auth-Core/v0.1.0/deploy/.env.prod.example -o .env
chmod 600 .env
```

(Or clone the repository at the tag and use its `deploy/` directory.) The image `ghcr.io/mckcieply/auth-core:0.1.0` is public; `docker compose` pulls it.

## 2. The keys

Make the two RSA keys as [`docs/operations/key-rotation.md`](../operations/key-rotation.md) says ("Generating production keys"), into `/etc/auth-core/keys`, owned by
uid `1654` (the container's user), the private keys readable by it only. After the `chown` and `chmod` of that runbook, move the directory into place:
`sudo mkdir -p /etc/auth-core && sudo mv keys-new /etc/auth-core/keys`. Put a copy somewhere safe, apart from the server. The certificates have an end date:
write it in your calendar and rotate before it.

## 3. The manifest

Copy your product's `auth.yaml` to `/etc/auth-core/auth.yaml`. At least one default role must hold `members:manage` or `"*"`, so that the first admin of a
company can manage it. (`deploy/auth.yaml` in the repository is a development example.) The built-in permissions are `members:manage`, `roles:manage`,
`org:manage` and `org:delete`; a role with `"*"` holds all of them, **including the right to delete the company**.

The compose file mounts the key directory and the manifest from the paths in `.env` and never creates a missing one: a path that is not there stops the start
with "bind source path does not exist", instead of leaving the service on a directory that Docker made empty.

## 4. The environment

Edit `.env`. Every variable is explained in the file; these you must set:

| Variable | What |
| --- | --- |
| `POSTGRES_PASSWORD` | a long random value, letters and digits only |
| `AUTH_CORE_VERSION` | `0.1.0` |
| `AUTH_ISSUER` | `https://app.example.com/auth`: the origin your users see plus `/auth`. Your backend's package must be given the same value |
| `AUTH_AUDIENCE` | the audience your backend checks, for example `my-product-api` |
| `AUTH_KEYS_DIR`, `AUTH_MANIFEST` | the two paths above |
| `AUTH_APP_NAME`, `AUTH_APP_LOCALE` | the product's name in the mails, and `pl` or `en` |
| `AUTH_FRONTEND_RESET_URL`, `AUTH_FRONTEND_VERIFY_URL`, `AUTH_FRONTEND_INVITE_URL` | the three screens of **your frontend** that the mails link to, https |
| `AUTH_EMAIL_FROM`, `AUTH_SMTP_HOST`, `AUTH_SMTP_PORT`, `AUTH_SMTP_SECURITY`, `AUTH_SMTP_USERNAME`, `AUTH_SMTP_PASSWORD` | the relay |

The two SMTP credentials must both be **present** in `.env`: compose refuses to start when either line is missing. Leave both blank for a relay without
authentication.

### Who may tell Auth-Core the client address

Auth-Core limits and records requests per client address. It believes `X-Forwarded-For` only from the proxies you name, and by default the compose file names two:
`AUTH_PROXY_KNOWN_PROXIES` is `10.250.0.1,10.250.1.1`, the gateways (the first addresses) of its two networks, `10.250.0.0/24` (`AUTH_SUBNET`) and
`10.250.1.0/24` (`AUTH_PROXY_SUBNET`). A reverse proxy that runs **on this host** reaches the published port from the gateway of whichever of the two
networks Docker uses for it, so it is trusted without any change. If you change a subnet, change the address that names its gateway with it. If the proxy
runs in a container, see "A proxy in a container" under step 6.

- **Leaving the variable out keeps the default; writing `AUTH_PROXY_KNOWN_PROXIES=` with nothing after it trusts no proxy.** (This differs from the rate-limit
  variables, where blank means the default.) The example file has the default written out.
- **If no proxy is trusted, no forwarded header is read and every client looks like the proxy: all of them then share one set of rate limits** and the audit
  log records one address.
- The entries are written plainly: IPv4 as four decimal numbers, IPv6 without a `%scope`, a network as its base address and a prefix length of 1 or more
  (`10.250.1.0/24`). No short, hexadecimal or octal form, no IPv4-mapped IPv6 address, no `/0`. The service refuses to start otherwise and names the setting, never
  the value. A wide range such as `0.0.0.0/1` plus `128.0.0.0/1` is accepted and trusts every client: do not write one.
- **Do not set `ASPNETCORE_FORWARDEDHEADERS_ENABLED`.** It makes the framework trust every sender; the service refuses to start with it set.
- Which gateway a proxy on the host arrives from depends on the Docker engine. On a Linux server it is one of the two; `scripts/e2e-prod.sh` (step 6) prints it for
  the machine it runs on. Step 8 below shows what your own server records.
- A process on the host can reach the published port from a trusted gateway and so choose the address that is recorded for its own requests. The host is part of
  the trusted base ([`threat-model.md`](../security/threat-model.md), residual risks).

### The subnets

The two networks of the compose file have fixed subnets so that the trusted proxy can be named. They lie outside the ranges Docker takes its own networks from
(`172.17.0.0/16` to `172.31.0.0/16` and `192.168.0.0/16`): a fixed subnet taken from those can overlap a network Docker has already made, and Compose then stops
with "Pool overlaps". If `10.250.0.0/24` or `10.250.1.0/24` clashes with a network of yours (a VPN, an office network), change `AUTH_SUBNET`, `AUTH_PROXY_SUBNET` and
the addresses that name them (`AUTH_PROXY_KNOWN_PROXIES`, and the fixed address of a proxy in a container) together.

## 5. Start it

```bash
$C up -d
$C logs -f auth                                      # until you see it listening; Ctrl-C leaves the logs
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8080/auth/health      # 200 (use your AUTH_PORT when it is not 8080)
```

The service creates and migrates its database on its own at start. **`/auth/health` does not reach the database**, so a `200` there does not prove it is up. A login
does: it writes the attempt, so it is `500` until the database answers and `401` after. Ask with an address nobody has, until it says `401`:

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X POST -H 'Content-Type: application/json' \
  -d "{\"email\":\"nobody-$(date +%s)@example.invalid\",\"password\":\"not-a-password\"}" http://127.0.0.1:8080/auth/login
```

`000` or `500` means the service or the database does not answer yet: ask again every few seconds ([`backup.md`](../operations/backup.md), step 5 of the restore, has the
same check). A `429` (`too_many_requests`) means more than 30 logins a minute came from this host: wait the seconds in `Retry-After`, then ask again. When it says
`401` the service is ready; the first company is next. If the service does not start, `$C logs auth` names the setting that is wrong or
missing (it never prints a value): a key file that cannot be read, a mail relay without TLS, an `http` frontend URL, a missing issuer, a proxy entry written in a
form that is not accepted.

## 6. The reverse proxy and HTTPS

Auth-Core speaks plain HTTP on `127.0.0.1:8080` and expects TLS to end at your proxy. With Caddy (it gets and renews the certificate itself) a
`Caddyfile` for the product looks like this; it sends HSTS and the headers, and removes the `Server` header:

```
app.example.com {
	encode gzip
	header {
		Strict-Transport-Security "max-age=31536000"
		X-Content-Type-Options nosniff
		X-Frame-Options DENY
		Referrer-Policy no-referrer
		Permissions-Policy "camera=(), microphone=(), geolocation=(), payment=()"
		-Server
	}
	handle /auth/* {
		# Auth-Core limits and records requests per client address. Caddy (2.5 or newer) sets X-Forwarded-For to the address it saw and
		# X-Forwarded-Proto to the scheme, and drops what a client wrote into them, because this file names no trusted_proxies: do not
		# add header_up lines for them (they only produce a warning). Auth-Core believes Caddy's header because Caddy's address is in
		# AUTH_PROXY_KNOWN_PROXIES.
		reverse_proxy 127.0.0.1:8080
	}
	handle /api/* {
		reverse_proxy 127.0.0.1:8000        # your product's backend
	}
	handle {
		root * /srv/app                     # your frontend, built
		try_files {path} /index.html
		file_server
	}
}
```

Auth-Core sends no `Strict-Transport-Security` itself: the proxy does. A frontend needs its own `Content-Security-Policy` (the Angular sample's `Caddyfile` is an example;
the sample's headers and its client address rules are in the comments of its two `Caddyfile`s).
The mails' links open `AUTH_FRONTEND_*_URL`: they must be routes of your frontend. If another proxy or a CDN sits in front of Caddy, Caddy sees that proxy's address:
name it in Caddy's `trusted_proxies` so that Caddy hands on the real client's address (see Caddy's documentation), and never trust a range you do not control.

### A proxy in a container

A proxy that runs on this host (as above) needs nothing more. A proxy that runs in a container joins a network that the compose file of Auth-Core creates:
the network named by `AUTH_PROXY_NETWORK` (default `auth-core-proxy`), with the subnet `AUTH_PROXY_SUBNET` (default `10.250.1.0/24`). Declare it as an
**external** network in the proxy's own compose file, give the proxy a **fixed address** in that subnet, and use the service name `auth` as the address of
Auth-Core:

```yaml
# the proxy's own compose file
services:
  caddy:
    image: caddy:2            # pin it by digest, as the compose file of Auth-Core does
    networks:
      auth-core-proxy:
        ipv4_address: 10.250.1.10     # inside AUTH_PROXY_SUBNET, and not its gateway (10.250.1.1)
    # ... ports 80 and 443, the Caddyfile, a volume for /data
networks:
  auth-core-proxy:
    external: true
    name: auth-core-proxy     # the value of AUTH_PROXY_NETWORK
```

In the Caddyfile use `reverse_proxy auth:8080` (the rest is as above). In `.env` of Auth-Core trust that one address and nothing else:

```
AUTH_PROXY_KNOWN_PROXIES=10.250.1.10
AUTH_PROXY_KNOWN_NETWORKS=
```

An explicit value replaces the default, so the two gateways are no longer trusted (a proxy on the host would not be believed any more: this stack has one kind of proxy).
This is the pattern of the sample overlays, whose proxy has a fixed address too. **Do not trust the whole subnet** (`AUTH_PROXY_KNOWN_NETWORKS=10.250.1.0/24`): a
trusted subnet includes its gateway, which is the address a process on the host reaches the published port from, and such a process could then choose the address that
is recorded for its own requests (the residual risk of the threat model). Start Auth-Core first: the network exists once its compose file has been brought up, and
the proxy's compose file refuses an external network that does not exist.

## 7. The first company and its admin

```bash
$C run --rm -T --no-deps auth admin create-org --name "Acme"                              # prints the company id
$C run --rm -T --no-deps auth admin invite --org <id> --email boss@acme.example --role admin
$C run --rm -T --no-deps auth admin list-orgs
```

(`admin` is a role of your manifest that holds `members:manage` or `"*"`.) The invitation is mailed at the server's next poll, within a minute, and only while the
service runs. The admin follows the link to your frontend's invitation screen, chooses a password and signs in. Other members are invited from the company API
(`POST /auth/org/invites`) by anyone who holds `members:manage`. The commands never migrate the database: run them after step 5 says `401`.

## 8. Check it

```bash
curl -s -D - -o /dev/null https://app.example.com/auth/health    # 200, the security headers, Strict-Transport-Security from the proxy, no Server header
curl -s  https://app.example.com/auth/.well-known/jwks.json         # the public keys
curl -s  https://app.example.com/auth/openapi/v1.json | head -c 200  # the API description (the interactive reference is not served in Production)
```

Then check that the proxy is trusted: fail one login through the proxy and read the address that was recorded. It must be your own address, not `10.250.0.1` or
`10.250.1.1`:

```bash
curl -s -o /dev/null -X POST -H 'Content-Type: application/json' \
  -d '{"email":"probe@example.invalid","password":"not-a-password"}' https://app.example.com/auth/login
$C exec -T postgres psql -U auth -d auth -c "SELECT occurred_at, client_ip FROM audit_events WHERE kind = 'login.failed' ORDER BY occurred_at DESC LIMIT 1"
```

If it shows a gateway, the proxy's address is not in `AUTH_PROXY_KNOWN_PROXIES` (or the proxy runs in a container and its fixed address is not in `AUTH_PROXY_KNOWN_PROXIES`, or it has no fixed address): see "When
something is wrong". Then sign in through your frontend and call one product endpoint with the token.

## Running it

- **Limits.** 30 logins, 60 refreshes, 10 mail requests, 20 invitation requests and 300 other requests a minute per client address, answered `429 too_many_requests`
  with `Retry-After`. An office behind one NAT shares them: raise `AUTH_RATE_LIMIT_LOGIN` for it. A person who fails ten times is locked for a minute or more
  (`429 too_many_attempts`).
- **Backups.** A nightly dump and a restore that you have tried: [`docs/operations/backup.md`](../operations/backup.md).
- **The audit log** is the table `audit_events`, kept 90 days by default (`AUTH_AUDIT_RETENTION_DAYS`) and read with SQL: the queries are in the backup runbook.
- **Keys** change only by a rotation that signs everyone out: [`docs/operations/key-rotation.md`](../operations/key-rotation.md).
- **What can still go wrong** is listed in [`docs/security/threat-model.md`](../security/threat-model.md).

## Upgrading to a new version

1. Read the [changelog](../../CHANGELOG.md) for the version, and compare the `deploy/.env.prod.example` and `deploy/docker-compose.prod.yml` of its tag with your own
   files: a new required variable makes compose stop and name it.
2. **Back up** ([`backup.md`](../operations/backup.md)): a new version may migrate the database, and there is no downgrade of a migration.
3. Set `AUTH_CORE_VERSION` in `.env` to the new version and:

   ```bash
   $C pull auth
   $C up -d
   ```

   The service migrates the database at start. People stay signed in: the keys and the database are the same.
4. Wait for a login to answer `401` (step 5), then run `admin list-orgs` and sign in.
5. **Rolling back** is not a downgrade: restore the backup of step 2 with the old `AUTH_CORE_VERSION` ([`backup.md`](../operations/backup.md), "Restore").

## When something is wrong

| What you see | Why, and what to do |
| --- | --- |
| The `auth` container exits at start | `$C logs auth`: it names the setting (a key file, the relay's security, a frontend URL, the issuer or audience, a proxy entry, the database) |
| It says the setting `ASPNETCORE_FORWARDEDHEADERS_ENABLED` trusts every sender | Remove it from the `environment:` of the `auth` service (in `deploy/docker-compose.prod.yml` or an override file you added) or from a `run -e`; list the proxies in `AUTH_PROXY_KNOWN_PROXIES` and `AUTH_PROXY_KNOWN_NETWORKS` instead |
| `docker compose` says "required variable ... is missing" | A variable of `.env` has no default on purpose |
| "bind source path does not exist" | `AUTH_KEYS_DIR` or `AUTH_MANIFEST` names a path that is not there (compose never creates it) |
| "Pool overlaps" | A subnet of the compose file clashes with a network of the host: see "The subnets" |
| Everybody gets `429 too_many_requests` | The proxy is not trusted: every client looks like the proxy and shares its limits. Check `AUTH_PROXY_KNOWN_PROXIES` (the fixed address of a proxy in a container: see "A proxy in a container"), and that the proxy sets `X-Forwarded-For` |
| Refresh is `503 temporarily_unavailable` | The database cannot be reached; the cookie is kept. Look at `$C ps` and the logs of `postgres` |
| Mails do not arrive | `$C logs auth` shows each failed attempt (the dispatcher retries); check the relay's host, port, security, credentials and the sender's domain. The service verifies the relay's certificate against its chain **and for revocation**: the container needs outbound HTTP to the address of the certificate authority's revocation list (or OCSP responder), which is in the relay's certificate; a relay certificate that names no revocation list or OCSP responder is refused (the status is unknown); the read-only container cannot cache the list, so it fetches it at each connection |
| Signed-in people are asked to sign in again | The keys changed, or the database was restored from before their session |
| The recorded client address is always the same | The proxy is not trusted, or does not send `X-Forwarded-For`; see "Who may tell Auth-Core the client address" |
