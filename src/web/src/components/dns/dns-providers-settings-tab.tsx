"use client";

import * as React from "react";
import { CheckCircle2Icon, CloudIcon, PencilIcon, PlusIcon, RefreshCwIcon, Trash2Icon, TriangleAlertIcon } from "lucide-react";
import { FormError, SaveButton, fieldError, useAction } from "@/components/resources/form";
import { ErrorPanel, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { dnsApi, type DnsApi } from "@/lib/dns/api";
import type { DnsProvider, DnsProviderVerify } from "@/lib/dns/types";
import { formatAgo } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

const MAX_ZONES_SHOWN = 8;

/** How to create the Cloudflare token Aethera needs; shown in the dialog and as the empty state. */
export function TokenHowTo() {
  return (
    <div className="grid gap-2 border border-border bg-muted/40 px-3 py-2.5 text-xs text-muted-foreground">
      <p className="font-mono text-2xs uppercase tracking-[0.06em] text-foreground">Create a Cloudflare API token</p>
      <ol className="list-decimal space-y-0.5 pl-4">
        <li>Cloudflare dashboard &rarr; My Profile &rarr; API Tokens &rarr; Create Token &rarr; Create Custom Token.</li>
        <li>
          Permissions: <span className="font-mono text-foreground">Zone &rarr; DNS &rarr; Edit</span> and{" "}
          <span className="font-mono text-foreground">Zone &rarr; Zone &rarr; Read</span>.
        </li>
        <li>Zone Resources: include the zones you serve (or all zones), then copy the token here.</li>
      </ol>
      <p>
        With it Aethera can issue wildcard certificates (DNS-01) and certificates before the DNS switch, and it can check a record behind the orange cloud. The
        token is stored encrypted and never shown again.
      </p>
    </div>
  );
}

function VerifyResult({ result }: { result: DnsProviderVerify }) {
  const good = result.valid && result.dnsAccess;
  return (
    <div className="grid gap-3" role="status">
      <div className={`flex items-start gap-2 border px-3 py-2 text-sm ${good ? "border-success/60 bg-success-soft" : "border-warning/60 bg-warning-soft"}`}>
        {good ? <CheckCircle2Icon className="mt-0.5 size-4 text-success" aria-hidden="true" /> : <TriangleAlertIcon className="mt-0.5 size-4 text-warning" aria-hidden="true" />}
        <p>{good ? "The token works and can edit DNS records." : (result.message ?? "The token could not be verified.")}</p>
      </div>
      {result.zones.length > 0 && (
        <div>
          <p className="mb-1 font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Zones the token sees ({result.zones.length})</p>
          <ul className="flex max-h-40 flex-wrap gap-1.5 overflow-y-auto" aria-label="Zones">
            {result.zones.map((z) => (
              <li key={z} className="border border-border bg-card px-1.5 py-0.5 font-mono text-2xs">
                {z}
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

function ProviderDialog({ provider, api, onClose, onSaved }: { provider: DnsProvider | null; api: DnsApi; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = React.useState(provider?.name ?? "");
  const [token, setToken] = React.useState("");
  const [result, setResult] = React.useState<DnsProviderVerify | null>(null);
  const act = useAction();
  const done = result !== null;
  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && (onClose(), done && onSaved())}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            if (done) {
              onClose();
              return;
            }
            const saved = await act.run(async () => {
              const body = { name: name.trim(), ...(token ? { apiToken: token.trim() } : {}) };
              const p = provider ? await api.update(provider.id, body) : await api.create({ ...body, apiToken: token.trim() });
              // A new token has no zones yet: verify straight away so the dialog can show what it sees.
              return token ? await api.verify(p.id) : null;
            }, provider ? "DNS provider updated" : "DNS provider added");
            if (saved === undefined) return;
            onSaved();
            if (saved) setResult(saved);
            else onClose();
          }}
        >
          <DialogHeader>
            <DialogTitle>{done ? "Token verified" : provider ? `Edit ${provider.name}` : "Add DNS provider"}</DialogTitle>
            <DialogDescription>
              {done ? "The provider is saved. This is what Cloudflare said about the token." : "Cloudflare account that holds the DNS zones of your domains."}
            </DialogDescription>
          </DialogHeader>
          {done ? (
            <VerifyResult result={result} />
          ) : (
            <>
              <TokenHowTo />
              <Field label="Name" error={fieldError(act.fields, "name")} hint="Any label, e.g. Cloudflare main account.">
                {(c) => <Input {...c} value={name} onChange={(e) => setName(e.target.value)} required />}
              </Field>
              <Field
                label="API token"
                error={fieldError(act.fields, "apiToken")}
                hint={provider ? "Leave empty to keep the stored token. A new token must be verified again." : undefined}
              >
                {(c) => <Input {...c} type="password" autoComplete="new-password" className="font-mono" value={token} onChange={(e) => setToken(e.target.value)} required={!provider} />}
              </Field>
              <FormError message={act.error} />
            </>
          )}
          <DialogFooter>
            {!done && (
              <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
                Cancel
              </Button>
            )}
            <SaveButton pending={act.pending} disabled={!done && (!name.trim() || (!provider && !token.trim()))}>
              {done ? "Done" : provider ? "Save" : "Add and verify"}
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function ProviderStatus({ provider }: { provider: DnsProvider }) {
  if (provider.lastError) return <Badge tone="warning">needs attention</Badge>;
  if (provider.lastVerifiedAt) return <Badge tone="success">verified</Badge>;
  return <Badge tone="neutral">not verified</Badge>;
}

/** Settings > DNS: DNS providers (Cloudflare API token) for DNS-01 certificates and DNS checks that see past the Cloudflare proxy (ADR 0010). */
export function DnsProvidersSettingsTab({ api = dnsApi }: { api?: DnsApi }) {
  const list = usePolled((signal) => api.list({}, { signal }), "dns-providers", { intervalMs: null });
  const [editing, setEditing] = React.useState<DnsProvider | "new" | null>(null);
  const [deleting, setDeleting] = React.useState<DnsProvider | null>(null);
  const [verified, setVerified] = React.useState<DnsProviderVerify | null>(null);
  const [verifying, setVerifying] = React.useState<string | null>(null);
  const act = useAction();
  const rows = list.data?.items ?? [];

  async function verify(p: DnsProvider) {
    setVerifying(p.id);
    const result = await act.run(() => api.verify(p.id));
    setVerifying(null);
    if (result) {
      setVerified(result);
      await list.refresh();
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <SectionTitle
        index="01"
        actions={
          <Button size="sm" onClick={() => setEditing("new")}>
            <PlusIcon aria-hidden="true" /> Add provider
          </Button>
        }
      >
        DNS providers
      </SectionTitle>
      <p className="text-sm text-muted-foreground">
        A Cloudflare API token lets Aethera issue wildcard certificates and certificates before the DNS switch, and check records that are proxied (orange cloud).
      </p>
      <FormError message={act.error} />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load DNS providers" />}
      {list.loading && <Skeleton className="h-20 w-full" />}
      {list.data && rows.length === 0 && (
        <div className="grid gap-3 border border-dashed border-border px-4 py-5">
          <p className="flex items-center gap-2 text-sm">
            <CloudIcon className="size-4 text-lime" aria-hidden="true" /> No DNS provider yet.
          </p>
          <TokenHowTo />
        </div>
      )}
      {rows.length > 0 && (
        <ul className="border border-border bg-card">
          {rows.map((p) => (
            <li key={p.id} className="grid gap-2 border-b border-border px-4 py-3 last:border-b-0">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="min-w-0">
                  <p className="flex items-center gap-2 font-medium">
                    {p.name} <ProviderStatus provider={p} />
                  </p>
                  <p className="font-mono text-2xs text-muted-foreground">
                    {p.kind} · {p.zones.length} zone{p.zones.length === 1 ? "" : "s"}
                    {p.lastVerifiedAt != null ? ` · verified ${formatAgo(p.lastVerifiedAt as string)}` : ""}
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  <Button size="sm" variant="outline" onClick={() => void verify(p)} disabled={verifying !== null} aria-label={`Verify ${p.name}`}>
                    <RefreshCwIcon className={verifying === p.id ? "animate-spin motion-reduce:animate-none" : undefined} aria-hidden="true" /> Verify
                  </Button>
                  <Button size="icon-sm" variant="ghost" aria-label={`Edit ${p.name}`} onClick={() => setEditing(p)}>
                    <PencilIcon aria-hidden="true" />
                  </Button>
                  <Button size="icon-sm" variant="ghost" aria-label={`Delete ${p.name}`} onClick={() => setDeleting(p)}>
                    <Trash2Icon aria-hidden="true" />
                  </Button>
                </div>
              </div>
              {p.lastError && (
                <p role="alert" className="border border-warning/60 bg-warning-soft px-3 py-2 text-xs">
                  {p.lastError}
                </p>
              )}
              {p.zones.length > 0 && (
                <ul className="flex flex-wrap gap-1.5" aria-label={`Zones of ${p.name}`}>
                  {p.zones.slice(0, MAX_ZONES_SHOWN).map((z) => (
                    <li key={z} className="border border-border px-1.5 py-0.5 font-mono text-2xs">
                      {z}
                    </li>
                  ))}
                  {p.zones.length > MAX_ZONES_SHOWN && <li className="px-1.5 py-0.5 font-mono text-2xs text-muted-foreground">+{p.zones.length - MAX_ZONES_SHOWN} more</li>}
                </ul>
              )}
            </li>
          ))}
        </ul>
      )}
      {editing && (
        <ProviderDialog provider={editing === "new" ? null : editing} api={api} onClose={() => setEditing(null)} onSaved={() => void list.refresh()} />
      )}
      {verified && (
        <Dialog open onOpenChange={(o) => !o && setVerified(null)}>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>{verified.provider.name}</DialogTitle>
              <DialogDescription>What Cloudflare said about the token.</DialogDescription>
            </DialogHeader>
            <VerifyResult result={verified} />
            <DialogFooter>
              <Button onClick={() => setVerified(null)}>Close</Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      )}
      {deleting && (
        <ConfirmDialog
          open
          onOpenChange={(o) => !o && setDeleting(null)}
          resourceName={deleting.name}
          title="Delete DNS provider"
          description="Certificates that need it for DNS-01 can no longer be issued, and DNS checks fall back to the public answer. A provider that a domain uses cannot be deleted."
          onConfirm={async (typed) => {
            await api.remove(deleting.id, typed);
            setDeleting(null);
            await list.refresh();
          }}
        />
      )}
    </div>
  );
}
