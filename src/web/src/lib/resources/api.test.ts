import { describe, expect, it, vi } from "vitest";
import { createApiClient } from "@/lib/api/client";
import { createResourcesApi } from "./api";

type Call = { url: string; method: string; body?: string };

function setup() {
  const calls: Call[] = [];
  const fetchMock = vi.fn(async (input: string, init: RequestInit) => {
    if (input.endsWith("/auth/csrf")) return new Response(JSON.stringify({ token: "t" }), { status: 200 });
    calls.push({ url: input, method: String(init.method), body: init.body as string | undefined });
    return new Response(JSON.stringify({ items: [], nextCursor: null }), { status: 200 });
  });
  return { api: createResourcesApi(createApiClient({ fetch: fetchMock as unknown as typeof fetch })), calls };
}

describe("resources api client", () => {
  // The resource endpoints reject unknown query parameters and know the paging parameters in lower case only
  // (`RejectUnknownQuery`); the capitalised spelling in the OpenAPI document is an artefact of [AsParameters].
  it("pages with lower-case limit, cursor and sort on every list", async () => {
    const { api, calls } = setup();
    await api.projects.list({ limit: 100, sort: "name", cursor: "c1" });
    await api.applications.list({ limit: 50, status: "failed", projectId: "p" });
    await api.services.list({ limit: 50 });
    await api.deployments.list({ limit: 25, cursor: "c2", status: "failed" });
    await api.domains.list({ limit: 10, dnsStatus: "ok" });
    await api.secrets.list({ limit: 10 });
    await api.registries.list({ limit: 10 });
    await api.audit.list({ limit: 50, cursor: "c3", action: "secret.*" });
    await api.jobs.list({ limit: 10, status: "queued,running" });
    await api.envVars.list("applications", "a1");
    for (const c of calls) {
      expect(c.url, c.url).not.toMatch(/[?&](Limit|Cursor)=/);
    }
    expect(calls[0].url).toBe("/api/v1/projects?limit=100&cursor=c1&sort=name");
    expect(calls[3].url).toBe("/api/v1/deployments?limit=25&cursor=c2&status=failed");
    expect(calls[7].url).toBe("/api/v1/audit-log?limit=50&cursor=c3&action=secret.*");
    expect(calls[8].url).toBe("/api/v1/jobs?limit=10&status=queued%2Crunning");
  });

  it("addresses applications and services through their own prefixes", async () => {
    const { api, calls } = setup();
    await api.applications.deploy("a1");
    await api.services.deploy("s1");
    await api.applications.lifecycle("a1", "restart");
    await api.services.lifecycle("s1", "stop");
    await api.envVars.create("services", "s1", { key: "A", value: "1" });
    expect(calls.map((c) => `${c.method} ${c.url}`)).toEqual([
      "POST /api/v1/applications/a1/deployments",
      "POST /api/v1/services/s1/deployments",
      "POST /api/v1/applications/a1/restart",
      "POST /api/v1/services/s1/stop",
      "POST /api/v1/services/s1/env-vars",
    ]);
  });

  it("sends the confirmation name for destructive calls and cascades a project delete on request", async () => {
    const { api, calls } = setup();
    await api.projects.remove("p1", "shop", true);
    await api.applications.remove("a1", "web");
    await api.gitCredentials.remove("g1", "token");
    expect(calls.map((c) => c.url)).toEqual([
      "/api/v1/projects/p1?confirm=shop&cascade=true",
      "/api/v1/applications/a1?confirm=web",
      "/api/v1/git-credentials/g1?confirm=token",
    ]);
  });

  it("builds the log download link for a deployment stream", () => {
    const { api } = setup();
    expect(api.deployments.logDownloadUrl("d 1", "build")).toBe("/api/v1/deployments/d%201/logs?download=true&source=build");
  });

  it("reaches the version, update and maintenance endpoints", async () => {
    const { api, calls } = setup();
    await api.system.version();
    await api.system.checkVersion();
    await api.system.startUpdate();
    await api.system.maintenance();
    await api.system.runMaintenance();
    expect(calls.map((c) => `${c.method} ${c.url}`)).toEqual([
      "GET /api/v1/system/version",
      "POST /api/v1/system/version/check",
      "POST /api/v1/system/update",
      "GET /api/v1/system/maintenance",
      "POST /api/v1/system/maintenance/run",
    ]);
  });

  it("reads the compose file of an application through the inspect endpoint", async () => {
    const { api, calls } = setup();
    await api.applications.composeInspect("a1");
    expect(calls.map((c) => `${c.method} ${c.url}`)).toEqual(["POST /api/v1/applications/a1/compose/inspect"]);
  });

  it("lists DNS providers, and tolerates a server that does not have the endpoint yet", async () => {
    const found = createResourcesApi(
      createApiClient({
        fetch: (async (input: string) =>
          input.endsWith("/auth/csrf")
            ? new Response(JSON.stringify({ token: "t" }))
            : new Response(JSON.stringify({ items: [{ id: "p1", name: "Cloudflare" }], nextCursor: null }))) as unknown as typeof fetch,
      }),
    );
    expect(await found.domains.dnsProviders()).toEqual([{ id: "p1", name: "Cloudflare" }]);

    const missing = createResourcesApi(
      createApiClient({
        fetch: (async (input: string) =>
          input.endsWith("/auth/csrf")
            ? new Response(JSON.stringify({ token: "t" }))
            : new Response(JSON.stringify({ type: "about:blank", title: "Not found", status: 404, code: "http.404" }), { status: 404, headers: { "content-type": "application/problem+json" } })) as unknown as typeof fetch,
      }),
    );
    expect(await missing.domains.dnsProviders()).toEqual([]);
  });
});
