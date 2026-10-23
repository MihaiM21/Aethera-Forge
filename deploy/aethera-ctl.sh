#!/bin/sh
# aethera: operate an Aethera installation made by install.sh (installed as /usr/local/bin/aethera).
#
#   aethera status                 containers, version, health
#   aethera logs [service]         follow the logs (aethera, postgres, redis, agent, updater)
#   aethera update [--version v]   back up the database, pull the new images, restart; roll back if the new version does not come up
#   aethera backup                 write a database backup (pg_dump -Fc) to <dir>/backups
#   aethera restore <file>         restore a backup into the database (stops the control plane meanwhile)
#   aethera backup-decrypt <in> <out>   decrypt a backup of Aethera's own database downloaded from a backup destination (AEB1);
#                                       <out> can then be given to `aethera restore`. Needs the master key of this installation.
#   aethera version                the installed version
#
# The same script runs inside the updater container ("daemon" mode): the UI's "Update" button writes a request file that it picks up.
# POSIX sh on purpose: it must run in the alpine-based updater image as well as on any host.
set -eu

DIR="${AETHERA_DIR:-/opt/aethera}"
COMPOSE_FILE="$DIR/docker-compose.yml"
ENV_FILE="$DIR/.env"
CTL="${AETHERA_CONTROL_DIR:-/data/updater}"
REPO="${AETHERA_REPO:-MihaiM21/aethera-forge}"
MODE="cli"
BACKUP_DIR="${AETHERA_BACKUP_DIR:-$DIR/backups}"
HEALTH_TIMEOUT="${AETHERA_HEALTH_TIMEOUT:-180}"

say() { printf '==> %s\n' "$*"; }
die() { printf 'aethera: %s\n' "$*" >&2; exit 1; }

dc() { docker compose -p aethera --project-directory "$DIR" -f "$COMPOSE_FILE" --env-file "$ENV_FILE" "$@"; }
env_get() { sed -n "s/^$1=//p" "$ENV_FILE" 2>/dev/null | tail -n1; }
env_set() {
  if grep -q "^$1=" "$ENV_FILE"; then
    awk -v k="$1" -v v="$2" 'BEGIN{FS=OFS="="} $1==k {print k "=" v; next} {print}' "$ENV_FILE" > "$ENV_FILE.tmp" && cat "$ENV_FILE.tmp" > "$ENV_FILE" && rm -f "$ENV_FILE.tmp"
  else
    printf '%s=%s\n' "$1" "$2" >> "$ENV_FILE"
  fi
}

# ---- status reporting to the control plane (daemon mode only) ------------------------------------------------------------------
report() { # report <state> <version> <message>
  [ "$MODE" = daemon ] || { say "$3"; return 0; }
  mkdir -p "$CTL"
  printf '{"state":"%s","version":"%s","message":"%s","updatedAt":"%s"}\n' "$1" "$2" "$(printf '%s' "$3" | tr -d '"\\' | tr '\n' ' ')" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$CTL/status.json.tmp"
  mv "$CTL/status.json.tmp" "$CTL/status.json"
}

require_install() {
  [ -f "$COMPOSE_FILE" ] && [ -f "$ENV_FILE" ] || die "no installation in $DIR (set AETHERA_DIR, or install with install.sh)"
  command -v docker >/dev/null 2>&1 || die "docker is not installed"
}

# ---- database backup and restore -----------------------------------------------------------------------------------------------
backup_to() { # backup_to <file>
  mkdir -p "$(dirname "$1")"
  # pg_dump runs inside the postgres container: its version always matches the server's.
  dc exec -T postgres pg_dump -U aethera -d aethera --format=custom --no-owner > "$1.partial" || { rm -f "$1.partial"; return 1; }
  [ -s "$1.partial" ] || { rm -f "$1.partial"; return 1; }
  mv "$1.partial" "$1"
}

restore_from() { # restore_from <file>
  dc exec -T postgres pg_restore -U aethera -d aethera --clean --if-exists --no-owner < "$1"
}

wait_healthy() { # wait_healthy <seconds>: the control plane answers /ready
  n=0
  while [ "$n" -lt "$1" ]; do
    if dc exec -T aethera bash -c "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /ready HTTP/1.0\r\n\r\n' >&3 && head -n1 <&3 | grep -q ' 200 '" >/dev/null 2>&1; then return 0; fi
    sleep 3; n=$((n + 3))
  done
  return 1
}

# ---- update ---------------------------------------------------------------------------------------------------------------------
latest_version() {
  command -v curl >/dev/null 2>&1 || die "curl is needed to look up the latest release (or pass --version)"
  curl -fsS --max-time 15 "https://api.github.com/repos/$REPO/releases/latest" | grep -o '"tag_name": *"[^"]*"' | head -n1 | cut -d'"' -f4 | sed 's/^v//'
}

# Pull the images of the stack at its (new) version. A registry that cannot be reached is fine if the images are already local
# (air-gapped installs, images loaded with `docker load`).
pull_or_have() {
  if dc pull --quiet aethera postgres redis >/dev/null 2>&1; then return 0; fi
  for image in $(dc config --images 2>/dev/null | grep -E "control-plane|aethera" | sort -u); do
    docker image inspect "$image" >/dev/null 2>&1 || return 1
  done
}

do_update() { # do_update <version>
  target="$1"
  require_install
  current="$(env_get AETHERA_VERSION)"
  [ -n "$target" ] || die "no target version"
  say "Updating Aethera ${current:-unknown} -> $target"

  ts="$(date -u +%Y%m%d-%H%M%S)"
  dump="$BACKUP_DIR/aethera-pre-update-$ts.dump"
  report backing_up "$target" "Backing up the database to $(basename "$dump")"
  backup_to "$dump" || { report failed "$target" "The database backup failed; nothing was changed."; return 1; }
  ls -1t "$BACKUP_DIR"/aethera-pre-update-*.dump 2>/dev/null | tail -n +6 | while read -r old; do rm -f "$old"; done

  cp "$ENV_FILE" "$ENV_FILE.before-$ts"
  env_set AETHERA_VERSION "$target"
  report pulling "$target" "Pulling the images of $target"
  if ! pull_or_have; then
    cp "$ENV_FILE.before-$ts" "$ENV_FILE"
    report failed "$target" "The images of $target could not be pulled and are not on this machine; nothing was changed."
    return 1
  fi

  report restarting "$target" "Restarting the control plane on $target"
  # --no-deps keeps PostgreSQL and Redis running; the agent of this machine is replaced by a matching one. The updater is last.
  dc up -d --no-deps aethera || true
  if [ -n "$(dc ps -q agent 2>/dev/null)" ]; then dc up -d --no-deps agent || true; fi

  if wait_healthy "$HEALTH_TIMEOUT"; then
    report "done" "$target" "Aethera is running $target."
    # The updater replaces itself last. `compose up` stops the container it runs in, which would kill it before the new one exists, so a
    # short-lived helper container (the same image) does it, a couple of seconds from now.
    if [ "$MODE" = daemon ]; then swap_updater; fi
    return 0
  fi

  report restarting "$target" "$target did not become healthy; restoring $current and the database backup"
  dc stop aethera >/dev/null 2>&1 || true
  cp "$ENV_FILE.before-$ts" "$ENV_FILE"
  # The new version may have migrated the schema; the old one refuses to start on a newer schema, so the backup goes back first.
  restore_from "$dump" >/dev/null 2>&1 || say "warning: restoring the database backup reported errors (often harmless on a clean --clean run)"
  dc up -d --no-deps aethera || true
  if wait_healthy 90; then
    report failed "$target" "$target did not start; rolled back to $current (the database was restored from $(basename "$dump"))."
  else
    report failed "$target" "$target did not start and the rollback to $current did not come up either: see 'aethera logs aethera'. The backup is $dump."
  fi
  return 1
}

swap_updater() {
  image="$(docker inspect --format '{{.Config.Image}}' "$(hostname)" 2>/dev/null || true)"
  [ -n "$image" ] || return 0
  docker rm -f aethera-updater-swap >/dev/null 2>&1 || true
  docker run -d --rm --name aethera-updater-swap -v /var/run/docker.sock:/var/run/docker.sock -v "$DIR:$DIR" -e AETHERA_DIR="$DIR"     --entrypoint aethera "$image" swap-updater >/dev/null 2>&1 || true
}

# ---- daemon (updater container) -------------------------------------------------------------------------------------------------
daemon() {
  MODE=daemon
  BACKUP_DIR="${AETHERA_BACKUP_DIR:-/data/backups}"
  require_install
  # The control plane runs unprivileged and must be able to drop request.json here, whoever created the folder first.
  mkdir -p "$CTL"
  chmod 1777 "$CTL"
  say "Updater running; watching $CTL"
  while :; do
    : > "$CTL/alive"
    if [ -s "$CTL/request.json" ]; then
      version="$(sed -n 's/.*"version" *: *"\([^"]*\)".*/\1/p' "$CTL/request.json" | head -n1)"
      mv "$CTL/request.json" "$CTL/request.taken"
      case "$version" in
        ""|*[!0-9A-Za-z.+-]*) report failed "$version" "The update request named no valid version." ;;
        *) do_update "$version" || true ;;
      esac
    fi
    sleep 5
  done
}

# ---- commands -------------------------------------------------------------------------------------------------------------------
cmd="${1:-help}"
[ $# -gt 0 ] && shift
case "$cmd" in
  daemon) daemon ;;
  swap-updater) require_install; sleep 3; dc up -d --no-deps updater ;;
  status)
    require_install
    say "Version $(env_get AETHERA_VERSION)"
    dc ps
    ;;
  version) require_install; env_get AETHERA_VERSION ;;
  logs) require_install; dc logs -f --tail 100 "$@" ;;
  backup)
    require_install
    file="$BACKUP_DIR/aethera-manual-$(date -u +%Y%m%d-%H%M%S).dump"
    backup_to "$file" || die "the backup failed"
    say "Backup written: $file"
    ;;
  restore)
    require_install
    [ $# -eq 1 ] && [ -f "$1" ] || die "usage: aethera restore <backup file>"
    say "Stopping the control plane and restoring $1 (the current data is replaced)"
    dc stop aethera
    restore_from "$1"
    dc up -d aethera
    say "Restored. The control plane is starting."
    ;;
  backup-decrypt)
    require_install
    [ $# -eq 2 ] && [ -f "$1" ] || die "usage: aethera backup-decrypt <encrypted backup file> <output file>"
    # Runs in the control plane container, which has the master key; the file is piped through, so nothing needs mounting.
    dc exec -T aethera dotnet Aethera.Api.dll backup-decrypt - - < "$1" > "$2.partial" || { rm -f "$2.partial"; die "could not decrypt $1 (wrong master key, or the file is damaged or incomplete)"; }
    mv "$2.partial" "$2"
    say "Decrypted: $2 (restore it with: aethera restore $2)"
    ;;
  update)
    target=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --version) target="${2:?--version needs a value}"; shift 2 ;;
        *) die "unknown option: $1" ;;
      esac
    done
    require_install
    [ -n "$target" ] || target="$(latest_version)"
    [ -n "$target" ] || die "could not determine the latest release"
    target="${target#v}"
    if [ "$target" = "$(env_get AETHERA_VERSION)" ]; then say "Already on $target."; exit 0; fi
    do_update "$target"
    ;;
  help|-h|--help) sed -n '2,14p' "$0" ;;
  *) die "unknown command: $cmd (try 'aethera help')" ;;
esac
