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
| **auth** | `GET /auth/setup` -> `{ "setupRequired": bool }` (anonymous; true while no user exists, so the UI knows whether to show first-run setup or login), `POST /auth/setup`, `POST /auth/login`, `POST /auth/logout`, `GET /auth/me`, `GET /auth/csrf`, `POST /auth/password` | no auth required for `GET`/`POST /auth/setup` and `POST /auth/login` only |
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

## Implementation notes (WP1.0)

WP1.0 built the shared plumbing; feature work packages only add endpoints. Where this section differs from the text above, this section is what the code does.

### Composition

- `Program.cs` is final: `AddAetheraApi` (shared), then `AddAuth` / `AddResources` / `AddJobs` (WP1.1 / 1.2 / 1.3, `Aethera.Api/Features/*/…Module.cs`), the pipeline (`UseAetheraApi`), and `MapAuth` / `MapResources` / `MapJobs` on **one** `app.MapGroup("/api/v1")`, plus `MapJobsHubs(root)` for `/hubs/*`. No work package edits `Program.cs`.
- The `/api/v1` group is **secure by default**: it requires an authenticated caller and limits request bodies to 1 MiB. Anonymous endpoints (`GET/POST /auth/setup`, `POST /auth/login`) must say `.AllowAnonymous()`. Add `.RequireRole(AetheraPolicies.X)` and `.RequireScope(Scopes.Y)` per endpoint. Every endpoint needs `.WithName("verbNoun")` (the OpenAPI `operationId`, unique, camelCase; a test fails otherwise) and a tag per resource family (`.WithTags("Projects")`, usually on the `MapGroup("/projects")`).
- Validators (FluentValidation) in the API assembly are registered automatically; endpoints opt in with `.Validate<TBody>()` (422, `errors[]` with JSON Pointers). Domain exceptions, `DbUpdateConcurrencyException` (409 `concurrency.conflict`) and unique violations (409 `resource.conflict`) are mapped centrally; deeper code can `throw new ApiProblemException(ApiProblems.X(...))`.
- `GET /ready` runs every registered `IReadinessCheck` (PostgreSQL built in; WP1.3 registers Redis) and reports them by name in `checks`.

### Differences and clarifications

- `traceId` in error bodies **is** the `X-Request-Id` of the response (an inbound id that matches `[A-Za-z0-9._:-]{1,100}` is echoed; otherwise the W3C trace id or a new GUID is used). `traceparent` is also returned when the request has an activity.
- Out-of-range `limit` is `400 validation.invalid_parameter` (section 2); the `limit` line in the 422 example of section 3 is illustrative only. Unknown `sort` fields are the same `400` with `parameter: "sort"`.
- The 401 for a bad/absent credential is `auth.unauthenticated`; a handler can refine it (`auth.token_expired`, `auth.token_revoked`) by setting `HttpContext.Items["Aethera.AuthFailureCode"]`. A token missing a scope gets `403 auth.insufficient_scope` with the `requiredScope` extension; a role failure is `403 auth.forbidden` (and wins when both fail).
- Validation errors only carry `pointer` or `parameter` (whichever applies); the other member is omitted.
- The shared `ProblemDetails` / `ValidationProblem` / `FieldError` schemas and the `cookieAuth` / `bearerAuth` security schemes are in the OpenAPI document; each operation gets 401/403 (when authorized), 422 (when it validates) and 500 responses plus the `x-required-scope` extension. The `Idempotency-Key` / `If-Match` header parameters are not yet added to the document (no middleware implements them yet).
- The build writes the document to `src/web/openapi/aethera.v1.json` (the generator only accepts `[A-Za-z0-9_-]` in file names, so a build target renames `aethera-v1.json`). The document name stays `v1`, so it is served at `/api/openapi/v1.json`. With `AETHERA_DOCS=false` the Scalar UI is not mapped and the JSON requires at least the Viewer role.
- Audit: `IAuditLog` / `EfAuditLog` are the writer (actor, request id, IP from `ICurrentActor`; metadata redacted). The "every mutating request" middleware of section 7 belongs to WP1.1.

### Token scopes (Phase 1)

Phase 1 implements seven coarse scopes (`Aethera.Api.Security.Scopes`). The resource-level table in section 7 is the longer-term vocabulary; add finer scopes there only with an ADR amendment. Scopes only narrow a token below its owner's role (effective permission = scope AND role); browser sessions are bound by role only, so `RequireScope` is ignored for them.

| Scope | Grants |
|---|---|
| `read` | all reads except secrets |
| `write` | create/update/delete resources except secrets and servers; implies `read` |
| `deploy` | deploy, redeploy, rollback, restart, start, stop; cancel/retry jobs |
| `secrets:read` | list secrets (masked) |
| `secrets:write` | create/rotate/delete/reveal secrets; implies `secrets:read` |
| `servers:write` | servers CRUD, join tokens, agent install, prune |
| `admin` | users, roles, others' tokens, settings, audit log; satisfies every scope (as does `*`) |

### Claim types (`Aethera.Api.Security.AetheraClaimTypes`)

| Claim | Meaning |
|---|---|
| `aethera:user_id` | user id (sessions and tokens) |
| `aethera:token_id` | API token id (token principals only) |
| `aethera:org_id` | organization id |
| `aethera:role` | `viewer` / `developer` / `admin` / `owner` (for tokens: the owner's role) |
| `aethera:scope` | one claim per token scope (token principals only) |
| `aethera:auth_method` | `session` or `token` |

Authentication schemes: default `Aethera` (policy scheme) forwards to `Aethera.Session` (cookie) or `Aethera.Token` (`Authorization: Bearer`, or `access_token` under `/hubs`). `AetheraPrincipal.Create` builds principals with exactly these claims.

## Implementation notes (WP1.5)

WP1.5 (integration and hardening) changed or fixed the following. Where this section differs from the text above, this section is what the code does.

### Hub Origin rule (`auth.origin_not_allowed`)

The session cookie is `SameSite=Lax`, but applications deployed by Aethera usually run on **sibling subdomains** of the panel, and those are the *same site*. A page on `app.example.com` could therefore open a WebSocket, or send negotiate / long-polling requests, to `/hubs/*` and the browser would attach the administrator's cookie (SignalR has no per-request CSRF token).

- Every `/hubs/*` request (negotiate, WebSocket, SSE, long polling) that is authenticated by the **session** scheme must carry an `Origin` header equal to the request's own origin (`scheme://host[:port]`, compared after forwarded-header processing, default ports normalised, case-insensitive) **or** listed in `AETHERA_CORS_ORIGINS`. Otherwise: `403` with ProblemDetails code `auth.origin_not_allowed`.
- Bearer-token requests (the `Authorization` header and the `access_token` query parameter) are exempt: they carry no ambient credentials. So are anonymous requests, which are answered `401` by authorization as before. The REST API is unaffected (CSRF tokens cover it).
- Browsers send `Origin` on WebSocket handshakes and same-origin POSTs but **not** on same-origin GETs (the SSE and long-polling transports). For those, the browser-controlled `Sec-Fetch-Site: same-origin` header is accepted instead; a sibling subdomain sends `same-site` and is still refused. A cookie request with neither header is not a browser page and is refused.
- Implemented by `HubOriginMiddleware` (after authentication, before authorization); the code is in the OpenAPI `x-known-codes`.

### Reveal requires Admin and `secrets:write`

`POST /secrets/{id}/reveal` needs the **Administrator** role (or Owner) *and*, for API tokens, the `secrets:write` scope, which covers reveal in Phase 1 (the finer `secrets:reveal` scope of section 7 is the long-term vocabulary). It is audited on every call, answers with `Cache-Control: no-store`, and the plaintext appears in no other response, log or audit entry. Developers can create, rotate and delete secrets but never read one back.

### Dictionary keys are verbatim

`JsonSerializerOptions.DictionaryKeyPolicy` is **not** set. Property names are camelCase; the keys of dictionaries are data and are kept exactly as stored (`NODE_ENV`, `X-Custom`). WP1.0 had set `CamelCase` for both, which turned `NODE_ENV` into `node_ENV`.

### Forwarded headers

`UseForwardedHeaders` runs **first** in the pipeline, so the login per-IP limit, audit events, token `lastUsedIp`, cookie security and the hub origin check all see the real client address and scheme behind Traefik.

- Honoured: `X-Forwarded-For` and `X-Forwarded-Proto` only (not `X-Forwarded-Host`; Traefik preserves `Host`).
- Only when the TCP peer is a trusted proxy: `Aethera:Http:TrustedProxies` (environment `Aethera__Http__TrustedProxies`), comma-separated IP addresses or CIDR ranges, for example `172.18.0.0/16,10.0.0.5`. **Default: loopback only** (`127.0.0.0/8`, `::1`); a configured list replaces the default. There is no wildcard; an invalid entry stops the start-up. From any other peer the headers are ignored.
- Behind Traefik in Docker, set it to the subnet of the Docker network Traefik is on.

### Readiness is tri-state

`IReadinessCheck.CheckAsync` returns a `ReadinessResult`: `Ok`, `Unavailable` or `Skipped`, each with an optional `Detail`. `GET /ready` answers `503` **only** when a check is `Unavailable` (an exception or a 3 s timeout counts as unavailable). `Skipped` means "not configured, nothing needs it" and never fails readiness: the Redis check is `skipped` when `ConnectionStrings:Redis` is not set. The body is `{ "status": "ready|unavailable", "checks": { "database": "ok", "redis": "skipped" }, "details": { "redis": "..." } }`. A detail is a fixed phrase written by the check; it never contains a connection string, host, user name or exception message (exceptions are logged, not returned).

### Jobs belong to an organization

`jobs.organization_id` (NOT NULL, FK) replaces the inference from `created_by`; see `src/control-plane/docs/schema.md`. `IJobQueue.EnqueueAsync` takes it from `JobRequest.OrganizationId`, else from `ICurrentActor`, else throws. Creator-less system jobs (webhooks, schedules) therefore belong to exactly one organization, and REST, hubs and log streams all filter on it.

### Smaller changes

- `GET /auth/csrf` needs a session, so the web client sends no CSRF token for `POST /auth/login` and `POST /auth/setup`.
- Brotli/gzip response compression is enabled for text responses (not over TLS terminated by the app itself).
- The static web UI is served by the API (ADR 0005, `Aethera:Web:Root`).
- Hosted services and start-up checks do nothing while the build-time OpenAPI generator runs (`AetheraHost.IsOpenApiGeneration`).

## Implementation notes (WP1.6)

WP1.6 (security fixes and the trust model, [ADR 0006](./0006-trust-model.md)) changed the Resource API as follows. Where this section differs from the text above, this section is what the code does.

### Secrets have a purpose

- `SecretResponse` gains `purpose` (`user`, `registryCredential`, `sshCredential`, `gitCredential`, `serviceGenerated`), `managed` (`purpose != user`) and `managedBy` (`{type, id, name}` with `type` one of `registry`, `server`, `gitCredential`, `service`; `null` for user secrets). Managed secrets are listed like any other.
- `PATCH /secrets/{id}`, `POST /secrets/{id}/rotate` and `DELETE /secrets/{id}` on a managed secret: **409 `secret.managed`** (the `detail` names the owning endpoint; the problem also carries `purpose` and `managedBy`). Change them through the registry or server endpoint instead.
- Binding a secret to an environment variable (`POST` and `PATCH .../env-vars`, `secretId`): a Developer may bind `user` secrets scoped to the same workload, environment or project; an organization-scoped secret needs an Administrator; a managed secret is never bindable (a generated service password only to its own service). Refusals are **403 `secret.binding_forbidden`**. A scope that does not contain the workload is still 422 `scope_mismatch`.
- `sshCredentialSecretId` on a server turns an organization-scoped user secret into an `sshCredential` (422 `secret.managed`, `secret.in_use`, `scope_mismatch` otherwise) and releases it when no server uses it.

### Scopes

The coarse scopes of WP1.0 are unchanged. Two additions to who needs `secrets:write` (the `write` scope still excludes secrets):

- Registries: `POST`, `PATCH`, `DELETE /registries` need **`write` and `secrets:write`** (they create, replace or remove a credential). A token with `write` only gets `403 auth.insufficient_scope` with `requiredScope: secrets:write`. The OpenAPI `x-required-scope` shows the last, `secrets:write`. Git credential endpoints, when added, follow the same rule.
- Binding a secret to an environment variable with a token needs `secrets:write` on top of `write`.

### New problem codes

| Code | Status | When |
|---|---|---|
| `secret.managed` | 409 | change, rotate or delete of a managed secret through `/secrets` |
| `secret.binding_forbidden` | 403 | the caller may not bind this secret (managed, or organization-wide without Administrator) |
| `volume.host_path_requires_admin` | 403 | `hostPath` set or changed by a Developer |
| `volume.host_path_forbidden` | 422 | host path on the denylist (also for compose bind sources, with `pointers`) |
| `port.privileged_requires_admin` | 403 | published host port below 1024 by a Developer |
| `port.reserved` | 422 | published host port reserved by Aethera, without Administrator and `allowReserved` |
| `compose.option_requires_admin` | 403 | inline compose uses root-equivalent options; `pointers` lists JSON Pointers into the compose document |
| `compose.invalid` | 422 | inline compose is not valid YAML or exceeds a limit (256 KiB, 50 aliases, depth 32) |

The trust-model problems carry `errors[]` entries with a request-body `pointer` (`/hostPath`, `/runtime/ports/0/publishedPort`, `/compose/inlineContent`, `/config/compose`) and the problem's own `code`.

### Request changes

- `PortRequest.allowReserved` (bool, optional, not stored, Administrators only): publish a reserved host port anyway. Unchanged ports of an update are not checked again.
- `hostPath` is normalized (`//` collapsed, `.` dropped, trailing `/` removed, `..` rejected) before it is checked and stored.
- `pathPrefix` of a domain: `\A/[A-Za-z0-9._~/-]*\z`, at most 256 characters, no `//`, `.` or `..` segment (was: anything without whitespace, `?`, `#`, `\`, `"`; `//` was collapsed, now it is a `pattern` error).
- `build.context`, `build.dockerfilePath`, `build.outputDirectory`, `compose.filePath`: relative, inside the repository (no `..`, leading `/`, `~` or `-`, drive letter, backslash, control character).
- `gitSource.branch`: a valid git ref that cannot be an option (no leading `-`, whitespace, control characters, `~ ^ : ? * [ \`, `..`, `@{`). `gitSource.repositoryUrl`: http(s), ssh or git URL or `user@host:path`, with no `-` at the start of a user or host, no `file:` and no `::` transport helper, no whitespace or control characters.
- `compose.inlineContent` is limited to 256 KiB (was 512 KiB).

### Validation anchors

Every validator regular expression under `Features/Resources`, `Features/Auth` and `Aethera.Domain` uses `\A...\z`. .NET's `$` also matches before a final `\n`, so `^[a-z]+$` accepted `"abc\n"`. A reflection test fails when a new pattern in these namespaces starts with `^` or contains an unescaped `$`.

### Dotenv export

`GET .../env-vars/export` writes every value in single quotes with an embedded `'` as `'\''` (`KEY='it'\''s'`), so sourcing the file in a shell cannot expand or execute anything. Import still accepts unquoted, double-quoted and single-quoted values, including the `'\''` idiom; export then import reproduces the values (the one exception: a carriage return is read back as a line feed).

