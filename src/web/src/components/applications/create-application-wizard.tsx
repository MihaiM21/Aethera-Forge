"use client";

import * as React from "react";
import Link from "next/link";
import { CheckIcon, Loader2Icon, PlusIcon, SearchCodeIcon, Trash2Icon, UploadIcon } from "lucide-react";
import { PageHeader } from "@/components/aethera/page-header";
import { Stepper } from "@/components/aethera/stepper";
import { ErrorPanel, Facts, NativeSelect } from "@/components/servers/common";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { repoLabel } from "@/lib/resources/status";
import type { BuildCandidate, GitCredential, Project, Registry } from "@/lib/resources/types";
import {
  applyCandidate,
  BUILD_METHODS,
  envToCreate,
  initialWizardState,
  isRepoMethod,
  nextSlug,
  parseDotenv,
  suggestName,
  toCreateRequest,
  validateStep,
  WIZARD_STEPS,
  type Errors,
  type WizardState,
} from "@/lib/resources/wizard";
import { errorMessage } from "@/lib/servers/errors";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import type { Server } from "@/lib/servers/types";
import { cn } from "@/lib/utils";
import { navigateTo } from "@/lib/navigate";

type Progress = { label: string; state: "pending" | "running" | "done" | "failed"; error?: string };

function useLoaded<T>(load: () => Promise<T>, deps: React.DependencyList): { data?: T; error?: unknown; loading: boolean } {
  const [state, setState] = React.useState<{ data?: T; error?: unknown; loading: boolean }>({ loading: true });
  React.useEffect(() => {
    let alive = true;
    load().then(
      (data) => alive && setState({ data, loading: false }),
      (error) => alive && setState({ error, loading: false }),
    );
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
  return state;
}

export function CreateApplicationWizard({ api = resourcesApi, servers = serversApi }: { api?: ResourcesApi; servers?: ServersApi }) {
  const [s, setS] = React.useState<WizardState>(initialWizardState);
  const [step, setStep] = React.useState(0);
  const [errors, setErrors] = React.useState<Errors>({});
  const [progress, setProgress] = React.useState<Progress[] | null>(null);
  const [candidates, setCandidates] = React.useState<BuildCandidate[] | null>(null);
  const [detecting, setDetecting] = React.useState(false);
  const [detectError, setDetectError] = React.useState<string | null>(null);
  const [createdId, setCreatedId] = React.useState<string | null>(null);
  const envId = React.useRef(1);

  const projects = useLoaded(() => api.projects.list({ limit: 100, sort: "name" }), []);
  const serverList = useLoaded(() => servers.list({ limit: 100, sort: "name" }), []);
  const creds = useLoaded(() => api.gitCredentials.list().catch(() => ({ items: [] as GitCredential[], nextCursor: null })), []);
  const registries = useLoaded(() => api.registries.list({ limit: 100 }).catch(() => ({ items: [] as Registry[], nextCursor: null })), []);

  const set = (patch: Partial<WizardState>) => setS((p) => ({ ...p, ...patch }));

  // Defaults once the lists arrive: the project from ?project=, else the first; its first environment; the first server.
  React.useEffect(() => {
    const items = projects.data?.items;
    if (!items?.length) return;
    setS((p) => {
      if (p.projectId) return p;
      const wanted = new URLSearchParams(window.location.search).get("project");
      const project = items.find((x) => x.id === wanted) ?? items[0];
      const env = project.environments.find((e) => !e.isProduction) ?? project.environments[0];
      return { ...p, projectId: project.id, environmentId: env?.id ?? "" };
    });
  }, [projects.data]);
  React.useEffect(() => {
    const items = serverList.data?.items;
    if (!items?.length) return;
    setS((p) => (p.serverId ? p : { ...p, serverId: (items.find((x) => x.status.agent.status === "connected") ?? items[0]).id }));
  }, [serverList.data]);

  const project: Project | undefined = projects.data?.items.find((p) => p.id === s.projectId);
  const key = WIZARD_STEPS[step].key;

  function go(next: number) {
    if (next > step) {
      const e = validateStep(key, s);
      setErrors(e);
      if (Object.keys(e).length > 0) return;
    } else setErrors({});
    setStep(next);
  }

  async function detect() {
    setDetecting(true);
    setDetectError(null);
    setCandidates(null);
    try {
      const res = await api.servers.buildDetect(s.serverId, {
        repositoryUrl: s.repoUrl.trim(),
        branch: s.branch.trim() || undefined,
        gitCredentialId: s.credentialId || undefined,
      });
      setCandidates(res.candidates);
      if (res.candidates[0]) setS((p) => applyCandidate(p, res.candidates[0]));
    } catch (e) {
      setDetectError(errorMessage(e));
    } finally {
      setDetecting(false);
    }
  }

  async function submit() {
    for (const k of ["source", "build", "env", "server", "domain"] as const) {
      const e = validateStep(k, s);
      if (Object.keys(e).length > 0) {
        setErrors(e);
        setStep(WIZARD_STEPS.findIndex((x) => x.key === k));
        return;
      }
    }
    const env = envToCreate(s);
    const steps: Progress[] = [
      { label: "Create the application", state: "pending" },
      ...(env.length > 0 ? [{ label: `Save ${env.length} environment variable${env.length === 1 ? "" : "s"}`, state: "pending" as const }] : []),
      ...(s.hostname.trim() ? [{ label: `Attach ${s.hostname.trim()}`, state: "pending" as const }] : []),
      { label: "Start the deployment", state: "pending" },
    ];
    setProgress(steps);
    const mark = (i: number, patch: Partial<Progress>) => setProgress((p) => p && p.map((x, j) => (j === i ? { ...x, ...patch } : x)));
    let i = 0;
    let appId = createdId;
    try {
      if (!appId) {
        mark(i, { state: "running" });
        appId = (await api.applications.create(toCreateRequest(s))).id;
        setCreatedId(appId);
      }
      mark(i++, { state: "done" });
      if (env.length > 0) {
        mark(i, { state: "running" });
        for (const row of env) {
          if (row.secret) {
            const secret = await api.secrets.create({ name: `${s.slug}_${row.key}`, value: row.value, workloadId: appId });
            await api.envVars.create("applications", appId, { key: row.key, secretId: secret.id, isBuildTime: true, isRuntime: true });
          } else {
            await api.envVars.create("applications", appId, { key: row.key, value: row.value, isBuildTime: true, isRuntime: true });
          }
        }
        mark(i++, { state: "done" });
      }
      if (s.hostname.trim()) {
        mark(i, { state: "running" });
        await api.domains.create("applications", appId, {
          hostname: s.hostname.trim().toLowerCase(),
          httpsEnabled: s.https,
          isPrimary: true,
          targetPort: s.method === "compose" ? undefined : Number(s.port),
        });
        mark(i++, { state: "done" });
      }
      mark(i, { state: "running" });
      const deployment = await api.applications.deploy(appId);
      mark(i, { state: "done" });
      navigateTo(`/deployments/${encodeURIComponent(deployment.id)}`);
    } catch (e) {
      setProgress((p) => p && p.map((x) => (x.state === "running" ? { ...x, state: "failed", error: errorMessage(e) } : x)));
    }
  }

  const busy = progress?.some((p) => p.state === "running") ?? false;
  const err = (k: string) => errors[k] ?? null;

  return (
    <div className="flex flex-col gap-8">
      <PageHeader eyebrow="Applications" title="Create application" description="Pick a source, a build method and a server, then deploy. Nothing runs until the last step." />
      <Stepper steps={WIZARD_STEPS.map((x) => ({ title: x.title, description: x.description }))} current={step} aria-label="Create application steps" />

      {projects.error != null && <ErrorPanel error={projects.error} title="Could not load projects" />}
      {projects.data && projects.data.items.length === 0 && (
        <p role="status" className="border border-dashed border-warning/70 bg-warning-soft px-4 py-3 text-sm">
          Create a project first: an application lives in an environment of a project.{" "}
          <Link href="/projects" className="text-lime underline-offset-4 hover:underline">
            Go to projects
          </Link>
        </p>
      )}

      <Card className="grid max-w-3xl gap-5 p-5">
        {key === "source" && (
          <>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Project" error={err("environmentId")}>
                {(c) => (
                  <NativeSelect
                    {...c}
                    value={s.projectId}
                    onChange={(e) => {
                      const p = projects.data?.items.find((x) => x.id === e.target.value);
                      set({ projectId: e.target.value, environmentId: (p?.environments.find((x) => !x.isProduction) ?? p?.environments[0])?.id ?? "" });
                    }}
                  >
                    {(projects.data?.items ?? []).map((p) => (
                      <option key={p.id} value={p.id}>
                        {p.name}
                      </option>
                    ))}
                  </NativeSelect>
                )}
              </Field>
              <Field label="Environment">
                {(c) => (
                  <NativeSelect {...c} value={s.environmentId} onChange={(e) => set({ environmentId: e.target.value })}>
                    {(project?.environments ?? []).map((x) => (
                      <option key={x.id} value={x.id}>
                        {x.name}
                        {x.isProduction ? " (production)" : ""}
                      </option>
                    ))}
                  </NativeSelect>
                )}
              </Field>
            </div>

            <fieldset className="grid gap-2">
              <legend className="mb-1 font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Where does the code come from?</legend>
              <div className="grid gap-2 sm:grid-cols-2">
                {BUILD_METHODS.map((m) => (
                  <label
                    key={m.value}
                    className={cn("flex cursor-pointer gap-3 border border-border px-3 py-2.5 text-sm", s.method === m.value && "border-primary bg-primary-soft")}
                  >
                    <input type="radio" name="method" className="mt-1" checked={s.method === m.value} onChange={() => set({ method: m.value })} />
                    <span>
                      <span className="block font-medium">{m.label}</span>
                      <span className="block text-xs text-muted-foreground">{m.hint}</span>
                    </span>
                  </label>
                ))}
              </div>
            </fieldset>

            {isRepoMethod(s.method) && (
              <div className="grid gap-4">
                <Field label="Repository URL" error={err("repoUrl")} hint="https://github.com/owner/repo.git, an ssh:// URL or git@host:owner/repo.git">
                  {(c) => (
                    <Input
                      {...c}
                      value={s.repoUrl}
                      placeholder="https://github.com/owner/repo.git"
                      onChange={(e) => {
                        const url = e.target.value;
                        set({ repoUrl: url, ...(s.name ? {} : nextSlug(s, suggestName(url))) });
                      }}
                    />
                  )}
                </Field>
                <div className="grid gap-4 sm:grid-cols-2">
                  <Field label="Branch" error={err("branch")}>
                    {(c) => <Input {...c} value={s.branch} onChange={(e) => set({ branch: e.target.value })} />}
                  </Field>
                  <Field label="Git credential" hint="Only for private repositories. Add credentials under Settings.">
                    {(c) => (
                      <NativeSelect {...c} value={s.credentialId} onChange={(e) => set({ credentialId: e.target.value })}>
                        <option value="">None (public)</option>
                        {(creds.data?.items ?? []).map((g) => (
                          <option key={g.id} value={g.id}>
                            {g.name}
                          </option>
                        ))}
                      </NativeSelect>
                    )}
                  </Field>
                </div>
                <label className="flex items-center gap-3 text-sm">
                  <Switch checked={s.autoDeploy} onCheckedChange={(v) => set({ autoDeploy: v })} aria-label="Deploy on push" />
                  Deploy automatically when this branch is pushed (needs the webhook set up later)
                </label>
              </div>
            )}

            {s.method === "dockerImage" && (
              <div className="grid gap-4 sm:grid-cols-3">
                <Field label="Image" error={err("image")} className="sm:col-span-2">
                  {(c) => <Input {...c} value={s.image} placeholder="nginx" onChange={(e) => set({ image: e.target.value, ...(s.name ? {} : nextSlug(s, e.target.value.split("/").pop() ?? "")) })} />}
                </Field>
                <Field label="Tag" error={err("tag")}>
                  {(c) => <Input {...c} value={s.tag} onChange={(e) => set({ tag: e.target.value })} />}
                </Field>
                <Field label="Registry" className="sm:col-span-3" hint="Leave empty for Docker Hub public images.">
                  {(c) => (
                    <NativeSelect {...c} value={s.registryId} onChange={(e) => set({ registryId: e.target.value })}>
                      <option value="">Public / Docker Hub</option>
                      {(registries.data?.items ?? []).map((r) => (
                        <option key={r.id} value={r.id}>
                          {r.name}
                        </option>
                      ))}
                    </NativeSelect>
                  )}
                </Field>
              </div>
            )}

            {s.method === "compose" && (
              <Field label="Compose file" error={err("composeContent")} hint="Paste compose.yaml. Services that build from a local context are not supported yet.">
                {(c) => <Textarea {...c} rows={10} className="font-mono text-xs" value={s.composeContent} onChange={(e) => set({ composeContent: e.target.value })} />}
              </Field>
            )}

            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="Application name" error={err("name")}>
                {(c) => <Input {...c} value={s.name} onChange={(e) => set(nextSlug(s, e.target.value))} />}
              </Field>
              <Field label="Slug" error={err("slug")} hint="Used in container and image names.">
                {(c) => <Input {...c} className="font-mono" value={s.slug} onChange={(e) => set({ slug: e.target.value, slugTouched: true })} />}
              </Field>
            </div>
          </>
        )}

        {key === "build" && (
          <>
            {isRepoMethod(s.method) ? (
              <>
                <div className="flex flex-wrap items-center justify-between gap-3 border border-dashed border-border px-3 py-2.5 text-sm">
                  <p className="text-muted-foreground">Let the server look at the repository and suggest a build method, commands and a port.</p>
                  <Button variant="outline" onClick={() => void detect()} disabled={detecting || !s.serverId}>
                    {detecting ? <Loader2Icon className="animate-spin" aria-hidden="true" /> : <SearchCodeIcon aria-hidden="true" />} Detect
                  </Button>
                </div>
                {detectError && <ErrorPanel error={new Error(detectError)} title="Detection failed" />}
                {candidates && (
                  <ul className="grid gap-1.5" aria-label="Detected build methods">
                    {candidates.length === 0 && <li className="text-sm text-muted-foreground">Nothing recognisable. Choose a method by hand.</li>}
                    {candidates.map((c) => (
                      <li key={`${c.engine}:${c.language}`}>
                        <button
                          type="button"
                          onClick={() => set(applyCandidate(s, c))}
                          className="focus-ring flex w-full items-center justify-between gap-3 border border-border px-3 py-2 text-left text-sm hover:border-border-strong"
                        >
                          <span>
                            <span className="font-medium">{c.engine}</span> <span className="text-muted-foreground">· {c.language}</span>
                            <span className="block text-xs text-muted-foreground">{c.reason}</span>
                          </span>
                          <span className="font-mono text-2xs text-lime">{Math.round(Number(c.confidence) * 100)}%</span>
                        </button>
                      </li>
                    ))}
                  </ul>
                )}
                <fieldset className="grid gap-2">
                  <legend className="mb-1 font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Build method</legend>
                  <div className="flex flex-wrap gap-2">
                    {BUILD_METHODS.filter((m) => m.fromRepo).map((m) => (
                      <label key={m.value} className={cn("flex cursor-pointer items-center gap-2 border border-border px-3 py-1.5 text-sm", s.method === m.value && "border-primary bg-primary-soft")}>
                        <input type="radio" name="build-method" checked={s.method === m.value} onChange={() => set({ method: m.value })} />
                        {m.label}
                      </label>
                    ))}
                  </div>
                </fieldset>
                <div className="grid gap-4 sm:grid-cols-2">
                  <Field label="Build context" hint="Directory inside the repository.">
                    {(c) => <Input {...c} className="font-mono" value={s.context} onChange={(e) => set({ context: e.target.value })} />}
                  </Field>
                  {s.method === "dockerfile" && (
                    <Field label="Dockerfile path" error={err("dockerfilePath")}>
                      {(c) => <Input {...c} className="font-mono" value={s.dockerfilePath} onChange={(e) => set({ dockerfilePath: e.target.value })} />}
                    </Field>
                  )}
                  {s.method !== "dockerfile" && (
                    <>
                      <Field label="Install command">{(c) => <Input {...c} className="font-mono" value={s.installCommand} onChange={(e) => set({ installCommand: e.target.value })} />}</Field>
                      <Field label="Build command">{(c) => <Input {...c} className="font-mono" value={s.buildCommand} onChange={(e) => set({ buildCommand: e.target.value })} />}</Field>
                    </>
                  )}
                  {s.method === "nixpacks" && <Field label="Start command">{(c) => <Input {...c} className="font-mono" value={s.startCommand} onChange={(e) => set({ startCommand: e.target.value })} />}</Field>}
                  {s.method === "static" && (
                    <Field label="Output directory" error={err("outputDirectory")}>
                      {(c) => <Input {...c} className="font-mono" value={s.outputDirectory} placeholder="dist" onChange={(e) => set({ outputDirectory: e.target.value })} />}
                    </Field>
                  )}
                </div>
              </>
            ) : (
              <p className="text-sm text-muted-foreground" role="status">
                {s.method === "dockerImage" ? "A prebuilt image needs no build: it is pulled and started as is." : "Compose runs the pasted file as is: there is no build step."}
              </p>
            )}
          </>
        )}

        {key === "env" && (
          <>
            <p className="text-sm text-muted-foreground">Variables are available at build time and at runtime. Secret values are stored encrypted and never shown again.</p>
            <div className="grid gap-2">
              {s.env.map((r) => (
                <div key={r.id} className="grid gap-1">
                  <div className="grid grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)_auto_auto] items-center gap-2">
                    <Input aria-label="Variable name" className="font-mono" placeholder="NAME" value={r.key} aria-invalid={errors[`env.${r.id}`] ? true : undefined}
                      onChange={(e) => set({ env: s.env.map((x) => (x.id === r.id ? { ...x, key: e.target.value } : x)) })} />
                    <Input aria-label="Variable value" className="font-mono" type={r.secret ? "password" : "text"} placeholder="value" value={r.value} autoComplete="off"
                      onChange={(e) => set({ env: s.env.map((x) => (x.id === r.id ? { ...x, value: e.target.value } : x)) })} />
                    <label className="flex items-center gap-1.5 font-mono text-2xs text-muted-foreground">
                      <input type="checkbox" checked={r.secret} onChange={(e) => set({ env: s.env.map((x) => (x.id === r.id ? { ...x, secret: e.target.checked } : x)) })} /> secret
                    </label>
                    <Button variant="ghost" size="icon-sm" aria-label={`Remove ${r.key || "variable"}`} onClick={() => set({ env: s.env.filter((x) => x.id !== r.id) })}>
                      <Trash2Icon aria-hidden="true" />
                    </Button>
                  </div>
                  {errors[`env.${r.id}`] && <p className="text-xs text-danger">{errors[`env.${r.id}`]}</p>}
                </div>
              ))}
            </div>
            <div className="flex flex-wrap gap-2">
              <Button variant="outline" onClick={() => set({ env: [...s.env, { id: envId.current++, key: "", value: "", secret: false }] })}>
                <PlusIcon aria-hidden="true" /> Add variable
              </Button>
              <Button
                variant="outline"
                onClick={() => {
                  const text = window.prompt("Paste the contents of a .env file");
                  if (!text) return;
                  const rows = parseDotenv(text).map((r) => ({ id: envId.current++, key: r.key, value: r.value, secret: false }));
                  set({ env: [...s.env.filter((x) => x.key), ...rows] });
                }}
              >
                <UploadIcon aria-hidden="true" /> Paste .env
              </Button>
            </div>
          </>
        )}

        {key === "server" && (
          <>
            <Field label="Server" error={err("serverId")} hint="The machine that builds and runs this application.">
              {(c) => (
                <NativeSelect {...c} value={s.serverId} onChange={(e) => set({ serverId: e.target.value })}>
                  <option value="">Select a server…</option>
                  {(serverList.data?.items ?? []).map((x: Server) => (
                    <option key={x.id} value={x.id}>
                      {x.name} · {x.host} ({x.status.agent.status})
                    </option>
                  ))}
                </NativeSelect>
              )}
            </Field>
            {serverList.data && serverList.data.items.length === 0 && (
              <p role="status" className="border border-dashed border-warning/70 bg-warning-soft px-3 py-2 text-xs">
                No servers yet.{" "}
                <Link className="text-lime underline-offset-4 hover:underline" href="/servers/new">
                  Add one
                </Link>{" "}
                before deploying.
              </p>
            )}
            {s.method !== "compose" && (
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Container port" error={err("port")} hint="The port your application listens on.">
                  {(c) => <Input {...c} inputMode="numeric" className="font-mono" value={s.port} onChange={(e) => set({ port: e.target.value })} />}
                </Field>
                <Field label="Health check path" error={err("healthPath")} hint="Empty = a TCP check on the port.">
                  {(c) => <Input {...c} className="font-mono" placeholder="/health" value={s.healthPath} onChange={(e) => set({ healthPath: e.target.value })} />}
                </Field>
              </div>
            )}
          </>
        )}

        {key === "domain" && (
          <>
            <Field label="Domain" error={err("hostname")} hint="Optional. Point its DNS record at the server, then HTTPS is issued automatically.">
              {(c) => <Input {...c} className="font-mono" placeholder="app.example.com" value={s.hostname} onChange={(e) => set({ hostname: e.target.value })} />}
            </Field>
            <label className="flex items-center gap-3 text-sm">
              <Switch checked={s.https} onCheckedChange={(v) => set({ https: v })} aria-label="HTTPS" disabled={!s.hostname.trim()} />
              Serve over HTTPS (Let&apos;s Encrypt)
            </label>
          </>
        )}

        {key === "deploy" && (
          <>
            <Facts
              rows={[
                ["Project", `${project?.name ?? "-"} / ${project?.environments.find((e) => e.id === s.environmentId)?.name ?? "-"}`],
                ["Application", `${s.name} (${s.slug})`],
                ["Method", BUILD_METHODS.find((m) => m.value === s.method)?.label],
                ["Source", isRepoMethod(s.method) ? `${repoLabel(s.repoUrl)} @ ${s.branch}` : s.method === "dockerImage" ? `${s.image}:${s.tag}` : "compose file"],
                ["Server", serverList.data?.items.find((x) => x.id === s.serverId)?.name],
                ["Port", s.method === "compose" ? "from compose" : s.port],
                ["Variables", String(envToCreate(s).length)],
                ["Domain", s.hostname.trim() ? `${s.https ? "https" : "http"}://${s.hostname.trim()}` : "none"],
              ]}
            />
            {progress && (
              <ol className="grid gap-1.5" aria-label="Progress">
                {progress.map((p) => (
                  <li key={p.label} className="flex items-start gap-2 text-sm">
                    {p.state === "done" ? <CheckIcon className="mt-0.5 size-4 text-success" aria-hidden="true" /> : p.state === "running" ? <Loader2Icon className="mt-0.5 size-4 animate-spin text-lime" aria-hidden="true" /> : <span className="mt-0.5 size-4 text-center text-danger">{p.state === "failed" ? "×" : "·"}</span>}
                    <span className={p.state === "failed" ? "text-danger" : undefined}>
                      {p.label}
                      {p.error && <span className="block text-xs">{p.error}</span>}
                    </span>
                  </li>
                ))}
              </ol>
            )}
            {createdId && progress?.some((p) => p.state === "failed") && (
              <p className="text-xs text-muted-foreground">
                The application exists already.{" "}
                <a className="text-lime hover:underline" href={`/applications/${encodeURIComponent(createdId)}`}>
                  Open it
                </a>{" "}
                or retry to continue.
              </p>
            )}
          </>
        )}

        <div className="flex items-center justify-between border-t border-border pt-4">
          <Button variant="outline" onClick={() => go(step - 1)} disabled={step === 0 || busy}>
            Back
          </Button>
          {step < WIZARD_STEPS.length - 1 ? (
            <Button onClick={() => go(step + 1)}>Continue</Button>
          ) : (
            <Button onClick={() => void submit()} disabled={busy}>
              {busy && <Loader2Icon className="animate-spin" aria-hidden="true" />}
              {createdId ? "Retry" : "Create and deploy"}
            </Button>
          )}
        </div>
      </Card>
    </div>
  );
}
