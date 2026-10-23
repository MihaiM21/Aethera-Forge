"use client";

/** Backups of one workload: policies, runs, "back up now", restore (R1.1, ADR 0010). */
export function WorkloadBackupsTab({ kind, id }: { kind: "applications" | "services"; id: string }) {
  return <p className="text-sm text-muted-foreground" data-kind={kind} data-id={id}>Backups are coming in this release.</p>;
}
