"use client";

import * as React from "react";
import { FormError, SaveButton, fieldError, fieldErrorsOf, useAction } from "@/components/resources/form";
import { NativeSelect } from "@/components/servers/common";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { toDate } from "@/lib/servers/format";
import type { TasksApi } from "@/lib/tasks/api";
import { CUSTOM, PRESETS, browserTimeZone, describeSchedule, presetKeyOf } from "@/lib/tasks/schedule";
import type { ScheduledTask } from "@/lib/tasks/types";

export type TaskWorkload = { kind: "applications" | "services"; id: string };

/** An occurrence as the person who set the schedule reads it: in the task's own time zone, with the zone named. */
export function formatOccurrence(iso: string, timeZone: string): string {
  const d = toDate(iso);
  if (!d) return "-";
  try {
    return d.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short", timeZone, timeZoneName: "short" });
  } catch {
    return d.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
  }
}

function timeZoneOptions(): string[] {
  try {
    const intl = Intl as unknown as { supportedValuesOf?: (key: string) => string[] };
    return intl.supportedValuesOf?.("timeZone") ?? [];
  } catch {
    return [];
  }
}

/** The next three runs of the schedule as the API computes them (it owns the cron semantics), refreshed while the person types. */
function useSchedulePreview(api: TasksApi, cron: string, timeZone: string) {
  const [state, setState] = React.useState<{ key: string; runs: string[]; error: string | null }>({ key: "", runs: [], error: null });
  const key = `${cron}|${timeZone}`;
  React.useEffect(() => {
    if (!cron.trim()) return;
    const controller = new AbortController();
    const timer = setTimeout(async () => {
      try {
        const preview = await api.preview(cron.trim(), timeZone.trim() || "UTC", { signal: controller.signal });
        setState({ key, runs: preview.nextRuns, error: null });
      } catch (e) {
        if (controller.signal.aborted) return;
        const fields = fieldErrorsOf(e);
        setState({ key, runs: [], error: fieldError(fields, "schedule") ?? fieldError(fields, "timeZone") ?? (e instanceof Error ? e.message : "Invalid schedule.") });
      }
    }, 250);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [api, cron, timeZone, key]);
  const current = state.key === key;
  return { runs: current ? state.runs : [], error: current ? state.error : null, pending: !current && cron.trim() !== "" };
}

/** Create (no `task`) or edit a scheduled task. */
export function TaskDialog({
  workload,
  task,
  isCompose,
  composeServices,
  api,
  onClose,
  onSaved,
}: {
  workload: TaskWorkload;
  task?: ScheduledTask;
  isCompose: boolean;
  /** Services of the compose project when known (the picker); `null` = ask for the name. */
  composeServices: string[] | null;
  api: TasksApi;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [name, setName] = React.useState(task?.name ?? "");
  const [command, setCommand] = React.useState(task?.command ?? "");
  const [service, setService] = React.useState(task?.composeService ?? composeServices?.[0] ?? "");
  const [cron, setCron] = React.useState(task?.schedule ?? PRESETS[2].cron);
  const [timeZone, setTimeZone] = React.useState(task?.timeZone ?? browserTimeZone());
  const [timeout, setTimeoutSeconds] = React.useState(String(task?.timeoutSeconds ?? 3600));
  const [enabled, setEnabled] = React.useState(task?.enabled ?? true);
  const act = useAction();
  const preview = useSchedulePreview(api, cron, timeZone);
  const zones = React.useMemo(() => timeZoneOptions(), []);
  const timeoutValue = Number(timeout);
  const timeoutOk = Number.isInteger(timeoutValue) && timeoutValue >= 1 && timeoutValue <= 86400;
  const valid = name.trim() !== "" && command.trim() !== "" && cron.trim() !== "" && timeoutOk && (!isCompose || service.trim() !== "");
  const picker = isCompose && composeServices !== null;

  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent className="max-w-xl">
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            const common = {
              name: name.trim(),
              command,
              schedule: cron.trim(),
              timeZone: timeZone.trim() || "UTC",
              timeoutSeconds: timeoutValue,
              enabled,
              ...(isCompose ? { composeService: service.trim() } : {}),
            };
            const ok = await act.run(
              () => (task ? api.update(task.id, common) : api.create(workload.kind, workload.id, common)),
              task ? "Task saved" : "Task created",
            );
            if (ok) {
              onSaved();
              onClose();
            }
          }}
        >
          <DialogHeader>
            <DialogTitle>{task ? "Edit scheduled task" : "New scheduled task"}</DialogTitle>
            <DialogDescription>
              Runs the command as <span className="font-mono">sh -c</span> inside the running container. A stopped workload skips the run.
            </DialogDescription>
          </DialogHeader>

          <Field label="Name" error={fieldError(act.fields, "name")}>
            {(c) => <Input {...c} value={name} onChange={(e) => setName(e.target.value)} placeholder="Clear the cache" required />}
          </Field>
          <Field label="Command" hint="Up to 4000 characters. Environment variables of the workload (secrets included) are available." error={fieldError(act.fields, "command")}>
            {(c) => <Textarea {...c} className="font-mono text-xs" rows={3} value={command} onChange={(e) => setCommand(e.target.value)} placeholder="php artisan schedule:run" required />}
          </Field>

          {isCompose && (
            <Field label="Service" hint="The compose service whose container runs the command." error={fieldError(act.fields, "composeService")}>
              {(c) =>
                picker ? (
                  <NativeSelect {...c} value={service} onChange={(e) => setService(e.target.value)}>
                    {!composeServices.includes(service) && service && <option value={service}>{service}</option>}
                    {composeServices.map((s) => (
                      <option key={s} value={s}>{s}</option>
                    ))}
                  </NativeSelect>
                ) : (
                  <Input {...c} className="font-mono" value={service} onChange={(e) => setService(e.target.value)} placeholder="web" required />
                )
              }
            </Field>
          )}

          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Schedule">
              {(c) => (
                <NativeSelect
                  {...c}
                  value={presetKeyOf(cron)}
                  onChange={(e) => {
                    const preset = PRESETS.find((p) => p.key === e.target.value);
                    if (preset) setCron(preset.cron);
                  }}
                >
                  {PRESETS.map((p) => (
                    <option key={p.key} value={p.key}>{p.label}</option>
                  ))}
                  <option value={CUSTOM}>Custom cron expression</option>
                </NativeSelect>
              )}
            </Field>
            <Field label="Cron expression" hint="minute hour day month weekday" error={preview.error ?? fieldError(act.fields, "schedule")}>
              {(c) => <Input {...c} className="font-mono" value={cron} onChange={(e) => setCron(e.target.value)} spellCheck={false} required />}
            </Field>
          </div>

          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Time zone" hint="An IANA name; defaults to this browser's." error={fieldError(act.fields, "timeZone")}>
              {(c) => (
                <>
                  <Input {...c} className="font-mono" list={zones.length > 0 ? "task-time-zones" : undefined} value={timeZone} onChange={(e) => setTimeZone(e.target.value)} />
                  {zones.length > 0 && (
                    <datalist id="task-time-zones">
                      {zones.map((z) => <option key={z} value={z} />)}
                    </datalist>
                  )}
                </>
              )}
            </Field>
            <Field label="Timeout (seconds)" hint="1 s to 24 h (86400). The run is stopped when it takes longer." error={fieldError(act.fields, "timeoutSeconds") ?? (timeoutOk ? null : "Enter a whole number from 1 to 86400.")}>
              {(c) => <Input {...c} inputMode="numeric" value={timeout} onChange={(e) => setTimeoutSeconds(e.target.value)} />}
            </Field>
          </div>

          <div className="border border-border bg-muted/30 px-3 py-2" aria-live="polite">
            <p className="font-mono text-2xs tracking-[0.06em] text-muted-foreground uppercase">Next runs · {describeSchedule(cron)}</p>
            {preview.runs.length > 0 ? (
              <ul className="mt-1 grid gap-0.5 font-mono text-xs">
                {preview.runs.map((r) => (
                  <li key={r}>{formatOccurrence(r, timeZone.trim() || "UTC")}</li>
                ))}
              </ul>
            ) : (
              <p className="mt-1 text-xs text-muted-foreground">{preview.error ? "No valid schedule yet." : preview.pending ? "Checking…" : "-"}</p>
            )}
          </div>

          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Enabled
          </label>

          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>Cancel</Button>
            <SaveButton pending={act.pending} disabled={!valid || preview.error !== null}>{task ? "Save task" : "Create task"}</SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
