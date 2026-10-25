"use client";

import * as React from "react";
import { DatabaseBackupIcon, Loader2Icon, PlayIcon, RotateCcwIcon, Trash2Icon } from "lucide-react";
import { PolicyEditor } from "@/components/backups/policy-editor";
import { RestoreDialog } from "@/components/backups/restore-dialog";
import { FormError, useAction } from "@/components/resources/form";
import { ErrorPanel, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { backupsApi, type BackupsApi } from "@/lib/backups/api";
import { describeRetention, describeSchedule } from "@/lib/backups/presets";
import type { BackupPolicy, BackupRun, BackupTarget, RunStatus } from "@/lib/backups/types";
import { useAuth } from "@/lib/auth/auth-context";
import { formatDuration } from "@/lib/resources/status";
import { formatAgo, formatBytes, formatDateTime } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

const STATUS_TONE: Record<RunStatus, "success" | "danger" | "info" | "neutral"> = {
  succeeded: "success",
  failed: "danger",
  running: "info",
  pending: "info",
  cancelled: "neutral",
};

const TRIGGER_LABEL = { scheduled: "scheduled", manual: "manual", preRestore: "before restore" } as const;

const isActive = (r: BackupRun) => r.status === "pending" || r.status === "running";

const canWrite = (role: string | undefined) => role === "owner" || role === "admin" || role === "developer";
const isAdmin = (role: string | undefined) => role === "owner" || role === "admin";

function targetTitle(t: BackupTarget): string {
  return t.kind === "database" ? `Database${t.engine ? ` (${t.engine})` : ""}` : `Volume ${t.name}`;
}

function PolicyCard({
  target,
  policy,
  writable,
  busy,
  onEdit,
  onRun,
  onRemove,
}: {
  target: BackupTarget;
  policy: BackupPolicy | undefined;
  writable: boolean;
  busy: boolean;
  onEdit: () => void;
  onRun: () => void;
  onRemove: () => void;
}) {
  return (
    <li className="flex flex-col gap-2 border border-border bg-card px-4 py-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="flex flex-wrap items-center gap-2 font-medium">
          {targetTitle(target)}
          {target.mountPath && <span className="font-mono text-xs text-muted-foreground">{target.mountPath}</span>}
          {policy ? (
            policy.enabled ? <Badge tone="success">enabled</Badge> : <Badge tone="neutral">disabled</Badge>
          ) : target.supported ? (
            <Badge tone="warning">not backed up</Badge>
          ) : null}
        </p>
        {writable && target.supported && (
          <div className="flex gap-2">
            {policy && (
              <Button size="sm" variant="outline" onClick={onRun} disabled={busy}>
                {busy ? <Loader2Icon className="animate-spin" aria-hidden="true" /> : <PlayIcon aria-hidden="true" />} Back up now
              </Button>
            )}
            <Button size="sm" variant={policy ? "outline" : "primary"} onClick={onEdit}>
              {policy ? "Edit" : "Set up backups"}
            </Button>
            {policy && (
              <Button size="icon-sm" variant="ghost" aria-label={`Remove the policy of ${targetTitle(target)}`} onClick={onRemove}>
                <Trash2Icon aria-hidden="true" />
              </Button>
            )}
          </div>
        )}
      </div>
      {!target.supported && <p className="text-xs text-muted-foreground">{target.reason}</p>}
      {policy && (
        <p className="font-mono text-xs text-muted-foreground">
          {describeSchedule(policy.schedule, policy.timeZone)} · to {policy.destinationName} · {describeRetention(policy)}
          {policy.enabled && policy.nextRunAt ? ` · next ${formatDateTime(policy.nextRunAt)}` : ""}
          {policy.lastStatus ? ` · last ${policy.lastStatus}${policy.lastSuccessAt && policy.lastStatus !== "succeeded" ? `, last success ${formatAgo(policy.lastSuccessAt)}` : ""}` : ""}
        </p>
      )}
    </li>
  );
}

/**
 * Backups of one workload (R1.1, ADR 0010): the policy of its database (supported templates) and of each named volume, "back up now", the history of
 * runs and, for administrators, restore. The schedule's next run times come from the API.
 */
export function WorkloadBackupsTab({
  kind,
  id,
  name,
  api = backupsApi,
  pollMs = 10_000,
}: {
  kind: "applications" | "services";
  id: string;
  /** The workload's name, to be typed to confirm a restore. */
  name?: string;
  api?: BackupsApi;
  pollMs?: number;
}) {
  const { me } = useAuth();
  const writable = canWrite(me?.role);
  const admin = isAdmin(me?.role);
  const workload = name ?? id;

  const config = usePolled((signal) => api.policies.forWorkload(kind, id, { signal }), `backups-${kind}-${id}`, { intervalMs: 30_000 });
  const destinations = usePolled((signal) => api.destinations.list({ signal }), "backup-destinations", { intervalMs: 60_000 });
  const runs = usePolled((signal) => api.runs.list({ workloadId: id }, { signal }), `backup-runs-${id}`, { intervalMs: pollMs });
  const act = useAction();
  const save = useAction();
  const [editing, setEditing] = React.useState<BackupTarget | null>(null);
  const [restoring, setRestoring] = React.useState<BackupRun | null>(null);
  const [running, setRunning] = React.useState<string | null>(null);

  const policies = config.data?.policies ?? [];
  const targets = config.data?.targets ?? [];
  const policyOf = (t: BackupTarget) => policies.find((p) => (t.kind === "database" ? p.kind === "database" : p.kind === "volume" && p.volumeId === t.volumeId));
  const dests = destinations.data?.items ?? [];
  const items = runs.data?.items ?? [];

  async function runNow(policy: BackupPolicy) {
    setRunning(policy.id);
    if (await act.runOk(() => api.policies.run(policy.id), "Backup queued")) await Promise.all([runs.refresh(), config.refresh()]);
    setRunning(null);
  }

  if (config.error && !config.data) return <ErrorPanel error={config.error} onRetry={() => void config.refresh()} title="Could not load the backups" />;

  return (
    <div className="flex flex-col gap-8">
      <div className="flex flex-col gap-4">
        <SectionTitle index="01">Policies</SectionTitle>
        <FormError message={act.error} />
        {!config.data && <Skeleton className="h-20 w-full" />}
        {config.data && dests.length === 0 && destinations.data && (
          <p role="note" className="border border-dashed border-warning/70 bg-warning-soft px-3 py-2 text-xs">
            There is no backup destination yet. An administrator adds one under Settings &gt; Backups (Cloudflare R2 is recommended); then backups can be set up here.
          </p>
        )}
        {config.data && (
          <ul className="flex flex-col gap-2">
            {targets.map((t) => (
              <PolicyCard
                key={`${t.kind}:${t.volumeId ?? ""}`}
                target={t}
                policy={policyOf(t)}
                writable={writable && dests.length > 0}
                busy={running === policyOf(t)?.id}
                onEdit={() => setEditing(t)}
                onRun={() => void runNow(policyOf(t)!)}
                onRemove={async () => {
                  const p = policyOf(t)!;
                  if (!window.confirm(`Remove the backup policy of ${targetTitle(t)}? Backups already stored stay in the destination.`)) return;
                  if (await act.runOk(() => api.policies.remove(p.id), "Policy removed")) await Promise.all([config.refresh(), runs.refresh()]);
                }}
              />
            ))}
          </ul>
        )}
      </div>

      <div className="flex flex-col gap-4">
        <SectionTitle index="02">Backups</SectionTitle>
        {runs.error && !runs.data && <ErrorPanel error={runs.error} onRetry={() => void runs.refresh()} title="Could not load the backups" />}
        {!runs.data && !runs.error && <Skeleton className="h-24 w-full" />}
        {runs.data && items.length === 0 && (
          <p className="flex items-center justify-center gap-2 border border-dashed border-border px-4 py-6 text-sm text-muted-foreground">
            <DatabaseBackupIcon className="size-4" aria-hidden="true" /> No backups yet.
          </p>
        )}
        {items.length > 0 && (
          <div className="overflow-x-auto border border-border bg-card">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Time</TableHead>
                  <TableHead>Trigger</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead className="text-right">Size</TableHead>
                  <TableHead className="text-right">Duration</TableHead>
                  <TableHead>Key</TableHead>
                  <TableHead>Error</TableHead>
                  <TableHead>
                    <span className="sr-only">Actions</span>
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {items.map((r) => (
                  <TableRow key={r.id}>
                    <TableCell title={formatDateTime(r.createdAt)} className="whitespace-nowrap font-mono text-xs">
                      {formatAgo(r.createdAt)}
                      {r.prunedAt && <span className="ml-2 text-muted-foreground">(removed by retention)</span>}
                    </TableCell>
                    <TableCell className="text-xs">{TRIGGER_LABEL[r.trigger] ?? r.trigger}</TableCell>
                    <TableCell>
                      <Badge tone={STATUS_TONE[r.status]}>{isActive(r) && <Loader2Icon className="animate-spin" aria-hidden="true" />}{r.status}</Badge>
                    </TableCell>
                    <TableCell className="text-right font-mono text-xs">{r.storedSizeBytes != null ? formatBytes(r.storedSizeBytes) : "-"}</TableCell>
                    <TableCell className="text-right font-mono text-xs">{r.durationMs != null ? formatDuration(r.durationMs) : "-"}</TableCell>
                    <TableCell className="font-mono text-2xs text-muted-foreground">{r.keyId ?? "-"}</TableCell>
                    <TableCell className="max-w-64 truncate text-xs text-danger" title={r.error ?? undefined}>
                      {r.error ?? ""}
                    </TableCell>
                    <TableCell className="text-right">
                      {admin && r.restorable && (
                        <Button size="sm" variant="outline" onClick={() => setRestoring(r)} aria-label={`Restore the backup of ${formatDateTime(r.createdAt)}`}>
                          <RotateCcwIcon aria-hidden="true" /> Restore…
                        </Button>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
        {!admin && items.some((r) => r.restorable) && <p className="text-xs text-muted-foreground">Restoring a backup needs the administrator role.</p>}
      </div>

      {editing && (
        <Dialog open onOpenChange={(o) => !o && !save.pending && setEditing(null)}>
          <DialogContent className="max-w-xl">
            <DialogHeader>
              <DialogTitle>
                {policyOf(editing) ? "Edit" : "Set up"} backups of {targetTitle(editing).toLowerCase()}
              </DialogTitle>
              <DialogDescription>
                {editing.kind === "database" ? "A dump made by the database's own tool inside its container." : "A compressed archive of the named volume."} Encrypted before it
                leaves the server.
              </DialogDescription>
            </DialogHeader>
            <PolicyEditor
              policy={policyOf(editing) ?? null}
              destinations={dests}
              pending={save.pending}
              error={save.error}
              fields={save.fields}
              onCancel={() => setEditing(null)}
              onSubmit={async (input) => {
                const body = { ...input, kind: editing.kind === "volume" ? ("volume" as const) : ("database" as const), ...(editing.volumeId ? { volumeId: editing.volumeId } : {}) };
                if (await save.runOk(() => api.policies.put(kind, id, body), "Policy saved")) {
                  setEditing(null);
                  await config.refresh();
                }
              }}
            />
          </DialogContent>
        </Dialog>
      )}

      {restoring && (
        <RestoreDialog
          run={restoring}
          workloadName={workload}
          canCreateNew={kind === "services" && restoring.kind === "database"}
          api={api}
          onClose={() => setRestoring(null)}
          onStarted={() => void runs.refresh()}
        />
      )}
    </div>
  );
}
