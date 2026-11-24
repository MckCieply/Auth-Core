#!/usr/bin/env bash
# Real-network end-to-end check of spec 0012 (GitHub, and managing one's own logins) against the web stack with the fake provider: Auth-Core,
# PostgreSQL, the mail catcher (Mailpit), the notes service, Caddy (https://localhost:8443) and the fake (http://localhost:8099), which serves
# Google, Microsoft and, under /github, GitHub with its minimal discovery document (tests/Auth.FakeOidc/compose.yml, with GitHub's keys and
# Auth__Testing__WindowEndpoint "true"). The script plays the browser with curl, as scripts/e2e-providers.sh does; the confirmation window is
# the real one (5 minutes), and the testing endpoint closes it where a step needs it closed.
#
# What it checks, in order (NEW = a person who signs up with GitHub and has no password; OTHER = a person with a Microsoft account; PW = a person
# with a password account; SW = a person with a password account who makes a company and switches into it; ORG = a person who makes a
# company with GitHub). The guarding tests are in docs/superpowers/plans/0012-acceptance-map.md.
#   1. health is Healthy; GET /auth/options offers google, microsoft and github (org_creation self-service); the description names the four settings endpoints and not the
#      testing endpoint - criteria 1, 26.
#   2. NEW's GitHub round trip by the fake's non-interactive mode: the start asks for user:email with S256 and no nonce; the fragment carries a
#      ticket for sign_in; the fake saw the token revoked once (its inspection endpoint) - criteria 2, 3, 32.
#   3. NEW finishes privately and is signed in; /auth/me says confirmed_until; NEW connects Google at once, without being asked to confirm;
#      /auth/me lists github and google - criteria 8, 12, 23.
#   4. the connect's refusals: a sign_in ticket -> 400 invalid_ticket; a second Google identity -> 409 provider_in_use; OTHER's Microsoft
#      identity -> 409 provider_already_linked; another address -> 403 address_mismatch; an unverified one -> 403 email_not_verified; none ->
#      403 no_email; no refresh cookie -> 401 session_required; the window ended by the testing endpoint -> 403 confirmation_required -
#      criteria 13, 24.
#   5. NEW confirms: a password -> 403 reauth_required; a sign_in ticket -> 403 reauth_failed; a reauth ticket of GitHub -> 204, the window
#      open again - criteria 10, 11.
#   6. NEW disconnects: an unknown name -> 404 not_found; GitHub -> 200 with a renewed session, the old refresh cookie -> 401 invalid_grant;
#      the last login (Google) -> 409 last_login; a password change -> 403 password_not_set - criteria 16, 17, 19.
#   7. NEW sets a first password through "forgot password" and the reset mail, and signs in with it - criterion 20.
#   8. PW, with a password: a ticket -> 403 password_required; a wrong password -> 403 wrong_password; the right one -> 204; a weak new password
#      -> 400 weak_password; the password changed -> 200 with a renewed session; the new password signs in, the old one does not - criteria
#      10, 11, 18, 19.
#  8b. SW makes a company, switches into it and changes the password: the renewed session is still in that company, with the same window
#      (specs 0015 and 0012, Amendment B, owner decision F2).
#   9. ORG makes a company with GitHub and deletes it with "Confirm with GitHub": a reauth ticket -> 204, auth_pt cleared; the company was
#      ORG's only space, so the account goes with it (spec 0015): the old token is 403 permissions_changed and the account row is gone
#      - criterion 21.
#  10. the mails, read from the mail catcher: "Google was connected", "GitHub was disconnected" (NEW) and "the password was changed" (PW, SW),
#      in the stack's language, with no link; the notice limit of NEW's address counted two - criterion 22.
#  11. the audit rows of the run, read from the database, and no row holds a secret of the run - criteria 25, 7.
#
# Full sequence, from the repo root (the stack of scripts/e2e-providers.sh; the per-IP limiter is OFF):
#   cp .env.example .env                  # then set real local values (git-ignored); NOTES_DB_PASSWORD too
#   MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh        # signing/encryption keys into .secrets/ (git-ignored)
#   export COMPOSE_PROJECT_NAME=auth-core-settings        # a project of its own, never "auth-core" (the development stack)
#   files="-f deploy/docker-compose.yml -f samples/notes-api/compose.yml -f samples/notes-web/compose.yml -f tests/Auth.FakeOidc/compose.yml"
#   docker compose $files --env-file .env down -v                                   # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose $files --env-file .env up -d --build
#   scripts/e2e-settings.sh               # this script (does NOT bring the stack up or down)
#   docker compose $files --env-file .env down -v                                   # tear down
#
# Needs: curl, python3 (standard library only; E2E_PYTHON=<interpreter> names another), docker compose, and host ports 8088, 8443, 8080, 8025
# and 8099 free. Env: BASE_URL (default https://localhost:8443), FAKE_URL (default http://localhost:8099), MAILPIT_URL (default
# http://localhost:8025). Takes about a minute. Re-runnable on the same stack. Exits non-zero on the first failure; prints "PASS <step>" per
# step; never prints a password, a token, a ticket, a cookie, a state, a code or a mail body.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-https://localhost:8443}"
FAKE_URL="${FAKE_URL:-http://localhost:8099}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
RETURN_URL="$BASE_URL/provider-return"
PY="${E2E_PYTHON:-python3}"
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-settings}"
[[ "$COMPOSE_PROJECT_NAME" != "auth-core" ]] || { echo "FAIL COMPOSE_PROJECT_NAME=auth-core is the development stack; use another name" >&2; exit 1; }
compose=(docker compose -f "$root/deploy/docker-compose.yml" -f "$root/samples/notes-api/compose.yml" -f "$root/samples/notes-web/compose.yml"
  -f "$root/tests/Auth.FakeOidc/compose.yml" --env-file "$root/.env")
SIGNUP_LINK="http://localhost:8088/complete-signup"
RESET_LINK="http://localhost:8088/reset"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
: > "$tmp/secrets"

fail() { echo "FAIL $*" >&2; exit 1; }
pass() { echo "PASS $*"; }

keep_secret() {
  if [[ -n "$1" ]]; then printf '%s\n' "$1" >> "$tmp/secrets"; fi
}

[[ -f "$root/.env" ]] || fail "missing $root/.env (copy .env.example and set local values)"

run="$RANDOM$RANDOM"
NEW_EMAIL="new-$run@github.test"
OTHER_EMAIL="other-$run@outlook.test"
STRANGER_EMAIL="stranger-$run@outlook.test"
PW_EMAIL="pw-$run@poczta.test"
SW_EMAIL="sw-$run@poczta.test"
ORG_EMAIL="org-$run@github.test"
ORG_NAME="Firma $run"
OTHER_OID="$("$PY" -c 'import uuid; print(uuid.uuid4())' | tr -d '\r')"
export E2E_PW_EMAIL="$PW_EMAIL" E2E_NEW_EMAIL="$NEW_EMAIL" E2E_SW_EMAIL="$SW_EMAIL" E2E_ORG_NAME="$ORG_NAME"
export E2E_PASSWORD="E2e-Passw0rd-$run" E2E_WRONG_PASSWORD="E2e-Wrong-Passw0rd-$run" E2E_NEW_PASSWORD="E2e-Fresh-Passw0rd-$run"
export E2E_FIRST_PASSWORD="E2e-First-Passw0rd-$run" E2E_SW_NEW_PASSWORD="E2e-Switch-Passw0rd-$run"
for secret in "$E2E_PASSWORD" "$E2E_WRONG_PASSWORD" "$E2E_NEW_PASSWORD" "$E2E_FIRST_PASSWORD" "$E2E_SW_NEW_PASSWORD"; do keep_secret "$secret"; done

# --- helpers copied from scripts/e2e-providers.sh -----------------------------------------------------------------------------
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

# --- helpers of the settings screen -------------------------------------------------------------------------------------------

# keep_session <who> [loose]: after a login's answer, keeps the bearer header in $tmp/<who>.auth and the refresh cookie's pair in $tmp/<who>.rt.
# The body must be exactly a login's (access_token, status) unless "loose" is given (the switch's answer is checked by its caller).
keep_session() {
  local line
  if [[ "${2:-}" != "loose" ]]; then
    expect_eq "$1: the body is a login's" "$(val 'sorted(d.keys())')" "['access_token', 'status']"
  fi
  printf 'Authorization: Bearer %s\n' "$(val 'd["access_token"]')" > "$tmp/$1.auth"
  keep_secret "$(val 'd["access_token"]')"
  line="$(set_cookie_line auth_rt)"
  [[ -n "$line" ]] || fail "$1: no auth_rt cookie"
  pair_of "$line" > "$tmp/$1.rt"
  keep_secret "$(cookie_value auth_rt)"
}

# settings <method> <path> <body-file|""> <who> [round-trip-name] [norefresh]: a settings call with <who>'s bearer token and one Cookie header
# holding <who>'s auth_rt (left out with "norefresh") and, with a round trip's name, that browser's auth_pt.
settings() {
  local cookie=""
  if [[ "${6:-}" != "norefresh" ]]; then cookie="$(cat "$tmp/$4.rt")"; fi
  if [[ -n "${5:-}" ]]; then cookie="${cookie:+$cookie; }$(sed 's/^Cookie: //' "$tmp/$5.pt" | tr -d '\r')"; fi
  if [[ -n "$cookie" ]]; then
    printf 'Cookie: %s\n' "$cookie" > "$tmp/cookie.hdr"
    call "$1" "$2" "$3" "$tmp/$4.auth" "$tmp/cookie.hdr"
  else
    call "$1" "$2" "$3" "$tmp/$4.auth"
  fi
}

confirmed_until() { # confirmed_until <who>: confirmed_until of GET /auth/me with <who>'s token and cookie ("None" when null)
  settings GET /auth/me "" "$1"
  expect_status "$1: GET /auth/me" 200
  val 'd["confirmed_until"]'
}

revocations() { # the number of revocations the fake has recorded, and the status of the last ("<n> <status>")
  curl -sS --max-time 10 "$FAKE_URL/_fake/github/revocations" -o "$tmp/revocations.json"
  "$PY" -c 'import json,sys; r=json.load(sys.stdin.buffer)["revocations"]; print(len(r), r[-1]["status"] if r else "-")' < "$tmp/revocations.json" | tr -d '\r'
}

# mail_without_link <address> <subject-start>: the newest such mail to the address names no link and no token (its text and HTML parts)
mail_without_link() {
  wait_mail "$1" "$2" 30 || fail "no mail '$2' for $1 within 30s"
  "$PY" -c '
import json, sys
mail = json.load(sys.stdin.buffer)
text = (mail.get("Text") or "") + (mail.get("HTML") or "")
if "http" in text.lower() or "token" in text.lower() or "<a " in text.lower():
    sys.exit("the mail holds a link")
' < "$tmp/mail.json" || fail "the mail '$2' to $1 holds a link"
}

notice_count() { # notice_count <address>: WindowCount of the notice limit of the address (kind 100), 0 when it has none
  local value
  value="$(psql_value "SELECT coalesce((SELECT \"WindowCount\" FROM \"MailRequestLimits\" WHERE \"Kind\" = 100 AND \"IdentifierHash\" = sha256(convert_to(upper('$1'), 'UTF8'))), 0)")"
  printf '%s' "$value"
}

# --- Step 1: health, the options, the description (criteria 1, 26) ------------------------------------------------------------
wait_ok "$BASE_URL/auth/health" 120 || fail "step 1: $BASE_URL/auth/health did not return 200 within 120s (is the stack up? see the header)"
wait_ok "$FAKE_URL/github/.well-known/openid-configuration" 60 || fail "step 1: the fake's GitHub ($FAKE_URL/github) did not answer within 60s"
wait_ok "$MAILPIT_URL/readyz" 60 || fail "step 1: $MAILPIT_URL/readyz did not return 200 within 60s"
START_TS="$(psql_value "SELECT now()")"
call GET /auth/health
expect_status "step 1: health" 200 "Healthy"
call GET /auth/options
expect_status "step 1: the options" 200 '{"signup":"self-service","private_signup":"self-service","providers":["google","microsoft","github"],"org_creation":"self-service"}'
call GET /auth/openapi/v1.json
expect_status "step 1: the OpenAPI description" 200
expect_eq "step 1: the settings endpoints are described" \
  "$(val "sorted(p for p in d['paths'] if p in ('/auth/account/confirm', '/auth/providers/connect', '/auth/account/logins/unlink', '/auth/account/password'))")" \
  "['/auth/account/confirm', '/auth/account/logins/unlink', '/auth/account/password', '/auth/providers/connect']"
expect_eq "step 1: the testing endpoint is not" "$(val "[p for p in d['paths'] if p.startswith('/auth/testing')]")" "[]"
pass "step 1: health is Healthy; the options offer the three providers; the description names the four settings endpoints and not the testing endpoint"

# --- Step 2: NEW's GitHub round trip and the revocation (criteria 2, 3, 32) ---------------------------------------------------
BEFORE="$(revocations)"
round_trip new github "" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
expect_eq "step 2: the scope" "$(url_param "$tmp/new.start" query scope)" "user:email"
expect_eq "step 2: PKCE" "$(url_param "$tmp/new.start" query code_challenge_method)" "S256"
expect_eq "step 2: no nonce" "$(url_param "$tmp/new.start" query nonce)" ""
expect_eq "step 2: no max_age" "$(url_param "$tmp/new.start" query max_age)" ""
expect_eq "step 2: the fragment" "$(fragment_of new)" '{"purpose":"sign_in","ticket":43}'
AFTER="$(revocations)"
expect_eq "step 2: the fake saw one revocation more, answered 204" "$AFTER" "$(( ${BEFORE%% *} + 1 )) 204"
pass "step 2: GitHub's round trip asks for user:email with S256 and no nonce; the ticket is for sign_in; the token was revoked once"

# --- Step 3: NEW signs up with GitHub and connects Google at once (criteria 8, 12, 23) -----------------------------------------
ticket_json new ticket "$tmp/new.finish" "{'kind': 'private'}"
call POST /auth/providers/finish "$tmp/new.finish" "$tmp/new.pt"
expect_status "step 3: the finish" 200
keep_session new
[[ "$(confirmed_until new)" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]] || fail "step 3: confirmed_until is not a UTC time with seconds"
round_trip gconnect google "?purpose=connect" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
expect_eq "step 3: the connect fragment" "$(fragment_of gconnect)" '{"purpose":"connect","ticket":43}'
ticket_json gconnect ticket "$tmp/gconnect.body"
settings POST /auth/providers/connect "$tmp/gconnect.body" new gconnect
expect_status "step 3: the connect" 204 ""
expect_attrs "step 3: the cleared auth_pt" "$(set_cookie_line auth_pt)" "Max-Age=0"
settings GET /auth/me "" new
expect_eq "step 3: linked_providers" "$(val 'd["linked_providers"]')" "['github', 'google']"
expect_eq "step 3: has_password" "$(val 'd["has_password"]')" "False"
NEW_SUB="$(val 'd["sub"]')"
check_guid "NEW's sub" "$NEW_SUB"
pass "step 3: the GitHub finish signs NEW in with an open window; Google connects without a confirmation; /auth/me lists github and google"

# --- Step 4: the connect's refusals (criteria 13, 24) ------------------------------------------------------------------------
round_trip signin google "" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
ticket_json signin ticket "$tmp/signin.body"
settings POST /auth/providers/connect "$tmp/signin.body" new signin
expect_error "step 4: a sign_in ticket" 400 invalid_ticket
round_trip second google "?purpose=connect" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true&subject=second-$run"
ticket_json second ticket "$tmp/second.body"
settings POST /auth/providers/connect "$tmp/second.body" new second
expect_error "step 4: a second Google identity" 409 provider_in_use
round_trip other microsoft "" "person=custom&email=$(enc "$OTHER_EMAIL")&verified=true&subject=$OTHER_OID"
ticket_json other ticket "$tmp/other.finish" "{'kind': 'private'}"
call POST /auth/providers/finish "$tmp/other.finish" "$tmp/other.pt"
expect_status "step 4: OTHER's finish" 200
keep_session other
round_trip taken microsoft "?purpose=connect" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true&subject=$OTHER_OID"
ticket_json taken ticket "$tmp/taken.body"
settings POST /auth/providers/connect "$tmp/taken.body" new taken
expect_error "step 4: OTHER's Microsoft identity" 409 provider_already_linked
round_trip stranger microsoft "?purpose=connect" "person=custom&email=$(enc "$STRANGER_EMAIL")&verified=true"
ticket_json stranger ticket "$tmp/stranger.body"
settings POST /auth/providers/connect "$tmp/stranger.body" new stranger
expect_error "step 4: another address" 403 address_mismatch
round_trip unverified microsoft "?purpose=connect" "person=custom&email=$(enc "$NEW_EMAIL")&verified=false"
ticket_json unverified ticket "$tmp/unverified.body"
settings POST /auth/providers/connect "$tmp/unverified.body" new unverified
expect_error "step 4: an unverified address" 403 email_not_verified
round_trip noemail microsoft "?purpose=connect" "person=microsoft-personal&defect=no_email"
ticket_json noemail ticket "$tmp/noemail.body"
settings POST /auth/providers/connect "$tmp/noemail.body" new noemail
expect_error "step 4: no address" 403 no_email
round_trip later microsoft "?purpose=connect" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
ticket_json later ticket "$tmp/later.body"
settings POST /auth/providers/connect "$tmp/later.body" new later norefresh
expect_error "step 4: no refresh cookie" 401 session_required
settings POST /auth/testing/end-confirmation "" new
expect_status "step 4: the testing endpoint ends the window" 204 ""
expect_eq "step 4: the window is closed" "$(confirmed_until new)" "None"
settings POST /auth/providers/connect "$tmp/later.body" new later
expect_error "step 4: a closed window" 403 confirmation_required
pass "step 4: invalid_ticket, provider_in_use, provider_already_linked, address_mismatch, email_not_verified, no_email, session_required, confirmation_required"

# --- Step 5: NEW confirms (criteria 10, 11) ---------------------------------------------------------------------------------
json_body "$tmp/new.password" "{'password': os.environ['E2E_PASSWORD']}"
settings POST /auth/account/confirm "$tmp/new.password" new
expect_error "step 5: a password for an account without one" 403 reauth_required
ticket_json signin provider_ticket "$tmp/signin.confirm"
settings POST /auth/account/confirm "$tmp/signin.confirm" new signin
expect_error "step 5: a sign_in ticket" 403 reauth_failed
round_trip reauth github "?purpose=reauth" "person=custom&email=$(enc "$NEW_EMAIL")&verified=true"
ticket_json reauth provider_ticket "$tmp/reauth.confirm"
settings POST /auth/account/confirm "$tmp/reauth.confirm" new reauth
expect_status "step 5: the GitHub confirmation" 204 ""
expect_attrs "step 5: the cleared auth_pt" "$(set_cookie_line auth_pt)" "Max-Age=0"
[[ "$(confirmed_until new)" != "None" ]] || fail "step 5: the window did not open"
pass "step 5: a password -> reauth_required; a sign_in ticket -> reauth_failed; GitHub's reauth ticket opens the window"

# --- Step 6: NEW disconnects (criteria 16, 17, 19) ---------------------------------------------------------------------------
json_body "$tmp/unlink.gitlab" "{'provider': 'gitlab'}"
settings POST /auth/account/logins/unlink "$tmp/unlink.gitlab" new
expect_error "step 6: an unknown name" 404 not_found
cp "$tmp/new.rt" "$tmp/old.rt"
json_body "$tmp/unlink.github" "{'provider': 'github'}"
settings POST /auth/account/logins/unlink "$tmp/unlink.github" new
expect_status "step 6: the unlink" 200
keep_session new
printf 'Cookie: %s\n' "$(cat "$tmp/old.rt")" > "$tmp/old.cookie"
call POST /auth/refresh "" "$tmp/old.cookie"
expect_status "step 6: the refresh cookie the unlink replaced" 401
expect_eq "step 6: invalid_grant" "$(val 'd["error"]')" "invalid_grant"
settings GET /auth/me "" new
expect_eq "step 6: linked_providers" "$(val 'd["linked_providers"]')" "['google']"
[[ "$(val 'd["confirmed_until"]')" != "None" ]] || fail "step 6: the renewed session lost the window"
json_body "$tmp/unlink.google" "{'provider': 'google'}"
settings POST /auth/account/logins/unlink "$tmp/unlink.google" new
expect_error "step 6: the last way in" 409 last_login
json_body "$tmp/new.change" "{'current_password': os.environ['E2E_PASSWORD'], 'new_password': os.environ['E2E_NEW_PASSWORD']}"
settings POST /auth/account/password "$tmp/new.change" new
expect_error "step 6: a password change without a password" 403 password_not_set
pass "step 6: not_found; GitHub disconnected with a renewed session (the old cookie is dead, the window kept); last_login; password_not_set"

# --- Step 7: NEW's first password through "forgot password" (criterion 20) ------------------------------------------------------
BEFORE_MAILS="$(mail_count "$NEW_EMAIL")"
json_body "$tmp/new.forgot" "{'email': os.environ['E2E_NEW_EMAIL']}"
call POST /auth/password/forgot "$tmp/new.forgot"
expect_status "step 7: forgot password" 202 ""
wait_mail "$NEW_EMAIL" "Reset your" 90 "$BEFORE_MAILS" || fail "step 7: no reset mail for NEW within 90s"
token_body "$RESET_LINK" "$tmp/new.reset.token" || fail "step 7: no usable reset link in NEW's mail"
keep_secret "$(token_of "$tmp/new.reset.token")"
"$PY" -c 'import json,os,sys; b=json.load(open(sys.argv[1])); b["new_password"]=os.environ["E2E_FIRST_PASSWORD"]; print(json.dumps(b))' \
  "$tmp/new.reset.token" > "$tmp/new.reset"
call POST /auth/password/reset "$tmp/new.reset"
expect_status "step 7: the reset" 204 ""
json_body "$tmp/new.login" "{'email': os.environ['E2E_NEW_EMAIL'], 'password': os.environ['E2E_FIRST_PASSWORD']}"
call POST /auth/login "$tmp/new.login"
expect_status "step 7: NEW signs in with the first password" 200
keep_session new
pass "step 7: a first password through the reset mail; NEW signs in with it"

# --- Step 8: PW, with a password (criteria 10, 11, 18, 19) ------------------------------------------------------------------
json_body "$tmp/pw.signup" "{'email': os.environ['E2E_PW_EMAIL'], 'kind': 'private'}"
call POST /auth/signup "$tmp/pw.signup"
expect_status "step 8: PW's private sign-up request" 202 ""
wait_mail "$PW_EMAIL" "Complete your sign-up" 90 || fail "step 8: no sign-up mail for PW within 90s"
token_body "$SIGNUP_LINK" "$tmp/pw.complete" E2E_PASSWORD || fail "step 8: no usable sign-up link in PW's mail"
keep_secret "$(token_of "$tmp/pw.complete")"
call POST /auth/signup/complete "$tmp/pw.complete"
expect_status "step 8: PW's completion" 200
keep_session pw
json_body "$tmp/pw.ticket" "{'provider_ticket': 'not-a-ticket'}"
settings POST /auth/account/confirm "$tmp/pw.ticket" pw
expect_error "step 8: a ticket for an account with a password" 403 password_required
json_body "$tmp/pw.wrong" "{'password': os.environ['E2E_WRONG_PASSWORD']}"
settings POST /auth/account/confirm "$tmp/pw.wrong" pw
expect_error "step 8: a wrong password" 403 wrong_password
json_body "$tmp/pw.right" "{'password': os.environ['E2E_PASSWORD']}"
settings POST /auth/account/confirm "$tmp/pw.right" pw
expect_status "step 8: the right password" 204 ""
json_body "$tmp/pw.weak" "{'current_password': os.environ['E2E_PASSWORD'], 'new_password': 'short'}"
settings POST /auth/account/password "$tmp/pw.weak" pw
expect_status "step 8: a weak new password" 400
expect_eq "step 8: weak_password" "$(val 'd["error"]')" "weak_password"
json_body "$tmp/pw.change" "{'current_password': os.environ['E2E_PASSWORD'], 'new_password': os.environ['E2E_NEW_PASSWORD']}"
settings POST /auth/account/password "$tmp/pw.change" pw
expect_status "step 8: the password change" 200
keep_session pw
json_body "$tmp/pw.old" "{'email': os.environ['E2E_PW_EMAIL'], 'password': os.environ['E2E_PASSWORD']}"
call POST /auth/login "$tmp/pw.old"
expect_status "step 8: the old password" 401
expect_eq "step 8: invalid_credentials" "$(val 'd["error"]')" "invalid_credentials"
json_body "$tmp/pw.new" "{'email': os.environ['E2E_PW_EMAIL'], 'password': os.environ['E2E_NEW_PASSWORD']}"
call POST /auth/login "$tmp/pw.new"
expect_status "step 8: the new password" 200
pass "step 8: password_required; wrong_password; the right password confirms; weak_password; the change renews the session; the new password signs in"

# --- Step 8b: a renewed session stays in the company (spec 0015; Amendment B, F2) ------------------------------------------------
json_body "$tmp/sw.signup" "{'email': os.environ['E2E_SW_EMAIL'], 'kind': 'private'}"
call POST /auth/signup "$tmp/sw.signup"
expect_status "step 8b: SW's private sign-up request" 202 ""
wait_mail "$SW_EMAIL" "Complete your sign-up" 90 || fail "step 8b: no sign-up mail for SW within 90s"
token_body "$SIGNUP_LINK" "$tmp/sw.complete" E2E_PASSWORD || fail "step 8b: no usable sign-up link in SW's mail"
keep_secret "$(token_of "$tmp/sw.complete")"
call POST /auth/signup/complete "$tmp/sw.complete"
expect_status "step 8b: SW's completion" 200
keep_session sw
json_body "$tmp/sw.company" "{'name': 'Switch Co'}"
settings POST /auth/orgs "$tmp/sw.company" sw
expect_status "step 8b: SW creates a company" 201
SW_COMPANY="$(val 'd["id"]')"
check_guid "SW's company" "$SW_COMPANY"
export E2E_SW_COMPANY="$SW_COMPANY"
json_body "$tmp/sw.switch" "{'org_id': os.environ['E2E_SW_COMPANY']}"
settings POST /auth/switch "$tmp/sw.switch" sw
expect_status "step 8b: SW switches into the company" 200
expect_eq "step 8b: the switch answer" "$(val 'sorted(d.keys())')" "['access_token']"
keep_session sw loose
settings GET /auth/me "" sw
expect_status "step 8b: GET /auth/me after the switch" 200
expect_eq "step 8b: the token is in the company" "$(val 'd["org_id"]')" "$SW_COMPANY"
W1="$(val 'd["confirmed_until"]')"
[[ "$W1" != "None" ]] || fail "step 8b: the window of SW's session is not open after the sign-up"
json_body "$tmp/sw.change" "{'current_password': os.environ['E2E_PASSWORD'], 'new_password': os.environ['E2E_SW_NEW_PASSWORD']}"
settings POST /auth/account/password "$tmp/sw.change" sw
expect_status "step 8b: SW changes the password" 200
keep_session sw
settings GET /auth/orgs "" sw
expect_status "step 8b: GET /auth/orgs" 200
expect_eq "step 8b: the current entry is the company" "$(val '[o["id"] for o in d["orgs"] if o["current"]]')" "['$SW_COMPANY']"
settings GET /auth/me "" sw
expect_eq "step 8b: org_id after the change" "$(val 'd["org_id"]')" "$SW_COMPANY"
expect_eq "step 8b: the window is the same" "$(val 'd["confirmed_until"]')" "$W1"
# The renewed session's refresh token carries the session's company: a refresh must stay in it.
settings POST /auth/refresh "" sw
expect_status "step 8b: SW refreshes the renewed session" 200
expect_eq "step 8b: the refresh answer" "$(val 'sorted(d.keys())')" "['access_token']"
keep_session sw loose
settings GET /auth/orgs "" sw
expect_eq "step 8b: the current entry after the refresh" "$(val '[o["id"] for o in d["orgs"] if o["current"]]')" "['$SW_COMPANY']"
settings GET /auth/me "" sw
expect_eq "step 8b: org_id after the refresh" "$(val 'd["org_id"]')" "$SW_COMPANY"
pass "step 8b: after a password change in a switched company the renewed session is still in that company, with the same window"

# --- Step 9: ORG deletes its company with GitHub (criterion 21) --------------------------------------------------------------
round_trip org github "" "person=custom&email=$(enc "$ORG_EMAIL")&verified=true"
ticket_json org ticket "$tmp/org.finish" "{'kind': 'organization', 'company_name': os.environ['E2E_ORG_NAME']}"
call POST /auth/providers/finish "$tmp/org.finish" "$tmp/org.pt"
expect_status "step 9: ORG's finish" 200
keep_session org
round_trip orgreauth github "?purpose=reauth" "person=custom&email=$(enc "$ORG_EMAIL")&verified=true"
ticket_json orgreauth provider_ticket "$tmp/org.delete" "{'name': os.environ['E2E_ORG_NAME']}"
call DELETE /auth/org "$tmp/org.delete" "$tmp/org.auth" "$tmp/orgreauth.pt"
expect_status "step 9: the company deletion" 204 ""
expect_attrs "step 9: the cleared auth_pt" "$(set_cookie_line auth_pt)" "Max-Age=0"
expect_eq "step 9: the company is gone" "$(psql_value "SELECT count(*) FROM \"Companies\" WHERE \"Name\" = '$ORG_NAME'")" "0"
# Spec 0015: the company was ORG's only space, so the account went with it.
call GET /auth/me "" "$tmp/org.auth"
expect_error "step 9: the old token of the deleted account" 403 permissions_changed
expect_eq "step 9: the account row is gone" "$(psql_value "SELECT count(*) FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" = upper('$ORG_EMAIL')")" "0"
pass "step 9: an admin without a password deletes the company with GitHub's reauth ticket; the account, whose only space it was, goes with it"

# --- Step 10: the notice mails of the settings changes (criterion 22) ---------------------------------------------------------------------------------
mail_without_link "$NEW_EMAIL" "Google was connected"
mail_without_link "$NEW_EMAIL" "GitHub was disconnected"
mail_without_link "$PW_EMAIL" "The password of your"
mail_without_link "$SW_EMAIL" "The password of your"
expect_eq "step 10: the notices NEW's address counted" "$(notice_count "$NEW_EMAIL")" "2"
expect_eq "step 10: the notices PW's address counted" "$(notice_count "$PW_EMAIL")" "1"
expect_eq "step 10: the notices SW's address counted" "$(notice_count "$SW_EMAIL")" "1"
expect_eq "step 10: none for the finishes and the reset" "$(notice_count "$ORG_EMAIL")" "0"
pass "step 10: provider connected, provider disconnected and password changed (twice) were mailed, with no link; the notice limit counted them"

# --- Step 11: the audit rows and no secret (criteria 25, 7) ------------------------------------------------------------------
since="occurred_at >= '$START_TS'"
expect_eq "step 11: account.confirmed (github, password)" \
  "$(psql_value "SELECT string_agg(details->>'method', ',' ORDER BY details->>'method') FROM audit_events WHERE kind = 'account.confirmed' AND $since")" "github,password"
expect_eq "step 11: account.confirm_refused (reason:action)" \
  "$(psql_value "SELECT string_agg((details->>'reason') || ':' || (details->>'action'), ',' ORDER BY (details->>'reason') || ':' || (details->>'action')) FROM audit_events WHERE kind = 'account.confirm_refused' AND $since")" \
  "reauth_failed:confirm,wrong_password:confirm"
expect_eq "step 11: provider.linked via settings" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'provider.linked' AND subject_user_id = '$NEW_SUB' AND details->>'via' = 'settings' AND details->>'provider' = 'google'")" "1"
expect_eq "step 11: provider.unlinked" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'provider.unlinked' AND subject_user_id = '$NEW_SUB' AND details->>'provider' = 'github'")" "1"
expect_eq "step 11: password.changed of PW" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'password.changed' AND subject_email = '$PW_EMAIL' AND $since")" "1"
# 3 = NEW's unlink, PW's change and SW's change (step 8b).
expect_eq "step 11: login.succeeded via session_renewal (the unlink, the two changes)" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'login.succeeded' AND details->>'via' = 'session_renewal' AND $since")" "3"
expect_eq "step 11: password.changed of SW" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'password.changed' AND subject_email = '$SW_EMAIL' AND $since")" "1"
expect_eq "step 11: signup.completed with github (NEW, ORG)" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'signup.completed' AND details->>'provider' = 'github' AND $since")" "2"
expect_eq "step 11: provider.reauthenticated of the company deletion" \
  "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'provider.reauthenticated' AND details->>'provider' = 'github' AND org_name = '$ORG_NAME'")" "1"
expect_eq "step 11: org.deleted" "$(psql_value "SELECT count(*) FROM audit_events WHERE kind = 'org.deleted' AND org_name = '$ORG_NAME'")" "1"
COUNT="$(grep -c '' "$tmp/secrets")"
[[ "$COUNT" -ge 40 ]] || fail "step 11: only $COUNT secrets were collected"
printf '%s\n' "$PW_EMAIL" > "$tmp/control"
[[ "$(secrets_in_audit "$tmp/control")" -ge 1 ]] || fail "step 11: the search of the audit log does not find a value it holds"
expect_eq "step 11: rows of the audit log that hold a secret of this run" "$(secrets_in_audit "$tmp/secrets")" "0"
pass "step 11: the audit rows of the run hold their fields; none of the $COUNT secrets of the run is in the audit log"

echo "ALL PASS"
