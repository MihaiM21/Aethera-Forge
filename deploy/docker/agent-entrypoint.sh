#!/bin/sh
# Enrolls on first start (when the join token is given), then runs the agent. Enrollment state lives in the state dir volume,
# so later restarts need no token.
set -eu
STATE_DIR="${AETHERA_STATE_DIR:-/var/lib/aethera}"
if [ ! -s "$STATE_DIR/enrollment.json" ]; then
  if [ -z "${AETHERA_JOIN_TOKEN:-}" ] || [ -z "${AETHERA_ENDPOINT:-}" ] || [ -z "${AETHERA_CA_SHA256:-}" ]; then
    echo "aethera-agent: not enrolled. Set AETHERA_ENDPOINT, AETHERA_CA_SHA256 and AETHERA_JOIN_TOKEN (UI: Servers > Add server)." >&2
    exit 1
  fi
  aethera-agent enroll --state-dir "$STATE_DIR" --endpoint "$AETHERA_ENDPOINT" --ca-sha256 "$AETHERA_CA_SHA256" \
    ${AETHERA_AGENT_NAME:+--name "$AETHERA_AGENT_NAME"}
fi
exec aethera-agent run --state-dir "$STATE_DIR"
