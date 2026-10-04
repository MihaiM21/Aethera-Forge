"use client";

import * as React from "react";
import { RefreshCwIcon, RocketIcon } from "lucide-react";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { PageHeader } from "@/components/aethera/page-header";
import { ErrorPanel, NativeSelect } from "@/components/servers/common";
import { DetailLink } from "@/components/shell/detail-link";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { deploymentDuration, isDeploymentActive, shortSha, stepLabel, TRIGGER_LABEL } from "@/lib/resources/status";
import type { Deployment, DeploymentListItem, DeploymentStatus } from "@/lib/resources/types";
import { formatAgo, toNum } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";
import { DeploymentStatusPill } from "./badges";

export const STATUS_FILTERS: Array<{ value: string; label: string }> = [
  { value: "", label: "All statuses" },
  { value: "inProgress,queued", label: "Active" },
  { value: "running", label: "Running" },
  { value: "failed", label: "Failed" },
  { value: "superseded", label: "Superseded" },
  { value: "cancelled", label: "Cancelled" },
];

export type DeploymentRow = { deployment: Deployment; applicationName?: string; applicationId: string };

export function toRow(item: DeploymentListItem): DeploymentRow {
  return { deployment: item.deployment, applicationName: item.applicationName, applicationId: item.deployment.applicationId };
}

export function DeploymentRowView({ row, now, showApp }: { row: DeploymentRow; now: Date; showApp: boolean }) {
  const d = row.deployment;
  const failed = d.status === "failed";
  return (
    <li className="grid gap-3 border-b border-border px-4 py-3 last:border-b-0 md:grid-cols-[minmax(0,1.4fr)_minmax(0,1.2fr)_9rem_8rem] md:items-center">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <DetailLink href={`/deployments/${encodeURIComponent(d.id)}`} className="focus-ring font-mono text-sm font-medium hover:text-lime">
            #{toNum(d.number)}
          </DetailLink>
          {showApp && row.applicationName && (
            <DetailLink href={`/applications/${encodeURIComponent(row.applicationId)}`} className="focus-ring truncate text-sm hover:text-lime">
              {row.applicationName}
            </DetailLink>
          )}
          <DeploymentStatusPill status={d.status} />
          {d.isRollbackPoint && <Badge tone="outline">rollback point</Badge>}
        </div>
        <p className="mt-1 truncate font-mono text-2xs text-muted-foreground">
          {TRIGGER_LABEL[d.trigger] ?? d.trigger} · {d.strategy}
          {d.rollbackOfDeploymentId ? " · rollback" : ""}
        </p>
      </div>
      <div className="min-w-0">
        <p className="truncate text-sm">{d.commitMessage ?? d.imageRef ?? "-"}</p>
        <p className="truncate font-mono text-2xs text-muted-foreground">
          {[d.ref, shortSha(d.commitSha) !== "-" ? shortSha(d.commitSha) : null, d.commitAuthor].filter(Boolean).join(" · ") || "-"}
        </p>
        {failed && (
          <p className="truncate font-mono text-2xs text-danger">
            failed at {stepLabel(d.failedStep)}
            {d.failureCode ? ` · ${d.failureCode}` : ""}
          </p>
        )}
      </div>
      <p className="font-mono text-xs text-muted-foreground">{deploymentDuration(d, now)}</p>
      <p className="font-mono text-xs text-muted-foreground">{formatAgo(d.createdAt, now)}</p>
    </li>
  );
}

function useNow(ms = 5000) {
  const [now, setNow] = React.useState(() => new Date());
  React.useEffect(() => {
    const t = setInterval(() => setNow(new Date()), ms);
    return () => clearInterval(t);
  }, [ms]);
  return now;
}

/** Deployment list of the whole installation (`GET /deployments`), newest first, with a status filter and paging. */
export function DeploymentsView({ api = resourcesApi, pollMs = 5000 }: { api?: ResourcesApi; pollMs?: number }) {
  const [status, setStatus] = React.useState("");
  const [extra, setExtra] = React.useState<{ key: string; rows: DeploymentRow[]; cursor: string | null } | null>(null);
  const [loadingMore, setLoadingMore] = React.useState(false);
  const now = useNow();

  const first = usePolled(
    (signal) => api.deployments.list({ limit: 25, status: status || undefined }, { signal }),
    `list:${status}`,
    { intervalMs: pollMs },
  );
  const firstRows = (first.data?.items ?? []).map(toRow);
  const more = extra?.key === status ? extra : null;
  const seen = new Set(firstRows.map((r) => r.deployment.id));
  const rows = [...firstRows, ...(more?.rows ?? []).filter((r) => !seen.has(r.deployment.id))];
  const cursor = more ? more.cursor : (first.data?.nextCursor ?? null);

  async function loadMore() {
    if (!cursor) return;
    setLoadingMore(true);
    try {
      const page = await api.deployments.list({ limit: 25, cursor, status: status || undefined });
      setExtra({ key: status, rows: [...(more?.rows ?? []), ...page.items.map(toRow)], cursor: page.nextCursor });
    } finally {
      setLoadingMore(false);
    }
  }

  const activeCount = rows.filter((r) => isDeploymentActive(r.deployment.status as DeploymentStatus)).length;

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        eyebrow="Delivery"
        title="Deployments"
        description="Every deploy of every application with its pipeline, logs and result, newest first."
        actions={
          <Button variant="outline" onClick={() => void first.refresh()} disabled={first.refreshing} aria-label="Refresh deployments">
            <RefreshCwIcon className={first.refreshing ? "animate-spin motion-reduce:animate-none" : undefined} aria-hidden="true" /> Refresh
          </Button>
        }
      />
      <div className="flex flex-wrap items-end gap-3">
        <div className="grid gap-1">
          <label htmlFor="deployment-status" className="font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">
            Status
          </label>
          <NativeSelect id="deployment-status" value={status} onChange={(e) => setStatus(e.target.value)} className="w-44">
            {STATUS_FILTERS.map((f) => (
              <option key={f.value} value={f.value}>
                {f.label}
              </option>
            ))}
          </NativeSelect>
        </div>
        {activeCount > 0 && <Badge tone="info">{activeCount} active</Badge>}
      </div>

      {first.error && !first.data && <ErrorPanel error={first.error} onRetry={() => void first.refresh()} title="Could not load deployments" />}
      {first.loading && (
        <div className="grid gap-3 border border-border p-4" role="status" aria-label="Loading deployments">
          <Skeleton className="h-5 w-64" />
          <Skeleton className="h-5 w-full" />
          <Skeleton className="h-5 w-full" />
        </div>
      )}
      {first.data && rows.length === 0 && (
        <DotGrid fade className="border border-dashed border-border">
          <EmptyState
            icon={RocketIcon}
            label="Deployments · empty"
            title={status ? "No deployments with this status" : "No deployments yet"}
            description={status ? "Pick another filter." : "Deploy an application and it shows up here with its pipeline and logs."}
          />
        </DotGrid>
      )}
      {rows.length > 0 && (
        <>
          <ul className="border border-border bg-card">
            {rows.map((r) => (
              <DeploymentRowView key={r.deployment.id} row={r} now={now} showApp />
            ))}
          </ul>
          {cursor && (
            <div className="flex justify-center">
              <Button variant="outline" onClick={() => void loadMore()} disabled={loadingMore}>
                {loadingMore ? "Loading…" : "Load more"}
              </Button>
            </div>
          )}
        </>
      )}
    </div>
  );
}
