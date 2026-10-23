"use client";

/** Scheduled tasks of one workload: list, create, run now, history (R1.3, ADR 0010). */
export function ScheduledTasksTab({ kind, id }: { kind: "applications" | "services"; id: string }) {
  return <p className="text-sm text-muted-foreground" data-kind={kind} data-id={id}>Scheduled tasks are coming in this release.</p>;
}
