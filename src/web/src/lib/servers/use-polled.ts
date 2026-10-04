"use client";

import * as React from "react";
import { isApiError, type ApiError } from "@/lib/api/errors";

export type Polled<T> = {
  data: T | undefined;
  error: ApiError | Error | null;
  /** True until the first response or error. */
  loading: boolean;
  /** True while a manual refresh is running (data may be stale). */
  refreshing: boolean;
  updatedAt: number | null;
  refresh: () => Promise<void>;
};

export type PollOptions = {
  /** Poll interval in ms; `null`/0 disables polling (still fetches once). */
  intervalMs?: number | null;
  /** Skip fetching entirely (e.g. no id yet). */
  enabled?: boolean;
};

type State<T> = {
  /** The key this state belongs to; a different key means "start over". */
  key: string;
  data: T | undefined;
  error: ApiError | Error | null;
  refreshing: boolean;
  updatedAt: number | null;
};

const fresh = <T,>(key: string): State<T> => ({ key, data: undefined, error: null, refreshing: false, updatedAt: null });

/**
 * Fetch on mount and then every `intervalMs`, pausing while the tab is hidden.
 * The control plane has no push channel for these resources yet (the SignalR
 * hub only carries job logs), so polling is the "live" mechanism.
 *
 * `key` identifies what is being fetched (server id, range...). A new key drops
 * the old data and starts over. The latest `fetcher` is always used, so it may
 * close over anything. A failed refetch keeps the previous data and exposes the
 * error, so a blip does not blank the page.
 */
export function usePolled<T>(
  fetcher: (signal: AbortSignal) => Promise<T>,
  key: string,
  { intervalMs = 10_000, enabled = true }: PollOptions = {},
): Polled<T> {
  const [state, setState] = React.useState<State<T>>(() => fresh<T>(key));
  const current: State<T> = state.key === key ? state : fresh<T>(key);

  const fetcherRef = React.useRef(fetcher);
  React.useEffect(() => {
    fetcherRef.current = fetcher;
  });
  const controllerRef = React.useRef<AbortController | null>(null);

  // No synchronous setState in here: the effect below calls it.
  const fetchOnce = React.useCallback(async () => {
    controllerRef.current?.abort();
    const controller = new AbortController();
    controllerRef.current = controller;
    try {
      const data = await fetcherRef.current(controller.signal);
      if (controller.signal.aborted) return;
      setState({ key, data, error: null, refreshing: false, updatedAt: Date.now() });
    } catch (e) {
      if (controller.signal.aborted) return;
      if (e instanceof DOMException && e.name === "AbortError") return;
      const err = isApiError(e) ? e : e instanceof Error ? e : new Error(String(e));
      setState((s) => ({ ...(s.key === key ? s : fresh<T>(key)), error: err, refreshing: false }));
    }
  }, [key]);

  const refresh = React.useCallback(async () => {
    setState((s) => ({ ...(s.key === key ? s : fresh<T>(key)), refreshing: true }));
    await fetchOnce();
  }, [key, fetchOnce]);

  React.useEffect(() => {
    if (!enabled) return;
    void fetchOnce();
    let timer: ReturnType<typeof setInterval> | undefined;
    if (intervalMs && intervalMs > 0) {
      timer = setInterval(() => {
        if (!document.hidden) void fetchOnce();
      }, intervalMs);
    }
    const onVisible = () => {
      if (!document.hidden) void fetchOnce();
    };
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      controllerRef.current?.abort();
      if (timer !== undefined) clearInterval(timer);
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, [fetchOnce, enabled, intervalMs]);

  return {
    data: enabled ? current.data : undefined,
    error: enabled ? current.error : null,
    loading: enabled && current.data === undefined && current.error === null,
    refreshing: current.refreshing,
    updatedAt: current.updatedAt,
    refresh,
  };
}
