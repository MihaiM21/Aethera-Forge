import type { NotificationsApi } from "@/lib/notifications/api";
import type { Alert, Channel, EmailSettings, NotificationEvent } from "@/lib/notifications/types";

export const NOW = "2026-10-24T10:00:00.000Z";

/** A partial API: the test supplies the calls it expects, anything else is a bug and throws. */
export function fakeNotifications(partial: Record<string, Record<string, unknown>>): NotificationsApi {
  return new Proxy(partial, {
    get(target, group: string) {
      return new Proxy(target[group] ?? {}, {
        get(inner, name: string) {
          if (name in inner) return inner[name];
          return () => {
            throw new Error(`Unexpected API call: ${group}.${name}`);
          };
        },
      });
    },
  }) as unknown as NotificationsApi;
}

const event = (type: string, group: string, label: string, def: boolean): NotificationEvent => ({ type, group, label, description: `${label} description`, default: def });

export const EVENTS: NotificationEvent[] = [
  event("deployment.succeeded", "Deployments", "Deployment succeeded", false),
  event("deployment.failed", "Deployments", "Deployment failed", true),
  event("backup.succeeded", "Backups", "Backup succeeded", false),
  event("backup.failed", "Backups", "Backup failed", true),
  event("server.unreachable", "Servers", "Server unreachable", true),
  event("server.recovered", "Servers", "Server recovered", true),
];

export function makeEmail(over: Partial<EmailSettings> = {}): EmailSettings {
  return {
    configured: true, enabled: true, transport: "smtp", smtpHost: "smtp.example.com", smtpPort: 587, smtpEncryption: "startTls", smtpUsername: "mailer",
    hasSmtpPassword: true, hasResendApiKey: false, fromAddress: "aethera@example.com", fromName: "Aethera", lastTestedAt: null, lastTestError: null, updatedAt: NOW, ...over,
  };
}

export function makeChannel(over: Partial<Channel> = {}): Channel {
  return {
    id: "ch-1", name: "Ops", kind: "email", enabled: true, sendToMembers: true, emailRecipients: ["ops@example.com"], webhookUrl: null, hasWebhookSecret: false,
    events: ["deployment.failed"], projectId: null, environmentId: null, createdAt: NOW, updatedAt: NOW, ...over,
  };
}

export function makeAlert(over: Partial<Alert> = {}): Alert {
  return {
    id: "al-1", type: "server.unreachable", severity: "critical", state: "open", title: "Server web-1 is unreachable", message: "No agent session.", resourceType: "server",
    resourceId: "srv-1", resource: "web-1", path: "/servers/srv-1", details: {}, openedAt: NOW, lastSeenAt: NOW, resolvedAt: null, notificationCount: 1,
    acknowledgedAt: null, acknowledgedBy: null, ...over,
  };
}
