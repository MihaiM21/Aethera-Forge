import * as React from "react";
import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";

let authStatus = "authenticated";
vi.mock("@/lib/auth/auth-context", () => ({ useAuth: () => ({ status: authStatus }) }));

const serversList = vi.fn();
const jobsList = vi.fn();
vi.mock("@/lib/servers/api", () => ({ serversApi: { list: (...a: unknown[]) => serversList(...a) } }));
vi.mock("@/lib/resources/api", () => ({ resourcesApi: { jobs: { list: (...a: unknown[]) => jobsList(...a) } } }));

import { useConnectionStatus } from "./use-connection-status";

function Probe() {
  const s = useConnectionStatus();
  return <p data-testid="s">{JSON.stringify(s)}</p>;
}

describe("useConnectionStatus", () => {
  beforeEach(() => {
    authStatus = "authenticated";
    serversList.mockReset();
    jobsList.mockReset();
  });

  it("reports the real number of servers and running jobs once signed in", async () => {
    serversList.mockResolvedValue({ items: [{}, {}, {}], nextCursor: null });
    jobsList.mockResolvedValue({ items: [{}], nextCursor: null });
    render(<Probe />);
    await waitFor(() => expect(JSON.parse(screen.getByTestId("s").textContent!)).toEqual({ state: "connected", servers: 3, jobsRunning: 1 }));
    expect(jobsList).toHaveBeenCalledWith(expect.objectContaining({ status: "running" }), expect.anything());
  });

  it("never shows a made-up zero: a failing count stays unknown", async () => {
    serversList.mockRejectedValue(new Error("down"));
    jobsList.mockRejectedValue(new Error("down"));
    render(<Probe />);
    await waitFor(() => expect(serversList).toHaveBeenCalled());
    expect(JSON.parse(screen.getByTestId("s").textContent!)).toEqual({ state: "connected", servers: null, jobsRunning: null });
  });

  it("does not ask the API before the session exists", async () => {
    authStatus = "loading";
    render(<Probe />);
    expect(JSON.parse(screen.getByTestId("s").textContent!)).toEqual({ state: "connecting", servers: null, jobsRunning: null });
    expect(serversList).not.toHaveBeenCalled();
    expect(jobsList).not.toHaveBeenCalled();
  });
});
