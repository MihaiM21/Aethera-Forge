import { isApiError } from "@/lib/api/errors";

/** Problem codes of the live Docker inventory endpoints (503 while the host cannot be asked). */
export const ServerProblemCodes = {
  agentUnavailable: "server.agent_unavailable",
  unreachable: "server.unreachable",
  agentBusy: "transport.agent_busy",
  ackTimeout: "transport.ack_timeout",
  commandFailed: "server.command_failed",
} as const;

const UNAVAILABLE = new Set<string>([
  ServerProblemCodes.agentUnavailable,
  ServerProblemCodes.unreachable,
  ServerProblemCodes.agentBusy,
  ServerProblemCodes.ackTimeout,
]);

/** The host (or its agent) could not answer right now; the page should explain, not crash. */
export function isHostUnavailable(e: unknown): boolean {
  return isApiError(e) && (UNAVAILABLE.has(e.code) || e.status === 503 || e.status === 504);
}

export function errorMessage(e: unknown, fallback = "Something went wrong."): string {
  if (isApiError(e)) {
    if (e.isNetworkError) return "The control plane is unreachable. Check your connection and retry.";
    const fields = e.errors.map((f) => f.message).filter(Boolean);
    if (fields.length > 0) return `${e.detail ?? e.title}: ${fields.join(" ")}`;
    return e.detail ?? e.title ?? fallback;
  }
  return e instanceof Error ? e.message : fallback;
}
