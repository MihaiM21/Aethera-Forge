#!/usr/bin/env bash
# Installs the Aethera agent on a server as a systemd service and enrolls it with the control plane.
#
# The control plane prints the exact command (Servers > Add server):
#   curl -fsSL https://aethera.example.com/install-agent.sh | sudo bash -s -- --token <join token> --endpoint <host:9443> --ca-sha256 <hex>
#
# Options:
#   --token <t>         one-time join token (or AETHERA_JOIN_TOKEN). Read once, never stored.
#   --endpoint <h:p>    gRPC address of the control plane (or AETHERA_ENDPOINT)
#   --ca-sha256 <hex>   fingerprint of the control plane's CA, pinned on first contact (or AETHERA_CA_SHA256)
#   --name <name>       name the agent reports (default: the host name)
#   --version <v>       agent release to install (default: latest, the newest non-pre-release), e.g. v0.1.0, 0.1.0 or 0.1.0-rc.1
#   --download-url <u>  base URL holding aethera-agent-linux-<arch> and SHA256SUMS (default: the GitHub release)
#   --no-docker         do not install Docker when it is missing (fail instead)
#
# Needs: Linux (amd64 or arm64) with systemd, root, curl. Safe to run again: it upgrades the binary and keeps the enrollment.
set -euo pipefail

REPO="${AETHERA_REPO:-MihaiM21/aethera-forge}"
STATE_DIR=/var/lib/aethera
CONF_DIR=/etc/aethera
UNIT=/etc/systemd/system/aethera-agent.service
USER_NAME=aethera-agent

TOKEN="${AETHERA_JOIN_TOKEN:-}"
ENDPOINT="${AETHERA_ENDPOINT:-}"
CA_SHA256="${AETHERA_CA_SHA256:-}"
NAME="${AETHERA_AGENT_NAME:-}"
VERSION="${AETHERA_AGENT_VERSION:-latest}"
DOWNLOAD_URL="${AETHERA_AGENT_DOWNLOAD_URL:-}"
INSTALL_DOCKER=1

say() { printf '==> %s\n' "$*"; }
die() { printf 'install-agent: %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --token) TOKEN="${2:?--token needs a value}"; shift 2 ;;
    --endpoint) ENDPOINT="${2:?--endpoint needs a value}"; shift 2 ;;
    --ca-sha256) CA_SHA256="${2:?--ca-sha256 needs a value}"; shift 2 ;;
    --name) NAME="${2:?--name needs a value}"; shift 2 ;;
    --version) VERSION="${2:?--version needs a value}"; shift 2 ;;
    --download-url) DOWNLOAD_URL="${2:?--download-url needs a value}"; shift 2 ;;
    --no-docker) INSTALL_DOCKER=0; shift ;;
    -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
done

[ "$(id -u)" -eq 0 ] || die "run as root (sudo)"
[ "$(uname -s)" = Linux ] || die "Linux only"
command -v systemctl >/dev/null 2>&1 || die "systemd is required"
command -v curl >/dev/null 2>&1 || die "curl is required"
command -v sha256sum >/dev/null 2>&1 || die "sha256sum is required"

case "$(uname -m)" in
  x86_64|amd64) ARCH=amd64 ;;
  aarch64|arm64) ARCH=arm64 ;;
  *) die "unsupported CPU architecture: $(uname -m)" ;;
esac

ENROLLED=0
[ -s "$STATE_DIR/enrollment.json" ] && ENROLLED=1
if [ "$ENROLLED" -eq 0 ]; then
  [ -n "$TOKEN" ] || die "--token is required for a first install"
  [ -n "$ENDPOINT" ] || die "--endpoint is required for a first install"
  [ -n "$CA_SHA256" ] || die "--ca-sha256 is required for a first install"
fi

# ---- Docker -----------------------------------------------------------------------------------------------------------
if ! command -v docker >/dev/null 2>&1; then
  [ "$INSTALL_DOCKER" -eq 1 ] || die "Docker is not installed (drop --no-docker to let this script install it)"
  say "Installing Docker (get.docker.com)"
  curl -fsSL https://get.docker.com | sh
fi
systemctl enable --now docker >/dev/null 2>&1 || true

# ---- binary -----------------------------------------------------------------------------------------------------------
if [ -z "$DOWNLOAD_URL" ]; then
  if [ "$VERSION" = latest ]; then DOWNLOAD_URL="https://github.com/$REPO/releases/latest/download"
  else DOWNLOAD_URL="https://github.com/$REPO/releases/download/v${VERSION#v}"; fi # the git tag always has the v; --version takes either form
fi
case "$DOWNLOAD_URL" in https://*) ;; *) die "the download URL must use https" ;; esac

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
say "Downloading aethera-agent ($ARCH) from $DOWNLOAD_URL"
curl -fsSL "$DOWNLOAD_URL/aethera-agent-linux-$ARCH" -o "$TMP/aethera-agent"
curl -fsSL "$DOWNLOAD_URL/SHA256SUMS" -o "$TMP/SHA256SUMS"
expected="$(awk -v f="aethera-agent-linux-$ARCH" '$2 == f || $2 == "*" f { print $1 }' "$TMP/SHA256SUMS" | head -n1)"
[ -n "$expected" ] || die "SHA256SUMS has no entry for aethera-agent-linux-$ARCH"
actual="$(sha256sum "$TMP/aethera-agent" | awk '{print $1}')"
[ "$expected" = "$actual" ] || die "checksum mismatch for the downloaded binary (expected $expected, got $actual)"
chmod 0755 "$TMP/aethera-agent"
"$TMP/aethera-agent" version >/dev/null 2>&1 || die "the downloaded binary does not run on this machine"

# ---- user, directories, files -----------------------------------------------------------------------------------------
if ! id "$USER_NAME" >/dev/null 2>&1; then
  useradd --system --home-dir "$STATE_DIR" --shell /usr/sbin/nologin "$USER_NAME"
fi
# Membership of the docker group is root-equivalent on the host; the agent only runs typed, allowlisted commands.
getent group docker >/dev/null 2>&1 && usermod -aG docker "$USER_NAME"

install -d -o "$USER_NAME" -g "$USER_NAME" -m 0700 "$STATE_DIR" "$STATE_DIR/bin"
install -d -m 0755 "$CONF_DIR"
# The policy file is root-owned and never overwritten: local policy is not changeable by the control plane.
if [ ! -f "$CONF_DIR/agent.yaml" ]; then
  cat > "$CONF_DIR/agent.yaml" <<'YAML'
# Local policy of the Aethera agent. Root-owned; the control plane cannot change it.
state_dir: /var/lib/aethera
log_level: info
YAML
  chmod 0644 "$CONF_DIR/agent.yaml"
fi

# The binary lives under the state directory: self-update swaps it there and keeps the previous one for rollback.
BIN="$STATE_DIR/bin/aethera-agent"
if [ -f "$BIN" ] && [ "$ENROLLED" -eq 1 ] && systemctl is-active --quiet aethera-agent; then systemctl stop aethera-agent; fi
install -o "$USER_NAME" -g "$USER_NAME" -m 0755 "$TMP/aethera-agent" "$BIN"

cat > "$UNIT" <<UNIT
[Unit]
Description=Aethera server agent
Documentation=https://github.com/$REPO
After=network-online.target docker.service
Wants=network-online.target

[Service]
Type=simple
User=$USER_NAME
Group=$USER_NAME
SupplementaryGroups=docker
ExecStart=$BIN run --config $CONF_DIR/agent.yaml
# Self-update and rollback end the process on purpose; always bring it back.
Restart=always
RestartSec=3
# Containers keep running independently of the agent: never kill them with it.
KillMode=process
TimeoutStopSec=30
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=$STATE_DIR
ProtectHome=true
PrivateTmp=true
PrivateDevices=true

[Install]
WantedBy=multi-user.target
UNIT

# ---- enroll -----------------------------------------------------------------------------------------------------------
if [ "$ENROLLED" -eq 0 ]; then
  say "Enrolling with $ENDPOINT"
  # The token goes through the environment, not the command line, so it stays out of the process list.
  AETHERA_JOIN_TOKEN="$TOKEN" runuser -u "$USER_NAME" -- "$BIN" enroll \
    --config "$CONF_DIR/agent.yaml" --endpoint "$ENDPOINT" --ca-sha256 "$CA_SHA256" ${NAME:+--name "$NAME"}
fi

systemctl daemon-reload
systemctl enable --now aethera-agent
sleep 2
systemctl is-active --quiet aethera-agent || die "the agent did not stay up: journalctl -u aethera-agent -n 50"
say "Aethera agent is running ($("$BIN" version 2>/dev/null || echo unknown)). The server shows as connected in the UI."
