"use client";

import * as React from "react";
import { PlusIcon, RefreshCwIcon, SearchIcon, ServerIcon } from "lucide-react";
import Link from "next/link";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { PageHeader } from "@/components/aethera/page-header";
import { UsageBar } from "@/components/aethera/usage-bar";
import { DetailLink } from "@/components/shell/detail-link";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { formatAgo, formatBytes, ratioPercent, toDate, toNum } from "@/lib/servers/format";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { describeHealth } from "@/lib/servers/status";
import type { LatestMetrics, Server, ServerHealth } from "@/lib/servers/types";
import { usePolled } from "@/lib/servers/use-polled";
import { ErrorPanel, NativeSelect } from "./common";
import { ServerStatusBadges } from "./status-badges";

export const LIST_POLL_MS = 10_000;

export type ServerRowData = {
  server: Server;
  /** `GET /servers/{id}/status`; undefined if that call failed (the row still renders). */
  health?: ServerHealth;
  /** `GET /servers/{id}/metrics/latest`. */
  metrics?: LatestMetrics;
};

async function loadRows(api: ServersApi, signal: AbortSignal): Promise<{ rows: ServerRowData[]; more: boolean }> {
  const page = await api.list({ limit: 100, sort: "name" }, { signal });
  // The list carries no application health and no metrics, so each row asks for them. Failures are per row.
  const rows = await Promise.all(
    page.items.map(async (server): Promise<ServerRowData> => {
      const [health, metrics] = await Promise.allSettled([
        api.status(server.id, { signal }),
        api.latestMetrics(server.id, { signal }),
      ]);
      return {
        server,
        health: health.status === "fulfilled" ? health.value : undefined,
        metrics: metrics.status === "fulfilled" ? metrics.value : undefined,
      };
    }),
  );
  return { rows, more: page.nextCursor !== null };
}

export function matchesFilter(row: ServerRowData, query: string, agent: string): boolean {
  const q = query.trim().toLowerCase();
  if (q) {
    const hay = [row.server.name, row.server.host, row.server.publicIp ?? "", ...row.server.roles].join(" ").toLowerCase();
    if (!hay.includes(q)) return false;
  }
  if (agent !== "all" && row.server.status.agent.status !== agent) return false;
  return true;
}

function UsageCell({ metrics }: { metrics?: LatestMetrics }) {
  const host = metrics?.host ?? null;
  const stale = !host || metrics?.stale;
  return (
    <div className={stale ? "opacity-60" : undefined} aria-label={stale ? "Resource usage (stale)" : "Resource usage"}>
      <div className="grid gap-1">
        <UsageBar label="CPU" value={toNum(host?.cpuPercent)} />
        <UsageBar
          label="RAM"
          value={ratioPercent(host?.memoryUsedBytes, host?.memoryTotalBytes)}
          detail={host ? `${formatBytes(host.memoryUsedBytes)} / ${formatBytes(host.memoryTotalBytes)}` : undefined}
        />
        <UsageBar
          label="Disk"
          value={ratioPercent(host?.diskUsedBytes, host?.diskTotalBytes)}
          detail={host ? `${formatBytes(host.diskUsedBytes)} / ${formatBytes(host.diskTotalBytes)}` : undefined}
        />
      </div>
      {host && metrics?.stale && <p className="mt-1 font-mono text-2xs text-warning">metrics stale</p>}
    </div>
  );
}

export function ServerRow({ row, now }: { row: ServerRowData; now: Date }) {
  const { server, health } = row;
  const heartbeat = server.status.agent.lastHeartbeatAt;
  const staleAxes = health ? describeHealth(health, now).filter((a) => a.stale) : [];
  return (
    <li className="grid gap-4 border-b border-border px-4 py-4 last:border-b-0 lg:grid-cols-[minmax(0,1.3fr)_minmax(0,1.6fr)_minmax(0,1fr)_14rem] lg:items-start">
      <div className="min-w-0">
        <DetailLink
          href={`/servers/${encodeURIComponent(server.id)}`}
          className="focus-ring block truncate text-md font-medium tracking-subheading hover:text-lime"
        >
          {server.name}
        </DetailLink>
        <p className="truncate font-mono text-xs text-muted-foreground">
          {server.host}
          {server.publicIp && server.publicIp !== server.host ? ` · ${server.publicIp}` : ""}
        </p>
        <div className="mt-2 flex flex-wrap gap-1.5">
          {server.roles.map((r) => (
            <Badge key={r} tone="outline">
              {r}
            </Badge>
          ))}
          <Badge tone="neutral">{server.transport}</Badge>
          {server.lifecycle !== "active" && <Badge tone="warning">{server.lifecycle}</Badge>}
        </div>
      </div>

      <div className="min-w-0">
        <ServerStatusBadges server={server} health={health} />
        <p className="mt-2 font-mono text-2xs text-muted-foreground">
          {toDate(heartbeat) ? `last seen ${formatAgo(heartbeat, now)}` : "never seen"}
          {staleAxes.map((a) => (
            <span key={a.key} className="ml-2 text-warning">
              {a.title.toLowerCase()} {a.stale}
            </span>
          ))}
        </p>
      </div>

      <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 font-mono text-2xs">
        <dt className="uppercase tracking-[0.06em] text-muted-foreground">Agent</dt>
        <dd className="truncate text-foreground">{server.agentVersion ?? "-"}</dd>
        <dt className="uppercase tracking-[0.06em] text-muted-foreground">OS</dt>
        <dd className="truncate text-foreground">
          {[server.resources.os, server.resources.architecture].filter(Boolean).join(" ") || "-"}
        </dd>
        <dt className="uppercase tracking-[0.06em] text-muted-foreground">Apps</dt>
        <dd className="text-foreground">{toNum(server.workloadCount) ?? 0}</dd>
      </dl>

      <UsageCell metrics={row.metrics} />
    </li>
  );
}

function RowSkeleton() {
  return (
    <div className="grid gap-3 px-4 py-4" aria-hidden="true">
      <Skeleton className="h-5 w-48" />
      <Skeleton className="h-4 w-full max-w-md" />
      <Skeleton className="h-4 w-64" />
    </div>
  );
}

export function ServersView({ api = serversApi, pollMs = LIST_POLL_MS }: { api?: ServersApi; pollMs?: number }) {
  const [query, setQuery] = React.useState("");
  const [agent, setAgent] = React.useState("all");
  const { data, error, loading, refreshing, updatedAt, refresh } = usePolled((signal) => loadRows(api, signal), "list", {
    intervalMs: pollMs,
  });
  const [now, setNow] = React.useState(() => new Date());
  React.useEffect(() => {
    const t = setInterval(() => setNow(new Date()), 5000);
    return () => clearInterval(t);
  }, []);

  const rows = data?.rows ?? [];
  const visible = rows.filter((r) => matchesFilter(r, query, agent));

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        eyebrow="Infrastructure"
        title="Servers"
        description="Machines that run your workloads. Server, agent, Docker and applications are reported as separate axes."
        actions={
          <>
            <Button variant="outline" onClick={() => void refresh()} aria-label="Refresh servers" disabled={refreshing}>
              <RefreshCwIcon className={refreshing ? "animate-spin motion-reduce:animate-none" : undefined} aria-hidden="true" />
              Refresh
            </Button>
            <Button asChild>
              <Link href="/servers/new">
                <PlusIcon aria-hidden="true" /> Add server
              </Link>
            </Button>
          </>
        }
      />

      {rows.length > 0 && (
        <div className="flex flex-wrap items-end gap-3">
          <div className="relative min-w-56 flex-1 sm:max-w-sm">
            <label htmlFor="server-search" className="sr-only">
              Search servers
            </label>
            <SearchIcon className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
            <Input
              id="server-search"
              type="search"
              placeholder="Search name, host, role…"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              className="pl-8"
            />
          </div>
          <div className="grid gap-1">
            <label htmlFor="agent-filter" className="font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">
              Agent
            </label>
            <NativeSelect id="agent-filter" value={agent} onChange={(e) => setAgent(e.target.value)}>
              <option value="all">All</option>
              <option value="connected">Connected</option>
              <option value="unavailable">Unavailable</option>
              <option value="notInstalled">Not installed</option>
              <option value="unknown">Unknown</option>
            </NativeSelect>
          </div>
          <p className="ml-auto font-mono text-2xs text-muted-foreground" aria-live="polite">
            {visible.length}/{rows.length} shown
            {updatedAt ? ` · updated ${formatAgo(updatedAt, now)} · auto-refresh ${Math.round(pollMs / 1000)}s` : ""}
          </p>
        </div>
      )}

      {error && <ErrorPanel error={error} title="Could not load servers" onRetry={() => void refresh()} />}

      {loading && !data ? (
        <div className="border border-border bg-card" role="status" aria-label="Loading servers">
          <RowSkeleton />
          <RowSkeleton />
        </div>
      ) : data && rows.length === 0 ? (
        <DotGrid fade className="border border-border">
          <EmptyState
            icon={ServerIcon}
            label="No servers"
            title="Connect your first server"
            description="Install the Aethera agent on a machine with Docker. It connects out to the control plane, so no inbound ports are needed."
            action={
              <Button asChild>
                <Link href="/servers/new">
                  <PlusIcon aria-hidden="true" /> Add server
                </Link>
              </Button>
            }
          />
        </DotGrid>
      ) : data && visible.length === 0 ? (
        <div className="border border-dashed border-border-strong px-4 py-10 text-center text-sm text-muted-foreground">
          No server matches the current search and filter.
          <div className="mt-3">
            <Button
              variant="outline"
              size="sm"
              onClick={() => {
                setQuery("");
                setAgent("all");
              }}
            >
              Clear filters
            </Button>
          </div>
        </div>
      ) : data ? (
        <ul className="border border-border bg-card" aria-label="Servers">
          {visible.map((r) => (
            <ServerRow key={r.server.id} row={r} now={now} />
          ))}
        </ul>
      ) : null}

      {data?.more && (
        <p className="font-mono text-2xs text-muted-foreground">Showing the first 100 servers.</p>
      )}
    </div>
  );
}
