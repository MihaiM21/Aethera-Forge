import * as React from "react";
import { cn } from "@/lib/utils";

/**
 * Mini resource meter: mono label, square 1px-bordered track, lime fill and the
 * value as text (colour is never the only signal). `value` is 0..100 or null
 * when the API has no sample.
 */
export function UsageBar({
  label,
  value,
  detail,
  className,
}: {
  label: string;
  value: number | null;
  /** Extra text after the percentage, e.g. `3.1 GiB / 8 GiB`. Exposed as the tooltip and to screen readers. */
  detail?: string;
  className?: string;
}) {
  const known = value !== null && Number.isFinite(value);
  const pct = known ? Math.min(100, Math.max(0, value)) : 0;
  const tone = !known ? "" : pct >= 90 ? "bg-danger" : pct >= 75 ? "bg-warning" : "bg-primary";
  return (
    <div
      data-slot="usage-bar"
      role="meter"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={known ? Math.round(pct) : undefined}
      aria-valuetext={known ? `${Math.round(pct)}%${detail ? `, ${detail}` : ""}` : "no data"}
      title={detail}
      className={cn("grid grid-cols-[2rem_1fr_2.5rem] items-center gap-2 font-mono text-2xs", className)}
    >
      <span className="uppercase tracking-[0.06em] text-muted-foreground">{label}</span>
      <span className="relative h-1.5 border border-border bg-muted" aria-hidden="true">
        {known ? (
          <span className={cn("absolute inset-y-0 left-0", tone)} style={{ width: `${pct}%` }} />
        ) : (
          <span className="absolute inset-x-0 top-1/2 border-t border-dashed border-border-strong" />
        )}
      </span>
      <span className="text-right tabular-nums text-foreground">{known ? `${Math.round(pct)}%` : "-"}</span>
    </div>
  );
}
