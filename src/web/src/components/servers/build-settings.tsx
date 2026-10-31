"use client";

import * as React from "react";
import { FormError, SaveButton, fieldError, numOrUndef, useAction } from "@/components/resources/form";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import type { Server } from "@/lib/servers/types";
import { usePolled } from "@/lib/servers/use-polled";
import { NativeSelect } from "./common";

const MIB = 1024 * 1024;
const GIB = 1024 * MIB;

/** Roles in words: what each one means for placement. Roles guide where Aethera puts things; they never move or stop a running workload. */
export const ROLE_HELP: Record<string, string> = {
  worker: "Runs applications (and services without data).",
  storage: "Runs databases and other services with a data volume.",
  build: "Builds images for applications that name it as their build server.",
  master: "The machine the control plane runs on. It takes workloads too, but a server with the right role is preferred.",
  ci: "Not used yet.",
};

/** Memory in the input as a number of MiB (empty = unlimited) to bytes. */
export function mibToBytes(text: string): number | null {
  const n = numOrUndef(text);
  return n === undefined ? null : Math.round(n * MIB);
}

export function bytesToMib(bytes: unknown): string {
  const n = typeof bytes === "number" ? bytes : typeof bytes === "string" ? Number(bytes) : NaN;
  return Number.isFinite(n) && n > 0 ? String(Math.round((n / MIB) * 100) / 100) : "";
}

/**
 * Build settings of a server (ADR 0012): the quota of the BuildKit builder container (CPU and memory; empty = the Docker daemon's own
 * builder, no limit) and the registry images built here for another server are pushed to.
 */
export function BuildSettingsForm({
  server,
  api = serversApi,
  resources = resourcesApi,
  onSaved,
}: {
  server: Server;
  api?: ServersApi;
  resources?: ResourcesApi;
  onSaved: (s: Server) => void;
}) {
  const registries = usePolled((signal) => resources.registries.list({ limit: 100, sort: "name" }, { signal }), "build-registries", { intervalMs: 60_000 });
  const [cpu, setCpu] = React.useState(server.buildCpuLimit ? String(server.buildCpuLimit) : "");
  const [memory, setMemory] = React.useState(bytesToMib(server.buildMemoryLimitBytes));
  const [registryId, setRegistryId] = React.useState(server.buildRegistryId ?? "");
  const act = useAction();
  const items = registries.data?.items ?? [];
  const chosen = items.find((r) => r.id === registryId);

  return (
    <form
      className="grid max-w-2xl gap-4 border border-border bg-card p-5"
      noValidate
      onSubmit={async (e) => {
        e.preventDefault();
        const updated = await act.run(
          () =>
            api.update(server.id, {
              buildCpuLimit: numOrUndef(cpu) ?? null,
              buildMemoryLimitBytes: mibToBytes(memory),
              buildRegistryId: registryId || null,
            }),
          "Build settings saved",
        );
        if (updated) onSaved(updated);
      }}
    >
      <p className="text-sm text-muted-foreground">
        Applications can name this server as their <em>build server</em>: it builds the image, pushes it to the registry below and the server that runs the
        application pulls it. The quota keeps builds from starving what already runs here; it applies on top of the number of parallel builds.
      </p>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Build CPU limit (cores)" error={fieldError(act.fields, "buildCpuLimit")} hint="Empty: no limit.">
          {(c) => <Input {...c} inputMode="decimal" className="font-mono" placeholder="2" value={cpu} onChange={(e) => setCpu(e.target.value)} />}
        </Field>
        <Field label="Build memory limit (MiB)" error={fieldError(act.fields, "buildMemoryLimitBytes")} hint={`Empty: no limit. At least 128; ${GIB / MIB} is 1 GiB.`}>
          {(c) => <Input {...c} inputMode="numeric" className="font-mono" placeholder="2048" value={memory} onChange={(e) => setMemory(e.target.value)} />}
        </Field>
      </div>
      <div className="grid gap-1.5 sm:w-80">
        <label htmlFor="build-registry" className="mono-label text-muted-foreground">
          Build registry
        </label>
        <NativeSelect id="build-registry" value={registryId} onChange={(e) => setRegistryId(e.target.value)}>
          <option value="">None: this server cannot build for another server</option>
          {items.map((r) => (
            <option key={r.id} value={r.id}>
              {r.name} ({r.url})
            </option>
          ))}
        </NativeSelect>
        {fieldError(act.fields, "buildRegistryId") && <p className="text-xs text-danger">{fieldError(act.fields, "buildRegistryId")}</p>}
        <p className="text-xs text-muted-foreground">
          {chosen
            ? chosen.namespace || chosen.username
              ? `Images are pushed to ${chosen.url}/${chosen.namespace ?? chosen.username}/<app>:<deployment>.`
              : "This registry has no namespace and no username: set a namespace in Registries."
            : "Add registries under Registries; GHCR needs a token that can write packages."}
        </p>
      </div>
      <FormError message={act.error} />
      <div>
        <SaveButton pending={act.pending}>Save build settings</SaveButton>
      </div>
    </form>
  );
}
