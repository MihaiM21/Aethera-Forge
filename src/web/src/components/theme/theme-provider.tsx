"use client";

import * as React from "react";
import {
  applyTheme,
  readThemePreference,
  systemTheme,
  writeThemePreference,
  type ResolvedTheme,
  type ThemePreference,
} from "@/lib/theme";

type ThemeContextValue = {
  /** What the user chose: light, dark or system. */
  preference: ThemePreference;
  /** What is actually applied to <html data-theme>. */
  resolved: ResolvedTheme;
  setPreference: (pref: ThemePreference) => void;
  /** Flip between light and dark (resolves "system" first). */
  toggle: () => void;
};

const ThemeContext = React.createContext<ThemeContextValue | null>(null);

const listeners = new Set<() => void>();
function emit() {
  listeners.forEach((l) => l());
}

function subscribePreference(cb: () => void) {
  listeners.add(cb);
  window.addEventListener("storage", cb);
  return () => {
    listeners.delete(cb);
    window.removeEventListener("storage", cb);
  };
}

function subscribeSystem(cb: () => void) {
  let mq: MediaQueryList | null = null;
  try {
    mq = window.matchMedia("(prefers-color-scheme: light)");
    mq.addEventListener("change", cb);
  } catch {
    /* matchMedia unavailable */
  }
  return () => mq?.removeEventListener("change", cb);
}

export function ThemeProvider({ children }: { children: React.ReactNode }) {
  const preference = React.useSyncExternalStore<ThemePreference>(
    subscribePreference,
    readThemePreference,
    () => "dark",
  );
  const system = React.useSyncExternalStore<ResolvedTheme>(
    subscribeSystem,
    systemTheme,
    () => "dark",
  );
  const resolved: ResolvedTheme = preference === "system" ? system : preference;

  React.useEffect(() => {
    applyTheme(resolved);
  }, [resolved]);

  const setPreference = React.useCallback((pref: ThemePreference) => {
    writeThemePreference(pref);
    emit();
  }, []);

  const toggle = React.useCallback(() => {
    setPreference(resolved === "dark" ? "light" : "dark");
  }, [resolved, setPreference]);

  const value = React.useMemo(
    () => ({ preference, resolved, setPreference, toggle }),
    [preference, resolved, setPreference, toggle],
  );

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

export function useTheme(): ThemeContextValue {
  const ctx = React.useContext(ThemeContext);
  if (!ctx) throw new Error("useTheme must be used inside <ThemeProvider>");
  return ctx;
}
