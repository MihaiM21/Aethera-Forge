import * as React from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "@/lib/api";
import LoginPage from "./page";

const replace = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace, push: vi.fn() }) }));

const login = vi.fn();
const loginTwoFactor = vi.fn();
vi.mock("@/lib/auth/auth-context", () => ({
  useAuth: () => ({ login, loginTwoFactor }),
  useMe: () => ({ status: "unauthenticated", me: null, error: null, refresh: vi.fn() }),
}));

// The frame (theme toggle, particles) is not what these tests are about.
vi.mock("@/components/shell/auth-screen", () => ({ AuthScreen: ({ children }: { children: React.ReactNode }) => <main>{children}</main> }));

vi.mock("@/lib/api", async (original) => ({
  ...(await original<typeof import("@/lib/api")>()),
  authApi: { getSetupStatus: vi.fn().mockResolvedValue({ setupRequired: false }) },
}));

const ME = { user: { id: "u", email: "me@example.com", displayName: "Me" }, organization: { id: "o", name: "Acme", slug: "acme" }, role: "owner" };
const problem = (status: number, code: string, extra: Record<string, unknown> = {}) => ApiError.fromProblem(status, { status, code, ...extra });

async function signIn(user: ReturnType<typeof userEvent.setup>) {
  await user.type(await screen.findByLabelText("Email"), "me@example.com");
  await user.type(screen.getByLabelText("Password"), "a long password");
  await user.click(screen.getByRole("button", { name: "Log in" }));
}

describe("login with two-factor authentication", () => {
  beforeEach(() => {
    window.sessionStorage.setItem("aethera-booted", "1"); // skip the boot animation
    replace.mockReset();
    login.mockReset();
    loginTwoFactor.mockReset();
  });

  it("asks for the code after the password and finishes with the challenge", async () => {
    const user = userEvent.setup();
    login.mockRejectedValue(problem(401, "auth.two_factor_required", { challenge: "ch-1", expiresIn: 300 }));
    loginTwoFactor.mockResolvedValue(ME);
    render(<LoginPage />);

    await signIn(user);
    await user.type(await screen.findByLabelText("Authentication code"), "123456");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    expect(loginTwoFactor).toHaveBeenCalledWith({ challenge: "ch-1", code: "123456" });
    expect(replace).toHaveBeenCalledWith("/dashboard");
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("accepts a recovery code instead", async () => {
    const user = userEvent.setup();
    login.mockRejectedValue(problem(401, "auth.two_factor_required", { challenge: "ch-2" }));
    loginTwoFactor.mockResolvedValue(ME);
    render(<LoginPage />);

    await signIn(user);
    await user.click(await screen.findByRole("button", { name: "Use a recovery code" }));
    await user.type(screen.getByLabelText("Recovery code"), "abcd-efgh-ijkl-mnop");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    expect(loginTwoFactor).toHaveBeenCalledWith({ challenge: "ch-2", recoveryCode: "abcd-efgh-ijkl-mnop" });
  });

  it("explains a wrong code and stays on the code step", async () => {
    const user = userEvent.setup();
    login.mockRejectedValue(problem(401, "auth.two_factor_required", { challenge: "ch-3" }));
    loginTwoFactor.mockRejectedValue(problem(401, "auth.two_factor_invalid_code"));
    render(<LoginPage />);

    await signIn(user);
    await user.type(await screen.findByLabelText("Authentication code"), "000000");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("That code is not correct");
    expect(screen.getByLabelText("Authentication code")).toHaveValue("");
  });

  it("goes back to the password when the challenge expired", async () => {
    const user = userEvent.setup();
    login.mockRejectedValue(problem(401, "auth.two_factor_required", { challenge: "ch-4" }));
    loginTwoFactor.mockRejectedValue(problem(401, "auth.two_factor_challenge_invalid"));
    render(<LoginPage />);

    await signIn(user);
    await user.type(await screen.findByLabelText("Authentication code"), "111111");
    await user.click(screen.getByRole("button", { name: "Verify" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("took too long");
    expect(screen.getByLabelText("Password")).toHaveValue("");
  });

  it("sends a member who must enrol to the enrolment screen", async () => {
    const user = userEvent.setup();
    login.mockResolvedValue({ ...ME, twoFactorEnrolmentRequired: true });
    render(<LoginPage />);

    await signIn(user);

    expect(replace).toHaveBeenCalledWith("/two-factor");
  });
});
