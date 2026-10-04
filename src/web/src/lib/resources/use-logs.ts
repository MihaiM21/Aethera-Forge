"use client";

import * as React from "react";
import type { LogChunk } from "@/components/aethera/log-viewer";
import type { ResourcesApi } from "./api";
import type { DeploymentLogPage, RuntimeLogs } from "./types";

export type FollowedLog = {
  chunks: LogChunk[];
  /** The stream has ended and everything was read. */
  ended: boolean;
  error: Error | null;
  loading: boolean;
  /** `build` and/or `deploy`: which streams this deployment has. */
  sources: Array<"build" | "deploy">;
};

const EMPTY: FollowedLog = { chunks: [], ended: false, error: null, loading: true, sources: [] };

export function pageToChunks(page: DeploymentLogPage): LogChunk[] {
  return page.items.map((l) => ({
    id: Number(l.sequence),
    text: l.text,
    stream: l.stream === "stderr" || l.stream === 2 ? "stderr" : "stdout",
    timestamp: l.timestamp,
  }));
}

/**
 * Follows the stored build or pipeline log of a deployment by sequence. The API has a live SignalR hub for the same
 * streams, but a poll of the paged REST endpoint needs no extra client and survives reconnects for free: each tick asks for
 * `fromSequence = next` and stops once the page says the stream ended.
 */
export function useDeploymentLog(
  api: ResourcesApi,
  deploymentId: string | null,
  source: "build" | "deploy" | undefined,
  { intervalMs = 1500, enabled = true }: { intervalMs?: number; enabled?: boolean } = {},
): FollowedLog {
  const key = `${deploymentId}:${source ?? "auto"}`;
  const [state, setState] = React.useState<{ key: string; log: FollowedLog }>({ key, log: EMPTY });

  React.useEffect(() => {
    if (!deploymentId || !enabled) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let next = 0;
    let chunks: LogChunk[] = [];
    const controller = new AbortController();

    const tick = async () => {
      try {
        const page = await api.deployments.logs(deploymentId, { source, fromSequence: next, limit: 500 }, { signal: controller.signal });
        if (stopped) return;
        next = Number(page.nextSequence);
        if (page.items.length > 0) chunks = chunks.concat(pageToChunks(page));
        const ended = page.ended && !page.hasMore;
        setState({ key, log: { chunks, ended, error: null, loading: false, sources: page.sources } });
        if (ended) return;
        timer = setTimeout(tick, page.hasMore ? 0 : intervalMs);
      } catch (e) {
        if (stopped || (e instanceof DOMException && e.name === "AbortError")) return;
        setState((s) => ({ key, log: { ...(s.key === key ? s.log : EMPTY), loading: false, error: e instanceof Error ? e : new Error(String(e)) } }));
        timer = setTimeout(tick, intervalMs * 3);
      }
    };
    void tick();
    return () => {
      stopped = true;
      clearTimeout(timer);
      controller.abort();
    };
  }, [api, deploymentId, source, enabled, intervalMs, key]);

  return state.key === key ? state.log : EMPTY;
}

export type RuntimeLogState = { logs: RuntimeLogs | undefined; error: Error | null; loading: boolean };

/** Chunks of a runtime log snapshot (one chunk per line, containers interleaved by time). */
export function runtimeToChunks(logs: RuntimeLogs | undefined, multi: boolean): LogChunk[] {
  if (!logs) return [];
  return logs.lines.map((l, i) => ({
    id: i + 1,
    text: `${multi ? `[${l.container.slice(0, 12)}] ` : ""}${l.text.endsWith("\n") ? l.text : `${l.text}\n`}`,
    stream: l.stream === "stderr" ? "stderr" : "stdout",
    timestamp: String(l.timestamp),
  }));
}
