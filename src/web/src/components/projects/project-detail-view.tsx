"use client";

import * as React from "react";
import Link from "next/link";
import { BoxesIcon, ChevronLeftIcon, FolderKanbanIcon, LayersIcon, PlusIcon } from "lucide-react";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { PageHeader } from "@/components/aethera/page-header";
import { WorkloadStatusPill } from "@/components/deployments/badges";
import { FormError, SaveButton, useAction } from "@/components/resources/form";
import { ErrorPanel, SectionTitle } from "@/components/servers/common";
import { DetailLink } from "@/components/shell/detail-link";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { toast } from "@/components/ui/sonner";
import { isApiError } from "@/lib/api/errors";
import { navigateTo } from "@/lib/navigate";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { slugify } from "@/lib/resources/status";
import { usePolled } from "@/lib/servers/use-polled";
import { useResourceId } from "@/lib/use-resource-id";

function NewEnvironmentDialog({ projectId, api, onClose, onCreated }: { projectId: string; api: ResourcesApi; onClose: () => void; onCreated: () => void }) {
  const [name, setName] = React.useState("");
  const [production, setProduction] = React.useState(false);
  const act = useAction();
  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            if (await act.runOk(() => api.projects.createEnvironment(projectId, { name: name.trim(), slug: slugify(name), isProduction: production }), "Environment created")) {
              onCreated();
              onClose();
            }
          }}
        >
          <DialogHeader>
            <DialogTitle>New environment</DialogTitle>
            <DialogDescription>Environments separate staging from production inside a project.</DialogDescription>
          </DialogHeader>
          <Field label="Name">{(c) => <Input {...c} value={name} onChange={(e) => setName(e.target.value)} required />}</Field>
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={production} onChange={(e) => setProduction(e.target.checked)} /> Production environment
          </label>
          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
              Cancel
            </Button>
            <SaveButton pending={act.pending} disabled={!name.trim()}>
              Create
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Project page: environments, and the applications and services that live in them. */
export function ProjectDetailView({ api = resourcesApi, id: idProp }: { api?: ResourcesApi; id?: string }) {
  const routeId = useResourceId();
  const id = idProp ?? routeId;
  const project = usePolled((signal) => api.projects.get(id!, { signal }), String(id), { intervalMs: 15_000, enabled: id !== null });
  const apps = usePolled((signal) => api.applications.list({ projectId: id!, limit: 100, sort: "name" }, { signal }), `p-apps:${id}`, { intervalMs: 8000, enabled: id !== null });
  const services = usePolled((signal) => api.services.list({ projectId: id!, limit: 100, sort: "name" }, { signal }), `p-svcs:${id}`, { intervalMs: 8000, enabled: id !== null });
  const [env, setEnv] = React.useState<string>("");
  const [addingEnv, setAddingEnv] = React.useState(false);
  const [deleting, setDeleting] = React.useState(false);
  const p = project.data;

  React.useEffect(() => {
    if (p) document.title = `${p.name} · Projects · Aethera`;
  }, [p]);

  if (id === null || (project.loading && !p)) {
    return (
      <div className="flex flex-col gap-6" role="status" aria-label="Loading project">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40 w-full" />
      </div>
    );
  }
  if (isApiError(project.error) && project.error.status === 404) {
    return (
      <DotGrid fade className="border border-border">
        <EmptyState
          icon={FolderKanbanIcon}
          label="404"
          title="Project not found"
          description="It may have been deleted, or the link is wrong."
          action={
            <Button asChild>
              <Link href="/projects">All projects</Link>
            </Button>
          }
        />
      </DotGrid>
    );
  }
  if (!p) return <ErrorPanel error={project.error} title="Could not load the project" onRetry={() => void project.refresh()} />;

  const envFilter = env && p.environments.some((e) => e.id === env) ? env : "";
  const appRows = (apps.data?.items ?? []).filter((a) => !envFilter || a.environmentId === envFilter);
  const svcRows = (services.data?.items ?? []).filter((s) => !envFilter || s.environmentId === envFilter);
  const envName = (envId: string) => p.environments.find((e) => e.id === envId)?.name ?? "";

  return (
    <div className="flex flex-col gap-8">
      <PageHeader
        eyebrow="Project"
        title={p.name}
        description={p.description ?? <span className="font-mono text-xs">{p.slug}</span>}
        actions={
          <>
            <Button variant="outline" asChild>
              <Link href="/projects">
                <ChevronLeftIcon aria-hidden="true" /> All projects
              </Link>
            </Button>
            <Button variant="outline" asChild>
              <Link href={`/services/new?project=${encodeURIComponent(p.id)}`}>
                <LayersIcon aria-hidden="true" /> Add service
              </Link>
            </Button>
            <Button asChild>
              <Link href={`/applications/new?project=${encodeURIComponent(p.id)}`}>
                <PlusIcon aria-hidden="true" /> Add application
              </Link>
            </Button>
          </>
        }
      />

      <section className="flex flex-col gap-3">
        <SectionTitle
          index="01"
          actions={
            <Button size="sm" variant="outline" onClick={() => setAddingEnv(true)}>
              <PlusIcon aria-hidden="true" /> Environment
            </Button>
          }
        >
          Environments
        </SectionTitle>
        <div className="flex flex-wrap gap-2" role="group" aria-label="Filter by environment">
          <Button size="sm" variant={envFilter === "" ? "primary" : "outline"} onClick={() => setEnv("")}>
            All
          </Button>
          {p.environments.map((e) => (
            <Button key={e.id} size="sm" variant={envFilter === e.id ? "primary" : "outline"} onClick={() => setEnv(e.id)}>
              {e.name}
              {e.isProduction && <Badge tone="brand">prod</Badge>}
            </Button>
          ))}
        </div>
      </section>

      <section className="flex flex-col gap-3">
        <SectionTitle index="02">Applications</SectionTitle>
        {apps.data && appRows.length === 0 && (
          <p className="flex items-center gap-2 border border-dashed border-border px-4 py-5 text-sm text-muted-foreground">
            <BoxesIcon className="size-4 text-lime" aria-hidden="true" /> No applications here yet.
          </p>
        )}
        {appRows.length > 0 && (
          <ul className="border border-border bg-card">
            {appRows.map((a) => (
              <li key={a.id} className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-4 py-3 last:border-b-0">
                <div className="min-w-0">
                  <DetailLink href={`/applications/${encodeURIComponent(a.id)}`} className="font-medium hover:text-lime">
                    {a.name}
                  </DetailLink>
                  <p className="font-mono text-2xs text-muted-foreground">
                    {envName(a.environmentId)} · {a.sourceKind}
                  </p>
                </div>
                <WorkloadStatusPill status={a.status} />
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="flex flex-col gap-3">
        <SectionTitle index="03">Services</SectionTitle>
        {services.data && svcRows.length === 0 && (
          <p className="flex items-center gap-2 border border-dashed border-border px-4 py-5 text-sm text-muted-foreground">
            <LayersIcon className="size-4 text-lime" aria-hidden="true" /> No services here yet.
          </p>
        )}
        {svcRows.length > 0 && (
          <ul className="border border-border bg-card">
            {svcRows.map((s) => (
              <li key={s.id} className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-4 py-3 last:border-b-0">
                <div className="min-w-0">
                  <DetailLink href={`/services/${encodeURIComponent(s.id)}`} className="font-medium hover:text-lime">
                    {s.name}
                  </DetailLink>
                  <p className="font-mono text-2xs text-muted-foreground">
                    {envName(s.environmentId)} · {s.image}
                  </p>
                </div>
                <WorkloadStatusPill status={s.status} />
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="grid max-w-xl gap-3 border border-danger/50 p-4">
        <h2 className="text-base font-medium text-danger">Danger zone</h2>
        <p className="text-sm text-muted-foreground">Deleting a project removes its environments, applications and services from Aethera. Containers on servers are not touched.</p>
        <div>
          <Button variant="destructive" onClick={() => setDeleting(true)}>
            Delete project
          </Button>
        </div>
      </section>

      {addingEnv && <NewEnvironmentDialog projectId={p.id} api={api} onClose={() => setAddingEnv(false)} onCreated={() => void project.refresh()} />}
      <ConfirmDialog
        open={deleting}
        onOpenChange={setDeleting}
        resourceName={p.slug}
        title="Delete project"
        description={`This deletes ${p.name} and everything in it. Type its slug to confirm.`}
        onConfirm={async (confirm) => {
          await api.projects.remove(p.id, confirm, true);
          toast.success("Project deleted");
          navigateTo("/projects");
        }}
      />
    </div>
  );
}
