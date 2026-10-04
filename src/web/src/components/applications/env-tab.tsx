"use client";

import * as React from "react";
import { EyeIcon, EyeOffIcon, KeyRoundIcon, PlusIcon, Trash2Icon, UploadIcon } from "lucide-react";
import { FormError, SaveButton, useAction } from "@/components/resources/form";
import { ErrorPanel, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Textarea } from "@/components/ui/textarea";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import type { EnvVar } from "@/lib/resources/types";
import { parseDotenv } from "@/lib/resources/wizard";
import { toNum } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

export type WorkloadKind = "applications" | "services";

function EnvDialog({
  open,
  onOpenChange,
  existing,
  onSave,
}: {
  open: boolean;
  onOpenChange: (o: boolean) => void;
  existing: EnvVar | null;
  onSave: (v: { key: string; value: string; secret: boolean; build: boolean; runtime: boolean }) => Promise<boolean>;
}) {
  const [key, setKey] = React.useState(existing?.key ?? "");
  const [value, setValue] = React.useState(existing?.isSecret ? "" : (existing?.value ?? ""));
  const [secret, setSecret] = React.useState(existing?.isSecret ?? false);
  const [build, setBuild] = React.useState(existing?.isBuildTime ?? true);
  const [runtime, setRuntime] = React.useState(existing?.isRuntime ?? true);
  const [show, setShow] = React.useState(false);
  const act = useAction();
  return (
    <Dialog open={open} onOpenChange={(o) => !act.pending && onOpenChange(o)}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            const ok = await act.run(() => onSave({ key: key.trim(), value, secret, build, runtime }));
            if (ok) onOpenChange(false);
          }}
        >
          <DialogHeader>
            <DialogTitle>{existing ? `Edit ${existing.key}` : "Add variable"}</DialogTitle>
            <DialogDescription>
              {existing?.isSecret ? "Leave the value empty to keep the current secret value." : "Variables are applied on the next deployment."}
            </DialogDescription>
          </DialogHeader>
          <Field label="Name">{(c) => <Input {...c} className="font-mono" value={key} disabled={Boolean(existing)} onChange={(e) => setKey(e.target.value)} required />}</Field>
          <Field label="Value">
            {(c) => (
              <div className="flex gap-2">
                <Input {...c} className="font-mono" type={secret && !show ? "password" : "text"} autoComplete="off" value={value} onChange={(e) => setValue(e.target.value)} />
                {secret && (
                  <Button type="button" variant="outline" size="icon" aria-label={show ? "Hide value" : "Show value"} onClick={() => setShow((v) => !v)}>
                    {show ? <EyeOffIcon aria-hidden="true" /> : <EyeIcon aria-hidden="true" />}
                  </Button>
                )}
              </div>
            )}
          </Field>
          <div className="flex flex-wrap gap-4 text-sm">
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={secret} disabled={Boolean(existing)} onChange={(e) => setSecret(e.target.checked)} /> Secret (encrypted, masked)
            </label>
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={build} onChange={(e) => setBuild(e.target.checked)} /> Build time
            </label>
            <label className="flex items-center gap-2">
              <input type="checkbox" checked={runtime} onChange={(e) => setRuntime(e.target.checked)} /> Runtime
            </label>
          </div>
          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)} disabled={act.pending}>
              Cancel
            </Button>
            <SaveButton pending={act.pending} disabled={!key.trim()}>
              Save
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function ImportDialog({ open, onOpenChange, onImport }: { open: boolean; onOpenChange: (o: boolean) => void; onImport: (text: string) => Promise<boolean> }) {
  const [text, setText] = React.useState("");
  const act = useAction();
  const count = parseDotenv(text).length;
  return (
    <Dialog open={open} onOpenChange={(o) => !act.pending && onOpenChange(o)}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            if (await act.run(() => onImport(text))) {
              setText("");
              onOpenChange(false);
            }
          }}
        >
          <DialogHeader>
            <DialogTitle>Import a .env file</DialogTitle>
            <DialogDescription>Existing names are updated, new ones are added. Imported values are plain variables; mark sensitive ones as secrets afterwards.</DialogDescription>
          </DialogHeader>
          <Field label=".env contents" hint={`${count} variable${count === 1 ? "" : "s"} found`}>
            {(c) => <Textarea {...c} rows={8} className="font-mono text-xs" value={text} onChange={(e) => setText(e.target.value)} placeholder={"KEY=value\nOTHER=value"} />}
          </Field>
          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <SaveButton pending={act.pending} disabled={count === 0}>
              Import
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Environment variables of an application or service. Secret values are masked: the API never returns them. */
export function EnvTab({ kind, id, slug, api = resourcesApi }: { kind: WorkloadKind; id: string; slug: string; api?: ResourcesApi }) {
  const list = usePolled((signal) => api.envVars.list(kind, id, { signal }), `env:${id}`, { intervalMs: null });
  const [editing, setEditing] = React.useState<EnvVar | "new" | null>(null);
  const [importing, setImporting] = React.useState(false);
  const act = useAction();

  async function save(v: { key: string; value: string; secret: boolean; build: boolean; runtime: boolean }): Promise<boolean> {
    if (editing && editing !== "new") {
      const body: Record<string, unknown> = { isBuildTime: v.build, isRuntime: v.runtime };
      if (editing.isSecret) {
        if (v.value && editing.secretId) await api.secrets.rotate(editing.secretId, v.value);
      } else body.value = v.value;
      await api.envVars.update(kind, id, editing.id, body);
    } else if (v.secret) {
      const secret = await api.secrets.create({ name: `${slug}_${v.key}`, value: v.value, workloadId: id });
      await api.envVars.create(kind, id, { key: v.key, secretId: secret.id, isBuildTime: v.build, isRuntime: v.runtime });
    } else {
      await api.envVars.create(kind, id, { key: v.key, value: v.value, isBuildTime: v.build, isRuntime: v.runtime });
    }
    await list.refresh();
    return true;
  }

  const rows = list.data?.items ?? [];
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle
        index="01"
        actions={
          <div className="flex gap-2">
            <Button size="sm" variant="outline" onClick={() => setImporting(true)}>
              <UploadIcon aria-hidden="true" /> Import .env
            </Button>
            <Button size="sm" onClick={() => setEditing("new")}>
              <PlusIcon aria-hidden="true" /> Add variable
            </Button>
          </div>
        }
      >
        Environment variables
      </SectionTitle>
      <p className="text-xs text-muted-foreground">Changes apply on the next deployment.</p>
      <FormError message={act.error} />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load variables" />}
      {list.loading && <Skeleton className="h-24 w-full" />}
      {list.data && rows.length === 0 && <p className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">No variables yet.</p>}
      {rows.length > 0 && (
        <div className="overflow-x-auto border border-border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Value</TableHead>
                <TableHead>Scope</TableHead>
                <TableHead className="w-24 text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rows.map((v) => (
                <TableRow key={v.id}>
                  <TableCell className="font-mono text-xs">{v.key}</TableCell>
                  <TableCell className="max-w-72 truncate font-mono text-xs text-muted-foreground">
                    {v.isSecret ? (
                      <span className="inline-flex items-center gap-1.5">
                        <KeyRoundIcon className="size-3 text-lime" aria-hidden="true" /> ••••••••{v.secretVersion ? ` (v${toNum(v.secretVersion)})` : ""}
                      </span>
                    ) : (
                      v.value
                    )}
                  </TableCell>
                  <TableCell>
                    <span className="flex gap-1">
                      {v.isBuildTime && <Badge tone="outline">build</Badge>}
                      {v.isRuntime && <Badge tone="outline">runtime</Badge>}
                    </span>
                  </TableCell>
                  <TableCell className="text-right">
                    <Button size="sm" variant="ghost" onClick={() => setEditing(v)}>
                      Edit
                    </Button>
                    <Button
                      size="icon-sm"
                      variant="ghost"
                      aria-label={`Delete ${v.key}`}
                      onClick={async () => {
                        if (!window.confirm(`Delete ${v.key}?`)) return;
                        if (await act.runOk(() => api.envVars.remove(kind, id, v.id), `${v.key} deleted`)) await list.refresh();
                      }}
                    >
                      <Trash2Icon aria-hidden="true" />
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
      {editing && <EnvDialog key={editing === "new" ? "new" : editing.id} open onOpenChange={(o) => !o && setEditing(null)} existing={editing === "new" ? null : editing} onSave={save} />}
      {importing && (
        <ImportDialog
          open
          onOpenChange={(o) => !o && setImporting(false)}
          onImport={async (text) => {
            await api.envVars.importDotenv(kind, id, text);
            await list.refresh();
            return true;
          }}
        />
      )}
    </div>
  );
}
