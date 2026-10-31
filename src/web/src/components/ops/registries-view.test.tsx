import * as React from "react";
import { describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { fakeResources, NOW } from "@/test/resources-fixtures";
import { RegistriesView } from "./registries-view";

const registry = (over: object) => ({ id: "r1", name: "GitHub", url: "ghcr.io", username: "ci-bot", hasCredentials: true, createdAt: NOW, updatedAt: NOW, ...over });

describe("registries", () => {
  it("tests a registry connection and shows the verdict next to it", async () => {
    const list = vi.fn().mockResolvedValue({ items: [registry({}), registry({ id: "r2", name: "Local", url: "registry.lan:5000", username: null, hasCredentials: false })], nextCursor: null });
    const test = vi
      .fn()
      .mockResolvedValueOnce({ result: "ok", message: "Reachable; the stored credentials were accepted.", httpStatus: 200 })
      .mockResolvedValueOnce({ result: "unreachable", message: "registry.lan:5000 refused the connection.", httpStatus: null });
    const user = userEvent.setup();
    render(<RegistriesView api={fakeResources({ registries: { list, test } })} />);

    const rows = await screen.findAllByRole("listitem");
    await user.click(within(rows[0]).getByRole("button", { name: "Test GitHub" }));
    expect(await within(rows[0]).findByRole("status")).toHaveTextContent("ok: Reachable; the stored credentials were accepted.");
    expect(test).toHaveBeenCalledWith("r1");
    expect(within(rows[1]).queryByRole("status")).not.toBeInTheDocument();

    await user.click(within(rows[1]).getByRole("button", { name: "Test Local" }));
    expect(await within(rows[1]).findByRole("status")).toHaveTextContent("unreachable: registry.lan:5000 refused the connection.");
    expect(test).toHaveBeenCalledWith("r2");
  });

  it("deletes a registry with its name as the confirmation the API requires", async () => {
    const list = vi.fn().mockResolvedValue({ items: [registry({})], nextCursor: null });
    const remove = vi.fn().mockResolvedValue(undefined);
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    const user = userEvent.setup();
    render(<RegistriesView api={fakeResources({ registries: { list, remove } })} />);

    await user.click(await screen.findByRole("button", { name: "Delete GitHub" }));
    expect(remove).toHaveBeenCalledWith("r1", "GitHub");
    confirm.mockRestore();
  });
});
