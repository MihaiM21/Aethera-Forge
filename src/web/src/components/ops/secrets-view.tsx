"use client";

import * as React from "react";
import { EyeIcon, KeyRoundIcon, PlusIcon, RefreshCwIcon, RotateCwIcon, SearchIcon, Trash2Icon } from "lucide-react";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { PageHeader } from "@/components/aethera/page-header";
import { FormError, SaveButton, fieldError, useAction } from "@/components/resources/form";
import { CopyButton, ErrorPanel, NativeSelect } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Textarea } from "@/components/ui/textarea";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import type { Secret } from "@/lib/resources/types";
import { formatAgo, toNum } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

/** Managed secrets (registry passwords, git credentials, generated service passwords) belong to another resource and are changed there. */
export function isManaged(s: Pick<Secret, "managed" | "purpose">): boolean {
  return s.managed || s.purpose !== "user";
}

function CreateDialog({ api, onClose, onCreated }: { api: ResourcesApi; onClose: () => void; onCreated: () => void }) {
  const projects = usePolled((signal) => api.projects.list({ limit: 100, sort: "name" }, { signal }), "sec-projects", { intervalMs: null });
  const [name, setName] = React.useState("");
  const [value, setValue] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [projectId, setProjectId] = React.useState("");
  const act = useAction();
  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            if (await act.runOk(() => api.secrets.create({ name: name.trim(), value, description: description.trim() || undefined, projectId: projectId || undefined }), "Secret created")) {
              onCreated();
              onClose();
            }
          }}
        >
          <DialogHeader>
            <DialogTitle>New secret</DialogTitle>
            <DialogDescription>The value is encrypted at rest and never shown again, except to administrators who reveal it.</DialogDescription>
          </DialogHeader>
          <Field label="Name" error={fieldError(act.fields, "name")}>{(c) => <Input {...c} className="font-mono" value={name} onChange={(e) => setName(e.target.value)} required />}</Field>
          <Field label="Value" error={fieldError(act.fields, "value")}>
            {(c) => <Textarea {...c} rows={3} className="font-mono text-xs" autoComplete="off" value={value} onChange={(e) => setValue(e.target.value)} required />}
          </Field>
          <Field label="Description">{(c) => <Input {...c} value={description} onChange={(e) => setDescription(e.target.value)} />}</Field>
          <Field label="Scope" hint="Organization-wide secrets can only be created by administrators.">
            {(c) => (
              <NativeSelect {...c} value={projectId} onChange={(e) => setProjectId(e.target.value)}>
                <option value="">Whole organization</option>
                {(projects.data?.items ?? []).map((p) => (
                  <option key={p.id} value={p.id}>
                    Project: {p.name}
                  </option>
                ))}
              </NativeSelect>
            )}
          </Field>
          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
              Cancel
            </Button>
            <SaveButton pending={act.pending} disabled={!name.trim() || !value}>
              Create secret
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function RotateDialog({ secret, api, onClose, onDone }: { secret: Secret; api: ResourcesApi; onClose: () => void; onDone: () => void }) {
  const [value, setValue] = React.useState("");
  const act = useAction();
  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            if (await act.runOk(() => api.secrets.rotate(secret.id, value), "Secret rotated")) {
              onDone();
              onClose();
            }
          }}
        >
          <DialogHeader>
            <DialogTitle>Rotate {secret.name}</DialogTitle>
            <DialogDescription>Creates a new version. Deployments keep the version they pinned until the next deploy.</DialogDescription>
          </DialogHeader>
          <Field label="New value">{(c) => <Textarea {...c} rows={3} className="font-mono text-xs" autoComplete="off" value={value} onChange={(e) => setValue(e.target.value)} required />}</Field>
          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
              Cancel
            </Button>
            <SaveButton pending={act.pending} disabled={!value}>
              Rotate
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function RevealDialog({ secret, api, onClose }: { secret: Secret; api: ResourcesApi; onClose: () => void }) {
  const [value, setValue] = React.useState<string | null>(null);
  const act = useAction();
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Reveal {secret.name}</DialogTitle>
          <DialogDescription>Needs the administrator role. Every reveal is recorded in the audit log.</DialogDescription>
        </DialogHeader>
        {value === null ? (
          <Button onClick={async () => { const r = await act.run(() => api.secrets.reveal(secret.id)); if (r) setValue(r.value); }} disabled={act.pending}>
            <EyeIcon aria-hidden="true" /> Reveal the current value
          </Button>
        ) : (
          <div className="flex items-center gap-2">
            <code className="min-w-0 flex-1 break-all border border-border bg-muted px-2 py-1.5 font-mono text-xs">{value}</code>
            <CopyButton text={value} />
          </div>
        )}
        <FormError message={act.error} />
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Close
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

const SCOPE_LABEL: Record<string, string> = { organization: "organization", project: "project", environment: "environment", workload: "workload" };

/** Secrets of the organization: names and metadata only. Values are masked; rotate, reveal (administrators) and delete are explicit actions. */
export function SecretsView({ api = resourcesApi }: { api?: ResourcesApi }) {
  const [q, setQ] = React.useState("");
  const [scope, setScope] = React.useState("");
  const list = usePolled((signal) => api.secrets.list({ limit: 200, sort: "name", scope: scope || undefined }, { signal }), `secrets:${scope}`, { intervalMs: 20_000 });
  const [creating, setCreating] = React.useState(false);
  const [rotating, setRotating] = React.useState<Secret | null>(null);
  const [revealing, setRevealing] = React.useState<Secret | null>(null);
  const act = useAction();
  const rows = (list.data?.items ?? []).filter((s) => !q.trim() || s.name.toLowerCase().includes(q.trim().toLowerCase()));
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        eyebrow="Security"
        title="Secrets"
        description="Encrypted variables and credentials, masked in the UI and redacted from logs."
        actions={
          <>
            <Button variant="outline" onClick={() => void list.refresh()} disabled={list.refreshing} aria-label="Refresh secrets">
              <RefreshCwIcon className={list.refreshing ? "animate-spin motion-reduce:animate-none" : undefined} aria-hidden="true" /> Refresh
            </Button>
            <Button onClick={() => setCreating(true)}>
              <PlusIcon aria-hidden="true" /> New secret
            </Button>
          </>
        }
      />
      <div className="flex flex-wrap items-end gap-3">
        <div className="relative min-w-56 flex-1 sm:max-w-sm">
          <label htmlFor="secret-search" className="sr-only">
            Search secrets
          </label>
          <SearchIcon className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
          <Input id="secret-search" type="search" placeholder="Search name…" value={q} onChange={(e) => setQ(e.target.value)} className="pl-8" />
        </div>
        <label htmlFor="secret-scope" className="sr-only">
          Scope
        </label>
        <NativeSelect id="secret-scope" value={scope} onChange={(e) => setScope(e.target.value)} className="w-44">
          <option value="">All scopes</option>
          {Object.entries(SCOPE_LABEL).map(([v, l]) => (
            <option key={v} value={v}>
              {l}
            </option>
          ))}
        </NativeSelect>
      </div>
      <FormError message={act.error} />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load secrets" />}
      {list.loading && <Skeleton className="h-24 w-full" />}
      {list.data && list.data.items.length === 0 && !scope && (
        <DotGrid fade className="border border-dashed border-border">
          <EmptyState icon={KeyRoundIcon} label="Secrets · empty" title="No secrets yet" description="Secrets are encrypted at rest, masked in the UI and redacted from logs." action={<Button onClick={() => setCreating(true)}>New secret</Button>} />
        </DotGrid>
      )}
      {rows.length > 0 && (
        <ul className="border border-border bg-card">
          {rows.map((s) => (
            <li key={s.id} className="grid gap-3 border-b border-border px-4 py-3 last:border-b-0 md:grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)_auto] md:items-center">
              <div className="min-w-0">
                <p className="flex items-center gap-2 truncate font-mono text-sm">
                  <KeyRoundIcon className="size-3.5 shrink-0 text-lime" aria-hidden="true" />
                  {s.name}
                </p>
                <p className="mt-0.5 truncate text-xs text-muted-foreground">{s.description ?? <span className="font-mono">••••••••</span>}</p>
              </div>
              <div className="flex flex-wrap items-center gap-1.5">
                <Badge tone="outline">{s.scope}</Badge>
                {isManaged(s) && <Badge tone="brand">{s.managedBy ? `managed · ${s.managedBy.type}` : s.purpose}</Badge>}
                <span className="font-mono text-2xs text-muted-foreground">v{toNum(s.currentVersion)} · {formatAgo(s.rotatedAt ?? s.updatedAt)}</span>
              </div>
              <div className="flex gap-1">
                <Button size="icon-sm" variant="ghost" aria-label={`Reveal ${s.name}`} onClick={() => setRevealing(s)}>
                  <EyeIcon aria-hidden="true" />
                </Button>
                {!isManaged(s) && (
                  <>
                    <Button size="icon-sm" variant="ghost" aria-label={`Rotate ${s.name}`} onClick={() => setRotating(s)}>
                      <RotateCwIcon aria-hidden="true" />
                    </Button>
                    <Button
                      size="icon-sm"
                      variant="ghost"
                      aria-label={`Delete ${s.name}`}
                      onClick={async () => {
                        if (!window.confirm(`Delete the secret ${s.name}? Environment variables bound to it stop working.`)) return;
                        if (await act.runOk(() => api.secrets.remove(s.id), "Secret deleted")) await list.refresh();
                      }}
                    >
                      <Trash2Icon aria-hidden="true" />
                    </Button>
                  </>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
      {creating && <CreateDialog api={api} onClose={() => setCreating(false)} onCreated={() => void list.refresh()} />}
      {rotating && <RotateDialog secret={rotating} api={api} onClose={() => setRotating(null)} onDone={() => void list.refresh()} />}
      {revealing && <RevealDialog secret={revealing} api={api} onClose={() => setRevealing(null)} />}
    </div>
  );
}
