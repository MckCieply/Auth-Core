#!/usr/bin/env bash
# Real-network end-to-end check of spec 0005 (companies, members, roles and invitations) against the compose stack,
# with the mail catcher (Mailpit) in it. It drives the sequence of the spec's Goal — the operator creates a company and
# invites its first admin, who invites a member, whom the admin then removes — and the role and safety cases.
#
# What it checks, in order (ADMIN = the first admin of the new company A, WORKER = a member of A, SEED = the development
# seed user, B = a second company). Each step names only what it really checks; the criteria it touches only in part
# are marked "part of".
#   1. health answers 200 "Healthy"; the mail catcher answers; the OpenAPI description is served and names seven of the
#      paths; the interactive reference is served (Development). A smoke test: it checks neither the Degraded path of
#      criterion 20 nor the requests, responses and error codes of criterion 24.
#   2. SEED logs in: its token carries roles ["admin"] and the expanded permissions; GET /auth/me agrees - criterion 4,
#      part of 19 (GET /auth/me only).
#   3. the operator CLI in the service's own image: create-org prints the id only, list-orgs shows it - criterion 1.
#   4. the operator invites ADMIN: the mail arrives at the server's next poll (within a minute) and names the application,
#      the company, the role and the 7 days; preview twice, a weak password -> 400 weak_password (the token stays), accept
#      -> 204 with no cookie, accept and preview again -> 400 invalid_token - criteria 2, 3, 7; part of 4 and 6 (a used
#      token only).
#   5. ADMIN logs in: org_id of A, roles ["admin"], permissions sorted, no "*" - criterion 4.
#   6. ADMIN invites WORKER through the API (202), the mail arrives, WORKER accepts and logs in with roles ["user"]; the
#      member list shows both - part of 2.
#   7. roles and safety: WORKER without members:manage is forbidden; no token is a 401 with an empty body and a Bearer
#      challenge (one route); nobody changes their own role or removes themselves; a role is created, an unknown
#      permission and a taken name are refused; a role change reaches WORKER's next refresh; a lesser manager can neither
#      remove nor re-role ADMIN, invite with the star role, nor resend or cancel an invitation for it; a demoted caller's
#      old token gets permissions_changed and the new one forbidden; a role edit that would leave no manager is
#      last_manager - criterion 17; part of 12, 14, 15, 16 and 18 (no role delete or role_in_use).
#   8. B: a role or member of another company is 404 on the member and role endpoints; an invitation to an address of
#      another company is answered with the same status and headers as one to an unknown address, and its preview and
#      acceptance say already_member - criterion 8; part of 14.
#   9. removal: WORKER's refresh cookie -> 401 with no cookie written, login with the right password -> 403
#      no_membership with no cookie, the old access token -> permissions_changed - part of 11 and 13 (no wrong password,
#      no invitation back in).
#  10. the CLI refuses to remove the last manager (last_manager) and removes it with --force; afterwards ADMIN's sessions
#      are over - criterion 22; part of 16.
# Criteria 5, 9, 10, 19 (GET and PATCH /auth/org), 20, 21, 23 and 24 as a whole, the parallel accepts of 6 and the rest
# of the partial ones above are guarded by the integration tests, not here: see docs/superpowers/plans/0005-acceptance-map.md.
#
# Full sequence, from the repo root (same stack and .env as the other e2e scripts; the four existing ones first,
# this one last - they must still pass with the seed users as members of the development company):
#   cp .env.example .env                  # then set real local values (git-ignored)
#   scripts/dev-keys.sh                   # dev signing/encryption keys into .secrets/ (git-ignored)
#   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, never "auth-core" (the development stack): a bare down -v would wipe it; the scripts default to this name and refuse "auth-core"
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build    # start postgres, mailpit, auth
#                                         # the per-IP limiter is off for the older checks (spec 0008): the lockout script makes about 58 logins a minute and the mail script exactly 10 mail requests; scripts/e2e-hardening.sh runs last on the stack recreated with the defaults
#   scripts/e2e-login.sh                  # spec 0001 regression
#   scripts/e2e-refresh.sh                # spec 0002 regression
#   scripts/e2e-lockout.sh                # spec 0003 regression
#   scripts/e2e-email.sh                  # spec 0004 regression
#   scripts/e2e-tenancy.sh                # this script (does NOT bring the stack up or down)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# Reads AUTH_DEV_SEED_EMAIL and AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced). The operator
# commands run as `docker compose run --rm -T --no-deps auth admin ...` (the image has no shell: its entrypoint takes the
# arguments). Needs: curl, python3 (standard library only), docker compose. Env: BASE_URL (default http://localhost:8080),
# MAILPIT_URL (default http://localhost:8025). Takes about two minutes, most of it waiting for the server to pick up the
# invitation the CLI queued. Re-runnable on the same stack: every company and address is made for the run.
# Exits non-zero on the first failure; prints "PASS <step>" per step; never prints a password, a token, a cookie or a mail
# body. Request bodies are built into files in a mktemp -d directory (removed on exit) and curl reads them with
# --data-binary @file; tokens and cookies reach curl through header files.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
# The stack of these scripts is its own compose project, never "auth-core" (the development stack): the restarts and one-off containers below meet the same stack as the down -v of the header.
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-hardening}"
[[ "$COMPOSE_PROJECT_NAME" != "auth-core" ]] || { echo "FAIL COMPOSE_PROJECT_NAME=auth-core is the development stack; use another name" >&2; exit 1; }
compose=(docker compose -f "$root/deploy/docker-compose.yml" --env-file "$root/.env")

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

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
ADMIN_EMAIL="boss-$run@acme.test"
WORKER_EMAIL="worker-$run@acme.test"
STRANGER_EMAIL="nobody-$run@example.invalid"
BADMIN_EMAIL="boss-$run@globex.test"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
export E2E_ADMIN_EMAIL="$ADMIN_EMAIL" E2E_WORKER_EMAIL="$WORKER_EMAIL" E2E_BADMIN_EMAIL="$BADMIN_EMAIL"
export E2E_PASSWORD="E2e-Passw0rd-$run" E2E_WEAK_PASSWORD="abc"

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

header_names() { # sorted header names of the last response, Date excluded
  tail -n +2 "$tmp/hdr" | tr -d '\r' | grep ':' | cut -d: -f1 | tr '[:upper:]' '[:lower:]' | { grep -vx 'date' || true; } | sort | tr '\n' ' '
}

# The python3 of some machines is a Windows interpreter, which ends its lines with CR LF: the helpers below that print a
# value strip the CR, so that the value compares equal in bash.

# val <python-expression-of-d>: evaluates it against the JSON of $BODY and prints the result
val() { python3 -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1" <<< "$BODY" | tr -d '\r'; }

# jwt_payload <auth-header-file>: the payload of the bearer token in the file, as JSON (the token is never printed)
jwt_payload() {
  python3 -c '
import base64, json, sys
token = sys.stdin.read().split("Bearer ", 1)[1].strip().split(".")[1]
print(json.dumps(json.loads(base64.urlsafe_b64decode(token + "=" * (-len(token) % 4)))))
' < "$1" | tr -d '\r'
}

json_body() { # json_body <file> <python-expression>: writes the JSON of the expression, which may read os.environ
  python3 -c 'import json,os,sys; print(json.dumps(eval(sys.argv[1])))' "$2" > "$1"
}

expect_status() { # expect_status <what> <code> [exact-body]
  [[ "$HTTP_CODE" == "$2" ]] || fail "$1: HTTP $HTTP_CODE, expected $2"
  if [[ $# -ge 3 ]]; then [[ "$BODY" == "$3" ]] || fail "$1: unexpected body"; fi
}

expect_error() { expect_status "$1" "$2" "{\"error\":\"$3\"}"; }

expect_no_store() { [[ "$(header cache-control)" == *no-store* ]] || fail "$1: no Cache-Control: no-store"; }

expect_eq() { [[ "$2" == "$3" ]] || fail "$1: got '$2', expected '$3'"; }

# login <name> <email-env> <password-env>: logs in, keeps the bearer header in $tmp/<name>.auth and the cookie in $tmp/<name>.cookie
login() {
  json_body "$tmp/$1.login" "{'email': os.environ['$2'], 'password': os.environ['$3']}"
  call POST /auth/login "$tmp/$1.login"
  expect_status "login of $1" 200
  keep_session "$1"
}

keep_session() { # keep_session <name>: the access token of $BODY and the cookie of the last response
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$1.auth"
  local pair
  pair="$({ grep -i '^set-cookie:[[:space:]]*auth_rt=[^;]' "$tmp/hdr" || true; } | head -n1 | tr -d '\r' | cut -d: -f2- | sed 's/^ *//' | cut -d';' -f1)"
  [[ -n "$pair" ]] || fail "the response set no auth_rt cookie for $1"
  printf 'Cookie: %s\n' "$pair" > "$tmp/$1.cookie"
}

# refresh <name>: refreshes the session of <name> with its cookie; keeps the new tokens. Sets HTTP_CODE.
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

mail_field() { python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)[sys.argv[1]])' "$1" < "$tmp/mail.json" | tr -d '\r'; }

# --- the operator CLI, in the service's own image ----------------------------------------------------------------

# cli <args...>: runs `auth-server admin <args>` in a one-off container; sets CLI_OUT (stdout), CLI_EXIT, and keeps stderr in $tmp/cli.err
cli() {
  CLI_EXIT=0
  CLI_OUT="$("${compose[@]}" run --rm -T --no-deps auth admin "$@" 2> "$tmp/cli.err")" || CLI_EXIT=$?
  CLI_OUT="${CLI_OUT//$'\r'/}"
}

# --- Step 1: health, the mail catcher, the description ---------------------------------------------------------------
wait_healthy || fail "step 1: $BASE_URL/auth/health did not return 200 within 90s"
wait_mailpit || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s (is the mailpit service up?)"
call GET /auth/health
expect_status "step 1: health" 200 "Healthy"
call GET /auth/openapi/v1.json
expect_status "step 1: the OpenAPI description" 200
for path in /auth/login /auth/org/invites /auth/org/members /auth/org/roles /auth/invites/accept /auth/me /auth/.well-known/jwks.json; do
  [[ "$(val "'$path' in d['paths']")" == "True" ]] || fail "step 1: $path is not in the OpenAPI description"
done
[[ "$(curl -sL -o /dev/null --max-time 10 -w '%{http_code}' "$BASE_URL/auth/scalar")" == "200" ]] || fail "step 1: the interactive reference is not served in Development"
pass "step 1: health is Healthy; the mail catcher answers; the OpenAPI description names the endpoints; the reference is served (Development)"

# --- Step 2: the seed user is the admin of the development company -----------------------------------------------------
login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD
PAYLOAD="$(jwt_payload "$tmp/seed.auth")"
BODY="$PAYLOAD"
expect_eq "step 2: roles" "$(val 'd["roles"]')" "['admin']"
expect_eq "step 2: permissions" "$(val 'd["permissions"]')" "['documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:delete', 'org:manage', 'roles:manage']"
call GET /auth/me "" "$tmp/seed.auth"
expect_status "step 2: GET /auth/me" 200
expect_no_store "step 2: GET /auth/me"
expect_eq "step 2: /auth/me email" "$(val 'd["email"]')" "$SEED_EMAIL"
expect_eq "step 2: /auth/me company" "$(val 'd["org_name"]')" "Development"
pass "step 2: the seed user is the admin of the development company; its token and /auth/me say so"

# --- Step 3: the operator creates company A ----------------------------------------------------------------------------
cli create-org --name "E2E Acme $run"
expect_eq "step 3: create-org exit code" "$CLI_EXIT" "0"
[[ "$CLI_OUT" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || fail "step 3: create-org printed more than the company id"
A="$CLI_OUT"
cli list-orgs
expect_eq "step 3: list-orgs exit code" "$CLI_EXIT" "0"
grep -qF "$A"$'\t'"E2E Acme $run"$'\t'"0" <<< "$CLI_OUT" || fail "step 3: list-orgs does not show the new company with 0 members"
pass "step 3: create-org printed the company id; list-orgs shows it with no members"

# --- Step 4: the operator invites ADMIN; the invitation, its preview and its acceptance --------------------------------
cli invite --org "$A" --email "$ADMIN_EMAIL" --role admin
expect_eq "step 4: invite exit code" "$CLI_EXIT" "0"
# The CLI only queues the mail: the server sends it at its next poll, within a minute.
wait_mail "$ADMIN_EMAIL" 1 120 || fail "step 4: no invitation mail within 120s of the CLI invitation"
[[ "$(mail_field Subject)" == *auth-core-dev* ]] || fail "step 4: the subject does not carry the application name"
python3 -c '
import json, sys
mail = json.load(sys.stdin.buffer)
text = mail["Text"]
for needle in ("E2E Acme", "admin", "7 days"):
    if needle not in text:
        sys.exit("the invitation mail does not say: " + needle)
' < "$tmp/mail.json" || fail "step 4: the invitation mail misses its company, role or lifetime"
token_body "$tmp/admin-preview.json" || fail "step 4: no usable invitation link in the mail"
token_body "$tmp/admin-weak.json" E2E_WEAK_PASSWORD
token_body "$tmp/admin-accept.json" E2E_PASSWORD
call POST /auth/invites/preview "$tmp/admin-preview.json"
expect_status "step 4: preview" 200
expect_no_store "step 4: preview"
expect_eq "step 4: preview company" "$(val 'd["org_name"]')" "E2E Acme $run"
expect_eq "step 4: preview address" "$(val 'd["email"]')" "$ADMIN_EMAIL"
expect_eq "step 4: preview role" "$(val 'd["role"]')" "admin"
call POST /auth/invites/preview "$tmp/admin-preview.json"
expect_status "step 4: preview again (it does not use the token up)" 200
call POST /auth/invites/accept "$tmp/admin-weak.json"
expect_status "step 4: accept with a weak password" 400 '{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}'
call POST /auth/invites/accept "$tmp/admin-accept.json"
expect_status "step 4: accept" 204 ""
expect_no_store "step 4: accept"
[[ -z "$(header set-cookie)" ]] || fail "step 4: accept set a cookie"
call POST /auth/invites/accept "$tmp/admin-accept.json"
expect_error "step 4: accept again" 400 invalid_token
call POST /auth/invites/preview "$tmp/admin-preview.json"
expect_error "step 4: preview of a used token" 400 invalid_token
pass "step 4: the CLI invitation arrived (names the company, role and 7 days); preview twice; weak password -> 400 weak_password; accept -> 204; again -> 400 invalid_token"

# --- Step 5: ADMIN logs in ---------------------------------------------------------------------------------------------
login admin E2E_ADMIN_EMAIL E2E_PASSWORD
BODY="$(jwt_payload "$tmp/admin.auth")"
expect_eq "step 5: org_id" "$(val 'd["org_id"]')" "$A"
expect_eq "step 5: roles" "$(val 'd["roles"]')" "['admin']"
expect_eq "step 5: permissions" "$(val 'd["permissions"]')" "['documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:delete', 'org:manage', 'roles:manage']"
pass "step 5: ADMIN's token carries org_id of company A, roles [admin] and the expanded, sorted permissions without a star"

# --- Step 6: ADMIN invites WORKER through the API ---------------------------------------------------------------------
call GET /auth/org/roles "" "$tmp/admin.auth"
expect_status "step 6: GET /auth/org/roles" 200
expect_eq "step 6: catalog" "$(val 'd["catalog"]')" "['*', 'documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:delete', 'org:manage', 'roles:manage']"
USER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "user"][0]')"
ADMIN_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "admin"][0]')"
export E2E_USER_ROLE="$USER_ROLE" E2E_ADMIN_ROLE="$ADMIN_ROLE"
json_body "$tmp/invite-worker.json" "{'email': os.environ['E2E_WORKER_EMAIL'], 'role_id': os.environ['E2E_USER_ROLE']}"
call POST /auth/org/invites "$tmp/invite-worker.json" "$tmp/admin.auth"
expect_status "step 6: invite WORKER" 202 ""
expect_no_store "step 6: invite WORKER"
wait_mail "$WORKER_EMAIL" 1 30 || fail "step 6: no invitation mail for WORKER within 30s (an API invitation wakes the dispatcher)"
token_body "$tmp/worker-accept.json" E2E_PASSWORD
call POST /auth/invites/accept "$tmp/worker-accept.json"
expect_status "step 6: WORKER accepts" 204 ""
login worker E2E_WORKER_EMAIL E2E_PASSWORD
BODY="$(jwt_payload "$tmp/worker.auth")"
expect_eq "step 6: WORKER roles" "$(val 'd["roles"]')" "['user']"
expect_eq "step 6: WORKER permissions" "$(val 'd["permissions"]')" "['documents:read', 'documents:write']"
call GET /auth/org/members "" "$tmp/admin.auth"
expect_status "step 6: GET /auth/org/members" 200
expect_eq "step 6: members" "$(val '[m["email"] for m in d["members"]]')" "['$ADMIN_EMAIL', '$WORKER_EMAIL']"
WORKER_ID="$(val '[m["user_id"] for m in d["members"] if m["email"] == "'"$WORKER_EMAIL"'"][0]')"
ADMIN_ID="$(val '[m["user_id"] for m in d["members"] if m["email"] == "'"$ADMIN_EMAIL"'"][0]')"
pass "step 6: ADMIN invited WORKER (202, mail within seconds); WORKER accepted and logged in as [user]; the member list shows both"

# --- Step 7: roles and safety rules -------------------------------------------------------------------------------------
call GET /auth/org/members "" "$tmp/worker.auth"
expect_error "step 7: a member without members:manage lists members" 403 forbidden
call GET /auth/org/members
[[ "$HTTP_CODE" == "401" && -z "$BODY" && "$(header www-authenticate)" == Bearer* ]] || fail "step 7: no token must be a 401, empty body, WWW-Authenticate: Bearer"
json_body "$tmp/role-self.json" "{'role_id': os.environ['E2E_USER_ROLE']}"
call PUT "/auth/org/members/$ADMIN_ID/role" "$tmp/role-self.json" "$tmp/admin.auth"
expect_error "step 7: changing one's own role" 409 cannot_change_self
call DELETE "/auth/org/members/$ADMIN_ID" "" "$tmp/admin.auth"
expect_error "step 7: removing oneself" 409 cannot_change_self
json_body "$tmp/role-new.json" "{'name': 'Reviewer', 'permissions': ['documents:read', 'documents:approve']}"
call POST /auth/org/roles "$tmp/role-new.json" "$tmp/admin.auth"
expect_status "step 7: create a role" 201
expect_eq "step 7: the new role's permissions" "$(val 'd["permissions"]')" "['documents:approve', 'documents:read']"
json_body "$tmp/role-unknown.json" "{'name': 'Odd', 'permissions': ['documents:publish']}"
call POST /auth/org/roles "$tmp/role-unknown.json" "$tmp/admin.auth"
expect_error "step 7: a permission outside the catalog" 400 unknown_permission
json_body "$tmp/role-taken.json" "{'name': 'REVIEWER', 'permissions': []}"
call POST /auth/org/roles "$tmp/role-taken.json" "$tmp/admin.auth"
expect_error "step 7: a name that is taken, whatever its case" 409 role_name_taken
json_body "$tmp/role-lead.json" "{'name': 'Lead', 'permissions': ['members:manage', 'documents:read']}"
call POST /auth/org/roles "$tmp/role-lead.json" "$tmp/admin.auth"
expect_status "step 7: create the role Lead" 201
LEAD_ROLE="$(val 'd["id"]')"
export E2E_LEAD_ROLE="$LEAD_ROLE"
json_body "$tmp/role-lead-for-worker.json" "{'role_id': os.environ['E2E_LEAD_ROLE']}"
call PUT "/auth/org/members/$WORKER_ID/role" "$tmp/role-lead-for-worker.json" "$tmp/admin.auth"
expect_status "step 7: give WORKER the role Lead" 204 ""
refresh worker
expect_eq "step 7: WORKER refreshed" "$HTTP_CODE" "200"
BODY="$(jwt_payload "$tmp/worker.auth")"
expect_eq "step 7: the change reached the next refresh" "$(val 'd["permissions"]')" "['documents:read', 'members:manage']"
# WORKER (Lead) holds less than ADMIN (star): they can neither remove ADMIN nor give ADMIN another role.
call DELETE "/auth/org/members/$ADMIN_ID" "" "$tmp/worker.auth"
expect_error "step 7: removing a member who holds more than the caller" 403 permission_not_held
call PUT "/auth/org/members/$ADMIN_ID/role" "$tmp/role-self.json" "$tmp/worker.auth"
expect_error "step 7: changing the role of a member who holds more than the caller" 403 permission_not_held
# WORKER (Lead) may invite, but with nothing they do not hold, and may not touch roles.
json_body "$tmp/invite-star.json" "{'email': 'star-$run@acme.test', 'role_id': os.environ['E2E_ADMIN_ROLE']}"
call POST /auth/org/invites "$tmp/invite-star.json" "$tmp/worker.auth"
expect_error "step 7: a role with star given by a caller whose role lacks it" 403 permission_not_held
# An invitation for the role admin, made by ADMIN: WORKER (Lead) may neither resend nor cancel it; ADMIN may cancel it.
json_body "$tmp/invite-big.json" "{'email': 'big-$run@acme.test', 'role_id': os.environ['E2E_ADMIN_ROLE']}"
call POST /auth/org/invites "$tmp/invite-big.json" "$tmp/admin.auth"
expect_status "step 7: ADMIN invites with the role admin" 202 ""
call GET /auth/org/invites "" "$tmp/admin.auth"
BIG_INVITE="$(val "[i['id'] for i in d['invites'] if i['email'] == 'big-$run@acme.test'][0]")"
call POST "/auth/org/invites/$BIG_INVITE/resend" "" "$tmp/worker.auth"
expect_error "step 7: resending an invitation for a role that holds more than the caller" 403 permission_not_held
call DELETE "/auth/org/invites/$BIG_INVITE" "" "$tmp/worker.auth"
expect_error "step 7: cancelling an invitation for a role that holds more than the caller" 403 permission_not_held
call DELETE "/auth/org/invites/$BIG_INVITE" "" "$tmp/admin.auth"
expect_status "step 7: ADMIN cancels it" 204 ""
json_body "$tmp/role-by-worker.json" "{'name': 'Mine', 'permissions': []}"
call POST /auth/org/roles "$tmp/role-by-worker.json" "$tmp/worker.auth"
expect_error "step 7: Lead cannot manage roles" 403 forbidden
# ADMIN takes members:manage from Lead: WORKER's token still says they may, the database says no.
json_body "$tmp/role-lead-edit.json" "{'name': 'Lead', 'permissions': ['documents:read']}"
call PUT "/auth/org/roles/$LEAD_ROLE" "$tmp/role-lead-edit.json" "$tmp/admin.auth"
expect_status "step 7: ADMIN edits Lead" 204 ""
call GET /auth/org/members "" "$tmp/worker.auth"
expect_error "step 7: the old token of a demoted caller" 403 permissions_changed
refresh worker
expect_eq "step 7: WORKER refreshed after the edit" "$HTTP_CODE" "200"
call GET /auth/org/members "" "$tmp/worker.auth"
expect_error "step 7: the new token of the demoted caller" 403 forbidden
# The only manager is ADMIN, through star: an edit of admin that drops members:manage would leave nobody.
json_body "$tmp/role-admin-edit.json" "{'name': 'admin', 'permissions': ['roles:manage', 'org:manage']}"
call PUT "/auth/org/roles/$ADMIN_ROLE" "$tmp/role-admin-edit.json" "$tmp/admin.auth"
expect_error "step 7: an edit that leaves the company without a manager" 409 last_manager
pass "step 7: roles and the three safety rules hold over the network; a demoted caller gets permissions_changed with the old token and forbidden with the new one"

# --- Step 8: company B and the other company's ids -------------------------------------------------------------------
cli create-org --name "E2E Globex $run"
expect_eq "step 8: create-org B exit code" "$CLI_EXIT" "0"
B="$CLI_OUT"
cli invite --org "$B" --email "$BADMIN_EMAIL" --role admin
expect_eq "step 8: invite B's admin exit code" "$CLI_EXIT" "0"
wait_mail "$BADMIN_EMAIL" 1 120 || fail "step 8: no invitation mail for B's admin within 120s"
token_body "$tmp/badmin-accept.json" E2E_PASSWORD
call POST /auth/invites/accept "$tmp/badmin-accept.json"
expect_status "step 8: B's admin accepts" 204 ""
login badmin E2E_BADMIN_EMAIL E2E_PASSWORD
call GET /auth/org/roles "" "$tmp/badmin.auth"
B_USER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "user"][0]')"
call GET /auth/org/members "" "$tmp/badmin.auth"
B_ADMIN_ID="$(val '[m["user_id"] for m in d["members"] if m["email"] == "'"$BADMIN_EMAIL"'"][0]')"
export E2E_B_USER_ROLE="$B_USER_ROLE"
json_body "$tmp/role-foreign.json" "{'role_id': os.environ['E2E_B_USER_ROLE']}"
call PUT "/auth/org/members/$WORKER_ID/role" "$tmp/role-foreign.json" "$tmp/admin.auth"
expect_error "step 8: a role of another company" 404 not_found
call PUT "/auth/org/members/$B_ADMIN_ID/role" "$tmp/role-lead-for-worker.json" "$tmp/admin.auth"
expect_error "step 8: a member of another company" 404 not_found
call DELETE "/auth/org/members/$B_ADMIN_ID" "" "$tmp/admin.auth"
expect_error "step 8: removing a member of another company" 404 not_found
call DELETE "/auth/org/roles/$B_USER_ROLE" "" "$tmp/admin.auth"
expect_error "step 8: deleting a role of another company" 404 not_found
call PUT "/auth/org/roles/$B_USER_ROLE" "$tmp/role-new.json" "$tmp/admin.auth"
expect_error "step 8: editing a role of another company" 404 not_found
# An invitation to an address of another company is answered exactly as one to an address nobody has.
json_body "$tmp/invite-stranger.json" "{'email': '$STRANGER_EMAIL', 'role_id': os.environ['E2E_USER_ROLE']}"
call POST /auth/org/invites "$tmp/invite-stranger.json" "$tmp/admin.auth"
expect_status "step 8: invite an unknown address" 202 ""
STRANGER_HEADERS="$(header_names)"
json_body "$tmp/invite-badmin.json" "{'email': os.environ['E2E_BADMIN_EMAIL'], 'role_id': os.environ['E2E_USER_ROLE']}"
call POST /auth/org/invites "$tmp/invite-badmin.json" "$tmp/admin.auth"
expect_status "step 8: invite a member of another company" 202 ""
[[ "$(header_names)" == "$STRANGER_HEADERS" ]] || fail "step 8: the headers differ between an unknown address and a member of another company"
wait_mail "$BADMIN_EMAIL" 2 30 || fail "step 8: no second invitation mail for B's admin"
token_body "$tmp/badmin-second.json" E2E_PASSWORD
call POST /auth/invites/preview "$tmp/badmin-second.json"
expect_error "step 8: the preview tells the invited person" 409 already_member
call POST /auth/invites/accept "$tmp/badmin-second.json"
expect_error "step 8: the acceptance too" 409 already_member
pass "step 8: ids of another company are 404; an invitation to a member of another company is answered like one to an unknown address, and only the acceptance screen says already_member"

# --- Step 9: removing a member ---------------------------------------------------------------------------------------
call DELETE "/auth/org/members/$WORKER_ID" "" "$tmp/admin.auth"
expect_status "step 9: remove WORKER" 204 ""
refresh worker
expect_error "step 9: WORKER's refresh cookie" 401 invalid_grant
[[ -z "$(header set-cookie)" ]] || fail "step 9: a failed refresh wrote the cookie"
json_body "$tmp/worker.login2" "{'email': os.environ['E2E_WORKER_EMAIL'], 'password': os.environ['E2E_PASSWORD']}"
call POST /auth/login "$tmp/worker.login2"
expect_error "step 9: WORKER logs in" 403 no_membership
expect_no_store "step 9: no_membership"
[[ -z "$(header set-cookie)" ]] || fail "step 9: no_membership set a cookie"
call GET /auth/org "" "$tmp/worker.auth"
expect_error "step 9: WORKER's old access token on the company API" 403 permissions_changed
pass "step 9: the removed member's refresh -> 401 invalid_grant, login -> 403 no_membership, old access token -> permissions_changed"

# --- Step 10: the CLI removes the last manager only with --force -------------------------------------------------------
cli remove-member --org "$A" --email "$ADMIN_EMAIL"
expect_eq "step 10: remove-member exit code" "$CLI_EXIT" "1"
grep -qF "error: last_manager" "$tmp/cli.err" || fail "step 10: the refusal does not name last_manager"
call GET /auth/org "" "$tmp/admin.auth"
expect_status "step 10: ADMIN is still a member" 200
cli remove-member --org "$A" --email "$ADMIN_EMAIL" --force
expect_eq "step 10: remove-member --force exit code" "$CLI_EXIT" "0"
refresh admin
expect_error "step 10: ADMIN's refresh cookie" 401 invalid_grant
call POST /auth/login "$tmp/admin.login"
expect_error "step 10: ADMIN logs in" 403 no_membership
cli list-orgs
grep -qF "$A"$'\t'"E2E Acme $run"$'\t'"0" <<< "$CLI_OUT" || fail "step 10: company A should be left with no members"
pass "step 10: remove-member refused the last manager (last_manager) and removed it with --force; ADMIN's sessions are over"

echo "ALL PASS"
