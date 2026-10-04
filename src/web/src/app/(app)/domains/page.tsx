import type { Metadata } from "next";
import { DomainsView } from "@/components/ops/domains-view";

export const metadata: Metadata = { title: "Domains" };

export default function Page() {
  return <DomainsView />;
}
