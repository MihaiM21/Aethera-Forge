"use client";

import * as React from "react";
import { Loader2Icon } from "lucide-react";
import { toast } from "@/components/ui/sonner";
import { Button } from "@/components/ui/button";
import { isApiError } from "@/lib/api/errors";
import { errorMessage } from "@/lib/servers/errors";

export type FieldErrors = Record<string, string>;

/** Field errors of a 400 as `{ "/pointer": message }`; the pointer is the JSON pointer of the request body. */
export function fieldErrorsOf(e: unknown): FieldErrors {
  const out: FieldErrors = {};
  if (isApiError(e)) for (const f of e.errors) if (f.message) out[f.pointer ?? f.parameter ?? ""] = f.message;
  return out;
}

/** First message whose pointer ends with `/name` (case-insensitive), so forms need not know the nesting. */
export function fieldError(errors: FieldErrors, name: string): string | null {
  const n = name.toLowerCase();
  for (const [k, v] of Object.entries(errors)) if (k.toLowerCase().endsWith(`/${n}`) || k.toLowerCase() === n) return v;
  return null;
}

/** Runs a save/action, tracks pending state, maps API field errors and toasts the result. */
export function useAction() {
  const [pending, setPending] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [fields, setFields] = React.useState<FieldErrors>({});
  const run = React.useCallback(async <T,>(fn: () => Promise<T>, success?: string): Promise<T | undefined> => {
    setPending(true);
    setError(null);
    setFields({});
    try {
      const result = await fn();
      if (success) toast.success(success);
      return result;
    } catch (e) {
      setFields(fieldErrorsOf(e));
      setError(errorMessage(e));
      return undefined;
    } finally {
      setPending(false);
    }
  }, []);
  /** Like `run` for actions without a result: true when it succeeded. */
  const runOk = React.useCallback(async (fn: () => Promise<unknown>, success?: string): Promise<boolean> => {
    let ok = false;
    await run(async () => {
      await fn();
      ok = true;
    }, success);
    return ok;
  }, [run]);
  return { pending, error, fields, run, runOk, clear: () => { setError(null); setFields({}); } };
}

export function FormError({ message }: { message: string | null }) {
  return message ? (
    <p role="alert" className="border border-danger bg-danger-soft px-3 py-2 text-xs text-danger">
      {message}
    </p>
  ) : null;
}

export function SaveButton({ pending, children = "Save changes", disabled }: { pending: boolean; children?: React.ReactNode; disabled?: boolean }) {
  return (
    <Button type="submit" disabled={pending || disabled}>
      {pending && <Loader2Icon className="animate-spin" aria-hidden="true" />}
      {children}
    </Button>
  );
}

/** Numeric text field value -> number, or undefined for empty (so the API keeps its default). */
export function numOrUndef(v: string): number | undefined {
  const t = v.trim();
  if (!t) return undefined;
  const n = Number(t);
  return Number.isFinite(n) ? n : undefined;
}
