import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { makeServer } from "@/test/servers-fixtures";

const push = vi.fn();
const list = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ push, replace: vi.fn() }) }));
vi.mock("@/components/theme/theme-provider", () => ({ useTheme: () => ({ toggle: vi.fn(), resolved: "dark" }) }));
vi.mock("@/lib/auth/auth-context", () => ({ useAuth: () => ({ logout: vi.fn() }) }));
vi.mock("@/lib/servers/api", () => ({ serversApi: { list: (...a: unknown[]) => list(...a) } }));

import { CommandPalette } from "./command-palette";

describe("CommandPalette servers", () => {
  it("lists servers by name and host, and offers Add server", async () => {
    list.mockResolvedValue({ items: [makeServer({ id: "s1", name: "edge-1", host: "203.0.113.10" })], nextCursor: null });
    const user = userEvent.setup();
    render(<CommandPalette open onOpenChange={vi.fn()} onToggleSidebar={vi.fn()} />);
    const item = await screen.findByRole("option", { name: /edge-1/ });
    expect(item).toHaveTextContent("203.0.113.10");
    await user.type(screen.getByPlaceholderText(/search or run command/i), "add server");
    await user.click(await screen.findByRole("option", { name: /add server/i }));
    await waitFor(() => expect(push).toHaveBeenCalledWith("/servers/new"));
  });

  it("stays usable when the server list fails", async () => {
    list.mockRejectedValue(new Error("down"));
    render(<CommandPalette open onOpenChange={vi.fn()} onToggleSidebar={vi.fn()} />);
    expect(await screen.findByRole("option", { name: /dashboard/i })).toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /edge-1/ })).not.toBeInTheDocument();
  });
});
