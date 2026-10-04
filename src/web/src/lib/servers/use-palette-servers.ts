"use client";

import * as React from "react";
import { serversApi, type ServersApi } from "./api";
import type { Server } from "./types";

/**
 * Servers for the command palette. Fetched when the palette opens (not before),
 * best effort: if the call fails the palette simply has no server entries.
 */
export function usePaletteServers(open: boolean, api: ServersApi = serversApi): Server[] {
  const [servers, setServers] = React.useState<Server[]>([]);
  React.useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    api
      .list({ limit: 50, sort: "name" }, { signal: controller.signal })
      .then((page) => {
        if (!controller.signal.aborted) setServers(page.items);
      })
      .catch(() => {
        /* palette stays usable without server entries */
      });
    return () => controller.abort();
  }, [open, api]);
  return servers;
}
