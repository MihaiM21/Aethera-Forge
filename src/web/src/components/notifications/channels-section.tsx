"use client";

import * as React from "react";
import { KeyRoundIcon, PencilIcon, PlusIcon, SendIcon, Trash2Icon } from "lucide-react";
import { FormError, SaveButton, fieldError, useAction } from "@/components/resources/form";
import { CopyButton, ErrorPanel, NativeSelect, SectionTitle } from "@/components/servers/common";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { defaultEvents, groupEvents, parseRecipients } from "@/lib/notifications/events";
import type { NotificationsApi } from "@/lib/notifications/api";
import type { Channel, ChannelKind, NotificationEvent } from "@/lib/notifications/types";
import type { ResourcesApi } from "@/lib/resources/api";
import { usePolled } from "@/lib/servers/use-polled";
import { cn } from "@/lib/utils";

/** The events of one group as checkboxes, with a checkbox for the whole group (indeterminate when some are chosen). */
export function EventPicker({ events, selected, onChange }: { events: NotificationEvent[]; selected: string[]; onChange: (next: string[]) => void }) {
  const chosen = new Set(selected);
  const toggle = (types: string[], on: boolean) => {
    const next = new Set(chosen);
    for (const t of types) {
      if (on) next.add(t);
      else next.delete(t);
    }
    onChange(events.map((e) => e.type).filter((t) => next.has(t)));
  };
  return (
    <div className="grid gap-4" role="group" aria-label="Events">
      {groupEvents(events).map((group) => {
        const types = group.events.map((e) => e.type);
        const count = types.filter((t) => chosen.has(t)).length;
        return (
          <fieldset key={group.name} className="grid gap-1 border border-border p-3">
            <legend className="px-1">
              <label className="flex items-center gap-2 font-mono text-2xs uppercase tracking-[0.1em] text-muted-foreground">
                <Checkbox
                  checked={count === types.length ? true : count === 0 ? false : "indeterminate"}
                  onCheckedChange={(v) => toggle(types, v === true)}
                  aria-label={`All ${group.name}`}
                />
                {group.name}
              </label>
            </legend>
            {group.events.map((event) => (
              <div key={event.type} className="flex items-start gap-2 py-1">
                <Checkbox id={`event-${event.type}`} checked={chosen.has(event.type)} onCheckedChange={(v) => toggle([event.type], v === true)} className="mt-0.5" />
                <Label htmlFor={`event-${event.type}`} className="grid gap-0.5 font-normal">
                  <span className="text-sm">{event.label}</span>
                  <span className="text-xs text-muted-foreground">{event.description}</span>
                </Label>
              </div>
            ))}
          </fieldset>
        );
      })}
    </div>
  );
}

function ChannelDialog({
  channel, events, api, resources, onClose, onSaved,
}: {
  channel: Channel | null;
  events: NotificationEvent[];
  api: NotificationsApi;
  resources: ResourcesApi;
  onClose: () => void;
  onSaved: (channel: Channel, created: boolean) => void;
}) {
  const [name, setName] = React.useState(channel?.name ?? "");
  const [kind, setKind] = React.useState<ChannelKind>(channel?.kind ?? "email");
  const [enabled, setEnabled] = React.useState(channel?.enabled ?? true);
  const [sendToMembers, setSendToMembers] = React.useState(channel?.sendToMembers ?? true);
  const [recipients, setRecipients] = React.useState((channel?.emailRecipients ?? []).join("\n"));
  const [url, setUrl] = React.useState(channel?.webhookUrl ?? "");
  const [selected, setSelected] = React.useState<string[]>(channel?.events ?? defaultEvents(events));
  const [projectId, setProjectId] = React.useState(channel?.projectId ?? "");
  const [environmentId, setEnvironmentId] = React.useState(channel?.environmentId ?? "");
  const projects = usePolled((signal) => resources.projects.list({ limit: 100, sort: "name" }, { signal }), "notification-projects", { intervalMs: null });
  const act = useAction();

  const projectItems = projects.data?.items ?? [];
  // An environment filter lists the environments of the chosen project, or of every project when none is chosen.
  const environmentOptions = projectItems
    .filter((p) => !projectId || p.id === projectId)
    .flatMap((p) => p.environments.map((e) => ({ id: e.id, label: projectId ? e.name : `${p.name} / ${e.name}` })));

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    const filter = { projectId: projectId || null, environmentId: environmentId || null };
    const saved = await act.run(
      () =>
        channel
          ? api.channels.update(channel.id, {
              name: name.trim(), enabled, events: selected, ...filter,
              ...(kind === "email" ? { sendToMembers, emailRecipients: parseRecipients(recipients) } : { webhookUrl: url.trim() }),
            })
          : api.channels.create({
              name: name.trim(), kind, enabled, events: selected, ...filter,
              ...(kind === "email" ? { sendToMembers, emailRecipients: parseRecipients(recipients) } : { webhookUrl: url.trim() }),
            }),
      channel ? "Channel updated" : "Channel created",
    );
    if (saved) {
      onSaved(saved, !channel);
      onClose();
    }
  }

  return (
    <Dialog open onOpenChange={(o) => !o && !act.pending && onClose()}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl">
        <form className="grid gap-4" onSubmit={onSubmit}>
          <DialogHeader>
            <DialogTitle>{channel ? `Edit ${channel.name}` : "Add notification channel"}</DialogTitle>
            <DialogDescription>Choose where messages go and which events this channel receives.</DialogDescription>
          </DialogHeader>

          <Field label="Name" error={fieldError(act.fields, "name")}>{(c) => <Input {...c} placeholder="Ops team" value={name} onChange={(e) => setName(e.target.value)} required />}</Field>

          {!channel && (
            <div role="radiogroup" aria-label="Kind" className="inline-flex w-fit border border-border-strong">
              {(["email", "webhook"] as const).map((k) => (
                <button
                  key={k}
                  type="button"
                  role="radio"
                  aria-checked={kind === k}
                  onClick={() => setKind(k)}
                  className={cn("focus-ring h-8 border-r border-border-strong px-3 font-mono text-xs uppercase tracking-[0.06em] last:border-r-0", kind === k ? "bg-primary text-primary-foreground" : "text-muted-foreground hover:bg-accent")}
                >
                  {k}
                </button>
              ))}
            </div>
          )}

          {kind === "email" ? (
            <div className="grid gap-3">
              <label className="flex items-center gap-2 text-sm">
                <Checkbox checked={sendToMembers} onCheckedChange={(v) => setSendToMembers(v === true)} aria-label="Send to administrators and owners" />
                Send to every administrator and owner
              </label>
              <Field label="Extra recipients" hint="One address per line or separated by commas." error={fieldError(act.fields, "emailRecipients")}>
                {(c) => <Textarea {...c} className="font-mono" placeholder="ops@example.com" value={recipients} onChange={(e) => setRecipients(e.target.value)} />}
              </Field>
            </div>
          ) : (
            <Field label="Webhook URL" hint="Receives a JSON POST signed with X-Aethera-Signature (HMAC-SHA256)." error={fieldError(act.fields, "webhookUrl")}>
              {(c) => <Input {...c} className="font-mono" placeholder="https://hooks.example.com/aethera" value={url} onChange={(e) => setUrl(e.target.value)} required />}
            </Field>
          )}

          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Only for project" hint="Applies to events of applications and services.">
              {(c) => (
                <NativeSelect {...c} value={projectId} onChange={(e) => { setProjectId(e.target.value); setEnvironmentId(""); }}>
                  <option value="">All projects</option>
                  {projectItems.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                </NativeSelect>
              )}
            </Field>
            <Field label="Only for environment" error={fieldError(act.fields, "environmentId")}>
              {(c) => (
                <NativeSelect {...c} value={environmentId} onChange={(e) => setEnvironmentId(e.target.value)}>
                  <option value="">All environments</option>
                  {environmentOptions.map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
                </NativeSelect>
              )}
            </Field>
          </div>

          <div className="grid gap-2">
            <p className="mono-label text-xs font-medium tracking-[0.14em] text-muted-foreground uppercase">Events</p>
            <EventPicker events={events} selected={selected} onChange={setSelected} />
            {fieldError(act.fields, "events") && <p className="text-xs text-danger">{fieldError(act.fields, "events")}</p>}
          </div>

          <label className="flex items-center gap-3 text-sm">
            <Switch checked={enabled} onCheckedChange={setEnabled} aria-label="Channel enabled" />
            Channel enabled
          </label>

          <FormError message={act.error} />
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose} disabled={act.pending}>Cancel</Button>
            <SaveButton pending={act.pending} disabled={!name.trim() || selected.length === 0 || (kind === "webhook" && !url.trim())}>
              {channel ? "Save" : "Add channel"}
            </SaveButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/** Shown once after a webhook channel is created (or its secret rotated): the signing secret cannot be read again. */
function SecretDialog({ channel, onClose }: { channel: Channel; onClose: () => void }) {
  const secret = channel.webhookSecret ?? "";
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Signing secret for {channel.name}</DialogTitle>
          <DialogDescription>
            Copy it now: it is shown only once. Verify each delivery by comparing X-Aethera-Signature with sha256= and the hex HMAC-SHA256 of the raw body, keyed with this secret.
          </DialogDescription>
        </DialogHeader>
        <div className="flex items-center gap-2">
          <code aria-label="Signing secret" className="min-w-0 flex-1 break-all border border-border bg-muted px-3 py-2 font-mono text-xs select-all">{secret}</code>
          <CopyButton text={secret} label="Copy" aria-label="Copy the signing secret" />
        </div>
        <DialogFooter>
          <Button onClick={onClose}>I have copied it</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** Channels: where notifications go, which events each one receives, a test button and the signing secret of webhooks. */
export function ChannelsSection({ api, resources, onChanged }: { api: NotificationsApi; resources: ResourcesApi; onChanged?: () => void }) {
  const list = usePolled((signal) => api.channels.list({ signal }), "notification-channels", { intervalMs: 30_000 });
  const catalogue = usePolled((signal) => api.events.list({ signal }), "notification-events", { intervalMs: null });
  const [editing, setEditing] = React.useState<Channel | "new" | null>(null);
  const [secretOf, setSecretOf] = React.useState<Channel | null>(null);
  const [results, setResults] = React.useState<Record<string, { ok: boolean; text: string }>>({});
  const act = useAction();
  const events = catalogue.data?.items ?? [];
  const rows = list.data?.items ?? [];

  async function sendTest(c: Channel) {
    const result = await act.run(() => api.channels.test(c.id));
    if (result) setResults((r) => ({ ...r, [c.id]: { ok: result.ok, text: result.ok ? "Test message sent." : `The test failed: ${result.error}` } }));
    onChanged?.();
  }

  const changed = async () => {
    await list.refresh();
    onChanged?.();
  };

  return (
    <section className="grid gap-3" aria-label="Channels">
      <SectionTitle
        index="02"
        actions={
          <Button size="sm" onClick={() => setEditing("new")} disabled={events.length === 0}>
            <PlusIcon aria-hidden="true" /> Add channel
          </Button>
        }
      >
        Channels
      </SectionTitle>
      <p className="text-xs text-muted-foreground">Each channel picks its own events. Success events are off by default; failures, recoveries and alerts are on.</p>
      <FormError message={act.error} />
      {list.error && !list.data && <ErrorPanel error={list.error} onRetry={() => void list.refresh()} title="Could not load the channels" />}
      {list.loading && <Skeleton className="h-20 w-full" />}
      {list.data && rows.length === 0 && <p className="border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">No channels yet. Add one to start receiving notifications.</p>}
      {rows.length > 0 && (
        <ul className="border border-border bg-card">
          {rows.map((c) => (
            <li key={c.id} className="border-b border-border px-4 py-3 last:border-b-0">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="min-w-0">
                  <p className="flex items-center gap-2 font-medium">
                    {c.name}
                    <Badge tone="outline">{c.kind}</Badge>
                    <Badge tone={c.enabled ? "success" : "neutral"}>{c.enabled ? "enabled" : "disabled"}</Badge>
                  </p>
                  <p className="truncate font-mono text-xs text-muted-foreground">
                    {c.kind === "webhook" ? c.webhookUrl : [c.sendToMembers ? "administrators" : null, ...c.emailRecipients].filter(Boolean).join(", ") || "no recipients"}
                    {` · ${c.events.length} event${c.events.length === 1 ? "" : "s"}`}
                    {c.projectId || c.environmentId ? " · filtered" : ""}
                  </p>
                </div>
                <div className="flex items-center gap-1">
                  <Button size="sm" variant="outline" onClick={() => void sendTest(c)} disabled={act.pending} aria-label={`Send test to ${c.name}`}>
                    <SendIcon aria-hidden="true" /> Send test
                  </Button>
                  {c.kind === "webhook" && (
                    <Button
                      size="icon-sm"
                      variant="ghost"
                      aria-label={`Rotate the signing secret of ${c.name}`}
                      onClick={async () => {
                        if (!window.confirm(`Create a new signing secret for ${c.name}? The old one stops working.`)) return;
                        const rotated = await act.run(() => api.channels.rotateSecret(c.id));
                        if (rotated) setSecretOf(rotated);
                      }}
                    >
                      <KeyRoundIcon aria-hidden="true" />
                    </Button>
                  )}
                  <Button size="icon-sm" variant="ghost" aria-label={`Edit ${c.name}`} onClick={() => setEditing(c)}>
                    <PencilIcon aria-hidden="true" />
                  </Button>
                  <Button
                    size="icon-sm"
                    variant="ghost"
                    aria-label={`Delete ${c.name}`}
                    onClick={async () => {
                      if (!window.confirm(`Delete the channel ${c.name}?`)) return;
                      if (await act.runOk(() => api.channels.remove(c.id), "Channel deleted")) await changed();
                    }}
                  >
                    <Trash2Icon aria-hidden="true" />
                  </Button>
                </div>
              </div>
              {results[c.id] && (
                <p role="status" className={cn("mt-2 border px-3 py-1.5 text-xs", results[c.id].ok ? "border-success bg-success-soft text-success" : "border-danger bg-danger-soft text-danger")}>
                  {results[c.id].text}
                </p>
              )}
            </li>
          ))}
        </ul>
      )}
      {editing && (
        <ChannelDialog
          channel={editing === "new" ? null : editing}
          events={events}
          api={api}
          resources={resources}
          onClose={() => setEditing(null)}
          onSaved={(saved, created) => {
            if (created && saved.webhookSecret) setSecretOf(saved);
            void changed();
          }}
        />
      )}
      {secretOf && <SecretDialog channel={secretOf} onClose={() => setSecretOf(null)} />}
    </section>
  );
}
