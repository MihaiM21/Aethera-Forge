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
- [ ] WP2.1 Go agent
- [x] WP2.2 Agent gateway (.NET)
- [ ] WP2.3 SSH transport
- [ ] WP2.4 UI servers

## Phase 3 — Build & deploy engine
- [ ] WP3.1 Build engines
- [ ] WP3.2 Deployment engine
- [ ] WP3.3 Traefik & networking
- [ ] WP3.4 Git integration

## Phase 4 — Product UI & services
- [ ] WP4.1 App flows
- [ ] WP4.2 Deployments UI
- [ ] WP4.3 Service templates
- [ ] WP4.4 Dashboard & ops pages

## Phase 5 — Ship it (MVP)
- [ ] WP5.1 Packaging (Dockerfile, compose, install.sh, release workflow)
- [ ] WP5.2 Self-ops (self-update, maintenance jobs, log retention)
- [ ] WP5.3 Docs (install, architecture, Coolify migration, CONTRIBUTING, CHANGELOG)

## Later phases
- [ ] 6. Multi-server roles & resources
- [ ] 7. Secure networking (WireGuard / Tailscale)
- [ ] 8. Backups
- [ ] 9. Alerts & notifications
- [ ] 10. Scheduled jobs, CLI, config-as-code, environment promotion, blue/green & canary, OIDC, integrated registry
