import { api, type ApiClient } from "@/lib/api/client";
import type { Deployment, Page, Preview, PreviewSettings, PreviewSettingsInput } from "./types";

type Signal = { signal?: AbortSignal };
const enc = encodeURIComponent;

/** The pull request preview endpoints of an application (ADR 0012). */
export function createPreviewsApi(client: ApiClient = api) {
  const base = (applicationId: string) => `/applications/${enc(applicationId)}/previews`;
  return {
    /** Removed previews are left out unless asked for. */
    list: (applicationId: string, o: Signal & { includeRemoved?: boolean } = {}) =>
      client.get<Page<Preview>>(base(applicationId), { query: { limit: 100, includeRemoved: o.includeRemoved ? true : undefined }, signal: o.signal }),
    settings: (applicationId: string, o: Signal = {}) => client.get<PreviewSettings>(`${base(applicationId)}/settings`, o),
    /** Turning on `allowForks` needs the administrator role. */
    putSettings: (applicationId: string, body: PreviewSettingsInput) => client.put<PreviewSettings>(`${base(applicationId)}/settings`, body),
    redeploy: (applicationId: string, number: number) => client.post<Deployment>(`${base(applicationId)}/${number}/redeploy`, {}),
    /** Tears the preview down; the pull request may stay open (its next push brings the preview back). */
    remove: (applicationId: string, number: number) => client.delete<Preview>(`${base(applicationId)}/${number}`),
  };
}

export type PreviewsApi = ReturnType<typeof createPreviewsApi>;

export const previewsApi: PreviewsApi = createPreviewsApi();
