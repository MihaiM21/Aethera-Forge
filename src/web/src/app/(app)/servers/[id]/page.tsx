import type { Metadata } from "next";
import { ServerDetailView } from "@/components/servers/server-detail-view";

export const metadata: Metadata = { title: "Server" };

// Static export: one HTML shell for every server id (docs/architecture/0005-web-routing.md).
// The API serves `servers/_.html` for `/servers/{anything}`; the client reads the real id from the URL.
export const dynamicParams = false;

export function generateStaticParams() {
  return [{ id: "_" }];
}

export default function ServerDetailPage() {
  return <ServerDetailView />;
}
