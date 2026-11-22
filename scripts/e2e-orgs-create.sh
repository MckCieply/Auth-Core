#!/usr/bin/env bash
# Real-network end-to-end check of spec 0016 (creating another company: POST /auth/orgs) against the compose stack, with the mail catcher
# (Mailpit) in it. It drives the sequence of the spec's Goal - the development seed user creates a company, switches into it, sees
# themselves as its only member and deletes it; a private person signs up through the mail, creates a company, cannot delete the account
# while in it, deletes the company and then the account.
#
# What it checks, in order (SEED = the development seed user, the admin of the development company; PRIV = a private person whose
# address is made for the run). "jar" and "jar2" are the sessions of SEED, "priv" and "priv2" those of PRIV: the access token in a header
# file and the auth_rt refresh cookie in another, as in the other scripts. Each step names the criteria of the spec it touches; the
# guarding tests are in docs/superpowers/plans/0016-acceptance-map.md.
#   1. health is Healthy; the mail catcher answers; GET /auth/options -> 200 {"signup":"self-service","private_signup":"self-service",
#      "providers":[],"org_creation":"self-service"} (deploy/auth.yaml turns all three on); SEED logs in (jar) -> 200 with the auth_rt
#      cookie; POST /auth/orgs without a token -> 401 - criteria 2, 7.
#   2. SEED creates "Gamma": POST /auth/orgs {"name":"Gamma"} -> 201, no-store, exactly the members id, name, kind (organization), role
#      (an id and the name admin) and current (false), and no cookie written; GET /auth/orgs lists it, not current - criteria 3, 4.
#   3. POST /auth/switch {"org_id": GAMMA} with jar's cookie -> 200; the token's org_id is GAMMA, org_kind organization, roles ["admin"];
#      GET /auth/org/members -> 200 with one member, SEED - criterion 4.
#   4. DELETE /auth/org {"name":"Gamma","password":...} with that token -> 204; the account is kept: a new sign-in (jar2) -> 200, the same
#      account, not in GAMMA, and GET /auth/orgs no longer lists it - criterion 16.
#   5. PRIV signs up privately through the mail (the sign-up mail, the completion with a password -> 200, org_kind personal); PRIV creates
#      "Solo": POST /auth/orgs -> 201 with the entry's shape - criteria 3, 11.
#   6. PRIV's DELETE /auth/account with the password -> 403 {"error":"company_member"}: the account is in a company - criterion 15.
#   7. POST /auth/switch {"org_id": SOLO} with PRIV's cookie -> 200, org_id SOLO, roles ["admin"]; DELETE /auth/org {"name":"Solo",
#      "password":...} -> 204; a new sign-in (priv2) -> 200, org_kind personal, the same account - criterion 16.
#   8. PRIV's DELETE /auth/account with the new token -> 204; PRIV's login -> the 401 of an unknown address - criterion 16.
#
# Cleanup: a trap deletes the companies this run created (Gamma, Solo) and PRIV's account even when a step fails, so the script passes
# when run again on the same stack. It uses no docker compose command and so touches no stack: only the services at BASE_URL and
# MAILPIT_URL.
#
# Full sequence, from the repo root (the stack and .env of the other e2e scripts, with the per-IP limiter OFF as for them; run this one
# after e2e-orgs.sh and before e2e-hardening.sh, which recreates the stack with the limits on):
#   cp .env.example .env                  # then set real local values (git-ignored)
#   scripts/dev-keys.sh                   # on Windows: MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh
#   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, never "auth-core" (the development stack)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build
#   scripts/e2e-login.sh; scripts/e2e-refresh.sh; scripts/e2e-lockout.sh; scripts/e2e-email.sh; scripts/e2e-tenancy.sh; scripts/e2e-signup.sh; scripts/e2e-private.sh; scripts/e2e-orgs.sh
#   scripts/e2e-orgs-create.sh            # this script (does NOT bring the stack up or down)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# Reads AUTH_DEV_SEED_EMAIL and AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced). Needs: curl, python3 (standard
# library only; E2E_PYTHON=<interpreter> names another, for example E2E_PYTHON=C:/p6v/Scripts/python.exe). Env: BASE_URL (default
# http://localhost:8080), MAILPIT_URL (default http://localhost:8025). The links of the mails start with http://localhost:4200
# (appsettings.Development.json). Takes under a minute. Re-runnable on the same stack: PRIV's address is made for the run. Exits non-zero
# on the first failure; prints "PASS <step>" per step; never prints a password, a token, a cookie or a mail body. Request bodies are
# built into files in a mktemp -d directory (removed on exit) and curl reads them with --data-binary @file; tokens and cookies reach curl
# through header files.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
PY="${E2E_PYTHON:-python3}"
SIGNUP_LINK="http://localhost:4200/complete-signup"

tmp="$(mktemp -d)"
# What the run made and has not yet deleted, for the cleanup (see cleanup below).
GAMMA_ID="" GAMMA_GONE="" SOLO_ID="" SOLO_GONE="" PRIV_UP="" PRIV_GONE=""

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
PRIV_EMAIL="sola-$run@poczta.test"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
export E2E_PRIV_EMAIL="$PRIV_EMAIL" E2E_PRIV_PASSWORD="E2e-Passw0rd-$run"

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

# keep_session <name>: the access token of $BODY and the refresh cookie of the last response, kept in $tmp/<name>.auth and $tmp/<name>.cookie
keep_session() {
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$1.auth"
  local pair
  pair="$({ grep -i '^set-cookie:[[:space:]]*auth_rt=[^;]' "$tmp/hdr" || true; } | head -n1 | tr -d '\r' | cut -d: -f2- | sed 's/^ *//' | cut -d';' -f1)"
  [[ -n "$pair" ]] || fail "the response set no auth_rt cookie for $1"
  printf 'Cookie: %s\n' "$pair" > "$tmp/$1.cookie"
}

# login <name> <email-env> <password-env>: logs in; keeps the session as <name>
login() {
  json_body "$tmp/$1.login" "{'email': os.environ['$2'], 'password': os.environ['$3']}"
  call POST /auth/login "$tmp/$1.login"
  expect_status "login of $1" 200
  keep_session "$1"
}

# claim <name> <python-expression-of-d>: a value of the payload of the access token of the session <name>
claim() {
  BODY="$(jwt_payload "$tmp/$1.auth")"
  val "$2"
}

# switch_to <name> <org-id>: switches the session <name> to the company, with its cookie; keeps the new tokens
switch_to() {
  export E2E_TARGET_ORG="$2"
  json_body "$tmp/$1.switch" "{'org_id': os.environ['E2E_TARGET_ORG']}"
  call POST /auth/switch "$tmp/$1.switch" "$tmp/$1.cookie"
  expect_status "the switch of $1" 200
  keep_session "$1"
}

# delete_org <name> <company-name> <password-env>: DELETE /auth/org with the session <name>'s token (which must be in that company) -> 204
delete_org() {
  json_body "$tmp/$1.delorg" "{'name': '$2', 'password': os.environ['$3']}"
  call DELETE /auth/org "$tmp/$1.delorg" "$tmp/$1.auth"
  expect_status "deleting the company $2" 204 ""
}

# delete_account <name> <password-env>: DELETE /auth/account with the session <name>'s token
delete_account() {
  json_body "$tmp/$1.delacc" "{'password': os.environ['$2']}"
  call DELETE /auth/account "$tmp/$1.delacc" "$tmp/$1.auth"
}

# check_entry <what> <company-name>: the last answer is a 201 with the entry of a new company, as GET /auth/orgs lists it; sets ENTRY_ID
check_entry() {
  expect_status "$1" 201
  expect_no_store "$1"
  [[ -z "$(header set-cookie)" ]] || fail "$1: the response wrote a cookie"
  expect_eq "$1: the members of the entry" "$(val 'sorted(d.keys())')" "['current', 'id', 'kind', 'name', 'role']"
  expect_eq "$1: the members of the role" "$(val 'sorted(d["role"].keys())')" "['id', 'name']"
  expect_eq "$1: name" "$(val 'd["name"]')" "$2"
  expect_eq "$1: kind" "$(val 'd["kind"]')" "organization"
  expect_eq "$1: the creator's role" "$(val 'd["role"]["name"]')" "admin"
  expect_eq "$1: current" "$(val 'd["current"]')" "False"
  ENTRY_ID="$(val 'd["id"]')"
  [[ -n "$ENTRY_ID" ]] || fail "$1: the entry has no id"
}

# --- the mail catcher (as in e2e-private.sh) ---------------------------------------------------------------------------

mail_count() {
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" -o "$tmp/search.json"
  "$PY" -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages_count"])' < "$tmp/search.json" | tr -d '\r'
}

# wait_mail <address> <subject-start> <seconds> [more-than]: waits until the catcher holds more than <more-than> (default 0) mails to the
# address and the newest whose subject starts so is to exactly this address; saves that one to $tmp/mail.json
wait_mail() {
  local i id
  for i in $(seq 1 "$3"); do
    if [[ "$(mail_count "$1")" -gt "${4:-0}" ]]; then
      id="$("$PY" -c '
import json, sys
for message in json.load(sys.stdin.buffer)["messages"]:
    if message["Subject"].startswith(sys.argv[1]) and any(t["Address"].lower() == sys.argv[2].lower() for t in (message["To"] or [])):
        print(message["ID"])
        break
' "$2" "$1" < "$tmp/search.json" | tr -d '\r')"
      if [[ -n "$id" ]]; then
        curl -sS --max-time 10 "$MAILPIT_URL/api/v1/message/$id" -o "$tmp/mail.json"
        return 0
      fi
    fi
    sleep 1
  done
  return 1
}

# token_body <link-prefix> <file> [password-env]: takes the token from the text part of $tmp/mail.json, checks that the HTML part holds
# the same link, and writes the request body ({"token"}, plus "password" when named). Never printed.
token_body() {
  "$PY" -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(re.escape(sys.argv[1]) + r"\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no link with a token")
if sys.argv[1] + "?token=" + match.group(1) not in mail["HTML"]:
    sys.exit("the HTML part does not hold the link of the text part")
body = {"token": match.group(1)}
if sys.argv[2]:
    body["password"] = os.environ[sys.argv[2]]
print(json.dumps(body))
' "$1" "${3:-}" < "$tmp/mail.json" > "$2"
}

# --- the cleanup ---------------------------------------------------------------------------------------------------------
# Deletes what the run made and has not deleted (even when a step failed), so the script passes when run again on the same stack. Each
# part runs in a subshell: a `fail` inside it ends that part only. A leftover is reported on stderr; the exit status stays the run's.
cleanup() {
  set +e
  if [[ -n "$GAMMA_ID" && -z "$GAMMA_GONE" ]]; then
    if ( login cseed E2E_SEED_EMAIL E2E_SEED_PASSWORD && switch_to cseed "$GAMMA_ID" && delete_org cseed Gamma E2E_SEED_PASSWORD ) > /dev/null 2>&1; then
      echo "cleanup: deleted the company Gamma" >&2
    else
      echo "cleanup: could not delete the company Gamma ($GAMMA_ID): delete it by hand" >&2
    fi
  fi
  if [[ -n "$PRIV_UP" && -z "$PRIV_GONE" ]]; then
    if (
      set -e
      login cpriv E2E_PRIV_EMAIL E2E_PRIV_PASSWORD
      if [[ -n "$SOLO_ID" && -z "$SOLO_GONE" ]]; then
        switch_to cpriv "$SOLO_ID"
        delete_org cpriv Solo E2E_PRIV_PASSWORD
        login cpriv E2E_PRIV_EMAIL E2E_PRIV_PASSWORD
      fi
      delete_account cpriv E2E_PRIV_PASSWORD
      expect_status "deleting the account" 204 ""
    ) > /dev/null 2>&1; then
      echo "cleanup: deleted the private account and its company" >&2
    else
      echo "cleanup: could not delete the private account $PRIV_EMAIL or its company Solo ($SOLO_ID): delete them by hand" >&2
    fi
  fi
  rm -rf "$tmp"
}
trap cleanup EXIT

# --- Step 1: health, the options, the sign-in (criteria 2, 7) ----------------------------------------------------------
wait_ok "$BASE_URL/auth/health" 90 || fail "step 1: $BASE_URL/auth/health did not return 200 within 90s"
wait_ok "$MAILPIT_URL/readyz" 60 || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s (is the mailpit service up?)"
call GET /auth/health
expect_status "step 1: health" 200 "Healthy"
call GET /auth/options
expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service","providers":[],"org_creation":"self-service"}'
expect_no_store "step 1: the options"
json_body "$tmp/gamma.json" "{'name': 'Gamma'}"
call POST /auth/orgs "$tmp/gamma.json"
expect_status "step 1: POST /auth/orgs without a token" 401
login jar E2E_SEED_EMAIL E2E_SEED_PASSWORD
FIRST="$(claim jar 'd["org_id"]')"
SEED_SUB="$(claim jar 'd["sub"]')"
[[ -n "$FIRST" && -n "$SEED_SUB" ]] || fail "step 1: the token of the sign-in has no org_id or sub"
pass "step 1: health is Healthy; GET /auth/options says org_creation self-service; POST /auth/orgs without a token is 401; SEED signed in (jar)"

# --- Step 2: SEED creates a company (criteria 3, 4) --------------------------------------------------------------------
call POST /auth/orgs "$tmp/gamma.json" "$tmp/jar.auth"
check_entry "step 2: POST /auth/orgs {\"name\":\"Gamma\"}" Gamma
GAMMA_ID="$ENTRY_ID"
[[ "$GAMMA_ID" != "$FIRST" ]] || fail "step 2: the new company has the id of the company of the sign-in"
call GET /auth/orgs "" "$tmp/jar.auth"
expect_status "step 2: GET /auth/orgs" 200
expect_eq "step 2: the list holds Gamma once" "$(val 'len([o for o in d["orgs"] if o["name"] == "Gamma"])')" "1"
expect_eq "step 2: Gamma is not current" "$(val '[o["current"] for o in d["orgs"] if o["name"] == "Gamma"][0]')" "False"
expect_eq "step 2: Gamma's id in the list" "$(val '[o["id"] for o in d["orgs"] if o["name"] == "Gamma"][0]')" "$GAMMA_ID"
pass "step 2: POST /auth/orgs -> 201 with the entry (admin, organization, not current); GET /auth/orgs lists it"

# --- Step 3: the switch into it (criterion 4) ------------------------------------------------------------------------
switch_to jar "$GAMMA_ID"
expect_eq "step 3: org_id" "$(claim jar 'd["org_id"]')" "$GAMMA_ID"
expect_eq "step 3: org_kind" "$(claim jar 'd["org_kind"]')" "organization"
expect_eq "step 3: roles" "$(claim jar 'd["roles"]')" "['admin']"
expect_eq "step 3: sub" "$(claim jar 'd["sub"]')" "$SEED_SUB"
call GET /auth/org/members "" "$tmp/jar.auth"
expect_status "step 3: GET /auth/org/members" 200
expect_eq "step 3: one member" "$(val 'len(d["members"])')" "1"
expect_eq "step 3: the member is SEED" "$(val 'd["members"][0]["email"].lower()')" "$(printf '%s' "$SEED_EMAIL" | tr '[:upper:]' '[:lower:]')"
pass "step 3: POST /auth/switch into Gamma -> 200, the token says Gamma, organization, admin; GET /auth/org/members -> one member, SEED"

# --- Step 4: SEED deletes the company, the account is kept (criterion 16) -------------------------------------------
delete_org jar Gamma E2E_SEED_PASSWORD
GAMMA_GONE=1
login jar2 E2E_SEED_EMAIL E2E_SEED_PASSWORD
expect_eq "step 4: the same account" "$(claim jar2 'd["sub"]')" "$SEED_SUB"
[[ "$(claim jar2 'd["org_id"]')" != "$GAMMA_ID" ]] || fail "step 4: the new sign-in is in the deleted company"
call GET /auth/orgs "" "$tmp/jar2.auth"
expect_status "step 4: GET /auth/orgs" 200
expect_eq "step 4: the list no longer holds Gamma" "$(val 'len([o for o in d["orgs"] if o["name"] == "Gamma"])')" "0"
pass "step 4: DELETE /auth/org with the name and the password -> 204; the account is kept: a new sign-in works and Gamma is gone"

# --- Step 5: a private person creates a company (criteria 3, 11) --------------------------------------------------------
MAILS_BEFORE="$(mail_count "$PRIV_EMAIL")"
json_body "$tmp/priv.signup" "{'email': os.environ['E2E_PRIV_EMAIL'], 'kind': 'private'}"
call POST /auth/signup "$tmp/priv.signup"
expect_status "step 5: the private sign-up request" 202 ""
wait_mail "$PRIV_EMAIL" "Complete your sign-up" 90 "$MAILS_BEFORE" || fail "step 5: no sign-up mail for PRIV within 90s"
token_body "$SIGNUP_LINK" "$tmp/priv.complete" E2E_PRIV_PASSWORD || fail "step 5: no usable sign-up link in the mail"
call POST /auth/signup/complete "$tmp/priv.complete"
expect_status "step 5: the completion" 200
PRIV_UP=1
keep_session priv
PRIV_SUB="$(claim priv 'd["sub"]')"
expect_eq "step 5: org_kind after the sign-up" "$(claim priv 'd["org_kind"]')" "personal"
json_body "$tmp/solo.json" "{'name': 'Solo'}"
call POST /auth/orgs "$tmp/solo.json" "$tmp/priv.auth"
check_entry "step 5: POST /auth/orgs {\"name\":\"Solo\"}" Solo
SOLO_ID="$ENTRY_ID"
pass "step 5: a private person signed up through the mail and created Solo: 201 with the entry"

# --- Step 6: an account in a company cannot delete itself (criterion 15) -----------------------------------------------
delete_account priv E2E_PRIV_PASSWORD
expect_error "step 6: DELETE /auth/account while in a company" 403 company_member
pass "step 6: DELETE /auth/account of a person who is in a company -> 403 company_member"

# --- Step 7: the company is deleted, the account stays personal (criterion 16) --------------------------------------
switch_to priv "$SOLO_ID"
expect_eq "step 7: org_id" "$(claim priv 'd["org_id"]')" "$SOLO_ID"
expect_eq "step 7: roles" "$(claim priv 'd["roles"]')" "['admin']"
delete_org priv Solo E2E_PRIV_PASSWORD
SOLO_GONE=1
login priv2 E2E_PRIV_EMAIL E2E_PRIV_PASSWORD
expect_eq "step 7: org_kind of a new sign-in" "$(claim priv2 'd["org_kind"]')" "personal"
expect_eq "step 7: the same account" "$(claim priv2 'd["sub"]')" "$PRIV_SUB"
pass "step 7: POST /auth/switch into Solo, DELETE /auth/org -> 204; a new sign-in lands in the personal space of the same account"

# --- Step 8: the account can be deleted now (criterion 16) -------------------------------------------------------------
delete_account priv2 E2E_PRIV_PASSWORD
expect_status "step 8: DELETE /auth/account" 204 ""
PRIV_GONE=1
json_body "$tmp/priv.login" "{'email': os.environ['E2E_PRIV_EMAIL'], 'password': os.environ['E2E_PRIV_PASSWORD']}"
call POST /auth/login "$tmp/priv.login"
expect_error "step 8: PRIV's login is that of an unknown address" 401 invalid_credentials
pass "step 8: DELETE /auth/account of the person with no company -> 204; the login is refused"

echo "ALL PASS"
