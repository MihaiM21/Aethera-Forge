"use client";

import * as React from "react";
import Link from "next/link";
import {
  ArrowUpRightIcon,
  ChevronDownIcon,
  InboxIcon,
  PlusIcon,
  RocketIcon,
  TrashIcon,
} from "lucide-react";
import { BootLines } from "@/components/aethera/boot-lines";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { Eyebrow } from "@/components/aethera/eyebrow";
import { NumberedCard, NumberedCardGrid } from "@/components/aethera/numbered-card";
import { PipelineSteps, type PipelineStep } from "@/components/aethera/pipeline-steps";
import { StatusDot } from "@/components/aethera/status-dot";
import { TerminalCard } from "@/components/aethera/terminal-card";
import { StatusBarView } from "@/components/shell/status-bar";
import { ThemeToggle } from "@/components/shell/theme-toggle";
import { useTheme } from "@/components/theme/theme-provider";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList, CommandShortcut } from "@/components/ui/command";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuShortcut, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Field } from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Kbd } from "@/components/ui/kbd";
import { Label } from "@/components/ui/label";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Separator } from "@/components/ui/separator";
import { Skeleton } from "@/components/ui/skeleton";
import { toast } from "@/components/ui/sonner";
import { STATUS_MAP, StatusPill, type StatusKey } from "@/components/ui/status-pill";
import { Switch } from "@/components/ui/switch";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";

const SWATCHES: Array<[string, string]> = [
  ["background", "bg-background"],
  ["card", "bg-card"],
  ["popover", "bg-popover"],
  ["muted", "bg-muted"],
  ["accent (hover)", "bg-accent"],
  ["border", "bg-border"],
  ["input", "bg-input"],
  ["primary", "bg-primary"],
  ["primary-hover", "bg-primary-hover"],
  ["lime (text)", "bg-lime"],
  ["success", "bg-success"],
  ["warning", "bg-warning"],
  ["danger", "bg-danger"],
  ["info", "bg-info"],
  ["chart-1", "bg-chart-1"],
  ["chart-2", "bg-chart-2"],
  ["chart-3", "bg-chart-3"],
  ["chart-4", "bg-chart-4"],
  ["chart-5", "bg-chart-5"],
  ["chart-6", "bg-chart-6"],
  ["chart-7", "bg-chart-7"],
  ["chart-8", "bg-chart-8"],
];

const PIPELINE: PipelineStep[] = [
  { title: "Source", meta: "github.com/acme/api @ 3f9c2d1", state: "success" },
  { title: "Build", meta: "dockerfile · 48s", state: "success" },
  { title: "Image", meta: "registry.local/acme/api:3f9c2d1", state: "running" },
  {
    title: "Health check",
    meta: "GET /healthz · 30s timeout",
    state: "failed",
    failure: {
      reason: "Container exited with code 1 before becoming healthy.",
      logTail: ["Error: connect ECONNREFUSED 10.0.0.4:5432", "    at TCPConnectWrap.afterConnect"],
    },
  },
  { title: "Network", meta: "proxy route", state: "skipped" },
  { title: "Running", state: "pending" },
];

function Section({
  index,
  title,
  children,
}: {
  index: string;
  title: string;
  children: React.ReactNode;
}) {
  return (
    <section aria-labelledby={`s-${index}`} className="flex flex-col gap-4">
      <div className="flex items-baseline gap-3 border-b border-border pb-2">
        <span className="font-mono text-2xs text-lime">{index}</span>
        <h2 id={`s-${index}`} className="text-lg font-medium tracking-subheading">
          {title}
        </h2>
      </div>
      {children}
    </section>
  );
}

function Row({ children, className }: { children: React.ReactNode; className?: string }) {
  return <div className={cn("flex flex-wrap items-center gap-3", className)}>{children}</div>;
}

function Gallery() {
  const [confirmOpen, setConfirmOpen] = React.useState(false);
  const [bootKey, setBootKey] = React.useState(0);

  return (
    <div className="flex flex-col gap-10">
      <Section index="01" title="Colour tokens">
        <div className="grid grid-cols-2 gap-px border border-border bg-border sm:grid-cols-3 lg:grid-cols-4">
          {SWATCHES.map(([name, cls]) => (
            <div key={name} className="flex items-center gap-2 bg-card p-2">
              <span className={cn("size-6 shrink-0 border border-border-strong", cls)} />
              <span className="truncate font-mono text-2xs text-muted-foreground">{name}</span>
            </div>
          ))}
        </div>
      </Section>

      <Section index="02" title="Typography">
        <div className="grid gap-3">
          <p className="ae-display">
            Make the invisible <em className="ae-accent-text not-italic">operational.</em>
          </p>
          <h3 className="text-3xl tracking-heading">Page title, 32px</h3>
          <h4 className="text-lg tracking-subheading">Card title, 18px</h4>
          <p className="text-base">Body text is 14px Geist Sans with a 1.5 line height.</p>
          <p className="text-sm text-muted-foreground">Dense UI text, 13px, muted.</p>
          <p className="font-mono text-xs">mono 12px · 3f9c2d1 · 2026-10-03T16:49:54Z</p>
          <Row>
            <Eyebrow>Deployments</Eyebrow>
            <Eyebrow muted>Secondary label</Eyebrow>
            <Kbd>Ctrl</Kbd>
            <Kbd>K</Kbd>
          </Row>
        </div>
      </Section>

      <Section index="03" title="Buttons">
        <Row>
          <Button>
            Primary <ArrowUpRightIcon aria-hidden="true" />
          </Button>
          <Button variant="secondary">Secondary</Button>
          <Button variant="outline">Outline</Button>
          <Button variant="ghost">Ghost</Button>
          <Button variant="destructive">Destructive</Button>
          <Button variant="link">Link</Button>
        </Row>
        <Row>
          <Button size="sm">Small</Button>
          <Button>Default</Button>
          <Button size="lg">Large</Button>
          <Button size="icon" aria-label="Add">
            <PlusIcon aria-hidden="true" />
          </Button>
          <Button disabled>Disabled</Button>
          <Button variant="outline" disabled>
            Disabled
          </Button>
        </Row>
      </Section>

      <Section index="04" title="Form controls">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Application name" hint="Lowercase letters, digits and dashes.">
            {(c) => <Input {...c} placeholder="my-api" />}
          </Field>
          <Field label="Domain" error="Must be a valid DNS name.">
            {(c) => <Input {...c} defaultValue="not a host" />}
          </Field>
          <Field label="Build method">
            {(c) => (
              <Select defaultValue="dockerfile">
                <SelectTrigger id={c.id}>
                  <SelectValue placeholder="Choose…" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="dockerfile">Dockerfile</SelectItem>
                  <SelectItem value="compose">Docker Compose</SelectItem>
                  <SelectItem value="nixpacks">Buildpacks</SelectItem>
                </SelectContent>
              </Select>
            )}
          </Field>
          <Field label="Disabled">
            {(c) => <Input {...c} disabled defaultValue="read-only value" />}
          </Field>
          <Field label="Notes" className="sm:col-span-2">
            {(c) => <Textarea {...c} placeholder="Anything worth remembering…" />}
          </Field>
        </div>
        <Row className="gap-6">
          <div className="flex items-center gap-2">
            <Checkbox defaultChecked aria-label="Enabled checkbox" />
            <span className="text-sm">Checked</span>
          </div>
          <div className="flex items-center gap-2">
            <Checkbox aria-label="Unchecked checkbox" />
            <span className="text-sm">Unchecked</span>
          </div>
          <div className="flex items-center gap-2">
            <Checkbox checked="indeterminate" aria-label="Indeterminate checkbox" />
            <span className="text-sm">Mixed</span>
          </div>
          <div className="flex items-center gap-2">
            <Switch defaultChecked aria-label="Auto deploy" />
            <Label className="normal-case tracking-normal">Auto deploy</Label>
          </div>
          <div className="flex items-center gap-2">
            <Switch aria-label="Maintenance mode" />
            <Label className="normal-case tracking-normal">Maintenance</Label>
          </div>
        </Row>
      </Section>

      <Section index="05" title="Badges and status pills">
        <Row>
          <Badge>Neutral</Badge>
          <Badge tone="outline">Outline</Badge>
          <Badge tone="brand">Brand</Badge>
          <Badge tone="success">Success</Badge>
          <Badge tone="warning">Warning</Badge>
          <Badge tone="danger">Danger</Badge>
          <Badge tone="info">Info</Badge>
        </Row>
        <Row>
          {(Object.keys(STATUS_MAP) as StatusKey[]).map((k) => (
            <StatusPill key={k} status={k} />
          ))}
        </Row>
        <Row className="gap-5">
          <span className="flex items-center gap-2 text-xs">
            <StatusDot tone="success" live label="live" /> live
          </span>
          <span className="flex items-center gap-2 text-xs">
            <StatusDot tone="info" live label="deploying" /> deploying
          </span>
          <span className="flex items-center gap-2 text-xs">
            <StatusDot tone="warning" label="degraded" /> degraded
          </span>
          <span className="flex items-center gap-2 text-xs">
            <StatusDot tone="danger" label="failed" /> failed
          </span>
          <span className="flex items-center gap-2 text-xs">
            <StatusDot tone="idle" hollow label="stopped" /> stopped
          </span>
        </Row>
      </Section>

      <Section index="06" title="Card, table, tabs, skeleton">
        <div className="grid gap-4 lg:grid-cols-2">
          <Card>
            <CardHeader>
              <div>
                <CardTitle>acme-api</CardTitle>
                <CardDescription>Production · server-a</CardDescription>
              </div>
              <StatusPill status="running" />
            </CardHeader>
            <CardContent className="grid gap-2 text-sm">
              <div className="flex justify-between">
                <span className="text-muted-foreground">Commit</span>
                <code className="text-xs">3f9c2d1</code>
              </div>
              <div className="flex justify-between">
                <span className="text-muted-foreground">Uptime</span>
                <span className="tabular">3d 4h</span>
              </div>
            </CardContent>
            <CardFooter>
              <Button size="sm" variant="outline">
                Logs
              </Button>
              <Button size="sm" variant="ghost">
                Restart
              </Button>
            </CardFooter>
          </Card>

          <Card>
            <CardContent className="grid gap-3">
              <Skeleton className="h-4 w-1/3" />
              <Skeleton className="h-3 w-full" />
              <Skeleton className="h-3 w-5/6" />
              <Separator />
              <Skeleton className="h-8 w-24" />
            </CardContent>
          </Card>
        </div>

        <Card>
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
              <TableRow>
                <TableCell>acme-api</TableCell>
                <TableCell className="font-mono text-xs">3f9c2d1</TableCell>
                <TableCell>
                  <StatusPill status="succeeded" />
                </TableCell>
                <TableCell className="text-right">1m 12s</TableCell>
              </TableRow>
              <TableRow>
                <TableCell>acme-web</TableCell>
                <TableCell className="font-mono text-xs">a81b0ce</TableCell>
                <TableCell>
                  <StatusPill status="failed" />
                </TableCell>
                <TableCell className="text-right">38s</TableCell>
              </TableRow>
              <TableRow>
                <TableCell>acme-worker</TableCell>
                <TableCell className="font-mono text-xs">c02e77f</TableCell>
                <TableCell>
                  <StatusPill status="deploying" />
                </TableCell>
                <TableCell className="text-right">—</TableCell>
              </TableRow>
            </TableBody>
          </Table>
        </Card>

        <Tabs defaultValue="overview">
          <TabsList>
            <TabsTrigger value="overview">Overview</TabsTrigger>
            <TabsTrigger value="deployments">Deployments</TabsTrigger>
            <TabsTrigger value="logs">Logs</TabsTrigger>
          </TabsList>
          <TabsContent value="overview" className="text-sm text-muted-foreground">
            Current deployment and health state.
          </TabsContent>
          <TabsContent value="deployments" className="text-sm text-muted-foreground">
            History with pipeline and logs.
          </TabsContent>
          <TabsContent value="logs" className="text-sm text-muted-foreground">
            Live container logs.
          </TabsContent>
        </Tabs>

        <ScrollArea className="h-24 border border-border">
          <div className="ae-log p-3">
            {Array.from({ length: 12 }, (_, i) => (
              <div key={i}>
                <span className="ae-log__ts">16:49:{String(10 + i).padStart(2, "0")} </span>
                <span data-stream={i === 7 ? "stderr" : undefined}>
                  {i === 7 ? "warn: slow query (412ms)" : `GET /api/items/${i} 200`}
                </span>
              </div>
            ))}
          </div>
        </ScrollArea>
      </Section>

      <Section index="07" title="Overlays and menus">
        <Row>
          <Dialog>
            <DialogTrigger asChild>
              <Button variant="outline">Open dialog</Button>
            </DialogTrigger>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>Add a server</DialogTitle>
                <DialogDescription>
                  Aethera installs its agent over SSH and connects it back to the control plane.
                </DialogDescription>
              </DialogHeader>
              <Field label="Host">{(c) => <Input {...c} placeholder="203.0.113.10" />}</Field>
              <DialogFooter>
                <Button variant="outline">Cancel</Button>
                <Button>Add server</Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>

          <Button variant="destructive" onClick={() => setConfirmOpen(true)}>
            <TrashIcon aria-hidden="true" />
            Delete application…
          </Button>
          <ConfirmDialog
            open={confirmOpen}
            onOpenChange={setConfirmOpen}
            resourceName="acme-api"
            title="Delete application"
            description="This removes the application, its containers and its deployment history. Volumes are kept."
            confirmLabel="Delete application"
            onConfirm={async () => {
              toast.success("Deleted acme-api (demo)");
            }}
          />

          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="secondary">
                Actions <ChevronDownIcon aria-hidden="true" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="start">
              <DropdownMenuLabel>acme-api</DropdownMenuLabel>
              <DropdownMenuItem>
                <RocketIcon aria-hidden="true" /> Deploy <DropdownMenuShortcut>D</DropdownMenuShortcut>
              </DropdownMenuItem>
              <DropdownMenuItem>Restart</DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem variant="destructive">
                <TrashIcon aria-hidden="true" /> Delete…
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>

          <Tooltip>
            <TooltipTrigger asChild>
              <Button variant="ghost">Hover me</Button>
            </TooltipTrigger>
            <TooltipContent>
              Tooltip <Kbd>?</Kbd>
            </TooltipContent>
          </Tooltip>

          <ThemeToggle />
        </Row>
        <Row>
          <Button variant="outline" size="sm" onClick={() => toast("Deployment queued", { description: "acme-api · 3f9c2d1" })}>
            Toast
          </Button>
          <Button variant="outline" size="sm" onClick={() => toast.success("Deployed acme-api")}>
            Success
          </Button>
          <Button variant="outline" size="sm" onClick={() => toast.warning("Disk usage above 80%")}>
            Warning
          </Button>
          <Button variant="outline" size="sm" onClick={() => toast.error("Build failed at step 2")}>
            Error
          </Button>
        </Row>
        <Command className="h-64 max-w-xl border border-border-strong">
          <CommandInput placeholder="Search or run command…" />
          <CommandList>
            <CommandEmpty>No results</CommandEmpty>
            <CommandGroup heading="Navigate">
              <CommandItem>
                Dashboard
                <CommandShortcut>g d</CommandShortcut>
              </CommandItem>
              <CommandItem>
                Servers
                <CommandShortcut>g s</CommandShortcut>
              </CommandItem>
            </CommandGroup>
            <CommandGroup heading="Actions">
              <CommandItem>Toggle theme</CommandItem>
              <CommandItem>Log out</CommandItem>
            </CommandGroup>
          </CommandList>
        </Command>
      </Section>

      <Section index="08" title="Motifs">
        <div className="grid items-start gap-8 lg:grid-cols-2">
          <TerminalCard path="boot" label="01">
            <BootLines
              key={bootKey}
              lines={["loading project model", "mounting runtime graph", "verifying boundaries"]}
            />
            <Button
              size="sm"
              variant="outline"
              className="mt-3 font-sans"
              onClick={() => setBootKey((k) => k + 1)}
            >
              Replay
            </Button>
          </TerminalCard>

          <div className="border border-border bg-card p-5">
            <PipelineSteps steps={PIPELINE} aria-label="Deployment pipeline" />
          </div>
        </div>

        <NumberedCardGrid columns={3}>
          <NumberedCard index="01" title="Servers" description="Connect a machine and install the agent." className="min-h-36" />
          <NumberedCard index="02" title="Applications" description="Deploy from a repository or an image." className="min-h-36" />
          <NumberedCard index="03" title="Domains" description="Route hostnames and issue certificates." className="min-h-36" />
        </NumberedCardGrid>

        <DotGrid fade className="border border-border">
          <EmptyState
            icon={InboxIcon}
            label="Dot grid · empty state"
            title="Nothing deployed yet"
            description="Create an application to see it here."
            action={
              <>
                <Button>
                  <PlusIcon aria-hidden="true" /> New application
                </Button>
                <Button variant="outline">Read the docs</Button>
              </>
            }
          />
        </DotGrid>
      </Section>

      <Section index="09" title="Status bar">
        <div className="grid border border-border">
          <StatusBarView status={{ state: "connected", servers: 3, jobsRunning: 1 }} />
          <StatusBarView status={{ state: "reconnecting", servers: null, jobsRunning: null }} className="border-t" />
          <StatusBarView status={{ state: "unavailable", servers: null, jobsRunning: null }} className="border-t" />
        </div>
      </Section>
    </div>
  );
}

type View = "both" | "current";

export function DesignGallery() {
  const { resolved } = useTheme();
  const [view, setView] = React.useState<View>("both");

  return (
    <div className="min-h-dvh bg-background text-foreground">
      <header className="sticky top-0 z-[var(--ae-z-sticky)] flex flex-wrap items-center gap-3 border-b border-border bg-background/95 px-4 py-2 backdrop-blur sm:px-6">
        <Link
          href="/dashboard"
          className="focus-ring font-mono text-sm font-semibold text-lime"
          aria-label="Aethera"
        >
          aethera://design
        </Link>
        <span className="hidden text-xs text-muted-foreground sm:inline">
          Every token, component and motif. Theme now: {resolved}.
        </span>
        <div className="ml-auto flex items-center gap-2">
          <div role="group" aria-label="Gallery view" className="flex border border-border">
            {(["both", "current"] as const).map((v) => (
              <button
                key={v}
                type="button"
                aria-pressed={view === v}
                onClick={() => setView(v)}
                className={cn(
                  "focus-ring h-8 px-3 font-mono text-2xs tracking-wider uppercase transition-colors",
                  view === v ? "bg-primary text-primary-foreground" : "text-muted-foreground hover:bg-accent",
                )}
              >
                {v === "both" ? "Dark + light" : "Current theme"}
              </button>
            ))}
          </div>
          <ThemeToggle />
        </div>
      </header>

      <main
        id="main"
        className={cn(
          "mx-auto grid gap-px bg-border",
          view === "both" ? "max-w-[1800px] xl:grid-cols-2" : "max-w-5xl",
        )}
      >
        {view === "both" ? (
          <>
            <div data-theme="dark" className="ae-grid-bg min-w-0 p-4 text-foreground sm:p-6">
              <p className="mono-label mb-6 text-muted-foreground">── Dark</p>
              <Gallery />
            </div>
            <div data-theme="light" className="ae-grid-bg min-w-0 p-4 text-foreground sm:p-6">
              <p className="mono-label mb-6 text-muted-foreground">── Light</p>
              <Gallery />
            </div>
          </>
        ) : (
          <div className="ae-grid-bg min-w-0 bg-background p-4 sm:p-6">
            <Gallery />
          </div>
        )}
      </main>
    </div>
  );
}
