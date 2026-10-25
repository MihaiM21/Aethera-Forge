"use client";

import * as React from "react";
import { SendIcon } from "lucide-react";
import { FormError, SaveButton, fieldError, numOrUndef, useAction } from "@/components/resources/form";
import { ErrorPanel, SectionTitle } from "@/components/servers/common";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import type { NotificationsApi } from "@/lib/notifications/api";
import type { EmailSettings, EmailTestResult, EmailTransport, SmtpEncryption } from "@/lib/notifications/types";
import { formatAgo } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";
import { cn } from "@/lib/utils";

const ENCRYPTIONS: Array<{ value: SmtpEncryption; label: string; port: string }> = [
  { value: "startTls", label: "STARTTLS", port: "587" },
  { value: "tls", label: "TLS", port: "465" },
  { value: "none", label: "None", port: "25" },
];

/** A row of mutually exclusive buttons (a radio group in the field style). */
function Choice<T extends string>({ label, value, options, onChange }: { label: string; value: T; options: Array<{ value: T; label: string }>; onChange: (v: T) => void }) {
  return (
    <div role="radiogroup" aria-label={label} className="inline-flex border border-border-strong">
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          role="radio"
          aria-checked={value === o.value}
          onClick={() => onChange(o.value)}
          className={cn(
            "focus-ring h-8 border-r border-border-strong px-3 font-mono text-xs uppercase tracking-[0.06em] last:border-r-0",
            value === o.value ? "bg-primary text-primary-foreground" : "text-muted-foreground hover:bg-accent",
          )}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

function EmailForm({ settings, api, onSaved }: { settings: EmailSettings; api: NotificationsApi; onSaved: () => void }) {
  const [enabled, setEnabled] = React.useState(settings.enabled);
  const [transport, setTransport] = React.useState<EmailTransport>(settings.transport);
  const [host, setHost] = React.useState(settings.smtpHost ?? "");
  const [port, setPort] = React.useState(settings.smtpPort ? String(settings.smtpPort) : "");
  const [encryption, setEncryption] = React.useState<SmtpEncryption>(settings.smtpEncryption);
  const [username, setUsername] = React.useState(settings.smtpUsername ?? "");
  const [password, setPassword] = React.useState("");
  const [clearPassword, setClearPassword] = React.useState(false);
  const [apiKey, setApiKey] = React.useState("");
  const [clearApiKey, setClearApiKey] = React.useState(false);
  const [fromAddress, setFromAddress] = React.useState(settings.fromAddress ?? "");
  const [fromName, setFromName] = React.useState(settings.fromName ?? "Aethera");
  const [result, setResult] = React.useState<EmailTestResult | null>(null);
  const save = useAction();
  const test = useAction();

  const body = () => ({
    enabled,
    transport,
    smtpHost: host.trim() || null,
    smtpPort: numOrUndef(port) ?? null,
    smtpEncryption: encryption,
    smtpUsername: username.trim() || null,
    // Secrets: absent keeps the stored one, a value replaces it, an empty string removes it.
    ...(password ? { smtpPassword: password } : clearPassword ? { smtpPassword: "" } : {}),
    ...(apiKey ? { resendApiKey: apiKey } : clearApiKey ? { resendApiKey: "" } : {}),
    fromAddress: fromAddress.trim(),
    fromName: fromName.trim() || null,
  });

  /** Secrets are write-only: once saved, the fields go back to empty and the hint says one is stored. */
  function savedSecrets() {
    setPassword("");
    setClearPassword(false);
    setApiKey("");
    setClearApiKey(false);
  }

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    const saved = await save.run(() => api.email.save(body()), "Email settings saved");
    if (!saved) return;
    savedSecrets();
    onSaved();
  }

  async function onTest() {
    setResult(null);
    // The test uses the saved settings, so what is on screen is saved first.
    const saved = await save.run(() => api.email.save(body()));
    if (!saved) return;
    savedSecrets();
    const tested = await test.run(() => api.email.test());
    if (tested) setResult(tested);
    onSaved();
  }

  const placeholderPort = ENCRYPTIONS.find((x) => x.value === encryption)?.port;
  return (
    <form className="grid gap-5 border border-border bg-card p-4" onSubmit={onSubmit} aria-label="Email settings">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <label className="flex items-center gap-3 text-sm">
          <Switch checked={enabled} onCheckedChange={setEnabled} aria-label="Email notifications enabled" />
          <span>{enabled ? "Email is on" : "Email is off"}</span>
        </label>
        <Choice label="Transport" value={transport} onChange={setTransport} options={[{ value: "smtp", label: "SMTP" }, { value: "resend", label: "Resend" }]} />
      </div>

      {transport === "smtp" ? (
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="SMTP host" error={fieldError(save.fields, "smtpHost")}>{(c) => <Input {...c} className="font-mono" placeholder="smtp.example.com" value={host} onChange={(e) => setHost(e.target.value)} />}</Field>
          <Field label="Port" error={fieldError(save.fields, "smtpPort")}>{(c) => <Input {...c} inputMode="numeric" className="font-mono" placeholder={placeholderPort} value={port} onChange={(e) => setPort(e.target.value)} />}</Field>
          <div className="grid gap-1.5">
            <span className="text-xs font-medium">Encryption</span>
            <Choice label="Encryption" value={encryption} onChange={setEncryption} options={ENCRYPTIONS} />
          </div>
          <span />
          <Field label="Username" error={fieldError(save.fields, "smtpUsername")}>{(c) => <Input {...c} autoComplete="off" value={username} onChange={(e) => setUsername(e.target.value)} />}</Field>
          <Field
            label="Password"
            error={fieldError(save.fields, "smtpPassword")}
            hint={settings.hasSmtpPassword && !clearPassword ? "A password is stored. Leave empty to keep it." : clearPassword ? "The stored password is removed when you save." : undefined}
          >
            {(c) => (
              <div className="flex gap-2">
                <Input {...c} type="password" autoComplete="new-password" value={password} onChange={(e) => { setPassword(e.target.value); setClearPassword(false); }} />
                {settings.hasSmtpPassword && !clearPassword && (
                  <Button type="button" variant="outline" onClick={() => { setPassword(""); setClearPassword(true); }}>
                    Remove
                  </Button>
                )}
              </div>
            )}
          </Field>
        </div>
      ) : (
        <Field
          label="Resend API key"
          error={fieldError(save.fields, "resendApiKey")}
          hint={settings.hasResendApiKey && !clearApiKey ? "A key is stored. Leave empty to keep it." : clearApiKey ? "The stored key is removed when you save." : "Create a key at resend.com with sending access."}
        >
          {(c) => (
            <div className="flex gap-2">
              <Input {...c} type="password" autoComplete="new-password" className="font-mono" placeholder="re_..." value={apiKey} onChange={(e) => { setApiKey(e.target.value); setClearApiKey(false); }} />
              {settings.hasResendApiKey && !clearApiKey && (
                <Button type="button" variant="outline" onClick={() => { setApiKey(""); setClearApiKey(true); }}>
                  Remove
                </Button>
              )}
            </div>
          )}
        </Field>
      )}

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="From address" error={fieldError(save.fields, "fromAddress")}>{(c) => <Input {...c} type="email" placeholder="aethera@example.com" value={fromAddress} onChange={(e) => setFromAddress(e.target.value)} required />}</Field>
        <Field label="From name" error={fieldError(save.fields, "fromName")}>{(c) => <Input {...c} value={fromName} onChange={(e) => setFromName(e.target.value)} />}</Field>
      </div>

      <FormError message={save.error ?? test.error} />
      {result && (
        <p role="status" className={cn("border px-3 py-2 text-xs", result.ok ? "border-success bg-success-soft text-success" : "border-danger bg-danger-soft text-danger")}>
          {result.ok ? `Test email sent to ${result.sentTo}.` : `The test email failed: ${result.error}`}
        </p>
      )}
      {!result && settings.lastTestedAt != null && (
        <p className="font-mono text-2xs text-muted-foreground">
          Last test {formatAgo(settings.lastTestedAt)}: {settings.lastTestError ? `failed (${settings.lastTestError})` : "sent"}
        </p>
      )}

      <div className="flex flex-wrap justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => void onTest()} disabled={save.pending || test.pending || !fromAddress.trim()}>
          <SendIcon aria-hidden="true" /> Send test email
        </Button>
        <SaveButton pending={save.pending} disabled={!fromAddress.trim()} />
      </div>
    </form>
  );
}

/** Email transport: SMTP or Resend, like Coolify's one settings form with a test button. */
export function EmailSection({ api }: { api: NotificationsApi }) {
  const email = usePolled((signal) => api.email.get({ signal }), "notifications-email", { intervalMs: null });
  return (
    <section className="grid gap-3" aria-label="Email">
      <SectionTitle index="01">Email</SectionTitle>
      <p className="text-xs text-muted-foreground">
        Channels of kind email send through this transport. Passwords and keys are stored as encrypted secrets and never shown again.
      </p>
      {email.loading && <Skeleton className="h-48 w-full" />}
      {email.error && !email.data && <ErrorPanel error={email.error} onRetry={() => void email.refresh()} title="Could not load the email settings" />}
      {email.data && <EmailForm settings={email.data} api={api} onSaved={() => void email.refresh()} />}
    </section>
  );
}
