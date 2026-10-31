"use client";

import * as React from "react";
import { FormError } from "@/components/resources/form";
import { NativeSelect, SectionTitle } from "@/components/servers/common";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { toast } from "@/components/ui/sonner";
import { Field } from "@/components/ui/field";
import { isApiError } from "@/lib/api/errors";
import { resourcesApi, type ResourcesApi } from "@/lib/resources/api";
import type { MoveWorkloadResponse } from "@/lib/resources/types";
import { serversApi, type ServersApi } from "@/lib/servers/api";
import { describeFree } from "@/lib/servers/capacity";
import { errorMessage } from "@/lib/servers/errors";
import { usePolled } from "@/lib/servers/use-polled";

export const MOVE_CONFIRMATION_REQUIRED = "workload.move_confirmation_required";

/**
 * Moves an application or a service to another server (ADR 0012). The API does the thinking: it queues a deployment on the target, keeps the old
 * server serving until it runs, then removes the old containers. Volumes do not move; the API refuses until the caller confirms, and the panel shows
 * its explanation (and the volumes it names) before asking.
 */
export function MovePanel({
  kind,
  id,
  name,
  currentServerId,
  api = resourcesApi,
  servers = serversApi,
  onMoved,
}: {
  kind: "applications" | "services";
  id: string;
  name: string;
  currentServerId: string;
  api?: ResourcesApi;
  servers?: ServersApi;
  onMoved: () => void;
}) {
  const noun = kind === "applications" ? "application" : "service";
  const list = usePolled((signal) => servers.list({ limit: 100, sort: "name" }, { signal }), "move-servers", { intervalMs: null });
  const capacity = usePolled((signal) => servers.capacity({ signal }), "move-capacity", { intervalMs: null });
  const suggestion = usePolled((signal) => servers.suggest({ workloadId: id }, { signal }), `move-suggest-${id}`, { intervalMs: null });
  const [target, setTarget] = React.useState("");
  const [pending, setPending] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [needsConfirm, setNeedsConfirm] = React.useState<string | null>(null);
  const [result, setResult] = React.useState<MoveWorkloadResponse | null>(null);

  const others = (list.data?.items ?? []).filter((s) => s.id !== currentServerId);
  const chosen = target || suggestion.data?.suggestedServerId || "";
  const chosenId = others.some((s) => s.id === chosen) ? chosen : (others[0]?.id ?? "");
  const candidate = suggestion.data?.candidates.find((c) => c.serverId === chosenId);
  const room = capacity.data?.find((c) => c.serverId === chosenId);

  async function move(confirm: boolean) {
    setPending(true);
    setError(null);
    try {
      const doMove = kind === "applications" ? api.applications.move : api.services.move;
      const response = await doMove(id, chosenId, confirm);
      setResult(response);
      toast.success(response.deploymentId ? "Move started" : "Moved");
      onMoved();
    } catch (e) {
      if (isApiError(e) && e.code === MOVE_CONFIRMATION_REQUIRED) setNeedsConfirm(e.detail ?? "The data stays on the old server.");
      else setError(errorMessage(e));
    } finally {
      setPending(false);
    }
  }

  return (
    <section aria-labelledby="move-title" className="grid gap-3">
      <SectionTitle index="02">
        <span id="move-title">Move to another server</span>
      </SectionTitle>
      <p className="text-xs text-muted-foreground">
        The {noun} is deployed on the other server; the current one keeps serving until the new deployment runs, then its containers are removed. If the deployment
        fails nothing changes. Volumes do not move: their data stays on the old server.
      </p>
      {others.length === 0 ? (
        <p className="text-sm text-muted-foreground">There is no other server. Add one under Servers.</p>
      ) : (
        <div className="grid max-w-xl gap-3">
          <Field label="Target server">
            {(c) => (
              <NativeSelect {...c} value={chosenId} onChange={(e) => setTarget(e.target.value)}>
                {others.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.name} · {s.host}
                    {s.id === suggestion.data?.suggestedServerId ? " (suggested)" : ""}
                  </option>
                ))}
              </NativeSelect>
            )}
          </Field>
          {room && <p className="text-xs text-muted-foreground">{room.name}: {describeFree(room)}.</p>}
          {candidate && (
            <ul className="grid gap-0.5 text-xs text-muted-foreground" aria-label="Why this server">
              {candidate.reasons.map((r) => (
                <li key={r} className={candidate.eligible || r !== candidate.reasons[0] ? undefined : "text-warning"}>
                  {r}
                </li>
              ))}
            </ul>
          )}
          <FormError message={error} />
          <div>
            <Button variant="outline" disabled={pending || !chosenId} onClick={() => void move(false)}>
              Move {noun}
            </Button>
          </div>
        </div>
      )}
      {result && (
        <div role="status" className="grid gap-1 border border-border bg-card px-3 py-2 text-xs">
          <p>{result.message}</p>
          {result.warnings.map((w) => (
            <p key={w} className="text-warning">
              {w}
            </p>
          ))}
        </div>
      )}
      <ConfirmDialog
        open={needsConfirm !== null}
        onOpenChange={(o) => !o && setNeedsConfirm(null)}
        resourceName={name}
        confirmLabel="Move anyway"
        title={`Move ${name} and leave its data behind?`}
        description={`${needsConfirm ?? ""} Type its name to move it anyway.`}
        onConfirm={async () => {
          setNeedsConfirm(null);
          await move(true);
        }}
      />
    </section>
  );
}
