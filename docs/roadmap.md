# Aethera Roadmap

Live checklist derived from [`docs/plan.md`](plan.md). Tick a work package when it is merged.

## Phase 0 — Foundation
- [x] WP0.1 Toolchain & skeleton
- [x] WP0.2a Contracts: agent `.proto`, ADRs 0002-0004, design tokens
- [x] WP0.2b Domain model + initial EF migration (`src/control-plane/docs/schema.md`)
- [x] WP0.3 Protocol code generation (Go via buf, C# via Grpc.Tools, CI `proto` job)

## Phase 1 — Control plane core + UI shell
- [x] WP1.0 Shared API seams (module stubs, abstractions, errors/validation/pagination, authorization, OpenAPI, test support)
- [x] WP1.1 Auth & access
- [x] WP1.2 Resource API
- [x] WP1.3 Job system
- [x] WP1.4 UI shell
- [x] WP1.5 Integration & hardening (hub Origin check, job tenancy, static UI hosting, readiness, forwarded headers, CI with Postgres/Redis, `deploy/smoke.sh`)

## Phase 2 — Servers & agent
- [x] WP2.1 Go agent
- [x] WP2.2 Agent gateway (.NET)
- [x] WP2.3 SSH transport
- [x] WP2.4 UI servers
- [x] WP2.5 Agent <-> gateway end-to-end (`deploy/agent-e2e.sh`, interop fixes)

## Phase 3 — Build & deploy engine
- [x] WP3.1 Build engines
- [x] WP3.2 Deployment engine
- [x] WP3.3 Traefik & networking
- [x] WP3.4 Git integration (details and known limits: `docs/architecture/0007-build-and-deploy.md`)

## Phase 4 — Product UI & services
- [x] WP4.1 App flows
- [x] WP4.2 Deployments UI
- [x] WP4.3 Service templates
- [x] WP4.4 Dashboard & ops pages (details, API additions and known limits: `docs/architecture/0008-product-ui.md`; verified by `E2E_PHASES=ui bash deploy/agent-e2e.sh`)

## Phase 5 — Ship it (MVP)
- [x] WP5.1 Packaging (Dockerfile, compose, install.sh, release workflow)
- [x] WP5.2 Self-ops (self-update, maintenance jobs, log retention)
- [x] WP5.3 Docs (install, architecture, Coolify migration, CONTRIBUTING, CHANGELOG; details and known limits: `docs/architecture/0009-packaging-and-self-ops.md`; verified by `bash deploy/install-e2e.sh`)

## R1 — v0.1.0 beta (the Coolify cut-over; plan: [`release-0.1.md`](release-0.1.md))
- [x] R1.0 Contracts (proto, migration, seams, ADR 0010)
- [x] R1.1 Backups (S3/R2, Postgres/MongoDB/Redis/volumes, encryption, schedules, retention, restore) — Phase 8
- [x] R1.2 Email notifications & alerts (SMTP/Resend, webhook, per-event settings, alert store) — Phase 9
- [x] R1.3 Scheduled tasks (`container.exec`, cron, history) — part of Phase 10
- [x] R1.4 Compose from the repository (`build:`, domains per service)
- [x] R1.5 Domains & proxy (Cloudflare DNS-01, wildcards, www redirect, strip prefix, basic auth, custom labels)
- [x] R1.6 Hardening (bug hunt fixes, `aethera reset-password`, registry test, docs, beta labelling)
- [x] R1.7 Coolify-style deploys and domains (automatic sslip.io / wildcard domains, dashboard domain in Settings, ports from EXPOSE, liveness checks, safe recreate, compose data that survives deploys; ADR 0011)
- [ ] R1 verification, PR, `v0.1.0-rc.1`, owner test on the VPS, `v0.1.0`

## R2 — v0.2.0 (two servers; plan: [`release-0.2.md`](release-0.2.md))
- [x] R2.0 Contracts (mesh proto, build quota, priority, migration, seams, ADR 0012)
- [ ] R2.1 Multi-server roles & resources (placement, limits and priorities, build quota, capacity view, build → registry → run elsewhere, move) — Phase 6
- [ ] R2.2 Secure networking (WireGuard mesh, Tailscale option, private-network deploys) — Phase 7
- [ ] R2.3 PR preview deployments (GitHub + GitLab, wildcard domains, PR comments, cleanup on close)
- [ ] R2 verification, PR, `v0.2.0-rc.1`, owner test with the second server, `v0.2.0`

## R3 — v0.3+
- [ ] CLI, config-as-code (`aethera.yaml`), environment promotion, blue/green & canary, OIDC/GitHub login, integrated registry
- [ ] Discord/Slack/Telegram notifications, Cloudflare DNS record automation, Caddy proxy, email invitations and password reset

v1.0.0 follows about a month of production use (a real restore, two servers, no data-loss bug).
