import type { Metadata } from "next";
import { Eyebrow } from "@/components/aethera/eyebrow";
import { DetailLink } from "@/components/shell/detail-link";
import { PlaceholderPage } from "@/components/shell/placeholder-page";

export const metadata: Metadata = { title: "Projects" };

export default function Page() {
  return (
    <div className="flex flex-col gap-8">
      <PlaceholderPage href="/projects" />
      <p className="flex flex-wrap items-center gap-3 text-xs text-muted-foreground">
        <Eyebrow muted>Routing example</Eyebrow>
        <DetailLink
          href="/projects/demo-project"
          className="font-mono text-lime underline-offset-4 hover:underline"
        >
          /projects/demo-project
        </DetailLink>
        <span>served from the single exported shell projects/_.html</span>
      </p>
    </div>
  );
}
