"use client";

import { useAuth } from "@/lib/auth/auth-context";

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

/**
 * STUB. The status bar reads this hook; a later work package replaces the
 * body with the SignalR connection state (`/hubs/...`) and live counts.
 *
 * Until then: "connected" means the last `GET /auth/me` succeeded, and
 * "unavailable" means the control plane could not be reached (spec section 44,
 * "Control plane unavailable"). Counts are 0 until servers and jobs exist.
 */
export function useConnectionStatus(): ConnectionStatus {
  const { status } = useAuth();
  switch (status) {
    case "authenticated":
      return { state: "connected", servers: 0, jobsRunning: 0 };
    case "unavailable":
      return { state: "unavailable", servers: null, jobsRunning: null };
    default:
      return { state: "connecting", servers: null, jobsRunning: null };
  }
}
