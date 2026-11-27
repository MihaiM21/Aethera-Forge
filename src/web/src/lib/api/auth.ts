/**
 * Auth endpoints. Hand-typed for now (contract fixed by the orchestrator, see
 * ADR 0003 section 7); once `pnpm gen:api` has produced schema.d.ts these can
 * be derived from it instead.
 */
import { api, type ApiClient } from "./client";
import { ApiError, ProblemCodes } from "./errors";

export type Role = "owner" | "admin" | "developer" | "viewer";

export type Me = {
  user: { id: string; email: string; displayName: string };
  organization: { id: string; name: string; slug: string };
  role: Role;
  /** The user has a confirmed authenticator (ADR 0013). */
  twoFactorEnabled?: boolean;
  /** The organization requires two-factor authentication and this session must enrol before anything else. */
  twoFactorEnrolmentRequired?: boolean;
};

export type SetupStatus = { setupRequired: boolean };

export type SetupRequest = {
  email: string;
  password: string;
  displayName: string;
  organizationName: string;
};

export type LoginRequest = {
  email: string;
  password: string;
  rememberMe: boolean;
};

/** The second sign-in step: the challenge from `auth.two_factor_required` and a code or a recovery code. */
export type TwoFactorLoginRequest = {
  challenge: string;
  code?: string;
  recoveryCode?: string;
};

/** What `POST /auth/login` answers instead of a session when the account has two-factor authentication. */
export type TwoFactorChallenge = { challenge: string; expiresIn: number };

/** The challenge of a `401 auth.two_factor_required`, or null for any other error. */
export function twoFactorChallengeOf(e: unknown): TwoFactorChallenge | null {
  if (!(e instanceof ApiError) || e.code !== ProblemCodes.twoFactorRequired) return null;
  const challenge = e.problem?.challenge;
  const expiresIn = e.problem?.expiresIn;
  if (typeof challenge !== "string" || !challenge) return null;
  return { challenge, expiresIn: typeof expiresIn === "number" ? expiresIn : 300 };
}

export type TwoFactorStatus = {
  enabled: boolean;
  enabledAt?: string | null;
  enrolmentPending: boolean;
  recoveryCodesRemaining: number;
  requiredByOrganization: boolean;
};

export type TwoFactorEnrolment = {
  secret: string;
  otpauthUri: string;
  issuer: string;
  accountName: string;
  digits: number;
  period: number;
};

export type RecoveryCodes = { recoveryCodes: string[] };

export function createAuthApi(client: ApiClient) {
  return {
    getSetupStatus: () => client.get<SetupStatus>("/auth/setup"),
    async setup(req: SetupRequest): Promise<Me> {
      const me = await client.post<Me>("/auth/setup", req);
      client.clearCsrfToken(); // new session, new antiforgery context
      return me;
    },
    /** Throws ApiError `auth.two_factor_required` (see twoFactorChallengeOf) when a code is needed next. */
    async login(req: LoginRequest): Promise<Me> {
      const me = await client.post<Me>("/auth/login", req);
      client.clearCsrfToken();
      return me;
    },
    async loginTwoFactor(req: TwoFactorLoginRequest): Promise<Me> {
      const me = await client.post<Me>("/auth/login/two-factor", req);
      client.clearCsrfToken();
      return me;
    },
    async logout(): Promise<void> {
      await client.post<void>("/auth/logout");
      client.clearCsrfToken();
    },
    me: (signal?: AbortSignal) => client.get<Me>("/auth/me", { signal }),
    csrf: () => client.get<{ token: string }>("/auth/csrf"),
    twoFactor: {
      status: (signal?: AbortSignal) => client.get<TwoFactorStatus>("/auth/two-factor", { signal }),
      startEnrolment: (password: string) => client.post<TwoFactorEnrolment>("/auth/two-factor/enrolment", { password }),
      confirmEnrolment: (code: string) => client.post<RecoveryCodes>("/auth/two-factor/enrolment/confirm", { code }),
      disable: (body: { password: string; code?: string; recoveryCode?: string }) => client.post<void>("/auth/two-factor/disable", body),
      regenerateRecoveryCodes: (password: string, code: string) =>
        client.post<RecoveryCodes>("/auth/two-factor/recovery-codes", { password, code }),
      setPolicy: (required: boolean) => client.put<TwoFactorStatus>("/auth/two-factor/policy", { required }),
    },
  };
}

export type AuthApi = ReturnType<typeof createAuthApi>;

export const authApi = createAuthApi(api);
