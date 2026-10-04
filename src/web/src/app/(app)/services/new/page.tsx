import type { Metadata } from "next";
import { NewServiceView } from "@/components/services/new-service-view";

export const metadata: Metadata = { title: "New service" };

export default function Page() {
  return <NewServiceView />;
}
