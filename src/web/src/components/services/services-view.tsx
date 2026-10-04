"use client";

import * as React from "react";
import { LayersIcon, PlusIcon, RefreshCwIcon } from "lucide-react";
import Link from "next/link";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { PageHeader } from "@/components/aethera/page-header";
import { WorkloadStatusPill } from "@/components/deployments/badges";
import { ErrorPanel } from "@/components/servers/common";
import { DetailLink } from "@/components/shell/detail-link";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import type { ServiceSummary } from "@/lib/resources/types";
import { formatAgo } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

export function ServiceRow({ service }: { service: ServiceSummary }) {
  return (
    <li className="grid gap-3 border-b border-border px-4 py-4 last:border-b-0 md:grid-cols-[minmax(0,1.3fr)_minmax(0,1.3fr)_10rem_7rem] md:items-center">
      <div className="min-w-0">
        <DetailLink href={`/services/${encodeURIComponent(service.id)}`} className="focus-ring block truncate text-md font-medium tracking-subheading hover:text-lime">
          {service.name}
        </DetailLink>
        <p className="truncate font-mono text-xs text-muted-foreground">{service.slug}</p>
      </div>
      <div className="min-w-0">
        <p className="truncate font-mono text-xs">{service.image}</p>
        <div className="mt-1 flex gap-1.5">
          <Badge tone="outline">{service.templateKey}</Badge>
        </div>
      </div>
      <div>
        <WorkloadStatusPill status={service.status} />
        {service.statusReason && service.status === "failed" && <p className="mt-1 truncate font-mono text-2xs text-danger">{service.statusReason}</p>}
      </div>
      <p className="font-mono text-xs text-muted-foreground">{formatAgo(service.updatedAt)}</p>
    </li>
  );
}

/** Services of the organization: databases, caches and other managed images created from templates. */
export function ServicesView({ api = resourcesApi }: { api?: ResourcesApi }) {
  const list = usePolled((signal) => api.services.list({ limit: 100, sort: "name" }, { signal }), "services", { intervalMs: 8000 });
  const rows = list.data?.items ?? [];
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        eyebrow="Workloads"
        title="Services"
        description="Databases, caches and other managed services running next to your apps."
        actions={
          <>
            <Button variant="outline" onClick={() => void list.refresh()} disabled={list.refreshing} aria-label="Refresh services">
              <RefreshCwIcon className={list.refreshing ? "animate-spin motion-reduce:animate-none" : undefined} aria-hidden="true" /> Refresh
            </Button>
            <Button asChild>
              <Link href="/services/new">
                <PlusIcon aria-hidden="true" /> New service
              </Link>
            </Button>
          </>
        }
      />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load services" />}
      {list.loading && <Skeleton className="h-24 w-full" />}
      {list.data && rows.length === 0 && (
        <DotGrid fade className="border border-dashed border-border">
          <EmptyState
            icon={LayersIcon}
            label="Services · empty"
            title="No services yet"
            description="Start PostgreSQL, Redis, MongoDB, Grafana, MinIO and more from a template, with generated credentials."
            action={
              <Button asChild>
                <Link href="/services/new">New service</Link>
              </Button>
            }
          />
        </DotGrid>
      )}
      {rows.length > 0 && <ul className="border border-border bg-card">{rows.map((s) => <ServiceRow key={s.id} service={s} />)}</ul>}
    </div>
  );
}
