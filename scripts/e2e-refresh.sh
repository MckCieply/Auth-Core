#!/usr/bin/env bash
# Real-network end-to-end check of spec 0002 (refresh + logout) against the compose stack.
#
# Full sequence, from the repo root (same stack and .env as scripts/e2e-login.sh):
#   cp .env.example .env                  # then set real local values (git-ignored)
#   scripts/dev-keys.sh                   # dev signing/encryption keys into .secrets/ (git-ignored)
#   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, never "auth-core" (the development stack): a bare down -v would wipe it; the scripts default to this name and refuse "auth-core"
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build    # start postgres + auth
#                                         # the per-IP limiter is off for the older checks (spec 0008): the lockout script makes about 58 logins a minute and the mail script exactly 10 mail requests; scripts/e2e-hardening.sh runs last on the stack recreated with the defaults
#   scripts/e2e-login.sh                  # spec 0001 regression
#   scripts/e2e-refresh.sh                # this script (does NOT bring the stack up or down; it restarts auth once)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# Reads AUTH_DEV_SEED_EMAIL / AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced).
# Needs: curl (7.55+ for -H @file), python3 with PyJWT[crypto], docker compose. Env: BASE_URL (default http://localhost:8080).
# Takes about 30 s: it waits out the 15 s reuse grace window once.
# Exits non-zero on the first failure; prints "PASS <step>" per step; never prints a token, a cookie or a password.
#
# The refresh cookie is `Secure` and the stack speaks plain HTTP, so a client would not send it back: this script
# carries it by hand. Each cookie value goes into a header file (a mktemp -d directory, removed on exit) that curl
# sends with -H @file, so no refresh token ever appears on a command line.
# The absolute 30-day cap (criterion 9) cannot be waited out over HTTP; integration tests cover it (spec Decision 10).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
ISSUER="${ISSUER:-http://localhost:8080/auth}"
AUDIENCE="${AUDIENCE:-auth-core-dev}"
JWKS_URL="$BASE_URL/auth/.well-known/jwks.json"
GRACE_WAIT=16 # the reuse grace window is 15 s (spec Decision 2)
INVALID_GRANT='{"error":"invalid_grant"}'
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
EMAIL="$(env_get AUTH_DEV_SEED_EMAIL)"
PASSWORD="$(env_get AUTH_DEV_SEED_PASSWORD)"
[[ -n "$EMAIL" && -n "$PASSWORD" ]] || fail "AUTH_DEV_SEED_EMAIL / AUTH_DEV_SEED_PASSWORD not set in .env"
export E2E_EMAIL="$EMAIL" E2E_PASSWORD="$PASSWORD"

json_get() { python3 -c 'import json,sys; print(json.load(sys.stdin)[sys.argv[1]])' "$1"; }

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

# login  ->  sets HTTP_CODE, BODY; response headers in $tmp/hdr. The credentials travel on stdin, not the command line.
login() {
  HTTP_CODE="$(python3 -c 'import json,os; print(json.dumps({"email": os.environ["E2E_EMAIL"], "password": os.environ["E2E_PASSWORD"]}))' \
    | curl -sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' -X POST "$BASE_URL/auth/login" \
        -H 'Content-Type: application/json' --data-binary @-)"
  BODY="$(cat "$tmp/body")"
}

# post <path> [cookie-value]  ->  sets HTTP_CODE, BODY; response headers in $tmp/hdr.
# The cookie value goes into a header file, never onto the command line. No cookie argument = no Cookie header.
post() {
  local path="$1" args=()
  if [[ $# -ge 2 ]]; then
    printf 'Cookie: auth_rt=%s\n' "$2" > "$tmp/cookie"
    args=(-H "@$tmp/cookie")
  fi
  HTTP_CODE="$(curl -sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code}' -X POST \
    ${args[@]+"${args[@]}"} "$BASE_URL$path")"
  BODY="$(cat "$tmp/body")"
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

expect_invalid_grant() { # expect_invalid_grant <what>: the last response is the uniform refresh failure
  [[ "$HTTP_CODE" == "401" ]] || fail "$1: HTTP $HTTP_CODE, expected 401"
  [[ "$BODY" == "$INVALID_GRANT" ]] || fail "$1: body is not exactly $INVALID_GRANT"
}

# --- Step 1: health; login sets the refresh cookie with the ADR 0004 attributes -----------------------------
wait_healthy || fail "step 1: $BASE_URL/auth/health did not return 200 within 90s"
pass "health: /auth/health returned 200"

login
[[ "$HTTP_CODE" == "200" ]] || fail "step 1: login returned HTTP $HTTP_CODE, expected 200"
LINE="$(set_cookie_line)"
[[ -n "$LINE" ]] || fail "step 1: login set no auth_rt cookie"
for attr in HttpOnly Secure SameSite=Strict Path=/auth; do
  has_attr "$LINE" "$attr" || fail "step 1: the auth_rt cookie lacks the $attr attribute"
done
RT0="$(cookie_value)"
[[ -n "$RT0" ]] || fail "step 1: the auth_rt cookie is empty"
[[ "$BODY" != *"$RT0"* ]] || fail "step 1: the login body carries the refresh token"
pass "step 1: login -> 200; auth_rt cookie is HttpOnly, Secure, SameSite=Strict, Path=/auth; not in the body"

# --- Step 2: refresh rotates the cookie and returns a verifiable access token -------------------------------
post /auth/refresh "$RT0"
[[ "$HTTP_CODE" == "200" ]] || fail "step 2: refresh returned HTTP $HTTP_CODE, expected 200"
printf '%s' "$BODY" | python3 -c 'import json,sys; sys.exit(0 if list(json.load(sys.stdin)) == ["access_token"] else 1)' \
  || fail "step 2: refresh body keys are not exactly [access_token]"
TOKEN="$(printf '%s' "$BODY" | json_get access_token)"
python3 "$root/scripts/verify_jwt.py" "$TOKEN" "$JWKS_URL" "$ISSUER" "$AUDIENCE" >/dev/null \
  || fail "step 2: PyJWT could not verify the refreshed access token against the JWKS"
RT1="$(cookie_value)"
[[ -n "$RT1" ]] || fail "step 2: refresh did not set a new auth_rt cookie"
[[ "$RT1" != "$RT0" ]] || fail "step 2: the refresh cookie was not rotated"
[[ "$BODY" != *"$RT1"* ]] || fail "step 2: the refresh body carries the new refresh token"
has_attr "$(set_cookie_line)" "HttpOnly" && has_attr "$(set_cookie_line)" "Secure" \
  && has_attr "$(set_cookie_line)" "SameSite=Strict" && has_attr "$(set_cookie_line)" "Path=/auth" \
  || fail "step 2: the rotated cookie lost an attribute"
pass "step 2: refresh -> 200 {access_token} only; PyJWT verifies it; cookie rotated, not in the body"

# --- Step 3: the token store survives a genuine process restart ---------------------------------------------
"${compose[@]}" restart auth >/dev/null 2>&1 || fail "step 3: docker compose restart auth failed"
wait_healthy || fail "step 3: auth did not become healthy after restart"
post /auth/refresh "$RT1"
[[ "$HTTP_CODE" == "200" ]] || fail "step 3: refresh with the pre-restart cookie returned HTTP $HTTP_CODE, expected 200"
RT2="$(cookie_value)"
[[ -n "$RT2" && "$RT2" != "$RT1" ]] || fail "step 3: the post-restart refresh did not rotate the cookie"
pass "step 3: restart auth -> refresh with the pre-restart cookie -> 200 and rotated"

# --- Step 4: reuse outside the grace window is rejected and revokes the whole family ------------------------
sleep "$GRACE_WAIT"
post /auth/refresh "$RT1"
expect_invalid_grant "step 4: replay of the consumed token after the grace window"
post /auth/refresh "$RT2"
expect_invalid_grant "step 4: the current token after reuse detection (family revoked)"
pass "step 4: consumed token after ${GRACE_WAIT}s -> 401 invalid_grant; the current token of that family -> the same 401"

# --- Step 5: missing or junk cookie -> the same uniform 401 -------------------------------------------------
post /auth/refresh
expect_invalid_grant "step 5: refresh with no cookie"
post /auth/refresh "junk-$RANDOM-not-a-token"
expect_invalid_grant "step 5: refresh with a junk cookie"
pass "step 5: no cookie and junk cookie -> the same 401 invalid_grant"

# --- Step 6: logout ends the session; without a valid cookie it is still 204 --------------------------------
login
[[ "$HTTP_CODE" == "200" ]] || fail "step 6: login returned HTTP $HTTP_CODE, expected 200"
RT="$(cookie_value)"
[[ -n "$RT" ]] || fail "step 6: login set no auth_rt cookie"
post /auth/logout "$RT"
[[ "$HTTP_CODE" == "204" ]] || fail "step 6: logout returned HTTP $HTTP_CODE, expected 204"
LINE="$(set_cookie_line)"
[[ -n "$LINE" ]] || fail "step 6: logout did not touch the auth_rt cookie"
printf '%s' "$LINE" | grep -qi '^set-cookie: auth_rt=;' || fail "step 6: logout did not clear the auth_rt value"
has_attr "$LINE" "Max-Age=0" || fail "step 6: the clearing cookie lacks Max-Age=0"
pass "step 6: logout -> 204 with Set-Cookie auth_rt=; Max-Age=0"

post /auth/refresh "$RT"
expect_invalid_grant "step 6: refresh with a logged-out token"
pass "step 6: refresh with the logged-out token -> 401 invalid_grant"

post /auth/logout
[[ "$HTTP_CODE" == "204" ]] || fail "step 6: logout with no cookie returned HTTP $HTTP_CODE, expected 204"
pass "step 6: logout with no cookie -> 204"

# --- Step 7: regression, a fresh login still works ----------------------------------------------------------
login
[[ "$HTTP_CODE" == "200" ]] || fail "step 7: login returned HTTP $HTTP_CODE, expected 200"
TOKEN="$(printf '%s' "$BODY" | json_get access_token)"
python3 "$root/scripts/verify_jwt.py" "$TOKEN" "$JWKS_URL" "$ISSUER" "$AUDIENCE" >/dev/null \
  || fail "step 7: the fresh login's token does not verify"
pass "step 7: fresh login -> 200 and PyJWT verifies it"

echo "ALL PASS"
