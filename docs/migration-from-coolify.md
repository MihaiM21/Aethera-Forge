# Migrating from Coolify to Aethera (the Turn One stack)

This guide moves a Coolify installation to Aethera without downtime you did not choose. It is written for the Turn One setup (frontend, backend, T1API production and development, docs, PostgreSQL, Redis, InfluxDB, Grafana, Prometheus) but nothing in it is specific to it: replace the names with yours.

The approach is **side by side**: Aethera comes up on a *new* server (or the same one, see "One server" below), every piece is recreated and verified there under a temporary hostname, data is copied once at the end, and DNS is switched last. Coolify keeps running and serving until you decide, so the way back is changing DNS back.

## Concepts

| Coolify | Aethera | Differences that matter |
|---|---|---|
| Server | Server | Aethera uses an agent (outbound connection, mutual TLS) instead of SSH from the control plane; an SSH transport exists as a fallback for servers you cannot install the agent on. |
| Project / environment | Project / environment | Same idea. Aethera projects have templates (empty, web + database, ...). |
| Application (Nixpacks, Dockerfile, static, compose, image) | Application | Sources: Git (public, token, deploy key), image, compose with the file pasted in. Builds: Dockerfile, Nixpacks, static site, image. |
| Database / service (one-click) | Service from a template | PostgreSQL, MySQL, MariaDB, Redis, MongoDB, InfluxDB, Grafana, Prometheus, MinIO. Templates are YAML, editable after creation. Generated credentials are stored as secrets. |
| Environment variables, build-time vars, secrets | Environment variables (plain, secret, build-time) | `.env` files can be imported in the Environment tab. Secret values are encrypted, masked and revealable only by administrators (audited). |
| Domains and Let's Encrypt (Traefik/Caddy) | Domains with DNS check and Let's Encrypt (Traefik) | Aethera runs its own Traefik on each server; applications on the same server share ports 80 and 443 through it. |
| Webhooks (auto deploy) | Webhooks | GitHub (HMAC), GitLab (token) and generic hosts. Set up per application; the secret is shown once. |
| Rollback to a previous image | Rollback | Pick any earlier deployment whose image is still on the server; unused images are pruned weekly but rollback images are kept. |
| Persistent storage | Volumes | Named volumes or bind mounts (bind mounts of host paths need the administrator role). |
| Backups (S3) | Not yet | Aethera backs up its own database. Backups of your databases and volumes are a later phase: do them yourself (below) until then. |
| Notifications | Not yet | The dashboard shows alerts derived from status; Slack/email/Discord arrive in a later phase. |

## 0. Inventory what you have

In Coolify, write down for every resource: the repository and branch (or image and tag), build method and its commands, port, health check, **all environment variables** (including build-time ones), domains, volumes with their paths, and which database/service it talks to. For the databases: engine, version, database names, users and the size of the data. Coolify shows most of it on the resource page; for environment variables use "Developer view" and copy the `.env` text (it contains secrets: keep it out of chat and tickets).

Decide the cut-over order: databases last (they hold state), stateless applications first.

## 1. Install Aethera on the new server

```bash
curl -fsSL https://github.com/MihaiM21/aethera-forge/releases/latest/download/install.sh | sudo bash -s -- \
  --admin-email you@example.com --host aethera.example.com --acme-email you@example.com
```

Back up `/opt/aethera/.env`. See [install.md](install.md) for the details and for adding more servers (for example a separate build or database server, which is a good idea for production).

**One server.** If Aethera must run on the machine Coolify is on today, both want ports 80 and 443 (Coolify's proxy and Aethera's Traefik). Stop Coolify's proxy first (`docker stop coolify-proxy`), which takes Coolify's applications offline: only do this for a short, planned cut-over, never for a rehearsal. Prefer a second server.

## 2. Recreate the stack in Aethera

Use temporary hostnames under a domain you control (for example `*.new.example.com`) so nothing collides with production names.

1. **Project** per Coolify project (Projects > New, pick a template or start empty) with an environment `production` (and `development` for T1API dev).
2. **Services first**, so applications can reach them. Services > New from template: PostgreSQL (same major version as production), Redis, InfluxDB, Grafana, Prometheus. Applications in the same environment reach a service by its name on a shared network: `postgres:5432`, `redis:6379`. Nothing is published on the server's ports unless you add a published port to the service. Open each service to see the generated credentials (Environment tab, reveal needs an administrator) and its connection string.
3. **Applications** (Applications > New): repository or image, build method (detected for you; a Dockerfile in the repository wins), environment, server, domain, then deploy.
   - Paste the environment from your inventory with *Import .env* in the Environment tab. Mark secrets as secret. Replace the old database and Redis hosts with the service names above.
   - **Port**: set the container port the application listens on; Aethera routes the domain to it.
   - **Health check**: set the path the application answers on (or TCP); Aethera only switches traffic once it passes (low-downtime strategy keeps the old version serving until then).
   - **Auto deploy**: Build tab, Webhook section: copy the URL and secret into the repository's webhook settings (GitHub: content type `application/json`, push events).
4. **Verify under the temporary hostnames** before touching DNS: open each application, check the Deployments page for a green pipeline, and watch Logs for startup errors.

Things Coolify does that you may rely on and that need a decision:

- **Docker Compose applications**: paste the compose file's content into the application (it is not read from the repository), and note that services with `build:` are not supported: build the image elsewhere (an Aethera application with a Dockerfile) and use `image:`.
- **Private registries**: Registries > New, then pick the registry in the application.
- **Scheduled tasks / cron** and **backups to S3**: not available yet. Keep them where they are (a cron on the host, a separate backup job) until a later phase.
- **Prometheus configuration**: the template starts Prometheus with its default configuration. Mount your `prometheus.yml` (a bind mount of a file you place on the server, administrator role) or edit the service's command; Grafana's data sources and dashboards are in its volume, so copy that volume (below) rather than rebuilding them.

## 3. Copy the data (the cut-over window)

Do a **rehearsal** with a copy first, to know how long it takes, then repeat it for real. For each stateful service, stop writes (stop the applications that use it in Coolify, or put them in maintenance), copy, restore, start the Aethera applications.

### PostgreSQL

On the Coolify server (use the container name from `docker ps`):

```bash
docker exec -t <coolify-postgres> pg_dump -U <user> -d <database> --format=custom --no-owner > turnone.dump
scp turnone.dump root@aethera-server:/root/
```

On the Aethera server (the service's container is named after the service, `docker ps` shows it):

```bash
docker exec -i <aethera-postgres> pg_restore -U <user> -d <database> --no-owner --clean --if-exists < /root/turnone.dump
```

Create the role and database first if the dump needs them (the template starts PostgreSQL with the user `postgres`, the database `app` and a generated password (Environment tab); use those, or create more with `psql`). A dump is portable across versions upward (restore a 16 dump into 17); never downward.

### Redis

If it is only a cache, skip it and let it warm up. If it holds data: `docker exec <redis> redis-cli -a "$REDIS_PASSWORD" --rdb /data/dump.rdb`, copy `dump.rdb` into the Aethera service's data volume while the service is stopped (`docker run --rm -v <volume>:/data -v $PWD:/src alpine cp /src/dump.rdb /data/dump.rdb`), then start the service.

### InfluxDB, Grafana, other volumes

Stop the container on the old side, copy the volume, and mount it into the new service:

```bash
# old server: archive the volume
docker run --rm -v <old-volume>:/data -v $PWD:/out alpine tar czf /out/volume.tgz -C /data .
# new server: unpack into the volume of the Aethera service (stopped)
docker run --rm -v <aethera-volume>:/data -v $PWD:/in alpine sh -c 'cd /data && tar xzf /in/volume.tgz'
```

The service's Storage tab lists its volumes; `docker volume ls` on the server shows their Docker names. For InfluxDB 2 prefer `influx backup` / `influx restore` when the version differs, and keep the template's initial setup variables consistent with the data you restore (the init variables only apply to an empty data directory).

## 4. Switch DNS

1. Lower the TTL of the production records a day before.
2. Point each hostname at the Aethera server. Aethera issues the certificate with the first request (HTTP-01), so DNS must already point at it; if you must avoid a window without HTTPS, keep the Coolify certificate serving until Aethera's has been issued (the Domains page shows the DNS state per domain).
3. Watch Dashboard, Deployments and Logs. Check each application end to end (login, a write, a background job).
4. Keep Coolify and its data untouched for a week. It is the rollback: point DNS back and, if writes happened on Aethera in the meantime, dump them back the same way as above.

## 5. After the cut-over

- Stop and remove Coolify applications one by one, then Coolify itself, once you are sure.
- Turn on what Aethera does for you: automatic deploys from the repositories, the weekly prune, daily backups of Aethera's own database (copy `/data/backups` off the machine), and release notifications (Settings > System).
- Take your own backups of service volumes (a cron on each server with `pg_dump` and a volume archive, copied off the machine) until backups arrive in Aethera.
- Add an API token with the scopes your CI needs (Settings > API tokens), instead of sharing a login.

## Checklist

- [ ] Inventory complete, secrets stored safely
- [ ] Aethera installed on a new server, `.env` backed up
- [ ] Services created and reachable by name from the applications
- [ ] Applications deployed under temporary hostnames, pipelines green, logs clean
- [ ] Webhooks configured, a test push deployed
- [ ] Rehearsal of the data copy timed and verified
- [ ] TTLs lowered, cut-over window announced
- [ ] Data copied, applications switched, DNS changed
- [ ] Verified end to end; Coolify left untouched for a week as the rollback
