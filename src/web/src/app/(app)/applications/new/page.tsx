import type { Metadata } from "next";
import { CreateApplicationWizard } from "@/components/applications/create-application-wizard";

export const metadata: Metadata = { title: "New application" };

export default function Page() {
  return <CreateApplicationWizard />;
}
