import * as React from "react";
import { CheckIcon, MinusIcon, XIcon } from "lucide-react";
import { cn } from "@/lib/utils";
import { StatusDot } from "./status-dot";

export type PipelineState = "pending" | "running" | "success" | "failed" | "skipped";

export type PipelineStep = {
  title: string;
  /** One mono line under the title (commit, image tag, duration...). */
  meta?: string;
  state: PipelineState;
  /** Shown under a failed step (spec section 40: say where and why it failed). */
  failure?: { reason: string; logTail?: string[] };
};

const cssState: Record<PipelineState, string> = {
  pending: "pending",
  running: "active",
  success: "done",
  failed: "failed",
  skipped: "skipped",
};

const stateLabel: Record<PipelineState, string> = {
  pending: "pending",
  running: "running",
  success: "done",
  failed: "failed",
  skipped: "skipped",
};

function StateMark({ state }: { state: PipelineState }) {
  const base = "inline-flex items-center gap-1 font-mono text-2xs uppercase tracking-[0.06em]";
  switch (state) {
    case "running":
      return (
        <span className={cn(base, "text-lime")}>
          <StatusDot tone="brand" live /> {stateLabel.running}
        </span>
      );
    case "success":
      return (
        <span className={cn(base, "text-success")}>
          <CheckIcon className="size-3" aria-hidden="true" /> {stateLabel.success}
        </span>
      );
    case "failed":
      return (
        <span className={cn(base, "text-danger")}>
          <XIcon className="size-3" aria-hidden="true" /> {stateLabel.failed}
        </span>
      );
    case "skipped":
      return (
        <span className={cn(base, "text-muted-foreground")}>
          <MinusIcon className="size-3" aria-hidden="true" /> {stateLabel.skipped}
        </span>
      );
    default:
      return <span className={cn(base, "text-muted-foreground")}>{stateLabel.pending}</span>;
  }
}

/**
 * Deployment lifecycle stepper: lime mono indices joined by a dashed vertical
 * connector. Each step carries its state as text + icon, not just colour.
 */
export function PipelineSteps({
  steps,
  className,
  ...props
}: Omit<React.ComponentProps<"ol">, "children"> & { steps: PipelineStep[] }) {
  return (
    <ol data-slot="pipeline-steps" className={cn("ae-pipeline", className)} {...props}>
      {steps.map((step, i) => (
        <li
          key={`${i}-${step.title}`}
          className="ae-pipeline__step"
          data-state={cssState[step.state]}
        >
          <span className="ae-pipeline__index">{String(i + 1).padStart(2, "0")}</span>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
              <span
                className={cn(
                  "ae-pipeline__title",
                  step.state === "pending" && "text-muted-foreground",
                  step.state === "skipped" && "text-muted-foreground line-through",
                )}
              >
                {step.title}
              </span>
              <StateMark state={step.state} />
            </div>
            {step.meta && <div className="ae-pipeline__meta">{step.meta}</div>}
            {step.state === "failed" && step.failure && (
              <div
                role="group"
                aria-label={`${step.title} failure details`}
                className="mt-3 border border-danger/50 bg-danger-soft"
              >
                <p className="px-3 py-2 text-xs text-danger">{step.failure.reason}</p>
                {step.failure.logTail && step.failure.logTail.length > 0 && (
                  <pre className="ae-log overflow-x-auto border-t border-danger/30 px-3 py-2">
                    {step.failure.logTail.join("\n")}
                  </pre>
                )}
              </div>
            )}
          </div>
        </li>
      ))}
    </ol>
  );
}
