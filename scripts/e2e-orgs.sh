#!/usr/bin/env bash
# Real-network end-to-end check of spec 0015 (a person in several companies: list them, switch between them) against the compose
# stack. It drives the sequence of the spec's Goal - the development seed user, who is the admin of the development company and a plain
# member of a second one, lists their companies, switches to the second, refreshes and signs in again there, and is removed from it by
# the operator - and the refusals around the switch.
#
# What it checks, in order (SEED = the development seed user; FIRST = the development company, where SEED is the admin; SECOND = the
# company named by AUTH_DEV_SEED_SECOND_ORG_NAME, "Beta" by default, where SEED holds the plain role "user"). "jar", "jar2" and "jar3" are
# the sessions of three sign-ins: the access token in a header file and the auth_rt refresh cookie in another, as in the other scripts.
# Each step names the criteria of the spec it touches; the guarding tests are in docs/superpowers/plans/0015-acceptance-map.md.
#   1. health is Healthy; SEED logs in (jar) -> 200 with the auth_rt cookie; the token's org_id is FIRST (the company used last by the earlier
#      scripts, and the one joined first) - criteria 3, 4.
#   2. GET /auth/orgs with jar's token -> 200, no-store, exactly two entries: FIRST (current true, role admin, kind organization) and
#      SECOND (named as the setting, current false, role user, kind organization), each with an id and a role id; without a token -> 401 -
#      criteria 12, 13, 14.
#   3. POST /auth/switch {"org_id": SECOND} with jar's cookie -> 200 with a refresh's answer (an access token, no-store, a new cookie); the
#      token's org_id is SECOND, its roles are ["user"]; GET /auth/orgs with it marks SECOND as current; the audit log holds one
#      org.switched row (actor and subject SEED, org_id SECOND, details.from_org_id FIRST) - criteria 13, 15.
#   4. POST /auth/refresh with the new cookie (jar) -> 200, org_id SECOND: the session stays in the company it switched to - criteria 4, 6.
#   5. a new sign-in of SEED (jar2) -> 200, org_id SECOND: a sign-in lands in the company used last - criterion 4.
#   6. the refusals: a switch to an unknown UUID -> 404 {"error":"not_found"}; {"org_id":"x"} -> 400 invalid_request; a form post
#      (application/x-www-form-urlencoded) -> 400 invalid_request; none of them writes a cookie, and after each jar still refreshes (the
#      refresh token was not used up) - criteria 17, 18.
#   7. the operator removes SEED from SECOND: `admin remove-member --org <SECOND> --email <SEED>` -> "Member removed; the account is
#      kept."; the audit log holds a member.removed row for SECOND and no account.deleted row for SEED - criterion 26.
#   8. POST /auth/refresh with jar's cookie and with jar2's -> 401 {"error":"invalid_grant"}: the devices that worked in SECOND are signed
#      out at their next refresh - criterion 8.
#   9. a new sign-in (jar3) -> 200, org_id FIRST: the account stayed and the company left is the first - criteria 4, 26.
#  10. spec 0017 (an invitation keeps the account's memberships): the operator makes the company GAMMA ("E2E Gamma <run>", a name of
#      its own per run) and invites its admin, who invites SEED; the invitation mail tells an existing account it joins; the preview ->
#      200 has_account true and GAMMA's org_id; the acceptance with the token alone -> 204; jar3 (signed in to FIRST) still refreshes in
#      FIRST; GET /auth/orgs lists FIRST (current) and GAMMA (SECOND was left in step 7); a switch into GAMMA -> 200, role user; a new
#      sign-in (jar4) lands in GAMMA. A trap deletes GAMMA with the operator command delete-org, also when a step fails.
#
# Full sequence, from the repo root (the stack and .env of the other e2e scripts, with the per-IP limiter OFF as for them; run this one
# after e2e-private.sh and before e2e-hardening.sh, which recreates the stack with the limits on):
#   cp .env.example .env                  # then set real local values (git-ignored)
#   scripts/dev-keys.sh                   # on Windows: MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh
#   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, never "auth-core" (the development stack)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build
#   scripts/e2e-login.sh; scripts/e2e-refresh.sh; scripts/e2e-lockout.sh; scripts/e2e-email.sh; scripts/e2e-tenancy.sh; scripts/e2e-signup.sh; scripts/e2e-private.sh
#   scripts/e2e-orgs.sh                   # this script (does NOT bring the stack up or down)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# It needs the development seed's second company, which exists only after the seed ran with AUTH_DEV_SEED_SECOND_ORG_NAME set (the
# default is "Beta"): a fresh stack, or a restart of the auth service (`docker compose ... restart auth`) after an earlier run of this
# script, which removes SEED from that company. Reads AUTH_DEV_SEED_EMAIL, AUTH_DEV_SEED_PASSWORD and AUTH_DEV_SEED_SECOND_ORG_NAME from
# the repo-root .env (parsed, never sourced). The operator command runs as `docker compose run --rm -T --no-deps auth admin ...`; the
# audit log is read with `docker compose exec postgres psql`. Needs: curl, python3 (standard library only; E2E_PYTHON=<interpreter>
# names another, for example E2E_PYTHON=C:/p6v/Scripts/python.exe), docker compose. Env: BASE_URL (default http://localhost:8080),
# MAILPIT_URL (default http://localhost:8025; step 10).
# Takes about two minutes. Exits non-zero on the first failure; prints "PASS <step>" per step; never prints a password, a token, a cookie or
# a mail body. Step 10 waits for the invitation mail of GAMMA's admin, which the server sends at its next poll (up to a minute). Request bodies are built into files in a mktemp -d directory (removed on exit) and curl reads them with --data-binary
# @file; tokens and cookies reach curl through header files.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
INVITE_LINK="http://localhost:4200/invite"
PY="${E2E_PYTHON:-python3}"
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-hardening}"
[[ "$COMPOSE_PROJECT_NAME" != "auth-core" ]] || { echo "FAIL COMPOSE_PROJECT_NAME=auth-core is the development stack; use another name" >&2; exit 1; }
compose=(docker compose -f "$root/deploy/docker-compose.yml" --env-file "$root/.env")

tmp="$(mktemp -d)"
# What step 10 made and has not yet deleted, for the cleanup (GAMMA_ID is set as soon as the company exists).
GAMMA_ID=""
GAMMA_NAME=""
# The trap removes the temporary directory and, when step 10 made GAMMA, deletes it with the operator command (also when a step failed), so
# that the script can run again and the next scripts meet SEED in the company they expect. It runs with `set +e`: a leftover is reported on
# stderr and the exit status stays the run's. It extends the former trap, which only removed $tmp.
cleanup() {
  set +e
  if [[ -n "$GAMMA_ID" ]]; then
    if cli delete-org --org "$GAMMA_ID" --confirm "$GAMMA_NAME" > /dev/null 2>&1 && [[ "$CLI_EXIT" == "0" ]]; then
      echo "cleanup: deleted the company $GAMMA_NAME" >&2
    else
      echo "cleanup: could not delete the company $GAMMA_NAME ($GAMMA_ID): delete it by hand with the operator command delete-org" >&2
    fi
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
# The name of the second company: "Beta" when the line is absent; a line that is present and blank means "no second company" (the compose file passes it on as it is).
SECOND_NAME="Beta"
if grep -qE '^AUTH_DEV_SEED_SECOND_ORG_NAME=' "$root/.env"; then
  SECOND_NAME="$(env_get AUTH_DEV_SEED_SECOND_ORG_NAME)"
  [[ -n "$SECOND_NAME" ]] || fail "AUTH_DEV_SEED_SECOND_ORG_NAME is blank in .env: this script needs the seed's second company"
fi
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"

wait_ok() { # wait_ok <url> <seconds>: waits until the URL answers 200
  local i
  for i in $(seq 1 "$2"); do
    if [[ "$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "$1" || true)" == "200" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

# call <method> <path> [body-file] [header-file] -> sets HTTP_CODE and BODY; response headers in $tmp/hdr
call() {
  local args=(-sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' -X "$1" "$BASE_URL$2")
  if [[ -n "${3:-}" ]]; then args+=(-H 'Content-Type: application/json' --data-binary "@$3"); fi
  if [[ -n "${4:-}" ]]; then args+=(-H "@$4"); fi
  HTTP_CODE="$(curl "${args[@]}")"
  BODY="$(cat "$tmp/body")"
}

# call_form <path> <form-data> <header-file>: POST of an application/x-www-form-urlencoded body; sets HTTP_CODE and BODY as call does
call_form() {
  HTTP_CODE="$(curl -sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' -X POST "$BASE_URL$1" \
    -H 'Content-Type: application/x-www-form-urlencoded' --data-binary "$2" -H "@$3")"
  BODY="$(cat "$tmp/body")"
}

header() { # value of a response header of the last response, CR stripped (empty when absent)
  { grep -i "^$1:" "$tmp/hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//'
}

# A Windows python3 ends its lines with CR LF: the helpers that print a value strip the CR.
val() { "$PY" -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1" <<< "$BODY" | tr -d '\r'; }

jwt_payload() { # jwt_payload <auth-header-file>: the payload of the bearer token in the file, as JSON (the token is never printed)
  "$PY" -c '
import base64, json, sys
token = sys.stdin.read().split("Bearer ", 1)[1].strip().split(".")[1]
print(json.dumps(json.loads(base64.urlsafe_b64decode(token + "=" * (-len(token) % 4)))))
' < "$1" | tr -d '\r'
}

json_body() { # json_body <file> <python-expression>: writes the JSON of the expression, which may read os.environ
  "$PY" -c 'import json,os,sys; print(json.dumps(eval(sys.argv[1])))' "$2" > "$1"
}

expect_status() { # expect_status <what> <code> [exact-body]
  [[ "$HTTP_CODE" == "$2" ]] || fail "$1: HTTP $HTTP_CODE, expected $2"
  if [[ $# -ge 3 ]]; then [[ "$BODY" == "$3" ]] || fail "$1: unexpected body"; fi
}

expect_error() { expect_status "$1" "$2" "{\"error\":\"$3\"}"; }

expect_no_store() { [[ "$(header cache-control)" == *no-store* ]] || fail "$1: no Cache-Control: no-store"; }

expect_eq() { [[ "$2" == "$3" ]] || fail "$1: got '$2', expected '$3'"; }

expect_no_cookie() { [[ -z "$(header set-cookie)" ]] || fail "$1: the response wrote a cookie"; }

# expect_invalid_request <what>: the last response is a 400 whose error member is invalid_request (an error_description may follow, so
# the whole body is not compared) and which wrote no cookie
expect_invalid_request() {
  [[ "$HTTP_CODE" == "400" ]] || fail "$1: HTTP $HTTP_CODE, expected 400"
  expect_eq "$1: error" "$(val 'd["error"]')" "invalid_request"
  expect_no_cookie "$1"
}

# keep_session <name>: the access token of $BODY and the refresh cookie of the last response, kept in $tmp/<name>.auth and $tmp/<name>.cookie
keep_session() {
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$1.auth"
  local pair
  pair="$({ grep -i '^set-cookie:[[:space:]]*auth_rt=[^;]' "$tmp/hdr" || true; } | head -n1 | tr -d '\r' | cut -d: -f2- | sed 's/^ *//' | cut -d';' -f1)"
  [[ -n "$pair" ]] || fail "the response set no auth_rt cookie for $1"
  printf 'Cookie: %s\n' "$pair" > "$tmp/$1.cookie"
}

# login <name>: SEED logs in; keeps the session as <name>
login() {
  json_body "$tmp/$1.login" "{'email': os.environ['E2E_SEED_EMAIL'], 'password': os.environ['E2E_SEED_PASSWORD']}"
  call POST /auth/login "$tmp/$1.login"
  expect_status "login of $1" 200
  keep_session "$1"
}

# refresh <name>: refreshes the session of <name> with its cookie; keeps the new tokens on 200. Sets HTTP_CODE.
refresh() {
  call POST /auth/refresh "" "$tmp/$1.cookie"
  if [[ "$HTTP_CODE" == "200" ]]; then keep_session "$1"; fi
}

# claim <name> <python-expression-of-d>: a value of the payload of the access token of the session <name>
claim() {
  BODY="$(jwt_payload "$tmp/$1.auth")"
  val "$2"
}

# --- the operator CLI, in the service's own image ----------------------------------------------------------------

# cli <args...>: runs `auth-server admin <args>` in a one-off container; sets CLI_OUT (stdout), CLI_EXIT, and keeps stderr in $tmp/cli.err
cli() {
  CLI_EXIT=0
  CLI_OUT="$("${compose[@]}" run --rm -T --no-deps auth admin "$@" 2> "$tmp/cli.err")" || CLI_EXIT=$?
  CLI_OUT="${CLI_OUT//$'\r'/}"
}

# --- the mail catcher (step 10) -----------------------------------------------------------------------------------------

mail_count() {
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" -o "$tmp/search.json"
  "$PY" -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages_count"])' < "$tmp/search.json" | tr -d '\r'
}

# wait_mail <address> <count> <seconds>: waits until the catcher holds <count> mails for the address, then saves the newest to $tmp/mail.json
wait_mail() {
  local i id
  for i in $(seq 1 "$3"); do
    if [[ "$(mail_count "$1")" == "$2" ]]; then
      id="$("$PY" -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages"][0]["ID"])' < "$tmp/search.json" | tr -d '\r')"
      curl -sS --max-time 10 "$MAILPIT_URL/api/v1/message/$id" -o "$tmp/mail.json"
      return 0
    fi
    sleep 1
  done
  return 1
}

# token_body <file> [password-env]: takes the token of the invitation link from the text part of $tmp/mail.json, checks that the HTML part
# holds the same token, and writes the request body ({"token"} or {"token","password"}). Never printed.
token_body() {
  "$PY" -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(re.escape(sys.argv[3]) + r"\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no invitation link with a token")
if match.group(1) not in mail["HTML"]:
    sys.exit("the HTML part does not hold the link of the text part")
body = {"token": match.group(1)}
if sys.argv[2]:
    body["password"] = os.environ[sys.argv[2]]
print(json.dumps(body))
' "$1" "${2:-}" "$INVITE_LINK" < "$tmp/mail.json" > "$1"
}

# mail_says <text>...: the text part of $tmp/mail.json holds every one of the texts
mail_says() {
  "$PY" -c '
import json, sys
text = json.load(sys.stdin.buffer)["Text"]
sys.exit(0 if all(needle in text for needle in sys.argv[1:]) else 1)
' "$@" < "$tmp/mail.json"
}

# psql_value <sql>: the value the query returns. The SQL is this script's own text: a value that came from the service under test is
# only ever a UUID or an address checked above, never free text.
psql_value() {
  printf '%s\n' "$1" | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'
}

# --- Step 1: health, the sign-in (criterion 3) -------------------------------------------------------------------------
wait_ok "$BASE_URL/auth/health" 90 || fail "step 1: $BASE_URL/auth/health did not return 200 within 90s"
call GET /auth/health
expect_status "step 1: health" 200 "Healthy"
START_TS="$(psql_value "SELECT now()")"
[[ -n "$START_TS" ]] || fail "the audit log cannot be read: is the compose project of the running stack the one this script uses? (set COMPOSE_PROJECT_NAME)"
login jar
FIRST="$(claim jar 'd["org_id"]')"
SEED_SUB="$(claim jar 'd["sub"]')"
[[ -n "$FIRST" && -n "$SEED_SUB" ]] || fail "step 1: the token of the sign-in has no org_id or sub"
expect_eq "step 1: the sign-in's roles" "$(claim jar 'd["roles"]')" "['admin']"
pass "step 1: health is Healthy; SEED signed in (jar) with a refresh cookie, into the company where SEED is the admin"

# --- Step 2: the list of companies (criteria 12, 13, 14) ---------------------------------------------------------------
call GET /auth/orgs "" "$tmp/jar.auth"
expect_status "step 2: GET /auth/orgs" 200
expect_no_store "step 2: GET /auth/orgs"
expect_eq "step 2: the body is an object with one member, orgs" "$(val 'list(d.keys())')" "['orgs']"
[[ "$(val 'len(d["orgs"])')" == "2" ]] || fail "step 2: SEED is not in exactly two companies ('$SECOND_NAME' is made by the seed at the start of the auth service: use a fresh stack, or restart auth after an earlier run of this script)"
expect_eq "step 2: exactly one entry is current" "$(val 'len([o for o in d["orgs"] if o["current"]])')" "1"
expect_eq "step 2: the current entry is the company of the sign-in" "$(val '[o["id"] for o in d["orgs"] if o["current"]][0]')" "$FIRST"
expect_eq "step 2: the current entry's role" "$(val '[o["role"]["name"] for o in d["orgs"] if o["current"]][0]')" "admin"
expect_eq "step 2: the current entry's kind" "$(val '[o["kind"] for o in d["orgs"] if o["current"]][0]')" "organization"
expect_eq "step 2: the other entry is named as the setting" "$(val '[o["name"] for o in d["orgs"] if not o["current"]][0]')" "$SECOND_NAME"
expect_eq "step 2: the other entry's role" "$(val '[o["role"]["name"] for o in d["orgs"] if not o["current"]][0]')" "user"
expect_eq "step 2: the other entry's kind" "$(val '[o["kind"] for o in d["orgs"] if not o["current"]][0]')" "organization"
expect_eq "step 2: every entry has an id, a name, a kind, a role with an id and a name, and current" \
  "$(val 'all(sorted(o.keys()) == ["current", "id", "kind", "name", "role"] and sorted(o["role"].keys()) == ["id", "name"] for o in d["orgs"])')" "True"
SECOND="$(val '[o["id"] for o in d["orgs"] if not o["current"]][0]')"
[[ -n "$SECOND" && "$SECOND" != "$FIRST" ]] || fail "step 2: the second company has no usable id"
call GET /auth/orgs
expect_status "step 2: GET /auth/orgs without a token" 401
pass "step 2: GET /auth/orgs lists two companies - the first current with the role admin, the second named '$SECOND_NAME' with the role user; without a token it is 401"

# --- Step 3: the switch (criteria 13, 15) ------------------------------------------------------------------------------
export E2E_SECOND_ORG="$SECOND"
json_body "$tmp/switch.json" "{'org_id': os.environ['E2E_SECOND_ORG']}"
call POST /auth/switch "$tmp/switch.json" "$tmp/jar.cookie"
expect_status "step 3: the switch to the second company" 200
expect_no_store "step 3: the switch"
expect_eq "step 3: the body is a refresh's" "$(val 'sorted(d.keys())')" "['access_token']"
keep_session jar
expect_eq "step 3: org_id" "$(claim jar 'd["org_id"]')" "$SECOND"
expect_eq "step 3: org_kind" "$(claim jar 'd["org_kind"]')" "organization"
expect_eq "step 3: roles" "$(claim jar 'd["roles"]')" "['user']"
expect_eq "step 3: sub" "$(claim jar 'd["sub"]')" "$SEED_SUB"
call GET /auth/orgs "" "$tmp/jar.auth"
expect_status "step 3: GET /auth/orgs with the new token" 200
expect_eq "step 3: the current entry follows the token" "$(val '[o["id"] for o in d["orgs"] if o["current"]][0]')" "$SECOND"
expect_eq "step 3: org.switched is in the audit log, from the first company to the second" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'org.switched' AND actor_user_id = '$SEED_SUB' AND subject_user_id = '$SEED_SUB' AND org_id = '$SECOND' AND details->>'from_org_id' = '$FIRST' AND occurred_at >= '$START_TS'")" "1"
pass "step 3: POST /auth/switch with the cookie -> 200 with a refresh's answer; the token says the second company and its role; the list marks it current; org.switched is in the audit log"

# --- Step 4: a refresh stays in the company switched to (criteria 4, 6) ------------------------------------------------
refresh jar
expect_status "step 4: the refresh" 200
expect_eq "step 4: org_id" "$(claim jar 'd["org_id"]')" "$SECOND"
pass "step 4: POST /auth/refresh with the new cookie -> 200, still the second company"

# --- Step 5: a new sign-in lands in the company used last (criterion 4) ------------------------------------------------
login jar2
expect_eq "step 5: org_id" "$(claim jar2 'd["org_id"]')" "$SECOND"
pass "step 5: a new sign-in of SEED (jar2) -> 200 into the second company, the one used last"

# --- Step 6: the refusals (criteria 17, 18) ----------------------------------------------------------------------------
UNKNOWN="$("$PY" -c 'import uuid; print(uuid.uuid4())' | tr -d '\r')"
export E2E_UNKNOWN_ORG="$UNKNOWN"
json_body "$tmp/switch-unknown.json" "{'org_id': os.environ['E2E_UNKNOWN_ORG']}"
call POST /auth/switch "$tmp/switch-unknown.json" "$tmp/jar.cookie"
expect_error "step 6: a switch to an unknown company" 404 not_found
expect_no_cookie "step 6: the 404"
refresh jar
expect_status "step 6: the cookie still refreshes after the 404" 200
expect_eq "step 6: and stays in the second company" "$(claim jar 'd["org_id"]')" "$SECOND"
json_body "$tmp/switch-bad.json" "{'org_id': 'x'}"
call POST /auth/switch "$tmp/switch-bad.json" "$tmp/jar.cookie"
expect_invalid_request "step 6: an org_id that is not a UUID"
call_form /auth/switch "org_id=$SECOND" "$tmp/jar.cookie"
expect_invalid_request "step 6: a form post"
refresh jar
expect_status "step 6: the cookie still refreshes after the 400s" 200
expect_eq "step 6: and stays in the second company" "$(claim jar 'd["org_id"]')" "$SECOND"
pass "step 6: a switch to an unknown UUID -> 404 not_found; {\"org_id\":\"x\"} and a form post -> 400 invalid_request; no cookie written, and the cookie still refreshes"

# --- Step 7: the operator removes SEED from the second company (criterion 26) ------------------------------------------
cli remove-member --org "$SECOND" --email "$SEED_EMAIL"
[[ "$CLI_EXIT" == "0" ]] || fail "step 7: remove-member exit $CLI_EXIT: $(head -c 300 "$tmp/cli.err")"
expect_eq "step 7: remove-member exit code" "$CLI_EXIT" "0"
expect_eq "step 7: remove-member says the account is kept" "$CLI_OUT" "Member removed; the account is kept."
expect_eq "step 7: member.removed is in the audit log for the second company" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'member.removed' AND subject_user_id = '$SEED_SUB' AND org_id = '$SECOND' AND occurred_at >= '$START_TS'")" "1"
expect_eq "step 7: no account.deleted row for SEED" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'account.deleted' AND subject_user_id = '$SEED_SUB'")" "0"
pass "step 7: remove-member --org <second> -> 'Member removed; the account is kept.'; member.removed is in the audit log and account.deleted is not"

# --- Step 8: the devices that worked in the second company are signed out (criterion 8) --------------------------------
refresh jar
expect_error "step 8: jar's refresh" 401 invalid_grant
refresh jar2
expect_error "step 8: jar2's refresh" 401 invalid_grant
pass "step 8: POST /auth/refresh of both sessions of the second company -> 401 invalid_grant"

# --- Step 9: the account stays, in the first company (criteria 4, 26) --------------------------------------------------
login jar3
expect_eq "step 9: org_id" "$(claim jar3 'd["org_id"]')" "$FIRST"
expect_eq "step 9: roles" "$(claim jar3 'd["roles"]')" "['admin']"
expect_eq "step 9: sub" "$(claim jar3 'd["sub"]')" "$SEED_SUB"
pass "step 9: a new sign-in (jar3) -> 200 into the first company: the account is kept, as the next scripts expect"

# --- Step 10: an invitation from a third company keeps SEED's memberships (spec 0017) ---------------------------------------
# GAMMA is a company of this run, named with the run's number so that it never meets the Gamma of e2e-orgs-create.sh. Its admin is made
# with the operator command, as e2e-tenancy.sh makes its companies; the trap above deletes GAMMA at the end.
run="$RANDOM$RANDOM"
GAMMA_NAME="E2E Gamma $run"
GAMMA_ADMIN_EMAIL="gamma-$run@acme.test"
export E2E_GAMMA_ADMIN_EMAIL="$GAMMA_ADMIN_EMAIL" E2E_GAMMA_PASSWORD="E2e-Passw0rd-$run"
cli create-org --name "$GAMMA_NAME"
expect_eq "step 10: create-org exit code" "$CLI_EXIT" "0"
[[ "$CLI_OUT" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]] || fail "step 10: create-org printed more than the company id"
GAMMA_ID="$CLI_OUT"
cli invite --org "$GAMMA_ID" --email "$GAMMA_ADMIN_EMAIL" --role admin
expect_eq "step 10: invite exit code" "$CLI_EXIT" "0"
# The CLI only queues the mail: the server sends it at its next poll, within a minute.
wait_mail "$GAMMA_ADMIN_EMAIL" 1 120 || fail "step 10: no invitation mail for GAMMA's admin within 120s of the CLI invitation"
token_body "$tmp/gamma-admin-accept.json" E2E_GAMMA_PASSWORD || fail "step 10: no usable invitation link in the mail of GAMMA's admin"
call POST /auth/invites/accept "$tmp/gamma-admin-accept.json"
expect_status "step 10: GAMMA's admin accepts" 204 ""
json_body "$tmp/gadmin.login" "{'email': os.environ['E2E_GAMMA_ADMIN_EMAIL'], 'password': os.environ['E2E_GAMMA_PASSWORD']}"
call POST /auth/login "$tmp/gadmin.login"
expect_status "step 10: login of GAMMA's admin" 200
keep_session gadmin
expect_eq "step 10: GAMMA's admin is in GAMMA" "$(claim gadmin 'd["org_id"]')" "$GAMMA_ID"
call GET /auth/org/roles "" "$tmp/gadmin.auth"
expect_status "step 10: GET /auth/org/roles of GAMMA" 200
export E2E_GAMMA_USER_ROLE
E2E_GAMMA_USER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "user"][0]')"
SEED_MAILS="$(mail_count "$SEED_EMAIL")"
json_body "$tmp/invite-seed.json" "{'email': os.environ['E2E_SEED_EMAIL'], 'role_id': os.environ['E2E_GAMMA_USER_ROLE']}"
call POST /auth/org/invites "$tmp/invite-seed.json" "$tmp/gadmin.auth"
expect_status "step 10: GAMMA's admin invites SEED" 202 ""
wait_mail "$SEED_EMAIL" "$((SEED_MAILS + 1))" 90 || fail "step 10: no invitation mail for SEED within 90s"
mail_says "$GAMMA_NAME" "You already have an account on" || fail "step 10: the invitation mail does not name the company and tell an existing account it joins"
token_body "$tmp/seed-invite.json" || fail "step 10: no usable invitation link in SEED's mail"
call POST /auth/invites/preview "$tmp/seed-invite.json"
expect_status "step 10: the preview" 200
expect_no_store "step 10: the preview"
expect_eq "step 10: has_account" "$(val 'd["has_account"]')" "True"
expect_eq "step 10: org_id" "$(val 'd["org_id"]')" "$GAMMA_ID"
expect_eq "step 10: org_name" "$(val 'd["org_name"]')" "$GAMMA_NAME"
call POST /auth/invites/accept "$tmp/seed-invite.json"
expect_status "step 10: the acceptance with the token alone" 204 ""
expect_no_cookie "step 10: the acceptance"
# The session of step 9 (jar3, in FIRST) is not ended by the acceptance.
refresh jar3
expect_status "step 10: jar3 still refreshes" 200
expect_eq "step 10: jar3 stays in the first company" "$(claim jar3 'd["org_id"]')" "$FIRST"
call GET /auth/orgs "" "$tmp/jar3.auth"
expect_status "step 10: GET /auth/orgs" 200
expect_eq "step 10: the list holds the first company and GAMMA, and no other (SECOND was left in step 7)" \
  "$(val "sorted([o['id'] for o in d['orgs']]) == sorted(['$FIRST', '$GAMMA_ID'])")" "True"
expect_eq "step 10: the first company is still the current one" "$(val '[o["id"] for o in d["orgs"] if o["current"]][0]')" "$FIRST"
expect_eq "step 10: GAMMA is listed with the role user" "$(val "[o['role']['name'] for o in d['orgs'] if o['id'] == '$GAMMA_ID'][0]")" "user"
export E2E_GAMMA_ORG="$GAMMA_ID"
json_body "$tmp/switch-gamma.json" "{'org_id': os.environ['E2E_GAMMA_ORG']}"
call POST /auth/switch "$tmp/switch-gamma.json" "$tmp/jar3.cookie"
expect_status "step 10: the switch into GAMMA" 200
keep_session jar3
expect_eq "step 10: org_id after the switch" "$(claim jar3 'd["org_id"]')" "$GAMMA_ID"
expect_eq "step 10: roles after the switch" "$(claim jar3 'd["roles"]')" "['user']"
login jar4
expect_eq "step 10: a new sign-in lands in GAMMA" "$(claim jar4 'd["org_id"]')" "$GAMMA_ID"
expect_eq "step 10: invite.accepted is in the audit log, with no move" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'invite.accepted' AND subject_user_id = '$SEED_SUB' AND org_id = '$GAMMA_ID' AND details->>'left_private_account' IS NULL AND occurred_at >= '$START_TS'")" "1"
pass "step 10: an invitation from GAMMA to SEED: the mail tells an existing account it joins; has_account and org_id in the preview; the token alone -> 204; jar3 still refreshes in the first company; the list holds two companies; a switch and a new sign-in land in GAMMA"

echo "ALL PASS"
