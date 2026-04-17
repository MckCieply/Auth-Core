#!/usr/bin/env bash
# Real-network end-to-end check of spec 0010 (private accounts) against the compose stack, with the mail catcher (Mailpit) in it. It drives
# the sequence of the spec's Goal - a private person signs up through the mail, is signed in to a space of their own and deletes the
# account - and the cases around it.
#
# What it checks, in order (PRIV = the private person, INV = a second private person whom the development company invites, SEED = the
# development seed user, the admin of the development company). Each step names the criteria it touches; the guarding tests are in
# docs/superpowers/plans/0010-acceptance-map.md.
#   1. health is Healthy; the mail catcher answers; GET /auth/options -> 200 {"signup":"self-service","private_signup":"self-service"}
#      (deploy/auth.yaml turns both on); the OpenAPI description names DELETE /auth/account - part of criteria 2 and 30.
#   2. PRIV asks for a private sign-up -> 202 with an empty body; the mail "Complete your sign-up" has the private intro and the same link in
#      its text and HTML parts; the preview -> 200 {"kind":"private","email":PRIV}; a weak password -> 400 weak_password; the password ->
#      200 with a login's body and the auth_rt cookie; the token says org_kind personal, roles ["private"] and the private permissions of
#      deploy/auth.yaml; signup.requested and signup.completed say kind private, and no org.* row names the space - criteria 4, 8, 9, 10.
#   3. GET /auth/me -> org_kind personal, org_name private; GET /auth/org and GET /auth/org/members -> 403 forbidden - criteria 12, 14.
#   4. a minute later an organization request for PRIV -> 202; the mail "You already have an account" holds the sentence about a new
#      account on purpose, and no link - criterion 5.
#   5. INV signs up privately through the mail; SEED invites INV through the company API; the invitation mail holds the paragraph about the
#      private account; the preview says has_private_account true; the acceptance without leave_private_account -> 409
#      private_account_exists; with it -> 204; INV's cookie of its sign-up -> 401 invalid_grant; INV signs in with its own password, into
#      the development company, as user, org_kind organization; invite.accepted holds left_private_account true and the old space's id -
#      criteria 16 to 19.
#   6. PRIV deletes the account: a wrong password -> 403 wrong_password and an account.delete_refused row; the right one -> 204 and the
#      auth_rt cookie cleared (Max-Age=0); PRIV's refresh cookie -> 401 invalid_grant; PRIV's login -> the 401 of an unknown address;
#      account.deleted with the reason self; PRIV signs up privately again through the mail and finishes -> 200: the address is free -
#      criteria 21, 22.
#   7. SEED's DELETE /auth/account -> 403 forbidden - criterion 22.
#   8. SEED removes INV through the company API -> 204; INV's login -> the 401 of an unknown address; account.deleted with the reason
#      member_removed - criterion 23.
#   9. no audit row of the run holds a password, a link or a session token of the run - criterion 29.
#
# Full sequence, from the repo root (the stack and .env of the other e2e scripts, with the per-IP limiter OFF as for them; run this one after
# e2e-signup.sh and before e2e-hardening.sh, which recreates the stack with the limits on):
#   cp .env.example .env                  # then set real local values (git-ignored)
#   scripts/dev-keys.sh                   # on Windows: MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh
#   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, never "auth-core" (the development stack)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build
#   scripts/e2e-login.sh; scripts/e2e-refresh.sh; scripts/e2e-lockout.sh; scripts/e2e-email.sh; scripts/e2e-tenancy.sh; scripts/e2e-signup.sh
#   scripts/e2e-private.sh                # this script (does NOT bring the stack up or down)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# Reads AUTH_DEV_SEED_EMAIL and AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced). The database is read with
# `docker compose exec postgres psql`. Needs: curl, python3 (standard library only; E2E_PYTHON=<interpreter> names another, for example
# E2E_PYTHON=C:/p6v/Scripts/python.exe), docker compose. Env: BASE_URL (default http://localhost:8080), MAILPIT_URL (default
# http://localhost:8025). The links of the mails start with http://localhost:4200 (appsettings.Development.json). Takes about three
# minutes: the mail limit of PRIV (one request a minute) is waited out twice. Re-runnable on the same stack: every address is made for the
# run. Exits non-zero on the first failure; prints "PASS <step>" per step; never prints a password, a token, a cookie or a mail body.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
PY="${E2E_PYTHON:-python3}"
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-hardening}"
[[ "$COMPOSE_PROJECT_NAME" != "auth-core" ]] || { echo "FAIL COMPOSE_PROJECT_NAME=auth-core is the development stack; use another name" >&2; exit 1; }
compose=(docker compose -f "$root/deploy/docker-compose.yml" --env-file "$root/.env")
SIGNUP_LINK="http://localhost:4200/complete-signup"
INVITE_LINK="http://localhost:4200/invite"

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
PRIV_EMAIL="ola-$run@poczta.test"
INV_EMAIL="jan-$run@poczta.test"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
export E2E_PRIV_EMAIL="$PRIV_EMAIL" E2E_INV_EMAIL="$INV_EMAIL" E2E_COMPANY="Firma $run"
export E2E_PASSWORD="E2e-Passw0rd-$run" E2E_INV_PASSWORD="E2e-Inv-Passw0rd-$run" E2E_AGAIN_PASSWORD="E2e-Again-Passw0rd-$run"
export E2E_IGNORED_PASSWORD="E2e-Ignored-Passw0rd-$run" E2E_WRONG_PASSWORD="E2e-Wrong-Passw0rd-$run" E2E_WEAK_PASSWORD="abc"
export E2E_PRIVATE_INTRO="Someone asked to create a private account on"
export E2E_SENTENCE="If you want a new account on purpose, delete the one you have first, or sign up with another email address."
export E2E_PARAGRAPH="You already have a private account on"

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

# login <name> <email-env> <password-env>: logs in, keeps the bearer header in $tmp/<name>.auth
login() {
  json_body "$tmp/$1.login" "{'email': os.environ['$2'], 'password': os.environ['$3']}"
  call POST /auth/login "$tmp/$1.login"
  expect_status "login of $1" 200
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$1.auth"
}

set_cookie_line() { # the auth_rt Set-Cookie line of the last response, CR stripped (empty when there is none)
  { grep -i '^set-cookie: auth_rt=' "$tmp/hdr" || true; } | head -n1 | tr -d '\r'
}

cookie_value() { # value of the auth_rt cookie in the last response
  local line
  line="$(set_cookie_line)"
  line="${line#*: }"
  line="${line#auth_rt=}"
  printf '%s' "${line%%;*}"
}

has_attr() { # has_attr <set-cookie-line> <attribute>: exact, case-insensitive match of one "; "-separated attribute
  printf '%s' "$1" | tr ';' '\n' | sed 's/^ *//' | grep -qixF -- "$2"
}

# --- the mail catcher ---------------------------------------------------------------------------------------------

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

# token_body <link-prefix> <file> [password-env] [leave]: takes the token from the text part of $tmp/mail.json, checks that the HTML part
# holds the same link, and writes the request body ({"token"}, plus "password" when named, plus "leave_private_account": true when <leave>
# is "leave"). Never printed.
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
if sys.argv[3] == "leave":
    body["leave_private_account"] = True
print(json.dumps(body))
' "$1" "${3:-}" "${4:-}" < "$tmp/mail.json" > "$2"
}

token_of() { "$PY" -c 'import json,sys; print(json.load(sys.stdin)["token"])' < "$1" | tr -d '\r'; }   # never printed by the callers

mail_field() { "$PY" -c 'import json,sys; print(json.load(sys.stdin.buffer)[sys.argv[1]])' "$1" < "$tmp/mail.json" | tr -d '\r'; }

mail_holds() { # mail_holds <env-name>: whether the text part and the HTML part of $tmp/mail.json both hold the value of that variable
  "$PY" -c '
import json, os, sys
mail = json.load(sys.stdin.buffer)
value = os.environ[sys.argv[1]]
sys.exit(0 if value in mail["Text"] and value in mail["HTML"] else 1)
' "$1" < "$tmp/mail.json"
}

mail_has_no_link() { # neither part of $tmp/mail.json holds a token parameter or a URL
  "$PY" -c '
import json, sys
mail = json.load(sys.stdin.buffer)
sys.exit(0 if all("token=" not in part and "://" not in part for part in (mail["Text"], mail["HTML"])) else 1)
' < "$tmp/mail.json"
}

# psql_value <sql>: the value the query returns. The SQL is this script's own text: a value that came from the service under test is never put into it.
psql_value() {
  printf '%s\n' "$1" | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'
}

# secret_in_audit <secret>: how many rows of the audit log hold the text in any column; the secret goes on stdin only, and psql's standard
# error (which would echo a failed query) goes to a file that is never printed.
secret_in_audit() {
  local count
  [[ "$1" != *"'"* ]] || fail "a secret with a quote cannot be looked for"
  count="$(printf "SELECT count(*) FROM audit_events WHERE position('%s' in (kind || ' ' || coalesce(subject_email, '') || ' ' || coalesce(org_name, '') || ' ' || coalesce(client_ip, '') || ' ' || coalesce(details::text, ''))) > 0;\n" "$1" \
    | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA 2> "$tmp/psql.err" | tr -d '\r' || true)"
  [[ "$count" =~ ^[0-9]+$ ]] || fail "the audit log could not be searched for a secret (the database answer is not a number)"
  printf '%s\n' "$count"
}

# request_signup <body-file> <what>: POST /auth/signup; a 429 of the mail limit of the address is waited out once
request_signup() {
  call POST /auth/signup "$1"
  if [[ "$HTTP_CODE" == "429" ]]; then
    [[ "$BODY" =~ ^\{\"error\":\"too_many_attempts\",\"retry_after_seconds\":([0-9]+)\}$ ]] || fail "$2: unexpected 429 body"
    echo "waiting $((BASH_REMATCH[1] + 1)) s for the mail limit of the address..."
    sleep "$((BASH_REMATCH[1] + 1))"
    call POST /auth/signup "$1"
  fi
  expect_status "$2" 202 ""
}

# private_signup <email-env> <password-env> <name>: a private sign-up through the mail, to the end; the completion's bearer header is kept in
# $tmp/<name>.auth and its refresh cookie in $tmp/<name>.cookie (a header file for curl)
private_signup() {
  local before
  before="$(mail_count "${!1}")"
  json_body "$tmp/$3.signup" "{'email': os.environ['$1'], 'kind': 'private'}"
  request_signup "$tmp/$3.signup" "the private sign-up request of $3"
  wait_mail "${!1}" "Complete your sign-up" 90 "$before" || fail "no new sign-up mail for $3 within 90s"
  token_body "$SIGNUP_LINK" "$tmp/$3.complete" "$2" || fail "no usable sign-up link in the mail of $3"
  call POST /auth/signup/complete "$tmp/$3.complete"
  expect_status "the completion of $3" 200
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$3.auth"
  printf 'Cookie: auth_rt=%s\n' "$(cookie_value)" > "$tmp/$3.cookie"
}

# --- Step 1: health, the mail catcher, the options, the description (criteria 2, 30) ------------------------------------
wait_ok "$BASE_URL/auth/health" 90 || fail "step 1: $BASE_URL/auth/health did not return 200 within 90s"
wait_ok "$MAILPIT_URL/readyz" 60 || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s (is the mailpit service up?)"
call GET /auth/health
expect_status "step 1: health" 200 "Healthy"
call GET /auth/options
expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service"}'
expect_no_store "step 1: the options"
call GET /auth/openapi/v1.json
expect_status "step 1: the OpenAPI description" 200
expect_eq "step 1: DELETE /auth/account is described" "$(val "'delete' in d['paths'].get('/auth/account', {})")" "True"
pass "step 1: health is Healthy; GET /auth/options says both kinds of sign-up are on; the description names DELETE /auth/account"

# --- Step 2: a private sign-up through the mail (criteria 4, 8, 9, 10) --------------------------------------------------
START_TS="$(psql_value "SELECT now()")"
json_body "$tmp/priv.signup" "{'email': os.environ['E2E_PRIV_EMAIL'], 'kind': 'private'}"
call POST /auth/signup "$tmp/priv.signup"
expect_status "step 2: the private sign-up request" 202 ""
expect_no_store "step 2: the 202"
wait_mail "$PRIV_EMAIL" "Complete your sign-up" 90 || fail "step 2: no sign-up mail for PRIV within 90s"
mail_holds E2E_PRIVATE_INTRO || fail "step 2: the text and the HTML part do not both hold the private intro"
token_body "$SIGNUP_LINK" "$tmp/priv.preview" || fail "step 2: no usable sign-up link in the mail"
token_body "$SIGNUP_LINK" "$tmp/priv.weak" E2E_WEAK_PASSWORD || fail "step 2: the weak completion body could not be made"
token_body "$SIGNUP_LINK" "$tmp/priv.complete" E2E_PASSWORD || fail "step 2: the completion body could not be made"
PRIV_LINK="$(token_of "$tmp/priv.preview")"
call POST /auth/signup/preview "$tmp/priv.preview"
expect_status "step 2: preview" 200 "{\"kind\":\"private\",\"email\":\"$PRIV_EMAIL\"}"
call POST /auth/signup/complete "$tmp/priv.weak"
expect_status "step 2: a weak password" 400 '{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}'
call POST /auth/signup/complete "$tmp/priv.complete"
expect_status "step 2: the completion" 200
expect_eq "step 2: the body is a login's" "$(val 'sorted(d.keys())')" "['access_token', 'status']"
LINE="$(set_cookie_line)"
[[ -n "$LINE" ]] || fail "step 2: the completion set no auth_rt cookie"
for attr in HttpOnly Secure SameSite=Strict Path=/auth; do
  has_attr "$LINE" "$attr" || fail "step 2: the auth_rt cookie lacks the $attr attribute"
done
PRIV_ACCESS="$(val 'd["access_token"]')"
PRIV_COOKIE="$(cookie_value)"
printf 'Authorization: Bearer %s\n' "$PRIV_ACCESS" > "$tmp/priv.auth"
printf 'Cookie: auth_rt=%s\n' "$PRIV_COOKIE" > "$tmp/priv.cookie"
BODY="$(jwt_payload "$tmp/priv.auth")"
expect_eq "step 2: org_kind" "$(val 'd["org_kind"]')" "personal"
expect_eq "step 2: roles" "$(val 'd["roles"]')" "['private']"
expect_eq "step 2: permissions" "$(val 'd["permissions"]')" "['documents:read', 'documents:write']"
PRIV_SPACE="$(val 'd["org_id"]')"
PRIV_SUB="$(val 'd["sub"]')"
expect_eq "step 2: signup.requested says kind private" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.requested' AND subject_email = '$PRIV_EMAIL' AND details->>'kind' = 'private' AND occurred_at >= '$START_TS'")" "1"
expect_eq "step 2: signup.completed says kind private and names the space" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.completed' AND subject_user_id = '$PRIV_SUB' AND org_id = '$PRIV_SPACE' AND org_name = 'private' AND details->>'kind' = 'private'")" "1"
expect_eq "step 2: no org.* row names the space" "$(psql_value "SELECT count(*) FROM audit_events WHERE kind LIKE 'org.%' AND org_id = '$PRIV_SPACE'")" "0"
expect_eq "step 2: the space is personal" "$(psql_value "SELECT \"Kind\" FROM \"Companies\" WHERE \"Id\" = '$PRIV_SPACE'")" "2"
pass "step 2: a private sign-up through the mail: 202, the private mail, the preview of its kind, weak_password, then a login's answer with org_kind personal and the private role; the audit rows say kind private"

# --- Step 3: /auth/me and the company API (criteria 12, 14) -------------------------------------------------------------
call GET /auth/me "" "$tmp/priv.auth"
expect_status "step 3: GET /auth/me" 200
expect_eq "step 3: org_kind" "$(val 'd["org_kind"]')" "personal"
expect_eq "step 3: org_name" "$(val 'd["org_name"]')" "private"
expect_eq "step 3: roles" "$(val 'd["roles"]')" "['private']"
call GET /auth/org "" "$tmp/priv.auth"
expect_error "step 3: GET /auth/org" 403 forbidden
call GET /auth/org/members "" "$tmp/priv.auth"
expect_error "step 3: GET /auth/org/members" 403 forbidden
pass "step 3: /auth/me says personal and private; the company API answers 403 forbidden, GET /auth/org included"

# --- Step 4: an account that exists gets the notice (criterion 5) -------------------------------------------------------
MAILS="$(mail_count "$PRIV_EMAIL")"
json_body "$tmp/priv.org" "{'email': os.environ['E2E_PRIV_EMAIL'], 'company_name': os.environ['E2E_COMPANY']}"
request_signup "$tmp/priv.org" "step 4: an organization request for PRIV"
wait_mail "$PRIV_EMAIL" "You already have an account" 90 "$MAILS" || fail "step 4: no 'you already have an account' mail for PRIV within 90s"
mail_holds E2E_SENTENCE || fail "step 4: the mail does not hold the sentence about a new account on purpose"
mail_has_no_link || fail "step 4: the mail holds a link or a token"
pass "step 4: an organization request for an address with a private account -> 202 and the mail that says so, with the new sentence and no link"

# --- Step 5: the invitation of a private person (criteria 16 to 19) -----------------------------------------------------
private_signup E2E_INV_EMAIL E2E_INV_PASSWORD inv
BODY="$(jwt_payload "$tmp/inv.auth")"
INV_SPACE="$(val 'd["org_id"]')"
INV_SUB="$(val 'd["sub"]')"
login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD
call GET /auth/me "" "$tmp/seed.auth"
expect_status "step 5: GET /auth/me of SEED" 200
DEV_ORG="$(val 'd["org_id"]')"
call GET /auth/org/roles "" "$tmp/seed.auth"
expect_status "step 5: GET /auth/org/roles" 200
export E2E_USER_ROLE
E2E_USER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "user"][0]')"
MAILS="$(mail_count "$INV_EMAIL")"
json_body "$tmp/invite.json" "{'email': os.environ['E2E_INV_EMAIL'], 'role_id': os.environ['E2E_USER_ROLE']}"
call POST /auth/org/invites "$tmp/invite.json" "$tmp/seed.auth"
expect_status "step 5: SEED invites INV" 202 ""
wait_mail "$INV_EMAIL" "You are invited" 90 "$MAILS" || fail "step 5: no invitation mail for INV within 90s"
mail_holds E2E_PARAGRAPH || fail "step 5: the invitation mail does not hold the paragraph about the private account"
token_body "$INVITE_LINK" "$tmp/inv.preview" || fail "step 5: no usable invitation link"
token_body "$INVITE_LINK" "$tmp/inv.plain" E2E_IGNORED_PASSWORD || fail "step 5: the acceptance body could not be made"
token_body "$INVITE_LINK" "$tmp/inv.leave" E2E_IGNORED_PASSWORD leave || fail "step 5: the moving acceptance body could not be made"
INV_LINK="$(token_of "$tmp/inv.preview")"
call POST /auth/invites/preview "$tmp/inv.preview"
expect_status "step 5: preview" 200
expect_eq "step 5: has_private_account" "$(val 'd["has_private_account"]')" "True"
call POST /auth/invites/accept "$tmp/inv.plain"
expect_error "step 5: the acceptance without leave_private_account" 409 private_account_exists
call POST /auth/invites/accept "$tmp/inv.leave"
expect_status "step 5: the acceptance with leave_private_account true" 204 ""
call POST /auth/refresh "" "$tmp/inv.cookie"
expect_error "step 5: INV's cookie of its sign-up" 401 invalid_grant
json_body "$tmp/inv.ignored" "{'email': os.environ['E2E_INV_EMAIL'], 'password': os.environ['E2E_IGNORED_PASSWORD']}"
call POST /auth/login "$tmp/inv.ignored"
expect_error "step 5: the password sent with the acceptance was not used" 401 invalid_credentials
login inv E2E_INV_EMAIL E2E_INV_PASSWORD
BODY="$(jwt_payload "$tmp/inv.auth")"
expect_eq "step 5: INV is the same account" "$(val 'd["sub"]')" "$INV_SUB"
expect_eq "step 5: INV is in the development company" "$(val 'd["org_id"]')" "$DEV_ORG"
expect_eq "step 5: org_kind" "$(val 'd["org_kind"]')" "organization"
expect_eq "step 5: roles" "$(val 'd["roles"]')" "['user']"
expect_eq "step 5: the personal space is gone" "$(psql_value "SELECT count(*) FROM \"Companies\" WHERE \"Id\" = '$INV_SPACE'")" "0"
expect_eq "step 5: invite.accepted says the private account was left, and which space" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'invite.accepted' AND subject_user_id = '$INV_SUB' AND details->>'left_private_account' = 'true' AND details->>'personal_org_id' = '$INV_SPACE'")" "1"
pass "step 5: the invitation of a private person: the mail's paragraph, has_private_account, 409 private_account_exists, the move (204), the old session ended, the old password into the company"

# --- Step 6: the self-deletion (criteria 21, 22) ------------------------------------------------------------------------
json_body "$tmp/delete-wrong.json" "{'password': os.environ['E2E_WRONG_PASSWORD']}"
call DELETE /auth/account "$tmp/delete-wrong.json" "$tmp/priv.auth"
expect_error "step 6: a wrong password" 403 wrong_password
expect_eq "step 6: account.delete_refused with the reason wrong_password" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'account.delete_refused' AND subject_user_id = '$PRIV_SUB' AND details->>'reason' = 'wrong_password'")" "1"
json_body "$tmp/delete.json" "{'password': os.environ['E2E_PASSWORD']}"
call DELETE /auth/account "$tmp/delete.json" "$tmp/priv.auth"
expect_status "step 6: the deletion" 204 ""
expect_no_store "step 6: the 204"
LINE="$(set_cookie_line)"
[[ -n "$LINE" ]] || fail "step 6: the deletion did not clear the auth_rt cookie"
has_attr "$LINE" "Max-Age=0" || fail "step 6: the cleared cookie has no Max-Age=0"
[[ -z "$(cookie_value)" ]] || fail "step 6: the cleared cookie is not empty"
call POST /auth/refresh "" "$tmp/priv.cookie"
expect_error "step 6: PRIV's refresh cookie" 401 invalid_grant
json_body "$tmp/priv.login" "{'email': os.environ['E2E_PRIV_EMAIL'], 'password': os.environ['E2E_PASSWORD']}"
call POST /auth/login "$tmp/priv.login"
expect_error "step 6: PRIV's login is that of an unknown address" 401 invalid_credentials
expect_eq "step 6: account.deleted with the reason self" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'account.deleted' AND subject_user_id = '$PRIV_SUB' AND actor_user_id = '$PRIV_SUB' AND org_id = '$PRIV_SPACE' AND details->>'reason' = 'self'")" "1"
expect_eq "step 6: no account, no space" \
  "$(psql_value "SELECT (SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = upper('$PRIV_EMAIL')) + (SELECT count(*) FROM \"Companies\" WHERE \"Id\" = '$PRIV_SPACE')")" "0"
private_signup E2E_PRIV_EMAIL E2E_AGAIN_PASSWORD again
BODY="$(jwt_payload "$tmp/again.auth")"
[[ "$(val 'd["sub"]')" != "$PRIV_SUB" ]] || fail "step 6: the new sign-up has the deleted account's id"
pass "step 6: a wrong password -> 403 wrong_password (recorded); the right one -> 204 with the cookie cleared; the old session and the login are refused; the address signs up again"

# --- Step 7: a company member cannot delete their account (criterion 22) ------------------------------------------------
json_body "$tmp/seed-delete.json" "{'password': os.environ['E2E_SEED_PASSWORD']}"
call DELETE /auth/account "$tmp/seed-delete.json" "$tmp/seed.auth"
expect_error "step 7: SEED's DELETE /auth/account" 403 forbidden
pass "step 7: a company member's DELETE /auth/account -> 403 forbidden"

# --- Step 8: a removal deletes the account (criterion 23) ---------------------------------------------------------------
call GET /auth/org/members "" "$tmp/seed.auth"
expect_status "step 8: GET /auth/org/members" 200
INV_ID="$(val "[m['user_id'] for m in d['members'] if m['email'].lower() == '$INV_EMAIL'][0]")"
call DELETE "/auth/org/members/$INV_ID" "" "$tmp/seed.auth"
expect_status "step 8: SEED removes INV" 204 ""
json_body "$tmp/inv.login2" "{'email': os.environ['E2E_INV_EMAIL'], 'password': os.environ['E2E_INV_PASSWORD']}"
call POST /auth/login "$tmp/inv.login2"
expect_error "step 8: INV's login is that of an unknown address" 401 invalid_credentials
expect_eq "step 8: account.deleted with the reason member_removed" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'account.deleted' AND subject_user_id = '$INV_SUB' AND org_id = '$DEV_ORG' AND details->>'reason' = 'member_removed'")" "1"
pass "step 8: a removal through the company API -> 204, and the account is deleted with its row"

# --- Step 9: no secret in the audit log (criterion 29) ------------------------------------------------------------------
for secret in "$E2E_PASSWORD" "$E2E_INV_PASSWORD" "$E2E_AGAIN_PASSWORD" "$E2E_IGNORED_PASSWORD" "$E2E_WRONG_PASSWORD" "$PRIV_LINK" "$INV_LINK" "$PRIV_ACCESS" "$PRIV_COOKIE"; do
  [[ -n "$secret" ]] || fail "step 9: a secret to look for is empty"
  expect_eq "step 9: rows of the audit log that hold a password, a link or a session token of this run" "$(secret_in_audit "$secret")" "0"
done
pass "step 9: no audit row holds a password, a link or a session token of this run"

echo "ALL PASS"
