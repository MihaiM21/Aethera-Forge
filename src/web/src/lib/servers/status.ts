/**
 * Display helpers for the status axes (ADR 0002 failure model).
 *
 * The API derives everything (`AxisResponse.health`, `blockedBy`, `stale`,
 * `firstFailingLayer`); this module only turns those values into labels and
 * tones. It never combines the axes into one "online" flag.
 */
import { formatAgo, toDate } from "./format";
import type { Axis, AxisHealth, Server, ServerHealth } from "./types";

export type AxisKey = "server" | "agent" | "docker" | "application";
export type StatusTone = "success" | "warning" | "danger" | "info" | "idle";

export const AXIS_ORDER: readonly AxisKey[] = ["server", "agent", "docker", "application"];

export const AXIS_TITLE: Record<AxisKey, string> = {
  server: "Server",
  agent: "Agent",
  docker: "Docker",
  application: "Applications",
};

const HEALTH_WORDS: Record<AxisKey, Record<AxisHealth, string>> = {
  server: { available: "reachable", unavailable: "unreachable", unknown: "unknown", notInstalled: "not installed" },
  agent: { available: "connected", unavailable: "unavailable", unknown: "unknown", notInstalled: "not installed" },
  docker: { available: "running", unavailable: "unavailable", unknown: "unknown", notInstalled: "not installed" },
  application: { available: "healthy", unavailable: "unavailable", unknown: "unknown", notInstalled: "not installed" },
};

export function healthTone(health: AxisHealth, blocked = false): StatusTone {
  if (blocked) return "idle";
  switch (health) {
    case "available":
      return "success";
    case "unavailable":
      return "danger";
    default:
      return "idle";
  }
}

export function healthWord(axis: AxisKey, health: AxisHealth): string {
  return HEALTH_WORDS[axis][health] ?? health;
}

export function isAxisKey(v: string | null | undefined): v is AxisKey {
  return v === "server" || v === "agent" || v === "docker" || v === "application";
}

/** `blocked by server`, or null if the API did not attribute the axis to a lower layer. */
export function blockedByText(axis: Axis): string | null {
  return axis.blockedBy ? `blocked by ${axis.blockedBy}` : null;
}

/** `stale since 12m ago`, or null if the value is current. */
export function staleText(axis: Axis, now: Date = new Date()): string | null {
  if (!axis.stale) return null;
  const since = toDate(axis.since);
  return since ? `stale since ${formatAgo(since, now)}` : "stale";
}

export type AxisView = {
  key: AxisKey;
  title: string;
  /** `Agent: connected` style text; colour is never the only signal. */
  text: string;
  tone: StatusTone;
  blockedBy: string | null;
  stale: string | null;
  detail: string | null;
  health: AxisHealth;
};

export function describeAxis(key: AxisKey, axis: Axis, now: Date = new Date()): AxisView {
  const blocked = blockedByText(axis);
  const stale = staleText(axis, now);
  return {
    key,
    title: AXIS_TITLE[key],
    text: healthWord(key, axis.health),
    tone: healthTone(axis.health, axis.blockedBy != null),
    blockedBy: blocked,
    stale,
    detail: axis.detail,
    health: axis.health,
  };
}

export function describeHealth(health: ServerHealth, now: Date = new Date()): AxisView[] {
  return AXIS_ORDER.map((k) => describeAxis(k, health[k], now));
}

/**
 * The list endpoint returns only the three machine-level statuses
 * (reachability / agent / docker). Same wording, no `blockedBy` (that comes from
 * `GET /servers/{id}/status`).
 */
export type ListAxisView = { key: "server" | "agent" | "docker"; title: string; text: string; tone: StatusTone; since: unknown };

export function describeServerStatus(server: Server): ListAxisView[] {
  const { reachability, agent, docker } = server.status;
  const reach: AxisHealth =
    reachability.status === "reachable" ? "available" : reachability.status === "unreachable" ? "unavailable" : "unknown";
  const ag: AxisHealth =
    agent.status === "connected" ? "available" : agent.status === "unavailable" ? "unavailable" : agent.status === "notInstalled" ? "notInstalled" : "unknown";
  const dk: AxisHealth =
    docker.status === "running"
      ? "available"
      : docker.status === "notInstalled"
        ? "notInstalled"
        : docker.status === "unknown"
          ? "unknown"
          : "unavailable";
  return [
    { key: "server", title: AXIS_TITLE.server, text: healthWord("server", reach), tone: healthTone(reach), since: reachability.changedAt },
    { key: "agent", title: AXIS_TITLE.agent, text: healthWord("agent", ag), tone: healthTone(ag), since: agent.changedAt },
    {
      key: "docker",
      title: AXIS_TITLE.docker,
      // Keep the daemon's own wording for "stopped" / "permissionDenied" instead of flattening it.
      text: docker.status === "running" || docker.status === "unknown" || docker.status === "notInstalled" ? healthWord("docker", dk) : docker.status,
      tone: healthTone(dk),
      since: docker.changedAt,
    },
  ];
}
