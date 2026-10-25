import type { BackupsApi } from "@/lib/backups/api";
import type { BackupDestination, BackupPolicy, BackupRun, BackupTarget } from "@/lib/backups/types";

export const NOW = "2026-10-23T10:00:00.000Z";

export const makeDestination = (over: Partial<BackupDestination> = {}): BackupDestination => ({
  id: "dest-1",
  name: "Turn One R2",
  kind: "s3",
  endpoint: "https://abc123.r2.cloudflarestorage.com",
  region: "auto",
  bucket: "turn-one-backups",
  pathPrefix: "aethera",
  accessKeyId: "AKIA",
  hasSecret: true,
  forcePathStyle: false,
  isDefault: true,
  lastTestedAt: null,
  lastTestError: null,
  createdAt: NOW,
  updatedAt: NOW,
  ...over,
});

export const makePolicy = (over: Partial<BackupPolicy> = {}): BackupPolicy => ({
  id: "pol-1",
  workloadId: "svc-1",
  kind: "database",
  volumeId: null,
  volumeName: null,
  destinationId: "dest-1",
  destinationName: "Turn One R2",
  enabled: true,
  schedule: "0 3 * * *",
  timeZone: "UTC",
  keepDaily: 7,
  keepWeekly: 4,
  keepMonthly: 6,
  nextRunAt: "2026-10-24T03:00:00.000Z",
  lastRunAt: NOW,
  nextRuns: ["2026-10-24T03:00:00.000Z", "2026-10-25T03:00:00.000Z", "2026-10-26T03:00:00.000Z"],
  lastStatus: "succeeded",
  lastSuccessAt: NOW,
  createdAt: NOW,
  updatedAt: NOW,
  ...over,
});

export const databaseTarget: BackupTarget = { kind: "database", volumeId: null, name: "postgres", mountPath: null, supported: true, engine: "postgres", reason: null };
export const volumeTarget: BackupTarget = { kind: "volume", volumeId: "vol-1", name: "db-1234-data", mountPath: "/var/lib/postgresql/data", supported: true, engine: "volume", reason: null };

export const makeRun = (over: Partial<BackupRun> = {}): BackupRun => ({
  id: "run-1",
  policyId: "pol-1",
  workloadId: "svc-1",
  destinationId: "dest-1",
  kind: "database",
  trigger: "scheduled",
  status: "succeeded",
  jobId: "job-1",
  engine: "postgres",
  objectKey: "aethera/x/db/database/20261023T030000Z-abcd1234.pgdump.aeb",
  rawSizeBytes: 1_000_000,
  storedSizeBytes: 1_500_000,
  storedSha256: "a".repeat(64),
  keyId: "mk1-0a1b2c3d",
  error: null,
  createdAt: NOW,
  startedAt: NOW,
  finishedAt: NOW,
  durationMs: 4200,
  prunedAt: null,
  restorable: true,
  ...over,
});

/** A partial backups API: the test supplies the calls it expects, anything else throws. */
export function fakeBackups(partial: { destinations?: Record<string, unknown>; policies?: Record<string, unknown>; system?: Record<string, unknown>; runs?: Record<string, unknown> }): BackupsApi {
  const group = (name: string, calls: Record<string, unknown> = {}) =>
    new Proxy(calls, {
      get(target, key: string) {
        if (key in target) return target[key];
        return () => {
          throw new Error(`Unexpected API call: ${name}.${key}`);
        };
      },
    });
  return {
    destinations: group("destinations", partial.destinations),
    policies: group("policies", partial.policies),
    system: group("system", partial.system),
    runs: group("runs", partial.runs),
  } as unknown as BackupsApi;
}

export const job = { id: "job-9", type: "backup.restore", status: "queued" };
