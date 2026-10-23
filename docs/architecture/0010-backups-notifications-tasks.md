# ADR 0010: Backups, notifications, scheduled tasks and compose from Git

- Status: accepted
- Date: 2026-10-23
- Scope: release 0.1 (R1, [`docs/release-0.1.md`](../release-0.1.md)): backups (Phase 8), email notifications and alerts (Phase 9), scheduled tasks, compose read from the repository, Cloudflare DNS-01 and the domain options
- Related: [ADR 0002](./0002-agent-communication.md) (command allowlist), [ADR 0004](./0004-jobs-and-deployments.md) (jobs), [ADR 0006](./0006-trust-model.md) (who may do what), [ADR 0007](./0007-build-and-deploy.md) (deploy engine, proxy)

## Contracts added

| Contract | What |
|---|---|
| `proto/.../docker.proto` | `ComposeProject.git`, `compose_path`, `build_args`; `ComposeResult.commit_sha`; `ComposeInspect` / `ComposeInspectResult`; `ContainerExec` / `ContainerExecResult` |
| `proto/.../backup.proto` | `BackupRun`, `BackupRestore`, `BackupDelete` with sources (`ExecDump`, `VolumeArchive`), sinks (`ExecRestore`, `VolumeRestore`), locations (`S3Location`, `LocalLocation`) and `BackupEncryption` |
| Capabilities | `container.exec`, `backup.v1`, `compose.git` (an agent without them gets no such command; the UI says the agent needs an update) |
| Migration `AddR1BackupsNotificationsTasks` | `backup_destinations`, `backup_policies`, `backup_runs`, `email_settings`, `notification_channels`, `alerts`, `notification_deliveries`, `scheduled_tasks`, `scheduled_task_runs`, `dns_providers`; `workloads.custom_labels`; `domains.compose_service`, `redirect_to`, `strip_path_prefix`, `basic_auth_secret_id`, `dns_provider_id`; secret purposes `backupCredential`, `notificationCredential`, `dnsCredential`, `basicAuth` |
| Seams | `INotifier` + `NotificationEvent` + `NotificationEvents` (Domain), `CronSchedule` (Infrastructure/Scheduling), API modules `Backups`, `Notifications`, `ScheduledTasks` |

## Exec inside a container (amends ADR 0002)

ADR 0002 forbids a generic exec/shell command. `container.exec` is not one: it runs an argument vector **inside an existing, running workload container** through the Docker exec API, in that container's namespaces, image and user. It cannot reach the host, cannot name a container the control plane did not resolve from a workload of the caller's organization, and the people who may create a scheduled task (developers and up, scope `write`; "run now" needs `deploy`) can already change that container's image and command. The control plane sends `["sh", "-c", "<command>"]` for a user's command line (as Coolify does); the agent never interprets a string. Output streams as `DEPLOY` log chunks of the command and the last 16 KiB come back in the result. The command succeeds when the program ran to completion; the exit code decides the task's outcome.

The agent refuses `container.exec` on containers it does not manage (no `aethera.workload` label), on the proxy and on itself.

## Backups

**Who does what.** The control plane decides what, when and where (policies, schedule, retention) and holds the destination credentials. The agent produces the stream on the server where the data lives and talks to the store directly, so a large dump never crosses the control plane. Aethera's own database is the exception: the control plane dumps it (`pg_dump -Fc`, as today) and uploads it itself.

| Engine | Dump (inside the database container, `ExecDump`) | Restore (`ExecRestore`) |
|---|---|---|
| PostgreSQL | `pg_dump -Fc -U $POSTGRES_USER -d <db>` | `pg_restore --clean --if-exists --no-owner -U ... -d <db>` |
| MongoDB | `mongodump --archive --gzip` (+ auth from the service's variables) | `mongorestore --archive --gzip --drop` |
| Redis | `redis-cli --rdb -` | stop, `VolumeRestore` of `dump.rdb`, start (off by default: usually a cache) |
| MySQL/MariaDB | `mysqldump --single-transaction --all-databases` | `mysql` |
| Volume | `VolumeArchive`: helper container (busybox) with the volume read-only at `/data`, `tar czf -` | `VolumeRestore`: workload stopped, `tar xzf -` |

Credentials reach the dump program through `env` (secret variants), never the argument vector. The template key of a service selects the engine; applications get volume backups only.

**Format AEB1.** The stream is encrypted by the agent before it leaves the server:

```
header  = "AEB1" | key_id length (1 byte) | key_id (ASCII) | salt (32 random bytes)
key     = HKDF-SHA256(ikm = stream key, salt = salt, info = "aethera/backup/aeb1", 32 bytes)
chunks  = AES-256-GCM(key, nonce = 11-byte big-endian counter | final flag (0x00 / 0x01), plaintext chunk of 65536 bytes, aad = header)
```

The last chunk carries the final flag and may be shorter (or empty), so a truncated object fails to decrypt instead of restoring half a database. The stored object's SHA-256 is recorded on the run and checked before a restore. Go (agent) and .NET (control plane, for its own database and `aethera backup decrypt`) implement the same format against a shared test vector (`src/agent/internal/backup/testdata/aeb1-vector.json`).

**Keys.** The stream key is derived from the master key: `HKDF-SHA256(ikm = master key v, info = "aethera/backup/v1")`, id `mk<v>-<first 8 hex of SHA-256(stream key)>`. Rotating the master key keeps old versions in the keyring, so old backups stay readable. **Losing the master key loses the backups**: the UI and the install guide say so, and Settings > Backups shows how to copy it.

**Objects.** `<prefix>/<org8>/<workload slug>-<workload8>/<kind>/<UTC yyyyMMdd'T'HHmmss'Z'>-<run8>.<ext>.aeb` (`ext` = `pgdump`, `mongo.gz`, `rdb`, `tar.gz`, `sql`). The S3 upload is a streaming multipart upload (unknown length); an aborted run aborts the multipart upload.

**Permissions.** Destinations: administrators, scopes `write` + `secrets:write` (the access key is a managed secret). Policies and "back up now": developers and up, `write` / `deploy`. Downloads of stored objects are not offered in R1.

**Schedule and retention.** A scheduler wakes every minute, takes due policies with `FOR UPDATE SKIP LOCKED`, enqueues one `backup.run` job each (per-workload lock, so a backup never overlaps a deployment of the same workload) and computes `next_run_at` with `CronSchedule`. Retention keeps the newest successful run of each of the last `keep_daily` days, `keep_weekly` ISO weeks and `keep_monthly` months, never the newest successful run, and deletes the rest from the store (`backup_runs.pruned_at` keeps the history).

**Restore.** Administrators only (scope `deploy`), a destructive action with a typed confirmation (the service name). "Into this service": a `PreRestore` backup is taken first, the workload is stopped where the engine needs it, the stream is restored, the workload is started. "Into a new service": a service from the same template and version is created and the backup restored into it, which is how a backup is tested without touching production.

## Notifications and alerts

Email settings and channels are administrator settings (scope `write`, plus `secrets:write` for the SMTP password, Resend key and webhook secret, which are managed secrets).


`INotifier.PublishAsync(NotificationEvent)` is the only entry point; it never throws for a delivery problem. Events with an `AlertKey` open or refresh an alert (one open alert per key); a resolving event closes it and sends "recovered". While an alert stays open a reminder goes out every 6 hours. Deliveries are queued (`notification.send` jobs) so a slow SMTP server never blocks a deployment, and recorded in `notification_deliveries`.

Detectors: deployment, backup and task results (from their job handlers); agent down for 2 minutes / back (server status); disk over 85 % and memory over 90 % for 5 minutes (metrics, per-server thresholds later); a managed container that died, was OOM-killed or restarts in a loop (agent events); certificates expiring within 14 days or failing (a TLS handshake from the control plane to each HTTPS domain, daily); an Aethera update (release checker).

Email: SMTP (MailKit; STARTTLS, implicit TLS or none) or Resend's HTTP API. Recipients: the organization's administrators and owners and/or a list. Webhook: `POST` JSON, `X-Aethera-Signature: sha256=<hex HMAC of the body>`, `X-Aethera-Event`, 10 s timeout, the same SSRF rules as Git webhooks' outbound calls. Mail and webhook bodies never contain secret values.

## Compose from the repository

With `ComposeSource.FilePath` set (and the application's Git source), the agent checks the repository out into the project directory, validates the resolved file with the same policy as inline files (`docker compose config` output: no privileged, no host network/PID, binds only under the allowed prefixes, no Docker socket), builds `build:` services with `--build-arg` values from the environment and runs `compose up --build --wait` with Aethera's override. `compose.inspect` lists services and ports so each domain can name its `compose_service`; the override file puts the proxy labels and network on that service. A domain without a service on a compose application is refused.

## Domains and the proxy

- **DNS-01.** A `dns_providers` row (Cloudflare API token with `Zone:DNS:Edit`) adds a `cloudflare` certificate resolver to the server's Traefik static configuration (`CF_DNS_API_TOKEN` through `ProxyEnsure.env`). A domain with `dns_provider_id` uses it; wildcard hosts (`*.example.com`) require it.
- **DNS check.** With a provider for the zone, the check reads the record through the Cloudflare API: a proxied (orange) record whose origin is the server is `Ok`, not `Mismatch`. Without one, an answer inside Cloudflare's published ranges is reported as "proxied by Cloudflare, origin not verifiable" instead of a mismatch.
- **Redirects** (`redirect_to`): a router for the host with a `redirectregex` middleware to the target host, permanent, path and query kept. **Strip prefix** adds `stripprefix`. **Basic auth** adds `basicauth` with the bcrypt htpasswd lines from the managed secret. **Custom labels** (`workloads.custom_labels`, administrators only) are merged last; `traefik.docker.network` and the `aethera.*` labels cannot be overridden.

## Consequences

- An agent older than this release cannot back up, run tasks or deploy compose from Git; the control plane checks the capability and asks for an agent update instead of failing late.
- Backups depend on the master key. That is deliberate (nothing readable leaves the server) and documented loudly.
- Scheduled tasks need a running container; a stopped workload skips the run (`skipped`) instead of starting one.
