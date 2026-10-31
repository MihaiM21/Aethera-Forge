"use client";

import * as React from "react";
import { CopyIcon, PlusIcon, Trash2Icon } from "lucide-react";
import { BindScopeSelect, PrivateNetworkNote } from "@/components/mesh/bind-scope";
import { FormError, SaveButton, fieldError, numOrUndef, useAction } from "@/components/resources/form";
import { parseScope } from "@/lib/mesh/format";
import type { PortBindScope } from "@/lib/mesh/types";
import { CopyButton, ErrorPanel, SectionTitle } from "@/components/servers/common";
import { NativeSelect } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { toast } from "@/components/ui/sonner";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import type { Application, RuntimeRequest, Webhook, WebhookDelivery } from "@/lib/resources/types";
import { formatAgo, formatBytes } from "@/lib/servers/format";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { usePolled } from "@/lib/servers/use-polled";
import { navigateTo } from "@/lib/navigate";
import { useAuth } from "@/lib/auth/auth-context";
import { isAdminRole } from "@/components/ops/settings-view";
import { isRelativePath, providerOf } from "@/lib/resources/wizard";
import { describeFree, overCommitWarning } from "@/lib/servers/capacity";
import { MovePanel } from "./move-panel";

type Props = { app: Application; api?: ResourcesApi; onSaved: () => void };

const str = (v: unknown) => (v === null || v === undefined ? "" : String(v));
const nz = (v: string) => (v.trim() ? v.trim() : null);

/** PATCH helper: sends only the changed section, keeps merge-patch semantics of the API. */
function usePatch(api: ResourcesApi, app: Application, onSaved: () => void) {
  const act = useAction();
  const save = async (body: Parameters<ResourcesApi["applications"]["update"]>[1], msg = "Saved") => {
    if (await act.runOk(() => api.applications.update(app.id, body), msg)) onSaved();
  };
  return { act, save };
}

// ------------------------------------------------------------------------------------------------ Networking

type PortRow = { id: number; port: string; protocol: "tcp" | "udp"; published: string; http: boolean; scope: PortBindScope };

export function NetworkingTab({ app, api = resourcesApi, onSaved }: Props) {
  const next = React.useRef(1000);
  const [rows, setRows] = React.useState<PortRow[]>(() =>
    app.runtime.ports.map((p, i) => ({ id: i, port: str(p.containerPort), protocol: p.protocol === "udp" ? "udp" : "tcp", published: str(p.publishedPort), http: p.isHttp, scope: parseScope(p.bindScope) })),
  );
  const { act, save } = usePatch(api, app, onSaved);
  const update = (id: number, patch: Partial<PortRow>) => setRows((r) => r.map((x) => (x.id === id ? { ...x, ...patch } : x)));
  return (
    <form
      className="grid max-w-3xl gap-5"
      onSubmit={(e) => {
        e.preventDefault();
        void save({
          runtime: {
            ports: rows.filter((r) => r.port.trim()).map((r) => ({ containerPort: Number(r.port), protocol: r.protocol, publishedPort: numOrUndef(r.published), bindScope: numOrUndef(r.published) === undefined ? undefined : r.scope, isHttp: r.http })),
          },
        });
      }}
    >
      <SectionTitle index="01">Ports</SectionTitle>
      <p className="text-xs text-muted-foreground">
        HTTP ports are routed by the reverse proxy to your domains. A published port opens the port on the server itself; leave it empty to keep the application reachable only through the proxy
        and other containers. Choose <em>Private network</em> to listen on the server&apos;s private address only (reachable from your other servers, not from the internet) or <em>This server
        only</em> for 127.0.0.1.
      </p>
      <div className="grid gap-2">
        {rows.length === 0 && <p className="text-sm text-muted-foreground">No ports configured.</p>}
        {rows.map((r) => (
          <div key={r.id} className="grid grid-cols-[6rem_6rem_7rem_11rem_auto_auto] items-center gap-2">
            <Input aria-label="Container port" inputMode="numeric" className="font-mono" placeholder="3000" value={r.port} onChange={(e) => update(r.id, { port: e.target.value })} />
            <NativeSelect aria-label="Protocol" value={r.protocol} onChange={(e) => update(r.id, { protocol: e.target.value as "tcp" | "udp" })}>
              <option value="tcp">tcp</option>
              <option value="udp">udp</option>
            </NativeSelect>
            <Input aria-label="Published port" inputMode="numeric" className="font-mono" placeholder="publish" value={r.published} onChange={(e) => update(r.id, { published: e.target.value })} />
            <BindScopeSelect value={r.scope} disabled={!r.published.trim()} onChange={(scope) => update(r.id, { scope })} />
            <label className="flex items-center gap-1.5 font-mono text-2xs text-muted-foreground">
              <input type="checkbox" checked={r.http} onChange={(e) => update(r.id, { http: e.target.checked })} /> http
            </label>
            <Button type="button" variant="ghost" size="icon-sm" aria-label="Remove port" onClick={() => setRows((x) => x.filter((y) => y.id !== r.id))}>
              <Trash2Icon aria-hidden="true" />
            </Button>
          </div>
        ))}
      </div>
      <div>
        <Button type="button" variant="outline" onClick={() => setRows((r) => [...r, { id: next.current++, port: "", protocol: "tcp", published: "", http: r.length === 0, scope: "public" }])}>
          <PlusIcon aria-hidden="true" /> Add port
        </Button>
      </div>
      <PrivateNetworkNote serverId={app.serverId} anyPrivate={rows.some((r) => r.scope === "private" && r.published.trim() !== "")} />
      <FormError message={act.error} />
      <div>
        <SaveButton pending={act.pending}>Save ports</SaveButton>
      </div>
    </form>
  );
}

// ------------------------------------------------------------------------------------------------ Resources

const MIB = 1024 * 1024;

/** What is left on the server the application is on, and a warning when the numbers typed would over-commit it (the application's own reservation does not count against it). */
function ServerRoom({ app, servers, cpu, cpuRes, mem, memRes }: { app: Application; servers: ServersApi; cpu: string; cpuRes: string; mem: string; memRes: string }) {
  const capacity = usePolled((signal) => servers.capacity({ signal }), "resources-capacity", { intervalMs: 30_000 });
  const here = capacity.data?.find((c) => c.serverId === app.serverId);
  if (!here) return null;
  const r = app.runtime.resources;
  const own = {
    cpuCores: Number(r.cpuReservationCores ?? r.cpuLimitCores ?? 0),
    memoryBytes: Number(r.memoryReservationBytes ?? r.memoryLimitBytes ?? 0),
  };
  const asked = (res: string, limit: string, unit = 1) => {
    const v = numOrUndef(res) ?? numOrUndef(limit);
    return v === undefined ? null : v * unit;
  };
  const warning = overCommitWarning(here, { cpuCores: asked(cpuRes, cpu), memoryBytes: asked(memRes, mem, MIB) }, own);
  return (
    <div className="grid gap-1 border border-border bg-card px-3 py-2 text-xs" aria-label="Room on the server">
      <p>
        <span className="mono-label text-muted-foreground">{here.name}</span> · {describeFree(here)} (this application&apos;s own reservation counts as free)
      </p>
      {warning && (
        <p role="status" className="text-warning">
          {warning}
        </p>
      )}
    </div>
  );
}

export function ResourcesTab({ app, api = resourcesApi, servers = serversApi, onSaved }: Props & { servers?: ServersApi }) {
  const r = app.runtime.resources;
  const [priority, setPriority] = React.useState<string>(app.runtime.priority ?? "normal");
  const [cpu, setCpu] = React.useState(str(r.cpuLimitCores));
  const [cpuRes, setCpuRes] = React.useState(str(r.cpuReservationCores));
  const [mem, setMem] = React.useState(r.memoryLimitBytes != null ? String(Math.round(Number(r.memoryLimitBytes) / MIB)) : "");
  const [memRes, setMemRes] = React.useState(r.memoryReservationBytes != null ? String(Math.round(Number(r.memoryReservationBytes) / MIB)) : "");
  const [pids, setPids] = React.useState(str(r.pidsLimit));
  const [strategy, setStrategy] = React.useState(app.runtime.strategy);
  const [restart, setRestart] = React.useState(app.runtime.restartPolicy);
  const { act, save } = usePatch(api, app, onSaved);
  const mb = (v: string) => (v.trim() ? Math.round(Number(v) * MIB) : null);
  return (
    <form
      className="grid max-w-3xl gap-5"
      onSubmit={(e) => {
        e.preventDefault();
        void save({
          runtime: {
            strategy,
            restartPolicy: restart,
            priority,
            resources: { cpuLimitCores: numOrUndef(cpu) ?? null, cpuReservationCores: numOrUndef(cpuRes) ?? null, memoryLimitBytes: mb(mem), memoryReservationBytes: mb(memRes), pidsLimit: numOrUndef(pids) ?? null },
          } as RuntimeRequest,
        });
      }}
    >
      <SectionTitle index="01">Limits</SectionTitle>
      <p className="text-xs text-muted-foreground">Empty means unlimited. Limits apply when the container is created, on the next deployment.</p>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="CPU limit (cores)" error={fieldError(act.fields, "cpuLimitCores")}>{(c) => <Input {...c} inputMode="decimal" className="font-mono" placeholder="1.5" value={cpu} onChange={(e) => setCpu(e.target.value)} />}</Field>
        <Field label="CPU reservation (cores)">{(c) => <Input {...c} inputMode="decimal" className="font-mono" value={cpuRes} onChange={(e) => setCpuRes(e.target.value)} />}</Field>
        <Field label="Memory limit (MiB)" error={fieldError(act.fields, "memoryLimitBytes")} hint={r.memoryLimitBytes != null ? `currently ${formatBytes(r.memoryLimitBytes)}` : undefined}>
          {(c) => <Input {...c} inputMode="numeric" className="font-mono" placeholder="512" value={mem} onChange={(e) => setMem(e.target.value)} />}
        </Field>
        <Field label="Memory reservation (MiB)">{(c) => <Input {...c} inputMode="numeric" className="font-mono" value={memRes} onChange={(e) => setMemRes(e.target.value)} />}</Field>
        <Field label="Process limit">{(c) => <Input {...c} inputMode="numeric" className="font-mono" value={pids} onChange={(e) => setPids(e.target.value)} />}</Field>
      </div>
      <p className="text-xs text-muted-foreground">
        A reservation is what the application is promised: Aethera counts it when it places other things on the server. Without one, the limit counts.
      </p>
      <ServerRoom app={app} servers={servers} cpu={cpu} cpuRes={cpuRes} mem={mem} memRes={memRes} />
      <SectionTitle index="02">Priority</SectionTitle>
      <Field
        label="When the server is short of CPU or memory"
        hint="High doubles the CPU shares (from the reservation, or one core's worth without one) and protects the application from the OOM killer; low halves them and makes it the first to be stopped. Applies on the next deployment."
        error={fieldError(act.fields, "priority")}
      >
        {(c) => (
          <NativeSelect {...c} value={priority} onChange={(e) => setPriority(e.target.value)}>
            <option value="high">High: keep it running first</option>
            <option value="normal">Normal</option>
            <option value="low">Low: give way to others</option>
          </NativeSelect>
        )}
      </Field>
      <SectionTitle index="03">Deployment behaviour</SectionTitle>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Strategy" hint="Low downtime starts the new container next to the old one and switches after it is healthy.">
          {(c) => (
            <NativeSelect {...c} value={strategy} onChange={(e) => setStrategy(e.target.value)}>
              <option value="recreate">Recreate (brief downtime)</option>
              <option value="low-downtime">Low downtime</option>
            </NativeSelect>
          )}
        </Field>
        <Field label="Restart policy">
          {(c) => (
            <NativeSelect {...c} value={restart} onChange={(e) => setRestart(e.target.value as typeof restart)}>
              <option value="no">No</option>
              <option value="always">Always</option>
              <option value="onFailure">On failure</option>
              <option value="unlessStopped">Unless stopped</option>
            </NativeSelect>
          )}
        </Field>
      </div>
      <FormError message={act.error} />
      <div>
        <SaveButton pending={act.pending} />
      </div>
    </form>
  );
}

// ------------------------------------------------------------------------------------------------ Health

export function HealthTab({ app, api = resourcesApi, onSaved }: Props) {
  const h = app.runtime.healthCheck;
  const [type, setType] = React.useState(h.type);
  const [path, setPath] = React.useState(str(h.path));
  const [port, setPort] = React.useState(str(h.port));
  const [interval, setInterval_] = React.useState(str(h.intervalSeconds));
  const [timeout, setTimeout_] = React.useState(str(h.timeoutSeconds));
  const [retries, setRetries] = React.useState(str(h.retries));
  const [start, setStart] = React.useState(str(h.startPeriodSeconds));
  const { act, save } = usePatch(api, app, onSaved);
  return (
    <form
      className="grid max-w-3xl gap-5"
      onSubmit={(e) => {
        e.preventDefault();
        void save({
          runtime: {
            healthCheck: {
              type,
              path: type === "http" ? nz(path) : null,
              port: numOrUndef(port),
              intervalSeconds: numOrUndef(interval),
              timeoutSeconds: numOrUndef(timeout),
              retries: numOrUndef(retries),
              startPeriodSeconds: numOrUndef(start),
            },
          } as RuntimeRequest,
        });
      }}
    >
      <SectionTitle index="01">Health check</SectionTitle>
      <p className="text-xs text-muted-foreground">
        A deployment only becomes the live one after the check passes. The probe runs from the server, so private addresses are fine.
      </p>
      <div className="grid gap-4 sm:grid-cols-3">
        <Field label="Type">
          {(c) => (
            <NativeSelect {...c} value={type} onChange={(e) => setType(e.target.value as typeof type)}>
              <option value="none">None</option>
              <option value="http">HTTP</option>
              <option value="tcp">TCP</option>
              <option value="container">Container (Docker health)</option>
            </NativeSelect>
          )}
        </Field>
        {type === "http" && <Field label="Path" error={fieldError(act.fields, "path")}>{(c) => <Input {...c} className="font-mono" placeholder="/health" value={path} onChange={(e) => setPath(e.target.value)} />}</Field>}
        {(type === "http" || type === "tcp") && <Field label="Port">{(c) => <Input {...c} inputMode="numeric" className="font-mono" value={port} onChange={(e) => setPort(e.target.value)} />}</Field>}
      </div>
      {type !== "none" && (
        <div className="grid gap-4 sm:grid-cols-4">
          <Field label="Interval (s)">{(c) => <Input {...c} inputMode="numeric" className="font-mono" value={interval} onChange={(e) => setInterval_(e.target.value)} />}</Field>
          <Field label="Timeout (s)">{(c) => <Input {...c} inputMode="numeric" className="font-mono" value={timeout} onChange={(e) => setTimeout_(e.target.value)} />}</Field>
          <Field label="Retries">{(c) => <Input {...c} inputMode="numeric" className="font-mono" value={retries} onChange={(e) => setRetries(e.target.value)} />}</Field>
          <Field label="Start period (s)">{(c) => <Input {...c} inputMode="numeric" className="font-mono" value={start} onChange={(e) => setStart(e.target.value)} />}</Field>
        </div>
      )}
      <FormError message={act.error} />
      <div>
        <SaveButton pending={act.pending} />
      </div>
    </form>
  );
}

// ------------------------------------------------------------------------------------------------ Build and source

function WebhookPanel({ app, api }: { app: Application; api: ResourcesApi }) {
  const hook = usePolled((signal) => api.applications.webhook(app.id, { signal }).catch((e) => (e?.status === 404 ? null : Promise.reject(e))), `hook:${app.id}`, { intervalMs: null });
  const deliveries = usePolled((signal) => api.applications.webhookDeliveries(app.id, { signal }), `hook-d:${app.id}`, { intervalMs: 20_000, enabled: Boolean(hook.data) });
  const [secret, setSecret] = React.useState<Webhook | null>(null);
  const [filter, setFilter] = React.useState("");
  const act = useAction();
  const gitProvider = app.gitSource?.provider ?? "generic";
  const w = secret ?? hook.data;
  const url = w ? `${window.location.origin}${w.path.startsWith("/") ? w.path : `/${w.path}`}` : "";
  return (
    <div className="grid gap-3">
      <SectionTitle index="03">Webhook</SectionTitle>
      {hook.error && !hook.loading && <ErrorPanel error={hook.error} title="Could not load the webhook" />}
      {!w && !hook.loading && (
        <div className="flex flex-wrap items-center justify-between gap-3 border border-dashed border-border px-4 py-3 text-sm">
          <p className="text-muted-foreground">Deploy automatically when the repository receives a push.</p>
          <Button
            variant="outline"
            onClick={async () => {
              const created = await act.run(() => api.applications.setWebhook(app.id, { provider: gitProvider, branchFilter: filter.trim() || undefined }), "Webhook created");
              if (created) {
                setSecret(created);
                await hook.refresh();
              }
            }}
            disabled={act.pending}
          >
            Set up webhook
          </Button>
        </div>
      )}
      <FormError message={act.error} />
      {w && (
        <div className="grid gap-3 border border-border bg-card p-4">
          <div className="flex flex-wrap items-center gap-2">
            <Badge tone={w.enabled ? "success" : "neutral"}>{w.enabled ? "enabled" : "disabled"}</Badge>
            <Badge tone="outline">{w.provider}</Badge>
            {w.branchFilter && <Badge tone="outline">branches: {w.branchFilter}</Badge>}
            {w.lastDeliveryAt != null && <span className="font-mono text-2xs text-muted-foreground">last delivery {formatAgo(w.lastDeliveryAt)}</span>}
          </div>
          <div className="flex items-center gap-2">
            <code className="min-w-0 flex-1 truncate border border-border bg-muted px-2 py-1.5 font-mono text-xs">{url}</code>
            <CopyButton text={url} label="Copy URL" />
          </div>
          {secret?.secret && (
            <div role="status" className="border border-warning/70 bg-warning-soft px-3 py-2 text-xs">
              <p className="font-medium text-warning">Copy the secret now: it is not shown again.</p>
              <div className="mt-1 flex items-center gap-2">
                <code className="min-w-0 flex-1 truncate font-mono">{secret.secret}</code>
                <CopyButton text={secret.secret} label="Copy secret" />
              </div>
              <p className="mt-1 text-muted-foreground">
                {w.provider === "gitHub" ? "GitHub: content type application/json, this secret." : w.provider === "gitLab" ? "GitLab: use it as the secret token." : "Send it in the X-Aethera-Token header."}
              </p>
            </div>
          )}
          <div className="flex flex-wrap gap-2">
            <Button
              size="sm"
              variant="outline"
              onClick={async () => {
                const rotated = await act.run(() => api.applications.setWebhook(app.id, { provider: w.provider, branchFilter: w.branchFilter ?? undefined }), "Secret rotated");
                if (rotated) setSecret(rotated);
              }}
            >
              <CopyIcon aria-hidden="true" /> Rotate secret
            </Button>
            <Button
              size="sm"
              variant="outline"
              onClick={async () => {
                if (!window.confirm("Remove the webhook? Pushes will no longer deploy this application.")) return;
                if (await act.runOk(() => api.applications.removeWebhook(app.id), "Webhook removed")) {
                  setSecret(null);
                  await hook.refresh();
                }
              }}
            >
              Remove
            </Button>
          </div>
          {(deliveries.data?.items.length ?? 0) > 0 && (
            <ul className="grid gap-1 border-t border-border pt-3" aria-label="Recent deliveries">
              {deliveries.data!.items.slice(0, 8).map((d: WebhookDelivery) => (
                <li key={d.deliveryId} className="flex flex-wrap items-center gap-2 font-mono text-2xs">
                  <Badge tone={d.outcome === "accepted" ? "success" : d.outcome === "rejected" ? "danger" : "neutral"}>{d.outcome}</Badge>
                  <span className="text-muted-foreground">{formatAgo(d.receivedAt)}</span>
                  <span>{d.ref ?? ""}</span>
                  <span className="text-muted-foreground">{d.detail ?? ""}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
      {!w && !hook.loading && (
        <Field label="Branch filter (optional)" hint="Comma-separated globs, e.g. main, release/*. Empty = the configured branch.">
          {(c) => <Input {...c} className="font-mono" value={filter} onChange={(e) => setFilter(e.target.value)} />}
        </Field>
      )}
    </div>
  );
}

/** Compose applications: the file comes from the repository (built on the server) or is pasted in (ADR 0010). */
function ComposeSourceForm(
  p: Props & {
    branch: string;
    setBranch: (v: string) => void;
    pin: string;
    setPin: (v: string) => void;
    autoDeploy: boolean;
    setAutoDeploy: (v: boolean) => void;
    compose: string;
    setCompose: (v: string) => void;
  },
) {
  const { app, api = resourcesApi } = p;
  const [mode, setMode] = React.useState<"repo" | "inline">(app.compose?.filePath ? "repo" : app.compose?.inlineContent ? "inline" : "repo");
  const [path, setPath] = React.useState(app.compose?.filePath ?? "docker-compose.yml");
  const [repoUrl, setRepoUrl] = React.useState(app.gitSource?.repositoryUrl ?? "");
  const [credentialId, setCredentialId] = React.useState(app.gitSource?.gitCredentialId ?? "");
  const creds = usePolled((signal) => api.gitCredentials.list({ signal }).catch(() => ({ items: [], nextCursor: null })), "compose-creds", { intervalMs: null });
  const { act, save } = usePatch(api, app, p.onSaved);
  const pathInvalid = mode === "repo" && !isRelativePath(path.trim());
  return (
    <form
      className="grid max-w-3xl gap-5"
      onSubmit={(e) => {
        e.preventDefault();
        if (mode === "inline") {
          void save({ compose: { filePath: null, inlineContent: p.compose } });
          return;
        }
        void save({
          gitSource: {
            repositoryUrl: repoUrl.trim(),
            provider: providerOf(repoUrl),
            branch: p.branch.trim() || "main",
            commitPin: nz(p.pin),
            autoDeploy: p.autoDeploy,
            gitCredentialId: credentialId || null,
          },
          compose: { filePath: path.trim(), inlineContent: null },
        });
      }}
    >
      <SectionTitle index="01">Compose file</SectionTitle>
      <fieldset className="flex flex-wrap gap-2">
        <legend className="sr-only">Where the compose file comes from</legend>
        <label className={`flex cursor-pointer items-center gap-2 border border-border px-3 py-1.5 text-sm ${mode === "repo" ? "border-primary bg-primary-soft" : ""}`}>
          <input type="radio" name="compose-source" checked={mode === "repo"} onChange={() => setMode("repo")} /> Compose file from the repository
        </label>
        <label className={`flex cursor-pointer items-center gap-2 border border-border px-3 py-1.5 text-sm ${mode === "inline" ? "border-primary bg-primary-soft" : ""}`}>
          <input type="radio" name="compose-source" checked={mode === "inline"} onChange={() => setMode("inline")} /> Paste a compose file
        </label>
      </fieldset>
      {mode === "repo" ? (
        <>
          <Field label="Repository URL" error={fieldError(act.fields, "repositoryUrl")}>
            {(c) => <Input {...c} value={repoUrl} placeholder="https://github.com/owner/repo.git" onChange={(e) => setRepoUrl(e.target.value)} />}
          </Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Branch" error={fieldError(act.fields, "branch")}>{(c) => <Input {...c} value={p.branch} onChange={(e) => p.setBranch(e.target.value)} />}</Field>
            <Field label="Pin to a commit" hint="Empty = follow the branch." error={fieldError(act.fields, "commitPin")}>
              {(c) => <Input {...c} className="font-mono" value={p.pin} onChange={(e) => p.setPin(e.target.value)} />}
            </Field>
          </div>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Compose file path" hint="Relative to the repository root." error={pathInvalid ? "Use a path inside the repository." : fieldError(act.fields, "filePath")}>
              {(c) => <Input {...c} className="font-mono" value={path} onChange={(e) => setPath(e.target.value)} />}
            </Field>
            <Field label="Git credential" hint="Only for private repositories.">
              {(c) => (
                <NativeSelect {...c} value={credentialId} onChange={(e) => setCredentialId(e.target.value)}>
                  <option value="">None (public)</option>
                  {(creds.data?.items ?? []).map((g) => (
                    <option key={g.id} value={g.id}>{g.name}</option>
                  ))}
                </NativeSelect>
              )}
            </Field>
          </div>
          <label className="flex items-center gap-3 text-sm">
            <Switch checked={p.autoDeploy} onCheckedChange={p.setAutoDeploy} aria-label="Deploy on push" /> Deploy automatically when the branch is pushed
          </label>
          <p className="text-xs text-muted-foreground">Services with build: are built on the server from the checkout on every deployment; build arguments are the application&apos;s build-time variables.</p>
        </>
      ) : (
        <Field label="compose.yaml" error={fieldError(act.fields, "inlineContent")}>
          {(c) => <Textarea {...c} rows={16} className="font-mono text-xs" value={p.compose} onChange={(e) => p.setCompose(e.target.value)} />}
        </Field>
      )}
      <FormError message={act.error} />
      <div><SaveButton pending={act.pending} disabled={mode === "repo" && (!repoUrl.trim() || pathInvalid)} /></div>
    </form>
  );
}

export function BuildTab({ app, api = resourcesApi, servers = serversApi, onSaved }: Props & { servers?: ServersApi }) {
  const b = app.build;
  const g = app.gitSource;
  const [branch, setBranch] = React.useState(g?.branch ?? "");
  const [pin, setPin] = React.useState(str(g?.commitPin));
  const [autoDeploy, setAutoDeploy] = React.useState(g?.autoDeploy ?? false);
  const [context, setContext] = React.useState(b?.context ?? ".");
  const [dockerfile, setDockerfile] = React.useState(str(b?.dockerfilePath));
  const [install, setInstall] = React.useState(str(b?.installCommand));
  const [build, setBuild] = React.useState(str(b?.buildCommand));
  const [startCmd, setStartCmd] = React.useState(str(b?.startCommand));
  const [output, setOutput] = React.useState(str(b?.outputDirectory));
  const [cache, setCache] = React.useState(b?.cacheEnabled ?? true);
  const [image, setImage] = React.useState(app.image?.image ?? "");
  const [tag, setTag] = React.useState(app.image?.tag ?? "");
  const [pullPolicy, setPullPolicy] = React.useState<string>(app.image?.pullPolicy ?? "always");
  const [compose, setCompose] = React.useState(app.compose?.inlineContent ?? "");
  const { act, save } = usePatch(api, app, onSaved);
  const engine = b?.engine ?? "";

  if (app.sourceKind === "dockerImage") {
    return (
      <form className="grid max-w-3xl gap-5" onSubmit={(e) => { e.preventDefault(); void save({ image: { image: image.trim(), tag: tag.trim(), pullPolicy } }); }}>
        <SectionTitle index="01">Image</SectionTitle>
        <div className="grid gap-4 sm:grid-cols-3">
          <Field label="Image" className="sm:col-span-2" error={fieldError(act.fields, "image")}>{(c) => <Input {...c} className="font-mono" value={image} onChange={(e) => setImage(e.target.value)} />}</Field>
          <Field label="Tag" error={fieldError(act.fields, "tag")}>{(c) => <Input {...c} className="font-mono" value={tag} onChange={(e) => setTag(e.target.value)} />}</Field>
          <Field label="Pull" hint="Always fetches a new image for tags like latest on every deployment." error={fieldError(act.fields, "pullPolicy")}>
            {(c) => (
              <NativeSelect {...c} value={pullPolicy} onChange={(e) => setPullPolicy(e.target.value)}>
                <option value="always">On every deployment</option>
                <option value="ifNotPresent">Only when missing on the server</option>
                <option value="never">Never (the image must be on the server)</option>
              </NativeSelect>
            )}
          </Field>
        </div>
        <FormError message={act.error} />
        <div><SaveButton pending={act.pending} /></div>
      </form>
    );
  }
  if (app.sourceKind === "compose") {
    return (
      <ComposeSourceForm
        app={app}
        api={api}
        onSaved={onSaved}
        branch={branch}
        setBranch={setBranch}
        pin={pin}
        setPin={setPin}
        autoDeploy={autoDeploy}
        setAutoDeploy={setAutoDeploy}
        compose={compose}
        setCompose={setCompose}
      />
    );
  }
  return (
    <div className="grid max-w-3xl gap-8">
      <form
        className="grid gap-5"
        onSubmit={(e) => {
          e.preventDefault();
          void save({
            gitSource: { branch: branch.trim(), commitPin: nz(pin), autoDeploy },
            build: {
              context: context.trim() || ".",
              dockerfilePath: engine === "dockerfile" ? nz(dockerfile) : undefined,
              installCommand: nz(install),
              buildCommand: nz(build),
              startCommand: nz(startCmd),
              outputDirectory: nz(output),
              cacheEnabled: cache,
            },
          });
        }}
      >
        <SectionTitle index="01">Source</SectionTitle>
        <p className="font-mono text-xs text-muted-foreground">{g?.repositoryUrl}</p>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Branch" error={fieldError(act.fields, "branch")}>{(c) => <Input {...c} value={branch} onChange={(e) => setBranch(e.target.value)} />}</Field>
          <Field label="Pinned commit" hint="Empty = the tip of the branch.">{(c) => <Input {...c} className="font-mono" value={pin} onChange={(e) => setPin(e.target.value)} />}</Field>
        </div>
        <label className="flex items-center gap-3 text-sm">
          <Switch checked={autoDeploy} onCheckedChange={setAutoDeploy} aria-label="Deploy on push" /> Deploy when the branch is pushed
        </label>
        <SectionTitle index="02">Build · {engine}</SectionTitle>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Build context" error={fieldError(act.fields, "context")}>{(c) => <Input {...c} className="font-mono" value={context} onChange={(e) => setContext(e.target.value)} />}</Field>
          {engine === "dockerfile" ? (
            <Field label="Dockerfile path" error={fieldError(act.fields, "dockerfilePath")}>{(c) => <Input {...c} className="font-mono" value={dockerfile} onChange={(e) => setDockerfile(e.target.value)} />}</Field>
          ) : (
            <>
              <Field label="Install command">{(c) => <Input {...c} className="font-mono" value={install} onChange={(e) => setInstall(e.target.value)} />}</Field>
              <Field label="Build command">{(c) => <Input {...c} className="font-mono" value={build} onChange={(e) => setBuild(e.target.value)} />}</Field>
            </>
          )}
          {engine === "nixpacks" && <Field label="Start command">{(c) => <Input {...c} className="font-mono" value={startCmd} onChange={(e) => setStartCmd(e.target.value)} />}</Field>}
          {engine === "static" && <Field label="Output directory" error={fieldError(act.fields, "outputDirectory")}>{(c) => <Input {...c} className="font-mono" value={output} onChange={(e) => setOutput(e.target.value)} />}</Field>}
        </div>
        <label className="flex items-center gap-3 text-sm">
          <Switch checked={cache} onCheckedChange={setCache} aria-label="Build cache" /> Use the build cache
        </label>
        <FormError message={act.error} />
        <div><SaveButton pending={act.pending} /></div>
      </form>
      <BuildServerForm app={app} api={api} servers={servers} onSaved={onSaved} />
      <WebhookPanel app={app} api={api} />
    </div>
  );
}

/**
 * The server that builds the image (ADR 0012). "The server it runs on" builds locally; another server builds, pushes the image to its build registry
 * and the run server pulls it. The API answers with warnings (no build role, no registry yet) and they are shown as they come.
 */
function BuildServerForm({ app, api, servers, onSaved }: { app: Application; api: ResourcesApi; servers: ServersApi; onSaved: () => void }) {
  const list = usePolled((signal) => servers.list({ limit: 100, sort: "name" }, { signal }), "build-servers", { intervalMs: null });
  const [buildServerId, setBuildServerId] = React.useState(app.buildServerId ?? "");
  const [warnings, setWarnings] = React.useState<string[]>([]);
  const act = useAction();
  const items = list.data?.items ?? [];
  const chosen = items.find((s) => s.id === buildServerId);
  const elsewhere = chosen !== undefined && chosen.id !== app.serverId;
  return (
    <form
      className="grid gap-3"
      onSubmit={async (e) => {
        e.preventDefault();
        const updated = await act.run(() => api.applications.update(app.id, { buildServerId: buildServerId || null }), "Build server saved");
        if (updated) {
          setWarnings(updated.warnings ?? []);
          onSaved();
        }
      }}
    >
      <SectionTitle index="03">Build server</SectionTitle>
      <p className="text-xs text-muted-foreground">
        By default the server the application runs on builds it. A build server keeps builds off a small or busy production server: it builds and pushes the image
        to its registry, and the run server pulls it. Rollbacks pull the same image, so they work on any server.
      </p>
      <div className="max-w-md">
        <Field label="Builds on" error={fieldError(act.fields, "buildServerId")}>
          {(c) => (
            <NativeSelect {...c} value={buildServerId} onChange={(e) => setBuildServerId(e.target.value)}>
              <option value="">The server it runs on</option>
              {items.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name}
                  {s.roles.includes("build") ? " · build" : ""}
                  {s.id === app.serverId ? " (the run server)" : ""}
                </option>
              ))}
            </NativeSelect>
          )}
        </Field>
      </div>
      {elsewhere && !chosen.buildRegistryId && (
        <p role="status" className="text-xs text-warning">
          {chosen.name} has no build registry yet, so deployments will fail with build.registry_missing. Choose one in the server&apos;s settings (Servers · {chosen.name} · Settings · Builds).
        </p>
      )}
      {warnings.map((w) => (
        <p key={w} role="status" className="text-xs text-warning">
          {w}
        </p>
      ))}
      <FormError message={act.error} />
      <div>
        <SaveButton pending={act.pending} disabled={buildServerId === (app.buildServerId ?? "")}>
          Save build server
        </SaveButton>
      </div>
    </form>
  );
}

// ------------------------------------------------------------------------------------------------ Settings

type LabelRow = { id: number; key: string; value: string };

/** Extra container labels (Traefik middlewares and the like), merged over the generated ones. Administrators only; the API refuses `aethera.*` and `traefik.docker.network`. */
export function CustomLabelsEditor({ app, api = resourcesApi, onSaved }: Props) {
  const initial = Object.entries(app.customLabels ?? {}) as Array<[string, string]>;
  const nextId = React.useRef(initial.length + 1);
  const [rows, setRows] = React.useState<LabelRow[]>(initial.map(([key, value], i) => ({ id: i + 1, key, value })));
  const { act, save } = usePatch(api, app, onSaved);
  const filled = rows.filter((r) => r.key.trim());
  const protectedKey = (k: string) => /^(aethera\.|traefik\.docker\.network)/i.test(k.trim());
  const duplicate = new Set(filled.map((r) => r.key.trim())).size !== filled.length;
  const invalid = filled.some((r) => protectedKey(r.key)) || duplicate;
  const update = (id: number, patch: Partial<LabelRow>) => setRows((p) => p.map((r) => (r.id === id ? { ...r, ...patch } : r)));
  return (
    <form
      className="grid gap-4"
      aria-label="Custom labels"
      onSubmit={(e) => {
        e.preventDefault();
        void save({ customLabels: Object.fromEntries(filled.map((r) => [r.key.trim(), r.value])) }, "Labels saved: they apply on the next deployment");
      }}
    >
      <SectionTitle index="02">Custom labels</SectionTitle>
      <p className="text-sm text-muted-foreground">
        Container labels merged last over the generated ones, for example a Traefik middleware. On a compose application they go on its first routed service. Names starting with <span className="font-mono">aethera.</span> or{" "}
        <span className="font-mono">traefik.docker.network</span> are managed by Aethera.
      </p>
      <div className="grid gap-2">
        {rows.map((r) => (
          <div key={r.id} className="grid grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)_auto] items-center gap-2">
            <Input aria-label="Label name" className="font-mono" placeholder="traefik.http.middlewares.x.compress" value={r.key} aria-invalid={protectedKey(r.key) ? true : undefined} onChange={(e) => update(r.id, { key: e.target.value })} />
            <Input aria-label="Label value" className="font-mono" placeholder="true" value={r.value} onChange={(e) => update(r.id, { value: e.target.value })} />
            <Button type="button" variant="ghost" size="icon-sm" aria-label={`Remove ${r.key || "label"}`} onClick={() => setRows((p) => p.filter((x) => x.id !== r.id))}>
              <Trash2Icon aria-hidden="true" />
            </Button>
          </div>
        ))}
      </div>
      {invalid && <p className="text-xs text-danger">{duplicate ? "A label name appears twice." : "Names starting with aethera. or traefik.docker.network cannot be set."}</p>}
      <FormError message={act.error ?? fieldError(act.fields, "customLabels")} />
      <div className="flex gap-2">
        <Button type="button" variant="outline" onClick={() => setRows((p) => [...p, { id: nextId.current++, key: "", value: "" }])}>
          <PlusIcon aria-hidden="true" /> Add label
        </Button>
        <SaveButton pending={act.pending} disabled={invalid} />
      </div>
    </form>
  );
}

export function SettingsTab({ app, api = resourcesApi, servers = serversApi, onSaved }: Props & { servers?: ServersApi }) {
  const { me } = useAuth();
  const [name, setName] = React.useState(app.name);
  const [description, setDescription] = React.useState(app.description ?? "");
  const [confirming, setConfirming] = React.useState(false);
  const { act, save } = usePatch(api, app, onSaved);
  const dirty = name.trim() !== app.name || description !== (app.description ?? "");
  return (
    <div className="grid max-w-3xl gap-8">
      <form
        className="grid gap-5"
        onSubmit={(e) => {
          e.preventDefault();
          void save({ name: name.trim(), description: description.trim() || null }, "Application updated");
        }}
      >
        <SectionTitle index="01">General</SectionTitle>
        <Field label="Name" error={fieldError(act.fields, "name")}>{(c) => <Input {...c} value={name} onChange={(e) => setName(e.target.value)} />}</Field>
        <Field label="Description">{(c) => <Textarea {...c} rows={3} value={description} onChange={(e) => setDescription(e.target.value)} />}</Field>
        <p className="font-mono text-2xs text-muted-foreground">slug: {app.slug} · id: {app.id}</p>
        <FormError message={act.error} />
        <div><SaveButton pending={act.pending} disabled={!dirty || !name.trim()} /></div>
      </form>
      <MovePanel kind="applications" id={app.id} name={app.name} currentServerId={app.serverId} api={api} servers={servers} onMoved={onSaved} />
      {isAdminRole(me?.role) && <CustomLabelsEditor app={app} api={api} onSaved={onSaved} />}
      <section className="grid gap-3 border border-danger/50 p-4">
        <h3 className="text-base font-medium text-danger">Danger zone</h3>
        <p className="text-sm text-muted-foreground">Deleting the application removes its configuration and domains. Containers already running on the server are not touched.</p>
        <div>
          <Button variant="destructive" onClick={() => setConfirming(true)}>
            Delete application
          </Button>
        </div>
      </section>
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        resourceName={app.slug}
        title="Delete application"
        description={`This deletes ${app.name}. Type its slug to confirm.`}
        onConfirm={async (confirm) => {
          await api.applications.remove(app.id, confirm);
          toast.success("Application deleted");
          navigateTo("/applications");
        }}
      />
    </div>
  );
}

