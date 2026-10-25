#!/bin/sh
# Enrolls on first start, then runs the agent. Enrollment state lives in the state dir volume, so later restarts need no token.
#
# Two ways to enroll:
#  - AETHERA_JOIN_TOKEN, AETHERA_ENDPOINT and AETHERA_CA_SHA256 given: enroll with them (UI: Servers > Add server).
#  - AETHERA_ENROLL_DIR and AETHERA_ENDPOINT given, no token: wait for the control plane to ask (it does by itself once the first owner has registered;
#    Servers > "Use this machine" asks on demand). It drops
#    enable-agent.json (join token and CA fingerprint) in that shared folder; agent-alive tells it something here can act on the
#    request, agent-status.json reports progress back. The endpoint stays the loopback one (--keep-endpoint): this container runs in the
#    host network namespace (Docker Desktop: in the control plane's), where the gateway answers on 127.0.0.1, whatever public address
#    and port the control plane advertises.
set -eu
STATE_DIR="${AETHERA_STATE_DIR:-/var/lib/aethera}"

wait_for_request() {
  DIR="$AETHERA_ENROLL_DIR"
  REQ="$DIR/enable-agent.json"
  mkdir -p "$DIR" && chmod 1777 "$DIR"
  field() { sed -n "s/.*\"$1\" *: *\"\([^\"]*\)\".*/\1/p" "$REQ" | head -n1; }
  status() { printf '{"state":"%s","message":"%s"}\n' "$1" "$2" > "$DIR/agent-status.json.tmp" && mv "$DIR/agent-status.json.tmp" "$DIR/agent-status.json"; }
  echo "aethera-agent: not enrolled; waiting for the control plane to enroll this machine (after the first owner registers, or Servers > Use this machine)"
  while :; do
    : > "$DIR/agent-alive"
    if [ -s "$REQ" ]; then
      token="$(field joinToken)"; ca="$(field caSha256)"; name="$(field name)"
      rm -f "$REQ"
      ok=1
      case "$token" in aeth_join_*) ;; *) ok=0 ;; esac
      case "$ca$name" in *[!0-9A-Za-z_-]*) ok=0 ;; esac
      if [ "$ok" = 1 ]; then
        status starting "Enrolling this machine"
        if AETHERA_JOIN_TOKEN="$token" aethera-agent enroll --keep-endpoint --state-dir "$STATE_DIR" --endpoint "$AETHERA_ENDPOINT" --ca-sha256 "$ca" ${name:+--name "$name"}; then
          status "done" "Enrolled; the agent is connecting."
          return 0
        fi
        status failed "Enrolling failed: see the logs of the agent container."
      else
        status failed "The request carried no valid join token."
      fi
    fi
    sleep 3
  done
}

# Enrolled under another control plane? The database of this installation was recreated (a new CA) while this state directory survived:
# that enrollment can never connect. The control plane publishes its CA fingerprint in the shared folder every few seconds; when a fresh
# one differs from the pinned CA, forget the enrollment and wait to be enrolled again. Without a fresh file (an older control plane, or
# none running yet) after a minute, the enrollment is kept as it is.
forget_foreign_enrollment() {
  [ -s "$STATE_DIR/enrollment.json" ] && [ -n "${AETHERA_ENROLL_DIR:-}" ] && [ -n "${AETHERA_ENDPOINT:-}" ] || return 0
  f="$AETHERA_ENROLL_DIR/ca-sha256"; want=""; tries=0
  while [ "$tries" -lt 30 ]; do
    if [ -s "$f" ] && [ $(( $(date +%s) - $(stat -c %Y "$f") )) -lt 60 ]; then want="$(tr -d ' \r\n' < "$f" | tr 'A-F' 'a-f')"; break; fi
    tries=$((tries + 1)); sleep 2
  done
  have="$(sed -n 's/.*"ca_sha256" *: *"\([^"]*\)".*/\1/p' "$STATE_DIR/enrollment.json" | head -n1 | tr 'A-F' 'a-f')"
  if [ -n "$want" ] && [ -n "$have" ] && [ "$want" != "$have" ]; then
    echo "aethera-agent: the enrollment belongs to another control plane (its CA changed); forgetting it"
    rm -f "$STATE_DIR/enrollment.json" "$STATE_DIR/agent.crt" "$STATE_DIR/agent.key" "$STATE_DIR/ca.crt"
  fi
}
forget_foreign_enrollment
if [ ! -s "$STATE_DIR/enrollment.json" ]; then
  if [ -n "${AETHERA_JOIN_TOKEN:-}" ] && [ -n "${AETHERA_ENDPOINT:-}" ] && [ -n "${AETHERA_CA_SHA256:-}" ]; then
    aethera-agent enroll --state-dir "$STATE_DIR" --endpoint "$AETHERA_ENDPOINT" --ca-sha256 "$AETHERA_CA_SHA256" \
      ${AETHERA_AGENT_NAME:+--name "$AETHERA_AGENT_NAME"}
  elif [ -n "${AETHERA_ENROLL_DIR:-}" ] && [ -n "${AETHERA_ENDPOINT:-}" ]; then
    wait_for_request
  else
    echo "aethera-agent: not enrolled. Set AETHERA_ENDPOINT, AETHERA_CA_SHA256 and AETHERA_JOIN_TOKEN (UI: Servers > Add server)." >&2
    exit 1
  fi
fi
# An enrollment made earlier (host network, or the endpoint the control plane advertises, e.g. localhost:9443 which resolves to ::1 here
# while the gateway listens on IPv4) is pointed at the loopback endpoint this container is configured with.
if [ -n "${AETHERA_ENROLL_DIR:-}" ] && [ -n "${AETHERA_ENDPOINT:-}" ] && [ -s "$STATE_DIR/enrollment.json" ]; then
  sed -i "s|\"endpoint\": *\"[^\"]*\"|\"endpoint\": \"$AETHERA_ENDPOINT\"|" "$STATE_DIR/enrollment.json"
fi
exec aethera-agent run --state-dir "$STATE_DIR"
