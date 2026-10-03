"use client";

import * as React from "react";
import { StatusDot, type Tone } from "@/components/aethera/status-dot";
import {
  useConnectionStatus,
  type ConnectionStatus,
} from "@/hooks/use-connection-status";
import { cn } from "@/lib/utils";
import pkg from "../../../package.json";

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** Presentational status bar (also used by the /design gallery). */
export function StatusBarView({
  status,
  className,
}: {
  status: ConnectionStatus;
  className?: string;
}) {
  const { state, servers, jobsRunning } = status;
  const tone: Tone =
    state === "connected" ? "success" : state === "unavailable" ? "danger" : state === "reconnecting" ? "warning" : "idle";

  let text: React.ReactNode;
  if (state === "unavailable") {
    text = <span className="text-danger">Control plane unavailable</span>;
  } else if (state === "connecting") {
    text = <span>Connecting…</span>;
  } else if (state === "reconnecting") {
    text = <span className="text-warning">Reconnecting…</span>;
  } else {
    text = (
      <>
        <span>Connected</span>
        {servers !== null && (
          <>
            <Sep />
            <span>{plural(servers, "server", "servers")}</span>
          </>
        )}
        {jobsRunning !== null && (
          <span className="hidden items-center gap-1.5 md:flex">
            <Sep />
            <span>{plural(jobsRunning, "job", "jobs")} running</span>
          </span>
        )}
      </>
    );
  }

  return (
    <footer
      data-slot="status-bar"
      className={cn(
        "flex h-[var(--ae-statusbar-height)] shrink-0 items-center justify-between gap-4 border-t border-sidebar-border bg-sidebar px-3 font-mono text-xs text-muted-foreground",
        className,
      )}
    >
      <div
        role="status"
        aria-live="polite"
        className="flex min-w-0 items-center gap-2 whitespace-nowrap"
      >
        <StatusDot
          tone={tone}
          live={state === "connected"}
          label={state === "connected" ? "Connected" : state}
        />
        <span className="flex items-center gap-1.5 truncate">{text}</span>
      </div>
      <span className="hidden shrink-0 sm:inline">v{pkg.version}</span>
    </footer>
  );
}

function Sep() {
  return (
    <span aria-hidden="true">·</span>
  );
}

export function StatusBar() {
  const status = useConnectionStatus();
  return <StatusBarView status={status} />;
}
