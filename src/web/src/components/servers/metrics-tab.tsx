"use client";

import * as React from "react";
import { LineChart, type ChartSeries } from "@/components/aethera/line-chart";
import { Skeleton } from "@/components/ui/skeleton";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { formatBytes, formatPercent, formatRate, ratioPercent, toDate, toNum } from "@/lib/servers/format";
import type { MetricPoint } from "@/lib/servers/types";
import { usePolled } from "@/lib/servers/use-polled";
import { cn } from "@/lib/utils";
import { ErrorPanel } from "./common";

export const RANGES = [
  { key: "15m", label: "15m", ms: 15 * 60_000 },
  { key: "1h", label: "1h", ms: 60 * 60_000 },
  { key: "6h", label: "6h", ms: 6 * 60 * 60_000 },
  { key: "24h", label: "24h", ms: 24 * 60 * 60_000 },
  { key: "7d", label: "7d", ms: 7 * 24 * 60 * 60_000 },
  { key: "30d", label: "30d", ms: 30 * 24 * 60 * 60_000 },
] as const;
export type RangeKey = (typeof RANGES)[number]["key"];

/** Chart-ready series from API points. Pure; the API already aggregated and rate-derived the values. */
export function toSeries(points: MetricPoint[]) {
  const t = (p: MetricPoint) => toDate(p.timestamp)?.getTime() ?? NaN;
  const valid = points.filter((p) => !Number.isNaN(t(p)));
  return {
    cpu: valid.map((p) => ({ t: t(p), v: toNum(p.cpuPercent) })),
    mem: valid.map((p) => ({ t: t(p), v: ratioPercent(p.memoryUsedBytes, p.memoryTotalBytes) })),
    disk: valid.map((p) => ({ t: t(p), v: ratioPercent(p.diskUsedBytes, p.diskTotalBytes) })),
    rx: valid.map((p) => ({ t: t(p), v: toNum(p.netRxBytesPerSecond) })),
    tx: valid.map((p) => ({ t: t(p), v: toNum(p.netTxBytesPerSecond) })),
    load: valid.map((p) => ({ t: t(p), v: toNum(p.load1) })),
  };
}

export function MetricsTab({
  serverId,
  api = serversApi,
  pollMs = 15_000,
}: {
  serverId: string;
  api?: ServersApi;
  pollMs?: number;
}) {
  const [range, setRange] = React.useState<RangeKey>("1h");
  const spec = RANGES.find((r) => r.key === range)!;

  const series = usePolled(
    (signal) => {
      const to = new Date();
      const from = new Date(to.getTime() - spec.ms);
      return api.metrics(serverId, { from: from.toISOString(), to: to.toISOString(), resolution: "auto", maxPoints: 300 }, { signal });
    },
    `${serverId}:${range}`,
    { intervalMs: pollMs },
  );
  const latest = usePolled((signal) => api.latestMetrics(serverId, { signal }), serverId, { intervalMs: pollMs });

  const s = React.useMemo(() => toSeries(series.data?.points ?? []), [series.data]);
  const fmtX = React.useCallback(
    (t: number) =>
      spec.ms > 24 * 60 * 60_000
        ? new Date(t).toLocaleDateString(undefined, { month: "short", day: "numeric" })
        : new Date(t).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" }),
    [spec.ms],
  );

  const percent = (key: string, label: string, color: string, pts: ChartSeries["points"]): ChartSeries => ({ key, label, color, points: pts });
  const host = latest.data?.host;

  return (
    <div className="grid gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div role="radiogroup" aria-label="Time range" className="inline-flex border border-border">
          {RANGES.map((r) => (
            <button
              key={r.key}
              type="button"
              role="radio"
              aria-checked={range === r.key}
              onClick={() => setRange(r.key)}
              className={cn(
                "focus-ring h-8 px-3 font-mono text-xs transition-colors",
                range === r.key ? "bg-primary text-primary-foreground" : "text-muted-foreground hover:bg-accent hover:text-foreground",
              )}
            >
              {r.label}
            </button>
          ))}
        </div>
        <p className="font-mono text-2xs text-muted-foreground" aria-live="polite">
          {series.data ? `resolution ${series.data.resolution} · ${series.data.points.length} points` : ""} · refreshes every{" "}
          {Math.round(pollMs / 1000)}s
        </p>
      </div>

      {latest.data?.stale && (
        <p role="status" className="border border-dashed border-warning/70 bg-warning-soft px-3 py-2 text-xs">
          No fresh sample from the agent. The charts show history; the live values are stale.
        </p>
      )}

      {series.error && !series.data ? (
        <ErrorPanel error={series.error} title="Could not load metrics" onRetry={() => void series.refresh()} />
      ) : !series.data ? (
        <div className="grid gap-4 lg:grid-cols-2" role="status" aria-label="Loading metrics">
          {[0, 1, 2, 3].map((i) => (
            <Skeleton key={i} className="h-52 w-full" />
          ))}
        </div>
      ) : (
        <div className="grid gap-4 lg:grid-cols-2">
          <LineChart title="CPU" yMax={100} formatY={(v) => formatPercent(v, 1)} formatX={fmtX} series={[percent("cpu", "cpu", "var(--chart-1)", s.cpu)]} />
          <LineChart
            title={host ? `Memory · ${formatBytes(host.memoryUsedBytes)} of ${formatBytes(host.memoryTotalBytes)} now` : "Memory"}
            yMax={100}
            formatY={(v) => formatPercent(v, 1)}
            formatX={fmtX}
            series={[percent("mem", "used", "var(--chart-2)", s.mem)]}
          />
          <LineChart
            title={host ? `Disk · ${formatBytes(host.diskUsedBytes)} of ${formatBytes(host.diskTotalBytes)} now` : "Disk"}
            yMax={100}
            formatY={(v) => formatPercent(v, 1)}
            formatX={fmtX}
            series={[percent("disk", "used", "var(--chart-3)", s.disk)]}
          />
          <LineChart
            title="Network"
            formatY={(v) => formatRate(v)}
            formatX={fmtX}
            series={[percent("rx", "rx", "var(--chart-6)", s.rx), percent("tx", "tx", "var(--chart-4)", s.tx)]}
          />
        </div>
      )}
    </div>
  );
}
