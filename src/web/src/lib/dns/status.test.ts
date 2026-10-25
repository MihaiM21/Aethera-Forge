import { describe, expect, it } from "vitest";
import { createDnsApi } from "./api";
import { dnsMethodLabel, dnsStatusExplanation, dnsStatusTone } from "./status";
import type { ApiClient } from "@/lib/api/client";

describe("dns status", () => {
  it("shows proxied with a neutral badge and explains it", () => {
    expect(dnsStatusTone("proxied")).toBe("neutral");
    expect(dnsStatusExplanation("proxied")).toMatch(/Proxied by Cloudflare/);
    expect(dnsStatusExplanation("proxied")).toMatch(/DNS provider/);
  });

  it("maps the other statuses like the existing badge does", () => {
    expect(dnsStatusTone("ok")).toBe("success");
    expect(dnsStatusTone("unknown")).toBe("neutral");
    expect(dnsStatusTone("mismatch")).toBe("warning");
    expect(dnsStatusTone("missing")).toBe("warning");
    expect(dnsStatusTone("error")).toBe("danger");
    expect(dnsStatusExplanation("unknown")).toBeNull();
  });

  it("names the check method", () => {
    expect(dnsMethodLabel("cloudflareApi")).toBe("Cloudflare API");
    expect(dnsMethodLabel("resolver")).toBe("DNS lookup");
  });
});

describe("dns api", () => {
  it("calls the dns-providers endpoints", async () => {
    const calls: unknown[][] = [];
    const client = {
      get: async (...a: unknown[]) => (calls.push(["get", ...a]), { items: [], nextCursor: null }),
      post: async (...a: unknown[]) => (calls.push(["post", ...a]), {}),
      patch: async (...a: unknown[]) => (calls.push(["patch", ...a]), {}),
      delete: async (...a: unknown[]) => (calls.push(["delete", ...a]), undefined),
    } as unknown as ApiClient;
    const dns = createDnsApi(client);
    await dns.list();
    await dns.create({ name: "n", apiToken: "t" });
    await dns.update("a/b", { name: "m" });
    await dns.remove("id", "My name");
    await dns.verify("id");
    expect(calls).toEqual([
      ["get", "/dns-providers", { query: { limit: 100, sort: "name" }, signal: undefined }],
      ["post", "/dns-providers", { name: "n", apiToken: "t" }],
      ["patch", "/dns-providers/a%2Fb", { name: "m" }],
      ["delete", "/dns-providers/id", { query: { confirm: "My name" } }],
      ["post", "/dns-providers/id/verify"],
    ]);
  });
});
