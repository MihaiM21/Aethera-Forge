import type { Metadata } from "next";
import { ApplicationsView } from "@/components/applications/applications-view";

export const metadata: Metadata = { title: "Applications" };

export default function Page() {
  return <ApplicationsView />;
}
