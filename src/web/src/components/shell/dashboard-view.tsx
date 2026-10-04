"use client";

import * as React from "react";
import Link from "next/link";
import { BellIcon, BoxesIcon, CircleCheckIcon, ListChecksIcon, PlusIcon, RocketIcon, ServerIcon } from "lucide-react";
import { EmptyState } from "@/components/aethera/empty-state";
import { NumberedCard, NumberedCardGrid } from "@/components/aethera/numbered-card";
import { PageHeader } from "@/components/aethera/page-header";
import { UsageBar } from "@/components/aethera/usage-bar";
import { DeploymentStatusPill, WorkloadStatusPill } from "@/components/deployments/badges";
import { ErrorPanel } from "@/components/servers/common";
import { ServerStatusBadges } from "@/components/servers/status-badges";
import { DetailLink } from "@/components/shell/detail-link";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useAuth } from "@/lib/auth/auth-context";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { deploymentDuration, shortSha, stepLabel } from "@/lib/resources/status";
import type { ApplicationSummary, DeploymentListItem, Job, ServiceSummary } from "@/lib/resources/types";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { formatAgo, formatBytes, ratioPercent, toDate, toNum } from "@/lib/servers/format";
import type { LatestMetrics, Server } from "@/lib/servers/types";
import { usePolled } from "@/lib/servers/use-polled";
import { cn } from "@/lib/utils";

const POLL_MS = 8000;

function Panel({ title, meta, className, children }: { title: string; meta?: string; className?: string; children: React.ReactNode }) {
  return (
    <Card className={className}>
      <CardHeader className="items-center py-2.5">
        <CardTitle className="mono-label text-xs font-medium tracking-[0.14em] text-muted-foreground uppercase">{title}</CardTitle>
        {meta && <span className="font-mono text-2xs text-muted-foreground">{meta}</span>}
      </CardHeader>
      <CardContent className="p-0">{children}</CardContent>
    </Card>
  );
}

function Skeletons({ rows = 3 }: { rows?: number }) {
  return (
    <ul className="divide-y divide-border" aria-hidden="true">
      {Array.from({ length: rows }, (_, i) => (
        <li key={i} className="flex items-center gap-3 px-4 py-3">
          <Skeleton className="h-3 w-32" />
          <Skeleton className="ml-auto h-3 w-16" />
        </li>
      ))}
    </ul>
  );
}

export type Alert = { key: string; tone: "danger" | "warning"; text: string; href: string };

/** Alerts derived from what the API already reports: servers whose agent is down, failing applications, high usage. */
export function deriveAlerts(servers: Server[], apps: ApplicationSummary[], services: ServiceSummary[], usage: { cpu: number | null; memory: number | null; disk: number | null }): Alert[] {
  const alerts: Alert[] = [];
  for (const s of servers) {
    if (s.lifecycle === "active" && s.status.agent.status !== "connected" && s.transport === "agent")
      alerts.push({ key: `srv:${s.id}`, tone: "danger", text: `${s.name}: the agent is ${s.status.agent.status}`, href: `/servers/${s.id}` });
  }
  for (const a of apps.filter((x) => x.status === "failed" || x.status === "unhealthy"))
    alerts.push({ key: `app:${a.id}`, tone: a.status === "failed" ? "danger" : "warning", text: `${a.name} is ${a.status}${a.statusReason ? `: ${a.statusReason}` : ""}`, href: `/applications/${a.id}` });
  for (const v of services.filter((x) => x.status === "failed" || x.status === "unhealthy"))
    alerts.push({ key: `svc:${v.id}`, tone: v.status === "failed" ? "danger" : "warning", text: `${v.name} is ${v.status}`, href: `/services/${v.id}` });
  for (const [label, value] of [["CPU", usage.cpu], ["Memory", usage.memory], ["Disk", usage.disk]] as const)
    if (value !== null && value >= 90) alerts.push({ key: `use:${label}`, tone: "warning", text: `${label} usage is ${Math.round(value)}% across servers`, href: "/servers" });
  return alerts;
}

export function aggregateUsage(metrics: Array<LatestMetrics | undefined>) {
  const hosts = metrics.flatMap((m) => (m?.host && !m.stale ? [m.host] : []));
  const mean = (xs: number[]) => (xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : null);
  const sum = (f: (h: (typeof hosts)[number]) => unknown) => hosts.reduce((a, h) => a + (toNum(f(h)) ?? 0), 0);
  return {
    count: hosts.length,
    cpu: mean(hosts.map((h) => toNum(h.cpuPercent)).filter((x): x is number => x !== null)),
    memory: ratioPercent(sum((h) => h.memoryUsedBytes), sum((h) => h.memoryTotalBytes)),
    disk: ratioPercent(sum((h) => h.diskUsedBytes), sum((h) => h.diskTotalBytes)),
    memoryText: `${formatBytes(sum((h) => h.memoryUsedBytes))} / ${formatBytes(sum((h) => h.memoryTotalBytes))}`,
    diskText: `${formatBytes(sum((h) => h.diskUsedBytes))} / ${formatBytes(sum((h) => h.diskTotalBytes))}`,
  };
}

const countBy = <T,>(items: T[], pick: (t: T) => string) => items.reduce<Record<string, number>>((acc, i) => ({ ...acc, [pick(i)]: (acc[pick(i)] ?? 0) + 1 }), {});

/** The landing page: server and application status, recent and failed deployments, resource usage, active jobs and alerts. */
export function DashboardView({ api = resourcesApi, servers = serversApi, pollMs = POLL_MS }: { api?: ResourcesApi; servers?: ServersApi; pollMs?: number }) {
  const { me } = useAuth();
  const serverList = usePolled((signal) => servers.list({ limit: 100, sort: "name" }, { signal }), "dash-servers", { intervalMs: pollMs });
  const metrics = usePolled(
    async (signal) => Promise.all((serverList.data?.items ?? []).map((s) => servers.latestMetrics(s.id, { signal }).catch(() => undefined))),
    `dash-metrics:${serverList.data?.items.map((s) => s.id).join(",")}`,
    { intervalMs: pollMs, enabled: serverList.data !== undefined },
  );
  const apps = usePolled((signal) => api.applications.list({ limit: 100, sort: "name" }, { signal }), "dash-apps", { intervalMs: pollMs });
  const services = usePolled((signal) => api.services.list({ limit: 100, sort: "name" }, { signal }), "dash-services", { intervalMs: pollMs });
  const recent = usePolled((signal) => api.deployments.list({ limit: 25 }, { signal }), "dash-deployments", { intervalMs: pollMs });
  const failed = usePolled((signal) => api.deployments.list({ limit: 5, status: "failed" }, { signal }), "dash-failed", { intervalMs: pollMs });
  const jobs = usePolled((signal) => api.jobs.list({ limit: 10, status: "queued,running" }, { signal }), "dash-jobs", { intervalMs: pollMs });
  const [now, setNow] = React.useState(() => new Date());
  React.useEffect(() => {
    const t = setInterval(() => setNow(new Date()), 5000);
    return () => clearInterval(t);
  }, []);

  const serverItems = serverList.data?.items ?? [];
  const appItems = apps.data?.items ?? [];
  const svcItems = services.data?.items ?? [];
  const recentItems: DeploymentListItem[] = recent.data?.items ?? [];
  const last24h = recentItems.filter((d) => {
    const at = toDate(d.deployment.createdAt);
    return at !== null && now.getTime() - at.getTime() < 24 * 3600 * 1000;
  }).length;
  const serverCounts = countBy(serverItems, (s) => (s.status.agent.status === "connected" ? "online" : s.transport === "ssh" ? "ssh" : "offline"));
  const appCounts = countBy(appItems, (a) => a.status);
  const usage = aggregateUsage(metrics.data ?? []);
  const alerts = deriveAlerts(serverItems, appItems, svcItems, usage);
  const activeJobs: Job[] = jobs.data?.items ?? [];
  const firstError = [serverList, apps, recent].find((r) => r.error && !r.data)?.error;

  return (
    <div className="flex flex-col gap-8">
      <PageHeader
        eyebrow="Overview"
        title="Dashboard"
        description={me ? `Signed in as ${me.user.displayName} · ${me.organization.name}` : "Everything running on this installation, at a glance."}
        actions={
          <Button asChild>
            <Link href="/applications/new">
              <PlusIcon aria-hidden="true" />
              New application
            </Link>
          </Button>
        }
      />
      {firstError && <ErrorPanel error={firstError} title="Some dashboard data could not be loaded" onRetry={() => { void serverList.refresh(); void apps.refresh(); void recent.refresh(); }} />}

      <NumberedCardGrid>
        <NumberedCard index="01" title="Servers" description={`${serverCounts.online ?? 0} online · ${serverCounts.ssh ?? 0} ssh · ${serverCounts.offline ?? 0} offline`} className="min-h-36">
          {serverList.data ? <p className="mb-2 font-mono text-3xl tabular-nums">{serverItems.length}</p> : <Skeleton className="mb-2 h-7 w-12" />}
        </NumberedCard>
        <NumberedCard
          index="02"
          title="Applications"
          description={`${appCounts.running ?? 0} running · ${appCounts.deploying ?? 0} deploying · ${(appCounts.failed ?? 0) + (appCounts.unhealthy ?? 0)} failing`}
          className="min-h-36"
        >
          {apps.data ? <p className="mb-2 font-mono text-3xl tabular-nums">{appItems.length}</p> : <Skeleton className="mb-2 h-7 w-12" />}
        </NumberedCard>
        <NumberedCard index="03" title="Deployments" description="Last 24 hours" className="min-h-36">
          {recent.data ? <p className="mb-2 font-mono text-3xl tabular-nums">{last24h}</p> : <Skeleton className="mb-2 h-7 w-12" />}
        </NumberedCard>
        <NumberedCard index="04" title="Active jobs" description="Queued and running now" className="min-h-36">
          {jobs.data ? <p className="mb-2 font-mono text-3xl tabular-nums">{activeJobs.length}</p> : <Skeleton className="mb-2 h-7 w-12" />}
        </NumberedCard>
      </NumberedCardGrid>

      <div className="grid gap-6 lg:grid-cols-2">
        <Panel title="Server status" meta="live">
          {!serverList.data && <Skeletons />}
          {serverList.data && serverItems.length === 0 && (
            <EmptyState icon={ServerIcon} title="No servers yet" description="Add a server to start deploying." className="py-8" action={<Button asChild size="sm"><Link href="/servers/new">Add server</Link></Button>} />
          )}
          <ul className="divide-y divide-border">
            {serverItems.slice(0, 6).map((s) => (
              <li key={s.id} className="flex flex-wrap items-center justify-between gap-2 px-4 py-2.5">
                <DetailLink href={`/servers/${s.id}`} className="truncate text-sm hover:text-lime">{s.name}</DetailLink>
                <ServerStatusBadges server={s} />
              </li>
            ))}
          </ul>
        </Panel>

        <Panel title="Application status" meta="live">
          {!apps.data && <Skeletons />}
          {apps.data && appItems.length === 0 && (
            <EmptyState icon={BoxesIcon} title="No applications yet" description="Create one and deploy it." className="py-8" action={<Button asChild size="sm"><Link href="/applications/new">New application</Link></Button>} />
          )}
          <ul className="divide-y divide-border">
            {[...appItems].sort((a, b) => Number(a.status === "running") - Number(b.status === "running")).slice(0, 6).map((a) => (
              <li key={a.id} className="flex flex-wrap items-center justify-between gap-2 px-4 py-2.5">
                <DetailLink href={`/applications/${a.id}`} className="truncate text-sm hover:text-lime">{a.name}</DetailLink>
                <WorkloadStatusPill status={a.status} />
              </li>
            ))}
          </ul>
        </Panel>

        <Panel title="Recent deployments">
          {!recent.data && <Skeletons />}
          {recent.data && recentItems.length === 0 && <EmptyState icon={RocketIcon} title="No deployments yet" description="Deploy an application to see its history here." className="py-8" />}
          <ul className="divide-y divide-border">
            {recentItems.slice(0, 6).map((d) => (
              <li key={d.deployment.id} className="grid grid-cols-[minmax(0,1fr)_auto_auto] items-center gap-3 px-4 py-2.5">
                <div className="min-w-0">
                  <DetailLink href={`/deployments/${d.deployment.id}`} className="block truncate text-sm hover:text-lime">
                    {d.applicationName} <span className="font-mono text-2xs text-muted-foreground">#{toNum(d.deployment.number)}</span>
                  </DetailLink>
                  <p className="truncate font-mono text-2xs text-muted-foreground">{shortSha(d.deployment.commitSha) !== "-" ? shortSha(d.deployment.commitSha) : d.deployment.imageRef ?? ""} · {formatAgo(d.deployment.createdAt, now)}</p>
                </div>
                <DeploymentStatusPill status={d.deployment.status} />
                <span className="w-14 text-right font-mono text-2xs text-muted-foreground">{deploymentDuration(d.deployment, now)}</span>
              </li>
            ))}
          </ul>
        </Panel>

        <Panel title="Failed deployments">
          {failed.data && failed.data.items.length === 0 && <EmptyState icon={CircleCheckIcon} title="No failed deployments" description="Failures show where the pipeline stopped and why." className="py-8" />}
          {!failed.data && <Skeletons />}
          <ul className="divide-y divide-border">
            {(failed.data?.items ?? []).map((d) => (
              <li key={d.deployment.id} className="px-4 py-2.5">
                <DetailLink href={`/deployments/${d.deployment.id}`} className="block truncate text-sm hover:text-lime">
                  {d.applicationName} <span className="font-mono text-2xs text-muted-foreground">#{toNum(d.deployment.number)}</span>
                </DetailLink>
                <p className="truncate font-mono text-2xs text-danger">
                  failed at {stepLabel(d.deployment.failedStep)}
                  {d.deployment.failureCode ? ` · ${d.deployment.failureCode}` : ""} · {formatAgo(d.deployment.createdAt, now)}
                </p>
              </li>
            ))}
          </ul>
        </Panel>

        <Panel title="Resource usage" meta={usage.count > 0 ? `${usage.count} server${usage.count === 1 ? "" : "s"}` : "all servers"}>
          <div className="grid gap-3 p-4">
            {usage.count === 0 && <p className="text-xs text-muted-foreground">{metrics.data ? "No fresh metrics yet. Connected agents report every few seconds." : "Loading…"}</p>}
            <UsageBar label="CPU" value={usage.cpu} />
            <UsageBar label="RAM" value={usage.memory} detail={usage.count > 0 ? usage.memoryText : undefined} />
            <UsageBar label="Disk" value={usage.disk} detail={usage.count > 0 ? usage.diskText : undefined} />
          </div>
        </Panel>

        <Panel title="Active jobs">
          {jobs.data && activeJobs.length === 0 && <EmptyState icon={ListChecksIcon} title="No jobs running" description="Builds, deployments and server tasks appear here while they run." className="py-8" />}
          {!jobs.data && <Skeletons />}
          <ul className="divide-y divide-border">
            {activeJobs.map((j) => (
              <li key={j.id} className="flex items-center justify-between gap-3 px-4 py-2.5">
                <span className="truncate font-mono text-xs">{j.type}</span>
                <span className="font-mono text-2xs text-muted-foreground">{j.status} · {formatAgo(j.createdAt, now)}</span>
              </li>
            ))}
          </ul>
        </Panel>

        <Panel title="Alerts" meta="from current status" className={cn("lg:col-span-2")}>
          {alerts.length === 0 && <EmptyState icon={BellIcon} title="All quiet" description="Unreachable servers, failing applications and high usage will be listed here." className="py-8" />}
          <ul className="divide-y divide-border">
            {alerts.map((a) => (
              <li key={a.key}>
                <Link href={a.href} className="flex items-center gap-3 px-4 py-2.5 text-sm hover:bg-accent">
                  <span className={cn("size-2 shrink-0", a.tone === "danger" ? "bg-danger" : "bg-warning")} aria-hidden="true" />
                  <span className="sr-only">{a.tone === "danger" ? "Critical: " : "Warning: "}</span>
                  {a.text}
                </Link>
              </li>
            ))}
          </ul>
        </Panel>
      </div>
    </div>
  );
}
