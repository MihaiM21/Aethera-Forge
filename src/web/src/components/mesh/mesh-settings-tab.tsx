"use client";

import * as React from "react";
import Link from "next/link";
import { RefreshCwIcon } from "lucide-react";
import { FormError, SaveButton, fieldError, numOrUndef, useAction } from "@/components/resources/form";
import { ErrorPanel, NativeSelect, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { meshApi, type MeshApi } from "@/lib/mesh/api";
import { DEFAULT_CIDR, DEFAULT_LISTEN_PORT, PROVIDER_LABEL, STATUS_LABEL, STATUS_TONE } from "@/lib/mesh/format";
import type { MeshProvider, MeshSettings } from "@/lib/mesh/types";
import { formatAgo, toNum } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

const PROVIDERS: MeshProvider[] = ["none", "wireGuard", "tailscale"];

function SettingsForm({ settings, api, onSaved }: { settings: MeshSettings; api: MeshApi; onSaved: () => void }) {
  const [provider, setProvider] = React.useState<MeshProvider>(settings.provider);
  const [cidr, setCidr] = React.useState(settings.cidr);
  const [port, setPort] = React.useState(String(toNum(settings.listenPort) ?? DEFAULT_LISTEN_PORT));
  const act = useAction();
  const wireGuard = provider === "wireGuard";
  const rangeChanged = settings.provider === "wireGuard" && wireGuard && cidr.trim() !== settings.cidr;
  return (
    <form
      className="grid max-w-3xl gap-5"
      onSubmit={async (e) => {
        e.preventDefault();
        const body = { provider, cidr: cidr.trim() || undefined, listenPort: numOrUndef(port) };
        if (await act.runOk(() => api.saveSettings(body), provider === "none" ? "Private network turned off" : "Private network saved")) onSaved();
      }}
    >
      <Field label="Provider" hint="Off by default. The servers keep working exactly as before until you turn it on.">
        {(c) => (
          <NativeSelect {...c} value={provider} onChange={(e) => setProvider(e.target.value as MeshProvider)}>
            {PROVIDERS.map((p) => (
              <option key={p} value={p}>
                {PROVIDER_LABEL[p]}
              </option>
            ))}
          </NativeSelect>
        )}
      </Field>
      {wireGuard && (
        <>
          <div className="grid gap-5 sm:grid-cols-2">
            <Field label="Range" hint="Every server gets one address from it. A private /16 to /24." error={fieldError(act.fields, "cidr")}>
              {(c) => <Input {...c} className="font-mono" value={cidr} placeholder={DEFAULT_CIDR} onChange={(e) => setCidr(e.target.value)} />}
            </Field>
            <Field label="UDP port" hint="Open it between your servers (see the networking guide)." error={fieldError(act.fields, "listenPort")}>
              {(c) => <Input {...c} className="font-mono" inputMode="numeric" value={port} placeholder={String(DEFAULT_LISTEN_PORT)} onChange={(e) => setPort(e.target.value)} />}
            </Field>
          </div>
          <p className="text-xs text-muted-foreground">
            Aethera creates the interface <span className="font-mono">aethera0</span> on each server. Private keys are generated on the server and never leave it. Every server must have
            the WireGuard kernel module and its agent the <span className="font-mono">NET_ADMIN</span> capability, and the servers must reach each other on the UDP port above.
          </p>
        </>
      )}
      {provider === "tailscale" && (
        <p className="text-xs text-muted-foreground">
          Aethera uses the address of <span className="font-mono">tailscale0</span> on each server and never logs in to Tailscale or changes it: install and connect Tailscale on every server first.
        </p>
      )}
      {rangeChanged && (
        <p role="status" className="border border-dashed border-warning/70 bg-warning-soft px-3 py-2 text-xs">
          A new range gives every server a new address. Applications and services with ports on the private network must be redeployed afterwards, and clients that use the old
          addresses must be updated.
        </p>
      )}
      <FormError message={act.error} />
      <div>
        <SaveButton pending={act.pending} disabled={provider === settings.provider && provider !== "wireGuard"}>
          {provider === "none" ? "Save" : "Save and apply"}
        </SaveButton>
      </div>
    </form>
  );
}

/** Settings > Network: the private mesh between servers (WireGuard or Tailscale), its range and port (R2.2, ADR 0012). */
export function MeshSettingsTab({ api = meshApi, pollMs = 8000 }: { api?: MeshApi; pollMs?: number }) {
  const settings = usePolled((signal) => api.settings({ signal }), "mesh-settings", { intervalMs: pollMs });
  const act = useAction();
  const [now] = React.useState(() => new Date());
  const data = settings.data;
  return (
    <div className="flex flex-col gap-8">
      <section className="flex flex-col gap-4">
        <SectionTitle index="01">Private network</SectionTitle>
        <p className="max-w-3xl text-sm text-muted-foreground">
          An encrypted network between your servers that Aethera sets up itself. A database can then listen on its private address only (choose <em>Private network</em> for a port) and
          be used from the applications on your other servers without ever being open to the internet.
        </p>
        {settings.error && !data && <ErrorPanel error={settings.error} onRetry={() => void settings.refresh()} title="Could not load the private network" />}
        {settings.loading && <Skeleton className="h-32 w-full" />}
        {data && <SettingsForm key={`${data.provider}|${data.cidr}|${String(data.listenPort)}`} settings={data} api={api} onSaved={() => void settings.refresh()} />}
      </section>

      {data && data.provider !== "none" && (
        <section className="flex flex-col gap-4">
          <SectionTitle
            index="02"
            actions={
              <Button
                size="sm"
                variant="outline"
                disabled={act.pending}
                onClick={async () => {
                  if (await act.runOk(() => api.reconcile(), "Applying the private network")) void settings.refresh();
                }}
              >
                <RefreshCwIcon aria-hidden="true" /> Apply again
              </Button>
            }
          >
            Servers
          </SectionTitle>
          <FormError message={act.error} />
          {data.servers.length === 0 && <p className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">No server with an agent yet.</p>}
          {data.servers.length > 0 && (
            <ul className="border border-border bg-card">
              {data.servers.map((s) => (
                <li key={s.id} className="flex flex-col gap-1 border-b border-border px-4 py-3 last:border-b-0">
                  <div className="flex flex-wrap items-center justify-between gap-3">
                    <p className="flex flex-wrap items-center gap-2 font-medium">
                      <Link href={`/servers/${s.id}#network`} className="hover:underline">
                        {s.name}
                      </Link>
                      <Badge tone={STATUS_TONE[s.status]}>{STATUS_LABEL[s.status]}</Badge>
                      {!s.agentConnected && <Badge tone="neutral">agent offline</Badge>}
                    </p>
                    <span className="font-mono text-xs text-muted-foreground">
                      {s.address ?? "no address yet"}
                      {s.handshakeAt ? ` · handshake ${formatAgo(s.handshakeAt, now)}` : ""}
                    </span>
                  </div>
                  {s.error && <p className={`text-xs ${s.status === "failed" ? "text-danger" : "text-muted-foreground"}`}>{s.error}</p>}
                </li>
              ))}
            </ul>
          )}
        </section>
      )}
    </div>
  );
}
