"use client";

import * as React from "react";
import { StatusPill, type StatusKey } from "@/components/ui/status-pill";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { errorMessage } from "@/lib/servers/errors";
import type { Job, JobStatus } from "@/lib/servers/types";

const TERMINAL: ReadonlySet<JobStatus> = new Set(["succeeded", "failed", "cancelled"]);
export const isTerminal = (s: JobStatus) => TERMINAL.has(s);

const PILL: Record<JobStatus, StatusKey> = {
  queued: "queued",
  running: "running",
  succeeded: "succeeded",
  failed: "failed",
  cancelled: "cancelled",
};

/**
 * Follows a job (`GET /jobs/{id}`) until it ends and reports it. There is no
 * shared job component yet, so this one is generic: label, status pill, queue
 * position, and the API's own failure (code, step, retryability) when it fails.
 */
export function JobProgress({
  jobId,
  label,
  api = serversApi,
  intervalMs = 2000,
  onFinished,
}: {
  jobId: string;
  label: string;
  api?: ServersApi;
  intervalMs?: number;
  onFinished?: (job: Job) => void;
}) {
  const [job, setJob] = React.useState<Job | null>(null);
  const [pollError, setPollError] = React.useState<string | null>(null);
  const finishedRef = React.useRef(onFinished);
  React.useEffect(() => {
    finishedRef.current = onFinished;
  });

  React.useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const controller = new AbortController();
    const tick = async () => {
      try {
        const next = await api.job(jobId, { signal: controller.signal });
        if (cancelled) return;
        setJob(next);
        setPollError(null);
        if (isTerminal(next.status)) {
          finishedRef.current?.(next);
          return;
        }
      } catch (e) {
        if (cancelled || (e instanceof DOMException && e.name === "AbortError")) return;
        setPollError(errorMessage(e));
      }
      timer = setTimeout(() => void tick(), intervalMs);
    };
    void tick();
    return () => {
      cancelled = true;
      controller.abort();
      clearTimeout(timer);
    };
  }, [api, jobId, intervalMs]);

  const status = job?.status ?? "queued";
  return (
    <div data-slot="job-progress" className="grid gap-2 border border-border bg-card px-4 py-3 text-sm" aria-live="polite">
      <div className="flex flex-wrap items-center gap-3">
        <span className="font-medium">{label}</span>
        <StatusPill status={PILL[status]} />
        {job?.queuePosition != null && status === "queued" && (
          <span className="font-mono text-2xs text-muted-foreground">position {String(job.queuePosition)} in queue</span>
        )}
        {job && Number(job.attempt) > 1 && (
          <span className="font-mono text-2xs text-muted-foreground">
            attempt {String(job.attempt)}/{String(job.maxAttempts)}
          </span>
        )}
        <span className="ml-auto font-mono text-2xs text-muted-foreground">job {jobId.slice(0, 8)}</span>
      </div>
      {job?.status === "failed" && job.error && (
        <p role="alert" className="border border-danger/60 bg-danger-soft px-3 py-2 text-xs text-danger">
          {job.error.title}
          {job.error.detail ? `: ${job.error.detail}` : ""}
          {job.error.failedStep ? ` (step ${job.error.failedStep})` : ""}
        </p>
      )}
      {pollError && <p className="text-xs text-warning">Could not refresh the job state: {pollError}</p>}
    </div>
  );
}
