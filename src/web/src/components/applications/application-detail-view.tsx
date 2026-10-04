"use client";

import * as React from "react";
import Link from "next/link";
import { BoxesIcon, PlayIcon, RefreshCwIcon, RocketIcon, RotateCcwIcon, SquareIcon, Undo2Icon } from "lucide-react";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { LogViewer } from "@/components/aethera/log-viewer";
import { PageHeader } from "@/components/aethera/page-header";
import { DeploymentStatusPill, WorkloadStatusPill } from "@/components/deployments/badges";
import { DeploymentRowView, toRow } from "@/components/deployments/deployments-view";
import { DeploymentPipeline } from "@/components/deployments/pipeline";
import { RollbackDialog } from "@/components/deployments/rollback-dialog";
import { FormError, useAction } from "@/components/resources/form";
import { ErrorPanel, Facts, NativeSelect, SectionTitle } from "@/components/servers/common";
import { DetailLink } from "@/components/shell/detail-link";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "@/lib/api/errors";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { repoLabel, TRIGGER_LABEL } from "@/lib/resources/status";
import { runtimeToChunks } from "@/lib/resources/use-logs";
import type { Application } from "@/lib/resources/types";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { formatAgo, toNum } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";
import { useResourceId } from "@/lib/use-resource-id";
import { BuildTab, HealthTab, NetworkingTab, ResourcesTab, SettingsTab } from "./config-tabs";
import { DomainsTab, CertBadge, DnsBadge } from "./domains-tab";
import { EnvTab } from "./env-tab";
import { StorageTab } from "./storage-tab";
import { navigateTo } from "@/lib/navigate";

export const APP_TABS = ["overview", "deployments", "logs", "environment", "domains", "storage", "networking", "resources", "build", "health", "settings"] as const;
export type AppTab = (typeof APP_TABS)[number];

const TAB_LABEL: Record<AppTab, string> = {
  overview: "Overview",
  deployments: "Deployments",
  logs: "Logs",
  environment: "Environment",
  domains: "Domains",
  storage: "Storage",
  networking: "Networking",
  resources: "Resources",
  build: "Build",
  health: "Health",
  settings: "Settings",
};

export function tabFromHash(hash: string): AppTab {
  const h = hash.replace(/^#/, "");
  return (APP_TABS as readonly string[]).includes(h) ? (h as AppTab) : "overview";
}

function sourceLabel(app: Application): string {
  if (app.gitSource) return `${repoLabel(app.gitSource.repositoryUrl)} @ ${app.gitSource.branch}`;
  if (app.image) return `${app.image.image}:${app.image.tag}`;
  if (app.compose) return "compose file";
  return "-";
}

function OverviewTab({ app, api, onChanged }: { app: Application; api: ResourcesApi; onChanged: () => void }) {
  const latest = usePolled((signal) => api.applications.deployments(app.id, { limit: 5 }, { signal }), `latest:${app.id}`, { intervalMs: 5000 });
  const domains = usePolled((signal) => api.domains.list({ workloadId: app.id, limit: 20 }, { signal }), `ov-domains:${app.id}`, { intervalMs: 20_000 });
  const items = latest.data?.items ?? [];
  const current = items.find((d) => d.id === app.state.currentDeploymentId) ?? items[0];
  const [now] = React.useState(() => new Date());
  return (
    <div className="grid gap-8 lg:grid-cols-[minmax(0,1fr)_minmax(0,22rem)]">
      <div className="flex min-w-0 flex-col gap-8">
        <section className="flex flex-col gap-3">
          <SectionTitle index="01">Latest deployment</SectionTitle>
          {latest.loading && <Skeleton className="h-40 w-full" />}
          {latest.data && !current && (
            <p className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">Not deployed yet. Use Deploy to build and start it.</p>
          )}
          {current && (
            <div className="grid gap-4 border border-border bg-card p-4">
              <div className="flex flex-wrap items-center gap-3">
                <DetailLink href={`/deployments/${current.id}`} className="font-mono text-sm font-medium hover:text-lime">
                  #{toNum(current.number)}
                </DetailLink>
                <DeploymentStatusPill status={current.status} />
                <span className="font-mono text-2xs text-muted-foreground">
                  {TRIGGER_LABEL[current.trigger] ?? current.trigger} · {formatAgo(current.createdAt, now)}
                </span>
              </div>
              {current.commitMessage && <p className="text-sm">{current.commitMessage}</p>}
              <DeploymentPipeline deployment={current} />
            </div>
          )}
        </section>
        {items.length > 1 && (
          <section className="flex flex-col gap-3">
            <SectionTitle index="02">Recent deployments</SectionTitle>
            <ul className="border border-border bg-card">
              {items.slice(0, 5).map((d) => (
                <DeploymentRowView key={d.id} row={{ deployment: d, applicationId: app.id }} now={now} showApp={false} />
              ))}
            </ul>
          </section>
        )}
      </div>
      <aside className="flex flex-col gap-6" aria-label="Application facts">
        <section className="flex flex-col gap-3">
          <SectionTitle index="03">Details</SectionTitle>
          <Facts
            rows={[
              ["Source", sourceLabel(app)],
              ["Kind", app.sourceKind],
              ["Strategy", app.runtime.strategy],
              ["Restart", app.runtime.restartPolicy],
              ["Ports", app.runtime.ports.map((p) => `${toNum(p.containerPort)}/${p.protocol}`).join(", ") || "-"],
              ["Status", app.state.statusReason ?? app.state.status],
              ["Changed", formatAgo(app.state.statusChangedAt, now)],
              ["Server", <DetailLink key="s" href={`/servers/${app.serverId}`} className="text-lime hover:underline">open server</DetailLink>],
              ["Project", <DetailLink key="p" href={`/projects/${app.projectId}`} className="text-lime hover:underline">open project</DetailLink>],
            ]}
          />
        </section>
        <section className="flex flex-col gap-3">
          <SectionTitle index="04">Domains</SectionTitle>
          {(domains.data?.items.length ?? 0) === 0 && <p className="text-sm text-muted-foreground">No domains. Add one in the Domains tab.</p>}
          <ul className="grid gap-2">
            {(domains.data?.items ?? []).map((d) => (
              <li key={d.id} className="grid gap-1 border border-border px-3 py-2">
                <a href={`${d.httpsEnabled ? "https" : "http"}://${d.hostname}`} target="_blank" rel="noreferrer" className="truncate font-mono text-xs hover:text-lime">
                  {d.hostname}
                </a>
                <span className="flex gap-1.5">
                  <DnsBadge domain={d} />
                  <CertBadge domain={d} />
                </span>
              </li>
            ))}
          </ul>
        </section>
        <Button variant="outline" onClick={onChanged}>
          <RefreshCwIcon aria-hidden="true" /> Refresh
        </Button>
      </aside>
    </div>
  );
}

function DeploymentsTab({ app, api, onRollback }: { app: Application; api: ResourcesApi; onRollback: () => void }) {
  const [status, setStatus] = React.useState("");
  const list = usePolled((signal) => api.applications.deployments(app.id, { limit: 30, status: status || undefined }, { signal }), `deps:${app.id}:${status}`, { intervalMs: 5000 });
  const [now, setNow] = React.useState(() => new Date());
  React.useEffect(() => {
    const t = setInterval(() => setNow(new Date()), 5000);
    return () => clearInterval(t);
  }, []);
  const rows = list.data?.items ?? [];
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle
        index="01"
        actions={
          <div className="flex items-center gap-2">
            <label htmlFor="app-dep-status" className="sr-only">
              Status
            </label>
            <NativeSelect id="app-dep-status" value={status} onChange={(e) => setStatus(e.target.value)} className="w-36">
              <option value="">All</option>
              <option value="running">Running</option>
              <option value="failed">Failed</option>
              <option value="superseded">Superseded</option>
            </NativeSelect>
            <Button size="sm" variant="outline" onClick={onRollback}>
              <Undo2Icon aria-hidden="true" /> Roll back…
            </Button>
          </div>
        }
      >
        Deployments
      </SectionTitle>
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load deployments" />}
      {list.loading && <Skeleton className="h-28 w-full" />}
      {list.data && rows.length === 0 && <p className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">No deployments.</p>}
      {rows.length > 0 && (
        <ul className="border border-border bg-card">
          {rows.map((d) => (
            <DeploymentRowView key={d.id} row={toRow({ deployment: d, applicationName: app.name, applicationSlug: app.slug, serverId: app.serverId })} now={now} showApp={false} />
          ))}
        </ul>
      )}
    </div>
  );
}

const TAILS = [100, 200, 500, 1000];

function LogsTab({ app, api }: { app: Application; api: ResourcesApi }) {
  const [tail, setTail] = React.useState(200);
  const [paused, setPaused] = React.useState(false);
  const logs = usePolled((signal) => api.applications.logs(app.id, { tail }, { signal }), `logs:${app.id}:${tail}`, { intervalMs: paused ? null : 3000 });
  const containers = logs.data?.containers ?? [];
  const chunks = React.useMemo(() => runtimeToChunks(logs.data, containers.length > 1), [logs.data, containers.length]);
  const errors = containers.filter((c) => c.error);
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle
        index="01"
        actions={
          <div className="flex items-center gap-2">
            <label htmlFor="log-tail" className="sr-only">
              Lines
            </label>
            <NativeSelect id="log-tail" value={tail} onChange={(e) => setTail(Number(e.target.value))} className="w-28">
              {TAILS.map((n) => (
                <option key={n} value={n}>
                  last {n}
                </option>
              ))}
            </NativeSelect>
            <Button size="sm" variant="outline" onClick={() => setPaused((p) => !p)} aria-pressed={paused}>
              {paused ? "Resume" : "Pause"}
            </Button>
          </div>
        }
      >
        Application logs
      </SectionTitle>
      {logs.error && !logs.data && <ErrorPanel error={logs.error} onRetry={() => void logs.refresh()} title="Could not read the logs" />}
      {errors.map((c) => (
        <p key={c.container} role="status" className="border border-dashed border-warning/70 bg-warning-soft px-3 py-2 text-xs">
          {c.container.slice(0, 12)}: {c.error}
        </p>
      ))}
      {logs.data && containers.length === 0 && (
        <p className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">No running container. Deploy the application to see its output here.</p>
      )}
      {containers.length > 0 && (
        <LogViewer label="Application logs" chunks={chunks} status={paused ? "paused" : "live · every 3s"} emptyText="The container has not written anything yet." showTimestamps maxHeight="34rem" />
      )}
    </div>
  );
}

/** Application page: header with deploy and lifecycle actions, and the eleven tabs (the active one lives in the hash). */
export function ApplicationDetailView({ api = resourcesApi, servers = serversApi, id: idProp }: { api?: ResourcesApi; servers?: ServersApi; id?: string }) {
  const routeId = useResourceId();
  const id = idProp ?? routeId;
  const [tab, setTab] = React.useState<AppTab>("overview");
  const [rollback, setRollback] = React.useState(false);
  const act = useAction();

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

  const app = usePolled((signal) => api.applications.get(id!, { signal }), String(id), { intervalMs: 5000, enabled: id !== null });
  const a = app.data;
  React.useEffect(() => {
    if (a) document.title = `${a.name} · Applications · Aethera`;
  }, [a]);

  if (id === null || (app.loading && !a)) {
    return (
      <div className="flex flex-col gap-6" role="status" aria-label="Loading application">
        <Skeleton className="h-6 w-24" />
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40 w-full" />
      </div>
    );
  }
  if (isApiError(app.error) && app.error.status === 404) {
    return (
      <DotGrid fade className="border border-border">
        <EmptyState
          icon={BoxesIcon}
          label="404"
          title="Application not found"
          description="It may have been deleted, or the link is wrong."
          action={
            <Button asChild>
              <Link href="/applications">All applications</Link>
            </Button>
          }
        />
      </DotGrid>
    );
  }
  if (!a) return <ErrorPanel error={app.error} title="Could not load the application" onRetry={() => void app.refresh()} />;

  const deploying = a.state.status === "deploying";
  const run = (fn: () => Promise<unknown>, msg: string) => void act.runOk(fn, msg).then((ok) => {
      if (ok) void app.refresh();
    });
  const goToDeployment = (depId: string) => navigateTo(`/deployments/${encodeURIComponent(depId)}`);

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        eyebrow="Application"
        title={
          <span className="flex flex-wrap items-center gap-3">
            {a.name}
            <WorkloadStatusPill status={a.state.status} />
          </span>
        }
        description={
          <span className="font-mono text-xs">
            {sourceLabel(a)} · {a.slug}
          </span>
        }
        actions={
          <>
            <Button variant="outline" onClick={() => run(() => api.applications.lifecycle(a.id, "restart"), "Restart queued")} disabled={act.pending || deploying}>
              <RotateCcwIcon aria-hidden="true" /> Restart
            </Button>
            {a.state.desiredState === "stopped" || a.state.status === "stopped" ? (
              <Button variant="outline" onClick={() => run(() => api.applications.lifecycle(a.id, "start"), "Start queued")} disabled={act.pending || deploying}>
                <PlayIcon aria-hidden="true" /> Start
              </Button>
            ) : (
              <Button variant="outline" onClick={() => run(() => api.applications.lifecycle(a.id, "stop"), "Stop queued")} disabled={act.pending || deploying}>
                <SquareIcon aria-hidden="true" /> Stop
              </Button>
            )}
            <Button
              onClick={async () => {
                const d = await act.run(() => api.applications.deploy(a.id));
                if (d) goToDeployment(d.id);
              }}
              disabled={act.pending}
            >
              <RocketIcon aria-hidden="true" /> Deploy
            </Button>
          </>
        }
      />
      <FormError message={act.error} />
      {a.state.status === "failed" && a.state.statusReason && (
        <div role="alert" className="border border-danger/60 bg-danger-soft px-4 py-3 text-sm">
          <p className="font-medium text-danger">The application is failing</p>
          <p className="text-xs text-muted-foreground">{a.state.statusReason}</p>
        </div>
      )}
      {a.state.currentDeploymentId == null && deploying && <Badge tone="info">first deployment in progress</Badge>}

      <Tabs value={tab} onValueChange={onTab}>
        <TabsList className="overflow-x-auto" aria-label="Application sections">
          {APP_TABS.map((t) => (
            <TabsTrigger key={t} value={t}>
              {TAB_LABEL[t]}
            </TabsTrigger>
          ))}
        </TabsList>
        <TabsContent value="overview">
          <OverviewTab app={a} api={api} onChanged={() => void app.refresh()} />
        </TabsContent>
        <TabsContent value="deployments">
          <DeploymentsTab app={a} api={api} onRollback={() => setRollback(true)} />
        </TabsContent>
        <TabsContent value="logs">{tab === "logs" && <LogsTab app={a} api={api} />}</TabsContent>
        <TabsContent value="environment">{tab === "environment" && <EnvTab kind="applications" id={a.id} slug={a.slug} api={api} />}</TabsContent>
        <TabsContent value="domains">{tab === "domains" && <DomainsTab kind="applications" id={a.id} api={api} />}</TabsContent>
        <TabsContent value="storage">{tab === "storage" && <StorageTab kind="applications" id={a.id} api={api} />}</TabsContent>
        <TabsContent value="networking">{tab === "networking" && <NetworkingTab app={a} api={api} onSaved={() => void app.refresh()} />}</TabsContent>
        <TabsContent value="resources">{tab === "resources" && <ResourcesTab app={a} api={api} onSaved={() => void app.refresh()} />}</TabsContent>
        <TabsContent value="build">{tab === "build" && <BuildTab app={a} api={api} onSaved={() => void app.refresh()} />}</TabsContent>
        <TabsContent value="health">{tab === "health" && <HealthTab app={a} api={api} onSaved={() => void app.refresh()} />}</TabsContent>
        <TabsContent value="settings">{tab === "settings" && <SettingsTab app={a} api={api} servers={servers} onSaved={() => void app.refresh()} />}</TabsContent>
      </Tabs>

      <RollbackDialog applicationId={a.id} currentDeploymentId={a.state.currentDeploymentId} open={rollback} onOpenChange={setRollback} onStarted={(d) => goToDeployment(d.id)} api={api} />
    </div>
  );
}

