import { api, type ApiClient } from "@/lib/api/client";
import type { MeshSettings, MeshSettingsInput, ServerMesh, ServerMeshInput } from "./types";

type Signal = { signal?: AbortSignal };
const enc = encodeURIComponent;

/** The private network endpoints (ADR 0012): the instance setting and a server's address, key, endpoint and peers. */
export function createMeshApi(client: ApiClient = api) {
  return {
    /** Administrators. */
    settings: (o: Signal = {}) => client.get<MeshSettings>("/system/mesh", o),
    saveSettings: (body: MeshSettingsInput) => client.put<MeshSettings>("/system/mesh", body),
    /** Applies the network to every server again; `queued` is false when no server could take part. */
    reconcile: () => client.post<{ queued: boolean }>("/system/mesh/reconcile", {}),
    server: (id: string, o: Signal = {}) => client.get<ServerMesh>(`/servers/${enc(id)}/mesh`, o),
    saveServer: (id: string, body: ServerMeshInput) => client.put<ServerMesh>(`/servers/${enc(id)}/mesh`, body),
  };
}

export type MeshApi = ReturnType<typeof createMeshApi>;

export const meshApi: MeshApi = createMeshApi();
