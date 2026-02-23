#!/usr/bin/env bash
# Real-network end-to-end check of spec 0007 (the Angular sample "notes-web") against the compose stack with the notes
# sample and the web overlay: Auth-Core, PostgreSQL, the mail catcher (Mailpit), the notes service and Caddy. Playwright
# drives the built app in Chromium (desktop) and WebKit (iPhone 15) on https://localhost:8443, whose certificate comes from
# Caddy's own authority and is accepted by the tests. Auth-Core's mail links point at http://localhost:8088; a test opens the
# same path on the test origin.
#
# For each project (E2E_PROJECTS, default "chromium webkit") the script: removes the stack and its volumes, starts it clean
# (docker compose up -d --build), waits for it, seeds what the tests need, runs Playwright for that project, and at the end
# stops the stack (E2E_KEEP_STACK=1 keeps it). One clean stack per project, because some state is used up by a run: the
# seeded unverified user can be confirmed once, and the mail limits are per address.
#
# What it seeds (nothing printed): a note by the development admin; through the operator CLI two invitations in the
# development company, a viewer (test group 4) and a user (test group 3, who sets a password, forgets it and resets it).
# The CLI only queues the mail, the server sends it within a minute or two; the tests wait for the mails.
#
# Full sequence, from the repo root:
#   cp .env.example .env                  # then set real local values (git-ignored); NOTES_DB_PASSWORD and the unverified user too
#   MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh        # signing/encryption keys into .secrets/ (git-ignored)
#   scripts/e2e-web.sh
# Needs: node 24 (and `npm ci` in samples/notes-web, which the script runs when node_modules is missing), Playwright's
# Chromium and WebKit (npx playwright install chromium webkit, once), curl, docker compose, and the host ports 8088, 8443,
# 8080 and 8025 free. Reads AUTH_DEV_SEED_EMAIL, AUTH_DEV_SEED_PASSWORD, AUTH_DEV_SEED_UNVERIFIED_EMAIL,
# AUTH_DEV_SEED_UNVERIFIED_PASSWORD and NOTES_DB_PASSWORD from the repo-root .env (parsed, never sourced).
# Env: COMPOSE_PROJECT_NAME (default auth-core-web; a name must be auth-core-web or auth-core-web-<suffix> with a suffix of
# a-z, 0-9 and -, because the script runs `down -v` on it: the development stack, "auth-core", and any other project are refused;
# a project of that name that is running is refused too, so two runs never remove each other's stack: set another name), E2E_PROJECTS, E2E_KEEP_STACK, E2E_KEEP_RESULTS,
# HTTP_URL, HTTPS_URL, MAILPIT_URL. Exits non-zero on the first failure; prints "PASS <project>" per project; never prints a
# password, a token, a cookie or a mail body. samples/notes-web/test-results/ (a screenshot of each failed test shows the page:
# emails, notes) is removed when the script ends, whatever the outcome; E2E_KEEP_RESULTS=1 keeps it for a look.
#
# The stack is started with the per-IP rate limiter OFF (AUTH_RATE_LIMIT_ENABLED=false, spec 0008): the seeding and every test of both
# browsers reach Auth-Core from one address through the trusted Caddy proxy, so they share one limiter partition, and a run can make
# more than 30 logins or 10 mail requests a minute. The limits are checked by scripts/e2e-hardening.sh, not here.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
web="$root/samples/notes-web"
HTTP_URL="${HTTP_URL:-http://localhost:8088}"
HTTPS_URL="${HTTPS_URL:-https://localhost:8443}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
PROJECTS="${E2E_PROJECTS:-chromium webkit}"
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-auth-core-web}"
# The script runs `down -v` on its project: only on a project of its own, named auth-core-web or auth-core-web-<suffix> (suffix of
# lower-case letters, digits and hyphens). Never on the development stack (auth-core) or on a project of something else.
[[ "$COMPOSE_PROJECT_NAME" =~ ^auth-core-web(-[a-z0-9-]+)?$ ]] \
  || { echo "FAIL COMPOSE_PROJECT_NAME must be auth-core-web or auth-core-web-<suffix> (suffix: a-z, 0-9, -): this script removes the volumes of its project; use such a name" >&2; exit 1; }
# A stack of that name that is running now (another run, another person) is not touched: the clean start below would remove it.
if [[ -n "$(docker ps -q --filter "label=com.docker.compose.project=$COMPOSE_PROJECT_NAME" 2>/dev/null || true)" ]]; then
  echo "FAIL a stack named $COMPOSE_PROJECT_NAME is running: this script would remove it (down -v); pick another name, e.g. COMPOSE_PROJECT_NAME=auth-core-web-mine $0" >&2
  exit 1
fi
# The limiter is off for this stack (see the header): the compose file reads the variable from the environment, which wins over .env.
export AUTH_RATE_LIMIT_ENABLED=false
compose=(docker compose -f "$root/deploy/docker-compose.yml" -f "$root/samples/notes-api/compose.yml" -f "$web/compose.yml" --env-file "$root/.env")

tmp="$(mktemp -d)"
cleanup() {
  if [[ "${E2E_KEEP_STACK:-0}" != "1" ]]; then "${compose[@]}" down -v >/dev/null 2>&1 || true; fi
  # The screenshots of failed tests show the page (emails, notes): they do not stay on disk.
  if [[ "${E2E_KEEP_RESULTS:-0}" != "1" ]]; then rm -rf "$web/test-results"; fi
  rm -rf "$tmp"
}
trap cleanup EXIT

fail() { echo "FAIL $*" >&2; exit 1; }
pass() { echo "PASS $*"; }

# Everything of a tool's output that is passed on goes through this: a mail link (token=...) and the value of a Playwright
# fill("...") (a typed password) are hidden.
hide() { sed -E -e 's/token=[A-Za-z0-9_-]+/token=<hidden>/g' -e 's/fill\("([^"\\]|\\.)*"\)/fill("<hidden>")/g'; }

env_get() { # read KEY from .env without executing it; strips one pair of surrounding quotes
  local line
  line="$(grep -E "^$1=" "$root/.env" | tail -n1 || true)"
  line="${line#*=}"
  line="${line%$'\r'}"
  if [[ "$line" =~ ^\"(.*)\"$ || "$line" =~ ^\'(.*)\'$ ]]; then line="${BASH_REMATCH[1]}"; fi
  printf '%s' "$line"
}

[[ -f "$root/.env" ]] || fail "missing $root/.env (copy .env.example and set local values)"
[[ -d "$root/.secrets" ]] || fail "missing $root/.secrets (run: MSYS2_ARG_CONV_EXCL='/CN=' scripts/dev-keys.sh)"
SEED_EMAIL="$(env_get AUTH_DEV_SEED_EMAIL)"
SEED_PASSWORD="$(env_get AUTH_DEV_SEED_PASSWORD)"
UNVERIFIED_EMAIL="$(env_get AUTH_DEV_SEED_UNVERIFIED_EMAIL)"
UNVERIFIED_PASSWORD="$(env_get AUTH_DEV_SEED_UNVERIFIED_PASSWORD)"
[[ -n "$SEED_EMAIL" && -n "$SEED_PASSWORD" ]] || fail "AUTH_DEV_SEED_EMAIL / AUTH_DEV_SEED_PASSWORD not set in .env"
[[ -n "$UNVERIFIED_EMAIL" && -n "$UNVERIFIED_PASSWORD" ]] \
  || fail "AUTH_DEV_SEED_UNVERIFIED_EMAIL / AUTH_DEV_SEED_UNVERIFIED_PASSWORD not set in .env (see .env.example)"
[[ -n "$(env_get NOTES_DB_PASSWORD)" ]] || fail "NOTES_DB_PASSWORD not set in .env (see .env.example)"
command -v node >/dev/null || fail "node is not on the PATH"
if [[ ! -d "$web/node_modules/@playwright/test" ]]; then
  echo "installing the packages of samples/notes-web (npm ci)..."
  (cd "$web" && npm ci > "$tmp/npm.log" 2>&1) || { tail -n 20 "$tmp/npm.log" >&2; fail "npm ci failed"; }
fi

wait_ok() { # wait_ok <url> <seconds> [curl option]: waits until the URL answers 200
  local i
  for i in $(seq 1 "$2"); do
    if [[ "$(curl -s ${3:-} -o /dev/null --max-time 3 -w '%{http_code}' "$1" || true)" == "200" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}

json_field() { # json_field <name>: the string field <name> of the JSON on stdin (exit 1 when it is not a string)
  node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const v=JSON.parse(s)[process.argv[1]];if(typeof v!=="string")process.exit(1);process.stdout.write(v)})' "$1"
}

cli() { # cli <args...>: the operator CLI, in the service's own image; stdout and stderr are kept out of the log
  "${compose[@]}" run --rm -T --no-deps auth admin "$@" > "$tmp/cli.out" 2> "$tmp/cli.err" \
    || { hide < "$tmp/cli.err" >&2; fail "the operator CLI failed: admin $1"; }
}

seed_stack() {
  local run="$RANDOM$RANDOM" code org token
  VIEWER_EMAIL="viewer-$run@e2e.test"
  RESETTER_EMAIL="resetter-$run@e2e.test"
  SEEDED_NOTE="Seeded by the e2e script, run $run"
  USER_PASSWORD="E2e-Passw0rd-$run"
  NEW_PASSWORD="E2e-N3w-Passw0rd-$run"

  # The development admin: a token (kept in a header file for curl, never printed), the company id, one note.
  E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD" \
    node -e 'console.log(JSON.stringify({email: process.env.E2E_SEED_EMAIL, password: process.env.E2E_SEED_PASSWORD}))' > "$tmp/login.json"
  curl -sS --max-time 20 -X POST "$HTTP_URL/auth/login" -H 'Content-Type: application/json' \
    --data-binary "@$tmp/login.json" -o "$tmp/login.out" || fail "seeding: the admin could not sign in"
  token="$(json_field access_token < "$tmp/login.out")" \
    || fail "seeding: the admin's sign-in gave no token (is the development user in .env the one the stack was started with?)"
  printf 'Authorization: Bearer %s\n' "$token" > "$tmp/admin.auth"
  token=""
  curl -sS --max-time 20 "$HTTP_URL/auth/me" -H "@$tmp/admin.auth" -o "$tmp/me.json" || fail "seeding: GET /auth/me failed"
  org="$(json_field org_id < "$tmp/me.json")" || fail "seeding: /auth/me gave no company"
  E2E_SEEDED_NOTE="$SEEDED_NOTE" node -e 'console.log(JSON.stringify({text: process.env.E2E_SEEDED_NOTE}))' > "$tmp/note.json"
  code="$(curl -sS --max-time 20 -o /dev/null -w '%{http_code}' -X POST "$HTTP_URL/api/notes" \
    -H 'Content-Type: application/json' -H "@$tmp/admin.auth" --data-binary "@$tmp/note.json")"
  [[ "$code" == "201" ]] || fail "seeding: adding the first note answered $code, not 201"

  # Two invitations through the operator CLI. The server sends the mails at its next poll.
  cli invite --org "$org" --email "$VIEWER_EMAIL" --role viewer
  cli invite --org "$org" --email "$RESETTER_EMAIL" --role user
  rm -f "$tmp/login.json" "$tmp/login.out" "$tmp/admin.auth" "$tmp/me.json" "$tmp/note.json"
}

for project in $PROJECTS; do
  echo "== $project: a clean stack (the first build takes a few minutes)..."
  "${compose[@]}" down -v > "$tmp/down.log" 2>&1 || true
  "${compose[@]}" up -d --build > "$tmp/up.log" 2>&1 || { hide < "$tmp/up.log" >&2; fail "$project: docker compose up failed"; }
  wait_ok "$HTTP_URL/auth/health" 120 || fail "$project: $HTTP_URL/auth/health did not return 200 within 120s"
  wait_ok "$HTTP_URL/api/health" 60 || fail "$project: $HTTP_URL/api/health did not return 200 within 60s"
  wait_ok "$MAILPIT_URL/readyz" 60 || fail "$project: $MAILPIT_URL/readyz did not return 200 within 60s"
  wait_ok "$HTTPS_URL/login" 60 -k || fail "$project: $HTTPS_URL/login (the app over HTTPS) did not return 200 within 60s"
  seed_stack

  export E2E_BASE_URL="$HTTPS_URL" E2E_API_URL="$HTTP_URL" E2E_MAILPIT_URL="$MAILPIT_URL"
  export E2E_SEED_EMAIL="$SEED_EMAIL" E2E_SEED_PASSWORD="$SEED_PASSWORD"
  export E2E_UNVERIFIED_EMAIL="$UNVERIFIED_EMAIL" E2E_UNVERIFIED_PASSWORD="$UNVERIFIED_PASSWORD"
  export E2E_VIEWER_EMAIL="$VIEWER_EMAIL" E2E_RESETTER_EMAIL="$RESETTER_EMAIL"
  export E2E_USER_PASSWORD="$USER_PASSWORD" E2E_NEW_PASSWORD="$NEW_PASSWORD" E2E_SEEDED_NOTE="$SEEDED_NOTE"
  # Without this, a failed test leaves test-results/<test>/error-context.md: an ARIA snapshot of the page that holds the value
  # of every input, a typed password included (Playwright 1.58.2 writes it unless this variable is set; workers inherit it).
  export PLAYWRIGHT_NO_COPY_PROMPT=1
  # A failure message of Playwright can hold a mail link (the URL of a page.goto) and, in its call log, the value of a
  # fill("...") (a typed password): both are hidden on the way out.
  # (pipefail is on: the exit status is Playwright's.)
  (cd "$web" && npx playwright test --project="$project" 2>&1 | hide) \
    || fail "$project: Playwright failed (E2E_KEEP_STACK=1 keeps the stack for a look)"
  pass "$project: every Playwright test passed on a clean stack"
done

echo "ALL PASS"
