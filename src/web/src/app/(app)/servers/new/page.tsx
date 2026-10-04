import type { Metadata } from "next";
import { AddServerWizard } from "@/components/servers/add-server-wizard";

export const metadata: Metadata = { title: "Add server" };

export default function Page() {
  return <AddServerWizard />;
}
