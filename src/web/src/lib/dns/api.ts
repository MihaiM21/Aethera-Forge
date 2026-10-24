import { api, type ApiClient } from "@/lib/api/client";
import type { CreateDnsProviderRequest, DnsPage, DnsProvider, DnsProviderVerify, UpdateDnsProviderRequest } from "./types";

const enc = encodeURIComponent;

/** `/api/v1/dns-providers`. The API token is write-only: it goes out in `create` and `update`, no response carries it. */
export function createDnsApi(client: ApiClient = api) {
  return {
    list: (q: { limit?: number; sort?: string } = {}, o: { signal?: AbortSignal } = {}) =>
      client.get<DnsPage<DnsProvider>>("/dns-providers", { query: { limit: q.limit ?? 100, sort: q.sort ?? "name" }, signal: o.signal }),
    create: (body: CreateDnsProviderRequest) => client.post<DnsProvider>("/dns-providers", body),
    update: (id: string, body: UpdateDnsProviderRequest) => client.patch<DnsProvider>(`/dns-providers/${enc(id)}`, body),
    /** Needs the provider's name as `?confirm=`; 409 `dns_provider.in_use` while a domain uses it. */
    remove: (id: string, confirm: string) => client.delete<void>(`/dns-providers/${enc(id)}`, { query: { confirm } }),
    /** Checks the token at Cloudflare and stores the zones it sees. */
    verify: (id: string) => client.post<DnsProviderVerify>(`/dns-providers/${enc(id)}/verify`),
  };
}

export type DnsApi = ReturnType<typeof createDnsApi>;

export const dnsApi: DnsApi = createDnsApi();
