#!/usr/bin/env bash
# Real-network end-to-end check of spec 0004 (email flows: password reset and email verification) against the
# compose stack, with the mail catcher (Mailpit) in it.
#
# What it checks, in order (the second seed user, whose email is not confirmed, is NEW; the first is SEED):
#   1. health of the auth service and of the mail catcher.
#   2. an unconfirmed login: a wrong password -> 401, the right one -> 403 email_not_verified (no-store, no cookie)
#      - criteria 13, 16; an email with the JSON escape of U+FFFE -> 400 (the image has no ICU).
#   3. a verification request -> 202 with no body; the same request at once -> 429 with Retry-After (criteria 9, 11).
#   4. the mail arrives (HTML and text parts carry the same token); the token verifies the email (204) once, the
#      second use -> 400 invalid_token (criteria 6, 12, 15).
#   5. NEW logs in (200 + auth_rt cookie).
#   6. forgot for an address without an account and for NEW: the same 202, the same header names, the same empty
#      body (criteria 1, 2).
#   7. the reset mail arrives, none for the unknown address; a weak password -> 400 weak_password naming every
#      rule and the token stays usable; the new password -> 204; the token again -> 400 (criteria 3, 6, 7).
#   8. after the reset: the old password -> 401, the refresh cookie of step 5 -> 401 invalid_grant, the new
#      password -> 200 (criteria 3, 4).
#   9. a mail outage: with the mail catcher stopped, forgot for SEED -> 202 within 2 s; the first attempt fails
#      (seen in the service log); after the catcher is back, a retry delivers the mail (criterion 14).
#
# Full sequence, from the repo root (same stack and .env as the other e2e scripts; the three existing ones first,
# this one last):
#   cp .env.example .env                  # then set real local values (git-ignored)
#   scripts/dev-keys.sh                   # dev signing/encryption keys into .secrets/ (git-ignored)
#   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, never "auth-core" (the development stack): a bare down -v would wipe it
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build    # start postgres, mailpit, auth
#                                         # the per-IP limiter is off for the older checks (spec 0008): the lockout script makes about 58 logins a minute and the mail script exactly 10 mail requests; scripts/e2e-hardening.sh runs last on the stack recreated with the defaults
#   scripts/e2e-login.sh                  # spec 0001 regression
#   scripts/e2e-refresh.sh                # spec 0002 regression
#   scripts/e2e-lockout.sh                # spec 0003 regression
#   scripts/e2e-email.sh                  # this script (does NOT bring the stack up or down; it stops and starts mailpit)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# Reads AUTH_DEV_SEED_EMAIL, AUTH_DEV_SEED_PASSWORD, AUTH_DEV_SEED_UNVERIFIED_EMAIL and
# AUTH_DEV_SEED_UNVERIFIED_PASSWORD from the repo-root .env (parsed, never sourced).
# Needs: curl, python3 (standard library only), docker compose. Env: BASE_URL (default http://localhost:8080),
# MAILPIT_URL (default http://localhost:8025). The links in the mails start with http://localhost:4200/verify and
# http://localhost:4200/reset (appsettings.Development.json).
# Takes about a minute. NOT re-runnable on the same stack: it confirms the second seed user and changes its
# password, so a second run needs `docker compose ... down -v` and a new `up` first. SEED keeps its password.
# Exits non-zero on the first failure; prints "PASS <step>" per step; never prints a password, a token, a cookie or a
# mail body. Request bodies are built into files in a mktemp -d directory (removed on exit) and curl reads them with
# --data-binary @file; a token goes from the mail straight into such a file.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
compose=(docker compose -f "$root/deploy/docker-compose.yml" --env-file "$root/.env")

tmp="$(mktemp -d)"
MAILPIT_STOPPED=0
cleanup() {
  # a run that dies during the outage of step 9 must not leave the mail catcher stopped
  if [[ "$MAILPIT_STOPPED" == "1" ]]; then "${compose[@]}" start mailpit >/dev/null 2>&1 || true; fi
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
NEW_EMAIL="$(env_get AUTH_DEV_SEED_UNVERIFIED_EMAIL)"
NEW_OLD_PASSWORD="$(env_get AUTH_DEV_SEED_UNVERIFIED_PASSWORD)"
[[ -n "$SEED_EMAIL" && -n "$SEED_PASSWORD" ]] || fail "AUTH_DEV_SEED_EMAIL / AUTH_DEV_SEED_PASSWORD not set in .env"
[[ -n "$NEW_EMAIL" && -n "$NEW_OLD_PASSWORD" ]] \
  || fail "AUTH_DEV_SEED_UNVERIFIED_EMAIL / AUTH_DEV_SEED_UNVERIFIED_PASSWORD not set in .env (see .env.example)"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
export E2E_NEW_EMAIL="$NEW_EMAIL" E2E_NEW_OLD_PASSWORD="$NEW_OLD_PASSWORD"
export E2E_NEW_PASSWORD="E2e-New-Passw0rd-$RANDOM$RANDOM"
export E2E_WRONG_PASSWORD="wrong-password-$RANDOM$RANDOM"
export E2E_WEAK_PASSWORD="abc"

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

# make_body <email-env> <password-env> <file>: JSON login body from the given env var names, written to a file
# under $tmp, so that no secret is on a command line.
make_body() {
  python3 -c 'import json,os,sys; print(json.dumps({"email": os.environ[sys.argv[1]], "password": os.environ[sys.argv[2]]}))' "$1" "$2" > "$3"
}

# post <path> <body-file>  ->  sets HTTP_CODE, BODY, TIME_S (curl's time_total, seconds); response headers in $tmp/hdr
post() {
  local out
  out="$(curl -sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code} %{time_total}' \
    -X POST "$BASE_URL$1" -H 'Content-Type: application/json' --data-binary "@$2")"
  HTTP_CODE="${out%% *}"
  TIME_S="${out##* }"
  BODY="$(cat "$tmp/body")"
}

# refresh <cookie-header-file>  ->  sets HTTP_CODE, BODY; the cookie goes in by header file, never on the command line
refresh() {
  HTTP_CODE="$(curl -sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' -X POST \
    -H "@$1" "$BASE_URL/auth/refresh")"
  BODY="$(cat "$tmp/body")"
}

header() { # value of a response header of the last response, CR stripped (empty when absent)
  { grep -i "^$1:" "$tmp/hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//'
}

header_names() { # sorted header names of the last response, Date excluded
  tail -n +2 "$tmp/hdr" | tr -d '\r' | grep ':' | cut -d: -f1 | tr '[:upper:]' '[:lower:]' | { grep -vx 'date' || true; } | sort | tr '\n' ' '
}

# email_body <email-env> <file>: {"email": ...} from the given env var name
email_body() {
  python3 -c 'import json,os,sys; print(json.dumps({"email": os.environ[sys.argv[1]]}))' "$1" > "$2"
}

# The python3 of some machines is a Windows interpreter, which ends its lines with CR LF: the helpers below that
# print a value strip the CR, so that the value compares equal in bash.

# mail_count <address>  ->  prints how many mails the catcher holds for the address
mail_count() {
  curl -sS --max-time 10 -G "$MAILPIT_URL/api/v1/search" --data-urlencode "query=to:$1" -o "$tmp/search.json"
  python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["messages_count"])' < "$tmp/search.json" | tr -d '\r'
}

# wait_mail <address> <count> <seconds>: waits until the catcher holds <count> mails for the address, then saves
# the newest one (the search lists newest first) to $tmp/mail.json
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

# token_body <link-prefix> <file> [new-password-env]: takes the token from the text part of $tmp/mail.json, checks
# that the HTML part holds the same token, and writes the request body. The token is never printed.
token_body() {
  python3 -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(re.escape(sys.argv[1]) + r"\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no link with a token")
if match.group(1) not in mail["HTML"]:
    sys.exit("the HTML part does not hold the link of the text part")
body = {"token": match.group(1)}
if len(sys.argv) > 2 and sys.argv[2]:
    body["new_password"] = os.environ[sys.argv[2]]
print(json.dumps(body))
' "$1" "${3:-}" < "$tmp/mail.json" > "$2"
}

mail_subject() { python3 -c 'import json,sys; print(json.load(sys.stdin.buffer)["Subject"])' < "$tmp/mail.json" | tr -d '\r'; }

expect_status() { # expect_status <what> <code> [exact-body]: the last response has that status (and body)
  [[ "$HTTP_CODE" == "$2" ]] || fail "$1: HTTP $HTTP_CODE, expected $2"
  if [[ $# -ge 3 ]]; then [[ "$BODY" == "$3" ]] || fail "$1: unexpected body"; fi
}

expect_no_store() { [[ "$(header cache-control)" == *no-store* ]] || fail "$1: no Cache-Control: no-store"; }

expect_no_cookie() { [[ -z "$(header set-cookie)" ]] || fail "$1: the response set a cookie"; }

# --- Step 1: health of the service and of the mail catcher ----------------------------------------------------
wait_healthy || fail "step 1: $BASE_URL/auth/health did not return 200 within 90s"
wait_mailpit || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s (is the mailpit service up?)"
pass "step 1: /auth/health and the mail catcher's /readyz returned 200"

make_body E2E_NEW_EMAIL E2E_NEW_OLD_PASSWORD "$tmp/new-old.json"
make_body E2E_NEW_EMAIL E2E_NEW_PASSWORD "$tmp/new-new.json"
make_body E2E_NEW_EMAIL E2E_WRONG_PASSWORD "$tmp/new-wrong.json"
email_body E2E_NEW_EMAIL "$tmp/new-email.json"
email_body E2E_SEED_EMAIL "$tmp/seed-email.json"

# --- Step 2: an unconfirmed login (criteria 13, 16) -------------------------------------------------------------
post /auth/login "$tmp/new-wrong.json"
expect_status "step 2: unconfirmed account, wrong password" 401 '{"error":"invalid_credentials"}'
post /auth/login "$tmp/new-old.json"
expect_status "step 2: unconfirmed account, right password" 403 '{"error":"email_not_verified"}'
expect_no_store "step 2: the 403"
expect_no_cookie "step 2: the 403"
# An email with the JSON escape of U+FFFE: the container image has no ICU, where the framework's normaliser would
# have let that address through. The six characters of the escape are built at run time (backslash + "ufffe").
printf '%s' '{"email":"a' > "$tmp/fffe.json"
printf '\\%s' 'ufffeb@example.com","password":"x"}' >> "$tmp/fffe.json"
post /auth/login "$tmp/fffe.json"
expect_status "step 2: an email with U+FFFE" 400
grep -Eq '"error"[[:space:]]*:[[:space:]]*"invalid_request"' <<< "$BODY" || fail "step 2: the 400 for U+FFFE is not an invalid_request"
pass "step 2: unconfirmed login -> 401 with a wrong password, 403 email_not_verified (no-store, no cookie) with the right one; U+FFFE email -> 400"

# --- Step 3: a verification request and its limit (criteria 9, 11) ----------------------------------------------
post /auth/email/verify/request "$tmp/new-email.json"
expect_status "step 3: verification request" 202 ""
expect_no_store "step 3: the 202"
post /auth/email/verify/request "$tmp/new-email.json"
expect_status "step 3: the same request again at once" 429
[[ "$BODY" =~ ^\{\"error\":\"too_many_attempts\",\"retry_after_seconds\":([0-9]+)\}$ ]] || fail "step 3: unexpected 429 body"
RETRY="${BASH_REMATCH[1]}"
[[ "$(header retry-after)" == "$RETRY" ]] || fail "step 3: Retry-After differs from retry_after_seconds"
# Up to 62, not 60: on Docker Desktop the container clock can step slightly, and a 60 s wait then reads 61 or 62.
(( RETRY >= 1 && RETRY <= 62 )) || fail "step 3: retry_after_seconds $RETRY is not between 1 and 62"
expect_no_store "step 3: the 429"
pass "step 3: verification request -> 202 without a body; the next one at once -> 429 too_many_attempts with Retry-After between 1 and 62"

# --- Step 4: the verification mail and the token (criteria 6, 12, 15) ------------------------------------------
wait_mail "$NEW_EMAIL" 1 30 || fail "step 4: no mail for the unconfirmed account within 30s"
[[ "$(mail_subject)" == *auth-core-dev* ]] || fail "step 4: the mail's subject does not carry the application name"
token_body http://localhost:4200/verify "$tmp/verify-token.json" || fail "step 4: no usable verification link in the mail"
post /auth/email/verify "$tmp/verify-token.json"
expect_status "step 4: verification with the mailed token" 204 ""
post /auth/email/verify "$tmp/verify-token.json"
expect_status "step 4: the same token again" 400 '{"error":"invalid_token"}'
pass "step 4: mail arrived (subject names the application; text and HTML parts hold the same token); token -> 204; again -> 400 invalid_token"

# --- Step 5: the confirmed account logs in -----------------------------------------------------------------------
post /auth/login "$tmp/new-old.json"
expect_status "step 5: login after the verification" 200
cookie_pair="$({ grep -i '^set-cookie:[[:space:]]*auth_rt=[^;]' "$tmp/hdr" || true; } | head -n1 | tr -d '\r' | cut -d: -f2- | sed 's/^ *//' | cut -d';' -f1)"
[[ -n "$cookie_pair" ]] || fail "step 5: the login set no auth_rt cookie"
printf 'Cookie: %s\n' "$cookie_pair" > "$tmp/old-session-cookie"
cookie_pair=""
pass "step 5: the confirmed account logs in -> 200 with an auth_rt cookie"

# --- Step 6: forgot, no enumeration (criteria 1, 2) -------------------------------------------------------------
export E2E_UNKNOWN_EMAIL="nobody-$RANDOM$RANDOM@example.invalid"
email_body E2E_UNKNOWN_EMAIL "$tmp/unknown-email.json"
post /auth/password/forgot "$tmp/unknown-email.json"
expect_status "step 6: forgot for an address without an account" 202 ""
UNKNOWN_HEADERS="$(header_names)"
post /auth/password/forgot "$tmp/new-email.json"
expect_status "step 6: forgot for an account" 202 ""
[[ "$(header_names)" == "$UNKNOWN_HEADERS" ]] || fail "step 6: the response headers differ between an account and an unknown address"
pass "step 6: forgot -> 202 with an empty body and the same header names for an unknown address and for an account"

# --- Step 7: reset (criteria 3, 6, 7) ---------------------------------------------------------------------------
wait_mail "$NEW_EMAIL" 2 30 || fail "step 7: no reset mail for the account within 30s"
# The dispatcher deletes the row of the unknown address in one statement at the start of a pass; give it time to
# have done so, or the zero below would only mean that nothing was sent yet.
sleep 3
[[ "$(mail_count "$E2E_UNKNOWN_EMAIL")" == "0" ]] || fail "step 7: the catcher holds a mail for the address without an account"
token_body http://localhost:4200/reset "$tmp/reset-weak.json" E2E_WEAK_PASSWORD || fail "step 7: no usable reset link in the mail"
token_body http://localhost:4200/reset "$tmp/reset-good.json" E2E_NEW_PASSWORD || fail "step 7: no usable reset link in the mail"
post /auth/password/reset "$tmp/reset-weak.json"
expect_status "step 7: reset with a weak password" 400 '{"error":"weak_password","rules":["too_short","requires_upper","requires_digit"]}'
post /auth/password/reset "$tmp/reset-good.json"
expect_status "step 7: reset with the same token and a good password" 204 ""
post /auth/password/reset "$tmp/reset-good.json"
expect_status "step 7: the same reset again" 400 '{"error":"invalid_token"}'
pass "step 7: reset mail arrived, none for the unknown address; weak password -> 400 naming every rule (token still usable); good one -> 204; again -> 400 invalid_token"

# --- Step 8: after the reset (criteria 3, 4) --------------------------------------------------------------------
post /auth/login "$tmp/new-old.json"
expect_status "step 8: the old password" 401 '{"error":"invalid_credentials"}'
refresh "$tmp/old-session-cookie"
expect_status "step 8: refresh with the cookie of step 5" 401 '{"error":"invalid_grant"}'
post /auth/login "$tmp/new-new.json"
expect_status "step 8: the new password" 200
pass "step 8: old password -> 401; the refresh cookie from before the reset -> 401 invalid_grant; new password -> 200"

# --- Step 9: a mail outage (criterion 14) -----------------------------------------------------------------------
"${compose[@]}" stop mailpit >/dev/null 2>&1 || fail "step 9: docker compose stop mailpit failed"
MAILPIT_STOPPED=1
[[ "$(curl -s -o /dev/null --max-time 3 -w '%{http_code}' "$MAILPIT_URL/readyz" || true)" != "200" ]] \
  || fail "step 9: the mail catcher still answers after stop"
# Failed first attempts already in the log (none, on a clean stack): the check below needs one more than that.
failed_before="$("${compose[@]}" logs auth 2>/dev/null | grep -c 'failed on attempt 1' || true)"
sent_at=$SECONDS
post /auth/password/forgot "$tmp/seed-email.json"
expect_status "step 9: forgot while the mail server is down" 202 ""
python3 -c 'import sys; sys.exit(0 if float(sys.argv[1]) < 2.0 else 1)' "$TIME_S" \
  || fail "step 9: the request took ${TIME_S}s: it waited for the mail server"
failed_seen=0
for i in $(seq 1 60); do
  n="$("${compose[@]}" logs auth 2>/dev/null | grep -c 'failed on attempt 1' || true)"
  if [[ "${n:-0}" -gt "${failed_before:-0}" ]]; then failed_seen=1; break; fi
  sleep 1
done
(( failed_seen == 1 )) || fail "step 9: the service log shows no failed first attempt within 60s"
"${compose[@]}" start mailpit >/dev/null 2>&1 || fail "step 9: docker compose start mailpit failed"
MAILPIT_STOPPED=0
wait_mailpit || fail "step 9: the mail catcher did not come back within 60s"
wait_mail "$SEED_EMAIL" 1 180 || fail "step 9: no mail for the account within 180s of the request: the retry did not deliver"
pass "step 9: forgot with the mail server down -> 202 in ${TIME_S}s; first attempt failed (service log); after the restart a retry delivered the mail $((SECONDS - sent_at)) s after the request"

echo "ALL PASS"
