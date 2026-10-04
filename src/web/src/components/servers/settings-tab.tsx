"use client";

import * as React from "react";
import { KeyRoundIcon, Loader2Icon, SparklesIcon } from "lucide-react";
import { toast } from "@/components/ui/sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { isApiError } from "@/lib/api/errors";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { errorMessage } from "@/lib/servers/errors";
import { formatDateTime } from "@/lib/servers/format";
import { SERVER_ROLES, type Job, type JoinToken, type PruneRequest, type Server, type ServerLifecycle, type ServerRole } from "@/lib/servers/types";
import { usePolled } from "@/lib/servers/use-polled";
import { ErrorPanel, NativeSelect, SectionTitle } from "./common";
import { JobProgress } from "./job-progress";
import { JoinTokenPanel } from "./join-token-panel";
import { PruneDialog } from "./prune-dialog";

const LIFECYCLES: ServerLifecycle[] = ["pending", "active", "maintenance", "disabled"];

function GeneralForm({ server, api, onSaved }: { server: Server; api: ServersApi; onSaved: (s: Server) => void }) {
  const [name, setName] = React.useState(server.name);
  const [host, setHost] = React.useState(server.host);
  const [roles, setRoles] = React.useState<ReadonlySet<ServerRole>>(new Set(server.roles));
  const [lifecycle, setLifecycle] = React.useState<ServerLifecycle>(server.lifecycle);
  const [pending, setPending] = React.useState(false);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [formError, setFormError] = React.useState<string | null>(null);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setPending(true);
    setErrors({});
    setFormError(null);
    try {
      const updated = await api.update(server.id, { name: name.trim(), host: host.trim(), roles: [...roles], lifecycle });
      onSaved(updated);
      toast.success("Server updated");
    } catch (err) {
      if (isApiError(err) && err.errors.length > 0) {
        const mapped: Record<string, string> = {};
        for (const f of err.errors) if (f.pointer && f.message) mapped[f.pointer] = f.message;
        setErrors(mapped);
      }
      setFormError(errorMessage(err));
    } finally {
      setPending(false);
    }
  }

  return (
    <form onSubmit={save} className="grid max-w-2xl gap-4 border border-border bg-card p-5" noValidate>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Name" error={errors["/name"]}>
          {(c) => <Input {...c} value={name} onChange={(e) => setName(e.target.value)} maxLength={200} autoComplete="off" />}
        </Field>
        <Field label="Host" error={errors["/host"]}>
          {(c) => <Input {...c} value={host} onChange={(e) => setHost(e.target.value)} className="font-mono" autoComplete="off" />}
        </Field>
      </div>
      <fieldset className="grid gap-2">
        <legend className="mono-label mb-1 text-muted-foreground">Roles</legend>
        <div className="flex flex-wrap gap-4">
          {SERVER_ROLES.map((role) => (
            <div key={role} className="flex items-center gap-2">
              <Checkbox
                id={`settings-role-${role}`}
                checked={roles.has(role)}
                onCheckedChange={(v) =>
                  setRoles((prev) => {
                    const next = new Set(prev);
                    if (v === true) next.add(role);
                    else next.delete(role);
                    return next;
                  })
                }
              />
              <Label htmlFor={`settings-role-${role}`} className="normal-case tracking-normal">
                {role}
              </Label>
            </div>
          ))}
        </div>
      </fieldset>
      <div className="grid gap-1.5 sm:w-56">
        <Label htmlFor="settings-lifecycle">Lifecycle</Label>
        <NativeSelect id="settings-lifecycle" value={lifecycle} onChange={(e) => setLifecycle(e.target.value as ServerLifecycle)}>
          {LIFECYCLES.map((l) => (
            <option key={l} value={l}>
              {l}
            </option>
          ))}
        </NativeSelect>
      </div>
      {formError && (
        <p role="alert" className="border border-danger/60 bg-danger-soft px-3 py-2 text-xs text-danger">
          {formError}
        </p>
      )}
      <div>
        <Button type="submit" disabled={pending}>
          {pending && <Loader2Icon className="animate-spin" aria-hidden="true" />} Save changes
        </Button>
      </div>
    </form>
  );
}

function tokenTone(state: string): "success" | "neutral" | "warning" | "danger" {
  switch (state) {
    case "active":
      return "success";
    case "expired":
      return "warning";
    case "revoked":
      return "danger";
    default:
      return "neutral";
  }
}

export function JoinTokensSection({ server, api }: { server: Server; api: ServersApi }) {
  const tokens = usePolled((signal) => api.listJoinTokens(server.id, { signal }), server.id, { intervalMs: 30_000 });
  const [fresh, setFresh] = React.useState<JoinToken | null>(null);
  const [creating, setCreating] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [revoking, setRevoking] = React.useState<string | null>(null);

  async function create() {
    setCreating(true);
    setError(null);
    try {
      setFresh(await api.createJoinToken(server.id));
      void tokens.refresh();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setCreating(false);
    }
  }

  async function revoke(id: string) {
    setRevoking(id);
    setError(null);
    try {
      await api.revokeJoinToken(server.id, id);
      await tokens.refresh();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setRevoking(null);
    }
  }

  return (
    <section aria-labelledby="tokens-title" className="grid gap-3">
      <SectionTitle
        index="02"
        actions={
          <Button size="sm" onClick={() => void create()} disabled={creating}>
            {creating ? <Loader2Icon className="animate-spin" aria-hidden="true" /> : <KeyRoundIcon aria-hidden="true" />}
            New join token
          </Button>
        }
      >
        <span id="tokens-title">Agent join tokens</span>
      </SectionTitle>
      <p className="text-xs text-muted-foreground">
        A token enrolls one agent and is consumed on first use. Values are never listed; only the one you create now is shown, once.
      </p>
      {error && (
        <p role="alert" className="text-xs text-danger">
          {error}
        </p>
      )}
      {fresh && <JoinTokenPanel token={fresh} onDismiss={() => setFresh(null)} />}
      {tokens.error && !tokens.data ? (
        <ErrorPanel error={tokens.error} title="Could not load join tokens" onRetry={() => void tokens.refresh()} />
      ) : !tokens.data ? (
        <Skeleton className="h-16 w-full" />
      ) : tokens.data.length === 0 ? (
        <p className="border border-dashed border-border-strong px-4 py-6 text-center text-sm text-muted-foreground">No join tokens yet.</p>
      ) : (
        <div className="border border-border bg-card">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>State</TableHead>
                <TableHead>Created</TableHead>
                <TableHead>Expires</TableHead>
                <TableHead>Used</TableHead>
                <TableHead className="text-right">
                  <span className="sr-only">Actions</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {tokens.data.map((t) => (
                <TableRow key={t.id}>
                  <TableCell>
                    <Badge tone={tokenTone(t.state)}>{t.state}</Badge>
                  </TableCell>
                  <TableCell className="font-mono text-xs">{formatDateTime(t.createdAt)}</TableCell>
                  <TableCell className="font-mono text-xs">{formatDateTime(t.expiresAt)}</TableCell>
                  <TableCell className="font-mono text-xs">{t.usedAt ? formatDateTime(t.usedAt) : "-"}</TableCell>
                  <TableCell className="text-right">
                    {t.state === "active" && (
                      <Button size="sm" variant="outline" onClick={() => void revoke(t.id)} disabled={revoking === t.id} aria-label={`Revoke token created ${formatDateTime(t.createdAt)}`}>
                        {revoking === t.id && <Loader2Icon className="animate-spin" aria-hidden="true" />} Revoke
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </section>
  );
}

export function MaintenanceSection({ server, api }: { server: Server; api: ServersApi }) {
  const [pruneOpen, setPruneOpen] = React.useState(false);
  const [jobs, setJobs] = React.useState<Array<{ job: Job; label: string }>>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [refreshing, setRefreshing] = React.useState(false);

  async function refreshDiscovery() {
    setRefreshing(true);
    setError(null);
    try {
      const job = await api.refreshDiscovery(server.id);
      setJobs((j) => [{ job, label: "Refresh discovery" }, ...j]);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setRefreshing(false);
    }
  }

  return (
    <section aria-labelledby="maint-title" className="grid gap-3">
      <SectionTitle index="03">
        <span id="maint-title">Maintenance</span>
      </SectionTitle>
      <p className="text-xs text-muted-foreground">Maintenance runs as background jobs on the server through its agent.</p>
      <div className="flex flex-wrap gap-2">
        <Button variant="outline" onClick={() => void refreshDiscovery()} disabled={refreshing}>
          {refreshing ? <Loader2Icon className="animate-spin" aria-hidden="true" /> : <SparklesIcon aria-hidden="true" />}
          Refresh discovery
        </Button>
        <Button variant="outline" onClick={() => setPruneOpen(true)}>
          Prune Docker resources…
        </Button>
      </div>
      {error && (
        <p role="alert" className="text-xs text-danger">
          {error}
        </p>
      )}
      <div className="grid gap-2">
        {jobs.map(({ job, label }) => (
          <JobProgress key={job.id} jobId={job.id} label={label} api={api} />
        ))}
      </div>
      <PruneDialog
        open={pruneOpen}
        onOpenChange={setPruneOpen}
        serverName={server.name}
        onSubmit={async (body: PruneRequest, confirm) => {
          const job = await api.prune(server.id, body, confirm);
          setJobs((j) => [{ job, label: "Prune Docker resources" }, ...j]);
        }}
      />
    </section>
  );
}

export function DangerZone({
  server,
  api,
  onAgentReset,
  onDeleted,
}: {
  server: Server;
  api: ServersApi;
  onAgentReset: () => void;
  onDeleted: () => void;
}) {
  const [resetOpen, setResetOpen] = React.useState(false);
  const [deleteOpen, setDeleteOpen] = React.useState(false);
  return (
    <section aria-labelledby="danger-title" className="grid gap-3">
      <SectionTitle index="04">
        <span id="danger-title" className="text-danger">
          Danger zone
        </span>
      </SectionTitle>
      <div className="grid gap-px border border-danger/50 bg-danger/30 [&>*]:bg-card">
        <div className="flex flex-wrap items-center justify-between gap-3 p-4">
          <div className="max-w-xl">
            <p className="text-sm font-medium">Reset agent</p>
            <p className="text-xs text-muted-foreground">
              Revokes the agent&apos;s certificates and closes its connection. The agent has to enroll again with a new join token.
            </p>
          </div>
          <Button variant="destructive" onClick={() => setResetOpen(true)}>
            Reset agent…
          </Button>
        </div>
        <div className="flex flex-wrap items-center justify-between gap-3 p-4">
          <div className="max-w-xl">
            <p className="text-sm font-medium">Delete server</p>
            <p className="text-xs text-muted-foreground">Removes the server from Aethera. Containers on the machine are not touched.</p>
          </div>
          <Button variant="destructive" onClick={() => setDeleteOpen(true)}>
            Delete server…
          </Button>
        </div>
      </div>
      <ConfirmDialog
        open={resetOpen}
        onOpenChange={setResetOpen}
        resourceName={server.name}
        title="Reset agent"
        confirmLabel="Reset agent"
        description="The agent loses access immediately and must re-enroll with a new join token."
        onConfirm={async (name) => {
          await api.resetAgent(server.id, name);
          toast.success("Agent reset");
          onAgentReset();
        }}
      />
      <ConfirmDialog
        open={deleteOpen}
        onOpenChange={setDeleteOpen}
        resourceName={server.name}
        title="Delete server"
        confirmLabel="Delete server"
        description="This removes the server record. This cannot be undone."
        onConfirm={async (name) => {
          await api.remove(server.id, name);
          toast.success("Server deleted");
          onDeleted();
        }}
      />
    </section>
  );
}

export function SettingsTab({
  server,
  api = serversApi,
  onServerChanged,
  onDeleted,
}: {
  server: Server;
  api?: ServersApi;
  onServerChanged: (s?: Server) => void;
  onDeleted: () => void;
}) {
  return (
    <div className="grid gap-10">
      <section aria-labelledby="general-title" className="grid gap-3">
        <SectionTitle index="01">
          <span id="general-title">General</span>
        </SectionTitle>
        <GeneralForm key={server.updatedAt as string} server={server} api={api} onSaved={(s) => onServerChanged(s)} />
      </section>
      <JoinTokensSection server={server} api={api} />
      <MaintenanceSection server={server} api={api} />
      <DangerZone server={server} api={api} onAgentReset={() => onServerChanged()} onDeleted={onDeleted} />
    </div>
  );
}
