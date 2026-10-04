# Agent <-> gateway end-to-end test (Phase 2)

`deploy/agent-e2e.sh` runs the real .NET API with its mTLS gRPC gateway on the host and the real Go agent (a static linux binary) in a
Linux container that dials the gateway. Everything is driven through the public REST API with curl. It is the only test in which the two
implementations meet: the .NET suites use `Grpc.Net` clients as the agent, the Go suites use a Go fake control plane.

```bash
bash deploy/agent-e2e.sh                     # everything, about 12 minutes
E2E_PHASES=main bash deploy/agent-e2e.sh     # one phase (main | renew | upgrade)
SKIP_AGENT_BUILD=1 SKIP_API_BUILD=1 bash deploy/agent-e2e.sh   # reuse src/agent/dist and the built API
```

Needs `bash`, `docker`, `curl`, `openssl` and the .NET 10 SDK on the host. `jq` is used when installed, otherwise the script runs
`ghcr.io/jqlang/jq` in a container. No Go toolchain is needed: the agent is built (and `go vet` / `go test` run) in `golang:1.24` with the
module cache volume `aethera-gomod`. The script works on Linux, macOS and Windows (MSYS bash with Docker Desktop).

## What it starts

| Part | Where | Notes |
|---|---|---|
| PostgreSQL 17, Redis 7 | containers, `127.0.0.1:BASE+1`, `BASE+2` | one throw-away database per phase |
| API + gateway | host process, API `BASE+11`, gRPC `BASE+10` | Development, AutoMigrate, a fresh master key, `Aethera:Agents:PublicEndpoint = host.docker.internal:BASE+10` |
| Docker-in-Docker daemon | privileged container | the agent's Docker: listing, pruning and freezing never touch your own containers (`E2E_DOCKER=none` runs the agent without any Docker) |
| Agent | `debian:12-slim` container, state in a named volume | enrolled with `aethera-agent enroll`, started with `aethera-agent run` |

Containers and volumes carry the label `aethera-e2e=1`; the script removes leftovers of an aborted run at the start and its own at the
end (`E2E_KEEP=1` keeps them). The default ports are `15401`-`15411` (high ports: Windows reserves many lower ranges); `E2E_PORT_BASE`
moves them. The agent reaches the host through `E2E_ENDPOINT_HOST` (default `host.docker.internal`, mapped with `--add-host ...:host-gateway`
on Linux). That name is also the SAN of the gateway certificate, so changing it exercises the SAN match.

## Scenarios

**main** (default gateway settings, idle ping 4 s)

1. owner via the setup API, server, join token (endpoint, CA pin and command in the response; the token never appears in the API log)
2. enroll: an unknown token and a wrong CA pin are refused, the right one succeeds, replaying the used token is refused
3. connect: `status.agent = available`, session version/capabilities/clock skew, `status.docker = available`
4. discovery report and facts, metrics latest and a raw series, status-change events
5. Docker inventory (containers, images, volumes, networks) of the agent's Docker, `refresh-discovery` job, `prune` job (dangling images only)
6. 65 s idle: HTTP/2 keepalive and the gateway's `Ping`/`Pong` (round-trip time measured)
7. Docker daemon stopped: `status.docker = unavailable` while the agent stays available, commands answer 502; restored
8. cancel: the daemon is frozen so a `system_prune` blocks inside the agent, the job is cancelled, the agent reports `CANCELLED`
9. agent container killed: `status.agent = unavailable` within seconds, commands answer 503; restarted: available again
10. agent process frozen (`docker pause`): heartbeat watchdog flips the axis after about 45 s; unfrozen: reconnects
11. a second agent with the same identity: the older stream receives `Disconnect(SUPERSEDED)`
12. API restarted: the agent reconnects on its own (new listener certificate, same CA pin); the agent's own log stream is stored in
    `log_chunks` without gaps (`Hello.active_log_stream_ids` resume)
13. `agent/reset`: the stream is closed with `REVOKED`, the agent logs it and stops retrying but keeps running; re-enrolling the same state
    volume with a new token brings it back
14. server deleted: stream closed with `REVOKED`, no more retries

**renew** gateway certificates last 5 minutes with a 3.6 minute renewal window: the gateway sends `CertRotationHint`, the agent renews,
replaces key and certificate on disk, reconnects, and the superseded certificate is revoked (exactly one live certificate remains).

**upgrade** `MinAgentVersion=99.0.0`: the agent gets `Disconnect(UPGRADE_REQUIRED)` and is never marked available.

## Not covered (and why)

- Container log follow and build/deploy log flow control: there is no REST endpoint that issues `LogStreamStart` yet, so a log-producing
  container cannot be driven from outside. The agent's own `AGENT` log stream (LogChunk + `LogFlowControl` + resume after reconnect) is
  covered; follow streams are covered by the Go (`internal/logstream`) and .NET (`LogIngestTests`) suites.
- A skewed agent clock (the containers share the host kernel clock): only the measured skew is asserted to be small.
- The host's real Docker socket: the agent only talks to the throw-away Docker-in-Docker daemon.

Exit status is non-zero on the first failed check; the failure message is followed by the tail of the API log and of every agent container.
