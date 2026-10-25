import { api, type ApiClient } from "@/lib/api/client";
import type {
  BackupDestination,
  BackupPolicy,
  BackupRun,
  DestinationInput,
  DestinationTest,
  Job,
  Page,
  PolicyInput,
  RestoreRequest,
  RunsQuery,
  SystemBackupPolicy,
  WorkloadBackups,
} from "./types";

type Signal = { signal?: AbortSignal };
const enc = encodeURIComponent;

export type WorkloadCollection = "applications" | "services";

/** The backup endpoints (ADR 0010): destinations, policies of workloads and of Aethera itself, runs and restores. */
export function createBackupsApi(client: ApiClient = api) {
  return {
    destinations: {
      list: (o: Signal = {}) => client.get<Page<BackupDestination>>("/backup-destinations", { query: { limit: 100 }, signal: o.signal }),
      create: (body: DestinationInput) => client.post<BackupDestination>("/backup-destinations", body),
      update: (id: string, body: Partial<DestinationInput>) => client.patch<BackupDestination>(`/backup-destinations/${enc(id)}`, body),
      /** Needs the destination name as `?confirm=`; 409 `backup_destination.in_use` while a policy uses it. */
      remove: (id: string, confirm: string) => client.delete<void>(`/backup-destinations/${enc(id)}`, { query: { confirm } }),
      /** Writes, reads and deletes a small object; the result says whether the connection and the credentials work. */
      test: (id: string) => client.post<DestinationTest>(`/backup-destinations/${enc(id)}/test`, {}),
    },
    policies: {
      forWorkload: (collection: WorkloadCollection, id: string, o: Signal = {}) =>
        client.get<WorkloadBackups>(`/${collection}/${enc(id)}/backup-policies`, o),
      put: (collection: WorkloadCollection, id: string, body: PolicyInput) => client.put<BackupPolicy>(`/${collection}/${enc(id)}/backup-policies`, body),
      remove: (id: string) => client.delete<void>(`/backup-policies/${enc(id)}`),
      /** "Back up now": a job. */
      run: (id: string) => client.post<Job>(`/backup-policies/${enc(id)}/run`, {}),
    },
    system: {
      policy: (o: Signal = {}) => client.get<SystemBackupPolicy>("/system/backup-policy", o),
      putPolicy: (body: Omit<PolicyInput, "kind" | "volumeId">) => client.put<SystemBackupPolicy>("/system/backup-policy", body),
    },
    runs: {
      list: (q: RunsQuery = {}, o: Signal = {}) => client.get<Page<BackupRun>>("/backup-runs", { query: { limit: 25, ...q }, signal: o.signal }),
      /** Administrators; a job. "same" overwrites data after a safety backup, "new" makes a new service. */
      restore: (id: string, body: RestoreRequest) => client.post<Job>(`/backup-runs/${enc(id)}/restore`, body),
    },
  };
}

export type BackupsApi = ReturnType<typeof createBackupsApi>;

export const backupsApi: BackupsApi = createBackupsApi();
