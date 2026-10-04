"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ChevronLeftIcon, RefreshCwIcon, ServerCrashIcon } from "lucide-react";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { PageHeader } from "@/components/aethera/page-header";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "@/lib/api/errors";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { usePolled } from "@/lib/servers/use-polled";
import { useResourceId } from "@/lib/use-resource-id";
import { ErrorPanel } from "./common";
import { ContainersTab, EventsTab, InventoryTab } from "./inventory-tabs";
import { MetricsTab } from "./metrics-tab";
import { OverviewTab } from "./overview-tab";
import { SettingsTab } from "./settings-tab";
import { ServerStatusBadges } from "./status-badges";

export const DETAIL_TABS = ["overview", "metrics", "containers", "inventory", "events", "settings"] as const;
export type DetailTab = (typeof DETAIL_TABS)[number];

const TAB_LABEL: Record<DetailTab, string> = {
  overview: "Overview",
  metrics: "Metrics",
  containers: "Containers",
  inventory: "Images · Volumes · Networks",
  events: "Events",
  settings: "Settings",
};

function tabFromHash(hash: string): DetailTab {
  const h = hash.replace(/^#/, "");
  return (DETAIL_TABS as readonly string[]).includes(h) ? (h as DetailTab) : "overview";
}

/**
 * Server detail. The id comes from the URL (static export, ADR 0005); the tab
 * lives in the hash so a refresh or a shared link lands on the same tab.
 */
export function ServerDetailView({ api = serversApi, id: idProp }: { api?: ServersApi; id?: string }) {
  const router = useRouter();
  const routeId = useResourceId();
  const id = idProp ?? routeId;
  const [tab, setTab] = React.useState<DetailTab>("overview");

  React.useEffect(() => {
    const apply = () => setTab(tabFromHash(window.location.hash));
    apply();
    window.addEventListener("hashchange", apply);
    return () => window.removeEventListener("hashchange", apply);
  }, []);

  function onTab(next: string) {
    const t = tabFromHash(next);
    setTab(t);
    try {
      window.history.replaceState(null, "", `#${t}`);
    } catch {
      /* ignore */
    }
  }

  const server = usePolled((signal) => api.get(id!, { signal }), String(id), { intervalMs: 10_000, enabled: id !== null });
  const health = usePolled((signal) => api.status(id!, { signal }), String(id), { intervalMs: 10_000, enabled: id !== null });

  React.useEffect(() => {
    if (server.data) document.title = `${server.data.name} · Servers · Aethera`;
  }, [server.data]);

  const notFound = isApiError(server.error) && server.error.status === 404;

  function refreshAll() {
    void server.refresh();
    void health.refresh();
  }

  if (id === null || (server.loading && !server.data)) {
    return (
      <div className="flex flex-col gap-6" role="status" aria-label="Loading server">
        <Skeleton className="h-6 w-24" />
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40 w-full" />
      </div>
    );
  }

  if (notFound) {
    return (
      <DotGrid fade className="border border-border">
        <EmptyState
          icon={ServerCrashIcon}
          label="404"
          title="Server not found"
          description="It may have been deleted, or the link is wrong."
          action={
            <Button asChild>
              <Link href="/servers">All servers</Link>
            </Button>
          }
        />
      </DotGrid>
    );
  }

  if (!server.data) {
    return <ErrorPanel error={server.error} title="Could not load the server" onRetry={refreshAll} />;
  }

  const s = server.data;
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        eyebrow="Server"
        title={s.name}
        description={
          <span className="flex flex-wrap items-center gap-2">
            <span className="font-mono text-xs">{s.host}</span>
            {s.roles.map((r) => (
              <Badge key={r} tone="outline">
                {r}
              </Badge>
            ))}
            <Badge tone="neutral">via {s.transport}</Badge>
            {s.lifecycle !== "active" && <Badge tone="warning">{s.lifecycle}</Badge>}
            {s.agentVersion && <span className="font-mono text-2xs">agent {s.agentVersion}</span>}
          </span>
        }
        actions={
          <>
            <Button variant="outline" asChild>
              <Link href="/servers">
                <ChevronLeftIcon aria-hidden="true" /> All servers
              </Link>
            </Button>
            <Button variant="outline" onClick={refreshAll} aria-label="Refresh server">
              <RefreshCwIcon aria-hidden="true" /> Refresh
            </Button>
          </>
        }
      />
      <ServerStatusBadges server={s} health={health.data} />
      {server.error && (
        <p role="status" className="font-mono text-2xs text-warning">
          Could not refresh from the control plane; showing the last loaded data.
        </p>
      )}

      <Tabs value={tab} onValueChange={onTab}>
        <TabsList aria-label="Server sections" className="overflow-x-auto">
          {DETAIL_TABS.map((t) => (
            <TabsTrigger key={t} value={t}>
              {TAB_LABEL[t]}
            </TabsTrigger>
          ))}
        </TabsList>
        <TabsContent value="overview">
          <OverviewTab server={s} health={health.data} healthError={health.error} onRetryHealth={() => void health.refresh()} api={api} />
        </TabsContent>
        <TabsContent value="metrics">
          <MetricsTab serverId={s.id} api={api} />
        </TabsContent>
        <TabsContent value="containers">
          <ContainersTab serverId={s.id} api={api} />
        </TabsContent>
        <TabsContent value="inventory">
          <InventoryTab serverId={s.id} api={api} />
        </TabsContent>
        <TabsContent value="events">
          <EventsTab serverId={s.id} api={api} />
        </TabsContent>
        <TabsContent value="settings">
          <SettingsTab
            server={s}
            api={api}
            onServerChanged={() => refreshAll()}
            onDeleted={() => router.push("/servers")}
          />
        </TabsContent>
      </Tabs>
    </div>
  );
}
