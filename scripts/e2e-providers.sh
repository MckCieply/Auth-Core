#!/usr/bin/env bash
# Real-network end-to-end check of spec 0011 (Google and Microsoft sign-in) against the web stack with the fake OpenID provider: Auth-Core,
# PostgreSQL, the mail catcher (Mailpit), the notes service, Caddy (https://localhost:8443) and the fake (http://localhost:8099), which
# Auth-Core trusts as its test issuer (tests/Auth.FakeOidc/compose.yml). The script plays the browser with curl: it follows each redirect
# itself, keeps the cookies in files, and asks the fake for an answer at once (its non-interactive mode: person=...).
#
# What it checks, in order (NEW = a person who signs up with Google; PW = a person with a password account who links Microsoft; INV = a person
# the development company invites; SEED = the development admin). Each step names the criteria it touches; the guarding tests are in
# docs/superpowers/plans/0011-acceptance-map.md.
#   1. health is Healthy; the fake answers; GET /auth/options -> {"signup":"self-service","private_signup":"self-service",
#      "providers":["google","microsoft"]}; the OpenAPI description names the five provider paths - criteria 2, 29.
#   2. the start: an unknown name -> 404 not_found; another purpose -> 400 invalid_request; Google and Microsoft -> 302 to the fake with the
#      client id, the callback address on this origin, the code flow, the scopes, S256, a state, a nonce and prompt=select_account; the
#      request-forgery cookie is HttpOnly, Secure, SameSite=Lax, Path=/auth/providers - criteria 5, 6.
#   3. NEW's round trip with Google: the return address with a ticket and purpose sign_in; auth_pt HttpOnly, Secure, SameSite=Strict,
#      Path=/auth, Max-Age=600; the exchange without auth_pt or with another browser's -> 400 invalid_ticket; with its own -> next
#      finish_signup with both kinds - criteria 7, 12, 14.
#   4. NEW finishes privately -> a login's answer, auth_rt set, auth_pt cleared, org_kind personal; the used ticket -> invalid_ticket; NEW
#      signs in again with the button alone -> authenticated, the same account; /auth/me -> has_password false, linked_providers [google]
#      - criteria 10, 14, 18, 26.
#   5. PW signs up privately through the mail with a password; PW's round trip with Microsoft (a verified address) -> next link, verified;
#      a wrong password -> 401 invalid_credentials; the right one -> authenticated, PW's account; the next round trip -> authenticated
#      without a password; the used ticket -> 400 invalid_ticket; PW's password still signs in; /auth/me -> has_password true,
#      linked_providers [microsoft] - criteria 12, 15, 16, 26.
#   6. a second Microsoft identity with PW's address, unverified (a work address), and one with an address no account has -> the same
#      next link with email_verified false; PW's own Microsoft identity coming back with another address -> authenticated, PW's account; a
#      Google identity without an address -> 403 no_email - criteria 9, 11, 13.
#   7. SEED invites INV; a round trip that starts with the invitation and comes back with another address -> 403 address_mismatch; with INV's
#      address -> authenticated, in the development company, as user, with no password; the invitation and the ticket are used up (a second
#      acceptance -> 400 invalid_token, the ticket again -> 400 invalid_ticket, no pending invitation) - criteria 21, 22.
#   8. NEW deletes the account: a password -> 403 reauth_required; a sign-in ticket -> 403 reauth_failed; the reauth start asks Google for
#      max_age=0; the reauth ticket without auth_pt -> 403 reauth_failed; with it -> 204, auth_rt and auth_pt cleared; NEW's next round trip
#      -> next finish_signup (the account is gone) - criteria 23, 24.
#   9. a cancel at the provider -> the return address with error=cancelled and purpose sign_in and no auth_pt; the same without the
#      request-forgery cookie -> error=invalid_state - criterion 8.
#  10. a look-alike: an account under the micro-sign spelling of an address (made by a Google finish), then the Greek-mu spelling, verified,
#      as another identity -> 409 already_registered - criterion 12.
#  11. the refusals of the exchange the stack reaches only with some set-up (spec 0011, Amendment A8): an invitation accepted with PW's
#      Microsoft identity -> 409 provider_already_linked; the same invitation cancelled by SEED -> 400 invalid_token; an invitation of PW's
#      address (a private account) -> 409 private_account_exists; INV's membership deleted by one SQL statement -> 403 no_membership;
#      INV invited again and its membership put back by one SQL statement -> 409 already_member - criteria 10, 22. Only no_account is out
#      of the stack's reach (both sign-up switches are on): the integration tests guard it.
#  12. the audit rows of the run, read from the database: login.succeeded with details.provider, login.failed, provider.linked,
#      signup.completed, invite.accepted, account.delete_refused, provider.reauthenticated, account.deleted; and no row holds a state, a
#      nonce, a challenge, a code, a request-forgery cookie, a ticket, an auth_pt, a session token, an invitation token or a password of the
#      run (one psql call for all of them, after a control that the search finds a known value) - criteria 28, 33.
#
# Full sequence, from the repo root (the stack is the web sample's with the fake's overlay; the per-IP limiter is OFF, as for the other scripts
# that sign in many times through the one proxy address):
#   cp .env.example .env                  # then set real local values (git-ignored); NOTES_DB_PASSWORD too
#   MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh        # signing/encryption keys into .secrets/ (git-ignored)
#   export COMPOSE_PROJECT_NAME=auth-core-providers       # a project of its own, never "auth-core" (the development stack)
#   files="-f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f samples/notes-web/compose.yml -f tests/Auth.FakeOidc/compose.yml"
#   docker compose $files --env-file .env down -v                                   # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose $files --env-file .env up -d --build
#   scripts/e2e-providers.sh              # this script (does NOT bring the stack up or down)
#   docker compose $files --env-file .env down -v                                   # tear down
#
# Reads AUTH_DEV_SEED_EMAIL and AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced). The database is read with
# `docker compose exec postgres psql`. Needs: curl, python3 (standard library only; E2E_PYTHON=<interpreter> names another, for example
# E2E_PYTHON=C:/p6v/Scripts/python.exe), docker compose, and host ports 8088, 8443, 8080, 8025 and 8099 free. Env: BASE_URL (default
# https://localhost:8443), FAKE_URL (default http://localhost:8099), MAILPIT_URL (default http://localhost:8025). Takes about two minutes (the mails
# are sent at once; the one wait is the mail limit of INV's second invitation, up to a minute). Re-runnable on the same stack: every address is made for the run. Exits non-zero on the first failure; prints
# "PASS <step>" per step; never prints a password, a token, a ticket, a cookie, a state, a code or a mail body.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-https://localhost:8443}"
FAKE_URL="${FAKE_URL:-http://localhost:8099}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
RETURN_URL="$BASE_URL/provider-return"
PY="${E2E_PYTHON:-python3}"
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-providers}"
[[ "$COMPOSE_PROJECT_NAME" != "auth-core" ]] || { echo "FAIL COMPOSE_PROJECT_NAME=auth-core is the development stack; use another name" >&2; exit 1; }
compose=(docker compose -f "$root/deploy/docker-compose.yml" -f "$root/samples/notes-api/compose.yml" -f "$root/samples/notes-web/compose.yml"
  -f "$root/tests/Auth.FakeOidc/compose.yml" --env-file "$root/.env")
# The mail links of this stack (samples/notes-web/compose.yml).
SIGNUP_LINK="http://localhost:8088/complete-signup"
INVITE_LINK="http://localhost:8088/invite"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
: > "$tmp/secrets"

fail() { echo "FAIL $*" >&2; exit 1; }
pass() { echo "PASS $*"; }

# keep_secret <value>: a secret the run saw, for the search of the audit log at the end. An empty value (a member an answer did not have, such
# as the code of a cancelled round trip) is skipped: it is no secret, and the search of the last step refuses an empty one.
keep_secret() {
  if [[ -n "$1" ]]; then printf '%s\n' "$1" >> "$tmp/secrets"; fi
}

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
NEW_EMAIL="new-$run@gmail.test"
PW_EMAIL="pw-$run@poczta.test"
NOBODY_EMAIL="nobody-$run@firma.test"
INV_EMAIL="inv-$run@gmail.test"
OTHER_EMAIL="other-$run@gmail.test"
LINKED_EMAIL="linked-$run@gmail.test"
# PW's Microsoft identity has a fixed object id, so that step 11 can present the same identity with another address.
PW_OID="$("$PY" -c 'import uuid; print(uuid.uuid4())' | tr -d '\r')"
export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD" E2E_PW_EMAIL="$PW_EMAIL" E2E_INV_EMAIL="$INV_EMAIL"
export E2E_LINKED_EMAIL="$LINKED_EMAIL"
export E2E_PASSWORD="E2e-Passw0rd-$run" E2E_WRONG_PASSWORD="E2e-Wrong-Passw0rd-$run"
keep_secret "$E2E_PASSWORD"
keep_secret "$E2E_WRONG_PASSWORD"
keep_secret "$SEED_PASSWORD"

wait_ok() { # wait_ok <url> <seconds>: waits until the URL answers 200 (any certificate)
  local i
  for i in $(seq 1 "$2"); do
    if [[ "$(curl -s -k -o /dev/null --max-time 3 -w '%{http_code}' "$1" || true)" == "200" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

# call <method> <path> [body-file] [header-file ...] -> sets HTTP_CODE and BODY; response headers in $tmp/hdr. The URL reaches curl through a
# config file, so that a state or a code in it never shows in the process list.
call() {
  local file
  printf 'url = "%s"\n' "$BASE_URL$2" > "$tmp/url.cfg"
  local args=(-sS -k --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' -X "$1" -K "$tmp/url.cfg")
  if [[ -n "${3:-}" ]]; then args+=(-H 'Content-Type: application/json' --data-binary "@$3"); fi
  for file in "${@:4}"; do
    if [[ -n "$file" ]]; then args+=(-H "@$file"); fi
  done
  HTTP_CODE="$(curl "${args[@]}")"
  BODY="$(cat "$tmp/body")"
}

header() { # value of a response header of the last response, CR stripped (empty when absent)
  { grep -i "^$1:" "$tmp/hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//'
}

# A Windows python3 ends its lines with CR LF: the helpers that print a value strip the CR.
val() { "$PY" -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1" <<< "$BODY" | tr -d '\r'; }

enc() { "$PY" -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$1" | tr -d '\r'; }

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

set_cookie_line() { # set_cookie_line <name-pattern>: the Set-Cookie line of that cookie in the last response, CR stripped (empty when none)
  { grep -iE "^set-cookie: $1=" "$tmp/hdr" || true; } | head -n1 | tr -d '\r'
}

pair_of() { local line="${1#*: }"; printf '%s' "${line%%;*}"; }   # pair_of <set-cookie-line>: name=value

cookie_value() { local pair; pair="$(pair_of "$(set_cookie_line "$1")")"; printf '%s' "${pair#*=}"; }

has_attr() { # has_attr <set-cookie-line> <attribute>: exact, case-insensitive match of one "; "-separated attribute
  printf '%s' "$1" | tr ';' '\n' | sed 's/^ *//' | grep -qixF -- "$2"
}

expect_attrs() { # expect_attrs <what> <set-cookie-line> <attribute> ...
  local what="$1" line="$2" attr
  [[ -n "$line" ]] || fail "$what: the cookie was not set"
  for attr in "${@:3}"; do
    has_attr "$line" "$attr" || fail "$what: the cookie lacks the $attr attribute"
  done
}

url_param() { # url_param <file-with-a-url> <query|fragment> <name>: one parameter of the URL in the file (empty when absent); never printed by the callers
  "$PY" -c '
import sys, urllib.parse
parts = urllib.parse.urlsplit(open(sys.argv[1], encoding="utf-8").read().strip())
text = parts.query if sys.argv[2] == "query" else parts.fragment
print(urllib.parse.parse_qs(text, keep_blank_values=True).get(sys.argv[3], [""])[0])
' "$1" "$2" "$3" | tr -d '\r'
}

fragment_of() { # fragment_of <name>: the members of the fragment of <name>'s return address as JSON, a ticket replaced by its length
  "$PY" -c '
import json, re, sys, urllib.parse
fragment = urllib.parse.urlsplit(open(sys.argv[1], encoding="utf-8").read().strip()).fragment
members = {k: v[0] if len(v) == 1 else v for k, v in urllib.parse.parse_qs(fragment, keep_blank_values=True).items()}
if "ticket" in members:
    members["ticket"] = len(members["ticket"]) if re.fullmatch(r"[A-Za-z0-9_-]+", members["ticket"]) else "not base64url"
print(json.dumps(members, sort_keys=True, separators=(",", ":")))
' "$tmp/$1.return" | tr -d '\r'
}

# round_trip <name> <provider> <start-query> <fake-parameters> [nocookie]: one browser's round trip, the fake answering at once. Keeps the start's
# Location in $tmp/<name>.start, the callback's Location in $tmp/<name>.return, the ticket (when there is one) in $tmp/<name>.ticket, the auth_pt
# header file in $tmp/<name>.pt and the Set-Cookie line of auth_pt in AUTH_PT_LINE; every secret it sees goes to $tmp/secrets. With "nocookie"
# the callback is sent without the request-forgery cookie.
round_trip() {
  local name="$1" provider="$2" line member back state_header
  call GET "/auth/providers/$provider/start$3"
  expect_status "$name: the start" 302
  header location > "$tmp/$name.start"
  [[ "$(cat "$tmp/$name.start")" == "$FAKE_URL/$provider/authorize?"* ]] || fail "$name: the start does not send the browser to the fake's $provider"
  line="$(set_cookie_line 'OpenIddict\.Client\.State[^=]*')"
  [[ -n "$line" ]] || fail "$name: the start set no request-forgery cookie"
  printf 'Cookie: %s\n' "$(pair_of "$line")" > "$tmp/$name.state"
  keep_secret "$(pair_of "$line" | cut -d= -f2-)"
  for member in state nonce code_challenge; do keep_secret "$(url_param "$tmp/$name.start" query "$member")"; done

  printf 'url = "%s&%s"\n' "$(cat "$tmp/$name.start")" "$4" > "$tmp/fake.cfg"
  [[ "$(curl -sS --max-time 20 -K "$tmp/fake.cfg" -o /dev/null -D "$tmp/fake.hdr" -w '%{http_code}')" == "302" ]] \
    || fail "$name: the fake did not answer 302"
  { grep -i '^location:' "$tmp/fake.hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//' > "$tmp/$name.back"
  back="$(cat "$tmp/$name.back")"
  [[ "$back" == "$BASE_URL/auth/providers/$provider/callback?"* ]] || fail "$name: the fake did not send the browser to the callback"
  keep_secret "$(url_param "$tmp/$name.back" query code)"

  state_header="$tmp/$name.state"
  if [[ "${5:-}" == "nocookie" ]]; then state_header=""; fi
  call GET "${back#"$BASE_URL"}" "" "$state_header"
  expect_status "$name: the callback" 302
  expect_no_store "$name: the callback"
  header location > "$tmp/$name.return"
  [[ "$(cat "$tmp/$name.return")" == "$RETURN_URL#"* ]] || fail "$name: the callback did not send the browser to the return address"
  rm -f "$tmp/$name.ticket" "$tmp/$name.pt"
  AUTH_PT_LINE="$(set_cookie_line auth_pt)"
  if [[ -n "$(url_param "$tmp/$name.return" fragment ticket)" ]]; then
    url_param "$tmp/$name.return" fragment ticket > "$tmp/$name.ticket"
    keep_secret "$(cat "$tmp/$name.ticket")"
    [[ -n "$AUTH_PT_LINE" ]] || fail "$name: the callback made a ticket and set no auth_pt"
    printf 'Cookie: %s\n' "$(pair_of "$AUTH_PT_LINE")" > "$tmp/$name.pt"
    keep_secret "$(pair_of "$AUTH_PT_LINE" | cut -d= -f2-)"
  fi
}

ticket_json() { # ticket_json <name> <member> <out-file> [python dict of more members, may read os.environ]: a body with the ticket of <name>
  "$PY" -c '
import json, os, sys
body = {sys.argv[2]: open(sys.argv[1], encoding="utf-8").read().strip()}
body.update(eval(sys.argv[3]))
print(json.dumps(body))
' "$tmp/$1.ticket" "$2" "${4:-dict()}" > "$3"
}

expect_signed_in() { # expect_signed_in <what> <who>: a login's answer, auth_rt set, auth_pt cleared; keeps the bearer header in $tmp/<who>.auth
  local line
  expect_status "$1" 200
  expect_eq "$1: the body is a login's" "$(val 'sorted(d.keys())')" "['access_token', 'status']"
  expect_eq "$1: status" "$(val 'd["status"]')" "authenticated"
  line="$(set_cookie_line auth_rt)"
  [[ -n "$line" ]] || fail "$1: no auth_rt cookie"
  line="$(set_cookie_line auth_pt)"
  expect_attrs "$1: the cleared auth_pt" "$line" "Max-Age=0" "Path=/auth"
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$2.auth"
  keep_secret "$(val 'd["access_token"]')"
  keep_secret "$(cookie_value auth_rt)"
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

# token_body <link-prefix> <file> [password-env]: takes the token from the text part of $tmp/mail.json and writes {"token"} (plus "password"
# when named). Never printed.
token_body() {
  "$PY" -c '
import json, os, re, sys
mail = json.load(sys.stdin.buffer)
match = re.search(re.escape(sys.argv[1]) + r"\?token=([A-Za-z0-9_-]{43})\s", mail["Text"])
if not match:
    sys.exit("the mail holds no link with a token")
body = {"token": match.group(1)}
if sys.argv[2]:
    body["password"] = os.environ[sys.argv[2]]
print(json.dumps(body))
' "$1" "${3:-}" < "$tmp/mail.json" > "$2"
}

token_of() { "$PY" -c 'import json,sys; print(json.load(sys.stdin)["token"])' < "$1" | tr -d '\r'; }   # never printed by the callers

# psql_value <sql>: the value the query returns. A value of the service under test goes into a statement only as a GUID it returned (check_guid
# refuses anything else where it is read) or as the database's own timestamp; no secret ever goes into a statement given here.
psql_value() {
  printf '%s\n' "$1" | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA | tr -d '\r'
}

check_guid() { # check_guid <what> <value>: the value is a GUID (it is then safe in a statement)
  [[ "$2" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]] || fail "$1 is not a GUID"
}

# secrets_in_audit <file>: how many pairs of (a line of the file, a row of the audit log) where the row holds the line in any column. One psql
# call: Python builds one statement from the file (single quotes doubled) and it goes to psql on stdin, so no secret is on a command line or on
# the screen; psql's standard error (which would echo a failed statement) goes to a file that is never printed. Empty lines are refused.
secrets_in_audit() {
  local count
  [[ -s "$1" ]] && ! grep -qx '' "$1" || fail "the file of values to look for is empty or holds an empty line"
  count="$("$PY" -c '
import sys
lines = sys.stdin.buffer.read().decode("utf-8").split("\n")
if lines and lines[-1] == "":
    lines.pop()
if not lines or any(line == "" for line in lines):
    sys.exit(1)
values = ",".join("(\x27" + line.replace("\x27", "\x27\x27") + "\x27)" for line in sorted(set(lines)))
sys.stdout.buffer.write((
    "WITH s(v) AS (VALUES " + values + ") SELECT count(*) FROM s JOIN audit_events a ON position(s.v in (a.kind || \x27 \x27 || "
    "coalesce(a.subject_email, \x27\x27) || \x27 \x27 || coalesce(a.org_name, \x27\x27) || \x27 \x27 || coalesce(a.client_ip, \x27\x27) || \x27 \x27 || "
    "coalesce(a.details::text, \x27\x27))) > 0;\n"
).encode("utf-8"))
' < "$1" \
    | "${compose[@]}" exec -T postgres psql -U auth -d auth -tA -v ON_ERROR_STOP=1 2> "$tmp/psql.err" | tr -d '\r' || true)"
  [[ "$count" =~ ^[0-9]+$ ]] || fail "the audit log could not be searched (the database answer is not a number)"
  printf '%s\n' "$count"
}

# --- Step 1: health, the fake, the options, the description (criteria 2, 29) ------------------------------------------
wait_ok "$BASE_URL/auth/health" 120 || fail "step 1: $BASE_URL/auth/health did not return 200 within 120s (is the stack up? see the header)"
wait_ok "$FAKE_URL/google/.well-known/openid-configuration" 60 || fail "step 1: the fake provider ($FAKE_URL) did not answer within 60s"
wait_ok "$MAILPIT_URL/readyz" 60 || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s"
START_TS="$(psql_value "SELECT now()")"
call GET /auth/health
expect_status "step 1: health" 200 "Healthy"
call GET /auth/options
expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft"]}'
expect_no_store "step 1: the options"
call GET /auth/openapi/v1.json
expect_status "step 1: the OpenAPI description" 200
expect_eq "step 1: the five provider paths are described" \
  "$(val "sorted(p for p in d['paths'] if p.startswith('/auth/providers/'))")" \
  "['/auth/providers/exchange', '/auth/providers/finish', '/auth/providers/link', '/auth/providers/{name}/callback', '/auth/providers/{name}/start']"
pass "step 1: health is Healthy; the fake answers; GET /auth/options offers google and microsoft; the description names the five provider paths"

# --- Step 2: the start (criteria 5, 6) --------------------------------------------------------------------------------------
call GET /auth/providers/github/start
expect_error "step 2: an unknown name" 404 not_found
call GET "/auth/providers/google/start?purpose=other"
expect_error "step 2: another purpose" 400 invalid_request
for provider in google microsoft; do
  call GET "/auth/providers/$provider/start"
  expect_status "step 2: the start of $provider" 302
  header location > "$tmp/start.url"
  [[ "$(cat "$tmp/start.url")" == "$FAKE_URL/$provider/authorize?"* ]] || fail "step 2: the start of $provider does not go to the fake"
  [[ -n "$(url_param "$tmp/start.url" query client_id)" ]] || fail "step 2: $provider: no client_id"
  expect_eq "step 2: $provider: redirect_uri" "$(url_param "$tmp/start.url" query redirect_uri)" "$BASE_URL/auth/providers/$provider/callback"
  expect_eq "step 2: $provider: response_type" "$(url_param "$tmp/start.url" query response_type)" "code"
  expect_eq "step 2: $provider: scope" "$(url_param "$tmp/start.url" query scope | tr ' ' '\n' | sort | tr '\n' ' ')" "email openid profile "
  expect_eq "step 2: $provider: code_challenge_method" "$(url_param "$tmp/start.url" query code_challenge_method)" "S256"
  expect_eq "step 2: $provider: prompt" "$(url_param "$tmp/start.url" query prompt)" "select_account"
  for member in state nonce code_challenge; do
    [[ -n "$(url_param "$tmp/start.url" query "$member")" ]] || fail "step 2: $provider: no $member"
    keep_secret "$(url_param "$tmp/start.url" query "$member")"
  done
  LINE="$(set_cookie_line 'OpenIddict\.Client\.State[^=]*')"
  expect_attrs "step 2: $provider: the request-forgery cookie" "$LINE" HttpOnly Secure SameSite=Lax Path=/auth/providers
  keep_secret "$(pair_of "$LINE" | cut -d= -f2-)"
done
pass "step 2: an unknown name -> 404, another purpose -> 400; each start -> 302 to the fake with the code flow, PKCE, a state, a nonce and the callback on this origin; the cookie is Lax"

# --- Step 3: NEW's round trip and the browser binding (criteria 7, 12, 14) ---------------------------------------------
round_trip new google "" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
expect_eq "step 3: the fragment" "$(fragment_of new)" '{"purpose":"sign_in","ticket":43}'
expect_attrs "step 3: auth_pt" "$AUTH_PT_LINE" HttpOnly Secure SameSite=Strict Path=/auth Max-Age=600
round_trip other google "" "person=custom&email=$(enc "$OTHER_EMAIL")&verified=true"
ticket_json new ticket "$tmp/new.exchange"
call POST /auth/providers/exchange "$tmp/new.exchange"
expect_error "step 3: the exchange without auth_pt" 400 invalid_ticket
call POST /auth/providers/exchange "$tmp/new.exchange" "$tmp/other.pt"
expect_error "step 3: the exchange with another browser's auth_pt" 400 invalid_ticket
call POST /auth/providers/exchange "$tmp/new.exchange" "$tmp/new.pt"
expect_status "step 3: the exchange" 200 "{\"next\":\"finish_signup\",\"email\":\"$NEW_EMAIL\",\"kinds\":[\"organization\",\"private\"]}"
pass "step 3: a round trip -> the return address with a 43-character ticket and auth_pt (Strict, 10 minutes); without it or with another browser's -> invalid_ticket; with it -> next finish_signup"

# --- Step 4: NEW finishes privately and signs in again (criteria 10, 14, 18, 26) -----------------------------------------------
ticket_json new ticket "$tmp/new.finish" "{'kind': 'private'}"
call POST /auth/providers/finish "$tmp/new.finish" "$tmp/new.pt"
expect_signed_in "step 4: the finish" new
BODY="$(jwt_payload "$tmp/new.auth")"
expect_eq "step 4: org_kind" "$(val 'd["org_kind"]')" "personal"
expect_eq "step 4: roles" "$(val 'd["roles"]')" "['private']"
NEW_SUB="$(val 'd["sub"]')"
check_guid "NEW's sub" "$NEW_SUB"
call POST /auth/providers/exchange "$tmp/new.exchange" "$tmp/new.pt"
expect_error "step 4: the used ticket" 400 invalid_ticket
round_trip new2 google "" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
ticket_json new2 ticket "$tmp/new2.exchange"
call POST /auth/providers/exchange "$tmp/new2.exchange" "$tmp/new2.pt"
expect_signed_in "step 4: the button alone" new
BODY="$(jwt_payload "$tmp/new.auth")"
expect_eq "step 4: the same account" "$(val 'd["sub"]')" "$NEW_SUB"
call GET /auth/me "" "$tmp/new.auth"
expect_status "step 4: GET /auth/me" 200
expect_eq "step 4: has_password" "$(val 'd["has_password"]')" "False"
expect_eq "step 4: linked_providers" "$(val 'd["linked_providers"]')" "['google']"
pass "step 4: the private finish signs NEW in (personal space, auth_pt cleared); the ticket is used up; the button alone signs NEW in again; /auth/me says no password and google"

# --- Step 5: PW links Microsoft with the password (criteria 12, 15, 16, 26) -----------------------------------------------------
json_body "$tmp/pw.signup" "{'email': os.environ['E2E_PW_EMAIL'], 'kind': 'private'}"
call POST /auth/signup "$tmp/pw.signup"
expect_status "step 5: PW's private sign-up request" 202 ""
wait_mail "$PW_EMAIL" "Complete your sign-up" 90 || fail "step 5: no sign-up mail for PW within 90s"
token_body "$SIGNUP_LINK" "$tmp/pw.complete" E2E_PASSWORD || fail "step 5: no usable sign-up link in PW's mail"
keep_secret "$(token_of "$tmp/pw.complete")"
call POST /auth/signup/complete "$tmp/pw.complete"
expect_status "step 5: PW's completion" 200
keep_secret "$(val 'd["access_token"]')"
keep_secret "$(cookie_value auth_rt)"
printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/pw.auth"
BODY="$(jwt_payload "$tmp/pw.auth")"
PW_SUB="$(val 'd["sub"]')"
check_guid "PW's sub" "$PW_SUB"
round_trip pw microsoft "" "person=custom&email=$(enc "$PW_EMAIL")&verified=true&subject=$PW_OID"
ticket_json pw ticket "$tmp/pw.exchange"
call POST /auth/providers/exchange "$tmp/pw.exchange" "$tmp/pw.pt"
expect_status "step 5: the exchange" 200 "{\"next\":\"link\",\"email\":\"$PW_EMAIL\",\"email_verified\":true}"
ticket_json pw ticket "$tmp/pw.wrong" "{'password': os.environ['E2E_WRONG_PASSWORD']}"
call POST /auth/providers/link "$tmp/pw.wrong" "$tmp/pw.pt"
expect_error "step 5: a wrong password" 401 invalid_credentials
ticket_json pw ticket "$tmp/pw.link" "{'password': os.environ['E2E_PASSWORD']}"
call POST /auth/providers/link "$tmp/pw.link" "$tmp/pw.pt"
expect_signed_in "step 5: the link" pw
BODY="$(jwt_payload "$tmp/pw.auth")"
expect_eq "step 5: PW's account" "$(val 'd["sub"]')" "$PW_SUB"
round_trip pw2 microsoft "" "person=custom&email=$(enc "$PW_EMAIL")&verified=true&subject=$PW_OID"
ticket_json pw2 ticket "$tmp/pw2.exchange"
call POST /auth/providers/exchange "$tmp/pw2.exchange" "$tmp/pw2.pt"
expect_signed_in "step 5: the button alone after the link" pw
BODY="$(jwt_payload "$tmp/pw.auth")"
expect_eq "step 5: still PW's account" "$(val 'd["sub"]')" "$PW_SUB"
call POST /auth/providers/exchange "$tmp/pw.exchange" "$tmp/pw.pt"
expect_error "step 5: the ticket of the link, used up" 400 invalid_ticket
json_body "$tmp/pw.login" "{'email': os.environ['E2E_PW_EMAIL'], 'password': os.environ['E2E_PASSWORD']}"
call POST /auth/login "$tmp/pw.login"
expect_status "step 5: the password still signs PW in" 200
keep_secret "$(val 'd["access_token"]')"
keep_secret "$(cookie_value auth_rt)"
call GET /auth/me "" "$tmp/pw.auth"
expect_status "step 5: GET /auth/me" 200
expect_eq "step 5: has_password" "$(val 'd["has_password"]')" "True"
expect_eq "step 5: linked_providers" "$(val 'd["linked_providers"]')" "['microsoft']"
pass "step 5: a verified address of a password account -> next link; a wrong password -> 401; the right one links and signs in; the button alone signs in next time; the ticket is used up; the password still signs in"

# --- Step 6: unverified addresses and no address (criteria 9, 11, 13) ------------------------------------------------------
SECOND_OID="$("$PY" -c 'import uuid; print(uuid.uuid4())' | tr -d '\r')"
round_trip work microsoft "" "person=custom&email=$(enc "$PW_EMAIL")&subject=$SECOND_OID"
ticket_json work ticket "$tmp/work.exchange"
call POST /auth/providers/exchange "$tmp/work.exchange" "$tmp/work.pt"
expect_status "step 6: an unverified address that has an account" 200 "{\"next\":\"link\",\"email\":\"$PW_EMAIL\",\"email_verified\":false}"
round_trip moved microsoft "" "person=custom&email=$(enc "$OTHER_EMAIL")&verified=true&subject=$PW_OID"
ticket_json moved ticket "$tmp/moved.exchange"
call POST /auth/providers/exchange "$tmp/moved.exchange" "$tmp/moved.pt"
expect_signed_in "step 6: a linked identity whose address changed" moved
BODY="$(jwt_payload "$tmp/moved.auth")"
expect_eq "step 6: it signs in to the same account" "$(val 'd["sub"]')" "$PW_SUB"
round_trip nobody microsoft "" "person=custom&email=$(enc "$NOBODY_EMAIL")"
ticket_json nobody ticket "$tmp/nobody.exchange"
call POST /auth/providers/exchange "$tmp/nobody.exchange" "$tmp/nobody.pt"
expect_status "step 6: an unverified address that has none" 200 "{\"next\":\"link\",\"email\":\"$NOBODY_EMAIL\",\"email_verified\":false}"
round_trip noemail google "" "person=google&subject=noemail-$run&defect=no_email"
ticket_json noemail ticket "$tmp/noemail.exchange"
call POST /auth/providers/exchange "$tmp/noemail.exchange" "$tmp/noemail.pt"
expect_error "step 6: an identity without an address" 403 no_email
pass "step 6: an unverified work address -> the same next link (email_verified false) whether or not an account has it; the linked identity signs in whatever address it brings; no address -> 403 no_email"

# --- Step 7: an invitation accepted with Google (criteria 21, 22) -----------------------------------------------------------
json_body "$tmp/seed.login" "{'email': os.environ['E2E_SEED_EMAIL'], 'password': os.environ['E2E_SEED_PASSWORD']}"
call POST /auth/login "$tmp/seed.login"
expect_status "step 7: SEED's login" 200
keep_secret "$(cookie_value auth_rt)"
printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/seed.auth"
keep_secret "$(val 'd["access_token"]')"
call GET /auth/me "" "$tmp/seed.auth"
expect_status "step 7: GET /auth/me of SEED" 200
DEV_ORG="$(val 'd["org_id"]')"
check_guid "the development company's id" "$DEV_ORG"
call GET /auth/org/roles "" "$tmp/seed.auth"
expect_status "step 7: GET /auth/org/roles" 200
export E2E_USER_ROLE
E2E_USER_ROLE="$(val '[r["id"] for r in d["roles"] if r["name"] == "user"][0]')"
MAILS="$(mail_count "$INV_EMAIL")"
json_body "$tmp/invite.json" "{'email': os.environ['E2E_INV_EMAIL'], 'role_id': os.environ['E2E_USER_ROLE']}"
call POST /auth/org/invites "$tmp/invite.json" "$tmp/seed.auth"
expect_status "step 7: SEED invites INV" 202 ""
wait_mail "$INV_EMAIL" "You are invited" 90 "$MAILS" || fail "step 7: no invitation mail for INV within 90s"
token_body "$INVITE_LINK" "$tmp/inv.token" || fail "step 7: no usable invitation link"
INVITE_TOKEN="$(token_of "$tmp/inv.token")"
keep_secret "$INVITE_TOKEN"
round_trip mismatch google "?invite=$INVITE_TOKEN" "person=custom&email=$(enc "$OTHER_EMAIL")&verified=true"
expect_eq "step 7: the fragment" "$(fragment_of mismatch)" '{"purpose":"invite","ticket":43}'
ticket_json mismatch ticket "$tmp/mismatch.exchange"
call POST /auth/providers/exchange "$tmp/mismatch.exchange" "$tmp/mismatch.pt"
expect_error "step 7: another address" 403 address_mismatch
round_trip inv google "?invite=$INVITE_TOKEN" "person=custom&email=$(enc "$INV_EMAIL")&verified=true"
ticket_json inv ticket "$tmp/inv.exchange"
call POST /auth/providers/exchange "$tmp/inv.exchange" "$tmp/inv.pt"
expect_signed_in "step 7: the acceptance" inv
BODY="$(jwt_payload "$tmp/inv.auth")"
expect_eq "step 7: INV is in the development company" "$(val 'd["org_id"]')" "$DEV_ORG"
expect_eq "step 7: org_kind" "$(val 'd["org_kind"]')" "organization"
expect_eq "step 7: roles" "$(val 'd["roles"]')" "['user']"
INV_SUB="$(val 'd["sub"]')"
check_guid "INV's sub" "$INV_SUB"
call GET /auth/me "" "$tmp/inv.auth"
expect_status "step 7: GET /auth/me of INV" 200
expect_eq "step 7: INV has no password" "$(val 'd["has_password"]')" "False"
expect_eq "step 7: INV's linked_providers" "$(val 'd["linked_providers"]')" "['google']"
call GET /auth/org/invites "" "$tmp/seed.auth"
expect_status "step 7: GET /auth/org/invites" 200
expect_eq "step 7: the invitation is used up" "$(val "[i['email'] for i in d['invites'] if i['email'].lower() == '$INV_EMAIL']")" "[]"
call POST /auth/providers/exchange "$tmp/inv.exchange" "$tmp/inv.pt"
expect_error "step 7: the used ticket" 400 invalid_ticket
round_trip second google "?invite=$INVITE_TOKEN" "person=custom&email=$(enc "$INV_EMAIL")&verified=true"
ticket_json second ticket "$tmp/second.exchange"
call POST /auth/providers/exchange "$tmp/second.exchange" "$tmp/second.pt"
expect_error "step 7: a second acceptance of the invitation" 400 invalid_token
pass "step 7: an invitation started with Google: another address -> 403 address_mismatch; the invited address -> signed in to the company as user, without a password; the invitation and the ticket are used up"

# --- Step 8: NEW deletes the account with Google (criteria 23, 24) -----------------------------------------------------------
json_body "$tmp/new.password" "{'password': os.environ['E2E_PASSWORD']}"
call DELETE /auth/account "$tmp/new.password" "$tmp/new.auth"
expect_error "step 8: a password for an account without one" 403 reauth_required
round_trip signin google "" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
ticket_json signin provider_ticket "$tmp/signin.delete"
call DELETE /auth/account "$tmp/signin.delete" "$tmp/new.auth" "$tmp/signin.pt"
expect_error "step 8: a sign-in ticket" 403 reauth_failed
call GET "/auth/providers/google/start?purpose=reauth"
expect_status "step 8: the reauth start" 302
header location > "$tmp/reauth.url"
expect_eq "step 8: Google is asked for a fresh sign-in" "$(url_param "$tmp/reauth.url" query max_age)" "0"
expect_eq "step 8: and not for an account choice" "$(url_param "$tmp/reauth.url" query prompt)" ""
for member in state nonce code_challenge; do keep_secret "$(url_param "$tmp/reauth.url" query "$member")"; done
keep_secret "$(pair_of "$(set_cookie_line 'OpenIddict\.Client\.State[^=]*')" | cut -d= -f2-)"
round_trip reauth google "?purpose=reauth" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
expect_eq "step 8: the fragment" "$(fragment_of reauth)" '{"purpose":"reauth","ticket":43}'
ticket_json reauth provider_ticket "$tmp/reauth.delete"
call DELETE /auth/account "$tmp/reauth.delete" "$tmp/new.auth"
expect_error "step 8: the reauth ticket without auth_pt" 403 reauth_failed
call DELETE /auth/account "$tmp/reauth.delete" "$tmp/new.auth" "$tmp/reauth.pt"
expect_status "step 8: the deletion" 204 ""
expect_attrs "step 8: the cleared auth_rt" "$(set_cookie_line auth_rt)" "Max-Age=0"
expect_attrs "step 8: the cleared auth_pt" "$(set_cookie_line auth_pt)" "Max-Age=0"
round_trip gone google "" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
ticket_json gone ticket "$tmp/gone.exchange"
call POST /auth/providers/exchange "$tmp/gone.exchange" "$tmp/gone.pt"
expect_status "step 8: the deleted account's identity" 200 "{\"next\":\"finish_signup\",\"email\":\"$NEW_EMAIL\",\"kinds\":[\"organization\",\"private\"]}"
pass "step 8: a password -> reauth_required; a sign-in ticket or no auth_pt -> reauth_failed; the reauth ticket deletes the account (204, both cookies cleared); the identity is free again"

# --- Step 9: a cancel at the provider (criterion 8) ----------------------------------------------------------------------------
round_trip cancel google "" "person=cancel"
expect_eq "step 9: the fragment" "$(fragment_of cancel)" '{"error":"cancelled","purpose":"sign_in"}'
[[ -z "$AUTH_PT_LINE" ]] || fail "step 9: a cancel set auth_pt"
round_trip forged google "" "person=cancel" nocookie
expect_eq "step 9: the fragment without the request-forgery cookie" "$(fragment_of forged)" '{"error":"invalid_state","purpose":"sign_in"}'
pass "step 9: a cancel -> error=cancelled with its purpose and no auth_pt; without the request-forgery cookie -> error=invalid_state"

# --- Step 10: a look-alike address (criterion 12) ----------------------------------------------------------------------------
# The micro sign and the Greek mu upper-case to one letter: an account under one spelling makes the other a look-alike. Python builds them with
# chr() and hands the fake only their percent-encoded form, so that the script holds no character outside ASCII.
lookalike() { # lookalike <code point in hex>: the percent-encoded address "<that character>aria-<run>@gmail.test"
  "$PY" -c 'import sys, urllib.parse; print(urllib.parse.quote(chr(int(sys.argv[1], 16)) + "aria-" + sys.argv[2] + "@gmail.test", safe=""))' "$1" "$run" \
    | tr -d '\r'
}
round_trip micro google "" "person=custom&email=$(lookalike B5)&verified=true"
ticket_json micro ticket "$tmp/micro.exchange"
call POST /auth/providers/exchange "$tmp/micro.exchange" "$tmp/micro.pt"
expect_status "step 10: the micro-sign spelling" 200
expect_eq "step 10: the micro-sign spelling has no account yet" "$(val 'd["next"]')" "finish_signup"
ticket_json micro ticket "$tmp/micro.finish" "{'kind': 'private'}"
call POST /auth/providers/finish "$tmp/micro.finish" "$tmp/micro.pt"
expect_signed_in "step 10: the account of the micro-sign spelling" micro
# The fake derives a custom person's identity from the upper-cased address: an explicit subject makes the Greek mu another identity.
round_trip mu google "" "person=custom&email=$(lookalike 3BC)&verified=true&subject=mu-$run"
ticket_json mu ticket "$tmp/mu.exchange"
call POST /auth/providers/exchange "$tmp/mu.exchange" "$tmp/mu.pt"
expect_error "step 10: the Greek-mu spelling of that address" 409 already_registered
pass "step 10: an address that only resembles an account's (micro sign and Greek mu) -> 409 already_registered"

# --- Step 11: the refusals that need a set-up (criteria 10, 22; Amendment A8) --------------------------------------------------
# invite <email-env> <name>: SEED invites the address as user; the token of the invitation mail goes to $tmp/<name>.invite (never printed)
invite() {
  local before
  before="$(mail_count "${!1}")"
  json_body "$tmp/$2.invite.json" "{'email': os.environ['$1'], 'role_id': os.environ['E2E_USER_ROLE']}"
  call POST /auth/org/invites "$tmp/$2.invite.json" "$tmp/seed.auth"
  # The invitation mail of one company and address is limited to one a minute: INV was invited in step 7, and the second invitation may come
  # within that minute. A 429 of that limit is waited out once.
  if [[ "$HTTP_CODE" == "429" ]]; then
    [[ "$BODY" =~ ^\{\"error\":\"too_many_attempts\",\"retry_after_seconds\":([0-9]+)\}$ ]] || fail "step 11: unexpected 429 body of the invitation of $2"
    echo "waiting $((BASH_REMATCH[1] + 1)) s for the mail limit of the invitation..."
    sleep "$((BASH_REMATCH[1] + 1))"
    call POST /auth/org/invites "$tmp/$2.invite.json" "$tmp/seed.auth"
  fi
  expect_status "step 11: SEED invites $2" 202 ""
  wait_mail "${!1}" "You are invited" 90 "$before" || fail "step 11: no invitation mail for $2 within 90s"
  token_body "$INVITE_LINK" "$tmp/$2.invite.body" || fail "step 11: no usable invitation link for $2"
  token_of "$tmp/$2.invite.body" > "$tmp/$2.invite"
  keep_secret "$(cat "$tmp/$2.invite")"
}

# provider_already_linked: a new address invited, accepted with PW's Microsoft identity (the same object id, another address).
invite E2E_LINKED_EMAIL linked
round_trip linked microsoft "?invite=$(cat "$tmp/linked.invite")" "person=custom&email=$(enc "$LINKED_EMAIL")&verified=true&subject=$PW_OID"
ticket_json linked ticket "$tmp/linked.exchange"
call POST /auth/providers/exchange "$tmp/linked.exchange" "$tmp/linked.pt"
expect_error "step 11: an identity that is already a login of an account" 409 provider_already_linked

# invalid_token: SEED cancels that invitation; a round trip with its token (the start reads no invitation) is refused at the exchange.
call GET /auth/org/invites "" "$tmp/seed.auth"
expect_status "step 11: GET /auth/org/invites" 200
LINKED_INVITE="$(val "[i['id'] for i in d['invites'] if i['email'].lower() == '$LINKED_EMAIL'][0]")"
call DELETE "/auth/org/invites/$LINKED_INVITE" "" "$tmp/seed.auth"
expect_status "step 11: SEED cancels the invitation" 204 ""
round_trip cancelled google "?invite=$(cat "$tmp/linked.invite")" "person=custom&email=$(enc "$LINKED_EMAIL")&verified=true"
ticket_json cancelled ticket "$tmp/cancelled.exchange"
call POST /auth/providers/exchange "$tmp/cancelled.exchange" "$tmp/cancelled.pt"
expect_error "step 11: a cancelled invitation" 400 invalid_token

# private_account_exists: PW's address (a private account) invited, accepted with a Google identity of that address.
invite E2E_PW_EMAIL pwinvite
round_trip pwinvite google "?invite=$(cat "$tmp/pwinvite.invite")" "person=custom&email=$(enc "$PW_EMAIL")&verified=true"
ticket_json pwinvite ticket "$tmp/pwinvite.exchange"
call POST /auth/providers/exchange "$tmp/pwinvite.exchange" "$tmp/pwinvite.pt"
expect_error "step 11: an invitation of a private account's address" 409 private_account_exists

# no_membership: INV's membership deleted by one SQL statement (no API leaves an account without one); INV's linked identity is refused.
expect_eq "step 11: INV's membership is deleted" \
  "$(psql_value "WITH gone AS (DELETE FROM \"Memberships\" WHERE \"UserId\" = '$INV_SUB' RETURNING 1) SELECT count(*) FROM gone")" "1"
round_trip nomember google "" "person=custom&email=$(enc "$INV_EMAIL")&verified=true"
ticket_json nomember ticket "$tmp/nomember.exchange"
call POST /auth/providers/exchange "$tmp/nomember.exchange" "$tmp/nomember.pt"
expect_error "step 11: a linked identity whose account has no membership" 403 no_membership

# already_member: INV invited again (SEED's company refuses only its own members), the membership put back by one SQL statement, and the
# invitation accepted with another Google identity of INV's address.
invite E2E_INV_EMAIL again
expect_eq "step 11: INV's membership is back" \
  "$(psql_value "WITH made AS (INSERT INTO \"Memberships\" (\"UserId\", \"CompanyId\", \"RoleId\", \"JoinedAt\") SELECT '$INV_SUB'::uuid, \"CompanyId\", \"Id\", now() FROM \"CompanyRoles\" WHERE \"CompanyId\" = '$DEV_ORG' AND \"Name\" = 'user' RETURNING 1) SELECT count(*) FROM made")" "1"
round_trip again google "?invite=$(cat "$tmp/again.invite")" "person=custom&email=$(enc "$INV_EMAIL")&verified=true&subject=again-$run"
ticket_json again ticket "$tmp/again.exchange"
call POST /auth/providers/exchange "$tmp/again.exchange" "$tmp/again.pt"
expect_error "step 11: an invitation of a company member's address" 409 already_member
pass "step 11: provider_already_linked, invalid_token (a cancelled invitation), private_account_exists, no_membership and already_member"

# --- Step 12: the audit rows and no secret (criteria 28, 33) -------------------------------------------------------------------
[[ -s "$tmp/secrets" ]] || fail "step 12: no secret was collected"
since="occurred_at >= '$START_TS'"
expect_eq "step 12: login.succeeded with details.provider (finish, button, link, button, the moved address, invitation, the micro-sign finish)" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.succeeded' AND details ? 'provider' AND $since")" "7"
expect_eq "step 12: login.failed of the wrong password" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.failed' AND subject_email = '$PW_EMAIL' AND details->>'reason' = 'wrong_password' AND details->>'provider' = 'microsoft' AND $since")" "1"
expect_eq "step 12: provider.linked" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'provider.linked' AND subject_user_id = '$PW_SUB' AND actor_user_id = '$PW_SUB' AND details->>'provider' = 'microsoft'")" "1"
expect_eq "step 12: signup.completed with the provider" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.completed' AND subject_user_id = '$NEW_SUB' AND details->>'kind' = 'private' AND details->>'provider' = 'google'")" "1"
expect_eq "step 12: invite.accepted with the provider" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'invite.accepted' AND subject_email = '$INV_EMAIL' AND org_id = '$DEV_ORG' AND details->>'provider' = 'google' AND details->>'role' = 'user'")" "1"
expect_eq "step 12: account.delete_refused (the sign-in ticket, the missing auth_pt)" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'account.delete_refused' AND subject_user_id = '$NEW_SUB' AND details->>'reason' = 'reauth_failed'")" "2"
expect_eq "step 12: provider.reauthenticated" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'provider.reauthenticated' AND subject_user_id = '$NEW_SUB' AND actor_user_id = '$NEW_SUB' AND details->>'provider' = 'google'")" "1"
expect_eq "step 12: account.deleted" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'account.deleted' AND subject_user_id = '$NEW_SUB' AND details->>'reason' = 'self'")" "1"
COUNT="$(grep -c '' "$tmp/secrets")"
[[ "$COUNT" -ge 60 ]] || fail "step 12: only $COUNT secrets were collected"
# The control: the search finds a value that the audit log does hold (PW's address), so that a search that cannot match is not read as clean.
printf '%s\n' "$PW_EMAIL" > "$tmp/control"
[[ "$(secrets_in_audit "$tmp/control")" -ge 1 ]] || fail "step 12: the search of the audit log does not find a value it holds"
expect_eq "step 12: rows of the audit log that hold a secret of this run" "$(secrets_in_audit "$tmp/secrets")" "0"
pass "step 12: the audit rows of the run hold the provider; none of the $COUNT secrets of the run (states, codes, cookies, tickets, tokens, passwords) is in the audit log"

echo "ALL PASS"
