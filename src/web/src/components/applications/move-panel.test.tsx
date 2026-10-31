import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "@/lib/api/errors";
import { fakeApi, makeServer } from "@/test/servers-fixtures";
import { fakeResources } from "@/test/resources-fixtures";
import { MOVE_CONFIRMATION_REQUIRED, MovePanel } from "./move-panel";

const moved = {
  workloadId: "app-1",
  fromServerId: "srv-1",
  toServerId: "srv-2",
  deploymentId: "dep-2",
  jobId: "job-2",
  dataStaysBehind: false,
  volumes: [],
  warnings: ["new-box has the roles storage."],
  message: "Deploying on new-box. old-box keeps serving until the new deployment runs; then its containers are removed.",
};

function setup(move: ReturnType<typeof vi.fn>) {
  const api = fakeResources({ applications: { move } });
  const servers = fakeApi({
    list: vi.fn().mockResolvedValue({ items: [makeServer({ id: "srv-1", name: "old-box" }), makeServer({ id: "srv-2", name: "new-box" })], nextCursor: null }),
    capacity: vi.fn().mockResolvedValue([]),
    suggest: vi.fn().mockResolvedValue({
      kind: "application",
      suggestedServerId: "srv-2",
      message: null,
      candidates: [{ serverId: "srv-2", serverName: "new-box", eligible: true, score: 1, reasons: ["It has the worker role."], cpuFreeCores: null, memoryFreeBytes: null }],
    }),
  });
  const onMoved = vi.fn();
  render(<MovePanel kind="applications" id="app-1" name="Shop" currentServerId="srv-1" api={api} servers={servers} onMoved={onMoved} />);
  return { onMoved };
}

describe("MovePanel", () => {
  it("offers the other servers, explains the suggestion and moves", async () => {
    const move = vi.fn().mockResolvedValue(moved);
    const { onMoved } = setup(move);

    expect(await screen.findByRole("option", { name: /new-box.*\(suggested\)/ })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /old-box/ })).not.toBeInTheDocument(); // not the server it is on
    expect(await screen.findByText("It has the worker role.")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Move application" }));

    await waitFor(() => expect(move).toHaveBeenCalledWith("app-1", "srv-2", false));
    expect(await screen.findByText(/old-box keeps serving/)).toBeInTheDocument();
    expect(screen.getByText("new-box has the roles storage.")).toBeInTheDocument();
    expect(onMoved).toHaveBeenCalled();
  });

  it("shows the API's explanation when volumes stay behind and moves only after the name is typed", async () => {
    const move = vi
      .fn()
      .mockRejectedValueOnce(new ApiError({ status: 428, code: MOVE_CONFIRMATION_REQUIRED, detail: "This application has 1 volume (data). Volumes do not move: the data stays on old-box." }))
      .mockResolvedValueOnce({ ...moved, dataStaysBehind: true, volumes: ["data"] });
    const { onMoved } = setup(move);
    await screen.findByRole("option", { name: /new-box/ });

    await userEvent.click(screen.getByRole("button", { name: "Move application" }));

    expect(await screen.findByText(/Volumes do not move: the data stays on old-box/)).toBeInTheDocument();
    expect(onMoved).not.toHaveBeenCalled();
    const confirm = screen.getByRole("button", { name: "Move anyway" });
    expect(confirm).toBeDisabled();
    await userEvent.type(screen.getByLabelText(/Type/), "Shop");
    await userEvent.click(confirm);

    await waitFor(() => expect(move).toHaveBeenLastCalledWith("app-1", "srv-2", true));
    await waitFor(() => expect(onMoved).toHaveBeenCalled());
  });

  it("shows any other refusal as an error and does not report a move", async () => {
    const move = vi.fn().mockRejectedValue(new ApiError({ status: 409, code: "workload.deployment_in_progress", detail: "A deployment of this application is still running." }));
    const { onMoved } = setup(move);
    await screen.findByRole("option", { name: /new-box/ });

    await userEvent.click(screen.getByRole("button", { name: "Move application" }));

    expect(await screen.findByText("A deployment of this application is still running.")).toBeInTheDocument();
    expect(onMoved).not.toHaveBeenCalled();
  });
});
