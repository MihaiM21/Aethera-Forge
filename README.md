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

## Deploy with Docker Compose

Master (UI, API, PostgreSQL, Redis; the master key is generated on first start and kept in the `aethera_data` volume, back it up):

```bash
cp deploy/.env.example deploy/.env        # set AETHERA_PUBLIC_HOST to a name/IP the slaves can reach
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build
# UI: http://<host>:8080, agents dial <host>:9443 (mTLS, no TLS-terminating proxy in front of it)
```

Slave server: create it in the UI (Servers > Add server), copy the endpoint, CA fingerprint and join token, then on the slave:

```bash
cp deploy/.env.agent.example deploy/.env.agent      # paste the three values
docker compose -f deploy/docker-compose.agent.yml --env-file deploy/.env.agent up -d --build
```

The agent enrolls once (state in `/var/lib/aethera`), needs only outbound access, and the token is not needed afterwards. To manage the master machine too, add `--profile agent` to the master command with the same three values in `deploy/.env`.

## Quick dev start

Prerequisites: .NET 10 SDK, Go 1.24, Node 22 with pnpm 10, Docker (for Postgres and Redis), `buf` (only when changing `proto/`).

```bash
# Dependencies (PostgreSQL 17, Redis 7)
docker compose -f deploy/docker-compose.dev.yml up -d

# Control plane
cd src/control-plane
dotnet build && dotnet test
dotnet run --project src/Aethera.Api     # GET http://localhost:5033/health

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
