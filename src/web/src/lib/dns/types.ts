/** Types of the DNS provider endpoints, from the generated OpenAPI schema (`pnpm gen:api`). */
import type { components } from "@/lib/api/schema";

type S = components["schemas"];

export type DnsProvider = S["DnsProviderResponse"];
export type DnsProviderVerify = S["DnsProviderVerifyResponse"];
export type DnsCheck = S["DnsCheckResponse"];
export type DnsStatus = S["DnsStatus"];
export type CreateDnsProviderRequest = S["CreateDnsProviderRequest"];
export type UpdateDnsProviderRequest = S["UpdateDnsProviderRequest"];
export type DnsPage<T> = { items: T[]; nextCursor: string | null };
