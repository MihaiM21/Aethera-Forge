import * as React from "react";
import { cn } from "@/lib/utils";
import { Label } from "./label";

/**
 * Label + control + hint/error wiring. The control must be passed through the
 * render prop so it gets the generated `id` and `aria-describedby`.
 */
export function Field({
  label,
  hint,
  error,
  className,
  children,
}: {
  label: React.ReactNode;
  hint?: React.ReactNode;
  error?: string | null;
  className?: string;
  children: (control: {
    id: string;
    "aria-describedby"?: string;
    "aria-invalid"?: true;
  }) => React.ReactNode;
}) {
  const id = React.useId();
  const descId = `${id}-desc`;
  const hasDesc = Boolean(error || hint);
  return (
    <div data-slot="field" className={cn("grid gap-1.5", className)}>
      <Label htmlFor={id}>{label}</Label>
      {children({
        id,
        "aria-describedby": hasDesc ? descId : undefined,
        "aria-invalid": error ? true : undefined,
      })}
      {hasDesc && (
        <p
          id={descId}
          className={cn("text-xs", error ? "text-danger" : "text-muted-foreground")}
        >
          {error ?? hint}
        </p>
      )}
    </div>
  );
}
