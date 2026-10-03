"use client";

import * as React from "react";
import {
  BellIcon,
  CircleCheckIcon,
  ListChecksIcon,
  PlusIcon,
  RocketIcon,
} from "lucide-react";
import { EmptyState } from "@/components/aethera/empty-state";
import { NumberedCard, NumberedCardGrid } from "@/components/aethera/numbered-card";
import { PageHeader } from "@/components/aethera/page-header";
import { StatusDot } from "@/components/aethera/status-dot";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { useAuth } from "@/lib/auth/auth-context";
import { cn } from "@/lib/utils";

function Panel({
  title,
  meta,
  className,
  children,
}: {
  title: string;
  meta?: string;
  className?: string;
  children: React.ReactNode;
}) {
  return (
    <Card className={className}>
      <CardHeader className="items-center py-2.5">
        <CardTitle className="mono-label text-xs font-medium tracking-[0.14em] text-muted-foreground uppercase">
          {title}
        </CardTitle>
        {meta && <span className="font-mono text-2xs text-muted-foreground">{meta}</span>}
      </CardHeader>
      <CardContent className="p-0">{children}</CardContent>
    </Card>
  );
}

function SkeletonRows({ rows = 3, dot = true }: { rows?: number; dot?: boolean }) {
  return (
    <ul className="divide-y divide-border" aria-hidden="true">
      {Array.from({ length: rows }, (_, i) => (
        <li key={i} className="flex items-center gap-3 px-4 py-3">
          {dot && <StatusDot tone="idle" hollow />}
          <Skeleton className="h-3 w-32" />
          <Skeleton className="ml-auto h-3 w-16" />
        </li>
      ))}
    </ul>
  );
}

function Meter({ label }: { label: string }) {
  return (
    <div className="grid gap-1.5">
      <div className="flex items-center justify-between font-mono text-2xs tracking-wider text-muted-foreground uppercase">
        <span>{label}</span>
        <span className="tabular">--%</span>
      </div>
      <div className="h-1.5 bg-muted" aria-hidden="true">
        <Skeleton className="h-full w-2/5" />
      </div>
    </div>
  );
}

export function DashboardView() {
  const { me } = useAuth();

  return (
    <div className="flex flex-col gap-8">
      <PageHeader
        eyebrow="Overview"
        title="Dashboard"
        description={
          me
            ? `Signed in as ${me.user.displayName} · ${me.organization.name}`
            : "Everything running on this installation, at a glance."
        }
        actions={
          <Button disabled title="Available in a later work package">
            <PlusIcon aria-hidden="true" />
            New application
          </Button>
        }
      />

      <NumberedCardGrid>
        <NumberedCard
          index="01"
          title="Servers"
          description="Online · degraded · offline"
          className="min-h-36"
        >
          <Skeleton className="mb-2 h-7 w-12" />
        </NumberedCard>
        <NumberedCard
          index="02"
          title="Applications"
          description="Running · deploying · failed"
          className="min-h-36"
        >
          <Skeleton className="mb-2 h-7 w-12" />
        </NumberedCard>
        <NumberedCard
          index="03"
          title="Deployments"
          description="Last 24 hours"
          className="min-h-36"
        >
          <Skeleton className="mb-2 h-7 w-12" />
        </NumberedCard>
        <NumberedCard
          index="04"
          title="Active jobs"
          description="Queued and running now"
          className="min-h-36"
        >
          <Skeleton className="mb-2 h-7 w-12" />
        </NumberedCard>
      </NumberedCardGrid>

      <div className="grid gap-6 lg:grid-cols-2">
        <Panel title="Server status" meta="live">
          <SkeletonRows />
        </Panel>
        <Panel title="Application status" meta="live">
          <SkeletonRows />
        </Panel>

        <Panel title="Recent deployments">
          <Table>
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                <TableHead>Application</TableHead>
                <TableHead>Commit</TableHead>
                <TableHead>Status</TableHead>
                <TableHead className="text-right">Duration</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={4} className="h-auto p-0">
                  <EmptyState
                    icon={RocketIcon}
                    title="No deployments yet"
                    description="Deploy an application to see its history here."
                    className="py-8"
                  />
                </TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </Panel>

        <Panel title="Failed deployments">
          <EmptyState
            icon={CircleCheckIcon}
            title="No failed deployments"
            description="Failures show where the pipeline stopped and why."
            className="py-8"
          />
        </Panel>

        <Panel title="Resource usage" meta="all servers">
          <div className="grid gap-4 p-4">
            <Meter label="CPU" />
            <Meter label="Memory" />
            <Meter label="Disk" />
          </div>
        </Panel>

        <Panel title="Active jobs">
          <EmptyState
            icon={ListChecksIcon}
            title="No jobs running"
            description="Builds, deployments and server tasks appear here while they run."
            className="py-8"
          />
        </Panel>

        <Panel title="Alerts" className={cn("lg:col-span-2")}>
          <EmptyState
            icon={BellIcon}
            title="All quiet"
            description="Unreachable servers, failing health checks and high usage will be listed here."
            className="py-8"
          />
        </Panel>
      </div>
    </div>
  );
}
