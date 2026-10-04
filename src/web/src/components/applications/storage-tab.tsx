"use client";

import * as React from "react";
import { HardDriveIcon, PlusIcon, Trash2Icon } from "lucide-react";
import { FormError, SaveButton, useAction, fieldError } from "@/components/resources/form";
import { ErrorPanel, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { usePolled } from "@/lib/servers/use-polled";
import type { WorkloadKind } from "./env-tab";

function AddVolumeDialog({ kind, id, api, onClose, onAdded }: { kind: WorkloadKind; id: string; api: ResourcesApi; onClose: () => void; onAdded: () => void }) {
  const [name, setName] = React.useState("");
  const [mount, setMount] = React.useState("");
  const [hostPath, setHostPath] = React.useState("");
  const [readOnly, setReadOnly] = React.useState(false);
  const [backup, setBackup] = React.useState(false);
  const act = useAction();
  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            const ok = await act.run(
              () => api.volumes.create(kind, id, { name: name.trim(), mountPath: mount.trim(), hostPath: hostPath.trim() || undefined, readOnly, backupEnabled: backup }),
              "Volume added",
            );
            if (ok) {
              onAdded();
              onClose();
            }
          }}
        >
          <DialogHeader>
            <DialogTitle>Add volume</DialogTitle>
            <DialogDescription>A named Docker volume survives redeploys. A host path binds a directory of the server and must be allowed by the agent policy.</DialogDescription>
          </DialogHeader>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Name" error={fieldError(act.fields, "name")}>{(c) => <Input {...c} className="font-mono" value={name} onChange={(e) => setName(e.target.value)} required />}</Field>
            <Field label="Mount path" error={fieldError(act.fields, "mountPath")}>
              {(c) => <Input {...c} className="font-mono" placeholder="/data" value={mount} onChange={(e) => setMount(e.target.value)} required />}
            </Field>
          </div>
          <Field label="Host path" hint="Optional. Empty = a named volume." error={fieldError(act.fields, "hostPath")}>
            {(c) => <Input {...c} className="font-mono" placeholder="/srv/app-data" value={hostPath} onChange={(e) => setHostPath(e.target.value)} />}
          </Field>
          <div className="flex flex-wrap gap-4 text-sm">
            <label className="flex items-center gap-2"><input type="checkbox" checked={readOnly} onChange={(e) => setReadOnly(e.target.checked)} /> Read only</label>
            <label className="flex items-center gap-2"><input type="checkbox" checked={backup} onChange={(e) => setBackup(e.target.checked)} /> Include in backups</label>
          </div>
          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>Cancel</Button>
            <SaveButton pending={act.pending} disabled={!name.trim() || !mount.trim()}>Add volume</SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Persistent storage of an application or service. Volumes apply on the next deployment. */
export function StorageTab({ kind, id, api = resourcesApi }: { kind: WorkloadKind; id: string; api?: ResourcesApi }) {
  const list = usePolled((signal) => api.volumes.list(kind, id, { signal }), `volumes:${id}`, { intervalMs: null });
  const [adding, setAdding] = React.useState(false);
  const act = useAction();
  const rows = list.data?.items ?? [];
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle index="01" actions={<Button size="sm" onClick={() => setAdding(true)}><PlusIcon aria-hidden="true" /> Add volume</Button>}>
        Storage
      </SectionTitle>
      <FormError message={act.error} />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load volumes" />}
      {list.loading && <Skeleton className="h-20 w-full" />}
      {list.data && rows.length === 0 && (
        <p className="flex items-center justify-center gap-2 border border-dashed border-border px-4 py-6 text-sm text-muted-foreground">
          <HardDriveIcon className="size-4 text-lime" aria-hidden="true" /> No volumes. Data written inside the container is lost on redeploy.
        </p>
      )}
      <ul className="grid gap-2">
        {rows.map((v) => (
          <li key={v.id} className="flex flex-wrap items-center justify-between gap-3 border border-border bg-card px-4 py-3">
            <div className="min-w-0">
              <p className="font-mono text-sm">{v.name}</p>
              <p className="font-mono text-2xs text-muted-foreground">
                {v.hostPath ? `${v.hostPath} → ` : "volume → "}
                {v.mountPath}
              </p>
            </div>
            <div className="flex items-center gap-2">
              {v.readOnly && <Badge tone="outline">read only</Badge>}
              {v.backupEnabled && <Badge tone="brand">backup</Badge>}
              <Button
                size="icon-sm"
                variant="ghost"
                aria-label={`Remove ${v.name}`}
                onClick={async () => {
                  if (!window.confirm(`Remove the volume entry ${v.name}? The data on the server is kept.`)) return;
                  if (await act.runOk(() => api.volumes.remove(v.id), "Volume removed")) await list.refresh();
                }}
              >
                <Trash2Icon aria-hidden="true" />
              </Button>
            </div>
          </li>
        ))}
      </ul>
      {adding && <AddVolumeDialog kind={kind} id={id} api={api} onClose={() => setAdding(false)} onAdded={() => void list.refresh()} />}
    </div>
  );
}
