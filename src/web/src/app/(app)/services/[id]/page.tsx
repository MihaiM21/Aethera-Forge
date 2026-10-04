import type { Metadata } from "next";
import { ServiceDetailView } from "@/components/services/service-detail-view";

export const metadata: Metadata = { title: "Service" };

// Static export: one HTML shell for every service id (docs/architecture/0005-web-routing.md).
export const dynamicParams = false;

export function generateStaticParams() {
  return [{ id: "_" }];
}

export default function ServiceDetailPage() {
  return <ServiceDetailView />;
}
