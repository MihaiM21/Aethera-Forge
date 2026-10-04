"use client";

import * as React from "react";
import { KeyRoundIcon, PlusIcon, RefreshCwIcon, ShieldIcon, Trash2Icon, UserPlusIcon } from "lucide-react";
import { PageHeader } from "@/components/aethera/page-header";
import { FormError, SaveButton, fieldError, useAction } from "@/components/resources/form";
import { CopyButton, ErrorPanel, Facts, NativeSelect, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { useAuth } from "@/lib/auth/auth-context";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import type { ApiToken, AuditEvent, GitCredential, OrganizationRole, User } from "@/lib/resources/types";
import { formatAgo, formatDateTime, toDate } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";

export const ROLES: OrganizationRole[] = ["viewer", "developer", "admin", "owner"];
export const TOKEN_SCOPES: Array<{ value: string; hint: string }> = [
  { value: "read", hint: "Read everything the role may read, except secrets" },
  { value: "write", hint: "Create, change and delete resources" },
  { value: "deploy", hint: "Deploy, roll back, start, stop; cancel and retry jobs" },
  { value: "secrets:read", hint: "List secrets (never values)" },
  { value: "secrets:write", hint: "Create, rotate, reveal and delete secrets, registries, git credentials" },
  { value: "servers:write", hint: "Servers, join tokens, agent install" },
  { value: "admin", hint: "Users, roles, other people's tokens, audit log" },
];

export const isAdminRole = (role: string | undefined) => role === "owner" || role === "admin";

// ------------------------------------------------------------------------------------------------ Account

function AccountTab({ api }: { api: ResourcesApi }) {
  const { me } = useAuth();
  const [current, setCurrent] = React.useState("");
  const [next, setNext] = React.useState("");
  const [again, setAgain] = React.useState("");
  const act = useAction();
  const mismatch = again !== "" && next !== again;
  return (
    <div className="grid max-w-2xl gap-8">
      <section className="grid gap-3">
        <SectionTitle index="01">Profile</SectionTitle>
        <Facts rows={[["Name", me?.user.displayName], ["Email", me?.user.email], ["Role", me?.role], ["Organization", me?.organization.name]]} />
      </section>
      <form
        className="grid gap-4"
        onSubmit={async (e) => {
          e.preventDefault();
          if (await act.runOk(() => api.account.changePassword(current, next), "Password changed")) {
            setCurrent("");
            setNext("");
            setAgain("");
          }
        }}
      >
        <SectionTitle index="02">Change password</SectionTitle>
        <Field label="Current password" error={fieldError(act.fields, "currentPassword")}>{(c) => <Input {...c} type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} required />}</Field>
        <Field label="New password" error={fieldError(act.fields, "newPassword")}>{(c) => <Input {...c} type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} required />}</Field>
        <Field label="Repeat the new password" error={mismatch ? "The passwords do not match." : null}>
          {(c) => <Input {...c} type="password" autoComplete="new-password" value={again} onChange={(e) => setAgain(e.target.value)} required />}
        </Field>
        <FormError message={act.error} />
        <div>
          <SaveButton pending={act.pending} disabled={!current || !next || mismatch || !again}>
            Change password
          </SaveButton>
        </div>
      </form>
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ Members

function NewUserDialog({ api, onClose, onCreated }: { api: ResourcesApi; onClose: () => void; onCreated: () => void }) {
  const [email, setEmail] = React.useState("");
  const [displayName, setDisplayName] = React.useState("");
  const [password, setPassword] = React.useState("");
  const [role, setRole] = React.useState<OrganizationRole>("developer");
  const act = useAction();
  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            if (await act.runOk(() => api.users.create({ email: email.trim(), displayName: displayName.trim(), password, role }), "Member added")) {
              onCreated();
              onClose();
            }
          }}
        >
          <DialogHeader>
            <DialogTitle>Add member</DialogTitle>
            <DialogDescription>Create the account with an initial password and share it out of band; the member can change it after signing in.</DialogDescription>
          </DialogHeader>
          <Field label="Email" error={fieldError(act.fields, "email")}>{(c) => <Input {...c} type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />}</Field>
          <Field label="Name" error={fieldError(act.fields, "displayName")}>{(c) => <Input {...c} value={displayName} onChange={(e) => setDisplayName(e.target.value)} required />}</Field>
          <Field label="Initial password" error={fieldError(act.fields, "password")}>{(c) => <Input {...c} type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} required />}</Field>
          <Field label="Role">
            {(c) => (
              <NativeSelect {...c} value={role} onChange={(e) => setRole(e.target.value as OrganizationRole)}>
                {ROLES.filter((r) => r !== "owner").map((r) => (
                  <option key={r} value={r}>
                    {r}
                  </option>
                ))}
              </NativeSelect>
            )}
          </Field>
          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
              Cancel
            </Button>
            <SaveButton pending={act.pending} disabled={!email || !displayName || !password}>
              Add member
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function MembersTab({ api }: { api: ResourcesApi }) {
  const { me } = useAuth();
  const list = usePolled((signal) => api.users.list({ signal }), "users", { intervalMs: 30_000 });
  const [adding, setAdding] = React.useState(false);
  const act = useAction();
  const rows: User[] = list.data?.items ?? [];
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle index="01" actions={<Button size="sm" onClick={() => setAdding(true)}><UserPlusIcon aria-hidden="true" /> Add member</Button>}>
        Members
      </SectionTitle>
      <FormError message={act.error} />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load members" />}
      {list.loading && <Skeleton className="h-24 w-full" />}
      <ul className="border border-border bg-card">
        {rows.map((u) => {
          const self = u.id === me?.user.id;
          return (
            <li key={u.id} className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-4 py-3 last:border-b-0">
              <div className="min-w-0">
                <p className="flex items-center gap-2 font-medium">
                  {u.displayName}
                  {self && <Badge tone="brand">you</Badge>}
                  {!u.isActive && <Badge tone="warning">deactivated</Badge>}
                </p>
                <p className="truncate font-mono text-xs text-muted-foreground">
                  {u.email} · {u.lastLoginAt != null ? `signed in ${formatAgo(u.lastLoginAt)}` : "never signed in"}
                </p>
              </div>
              <div className="flex items-center gap-2">
                <label htmlFor={`role-${u.id}`} className="sr-only">
                  Role of {u.displayName}
                </label>
                <NativeSelect
                  id={`role-${u.id}`}
                  value={u.role}
                  disabled={self || u.role === "owner"}
                  onChange={async (e) => {
                    if (await act.runOk(() => api.users.setRole(u.id, e.target.value as OrganizationRole), "Role changed")) await list.refresh();
                  }}
                  className="w-32"
                >
                  {ROLES.map((r) => (
                    <option key={r} value={r} disabled={r === "owner" && u.role !== "owner"}>
                      {r}
                    </option>
                  ))}
                </NativeSelect>
                {!self && u.role !== "owner" && (
                  <>
                    <Button size="sm" variant="outline" onClick={async () => { if (await act.runOk(() => api.users.update(u.id, { isActive: !u.isActive }), u.isActive ? "Member deactivated" : "Member activated")) await list.refresh(); }}>
                      {u.isActive ? "Deactivate" : "Activate"}
                    </Button>
                    <Button
                      size="icon-sm"
                      variant="ghost"
                      aria-label={`Remove ${u.displayName}`}
                      onClick={async () => {
                        if (!window.confirm(`Remove ${u.displayName}? Their API tokens stop working.`)) return;
                        if (await act.runOk(() => api.users.remove(u.id), "Member removed")) await list.refresh();
                      }}
                    >
                      <Trash2Icon aria-hidden="true" />
                    </Button>
                  </>
                )}
              </div>
            </li>
          );
        })}
      </ul>
      {adding && <NewUserDialog api={api} onClose={() => setAdding(false)} onCreated={() => void list.refresh()} />}
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ API tokens

function NewTokenDialog({ api, admin, onClose, onCreated }: { api: ResourcesApi; admin: boolean; onClose: () => void; onCreated: () => void }) {
  const [name, setName] = React.useState("");
  const [scopes, setScopes] = React.useState<Set<string>>(new Set(["read"]));
  const [days, setDays] = React.useState("90");
  const [created, setCreated] = React.useState<{ token: string; name: string } | null>(null);
  const act = useAction();
  const toggle = (s: string) => setScopes((p) => { const n = new Set(p); if (n.has(s)) n.delete(s); else n.add(s); return n; });
  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent>
        {created ? (
          <div className="grid gap-4">
            <DialogHeader>
              <DialogTitle>Token created</DialogTitle>
              <DialogDescription>Copy it now: it is shown only once. Use it as a bearer token against /api/v1.</DialogDescription>
            </DialogHeader>
            <div className="flex items-center gap-2">
              <code className="min-w-0 flex-1 break-all border border-border bg-muted px-2 py-1.5 font-mono text-xs">{created.token}</code>
              <CopyButton text={created.token} />
            </div>
            <DialogFooter>
              <Button onClick={onClose}>Done</Button>
            </DialogFooter>
          </div>
        ) : (
          <form
            className="grid gap-4"
            onSubmit={async (e) => {
              e.preventDefault();
              const expiresAt = days ? new Date(Date.now() + Number(days) * 86_400_000).toISOString() : undefined;
              const t = await act.run(() => api.tokens.create({ name: name.trim(), scopes: [...scopes], expiresAt }), "Token created");
              if (t) {
                setCreated({ token: t.token, name: t.name });
                onCreated();
              }
            }}
          >
            <DialogHeader>
              <DialogTitle>New API token</DialogTitle>
              <DialogDescription>A token never has more rights than you: scopes only narrow them.</DialogDescription>
            </DialogHeader>
            <Field label="Name" error={fieldError(act.fields, "name")}>{(c) => <Input {...c} placeholder="CI deploy" value={name} onChange={(e) => setName(e.target.value)} required />}</Field>
            <fieldset className="grid gap-1.5">
              <legend className="mb-1 font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Scopes</legend>
              {TOKEN_SCOPES.filter((s) => admin || s.value !== "admin").map((s) => (
                <label key={s.value} className="flex items-start gap-2 text-sm">
                  <input type="checkbox" className="mt-1" checked={scopes.has(s.value)} onChange={() => toggle(s.value)} />
                  <span>
                    <span className="font-mono text-xs">{s.value}</span>
                    <span className="block text-xs text-muted-foreground">{s.hint}</span>
                  </span>
                </label>
              ))}
            </fieldset>
            <Field label="Expires in (days)" hint="Empty = never expires.">{(c) => <Input {...c} inputMode="numeric" className="font-mono" value={days} onChange={(e) => setDays(e.target.value)} />}</Field>
            <FormError message={act.error} />
            <DialogFooter>
              <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
                Cancel
              </Button>
              <SaveButton pending={act.pending} disabled={!name.trim() || scopes.size === 0}>
                Create token
              </SaveButton>
            </DialogFooter>
          </form>
        )}
      </DialogContent>
    </Dialog>
  );
}

function TokensTab({ api, admin }: { api: ResourcesApi; admin: boolean }) {
  const list = usePolled((signal) => api.tokens.list({ signal }), "tokens", { intervalMs: 30_000 });
  const [creating, setCreating] = React.useState(false);
  const act = useAction();
  const rows: ApiToken[] = list.data?.items ?? [];
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle index="01" actions={<Button size="sm" onClick={() => setCreating(true)}><PlusIcon aria-hidden="true" /> New token</Button>}>
        API tokens
      </SectionTitle>
      <FormError message={act.error} />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load tokens" />}
      {list.loading && <Skeleton className="h-20 w-full" />}
      {list.data && rows.length === 0 && <p className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">No tokens. Create one for the CLI or CI.</p>}
      <ul className="border border-border bg-card">
        {rows.map((t) => {
          const revoked = toDate(t.revokedAt) !== null;
          return (
            <li key={t.id} className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-4 py-3 last:border-b-0">
              <div className="min-w-0">
                <p className="flex items-center gap-2 font-medium">
                  <KeyRoundIcon className="size-3.5 text-lime" aria-hidden="true" />
                  {t.name}
                  {revoked && <Badge tone="danger">revoked</Badge>}
                </p>
                <p className="font-mono text-2xs text-muted-foreground">
                  {t.prefix}… · {t.scopes.join(", ")} · {t.lastUsedAt != null ? `used ${formatAgo(t.lastUsedAt)}` : "never used"}
                  {t.expiresAt != null ? ` · expires ${formatDateTime(t.expiresAt)}` : ""}
                </p>
              </div>
              {!revoked && (
                <Button
                  size="sm"
                  variant="outline"
                  onClick={async () => {
                    if (!window.confirm(`Revoke ${t.name}? Anything using it stops working immediately.`)) return;
                    if (await act.runOk(() => api.tokens.revoke(t.id), "Token revoked")) await list.refresh();
                  }}
                >
                  Revoke
                </Button>
              )}
            </li>
          );
        })}
      </ul>
      {creating && <NewTokenDialog api={api} admin={admin} onClose={() => setCreating(false)} onCreated={() => void list.refresh()} />}
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ Git credentials

function NewGitCredentialDialog({ api, onClose, onCreated }: { api: ResourcesApi; onClose: () => void; onCreated: () => void }) {
  const [name, setName] = React.useState("");
  const [kind, setKind] = React.useState<"token" | "deployKey" | "basicAuth">("token");
  const [provider, setProvider] = React.useState("generic");
  const [username, setUsername] = React.useState("");
  const [value, setValue] = React.useState("");
  const act = useAction();
  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={async (e) => {
            e.preventDefault();
            if (await act.runOk(() => api.gitCredentials.create({ name: name.trim(), kind, provider, username: username.trim() || undefined, value }), "Credential added")) {
              onCreated();
              onClose();
            }
          }}
        >
          <DialogHeader>
            <DialogTitle>Add git credential</DialogTitle>
            <DialogDescription>For private repositories. The secret is write-only and administrators only.</DialogDescription>
          </DialogHeader>
          <Field label="Name" error={fieldError(act.fields, "name")}>{(c) => <Input {...c} value={name} onChange={(e) => setName(e.target.value)} required />}</Field>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Type">
              {(c) => (
                <NativeSelect {...c} value={kind} onChange={(e) => setKind(e.target.value as typeof kind)}>
                  <option value="token">Access token (https)</option>
                  <option value="basicAuth">Username and password</option>
                  <option value="deployKey">Deploy key (ssh)</option>
                </NativeSelect>
              )}
            </Field>
            <Field label="Provider">
              {(c) => (
                <NativeSelect {...c} value={provider} onChange={(e) => setProvider(e.target.value)}>
                  <option value="generic">Generic</option>
                  <option value="gitHub">GitHub</option>
                  <option value="gitLab">GitLab</option>
                </NativeSelect>
              )}
            </Field>
          </div>
          {kind !== "deployKey" && <Field label="Username" hint="Tokens: often x-access-token, oauth2 or your username.">{(c) => <Input {...c} className="font-mono" value={username} onChange={(e) => setUsername(e.target.value)} />}</Field>}
          <Field label={kind === "deployKey" ? "Private key" : "Token or password"} error={fieldError(act.fields, "value")}>
            {(c) => <Textarea {...c} rows={kind === "deployKey" ? 6 : 2} className="font-mono text-xs" autoComplete="off" value={value} onChange={(e) => setValue(e.target.value)} required />}
          </Field>
          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>
              Cancel
            </Button>
            <SaveButton pending={act.pending} disabled={!name.trim() || !value}>
              Add credential
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function GitTab({ api }: { api: ResourcesApi }) {
  const list = usePolled((signal) => api.gitCredentials.list({ signal }), "git-credentials", { intervalMs: null });
  const [adding, setAdding] = React.useState(false);
  const act = useAction();
  const rows: GitCredential[] = list.data?.items ?? [];
  return (
    <div className="flex flex-col gap-4">
      <SectionTitle index="01" actions={<Button size="sm" onClick={() => setAdding(true)}><PlusIcon aria-hidden="true" /> Add credential</Button>}>
        Git credentials
      </SectionTitle>
      <FormError message={act.error} />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load credentials" />}
      {list.data && rows.length === 0 && <p className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">No credentials. Public repositories need none.</p>}
      <ul className="border border-border bg-card">
        {rows.map((g) => (
          <li key={g.id} className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-4 py-3 last:border-b-0">
            <div>
              <p className="font-medium">{g.name}</p>
              <p className="font-mono text-2xs text-muted-foreground">{g.kind} · {g.provider}{g.username ? ` · ${g.username}` : ""}</p>
            </div>
            <Button
              size="icon-sm"
              variant="ghost"
              aria-label={`Delete ${g.name}`}
              onClick={async () => {
                const typed = window.prompt(`Type ${g.name} to delete this credential`);
                if (typed !== g.name) return;
                if (await act.runOk(() => api.gitCredentials.remove(g.id, typed), "Credential deleted")) await list.refresh();
              }}
            >
              <Trash2Icon aria-hidden="true" />
            </Button>
          </li>
        ))}
      </ul>
      {adding && <NewGitCredentialDialog api={api} onClose={() => setAdding(false)} onCreated={() => void list.refresh()} />}
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ Audit log

const ACTORS = ["", "user", "apiToken", "agent", "system"];

export function describeActor(e: Pick<AuditEvent, "actorType" | "actorLabel">): string {
  return e.actorLabel ?? e.actorType;
}

/** The organization's audit trail (administrators): who did what to which resource, newest first. Metadata is redacted by the API. */
function AuditTab({ api }: { api: ResourcesApi }) {
  const [action, setAction] = React.useState("");
  const [resourceType, setResourceType] = React.useState("");
  const [actorType, setActorType] = React.useState("");
  const [extra, setExtra] = React.useState<{ key: string; rows: AuditEvent[]; cursor: string | null } | null>(null);
  const [loadingMore, setLoadingMore] = React.useState(false);
  const [open, setOpen] = React.useState<string | null>(null);
  const filterKey = `${action}|${resourceType}|${actorType}`;
  const query = { limit: 50, action: action.trim() ? action.trim() : undefined, resourceType: resourceType.trim() || undefined, actorType: actorType || undefined };
  const first = usePolled((signal) => api.audit.list(query, { signal }), `audit:${filterKey}`, { intervalMs: 15_000 });
  const more = extra?.key === filterKey ? extra : null;
  const seen = new Set((first.data?.items ?? []).map((e) => e.id));
  const rows = [...(first.data?.items ?? []), ...(more?.rows ?? []).filter((e) => !seen.has(e.id))];
  const cursor = more ? more.cursor : (first.data?.nextCursor ?? null);

  async function loadMore() {
    if (!cursor) return;
    setLoadingMore(true);
    try {
      const page = await api.audit.list({ ...query, cursor });
      setExtra({ key: filterKey, rows: [...(more?.rows ?? []), ...page.items], cursor: page.nextCursor });
    } finally {
      setLoadingMore(false);
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <SectionTitle index="01" actions={<Button size="sm" variant="outline" onClick={() => void first.refresh()} disabled={first.refreshing}><RefreshCwIcon aria-hidden="true" /> Refresh</Button>}>
        Audit log
      </SectionTitle>
      <div className="flex flex-wrap items-end gap-3">
        <Field label="Action" hint="Exact, or a prefix ending in *">
          {(c) => <Input {...c} className="w-52 font-mono" placeholder="application.*" value={action} onChange={(e) => setAction(e.target.value)} />}
        </Field>
        <Field label="Resource type">{(c) => <Input {...c} className="w-40 font-mono" placeholder="application" value={resourceType} onChange={(e) => setResourceType(e.target.value)} />}</Field>
        <Field label="Actor">
          {(c) => (
            <NativeSelect {...c} value={actorType} onChange={(e) => setActorType(e.target.value)} className="w-36">
              {ACTORS.map((a) => (
                <option key={a} value={a}>
                  {a || "Anyone"}
                </option>
              ))}
            </NativeSelect>
          )}
        </Field>
      </div>
      {first.error && !first.data && <ErrorPanel error={first.error} onRetry={() => void first.refresh()} title="Could not load the audit log" />}
      {first.loading && <Skeleton className="h-32 w-full" />}
      {first.data && rows.length === 0 && <p className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">No events match.</p>}
      {rows.length > 0 && (
        <ul className="border border-border bg-card" aria-label="Audit events">
          {rows.map((e) => (
            <li key={e.id} className="border-b border-border last:border-b-0">
              <button
                type="button"
                onClick={() => setOpen(open === e.id ? null : e.id)}
                aria-expanded={open === e.id}
                className="focus-ring grid w-full gap-1 px-4 py-2.5 text-left hover:bg-accent md:grid-cols-[11rem_minmax(0,1fr)_minmax(0,1fr)] md:items-center"
              >
                <span className="font-mono text-2xs text-muted-foreground">{formatDateTime(e.occurredAt)}</span>
                <span className="truncate font-mono text-xs">{e.action}</span>
                <span className="truncate text-xs text-muted-foreground">
                  {describeActor(e)}
                  {e.resourceName ? ` → ${e.resourceName}` : e.resourceType ? ` → ${e.resourceType}` : ""}
                </span>
              </button>
              {open === e.id && (
                <div className="grid gap-2 border-t border-border bg-muted/40 px-4 py-3">
                  <Facts
                    rows={[
                      ["Resource", [e.resourceType, e.resourceId].filter(Boolean).join(" ")],
                      ["IP", e.ipAddress],
                      ["Request", e.requestId],
                      ["Actor", `${e.actorType}${e.actorUserId ? ` ${e.actorUserId}` : ""}`],
                    ]}
                  />
                  <pre className="ae-log max-h-48 overflow-auto border border-border p-2">{JSON.stringify(e.metadata ?? {}, null, 2)}</pre>
                </div>
              )}
            </li>
          ))}
        </ul>
      )}
      {cursor && (
        <div className="flex justify-center">
          <Button variant="outline" onClick={() => void loadMore()} disabled={loadingMore}>
            {loadingMore ? "Loading…" : "Load more"}
          </Button>
        </div>
      )}
    </div>
  );
}

// ------------------------------------------------------------------------------------------------ Page

export const SETTINGS_TABS = ["account", "members", "tokens", "git", "audit"] as const;
export type SettingsTab = (typeof SETTINGS_TABS)[number];

export function settingsTabFromHash(hash: string, admin: boolean): SettingsTab {
  const h = hash.replace(/^#/, "");
  const known = (SETTINGS_TABS as readonly string[]).includes(h) ? (h as SettingsTab) : "account";
  return !admin && (known === "members" || known === "audit" || known === "git") ? "account" : known;
}

/** Settings: your account and tokens for everyone; members, git credentials and the audit log for administrators. */
export function SettingsView({ api = resourcesApi }: { api?: ResourcesApi }) {
  const { me } = useAuth();
  const admin = isAdminRole(me?.role);
  const [tab, setTab] = React.useState<SettingsTab>("account");
  React.useEffect(() => {
    const apply = () => setTab(settingsTabFromHash(window.location.hash, admin));
    apply();
    window.addEventListener("hashchange", apply);
    return () => window.removeEventListener("hashchange", apply);
  }, [admin]);
  function onTab(next: string) {
    const t = settingsTabFromHash(next, admin);
    setTab(t);
    try {
      window.history.replaceState(null, "", `#${t}`);
    } catch {
      /* ignore */
    }
  }
  return (
    <div className="flex flex-col gap-6">
      <PageHeader eyebrow="Configuration" title="Settings" description="Account, organization members, API tokens and the audit trail." />
      {!admin && (
        <p role="note" className="flex items-center gap-2 border border-dashed border-border px-3 py-2 text-xs text-muted-foreground">
          <ShieldIcon className="size-3.5 text-lime" aria-hidden="true" /> Members, git credentials and the audit log need the administrator role.
        </p>
      )}
      <Tabs value={tab} onValueChange={onTab}>
        <TabsList className="overflow-x-auto" aria-label="Settings sections">
          <TabsTrigger value="account">Account</TabsTrigger>
          {admin && <TabsTrigger value="members">Members</TabsTrigger>}
          <TabsTrigger value="tokens">API tokens</TabsTrigger>
          {admin && <TabsTrigger value="git">Git credentials</TabsTrigger>}
          {admin && <TabsTrigger value="audit">Audit log</TabsTrigger>}
        </TabsList>
        <TabsContent value="account">
          <AccountTab api={api} />
        </TabsContent>
        {admin && <TabsContent value="members">{tab === "members" && <MembersTab api={api} />}</TabsContent>}
        <TabsContent value="tokens">{tab === "tokens" && <TokensTab api={api} admin={admin} />}</TabsContent>
        {admin && <TabsContent value="git">{tab === "git" && <GitTab api={api} />}</TabsContent>}
        {admin && <TabsContent value="audit">{tab === "audit" && <AuditTab api={api} />}</TabsContent>}
      </Tabs>
    </div>
  );
}

