"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { BootLines } from "@/components/aethera/boot-lines";
import { DotGrid } from "@/components/aethera/dot-grid";
import { TerminalCard } from "@/components/aethera/terminal-card";
import { Button } from "@/components/ui/button";
import { useMe } from "./auth-context";

function FullScreen({ children }: { children: React.ReactNode }) {
  return (
    <DotGrid className="flex min-h-dvh items-center justify-center p-6">
      <div className="w-full max-w-md">{children}</div>
    </DotGrid>
  );
}

/**
 * Client-side route guard for the app shell (there is no middleware in a
 * static export). Renders children only once a session is known:
 *  - loading: boot card
 *  - unauthenticated: redirect to /login
 *  - unavailable with no cached session: error card with retry
 *  - unavailable with a cached session: keep rendering (the status bar shows it)
 */
export function AuthGuard({ children }: { children: React.ReactNode }) {
  const { status, me, refresh } = useMe();
  const router = useRouter();

  React.useEffect(() => {
    if (status === "unauthenticated") router.replace("/login");
  }, [status, router]);

  if (status === "authenticated" || (status === "unavailable" && me)) {
    return <>{children}</>;
  }

  if (status === "unavailable") {
    return (
      <FullScreen>
        <TerminalCard path="boot" label="!!">
          <BootLines
            lines={[{ text: "control plane unreachable", status: "fail" }]}
            readyLabel="waiting"
          />
          <p className="mt-4 font-sans text-sm leading-normal text-muted-foreground">
            Aethera could not reach its API. Check that the control plane is running, then retry.
          </p>
          <Button className="mt-4" onClick={() => void refresh()}>
            Retry
          </Button>
        </TerminalCard>
      </FullScreen>
    );
  }

  return (
    <FullScreen>
      <TerminalCard path="boot" label="01">
        <div role="status" aria-label="Checking session" className="font-mono text-xs text-muted-foreground">
          [ .. ] checking session
        </div>
      </TerminalCard>
    </FullScreen>
  );
}
