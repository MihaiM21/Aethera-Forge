/**
 * Browser storage helpers. localStorage/sessionStorage can be missing or throw
 * (private windows, blocked site data, SSR), so every access is wrapped and the
 * UI must render correctly without it.
 */

type Kind = "local" | "session";

function area(kind: Kind): Storage | null {
  try {
    if (typeof window === "undefined") return null;
    return kind === "local" ? window.localStorage : window.sessionStorage;
  } catch {
    return null;
  }
}

export function readStorage(key: string, kind: Kind = "local"): string | null {
  try {
    return area(kind)?.getItem(key) ?? null;
  } catch {
    return null;
  }
}

export function writeStorage(
  key: string,
  value: string,
  kind: Kind = "local",
): boolean {
  try {
    const a = area(kind);
    if (!a) return false;
    a.setItem(key, value);
    return true;
  } catch {
    return false;
  }
}

export function removeStorage(key: string, kind: Kind = "local"): void {
  try {
    area(kind)?.removeItem(key);
  } catch {
    /* ignore */
  }
}
