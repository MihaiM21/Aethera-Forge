"use client";

import * as React from "react";
import { UsageBar } from "@/components/aethera/usage-bar";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { capacityMeters, describeFree } from "@/lib/servers/capacity";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { formatBytes, toNum } from "@/lib/servers/format";
import type { ServerCapacity } from "@/lib/servers/types";
import { usePolled } from "@/lib/servers/use-polled";
import { ErrorPanel } from "./common";

export const CAPACITY_POLL_MS = 15_000;

/** One server's capacity: what it has, what is in use now and what is promised to workloads. Over-commitment is a warning, never hidden. */
export function CapacityCard({ capacity: c, heading = true }: { capacity: ServerCapacity; heading?: boolean }) {
  const { usage, committed } = capacityMeters(c);
  const disk = toNum(c.free.diskBytes);
  return (
    <article aria-label={`Capacity of ${c.name}`} data-over-committed={c.overCommitted} className="grid content-start gap-3 border border-border bg-card p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        {heading ? <h3 className="font-medium">{c.name}</h3> : <span className="mono-label text-muted-foreground">Capacity</span>}
        <div className="flex flex-wrap gap-1.5">
          {c.roles.length === 0 ? <Badge tone="neutral">no roles: takes anything</Badge> : c.roles.map((r) => <Badge key={r} tone="neutral">{r}</Badge>)}
          {!c.connected && <Badge tone="warning">not connected</Badge>}
          {c.overCommitted && <Badge tone="danger">over-committed</Badge>}
        </div>
      </div>
      <p className="font-mono text-2xs text-muted-foreground">
        {toNum(c.cores) ?? "?"} cores · {formatBytes(c.memoryBytes)} memory · {formatBytes(c.diskBytes)} disk · {c.workloadCount}{" "}
        {c.workloadCount === 1 ? "workload" : "workloads"}
      </p>
      <div className="grid gap-1.5">
        <p className="mono-label text-muted-foreground">In use now</p>
        {usage.map((m) => (
          <UsageBar key={`u-${m.label}`} label={m.label} value={m.percent} detail={m.detail} />
        ))}
      </div>
      <div className="grid gap-1.5">
        <p className="mono-label text-muted-foreground">Reserved by workloads</p>
        {committed.map((m) => (
          <UsageBar key={`c-${m.label}`} label={m.label} value={m.percent} detail={m.detail} />
        ))}
      </div>
      <p className="text-xs text-muted-foreground">
        {describeFree(c)}
        {disk !== null ? ` · ${formatBytes(disk)} disk free` : ""}. Limits add up to {toNum(c.limits.cpuCores) ?? 0} cores and {formatBytes(c.limits.memoryBytes)}.
      </p>
      {c.warnings.map((w) => (
        <p key={w} role="status" className="border border-warning/60 bg-warning-soft px-3 py-2 text-xs text-warning">
          {w}
        </p>
      ))}
    </article>
  );
}

/** Capacity of every server side by side (the servers page). */
export function CapacityView({ api = serversApi, pollMs = CAPACITY_POLL_MS }: { api?: ServersApi; pollMs?: number }) {
  const { data, error, loading, refresh } = usePolled((signal) => api.capacity({ signal }), "capacity", { intervalMs: pollMs });
  if (error && !data) return <ErrorPanel error={error} title="Could not load capacity" onRetry={() => void refresh()} />;
  if (loading && !data) return <Skeleton className="h-40 w-full" />;
  if (!data || data.length === 0) return <p className="text-sm text-muted-foreground">No servers yet.</p>;
  return (
    <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3" aria-label="Server capacity">
      {data.map((c) => (
        <CapacityCard key={c.serverId} capacity={c} />
      ))}
    </div>
  );
}

/** The capacity of one server (the overview tab). Renders nothing when the server is not in the list. */
export function ServerCapacityCard({ serverId, api = serversApi, pollMs = CAPACITY_POLL_MS }: { serverId: string; api?: ServersApi; pollMs?: number }) {
  const { data } = usePolled((signal) => api.capacity({ signal }), "capacity", { intervalMs: pollMs });
  const mine = data?.find((c) => c.serverId === serverId);
  return mine ? <CapacityCard capacity={mine} heading={false} /> : null;
}
