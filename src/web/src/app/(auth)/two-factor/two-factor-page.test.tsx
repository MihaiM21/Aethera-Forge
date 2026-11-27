import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import TwoFactorEnrolmentPage from "./page";

const replace = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace }) }));
vi.mock("@/components/shell/auth-screen", () => ({ AuthScreen: ({ children }: { children: React.ReactNode }) => <main>{children}</main> }));
vi.mock("@/components/account/two-factor", () => ({ TwoFactorEnrolmentFlow: () => <p>enrolment flow</p> }));

let required = true;
vi.mock("@/lib/auth/auth-context", () => ({
  useAuth: () => ({ logout: vi.fn() }),
  useMe: () => ({
    status: "authenticated",
    me: { user: { id: "u", email: "me@example.com", displayName: "Me" }, organization: { id: "o", name: "Acme", slug: "acme" }, role: "developer", twoFactorEnrolmentRequired: required },
    error: null,
    refresh: vi.fn(),
  }),
}));

describe("the enrolment screen", () => {
  it("keeps the flow (and the recovery codes in it) when a refresh already says enrolled", () => {
    required = true;
    const { rerender } = render(<TwoFactorEnrolmentPage />);
    expect(screen.getByText("enrolment flow")).toBeInTheDocument();
    expect(screen.getByText(/Acme requires two-factor authentication/)).toBeInTheDocument();

    required = false; // the periodic /auth/me after the confirm
    rerender(<TwoFactorEnrolmentPage />);
    expect(screen.getByText("enrolment flow")).toBeInTheDocument();
    expect(replace).not.toHaveBeenCalled();
  });

  it("sends a session that does not need to enrol to the dashboard", () => {
    required = false;
    render(<TwoFactorEnrolmentPage />);
    expect(replace).toHaveBeenCalledWith("/dashboard");
  });
});
