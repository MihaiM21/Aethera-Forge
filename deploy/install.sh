#!/usr/bin/env bash
# Installs Aethera (control plane, PostgreSQL, Redis and an agent for this machine) with Docker Compose.
#
#   curl -fsSL https://github.com/MihaiM21/aethera-forge/releases/latest/download/install.sh | sudo bash
#
# Options (each also as an environment variable):
#   --dir <path>              install directory (AETHERA_DIR, default /opt/aethera)
#   --version <v>             release to install, e.g. v0.1.0 (AETHERA_VERSION, default latest)
#   --host <name-or-ip>       what servers use to reach this machine's agent port (AETHERA_PUBLIC_HOST, default: detected)
#   --domain <name>           serve the UI on https://<name> through Aethera's proxy (AETHERA_DOMAIN)
#   --admin-email <email>     create the first owner now (AETHERA_ADMIN_EMAIL); otherwise open the URL and register
#   --admin-password <pw>     its password (AETHERA_ADMIN_PASSWORD); generated and printed once when only the email is given
#   --admin-name <name>       display name of that owner (AETHERA_ADMIN_NAME, default "Administrator")
#   --org-name <name>         organization name (AETHERA_ORG_NAME, default "Aethera")
#   --no-agent                do not enroll an agent for this machine
#   --compose-file <path>     use this compose file instead of downloading docker-compose.prod.yml (for development)
#   --http-port <n>           host port of the UI/API (AETHERA_HTTP_PORT, default 8080)
#   --grpc-port <n>           host port of the agent gateway (AETHERA_GRPC_PORT, default 9443)
#
# Needs: Linux with systemd, root, curl. Installs Docker (get.docker.com) when it is missing. Running it again upgrades in place:
# secrets in <dir>/.env are kept, never regenerated.
set -euo pipefail

REPO="${AETHERA_REPO:-MihaiM21/aethera-forge}"
DIR="${AETHERA_DIR:-/opt/aethera}"
VERSION="${AETHERA_VERSION:-latest}"
PUBLIC_HOST="${AETHERA_PUBLIC_HOST:-}"
DOMAIN="${AETHERA_DOMAIN:-}"
ADMIN_EMAIL="${AETHERA_ADMIN_EMAIL:-}"
ADMIN_PASSWORD="${AETHERA_ADMIN_PASSWORD:-}"
ADMIN_NAME="${AETHERA_ADMIN_NAME:-Administrator}"
ORG_NAME="${AETHERA_ORG_NAME:-Aethera}"
HTTP_PORT="${AETHERA_HTTP_PORT:-8080}"
GRPC_PORT="${AETHERA_GRPC_PORT:-9443}"
WITH_AGENT=1
COMPOSE_FILE_SRC=""
GENERATED_PASSWORD=0

say() { printf '==> %s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die() { printf 'install: %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --dir) DIR="${2:?--dir needs a value}"; shift 2 ;;
    --version) VERSION="${2:?--version needs a value}"; shift 2 ;;
    --host) PUBLIC_HOST="${2:?--host needs a value}"; shift 2 ;;
    --domain) DOMAIN="${2:?--domain needs a value}"; shift 2 ;;
    --admin-email) ADMIN_EMAIL="${2:?--admin-email needs a value}"; shift 2 ;;
    --admin-password) ADMIN_PASSWORD="${2:?--admin-password needs a value}"; shift 2 ;;
    --admin-name) ADMIN_NAME="${2:?--admin-name needs a value}"; shift 2 ;;
    --org-name) ORG_NAME="${2:?--org-name needs a value}"; shift 2 ;;
    --no-agent) WITH_AGENT=0; shift ;;
    --compose-file) COMPOSE_FILE_SRC="${2:?--compose-file needs a value}"; shift 2 ;;
    --http-port) HTTP_PORT="${2:?--http-port needs a value}"; shift 2 ;;
    --grpc-port) GRPC_PORT="${2:?--grpc-port needs a value}"; shift 2 ;;
    -h|--help) sed -n '2,23p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
done

# ---- checks -----------------------------------------------------------------------------------------------------------
[ "$(uname -s)" = Linux ] || die "Linux only"
[ "$(id -u)" -eq 0 ] || die "run as root (sudo)"
command -v curl >/dev/null 2>&1 || die "curl is required"
command -v openssl >/dev/null 2>&1 || command -v base64 >/dev/null 2>&1 || die "openssl or base64 is required"

random_b64_32() { head -c 32 /dev/urandom | base64 | tr -d '\n'; }
random_token() { head -c 24 /dev/urandom | base64 | tr -d '/+=\n' | head -c 24; }

# ---- Docker -----------------------------------------------------------------------------------------------------------
if ! command -v docker >/dev/null 2>&1; then
  say "Installing Docker (get.docker.com)"
  curl -fsSL https://get.docker.com | sh
fi
systemctl enable --now docker >/dev/null 2>&1 || true
docker compose version >/dev/null 2>&1 || die "Docker Compose v2 is required (the docker-compose-plugin package)"

# ---- files ------------------------------------------------------------------------------------------------------------
mkdir -p "$DIR"
chmod 0700 "$DIR"
ENV_FILE="$DIR/.env"
COMPOSE="$DIR/docker-compose.yml"

if [ -n "$COMPOSE_FILE_SRC" ]; then
  [ -f "$COMPOSE_FILE_SRC" ] || die "no such compose file: $COMPOSE_FILE_SRC"
  cp "$COMPOSE_FILE_SRC" "$COMPOSE"
else
  if [ "$VERSION" = latest ]; then base="https://github.com/$REPO/releases/latest/download"
  else base="https://github.com/$REPO/releases/download/$VERSION"; fi
  say "Downloading docker-compose.prod.yml ($VERSION)"
  curl -fsSL "$base/docker-compose.prod.yml" -o "$COMPOSE.tmp"
  mv "$COMPOSE.tmp" "$COMPOSE"
fi

# The .env keeps the generated secrets. An existing one is the source of truth: re-running must never rotate the master key or the
# database password (that would lock the installation out of its own data).
env_get() { [ -f "$ENV_FILE" ] && sed -n "s/^$1=//p" "$ENV_FILE" | tail -n1 || true; }
env_set() {
  touch "$ENV_FILE"; chmod 0600 "$ENV_FILE"
  if grep -q "^$1=" "$ENV_FILE"; then
    # Written through a temp file: values may contain characters sed would treat specially.
    awk -v k="$1" -v v="$2" 'BEGIN{FS=OFS="="} $1==k {print k "=" v; next} {print}' "$ENV_FILE" > "$ENV_FILE.tmp" && mv "$ENV_FILE.tmp" "$ENV_FILE"
    chmod 0600 "$ENV_FILE"
  else
    printf '%s=%s\n' "$1" "$2" >> "$ENV_FILE"
  fi
}

[ -n "$(env_get AETHERA_DB_PASSWORD)" ] || env_set AETHERA_DB_PASSWORD "$(random_token)"
if [ -z "$(env_get AETHERA_MASTER_KEY)" ]; then
  env_set AETHERA_MASTER_KEY "$(random_b64_32)"
  say "Generated the master key in $ENV_FILE. It encrypts every stored secret: back this file up."
fi

if [ -z "$PUBLIC_HOST" ]; then PUBLIC_HOST="$(env_get AETHERA_PUBLIC_HOST)"; fi
if [ -z "$PUBLIC_HOST" ] && [ -n "$DOMAIN" ]; then PUBLIC_HOST="$DOMAIN"; fi
if [ -z "$PUBLIC_HOST" ]; then
  PUBLIC_HOST="$(curl -fsS --max-time 5 https://api.ipify.org 2>/dev/null || true)"
  [ -n "$PUBLIC_HOST" ] || PUBLIC_HOST="$(hostname -I 2>/dev/null | awk '{print $1}')"
  [ -n "$PUBLIC_HOST" ] || PUBLIC_HOST=localhost
  say "Using $PUBLIC_HOST as the address servers reach this machine at (override with --host)."
fi
env_set AETHERA_PUBLIC_HOST "$PUBLIC_HOST"
[ -z "$DOMAIN" ] || env_set AETHERA_DOMAIN "$DOMAIN"
env_set AETHERA_VERSION "$VERSION"
env_set AETHERA_HTTP_PORT "$HTTP_PORT"
env_set AETHERA_GRPC_PORT "$GRPC_PORT"

dc() { docker compose -f "$COMPOSE" --env-file "$ENV_FILE" "$@"; }

# ---- start ------------------------------------------------------------------------------------------------------------
say "Pulling images"
dc pull --quiet postgres redis aethera || warn "could not pull every image (building locally, or offline); continuing"
say "Starting the control plane"
dc up -d postgres redis aethera

BASE="http://127.0.0.1:$HTTP_PORT"
say "Waiting for the control plane"
ready=0
for _ in $(seq 1 90); do
  if curl -fsS --max-time 3 "$BASE/ready" >/dev/null 2>&1; then ready=1; break; fi
  sleep 2
done
[ "$ready" -eq 1 ] || { dc logs --tail 40 aethera >&2 || true; die "the control plane did not become ready (docker compose -f $COMPOSE logs aethera)"; }

# ---- owner and local agent --------------------------------------------------------------------------------------------
JAR="$(mktemp)"
trap 'rm -f "$JAR"' EXIT
json_field() { grep -o "\"$1\":\"[^\"]*\"" | head -n1 | cut -d'"' -f4; }

setup_required() { curl -fsS "$BASE/api/v1/auth/setup" | grep -q '"setupRequired":true'; }

api() { # api METHOD PATH [JSON]
  local method="$1" path="$2" body="${3:-}"
  local csrf; csrf="$(curl -fsS -b "$JAR" "$BASE/api/v1/auth/csrf" | json_field token)"
  curl -fsS -X "$method" -b "$JAR" -H "X-CSRF-Token: $csrf" -H 'Content-Type: application/json' ${body:+-d "$body"} "$BASE/api/v1$path"
}

if [ -n "$ADMIN_EMAIL" ]; then
  if setup_required; then
    if [ -z "$ADMIN_PASSWORD" ]; then ADMIN_PASSWORD="$(random_token)$(random_token | head -c 6)"; GENERATED_PASSWORD=1; fi
    say "Creating the first owner ($ADMIN_EMAIL)"
    curl -fsS -c "$JAR" -X POST -H 'Content-Type: application/json' "$BASE/api/v1/auth/setup" \
      -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\",\"displayName\":\"$ADMIN_NAME\",\"organizationName\":\"$ORG_NAME\"}" >/dev/null \
      || die "could not create the owner (password too short or weak? the API's message is above)"
  else
    warn "setup is already complete; skipping the owner and the local agent"
    WITH_AGENT=0
  fi
else
  WITH_AGENT=0
fi

if [ "$WITH_AGENT" -eq 1 ]; then
  if [ -s /var/lib/aethera/enrollment.json ]; then
    say "This machine's agent is already enrolled"
    dc --profile agent up -d agent
  else
    say "Enrolling an agent for this machine"
    server="$(api POST /servers "{\"name\":\"local\",\"host\":\"$PUBLIC_HOST\",\"roles\":[\"master\",\"build\"]}")" || die "could not create the server record"
    server_id="$(printf '%s' "$server" | json_field id)"
    [ -n "$server_id" ] || die "unexpected response creating the server: $server"
    jt="$(api POST "/servers/$server_id/join-tokens" '{"ttlMinutes":30}')" || die "could not create a join token"
    env_set AETHERA_JOIN_TOKEN "$(printf '%s' "$jt" | json_field token)"
    env_set AETHERA_CA_SHA256 "$(printf '%s' "$jt" | json_field caFingerprintSha256)"
    env_set AETHERA_AGENT_ENDPOINT "127.0.0.1:$GRPC_PORT"
    mkdir -p /var/lib/aethera
    dc --profile agent up -d agent
    # The token is single use and only needed for the first start: take it out of the .env again.
    sleep 5
    env_set AETHERA_JOIN_TOKEN ""
  fi
fi

# ---- done -------------------------------------------------------------------------------------------------------------
if [ -n "$DOMAIN" ]; then URL="https://$DOMAIN (after the proxy is set up and DNS points here)"; else URL="http://$PUBLIC_HOST:$HTTP_PORT"; fi
echo
say "Aethera is running."
echo "    URL:         $URL"
echo "    Directory:   $DIR  (compose file and .env with the generated secrets; back up .env)"
if [ -n "$ADMIN_EMAIL" ]; then
  echo "    Owner:       $ADMIN_EMAIL"
  [ "$GENERATED_PASSWORD" -eq 1 ] && echo "    Password:    $ADMIN_PASSWORD   (shown once; change it in Settings)"
else
  echo "    Next:        open the URL and register: the first account becomes the owner."
fi
echo "    Manage:      docker compose -f $COMPOSE --env-file $ENV_FILE ps | logs | pull && up -d"
