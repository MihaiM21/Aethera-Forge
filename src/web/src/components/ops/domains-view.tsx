"use client";

import * as React from "react";
import { GlobeIcon, PlusIcon, RefreshCwIcon, SearchIcon, Trash2Icon } from "lucide-react";
import { AddDomainDialog, CertBadge, DnsBadge } from "@/components/applications/domains-tab";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { PageHeader } from "@/components/aethera/page-header";
import { FormError, useAction } from "@/components/resources/form";
import { ErrorPanel, NativeSelect } from "@/components/servers/common";
import { DetailLink } from "@/components/shell/detail-link";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import type { Domain } from "@/lib/resources/types";
import { formatAgo } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

type Owner = { name: string; href: string; kind: "applications" | "services" };

/** Picks the application or service a new domain is attached to, then reuses the per-workload dialog. */
function ChooseWorkloadDialog({ owners, onChoose, onClose }: { owners: Array<Owner & { id: string }>; onChoose: (o: Owner & { id: string }) => void; onClose: () => void }) {
  const [id, setId] = React.useState(owners[0]?.id ?? "");
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Add domain</DialogTitle>
          <DialogDescription>Which application or service should the domain route to?</DialogDescription>
        </DialogHeader>
        {owners.length === 0 ? (
          <p className="text-sm text-muted-foreground">Create an application or a service first.</p>
        ) : (
          <Field label="Route to">
            {(c) => (
              <NativeSelect {...c} value={id} onChange={(e) => setId(e.target.value)}>
                {owners.map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.name} ({o.kind === "services" ? "service" : "application"})
                  </option>
                ))}
              </NativeSelect>
            )}
          </Field>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <Button onClick={() => onChoose(owners.find((o) => o.id === id)!)} disabled={!id}>
            Continue
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

export function matchesDomain(d: Domain, q: string): boolean {
  const t = q.trim().toLowerCase();
  return !t || d.hostname.toLowerCase().includes(t);
}

/** All hostnames of the installation with their DNS and certificate state; attach, verify and remove them here. */
export function DomainsView({ api = resourcesApi }: { api?: ResourcesApi }) {
  const [q, setQ] = React.useState("");
  const [dns, setDns] = React.useState("");
  const list = usePolled((signal) => api.domains.list({ limit: 200, sort: "hostname", dnsStatus: dns || undefined }, { signal }), `domains:${dns}`, { intervalMs: 15_000 });
  const apps = usePolled((signal) => api.applications.list({ limit: 100 }, { signal }), "dom-apps", { intervalMs: null });
  const services = usePolled((signal) => api.services.list({ limit: 100 }, { signal }), "dom-svcs", { intervalMs: null });
  const [choosing, setChoosing] = React.useState(false);
  const [adding, setAdding] = React.useState<(Owner & { id: string }) | null>(null);
  const act = useAction();

  const owners = React.useMemo(() => {
    const m = new Map<string, Owner & { id: string }>();
    for (const a of apps.data?.items ?? []) m.set(a.id, { id: a.id, name: a.name, href: `/applications/${a.id}`, kind: "applications" });
    for (const s of services.data?.items ?? []) m.set(s.id, { id: s.id, name: s.name, href: `/services/${s.id}`, kind: "services" });
    return m;
  }, [apps.data, services.data]);

  const rows = (list.data?.items ?? []).filter((d) => matchesDomain(d, q));
  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        eyebrow="Networking"
        title="Domains"
        description="Hostnames, routing and certificates for your applications and services."
        actions={
          <>
            <Button variant="outline" onClick={() => void list.refresh()} disabled={list.refreshing} aria-label="Refresh domains">
              <RefreshCwIcon className={list.refreshing ? "animate-spin motion-reduce:animate-none" : undefined} aria-hidden="true" /> Refresh
            </Button>
            <Button onClick={() => setChoosing(true)}>
              <PlusIcon aria-hidden="true" /> Add domain
            </Button>
          </>
        }
      />
      <div className="flex flex-wrap items-end gap-3">
        <div className="relative min-w-56 flex-1 sm:max-w-sm">
          <label htmlFor="domain-search" className="sr-only">
            Search domains
          </label>
          <SearchIcon className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
          <Input id="domain-search" type="search" placeholder="Search hostname…" value={q} onChange={(e) => setQ(e.target.value)} className="pl-8" />
        </div>
        <label htmlFor="domain-dns" className="sr-only">
          DNS status
        </label>
        <NativeSelect id="domain-dns" value={dns} onChange={(e) => setDns(e.target.value)} className="w-44">
          <option value="">All DNS states</option>
          <option value="ok">DNS ok</option>
          <option value="mismatch">Mismatch</option>
          <option value="missing">Missing</option>
          <option value="unknown">Unknown</option>
        </NativeSelect>
      </div>
      <FormError message={act.error} />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load domains" />}
      {list.loading && <Skeleton className="h-24 w-full" />}
      {list.data && list.data.items.length === 0 && !dns && (
        <DotGrid fade className="border border-dashed border-border">
          <EmptyState
            icon={GlobeIcon}
            label="Domains · empty"
            title="No domains yet"
            description="Attach a hostname to an application to route traffic and issue certificates."
            action={<Button onClick={() => setChoosing(true)}>Add domain</Button>}
          />
        </DotGrid>
      )}
      {rows.length > 0 && (
        <ul className="border border-border bg-card">
          {rows.map((d) => {
            const owner = owners.get(d.workloadId);
            return (
              <li key={d.id} className="grid gap-3 border-b border-border px-4 py-3 last:border-b-0 md:grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)_auto] md:items-center">
                <div className="min-w-0">
                  <p className="flex flex-wrap items-center gap-2 font-mono text-sm">
                    <a href={`${d.httpsEnabled ? "https" : "http"}://${d.hostname}`} target="_blank" rel="noreferrer" className="truncate hover:text-lime">
                      {d.hostname}
                      {d.pathPrefix !== "/" ? d.pathPrefix : ""}
                    </a>
                    {d.isPrimary && <Badge tone="brand">primary</Badge>}
                  </p>
                  <p className="mt-1 text-xs text-muted-foreground">
                    {owner ? (
                      <DetailLink href={owner.href} className="hover:text-lime">
                        {owner.name}
                      </DetailLink>
                    ) : (
                      "…"
                    )}
                    {d.dns.checkedAt != null && <span className="ml-2 font-mono text-2xs">checked {formatAgo(d.dns.checkedAt)}</span>}
                  </p>
                </div>
                <div className="flex flex-wrap items-center gap-1.5">
                  <DnsBadge domain={d} />
                  <CertBadge domain={d} />
                </div>
                <div className="flex gap-2">
                  <Button size="sm" variant="outline" onClick={async () => { if (await act.runOk(() => api.domains.verifyDns(d.id), "DNS checked")) await list.refresh(); }}>
                    Verify DNS
                  </Button>
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
            );
          })}
        </ul>
      )}
      {choosing && (
        <ChooseWorkloadDialog
          owners={[...owners.values()]}
          onClose={() => setChoosing(false)}
          onChoose={(o) => {
            setChoosing(false);
            setAdding(o);
          }}
        />
      )}
      {adding && <AddDomainDialog kind={adding.kind} id={adding.id} api={api} onClose={() => setAdding(null)} onAdded={() => void list.refresh()} />}
    </div>
  );
}
