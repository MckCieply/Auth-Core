#!/usr/bin/env bash
# Real-network check of the production compose file (spec 0008, criteria 12 and 13). It builds the image under the GHCR name, starts
# deploy/docker-compose.prod.yml with the test overlay (Mailpit with STARTTLS and a test authority, Caddy on https://localhost:8443) from an
# EMPTY volume, and drives it.
#
# What it checks, in order:
#   1. docker compose refuses to render the production file without its secrets, and the service refuses to start with a key file that
#      is not there and with a mail relay without TLS; the file does not render without the password of Auth-Core's database role.
#   2. the image carries the OCI labels (source, version, licenses MIT, revision); the container runs with a read-only root file system,
#      no capability and no privilege gain, and serves /auth/health; the headers of the table on a 200 (nosniff, X-Frame-Options DENY,
#      the Content-Security-Policy, Referrer-Policy no-referrer, Cross-Origin-Resource-Policy same-origin, Cache-Control no-store,
#      Pragma no-cache), no Server header and no Strict-Transport-Security (HSTS is the proxy's); no interactive reference; a login
#      is answered (the database is reached and migrated: /auth/health does not say so); Auth-Core's database role is not a superuser,
#      owns the database and every table, cannot connect to the maintenance database, run a program or make a role (Decision 14).
#   3. the first company from the CLI (create-org prints the id), an invitation for its admin: the mail arrives over STARTTLS (Mailpit
#      refuses plain SMTP; the relay's certificate is checked for revocation against the list the test authority publishes, which the
#      container fetches over HTTP (it cannot cache it: the root file system is read-only)) and names the https frontend URL; no seed user exists.
#   4. through the proxy: the admin accepts, logs in (the refresh cookie is HttpOnly, Secure, SameSite=Strict, Path=/auth), refreshes;
#      HSTS is sent; a failed login through the proxy is recorded with the address the proxy saw, not the one the client wrote and not
#      the proxy's own. The proxy has a fixed address on the proxy network and that one address is all the service trusts (the pattern of
#      docs/deployment/vps.md, "A proxy in a container").
#   5. the backup runbook (docs/operations/backup.md), command for command: a dump, the database dropped, recreated and restored, the
#      service started again and waited for with the runbook's login (not /auth/health); the restored objects belong to Auth-Core's role
#      again (pg_restore --role), the role still has every property of step 2, a login and the refresh cookie issued BEFORE the backup work,
#      and the company is still there.
#   6. which gateway a proxy on the host arrives from: one failed login straight to the published port, with no X-Forwarded-For, is
#      recorded with the gateway of one of the two networks (10.250.0.1 or 10.250.1.1), the two addresses the compose file trusts BY DEFAULT
#      (a host proxy needs them). The networks of the container and the Docker version are printed. This is a check of the Docker of THIS
#      machine: Docker Desktop (Windows, macOS) is not a Linux VPS and may deliver the connection from another address (its own VM's); a
#      failure there is a finding about the default of AUTH_PROXY_KNOWN_PROXIES for that Docker, not about the script. This stack trusts only
#      its proxy's own address (step 4), so a second login from the host with a forged X-Forwarded-For must be recorded with the same
#      connection address, not the forged one: the host's gateway is not believed. It runs last so that steps 1 to 5 are not lost to it.
#
# Run from the repo root: scripts/e2e-prod.sh. Needs: docker (with buildx for the image build), openssl, curl, python3 (standard library
# only) and the host ports 8080 (Auth-Core, loopback; AUTH_PORT changes it), 8443 (Caddy) and 8025 (Mailpit) free. The compose subnets
# 10.250.0.0/24 and 10.250.1.0/24 must be free too (no other Auth-Core production stack on this host, no second run of this script).
# Env: COMPOSE_PROJECT_NAME (default auth-core-prodtest; it must be auth-core-prodtest or auth-core-prodtest-<suffix>, because the script
# runs `down -v` on its project: "auth-core", the development stack, and any other project are refused, and so is a project that already
# has containers or volumes), AUTH_PORT, E2E_PROD_SKIP_BUILD=1 (reuse an image already built under the name), E2E_KEEP_STACK=1 (leave the
# stack up, and keep the directory with the files it mounts). The addresses of the proxy (https://localhost:8443) and of the mail catcher
# (http://localhost:8025) are fixed, because the overlay and the Caddyfile fix their ports. The AUTH_* variables of the compose file that are set in your shell are unset: the stack
# gets the environment file this script makes and nothing else.
# Takes about five minutes (the first build longer; the invitation mail waits for the server's next poll, within a minute). Exits non-zero
# on the first failure; prints "PASS <step>" per step; never prints a password, a token, a cookie or a mail body. Keys, the test
# authority and the environment file are made in a mktemp -d directory (removed on exit, unless E2E_KEEP_STACK=1 keeps the stack: its
# containers mount files from there); request bodies and cookies go through files.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-prodtest}"
AUTH_PORT="${AUTH_PORT:-8080}"
# Fixed: the overlay publishes Caddy on 8443 and Mailpit on 8025, and the Caddyfile's site is https://localhost:8443.
PROXY_URL="https://localhost:8443"
MAILPIT_URL="http://localhost:8025"
DIRECT_URL="http://127.0.0.1:$AUTH_PORT"
# The test proxy's fixed address on the proxy network (inside AUTH_PROXY_SUBNET, not its gateway): the one address the service trusts, the
# pattern of docs/deployment/vps.md ("A proxy in a container"). Trusting the whole subnet would also trust its gateway.
PROXY_IP="10.250.1.10"
VERSION="0.0.0-prodtest"
IMAGE="ghcr.io/mckcieply/auth-core:$VERSION"

fail() { echo "FAIL $*" >&2; exit 1; }
pass() { echo "PASS $*"; }

# The script runs `down -v` on its project: only on a project of its own, named auth-core-prodtest or auth-core-prodtest-<suffix>
# (suffix of lower-case letters, digits and hyphens). Never on the development stack (auth-core) or on a project of something else.
[[ "$COMPOSE_PROJECT_NAME" =~ ^auth-core-prodtest(-[a-z0-9-]+)?$ ]] \
  || fail "COMPOSE_PROJECT_NAME must be auth-core-prodtest or auth-core-prodtest-<suffix> (suffix: a-z, 0-9, -): this script removes the volumes of its project; use such a name"
for tool in docker openssl curl python3; do command -v "$tool" > /dev/null || fail "$tool is not on the PATH"; done
# A project of that name that has containers (running or stopped) or volumes is not touched: the clean start and the final down -v would remove it.
if [[ -n "$(docker ps -aq --filter "label=com.docker.compose.project=$COMPOSE_PROJECT_NAME" 2>/dev/null || true)" \
   || -n "$(docker volume ls -q --filter "label=com.docker.compose.project=$COMPOSE_PROJECT_NAME" 2>/dev/null || true)" ]]; then
  fail "a project named $COMPOSE_PROJECT_NAME already has containers or volumes: this script would remove them (down -v); remove them yourself (docker compose -p $COMPOSE_PROJECT_NAME down -v) or pick another name, e.g. COMPOSE_PROJECT_NAME=auth-core-prodtest-mine $0"
fi

# The docker of a Windows machine wants Windows paths for files it mounts.
host_path() { if command -v cygpath > /dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

tmp="$(mktemp -d)"
pki="$tmp/pki"
keys="$tmp/keys"
backup="$tmp/backup"
mkdir -p "$pki/relay" "$pki/crl" "$keys" "$backup"
# Every compose command names its project (-p) as well as the variable: the production file has a name of its own.
compose=(docker compose -p "$COMPOSE_PROJECT_NAME" -f "$(host_path "$root/deploy/docker-compose.prod.yml")" -f "$(host_path "$root/scripts/prod-test.compose.yml")" --env-file "$(host_path "$tmp/prod.env")")

STACK_STARTED=0
cleanup() {
  if [[ "$STACK_STARTED" == "1" && "${E2E_KEEP_STACK:-0}" == "1" ]]; then
    # The containers mount files from $tmp (the keys, the test authority's certificate, the relay's certificate and key, the list): it stays with the stack.
    echo "stack kept: files in $tmp; remove with: docker compose -p $COMPOSE_PROJECT_NAME down -v; rm -rf $tmp" >&2
    return
  fi
  if [[ "$STACK_STARTED" == "1" ]]; then "${compose[@]}" down -v > /dev/null 2>&1 || true; fi
  rm -rf "$tmp"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# --- what the stack needs: a test authority and a certificate for the relay, the keys of the service, the environment ----------
mkcert() {  # MSYS2_ARG_CONV_EXCL keeps Git Bash from turning /CN=... into a path
  MSYS2_ARG_CONV_EXCL='/CN=' openssl "$@" > /dev/null 2>&1 || fail "openssl $1 failed"
}
mkcert req -x509 -newkey rsa:2048 -nodes -days 2 -subj "/CN=auth-core-prod-test-ca" \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" -keyout "$pki/ca.key" -out "$pki/ca.crt"
mkcert req -newkey rsa:2048 -nodes -subj "/CN=mailpit" -keyout "$pki/relay/mailpit.key" -out "$pki/mailpit.csr"
printf 'subjectAltName=DNS:mailpit\nextendedKeyUsage=serverAuth\ncrlDistributionPoints=URI:http://pki.test/ca.crl\n' > "$pki/mailpit.ext"
mkcert x509 -req -in "$pki/mailpit.csr" -CA "$pki/ca.crt" -CAkey "$pki/ca.key" -CAcreateserial -days 2 -extfile "$pki/mailpit.ext" -out "$pki/relay/mailpit.crt"
# The authority's certificate revocation list: empty, signed by the authority, valid for two days, in DER (what an HTTP distribution point
# serves). `openssl ca -gencrl` wants a small configuration with the (empty) index of issued certificates and a CRL number. The service
# "pki" of the overlay serves the directory crl/ and nothing else.
: > "$pki/index.txt"
printf '01\n' > "$pki/crlnumber"
printf '[ca]\ndefault_ca = CA_default\n[CA_default]\ndatabase = index.txt\ncrlnumber = crlnumber\ndefault_md = sha256\ndefault_crl_days = 2\n' > "$pki/ca.cnf"
( cd "$pki" && mkcert ca -config ca.cnf -cert ca.crt -keyfile ca.key -gencrl -out ca.crl.pem )
mkcert crl -in "$pki/ca.crl.pem" -outform DER -out "$pki/crl/ca.crl"
mkcert verify -crl_check -CAfile "$pki/ca.crt" -CRLfile "$pki/ca.crl.pem" "$pki/relay/mailpit.crt"   # the certificate of the relay is good against this list
# The keys of the service, as docs/operations/key-rotation.md makes them: RSA 3072, PKCS#8, the key usage OpenIddict checks.
mkcert req -x509 -newkey rsa:3072 -nodes -days 30 -subj "/CN=auth-core-prodtest-signing" \
  -addext "keyUsage=critical,digitalSignature" -keyout "$keys/signing.key" -out "$keys/signing.crt"
mkcert req -x509 -newkey rsa:3072 -nodes -days 30 -subj "/CN=auth-core-prodtest-encryption" \
  -addext "keyUsage=critical,keyEncipherment" -keyout "$keys/encryption.key" -out "$keys/encryption.crt"
# Readable by the container's user (uid 1654) and by Mailpit: this is a throwaway directory.
chmod 0644 "$pki/ca.crt" "$pki"/relay/* "$keys"/*.crt "$keys"/*.key "$pki"/crl/ca.crl
chmod 0755 "$pki/relay" "$pki/crl" "$keys"

POSTGRES_PASSWORD="$(openssl rand -hex 16)"
APP_DB_PASSWORD="$(openssl rand -hex 16)"
# Auth-Core's database role: a name of its own (not the default), so that the variable is exercised by the compose file, the init script and the commands of the runbook.
APP_ROLE="auth_app_prodtest"
cat > "$tmp/prod.env" <<EOF
AUTH_CORE_VERSION=$VERSION
POSTGRES_PASSWORD=$POSTGRES_PASSWORD
AUTH_DB_APP_USER=$APP_ROLE
AUTH_DB_APP_PASSWORD=$APP_DB_PASSWORD
AUTH_ISSUER=$PROXY_URL/auth
AUTH_AUDIENCE=prod-test-api
AUTH_KEYS_DIR=$(host_path "$keys")
AUTH_MANIFEST=$(host_path "$root/deploy/auth.yaml")
AUTH_APP_NAME=Prod Test
AUTH_APP_LOCALE=en
AUTH_FRONTEND_RESET_URL=$PROXY_URL/reset
AUTH_FRONTEND_VERIFY_URL=$PROXY_URL/verify
AUTH_FRONTEND_INVITE_URL=$PROXY_URL/invite
AUTH_EMAIL_FROM=no-reply@prodtest.example
AUTH_SMTP_HOST=mailpit
AUTH_SMTP_PORT=1025
AUTH_SMTP_SECURITY=starttls
AUTH_SMTP_USERNAME=
AUTH_SMTP_PASSWORD=
AUTH_PORT=$AUTH_PORT
AUTH_SUBNET=10.250.0.0/24
AUTH_PROXY_SUBNET=10.250.1.0/24
AUTH_PROXY_NETWORK=$COMPOSE_PROJECT_NAME-proxy
AUTH_PROXY_KNOWN_PROXIES=$PROXY_IP
AUTH_PROXY_KNOWN_NETWORKS=
E2E_PROD_PROXY_IP=$PROXY_IP
E2E_PROD_PKI_DIR=$(host_path "$pki")
E2E_PROD_CADDYFILE=$(host_path "$root/scripts/prod-test.Caddyfile")
EOF
chmod 0600 "$tmp/prod.env"
# AUTH_PROXY_KNOWN_PROXIES is the proxy's own address, not the default of the file (both gateways): an explicit value replaces the default.
# A variable of the compose file that is set in this shell would win over the environment file: they are unset, so the stack gets exactly
# the file above.
while IFS='=' read -r name _; do unset "$name"; done < <(grep -hoE '^[A-Z][A-Z0-9_]*=' "$root/deploy/.env.prod.example" "$tmp/prod.env")

# --- helpers ---------------------------------------------------------------------------------------------------------------------
# call <method> <url-path> [body-file] [header-file]: through the proxy (https, the certificate of Caddy's own authority is accepted)
call() {
  local args=(-sS -k --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' -X "$1" "$PROXY_URL$2")
  if [[ -n "${3:-}" ]]; then args+=(-H 'Content-Type: application/json' --data-binary "@$3"); fi
  if [[ -n "${4:-}" ]]; then args+=(-H "@$4"); fi
  if [[ -n "${EXTRA_HEADER:-}" ]]; then args+=(-H "$EXTRA_HEADER"); fi
  HTTP_CODE="$(curl "${args[@]}")"
  BODY="$(cat "$tmp/body")"
}

# direct <path> [body-file]: straight to the published loopback port of the service (a POST when there is a body); no forwarded header
direct() {
  local args=(-sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}')
  if [[ -n "${2:-}" ]]; then args+=(-X POST -H 'Content-Type: application/json' --data-binary "@$2"); fi
  HTTP_CODE="$(curl "${args[@]}" "$DIRECT_URL$1")"
  BODY="$(cat "$tmp/body")"
}

header() { { grep -i "^$1:" "$tmp/hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//'; }
json_body() { python3 -c 'import json,os,sys; print(json.dumps(eval(sys.argv[1])))' "$2" > "$1"; }
expect_status() { [[ "$HTTP_CODE" == "$2" ]] || fail "$1: HTTP $HTTP_CODE, expected $2"; if [[ $# -ge 3 ]]; then [[ "$BODY" == "$3" ]] || fail "$1: unexpected body"; fi; }
expect_eq() { [[ "$2" == "$3" ]] || fail "$1: got '$2', expected '$3'"; }

wait_ok() { # wait_ok <seconds> <curl args...>: waits until the URL answers 200
  local deadline=$((SECONDS + $1)); shift
  while (( SECONDS < deadline )); do
    if [[ "$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "$@" || true)" == "200" ]]; then return 0; fi
    sleep 1
  done
  return 1
}

# Every docker call that talks to a container is bounded by timeout (coreutils; it works with a pipe and with a redirected standard input).
psql_value() { printf '%s\n' "$1" | timeout 120 "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'; }

recorded_ip() { # recorded_ip <email>: the client_ip of the newest failed login of that address; waits up to 15 s for the row
  local out deadline=$((SECONDS + 15))
  while (( SECONDS < deadline )); do
    out="$(psql_value "SELECT client_ip FROM audit_events WHERE kind = 'login.failed' AND subject_email = '$1' ORDER BY occurred_at DESC LIMIT 1" || true)"
    if [[ -n "$out" ]]; then printf '%s' "$out"; return 0; fi
    sleep 1
  done
  return 1
}

cli() { # cli <args...>: the operator's command in a one-off container of the production service; sets CLI_OUT and CLI_EXIT
  CLI_EXIT=0
  CLI_OUT="$(timeout 120 "${compose[@]}" run --rm -T --no-deps auth admin "$@" 2> "$tmp/cli.err")" || CLI_EXIT=$?
  CLI_OUT="${CLI_OUT//$'\r'/}"
}

# check_app_role <when>: Auth-Core's database role (spec 0008, Decision 14). It is not a superuser and has none of the other privileges that
# matter; it owns the database and every table, sequence and index of the schema public; PUBLIC may not connect to the database; the role
# itself may not connect to the maintenance database, run a program from the database or make a role. Read as the superuser, from inside the
# postgres container (the operator's way: the local socket needs no password). Called after the first start, where the service has migrated
# the database as the role, and again after the restore.
check_app_role() {
  local when="$1" out object
  out="$(psql_value "SELECT rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls, rolcanlogin FROM pg_roles WHERE rolname = '$APP_ROLE'")" \
    || fail "$when: the database could not be queried"
  expect_eq "$when: rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls and rolcanlogin of $APP_ROLE" "$out" "f|f|f|f|f|t"
  out="$(psql_value "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'auth'")" || fail "$when: the database could not be queried"
  expect_eq "$when: the owner of the database auth" "$out" "$APP_ROLE"
  out="$(psql_value "SELECT count(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind = 'r'")" || fail "$when: the database could not be queried"
  [[ "$out" =~ ^[0-9]+$ ]] && (( out >= 10 )) || fail "$when: the schema public holds '$out' tables: the database is not migrated"
  out="$(psql_value "SELECT count(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind IN ('r', 'S', 'i', 'v', 'm', 'p', 'f') AND pg_get_userbyid(relowner) <> '$APP_ROLE'")" \
    || fail "$when: the database could not be queried"
  expect_eq "$when: tables, sequences and indexes of the schema public that $APP_ROLE does not own" "$out" "0"
  for object in '"AspNetUsers"' '"OpenIddictTokens"' '"Companies"' audit_events; do
    out="$(psql_value "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relname = '${object//\"/}'")" \
      || fail "$when: the database could not be queried"
    expect_eq "$when: the owner of $object" "$out" "$APP_ROLE"
  done
  out="$(psql_value "SELECT has_database_privilege('pg_monitor', 'auth', 'CONNECT')")" || fail "$when: the database could not be queried"
  expect_eq "$when: PUBLIC may connect to the database auth" "$out" "f"
  if timeout 120 "${compose[@]}" exec -T postgres psql -U "$APP_ROLE" -d postgres -tA -c 'SELECT 1' > /dev/null 2>&1; then
    fail "$when: $APP_ROLE can connect to the maintenance database postgres"
  fi
  out="$(timeout 120 "${compose[@]}" exec -T postgres psql -U "$APP_ROLE" -d auth -tA -c "COPY (SELECT 1) TO PROGRAM 'true'" 2>&1 || true)"
  [[ "$out" == *"permission denied"* ]] || fail "$when: $APP_ROLE may run a program from the database (answer: $out)"
  out="$(timeout 120 "${compose[@]}" exec -T postgres psql -U "$APP_ROLE" -d auth -tA -c "CREATE ROLE e2e_not_allowed_$run" 2>&1 || true)"
  [[ "$out" == *"permission denied"* ]] || fail "$when: $APP_ROLE may make a role (answer: $out)"
}

run="$RANDOM$RANDOM"

# --- Step 1: the file and the service refuse to start without their secrets --------------------------------------------------------
: > "$tmp/empty.env"
if docker compose -p "$COMPOSE_PROJECT_NAME" -f "$(host_path "$root/deploy/docker-compose.prod.yml")" --env-file "$(host_path "$tmp/empty.env")" config > "$tmp/config.out" 2>&1; then
  fail "step 1: docker compose rendered the production file with no secrets"
fi
grep -q "required variable" "$tmp/config.out" || fail "step 1: docker compose did not say which variable is required"
# The password of Auth-Core's database role has no default: the file does not render without it.
grep -v '^AUTH_DB_APP_PASSWORD=' "$tmp/prod.env" > "$tmp/no-app-password.env"
if docker compose -p "$COMPOSE_PROJECT_NAME" -f "$(host_path "$root/deploy/docker-compose.prod.yml")" --env-file "$(host_path "$tmp/no-app-password.env")" config > "$tmp/config2.out" 2>&1; then
  fail "step 1: docker compose rendered the production file without AUTH_DB_APP_PASSWORD"
fi
grep -q "AUTH_DB_APP_PASSWORD" "$tmp/config2.out" || fail "step 1: docker compose did not name AUTH_DB_APP_PASSWORD as the missing variable"
if [[ "${E2E_PROD_SKIP_BUILD:-0}" != "1" ]]; then
  echo "building the image under its GHCR name (the first build takes a few minutes)..."
  docker build -q -f "$(host_path "$root/src/Auth.Server/Dockerfile")" --build-arg "VERSION=$VERSION" --build-arg "REVISION=$(git -C "$root" rev-parse HEAD)" \
    -t "$IMAGE" "$(host_path "$root")" > "$tmp/build.out" 2>&1 || { tail -n 20 "$tmp/build.out" >&2; fail "step 1: the image did not build"; }
fi
docker image inspect "$IMAGE" > /dev/null 2>&1 || fail "step 1: the image $IMAGE is not there (build it, or unset E2E_PROD_SKIP_BUILD)"
# From here compose makes things of the project (the networks and the volume of a one-off run): the trap removes them.
STACK_STARTED=1
# MSYS_NO_PATHCONV: Git Bash must not turn the path in the value into a Windows path.
if MSYS_NO_PATHCONV=1 timeout 120 "${compose[@]}" run --rm -T --no-deps -e Auth__Keys__SigningKeyPath=/nonexistent/signing.key auth > "$tmp/nokey.out" 2>&1; then
  fail "step 1: the service started with a signing key that is not there"
fi
grep -q "Auth:Keys" "$tmp/nokey.out" || fail "step 1: the refusal does not name the key setting"
if timeout 120 "${compose[@]}" run --rm -T --no-deps -e Auth__Email__Smtp__Security=none auth > "$tmp/nomail.out" 2>&1; then
  fail "step 1: the service started with a mail relay without TLS in Production"
fi
grep -q "Auth:Email:Smtp:Security" "$tmp/nomail.out" || fail "step 1: the refusal does not name the mail security setting"
pass "step 1: docker compose refuses the file without its secrets (the password of Auth-Core's database role among them); the service refuses a missing key file and a relay without TLS, naming the setting"

# --- Step 2: the image, the container, the headers ------------------------------------------------------------------------------------
labels="$(docker image inspect "$IMAGE" --format '{{json .Config.Labels}}' | tr -d '\r')" || fail "step 2: docker image inspect of $IMAGE failed"
for expected in '"org.opencontainers.image.source":"https://github.com/MckCieply/Auth-Core"' "\"org.opencontainers.image.version\":\"$VERSION\"" \
                '"org.opencontainers.image.licenses":"MIT"' '"org.opencontainers.image.revision":"'; do
  [[ "$labels" == *"$expected"* ]] || fail "step 2: the image has no label $expected"
done
"${compose[@]}" up -d > "$tmp/up.log" 2>&1 || { tail -n 20 "$tmp/up.log" >&2; fail "step 2: docker compose up failed"; }
wait_ok 120 "$DIRECT_URL/auth/health" || fail "step 2: $DIRECT_URL/auth/health did not return 200 within 120s (docker compose -p $COMPOSE_PROJECT_NAME logs auth)"
wait_ok 60 -k "$PROXY_URL/auth/health" || fail "step 2: $PROXY_URL/auth/health (through the proxy) did not return 200 within 60s"
wait_ok 60 "$MAILPIT_URL/readyz" || fail "step 2: the mail catcher did not answer within 60s"
auth_id="$("${compose[@]}" ps -q auth | tr -d '\r')" || fail "step 2: docker compose ps failed"
[[ -n "$auth_id" ]] || fail "step 2: no auth container"
# The inspect is captured first and the caller fails: a fail inside $(...) would print its FAIL line and then the caller's.
read_only="$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$auth_id" | tr -d '\r')" || fail "step 2: docker inspect of the auth container failed"
cap_drop="$(docker inspect -f '{{.HostConfig.CapDrop}}' "$auth_id" | tr -d '\r')" || fail "step 2: docker inspect of the auth container failed"
security_opt="$(docker inspect -f '{{.HostConfig.SecurityOpt}}' "$auth_id" | tr -d '\r')" || fail "step 2: docker inspect of the auth container failed"
expect_eq "step 2: the root file system is read-only" "$read_only" "true"
[[ "$cap_drop" == *ALL* ]] || fail "step 2: the container does not drop every capability"
[[ "$security_opt" == *no-new-privileges* ]] || fail "step 2: the container may gain privileges"
direct /auth/health
expect_status "step 2: /auth/health" 200 "Healthy"
# The table of the spec (Security headers on every response), on a 200 of the live server.
[[ -z "$(header server)" ]] || fail "step 2: the live server sends a Server header"
[[ -z "$(header strict-transport-security)" ]] || fail "step 2: Auth-Core sends HSTS"
expect_eq "step 2: X-Content-Type-Options" "$(header x-content-type-options)" "nosniff"
expect_eq "step 2: X-Frame-Options" "$(header x-frame-options)" "DENY"
expect_eq "step 2: Content-Security-Policy" "$(header content-security-policy)" "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"
expect_eq "step 2: Referrer-Policy" "$(header referrer-policy)" "no-referrer"
expect_eq "step 2: Cross-Origin-Resource-Policy" "$(header cross-origin-resource-policy)" "same-origin"
expect_eq "step 2: Cache-Control" "$(header cache-control)" "no-store"
expect_eq "step 2: Pragma" "$(header pragma)" "no-cache"
direct /auth/scalar/
expect_status "step 2: no interactive reference in Production" 404
direct /auth/openapi/v1.json
expect_status "step 2: the OpenAPI description is served in Production" 200
# /auth/health does not reach the database. A login does: an unknown user is a 401 once the database is there and migrated (the service
# migrates at its start, and the operator's commands refuse a database that is not migrated). The container has a read-only root file
# system: a login that is answered shows it works there.
json_body "$tmp/ready.json" "{'email': 'ready-$run@example.invalid', 'password': 'Wrong-Password-1'}"
ready=0
deadline=$((SECONDS + 120))
while (( SECONDS < deadline )); do
  direct /auth/login "$tmp/ready.json" || true
  if [[ "$HTTP_CODE" == "401" ]]; then ready=1; break; fi
  sleep 3
done
[[ "$ready" == "1" ]] || { "${compose[@]}" logs --no-color --tail 30 auth >&2 || true; fail "step 2: a login was not answered with 401 within 120s (HTTP $HTTP_CODE): the database is not reached or not migrated"; }
check_app_role "step 2"
pass "step 2: the image has its labels; the container is read-only with no capability and no privilege gain, serves /auth/health and answers a login (as a role that is not a superuser and owns the database and its tables); the headers of the table, no Server header; no interactive reference"

# --- Step 3: the first company from the CLI, the mail over STARTTLS ----------------------------------------------------------------------
ADMIN_EMAIL="boss-$run@prodtest.example"
export E2E_PASSWORD="E2e-Passw0rd-$run" E2E_LINK_PREFIX="$PROXY_URL/invite"
cli create-org --name "Acme $run"
[[ "$CLI_EXIT" == "0" ]] || { cat "$tmp/cli.err" >&2; fail "step 3: create-org failed"; }
ORG="$CLI_OUT"
[[ "$ORG" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || fail "step 3: create-org did not print a company id"
cli invite --org "$ORG" --email "$ADMIN_EMAIL" --role admin
[[ "$CLI_EXIT" == "0" ]] || { cat "$tmp/cli.err" >&2; fail "step 3: the invitation failed"; }
mail_ok=0
deadline=$((SECONDS + 150))
while (( SECONDS < deadline )); do
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$ADMIN_EMAIL" -o "$tmp/search.json" || true
  if [[ "$(python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages_count"])' < "$tmp/search.json" 2> /dev/null | tr -d '\r' || true)" == "1" ]]; then mail_ok=1; break; fi
  sleep 1
done
if [[ "$mail_ok" != "1" ]]; then
  # The lines of the service's log that speak of the handshake, the certificate or the list, and the requests the list's server saw.
  echo "--- the log of auth (certificate, revocation, TLS and file lines) ---" >&2
  { "${compose[@]}" logs --no-color --tail 300 auth 2>&1 || true; } | grep -iE 'revoc|crl|certificate|ssl|tls|pki|starttls|smtp|IOException|UnauthorizedAccess|read-only|denied' | tail -n 20 >&2 || true
  echo "--- the access log of pki ---" >&2
  { "${compose[@]}" logs --no-color --tail 20 pki 2>&1 || true; } >&2
  fail "step 3: no invitation mail arrived within 150s: the relay requires STARTTLS and the service must trust its test authority and accept its certificate, whose revocation it checks against the list at http://pki.test/ca.crl; if the log above shows the list was not fetched or not cached, the CRL fetch fails on the read-only root file system (the fix is a writable HOME for the cache, HOME: /tmp in the auth service of deploy/docker-compose.prod.yml, and a line in docs/deployment/vps.md that the relay's certificate must be checkable from the container)"
fi
# The list was really asked for: the access log of the small server that publishes it (Caddy's JSON log, "uri":"/ca.crl") shows the
# request of the service. A mail without that request means the revocation of the relay's certificate was not checked.
crl_requests="$({ "${compose[@]}" logs --no-color pki 2>&1 || true; } | grep -c '"uri":"/ca\.crl"' || true)"
if [[ "${crl_requests:-0}" == "0" ]]; then
  { "${compose[@]}" logs --no-color --tail 20 pki 2>&1 || true; } >&2
  fail "step 3: the mail arrived but pki saw no request for /ca.crl: the relay's revocation was not checked"
fi
mail_id="$(python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages"][0]["ID"])' < "$tmp/search.json" | tr -d '\r')" || fail "step 3: the search result holds no mail"
curl -sS --max-time 10 "$MAILPIT_URL/api/v1/message/$mail_id" -o "$tmp/mail.json" || fail "step 3: the mail could not be read from the catcher"
python3 -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(re.escape(os.environ["E2E_LINK_PREFIX"]) + r"\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no invitation link on the https frontend URL")
print(json.dumps({"token": match.group(1), "password": os.environ["E2E_PASSWORD"]}))
' < "$tmp/mail.json" > "$tmp/accept.json" || fail "step 3: the mail has no link on $PROXY_URL/invite"
json_body "$tmp/seed.json" "{'email': 'user@example.com', 'password': 'Correct-Horse-Battery-1'}"
call POST /auth/login "$tmp/seed.json"
expect_status "step 3: a development seed user does not exist in Production" 401
pass "step 3: create-org prints the id; the invitation mail arrives over STARTTLS (the relay's certificate passed its revocation check, the list was fetched $crl_requests time(s)) with a link on the https frontend URL; no seed user exists"

# --- Step 4: through the proxy -----------------------------------------------------------------------------------------------
call POST /auth/invites/accept "$tmp/accept.json"
expect_status "step 4: the admin accepts the invitation" 204 ""
json_body "$tmp/login.json" "{'email': '$ADMIN_EMAIL', 'password': os.environ['E2E_PASSWORD']}"
call POST /auth/login "$tmp/login.json"
expect_status "step 4: login through the proxy" 200
cookie_line="$({ grep -i '^set-cookie:[[:space:]]*auth_rt=' "$tmp/hdr" || true; } | head -n1 | tr -d '\r' | cut -d: -f2- | sed 's/^ *//')"
for attribute in 'HttpOnly' 'Secure' 'SameSite=Strict' 'Path=/auth'; do
  [[ "$cookie_line" == *"$attribute"* ]] || fail "step 4: the refresh cookie lacks $attribute"
done
[[ -n "$(header strict-transport-security)" ]] || fail "step 4: the proxy sends no HSTS over HTTPS"
printf 'Cookie: %s\n' "$(printf '%s' "$cookie_line" | cut -d';' -f1)" > "$tmp/before.cookie"
call POST /auth/refresh "" "$tmp/before.cookie"
expect_status "step 4: refresh through the proxy" 200
# The cookie that the backup must keep alive is the one of THIS login, not used for a refresh: a second login, kept for step 5.
call POST /auth/login "$tmp/login.json"
expect_status "step 4: a second login, for the backup" 200
printf 'Cookie: %s\n' "$({ grep -i '^set-cookie:[[:space:]]*auth_rt=' "$tmp/hdr" || true; } | head -n1 | tr -d '\r' | cut -d: -f2- | sed 's/^ *//' | cut -d';' -f1)" > "$tmp/session.cookie"
SPOOF_EMAIL="nobody-$run@example.invalid"
json_body "$tmp/spoof.json" "{'email': '$SPOOF_EMAIL', 'password': 'Wrong-Password-1'}"
EXTRA_HEADER="X-Forwarded-For: 203.0.113.77" call POST /auth/login "$tmp/spoof.json"
expect_status "step 4: a failed login through the proxy" 401
RECORDED="$(recorded_ip "$SPOOF_EMAIL" || true)"
caddy_id="$("${compose[@]}" ps -q caddy | tr -d '\r')" || fail "step 4: docker compose ps failed"
[[ -n "$caddy_id" ]] || fail "step 4: no caddy container"
CADDY_IP="$(docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$v.IPAddress}}{{end}}' "$caddy_id" | tr -d '\r')" || fail "step 4: docker inspect of the caddy container failed"
expect_eq "step 4: the proxy has the fixed address the service trusts" "$CADDY_IP" "$PROXY_IP"
proxy_env="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$auth_id" | tr -d '\r' | grep '^Auth__Proxy__' | sort)" \
  || fail "step 4: docker inspect of the auth container failed"
expect_eq "step 4: what the service trusts for the client address" "$proxy_env" "$(printf 'Auth__Proxy__KnownNetworks__0=\nAuth__Proxy__KnownProxies__0=%s' "$PROXY_IP")"
[[ -n "$RECORDED" ]] || fail "step 4: the failed login left no audit row with an address"
[[ "$RECORDED" != "203.0.113.77" ]] || fail "step 4: the recorded address is the one the client wrote"
[[ "$RECORDED" != "$CADDY_IP" ]] || fail "step 4: the recorded address is the proxy's own ($CADDY_IP): X-Forwarded-For was not honoured from the trusted proxy"
pass "step 4: accept, login and refresh through the proxy; the cookie is HttpOnly, Secure, SameSite=Strict, Path=/auth; HSTS; the recorded address ($RECORDED) is neither a forged one nor the proxy's own"

# --- Step 5: the backup runbook, command for command ----------------------------------------------------------------------------
# docs/operations/backup.md: the database is dumped with pg_dump in the custom format, no owner; the key files, the manifest and the
# environment file are copied. Then the disaster: the service stops, the database is dropped. The restore: recreate it owned by Auth-Core's
# role, pg_restore as the superuser with --role so that the restored objects belong to that role, start. The commands are the runbook's, word for word.
timeout 300 "${compose[@]}" exec -T postgres pg_dump -U auth -d auth --format=custom --no-owner > "$backup/auth.dump" \
  || fail "step 5: pg_dump failed"
[[ -s "$backup/auth.dump" ]] || fail "step 5: the dump is empty"
cp -a "$keys" "$backup/keys"
cp "$root/deploy/auth.yaml" "$backup/auth.yaml"
cp "$tmp/prod.env" "$backup/prod.env"
timeout 120 "${compose[@]}" stop auth > /dev/null 2>&1 || fail "step 5: could not stop the service"
timeout 120 "${compose[@]}" exec -T postgres psql -U auth -d postgres -c 'DROP DATABASE IF EXISTS auth WITH (FORCE)' > /dev/null || fail "step 5: could not drop the database"
timeout 120 "${compose[@]}" exec -T postgres sh -c 'psql -U auth -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE auth OWNER $AUTH_DB_APP_USER" -c "REVOKE CONNECT ON DATABASE auth FROM PUBLIC"' > /dev/null || fail "step 5: could not create the database"
timeout 300 "${compose[@]}" exec -T postgres sh -c 'pg_restore -U auth -d auth --no-owner --role="$AUTH_DB_APP_USER" --exit-on-error' < "$backup/auth.dump" || fail "step 5: pg_restore failed"
timeout 120 "${compose[@]}" start auth > /dev/null 2>&1 || fail "step 5: could not start the service again"
# The runbook's own readiness check (docs/operations/backup.md, step 5 of the restore): a login of an address nobody has, a fresh one each time,
# until it is a 401. /auth/health does not reach the database; a login does (000 or 500 until the service and the database answer).
ready=0
deadline=$((SECONDS + 120))
while (( SECONDS < deadline )); do
  json_body "$tmp/ready5.json" "{'email': 'nobody-$(date +%s)@example.invalid', 'password': 'not-a-password'}"
  direct /auth/login "$tmp/ready5.json" || true
  if [[ "$HTTP_CODE" == "401" ]]; then ready=1; break; fi
  sleep 3
done
[[ "$ready" == "1" ]] || { "${compose[@]}" logs --no-color --tail 30 auth >&2 || true; fail "step 5: a login was not answered with 401 within 120s of the start of the service (HTTP $HTTP_CODE)"; }
ok=0
deadline=$((SECONDS + 60))
while (( SECONDS < deadline )); do
  call POST /auth/refresh "" "$tmp/session.cookie" || true
  if [[ "$HTTP_CODE" == "200" ]]; then ok=1; break; fi
  sleep 2
done
[[ "$ok" == "1" ]] || fail "step 5: the refresh cookie issued before the backup does not refresh within 60s of the restore (HTTP $HTTP_CODE)"
cli list-orgs
[[ "$CLI_EXIT" == "0" && "$CLI_OUT" == *"$ORG"* ]] || fail "step 5: the company is not in the restored database"
# The runbook's own check of the owners (docs/operations/backup.md, step 5 of the restore): one line, Auth-Core's role.
owners="$(timeout 120 "${compose[@]}" exec -T postgres psql -U auth -d auth -tA -c "SELECT pg_get_userbyid(relowner), count(*) FROM pg_class WHERE relnamespace = 'public'::regnamespace GROUP BY 1" | tr -d '\r')" \
  || fail "step 5: the owners of the restored objects could not be read"
[[ "$owners" =~ ^${APP_ROLE}\|[0-9]+$ ]] || fail "step 5: the restored objects do not all belong to $APP_ROLE (owner and count: $owners)"
check_app_role "step 5"
# The role works after the restore: a login (a write of the lockout row and of the token entries) and a refresh as the role the service connects as.
call POST /auth/login "$tmp/login.json"
expect_status "step 5: a login after the restore" 200
pass "step 5: the runbook's dump, drop, restore and start brought the database back, owned by Auth-Core's role again: the cookie issued before the backup still refreshes and the company is there"

# --- Step 6: which gateway a proxy on the host arrives from ---------------------------------------------------------------------------
# One failed login straight to the published port, with no X-Forwarded-For: the recorded address is the address the connection came from.
# On a Linux host that is the gateway (first address) of the bridge Docker uses for the published port, one of the two the compose file
# trusts. Docker Desktop (Windows, macOS) is not a Linux VPS: its connections may arrive from its VM's address.
GW_EMAIL="gateway-$run@example.invalid"
json_body "$tmp/gateway.json" "{'email': '$GW_EMAIL', 'password': 'Wrong-Password-1'}"
direct /auth/login "$tmp/gateway.json"
expect_status "step 6: a failed login straight to the published port" 401
GW_IP="$(recorded_ip "$GW_EMAIL" || true)"
auth_id="$("${compose[@]}" ps -q auth | tr -d '\r')" || fail "step 6: docker compose ps failed"
[[ -n "$auth_id" ]] || fail "step 6: no auth container"
GW_NETWORKS="$(docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} (container {{$v.IPAddress}}, gateway {{$v.Gateway}}) {{end}}' "$auth_id" | tr -d '\r')" || fail "step 6: docker inspect of the auth container failed"
GW_DOCKER="$(docker version --format '{{.Server.Version}}' 2>/dev/null | tr -d '\r' || echo unknown)"
echo "step 6: the connection to ${DIRECT_URL} was recorded from '${GW_IP:-none}'; networks of the auth container: $GW_NETWORKS; Docker server $GW_DOCKER"
if [[ "$GW_IP" != "10.250.0.1" && "$GW_IP" != "10.250.1.1" ]]; then
  fail "step 6: a host proxy arrives from '${GW_IP:-no address}', which is neither 10.250.0.1 nor 10.250.1.1 (the two addresses AUTH_PROXY_KNOWN_PROXIES trusts by default): on this Docker (server $GW_DOCKER; Docker Desktop is not a Linux VPS) a proxy on the host would not be trusted, so every client would share one set of limits. Networks: $GW_NETWORKS"
fi
# The gateway is NOT trusted in this stack (only the proxy's own address is): a forged X-Forwarded-For sent from the host is not believed.
GW2_EMAIL="gateway-forged-$run@example.invalid"
json_body "$tmp/gateway2.json" "{'email': '$GW2_EMAIL', 'password': 'Wrong-Password-1'}"
curl -sS --max-time 20 -o /dev/null -H 'Content-Type: application/json' -H 'X-Forwarded-For: 203.0.113.88' --data-binary "@$tmp/gateway2.json" "$DIRECT_URL/auth/login" \
  || fail "step 6: the login with a forged X-Forwarded-For was not answered"
GW2_IP="$(recorded_ip "$GW2_EMAIL" || true)"
[[ -n "$GW2_IP" && "$GW2_IP" == "$GW_IP" ]] \
  || fail "step 6: a forged X-Forwarded-For sent from the host was recorded as '${GW2_IP:-no address}' instead of '$GW_IP': the host's gateway is trusted"
pass "step 6: a host proxy arrives from $GW_IP, one of the two gateways the compose file trusts (Docker server $GW_DOCKER); a forged X-Forwarded-For from the host is not believed (only the proxy's own address is trusted)"

echo "ALL PASS"
