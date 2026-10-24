import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "@/lib/api/errors";
import { fakeBackups, makeDestination, makePolicy } from "@/lib/backups/test-fixtures";
import { SHOW_MASTER_KEY_COMMAND } from "@/lib/backups/presets";
import { BackupsSettingsTab } from "./backups-settings-tab";

const page = <T,>(items: T[]) => ({ items, nextCursor: null });
const noPolicy = { policy: null, defaultSchedule: "0 3 * * *" };

describe("Settings > Backups", () => {
  it("always shows the master key warning with the exact command to print the key", async () => {
    const api = fakeBackups({ destinations: { list: vi.fn().mockResolvedValue(page([])) }, system: { policy: vi.fn().mockResolvedValue(noPolicy) } });
    render(<BackupsSettingsTab api={api} />);

    const warning = screen.getByText(/Keep a copy of the master key/).closest("[role=alert]") as HTMLElement;
    expect(warning).toHaveTextContent("/opt/aethera/.env");
    expect(warning).toHaveTextContent("AETHERA_MASTER_KEY");
    expect(warning).toHaveTextContent("no backup can be restored");
    expect(within(warning).getByText(SHOW_MASTER_KEY_COMMAND)).toBeInTheDocument();
    expect(await screen.findByText(/No backup destination yet/)).toBeInTheDocument();
  });

  it("lists destinations with the result of the last test and never a secret", async () => {
    const api = fakeBackups({
      destinations: {
        list: vi.fn().mockResolvedValue(
          page([
            makeDestination({ lastTestedAt: "2026-10-23T09:00:00.000Z" }),
            makeDestination({ id: "dest-2", name: "Broken", isDefault: false, lastTestedAt: "2026-10-23T09:00:00.000Z", lastTestError: "The access key or secret was rejected by the store." }),
            makeDestination({ id: "dest-3", name: "Disk", kind: "local", endpoint: null, bucket: null, accessKeyId: null, hasSecret: false, isDefault: false }),
          ]),
        ),
      },
      system: { policy: vi.fn().mockResolvedValue(noPolicy) },
    });
    render(<BackupsSettingsTab api={api} />);

    expect(await screen.findByText("Turn One R2")).toBeInTheDocument();
    expect(screen.getByText("default")).toBeInTheDocument();
    expect(screen.getAllByText(/https:\/\/abc123\.r2\.cloudflarestorage\.com · turn-one-backups\/aethera/)).toHaveLength(2);
    expect(screen.getByText("test failed")).toBeInTheDocument();
    expect(screen.getByText(/rejected by the store/)).toBeInTheDocument();
    expect(screen.getByText(/the agent's backup directory/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Test connection of Disk/ })).not.toBeInTheDocument(); // nothing to test for a local destination
  });

  it("creates a Cloudflare R2 destination from the preset", async () => {
    const create = vi.fn().mockResolvedValue(makeDestination());
    const list = vi.fn().mockResolvedValue(page([]));
    render(<BackupsSettingsTab api={fakeBackups({ destinations: { list, create }, system: { policy: vi.fn().mockResolvedValue(noPolicy) } })} />);
    await screen.findByText(/No backup destination yet/);

    await userEvent.click(screen.getByRole("button", { name: /Add destination/ }));
    const dialog = await screen.findByRole("dialog");
    // the preset is Cloudflare R2: region auto, the endpoint still has the account id to fill in
    expect(within(dialog).getByLabelText("Provider")).toHaveValue("r2");
    expect(within(dialog).getByLabelText("Region")).toHaveValue("auto");
    expect(within(dialog).getByLabelText("Endpoint")).toHaveValue("https://<account id>.r2.cloudflarestorage.com");

    await userEvent.type(within(dialog).getByLabelText("Name"), "Turn One R2");
    await userEvent.type(within(dialog).getByLabelText("Bucket"), "turn-one-backups");
    await userEvent.type(within(dialog).getByLabelText("Access key id"), "AKIA");
    await userEvent.type(within(dialog).getByLabelText("Secret access key"), "s3cr3t");
    expect(within(dialog).getByRole("button", { name: "Add destination" })).toBeDisabled(); // the endpoint still contains <account id>

    const endpoint = within(dialog).getByLabelText("Endpoint");
    await userEvent.clear(endpoint);
    await userEvent.type(endpoint, "https://abc123.r2.cloudflarestorage.com");
    await userEvent.click(within(dialog).getByRole("button", { name: "Add destination" }));

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    expect(create).toHaveBeenCalledWith({
      name: "Turn One R2", kind: "s3", endpoint: "https://abc123.r2.cloudflarestorage.com", region: "auto", bucket: "turn-one-backups", pathPrefix: "aethera",
      accessKeyId: "AKIA", secretAccessKey: "s3cr3t", forcePathStyle: false, isDefault: false,
    });
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  }, 30_000);

  it("the other presets fill their own endpoints and MinIO asks for path-style addressing", async () => {
    render(<BackupsSettingsTab api={fakeBackups({ destinations: { list: vi.fn().mockResolvedValue(page([])) }, system: { policy: vi.fn().mockResolvedValue(noPolicy) } })} />);
    await userEvent.click(await screen.findByRole("button", { name: /Add destination/ }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.selectOptions(within(dialog).getByLabelText("Provider"), "minio");
    expect(within(dialog).getByLabelText("Endpoint")).toHaveValue("http://minio:9000");
    expect(within(dialog).getByLabelText(/Path-style addressing/)).toBeChecked();
    await userEvent.selectOptions(within(dialog).getByLabelText("Provider"), "b2");
    expect(within(dialog).getByLabelText("Endpoint")).toHaveValue("https://s3.<region>.backblazeb2.com");
    await userEvent.selectOptions(within(dialog).getByLabelText("Provider"), "local");
    expect(within(dialog).queryByLabelText("Endpoint")).not.toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Add destination" })).toBeDisabled(); // a name is still needed
  });

  it("shows the API's field errors in the dialog", async () => {
    const create = vi.fn().mockRejectedValue(
      new ApiError({ status: 422, code: "validation.failed", title: "Validation failed", errors: [{ pointer: "/endpoint", code: "pattern", message: "Use https." }] }),
    );
    render(<BackupsSettingsTab api={fakeBackups({ destinations: { list: vi.fn().mockResolvedValue(page([])), create }, system: { policy: vi.fn().mockResolvedValue(noPolicy) } })} />);
    await userEvent.click(await screen.findByRole("button", { name: /Add destination/ }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByLabelText("Name"), "x");
    const endpoint = within(dialog).getByLabelText("Endpoint");
    await userEvent.clear(endpoint);
    await userEvent.type(endpoint, "http://example.com");
    await userEvent.type(within(dialog).getByLabelText("Bucket"), "bucket-one");
    await userEvent.type(within(dialog).getByLabelText("Access key id"), "k");
    await userEvent.type(within(dialog).getByLabelText("Secret access key"), "s");
    await userEvent.click(within(dialog).getByRole("button", { name: "Add destination" }));
    expect(await within(dialog).findByText("Use https.")).toBeInTheDocument();
  }, 30_000);

  it("tests a connection and shows the failure that came back", async () => {
    const d = makeDestination();
    const list = vi.fn().mockResolvedValueOnce(page([d])).mockResolvedValue(page([{ ...d, lastTestedAt: "2026-10-23T10:00:00.000Z", lastTestError: "Access denied: the key may not read, write and delete objects in this bucket." }]));
    const test = vi.fn().mockResolvedValue({ ok: false, error: "Access denied", testedAt: "2026-10-23T10:00:00.000Z" });
    render(<BackupsSettingsTab api={fakeBackups({ destinations: { list, test }, system: { policy: vi.fn().mockResolvedValue(noPolicy) } })} />);

    await userEvent.click(await screen.findByRole("button", { name: /Test connection of Turn One R2/ }));
    await waitFor(() => expect(test).toHaveBeenCalledWith("dest-1"));
    expect(await screen.findByText(/Access denied: the key may not read/)).toBeInTheDocument();
    expect(screen.getByText("test failed")).toBeInTheDocument();
  });

  it("deleting a destination needs its name typed", async () => {
    const remove = vi.fn().mockResolvedValue(undefined);
    const list = vi.fn().mockResolvedValue(page([makeDestination()]));
    render(<BackupsSettingsTab api={fakeBackups({ destinations: { list, remove }, system: { policy: vi.fn().mockResolvedValue(noPolicy) } })} />);
    await userEvent.click(await screen.findByRole("button", { name: "Delete Turn One R2" }));
    const dialog = await screen.findByRole("alertdialog").catch(() => screen.getByRole("dialog"));
    const confirm = within(dialog).getByRole("button", { name: "Delete" });
    expect(confirm).toBeDisabled();
    await userEvent.type(within(dialog).getByRole("textbox"), "Turn One R2");
    await userEvent.click(confirm);
    await waitFor(() => expect(remove).toHaveBeenCalledWith("dest-1", "Turn One R2"));
  });

  it("enables the backup of Aethera's own database with a destination and a schedule", async () => {
    const putPolicy = vi.fn().mockResolvedValue({ policy: makePolicy({ workloadId: null, kind: "controlPlane" }), defaultSchedule: "0 3 * * *" });
    const api = fakeBackups({
      destinations: { list: vi.fn().mockResolvedValue(page([makeDestination(), makeDestination({ id: "dest-3", name: "Disk", kind: "local", isDefault: false })])) },
      system: { policy: vi.fn().mockResolvedValue(noPolicy), putPolicy },
    });
    render(<BackupsSettingsTab api={api} />);
    const button = await screen.findByRole("button", { name: "Enable backups of the database" });

    const destination = screen.getByLabelText("Destination");
    expect(within(destination).queryByRole("option", { name: "Disk" })).not.toBeInTheDocument(); // only S3 destinations can hold it
    await userEvent.selectOptions(screen.getByLabelText("Schedule"), "6h");
    await userEvent.click(button);

    await waitFor(() => expect(putPolicy).toHaveBeenCalledTimes(1));
    expect(putPolicy).toHaveBeenCalledWith({ destinationId: "dest-1", enabled: true, schedule: "0 */6 * * *", timeZone: "UTC", keepDaily: 7, keepWeekly: 4, keepMonthly: 6 });
  });

  it("shows the existing policy of Aethera's own database, backs up now and explains the restore on the host", async () => {
    const policy = makePolicy({ workloadId: null, kind: "controlPlane", lastStatus: "failed" });
    const run = vi.fn().mockResolvedValue({ id: "job-1" });
    const api = fakeBackups({
      destinations: { list: vi.fn().mockResolvedValue(page([makeDestination()])) },
      system: { policy: vi.fn().mockResolvedValue({ policy, defaultSchedule: "0 3 * * *" }) },
      policies: { run },
    });
    render(<BackupsSettingsTab api={api} />);

    expect(await screen.findByText("Daily at 03:00 (UTC)")).toBeInTheDocument();
    expect(screen.getByText("last: failed")).toBeInTheDocument();
    expect(screen.getByText(/to Turn One R2 · 7 daily · 4 weekly · 6 monthly/)).toBeInTheDocument();
    expect(screen.getByText(/aethera backup-decrypt backup\.pgdump\.aeb backup\.dump/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /Back up now/ }));
    await waitFor(() => expect(run).toHaveBeenCalledWith("pol-1"));
  });
});
