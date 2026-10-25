import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fakeResources } from "@/test/resources-fixtures";
import type { TasksApi } from "@/lib/tasks/api";
import type { ScheduledTask, ScheduledTaskRun } from "@/lib/tasks/types";
import { ScheduledTasksTab } from "./scheduled-tasks-tab";

const NEXT = ["2026-10-25T03:00:00Z", "2026-10-26T03:00:00Z", "2026-10-27T03:00:00Z"];

function task(over: Partial<ScheduledTask> = {}): ScheduledTask {
  return {
    id: "t-1",
    workloadId: "app-1",
    workloadKind: "application",
    workloadName: "Shop",
    name: "Clear cache",
    command: "php artisan cache:clear",
    composeService: null,
    schedule: "0 3 * * *",
    timeZone: "UTC",
    timeoutSeconds: 3600,
    enabled: true,
    nextRunAt: NEXT[0],
    lastRunAt: null,
    nextRuns: NEXT,
    lastRun: null,
    createdAt: "2026-10-24T10:00:00Z",
    updatedAt: "2026-10-24T10:00:00Z",
    ...over,
  };
}

function run(over: Partial<ScheduledTaskRun> = {}): ScheduledTaskRun {
  return {
    id: "r-1",
    taskId: "t-1",
    workloadId: "app-1",
    jobId: "j-1",
    trigger: "manual",
    status: "succeeded",
    exitCode: 0,
    outputTail: "Cache cleared\n",
    outputTruncated: false,
    error: null,
    createdByUserId: null,
    createdAt: "2026-10-24T10:00:00Z",
    startedAt: "2026-10-24T10:00:01Z",
    finishedAt: "2026-10-24T10:00:03Z",
    durationMs: 2000,
    logStreamId: "task:r-1",
    ...over,
  };
}

function fakeTasks(over: Partial<Record<keyof TasksApi, unknown>> = {}): TasksApi {
  return {
    list: vi.fn().mockResolvedValue({ items: [], nextCursor: null }),
    create: vi.fn().mockResolvedValue(task()),
    update: vi.fn().mockResolvedValue(task()),
    remove: vi.fn().mockResolvedValue(undefined),
    run: vi.fn().mockResolvedValue({ run: run({ status: "pending" }), job: {} }),
    runs: vi.fn().mockResolvedValue({ items: [], nextCursor: null }),
    getRun: vi.fn().mockResolvedValue(run()),
    runLogs: vi.fn().mockResolvedValue({ items: [], nextSequence: 0, hasMore: false, ended: true }),
    preview: vi.fn().mockResolvedValue({ schedule: "0 3 * * *", timeZone: "UTC", nextRuns: NEXT }),
    composeServices: vi.fn().mockResolvedValue(null),
    ...over,
  } as unknown as TasksApi;
}

const dockerApp = fakeResources({ applications: { get: vi.fn().mockResolvedValue({ id: "app-1", sourceKind: "dockerImage" }) } });

function setup(api: TasksApi, resources = dockerApp, kind: "applications" | "services" = "applications") {
  render(<ScheduledTasksTab kind={kind} id="app-1" api={api} resources={resources} />);
  return userEvent.setup();
}

describe("ScheduledTasksTab", { timeout: 20_000 }, () => {
  it("lists tasks with command, schedule in words, time zone, next run, last run and the enabled switch", async () => {
    const api = fakeTasks({
      list: vi.fn().mockResolvedValue({
        items: [
          task({ lastRun: { id: "r-1", trigger: "scheduled", status: "failed", exitCode: 2, createdAt: new Date(Date.now() - 3 * 60_000).toISOString(), startedAt: null, finishedAt: null, durationMs: 4200, jobId: "j-1" } }),
          task({ id: "t-2", name: "Weekly report", command: "./report.sh", schedule: "*/15 * * * *", timeZone: "Europe/Bucharest", enabled: false, nextRunAt: null }),
        ],
        nextCursor: null,
      }),
    });
    setup(api);

    expect(await screen.findByText("Clear cache")).toBeInTheDocument();
    expect(screen.getByText("php artisan cache:clear")).toBeInTheDocument();
    expect(screen.getByText("Every day at 03:00")).toBeInTheDocument();
    expect(screen.getByText(/0 3 \* \* \* · UTC · timeout 1 h/)).toBeInTheDocument();
    expect(screen.getByText("Failed")).toBeInTheDocument();
    expect(screen.getByText("exit 2")).toBeInTheDocument();
    expect(screen.getByText(/3m ago/)).toBeInTheDocument();
    expect(screen.getByRole("switch", { name: "Disable Clear cache" })).toBeChecked();

    expect(screen.getByText("Every 15 minutes")).toBeInTheDocument();
    expect(screen.getByText("disabled")).toBeInTheDocument();
    expect(screen.getByText("never run")).toBeInTheDocument();
    expect(screen.getByRole("switch", { name: "Enable Weekly report" })).not.toBeChecked();
  });

  it("shows an empty state", async () => {
    setup(fakeTasks());
    expect(await screen.findByText(/No scheduled tasks/)).toBeInTheDocument();
  });

  it("creates a task from a preset and shows the next runs the API computed", async () => {
    const api = fakeTasks();
    const user = setup(api);
    await user.click(await screen.findByRole("button", { name: /new task/i }));

    const dialog = await screen.findByRole("dialog");
    await user.type(within(dialog).getByLabelText("Name"), "Nightly cleanup");
    await user.type(within(dialog).getByLabelText("Command"), "rm -rf /tmp/cache");
    await user.selectOptions(within(dialog).getByLabelText("Schedule"), "hourly");
    expect(within(dialog).getByLabelText("Cron expression")).toHaveValue("0 * * * *");
    await waitFor(() => expect(api.preview).toHaveBeenCalledWith("0 * * * *", expect.any(String), expect.anything()));
    await waitFor(() => expect(within(dialog).getAllByRole("listitem")).toHaveLength(3));

    await user.click(within(dialog).getByRole("button", { name: "Create task" }));
    await waitFor(() => expect(api.create).toHaveBeenCalled());
    expect(api.create).toHaveBeenCalledWith(
      "applications",
      "app-1",
      expect.objectContaining({ name: "Nightly cleanup", command: "rm -rf /tmp/cache", schedule: "0 * * * *", timeoutSeconds: 3600, enabled: true }),
    );
    expect((api.create as ReturnType<typeof vi.fn>).mock.calls[0][2]).not.toHaveProperty("composeService");
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("takes a custom cron expression and blocks saving while the API rejects it", async () => {
    const preview = vi.fn().mockImplementation(async (schedule: string) => {
      if (schedule === "nope") {
        const { ApiError } = await import("@/lib/api/errors");
        throw new ApiError({ status: 422, code: "validation.failed", title: "Validation failed", errors: [{ parameter: "schedule", code: "schedule.invalid", message: "Invalid cron expression." }] });
      }
      return { schedule, timeZone: "UTC", nextRuns: NEXT };
    });
    const api = fakeTasks({ preview });
    const user = setup(api);
    await user.click(await screen.findByRole("button", { name: /new task/i }));
    const dialog = await screen.findByRole("dialog");
    await user.type(within(dialog).getByLabelText("Name"), "x");
    await user.type(within(dialog).getByLabelText("Command"), "y");

    const cron = within(dialog).getByLabelText("Cron expression");
    await user.clear(cron);
    await user.type(cron, "nope");
    expect(await within(dialog).findByText("Invalid cron expression.")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Create task" })).toBeDisabled();
    expect(within(dialog).getByLabelText("Schedule")).toHaveValue("custom");

    await user.clear(cron);
    await user.type(cron, "5 4 * * 1");
    await waitFor(() => expect(within(dialog).getByRole("button", { name: "Create task" })).toBeEnabled());
  });

  it("asks for the compose service of a compose application: a picker when the services are known", async () => {
    const resources = fakeResources({ applications: { get: vi.fn().mockResolvedValue({ id: "app-1", sourceKind: "compose" }) } });
    const api = fakeTasks({ composeServices: vi.fn().mockResolvedValue(["web", "worker"]) });
    const user = setup(api, resources);
    const button = await screen.findByRole("button", { name: /new task/i });
    await waitFor(() => expect(button).toBeEnabled());
    await user.click(button);
    const dialog = await screen.findByRole("dialog");
    await user.selectOptions(within(dialog).getByLabelText("Service"), "worker");
    await user.type(within(dialog).getByLabelText("Name"), "reindex");
    await user.type(within(dialog).getByLabelText("Command"), "./reindex");
    await user.click(within(dialog).getByRole("button", { name: "Create task" }));
    await waitFor(() => expect(api.create).toHaveBeenCalled());
    expect((api.create as ReturnType<typeof vi.fn>).mock.calls[0][2]).toMatchObject({ composeService: "worker", name: "reindex" });
  });

  it("falls back to a text field for the service when the compose services cannot be listed", async () => {
    const resources = fakeResources({ applications: { get: vi.fn().mockResolvedValue({ id: "app-1", sourceKind: "compose" }) } });
    const api = fakeTasks();
    const user = setup(api, resources);
    const button = await screen.findByRole("button", { name: /new task/i });
    await waitFor(() => expect(button).toBeEnabled());
    await user.click(button);
    const dialog = await screen.findByRole("dialog");
    const input = within(dialog).getByLabelText("Service");
    expect(input.tagName).toBe("INPUT");
    await user.type(input, "web");
    await user.type(within(dialog).getByLabelText("Name"), "n");
    await user.type(within(dialog).getByLabelText("Command"), "c");
    await user.click(within(dialog).getByRole("button", { name: "Create task" }));
    await waitFor(() => expect(api.create).toHaveBeenCalled());
    expect((api.create as ReturnType<typeof vi.fn>).mock.calls[0][2]).toMatchObject({ composeService: "web" });
  });

  it("runs a task now and opens its history with the output", async () => {
    const api = fakeTasks({
      list: vi.fn().mockResolvedValue({ items: [task()], nextCursor: null }),
      runs: vi.fn().mockResolvedValue({ items: [run({ status: "pending" })], nextCursor: null }),
      getRun: vi.fn().mockResolvedValue(run()),
      runLogs: vi.fn().mockResolvedValue({ items: [{ sequence: 1, timestamp: "2026-10-24T10:00:02Z", stream: "stdout", source: "deploy", text: "Cache cleared\n" }], nextSequence: 2, hasMore: false, ended: true }),
    });
    const user = setup(api);
    await user.click(await screen.findByRole("button", { name: /run now/i }));

    await waitFor(() => expect(api.run).toHaveBeenCalledWith("t-1"));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("Runs of Clear cache")).toBeInTheDocument();
    expect(await within(dialog).findByText("Cache cleared")).toBeInTheDocument();
    expect(api.runLogs).toHaveBeenCalledWith("r-1", { fromSequence: 0, limit: 500 }, expect.anything());
    expect(within(dialog).getByText("exit 0", { selector: "dd, span" })).toBeDefined();
  });

  it("shows why a manual run was refused", async () => {
    const { ApiError } = await import("@/lib/api/errors");
    const api = fakeTasks({
      list: vi.fn().mockResolvedValue({ items: [task()], nextCursor: null }),
      run: vi.fn().mockRejectedValue(new ApiError({ status: 409, code: "scheduled_task.already_running", title: "Conflict", detail: "The previous run of this task is still running." })),
    });
    const user = setup(api);
    await user.click(await screen.findByRole("button", { name: /run now/i }));
    expect(await screen.findByRole("alert")).toHaveTextContent("The previous run of this task is still running.");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("toggles enabled through the API", async () => {
    const api = fakeTasks({ list: vi.fn().mockResolvedValue({ items: [task()], nextCursor: null }) });
    const user = setup(api);
    await user.click(await screen.findByRole("switch", { name: "Disable Clear cache" }));
    await waitFor(() => expect(api.update).toHaveBeenCalledWith("t-1", { enabled: false }));
  });

  it("edits a task and sends the changed definition", async () => {
    const api = fakeTasks({ list: vi.fn().mockResolvedValue({ items: [task()], nextCursor: null }) });
    const user = setup(api);
    await user.click(await screen.findByRole("button", { name: "Edit Clear cache" }));
    const dialog = await screen.findByRole("dialog");
    const command = within(dialog).getByLabelText("Command");
    await user.clear(command);
    await user.type(command, "echo hi");
    expect(within(dialog).getByLabelText("Schedule")).toHaveValue("daily");
    await user.click(within(dialog).getByRole("button", { name: "Save task" }));
    await waitFor(() => expect(api.update).toHaveBeenCalledWith("t-1", expect.objectContaining({ command: "echo hi", schedule: "0 3 * * *", timeZone: "UTC" })));
  });
});
