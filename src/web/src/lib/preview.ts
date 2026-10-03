import type { Me } from "@/lib/api/auth";
import { readStorage, writeStorage } from "./storage";

/**
 * Preview mode: lets the static build render authenticated pages without an
 * API (design review, screenshots). It only activates when the bundle is built
 * with NEXT_PUBLIC_AETHERA_PREVIEW=1 (`pnpm build:preview`). In a normal build
 * the flag compiles to `false`, `isPreviewActive()` always returns false and
 * ignores `?preview=1`; the mock identity below stays in the bundle but is
 * unreachable. Never deploy a preview build.
 *
 * Activate with `?preview=1`; the choice sticks for the browser session.
 */
export const PREVIEW_BUILD = process.env.NEXT_PUBLIC_AETHERA_PREVIEW === "1";

const KEY = "aethera-preview";

export function isPreviewActive(): boolean {
  if (!PREVIEW_BUILD || typeof window === "undefined") return false;
  try {
    const q = new URLSearchParams(window.location.search).get("preview");
    if (q === "1") writeStorage(KEY, "1", "session");
    if (q === "0") writeStorage(KEY, "0", "session");
  } catch {
    /* ignore */
  }
  return readStorage(KEY, "session") === "1";
}

export const PREVIEW_ME: Me = {
  user: { id: "preview", email: "owner@aethera.local", displayName: "Preview Owner" },
  organization: { id: "preview-org", name: "Aethera Lab", slug: "aethera-lab" },
  role: "owner",
};
