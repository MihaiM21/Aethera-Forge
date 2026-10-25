"use client";

import * as React from "react";
import { AlertTriangleIcon, CheckCircle2Icon, Loader2Icon, PencilIcon, PlayIcon, PlugZapIcon, PlusIcon, Trash2Icon } from "lucide-react";
import { DestinationDialog } from "@/components/backups/destination-dialog";
import { PolicyEditor } from "@/components/backups/policy-editor";
import { FormError, useAction } from "@/components/resources/form";
import { CopyButton, ErrorPanel, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { backupsApi, type BackupsApi } from "@/lib/backups/api";
import { describeRetention, describeSchedule, SHOW_MASTER_KEY_COMMAND } from "@/lib/backups/presets";
import type { BackupDestination } from "@/lib/backups/types";
import { formatAgo, formatDateTime } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

/** What losing the master key means, and how to copy it. Always visible: nobody should discover this during a restore. */
export function MasterKeyWarning() {
  return (
    <div role="alert" className="flex flex-col gap-2 border border-warning/70 bg-warning-soft px-4 py-3 text-sm">
      <p className="flex items-center gap-2 font-medium text-warning">
        <AlertTriangleIcon className="size-4 shrink-0" aria-hidden="true" /> Keep a copy of the master key somewhere safe
      </p>
      <p className="text-xs text-foreground">
        Backups are encrypted with a key derived from the master key in <span className="font-mono">/opt/aethera/.env</span> (<span className="font-mono">AETHERA_MASTER_KEY</span>).
        Without it no backup can be restored: not by Aethera, not by anyone else. A backup in R2 is worthless if the server and its master key are lost together, so
        store the key in a password manager or print it.
      </p>
      <div className="flex flex-wrap items-center gap-2">
        <code className="border border-border bg-background px-2 py-1 font-mono text-xs">{SHOW_MASTER_KEY_COMMAND}</code>
        <CopyButton text={SHOW_MASTER_KEY_COMMAND} label="Copy command" />
      </div>
    </div>
  );
}

function TestBadge({ d }: { d: BackupDestination }) {
  if (d.kind === "local") return null;
  if (d.lastTestError) return <Badge tone="danger">test failed</Badge>;
  if (d.lastTestedAt) return <Badge tone="success">tested {formatAgo(d.lastTestedAt)}</Badge>;
  return <Badge tone="neutral">not tested</Badge>;
}

function DestinationsSection({ api }: { api: BackupsApi }) {
  const list = usePolled((signal) => api.destinations.list({ signal }), "backup-destinations", { intervalMs: 60_000 });
  const [editing, setEditing] = React.useState<BackupDestination | "new" | null>(null);
  const [deleting, setDeleting] = React.useState<BackupDestination | null>(null);
  const [testing, setTesting] = React.useState<string | null>(null);
  const act = useAction();
  const rows = list.data?.items ?? [];

  async function test(d: BackupDestination) {
    setTesting(d.id);
    await act.run(() => api.destinations.test(d.id), undefined);
    setTesting(null);
    await list.refresh();
  }

  return (
    <div className="flex flex-col gap-4">
      <SectionTitle
        index="02"
        actions={
          <Button size="sm" onClick={() => setEditing("new")}>
            <PlusIcon aria-hidden="true" /> Add destination
          </Button>
        }
      >
        Destinations
      </SectionTitle>
      <FormError message={act.error} />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load the destinations" />}
      {list.loading && <Skeleton className="h-20 w-full" />}
      {list.data && rows.length === 0 && (
        <p className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">
          No backup destination yet. Add an S3-compatible bucket (Cloudflare R2 is recommended) to back up databases and volumes off the machine.
        </p>
      )}
      {rows.length > 0 && (
        <ul className="border border-border bg-card">
          {rows.map((d) => (
            <li key={d.id} className="flex flex-col gap-2 border-b border-border px-4 py-3 last:border-b-0">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="min-w-0">
                  <p className="flex flex-wrap items-center gap-2 font-medium">
                    {d.name}
                    <Badge tone="outline">{d.kind === "local" ? "local" : "s3"}</Badge>
                    {d.isDefault && <Badge tone="brand">default</Badge>}
                    <TestBadge d={d} />
                  </p>
                  <p className="truncate font-mono text-xs text-muted-foreground">
                    {d.kind === "local" ? "the agent's backup directory on each server" : `${d.endpoint} · ${d.bucket}${d.pathPrefix ? `/${d.pathPrefix}` : ""}`}
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  {d.kind === "s3" && (
                    <Button size="sm" variant="outline" onClick={() => void test(d)} disabled={act.pending || testing !== null} aria-label={`Test connection of ${d.name}`}>
                      {testing === d.id ? <Loader2Icon className="animate-spin" aria-hidden="true" /> : <PlugZapIcon aria-hidden="true" />} Test connection
                    </Button>
                  )}
                  <Button size="icon-sm" variant="ghost" aria-label={`Edit ${d.name}`} onClick={() => setEditing(d)}>
                    <PencilIcon aria-hidden="true" />
                  </Button>
                  <Button size="icon-sm" variant="ghost" aria-label={`Delete ${d.name}`} onClick={() => setDeleting(d)}>
                    <Trash2Icon aria-hidden="true" />
                  </Button>
                </div>
              </div>
              {d.lastTestError && (
                <p role="alert" className="border border-danger bg-danger-soft px-3 py-2 text-xs text-danger">
                  {d.lastTestError}
                </p>
              )}
              {d.lastTestedAt && !d.lastTestError && (
                <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
                  <CheckCircle2Icon className="size-3.5 text-success" aria-hidden="true" /> Wrote, read and deleted a test object {formatDateTime(d.lastTestedAt)}.
                </p>
              )}
            </li>
          ))}
        </ul>
      )}
      {editing && <DestinationDialog destination={editing === "new" ? null : editing} api={api} onClose={() => setEditing(null)} onSaved={() => void list.refresh()} />}
      {deleting && (
        <ConfirmDialog
          open
          onOpenChange={(o) => !o && setDeleting(null)}
          resourceName={deleting.name}
          title={`Delete ${deleting.name}`}
          description="Backups already stored in the bucket stay there, but Aethera can no longer list or restore them. A destination that policies use cannot be deleted."
          onConfirm={async (name) => {
            await api.destinations.remove(deleting.id, name);
            setDeleting(null);
            await list.refresh();
          }}
        />
      )}
    </div>
  );
}

function ControlPlaneSection({ api }: { api: BackupsApi }) {
  const destinations = usePolled((signal) => api.destinations.list({ signal }), "backup-destinations", { intervalMs: 60_000 });
  const policy = usePolled((signal) => api.system.policy({ signal }), "backup-system-policy", { intervalMs: 30_000 });
  const act = useAction();
  const run = useAction();
  const [editing, setEditing] = React.useState(false);
  const s3 = (destinations.data?.items ?? []).filter((d) => d.kind === "s3");
  const current = policy.data?.policy ?? null;

  if (policy.error && !policy.data) return <ErrorPanel error={policy.error} onRetry={() => void policy.refresh()} title="Could not load the policy of Aethera's own database" />;
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle index="03">Aethera&apos;s own database</SectionTitle>
      <p className="text-xs text-muted-foreground">
        The control plane dumps its own database (projects, servers, encrypted secrets), encrypts the dump and uploads it to an S3-compatible destination. This is what
        you restore on a new machine.
      </p>
      <FormError message={run.error ?? act.error} />
      {!policy.data && <Skeleton className="h-16 w-full" />}
      {policy.data && s3.length === 0 && !current && <p className="border border-dashed border-border px-4 py-4 text-sm text-muted-foreground">Add an S3 destination first.</p>}
      {policy.data && current && !editing && (
        <div className="flex flex-col gap-2 border border-border bg-card px-4 py-3 text-sm">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <p className="flex flex-wrap items-center gap-2 font-medium">
              {current.enabled ? <Badge tone="success">enabled</Badge> : <Badge tone="neutral">disabled</Badge>}
              {describeSchedule(current.schedule, current.timeZone)}
              {current.lastStatus && <Badge tone={current.lastStatus === "succeeded" ? "success" : current.lastStatus === "failed" ? "danger" : "info"}>last: {current.lastStatus}</Badge>}
            </p>
            <div className="flex gap-2">
              <Button
                size="sm"
                variant="outline"
                disabled={run.pending}
                onClick={async () => {
                  if (await run.runOk(() => api.policies.run(current.id), "Backup queued")) await policy.refresh();
                }}
              >
                {run.pending ? <Loader2Icon className="animate-spin" aria-hidden="true" /> : <PlayIcon aria-hidden="true" />} Back up now
              </Button>
              <Button size="sm" variant="outline" onClick={() => setEditing(true)}>
                Edit
              </Button>
            </div>
          </div>
          <p className="font-mono text-xs text-muted-foreground">
            to {current.destinationName} · {describeRetention(current)}
            {current.lastSuccessAt ? ` · last success ${formatAgo(current.lastSuccessAt)}` : " · no successful backup yet"}
          </p>
        </div>
      )}
      {policy.data && (editing || (!current && s3.length > 0)) && (
        <PolicyEditor
          policy={current}
          destinations={s3}
          submitLabel={current ? "Save policy" : "Enable backups of the database"}
          pending={act.pending}
          error={act.error}
          fields={act.fields}
          onCancel={current ? () => setEditing(false) : undefined}
          onSubmit={async (input) => {
            if (await act.runOk(() => api.system.putPolicy(input), "Policy saved")) {
              setEditing(false);
              await policy.refresh();
            }
          }}
        />
      )}
      <div role="note" className="flex flex-col gap-1 border border-dashed border-border px-3 py-2 text-xs text-muted-foreground">
        <p className="font-medium text-foreground">Restoring Aethera itself on the server</p>
        <p>Download the object from the bucket, then:</p>
        <p className="font-mono text-foreground">aethera backup-decrypt backup.pgdump.aeb backup.dump &amp;&amp; sudo aethera restore backup.dump</p>
        <p>The master key of the installation must be the one the backup was made with.</p>
      </div>
    </div>
  );
}

/** Settings > Backups: the key warning, destinations (S3/R2, local) and the backup of Aethera's own database (R1.1, ADR 0010). */
export function BackupsSettingsTab({ api = backupsApi }: { api?: BackupsApi }) {
  return (
    <div className="flex flex-col gap-8">
      <div className="flex flex-col gap-4">
        <SectionTitle index="01">Master key</SectionTitle>
        <MasterKeyWarning />
      </div>
      <DestinationsSection api={api} />
      <ControlPlaneSection api={api} />
    </div>
  );
}
