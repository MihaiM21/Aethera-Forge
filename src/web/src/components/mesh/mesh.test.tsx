import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { connectionInfo } from "@/components/services/service-detail-view";
import type { MeshApi } from "@/lib/mesh/api";
import type { MeshSettings, ServerMesh } from "@/lib/mesh/types";
import { BindScopeSelect, PrivateNetworkNote } from "./bind-scope";
import { MeshSettingsTab } from "./mesh-settings-tab";
import { ServerNetworkTab } from "./server-network-tab";

let role = "owner";
vi.mock("@/lib/auth/auth-context", () => ({
  useAuth: () => ({ me: { user: { id: "u-1", email: "me@example.com", displayName: "Me" }, organization: { id: "o-1", name: "Acme", slug: "acme" }, role } }),
}));

const settings = (over: Partial<MeshSettings> = {}): MeshSettings => ({
  provider: "none",
  cidr: "10.210.0.0/16",
  listenPort: 51820,
  servers: [],
  ...over,
});

const serverMesh = (over: Partial<ServerMesh> = {}): ServerMesh => ({
  serverId: "s-1",
  instanceProvider: "wireGuard",
  provider: "wireGuard",
  status: "up",
  statusChangedAt: null,
  error: null,
  address: "10.210.0.1",
  publicKey: "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789AbCdEf=",
  endpoint: null,
  effectiveEndpoint: "203.0.113.1:51820",
  listenPort: null,
  effectiveListenPort: 51820,
  handshakeAt: null,
  agentConnected: true,
  peersLive: true,
  peers: [],
  ...over,
});

function fakeApi(over: Partial<MeshApi> = {}): MeshApi {
  return {
    settings: vi.fn().mockResolvedValue(settings()),
    saveSettings: vi.fn().mockResolvedValue(settings({ provider: "wireGuard" })),
    reconcile: vi.fn().mockResolvedValue({ queued: true }),
    server: vi.fn().mockResolvedValue(serverMesh()),
    saveServer: vi.fn().mockResolvedValue(serverMesh()),
    ...over,
  } as MeshApi;
}

describe("MeshSettingsTab", () => {
  it("starts off and turns WireGuard on with the default range and port", async () => {
    const api = fakeApi();
    render(<MeshSettingsTab api={api} pollMs={60_000} />);
    const select = await screen.findByLabelText("Provider");
    await userEvent.selectOptions(select, "wireGuard");
    expect(screen.getByLabelText("Range")).toHaveValue("10.210.0.0/16");
    expect(screen.getByLabelText("UDP port")).toHaveValue("51820");
    await userEvent.click(screen.getByRole("button", { name: "Save and apply" }));
    await waitFor(() => expect(api.saveSettings).toHaveBeenCalledWith({ provider: "wireGuard", cidr: "10.210.0.0/16", listenPort: 51820 }));
  });

  it("shows the API's message on the range field and the state of each server", async () => {
    const saveSettings = vi.fn().mockRejectedValue(Object.assign(new Error("invalid"), { name: "ApiError", status: 422, errors: [{ pointer: "/cidr", message: "The prefix must be between /16 and /24." }] }));
    const api = fakeApi({
      settings: vi.fn().mockResolvedValue(
        settings({
          provider: "wireGuard",
          servers: [
            { id: "s-1", name: "vps-1", provider: "wireGuard", status: "up", address: "10.210.0.1", error: null, handshakeAt: null, agentConnected: true },
            { id: "s-2", name: "vps-2", provider: "wireGuard", status: "failed", address: "10.210.0.2", error: "This server cannot run the private network", handshakeAt: null, agentConnected: true },
          ],
        }),
      ),
      saveSettings,
    });
    render(<MeshSettingsTab api={api} pollMs={60_000} />);
    expect(await screen.findByText("vps-1")).toBeInTheDocument();
    expect(screen.getByText("10.210.0.2", { exact: false })).toBeInTheDocument();
    expect(screen.getByText("This server cannot run the private network")).toBeInTheDocument();

    await userEvent.clear(screen.getByLabelText("Range"));
    await userEvent.type(screen.getByLabelText("Range"), "10.0.0.0/8");
    expect(screen.getByRole("status")).toHaveTextContent(/new address/i);
    await userEvent.click(screen.getByRole("button", { name: "Save and apply" }));
    await waitFor(() => expect(saveSettings).toHaveBeenCalled());
  });

  it("applies again on demand", async () => {
    const api = fakeApi({ settings: vi.fn().mockResolvedValue(settings({ provider: "tailscale" })) });
    render(<MeshSettingsTab api={api} pollMs={60_000} />);
    await userEvent.click(await screen.findByRole("button", { name: /apply again/i }));
    await waitFor(() => expect(api.reconcile).toHaveBeenCalled());
    expect(screen.getByText(/never logs in to Tailscale/i)).toBeInTheDocument();
  });
});

describe("ServerNetworkTab", () => {
  it("explains that the network is off", async () => {
    const api = fakeApi({ server: vi.fn().mockResolvedValue(serverMesh({ instanceProvider: "none", provider: "none", status: "disabled", address: null })) });
    render(<ServerNetworkTab serverId="s-1" api={api} pollMs={60_000} />);
    expect(await screen.findByText(/private network is off/i)).toBeInTheDocument();
  });

  it("shows the address, the key and the peers with their handshakes", async () => {
    const api = fakeApi({
      server: vi.fn().mockResolvedValue(
        serverMesh({
          peers: [
            { serverId: "s-2", name: "vps-2", publicKey: "PeerKeyPeerKeyPeerKey=", address: "10.210.0.2", endpoint: "203.0.113.2:51820", lastHandshake: new Date(Date.now() - 20_000).toISOString(), rxBytes: 2048, txBytes: 1024 },
            { serverId: "s-3", name: "vps-3", publicKey: "OtherKeyOtherKey=", address: "10.210.0.3", endpoint: null, lastHandshake: null, rxBytes: 0, txBytes: 0 },
          ],
        }),
      ),
    });
    render(<ServerNetworkTab serverId="s-1" api={api} pollMs={60_000} />);
    expect(await screen.findByText("10.210.0.1")).toBeInTheDocument();
    expect(screen.getByText("203.0.113.1:51820")).toBeInTheDocument();
    expect(screen.getByText("vps-2")).toBeInTheDocument();
    expect(screen.getByText(/^handshake .* ago/)).toBeInTheDocument();
    expect(screen.getByText(/^no handshake/)).toBeInTheDocument();
  });

  it("saves the endpoint and port overrides (administrators), clearing the empty ones", async () => {
    const api = fakeApi({ server: vi.fn().mockResolvedValue(serverMesh({ endpoint: "old.example.com:51820" })) });
    render(<ServerNetworkTab serverId="s-1" api={api} pollMs={60_000} />);
    const endpoint = await screen.findByLabelText("Endpoint");
    await userEvent.clear(endpoint);
    await userEvent.type(endpoint, "vpn.example.com:443");
    await userEvent.type(screen.getByLabelText("UDP port"), "443");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(api.saveServer).toHaveBeenCalledWith("s-1", { endpoint: "vpn.example.com:443", listenPort: 443 }));
  });

  it("hides the override form from developers and shows a failure as an alert", async () => {
    role = "developer";
    const api = fakeApi({ server: vi.fn().mockResolvedValue(serverMesh({ status: "failed", error: "needs the WireGuard kernel module" })) });
    render(<ServerNetworkTab serverId="s-1" api={api} pollMs={60_000} />);
    expect(await screen.findByRole("alert")).toHaveTextContent("needs the WireGuard kernel module");
    expect(screen.queryByLabelText("Endpoint")).not.toBeInTheDocument();
    role = "owner";
  });
});

describe("bind scope", () => {
  it("offers the three scopes", async () => {
    const onChange = vi.fn();
    render(<BindScopeSelect value="public" onChange={onChange} />);
    await userEvent.selectOptions(screen.getByLabelText("Listens on"), "private");
    expect(onChange).toHaveBeenCalledWith("private");
    expect(screen.getAllByRole("option").map((o) => o.textContent)).toEqual(["Public", "Private network", "This server only"]);
  });

  it("warns when the server has no private address and says where a port listens otherwise", async () => {
    const none = fakeApi({ server: vi.fn().mockResolvedValue(serverMesh({ address: null })) });
    const { unmount } = render(<PrivateNetworkNote serverId="s-1" anyPrivate api={none} />);
    expect(await screen.findByRole("status")).toHaveTextContent(/no address in the private network/i);
    unmount();

    render(<PrivateNetworkNote serverId="s-1" anyPrivate api={fakeApi()} />);
    expect(await screen.findByText("10.210.0.1")).toBeInTheDocument();
  });

  it("says nothing while no port is private", () => {
    const api = fakeApi();
    const { container } = render(<PrivateNetworkNote serverId="s-1" anyPrivate={false} api={api} />);
    expect(container).toBeEmptyDOMElement();
    expect(api.server).not.toHaveBeenCalled();
  });
});

describe("connection info of a service", () => {
  const service = {
    slug: "db",
    runtime: { ports: [{ containerPort: 5432, protocol: "tcp", publishedPort: 15432, isHttp: false, bindScope: "private" }, { containerPort: 6379, protocol: "tcp", publishedPort: 16379, isHttp: false, bindScope: "public" }] },
  } as never;

  it("adds the mesh address and published port of a private port", () => {
    expect(connectionInfo(service, "10.210.0.3")).toEqual([
      { label: "5432/tcp", value: "db:5432" },
      { label: "6379/tcp", value: "db:6379" },
      { label: "5432/tcp · private network", value: "10.210.0.3:15432" },
    ]);
  });

  it("adds nothing without a mesh address", () => {
    expect(connectionInfo(service, null)).toHaveLength(2);
  });
});
