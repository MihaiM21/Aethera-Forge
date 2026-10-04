#!/usr/bin/env bash
# Aethera end-to-end smoke test (Phase 1): builds the web export, runs the real API against a real
# PostgreSQL (and Redis), drives the REST API with curl, then drives the UI with headless Chromium.
#
# Phase 2 (a real Go agent in a container against this API): deploy/agent-e2e.sh, see tests/e2e/README.md.
#
#   bash deploy/smoke.sh
#
# Environment (all optional):
#   AETHERA_DB      Npgsql-style connection string of a PostgreSQL *server* whose role may CREATE DATABASE, e.g.
#                   "Host=localhost;Port=5432;Username=aethera;Password=aethera;Database=postgres" (the same value as
#                   AETHERA_TEST_DB). The smoke test creates its own throw-away database in it and drops it at the end,
#                   so it never touches your data and can be run again and again.
#                   Not set: a throw-away PostgreSQL is started from $PG_BIN (default /usr/lib/postgresql/16/bin), as
#                   the postgres user when run as root, in /tmp/aethera-smoke-*.
#   AETHERA_REDIS   StackExchange.Redis connection string, e.g. "localhost:6379". Not set: a throw-away redis-server is
#                   started on a free port (if redis-server is installed; otherwise the API uses its in-process fan-out).
#   AETHERA_SMOKE_PORT   Port of the API (default 5080).
#   SKIP_WEB_BUILD=1     Reuse src/web/out instead of running pnpm install + pnpm build.
#   SKIP_BROWSER=1       Skip the Playwright part.
#   SMOKE_KEEP=1         Keep the /tmp/aethera-smoke-* working directory (logs) on exit.
#
# Prints "[ ok ] ..." lines (the UI's boot-screen style) and exits non-zero on the first failure.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PORT="${AETHERA_SMOKE_PORT:-5080}"
BASE="http://127.0.0.1:${PORT}"
EMAIL="owner@smoke.example.com"
PASSWORD="correct horse battery staple"
WORK="$(mktemp -d /tmp/aethera-smoke-XXXXXX)"
JAR="$WORK/cookies.txt"
BODY="$WORK/body.json"
CSRF=""
STATUS=""

if [[ -d "$HOME/.dotnet" ]]; then
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  export PATH="$HOME/.dotnet:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# --------------------------------------------------------------------------------------------- output
ok() { printf '[ ok ] %s\n' "$*"; }
info() { printf '[ .. ] %s\n' "$*"; }
fail() {
  printf '[fail] %s\n' "$*" >&2
  if [[ -s "$BODY" ]]; then printf '       last response body: %s\n' "$(head -c 600 "$BODY")" >&2; fi
  if [[ -f "$WORK/api.log" ]]; then printf '       --- tail of the API log ---\n' >&2; tail -n 15 "$WORK/api.log" 2>/dev/null | sed 's/^/       /' >&2 || true; fi
  exit 1
}

# ------------------------------------------------------------------------------------------- cleanup
API_PID=""
PG_STARTED=""
REDIS_PID=""
DROP_DB=""
PG_ENV=()

cleanup() {
  local code=$?
  set +e
  trap - EXIT INT TERM
  if [[ -n "$API_PID" ]]; then
    kill -TERM -- "-$API_PID" 2>/dev/null
    for _ in $(seq 1 30); do kill -0 "$API_PID" 2>/dev/null || break; sleep 0.2; done
    kill -KILL -- "-$API_PID" 2>/dev/null
  fi
  if [[ -n "$DROP_DB" ]]; then
    env "${PG_ENV[@]}" psql -qAt -d postgres -c "DROP DATABASE IF EXISTS \"$DROP_DB\" WITH (FORCE)" >/dev/null 2>&1
  fi
  [[ -n "$REDIS_PID" ]] && kill "$REDIS_PID" 2>/dev/null
  if [[ -n "$PG_STARTED" ]]; then
    pg_as "$PG_BIN/pg_ctl" -D "$WORK/pgdata" -m immediate stop >/dev/null 2>&1
  fi
  if [[ "${SMOKE_KEEP:-}" == 1 ]]; then
    printf '[ .. ] kept %s\n' "$WORK"
  else
    rm -rf "$WORK"
  fi
  exit "$code"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# ----------------------------------------------------------------------------------------- helpers
free_port() {
  local p
  for p in $(shuf -i 20000-45000 -n 60); do
    if ! (exec 3<>"/dev/tcp/127.0.0.1/$p") 2>/dev/null; then echo "$p"; return 0; fi
  done
  return 1
}

# Runs a command as the postgres user when we are root (PostgreSQL refuses to run as root), else as ourselves.
pg_as() {
  if [[ $EUID -eq 0 ]]; then
    local cmd
    printf -v cmd '%q ' "$@"
    su postgres -s /bin/bash -c "cd /tmp && $cmd"
  else
    "$@"
  fi
}

# Reads one key (case-insensitive, with the usual synonyms) from an Npgsql connection string.
conn_get() {
  local conn="$1" want="$2" pair key val
  IFS=';' read -ra pairs <<<"$conn"
  for pair in "${pairs[@]}"; do
    key="${pair%%=*}"; val="${pair#*=}"
    key="$(echo "$key" | tr 'A-Z' 'a-z' | tr -d ' ')"
    case "$want:$key" in
      host:host|host:server) echo "$val"; return ;;
      port:port) echo "$val"; return ;;
      user:username|user:userid|user:user|user:uid) echo "$val"; return ;;
      password:password|password:pwd) echo "$val"; return ;;
      database:database|database:db) echo "$val"; return ;;
    esac
  done
}

# api METHOD PATH [JSON]: sets STATUS, writes the response body to $BODY. Cookie jar and CSRF token are used.
api() {
  local method="$1" path="$2" data="${3-}"
  local args=(-sS -o "$BODY" -w '%{http_code}' -b "$JAR" -c "$JAR" -X "$method" -H 'Accept: application/json')
  [[ -n "$CSRF" ]] && args+=(-H "X-CSRF-Token: $CSRF")
  if [[ -n "$data" ]]; then args+=(-H 'Content-Type: application/json' --data "$data"); fi
  STATUS="$(curl "${args[@]}" "$BASE$path")" || fail "$method $path: curl failed"
}

expect() { # expect STATUS DESCRIPTION
  [[ "$STATUS" == "$1" ]] || fail "$2: expected HTTP $1, got $STATUS"
}

# jqe FILTER: prints the filter's value from the last response; fails when it is missing, null or empty (false is a value).
jqe() {
  local value
  value="$(jq -r "$1" "$BODY")" || fail "unexpected response shape (jq $1)"
  [[ -n "$value" && "$value" != null ]] || fail "unexpected response (jq $1 is empty)"
  printf '%s' "$value"
}

# ============================================================================================ 1. build
cd "$ROOT"

if [[ "${SKIP_WEB_BUILD:-}" == 1 && -f src/web/out/index.html ]]; then
  ok "web export reused (src/web/out)"
else
  info "building the web export"
  (cd src/web && pnpm install --frozen-lockfile >"$WORK/pnpm-install.log" 2>&1 && pnpm build >"$WORK/pnpm-build.log" 2>&1) \
    || { tail -n 20 "$WORK"/pnpm-*.log >&2 || true; fail "pnpm install/build failed"; }
  ok "web export built ($(find src/web/out -name '*.html' | wc -l) pages in src/web/out)"
fi
[[ -f src/web/out/projects/_.html ]] || fail "src/web/out/projects/_.html is missing (detail route shell)"

info "building the API"
dotnet build src/control-plane/src/Aethera.Api -v q --nologo >"$WORK/dotnet-build.log" 2>&1 \
  || { tail -n 30 "$WORK/dotnet-build.log" >&2 || true; fail "dotnet build failed"; }
ok "API built"

# ================================================================================ 2. PostgreSQL, Redis
if [[ -n "${AETHERA_DB:-}" ]]; then
  PGH="$(conn_get "$AETHERA_DB" host)"; PGP="$(conn_get "$AETHERA_DB" port)"
  PGU="$(conn_get "$AETHERA_DB" user)"; PGPW="$(conn_get "$AETHERA_DB" password)"
  PG_ENV=(PGHOST="${PGH:-localhost}" PGPORT="${PGP:-5432}" PGUSER="${PGU:-postgres}" PGPASSWORD="${PGPW:-}")
  info "using the PostgreSQL server of AETHERA_DB (${PGH:-localhost}:${PGP:-5432})"
else
  PG_BIN="${PG_BIN:-/usr/lib/postgresql/16/bin}"
  [[ -x "$PG_BIN/initdb" ]] || fail "no AETHERA_DB given and no PostgreSQL binaries in $PG_BIN (set PG_BIN)"
  PGPORT_LOCAL="$(free_port)"
  mkdir -p "$WORK/pgdata" "$WORK/pgsock"
  [[ $EUID -eq 0 ]] && chown -R postgres:postgres "$WORK/pgdata" "$WORK/pgsock" && chmod 755 "$WORK"
  pg_as "$PG_BIN/initdb" -D "$WORK/pgdata" -U aethera --auth=trust >"$WORK/initdb.log" 2>&1 || { cat "$WORK/initdb.log" >&2 || true; fail "initdb failed"; }
  pg_as "$PG_BIN/pg_ctl" -D "$WORK/pgdata" -l "$WORK/pgsock/postgres.log" -w \
    -o "-p $PGPORT_LOCAL -c listen_addresses=127.0.0.1 -c unix_socket_directories=$WORK/pgsock -c fsync=off" start >/dev/null \
    || { tail -n 20 "$WORK/pgsock/postgres.log" >&2 || true; fail "PostgreSQL did not start"; }
  PG_STARTED=1
  AETHERA_DB="Host=127.0.0.1;Port=$PGPORT_LOCAL;Username=aethera;Password=aethera;Database=postgres"
  PG_ENV=(PGHOST=127.0.0.1 PGPORT="$PGPORT_LOCAL" PGUSER=aethera PGPASSWORD=aethera)
  info "throw-away PostgreSQL on 127.0.0.1:$PGPORT_LOCAL"
fi

DB_NAME="aethera_smoke_$(openssl rand -hex 6)"
env "${PG_ENV[@]}" psql -qAt -d postgres -c "CREATE DATABASE \"$DB_NAME\"" >/dev/null \
  || fail "could not create the smoke database (does the role of AETHERA_DB have CREATEDB?)"
DROP_DB="$DB_NAME"
DB_CONN="Host=$(conn_get "$AETHERA_DB" host);Port=$(conn_get "$AETHERA_DB" port);Username=$(conn_get "$AETHERA_DB" user);Password=$(conn_get "$AETHERA_DB" password);Database=$DB_NAME"
ok "database $DB_NAME created"

if [[ -z "${AETHERA_REDIS:-}" ]] && command -v redis-server >/dev/null; then
  REDIS_PORT="$(free_port)"
  redis-server --port "$REDIS_PORT" --bind 127.0.0.1 --dir "$WORK" --save "" --appendonly no >"$WORK/redis.log" 2>&1 &
  REDIS_PID=$!
  for _ in $(seq 1 50); do (exec 3<>"/dev/tcp/127.0.0.1/$REDIS_PORT") 2>/dev/null && break; sleep 0.1; done
  AETHERA_REDIS="127.0.0.1:$REDIS_PORT"
  info "throw-away Redis on $AETHERA_REDIS"
fi

# ============================================================================================ 3. run the API
if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then fail "port $PORT is already in use (set AETHERA_SMOKE_PORT)"; fi

export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="$BASE"
export ConnectionStrings__Aethera="$DB_CONN"
export Aethera__Database__AutoMigrate=true
export Aethera__Web__Root="$ROOT/src/web/out"
export Logging__LogLevel__Microsoft__EntityFrameworkCore=Warning # Development logs every SQL statement otherwise
export AETHERA_MASTER_KEY="$(openssl rand -base64 32)"
[[ -n "${AETHERA_REDIS:-}" ]] && export ConnectionStrings__Redis="$AETHERA_REDIS"

setsid bash -c 'exec dotnet run --no-build --no-launch-profile --project src/control-plane/src/Aethera.Api' >"$WORK/api.log" 2>&1 &
API_PID=$!
for _ in $(seq 1 240); do
  kill -0 "$API_PID" 2>/dev/null || fail "the API exited during start-up"
  [[ "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/ready" || true)" == 200 ]] && break
  sleep 0.5
done
[[ "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/ready" || true)" == 200 ]] || fail "the API did not become ready"
ok "API listening on $BASE (Development, AutoMigrate, web root src/web/out)"

READY="$(curl -s "$BASE/ready")"
echo "$READY" | jq -e '.status == "ready" and .checks.database == "ok"' >/dev/null || fail "/ready: $READY"
ok "GET /ready: $(echo "$READY" | jq -c '.checks')"

# ================================================================================== 4. REST, with curl
api GET /api/v1/auth/setup; expect 200 "GET /auth/setup"
[[ "$(jqe '.setupRequired')" == true ]] || fail "GET /auth/setup: setupRequired should be true on a fresh database"
ok "GET /auth/setup -> setupRequired: true"

api POST /api/v1/auth/setup "$(jq -nc --arg e "$EMAIL" --arg p "$PASSWORD" '{email:$e,password:$p,displayName:"Smoke Owner",organizationName:"Smoke Inc"}')"
expect 201 "POST /auth/setup"
ok "POST /auth/setup -> 201 (owner created, signed in)"

api GET /api/v1/auth/setup; expect 200 "GET /auth/setup (again)"
[[ "$(jqe '.setupRequired')" == false ]] || fail "setupRequired should be false after setup"

api GET /api/v1/auth/csrf; expect 200 "GET /auth/csrf"
CSRF="$(jqe '.token')"
[[ ${#CSRF} -ge 16 ]] || fail "GET /auth/csrf: token too short"
ok "GET /auth/csrf -> token (${#CSRF} chars)"

api GET /api/v1/auth/me; expect 200 "GET /auth/me"
[[ "$(jqe '.role')" == owner && "$(jqe '.user.email')" == "$EMAIL" ]] || fail "GET /auth/me: unexpected identity"
ok "GET /auth/me -> $EMAIL (owner)"

api POST /api/v1/servers '{"name":"smoke-server","host":"smoke.example.com","roles":["master","worker"]}'
expect 201 "POST /servers"
SERVER_ID="$(jqe '.id')"
ok "POST /servers -> 201 ($SERVER_ID)"

api POST /api/v1/projects/from-template "$(jq -nc --arg s "$SERVER_ID" '{templateKey:"web-app",name:"Smoke Shop",serverId:$s}')"
expect 201 "POST /projects/from-template"
PROJECT_ID="$(jqe '.project.id')"
ENV_ID="$(jqe '.project.environments[0].id')"
[[ "$(jqe '.workloads | map(.slug) | join(",")')" == "frontend,backend,postgres,redis" ]] || fail "web-app template: unexpected workloads"
ok "POST /projects/from-template (web-app) -> 201 (project $PROJECT_ID, 4 workloads)"

api POST /api/v1/applications "$(jq -nc --arg e "$ENV_ID" --arg s "$SERVER_ID" \
  '{name:"smoke-nginx",environmentId:$e,serverId:$s,sourceKind:"dockerImage",image:{image:"nginx",tag:"1.27"}}')"
expect 201 "POST /applications"
APP_ID="$(jqe '.id')"
ok "POST /applications -> 201 ($APP_ID)"

api POST "/api/v1/applications/$APP_ID/env-vars" '{"key":"LOG_LEVEL","value":"debug"}'
expect 201 "POST /applications/{id}/env-vars"
[[ "$(jqe '.value')" == debug && "$(jqe '.isSecret')" == false ]] || fail "plain env var should come back readable"
ok "POST env-vars LOG_LEVEL=debug -> 201 (plain value returned)"

SECRET_VALUE="smoke-secret-$(openssl rand -hex 8)"
api POST /api/v1/secrets "$(jq -nc --arg v "$SECRET_VALUE" '{name:"DB_PASSWORD",description:"smoke",value:$v}')"
expect 201 "POST /secrets"
SECRET_ID="$(jqe '.id')"
grep -q "$SECRET_VALUE" "$BODY" && fail "POST /secrets leaked the plaintext"
[[ "$(jqe '.value')" == '********' ]] || fail "POST /secrets: value should be masked"
ok "POST /secrets -> 201 (value masked)"

api POST "/api/v1/applications/$APP_ID/env-vars" "$(jq -nc --arg s "$SECRET_ID" '{key:"DATABASE_PASSWORD",secretId:$s}')"
expect 201 "POST env-vars (secret-backed)"
[[ "$(jqe '.isSecret')" == true && "$(jqe '.value')" == '********' ]] || fail "secret-backed env var should be masked"
ok "POST env-vars DATABASE_PASSWORD=<secret> -> 201 (masked)"

for path in "/api/v1/applications/$APP_ID/env-vars" "/api/v1/applications/$APP_ID/env-vars/export" "/api/v1/secrets" "/api/v1/secrets/$SECRET_ID" "/api/v1/applications/$APP_ID"; do
  api GET "$path"; expect 200 "GET $path"
  grep -q "$SECRET_VALUE" "$BODY" && fail "GET $path leaked the secret value"
done
ok "the secret never appears in env-vars, export, secrets or application responses"

api POST /api/v1/jobs/echo '{"lines":["hello from the smoke test","second line","third line"],"delayMs":30}'
expect 202 "POST /jobs/echo"
JOB_ID="$(jqe '.id')"
for _ in $(seq 1 100); do
  api GET "/api/v1/jobs/$JOB_ID"; expect 200 "GET /jobs/{id}"
  JOB_STATUS="$(jqe '.status')"
  [[ "$JOB_STATUS" == succeeded || "$JOB_STATUS" == failed || "$JOB_STATUS" == cancelled ]] && break
  sleep 0.2
done
[[ "$JOB_STATUS" == succeeded ]] || fail "job $JOB_ID ended as '$JOB_STATUS'"
ok "POST /jobs/echo -> 202, job $JOB_ID Succeeded"

api GET "/api/v1/jobs/$JOB_ID/logs"; expect 200 "GET /jobs/{id}/logs"
LOG_TEXT="$(jqe '[.items[].text] | join("")')"
[[ "$LOG_TEXT" == *"hello from the smoke test"* && "$LOG_TEXT" == *"third line"* ]] || fail "job logs are missing the echoed lines"
ok "GET /jobs/{id}/logs -> $(jqe '.items | length') chunk(s) with the echoed lines"

api POST /api/v1/api-tokens '{"name":"smoke","scopes":["read"]}'
expect 201 "POST /api-tokens"
API_TOKEN="$(jqe '.token')"
[[ "$API_TOKEN" == aeth_* ]] || fail "API token has the wrong format"
BEARER_STATUS="$(curl -s -o "$BODY" -w '%{http_code}' -H "Authorization: Bearer $API_TOKEN" "$BASE/api/v1/projects")"
[[ "$BEARER_STATUS" == 200 ]] || fail "GET /projects with the Bearer token: HTTP $BEARER_STATUS"
jq -e --arg id "$PROJECT_ID" '.items | map(.id) | index($id) != null' "$BODY" >/dev/null || fail "the project is missing from GET /projects"
WRITE_STATUS="$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -H "Authorization: Bearer $API_TOKEN" --data '{"name":"nope"}' "$BASE/api/v1/projects")"
[[ "$WRITE_STATUS" == 403 ]] || fail "a read-scoped token must not create projects (got HTTP $WRITE_STATUS)"
ok "Bearer token (aeth_...) -> GET /projects 200, POST /projects 403 (scope)"

# ============================================================================ 5. static UI (ADR 0005)
HEADERS="$(curl -s -D - -o "$WORK/login.html" "$BASE/login")"
echo "$HEADERS" | head -1 | grep -q ' 200' || fail "GET /login: $(echo "$HEADERS" | head -1)"
echo "$HEADERS" | grep -qi '^content-type: text/html' || fail "GET /login is not text/html"
echo "$HEADERS" | grep -qi "^content-security-policy: default-src 'self'; script-src 'self' 'unsafe-inline'" || fail "GET /login has no CSP header"
cmp -s "$WORK/login.html" src/web/out/login.html || fail "GET /login did not return login.html"
ok "GET /login -> 200 text/html with Content-Security-Policy"

DETAIL_STATUS="$(curl -s -o "$WORK/detail.html" -w '%{http_code}' "$BASE/projects/$PROJECT_ID")"
[[ "$DETAIL_STATUS" == 200 ]] || fail "GET /projects/<id>: HTTP $DETAIL_STATUS"
cmp -s "$WORK/detail.html" src/web/out/projects/_.html || fail "GET /projects/<id> did not return projects/_.html"
ok "GET /projects/$PROJECT_ID -> 200 (projects/_.html shell)"

ASSET="$(grep -o '/_next/static/[^"]*\.js' "$WORK/login.html" | head -1)"
[[ -n "$ASSET" ]] || fail "no _next/static asset referenced by /login"
curl -sI "$BASE$ASSET" | grep -qi '^cache-control: public, max-age=31536000, immutable' || fail "$ASSET is not immutable"
ok "GET $ASSET -> immutable cache"

# ================================================================================================ 6. logout
api POST /api/v1/auth/logout; expect 204 "POST /auth/logout"
api GET /api/v1/auth/me; expect 401 "GET /auth/me after logout"
ok "POST /auth/logout -> 204, GET /auth/me -> 401"

# ================================================================================== 7. browser (Chromium)
if [[ "${SKIP_BROWSER:-}" == 1 ]]; then
  info "browser smoke skipped (SKIP_BROWSER=1)"
else
  (cd src/web && node scripts/smoke-browser.mjs "$BASE" "$EMAIL" "$PASSWORD" screenshots/smoke-dashboard-dark.png) \
    || fail "browser smoke failed"
fi

printf '\n[ ok ] all smoke checks passed\n'
