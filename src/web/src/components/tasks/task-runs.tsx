"use client";

import * as React from "react";
import { LogViewer } from "@/components/aethera/log-viewer";
import { ErrorPanel } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { StatusPill, type StatusKey } from "@/components/ui/status-pill";
import { formatDuration } from "@/lib/resources/status";
import { formatAgo, formatDateTime } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";
import type { TasksApi } from "@/lib/tasks/api";
import type { ScheduledTask, TaskRunStatus } from "@/lib/tasks/types";
import { useRunLog } from "@/lib/tasks/use-run-log";

const ACTIVE: ReadonlySet<TaskRunStatus> = new Set(["pending", "running"]);

/** A run's status as a pill; skipped is not a failure and not a success, so it is a plain badge. */
export function RunStatus({ status }: { status: TaskRunStatus }) {
  if (status === "skipped") return <Badge tone="neutral">Skipped</Badge>;
  const map: Record<Exclude<TaskRunStatus, "skipped">, StatusKey> = {
    pending: "queued",
    running: "running",
    succeeded: "succeeded",
    failed: "failed",
    cancelled: "cancelled",
  };
  return <StatusPill status={map[status]} label={status === "pending" ? "Pending" : undefined} />;
}

/** The output of one run: what the API kept of it (tail) until the log stream answers, then the stream itself. */
function RunOutput({ api, runId }: { api: TasksApi; runId: string }) {
  const run = usePolled((signal) => api.getRun(runId, { signal }), `run:${runId}`, { intervalMs: 2000 });
  const log = useRunLog(api, runId);
  const data = run.data;
  const hasStream = log.chunks.length > 0;
  return (
    <div className="grid gap-3">
      {data && (
        <dl className="grid grid-cols-2 gap-x-6 gap-y-1 font-mono text-xs sm:grid-cols-4">
          <div><dt className="text-muted-foreground">Status</dt><dd><RunStatus status={data.status} /></dd></div>
          <div><dt className="text-muted-foreground">Exit code</dt><dd>{data.exitCode ?? "-"}</dd></div>
          <div><dt className="text-muted-foreground">Started</dt><dd>{formatDateTime(data.startedAt)}</dd></div>
          <div><dt className="text-muted-foreground">Duration</dt><dd>{formatDuration(data.durationMs)}</dd></div>
        </dl>
      )}
      {data?.error && <p role="status" className="border border-border bg-muted/30 px-3 py-2 text-xs">{data.error}</p>}
      {log.error && !hasStream && <ErrorPanel error={log.error} title="Could not load the output" />}
      <LogViewer
        chunks={hasStream ? log.chunks : data?.outputTail ? [{ id: 1, text: data.outputTail }] : []}
        status={ACTIVE.has(data?.status ?? "pending") ? "live" : hasStream ? "ended" : data?.outputTail ? "output tail" : undefined}
        emptyText={log.loading ? "Loading the output…" : "The command printed nothing."}
        label="Task output"
        maxHeight="20rem"
      />
      {data?.outputTruncated && !hasStream && <p className="text-xs text-muted-foreground">Only the end of the output was kept.</p>}
    </div>
  );
}

/** Run history of a task (newest first) with the output of the selected run. */
export function RunHistoryDialog({ api, task, onClose }: { api: TasksApi; task: ScheduledTask; onClose: () => void }) {
  const runs = usePolled((signal) => api.runs(task.id, { limit: 50 }, { signal }), `runs:${task.id}`, { intervalMs: 5000 });
  const [picked, setPicked] = React.useState<string | null>(null);
  const items = runs.data?.items ?? [];
  const selected = picked ?? items[0]?.id ?? null;
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-w-3xl">
        <DialogHeader>
          <DialogTitle>Runs of {task.name}</DialogTitle>
          <DialogDescription>The newest 100 runs are kept.</DialogDescription>
        </DialogHeader>
        {runs.error && !runs.data && <ErrorPanel error={runs.error} onRetry={() => void runs.refresh()} title="Could not load the runs" />}
        {runs.loading && <Skeleton className="h-20 w-full" />}
        {runs.data && items.length === 0 && <p className="text-sm text-muted-foreground">No runs yet.</p>}
        {items.length > 0 && (
          <div className="grid gap-4 md:grid-cols-[14rem_1fr]">
            <ul className="grid max-h-80 content-start gap-1 overflow-auto" aria-label="Runs">
              {items.map((r) => (
                <li key={r.id}>
                  <button
                    type="button"
                    onClick={() => setPicked(r.id)}
                    aria-current={r.id === selected ? "true" : undefined}
                    className={`focus-ring flex w-full items-center justify-between gap-2 border px-2 py-1.5 text-left ${r.id === selected ? "border-lime bg-primary-soft" : "border-border hover:bg-muted/40"}`}
                  >
                    <span className="min-w-0">
                      <span className="block font-mono text-2xs text-muted-foreground">{formatAgo(r.createdAt)} · {r.trigger}</span>
                      <span className="block font-mono text-xs">{r.exitCode !== null ? `exit ${r.exitCode}` : r.status}</span>
                    </span>
                    <RunStatus status={r.status} />
                  </button>
                </li>
              ))}
            </ul>
            {selected && <RunOutput key={selected} api={api} runId={selected} />}
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}
