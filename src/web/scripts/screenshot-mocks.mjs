// Mock responses for the server pages, used only by scripts/screenshots.mjs
// (page.route). Shapes follow openapi/aethera.v1.json; nothing here ships.
const now = Date.now();
const iso = (msAgo = 0) => new Date(now - msAgo).toISOString();
const GiB = 1024 ** 3;

const status = (reach, agent, docker, hb) => ({
  reachability: { status: reach, checkedAt: iso(20_000), changedAt: iso(3_600_000), probePort: 22 },
  agent: { status: agent, lastHeartbeatAt: hb, changedAt: iso(3_600_000) },
  docker: { status: docker, changedAt: iso(3_600_000) },
});

export const SERVERS = [
  {
    id: "0192f3c8-0001-4000-8000-000000000001", name: "edge-fra-1", host: "203.0.113.10", sshPort: 22, sshUser: null, transport: "agent",
    roles: ["master", "worker"], lifecycle: "active", publicIp: "203.0.113.10", maxConcurrentBuilds: 2, sshCredentialSecretId: null,
    resources: { cpuCores: 4, memoryBytes: 8 * GiB, diskBytes: 160 * GiB, os: "Ubuntu 24.04", architecture: "amd64" },
    status: status("reachable", "connected", "running", iso(4_000)), agentVersion: "0.3.1", workloadCount: 5, createdAt: iso(9e8), updatedAt: iso(1e5),
  },
  {
    id: "0192f3c8-0002-4000-8000-000000000002", name: "build-ams-2", host: "build-2.example.com", sshPort: 22, sshUser: null, transport: "agent",
    roles: ["build", "ci"], lifecycle: "active", publicIp: null, maxConcurrentBuilds: 4, sshCredentialSecretId: null,
    resources: { cpuCores: 8, memoryBytes: 16 * GiB, diskBytes: 320 * GiB, os: "Debian 12", architecture: "arm64" },
    status: status("reachable", "connected", "stopped", iso(6_000)), agentVersion: "0.3.1", workloadCount: 0, createdAt: iso(8e8), updatedAt: iso(1e5),
  },
  {
    id: "0192f3c8-0003-4000-8000-000000000003", name: "db-nbg-1", host: "198.51.100.23", sshPort: 22, sshUser: null, transport: "agent",
    roles: ["storage"], lifecycle: "active", publicIp: null, maxConcurrentBuilds: 1, sshCredentialSecretId: null,
    resources: { cpuCores: 2, memoryBytes: 4 * GiB, diskBytes: 80 * GiB, os: "Ubuntu 22.04", architecture: "amd64" },
    status: status("unreachable", "unavailable", "unknown", iso(14 * 60_000)), agentVersion: "0.3.0", workloadCount: 3, createdAt: iso(7e8), updatedAt: iso(1e5),
  },
];

const axis = (name, health, extra = {}) => ({ axis: name, health, blockedBy: null, since: iso(3_600_000), stale: false, detail: null, ...extra });

export const NEW_SERVER_ID = "0192f3c8-0009-4000-8000-000000000009";

export function healthFor(id) {
  if (id === NEW_SERVER_ID) {
    // Freshly registered: the agent has not enrolled yet.
    return {
      serverId: id, server: axis("server", "unknown"), agent: axis("agent", "unknown"), docker: axis("docker", "unknown", { blockedBy: "agent" }),
      application: axis("application", "unknown", { detail: "No workloads on this server." }), firstFailingLayer: null,
      applications: { total: 0, unavailable: 0, healthy: 0, unknown: 0 }, session: null, observedAt: iso(),
    };
  }
  if (id === SERVERS[2].id) {
    return {
      serverId: id,
      server: axis("server", "unavailable", { since: iso(14 * 60_000), detail: "The reachability probe fails." }),
      agent: axis("agent", "unknown", { blockedBy: "server", since: iso(14 * 60_000) }),
      docker: axis("docker", "unknown", { blockedBy: "server", since: iso(14 * 60_000) }),
      application: axis("application", "unknown", { blockedBy: "server", stale: true, since: iso(14 * 60_000), detail: "3 workload(s), last known state shown as stale." }),
      firstFailingLayer: "server",
      applications: { total: 3, unavailable: 0, healthy: 0, unknown: 3 },
      session: null,
      observedAt: iso(),
    };
  }
  const edge = id === SERVERS[0].id;
  return {
    serverId: id,
    server: axis("server", "available"),
    agent: axis("agent", "available"),
    docker: axis("docker", edge ? "available" : "unavailable", edge ? {} : { detail: "The Docker daemon is stopped." }),
    application: edge ? axis("application", "available") : axis("application", "unknown", { detail: "No workloads on this server." }),
    firstFailingLayer: edge ? null : "docker",
    applications: edge ? { total: 5, unavailable: 0, healthy: 5, unknown: 0 } : { total: 0, unavailable: 0, healthy: 0, unknown: 0 },
    session: { sessionId: "sess-1", agentVersion: "0.3.1", connectedAt: iso(3_300_000), rttMilliseconds: 18.4, clockSkewSeconds: 0.04, capabilities: ["docker.read", "docker.prune", "metrics.stream", "discovery"] },
    observedAt: iso(),
  };
}

export function latestFor(id) {
  if (id === SERVERS[2].id) return { serverId: id, host: null, containers: [], observedAt: iso(14 * 60_000), stale: true };
  const edge = id === SERVERS[0].id;
  return {
    serverId: id,
    host: {
      timestamp: iso(4_000), cpuPercent: edge ? 38.5 : 6.2, memoryUsedBytes: (edge ? 5.9 : 2.1) * GiB, memoryTotalBytes: (edge ? 8 : 16) * GiB,
      diskUsedBytes: (edge ? 142 : 61) * GiB, diskTotalBytes: (edge ? 160 : 320) * GiB, netRxBytes: 1, netTxBytes: 1,
      netRxBytesPerSecond: 182_000, netTxBytesPerSecond: 64_000, load1: 0.8, load5: 0.7, load15: 0.6,
    },
    containers: [], observedAt: iso(4_000), stale: false,
  };
}

export function seriesFor(id, fromMs, toMs) {
  const n = 120;
  const points = Array.from({ length: n }, (_, i) => {
    const t = fromMs + ((toMs - fromMs) * i) / (n - 1);
    const wave = Math.sin(i / 9);
    return {
      timestamp: new Date(t).toISOString(),
      cpuPercent: 35 + 22 * wave + (i % 11),
      memoryUsedBytes: (5.2 + 0.5 * Math.sin(i / 17) + i * 0.004) * GiB, memoryTotalBytes: 8 * GiB,
      diskUsedBytes: (140 + i * 0.015) * GiB, diskTotalBytes: 160 * GiB,
      netRxBytes: 0, netTxBytes: 0,
      netRxBytesPerSecond: i === 0 ? null : 150_000 + 90_000 * Math.sin(i / 5) ** 2,
      netTxBytesPerSecond: i === 0 ? null : 50_000 + 40_000 * Math.cos(i / 7) ** 2,
      load1: 0.8, load5: 0.7, load15: 0.6,
    };
  });
  return { serverId: id, resolution: "raw", from: new Date(fromMs).toISOString(), to: new Date(toMs).toISOString(), containerId: null, points };
}

const container = (name, image, state, status, ports, health = "none") => ({
  id: name, name: `/${name}`, image, imageId: "sha256:x", state, status, health, createdAt: iso(7_200_000), startedAt: iso(7_000_000), finishedAt: null,
  exitCode: 0, oomKilled: false, restartCount: 0, labels: {}, ports, mounts: [], networks: [], inspectJson: null,
});

export const CONTAINERS = [
  container("proxy", "traefik:v3.1", "running", "Up 2 hours", [{ containerPort: 443, hostPort: 443, protocol: "tcp" }, { containerPort: 80, hostPort: 80, protocol: "tcp" }], "healthy"),
  container("shop-api", "registry.local/acme/shop-api:3f9c2d1", "running", "Up 41 minutes", [{ containerPort: 8080, hostPort: 0, protocol: "tcp" }], "healthy"),
  container("shop-worker", "registry.local/acme/shop-worker:3f9c2d1", "restarting", "Restarting (1) 12 seconds ago", [], "unhealthy"),
  container("postgres", "postgres:17", "running", "Up 6 days", [{ containerPort: 5432, hostPort: 0, protocol: "tcp" }]),
  container("migrate-once", "registry.local/acme/shop-api:3f9c2d0", "exited", "Exited (0) 3 hours ago", []),
];

export const DISCOVERY = {
  serverId: SERVERS[0].id,
  facts: { os: "Ubuntu", osVersion: "24.04", kernel: "6.8.0-45", architecture: "amd64", cpuModel: "AMD EPYC 7543", cpuCores: 4, memoryBytes: 8 * GiB, diskBytes: 160 * GiB, dockerVersion: "27.3.1", discoveredAt: iso(600_000) },
  report: {
    collectedAt: iso(600_000),
    host: { hostname: "edge-fra-1", osName: "Ubuntu", osVersion: "24.04.1 LTS", kernelVersion: "6.8.0-45-generic", architecture: "amd64", cpuModel: "AMD EPYC 7543 32-Core", cpuCoresPhysical: 2, cpuCoresLogical: 4, memoryTotalBytes: 8 * GiB, swapTotalBytes: 2 * GiB, virtualization: "kvm", ipAddresses: ["203.0.113.10", "10.0.0.4"], timezone: "UTC", bootTime: iso(9e8) },
    docker: { status: "running", version: "27.3.1", apiVersion: "1.47", storageDriver: "overlay2", cgroupVersion: "2", dockerRootDir: "/var/lib/docker", composeVersion: "2.29.7", buildxVersion: "0.17.1", rootless: false, swarmActive: false, containersRunning: 4, containersStopped: 1, imageCount: 9, error: "" },
    disks: [{ mountPoint: "/", device: "/dev/vda1", fsType: "ext4", totalBytes: 160 * GiB, usedBytes: 142 * GiB, inodesTotal: 1, inodesUsed: 1 }],
    interfaces: [], containers: [], networks: [], volumes: [],
    tools: [{ name: "git", version: "2.43.0", path: "/usr/bin/git" }, { name: "docker-compose", version: "2.29.7", path: "/usr/libexec/docker/cli-plugins/docker-compose" }],
  },
  storedAt: iso(600_000),
  stale: false,
};

export const EVENTS = [
  { id: "e1", kind: "status.changed", axis: "agent", oldValue: "unavailable", newValue: "connected", detail: null, occurredAt: iso(3_300_000) },
  { id: "e2", kind: "status.changed", axis: "docker", oldValue: "stopped", newValue: "running", detail: null, occurredAt: iso(3_290_000) },
  { id: "e3", kind: "status.changed", axis: "agent", oldValue: "connected", newValue: "unavailable", detail: "stream closed", occurredAt: iso(3_900_000) },
];

export const TOKENS = [
  { id: "t1", state: "used", createdAt: iso(9e8), expiresAt: iso(9e8 - 3_600_000), usedAt: iso(9e8 - 60_000), revokedAt: null, createdByUserId: null },
  { id: "t2", state: "expired", createdAt: iso(5e8), expiresAt: iso(5e8 - 3_600_000), usedAt: null, revokedAt: null, createdByUserId: null },
];

export const JOIN_TOKEN = {
  id: "t3", serverId: SERVERS[0].id, token: "ajt_8Zk3mQ0vRlP2xW9uYb7tNcH5",
  expiresAt: new Date(Date.now() + 58 * 60_000).toISOString(), endpoint: "https://aethera.example.com:8443",
  caFingerprintSha256: "5C:1A:E9:07:B3:44:D2:8F:6A:90:1E:C7:3B:A2:55:0D",
  installCommand: "curl -fsSL https://aethera.example.com/agent/install.sh | sudo sh -s -- --endpoint https://aethera.example.com:8443 --ca-sha256 5C1AE907B344D28F6A901EC73BA2550D --token ajt_8Zk3mQ0vRlP2xW9uYb7tNcH5",
};

/** Returns `{status, body}` for a /api/v1 path, or null when this module has no mock for it. */
export function mockServersApi(pathname, method, search = "") {
  const p = pathname.replace(/^\/api\/v1/, "");
  if (p === "/servers" && method === "GET") return { status: 200, body: { items: SERVERS, nextCursor: null } };
  if (p === "/servers" && method === "POST") return { status: 201, body: { ...SERVERS[0], id: NEW_SERVER_ID, name: "edge-new-1" } };
  const m = p.match(/^\/servers\/([^/]+)(?:\/(.*))?$/);
  if (!m) return null;
  const [, id, rest] = m;
  const server = SERVERS.find((s) => s.id === id) ?? SERVERS[0];
  if (!rest) return { status: 200, body: server };
  const q = new URLSearchParams(search);
  switch (rest) {
    case "status": return { status: 200, body: healthFor(id) };
    case "metrics/latest": return { status: 200, body: latestFor(id) };
    case "metrics": return { status: 200, body: seriesFor(id, Date.parse(q.get("from") ?? iso(3_600_000)), Date.parse(q.get("to") ?? iso())) };
    case "discovery": return { status: 200, body: DISCOVERY };
    case "events": return { status: 200, body: EVENTS };
    case "docker/containers": return { status: 200, body: CONTAINERS };
    case "docker/images": return { status: 200, body: [{ id: "sha256:abcdef0123456789abcdef", repoTags: ["traefik:v3.1"], repoDigests: [], sizeBytes: 190e6, createdAt: iso(5e8), labels: {}, architecture: "amd64", os: "linux", containersUsing: 1 }] };
    case "docker/volumes": return { status: 200, body: [] };
    case "docker/networks": return { status: 200, body: [] };
    case "join-tokens": return method === "POST" ? { status: 201, body: { ...JOIN_TOKEN, serverId: id } } : { status: 200, body: TOKENS };
    default: return null;
  }
}
