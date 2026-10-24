// The backup API (ADR 0010). Shapes follow openapi/aethera.v1.json (BackupDestinationResponse, BackupPolicyResponse, BackupRunResponse...).
import type { Job } from "@/lib/resources/types";

export type { Job };

export type DestinationKind = "s3" | "local";

export type BackupDestination = {
  id: string;
  name: string;
  kind: DestinationKind;
  endpoint: string | null;
  region: string | null;
  bucket: string | null;
  pathPrefix: string | null;
  accessKeyId: string | null;
  hasSecret: boolean;
  forcePathStyle: boolean;
  isDefault: boolean;
  lastTestedAt: string | null;
  lastTestError: string | null;
  createdAt: string;
  updatedAt: string;
};

export type DestinationInput = {
  name: string;
  kind?: DestinationKind;
  endpoint?: string;
  region?: string | null;
  bucket?: string;
  pathPrefix?: string | null;
  accessKeyId?: string;
  /** Write-only. Omitted on an update = the stored key stays. */
  secretAccessKey?: string;
  forcePathStyle?: boolean;
  isDefault?: boolean;
};

export type DestinationTest = { ok: boolean; error: string | null; testedAt: string };

export type BackupKind = "database" | "volume" | "controlPlane";
export type RunStatus = "pending" | "running" | "succeeded" | "failed" | "cancelled";
export type RunTrigger = "scheduled" | "manual" | "preRestore";

export type BackupPolicy = {
  id: string;
  workloadId: string | null;
  kind: BackupKind;
  volumeId: string | null;
  volumeName: string | null;
  destinationId: string;
  destinationName: string;
  enabled: boolean;
  schedule: string;
  timeZone: string;
  keepDaily: number;
  keepWeekly: number;
  keepMonthly: number;
  nextRunAt: string | null;
  lastRunAt: string | null;
  /** The next three occurrences, computed by the API. */
  nextRuns: string[];
  lastStatus: RunStatus | null;
  lastSuccessAt: string | null;
  createdAt: string;
  updatedAt: string;
};

export type BackupTarget = {
  kind: BackupKind;
  volumeId: string | null;
  name: string;
  mountPath: string | null;
  supported: boolean;
  engine: string | null;
  reason: string | null;
};

export type WorkloadBackups = { policies: BackupPolicy[]; targets: BackupTarget[] };

export type SystemBackupPolicy = { policy: BackupPolicy | null; defaultSchedule: string };

/** What the policy form sends (PUT). */
export type PolicyInput = {
  kind?: "database" | "volume";
  volumeId?: string;
  destinationId: string;
  enabled: boolean;
  schedule: string;
  timeZone: string;
  keepDaily: number;
  keepWeekly: number;
  keepMonthly: number;
};

export type BackupRun = {
  id: string;
  policyId: string;
  workloadId: string | null;
  destinationId: string;
  kind: BackupKind;
  trigger: RunTrigger;
  status: RunStatus;
  jobId: string | null;
  engine: string | null;
  objectKey: string | null;
  rawSizeBytes: number | null;
  storedSizeBytes: number | null;
  storedSha256: string | null;
  keyId: string | null;
  error: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  durationMs: number | null;
  prunedAt: string | null;
  restorable: boolean;
};

export type RestoreRequest = { target: "same" | "new"; confirm: string; newServiceName?: string };

export type Page<T> = { items: T[]; nextCursor: string | null };

export type RunsQuery = { workloadId?: string; policyId?: string; status?: string; limit?: number; cursor?: string };
