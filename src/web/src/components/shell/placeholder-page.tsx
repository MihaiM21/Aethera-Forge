import * as React from "react";
import Link from "next/link";
import { EmptyState } from "@/components/aethera/empty-state";
import { PageHeader } from "@/components/aethera/page-header";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { NAV_ITEMS } from "./nav-items";

const EMPTY: Record<string, { title: string; hint: string; cta: string }> = {
  "/projects": {
    title: "No projects yet",
    hint: "Projects group applications, services and environments.",
    cta: "New project",
  },
  "/applications": {
    title: "No applications yet",
    hint: "Create an application, pick a repository and a build method, then deploy.",
    cta: "New application",
  },
  "/services": {
    title: "No services yet",
    hint: "Databases and caches will appear here once you add them.",
    cta: "New service",
  },
  "/servers": {
    title: "No servers connected",
    hint: "Add a server to install the Aethera agent and start deploying.",
    cta: "Add server",
  },
  "/deployments": {
    title: "No deployments yet",
    hint: "Deployments show up here with their pipeline, logs and result.",
    cta: "Deploy an application",
  },
  "/domains": {
    title: "No domains yet",
    hint: "Attach a hostname to an application to route traffic and issue certificates.",
    cta: "Add domain",
  },
  "/registries": {
    title: "No registries yet",
    hint: "Connect a container registry to pull and push images.",
    cta: "Add registry",
  },
  "/secrets": {
    title: "No secrets yet",
    hint: "Secrets are encrypted at rest, masked in the UI and redacted from logs.",
    cta: "New secret",
  },
  "/monitoring": {
    title: "Nothing to monitor yet",
    hint: "Resource usage and health checks appear once a server is connected.",
    cta: "Add server",
  },
  "/settings": {
    title: "Settings are on the way",
    hint: "Account, organization, members and API tokens arrive in a later work package.",
    cta: "Back to dashboard",
  },
};

/** Section placeholder: eyebrow, title, description and an empty state. */
export function PlaceholderPage({ href }: { href: string }) {
  const item = NAV_ITEMS.find((i) => i.href === href);
  if (!item) throw new Error(`Unknown section ${href}`);
  const empty = EMPTY[href] ?? { title: "Nothing here yet", hint: "", cta: "Back" };
  const Icon = item.icon;
  const isSettings = href === "/settings";

  return (
    <div className="flex flex-col gap-8">
      <PageHeader eyebrow={item.label} title={item.label} description={item.description} />
      <Card className="border-dashed bg-card/60">
        <EmptyState
          icon={Icon}
          label={`${item.label} · empty`}
          title={empty.title}
          description={empty.hint}
          className="py-20"
          action={
            isSettings ? (
              <Button asChild>
                <Link href="/dashboard">{empty.cta}</Link>
              </Button>
            ) : (
              <Button disabled title="Available in a later work package">
                {empty.cta}
              </Button>
            )
          }
        />
      </Card>
    </div>
  );
}
