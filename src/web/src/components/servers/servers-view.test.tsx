import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axis, fakeApi, makeHealth, makeServer, problem } from "@/test/servers-fixtures";
import { ServersView } from "./servers-view";

const edge = makeServer({ id: "id-edge", name: "edge-1", host: "203.0.113.10", roles: ["worker", "build"] });
const dead = makeServer({
  id: "id-dead",
  name: "db-1",
  host: "198.51.100.2",
  roles: ["storage"],
  agentVersion: null,
  status: {
    reachability: { status: "unreachable", checkedAt: "2026-10-04T10:00:00Z", changedAt: "2026-10-04T10:00:00Z", probePort: 22 },
    agent: { status: "unavailable", lastHeartbeatAt: "2026-10-04T09:00:00Z", changedAt: "2026-10-04T09:00:00Z" },
    docker: { status: "unknown", changedAt: null },
  } as never,
});

function api(servers = [edge, dead]) {
  return fakeApi({
    list: vi.fn().mockResolvedValue({ items: servers, nextCursor: null }),
    status: vi.fn().mockImplementation(async (id: string) =>
      id === "id-dead"
        ? makeHealth({
            server: axis("server", "unavailable"),
            agent: axis("agent", "unknown", { blockedBy: "server" }),
            docker: axis("docker", "unknown", { blockedBy: "server" }),
            application: axis("application", "unknown", { blockedBy: "server", stale: true, since: "2026-10-04T09:00:00Z" }),
            firstFailingLayer: "server",
            applications: { total: 3, unavailable: 0, healthy: 0, unknown: 3 },
          })
        : makeHealth(),
    ),
    latestMetrics: vi.fn().mockImplementation(async (id: string) =>
      id === "id-edge"
        ? {
            serverId: id,
            host: { timestamp: "2026-10-04T10:00:00Z", cpuPercent: 37, memoryUsedBytes: 2 * 1024 ** 3, memoryTotalBytes: 8 * 1024 ** 3, diskUsedBytes: 90 * 1024 ** 3, diskTotalBytes: 100 * 1024 ** 3 },
            containers: [],
            observedAt: "2026-10-04T10:00:00Z",
            stale: false,
          }
        : { serverId: id, host: null, containers: [], observedAt: null, stale: true },
    ),
  });
}

describe("ServersView", () => {
  it("shows each axis as its own badge plus the application summary, never a single online flag", async () => {
    render(<ServersView api={api()} pollMs={60_000} />);
    const list = await screen.findByRole("list", { name: "Servers" });
    const rows = within(list).getAllByRole("listitem");
    expect(rows).toHaveLength(2);

    const edgeGroup = within(rows[0]).getByRole("group", { name: "Status axes" });
    expect(edgeGroup).toHaveTextContent(/server\s*reachable/i);
    expect(edgeGroup).toHaveTextContent(/agent\s*connected/i);
    expect(edgeGroup).toHaveTextContent(/docker\s*running/i);
    await within(rows[0]).findByText(/2\/2 healthy/i);
    expect(within(rows[0]).queryByText(/^online$/i)).not.toBeInTheDocument();

    const deadGroup = within(rows[1]).getByRole("group", { name: "Status axes" });
    expect(deadGroup).toHaveTextContent(/server\s*unreachable/i);
    expect(deadGroup).toHaveTextContent(/agent\s*unavailable/i);
    expect(deadGroup).toHaveTextContent(/docker\s*unknown/i);
  });

  it("shows last-seen, 'stale since', version, roles and the CPU/RAM/disk mini bars", async () => {
    render(<ServersView api={api()} pollMs={60_000} />);
    const rows = within(await screen.findByRole("list", { name: "Servers" })).getAllByRole("listitem");
    expect(rows[0]).toHaveTextContent("0.3.1");
    expect(rows[0]).toHaveTextContent("worker");
    expect(rows[0]).toHaveTextContent("build");
    expect(within(rows[0]).getByRole("meter", { name: "CPU" })).toHaveAttribute("aria-valuenow", "37");
    expect(within(rows[0]).getByRole("meter", { name: "RAM" })).toHaveAttribute("aria-valuenow", "25");
    expect(within(rows[0]).getByRole("meter", { name: "Disk" })).toHaveAttribute("aria-valuenow", "90");
    // No sample for the dead server: bars render empty, not as 0%.
    expect(within(rows[1]).getByRole("meter", { name: "CPU" })).not.toHaveAttribute("aria-valuenow");
    expect(await within(rows[1]).findByText(/stale since/i)).toBeInTheDocument();
    expect(rows[1]).toHaveTextContent(/last seen/i);
  });

  it("links names to the detail route (plain anchor, static export)", async () => {
    render(<ServersView api={api()} pollMs={60_000} />);
    expect(await screen.findByRole("link", { name: "edge-1" })).toHaveAttribute("href", "/servers/id-edge");
  });

  it("searches by name/host/role and filters by agent status", async () => {
    const user = userEvent.setup();
    render(<ServersView api={api()} pollMs={60_000} />);
    await screen.findByRole("list", { name: "Servers" });
    await user.type(screen.getByRole("searchbox", { name: "Search servers" }), "storage");
    expect(screen.getByRole("link", { name: "db-1" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "edge-1" })).not.toBeInTheDocument();
    await user.clear(screen.getByRole("searchbox", { name: "Search servers" }));
    await user.selectOptions(screen.getByLabelText("Agent"), "connected");
    expect(screen.getByRole("link", { name: "edge-1" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "db-1" })).not.toBeInTheDocument();
    await user.type(screen.getByRole("searchbox", { name: "Search servers" }), "zzz");
    expect(screen.getByText(/no server matches/i)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Clear filters" }));
    expect(screen.getAllByRole("listitem")).toHaveLength(2);
  });

  it("renders the empty state with a call to action", async () => {
    render(<ServersView api={api([])} pollMs={60_000} />);
    expect(await screen.findByText("Connect your first server")).toBeInTheDocument();
    expect(screen.getAllByRole("link", { name: /add server/i }).at(-1)).toHaveAttribute("href", "/servers/new");
  });

  it("keeps rendering a row when its status or metrics call fails", async () => {
    const a = api();
    (a.status as ReturnType<typeof vi.fn>).mockRejectedValue(problem(500, "server.error"));
    (a.latestMetrics as ReturnType<typeof vi.fn>).mockRejectedValue(problem(500, "server.error"));
    render(<ServersView api={a} pollMs={60_000} />);
    const rows = within(await screen.findByRole("list", { name: "Servers" })).getAllByRole("listitem");
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent(/2 workloads/i);
  });

  it("shows a retryable error when the list cannot be loaded", async () => {
    const list = vi.fn().mockRejectedValueOnce(problem(500, "server.error", "Internal error")).mockResolvedValue({ items: [edge], nextCursor: null });
    const user = userEvent.setup();
    render(<ServersView api={fakeApi({ list, status: vi.fn().mockResolvedValue(makeHealth()), latestMetrics: vi.fn().mockRejectedValue(problem(404, "x")) })} pollMs={60_000} />);
    expect(await screen.findByRole("alert")).toHaveTextContent("Internal error");
    await user.click(screen.getByRole("button", { name: "Retry" }));
    expect(await screen.findByRole("link", { name: "edge-1" })).toBeInTheDocument();
  });

  it("refreshes on its own interval", async () => {
    const a = api();
    render(<ServersView api={a} pollMs={40} />);
    await screen.findByRole("list", { name: "Servers" });
    await vi.waitFor(() => expect((a.list as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(2));
  });
});
