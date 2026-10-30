import type { StatusKey } from "@/components/ui/status-pill";
import type { PreviewStatus } from "./types";

/** Preview status -> the shared status pill and the text shown for it. */
export const PREVIEW_PILL: Record<PreviewStatus, { status: StatusKey; label: string }> = {
  pending: { status: "queued", label: "Waiting for a slot" },
  deploying: { status: "deploying", label: "Deploying" },
  running: { status: "running", label: "Running" },
  failed: { status: "failed", label: "Failed" },
  closing: { status: "stopped", label: "Removing" },
  removed: { status: "stopped", label: "Removed" },
};

/** A preview that can be deployed again or removed by hand. */
export const isLive = (s: PreviewStatus) => s !== "closing" && s !== "removed";
