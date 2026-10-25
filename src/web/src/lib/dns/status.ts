import type { DnsStatus } from "./types";

export type DnsTone = "success" | "neutral" | "warning" | "danger";

/** Badge tone of a domain's DNS status. `proxied` is neutral: the origin is hidden by Cloudflare, nothing is wrong. */
export function dnsStatusTone(status: DnsStatus): DnsTone {
  switch (status) {
    case "ok":
      return "success";
    case "unknown":
    case "proxied":
      return "neutral";
    case "error":
      return "danger";
    default:
      return "warning";
  }
}

/** The sentence that explains a status where the badge alone would not (shown as a tooltip or helper text). */
export function dnsStatusExplanation(status: DnsStatus): string | null {
  switch (status) {
    case "proxied":
      return "Proxied by Cloudflare: the public answer is a Cloudflare address, so the origin cannot be verified. Add a Cloudflare DNS provider in Settings > DNS to check the record itself.";
    case "ok":
      return "The name (or its Cloudflare record) points at the server.";
    case "mismatch":
      return "The name points at a different address than the server's.";
    case "missing":
      return "No A, AAAA or CNAME record was found.";
    case "error":
      return "The DNS lookup failed. Try again later.";
    default:
      return null;
  }
}

/** How the last check found its answer: `cloudflareApi` (the record read through a DNS provider) or `resolver` (a public DNS lookup). */
export function dnsMethodLabel(method: string): string {
  return method === "cloudflareApi" ? "Cloudflare API" : "DNS lookup";
}
