import type { StatusKey } from "@/components/ui/status-pill";
import { toDate, toNum } from "@/lib/servers/format";
import type { Deployment, DeploymentStatus, DeploymentStep, WorkloadStatus } from "./types";

type Pill = { status: StatusKey; label?: string };

/** `WorkloadStatus` of an application or service -> status pill (docs/design/tokens.md section 2.3). */
export function workloadPill(status: WorkloadStatus): Pill {
  switch (status) {
    case "running":
      return { status: "running" };
    case "deploying":
      return { status: "deploying" };
    case "unhealthy":
      return { status: "unhealthy" };
    case "stopped":
      return { status: "stopped" };
    case "failed":
      return { status: "failed" };
    case "notDeployed":
      return { status: "queued", label: "Not deployed" };
    default:
      return { status: "queued", label: "Unknown" };
  }
}

export function deploymentPill(status: DeploymentStatus): Pill {
  switch (status) {
    case "queued":
      return { status: "queued" };
    case "inProgress":
      return { status: "deploying", label: "In progress" };
    case "running":
      return { status: "running" };
    case "superseded":
      return { status: "stopped", label: "Superseded" };
    case "stopped":
      return { status: "stopped" };
    case "failed":
      return { status: "failed" };
    default:
      return { status: "cancelled" };
  }
}

export const STEP_LABEL: Record<NonNullable<DeploymentStep>, string> = {
  source: "Source",
  build: "Build",
  image: "Image",
  targetServer: "Target server",
  container: "Container",
  network: "Network",
  domain: "Domain",
  healthCheck: "Health check",
  running: "Running",
};

export function stepLabel(step: DeploymentStep): string {
  return step ? STEP_LABEL[step] : "-";
}

export const TRIGGER_LABEL: Record<string, string> = {
  manual: "Manual",
  webhook: "Webhook",
  redeploy: "Redeploy",
  rollback: "Rollback",
  schedule: "Schedule",
  api: "API",
};

/** `1m 23s`, `42s`, `850ms`; `-` without a duration. */
export function formatDuration(ms: unknown): string {
  const n = toNum(ms);
  if (n === null) return "-";
  if (n < 1000) return `${Math.round(n)}ms`;
  const s = Math.round(n / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${String(s % 60).padStart(2, "0")}s`;
  return `${Math.floor(m / 60)}h ${String(m % 60).padStart(2, "0")}m`;
}

/** Live duration of a deployment that is still running (`durationMs` is only set when it finished). */
export function deploymentDuration(d: Pick<Deployment, "durationMs" | "startedAt" | "finishedAt">, now: Date = new Date()): string {
  if (toNum(d.durationMs) !== null) return formatDuration(d.durationMs);
  const start = toDate(d.startedAt);
  return start && !toDate(d.finishedAt) ? formatDuration(now.getTime() - start.getTime()) : "-";
}

const ACTIVE: ReadonlySet<DeploymentStatus> = new Set(["queued", "inProgress"]);

/** True while the deployment can still change; the page polls only then. */
export function isDeploymentActive(status: DeploymentStatus): boolean {
  return ACTIVE.has(status);
}

export function shortSha(sha: string | null | undefined): string {
  return sha ? sha.slice(0, 7) : "-";
}

/** `owner/repo` from a repository URL, or the URL itself when it does not look like one. */
export function repoLabel(url: string | null | undefined): string {
  if (!url) return "-";
  const m = /[/:]([^/:]+\/[^/]+?)(?:\.git)?\/?$/.exec(url);
  return m ? m[1] : url;
}

/** Lower-case slug the API accepts: letters, digits and single hyphens, 1-63 characters. */
export function slugify(name: string): string {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 63);
}
