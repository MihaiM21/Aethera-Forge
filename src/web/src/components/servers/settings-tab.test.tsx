import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fakeApi, makeJob, makeServer, makeToken, problem } from "@/test/servers-fixtures";
import { SettingsTab } from "./settings-tab";

const server = makeServer({ name: "edge-1" });

function setup(over: Parameters<typeof fakeApi>[0] = {}) {
  const api = fakeApi({
    listJoinTokens: vi.fn().mockResolvedValue([]),
    job: vi.fn().mockResolvedValue(makeJob({ status: "running" })),
    ...over,
  });
  const onServerChanged = vi.fn();
  const onDeleted = vi.fn();
  render(<SettingsTab server={server} api={api} onServerChanged={onServerChanged} onDeleted={onDeleted} />);
  return { api, onServerChanged, onDeleted, user: userEvent.setup() };
}

describe("SettingsTab destructive actions", () => {
  it("reset agent: the button stays disabled until the exact server name is typed, then calls the API with ?confirm", async () => {
    const resetAgent = vi.fn().mockResolvedValue(undefined);
    const { user, onServerChanged } = setup({ resetAgent });
    await user.click(screen.getByRole("button", { name: /reset agent…/i }));
    const dialog = await screen.findByRole("dialog");
    const confirm = within(dialog).getByRole("button", { name: "Reset agent" });
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByLabelText(/type edge-1 to confirm/i), "Edge-1");
    expect(confirm).toBeDisabled();
    await user.clear(within(dialog).getByLabelText(/type edge-1 to confirm/i));
    await user.type(within(dialog).getByLabelText(/type edge-1 to confirm/i), "edge-1");
    expect(confirm).toBeEnabled();
    await user.click(confirm);
    await waitFor(() => expect(resetAgent).toHaveBeenCalledWith(server.id, "edge-1"));
    expect(onServerChanged).toHaveBeenCalled();
  });

  it("delete server: typed name, redirect callback on success", async () => {
    const remove = vi.fn().mockResolvedValue(undefined);
    const { user, onDeleted } = setup({ remove });
    await user.click(screen.getByRole("button", { name: /delete server…/i }));
    const dialog = await screen.findByRole("dialog");
    await user.type(within(dialog).getByLabelText(/type edge-1 to confirm/i), "edge-1");
    await user.click(within(dialog).getByRole("button", { name: "Delete server" }));
    await waitFor(() => expect(remove).toHaveBeenCalledWith(server.id, "edge-1"));
    expect(onDeleted).toHaveBeenCalled();
  });

  it("shows the API's message when a destructive call is rejected", async () => {
    const resetAgent = vi.fn().mockRejectedValue(problem(428, "confirmation.required", "Confirmation required"));
    const { user } = setup({ resetAgent });
    await user.click(screen.getByRole("button", { name: /reset agent…/i }));
    const dialog = await screen.findByRole("dialog");
    await user.type(within(dialog).getByLabelText(/type edge-1 to confirm/i), "edge-1");
    await user.click(within(dialog).getByRole("button", { name: "Reset agent" }));
    expect(await within(dialog).findByRole("alert")).toHaveTextContent("Confirmation required");
  });

  it("prune volumes goes through the typed-name dialog and then follows the job", async () => {
    const prune = vi.fn().mockResolvedValue(makeJob({ id: "99999999-0000-4000-8000-000000000000" }));
    const { user, api } = setup({ prune });
    await user.click(screen.getByRole("button", { name: /prune docker resources…/i }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByLabelText(/unused volumes/i));
    await user.type(within(dialog).getByLabelText(/type edge-1 to confirm/i), "edge-1");
    await user.click(within(dialog).getByRole("button", { name: "Start prune" }));
    await waitFor(() => expect(prune).toHaveBeenCalledWith(server.id, expect.objectContaining({ volumes: true }), "edge-1"));
    expect(await screen.findByText("Prune Docker resources", { selector: "span" })).toBeInTheDocument();
    await waitFor(() => expect(api.job).toHaveBeenCalledWith("99999999-0000-4000-8000-000000000000", expect.anything()));
  });
});

describe("SettingsTab join tokens", () => {
  it("lists tokens without values and revokes only active ones", async () => {
    const revokeJoinToken = vi.fn().mockResolvedValue(undefined);
    const listJoinTokens = vi
      .fn()
      .mockResolvedValue([
        { id: "t1", state: "active", createdAt: "2026-10-04T09:00:00Z", expiresAt: "2026-10-04T10:00:00Z", usedAt: null, revokedAt: null, createdByUserId: null },
        { id: "t2", state: "used", createdAt: "2026-10-03T09:00:00Z", expiresAt: "2026-10-03T10:00:00Z", usedAt: "2026-10-03T09:05:00Z", revokedAt: null, createdByUserId: null },
      ]);
    const { user } = setup({ listJoinTokens, revokeJoinToken });
    expect(await screen.findByText("active")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: /revoke token/i })).toHaveLength(1);
    await user.click(screen.getByRole("button", { name: /revoke token/i }));
    await waitFor(() => expect(revokeJoinToken).toHaveBeenCalledWith(server.id, "t1"));
  });

  it("issues a new token, shows it once and drops it when hidden", async () => {
    const createJoinToken = vi.fn().mockResolvedValue(makeToken());
    const { user } = setup({ createJoinToken });
    await user.click(screen.getByRole("button", { name: /new join token/i }));
    expect(await screen.findByLabelText("Join token")).toHaveTextContent("ajt_SECRET_TOKEN_VALUE");
    await user.click(screen.getByRole("button", { name: /hide token/i }));
    expect(screen.queryByText(/ajt_SECRET_TOKEN_VALUE/)).not.toBeInTheDocument();
  });
});
