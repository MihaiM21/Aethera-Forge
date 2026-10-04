import type { Metadata } from "next";
import { RegistriesView } from "@/components/ops/registries-view";

export const metadata: Metadata = { title: "Registries" };

export default function Page() {
  return <RegistriesView />;
}
