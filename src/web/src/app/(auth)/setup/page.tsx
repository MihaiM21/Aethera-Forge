"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { Loader2Icon } from "lucide-react";
import { BootLines } from "@/components/aethera/boot-lines";
import { Eyebrow } from "@/components/aethera/eyebrow";
import { TerminalCard } from "@/components/aethera/terminal-card";
import { AuthScreen } from "@/components/shell/auth-screen";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { ApiError, ProblemCodes, authApi } from "@/lib/api";
import { describeAuthError } from "@/lib/auth/auth-messages";
import { useAuth } from "@/lib/auth/auth-context";

type Errors = Partial<
  Record<"displayName" | "email" | "password" | "confirm" | "organizationName", string>
>;

export default function SetupPage() {
  const router = useRouter();
  const { setup } = useAuth();

  const [introDone, setIntroDone] = React.useState(false);
  const [statusError, setStatusError] = React.useState<string | null>(null);

  // If an owner already exists, there is nothing to set up.
  React.useEffect(() => {
    let cancelled = false;
    authApi
      .getSetupStatus()
      .then((s) => {
        if (!cancelled && !s.setupRequired) router.replace("/login");
      })
      .catch((e) => {
        if (!cancelled) setStatusError(describeAuthError(e).message);
      });
    return () => {
      cancelled = true;
    };
  }, [router]);

  const [displayName, setDisplayName] = React.useState("");
  const [email, setEmail] = React.useState("");
  const [password, setPassword] = React.useState("");
  const [confirm, setConfirm] = React.useState("");
  const [organizationName, setOrganizationName] = React.useState("");
  const [errors, setErrors] = React.useState<Errors>({});
  const [formError, setFormError] = React.useState<{ message: string; traceId?: string } | null>(
    null,
  );
  const [pending, setPending] = React.useState(false);

  function validate(): Errors {
    const next: Errors = {};
    if (!displayName.trim()) next.displayName = "Enter your name.";
    if (!/^\S+@\S+\.\S+$/.test(email.trim())) next.email = "Enter a valid email address.";
    if (!password) next.password = "Choose a password.";
    if (confirm !== password) next.confirm = "Passwords do not match.";
    if (!organizationName.trim()) next.organizationName = "Name your organization.";
    return next;
  }

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (pending) return;
    const local = validate();
    setErrors(local);
    setFormError(null);
    if (Object.keys(local).length > 0) return;

    setPending(true);
    try {
      await setup({
        displayName: displayName.trim(),
        email: email.trim(),
        password,
        organizationName: organizationName.trim(),
      });
      router.replace("/dashboard");
    } catch (err) {
      setFormError(describeAuthError(err));
      if (err instanceof ApiError) {
        setErrors({
          displayName: err.fieldError("/displayName"),
          email: err.fieldError("/email"),
          password: err.fieldError("/password"),
          organizationName: err.fieldError("/organizationName"),
        });
        if (err.code === ProblemCodes.setupCompleted) {
          window.setTimeout(() => router.replace("/login"), 1500);
        }
      }
      setPending(false);
    }
  }

  return (
    <AuthScreen>
      <div className="flex flex-col gap-8">
        <div className="flex flex-col gap-3">
          <Eyebrow>First run</Eyebrow>
          <h1 className="text-4xl leading-[1.05] font-medium tracking-display">
            Create the owner account.
          </h1>
          <p className="max-w-md text-sm text-muted-foreground">
            This installation has no users yet. The first account becomes the owner of a new
            organization and can invite others later.
          </p>
        </div>

        <TerminalCard path="setup" label="01">
          <BootLines
            lines={[
              statusError
                ? { text: "control plane unavailable", status: "fail" }
                : "control plane reachable",
              "no owner account found",
              "ready to create the first owner",
            ]}
            readyLabel="awaiting input"
            onDone={() => setIntroDone(true)}
            className="mb-6"
          />

          {/* The form appears once the intro has played (immediately under reduced motion). */}
          {introDone && (
            <form
              onSubmit={onSubmit}
              noValidate
              className="grid gap-4 border-t border-border pt-6 font-sans text-base leading-normal"
            >
              {(formError || statusError) && (
                <div
                  role="alert"
                  className="border border-danger/60 bg-danger-soft px-3 py-2 text-sm text-danger"
                >
                  <p>{formError?.message ?? statusError}</p>
                  {formError?.traceId && (
                    <p className="mt-1 font-mono text-2xs opacity-80">trace {formError.traceId}</p>
                  )}
                </div>
              )}

              <Field label="Your name" error={errors.displayName}>
                {(c) => (
                  <Input
                    {...c}
                    name="displayName"
                    autoComplete="name"
                    autoFocus
                    value={displayName}
                    onChange={(e) => setDisplayName(e.target.value)}
                  />
                )}
              </Field>

              <Field label="Email" error={errors.email}>
                {(c) => (
                  <Input
                    {...c}
                    type="email"
                    name="email"
                    autoComplete="username"
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    placeholder="you@company.com"
                  />
                )}
              </Field>

              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Password" error={errors.password}>
                  {(c) => (
                    <Input
                      {...c}
                      type="password"
                      name="password"
                      autoComplete="new-password"
                      value={password}
                      onChange={(e) => setPassword(e.target.value)}
                    />
                  )}
                </Field>
                <Field label="Confirm password" error={errors.confirm}>
                  {(c) => (
                    <Input
                      {...c}
                      type="password"
                      name="confirmPassword"
                      autoComplete="new-password"
                      value={confirm}
                      onChange={(e) => setConfirm(e.target.value)}
                    />
                  )}
                </Field>
              </div>

              <Field
                label="Organization name"
                error={errors.organizationName}
                hint="Shown in the sidebar. You can rename it later."
              >
                {(c) => (
                  <Input
                    {...c}
                    name="organizationName"
                    autoComplete="organization"
                    value={organizationName}
                    onChange={(e) => setOrganizationName(e.target.value)}
                  />
                )}
              </Field>

              <Button type="submit" size="lg" disabled={pending}>
                {pending && <Loader2Icon className="animate-spin" aria-hidden="true" />}
                Create owner account
              </Button>
            </form>
          )}
        </TerminalCard>
      </div>
    </AuthScreen>
  );
}
