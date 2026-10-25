"use client";

import * as React from "react";
import Link from "next/link";
import { BellIcon, CheckIcon, RefreshCwIcon } from "lucide-react";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { PageHeader } from "@/components/aethera/page-header";
import { FormError, useAction } from "@/components/resources/form";
import { ErrorPanel, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { useAuth } from "@/lib/auth/auth-context";
import { notificationsApi, type NotificationsApi } from "@/lib/notifications/api";
import { severityTone, sortOpenAlerts } from "@/lib/notifications/events";
import type { Alert } from "@/lib/notifications/types";
import { formatAgo } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

const POLL_MS = 15_000;

function AlertTitle({ alert }: { alert: Alert }) {
  return alert.path ? (
    <Link href={alert.path} className="font-medium hover:underline">
      {alert.title}
    </Link>
  ) : (
    <span className="font-medium">{alert.title}</span>
  );
}

function OpenAlertRow({ alert, canAcknowledge, onAcknowledge, pending }: { alert: Alert; canAcknowledge: boolean; onAcknowledge: () => void; pending: boolean }) {
  return (
    <li className="grid gap-1 border-b border-border px-4 py-3 last:border-b-0">
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone={severityTone(alert.severity)}>{alert.severity}</Badge>
        <AlertTitle alert={alert} />
        {alert.acknowledgedAt != null && <Badge tone="neutral"><CheckIcon aria-hidden="true" /> acknowledged</Badge>}
        {canAcknowledge && alert.acknowledgedAt == null && (
          <Button size="sm" variant="outline" className="ml-auto" onClick={onAcknowledge} disabled={pending} aria-label={`Acknowledge ${alert.title}`}>
            Acknowledge
          </Button>
        )}
      </div>
      {alert.message && <p className="text-sm text-muted-foreground">{alert.message}</p>}
      <p className="font-mono text-2xs text-muted-foreground">
        {alert.resource ? `${alert.resource} · ` : ""}
        {alert.type} · opened {formatAgo(alert.openedAt)} · last seen {formatAgo(alert.lastSeenAt)}
      </p>
    </li>
  );
}

/** Monitoring: the open alerts first (most severe on top), then the history of resolved ones. */
export function MonitoringView({ api = notificationsApi, pollMs = POLL_MS }: { api?: NotificationsApi; pollMs?: number }) {
  const { me } = useAuth();
  const canAcknowledge = me?.role === "owner" || me?.role === "admin" || me?.role === "developer";
  const open = usePolled((signal) => api.alerts.list({ state: "open", limit: 100 }, { signal }), "monitoring-open", { intervalMs: pollMs });
  const resolved = usePolled((signal) => api.alerts.list({ state: "resolved", limit: 25 }, { signal }), "monitoring-resolved", { intervalMs: pollMs });
  const [more, setMore] = React.useState<Alert[]>([]);
  const [cursor, setCursor] = React.useState<string | null | undefined>(undefined);
  const act = useAction();

  const openItems = sortOpenAlerts(open.data?.items ?? []);
  const nextCursor = cursor === undefined ? (resolved.data?.nextCursor ?? null) : cursor;
  const history = [...(resolved.data?.items ?? []), ...more];

  async function loadMore() {
    if (!nextCursor) return;
    const page = await act.run(() => api.alerts.list({ state: "resolved", limit: 25, cursor: nextCursor }));
    if (page) {
      setMore((m) => [...m, ...page.items]);
      setCursor(page.nextCursor);
    }
  }

  async function acknowledge(alert: Alert) {
    if (await act.runOk(() => api.alerts.acknowledge(alert.id), "Alert acknowledged")) await open.refresh();
  }

  return (
    <div className="flex flex-col gap-8">
      <PageHeader
        eyebrow="Monitoring"
        title="Alerts"
        description="Problems Aethera detected: failed deployments, unreachable servers, full disks, crashing containers, expiring certificates."
        actions={
          <Button variant="outline" onClick={() => { void open.refresh(); void resolved.refresh(); }} disabled={open.refreshing} aria-label="Refresh alerts">
            <RefreshCwIcon className={open.refreshing ? "animate-spin motion-reduce:animate-none" : undefined} aria-hidden="true" /> Refresh
          </Button>
        }
      />
      <FormError message={act.error} />

      <section className="grid gap-3" aria-label="Open alerts">
        <SectionTitle index="01">Open</SectionTitle>
        {open.error && !open.data && <ErrorPanel error={open.error} onRetry={() => void open.refresh()} title="Could not load the alerts" />}
        {open.loading && <Skeleton className="h-24 w-full" />}
        {open.data && openItems.length === 0 && (
          <DotGrid fade className="border border-dashed border-border">
            <EmptyState icon={BellIcon} label="Alerts · none open" title="All quiet" description="Nothing needs attention. New problems show up here and in your notification channels." className="py-10" />
          </DotGrid>
        )}
        {openItems.length > 0 && (
          <ul className="border border-border bg-card" aria-label="Open alerts list">
            {openItems.map((a) => (
              <OpenAlertRow key={a.id} alert={a} canAcknowledge={canAcknowledge} pending={act.pending} onAcknowledge={() => void acknowledge(a)} />
            ))}
          </ul>
        )}
      </section>

      <section className="grid gap-3" aria-label="Resolved alerts">
        <SectionTitle index="02">Resolved history</SectionTitle>
        {resolved.loading && <Skeleton className="h-16 w-full" />}
        {resolved.data && history.length === 0 && <p className="text-xs text-muted-foreground">No resolved alerts yet.</p>}
        {history.length > 0 && (
          <ul className="border border-border bg-card" aria-label="Resolved alerts list">
            {history.map((a) => (
              <li key={a.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-border px-4 py-2.5 text-sm last:border-b-0">
                <Badge tone="success">resolved</Badge>
                <AlertTitle alert={a} />
                <span className="ml-auto font-mono text-2xs text-muted-foreground">
                  {a.type} · opened {formatAgo(a.openedAt)} · resolved {formatAgo(a.resolvedAt)}
                </span>
              </li>
            ))}
          </ul>
        )}
        {nextCursor && (
          <Button variant="outline" className="w-fit" onClick={() => void loadMore()} disabled={act.pending}>
            Load more
          </Button>
        )}
      </section>
    </div>
  );
}
