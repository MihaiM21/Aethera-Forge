import { describe, expect, it } from "vitest";
import { axis, makeHealth, makeServer } from "@/test/servers-fixtures";
import { blockedByText, describeAxis, describeHealth, describeServerStatus, healthTone, staleText } from "./status";

const NOW = new Date("2026-10-04T10:30:00.000Z");

describe("status axes display", () => {
  it("keeps the four axes separate and in order", () => {
    const views = describeHealth(makeHealth(), NOW);
    expect(views.map((v) => v.key)).toEqual(["server", "agent", "docker", "application"]);
    expect(views.map((v) => v.text)).toEqual(["reachable", "connected", "running", "healthy"]);
  });

  it("never merges axes into one: an unreachable server leaves agent and docker as the API reported them", () => {
    const health = makeHealth({
      server: axis("server", "unavailable", { detail: "The reachability probe fails." }),
      agent: axis("agent", "unknown", { blockedBy: "server" }),
      docker: axis("docker", "unknown", { blockedBy: "server" }),
      application: axis("application", "unknown", { blockedBy: "server", stale: true }),
      firstFailingLayer: "server",
    });
    const [server, agent, docker, app] = describeHealth(health, NOW);
    expect(server.tone).toBe("danger");
    // Blocked axes are neutral (we do not know), never red: they are not the failing layer.
    expect(agent.tone).toBe("idle");
    expect(agent.blockedBy).toBe("blocked by server");
    expect(docker.blockedBy).toBe("blocked by server");
    expect(app.blockedBy).toBe("blocked by server");
    expect(app.stale).toMatch(/^stale/);
  });

  it("formats blockedBy only when the API sent it", () => {
    expect(blockedByText(axis("agent", "available"))).toBeNull();
    expect(blockedByText(axis("docker", "unknown", { blockedBy: "agent" }))).toBe("blocked by agent");
  });

  it("formats 'stale since' from the axis timestamp", () => {
    const stale = axis("application", "unknown", { stale: true, since: "2026-10-04T10:00:00.000Z" });
    expect(staleText(stale, NOW)).toBe("stale since 30m ago");
    expect(staleText(axis("application", "available"), NOW)).toBeNull();
    expect(staleText(axis("application", "unknown", { stale: true, since: null as never }), NOW)).toBe("stale");
  });

  it("maps health to tones", () => {
    expect(healthTone("available")).toBe("success");
    expect(healthTone("unavailable")).toBe("danger");
    expect(healthTone("notInstalled")).toBe("idle");
    expect(healthTone("unknown")).toBe("idle");
    expect(healthTone("available", true)).toBe("idle");
  });

  it("uses axis specific words", () => {
    expect(describeAxis("agent", axis("agent", "notInstalled")).text).toBe("not installed");
    expect(describeAxis("server", axis("server", "unavailable")).text).toBe("unreachable");
  });

  it("describes the list-level statuses (server, agent, docker) from ServerResponse", () => {
    const views = describeServerStatus(
      makeServer({
        status: {
          reachability: { status: "unreachable", checkedAt: null, changedAt: null, probePort: null },
          agent: { status: "unavailable", lastHeartbeatAt: null, changedAt: null },
          docker: { status: "permissionDenied", changedAt: null },
        } as never,
      }),
    );
    expect(views.map((v) => [v.key, v.text, v.tone])).toEqual([
      ["server", "unreachable", "danger"],
      ["agent", "unavailable", "danger"],
      ["docker", "permissionDenied", "danger"],
    ]);
  });
});
