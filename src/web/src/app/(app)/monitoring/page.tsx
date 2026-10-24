import type { Metadata } from "next";
import { MonitoringView } from "@/components/notifications/monitoring-view";

export const metadata: Metadata = { title: "Monitoring" };

export default function Page() {
  return <MonitoringView />;
}
