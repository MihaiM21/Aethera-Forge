import type { Metadata } from "next";
import { PlaceholderPage } from "@/components/shell/placeholder-page";

export const metadata: Metadata = { title: "Settings" };

export default function Page() {
  return <PlaceholderPage href="/settings" />;
}
