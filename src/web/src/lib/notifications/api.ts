import { api, type ApiClient } from "@/lib/api/client";
import type {
  Alert,
  AlertState,
  Channel,
  ChannelTestResult,
  CreateChannel,
  Delivery,
  EmailSettings,
  EmailTestResult,
  NotificationEvent,
  Page,
  UpdateChannel,
  UpdateEmailSettings,
} from "./types";

type Signal = { signal?: AbortSignal };

const enc = encodeURIComponent;

/**
 * Typed wrapper over the notification endpoints: email settings, channels, deliveries, the event catalogue and alerts.
 * Paths follow the OpenAPI document; the UI has no behaviour the API lacks.
 */
export function createNotificationsApi(client: ApiClient) {
  return {
    email: {
      get: (o: Signal = {}) => client.get<EmailSettings>("/notifications/email", o),
      save: (body: UpdateEmailSettings) => client.put<EmailSettings>("/notifications/email", body),
      /** Sends a test message to the caller's address with the saved settings. The result carries the SMTP/Resend error text. */
      test: () => client.post<EmailTestResult>("/notifications/email/test"),
    },
    events: {
      list: (o: Signal = {}) => client.get<{ items: NotificationEvent[] }>("/notification-events", o),
    },
    channels: {
      list: (o: Signal = {}) => client.get<Page<Channel>>("/notification-channels", { query: { limit: 100, sort: "name" }, signal: o.signal }),
      /** A webhook channel's response carries `webhookSecret` once. */
      create: (body: CreateChannel) => client.post<Channel>("/notification-channels", body),
      update: (id: string, body: UpdateChannel) => client.patch<Channel>(`/notification-channels/${enc(id)}`, body),
      remove: (id: string) => client.delete<void>(`/notification-channels/${enc(id)}`),
      test: (id: string) => client.post<ChannelTestResult>(`/notification-channels/${enc(id)}/test`),
      rotateSecret: (id: string) => client.post<Channel>(`/notification-channels/${enc(id)}/rotate-secret`),
    },
    deliveries: {
      list: (q: { channelId?: string; limit?: number } = {}, o: Signal = {}) =>
        client.get<Page<Delivery>>("/notification-deliveries", { query: { channelId: q.channelId, limit: q.limit ?? 20 }, signal: o.signal }),
    },
    alerts: {
      list: (q: { state?: AlertState; limit?: number; cursor?: string } = {}, o: Signal = {}) =>
        client.get<Page<Alert>>("/alerts", { query: { state: q.state, limit: q.limit, cursor: q.cursor }, signal: o.signal }),
      acknowledge: (id: string) => client.post<Alert>(`/alerts/${enc(id)}/acknowledge`),
    },
  };
}

export type NotificationsApi = ReturnType<typeof createNotificationsApi>;

export const notificationsApi: NotificationsApi = createNotificationsApi(api);
