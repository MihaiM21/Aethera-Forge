"use client";

import * as React from "react";
import { BindScopeSelect, PrivateNetworkNote } from "@/components/mesh/bind-scope";
import { FormError, SaveButton, numOrUndef, useAction } from "@/components/resources/form";
import { SectionTitle } from "@/components/servers/common";
import { Input } from "@/components/ui/input";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import type { Service } from "@/lib/resources/types";
import { parseScope } from "@/lib/mesh/format";
import type { PortBindScope } from "@/lib/mesh/types";
import { toNum } from "@/lib/servers/format";

type Row = { containerPort: number; protocol: "tcp" | "udp"; isHttp: boolean; published: string; scope: PortBindScope };

/**
 * The ports of a service on the server: whether and where each listens. A database that other servers use listens on the private network
 * (ADR 0012); the connection info of the service then shows `<private address>:<port>`.
 */
export function ServicePorts({ service, api = resourcesApi, onSaved }: { service: Service; api?: ResourcesApi; onSaved: () => void }) {
  const [rows, setRows] = React.useState<Row[]>(() =>
    service.runtime.ports.map((p) => ({
      containerPort: toNum(p.containerPort) ?? 0,
      protocol: p.protocol === "udp" ? "udp" : "tcp",
      isHttp: p.isHttp,
      published: p.publishedPort == null ? "" : String(p.publishedPort),
      scope: parseScope(p.bindScope),
    })),
  );
  const act = useAction();
  const update = (i: number, patch: Partial<Row>) => setRows((r) => r.map((x, j) => (j === i ? { ...x, ...patch } : x)));
  if (rows.length === 0) return null;
  return (
    <form
      className="grid gap-4"
      onSubmit={async (e) => {
        e.preventDefault();
        const ports = rows.map((r) => {
          const published = numOrUndef(r.published);
          return { containerPort: r.containerPort, protocol: r.protocol, isHttp: r.isHttp, publishedPort: published, bindScope: published === undefined ? undefined : r.scope };
        });
        if (await act.runOk(() => api.services.update(service.id, { runtime: { ports } }), "Ports saved")) onSaved();
      }}
    >
      <SectionTitle index="02">Ports on the server</SectionTitle>
      <p className="text-xs text-muted-foreground">
        Other containers of the environment always reach the service by its name. Publish a port to reach it from outside, and choose where it listens: on the private network only
        (your other servers, never the internet), on this server only, or on every interface. It applies on the next deployment.
      </p>
      <div className="grid gap-2">
        {rows.map((r, i) => (
          <div key={`${r.containerPort}/${r.protocol}`} className="grid grid-cols-[6rem_7rem_11rem] items-center gap-2">
            <span className="font-mono text-sm">
              {r.containerPort}/{r.protocol}
            </span>
            <Input aria-label={`Published port for ${r.containerPort}`} inputMode="numeric" className="font-mono" placeholder="publish" value={r.published} onChange={(e) => update(i, { published: e.target.value })} />
            <BindScopeSelect aria-label={`Listens on for ${r.containerPort}`} value={r.scope} disabled={!r.published.trim()} onChange={(scope) => update(i, { scope })} />
          </div>
        ))}
      </div>
      <PrivateNetworkNote serverId={service.serverId} anyPrivate={rows.some((r) => r.scope === "private" && r.published.trim() !== "")} />
      <FormError message={act.error} />
      <div>
        <SaveButton pending={act.pending}>Save ports</SaveButton>
      </div>
    </form>
  );
}
