"use client";

import * as React from "react";
import { FormError, SaveButton, fieldError, numOrUndef, useAction } from "@/components/resources/form";
import { CopyButton, ErrorPanel, Facts, SectionTitle } from "@/components/servers/common";
import { isAdminRole } from "@/components/ops/settings-view";
import { Badge } from "@/components/ui/badge";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useAuth } from "@/lib/auth/auth-context";
import { meshApi, type MeshApi } from "@/lib/mesh/api";
import { PROVIDER_LABEL, STATUS_LABEL, STATUS_TONE } from "@/lib/mesh/format";
import type { ServerMesh } from "@/lib/mesh/types";
import { formatAgo, formatBytes, toNum } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

function OverrideForm({ serverId, mesh, api, onSaved }: { serverId: string; mesh: ServerMesh; api: MeshApi; onSaved: () => void }) {
  const [endpoint, setEndpoint] = React.useState(mesh.endpoint ?? "");
  const [port, setPort] = React.useState(mesh.listenPort == null ? "" : String(toNum(mesh.listenPort)));
  const act = useAction();
  return (
    <form
      className="grid max-w-2xl gap-4"
      onSubmit={async (e) => {
        e.preventDefault();
        const body = { endpoint: endpoint.trim() || null, listenPort: numOrUndef(port) ?? null };
        if (await act.runOk(() => api.saveServer(serverId, body), "Saved")) onSaved();
      }}
    >
      <div className="grid gap-4 sm:grid-cols-2">
        <Field
          label="Endpoint"
          hint={`Where the other servers dial this one. Empty: ${mesh.effectiveEndpoint ?? "nothing (it dials them)"}.`}
          error={fieldError(act.fields, "endpoint")}
        >
          {(c) => <Input {...c} className="font-mono" placeholder="203.0.113.7:51820" value={endpoint} onChange={(e) => setEndpoint(e.target.value)} />}
        </Field>
        <Field label="UDP port" hint={`Empty: the port of the instance (${String(toNum(mesh.effectiveListenPort))}).`} error={fieldError(act.fields, "listenPort")}>
          {(c) => <Input {...c} className="font-mono" inputMode="numeric" value={port} onChange={(e) => setPort(e.target.value)} />}
        </Field>
      </div>
      <FormError message={act.error} />
      <div>
        <SaveButton pending={act.pending}>Save</SaveButton>
      </div>
    </form>
  );
}

/** Server > Network: this server's mesh address, key, endpoint and peers with their last handshake (R2.2, ADR 0012). */
export function ServerNetworkTab({ serverId, api = meshApi, pollMs = 8000 }: { serverId: string; api?: MeshApi; pollMs?: number }) {
  const { me } = useAuth();
  const admin = isAdminRole(me?.role);
  const mesh = usePolled((signal) => api.server(serverId, { signal }), `server-mesh:${serverId}`, { intervalMs: pollMs });
  const [now] = React.useState(() => new Date());
  const m = mesh.data;
  return (
    <div className="flex flex-col gap-8" data-server-id={serverId}>
      {mesh.error && !m && <ErrorPanel error={mesh.error} onRetry={() => void mesh.refresh()} title="Could not load the private network of this server" />}
      {mesh.loading && <Skeleton className="h-32 w-full" />}
      {m && m.instanceProvider === "none" && m.status === "disabled" && (
        <p className="border border-dashed border-border px-4 py-6 text-sm text-muted-foreground">
          The private network is off. An administrator turns it on in Settings &gt; Network; this server then gets an address and connects to the others.
        </p>
      )}
      {m && (m.instanceProvider !== "none" || m.status !== "disabled") && (
        <>
          <section className="flex flex-col gap-3">
            <SectionTitle index="01">This server</SectionTitle>
            <div className="flex flex-wrap items-center gap-2">
              <Badge tone={STATUS_TONE[m.status]}>{STATUS_LABEL[m.status]}</Badge>
              <span className="text-xs text-muted-foreground">{PROVIDER_LABEL[m.provider === "none" ? m.instanceProvider : m.provider]}</span>
              {!m.agentConnected && <Badge tone="neutral">agent offline</Badge>}
            </div>
            {m.error && (
              <p role={m.status === "failed" ? "alert" : "status"} className={`border px-3 py-2 text-xs ${m.status === "failed" ? "border-danger bg-danger-soft text-danger" : "border-warning/70 bg-warning-soft"}`}>
                {m.error}
              </p>
            )}
            <Facts
              rows={[
                ["Address", m.address ? <span key="a" className="inline-flex items-center gap-2">{m.address}<CopyButton text={m.address} label="Copy" /></span> : "not assigned yet"],
                ["Public key", m.publicKey ?? (m.provider === "tailscale" ? "managed by Tailscale" : "generated when the agent first applies it")],
                ["Endpoint", m.effectiveEndpoint ?? "none: the other servers wait for this one to dial in"],
                ["UDP port", String(toNum(m.effectiveListenPort))],
                ["Last handshake", m.handshakeAt ? formatAgo(m.handshakeAt, now) : "-"],
              ]}
            />
          </section>

          {m.provider === "wireGuard" || m.instanceProvider === "wireGuard" ? (
            <section className="flex flex-col gap-3">
              <SectionTitle index="02">Peers</SectionTitle>
              {m.peers.length === 0 && <p className="text-sm text-muted-foreground">No other server has joined the network yet.</p>}
              {m.peers.length > 0 && (
                <>
                  {!m.peersLive && <p className="font-mono text-2xs text-muted-foreground">The agent could not be asked: handshakes are not shown.</p>}
                  <ul className="border border-border bg-card">
                    {m.peers.map((p) => (
                      <li key={p.publicKey} className="grid gap-1 border-b border-border px-4 py-3 last:border-b-0 sm:grid-cols-[minmax(0,1fr)_auto]">
                        <div className="min-w-0">
                          <p className="font-medium">{p.name ?? "unknown peer"}</p>
                          <p className="truncate font-mono text-2xs text-muted-foreground">
                            {p.address ?? "-"} · {p.endpoint ?? "no endpoint"} · {p.publicKey.slice(0, 12)}...
                          </p>
                        </div>
                        <p className="font-mono text-2xs text-muted-foreground">
                          {p.lastHandshake ? `handshake ${formatAgo(p.lastHandshake, now)}` : "no handshake"}
                          {m.peersLive ? ` · rx ${formatBytes(p.rxBytes)} · tx ${formatBytes(p.txBytes)}` : ""}
                        </p>
                      </li>
                    ))}
                  </ul>
                </>
              )}
            </section>
          ) : null}

          {admin && m.instanceProvider === "wireGuard" && (
            <section className="flex flex-col gap-3">
              <SectionTitle index="03">Reachability</SectionTitle>
              <p className="max-w-2xl text-xs text-muted-foreground">
                Peers dial this server at its public IP and the UDP port above. Change them when the server sits behind NAT or a different port is forwarded to it.
              </p>
              <OverrideForm key={`${m.endpoint}|${String(m.listenPort)}`} serverId={serverId} mesh={m} api={api} onSaved={() => void mesh.refresh()} />
            </section>
          )}
        </>
      )}
    </div>
  );
}
