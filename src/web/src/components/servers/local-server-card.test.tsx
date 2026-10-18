import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { LocalServer } from "@/lib/resources/types";
import { LocalServerCard } from "./local-server-card";

const state = (over: Partial<LocalServer>): LocalServer =>
  ({ updaterAvailable: true, state: "idle", message: null, serverId: null, connected: false, ...over }) as LocalServer;

describe("LocalServerCard", () => {
  it("offers one button, then waits for the agent and reports when it connected", async () => {
    let polls = -1; // counts polls after the click; -1 = not clicked yet
    const localServer = vi.fn(async () => {
      if (polls < 0) return state({});
      return ++polls < 3 ? state({ state: "starting", message: "Starting the agent on this machine" }) : state({ state: "done", connected: true, serverId: "s1" });
    });
    const enableLocalServer = vi.fn(async () => {
      polls = 0;
      return state({ state: "requested", serverId: "s1" });
    });
    const onConnected = vi.fn();
    render(<LocalServerCard api={{ localServer, enableLocalServer }} pollMs={10} onConnected={onConnected} />);

    await userEvent.click(await screen.findByRole("button", { name: "Use this machine" }));
    expect(enableLocalServer).toHaveBeenCalledOnce();
    expect(await screen.findByRole("button", { name: /Connecting/ })).toBeDisabled();

    await waitFor(() => expect(onConnected).toHaveBeenCalledOnce());
    expect(screen.queryByRole("region")).toBeNull();
  });

  it("explains a missing updater and disables the button", async () => {
    render(<LocalServerCard api={{ localServer: async () => state({ updaterAvailable: false }), enableLocalServer: vi.fn() }} />);
    expect(await screen.findByText(/Needs the agent container/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Use this machine" })).toBeDisabled();
  });

  it("offers a retry after a failure, with the reason", async () => {
    render(
      <LocalServerCard
        api={{ localServer: async () => state({ state: "failed", message: "The agent container could not be started" }), enableLocalServer: vi.fn() }}
      />,
    );
    expect(await screen.findByText(/could not be started/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Try again" })).toBeEnabled();
  });

  it("stays out of the way when the state cannot be read", async () => {
    const localServer = vi.fn().mockRejectedValue(new Error("down"));
    const { container } = render(<LocalServerCard api={{ localServer, enableLocalServer: vi.fn() }} />);
    await waitFor(() => expect(localServer).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
  });
});
