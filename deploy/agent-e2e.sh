#!/usr/bin/env bash
# Aethera agent <-> gateway end-to-end test (Phase 2, WP2.5).
#
#   bash deploy/agent-e2e.sh
#
# Runs the REAL .NET API (with its mTLS gRPC gateway) on the host against throw-away PostgreSQL/Redis containers, and the REAL Go agent
# (static linux binary) inside a Linux container that dials the gateway at host.docker.internal. A throw-away Docker-in-Docker daemon is
# the agent's Docker, so listing, pruning and log-producing containers never touch your own containers. Everything is driven through the
# public REST API with curl.
#
# Needs: bash, docker, curl, openssl, the .NET 10 SDK. jq is used when installed, else a jq container (ghcr.io/jqlang/jq).
#
# Environment (all optional):
#   E2E_PORT_BASE      Host ports are derived from it: +1 PostgreSQL, +2 Redis, +10 gateway (gRPC), +11 API (default 15400)
#   E2E_ENDPOINT_HOST  Host name agents use to reach the gateway; it is the SAN of the gateway certificate (default host.docker.internal)
#   SKIP_AGENT_BUILD=1 Reuse src/agent/dist/aethera-agent-linux-<arch> instead of building it with golang:1.24 (module cache volume aethera-gomod)
#   SKIP_API_BUILD=1   Do not run dotnet build
#   E2E_DOCKER=dind|none   dind (default): the agent talks to a Docker-in-Docker daemon. none: no Docker for the agent at all.
#   E2E_PHASES         Space separated subset of: main renew upgrade restart ui (default: all but ui; ui = the Phase 4 browser run, see deploy/ui-e2e-phase.sh)
#   E2E_KEEP=1         Leave containers/volumes/work dir in place on exit (the next run removes leftovers anyway)
#
# Prints "[ ok ] ..." lines and exits non-zero on the first failure (with the tail of the API and agent logs).
set -uo pipefail
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE_PORT="${E2E_PORT_BASE:-15400}"
PG_PORT=$((BASE_PORT + 1)); REDIS_PORT=$((BASE_PORT + 2)); GRPC_PORT=$((BASE_PORT + 10)); API_PORT=$((BASE_PORT + 11))
ENDPOINT_HOST="${E2E_ENDPOINT_HOST:-host.docker.internal}"
API="http://127.0.0.1:${API_PORT}"
PHASES="${E2E_PHASES:-main renew upgrade restart}"
DOCKER_MODE="${E2E_DOCKER:-dind}"
LABEL="aethera-e2e=1"
PREFIX="aethera-e2e"
EMAIL="owner@e2e.example.com"
PASSWORD="correct horse battery staple"
MAIN_PID=$$
WORK="$(mktemp -d "${TMPDIR:-/tmp}/aethera-e2e-XXXXXX")"
if command -v cygpath >/dev/null 2>&1; then WORK="$(cygpath -m "$WORK")"; fi # native tools (curl, docker, dotnet) need Windows-style paths under MSYS bash on Windows
JAR="$WORK/cookies.txt"; BODY="$WORK/body.json"; CSRF=""; STATUS=""
JQ_IMAGE="ghcr.io/jqlang/jq:1.7.1"
DOTNET_CLI_TELEMETRY_OPTOUT=1; DOTNET_NOLOGO=1; export DOTNET_CLI_TELEMETRY_OPTOUT DOTNET_NOLOGO

case "$(uname -m)" in aarch64|arm64) ARCH=arm64 ;; *) ARCH=amd64 ;; esac
AGENT_BIN="$ROOT/src/agent/dist/aethera-agent-linux-$ARCH"

# ------------------------------------------------------------------------------------------------------------- output
ok() { printf '[ ok ] %s\n' "$*"; }
info() { printf '[ .. ] %s\n' "$*"; }
agent_logs_tail() { # agent_logs_tail CONTAINER
  docker logs --tail 25 "$1" 2>&1 | sed 's/^/       /' >&2 || true
}
fail() {
  printf '[fail] %s\n' "$*" >&2
  if [[ -s "$BODY" ]]; then printf '       last response body: %s\n' "$(head -c 500 "$BODY")" >&2; fi
  if [[ -f "$WORK/api.log" ]]; then printf '       --- tail of the API log ---\n' >&2; tail -n 20 "$WORK/api.log" | sed 's/^/       /' >&2; fi
  for c in $(docker ps -a --filter "label=$LABEL" --format '{{.Names}}' 2>/dev/null | grep -E "agent" || true); do
    printf '       --- tail of %s ---\n' "$c" >&2; agent_logs_tail "$c"
  done
  kill -USR1 "$MAIN_PID" 2>/dev/null # fail may run in a $(...) subshell: stop the whole script
  exit 1
}

# ----------------------------------------------------------------------------------------------------------- helpers
if command -v jq >/dev/null 2>&1; then
  jqx() { jq "$@"; }
else
  jqx() { docker run --rm -i "$JQ_IMAGE" "$@"; }
fi

hostpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }

# api METHOD PATH [JSON]: sets STATUS, writes the body to $BODY (cookie jar + CSRF token).
api() {
  local method="$1" path="$2" data="${3-}"
  local args=(-sS -o "$BODY" -w '%{http_code}' -b "$JAR" -c "$JAR" -X "$method" -H 'Accept: application/json')
  [[ -n "$CSRF" ]] && args+=(-H "X-CSRF-Token: $CSRF")
  if [[ -n "$data" ]]; then args+=(-H 'Content-Type: application/json' --data "$data"); fi
  STATUS="$(curl "${args[@]}" "$API$path")" || fail "$method $path: curl failed"
}
expect() { [[ "$STATUS" == "$1" ]] || fail "$2: expected HTTP $1, got $STATUS"; }
jqe() { # prints the value of a filter over the last response; fails when null/empty
  local v; v="$(jqx -r "$1" <"$BODY")" || fail "unexpected response shape (jq $1)"
  [[ -n "$v" && "$v" != null ]] || fail "unexpected response (jq $1 is empty)"
  printf '%s' "$v"
}
jqv() { jqx -r "$1" <"$BODY" 2>/dev/null; } # tolerant version: prints "null"/empty instead of failing

# wait_for DESCRIPTION TIMEOUT_SECONDS COMMAND...: polls until the command succeeds.
wait_for() {
  local desc="$1" timeout="$2"; shift 2
  local end=$((SECONDS + timeout))
  while ((SECONDS < end)); do
    if "$@" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  fail "timed out after ${timeout}s waiting for: $desc"
}

# status_axis SERVER AXIS: current health of one axis ("available", "unavailable", "unknown", ...)
status_axis() { api GET "/api/v1/servers/$1/status" && [[ "$STATUS" == 200 ]] && jqv ".$2.health"; }
axis_is() { [[ "$(status_axis "$1" "$2")" == "$3" ]]; }
# session_live SERVER: the gateway holds a live agent stream (the stored axis alone can be stale after a control plane crash)
session_live() { api GET "/api/v1/servers/$1/status" && [[ "$STATUS" == 200 ]] && [[ "$(jqx -r '.session != null' <"$BODY")" == true ]]; }

# job_wait JOBID: waits for a terminal state and prints it
job_wait() {
  local id="$1" st="" end=$((SECONDS + ${2:-90}))
  while ((SECONDS < end)); do
    api GET "/api/v1/jobs/$id"; expect 200 "GET /jobs/$id"
    st="$(jqv .status)"
    case "$st" in succeeded|failed|cancelled) printf '%s' "$st"; return 0 ;; esac
    sleep 1
  done
  fail "job $id did not finish (last status: $st)"
}

# ------------------------------------------------------------------------------------------------------------ cleanup
API_PID=""
remove_containers() {
  local ids; ids="$(docker ps -aq --filter "label=$LABEL" 2>/dev/null)"
  [[ -n "$ids" ]] && docker rm -f $ids >/dev/null 2>&1
  local vols; vols="$(docker volume ls -q --filter "label=$LABEL" 2>/dev/null)"
  [[ -n "$vols" ]] && docker volume rm -f $vols >/dev/null 2>&1
  return 0
}
stop_api() {
  if [[ -n "$API_PID" ]]; then
    kill "$API_PID" 2>/dev/null
    for _ in $(seq 1 30); do kill -0 "$API_PID" 2>/dev/null || break; sleep 0.3; done
    kill -9 "$API_PID" 2>/dev/null
    wait "$API_PID" 2>/dev/null
    API_PID=""
  fi
  if command -v taskkill >/dev/null 2>&1; then # Windows (MSYS bash): the native process can outlive its shell wrapper; free the ports by pid
    local pid
    for pid in $(netstat -ano 2>/dev/null | grep -E "[:.]($API_PORT|$GRPC_PORT) .*LISTENING" | awk '{print $NF}' | sort -u); do
      taskkill //F //PID "$pid" >/dev/null 2>&1
    done
  fi
  return 0
}
cleanup() {
  local code=$?
  set +e; trap - EXIT INT TERM
  stop_api
  if [[ "${E2E_KEEP:-}" == 1 ]]; then info "kept containers (label $LABEL) and $WORK"; else remove_containers; rm -rf "$WORK"; fi
  exit "$code"
}
trap cleanup EXIT
trap 'exit 130' INT TERM
trap 'exit 1' USR1

# ----------------------------------------------------------------------------------------------------- 1. prerequisites
cd "$ROOT"
for tool in docker curl openssl dotnet; do command -v "$tool" >/dev/null 2>&1 || fail "$tool is required"; done
docker info >/dev/null 2>&1 || fail "the Docker daemon is not reachable"
for image in postgres:17 redis:7 debian:12-slim; do docker image inspect "$image" >/dev/null 2>&1 || docker pull -q "$image" >/dev/null || fail "cannot pull $image"; done
# Docker Desktop resolves host.docker.internal itself (IPv4 only); on Linux it has to be mapped to the host gateway.
HOST_FLAGS=()
if [[ "$ENDPOINT_HOST" == host.docker.internal ]] && ! docker run --rm debian:12-slim getent hosts host.docker.internal >/dev/null 2>&1; then
  HOST_FLAGS=(--add-host host.docker.internal:host-gateway)
fi
remove_containers # leftovers of an aborted run
for p in $PG_PORT $REDIS_PORT $GRPC_PORT $API_PORT; do
  if (exec 3<>"/dev/tcp/127.0.0.1/$p") 2>/dev/null; then fail "port $p is already in use (set E2E_PORT_BASE)"; fi
done

# ---- the agent binary (static, built in a golang container)
if [[ "${SKIP_AGENT_BUILD:-}" == 1 && -f "$AGENT_BIN" ]]; then
  ok "agent binary reused ($AGENT_BIN)"
else
  info "building the agent (golang:1.24, go vet + go test + make cross)"
  docker volume create aethera-gomod >/dev/null
  docker run --rm -v "$(hostpath "$ROOT/src/agent"):/src" -v aethera-gomod:/go/pkg/mod -w /src golang:1.24 \
    sh -c 'go vet ./... && go test ./... >/tmp/gotest.log 2>&1 || { tail -n 30 /tmp/gotest.log; exit 1; }; make cross VERSION=0.1.0-e2e' >"$WORK/agent-build.log" 2>&1 \
    || { tail -n 30 "$WORK/agent-build.log" >&2; fail "agent build failed"; }
  ok "agent built ($(du -h "$AGENT_BIN" | cut -f1))"
fi

# ---- the API
API_DIR="$ROOT/src/control-plane/src/Aethera.Api"
if [[ "${SKIP_API_BUILD:-}" != 1 ]]; then
  info "building the API"
  dotnet build "$(hostpath "$API_DIR")" -v q --nologo >"$WORK/dotnet-build.log" 2>&1 || { tail -n 30 "$WORK/dotnet-build.log" >&2; fail "dotnet build failed"; }
fi
API_DLL="$(find "$API_DIR/bin" -name Aethera.Api.dll -path '*net10.0*' | head -1)"
[[ -n "$API_DLL" ]] || fail "Aethera.Api.dll not found (build the API first)"
ok "API ready to start ($(basename "$API_DLL"))"

# ----------------------------------------------------------------------------------------------- 2. PostgreSQL, Redis
docker run -d --name "$PREFIX-pg" --label "$LABEL" -e POSTGRES_USER=aethera -e POSTGRES_PASSWORD=aethera -p "127.0.0.1:$PG_PORT:5432" postgres:17 >/dev/null \
  || fail "could not start PostgreSQL"
docker run -d --name "$PREFIX-redis" --label "$LABEL" -p "127.0.0.1:$REDIS_PORT:6379" redis:7 >/dev/null || fail "could not start Redis"
wait_for "PostgreSQL" 60 docker exec "$PREFIX-pg" pg_isready -U aethera
sleep 2 # pg_isready answers during the init restart
wait_for "PostgreSQL after init" 30 docker exec "$PREFIX-pg" psql -U aethera -d postgres -c 'select 1'
ok "PostgreSQL on 127.0.0.1:$PG_PORT, Redis on 127.0.0.1:$REDIS_PORT (throw-away containers)"

MASTER_KEY="$(openssl rand -base64 32)"
DB_NUM=0
new_database() { # new_database: creates a fresh database and sets DB_CONN to its connection string (no subshell: the counter must persist)
  DB_NUM=$((DB_NUM + 1))
  local name="e2e_$DB_NUM"
  docker exec "$PREFIX-pg" psql -U aethera -d postgres -qAt -c "CREATE DATABASE $name" >/dev/null || fail "CREATE DATABASE failed"
  DB_CONN="Host=127.0.0.1;Port=$PG_PORT;Username=aethera;Password=aethera;Database=$name"
}

# start_api CONNECTION [KEY=VALUE ...]: starts the API with the extra Aethera:Agents settings (as environment variables)
start_api() {
  local conn="$1"; shift
  stop_api
  : >"$WORK/api.log"
  ( cd "$API_DIR" && exec env ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="$API" \
      ConnectionStrings__Aethera="$conn" ConnectionStrings__Redis="127.0.0.1:$REDIS_PORT" \
      Aethera__Database__AutoMigrate=true Aethera__Agents__GrpcPort="$GRPC_PORT" Aethera__Agents__PublicEndpoint="$ENDPOINT_HOST:$GRPC_PORT" \
      Logging__LogLevel__Microsoft.EntityFrameworkCore=Warning Logging__LogLevel__Aethera=Information AETHERA_MASTER_KEY="$MASTER_KEY" \
      "$@" dotnet "$(hostpath "$API_DLL")" >>"$WORK/api.log" 2>&1 ) &
  API_PID=$!
  local end=$((SECONDS + 120))
  until [[ "$(curl -s -o /dev/null -w '%{http_code}' "$API/ready" || true)" == 200 ]]; do
    kill -0 "$API_PID" 2>/dev/null || fail "the API exited during start-up"
    ((SECONDS < end)) || fail "the API did not become ready"
    sleep 1
  done
  ok "API up on $API, gateway on :$GRPC_PORT (endpoint $ENDPOINT_HOST:$GRPC_PORT) [$*]"
}

login_owner() { # first time: setup, afterwards: login
  CSRF=""; : >"$JAR"
  api GET /api/v1/auth/setup; expect 200 "GET /auth/setup"
  if [[ "$(jqv .setupRequired)" == true ]]; then
    api POST /api/v1/auth/setup "$(jqx -nc --arg e "$EMAIL" --arg p "$PASSWORD" '{email:$e,password:$p,displayName:"E2E Owner",organizationName:"E2E Inc"}')"
    expect 201 "POST /auth/setup"
  else
    api POST /api/v1/auth/login "$(jqx -nc --arg e "$EMAIL" --arg p "$PASSWORD" '{email:$e,password:$p}')"
    [[ "$STATUS" == 200 || "$STATUS" == 204 ]] || fail "POST /auth/login: HTTP $STATUS"
  fi
  api GET /api/v1/auth/csrf; expect 200 "GET /auth/csrf"
  CSRF="$(jqe .token)"
}

# ---------------------------------------------------------------------------------------------------- 3. agent plumbing
SOCK_VOL="$PREFIX-dockersock"
docker volume create --label "$LABEL" "$SOCK_VOL" >/dev/null

start_dind() {
  [[ "$DOCKER_MODE" == dind ]] || return 0
  docker pull -q docker:27-dind >/dev/null 2>&1 || true
  docker run -d --privileged --name "$PREFIX-dind" --label "$LABEL" -e DOCKER_TLS_CERTDIR= -v "$SOCK_VOL:/var/run" docker:27-dind >/dev/null \
    || fail "could not start Docker-in-Docker"
  wait_for "the Docker-in-Docker daemon" 90 docker exec "$PREFIX-dind" docker info
  ok "Docker-in-Docker daemon is up (the agent's Docker)"
}

# agent_setup NAME: creates the state volume and agent.yaml for an agent called NAME
agent_setup() {
  local name="$1" cfg="$WORK/$1.yaml"
  docker volume create --label "$LABEL" "$PREFIX-$name-state" >/dev/null
  {
    echo "state_dir: /var/lib/aethera"
    [[ "$DOCKER_MODE" == dind ]] && echo "docker_host: unix:///var/run/dind/docker.sock"
    echo "log_level: debug"
  } >"$cfg"
}

agent_docker() { # agent_docker NAME ARGS...: docker run flags shared by enroll and run
  local name="$1"; shift
  local args=(--label "$LABEL")
  # AGENT_NETWORK (phase ui): share the Docker daemon's network namespace, as an agent installed on the host does, so health probes
  # reach the containers' bridge addresses. Host mappings then belong to the daemon's container.
  if [[ -n "${AGENT_NETWORK:-}" ]]; then args+=(--network "$AGENT_NETWORK"); else args+=("${HOST_FLAGS[@]}"); fi
  args+=(-v "$(hostpath "$AGENT_BIN"):/usr/local/bin/aethera-agent:ro" -v "$(hostpath "$WORK/$name.yaml"):/etc/aethera/agent.yaml:ro"
    -v "$PREFIX-$name-state:/var/lib/aethera")
  [[ "$DOCKER_MODE" == dind ]] && args+=(-v "$SOCK_VOL:/var/run/dind")
  printf '%s\0' "${args[@]}"
}

# enroll_agent NAME TOKEN ENDPOINT PIN: runs `aethera-agent enroll` in a throw-away container sharing the agent's state volume
enroll_agent() {
  local name="$1" token="$2" endpoint="$3" pin="$4" flags=()
  while IFS= read -r -d '' f; do flags+=("$f"); done < <(agent_docker "$name")
  printf '%s' "$token" | docker run --rm -i "${flags[@]}" debian:12-slim aethera-agent enroll --endpoint "$endpoint" --token - --ca-sha256 "$pin" \
    >"$WORK/enroll-$name.log" 2>&1
}

run_agent() { # run_agent NAME [CONTAINER_SUFFIX]
  local name="$1" cname="$PREFIX-agent-$1${2:-}" flags=()
  while IFS= read -r -d '' f; do flags+=("$f"); done < <(agent_docker "$name")
  docker run -d --name "$cname" "${flags[@]}" debian:12-slim aethera-agent run --config /etc/aethera/agent.yaml >/dev/null || fail "could not start agent $cname"
}

new_join_token() { # new_join_token SERVER_ID -> sets TOKEN ENDPOINT PIN
  api POST "/api/v1/servers/$1/join-tokens" '{"ttlMinutes":30}'; expect 201 "POST /servers/{id}/join-tokens"
  TOKEN="$(jqe .token)"; ENDPOINT="$(jqe .endpoint)"; PIN="$(jqe .caFingerprintSha256)"
  [[ "$TOKEN" == aeth_join_* ]] || fail "join token has the wrong prefix"
  [[ "$ENDPOINT" == "$ENDPOINT_HOST:$GRPC_PORT" ]] || fail "join endpoint is '$ENDPOINT', expected $ENDPOINT_HOST:$GRPC_PORT"
  grep -q "$TOKEN" "$WORK/api.log" && fail "the join token appears in the API log"
  return 0
}

# agent_log_count CONTAINER PATTERN
agent_log_count() { docker logs "$1" 2>&1 | grep -c -- "$2" || true; }
agent_log_has() { docker logs "$1" 2>&1 | grep -q -- "$2"; }

# =========================================================================================================== phase: main
phase_main() {
  info "---- phase main: default gateway settings, idle ping after 4 s"
  start_dind
  new_database; start_api "$DB_CONN" Aethera__Agents__PingSeconds=4
  login_owner
  ok "owner created and signed in"

  api POST /api/v1/servers '{"name":"e2e-server","host":"host.docker.internal","roles":["master","worker"]}'; expect 201 "POST /servers"
  SERVER="$(jqe .id)"; SERVER_NAME="$(jqe .name)"
  ok "server created ($SERVER)"

  new_join_token "$SERVER"
  ok "join token issued; endpoint $ENDPOINT, pin ${PIN:0:12}..., command contains --token/--endpoint/--ca-sha256"

  # ---- enrollment negatives
  agent_setup main
  enroll_agent main "aeth_join_$(openssl rand -hex 16)" "$ENDPOINT" "$PIN" && fail "enrolling with an unknown token must fail"
  grep -q -i "rejected\|unknown" "$WORK/enroll-main.log" || fail "unknown token: unexpected message: $(head -c 300 "$WORK/enroll-main.log")"
  ok "enroll with an unknown token is refused"
  enroll_agent main "$TOKEN" "$ENDPOINT" "$(printf '%064d' 0)" && fail "enrolling with a wrong CA pin must fail"
  ok "enroll with a wrong CA pin is refused (control plane not authenticated)"

  enroll_agent main "$TOKEN" "$ENDPOINT" "$PIN" || { cat "$WORK/enroll-main.log" >&2; fail "aethera-agent enroll failed"; }
  grep -q "enrolled as server $SERVER" "$WORK/enroll-main.log" || fail "enroll output does not name server $SERVER: $(cat "$WORK/enroll-main.log")"
  grep -q "$TOKEN" "$WORK/enroll-main.log" && fail "the join token appears in the enroll output"
  ok "aethera-agent enroll -> server $SERVER (SAN identity accepted by the agent's own validation)"
  enroll_agent main "$TOKEN" "$ENDPOINT" "$PIN" && fail "a join token must be single use"
  ok "replaying the used join token is refused"

  api GET "/api/v1/servers/$SERVER/join-tokens"; expect 200 "list join tokens"
  [[ "$(jqx -r '[.[]|select(.state=="used")]|length' <"$BODY")" -ge 1 ]] || fail "the token is not listed as used"

  # ---- connect
  run_agent main
  wait_for "agent axis available" 60 axis_is "$SERVER" agent available
  ok "agent connected: status.agent = available"
  api GET "/api/v1/servers/$SERVER/status"; expect 200 "GET status"
  [[ "$(jqe .session.agentVersion)" == "0.1.0-e2e" ]] || fail "session.agentVersion=$(jqv .session.agentVersion)"
  info "capabilities: $(jqx -r '.session.capabilities|join(",")' <"$BODY")"
  SKEW="$(jqv .session.clockSkewSeconds)"
  ok "session: version 0.1.0-e2e, clock skew ${SKEW}s"

  if [[ "$DOCKER_MODE" == dind ]]; then
    wait_for "docker axis available" 60 axis_is "$SERVER" docker available
    ok "status.docker = available (heartbeat docker_status RUNNING)"
  else
    wait_for "docker axis unavailable" 60 axis_is "$SERVER" docker unavailable
    ok "no Docker for the agent: status.docker = unavailable"
  fi

  # ---- discovery
  wait_for "discovery stored" 60 bash -c "curl -s -b '$JAR' '$API/api/v1/servers/$SERVER/discovery' | grep -q '\"report\":{'"
  api GET "/api/v1/servers/$SERVER/discovery"; expect 200 "GET discovery"
  info "facts: os=$(jqv .facts.os) arch=$(jqv .facts.architecture) cpu=$(jqv .facts.cpuCores) mem=$(jqv .facts.memoryBytes) docker=$(jqv .facts.dockerVersion)"
  [[ "$(jqe .facts.os)" == linux* || "$(jqe .facts.os)" == Linux* || -n "$(jqv .facts.os)" ]] || fail "discovery facts missing os"
  [[ "$(jqv .stale)" == false ]] || fail "discovery should not be stale while connected"
  ok "discovery report stored and exposed"

  # ---- metrics
  wait_for "metrics latest" 60 bash -c "curl -s -b '$JAR' '$API/api/v1/servers/$SERVER/metrics/latest' | grep -q '\"cpuPercent\"'"
  api GET "/api/v1/servers/$SERVER/metrics/latest"; expect 200 "GET metrics/latest"
  MEMTOTAL="$(jqe .host.memoryTotalBytes)"; [[ "$MEMTOTAL" -gt 0 ]] || fail "metrics latest has no memory total"
  [[ "$(jqv .stale)" == false ]] || fail "latest metrics are stale"
  sleep 25 # a few more 10 s samples
  api GET "/api/v1/servers/$SERVER/metrics?resolution=raw"; expect 200 "GET metrics series"
  POINTS="$(jqe '.points|length')"
  [[ "$POINTS" -ge 2 ]] || fail "metric series has $POINTS point(s), expected several"
  ok "metrics: latest (mem total $MEMTOTAL) and a series of $POINTS raw points"

  # ---- events (status transitions are recorded)
  api GET "/api/v1/servers/$SERVER/events"; expect 200 "GET events"
  [[ "$(jqx -r '[.[]|select(.axis=="agent" and .newValue=="connected")]|length' <"$BODY")" -ge 1 ]] || fail "no status.changed event for the agent axis"
  ok "events: agent axis transition recorded ($(jqx -r 'length' <"$BODY") event(s))"

  # ---- docker inventory + jobs
  if [[ "$DOCKER_MODE" == dind ]]; then
    docker exec "$PREFIX-dind" docker pull busybox:1.36 >/dev/null 2>&1 || info "could not pull busybox (offline?)"
    docker exec "$PREFIX-dind" docker run -d --name e2e-logger busybox:1.36 sh -c 'i=0; while true; do i=$((i+1)); echo "e2e log line $i"; sleep 0.05; done' >/dev/null 2>&1 \
      || info "could not start the log producer"
    docker exec "$PREFIX-dind" docker run -d --name e2e-stopped busybox:1.36 true >/dev/null 2>&1 || true
    api GET "/api/v1/servers/$SERVER/docker/containers"; expect 200 "GET docker/containers"
    jqx -e 'map(.names // [.name]|flatten|join(","))|join(" ")|contains("e2e-logger")' <"$BODY" >/dev/null || fail "container list lacks e2e-logger: $(head -c 300 "$BODY")"
    ok "docker/containers lists the containers of the agent's Docker ($(jqx -r length <"$BODY"))"
    api GET "/api/v1/servers/$SERVER/docker/images"; expect 200 "GET docker/images"
    ok "docker/images -> $(jqx -r length <"$BODY") image(s)"
    api GET "/api/v1/servers/$SERVER/docker/volumes"; expect 200 "GET docker/volumes"
    api GET "/api/v1/servers/$SERVER/docker/networks"; expect 200 "GET docker/networks"
    ok "docker/volumes and docker/networks answer"
    wait_for "container events" 30 bash -c "curl -s -b '$JAR' '$API/api/v1/servers/$SERVER/events?limit=100' | grep -q container"
  fi

  api POST "/api/v1/servers/$SERVER/maintenance/refresh-discovery"; expect 202 "POST refresh-discovery"
  JOB="$(jqe .id)"
  [[ "$(job_wait "$JOB")" == succeeded ]] || fail "refresh-discovery job did not succeed"
  ok "refresh-discovery job succeeded"

  api POST "/api/v1/servers/$SERVER/maintenance/prune" '{"danglingImages":true}'; expect 202 "POST prune"
  JOB="$(jqe .id)"
  [[ "$(job_wait "$JOB")" == succeeded ]] || fail "prune job did not succeed"
  ok "prune job (dangling images only) succeeded"
  if [[ "$DOCKER_MODE" == dind ]]; then
    api GET "/api/v1/servers/$SERVER/docker/containers"
    jqx -e 'map(.names // [.name]|flatten|join(","))|join(" ")|contains("e2e-logger")' <"$BODY" >/dev/null || fail "prune removed a running container"
  fi

  # ---- keepalive / idle ping: stay connected for > 2 keepalive intervals and one gateway ping
  info "holding the stream for 65 s (HTTP/2 keepalive every 20 s, gateway Ping after 4 s idle (agent traffic keeps a real stream under 10 s idle))"
  LOST_BEFORE="$(agent_log_count "$PREFIX-agent-main" 'connection lost')"
  sleep 65
  [[ "$(agent_log_count "$PREFIX-agent-main" 'connection lost')" == "$LOST_BEFORE" ]] || fail "the stream broke while idle"
  axis_is "$SERVER" agent available || fail "agent no longer available after idling"
  api GET "/api/v1/servers/$SERVER/status"
  [[ "$(jqe .session.rttMilliseconds)" != null ]] || fail "no Ping/Pong round trip measured"
  ok "stream survived 65 s idle with keepalive pings; gateway Ping/Pong rtt $(jqv .session.rttMilliseconds) ms"

  # ---- Docker daemon down / up (dind stopped)
  if [[ "$DOCKER_MODE" == dind ]]; then
    docker stop "$PREFIX-dind" >/dev/null
    wait_for "docker axis unavailable" 60 axis_is "$SERVER" docker unavailable
    axis_is "$SERVER" agent available || fail "agent axis should stay available while Docker is down"
    api GET "/api/v1/servers/$SERVER/docker/containers"
    [[ "$STATUS" == 502 || "$STATUS" == 503 || "$STATUS" == 422 ]] || fail "docker/containers with Docker down: expected an error status, got $STATUS"
    ok "Docker down: docker axis unavailable, agent axis still available, command -> HTTP $STATUS"
    docker start "$PREFIX-dind" >/dev/null
    wait_for "docker daemon back" 90 docker exec "$PREFIX-dind" docker info
    wait_for "docker axis available again" 90 axis_is "$SERVER" docker available
    ok "Docker back: docker axis available again"

    # ---- cancel an in-flight command: freeze the Docker daemon so the prune blocks inside the agent, then cancel the job
    docker pause "$PREFIX-dind" >/dev/null
    api POST "/api/v1/servers/$SERVER/maintenance/prune" '{"danglingImages":true}'; expect 202 "POST prune (frozen Docker)"
    JOB="$(jqe .id)"
    wait_for "the prune job to run" 60 bash -c "curl -s -b '$JAR' '$API/api/v1/jobs/$JOB' | grep -q '\"status\":\"running\"'"
    sleep 4 # the agent has acknowledged and is blocked in the Docker call
    api POST "/api/v1/jobs/$JOB/cancel"; expect 202 "POST jobs/{id}/cancel"
    JOB_STATE="$(job_wait "$JOB" 90)"
    [[ "$JOB_STATE" == cancelled ]] || fail "the cancelled job ended as '$JOB_STATE'"
    wait_for "the agent reports the command cancelled" 30 agent_log_has "$PREFIX-agent-main" 'command_type=system_prune.*COMMAND_STATUS_CANCELLED'
    ok "cancel: job cancelled, agent aborted the blocked system_prune with COMMAND_STATUS_CANCELLED"
    docker unpause "$PREFIX-dind" >/dev/null
    wait_for "docker axis available after unfreezing" 90 axis_is "$SERVER" docker available
  fi

  # ---- agent killed / restarted
  docker kill "$PREFIX-agent-main" >/dev/null
  START=$SECONDS
  wait_for "agent axis unavailable" 50 axis_is "$SERVER" agent unavailable
  ok "agent killed: status.agent = unavailable after $((SECONDS - START)) s"
  api GET "/api/v1/servers/$SERVER/docker/containers"
  [[ "$STATUS" == 503 ]] || fail "docker/containers with the agent down: expected 503, got $STATUS"
  [[ "$(jqe .code)" == *agent_unavailable* || "$(jqe .type)" == *agent_unavailable* ]] || info "503 body: $(head -c 200 "$BODY")"
  ok "commands while the agent is down -> HTTP 503"
  docker rm -f "$PREFIX-agent-main" >/dev/null
  run_agent main
  wait_for "agent axis available again" 60 axis_is "$SERVER" agent available
  ok "agent restarted (same state volume): reconnected, status.agent = available"

  # ---- frozen agent (SIGSTOP via docker pause): heartbeat timeout after 45 s
  docker pause "$PREFIX-agent-main" >/dev/null
  START=$SECONDS
  wait_for "agent axis unavailable (heartbeat timeout)" 90 axis_is "$SERVER" agent unavailable
  ELAPSED=$((SECONDS - START))
  ((ELAPSED >= 30 && ELAPSED <= 75)) || fail "frozen agent flagged unavailable after $ELAPSED s, expected about 45 s"
  ok "frozen agent: heartbeat timeout -> status.agent = unavailable after $ELAPSED s (expected ~45 s)"
  docker unpause "$PREFIX-agent-main" >/dev/null
  wait_for "agent axis available after unpause" 120 axis_is "$SERVER" agent available
  ok "unfrozen agent reconnected"

  # ---- supersede: a second process with the same identity
  run_agent main -dup
  wait_for "the first agent was superseded" 60 agent_log_has "$PREFIX-agent-main" 'SUPERSEDED\|superseded'
  ok "second agent with the same identity: the older stream got Disconnect(SUPERSEDED)"
  docker rm -f "$PREFIX-agent-main-dup" >/dev/null
  docker rm -f "$PREFIX-agent-main" >/dev/null
  run_agent main
  wait_for "agent available after the duplicate" 90 axis_is "$SERVER" agent available

  # ---- control plane restart: agents reconnect on their own
  info "restarting the API (same database): the agent must reconnect by itself"
  start_api "$DB_CONN" Aethera__Agents__PingSeconds=4
  login_owner
  wait_for "agent reconnects after an API restart" 120 session_live "$SERVER"
  ok "API restarted: agent reconnected (new gateway certificate, same CA pin)"

  # ---- the agent's own log stream (WARN and above are forwarded as LogChunks and acknowledged with LogFlowControl): stored without gaps
  agent_log_rows() { docker exec "$PREFIX-pg" psql -U aethera -d "e2e_$DB_NUM" -qAt -c "$1"; }
  wait_for "agent log chunks stored" 60 bash -c "[ \"\$(docker exec $PREFIX-pg psql -U aethera -d e2e_$DB_NUM -qAt -c \"select count(*) from log_chunks where stream_id like 'agent:%'\")\" -gt 0 ]"
  GAPS="$(agent_log_rows "select count(*) from (select stream_id, count(*) c, max(sequence) m, min(sequence) n from log_chunks where stream_id like 'agent:%' group by 1) t where c <> m or n <> 1")"
  [[ "$GAPS" == 0 ]] || fail "agent log streams have gaps or duplicates: $(agent_log_rows "select stream_id, count(*), min(sequence), max(sequence) from log_chunks where stream_id like 'agent:%' group by 1")"
  ok "agent log stream: $(agent_log_rows "select count(*) from log_chunks where stream_id like 'agent:%'") chunk(s) stored, sequences gap-free across the reconnects (Hello.active_log_stream_ids resume)"

  # ---- revoke
  api POST "/api/v1/servers/$SERVER/agent/reset?confirm=$SERVER_NAME"; expect 204 "POST agent/reset"
  wait_for "agent saw REVOKED" 30 agent_log_has "$PREFIX-agent-main" 'revoked'
  wait_for "agent axis not available" 30 bash -c "! curl -s -b '$JAR' '$API/api/v1/servers/$SERVER/status' | grep -q '\"agent\":{\"axis\":\"agent\",\"health\":\"available\"'"
  ok "agent reset: stream closed with REVOKED, agent logged it"
  CONN_BEFORE="$(grep -c 'agent.connected\|Hello' "$WORK/api.log" || true)"
  sleep 20
  ! axis_is "$SERVER" agent available || fail "revoked agent reconnected"
  [[ "$(agent_log_count "$PREFIX-agent-main" 'not reconnecting')" -ge 1 ]] || fail "agent did not announce that it stops retrying"
  docker ps --filter "name=$PREFIX-agent-main" --filter status=running -q | grep -q . || fail "the agent process exited (it should idle until re-enrolled)"
  ok "revoked agent stops retrying (process idles awaiting re-enrollment)"

  # ---- re-enroll the same state volume; the idle agent picks up the new identity
  new_join_token "$SERVER"
  docker stop "$PREFIX-agent-main" >/dev/null
  enroll_agent main "$TOKEN" "$ENDPOINT" "$PIN" || { cat "$WORK/enroll-main.log" >&2; fail "re-enrollment failed"; }
  docker start "$PREFIX-agent-main" >/dev/null
  wait_for "agent available after re-enrollment" 90 axis_is "$SERVER" agent available
  ok "re-enrolled with a fresh join token: agent available again"

  # ---- delete server closes the stream
  REVOKED_LINES="$(agent_log_count "$PREFIX-agent-main" 'not reconnecting')"
  api DELETE "/api/v1/servers/$SERVER?confirm=$SERVER_NAME"
  [[ "$STATUS" == 204 || "$STATUS" == 200 ]] || fail "DELETE /servers: HTTP $STATUS"
  wait_for "agent stops after the server was deleted" 60 bash -c "[ \$(docker logs '$PREFIX-agent-main' 2>&1 | grep -c 'not reconnecting') -gt $REVOKED_LINES ]"
  ! axis_is "$SERVER" agent available >/dev/null 2>&1 # the server is gone: the status endpoint answers 404
  ok "server deleted: the stream was closed with REVOKED and the agent stopped retrying"
  docker rm -f "$PREFIX-agent-main" >/dev/null 2>&1
}

# ========================================================================================================== phase: renew
phase_renew() {
  info "---- phase renew: 5-minute agent certificates, renewal window 3.6 minutes"
  new_database; start_api "$DB_CONN" Aethera__Agents__AgentCertificateDays=0.0035 Aethera__Agents__RenewBeforeDays=0.0025
  login_owner
  api POST /api/v1/servers '{"name":"e2e-renew","host":"host.docker.internal","roles":["worker"]}'; expect 201 "POST /servers"
  RSERVER="$(jqe .id)"
  new_join_token "$RSERVER"
  agent_setup renew
  enroll_agent renew "$TOKEN" "$ENDPOINT" "$PIN" || { cat "$WORK/enroll-renew.log" >&2; fail "enroll failed"; }
  run_agent renew
  wait_for "agent available" 60 axis_is "$RSERVER" agent available
  CERT1="$(docker run --rm -v "$PREFIX-renew-state:/s:ro" debian:12-slim sh -c 'cat /s/agent.crt 2>/dev/null | md5sum' )"
  wait_for "agent renewed its certificate (gateway rotation hint)" 200 agent_log_has "$PREFIX-agent-renew" 'renewing client certificate'
  ok "gateway sent CertRotationHint, agent started the renewal"
  sleep 8
  CERT2="$(docker run --rm -v "$PREFIX-renew-state:/s:ro" debian:12-slim sh -c 'cat /s/agent.crt 2>/dev/null | md5sum' )"
  [[ "$CERT1" != "$CERT2" ]] || fail "the certificate on disk did not change after renewal"
  wait_for "agent available on the new certificate" 60 axis_is "$RSERVER" agent available
  agent_log_has "$PREFIX-agent-renew" 'certificate renewal failed' && fail "renewal failed"
  ok "certificate replaced on disk; agent reconnected with the new certificate"
  wait_for "the superseded certificate was revoked" 60 bash -c "[ \"\$(docker exec $PREFIX-pg psql -U aethera -d e2e_$DB_NUM -qAt -c \"select count(*) from agent_certificates where server_id='$RSERVER' and revoked_at is not null\")\" -ge 1 ]"
  LIVE="$(docker exec "$PREFIX-pg" psql -U aethera -d "e2e_$DB_NUM" -qAt -c "select count(*) from agent_certificates where server_id='$RSERVER' and revoked_at is null")"
  [[ "$LIVE" == 1 ]] || fail "expected exactly one live certificate after renewal, found $LIVE"
  ok "renewal revoked the superseded certificate; exactly one live certificate remains"
  docker rm -f "$PREFIX-agent-renew" >/dev/null 2>&1
}

# ========================================================================================================= phase: upgrade
phase_upgrade() {
  info "---- phase upgrade: gateway requires agent >= 99.0.0"
  new_database; start_api "$DB_CONN" Aethera__Agents__MinAgentVersion=99.0.0
  login_owner
  api POST /api/v1/servers '{"name":"e2e-old","host":"host.docker.internal","roles":["worker"]}'; expect 201 "POST /servers"
  USERVER="$(jqe .id)"
  new_join_token "$USERVER"
  agent_setup upgrade
  enroll_agent upgrade "$TOKEN" "$ENDPOINT" "$PIN" || { cat "$WORK/enroll-upgrade.log" >&2; fail "enroll failed"; }
  run_agent upgrade
  wait_for "agent told to upgrade" 60 agent_log_has "$PREFIX-agent-upgrade" 'UPGRADE_REQUIRED\|requires a newer agent'
  ! axis_is "$USERVER" agent available || fail "an agent below the minimum version must not become available"
  ok "agent below MinAgentVersion gets Disconnect(UPGRADE_REQUIRED) and is not marked available"
  docker rm -f "$PREFIX-agent-upgrade" >/dev/null 2>&1
}

# ======================================================================================================== phase: restart
phase_restart() {
  info "---- phase restart: the API is killed without a goodbye five times; the agent must reconnect every time, a vanished agent must be noticed"
  new_database; start_api "$DB_CONN"
  login_owner
  api POST /api/v1/servers '{"name":"e2e-restart","host":"host.docker.internal","roles":["worker"]}'; expect 201 "POST /servers"
  XSERVER="$(jqe .id)"
  new_join_token "$XSERVER"
  agent_setup restart
  enroll_agent restart "$TOKEN" "$ENDPOINT" "$PIN" || { cat "$WORK/enroll-restart.log" >&2; fail "enroll failed"; }
  run_agent restart
  wait_for "agent connected" 60 session_live "$XSERVER"
  for i in 1 2 3 4 5; do
    docker restart "$PREFIX-agent-restart" >/dev/null # the agent is in its first seconds when the API goes away (fresh dial, no backoff state)
    stop_api
    start_api "$DB_CONN"
    login_owner
    START=$SECONDS
    wait_for "agent reconnects after API restart $i" 90 session_live "$XSERVER"
    ok "API kill/restart $i: agent reconnected after $((SECONDS - START)) s"
    agent_log_has "$PREFIX-agent-restart" 'TLS/certificate error' && fail "a control plane restart put the agent into the slow certificate-error retry: $(docker logs "$PREFIX-agent-restart" 2>&1 | grep 'TLS/certificate error' | tail -1 | cut -c1-400)"
  done
  # the agent vanishes while the API is down: the stored "connected" must not outlive the restart
  stop_api
  docker stop "$PREFIX-agent-restart" >/dev/null
  start_api "$DB_CONN"
  login_owner
  START=$SECONDS
  wait_for "stale 'connected' corrected" 150 axis_is "$XSERVER" agent unavailable
  ok "agent gone during an API outage: status.agent corrected to unavailable after $((SECONDS - START)) s"
  docker start "$PREFIX-agent-restart" >/dev/null
  wait_for "agent back" 120 session_live "$XSERVER"
  wait_for "agent axis available" 30 axis_is "$XSERVER" agent available
  ok "agent started again: connected"
  docker rm -f "$PREFIX-agent-restart" >/dev/null 2>&1
}

for p in $PHASES; do
  case "$p" in
    main) phase_main ;;
    renew) phase_renew ;;
    upgrade) phase_upgrade ;;
    restart) phase_restart ;;
    ui) source "$ROOT/deploy/ui-e2e-phase.sh"; phase_ui ;;
    *) fail "unknown phase $p" ;;
  esac
done

printf '\n[ ok ] all agent end-to-end checks passed\n'
