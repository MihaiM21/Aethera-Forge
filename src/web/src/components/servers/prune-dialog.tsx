"use client";

import * as React from "react";
import { AlertTriangleIcon, Loader2Icon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { errorMessage } from "@/lib/servers/errors";
import type { PruneRequest } from "@/lib/servers/types";

type Scope = Exclude<keyof PruneRequest, "olderThanHours">;

const SCOPES: Array<{ key: Scope; label: string; hint: string; destructive?: boolean }> = [
  { key: "stoppedContainers", label: "Stopped containers", hint: "Containers that have exited." },
  { key: "danglingImages", label: "Dangling images", hint: "Untagged layers left by builds." },
  { key: "unusedImages", label: "Unused images", hint: "Images no container uses. Rollback points are always kept." },
  { key: "unusedNetworks", label: "Unused networks", hint: "Networks with no attached container." },
  { key: "buildCache", label: "Build cache", hint: "Docker builder cache." },
  { key: "volumes", label: "Unused volumes", hint: "Deletes data. Anything stored only in an unused volume is lost.", destructive: true },
];

/**
 * Prune dialog. The API needs `?confirm=<server name>` only when volumes are
 * selected, so the typed-name field appears (and gates the button) only then.
 */
export function PruneDialog({
  open,
  onOpenChange,
  serverName,
  onSubmit,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  serverName: string;
  /** Resolve to close. Throw to keep the dialog open and show the message. `confirm` is set only for volume prunes. */
  onSubmit: (body: PruneRequest, confirm: string | undefined) => Promise<void>;
}) {
  const [selected, setSelected] = React.useState<ReadonlySet<Scope>>(new Set());
  const [olderThan, setOlderThan] = React.useState("");
  const [typed, setTyped] = React.useState("");
  const [pending, setPending] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const nameId = React.useId();

  const volumes = selected.has("volumes");
  const nothing = selected.size === 0;
  const nameOk = !volumes || typed === serverName;

  function reset() {
    setSelected(new Set());
    setOlderThan("");
    setTyped("");
    setError(null);
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (nothing || !nameOk || pending) return;
    const body: PruneRequest = Object.fromEntries(SCOPES.map((s) => [s.key, selected.has(s.key)]));
    const hours = Number(olderThan);
    if (olderThan.trim() && Number.isInteger(hours) && hours > 0) body.olderThanHours = hours;
    setPending(true);
    setError(null);
    try {
      await onSubmit(body, volumes ? typed : undefined);
      reset();
      onOpenChange(false);
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        if (pending) return;
        if (!next) reset();
        onOpenChange(next);
      }}
    >
      <DialogContent>
        <form onSubmit={submit} className="grid gap-4">
          <DialogHeader>
            <DialogTitle>Prune Docker resources</DialogTitle>
            <DialogDescription>
              Runs as a background job on <code className="text-foreground">{serverName}</code>. Pick what to remove.
            </DialogDescription>
          </DialogHeader>

          <fieldset className="grid gap-2">
            <legend className="sr-only">What to prune</legend>
            {SCOPES.map((s) => {
              const id = `prune-${s.key}`;
              return (
                <div key={s.key} className="flex items-start gap-2">
                  <Checkbox
                    id={id}
                    checked={selected.has(s.key)}
                    onCheckedChange={(v) =>
                      setSelected((prev) => {
                        const next = new Set(prev);
                        if (v === true) next.add(s.key);
                        else next.delete(s.key);
                        return next;
                      })
                    }
                    className="mt-0.5"
                  />
                  <Label htmlFor={id} className="grid gap-0.5 normal-case tracking-normal">
                    <span className={s.destructive ? "text-sm text-danger" : "text-sm text-foreground"}>{s.label}</span>
                    <span className="text-xs font-normal text-muted-foreground">{s.hint}</span>
                  </Label>
                </div>
              );
            })}
          </fieldset>

          <div className="grid gap-1.5">
            <Label htmlFor="prune-older" className="normal-case tracking-normal">
              Only older than (hours, optional)
            </Label>
            <Input id="prune-older" inputMode="numeric" value={olderThan} onChange={(e) => setOlderThan(e.target.value)} className="w-32 font-mono" />
          </div>

          {volumes && (
            <div className="grid gap-2 border border-danger/60 bg-danger-soft p-3">
              <p className="flex items-start gap-2 text-xs text-danger">
                <AlertTriangleIcon className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
                Removing volumes permanently deletes their data and cannot be undone.
              </p>
              <Label htmlFor={nameId} className="normal-case tracking-normal">
                <span>
                  Type <code className="text-foreground">{serverName}</code> to confirm
                </span>
              </Label>
              <Input
                id={nameId}
                value={typed}
                onChange={(e) => setTyped(e.target.value)}
                autoComplete="off"
                autoCapitalize="off"
                spellCheck={false}
                className="font-mono"
                disabled={pending}
              />
            </div>
          )}

          {error && (
            <p role="alert" className="border border-danger bg-danger-soft px-3 py-2 text-xs text-danger">
              {error}
            </p>
          )}

          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => {
                reset();
                onOpenChange(false);
              }}
              disabled={pending}
            >
              Cancel
            </Button>
            <Button type="submit" variant={volumes ? "destructive" : "primary"} disabled={nothing || !nameOk || pending}>
              {pending && <Loader2Icon className="animate-spin" aria-hidden="true" />}
              Start prune
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
