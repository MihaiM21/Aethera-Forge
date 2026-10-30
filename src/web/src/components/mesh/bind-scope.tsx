"use client";

import * as React from "react";
import { NativeSelect } from "@/components/servers/common";
import { meshApi, type MeshApi } from "@/lib/mesh/api";
import { SCOPES, SCOPE_HINT, SCOPE_LABEL } from "@/lib/mesh/format";
import type { PortBindScope } from "@/lib/mesh/types";

/** Where a published port listens: every interface, the private network or this server only (ADR 0012). */
export function BindScopeSelect({
  value,
  onChange,
  disabled,
  ...props
}: { value: PortBindScope; onChange: (v: PortBindScope) => void; disabled?: boolean } & Omit<React.ComponentProps<"select">, "value" | "onChange">) {
  return (
    <NativeSelect aria-label="Listens on" title={SCOPE_HINT[value]} {...props} value={value} disabled={disabled} onChange={(e) => onChange(e.target.value as PortBindScope)}>
      {SCOPES.map((s) => (
        <option key={s} value={s}>
          {SCOPE_LABEL[s]}
        </option>
      ))}
    </NativeSelect>
  );
}

/**
 * Warns when a port is set to the private network but the server has no address in it: the deployment would fail at the Network step. Shown
 * only while a port uses that scope. The address comes from the API (`GET /servers/{id}/mesh`).
 */
export function PrivateNetworkNote({ serverId, anyPrivate, api = meshApi }: { serverId: string; anyPrivate: boolean; api?: MeshApi }) {
  const [address, setAddress] = React.useState<string | null | undefined>(undefined);
  React.useEffect(() => {
    if (!anyPrivate) return;
    const controller = new AbortController();
    api
      .server(serverId, { signal: controller.signal })
      .then((m) => setAddress(m.address))
      .catch(() => undefined);
    return () => controller.abort();
  }, [api, serverId, anyPrivate]);
  if (!anyPrivate) return null;
  return address === null ? (
    <p role="status" className="max-w-3xl border border-dashed border-warning/70 bg-warning-soft px-3 py-2 text-xs">
      This server has no address in the private network yet, so a deployment with a private port fails. Turn the private network on in Settings &gt; Network first (and wait until the
      server shows an address), or publish the port publicly.
    </p>
  ) : (
    <p className="max-w-3xl text-xs text-muted-foreground">
      A private port listens on {address ? <span className="font-mono">{address}</span> : "the server's address in the private network"}: reachable from your other servers, never from the
      internet. It applies on the next deployment.
    </p>
  );
}
