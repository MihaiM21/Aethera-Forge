"use client";

import * as React from "react";
import { ContainerIcon, PencilIcon, PlusIcon, RefreshCwIcon, Trash2Icon } from "lucide-react";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { PageHeader } from "@/components/aethera/page-header";
import { FormError, SaveButton, fieldError, useAction } from "@/components/resources/form";
import { ErrorPanel } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import type { Registry } from "@/lib/resources/types";
import { formatAgo } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

function RegistryDialog({ registry, api, onClose, onSaved }: { registry: Registry | null; api: ResourcesApi; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = React.useState(registry?.name ?? "");
  const [url, setUrl] = React.useState(registry?.url ?? "");
  const [username, setUsername] = React.useState(registry?.username ?? "");
  const [password, setPassword] = React.useState("");
  const act = useAction();
  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            const body = { name: name.trim(), url: url.trim(), username: username.trim() || undefined, ...(password ? { password } : {}) };
            const ok = await act.runOk(() => (registry ? api.registries.update(registry.id, body) : api.registries.create(body)), registry ? "Registry updated" : "Registry added");
            if (ok) {
              onSaved();
              onClose();
            }
          }}
        >
          <DialogHeader>
            <DialogTitle>{registry ? `Edit ${registry.name}` : "Add registry"}</DialogTitle>
            <DialogDescription>The password is stored as a managed secret and only sent to the server that pulls the image.</DialogDescription>
          </DialogHeader>
          <Field label="Name" error={fieldError(act.fields, "name")}>{(c) => <Input {...c} value={name} onChange={(e) => setName(e.target.value)} required />}</Field>
          <Field label="URL" error={fieldError(act.fields, "url")} hint="e.g. ghcr.io or registry.example.com:5000">
            {(c) => <Input {...c} className="font-mono" placeholder="ghcr.io" value={url} onChange={(e) => setUrl(e.target.value)} required />}
          </Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Username">{(c) => <Input {...c} autoComplete="off" value={username} onChange={(e) => setUsername(e.target.value)} />}</Field>
            <Field label="Password or token" hint={registry?.hasCredentials ? "Leave empty to keep the stored one." : undefined}>
              {(c) => <Input {...c} type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />}
            </Field>
          </div>
          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
              Cancel
            </Button>
            <SaveButton pending={act.pending} disabled={!name.trim() || !url.trim()}>
              {registry ? "Save" : "Add registry"}
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Container registries used to pull private images. Credentials are write-only. */
export function RegistriesView({ api = resourcesApi }: { api?: ResourcesApi }) {
  const list = usePolled((signal) => api.registries.list({ limit: 100, sort: "name" }, { signal }), "registries", { intervalMs: 30_000 });
  const [editing, setEditing] = React.useState<Registry | "new" | null>(null);
  const act = useAction();
  const rows = list.data?.items ?? [];
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        eyebrow="Images"
        title="Registries"
        description="Container registries used to pull private images."
        actions={
          <>
            <Button variant="outline" onClick={() => void list.refresh()} disabled={list.refreshing} aria-label="Refresh registries">
              <RefreshCwIcon className={list.refreshing ? "animate-spin motion-reduce:animate-none" : undefined} aria-hidden="true" /> Refresh
            </Button>
            <Button onClick={() => setEditing("new")}>
              <PlusIcon aria-hidden="true" /> Add registry
            </Button>
          </>
        }
      />
      <FormError message={act.error} />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load registries" />}
      {list.loading && <Skeleton className="h-24 w-full" />}
      {list.data && rows.length === 0 && (
        <DotGrid fade className="border border-dashed border-border">
          <EmptyState icon={ContainerIcon} label="Registries · empty" title="No registries yet" description="Connect a container registry to pull and push images." action={<Button onClick={() => setEditing("new")}>Add registry</Button>} />
        </DotGrid>
      )}
      {rows.length > 0 && (
        <ul className="border border-border bg-card">
          {rows.map((r) => (
            <li key={r.id} className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-4 py-3 last:border-b-0">
              <div className="min-w-0">
                <p className="font-medium">{r.name}</p>
                <p className="truncate font-mono text-xs text-muted-foreground">
                  {r.url}
                  {r.username ? ` · ${r.username}` : ""}
                </p>
              </div>
              <div className="flex items-center gap-2">
                <Badge tone={r.hasCredentials ? "success" : "neutral"}>{r.hasCredentials ? "credentials stored" : "anonymous"}</Badge>
                <span className="font-mono text-2xs text-muted-foreground">{formatAgo(r.updatedAt)}</span>
                <Button size="icon-sm" variant="ghost" aria-label={`Edit ${r.name}`} onClick={() => setEditing(r)}>
                  <PencilIcon aria-hidden="true" />
                </Button>
                <Button
                  size="icon-sm"
                  variant="ghost"
                  aria-label={`Delete ${r.name}`}
                  onClick={async () => {
                    if (!window.confirm(`Delete the registry ${r.name}?`)) return;
                    if (await act.runOk(() => api.registries.remove(r.id), "Registry deleted")) await list.refresh();
                  }}
                >
                  <Trash2Icon aria-hidden="true" />
                </Button>
              </div>
            </li>
          ))}
        </ul>
      )}
      {editing && <RegistryDialog registry={editing === "new" ? null : editing} api={api} onClose={() => setEditing(null)} onSaved={() => void list.refresh()} />}
    </div>
  );
}
