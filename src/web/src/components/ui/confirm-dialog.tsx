"use client";

import * as React from "react";
import { AlertTriangleIcon, Loader2Icon } from "lucide-react";
import { Button } from "./button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "./dialog";
import { Input } from "./input";
import { Label } from "./label";

export type ConfirmDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Exact name (or slug) the user must type. Becomes `?confirm=` (ADR 0003). */
  resourceName: string;
  title?: string;
  description?: React.ReactNode;
  confirmLabel?: string;
  /**
   * Runs when the user confirms. The dialog stays open and shows the error if
   * this rejects; it closes on success.
   */
  onConfirm: (confirmedName: string) => void | Promise<void>;
};

/**
 * Typed-name confirmation for destructive actions. The confirm button stays
 * disabled until the input matches `resourceName` exactly (case-sensitive), so
 * the value sent as `?confirm=` is always what the user deliberately typed.
 */
export function ConfirmDialog({
  open,
  onOpenChange,
  resourceName,
  title = "Delete resource",
  description,
  confirmLabel = "Delete",
  onConfirm,
}: ConfirmDialogProps) {
  const [typed, setTyped] = React.useState("");
  const [pending, setPending] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const inputId = React.useId();
  const hintId = React.useId();

  const matches = typed === resourceName;

  function handleOpenChange(next: boolean) {
    if (pending) return; // don't dismiss mid-request
    if (!next) {
      setTyped("");
      setError(null);
    }
    onOpenChange(next);
  }

  async function submit(e?: React.FormEvent) {
    e?.preventDefault();
    if (!matches || pending) return;
    setPending(true);
    setError(null);
    try {
      await onConfirm(typed);
      setTyped("");
      onOpenChange(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : "The action failed.");
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={handleOpenChange}>
      <DialogContent>
        <form onSubmit={submit} className="grid gap-4">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2 text-danger">
              <AlertTriangleIcon className="size-5" aria-hidden="true" />
              {title}
            </DialogTitle>
            <DialogDescription>
              {description ?? "This action cannot be undone."}
            </DialogDescription>
          </DialogHeader>

          <div className="grid gap-2">
            <Label htmlFor={inputId} className="normal-case tracking-normal">
              <span>
                Type <code className="text-foreground">{resourceName}</code> to confirm
              </span>
            </Label>
            <Input
              id={inputId}
              value={typed}
              onChange={(e) => setTyped(e.target.value)}
              autoComplete="off"
              autoCapitalize="off"
              spellCheck={false}
              aria-describedby={hintId}
              aria-invalid={typed.length > 0 && !matches ? true : undefined}
              className="font-mono"
              disabled={pending}
            />
            <p id={hintId} className="text-xs text-muted-foreground">
              The name must match exactly, including case.
            </p>
          </div>

          {error && (
            <p role="alert" className="border border-danger bg-danger-soft px-3 py-2 text-xs text-danger">
              {error}
            </p>
          )}

          <DialogFooter>
            <Button
              type="button"
              variant="outline"
              onClick={() => handleOpenChange(false)}
              disabled={pending}
            >
              Cancel
            </Button>
            <Button type="submit" variant="destructive" disabled={!matches || pending}>
              {pending && <Loader2Icon className="animate-spin" aria-hidden="true" />}
              {confirmLabel}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
