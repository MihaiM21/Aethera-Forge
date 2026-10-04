"use client";

import * as React from "react";
import { RefreshCwIcon } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { formatAgo, formatBytes, formatDateTime, toDate, toNum } from "@/lib/servers/format";
import type {
  DockerContainer,
  DockerImage,
  DockerNetwork,
  DockerVolume,
  ResourceEvent,
} from "@/lib/servers/types";
import { usePolled, type Polled } from "@/lib/servers/use-polled";
import { ErrorPanel, InventoryError } from "./common";

const INVENTORY_POLL_MS = 15_000;

function Loading() {
  return (
    <div className="grid gap-2" role="status" aria-label="Loading">
      <Skeleton className="h-8 w-full" />
      <Skeleton className="h-8 w-full" />
      <Skeleton className="h-8 w-full" />
    </div>
  );
}

function Toolbar({ poll, count, noun, children }: { poll: Polled<unknown>; count?: number; noun: string; children?: React.ReactNode }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <p className="font-mono text-2xs text-muted-foreground" aria-live="polite">
        {count !== undefined ? `${count} ${noun}${count === 1 ? "" : "s"} · ` : ""}read live from the agent
        {poll.updatedAt ? ` · updated ${formatAgo(poll.updatedAt)}` : ""}
      </p>
      <div className="flex items-center gap-3">
        {children}
        <Button size="sm" variant="outline" onClick={() => void poll.refresh()} disabled={poll.refreshing}>
          <RefreshCwIcon className={poll.refreshing ? "animate-spin motion-reduce:animate-none" : undefined} aria-hidden="true" /> Refresh
        </Button>
      </div>
    </div>
  );
}

function Empty({ children }: { children: React.ReactNode }) {
  return <p className="border border-dashed border-border-strong px-4 py-8 text-center text-sm text-muted-foreground">{children}</p>;
}

function stateTone(state: DockerContainer["state"]): "success" | "warning" | "danger" | "neutral" {
  switch (state) {
    case "running":
      return "success";
    case "restarting":
    case "paused":
      return "warning";
    case "dead":
      return "danger";
    default:
      return "neutral";
  }
}

export function portText(c: DockerContainer): string {
  return (
    c.ports
      .map((p) => {
        const host = toNum(p.hostPort);
        const cp = toNum(p.containerPort);
        return host ? `${host}:${cp}/${p.protocol ?? "tcp"}` : `${cp}/${p.protocol ?? "tcp"}`;
      })
      .join(", ") || "-"
  );
}

export function ContainersTab({ serverId, api = serversApi }: { serverId: string; api?: ServersApi }) {
  const [all, setAll] = React.useState(true);
  const poll = usePolled((signal) => api.containers(serverId, all, { signal }), `${serverId}:${all}`, { intervalMs: INVENTORY_POLL_MS });
  const rows: DockerContainer[] = poll.data ?? [];
  return (
    <div className="grid gap-3">
      <Toolbar poll={poll} count={poll.data ? rows.length : undefined} noun="container">
        <label className="flex items-center gap-2 text-xs text-muted-foreground">
          <Switch checked={all} onCheckedChange={setAll} aria-label="Include stopped containers" />
          Include stopped
        </label>
      </Toolbar>
      {poll.error && !poll.data ? (
        <InventoryError error={poll.error} onRetry={() => void poll.refresh()} />
      ) : !poll.data ? (
        <Loading />
      ) : rows.length === 0 ? (
        <Empty>No containers{all ? "" : " running"} on this server.</Empty>
      ) : (
        <div className="border border-border bg-card">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Image</TableHead>
                <TableHead>State</TableHead>
                <TableHead>Ports</TableHead>
                <TableHead>Started</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((c) => (
                <TableRow key={c.id}>
                  <TableCell className="font-mono text-xs">{c.name.replace(/^\//, "")}</TableCell>
                  <TableCell className="max-w-64 truncate font-mono text-xs" title={c.image}>
                    {c.image}
                  </TableCell>
                  <TableCell>
                    <span className="flex flex-wrap items-center gap-1.5">
                      <Badge tone={stateTone(c.state)}>{c.state}</Badge>
                      {c.health !== "none" && c.health !== "unspecified" && (
                        <Badge tone={c.health === "healthy" ? "success" : c.health === "unhealthy" ? "warning" : "neutral"}>{c.health}</Badge>
                      )}
                      {c.oomKilled && <Badge tone="danger">oom killed</Badge>}
                    </span>
                    <span className="block font-mono text-2xs text-muted-foreground">{c.status}</span>
                  </TableCell>
                  <TableCell className="font-mono text-xs">{portText(c)}</TableCell>
                  <TableCell className="font-mono text-xs text-muted-foreground">{toDate(c.startedAt) ? formatAgo(c.startedAt) : "-"}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  );
}

function ImagesPanel({ serverId, api }: { serverId: string; api: ServersApi }) {
  const poll = usePolled((signal) => api.images(serverId, { signal }), serverId, { intervalMs: INVENTORY_POLL_MS });
  const rows: DockerImage[] = poll.data ?? [];
  return (
    <div className="grid gap-3">
      <Toolbar poll={poll} count={poll.data ? rows.length : undefined} noun="image" />
      {poll.error && !poll.data ? (
        <InventoryError error={poll.error} onRetry={() => void poll.refresh()} />
      ) : !poll.data ? (
        <Loading />
      ) : rows.length === 0 ? (
        <Empty>No images on this server.</Empty>
      ) : (
        <div className="border border-border bg-card">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Tags</TableHead>
                <TableHead>Size</TableHead>
                <TableHead>Used by</TableHead>
                <TableHead>Created</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((i) => (
                <TableRow key={i.id}>
                  <TableCell className="font-mono text-xs">
                    {i.repoTags.length ? i.repoTags.join(", ") : <span className="text-muted-foreground">&lt;none&gt; {i.id.slice(7, 19)}</span>}
                  </TableCell>
                  <TableCell className="font-mono text-xs">{formatBytes(i.sizeBytes)}</TableCell>
                  <TableCell className="font-mono text-xs">{toNum(i.containersUsing) ?? 0}</TableCell>
                  <TableCell className="font-mono text-xs text-muted-foreground">{formatAgo(i.createdAt)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  );
}

function VolumesPanel({ serverId, api }: { serverId: string; api: ServersApi }) {
  const poll = usePolled((signal) => api.volumes(serverId, { signal }), serverId, { intervalMs: INVENTORY_POLL_MS });
  const rows: DockerVolume[] = poll.data ?? [];
  return (
    <div className="grid gap-3">
      <Toolbar poll={poll} count={poll.data ? rows.length : undefined} noun="volume" />
      {poll.error && !poll.data ? (
        <InventoryError error={poll.error} onRetry={() => void poll.refresh()} />
      ) : !poll.data ? (
        <Loading />
      ) : rows.length === 0 ? (
        <Empty>No volumes on this server.</Empty>
      ) : (
        <div className="border border-border bg-card">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Driver</TableHead>
                <TableHead>Size</TableHead>
                <TableHead>In use</TableHead>
                <TableHead>Created</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((v) => (
                <TableRow key={v.name}>
                  <TableCell className="max-w-64 truncate font-mono text-xs" title={v.mountpoint}>
                    {v.name}
                  </TableCell>
                  <TableCell className="font-mono text-xs">{v.driver}</TableCell>
                  <TableCell className="font-mono text-xs">{toNum(v.sizeBytes) !== null && Number(v.sizeBytes) >= 0 ? formatBytes(v.sizeBytes) : "-"}</TableCell>
                  <TableCell>
                    {(toNum(v.refCount) ?? 0) > 0 ? <Badge tone="success">{toNum(v.refCount)} containers</Badge> : <Badge tone="neutral">unused</Badge>}
                  </TableCell>
                  <TableCell className="font-mono text-xs text-muted-foreground">{formatAgo(v.createdAt)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  );
}

function NetworksPanel({ serverId, api }: { serverId: string; api: ServersApi }) {
  const poll = usePolled((signal) => api.networks(serverId, { signal }), serverId, { intervalMs: INVENTORY_POLL_MS });
  const rows: DockerNetwork[] = poll.data ?? [];
  return (
    <div className="grid gap-3">
      <Toolbar poll={poll} count={poll.data ? rows.length : undefined} noun="network" />
      {poll.error && !poll.data ? (
        <InventoryError error={poll.error} onRetry={() => void poll.refresh()} />
      ) : !poll.data ? (
        <Loading />
      ) : rows.length === 0 ? (
        <Empty>No networks on this server.</Empty>
      ) : (
        <div className="border border-border bg-card">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Driver</TableHead>
                <TableHead>Subnets</TableHead>
                <TableHead>Containers</TableHead>
                <TableHead>Flags</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((n) => (
                <TableRow key={n.id}>
                  <TableCell className="font-mono text-xs">{n.name}</TableCell>
                  <TableCell className="font-mono text-xs">
                    {n.driver} · {n.scope}
                  </TableCell>
                  <TableCell className="font-mono text-xs">{n.ipam.map((i) => i.subnet).filter(Boolean).join(", ") || "-"}</TableCell>
                  <TableCell className="font-mono text-xs">{n.endpoints.length}</TableCell>
                  <TableCell>
                    <span className="flex flex-wrap gap-1">
                      {n.internal && <Badge tone="outline">internal</Badge>}
                      {n.attachable && <Badge tone="outline">attachable</Badge>}
                      {n.ipv6 && <Badge tone="outline">ipv6</Badge>}
                    </span>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  );
}

/** Images, volumes and networks as sub-tabs; each loads only when opened. */
export function InventoryTab({ serverId, api = serversApi }: { serverId: string; api?: ServersApi }) {
  return (
    <Tabs defaultValue="images" className="gap-3">
      <TabsList aria-label="Docker inventory" className="w-fit">
        <TabsTrigger value="images">Images</TabsTrigger>
        <TabsTrigger value="volumes">Volumes</TabsTrigger>
        <TabsTrigger value="networks">Networks</TabsTrigger>
      </TabsList>
      <TabsContent value="images">
        <ImagesPanel serverId={serverId} api={api} />
      </TabsContent>
      <TabsContent value="volumes">
        <VolumesPanel serverId={serverId} api={api} />
      </TabsContent>
      <TabsContent value="networks">
        <NetworksPanel serverId={serverId} api={api} />
      </TabsContent>
    </Tabs>
  );
}

function eventTone(e: ResourceEvent): "success" | "danger" | "warning" | "neutral" {
  const next = e.newValue ?? "";
  if (["connected", "available", "running", "reachable"].includes(next)) return "success";
  if (["unavailable", "unreachable", "stopped"].includes(next)) return "danger";
  return "neutral";
}

export function EventsTab({ serverId, api = serversApi }: { serverId: string; api?: ServersApi }) {
  const poll = usePolled((signal) => api.events(serverId, 100, { signal }), serverId, { intervalMs: INVENTORY_POLL_MS });
  const events = poll.data ?? [];
  return (
    <div className="grid gap-3">
      <div className="flex items-center justify-between gap-3">
        <p className="font-mono text-2xs text-muted-foreground">latest {events.length || ""} events, newest first</p>
        <Button size="sm" variant="outline" onClick={() => void poll.refresh()} disabled={poll.refreshing}>
          <RefreshCwIcon aria-hidden="true" /> Refresh
        </Button>
      </div>
      {poll.error && !poll.data ? (
        <ErrorPanel error={poll.error} title="Could not load events" onRetry={() => void poll.refresh()} />
      ) : !poll.data ? (
        <Loading />
      ) : events.length === 0 ? (
        <Empty>No events yet. State changes of the server, agent and Docker axes are recorded here.</Empty>
      ) : (
        <ol className="border border-border bg-card" aria-label="Server events">
          {events.map((e) => (
            <li key={e.id} className="grid gap-1 border-b border-border px-4 py-2.5 text-sm last:border-b-0 sm:grid-cols-[11rem_1fr] sm:gap-4">
              <time dateTime={String(e.occurredAt)} className="font-mono text-2xs text-muted-foreground" title={formatDateTime(e.occurredAt)}>
                {formatDateTime(e.occurredAt)}
              </time>
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-mono text-xs">{e.kind}</span>
                {e.axis && <Badge tone="outline">{e.axis}</Badge>}
                {(e.oldValue || e.newValue) && (
                  <span className="flex items-center gap-1.5 font-mono text-xs text-muted-foreground">
                    {e.oldValue ?? "-"} <span aria-hidden="true">→</span>
                    <span className="sr-only">to</span> <Badge tone={eventTone(e)}>{e.newValue ?? "-"}</Badge>
                  </span>
                )}
                {e.detail && <span className="text-xs text-muted-foreground">{e.detail}</span>}
              </div>
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}
