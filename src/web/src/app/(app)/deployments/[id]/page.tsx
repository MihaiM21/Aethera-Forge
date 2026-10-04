import type { Metadata } from "next";
import { DeploymentDetailView } from "@/components/deployments/deployment-detail-view";

export const metadata: Metadata = { title: "Deployment" };

// Static export: one HTML shell for every deployment id (docs/architecture/0005-web-routing.md).
export const dynamicParams = false;

export function generateStaticParams() {
  return [{ id: "_" }];
}

export default function DeploymentDetailPage() {
  return <DeploymentDetailView />;
}
