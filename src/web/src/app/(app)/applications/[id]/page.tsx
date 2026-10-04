import type { Metadata } from "next";
import { ApplicationDetailView } from "@/components/applications/application-detail-view";

export const metadata: Metadata = { title: "Application" };

// Static export: one HTML shell for every application id (docs/architecture/0005-web-routing.md).
export const dynamicParams = false;

export function generateStaticParams() {
  return [{ id: "_" }];
}

export default function ApplicationDetailPage() {
  return <ApplicationDetailView />;
}
