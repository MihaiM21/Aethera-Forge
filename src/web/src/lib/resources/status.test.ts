import { describe, expect, it } from "vitest";
import { deploymentDuration, deploymentPill, formatDuration, isDeploymentActive, repoLabel, shortSha, slugify, workloadPill } from "./status";

describe("status helpers", () => {
  it("maps workload and deployment states to pills without inventing a state", () => {
    expect(workloadPill("running")).toEqual({ status: "running" });
    expect(workloadPill("notDeployed")).toMatchObject({ label: "Not deployed" });
    expect(workloadPill("unknown")).toMatchObject({ label: "Unknown" });
    expect(deploymentPill("inProgress")).toMatchObject({ status: "deploying" });
    expect(deploymentPill("superseded")).toMatchObject({ status: "stopped", label: "Superseded" });
    expect(deploymentPill("cancelled")).toEqual({ status: "cancelled" });
  });

  it("formats durations from milliseconds up to hours", () => {
    expect(formatDuration(850)).toBe("850ms");
    expect(formatDuration(42_000)).toBe("42s");
    expect(formatDuration(83_000)).toBe("1m 23s");
    expect(formatDuration(3_900_000)).toBe("1h 05m");
    expect(formatDuration(null)).toBe("-");
  });

  it("ticks a running deployment live but trusts the stored duration once it finished", () => {
    const now = new Date("2026-10-04T10:01:00Z");
    expect(deploymentDuration({ durationMs: null, startedAt: "2026-10-04T10:00:00Z", finishedAt: null }, now)).toBe("1m 00s");
    expect(deploymentDuration({ durationMs: 5000, startedAt: "2026-10-04T10:00:00Z", finishedAt: "2026-10-04T10:00:05Z" }, now)).toBe("5s");
    expect(deploymentDuration({ durationMs: null, startedAt: null, finishedAt: null }, now)).toBe("-");
  });

  it("knows which deployments can still change", () => {
    expect(isDeploymentActive("queued")).toBe(true);
    expect(isDeploymentActive("inProgress")).toBe(true);
    expect(isDeploymentActive("running")).toBe(false);
    expect(isDeploymentActive("failed")).toBe(false);
  });

  it("shortens shas, repositories and names", () => {
    expect(shortSha("0123456789abcdef")).toBe("0123456");
    expect(shortSha(null)).toBe("-");
    expect(repoLabel("https://github.com/acme/web.git")).toBe("acme/web");
    expect(repoLabel("git@github.com:acme/web.git")).toBe("acme/web");
    expect(repoLabel(null)).toBe("-");
    expect(slugify("  My Cool App!! ")).toBe("my-cool-app");
    expect(slugify("x".repeat(100))).toHaveLength(63);
  });
});
