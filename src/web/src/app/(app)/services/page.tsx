import type { Metadata } from "next";
import { ServicesView } from "@/components/services/services-view";

export const metadata: Metadata = { title: "Services" };

export default function Page() {
  return <ServicesView />;
}
