"use client";

import * as React from "react";
import {
  ApiError,
  authApi,
  type LoginRequest,
  type Me,
  type SetupRequest,
} from "@/lib/api";
import { PREVIEW_ME, isPreviewActive } from "@/lib/preview";

export type AuthStatus =
  /** The first /auth/me has not completed yet. */
  | "loading"
  | "authenticated"
  /** The API answered 401 auth.unauthenticated. */
  | "unauthenticated"
  /** The API could not be reached (or failed). `me` is kept if we had one. */
  | "unavailable";

export type AuthContextValue = {
  status: AuthStatus;
  me: Me | null;
  error: ApiError | null;
  /** Re-check the session. Does not flip back to "loading". */
  refresh: () => Promise<void>;
  /** Starts the first load once; safe to call from many components. */
  ensureLoaded: () => void;
  login: (req: LoginRequest) => Promise<Me>;
  setup: (req: SetupRequest) => Promise<Me>;
  logout: () => Promise<void>;
};

const AuthContext = React.createContext<AuthContextValue | null>(null);

// Inlined at build time; must be a literal check in THIS module so the
// bundler can drop the preview branches from normal builds (see lib/preview.ts).
const PREVIEW = process.env.NEXT_PUBLIC_AETHERA_PREVIEW === "1";

/** How often the session / control-plane reachability is re-checked. */
const POLL_MS = 30_000;

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [status, setStatus] = React.useState<AuthStatus>("loading");
  const [me, setMe] = React.useState<Me | null>(null);
  const [error, setError] = React.useState<ApiError | null>(null);

  const inflight = React.useRef<Promise<void> | null>(null);
  const started = React.useRef(false);

  const refresh = React.useCallback((): Promise<void> => {
    if (inflight.current) return inflight.current;
    const run = (async () => {
      if (PREVIEW && isPreviewActive()) {
        setMe(PREVIEW_ME);
        setError(null);
        setStatus("authenticated");
        return;
      }
      try {
        const next = await authApi.me();
        setMe(next);
        setError(null);
        setStatus("authenticated");
      } catch (e) {
        if (e instanceof ApiError && e.status === 401) {
          setMe(null);
          setError(null);
          setStatus("unauthenticated");
        } else {
          const err =
            e instanceof ApiError ? e : ApiError.network(e);
          setError(err);
          setStatus("unavailable");
        }
      }
    })().finally(() => {
      inflight.current = null;
    });
    inflight.current = run;
    return run;
  }, []);

  const ensureLoaded = React.useCallback(() => {
    if (started.current) return;
    started.current = true;
    void refresh();
  }, [refresh]);

  // Keep the session and the "control plane unavailable" signal fresh.
  React.useEffect(() => {
    if (status === "loading" || status === "unauthenticated") return;
    const id = window.setInterval(() => void refresh(), POLL_MS);
    const onFocus = () => void refresh();
    window.addEventListener("focus", onFocus);
    return () => {
      window.clearInterval(id);
      window.removeEventListener("focus", onFocus);
    };
  }, [status, refresh]);

  const login = React.useCallback(async (req: LoginRequest) => {
    const next = await authApi.login(req);
    setMe(next);
    setError(null);
    setStatus("authenticated");
    return next;
  }, []);

  const setup = React.useCallback(async (req: SetupRequest) => {
    const next = await authApi.setup(req);
    setMe(next);
    setError(null);
    setStatus("authenticated");
    return next;
  }, []);

  const logout = React.useCallback(async () => {
    try {
      if (!(PREVIEW && isPreviewActive())) await authApi.logout();
    } catch {
      // A 401 means the session is already gone; any other failure still ends
      // the local session so the user is never stuck in the shell. If the
      // cookie is somehow still valid, the next /auth/me call will say so.
    }
    setMe(null);
    setError(null);
    setStatus("unauthenticated");
  }, []);

  const value = React.useMemo<AuthContextValue>(
    () => ({ status, me, error, refresh, ensureLoaded, login, setup, logout }),
    [status, me, error, refresh, ensureLoaded, login, setup, logout],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

/** Raw access without triggering the initial session check. */
export function useAuth(): AuthContextValue {
  const ctx = React.useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside <AuthProvider>");
  return ctx;
}

/** Current session. Triggers the first `GET /auth/me` on first use. */
export function useMe(): Pick<
  AuthContextValue,
  "status" | "me" | "error" | "refresh"
> {
  const { status, me, error, refresh, ensureLoaded } = useAuth();
  React.useEffect(() => {
    ensureLoaded();
  }, [ensureLoaded]);
  return { status, me, error, refresh };
}
