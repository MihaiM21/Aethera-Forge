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
import { authApi, ApiError, twoFactorChallengeOf, type Me } from "@/lib/api";
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
  const { login, loginTwoFactor } = useAuth();
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

  // Second step (ADR 0013): the password was right and the account has two-factor authentication.
  const [challenge, setChallenge] = React.useState<string | null>(null);
  const [code, setCode] = React.useState("");
  const [useRecovery, setUseRecovery] = React.useState(false);

  // A session that must enrol an authenticator first goes to the enrolment screen.
  const landing = (me: Me) => router.replace(me.twoFactorEnrolmentRequired ? "/two-factor" : "/dashboard");

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (pending) return;
    setPending(true);
    setError(null);
    setFieldErrors({});
    try {
      landing(await login({ email: email.trim(), password, rememberMe }));
    } catch (err) {
      const next = twoFactorChallengeOf(err);
      if (next) {
        setChallenge(next.challenge);
        setCode("");
        setUseRecovery(false);
        setPending(false);
        return;
      }
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

  async function onSubmitCode(e: React.FormEvent) {
    e.preventDefault();
    if (pending || !challenge) return;
    setPending(true);
    setError(null);
    try {
      const value = code.trim();
      landing(await loginTwoFactor(useRecovery ? { challenge, recoveryCode: value } : { challenge, code: value }));
    } catch (err) {
      setError(describeAuthError(err));
      // An expired challenge (or a password changed meanwhile) means starting over with the password.
      if (err instanceof ApiError && (err.code === "auth.two_factor_challenge_invalid" || err.code === "auth.locked_out")) {
        setChallenge(null);
        setPassword("");
      }
      setCode("");
      setPending(false);
    }
  }

  function backToPassword() {
    setChallenge(null);
    setCode("");
    setError(null);
  }

  const errorBox = error && (
    <div role="alert" className="border border-danger/60 bg-danger-soft px-3 py-2 text-sm text-danger">
      <p>{error.message}</p>
      {error.traceId && <p className="mt-1 font-mono text-2xs opacity-80">trace {error.traceId}</p>}
    </div>
  );

  return (
    <AuthScreen>
      {phase === "boot" && (
        <TerminalCard path="boot" label="01">
          <BootLines lines={BOOT_LINES} skippable onDone={finishBoot} />
        </TerminalCard>
      )}

      {phase === "form" && challenge && (
        <div className="flex flex-col gap-8">
          <div className="flex flex-col gap-3">
            <Eyebrow>Two-factor authentication</Eyebrow>
            <h1 className="text-4xl leading-[1.05] font-medium tracking-display">
              One more
              <br />
              <em className="text-lime not-italic">step.</em>
            </h1>
          </div>

          <TerminalCard path="auth/two-factor" label="02">
            <form onSubmit={onSubmitCode} noValidate className="grid gap-4 font-sans text-base leading-normal">
              {errorBox}
              <p className="text-sm text-muted-foreground">
                {useRecovery
                  ? "Enter one of the recovery codes you saved when you set up two-factor authentication. Each code works once."
                  : "Enter the 6-digit code from your authenticator app."}
              </p>
              <Field label={useRecovery ? "Recovery code" : "Authentication code"}>
                {(c) => (
                  <Input
                    {...c}
                    key={useRecovery ? "recovery" : "totp"}
                    name={useRecovery ? "recoveryCode" : "code"}
                    autoComplete="one-time-code"
                    inputMode={useRecovery ? "text" : "numeric"}
                    autoFocus
                    required
                    className="font-mono tracking-widest"
                    placeholder={useRecovery ? "xxxx-xxxx-xxxx-xxxx" : "123456"}
                    value={code}
                    onChange={(e) => setCode(e.target.value)}
                  />
                )}
              </Field>
              <Button type="submit" size="lg" disabled={pending || !code.trim()}>
                {pending && <Loader2Icon className="animate-spin" aria-hidden="true" />}
                Verify
              </Button>
              <div className="flex flex-wrap items-center justify-between gap-2 font-mono text-2xs uppercase tracking-wider">
                <button
                  type="button"
                  className="text-muted-foreground underline-offset-4 hover:text-foreground hover:underline"
                  onClick={() => {
                    setUseRecovery((v) => !v);
                    setCode("");
                    setError(null);
                  }}
                >
                  {useRecovery ? "Use the authenticator app" : "Use a recovery code"}
                </button>
                <button
                  type="button"
                  className="text-muted-foreground underline-offset-4 hover:text-foreground hover:underline"
                  onClick={backToPassword}
                >
                  Back
                </button>
              </div>
            </form>
          </TerminalCard>
        </div>
      )}

      {phase === "form" && !challenge && (
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
              {errorBox}

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
