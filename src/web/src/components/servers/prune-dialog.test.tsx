import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { PruneDialog } from "./prune-dialog";

function setup(onSubmit = vi.fn().mockResolvedValue(undefined)) {
  const onOpenChange = vi.fn();
  render(<PruneDialog open onOpenChange={onOpenChange} serverName="edge-1" onSubmit={onSubmit} />);
  return { onSubmit, onOpenChange, user: userEvent.setup() };
}

describe("PruneDialog", () => {
  it("needs at least one scope", () => {
    setup();
    expect(screen.getByRole("button", { name: "Start prune" })).toBeDisabled();
  });

  it("starts a non-destructive prune without a typed name", async () => {
    const { onSubmit, user, onOpenChange } = setup();
    await user.click(screen.getByLabelText(/dangling images/i));
    await user.click(screen.getByLabelText(/build cache/i));
    expect(screen.queryByLabelText(/type edge-1 to confirm/i)).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Start prune" }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalled());
    const [body, confirm] = onSubmit.mock.calls[0];
    expect(body).toMatchObject({ danglingImages: true, buildCache: true, volumes: false, stoppedContainers: false });
    expect(confirm).toBeUndefined();
    await waitFor(() => expect(onOpenChange).toHaveBeenCalledWith(false));
  });

  it("requires typing the server name before volumes can be pruned, and sends it as confirm", async () => {
    const { onSubmit, user } = setup();
    await user.click(screen.getByLabelText(/unused volumes/i));
    const submit = screen.getByRole("button", { name: "Start prune" });
    expect(submit).toBeDisabled();
    const input = screen.getByLabelText(/type edge-1 to confirm/i);
    await user.type(input, "edge-");
    expect(submit).toBeDisabled();
    await user.type(input, "1");
    expect(submit).toBeEnabled();
    await user.click(submit);
    await waitFor(() => expect(onSubmit).toHaveBeenCalled());
    expect(onSubmit.mock.calls[0][0]).toMatchObject({ volumes: true });
    expect(onSubmit.mock.calls[0][1]).toBe("edge-1");
  });

  it("sends olderThanHours only when it is a positive integer", async () => {
    const { onSubmit, user } = setup();
    await user.click(screen.getByLabelText(/stopped containers/i));
    await user.type(screen.getByLabelText(/older than/i), "24");
    await user.click(screen.getByRole("button", { name: "Start prune" }));
    await waitFor(() => expect(onSubmit).toHaveBeenCalled());
    expect(onSubmit.mock.calls[0][0].olderThanHours).toBe(24);
  });

  it("keeps the dialog open and shows the API error", async () => {
    const onSubmit = vi.fn().mockRejectedValue(new Error("Another maintenance job is running."));
    const { user, onOpenChange } = setup(onSubmit);
    await user.click(screen.getByLabelText(/stopped containers/i));
    await user.click(screen.getByRole("button", { name: "Start prune" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Another maintenance job is running.");
    expect(onOpenChange).not.toHaveBeenCalledWith(false);
  });
});
