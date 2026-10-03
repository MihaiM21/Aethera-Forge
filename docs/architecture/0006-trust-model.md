# ADR 0006: Trust model

- Status: accepted
- Date: 2026-10-03
- Scope: who may configure what on a server, how credentials owned by other resources are protected, and where each rule is enforced (API now, agent and engine in Phase 3)
- Spec: sections 14, 29, 30 of `docs/idea/01_AppIdea.md`
- Related: [ADR 0002](./0002-agent-communication.md) (agent runs only typed, allowlisted commands), [ADR 0003](./0003-api-conventions.md) (roles, scopes, error format), [ADR 0004](./0004-jobs-and-deployments.md) (configuration snapshot)
- Origin: security review of the Resource API (WP1.2) and the repository owner's decision "restrict risky bits to Admin"

## Context

Aethera runs containers for other people on servers the operator owns. Two facts shape the model:

1. **Whoever controls a container's configuration can often control the host.** A bind mount of `/`, the Docker socket, `privileged: true`, `network_mode: host` or an added capability each give root on the server. A deployment platform cannot forbid all of them (operators legitimately need some), but it must not hand them to every person who may "deploy an app".
2. **Credentials that Aethera stores for itself are ordinary rows.** Registry passwords, server SSH keys and git tokens are stored as organization-scoped secrets. Before this ADR a Developer (role Developer, token scope `secrets:write`) could rotate or delete them through `/secrets`, bypassing the Admin-only endpoints and their audit trail, and could bind any organization secret into an application's environment where the deployed program can print it, although `reveal` is Admin-only.

## Decision summary

1. **Developers deploy applications normally. Anything root-equivalent on a server needs the Administrator role** (Admin or Owner). The capability table below lists each one, its role, and where it is enforced.
2. **Secrets have a purpose and organization-wide ones belong to Admins.** Secrets that belong to another resource are *managed*: visible in `/secrets`, changed only through their owner, never bindable to an environment variable (one narrow exception for generated service passwords).
3. **Binding a secret is a privilege decision.** A Developer binds user secrets scoped to the workload, its environment or its project. An organization-wide secret needs an Administrator.
4. **The API checks now; the agent and engine check again later.** Every rule marked "re-check" in the table is validation on untrusted input in Phase 3 as well: the engine and agent must not assume that the API (or an older version of it) accepted the configuration they receive.

## 1. Roles

The roles are those of ADR 0003 (Viewer < Developer < Admin < Owner). "Admin" in the table below means *Administrator or Owner*. API tokens act with their owner's role, narrowed by scopes (effective permission = scope AND role). A browser session is bound by role only.

## 2. Capability table

| Capability | Role | Enforced now (API) | Error | Later (Phase 3 agent / engine) |
|---|---|---|---|---|
| Create and configure applications and services, plain env vars, domains, deploy | Developer | roles and scopes (ADR 0003) | `auth.forbidden`, `auth.insufficient_scope` | The deploy job runs with the actor's role from the configuration snapshot; webhook and schedule triggers use the Developer rules unless an Admin approved the configuration. |
| Bind a `user` secret scoped to the workload, environment or project to an env var | Developer | `RequireSecretForWorkloadAsync`: scope match | `validation.failed` / `scope_mismatch` | At job start, resolve every secret reference of the snapshot again: same organization, same scope rule, purpose `user` (or `serviceGenerated` of this service). |
| Bind an organization-scoped `user` secret to an env var | **Admin** | same | **403 `secret.binding_forbidden`** | same |
| Bind a managed secret (`registryCredential`, `sshCredential`, `gitCredential`, other service's `serviceGenerated`) | nobody | same | **403 `secret.binding_forbidden`** | same; the engine reads managed secrets only for their purpose (registry login, SSH, clone). |
| Bind any secret with an API token | role above **and** scope `secrets:write` | `ManagedSecrets.RequireTokenScope` | `auth.insufficient_scope` (`requiredScope: secrets:write`) | n/a |
| Create, PATCH, rotate or delete an **organization-scoped** secret | **Admin** | `ManagedSecrets.RequireAdminForOrganizationScope` | **403 `secret.org_scope_requires_admin`** | n/a |
| Create, PATCH, rotate or delete a secret scoped to a project, environment or workload | Developer (+ `secrets:write` for tokens) | endpoint policies | `auth.forbidden`, `auth.insufficient_scope` | n/a |
| PATCH, rotate or delete a managed secret through `/secrets` | nobody | `ManagedSecrets.EnsureUserSecretAsync` | **409 `secret.managed`** (the message names the owning endpoint) | n/a |
| Create, update, delete registries (they own a password secret) | Admin; token needs `write` and `secrets:write` | endpoint policies | `auth.forbidden`, `auth.insufficient_scope` | Credentials are sent only to the registry host they were created for (see 5). |
| Create, update git credentials (not implemented yet) | Admin; token needs `write` and `secrets:write` | **to be applied by the endpoints that add them** | same | Credentials are sent only to the git host they were created for (see 5). |
| Set a server's SSH credential, create/change servers | Admin (`servers:write`) | endpoint policies, `ClaimForServerAsync` | `auth.forbidden`, `secret.managed`, `secret.in_use`, `scope_mismatch` | The agent installer is the only user of the key. |
| **Volume `hostPath` (bind mount of a host directory)** | **Admin** | `TrustChecks.CheckHostPath` | **403 `volume.host_path_requires_admin`** | Re-check after resolving symlinks (`realpath`) with the same denylist and allowlist; the agent refuses a bind source that is not below an allowlisted prefix or a Docker-managed volume. |
| Host path under `/var/run`, `/run`, `/proc`, `/sys`, `/dev`, `/etc`, `/boot`, `/root`, `/var/lib/aethera` (and `/var/lib/docker`, `/var/lib/containerd`), or `/` itself, even for Admin | nobody (allowlist prefix excepted) | `TrustPolicy.HostPathDenial` | **422 `volume.host_path_forbidden`** | same |
| **Published host port below 1024** | **Admin** | `TrustChecks.CheckPorts` | **403 `port.privileged_requires_admin`** | The engine checks collisions on the server (ports in use, ports of the proxy and the agent). |
| Published host port reserved by Aethera (default 22, 80, 443, 2375, 2376, 5080, 9443) | nobody, unless Admin passes `allowReserved: true` on the port | same | **422 `port.reserved`** | same |
| **Inline compose: root-equivalent options** (see below) | **Admin** | `ComposeInspector` + `TrustChecks.CheckCompose` | **403 `compose.option_requires_admin`** with `pointers` | Parse the *effective* configuration (after interpolation, `extends`, `include` and override files) with the same rules before `docker compose up`; reject what the actor's role may not use. **Compose files read from a git repository are not seen by the API at all**, so this re-check is where they are covered. |
| Inline compose: bind sources on the host-path denylist, even for Admin | nobody | same | **422 `volume.host_path_forbidden`** with `pointers` | same, on the resolved paths |
| Inline compose that is not valid YAML, too large (256 KiB), too many aliases (50), too deep (32) or too expensive to read | nobody | `ComposeInspector` | **422 `compose.invalid`** | same limits |
| Build inputs: `build.context`, `dockerfilePath`, `outputDirectory`, `compose.filePath` | Developer, if relative and inside the repository | `BuildInputRules.IsSafeRelativePath` | `validation.failed` / `pattern` | Resolve against the checkout and verify the real path stays inside; pass paths after `--`. |
| Git `branch`, `commitPin`, refs; `repositoryUrl` | Developer, if not an option or a dangerous transport | `BuildInputRules.IsSafeGitRef`, `IsSafeRepositoryUrl` | `validation.failed` / `pattern` | Pass refs and URLs after `--`; run git with `GIT_ALLOW_PROTOCOL=http:https:ssh:git`, `protocol.ext.allow=never`, `protocol.file.allow=never`. |
| Which hosts the clone host may reach (SSRF: cloud metadata, the control plane, internal networks) | engine policy | **not in the API** | n/a | **Phase 3 engine concern**: the clone runs in a sandbox with an egress policy (deny link-local, loopback and the control plane's addresses by default; allow-list for internal git servers). A syntactically valid URL is not a safe URL. |
| Domain `pathPrefix` | Developer, `\A/[A-Za-z0-9._~/-]*\z`, at most 256, no `//`, `.` or `..` segment | `DomainRules.TryNormalizePath` | `validation.failed` / `pattern` | Generated Traefik rules escape or reject anything else anyway. |
| Reveal a secret's value | Admin (+ `secrets:write` for tokens) | ADR 0003 | `auth.forbidden` | n/a |

### Compose options a Developer cannot use

For every service (merge keys `<<: *anchor` are followed, so an anchor cannot hide an option):

| Key | Rejected when |
|---|---|
| `privileged` | not `false` |
| `network_mode`, `pid`, `ipc`, `userns_mode`, `uts`, `cgroup` | `host`, `container:...`, or a variable |
| `cap_add`, `devices`, `device_cgroup_rules` | not empty |
| `security_opt` | any entry mentioning `unconfined` or `disable` |
| `cgroup_parent` | present |
| `volumes` (short and long syntax) | any bind source: anything that is not a bare volume name, i.e. absolute paths, `./relative`, `../`, `~`, `${VARIABLE}`; `type: bind` |
| `volumes_from` | `container:...` |
| `ports` | a published host port below 1024 or reserved, or set by a variable |
| `env_file` | present (reads files of the host) |
| `extends` | with a `file` |
| `build` | a context or Dockerfile outside the project (absolute, `..`, `~`, variable) |

Top level: `include`; `volumes.<name>.driver_opts` (a "named" volume can be a bind mount: `type: none, o: bind, device: /etc`); `secrets.<name>.file` and `configs.<name>.file` (they read host files). The first group is the owner's list (`privileged`, `network_mode: host`, `pid: host`, `ipc: host`, `userns_mode: host`, `cap_add`, `devices`, `security_opt: unconfined`, `cgroup_parent`, host bind mounts); the rest are the same class of escape found while implementing it and are covered by the same code.

The inspection reads the document into a **plain YAML node tree** (YamlDotNet `YamlStream`). It never deserializes into types, never constructs objects from tags, and bounds the work before building anything: 256 KiB, 50 aliases, nesting 32, 50 000 parser events, and a visit budget of 100 000 for every traversal (so exponential merge-key chains fail instead of burning CPU). An Admin's bind sources still go through the host-path denylist; relative sources are accepted if they have no `..` (the engine resolves them inside the project directory).

A service's `config` (stored as given) is inspected the same way if it carries inline compose under the reserved key `compose`.

## 3. Managed secrets

`secrets.purpose` (migration `AddSecretPurpose`; enum stored as a camelCase string, check constraint `ck_secrets_purpose`):

| Purpose | Created by | Owner (shown as `managedBy`) | Bindable to env vars |
|---|---|---|---|
| `user` (default) | `POST /secrets` | none | by scope: workload, environment, project for Developers; organization for Admins |
| `registryCredential` | registry endpoints (`registry/<id>`) | the registry | never |
| `sshCredential` | an Admin attaches an organization-scoped user secret to a server | the (first) server using it | never |
| `gitCredential` | git credential endpoints (later) | the git credential | never |
| `serviceGenerated` | service templates (generated passwords) | the service | only to the service it was generated for |

- `GET /secrets` and `GET /secrets/{id}` return `purpose`, `managed` and `managedBy {type, id, name}` (`registry`, `server`, `gitCredential`, `service`).
- `PATCH`, `rotate` and `DELETE` on a managed secret answer **409 `secret.managed`**; the message names the endpoint that owns it (`PATCH /registries/{id}`). `reveal` is unchanged (Admin, audited).
- Attaching a secret to a server as its SSH credential (`sshCredentialSecretId`) turns a `user` secret into an `sshCredential`. It must be organization-scoped and not bound to any environment variable (otherwise 422 `secret.in_use` / `scope_mismatch`); a secret managed by something else is refused (422 `secret.managed`). When no server uses it any more (cleared, replaced, server deleted) it becomes a `user` secret again.
- **Backfill** (migration): registry passwords (linked from `registries.password_secret_id`, or organization-scoped and named `registry/%`), SSH credentials (`servers.ssh_credential_secret_id`), git credentials (`git_credentials.secret_id`) and service passwords (service-scoped, described "Generated for service ...", used by an env var of that service). Everything else stays `user`.
- Pre-existing env var bindings are not touched: a Developer who bound an organization-wide secret before this change keeps the binding until someone removes it. Review `GET /secrets` and the env vars of important applications after upgrading.

## 4. Configuration

| Setting (`Aethera:Trust:*`, environment `Aethera__Trust__*`) | Default | Meaning |
|---|---|---|
| `HostPathAllowlist` (array) | `/var/lib/aethera/volumes/` | Prefixes an Admin may bind from although they sit below a denied directory. Replaces the default when set. An entry that is `/` or contains a denied directory (`/var`, `/var/lib`, `/etc`) stops the application from starting. |
| `ReservedPorts` (array) | `22, 80, 443, 2375, 2376, 5080, 9443` | Host ports only an Admin with `allowReserved` may publish. Replaces the default when set. The privileged limit (1024) is fixed. |

## 5. Phase 3 and later

Things the API cannot decide, recorded so the engine and agent work packages pick them up:

1. **Re-check, do not trust.** The agent and engine validate host paths (after `realpath`), ports, compose options (effective configuration) and build inputs themselves. The deploy job's configuration snapshot (ADR 0004) records the role of the triggering actor so the engine applies the matching rules.
2. **Repository-hosted compose files, `extends.file`, `include`, `env_file`** are only visible after checkout. The same inspector (`ComposeInspector`) is the reference implementation; run it on the checked-out files with the actor's role.
3. **Clone-host SSRF.** `repositoryUrl` may name an internal address (`http://169.254.169.254/`, the control plane, other containers). Policy for which hosts the clone host may reach is an engine concern (sandbox with egress rules); the API only guarantees the URL cannot inject git or ssh options and does not use `file:`, `ext::` or other transport helpers.
4. **Credentials follow hosts.** A git credential or registry credential must only be sent to the host it was created for. Today an application chooses `gitCredentialId` / `registryId` and a repository URL / image reference independently, so a Developer could point an Admin's credential at a server they control. The engine must bind credentials to hosts (or the API must refuse the combination when git credential endpoints arrive).
5. **Traefik labels.** Compose `labels` such as `traefik.http.routers.*.rule` could route other hosts' traffic to a container. The engine strips or regenerates `traefik.*` labels.
6. **Webhook secrets** (`webhook_endpoints.secret_id`) need a purpose of their own (`webhookSecret`) when webhook endpoints are implemented; until then no endpoint creates one.
7. **Git credential endpoints** must create their secret with purpose `gitCredential`, require Admin plus `write` and `secrets:write`, and follow rule 4.
8. **Docker socket proxies.** If the agent ever exposes Docker to workloads, it does so through a filtered proxy; no rule above is a substitute for that.

## Alternatives considered

| Option | Decision |
|---|---|
| Developers may use any compose option; rely on the operator to review | Rejected: the first Developer account would own every server. |
| Only Admins may create compose applications | Rejected: too coarse. Most compose files are ordinary, and inspecting them lets Developers keep the common case. |
| Typed deserialization of compose into a model | Rejected: a model silently drops unknown keys (including new Docker options) and can construct objects from tags. The node tree sees every key. |
| Separate tables for registry, SSH and git secrets | Rejected: one secret store with one envelope encryption and one reveal path is simpler; a `purpose` column plus ownership links gives the same guarantees. |
| Forbid binding organization secrets for everybody | Rejected: shared values (a registry-independent API key) are a normal use; an Admin deciding to bind one is exactly the control wanted. |

## Consequences

- Admin and Owner can still do dangerous things (privileged containers, bind mounts outside the denylist). The model limits who, not what, and keeps the worst targets (Docker socket, `/etc`, Aethera's own state) out of reach for everyone.
- Organization-scoped *user* secrets are written (create, change, rotate, delete) by Administrators only, so a Developer cannot swap the value behind an application an Admin wired up. Developers keep full control of secrets scoped to a project, environment or workload. Managed secrets are checked first: a Developer who rotates a registry password gets `secret.managed`, not `secret.org_scope_requires_admin`.
- Existing data keeps its old bindings (section 3). Existing tests that relied on Developers binding organization secrets, creating host paths, or writing registries with a plain `write` token were changed on purpose.
- Compose, port and host-path rules add error codes (`volume.host_path_requires_admin`, `volume.host_path_forbidden`, `port.privileged_requires_admin`, `port.reserved`, `compose.option_requires_admin`, `compose.invalid`, `secret.managed`, `secret.binding_forbidden`) that are part of the API contract from now on.
