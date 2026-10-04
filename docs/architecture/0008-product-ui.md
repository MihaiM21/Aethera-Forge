# ADR 0008: Product UI and services

- Status: accepted
- Date: 2026-10-04
- Scope: application flows, deployments UI, service templates, dashboard and ops pages (Phase 4), and the API this needed
- Related: [ADR 0003](./0003-api-conventions.md) (API conventions), [ADR 0005](./0005-web-routing.md) (static export), [ADR 0007](./0007-build-and-deploy.md) (deploy engine)

## API additions

The UI has no logic the API lacks, so Phase 4 started with the endpoints it was missing (all under `/api/v1`):

| Endpoint | Purpose |
|---|---|
| `GET /deployments` | Deployments of every application and service, newest first; filters `status`, `applicationId`, `serverId`, `trigger`; each item names its workload |
| `GET /deployments/{id}/logs` | The build (`build:<buildId>`) or pipeline (`job:<jobId>`) log of a deployment, paged by sequence or as a text download; `sources` says which streams exist, `streamId` is what `/hubs/logs` subscribes to |
| `GET /applications/{id}/logs`, `GET /services/{id}/logs` | A bounded tail of the running containers (`tail`, `since`), read live through the agent (SSH fallback) |
| `GET /audit-log` | The organization's audit trail for administrators; filters `action` (exact or prefix `x.*`), `resourceType`, `resourceId`, `actorType`, `from`, `to` |
| `/services/{id}/deployments`, `redeploy`, `start`, `stop`, `restart` | Services deploy through the same engine as applications |

`BuildLogStreamAuthorizer` lets `/hubs/logs` subscribe to `build:<id>` streams (the build must belong to a deployment of the caller's organization).

## Services run through the deploy engine

A service is an image, so its snapshot is the template image plus the common parts (environment, ports, volumes, domains, networks). `DeploymentSnapshotFactory.Create(Workload)` dispatches on the kind; `DeployJobHandler`, `LifecycleJobHandler` and `DeploymentService` work on `Workload`, and the routes under `/applications` and `/services` share one implementation (the route decides which kind the id must be, the other kind answers 404). The deployment's `applicationId` field carries the workload id for both.

**Environment network.** A workload without explicit networks joins `aethera-env-<environmentId[..8]>` (not internal) with its slug as DNS alias. Applications therefore reach a service of the same environment by its slug (`postgres:5432`), and nothing is published on the server's own ports unless a port has a `publishedPort`.

**Start command.** An image without a default command (MinIO) needs one: a template may carry `command`, the service stores it in its `config`, and the snapshot hands it to the container (`ImageSnapshot.Command`).

## Service template format (YAML)

Each template is a file in `src/control-plane/src/Aethera.Api/Features/Resources/Services/Templates/`, embedded in the assembly; the numeric prefix of the file name is the display order. Adding a database is adding a file.

```yaml
key: postgres                 # required, lowercase letters, digits, hyphens; unique
name: PostgreSQL              # required
description: Relational database.
category: database            # database | cache | monitoring | storage (a new value creates a new group in the UI)
image: postgres               # required; the tag comes from versions
versions: ["17", "16"]        # required, the first is the default
command: [server, /data]      # optional: start command for images that have none
ports:
  - port: 5432                # container port; internal only
    http: false               # true = routable through a domain
volumes:
  - name: data                # becomes <slug>-<id8>-data
    path: /var/lib/postgresql/data
healthCheck:                  # none | http | tcp | container
  type: tcp
  port: 5432                  # default: the first port
  # path: /health             # required for http
  # intervalSeconds, timeoutSeconds, retries, startPeriodSeconds: defaults 10/5/5/20 (http: 15/5/5/30)
env:
  - key: POSTGRES_USER
    value: postgres           # a plain variable
  - key: POSTGRES_PASSWORD
    generate: password        # a random secret, stored encrypted, linked as a secret-backed variable
    length: 48                # optional, default 32
```

`ServiceTemplates.Parse` validates every field and names the file and the field in its error; a broken embedded file stops the API at start-up instead of showing a half template. After creation everything above is editable on the service (environment, domains, storage, version, name); the template only provides the defaults.

## Web

- `src/lib/resources/` is the typed layer over the endpoints (`api.ts`), their types from the generated schema (`types.ts`), pure helpers (`status.ts`, `wizard.ts`) and the log hooks (`use-logs.ts`). Two response types are hand-written (`LogLine`, `DeploymentLogPage`): the schema generator drops a JSON response that shares its 200 with a `text/plain` one, as the job log endpoint already does.
- **Live logs are polled, not pushed.** `useDeploymentLog` asks for `fromSequence = next` every 1.5 s until the page says the stream ended; the SignalR hub carries the same streams and stays available for a CLI. A poll survives reconnects, proxies and a restarted API without extra client code. Runtime logs poll a bounded tail every 3 s.
- **Detail routes** (`/applications/{id}`, `/deployments/{id}`, `/services/{id}`, `/projects/{id}`) are single exported shells (ADR 0005); tabs live in the URL hash.
- **The wizard is not atomic.** It creates the application, then variables (secret values become secrets of the application and are linked, never sent as plain values), then the domain, then the deployment. If a later call fails the application stays, the page says which step failed, links to the application and retries only what is left.
- **Secrets are never shown.** Lists show masked values; revealing needs the administrator role, is recorded in the audit log and is a separate, explicit action.
- **Alerts on the dashboard are derived** from what the API already reports (agent down, failing applications and services, usage over 90%). There is no alert store yet; the Monitoring section keeps its placeholder.

## Known limits

- Compose applications still need the file pasted in (ADR 0007); the wizard says so.
- `POST /registries/{id}/test` answers 501, so the registries page has no "test connection".
- Services have no backup or restore, and changing a database version is a plain image change: read the upgrade notes of the database first.
- Runtime logs are a snapshot per poll, not a stream, and read at most eight containers of a compose project.
- A service is deployable only to a server the agent can reach; there is no scheduling across servers.
