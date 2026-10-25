/**
 * Types of the scheduled-task endpoints (R1.3, ADR 0010). The OpenAPI generator types `DateTimeOffset` as `unknown`, so the response
 * types are narrowed here; run `pnpm gen:api` after an API change and keep these in step with `ScheduledTaskResponse`,
 * `ScheduledTaskRunResponse` and `RunScheduledTaskResponse`.
 */
import type { components } from "@/lib/api/schema";
import type { Job } from "@/lib/resources/types";

type S = components["schemas"];

export type TaskRunStatus = S["ScheduledTaskRunStatus"];
export type TaskTrigger = S["ScheduledTaskTrigger"];
export type CreateTaskRequest = S["CreateScheduledTaskRequest"];
export type UpdateTaskRequest = S["UpdateScheduledTaskRequest"];

export type TaskRunSummary = {
  id: string;
  trigger: TaskTrigger;
  status: TaskRunStatus;
  exitCode: number | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  durationMs: number | null;
  jobId: string | null;
};

export type ScheduledTask = {
  id: string;
  workloadId: string;
  workloadKind: "application" | "service";
  workloadName: string;
  name: string;
  command: string;
  composeService: string | null;
  schedule: string;
  timeZone: string;
  timeoutSeconds: number;
  enabled: boolean;
  nextRunAt: string | null;
  lastRunAt: string | null;
  /** The next three occurrences of the schedule (also for a disabled task). */
  nextRuns: string[];
  lastRun: TaskRunSummary | null;
  createdAt: string;
  updatedAt: string;
};

export type ScheduledTaskRun = {
  id: string;
  taskId: string;
  workloadId: string;
  jobId: string | null;
  trigger: TaskTrigger;
  status: TaskRunStatus;
  exitCode: number | null;
  /** The end of the output (16 KiB at most); the full output is the log stream. */
  outputTail: string | null;
  outputTruncated: boolean;
  error: string | null;
  createdByUserId: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  durationMs: number | null;
  logStreamId: string;
};

export type RunTaskResponse = { run: ScheduledTaskRun; job: Job };

export type SchedulePreview = { schedule: string; timeZone: string; nextRuns: string[] };

export type TaskLogLine = { sequence: number; timestamp: string; stream: "stdout" | "stderr" | number; source: string | number; text: string };

export type TaskLogPage = { items: TaskLogLine[]; nextSequence: number; hasMore: boolean; ended: boolean };

export type TaskPage<T> = { items: T[]; nextCursor: string | null };
