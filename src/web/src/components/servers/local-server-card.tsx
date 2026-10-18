"use client";

import * as React from "react";
import { HardDriveIcon, LoaderCircleIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { toast } from "@/components/ui/sonner";
import { resourcesApi } from "@/lib/resources/api";
import { usePolled } from "@/lib/servers/use-polled";
import type { LocalServer } from "@/lib/resources/types";

export const LOCAL_POLL_MS = 4000;

type LocalServerApi = {
  localServer: (o?: { signal?: AbortSignal }) => Promise<LocalServer>;
  enableLocalServer: () => Promise<LocalServer>;
};

const busy = (s: LocalServer | null) => s?.state === "requested" || s?.state === "starting";

/**
 * "Use this machine": a fresh installation connects its own machine by itself once the owner has registered (the agent container of the
 * compose project waits for it); this card shows that happening and is the manual way (or the retry). The API creates the server and a
 * one-time join token and hands it to the waiting agent. Hidden once the agent has connected (the machine is then an ordinary row in the
 * list) and while the state is unknown.
 */
export function LocalServerCard({
  api = resourcesApi.system,
  pollMs = LOCAL_POLL_MS,
  onConnected,
}: {
  api?: LocalServerApi;
  pollMs?: number;
  onConnected?: () => void;
}) {
  const [submitting, setSubmitting] = React.useState(false);
  const wasBusy = React.useRef(false);
  const { data: state, refresh } = usePolled((signal) => api.localServer({ signal }), "local-server", { intervalMs: pollMs });
  const waiting = busy(state ?? null) || submitting;

  React.useEffect(() => {
    if (busy(state ?? null)) wasBusy.current = true;
    if (state?.connected && wasBusy.current) {
      wasBusy.current = false;
      toast.success("This machine is now a server");
      onConnected?.();
    }
  }, [state, onConnected]);

  async function enable() {
    setSubmitting(true);
    try {
      await api.enableLocalServer();
      await refresh();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Could not connect this machine");
    } finally {
      setSubmitting(false);
    }
  }

  if (!state || state.connected) return null;

  const failed = state.state === "failed";
  return (
    <section
      aria-labelledby="local-server-title"
      className="flex flex-wrap items-center gap-4 border border-border bg-card px-4 py-4"
    >
      <HardDriveIcon className="size-5 shrink-0 text-lime" aria-hidden="true" />
      <div className="min-w-0 flex-1">
        <h2 id="local-server-title" className="text-md font-medium tracking-subheading">
          Use this machine as a server
        </h2>
        <p className="text-sm text-muted-foreground" aria-live="polite">
          {waiting
            ? (state.message ?? "Starting the agent on this machine…")
            : failed
              ? `${state.message ?? "Starting the agent failed."} You can try again.`
              : state.updaterAvailable
                ? "Deploy and build on the machine Aethera itself runs on. No install command: one click connects the agent that waits here."
                : "Needs the agent container of the compose file to be running. Start it, or add this machine like any other server."}
        </p>
      </div>
      <Button onClick={() => void enable()} disabled={waiting || !state.updaterAvailable}>
        {waiting ? (
          <>
            <LoaderCircleIcon className="animate-spin motion-reduce:animate-none" aria-hidden="true" /> Connecting…
          </>
        ) : failed ? (
          "Try again"
        ) : (
          "Use this machine"
        )}
      </Button>
    </section>
  );
}
