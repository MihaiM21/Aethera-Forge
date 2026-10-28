# ADR 0011: Automatic domains, the dashboard domain and first-try deployments

- Status: accepted
- Date: 2026-10-28
- Scope: R1.7. Applications are reachable without adding a domain, as in Coolify. The domain of the Aethera UI and the Let's Encrypt account are set in the UI. Docker and Compose deployments work the first time without hand-tuning ports or health checks.
- Related: [ADR 0004](./0004-jobs-and-deployments.md) (deployment model), [ADR 0007](./0007-build-and-deploy.md) (engine, proxy), [ADR 0009](./0009-packaging-and-self-ops.md) (packaging), [ADR 0010](./0010-backups-notifications-tasks.md) (domain options, compose from Git)

## Contracts added

| Contract | What |
|---|---|
| `proto/.../commands.proto` | `ProxyEnsure.dynamic_config`: a Traefik file-provider document, written to a watched directory and reloaded live |
| `proto/.../docker.proto` | `ImageInfo.exposed_ports`, `ImageInfo.has_healthcheck`, `ComposeServiceSpec.network_mode` |
| Migration `AutomaticDomains` | `servers.wildcard_domain`, `domains.is_automatic`, `workloads.auto_domain_assigned`. The migration sets `auto_domain_assigned` on every existing workload, so an upgrade adds no routes. |
| Settings keys | `proxy` (`acmeEmail`, `staging`) and `dashboard` (`domain`, `https`, `serverId`) |
| API | `GET/PUT /system/proxy-settings`; `wildcardDomain` and `automaticDomainBase` on servers; `isAutomatic` on domains; `autoDomain` on application and service creation |

## Automatic domains (Coolify's wildcard domain)

Every server has a base for automatic domains:
- its `wildcardDomain` (for example `apps.example.com`, with a `*.apps.example.com` DNS record pointing at it);
- otherwise `<public ip with dashes>.sslip.io`. sslip.io is a public DNS service that answers any such name with the IP inside it, so no DNS setup is needed.

The IP comes from `Server.PublicIp`, or from `Host` when that is an IP literal. The local server records the installer's public host as its public IP. No DNS lookup is made. A server with neither a wildcard domain nor a known IP gets no automatic domain, and the deployment log says why.

**Which workloads get one:**
- **New applications** get `<slug>-<6 random hex>.<base>` on creation. The domain is primary, has no target port (the port is settled from the image, below) and uses HTTPS when an ACME email is configured.
- **Services** get one only when they have an HTTP port: Grafana yes, Postgres no.
- **Compose applications** get theirs at the first deployment, once their services are known. It goes to the first service that exposes a usual web port (80, 8080, 3000, ...), or else to one named `web`, `app`, `frontend`, ... When nothing looks like a web server, nothing is guessed.

**Behaviour after creation:**
- The domain is an ordinary row marked `isAutomatic`, so it can be edited or deleted like any other.
- `auto_domain_assigned` makes the assignment happen only once: a domain the user deleted never comes back.
- Renaming an automatic domain makes it the user's own (`isAutomatic` becomes false).
- Changing a server's wildcard domain never rewrites existing domains.
- `autoDomain: false` opts out, for example when the wizard is given a domain of the user's own.

**HTTPS on sslip.io:** Let's Encrypt counts its limits per registered domain, and sslip.io is on the Public Suffix List, so each `<ip>.sslip.io` is counted separately (one bucket per server).

## The proxy is always there

The agent's proxy used to appear only with the first deployment that had a domain. Now the proxy is ensured whenever an agent connects, through `IAgentConnectedHook` → `ProxyAutoEnsurer`, which runs in the background and once per server at a time. It is still also ensured by every deployment with domains.

**Dynamic configuration.** Traefik's static configuration now always contains a file provider watching `/etc/traefik/dynamic`. The agent writes `dynamic_config` there, atomically through a rename.
- The file is not part of the configuration hash, so a change reloads live and never recreates the container.
- The directory is mounted rather than the file, because a bind-mounted file keeps pointing at the old inode after a rename.

## The dashboard domain

The UI's domain used to be static Traefik labels on the `aethera` container in `docker-compose.prod.yml`, which could only change by editing the `.env`. It is now a file-provider route that the control plane gives the proxy of the dashboard server (the `local` server unless `dashboard.serverId` says otherwise):
- routers `aethera-http` and `aethera-https`, in the `@file` namespace;
- an HTTP-to-HTTPS redirect when HTTPS is on;
- the `letsencrypt` resolver when there is an ACME email;
- the upstream `http://aethera-control-plane:8080`, which is the alias of the `aethera` service on `aethera-proxy`.

**Where the values come from:**
- **Settings > System** edits the values through `PUT /system/proxy-settings`. Saving applies them at once and reports the DNS check of the name. Port 8080 keeps answering, so a wrong domain cannot lock anyone out.
- **Until the first save** the values come from the environment: `AETHERA_DOMAIN` (as `Aethera:Proxy:ControlPlaneDomain`) and `AETHERA_ACME_EMAIL`.
- **After that** the saved values win.
- The links in emails and the printed agent install command follow the saved domain, even though `AETHERA_PUBLIC_URL` stays in the `.env`.

## Deployments that work the first time

**Ports.**
- After the Image step the runner inspects the image and rebuilds the plan with what the image says about itself (`DeploymentPlan.WithImage`, `ImageFacts`).
- A workload that declares no port uses, in this order: its image's EXPOSE port (the only one, else the most web-like one); the build engine's default (static: 80, Nixpacks: 3000).
- The port found is stored on the workload.
- A domain with no port anywhere now fails the Domain step with a clear message instead of being dropped silently.
- The wizard no longer defaults to 3000.

**Health.**
- Without a configured check, the new container must stay up for three probes in a row. If its image has a `HEALTHCHECK`, it must also report healthy (up to about three minutes). A crash at start no longer counts as running.
- Configured checks now default to a 30 s start period and 10 probes 5 s apart.
- HTTP probes send the primary domain as `Host`.
- When a health check fails, the container's last 100 lines of output are copied into the deployment log.

**Strategy.**
- New workloads default to low-downtime. The runner still falls back to recreate for a workload with a host port or a writable volume.
- Recreate now stops the old container instead of removing it, starts the new one under its own name and probes it.
- If the new one fails, it is removed and the old container is started again ("The previous version was started again"), and the old deployment stays live.

**Compose.**
- **Persistent bind mounts.** A relative bind mount the repository does not provide (`./data`, or a directory holding only `.gitkeep`) lives in `<project>/persist/<path>`, so it survives the fresh checkout of every deployment. This is done through an agent-generated override. Files the repository does provide still come from the checkout.
- **Inspection** uses its own temporary checkout, so it never touches the running project's.
- **Interpolation.** The repository's `.env` is passed before Aethera's variables, so it is still used and Aethera's values win.
- **One-shot services.** Services that ran and exited 0 (migrations, init jobs) no longer fail `--wait`.
- **Network modes.** Services with `network_mode: service:x` get no `networks` in the override, which compose would refuse. A domain on such a service fails clearly.
- **Ports 80 and 443.** Host ports 80 and 443 in the user's file are refused with a hint to use a domain.

**Builds.**
- Stored `BuildArgs` reach Dockerfile builds. Build variables win on a name conflict.
- Image applications created by the wizard always pull, so a redeploy of `:latest` runs the new image.
- Build paths accept a leading `/` (the repository root, Coolify style) and are stored without it.
- Git submodules are fetched when `.gitmodules` exists. A submodule the server cannot reach is only a warning.

## Known limits

- The dashboard domain is routed by one server's proxy (the local one). A control plane that runs off-cluster needs its own proxy.
- Forwarded headers from Aethera's own proxy are still only trusted when `AETHERA_TRUSTED_PROXIES` names the proxy network. Without that, the session cookie keeps its plain (non-`__Host-`) form behind the domain.
- Build detection (`build.detect`) still reads EXPOSE only from `./Dockerfile`. The deployment itself reads it from the built image, which is what counts.
- Compose services pull private images only with registry credentials configured on the server (`docker login`).
