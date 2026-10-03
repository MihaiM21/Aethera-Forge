import { ApiError, ProblemCodes, type ProblemDetails } from "./errors";

export const API_BASE_PATH = "/api/v1";
export const CSRF_HEADER = "X-CSRF-Token";

const UNSAFE_METHODS = new Set(["POST", "PUT", "PATCH", "DELETE"]);

/**
 * Unsafe endpoints that are called without a session (ADR 0003 section 7: only cookie-authenticated
 * requests carry a CSRF token). GET /auth/csrf itself needs a session, so asking for a token before
 * signing in would fail with 401 and the sign-in would never be sent.
 */
const ANONYMOUS_UNSAFE_PATHS = new Set(["/auth/login", "/auth/setup"]);

export type Query = Record<string, string | number | boolean | null | undefined>;

export type RequestOptions = {
  query?: Query;
  /** JSON body. Serialised with JSON.stringify. */
  body?: unknown;
  signal?: AbortSignal;
  headers?: Record<string, string>;
};

export type ApiClientOptions = {
  basePath?: string;
  /** Injectable for tests. Defaults to the global fetch. */
  fetch?: typeof fetch;
};

export type ApiClient = {
  request<T = unknown>(method: string, path: string, opts?: RequestOptions): Promise<T>;
  get<T = unknown>(path: string, opts?: RequestOptions): Promise<T>;
  post<T = unknown>(path: string, body?: unknown, opts?: RequestOptions): Promise<T>;
  put<T = unknown>(path: string, body?: unknown, opts?: RequestOptions): Promise<T>;
  patch<T = unknown>(path: string, body?: unknown, opts?: RequestOptions): Promise<T>;
  delete<T = unknown>(path: string, opts?: RequestOptions): Promise<T>;
  /** Drop the cached CSRF token (call after login/logout: the session changed). */
  clearCsrfToken(): void;
};

function buildUrl(basePath: string, path: string, query?: Query): string {
  let url = `${basePath}${path.startsWith("/") ? path : `/${path}`}`;
  if (query) {
    const params = new URLSearchParams();
    for (const [k, v] of Object.entries(query)) {
      if (v !== undefined && v !== null) params.set(k, String(v));
    }
    const qs = params.toString();
    if (qs) url += `?${qs}`;
  }
  return url;
}

async function readProblem(res: Response): Promise<ApiError> {
  let problem: ProblemDetails | undefined;
  try {
    const text = await res.text();
    if (text) {
      const parsed: unknown = JSON.parse(text);
      if (parsed && typeof parsed === "object") problem = parsed as ProblemDetails;
    }
  } catch {
    /* not JSON (proxy error page, empty body) */
  }
  if (problem) return ApiError.fromProblem(res.status, problem);
  return new ApiError({
    status: res.status,
    code: `http.${res.status}`,
    title: res.statusText || `HTTP ${res.status}`,
  });
}

export function createApiClient(options: ApiClientOptions = {}): ApiClient {
  const basePath = options.basePath ?? API_BASE_PATH;
  const doFetch = (input: string, init: RequestInit) => {
    const f = options.fetch ?? globalThis.fetch;
    return f(input, init);
  };

  let csrfToken: string | null = null;
  let csrfInflight: Promise<string> | null = null;

  async function getCsrfToken(force = false): Promise<string> {
    if (!force && csrfToken) return csrfToken;
    if (csrfInflight) return csrfInflight;
    csrfInflight = (async () => {
      try {
        const body = await send<{ token: string }>("GET", "/auth/csrf", {}, false);
        if (!body || typeof body.token !== "string" || !body.token) {
          throw new ApiError({
            status: 500,
            code: "client.csrf_malformed",
            title: "Malformed CSRF response",
          });
        }
        csrfToken = body.token;
        return csrfToken;
      } finally {
        csrfInflight = null;
      }
    })();
    return csrfInflight;
  }

  async function send<T>(
    method: string,
    path: string,
    opts: RequestOptions,
    allowCsrfRetry: boolean,
  ): Promise<T> {
    const upper = method.toUpperCase();
    const unsafe = UNSAFE_METHODS.has(upper);

    const headers: Record<string, string> = {
      Accept: "application/json, application/problem+json",
      ...opts.headers,
    };
    let body: string | undefined;
    if (opts.body !== undefined) {
      headers["Content-Type"] = "application/json";
      body = JSON.stringify(opts.body);
    }
    if (unsafe && !(upper === "POST" && ANONYMOUS_UNSAFE_PATHS.has(path))) headers[CSRF_HEADER] = await getCsrfToken();

    let res: Response;
    try {
      res = await doFetch(buildUrl(basePath, path, opts.query), {
        method: upper,
        credentials: "same-origin",
        headers,
        body,
        signal: opts.signal,
      });
    } catch (e) {
      if (e instanceof DOMException && e.name === "AbortError") throw e;
      throw ApiError.network(e);
    }

    if (res.ok) {
      if (res.status === 204 || res.status === 205) return undefined as T;
      const text = await res.text();
      return (text ? JSON.parse(text) : undefined) as T;
    }

    const error = await readProblem(res);
    if (
      unsafe &&
      allowCsrfRetry &&
      error.status === 403 &&
      error.code === ProblemCodes.csrfInvalid
    ) {
      // The token is stale (session rotated, cookie expired): fetch a fresh
      // one and retry exactly once.
      csrfToken = null;
      await getCsrfToken(true);
      return send<T>(method, path, opts, false);
    }
    throw error;
  }

  const request: ApiClient["request"] = (method, path, opts = {}) =>
    send(method, path, opts, true);

  return {
    request,
    get: (path, opts) => request("GET", path, opts),
    post: (path, body, opts) => request("POST", path, { ...opts, body }),
    put: (path, body, opts) => request("PUT", path, { ...opts, body }),
    patch: (path, body, opts) => request("PATCH", path, { ...opts, body }),
    delete: (path, opts) => request("DELETE", path, opts),
    clearCsrfToken() {
      csrfToken = null;
    },
  };
}

/** App-wide singleton. Tests build their own with `createApiClient({ fetch })`. */
export const api = createApiClient();
