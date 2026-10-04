"use client";

import * as React from "react";
import Link from "next/link";
import { LayersIcon, PlayIcon, RocketIcon, RotateCcwIcon, SquareIcon } from "lucide-react";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { LogViewer } from "@/components/aethera/log-viewer";
import { PageHeader } from "@/components/aethera/page-header";
import { DomainsTab } from "@/components/applications/domains-tab";
import { EnvTab } from "@/components/applications/env-tab";
import { StorageTab } from "@/components/applications/storage-tab";
import { WorkloadStatusPill } from "@/components/deployments/badges";
import { DeploymentRowView, toRow } from "@/components/deployments/deployments-view";
import { FormError, SaveButton, fieldError, useAction } from "@/components/resources/form";
import { CopyButton, ErrorPanel, Facts, NativeSelect, SectionTitle } from "@/components/servers/common";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { toast } from "@/components/ui/sonner";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "@/lib/api/errors";
import { navigateTo } from "@/lib/navigate";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { runtimeToChunks } from "@/lib/resources/use-logs";
import type { Service, ServiceTemplate } from "@/lib/resources/types";
import { formatAgo, toNum } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";
import { useResourceId } from "@/lib/use-resource-id";

export const SERVICE_TABS = ["overview", "deployments", "logs", "environment", "domains", "storage", "settings"] as const;
export type ServiceTab = (typeof SERVICE_TABS)[number];

const TAB_LABEL: Record<ServiceTab, string> = {
  overview: "Overview",
  deployments: "Deployments",
  logs: "Logs",
  environment: "Environment",
  domains: "Domains",
  storage: "Storage",
  settings: "Settings",
};

export function serviceTabFromHash(hash: string): ServiceTab {
  const h = hash.replace(/^#/, "");
  return (SERVICE_TABS as readonly string[]).includes(h) ? (h as ServiceTab) : "overview";
}

/** How other workloads of the environment reach the service: its slug is a DNS alias on the environment network. */
export function connectionInfo(service: Pick<Service, "slug" | "runtime">): Array<{ label: string; value: string }> {
  return service.runtime.ports.map((p) => ({ label: `${toNum(p.containerPort)}/${p.protocol}`, value: `${service.slug}:${toNum(p.containerPort)}` }));
}

function Overview({ service, api }: { service: Service; api: ResourcesApi }) {
  const deployments = usePolled((signal) => api.services.deployments(service.id, { signal }), `sv-deps:${service.id}`, { intervalMs: 6000 });
  const [now] = React.useState(() => new Date());
  const conn = connectionInfo(service);
  const items = deployments.data?.items ?? [];
  return (
    <div className="grid gap-8 lg:grid-cols-[minmax(0,1fr)_minmax(0,22rem)]">
      <div className="flex min-w-0 flex-col gap-6">
        <section className="flex flex-col gap-3">
          <SectionTitle index="01">Connection</SectionTitle>
          <p className="text-xs text-muted-foreground">
            Applications and services in the same environment reach this service on the environment network by its name. Nothing is published on the server&apos;s own ports.
          </p>
          {conn.length === 0 && <p className="text-sm text-muted-foreground">This service exposes no ports.</p>}
          <ul className="grid gap-2">
            {conn.map((c) => (
              <li key={c.value} className="flex items-center justify-between gap-3 border border-border bg-card px-3 py-2">
                <code className="font-mono text-sm">{c.value}</code>
                <CopyButton text={c.value} label="Copy" />
              </li>
            ))}
          </ul>
          <p className="text-xs text-muted-foreground">Credentials are secrets: open the Environment tab to reveal them (administrators).</p>
        </section>
        <section className="flex flex-col gap-3">
          <SectionTitle index="02">Recent deployments</SectionTitle>
          {deployments.data && items.length === 0 && <p className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">Not deployed yet.</p>}
          {items.length > 0 && (
            <ul className="border border-border bg-card">
              {items.slice(0, 5).map((d) => (
                <DeploymentRowView key={d.id} row={toRow({ deployment: d, applicationName: service.name, applicationSlug: service.slug, serverId: service.serverId })} now={now} showApp={false} />
              ))}
            </ul>
          )}
        </section>
      </div>
      <aside aria-label="Service facts" className="flex flex-col gap-3">
        <SectionTitle index="03">Details</SectionTitle>
        <Facts
          rows={[
            ["Template", service.templateKey],
            ["Version", service.templateVersion],
            ["Image", service.image],
            ["Restart", service.runtime.restartPolicy],
            ["Health", `${service.runtime.healthCheck.type}${service.runtime.healthCheck.path ? ` ${service.runtime.healthCheck.path}` : ""}`],
            ["Status", service.state.statusReason ?? service.state.status],
            ["Changed", formatAgo(service.state.statusChangedAt, now)],
          ]}
        />
      </aside>
    </div>
  );
}

function ServiceLogs({ service, api }: { service: Service; api: ResourcesApi }) {
  const logs = usePolled((signal) => api.services.logs(service.id, { tail: 300 }, { signal }), `sv-logs:${service.id}`, { intervalMs: 3000 });
  const containers = logs.data?.containers ?? [];
  const chunks = React.useMemo(() => runtimeToChunks(logs.data, containers.length > 1), [logs.data, containers.length]);
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle index="01">Service logs</SectionTitle>
      {logs.error && !logs.data && <ErrorPanel error={logs.error} onRetry={() => void logs.refresh()} title="Could not read the logs" />}
      {logs.data && containers.length === 0 && <p className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">No running container yet.</p>}
      {containers.map((c) => c.error && <p key={c.container} role="status" className="border border-dashed border-warning/70 bg-warning-soft px-3 py-2 text-xs">{c.container.slice(0, 12)}: {c.error}</p>)}
      {containers.length > 0 && <LogViewer label="Service logs" chunks={chunks} status="live · every 3s" showTimestamps maxHeight="34rem" />}
    </div>
  );
}

function ServiceSettings({ service, template, api, onSaved }: { service: Service; template?: ServiceTemplate; api: ResourcesApi; onSaved: () => void }) {
  const [name, setName] = React.useState(service.name);
  const [version, setVersion] = React.useState(service.templateVersion ?? "");
  const [confirming, setConfirming] = React.useState(false);
  const act = useAction();
  const picked = template?.versions.find((v) => v.version === version);
  const dirty = name.trim() !== service.name || (picked !== undefined && picked.image !== service.image);
  return (
    <div className="grid max-w-3xl gap-8">
      <form
        className="grid gap-5"
        onSubmit={async (e) => {
          e.preventDefault();
          const body: Parameters<ResourcesApi["services"]["update"]>[1] = { name: name.trim() };
          if (picked && picked.image !== service.image) {
            body.image = picked.image;
            body.templateVersion = picked.version;
          }
          if (await act.runOk(() => api.services.update(service.id, body), "Service updated")) onSaved();
        }}
      >
        <SectionTitle index="01">General</SectionTitle>
        <Field label="Name" error={fieldError(act.fields, "name")}>{(c) => <Input {...c} value={name} onChange={(e) => setName(e.target.value)} />}</Field>
        {template && (
          <Field label="Version" hint="A different version applies on the next deployment. Check the upgrade notes of the database first: some cannot go back.">
            {(c) => (
              <NativeSelect {...c} value={version} onChange={(e) => setVersion(e.target.value)}>
                {!template.versions.some((v) => v.version === service.templateVersion) && <option value={service.templateVersion ?? ""}>{service.image}</option>}
                {template.versions.map((v) => (
                  <option key={v.version} value={v.version}>
                    {v.version}
                  </option>
                ))}
              </NativeSelect>
            )}
          </Field>
        )}
        <p className="font-mono text-2xs text-muted-foreground">slug: {service.slug} · id: {service.id}</p>
        <FormError message={act.error} />
        <div>
          <SaveButton pending={act.pending} disabled={!dirty || !name.trim()} />
        </div>
      </form>
      <section className="grid gap-3 border border-danger/50 p-4">
        <h3 className="text-base font-medium text-danger">Danger zone</h3>
        <p className="text-sm text-muted-foreground">Deleting the service removes it from Aethera. Its data volumes and the running container stay on the server until you remove them.</p>
        <div>
          <Button variant="destructive" onClick={() => setConfirming(true)}>
            Delete service
          </Button>
        </div>
      </section>
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        resourceName={service.slug}
        title="Delete service"
        description={`This deletes ${service.name}. Type its slug to confirm.`}
        onConfirm={async (confirm) => {
          await api.services.remove(service.id, confirm);
          toast.success("Service deleted");
          navigateTo("/services");
        }}
      />
    </div>
  );
}

/** Service page: connection info, deployments, logs, environment (with credential reveal), domains, storage and settings. */
export function ServiceDetailView({ api = resourcesApi, id: idProp }: { api?: ResourcesApi; id?: string }) {
  const routeId = useResourceId();
  const id = idProp ?? routeId;
  const [tab, setTab] = React.useState<ServiceTab>("overview");
  const act = useAction();

  React.useEffect(() => {
    const apply = () => setTab(serviceTabFromHash(window.location.hash));
    apply();
    window.addEventListener("hashchange", apply);
    return () => window.removeEventListener("hashchange", apply);
  }, []);
  function onTab(next: string) {
    const t = serviceTabFromHash(next);
    setTab(t);
    try {
      window.history.replaceState(null, "", `#${t}`);
    } catch {
      /* ignore */
    }
  }

  const svc = usePolled((signal) => api.services.get(id!, { signal }), String(id), { intervalMs: 5000, enabled: id !== null });
  const templates = usePolled((signal) => api.services.templates({ signal }), "service-templates", { intervalMs: null });
  const s = svc.data;
  React.useEffect(() => {
    if (s) document.title = `${s.name} · Services · Aethera`;
  }, [s]);

  if (id === null || (svc.loading && !s)) {
    return (
      <div className="flex flex-col gap-6" role="status" aria-label="Loading service">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40 w-full" />
      </div>
    );
  }
  if (isApiError(svc.error) && svc.error.status === 404) {
    return (
      <DotGrid fade className="border border-border">
        <EmptyState
          icon={LayersIcon}
          label="404"
          title="Service not found"
          description="It may have been deleted, or the link is wrong."
          action={
            <Button asChild>
              <Link href="/services">All services</Link>
            </Button>
          }
        />
      </DotGrid>
    );
  }
  if (!s) return <ErrorPanel error={svc.error} title="Could not load the service" onRetry={() => void svc.refresh()} />;

  const busy = act.pending || s.state.status === "deploying";
  const run = (fn: () => Promise<unknown>, msg: string) =>
    void act.runOk(fn, msg).then((ok) => {
      if (ok) void svc.refresh();
    });
  const template = templates.data?.find((t) => t.key === s.templateKey);

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        eyebrow={`Service · ${s.templateKey}`}
        title={
          <span className="flex flex-wrap items-center gap-3">
            {s.name}
            <WorkloadStatusPill status={s.state.status} />
          </span>
        }
        description={<span className="font-mono text-xs">{s.image}</span>}
        actions={
          <>
            <Button variant="outline" onClick={() => run(() => api.services.lifecycle(s.id, "restart"), "Restart queued")} disabled={busy}>
              <RotateCcwIcon aria-hidden="true" /> Restart
            </Button>
            {s.state.status === "stopped" ? (
              <Button variant="outline" onClick={() => run(() => api.services.lifecycle(s.id, "start"), "Start queued")} disabled={busy}>
                <PlayIcon aria-hidden="true" /> Start
              </Button>
            ) : (
              <Button variant="outline" onClick={() => run(() => api.services.lifecycle(s.id, "stop"), "Stop queued")} disabled={busy}>
                <SquareIcon aria-hidden="true" /> Stop
              </Button>
            )}
            <Button
              onClick={async () => {
                const d = await act.run(() => api.services.deploy(s.id));
                if (d) navigateTo(`/deployments/${encodeURIComponent(d.id)}`);
              }}
              disabled={busy}
            >
              <RocketIcon aria-hidden="true" /> {s.state.currentDeploymentId ? "Redeploy" : "Deploy"}
            </Button>
          </>
        }
      />
      <FormError message={act.error} />
      {s.state.status === "failed" && s.state.statusReason && (
        <div role="alert" className="border border-danger/60 bg-danger-soft px-4 py-3 text-sm">
          <p className="font-medium text-danger">The service is failing</p>
          <p className="text-xs text-muted-foreground">{s.state.statusReason}</p>
        </div>
      )}
      <Tabs value={tab} onValueChange={onTab}>
        <TabsList className="overflow-x-auto" aria-label="Service sections">
          {SERVICE_TABS.map((t) => (
            <TabsTrigger key={t} value={t}>
              {TAB_LABEL[t]}
            </TabsTrigger>
          ))}
        </TabsList>
        <TabsContent value="overview">
          <Overview service={s} api={api} />
        </TabsContent>
        <TabsContent value="deployments">
          {tab === "deployments" && <ServiceDeployments service={s} api={api} />}
        </TabsContent>
        <TabsContent value="logs">{tab === "logs" && <ServiceLogs service={s} api={api} />}</TabsContent>
        <TabsContent value="environment">{tab === "environment" && <EnvTab kind="services" id={s.id} slug={s.slug} api={api} />}</TabsContent>
        <TabsContent value="domains">{tab === "domains" && <DomainsTab kind="services" id={s.id} api={api} />}</TabsContent>
        <TabsContent value="storage">{tab === "storage" && <StorageTab kind="services" id={s.id} api={api} />}</TabsContent>
        <TabsContent value="settings">{tab === "settings" && <ServiceSettings service={s} template={template} api={api} onSaved={() => void svc.refresh()} />}</TabsContent>
      </Tabs>
    </div>
  );
}

function ServiceDeployments({ service, api }: { service: Service; api: ResourcesApi }) {
  const list = usePolled((signal) => api.services.deployments(service.id, { signal }), `sv-dl:${service.id}`, { intervalMs: 5000 });
  const [now] = React.useState(() => new Date());
  const items = list.data?.items ?? [];
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle index="01">Deployments</SectionTitle>
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load deployments" />}
      {list.data && items.length === 0 && <p className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">No deployments.</p>}
      {items.length > 0 && (
        <ul className="border border-border bg-card">
          {items.map((d) => (
            <DeploymentRowView key={d.id} row={toRow({ deployment: d, applicationName: service.name, applicationSlug: service.slug, serverId: service.serverId })} now={now} showApp={false} />
          ))}
        </ul>
      )}
    </div>
  );
}
