# Release 0.1 (beta) — plan

Decided with the owner on 2026-10-23. R1 is what the Turn One cut-over from Coolify needs; everything else waits for R2/R3. Status lives in [`roadmap.md`](roadmap.md); the MVP plan is [`plan.md`](plan.md).

## Decisions

| Area | Decision |
|---|---|
| Target | One **new** VPS next to Coolify (Ubuntu, Cloudflare DNS). Coolify stays untouched a week as the rollback, then the old server is wiped and joins Aethera as server #2. |
| Workloads | Turn One website (prod, dev), T1API (prod, dev), docs, status website; MongoDB, PostgreSQL, Redis. Built mostly with **Dockerfile** and **Docker Compose**; Nixpacks is low priority. |
| Versioning | R1 = **v0.1.0**, a normal GitHub release (not a pre-release, so `releases/latest` and the update check find it), called **beta** in README, docs and UI. Test builds are `v0.1.0-rc.N` (pre-releases, installed with `install.sh --version`). R2 = v0.2.0. **v1.0.0** after about a month of Turn One in production, a real restore, two servers and no data-loss bug. |
| Proxy | **Traefik only** in v0.x. Caddy/nginx as server proxies maybe later behind `IProxyProvider`. |
| DNS | Cloudflare **DNS-01** with an API token (`Zone:DNS:Edit`): wildcards, certificates before the DNS switch. Creating DNS records automatically is R3. The DNS check understands the Cloudflare proxy (orange cloud). |
| Backups | Any S3-compatible destination (Cloudflare R2 recommended) + local (tests). Postgres `pg_dump -Fc`, MongoDB `mongodump --archive --gzip`, Redis RDB (off by default), volume archives, and Aethera's own database. The agent streams to S3; client-side encryption with a key derived from the master key. Cron schedule per service (default 03:00) + "back up now". Retention 7 daily / 4 weekly / 6 monthly. Restore into the same service (confirmed) or into a new service. |
| Notifications | **Email like Coolify**: SMTP or Resend, test email, per-event checkboxes (deployments, backups, scheduled tasks, server unreachable, disk/RAM, containers, certificates, updates; "success" events off by default), recipients = members or a list. A generic signed webhook channel too. Recovery messages, a reminder every 6 h while open, alerts stored (the Monitoring section becomes real). Discord/Slack/Telegram are R3. |
| Scheduled tasks | Coolify-style: a command run **inside the workload's container** (`container.exec`, a typed allowlisted agent command with an argument vector — never a host shell). Cron, "run now", history with logs, email on failure. |
| Compose | The compose file is read **from the repository** (path in the repo), `build:` is supported, and **each compose service can have its own domains**. Inline compose stays. |
| Domains | www ↔ apex redirect, strip path prefix, basic auth, custom Traefik labels (admins), wildcard hosts. |
| Hardening | A full bug hunt first; `sudo aethera reset-password <email>`; registry "test connection"; GHCR packages made public after the first push. |
| If time is short | Drop basic auth + custom labels first, then the webhook channel. **Backups and email are never cut.** |

## Releases

| Release | Contents |
|---|---|
| **R1 — v0.1.0 (beta)** | This document. |
| **R2 — v0.2.0** | Phase 6 multi-server: roles, placement, CPU/RAM limits, build quota (a BuildKit builder with its own limits), capacity view, build on one server → registry → run on another. Phase 7: WireGuard mesh (Tailscale option), private-network deploys. PR preview deployments. |
| **R3 — v0.3+** | CLI, `aethera.yaml`, OIDC/GitHub login, environment promotion, blue/green and canary, integrated registry, Discord/Slack/Telegram, Cloudflare record automation, Caddy, email invitations and password reset. |

## Work packages (R1)

Contracts first (orchestrator), then the feature packages in parallel worktrees, each owning disjoint paths. Every package: tests required, warning-free builds, `/code-review`; `/security-review` for R1.1, R1.2, R1.3 and R1.4.

- **R1.0 Contracts** (orchestrator): proto additions (`container.exec`, backup run/restore, compose from Git + inspect), domain entities and one migration (backup destinations/policies/runs, email settings, notification channels, alerts, scheduled tasks and runs, DNS providers, domain options), shared seams (cron schedules, alert sink), ADR 0010.
- **R1.1 Backups**: agent backup engine (dump/archive → encrypt → S3 multipart; restore), control-plane scheduler, retention, Aethera's own database to the destination, API, UI (Settings > Backup destinations, service Backups tab, restore flows).
- **R1.2 Notifications & alerts**: email (SMTP, Resend), webhook channel, event catalogue and per-event settings, alert store with dedup/recovery/reminders, detectors (deploy/backup/task failures, agent down, disk/RAM, container died/restarting, certificate expiry, updates), UI (Settings > Notifications, Monitoring).
- **R1.3 Scheduled tasks**: agent `container.exec`, scheduler, run history and logs, API, UI tab on applications and services.
- **R1.4 Compose from the repository**: clone + `build:` in the agent, compose validation on the resolved file, inspect (services, ports), routes per compose service, UI.
- **R1.5 Domains & proxy**: Cloudflare DNS-01 and wildcards, DNS check through Cloudflare, www redirect, strip prefix, basic auth, custom labels, UI.
- **R1.6 Hardening**: fixes from the bug hunt, `aethera reset-password`, registry test connection, docs (migration guide: MongoDB, backups, Cloudflare), beta labelling.

## Verification and hand-over

`dotnet test` with Postgres/Redis, `go test`, `pnpm lint test build`, `deploy/smoke.sh`, `E2E_PHASES=ui deploy/agent-e2e.sh` (extended with a compose-from-repo app, a backup to a local MinIO and its restore, a scheduled task, a test email to a local SMTP sink), `deploy/install-e2e.sh`. Then one PR `release-0.1` → `master`; after the owner confirms, tag `v0.1.0-rc.1` (first real run of the release workflow, make the GHCR packages public), the owner tests on the new VPS, fixes go out as further RCs through the updater, then `v0.1.0` and the cut-over following [`migration-from-coolify.md`](migration-from-coolify.md).
