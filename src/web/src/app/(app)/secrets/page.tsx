import type { Metadata } from "next";
import { SecretsView } from "@/components/ops/secrets-view";

export const metadata: Metadata = { title: "Secrets" };

export default function Page() {
  return <SecretsView />;
}
