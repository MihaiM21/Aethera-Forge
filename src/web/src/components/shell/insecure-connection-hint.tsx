"use client";

import * as React from "react";
import { ShieldAlertIcon } from "lucide-react";

const subscribeNever = () => () => {};

/** True for http:// pages on a host that is not this machine (localhost and loopback are treated as secure by browsers). */
export function isInsecureRemote(protocol: string, hostname: string): boolean {
  if (protocol !== "http:") return false;
  const host = hostname.replace(/^\[|\]$/g, "").toLowerCase();
  if (host === "localhost" || host.endsWith(".localhost")) return false;
  if (host === "::1" || host.startsWith("127.")) return false;
  return true;
}

/**
 * Small notice on the sign-in and setup pages when the UI is served over plain HTTP to a non-local host (a fresh install on
 * http://<ip>:8080): the password and the session cookie cross the network unencrypted.
 */
export function InsecureConnectionHint() {
  const insecure = React.useSyncExternalStore<boolean>(
    subscribeNever,
    () => isInsecureRemote(window.location.protocol, window.location.hostname),
    () => false,
  );
  if (!insecure) return null;
  return (
    <p
      role="note"
      className="mt-6 flex items-start gap-2 border border-warning/50 bg-warning-soft px-3 py-2 text-sm text-warning"
    >
      <ShieldAlertIcon className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
      <span>
        Not using HTTPS: your password and session travel unencrypted. Put a domain with a certificate in front of Aethera
        (see the install guide, &ldquo;HTTPS and a domain for the UI&rdquo;).
      </span>
    </p>
  );
}
