# Backups

Aethera backs up the databases and volumes it runs, and its own database, to an S3-compatible bucket (Cloudflare R2 is the recommended one) or, for tests, to a directory on the server. Backups are **encrypted before they leave the server** and can be restored from the UI. The design is [ADR 0010](architecture/0010-backups-notifications-tasks.md).

> **Keep a copy of the master key.** Backups are encrypted with a key derived from the master key in `/opt/aethera/.env` (`AETHERA_MASTER_KEY`). Without it no backup can be restored, not by Aethera and not by anyone else. If the server and its master key are lost together, a backup in R2 is just noise. Copy the key into a password manager now:
>
> ```sh
> sudo grep '^AETHERA_MASTER_KEY=' /opt/aethera/.env
> ```
>
> The same warning and command are on Settings > Backups. Rotating the master key keeps the old versions in the keyring, so older backups stay readable as long as the old version is kept (the key id of every object, `mk<version>-<hash>`, says which one it needs).

## What is backed up

| Workload | How | Restore |
|---|---|---|
| PostgreSQL service | `pg_dump -Fc` of the database named by `POSTGRES_DB` (override: `{"backup": {"database": "name"}}` in the service's `config`) | `pg_restore --clean --if-exists --no-owner`, while the database runs |
| MySQL / MariaDB service | `mysqldump` / `mariadb-dump --single-transaction --all-databases` as root | `mysql` / `mariadb`, while it runs |
| MongoDB service | `mongodump --archive --gzip` | `mongorestore --archive --gzip --drop`, while it runs |
| Redis service | an RDB snapshot streamed from the running server (`redis-cli --rdb`), packed as a tar.gz | the service is stopped, the snapshot is unpacked into its `/data` volume, the service is started. Off by default: Redis is usually a cache |
| Any named volume (services and applications) | a gzipped tar made by a short-lived helper container that mounts the volume read-only | the workload is stopped, the volume is emptied and the archive unpacked, the workload is started |
| Aethera's own database | `pg_dump -Fc`, encrypted by the control plane and uploaded by it | on the host, see below |

Bind mounts (a host path) are not backed up; only named volumes are. One policy per database, one per volume, one for Aethera.

A PostgreSQL service is dumped one database at a time. If a service hosts several databases, back up the others through their own service or volume, or set `backup.database` to the one that matters most.

Passwords are given to the dump programs through the environment of the command (`PGPASSWORD`, `MYSQL_PWD`, `REDISCLI_AUTH`), never on a command line. `mongodump` can only read a password from its command line or a config file, so MongoDB goes through a short script that writes a `0600` config file inside the container and removes it when the dump ends.

## Set up Cloudflare R2

1. In the Cloudflare dashboard open **R2 Object Storage** and **Create bucket** (for example `turn-one-backups`). Note the **account id** shown on the R2 overview page.
2. **Manage R2 API tokens > Create API token**, permission **Object Read & Write**, limited to that bucket. Copy the **Access Key ID** and the **Secret Access Key** (shown once).
3. In Aethera: **Settings > Backups > Add destination**, provider **Cloudflare R2**:
   - Endpoint: `https://<account id>.r2.cloudflarestorage.com` (replace the account id)
   - Region: `auto`
   - Bucket, the optional folder (default `aethera`), the access key id and the secret.
4. Press **Test connection**: Aethera writes, reads back and deletes a small object. A failure says why (wrong key, missing bucket, no permission to delete).

Backblaze B2, AWS S3 and MinIO work the same way; the presets in the dialog fill their endpoints. Plain `http` is only accepted for local addresses (a MinIO on the same network).

## Policies and "back up now"

Open the **Backups** tab of a service or application:

- **Set up backups** for the database (supported templates) or a volume: destination, schedule (daily 03:00, every 6 hours, weekly, or a custom five-field cron expression in a time zone; the next three run times are shown for the saved schedule), and retention. Backups can run at most every 15 minutes.
- **Back up now** queues the same job as the schedule. A backup takes the workload's lock, so it never runs during a deployment of the same workload.
- The table lists every run: time, trigger, status, size, duration, key id and error. A failed backup raises an alert event (email once notifications are configured) and a later success resolves it.

**Retention** (default 7 daily, 4 weekly, 6 monthly) keeps the newest successful backup of each of the last N days, ISO weeks and months *that have a backup*, in the policy's time zone, and always the newest successful backup. The rest is deleted from the destination after each run; the row stays in the history marked "removed by retention".

## Restore

Administrators restore from the table (**Restore...**), typing the exact name of the workload.

- **Into the same service** overwrites its data. First a *safety backup* of the current state is taken (trigger "before restore"); if it fails, nothing is restored. Databases are restored while they run; Redis and volumes are stopped for the restore and started again, even when the restore fails. The agent checks the SHA-256 of the stored object before restoring and the AEB1 authentication of every chunk while it reads.
- **Into a new service** creates a service from the same template and version with its own generated credentials, deploys it and restores the backup into it. Production is not touched: this is how to check that a backup works. Delete the service afterwards.

A backup removed by retention, or one that did not succeed, cannot be restored. The restore is a job: its log (Jobs) narrates each step, and the audit log records who asked and the outcome.

## Restoring Aethera's own database

The backup of Aethera's own database (Settings > Backups > *Aethera's own database*) is made by the control plane, encrypted with the same key scheme and uploaded to an S3 destination. To restore it, for example on a new machine installed with the **same master key** (`AETHERA_MASTER_KEY` in `/opt/aethera/.env`):

1. Download the object from the bucket (for example with the Cloudflare dashboard or `aws s3 cp --endpoint-url https://<account id>.r2.cloudflarestorage.com`), e.g. `backup.pgdump.aeb`. Its path is `<folder>/<org>/aethera/controlplane/<UTC time>-<id>.pgdump.aeb`.
2. Decrypt it with the key the object's header names:

   ```sh
   aethera backup-decrypt backup.pgdump.aeb backup.dump
   ```

   (This runs `dotnet Aethera.Api.dll backup-decrypt` inside the control plane container, which holds the master key. It fails with a clear message if the key is wrong or the file is damaged or incomplete.)
3. Restore the database dump (the control plane is stopped meanwhile):

   ```sh
   sudo aethera restore backup.dump
   ```

Secrets stored in the database are encrypted with the master key as well, so a restore on a machine with another master key would leave them unreadable: keep the key with the backup.

## Object layout and format

Objects are `<folder>/<org>/<workload slug>-<workload id>/<database|volume>/<UTC yyyyMMddTHHmmssZ>-<run id>.<ext>.aeb` with `ext` = `pgdump`, `mongo.gz`, `rdb.tar.gz`, `sql`, `tar.gz`. The `.aeb` format (AEB1) is documented in ADR 0010: a header naming the key, then 64 KiB chunks of AES-256-GCM that bind the header and mark the last chunk, so a truncated object fails to decrypt instead of restoring half a database.
