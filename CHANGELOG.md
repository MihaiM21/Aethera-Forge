# Changelog

All notable changes are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows [Semantic Versioning](https://semver.org/). The release workflow publishes the section of the version being released as the release notes.

## [Unreleased]

### Fixed
- Updates (`aethera update` and *Update to X*) install the new release's compose file instead of keeping the old one, so a release that changes the stack itself (the agent's `NET_ADMIN` for the private network, the control plane's proxy alias) takes effect without re-running `install.sh`. The old file is kept and comes back on a rollback; an installation that cannot download it keeps its current file. `AETHERA_RELEASE_URL` points it at a mirror. Installations from before this fix must update once with `install.sh --version <new version>`.

## [0.1.0] - unreleased

First release, **beta**: the API and the configuration may still change between 0.x releases, and you should back up before every update. Everything below is new. Release candidates are published as `v0.1.0-rc.N` pre-releases and installed with `install.sh --version 0.1.0-rc.N`.

### Release 0.1 (what the Turn One cut-over from Coolify needs)
- **Backups** to any S3-compatible destination (Cloudflare R2 recommended) or a local folder: PostgreSQL (`pg_dump -Fc`), MongoDB (`mongodump --archive --gzip`), Redis (RDB, off by default), volume archives and Aethera's own database. The agent streams to S3 with client-side encryption (key derived from the master key); a cron schedule per service (default 03:00), "back up now", retention 7 daily / 4 weekly / 6 monthly, restore into the same service (confirmed) or into a new one.
- **Email notifications** like Coolify (SMTP or Resend, a test email, per-event settings for deployments, backups, scheduled tasks, server unreachable, disk and memory, containers, certificates and updates; success events off by default; recipients are members or a list) and a signed **webhook** channel. Alerts are stored with deduplication, recovery messages and a reminder every six hours while open; the Monitoring section shows them.
- **Scheduled tasks** (Coolify-style cron): a command run inside the workload's container through a typed, allowlisted agent command (never a host shell), "run now", history with logs, email on failure.
- **Docker Compose from the repository**: the compose file is read from the Git repository (path configurable), `build:` services are supported, and each compose service can have its own domains. Inline compose still works.
- **Domains and proxy**: Cloudflare DNS-01 with an API token (wildcards, certificates before the DNS switch), a DNS check that understands the Cloudflare proxy, www to apex redirects, path-prefix stripping, basic auth and custom Traefik labels (administrators).
- **Hardening**: a full bug hunt before the release. Sign-in works over plain `http://<ip>:8080` (the session cookie now follows the request scheme; the login page warns about plain HTTP); this machine's agent runs in the host network namespace on Linux, so template databases pass their health checks and the agent survives a restart of the control plane (`docker-compose.desktop.yml` keeps the old mode on Docker Desktop); `install.sh --version` accepts `v0.1.0-rc.1` and `0.1.0-rc.1`; the Redis template applies its generated password and keeps an append-only file; `sudo aethera reset-password <email>`; registry **Test connection** (Docker Registry v2, Bearer and Basic); multi-arch images are cross-compiled instead of built under QEMU.
- **Beta labelling**: a `beta` badge next to the version in the UI, README and install guide say what beta means.
- Docs: install guide (HTTPS needs `--acme-email`, bind mounts only under `/var/lib/aethera`, public GHCR packages, agent networking on Docker Desktop and Linux), and the Coolify migration guide with MongoDB, backups, Cloudflare and compose from the repository.

### Install and operate
- One-line installer (`install.sh`): Docker if missing, generated database password and master key, control plane + PostgreSQL + Redis, optional first owner and an agent for the same machine, connected automatically. `/install-agent.sh` adds more servers (systemd service, checksum-verified download).
- `install.sh --from-source [ref]` (also the automatic fallback while no release exists) builds the stack from the repository; `install.ps1` does the same on Windows/macOS with Docker Desktop.
- Release images on GHCR for linux/amd64 and linux/arm64 (`control-plane`, `agent`, `updater`), static agent binaries and `SHA256SUMS` as release assets.
- Updates: the control plane checks the GitHub releases and shows the new version with its notes; one click (or `aethera update`) backs up the database, pulls the images, restarts and **rolls back with the database restored** if the new version does not start. Agents update themselves on request.
- **One server is enough**: the stack contains an agent container for the machine it runs on. Once the first owner has registered, the control plane connects it as the `local` server by itself (a one-time join token handed over through a shared volume), so applications can be deployed right away. **Use this machine** on the Servers page does the same on demand; `AETHERA_AUTO_LOCAL_SERVER=false` / `--no-agent` opt out.
- Database safety: a backup before every migration of an existing installation, a refusal to start on a database newer than the code, daily `pg_dump` backups (newest seven kept).
- Housekeeping: log, event, session and audit-log retention, and a weekly prune of images, networks and build cache on every connected server (never volumes, rollback images kept). Settings > System shows the values, the backups and the last run, and can run it now.
- The `aethera` command: `status`, `logs`, `update`, `backup`, `restore`, `reset-password`.

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
- No Discord, Slack or Telegram notifications, no CLI beyond `aethera` on the host, no automatic DNS record creation (R3).
- Docker Desktop (the Windows and macOS installer) is for trying Aethera out: the agent there cannot reach containers on environment networks.
- The Nixpacks engine has not been run against the real CLI.
- Agent binaries are not signed; the checksum next to the release is the integrity check.
- One API instance. Backups stay on the machine they were taken on.

Details: [ADR 0007](docs/architecture/0007-build-and-deploy.md), [ADR 0008](docs/architecture/0008-product-ui.md), [ADR 0009](docs/architecture/0009-packaging-and-self-ops.md).
