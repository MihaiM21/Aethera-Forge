import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { fakeApi, makeJob } from "@/test/servers-fixtures";
import { JobProgress } from "./job-progress";

describe("JobProgress", () => {
  it("follows a job until it succeeds, then stops polling and reports it", async () => {
    const job = vi
      .fn()
      .mockResolvedValueOnce(makeJob({ status: "queued", queuePosition: 2 }))
      .mockResolvedValueOnce(makeJob({ status: "running" }))
      .mockResolvedValue(makeJob({ status: "succeeded" }));
    const onFinished = vi.fn();
    render(<JobProgress jobId="abcdef12-0000" label="Prune Docker resources" api={fakeApi({ job })} intervalMs={10} onFinished={onFinished} />);
    expect(await screen.findByText(/position 2 in queue/i)).toBeInTheDocument();
    await waitFor(() => expect(onFinished).toHaveBeenCalledTimes(1));
    expect(onFinished.mock.calls[0][0].status).toBe("succeeded");
    expect(screen.getByText("Succeeded")).toBeInTheDocument();
    const calls = job.mock.calls.length;
    await new Promise((r) => setTimeout(r, 60));
    expect(job.mock.calls.length).toBe(calls);
  });

  it("shows the API's failure with the failed step", async () => {
    const job = vi.fn().mockResolvedValue(makeJob({ status: "failed", error: { code: "transport.command_failed", title: "Command failed", detail: "docker: permission denied", failedStep: "prune", retryable: false } }));
    render(<JobProgress jobId="abcdef12-0000" label="Prune" api={fakeApi({ job })} intervalMs={10} />);
    expect(await screen.findByRole("alert")).toHaveTextContent("Command failed: docker: permission denied (step prune)");
  });

  it("keeps trying after a transient polling error", async () => {
    const job = vi.fn().mockRejectedValueOnce(new Error("boom")).mockResolvedValue(makeJob({ status: "running" }));
    render(<JobProgress jobId="abcdef12-0000" label="Prune" api={fakeApi({ job })} intervalMs={10} />);
    await waitFor(() => expect(screen.getByText("Running")).toBeInTheDocument());
    expect(screen.queryByText(/could not refresh/i)).not.toBeInTheDocument();
  });
});
