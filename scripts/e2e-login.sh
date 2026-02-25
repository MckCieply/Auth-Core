#!/usr/bin/env bash
# Real-network end-to-end check of spec 0001 (login + JWKS + restart persistence) against the compose stack.
#
# Full sequence, from the repo root:
#   cp .env.example .env                  # then set real local values (git-ignored)
#   scripts/dev-keys.sh                   # dev signing/encryption keys into .secrets/ (git-ignored)
#   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, never "auth-core" (the development stack): a bare down -v would wipe it; the scripts default to this name and refuse "auth-core"
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build    # start postgres + auth
#                                         # the per-IP limiter is off for the older checks (spec 0008): the lockout script makes about 58 logins a minute and the mail script exactly 10 mail requests; scripts/e2e-hardening.sh runs last on the stack recreated with the defaults
#   scripts/e2e-login.sh                  # this script (does NOT bring the stack up or down)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# Compose resolves .env relative to deploy/, hence the explicit --env-file (also used by this script's restart).
# Reads AUTH_DEV_SEED_EMAIL / AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced).
# Needs: curl, python3 with PyJWT[crypto], docker compose. Env: BASE_URL (default http://localhost:8080).
# Exits non-zero on the first failure; prints "PASS <step>" per step; never prints tokens or passwords.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
ISSUER="${ISSUER:-http://localhost:8080/auth}"
AUDIENCE="${AUDIENCE:-auth-core-dev}"
JWKS_URL="$BASE_URL/auth/.well-known/jwks.json"
# The stack of these scripts is its own compose project, never "auth-core" (the development stack): the restarts and one-off containers below meet the same stack as the down -v of the header.
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-hardening}"
[[ "$COMPOSE_PROJECT_NAME" != "auth-core" ]] || { echo "FAIL COMPOSE_PROJECT_NAME=auth-core is the development stack; use another name" >&2; exit 1; }
compose=(docker compose -f "$root/deploy/docker-compose.yml" --env-file "$root/.env")

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

login_body() { # JSON login body built from the given env var names; keeps secrets off the command line
  python3 -c 'import json,os,sys; print(json.dumps({"email": os.environ[sys.argv[1]], "password": os.environ[sys.argv[2]]}))' "$1" "$2"
}

# post_json <json-on-stdin>  ->  sets HTTP_CODE and BODY
post_json() {
  local out
  out="$(curl -sS --max-time 20 -w $'\n%{http_code}' -X POST "$BASE_URL/auth/login" \
    -H 'Content-Type: application/json' --data-binary @-)"
  HTTP_CODE="${out##*$'\n'}"
  BODY="${out%$'\n'*}"
}

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

# --- Step 1: health, login 200 + status=authenticated -------------------------------------------------
wait_healthy || fail "step 1: $BASE_URL/auth/health did not return 200 within 90s"
pass "health: /auth/health returned 200"

post_json < <(login_body E2E_EMAIL E2E_PASSWORD)
[[ "$HTTP_CODE" == "200" ]] || fail "step 1: login returned HTTP $HTTP_CODE, expected 200"
[[ "$(printf '%s' "$BODY" | json_get status)" == "authenticated" ]] || fail "step 1: status is not 'authenticated'"
TOKEN="$(printf '%s' "$BODY" | json_get access_token)"
[[ -n "$TOKEN" ]] || fail "step 1: empty access_token"
pass "step 1: login -> 200 status=authenticated with access_token"

# --- Step 2: JWKS has no private members; independent PyJWT verification ------------------------------
JWKS="$(curl -sS --max-time 20 -f "$JWKS_URL")" || fail "step 2: could not fetch $JWKS_URL"
printf '%s' "$JWKS" | python3 -c '
import json, sys
keys = json.load(sys.stdin)["keys"]
assert keys, "JWKS has no keys"
bad = [k for key in keys for k in ("d", "p", "q", "dp", "dq", "qi") if k in key]
assert not bad, "JWKS exposes private members: " + ",".join(bad)
' || fail "step 2: JWKS check failed"
pass "step 2: JWKS has keys and no private members (d/p/q/dp/dq/qi)"

python3 "$root/scripts/verify_jwt.py" "$TOKEN" "$JWKS_URL" "$ISSUER" "$AUDIENCE" >/dev/null \
  || fail "step 2: PyJWT could not verify the token against the JWKS"
pass "step 2: PyJWT verifies the token (RS256, iss, aud, exp, iat, sub) against the JWKS"

# --- Step 3: uniform 401, form post -> 400 --------------------------------------------------------------
export E2E_WRONG_PASSWORD="wrong-password-$RANDOM" E2E_UNKNOWN_EMAIL="nobody-$RANDOM@example.invalid"
post_json < <(login_body E2E_EMAIL E2E_WRONG_PASSWORD)
WRONG_CODE="$HTTP_CODE"; WRONG_BODY="$BODY"
post_json < <(login_body E2E_UNKNOWN_EMAIL E2E_PASSWORD)
UNKNOWN_CODE="$HTTP_CODE"; UNKNOWN_BODY="$BODY"
[[ "$WRONG_CODE" == "401" && "$UNKNOWN_CODE" == "401" ]] \
  || fail "step 3: expected 401/401, got $WRONG_CODE/$UNKNOWN_CODE"
[[ "$WRONG_BODY" == "$UNKNOWN_BODY" ]] || fail "step 3: wrong-password and unknown-email bodies differ"
[[ "$WRONG_BODY" == '{"error":"invalid_credentials"}' ]] || fail "step 3: unexpected 401 body"
pass "step 3: wrong password and unknown email both -> 401 {\"error\":\"invalid_credentials\"} (identical)"

FORM_CODE="$(curl -s -o /dev/null --max-time 20 -w '%{http_code}' -X POST "$BASE_URL/auth/login" \
  --data-urlencode "email=form@example.invalid" --data-urlencode "password=form-not-a-secret")"
[[ "$FORM_CODE" == "400" ]] || fail "step 3: form post returned HTTP $FORM_CODE, expected 400"
pass "step 3: form-encoded login -> 400"

# --- Step 4: genuine restart; pre-restart token vs post-restart JWKS ------------------------------------
"${compose[@]}" restart auth >/dev/null 2>&1 || fail "step 4: docker compose restart auth failed"
wait_healthy || fail "step 4: auth did not become healthy after restart"
python3 "$root/scripts/verify_jwt.py" "$TOKEN" "$JWKS_URL" "$ISSUER" "$AUDIENCE" >/dev/null \
  || fail "step 4: pre-restart token no longer verifies against the post-restart JWKS"
pass "step 4: restart auth -> pre-restart token verifies against post-restart JWKS"

# --- Step 5: regression, fresh login after restart -------------------------------------------------------
post_json < <(login_body E2E_EMAIL E2E_PASSWORD)
[[ "$HTTP_CODE" == "200" ]] || fail "step 5: post-restart login returned HTTP $HTTP_CODE"
TOKEN2="$(printf '%s' "$BODY" | json_get access_token)"
python3 "$root/scripts/verify_jwt.py" "$TOKEN2" "$JWKS_URL" "$ISSUER" "$AUDIENCE" >/dev/null \
  || fail "step 5: post-restart token does not verify"
pass "step 5: fresh login after restart -> 200 and verifies"

echo "ALL PASS"
