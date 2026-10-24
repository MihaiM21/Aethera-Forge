import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fakeResources } from "@/test/resources-fixtures";
import type { ComposeInspect, Domain } from "@/lib/resources/types";
import { DomainsTab } from "./domains-tab";

const NO_ACME = "No ACME email configured: HTTPS routes use Traefik's default certificate (browsers warn). Set it with the installer's --acme-email option or Aethera:Proxy:AcmeEmail.";

function makeDomain(over: Partial<Domain> = {}): Domain {
  return {
    id: "dom-1", workloadId: "app-1", serverId: "srv-1", hostname: "app.example.com", pathPrefix: "/", httpsEnabled: true, targetPort: null, isPrimary: true,
    composeService: null, redirectTo: null, stripPathPrefix: false, basicAuthUsers: [], dnsProviderId: null,
    certificate: { status: "none", expiresAt: null, error: null }, dns: { status: "unknown", checkedAt: null, resolvedIps: [] }, warnings: [],
    createdAt: "2026-10-01T00:00:00Z", updatedAt: "2026-10-01T00:00:00Z", ...over,
  } as Domain;
}

const inspection: ComposeInspect = {
  services: [
    { name: "web", image: "", hasBuild: true, ports: [3000] },
    { name: "worker", image: "", hasBuild: true, ports: [] },
    { name: "db", image: "postgres:17", hasBuild: false, ports: [5432] },
  ],
  commitSha: "abc123",
  violations: [],
};

function setup(over: { domains?: Domain[]; compose?: boolean; inspect?: ReturnType<typeof vi.fn>; providers?: Array<{ id: string; name: string }> } = {}) {
  const create = vi.fn().mockResolvedValue(makeDomain());
  const update = vi.fn().mockResolvedValue(makeDomain());
  const composeInspect = over.inspect ?? vi.fn().mockResolvedValue(inspection);
  const dnsProviders = vi.fn().mockResolvedValue(over.providers ?? [{ id: "prov-1", name: "Cloudflare" }]);
  const api = fakeResources({
    domains: { list: vi.fn().mockResolvedValue({ items: over.domains ?? [], nextCursor: null }), create, update, dnsProviders, verifyDns: vi.fn(), remove: vi.fn() },
    applications: { composeInspect },
  });
  render(<DomainsTab kind="applications" id="app-1" compose={over.compose} api={api} />);
  return { create, update, composeInspect, dnsProviders };
}

const openAdd = async () => {
  await userEvent.click(await screen.findByRole("button", { name: /Add domain/ }));
  return screen.findByRole("dialog");
};

describe("DomainsTab", () => {
  it("shows the options of each domain and the ACME warning once", async () => {
    setup({
      compose: true,
      domains: [
        makeDomain({ id: "d1", hostname: "turnone.example.com", composeService: "web", basicAuthUsers: ["alice", "bob"], dnsProviderId: "prov-1", warnings: [NO_ACME] }),
        makeDomain({ id: "d2", hostname: "www.example.com", isPrimary: false, redirectTo: "turnone.example.com", warnings: [NO_ACME] }),
        makeDomain({ id: "d3", hostname: "api.example.com", isPrimary: false, pathPrefix: "/v1", stripPathPrefix: true, httpsEnabled: false }),
      ],
    });
    expect(await screen.findByText("service web")).toBeInTheDocument();
    expect(screen.getByText("basic auth · alice, bob")).toBeInTheDocument();
    expect(screen.getByText("DNS-01")).toBeInTheDocument();
    expect(screen.getByText("→ turnone.example.com")).toBeInTheDocument();
    expect(screen.getByText("strip prefix")).toBeInTheDocument();
    const warnings = screen.getAllByRole("alert");
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toHaveTextContent(/No ACME email configured/);
    expect(warnings[0]).toHaveTextContent(/--acme-email/);
    expect(warnings[0]).toHaveTextContent(/Aethera:Proxy:AcmeEmail/);
  });

  it("a compose application picks the service of the domain from the compose file", async () => {
    const m = setup({ compose: true });
    const dialog = await openAdd();
    await waitFor(() => expect(m.composeInspect).toHaveBeenCalledWith("app-1"));
    const picker = await within(dialog).findByLabelText("Compose service");
    expect(within(picker).getAllByRole("option").map((o) => o.textContent)).toEqual(["Select a service…", "web (port 3000)", "worker", "db (port 5432)"]);

    await userEvent.type(within(dialog).getByLabelText("Hostname"), "turnone.example.com");
    expect(within(dialog).getByRole("button", { name: "Add domain" })).toBeDisabled(); // a service is required
    await userEvent.selectOptions(picker, "web");
    expect(within(dialog).getByText("Empty = 3000 (from the compose file).")).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "Add domain" }));

    await waitFor(() => expect(m.create).toHaveBeenCalled());
    expect(m.create).toHaveBeenCalledWith("applications", "app-1", expect.objectContaining({ hostname: "turnone.example.com", composeService: "web", httpsEnabled: true }));
    expect(m.create.mock.calls[0][2]).not.toHaveProperty("basicAuth");
  });

  it("falls back to typing the service when the compose file cannot be listed", async () => {
    const m = setup({ compose: true, inspect: vi.fn().mockRejectedValue(new Error("The agent on this server is too old to read a compose file: update the agent.")) });
    const dialog = await openAdd();
    expect(await within(dialog).findByText(/The services could not be listed/)).toHaveTextContent(/update the agent/);
    await userEvent.type(within(dialog).getByLabelText("Hostname"), "a.example.com");
    await userEvent.type(within(dialog).getByLabelText("Compose service"), "web");
    await userEvent.click(within(dialog).getByRole("button", { name: "Add domain" }));
    await waitFor(() => expect(m.create).toHaveBeenCalledWith("applications", "app-1", expect.objectContaining({ composeService: "web" })));
  });

  it("does not ask for a service on other applications and never calls inspect", async () => {
    const m = setup();
    const dialog = await openAdd();
    expect(within(dialog).queryByLabelText("Compose service")).not.toBeInTheDocument();
    expect(m.composeInspect).not.toHaveBeenCalled();
  });

  it("a redirect replaces the serving options and is sent as the target host", async () => {
    const m = setup();
    const dialog = await openAdd();
    await userEvent.type(within(dialog).getByLabelText("Hostname"), "www.example.com");
    expect(within(dialog).getByLabelText("Target port")).toBeInTheDocument();
    await userEvent.type(within(dialog).getByLabelText("Redirect to"), "example.com");
    expect(within(dialog).queryByLabelText("Target port")).not.toBeInTheDocument();
    expect(within(dialog).queryByLabelText("Basic auth user name")).not.toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole("button", { name: "Add domain" }));
    await waitFor(() => expect(m.create).toHaveBeenCalled());
    expect(m.create.mock.calls[0][2]).toMatchObject({ hostname: "www.example.com", redirectTo: "example.com" });
    expect(m.create.mock.calls[0][2].targetPort).toBeUndefined();
  });

  it("refuses an invalid redirect target", async () => {
    setup();
    const dialog = await openAdd();
    await userEvent.type(within(dialog).getByLabelText("Hostname"), "www.example.com");
    await userEvent.type(within(dialog).getByLabelText("Redirect to"), "not a host");
    expect(within(dialog).getByText("That is not a valid hostname.")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Add domain" })).toBeDisabled();
  });

  it("strips the path prefix only when there is one", async () => {
    const m = setup();
    const dialog = await openAdd();
    await userEvent.type(within(dialog).getByLabelText("Hostname"), "api.example.com");
    const strip = within(dialog).getByRole("switch", { name: "Strip the path prefix" });
    expect(strip).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText("Path prefix"), "/api");
    expect(strip).toBeEnabled();
    await userEvent.click(strip);
    await userEvent.click(within(dialog).getByRole("button", { name: "Add domain" }));
    await waitFor(() => expect(m.create).toHaveBeenCalled());
    expect(m.create.mock.calls[0][2]).toMatchObject({ pathPrefix: "/api", stripPathPrefix: true });
  });

  it("lists the DNS providers in the certificate select and sends the chosen one", async () => {
    const m = setup();
    const dialog = await openAdd();
    await userEvent.type(within(dialog).getByLabelText("Hostname"), "a.example.com");
    const cert = within(dialog).getByLabelText("Certificate");
    expect(within(cert).getAllByRole("option").map((o) => o.textContent)).toEqual(["HTTP-01 (Let's Encrypt)", "DNS-01 via Cloudflare"]);
    await userEvent.selectOptions(cert, "prov-1");
    await userEvent.click(within(dialog).getByRole("button", { name: "Add domain" }));
    await waitFor(() => expect(m.create).toHaveBeenCalled());
    expect(m.create.mock.calls[0][2]).toMatchObject({ dnsProviderId: "prov-1" });
  });

  it("a wildcard host needs a DNS provider", async () => {
    const m = setup();
    const dialog = await openAdd();
    await userEvent.type(within(dialog).getByLabelText("Hostname"), "*.example.com");
    expect(within(dialog).getAllByText(/A wildcard host needs a DNS provider/).length).toBeGreaterThan(0);
    expect(within(dialog).getByRole("button", { name: "Add domain" })).toBeDisabled();
    await userEvent.selectOptions(within(dialog).getByLabelText("Certificate"), "prov-1");
    expect(within(dialog).getByRole("button", { name: "Add domain" })).toBeEnabled();
    await userEvent.click(within(dialog).getByRole("button", { name: "Add domain" }));
    await waitFor(() => expect(m.create).toHaveBeenCalledWith("applications", "app-1", expect.objectContaining({ hostname: "*.example.com", dnsProviderId: "prov-1" })));
  });

  it("works without any DNS provider (the endpoint may not exist yet)", async () => {
    setup({ providers: [] });
    const dialog = await openAdd();
    await userEvent.type(within(dialog).getByLabelText("Hostname"), "a.example.com");
    expect(within(within(dialog).getByLabelText("Certificate")).getAllByRole("option")).toHaveLength(1);
    expect(within(dialog).getByText(/Add a DNS provider under Settings/)).toBeInTheDocument();
  });

  it("edits basic auth users: needs valid names and passwords, sends them once", async () => {
    const m = setup();
    const dialog = await openAdd();
    await userEvent.type(within(dialog).getByLabelText("Hostname"), "admin.example.com");
    await userEvent.click(within(dialog).getByRole("button", { name: "Add user" }));
    await userEvent.type(within(dialog).getByLabelText("Basic auth user name"), "alice");
    await userEvent.type(within(dialog).getByLabelText("Basic auth password"), "short");
    expect(within(dialog).getByText(/passwords need at least 8 characters/)).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Add domain" })).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText("Basic auth password"), "-long-enough");
    await userEvent.click(within(dialog).getByRole("button", { name: "Add domain" }));
    await waitFor(() => expect(m.create).toHaveBeenCalled());
    expect(m.create.mock.calls[0][2]).toMatchObject({ basicAuth: [{ username: "alice", password: "short-long-enough" }] });
  });

  it("an existing domain keeps its basic auth unless it is replaced or removed, and its passwords are never shown", async () => {
    const domain = makeDomain({ basicAuthUsers: ["alice"] });
    const m = setup({ domains: [domain] });
    await userEvent.click(await screen.findByRole("button", { name: "Edit app.example.com" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("alice")).toBeInTheDocument();
    expect(within(dialog).queryByLabelText("Basic auth password")).not.toBeInTheDocument();

    await userEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(m.update).toHaveBeenCalledTimes(1));
    expect(m.update.mock.calls[0][1]).not.toHaveProperty("basicAuth");
    expect(m.update.mock.calls[0][0]).toBe("dom-1");

    await userEvent.click(await screen.findByRole("button", { name: "Edit app.example.com" }));
    const again = await screen.findByRole("dialog");
    await userEvent.click(within(again).getByRole("button", { name: "Remove basic auth" }));
    await userEvent.click(within(again).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(m.update).toHaveBeenCalledTimes(2));
    expect(m.update.mock.calls[1][1]).toMatchObject({ basicAuth: [] });
  });
});
