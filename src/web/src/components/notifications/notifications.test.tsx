import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { defaultEvents, groupEvents, parseRecipients, sortOpenAlerts } from "@/lib/notifications/events";
import { EVENTS, fakeNotifications, makeAlert, makeChannel, makeEmail, NOW } from "@/test/notifications-fixtures";
import { MonitoringView } from "./monitoring-view";
import { NotificationsSettingsTab } from "./notifications-settings-tab";

let role = "owner";
vi.mock("@/lib/auth/auth-context", () => ({
  useAuth: () => ({ me: { user: { id: "u-1", email: "me@example.com", displayName: "Me" }, organization: { id: "o-1", name: "Acme", slug: "acme" }, role } }),
}));

const none = { items: [], nextCursor: null };
const resources = { projects: { list: vi.fn().mockResolvedValue({ items: [{ id: "p-1", name: "Shop", environments: [{ id: "e-1", name: "production" }] }], nextCursor: null }) } } as never;

function settingsApi(over: Record<string, Record<string, unknown>> = {}) {
  return fakeNotifications({
    email: { get: vi.fn().mockResolvedValue(makeEmail()), save: vi.fn().mockImplementation(async (b) => makeEmail(b)), test: vi.fn().mockResolvedValue({ ok: true, error: null, sentTo: "me@example.com", testedAt: NOW }), ...over.email },
    events: { list: vi.fn().mockResolvedValue({ items: EVENTS }) },
    channels: { list: vi.fn().mockResolvedValue({ items: [makeChannel()], nextCursor: null }), ...over.channels },
    deliveries: { list: vi.fn().mockResolvedValue(none), ...over.deliveries },
  });
}

describe("notification helpers", () => {
  it("groups the catalogue in API order and defaults to everything but the success events", () => {
    expect(groupEvents(EVENTS).map((g) => [g.name, g.events.length])).toEqual([["Deployments", 2], ["Backups", 2], ["Servers", 2]]);
    expect(defaultEvents(EVENTS)).toEqual(["deployment.failed", "backup.failed", "server.unreachable", "server.recovered"]);
  });

  it("parses recipients from lines and commas without duplicates", () => {
    expect(parseRecipients("a@x.com, b@x.com\nA@x.com;  c@x.com\n")).toEqual(["a@x.com", "b@x.com", "c@x.com"]);
    expect(parseRecipients("")).toEqual([]);
  });

  it("sorts open alerts by severity, then newest first", () => {
    const sorted = sortOpenAlerts([
      makeAlert({ id: "w-old", severity: "warning", openedAt: "2026-10-24T08:00:00.000Z" }),
      makeAlert({ id: "c-old", severity: "critical", openedAt: "2026-10-24T07:00:00.000Z" }),
      makeAlert({ id: "w-new", severity: "warning", openedAt: "2026-10-24T09:00:00.000Z" }),
      makeAlert({ id: "i", severity: "info" }),
      makeAlert({ id: "c-new", severity: "critical", openedAt: "2026-10-24T09:00:00.000Z" }),
    ]);
    expect(sorted.map((a) => a.id)).toEqual(["c-new", "c-old", "w-new", "w-old", "i"]);
  });
});

describe("Settings > Notifications: email", () => {
  it("shows the saved SMTP settings, never the password, and saves changes without touching the stored secret", async () => {
    const api = settingsApi();
    render(<NotificationsSettingsTab api={api} resources={resources} />);
    const form = await screen.findByRole("form", { name: "Email settings" });

    expect(within(form).getByLabelText("SMTP host")).toHaveValue("smtp.example.com");
    expect(within(form).getByLabelText("Password")).toHaveValue("");
    expect(within(form).getByText("A password is stored. Leave empty to keep it.")).toBeInTheDocument();

    const host = within(form).getByLabelText("SMTP host");
    await userEvent.clear(host);
    await userEvent.type(host, "mail.example.org");
    await userEvent.click(within(form).getByRole("button", { name: "Save changes" }));

    await waitFor(() => expect(api.email.save).toHaveBeenCalled());
    const body = (api.email.save as ReturnType<typeof vi.fn>).mock.calls[0][0];
    expect(body).toMatchObject({ enabled: true, transport: "smtp", smtpHost: "mail.example.org", smtpPort: 587, smtpEncryption: "startTls", fromAddress: "aethera@example.com" });
    expect(body).not.toHaveProperty("smtpPassword");
  });

  it("switches to Resend and sends the key only when one is typed", async () => {
    const api = settingsApi({ email: { get: vi.fn().mockResolvedValue(makeEmail({ configured: false, hasSmtpPassword: false, smtpHost: null })) } });
    render(<NotificationsSettingsTab api={api} resources={resources} />);
    const form = await screen.findByRole("form", { name: "Email settings" });

    await userEvent.click(within(form).getByRole("radio", { name: "Resend" }));
    expect(within(form).queryByLabelText("SMTP host")).not.toBeInTheDocument();
    await userEvent.type(within(form).getByLabelText("Resend API key"), "re_abc");
    await userEvent.click(within(form).getByRole("button", { name: "Save changes" }));

    await waitFor(() => expect(api.email.save).toHaveBeenCalled());
    expect((api.email.save as ReturnType<typeof vi.fn>).mock.calls[0][0]).toMatchObject({ transport: "resend", resendApiKey: "re_abc" });
  });

  it("removes a stored password with an empty string", async () => {
    const api = settingsApi();
    render(<NotificationsSettingsTab api={api} resources={resources} />);
    const form = await screen.findByRole("form", { name: "Email settings" });

    await userEvent.click(within(form).getByRole("button", { name: "Remove" }));
    await userEvent.click(within(form).getByRole("button", { name: "Save changes" }));

    await waitFor(() => expect(api.email.save).toHaveBeenCalled());
    expect((api.email.save as ReturnType<typeof vi.fn>).mock.calls[0][0]).toMatchObject({ smtpPassword: "" });
  });

  it("saves, sends the test email and shows the result inline", async () => {
    const api = settingsApi();
    render(<NotificationsSettingsTab api={api} resources={resources} />);
    const form = await screen.findByRole("form", { name: "Email settings" });

    await userEvent.click(within(form).getByRole("button", { name: "Send test email" }));

    expect(await within(form).findByText("Test email sent to me@example.com.")).toBeInTheDocument();
    expect(api.email.save).toHaveBeenCalledTimes(1);
    expect(api.email.test).toHaveBeenCalledTimes(1);
  });

  it("shows the error text of a failed test inline", async () => {
    const test = vi.fn().mockResolvedValue({ ok: false, error: "The SMTP server smtp.example.com:587 rejected the credentials: 535 Authentication failed", sentTo: "me@example.com", testedAt: NOW });
    render(<NotificationsSettingsTab api={settingsApi({ email: { test } })} resources={resources} />);
    const form = await screen.findByRole("form", { name: "Email settings" });

    await userEvent.click(within(form).getByRole("button", { name: "Send test email" }));

    expect(await within(form).findByRole("status")).toHaveTextContent("The test email failed: The SMTP server smtp.example.com:587 rejected the credentials: 535 Authentication failed");
  });
});

describe("Settings > Notifications: channels", () => {
  it("lists channels with their kind, recipients and event count", async () => {
    render(<NotificationsSettingsTab api={settingsApi()} resources={resources} />);
    const list = await screen.findByRole("region", { name: "Channels" });

    expect(await within(list).findByText("Ops")).toBeInTheDocument();
    expect(within(list).getByText(/administrators, ops@example.com · 1 event/)).toBeInTheDocument();
  });

  it("offers the events grouped, success events off by default, with a checkbox per group", async () => {
    render(<NotificationsSettingsTab api={settingsApi()} resources={resources} />);
    await userEvent.click(await screen.findByRole("button", { name: "Add channel" }));
    const dialog = await screen.findByRole("dialog");

    for (const group of ["Deployments", "Backups", "Servers"]) expect(within(dialog).getByRole("checkbox", { name: `All ${group}` })).toBeInTheDocument();
    expect(within(dialog).getByRole("checkbox", { name: /Deployment succeeded/ })).not.toBeChecked();
    expect(within(dialog).getByRole("checkbox", { name: /Deployment failed/ })).toBeChecked();
    expect(within(dialog).getByRole("checkbox", { name: /Backup succeeded/ })).not.toBeChecked();
    expect(within(dialog).getByRole("checkbox", { name: "All Deployments" })).toHaveAttribute("data-state", "indeterminate");
    expect(within(dialog).getByRole("checkbox", { name: "All Servers" })).toBeChecked();

    await userEvent.click(within(dialog).getByRole("checkbox", { name: "All Deployments" }));
    expect(within(dialog).getByRole("checkbox", { name: /Deployment succeeded/ })).toBeChecked();
    await userEvent.click(within(dialog).getByRole("checkbox", { name: "All Servers" }));
    expect(within(dialog).getByRole("checkbox", { name: /Server unreachable/ })).not.toBeChecked();
    expect(within(dialog).getByRole("checkbox", { name: /Server recovered/ })).not.toBeChecked();
  });

  it("creates an email channel with the chosen events and recipients", async () => {
    const create = vi.fn().mockResolvedValue(makeChannel({ id: "ch-2", name: "Team" }));
    const api = settingsApi({ channels: { create } });
    render(<NotificationsSettingsTab api={api} resources={resources} />);
    await userEvent.click(await screen.findByRole("button", { name: "Add channel" }));
    const dialog = await screen.findByRole("dialog");

    await userEvent.type(within(dialog).getByLabelText("Name"), "Team");
    await userEvent.type(within(dialog).getByLabelText("Extra recipients"), "a@example.com, b@example.com");
    await userEvent.click(within(dialog).getByRole("checkbox", { name: /Deployment succeeded/ }));
    await userEvent.click(within(dialog).getByRole("checkbox", { name: /Backup failed/ }));
    await userEvent.click(within(dialog).getByRole("button", { name: "Add channel" }));

    await waitFor(() => expect(create).toHaveBeenCalled());
    expect(create.mock.calls[0][0]).toEqual({
      name: "Team", kind: "email", enabled: true, sendToMembers: true, emailRecipients: ["a@example.com", "b@example.com"],
      events: ["deployment.succeeded", "deployment.failed", "server.unreachable", "server.recovered"], projectId: null, environmentId: null,
    });
  });

  it("creates a webhook channel and shows the signing secret once in a copyable box", async () => {
    const create = vi.fn().mockResolvedValue(makeChannel({ id: "ch-3", name: "Chat", kind: "webhook", webhookUrl: "https://hooks.example.com/x", hasWebhookSecret: true, webhookSecret: "whsec_topsecret" }));
    render(<NotificationsSettingsTab api={settingsApi({ channels: { create } })} resources={resources} />);
    await userEvent.click(await screen.findByRole("button", { name: "Add channel" }));
    const dialog = await screen.findByRole("dialog");

    await userEvent.type(within(dialog).getByLabelText("Name"), "Chat");
    await userEvent.click(within(dialog).getByRole("radio", { name: "webhook" }));
    await userEvent.type(within(dialog).getByLabelText("Webhook URL"), "https://hooks.example.com/x");
    await userEvent.click(within(dialog).getByRole("button", { name: "Add channel" }));

    await waitFor(() => expect(create).toHaveBeenCalled());
    expect(create.mock.calls[0][0]).toMatchObject({ kind: "webhook", webhookUrl: "https://hooks.example.com/x" });
    expect(create.mock.calls[0][0]).not.toHaveProperty("emailRecipients");
    const secret = await screen.findByLabelText("Signing secret");
    expect(secret).toHaveTextContent("whsec_topsecret");
    expect(screen.getByRole("button", { name: "Copy the signing secret" })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "I have copied it" }));
    await waitFor(() => expect(screen.queryByLabelText("Signing secret")).not.toBeInTheDocument());
  });

  it("sends a test to a channel and shows the outcome under it", async () => {
    const test = vi.fn().mockResolvedValue({ ok: false, error: "The channel has no recipients." });
    render(<NotificationsSettingsTab api={settingsApi({ channels: { test } })} resources={resources} />);

    await userEvent.click(await screen.findByRole("button", { name: "Send test to Ops" }));

    expect(await screen.findByText("The test failed: The channel has no recipients.")).toBeInTheDocument();
    expect(test).toHaveBeenCalledWith("ch-1");
  });

  it("shows recent deliveries with failures and their error text", async () => {
    const deliveries = { list: vi.fn().mockResolvedValue({ items: [
      { id: "d1", channelId: "ch-1", alertId: null, eventType: "deployment.failed", status: "failed", error: "Resend answered 422: domain not verified", createdAt: NOW },
      { id: "d2", channelId: "ch-1", alertId: null, eventType: "server.unreachable", status: "sent", error: null, createdAt: NOW },
    ], nextCursor: null }) };
    render(<NotificationsSettingsTab api={settingsApi({ deliveries })} resources={resources} />);

    const section = await screen.findByRole("region", { name: "Recent deliveries" });
    expect(await within(section).findByText("Resend answered 422: domain not verified")).toBeInTheDocument();
    expect(within(section).getByText("failed")).toBeInTheDocument();
    expect(within(section).getByText("sent")).toBeInTheDocument();
  });
});

describe("Monitoring", () => {
  const resolved = makeAlert({ id: "al-r", state: "resolved", title: "Disk on db-1 recovered", severity: "warning", resolvedAt: NOW });

  function monitoringApi(over: Record<string, unknown> = {}) {
    return fakeNotifications({
      alerts: {
        list: vi.fn().mockImplementation(async (q: { state?: string; cursor?: string }) =>
          q.state === "open"
            ? { items: [makeAlert({ id: "al-w", severity: "warning", title: "Disk almost full", openedAt: "2026-10-24T09:59:00.000Z" }), makeAlert()], nextCursor: null }
            : q.cursor ? { items: [makeAlert({ id: "al-r2", state: "resolved", title: "Older problem" })], nextCursor: null } : { items: [resolved], nextCursor: "next" }),
        acknowledge: vi.fn().mockResolvedValue(makeAlert({ acknowledgedAt: NOW })),
        ...over,
      },
    });
  }

  it("lists open alerts first with the most severe on top, then the resolved history", async () => {
    role = "owner";
    render(<MonitoringView api={monitoringApi()} pollMs={60_000} />);

    const open = await screen.findByRole("list", { name: "Open alerts list" });
    const titles = within(open).getAllByRole("listitem").map((li) => li.textContent ?? "");
    expect(titles[0]).toContain("Server web-1 is unreachable");
    expect(titles[1]).toContain("Disk almost full");
    expect(within(open).getByRole("link", { name: "Server web-1 is unreachable" })).toHaveAttribute("href", "/servers/srv-1");
    expect(titles[0]).toContain("critical");
    const history = await screen.findByRole("list", { name: "Resolved alerts list" });
    expect(within(history).getByText("Disk on db-1 recovered")).toBeInTheDocument();
  });

  it("acknowledges an alert and reloads", async () => {
    role = "developer";
    const api = monitoringApi();
    render(<MonitoringView api={api} pollMs={60_000} />);

    await userEvent.click(await screen.findByRole("button", { name: "Acknowledge Server web-1 is unreachable" }));

    await waitFor(() => expect(api.alerts.acknowledge).toHaveBeenCalledWith("al-1"));
  });

  it("does not offer acknowledging to a viewer", async () => {
    role = "viewer";
    render(<MonitoringView api={monitoringApi()} pollMs={60_000} />);
    await screen.findByRole("list", { name: "Open alerts list" });
    expect(screen.queryByRole("button", { name: /Acknowledge/ })).not.toBeInTheDocument();
  });

  it("loads more resolved alerts with the cursor", async () => {
    role = "owner";
    const api = monitoringApi();
    render(<MonitoringView api={api} pollMs={60_000} />);

    await userEvent.click(await screen.findByRole("button", { name: "Load more" }));

    expect(await screen.findByText("Older problem")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Load more" })).not.toBeInTheDocument();
  });

  it("is calm when nothing is open", async () => {
    role = "owner";
    render(<MonitoringView api={fakeNotifications({ alerts: { list: vi.fn().mockResolvedValue(none) } })} pollMs={60_000} />);
    expect(await screen.findByText("All quiet")).toBeInTheDocument();
    expect(await screen.findByText("No resolved alerts yet.")).toBeInTheDocument();
  });
});
