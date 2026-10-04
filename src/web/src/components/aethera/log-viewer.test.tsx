import * as React from "react";
import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { chunksToRows, filterRows, LogViewer, type LogChunk } from "./log-viewer";

const chunks: LogChunk[] = [
  { id: 1, text: "Step 1/3 : FROM node\nStep 2/3 : RUN pnpm install\n", stream: "stdout" },
  { id: 2, text: "WARN deprecated\n", stream: "stderr" },
  { id: 3, text: "done", stream: "stdout" },
];

describe("chunksToRows", () => {
  it("joins a line that spans chunks of the same stream and keeps a trailing partial line", () => {
    const rows = chunksToRows([
      { id: 1, text: "Step 2/3 : RUN pnpm ", stream: "stdout" },
      { id: 2, text: "install\nnext", stream: "stdout" },
    ]);
    expect(rows.map((r) => r.text)).toEqual(["Step 2/3 : RUN pnpm install", "next"]);
    expect(rows.map((r) => r.n)).toEqual([1, 2]);
  });

  it("does not glue lines of different streams together", () => {
    const rows = chunksToRows([
      { id: 1, text: "partial", stream: "stdout" },
      { id: 2, text: "error line\n", stream: "stderr" },
    ]);
    expect(rows.map((r) => [r.text, r.stream])).toEqual([
      ["partial", "stdout"],
      ["error line", "stderr"],
    ]);
  });

  it("strips carriage returns and survives an empty log", () => {
    expect(chunksToRows([{ id: 1, text: "a\r\nb\r\n" }]).map((r) => r.text)).toEqual(["a", "b"]);
    expect(chunksToRows([])).toEqual([]);
  });
});

describe("filterRows", () => {
  const rows = chunksToRows(chunks);

  it("filters by stderr and by search text", () => {
    expect(filterRows(rows, { query: "", stderrOnly: true, onlyMatches: true }).map((r) => r.text)).toEqual(["WARN deprecated"]);
    expect(filterRows(rows, { query: "PNPM", stderrOnly: false, onlyMatches: true }).map((r) => r.text)).toEqual(["Step 2/3 : RUN pnpm install"]);
    expect(filterRows(rows, { query: "PNPM", stderrOnly: false, onlyMatches: false })).toHaveLength(rows.length);
  });
});

describe("LogViewer", () => {
  it("shows the lines, counts matches and highlights them", async () => {
    render(<LogViewer chunks={chunks} downloadHref="/api/v1/x?download=true" />);
    expect(screen.getByRole("log", { name: "Log output" })).toHaveTextContent("Step 1/3 : FROM node");

    await userEvent.type(screen.getByRole("searchbox", { name: "Search the log" }), "step");
    expect(screen.getByText("2 matches")).toBeInTheDocument();
    expect(screen.queryByText("done")).not.toBeInTheDocument();
    expect(document.querySelectorAll("mark")).toHaveLength(2);
    expect(screen.getByRole("link", { name: /download/i })).toHaveAttribute("href", "/api/v1/x?download=true");
  });

  it("says so when nothing matches and when there is no output yet", async () => {
    const { rerender } = render(<LogViewer chunks={[]} emptyText="Waiting for output…" />);
    expect(screen.getByText("Waiting for output…")).toBeInTheDocument();
    rerender(<LogViewer chunks={chunks} />);
    await userEvent.type(screen.getByRole("searchbox", { name: "Search the log" }), "zzz");
    expect(screen.getByText("No lines match the filter.")).toBeInTheDocument();
  });
});
