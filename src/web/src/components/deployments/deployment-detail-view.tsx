"use client";

import * as React from "react";
import Link from "next/link";
import { ChevronLeftIcon, RocketIcon, Undo2Icon } from "lucide-react";
import { DotGrid } from "@/components/aethera/dot-grid";
import { EmptyState } from "@/components/aethera/empty-state";
import { LogViewer } from "@/components/aethera/log-viewer";
import { PageHeader } from "@/components/aethera/page-header";
import { TerminalCard } from "@/components/aethera/terminal-card";
import { ErrorPanel, Facts, SectionTitle } from "@/components/servers/common";
import { DetailLink } from "@/components/shell/detail-link";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { isApiError } from "@/lib/api/errors";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import { deploymentDuration, isDeploymentActive, shortSha, stepLabel, TRIGGER_LABEL } from "@/lib/resources/status";
import { useDeploymentLog } from "@/lib/resources/use-logs";
import { errorMessage } from "@/lib/servers/errors";
import { formatDateTime, toNum } from "@/lib/servers/format";
import { usePolled } from "@/lib/servers/use-polled";
import { useResourceId } from "@/lib/use-resource-id";
import { DeploymentStatusPill } from "./badges";
import { DeploymentPipeline } from "./pipeline";
import { RollbackDialog } from "./rollback-dialog";
import { navigateTo } from "@/lib/navigate";

/** Deployment detail: header, pipeline stepper with the failed step highlighted, and the build and pipeline logs. */
export function DeploymentDetailView({ api = resourcesApi, id: idProp }: { api?: ResourcesApi; id?: string }) {
  const routeId = useResourceId();
  const id = idProp ?? routeId;
  const [rollbackOpen, setRollbackOpen] = React.useState(false);
  const [actionError, setActionError] = React.useState<string | null>(null);
  const [tab, setTab] = React.useState<"build" | "deploy" | null>(null);

  const dep = usePolled((signal) => api.deployments.get(id!, { signal }), String(id), { intervalMs: 2500, enabled: id !== null });
  const d = dep.data;
  const active = d ? isDeploymentActive(d.status) : true;
  // The id is a workload id: an application, or a service (the API answers 404 for the wrong kind).
  const workload = usePolled(
    async (signal) => {
      try {
        const app = await api.applications.get(d!.applicationId, { signal });
        return { kind: "applications" as const, name: app.name };
      } catch (e) {
        if (!isApiError(e) || e.status !== 404) throw e;
        const svc = await api.services.get(d!.applicationId, { signal });
        return { kind: "services" as const, name: svc.name };
      }
    },
    `workload:${d?.applicationId}`,
    { intervalMs: null, enabled: Boolean(d) },
  );
  const log = useDeploymentLog(api, id, tab ?? undefined);
  const [now, setNow] = React.useState(() => new Date());
  React.useEffect(() => {
    if (!active) return;
    const t = setInterval(() => setNow(new Date()), 1000);
    return () => clearInterval(t);
  }, [active]);
  React.useEffect(() => {
    if (d) document.title = `Deployment #${toNum(d.number)} · Aethera`;
  }, [d]);

  if (id === null || (dep.loading && !d)) {
    return (
      <div className="flex flex-col gap-6" role="status" aria-label="Loading deployment">
        <Skeleton className="h-6 w-24" />
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-64 w-full" />
      </div>
    );
  }
  if (isApiError(dep.error) && dep.error.status === 404) {
    return (
      <DotGrid fade className="border border-border">
        <EmptyState
          icon={RocketIcon}
          label="404"
          title="Deployment not found"
          description="It may belong to another organization, or the link is wrong."
          action={
            <Button asChild>
              <Link href="/deployments">All deployments</Link>
            </Button>
          }
        />
      </DotGrid>
    );
  }
  if (!d) return <ErrorPanel error={dep.error} title="Could not load the deployment" onRetry={() => void dep.refresh()} />;

  const logTab = tab ?? (log.sources.includes("build") ? "build" : "deploy");
  const kind = workload.data?.kind ?? "applications";
  const appName = workload.data?.name ?? "Workload";

  async function redeploy() {
    setActionError(null);
    try {
      const next = await (kind === "services" ? api.services.deploy(d!.applicationId) : api.applications.redeploy(d!.applicationId));
      navigateTo(`/deployments/${encodeURIComponent(next.id)}`);
    } catch (e) {
      setActionError(errorMessage(e));
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        eyebrow={`Deployment · ${TRIGGER_LABEL[d.trigger] ?? d.trigger}`}
        title={
          <span className="flex flex-wrap items-center gap-3">
            #{toNum(d.number)}
            <DeploymentStatusPill status={d.status} />
          </span>
        }
        description={
          <span>
            <DetailLink href={`/${kind}/${encodeURIComponent(d.applicationId)}`} className="text-lime underline-offset-4 hover:underline">
              {appName}
            </DetailLink>
            {d.commitMessage ? ` · ${d.commitMessage}` : ""}
          </span>
        }
        actions={
          <>
            <Button variant="outline" asChild>
              <DetailLink href={`/${kind}/${encodeURIComponent(d.applicationId)}#deployments`}>
                <ChevronLeftIcon aria-hidden="true" /> {kind === "services" ? "Service" : "Application"}
              </DetailLink>
            </Button>
            <Button variant="outline" onClick={() => setRollbackOpen(true)}>
              <Undo2Icon aria-hidden="true" /> Roll back…
            </Button>
            <Button onClick={() => void redeploy()}>
              <RocketIcon aria-hidden="true" /> Redeploy
            </Button>
          </>
        }
      />
      {actionError && (
        <p role="alert" className="border border-danger bg-danger-soft px-3 py-2 text-xs text-danger">
          {actionError}
        </p>
      )}
      {d.status === "failed" && (
        <div role="alert" className="border border-danger/60 bg-danger-soft px-4 py-3 text-sm">
          <p className="font-medium text-danger">
            Failed at {stepLabel(d.failedStep)}
            {d.failureCode ? ` · ${d.failureCode}` : ""}
          </p>
          {d.failureReason && <p className="text-xs text-muted-foreground">{d.failureReason}</p>}
        </div>
      )}

      <div className="grid gap-8 lg:grid-cols-[minmax(0,22rem)_minmax(0,1fr)]">
        <section aria-label="Pipeline" className="flex flex-col gap-4">
          <SectionTitle index="01">Pipeline</SectionTitle>
          <DeploymentPipeline deployment={d} />
          <Facts
            rows={[
              ["Duration", deploymentDuration(d, now)],
              ["Started", formatDateTime(d.startedAt)],
              ["Finished", formatDateTime(d.finishedAt)],
              ["Strategy", d.strategy],
              ["Commit", d.commitSha ? `${shortSha(d.commitSha)}${d.commitAuthor ? ` by ${d.commitAuthor}` : ""}` : null],
              ["Ref", d.ref],
              ["Image", d.imageRef],
              ["Digest", d.imageDigest ? d.imageDigest.slice(0, 19) : null],
              ["Rollback", d.rollbackOfDeploymentId ? <DetailLink key="r" href={`/deployments/${d.rollbackOfDeploymentId}`} className="text-lime hover:underline">of an earlier deployment</DetailLink> : null],
            ]}
          />
          <div className="flex flex-wrap gap-1.5">
            {d.isRollbackPoint && <Badge tone="outline">rollback point</Badge>}
            {d.canRollbackTo && <Badge tone="success">image available</Badge>}
          </div>
        </section>

        <section aria-label="Logs" className="flex min-w-0 flex-col gap-4">
          <SectionTitle index="02">Logs</SectionTitle>
          <TerminalCard path={`deployments/${toNum(d.number)}`} label={active ? "live" : "ended"} plate={false}>
            <Tabs value={logTab} onValueChange={(v) => setTab(v as "build" | "deploy")}>
              <TabsList>
                {log.sources.includes("build") && <TabsTrigger value="build">Build</TabsTrigger>}
                <TabsTrigger value="deploy">Deploy</TabsTrigger>
              </TabsList>
              <TabsContent value={logTab} className="grid gap-2">
                {log.error && <ErrorPanel error={log.error} title="Could not read the log" />}
                <LogViewer
                  label={`${logTab} log`}
                  chunks={log.chunks}
                  status={log.loading ? "loading…" : log.ended ? "ended" : "live"}
                  downloadHref={api.deployments.logDownloadUrl(d.id, logTab)}
                  downloadName={`deployment-${toNum(d.number)}-${logTab}.log`}
                  emptyText={log.ended ? "This stream produced no output." : "Waiting for output…"}
                  maxHeight="32rem"
                />
              </TabsContent>
            </Tabs>
          </TerminalCard>
        </section>
      </div>

      <RollbackDialog
        applicationId={d.applicationId}
        kind={kind}
        currentDeploymentId={d.id}
        open={rollbackOpen}
        onOpenChange={setRollbackOpen}
        preselect={d.canRollbackTo ? d.id : undefined}
        onStarted={(next) => navigateTo(`/deployments/${encodeURIComponent(next.id)}`)}
        api={api}
      />
    </div>
  );
}
