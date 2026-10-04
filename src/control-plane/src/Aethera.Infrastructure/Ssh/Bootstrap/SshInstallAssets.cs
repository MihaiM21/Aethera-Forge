namespace Aethera.Infrastructure.Ssh.Bootstrap;

/// <summary>
/// The files the SSH bootstrap puts on a server. <see cref="UnitFile"/> and <see cref="AgentYaml"/> are copies of
/// <c>deploy/agent/aethera-agent.service</c> and <c>deploy/agent/agent.yaml</c> (a test fails when they drift); <see cref="InstallScript"/>
/// is the fixed script the bootstrap runs as root.
/// </summary>
public static class SshInstallAssets
{
    public const string UnitFile = """
# Aethera server agent (ADR 0002 "Agent over-privilege").
#
# Install:
#   useradd --system --home-dir /var/lib/aethera --shell /usr/sbin/nologin aethera-agent
#   usermod -aG docker aethera-agent       # root-equivalent on the host; prefer rootless Docker
#   install -d -o aethera-agent -g aethera-agent -m 0700 /var/lib/aethera /var/lib/aethera/bin
#   install -m 0755 aethera-agent /var/lib/aethera/bin/aethera-agent
#   install -d -m 0755 /etc/aethera && install -m 0644 agent.yaml /etc/aethera/agent.yaml   # root-owned policy
#   sudo -u aethera-agent /var/lib/aethera/bin/aethera-agent enroll --endpoint <host:9443> --token <join token> --ca-sha256 <hex>
#   systemctl enable --now aethera-agent
#
# The binary lives under the state directory because that is the only writable
# path: self-update (AgentSelfUpdate) swaps it atomically and keeps the previous
# one for rollback. The agent listens on no socket; it only dials out.
[Unit]
Description=Aethera server agent
Documentation=https://github.com/MihaiM21/aethera-forge
After=network-online.target docker.service
Wants=network-online.target

[Service]
Type=simple
User=aethera-agent
Group=aethera-agent
SupplementaryGroups=docker
ExecStart=/var/lib/aethera/bin/aethera-agent run --config /etc/aethera/agent.yaml
# Self-update and rollback end the process on purpose; always bring it back.
Restart=always
RestartSec=3
# Containers keep running independently of the agent: never kill them with it.
KillMode=process
TimeoutStopSec=30

# --- Hardening -----------------------------------------------------------
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=/var/lib/aethera
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectKernelLogs=true
ProtectControlGroups=true
ProtectClock=true
ProtectHostname=true
RestrictNamespaces=true
RestrictRealtime=true
RestrictSUIDSGID=true
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK
LockPersonality=true
MemoryDenyWriteExecute=true
SystemCallArchitectures=native
SystemCallFilter=@system-service
CapabilityBoundingSet=
AmbientCapabilities=
UMask=0077

[Install]
WantedBy=multi-user.target
""";

    public const string AgentYaml = """
# /etc/aethera/agent.yaml - local policy of the Aethera agent.
#
# Root-owned and NOT changeable by the control plane (ADR 0002). A command that
# violates it is refused with ACK_STATUS_REJECTED_POLICY. Every key is optional;
# unknown keys are an error (the agent refuses to start rather than guess).

# Where the agent keeps its key, certificate, CA bundle, state and (later) Compose
# projects. Also the only path the systemd unit makes writable.
state_dir: /var/lib/aethera

# Docker endpoint. Empty = $DOCKER_HOST or unix:///var/run/docker.sock.
# Rootless Docker: unix:///run/user/<uid>/docker.sock
docker_host: ""

# Also trust the operating system's CA store for the control plane. Only enable
# this when the control plane presents a publicly trusted certificate. The
# default pins the Aethera CA received at enrollment.
trust_system_roots: false

# Upper bound on commands executing at once (the control plane may lower it).
max_concurrent_commands: 8

# debug | info | warn | error. WARN and above are also forwarded to the control plane.
log_level: info

# Base64 Ed25519 public key. When set, AgentSelfUpdate must carry a valid
# signature over the binary's SHA-256 digest.
# release_public_key: ""

policy:
  # Host directories bind mounts may live under. Default: only the state dir.
  # The Docker socket, the agent's key/certificates/state and /etc, /root, /proc,
  # /sys, /dev, /boot, /var/lib/docker and /var/lib/containerd are always refused,
  # also through symlinks.
  allowed_bind_prefixes:
    - /var/lib/aethera
  # Extra paths to refuse on top of the built-in list.
  denied_sources: []
  # Container log drivers that may be requested.
  allowed_log_drivers: [json-file, local, none]
  # Health-probe targets besides loopback and Docker networks: host names, IPs or
  # CIDRs. Link-local addresses (cloud metadata) can never be probed.
  probe_allowlist: []
  # Options to refuse for containers created through this agent. Choose from:
  #   run_as_root       containers without an explicit non-root user
  #   published_ports  any published host port
  #   privileged_ports host ports below 1024
  #   extra_hosts      extra_hosts entries
  #   custom_user_group any explicit container user
  # (Privileged mode, host namespaces, devices and cap_add cannot be expressed in
  # the protocol at all.)
  forbidden_options: []
""";

    public const string InstallScript = """
#!/bin/sh
# Run as root by the Aethera control plane over SSH ("add server by SSH"). Arguments: <staging dir> <endpoint host:port> <CA sha256 hex>.
# The one-time join token is read from the first line of standard input and never appears in a process list or in this script's output.
set -eu
T=$1
ENDPOINT=$2
CA_SHA256=$3
AGENT=/var/lib/aethera/bin/aethera-agent

[ "$(id -u)" = 0 ] || { echo "install.sh must run as root" >&2; exit 1; }
command -v systemctl >/dev/null 2>&1 || { echo "systemd (systemctl) is required" >&2; exit 1; }
IFS= read -r TOKEN
[ -n "$TOKEN" ] || { echo "no join token on standard input" >&2; exit 1; }

echo "creating the aethera-agent user"
if ! id aethera-agent >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/aethera --shell /usr/sbin/nologin aethera-agent
fi
if getent group docker >/dev/null 2>&1; then
  usermod -aG docker aethera-agent
else
  echo "warning: no docker group found; the agent needs access to the Docker socket" >&2
fi

echo "installing the agent binary"
install -d -o aethera-agent -g aethera-agent -m 0700 /var/lib/aethera /var/lib/aethera/bin
install -o aethera-agent -g aethera-agent -m 0755 "$T/aethera-agent" "$AGENT.new"
mv -f "$AGENT.new" "$AGENT"
install -d -m 0755 /etc/aethera
if [ ! -f /etc/aethera/agent.yaml ]; then install -m 0644 "$T/agent.yaml" /etc/aethera/agent.yaml; fi
install -m 0644 "$T/aethera-agent.service" /etc/systemd/system/aethera-agent.service
systemctl daemon-reload

echo "enrolling with the control plane"
if command -v runuser >/dev/null 2>&1; then
  printf '%s\n' "$TOKEN" | runuser -u aethera-agent -- "$AGENT" enroll --endpoint "$ENDPOINT" --ca-sha256 "$CA_SHA256" --token -
else
  printf '%s\n' "$TOKEN" | su -s /bin/sh aethera-agent -c "$AGENT enroll --endpoint '$ENDPOINT' --ca-sha256 '$CA_SHA256' --token -"
fi

echo "starting the service"
systemctl enable aethera-agent
systemctl restart aethera-agent
echo "installed: $("$AGENT" --version 2>&1 | head -n 1)"
""";
}
