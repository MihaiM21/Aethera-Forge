#!/usr/bin/env bash
# End to end check of WP5.1 and WP5.2 on a throw-away "host": a Docker-in-Docker container is the machine, install.sh installs
# Aethera on it from the repository's own files, and the one-click updater is exercised for real.
#
#   bash deploy/install-e2e.sh
#
# What it proves:
#   1. install.sh: generates secrets, starts PostgreSQL, Redis and the control plane, creates the owner and enrolls this machine's agent
#      (also: sign-in over plain http to a non-local Host gets a usable cookie, `aethera reset-password` works end to end)
#   2. /install-agent.sh is served and validates its input; the local agent connects, in the host network namespace
#      (also: it reconnects within a minute after `docker restart` of the control plane, and a template PostgreSQL service deploys
#      healthy, i.e. the agent's TCP probe reaches containers on environment networks)
#   3. maintenance: "Run now" writes a real pg_dump backup (the client in the image matches the server)
#   4. update through the UI endpoint: backup, pull, restart on the new version, agent follows, data intact
#   5. an update whose images cannot be pulled changes nothing
#   6. an update whose new version does not start is rolled back, database restored from the pre-update backup
#
# Needs Docker. Builds the images when they are missing (a few minutes the first time). Environment:
#   E2E_V1 / E2E_V2 / E2E_V3 / E2E_V4   versions used for the images (default 0.1.0 0.2.0 0.3.0 0.4.0); 0.3.0 is never built
#   E2E_KEEP=1                          leave the Docker-in-Docker container running afterwards
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# docker.exe (Git Bash on Windows) needs Windows paths for what it reads from the host; elsewhere pwd -W does not exist.
WROOT="$(cd "$ROOT" && { pwd -W 2>/dev/null || pwd; })"
V1="${E2E_V1:-0.1.0}"; V2="${E2E_V2:-0.2.0}"; V3="${E2E_V3:-0.3.0}"; V4="${E2E_V4:-0.4.0}"
DIND="aethera-install-e2e"
REPO_SLUG="MihaiM21/aethera-forge"
EMAIL="owner@e2e.test"

ok() { printf '[ ok ] %s\n' "$*"; }
info() { printf '[ .. ] %s\n' "$*"; }
fail() { printf '[fail] %s\n' "$*" >&2; dump_logs || true; exit 1; }
D() { docker exec "$DIND" "$@"; }
dsh() { docker exec "$DIND" bash -c "$1"; }

dump_logs() {
  echo "---- compose ps"; D bash -c 'cd /opt/aethera && docker compose -p aethera --project-directory /opt/aethera -f docker-compose.yml --env-file .env ps -a' 2>&1 | tail -15 || true
  echo "---- aethera logs"; D docker logs aethera-aethera-1 2>&1 | grep -v 'Microsoft.EntityFrameworkCore\|^      ' | tail -40 || true
  echo "---- updater logs"; D docker logs --tail 40 aethera-updater-1 2>&1 | tail -40 || true
}

# jq runs inside the host container (it is installed there, not necessarily on the machine running this script); exported so the
# bash -c snippets of wait_for see it too.
export DIND
jq() { docker exec -i "$DIND" jq "$@"; }
export -f jq

cleanup() { if [ "${E2E_KEEP:-}" != 1 ]; then docker rm -f "$DIND" >/dev/null 2>&1 || true; fi; }
trap cleanup EXIT

# ---------------------------------------------------------------------------------------------------------------- images
build() { # build <component> <version>
  local dockerfile="$WROOT/deploy/docker/Dockerfile.$1"; local image="aethera-local/$1:$2"
  if docker image inspect "$image" >/dev/null 2>&1; then return; fi
  info "building $image"
  docker build -q -f "$dockerfile" --build-arg "VERSION=$2" -t "$image" "$WROOT" >/dev/null
}
for v in "$V1" "$V2"; do for c in control-plane agent updater; do build "$c" "$v"; done; done
# 0.4.0 is a release whose control plane does not start: the same image with a failing entrypoint.
if ! docker image inspect "aethera-local/control-plane:$V4" >/dev/null 2>&1; then
  printf 'FROM aethera-local/control-plane:%s\nENTRYPOINT ["sh","-c","echo this release is broken; exit 1"]\n' "$V2" | docker build -q -t "aethera-local/control-plane:$V4" - >/dev/null
fi
docker tag "aethera-local/agent:$V2" "aethera-local/agent:$V4"
docker tag "aethera-local/updater:$V2" "aethera-local/updater:$V4"
ok "images: control-plane, agent and updater at $V1 and $V2 (and a broken $V4)"

# ---------------------------------------------------------------------------------------------------------------- host
docker rm -f "$DIND" >/dev/null 2>&1 || true
docker run -d --privileged --name "$DIND" -e DOCKER_TLS_CERTDIR= docker:27-dind >/dev/null
for _ in $(seq 1 60); do D docker info >/dev/null 2>&1 && break; sleep 1; done
D docker info >/dev/null 2>&1 || fail "the Docker-in-Docker daemon did not start"
D apk add --no-cache bash curl openssl coreutils jq >/dev/null
for image in postgres:17 redis:7 busybox "aethera-local/control-plane:$V1" "aethera-local/control-plane:$V2" "aethera-local/control-plane:$V4" \
             "aethera-local/agent:$V1" "aethera-local/agent:$V2" "aethera-local/agent:$V4" "aethera-local/updater:$V1" "aethera-local/updater:$V2" "aethera-local/updater:$V4"; do
  docker image inspect "$image" >/dev/null 2>&1 || docker pull -q "$image" >/dev/null
done
docker save postgres:17 redis:7 busybox aethera-local/control-plane aethera-local/agent aethera-local/updater | docker exec -i "$DIND" docker load >/dev/null
docker cp "$WROOT/deploy" "$DIND:/deploy"
ok "a fresh Linux host with Docker is ready"

# ---------------------------------------------------------------------------------------------------------------- 1. install
# A tiny release server stands in for GitHub: the API's update check is pointed at it (Aethera:Updates:ApiBaseUrl).
release() { # release <version>
  dsh "mkdir -p /www/repos/$REPO_SLUG/releases && cat > /www/repos/$REPO_SLUG/releases/latest <<EOF
{\"tag_name\":\"v$1\",\"html_url\":\"https://example.test/r/$1\",\"body\":\"Release notes for $1\",\"prerelease\":false,\"draft\":false,\"assets\":[]}
EOF"
}
release "$V2"

info "running install.sh"
INSTALL_OUT="$(docker exec \
  -e AETHERA_IMAGE=aethera-local/control-plane -e AETHERA_AGENT_IMAGE=aethera-local/agent -e AETHERA_UPDATER_IMAGE=aethera-local/updater \
  -e AETHERA_UPDATE_API_URL=http://releases -e AETHERA_HEALTH_TIMEOUT=25 \
  "$DIND" bash /deploy/install.sh --compose-file /deploy/docker-compose.prod.yml --dir /opt/aethera --version "$V1" --host 127.0.0.1 \
  --admin-email "$EMAIL" --http-port 8080 --grpc-port 9443 2>&1)" || { echo "$INSTALL_OUT"; fail "install.sh failed"; }
echo "$INSTALL_OUT" | sed 's/^/       /' | tail -14
PASSWORD="$(echo "$INSTALL_OUT" | sed -n 's/^ *Password: *\([^ ]*\).*/\1/p')"
[ -n "$PASSWORD" ] || fail "install.sh did not print the generated owner password"
ok "install.sh finished; generated owner password captured"
# The release server joins the stack's network once compose has created it (compose refuses a network it did not create itself).
D docker run -d --name releases --network aethera_default --network-alias releases -v /www:/www busybox httpd -f -p 80 -h /www >/dev/null

dsh 'test "$(stat -c %a /opt/aethera/.env)" = 600' || fail ".env is not mode 0600"
dsh 'grep -q "^AETHERA_MASTER_KEY=.\{40,\}" /opt/aethera/.env && grep -q "^AETHERA_DB_PASSWORD=.\{16,\}" /opt/aethera/.env' || fail "secrets were not generated"
dsh 'grep -q "^AETHERA_JOIN_TOKEN=$" /opt/aethera/.env' || fail "the single-use join token stayed in the .env"
dsh 'test -x /usr/local/bin/aethera' || fail "the aethera command was not installed"
ok ".env holds generated secrets with mode 0600, the join token is gone, aethera command installed"

# ---------------------------------------------------------------------------------------------------------------- API helpers
api() { # api METHOD PATH [JSON]  -> body on stdout; session in /tmp/jar of the host
  local method="$1" path="$2" body="${3:-}" csrf
  csrf="$(D curl -fsS -b /tmp/jar http://127.0.0.1:8080/api/v1/auth/csrf | jq -r .token)"
  if [ -n "$body" ]; then D curl -sS -X "$method" -b /tmp/jar -H "X-CSRF-Token: $csrf" -H 'Content-Type: application/json' -d "$body" "http://127.0.0.1:8080/api/v1$path"
  else D curl -sS -X "$method" -b /tmp/jar -H "X-CSRF-Token: $csrf" "http://127.0.0.1:8080/api/v1$path"; fi
}
D curl -fsS -c /tmp/jar -X POST -H 'Content-Type: application/json' -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}" http://127.0.0.1:8080/api/v1/auth/login >/dev/null \
  || fail "the owner cannot sign in with the generated password"
ok "the owner signs in with the generated password"

wait_for() { # wait_for <description> <timeout> <command...>
  local what="$1" timeout="$2"; shift 2
  local n=0
  until "$@" >/dev/null 2>&1; do
    n=$((n + 3)); [ "$n" -le "$timeout" ] || fail "timed out waiting for $what"; sleep 3
  done
}
agent_connected() { api GET /servers | jq -e '.items[] | select(.name=="local") | select(.status.agent.status=="connected")' >/dev/null; }
wait_for "the local agent to connect" 90 agent_connected
ok "the local agent enrolled and is connected"

[ "$(api GET /system/version | jq -r .current)" = "$V1" ] || fail "the control plane does not report version $V1"
ok "the control plane reports version $V1"

# ---------------------------------------------------------------------------------------------------------------- sign-in over plain HTTP
# A browser drops Secure and __Host- cookies from http://<ip>:8080; curl on 127.0.0.1 would not notice. The Host header stands in for the
# address of a real server: the response must carry the plain, non-Secure cookie, and the cookie must work on that address too.
hdrs="$(D curl -sS -D - -o /dev/null -c /tmp/jar-ip -H 'Host: 203.0.113.5:8080' -X POST -H 'Content-Type: application/json' \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}" http://127.0.0.1:8080/api/v1/auth/login | tr -d '\r')"
cookie="$(echo "$hdrs" | grep -i '^set-cookie:' || true)"
echo "$cookie" | grep -q '^[Ss]et-[Cc]ookie: aethera_session=' || fail "login over plain http did not set the plain aethera_session cookie: $cookie"
echo "$cookie" | grep -qi 'secure' && fail "the plain-http session cookie is marked Secure (browsers would drop it): $cookie"
echo "$cookie" | grep -qi 'httponly' && echo "$cookie" | grep -qi 'samesite=lax' || fail "the session cookie lost HttpOnly or SameSite=Lax: $cookie"
code="$(D curl -s -o /dev/null -w '%{http_code}' -H 'Host: 203.0.113.5:8080' -H 'Cookie: aethera_session='"$(echo "$cookie" | sed -n 's/^[Ss]et-[Cc]ookie: aethera_session=\([^;]*\).*/\1/p')" http://127.0.0.1:8080/api/v1/auth/me)"
[ "$code" = 200 ] || fail "the plain-http session cookie is not accepted on a non-loopback host (answered $code)"
hdrs="$(D curl -sS -D - -o /dev/null -H 'X-Forwarded-Proto: https' -H 'Host: aethera.example.test' -X POST -H 'Content-Type: application/json' \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}" http://127.0.0.1:8080/api/v1/auth/login | tr -d '\r')"
echo "$hdrs" | grep -i '^set-cookie:' | grep -q '^[Ss]et-[Cc]ookie: __Host-aethera_session=.*[Ss]ecure' \
  || fail "login behind a TLS-terminating proxy (X-Forwarded-Proto: https) did not set the __Host- Secure cookie"
ok "sign-in over plain http to a non-local host gets a usable non-Secure cookie; over https (forwarded) the __Host- Secure one"

# ---------------------------------------------------------------------------------------------------------------- aethera reset-password
out="$(dsh 'AETHERA_DIR=/opt/aethera aethera reset-password OWNER@e2e.test 2>/dev/null')" || fail "aethera reset-password failed"
generated="$(echo "$out" | tail -n1 | tr -d '\r ')"
[ "${#generated}" -ge 12 ] || fail "aethera reset-password printed no usable password: $out"
code="$(D curl -s -o /dev/null -w '%{http_code}' -b /tmp/jar http://127.0.0.1:8080/api/v1/auth/me)"
[ "$code" = 401 ] || fail "the old session survived reset-password (answered $code)"
code="$(D curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}" http://127.0.0.1:8080/api/v1/auth/login)"
[ "$code" = 401 ] || fail "the old password still works after reset-password (answered $code)"
# a password given on stdin becomes the one the rest of this script uses
PASSWORD="e2e chosen password 42"
dsh "printf '%s\n' '$PASSWORD' | AETHERA_DIR=/opt/aethera aethera reset-password $EMAIL --stdin >/dev/null 2>&1" || fail "aethera reset-password --stdin failed"
D curl -fsS -c /tmp/jar -X POST -H 'Content-Type: application/json' -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}" http://127.0.0.1:8080/api/v1/auth/login >/dev/null \
  || fail "the owner cannot sign in with the password given to reset-password"
api GET "/audit-log?limit=100" | jq -e '[.items[] | select(.action=="users.password_reset" and .actorType=="system")] | length >= 2' >/dev/null \
  || fail "reset-password left no system-actor audit events"
ok "aethera reset-password: new password printed once (or read from stdin), old password and sessions dead, audited as the system"

# ---------------------------------------------------------------------------------------------------------------- 2. install-agent.sh
code="$(D curl -s -o /tmp/ia.sh -w '%{http_code}' http://127.0.0.1:8080/install-agent.sh)"
[ "$code" = 200 ] && dsh 'head -1 /tmp/ia.sh | grep -q "^#!/usr/bin/env bash"' || fail "/install-agent.sh is not served"
dsh 'printf "#!/bin/sh\nexit 0\n" > /usr/local/bin/systemctl && chmod +x /usr/local/bin/systemctl'
# This host's agent is enrolled already (a re-run upgrades without a token): hide the enrollment for the negative checks.
dsh 'mv /var/lib/aethera/enrollment.json /var/lib/aethera/enrollment.json.hidden'
out="$(dsh 'bash /tmp/ia.sh 2>&1 || true')"
echo "$out" | grep -q "run as root\|--token is required" || fail "install-agent.sh did not reject a missing token: $out"
out="$(dsh 'bash /tmp/ia.sh --token t --endpoint e:1 --ca-sha256 abc --download-url http://insecure.example 2>&1 || true')"
echo "$out" | grep -q "must use https" || fail "install-agent.sh accepted a plain-http download URL: $out"
dsh 'mv /var/lib/aethera/enrollment.json.hidden /var/lib/aethera/enrollment.json'
ok "/install-agent.sh is served, refuses a missing token and an http download"

# ---------------------------------------------------------------------------------------------------------------- 3. maintenance
report="$(api POST /system/maintenance/run)"
backup="$(echo "$report" | jq -r .backupTaken)"
[ "$backup" != null ] && [ -n "$backup" ] || fail "maintenance wrote no backup: $report"
[ "$(echo "$report" | jq '.errors | length')" = 0 ] || fail "maintenance reported errors: $report"
dsh "docker exec aethera-aethera-1 test -s /data/backups/$backup" || fail "the backup file $backup is missing or empty"
dsh "docker exec aethera-aethera-1 pg_restore --list /data/backups/$backup | grep -q 'TABLE DATA.*users'" || fail "the backup does not contain the users table"
api GET /system/maintenance | jq -e --arg b "$backup" '.backups[] | select(.name==$b)' >/dev/null || fail "the backup is not listed by GET /system/maintenance"
ok "maintenance wrote a real pg_dump backup ($backup) that pg_restore can read, and it is listed"

# ---------------------------------------------------------------------------------------------------------------- 3b. local agent networking
# The agent of this machine runs in the host network namespace: it must survive a restart of the control plane container (a container
# sharing that container's namespace would stay cut off) and reach containers on environment networks (template health checks).
[ "$(D docker inspect --format '{{.HostConfig.NetworkMode}}' aethera-agent-1)" = host ] || fail "the local agent container is not in the host network namespace"
control_ready() { D curl -fs http://127.0.0.1:8080/ready; }
D docker restart aethera-aethera-1 >/dev/null
restarted="$(date +%s)"
wait_for "the control plane after docker restart" 90 control_ready
wait_for "the local agent to reconnect after the control plane restarted" 60 agent_connected
ok "after 'docker restart' of the control plane the local agent is connected again within $(( $(date +%s) - restarted )) s (limit 60 s after /ready)"

local_server="$(api GET /servers | jq -r '.items[] | select(.name=="local") | .id')"
pg_project="$(api POST /projects '{"name":"net e2e"}')"
pg_env="$(echo "$pg_project" | jq -r '.environments[0].id')"
pg_service="$(api POST /services "{\"name\":\"net-db\",\"environmentId\":\"$pg_env\",\"serverId\":\"$local_server\",\"templateKey\":\"postgres\"}" | jq -r .id)"
[ -n "$pg_service" ] && [ "$pg_service" != null ] || fail "could not create a PostgreSQL service from the template"
pg_deployment="$(api POST "/services/$pg_service/deployments" '{}' | jq -r .id)"
[ -n "$pg_deployment" ] && [ "$pg_deployment" != null ] || fail "could not start the PostgreSQL deployment"
n=0; pg_status=""
while [ "$n" -le 300 ]; do
  pg_status="$(api GET "/deployments/$pg_deployment" | jq -r .status)"
  case "$pg_status" in running|failed|cancelled) break ;; esac
  sleep 3; n=$((n + 3))
done
if [ "$pg_status" != running ]; then
  api GET "/deployments/$pg_deployment" | jq -c '{status, failedStep, failureCode, failureMessage}' >&2 || true
  fail "the PostgreSQL service did not become healthy (status: $pg_status): the agent cannot reach the container on the environment network"
fi
ok "a PostgreSQL service from the template deployed healthy: the TCP probe reaches it through its environment network"

# ---------------------------------------------------------------------------------------------------------------- 4. update
project="$(api POST /projects '{"name":"survives the update"}' | jq -r .id)"
[ -n "$project" ] && [ "$project" != null ] || fail "could not create the marker project"
update_state() { api GET /system/version 2>/dev/null | jq -r '.updater.state // "unreachable"'; }
wait_state() { # wait_state <state> <timeout>
  local want="$1" timeout="$2" n=0 got=""
  while [ "$n" -le "$timeout" ]; do
    got="$(update_state || true)"
    [ "$got" = "$want" ] && return 0
    sleep 3; n=$((n + 3))
  done
  fail "the updater never reached '$want' (last state: $got)"
}

api POST /system/version/check | jq -e --arg v "$V2" '.updateAvailable == true and .latest.version == $v' >/dev/null || fail "the update to $V2 is not offered"
ok "the control plane sees release $V2 as an update"
wait_for "the updater container" 60 bash -c "docker exec $DIND curl -s -b /tmp/jar http://127.0.0.1:8080/api/v1/system/version | jq -e '.updater.available == true'"
ok "the updater container is running and reports itself"

code="$(D curl -s -o /dev/null -w '%{http_code}' -b /tmp/jar -X POST -H "X-CSRF-Token: $(D curl -fsS -b /tmp/jar http://127.0.0.1:8080/api/v1/auth/csrf | jq -r .token)" -H 'Content-Type: application/json' -d '{}' http://127.0.0.1:8080/api/v1/system/update)"
[ "$code" = 202 ] || fail "POST /system/update answered $code instead of 202"
info "updating to $V2 (backup, pull, restart)"
wait_state "done" 240
wait_for "the control plane on $V2" 120 bash -c "[ \"\$(docker exec $DIND curl -s -b /tmp/jar http://127.0.0.1:8080/api/v1/system/version | jq -r .current)\" = $V2 ]"
ok "the control plane now runs $V2"
dsh "grep -q '^AETHERA_VERSION=$V2\$' /opt/aethera/.env" || fail "the .env still names the old version"
dsh 'ls /opt/aethera/.env.before-* >/dev/null' || fail "the previous .env was not kept"
dsh 'docker exec aethera-aethera-1 ls /data/backups | grep -q "^aethera-pre-update-"' || fail "no pre-update backup was written"
api GET "/projects/$project" | jq -e '.name == "survives the update"' >/dev/null || fail "the data did not survive the update"
wait_for "the local agent to reconnect on the new version" 120 agent_connected
dsh "docker ps --format '{{.Image}}' | grep -q 'aethera-local/agent:$V2'" || fail "the local agent was not replaced by the new version"
ok "update $V1 -> $V2: pre-update backup, new images, data intact, the local agent reconnected on $V2"
wait_for "the updater to be replaced" 90 bash -c "docker exec $DIND docker ps --format '{{.Image}}' | grep -q 'aethera-local/updater:$V2'"
wait_for "the new updater to be alive" 90 bash -c "docker exec $DIND curl -s -b /tmp/jar http://127.0.0.1:8080/api/v1/system/version | jq -e '.updater.available == true'"
ok "the updater replaced itself and is alive again"

# ---------------------------------------------------------------------------------------------------------------- 5. images that cannot be pulled
release "$V3"
api POST /system/version/check | jq -e --arg v "$V3" '.latest.version == $v' >/dev/null || fail "release $V3 is not seen"
api POST /system/update '{}' >/dev/null
wait_state failed 90
msg="$(api GET /system/version | jq -r .updater.message)"
echo "$msg" | grep -q "could not be pulled" || fail "unexpected failure message: $msg"
[ "$(api GET /system/version | jq -r .current)" = "$V2" ] || fail "a failed pull changed the running version"
dsh "grep -q '^AETHERA_VERSION=$V2\$' /opt/aethera/.env" || fail "a failed pull left the .env on the wrong version"
ok "an update whose images cannot be pulled changes nothing ($msg)"

# ---------------------------------------------------------------------------------------------------------------- 6. rollback
release "$V4"
api POST /system/version/check | jq -e --arg v "$V4" '.latest.version == $v' >/dev/null || fail "release $V4 is not seen"
api POST /projects '{"name":"made just before the broken update"}' >/dev/null
api POST /system/update '{}' >/dev/null
info "updating to the broken $V4: the updater must notice and roll back"
wait_state failed 240
wait_for "the control plane to be back" 120 bash -c "docker exec $DIND curl -fs http://127.0.0.1:8080/ready"
# the session cookie belongs to the restored database: sign in again
D curl -fsS -c /tmp/jar -X POST -H 'Content-Type: application/json' -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}" http://127.0.0.1:8080/api/v1/auth/login >/dev/null || fail "cannot sign in after the rollback"
msg="$(api GET /system/version | jq -r .updater.message)"
echo "$msg" | grep -q "rolled back to $V2" || fail "unexpected rollback message: $msg"
[ "$(api GET /system/version | jq -r .current)" = "$V2" ] || fail "after the rollback the version is not $V2"
dsh "grep -q '^AETHERA_VERSION=$V2\$' /opt/aethera/.env" || fail "the rollback left the .env on the broken version"
api GET "/projects/$project" | jq -e '.name == "survives the update"' >/dev/null || fail "the data is gone after the rollback"
api GET "/projects?limit=100" | jq -e '[.items[].name] | index("made just before the broken update") != null' >/dev/null || fail "the pre-update data is gone after the rollback"
wait_for "the local agent after the rollback" 120 agent_connected
ok "a new version that does not start is rolled back: $V2 runs again, data restored from the pre-update backup ($msg)"

# ---------------------------------------------------------------------------------------------------------------- CLI
status="$(dsh 'AETHERA_DIR=/opt/aethera aethera status 2>&1')"
echo "$status" | grep -q "Version $V2" || fail "'aethera status' does not show $V2: $status"
dsh 'AETHERA_DIR=/opt/aethera aethera backup >/dev/null 2>&1 && ls /opt/aethera/backups/aethera-manual-*.dump >/dev/null' || fail "'aethera backup' wrote no file"
ok "the aethera command reports status and writes backups"

echo
ok "install and update end to end: all checks passed"
