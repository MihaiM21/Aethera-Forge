# Migrating from Coolify to Aethera (the Turn One stack)

This guide moves a Coolify installation to Aethera without downtime you did not choose. It is written for the Turn One setup (frontend, backend, T1API production and development, docs, PostgreSQL, Redis, InfluxDB, Grafana, Prometheus) but nothing in it is specific to it: replace the names with yours.

The approach is **side by side**: Aethera comes up on a *new* server (or the same one, see "One server" below), every piece is recreated and verified there under a temporary hostname, data is copied once at the end, and DNS is switched last. Coolify keeps running and serving until you decide, so the way back is changing DNS back.

## Concepts

| Coolify | Aethera | Differences that matter |
|---|---|---|
| Server | Server | Aethera uses an agent (outbound connection, mutual TLS) instead of SSH from the control plane; an SSH transport exists as a fallback for servers you cannot install the agent on. |
| Project / environment | Project / environment | Same idea. Aethera projects have templates (empty, web + database, ...). |
| Application (Nixpacks, Dockerfile, static, compose, image) | Application | Sources: Git (public, token, deploy key), image, compose (the compose file is read from the repository, or pasted in). Builds: Dockerfile, Nixpacks, static site, image. |
| Database / service (one-click) | Service from a template | PostgreSQL, MySQL, MariaDB, Redis, MongoDB, InfluxDB, Grafana, Prometheus, MinIO. Templates are YAML, editable after creation. Generated credentials are stored as secrets. |
| Environment variables, build-time vars, secrets | Environment variables (plain, secret, build-time) | `.env` files can be imported in the Environment tab. Secret values are encrypted, masked and revealable only by administrators (audited). |
| Domains and Let's Encrypt (Traefik/Caddy) | Domains with DNS check and Let's Encrypt (Traefik) | Aethera runs its own Traefik on each server; applications on the same server share ports 80 and 443 through it. |
| Webhooks (auto deploy) | Webhooks | GitHub (HMAC), GitLab (token) and generic hosts. Set up per application; the secret is shown once. |
| Rollback to a previous image | Rollback | Pick any earlier deployment whose image is still on the server; unused images are pruned weekly but rollback images are kept. |
| Persistent storage | Volumes | Named volumes or bind mounts (bind mounts of host paths need the administrator role). |
| Backups (S3) | Backups | Scheduled and on-demand backups of PostgreSQL, MongoDB, Redis and volumes to any S3-compatible destination (Cloudflare R2 works), with retention and restore: see [backups.md](backups.md). The copy in section 3 below is the one-time migration, not your backup. |
| Notifications | Email and webhook | Email (SMTP or Resend) like Coolify, per-event settings, plus a signed webhook. Discord, Slack and Telegram arrive later. |

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

- **Docker Compose applications**: choose the compose source *Git repository* and give the path of the file in it (default `docker-compose.yml`); Aethera reads it from the repository on every deployment, so the file in Git is the source of truth, and `build:` services are built from the repository too. Pasting the file into the application is still possible for a file that is not in Git. Each compose service can have its own domains. The application's environment variables are available to the file's `${VAR}` interpolation; reference them with `environment:` or `env_file:` for the containers to see them.
- **Private registries**: Registries > New, then pick the registry in the application.
- **Scheduled tasks / cron**: a task runs a command inside the workload's container on a cron schedule (Scheduled tasks tab, history with logs, email on failure). A cron on the host that runs `docker exec` maps directly to it.
- **Backups to S3**: set up a destination and a schedule per service (see [backups.md](backups.md)) before the cut-over, so the first scheduled backup of the new services exists before you switch DNS.
- **Prometheus configuration**: the template starts Prometheus with its default configuration. Mount your `prometheus.yml` (a bind mount of a file you place on the server, administrator role) or edit the service's command. Bind mounts are only allowed **under `/var/lib/aethera`** on the server (for example `/var/lib/aethera/prometheus/prometheus.yml`); any other host path is refused when the deployment starts; Grafana's data sources and dashboards are in its volume, so copy that volume (below) rather than rebuilding them.

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

### MongoDB

Dump everything to one compressed archive on the Coolify server (use the container name from `docker ps`; add `--db <name>` for a single database):

```bash
docker exec <coolify-mongo> mongodump --username <user> --password '<password>' --authenticationDatabase admin --archive --gzip > turnone.archive.gz
scp turnone.archive.gz root@aethera-server:/root/
```

Restore it into the Aethera service (the template creates the user `admin` with a generated password: Environment tab, reveal needs an administrator). `--drop` replaces collections that already exist, so a rehearsal can be repeated:

```bash
docker exec -i <aethera-mongo> mongorestore --username admin --password '<password>' --authenticationDatabase admin --archive --gzip --drop < /root/turnone.archive.gz
```

Then point the applications at `mongodb://admin:<password>@mongodb:27017/<database>?authSource=admin` (or a dedicated user you create with `mongosh`). Restore into the same major version or a newer one, never an older one, and keep the old Mongo stopped for writes between the dump and the switch. For a database that must stay online during the copy use a replica set instead of dumps; that is outside this guide.

### InfluxDB, Grafana, other volumes

Stop the container on the old side, copy the volume, and mount it into the new service:

```bash
# old server: archive the volume
docker run --rm -v <old-volume>:/data -v $PWD:/out alpine tar czf /out/volume.tgz -C /data .
# new server: unpack into the volume of the Aethera service (stopped)
docker run --rm -v <aethera-volume>:/data -v $PWD:/in alpine sh -c 'cd /data && tar xzf /in/volume.tgz'
```

The service's Storage tab lists its volumes; `docker volume ls` on the server shows their Docker names. For InfluxDB 2 prefer `influx backup` / `influx restore` when the version differs, and keep the template's initial setup variables consistent with the data you restore (the init variables only apply to an empty data directory).

## Cloudflare

If the domains are on Cloudflare (as Turn One's are):

- **Certificates before the switch: DNS-01.** Create an API token with the permission *Zone > DNS > Edit* for the zones involved (nothing broader) and add it in Aethera as a Cloudflare DNS provider (Domains). Aethera then proves domain ownership with a TXT record instead of by answering on port 80, so it can issue the certificates (also wildcards such as `*.example.com`) **while DNS still points at Coolify**: HTTPS works on the first request after the switch. Without a provider Aethera uses HTTP-01, which needs DNS to point at the new server first.
- **The orange cloud (proxied) is fine.** Aethera's DNS check understands Cloudflare's proxy: a proxied record resolves to Cloudflare addresses, not to your server, and is accepted as such. Keep proxying on if you want it; keep it grey while you rehearse so the check sees the server's real address.
- **SSL/TLS mode must be *Full (strict)*.** *Flexible* talks plain HTTP to the origin, which Aethera redirects to HTTPS: an endless redirect loop. *Full* without strict would accept any certificate. With a certificate issued by Aethera (Let's Encrypt) Full (strict) just works.
- Aethera does not create or change DNS records itself in this release: add the records (or change the existing ones) in Cloudflare yourself, as in step 4.

## 4. Switch DNS

1. Lower the TTL of the production records a day before.
2. Point each hostname at the Aethera server. Aethera issues the certificate with the first request (HTTP-01), so DNS must already point at it; if you must avoid a window without HTTPS, keep the Coolify certificate serving until Aethera's has been issued (the Domains page shows the DNS state per domain).
3. Watch Dashboard, Deployments and Logs. Check each application end to end (login, a write, a background job).
4. Keep Coolify and its data untouched for a week. It is the rollback: point DNS back and, if writes happened on Aethera in the meantime, dump them back the same way as above.

## 5. After the cut-over

- Stop and remove Coolify applications one by one, then Coolify itself, once you are sure.
- Turn on what Aethera does for you: automatic deploys from the repositories, the weekly prune, daily backups of Aethera's own database (copy `/data/backups` off the machine), and release notifications (Settings > System).
- Check that the scheduled backups of every service ran and, once, **restore one into a new service** to prove the chain works ([backups.md](backups.md)). Keep the master key (`/opt/aethera/.env`) with them: backups are encrypted with a key derived from it.
- Add an API token with the scopes your CI needs (Settings > API tokens), instead of sharing a login.

## Checklist

- [ ] Inventory complete, secrets stored safely
- [ ] Aethera installed on a new server, `.env` backed up
- [ ] Services created and reachable by name from the applications
- [ ] Applications deployed under temporary hostnames, pipelines green, logs clean
- [ ] Webhooks configured, a test push deployed
- [ ] Cloudflare: DNS-01 token added, SSL mode Full (strict), certificates issued before the switch
- [ ] Backup destination and schedules set; one backup restored into a new service
- [ ] Rehearsal of the data copy timed and verified
- [ ] TTLs lowered, cut-over window announced
- [ ] Data copied, applications switched, DNS changed
- [ ] Verified end to end; Coolify left untouched for a week as the rollback
