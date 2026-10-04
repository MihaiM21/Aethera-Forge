import * as React from "react";
import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { LineChart, buildPaths } from "./line-chart";
import { Stepper } from "./stepper";
import { UsageBar } from "./usage-bar";

describe("buildPaths", () => {
  it("breaks the line at missing samples", () => {
    const d = buildPaths(
      [
        { t: 0, v: 1 },
        { t: 1, v: 2 },
        { t: 2, v: null },
        { t: 3, v: 3 },
      ],
      (t) => t * 10,
      (v) => v * 10,
    );
    expect(d).toBe("M0.0 10.0L10.0 20.0M30.0 30.0");
  });
});

describe("LineChart", () => {
  it("describes itself for assistive tech and shows the latest value", () => {
    render(
      <LineChart
        title="CPU"
        yMax={100}
        formatY={(v) => `${v}%`}
        formatX={(t) => `t${t}`}
        series={[{ key: "cpu", label: "cpu", color: "var(--chart-1)", points: [{ t: 1, v: 10 }, { t: 2, v: 42 }] }]}
      />,
    );
    expect(screen.getByRole("img", { name: "CPU: cpu 42%" })).toBeInTheDocument();
    expect(screen.getAllByText("42%").length).toBeGreaterThan(0);
  });

  it("renders an explicit empty state", () => {
    render(<LineChart title="Network" formatY={String} series={[{ key: "rx", label: "rx", color: "red", points: [] }]} />);
    expect(screen.getByText("No samples in this range")).toBeInTheDocument();
  });
});

describe("UsageBar", () => {
  it("exposes a meter with value text", () => {
    render(<UsageBar label="RAM" value={62.4} detail="5 GiB / 8 GiB" />);
    const m = screen.getByRole("meter", { name: "RAM" });
    expect(m).toHaveAttribute("aria-valuenow", "62");
    expect(m).toHaveAttribute("aria-valuetext", "62%, 5 GiB / 8 GiB");
  });

  it("does not pretend an unknown value is 0%", () => {
    render(<UsageBar label="CPU" value={null} />);
    const m = screen.getByRole("meter", { name: "CPU" });
    expect(m).not.toHaveAttribute("aria-valuenow");
    expect(m).toHaveAttribute("aria-valuetext", "no data");
  });
});

describe("Stepper", () => {
  it("marks the current and completed steps", () => {
    render(<Stepper aria-label="steps" current={1} steps={[{ title: "Details" }, { title: "Connect" }, { title: "Done" }]} />);
    expect(screen.getByRole("listitem", { current: "step" })).toHaveTextContent("Connect");
    expect(screen.getByText("(completed)")).toBeInTheDocument();
  });
});
