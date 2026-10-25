"use client";

import * as React from "react";
import { FormError, SaveButton, fieldError, type FieldErrors } from "@/components/resources/form";
import { NativeSelect } from "@/components/servers/common";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { SCHEDULE_PRESETS, draftOf, draftProblem, inputOf, schedulePresetOf, type PolicyDraft } from "@/lib/backups/presets";
import type { BackupDestination, BackupPolicy, PolicyInput } from "@/lib/backups/types";
import { formatDateTime } from "@/lib/servers/format";

const COMMON_ZONES = ["UTC", "Europe/Bucharest", "Europe/London", "Europe/Berlin", "America/New_York", "America/Los_Angeles", "Asia/Tokyo"];

function zoneOptions(): string[] {
  try {
    const supported = (Intl as unknown as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf?.("timeZone");
    if (supported?.length) return Array.from(new Set(["UTC", ...supported]));
  } catch {
    /* an old engine: the short list is enough */
  }
  return COMMON_ZONES;
}

/**
 * The fields of one backup policy: destination, schedule (presets or a cron expression), time zone and retention. Used for the policy of a database or
 * volume and for Aethera's own database. The next run times come from the API (they are shown for the saved schedule), the form does not compute any.
 */
export function PolicyEditor({
  policy,
  destinations,
  defaults,
  submitLabel = "Save policy",
  pending,
  error,
  fields,
  onSubmit,
  onCancel,
}: {
  policy: BackupPolicy | null;
  destinations: BackupDestination[];
  /** Defaults of a new policy (the destination marked default is chosen when not given). */
  defaults?: Omit<PolicyInput, "destinationId">;
  submitLabel?: string;
  pending: boolean;
  error: string | null;
  fields: FieldErrors;
  onSubmit: (input: Omit<PolicyInput, "kind" | "volumeId">) => void | Promise<void>;
  onCancel?: () => void;
}) {
  const initialDestination = policy?.destinationId ?? (destinations.find((d) => d.isDefault) ?? destinations[0])?.id ?? "";
  const [draft, setDraft] = React.useState<PolicyDraft>(() => draftOf(policy, initialDestination, defaults));
  const [custom, setCustom] = React.useState(() => schedulePresetOf(draft.schedule) === "custom");
  const set = (patch: Partial<PolicyDraft>) => setDraft((d) => ({ ...d, ...patch }));
  const zones = React.useMemo(() => zoneOptions(), []);
  const zoneListId = React.useId();
  const problem = draftProblem(draft);

  return (
    <form
      className="grid gap-4"
      onSubmit={(e) => {
        e.preventDefault();
        if (!problem) void onSubmit(inputOf(draft));
      }}
    >
      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" checked={draft.enabled} onChange={(e) => set({ enabled: e.target.checked })} /> Enabled (runs on the schedule)
      </label>

      <Field label="Destination" error={fieldError(fields, "destinationId")}>
        {(c) => (
          <NativeSelect {...c} value={draft.destinationId} onChange={(e) => set({ destinationId: e.target.value })}>
            {destinations.map((d) => (
              <option key={d.id} value={d.id}>
                {d.name}
                {d.isDefault ? " (default)" : ""}
              </option>
            ))}
          </NativeSelect>
        )}
      </Field>

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label="Schedule" error={custom ? null : fieldError(fields, "schedule")}>
          {(c) => (
            <NativeSelect
              {...c}
              value={custom ? "custom" : schedulePresetOf(draft.schedule)}
              onChange={(e) => {
                if (e.target.value === "custom") setCustom(true);
                else {
                  setCustom(false);
                  set({ schedule: SCHEDULE_PRESETS.find((p) => p.key === e.target.value)!.cron });
                }
              }}
            >
              {SCHEDULE_PRESETS.map((p) => (
                <option key={p.key} value={p.key}>
                  {p.label}
                </option>
              ))}
              <option value="custom">Custom cron expression</option>
            </NativeSelect>
          )}
        </Field>
        <Field label="Time zone" error={fieldError(fields, "timeZone")}>
          {(c) => (
            <>
              <Input {...c} className="font-mono" list={zoneListId} value={draft.timeZone} onChange={(e) => set({ timeZone: e.target.value })} />
              <datalist id={zoneListId}>
                {zones.map((z) => (
                  <option key={z} value={z} />
                ))}
              </datalist>
            </>
          )}
        </Field>
      </div>

      {custom && (
        <Field label="Cron expression" hint="Five fields: minute hour day month weekday, in the time zone above. At most every 15 minutes." error={fieldError(fields, "schedule")}>
          {(c) => <Input {...c} className="font-mono" placeholder="0 3 * * *" value={draft.schedule} onChange={(e) => set({ schedule: e.target.value })} />}
        </Field>
      )}

      {policy && policy.nextRuns.length > 0 && (
        <p className="text-xs text-muted-foreground">
          Next runs of the saved schedule:{" "}
          <span className="font-mono text-foreground">{policy.nextRuns.map((r) => formatDateTime(r)).join(" · ")}</span>
        </p>
      )}

      <fieldset className="grid gap-2">
        <legend className="mb-1 font-mono text-2xs uppercase tracking-[0.06em] text-muted-foreground">Retention: the newest backup of each of the last…</legend>
        <div className="grid gap-4 sm:grid-cols-3">
          <Field label="Days" error={fieldError(fields, "keepDaily")}>
            {(c) => <Input {...c} type="number" min={0} inputMode="numeric" value={draft.keepDaily} onChange={(e) => set({ keepDaily: e.target.value })} />}
          </Field>
          <Field label="Weeks">{(c) => <Input {...c} type="number" min={0} inputMode="numeric" value={draft.keepWeekly} onChange={(e) => set({ keepWeekly: e.target.value })} />}</Field>
          <Field label="Months">{(c) => <Input {...c} type="number" min={0} inputMode="numeric" value={draft.keepMonthly} onChange={(e) => set({ keepMonthly: e.target.value })} />}</Field>
        </div>
        <p className="text-xs text-muted-foreground">Older backups are deleted from the destination after each run. The newest successful backup is always kept.</p>
      </fieldset>

      {problem && draft.destinationId && <p className="text-xs text-warning">{problem}</p>}
      <FormError message={error} />
      <div className="flex justify-end gap-2">
        {onCancel && (
          <Button type="button" variant="outline" onClick={onCancel} disabled={pending}>
            Cancel
          </Button>
        )}
        <SaveButton pending={pending} disabled={!!problem}>
          {submitLabel}
        </SaveButton>
      </div>
    </form>
  );
}
