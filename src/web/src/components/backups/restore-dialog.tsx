"use client";

import * as React from "react";
import { AlertTriangleIcon, Loader2Icon } from "lucide-react";
import { FormError, fieldError, useAction } from "@/components/resources/form";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import type { BackupsApi } from "@/lib/backups/api";
import type { BackupRun, Job } from "@/lib/backups/types";
import { formatDateTime } from "@/lib/servers/format";

/**
 * Restore a backup (administrators). Two targets: <b>into this service</b> (overwrites its data; a safety backup is taken first, and if that fails nothing is
 * touched) or <b>into a new service</b> made from the same template, which tests a backup without touching production. The exact name of the workload must be typed.
 */
export function RestoreDialog({
  run,
  workloadName,
  canCreateNew,
  api,
  onClose,
  onStarted,
}: {
  run: BackupRun;
  workloadName: string;
  /** A new service can only be made from the backup of a service's database. */
  canCreateNew: boolean;
  api: BackupsApi;
  onClose: () => void;
  onStarted: (job: Job) => void;
}) {
  const [target, setTarget] = React.useState<"same" | "new">("same");
  const [newName, setNewName] = React.useState(`${workloadName}-restore-test`);
  const [typed, setTyped] = React.useState("");
  const act = useAction();
  const confirmed = typed === workloadName && (target === "same" || newName.trim().length > 0);
  const when = formatDateTime(run.finishedAt ?? run.createdAt);

  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            if (!confirmed) return;
            const job = await act.run(
              () => api.runs.restore(run.id, { target, confirm: typed, ...(target === "new" ? { newServiceName: newName.trim() } : {}) }),
              "Restore started",
            );
            if (job) {
              onStarted(job);
              onClose();
            }
          }}
        >
          <DialogHeader>
            <DialogTitle>Restore {workloadName}</DialogTitle>
            <DialogDescription>
              From the backup of {when}
              {run.keyId ? ` (key ${run.keyId})` : ""}. The restore runs as a job: follow it under Jobs.
            </DialogDescription>
          </DialogHeader>

          <fieldset className="grid gap-2">
            <legend className="mb-1 font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Restore into</legend>
            <label className="flex items-start gap-2 border border-border px-3 py-2 text-sm has-[:checked]:border-primary">
              <input type="radio" name="restore-target" className="mt-1" checked={target === "same"} onChange={() => setTarget("same")} />
              <span>
                <span className="block font-medium">This {run.kind === "volume" ? "volume" : "service"} ({workloadName})</span>
                <span className="block text-xs text-muted-foreground">Replaces the current data with the backup.</span>
              </span>
            </label>
            <label className={`flex items-start gap-2 border border-border px-3 py-2 text-sm has-[:checked]:border-primary ${canCreateNew ? "" : "opacity-50"}`}>
              <input type="radio" name="restore-target" className="mt-1" checked={target === "new"} disabled={!canCreateNew} onChange={() => setTarget("new")} />
              <span>
                <span className="block font-medium">A new service</span>
                <span className="block text-xs text-muted-foreground">
                  {canCreateNew
                    ? "Created from the same template and version with its own credentials. Production is not touched: use it to check that a backup works."
                    : "Only the backup of a service's database can be restored into a new service."}
                </span>
              </span>
            </label>
          </fieldset>

          {target === "same" && (
            <div role="alert" className="flex items-start gap-2 border border-danger bg-danger-soft px-3 py-2 text-xs text-danger">
              <AlertTriangleIcon className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
              <p>
                This overwrites the data of {workloadName}. A safety backup is taken first; if it fails, nothing is restored.
                {run.engine === "redis" || run.kind === "volume" ? " The workload is stopped while the data is replaced and started again afterwards." : ""}
              </p>
            </div>
          )}

          {target === "new" && (
            <Field label="Name of the new service" error={fieldError(act.fields, "newServiceName")}>
              {(c) => <Input {...c} value={newName} onChange={(e) => setNewName(e.target.value)} required />}
            </Field>
          )}

          <Field label={`Type ${workloadName} to confirm`} error={fieldError(act.fields, "confirm")}>
            {(c) => <Input {...c} className="font-mono" autoComplete="off" value={typed} onChange={(e) => setTyped(e.target.value)} />}
          </Field>

          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
              Cancel
            </Button>
            <Button type="submit" variant={target === "same" ? "destructive" : "primary"} disabled={!confirmed || act.pending}>
              {act.pending && <Loader2Icon className="animate-spin" aria-hidden="true" />}
              {target === "same" ? "Overwrite and restore" : "Restore into a new service"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
