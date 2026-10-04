import * as React from "react";
import { CheckIcon } from "lucide-react";
import { cn } from "@/lib/utils";

export type StepperStep = { title: string; description?: string };

/**
 * Horizontal wizard stepper: mono lime indices joined by a dashed connector (the
 * same motif as PipelineSteps, laid out in a row; vertical on narrow screens).
 * `current` is 0-based. Completed steps show a check, the current step is
 * marked with `aria-current="step"`.
 */
export function Stepper({
  steps,
  current,
  className,
  ...props
}: Omit<React.ComponentProps<"ol">, "children"> & { steps: StepperStep[]; current: number }) {
  return (
    <ol
      data-slot="stepper"
      className={cn("flex flex-col gap-0 sm:flex-row sm:items-start", className)}
      {...props}
    >
      {steps.map((step, i) => {
        const done = i < current;
        const active = i === current;
        const last = i === steps.length - 1;
        return (
          <li
            key={step.title}
            data-state={done ? "done" : active ? "active" : "pending"}
            aria-current={active ? "step" : undefined}
            className="relative flex flex-1 gap-3 pb-6 sm:flex-col sm:gap-2 sm:pb-0 sm:pr-4"
          >
            <div className="flex items-center sm:w-full">
              <span
                className={cn(
                  "inline-flex size-6 shrink-0 items-center justify-center border font-mono text-2xs",
                  done && "border-primary bg-primary-soft text-lime",
                  active && "border-primary bg-primary text-primary-foreground",
                  !done && !active && "border-border-strong text-muted-foreground",
                )}
              >
                {done ? <CheckIcon className="size-3.5" aria-hidden="true" /> : String(i + 1).padStart(2, "0")}
              </span>
              {!last && (
                <span
                  aria-hidden="true"
                  className={cn(
                    "hidden flex-1 border-t border-dashed sm:ml-2 sm:block",
                    done ? "border-primary/60" : "border-[var(--ae-connector)]",
                  )}
                />
              )}
            </div>
            {!last && (
              <span
                aria-hidden="true"
                className="absolute top-7 bottom-1 left-3 border-l border-dashed border-[var(--ae-connector)] sm:hidden"
              />
            )}
            <div className="min-w-0">
              <div className={cn("text-sm", active ? "text-foreground" : "text-muted-foreground")}>
                {step.title}
                <span className="sr-only">{done ? " (completed)" : active ? " (current step)" : ""}</span>
              </div>
              {step.description && (
                <div className="font-mono text-2xs text-muted-foreground">{step.description}</div>
              )}
            </div>
          </li>
        );
      })}
    </ol>
  );
}
