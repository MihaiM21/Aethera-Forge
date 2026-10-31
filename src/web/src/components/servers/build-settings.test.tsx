import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fakeApi, makeServer } from "@/test/servers-fixtures";
import { fakeResources } from "@/test/resources-fixtures";
import { BuildSettingsForm, bytesToMib, mibToBytes } from "./build-settings";

describe("memory in MiB", () => {
  it("converts to bytes and back, empty meaning unlimited", () => {
    expect(mibToBytes("2048")).toBe(2 * 1024 ** 3);
    expect(mibToBytes("  ")).toBeNull();
    expect(bytesToMib(2 * 1024 ** 3)).toBe("2048");
    expect(bytesToMib(null)).toBe("");
  });
});

describe("BuildSettingsForm", () => {
  const registries = { list: vi.fn().mockResolvedValue({ items: [{ id: "reg-1", name: "GHCR", url: "ghcr.io", username: "mihaim21", namespace: "mihaim21", hasCredentials: true }], nextCursor: null }) };

  it("saves the quota and the registry, and clears them when emptied", async () => {
    const server = makeServer({ id: "srv-1", name: "builder" });
    const update = vi.fn().mockResolvedValue(server);
    const onSaved = vi.fn();
    render(<BuildSettingsForm server={server} api={fakeApi({ update })} resources={fakeResources({ registries })} onSaved={onSaved} />);

    await userEvent.type(screen.getByLabelText("Build CPU limit (cores)"), "2");
    await userEvent.type(screen.getByLabelText("Build memory limit (MiB)"), "2048");
    await userEvent.selectOptions(await screen.findByLabelText("Build registry"), "reg-1");
    expect(await screen.findByText(/ghcr.io\/mihaim21\/<app>:<deployment>/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Save build settings" }));

    await waitFor(() => expect(update).toHaveBeenCalledWith("srv-1", { buildCpuLimit: 2, buildMemoryLimitBytes: 2 * 1024 ** 3, buildRegistryId: "reg-1" }));
    expect(onSaved).toHaveBeenCalled();

    await userEvent.clear(screen.getByLabelText("Build CPU limit (cores)"));
    await userEvent.clear(screen.getByLabelText("Build memory limit (MiB)"));
    await userEvent.selectOptions(screen.getByLabelText("Build registry"), "");
    await userEvent.click(screen.getByRole("button", { name: "Save build settings" }));
    await waitFor(() => expect(update).toHaveBeenLastCalledWith("srv-1", { buildCpuLimit: null, buildMemoryLimitBytes: null, buildRegistryId: null }));
  });
});
