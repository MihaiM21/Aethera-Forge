import * as React from "react";
import { PipelineSteps, type PipelineState, type PipelineStep } from "@/components/aethera/pipeline-steps";
import { formatDuration, STEP_LABEL } from "@/lib/resources/status";
import { DEPLOYMENT_STEPS, type Deployment, type DeploymentStepDto } from "@/lib/resources/types";
import { toDate } from "@/lib/servers/format";

function stepState(s: DeploymentStepDto | undefined): PipelineState {
  switch (s?.status) {
    case "running":
      return "running";
    case "succeeded":
      return "success";
    case "failed":
      return "failed";
    case "skipped":
      return "skipped";
    case "cancelled":
      return "failed";
    default:
      return "pending";
  }
}

function stepMeta(d: Deployment, s: DeploymentStepDto | undefined, step: (typeof DEPLOYMENT_STEPS)[number]): string | undefined {
  const parts: string[] = [];
  if (step === "source" && d.commitSha) parts.push(d.commitSha.slice(0, 7), ...(d.ref ? [d.ref] : []));
  if (step === "image" && d.imageRef) parts.push(d.imageRef);
  if (step === "container" && d.strategy) parts.push(d.strategy);
  const start = toDate(s?.startedAt);
  const end = toDate(s?.finishedAt);
  if (start && end) parts.push(formatDuration(end.getTime() - start.getTime()));
  return parts.length > 0 ? parts.join(" · ") : undefined;
}

/** The deployment's nine steps as the dashed-connector stepper; the failed step carries the API's code and reason. */
export function pipelineSteps(d: Deployment): PipelineStep[] {
  const byStep = new Map((d.steps ?? []).map((s) => [s.step, s] as const));
  return DEPLOYMENT_STEPS.map((step) => {
    const s = byStep.get(step);
    const state = stepState(s);
    const failedHere = state === "failed" || d.failedStep === step;
    return {
      title: STEP_LABEL[step as keyof typeof STEP_LABEL],
      meta: stepMeta(d, s, step),
      state: failedHere && d.status === "failed" ? "failed" : state,
      failure: failedHere
        ? {
            reason: [s?.errorCode ?? d.failureCode, s?.errorMessage ?? d.failureReason].filter(Boolean).join(": ") || "The step failed.",
          }
        : undefined,
    };
  });
}

export function DeploymentPipeline({ deployment, className }: { deployment: Deployment; className?: string }) {
  return <PipelineSteps className={className} aria-label="Deployment pipeline" steps={pipelineSteps(deployment)} />;
}
