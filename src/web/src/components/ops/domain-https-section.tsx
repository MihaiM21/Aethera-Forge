"use client";

import * as React from "react";
import { GlobeIcon, Loader2Icon, SparklesIcon } from "lucide-react";
import { FormError, fieldError, useAction } from "@/components/resources/form";
import { ErrorPanel, Facts, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import type { ProxySettings, ProxySettingsUpdated } from "@/lib/resources/types";
import { usePolled } from "@/lib/servers/use-polled";

function DnsLine({ result }: { result: ProxySettingsUpdated }) {
  if (result.proxyError) return <p className="text-xs text-warning">Saved, but the proxy was not updated: {result.proxyError} It is applied when the agent reconnects.</p>;
  const dns = result.dns;
  if (!dns) return null;
  const ok = dns.status === "ok" || dns.status === "proxied";
  return (
    <p className="flex flex-wrap items-center gap-2 text-xs">
      <Badge tone={ok ? "success" : "warning"}>DNS {dns.status}</Badge>
      <span className="text-muted-foreground">{dns.message}</span>
    </p>
  );
}

/**
 * The domain the Aethera UI is served on and the Let's Encrypt account (ADR 0011). Saved here they win over the installer's `.env`; the
 * proxy of the control plane's machine routes the domain at once, and the UI keeps answering on its own port, so a typo cannot lock anyone out.
 */
export function DomainHttpsSection({ api = resourcesApi }: { api?: ResourcesApi }) {
  const settings = usePolled((signal) => api.system.proxySettings({ signal }), "proxy-settings", { intervalMs: 0 });
  const data: ProxySettings | undefined = settings.data;
  const [domain, setDomain] = React.useState("");
  const [https, setHttps] = React.useState(true);
  const [email, setEmail] = React.useState("");
  const [result, setResult] = React.useState<ProxySettingsUpdated | null>(null);
  const loaded = React.useRef(false);
  const act = useAction();

  React.useEffect(() => {
    if (!data || loaded.current) return;
    loaded.current = true;
    setDomain(data.dashboardDomain ?? "");
    setHttps(data.dashboardHttps);
    setEmail(data.acmeEmail ?? "");
  }, [data]);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    const saved = await act.run(
      () => api.system.updateProxySettings({ dashboardDomain: domain.trim() || null, dashboardHttps: https, acmeEmail: email.trim() || null }),
      "Domain settings saved",
    );
    if (saved) {
      setResult(saved);
      await settings.refresh();
    }
  }

  return (
    <section aria-labelledby="domain-https-title" className="grid gap-3">
      <SectionTitle index="03">
        <span id="domain-https-title">Domain &amp; HTTPS</span>
      </SectionTitle>
      {settings.error && !data && <ErrorPanel error={settings.error} title="Could not load the domain settings" onRetry={() => void settings.refresh()} />}
      {!data && !settings.error && <Skeleton className="h-40 w-full" />}
      {data && (
        <form onSubmit={save} className="grid max-w-2xl gap-4 border border-border bg-card p-5" noValidate>
          <Facts
            rows={[
              ["Opened at", data.dashboardUrl ? <a className="hover:text-lime" href={data.dashboardUrl}>{data.dashboardUrl}</a> : "its own port only"],
              ["From", data.source === "settings" ? "these settings" : "the installer (.env)"],
            ]}
          />
          <Field
            label="Dashboard domain"
            error={fieldError(act.fields, "dashboardDomain")}
            hint="Point an A record at the control plane's server first. Empty = the UI is only served on its port."
          >
            {(c) => (
              <div className="flex gap-2">
                <Input {...c} className="font-mono" placeholder="aethera.example.com" value={domain} onChange={(e) => setDomain(e.target.value)} autoComplete="off" />
                {data.suggestedDomain && (
                  <Button type="button" variant="outline" onClick={() => setDomain(data.suggestedDomain!)} title={`Use ${data.suggestedDomain} (no DNS record needed)`}>
                    <SparklesIcon aria-hidden="true" /> sslip.io
                  </Button>
                )}
              </div>
            )}
          </Field>
          <label className="flex items-center gap-3 text-sm">
            <Switch checked={https} onCheckedChange={setHttps} aria-label="Serve the dashboard over HTTPS" />
            Serve over HTTPS (HTTP redirects to it)
          </label>
          <Field
            label="Let's Encrypt email"
            error={fieldError(act.fields, "acmeEmail")}
            hint="The account certificates are issued with, for the dashboard and every HTTPS domain. Without it HTTPS uses a self-signed certificate."
          >
            {(c) => <Input {...c} type="email" placeholder="you@example.com" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="email" />}
          </Field>
          <FormError message={act.error} />
          {result && <DnsLine result={result} />}
          <div className="flex items-center gap-3">
            <Button type="submit" disabled={act.pending}>
              {act.pending ? <Loader2Icon className="animate-spin" aria-hidden="true" /> : <GlobeIcon aria-hidden="true" />} Save and apply
            </Button>
            <span className="text-xs text-muted-foreground">New applications get an automatic domain from their server; set your own per application.</span>
          </div>
        </form>
      )}
    </section>
  );
}
