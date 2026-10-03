import * as React from "react";
import { StatusDot, type Tone } from "@/components/aethera/status-dot";
import { cn } from "@/lib/utils";
import { badgeVariants } from "./badge";

/**
 * Domain state -> tone / dot mapping from docs/design/tokens.md section 2.3.
 * Pulse only for live states; stopped and cancelled are hollow.
 */
export const STATUS_MAP = {
  running: { tone: "success", live: true, label: "Running" },
  healthy: { tone: "success", live: true, label: "Healthy" },
  online: { tone: "success", live: false, label: "Online" },
  succeeded: { tone: "success", live: false, label: "Succeeded" },
  deploying: { tone: "info", live: true, label: "Deploying" },
  building: { tone: "info", live: true, label: "Building" },
  queued: { tone: "idle", live: false, label: "Queued" },
  degraded: { tone: "warning", live: false, label: "Degraded" },
  unhealthy: { tone: "warning", live: false, label: "Unhealthy" },
  docker_unavailable: { tone: "warning", live: false, label: "Docker unavailable" },
  failed: { tone: "danger", live: false, label: "Failed" },
  offline: { tone: "danger", live: false, label: "Offline" },
  agent_unavailable: { tone: "danger", live: false, label: "Agent unavailable" },
  server_unavailable: { tone: "danger", live: false, label: "Server unavailable" },
  stopped: { tone: "idle", live: false, hollow: true, label: "Stopped" },
  cancelled: { tone: "idle", live: false, hollow: true, label: "Cancelled" },
} as const satisfies Record<
  string,
  { tone: Tone; live: boolean; hollow?: boolean; label: string }
>;

export type StatusKey = keyof typeof STATUS_MAP;

const badgeTone = {
  success: "success",
  info: "info",
  warning: "warning",
  danger: "danger",
  idle: "neutral",
  brand: "brand",
} as const;

export function StatusPill({
  status,
  label,
  className,
}: {
  status: StatusKey;
  /** Override the default text. */
  label?: string;
  className?: string;
}) {
  const s: { tone: Tone; live: boolean; hollow?: boolean; label: string } =
    STATUS_MAP[status];
  return (
    <span
      data-slot="status-pill"
      data-status={status}
      className={cn(badgeVariants({ tone: badgeTone[s.tone] }), className)}
    >
      <StatusDot tone={s.tone} live={s.live} hollow={s.hollow} />
      {label ?? s.label}
    </span>
  );
}
