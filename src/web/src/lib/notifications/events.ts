import type { Alert, AlertSeverity, NotificationEvent } from "./types";

export type EventGroup = { name: string; events: NotificationEvent[] };

/** Groups the catalogue by its `group`, keeping the order the API lists them in. */
export function groupEvents(events: NotificationEvent[]): EventGroup[] {
  const groups: EventGroup[] = [];
  for (const event of events) {
    let group = groups.find((g) => g.name === event.group);
    if (!group) {
      group = { name: event.group, events: [] };
      groups.push(group);
    }
    group.events.push(event);
  }
  return groups;
}

/** The events a new channel subscribes to: the catalogue's defaults (everything except the success events). */
export function defaultEvents(events: NotificationEvent[]): string[] {
  return events.filter((e) => e.default).map((e) => e.type);
}

/** `a@x.com, b@y.com` or one per line -> trimmed, unique addresses. */
export function parseRecipients(text: string): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const part of text.split(/[\s,;]+/)) {
    const address = part.trim();
    if (address && !seen.has(address.toLowerCase())) {
      seen.add(address.toLowerCase());
      out.push(address);
    }
  }
  return out;
}

export function severityTone(severity: AlertSeverity): "danger" | "warning" | "info" {
  return severity === "critical" ? "danger" : severity === "warning" ? "warning" : "info";
}

const SEVERITY_ORDER: Record<AlertSeverity, number> = { critical: 0, warning: 1, info: 2 };

/** Open alerts: most severe first, then the newest. */
export function sortOpenAlerts(alerts: Alert[]): Alert[] {
  return [...alerts].sort((a, b) => SEVERITY_ORDER[a.severity] - SEVERITY_ORDER[b.severity] || String(b.openedAt).localeCompare(String(a.openedAt)));
}
