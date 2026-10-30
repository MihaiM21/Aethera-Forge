import type { MeshProvider, MeshStatus, PortBindScope } from "./types";

export const PROVIDER_LABEL: Record<MeshProvider, string> = { none: "Off", wireGuard: "WireGuard (managed by Aethera)", tailscale: "Tailscale (existing tailnet)" };

export const STATUS_LABEL: Record<MeshStatus, string> = { disabled: "off", pending: "waiting", up: "up", degraded: "degraded", failed: "failed" };

export type Tone = "neutral" | "success" | "warning" | "danger";

export const STATUS_TONE: Record<MeshStatus, Tone> = { disabled: "neutral", pending: "neutral", up: "success", degraded: "warning", failed: "danger" };

export const SCOPE_LABEL: Record<PortBindScope, string> = { public: "Public", private: "Private network", loopback: "This server only" };

export const SCOPE_HINT: Record<PortBindScope, string> = {
  public: "Listens on every interface of the server, the internet included.",
  private: "Listens on the server's address in the private network: reachable from your other servers, never from the internet.",
  loopback: "Listens on 127.0.0.1: only processes on this server can connect.",
};

/** The scopes a port may have. */
export const SCOPES: PortBindScope[] = ["public", "private", "loopback"];

export function parseScope(v: string | undefined | null): PortBindScope {
  return v === "private" || v === "loopback" ? v : "public";
}

/**
 * Where something outside the server connects to a published port: the mesh address for a private port, the public address of the server for a
 * public one. Null when there is nothing to say (no host port, loopback, private without an address).
 */
export function publishedAddress(
  port: { publishedPort?: number | string | null; bindScope?: PortBindScope },
  server: { meshAddress?: string | null; publicHost?: string | null },
): string | null {
  if (port.publishedPort == null || port.publishedPort === "") return null;
  const scope = port.bindScope ?? "public";
  if (scope === "private") return server.meshAddress ? `${server.meshAddress}:${port.publishedPort}` : null;
  if (scope === "public") return server.publicHost ? `${server.publicHost}:${port.publishedPort}` : null;
  return null;
}

/** The port the agents listen on when a server has no override. */
export const DEFAULT_LISTEN_PORT = 51820;
export const DEFAULT_CIDR = "10.210.0.0/16";
