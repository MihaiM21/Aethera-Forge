/**
 * Auth endpoints. Hand-typed for now (contract fixed by the orchestrator, see
 * ADR 0003 section 7); once `pnpm gen:api` has produced schema.d.ts these can
 * be derived from it instead.
 */
import { api, type ApiClient } from "./client";

export type Role = "owner" | "admin" | "developer" | "viewer";

export type Me = {
  user: { id: string; email: string; displayName: string };
  organization: { id: string; name: string; slug: string };
  role: Role;
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

export function createAuthApi(client: ApiClient) {
  return {
    getSetupStatus: () => client.get<SetupStatus>("/auth/setup"),
    async setup(req: SetupRequest): Promise<Me> {
      const me = await client.post<Me>("/auth/setup", req);
      client.clearCsrfToken(); // new session, new antiforgery context
      return me;
    },
    async login(req: LoginRequest): Promise<Me> {
      const me = await client.post<Me>("/auth/login", req);
      client.clearCsrfToken();
      return me;
    },
    async logout(): Promise<void> {
      await client.post<void>("/auth/logout");
      client.clearCsrfToken();
    },
    me: (signal?: AbortSignal) => client.get<Me>("/auth/me", { signal }),
    csrf: () => client.get<{ token: string }>("/auth/csrf"),
  };
}

export const authApi = createAuthApi(api);
