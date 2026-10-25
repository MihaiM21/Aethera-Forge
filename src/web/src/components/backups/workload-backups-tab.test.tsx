import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { databaseTarget, fakeBackups, makeDestination, makePolicy, makeRun, volumeTarget } from "@/lib/backups/test-fixtures";
import { WorkloadBackupsTab } from "./workload-backups-tab";

let role = "owner";
vi.mock("@/lib/auth/auth-context", () => ({
  useAuth: () => ({ me: { user: { id: "u-1", email: "me@example.com", displayName: "Me" }, organization: { id: "o-1", name: "Acme", slug: "acme" }, role } }),
}));

const page = <T,>(items: T[]) => ({ items, nextCursor: null });

function setup(over: { policies?: ReturnType<typeof makePolicy>[]; targets?: unknown[]; runs?: ReturnType<typeof makeRun>[]; destinations?: ReturnType<typeof makeDestination>[]; calls?: Record<string, unknown>; kind?: "applications" | "services" } = {}) {
  const calls = {
    forWorkload: vi.fn().mockResolvedValue({ policies: over.policies ?? [], targets: over.targets ?? [databaseTarget, volumeTarget] }),
    put: vi.fn().mockResolvedValue(makePolicy()),
    run: vi.fn().mockResolvedValue({ id: "job-1" }),
    remove: vi.fn().mockResolvedValue(undefined),
    ...over.calls,
  };
  const restore = vi.fn().mockResolvedValue({ id: "job-9" });
  const api = fakeBackups({
    destinations: { list: vi.fn().mockResolvedValue(page(over.destinations ?? [makeDestination()])) },
    policies: calls,
    runs: { list: vi.fn().mockResolvedValue(page(over.runs ?? [])), restore },
  });
  render(<WorkloadBackupsTab kind={over.kind ?? "services"} id="svc-1" name="db-main" api={api} pollMs={60_000} />);
  return { calls, restore };
}

describe("WorkloadBackupsTab policies", () => {
  it("offers the database and each volume, and says which are not backed up", async () => {
    setup();
    expect(await screen.findByText("Database (postgres)")).toBeInTheDocument();
    expect(screen.getByText("Volume db-1234-data")).toBeInTheDocument();
    expect(screen.getAllByText("not backed up")).toHaveLength(2);
    expect(screen.getAllByRole("button", { name: "Set up backups" })).toHaveLength(2);
  });

  it("explains why a template has no database backup and why a bind mount is skipped", async () => {
    setup({
      targets: [
        { ...databaseTarget, supported: false, engine: null, name: "grafana", reason: "Database backups are not available for the 'grafana' template. Back up its volumes instead." },
        { ...volumeTarget, name: "/srv/data", supported: false, reason: "Bind mounts are not backed up; only named volumes are." },
      ],
    });
    expect(await screen.findByText(/not available for the 'grafana' template/)).toBeInTheDocument();
    expect(screen.getByText(/Bind mounts are not backed up/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Set up backups" })).not.toBeInTheDocument();
  });

  it("sets up a database policy with a preset schedule and the default destination", async () => {
    const { calls } = setup();
    const [setUp] = await screen.findAllByRole("button", { name: "Set up backups" });
    await userEvent.click(setUp);
    const dialog = await screen.findByRole("dialog");

    expect(within(dialog).getByLabelText("Destination")).toHaveValue("dest-1");
    expect(within(dialog).getByLabelText("Schedule")).toHaveValue("daily");
    expect(within(dialog).getByLabelText("Time zone")).toHaveValue("UTC");
    expect(within(dialog).getByLabelText("Days")).toHaveValue(7);
    await userEvent.selectOptions(within(dialog).getByLabelText("Schedule"), "weekly");
    await userEvent.click(within(dialog).getByRole("button", { name: "Save policy" }));

    await waitFor(() => expect(calls.put).toHaveBeenCalledTimes(1));
    expect(calls.put).toHaveBeenCalledWith("services", "svc-1", {
      kind: "database", destinationId: "dest-1", enabled: true, schedule: "0 3 * * 0", timeZone: "UTC", keepDaily: 7, keepWeekly: 4, keepMonthly: 6,
    });
  });

  it("sets up a volume policy with its volume id and a custom cron expression", async () => {
    const { calls } = setup();
    const buttons = await screen.findAllByRole("button", { name: "Set up backups" });
    await userEvent.click(buttons[1]);
    const dialog = await screen.findByRole("dialog");
    await userEvent.selectOptions(within(dialog).getByLabelText("Schedule"), "custom");
    const cron = within(dialog).getByLabelText("Cron expression");
    await userEvent.clear(cron);
    await userEvent.type(cron, "30 2 * * 1-5");
    await userEvent.click(within(dialog).getByRole("button", { name: "Save policy" }));

    await waitFor(() => expect(calls.put).toHaveBeenCalled());
    expect(calls.put).toHaveBeenCalledWith("services", "svc-1", expect.objectContaining({ kind: "volume", volumeId: "vol-1", schedule: "30 2 * * 1-5" }));
  }, 20_000);

  it("shows the saved policy with its next runs in the editor and keeps the retention fields numeric", async () => {
    setup({ policies: [makePolicy()] });
    expect(await screen.findByText(/Daily at 03:00 \(UTC\) · to Turn One R2 · 7 daily · 4 weekly · 6 monthly/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Edit" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/Next runs of the saved schedule/)).toBeInTheDocument();

    const days = within(dialog).getByLabelText("Days");
    await userEvent.clear(days);
    await userEvent.type(days, "0");
    await userEvent.clear(within(dialog).getByLabelText("Weeks"));
    await userEvent.type(within(dialog).getByLabelText("Weeks"), "0");
    await userEvent.clear(within(dialog).getByLabelText("Months"));
    await userEvent.type(within(dialog).getByLabelText("Months"), "0");
    expect(within(dialog).getByText("Keep at least one backup.")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Save policy" })).toBeDisabled();
  }, 20_000);

  it("backs up now, and removes a policy after a confirmation", async () => {
    vi.spyOn(window, "confirm").mockReturnValue(true);
    const { calls } = setup({ policies: [makePolicy()] });
    await userEvent.click(await screen.findByRole("button", { name: /Back up now/ }));
    await waitFor(() => expect(calls.run).toHaveBeenCalledWith("pol-1"));

    await userEvent.click(screen.getByRole("button", { name: /Remove the policy of Database/ }));
    await waitFor(() => expect(calls.remove).toHaveBeenCalledWith("pol-1"));
  });

  it("asks for a destination first when there is none", async () => {
    setup({ destinations: [] });
    expect(await screen.findByText(/There is no backup destination yet/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Set up backups" })).not.toBeInTheDocument();
  });

  it("viewers see the policies but cannot change them", async () => {
    role = "viewer";
    try {
      setup({ policies: [makePolicy()] });
      expect(await screen.findByText("Database (postgres)")).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: /Back up now/ })).not.toBeInTheDocument();
      expect(screen.queryByRole("button", { name: "Edit" })).not.toBeInTheDocument();
    } finally {
      role = "owner";
    }
  });
});

describe("WorkloadBackupsTab runs and restore", () => {
  const runs = [
    makeRun(),
    makeRun({ id: "run-2", status: "failed", error: "pg_dump: connection refused", storedSizeBytes: null, restorable: false, keyId: null, durationMs: null, trigger: "manual" }),
    makeRun({ id: "run-3", trigger: "preRestore", prunedAt: "2026-10-23T11:00:00.000Z", restorable: false }),
  ];

  it("lists the runs with time, trigger, status, size, duration, key and error", async () => {
    setup({ runs });
    const table = await screen.findByRole("table");
    expect(within(table).getByText("scheduled")).toBeInTheDocument();
    expect(within(table).getByText("manual")).toBeInTheDocument();
    expect(within(table).getByText("before restore")).toBeInTheDocument();
    expect(within(table).getAllByText("succeeded")).toHaveLength(2);
    expect(within(table).getByText("failed")).toBeInTheDocument();
    expect(within(table).getAllByText("1.4 MiB")).toHaveLength(2);
    expect(within(table).getAllByText("4s")).toHaveLength(2);
    expect(within(table).getAllByText("mk1-0a1b2c3d")).toHaveLength(2);
    expect(within(table).getByText("pg_dump: connection refused")).toBeInTheDocument();
    expect(within(table).getByText(/removed by retention/)).toBeInTheDocument();
    expect(within(table).getAllByRole("button", { name: /Restore the backup/ })).toHaveLength(1); // only the restorable one
  });

  it("shows an empty history", async () => {
    setup({ runs: [] });
    expect(await screen.findByText("No backups yet.")).toBeInTheDocument();
  });

  it("restoring into the same service needs the exact name and warns that data is overwritten", async () => {
    const { restore } = setup({ runs: [makeRun()] });
    await userEvent.click(await screen.findByRole("button", { name: /Restore the backup/ }));
    const dialog = await screen.findByRole("dialog");

    expect(within(dialog).getByText(/overwrites the data of db-main/)).toBeInTheDocument();
    expect(within(dialog).getByText(/A safety backup is taken first/)).toBeInTheDocument();
    const confirm = within(dialog).getByRole("button", { name: "Overwrite and restore" });
    expect(confirm).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText(/Type db-main to confirm/), "db-mai");
    expect(confirm).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText(/Type db-main to confirm/), "n");
    expect(confirm).toBeEnabled();
    await userEvent.click(confirm);

    await waitFor(() => expect(restore).toHaveBeenCalledWith("run-1", { target: "same", confirm: "db-main" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  }, 20_000);

  it("restoring into a new service sends its name and no longer warns about overwriting", async () => {
    const { restore } = setup({ runs: [makeRun()] });
    await userEvent.click(await screen.findByRole("button", { name: /Restore the backup/ }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("radio", { name: /A new service/ }));

    expect(within(dialog).queryByText(/overwrites the data/)).not.toBeInTheDocument();
    const name = within(dialog).getByLabelText("Name of the new service");
    expect(name).toHaveValue("db-main-restore-test");
    await userEvent.type(within(dialog).getByLabelText(/Type db-main to confirm/), "db-main");
    await userEvent.click(within(dialog).getByRole("button", { name: "Restore into a new service" }));

    await waitFor(() => expect(restore).toHaveBeenCalledWith("run-1", { target: "new", confirm: "db-main", newServiceName: "db-main-restore-test" }));
  }, 20_000);

  it("a new service is not offered for a volume backup or for an application", async () => {
    setup({ runs: [makeRun({ kind: "volume", engine: "volume" })] });
    await userEvent.click(await screen.findByRole("button", { name: /Restore the backup/ }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("radio", { name: /A new service/ })).toBeDisabled();
    expect(within(dialog).getByText(/workload is stopped while the data is replaced/)).toBeInTheDocument();
  });

  it("only administrators can restore", async () => {
    role = "developer";
    try {
      setup({ runs: [makeRun()] });
      expect(await screen.findByRole("table")).toBeInTheDocument();
      expect(screen.queryByRole("button", { name: /Restore the backup/ })).not.toBeInTheDocument();
      expect(screen.getByText(/needs the administrator role/)).toBeInTheDocument();
    } finally {
      role = "owner";
    }
  });
});
