# Development

How to run Aethera from a checkout. The short version for contributors is in [`CONTRIBUTING.md`](../CONTRIBUTING.md); exact build and test commands are in [`CLAUDE.md`](../CLAUDE.md). For a real installation use the [installer](install.md).

## Docker Compose stack from a checkout

Builds the images from this checkout (no updater; for a real installation use the [installer](install.md)). Master (UI, API, PostgreSQL, Redis; the master key is generated on first start and kept in the `aethera_data` volume, back it up):

```bash
cp deploy/.env.example deploy/.env        # set AETHERA_PUBLIC_HOST to a name/IP the slaves can reach
docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build
# Docker Desktop (Windows, macOS): add  -f deploy/docker-compose.desktop.yml  after the first -f (install.ps1 does)
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

