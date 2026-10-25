"use client";

import * as React from "react";
import { ClockIcon, HistoryIcon, PencilIcon, PlayIcon, PlusIcon, Trash2Icon } from "lucide-react";
import { FormError, useAction } from "@/components/resources/form";
import { ErrorPanel, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { formatDuration } from "@/lib/resources/status";
import { formatAgo } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";
import { tasksApi, type TasksApi } from "@/lib/tasks/api";
import { describeSchedule, formatTimeout } from "@/lib/tasks/schedule";
import type { ScheduledTask } from "@/lib/tasks/types";
import { TaskDialog, formatOccurrence } from "./task-dialog";
import { RunHistoryDialog, RunStatus } from "./task-runs";

function NextRun({ task }: { task: ScheduledTask }) {
  if (!task.enabled) return <span className="text-muted-foreground">disabled</span>;
  if (!task.nextRunAt) return <span className="text-muted-foreground">not scheduled</span>;
  return <span title={formatOccurrence(task.nextRunAt, task.timeZone)}>{formatOccurrence(task.nextRunAt, task.timeZone)}</span>;
}

function LastRun({ task }: { task: ScheduledTask }) {
  const run = task.lastRun;
  if (!run) return <span className="text-muted-foreground">never run</span>;
  return (
    <span className="flex flex-wrap items-center gap-2">
      <RunStatus status={run.status} />
      {run.exitCode !== null && <span>exit {run.exitCode}</span>}
      <span className="text-muted-foreground">{formatDuration(run.durationMs)} · {formatAgo(run.createdAt)}</span>
    </span>
  );
}

/** Scheduled tasks of one workload: list, create and edit, run now, run history with the output (R1.3, ADR 0010). */
export function ScheduledTasksTab({
  kind,
  id,
  api = tasksApi,
  resources = resourcesApi,
}: {
  kind: "applications" | "services";
  id: string;
  api?: TasksApi;
  resources?: ResourcesApi;
}) {
  const list = usePolled((signal) => api.list(kind, id, { signal }), `tasks:${kind}:${id}`, { intervalMs: 10_000 });
  // Only an application can be a compose project; its services are listed when the API can inspect the file (else the form asks for a name).
  const app = usePolled((signal) => resources.applications.get(id, { signal }), `task-app:${id}`, { intervalMs: null, enabled: kind === "applications" });
  const isCompose = kind === "applications" && app.data?.sourceKind === "compose";
  const services = usePolled((signal) => api.composeServices(id, { signal }), `task-compose:${id}`, { intervalMs: null, enabled: isCompose });

  const [editing, setEditing] = React.useState<ScheduledTask | "new" | null>(null);
  const [history, setHistory] = React.useState<ScheduledTask | null>(null);
  const act = useAction();
  const rows = list.data?.items ?? [];
  const canOpenForm = kind === "services" || app.data !== undefined || app.error !== null;

  return (
    <div className="flex flex-col gap-4">
      <SectionTitle
        index="01"
        actions={
          <Button size="sm" onClick={() => setEditing("new")} disabled={!canOpenForm || (isCompose && services.loading)}>
            <PlusIcon aria-hidden="true" /> New task
          </Button>
        }
      >
        Scheduled tasks
      </SectionTitle>
      <FormError message={act.error} />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load the scheduled tasks" />}
      {list.loading && <Skeleton className="h-20 w-full" />}
      {list.data && rows.length === 0 && (
        <p className="flex items-center justify-center gap-2 border border-dashed border-border px-4 py-6 text-sm text-muted-foreground">
          <ClockIcon className="size-4 text-lime" aria-hidden="true" /> No scheduled tasks. Run a command in the container on a schedule, like a cache clear or a queue worker tick.
        </p>
      )}
      <ul className="grid gap-2">
        {rows.map((t) => (
          <li key={t.id} className="grid gap-3 border border-border bg-card px-4 py-3">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="min-w-0">
                <p className="flex flex-wrap items-center gap-2 text-sm font-medium">
                  {t.name}
                  {t.composeService && <Badge tone="outline">{t.composeService}</Badge>}
                </p>
                <p className="mt-1 max-w-full truncate font-mono text-xs text-muted-foreground" title={t.command}>{t.command}</p>
              </div>
              <div className="flex items-center gap-1">
                <label className="mr-2 flex items-center gap-2 font-mono text-2xs text-muted-foreground">
                  <Switch
                    aria-label={`${t.enabled ? "Disable" : "Enable"} ${t.name}`}
                    checked={t.enabled}
                    onCheckedChange={async (enabled) => {
                      if (await act.runOk(() => api.update(t.id, { enabled }), enabled ? "Task enabled" : "Task disabled")) await list.refresh();
                    }}
                  />
                  {t.enabled ? "on" : "off"}
                </label>
                <Button
                  size="sm"
                  variant="outline"
                  onClick={async () => {
                    if (await act.runOk(() => api.run(t.id), "Run started")) {
                      await list.refresh();
                      setHistory(t);
                    }
                  }}
                >
                  <PlayIcon aria-hidden="true" /> Run now
                </Button>
                <Button size="icon-sm" variant="ghost" aria-label={`Run history of ${t.name}`} onClick={() => setHistory(t)}><HistoryIcon aria-hidden="true" /></Button>
                <Button size="icon-sm" variant="ghost" aria-label={`Edit ${t.name}`} onClick={() => setEditing(t)}><PencilIcon aria-hidden="true" /></Button>
                <Button
                  size="icon-sm"
                  variant="ghost"
                  aria-label={`Delete ${t.name}`}
                  onClick={async () => {
                    if (!window.confirm(`Delete the scheduled task ${t.name}? Its run history is kept until it ages out.`)) return;
                    if (await act.runOk(() => api.remove(t.id), "Task deleted")) await list.refresh();
                  }}
                >
                  <Trash2Icon aria-hidden="true" />
                </Button>
              </div>
            </div>
            <dl className="grid gap-x-6 gap-y-1 font-mono text-xs sm:grid-cols-3">
              <div>
                <dt className="text-2xs text-muted-foreground uppercase">Schedule</dt>
                <dd>{describeSchedule(t.schedule)}</dd>
                <dd className="text-muted-foreground">{t.schedule} · {t.timeZone} · timeout {formatTimeout(t.timeoutSeconds)}</dd>
              </div>
              <div>
                <dt className="text-2xs text-muted-foreground uppercase">Next run</dt>
                <dd><NextRun task={t} /></dd>
              </div>
              <div>
                <dt className="text-2xs text-muted-foreground uppercase">Last run</dt>
                <dd><LastRun task={t} /></dd>
              </div>
            </dl>
          </li>
        ))}
      </ul>
      {editing && (
        <TaskDialog
          workload={{ kind, id }}
          task={editing === "new" ? undefined : editing}
          isCompose={isCompose}
          composeServices={services.data ?? null}
          api={api}
          onClose={() => setEditing(null)}
          onSaved={() => void list.refresh()}
        />
      )}
      {history && <RunHistoryDialog api={api} task={history} onClose={() => { setHistory(null); void list.refresh(); }} />}
    </div>
  );
}
