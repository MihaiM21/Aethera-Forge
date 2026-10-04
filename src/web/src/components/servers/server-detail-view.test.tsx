import * as React from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axis, fakeApi, makeHealth, makeServer, problem } from "@/test/servers-fixtures";
import { ServerDetailView } from "./server-detail-view";

const push = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ push }) }));

const server = makeServer({ id: "s1", name: "edge-1" });

const discovery = {
  serverId: "s1",
  facts: { os: "Ubuntu", osVersion: "24.04", kernel: "6.8", architecture: "amd64", cpuModel: "EPYC", cpuCores: 4, memoryBytes: 8 * 1024 ** 3, diskBytes: null, dockerVersion: "27.1", discoveredAt: "2026-10-04T10:00:00Z" },
  report: null,
  storedAt: "2026-10-04T09:50:00Z",
  stale: true,
};

function api(over: Parameters<typeof fakeApi>[0] = {}) {
  return fakeApi({
    get: vi.fn().mockResolvedValue(server),
    status: vi.fn().mockResolvedValue(makeHealth()),
    discovery: vi.fn().mockResolvedValue(discovery),
    ...over,
  });
}

describe("ServerDetailView", () => {
  beforeEach(() => {
    window.location.hash = "";
    push.mockClear();
  });

  it("renders the overview with the four axes as separate cards", async () => {
    render(<ServerDetailView api={api()} id="s1" />);
    expect(await screen.findByRole("heading", { name: "edge-1" })).toBeInTheDocument();
    for (const name of ["Server axis", "Agent axis", "Docker axis", "Applications axis"]) {
      expect(await screen.findByRole("article", { name })).toBeInTheDocument();
    }
    expect(screen.getByRole("article", { name: "Agent axis" })).toHaveTextContent("connected");
  });

  it("explains 'blocked by <layer>', 'stale since' and the first failing layer", async () => {
    const health = makeHealth({
      server: axis("server", "unavailable", { detail: "The reachability probe fails." }),
      agent: axis("agent", "unknown", { blockedBy: "server" }),
      docker: axis("docker", "unknown", { blockedBy: "server" }),
      application: axis("application", "unknown", { blockedBy: "server", stale: true, since: new Date(Date.now() - 12 * 60_000).toISOString() as never, detail: "2 workload(s), last known state shown as stale." }),
      firstFailingLayer: "server",
    });
    render(<ServerDetailView api={api({ status: vi.fn().mockResolvedValue(health) })} id="s1" />);
    const agent = await screen.findByRole("article", { name: "Agent axis" });
    expect(agent).toHaveTextContent("blocked by server");
    expect(screen.getByRole("article", { name: "Docker axis" })).toHaveTextContent("blocked by server");
    const app = screen.getByRole("article", { name: "Applications axis" });
    expect(app).toHaveTextContent("blocked by server");
    expect(app).toHaveTextContent(/stale since 12m ago/);
    expect(app).toHaveTextContent("last known state shown as stale");
    expect(screen.getByText(/first failing layer/i)).toHaveTextContent("server");
    expect(screen.getByRole("article", { name: "Server axis" })).toHaveTextContent("The reachability probe fails.");
  });

  it("shows stored discovery data and says the agent is not connected", async () => {
    render(<ServerDetailView api={api()} id="s1" />);
    expect(await screen.findByText(/showing what was collected/i)).toBeInTheDocument();
    expect(screen.getByText("EPYC")).toBeInTheDocument();
  });

  it("shows a not-found state for an unknown id", async () => {
    render(<ServerDetailView api={fakeApi({ get: vi.fn().mockRejectedValue(problem(404, "server.not_found")) })} id="nope" />);
    expect(await screen.findByText("Server not found")).toBeInTheDocument();
  });

  it("shows a retryable error for other failures", async () => {
    render(<ServerDetailView api={fakeApi({ get: vi.fn().mockRejectedValue(problem(500, "server.error", "Internal error")) })} id="s1" />);
    expect(await screen.findByRole("alert")).toHaveTextContent("Internal error");
  });

  it("opens the containers tab, and explains a 503 agent_unavailable instead of failing", async () => {
    const user = userEvent.setup();
    const containers = vi.fn().mockRejectedValue(problem(503, "server.agent_unavailable", "Agent unavailable"));
    render(<ServerDetailView api={api({ containers })} id="s1" />);
    await screen.findByRole("heading", { name: "edge-1" });
    await user.click(screen.getByRole("tab", { name: "Containers" }));
    const note = await screen.findByText(/read live from the server's agent/i);
    expect(note.closest("[role=status]")).toHaveTextContent("Agent unavailable");
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    expect(window.location.hash).toBe("#containers");
  });

  it("lists containers read through the agent", async () => {
    const user = userEvent.setup();
    const containers = vi.fn().mockResolvedValue([
      { id: "c1", name: "/web", image: "nginx:1.27", imageId: "sha256:1", state: "running", status: "Up 2 hours", health: "healthy", createdAt: "2026-10-04T08:00:00Z", startedAt: "2026-10-04T08:00:00Z", finishedAt: null, exitCode: 0, oomKilled: false, restartCount: 0, labels: {}, ports: [{ containerPort: 80, hostPort: 8080, protocol: "tcp" }], mounts: [], networks: [], inspectJson: null },
    ]);
    render(<ServerDetailView api={api({ containers })} id="s1" />);
    await screen.findByRole("heading", { name: "edge-1" });
    await user.click(screen.getByRole("tab", { name: "Containers" }));
    const table = await screen.findByRole("table");
    expect(within(table).getByText("web")).toBeInTheDocument();
    expect(within(table).getByText("8080:80/tcp")).toBeInTheDocument();
    expect(within(table).getByText("healthy")).toBeInTheDocument();
  });

  it("images, volumes and networks load lazily per sub-tab", async () => {
    const user = userEvent.setup();
    const images = vi.fn().mockResolvedValue([{ id: "sha256:abcdef0123456789abcd", repoTags: ["nginx:1.27"], repoDigests: [], sizeBytes: 190_000_000, createdAt: "2026-10-01T00:00:00Z", labels: {}, architecture: "amd64", os: "linux", containersUsing: 1 }]);
    const volumes = vi.fn().mockResolvedValue([]);
    render(<ServerDetailView api={api({ images, volumes })} id="s1" />);
    await screen.findByRole("heading", { name: "edge-1" });
    await user.click(screen.getByRole("tab", { name: /images/i }));
    expect(await screen.findByText("nginx:1.27")).toBeInTheDocument();
    expect(volumes).not.toHaveBeenCalled();
    await user.click(screen.getByRole("tab", { name: "Volumes" }));
    expect(await screen.findByText(/no volumes on this server/i)).toBeInTheDocument();
  });

  it("renders events newest first with the state transition", async () => {
    const user = userEvent.setup();
    const events = vi.fn().mockResolvedValue([
      { id: "e1", kind: "status.changed", axis: "agent", oldValue: "unavailable", newValue: "connected", detail: null, occurredAt: "2026-10-04T10:00:00Z" },
    ]);
    render(<ServerDetailView api={api({ events })} id="s1" />);
    await screen.findByRole("heading", { name: "edge-1" });
    await user.click(screen.getByRole("tab", { name: "Events" }));
    const list = await screen.findByRole("list", { name: "Server events" });
    expect(list).toHaveTextContent("status.changed");
    expect(list).toHaveTextContent("agent");
    expect(list).toHaveTextContent("connected");
  });

  it("restores the tab from the URL hash", async () => {
    window.location.hash = "#settings";
    render(<ServerDetailView api={api({ listJoinTokens: vi.fn().mockResolvedValue([]) })} id="s1" />);
    expect(await screen.findByRole("tab", { name: "Settings", selected: true })).toBeInTheDocument();
    expect(await screen.findByText("Agent join tokens")).toBeInTheDocument();
  });
});
