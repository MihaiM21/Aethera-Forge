"use client";

import * as React from "react";
import QRCode from "qrcode";
import { DownloadIcon, KeyRoundIcon, ShieldCheckIcon, ShieldOffIcon } from "lucide-react";
import { FormError, SaveButton, fieldError, useAction } from "@/components/resources/form";
import { CopyButton, ErrorPanel, Facts, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { authApi, type AuthApi, type TwoFactorEnrolment, type TwoFactorStatus } from "@/lib/api";
import { formatDateTime } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

/** The otpauth:// URI as a QR code (rendered in the browser; the secret never goes to a third party). */
export function QrCode({ value, size = 192 }: { value: string; size?: number }) {
  const [svg, setSvg] = React.useState<string | null>(null);
  React.useEffect(() => {
    let cancelled = false;
    QRCode.toString(value, { type: "svg", margin: 2, errorCorrectionLevel: "M", color: { dark: "#000000", light: "#ffffff" } })
      .then((text) => !cancelled && setSvg(text))
      .catch(() => !cancelled && setSvg(null));
    return () => {
      cancelled = true;
    };
  }, [value]);
  // Black on white whatever the theme: authenticator apps read dark modules on a light background best.
  return svg ? (
    // eslint-disable-next-line @next/next/no-img-element -- a data: URI generated here, nothing for next/image to optimise
    <img
      src={`data:image/svg+xml;utf8,${encodeURIComponent(svg)}`}
      width={size}
      height={size}
      alt="QR code for your authenticator app"
      className="border border-border bg-white"
    />
  ) : (
    <Skeleton className="border border-border" style={{ width: size, height: size }} aria-label="Generating QR code" />
  );
}

/** Groups a base32 secret in fours so it can be typed in. */
export function groupSecret(secret: string): string {
  return secret.replace(/(.{4})/g, "$1 ").trim();
}

/** The recovery codes, shown once, with copy and download. */
export function RecoveryCodesPanel({ codes, onDone, doneLabel = "I saved my recovery codes" }: { codes: string[]; onDone: () => void; doneLabel?: string }) {
  const text = codes.join("\n");
  function download() {
    const blob = new Blob([`Aethera recovery codes (each works once)\n\n${text}\n`], { type: "text/plain" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = "aethera-recovery-codes.txt";
    a.click();
    URL.revokeObjectURL(url);
  }
  return (
    <div className="grid gap-4">
      <p className="text-sm text-muted-foreground">
        Save these somewhere safe, like a password manager. Each code signs you in once if you lose your phone. They are shown only now.
      </p>
      <ul aria-label="Recovery codes" className="grid grid-cols-2 gap-x-6 gap-y-1.5 border border-border bg-muted/40 p-4 font-mono text-sm">
        {codes.map((c) => (
          <li key={c}>{c}</li>
        ))}
      </ul>
      <div className="flex flex-wrap gap-2">
        <CopyButton text={text} label="Copy codes" variant="outline" />
        <Button type="button" variant="outline" onClick={download}>
          <DownloadIcon aria-hidden="true" /> Download
        </Button>
        <Button type="button" onClick={onDone}>
          {doneLabel}
        </Button>
      </div>
    </div>
  );
}

type EnrolStep = { kind: "password" } | { kind: "scan"; enrolment: TwoFactorEnrolment } | { kind: "codes"; codes: string[] };

/**
 * Enrolling an authenticator: confirm the password, scan the QR code (or type the secret), confirm a code, then save the recovery codes.
 * Used from Settings > Account and from the enrolment screen an organization that requires two-factor authentication sends members to.
 */
export function TwoFactorEnrolmentFlow({ auth = authApi, onDone, onCancel }: { auth?: AuthApi; onDone: () => void; onCancel?: () => void }) {
  const [step, setStep] = React.useState<EnrolStep>({ kind: "password" });
  const [password, setPassword] = React.useState("");
  const [code, setCode] = React.useState("");
  const act = useAction();

  if (step.kind === "password") {
    return (
      <form
        className="grid gap-4"
        onSubmit={async (e) => {
          e.preventDefault();
          const enrolment = await act.run(() => auth.twoFactor.startEnrolment(password));
          if (enrolment) {
            setPassword("");
            setStep({ kind: "scan", enrolment });
          }
        }}
      >
        <p className="text-sm text-muted-foreground">Confirm your password to set up an authenticator app (1Password, Google Authenticator, Aegis, ...).</p>
        <Field label="Current password" error={fieldError(act.fields, "password")}>
          {(c) => <Input {...c} type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required autoFocus />}
        </Field>
        <FormError message={act.error} />
        <div className="flex gap-2">
          <SaveButton pending={act.pending} disabled={!password}>
            Continue
          </SaveButton>
          {onCancel && (
            <Button type="button" variant="outline" onClick={onCancel} disabled={act.pending}>
              Cancel
            </Button>
          )}
        </div>
      </form>
    );
  }

  if (step.kind === "scan") {
    const { enrolment } = step;
    return (
      <form
        className="grid gap-5"
        onSubmit={async (e) => {
          e.preventDefault();
          const result = await act.run(() => auth.twoFactor.confirmEnrolment(code.trim()));
          if (result) setStep({ kind: "codes", codes: result.recoveryCodes });
          else setCode("");
        }}
      >
        <div className="grid gap-5 sm:grid-cols-[auto_1fr] sm:items-start">
          <QrCode value={enrolment.otpauthUri} />
          <div className="grid min-w-0 gap-3">
            <p className="text-sm text-muted-foreground">Scan the code with your authenticator app, or enter the key by hand.</p>
            <Facts
              rows={[
                ["Account", enrolment.accountName],
                ["Issuer", enrolment.issuer],
                ["Key", <span key="k" className="select-all break-all">{groupSecret(enrolment.secret)}</span>],
                ["Type", `time based · ${enrolment.digits} digits · ${enrolment.period}s`],
              ]}
            />
            <div>
              <CopyButton text={enrolment.secret} label="Copy key" variant="outline" size="sm" />
            </div>
          </div>
        </div>
        <Field label="Code from the app" error={fieldError(act.fields, "code")}>
          {(c) => (
            <Input
              {...c}
              name="code"
              inputMode="numeric"
              autoComplete="one-time-code"
              className="max-w-48 font-mono tracking-widest"
              placeholder="123456"
              value={code}
              onChange={(e) => setCode(e.target.value)}
              required
              autoFocus
            />
          )}
        </Field>
        <FormError message={fieldError(act.fields, "code") ? null : act.error} />
        <div className="flex gap-2">
          <SaveButton pending={act.pending} disabled={!code.trim()}>
            Turn on two-factor authentication
          </SaveButton>
          {onCancel && (
            <Button type="button" variant="outline" onClick={onCancel} disabled={act.pending}>
              Cancel
            </Button>
          )}
        </div>
      </form>
    );
  }

  return <RecoveryCodesPanel codes={step.codes} onDone={onDone} />;
}

function DisableDialog({ auth, onClose, onDisabled }: { auth: AuthApi; onClose: () => void; onDisabled: () => void }) {
  const [password, setPassword] = React.useState("");
  const [code, setCode] = React.useState("");
  const [recovery, setRecovery] = React.useState(false);
  const act = useAction();
  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            const value = code.trim();
            const ok = await act.runOk(
              () => auth.twoFactor.disable(recovery ? { password, recoveryCode: value } : { password, code: value }),
              "Two-factor authentication is off",
            );
            if (ok) onDisabled();
          }}
        >
          <DialogHeader>
            <DialogTitle>Turn off two-factor authentication</DialogTitle>
            <DialogDescription>Signing in will need only your password. Your recovery codes stop working.</DialogDescription>
          </DialogHeader>
          <Field label="Current password" error={fieldError(act.fields, "password")}>
            {(c) => <Input {...c} type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />}
          </Field>
          <Field label={recovery ? "Recovery code" : "Code from the app"} error={fieldError(act.fields, recovery ? "recoveryCode" : "code")}>
            {(c) => (
              <Input {...c} autoComplete="one-time-code" className="font-mono" value={code} onChange={(e) => setCode(e.target.value)} required />
            )}
          </Field>
          <button
            type="button"
            className="justify-self-start font-mono text-2xs uppercase tracking-wider text-muted-foreground hover:text-foreground"
            onClick={() => {
              setRecovery((v) => !v);
              setCode("");
            }}
          >
            {recovery ? "Use the authenticator app" : "Use a recovery code"}
          </button>
          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
              Cancel
            </Button>
            <SaveButton pending={act.pending} disabled={!password || !code.trim()}>
              Turn off
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function RegenerateDialog({ auth, onClose }: { auth: AuthApi; onClose: () => void }) {
  const [password, setPassword] = React.useState("");
  const [code, setCode] = React.useState("");
  const [codes, setCodes] = React.useState<string[] | null>(null);
  const act = useAction();
  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>New recovery codes</DialogTitle>
          <DialogDescription>{codes ? "Your old codes no longer work." : "Your current recovery codes will stop working."}</DialogDescription>
        </DialogHeader>
        {codes ? (
          <RecoveryCodesPanel codes={codes} onDone={onClose} />
        ) : (
          <form
            className="grid gap-4"
            onSubmit={async (e) => {
              e.preventDefault();
              const result = await act.run(() => auth.twoFactor.regenerateRecoveryCodes(password, code.trim()));
              if (result) setCodes(result.recoveryCodes);
            }}
          >
            <Field label="Current password" error={fieldError(act.fields, "password")}>
              {(c) => <Input {...c} type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />}
            </Field>
            <Field label="Code from the app" error={fieldError(act.fields, "code")}>
              {(c) => <Input {...c} inputMode="numeric" autoComplete="one-time-code" className="font-mono" value={code} onChange={(e) => setCode(e.target.value)} required />}
            </Field>
            <FormError message={act.error} />
            <DialogFooter>
              <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
                Cancel
              </Button>
              <SaveButton pending={act.pending} disabled={!password || !code.trim()}>
                Create new codes
              </SaveButton>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}

function statusBadge(s: TwoFactorStatus) {
  if (s.enabled) return <Badge tone="success">on</Badge>;
  if (s.enrolmentPending) return <Badge tone="warning">setup not finished</Badge>;
  return <Badge tone={s.requiredByOrganization ? "danger" : "neutral"}>off</Badge>;
}

/** Settings > Account: the caller's two-factor authentication. */
export function TwoFactorSection({ auth = authApi, index = "03", onChanged }: { auth?: AuthApi; index?: string; onChanged?: () => void }) {
  const status = usePolled((signal) => auth.twoFactor.status(signal), "two-factor", { intervalMs: 60_000 });
  const [mode, setMode] = React.useState<"idle" | "enrol" | "disable" | "regenerate">("idle");
  const done = async () => {
    setMode("idle");
    await status.refresh();
    onChanged?.();
  };
  const s = status.data;
  return (
    <section className="grid gap-4">
      <SectionTitle index={index} actions={s && statusBadge(s)}>
        Two-factor authentication
      </SectionTitle>
      {status.error && !s ? (
        <ErrorPanel error={status.error} onRetry={() => void status.refresh()} />
      ) : !s ? (
        <Skeleton className="h-16" />
      ) : mode === "enrol" ? (
        <TwoFactorEnrolmentFlow auth={auth} onDone={() => void done()} onCancel={() => setMode("idle")} />
      ) : s.enabled ? (
        <div className="grid gap-4">
          <Facts
            rows={[
              ["Since", s.enabledAt ? formatDateTime(s.enabledAt) : null],
              ["Recovery codes", `${s.recoveryCodesRemaining} of 10 left`],
            ]}
          />
          {s.recoveryCodesRemaining <= 3 && (
            <p role="note" className="border border-warning/60 bg-warning-soft px-3 py-2 text-xs text-warning">
              Only {s.recoveryCodesRemaining} recovery codes left. Create new ones before you run out.
            </p>
          )}
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" onClick={() => setMode("regenerate")}>
              <KeyRoundIcon aria-hidden="true" /> New recovery codes
            </Button>
            <Button variant="outline" onClick={() => setMode("disable")}>
              <ShieldOffIcon aria-hidden="true" /> Turn off
            </Button>
          </div>
          {s.requiredByOrganization && (
            <p className="text-xs text-muted-foreground">Your organization requires two-factor authentication: turning it off sends you straight back to setting it up.</p>
          )}
        </div>
      ) : (
        <div className="grid gap-3">
          <p className="text-sm text-muted-foreground">
            Ask for a code from an authenticator app after your password when you sign in. API tokens are not affected.
          </p>
          <div>
            <Button onClick={() => setMode("enrol")}>
              <ShieldCheckIcon aria-hidden="true" /> {s.enrolmentPending ? "Finish setting up" : "Set up two-factor authentication"}
            </Button>
          </div>
        </div>
      )}
      {mode === "disable" && <DisableDialog auth={auth} onClose={() => setMode("idle")} onDisabled={() => void done()} />}
      {mode === "regenerate" && <RegenerateDialog auth={auth} onClose={() => void done()} />}
    </section>
  );
}

/** Settings > Members (owners): require two-factor authentication for everyone. */
export function TwoFactorPolicy({ auth = authApi, canChange }: { auth?: AuthApi; canChange: boolean }) {
  const status = usePolled((signal) => auth.twoFactor.status(signal), "two-factor-policy", { intervalMs: 60_000 });
  const act = useAction();
  const s = status.data;
  const id = React.useId();
  return (
    <div className="grid gap-2 border border-border p-4">
      <div className="flex items-center justify-between gap-4">
        <label htmlFor={id} className="grid gap-1">
          <span className="text-sm font-medium">Require two-factor authentication</span>
          <span className="text-xs text-muted-foreground">
            Members without an authenticator can only set one up until they have. API tokens keep working.
            {canChange && s && !s.enabled && " Turn it on for your own account first."}
          </span>
        </label>
        <Switch
          id={id}
          checked={s?.requiredByOrganization ?? false}
          disabled={!canChange || !s || act.pending || (!s.enabled && !s.requiredByOrganization)}
          onCheckedChange={async (required) => {
            const ok = await act.runOk(() => auth.twoFactor.setPolicy(required), required ? "Two-factor authentication is required" : "Two-factor authentication is optional");
            if (ok) await status.refresh();
          }}
        />
      </div>
      {!canChange && <p className="text-xs text-muted-foreground">Only an owner can change this.</p>}
      <FormError message={act.error} />
    </div>
  );
}
