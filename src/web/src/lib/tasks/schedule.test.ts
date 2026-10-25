import { describe, expect, it, vi } from "vitest";
import { PRESETS, browserTimeZone, describeSchedule, formatTimeout, presetKeyOf } from "./schedule";

describe("schedule helpers", () => {
  it("recognises the presets and calls everything else custom", () => {
    for (const p of PRESETS) expect(presetKeyOf(p.cron)).toBe(p.key);
    expect(presetKeyOf("  0   3 * * *  ")).toBe("daily");
    expect(presetKeyOf("5 4 * * 1")).toBe("custom");
  });

  it("describes the common shapes in words and leaves the rest as cron", () => {
    expect(describeSchedule("* * * * *")).toBe("Every minute");
    expect(describeSchedule("*/5 * * * *")).toBe("Every 5 minutes");
    expect(describeSchedule("0 * * * *")).toBe("Every hour");
    expect(describeSchedule("15 * * * *")).toBe("Every hour at minute 15");
    expect(describeSchedule("30 14 * * *")).toBe("Every day at 14:30");
    expect(describeSchedule("0 3 * * 0")).toBe("Every Sunday at 03:00");
    expect(describeSchedule("0 3 * * 7")).toBe("Every Sunday at 03:00");
    expect(describeSchedule("0 9 15 * *")).toBe("On day 15 of every month at 09:00");
    expect(describeSchedule("@daily")).toBe("Every day at 00:00");
    expect(describeSchedule("0 9-17 * * 1-5")).toBe("0 9-17 * * 1-5");
    expect(describeSchedule("0 0 1 6 *")).toBe("0 0 1 6 *");
    expect(describeSchedule("nonsense")).toBe("nonsense");
  });

  it("formats timeouts the way people say them", () => {
    expect(formatTimeout(3600)).toBe("1 h");
    expect(formatTimeout(7200)).toBe("2 h");
    expect(formatTimeout(300)).toBe("5 min");
    expect(formatTimeout(90)).toBe("90 s");
  });

  it("defaults to the browser's time zone and to UTC when it cannot be read", () => {
    expect(browserTimeZone()).toBe(Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC");
    const spy = vi.spyOn(Intl, "DateTimeFormat").mockImplementation(() => {
      throw new Error("no Intl");
    });
    expect(browserTimeZone()).toBe("UTC");
    spy.mockRestore();
  });
});
