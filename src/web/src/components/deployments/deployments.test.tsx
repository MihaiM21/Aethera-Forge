import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fakeResources, makeDeployment, makeListItem, makeStep } from "@/test/resources-fixtures";
import { DeploymentsView } from "./deployments-view";
import { pipelineSteps, DeploymentPipeline } from "./pipeline";
import { RollbackDialog } from "./rollback-dialog";

describe("pipeline", () => {
  const failed = makeDeployment({
    status: "failed",
    failedStep: "build",
    failureCode: "build.failed",
    failureReason: "exit status 1",
    steps: [makeStep("source", "succeeded"), makeStep("build", "failed", { errorCode: "build.failed", errorMessage: "npm ERR! missing script: build" })],
  });

  it("highlights the failed step with the API's code and message and leaves later steps pending", () => {
    const steps = pipelineSteps(failed);
    expect(steps).toHaveLength(9);
    expect(steps[0]).toMatchObject({ title: "Source", state: "success" });
    expect(steps[1]).toMatchObject({ title: "Build", state: "failed" });
    expect(steps[1].failure?.reason).toContain("build.failed");
    expect(steps[1].failure?.reason).toContain("missing script");
    expect(steps.slice(2).every((s) => s.state === "pending")).toBe(true);
  });

  it("renders the steps as a list with the failure details labelled", () => {
    render(<DeploymentPipeline deployment={failed} />);
    const list = screen.getByRole("list", { name: "Deployment pipeline" });
    expect(within(list).getAllByRole("listitem")).toHaveLength(9);
    expect(screen.getByRole("group", { name: "Build failure details" })).toHaveTextContent("missing script: build");
  });

  it("prints no duration for a skipped step but does for a finished one", () => {
    const d = makeDeployment({ steps: [makeStep("build", "skipped"), makeStep("image", "succeeded")] });
    const steps = pipelineSteps(d);
    expect(steps[1].meta).toBeUndefined();
    expect(steps[2].meta).toContain("aethera/web:d-1");
  });

  it("marks skipped steps and a running step", () => {
    const d = makeDeployment({ status: "inProgress", steps: [makeStep("source", "skipped"), makeStep("build", "skipped"), makeStep("image", "running")] });
    const steps = pipelineSteps(d);
    expect(steps.map((s) => s.state).slice(0, 4)).toEqual(["skipped", "skipped", "running", "pending"]);
  });
});

describe("DeploymentsView", () => {
  it("lists deployments with application, status, commit and failure place; filters by status through the API", async () => {
    const list = vi
      .fn()
      .mockResolvedValueOnce({
        items: [
          makeListItem({ id: "d-2", number: 2, status: "failed", failedStep: "healthCheck", failureCode: "health.timeout" }, "api"),
          makeListItem({ id: "d-1", number: 1 }, "web"),
        ],
        nextCursor: null,
      })
      .mockResolvedValue({ items: [], nextCursor: null });
    render(<DeploymentsView api={fakeResources({ deployments: { list } })} pollMs={60_000} />);

    const rows = await screen.findAllByRole("listitem");
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent("api");
    expect(rows[0]).toHaveTextContent("Failed");
    expect(rows[0]).toHaveTextContent("failed at Health check · health.timeout");
    expect(rows[1]).toHaveTextContent("Fix the thing");
    expect(rows[1]).toHaveTextContent("1m 23s");
    expect(within(rows[1]).getByRole("link", { name: "#1" })).toHaveAttribute("href", "/deployments/d-1");

    await userEvent.selectOptions(screen.getByLabelText("Status"), "failed");
    await waitFor(() => expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ status: "failed" }), expect.anything()));
    expect(await screen.findByText("No deployments with this status")).toBeInTheDocument();
  });

  it("offers more pages through the cursor", async () => {
    const list = vi
      .fn()
      .mockResolvedValueOnce({ items: [makeListItem({ id: "d-2", number: 2 })], nextCursor: "c1" })
      .mockResolvedValueOnce({ items: [makeListItem({ id: "d-1", number: 1 })], nextCursor: null });
    render(<DeploymentsView api={fakeResources({ deployments: { list } })} pollMs={60_000} />);
    await screen.findByText("#2");
    await userEvent.click(screen.getByRole("button", { name: "Load more" }));
    await screen.findByText("#1");
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ cursor: "c1" }));
    expect(screen.queryByRole("button", { name: "Load more" })).not.toBeInTheDocument();
  });

  it("shows an error panel when the API fails", async () => {
    const list = vi.fn().mockRejectedValue(new Error("boom"));
    render(<DeploymentsView api={fakeResources({ deployments: { list } })} pollMs={60_000} />);
    expect(await screen.findByRole("alert")).toHaveTextContent("boom");
  });
});

describe("RollbackDialog", () => {
  const items = [
    makeDeployment({ id: "d-3", number: 3, canRollbackTo: false, status: "running" }),
    makeDeployment({ id: "d-2", number: 2, canRollbackTo: true, status: "superseded", commitMessage: "Older but fine" }),
    makeDeployment({ id: "d-1", number: 1, canRollbackTo: true, status: "superseded", commitMessage: "Oldest" }),
  ];

  it("offers only rollback points other than the current one and starts the chosen rollback", async () => {
    const rollback = vi.fn().mockResolvedValue(makeDeployment({ id: "d-4", number: 4, trigger: "rollback" }));
    const onStarted = vi.fn();
    const api = fakeResources({ applications: { deployments: vi.fn().mockResolvedValue({ items, nextCursor: null }) }, deployments: { rollback } });
    render(<RollbackDialog applicationId="app-1" currentDeploymentId="d-3" open onOpenChange={() => {}} onStarted={onStarted} api={api} />);

    const radios = await screen.findAllByRole("radio");
    expect(radios).toHaveLength(2);
    expect(radios[0]).toBeChecked();
    await userEvent.click(radios[1]);
    await userEvent.click(screen.getByRole("button", { name: "Roll back" }));
    await waitFor(() => expect(rollback).toHaveBeenCalledWith("d-1"));
    expect(onStarted).toHaveBeenCalledWith(expect.objectContaining({ id: "d-4" }));
  });

  it("explains when nothing can be rolled back to and disables the action", async () => {
    const api = fakeResources({ applications: { deployments: vi.fn().mockResolvedValue({ items: [items[0]], nextCursor: null }) } });
    render(<RollbackDialog applicationId="app-1" currentDeploymentId="d-3" open onOpenChange={() => {}} onStarted={() => {}} api={api} />);
    expect(await screen.findByRole("status")).toHaveTextContent("No earlier deployment can be rolled back to");
    expect(screen.getByRole("button", { name: "Roll back" })).toBeDisabled();
  });

  it("shows the API's reason when the rollback is refused", async () => {
    const rollback = vi.fn().mockRejectedValue(new Error("The image was pruned."));
    const api = fakeResources({ applications: { deployments: vi.fn().mockResolvedValue({ items, nextCursor: null }) }, deployments: { rollback } });
    render(<RollbackDialog applicationId="app-1" currentDeploymentId="d-3" open onOpenChange={() => {}} onStarted={() => {}} api={api} />);
    await screen.findAllByRole("radio");
    await userEvent.click(screen.getByRole("button", { name: "Roll back" }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The image was pruned.");
  });
});
