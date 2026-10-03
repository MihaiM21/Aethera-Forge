import { readStorage, writeStorage } from "./storage";

export type ThemePreference = "light" | "dark" | "system";
export type ResolvedTheme = "light" | "dark";

export const THEME_STORAGE_KEY = "aethera-theme";

export function isThemePreference(v: unknown): v is ThemePreference {
  return v === "light" || v === "dark" || v === "system";
}

// Fallback for when storage is unavailable, so the toggle still works for the
// lifetime of the page.
let memoryPreference: ThemePreference | null = null;

export function readThemePreference(): ThemePreference {
  const v = readStorage(THEME_STORAGE_KEY);
  if (isThemePreference(v)) return v;
  return memoryPreference ?? "dark";
}

export function writeThemePreference(pref: ThemePreference): void {
  memoryPreference = pref;
  writeStorage(THEME_STORAGE_KEY, pref);
}

export function systemTheme(): ResolvedTheme {
  try {
    return window.matchMedia("(prefers-color-scheme: light)").matches
      ? "light"
      : "dark";
  } catch {
    return "dark";
  }
}

export function resolveTheme(pref: ThemePreference): ResolvedTheme {
  return pref === "system" ? systemTheme() : pref;
}

export function applyTheme(theme: ResolvedTheme): void {
  const el = document.documentElement;
  el.setAttribute("data-theme", theme);
  el.style.colorScheme = theme;
}

/**
 * Inline script for the root layout. Runs synchronously during HTML parsing,
 * before first paint, so there is no flash of the wrong theme. Keep it
 * dependency free and in sync with the helpers above (dark is the fallback).
 */
export const THEME_INIT_SCRIPT = `(function(){var t="dark";try{var p=localStorage.getItem("${THEME_STORAGE_KEY}");if(p==="light"||p==="dark"){t=p}else if(p==="system"){t=window.matchMedia("(prefers-color-scheme: light)").matches?"light":"dark"}}catch(e){}var d=document.documentElement;d.setAttribute("data-theme",t);d.style.colorScheme=t})();`;
