// The private network API (R2.2, ADR 0012). Shapes follow openapi/aethera.v1.json (MeshSettingsResponse, ServerMeshResponse...).
import type { components } from "@/lib/api/schema";

type S = components["schemas"];

export type MeshProvider = S["MeshProvider"];
export type MeshStatus = S["MeshStatus"];
export type PortBindScope = S["PortBindScope"];
export type MeshSettings = S["MeshSettingsResponse"];
export type MeshServerSummary = S["MeshServerSummary"];
export type ServerMesh = S["ServerMeshResponse"];
export type MeshPeer = S["MeshPeerResponse"];

/** PUT /system/mesh: absent `cidr` and `listenPort` mean the defaults. */
export type MeshSettingsInput = { provider: MeshProvider; cidr?: string; listenPort?: number };

/** PUT /servers/{id}/mesh: null clears an override. */
export type ServerMeshInput = { endpoint: string | null; listenPort: number | null };
