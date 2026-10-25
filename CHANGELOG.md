# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows [Semantic Versioning](https://semver.org/). The release workflow publishes the section of the version being released as the release notes.

## [Unreleased]

First release (MVP). Everything below is new.

### Install and operate
- One-line installer (`install.sh`): Docker if missing, generated database password and master key, control plane + PostgreSQL + Redis, optional first owner and an agent for the same machine, connected automatically. `/install-agent.sh` adds more servers (systemd service, checksum-verified download).
- `install.sh --from-source [ref]` (also the automatic fallback while no release exists) builds the stack from the repository; `install.ps1` does the same on Windows/macOS with Docker Desktop.
- Release images on GHCR for linux/amd64 and linux/arm64 (`control-plane`, `agent`, `updater`), static agent binaries and `SHA256SUMS` as release assets.
- Updates: the control plane checks the GitHub releases and shows the new version with its notes; one click (or `aethera update`) backs up the database, pulls the images, restarts and **rolls back with the database restored** if the new version does not start. Agents update themselves on request.
- **One server is enough**: the stack contains an agent container for the machine it runs on. Once the first owner has registered, the control plane connects it as the `local` server by itself (a one-time join token handed over through a shared volume), so applications can be deployed right away. **Use this machine** on the Servers page does the same on demand; `AETHERA_AUTO_LOCAL_SERVER=false` / `--no-agent` opt out.
- Database safety: a backup before every migration of an existing installation, a refusal to start on a database newer than the code, daily `pg_dump` backups (newest seven kept).
- Housekeeping: log, event, session and audit-log retention, and a weekly prune of images, networks and build cache on every connected server (never volumes, rollback images kept). Settings > System shows the values, the backups and the last run, and can run it now.
- The `aethera` command: `status`, `logs`, `update`, `backup`, `restore`.

### Product
- Projects (with templates), applications from Git (public, token, deploy key), images and compose files; Dockerfile, Nixpacks, static and image builds with detection; environment variables with secrets; domains with DNS checks and Let's Encrypt through Aethera's own Traefik per server; volumes, networks, resource limits and health checks.
- Deployments: nine-step pipeline, live build and deploy logs (search, filter, download), low-downtime and recreate strategies, automatic deploys from GitHub, GitLab and generic webhooks, rollback to any kept image, failure shown at the step that failed.
- Services from YAML templates (PostgreSQL, MySQL, MariaDB, Redis, MongoDB, InfluxDB, Grafana, Prometheus, MinIO) with generated credentials stored as secrets, reachable from the applications of the same environment by name.
- Dashboard, servers (live status on separate axes, metrics, Docker inventory, prune), domains, secrets (reveal for administrators, audited), registries, members and roles, API tokens with scopes, git credentials and the audit log.
- Web UI: dark by default with a light theme, keyboard operable, usable on a phone.

### Platform
- .NET control plane (REST API with OpenAPI, durable job queue on PostgreSQL, live logs over SignalR), Go agent (outbound mutual-TLS gRPC stream, typed allowlisted commands only, SSH fallback transport for servers without an agent), static Next.js UI served by the API.
- Trust model: roles (viewer, developer, admin, owner), token scopes, secrets encrypted with a master key, root-equivalent container settings restricted to administrators ([ADR 0006](docs/architecture/0006-trust-model.md)).

### Known limits
- No backups of your own databases and volumes yet (the control plane's own database is backed up), no notifications, no scheduled jobs, no CLI beyond `aethera` on the host.
- Compose applications need the file pasted in; compose services with `build:` are not supported.
- The registry "test connection" endpoint is not implemented; the Nixpacks engine has not been run against the real CLI.
- Agent binaries are not signed; the checksum next to the release is the integrity check.
- One API instance. Backups stay on the machine they were taken on.

Details: [ADR 0007](docs/architecture/0007-build-and-deploy.md), [ADR 0008](docs/architecture/0008-product-ui.md), [ADR 0009](docs/architecture/0009-packaging-and-self-ops.md).
