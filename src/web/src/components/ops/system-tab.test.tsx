import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fakeResources, NOW } from "@/test/resources-fixtures";
import { describeReport, isUpdateInFlight, SystemTab } from "./system-tab";

type Over = Record<string, unknown>;

const version = (over: Over = {}, updater: Over = {}) =>
  ({
    current: "1.0.0",
    latest: { version: "1.2.0", url: "https://example.test/r/1.2.0", notes: "Fixes things.", preRelease: false, publishedAt: NOW },
    updateAvailable: true,
    checkEnabled: true,
    checkedAt: NOW,
    checkError: null,
    updater: { available: true, state: "idle", targetVersion: null, message: null, updatedAt: null, ...updater },
    ...over,
  }) as never;

const maintenance = (over: Over = {}) =>
  ({
    settings: {
      enabled: true, logRetentionDays: 30, eventRetentionDays: 90, auditRetentionDays: 365, sessionRetentionDays: 7,
      pruneEnabled: true, pruneIntervalHours: 168, pruneKeepHours: 168, backupEnabled: true, backupIntervalHours: 24, backupsKept: 7,
    },
    backupAvailable: true,
    backups: [{ name: "aethera-scheduled-20261004-020000.dump", sizeBytes: 2_500_000, createdAt: NOW, kind: "scheduled" }],
    lastBackupAt: NOW,
    lastPruneAt: null,
    lastRun: null,
    ...over,
  }) as never;

const report = (over: Over = {}) =>
  ({ at: NOW, logChunksDeleted: 12, eventsDeleted: 3, webhookDeliveriesDeleted: 0, auditEventsDeleted: 0, sessionsDeleted: 1, idempotencyRecordsDeleted: 0, pruneJobsQueued: 2, backupTaken: "aethera-manual-1.dump", backupsDeleted: 1, errors: [], ...over }) as never;

const api = (system: Over) => fakeResources({ system: { maintenance: vi.fn().mockResolvedValue(maintenance()), ...system } });

describe("describeReport", () => {
  it("summarises what a pass did", () => {
    expect(describeReport(report())).toBe("16 old records deleted · backup aethera-manual-1.dump · 1 old backup removed · 2 server prunes queued");
    expect(describeReport(report({ logChunksDeleted: 1, eventsDeleted: 0, sessionsDeleted: 0, backupTaken: null, backupsDeleted: 0, pruneJobsQueued: 0 }))).toBe("1 old record deleted");
  });

  it("knows which updater states are unfinished", () => {
    for (const s of ["requested", "backing_up", "pulling", "restarting"]) expect(isUpdateInFlight(s)).toBe(true);
    for (const s of ["idle", "done", "failed", undefined]) expect(isUpdateInFlight(s)).toBe(false);
  });
});

describe("SystemTab version", () => {
  it("shows the running and the latest version, the notes and who can do what", async () => {
    render(<SystemTab api={api({ version: vi.fn().mockResolvedValue(version()) })} pollMs={60_000} />);
    expect(await screen.findByText("update available")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "1.2.0" })).toHaveAttribute("href", "https://example.test/r/1.2.0");
    expect(screen.getByText("Fixes things.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Update to 1\.2\.0/ })).toBeInTheDocument();
  });

  it("says up to date and offers no update when the running version is the newest", async () => {
    render(<SystemTab api={api({ version: vi.fn().mockResolvedValue(version({ current: "1.2.0", updateAvailable: false })) })} pollMs={60_000} />);
    expect(await screen.findByText("up to date")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Update to/ })).not.toBeInTheDocument();
  });

  it("without an updater it explains how to update on the host instead of offering the button", async () => {
    render(<SystemTab api={api({ version: vi.fn().mockResolvedValue(version({}, { available: false })) })} pollMs={60_000} />);
    const note = await screen.findByText(/No updater is running/);
    expect(note.closest("[role=note]")).toHaveTextContent("sudo aethera update");
    expect(screen.queryByRole("button", { name: /Update to/ })).not.toBeInTheDocument();
  });

  it("shows that update checks are off and a failed check without hiding the last answer", async () => {
    render(<SystemTab api={api({ version: vi.fn().mockResolvedValue(version({ latest: null, updateAvailable: false, checkEnabled: false })) })} pollMs={60_000} />);
    expect(await screen.findByText("Update checks are off")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Check now/ })).toBeDisabled();
  });

  it("reports a failed check and asks the API to check again on request", async () => {
    const checkVersion = vi.fn().mockResolvedValue(version({ checkError: "GitHub answered 403 for the releases of x/y." }));
    render(<SystemTab api={api({ version: vi.fn().mockResolvedValue(version({ checkError: "GitHub answered 403 for the releases of x/y." })), checkVersion })} pollMs={60_000} />);
    expect(await screen.findByText(/The last check failed: GitHub answered 403/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /Check now/ }));
    await waitFor(() => expect(checkVersion).toHaveBeenCalledTimes(1));
  });

  it("asks before updating, starts it, and follows the updater until it is done", async () => {
    const startUpdate = vi.fn().mockResolvedValue({ available: true, state: "requested", targetVersion: "1.2.0", message: null, updatedAt: NOW });
    const states = [
      version(),
      version({}, { state: "requested", targetVersion: "1.2.0", message: "Waiting for the updater to pick the request up." }),
      version({}, { state: "backing_up", targetVersion: "1.2.0", message: "Backing up the database to x.dump" }),
      version({ current: "1.2.0", updateAvailable: false }, { state: "done", targetVersion: "1.2.0", message: "Aethera is running 1.2.0." }),
    ];
    let i = 0;
    const versionFn = vi.fn().mockImplementation(async () => states[Math.min(i++, states.length - 1)]);
    render(<SystemTab api={api({ version: versionFn, startUpdate })} pollMs={60_000} />);

    await userEvent.click(await screen.findByRole("button", { name: /Update to 1\.2\.0/ }));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("backed up first");
    expect(startUpdate).not.toHaveBeenCalled();
    await userEvent.click(within(dialog).getByRole("button", { name: "Update now" }));

    await waitFor(() => expect(startUpdate).toHaveBeenCalledTimes(1));
    expect(await screen.findByRole("status")).toHaveTextContent(/Waiting for the updater|Backing up the database/);
  });

  it("shows the API's reason when the update is refused", async () => {
    const startUpdate = vi.fn().mockRejectedValue(new Error("An update is already in progress."));
    render(<SystemTab api={api({ version: vi.fn().mockResolvedValue(version()), startUpdate })} pollMs={60_000} />);
    await userEvent.click(await screen.findByRole("button", { name: /Update to 1\.2\.0/ }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Update now" }));
    expect((await screen.findAllByRole("alert"))[0]).toHaveTextContent("An update is already in progress.");
  });

  it("shows progress and the reason of an update that is running or failed", async () => {
    const running = render(<SystemTab api={api({ version: vi.fn().mockResolvedValue(version({}, { state: "pulling", targetVersion: "1.2.0", message: "Pulling the images of 1.2.0" })) })} pollMs={60_000} />);
    expect(await screen.findByRole("status")).toHaveTextContent("Pulling the new images");
    expect(screen.queryByRole("button", { name: /Update to/ })).not.toBeInTheDocument(); // not while one is running
    running.unmount();

    render(<SystemTab api={api({ version: vi.fn().mockResolvedValue(version({}, { state: "failed", targetVersion: "1.2.0", message: "1.2.0 did not start; rolled back to 1.0.0." })) })} pollMs={60_000} />);
    expect(await screen.findByText(/rolled back to 1\.0\.0/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Update to 1\.2\.0/ })).toBeInTheDocument(); // may try again
  });
});

describe("SystemTab maintenance", () => {
  it("lists the retention settings and the backups", async () => {
    render(<SystemTab api={api({ version: vi.fn().mockResolvedValue(version()) })} pollMs={60_000} />);
    expect(await screen.findByText("365 days")).toBeInTheDocument();
    expect(screen.getByText("30 days")).toBeInTheDocument();
    expect(screen.getByText("aethera-scheduled-20261004-020000.dump")).toBeInTheDocument();
    expect(screen.getByText(/every 7 days, images older than 7 days/)).toBeInTheDocument();
    expect(screen.getByText(/aethera restore/)).toBeInTheDocument();
  });

  it("says when data is kept forever and when the backup cannot run", async () => {
    const m = maintenance({ backupAvailable: false });
    (m as { settings: { auditRetentionDays: number } }).settings.auditRetentionDays = 0;
    render(<SystemTab api={api({ version: vi.fn().mockResolvedValue(version()), maintenance: vi.fn().mockResolvedValue(m) })} pollMs={60_000} />);
    expect(await screen.findByText("kept forever")).toBeInTheDocument();
    expect(screen.getByText(/pg_dump is missing: not running/)).toBeInTheDocument();
  });

  it("runs maintenance now and shows what it did, including its errors", async () => {
    const runMaintenance = vi.fn().mockResolvedValue(report({ errors: ["backup: pg_dump failed: connection refused"] }));
    render(<SystemTab api={api({ version: vi.fn().mockResolvedValue(version()), runMaintenance })} pollMs={60_000} />);
    await userEvent.click(await screen.findByRole("button", { name: /Run now/ }));
    await waitFor(() => expect(runMaintenance).toHaveBeenCalledTimes(1));
    expect(await screen.findByText(/16 old records deleted/)).toBeInTheDocument();
    expect(screen.getByText("backup: pg_dump failed: connection refused")).toBeInTheDocument();
  });

  it("shows an empty state without backups", async () => {
    render(<SystemTab api={api({ version: vi.fn().mockResolvedValue(version()), maintenance: vi.fn().mockResolvedValue(maintenance({ backups: [] })) })} pollMs={60_000} />);
    expect(await screen.findByText("No backups yet.")).toBeInTheDocument();
  });
});
