import * as React from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { SshNotAvailableError, type SshInstallAdapter } from "@/lib/servers/ssh";
import { fakeApi, makeHealth, makeJob, makeServer, makeToken, axis, problem } from "@/test/servers-fixtures";
import { AddServerWizard } from "./add-server-wizard";

const created = makeServer({ id: "new-server-id", name: "edge-2", host: "198.51.100.7" });

async function fillDetails(user: ReturnType<typeof userEvent.setup>, name = "edge-2", host = "198.51.100.7") {
  await user.type(screen.getByLabelText("Name"), name);
  await user.type(screen.getByLabelText("Host"), host);
}

describe("AddServerWizard", () => {
  beforeEach(() => {
    vi.spyOn(Storage.prototype, "setItem");
  });
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("starts at details and validates required fields before moving on", async () => {
    const user = userEvent.setup();
    render(<AddServerWizard api={fakeApi()} />);
    expect(screen.getByRole("listitem", { current: "step" })).toHaveTextContent("Details");
    await user.click(screen.getByRole("button", { name: /continue/i }));
    expect(screen.getByText("Enter a name.")).toBeInTheDocument();
    expect(screen.getByText(/enter a dns name or ip address/i)).toBeInTheDocument();
    await fillDetails(user);
    await user.click(screen.getByRole("button", { name: /continue/i }));
    expect(screen.getByRole("listitem", { current: "step" })).toHaveTextContent("Connect");
  });

  it("join-command path: creates the server, then a token, shows it once, waits for the agent and finishes", async () => {
    const user = userEvent.setup();
    const create = vi.fn().mockResolvedValue(created);
    const createJoinToken = vi.fn().mockResolvedValue(makeToken());
    const status = vi
      .fn()
      .mockResolvedValueOnce(makeHealth({ agent: axis("agent", "notInstalled") }))
      .mockResolvedValue(makeHealth());
    const api = fakeApi({ create, createJoinToken, status });
    render(<AddServerWizard api={api} pollMs={30} />);

    await fillDetails(user);
    await user.click(screen.getByRole("checkbox", { name: /^master/ }));
    await user.click(screen.getByRole("button", { name: /continue/i }));
    // Nothing is created until the user asks for the command.
    expect(create).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: /generate join command/i }));

    expect(await screen.findByLabelText("Join token")).toHaveTextContent("ajt_SECRET_TOKEN_VALUE");
    expect(create).toHaveBeenCalledWith({ name: "edge-2", host: "198.51.100.7", roles: ["worker", "master"], transport: "agent" });
    expect(createJoinToken).toHaveBeenCalledWith("new-server-id");
    expect(screen.getByText("AB:CD:EF:01")).toBeInTheDocument();
    expect(screen.getByRole("timer")).toBeInTheDocument();

    // The agent connects: the token is dropped and the done step shows.
    expect(await screen.findByText("edge-2 is connected")).toBeInTheDocument();
    expect(screen.queryByText(/ajt_SECRET_TOKEN_VALUE/)).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: /open server/i })).toHaveAttribute("href", "/servers/new-server-id");
    // Never persisted.
    expect(Storage.prototype.setItem).not.toHaveBeenCalled();
    expect(window.location.search + window.location.hash).not.toContain("ajt_");
  });

  it("hiding the token removes it for good; a new one can be requested without creating a second server", async () => {
    const user = userEvent.setup();
    const create = vi.fn().mockResolvedValue(created);
    const createJoinToken = vi.fn().mockResolvedValueOnce(makeToken()).mockResolvedValueOnce(makeToken({ token: "ajt_SECOND", installCommand: "run ajt_SECOND" }));
    const api = fakeApi({ create, createJoinToken, status: vi.fn().mockResolvedValue(makeHealth({ agent: axis("agent", "notInstalled") })) });
    render(<AddServerWizard api={api} pollMs={1000} />);
    await fillDetails(user);
    await user.click(screen.getByRole("button", { name: /continue/i }));
    await user.click(screen.getByRole("button", { name: /generate join command/i }));
    await screen.findByLabelText("Join token");
    await user.click(screen.getByRole("button", { name: /hide token/i }));
    expect(screen.queryByText(/ajt_SECRET_TOKEN_VALUE/)).not.toBeInTheDocument();
    expect(screen.getByText("token hidden")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /generate a new token/i }));
    expect(await screen.findByLabelText("Join token")).toHaveTextContent("ajt_SECOND");
    expect(create).toHaveBeenCalledTimes(1);
  });

  it("sends the user back to the details step when the API rejects a field", async () => {
    const user = userEvent.setup();
    const create = vi.fn().mockRejectedValue(problem(422, "validation.failed", "Validation failed", [{ pointer: "/name", code: "duplicate", message: "A server with this name already exists." }]));
    render(<AddServerWizard api={fakeApi({ create })} />);
    await fillDetails(user);
    await user.click(screen.getByRole("button", { name: /continue/i }));
    await user.click(screen.getByRole("button", { name: /generate join command/i }));
    expect(await screen.findAllByText("A server with this name already exists.")).not.toHaveLength(0);
    expect(screen.getByRole("listitem", { current: "step" })).toHaveTextContent("Details");
  });

  it("shows the API error and lets the user retry when the token cannot be created", async () => {
    const user = userEvent.setup();
    const createJoinToken = vi.fn().mockRejectedValueOnce(problem(500, "server.error", "Internal error")).mockResolvedValue(makeToken());
    const api = fakeApi({ create: vi.fn().mockResolvedValue(created), createJoinToken, status: vi.fn().mockResolvedValue(makeHealth({ agent: axis("agent", "notInstalled") })) });
    render(<AddServerWizard api={api} pollMs={1000} />);
    await fillDetails(user);
    await user.click(screen.getByRole("button", { name: /continue/i }));
    await user.click(screen.getByRole("button", { name: /generate join command/i }));
    expect(await screen.findByRole("alert")).toHaveTextContent("Internal error");
    await user.click(screen.getByRole("button", { name: /generate join command/i }));
    expect(await screen.findByLabelText("Join token")).toBeInTheDocument();
  });

  describe("install via SSH (feature flagged adapter)", () => {
    it("is disabled and explained while the flag is off", async () => {
      const user = userEvent.setup();
      render(<AddServerWizard api={fakeApi()} sshEnabled={false} />);
      await fillDetails(user);
      await user.click(screen.getByRole("button", { name: /continue/i }));
      const ssh = screen.getByRole("radio", { name: /install via ssh/i });
      expect(ssh).toBeDisabled();
      expect(ssh).toHaveTextContent("Not available yet");
    });

    it("drives probe, fingerprint confirmation and install through the adapter", async () => {
      const user = userEvent.setup();
      const ssh: SshInstallAdapter = {
        probeHostKey: vi.fn().mockResolvedValue({ algorithm: "ssh-ed25519", fingerprintSha256: "SHA256:abc123" }),
        install: vi.fn().mockResolvedValue({ jobId: "job-ssh-1" }),
      };
      const create = vi.fn().mockResolvedValue({ ...created, transport: "ssh" });
      const api = fakeApi({
        create,
        job: vi.fn().mockResolvedValue(makeJob({ id: "job-ssh-1", status: "succeeded" })),
        status: vi.fn().mockResolvedValue(makeHealth()),
      });
      render(<AddServerWizard api={api} ssh={ssh} sshEnabled pollMs={30} />);
      await fillDetails(user);
      await user.click(screen.getByRole("button", { name: /continue/i }));
      await user.click(screen.getByRole("radio", { name: /install via ssh/i }));

      await user.click(screen.getByRole("button", { name: /check host key/i }));
      expect(await screen.findByLabelText("Host key fingerprint")).toHaveTextContent("SHA256:abc123");
      expect(create).toHaveBeenCalledWith(expect.objectContaining({ transport: "ssh", sshPort: 22, sshUser: "root" }));

      const install = screen.getByRole("button", { name: "Install agent" });
      expect(install).toBeDisabled();
      await user.click(screen.getByLabelText(/i verified this fingerprint/i));
      await user.click(install);

      await waitFor(() =>
        expect(ssh.install).toHaveBeenCalledWith({
          serverId: "new-server-id",
          host: "198.51.100.7",
          port: 22,
          user: "root",
          confirmedFingerprintSha256: "SHA256:abc123",
        }),
      );
      expect(await screen.findByText("edge-2 is connected", {}, { timeout: 4000 })).toBeInTheDocument();
    });

    it("reports an unwired adapter instead of faking success", async () => {
      const user = userEvent.setup();
      const ssh: SshInstallAdapter = {
        probeHostKey: vi.fn().mockRejectedValue(new SshNotAvailableError()),
        install: vi.fn(),
      };
      render(<AddServerWizard api={fakeApi({ create: vi.fn().mockResolvedValue(created) })} ssh={ssh} sshEnabled />);
      await fillDetails(user);
      await user.click(screen.getByRole("button", { name: /continue/i }));
      await user.click(screen.getByRole("radio", { name: /install via ssh/i }));
      await user.click(screen.getByRole("button", { name: /check host key/i }));
      expect(await screen.findByRole("alert")).toHaveTextContent(/not available in this build/i);
    });
  });
});
