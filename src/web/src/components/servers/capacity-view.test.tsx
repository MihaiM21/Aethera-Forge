import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { fakeApi } from "@/test/servers-fixtures";
import type { ServerCapacity } from "@/lib/servers/types";
import { CapacityView, ServerCapacityCard } from "./capacity-view";

const GIB = 1024 ** 3;

function capacity(over: Partial<ServerCapacity>): ServerCapacity {
  return {
    serverId: "s1",
    name: "worker-1",
    lifecycle: "active",
    roles: ["worker"],
    connected: true,
    cores: 4,
    memoryBytes: 8 * GIB,
    diskBytes: 100 * GIB,
    usage: null,
    reservations: { cpuCores: 0, memoryBytes: 0 },
    limits: { cpuCores: 0, memoryBytes: 0 },
    committed: { cpuCores: 0, memoryBytes: 0 },
    free: { cpuCores: 4, memoryBytes: 8 * GIB, diskBytes: null },
    workloadCount: 0,
    overCommitted: false,
    warnings: [],
    ...over,
  } as ServerCapacity;
}

describe("CapacityView", () => {
  it("shows every server with its roles, what is left and the over-commit warning", async () => {
    const api = fakeApi({
      capacity: vi.fn().mockResolvedValue([
        capacity({ serverId: "s1", name: "worker-1" }),
        capacity({
          serverId: "s2", name: "crowded", roles: [], connected: false, overCommitted: true, workloadCount: 3,
          free: { cpuCores: -1, memoryBytes: -GIB, diskBytes: null } as ServerCapacity["free"],
          warnings: ["Memory reservations add up to 9 GiB on a server with 8 GiB."],
        }),
      ]),
    });
    render(<CapacityView api={api} pollMs={60_000} />);

    expect(await screen.findByRole("article", { name: "Capacity of worker-1" })).toHaveTextContent("8.0 GiB memory and 4 cores unreserved");
    const crowded = screen.getByRole("article", { name: "Capacity of crowded" });
    expect(crowded).toHaveAttribute("data-over-committed", "true");
    expect(crowded).toHaveTextContent("over-committed");
    expect(crowded).toHaveTextContent("not connected");
    expect(crowded).toHaveTextContent("no roles: takes anything");
    expect(crowded).toHaveTextContent("3 workloads");
    expect(crowded).toHaveTextContent("Memory reservations add up to 9 GiB");
  });

  it("says when there are no servers and explains a failure", async () => {
    const { unmount } = render(<CapacityView api={fakeApi({ capacity: vi.fn().mockResolvedValue([]) })} pollMs={60_000} />);
    expect(await screen.findByText("No servers yet.")).toBeInTheDocument();
    unmount();
    render(<CapacityView api={fakeApi({ capacity: vi.fn().mockRejectedValue(new Error("boom")) })} pollMs={60_000} />);
    expect(await screen.findByText("Could not load capacity")).toBeInTheDocument();
  });
});

describe("ServerCapacityCard", () => {
  it("shows the card of one server and nothing for an unknown one", async () => {
    const api = fakeApi({ capacity: vi.fn().mockResolvedValue([capacity({ serverId: "s1", name: "worker-1" })]) });
    const { container, rerender } = render(<ServerCapacityCard serverId="s1" api={api} pollMs={60_000} />);
    expect(await screen.findByRole("article", { name: "Capacity of worker-1" })).toBeInTheDocument();
    rerender(<ServerCapacityCard serverId="other" api={api} pollMs={60_000} />);
    expect(container.querySelector("article")).toBeNull();
  });
});
