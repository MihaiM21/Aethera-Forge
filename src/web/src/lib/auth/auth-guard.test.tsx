import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { AuthGuard } from "./auth-guard";

const replace = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace }) }));

let me: Record<string, unknown> | null = null;
vi.mock("./auth-context", () => ({
  useMe: () => ({ status: "authenticated", me, error: null, refresh: vi.fn() }),
}));

const BASE = { user: { id: "u", email: "me@example.com", displayName: "Me" }, organization: { id: "o", name: "Acme", slug: "acme" }, role: "developer" };

describe("auth guard and two-factor enrolment", () => {
  it("renders the app for a normal session", () => {
    me = { ...BASE, twoFactorEnrolmentRequired: false };
    render(<AuthGuard><p>app</p></AuthGuard>);
    expect(screen.getByText("app")).toBeInTheDocument();
  });

  it("sends a session that must enrol to /two-factor and renders nothing of the app", () => {
    replace.mockReset();
    me = { ...BASE, twoFactorEnrolmentRequired: true };
    render(<AuthGuard><p>app</p></AuthGuard>);
    expect(screen.queryByText("app")).not.toBeInTheDocument();
    expect(replace).toHaveBeenCalledWith("/two-factor");
  });
});
