import { describe, expect, it, vi } from "vitest";
import { createApiClient } from "@/lib/api/client";
import { ApiError } from "@/lib/api/errors";
import { createServersApi } from "./api";
import { errorMessage, isHostUnavailable } from "./errors";

type Call = { url: string; method: string; body?: string; csrf?: string };

function setup(responder: (call: Call) => Response) {
  const calls: Call[] = [];
  const fetchMock = vi.fn(async (input: string, init: RequestInit) => {
    const headers = init.headers as Record<string, string>;
    const call: Call = { url: input, method: String(init.method), body: init.body as string | undefined, csrf: headers["X-CSRF-Token"] };
    if (input.endsWith("/auth/csrf")) return new Response(JSON.stringify({ token: "csrf-1" }), { status: 200 });
    calls.push(call);
    return responder(call);
  });
  const api = createServersApi(createApiClient({ fetch: fetchMock as unknown as typeof fetch }));
  return { api, calls };
}

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const problemBody = (status: number, code: string) =>
  new Response(JSON.stringify({ type: `urn:aethera:problem:${code}`, title: code, status, code }), { status });

describe("servers api client", () => {
  it("lists with the capitalised paging parameters from the OpenAPI document", async () => {
    const { api, calls } = setup(() => json({ items: [], nextCursor: null }));
    await api.list({ limit: 100, sort: "name", q: "edge" });
    expect(calls[0].url).toBe("/api/v1/servers?Limit=100&sort=name&q=edge");
  });

  it("sends the server name as ?confirm= for reset, delete and volume prune", async () => {
    const { api, calls } = setup(() => new Response(null, { status: 204 }));
    await api.resetAgent("abc", "edge 1");
    await api.remove("abc", "edge 1");
    expect(calls[0]).toMatchObject({ method: "POST", url: "/api/v1/servers/abc/agent/reset?confirm=edge+1", csrf: "csrf-1" });
    expect(calls[1]).toMatchObject({ method: "DELETE", url: "/api/v1/servers/abc?confirm=edge+1" });
  });

  it("prune: JSON body, confirm only when given", async () => {
    const { api, calls } = setup(() => json({ id: "j" }, 202));
    await api.prune("abc", { danglingImages: true });
    await api.prune("abc", { volumes: true }, "edge-1");
    expect(calls[0].url).toBe("/api/v1/servers/abc/maintenance/prune");
    expect(JSON.parse(calls[0].body!)).toEqual({ danglingImages: true });
    expect(calls[1].url).toBe("/api/v1/servers/abc/maintenance/prune?confirm=edge-1");
  });

  it("builds the metrics query and encodes ids", async () => {
    const { api, calls } = setup(() => json({ points: [] }));
    await api.metrics("a/b", { from: "2026-10-04T00:00:00Z", resolution: "auto", maxPoints: 300 });
    expect(calls[0].url).toBe("/api/v1/servers/a%2Fb/metrics?from=2026-10-04T00%3A00%3A00Z&resolution=auto&maxPoints=300");
  });

  it("surfaces a 428 confirmation problem with its code", async () => {
    const { api } = setup(() => problemBody(428, "confirmation.required"));
    await expect(api.resetAgent("abc", "wrong")).rejects.toMatchObject({ status: 428, code: "confirmation.required" });
  });

  it("recognises an unavailable agent (503) on the inventory endpoints", async () => {
    const { api } = setup(() => problemBody(503, "server.agent_unavailable"));
    const err = await api.containers("abc").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(isHostUnavailable(err)).toBe(true);
    expect(isHostUnavailable(new ApiError({ status: 500, code: "server.error" }))).toBe(false);
  });

  it("turns errors into readable text without leaking internals", () => {
    expect(errorMessage(ApiError.network())).toMatch(/unreachable/i);
    expect(errorMessage(new ApiError({ status: 422, code: "validation.failed", title: "Validation failed", errors: [{ pointer: "/name", message: "Name is taken." }] }))).toContain("Name is taken.");
    expect(errorMessage("weird")).toBe("Something went wrong.");
  });
});
