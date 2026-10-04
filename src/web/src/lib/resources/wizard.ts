import { slugify } from "./status";
import type { ApplicationSourceKind, BuildCandidate, CreateApplicationRequest } from "./types";

/** The "build method" of the wizard: it maps one to one onto the API's `sourceKind`. */
export type BuildMethod = "dockerfile" | "nixpacks" | "static" | "dockerImage" | "compose";

export const BUILD_METHODS: Array<{ value: BuildMethod; label: string; hint: string; fromRepo: boolean }> = [
  { value: "dockerfile", label: "Dockerfile", hint: "Build the Dockerfile in your repository.", fromRepo: true },
  { value: "nixpacks", label: "Nixpacks", hint: "Detect the language and build without a Dockerfile.", fromRepo: true },
  { value: "static", label: "Static site", hint: "Build with Node and serve the output with nginx.", fromRepo: true },
  { value: "dockerImage", label: "Docker image", hint: "Run a prebuilt image from a registry.", fromRepo: false },
  { value: "compose", label: "Docker Compose", hint: "Run a compose file (pasted in).", fromRepo: false },
];

export type EnvRow = { id: number; key: string; value: string; secret: boolean };

export type WizardState = {
  projectId: string;
  environmentId: string;
  name: string;
  slug: string;
  slugTouched: boolean;
  /** The user typed the name: stop suggesting one from the repository or image. */
  nameTouched: boolean;
  method: BuildMethod;
  repoUrl: string;
  branch: string;
  credentialId: string;
  autoDeploy: boolean;
  image: string;
  tag: string;
  registryId: string;
  composeContent: string;
  context: string;
  dockerfilePath: string;
  installCommand: string;
  buildCommand: string;
  startCommand: string;
  outputDirectory: string;
  env: EnvRow[];
  serverId: string;
  port: string;
  healthPath: string;
  hostname: string;
  https: boolean;
};

export const initialWizardState: WizardState = {
  projectId: "",
  environmentId: "",
  name: "",
  slug: "",
  slugTouched: false,
  nameTouched: false,
  method: "dockerfile",
  repoUrl: "",
  branch: "main",
  credentialId: "",
  autoDeploy: true,
  image: "",
  tag: "latest",
  registryId: "",
  composeContent: "",
  context: ".",
  dockerfilePath: "Dockerfile",
  installCommand: "",
  buildCommand: "",
  startCommand: "",
  outputDirectory: "",
  env: [],
  serverId: "",
  port: "3000",
  healthPath: "",
  hostname: "",
  https: true,
};

export const WIZARD_STEPS = [
  { key: "source", title: "Source", description: "repo · project" },
  { key: "build", title: "Build", description: "method" },
  { key: "env", title: "Environment", description: "variables" },
  { key: "server", title: "Server", description: "target · port" },
  { key: "domain", title: "Domain", description: "routing" },
  { key: "deploy", title: "Deploy", description: "review" },
] as const;

export type StepKey = (typeof WIZARD_STEPS)[number]["key"];

export type Errors = Record<string, string>;

export const HOSTNAME = /^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/i;
const ENV_KEY = /^[A-Za-z_][A-Za-z0-9_]*$/;
const IMAGE = /^[A-Za-z0-9][A-Za-z0-9._\-/:@]*$/;

export function isRepoMethod(m: BuildMethod): boolean {
  return m === "dockerfile" || m === "nixpacks" || m === "static";
}

export function providerOf(url: string): "gitHub" | "gitLab" | "generic" {
  if (/github\.com[/:]/i.test(url)) return "gitHub";
  if (/gitlab\.com[/:]/i.test(url)) return "gitLab";
  return "generic";
}

/** Repo name of a URL, used to suggest an application name. */
export function suggestName(url: string): string {
  const m = /[/:]([^/:]+?)(?:\.git)?\/?$/.exec(url.trim());
  return m ? m[1] : "";
}

export function validateStep(step: StepKey, s: WizardState): Errors {
  const e: Errors = {};
  switch (step) {
    case "source":
      if (!s.environmentId) e.environmentId = "Pick a project and an environment.";
      if (!s.name.trim()) e.name = "Give the application a name.";
      if (!s.slug) e.slug = "A slug is required (lowercase letters, digits and hyphens).";
      else if (!/^[a-z0-9]+(-[a-z0-9]+)*$/.test(s.slug) || s.slug.length > 63) e.slug = "Use lowercase letters, digits and single hyphens (max 63).";
      if (isRepoMethod(s.method)) {
        if (!s.repoUrl.trim()) e.repoUrl = "Enter the repository URL.";
        else if (!/^(https?:\/\/|ssh:\/\/|git@)/i.test(s.repoUrl.trim())) e.repoUrl = "Use an https://, ssh:// or git@ URL.";
        if (!s.branch.trim()) e.branch = "Enter a branch.";
      }
      if (s.method === "dockerImage") {
        if (!s.image.trim()) e.image = "Enter the image, e.g. nginx or ghcr.io/org/app.";
        else if (!IMAGE.test(s.image.trim())) e.image = "That is not a valid image name.";
        if (!s.tag.trim()) e.tag = "Enter a tag (latest is allowed).";
      }
      if (s.method === "compose" && !s.composeContent.trim()) e.composeContent = "Paste the compose file.";
      break;
    case "build":
      if (s.method === "dockerfile" && !s.dockerfilePath.trim()) e.dockerfilePath = "Enter the Dockerfile path.";
      if (s.method === "static" && !s.outputDirectory.trim()) e.outputDirectory = "Enter the build output directory (e.g. dist).";
      break;
    case "env": {
      const seen = new Set<string>();
      for (const r of s.env) {
        if (!r.key && !r.value) continue;
        if (!ENV_KEY.test(r.key)) e[`env.${r.id}`] = "Names use letters, digits and underscores and do not start with a digit.";
        else if (seen.has(r.key)) e[`env.${r.id}`] = "Duplicate name.";
        seen.add(r.key);
      }
      break;
    }
    case "server":
      if (!s.serverId) e.serverId = "Pick the server that runs this application.";
      if (s.method !== "compose") {
        const port = Number(s.port);
        if (!Number.isInteger(port) || port < 1 || port > 65535) e.port = "Enter a port between 1 and 65535.";
      }
      if (s.healthPath && !s.healthPath.startsWith("/")) e.healthPath = "The health check path starts with /.";
      break;
    case "domain":
      if (s.hostname.trim() && !HOSTNAME.test(s.hostname.trim())) e.hostname = "That is not a valid hostname.";
      break;
  }
  return e;
}

/** Prefill the build step from a detection candidate (`POST /servers/{id}/build-detect`). */
export function applyCandidate(s: WizardState, c: BuildCandidate): WizardState {
  const method: BuildMethod = c.engine === "nixpacks" ? "nixpacks" : c.engine === "static" ? "static" : "dockerfile";
  const port = Number(c.suggestedPorts?.[0]);
  return {
    ...s,
    method,
    dockerfilePath: c.dockerfilePath ?? s.dockerfilePath,
    installCommand: c.installCommand ?? "",
    buildCommand: c.buildCommand ?? "",
    startCommand: c.startCommand ?? "",
    outputDirectory: c.outputDirectory ?? "",
    port: Number.isInteger(port) && port > 0 ? String(port) : s.port,
  };
}

export function nextSlug(s: WizardState, name: string): Partial<WizardState> {
  return s.slugTouched ? { name } : { name, slug: slugify(name) };
}

/** A name suggested from the repository or image, until the user types their own. */
export function suggestFrom(s: WizardState, text: string): Partial<WizardState> {
  return s.nameTouched || !text ? {} : nextSlug(s, text);
}

const nz = (v: string): string | undefined => (v.trim() ? v.trim() : undefined);

/** The `POST /applications` body for the wizard's answers. */
export function toCreateRequest(s: WizardState): CreateApplicationRequest {
  const kind: ApplicationSourceKind = s.method;
  const body: CreateApplicationRequest = {
    name: s.name.trim(),
    slug: s.slug,
    environmentId: s.environmentId,
    serverId: s.serverId,
    sourceKind: kind,
  };
  if (isRepoMethod(s.method)) {
    body.gitSource = {
      provider: providerOf(s.repoUrl),
      repositoryUrl: s.repoUrl.trim(),
      branch: s.branch.trim(),
      gitCredentialId: nz(s.credentialId) ?? null,
      autoDeploy: s.autoDeploy,
    };
    body.build = {
      engine: s.method,
      context: nz(s.context) ?? ".",
      dockerfilePath: s.method === "dockerfile" ? nz(s.dockerfilePath) : undefined,
      installCommand: nz(s.installCommand),
      buildCommand: nz(s.buildCommand),
      startCommand: nz(s.startCommand),
      outputDirectory: nz(s.outputDirectory),
      cacheEnabled: true,
    };
  }
  if (s.method === "dockerImage") body.image = { image: s.image.trim(), tag: s.tag.trim(), registryId: nz(s.registryId) ?? null, pullPolicy: "ifNotPresent" };
  if (s.method === "compose") body.compose = { inlineContent: s.composeContent };
  if (s.method !== "compose") {
    const health = s.healthPath.trim();
    body.runtime = {
      ports: [{ containerPort: Number(s.port), isHttp: true }],
      healthCheck: health ? { type: "http", path: health, port: Number(s.port) } : { type: "tcp", port: Number(s.port) },
    };
  }
  return body;
}

/** Environment rows that carry a value, as the API will store them. */
export function envToCreate(s: WizardState): Array<{ key: string; value: string; secret: boolean }> {
  return s.env.filter((r) => r.key.trim()).map((r) => ({ key: r.key.trim(), value: r.value, secret: r.secret }));
}

/** Parses `KEY=value` lines (a pasted .env file) into rows; comments and blank lines are skipped. */
export function parseDotenv(text: string): Array<{ key: string; value: string }> {
  const out: Array<{ key: string; value: string }> = [];
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith("#")) continue;
    const m = /^(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$/.exec(line);
    if (!m) continue;
    let v = m[2];
    if ((v.startsWith('"') && v.endsWith('"')) || (v.startsWith("'") && v.endsWith("'"))) v = v.slice(1, -1);
    out.push({ key: m[1], value: v });
  }
  return out;
}
