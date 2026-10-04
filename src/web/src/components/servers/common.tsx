"use client";

import * as React from "react";
import { AlertTriangleIcon, CheckIcon, CopyIcon, PlugZapIcon, RefreshCwIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { fieldBase } from "@/components/ui/input";
import { errorMessage, isHostUnavailable } from "@/lib/servers/errors";
import { cn } from "@/lib/utils";

/** Native select in the field style (labels and keyboard handling for free). */
export function NativeSelect({ className, ...props }: React.ComponentProps<"select">) {
  return <select data-slot="native-select" className={cn(fieldBase, "h-8 pr-8", className)} {...props} />;
}

export function ErrorPanel({
  error,
  onRetry,
  title = "Could not load",
  className,
}: {
  error: unknown;
  onRetry?: () => void;
  title?: string;
  className?: string;
}) {
  return (
    <div
      role="alert"
      className={cn("flex flex-wrap items-start gap-3 border border-danger/60 bg-danger-soft px-4 py-3 text-sm", className)}
    >
      <AlertTriangleIcon className="mt-0.5 size-4 shrink-0 text-danger" aria-hidden="true" />
      <div className="min-w-0 flex-1">
        <p className="font-medium text-danger">{title}</p>
        <p className="text-xs text-muted-foreground">{errorMessage(error)}</p>
      </div>
      {onRetry && (
        <Button size="sm" variant="outline" onClick={onRetry}>
          <RefreshCwIcon aria-hidden="true" /> Retry
        </Button>
      )}
    </div>
  );
}

/**
 * Live Docker inventory is answered by the host's agent. When the API says the
 * agent (or host) cannot answer (503 `server.agent_unavailable`...), explain it
 * instead of showing a generic failure.
 */
export function InventoryError({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  if (isHostUnavailable(error)) {
    return (
      <div role="status" className="flex flex-wrap items-start gap-3 border border-dashed border-warning/70 bg-warning-soft px-4 py-3 text-sm">
        <PlugZapIcon className="mt-0.5 size-4 shrink-0 text-warning" aria-hidden="true" />
        <div className="min-w-0 flex-1">
          <p className="font-medium text-warning">Agent unavailable</p>
          <p className="text-xs text-muted-foreground">
            This list is read live from the server&apos;s agent, and the agent cannot answer right now. Check the
            status axes on the Overview tab; last known discovery data stays available there.
          </p>
        </div>
        {onRetry && (
          <Button size="sm" variant="outline" onClick={onRetry}>
            <RefreshCwIcon aria-hidden="true" /> Retry
          </Button>
        )}
      </div>
    );
  }
  return <ErrorPanel error={error} onRetry={onRetry} title="Could not read the inventory" />;
}

export async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    /* fall through to the legacy path */
  }
  try {
    const ta = document.createElement("textarea");
    ta.value = text;
    ta.setAttribute("readonly", "");
    ta.style.position = "fixed";
    ta.style.opacity = "0";
    document.body.appendChild(ta);
    ta.select();
    const ok = document.execCommand("copy");
    document.body.removeChild(ta);
    return ok;
  } catch {
    return false;
  }
}

export function CopyButton({
  text,
  label = "Copy",
  className,
  ...props
}: Omit<React.ComponentProps<typeof Button>, "onClick" | "children"> & { text: string; label?: string }) {
  const [copied, setCopied] = React.useState(false);
  const timer = React.useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  React.useEffect(() => () => clearTimeout(timer.current), []);
  return (
    <Button
      variant="outline"
      size="sm"
      className={className}
      onClick={async () => {
        const ok = await copyText(text);
        setCopied(ok);
        clearTimeout(timer.current);
        timer.current = setTimeout(() => setCopied(false), 2000);
      }}
      {...props}
    >
      {copied ? <CheckIcon aria-hidden="true" /> : <CopyIcon aria-hidden="true" />}
      <span aria-live="polite">{copied ? "Copied" : label}</span>
    </Button>
  );
}

/** `key  value` rows in mono, used for facts. */
export function Facts({ rows, className }: { rows: Array<[string, React.ReactNode]>; className?: string }) {
  return (
    <dl className={cn("grid grid-cols-[minmax(6rem,9rem)_1fr] gap-x-4 gap-y-1.5 text-sm", className)}>
      {rows.map(([k, v]) => (
        <React.Fragment key={k}>
          <dt className="font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground pt-0.5">{k}</dt>
          <dd className="min-w-0 break-words font-mono text-xs text-foreground">{v ?? "-"}</dd>
        </React.Fragment>
      ))}
    </dl>
  );
}

export function SectionTitle({ index, children, actions }: { index?: string; children: React.ReactNode; actions?: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-3 border-b border-border pb-2">
      <h3 className="flex items-baseline gap-3 text-base font-medium tracking-subheading">
        {index && <span className="font-mono text-2xs text-lime">{index}</span>}
        {children}
      </h3>
      {actions}
    </div>
  );
}
