"use client";

import { useSyncExternalStore } from "react";

/**
 * Detail pages are exported once per route with the placeholder id `_`
 * (docs/architecture/0005-web-routing.md), so `params.id` is always `_` in the
 * browser. The real id is the last path segment of the address bar.
 */
export const PLACEHOLDER_ID = "_";

function subscribe(cb: () => void) {
  window.addEventListener("popstate", cb);
  return () => window.removeEventListener("popstate", cb);
}

/** Last segment of a pathname, decoded; `null` for the placeholder or empty. */
export function idFromPathname(pathname: string): string | null {
  const segments = pathname.split("/").filter(Boolean);
  const last = segments[segments.length - 1];
  if (!last) return null;
  let id = last;
  try {
    id = decodeURIComponent(last);
  } catch {
    /* keep raw */
  }
  return id === PLACEHOLDER_ID ? null : id;
}

/** The resource id from the URL, or `null` on the server / for the placeholder. */
export function useResourceId(): string | null {
  return useSyncExternalStore(
    subscribe,
    () => idFromPathname(window.location.pathname),
    () => null,
  );
}
