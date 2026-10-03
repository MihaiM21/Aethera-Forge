# Aethera

> **Self-hosted infrastructure. Simple deployments. Full control.**

Aethera is an open-source self-hosted deployment and infrastructure management platform for running applications, services, and containers on your own servers. It is an alternative to tools like Coolify, built and tested against real production infrastructure.

**Status:** early development (Phase 0, foundation). See [`docs/roadmap.md`](docs/roadmap.md) and [`docs/plan.md`](docs/plan.md).

## Components

| Component | Tech | Path |
|---|---|---|
| Control plane (API, jobs, deploy orchestration) | .NET 10 / ASP.NET Core, EF Core, PostgreSQL, Redis | `src/control-plane` |
| Server agent | Go (static binary, amd64 + arm64) | `src/agent` |
| Web UI | Next.js (App Router, static export), Tailwind v4, shadcn/ui | `src/web` |
| Agent protocol | Protobuf / gRPC | `proto` |

## Quick dev start

Prerequisites: .NET 10 SDK, Go 1.24, Node 22 with pnpm 10, Docker (for Postgres and Redis), `buf` (only when changing `proto/`).

```bash
# Dependencies (PostgreSQL 17, Redis 7)
docker compose -f deploy/docker-compose.dev.yml up -d

# Control plane
cd src/control-plane
dotnet build && dotnet test
dotnet run --project src/Aethera.Api     # GET /health

# Agent
cd src/agent
make all            # vet, test, build
make cross          # linux/amd64 + linux/arm64 static binaries in dist/

# Web UI
cd src/web
pnpm install
pnpm dev            # or: pnpm lint && pnpm build
```

## Repository layout

```
src/control-plane/   .NET solution (Api, Domain, Infrastructure, Engine, tests)
src/agent/           Go agent module
src/web/             Next.js app
proto/               Agent protocol (.proto), configured by buf.yaml / buf.gen.yaml
deploy/              Docker Compose files and deployment assets
docs/                Plan, roadmap, architecture decision records, product spec
scripts/             Tooling scripts (e.g. Claude Code session bootstrap)
```

## License

Apache-2.0, see [`LICENSE`](LICENSE).
