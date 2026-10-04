import { describe, expect, it } from "vitest";
import { formatAge, formatBytes, formatCountdown, formatPercent, formatRate, ratioPercent, toDate, toNum } from "./format";

describe("format helpers", () => {
  it("coerces generated number|string values", () => {
    expect(toNum("42")).toBe(42);
    expect(toNum(null)).toBeNull();
    expect(toNum("")).toBeNull();
    expect(toNum("abc")).toBeNull();
    expect(toDate("not a date")).toBeNull();
    expect(toDate("2026-10-04T10:00:00Z")?.getUTCHours()).toBe(10);
  });

  it("formats bytes with binary units", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(1536)).toBe("1.5 KiB");
    expect(formatBytes(8 * 1024 ** 3)).toBe("8.0 GiB");
    expect(formatBytes(null)).toBe("-");
    expect(formatRate(2048)).toBe("2.0 KiB/s");
  });

  it("formats percent and ratios", () => {
    expect(formatPercent(12.34, 1)).toBe("12.3%");
    expect(formatPercent(null)).toBe("-");
    expect(ratioPercent(50, 200)).toBe(25);
    expect(ratioPercent(5, 0)).toBeNull();
    expect(ratioPercent(null, 10)).toBeNull();
    expect(ratioPercent(500, 100)).toBe(100);
  });

  it("formats ages", () => {
    const now = new Date("2026-10-04T12:00:00Z");
    expect(formatAge("2026-10-04T11:59:30Z", now)).toBe("30s");
    expect(formatAge("2026-10-04T11:15:00Z", now)).toBe("45m");
    expect(formatAge("2026-10-04T05:00:00Z", now)).toBe("7h");
    expect(formatAge("2026-10-01T12:00:00Z", now)).toBe("3d");
    expect(formatAge(undefined, now)).toBe("-");
  });

  it("formats a countdown and clamps at zero", () => {
    const now = new Date("2026-10-04T12:00:00Z");
    expect(formatCountdown("2026-10-04T12:05:09Z", now)).toBe("5:09");
    expect(formatCountdown("2026-10-04T13:01:01Z", now)).toBe("1:01:01");
    expect(formatCountdown("2026-10-04T11:00:00Z", now)).toBe("0:00");
  });
});
