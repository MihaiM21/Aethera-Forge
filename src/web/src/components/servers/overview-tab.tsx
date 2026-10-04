"use client";

import * as React from "react";
import { Loader2Icon, RefreshCwIcon } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { errorMessage } from "@/lib/servers/errors";
import { formatAgo, formatBytes, formatDateTime, toDate, toNum } from "@/lib/servers/format";
import { describeHealth, isAxisKey, AXIS_TITLE, type AxisView } from "@/lib/servers/status";
import type { Discovery, Job, Server, ServerHealth } from "@/lib/servers/types";
import { usePolled } from "@/lib/servers/use-polled";
import { cn } from "@/lib/utils";
import { ErrorPanel, Facts, SectionTitle } from "./common";
import { JobProgress } from "./job-progress";

const TONE_TEXT = {
  success: "text-success",
  warning: "text-warning",
  danger: "text-danger",
  info: "text-info",
  idle: "text-muted-foreground",
} as const;

/**
 * One status axis, spelled out: state, "blocked by <layer>" when the API
 * attributes it to a lower layer, "stale since" when the value is last-known,
 * and the API's own explanation. Only formats what `/status` returned.
 */
export function AxisCard({ view, index }: { view: AxisView; index: string }) {
  return (
    <article
      aria-label={`${view.title} axis`}
      data-axis={view.key}
      className="grid content-start gap-2 border border-border bg-card p-4"
    >
      <div className="flex items-baseline justify-between">
        <span className="font-mono text-2xs text-lime">{index}</span>
        <span className="font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">{view.title}</span>
      </div>
      <p className={cn("text-lg font-medium tracking-subheading", TONE_TEXT[view.tone])}>{view.text}</p>
      <div className="flex flex-wrap gap-1.5">
        {view.blockedBy && <Badge tone="neutral">{view.blockedBy}</Badge>}
        {view.stale && <Badge tone="warning">{view.stale}</Badge>}
      </div>
      {view.detail && <p className="text-xs text-muted-foreground">{view.detail}</p>}
    </article>
  );
}

export function AxesPanel({ health, now = new Date() }: { health: ServerHealth; now?: Date }) {
  const views = describeHealth(health, now);
  const first = health.firstFailingLayer;
  return (
    <section aria-labelledby="axes-title" className="grid gap-3">
      <SectionTitle index="01">
        <span id="axes-title">Status axes</span>
      </SectionTitle>
      {first && (
        <p role="status" className="border border-danger/60 bg-danger-soft px-3 py-2 text-sm">
          First failing layer: <strong className="text-danger">{isAxisKey(first) ? AXIS_TITLE[first].toLowerCase() : first}</strong>. Layers above it
          cannot be judged until it recovers.
        </p>
      )}
      <div className="grid gap-px border border-border bg-border sm:grid-cols-2 xl:grid-cols-4 [&>*]:bg-card">
        {views.map((v, i) => (
          <AxisCard key={v.key} view={v} index={String(i + 1).padStart(2, "0")} />
        ))}
      </div>
      {health.session ? (
        <Facts
          className="border border-border bg-card p-4"
          rows={[
            ["agent version", health.session.agentVersion],
            ["connected", `${formatDateTime(health.session.connectedAt)} (${formatAgo(health.session.connectedAt, now)})`],
            ["round trip", toNum(health.session.rttMilliseconds) !== null ? `${Math.round(toNum(health.session.rttMilliseconds)!)} ms` : "-"],
            ["clock skew", toNum(health.session.clockSkewSeconds) !== null ? `${toNum(health.session.clockSkewSeconds)!.toFixed(2)} s` : "-"],
            [
              "capabilities",
              <span key="c" className="flex flex-wrap gap-1">
                {health.session.capabilities.map((c) => (
                  <Badge key={c} tone="outline">
                    {c}
                  </Badge>
                ))}
              </span>,
            ],
          ]}
        />
      ) : (
        <p className="text-xs text-muted-foreground">No live agent session. Showing last known state.</p>
      )}
    </section>
  );
}

function DiscoverySection({ discovery }: { discovery: Discovery }) {
  const report = discovery.report;
  const f = discovery.facts;
  return (
    <section aria-labelledby="sys-title" className="grid gap-3">
      <SectionTitle index="02">
        <span id="sys-title">System information</span>
      </SectionTitle>
      {discovery.stale && (
        <p role="status" className="border border-dashed border-warning/70 bg-warning-soft px-3 py-2 text-xs">
          The agent is not connected. Showing what was collected{" "}
          {toDate(discovery.storedAt) ? formatAgo(discovery.storedAt) : "earlier"}.
        </p>
      )}
      {!report && !f.os && !f.cpuModel ? (
        <p className="border border-dashed border-border-strong px-4 py-8 text-center text-sm text-muted-foreground">
          Nothing has been discovered yet. It is collected when the agent connects; you can also request a refresh below.
        </p>
      ) : (
        <div className="grid gap-4 lg:grid-cols-2">
          <div className="border border-border bg-card p-4">
            <Facts
              rows={[
                ["hostname", report?.host.hostname],
                ["os", [report?.host.osName ?? f.os, report?.host.osVersion ?? f.osVersion].filter(Boolean).join(" ")],
                ["kernel", report?.host.kernelVersion ?? f.kernel],
                ["architecture", report?.host.architecture ?? f.architecture],
                ["cpu", report?.host.cpuModel ?? f.cpuModel],
                [
                  "cores",
                  report
                    ? `${report.host.cpuCoresLogical} logical / ${report.host.cpuCoresPhysical} physical`
                    : f.cpuCores != null
                      ? String(f.cpuCores)
                      : null,
                ],
                ["memory", formatBytes(report?.host.memoryTotalBytes ?? f.memoryBytes)],
                ["swap", report ? formatBytes(report.host.swapTotalBytes) : null],
                ["virtualization", report?.host.virtualization],
                ["addresses", report?.host.ipAddresses.join(", ")],
                ["timezone", report?.host.timezone],
                ["booted", report ? formatDateTime(report.host.bootTime) : null],
              ]}
            />
          </div>
          <div className="grid content-start gap-4">
            <div className="border border-border bg-card p-4">
              <Facts
                rows={[
                  ["docker", report ? `${report.docker.version} (API ${report.docker.apiVersion})` : f.dockerVersion],
                  ["state", report?.docker.status],
                  ["storage driver", report?.docker.storageDriver],
                  ["cgroup", report?.docker.cgroupVersion],
                  ["root dir", report?.docker.dockerRootDir],
                  ["compose", report?.docker.composeVersion],
                  ["rootless", report ? (report.docker.rootless ? "yes" : "no") : null],
                  [
                    "containers",
                    report ? `${report.docker.containersRunning} running / ${report.docker.containersStopped} stopped` : null,
                  ],
                  ["images", report ? String(report.docker.imageCount) : null],
                ]}
              />
              {report?.docker.error && <p className="mt-2 text-xs text-danger">{report.docker.error}</p>}
            </div>
            {report && report.disks.length > 0 && (
              <div className="border border-border bg-card p-4">
                <p className="mb-2 font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Disks</p>
                <ul className="grid gap-1 font-mono text-xs">
                  {report.disks.map((d) => (
                    <li key={d.mountPoint} className="flex flex-wrap justify-between gap-2">
                      <span>{d.mountPoint}</span>
                      <span className="text-muted-foreground">
                        {formatBytes(d.usedBytes)} / {formatBytes(d.totalBytes)} · {d.fsType}
                      </span>
                    </li>
                  ))}
                </ul>
              </div>
            )}
            {report && report.tools.length > 0 && (
              <div className="border border-border bg-card p-4">
                <p className="mb-2 font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Tools</p>
                <div className="flex flex-wrap gap-1.5">
                  {report.tools.map((t) => (
                    <Badge key={t.name} tone="outline" title={t.path}>
                      {t.name} {t.version}
                    </Badge>
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>
      )}
    </section>
  );
}

export function OverviewTab({
  server,
  health,
  healthError,
  onRetryHealth,
  api = serversApi,
}: {
  server: Server;
  health: ServerHealth | undefined;
  healthError: unknown;
  onRetryHealth: () => void;
  api?: ServersApi;
}) {
  const discovery = usePolled((signal) => api.discovery(server.id, { signal }), server.id, { intervalMs: 30_000 });
  const [job, setJob] = React.useState<Job | null>(null);
  const [starting, setStarting] = React.useState(false);
  const [startError, setStartError] = React.useState<string | null>(null);
  const jobRunning = job !== null && (job.status === "queued" || job.status === "running");

  async function refresh() {
    setStarting(true);
    setStartError(null);
    try {
      setJob(await api.refreshDiscovery(server.id));
    } catch (e) {
      setStartError(errorMessage(e));
    } finally {
      setStarting(false);
    }
  }

  return (
    <div className="grid gap-8">
      {healthError && !health ? (
        <ErrorPanel error={healthError} title="Could not load the status axes" onRetry={onRetryHealth} />
      ) : health ? (
        <AxesPanel health={health} />
      ) : (
        <Skeleton className="h-40 w-full" />
      )}

      <div className="grid gap-3">
        <div className="flex flex-wrap items-center gap-3">
          <Button variant="outline" size="sm" onClick={() => void refresh()} disabled={starting || jobRunning}>
            {starting || jobRunning ? <Loader2Icon className="animate-spin" aria-hidden="true" /> : <RefreshCwIcon aria-hidden="true" />}
            Refresh discovery
          </Button>
          <span className="font-mono text-2xs text-muted-foreground">asks the agent to re-collect host and Docker facts</span>
        </div>
        {startError && (
          <p role="alert" className="text-xs text-danger">
            {startError}
          </p>
        )}
        {job && (
          <JobProgress
            jobId={job.id}
            label="Refresh discovery"
            api={api}
            onFinished={() => {
              void discovery.refresh();
            }}
          />
        )}
      </div>

      {discovery.error && !discovery.data ? (
        <ErrorPanel error={discovery.error} title="Could not load system information" onRetry={() => void discovery.refresh()} />
      ) : discovery.data ? (
        <DiscoverySection discovery={discovery.data} />
      ) : (
        <Skeleton className="h-48 w-full" />
      )}
    </div>
  );
}
