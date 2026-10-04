"use client";

import * as React from "react";
import { FolderKanbanIcon, LayoutTemplateIcon, PlusIcon, RefreshCwIcon } from "lucide-react";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { PageHeader } from "@/components/aethera/page-header";
import { FormError, SaveButton, fieldError, useAction } from "@/components/resources/form";
import { ErrorPanel, NativeSelect } from "@/components/servers/common";
import { DetailLink } from "@/components/shell/detail-link";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Textarea } from "@/components/ui/textarea";
import { navigateTo } from "@/lib/navigate";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { slugify } from "@/lib/resources/status";
import type { Project, ProjectTemplate } from "@/lib/resources/types";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { formatAgo } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";
import { cn } from "@/lib/utils";

/** Create a project, empty or from a template (environments plus placeholder applications and ready-made services). */
export function NewProjectDialog({ api, servers, onClose }: { api: ResourcesApi; servers: ServersApi; onClose: () => void }) {
  const templates = usePolled((signal) => api.projects.templates({ signal }), "project-templates", { intervalMs: null });
  const serverList = usePolled((signal) => servers.list({ limit: 100, sort: "name" }, { signal }), "new-project-servers", { intervalMs: null });
  const [template, setTemplate] = React.useState("empty");
  const [name, setName] = React.useState("");
  const [slug, setSlug] = React.useState("");
  const [touched, setTouched] = React.useState(false);
  const [description, setDescription] = React.useState("");
  const [serverId, setServerId] = React.useState("");
  const act = useAction();
  const chosen: ProjectTemplate | undefined = templates.data?.find((t) => t.key === template);
  const hasServices = chosen?.workloads.some((w) => w.kind === "service") ?? false;
  const effectiveServer = serverId || serverList.data?.items[0]?.id || "";

  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent className="max-w-2xl">
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            const project = await act.run(async () => {
              if (template === "empty") return api.projects.create({ name: name.trim(), slug: slug || undefined, description: description.trim() || undefined });
              return (
                await api.projects.fromTemplate({ templateKey: template, name: name.trim(), slug: slug || undefined, description: description.trim() || undefined, serverId: hasServices ? effectiveServer : undefined })
              ).project;
            }, "Project created");
            if (project) navigateTo(`/projects/${encodeURIComponent(project.id)}`);
          }}
        >
          <DialogHeader>
            <DialogTitle>New project</DialogTitle>
            <DialogDescription>A project groups environments, applications and services.</DialogDescription>
          </DialogHeader>
          <fieldset className="grid gap-2">
            <legend className="mb-1 flex items-center gap-1.5 font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">
              <LayoutTemplateIcon className="size-3" aria-hidden="true" /> Template
            </legend>
            {templates.error && !templates.data && <ErrorPanel error={templates.error} title="Could not load templates" />}
            <div className="grid max-h-56 gap-2 overflow-auto sm:grid-cols-2">
              {(templates.data ?? []).map((t) => (
                <label key={t.key} className={cn("flex cursor-pointer gap-3 border border-border px-3 py-2 text-sm", template === t.key && "border-primary bg-primary-soft")}>
                  <input type="radio" name="template" className="mt-1" checked={template === t.key} onChange={() => setTemplate(t.key)} />
                  <span className="min-w-0">
                    <span className="block font-medium">{t.name}</span>
                    <span className="block text-xs text-muted-foreground">{t.description}</span>
                    {t.workloads.length > 0 && <span className="mt-1 block font-mono text-2xs text-muted-foreground">{t.workloads.map((w) => w.name).join(" · ")}</span>}
                  </span>
                </label>
              ))}
            </div>
          </fieldset>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Name" error={fieldError(act.fields, "name")}>
              {(c) => (
                <Input
                  {...c}
                  value={name}
                  onChange={(e) => {
                    setName(e.target.value);
                    if (!touched) setSlug(slugify(e.target.value));
                  }}
                  required
                />
              )}
            </Field>
            <Field label="Slug" error={fieldError(act.fields, "slug")}>
              {(c) => (
                <Input
                  {...c}
                  className="font-mono"
                  value={slug}
                  onChange={(e) => {
                    setSlug(e.target.value);
                    setTouched(true);
                  }}
                />
              )}
            </Field>
          </div>
          <Field label="Description">{(c) => <Textarea {...c} rows={2} value={description} onChange={(e) => setDescription(e.target.value)} />}</Field>
          {hasServices && (
            <Field label="Server for the services" hint="The template creates its services on this server.">
              {(c) => (
                <NativeSelect {...c} value={effectiveServer} onChange={(e) => setServerId(e.target.value)}>
                  {(serverList.data?.items ?? []).map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.name} · {s.host}
                    </option>
                  ))}
                </NativeSelect>
              )}
            </Field>
          )}
          {hasServices && serverList.data && serverList.data.items.length === 0 && <p className="text-xs text-warning">Add a server first: this template creates services that need one.</p>}
          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
              Cancel
            </Button>
            <SaveButton pending={act.pending} disabled={!name.trim() || (hasServices && !effectiveServer)}>
              Create project
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

export function ProjectRow({ project }: { project: Project }) {
  return (
    <li className="grid gap-3 border-b border-border px-4 py-4 last:border-b-0 md:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)_7rem] md:items-center">
      <div className="min-w-0">
        <DetailLink href={`/projects/${encodeURIComponent(project.id)}`} className="focus-ring block truncate text-md font-medium tracking-subheading hover:text-lime">
          {project.name}
        </DetailLink>
        <p className="truncate text-xs text-muted-foreground">{project.description ?? <span className="font-mono">{project.slug}</span>}</p>
      </div>
      <div className="flex flex-wrap gap-1.5">
        {project.environments.map((e) => (
          <Badge key={e.id} tone={e.isProduction ? "brand" : "outline"}>
            {e.name}
          </Badge>
        ))}
        {project.templateKey && <Badge tone="neutral">{project.templateKey}</Badge>}
      </div>
      <p className="font-mono text-xs text-muted-foreground">{formatAgo(project.updatedAt)}</p>
    </li>
  );
}

export function ProjectsView({ api = resourcesApi, servers = serversApi }: { api?: ResourcesApi; servers?: ServersApi }) {
  const list = usePolled((signal) => api.projects.list({ limit: 100, sort: "name" }, { signal }), "projects", { intervalMs: 15_000 });
  const [creating, setCreating] = React.useState(false);
  const rows = list.data?.items ?? [];
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        eyebrow="Organization"
        title="Projects"
        description="Group applications, services and environments by project."
        actions={
          <>
            <Button variant="outline" onClick={() => void list.refresh()} disabled={list.refreshing} aria-label="Refresh projects">
              <RefreshCwIcon className={list.refreshing ? "animate-spin motion-reduce:animate-none" : undefined} aria-hidden="true" /> Refresh
            </Button>
            <Button onClick={() => setCreating(true)}>
              <PlusIcon aria-hidden="true" /> New project
            </Button>
          </>
        }
      />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load projects" />}
      {list.loading && <Skeleton className="h-28 w-full" />}
      {list.data && rows.length === 0 && (
        <DotGrid fade className="border border-dashed border-border">
          <EmptyState
            icon={FolderKanbanIcon}
            label="Projects · empty"
            title="No projects yet"
            description="Projects group applications, services and environments. Start from a template or an empty project."
            action={<Button onClick={() => setCreating(true)}>New project</Button>}
          />
        </DotGrid>
      )}
      {rows.length > 0 && <ul className="border border-border bg-card">{rows.map((p) => <ProjectRow key={p.id} project={p} />)}</ul>}
      {creating && <NewProjectDialog api={api} servers={servers} onClose={() => setCreating(false)} />}
    </div>
  );
}
