"use client";

import * as React from "react";
import { cn } from "@/lib/utils";

export type ChartPoint = { t: number; v: number | null };
export type ChartSeries = {
  key: string;
  label: string;
  /** CSS colour, normally a chart token: `var(--chart-1)`. */
  color: string;
  points: ChartPoint[];
};

const W = 640;
const H = 160;
const PAD = { l: 4, r: 4, t: 8, b: 8 };

/** Break the line at null values so gaps stay visible. */
export function buildPaths(
  points: ChartPoint[],
  x: (t: number) => number,
  y: (v: number) => number,
): string {
  let d = "";
  let pen = false;
  for (const p of points) {
    if (p.v === null) {
      pen = false;
      continue;
    }
    d += `${pen ? "L" : "M"}${x(p.t).toFixed(1)} ${y(p.v).toFixed(1)}`;
    pen = true;
  }
  return d;
}

/**
 * Dependency-free line chart on the design tokens: 1px dashed grid, square
 * markers, chart palette (`--chart-N`). The y axis starts at 0; pass `yMax` for
 * bounded series (percentages). Hover or arrow keys read exact values.
 */
export function LineChart({
  title,
  series,
  yMax,
  formatY,
  formatX,
  emptyLabel = "No samples in this range",
  className,
}: {
  title: string;
  series: ChartSeries[];
  yMax?: number;
  formatY: (v: number) => string;
  formatX?: (t: number) => string;
  emptyLabel?: string;
  className?: string;
}) {
  const [hover, setHover] = React.useState<number | null>(null);
  const ref = React.useRef<SVGSVGElement>(null);

  const all = series.flatMap((s) => s.points).filter((p) => p.v !== null) as Array<{ t: number; v: number }>;
  const hasData = all.length > 0;
  const tMin = hasData ? Math.min(...all.map((p) => p.t)) : 0;
  const tMax = hasData ? Math.max(...all.map((p) => p.t)) : 1;
  const vMax = yMax ?? (hasData ? Math.max(...all.map((p) => p.v)) * 1.1 || 1 : 1);
  const x = (t: number) => PAD.l + ((t - tMin) / Math.max(1, tMax - tMin)) * (W - PAD.l - PAD.r);
  const y = (v: number) => PAD.t + (1 - Math.min(v, vMax) / vMax) * (H - PAD.t - PAD.b);

  // Union of timestamps for hover lookup.
  const times = React.useMemo(
    () => Array.from(new Set(series.flatMap((s) => s.points.map((p) => p.t)))).sort((a, b) => a - b),
    [series],
  );
  const fmtX = formatX ?? ((t: number) => new Date(t).toLocaleTimeString());

  function indexFromPointer(clientX: number): number | null {
    const el = ref.current;
    if (!el || times.length === 0) return null;
    const rect = el.getBoundingClientRect();
    const ratio = Math.min(1, Math.max(0, (clientX - rect.left) / Math.max(1, rect.width)));
    const t = tMin + ratio * (tMax - tMin);
    let best = 0;
    for (let i = 1; i < times.length; i++) if (Math.abs(times[i] - t) < Math.abs(times[best] - t)) best = i;
    return best;
  }

  const hoverT = hover !== null ? times[hover] : null;
  const latest = series.map((s) => {
    const last = [...s.points].reverse().find((p) => p.v !== null);
    return { s, v: last?.v ?? null };
  });
  const summary = latest.map(({ s, v }) => `${s.label} ${v === null ? "no data" : formatY(v)}`).join(", ");

  return (
    <figure data-slot="line-chart" className={cn("grid gap-2 border border-border bg-card p-3", className)}>
      <figcaption className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <span className="font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">{title}</span>
        <ul className="flex flex-wrap gap-x-4 gap-y-1 font-mono text-2xs">
          {latest.map(({ s, v }) => {
            const shown =
              hoverT !== null ? (s.points.find((p) => p.t === hoverT)?.v ?? null) : v;
            return (
              <li key={s.key} className="flex items-center gap-1.5">
                <span aria-hidden="true" className="inline-block size-2" style={{ background: s.color }} />
                <span className="text-muted-foreground">{s.label}</span>
                <span className="text-foreground tabular-nums">{shown === null ? "-" : formatY(shown)}</span>
              </li>
            );
          })}
        </ul>
      </figcaption>

      <div className="relative">
        <svg
          ref={ref}
          viewBox={`0 0 ${W} ${H}`}
          role="img"
          aria-label={`${title}: ${hasData ? summary : emptyLabel}`}
          tabIndex={hasData ? 0 : -1}
          className="focus-ring block h-auto w-full touch-pan-y"
          onPointerMove={(e) => setHover(indexFromPointer(e.clientX))}
          onPointerLeave={() => setHover(null)}
          onBlur={() => setHover(null)}
          onKeyDown={(e) => {
            if (times.length === 0) return;
            if (e.key === "ArrowLeft") setHover((h) => Math.max(0, (h ?? times.length) - 1));
            else if (e.key === "ArrowRight") setHover((h) => Math.min(times.length - 1, (h ?? -1) + 1));
            else if (e.key === "Escape") setHover(null);
            else return;
            e.preventDefault();
          }}
        >
          {[0, 0.25, 0.5, 0.75, 1].map((f) => {
            const gy = PAD.t + f * (H - PAD.t - PAD.b);
            return (
              <line
                key={f}
                x1={0}
                x2={W}
                y1={gy}
                y2={gy}
                stroke="var(--ae-chart-grid)"
                strokeDasharray={f === 1 ? undefined : "2 4"}
                vectorEffect="non-scaling-stroke"
              />
            );
          })}
          {hasData &&
            series.map((s) => (
              <path
                key={s.key}
                d={buildPaths(s.points, x, y)}
                fill="none"
                stroke={s.color}
                strokeWidth={1.5}
                strokeLinejoin="round"
                vectorEffect="non-scaling-stroke"
              />
            ))}
          {hasData && hoverT !== null && (
            <>
              <line
                x1={x(hoverT)}
                x2={x(hoverT)}
                y1={0}
                y2={H}
                stroke="var(--ae-chart-axis)"
                strokeDasharray="3 3"
                vectorEffect="non-scaling-stroke"
              />
              {series.map((s) => {
                const p = s.points.find((q) => q.t === hoverT);
                return p && p.v !== null ? (
                  <rect key={s.key} x={x(p.t) - 3} y={y(p.v) - 3} width={6} height={6} fill={s.color} />
                ) : null;
              })}
            </>
          )}
        </svg>
        {!hasData && (
          <div className="absolute inset-0 flex items-center justify-center font-mono text-2xs text-muted-foreground">
            {emptyLabel}
          </div>
        )}
      </div>

      <div className="flex items-center justify-between font-mono text-2xs text-muted-foreground">
        <span>{hasData ? (hoverT !== null ? fmtX(hoverT) : fmtX(tMin)) : " "}</span>
        <span>{hasData ? `max ${formatY(vMax)}` : " "}</span>
        <span>{hasData && hoverT === null ? fmtX(tMax) : " "}</span>
      </div>
    </figure>
  );
}
