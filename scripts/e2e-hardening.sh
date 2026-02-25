#!/usr/bin/env bash
# Real-network end-to-end check of spec 0008 (hardening) against the development compose stack, started with the DEFAULT rate limits.
#
# What it checks, in order (SEED = the development seed user; ADMIN = the first admin of a company made for the run):
#   1. the security headers on 200, HEAD, 404, 405, the key set and the OpenAPI document (the last two without Cache-Control) and no
#      Server header on the live server; HEAD /auth/health has an empty body; POST /auth/health is 405 with Allow: GET, HEAD; a body
#      declared charset=utf-16 is 415 on login, forgot, preview and DELETE /auth/org, charset=utf-8 is read; a body declared far over the size limit
#      is refused with invalid_request and the headers (Kestrel's 413, or the endpoint's own 400: TestServer enforces neither); the
#      interactive reference has its own policy with a nonce - criteria 4, 5, 6.
#   2. after a minute without requests: 30 logins of new unknown addresses (each with another forged X-Forwarded-For) are 401, the 31st
#      is 429 too_many_requests with Retry-After; it evaluated no password (no failed-login row); 30 failed-login rows and one
#      rate_limit.hit carry the same recorded address, which the client did not write - criteria 1, 2, 8.
#   3. refresh (60 a minute), mail requests (10), invitation requests (20) and the rest (300) each answer 429 at their own number; the
#      audit log has a rate_limit.hit row for each of the five policies - criteria 1, 8.
#   4. a refresh while PostgreSQL is stopped is 503 temporarily_unavailable with Retry-After: 5 and no Set-Cookie; the health check
#      still answers; a login is 500 internal_error; once PostgreSQL is back the same cookie refreshes (polled with a refresh: the
#      health check does not reach the database); a logout and a replayed refresh token leave their audit rows - criteria 3, 4, 8.
#   5. the operator makes a company and its admin, the admin has a pending invitation and the operator queues another; DELETE /auth/org
#      with a wrong password (403 wrong_password), a wrong name (400) and the right ones (204); then the old access token is 403
#      permissions_changed, the refresh 401 invalid_grant, the login 403 no_membership, the pending link invalid_token, the company's
#      rows and the queued mail are gone, and the accounts stay - criteria 10, 8. (That the queued mail is never sent is not checked here: the
#      dispatcher may send a queued request in the second before the deletion; CompanyDeletionTests pins it.)
#   6. the audit log holds a row of each kind the run produced, and none of the passwords, links or session tokens the run used - criterion 8.
#
# Full sequence, from the repo root. The older scripts run first on a stack whose limiter is off (the lockout script makes about 58
# logins a minute and the mail script exactly 10 requests for a mail); this one runs last on the same stack recreated WITH the defaults:
#   cp .env.example .env                  # then set real local values (git-ignored); leave the AUTH_RATE_LIMIT_* lines blank
#   MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh
#   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, for the whole sequence: never "auth-core", the development stack
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build
#   scripts/e2e-login.sh && scripts/e2e-refresh.sh && scripts/e2e-lockout.sh && scripts/e2e-email.sh && scripts/e2e-tenancy.sh
#   docker compose -f deploy/docker-compose.yml --env-file .env up -d            # recreates auth with the defaults (AUTH_RATE_LIMIT_ENABLED unset)
#   scripts/e2e-hardening.sh              # this script (does NOT bring the stack up or down; it stops and starts PostgreSQL)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# Reads AUTH_DEV_SEED_EMAIL and AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced). The operator commands run as
# `docker compose run --rm -T --no-deps auth admin ...`; the audit log is read with `docker compose exec postgres psql`. Needs: curl,
# python3 (standard library only), docker compose. Env: BASE_URL (default http://localhost:8080), MAILPIT_URL (default
# http://localhost:8025), COMPOSE_PROJECT_NAME (default auth-core-hardening: the project of the running stack, which this script stops
# PostgreSQL in, so "auth-core" is refused). Nothing else may send requests to the stack while it runs: the limits are per address.
# Takes about six minutes: two minutes of waiting for the limiter's windows to empty, and up to two for the mail of the new admin.
# Re-runnable on the same stack. Exits non-zero on the first failure; prints "PASS <step>"
# per step; never prints a password, a token, a cookie or a mail body. Request bodies are built into files in a mktemp -d
# directory (removed on exit) and curl reads them with --data-binary @file; tokens and cookies reach curl through header files;
# a secret that is looked for in the audit log goes to psql on its standard input, never on a command line.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
# Step 4 stops PostgreSQL of the stack: never of the development stack, whose project is "auth-core".
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-hardening}"
[[ "$COMPOSE_PROJECT_NAME" != "auth-core" ]] \
  || { echo "FAIL COMPOSE_PROJECT_NAME=auth-core is the development stack: this script stops its PostgreSQL; use another name" >&2; exit 1; }
compose=(docker compose -f "$root/deploy/docker-compose.yml" --env-file "$root/.env")

tmp="$(mktemp -d)"
POSTGRES_STOPPED=0
cleanup() {
  # a run that dies during step 4 must not leave PostgreSQL stopped
  if [[ "$POSTGRES_STOPPED" == "1" ]]; then
    "${compose[@]}" start postgres > /dev/null 2>&1 || echo "WARNING: PostgreSQL could not be started again: start it by hand (docker compose start postgres)" >&2
  fi
  rm -rf "$tmp"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

fail() { echo "FAIL $*" >&2; exit 1; }
pass() { echo "PASS $*"; }

env_get() { # read KEY from .env without executing it; strips one pair of surrounding quotes
  local line
  line="$(grep -E "^$1=" "$root/.env" | tail -n1 || true)"
  line="${line#*=}"
  line="${line%$'\r'}"
  if [[ "$line" =~ ^\"(.*)\"$ || "$line" =~ ^\'(.*)\'$ ]]; then line="${BASH_REMATCH[1]}"; fi
  printf '%s' "$line"
}

[[ -f "$root/.env" ]] || fail "missing $root/.env (copy .env.example and set local values)"
SEED_EMAIL="$(env_get AUTH_DEV_SEED_EMAIL)"
SEED_PASSWORD="$(env_get AUTH_DEV_SEED_PASSWORD)"
[[ -n "$SEED_EMAIL" && -n "$SEED_PASSWORD" ]] || fail "AUTH_DEV_SEED_EMAIL / AUTH_DEV_SEED_PASSWORD not set in .env"

run="$RANDOM$RANDOM"
ADMIN_EMAIL="boss-$run@hardening.test"
PENDING_EMAIL="pending-$run@hardening.test"
QUEUED_EMAIL="queued-$run@hardening.test"
ORG_NAME="Hardening $run"
WRONG_PASSWORD="Wrong-Password-1"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
export E2E_ADMIN_EMAIL="$ADMIN_EMAIL" E2E_PASSWORD="E2e-Passw0rd-$run" E2E_ORG_NAME="$ORG_NAME"

wait_healthy() {
  local i
  for i in $(seq 1 90); do
    if [[ "$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "$BASE_URL/auth/health" || true)" == "200" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

wait_mailpit() {
  local i
  for i in $(seq 1 60); do
    if [[ "$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "$MAILPIT_URL/readyz" || true)" == "200" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

# call <method> <path> [body-file] [auth-header-file] [content-type] -> sets HTTP_CODE and BODY; response headers in $tmp/hdr.
# EXTRA_HEADER, when set, is sent as one more header (a forged X-Forwarded-For).
call() {
  local args=(-sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' -X "$1" "$BASE_URL$2")
  if [[ -n "${3:-}" ]]; then args+=(-H "Content-Type: ${5:-application/json}" --data-binary "@$3"); fi
  if [[ -n "${4:-}" ]]; then args+=(-H "@$4"); fi
  if [[ -n "${EXTRA_HEADER:-}" ]]; then args+=(-H "$EXTRA_HEADER"); fi
  HTTP_CODE="$(curl "${args[@]}")" || fail "$1 $2: no answer (curl failed)"
  BODY="$(cat "$tmp/body")"
}

# call_head <path>: a HEAD request, headers in $tmp/hdr
call_head() {
  HTTP_CODE="$(curl -sS --max-time 20 -I -o /dev/null -D "$tmp/hdr" -w '%{http_code}' "$BASE_URL$1")"
  BODY=""
}

# head_body_bytes <path>: sends a raw HEAD request and prints "<status> <bytes after the header block>" (Kestrel sends none, whatever the
# in-memory test server of the .NET tests does); "0 -1" when the answer has no header block. The whole URL goes to python: a bare
# /auth/... argument would be rewritten into a Windows path by Git Bash when python is a Windows interpreter.
head_body_bytes() {
  python3 - "$BASE_URL$1" <<'PY' | tr -d '\r'
import socket, sys, urllib.parse
u = urllib.parse.urlparse(sys.argv[1])
target = u.path + ("?" + u.query if u.query else "")
s = socket.create_connection((u.hostname, u.port or 80), timeout=10)
s.sendall(("HEAD %s HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n" % (target, u.netloc)).encode("ascii"))
data = b""
try:
    while True:
        chunk = s.recv(65536)
        if not chunk:
            break
        data += chunk
except OSError:
    pass
head, sep, rest = data.partition(b"\r\n\r\n")
status = head.split(b" ")[1].decode("ascii") if sep else "0"
print(status, len(rest) if sep else -1)
PY
}

# oversize_post <path>: (the whole URL goes to python, as in head_body_bytes) a raw POST that DECLARES a body of 100 MB (far over Kestrel's 30 MB limit and the endpoints' own 8 KB) and sends
# two bytes of it, so that this script never has to send 100 MB. Sets HTTP_CODE and BODY; the response headers go to $tmp/hdr.
oversize_post() {
  HTTP_CODE="$(python3 - "$BASE_URL$1" "$tmp/hdr" "$tmp/body" <<'PY' | tr -d '\r'
import re, socket, sys, urllib.parse
u = urllib.parse.urlparse(sys.argv[1])
target = u.path + ("?" + u.query if u.query else "")
s = socket.create_connection((u.hostname, u.port or 80), timeout=10)
s.sendall(("POST %s HTTP/1.1\r\nHost: %s\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: 104857600\r\n\r\n{}"
           % (target, u.netloc)).encode("ascii"))
data = b""
try:
    while True:
        chunk = s.recv(65536)
        if not chunk:
            break
        data += chunk
        head, sep, rest = data.partition(b"\r\n\r\n")
        if sep:
            length = re.search(rb"(?im)^content-length:\s*(\d+)", head)
            if length and len(rest) >= int(length.group(1)):
                break
except OSError:
    pass
head, sep, rest = data.partition(b"\r\n\r\n")
if not sep:
    sys.exit("no answer to the oversized request")
if re.search(rb"(?im)^transfer-encoding:\s*chunked", head):
    # the body is in chunks: <hex size>CRLF<data>CRLF ... 0CRLF
    out, pos = b"", 0
    while True:
        end = rest.find(b"\r\n", pos)
        size = int(rest[pos:end].split(b";")[0] or b"0", 16) if end > pos else 0
        if size == 0:
            break
        out += rest[end + 2:end + 2 + size]
        pos = end + 2 + size + 2
    rest = out
lines = head.split(b"\r\n")
open(sys.argv[2], "wb").write(b"\r\n".join(lines[1:]) + b"\r\n")
open(sys.argv[3], "wb").write(rest)
print(lines[0].split(b" ")[1].decode("ascii"))
PY
)" || fail "the raw oversized request to $1 got no answer"
  BODY="$(cat "$tmp/body")"
}

header() { # value of a response header of the last response, CR stripped (empty when absent)
  { grep -i "^$1:" "$tmp/hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//'
}

# The python3 of some machines is a Windows interpreter, which ends its lines with CR LF: the helpers below that print a value
# strip the CR, so that the value compares equal in bash.

# val <python-expression-of-d>: evaluates it against the JSON of $BODY and prints the result
val() { python3 -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1" <<< "$BODY" | tr -d '\r'; }

json_body() { # json_body <file> <python-expression>: writes the JSON of the expression, which may read os.environ
  python3 -c 'import json,os,sys; print(json.dumps(eval(sys.argv[1])))' "$2" > "$1"
}

expect_status() { # expect_status <what> <code> [exact-body]
  [[ "$HTTP_CODE" == "$2" ]] || fail "$1: HTTP $HTTP_CODE, expected $2"
  if [[ $# -ge 3 ]]; then [[ "$BODY" == "$3" ]] || fail "$1: unexpected body"; fi
}

expect_error() { expect_status "$1" "$2" "{\"error\":\"$3\"}"; }

expect_eq() { [[ "$2" == "$3" ]] || fail "$1: got '$2', expected '$3'"; }

expect_no_cookie() { [[ -z "$(header set-cookie)" ]] || fail "$1: the answer sets a cookie"; }

# expect_security_headers <what> [cacheable]: every header of the table, no Server and no HSTS; Cache-Control: no-store with
# Pragma: no-cache, except on the key set and the OpenAPI document ("cacheable"), which send none.
expect_security_headers() {
  [[ "$(header x-content-type-options)" == "nosniff" ]] || fail "$1: X-Content-Type-Options is not nosniff"
  [[ "$(header x-frame-options)" == "DENY" ]] || fail "$1: X-Frame-Options is not DENY"
  [[ "$(header content-security-policy)" == "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'" ]] \
    || fail "$1: Content-Security-Policy is not the strict one"
  [[ "$(header referrer-policy)" == "no-referrer" ]] || fail "$1: Referrer-Policy is not no-referrer"
  [[ "$(header cross-origin-resource-policy)" == "same-origin" ]] || fail "$1: Cross-Origin-Resource-Policy is not same-origin"
  [[ -z "$(header server)" ]] || fail "$1: a Server header is sent"
  [[ -z "$(header strict-transport-security)" ]] || fail "$1: Auth-Core sends HSTS (the proxy does)"
  if [[ "${2:-}" == "cacheable" ]]; then
    [[ -z "$(header cache-control)" ]] || fail "$1: the answer sends a Cache-Control"
  else
    [[ "$(header cache-control)" == *no-store* ]] || fail "$1: Cache-Control is not no-store"
    [[ "$(header pragma)" == *no-cache* ]] || fail "$1: Pragma is not no-cache"
  fi
}

# expect_too_many_requests <what>: the 429 of the per-address limit, whole: body, Retry-After, no-store, the headers, no cookie
expect_too_many_requests() {
  [[ "$HTTP_CODE" == "429" ]] || fail "$1: HTTP $HTTP_CODE, expected 429"
  [[ "$BODY" =~ ^\{\"error\":\"too_many_requests\",\"retry_after_seconds\":([0-9]+)\}$ ]] || fail "$1: the body is not the 429 of the limiter"
  local seconds="${BASH_REMATCH[1]}"
  (( seconds >= 1 && seconds <= 60 )) || fail "$1: retry_after_seconds is $seconds, not between 1 and 60"
  [[ "$(header retry-after)" == "$seconds" ]] || fail "$1: Retry-After is not $seconds"
  expect_no_cookie "$1"
  expect_security_headers "$1"
}

# login <name> <email-env> <password-env>: logs in, keeps the bearer header in $tmp/<name>.auth and the cookie in $tmp/<name>.cookie
login() {
  json_body "$tmp/$1.login" "{'email': os.environ['$2'], 'password': os.environ['$3']}"
  call POST /auth/login "$tmp/$1.login"
  expect_status "login of $1" 200
  keep_session "$1"
}

SESSION_SECRETS=()   # every access token and refresh cookie value of the run: step 6 looks for them in the audit log (never printed)

keep_session() { # keep_session <name>: the access token of $BODY and the cookie of the last response
  local token pair
  token="$(val 'd["access_token"]')"
  printf 'Authorization: Bearer %s\n' "$token" > "$tmp/$1.auth"
  pair="$({ grep -i '^set-cookie:[[:space:]]*auth_rt=[^;]' "$tmp/hdr" || true; } | head -n1 | tr -d '\r' | cut -d: -f2- | sed 's/^ *//' | cut -d';' -f1)"
  [[ -n "$pair" ]] || fail "the response set no auth_rt cookie for $1"
  printf 'Cookie: %s\n' "$pair" > "$tmp/$1.cookie"
  SESSION_SECRETS+=("$token" "${pair#auth_rt=}")
}

# psql_value <sql>: the value the query returns. The SQL is this script's own text: a value that came from the service under test is
# never put into it (the run's own addresses and ids are). It goes in on the standard input.
psql_value() {
  printf '%s\n' "$1" | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'
}

# secret_in_audit <secret>: how many rows of the audit log hold the text in any column; the secret goes on stdin only
secret_in_audit() {
  [[ "$1" != *"'"* ]] || fail "a secret with a quote cannot be looked for"
  printf "SELECT count(*) FROM audit_events WHERE position('%s' in (kind || ' ' || coalesce(subject_email, '') || ' ' || coalesce(org_name, '') || ' ' || coalesce(client_ip, '') || ' ' || coalesce(details::text, ''))) > 0;\n" "$1" \
    | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'
}

# flood <method> <path> <times> [body]: one curl, the same request <times> times; the status of each is a line of $tmp/flood.codes
flood() {
  local args=(-sS --max-time 180 -w '%{http_code}\n' -X "$1") urls=() i
  if [[ -n "${4:-}" ]]; then args+=(-H 'Content-Type: application/json' --data-binary "$4"); fi
  # every URL has its own -o: curl pairs one -o with one URL, and the bodies of the others would go to the standard output
  for ((i = 0; i < $3; i++)); do urls+=(-o /dev/null "$BASE_URL$2"); done
  curl "${args[@]}" "${urls[@]}" | tr -d '\r' > "$tmp/flood.codes" || fail "the burst of $3 requests to $2 failed"
}

# first_429: the line number (the request number) of the first 429 in $tmp/flood.codes; fails when there is none, or when a status before it is not $1
first_429() {
  local n
  n="$(awk '$1 == 429 { print NR; exit }' "$tmp/flood.codes")"
  [[ -n "$n" ]] || fail "$2: no request of the burst was refused"
  if (( n > 1 )) && [[ -n "$(head -n "$((n - 1))" "$tmp/flood.codes" | grep -vx "$1" || true)" ]]; then
    fail "$2: before the first 429 some request answered something other than $1"
  fi
  echo "$n"
}

# --- the mail catcher ---------------------------------------------------------------------------------------------

mail_count() {
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" -o "$tmp/search.json"
  python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages_count"])' < "$tmp/search.json" | tr -d '\r'
}

# wait_mail <address> <count> <seconds>: waits until the catcher holds <count> mails for the address, then saves the newest
wait_mail() {
  local i id
  for i in $(seq 1 "$3"); do
    if [[ "$(mail_count "$1")" == "$2" ]]; then
      id="$(python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages"][0]["ID"])' < "$tmp/search.json" | tr -d '\r')"
      curl -sS --max-time 10 "$MAILPIT_URL/api/v1/message/$id" -o "$tmp/mail.json"
      return 0
    fi
    sleep 1
  done
  return 1
}

# token_body <file> [password-env]: takes the token of the invitation link from the text part of $tmp/mail.json and writes the
# request body ({"token"} or {"token","password"}). Never printed.
token_body() {
  python3 -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(r"http://localhost:4200/invite\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no invitation link with a token")
body = {"token": match.group(1)}
if len(sys.argv) > 1 and sys.argv[1]:
    body["password"] = os.environ[sys.argv[1]]
print(json.dumps(body))
' "${2:-}" < "$tmp/mail.json" > "$1"
}

# token_of <body-file>: the token of a body made by token_body (only to look for it in the audit log, never to print)
token_of() { python3 -c 'import json,sys; print(json.load(sys.stdin)["token"])' < "$1" | tr -d '\r'; }

# cli <args...>: runs `auth-server admin <args>` in a one-off container; sets CLI_OUT (stdout), CLI_EXIT, and keeps stderr in $tmp/cli.err
cli() {
  CLI_EXIT=0
  CLI_OUT="$("${compose[@]}" run --rm -T --no-deps auth admin "$@" 2> "$tmp/cli.err")" || CLI_EXIT=$?
  CLI_OUT="${CLI_OUT//$'\r'/}"
}

UUID_PATTERN='^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
uuid_or_fail() { [[ "$2" =~ $UUID_PATTERN ]] || fail "$1 is not a UUID"; }

wait_healthy || fail "$BASE_URL/auth/health did not return 200 within 90s (is the stack up? see the header)"
wait_mailpit || fail "$MAILPIT_URL/readyz did not return 200 within 60s (is the mailpit service up?)"
START_TS="$(psql_value "SELECT now()" || true)"
[[ -n "$START_TS" ]] || fail "the audit log cannot be read: is the compose project of the running stack the one this script uses? (set COMPOSE_PROJECT_NAME)"

# --- Step 1: the headers, the methods, the charset ----------------------------------------------------------------------------
call GET /auth/health
expect_status "step 1: GET /auth/health" 200 "Healthy"
expect_security_headers "step 1: GET /auth/health"
call_head /auth/health
expect_eq "step 1: HEAD /auth/health" "$HTTP_CODE" "200"
expect_security_headers "step 1: HEAD /auth/health"
expect_eq "step 1: the status and the bytes after the headers of a raw HEAD /auth/health" "$(head_body_bytes /auth/health)" "200 0"
call POST /auth/health
expect_status "step 1: POST /auth/health" 405
expect_eq "step 1: the Allow of POST /auth/health" "$(header allow)" "GET, HEAD"
expect_security_headers "step 1: POST /auth/health"
call GET /nope
expect_status "step 1: the framework's 404" 404
expect_security_headers "step 1: the framework's 404"
call GET /auth/logout
expect_status "step 1: the framework's 405" 405
expect_security_headers "step 1: the framework's 405"
call GET /auth/.well-known/jwks.json
expect_status "step 1: the key set" 200
expect_security_headers "step 1: the key set" cacheable
call GET /auth/openapi/v1.json
expect_status "step 1: the OpenAPI description" 200
expect_security_headers "step 1: the OpenAPI description" cacheable
json_body "$tmp/charset.json" "{'email': 'charset-$run@example.invalid', 'password': 'x'}"
call POST /auth/login "$tmp/charset.json" "" "application/json; charset=utf-16"
expect_error "step 1: a login body declared utf-16" 415 unsupported_media_type
expect_security_headers "step 1: the 415"
call DELETE /auth/org "$tmp/charset.json" "" "application/json; charset=utf-16"
expect_error "step 1: DELETE /auth/org with a body declared utf-16 (before the token is looked at)" 415 unsupported_media_type
call POST /auth/login "$tmp/charset.json" "" "application/json; charset=utf-8"
expect_error "step 1: a login body declared utf-8 is read" 401 invalid_credentials
expect_security_headers "step 1: the 401 of a login"
call POST /auth/login "$tmp/charset.json"
expect_error "step 1: a login body with no charset is read" 401 invalid_credentials
# The email flows and the invitations read JSON too: the same 415, checked before they do anything (no mail, no counter of the mail limits).
call POST /auth/password/forgot "$tmp/charset.json" "" "application/json; charset=utf-16"
expect_error "step 1: a forgot-password body declared utf-16" 415 unsupported_media_type
call POST /auth/invites/preview "$tmp/charset.json" "" "application/json; charset=utf-16"
expect_error "step 1: an invitation preview body declared utf-16" 415 unsupported_media_type
# A body declared far over the limit. Kestrel answers 413 when the body is read past its limit; the login endpoint checks the
# declared size against its own 8 KB first and answers 400. Both are the endpoint's invalid_request, with the headers and no cookie.
oversize_post /auth/login
[[ "$HTTP_CODE" == "413" || "$HTTP_CODE" == "400" ]] || fail "step 1: a login body declared as 100 MB: HTTP $HTTP_CODE, expected 413 or 400"
expect_eq "step 1: the error of the oversized login body" "$(val 'd["error"]')" "invalid_request"
expect_no_cookie "step 1: the oversized login body"
expect_security_headers "step 1: the oversized login body"
call GET /auth/health
expect_status "step 1: the server answers after the oversized body" 200 "Healthy"
# The interactive reference (Development): a policy of its own, with a nonce that is in the page. The slash matters: /auth/scalar redirects.
curl -sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" "$BASE_URL/auth/scalar/" || fail "step 1: the interactive reference did not answer"
POLICY="$(header content-security-policy)"
grep -Eq "^default-src 'none'; script-src 'self' 'nonce-[A-Za-z0-9+/=_-]+'; style-src 'self' 'unsafe-inline'; img-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'$" <<< "$POLICY" \
  || fail "step 1: the interactive reference has not the policy of the spec"
NONCE="$(sed -E "s/.*'nonce-([^']+)'.*/\1/" <<< "$POLICY")"
grep -q -F -- "$NONCE" "$tmp/body" || fail "step 1: the nonce of the policy is not in the page"
[[ "$(header x-frame-options)" == "DENY" && "$(header x-content-type-options)" == "nosniff" ]] || fail "step 1: the interactive reference lacks a header"
[[ "$(header referrer-policy)" == "no-referrer" && "$(header cross-origin-resource-policy)" == "same-origin" ]] || fail "step 1: the interactive reference lacks Referrer-Policy or Cross-Origin-Resource-Policy"
[[ "$(header cache-control)" == *no-store* && -z "$(header server)" ]] || fail "step 1: the interactive reference is cacheable or sends a Server header"
pass "step 1: the headers are on 200, HEAD, 404, 405, 415, the oversized body and the two documents (no Server header); HEAD has no body; POST /auth/health is 405 Allow: GET, HEAD; utf-16 is 415 on login, forgot, preview and DELETE /auth/org, utf-8 is read; the reference has its own policy"

# --- Step 2: the login limit -----------------------------------------------------------------------------------------------
# Step 1 made requests of the login and general policies: wait until the limiter's windows are empty.
echo "waiting a minute so that the limiter's windows are empty..."
sleep 61
for i in $(seq 1 30); do
  json_body "$tmp/flood.json" "{'email': 'nobody-$run-$i@example.invalid', 'password': '$WRONG_PASSWORD'}"
  EXTRA_HEADER="X-Forwarded-For: 198.51.100.$i" call POST /auth/login "$tmp/flood.json"
  expect_error "step 2: login $i of an unknown address" 401 invalid_credentials
done
json_body "$tmp/flood.json" "{'email': 'nobody-$run-31@example.invalid', 'password': '$WRONG_PASSWORD'}"
EXTRA_HEADER="X-Forwarded-For: 198.51.100.31" call POST /auth/login "$tmp/flood.json"
if [[ "$HTTP_CODE" == "401" ]]; then
  fail "step 2: the 31st login was not refused: is the limiter off? Recreate the stack without AUTH_RATE_LIMIT_ENABLED=false (see the header)"
fi
expect_too_many_requests "step 2: the 31st login"
[[ "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.failed' AND subject_email = 'nobody-$run-31@example.invalid'")" == "0" ]] \
  || fail "step 2: the refused login evaluated a password (it left a failed-login row)"
expect_eq "step 2: the failed logins that were evaluated" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.failed' AND details->>'reason' = 'unknown_address' AND subject_email LIKE 'nobody-$run-%@example.invalid'")" "30"
expect_eq "step 2: one recorded address for the 31 logins" \
  "$(psql_value "SELECT count(DISTINCT client_ip) FROM audit_events WHERE kind IN ('login.failed', 'rate_limit.hit') AND occurred_at >= '$START_TS' AND (subject_email LIKE 'nobody-$run-%@example.invalid' OR (kind = 'rate_limit.hit' AND details->>'policy' = 'login'))")" "1"
RECORDED="$(psql_value "SELECT client_ip FROM audit_events WHERE kind = 'login.failed' AND subject_email = 'nobody-$run-1@example.invalid'")"
[[ -n "$RECORDED" && "$RECORDED" != 198.51.100.* ]] || fail "step 2: the recorded address is one the client wrote into X-Forwarded-For ($RECORDED)"
expect_eq "step 2: one rate_limit.hit for the login policy" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'rate_limit.hit' AND details->>'policy' = 'login' AND occurred_at >= '$START_TS'")" "1"
pass "step 2: 30 logins are 401, the 31st is 429 with Retry-After and evaluated no password; the forged X-Forwarded-For changed neither the count nor the recorded address; one rate_limit.hit"

# --- Step 3: the other limits ----------------------------------------------------------------------------------------------------
flood POST /auth/refresh 70 '{}'
N="$(first_429 401 "step 3: refresh")"
(( N >= 56 && N <= 61 )) || fail "step 3: the first refused refresh was request $N, expected about the 61st"
call POST /auth/refresh "" ""
expect_too_many_requests "step 3: refresh"
for i in $(seq 1 10); do
  json_body "$tmp/mail.json.req" "{'email': 'mail-$run-$i@example.invalid'}"
  call POST /auth/password/forgot "$tmp/mail.json.req"
  expect_status "step 3: mail request $i" 202 ""
done
json_body "$tmp/mail.json.req" "{'email': 'mail-$run-11@example.invalid'}"
call POST /auth/password/forgot "$tmp/mail.json.req"
expect_too_many_requests "step 3: the 11th request for a mail"
expect_eq "step 3: the refused mail request wrote no password.reset_requested row" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'password.reset_requested' AND subject_email = 'mail-$run-11@example.invalid'")" "0"
flood POST /auth/invites/preview 25 '{"token":"not-a-token"}'
N="$(first_429 400 "step 3: invitation requests")"
(( N >= 16 && N <= 21 )) || fail "step 3: the first refused invitation request was request $N, expected about the 21st"
flood GET /auth/health 330
N="$(first_429 200 "step 3: general requests")"
(( N >= 290 && N <= 301 )) || fail "step 3: the first refused request was request $N, expected about the 301st"
expect_eq "step 3: a rate_limit.hit row for each of the five policies" \
  "$(psql_value "SELECT count(DISTINCT details->>'policy') FROM audit_events WHERE kind = 'rate_limit.hit' AND occurred_at >= '$START_TS'")" "5"
pass "step 3: refresh, mail, invitation and general requests are refused at their own numbers; one rate_limit.hit row for each of the five policies"

# Step 3 filled the windows of every policy: wait until they are empty.
echo "waiting a minute so that the limiter's windows empty..."
sleep 61

# --- Step 4: a refresh during an outage ------------------------------------------------------------------------------------
login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD
cp "$tmp/seed.cookie" "$tmp/seed.cookie.kept"
POSTGRES_STOPPED=1
"${compose[@]}" stop postgres > "$tmp/stop.log" 2>&1 || { cat "$tmp/stop.log" >&2; fail "step 4: could not stop PostgreSQL"; }
call POST /auth/refresh "" "$tmp/seed.cookie"
expect_error "step 4: a refresh while PostgreSQL is stopped" 503 temporarily_unavailable
expect_eq "step 4: its Retry-After" "$(header retry-after)" "5"
expect_no_cookie "step 4: the 503 of the refresh"
expect_security_headers "step 4: the 503 of the refresh"
call GET /auth/health
expect_status "step 4: the health check does not reach the database" 200 "Healthy"
json_body "$tmp/outage.login" "{'email': os.environ['E2E_SEED_EMAIL'], 'password': os.environ['E2E_SEED_PASSWORD']}"
call POST /auth/login "$tmp/outage.login"
expect_error "step 4: a login while PostgreSQL is stopped" 500 internal_error
expect_no_cookie "step 4: the 500 of the login"
expect_security_headers "step 4: the 500 of the login"
"${compose[@]}" start postgres > "$tmp/start.log" 2>&1 || { cat "$tmp/start.log" >&2; fail "step 4: could not start PostgreSQL again"; }
POSTGRES_STOPPED=0
ok=0
for _ in $(seq 1 60); do
  call POST /auth/refresh "" "$tmp/seed.cookie.kept"
  if [[ "$HTTP_CODE" == "200" ]]; then ok=1; break; fi
  expect_error "step 4: while PostgreSQL starts again" 503 temporarily_unavailable
  sleep 2
done
[[ "$ok" == "1" ]] || fail "step 4: the same cookie did not refresh within two minutes of PostgreSQL's start"
keep_session seed
# A logout and a replayed refresh token (the leeway is 15 seconds) leave their rows.
cp "$tmp/seed.cookie" "$tmp/seed.cookie.rotated"
call POST /auth/refresh "" "$tmp/seed.cookie"
expect_status "step 4: a refresh to be replayed" 200
sleep 16
call POST /auth/refresh "" "$tmp/seed.cookie.rotated"
expect_error "step 4: the replayed refresh token" 401 invalid_grant
login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD
call POST /auth/logout "" "$tmp/seed.cookie"
expect_status "step 4: logout" 204 ""
pass "step 4: with PostgreSQL stopped a refresh is 503 temporarily_unavailable (Retry-After 5, no cookie), health is 200, a login is 500 internal_error; afterwards the same cookie refreshes"

# --- Step 5: deleting a company -----------------------------------------------------------------------------------------------
cli create-org --name "$ORG_NAME"
[[ "$CLI_EXIT" == "0" ]] || fail "step 5: create-org failed"
ORG="$CLI_OUT"
uuid_or_fail "step 5: the id of the new company" "$ORG"
cli invite --org "$ORG" --email "$ADMIN_EMAIL" --role admin
[[ "$CLI_EXIT" == "0" ]] || fail "step 5: the invitation of the admin failed"
wait_mail "$ADMIN_EMAIL" 1 150 || fail "step 5: no invitation mail for the admin within 150s"
token_body "$tmp/accept.json" E2E_PASSWORD
ADMIN_LINK="$(token_of "$tmp/accept.json")"
call POST /auth/invites/accept "$tmp/accept.json"
expect_status "step 5: the admin accepts" 204 ""
login admin E2E_ADMIN_EMAIL E2E_PASSWORD
call GET /auth/org/roles "" "$tmp/admin.auth"
expect_status "step 5: GET /auth/org/roles" 200
export E2E_USER_ROLE
E2E_USER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "user"][0]')"
uuid_or_fail "step 5: the id of the user role" "$E2E_USER_ROLE"
json_body "$tmp/invite.json" "{'email': '$PENDING_EMAIL', 'role_id': os.environ['E2E_USER_ROLE']}"
call POST /auth/org/invites "$tmp/invite.json" "$tmp/admin.auth"
expect_status "step 5: the admin invites a person" 202 ""
wait_mail "$PENDING_EMAIL" 1 40 || fail "step 5: no invitation mail for the pending person within 40s (an API invitation wakes the dispatcher)"
token_body "$tmp/pending.json"
PENDING_LINK="$(token_of "$tmp/pending.json")"
call POST /auth/invites/preview "$tmp/pending.json"
expect_status "step 5: the pending link works before the deletion" 200
json_body "$tmp/delete.json" "{'name': os.environ['E2E_ORG_NAME'], 'password': '$WRONG_PASSWORD'}"
call DELETE /auth/org "$tmp/delete.json" "$tmp/admin.auth"
expect_error "step 5: DELETE /auth/org with a wrong password" 403 wrong_password
json_body "$tmp/delete.json" "{'name': 'not the name', 'password': os.environ['E2E_PASSWORD']}"
call DELETE /auth/org "$tmp/delete.json" "$tmp/admin.auth"
expect_error "step 5: DELETE /auth/org with a wrong name" 400 invalid_request
# Queued now, so that the deletion follows within a second: the server mails a queued request at its next poll, within a minute.
cli invite --org "$ORG" --email "$QUEUED_EMAIL" --role user   # queued; the server would mail it at its next poll
[[ "$CLI_EXIT" == "0" ]] || fail "step 5: the operator's invitation failed"
QUEUED_BEFORE="$(psql_value "SELECT count(*) FROM \"MailRequests\" WHERE \"NormalizedEmail\" = '${QUEUED_EMAIL^^}'")"
json_body "$tmp/delete.json" "{'name': os.environ['E2E_ORG_NAME'], 'password': os.environ['E2E_PASSWORD']}"
call DELETE /auth/org "$tmp/delete.json" "$tmp/admin.auth"
expect_status "step 5: DELETE /auth/org with the name and the password" 204 ""
if [[ "$QUEUED_BEFORE" != "1" ]]; then
  echo "NOTE step 5: the dispatcher sent the queued mail before the deletion; CompanyDeletionTests pins it"
fi
expect_security_headers "step 5: the 204"
call GET /auth/me "" "$tmp/admin.auth"
expect_error "step 5: the old access token" 403 permissions_changed
call POST /auth/refresh "" "$tmp/admin.cookie"
expect_error "step 5: the refresh of a member of a deleted company" 401 invalid_grant
expect_no_cookie "step 5: that refresh"
call POST /auth/login "$tmp/admin.login"
expect_error "step 5: a new login" 403 no_membership
call POST /auth/invites/preview "$tmp/pending.json"
expect_error "step 5: the pending invitation link" 400 invalid_token
for table in '"Companies" WHERE "Id"' '"CompanyRoles" WHERE "CompanyId"' '"Memberships" WHERE "CompanyId"' '"Invites" WHERE "CompanyId"'; do
  expect_eq "step 5: the rows of $table" "$(psql_value "SELECT count(*) FROM $table = '$ORG'")" "0"
done
expect_eq "step 5: the queued mail of the company's invitation" \
  "$(psql_value "SELECT count(*) FROM \"MailRequests\" WHERE \"NormalizedEmail\" = '${QUEUED_EMAIL^^}'")" "0"
expect_eq "step 5: the account of the admin stays" \
  "$(psql_value "SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = '${ADMIN_EMAIL^^}'")" "1"
expect_eq "step 5: the deletion is in the audit log" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'org.deleted' AND org_id = '$ORG' AND org_name = '$ORG_NAME' AND actor_user_id IS NOT NULL")" "1"
expect_eq "step 5: the refused deletion is in the audit log" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'org.delete_refused' AND org_id = '$ORG' AND details->>'reason' = 'wrong_password'")" "1"
pass "step 5: DELETE /auth/org (403 wrong_password, 400 wrong name, 204); then permissions_changed, invalid_grant, no_membership, an invalid link, no rows left, the queued mail gone, the account stays"

# --- Step 6: the audit log -------------------------------------------------------------------------------------------------------
KINDS="$(psql_value "SELECT string_agg(DISTINCT kind, ',' ORDER BY kind) FROM audit_events WHERE occurred_at >= '$START_TS'")"
for kind in login.succeeded login.failed logout refresh.reuse_detected password.reset_requested invite.sent invite.accepted org.created org.deleted org.delete_refused rate_limit.hit; do
  [[ ",$KINDS," == *",$kind,"* ]] || fail "step 6: this run left no audit row of the kind $kind (kinds: $KINDS)"
done
for secret in "$SEED_PASSWORD" "$E2E_PASSWORD" "$WRONG_PASSWORD" "$ADMIN_LINK" "$PENDING_LINK" "${SESSION_SECRETS[@]}"; do
  [[ -n "$secret" ]] || fail "step 6: a secret to look for is empty"
  expect_eq "step 6: rows of the audit log that hold a password, a link or a session token of this run" "$(secret_in_audit "$secret")" "0"
done
pass "step 6: the audit log has a row of each kind this run produced, and none of its passwords, links or session tokens"

echo "ALL PASS"
