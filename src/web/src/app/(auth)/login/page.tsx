"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { Loader2Icon } from "lucide-react";
import { BootLines } from "@/components/aethera/boot-lines";
import { Eyebrow } from "@/components/aethera/eyebrow";
import { TerminalCard } from "@/components/aethera/terminal-card";
import { AuthScreen } from "@/components/shell/auth-screen";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { authApi, ApiError } from "@/lib/api";
import { describeAuthError } from "@/lib/auth/auth-messages";
import { useAuth, useMe } from "@/lib/auth/auth-context";
import { readStorage, writeStorage } from "@/lib/storage";

const BOOT_FLAG = "aethera-booted";
const BOOT_LINES = [
  "connecting to control plane",
  "verifying runtime boundaries",
  "loading workspace",
];

type Phase = "init" | "boot" | "form";

const subscribeNever = () => () => {};

export default function LoginPage() {
  const router = useRouter();
  const { login } = useAuth();
  const { status } = useMe();

  // The boot screen plays once per browser session. `init` renders only the
  // grid so server HTML and first client render match.
  const alreadyBooted = React.useSyncExternalStore<boolean | null>(
    subscribeNever,
    () => readStorage(BOOT_FLAG, "session") === "1",
    () => null,
  );
  const [bootFinished, setBootFinished] = React.useState(false);
  const phase: Phase =
    alreadyBooted === null ? "init" : alreadyBooted || bootFinished ? "form" : "boot";
  const finishBoot = React.useCallback(() => {
    writeStorage(BOOT_FLAG, "1", "session");
    setBootFinished(true);
  }, []);

  // Already logged in -> dashboard. First run -> setup.
  React.useEffect(() => {
    if (status === "authenticated") router.replace("/dashboard");
  }, [status, router]);
  React.useEffect(() => {
    let cancelled = false;
    authApi
      .getSetupStatus()
      .then((s) => {
        if (!cancelled && s.setupRequired) router.replace("/setup");
      })
      .catch(() => {
        /* API unreachable: the form's own error handling covers it */
      });
    return () => {
      cancelled = true;
    };
  }, [router]);

  const [email, setEmail] = React.useState("");
  const [password, setPassword] = React.useState("");
  const [rememberMe, setRememberMe] = React.useState(false);
  const [pending, setPending] = React.useState(false);
  const [error, setError] = React.useState<{ message: string; traceId?: string } | null>(null);
  const [fieldErrors, setFieldErrors] = React.useState<{ email?: string; password?: string }>({});
  const rememberId = React.useId();

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (pending) return;
    setPending(true);
    setError(null);
    setFieldErrors({});
    try {
      await login({ email: email.trim(), password, rememberMe });
      router.replace("/dashboard");
    } catch (err) {
      setError(describeAuthError(err));
      if (err instanceof ApiError) {
        setFieldErrors({
          email: err.fieldError("/email"),
          password: err.fieldError("/password"),
        });
      }
      setPending(false);
    }
  }

  return (
    <AuthScreen>
      {phase === "boot" && (
        <TerminalCard path="boot" label="01">
          <BootLines lines={BOOT_LINES} skippable onDone={finishBoot} />
        </TerminalCard>
      )}

      {phase === "form" && (
        <div className="flex flex-col gap-8">
          <div className="flex flex-col gap-3">
            <Eyebrow>Sign in</Eyebrow>
            <h1 className="text-4xl leading-[1.05] font-medium tracking-display">
              Your infrastructure,
              <br />
              <em className="text-lime not-italic">under control.</em>
            </h1>
          </div>

          <TerminalCard path="auth/login" label="01">
            <form
              onSubmit={onSubmit}
              noValidate
              className="grid gap-4 font-sans text-base leading-normal"
            >
              {error && (
                <div
                  role="alert"
                  className="border border-danger/60 bg-danger-soft px-3 py-2 text-sm text-danger"
                >
                  <p>{error.message}</p>
                  {error.traceId && (
                    <p className="mt-1 font-mono text-2xs opacity-80">trace {error.traceId}</p>
                  )}
                </div>
              )}

              <Field label="Email" error={fieldErrors.email}>
                {(c) => (
                  <Input
                    {...c}
                    type="email"
                    name="email"
                    autoComplete="username"
                    autoFocus
                    required
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    placeholder="you@company.com"
                  />
                )}
              </Field>

              <Field label="Password" error={fieldErrors.password}>
                {(c) => (
                  <Input
                    {...c}
                    type="password"
                    name="password"
                    autoComplete="current-password"
                    required
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                  />
                )}
              </Field>

              <div className="flex items-center gap-2">
                <Checkbox
                  id={rememberId}
                  checked={rememberMe}
                  onCheckedChange={(v) => setRememberMe(v === true)}
                />
                <Label htmlFor={rememberId} className="normal-case tracking-normal">
                  Remember me
                </Label>
              </div>

              <Button type="submit" size="lg" disabled={pending || !email || !password}>
                {pending && <Loader2Icon className="animate-spin" aria-hidden="true" />}
                Log in
              </Button>
            </form>
          </TerminalCard>
        </div>
      )}
    </AuthScreen>
  );
}
