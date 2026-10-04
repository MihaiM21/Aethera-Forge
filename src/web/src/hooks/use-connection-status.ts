"use client";

import { useAuth } from "@/lib/auth/auth-context";
import { resourcesApi } from "@/lib/resources/api";
import { serversApi } from "@/lib/servers/api";
import { usePolled } from "@/lib/servers/use-polled";

export type ConnectionState =
  | "connecting"
  | "connected"
  | "reconnecting"
  | "unavailable";

export type ConnectionStatus = {
  state: ConnectionState;
  /** Servers known to the control plane. `null` until the data exists. */
  servers: number | null;
  /** Jobs currently running. `null` until the data exists. */
  jobsRunning: number | null;
};

const COUNT_POLL_MS = 10_000;

/**
 * What the status bar shows. "connected" means the last `GET /auth/me` succeeded and "unavailable" that the control plane
 * could not be reached (spec section 44, "Control plane unavailable"). The counts are polled while signed in; a failed poll
 * leaves the previous count (or `null`) instead of showing a wrong zero.
 */
export function useConnectionStatus(): ConnectionStatus {
  const { status } = useAuth();
  const signedIn = status === "authenticated";
  const servers = usePolled(async (signal) => (await serversApi.list({ limit: 100 }, { signal })).items.length, "bar-servers", {
    intervalMs: COUNT_POLL_MS,
    enabled: signedIn,
  });
  const jobs = usePolled(async (signal) => (await resourcesApi.jobs.list({ limit: 100, status: "running" }, { signal })).items.length, "bar-jobs", {
    intervalMs: COUNT_POLL_MS,
    enabled: signedIn,
  });
  switch (status) {
    case "authenticated":
      return { state: "connected", servers: servers.data ?? null, jobsRunning: jobs.data ?? null };
    case "unavailable":
      return { state: "unavailable", servers: null, jobsRunning: null };
    default:
      return { state: "connecting", servers: null, jobsRunning: null };
  }
}
