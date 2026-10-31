# Running on several servers

Aethera places workloads on the servers of your organization, can build on one server and run on another, limits what builds and workloads may use, and moves a workload to a different server. The design is [ADR 0012](architecture/0012-multi-server-mesh-previews.md); this is the user guide for the placement, build and resource parts. (The private network between servers and pull request previews have their own guides.)

## Roles

A server has any of these roles (Servers > the server > Settings > General):

| Role | Aethera puts there |
|---|---|
| **Worker** | applications, and services without data |
| **Storage** | databases and every service with a data volume (all the built-in templates have one) |
| **Build** | builds of applications that name this server as their build server |
| **Master** | the machine the control plane runs on. It takes workloads too, but a server that has the right role is preferred |

Rules worth knowing:

- A server **with no roles takes everything**. A one-server install never has to think about roles.
- The installer gives the local server `master` + `build`. It still takes applications and databases, so a fresh install keeps working with "Automatic" server choice.
- Roles are advice for **new** placements and a check on create and move. Choosing a server by hand against its roles works, and the answer carries a warning. A role never stops or moves something that already runs.

## Placement: "Automatic"

Creating an application or a service **without a server** (the default in the UI, "Automatic") lets Aethera choose:

1. A server is **not eligible** when it is not active, its agent is not connected (for an SSH server: the probe says it is down), its roles exclude what is being placed, or its unreserved memory is smaller than what the workload reserves.
2. Eligible servers are ranked by **unreserved memory** (the server's memory minus what workloads placed there reserve, or are limited to when they reserve nothing), then free disk, then fewer workloads. A server that only qualifies because it is the master machine comes after those with the right role.
3. The best one is used. When none is eligible you get a clear 422 (`placement.no_eligible_server`) naming every server and why not.

`GET /api/v1/placement/suggest?kind=application&memory=<bytes>&cpu=<cores>` returns the ranking with the reasons of every server, and the wizard shows it before you create anything (`kind` is `application`, `statefulService`, `statelessService` or `build`; `workloadId=` judges an existing workload).

## Capacity

Servers > Capacity (and the overview of each server) shows cores, memory and disk, live usage, what is **reserved** by the workloads placed there and what is left. A server whose reservations exceed its size is marked **over-committed**; limits above the machine are noted but fine (they cap bursts). API: `GET /api/v1/servers/capacity`.

## Limits, reservations and priority

On an application (Resources tab) or a service you can set CPU and memory **limits** (a ceiling the container cannot cross) and **reservations** (what it is promised; placement counts them). The tab shows what is left on the server and warns when what you type would over-commit it.

**Priority** says who gives way when the server is short:

| Priority | CPU shares | OOM killer |
|---|---|---|
| High | doubled (from the reservation, or one core's worth) | protected (`oom_score_adj` -500) |
| Normal | as reserved | default |
| Low | halved | picked first (500) |

It applies when the container is created, on the next deployment. Compose applications do not get it.

## Build on one server, run on another

For an application that builds from source you can pick a **build server** (Build tab). The image then travels through a registry:

1. the build server builds and **pushes** `<registry>/<namespace>/<app>:<deployment id>`;
2. the run server **pulls** it with the same credentials;
3. rollbacks pull the stored reference, so they work on any server and after the run server pruned the image. Cleanup removes local copies only; nothing is deleted from the registry.

No registry means no building elsewhere: the Build step fails with `build.registry_missing` before anything is built, and the settings show the warning as soon as you pick such a server.

### Setting it up with GHCR

1. Create a GitHub personal access token (classic) with `write:packages` (and `read:packages`).
2. Registries > Add registry: URL `ghcr.io`, username your GitHub user, the token as password, **namespace** your user or organization (stored in lower case, as GHCR requires). Use "Test" to check the credentials.
3. Servers > the build server > Settings > Builds: choose that registry as the **build registry**.
4. Application > Build > Build server: choose the build server.

Docker Hub works the same way (URL `index.docker.io`, namespace your account). Packages created by the first push are private by default, which is what you want: the run server pulls with the same token.

### Quota and parallel builds

On the same Builds form you can limit the **CPU and memory** builds on that server may use. The agent then runs builds in a BuildKit builder container `aethera-buildkit` (`moby/buildkit`) with those limits, and recreates it when you change them. Empty means no limit (the Docker daemon's own builder). Notes:

- It needs an agent that advertises `build.quota` (this release's). With an older agent the build runs without the quota and the deployment log says so.
- Nixpacks builds are not limited yet (they drive Docker themselves); Dockerfile and static builds are.
- Multi-platform builds are refused together with a quota.
- **Max concurrent builds** (default 1) still applies to the build server, whether it builds for itself or for another server. A build waits for a free slot and says so in its log.

## Moving a workload

Application or service > Settings > Move to another server (`POST /applications/{id}/move`, `/services/{id}/move`):

1. a deployment of the current configuration is queued on the target server; the old server **keeps serving**;
2. when the workload moves, its domains move with it (an automatic domain is renamed after the new server, `<name>.<new ip>.sslip.io` or the new wildcard domain);
3. when the new deployment runs, the containers and images of the workload on the old server are removed;
4. if the deployment fails, the workload stays on (or goes back to) the old server, which was never touched.

**Volumes do not move.** A workload with volumes needs your confirmation, and the answer lists them: the data stays on the old server, so back it up first (Backups) and restore it on the new one. A workload that never ran just changes server. The target must be active and connected; a server against the workload's roles is accepted with a warning.
