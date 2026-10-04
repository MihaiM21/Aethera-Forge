import { vi } from "vitest";
import { ApiError } from "@/lib/api/errors";
import type { ServersApi } from "@/lib/servers/api";
import type { Axis, Job, JoinToken, Server, ServerHealth } from "@/lib/servers/types";

const NOW = "2026-10-04T10:00:00.000Z";

export function makeServer(over: Partial<Server> = {}): Server {
  return {
    id: "0192f3c8-aaaa-4bbb-8ccc-111122223333",
    name: "edge-1",
    host: "203.0.113.10",
    sshPort: 22,
    sshUser: null,
    transport: "agent",
    roles: ["worker"],
    lifecycle: "active",
    publicIp: null,
    maxConcurrentBuilds: 2,
    sshCredentialSecretId: null,
    resources: { cpuCores: 4, memoryBytes: 8 * 1024 ** 3, diskBytes: 100 * 1024 ** 3, os: "linux", architecture: "amd64" },
    status: {
      reachability: { status: "reachable", checkedAt: NOW, changedAt: NOW, probePort: 22 },
      agent: { status: "connected", lastHeartbeatAt: NOW, changedAt: NOW },
      docker: { status: "running", changedAt: NOW },
    },
    agentVersion: "0.3.1",
    workloadCount: 2,
    createdAt: NOW,
    updatedAt: NOW,
    ...over,
  } as Server;
}

export function axis(name: string, health: Axis["health"], over: Partial<Axis> = {}): Axis {
  return { axis: name, health, blockedBy: null, since: NOW, stale: false, detail: null, ...over } as Axis;
}

export function makeHealth(over: Partial<ServerHealth> = {}): ServerHealth {
  return {
    serverId: "0192f3c8-aaaa-4bbb-8ccc-111122223333",
    server: axis("server", "available"),
    agent: axis("agent", "available"),
    docker: axis("docker", "available"),
    application: axis("application", "available"),
    firstFailingLayer: null,
    applications: { total: 2, unavailable: 0, healthy: 2, unknown: 0 },
    session: null,
    observedAt: NOW,
    ...over,
  } as ServerHealth;
}

export function makeToken(over: Partial<JoinToken> = {}): JoinToken {
  return {
    id: "tok-1",
    serverId: "0192f3c8-aaaa-4bbb-8ccc-111122223333",
    token: "ajt_SECRET_TOKEN_VALUE",
    expiresAt: new Date(Date.now() + 60 * 60_000).toISOString(),
    endpoint: "https://aethera.example.com:8443",
    caFingerprintSha256: "AB:CD:EF:01",
    installCommand: "curl -fsSL https://aethera.example.com/install.sh | sh -s -- --token ajt_SECRET_TOKEN_VALUE",
    ...over,
  } as JoinToken;
}

export function makeJob(over: Partial<Job> = {}): Job {
  return {
    id: "11111111-2222-4333-8444-555555555555",
    type: "server.prune",
    status: "queued",
    cancelRequested: false,
    priority: 0,
    resource: null,
    queuePosition: null,
    attempt: 1,
    maxAttempts: 3,
    retryNo: 0,
    runAfter: NOW,
    createdAt: NOW,
    startedAt: null,
    finishedAt: null,
    parentJobId: null,
    createdBy: null,
    error: null,
    result: null,
    links: { self: "/api/v1/jobs/1", logs: "/api/v1/jobs/1/logs" },
    ...over,
  } as Job;
}

export function problem(status: number, code: string, title = code, errors: ApiError["errors"] = []) {
  return new ApiError({ status, code, title, errors });
}

/** A ServersApi where every method is a vi.fn rejecting with 404 unless overridden. */
export function fakeApi(over: Partial<Record<keyof ServersApi, unknown>> = {}): ServersApi {
  const names = [
    "list", "get", "create", "update", "remove", "createJoinToken", "listJoinTokens", "revokeJoinToken", "resetAgent",
    "status", "events", "latestMetrics", "metrics", "discovery", "containers", "images", "volumes", "networks",
    "prune", "refreshDiscovery", "job",
  ] as const;
  const api: Record<string, unknown> = {};
  for (const n of names) api[n] = vi.fn().mockRejectedValue(problem(404, "request.not_found"));
  return { ...api, ...over } as unknown as ServersApi;
}
