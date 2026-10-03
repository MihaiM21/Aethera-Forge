"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { BootLines } from "@/components/aethera/boot-lines";
import { TerminalCard } from "@/components/aethera/terminal-card";
import { AuthScreen } from "@/components/shell/auth-screen";
import { Button } from "@/components/ui/button";
import { authApi } from "@/lib/api";
import { useMe } from "@/lib/auth/auth-context";

/**
 * Entry redirect (no server, so it runs in the browser):
 * setup required -> /setup, else logged out -> /login, else -> /dashboard.
 */
export default function Home() {
  const router = useRouter();
  const { status, refresh } = useMe();
  const [setupRequired, setSetupRequired] = React.useState<boolean | null>(null);
  const [setupFailed, setSetupFailed] = React.useState(false);
  const [attempt, setAttempt] = React.useState(0);

  React.useEffect(() => {
    let cancelled = false;
    authApi
      .getSetupStatus()
      .then((s) => !cancelled && setSetupRequired(s.setupRequired))
      .catch(() => !cancelled && setSetupFailed(true));
    return () => {
      cancelled = true;
    };
  }, [attempt]);

  React.useEffect(() => {
    if (setupRequired) {
      router.replace("/setup");
    } else if (setupRequired === false) {
      if (status === "authenticated") router.replace("/dashboard");
      else if (status === "unauthenticated") router.replace("/login");
    }
  }, [setupRequired, status, router]);

  const unavailable = setupFailed || (status === "unavailable" && setupRequired !== true);

  return (
    <AuthScreen>
      <TerminalCard path="boot" label="01">
        {unavailable ? (
          <>
            <BootLines
              lines={[{ text: "control plane unreachable", status: "fail" }]}
              readyLabel="waiting"
            />
            <p className="mt-4 font-sans text-sm leading-normal text-muted-foreground">
              Aethera could not reach its API. Check that the control plane is running.
            </p>
            <Button
              className="mt-4"
              onClick={() => {
                setSetupFailed(false);
                setAttempt((n) => n + 1);
                void refresh();
              }}
            >
              Retry
            </Button>
          </>
        ) : (
          <div role="status" aria-label="Loading" className="font-mono text-xs text-muted-foreground">
            [ .. ] contacting control plane
          </div>
        )}
      </TerminalCard>
    </AuthScreen>
  );
}
