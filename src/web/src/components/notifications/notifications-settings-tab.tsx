"use client";

import * as React from "react";
import { ErrorPanel, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { notificationsApi, type NotificationsApi } from "@/lib/notifications/api";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { formatAgo } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";
import { ChannelsSection } from "./channels-section";
import { EmailSection } from "./email-section";

/** The most recent delivery attempts of every channel, failures with the error text. */
function DeliveriesSection({ api, version }: { api: NotificationsApi; version: number }) {
  const deliveries = usePolled((signal) => api.deliveries.list({ limit: 20 }, { signal }), `notification-deliveries:${version}`, { intervalMs: 30_000 });
  const channels = usePolled((signal) => api.channels.list({ signal }), `notification-delivery-channels:${version}`, { intervalMs: null });
  const names = new Map((channels.data?.items ?? []).map((c) => [c.id, c.name]));
  const rows = deliveries.data?.items ?? [];
  return (
    <section className="grid gap-3" aria-label="Recent deliveries">
      <SectionTitle index="03">Recent deliveries</SectionTitle>
      {deliveries.error && !deliveries.data && <ErrorPanel error={deliveries.error} onRetry={() => void deliveries.refresh()} title="Could not load the deliveries" />}
      {deliveries.loading && <Skeleton className="h-16 w-full" />}
      {deliveries.data && rows.length === 0 && <p className="text-xs text-muted-foreground">Nothing has been sent yet.</p>}
      {rows.length > 0 && (
        <ul className="border border-border bg-card">
          {rows.map((d) => (
            <li key={d.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-border px-4 py-2 text-sm last:border-b-0">
              <Badge tone={d.status === "sent" ? "success" : "danger"}>{d.status}</Badge>
              <span className="font-medium">{names.get(d.channelId) ?? "deleted channel"}</span>
              <span className="font-mono text-xs text-muted-foreground">{d.eventType}</span>
              <span className="ml-auto font-mono text-2xs text-muted-foreground">{formatAgo(d.createdAt)}</span>
              {d.error && <p className="w-full break-words text-xs text-danger">{d.error}</p>}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** Settings > Notifications: email transport, channels with their events, and recent deliveries (R1.2, ADR 0010). */
export function NotificationsSettingsTab({ api = notificationsApi, resources = resourcesApi }: { api?: NotificationsApi; resources?: ResourcesApi }) {
  // A test or a change adds deliveries: re-reading them is what this counter is for.
  const [version, setVersion] = React.useState(0);
  return (
    <div className="grid gap-8">
      <EmailSection api={api} />
      <ChannelsSection api={api} resources={resources} onChanged={() => setVersion((v) => v + 1)} />
      <DeliveriesSection api={api} version={version} />
    </div>
  );
}
