import { api, type ApiClient } from "@/lib/api/client";
import type {
  CreateServerRequest,
  Discovery,
  DockerContainer,
  DockerImage,
  DockerNetwork,
  DockerVolume,
  Job,
  JoinToken,
  JoinTokenSummary,
  LatestMetrics,
  MetricResolution,
  MetricSeries,
  PruneRequest,
  ResourceEvent,
  Server,
  ServerHealth,
  ServerPage,
  UpdateServerRequest,
} from "./types";

type Signal = { signal?: AbortSignal };

export type ListServersQuery = {
  limit?: number;
  cursor?: string;
  sort?: string;
  lifecycle?: string;
  transport?: string;
  q?: string;
};

export type MetricsQuery = {
  from?: string;
  to?: string;
  resolution?: MetricResolution;
  containerId?: string;
  maxPoints?: number;
};

/** Typed wrapper over `/api/v1/servers/**` and `/api/v1/jobs/{id}`. Paths follow the OpenAPI document. */
export function createServersApi(client: ApiClient) {
  const base = (id: string) => `/servers/${encodeURIComponent(id)}`;
  return {
    list: (q: ListServersQuery = {}, o: Signal = {}) =>
      client.get<ServerPage>("/servers", {
        query: { Limit: q.limit, Cursor: q.cursor, sort: q.sort, lifecycle: q.lifecycle, transport: q.transport, q: q.q },
        signal: o.signal,
      }),
    get: (id: string, o: Signal = {}) => client.get<Server>(base(id), o),
    create: (body: CreateServerRequest) => client.post<Server>("/servers", body),
    update: (id: string, body: UpdateServerRequest) => client.patch<Server>(base(id), body),
    /** Needs the exact server name as `?confirm=` (428 otherwise). */
    remove: (id: string, confirm: string) => client.delete<void>(base(id), { query: { confirm } }),

    createJoinToken: (id: string, ttlMinutes?: number) =>
      client.post<JoinToken>(`${base(id)}/join-tokens`, ttlMinutes ? { ttlMinutes } : {}),
    listJoinTokens: (id: string, o: Signal = {}) => client.get<JoinTokenSummary[]>(`${base(id)}/join-tokens`, o),
    revokeJoinToken: (id: string, tokenId: string) =>
      client.delete<void>(`${base(id)}/join-tokens/${encodeURIComponent(tokenId)}`),
    resetAgent: (id: string, confirm: string) => client.post<void>(`${base(id)}/agent/reset`, undefined, { query: { confirm } }),

    status: (id: string, o: Signal = {}) => client.get<ServerHealth>(`${base(id)}/status`, o),
    events: (id: string, limit = 50, o: Signal = {}) =>
      client.get<ResourceEvent[]>(`${base(id)}/events`, { query: { limit }, signal: o.signal }),
    latestMetrics: (id: string, o: Signal = {}) => client.get<LatestMetrics>(`${base(id)}/metrics/latest`, o),
    metrics: (id: string, q: MetricsQuery = {}, o: Signal = {}) =>
      client.get<MetricSeries>(`${base(id)}/metrics`, { query: { ...q }, signal: o.signal }),
    discovery: (id: string, o: Signal = {}) => client.get<Discovery>(`${base(id)}/discovery`, o),

    containers: (id: string, all = true, o: Signal = {}) =>
      client.get<DockerContainer[]>(`${base(id)}/docker/containers`, { query: { all }, signal: o.signal }),
    images: (id: string, o: Signal = {}) => client.get<DockerImage[]>(`${base(id)}/docker/images`, o),
    volumes: (id: string, o: Signal = {}) => client.get<DockerVolume[]>(`${base(id)}/docker/volumes`, o),
    networks: (id: string, o: Signal = {}) => client.get<DockerNetwork[]>(`${base(id)}/docker/networks`, o),

    /** `confirm` (the server name) is required by the API when `body.volumes` is true. */
    prune: (id: string, body: PruneRequest, confirm?: string) =>
      client.post<Job>(`${base(id)}/maintenance/prune`, body, { query: { confirm } }),
    refreshDiscovery: (id: string) => client.post<Job>(`${base(id)}/maintenance/refresh-discovery`),

    job: (jobId: string, o: Signal = {}) => client.get<Job>(`/jobs/${encodeURIComponent(jobId)}`, o),
  };
}

export type ServersApi = ReturnType<typeof createServersApi>;

export const serversApi: ServersApi = createServersApi(api);
