import * as React from "react";
import { cn } from "@/lib/utils";

export type Tone = "success" | "warning" | "danger" | "info" | "idle" | "brand";

const toneVar: Record<Tone, string> = {
  success: "var(--success)",
  warning: "var(--warning)",
  danger: "var(--danger)",
  info: "var(--info)",
  idle: "var(--muted-foreground)",
  brand: "var(--ae-accent)",
};

/**
 * 8px status dot. `live` adds the pulse ring and is only for states that are
 * actually live (running, deploying, connected). `hollow` is for stopped /
 * cancelled. Colour is never the only signal: pass `label` (read by screen
 * readers) or put a text label next to it.
 */
export function StatusDot({
  tone = "success",
  live = false,
  hollow = false,
  label,
  className,
}: {
  tone?: Tone;
  live?: boolean;
  hollow?: boolean;
  label?: string;
  className?: string;
}) {
  const a11y = label ? { role: "img", "aria-label": label } : { "aria-hidden": true };
  if (live) {
    return (
      <span
        data-slot="status-dot"
        data-live="true"
        className={cn("ae-pulse", className)}
        data-tone={tone === "brand" ? undefined : tone}
        {...a11y}
      />
    );
  }
  return (
    <span
      data-slot="status-dot"
      className={cn(
        "inline-block size-2 shrink-0 rounded-full",
        hollow && "border",
        className,
      )}
      style={
        hollow
          ? { borderColor: toneVar[tone], background: "transparent" }
          : { background: toneVar[tone] }
      }
      {...a11y}
    />
  );
}
