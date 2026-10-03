import { describe, expect, it, vi } from "vitest";
import { createApiClient, CSRF_HEADER } from "./client";
import { ApiError, ProblemCodes } from "./errors";

type Handler = (url: string, init: RequestInit) => Response | Promise<Response>;

function json(body: unknown, status = 200, contentType = "application/json"): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": contentType } });
}
function problem(status: number, body: Record<string, unknown>): Response {
  return json({ title: "x", status, ...body }, status, "application/problem+json");
}

function setup(handler: Handler) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) =>
    handler(String(input), init ?? {}),
  );
  const client = createApiClient({ fetch: fetchMock as unknown as typeof fetch });
  return { client, fetchMock };
}

function header(init: RequestInit, name: string): string | undefined {
  return (init.headers as Record<string, string> | undefined)?.[name];
}

describe("api client: requests", () => {
  it("calls /api/v1 with same-origin credentials and does not send a CSRF token on GET", async () => {
    const { client, fetchMock } = setup(() => json({ ok: true }));
    await client.get("/auth/me");
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/auth/me");
    expect(init.credentials).toBe("same-origin");
    expect(header(init, CSRF_HEADER)).toBeUndefined();
  });

  it("serialises query parameters and skips undefined ones", async () => {
    const { client, fetchMock } = setup(() => json([]));
    await client.get("/servers", { query: { limit: 10, q: "a b", cursor: undefined } });
    expect(fetchMock.mock.calls[0][0]).toBe("/api/v1/servers?limit=10&q=a+b");
  });

  it("returns undefined for 204", async () => {
    const { client } = setup(() => new Response(null, { status: 204 }));
    await expect(client.get("/x")).resolves.toBeUndefined();
  });
});

describe("api client: CSRF", () => {
  it("fetches the token once, caches it, and sends it on every unsafe method", async () => {
    const { client, fetchMock } = setup((url) =>
      url.endsWith("/auth/csrf") ? json({ token: "tok-1" }) : json({ ok: true }),
    );

    await client.post("/a", { n: 1 });
    await client.put("/b", {});
    await client.patch("/c", {});
    await client.delete("/d");

    const csrfCalls = fetchMock.mock.calls.filter(([u]) => String(u).endsWith("/auth/csrf"));
    expect(csrfCalls).toHaveLength(1);

    const unsafe = fetchMock.mock.calls.filter(([u]) => !String(u).endsWith("/auth/csrf"));
    expect(unsafe).toHaveLength(4);
    for (const [, init] of unsafe as Array<[string, RequestInit]>) {
      expect(header(init, CSRF_HEADER)).toBe("tok-1");
    }
    const [, post] = unsafe[0] as [string, RequestInit];
    expect(post.method).toBe("POST");
    expect(post.body).toBe(JSON.stringify({ n: 1 }));
    expect(header(post, "Content-Type")).toBe("application/json");
  });

  it("does not ask for a token before signing in or setting up (GET /auth/csrf needs a session)", async () => {
    const { client, fetchMock } = setup((url) =>
      url.endsWith("/auth/csrf") ? problem(401, { code: "auth.unauthenticated" }) : json({ ok: true }),
    );

    await client.post("/auth/login", { email: "a@b.c", password: "x" });
    await client.post("/auth/setup", { email: "a@b.c" });

    expect(fetchMock.mock.calls).toHaveLength(2);
    for (const [, init] of fetchMock.mock.calls as Array<[string, RequestInit]>) {
      expect(header(init, CSRF_HEADER)).toBeUndefined();
    }
  });

  it("still sends a token on logout and the other session writes", async () => {
    const { client, fetchMock } = setup((url) =>
      url.endsWith("/auth/csrf") ? json({ token: "tok" }) : json({}),
    );
    await client.post("/auth/logout");
    await client.post("/auth/password", {});
    const writes = fetchMock.mock.calls.filter(([u]) => !String(u).endsWith("/auth/csrf")) as Array<[string, RequestInit]>;
    expect(writes).toHaveLength(2);
    for (const [, init] of writes) expect(header(init, CSRF_HEADER)).toBe("tok");
  });

  it("shares one in-flight token request between concurrent writes", async () => {
    const { client, fetchMock } = setup((url) =>
      url.endsWith("/auth/csrf") ? json({ token: "t" }) : json({}),
    );
    await Promise.all([client.post("/a"), client.post("/b"), client.post("/c")]);
    expect(fetchMock.mock.calls.filter(([u]) => String(u).endsWith("/auth/csrf"))).toHaveLength(1);
  });

  it("refreshes the token and retries once on 403 auth.csrf_invalid", async () => {
    let issued = 0;
    const { client, fetchMock } = setup((url, init) => {
      if (url.endsWith("/auth/csrf")) return json({ token: `tok-${++issued}` });
      return header(init, CSRF_HEADER) === "tok-2"
        ? json({ done: true })
        : problem(403, { code: ProblemCodes.csrfInvalid });
    });

    await expect(client.post("/things", { a: 1 })).resolves.toEqual({ done: true });

    const calls = fetchMock.mock.calls.map(([u, i]) => [String(u), header(i as RequestInit, CSRF_HEADER)]);
    expect(calls).toEqual([
      ["/api/v1/auth/csrf", undefined],
      ["/api/v1/things", "tok-1"],
      ["/api/v1/auth/csrf", undefined],
      ["/api/v1/things", "tok-2"],
    ]);
  });

  it("does not retry more than once when the CSRF error persists", async () => {
    const { client, fetchMock } = setup((url) =>
      url.endsWith("/auth/csrf") ? json({ token: "t" }) : problem(403, { code: ProblemCodes.csrfInvalid }),
    );
    const err = await client.post("/things").catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).code).toBe(ProblemCodes.csrfInvalid);
    // csrf, post, csrf, post -- and nothing after.
    expect(fetchMock).toHaveBeenCalledTimes(4);
  });

  it("does not retry other 403s", async () => {
    const { client, fetchMock } = setup((url) =>
      url.endsWith("/auth/csrf") ? json({ token: "t" }) : problem(403, { code: "auth.forbidden" }),
    );
    await expect(client.post("/things")).rejects.toMatchObject({ code: "auth.forbidden", status: 403 });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("forgets the token after clearCsrfToken()", async () => {
    let n = 0;
    const { client, fetchMock } = setup((url) =>
      url.endsWith("/auth/csrf") ? json({ token: `t${++n}` }) : json({}),
    );
    await client.post("/a");
    client.clearCsrfToken();
    await client.post("/b");
    expect(fetchMock.mock.calls.filter(([u]) => String(u).endsWith("/auth/csrf"))).toHaveLength(2);
    expect(header(fetchMock.mock.calls[3][1] as RequestInit, CSRF_HEADER)).toBe("t2");
  });
});

describe("api client: ProblemDetails", () => {
  it("parses code, traceId and errors[] into an ApiError", async () => {
    const { client } = setup(() =>
      problem(422, {
        type: "urn:aethera:problem:validation.failed",
        title: "Validation failed",
        code: "validation.failed",
        detail: "2 fields are invalid.",
        traceId: "00-abc-01",
        errors: [
          { pointer: "/email", code: "pattern", message: "Must be an email address." },
          { pointer: "/password", code: "too_short", message: "Too short." },
        ],
      }),
    );

    const err = (await client.get("/x").catch((e: unknown) => e)) as ApiError;
    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(422);
    expect(err.code).toBe("validation.failed");
    expect(err.title).toBe("Validation failed");
    expect(err.detail).toBe("2 fields are invalid.");
    expect(err.traceId).toBe("00-abc-01");
    expect(err.errors).toHaveLength(2);
    expect(err.fieldError("/email")).toBe("Must be an email address.");
    expect(err.fieldError("/nope")).toBeUndefined();
    expect(err.message).toBe("2 fields are invalid.");
  });

  it("maps auth failures by code", async () => {
    const { client } = setup(() => problem(423, { code: "auth.locked_out", title: "Locked out" }));
    await expect(client.post("/auth/login", {})).rejects.toMatchObject({
      status: 423,
      code: "auth.locked_out",
    });
  });

  it("falls back to http.<status> when the body is not a problem document", async () => {
    const { client } = setup(
      () => new Response("<html>Bad gateway</html>", { status: 502, statusText: "Bad Gateway" }),
    );
    const err = (await client.get("/x").catch((e: unknown) => e)) as ApiError;
    expect(err).toBeInstanceOf(ApiError);
    expect(err.code).toBe("http.502");
    expect(err.status).toBe(502);
    expect(err.errors).toEqual([]);
  });

  it("turns a failed fetch into a network ApiError", async () => {
    const { client } = setup(() => {
      throw new TypeError("Failed to fetch");
    });
    const err = (await client.get("/x").catch((e: unknown) => e)) as ApiError;
    expect(err).toBeInstanceOf(ApiError);
    expect(err.isNetworkError).toBe(true);
    expect(err.status).toBe(0);
    expect(err.code).toBe(ProblemCodes.network);
  });
});
