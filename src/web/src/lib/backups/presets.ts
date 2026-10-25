import type { BackupPolicy, DestinationInput, DestinationKind, PolicyInput } from "./types";

export type DestinationPreset = {
  key: string;
  label: string;
  kind: DestinationKind;
  /** Pre-filled endpoint; a `<...>` part is for the person to replace. */
  endpoint: string;
  region: string;
  forcePathStyle: boolean;
  hint: string;
};

/** Where Aethera backups usually go. Cloudflare R2 is the recommended one (no egress fees). */
export const DESTINATION_PRESETS: readonly DestinationPreset[] = [
  {
    key: "r2",
    label: "Cloudflare R2",
    kind: "s3",
    endpoint: "https://<account id>.r2.cloudflarestorage.com",
    region: "auto",
    forcePathStyle: false,
    hint: "Create an API token with Object Read & Write for the bucket; the account id is on the R2 overview page.",
  },
  {
    key: "b2",
    label: "Backblaze B2",
    kind: "s3",
    endpoint: "https://s3.<region>.backblazeb2.com",
    region: "",
    forcePathStyle: false,
    hint: "Use an application key limited to the bucket. The region is part of the endpoint (for example us-west-004).",
  },
  {
    key: "aws",
    label: "AWS S3",
    kind: "s3",
    endpoint: "https://s3.<region>.amazonaws.com",
    region: "",
    forcePathStyle: false,
    hint: "Give the IAM user s3:PutObject, GetObject and DeleteObject on the bucket. Set the region of the bucket.",
  },
  {
    key: "minio",
    label: "MinIO",
    kind: "s3",
    endpoint: "http://minio:9000",
    region: "",
    forcePathStyle: true,
    hint: "Plain http is accepted for local addresses. Path-style addressing is needed.",
  },
  {
    key: "local",
    label: "Local (on the server)",
    kind: "local",
    endpoint: "",
    region: "",
    forcePathStyle: false,
    hint: "Stored in the agent's own backup directory on the server of each workload. It does not leave the machine: for tests only.",
  },
];

export const presetByKey = (key: string) => DESTINATION_PRESETS.find((p) => p.key === key);

/** True while the endpoint still contains a `<placeholder>` from a preset. */
export const hasPlaceholder = (endpoint: string) => /<[^>]+>/.test(endpoint);

export type SchedulePreset = { key: string; label: string; cron: string };

export const SCHEDULE_PRESETS: readonly SchedulePreset[] = [
  { key: "daily", label: "Daily at 03:00", cron: "0 3 * * *" },
  { key: "6h", label: "Every 6 hours", cron: "0 */6 * * *" },
  { key: "weekly", label: "Weekly, Sunday 03:00", cron: "0 3 * * 0" },
];

/** The preset a cron expression is, or "custom". */
export function schedulePresetOf(cron: string): string {
  return SCHEDULE_PRESETS.find((p) => p.cron === cron.trim())?.key ?? "custom";
}

/** Human wording of a schedule: the preset's label, else the expression itself. */
export function describeSchedule(cron: string, timeZone: string): string {
  const preset = SCHEDULE_PRESETS.find((p) => p.cron === cron.trim());
  return `${preset ? preset.label : cron} (${timeZone})`;
}

export function describeRetention(p: Pick<BackupPolicy, "keepDaily" | "keepWeekly" | "keepMonthly">): string {
  return `${p.keepDaily} daily · ${p.keepWeekly} weekly · ${p.keepMonthly} monthly`;
}

export const DEFAULT_POLICY: Omit<PolicyInput, "destinationId"> = {
  enabled: true,
  schedule: "0 3 * * *",
  timeZone: "UTC",
  keepDaily: 7,
  keepWeekly: 4,
  keepMonthly: 6,
};

/** The form values of a policy (or the defaults for a new one). */
export type PolicyDraft = {
  enabled: boolean;
  destinationId: string;
  schedule: string;
  timeZone: string;
  keepDaily: string;
  keepWeekly: string;
  keepMonthly: string;
};

export function draftOf(policy: BackupPolicy | null, defaultDestinationId: string, defaults: Omit<PolicyInput, "destinationId"> = DEFAULT_POLICY): PolicyDraft {
  const source = policy ?? { ...defaults, destinationId: defaultDestinationId };
  return {
    enabled: source.enabled,
    destinationId: source.destinationId,
    schedule: source.schedule,
    timeZone: source.timeZone,
    keepDaily: String(source.keepDaily),
    keepWeekly: String(source.keepWeekly),
    keepMonthly: String(source.keepMonthly),
  };
}

const count = (text: string) => {
  const n = Number(text.trim());
  return text.trim() !== "" && Number.isInteger(n) ? n : NaN;
};

/** Client-side check of what is obvious; the API validates everything (cron, zone, frequency). Returns a message or null. */
export function draftProblem(d: PolicyDraft): string | null {
  if (!d.destinationId) return "Choose a destination.";
  if (!d.schedule.trim()) return "Enter a schedule.";
  const counts = [count(d.keepDaily), count(d.keepWeekly), count(d.keepMonthly)];
  if (counts.some((n) => !Number.isFinite(n) || n < 0)) return "Retention counts must be whole numbers, zero or more.";
  if (counts.reduce((a, b) => a + b, 0) < 1) return "Keep at least one backup.";
  return null;
}

export function inputOf(d: PolicyDraft): Omit<PolicyInput, "kind" | "volumeId"> {
  return {
    destinationId: d.destinationId,
    enabled: d.enabled,
    schedule: d.schedule.trim(),
    timeZone: d.timeZone.trim() || "UTC",
    keepDaily: count(d.keepDaily),
    keepWeekly: count(d.keepWeekly),
    keepMonthly: count(d.keepMonthly),
  };
}

/** The body of a create or update of a destination from the dialog's fields. */
export type DestinationDraft = {
  presetKey: string;
  name: string;
  endpoint: string;
  region: string;
  bucket: string;
  pathPrefix: string;
  accessKeyId: string;
  secretAccessKey: string;
  forcePathStyle: boolean;
  isDefault: boolean;
};

export function destinationInput(d: DestinationDraft, kind: DestinationKind, editing: boolean): DestinationInput {
  if (kind === "local") return { name: d.name.trim(), kind: "local", isDefault: d.isDefault };
  return {
    name: d.name.trim(),
    ...(editing ? {} : { kind: "s3" as const }),
    endpoint: d.endpoint.trim(),
    region: d.region.trim() || null,
    bucket: d.bucket.trim(),
    pathPrefix: d.pathPrefix.trim().replace(/^\/+|\/+$/g, "") || null,
    accessKeyId: d.accessKeyId.trim(),
    ...(d.secretAccessKey ? { secretAccessKey: d.secretAccessKey } : {}),
    forcePathStyle: d.forcePathStyle,
    isDefault: d.isDefault,
  };
}

/** The command that shows the master key on the server (what backups are encrypted with). */
export const SHOW_MASTER_KEY_COMMAND = "sudo grep '^AETHERA_MASTER_KEY=' /opt/aethera/.env";
