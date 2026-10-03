/**
 * RFC 9457 ProblemDetails as produced by the Aethera API (ADR 0003 section 3).
 * Clients branch on `code`, never on `title` / `detail`.
 */

export type FieldError = {
  /** JSON Pointer into the request body (e.g. `/email`). */
  pointer?: string;
  /** Query or path parameter name. */
  parameter?: string;
  /** Short stable validator id: required, too_short, pattern, range, ... */
  code?: string;
  /** Human-readable message for this field. */
  message?: string;
};

export type ProblemDetails = {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  code?: string;
  traceId?: string;
  errors?: FieldError[];
  [extension: string]: unknown;
};

/** Stable codes the UI branches on. The full catalogue lives in the API. */
export const ProblemCodes = {
  unauthenticated: "auth.unauthenticated",
  invalidCredentials: "auth.invalid_credentials",
  lockedOut: "auth.locked_out",
  csrfInvalid: "auth.csrf_invalid",
  setupCompleted: "auth.setup_completed",
  validationFailed: "validation.failed",
  confirmationRequired: "confirmation.required",
  /** Client-side: the request never produced an HTTP response. */
  network: "client.network_error",
} as const;

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly title: string;
  readonly detail?: string;
  readonly traceId?: string;
  readonly errors: FieldError[];
  readonly problem?: ProblemDetails;

  constructor(init: {
    status: number;
    code: string;
    title?: string;
    detail?: string;
    traceId?: string;
    errors?: FieldError[];
    problem?: ProblemDetails;
  }) {
    super(init.detail ?? init.title ?? init.code);
    this.name = "ApiError";
    this.status = init.status;
    this.code = init.code;
    this.title = init.title ?? init.code;
    this.detail = init.detail;
    this.traceId = init.traceId;
    this.errors = init.errors ?? [];
    this.problem = init.problem;
  }

  /** No HTTP response at all: control plane down, offline, CORS, DNS... */
  get isNetworkError(): boolean {
    return this.code === ProblemCodes.network;
  }

  /** Field-level message for a JSON Pointer (`/email`), if the API sent one. */
  fieldError(pointer: string): string | undefined {
    return this.errors.find((e) => e.pointer === pointer)?.message;
  }

  static fromProblem(status: number, problem: ProblemDetails): ApiError {
    return new ApiError({
      status: typeof problem.status === "number" ? problem.status : status,
      code: typeof problem.code === "string" && problem.code ? problem.code : `http.${status}`,
      title: typeof problem.title === "string" ? problem.title : undefined,
      detail: typeof problem.detail === "string" ? problem.detail : undefined,
      traceId: typeof problem.traceId === "string" ? problem.traceId : undefined,
      errors: Array.isArray(problem.errors) ? problem.errors : [],
      problem,
    });
  }

  static network(cause?: unknown): ApiError {
    const err = new ApiError({
      status: 0,
      code: ProblemCodes.network,
      title: "Control plane unavailable",
      detail: "The request did not reach the Aethera API.",
    });
    if (cause !== undefined) (err as { cause?: unknown }).cause = cause;
    return err;
  }
}

export function isApiError(e: unknown): e is ApiError {
  return e instanceof ApiError;
}
