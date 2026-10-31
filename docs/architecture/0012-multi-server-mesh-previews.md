# ADR 0012: Multi-server placement, the private mesh and pull request previews

- Status: accepted
- Date: 2026-10-30
- Scope: R2 (v0.2.0). Plan and decisions: [`release-0.2.md`](../release-0.2.md).
- Related: [ADR 0002](./0002-agent-communication.md) (command allowlist), [ADR 0004](./0004-jobs-and-deployments.md) (deployments, jobs), [ADR 0006](./0006-trust-model.md) (trust model), [ADR 0007](./0007-build-and-deploy.md) (engine), [ADR 0011](./0011-automatic-domains-and-first-try-deploys.md) (automatic domains)

## Contracts added

| Contract | What |
|---|---|
| `proto/.../mesh.proto` | `MeshApply`, `MeshStatus`, `MeshRemove` commands and the `MeshState` result (command arms 130-132, result arm 130). Capabilities `mesh.wireguard`, `mesh.tailscale`. |
| `proto/.../build.proto` | `BuildRequest.builder_limits`: the limits of the BuildKit builder container the build runs in. Capability `build.quota`. |
| `proto/.../common.proto` | `ResourceLimits.oom_score_adj` (workload priority). |
| Migration `AddR2MultiServerMeshPreviews` | `servers`: `build_cpu_limit`, `build_memory_limit_bytes`, `build_registry_id`, `mesh_provider`, `mesh_address`, `mesh_public_key`, `mesh_endpoint`, `mesh_listen_port`, `mesh_status`, `mesh_status_changed_at`, `mesh_error`, `mesh_handshake_at`. `registries.namespace`. `workloads`: `build_server_id`, `priority`, `previews_enabled`, `preview_allow_forks`, `preview_server_id`, `preview_max_active`. `workload_ports.bind_scope`. `environment_variables.is_preview`. `deployments`: `preview_id`, `build_server_id`. New table `previews`. |
| Settings key | `mesh` (`provider`, `cidr`, `listenPort`) |
| Seams | `IPlacementService` (Domain), `IPullRequestCommenter` (Engine), job types `mesh.reconcile`, `preview.teardown`, `preview.sweep` |
| API (filled by R2.1-R2.3) | `/placement/suggest`, `/servers/capacity`, `/servers/{id}/move` (on workloads: `POST /applications/{id}/move`, `/services/{id}/move`), `/system/mesh`, `/servers/{id}/mesh`, `/applications/{id}/previews` |

## Roles and placement

`ServerRole` keeps its values. Semantics from R2:

| Role | Runs |
|---|---|
| `Worker` | applications (and services that are not databases) |
| `Storage` | databases and stateful services (templates with a data volume) |
| `Build` | builds of applications whose build server it is, or that run on it |
| `Master` | the control plane's own machine (the `local` server) |

A server **without roles accepts everything**, so a one-server install never changes behaviour. Roles are advice for placement and a check on create and move (an explicit choice against the role is accepted with a warning in the response); they never stop or move a running workload.

`IPlacementService.RankAsync` scores the organisation's active servers for a workload kind and its reservations: not eligible when the lifecycle is not `Active`, the agent is not connected or the role excludes the kind; then ranked by unreserved memory (facts minus the sum of memory reservations, or limits where no reservation is set, of the workloads placed there), then free disk, then fewer workloads. Every candidate carries its reasons, which `/placement/suggest` returns as they are.

## Build elsewhere

`Application.BuildServerId` (null = the run server). When it differs from `Workload.ServerId`:

1. The build server must have a `BuildRegistryId`. Its registry's `Namespace` (for example `mihaim21` on `ghcr.io`) gives the reference `<registry host>/<namespace>/<slug>:<deployment id>`.
2. The build command runs on the build server with `push = true` and the registry's credentials; the step log names both servers.
3. The run server pulls the reference with the same credentials (`Image` step); `Deployment.BuildServerId` and `ImageRef` record it.
4. A rollback pulls the stored reference, so it works on any server and after the run server pruned the image. Image cleanup removes the local copies only; nothing is deleted from the registry.

A missing registry fails the `Build` step with `build.registry_missing` before anything is built.

## Build quota

`Server.BuildCpuLimit` and `BuildMemoryLimitBytes` (null = unlimited). When either is set the agent runs builds through a BuildKit builder container `aethera-buildkit` started with these limits (recreated when they change) instead of the daemon's built-in builder. `MaxConcurrentBuilds` keeps limiting how many build jobs run at once.

## Priority

`RuntimeConfig.Priority`: `Normal` (default), `High`, `Low`. High doubles the CPU shares derived from the reservation (or 1024 without one) and sets `oom_score_adj = -500`; Low halves them and sets `oom_score_adj = 500`. Compose services do not get it.

## The mesh

Instance setting `mesh`: `provider` (`none` default, `wireGuard`, `tailscale`), `cidr` (default `10.210.0.0/16`), `listenPort` (default 51820).

**WireGuard.** The control plane allocates each server an address from the CIDR (`Server.MeshAddress`, stable, never reused while the server exists). A `mesh.reconcile` job sends every connected server a `MeshApply` with its address and the full list of peers: each other server's public key, endpoint (`MeshEndpoint`, else `<public ip>:<listen port>`) and `/32` allowed IP, with a 25 s keepalive. The agent creates its key pair on first use in its state directory (mode 0600) and **the private key never leaves the server**; `MeshState` returns the public key, which is stored on the server and triggers another reconcile when it changed. The reconcile runs when the mesh is enabled, a server connects for the first time with the mesh on, a key or endpoint changes, or a server is deleted (its peer is removed everywhere). The agent applies the configuration declaratively: peers not in the list are removed. It never touches other interfaces, firewall rules or routes outside the mesh CIDR.

**Tailscale.** The agent reports the IPv4 address of `tailscale0` as its mesh address (`MeshApply` with provider `TAILSCALE` only reads it). Aethera does not log in to Tailscale or change its configuration.

**Status.** `Server.MeshStatus`: `Disabled`, `Pending` (waiting for its key or first apply), `Up` (every peer had a handshake in the last 3 minutes), `Degraded` (some peers did not), `Failed` (the apply failed; `MeshError` says why). Refreshed by `MeshStatus` from the reconcile job and the periodic status sweep.

**Private-network deploys.** `WorkloadPort.BindScope`: `Public` (all interfaces, the default and the only behaviour before R2), `Private` (the server's mesh address; refused when the server has none), `Loopback` (`127.0.0.1`). Connection strings shown for services use the mesh address when the port is private.

## Pull request previews

**Settings** (on the application): `PreviewsEnabled`, `PreviewAllowForks` (false), `PreviewServerId` (null = the application's server), `PreviewMaxActive` (null = 10). Environment variables with `IsPreview` apply only to previews and override a regular variable with the same key.

**Events.** The existing webhook endpoint also accepts GitHub `pull_request` (`opened`, `reopened`, `synchronize`, `closed`) and GitLab `Merge Request Hook` (`open`, `reopen`, `update` with new commits, `close`, `merge`), signature checked as for pushes. A pull request from a fork is ignored (delivery recorded as `Ignored`, with the reason) unless `PreviewAllowForks`. Only pull requests targeting the application's branch are previewed.

**Model.** A `PreviewEnvironment` row per (application, pull request number). A preview deployment is a normal `Deployment` with `PreviewId` set and trigger `PullRequest`: it uses the application's configuration with the pull request's head commit, but its own names (`<slug>-pr-<n>` for containers, named volumes and compose projects), its own host (`pr-<n>-<automatic host label>.<automatic domain base>`) and it never changes the workload's status or `CurrentDeploymentId`. The preview's own `CurrentDeploymentId` and status play that part. Closing the pull request queues `preview.teardown` (containers, route, volumes, images); `preview.sweep` (daily) removes previews whose pull request has had no event for 14 days. When `PreviewMaxActive` is reached a new pull request is recorded but not deployed.

**Comments.** `IPullRequestCommenter` creates the comment once and edits it afterwards (`ProviderCommentId`): status, URL, commit. It needs a token credential on the git source; without one nothing is posted. Comment failures are logged on the preview and never fail the deployment.

## R2.1 implementation notes (placement, build elsewhere, moving)

Where the implementation adds to or differs from the text above:

- **Master servers still take workloads.** The installer creates the local server with `master` + `build`; read literally, "role excludes the kind" would make a fresh install unable to place anything. A server whose roles lack the kind's role but include `Master` is eligible, ranked after servers that have the role (`RoleFit.MasterFallback`). Servers with other roles that lack it (a `Worker` asked to run a database) stay excluded.
- **Memory is an eligibility rule.** When the request names a memory reservation and the server's memory is known, a server with less unreserved memory is not eligible (`docs/release-0.2.md`: "enough unreserved memory"). CPU shortage is only a reason (CPU over-commits are normal). The candidate `score` is the position among the eligible ones.
- **Connected** means `AgentStatus == Connected` for agent servers and "not `Unreachable`" for SSH servers.
- **Kind of a service** follows its data: a service with a volume (every built-in template) is `StatefulService`, one without is `StatelessService`.
- **`MaxConcurrentBuilds`** (documented in ADR 0004 but not enforced until now) is enforced as PostgreSQL session advisory locks per (server, slot), held for the Build step of the build server (`BuildSlotGate`).
- **Build quota** is sent only to agents advertising `build.quota`, and not for Nixpacks builds (it drives Docker itself). The agent runs the build through `docker buildx build --builder aethera-buildkit --load`, the builder being a `remote` buildx driver over the `aethera-buildkit` container (`moby/buildkit`, privileged as BuildKit needs, `--cpus`, `--memory`, `--memory-swap`).
- **Docker Hub** build registries use `docker.io/<namespace>` in references and `https://index.docker.io/v1/` as the credential key.
- **Moving**: `POST /applications/{id}/move` queues a normal deployment on the target (`DeployPayload.MoveFromServerId`). The deploy job switches the workload and its domains to the target when it starts (so the new deployment is frozen with them), and when it ends: success queues an `application.teardown` job for the *old* server (`TeardownPayload.ServerId`) and removes the old images; failure or cancellation puts the workload and its domains back. No new job type, no migration; the follow-up reruns safely when the job is retried or redelivered.
- **Previews and the build server**: a preview uses the application's build server like a production deployment. It builds there and pushes through the build registry only when the preview's own server differs (`PreviewServerId`, else the application's); on the same server it builds locally. Old preview images are removed from the build server too.
- **Responses** of create and update carry `warnings` (strings, always an array); role mismatches, a build server without the build role or registry, and over-commitment are warnings, never errors.

## Security notes

- The mesh commands configure one named interface. The agent refuses an interface name other than `aethera0`/`tailscale0`, addresses outside the configured CIDR, and allowed IPs wider than `/32` per peer.
- Preview deployments run code from a pull request with the application's (and preview) secrets: forks are off by default and the setting says so.
- Registry credentials for pushes are resolved like pull credentials: decrypted in memory, registered for log redaction, never stored in a snapshot.
