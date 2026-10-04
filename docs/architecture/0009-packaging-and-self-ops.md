# ADR 0009: Packaging and self-operations

- Status: accepted
- Date: 2026-10-04
- Scope: how Aethera is built, released and installed, and how it updates, backs up and cleans up after itself (Phase 5)
- Related: [ADR 0002](./0002-agent-communication.md) (agent, self-update of the agent), [ADR 0004](./0004-jobs-and-deployments.md) (jobs, maintenance lock keys), [ADR 0006](./0006-trust-model.md) (who may do what)

## Packaging

**Three images, one version.** A release publishes `ghcr.io/<repo>/control-plane`, `agent` and `updater` for linux/amd64 and linux/arm64, all tagged with the release version (`0.1.0`, `0.1`, `latest`; pre-releases get the full version only). The compose file pins them with a single `AETHERA_VERSION`, so a stack never mixes versions.

| Image | Contents | Notes |
|---|---|---|
| `control-plane` | the Next.js export and the .NET API in one image; `pg_dump` 17 | Runs as the unprivileged `$APP_UID`. The master key comes from `AETHERA_MASTER_KEY`, else it is generated once into `/data` by the entrypoint. |
| `agent` | the static Go agent | For this machine only; other servers run the binary under systemd (`/install-agent.sh`). |
| `updater` | `aethera-ctl.sh` on `docker:cli` | Opt-in profile, holds the Docker socket (see Updates). |

**`docker-compose.prod.yml`** has `postgres`, `redis`, `aethera`, and the profiles `agent` (this machine's agent, host network) and `updater`. There is no second Traefik: Aethera deploys its own proxy (`aethera-traefik`, ports 80 and 443) through the agent, and the compose file only puts the control plane on that proxy's network (`aethera-proxy`) with labels for `AETHERA_DOMAIN`. Two proxies would fight over the same ports.

**`install.sh`** (release asset, `curl ... | sudo bash`): installs Docker when missing, writes `<dir>/.env` (mode 0600) with a generated database password and master key, starts the stack, waits for `/ready`, and optionally creates the first owner and enrolls this machine's agent through the public API (server, join token, CA fingerprint, exactly what the UI does). Running it again upgrades in place and never regenerates a secret that is already in the `.env`: a new master key would make every stored secret unreadable.

**`/install-agent.sh`** is embedded in the API (`deploy/install-agent.sh`, one source of truth) and served anonymously, because the join-token command printed by the UI points at it and a new server has no account. It downloads `aethera-agent-linux-<arch>` from the release, verifies the SHA-256 against `SHA256SUMS`, refuses a non-https URL, installs a hardened systemd unit and enrolls with the token passed through the environment, not the command line.

**Release workflow** (`.github/workflows/release.yml`, tag `v*`): the three images, the agent binaries, `SHA256SUMS`, and the scripts and compose file as release assets (the compose file is pinned to the release version). A manual run is a rehearsal: images are built, nothing is pushed or published. The agent binaries are not signed yet; the agent supports an Ed25519 release key (ADR 0002) and the checksum is the only integrity check until a signing key exists.

## Updates

1. **Notify.** `ReleaseChecker` reads the GitHub releases API (`/releases/latest`, or the newest of the list when `IncludePrereleases`) every six hours and keeps the last good answer; a failed check records its reason and never throws. Semantic-version precedence decides whether it is an update (a release is newer than its own pre-releases). `Aethera:Updates:Enabled=false` turns it off for air-gapped installs; `ApiBaseUrl` points it at a mirror. The only thing sent is `User-Agent: Aethera/<version>`.
2. **Pull and restart.** The control plane cannot restart its own containers and must not hold the Docker socket, so the API and an *updater* container talk through files in a shared volume (`/data/updater`): the API writes `request.json` (atomically); the updater, which has the socket and the installation directory, writes `status.json` and touches `alive`. `GET /system/version` reports the updater's state; the UI polls it, and treats failing polls during the restart as "restarting".
3. **The update itself** (`aethera-ctl.sh`, the same script runs as `aethera update` on the host):
   1. `pg_dump` the database into `aethera-pre-update-<time>.dump` (the newest five are kept); a failing backup stops the update before anything changed.
   2. Set the new `AETHERA_VERSION` in `.env` (the old file is kept as `.env.before-<time>`) and pull the images; if they cannot be pulled and are not already on the machine, restore the `.env` and fail.
   3. Recreate `aethera` (and this machine's agent) with `--no-deps`; wait for `/ready`.
   4. If the new version does not come up within the timeout: stop it, put the old `.env` back, **restore the pre-update dump** (the new version may have migrated the schema, and the old one refuses to start on a newer schema), start the old version and report `failed ... rolled back to X`.
   5. The updater replaces itself last, through a short-lived helper container of the same image (`compose up` stops the container it runs in, which would kill it before the new one exists).
4. **Without the updater** (the profile is optional, or the installation was not made by `install.sh`) the UI says so and shows `sudo aethera update`.
5. **Agents on other servers** update through a job (`POST /servers/{id}/agent/update`, `server.agent_update`): the control plane resolves the release asset for the server's architecture and its checksum from `SHA256SUMS` and sends the existing `AgentSelfUpdate` command; the agent downloads, verifies, swaps atomically and rolls back by itself if the new binary does not reconnect. Only the newest release can be installed from the UI.

**Security of the update channel.** The updater is the only component with the Docker socket besides the agent, and it executes a fixed script: the only input from the API is a version string that must match `[0-9A-Za-z.+-]+`, and it only changes `AETHERA_VERSION`, so what runs is whatever the compose file names at that tag in the configured image repository. Whoever controls the release repository controls the update, as with any self-updating software; that is why the check is on by default only against the project's own repository and can be turned off.

## Migrations

`Aethera:Database:AutoMigrate` (on in the image) now goes through `SafeMigrator`:

- **A database newer than the code stops the start** (`DatabaseNewerThanApplicationException`: an applied migration this build does not contain). Running old code on a newer schema corrupts data; the message points at the pre-migration backups.
- **An existing database with pending migrations is backed up first** (`aethera-pre-migration-*.dump`, newest five kept). A failing backup stops the start. A missing `pg_dump` (a developer machine) only logs a warning; `Aethera:Database:BackupBeforeMigrate=false` turns it off.
- EF Core serialises concurrent migrators with its own database lock.

## Maintenance

`MaintenanceService` runs hourly (first pass two minutes after start), one pass at a time across instances (advisory lock). Every task catches its own failure and reports it; the "last run" is stored in `instance_settings` together with when the schedule last ran, so a restart does not repeat a weekly prune.

| Task | Default | Notes |
|---|---|---|
| Log retention | 30 days | `log_chunks` (build and deploy logs) |
| Event retention | 90 days | status events and webhook deliveries |
| Audit retention | 365 days, `0` = forever | the one sanctioned delete of audit rows, in SQL; EF still refuses |
| Session retention | 7 days past expiry or revocation | also expired idempotency records |
| Database backup | daily, newest 7 kept | `pg_dump -Fc` to `/data/backups`, written under a temporary name and renamed |
| Server prune | weekly | one `server.prune` job per connected server: dangling and unused images older than 7 days, unused networks, build cache. **Never volumes, never stopped containers** (a stopped application must start again); the handler adds every rollback image to the keep list. Visible in Jobs like any other job. |

`GET /system/maintenance` shows the effective settings, the backups and the last run; `POST /system/maintenance/run` runs everything now (a backup and a prune included). The settings come from configuration (`Aethera:Maintenance:*`, environment variables `Aethera__Maintenance__...`), not from the UI: they are operator decisions that belong with the deployment.

## API additions

| Endpoint | Role | Purpose |
|---|---|---|
| `GET /install-agent.sh` (root, not `/api`) | anonymous | the agent installer |
| `GET /system/version` | any member | running version, newest release, updater state |
| `POST /system/version/check` | admin | look for a release now |
| `POST /system/update` | admin | hand the update to the updater (202); 409 `system.updater_unavailable` / `system.update_in_progress`, 422 `system.no_update_available` |
| `GET /system/maintenance`, `POST /system/maintenance/run` | admin | retention, backups, last run; run now |
| `POST /servers/{id}/agent/update` | admin, `servers:write` | update the agent on a server (job) |

The UI: Settings > System (version, release notes, update button or the host command, maintenance and backups), an update alert on the dashboard for administrators, and "Update agent" in a server's settings.

## Verification

`bash deploy/install-e2e.sh` installs Aethera with `install.sh` on a throw-away Docker-in-Docker host and exercises, for real: secret generation and file modes, the owner and local agent, `/install-agent.sh`, a `pg_dump` backup that `pg_restore` can read, an update 0.1.0 -> 0.2.0 through the API (backup, new images, data intact, the agent and the updater replaced), an update whose images cannot be pulled (nothing changes) and an update to a release that does not start (rolled back, data restored from the pre-update backup).

## Known limits

- The release workflow has not run against GitHub yet (it is rehearsed with the manual trigger); the first tag is the first real test of the registry push and the release assets.
- No signed agent binaries; no signature on `install.sh` beyond HTTPS and the `SHA256SUMS` next to it.
- One API instance: the update, maintenance and updater protocol assume it (the maintenance lock already allows more).
- Backups stay on the same machine (`/data/backups`) until Phase 8 (remote destinations); copy them off the host.
- Downgrades are refused by design (see Migrations); the way back is restoring a pre-update backup with `aethera restore`.
- The compose file's label-based route for `AETHERA_DOMAIN` works once a server has the proxy and a certificate resolver (`--acme-email` / `Aethera:Proxy:AcmeEmail`); until then the UI is on `http://<host>:8080`.
