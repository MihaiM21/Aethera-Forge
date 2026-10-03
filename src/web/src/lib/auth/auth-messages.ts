import { ApiError, ProblemCodes } from "@/lib/api";

/** Human text for an auth failure. Branches on `code`, never on the API's wording. */
export function describeAuthError(e: unknown): { message: string; traceId?: string } {
  if (!(e instanceof ApiError)) {
    return { message: "Something went wrong. Try again." };
  }
  switch (e.code) {
    case ProblemCodes.invalidCredentials:
      return { message: "Invalid email or password." };
    case ProblemCodes.lockedOut:
      return {
        message:
          "This account is temporarily locked after too many failed attempts. Try again later.",
      };
    case ProblemCodes.setupCompleted:
      return { message: "Setup has already been completed. Log in instead." };
    case ProblemCodes.validationFailed:
      return { message: "Some fields are invalid.", traceId: e.traceId };
    case ProblemCodes.csrfInvalid:
      return { message: "Your session security token expired. Reload the page and try again." };
    case "rate_limited":
      return { message: "Too many attempts. Wait a moment and try again." };
    default:
      if (e.isNetworkError) {
        return {
          message: "Control plane unavailable. Check that the Aethera API is running and retry.",
        };
      }
      return { message: "The request failed.", traceId: e.traceId };
  }
}
