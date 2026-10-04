# Installing Aethera

Aethera is a control plane (API and web UI, PostgreSQL, Redis) plus one agent per server it manages. The installer sets up the control plane and, if you give it an owner, an agent for the same machine. More servers join afterwards with one command each.

## What you need

- A Linux server (Ubuntu, Debian, Fedora, RHEL-like or Alpine all work), amd64 or arm64, **1 vCPU and 2 GB RAM** at the very least (builds need more: 2 vCPU / 4 GB is comfortable).
- Root access and `curl`. The installer installs Docker (with the Compose plugin) when it is missing.
- Ports: **8080** for the UI and API (put a domain in front of it, below), **9443** for agents of *other* servers to reach the control plane (mutual TLS, no proxy in front of it), and **80 and 443** for the applications you deploy (Aethera runs its own Traefik on them).
- A DNS name pointing at the server if you want HTTPS for the UI or for your applications.

## Install

```bash
curl -fsSL https://github.com/MihaiM21/aethera-forge/releases/latest/download/install.sh | sudo bash -s -- \
  --admin-email you@example.com --host aethera.example.com
```

Without `--admin-email` nothing is created: open `http://<server>:8080` and register, the first account becomes the owner. With it, the installer creates the owner (the generated password is printed once, change it in Settings), creates a server called `local`, enrolls the agent for this machine and starts it.

What the installer does, in order:

1. checks for root, Linux and `curl`, installs Docker if needed;
2. creates `/opt/aethera` with `docker-compose.yml` and a `.env` (mode 0600) holding a generated **database password** and **master key**;
3. pulls the images, starts PostgreSQL, Redis and the control plane and waits until `/ready` answers;
4. creates the owner and enrolls the local agent (only with `--admin-email`);
5. starts the updater and installs the `aethera` command (`/usr/local/bin/aethera`).

> **Back up `/opt/aethera/.env`.** The master key encrypts every secret Aethera stores (registry passwords, SSH keys, environment secrets) and its internal certificate authority. Without it a restored database cannot be read. The installer never replaces an existing key or password, so running it again is safe.

### Options

| Option | Environment | Default | |
|---|---|---|---|
| `--dir <path>` | `AETHERA_DIR` | `/opt/aethera` | installation directory |
| `--version <v>` | `AETHERA_VERSION` | `latest` | release to install, e.g. `v0.1.0` |
| `--host <name-or-ip>` | `AETHERA_PUBLIC_HOST` | detected public IP | what **other servers** use to reach this machine's port 9443 (the agent gateway's certificate names it). Use a DNS name if the IP may change. |
| `--domain <name>` | `AETHERA_DOMAIN` | none | serve the UI on `https://<name>` through Aethera's proxy |
| `--acme-email <email>` | `AETHERA_ACME_EMAIL` | none | Let's Encrypt account for HTTPS certificates (also `AETHERA_ACME_STAGING=true` for the staging CA) |
| `--admin-email`, `--admin-password`, `--admin-name`, `--org-name` | `AETHERA_ADMIN_*`, `AETHERA_ORG_NAME` | | the first owner; the password is generated and printed when omitted |
| `--no-agent` | | | do not enroll an agent for this machine |
| `--http-port`, `--grpc-port` | `AETHERA_HTTP_PORT`, `AETHERA_GRPC_PORT` | 8080, 9443 | host ports |

Also read from the environment and written to the `.env`: `AETHERA_IMAGE`, `AETHERA_AGENT_IMAGE`, `AETHERA_UPDATER_IMAGE` (images from a private registry or mirror), `AETHERA_UPDATE_API_URL` (a GitHub mirror or Enterprise), `AETHERA_UPDATE_CHECK=false` (no release lookups, for air-gapped installs).

## First steps

1. Sign in at `http://<server>:8080`.
2. **HTTPS and a domain for the UI** (optional): install with `--acme-email you@example.com` (the Let's Encrypt account; without it the proxy serves HTTP only) and `--domain aethera.example.com` after pointing a DNS record at the server. The proxy appears on the `local` server with the first deployment that has a domain (or `POST /servers/{id}/proxy/ensure`); the UI is then served on `https://aethera.example.com` as well. To change these later edit `AETHERA_ACME_EMAIL` and `AETHERA_DOMAIN` in `/opt/aethera/.env` and run `docker compose -f /opt/aethera/docker-compose.yml --env-file /opt/aethera/.env up -d aethera`. Port 8080 keeps working throughout.
3. **Create a project and an application** (Applications > New): pick a repository or an image, the build method, environment variables, the server and a domain. See the [Coolify migration guide](migration-from-coolify.md) for a worked example.
4. **Add more servers**: Servers > Add server, then run the printed command on the new machine:

   ```bash
   curl -fsSL https://aethera.example.com/install-agent.sh | sudo bash -s -- --token <token> --endpoint aethera.example.com:9443 --ca-sha256 <fingerprint>
   ```

   Over plain HTTP that script is fetched without protection: once the UI has an HTTPS domain, set `AETHERA_INSTALL_AGENT_URL=https://aethera.example.com/install-agent.sh` in the `.env` (and restart the `aethera` service) so the printed command uses it. The binary the script downloads is always fetched over HTTPS and checked against the release's `SHA256SUMS`.

   The new server only needs **outbound** access to the control plane's port 9443. The token is single use and expires after an hour; the fingerprint pins the control plane's certificate authority, so the agent cannot be tricked into talking to someone else. The installer sets up Docker, a hardened systemd service and the agent's own policy file `/etc/aethera/agent.yaml`, which the control plane cannot change.

## Operating it

```bash
aethera status                # containers and version
aethera logs aethera          # follow a service's log (aethera, postgres, redis, agent, updater)
aethera backup                # pg_dump into /opt/aethera/backups
aethera restore <file>        # replace the database with a backup (stops the control plane meanwhile)
aethera update                # back up, pull the newest release, restart, roll back if it does not start
```

**Updates.** Settings > System shows the running version and the newest release (checked every six hours against the project's GitHub releases) with its notes. *Update to X* does what `aethera update` does: backs up the database, pulls the new images, restarts, and puts the old version and the backup back if the new one does not start. The UI is unavailable for about a minute. Pre-update backups are kept (the newest five). Agents on other servers update from their server's Settings tab ("Update agent"); the agent checks the download's checksum and rolls back by itself if the new version does not reconnect.

**Backups.** The control plane backs up its own database every day (the newest seven are kept) to the `aethera_data` volume, `/data/backups`, and before every migration. They stay on the machine: copy them off the host (`docker cp aethera-aethera-1:/data/backups .`, or mount the volume into your backup tool). What is *not* covered: the data of your own applications and services (their Docker volumes). Back those up on the server (there is no service backup in the UI yet).

**Housekeeping.** Aethera deletes build and deploy logs after 30 days, status events after 90, audit events after a year, and prunes unused images, networks and build cache on every connected server once a week (never volumes, never stopped containers, and always keeping the images your rollbacks need). Settings > System shows the effective values and the last run; they are configured with `Aethera__Maintenance__*` environment variables on the `aethera` service (for example `Aethera__Maintenance__AuditRetentionDays=0` keeps the audit log forever).

**Uninstall.**

```bash
cd /opt/aethera && docker compose --env-file .env down        # stops the control plane, keeps the data
docker compose --env-file .env down -v                        # also deletes the database and the backups volume
```

Applications deployed by Aethera are separate containers: remove them in the UI first (or stop them with `docker ps` / `docker rm` on each server), and remove the agent from other servers with `systemctl disable --now aethera-agent`.

## Troubleshooting

| Symptom | Check |
|---|---|
| The installer stops at "the control plane did not become ready" | `aethera logs aethera`; the usual causes are a taken port (use `--http-port`), too little memory, or a wrong `AETHERA_MASTER_KEY` copied into the `.env`. |
| A new server stays "pending" | The agent must reach `<host>:9443` (firewall, security group). `journalctl -u aethera-agent -n 50` on the server shows the reason; a certificate error means `--ca-sha256` does not match the control plane. |
| "database newer than this version" at start | The database was migrated by a newer release. Start that release, or restore a backup taken before the upgrade (`aethera restore`). Aethera refuses to run old code on a newer schema. |
| The update button says there is no updater | The installation was not made by `install.sh`, or the updater container is not running (`aethera status`). Use `sudo aethera update` on the server. |
| An update failed | Settings > System shows the reason (`could not be pulled`: no registry access and the images are not local; `did not start; rolled back`: the new version's own log is in `aethera logs aethera`). The database backup it took is `aethera-pre-update-*.dump`. |
| No release is found | The server cannot reach `api.github.com` (set `AETHERA_UPDATE_API_URL` to a mirror or `AETHERA_UPDATE_CHECK=false`), or no release has been published. |

## Building from source

`docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d --build` builds the images from a checkout (see the README); this is the development stack and has no updater. To use it as a real installation, use the release images and `install.sh`.
