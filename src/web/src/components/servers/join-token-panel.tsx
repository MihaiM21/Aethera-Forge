"use client";

import * as React from "react";
import { EyeOffIcon, KeyRoundIcon } from "lucide-react";
import { TerminalCard } from "@/components/aethera/terminal-card";
import { Button } from "@/components/ui/button";
import { formatCountdown, toDate } from "@/lib/servers/format";
import type { JoinToken } from "@/lib/servers/types";
import { CopyButton, Facts } from "./common";

/** Re-renders every `ms` with the current time. */
export function useNow(ms = 1000): Date {
  const [now, setNow] = React.useState(() => new Date());
  React.useEffect(() => {
    const t = setInterval(() => setNow(new Date()), ms);
    return () => clearInterval(t);
  }, [ms]);
  return now;
}

/**
 * Shows a freshly issued join token. The API returns the token once (only its
 * hash is stored), so this panel is the only place it ever exists: it lives in
 * React state of the caller, is never written to storage or the URL, and is
 * dropped when the user hides it or when it expires (`onDismiss`).
 */
export function JoinTokenPanel({
  token,
  onDismiss,
}: {
  token: JoinToken;
  /** Called when the user hides the token or it expires; the caller must discard it. */
  onDismiss: (reason: "hidden" | "expired") => void;
}) {
  const now = useNow();
  const expiresAt = toDate(token.expiresAt);
  const expired = expiresAt !== null && expiresAt.getTime() <= now.getTime();
  const dismissRef = React.useRef(onDismiss);
  React.useEffect(() => {
    dismissRef.current = onDismiss;
  });
  React.useEffect(() => {
    if (expired) dismissRef.current("expired");
  }, [expired]);

  return (
    <TerminalCard path="servers/join" label={expired ? "EXPIRED" : "ONE-TIME"} className="w-full">
      <div className="grid gap-4 font-sans">
        <p className="flex items-start gap-2 text-xs text-warning">
          <KeyRoundIcon className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          <span>
            This token is shown once and cannot be retrieved later. Copy the command now. It works a single time and
            expires in{" "}
            <time className="font-mono" role="timer" aria-live="off">
              {formatCountdown(token.expiresAt, now)}
            </time>
            .
          </span>
        </p>

        <div className="grid gap-1.5">
          <div className="flex items-center justify-between gap-2">
            <span className="font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Install command</span>
            <CopyButton text={token.installCommand} label="Copy command" aria-label="Copy install command" />
          </div>
          <pre
            tabIndex={0}
            aria-label="Install command"
            className="ae-log focus-ring overflow-x-auto border border-border bg-background px-3 py-2 text-xs whitespace-pre-wrap break-all"
          >
            {token.installCommand}
          </pre>
          <p className="text-2xs text-muted-foreground">
            Run it as root on the server. The agent connects out to the endpoint below and pins the control plane CA.
          </p>
        </div>

        <div className="grid gap-1.5">
          <div className="flex items-center justify-between gap-2">
            <span className="font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Join token</span>
            <CopyButton text={token.token} label="Copy token" aria-label="Copy join token" />
          </div>
          <code
            aria-label="Join token"
            className="block overflow-x-auto border border-border bg-background px-3 py-2 font-mono text-xs break-all"
          >
            {token.token}
          </code>
        </div>

        <Facts
          rows={[
            ["endpoint", token.endpoint],
            ["CA sha256", token.caFingerprintSha256],
            ["expires", expiresAt ? expiresAt.toLocaleString() : "-"],
          ]}
        />

        <div>
          <Button variant="ghost" size="sm" onClick={() => onDismiss("hidden")}>
            <EyeOffIcon aria-hidden="true" /> Hide token
          </Button>
        </div>
      </div>
    </TerminalCard>
  );
}
