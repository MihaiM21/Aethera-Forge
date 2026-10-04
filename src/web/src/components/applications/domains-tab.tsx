"use client";

import * as React from "react";
import { GlobeIcon, PlusIcon, RefreshCwIcon, Trash2Icon } from "lucide-react";
import { FormError, SaveButton, useAction, numOrUndef, fieldError } from "@/components/resources/form";
import { ErrorPanel, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import type { Domain } from "@/lib/resources/types";
import { HOSTNAME } from "@/lib/resources/wizard";
import { formatAgo } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";
import type { WorkloadKind } from "./env-tab";

export function DnsBadge({ domain }: { domain: Pick<Domain, "dns"> }) {
  const s = domain.dns.status;
  const tone = s === "ok" ? "success" : s === "unknown" ? "neutral" : "warning";
  return <Badge tone={tone}>dns {s}</Badge>;
}

export function CertBadge({ domain }: { domain: Pick<Domain, "certificate" | "httpsEnabled"> }) {
  if (!domain.httpsEnabled) return <Badge tone="neutral">http only</Badge>;
  const s = domain.certificate.status;
  return <Badge tone={s === "issued" ? "success" : s === "failed" || s === "expired" ? "danger" : "neutral"}>cert {s}</Badge>;
}

export function AddDomainDialog({ kind, id, onClose, onAdded, api }: { kind: WorkloadKind; id: string; onClose: () => void; onAdded: () => void; api: ResourcesApi }) {
  const [hostname, setHostname] = React.useState("");
  const [port, setPort] = React.useState("");
  const [path, setPath] = React.useState("");
  const [https, setHttps] = React.useState(true);
  const act = useAction();
  const invalid = hostname.trim() !== "" && !HOSTNAME.test(hostname.trim());
  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            const ok = await act.run(
              () => api.domains.create(kind, id, { hostname: hostname.trim().toLowerCase(), httpsEnabled: https, targetPort: numOrUndef(port), pathPrefix: path.trim() || undefined }),
              "Domain added",
            );
            if (ok) {
              onAdded();
              onClose();
            }
          }}
        >
          <DialogHeader>
            <DialogTitle>Add domain</DialogTitle>
            <DialogDescription>Point a DNS A record of the hostname at the server. Routing takes effect on the next deployment.</DialogDescription>
          </DialogHeader>
          <Field label="Hostname" error={invalid ? "That is not a valid hostname." : fieldError(act.fields, "hostname")}>
            {(c) => <Input {...c} className="font-mono" placeholder="app.example.com" value={hostname} onChange={(e) => setHostname(e.target.value)} required />}
          </Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Target port" hint="Empty = the application port.">
              {(c) => <Input {...c} inputMode="numeric" className="font-mono" value={port} onChange={(e) => setPort(e.target.value)} />}
            </Field>
            <Field label="Path prefix" hint="Empty = the whole host.">
              {(c) => <Input {...c} className="font-mono" placeholder="/api" value={path} onChange={(e) => setPath(e.target.value)} />}
            </Field>
          </div>
          <label className="flex items-center gap-3 text-sm">
            <Switch checked={https} onCheckedChange={setHttps} aria-label="HTTPS" /> HTTPS (Let&apos;s Encrypt)
          </label>
          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
              Cancel
            </Button>
            <SaveButton pending={act.pending} disabled={!hostname.trim() || invalid}>
              Add domain
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Domains of one application or service, with DNS verification and certificate state. */
export function DomainsTab({ kind, id, api = resourcesApi }: { kind: WorkloadKind; id: string; api?: ResourcesApi }) {
  const list = usePolled((signal) => api.domains.list({ workloadId: id, limit: 100 }, { signal }), `domains:${id}`, { intervalMs: 15_000 });
  const [adding, setAdding] = React.useState(false);
  const act = useAction();
  const rows = list.data?.items ?? [];
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle
        index="01"
        actions={
          <Button size="sm" onClick={() => setAdding(true)}>
            <PlusIcon aria-hidden="true" /> Add domain
          </Button>
        }
      >
        Domains
      </SectionTitle>
      <FormError message={act.error} />
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
                <a href={`${d.httpsEnabled ? "https" : "http"}://${d.hostname}${d.pathPrefix === "/" ? "" : d.pathPrefix}`} target="_blank" rel="noreferrer" className="truncate hover:text-lime">
                  {d.hostname}
                  {d.pathPrefix !== "/" ? d.pathPrefix : ""}
                </a>
                {d.isPrimary && <Badge tone="brand">primary</Badge>}
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
      {adding && <AddDomainDialog kind={kind} id={id} api={api} onClose={() => setAdding(false)} onAdded={() => void list.refresh()} />}
    </div>
  );
}
