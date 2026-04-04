#!/usr/bin/env bash
# Real-network end-to-end check of spec 0009 (self-service sign-up) against the compose stack, with the mail catcher (Mailpit) in it. It
# drives the sequence of the spec's Goal - a visitor asks for a sign-up, opens the link of the mail, chooses a password and is signed in as
# the first admin of a company of their own - and the cases around it.
#
# What it checks, in order (NEW = the address that signs up, SEED = the development seed user, PENDING = an address that asks and never
# finishes, INVITED = an address that is invited and also signs up, EXPIRED = an address whose link is made to have expired). Each step
# names the criteria it touches; the ones it touches only in part are marked "part of", and the guarding tests are in the acceptance map.
#   1. health answers 200 "Healthy" (the development settings name the completion address and deploy/auth.yaml says self-service); the
#      mail catcher answers; GET /auth/options -> 200 {"signup":"self-service"}, never stored; the OpenAPI description names the four
#      paths - part of criteria 1 (the other answers of options) and 20 (the answers and bodies of the operations).
#   2. POST /auth/signup for NEW -> 202 with an empty body, never stored, no cookie; the same request at once -> 429 too_many_attempts
#      with Retry-After - part of criteria 3 (the mail is step 4) and 6 (the hourly limit and the independence of the kinds).
#   3. a malformed address, a look-alike letter (built with chr()), a domain that is an address or has no dot, a bad company name and a
#      body that is not an object -> 400 invalid_request; what was refused made nothing (no pending sign-up, no mail: checked after
#      step 4) - criterion 5.
#   4. the mail arrives: the subject names the application and does not hold the company; the text and the HTML part hold the same
#      link <complete_signup url>?token=<43 characters> and the company name; exactly one signup.requested row for NEW has been written
#      since the run began - criteria 3, 21 (part of: Polish).
#   5. preview with the token -> 200 with exactly the typed address, the company name and has_account false, twice (it changes nothing); an unknown token
#      -> 400 invalid_token - part of criterion 8 (an expired token is step 11).
#   6. complete with a weak password -> 400 weak_password naming the rules, no cookie, and the token still works; then with a good password
#      and another company name -> 200 {"status":"authenticated","access_token"} and the auth_rt cookie (HttpOnly, Secure, SameSite=Strict,
#      Path=/auth), as a login answers (spec 0009, Amendment 2): the token has roles ["admin"] and the new company, GET /auth/me with it names
#      the company as given at the completion, the cookie refreshes (and rotates), and the audit log holds one login.succeeded row for NEW;
#      again -> 400 invalid_token and no cookie - criteria 11, 12 (part of: parallel completions), 9.
#   7. NEW logs in again (the email is confirmed): the token has the org_id of the completion's company, which is not the development
#      company's; GET /auth/org/roles lists the company's own copy of the two default roles (admin "*", user) with NEW the only member of
#      admin; the audit log holds signup.requested, signup.completed (this account by id, this company, this address) and org.created with
#      via signup, none of its rows holds the password, the token of the link, or the access token or the refresh cookie of the completion,
#      and the pending sign-up is gone - criteria 9, 10 (part of: the other manifests), 19 (part of: atomicity).
#   8. a minute later the same request for NEW -> the same 202 and the same header names as in step 2, and a mail that says the account
#      exists, with no link and no token; the pending sign-up is gone; NEW's password still works; list-orgs shows the company with
#      exactly one member and no second company - part of criterion 4 (that the second request runs the same statements as the first is
#      guarded by the tests, not here).
#   9. PENDING asks and never finishes: its login is the 401 of an unknown address, recorded as a failed login of an unknown address, and no
#      account exists; forgot password sends it nothing, and the limit of forgot is not the limit of sign-up - criterion 15, part of 6.
#  10. INVITED is invited through the operator CLI and asks for a sign-up; the invitation is accepted first; the completion is then
#      409 already_registered, makes no company, and its pending sign-up is gone; INVITED is a member of the development company -
#      criterion 14 (part of: the parallel case).
#  11. EXPIRED asks; once its mail has arrived the end of its link is moved into the past (one UPDATE of "PendingSignups"."TokenExpiresAt",
#      as scripts/e2e-web.sh does for the browser test); preview and complete answer 400 invalid_token and no account is made - part of
#      criteria 8 and 13 (the 24 hours themselves, and the hourly clean-up, are guarded by the tests).
# Criteria 2, 7 (the replacement of a link), 16, 17, 18, 22 and 23's other halves are guarded by the integration tests and the Playwright
# tests: see docs/superpowers/plans/0009-acceptance-map.md.
#
# Full sequence, from the repo root (the same stack and .env as the other e2e scripts, with the per-IP limiter OFF as for them; run this
# one after e2e-tenancy.sh and before e2e-hardening.sh, which recreates the stack with the limits on):
#   cp .env.example .env                  # then set real local values (git-ignored)
#   scripts/dev-keys.sh                   # dev signing/encryption keys into .secrets/ (git-ignored); on Windows: MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh
#   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, never "auth-core" (the development stack); the scripts default to this name and refuse "auth-core"
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build
#   scripts/e2e-login.sh; scripts/e2e-refresh.sh; scripts/e2e-lockout.sh; scripts/e2e-email.sh; scripts/e2e-tenancy.sh
#   scripts/e2e-signup.sh                 # this script (does NOT bring the stack up or down)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# Reads AUTH_DEV_SEED_EMAIL and AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced). The operator commands run as
# `docker compose run --rm -T --no-deps auth admin ...`, and the database is read and, in step 11, changed with `docker compose exec postgres
# psql`. Needs: curl, python3 (standard library only; E2E_PYTHON=<interpreter> names another, for example E2E_PYTHON=C:/p6v/Scripts/python.exe on
# the owner's machine), docker compose. Env: BASE_URL (default http://localhost:8080),
# MAILPIT_URL (default http://localhost:8025). The links in the mails start with http://localhost:4200/complete-signup and
# http://localhost:4200/invite (appsettings.Development.json). Run it with the per-IP limits OFF, as the stack of the header is started: steps 2 and 3 alone make all ten mail-policy
# requests a minute that the default limit allows. Takes about five minutes: one wait of 62 seconds for the mail limit, and up to a minute for the server to pick
# up the invitation the CLI queued. Re-runnable on the same stack: every address and company is made for the run.
# Exits non-zero on the first failure; prints "PASS <step>" per step; never prints a password, a token, a cookie or a mail body. Request
# bodies are built into files in a mktemp -d directory (removed on exit) and curl reads them with --data-binary @file; tokens and cookies
# reach curl through header files.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
PY="${E2E_PYTHON:-python3}"
# The stack of these scripts is its own compose project, never "auth-core" (the development stack): the one-off containers below meet the same stack as the down -v of the header.
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
NEW_EMAIL="anna-$run@firma.test"
INVITED_EMAIL="ola-$run@firma.test"
PENDING_EMAIL="pending-$run@firma.test"
UNKNOWN_EMAIL="nobody-$run@example.invalid"
COMPANY="Firma $run"
RENAMED="Firma Renamed $run"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
export E2E_NEW_EMAIL="$NEW_EMAIL" E2E_INVITED_EMAIL="$INVITED_EMAIL" E2E_PENDING_EMAIL="$PENDING_EMAIL" E2E_UNKNOWN_EMAIL="$UNKNOWN_EMAIL"
export E2E_COMPANY="$COMPANY" E2E_RENAMED="$RENAMED"
export E2E_PASSWORD="E2e-Passw0rd-$run" E2E_INVITED_PASSWORD="E2e-Invited-Passw0rd-$run" E2E_WEAK_PASSWORD="abc"

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

# The python3 of some machines is a Windows interpreter, which ends its lines with CR LF: the helpers below that print a value strip
# the CR, so that the value compares equal in bash.

# val <python-expression-of-d>: evaluates it against the JSON of $BODY and prints the result
val() { "$PY" -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1" <<< "$BODY" | tr -d '\r'; }

# jwt_payload <auth-header-file>: the payload of the bearer token in the file, as JSON (the token is never printed)
jwt_payload() {
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

expect_no_cookie() { [[ -z "$(header set-cookie)" ]] || fail "$1: the response set a cookie"; }

expect_eq() { [[ "$2" == "$3" ]] || fail "$1: got '$2', expected '$3'"; }

# login <name> <email-env> <password-env>: logs in, keeps the bearer header in $tmp/<name>.auth
login() {
  json_body "$tmp/$1.login" "{'email': os.environ['$2'], 'password': os.environ['$3']}"
  call POST /auth/login "$tmp/$1.login"
  expect_status "login of $1" 200
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$1.auth"
}

# --- the mail catcher ---------------------------------------------------------------------------------------------

mail_count() {
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" -o "$tmp/search.json"
  "$PY" -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages_count"])' < "$tmp/search.json" | tr -d '\r'
}

# wait_mail <address> <subject-start> <seconds>: waits until a mail to exactly this address has a subject that starts so, then saves the
# newest of them to $tmp/mail.json
wait_mail() {
  local i id
  for i in $(seq 1 "$3"); do
    curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" -o "$tmp/search.json"
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
    sleep 1
  done
  return 1
}

# token_body <link-prefix> <file> [password-env] [company-env]: takes the token from the text part of $tmp/mail.json, checks that the HTML
# part holds the same link, and writes the request body ({"token"}, plus "password" and "company_name" when named). Never printed.
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
if sys.argv[3]:
    body["company_name"] = os.environ[sys.argv[3]]
print(json.dumps(body))
' "$1" "${3:-}" "${4:-}" < "$tmp/mail.json" > "$2"
}

mail_field() { "$PY" -c 'import json,sys; print(json.load(sys.stdin.buffer)[sys.argv[1]])' "$1" < "$tmp/mail.json" | tr -d '\r'; }

# mail_holds <env-name>: whether the text part and the HTML part of $tmp/mail.json both hold the value of that variable
mail_holds() {
  "$PY" -c '
import json, os, sys
mail = json.load(sys.stdin.buffer)
value = os.environ[sys.argv[1]]
sys.exit(0 if value in mail["Text"] and value in mail["HTML"] else 1)
' "$1" < "$tmp/mail.json"
}

# mail_has_no_link: neither part of $tmp/mail.json holds a token parameter or a URL
mail_has_no_link() {
  "$PY" -c '
import json, sys
mail = json.load(sys.stdin.buffer)
sys.exit(0 if all("token=" not in part and "://" not in part for part in (mail["Text"], mail["HTML"])) else 1)
' < "$tmp/mail.json"
}

# --- the operator CLI, in the service's own image ----------------------------------------------------------------

# cli <args...>: runs `auth-server admin <args>` in a one-off container; sets CLI_OUT (stdout), CLI_EXIT, and keeps stderr in $tmp/cli.err
cli() {
  CLI_EXIT=0
  CLI_OUT="$("${compose[@]}" run --rm -T --no-deps auth admin "$@" 2> "$tmp/cli.err")" || CLI_EXIT=$?
  CLI_OUT="${CLI_OUT//$'\r'/}"
}

# psql_value <sql>: the value the query returns. The SQL is this script's own text: a value that came from the service under test is never put into it.
psql_value() {
  printf '%s\n' "$1" | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'
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

# wait_psql_value <sql> <value> <seconds>: polls the query once a second until it returns the value; false if it never does
wait_psql_value() {
  local i
  for i in $(seq 1 "$3"); do
    [[ "$(psql_value "$1")" == "$2" ]] && return 0
    sleep 1
  done
  return 1
}

# secret_in_audit <secret>: how many rows of the audit log hold the text in any column; the secret goes on stdin only
secret_in_audit() {
  [[ "$1" != *"'"* ]] || fail "a secret with a quote cannot be looked for"
  printf "SELECT count(*) FROM audit_events WHERE position('%s' in (kind || ' ' || coalesce(subject_email, '') || ' ' || coalesce(org_name, '') || ' ' || coalesce(client_ip, '') || ' ' || coalesce(details::text, ''))) > 0;\n" "$1" \
    | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'
}

# --- Step 1: health, the mail catcher, the options, the description (criteria 1, 20) ----------------------------------
wait_healthy || fail "step 1: $BASE_URL/auth/health did not return 200 within 90s"
wait_mailpit || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s (is the mailpit service up?)"
call GET /auth/health
expect_status "step 1: health" 200 "Healthy"
call GET /auth/options
expect_status "step 1: the options" 200 '{"signup":"self-service"}'
expect_no_store "step 1: the options"
expect_no_cookie "step 1: the options"
call GET /auth/openapi/v1.json
expect_status "step 1: the OpenAPI description" 200
for path in /auth/options /auth/signup /auth/signup/preview /auth/signup/complete; do
  [[ "$(val "'$path' in d['paths']")" == "True" ]] || fail "step 1: $path is not in the OpenAPI description"
done
pass "step 1: health is Healthy; the mail catcher answers; GET /auth/options says self-service (never stored); the OpenAPI description names the four paths"

# --- Step 2: the request, and its limit (criteria 3, 6) ---------------------------------------------------------------
START_TS="$(psql_value "SELECT now()")"   # `occurred_at` is a timestamp with a time zone, as is this
json_body "$tmp/signup.json" "{'email': os.environ['E2E_NEW_EMAIL'], 'company_name': os.environ['E2E_COMPANY']}"
call POST /auth/signup "$tmp/signup.json"
expect_status "step 2: the sign-up request" 202 ""
expect_no_store "step 2: the 202"
expect_no_cookie "step 2: the 202"
NEW_HEADERS="$(header_names)"
call POST /auth/signup "$tmp/signup.json"
expect_status "step 2: the same request at once" 429
[[ "$BODY" =~ ^\{\"error\":\"too_many_attempts\",\"retry_after_seconds\":([0-9]+)\}$ ]] || fail "step 2: unexpected 429 body"
RETRY="${BASH_REMATCH[1]}"
[[ "$(header retry-after)" == "$RETRY" ]] || fail "step 2: Retry-After differs from retry_after_seconds"
# Up to 62, not 60: on Docker Desktop the container clock can step slightly, and a 60 s wait then reads 61 or 62.
(( RETRY >= 1 && RETRY <= 62 )) || fail "step 2: retry_after_seconds $RETRY is not between 1 and 62"
expect_no_store "step 2: the 429"
pass "step 2: the request -> 202 without a body (never stored, no cookie); the same request at once -> 429 too_many_attempts with Retry-After between 1 and 62"

# --- Step 3: what is refused (criterion 5) ---------------------------------------------------------------------------
JOE_EMAIL="joe-$run@firma.test"
for refused in "bob.firma.test|Firma" "joe@localhost|Firma" "joe@10.0.0.5|Firma" "joe@[10.0.0.5]|Firma" "$JOE_EMAIL| Leading space" "$JOE_EMAIL|Trailing space "; do
  json_body "$tmp/bad.json" "{'email': '${refused%%|*}', 'company_name': '${refused#*|}'}"
  call POST /auth/signup "$tmp/bad.json"
  expect_error "step 3: '${refused%%|*}' with the name '${refused#*|}'" 400 invalid_request
done
# The letter after the first one is the long s (U+017F), which stands for an ASCII "s" in some software: built here with chr().
json_body "$tmp/bad.json" "{'email': chr(0x17f) + 'nna-$run@firma.test', 'company_name': 'Firma'}"
call POST /auth/signup "$tmp/bad.json"
expect_error "step 3: a look-alike of an ASCII letter in the address" 400 invalid_request
printf '[]' > "$tmp/array.json"
call POST /auth/signup "$tmp/array.json"
expect_error "step 3: a body that is not an object" 400 invalid_request
pass "step 3: a malformed address, a look-alike letter, a domain that is an address or has no dot, a name with white space at an end and a body that is not an object -> 400 invalid_request (that nothing was made is checked after step 4)"

# --- Step 4: the mail (criteria 3, 21) --------------------------------------------------------------------------------
wait_mail "$NEW_EMAIL" "Complete your sign-up" 90 || fail "step 4: no sign-up mail for NEW within 90s"
[[ "$(mail_field Subject)" == *auth-core-dev* ]] || fail "step 4: the subject does not carry the application name"
[[ "$(mail_field Subject)" != *"$COMPANY"* ]] || fail "step 4: the subject holds the company name, which a visitor typed"
mail_holds E2E_COMPANY || fail "step 4: the text and the HTML part do not both hold the company name"
token_body http://localhost:4200/complete-signup "$tmp/preview.json" || fail "step 4: no usable sign-up link in the mail"
token_body http://localhost:4200/complete-signup "$tmp/complete-weak.json" E2E_WEAK_PASSWORD E2E_RENAMED || fail "step 4: the weak completion body could not be made"
token_body http://localhost:4200/complete-signup "$tmp/complete-good.json" E2E_PASSWORD E2E_RENAMED || fail "step 4: the good completion body could not be made"
# The requests of step 3 that were refused made nothing: the dispatcher has had its time (the mail of NEW went out) and JOE has no mail and no pending sign-up.
expect_eq "step 4: no mail for the address of the refused names" "$(mail_count "$JOE_EMAIL")" "0"
expect_eq "step 4: no pending sign-up for the address of the refused names" "$(psql_value "SELECT count(*) FROM \"PendingSignups\" WHERE \"NormalizedEmail\" = upper('$JOE_EMAIL')")" "0"
expect_eq "step 4: one signup.requested row for NEW, written since this run began" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.requested' AND subject_email = '$NEW_EMAIL' AND occurred_at >= '$START_TS'")" "1"
pass "step 4: the mail arrived (the subject names the application and not the company; the text and the HTML part hold the same link and the company name); the refused requests of step 3 made no mail and no pending sign-up; one signup.requested row since the run began"

# --- Step 5: the preview (criterion 8) --------------------------------------------------------------------------------
call POST /auth/signup/preview "$tmp/preview.json"
expect_status "step 5: preview" 200 "{\"email\":\"$NEW_EMAIL\",\"company_name\":\"$COMPANY\",\"has_account\":false}"
expect_no_store "step 5: preview"
call POST /auth/signup/preview "$tmp/preview.json"
expect_status "step 5: preview again" 200 "{\"email\":\"$NEW_EMAIL\",\"company_name\":\"$COMPANY\",\"has_account\":false}"
"$PY" -c 'import json; print(json.dumps({"token": "x" * 43}))' > "$tmp/unknown-token.json"
call POST /auth/signup/preview "$tmp/unknown-token.json"
expect_error "step 5: preview of an unknown token" 400 invalid_token
pass "step 5: preview -> 200 with exactly the typed address, the company name and has_account false, twice; an unknown token -> 400 invalid_token"

# --- Step 6: the completion, which signs in (criteria 9, 11, 12) ------------------------------------------------------
call POST /auth/signup/complete "$tmp/complete-weak.json"
expect_status "step 6: complete with a weak password" 400 '{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}'
expect_no_cookie "step 6: the weak password"
call POST /auth/signup/complete "$tmp/complete-good.json"
expect_status "step 6: complete with a good password and another company name" 200
expect_no_store "step 6: the 200"
expect_eq "step 6: the body is a login's" "$(val 'sorted(d.keys())')" "['access_token', 'status']"
expect_eq "step 6: the status" "$(val 'd["status"]')" "authenticated"
LINE="$(set_cookie_line)"
[[ -n "$LINE" ]] || fail "step 6: the completion set no auth_rt cookie"
for attr in HttpOnly Secure SameSite=Strict Path=/auth; do
  has_attr "$LINE" "$attr" || fail "step 6: the auth_rt cookie lacks the $attr attribute"
done
RT0="$(cookie_value)"
[[ -n "$RT0" ]] || fail "step 6: the auth_rt cookie is empty"
COMPLETION_ACCESS="$(val 'd["access_token"]')"
printf 'Authorization: Bearer %s\n' "$COMPLETION_ACCESS" > "$tmp/completion.auth"
printf 'Cookie: auth_rt=%s\n' "$RT0" > "$tmp/completion.cookie"
BODY="$(jwt_payload "$tmp/completion.auth")"
expect_eq "step 6: roles of the completion's token" "$(val 'd["roles"]')" "['admin']"
COMPLETION_ORG="$(val 'd["org_id"]')"
call GET /auth/me "" "$tmp/completion.auth"
expect_status "step 6: GET /auth/me with the token of the completion" 200
expect_eq "step 6: the company is the one named at the completion" "$(val 'd["org_name"]')" "$RENAMED"
expect_eq "step 6: the address" "$(val 'd["email"]')" "$NEW_EMAIL"
expect_eq "step 6: org_id of /auth/me and of the token" "$(val 'd["org_id"]')" "$COMPLETION_ORG"
call POST /auth/refresh "" "$tmp/completion.cookie"
expect_status "step 6: refresh with the cookie of the completion" 200
[[ -n "$(cookie_value)" && "$(cookie_value)" != "$RT0" ]] || fail "step 6: the refresh did not rotate the cookie"
printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/refreshed.auth"
BODY="$(jwt_payload "$tmp/refreshed.auth")"
expect_eq "step 6: the refreshed token names the same company" "$(val 'd["org_id"]')" "$COMPLETION_ORG"
expect_eq "step 6: one login.succeeded row, written by the completion, for NEW and its company" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.succeeded' AND subject_email = '$NEW_EMAIL' AND org_name = '$RENAMED'")" "1"
call POST /auth/signup/complete "$tmp/complete-good.json"
expect_error "step 6: the same completion again" 400 invalid_token
expect_no_cookie "step 6: the same completion again"
call POST /auth/signup/preview "$tmp/preview.json"
expect_error "step 6: preview of the used token" 400 invalid_token
pass "step 6: a weak password -> 400 weak_password naming the rules (no cookie; the token still works); a good one -> 200 with a login's body and the auth_rt cookie (the attributes of ADR 0004), a token with roles [admin] and the new company, /auth/me names it, the cookie refreshes and rotates, one login.succeeded row; again -> 400 invalid_token, no cookie"

# --- Step 7: the new admin signs in again (criteria 9, 10) ------------------------------------------------------------
login new E2E_NEW_EMAIL E2E_PASSWORD
BODY="$(jwt_payload "$tmp/new.auth")"
expect_eq "step 7: roles" "$(val 'd["roles"]')" "['admin']"
expect_eq "step 7: permissions" "$(val 'd["permissions"]')" "['documents:approve', 'documents:read', 'documents:write', 'members:manage', 'org:delete', 'org:manage', 'roles:manage']"
NEW_ORG="$(val 'd["org_id"]')"
NEW_SUB="$(val 'd["sub"]')"
expect_eq "step 7: a login names the company of the completion's token" "$NEW_ORG" "$COMPLETION_ORG"
call GET /auth/me "" "$tmp/new.auth"
expect_status "step 7: GET /auth/me" 200
expect_eq "step 7: the company is the one named at the completion" "$(val 'd["org_name"]')" "$RENAMED"
expect_eq "step 7: the address" "$(val 'd["email"]')" "$NEW_EMAIL"
expect_eq "step 7: org_id of /auth/me and of the token" "$(val 'd["org_id"]')" "$NEW_ORG"
call GET /auth/org/roles "" "$tmp/new.auth"
expect_status "step 7: GET /auth/org/roles" 200
expect_eq "step 7: the company's own copy of the default roles, and NEW the only member of admin" \
  "$(val 'sorted((r["name"], sorted(r["permissions"]), r["members"]) for r in d["roles"])')" \
  "[('admin', ['*'], 1), ('user', ['documents:read', 'documents:write'], 0)]"
login seed E2E_SEED_EMAIL E2E_SEED_PASSWORD
call GET /auth/me "" "$tmp/seed.auth"
expect_status "step 7: GET /auth/me of SEED" 200
DEV_ORG="$(val 'd["org_id"]')"
[[ "$NEW_ORG" != "$DEV_ORG" ]] || fail "step 7: the new admin belongs to the development company"
expect_eq "step 7: the pending sign-up of NEW is gone" "$(psql_value "SELECT count(*) FROM \"PendingSignups\" WHERE \"NormalizedEmail\" = upper('$NEW_EMAIL')")" "0"
expect_eq "step 7: one signup.requested row for NEW" "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.requested' AND subject_email = '$NEW_EMAIL'")" "1"
expect_eq "step 7: one signup.completed row, for this account, this company and this address" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.completed' AND org_id = '$NEW_ORG' AND org_name = '$RENAMED' AND subject_email = '$NEW_EMAIL' AND subject_user_id = '$NEW_SUB'")" "1"
expect_eq "step 7: org.created with via signup, in the same company" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'org.created' AND org_id = '$NEW_ORG' AND details->>'via' = 'signup'")" "1"
SIGNUP_TOKEN="$("$PY" -c 'import json,sys; print(json.load(sys.stdin)["token"])' < "$tmp/preview.json" | tr -d '\r')"   # never printed
expect_eq "step 7: no audit row holds the password" "$(secret_in_audit "$E2E_PASSWORD")" "0"
expect_eq "step 7: no audit row holds the token of the link" "$(secret_in_audit "$SIGNUP_TOKEN")" "0"
expect_eq "step 7: no audit row holds the access token of the completion" "$(secret_in_audit "$COMPLETION_ACCESS")" "0"
expect_eq "step 7: no audit row holds the refresh cookie of the completion" "$(secret_in_audit "$RT0")" "0"
pass "step 7: NEW logs in again (its email is confirmed): roles [admin] and the permissions of the role, a company of its own named as given at the completion, not the development company, with its own copy of the default roles and NEW the only admin; the audit log holds the three rows of the contract and no password and no token, and the pending sign-up is gone"

# --- Step 8: the same address again (criterion 4) ---------------------------------------------------------------------
echo "waiting 62 s for the mail limit of NEW..."
sleep 62
call POST /auth/signup "$tmp/signup.json"
expect_status "step 8: the request for an address with an account" 202 ""
[[ "$(header_names)" == "$NEW_HEADERS" ]] || fail "step 8: the response headers differ from those of a new address (step 2)"
wait_mail "$NEW_EMAIL" "You already have an account" 90 || fail "step 8: no 'you already have an account' mail for NEW within 90s"
mail_has_no_link || fail "step 8: the mail holds a link or a token"
login new E2E_NEW_EMAIL E2E_PASSWORD
# The pending sign-up goes in the transaction of the send, which commits just after the relay has the mail: wait for it, do not guess.
wait_psql_value "SELECT count(*) FROM \"PendingSignups\" WHERE \"NormalizedEmail\" = upper('$NEW_EMAIL')" 0 20 || fail "step 8: the pending sign-up of NEW is still there after 20s"
expect_eq "step 8: the pending sign-up is gone" "$(psql_value "SELECT count(*) FROM \"PendingSignups\" WHERE \"NormalizedEmail\" = upper('$NEW_EMAIL')")" "0"
cli list-orgs
[[ "$CLI_EXIT" == "0" ]] || fail "step 8: the operator CLI failed: admin list-orgs"
grep -qxF "$NEW_ORG"$'\t'"$RENAMED"$'\t'"1" <<< "$CLI_OUT" || fail "step 8: list-orgs does not show the company of the completion with exactly one member"
expect_eq "step 8: one company named as at the completion" "$(grep -cF $'\t'"$RENAMED"$'\t' <<< "$CLI_OUT" || true)" "1"
expect_eq "step 8: no company was made for the second request" "$(grep -cF $'\t'"$COMPANY"$'\t' <<< "$CLI_OUT" || true)" "0"
pass "step 8: the same request for NEW -> the same 202 and header names; the mail says the account exists and holds no link; the pending sign-up is gone; its password works; the company has exactly one member, one company is named as at the completion, and no second company exists"

# --- Step 9: a pending sign-up is not an account (criteria 6, 15) -----------------------------------------------------
json_body "$tmp/pending.json" "{'email': os.environ['E2E_PENDING_EMAIL'], 'company_name': os.environ['E2E_COMPANY']}"
call POST /auth/signup "$tmp/pending.json"
expect_status "step 9: the request of PENDING" 202 ""
wait_mail "$PENDING_EMAIL" "Complete your sign-up" 90 || fail "step 9: no sign-up mail for PENDING within 90s"
json_body "$tmp/pending.login" "{'email': os.environ['E2E_PENDING_EMAIL'], 'password': os.environ['E2E_PASSWORD']}"
call POST /auth/login "$tmp/pending.login"
expect_error "step 9: login of an address with only a pending sign-up" 401 invalid_credentials
expect_eq "step 9: that login is recorded as a failed login of an unknown address" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.failed' AND subject_email = '$PENDING_EMAIL' AND details->>'reason' = 'unknown_address'")" "1"
expect_eq "step 9: PENDING has no account" "$(psql_value "SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = upper('$PENDING_EMAIL')")" "0"
json_body "$tmp/unknown.login" "{'email': os.environ['E2E_UNKNOWN_EMAIL'], 'password': os.environ['E2E_PASSWORD']}"
call POST /auth/login "$tmp/unknown.login"
expect_error "step 9: login of an unknown address" 401 invalid_credentials
json_body "$tmp/pending-email.json" "{'email': os.environ['E2E_PENDING_EMAIL']}"
call POST /auth/password/forgot "$tmp/pending-email.json"
expect_status "step 9: forgot password for PENDING (the sign-up limit is not the limit of forgot)" 202 ""
# The dispatcher removes the request of an address without an account within seconds: wait until none of PENDING's is left, or the count below would only mean that nothing was sent yet.
wait_psql_value "SELECT count(*) FROM \"MailRequests\" WHERE \"NormalizedEmail\" = upper('$PENDING_EMAIL')" 0 30 || fail "step 9: the mail requests of PENDING were not handled within 30s"
expect_eq "step 9: PENDING got no mail but the sign-up mail" "$(mail_count "$PENDING_EMAIL")" "1"
pass "step 9: a pending sign-up is no account: login is the 401 of an unknown address, recorded as login.failed with reason unknown_address, and no account exists; forgot password sends it nothing"

# --- Step 10: an invitation and a sign-up for one address (criterion 14) ---------------------------------------------
cli invite --org "$DEV_ORG" --email "$INVITED_EMAIL" --role user
[[ "$CLI_EXIT" == "0" ]] || fail "step 10: the operator CLI failed: admin invite"
json_body "$tmp/invited.json" "{'email': os.environ['E2E_INVITED_EMAIL'], 'company_name': os.environ['E2E_COMPANY']}"
call POST /auth/signup "$tmp/invited.json"
expect_status "step 10: the sign-up request of INVITED" 202 ""
wait_mail "$INVITED_EMAIL" "Complete your sign-up" 90 || fail "step 10: no sign-up mail for INVITED within 90s"
token_body http://localhost:4200/complete-signup "$tmp/invited-complete.json" E2E_PASSWORD E2E_COMPANY || fail "step 10: no usable sign-up link in the mail"
wait_mail "$INVITED_EMAIL" "You are invited" 150 || fail "step 10: no invitation mail for INVITED within 150s"
token_body http://localhost:4200/invite "$tmp/invited-accept.json" E2E_INVITED_PASSWORD || fail "step 10: no usable invitation link in the mail"
call POST /auth/invites/accept "$tmp/invited-accept.json"
expect_status "step 10: the invitation is accepted" 204 ""
call POST /auth/signup/complete "$tmp/invited-complete.json"
expect_error "step 10: the completion after the acceptance" 409 already_registered
call POST /auth/signup/complete "$tmp/invited-complete.json"
expect_error "step 10: the completion again (the pending sign-up is gone)" 400 invalid_token
expect_eq "step 10: the pending sign-up of INVITED is gone" "$(psql_value "SELECT count(*) FROM \"PendingSignups\" WHERE \"NormalizedEmail\" = upper('$INVITED_EMAIL')")" "0"
login invited E2E_INVITED_EMAIL E2E_INVITED_PASSWORD
BODY="$(jwt_payload "$tmp/invited.auth")"
expect_eq "step 10: INVITED is a member of the development company" "$(val 'd["org_id"]')" "$DEV_ORG"
expect_eq "step 10: INVITED has the role of the invitation" "$(val 'd["roles"]')" "['user']"
cli list-orgs
[[ "$CLI_EXIT" == "0" ]] || fail "step 10: the operator CLI failed: admin list-orgs"
expect_eq "step 10: no company was made for the completion" "$(grep -cF $'\t'"$COMPANY"$'\t' <<< "$CLI_OUT" || true)" "0"
pass "step 10: the invitation was accepted first -> the completion is 409 already_registered, makes no company and its pending sign-up is gone"

# --- Step 11: a link that has expired (criteria 8, 13) -----------------------------------------------------------------
EXPIRED_EMAIL="expired-$run@firma.test"
export E2E_EXPIRED_EMAIL="$EXPIRED_EMAIL"
json_body "$tmp/expired.json" "{'email': os.environ['E2E_EXPIRED_EMAIL'], 'company_name': os.environ['E2E_COMPANY']}"
call POST /auth/signup "$tmp/expired.json"
expect_status "step 11: the sign-up request of EXPIRED" 202 ""
wait_mail "$EXPIRED_EMAIL" "Complete your sign-up" 90 || fail "step 11: no sign-up mail for EXPIRED within 90s"
token_body http://localhost:4200/complete-signup "$tmp/expired-preview.json" || fail "step 11: no usable sign-up link in the mail"
token_body http://localhost:4200/complete-signup "$tmp/expired-complete.json" E2E_PASSWORD E2E_COMPANY || fail "step 11: no usable sign-up link in the mail"
call POST /auth/signup/preview "$tmp/expired-preview.json"
expect_status "step 11: preview of the live link" 200
# The 24 hours cannot be waited for: the end of the link is moved into the past, as scripts/e2e-web.sh does for the browser test.
expect_eq "step 11: the link was moved into the past" \
  "$(psql_value "UPDATE \"PendingSignups\" SET \"TokenExpiresAt\" = now() - interval '1 minute' WHERE \"NormalizedEmail\" = upper('$EXPIRED_EMAIL')")" "UPDATE 1"
call POST /auth/signup/preview "$tmp/expired-preview.json"
expect_error "step 11: preview of the expired link" 400 invalid_token
call POST /auth/signup/complete "$tmp/expired-complete.json"
expect_error "step 11: completion with the expired link" 400 invalid_token
expect_eq "step 11: no account was made" "$(psql_value "SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = upper('$EXPIRED_EMAIL')")" "0"
pass "step 11: a link whose end lies in the past -> preview and completion are 400 invalid_token, and no account is made"

echo "ALL PASS"
