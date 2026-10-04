"use client";

import * as React from "react";
import { CopyIcon, PlusIcon, Trash2Icon } from "lucide-react";
import { FormError, SaveButton, fieldError, numOrUndef, useAction } from "@/components/resources/form";
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

type PortRow = { id: number; port: string; protocol: "tcp" | "udp"; published: string; http: boolean };

export function NetworkingTab({ app, api = resourcesApi, onSaved }: Props) {
  const next = React.useRef(1000);
  const [rows, setRows] = React.useState<PortRow[]>(() =>
    app.runtime.ports.map((p, i) => ({ id: i, port: str(p.containerPort), protocol: p.protocol === "udp" ? "udp" : "tcp", published: str(p.publishedPort), http: p.isHttp })),
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
            ports: rows.filter((r) => r.port.trim()).map((r) => ({ containerPort: Number(r.port), protocol: r.protocol, publishedPort: numOrUndef(r.published), isHttp: r.http })),
          },
        });
      }}
    >
      <SectionTitle index="01">Ports</SectionTitle>
      <p className="text-xs text-muted-foreground">
        HTTP ports are routed by the reverse proxy to your domains. A published port opens the port on the server itself; leave it empty to keep the application reachable only through the proxy
        and other containers.
      </p>
      <div className="grid gap-2">
        {rows.length === 0 && <p className="text-sm text-muted-foreground">No ports configured.</p>}
        {rows.map((r) => (
          <div key={r.id} className="grid grid-cols-[6rem_6rem_7rem_auto_auto] items-center gap-2">
            <Input aria-label="Container port" inputMode="numeric" className="font-mono" placeholder="3000" value={r.port} onChange={(e) => update(r.id, { port: e.target.value })} />
            <NativeSelect aria-label="Protocol" value={r.protocol} onChange={(e) => update(r.id, { protocol: e.target.value as "tcp" | "udp" })}>
              <option value="tcp">tcp</option>
              <option value="udp">udp</option>
            </NativeSelect>
            <Input aria-label="Published port" inputMode="numeric" className="font-mono" placeholder="publish" value={r.published} onChange={(e) => update(r.id, { published: e.target.value })} />
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
        <Button type="button" variant="outline" onClick={() => setRows((r) => [...r, { id: next.current++, port: "", protocol: "tcp", published: "", http: r.length === 0 }])}>
          <PlusIcon aria-hidden="true" /> Add port
        </Button>
      </div>
      <FormError message={act.error} />
      <div>
        <SaveButton pending={act.pending}>Save ports</SaveButton>
      </div>
    </form>
  );
}

// ------------------------------------------------------------------------------------------------ Resources

const MIB = 1024 * 1024;

export function ResourcesTab({ app, api = resourcesApi, onSaved }: Props) {
  const r = app.runtime.resources;
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
      <SectionTitle index="02">Deployment behaviour</SectionTitle>
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

export function BuildTab({ app, api = resourcesApi, onSaved }: Props) {
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
  const [compose, setCompose] = React.useState(app.compose?.inlineContent ?? "");
  const { act, save } = usePatch(api, app, onSaved);
  const engine = b?.engine ?? "";

  if (app.sourceKind === "dockerImage") {
    return (
      <form className="grid max-w-3xl gap-5" onSubmit={(e) => { e.preventDefault(); void save({ image: { image: image.trim(), tag: tag.trim() } }); }}>
        <SectionTitle index="01">Image</SectionTitle>
        <div className="grid gap-4 sm:grid-cols-3">
          <Field label="Image" className="sm:col-span-2" error={fieldError(act.fields, "image")}>{(c) => <Input {...c} className="font-mono" value={image} onChange={(e) => setImage(e.target.value)} />}</Field>
          <Field label="Tag" error={fieldError(act.fields, "tag")}>{(c) => <Input {...c} className="font-mono" value={tag} onChange={(e) => setTag(e.target.value)} />}</Field>
        </div>
        <FormError message={act.error} />
        <div><SaveButton pending={act.pending} /></div>
      </form>
    );
  }
  if (app.sourceKind === "compose") {
    return (
      <form className="grid max-w-3xl gap-5" onSubmit={(e) => { e.preventDefault(); void save({ compose: { inlineContent: compose } }); }}>
        <SectionTitle index="01">Compose file</SectionTitle>
        <Field label="compose.yaml" error={fieldError(act.fields, "inlineContent")}>{(c) => <Textarea {...c} rows={16} className="font-mono text-xs" value={compose} onChange={(e) => setCompose(e.target.value)} />}</Field>
        <FormError message={act.error} />
        <div><SaveButton pending={act.pending} /></div>
      </form>
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
      <WebhookPanel app={app} api={api} />
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ Settings

export function SettingsTab({ app, api = resourcesApi, servers = serversApi, onSaved }: Props & { servers?: ServersApi }) {
  const [name, setName] = React.useState(app.name);
  const [description, setDescription] = React.useState(app.description ?? "");
  const [serverId, setServerId] = React.useState(app.serverId);
  const [confirming, setConfirming] = React.useState(false);
  const list = usePolled((signal) => servers.list({ limit: 100, sort: "name" }, { signal }), "settings-servers", { intervalMs: null });
  const { act, save } = usePatch(api, app, onSaved);
  const dirty = name.trim() !== app.name || description !== (app.description ?? "") || serverId !== app.serverId;
  return (
    <div className="grid max-w-3xl gap-8">
      <form
        className="grid gap-5"
        onSubmit={(e) => {
          e.preventDefault();
          void save({ name: name.trim(), description: description.trim() || null, ...(serverId !== app.serverId ? { serverId } : {}) }, "Application updated");
        }}
      >
        <SectionTitle index="01">General</SectionTitle>
        <Field label="Name" error={fieldError(act.fields, "name")}>{(c) => <Input {...c} value={name} onChange={(e) => setName(e.target.value)} />}</Field>
        <Field label="Description">{(c) => <Textarea {...c} rows={3} value={description} onChange={(e) => setDescription(e.target.value)} />}</Field>
        <Field label="Server" hint="Moving to another server takes effect on the next deployment; domains move with it.">
          {(c) => (
            <NativeSelect {...c} value={serverId} onChange={(e) => setServerId(e.target.value)}>
              {(list.data?.items ?? []).map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name} · {s.host}
                </option>
              ))}
              {!(list.data?.items ?? []).some((s) => s.id === app.serverId) && <option value={app.serverId}>Current server</option>}
            </NativeSelect>
          )}
        </Field>
        <p className="font-mono text-2xs text-muted-foreground">slug: {app.slug} · id: {app.id}</p>
        <FormError message={act.error} />
        <div><SaveButton pending={act.pending} disabled={!dirty || !name.trim()} /></div>
      </form>
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

