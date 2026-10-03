import type { Metadata } from "next";
import { PlaceholderPage } from "@/components/shell/placeholder-page";

export const metadata: Metadata = { title: "Applications" };

export default function Page() {
  return <PlaceholderPage href="/applications" />;
}
