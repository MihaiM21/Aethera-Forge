# Aethera — Implementation Plan (Turn One migration MVP)

## Context

The repo `MihaiM21/Aethera-Forge` contains only the product spec (`docs/idea/01_AppIdea.md`), UI notes (`docs/idea/01_UI.md`) and UI inspiration screenshots (`docs/idea/ui inspiration/*.png`, on `origin/master`). The goal is **Aethera**: an open-source, self-hosted deployment/infrastructure platform that can replace Coolify for the Turn One infrastructure (frontend, backend, T1API prod/dev, docs, PostgreSQL, Redis, InfluxDB, Grafana, Prometheus) without being Turn One-specific.

This plan covers the **Turn One migration MVP**, delivered in phases, with later phases outlined. I (Opus) act only as orchestrator; all implementation is done by **Sonnet 5.5 subagents** in this session.

### Locked decisions (from you)
| Area | Choice |
|---|---|
| Control plane | **.NET 10** / ASP.NET Core, EF Core + Npgsql, PostgreSQL, Redis |
| Server agent | **Go** single static binary (amd64 + arm64) |
| Agent link | **Both**: agent dials out over gRPC + mTLS (primary); SSH transport for bootstrap/install and fallback |
| Reverse proxy | **Traefik** (Docker labels, Let's Encrypt), behind a proxy abstraction |
| Frontend | **Next.js + Tailwind + shadcn/ui**, static export served by the .NET API (single container) |
| Workers | Sonnet subagents in this session, worktree isolation for parallel work |
| Review | **One PR per phase** to `master` from `claude/determined-meitner-yu61ri` |
| UI | Inspired by the ContextOS screenshots (see Design system below) |

Defaults I'll assume unless you object: license **Apache-2.0**; Go module path `github.com/mihaim21/aethera-forge/agent`.

---

## Architecture

```
Web UI (Next.js static) ─┐
CLI (later) ─────────────┼─► Aethera API (.NET 10, REST + OpenAPI, SignalR for live logs)
Git webhooks ────────────┘        │  Control plane: auth, projects, apps, jobs, deploy orchestration
                                  │  Postgres (state, jobs, audit, metrics)  Redis (log pub/sub, locks)
                                  ▼
                       IServerTransport ──► AgentTransport (gRPC bidi stream, mTLS, agent dials out)
                                       └──► SshTransport   (bootstrap install + fallback Docker CLI)
                                  ▼
                       Go agent on each server: Docker SDK ops, builds (BuildKit/Nixpacks),
                       Compose, Traefik mgmt, metrics (gopsutil), log streaming, heartbeat
```

Key design rules:
- **API-first**: the UI has no logic the API lacks (spec §59). OpenAPI → generated TS client.
- **Contract-first**: `proto/` (agent protocol), OpenAPI and the DB schema are frozen at end of Phase 0. Only the orchestrator changes contracts.
- **Extensibility interfaces** from day one: `IBuildEngine`, `IDeploymentStrategy`, `IProxyProvider`, `IGitProvider`, `IServerTransport`, `INotificationProvider` (stub).
- **Jobs**: durable Postgres jobs table (`FOR UPDATE SKIP LOCKED`) run by a .NET `BackgroundService`. States Queued/Running/Succeeded/Failed/Cancelled. Per-application lock, cancellation, retries.
- **Secrets**: AES-256-GCM envelope encryption with a master key from `AETHERA_MASTER_KEY`. Masked in the UI, redacted from logs, injected at deploy time only.
- **Agent safety**: a typed command allowlist (no arbitrary shell). Agent certs come from an internal CA in the control plane; one-time join tokens.
- **Server model** carries `roles[]` (master/build/storage/ci) and resource fields from the start, so placement can be added later without a schema rewrite.
- **Failure distinctions** (§44): control plane / server / agent / Docker / application unavailable are separate status fields.

### Repo layout (created in Phase 0)
```
/src/control-plane/  Aethera.sln
    Aethera.Api/            (endpoints, auth, SignalR hubs, gRPC agent gateway, static UI hosting)
    Aethera.Domain/         (entities, enums, interfaces)
    Aethera.Infrastructure/ (EF Core, migrations, Redis, crypto, SSH, jobs)
    Aethera.Engine/         (build detection, deployment engine, proxy/Traefik, git providers)
    tests/ Aethera.*.Tests  (xUnit; integration tests against local Postgres)
/src/agent/          Go module (cmd/aethera-agent, internal/{docker,metrics,build,proxy,transport})
/src/web/            Next.js app (pnpm), design tokens, generated API client
/proto/aethera/agent/v1/*.proto   (generated Go + C# code committed)
/deploy/             docker-compose.dev.yml, docker-compose.prod.yml, install.sh, Dockerfiles
/docs/architecture/  ADRs + component docs; docs/roadmap.md (live checklist)
/CLAUDE.md           conventions every worker reads first
/.github/workflows/  ci.yml (dotnet, go, web), release.yml (later)
```

---

## Design system (from the screenshots)

- **Dark default**: near-black `#0b0d0d` / surface `#111414`, hairline borders `#1f2523`. **Accent lime** `#b8f26b` (hover `#c8f78a`). Text off-white `#e6e8e6`, muted `#8a918d`. Light mode: off-white surface, same lime accent darkened for contrast.
- Type: a grotesk sans (Inter/Geist) with tight tracking for headings; **monospace** (JetBrains Mono/Geist Mono) for eyebrow labels (`── DEPLOYMENTS`), IDs, `01/02` indices, logs and status lines (`[ ok ]`).
- Motifs: **dotted-grid canvas**, dashed vertical connectors for pipelines (deployment lifecycle stepper like "the operating loop"), terminal-window cards (3 dots + `aethera://...` title), numbered square cards, a subtle particle/pulse dot for live status. Square corners, thin 1px borders, almost no shadows.
- Shell: left icon rail + collapsible sidebar, top **command palette** (Ctrl+K), bottom **status bar** (`● Connected · 3 servers · 1 job running`), theme toggle, and an animated boot screen on login.
- Tokens are CSS variables in Tailwind config and shadcn theme. A `/design` route in dev renders all components for review.

---

## Orchestration model (how I run Sonnet workers)

1. **Briefs**: each work package (WP) goes to an `Agent` call with `model: "sonnet"`, `subagent_type: "general-purpose"`. Parallel WPs use `isolation: "worktree"` and run in the background, at most 3–4 at once. Each brief contains: goal, **owned paths** (no overlap between parallel WPs), contracts to obey, acceptance commands that must pass, out-of-scope items, and "read CLAUDE.md + relevant ADR first".
2. **Gate per WP** (done by me): read the diff, run build/tests/lint myself, run `/code-review` (medium) and `/security-review` for auth/secrets/agent/webhook WPs. Send fix-ups back to the **same worker** via SendMessage (keeps its context) until green, then merge into the designated branch.
3. **Integration**: after each wave I run an integration check (dev compose up, smoke tests), update `docs/roadmap.md`, commit and push to `claude/determined-meitner-yu61ri`.
4. **End of phase**: open or update a PR to `master` with a summary and verification evidence, then offer to subscribe to PR activity for CI and review fixes. If a phase PR is merged, the branch restarts from `master` for the next phase. If it isn't merged yet, the next phase stacks on it.
5. **Tracking**: a session task list mirrors the WPs. I'll ask you whenever a WP surfaces a real product decision.

Environment notes (verified): Go 1.24, Node 22, pnpm, Postgres 16 and Redis 7 are present. **.NET is missing** (Phase 0 installs .NET 10 via `dotnet-install.sh`; NuGet is reachable). The Docker CLI is present but the daemon isn't running. The orchestrator will try to start `dockerd` for e2e tests, falling back to Docker-free unit tests with fakes plus real-VPS testing by you.

---

## Phases & work packages

`∥` = runs in parallel within the wave.

### Phase 0 — Foundation (sequential, 2 workers) → PR #1
- **WP0.1 Toolchain & skeleton**: rebase the branch onto `origin/master`. Install .NET 10 SDK, buf and protoc-gen-go. Add a SessionStart hook (via the `session-start-hook` skill) so future sessions have the tools. Scaffold the solution, Go module and Next.js app, plus `docker-compose.dev.yml` (Postgres, Redis), `CLAUDE.md`, LICENSE, README, `.gitignore`, and a CI workflow covering dotnet build/test, `go vet`/`go test` and pnpm lint/build.
- **WP0.2 Contracts**: domain model plus initial EF migration (Organization, User, Team/Role, ApiToken, Project, Environment, Application, Service, Server(roles, resources), Deployment, Build, Job, JobLog, EnvVar, Secret, Volume, Domain, Registry, GitSource, AuditEvent, MetricSample). Agent protocol `.proto` (Enroll, Connect bidi stream, Heartbeat, Metrics, Command/CommandResult, LogChunk) with generated code. API conventions ADR (REST shape, errors as ProblemDetails, pagination, OpenAPI via `Microsoft.AspNetCore.OpenApi` + Scalar). Design tokens doc.

### Phase 1 — Control plane core + UI shell → PR #2
- ∥ **WP1.1 Auth & access**: first-run owner setup; email/password with ASP.NET Identity hasher and cookie sessions; API tokens (hashed, scoped, expiry, last-used, shown once); roles Owner/Admin/Developer/Viewer with policy authorization; audit log middleware.
- ∥ **WP1.2 Resource API**: CRUD for orgs, projects, environments, applications, services, env vars (import/export), secrets (encrypted, masked), volumes, domains (duplicate detection), registries. Validation and OpenAPI annotations.
- ∥ **WP1.3 Job system**: durable queue, worker host, per-app locks (Postgres advisory locks), cancellation, retries, job logs persisted plus live fan-out via Redis → SignalR hub. `/health`, `/ready`, `/metrics` (prometheus-net).
- ∥ **WP1.4 UI shell**: design system, app shell (rail, sidebar, command palette, status bar, theme toggle, dotted canvas, boot screen), login/setup, an OpenAPI→TS client generator script, and a `/design` component gallery.

### Phase 2 — Servers & agent → PR #3
- ∥ **WP2.1 Go agent**: enrollment (join token → CSR → cert), persistent gRPC stream with reconnect/backoff, heartbeat, gopsutil metrics every 10s, resource discovery (§55), Docker SDK ops (containers/images/volumes/networks: list/inspect/start/stop/restart/remove/pull/prune), log streaming, typed command dispatcher, systemd unit, cross-compiled binaries.
- ∥ **WP2.2 Agent gateway (.NET)**: Grpc.AspNetCore server, internal CA (issue/rotate/revoke agent certs), session registry, command RPC with timeouts, metrics ingestion with retention/downsampling, server status state machine, and an `IServerTransport` abstraction.
- **WP2.3 SSH transport** (after WP2.2): SSH.NET-based server add (key/password stored as a secret), one-click agent install over SSH, and a fallback transport running allowlisted Docker CLI commands with polled metrics.
- ∥ **WP2.4 UI servers**: server list, add-server wizard (copy a join command, or install via SSH), server detail (system info, live CPU/RAM/disk/net charts, containers, Docker inventory, maintenance actions with destructive-action confirmations).

### Phase 3 — Build & deploy engine → PR #4
- ∥ **WP3.1 Build engines** (in the agent; the .NET side orchestrates): `IBuildEngine` implementations for Dockerfile (BuildKit, with cache), Docker image (pull only), Nixpacks, Static (build in a Node container, then serve from a small Caddy/nginx image), and Docker Compose (project-scoped, user file kept as is plus an Aethera override file for labels and networks). Repository build detection (§4.2) with an override. Live build log streaming.
- ∥ **WP3.2 Deployment engine**: lifecycle state machine (Source→Build→Image→Server→Container→Network→Domain→Health→Running) and image tagging `aethera/<app>:<deploymentId>`. Strategies: Recreate, plus a low-downtime start-new→health-check→switch-route→stop-old strategy. Also health checks (HTTP/TCP/container), rollback to any successful deployment, redeploy, restart, stop/start, failure-reason capture, and an image cleanup policy.
- ∥ **WP3.3 Traefik & networking**: the agent installs and manages a Traefik container per server. `IProxyProvider` generates Docker labels for routes. Let's Encrypt HTTP-01 for automatic HTTPS. Managed per-project internal networks, port model (container/server/public/route), and a DNS check (domain → expected server IP, mismatch warning).
- ∥ **WP3.4 Git integration**: `IGitProvider` for GitHub, GitLab and generic Git; private repos via deploy key or token; branch/commit selection; webhook endpoints with signature validation (GitHub HMAC, GitLab token); auto-deploy rules (branch filter, on/off).

### Phase 4 — Product UI & services → PR #5
- ∥ **WP4.1 App flows**: projects (with project templates), the "Create Application" wizard (§58: repo → build method → env → server → domain → deploy), and application page tabs (Overview, Deployments, Logs, Environment, Domains, Storage, Networking, Resources, Build, Health, Settings).
- ∥ **WP4.2 Deployments UI**: deployment list and detail (pipeline stepper using the dashed-connector motif, live build/deploy logs with search/filter/download, failure step highlighting), rollback picker, live app logs.
- ∥ **WP4.3 Service templates**: a template format (YAML, editable after creation) plus PostgreSQL, MySQL, MariaDB, Redis, MongoDB (version selection, generated credentials as secrets, volumes, health checks, internal-only networking) and the Turn One extras: InfluxDB, Grafana, Prometheus, MinIO.
- ∥ **WP4.4 Dashboard & ops pages**: dashboard (server/app status, recent and failed deployments, resource usage, active jobs), plus Domains, Secrets, Registries, Settings (users, tokens, audit log viewer).

### Phase 5 — Ship it → PR #6 (MVP done)
- ∥ **WP5.1 Packaging**: production multi-stage Dockerfile (API + static UI), `docker-compose.prod.yml` (aethera, postgres, redis, traefik, local agent), and an **`install.sh`** one-liner like Coolify's (checks OS, installs Docker, generates master key and secrets, starts the stack, prints the URL). A release workflow publishes GHCR images (amd64/arm64) and agent binaries.
- ∥ **WP5.2 Self-ops**: self-update (check GitHub releases → notify → pull → restart, migrations applied safely at startup with backup), maintenance jobs (image/volume/network/build-cache prune), and log retention.
- ∥ **WP5.3 Docs**: install guide, architecture docs, a **Coolify → Aethera migration guide for Turn One**, CONTRIBUTING, CHANGELOG, roadmap.

### Later phases (outlined, planned in detail after the MVP)
6. **Multi-server roles & resources**: role-aware placement (build servers push to a registry, storage/DB servers), CPU/RAM limits and reservations UI, priorities, capacity view across servers.
7. **Secure networking**: WireGuard mesh between servers (or Tailscale integration), private-network deploys.
8. **Backups**: S3/B2/local targets, DB-aware dumps, schedules, retention, restore.
9. **Alerts & notifications**: provider-based (email, Discord, Slack, webhooks), thresholds, certificate expiry.
10. **Scheduled jobs**, **CLI** (Go, same API), **config-as-code** (`aethera.yaml`), environment promotion, blue/green and canary, OIDC/GitHub login, integrated registry.

---

## Verification

- **Per WP**: `dotnet build && dotnet test` (integration tests use the local Postgres/Redis), `go vet ./... && go test ./...`, `pnpm lint && pnpm build`, plus the WP's own acceptance commands. `/code-review` on every merge and `/security-review` on WP1.1, 1.2 (secrets), 2.1–2.3, 3.4.
- **Per phase**: CI green on the PR. Smoke script `deploy/smoke.sh` (grows each phase) runs against `docker-compose.dev.yml`.
  - P1: setup owner → login → create project/app via the API and UI.
  - P2: start the agent locally → enroll → heartbeat and metrics visible in the UI; SSH install tested against a local sshd.
  - P3: deploy `nginx:latest`, a sample Dockerfile app, a Nixpacks Node app, a static Vite site and a Compose file. Verify health, route via Traefik, then roll back.
  - P4: run the full wizard flow in the browser with Playwright (Chromium is preinstalled), with light/dark screenshots attached to the PR.
- **MVP acceptance (you, on a real VPS + domain)**: `curl … | bash` install → add a second server → deploy a Turn One-like stack (Next.js site from GitHub with auto-deploy webhook, a .NET API via Dockerfile, a PostgreSQL template, Redis) with HTTPS domains → push a commit, watch the auto-deploy → roll back → stop the agent, then confirm the "agent unavailable" status.

## Risks / notes
- No Docker daemon in this container, so full e2e for Phases 3–5 may need `dockerd` started manually or your VPS. Unit tests use fakes behind `IServerTransport`.
- Scope is large: I expect several sessions. `docs/roadmap.md` and `CLAUDE.md` keep each session (and each worker) cheap to bootstrap.
- Nixpacks is in maintenance mode upstream. It stays behind `IBuildEngine` so Railpack can be added later (spec §3.7).
