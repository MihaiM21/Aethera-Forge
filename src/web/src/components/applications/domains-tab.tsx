"use client";

import * as React from "react";
import { GlobeIcon, PencilIcon, PlusIcon, RefreshCwIcon, Trash2Icon, TriangleAlertIcon } from "lucide-react";
import { FormError, SaveButton, useAction, numOrUndef, fieldError } from "@/components/resources/form";
import { ErrorPanel, NativeSelect, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { dnsStatusExplanation, dnsStatusTone } from "@/lib/dns/status";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import type { ComposeInspect, DnsProviderOption, Domain } from "@/lib/resources/types";
import { HOSTNAME } from "@/lib/resources/wizard";
import { errorMessage } from "@/lib/servers/errors";
import { formatAgo } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";
import type { WorkloadKind } from "./env-tab";

export function DnsBadge({ domain }: { domain: Pick<Domain, "dns"> }) {
  const s = domain.dns.status;
  return <Badge tone={dnsStatusTone(s)} title={dnsStatusExplanation(s) ?? undefined}>dns {s}</Badge>;
}

export function CertBadge({ domain }: { domain: Pick<Domain, "certificate" | "httpsEnabled"> }) {
  if (!domain.httpsEnabled) return <Badge tone="neutral">http only</Badge>;
  const s = domain.certificate.status;
  return <Badge tone={s === "issued" ? "success" : s === "failed" || s === "expired" ? "danger" : "neutral"}>cert {s}</Badge>;
}

const isWildcard = (host: string) => host.trim().startsWith("*.");
const HOST_OR_WILDCARD = (host: string) => HOSTNAME.test(host.trim().replace(/^\*\./, "")) || host.trim() === "localhost";

type AuthRow = { id: number; username: string; password: string };

/** Compose applications: the services of the compose file (`POST /applications/{id}/compose/inspect`), for the service picker. */
function useComposeServices(api: ResourcesApi, kind: WorkloadKind, id: string, enabled: boolean) {
  const [state, setState] = React.useState<{ loading: boolean; data?: ComposeInspect; error?: string }>({ loading: enabled });
  React.useEffect(() => {
    if (!enabled || kind !== "applications") return;
    let alive = true;
    api.applications.composeInspect(id).then(
      (data) => alive && setState({ loading: false, data }),
      (e) => alive && setState({ loading: false, error: errorMessage(e) }),
    );
    return () => {
      alive = false;
    };
  }, [api, kind, id, enabled]);
  return state;
}

/** Add a domain, or (with `domain`) change one: the service, redirect, prefix stripping, basic auth and certificate options of ADR 0010. */
export function DomainDialog({
  kind,
  id,
  compose = false,
  domain,
  onClose,
  onSaved,
  api,
}: {
  kind: WorkloadKind;
  id: string;
  compose?: boolean;
  domain?: Domain;
  onClose: () => void;
  onSaved: () => void;
  api: ResourcesApi;
}) {
  const editing = domain !== undefined;
  const [hostname, setHostname] = React.useState(domain?.hostname ?? "");
  const [port, setPort] = React.useState(domain?.targetPort != null ? String(domain.targetPort) : "");
  const [path, setPath] = React.useState(domain && domain.pathPrefix !== "/" ? domain.pathPrefix : "");
  const [https, setHttps] = React.useState(domain?.httpsEnabled ?? true);
  const [service, setService] = React.useState(domain?.composeService ?? "");
  const [redirect, setRedirect] = React.useState(domain?.redirectTo ?? "");
  const [strip, setStrip] = React.useState(domain?.stripPathPrefix ?? false);
  const [providerId, setProviderId] = React.useState(domain?.dnsProviderId ?? "");
  const [auth, setAuth] = React.useState<AuthRow[]>([]);
  const [authMode, setAuthMode] = React.useState<"keep" | "replace" | "remove">(editing && (domain.basicAuthUsers?.length ?? 0) > 0 ? "keep" : "replace");
  const authId = React.useRef(1);
  const act = useAction();

  const services = useComposeServices(api, kind, id, compose);
  const providers = usePolled<DnsProviderOption[]>((signal) => api.domains.dnsProviders({ signal }), "dns-providers", { intervalMs: null });
  const providerList = providers.data ?? [];

  const wildcard = isWildcard(hostname);
  const redirecting = redirect.trim() !== "";
  const invalidHost = hostname.trim() !== "" && !HOST_OR_WILDCARD(hostname);
  const invalidRedirect = redirecting && !HOSTNAME.test(redirect.trim());
  const needsProvider = wildcard && providerId === "";
  const existingUsers = domain?.basicAuthUsers ?? [];
  const newUsers = auth.filter((r) => r.username.trim() || r.password);
  const authInvalid = authMode === "replace" && newUsers.some((r) => !/^[A-Za-z0-9._@-]{1,64}$/.test(r.username) || r.password.length < 8);
  const selectedService = services.data?.services.find((s) => s.name === service);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    const users = newUsers.map((r) => ({ username: r.username.trim(), password: r.password }));
    const common = {
      httpsEnabled: https,
      targetPort: redirecting ? null : (numOrUndef(port) ?? null),
      composeService: compose ? service.trim() : undefined,
      redirectTo: redirecting ? redirect.trim().toLowerCase() : null,
      stripPathPrefix: !redirecting && strip,
      dnsProviderId: https && providerId ? providerId : null,
    };
    const ok = editing
      ? await act.run(
          () =>
            api.domains.update(domain.id, {
              ...common,
              pathPrefix: path.trim() || "/",
              ...(authMode === "replace" ? { basicAuth: users } : authMode === "remove" ? { basicAuth: [] } : {}),
            }),
          "Domain updated",
        )
      : await act.run(
          () =>
            api.domains.create(kind, id, {
              ...common,
              hostname: hostname.trim().toLowerCase(),
              pathPrefix: path.trim() || undefined,
              targetPort: common.targetPort ?? undefined,
              redirectTo: common.redirectTo ?? undefined,
              dnsProviderId: common.dnsProviderId ?? undefined,
              ...(users.length > 0 && !redirecting ? { basicAuth: users } : {}),
            }),
          "Domain added",
        );
    if (ok) {
      onSaved();
      onClose();
    }
  };

  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto">
        <form className="grid gap-4" onSubmit={(e) => void submit(e)}>
          <DialogHeader>
            <DialogTitle>{editing ? "Edit domain" : "Add domain"}</DialogTitle>
            <DialogDescription>Point a DNS A record of the hostname at the server. Routing takes effect on the next deployment.</DialogDescription>
          </DialogHeader>
          <Field label="Hostname" error={invalidHost ? "That is not a valid hostname." : fieldError(act.fields, "hostname")} hint={wildcard ? "A wildcard host needs a DNS provider below: its certificate is issued with a DNS-01 challenge." : "Use *.example.com for a wildcard."}>
            {(c) => <Input {...c} className="font-mono" placeholder="app.example.com" value={hostname} disabled={editing} onChange={(e) => setHostname(e.target.value)} required />}
          </Field>

          {compose && (
            <Field
              label="Compose service"
              error={fieldError(act.fields, "composeService")}
              hint={services.error ? undefined : "The service of the compose file that serves this domain."}
            >
              {(c) =>
                services.data && services.data.services.length > 0 ? (
                  <NativeSelect {...c} value={service} onChange={(e) => setService(e.target.value)} required>
                    <option value="">Select a service…</option>
                    {services.data.services.map((s) => (
                      <option key={s.name} value={s.name}>
                        {s.name}
                        {s.ports.length > 0 ? ` (port ${s.ports.join(", ")})` : ""}
                      </option>
                    ))}
                    {service !== "" && !services.data.services.some((s) => s.name === service) && <option value={service}>{service} (not in the file)</option>}
                  </NativeSelect>
                ) : (
                  <Input {...c} className="font-mono" placeholder="web" value={service} onChange={(e) => setService(e.target.value)} required />
                )
              }
            </Field>
          )}
          {compose && services.loading && <p className="text-xs text-muted-foreground">Reading the compose file…</p>}
          {compose && services.error && (
            <p role="alert" className="border border-warning/70 bg-warning-soft px-3 py-2 text-xs">
              The services could not be listed ({services.error}). Type the service name; it is checked when the application deploys.
            </p>
          )}
          {compose && services.data && services.data.violations.length > 0 && (
            <p role="alert" className="border border-danger bg-danger-soft px-3 py-2 text-xs text-danger">
              The compose file is not allowed: {services.data.violations.join("; ")}
            </p>
          )}

          <Field label="Redirect to" hint="Answer with a permanent redirect to this hostname instead of serving the application (path and query are kept). Use it for www to apex." error={invalidRedirect ? "That is not a valid hostname." : fieldError(act.fields, "redirectTo")}>
            {(c) => <Input {...c} className="font-mono" placeholder="example.com" value={redirect} onChange={(e) => setRedirect(e.target.value)} />}
          </Field>

          {!redirecting && (
            <>
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Target port" hint={selectedService?.ports.length ? `Empty = ${selectedService.ports[0]} (from the compose file).` : "Empty = the application port."}>
                  {(c) => <Input {...c} inputMode="numeric" className="font-mono" value={port} onChange={(e) => setPort(e.target.value)} />}
                </Field>
                <Field label="Path prefix" hint="Empty = the whole host." error={fieldError(act.fields, "pathPrefix")}>
                  {(c) => <Input {...c} className="font-mono" placeholder="/api" value={path} onChange={(e) => setPath(e.target.value)} />}
                </Field>
              </div>
              <label className="flex items-center gap-3 text-sm">
                <Switch checked={strip} onCheckedChange={setStrip} aria-label="Strip the path prefix" disabled={!path.trim() || path.trim() === "/"} />
                Strip the path prefix before the request reaches the container
              </label>
            </>
          )}

          <label className="flex items-center gap-3 text-sm">
            <Switch checked={https} onCheckedChange={setHttps} aria-label="HTTPS" /> HTTPS
          </label>
          {https && (
            <Field label="Certificate" error={needsProvider ? "A wildcard host needs a DNS provider." : fieldError(act.fields, "dnsProviderId")} hint={providerList.length === 0 ? "Add a DNS provider under Settings to use DNS-01 (wildcards, certificates before the DNS switch)." : undefined}>
              {(c) => (
                <NativeSelect {...c} value={providerId} onChange={(e) => setProviderId(e.target.value)}>
                  <option value="">HTTP-01 (Let&apos;s Encrypt)</option>
                  {providerList.map((p) => (
                    <option key={p.id} value={p.id}>
                      DNS-01 via {p.name}
                    </option>
                  ))}
                  {providerId !== "" && !providerList.some((p) => p.id === providerId) && <option value={providerId}>DNS-01 via the current provider</option>}
                </NativeSelect>
              )}
            </Field>
          )}

          {!redirecting && (
            <fieldset className="grid gap-2">
              <legend className="mb-1 font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Basic auth</legend>
              {authMode === "keep" ? (
                <div className="flex flex-wrap items-center gap-2 text-sm">
                  <span className="text-muted-foreground">Users:</span>
                  {existingUsers.map((u) => (
                    <Badge key={u} tone="neutral">
                      {u}
                    </Badge>
                  ))}
                  <Button type="button" size="sm" variant="outline" onClick={() => setAuthMode("replace")}>
                    Replace users
                  </Button>
                  <Button type="button" size="sm" variant="outline" onClick={() => setAuthMode("remove")}>
                    Remove basic auth
                  </Button>
                </div>
              ) : authMode === "remove" ? (
                <p className="text-sm text-muted-foreground">
                  Basic auth will be removed.{" "}
                  <button type="button" className="text-lime hover:underline" onClick={() => setAuthMode("keep")}>
                    Keep it
                  </button>
                </p>
              ) : (
                <>
                  <p className="text-xs text-muted-foreground">Visitors must sign in with one of these users. Passwords are hashed (bcrypt) and never shown again{editing && existingUsers.length > 0 ? "; saving replaces the current users" : ""}.</p>
                  {auth.map((r) => (
                    <div key={r.id} className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] items-center gap-2">
                      <Input aria-label="Basic auth user name" className="font-mono" placeholder="user" value={r.username} autoComplete="off" onChange={(e) => setAuth((p) => p.map((x) => (x.id === r.id ? { ...x, username: e.target.value } : x)))} />
                      <Input aria-label="Basic auth password" type="password" className="font-mono" placeholder="password (8+)" value={r.password} autoComplete="new-password" onChange={(e) => setAuth((p) => p.map((x) => (x.id === r.id ? { ...x, password: e.target.value } : x)))} />
                      <Button type="button" variant="ghost" size="icon-sm" aria-label={`Remove user ${r.username || ""}`.trim()} onClick={() => setAuth((p) => p.filter((x) => x.id !== r.id))}>
                        <Trash2Icon aria-hidden="true" />
                      </Button>
                    </div>
                  ))}
                  {authInvalid && <p className="text-xs text-danger">User names use letters, digits and . _ @ - ; passwords need at least 8 characters.</p>}
                  <FormError message={fieldError(act.fields, "basicAuth") ?? null} />
                  <div className="flex gap-2">
                    <Button type="button" size="sm" variant="outline" onClick={() => setAuth((p) => [...p, { id: authId.current++, username: "", password: "" }])}>
                      <PlusIcon aria-hidden="true" /> Add user
                    </Button>
                    {editing && existingUsers.length > 0 && (
                      <Button type="button" size="sm" variant="ghost" onClick={() => { setAuth([]); setAuthMode("keep"); }}>
                        Cancel
                      </Button>
                    )}
                  </div>
                </>
              )}
            </fieldset>
          )}

          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
              Cancel
            </Button>
            <SaveButton pending={act.pending} disabled={!hostname.trim() || invalidHost || invalidRedirect || needsProvider || authInvalid || (compose && !service.trim())}>
              {editing ? "Save" : "Add domain"}
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Kept for callers that only add (the dialog is the same). */
export const AddDomainDialog = DomainDialog;

/** Domains of one application or service, with DNS verification, certificate state and the routing options. */
export function DomainsTab({ kind, id, compose = false, api = resourcesApi }: { kind: WorkloadKind; id: string; compose?: boolean; api?: ResourcesApi }) {
  const list = usePolled((signal) => api.domains.list({ workloadId: id, limit: 100 }, { signal }), `domains:${id}`, { intervalMs: 15_000 });
  const [dialog, setDialog] = React.useState<{ domain?: Domain } | null>(null);
  const act = useAction();
  const rows = list.data?.items ?? [];
  const warnings = Array.from(new Set(rows.flatMap((d) => d.warnings ?? [])));
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle
        index="01"
        actions={
          <Button size="sm" onClick={() => setDialog({})}>
            <PlusIcon aria-hidden="true" /> Add domain
          </Button>
        }
      >
        Domains
      </SectionTitle>
      {compose && <p className="text-sm text-muted-foreground">Each domain of a compose application names the service of the compose file that serves it.</p>}
      <FormError message={act.error} />
      {warnings.map((w) => (
        <p key={w} role="alert" className="flex items-start gap-2 border border-warning/70 bg-warning-soft px-3 py-2 text-xs">
          <TriangleAlertIcon className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" /> {w}
        </p>
      ))}
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load domains" />}
      {list.loading && <Skeleton className="h-20 w-full" />}
      {list.data && rows.length === 0 && (
        <p className="flex items-center justify-center gap-2 border border-dashed border-border px-4 py-6 text-sm text-muted-foreground">
          <GlobeIcon className="size-4 text-lime" aria-hidden="true" /> No domains yet. Add one to route traffic and issue a certificate.
        </p>
      )}
      <ul className="grid gap-2">
        {rows.map((d) => (
          <li key={d.id} className="grid gap-2 border border-border bg-card px-4 py-3 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-center">
            <div className="min-w-0">
              <p className="flex flex-wrap items-center gap-2 font-mono text-sm">
                {d.hostname.startsWith("*.") ? (
                  <span className="truncate">{d.hostname}</span>
                ) : (
                  <a href={`${d.httpsEnabled ? "https" : "http"}://${d.hostname}${d.pathPrefix === "/" ? "" : d.pathPrefix}`} target="_blank" rel="noreferrer" className="truncate hover:text-lime">
                    {d.hostname}
                    {d.pathPrefix !== "/" ? d.pathPrefix : ""}
                  </a>
                )}
                {d.isPrimary && <Badge tone="brand">primary</Badge>}
                {d.composeService && <Badge tone="neutral">service {d.composeService}</Badge>}
                {d.redirectTo && <Badge tone="neutral">→ {d.redirectTo}</Badge>}
                {d.stripPathPrefix && <Badge tone="neutral">strip prefix</Badge>}
                {(d.basicAuthUsers?.length ?? 0) > 0 && <Badge tone="neutral">basic auth · {d.basicAuthUsers.join(", ")}</Badge>}
                {d.dnsProviderId && <Badge tone="neutral">DNS-01</Badge>}
              </p>
              <p className="mt-1.5 flex flex-wrap items-center gap-1.5">
                <DnsBadge domain={d} />
                <CertBadge domain={d} />
                {d.dns.checkedAt != null && <span className="font-mono text-2xs text-muted-foreground">checked {formatAgo(d.dns.checkedAt)}</span>}
                {d.dns.resolvedIps.length > 0 && <span className="font-mono text-2xs text-muted-foreground">→ {d.dns.resolvedIps.join(", ")}</span>}
              </p>
              {d.certificate.error && <p className="mt-1 text-xs text-danger">{d.certificate.error}</p>}
            </div>
            <div className="flex gap-2">
              <Button size="sm" variant="outline" onClick={() => setDialog({ domain: d })} aria-label={`Edit ${d.hostname}`}>
                <PencilIcon aria-hidden="true" /> Edit
              </Button>
              <Button size="sm" variant="outline" onClick={async () => { if (await act.runOk(() => api.domains.verifyDns(d.id), "DNS checked")) await list.refresh(); }}>
                <RefreshCwIcon aria-hidden="true" /> Verify DNS
              </Button>
              {!d.isPrimary && (
                <Button size="sm" variant="outline" onClick={async () => { if (await act.runOk(() => api.domains.update(d.id, { isPrimary: true }), "Primary domain changed")) await list.refresh(); }}>
                  Make primary
                </Button>
              )}
              <Button
                size="icon-sm"
                variant="ghost"
                aria-label={`Remove ${d.hostname}`}
                onClick={async () => {
                  if (!window.confirm(`Remove ${d.hostname}?`)) return;
                  if (await act.runOk(() => api.domains.remove(d.id), "Domain removed")) await list.refresh();
                }}
              >
                <Trash2Icon aria-hidden="true" />
              </Button>
            </div>
          </li>
        ))}
      </ul>
      {dialog && <DomainDialog kind={kind} id={id} compose={compose} domain={dialog.domain} api={api} onClose={() => setDialog(null)} onSaved={() => void list.refresh()} />}
    </div>
  );
}
