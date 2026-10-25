import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "@/lib/api/errors";
import type { DnsApi } from "@/lib/dns/api";
import type { DnsProvider, DnsProviderVerify } from "@/lib/dns/types";
import { DnsProvidersSettingsTab } from "./dns-providers-settings-tab";

const NOW = "2026-10-24T10:00:00.000Z";

function provider(over: Partial<DnsProvider> = {}): DnsProvider {
  return { id: "p-1", name: "Cloudflare main", kind: "cloudflare", hasToken: true, zones: [], lastVerifiedAt: null, lastError: null, createdAt: NOW, updatedAt: NOW, ...over };
}

function verifyResult(over: Partial<DnsProviderVerify> = {}): DnsProviderVerify {
  return { valid: true, dnsAccess: true, zones: ["turnone.com", "example.org"], message: null, provider: provider({ zones: ["turnone.com", "example.org"], lastVerifiedAt: NOW }), ...over };
}

function fakeDns(items: DnsProvider[], over: Partial<Record<keyof DnsApi, unknown>> = {}): DnsApi {
  return {
    list: vi.fn().mockResolvedValue({ items, nextCursor: null }),
    create: vi.fn().mockResolvedValue(provider()),
    update: vi.fn().mockResolvedValue(provider()),
    remove: vi.fn().mockResolvedValue(undefined),
    verify: vi.fn().mockResolvedValue(verifyResult()),
    ...over,
  } as unknown as DnsApi;
}

describe("DnsProvidersSettingsTab", () => {
  it("explains how to create the token when there is no provider", async () => {
    render(<DnsProvidersSettingsTab api={fakeDns([])} />);
    expect(await screen.findByText("No DNS provider yet.")).toBeInTheDocument();
    expect(screen.getByText(/Zone → DNS → Edit/)).toBeInTheDocument();
    expect(screen.getAllByText(/wildcard certificates/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/certificates before the DNS switch/).length).toBeGreaterThan(0);
  });

  it("lists providers with status, zones and the stored error, never a token", async () => {
    const api = fakeDns([
      provider({ id: "p-1", name: "Main", zones: ["a.com", "b.com"], lastVerifiedAt: NOW }),
      provider({ id: "p-2", name: "Broken", lastError: "Cloudflare rejected the token (invalid or expired)." }),
      provider({ id: "p-3", name: "Fresh" }),
    ]);
    render(<DnsProvidersSettingsTab api={api} />);

    expect(await screen.findByText("Main")).toBeInTheDocument();
    expect(screen.getByText("verified")).toBeInTheDocument();
    expect(screen.getByText("needs attention")).toBeInTheDocument();
    expect(screen.getByText("not verified")).toBeInTheDocument();
    expect(screen.getByText("Cloudflare rejected the token (invalid or expired).")).toBeInTheDocument();
    expect(within(screen.getByRole("list", { name: "Zones of Main" })).getAllByRole("listitem").map((li) => li.textContent)).toEqual(["a.com", "b.com"]);
    expect(screen.queryByDisplayValue(/./)).toBeNull();
  });

  it("adds a provider with its token, verifies it and shows the zones", async () => {
    const user = userEvent.setup();
    const api = fakeDns([]);
    render(<DnsProvidersSettingsTab api={api} />);
    await user.click(await screen.findByRole("button", { name: /Add provider/ }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/Zone → DNS → Edit/)).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Add and verify" })).toBeDisabled();
    await user.type(within(dialog).getByLabelText("Name"), "Cloudflare main");
    await user.type(within(dialog).getByLabelText("API token"), "tok-123");
    await user.click(within(dialog).getByRole("button", { name: "Add and verify" }));

    await waitFor(() => expect(api.create).toHaveBeenCalledWith({ name: "Cloudflare main", apiToken: "tok-123" }));
    expect(api.verify).toHaveBeenCalledWith("p-1");
    expect(await screen.findByText("The token works and can edit DNS records.")).toBeInTheDocument();
    expect(screen.getByText("turnone.com")).toBeInTheDocument();
    expect(screen.getByText("example.org")).toBeInTheDocument();
    expect(api.list).toHaveBeenCalledTimes(2); // refreshed after saving

    await user.click(screen.getByRole("button", { name: "Done" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("explains a missing DNS permission from the verification", async () => {
    const user = userEvent.setup();
    const message = "The token cannot read DNS records. Create it with the permission Zone > DNS > Edit.";
    const api = fakeDns([], { verify: vi.fn().mockResolvedValue(verifyResult({ dnsAccess: false, message })) });
    render(<DnsProvidersSettingsTab api={api} />);
    await user.click(await screen.findByRole("button", { name: /Add provider/ }));
    const dialog = await screen.findByRole("dialog");
    await user.type(within(dialog).getByLabelText("Name"), "x");
    await user.type(within(dialog).getByLabelText("API token"), "t");
    await user.click(within(dialog).getByRole("button", { name: "Add and verify" }));
    expect(await screen.findByText(message)).toBeInTheDocument();
  });

  it("edits the name without sending a token or verifying", async () => {
    const user = userEvent.setup();
    const api = fakeDns([provider({ zones: ["a.com"], lastVerifiedAt: NOW })]);
    render(<DnsProvidersSettingsTab api={api} />);
    await user.click(await screen.findByRole("button", { name: "Edit Cloudflare main" }));
    const dialog = await screen.findByRole("dialog");
    const name = within(dialog).getByLabelText("Name");
    await user.clear(name);
    await user.type(name, "Renamed");
    expect(within(dialog).getByText(/Leave empty to keep the stored token/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(api.update).toHaveBeenCalledWith("p-1", { name: "Renamed" }));
    expect(api.verify).not.toHaveBeenCalled();
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  it("replaces the token and verifies the new one", async () => {
    const user = userEvent.setup();
    const api = fakeDns([provider()]);
    render(<DnsProvidersSettingsTab api={api} />);
    await user.click(await screen.findByRole("button", { name: "Edit Cloudflare main" }));
    const dialog = await screen.findByRole("dialog");
    await user.type(within(dialog).getByLabelText("API token"), "new-token");
    await user.click(within(dialog).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(api.update).toHaveBeenCalledWith("p-1", { name: "Cloudflare main", apiToken: "new-token" }));
    expect(api.verify).toHaveBeenCalledWith("p-1");
  });

  it("shows API field errors in the dialog", async () => {
    const user = userEvent.setup();
    const error = ApiError.fromProblem(409, { type: "x", title: "Conflict", status: 409, code: "dns_provider.already_exists", detail: "A DNS provider named 'Main' already exists." });
    const api = fakeDns([], { create: vi.fn().mockRejectedValue(error) });
    render(<DnsProvidersSettingsTab api={api} />);
    await user.click(await screen.findByRole("button", { name: /Add provider/ }));
    const dialog = await screen.findByRole("dialog");
    await user.type(within(dialog).getByLabelText("Name"), "Main");
    await user.type(within(dialog).getByLabelText("API token"), "t");
    await user.click(within(dialog).getByRole("button", { name: "Add and verify" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent(/already exists/);
    expect(api.verify).not.toHaveBeenCalled();
  });

  it("verifies a provider and lists the zones Cloudflare returned", async () => {
    const user = userEvent.setup();
    const api = fakeDns([provider()]);
    render(<DnsProvidersSettingsTab api={api} />);
    await user.click(await screen.findByRole("button", { name: "Verify Cloudflare main" }));
    await waitFor(() => expect(api.verify).toHaveBeenCalledWith("p-1"));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Zones the token sees (2)")).toBeInTheDocument();
    expect(within(dialog).getByText("turnone.com")).toBeInTheDocument();
  });

  it("deletes after the name is typed, sending it as the confirmation", async () => {
    const user = userEvent.setup();
    const api = fakeDns([provider()]);
    render(<DnsProvidersSettingsTab api={api} />);
    await user.click(await screen.findByRole("button", { name: "Delete Cloudflare main" }));
    const dialog = await screen.findByRole("dialog");
    const confirm = within(dialog).getByRole("button", { name: "Delete" });
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByRole("textbox"), "Cloudflare main");
    await user.click(confirm);
    await waitFor(() => expect(api.remove).toHaveBeenCalledWith("p-1", "Cloudflare main"));
  });

  it("keeps the delete dialog open and shows the conflict while a domain uses the provider", async () => {
    const user = userEvent.setup();
    const error = ApiError.fromProblem(409, { type: "x", title: "Conflict", status: 409, code: "dns_provider.in_use", detail: "2 domain(s) use this DNS provider. Remove it from them first." });
    const api = fakeDns([provider()], { remove: vi.fn().mockRejectedValue(error) });
    render(<DnsProvidersSettingsTab api={api} />);
    await user.click(await screen.findByRole("button", { name: "Delete Cloudflare main" }));
    const dialog = await screen.findByRole("dialog");
    await user.type(within(dialog).getByRole("textbox"), "Cloudflare main");
    await user.click(within(dialog).getByRole("button", { name: "Delete" }));
    expect(await within(dialog).findByText(/2 domain\(s\) use this DNS provider/)).toBeInTheDocument();
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });
});
