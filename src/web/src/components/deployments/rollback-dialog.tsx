"use client";

import * as React from "react";
import { Loader2Icon, Undo2Icon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { ErrorPanel } from "@/components/servers/common";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { shortSha, TRIGGER_LABEL } from "@/lib/resources/status";
import type { Deployment } from "@/lib/resources/types";
import { errorMessage } from "@/lib/servers/errors";
import { formatAgo, toNum } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

/**
 * Rollback picker: the application's deployments whose image is still on the server (`canRollbackTo`), newest first.
 * A rollback is a new deployment that re-applies the chosen one's frozen configuration and image without building.
 */
export function RollbackDialog({
  applicationId,
  kind = "applications",
  currentDeploymentId,
  open,
  onOpenChange,
  onStarted,
  api = resourcesApi,
  preselect,
}: {
  applicationId: string;
  kind?: "applications" | "services";
  currentDeploymentId?: string | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Called with the new rollback deployment. */
  onStarted: (deployment: Deployment) => void;
  api?: ResourcesApi;
  preselect?: string;
}) {
  const list = usePolled((signal) => (kind === "services" ? api.services.deployments(applicationId, { signal }) : api.applications.deployments(applicationId, { limit: 50 }, { signal })), `rollback:${applicationId}`, {
    intervalMs: null,
    enabled: open,
  });
  const [picked, setPicked] = React.useState<string | null>(preselect ?? null);
  const [pending, setPending] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  const candidates = (list.data?.items ?? []).filter((d) => d.canRollbackTo && d.id !== currentDeploymentId);
  const selected = picked ?? candidates[0]?.id ?? null;

  async function submit() {
    if (!selected) return;
    setPending(true);
    setError(null);
    try {
      const next = await api.deployments.rollback(selected);
      onOpenChange(false);
      onStarted(next);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={(o) => !pending && onOpenChange(o)}>
      <DialogContent className="max-w-xl">
        <DialogHeader>
          <DialogTitle>Roll back</DialogTitle>
          <DialogDescription>
            Pick the deployment to return to. Its image and configuration are re-applied from what was frozen at that time; secrets stay at the versions it pinned.
          </DialogDescription>
        </DialogHeader>
        {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load deployments" />}
        {list.loading && <p className="text-sm text-muted-foreground">Loading deployments…</p>}
        {list.data && candidates.length === 0 && (
          <p role="status" className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">
            No earlier deployment can be rolled back to. A rollback point needs a successful deployment whose image is still on the server.
          </p>
        )}
        {candidates.length > 0 && (
          <fieldset className="grid max-h-72 gap-1.5 overflow-auto">
            <legend className="sr-only">Deployment to roll back to</legend>
            {candidates.map((d) => (
              <label
                key={d.id}
                className="flex cursor-pointer items-start gap-3 border border-border px-3 py-2 text-sm has-[:checked]:border-primary has-[:checked]:bg-primary-soft"
              >
                <input type="radio" name="rollback-target" className="mt-1" checked={selected === d.id} onChange={() => setPicked(d.id)} />
                <span className="min-w-0 flex-1">
                  <span className="font-mono text-xs">
                    #{toNum(d.number)} · {TRIGGER_LABEL[d.trigger] ?? d.trigger} · {formatAgo(d.createdAt)}
                  </span>
                  <span className="block truncate text-xs text-muted-foreground">
                    {d.commitMessage ?? d.imageRef ?? "-"} {d.commitSha ? `(${shortSha(d.commitSha)})` : ""}
                  </span>
                </span>
              </label>
            ))}
          </fieldset>
        )}
        {error && (
          <p role="alert" className="border border-danger bg-danger-soft px-3 py-2 text-xs text-danger">
            {error}
          </p>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={pending}>
            Cancel
          </Button>
          <Button onClick={() => void submit()} disabled={!selected || pending}>
            {pending ? <Loader2Icon className="animate-spin" aria-hidden="true" /> : <Undo2Icon aria-hidden="true" />}
            Roll back
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
