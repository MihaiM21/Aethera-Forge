"use client";

import * as React from "react";
import { FormError, SaveButton, fieldError, useAction } from "@/components/resources/form";
import { NativeSelect } from "@/components/servers/common";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import type { BackupsApi } from "@/lib/backups/api";
import { DESTINATION_PRESETS, destinationInput, hasPlaceholder, presetByKey, type DestinationDraft } from "@/lib/backups/presets";
import type { BackupDestination } from "@/lib/backups/types";

function draftOf(destination: BackupDestination | null): DestinationDraft {
  if (!destination) {
    const r2 = presetByKey("r2")!;
    return { presetKey: "r2", name: "", endpoint: r2.endpoint, region: r2.region, bucket: "", pathPrefix: "aethera", accessKeyId: "", secretAccessKey: "", forcePathStyle: r2.forcePathStyle, isDefault: false };
  }
  return {
    presetKey: destination.kind === "local" ? "local" : "custom",
    name: destination.name,
    endpoint: destination.endpoint ?? "",
    region: destination.region ?? "",
    bucket: destination.bucket ?? "",
    pathPrefix: destination.pathPrefix ?? "",
    accessKeyId: destination.accessKeyId ?? "",
    secretAccessKey: "",
    forcePathStyle: destination.forcePathStyle,
    isDefault: destination.isDefault,
  };
}

/**
 * Create or edit a backup destination. Presets fill the endpoint and region (Cloudflare R2: <code>https://&lt;account id&gt;.r2.cloudflarestorage.com</code>,
 * region <code>auto</code>). The secret access key is write-only: when editing, an empty field keeps the stored one. "Test connection" is on the list, because
 * it needs a saved destination.
 */
export function DestinationDialog({ destination, api, onClose, onSaved }: { destination: BackupDestination | null; api: BackupsApi; onClose: () => void; onSaved: () => void }) {
  const editing = destination !== null;
  const [draft, setDraft] = React.useState<DestinationDraft>(() => draftOf(destination));
  const act = useAction();
  const set = (patch: Partial<DestinationDraft>) => setDraft((d) => ({ ...d, ...patch }));
  const preset = presetByKey(draft.presetKey);
  const local = (editing ? destination.kind : preset?.kind ?? "s3") === "local";
  const kind = local ? "local" : "s3";

  function choosePreset(key: string) {
    const p = presetByKey(key);
    if (!p) return;
    set({ presetKey: key, endpoint: p.endpoint, region: p.region, forcePathStyle: p.forcePathStyle });
  }

  const missing = local
    ? !draft.name.trim()
    : !draft.name.trim() || !draft.endpoint.trim() || hasPlaceholder(draft.endpoint) || !draft.bucket.trim() || !draft.accessKeyId.trim() || (!editing && !draft.secretAccessKey);

  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            const body = destinationInput(draft, kind, editing);
            const ok = await act.runOk(() => (editing ? api.destinations.update(destination.id, body) : api.destinations.create(body)), editing ? "Destination updated" : "Destination added");
            if (ok) {
              onSaved();
              onClose();
            }
          }}
        >
          <DialogHeader>
            <DialogTitle>{editing ? `Edit ${destination.name}` : "Add backup destination"}</DialogTitle>
            <DialogDescription>Backups are encrypted before they leave the server. The access key is stored as a managed secret and never shown again.</DialogDescription>
          </DialogHeader>

          {!editing && (
            <Field label="Provider" hint={preset?.hint}>
              {(c) => (
                <NativeSelect {...c} value={draft.presetKey} onChange={(e) => choosePreset(e.target.value)}>
                  {DESTINATION_PRESETS.map((p) => (
                    <option key={p.key} value={p.key}>
                      {p.label}
                    </option>
                  ))}
                </NativeSelect>
              )}
            </Field>
          )}
          <Field label="Name" error={fieldError(act.fields, "name")}>
            {(c) => <Input {...c} value={draft.name} onChange={(e) => set({ name: e.target.value })} placeholder="Turn One R2" required />}
          </Field>

          {!local && (
            <>
              <Field label="Endpoint" error={fieldError(act.fields, "endpoint")} hint={hasPlaceholder(draft.endpoint) ? "Replace the part in <angle brackets>." : "https; plain http only for local addresses."}>
                {(c) => <Input {...c} className="font-mono" value={draft.endpoint} onChange={(e) => set({ endpoint: e.target.value })} required />}
              </Field>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Bucket" error={fieldError(act.fields, "bucket")}>
                  {(c) => <Input {...c} className="font-mono" value={draft.bucket} onChange={(e) => set({ bucket: e.target.value })} required />}
                </Field>
                <Field label="Region" hint="Empty = auto (R2) or us-east-1." error={fieldError(act.fields, "region")}>
                  {(c) => <Input {...c} className="font-mono" value={draft.region} onChange={(e) => set({ region: e.target.value })} />}
                </Field>
              </div>
              <Field label="Folder in the bucket" hint="Optional path prefix, for example aethera." error={fieldError(act.fields, "pathPrefix")}>
                {(c) => <Input {...c} className="font-mono" value={draft.pathPrefix} onChange={(e) => set({ pathPrefix: e.target.value })} />}
              </Field>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Access key id" error={fieldError(act.fields, "accessKeyId")}>
                  {(c) => <Input {...c} className="font-mono" autoComplete="off" value={draft.accessKeyId} onChange={(e) => set({ accessKeyId: e.target.value })} required />}
                </Field>
                <Field label="Secret access key" hint={editing ? "Leave empty to keep the stored key." : undefined} error={fieldError(act.fields, "secretAccessKey")}>
                  {(c) => <Input {...c} type="password" autoComplete="new-password" value={draft.secretAccessKey} onChange={(e) => set({ secretAccessKey: e.target.value })} required={!editing} />}
                </Field>
              </div>
              <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={draft.forcePathStyle} onChange={(e) => set({ forcePathStyle: e.target.checked })} /> Path-style addressing (MinIO and some others)
              </label>
            </>
          )}
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={draft.isDefault} onChange={(e) => set({ isDefault: e.target.checked })} /> Use as the default for new policies
          </label>

          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
              Cancel
            </Button>
            <SaveButton pending={act.pending} disabled={missing}>
              {editing ? "Save" : "Add destination"}
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
