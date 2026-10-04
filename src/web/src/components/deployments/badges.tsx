import { StatusPill } from "@/components/ui/status-pill";
import { deploymentPill, workloadPill } from "@/lib/resources/status";
import type { DeploymentStatus, WorkloadStatus } from "@/lib/resources/types";

export function DeploymentStatusPill({ status }: { status: DeploymentStatus }) {
  const p = deploymentPill(status);
  return <StatusPill status={p.status} label={p.label} />;
}

export function WorkloadStatusPill({ status }: { status: WorkloadStatus }) {
  const p = workloadPill(status);
  return <StatusPill status={p.status} label={p.label} />;
}
