import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fakeResources, makeApp, makeListItem, NOW } from "@/test/resources-fixtures";
import { fakeApi, makeServer } from "@/test/servers-fixtures";
import { aggregateUsage, DashboardView, deriveAlerts } from "@/components/shell/dashboard-view";
import { groupTemplates, NewServiceView } from "@/components/services/new-service-view";
import { connectionInfo } from "@/components/services/service-detail-view";
import { isManaged, SecretsView } from "./secrets-view";
import { isAdminRole, SettingsView, settingsTabFromHash } from "./settings-view";

let role = "developer";
vi.mock("@/lib/auth/auth-context", () => ({
  useAuth: () => ({ me: { user: { id: "u-1", email: "me@example.com", displayName: "Me" }, organization: { id: "o-1", name: "Acme", slug: "acme" }, role } }),
}));
vi.mock("@/lib/navigate", () => ({ navigateTo: vi.fn() }));

const empty = { items: [], nextCursor: null };

describe("dashboard", () => {
  const down = makeServer({ id: "s-down", name: "db-1", transport: "agent", status: { reachability: { status: "unreachable" }, agent: { status: "unavailable" }, docker: { status: "unknown" } } as never });
  const up = makeServer({ id: "s-up", name: "edge-1" });

  it("derives alerts from reported status only", () => {
    const alerts = deriveAlerts(
      [up, down],
      [makeApp({ id: "a1", name: "web", status: "failed", statusReason: "health check" }), makeApp({ id: "a2", name: "api", status: "running" }), makeApp({ id: "a3", name: "slow", status: "unhealthy" })],
      [],
      { cpu: 95, memory: 40, disk: null },
    );
    expect(alerts.map((a) => [a.key, a.tone])).toEqual([
      ["srv:s-down", "danger"],
      ["app:a1", "danger"],
      ["app:a3", "warning"],
      ["use:CPU", "warning"],
    ]);
    expect(deriveAlerts([up], [makeApp()], [], { cpu: 10, memory: 10, disk: 10 })).toEqual([]);
  });

  it("averages cpu and sums memory and disk over servers with fresh metrics", () => {
    const host = (cpu: number, used: number) => ({ timestamp: NOW, cpuPercent: cpu, memoryUsedBytes: used, memoryTotalBytes: 100, diskUsedBytes: used, diskTotalBytes: 200 });
    const usage = aggregateUsage([
      { serverId: "a", host: host(20, 10), containers: [], observedAt: NOW, stale: false } as never,
      { serverId: "b", host: host(60, 30), containers: [], observedAt: NOW, stale: false } as never,
      { serverId: "c", host: host(99, 99), containers: [], observedAt: NOW, stale: true } as never,
      undefined,
    ]);
    expect(usage.count).toBe(2);
    expect(usage.cpu).toBe(40);
    expect(usage.memory).toBe(20);
    expect(usage.disk).toBe(10);
  });

  it("shows live counts, recent and failed deployments, and a quiet alert panel", async () => {
    const api = fakeResources({
      applications: { list: vi.fn().mockResolvedValue({ items: [makeApp(), makeApp({ id: "a2", name: "api" })], nextCursor: null }) },
      services: { list: vi.fn().mockResolvedValue(empty) },
      deployments: {
        list: vi.fn().mockImplementation(async (q: { status?: string }) =>
          q.status === "failed"
            ? { items: [makeListItem({ id: "d-f", number: 7, status: "failed", failedStep: "build", failureCode: "build.failed" }, "api")], nextCursor: null }
            : { items: [makeListItem({ createdAt: new Date().toISOString() })], nextCursor: null },
        ),
      },
      jobs: { list: vi.fn().mockResolvedValue({ items: [{ id: "j1", type: "application.deploy", status: "running", createdAt: NOW }], nextCursor: null }) },
    });
    const servers = fakeApi({ list: vi.fn().mockResolvedValue({ items: [makeServer()], nextCursor: null }), latestMetrics: vi.fn().mockResolvedValue({ serverId: "x", host: null, containers: [], observedAt: null, stale: true }) });
    render(<DashboardView api={api} servers={servers} pollMs={60_000} />);

    expect(await screen.findByText("All quiet")).toBeInTheDocument();
    expect(screen.getByText("1 online · 0 ssh · 0 offline")).toBeInTheDocument();
    expect(screen.getByText("2 running · 0 deploying · 0 failing")).toBeInTheDocument();
    expect(await screen.findByText("failed at Build · build.failed", { exact: false })).toBeInTheDocument();
    expect(screen.getByText("application.deploy")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /New application/ })).toHaveAttribute("href", "/applications/new");
  });
});

describe("settings", () => {
  it("knows the admin roles and keeps non-admins on tabs they may use", () => {
    expect(isAdminRole("owner")).toBe(true);
    expect(isAdminRole("admin")).toBe(true);
    expect(isAdminRole("developer")).toBe(false);
    expect(settingsTabFromHash("#audit", false)).toBe("account");
    expect(settingsTabFromHash("#audit", true)).toBe("audit");
    expect(settingsTabFromHash("#tokens", false)).toBe("tokens");
    expect(settingsTabFromHash("", true)).toBe("account");
  });

  it("hides members, git credentials and the audit log from a developer", async () => {
    role = "developer";
    render(<SettingsView api={fakeResources({ tokens: { list: vi.fn().mockResolvedValue(empty) } })} />);
    expect(screen.getByRole("tab", { name: "Account" })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: "API tokens" })).toBeInTheDocument();
    expect(screen.queryByRole("tab", { name: "Members" })).not.toBeInTheDocument();
    expect(screen.queryByRole("tab", { name: "Audit log" })).not.toBeInTheDocument();
    expect(screen.getByRole("note")).toHaveTextContent("administrator role");
  });

  it("lets an administrator read the audit log with filters", async () => {
    role = "admin";
    const list = vi.fn().mockResolvedValue({
      items: [{ id: "e1", actorType: "user", actorLabel: "ada@example.com", action: "application.deploy_requested", resourceType: "application", resourceName: "web", resourceId: "a1", metadata: { number: 3 }, ipAddress: "10.0.0.1", requestId: "r1", occurredAt: NOW, actorUserId: "u-1", actorApiTokenId: null }],
      nextCursor: null,
    });
    window.location.hash = "#audit";
    render(<SettingsView api={fakeResources({ audit: { list } })} />);
    const row = await screen.findByRole("button", { name: /application\.deploy_requested/ });
    expect(row).toHaveTextContent("ada@example.com → web");
    await userEvent.click(row);
    expect(screen.getByText(/"number": 3/)).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText(/^Action/), "secret.*");
    await waitFor(() => expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ action: "secret.*" }), expect.anything()));
    window.location.hash = "";
  });
});

describe("secrets", () => {
  it("treats registry, git and generated secrets as managed", () => {
    expect(isManaged({ managed: false, purpose: "user" })).toBe(false);
    expect(isManaged({ managed: true, purpose: "user" })).toBe(true);
    expect(isManaged({ managed: false, purpose: "serviceGenerated" })).toBe(true);
  });

  it("masks values and offers rotate and delete only for secrets you own", async () => {
    role = "admin";
    const secret = (over: object) => ({ id: "s", name: "x", description: null, scope: "organization", currentVersion: 1, value: "", createdAt: NOW, updatedAt: NOW, rotatedAt: null, purpose: "user", managed: false, managedBy: null, ...over });
    const list = vi.fn().mockResolvedValue({ items: [secret({ id: "s1", name: "STRIPE_KEY" }), secret({ id: "s2", name: "POSTGRES_PASSWORD", purpose: "serviceGenerated", managed: true, managedBy: { type: "service", id: "x", name: "db" } })], nextCursor: null });
    render(<SecretsView api={fakeResources({ secrets: { list } })} />);
    const rows = await screen.findAllByRole("listitem");
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByRole("button", { name: "Rotate STRIPE_KEY" })).toBeInTheDocument();
    expect(within(rows[0]).getByRole("button", { name: "Delete STRIPE_KEY" })).toBeInTheDocument();
    expect(within(rows[1]).queryByRole("button", { name: /Rotate|Delete/ })).not.toBeInTheDocument();
    expect(within(rows[1]).getByText(/managed/)).toBeInTheDocument();
    expect(within(rows[1]).getByRole("button", { name: "Reveal POSTGRES_PASSWORD" })).toBeInTheDocument();
  });
});

describe("services", () => {
  const template = (key: string, category: string) => ({ key, name: key, description: "", category, defaultVersion: "1", defaultImage: `${key}:1`, versions: [{ version: "1", image: `${key}:1`, isDefault: true }], ports: [{ containerPort: 5432, protocol: "tcp", isHttp: false }], volumes: [{ name: "data", mountPath: "/data" }], healthCheck: { type: "tcp", path: null, port: 5432, intervalSeconds: 10, timeoutSeconds: 5, retries: 5, startPeriodSeconds: 20 }, env: [{ key: "PASSWORD", value: null, generate: "password", length: 32 }] });

  it("groups templates by category and describes how other workloads reach a service", () => {
    expect(groupTemplates([template("postgres", "database"), template("redis", "cache"), template("mysql", "database")] as never).map(([c, t]) => [c, t.length])).toEqual([["database", 2], ["cache", 1]]);
    expect(connectionInfo({ slug: "db", runtime: { ports: [{ containerPort: 5432, protocol: "tcp" }] } } as never)).toEqual([{ label: "5432/tcp", value: "db:5432" }]);
  });

  it("creates a service from a template with its version, then deploys it", async () => {
    const create = vi.fn().mockResolvedValue({ id: "svc-1" });
    const deploy = vi.fn().mockResolvedValue({ id: "dep-1" });
    const api = fakeResources({
      services: { templates: vi.fn().mockResolvedValue([template("postgres", "database")]), create, deploy },
      projects: { list: vi.fn().mockResolvedValue({ items: [{ id: "p", name: "Shop", environments: [{ id: "env-1", name: "Staging", isProduction: false }] }], nextCursor: null }) },
    });
    const servers = fakeApi({ list: vi.fn().mockResolvedValue({ items: [makeServer({ id: "srv-1", name: "edge-1" })], nextCursor: null }) });
    render(<NewServiceView api={api} servers={servers} />);

    await userEvent.click(await screen.findByRole("button", { name: /postgres/ }));
    expect(screen.getByText(/Credentials are generated and stored as secrets/)).toHaveTextContent("PASSWORD");
    expect(screen.getByText(/internal only/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Create and deploy" }));

    await waitFor(() => expect(deploy).toHaveBeenCalledWith("svc-1"));
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ templateKey: "postgres", version: "1", environmentId: "env-1", serverId: "srv-1", name: "postgres" }));
  });
});
