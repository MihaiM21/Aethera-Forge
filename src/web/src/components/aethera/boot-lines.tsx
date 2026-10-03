"use client";

import * as React from "react";
import { cn } from "@/lib/utils";

export type BootLine = { text: string; status?: "ok" | "wait" | "fail" };

/** Matches --ae-duration-boot (ms) and the line fade-in. */
const LINE_MS = 420;
const FADE_MS = 180;

function prefersReducedMotion(): boolean {
  try {
    return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  } catch {
    return false;
  }
}

/**
 * `[ ok ] loading ...` lines that appear one by one, then `▮ ready`.
 * Under prefers-reduced-motion everything renders at once and `onDone` fires
 * immediately. `skippable` lets any key press or click finish early.
 */
export function BootLines({
  lines,
  readyLabel = "ready",
  onDone,
  skippable = false,
  className,
}: {
  lines: Array<string | BootLine>;
  readyLabel?: string;
  onDone?: () => void;
  skippable?: boolean;
  className?: string;
}) {
  const items: BootLine[] = lines.map((l) => (typeof l === "string" ? { text: l } : l));
  const total = (items.length + 1) * LINE_MS + FADE_MS;
  const onDoneRef = React.useRef(onDone);
  React.useEffect(() => {
    onDoneRef.current = onDone;
  });

  React.useEffect(() => {
    let done = false;
    const finish = () => {
      if (done) return;
      done = true;
      onDoneRef.current?.();
    };
    if (prefersReducedMotion()) {
      finish();
      return;
    }
    const t = window.setTimeout(finish, total);
    const skip = skippable ? () => finish() : null;
    if (skip) {
      window.addEventListener("keydown", skip);
      window.addEventListener("pointerdown", skip);
    }
    return () => {
      window.clearTimeout(t);
      if (skip) {
        window.removeEventListener("keydown", skip);
        window.removeEventListener("pointerdown", skip);
      }
    };
  }, [total, skippable]);

  return (
    <div
      data-slot="boot-lines"
      role="status"
      aria-label="Starting Aethera"
      className={cn("flex flex-col", className)}
    >
      {items.map((line, i) => (
        <div
          key={`${i}-${line.text}`}
          className="ae-boot-line"
          data-status={line.status && line.status !== "ok" ? line.status : undefined}
          style={{ "--i": i } as React.CSSProperties}
        >
          {line.text}
        </div>
      ))}
      <div
        className="ae-boot-line mt-3 text-muted-foreground before:content-none"
        style={{ "--i": items.length } as React.CSSProperties}
      >
        <span className="ae-boot-block mr-2" aria-hidden="true" />
        {readyLabel}
      </div>
    </div>
  );
}
