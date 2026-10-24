"use client";

import * as React from "react";
import type { LogChunk } from "@/components/aethera/log-viewer";
import type { TasksApi } from "./api";
import type { TaskLogPage } from "./types";

export type FollowedRunLog = { chunks: LogChunk[]; ended: boolean; error: Error | null; loading: boolean };

const EMPTY: FollowedRunLog = { chunks: [], ended: false, error: null, loading: true };

export function pageToChunks(page: TaskLogPage): LogChunk[] {
  return page.items.map((l) => ({
    id: Number(l.sequence),
    text: l.text,
    stream: l.stream === "stderr" || l.stream === 2 ? "stderr" : "stdout",
    timestamp: l.timestamp,
  }));
}

/**
 * Follows the stored output of one task run by sequence (the same poll the deployment log uses): each tick asks for
 * `fromSequence = next` and stops once the page says the stream ended. A new run id starts over.
 */
export function useRunLog(api: TasksApi, runId: string | null, { intervalMs = 1500 }: { intervalMs?: number } = {}): FollowedRunLog {
  const [state, setState] = React.useState<{ key: string | null; log: FollowedRunLog }>({ key: runId, log: EMPTY });

  React.useEffect(() => {
    if (!runId) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let next = 0;
    let chunks: LogChunk[] = [];
    const controller = new AbortController();

    const tick = async () => {
      try {
        const page = await api.runLogs(runId, { fromSequence: next, limit: 500 }, { signal: controller.signal });
        if (stopped) return;
        next = Number(page.nextSequence);
        if (page.items.length > 0) chunks = chunks.concat(pageToChunks(page));
        const ended = page.ended && !page.hasMore;
        setState({ key: runId, log: { chunks, ended, error: null, loading: false } });
        if (ended) return;
        timer = setTimeout(tick, page.hasMore ? 0 : intervalMs);
      } catch (e) {
        if (stopped || (e instanceof DOMException && e.name === "AbortError")) return;
        setState((s) => ({ key: runId, log: { ...(s.key === runId ? s.log : EMPTY), loading: false, error: e instanceof Error ? e : new Error(String(e)) } }));
        timer = setTimeout(tick, intervalMs * 3);
      }
    };
    void tick();
    return () => {
      stopped = true;
      clearTimeout(timer);
      controller.abort();
    };
  }, [api, runId, intervalMs]);

  return state.key === runId ? state.log : EMPTY;
}
