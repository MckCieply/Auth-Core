#!/usr/bin/env bash
# Real-network end-to-end check of spec 0003 (lockout on POST /auth/login) against the compose stack.
#
# What it checks, in order:
#   1. health; the seed credentials log in (200), so the seed identifier starts without a streak.
#   2. timing (criterion 6): over 15 + 15 alternating samples, the median response time of an unknown email with a
#      wrong password is within 0.5-2x that of the seed email with a wrong password (both medians are printed).
#   3. a burst of five failed attempts on one unknown email locks the sixth (429, error body, Retry-After,
#      Cache-Control: no-store, no cookie) - criteria 2 and 5.
#   4. the same burst on the seed email: even the CORRECT password is then refused (criterion 1), and the refusal has
#      the same headers and the same body (up to the number) as the unknown email's (criteria 4 and 8).
#   5. after the cooldown (a real wait) the correct password logs in (200 + auth_rt cookie) - criterion 7.
#   6. four failures and a correct login: the streak is gone and the account is left clean (criterion 7).
#
# Full sequence, from the repo root (same stack and .env as scripts/e2e-login.sh):
#   cp .env.example .env                  # then set real local values (git-ignored)
#   scripts/dev-keys.sh                   # dev signing/encryption keys into .secrets/ (git-ignored)
#   export COMPOSE_PROJECT_NAME=auth-core-hardening   # a project of its own, never "auth-core" (the development stack): a bare down -v would wipe it
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # clean slate
#   AUTH_RATE_LIMIT_ENABLED=false docker compose -f deploy/docker-compose.yml --env-file .env up -d --build    # start postgres + auth
#                                         # the per-IP limiter is off for the older checks (spec 0008): the lockout script makes about 58 logins a minute and the mail script exactly 10 mail requests; scripts/e2e-hardening.sh runs last on the stack recreated with the defaults
#   scripts/e2e-login.sh                  # spec 0001 regression
#   scripts/e2e-refresh.sh                # spec 0002 regression
#   scripts/e2e-lockout.sh                # this script (does NOT bring the stack up or down)
#   docker compose -f deploy/docker-compose.yml --env-file .env down -v          # tear down
#
# Reads AUTH_DEV_SEED_EMAIL / AUTH_DEV_SEED_PASSWORD from the repo-root .env (parsed, never sourced).
# Needs: curl, python3, docker compose (for the stack only). Env: BASE_URL (default http://localhost:8080).
# Takes about 2.5 minutes: step 5 waits out a real cooldown (RETRY + 1 = 62-121 s). The threshold at human pace, the escalation
# from one cooldown to the next and the 24-hour reset are covered by integration tests with a controlled clock,
# not here.
# Re-runnable on the same stack: every unknown address is new per run. A run that dies between steps 4 and 6
# leaves the seed email locked for up to 30 minutes, so the other e2e scripts fail at their first login;
# `docker compose ... down -v` clears it.
# Exits non-zero on the first failure; prints "PASS <step>" per step and the two medians; never prints a password,
# a token or a cookie. Credentials stay off command lines: login bodies are built into files in a mktemp -d
# directory (removed on exit) and curl reads them with --data-binary @file.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_URL="${BASE_URL:-http://localhost:8080}"
SAMPLES=15

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
export E2E_EMAIL="$EMAIL" E2E_PASSWORD="$PASSWORD" E2E_WRONG_PASSWORD="wrong-password-$RANDOM$RANDOM"

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

# make_body <email-env> <password-env> <file>: JSON login body from the given env var names, written to a file
# under $tmp, so that no secret is on a command line and no interpreter starts between the requests of a burst.
make_body() {
  python3 -c 'import json,os,sys; print(json.dumps({"email": os.environ[sys.argv[1]], "password": os.environ[sys.argv[2]]}))' "$1" "$2" > "$3"
}

# login <body-file>  ->  sets HTTP_CODE, BODY, TIME_S (curl's time_total, seconds); response headers in $tmp/hdr
login() {
  local out
  out="$(curl -sS --max-time 20 -o "$tmp/body" -D "$tmp/hdr" -w '%{http_code} %{time_total}' \
    -X POST "$BASE_URL/auth/login" -H 'Content-Type: application/json' --data-binary "@$1")"
  HTTP_CODE="${out%% *}"
  TIME_S="${out##* }"
  BODY="$(cat "$tmp/body")"
}

header() { # value of a response header of the last response, CR stripped (empty when absent)
  { grep -i "^$1:" "$tmp/hdr" || true; } | head -n1 | cut -d: -f2- | tr -d '\r' | sed 's/^ *//'
}

header_names() { # sorted header names of the last response, Date excluded
  tail -n +2 "$tmp/hdr" | tr -d '\r' | grep ':' | cut -d: -f1 | tr '[:upper:]' '[:lower:]' | { grep -vx 'date' || true; } | sort | tr '\n' ' '
}

# expect_locked <what>  ->  asserts the lockout contract on the last response; sets RETRY
expect_locked() {
  [[ "$HTTP_CODE" == "429" ]] || fail "$1: HTTP $HTTP_CODE, expected 429"
  [[ "$BODY" =~ ^\{\"error\":\"too_many_attempts\",\"retry_after_seconds\":([0-9]+)\}$ ]] || fail "$1: unexpected 429 body"
  RETRY="${BASH_REMATCH[1]}"
  [[ "$(header retry-after)" == "$RETRY" ]] || fail "$1: Retry-After differs from retry_after_seconds"
  [[ "$(header cache-control)" == *no-store* ]] || fail "$1: no Cache-Control: no-store"
  [[ -z "$(header set-cookie)" ]] || fail "$1: a refused attempt set a cookie"
}

median_ms() { python3 -c 'import statistics,sys; print(round(statistics.median(map(float, sys.argv[1:])) * 1000))' "$@"; }

expect_401() { # expect_401 <what>: the last response is the uniform wrong-credentials 401
  [[ "$HTTP_CODE" == "401" ]] || fail "$1: HTTP $HTTP_CODE, expected 401"
  [[ "$BODY" == '{"error":"invalid_credentials"}' ]] || fail "$1: unexpected 401 body"
}

# --- Step 1: health; the seed credentials log in --------------------------------------------------------------
wait_healthy || fail "step 1: $BASE_URL/auth/health did not return 200 within 90s"
pass "health: /auth/health returned 200"

make_body E2E_EMAIL E2E_PASSWORD "$tmp/seed-right.json"
make_body E2E_EMAIL E2E_WRONG_PASSWORD "$tmp/seed-wrong.json"

login "$tmp/seed-right.json"
[[ "$HTTP_CODE" == "200" ]] || fail "step 1: login returned HTTP $HTTP_CODE, expected 200 (is the seed email still locked from an earlier run? tear the stack down with -v)"
pass "step 1: seed login -> 200"

# --- Step 2: timing, before any lock (criterion 6) ------------------------------------------------------------
# The right-password login after each pair ends the seed streak, so the failed samples never add up to a burst
# or to the threshold. Every unknown address is new, so no unknown identifier collects a streak either.
unknown_times=()
wrong_times=()
for i in $(seq 1 "$SAMPLES"); do
  export E2E_UNKNOWN_EMAIL="nobody-$RANDOM$RANDOM-$i@example.invalid"
  make_body E2E_UNKNOWN_EMAIL E2E_WRONG_PASSWORD "$tmp/unknown.json"
  login "$tmp/unknown.json"
  [[ "$HTTP_CODE" == "401" ]] || fail "step 2: unknown email, sample $i: HTTP $HTTP_CODE, expected 401"
  unknown_times+=("$TIME_S")

  login "$tmp/seed-wrong.json"
  [[ "$HTTP_CODE" == "401" ]] || fail "step 2: wrong password, sample $i: HTTP $HTTP_CODE, expected 401"
  wrong_times+=("$TIME_S")

  login "$tmp/seed-right.json"
  [[ "$HTTP_CODE" == "200" ]] || fail "step 2: right password after sample $i: HTTP $HTTP_CODE, expected 200"
done
UNKNOWN_MS="$(median_ms "${unknown_times[@]}")"
WRONG_MS="$(median_ms "${wrong_times[@]}")"
echo "median unknown email: ${UNKNOWN_MS} ms; median wrong password: ${WRONG_MS} ms (${SAMPLES} samples each)"
python3 -c 'import sys; u, w = float(sys.argv[1]), float(sys.argv[2]); sys.exit(0 if u > 0 and w > 0 and 0.5 <= u / w <= 2.0 else 1)' \
  "$UNKNOWN_MS" "$WRONG_MS" || fail "step 2: unknown/wrong median ratio is outside 0.5-2.0 (or a median is 0)"
RATIO="$(python3 -c 'import sys; print(round(float(sys.argv[1]) / float(sys.argv[2]), 2))' "$UNKNOWN_MS" "$WRONG_MS")"
pass "step 2: unknown/wrong median ratio $RATIO is within 0.5-2.0"

# --- Step 3: a burst on an unknown email locks the sixth attempt (criteria 2, 4, 5) -----------------------------
# One address for the whole burst, new for this run: a constant would still be locked on the next run.
export E2E_UNKNOWN_EMAIL="nobody-burst-$RANDOM$RANDOM@example.invalid"
make_body E2E_UNKNOWN_EMAIL E2E_WRONG_PASSWORD "$tmp/unknown-burst.json"
for i in 1 2 3 4 5; do
  login "$tmp/unknown-burst.json"
  expect_401 "step 3: unknown email, failed attempt $i"
done
login "$tmp/unknown-burst.json"
expect_locked "step 3: sixth attempt on an unknown email"
(( RETRY >= 61 && RETRY <= 120 )) || fail "step 3: retry_after_seconds $RETRY is not between 61 and 120"
UNKNOWN_HEADERS="$(header_names)"
UNKNOWN_LOCK_BODY="$BODY"
pass "step 3: five failures on an unknown email -> 401 each; the sixth -> 429 too_many_attempts with Retry-After and no-store"

# --- Step 4: the same burst on the seed email; a correct password is refused (criteria 1, 4, 8) -----------------
for i in 1 2 3 4 5; do
  login "$tmp/seed-wrong.json"
  expect_401 "step 4: seed email, failed attempt $i"
done
login "$tmp/seed-right.json"
expect_locked "step 4: the correct password right after the burst"
[[ "$(header_names)" == "$UNKNOWN_HEADERS" ]] || fail "step 4: the lockout response headers differ from the unknown email's"
[[ "$(printf '%s' "$BODY" | sed -E 's/[0-9]+/N/')" == "$(printf '%s' "$UNKNOWN_LOCK_BODY" | sed -E 's/[0-9]+/N/')" ]] \
  || fail "step 4: the lockout body differs from the unknown email's by more than the number"
pass "step 4: five failures on the seed email, then the correct password -> 429; same headers and body shape as the unknown email"

# --- Step 5: after the cooldown the correct password logs in (criterion 7) -------------------------------------
echo "waiting $((RETRY + 1)) s for the cooldown to elapse"
sleep $((RETRY + 1))
login "$tmp/seed-right.json"
[[ "$HTTP_CODE" == "200" ]] || fail "step 5: login after the cooldown returned HTTP $HTTP_CODE, expected 200"
[[ -n "$(header set-cookie | grep -i '^auth_rt=[^;]' || true)" ]] || fail "step 5: login after the cooldown set no auth_rt cookie"
pass "step 5: after the cooldown the correct password -> 200 with an auth_rt cookie"

# --- Step 6: success reset the streak; four failures do not lock ----------------------------------------------
for i in 1 2 3 4; do
  login "$tmp/seed-wrong.json"
  expect_401 "step 6: seed email, failed attempt $i after the reset"
done
login "$tmp/seed-right.json"
[[ "$HTTP_CODE" == "200" ]] || fail "step 6: the correct password after four failures returned HTTP $HTTP_CODE, expected 200"
pass "step 6: four failures after the reset -> 401 each; the correct password -> 200 (account left without a streak)"

echo "ALL PASS"
