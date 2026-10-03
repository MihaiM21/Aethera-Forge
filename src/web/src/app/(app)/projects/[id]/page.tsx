import type { Metadata } from "next";
import { ProjectDetailView } from "./project-detail-view";

export const metadata: Metadata = { title: "Project" };

// Static export: one HTML shell for every project id (see
// docs/architecture/0005-web-routing.md). The API serves `projects/_.html` for
// `/projects/{anything}`; the client reads the real id from the URL.
export const dynamicParams = false;

export function generateStaticParams() {
  return [{ id: "_" }];
}

export default function ProjectDetailPage() {
  return <ProjectDetailView />;
}
