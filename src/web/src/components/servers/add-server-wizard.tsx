"use client";

import * as React from "react";
import Link from "next/link";
import { ArrowLeftIcon, ArrowRightIcon, CheckCircle2Icon, Loader2Icon, TerminalIcon, KeyRoundIcon } from "lucide-react";
import { PageHeader } from "@/components/aethera/page-header";
import { Stepper } from "@/components/aethera/stepper";
import { DetailLink } from "@/components/shell/detail-link";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { isApiError } from "@/lib/api/errors";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { errorMessage } from "@/lib/servers/errors";
import { describeHealth } from "@/lib/servers/status";
import { SSH_INSTALL_ENABLED, sshAdapter, type SshHostKey, type SshInstallAdapter } from "@/lib/servers/ssh";
import { SERVER_ROLES, type JoinToken, type Server, type ServerRole } from "@/lib/servers/types";
import { usePolled } from "@/lib/servers/use-polled";
import { cn } from "@/lib/utils";
import { AxisViewBadge } from "./status-badges";
import { JobProgress } from "./job-progress";
import { JoinTokenPanel } from "./join-token-panel";

type Method = "join" | "ssh";

const STEPS = [
  { title: "Details", description: "name, host, roles" },
  { title: "Connect", description: "install the agent" },
  { title: "Done", description: "server is online" },
];

const ROLE_HELP: Record<ServerRole, string> = {
  master: "Runs the control plane",
  build: "Builds images",
  storage: "Backups and volumes",
  ci: "CI runners",
  worker: "Runs workloads",
};

/** Polls the status axes until the agent session is up, then calls `onConnected` once. */
function AgentWaiter({
  api,
  serverId,
  pollMs,
  onConnected,
}: {
  api: ServersApi;
  serverId: string;
  pollMs: number;
  onConnected: () => void;
}) {
  const { data, error } = usePolled((signal) => api.status(serverId, { signal }), serverId, { intervalMs: pollMs });
  const connected = data?.agent.health === "available";
  const cb = React.useRef(onConnected);
  React.useEffect(() => {
    cb.current = onConnected;
  });
  React.useEffect(() => {
    if (connected) cb.current();
  }, [connected]);

  return (
    <div role="status" aria-live="polite" className="grid gap-2 border border-dashed border-border-strong px-4 py-3">
      <p className="flex items-center gap-2 text-sm">
        <Loader2Icon className="size-4 animate-spin text-lime motion-reduce:animate-none" aria-hidden="true" />
        Waiting for the agent to connect…
      </p>
      {data && (
        <div className="flex flex-wrap gap-1.5">
          {describeHealth(data)
            .filter((v) => v.key !== "application")
            .map((v) => (
              <AxisViewBadge key={v.key} view={v} />
            ))}
        </div>
      )}
      {error && <p className="text-xs text-warning">Status check failed, retrying: {errorMessage(error)}</p>}
    </div>
  );
}

export function AddServerWizard({
  api = serversApi,
  ssh = sshAdapter,
  sshEnabled = SSH_INSTALL_ENABLED,
  pollMs = 3000,
}: {
  api?: ServersApi;
  ssh?: SshInstallAdapter;
  sshEnabled?: boolean;
  pollMs?: number;
}) {
  const [step, setStep] = React.useState(0);
  const [name, setName] = React.useState("");
  const [host, setHost] = React.useState("");
  const [roles, setRoles] = React.useState<ReadonlySet<ServerRole>>(new Set(["worker"]));
  const [method, setMethod] = React.useState<Method>("join");
  const [fieldErrors, setFieldErrors] = React.useState<Record<string, string>>({});

  const [server, setServer] = React.useState<Server | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  // The token exists only here. Never copied to storage, the URL or logs.
  const [token, setToken] = React.useState<JoinToken | null>(null);
  const [tokenGone, setTokenGone] = React.useState<null | "hidden" | "expired">(null);

  // SSH path
  const [sshPort, setSshPort] = React.useState("22");
  const [sshUser, setSshUser] = React.useState("root");
  const [hostKey, setHostKey] = React.useState<SshHostKey | null>(null);
  const [hostKeyTrusted, setHostKeyTrusted] = React.useState(false);
  const [sshJobId, setSshJobId] = React.useState<string | null>(null);
  const [sshJobDone, setSshJobDone] = React.useState<null | "succeeded" | "failed">(null);

  const [finalHealthKey, setFinalHealthKey] = React.useState(0);

  function toggleRole(role: ServerRole, on: boolean) {
    setRoles((prev) => {
      const next = new Set(prev);
      if (on) next.add(role);
      else next.delete(role);
      return next;
    });
  }

  function goToConnect(e: React.FormEvent) {
    e.preventDefault();
    const errs: Record<string, string> = {};
    if (!name.trim()) errs["/name"] = "Enter a name.";
    if (!host.trim()) errs["/host"] = "Enter a DNS name or IP address.";
    setFieldErrors(errs);
    if (Object.keys(errs).length === 0) setStep(1);
  }

  /** POST /servers once; later retries reuse the created server. */
  async function ensureServer(transport: "agent" | "ssh"): Promise<Server | null> {
    if (server) return server;
    try {
      const created = await api.create({
        name: name.trim(),
        host: host.trim(),
        roles: [...roles],
        transport,
        ...(transport === "ssh" ? { sshPort: Number(sshPort) || 22, sshUser: sshUser.trim() || undefined } : {}),
      });
      setServer(created);
      return created;
    } catch (e) {
      if (isApiError(e) && e.errors.length > 0) {
        const mapped: Record<string, string> = {};
        for (const f of e.errors) if (f.pointer && f.message) mapped[f.pointer] = f.message;
        if (Object.keys(mapped).length > 0) {
          setFieldErrors(mapped);
          setStep(0);
        }
      }
      setError(errorMessage(e));
      return null;
    }
  }

  async function generateJoinCommand() {
    setBusy(true);
    setError(null);
    try {
      const s = await ensureServer("agent");
      if (!s) return;
      const issued = await api.createJoinToken(s.id);
      setToken(issued);
      setTokenGone(null);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function checkHostKey() {
    setBusy(true);
    setError(null);
    try {
      const s = await ensureServer("ssh");
      if (!s) return;
      const key = await ssh.probeHostKey({ serverId: s.id, host: s.host, port: Number(sshPort) || 22, user: sshUser.trim() || "root" });
      setHostKey(key);
      setHostKeyTrusted(false);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function installViaSsh() {
    if (!server || !hostKey || !hostKeyTrusted) return;
    setBusy(true);
    setError(null);
    try {
      const { jobId } = await ssh.install({
        serverId: server.id,
        host: server.host,
        port: Number(sshPort) || 22,
        user: sshUser.trim() || "root",
        confirmedFingerprintSha256: hostKey.fingerprintSha256,
      });
      setSshJobId(jobId);
      setSshJobDone(null);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const connected = React.useCallback(() => {
    setToken(null); // the join token is spent; drop it
    setStep(2);
  }, []);

  const awaitingAgent =
    server !== null && ((method === "join" && (token !== null || tokenGone === "hidden")) || (method === "ssh" && sshJobDone === "succeeded"));

  return (
    <div className="flex flex-col gap-8">
      <PageHeader
        eyebrow="Infrastructure"
        title="Add server"
        description="Register a machine and install the Aethera agent. The agent connects out to the control plane, so the server needs no open inbound ports."
        actions={
          <Button variant="outline" asChild>
            <Link href="/servers">
              <ArrowLeftIcon aria-hidden="true" /> All servers
            </Link>
          </Button>
        }
      />

      <Stepper steps={STEPS} current={step} aria-label="Add server progress" />

      {step === 0 && (
        <form onSubmit={goToConnect} noValidate className="grid max-w-2xl gap-5 border border-border bg-card p-5">
          <Field label="Name" hint="Shown in the UI and typed to confirm destructive actions." error={fieldErrors["/name"]}>
            {(c) => (
              <Input {...c} value={name} onChange={(e) => setName(e.target.value)} autoComplete="off" maxLength={200} required />
            )}
          </Field>
          <Field label="Host" hint="DNS name or IP address, without scheme or port." error={fieldErrors["/host"]}>
            {(c) => (
              <Input
                {...c}
                value={host}
                onChange={(e) => setHost(e.target.value)}
                autoComplete="off"
                autoCapitalize="off"
                spellCheck={false}
                placeholder="203.0.113.10 or srv1.example.com"
                className="font-mono"
                required
              />
            )}
          </Field>
          <fieldset className="grid gap-2">
            <legend className="mono-label mb-1 text-muted-foreground">Roles</legend>
            <div className="grid gap-2 sm:grid-cols-2">
              {SERVER_ROLES.map((role) => {
                const id = `role-${role}`;
                return (
                  <div key={role} className="flex items-start gap-2 border border-border px-3 py-2">
                    <Checkbox id={id} checked={roles.has(role)} onCheckedChange={(v) => toggleRole(role, v === true)} className="mt-0.5" />
                    <Label htmlFor={id} className="grid gap-0.5 normal-case tracking-normal">
                      <span className="text-sm text-foreground">{role}</span>
                      <span className="text-xs font-normal text-muted-foreground">{ROLE_HELP[role]}</span>
                    </Label>
                  </div>
                );
              })}
            </div>
          </fieldset>
          <div className="flex justify-end">
            <Button type="submit">
              Continue <ArrowRightIcon aria-hidden="true" />
            </Button>
          </div>
        </form>
      )}

      {step === 1 && (
        <div className="grid max-w-3xl gap-5">
          <div role="radiogroup" aria-label="Connection method" className="grid gap-3 sm:grid-cols-2">
            <MethodCard
              selected={method === "join"}
              disabled={busy || server !== null}
              onSelect={() => setMethod("join")}
              icon={KeyRoundIcon}
              index="A"
              title="Copy join command"
              description="Run one command on the server. The agent enrolls with a one-time token."
            />
            <MethodCard
              selected={method === "ssh"}
              disabled={!sshEnabled || busy || server !== null}
              onSelect={() => setMethod("ssh")}
              icon={TerminalIcon}
              index="B"
              title="Install via SSH"
              description="Aethera connects once over SSH and installs the agent for you."
              note={sshEnabled ? undefined : "Not available yet"}
            />
          </div>

          {server && (
            <p className="font-mono text-2xs text-muted-foreground">
              server <span className="text-foreground">{server.name}</span> created. Details are locked while you connect it.
            </p>
          )}

          {error && (
            <p role="alert" className="border border-danger/60 bg-danger-soft px-3 py-2 text-xs text-danger">
              {error}
            </p>
          )}

          {method === "join" && (
            <div className="grid gap-4">
              {!token && (
                <div className="flex flex-wrap items-center gap-3">
                  <Button onClick={() => void generateJoinCommand()} disabled={busy}>
                    {busy && <Loader2Icon className="animate-spin" aria-hidden="true" />}
                    {tokenGone ? "Generate a new token" : "Generate join command"}
                  </Button>
                  {tokenGone === "expired" && <Badge tone="warning">token expired</Badge>}
                  {tokenGone === "hidden" && <Badge tone="neutral">token hidden</Badge>}
                </div>
              )}
              {token && (
                <JoinTokenPanel
                  token={token}
                  onDismiss={(reason) => {
                    setToken(null);
                    setTokenGone(reason);
                  }}
                />
              )}
              {server && awaitingAgent && <AgentWaiter api={api} serverId={server.id} pollMs={pollMs} onConnected={connected} />}
              {server && tokenGone === "expired" && (
                <p className="text-xs text-muted-foreground">
                  The token expired before an agent used it. Generate a new one; the server stays registered.
                </p>
              )}
            </div>
          )}

          {method === "ssh" && sshEnabled && (
            <div className="grid gap-4 border border-border bg-card p-5">
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="SSH port">
                  {(c) => (
                    <Input {...c} inputMode="numeric" value={sshPort} onChange={(e) => setSshPort(e.target.value)} disabled={server !== null} className="font-mono" />
                  )}
                </Field>
                <Field label="SSH user">
                  {(c) => <Input {...c} value={sshUser} onChange={(e) => setSshUser(e.target.value)} disabled={server !== null} className="font-mono" autoComplete="off" />}
                </Field>
              </div>
              {!hostKey && (
                <div>
                  <Button onClick={() => void checkHostKey()} disabled={busy}>
                    {busy && <Loader2Icon className="animate-spin" aria-hidden="true" />}
                    Check host key
                  </Button>
                </div>
              )}
              {hostKey && !sshJobId && (
                <div className="grid gap-3">
                  <div className="border border-dashed border-border-strong px-4 py-3 font-mono text-xs">
                    <div className="text-muted-foreground">{hostKey.algorithm}</div>
                    <div aria-label="Host key fingerprint" className="break-all text-foreground">
                      {hostKey.fingerprintSha256}
                    </div>
                  </div>
                  <div className="flex items-start gap-2">
                    <Checkbox id="trust-host-key" checked={hostKeyTrusted} onCheckedChange={(v) => setHostKeyTrusted(v === true)} className="mt-0.5" />
                    <Label htmlFor="trust-host-key" className="normal-case tracking-normal">
                      I verified this fingerprint belongs to {host.trim()} and trust it
                    </Label>
                  </div>
                  <div>
                    <Button onClick={() => void installViaSsh()} disabled={!hostKeyTrusted || busy}>
                      {busy && <Loader2Icon className="animate-spin" aria-hidden="true" />}
                      Install agent
                    </Button>
                  </div>
                </div>
              )}
              {sshJobId && (
                <JobProgress
                  jobId={sshJobId}
                  label="Install agent over SSH"
                  api={api}
                  onFinished={(job) => setSshJobDone(job.status === "succeeded" ? "succeeded" : "failed")}
                />
              )}
              {server && sshJobDone === "succeeded" && awaitingAgent && (
                <AgentWaiter api={api} serverId={server.id} pollMs={pollMs} onConnected={connected} />
              )}
            </div>
          )}

          <div>
            <Button variant="ghost" onClick={() => setStep(0)} disabled={busy || server !== null}>
              <ArrowLeftIcon aria-hidden="true" /> Back
            </Button>
          </div>
        </div>
      )}

      {step === 2 && server && (
        <div className="grid max-w-2xl gap-4 border border-border bg-card p-6" role="status">
          <p className="flex items-center gap-2 text-lg font-medium tracking-subheading">
            <CheckCircle2Icon className="size-5 text-success" aria-hidden="true" /> {server.name} is connected
          </p>
          <p className="text-sm text-muted-foreground">
            The agent enrolled and is streaming. System information and metrics appear on the server page within a few seconds.
          </p>
          <FinalAxes key={finalHealthKey} api={api} serverId={server.id} />
          <div className="flex flex-wrap gap-2">
            <Button asChild>
              <DetailLink href={`/servers/${encodeURIComponent(server.id)}`}>Open server</DetailLink>
            </Button>
            <Button variant="outline" onClick={() => setFinalHealthKey((k) => k + 1)}>
              Re-check status
            </Button>
            <Button variant="outline" onClick={() => window.location.reload()}>
              Add another
            </Button>
          </div>
        </div>
      )}
    </div>
  );
}

function FinalAxes({ api, serverId }: { api: ServersApi; serverId: string }) {
  const { data } = usePolled((signal) => api.status(serverId, { signal }), serverId, { intervalMs: null });
  if (!data) return null;
  return (
    <div className="flex flex-wrap gap-1.5">
      {describeHealth(data).map((v) => (
        <AxisViewBadge key={v.key} view={v} />
      ))}
    </div>
  );
}

function MethodCard({
  selected,
  disabled,
  onSelect,
  icon: Icon,
  index,
  title,
  description,
  note,
}: {
  selected: boolean;
  disabled?: boolean;
  onSelect: () => void;
  icon: React.ElementType;
  index: string;
  title: string;
  description: string;
  note?: string;
}) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={selected}
      aria-disabled={disabled || undefined}
      disabled={disabled}
      onClick={onSelect}
      className={cn(
        "focus-ring grid gap-2 border bg-card p-4 text-left transition-colors",
        selected ? "border-primary" : "border-border hover:border-border-strong",
        disabled && !selected && "opacity-60",
      )}
    >
      <span className="flex items-center justify-between">
        <span className="font-mono text-2xs text-lime">{index}</span>
        <Icon className="size-4 text-muted-foreground" aria-hidden="true" />
      </span>
      <span className="text-base font-medium tracking-subheading">{title}</span>
      <span className="text-xs text-muted-foreground">{description}</span>
      {note && <Badge tone="neutral" className="w-fit">{note}</Badge>}
    </button>
  );
}
