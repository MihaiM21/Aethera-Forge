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
#   --acme-email <email>      Let's Encrypt account for HTTPS certificates (AETHERA_ACME_EMAIL); without it the proxy serves HTTP only
#   --admin-email <email>     create the first owner now (AETHERA_ADMIN_EMAIL); otherwise open the URL and register
#   --admin-password <pw>     its password (AETHERA_ADMIN_PASSWORD); generated and printed once when only the email is given
#   --admin-name <name>       display name of that owner (AETHERA_ADMIN_NAME, default "Administrator")
#   --org-name <name>         organization name (AETHERA_ORG_NAME, default "Aethera")
#   --no-agent                do not enroll an agent for this machine
#   --from-source [<ref>]     build the images on this machine from the repository (branch or tag, default master) instead of pulling a
#                             release. Used automatically while no release is published. No updater: re-run this script to update.
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
ACME_EMAIL="${AETHERA_ACME_EMAIL:-}"
ADMIN_EMAIL="${AETHERA_ADMIN_EMAIL:-}"
ADMIN_PASSWORD="${AETHERA_ADMIN_PASSWORD:-}"
ADMIN_NAME="${AETHERA_ADMIN_NAME:-Administrator}"
ORG_NAME="${AETHERA_ORG_NAME:-Aethera}"
HTTP_PORT="${AETHERA_HTTP_PORT:-8080}"
GRPC_PORT="${AETHERA_GRPC_PORT:-9443}"
WITH_AGENT=1
COMPOSE_FILE_SRC=""
FROM_SOURCE=0
SOURCE_REF="${AETHERA_SOURCE_REF:-master}"
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
    --acme-email) ACME_EMAIL="${2:?--acme-email needs a value}"; shift 2 ;;
    --admin-email) ADMIN_EMAIL="${2:?--admin-email needs a value}"; shift 2 ;;
    --admin-password) ADMIN_PASSWORD="${2:?--admin-password needs a value}"; shift 2 ;;
    --admin-name) ADMIN_NAME="${2:?--admin-name needs a value}"; shift 2 ;;
    --org-name) ORG_NAME="${2:?--org-name needs a value}"; shift 2 ;;
    --no-agent) WITH_AGENT=0; shift ;;
    --from-source) FROM_SOURCE=1; if [ $# -gt 1 ] && [ "${2#-}" = "$2" ]; then SOURCE_REF="$2"; shift; fi; shift ;;
    --compose-file) COMPOSE_FILE_SRC="${2:?--compose-file needs a value}"; shift 2 ;;
    --http-port) HTTP_PORT="${2:?--http-port needs a value}"; shift 2 ;;
    --grpc-port) GRPC_PORT="${2:?--grpc-port needs a value}"; shift 2 ;;
    -h|--help) sed -n '2,26p' "$0"; exit 0 ;;
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

release_base() {
  if [ "$VERSION" = latest ]; then echo "https://github.com/$REPO/releases/latest/download"
  else echo "https://github.com/$REPO/releases/download/$VERSION"; fi
}

if [ -n "$COMPOSE_FILE_SRC" ]; then
  [ -f "$COMPOSE_FILE_SRC" ] || die "no such compose file: $COMPOSE_FILE_SRC"
  cp "$COMPOSE_FILE_SRC" "$COMPOSE"
elif [ "$FROM_SOURCE" -eq 0 ]; then
  say "Downloading docker-compose.prod.yml ($VERSION)"
  if ! curl -fsSL "$(release_base)/docker-compose.prod.yml" -o "$COMPOSE.tmp"; then
    rm -f "$COMPOSE.tmp"
    [ "$VERSION" = latest ] || die "release $VERSION not found at github.com/$REPO"
    warn "no published release yet: building from source ($SOURCE_REF) instead (first run takes several minutes)"
    FROM_SOURCE=1
  else
    mv "$COMPOSE.tmp" "$COMPOSE"
  fi
fi

if [ "$FROM_SOURCE" -eq 1 ] && [ -z "$COMPOSE_FILE_SRC" ]; then
  command -v tar >/dev/null 2>&1 || die "tar is required for --from-source"
  SRC="$DIR/src"
  say "Fetching the source ($SOURCE_REF)"
  rm -rf "$SRC.new" && mkdir -p "$SRC.new"
  curl -fsSL "https://codeload.github.com/$REPO/tar.gz/$SOURCE_REF" | tar -xz --strip-components=1 -C "$SRC.new"     || die "could not download $SOURCE_REF of github.com/$REPO"
  rm -rf "$SRC" && mv "$SRC.new" "$SRC"
  COMPOSE="$SRC/deploy/docker-compose.yml"
  [ -f "$COMPOSE" ] || die "the source has no deploy/docker-compose.yml"
fi

# `aethera`: status, logs, update, backup and restore on this machine; the updater container runs the same script. Release installs only.
if [ "$FROM_SOURCE" -eq 0 ] || [ -n "$COMPOSE_FILE_SRC" ]; then
  CTL_BIN=/usr/local/bin/aethera
  if [ -n "$COMPOSE_FILE_SRC" ] && [ -f "$(dirname "$COMPOSE_FILE_SRC")/aethera-ctl.sh" ]; then
    install -m 0755 "$(dirname "$COMPOSE_FILE_SRC")/aethera-ctl.sh" "$CTL_BIN"
  else
    curl -fsSL "$(release_base)/aethera-ctl.sh" -o "$CTL_BIN.tmp" && chmod 0755 "$CTL_BIN.tmp" && mv "$CTL_BIN.tmp" "$CTL_BIN" || warn "could not install the aethera command"
  fi
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
[ -z "$ACME_EMAIL" ] || env_set AETHERA_ACME_EMAIL "$ACME_EMAIL"
env_set AETHERA_VERSION "$VERSION"
env_set AETHERA_DIR "$DIR"
# The agent of this machine is part of the stack and waits for the control plane to enroll it; the updater is an opt-in profile.
if [ "$FROM_SOURCE" -eq 1 ]; then profiles=""; else profiles="updater"; fi
env_set COMPOSE_PROFILES "$profiles"
if [ "$WITH_AGENT" -eq 1 ]; then env_set AETHERA_AUTO_LOCAL_SERVER ""; else env_set AETHERA_AUTO_LOCAL_SERVER false; fi
env_set AETHERA_HTTP_PORT "$HTTP_PORT"
env_set AETHERA_GRPC_PORT "$GRPC_PORT"

# Settings the compose file reads, passed through when set in the environment of this script (private registry or mirror, a release
# mirror, update checks off): AETHERA_IMAGE, AETHERA_AGENT_IMAGE, AETHERA_UPDATER_IMAGE, AETHERA_UPDATE_API_URL, AETHERA_UPDATE_CHECK,
# AETHERA_UPDATE_REPOSITORY, AETHERA_INSTALL_AGENT_URL.
for name in AETHERA_IMAGE AETHERA_AGENT_IMAGE AETHERA_UPDATER_IMAGE AETHERA_UPDATE_API_URL AETHERA_UPDATE_CHECK AETHERA_UPDATE_REPOSITORY AETHERA_HEALTH_TIMEOUT AETHERA_INSTALL_AGENT_URL; do
  value="$(printenv "$name" || true)"
  [ -z "$value" ] || env_set "$name" "$value"
done

dc() { docker compose -f "$COMPOSE" --env-file "$ENV_FILE" "$@"; }

# ---- start ------------------------------------------------------------------------------------------------------------
mkdir -p /var/lib/aethera
if [ "$FROM_SOURCE" -eq 1 ]; then
  say "Building and starting the stack (several minutes the first time)"
  dc up -d --build --scale "agent=$WITH_AGENT"
else
  say "Pulling images"
  dc pull --quiet postgres redis aethera agent || warn "could not pull every image (building locally, or offline); continuing"
  say "Starting the control plane"
  dc up -d postgres redis aethera
  [ "$WITH_AGENT" -eq 0 ] || dc up -d agent
fi

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

if [ "$WITH_AGENT" -eq 1 ] && [ -n "$ADMIN_EMAIL" ]; then
  # The agent container is up and waiting. The control plane connects it as the "local" server by itself as soon as the owner exists
  # (what Servers > "Use this machine" does by hand); this only waits for that.
  say "Connecting this machine as a server"
  connected=0
  for _ in $(seq 1 60); do
    if api GET /system/local-server 2>/dev/null | grep -q '"connected":true'; then connected=1; break; fi
    sleep 2
  done
  [ "$connected" -eq 1 ] || warn "the agent of this machine is not connected yet (docker compose -f $COMPOSE logs agent); the Servers page has 'Use this machine'"
fi

if [ "$FROM_SOURCE" -eq 0 ]; then
  say "Starting the updater"
  dc up -d updater || warn "the updater did not start; update with 'aethera update' on this machine instead"
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
if [ "$FROM_SOURCE" -eq 1 ]; then
  echo "    Manage:      docker compose -f $COMPOSE --env-file $ENV_FILE ps | logs -f   (update: run this installer again)"
else
  echo "    Manage:      aethera status | logs | update | backup   (updates also work from Settings > System in the UI)"
fi
