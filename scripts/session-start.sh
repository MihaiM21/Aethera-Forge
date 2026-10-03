#!/usr/bin/env bash
# Claude Code SessionStart hook: prepares the toolchain in cloud sessions.
# Idempotent and non-interactive. Does nothing outside Claude Code cloud sessions.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

REPO_ROOT="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
export DOTNET_ROOT="$HOME/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export PATH="$DOTNET_ROOT:$HOME/go/bin:$PATH"

log() { echo "[session-start] $*" >&2; }

SUDO=""
if [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1; then SUDO="sudo"; fi

# --- .NET 10 SDK -------------------------------------------------------------
have_dotnet10() {
  command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'
}

install_dotnet() {
  # 1) Official installer into $HOME/.dotnet (the host may be blocked by an egress policy).
  if curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh 2>/dev/null \
     && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_ROOT" >&2; then
    return 0
  fi
  # 2) Fallback: Ubuntu archive package, linked at $HOME/.dotnet so DOTNET_ROOT stays uniform.
  log "dotnet-install.sh failed, falling back to apt (dotnet-sdk-10.0)"
  $SUDO apt-get update -qq >&2
  DEBIAN_FRONTEND=noninteractive $SUDO apt-get install -y -qq dotnet-sdk-10.0 >&2
  local sys_root
  sys_root="$(dirname "$(readlink -f "$(command -v dotnet)")")"
  if [ ! -e "$DOTNET_ROOT" ]; then ln -sfn "$sys_root" "$DOTNET_ROOT"; fi
}

if [ ! -x "$DOTNET_ROOT/dotnet" ] || ! "$DOTNET_ROOT/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then
  if have_dotnet10 && [ ! -e "$DOTNET_ROOT" ]; then
    ln -sfn "$(dirname "$(readlink -f "$(command -v dotnet)")")" "$DOTNET_ROOT"
  else
    log "installing .NET 10 SDK"
    install_dotnet
  fi
fi
log "dotnet $(dotnet --version)"

# --- buf, protoc-gen-go, protoc-gen-go-grpc ----------------------------------
if command -v go >/dev/null 2>&1; then
  (
    cd /tmp
    command -v buf >/dev/null 2>&1 || { log "installing buf"; go install github.com/bufbuild/buf/cmd/buf@latest >&2; }
    command -v protoc-gen-go >/dev/null 2>&1 || { log "installing protoc-gen-go"; go install google.golang.org/protobuf/cmd/protoc-gen-go@latest >&2; }
    command -v protoc-gen-go-grpc >/dev/null 2>&1 || { log "installing protoc-gen-go-grpc"; go install google.golang.org/grpc/cmd/protoc-gen-go-grpc@latest >&2; }
  )
else
  log "go not found, skipping buf/protoc plugins"
fi

# --- Project dependencies ----------------------------------------------------
if command -v pnpm >/dev/null 2>&1; then
  log "pnpm install (src/web)"
  (cd "$REPO_ROOT/src/web" && pnpm install >&2)
fi

log "dotnet restore (src/control-plane)"
(cd "$REPO_ROOT/src/control-plane" && dotnet restore >&2)

# --- Persist environment for the session ------------------------------------
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo "export DOTNET_ROOT=\"$DOTNET_ROOT\""
    echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "export DOTNET_NOLOGO=1"
    echo "export PATH=\"$DOTNET_ROOT:$HOME/go/bin:\$PATH\""
  } >> "$CLAUDE_ENV_FILE"
fi

log "done"
