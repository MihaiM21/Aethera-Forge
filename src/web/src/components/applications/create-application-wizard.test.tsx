import * as React from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fakeApi, makeServer } from "@/test/servers-fixtures";
import { fakeResources, makeDeployment, makeProject } from "@/test/resources-fixtures";

const navigateTo = vi.fn();
vi.mock("@/lib/navigate", () => ({ navigateTo: (p: string) => navigateTo(p) }));

import { CreateApplicationWizard } from "./create-application-wizard";

function setup(over: { create?: ReturnType<typeof vi.fn>; deploy?: ReturnType<typeof vi.fn>; buildDetect?: ReturnType<typeof vi.fn> } = {}) {
  const create = over.create ?? vi.fn().mockResolvedValue({ id: "app-9" });
  const deploy = over.deploy ?? vi.fn().mockResolvedValue(makeDeployment({ id: "dep-9" }));
  const envCreate = vi.fn().mockResolvedValue({});
  const secretCreate = vi.fn().mockResolvedValue({ id: "sec-1" });
  const domainCreate = vi.fn().mockResolvedValue({});
  const buildDetect = over.buildDetect ?? vi.fn();
  const api = fakeResources({
    projects: { list: vi.fn().mockResolvedValue({ items: [makeProject()], nextCursor: null }) },
    gitCredentials: { list: vi.fn().mockResolvedValue({ items: [], nextCursor: null }) },
    registries: { list: vi.fn().mockResolvedValue({ items: [], nextCursor: null }) },
    applications: { create, deploy },
    envVars: { create: envCreate },
    secrets: { create: secretCreate },
    domains: { create: domainCreate },
    servers: { buildDetect },
  });
  const servers = fakeApi({ list: vi.fn().mockResolvedValue({ items: [makeServer({ id: "srv-1", name: "edge-1" })], nextCursor: null }) });
  render(<CreateApplicationWizard api={api} servers={servers} />);
  return { create, deploy, envCreate, secretCreate, domainCreate, buildDetect };
}

const next = () => userEvent.click(screen.getByRole("button", { name: "Continue" }));

describe("CreateApplicationWizard", () => {
  beforeEach(() => navigateTo.mockReset());

  it("walks repo -> build -> env -> server -> domain -> deploy and creates everything in order", async () => {
    const m = setup();
    await screen.findByDisplayValue("Staging");

    await userEvent.type(screen.getByLabelText("Repository URL"), "https://github.com/acme/shop.git");
    expect(screen.getByLabelText("Application name")).toHaveValue("shop"); // suggested from the URL
    await next();

    expect(screen.getByRole("button", { name: /Detect/ })).toBeInTheDocument();
    await next();

    await userEvent.click(screen.getByRole("button", { name: "Add variable" }));
    await userEvent.type(screen.getByLabelText("Variable name"), "DATABASE_URL");
    await userEvent.type(screen.getByLabelText("Variable value"), "postgres://x");
    await userEvent.click(screen.getByRole("checkbox", { name: /secret/i }));
    await next();

    await waitFor(() => expect(screen.getByLabelText("Server")).toHaveValue("srv-1"));
    const port = screen.getByLabelText("Container port");
    await userEvent.clear(port);
    await userEvent.type(port, "8080");
    await next();

    await userEvent.type(screen.getByLabelText("Domain"), "shop.example.com");
    await next();

    expect(screen.getByText("shop (shop)")).toBeInTheDocument();
    expect(screen.getByText("https://shop.example.com")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Create and deploy" }));

    await waitFor(() => expect(navigateTo).toHaveBeenCalledWith("/deployments/dep-9"));
    expect(m.create).toHaveBeenCalledWith(
      expect.objectContaining({
        name: "shop",
        slug: "shop",
        environmentId: "env-1",
        serverId: "srv-1",
        sourceKind: "dockerfile",
        gitSource: expect.objectContaining({ repositoryUrl: "https://github.com/acme/shop.git", provider: "gitHub" }),
        runtime: expect.objectContaining({ ports: [{ containerPort: 8080, isHttp: true }] }),
      }),
    );
    // A secret variable is stored as a secret of the application and linked, never sent as a plain value.
    expect(m.secretCreate).toHaveBeenCalledWith({ name: "shop_DATABASE_URL", value: "postgres://x", workloadId: "app-9" });
    expect(m.envCreate).toHaveBeenCalledWith("applications", "app-9", expect.objectContaining({ key: "DATABASE_URL", secretId: "sec-1" }));
    expect(m.envCreate.mock.calls[0][2]).not.toHaveProperty("value");
    expect(m.domainCreate).toHaveBeenCalledWith("applications", "app-9", expect.objectContaining({ hostname: "shop.example.com", httpsEnabled: true, isPrimary: true, targetPort: 8080 }));
    expect(m.deploy).toHaveBeenCalledWith("app-9");
  });

  it("does not leave the first step with missing or malformed input", async () => {
    setup();
    await screen.findByDisplayValue("Staging");
    await next();
    expect(screen.getByText("Enter the repository URL.")).toBeInTheDocument();
    expect(screen.getByText("Give the application a name.")).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText("Repository URL"), "file:///etc/passwd");
    await next();
    expect(screen.getByText(/Use an https/)).toBeInTheDocument();
  });

  it("asks for an image, not a repository, for a docker image application", async () => {
    const m = setup();
    await screen.findByDisplayValue("Staging");
    await userEvent.click(screen.getByRole("radio", { name: /Docker image/ }));
    expect(screen.queryByLabelText("Repository URL")).not.toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Image"), "nginx");
    await next();
    expect(screen.getByText(/needs no build/i)).toBeInTheDocument();
    await next();
    await next();
    await waitFor(() => expect(screen.getByLabelText("Server")).toHaveValue("srv-1"));
    await next();
    await next();
    await userEvent.click(screen.getByRole("button", { name: "Create and deploy" }));
    await waitFor(() => expect(m.create).toHaveBeenCalled());
    expect(m.create.mock.calls[0][0]).toMatchObject({ sourceKind: "dockerImage", image: { image: "nginx", tag: "latest" } });
    expect(m.create.mock.calls[0][0].gitSource).toBeUndefined();
  });

  it("applies the best detection candidate to the build step", async () => {
    const buildDetect = vi.fn().mockResolvedValue({
      commitSha: "abc",
      candidates: [{ engine: "static", confidence: 0.92, reason: "Vite project", dockerfilePath: null, installCommand: "pnpm install", buildCommand: "pnpm build", startCommand: null, outputDirectory: "dist", language: "node", suggestedPorts: [80] }],
    });
    setup({ buildDetect });
    await screen.findByDisplayValue("Staging");
    await userEvent.type(screen.getByLabelText("Repository URL"), "https://github.com/acme/site.git");
    await next();
    await waitFor(() => expect(screen.getByRole("button", { name: /Detect/ })).toBeEnabled());
    await userEvent.click(screen.getByRole("button", { name: /Detect/ }));
    expect(await screen.findByText("Vite project")).toBeInTheDocument();
    expect(buildDetect).toHaveBeenCalledWith("srv-1", expect.objectContaining({ repositoryUrl: "https://github.com/acme/site.git", branch: "main" }));
    expect(screen.getByLabelText("Output directory")).toHaveValue("dist");
    expect(screen.getByRole("radio", { name: "Static site" })).toBeChecked();
  });

  it("keeps the created application and offers a retry when a later call fails", async () => {
    const deploy = vi.fn().mockRejectedValueOnce(new Error("The server is unreachable.")).mockResolvedValueOnce(makeDeployment({ id: "dep-2" }));
    const m = setup({ deploy });
    await screen.findByDisplayValue("Staging");
    await userEvent.type(screen.getByLabelText("Repository URL"), "https://github.com/acme/shop.git");
    await next();
    await next();
    await next();
    await waitFor(() => expect(screen.getByLabelText("Server")).toHaveValue("srv-1"));
    await next();
    await next();
    await userEvent.click(screen.getByRole("button", { name: "Create and deploy" }));

    expect(await screen.findByText("The server is unreachable.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open it" })).toHaveAttribute("href", "/applications/app-9");
    await userEvent.click(screen.getByRole("button", { name: "Retry" }));
    await waitFor(() => expect(navigateTo).toHaveBeenCalledWith("/deployments/dep-2"));
    expect(m.create).toHaveBeenCalledTimes(1); // the retry does not create a second application
  });
});
