"use client";

import * as React from "react";
import Link from "next/link";
import { ChevronLeftIcon } from "lucide-react";
import { PageHeader } from "@/components/aethera/page-header";
import { TerminalCard } from "@/components/aethera/terminal-card";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { useResourceId } from "@/lib/use-resource-id";

/**
 * Example detail route. `params.id` is always the placeholder `_` here; the
 * real id comes from `window.location` via useResourceId().
 */
export function ProjectDetailView() {
  const id = useResourceId();

  return (
    <div className="flex flex-col gap-8">
      <PageHeader
        eyebrow="Project"
        title={
          id ? <span className="font-mono tracking-normal">{id}</span> : <Skeleton className="h-7 w-48" />
        }
        description="Detail pages share one exported shell; the id is read from the URL in the browser."
        actions={
          <Button variant="outline" asChild>
            <Link href="/projects">
              <ChevronLeftIcon aria-hidden="true" />
              All projects
            </Link>
          </Button>
        }
      />
      <TerminalCard path={id ? `projects/${id}` : "projects/…"} label="01" className="max-w-xl">
        <dl className="grid grid-cols-[6rem_1fr] gap-y-1 text-muted-foreground">
          <dt>route</dt>
          <dd className="text-foreground">/projects/[id]</dd>
          <dt>id</dt>
          <dd className="text-lime">{id ?? "(placeholder)"}</dd>
          <dt>shell</dt>
          <dd className="text-foreground">projects/_.html</dd>
        </dl>
      </TerminalCard>
    </div>
  );
}
