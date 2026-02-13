#!/usr/bin/env bash
# Real-network end-to-end check of spec 0006 (the Python package and the "notes" sample) against the compose stack with
# the sample overlay: Auth-Core, PostgreSQL, the mail catcher (Mailpit), the notes service and Caddy. Everything goes
# through the proxy, as a browser would: http://localhost:8088/auth is Auth-Core, http://localhost:8088/api is the sample.
#
# This script STARTS the stack itself (docker compose up -d --build) and leaves it running. What it checks, in order
# (SEED = the development seed user, the admin of the development company A; B = a second company):
#   1. SEED logs in (the token carries notes:read and notes:write, the issuer is the proxy's origin), adds a note (201),
#      and the list holds it; the sample publishes no port of its own - criteria 1, 2 of the Goal sequence.
#   2. No token, a token with a changed signature and malformed headers each get 401, an empty body and
#      WWW-Authenticate: Bearer - criterion 3.
#   3. The operator creates company B with the CLI and invites its first admin; the admin accepts the invitation from the
#      mail in Mailpit and logs in. Their list is empty, and company A's note by its id is a 404 - criterion 8.
#   4. SEED invites a member with the role viewer through the company API; the viewer accepts and logs in. Reading is
#      200; adding a note is 403 forbidden - criterion 4.
#   5. SEED changes the viewer's role to user. The old token still says viewer; after a refresh the same person adds a
#      note (201).
#   6. Auth-Core is stopped and the sample is restarted, so it holds no keys: a valid token gets 503 auth_unavailable.
#      After Auth-Core starts again, the same request gets 200 (the sample asks for the keys again at most every
#      10 seconds, so the first answers after the restart may still be 503) - criterion 5.
#
# Full sequence, from the repo root (a clean stack: the development company is made from the manifest of the sample the
# first time the service starts, so a volume of an earlier stack that used another manifest makes step 1 fail):
#   cp .env.example .env                  # then set real local values (git-ignored); NOTES_DB_PASSWORD too
#   scripts/dev-keys.sh                   # dev signing/encryption keys into .secrets/ (git-ignored)
#   export COMPOSE_PROJECT_NAME=auth-core-notes   # the script's own project name (see below); set it for the down -v too
#   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
#   scripts/e2e-notes.sh                  # this script: brings the stack up (builds the images), then drives it
#   docker compose -f deploy/docker-compose.yml -f samples/notes-api/compose.yml --env-file .env down -v
# The script runs its stack as the compose project "auth-core-notes" (COMPOSE_PROJECT_NAME overrides it), not as "auth-core"
# that deploy/docker-compose.yml names: its containers and volumes stay apart from a development stack of the same
# clone, and the down -v above removes only them. (Host ports 8088, 8080 and 8025 are still shared: stop the other stack.)
# The earlier e2e scripts run on the stack WITHOUT the overlay (deploy/docker-compose.yml alone), as before.
#
# Reads AUTH_DEV_SEED_EMAIL, AUTH_DEV_SEED_PASSWORD and NOTES_DB_PASSWORD from the repo-root .env (parsed, never sourced).
# The operator commands run as `docker compose run --rm -T --no-deps auth admin ...` (spec 0005). Needs: curl, python3
# (standard library only), docker compose, and host ports 8088 (Caddy), 8080 (Auth-Core) and 8025 (Mailpit) free.
# Env: BASE_URL (default http://localhost:8088), MAILPIT_URL (default http://localhost:8025). The links in the mails start
# with http://localhost:4200/invite (appsettings.Development.json). Takes about three minutes, most of it the first image
# build and the wait for the server to pick up the invitation the CLI queued. Re-runnable on the same stack: every company
# and address is made for the run. Exits non-zero on the first failure; prints "PASS <step>" per step; never prints a
# password, a token, a cookie or a mail body. Request bodies are built into files in a mktemp -d directory (removed on
# exit) and curl reads them with --data-binary @file; tokens and cookies reach curl through header files.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8088}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
# deploy/docker-compose.yml names the project "auth-core", which every other stack of the clone uses; run apart from them
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-notes}"
compose=(docker compose -f "$root/deploy/docker-compose.yml" -f "$root/samples/notes-api/compose.yml" --env-file "$root/.env")

tmp="$(mktemp -d)"
AUTH_STOPPED=0
cleanup() {
  # a run that dies during step 6 must not leave Auth-Core stopped
  if [[ "$AUTH_STOPPED" == "1" ]]; then
    "${compose[@]}" start auth > /dev/null || echo "WARNING: Auth-Core could not be started again: start it by hand (docker compose start auth)" >&2
  fi
  rm -rf "$tmp"
}
trap cleanup EXIT

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
[[ -n "$(env_get NOTES_DB_PASSWORD)" ]] || fail "NOTES_DB_PASSWORD not set in .env (see .env.example)"

run="$RANDOM$RANDOM"
BADMIN_EMAIL="boss-$run@globex.test"
VIEWER_EMAIL="viewer-$run@acme.test"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
export E2E_BADMIN_EMAIL="$BADMIN_EMAIL" E2E_VIEWER_EMAIL="$VIEWER_EMAIL"
export E2E_PASSWORD="E2e-Passw0rd-$run"
NOTE_TEXT="hello from run $run"
export E2E_NOTE_TEXT="$NOTE_TEXT"

wait_ok() { # wait_ok <url> <seconds>: waits until the URL answers 200, for <seconds> by the clock (plus one last try)
  local deadline=$((SECONDS + $2))
  while (( SECONDS < deadline )); do
    if [[ "$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "$1" || true)" == "200" ]]; then
      return 0
    fi
    sleep 1
  done
  [[ "$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "$1" || true)" == "200" ]]
}

# call <method> <path> [body-file] [auth-header-file] -> sets HTTP_CODE and BODY; response headers in $tmp/hdr
call() {
  local args=(-sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' -X "$1" "$BASE_URL$2")
  if [[ -n "${3:-}" ]]; then args+=(-H 'Content-Type: application/json' --data-binary "@$3"); fi
  if [[ -n "${4:-}" ]]; then args+=(-H "@$4"); fi
  HTTP_CODE="$(curl "${args[@]}")"
  BODY="$(cat "$tmp/body")"
}

header() { # value of a response header of the last response, CR stripped (empty when absent)
  { grep -i "^$1:" "$tmp/hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//'
}

# The python3 of some machines is a Windows interpreter, which ends its lines with CR LF: the helpers below that print a
# value strip the CR, so that the value compares equal in bash.

# val <python-expression-of-d>: evaluates it against the JSON of $BODY and prints the result. The expression is the
# script's own text: a value that came from the service under test is never put into it, it is read from os.environ.
val() { python3 -c 'import json,os,sys; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1" <<< "$BODY" | tr -d '\r'; }

UUID_PATTERN='^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
uuid_or_fail() { # uuid_or_fail <what> <value>: a value of the service is used further on only when it is a UUID
  [[ "$2" =~ $UUID_PATTERN ]] || fail "$1 is not a UUID"
}

# jwt_payload <auth-header-file>: the payload of the bearer token in the file, as JSON (the token is never printed)
jwt_payload() {
  python3 -c '
import base64, json, sys
token = sys.stdin.read().split("Bearer ", 1)[1].strip().split(".")[1]
print(json.dumps(json.loads(base64.urlsafe_b64decode(token + "=" * (-len(token) % 4)))))
' < "$1" | tr -d '\r'
}

# changed_signature <auth-header-file> <out-file>: the same header with the first byte of the token's signature changed
changed_signature() {
  python3 -c '
import base64, sys
token = sys.stdin.read().split("Bearer ", 1)[1].strip()
head, payload, signature = token.split(".")
raw = bytearray(base64.urlsafe_b64decode(signature + "=" * (-len(signature) % 4)))
raw[0] ^= 0xFF
print("Authorization: Bearer " + ".".join([head, payload, base64.urlsafe_b64encode(bytes(raw)).rstrip(b"=").decode()]))
' < "$1" > "$2"
}

json_body() { # json_body <file> <python-expression>: writes the JSON of the expression, which may read os.environ
  python3 -c 'import json,os,sys; print(json.dumps(eval(sys.argv[1])))' "$2" > "$1"
}

expect_status() { # expect_status <what> <code> [exact-body]
  [[ "$HTTP_CODE" == "$2" ]] || fail "$1: HTTP $HTTP_CODE, expected $2"
  if [[ $# -ge 3 ]]; then [[ "$BODY" == "$3" ]] || fail "$1: unexpected body"; fi
}

expect_error() { expect_status "$1" "$2" "{\"error\":\"$3\"}"; }

expect_unauthorized() { # a 401 of the package: an empty body and WWW-Authenticate: Bearer
  [[ "$HTTP_CODE" == "401" ]] || fail "$1: HTTP $HTTP_CODE, expected 401"
  [[ -z "$BODY" ]] || fail "$1: the body of a 401 must be empty"
  [[ "$(header www-authenticate)" == "Bearer" ]] || fail "$1: WWW-Authenticate must be Bearer"
}

expect_eq() { [[ "$2" == "$3" ]] || fail "$1: got '$2', expected '$3'"; }

# login <name> <email-env> <password-env>: logs in, keeps the bearer header in $tmp/<name>.auth and the cookie in $tmp/<name>.cookie
login() {
  json_body "$tmp/$1.login" "{'email': os.environ['$2'], 'password': os.environ['$3']}"
  call POST /auth/login "$tmp/$1.login"
  expect_status "login of $1" 200
  keep_session "$1"
}

keep_session() { # keep_session <name>: the access token of $BODY and the cookie of the last response
  local access pair
  access="$(val 'd["access_token"]')" || fail "the response for $1 holds no access_token"
  [[ -n "$access" ]] || fail "the access_token for $1 is empty"
  printf 'Authorization: Bearer %s\n' "$access" > "$tmp/$1.auth"
  pair="$({ grep -i '^set-cookie:[[:space:]]*auth_rt=[^;]' "$tmp/hdr" || true; } | head -n1 | tr -d '\r' | cut -d: -f2- | sed 's/^ *//' | cut -d';' -f1)"
  [[ -n "$pair" ]] || fail "the response set no auth_rt cookie for $1"
  printf 'Cookie: %s\n' "$pair" > "$tmp/$1.cookie"
}

# refresh <name>: refreshes the session of <name> with its cookie (through the proxy: the cookie has Path=/auth); keeps the
# new tokens. Sets HTTP_CODE.
refresh() {
  call POST /auth/refresh "" "$tmp/$1.cookie"
  if [[ "$HTTP_CODE" == "200" ]]; then keep_session "$1"; fi
}

# --- the mail catcher ---------------------------------------------------------------------------------------------

mail_count() {
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" -o "$tmp/search.json"
  python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages_count"])' < "$tmp/search.json" | tr -d '\r'
}

# wait_mail <address> <count> <seconds>: waits until the catcher holds <count> mails for the address, then saves the newest
wait_mail() {
  local id deadline=$((SECONDS + $3))
  while (( SECONDS < deadline )); do
    if [[ "$(mail_count "$1")" == "$2" ]]; then
      id="$(python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages"][0]["ID"])' < "$tmp/search.json" | tr -d '\r')"
      curl -sS --max-time 10 "$MAILPIT_URL/api/v1/message/$id" -o "$tmp/mail.json"
      return 0
    fi
    sleep 1
  done
  return 1
}

# token_body <file> [password-env]: takes the token of the invitation link from the text part of $tmp/mail.json, checks that
# the HTML part holds the same token, and writes the request body ({"token"} or {"token","password"}). Never printed.
token_body() {
  python3 -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(r"http://localhost:4200/invite\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no invitation link with a token")
if match.group(1) not in mail["HTML"]:
    sys.exit("the HTML part does not hold the link of the text part")
body = {"token": match.group(1)}
if len(sys.argv) > 1 and sys.argv[1]:
    body["password"] = os.environ[sys.argv[1]]
print(json.dumps(body))
' "${2:-}" < "$tmp/mail.json" > "$1"
}

# --- the operator CLI, in the service's own image ----------------------------------------------------------------

# cli <args...>: runs `auth-server admin <args>` in a one-off container; sets CLI_OUT (stdout), CLI_EXIT, and keeps stderr in $tmp/cli.err
cli() {
  CLI_EXIT=0
  CLI_OUT="$("${compose[@]}" run --rm -T --no-deps auth admin "$@" 2> "$tmp/cli.err")" || CLI_EXIT=$?
  if [[ "$CLI_EXIT" != "0" ]]; then cat "$tmp/cli.err" >&2; fi
  CLI_OUT="${CLI_OUT//$'\r'/}"
}

# --- Step 1: the stack, SEED, the first note -------------------------------------------------------------------------
echo "bringing the stack up (the first build takes a few minutes)..."
"${compose[@]}" up -d --build > "$tmp/up.log" 2>&1 || { cat "$tmp/up.log" >&2; fail "step 1: docker compose up failed"; }
wait_ok "$BASE_URL/auth/health" 120 || fail "step 1: $BASE_URL/auth/health (Auth-Core through Caddy) did not return 200 within 120s"
wait_ok "$BASE_URL/api/health" 60 || fail "step 1: $BASE_URL/api/health (the sample through Caddy) did not return 200 within 60s"
wait_ok "$MAILPIT_URL/readyz" 60 || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s (is the mailpit service up?)"
SAMPLE_CONTAINER="$("${compose[@]}" ps -q notes-api)"
[[ -n "$SAMPLE_CONTAINER" ]] || fail "step 1: the notes-api service is not running"
# What the tokens must name is what the overlay configured the sample with (AUTH_ISSUER, AUTH_AUDIENCE), not a guess from BASE_URL.
# Only these two variables are read: the container's environment also holds the database URL.
sample_env() { docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$SAMPLE_CONTAINER" | tr -d '\r' | { grep -E "^$1=" || true; } | head -n1 | cut -d= -f2-; }
EXPECTED_ISSUER="$(sample_env AUTH_ISSUER)"
EXPECTED_AUDIENCE="$(sample_env AUTH_AUDIENCE)"
[[ -n "$EXPECTED_ISSUER" && -n "$EXPECTED_AUDIENCE" ]] || fail "step 1: the sample's AUTH_ISSUER or AUTH_AUDIENCE is not set"
[[ -z "$(docker port "$SAMPLE_CONTAINER")" ]] || fail "step 1: the notes service publishes a port of its own; it must be reachable only through Caddy"

login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD
# the refresh cookie as the browser gets it through the proxy: scoped to /auth and not readable by scripts
cookie_line="$({ grep -i '^set-cookie:[[:space:]]*auth_rt=' "$tmp/hdr" || true; } | head -n1 | tr -d '\r')"
cookie_attributes="$(printf '%s;' "$cookie_line" | tr 'A-Z' 'a-z' | tr -d ' ')"  # lower case, no spaces, a ; after each attribute
for attribute in 'Path=/auth' 'HttpOnly' 'Secure' 'SameSite=Strict'; do
  wanted="$(printf '%s' "$attribute" | tr 'A-Z' 'a-z')"
  [[ "$cookie_attributes" == *";$wanted;"* ]] || fail "step 1: the refresh cookie has no $attribute attribute"
done
BODY="$(jwt_payload "$tmp/seed.auth")"
SEED_SUB="$(val 'd["sub"]')"
expect_eq "step 1: issuer" "$(val 'd["iss"]')" "$EXPECTED_ISSUER"
expect_eq "step 1: audience (a JSON string)" "$(val 'd["aud"]')" "$EXPECTED_AUDIENCE"
[[ "$(val 'type(d["aud"]).__name__')" == "str" ]] || fail "step 1: the audience of the token is not a JSON string"
[[ "$(val '"notes:read" in d["permissions"] and "notes:write" in d["permissions"]')" == "True" ]] \
  || fail "step 1: the seed user's token does not carry notes:read and notes:write (a volume of an earlier stack? start from down -v)"
A="$(val 'd["org_id"]')"
call GET /auth/org/roles "" "$tmp/seed.auth"
expect_status "step 1: GET /auth/org/roles" 200
[[ "$(val 'sorted(r["name"] for r in d["roles"]) == ["admin", "user", "viewer"]')" == "True" ]] \
  || fail "step 1: the development company's roles are not the default roles of the notes manifest (start from down -v)"
USER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "user"][0]')"
VIEWER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "viewer"][0]')"
uuid_or_fail "step 1: the id of the role user" "$USER_ROLE"
uuid_or_fail "step 1: the id of the role viewer" "$VIEWER_ROLE"
export E2E_USER_ROLE="$USER_ROLE" E2E_VIEWER_ROLE="$VIEWER_ROLE"

json_body "$tmp/note.json" "{'text': os.environ['E2E_NOTE_TEXT']}"
call POST /api/notes "$tmp/note.json" "$tmp/seed.auth"
expect_status "step 1: add a note" 201
NOTE_ID="$(val 'd["id"]')"
uuid_or_fail "step 1: the id of the note" "$NOTE_ID"
export E2E_NOTE_ID="$NOTE_ID"
expect_eq "step 1: the note's text" "$(val 'd["text"]')" "$NOTE_TEXT"
expect_eq "step 1: the note's author" "$(val 'd["author_sub"]')" "$SEED_SUB"
call GET /api/notes "" "$tmp/seed.auth"
expect_status "step 1: list the notes" 200
[[ "$(val 'any(n["id"] == os.environ["E2E_NOTE_ID"] for n in d)')" == "True" ]] || fail "step 1: the list does not hold the note just added"
pass "step 1: the stack is up; the notes service publishes no port; SEED's token names the proxy's origin and carries notes:read and notes:write; a note was added (201) and is listed"

# --- Step 2: no token, a changed signature, malformed headers ---------------------------------------------------------
call GET /api/notes
expect_unauthorized "step 2: no token"
changed_signature "$tmp/seed.auth" "$tmp/seed-badsig.auth"
call GET /api/notes "" "$tmp/seed-badsig.auth"
expect_unauthorized "step 2: a changed signature"
printf 'Authorization: Bearer\n' > "$tmp/malformed-1.auth"
call GET /api/notes "" "$tmp/malformed-1.auth"
expect_unauthorized "step 2: a Bearer header without a token"
printf 'Authorization: Basic dXNlcjpwYXNz\n' > "$tmp/malformed-2.auth"
call GET /api/notes "" "$tmp/malformed-2.auth"
expect_unauthorized "step 2: a scheme other than Bearer"
printf 'Authorization: Bearer not.a.token\n' > "$tmp/malformed-3.auth"
call GET /api/notes "" "$tmp/malformed-3.auth"
expect_unauthorized "step 2: a token that is not a JWS"
call POST /api/notes "$tmp/note.json"
expect_unauthorized "step 2: adding a note without a token"
pass "step 2: no token, a changed signature and three malformed headers are each a 401 with an empty body and WWW-Authenticate: Bearer"

# --- Step 3: company B sees none of company A --------------------------------------------------------------------------
cli create-org --name "E2E Globex $run"
expect_eq "step 3: create-org exit code" "$CLI_EXIT" "0"
[[ "$CLI_OUT" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || fail "step 3: create-org printed more than the company id"
B="$CLI_OUT"
cli invite --org "$B" --email "$BADMIN_EMAIL" --role admin
expect_eq "step 3: invite exit code" "$CLI_EXIT" "0"
# The CLI only queues the mail: the server sends it at its next poll, within a minute.
wait_mail "$BADMIN_EMAIL" 1 120 || fail "step 3: no invitation mail within 120s of the CLI invitation"
token_body "$tmp/badmin-accept.json" E2E_PASSWORD || fail "step 3: no usable invitation link in the mail"
call POST /auth/invites/accept "$tmp/badmin-accept.json"
expect_status "step 3: B's admin accepts" 204 ""
login badmin E2E_BADMIN_EMAIL E2E_PASSWORD
BODY="$(jwt_payload "$tmp/badmin.auth")"
expect_eq "step 3: B's admin belongs to company B" "$(val 'd["org_id"]')" "$B"
[[ "$B" != "$A" ]] || fail "step 3: companies A and B are the same"
call GET /api/notes "" "$tmp/badmin.auth"
expect_status "step 3: B's list" 200 "[]"
call GET "/api/notes/$NOTE_ID" "" "$tmp/badmin.auth"
expect_error "step 3: company A's note, asked for by B" 404 not_found
call GET "/api/notes/not-a-uuid" "" "$tmp/badmin.auth"
expect_error "step 3: an id that is not a UUID" 404 not_found
call GET "/api/notes/$NOTE_ID" "" "$tmp/seed.auth"
expect_status "step 3: A still reads its own note" 200
pass "step 3: company B was made by the CLI, its admin accepted the mail and logged in; B's list is empty and A's note, by id, is a 404"

# --- Step 4: a viewer of company A reads and does not write ----------------------------------------------------------------
login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD  # a fresh token: step 3 may have waited a minute or two for the mail
json_body "$tmp/invite-viewer.json" "{'email': os.environ['E2E_VIEWER_EMAIL'], 'role_id': os.environ['E2E_VIEWER_ROLE']}"
call POST /auth/org/invites "$tmp/invite-viewer.json" "$tmp/seed.auth"
expect_status "step 4: invite the viewer" 202 ""
wait_mail "$VIEWER_EMAIL" 1 30 || fail "step 4: no invitation mail for the viewer within 30s (an API invitation wakes the dispatcher)"
token_body "$tmp/viewer-accept.json" E2E_PASSWORD
call POST /auth/invites/accept "$tmp/viewer-accept.json"
expect_status "step 4: the viewer accepts" 204 ""
login viewer E2E_VIEWER_EMAIL E2E_PASSWORD
BODY="$(jwt_payload "$tmp/viewer.auth")"
expect_eq "step 4: the viewer's permissions" "$(val 'd["permissions"]')" "['notes:read']"
expect_eq "step 4: the viewer's company" "$(val 'd["org_id"]')" "$A"
call GET /api/notes "" "$tmp/viewer.auth"
expect_status "step 4: the viewer reads" 200
[[ "$(val 'any(n["id"] == os.environ["E2E_NOTE_ID"] for n in d)')" == "True" ]] || fail "step 4: the viewer does not see the note of the company"
call POST /api/notes "$tmp/note.json" "$tmp/viewer.auth"
expect_error "step 4: the viewer adds a note" 403 forbidden
[[ "$(header cache-control)" == *no-store* ]] || fail "step 4: the 403 is not marked no-store"
pass "step 4: the viewer of company A reads its notes (200) and cannot add one (403 forbidden)"

# --- Step 5: the viewer becomes a user -----------------------------------------------------------------------------------
call GET /auth/org/members "" "$tmp/seed.auth"
expect_status "step 5: GET /auth/org/members" 200
VIEWER_ID="$(val '[m["user_id"] for m in d["members"] if m["email"] == os.environ["E2E_VIEWER_EMAIL"]][0]')"
uuid_or_fail "step 5: the id of the viewer" "$VIEWER_ID"
json_body "$tmp/role-user.json" "{'role_id': os.environ['E2E_USER_ROLE']}"
call PUT "/auth/org/members/$VIEWER_ID/role" "$tmp/role-user.json" "$tmp/seed.auth"
expect_status "step 5: change the viewer's role to user" 204 ""
# The sample trusts a valid token for its lifetime (spec 0005, Decisions 4 and 12): the old token still says viewer.
call POST /api/notes "$tmp/note.json" "$tmp/viewer.auth"
expect_error "step 5: the token issued before the change" 403 forbidden
refresh viewer
expect_eq "step 5: the viewer's refresh" "$HTTP_CODE" "200"
BODY="$(jwt_payload "$tmp/viewer.auth")"
expect_eq "step 5: the new permissions" "$(val 'd["permissions"]')" "['notes:read', 'notes:write']"
call POST /api/notes "$tmp/note.json" "$tmp/viewer.auth"
expect_status "step 5: the same person adds a note after the refresh" 201
pass "step 5: after the role change and a refresh, the same person adds a note (201); the token issued before the change was still a viewer's"

# --- Step 6: Auth-Core down, the sample holds no keys ----------------------------------------------------------------------
AUTH_STOPPED=1  # before the stop: a stop that fails halfway must still end with Auth-Core started again
"${compose[@]}" stop auth > "$tmp/stop.log" 2>&1 || { cat "$tmp/stop.log" >&2; fail "step 6: could not stop Auth-Core"; }
"${compose[@]}" restart notes-api > "$tmp/restart.log" 2>&1 || { cat "$tmp/restart.log" >&2; fail "step 6: could not restart the sample"; }
wait_ok "$BASE_URL/api/health" 60 || fail "step 6: the sample did not come back within 60s (it must start while Auth-Core is down)"
call GET /api/notes "" "$tmp/viewer.auth"
expect_error "step 6: a valid token while Auth-Core is down and the sample holds no key" 503 auth_unavailable
[[ "$(header cache-control)" == *no-store* ]] || fail "step 6: the 503 is not marked no-store"
call GET /api/notes
expect_unauthorized "step 6: no token is still a 401 while Auth-Core is down"
"${compose[@]}" start auth > "$tmp/start.log" 2>&1 || { cat "$tmp/start.log" >&2; fail "step 6: could not start Auth-Core again"; }
AUTH_STOPPED=0
wait_ok "$BASE_URL/auth/health" 120 || fail "step 6: Auth-Core did not answer within 120s of its start"
# The sample asks for the keys at most every 10 seconds: until then the answer stays 503, and nothing else is allowed.
ok=0
for _ in $(seq 1 30); do
  call GET /api/notes "" "$tmp/viewer.auth"
  if [[ "$HTTP_CODE" == "200" ]]; then ok=1; break; fi
  expect_error "step 6: while the sample waits to ask for the keys again" 503 auth_unavailable
  sleep 2
done
[[ "$ok" == "1" ]] || fail "step 6: the same request was not a 200 within 60s of Auth-Core's start"
pass "step 6: with Auth-Core down and no key held, a valid token is a 503 auth_unavailable (no token is still 401); after Auth-Core is back the same request is a 200"

echo "ALL PASS"
