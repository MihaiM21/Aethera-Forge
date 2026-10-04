# ADR 0007: Build and deploy engine

- Status: accepted
- Date: 2026-10-04
- Scope: build engines, deployment pipeline and strategies, rollback, proxy and routing, Git integration (Phase 3)
- Related: [ADR 0002](./0002-agent-communication.md) (agent protocol), [ADR 0004](./0004-jobs-and-deployments.md) (jobs, deployment lifecycle), [ADR 0006](./0006-trust-model.md)

## Where the code lives

| Concern | Place |
|---|---|
| Pipeline, strategies, plan building, proxy labels, Git providers, image policy | `Aethera.Engine` (pure: depends on `IServerTransport` and `IClock` only) |
| Jobs (`application.deploy`, `application.lifecycle`), snapshot from the database, `DeploymentService`, secret resolution | `Aethera.Infrastructure/Deployments` (Infrastructure now references Engine; Engine never references Infrastructure) |
| REST, webhook receiver | `Aethera.Api/Features/Deployments` |
| Git clone, detection, build engines, Compose, Traefik | Go agent: `internal/build`, `internal/compose`, `internal/proxy` |

The engine never talks gRPC or SSH. `ResolvingServerTransport` presents the transport resolver (agent first, SSH fallback) as one `IServerTransport`.

## Pipeline

`DeploymentRunner` drives the nine steps of ADR 0004. Image source: Source and Build are skipped, the image is pulled. Source build: the agent clones, builds and tags `aethera/<slug>:<deploymentId>`. Compose: Source, Build and Image are skipped and `compose up --wait` does the work. Every step is written to `deployment_steps` and saved, and every command carries `<job>:<operation>:0` as idempotency key, so a job that is redelivered after a crash re-runs from the frozen snapshot without repeating completed work.

Failures carry a stable code (`build.failed`, `image.pull_failed`, `image.missing`, `container.failed`, `network.failed`, `health.timeout`, `server.unavailable`, `proxy.failed`, `source.unsupported`) on the failed step. `server.unavailable` is the only retryable one.

## Snapshot and rollback

When the job starts, the application's configuration is frozen into `DeploymentSnapshot` (JSON on the deployment). It holds secret ids and pinned versions, never values. A rollback is a new deployment (`trigger = rollback`) that copies the target's snapshot and runs the target's image from the server's own store: the image is inspected, not pulled. If the image was pruned the deployment fails with `image.missing` and an explanation. Rollback points are kept by `ImagePolicy` (live deployment plus the newest three).

## Strategies

- **Recreate**: remove old container, create new, probe. Brief downtime; a failure after the old container was removed leaves the application `Failed`.
- **Low-downtime**: start `<name>-<number>` next to the old container with the same proxy labels, probe it, then remove the old one. A failed probe removes only the candidate; the old container keeps serving. Verified against a real Traefik: a second container with identical labels joins the service and the route keeps answering after the first is removed.
- **Compose**: project-scoped, user file stored verbatim, Aethera override file adds proxy labels and the external proxy network.

## Health checks

Probes address the container by name. The agent resolves container names through Docker (`Agent.resolveProbeHost`) before applying its SSRF policy, because container names do not resolve on the host.

## Proxy

`IProxyProvider` (Traefik only) renders the static configuration and per-route labels. Hosts, route names and paths are validated against a strict grammar before they are put into a rule, so a hostname cannot inject a Traefik rule. The agent's `proxy.ensure` creates `aethera-traefik` with the Docker socket mounted read-only (the only place the socket reaches a container; it is not reachable through any command). Domains of one application are grouped per (target port, path prefix) into one router; consecutive deployments share the route name, which is what makes the low-downtime strategy work.

**Traefik must be v3.6 or newer.** Docker Engine 29 raised the minimum API version and Traefik 3.1 then fails to talk to the daemon (every route answers 404). The default is `v3.6`; `Aethera:Proxy:Version` overrides it.

DNS verification of domains already exists (`POST /domains/{id}/verify-dns`, WP1.2).

## Git

`IGitProvider` for GitHub (HMAC-SHA256), GitLab (token) and generic hosts (`X-Aethera-Token`). The receiver (`POST /webhooks/git/{endpointId}`) is anonymous and answers 401 identically for an unknown endpoint, a disabled one and a bad signature; bodies are capped at 1 MiB; deliveries are idempotent on `(endpoint, delivery id)`; invalid deliveries are not stored (no unauthenticated writes). Pushes deploy only when auto-deploy is on, the branch matches the endpoint filter (or the configured branch), the branch was not deleted and no commit is pinned. Setting the webhook up again rotates its secret.

Credentials reach the agent in the build request. The agent passes an HTTPS token to git through the environment (`GIT_CONFIG_*`) and an SSH key through a 0600 temp file, never on a command line; repository URLs and refs are validated so they cannot become git options or local transports. Git credentials are managed secrets: creating or deleting them needs Administrator and the `secrets:write` scope, and they are write-only.

## Build engines (agent)

`dockerfile` (`docker build`, BuildKit), `nixpacks`, `static` (Node build stage, nginx runtime, optional SPA fallback) and `image` (pull and tag). Build argument and environment values travel in the process environment (`--build-arg NAME`), build secrets as 0600 files mounted with `--secret`, so neither shows up in the process list or in the image. `engine_name` takes precedence over the enum, so Railpack can be added without a protocol change. Detection proposes Dockerfile, static site, Nixpacks and per-language candidates with confidence, ports and commands.

## Known limits

- Compose applications need the compose file pasted in (`inlineContent`); reading it from the repository is not implemented. Compose services that use `build:` need a build context and are therefore not supported either.
- Inline Dockerfiles (`dockerfileInline`) are rejected with `source.unsupported`.
- The Nixpacks engine is covered by argument-vector tests only; it was not run against the real CLI.
- `POST /registries/{id}/test` still answers 501.
- Log streams: build output is stored under `build:<buildId>` (the build row id is the agent's stream id); the job narration is under `job:<id>`.
