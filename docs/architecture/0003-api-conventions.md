# ADR 0003: API conventions

- Status: accepted
- Date: 2026-10-03
- Scope: the public REST API (`/api/v1`), live updates (SignalR), authentication, OpenAPI and the generated TypeScript client
- Spec: sections 26, 27, 28, 30, 59 of `docs/idea/01_AppIdea.md`
- Related: [ADR 0004](./0004-jobs-and-deployments.md) (jobs, deployments, logs), [ADR 0002](./0002-agent-communication.md) (agent)

## Context

The API is the product's only surface: the web UI, the future CLI, CI systems and webhooks all use it (spec section 59, "API-first"). The OpenAPI document and the generated TypeScript client are contracts frozen at the end of Phase 0 (plan). Several workers will add endpoints in parallel, so the conventions must be precise enough that independently written endpoints look identical.

## Decision summary

- Base path `/api/v1`, JSON only, `camelCase`, UUIDv7 string IDs, RFC 3339 UTC timestamps.
- Cursor pagination (`?limit&cursor` -> `{items, nextCursor}`), filtering and sorting by query parameters.
- Errors are RFC 9457 `application/problem+json` with a stable `code` extension.
- Actions that take time return `202 Accepted` + a `Job` resource; progress and logs come over SignalR hubs.
- Auth: cookie session for the browser, `Authorization: Bearer aeth_...` scoped tokens for everything else.
- OpenAPI from `Microsoft.AspNetCore.OpenApi`, UI by Scalar at `/api/docs`, TS client generated with `openapi-typescript`.

## 1. Basics

| Topic | Convention |
|---|---|
| Base URL | `/api/v1/...`. The major version is in the path; additive changes stay in v1 (new fields, endpoints, optional params, enum values clients must tolerate). Breaking changes need `/api/v2`. |
| Media type | Requests and responses are `application/json; charset=utf-8` (`application/problem+json` for errors, `application/merge-patch+json` for PATCH, `text/plain` for log downloads, `text/event-stream` is not used: live data uses SignalR). |
| Property names | `camelCase` (`System.Text.Json` with `JsonNamingPolicy.CamelCase`). Query parameters are also `camelCase`. |
| Enums | JSON strings in `camelCase` (`"inProgress"`, `"agentUnavailable"`), via `JsonStringEnumConverter`. Clients must treat unknown values as "unknown" (new values are additive). |
| Absent values | Declared properties are always present; "no value" is `null`, never omitted. Collections are `[]`, never `null`. |
| IDs | **UUIDv7** (`Guid.CreateVersion7()`), serialized lowercase hyphenated (`"0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55"`). Time-ordered, so they index well and sort roughly by creation. Opaque to clients. Property is named `id`; references are `<thing>Id` (`projectId`). Human slugs (`slug`) exist for display and in-product URLs but the API addresses resources by ID only. |
| Timestamps | RFC 3339 in **UTC with `Z`**, millisecond precision: `"2026-10-03T14:07:31.482Z"`. Property names end in `At` (`createdAt`, `finishedAt`). Never local time or offsets in output; offsets are accepted in input and normalized. |
| Durations & sizes | Integers with the unit in the name: `durationMs`, `timeoutSeconds`, `sizeBytes`, `memoryLimitBytes`. CPU as decimal cores (`cpuLimitCores: 1.5`). |
| Money/percent | Percentages are numbers 0-100 (`cpuPercent`). |
| Naming of resources | Plural, lower-case, kebab-case nouns: `/applications`, `/env-vars`, `/api-tokens`, `/audit-events`. No verbs in resource paths; verbs only for actions (below). |
| Nesting | At most one level, and only for ownership/containment: `/projects/{projectId}/environments`, `/applications/{applicationId}/deployments`. Every resource that has an ID is also reachable at its top-level path (`/deployments/{id}`), so clients holding an ID never need the parent. |
| Idempotent verbs | `GET`, `PUT`, `DELETE` are idempotent; `POST` is not unless an `Idempotency-Key` is supplied. |
| Compression/limits | gzip/br response compression; JSON request body limit 1 MiB (10 MiB for env/secret import and compose file endpoints). |
| CORS | Off by default (the UI is served by the API on the same origin). Explicit allowlist via `AETHERA_CORS_ORIGINS` for custom frontends. |
| Correlation | Every response carries `X-Request-Id` and W3C `traceparent`; the same value appears as `traceId` in error bodies and in logs/audit entries. |

### Status codes

| Code | Use |
|---|---|
| `200 OK` | successful read/update with a body |
| `201 Created` | resource created; `Location` header with its URL; body is the resource |
| `202 Accepted` | long-running action started; body is a `Job` |
| `204 No Content` | successful delete or action with nothing to return |
| `304 Not Modified` | conditional GET matched `If-None-Match` |
| `400 Bad Request` | malformed JSON, unparsable query parameter, unknown sort/filter field |
| `401 Unauthorized` | missing/invalid credentials (`WWW-Authenticate: Bearer`) |
| `403 Forbidden` | authenticated, but role or token scope insufficient |
| `404 Not Found` | no such resource **or** the caller may not know it exists |
| `409 Conflict` | state conflict (deployment already running, name taken, job already finished) |
| `412 Precondition Failed` | `If-Match` ETag mismatch |
| `415` / `406` | unsupported content type / unacceptable `Accept` |
| `422 Unprocessable Content` | well-formed request that fails validation |
| `428 Precondition Required` | `If-Match` or destructive-action confirmation required but missing |
| `429 Too Many Requests` | rate limited; `Retry-After` in seconds |
| `500` / `503` | unexpected error / dependency unavailable (`Retry-After` when known) |

## 2. Collections: pagination, filtering, sorting

### Cursor pagination

```
GET /api/v1/deployments?limit=50&cursor=eyJ0IjoiMjAyNi0xMC0wM1QxNDowNzozMS40ODJaIiwiaSI6IjAxOTBmM2My...
```

```json
{
  "items": [ { "id": "0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55", "...": "..." } ],
  "nextCursor": "eyJ0IjoiMjAyNi0xMC0wMyIsImkiOiIwMTkw..."
}
```

- `limit`: 1-200, default **50**. Out of range -> `400 validation.invalid_parameter`.
- `cursor`: opaque, URL-safe base64 of the keyset `(sort values, id)`; clients must not parse or build it. `nextCursor` is `null` on the last page. A cursor is only valid with the same filters/sort that produced it (a mismatch is `400 pagination.invalid_cursor`).
- **Keyset, not offset**, so pages stay stable while rows are inserted (jobs, logs, deployments change constantly) and deep pages stay cheap. Sort order always ends with `id` as a tiebreaker.
- No total count by default (expensive). `?includeTotal=true` adds `"totalCount"` for UIs that need it; it is best-effort and may be an estimate above 10,000.
- Streams that are naturally append-only (logs) use a `fromSequence` / `afterSequence` parameter instead, see ADR 0004.

### Filtering

- Equality filters are top-level query parameters named after the property: `?status=running`, `?serverId=<uuid>`, `?projectId=<uuid>`.
- Multiple values are comma-separated (OR): `?status=queued,running`. Different parameters combine with AND.
- Time ranges: `?createdAfter=2026-10-01T00:00:00Z&createdBefore=...` (inclusive start, exclusive end); equivalents for `startedAt`, `finishedAt` where relevant.
- Text search: `?q=<text>` (case-insensitive substring over the documented searchable fields of that resource).
- `?include=lastDeployment,server` expands documented relations inline (default: IDs only) to avoid N+1 calls from the UI. Unknown names are `400`.
- Unknown filter parameters are rejected with `400`, not silently ignored, so typos are caught.

### Sorting

`?sort=-createdAt,name`: comma-separated property names, `-` prefix means descending, applied left to right. Each resource documents its sortable fields (always indexed) and its default (usually `-createdAt`).

### Partial responses

Not supported (`fields=`); responses are small and OpenAPI stays honest. List endpoints return a lighter "summary" shape than the detail endpoint where size matters (e.g. no `config` blob).

## 3. Errors: RFC 9457 ProblemDetails

All non-2xx responses are `application/problem+json`:

```json
{
  "type": "urn:aethera:problem:application.not_found",
  "title": "Application not found",
  "status": 404,
  "detail": "No application with id 0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55 exists.",
  "instance": "/api/v1/applications/0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55",
  "code": "application.not_found",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

- `code` is the **stable, machine-readable** identifier clients branch on. Format `area.reason` in lower snake case. `type` is always `urn:aethera:problem:<code>` (RFC 9457 allows non-dereferenceable URIs). `title` is a short, fixed English phrase per code; `detail` is occurrence-specific and for humans. **Never** parse `title`/`detail`.
- The catalogue of codes is part of the contract and lives next to the endpoints (`ProblemCodes` constants, rendered into the OpenAPI document via a shared `ProblemDetails` schema with the `code` enum). Codes are never repurposed; they may be deprecated.
- Mapping happens in one place (`IExceptionHandler` + `AddProblemDetails`), from domain exceptions to problems. Stack traces, SQL, file paths and secret values never appear in responses. A `500` returns `code: "internal.error"` and the `traceId` only.
- Standard codes (non-exhaustive):

| `code` | Status | When |
|---|---|---|
| `request.malformed` | 400 | invalid JSON / bad parameter syntax |
| `validation.failed` | 422 | body or parameter values invalid (see below) |
| `auth.unauthenticated` | 401 | no/invalid session or token |
| `auth.invalid_credentials` | 401 | wrong email/password (same message for unknown email) |
| `auth.token_expired` / `auth.token_revoked` | 401 | API token no longer valid |
| `auth.forbidden` | 403 | role/ownership prevents the action |
| `auth.insufficient_scope` | 403 | token lacks required scope (`requiredScope` extension) |
| `<resource>.not_found` | 404 | e.g. `application.not_found`, `server.not_found` |
| `<resource>.already_exists` | 409 | unique constraint (`domain.already_exists`) |
| `deployment.already_running` | 409 | per-application lock refuses a second concurrent action when queueing is disabled |
| `job.already_finished` | 409 | cancel on a finished job |
| `server.agent_unavailable` | 409 | action needs an agent that is not connected |
| `precondition.failed` | 412 | ETag mismatch |
| `confirmation.required` | 428 | destructive action without confirmation |
| `idempotency.key_reuse` | 422 | same `Idempotency-Key` with a different request |
| `idempotency.in_progress` | 409 | original request still running |
| `rate_limited` | 429 | too many requests |
| `internal.error` | 500 | unexpected |

### Validation errors

`422` with an `errors` array of precise, field-level problems. Locations use **JSON Pointer** for body fields and `parameter` for query/path:

```json
{
  "type": "urn:aethera:problem:validation.failed",
  "title": "Validation failed",
  "status": 422,
  "code": "validation.failed",
  "detail": "2 fields are invalid.",
  "traceId": "...",
  "errors": [
    { "pointer": "/domains/0/host", "code": "domain.invalid_host", "message": "Must be a valid DNS name." },
    { "pointer": "/resources/memoryLimitBytes", "code": "range", "message": "Must be at least 6291456 (6 MiB)." },
    { "parameter": "limit", "code": "range", "message": "Must be between 1 and 200." }
  ]
}
```

Field-level `code` values are short, stable validator ids (`required`, `too_long`, `too_short`, `pattern`, `range`, `invalid_enum`, `not_unique`, plus domain-specific ones). Validation uses FluentValidation (or minimal-API validation) registered once; the same rules back the OpenAPI schema constraints where expressible.

## 4. Concurrency and updates

- `POST` creates, `PUT` replaces (small config resources), `PATCH` uses **JSON Merge Patch** (RFC 7396, `application/merge-patch+json`): omitted properties unchanged, `null` clears. Arrays are replaced wholesale.
- Mutable resources return a strong **`ETag`** (derived from the row version / `xmin`). `PUT`/`PATCH`/`DELETE` honour `If-Match`; mismatch -> `412 precondition.failed`. `If-Match` is optional in v1 except where lost updates are dangerous (environment variables, secrets, compose files), where it is `428` if missing.
- Resources expose `version` (integer) only when clients need it for display; the ETag is the mechanism.

## 5. Actions and long-running operations

Actions that are not CRUD are `POST` sub-resources named with a verb:

```
POST /api/v1/applications/{id}/deploy
```

- **Short, synchronous, local** actions return `200`/`204` (e.g. `POST /secrets/{id}/rotate` writing a new version).
- **Anything that touches a server or takes more than ~1 s** is a **job**: the response is `202 Accepted` with `Location: /api/v1/jobs/{jobId}` and the `Job` as body:

```json
{
  "id": "0190f3c4-0000-7000-8000-5d1f0c2a9b10",
  "type": "application.deploy",
  "status": "queued",
  "resource": { "type": "application", "id": "0190f3c2-7b1e-7c3a-9f4d-2a6b8e1d4c55" },
  "deploymentId": "0190f3c4-0000-7000-8000-9a77e0c0aa01",
  "queuePosition": 1,
  "createdAt": "2026-10-03T14:07:31.482Z",
  "startedAt": null,
  "finishedAt": null,
  "error": null,
  "links": { "self": "/api/v1/jobs/0190f3c4-...", "logs": "/api/v1/jobs/0190f3c4-.../logs", "deployment": "/api/v1/deployments/0190f3c4-..." }
}
```

  Clients then either poll `GET /jobs/{id}` (CLI, CI: `--wait` polls with backoff until `succeeded|failed|cancelled`) or subscribe to the `/hubs/jobs` hub. Job states: `queued`, `running`, `succeeded`, `failed`, `cancelled` (spec section 15). `error` is a ProblemDetails-shaped object (`code`, `title`, `detail`, plus `failedStep` for deployments).
- Cancelling: `POST /jobs/{id}/cancel` -> `202` (cancellation is itself asynchronous) or `409 job.already_finished`.
- **Destructive actions** (delete application/server/volume/secret, `docker prune`, `compose down -v`, `rollback` over a stateful change...) require explicit confirmation: the caller passes `?confirm=<resource name or slug>` (for deletes) or `"confirm": true` in the body (for actions). Without it: `428 confirmation.required` with the expected value in `detail`. The UI shows a typed-name dialog; API clients opt in knowingly (spec section 22, 31).

### Idempotency-Key

Header `Idempotency-Key: <opaque string, 1-255 chars, UUID recommended>` is accepted on **every `POST`** and recommended for deploy actions (`deploy`, `redeploy`, `rollback`, `restart`, `stop`, `start`) and for CI usage.

- The first request with a key stores `(principal, key, method, path, SHA-256(body))` and, when finished, the response status, headers and body, for **24 hours**.
- Same key + identical request -> the stored response is replayed with `Idempotency-Replayed: true` (a retried deploy never starts a second deployment).
- Same key + different request -> `422 idempotency.key_reuse`. Same key while the first is still executing -> `409 idempotency.in_progress`.
- Keys are scoped to the authenticated principal. Git webhooks use the provider's delivery ID as their key, so provider retries are safe.

## 6. Live updates: SignalR

REST is the source of truth; SignalR (WebSockets with SSE/long-polling fallback) only **pushes changes**. Every pushed payload equals what the corresponding `GET` would return, so a client can always resync with REST after a reconnect.

| Hub | Path | Purpose |
|---|---|---|
| Jobs | `/hubs/jobs` | job and deployment state changes |
| Logs | `/hubs/logs` | live build/deploy/container/agent log lines |
| Servers | `/hubs/servers` | server/agent/Docker status, metrics, container events |

Hub contract (names are part of the API and appear in the docs and the generated client types):

```
/hubs/jobs     client -> server: Subscribe(jobId) | SubscribeResource(resourceType, resourceId) | Unsubscribe(...)
               server -> client: JobUpdated(job) | DeploymentUpdated(deployment) | DeploymentStepChanged(step)
/hubs/logs     client -> server: Subscribe(streamId, fromSequence?) | Unsubscribe(streamId)
               server -> client: LogLines(streamId, [{sequence, timestamp, stream, source, text}]) | LogStreamEnded(streamId, reason)
/hubs/servers  client -> server: Subscribe(serverId?) (omit = all visible) | Unsubscribe(serverId)
               server -> client: ServerStatusChanged(status) | ServerMetrics(serverId, sample) | ContainerEvent(serverId, event)
```

- **Authorization**: the hub connection authenticates like REST. Each `Subscribe` re-checks the caller's permission on the resource (SignalR groups are named `job:{id}`, `logs:{streamId}`, `server:{id}`); a denied subscribe fails with a hub error carrying a ProblemDetails `code`. Subscriptions are lightweight and re-established by the client after `onreconnected`.
- **Resume**: log subscriptions pass `fromSequence`; the server replays stored lines from Postgres and then switches to live, de-duplicated by sequence (ADR 0004). Other hubs resync via REST.
- **Batching**: `LogLines` messages are batches (<= 64 KiB, flushed at least every 100 ms); metrics are throttled to one message per server per second.
- **Tokens on WebSockets**: browsers authenticate with the session cookie. Non-browser clients (CLI, scripts) send `Authorization: Bearer ...`; for SignalR transports that cannot set headers, the standard `access_token` query parameter is accepted **only under `/hubs/*`** and is scrubbed from request logs.
- Hubs are typed (`Hub<IClient>`); payload types are included in the OpenAPI document as schemas (via a small `x-signalr` extension document) so the TS client gets types for them too.

## 7. Authentication and authorization

### Browser sessions

- Email/password with ASP.NET Identity's password hasher (PBKDF2/Argon2 per platform default, never plaintext). `POST /api/v1/auth/login` sets the cookie `__Host-aethera_session` (`HttpOnly; Secure; SameSite=Lax; Path=/`). Sliding expiry 12 h, absolute 30 days, "remember me" extends. `POST /auth/logout`, `GET /auth/me`.
- **CSRF**: unsafe methods authenticated by cookie must include `X-CSRF-Token` (value from `GET /auth/csrf`, double-submit/antiforgery). `SameSite=Lax` is defence in depth. Requests authenticated with a Bearer token are exempt (no ambient credentials).
- First-run setup: `POST /api/v1/auth/setup` creates the owner **only while no user exists**; afterwards it returns `409 auth.setup_completed`.
- Login is rate limited per IP and per account (exponential delay, `429` + `Retry-After`); failed logins are audited.

### API tokens

- `Authorization: Bearer aeth_<base62 secret, 40 chars>`. Format: constant prefix `aeth_` (enables secret scanning), 240 random bits. Stored as **SHA-256 hash** plus an 8-character `prefix` for display/lookup; the plaintext is returned **once**, in the creation response (`POST /api-tokens`), and can never be shown again (spec section 30).
- Token properties: `name`, `scopes[]`, `expiresAt` (nullable but defaulted by the UI to 90 days), `createdAt`, `lastUsedAt` (+ last IP), `revokedAt`. Revocation is immediate.
- **Scopes** are `<resource>:<action>` strings: `read`/`write` per resource family plus a few sensitive named actions. Effective permission = token scopes **intersected with** the owning user's role permissions (a Viewer's token can never write).

| Scope | Grants |
|---|---|
| `projects:read`, `projects:write` | projects, environments, project templates |
| `applications:read`, `applications:write` | application config CRUD, env vars (non-secret) |
| `applications:deploy` | `deploy`, `redeploy`, `rollback`, `restart`, `start`, `stop`, deployments cancel |
| `services:read`, `services:write` | services/databases CRUD and lifecycle |
| `servers:read`, `servers:write` | servers CRUD, join tokens, discovery, Docker inventory reads, maintenance |
| `deployments:read`, `builds:read`, `jobs:read` | read history and logs |
| `jobs:write` | cancel/retry jobs |
| `domains:read`, `domains:write` | domains, DNS verification |
| `registries:read`, `registries:write` | registries (credentials are write-only) |
| `secrets:read` | list secrets (names/metadata, **masked values**) |
| `secrets:write` | create/rotate/delete secrets |
| `secrets:reveal` | `POST /secrets/{id}/reveal` (audited) |
| `monitoring:read` | metrics, health, overview |
| `users:admin`, `settings:admin` | users/roles, tokens of others, instance settings, audit log |
| `*` | everything the user can do |

- Roles (Owner, Administrator, Developer, Viewer, spec section 29) are enforced with ASP.NET policies named after the permission (`applications.deploy`, ...). Single-user installs get a lone Owner and see no permission UI.
- Authentication failures never reveal whether the account/token exists.

### Audit

Every mutating request (any non-GET authorized request, plus `secrets:reveal`) writes an audit event (actor, token id if any, action, resource, IP, request id, redacted metadata) by middleware; payload bodies are never stored.

## 8. Resources

Top-level resource families follow spec section 27. IDs in paths are UUIDv7. `{id}` below is the resource ID. "List" = `GET /resource` with the standard pagination/filter/sort.

| Resource | Endpoints | Notes |
|---|---|---|
| **auth** | `POST /auth/setup`, `POST /auth/login`, `POST /auth/logout`, `GET /auth/me`, `GET /auth/csrf`, `POST /auth/password` | no auth required for `setup`/`login` only |
| **users** | `GET/POST /users`, `GET/PATCH/DELETE /users/{id}`, `PUT /users/{id}/role` | admin only |
| **api-tokens** | `GET/POST /api-tokens`, `GET/DELETE /api-tokens/{id}` | secret only in the POST response; `DELETE` = revoke |
| **organizations** | `GET /organizations`, `GET/PATCH /organizations/{id}` | one default org per install in the MVP; teams/members under `/organizations/{id}/members` later |
| **projects** | `GET/POST /projects`, `GET/PATCH/DELETE /projects/{id}`; `GET/POST /projects/{id}/environments`; `GET /project-templates`, `POST /projects/from-template` | environments (`development`/`staging`/`production`) also at `/environments/{id}` |
| **applications** | `GET/POST /applications`, `GET/PATCH/DELETE /applications/{id}` | filter `projectId`, `environmentId`, `serverId`, `status`; PATCH covers build, runtime, resources, health, strategy |
| &nbsp;&nbsp;actions | `POST /applications/{id}/deploy`, `/redeploy`, `/restart`, `/stop`, `/start`, `/rollback` | all return `202` + Job (below) |
| &nbsp;&nbsp;sub-resources | `/applications/{id}/env-vars` (CRUD, `POST .../env-vars/import`, `GET .../env-vars/export`), `/domains`, `/volumes`, `/deployments`, `/webhooks`, `GET /applications/{id}/logs` (container log history), `GET /applications/{id}/health` | env var values: plain returned, secret masked |
| **services** | `GET/POST /services`, `GET/PATCH/DELETE /services/{id}`, `POST /services/{id}/start\|stop\|restart`; `GET /service-templates` | databases/infra from templates |
| **servers** | `GET/POST /servers`, `GET/PATCH/DELETE /servers/{id}` | roles[], resource fields; `status` returns the separate axes from ADR 0002 |
| &nbsp;&nbsp;agent & discovery | `POST /servers/{id}/join-tokens` (one-time install token), `POST /servers/{id}/install-agent` (SSH bootstrap, job), `POST /servers/{id}/discovery/refresh` (job), `GET /servers/{id}/discovery` | |
| &nbsp;&nbsp;docker inventory | `GET /servers/{id}/containers\|images\|volumes\|networks`; `POST /servers/{id}/containers/{containerId}/start\|stop\|restart`; `DELETE /servers/{id}/containers/{containerId}`; `POST /servers/{id}/prune` | destructive ops need `confirm`; actions are jobs |
| **deployments** | `GET /deployments`, `GET /deployments/{id}`, `POST /deployments/{id}/cancel`, `GET /deployments/{id}/steps`, `GET /deployments/{id}/logs` | immutable history; no create (created by application actions/webhooks) |
| **builds** | `GET /builds`, `GET /builds/{id}`, `GET /builds/{id}/logs`; `POST /build-detections` (repo build detection, job) | |
| **domains** | `GET/POST /domains`, `GET/PATCH/DELETE /domains/{id}`, `POST /domains/{id}/verify-dns` | duplicate detection -> `domain.already_exists`; DNS result: expected vs actual IPs |
| **secrets** | `GET/POST /secrets`, `GET/PATCH/DELETE /secrets/{id}`, `POST /secrets/{id}/rotate`, `POST /secrets/{id}/reveal` | `GET` returns `"value": "********"` + metadata; reveal is scoped and audited |
| **volumes** | `GET/POST /volumes`, `GET/PATCH/DELETE /volumes/{id}` | persistent storage definitions/associations (Docker volume inventory is under servers) |
| **registries** | `GET/POST /registries`, `GET/PATCH/DELETE /registries/{id}`, `POST /registries/{id}/test` | credentials write-only |
| **git** (providers/credentials) | `GET/POST /git-credentials`, `GET/PATCH/DELETE /git-credentials/{id}`, `POST /git/refs` (list branches/commits for a repo) | deploy keys and tokens (secret) |
| **monitoring** | `GET /monitoring/overview`, `GET /monitoring/servers/{id}/metrics?from&to&resolution`, `GET /monitoring/containers/{id}/metrics`, `GET /monitoring/health` | time-series responses: `{ "series": [...], "resolutionSeconds": 10 }` |
| **jobs** | `GET /jobs`, `GET /jobs/{id}`, `POST /jobs/{id}/cancel`, `POST /jobs/{id}/retry`, `GET /jobs/{id}/logs` | `retry` creates a new linked job |
| **webhooks** | *Inbound:* `POST /webhooks/git/{endpointId}` (public, signature verified: GitHub HMAC SHA-256 `X-Hub-Signature-256`, GitLab `X-Gitlab-Token`; the provider delivery id is the idempotency key). *Management:* `GET/POST /applications/{id}/webhooks`, `DELETE /webhooks/{id}`, `GET /webhooks/{id}/deliveries` | `/webhooks` in spec section 27 = Git/automation webhook endpoints |
| **audit-events** | `GET /audit-events` | filter `actor`, `action`, `resourceType`, `resourceId`, time range; admin |
| **settings** | `GET/PATCH /settings`, `GET /settings/version`, `POST /settings/update` | instance configuration (admin) |

Outside `/api/v1`: `GET /health` (liveness), `GET /ready` (readiness incl. Postgres/Redis), `GET /metrics` (Prometheus, optionally token-protected), `/hubs/*` (SignalR), `/api/docs` (Scalar), `/api/openapi/v1.json` (spec).

### Application actions

All are `POST`, require `applications:deploy`, accept `Idempotency-Key`, and return `202` + `Job` (and `Location`). They share the per-application queue and lock (ADR 0004).

| Endpoint | Body (all optional unless stated) | Effect |
|---|---|---|
| `POST /applications/{id}/deploy` | `{ "ref": "main", "commit": "3f9c2d1...", "imageTag": "1.4.2", "strategy": "recreate\|lowDowntime", "noCache": false, "supersedeQueued": true }` | New deployment from source: build (or pull) + run. Without a body, deploys the configured branch tip / image. |
| `POST /applications/{id}/redeploy` | `{ "deploymentId"?: uuid }` | Re-run the last successful (or given) deployment's image and snapshot config **without** a new source revision (spec 5.3): new container, same image. |
| `POST /applications/{id}/restart` | `{}` | Restart running container(s) in place; no build, no new deployment record (a lifecycle event is recorded). |
| `POST /applications/{id}/stop` | `{ "confirm": true }` | Stop container(s); application status `stopped`. |
| `POST /applications/{id}/start` | `{}` | Start a stopped application's last deployment. |
| `POST /applications/{id}/rollback` | `{ "deploymentId": uuid (required), "restoreConfig": true }` | Redeploy an earlier successful deployment's image + configuration snapshot as a **new** deployment (`trigger: "rollback"`). `409` if that deployment was never successful or its image is gone (`rollback.image_unavailable`). |

Typical flow:

```
POST /api/v1/applications/0190.../deploy        Idempotency-Key: 6f1c...    -> 202 {job}
GET  /api/v1/jobs/{jobId}                        (poll)  or  hub /hubs/jobs Subscribe(jobId)
hub  /hubs/logs Subscribe(deployment.buildStreamId, 0)  -> LogLines...
GET  /api/v1/deployments/{deploymentId}          -> final status, failedStep, image
```

## 9. OpenAPI and the TypeScript client

- The document is produced by **`Microsoft.AspNetCore.OpenApi`** (`builder.Services.AddOpenApi("v1")`, `app.MapOpenApi("/api/openapi/{documentName}.json")`) and presented with **Scalar** (`Scalar.AspNetCore`) at **`/api/docs`**. Both can be disabled in production by setting `AETHERA_DOCS=false`; the JSON stays available behind auth.
- Every endpoint declares: a unique `operationId` in `camelCase` verb-noun form (`listApplications`, `deployApplication`), a `tag` per resource family, request/response types, all error `ProblemDetails` responses it can return, required scopes (`x-required-scope` extension and Bearer security scheme), and an example where it helps. A transformer adds the shared `ProblemDetails`/`ValidationProblem` schemas and the `Idempotency-Key`, `If-Match` headers.
- At build time (`Microsoft.Extensions.ApiDescription.Server`) the document is written to `src/web/openapi/aethera.v1.json` and committed. CI regenerates it and **fails on drift**, which doubles as the API-contract change review.
- **TS client**: `openapi-typescript` produces `src/web/src/lib/api/schema.d.ts` from that file; the app uses `openapi-fetch` (`createClient<paths>()`) for type-safe calls and `TanStack Query` for caching. A `pnpm api:gen` script runs the generation; the client is never hand-edited. Cursor pagination and ProblemDetails handling live in one thin wrapper (`ApiError` with `code`, `status`, `errors`).
- Compatibility: additions are non-breaking. Deprecations are marked in OpenAPI (`deprecated: true`) and signalled with `Deprecation` / `Sunset` response headers for at least one minor release before removal in the next major path version. A CI step diffs the committed OpenAPI file against `master` (e.g. `oasdiff breaking`) and requires an explicit label for breaking changes.

## 10. Alternatives considered

| Option | Decision |
|---|---|
| GraphQL | Rejected: conventional resources + actions + live hubs fit; OpenAPI tooling and webhooks/CLI/CI are simpler with REST |
| Offset pagination / `page` numbers | Rejected: unstable under constant inserts (jobs, logs) and slow when deep |
| `200 OK` with `{error}` envelopes or custom error format | Rejected in favour of RFC 9457, which tooling understands |
| `400` for validation | `422` chosen to separate "unparsable" from "semantically invalid" |
| Server-Sent Events instead of SignalR | Rejected: SignalR gives groups, reconnect and fallbacks in .NET; chosen in the plan |
| JWT access tokens for sessions | Rejected: opaque server-side sessions and revocable hashed API tokens are simpler and safer for a single-origin app |
| Integer or ULID IDs | UUIDv7 chosen: time-sortable like ULIDs, native `uuid` type in Postgres, generated in .NET 9+ |

## Consequences

- (+) One predictable shape for every endpoint; generated client and docs stay in lockstep with code.
- (+) Long-running operations are uniform (job + hubs) for UI, CLI and CI.
- (-) Strictness (unknown params rejected, idempotency storage, ETags) adds some boilerplate; it is centralised in shared endpoint filters and middleware.
- (-) Two live channels beyond REST (three hubs) must keep payloads consistent with REST; mitigated by reusing the same DTOs.
