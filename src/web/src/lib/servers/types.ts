/**
 * Types of the server / agent endpoints, taken from the generated OpenAPI
 * schema (`pnpm gen:api` -> src/lib/api/schema.d.ts). Nothing here is invented:
 * if the API lacks a field, the UI does not show it.
 *
 * Generated numbers are typed `number | string` and date-times `unknown`; use
 * `toNum` / `toDate` from ./format when reading them.
 */
import type { components } from "@/lib/api/schema";

type S = components["schemas"];

export type Server = S["ServerResponse"];
export type ServerPage = S["PageOfServerResponse"];
export type ServerRole = S["ServerRole"];
export type ServerTransport = S["ServerTransport"];
export type ServerLifecycle = S["ServerLifecycle"];
export type CreateServerRequest = S["CreateServerRequest"];
export type UpdateServerRequest = S["UpdateServerRequest"];

export type ServerHealth = S["ServerHealthResponse"];
export type Axis = S["AxisResponse"];
export type AxisHealth = S["AxisHealth"];
export type AgentSession = S["AgentSessionResponse"];

export type JoinToken = S["JoinTokenResponse"];
export type JoinTokenSummary = S["JoinTokenSummary"];

export type MetricPoint = S["MetricPointResponse"];
export type MetricSeries = S["MetricSeriesResponse"];
export type LatestMetrics = S["LatestMetricsResponse"];
export type ContainerMetric = S["ContainerMetricResponse"];

export type Discovery = S["DiscoveryResponse"];
export type DiscoveryInfo = S["DiscoveryInfo"];

export type DockerContainer = S["DockerContainer"];
export type DockerImage = S["DockerImage"];
export type DockerVolume = S["DockerVolume"];
export type DockerNetwork = S["DockerNetwork"];

export type ResourceEvent = S["ResourceEventResponse"];
export type PruneRequest = S["PruneRequest"];
export type Job = S["JobDto"];
export type JobStatus = S["JobStatus"];

export const SERVER_ROLES: readonly ServerRole[] = ["master", "build", "storage", "ci", "worker"];

/** Metric series resolutions accepted by `GET /servers/{id}/metrics`. */
export type MetricResolution = "auto" | "raw" | "fiveMinutes" | "oneHour";
