import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError, type AuthApi, type TwoFactorStatus } from "@/lib/api";
import { groupSecret, TwoFactorEnrolmentFlow, TwoFactorPolicy, TwoFactorSection } from "./two-factor";

const ENROLMENT = {
  secret: "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP",
  otpauthUri: "otpauth://totp/Aethera:me%40example.com?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Aethera",
  issuer: "Aethera",
  accountName: "me@example.com",
  digits: 6,
  period: 30,
};
const CODES = "abcdefghij".split("").map((c) => `abcd-efgh-ijkl-mn${c}a`);

const status = (over: Partial<TwoFactorStatus> = {}): TwoFactorStatus => ({
  enabled: false,
  enabledAt: null,
  enrolmentPending: false,
  recoveryCodesRemaining: 0,
  requiredByOrganization: false,
  ...over,
});

function fakeAuth(over: Partial<AuthApi["twoFactor"]> = {}): AuthApi {
  return {
    twoFactor: {
      status: vi.fn().mockResolvedValue(status()),
      startEnrolment: vi.fn().mockResolvedValue(ENROLMENT),
      confirmEnrolment: vi.fn().mockResolvedValue({ recoveryCodes: CODES }),
      disable: vi.fn().mockResolvedValue(undefined),
      regenerateRecoveryCodes: vi.fn().mockResolvedValue({ recoveryCodes: CODES }),
      setPolicy: vi.fn().mockResolvedValue(status({ enabled: true, requiredByOrganization: true })),
      ...over,
    },
  } as unknown as AuthApi;
}

const validation = (pointer: string, message: string) =>
  ApiError.fromProblem(422, { status: 422, code: "validation.failed", errors: [{ pointer, code: "incorrect", message }] });

describe("two-factor enrolment", () => {
  it("groups the secret in fours for typing", () => {
    expect(groupSecret("JBSWY3DPEHPK3PXP")).toBe("JBSW Y3DP EHPK 3PXP");
  });

  it("goes from the password to the QR code to the recovery codes", async () => {
    const user = userEvent.setup();
    const auth = fakeAuth();
    const onDone = vi.fn();
    render(<TwoFactorEnrolmentFlow auth={auth} onDone={onDone} />);

    await user.type(screen.getByLabelText("Current password"), "my password");
    await user.click(screen.getByRole("button", { name: "Continue" }));
    expect(auth.twoFactor.startEnrolment).toHaveBeenCalledWith("my password");

    expect(await screen.findByAltText("QR code for your authenticator app")).toHaveAttribute("src", expect.stringMatching(/^data:image\/svg\+xml/));
    expect(screen.getByText("JBSW Y3DP EHPK 3PXP JBSW Y3DP EHPK 3PXP")).toBeInTheDocument();
    expect(screen.getByText("me@example.com")).toBeInTheDocument();

    await user.type(screen.getByLabelText("Code from the app"), "123 456");
    await user.click(screen.getByRole("button", { name: "Turn on two-factor authentication" }));
    expect(auth.twoFactor.confirmEnrolment).toHaveBeenCalledWith("123 456");

    const list = await screen.findByRole("list", { name: "Recovery codes" });
    expect(list.querySelectorAll("li")).toHaveLength(10);
    expect(screen.getByText(CODES[0])).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "I saved my recovery codes" }));
    expect(onDone).toHaveBeenCalled();
  });

  it("shows a wrong password and a wrong code next to the field", async () => {
    const user = userEvent.setup();
    const start = vi.fn().mockRejectedValueOnce(validation("/password", "The password is not correct.")).mockResolvedValue(ENROLMENT);
    const confirm = vi.fn().mockRejectedValue(validation("/code", "The code is not correct."));
    render(<TwoFactorEnrolmentFlow auth={fakeAuth({ startEnrolment: start, confirmEnrolment: confirm })} onDone={vi.fn()} />);

    await user.type(screen.getByLabelText("Current password"), "wrong");
    await user.click(screen.getByRole("button", { name: "Continue" }));
    expect(await screen.findByText("The password is not correct.")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Continue" }));
    await user.type(await screen.findByLabelText("Code from the app"), "000000");
    await user.click(screen.getByRole("button", { name: "Turn on two-factor authentication" }));
    expect(await screen.findByText("The code is not correct.")).toBeInTheDocument();
    expect(screen.getByLabelText("Code from the app")).toHaveValue("");
  });
});

describe("two-factor settings", () => {
  it("offers setup while off", async () => {
    render(<TwoFactorSection auth={fakeAuth()} />);
    expect(await screen.findByRole("button", { name: /Set up two-factor authentication/ })).toBeInTheDocument();
    expect(screen.getByText("off")).toBeInTheDocument();
  });

  it("turns it off with the password and a recovery code", async () => {
    const user = userEvent.setup();
    const auth = fakeAuth({ status: vi.fn().mockResolvedValue(status({ enabled: true, enabledAt: "2026-11-01T10:00:00Z", recoveryCodesRemaining: 2 })) });
    const onChanged = vi.fn();
    render(<TwoFactorSection auth={auth} onChanged={onChanged} />);

    expect(await screen.findByText("2 of 10 left")).toBeInTheDocument();
    expect(screen.getByRole("note")).toHaveTextContent("Only 2 recovery codes left");
    await user.click(screen.getByRole("button", { name: /Turn off/ }));
    await user.type(screen.getByLabelText("Current password"), "pw");
    await user.click(screen.getByRole("button", { name: "Use a recovery code" }));
    await user.type(screen.getByLabelText("Recovery code"), "aaaa-bbbb-cccc-dddd");
    await user.click(screen.getByRole("button", { name: "Turn off" }));

    await waitFor(() => expect(auth.twoFactor.disable).toHaveBeenCalledWith({ password: "pw", recoveryCode: "aaaa-bbbb-cccc-dddd" }));
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });

  it("shows new recovery codes once they are created", async () => {
    const user = userEvent.setup();
    const auth = fakeAuth({ status: vi.fn().mockResolvedValue(status({ enabled: true, recoveryCodesRemaining: 10 })) });
    render(<TwoFactorSection auth={auth} />);

    await user.click(await screen.findByRole("button", { name: /New recovery codes/ }));
    await user.type(screen.getByLabelText("Current password"), "pw");
    await user.type(screen.getByLabelText("Code from the app"), "654321");
    await user.click(screen.getByRole("button", { name: "Create new codes" }));

    expect(auth.twoFactor.regenerateRecoveryCodes).toHaveBeenCalledWith("pw", "654321");
    expect(await screen.findByText(CODES[9])).toBeInTheDocument();
  });
});

describe("two-factor policy", () => {
  it("cannot be turned on before the owner has two-factor authentication", async () => {
    render(<TwoFactorPolicy auth={fakeAuth()} canChange />);
    expect(await screen.findByText(/Turn it on for your own account first/)).toBeInTheDocument();
    expect(screen.getByRole("switch")).toBeDisabled();
  });

  it("lets an owner with two-factor authentication require it", async () => {
    const user = userEvent.setup();
    const auth = fakeAuth({ status: vi.fn().mockResolvedValue(status({ enabled: true })) });
    render(<TwoFactorPolicy auth={auth} canChange />);

    const toggle = await screen.findByRole("switch");
    await waitFor(() => expect(toggle).toBeEnabled());
    await user.click(toggle);
    expect(auth.twoFactor.setPolicy).toHaveBeenCalledWith(true);
  });

  it("is read-only for administrators", async () => {
    render(<TwoFactorPolicy auth={fakeAuth({ status: vi.fn().mockResolvedValue(status({ enabled: true })) })} canChange={false} />);
    expect(await screen.findByText("Only an owner can change this.")).toBeInTheDocument();
    expect(screen.getByRole("switch")).toBeDisabled();
  });
});
