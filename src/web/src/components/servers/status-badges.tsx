import * as React from "react";
import { StatusDot, type Tone } from "@/components/aethera/status-dot";
import { Badge } from "@/components/ui/badge";
import {
  describeServerStatus,
  describeHealth,
  type AxisView,
  type ListAxisView,
  type StatusTone,
} from "@/lib/servers/status";
import type { Server, ServerHealth } from "@/lib/servers/types";
import { toNum } from "@/lib/servers/format";
import { cn } from "@/lib/utils";

const BADGE_TONE: Record<StatusTone, "success" | "warning" | "danger" | "info" | "neutral"> = {
  success: "success",
  warning: "warning",
  danger: "danger",
  info: "info",
  idle: "neutral",
};

const DOT_TONE: Record<StatusTone, Tone> = {
  success: "success",
  warning: "warning",
  danger: "danger",
  info: "info",
  idle: "idle",
};

/** One axis as a badge: dot + `Agent connected`. The text carries the state, not only the colour. */
export function AxisBadge({
  title,
  text,
  tone,
  hint,
  className,
}: {
  title: string;
  text: string;
  tone: StatusTone;
  /** Tooltip / extra context (e.g. `blocked by server`). */
  hint?: string | null;
  className?: string;
}) {
  return (
    <Badge
      tone={BADGE_TONE[tone]}
      title={hint ?? undefined}
      data-axis={title.toLowerCase()}
      data-health={text}
      className={cn("normal-case tracking-normal", className)}
    >
      <StatusDot tone={DOT_TONE[tone]} live={tone === "success" && title === "Agent"} hollow={tone === "idle"} />
      <span className="uppercase tracking-[0.06em]">{title}</span>
      <span className="text-foreground/80">{text}</span>
    </Badge>
  );
}

export function AxisViewBadge({ view }: { view: AxisView | ListAxisView }) {
  const hint =
    "blockedBy" in view
      ? [view.blockedBy, view.stale, view.detail].filter(Boolean).join(" · ") || null
      : null;
  return <AxisBadge title={view.title} text={view.text} tone={view.tone} hint={hint} />;
}

/**
 * The status axes as separate badges (ADR 0002: never a single "online"). The list
 * endpoint carries the three machine-level statuses; the application axis comes
 * from `GET /servers/{id}/status` when it has loaded, otherwise only the
 * workload count is shown.
 */
export function ServerStatusBadges({
  server,
  health,
  className,
}: {
  server: Server;
  health?: ServerHealth | null;
  className?: string;
}) {
  const machine = describeServerStatus(server);
  const app = health ? describeHealth(health).find((v) => v.key === "application") : null;
  const count = toNum(server.workloadCount) ?? 0;
  return (
    <div className={cn("flex flex-wrap gap-1.5", className)} role="group" aria-label="Status axes">
      {machine.map((v) => (
        <AxisViewBadge key={v.key} view={v} />
      ))}
      {app ? (
        <AxisBadge
          title="Apps"
          text={
            health && toNum(health.applications.total)
              ? `${toNum(health.applications.healthy)}/${toNum(health.applications.total)} healthy`
              : app.text
          }
          tone={app.tone}
          hint={[app.blockedBy, app.stale, app.detail].filter(Boolean).join(" · ") || null}
        />
      ) : (
        <AxisBadge title="Apps" text={count === 0 ? "none" : `${count} workload${count === 1 ? "" : "s"}`} tone="idle" />
      )}
    </div>
  );
}
