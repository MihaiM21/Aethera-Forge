import { api, type ApiClient } from "@/lib/api/client";
import type {
  CreateTaskRequest,
  RunTaskResponse,
  SchedulePreview,
  ScheduledTask,
  ScheduledTaskRun,
  TaskLogPage,
  TaskPage,
  TaskRunStatus,
  UpdateTaskRequest,
} from "./types";

type Signal = { signal?: AbortSignal };

const enc = encodeURIComponent;

/** Typed wrapper over the scheduled-task endpoints. Paths follow the OpenAPI document; the UI has no behaviour the API lacks. */
export function createTasksApi(client: ApiClient) {
  const workload = (kind: "applications" | "services", id: string) => `/${kind}/${enc(id)}/scheduled-tasks`;
  return {
    list: (kind: "applications" | "services", id: string, o: Signal = {}) =>
      client.get<TaskPage<ScheduledTask>>(workload(kind, id), { query: { limit: 200 }, signal: o.signal }),
    create: (kind: "applications" | "services", id: string, body: CreateTaskRequest) => client.post<ScheduledTask>(workload(kind, id), body),
    update: (taskId: string, body: UpdateTaskRequest) => client.patch<ScheduledTask>(`/scheduled-tasks/${enc(taskId)}`, body),
    remove: (taskId: string) => client.delete<void>(`/scheduled-tasks/${enc(taskId)}`),
    /** 202 with the run and its job; 409 `scheduled_task.already_running` while the previous run is still going. */
    run: (taskId: string) => client.post<RunTaskResponse>(`/scheduled-tasks/${enc(taskId)}/run`, {}),
    runs: (taskId: string, q: { limit?: number; cursor?: string; status?: TaskRunStatus } = {}, o: Signal = {}) =>
      client.get<TaskPage<ScheduledTaskRun>>(`/scheduled-tasks/${enc(taskId)}/runs`, { query: { limit: q.limit, cursor: q.cursor, status: q.status }, signal: o.signal }),
    getRun: (runId: string, o: Signal = {}) => client.get<ScheduledTaskRun>(`/scheduled-task-runs/${enc(runId)}`, o),
    /** The output of a run, paged by sequence; poll with `fromSequence = nextSequence` until `ended`. */
    runLogs: (runId: string, q: { fromSequence?: number; limit?: number } = {}, o: Signal = {}) =>
      client.get<TaskLogPage>(`/scheduled-task-runs/${enc(runId)}/logs`, { query: { fromSequence: q.fromSequence, limit: q.limit }, signal: o.signal }),
    /** The next three occurrences of a schedule; 422 with a field error for an invalid cron expression or time zone. */
    preview: (schedule: string, timeZone: string, o: Signal = {}) =>
      client.get<SchedulePreview>("/scheduled-tasks/preview", { query: { schedule, timeZone }, signal: o.signal }),
    /**
     * Services of a compose application, for the service picker. `POST /applications/{id}/compose/inspect` is added by the compose-from-Git
     * work: until the API has it (404/405) or the agent cannot answer, this returns `null` and the form asks for a name instead.
     */
    composeServices: async (applicationId: string, o: Signal = {}): Promise<string[] | null> => {
      try {
        const result = await client.post<{ services?: Array<{ name: string }> }>(`/applications/${enc(applicationId)}/compose/inspect`, {}, o);
        const names = (result.services ?? []).map((s) => s.name).filter(Boolean);
        return names.length > 0 ? names : null;
      } catch {
        return null;
      }
    },
  };
}

export type TasksApi = ReturnType<typeof createTasksApi>;

export const tasksApi: TasksApi = createTasksApi(api);
