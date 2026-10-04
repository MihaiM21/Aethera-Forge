import { api, type ApiClient } from "@/lib/api/client";
import type {
  Application,
  ApplicationSummary,
  ApiToken,
  AuditEvent,
  BuildDetectResponse,
  CreateApplicationRequest,
  CreateDomainRequest,
  CreateEnvVarRequest,
  CreateProjectRequest,
  CreateRegistryRequest,
  CreateSecretRequest,
  CreateServiceRequest,
  CreateUserRequest,
  CreateVolumeRequest,
  CreatedApiToken,
  Deployment,
  DeploymentListItem,
  DeploymentLogPage,
  Domain,
  EnvVar,
  Environment,
  GitCredential,
  Job,
  OrganizationRole,
  Page,
  Project,
  ProjectFromTemplate,
  ProjectTemplate,
  Registry,
  RuntimeLogs,
  Secret,
  Service,
  ServiceSummary,
  ServiceTemplate,
  UpdateApplicationRequest,
  UpdateServiceRequest,
  User,
  Volume,
  Webhook,
  WebhookDelivery,
  WebhookSetupRequest,
} from "./types";

type Signal = { signal?: AbortSignal };

export type ListQuery = { limit?: number; cursor?: string; sort?: string; q?: string };
export type ApplicationsQuery = ListQuery & { projectId?: string; environmentId?: string; serverId?: string; status?: string; sourceKind?: string };
export type ServicesQuery = ListQuery & { projectId?: string; environmentId?: string; serverId?: string; status?: string; templateKey?: string };
export type DeploymentsQuery = ListQuery & { status?: string; applicationId?: string; serverId?: string; trigger?: string };
export type DomainsQuery = ListQuery & { workloadId?: string; serverId?: string; dnsStatus?: string };
export type SecretsQuery = ListQuery & { projectId?: string; environmentId?: string; workloadId?: string; scope?: string };
export type AuditQuery = { limit?: number; cursor?: string; action?: string; resourceType?: string; resourceId?: string; actorType?: string; from?: string; to?: string };

const enc = encodeURIComponent;
const paged = (q: ListQuery) => ({ Limit: q.limit, Cursor: q.cursor, sort: q.sort, q: q.q });

/**
 * Typed wrapper over the project, application, deployment, service and ops endpoints. Paths follow the OpenAPI
 * document; the UI has no behaviour the API lacks.
 */
export function createResourcesApi(client: ApiClient) {
  const workload = (kind: "applications" | "services", id: string) => `/${kind}/${enc(id)}`;
  return {
    projects: {
      list: (q: ListQuery = {}, o: Signal = {}) => client.get<Page<Project>>("/projects", { query: paged(q), signal: o.signal }),
      get: (id: string, o: Signal = {}) => client.get<Project>(`/projects/${enc(id)}`, o),
      create: (body: CreateProjectRequest) => client.post<Project>("/projects", body),
      update: (id: string, body: { name?: string; description?: string | null }) => client.patch<Project>(`/projects/${enc(id)}`, body),
      remove: (id: string, confirm: string, cascade = false) => client.delete<void>(`/projects/${enc(id)}`, { query: { confirm, cascade: cascade || undefined } }),
      templates: (o: Signal = {}) => client.get<ProjectTemplate[]>("/project-templates", o),
      fromTemplate: (body: { templateKey: string; name: string; slug?: string; description?: string; serverId?: string }) =>
        client.post<ProjectFromTemplate>("/projects/from-template", body),
      environments: (id: string, o: Signal = {}) => client.get<Page<Environment>>(`/projects/${enc(id)}/environments`, o),
      createEnvironment: (id: string, body: { name: string; slug?: string; isProduction?: boolean }) =>
        client.post<Environment>(`/projects/${enc(id)}/environments`, body),
    },

    applications: {
      list: (q: ApplicationsQuery = {}, o: Signal = {}) =>
        client.get<Page<ApplicationSummary>>("/applications", {
          query: { ...paged(q), projectId: q.projectId, environmentId: q.environmentId, serverId: q.serverId, status: q.status, sourceKind: q.sourceKind },
          signal: o.signal,
        }),
      get: (id: string, o: Signal = {}) => client.get<Application>(workload("applications", id), o),
      create: (body: CreateApplicationRequest) => client.post<Application>("/applications", body),
      update: (id: string, body: UpdateApplicationRequest) => client.patch<Application>(workload("applications", id), body),
      remove: (id: string, confirm: string) => client.delete<void>(workload("applications", id), { query: { confirm } }),
      deploy: (id: string) => client.post<Deployment>(`${workload("applications", id)}/deployments`, {}),
      redeploy: (id: string) => client.post<Deployment>(`${workload("applications", id)}/redeploy`),
      lifecycle: (id: string, action: "start" | "stop" | "restart") => client.post<Job>(`${workload("applications", id)}/${action}`),
      deployments: (id: string, q: { limit?: number; cursor?: string; status?: string } = {}, o: Signal = {}) =>
        client.get<Page<Deployment>>(`${workload("applications", id)}/deployments`, {
          query: { Limit: q.limit, Cursor: q.cursor, status: q.status },
          signal: o.signal,
        }),
      logs: (id: string, q: { tail?: number; since?: string } = {}, o: Signal = {}) =>
        client.get<RuntimeLogs>(`${workload("applications", id)}/logs`, { query: { tail: q.tail, since: q.since }, signal: o.signal }),
      webhook: (id: string, o: Signal = {}) => client.get<Webhook>(`${workload("applications", id)}/webhook`, o),
      setWebhook: (id: string, body: WebhookSetupRequest) => client.put<Webhook>(`${workload("applications", id)}/webhook`, body),
      removeWebhook: (id: string) => client.delete<void>(`${workload("applications", id)}/webhook`),
      webhookDeliveries: (id: string, o: Signal = {}) =>
        client.get<Page<WebhookDelivery>>(`${workload("applications", id)}/webhook/deliveries`, { query: { Limit: 20 }, signal: o.signal }),
    },

    deployments: {
      list: (q: DeploymentsQuery = {}, o: Signal = {}) =>
        client.get<Page<DeploymentListItem>>("/deployments", {
          query: { Limit: q.limit, Cursor: q.cursor, sort: q.sort, status: q.status, applicationId: q.applicationId, serverId: q.serverId, trigger: q.trigger },
          signal: o.signal,
        }),
      get: (id: string, o: Signal = {}) => client.get<Deployment>(`/deployments/${enc(id)}`, o),
      rollback: (id: string) => client.post<Deployment>(`/deployments/${enc(id)}/rollback`),
      logs: (id: string, q: { source?: "build" | "deploy"; fromSequence?: number; limit?: number } = {}, o: Signal = {}) =>
        client.get<DeploymentLogPage>(`/deployments/${enc(id)}/logs`, { query: { source: q.source, fromSequence: q.fromSequence, limit: q.limit }, signal: o.signal }),
      /** Same-origin URL of the whole log as a text download. */
      logDownloadUrl: (id: string, source: "build" | "deploy") => `/api/v1/deployments/${enc(id)}/logs?download=true&source=${source}`,
    },

    envVars: {
      list: (kind: "applications" | "services", id: string, o: Signal = {}) =>
        client.get<Page<EnvVar>>(`${workload(kind, id)}/env-vars`, { query: { Limit: 200, sort: "key" }, signal: o.signal }),
      create: (kind: "applications" | "services", id: string, body: CreateEnvVarRequest) => client.post<EnvVar>(`${workload(kind, id)}/env-vars`, body),
      update: (kind: "applications" | "services", id: string, envId: string, body: Partial<CreateEnvVarRequest>) =>
        client.patch<EnvVar>(`${workload(kind, id)}/env-vars/${enc(envId)}`, body),
      remove: (kind: "applications" | "services", id: string, envId: string) => client.delete<void>(`${workload(kind, id)}/env-vars/${enc(envId)}`),
      importDotenv: (kind: "applications" | "services", id: string, content: string) =>
        client.post<{ created: number; updated: number }>(`${workload(kind, id)}/env-vars/import`, { content }),
    },

    volumes: {
      list: (kind: "applications" | "services", id: string, o: Signal = {}) =>
        client.get<Page<Volume>>(`${workload(kind, id)}/volumes`, { query: { Limit: 100 }, signal: o.signal }),
      create: (kind: "applications" | "services", id: string, body: CreateVolumeRequest) => client.post<Volume>(`${workload(kind, id)}/volumes`, body),
      remove: (id: string) => client.delete<void>(`/volumes/${enc(id)}`),
    },

    domains: {
      list: (q: DomainsQuery = {}, o: Signal = {}) =>
        client.get<Page<Domain>>("/domains", {
          query: { ...paged(q), workloadId: q.workloadId, serverId: q.serverId, dnsStatus: q.dnsStatus },
          signal: o.signal,
        }),
      create: (kind: "applications" | "services", id: string, body: CreateDomainRequest) => client.post<Domain>(`${workload(kind, id)}/domains`, body),
      update: (id: string, body: Partial<CreateDomainRequest>) => client.patch<Domain>(`/domains/${enc(id)}`, body),
      remove: (id: string) => client.delete<void>(`/domains/${enc(id)}`),
      verifyDns: (id: string) => client.post<Domain>(`/domains/${enc(id)}/verify-dns`),
    },

    services: {
      templates: (o: Signal = {}) => client.get<ServiceTemplate[]>("/service-templates", o),
      list: (q: ServicesQuery = {}, o: Signal = {}) =>
        client.get<Page<ServiceSummary>>("/services", {
          query: { ...paged(q), projectId: q.projectId, environmentId: q.environmentId, serverId: q.serverId, status: q.status, templateKey: q.templateKey },
          signal: o.signal,
        }),
      get: (id: string, o: Signal = {}) => client.get<Service>(workload("services", id), o),
      create: (body: CreateServiceRequest) => client.post<Service>("/services", body),
      update: (id: string, body: UpdateServiceRequest) => client.patch<Service>(workload("services", id), body),
      remove: (id: string, confirm: string) => client.delete<void>(workload("services", id), { query: { confirm } }),
      deploy: (id: string) => client.post<Deployment>(`${workload("services", id)}/deployments`, {}),
      lifecycle: (id: string, action: "start" | "stop" | "restart") => client.post<Job>(`${workload("services", id)}/${action}`),
      deployments: (id: string, o: Signal = {}) =>
        client.get<Page<Deployment>>(`${workload("services", id)}/deployments`, { query: { Limit: 30 }, signal: o.signal }),
      logs: (id: string, q: { tail?: number; since?: string } = {}, o: Signal = {}) =>
        client.get<RuntimeLogs>(`${workload("services", id)}/logs`, { query: { tail: q.tail, since: q.since }, signal: o.signal }),
    },

    secrets: {
      list: (q: SecretsQuery = {}, o: Signal = {}) =>
        client.get<Page<Secret>>("/secrets", {
          query: { ...paged(q), projectId: q.projectId, environmentId: q.environmentId, workloadId: q.workloadId, scope: q.scope },
          signal: o.signal,
        }),
      create: (body: CreateSecretRequest) => client.post<Secret>("/secrets", body),
      update: (id: string, body: { name?: string; description?: string | null }) => client.patch<Secret>(`/secrets/${enc(id)}`, body),
      rotate: (id: string, value: string) => client.post<Secret>(`/secrets/${enc(id)}/rotate`, { value }),
      reveal: (id: string) => client.post<{ id: string; name: string; version: number | string | null; value: string }>(`/secrets/${enc(id)}/reveal`),
      remove: (id: string) => client.delete<void>(`/secrets/${enc(id)}`),
    },

    registries: {
      list: (q: ListQuery = {}, o: Signal = {}) => client.get<Page<Registry>>("/registries", { query: paged(q), signal: o.signal }),
      create: (body: CreateRegistryRequest) => client.post<Registry>("/registries", body),
      update: (id: string, body: Partial<CreateRegistryRequest>) => client.patch<Registry>(`/registries/${enc(id)}`, body),
      remove: (id: string) => client.delete<void>(`/registries/${enc(id)}`),
    },

    gitCredentials: {
      list: (o: Signal = {}) => client.get<Page<GitCredential>>("/git-credentials", o),
      create: (body: { name: string; kind: "token" | "deployKey" | "basicAuth"; provider?: string; username?: string; value: string; publicKey?: string }) =>
        client.post<GitCredential>("/git-credentials", body),
      /** Needs the credential name as `?confirm=`; 409 `git_credential.in_use` while an application uses it. */
      remove: (id: string, confirm: string) => client.delete<void>(`/git-credentials/${enc(id)}`, { query: { confirm } }),
    },

    account: {
      changePassword: (currentPassword: string, newPassword: string) => client.post<void>("/auth/password", { currentPassword, newPassword }),
    },

    users: {
      list: (o: Signal = {}) => client.get<Page<User>>("/users", { query: { limit: 100 }, signal: o.signal }),
      create: (body: CreateUserRequest) => client.post<User>("/users", body),
      setRole: (id: string, role: OrganizationRole) => client.put<User>(`/users/${enc(id)}/role`, { role }),
      update: (id: string, body: { displayName?: string; isActive?: boolean }) => client.patch<User>(`/users/${enc(id)}`, body),
      remove: (id: string) => client.delete<void>(`/users/${enc(id)}`),
    },

    tokens: {
      list: (o: Signal = {}) => client.get<Page<ApiToken>>("/api-tokens", { query: { limit: 100 }, signal: o.signal }),
      create: (body: { name: string; scopes?: string[]; expiresAt?: string }) => client.post<CreatedApiToken>("/api-tokens", body),
      revoke: (id: string) => client.delete<void>(`/api-tokens/${enc(id)}`),
    },

    audit: {
      list: (q: AuditQuery = {}, o: Signal = {}) =>
        client.get<Page<AuditEvent>>("/audit-log", {
          query: { Limit: q.limit, Cursor: q.cursor, action: q.action, resourceType: q.resourceType, resourceId: q.resourceId, actorType: q.actorType, from: q.from, to: q.to },
          signal: o.signal,
        }),
    },

    jobs: {
      list: (q: { limit?: number; status?: string; type?: string } = {}, o: Signal = {}) =>
        client.get<Page<Job>>("/jobs", { query: { Limit: q.limit, status: q.status, type: q.type }, signal: o.signal }),
      cancel: (id: string) => client.post<Job>(`/jobs/${enc(id)}/cancel`),
    },

    servers: {
      buildDetect: (serverId: string, body: { repositoryUrl: string; branch?: string; contextPath?: string; gitCredentialId?: string }) =>
        client.post<BuildDetectResponse>(`/servers/${enc(serverId)}/build-detect`, body),
    },
  };
}

export type ResourcesApi = ReturnType<typeof createResourcesApi>;

export const resourcesApi: ResourcesApi = createResourcesApi(api);
