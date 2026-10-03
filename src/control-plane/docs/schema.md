# Aethera database schema

PostgreSQL schema owned by `Aethera.Infrastructure` (EF Core 10 + Npgsql). The model lives in `Aethera.Domain`
(no dependencies), the mapping in `Aethera.Infrastructure/Persistence/Configurations/*`, and the single migration
`InitialSchema` (plus later additive migrations such as `AddJobOrganization`) in `Aethera.Infrastructure/Persistence/Migrations`. The schema follows the product spec
(`docs/idea/01_AppIdea.md`) and ADR 0002 (agent communication) and ADR 0004 (jobs and deployments); where the ADRs
named tables or columns, those names are used.

## Conventions

| Topic | Decision |
|---|---|
| Ids | `uuid`, client-generated UUIDv7 (`Guid.CreateVersion7()` in `Entity`), never database-generated. Exceptions: `metric_samples.id` (bigint identity, high volume) and natural/composite keys (`log_chunks`, `deployment_steps`, `secret_versions`, `workload_networks`, `idempotency_records`, `settings`, the 1:1 application configs). |
| Names | `snake_case` tables and columns (`EFCore.NamingConventions`), plural table names, explicit `ToTable`. Constraint/index names are generated (`pk_`, `fk_`, `ix_`) or explicit (`ck_`, named `ix_jobs_*`). |
| Time | `timestamptz` everywhere. A value converter normalises any `DateTimeOffset` to UTC on write (Npgsql refuses other offsets). |
| Enums | Stored as **camelCase strings** (`InProgress` -> `inProgress`, `varchar(32)`), see `CamelCaseEnumConverter`. This matches the API's JSON enum casing (ADR 0003) and the SQL literals in ADR 0004 (`status = 'queued'`). Enum values are therefore additive and reorder-safe. Exceptions: `log_chunks.source`/`stream` are `smallint` (ADR 0004) with numbers equal to the protocol enums. Enum arrays (`servers.roles`) are `text[]`. |
| Concurrency | Every `MutableEntity` has `RowVersion`, mapped to Postgres `xmin` (`xid`, concurrency token, no real column). The API derives ETags from it (ADR 0003). `UpdatedAt` is stamped by `AetheraDbContext.SaveChanges` on modification. Immutable rows (`audit_events`, `log_chunks`, `metric_samples`, `secret_versions`, `webhook_deliveries`, `resource_events`) have no row version. |
| Soft delete | `deleted_at` on user, project, environment, server, workload, domain, secret, registry, git credential. A global EF query filter hides deleted rows (`IgnoreQueryFilters()` to see them). Uniqueness that must be freed by a delete is a **partial unique index** `WHERE deleted_at IS NULL` (slugs, email, hostnames, secret names). Hard deletes (purge jobs) cascade down the tree; `servers` and `organizations` are `RESTRICT`. |
| JSON | `jsonb` only for payloads that are opaque to SQL: job payload/result/error, deployment config snapshot and last health result, step details, audit metadata, service template config, build args, idempotency response headers, settings. Anything queried, joined or constrained is relational. |
| Secrets | No plaintext column exists anywhere. Values live in `secret_versions` (AES-256-GCM envelope: `ciphertext` incl. tag, `nonce`, `wrapped_data_key`, `wrapped_data_key_nonce`, `master_key_version`). Other tables hold a `*_secret_id` reference (SSH credential, registry password, git credential, webhook secret, env var value). References use `NO ACTION` so a secret in use cannot be deleted. |
| Audit | `audit_events` is append-only: EF refuses to update/delete it and a database trigger rejects `UPDATE` (retention `DELETE`s stay possible). Actor ids are not foreign keys, a label snapshot is stored. |

## Entities

**Identity and access**: `organizations`, `users`, `organization_members` (role Owner/Admin/Developer/Viewer), `teams`,
`team_members`, `api_tokens` (unique lookup `prefix`, SHA-256 `secret_hash` with length check, `scopes text[]`, expiry,
last used + IP, `revoked_at`), `user_sessions` (hashed cookie secret, expiry, revocation, IP, user agent).

**Projects**: `projects` (org, slug, optional `template_key`), `environments` (project, slug, `is_production`).

**Servers and agent PKI** (ADR 0002)
- `servers`: `transport` (Agent/Ssh), `roles text[]` (Master/Build/Storage/Ci/Worker), `lifecycle` (Pending/Active/Maintenance/Disabled),
  SSH settings with a credential **secret reference** and pinned host key fingerprint, `max_concurrent_builds`, `public_ip`,
  agent version, current cert serial/fingerprint/expiry, discovered facts (os, kernel, architecture, cpu model/cores, memory,
  disk, docker version) as a *complex type* flattened into the row.
- **Status axes** (spec section 44, ADR 0002 "five independent signals"), each with its own `*_changed_at` ("stale since"):
  `reachability_status` (control-plane probe) + `reachability_checked_at` + `reachability_probe_port`; `agent_status`
  (Unknown/NotInstalled/Connected/Unavailable) + `last_heartbeat_at`; `docker_status` (mirrors the protocol enum). The
  application axis is `workloads.status` (+ `status_reason`, `status_observed_at`). "Control plane unavailable" is observable only
  client-side and has no column. Transitions are appended to `resource_events` by the caller (`Server.Set*` return whether the value changed).
- `join_tokens`: SHA-256 `token_hash` (unique, length-checked), `server_id`, `expires_at` (check: at most 24 h after creation; domain default 1 h),
  `used_at`, `revoked_at`, `created_by_user_id`. Consume with the single atomic `UPDATE ... WHERE used_at IS NULL AND expires_at > now()`.
- `certificate_authorities`: the internal CA - certificate PEM, SHA-256 fingerprint (the `--ca-sha256` pin), validity, and the **private key encrypted
  with the master key** (ciphertext/nonce/wrapped data key/master key version). A partial unique index allows at most one active CA.
- `agent_certificates`: every issued client cert (`serial` unique, fingerprint, SAN URI, validity, `revoked_at`, `revoked_reason`). Revocation is a
  lookup by serial (no CRL/OCSP); `servers.cert_serial` points at the current one.

**Workloads** - `workloads` is one table (see below) holding `Application` and `Service` rows:
- common columns: environment, server, name/slug, `desired_state`, observed `status`, `current_deployment_id`, `deployment_sequence`
  (per-workload counter backing deployment numbers), runtime config (restart policy, deployment strategy name, cpu/memory/pids limits and
  reservations, health check type/path/port/interval/timeout/retries/start period - complex types flattened into the row).
- application columns: `source_kind` (Git/DockerImage/Dockerfile/Compose/Static/Nixpacks) + optional 1:1 tables sharing the application id as
  primary key: `git_sources`, `build_configs` (`engine` is a free string for extensibility, `build_args jsonb`), `image_sources`, `compose_sources`.
- service columns: `template_key`, `template_version`, `image`, `config jsonb`.
- child tables: `workload_ports` (container port, protocol, published port, is_http; unique per protocol), `environment_variables` (unique
  `(workload_id, key)`, plain `value` XOR `secret_id`, `is_build_time`/`is_runtime`), `volumes`, `networks` (managed Docker network per server, project- or
  environment-scoped) + `workload_networks` (aliases), `domains` (hostname normalised to lower case; unique `(hostname, path_prefix)` among active rows;
  certificate status/expiry/error; DNS status, last check, resolved IPs; proxy route name), `images` (per server, for cleanup policies).
- Git integration (ADR 0003): `git_credentials` (token/deploy key; secret reference), `webhook_endpoints` (per application; the endpoint id is the URL id;
  secret reference), `webhook_deliveries` (unique `(endpoint_id, delivery_id)` = idempotency).
- `secrets` (scope = at most one of project/environment/workload, check constraint; unique name per scope among live rows, `NULLS NOT DISTINCT`),
  `secret_versions`, `registries`.

**Deployments** (ADR 0004)
- `deployments`: `number` (unique per workload), `trigger` (manual/webhook/redeploy/rollback/schedule/api), `status` (queued/inProgress/running/superseded/stopped/failed/cancelled),
  `strategy`, `current_step`/`failed_step` (the nine steps), `failure_code`/`failure_reason`, source revision (type, repo URL, ref, commit sha/message/author),
  `build_id`, `image_ref`/`image_digest`, `container_ids text[]`, `config_snapshot jsonb` (frozen at job start; secret id + version references, never values),
  `health_check jsonb`, `is_rollback_point`, `rollback_of_deployment_id`, trigger actor (user/token), `job_id`, timestamps.
- `deployment_steps`: one row per (deployment, step) with status (running/succeeded/failed/skipped/cancelled), timestamps, error code/message, `details jsonb`.
  This is the resume checkpoint and the UI stepper; `current_step`/`failed_step` on the deployment are kept for cheap listing.
- `builds`: separate from `deployments` because a deployment can have several attempts and rollbacks/redeploys have none
  (unique `(deployment_id, attempt)`; engine, platform, commit, duration, cache hit info, result image/digest/size, `job_id` for the build log).
- The state machine and its guards are methods on `Deployment` (`Start`, `BeginStep`, `CompleteStep`, `SkipStep`, `MarkRunning`, `MarkFailed(step, code, reason)`, `MarkCancelled`, `MarkStopped`, `MarkSuperseded`).

**Jobs and logs** (ADR 0004)
- `jobs` has the ADR columns (plus the EF bookkeeping `created_at`/`updated_at`/`xmin`) and, since `AddJobOrganization`, **`organization_id`** (`uuid NOT NULL`, FK to `organizations` with `RESTRICT`, index `ix_jobs_organization_created_at (organization_id, created_at)`). Tenancy is that column: every REST and hub visibility filter is `organization_id = <actor's organization>`, so a job without a creator (webhook, schedule) belongs to exactly one organization too. The queue takes it from `JobRequest.OrganizationId`, else from `ICurrentActor`, else refuses to enqueue. The migration backfills existing rows from the creator's oldest membership, else the oldest organization: `attempt`/`max_attempts`/`retry_no`, `locked_by`, `lease_expires_at`,
  `cancel_requested_at`, `parent_job_id`, unique `idempotency_key`, `payload`/`result`/`error` jsonb. Indexes: **`ix_jobs_claim`** `(priority DESC, run_after, id) WHERE status = 'queued'`
  (the claim query), `ix_jobs_running_lock_key` and `ix_jobs_running_lease` (partial, `status = 'running'`), `ix_jobs_resource`. The per-application advisory lock is
  `pg_try_advisory_lock(hashtextextended(lock_key, 0))` on a dedicated connection; no column is needed. Transitions are methods on `Job` (`Claim`, `ExtendLease`, `Fail`, `ExpireLease`, `RequestCancel`...).
- `log_chunks` `(stream_id, sequence)` primary key, `ts`, `source`/`stream` smallint, `data` text - chunk rows, not line rows. Insert with `ON CONFLICT DO NOTHING`.

**Cross-cutting**: `audit_events`, `resource_events` (status transitions and lifecycle events like `restarted`), `idempotency_records` (ADR 0003, composite key principal + key, 24 h),
`settings` (key -> jsonb), `metric_samples` (`(server_id, resolution, timestamp)` index plus a partial per-container index; no partitioning yet).

## Why a single `workloads` table (TPH) instead of TPT

Application and Service share almost everything the engine cares about: server, environment, runtime config, ports, env vars, volumes,
domains, networks, deployments and images all point at "a workload". Their *own* columns are tiny (4 for services, 1 for applications; the
application source detail lives in 1:1 tables). With **table-per-hierarchy**:
- every foreign key to a workload is a plain FK to one table (TPT also allows this, but needs a join to read any workload),
- listing "everything on server X" or "everything in environment Y" is one query without joins or unions,
- the nullable columns are guarded by check constraints (`ck_workloads_application_columns`, `ck_workloads_service_columns`) so a row cannot be half of both.

TPT would add a join to every read for no benefit at this size; TPC cannot be the target of the shared foreign keys. If a third workload kind
with many own columns appears, it should get a 1:1 side table (like the application source configs) rather than more nullable columns.

Runtime config and server facts are EF **complex types** flattened into their parent row: they have no identity or lifecycle of their own, are
always read together with the parent, and need no joins. Ports are a real table (not jsonb) because they are queried (conflict checks, routing).

## Working with the schema

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
cd src/control-plane
# AETHERA_DB defaults to Host=localhost;Database=aethera;Username=aethera;Password=aethera (deploy/docker-compose.dev.yml)
dotnet tool restore
dotnet ef migrations add <Name> --project src/Aethera.Infrastructure --startup-project src/Aethera.Infrastructure --output-dir Persistence/Migrations
dotnet ef database update --project src/Aethera.Infrastructure --startup-project src/Aethera.Infrastructure
```

- Migrations are owned by the orchestrator (CLAUDE.md). `ModelTests.ModelHasNoPendingChanges_SoMigrationsAreUpToDate` fails when the model and snapshot diverge.
- The app applies migrations on start only when `Aethera:Database:AutoMigrate=true`; connection string `ConnectionStrings:Aethera`.
- Hand-written SQL in `InitialSchema`: the `audit_events` update-rejecting trigger.
- Raw `FromSql` on `jobs`/mutable tables must select `xmin` (`SELECT j.*, j.xmin ...`) for EF to materialise entities.
- Database tests (`Aethera.Api.Tests`) run when `AETHERA_TEST_DB` is set to a connection string whose role may `CREATE DATABASE`
  (each test class migrates its own throw-away database and drops it); they are skipped with a message otherwise.
- The migrations are the contract: later migrations add to them, they do not edit earlier ones.
