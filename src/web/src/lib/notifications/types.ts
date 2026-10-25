/**
 * Types of the notification endpoints, taken from the generated OpenAPI schema (`pnpm gen:api`): email settings, channels,
 * deliveries, the event catalogue and alerts. Nothing here is invented: if the API lacks a field, the UI does not show it.
 */
import type { components } from "@/lib/api/schema";

type S = components["schemas"];

export type Page<T> = { items: T[]; nextCursor: string | null };

export type EmailSettings = S["EmailSettingsResponse"];
export type UpdateEmailSettings = S["UpdateEmailSettingsRequest"];
export type EmailTransport = S["EmailTransport"];
export type SmtpEncryption = S["SmtpEncryption"];
export type EmailTestResult = S["EmailTestResponse"];

export type Channel = S["ChannelResponse"];
export type ChannelKind = S["NotificationChannelKind"];
export type CreateChannel = S["CreateChannelRequest"];
export type UpdateChannel = S["UpdateChannelRequest"];
export type ChannelTestResult = S["ChannelTestResponse"];
export type Delivery = S["DeliveryResponse"];
export type NotificationEvent = S["NotificationEventInfo"];

export type Alert = S["AlertResponse"];
export type AlertSeverity = S["AlertSeverity"];
export type AlertState = S["AlertState"];
