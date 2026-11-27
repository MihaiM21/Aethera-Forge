"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { Eyebrow } from "@/components/aethera/eyebrow";
import { TerminalCard } from "@/components/aethera/terminal-card";
import { TwoFactorEnrolmentFlow } from "@/components/account/two-factor";
import { AuthScreen } from "@/components/shell/auth-screen";
import { Button } from "@/components/ui/button";
import { useAuth, useMe } from "@/lib/auth/auth-context";

/**
 * Where a member lands when the organization requires two-factor authentication and they have none yet (ADR 0013). The API refuses
 * everything else for this session, so the enrolment runs outside the app shell.
 */
export default function TwoFactorEnrolmentPage() {
  const router = useRouter();
  const { status, me, refresh } = useMe();
  const { logout } = useAuth();

  const required = status === "authenticated" && me?.twoFactorEnrolmentRequired === true;
  // Latched: once the flow is showing, a refresh of `me` (every 30 s and on window focus) that already says "enrolled" must not swap it out
  // while the recovery codes are on screen. Only the flow's own "done" leaves this page then.
  const [started, setStarted] = React.useState(false);
  if (required && !started) setStarted(true);

  React.useEffect(() => {
    if (status === "unauthenticated") router.replace("/login");
    else if (status === "authenticated" && me && !me.twoFactorEnrolmentRequired && !started) router.replace("/dashboard");
  }, [status, me, started, router]);

  return (
    <AuthScreen>
      <div className="flex flex-col gap-8">
        <div className="flex flex-col gap-3">
          <Eyebrow>Two-factor authentication</Eyebrow>
          <h1 className="text-4xl leading-[1.05] font-medium tracking-display">
            Secure your
            <br />
            <em className="text-lime not-italic">account.</em>
          </h1>
          <p className="text-sm text-muted-foreground">
            {me?.organization.name ?? "Your organization"} requires two-factor authentication. Set up an authenticator app to continue.
          </p>
        </div>
        <TerminalCard path="auth/two-factor/setup" label="01">
          {required || started ? (
            <div className="font-sans text-base leading-normal">
              <TwoFactorEnrolmentFlow
                onDone={async () => {
                  await refresh();
                  router.replace("/dashboard");
                }}
              />
            </div>
          ) : (
            <div role="status" className="font-mono text-xs text-muted-foreground">
              [ .. ] checking session
            </div>
          )}
        </TerminalCard>
        <div>
          <Button
            variant="ghost"
            size="sm"
            onClick={async () => {
              await logout();
              router.replace("/login");
            }}
          >
            Sign out
          </Button>
        </div>
      </div>
    </AuthScreen>
  );
}
