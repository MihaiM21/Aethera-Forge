import * as React from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { PreviewsApi } from "@/lib/previews/api";
import type { Preview, PreviewSettings } from "@/lib/previews/types";
import type { ServersApi } from "@/lib/servers/api";
import { PreviewsTab } from "./previews-tab";

let role = "developer";
vi.mock("@/lib/auth/auth-context", () => ({
  useAuth: () => ({ me: { user: { id: "u-1", email: "me@example.com", displayName: "Me" }, organization: { id: "o-1", name: "Acme", slug: "acme" }, role } }),
}));
vi.mock("@/components/shell/detail-link", () => ({
  DetailLink: ({ href, children, ...rest }: { href: string; children: React.ReactNode }) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

const settings = (over: Partial<PreviewSettings> = {}): PreviewSettings => ({
  enabled: true,
  allowForks: false,
  serverId: null,
  maxActive: null,
  defaultMaxActive: 10,
  effectiveServerId: "srv-1",
  supported: true,
  unsupportedReason: null,
  webhookConfigured: true,
  webhookProvider: "gitHub",
  webhookPath: "/webhooks/git/abc",
  hostnamePattern: "pr-<number>-web-3fa2c1.apps.example.com",
  ...over,
});

const preview = (over: Partial<Preview> = {}): Preview => ({
  id: "p-1",
  applicationId: "app-1",
  number: 12,
  title: "Add the login page",
  authorLogin: "octocat",
  sourceBranch: "feature/login",
  headSha: "6dcb09b5b57875f334f61aebed695e2e4193db5e",
  fromFork: false,
  provider: "gitHub",
  serverId: "srv-1",
  hostname: "pr-12-web-3fa2c1.apps.example.com",
  url: "https://pr-12-web-3fa2c1.apps.example.com",
  status: "running",
  statusReason: null,
  statusChangedAt: new Date().toISOString(),
  lastEventAt: new Date().toISOString(),
  closedAt: null,
  removedAt: null,
  commentError: null,
  currentDeploymentId: "d-1",
  lastDeployment: { id: "d-1", number: 3, status: "running", failedStep: null, failureCode: null, commitSha: "6dcb09b", createdAt: new Date().toISOString(), finishedAt: null },
  createdAt: new Date().toISOString(),
  ...over,
});

function setup(opts: { settings?: PreviewSettings; previews?: Preview[]; calls?: Partial<Record<keyof PreviewsApi, unknown>> } = {}) {
  const calls = {
    list: vi.fn().mockResolvedValue({ items: opts.previews ?? [preview()], nextCursor: null }),
    settings: vi.fn().mockResolvedValue(opts.settings ?? settings()),
    putSettings: vi.fn().mockResolvedValue(settings()),
    redeploy: vi.fn().mockResolvedValue({ id: "d-2" }),
    remove: vi.fn().mockResolvedValue(preview({ status: "closing" })),
    ...opts.calls,
  };
  const servers = { list: vi.fn().mockResolvedValue({ items: [{ id: "srv-2", name: "build-box" }], nextCursor: null }) } as unknown as ServersApi;
  render(<PreviewsTab applicationId="app-1" api={calls as unknown as PreviewsApi} servers={servers} pollMs={60_000} />);
  return calls;
}

afterEach(() => {
  role = "developer";
  vi.restoreAllMocks();
});

describe("PreviewsTab", () => {
  it("lists the previews with number, title, branch, commit, status, URL and the logs link", async () => {
    setup();
    expect(await screen.findByText("#12")).toBeInTheDocument();
    expect(screen.getByText("Add the login page")).toBeInTheDocument();
    expect(screen.getByText(/feature\/login · 6dcb09b · octocat/)).toBeInTheDocument();
    expect(screen.getByText("Running")).toBeInTheDocument();
    const url = screen.getByRole("link", { name: /pr-12-web-3fa2c1\.apps\.example\.com/ });
    expect(url).toHaveAttribute("href", "https://pr-12-web-3fa2c1.apps.example.com");
    expect(screen.getByRole("link", { name: /#3 · logs/ })).toHaveAttribute("href", "/deployments/d-1");
  });

  it("says when there are no previews yet and warns when the webhook is missing", async () => {
    setup({ previews: [], settings: settings({ webhookConfigured: false, webhookPath: null }) });
    expect(await screen.findByText(/No previews yet/)).toBeInTheDocument();
    expect(screen.getByText(/application's webhook, and it is not set up/)).toBeInTheDocument();
  });

  it("marks fork previews and shows a comment error", async () => {
    setup({ previews: [preview({ fromFork: true, commentError: "GitHub answered 403 Forbidden" })] });
    expect(await screen.findByText("fork")).toBeInTheDocument();
    expect(screen.getByText(/comment: GitHub answered 403/)).toBeInTheDocument();
  });

  it("redeploys and removes (after a confirmation)", async () => {
    const calls = setup();
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: "Redeploy preview #12" }));
    expect(calls.redeploy).toHaveBeenCalledWith("app-1", 12);

    vi.spyOn(window, "confirm").mockReturnValue(true);
    await user.click(screen.getByRole("button", { name: "Remove preview #12" }));
    await waitFor(() => expect(calls.remove).toHaveBeenCalledWith("app-1", 12));
  });

  it("does not offer actions on a preview that is being removed, nor to viewers", async () => {
    setup({ previews: [preview({ status: "closing" })] });
    expect(await screen.findByText("Removing")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Redeploy preview/ })).not.toBeInTheDocument();
  });

  it("hides the actions from viewers", async () => {
    role = "viewer";
    setup();
    expect(await screen.findByText("#12")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Redeploy preview/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save changes" })).not.toBeInTheDocument();
  });

  it("warns about forks and lets only administrators turn them on", async () => {
    setup();
    expect(await screen.findByText(/Dangerous: anyone who can open a pull request/)).toBeInTheDocument();
    expect(screen.getByText(/Only an administrator can turn this on/)).toBeInTheDocument();
    expect(screen.getByRole("switch", { name: "Previews of pull requests from forks" })).toBeDisabled();
  });

  it("saves the settings, with the server and the limit", async () => {
    role = "admin";
    const calls = setup();
    const user = userEvent.setup();
    await user.click(await screen.findByRole("switch", { name: "Previews of pull requests from forks" }));
    await user.selectOptions(await screen.findByRole("combobox", { name: /Server/ }), "srv-2");
    await user.type(screen.getByLabelText(/Previews at once/), "3");
    await user.click(screen.getByRole("button", { name: "Save changes" }));
    await waitFor(() => expect(calls.putSettings).toHaveBeenCalledWith("app-1", { enabled: true, allowForks: true, serverId: "srv-2", maxActive: 3 }));
  });
});
