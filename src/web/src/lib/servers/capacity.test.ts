import { describe, expect, it } from "vitest";
import { capacityMeters, describeFree, overCommitWarning } from "./capacity";
import type { ServerCapacity } from "./types";

const GIB = 1024 ** 3;

function capacity(over: Partial<ServerCapacity> = {}): ServerCapacity {
  return {
    serverId: "s1",
    name: "worker-1",
    lifecycle: "active",
    roles: ["worker"],
    connected: true,
    cores: 4,
    memoryBytes: 8 * GIB,
    diskBytes: 100 * GIB,
    usage: { cpuPercent: 25, memoryUsedBytes: 2 * GIB, memoryTotalBytes: 8 * GIB, diskUsedBytes: 30 * GIB, diskTotalBytes: 100 * GIB, observedAt: "2026-10-30T10:00:00.000Z" },
    reservations: { cpuCores: 1, memoryBytes: 3 * GIB },
    limits: { cpuCores: 2, memoryBytes: 4 * GIB },
    committed: { cpuCores: 1.5, memoryBytes: 4 * GIB },
    free: { cpuCores: 2.5, memoryBytes: 4 * GIB, diskBytes: 70 * GIB },
    workloadCount: 2,
    overCommitted: false,
    warnings: [],
    ...over,
  } as ServerCapacity;
}

describe("capacityMeters", () => {
  it("shows live usage and the committed share", () => {
    const { usage, committed } = capacityMeters(capacity());
    expect(usage.map((m) => [m.label, m.percent])).toEqual([["CPU", 25], ["MEM", 25], ["DSK", 30]]);
    expect(committed[0].percent).toBeCloseTo(37.5);
    expect(committed[1].percent).toBe(50);
    expect(committed[1].detail).toBe("4.0 GiB reserved of 8.0 GiB");
  });

  it("has no percentages without a sample or without the server's size", () => {
    const { usage, committed } = capacityMeters(capacity({ usage: null, cores: null, memoryBytes: null }));
    expect(usage.every((m) => m.percent === null && m.detail === "no sample yet")).toBe(true);
    expect(committed.every((m) => m.percent === null)).toBe(true);
  });
});

describe("describeFree", () => {
  it("says what is left", () => expect(describeFree(capacity())).toBe("4.0 GiB memory and 2.5 cores unreserved"));
  it("never shows a negative amount", () =>
    expect(describeFree(capacity({ free: { cpuCores: -1, memoryBytes: -GIB, diskBytes: null } as ServerCapacity["free"] }))).toBe("0 B memory and 0 cores unreserved"));
  it("says when the size is unknown", () =>
    expect(describeFree(capacity({ free: { cpuCores: null, memoryBytes: null, diskBytes: null } as ServerCapacity["free"] }))).toBe("size not known yet"));
});

describe("overCommitWarning", () => {
  it("is null when the workload fits", () => expect(overCommitWarning(capacity(), { memoryBytes: 2 * GIB, cpuCores: 1 })).toBeNull());

  it("warns when memory or CPU would be over-committed", () => {
    expect(overCommitWarning(capacity(), { memoryBytes: 6 * GIB })).toContain("over-committed");
    expect(overCommitWarning(capacity(), { cpuCores: 3 })).toContain("cores unreserved");
  });

  it("does not count the workload's own reservation when it is already there", () => {
    expect(overCommitWarning(capacity(), { memoryBytes: 6 * GIB }, { memoryBytes: 3 * GIB })).toBeNull();
  });

  it("is silent when the server's size is unknown", () =>
    expect(overCommitWarning(capacity({ free: { cpuCores: null, memoryBytes: null, diskBytes: null } as ServerCapacity["free"] }), { memoryBytes: 64 * GIB })).toBeNull());
});
