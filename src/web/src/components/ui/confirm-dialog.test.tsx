import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ConfirmDialog } from "./confirm-dialog";

function Harness({
  onConfirm,
  name = "acme-api",
}: {
  onConfirm: (n: string) => void | Promise<void>;
  name?: string;
}) {
  const [open, setOpen] = React.useState(true);
  return (
    <>
      <span data-testid="state">{open ? "open" : "closed"}</span>
      <button type="button" onClick={() => setOpen(true)}>
        reopen
      </button>
      <ConfirmDialog
        open={open}
        onOpenChange={setOpen}
        resourceName={name}
        title="Delete application"
        confirmLabel="Delete"
        onConfirm={onConfirm}
      />
    </>
  );
}

describe("ConfirmDialog typed-name gating", () => {
  it("keeps Delete disabled until the exact name is typed", async () => {
    const user = userEvent.setup();
    render(<Harness onConfirm={vi.fn()} />);

    const confirm = screen.getByRole("button", { name: "Delete" });
    const input = screen.getByLabelText(/type acme-api to confirm/i);
    expect(confirm).toBeDisabled();

    await user.type(input, "acme-ap");
    expect(confirm).toBeDisabled();

    await user.type(input, "i");
    expect(confirm).toBeEnabled();

    await user.type(input, "x");
    expect(confirm).toBeDisabled();
  });

  it("is case sensitive and does not trim", async () => {
    const user = userEvent.setup();
    render(<Harness onConfirm={vi.fn()} />);
    const input = screen.getByLabelText(/to confirm/i);
    const confirm = screen.getByRole("button", { name: "Delete" });

    await user.type(input, "ACME-API");
    expect(confirm).toBeDisabled();

    await user.clear(input);
    await user.type(input, " acme-api");
    expect(confirm).toBeDisabled();
  });

  it("does not call onConfirm when submitted with a wrong name (Enter)", async () => {
    const onConfirm = vi.fn();
    const user = userEvent.setup();
    render(<Harness onConfirm={onConfirm} />);
    await user.type(screen.getByLabelText(/to confirm/i), "nope{Enter}");
    expect(onConfirm).not.toHaveBeenCalled();
    expect(screen.getByTestId("state")).toHaveTextContent("open");
  });

  it("calls onConfirm with the typed name and closes on success", async () => {
    const onConfirm = vi.fn().mockResolvedValue(undefined);
    const user = userEvent.setup();
    render(<Harness onConfirm={onConfirm} />);

    await user.type(screen.getByLabelText(/to confirm/i), "acme-api");
    await user.click(screen.getByRole("button", { name: "Delete" }));

    await waitFor(() => expect(onConfirm).toHaveBeenCalledWith("acme-api"));
    await waitFor(() => expect(screen.getByTestId("state")).toHaveTextContent("closed"));
  });

  it("stays open and shows the error when onConfirm rejects", async () => {
    const onConfirm = vi.fn().mockRejectedValue(new Error("Server refused"));
    const user = userEvent.setup();
    render(<Harness onConfirm={onConfirm} />);

    await user.type(screen.getByLabelText(/to confirm/i), "acme-api");
    await user.click(screen.getByRole("button", { name: "Delete" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Server refused");
    expect(screen.getByTestId("state")).toHaveTextContent("open");
  });

  it("resets the typed text after cancel", async () => {
    const user = userEvent.setup();
    render(<Harness onConfirm={vi.fn()} />);
    await user.type(screen.getByLabelText(/to confirm/i), "acme-api");
    await user.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.getByTestId("state")).toHaveTextContent("closed"));

    await user.click(screen.getByRole("button", { name: "reopen" }));
    expect(await screen.findByLabelText(/to confirm/i)).toHaveValue("");
    expect(screen.getByRole("button", { name: "Delete" })).toBeDisabled();
  });
});
