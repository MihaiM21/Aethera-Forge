"use client";

import * as React from "react";
import { CheckIcon, KeyRoundIcon, Loader2Icon } from "lucide-react";
import { PageHeader } from "@/components/aethera/page-header";
import { FormError, fieldError, useAction } from "@/components/resources/form";
import { ErrorPanel, NativeSelect } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { navigateTo } from "@/lib/navigate";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { slugify } from "@/lib/resources/status";
import type { ServiceTemplate } from "@/lib/resources/types";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { usePolled } from "@/lib/servers/use-polled";
import { cn } from "@/lib/utils";

const CATEGORY_LABEL: Record<string, string> = { database: "Databases", cache: "Caches", monitoring: "Monitoring", storage: "Storage" };

export function groupTemplates(templates: ServiceTemplate[]): Array<[string, ServiceTemplate[]]> {
  const groups = new Map<string, ServiceTemplate[]>();
  for (const t of templates) groups.set(t.category, [...(groups.get(t.category) ?? []), t]);
  return [...groups.entries()];
}

/** Create a service from a template: version, name, server. Credentials are generated as secrets by the API. */
export function NewServiceView({ api = resourcesApi, servers = serversApi }: { api?: ResourcesApi; servers?: ServersApi }) {
  const templates = usePolled((signal) => api.services.templates({ signal }), "service-templates", { intervalMs: null });
  const projects = usePolled((signal) => api.projects.list({ limit: 100, sort: "name" }, { signal }), "ns-projects", { intervalMs: null });
  const serverList = usePolled((signal) => servers.list({ limit: 100, sort: "name" }, { signal }), "ns-servers", { intervalMs: null });
  const [key, setKey] = React.useState("");
  const [version, setVersion] = React.useState("");
  const [name, setName] = React.useState("");
  const [slug, setSlug] = React.useState("");
  const [touched, setTouched] = React.useState(false);
  const [projectId, setProjectId] = React.useState("");
  const [environmentId, setEnvironmentId] = React.useState("");
  const [serverId, setServerId] = React.useState("");
  const act = useAction();

  const list = templates.data ?? [];
  const chosen = list.find((t) => t.key === key);
  const projectItems = projects.data?.items ?? [];
  const wanted = typeof window === "undefined" ? null : new URLSearchParams(window.location.search).get("project");
  const project = projectItems.find((p) => p.id === (projectId || wanted)) ?? projectItems[0];
  const env = project?.environments.find((e) => e.id === environmentId) ?? project?.environments.find((e) => !e.isProduction) ?? project?.environments[0];
  const server = serverList.data?.items.find((s) => s.id === serverId) ?? serverList.data?.items[0];
  const effVersion = version || chosen?.defaultVersion || "";

  function pick(t: ServiceTemplate) {
    setKey(t.key);
    setVersion("");
    if (!touched && !name) {
      setName(t.name);
      setSlug(slugify(t.name));
    }
  }

  async function create(e: React.FormEvent) {
    e.preventDefault();
    if (!chosen || !env || !server) return;
    const svc = await act.run(
      () => api.services.create({ name: name.trim(), slug: slug || undefined, environmentId: env.id, serverId: server.id, templateKey: chosen.key, version: effVersion }),
      "Service created",
    );
    if (!svc) return;
    const dep = await act.run(() => api.services.deploy(svc.id));
    navigateTo(dep ? `/deployments/${encodeURIComponent(dep.id)}` : `/services/${encodeURIComponent(svc.id)}`);
  }

  return (
    <div className="flex flex-col gap-8">
      <PageHeader eyebrow="Services" title="New service" description="Pick a template. Versions, volumes, health checks and generated credentials come with it." />
      {templates.error && !templates.data && <ErrorPanel error={templates.error} onRetry={() => void templates.refresh()} title="Could not load templates" />}
      {groupTemplates(list).map(([category, items]) => (
        <section key={category} className="flex flex-col gap-3" aria-label={CATEGORY_LABEL[category] ?? category}>
          <h2 className="font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">{CATEGORY_LABEL[category] ?? category}</h2>
          <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
            {items.map((t) => (
              <button
                key={t.key}
                type="button"
                onClick={() => pick(t)}
                aria-pressed={key === t.key}
                className={cn("focus-ring flex flex-col gap-1 border border-border bg-card px-4 py-3 text-left hover:border-border-strong", key === t.key && "border-primary bg-primary-soft")}
              >
                <span className="flex items-center justify-between gap-2 font-medium">
                  {t.name}
                  {key === t.key && <CheckIcon className="size-4 text-lime" aria-hidden="true" />}
                </span>
                <span className="text-xs text-muted-foreground">{t.description}</span>
                <span className="font-mono text-2xs text-muted-foreground">{t.defaultImage}</span>
              </button>
            ))}
          </div>
        </section>
      ))}

      {chosen && (
        <Card className="grid max-w-3xl gap-5 p-5">
          <form onSubmit={create} className="grid gap-5">
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Name" error={fieldError(act.fields, "name")}>
                {(c) => (
                  <Input
                    {...c}
                    value={name}
                    onChange={(e) => {
                      setName(e.target.value);
                      if (!touched) setSlug(slugify(e.target.value));
                    }}
                    required
                  />
                )}
              </Field>
              <Field label="Slug" error={fieldError(act.fields, "slug")} hint="Other apps in the environment reach it by this host name.">
                {(c) => <Input {...c} className="font-mono" value={slug} onChange={(e) => { setSlug(e.target.value); setTouched(true); }} />}
              </Field>
              <Field label="Version">
                {(c) => (
                  <NativeSelect {...c} value={effVersion} onChange={(e) => setVersion(e.target.value)}>
                    {chosen.versions.map((v) => (
                      <option key={v.version} value={v.version}>
                        {v.version}
                        {v.isDefault ? " (default)" : ""}
                      </option>
                    ))}
                  </NativeSelect>
                )}
              </Field>
              <Field label="Server" hint={serverList.data?.items.length === 0 ? "Add a server first." : undefined}>
                {(c) => (
                  <NativeSelect {...c} value={server?.id ?? ""} onChange={(e) => setServerId(e.target.value)}>
                    {(serverList.data?.items ?? []).map((s) => (
                      <option key={s.id} value={s.id}>
                        {s.name} · {s.host}
                      </option>
                    ))}
                  </NativeSelect>
                )}
              </Field>
              <Field label="Project">
                {(c) => (
                  <NativeSelect {...c} value={project?.id ?? ""} onChange={(e) => { setProjectId(e.target.value); setEnvironmentId(""); }}>
                    {projectItems.map((p) => (
                      <option key={p.id} value={p.id}>
                        {p.name}
                      </option>
                    ))}
                  </NativeSelect>
                )}
              </Field>
              <Field label="Environment">
                {(c) => (
                  <NativeSelect {...c} value={env?.id ?? ""} onChange={(e) => setEnvironmentId(e.target.value)}>
                    {(project?.environments ?? []).map((x) => (
                      <option key={x.id} value={x.id}>
                        {x.name}
                      </option>
                    ))}
                  </NativeSelect>
                )}
              </Field>
            </div>

            <div className="grid gap-3 border border-dashed border-border p-4 text-sm">
              <p className="font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">What you get</p>
              <ul className="grid gap-1 text-xs text-muted-foreground">
                <li>
                  Image <span className="font-mono text-foreground">{chosen.versions.find((v) => v.version === effVersion)?.image ?? chosen.defaultImage}</span>
                </li>
                <li>
                  Ports {chosen.ports.map((p) => <Badge key={p.containerPort} tone="outline" className="mr-1">{String(p.containerPort)}{p.isHttp ? " http" : ""}</Badge>)}{" "}
                  — internal only, nothing is published on the server.
                </li>
                <li>Volumes {chosen.volumes.map((v) => v.mountPath).join(", ") || "none"}</li>
                <li>Health check {chosen.healthCheck.type}{chosen.healthCheck.path ? ` ${chosen.healthCheck.path}` : ""}</li>
              </ul>
              {chosen.env.some((e) => e.generate) && (
                <p className="flex items-start gap-2 text-xs">
                  <KeyRoundIcon className="mt-0.5 size-3.5 text-lime" aria-hidden="true" />
                  <span>
                    Credentials are generated and stored as secrets:{" "}
                    <span className="font-mono">{chosen.env.filter((e) => e.generate).map((e) => e.key).join(", ")}</span>. Reveal them later in the Environment tab (administrators).
                  </span>
                </p>
              )}
            </div>
            <FormError message={act.error} />
            <div>
              <Button type="submit" disabled={act.pending || !name.trim() || !env || !server}>
                {act.pending && <Loader2Icon className="animate-spin" aria-hidden="true" />}
                Create and deploy
              </Button>
            </div>
          </form>
        </Card>
      )}
    </div>
  );
}
