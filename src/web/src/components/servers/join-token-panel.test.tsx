import * as React from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { makeToken } from "@/test/servers-fixtures";
import { JoinTokenPanel } from "./join-token-panel";

describe("JoinTokenPanel (token is shown once)", () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it("shows the token, the install command, endpoint and CA pin with an expiry countdown", () => {
    render(<JoinTokenPanel token={makeToken()} onDismiss={vi.fn()} />);
    expect(screen.getByLabelText("Join token")).toHaveTextContent("ajt_SECRET_TOKEN_VALUE");
    expect(screen.getByLabelText("Install command")).toHaveTextContent("--token ajt_SECRET_TOKEN_VALUE");
    expect(screen.getByText("https://aethera.example.com:8443")).toBeInTheDocument();
    expect(screen.getByText("AB:CD:EF:01")).toBeInTheDocument();
    expect(screen.getByRole("timer")).toHaveTextContent(/^(59:5\d|1:00:00)$/);
    expect(screen.getByText(/shown once/i)).toBeInTheDocument();
  });

  it("copies the command and the token", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    // user-event installs its own clipboard stub in setup(); replace it afterwards.
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    render(<JoinTokenPanel token={makeToken()} onDismiss={vi.fn()} />);
    await user.click(screen.getByRole("button", { name: "Copy install command" }));
    expect(writeText).toHaveBeenLastCalledWith(expect.stringContaining("--token ajt_SECRET_TOKEN_VALUE"));
    await user.click(screen.getByRole("button", { name: "Copy join token" }));
    expect(writeText).toHaveBeenLastCalledWith("ajt_SECRET_TOKEN_VALUE");
  });

  it("asks the owner to drop the token when the user hides it", async () => {
    const onDismiss = vi.fn();
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    render(<JoinTokenPanel token={makeToken()} onDismiss={onDismiss} />);
    await user.click(screen.getByRole("button", { name: /hide token/i }));
    expect(onDismiss).toHaveBeenCalledWith("hidden");
  });

  it("drops the token by itself when it expires", async () => {
    const onDismiss = vi.fn();
    render(<JoinTokenPanel token={makeToken({ expiresAt: new Date(Date.now() + 2000).toISOString() as never })} onDismiss={onDismiss} />);
    expect(onDismiss).not.toHaveBeenCalled();
    await act(async () => {
      vi.advanceTimersByTime(3500);
    });
    expect(onDismiss).toHaveBeenCalledWith("expired");
  });

  it("never writes the token to browser storage or the URL", async () => {
    const setItem = vi.spyOn(Storage.prototype, "setItem");
    const before = window.location.href;
    render(<JoinTokenPanel token={makeToken()} onDismiss={vi.fn()} />);
    await act(async () => {
      vi.advanceTimersByTime(2000);
    });
    expect(setItem).not.toHaveBeenCalled();
    expect(window.location.href).toBe(before);
    expect(JSON.stringify({ ...window.localStorage })).not.toContain("SECRET");
    expect(JSON.stringify({ ...window.sessionStorage })).not.toContain("SECRET");
  });
});
