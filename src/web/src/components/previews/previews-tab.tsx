"use client";

import * as React from "react";
import { AlertTriangleIcon, ExternalLinkIcon, GitPullRequestIcon, Loader2Icon, RefreshCwIcon, Trash2Icon } from "lucide-react";
import { FormError, SaveButton, useAction } from "@/components/resources/form";
import { CopyButton, ErrorPanel, NativeSelect, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { StatusPill } from "@/components/ui/status-pill";
import { Switch } from "@/components/ui/switch";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { DetailLink } from "@/components/shell/detail-link";
import { useAuth } from "@/lib/auth/auth-context";
import { previewsApi, type PreviewsApi } from "@/lib/previews/api";
import { isLive, PREVIEW_PILL } from "@/lib/previews/status";
import type { Preview, PreviewSettings } from "@/lib/previews/types";
import { shortSha } from "@/lib/resources/status";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { formatAgo, toNum } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

const canWrite = (role: string | undefined) => role === "owner" || role === "admin" || role === "developer";
const isAdmin = (role: string | undefined) => role === "owner" || role === "admin";

function SettingsForm({
  applicationId,
  settings,
  api,
  servers,
  writable,
  admin,
  onSaved,
}: {
  applicationId: string;
  settings: PreviewSettings;
  api: PreviewsApi;
  servers: ServersApi;
  writable: boolean;
  admin: boolean;
  onSaved: () => void;
}) {
  const [enabled, setEnabled] = React.useState(settings.enabled);
  const [allowForks, setAllowForks] = React.useState(settings.allowForks);
  const [serverId, setServerId] = React.useState(settings.serverId ?? "");
  const [maxActive, setMaxActive] = React.useState(settings.maxActive == null ? "" : String(toNum(settings.maxActive)));
  const act = useAction();
  const serverList = usePolled((signal) => servers.list({ limit: 100 }, { signal }), "preview-servers", { intervalMs: null });
  const items = serverList.data?.items ?? [];
  const defaultMax = toNum(settings.defaultMaxActive) ?? 10;
  const disabled = !writable || act.pending;

  return (
    <form
      className="flex flex-col gap-4"
      onSubmit={(e) => {
        e.preventDefault();
        void act
          .runOk(
            () =>
              api.putSettings(applicationId, {
                enabled,
                allowForks,
                serverId: serverId || null,
                maxActive: maxActive.trim() ? Number(maxActive) : null,
              }),
            "Preview settings saved",
          )
          .then((ok) => ok && onSaved());
      }}
    >
      {!settings.supported && (
        <p role="note" className="border border-dashed border-warning/70 bg-warning-soft px-3 py-2 text-xs">
          {settings.unsupportedReason}
        </p>
      )}
      <label className="flex items-center gap-3 text-sm">
        <Switch checked={enabled} onCheckedChange={setEnabled} disabled={disabled || !settings.supported} aria-label="Pull request previews" />
        <span>
          Deploy a preview for every pull request
          <span className="block text-xs text-muted-foreground">Opened, updated and reopened pull requests deploy; closed or merged ones are removed.</span>
        </span>
      </label>

      {enabled && !settings.webhookConfigured && (
        <p role="note" className="border border-dashed border-warning/70 bg-warning-soft px-3 py-2 text-xs">
          Previews are driven by the application&apos;s webhook, and it is not set up. Create it in Settings &gt; Webhook and send <span className="font-mono">pull_request</span> (GitHub) or{" "}
          <span className="font-mono">Merge request events</span> (GitLab) to it.
        </p>
      )}
      {settings.webhookPath && (
        <p className="flex flex-wrap items-center gap-2 font-mono text-xs text-muted-foreground">
          Webhook {settings.webhookPath} <CopyButton text={settings.webhookPath} />
        </p>
      )}
      {settings.hostnamePattern ? (
        <p className="font-mono text-xs text-muted-foreground">URL pattern: {settings.hostnamePattern}</p>
      ) : (
        <p role="note" className="border border-dashed border-warning/70 bg-warning-soft px-3 py-2 text-xs">
          The server has no wildcard domain and no known public IP address, so a preview would have no host name. Set a wildcard domain in the server&apos;s settings.
        </p>
      )}

      <div className="border border-border px-3 py-3">
        <label className="flex items-start gap-3 text-sm">
          <Switch checked={allowForks} onCheckedChange={setAllowForks} disabled={disabled || (!admin && !settings.allowForks)} aria-label="Previews of pull requests from forks" />
          <span>
            Also deploy pull requests from forks
            <span className="mt-1 flex items-start gap-1.5 text-xs text-danger">
              <AlertTriangleIcon className="mt-0.5 size-3 shrink-0" aria-hidden="true" />
              <span>
                Dangerous: anyone who can open a pull request gets their code built and run on your server with this application&apos;s environment variables and secrets. Leave it off
                unless the repository is private to people you trust.{!admin && " Only an administrator can turn this on."}
              </span>
            </span>
          </span>
        </label>
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Server" hint="Where previews run. Only previews created afterwards use a changed server.">
          {(c) => (
            <NativeSelect {...c} value={serverId} disabled={disabled} onChange={(e) => setServerId(e.target.value)}>
              <option value="">The application&apos;s server</option>
              {items.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name}
                </option>
              ))}
            </NativeSelect>
          )}
        </Field>
        <Field label="Previews at once" hint={`Further pull requests wait. Empty = ${defaultMax}.`}>
          {(c) => <Input {...c} inputMode="numeric" className="font-mono" placeholder={String(defaultMax)} value={maxActive} disabled={disabled} onChange={(e) => setMaxActive(e.target.value)} />}
        </Field>
      </div>
      <FormError message={act.error} />
      {writable && (
        <div>
          <SaveButton pending={act.pending} />
        </div>
      )}
    </form>
  );
}

function PreviewRow({
  preview,
  writable,
  busy,
  onRedeploy,
  onRemove,
  now,
}: {
  preview: Preview;
  writable: boolean;
  busy: boolean;
  onRedeploy: () => void;
  onRemove: () => void;
  now: Date;
}) {
  const pill = PREVIEW_PILL[preview.status];
  const number = toNum(preview.number);
  const last = preview.lastDeployment;
  return (
    <TableRow>
      <TableCell className="align-top">
        <p className="flex items-center gap-1.5 font-mono text-sm">
          <GitPullRequestIcon className="size-3.5 text-muted-foreground" aria-hidden="true" /> #{number}
        </p>
        {preview.fromFork && <Badge tone="warning">fork</Badge>}
      </TableCell>
      <TableCell className="max-w-64 align-top">
        <p className="truncate text-sm">{preview.title ?? "-"}</p>
        <p className="truncate font-mono text-2xs text-muted-foreground">
          {[preview.sourceBranch, shortSha(preview.headSha) !== "-" ? shortSha(preview.headSha) : null, preview.authorLogin].filter(Boolean).join(" · ")}
        </p>
      </TableCell>
      <TableCell className="align-top">
        <StatusPill status={pill.status} label={pill.label} />
        {preview.statusReason && <p className="mt-1 max-w-56 truncate font-mono text-2xs text-muted-foreground" title={preview.statusReason}>{preview.statusReason}</p>}
        {preview.commentError && (
          <p className="mt-1 max-w-56 truncate font-mono text-2xs text-warning" title={preview.commentError}>
            comment: {preview.commentError}
          </p>
        )}
      </TableCell>
      <TableCell className="max-w-56 align-top">
        {preview.url ? (
          <a href={preview.url} target="_blank" rel="noreferrer" className="focus-ring inline-flex items-center gap-1 truncate font-mono text-xs text-lime hover:underline">
            {preview.hostname} <ExternalLinkIcon className="size-3 shrink-0" aria-hidden="true" />
          </a>
        ) : (
          <span className="text-xs text-muted-foreground">-</span>
        )}
      </TableCell>
      <TableCell className="align-top text-xs">
        {last ? (
          <>
            <DetailLink href={`/deployments/${encodeURIComponent(last.id)}`} className="focus-ring font-mono hover:text-lime">
              #{toNum(last.number)} · logs
            </DetailLink>
            <p className="font-mono text-2xs text-muted-foreground">{formatAgo(last.createdAt, now)}</p>
          </>
        ) : (
          <span className="text-muted-foreground">-</span>
        )}
      </TableCell>
      <TableCell className="text-right align-top">
        {writable && isLive(preview.status) && (
          <>
            <Button size="icon-sm" variant="ghost" aria-label={`Redeploy preview #${number}`} disabled={busy || preview.status === "deploying"} onClick={onRedeploy}>
              {busy ? <Loader2Icon className="animate-spin" aria-hidden="true" /> : <RefreshCwIcon aria-hidden="true" />}
            </Button>
            <Button size="icon-sm" variant="ghost" aria-label={`Remove preview #${number}`} disabled={busy} onClick={onRemove}>
              <Trash2Icon aria-hidden="true" />
            </Button>
          </>
        )}
      </TableCell>
    </TableRow>
  );
}

/** Application > Previews: pull request previews, their settings and status (R2.3, ADR 0012). */
export function PreviewsTab({
  applicationId,
  api = previewsApi,
  servers = serversApi,
  pollMs = 5_000,
}: {
  applicationId: string;
  api?: PreviewsApi;
  servers?: ServersApi;
  pollMs?: number;
}) {
  const { me } = useAuth();
  const writable = canWrite(me?.role);
  const admin = isAdmin(me?.role);
  const settings = usePolled((signal) => api.settings(applicationId, { signal }), `preview-settings:${applicationId}`, { intervalMs: null });
  const list = usePolled((signal) => api.list(applicationId, { signal }), `previews:${applicationId}`, { intervalMs: pollMs });
  const act = useAction();
  const [busy, setBusy] = React.useState<number | null>(null);
  const now = new Date();
  const rows = list.data?.items ?? [];

  async function redeploy(p: Preview) {
    const n = toNum(p.number)!;
    setBusy(n);
    if (await act.runOk(() => api.redeploy(applicationId, n), `Preview #${n} is deploying`)) await list.refresh();
    setBusy(null);
  }

  async function remove(p: Preview) {
    const n = toNum(p.number)!;
    if (!window.confirm(`Remove the preview of pull request #${n}? Its containers, volumes and images are deleted. A new push to the pull request brings it back.`)) return;
    setBusy(n);
    if (await act.runOk(() => api.remove(applicationId, n), `Preview #${n} is being removed`)) await list.refresh();
    setBusy(null);
  }

  return (
    <div className="flex flex-col gap-8">
      <div className="flex flex-col gap-4">
        <SectionTitle index="01">Settings</SectionTitle>
        {settings.error && !settings.data && <ErrorPanel error={settings.error} onRetry={() => void settings.refresh()} title="Could not load the preview settings" />}
        {!settings.data && !settings.error && <Skeleton className="h-40 w-full" />}
        {settings.data && (
          <SettingsForm
            key={`${settings.updatedAt}`}
            applicationId={applicationId}
            settings={settings.data}
            api={api}
            servers={servers}
            writable={writable}
            admin={admin}
            onSaved={() => void Promise.all([settings.refresh(), list.refresh()])}
          />
        )}
      </div>

      <div className="flex flex-col gap-4">
        <SectionTitle index="02">Previews</SectionTitle>
        <FormError message={act.error} />
        {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load the previews" />}
        {!list.data && !list.error && <Skeleton className="h-24 w-full" />}
        {list.data && rows.length === 0 && (
          <p className="flex items-center justify-center gap-2 border border-dashed border-border px-4 py-6 text-sm text-muted-foreground">
            <GitPullRequestIcon className="size-4" aria-hidden="true" /> No previews yet. Open a pull request against the application&apos;s branch.
          </p>
        )}
        {rows.length > 0 && (
          <div className="overflow-x-auto border border-border bg-card">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Pull request</TableHead>
                  <TableHead>Title and commit</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>URL</TableHead>
                  <TableHead>Last deployment</TableHead>
                  <TableHead className="w-24 text-right">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {rows.map((p) => (
                  <PreviewRow
                    key={p.id}
                    preview={p}
                    writable={writable}
                    busy={busy === toNum(p.number)}
                    now={now}
                    onRedeploy={() => void redeploy(p)}
                    onRemove={() => void remove(p)}
                  />
                ))}
              </TableBody>
            </Table>
          </div>
        )}
      </div>
    </div>
  );
}
