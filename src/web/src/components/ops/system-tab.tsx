"use client";

import * as React from "react";
import { DownloadIcon, Loader2Icon, PlayIcon, RefreshCwIcon } from "lucide-react";
import { FormError, useAction } from "@/components/resources/form";
import { CopyButton, ErrorPanel, Facts, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import type { MaintenanceReport, SystemVersion } from "@/lib/resources/types";
import { formatAgo, formatBytes, formatDateTime } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";
import { DomainHttpsSection } from "./domain-https-section";

const IN_FLIGHT = new Set(["requested", "backing_up", "pulling", "restarting"]);

export const isUpdateInFlight = (state: string | undefined) => !!state && IN_FLIGHT.has(state);

const STATE_LABEL: Record<string, string> = {
  requested: "Waiting for the updater",
  backing_up: "Backing up the database",
  pulling: "Pulling the new images",
  restarting: "Restarting",
  done: "Done",
  failed: "Failed",
  idle: "Idle",
};

/** One line summarising what a maintenance pass did (also used by the tests). */
export function describeReport(r: MaintenanceReport): string {
  const parts: string[] = [];
  const deleted = Number(r.logChunksDeleted) + Number(r.eventsDeleted) + Number(r.webhookDeliveriesDeleted) + Number(r.auditEventsDeleted) + Number(r.sessionsDeleted) + Number(r.idempotencyRecordsDeleted);
  parts.push(`${deleted} old record${deleted === 1 ? "" : "s"} deleted`);
  if (r.backupTaken) parts.push(`backup ${r.backupTaken}`);
  if (Number(r.backupsDeleted) > 0) parts.push(`${r.backupsDeleted} old backup${r.backupsDeleted === 1 ? "" : "s"} removed`);
  if (Number(r.pruneJobsQueued) > 0) parts.push(`${r.pruneJobsQueued} server prune${r.pruneJobsQueued === 1 ? "" : "s"} queued`);
  return parts.join(" · ");
}

function UpdateDialog({ version, onClose, onStart, pending, error }: { version: string; onClose: () => void; onStart: () => void; pending: boolean; error: string | null }) {
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Update to {version}</DialogTitle>
          <DialogDescription>
            The database is backed up first, then the new images are pulled and the control plane restarts. The UI is unavailable for about a minute.
            If the new version does not come up, the previous version and the backup are restored.
          </DialogDescription>
        </DialogHeader>
        <FormError message={error} />
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={onStart} disabled={pending}>
            {pending && <Loader2Icon className="animate-spin" aria-hidden="true" />} Update now
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function VersionSection({ api, pollMs }: { api: ResourcesApi; pollMs: number }) {
  const [watching, setWatching] = React.useState(false);
  const [confirming, setConfirming] = React.useState(false);
  const act = useAction();
  // Faster polling while an update runs (and through the restart, when requests fail): that is what shows the progress.
  const version = usePolled((signal) => api.system.version({ signal }), "system-version", { intervalMs: watching ? Math.min(pollMs, 3000) : pollMs });
  const data: SystemVersion | undefined = version.data;
  const state = data?.updater.state;
  const inFlight = isUpdateInFlight(state);

  async function check() {
    await act.runOk(() => api.system.checkVersion(), "Checked for updates");
    await version.refresh();
  }

  async function start() {
    const ok = await act.runOk(() => api.system.startUpdate());
    if (ok) {
      setConfirming(false);
      setWatching(true);
      await version.refresh();
    }
  }

  if (version.error && !data && !watching) return <ErrorPanel error={version.error} onRetry={() => void version.refresh()} title="Could not load the version" />;
  if (!data) return <p className="text-sm text-muted-foreground">Loading…</p>;

  const latest = data.latest;
  // While an update runs the API goes away for a moment: a failed poll then means "restarting", not "broken".
  const restarting = watching && !!version.error;
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle
        index="01"
        actions={
          <Button size="sm" variant="outline" onClick={() => void check()} disabled={act.pending || !data.checkEnabled} title={data.checkEnabled ? undefined : "Update checks are turned off (Aethera:Updates:Enabled)"}>
            <RefreshCwIcon aria-hidden="true" /> Check now
          </Button>
        }
      >
        Version
      </SectionTitle>
      <FormError message={act.error} />
      <Facts
        rows={[
          ["Running", <span key="r" className="font-mono">{data.current}</span>],
          [
            "Latest",
            latest ? (
              <span key="l" className="flex flex-wrap items-center gap-2">
                <a className="underline underline-offset-2" href={latest.url} target="_blank" rel="noreferrer">
                  {latest.version}
                </a>
                {latest.preRelease && <Badge tone="warning">pre-release</Badge>}
                {data.updateAvailable ? <Badge tone="brand">update available</Badge> : <Badge tone="success">up to date</Badge>}
              </span>
            ) : data.checkEnabled ? (
              "No release found yet"
            ) : (
              "Update checks are off"
            ),
          ],
          ["Checked", data.checkedAt ? `${formatAgo(data.checkedAt)} (${formatDateTime(data.checkedAt)})` : "not yet"],
          ["Updater", data.updater.available ? "running" : "not running"],
        ]}
      />
      {data.checkError && (
        <p role="note" className="border border-dashed border-border px-3 py-2 text-xs text-muted-foreground">
          The last check failed: {data.checkError}
        </p>
      )}

      {data.updateAvailable && latest?.notes && (
        <details className="border border-border bg-card px-4 py-3 text-sm" open>
          <summary className="cursor-pointer font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Release notes {latest.version}</summary>
          <pre className="mt-2 max-h-64 overflow-auto whitespace-pre-wrap font-sans text-xs text-muted-foreground">{latest.notes}</pre>
        </details>
      )}

      {(inFlight || watching || state === "done" || state === "failed") && state !== "idle" && (
        <div role="status" className="flex flex-col gap-1 border border-border bg-card px-4 py-3 text-sm">
          <p className="flex items-center gap-2 font-medium">
            {(inFlight || restarting) && <Loader2Icon className="size-3.5 animate-spin" aria-hidden="true" />}
            {restarting ? "Restarting" : (STATE_LABEL[state ?? "idle"] ?? state)}
            {data.updater.targetVersion && <span className="font-mono text-xs text-muted-foreground">→ {data.updater.targetVersion}</span>}
          </p>
          {restarting ? <p className="text-xs text-muted-foreground">The control plane is restarting; this page reconnects by itself.</p> : data.updater.message && <p className="text-xs text-muted-foreground">{data.updater.message}</p>}
          {state === "done" && (
            <div>
              <Button size="sm" variant="outline" onClick={() => window.location.reload()}>
                Reload the UI
              </Button>
            </div>
          )}
        </div>
      )}

      {data.updateAvailable && !inFlight && (
        <div className="flex flex-col gap-2">
          {data.updater.available ? (
            <div>
              <Button onClick={() => setConfirming(true)}>
                <DownloadIcon aria-hidden="true" /> Update to {latest?.version}
              </Button>
            </div>
          ) : (
            <div role="note" className="flex flex-col gap-2 border border-dashed border-border px-3 py-2 text-xs text-muted-foreground">
              <p>No updater is running, so update on the server itself:</p>
              <span className="flex items-center gap-2 font-mono text-foreground">
                sudo aethera update <CopyButton text="sudo aethera update" label="Copy command" />
              </span>
            </div>
          )}
        </div>
      )}
      {confirming && latest && <UpdateDialog version={latest.version} onClose={() => setConfirming(false)} onStart={() => void start()} pending={act.pending} error={act.error} />}
    </div>
  );
}

function MaintenanceSection({ api, pollMs }: { api: ResourcesApi; pollMs: number }) {
  const info = usePolled((signal) => api.system.maintenance({ signal }), "system-maintenance", { intervalMs: pollMs });
  const [report, setReport] = React.useState<MaintenanceReport | null>(null);
  const act = useAction();

  async function run() {
    const r = await act.run(() => api.system.runMaintenance(), "Maintenance finished");
    if (r) {
      setReport(r);
      await info.refresh();
    }
  }

  if (info.error && !info.data) return <ErrorPanel error={info.error} onRetry={() => void info.refresh()} title="Could not load maintenance" />;
  const m = info.data;
  const last = report ?? m?.lastRun ?? null;
  const days = (n: number | string) => (Number(n) === 0 ? "kept forever" : `${n} days`);
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle
        index="02"
        actions={
          <Button size="sm" variant="outline" onClick={() => void run()} disabled={act.pending || !m}>
            {act.pending ? <Loader2Icon className="animate-spin" aria-hidden="true" /> : <PlayIcon aria-hidden="true" />} Run now
          </Button>
        }
      >
        Maintenance
      </SectionTitle>
      <FormError message={act.error} />
      {m && (
        <>
          <Facts
            rows={[
              ["Build and deploy logs", days(m.settings.logRetentionDays)],
              ["Status events", days(m.settings.eventRetentionDays)],
              ["Audit log", days(m.settings.auditRetentionDays)],
              ["Sessions", days(m.settings.sessionRetentionDays)],
              ["Server prune", m.settings.pruneEnabled ? `every ${Number(m.settings.pruneIntervalHours) / 24} days, images older than ${Math.round(Number(m.settings.pruneKeepHours) / 24)} days` : "off"],
              ["Database backup", m.settings.backupEnabled ? `every ${m.settings.backupIntervalHours} h, newest ${m.settings.backupsKept} kept${m.backupAvailable ? "" : " (pg_dump is missing: not running)"}` : "off"],
            ]}
          />
          <p className="text-xs text-muted-foreground">These come from the server configuration (<span className="font-mono">Aethera:Maintenance:*</span>). Volumes are never pruned automatically and rollback images are always kept.</p>
          {last && (
            <div role="status" className="border border-border bg-card px-4 py-3 text-sm">
              <p className="font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Last run {formatAgo(last.at)}</p>
              <p className="text-xs">{describeReport(last)}</p>
              {last.errors.length > 0 && (
                <ul className="mt-1 list-disc pl-4 text-xs text-danger">
                  {last.errors.map((e) => (
                    <li key={e}>{e}</li>
                  ))}
                </ul>
              )}
            </div>
          )}
          <div>
            <h4 className="mb-2 font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Database backups</h4>
            {m.backups.length === 0 ? (
              <p className="border border-dashed border-border px-4 py-4 text-center text-xs text-muted-foreground">No backups yet.</p>
            ) : (
              <ul className="border border-border bg-card">
                {m.backups.map((b) => (
                  <li key={b.name} className="flex flex-wrap items-center justify-between gap-2 border-b border-border px-4 py-2 last:border-b-0">
                    <span className="font-mono text-xs">{b.name}</span>
                    <span className="flex items-center gap-3 font-mono text-2xs text-muted-foreground">
                      <Badge tone="neutral">{b.kind}</Badge>
                      {formatBytes(b.sizeBytes)} · {formatAgo(b.createdAt)}
                    </span>
                  </li>
                ))}
              </ul>
            )}
            <p className="mt-2 text-xs text-muted-foreground">
              Restore on the server with <span className="font-mono">sudo aethera restore &lt;file&gt;</span>. The files are in the <span className="font-mono">aethera_data</span> volume under <span className="font-mono">/data/backups</span>.
            </p>
          </div>
        </>
      )}
    </div>
  );
}

/** Settings > System (administrators): the running version and one-click updates, retention, backups and scheduled maintenance. */
export function SystemTab({ api = resourcesApi, pollMs = 30_000 }: { api?: ResourcesApi; pollMs?: number }) {
  return (
    <div className="flex flex-col gap-8">
      <VersionSection api={api} pollMs={pollMs} />
      <MaintenanceSection api={api} pollMs={pollMs} />
      <DomainHttpsSection api={api} />
    </div>
  );
}
