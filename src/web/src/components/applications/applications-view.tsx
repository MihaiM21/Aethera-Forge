"use client";

import * as React from "react";
import { BoxesIcon, PlusIcon, RefreshCwIcon, SearchIcon } from "lucide-react";
import Link from "next/link";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { PageHeader } from "@/components/aethera/page-header";
import { WorkloadStatusPill } from "@/components/deployments/badges";
import { ErrorPanel, NativeSelect } from "@/components/servers/common";
import { DetailLink } from "@/components/shell/detail-link";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { repoLabel } from "@/lib/resources/status";
import type { ApplicationSummary } from "@/lib/resources/types";
import { formatAgo } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

const STATUS_FILTERS = [
  { value: "", label: "All statuses" },
  { value: "running", label: "Running" },
  { value: "deploying", label: "Deploying" },
  { value: "failed", label: "Failed" },
  { value: "stopped", label: "Stopped" },
  { value: "notDeployed", label: "Not deployed" },
];

export function matchesApp(a: ApplicationSummary, q: string): boolean {
  const t = q.trim().toLowerCase();
  return !t || [a.name, a.slug, a.repositoryUrl ?? "", a.image ?? ""].join(" ").toLowerCase().includes(t);
}

export function ApplicationRow({ app }: { app: ApplicationSummary }) {
  return (
    <li className="grid gap-3 border-b border-border px-4 py-4 last:border-b-0 md:grid-cols-[minmax(0,1.3fr)_minmax(0,1.3fr)_10rem_7rem] md:items-center">
      <div className="min-w-0">
        <DetailLink href={`/applications/${encodeURIComponent(app.id)}`} className="focus-ring block truncate text-md font-medium tracking-subheading hover:text-lime">
          {app.name}
        </DetailLink>
        <p className="truncate font-mono text-xs text-muted-foreground">{app.slug}</p>
      </div>
      <div className="min-w-0">
        <p className="truncate font-mono text-xs">{app.repositoryUrl ? repoLabel(app.repositoryUrl) : (app.image ?? "compose")}</p>
        <div className="mt-1 flex gap-1.5">
          <Badge tone="outline">{app.sourceKind}</Badge>
        </div>
      </div>
      <div>
        <WorkloadStatusPill status={app.status} />
        {app.statusReason && app.status === "failed" && <p className="mt-1 truncate font-mono text-2xs text-danger">{app.statusReason}</p>}
      </div>
      <p className="font-mono text-xs text-muted-foreground">{formatAgo(app.updatedAt)}</p>
    </li>
  );
}

/** Applications of the organization: search, status filter, link to the create wizard. */
export function ApplicationsView({ api = resourcesApi, pollMs = 8000 }: { api?: ResourcesApi; pollMs?: number }) {
  const [q, setQ] = React.useState("");
  const [status, setStatus] = React.useState("");
  const list = usePolled((signal) => api.applications.list({ limit: 100, sort: "name", status: status || undefined }, { signal }), `apps:${status}`, { intervalMs: pollMs });
  const rows = (list.data?.items ?? []).filter((a) => matchesApp(a, q));
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        eyebrow="Workloads"
        title="Applications"
        description="Everything you deploy: source, build method, environment, domains and health."
        actions={
          <>
            <Button variant="outline" onClick={() => void list.refresh()} disabled={list.refreshing} aria-label="Refresh applications">
              <RefreshCwIcon className={list.refreshing ? "animate-spin motion-reduce:animate-none" : undefined} aria-hidden="true" /> Refresh
            </Button>
            <Button asChild>
              <Link href="/applications/new">
                <PlusIcon aria-hidden="true" /> New application
              </Link>
            </Button>
          </>
        }
      />
      {(list.data?.items.length ?? 0) > 0 && (
        <div className="flex flex-wrap items-end gap-3">
          <div className="relative min-w-56 flex-1 sm:max-w-sm">
            <label htmlFor="app-search" className="sr-only">
              Search applications
            </label>
            <SearchIcon className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
            <Input id="app-search" type="search" placeholder="Search name, repository, image…" value={q} onChange={(e) => setQ(e.target.value)} className="pl-8" />
          </div>
          <label htmlFor="app-status" className="sr-only">
            Status
          </label>
          <NativeSelect id="app-status" value={status} onChange={(e) => setStatus(e.target.value)} className="w-44">
            {STATUS_FILTERS.map((f) => (
              <option key={f.value} value={f.value}>
                {f.label}
              </option>
            ))}
          </NativeSelect>
        </div>
      )}
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load applications" />}
      {list.loading && (
        <div className="grid gap-3 border border-border p-4" role="status" aria-label="Loading applications">
          <Skeleton className="h-5 w-48" />
          <Skeleton className="h-5 w-full" />
        </div>
      )}
      {list.data && list.data.items.length === 0 && !status && (
        <DotGrid fade className="border border-dashed border-border">
          <EmptyState
            icon={BoxesIcon}
            label="Applications · empty"
            title="No applications yet"
            description="Create an application, pick a repository and a build method, then deploy."
            action={
              <Button asChild>
                <Link href="/applications/new">New application</Link>
              </Button>
            }
          />
        </DotGrid>
      )}
      {list.data && list.data.items.length > 0 && rows.length === 0 && <p className="text-sm text-muted-foreground">No application matches the filter.</p>}
      {rows.length > 0 && <ul className="border border-border bg-card">{rows.map((a) => <ApplicationRow key={a.id} app={a} />)}</ul>}
    </div>
  );
}
