#!/bin/sh
# aethera: operate an Aethera installation made by install.sh (installed as /usr/local/bin/aethera).
#
#   aethera status                 containers, version, health
#   aethera logs [service]         follow the logs (aethera, postgres, redis, agent, updater)
#   aethera update [--version v]   back up the database, pull the new images, restart; roll back if the new version does not come up
#   aethera backup                 write a database backup (pg_dump -Fc) to <dir>/backups
#   aethera restore <file>         restore a backup into the database (stops the control plane meanwhile)
#   aethera reset-password <email> [--stdin]
#                                  set a new password for a user (printed once, or read from stdin), clear the lockout, sign them out
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
# Where a release's files (docker-compose.prod.yml, aethera-ctl.sh) are downloaded from: <base>/v<version>/<file>. A mirror or a test
# server through AETHERA_RELEASE_URL (also read from the .env, where install.sh records it).
RELEASE_URL="${AETHERA_RELEASE_URL:-}"

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

# Downloads the files of release <version> into <dir>: its compose file (always) and its aethera script (when it can be had).
# Fails when the compose file cannot be downloaded; an air-gapped installation has no release server and keeps its own.
fetch_release() { # fetch_release <version> <dir>
  command -v curl >/dev/null 2>&1 || return 1
  base="${RELEASE_URL:-$(env_get AETHERA_RELEASE_URL)}"
  base="${base:-https://github.com/$REPO/releases/download}"
  mkdir -p "$2"
  curl -fsSL --max-time 60 "$base/v$1/docker-compose.prod.yml" -o "$2/docker-compose.yml" 2>/dev/null && [ -s "$2/docker-compose.yml" ] || return 1
  curl -fsSL --max-time 60 "$base/v$1/aethera-ctl.sh" -o "$2/aethera-ctl.sh" 2>/dev/null && [ -s "$2/aethera-ctl.sh" ] || rm -f "$2/aethera-ctl.sh"
}

# Replaces this command with the one of the new release once the update has succeeded. Only on the host: in the updater container the
# new updater image brings its own copy.
install_ctl() { # install_ctl <new script>
  [ "$MODE" = cli ] && [ -s "$1" ] || return 0
  head -n1 "$1" | grep -q '^#!/bin/sh' || return 0 # not a script (an error page): keep the current one
  self="$(command -v "$0" 2>/dev/null || printf '%s' "$0")"
  [ -f "$self" ] && [ -w "$self" ] || return 0
  if cat "$1" > "$self.new" && chmod 0755 "$self.new"; then mv "$self.new" "$self"; else rm -f "$self.new"; fi
}

# Puts the .env and the compose file back as they were before the update started (written in place: either may be a bind mount).
restore_files() { # restore_files <ts>
  cat "$ENV_FILE.before-$1" > "$ENV_FILE"
  [ -f "$COMPOSE_FILE.before-$1" ] && cat "$COMPOSE_FILE.before-$1" > "$COMPOSE_FILE"
  return 0
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
  cp "$COMPOSE_FILE" "$COMPOSE_FILE.before-$ts"
  ls -1t "$COMPOSE_FILE".before-* 2>/dev/null | tail -n +6 | while read -r old; do rm -f "$old"; done
  env_set AETHERA_VERSION "$target"

  # A release can change the stack itself (a capability, a network alias, a new setting), not only its images: the new version runs with
  # its own compose file. One that cannot be downloaded (no release server) leaves the current file, and the update says so.
  files="$DIR/.update-$ts"
  note=""
  report pulling "$target" "Downloading the compose file of $target"
  if fetch_release "$target" "$files"; then
    if ! docker compose -p aethera --project-directory "$DIR" -f "$files/docker-compose.yml" --env-file "$ENV_FILE" config -q >/dev/null 2>&1; then
      restore_files "$ts"; rm -rf "$files"
      report failed "$target" "The compose file of $target is not valid for this installation; nothing was changed."
      return 1
    fi
    cat "$files/docker-compose.yml" > "$COMPOSE_FILE"
  else
    note=" The compose file of $target could not be downloaded; the current one was kept."
  fi

  report pulling "$target" "Pulling the images of $target"
  if ! pull_or_have; then
    restore_files "$ts"; rm -rf "$files"
    report failed "$target" "The images of $target could not be pulled and are not on this machine; nothing was changed."
    return 1
  fi

  report restarting "$target" "Restarting the control plane on $target"
  # --no-deps keeps PostgreSQL and Redis running; the agent of this machine is replaced by a matching one. The updater is last.
  dc up -d --no-deps aethera || true
  if [ -n "$(dc ps -q agent 2>/dev/null)" ]; then dc up -d --no-deps agent || true; fi

  if wait_healthy "$HEALTH_TIMEOUT"; then
    install_ctl "$files/aethera-ctl.sh"
    rm -rf "$files"
    report "done" "$target" "Aethera is running $target.$note"
    # The updater replaces itself last. `compose up` stops the container it runs in, which would kill it before the new one exists, so a
    # short-lived helper container (the same image) does it, a couple of seconds from now.
    if [ "$MODE" = daemon ]; then swap_updater; fi
    return 0
  fi

  report restarting "$target" "$target did not become healthy; restoring $current and the database backup"
  dc stop aethera >/dev/null 2>&1 || true
  restore_files "$ts"; rm -rf "$files"
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
      version="${version#v}" # image tags have no leading v (the git tag and release assets do)
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
  reset-password)
    # Runs a verb of the API binary inside the control-plane container (the database is reached with the container's own settings).
    # Needs access to Docker, i.e. root: whoever can run this can already read the database and the .env.
    require_install
    [ "$(id -u)" -eq 0 ] || die "reset-password needs root: run it with sudo"
    [ $# -ge 1 ] || die "usage: aethera reset-password <email> [--stdin]   (--stdin reads the new password from standard input)"
    dc exec -T aethera dotnet Aethera.Api.dll reset-password "$@"
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
    target="${target#v}" # --version v0.1.0-rc.2 and 0.1.0-rc.2 both work: image tags have no leading v
    case "$target" in
      *[!0-9A-Za-z.+-]*|"") die "invalid version: $target" ;;
    esac
    if [ "$target" = "$(env_get AETHERA_VERSION)" ]; then say "Already on $target."; exit 0; fi
    do_update "$target"
    ;;
  help|-h|--help) sed -n '2,13p' "$0" ;;
  *) die "unknown command: $cmd (try 'aethera help')" ;;
esac
