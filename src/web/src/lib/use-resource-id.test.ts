import { describe, expect, it } from "vitest";
import { idFromPathname } from "./use-resource-id";

describe("idFromPathname", () => {
  it("returns the last path segment", () => {
    expect(idFromPathname("/projects/demo-project")).toBe("demo-project");
    expect(idFromPathname("/projects/demo-project/")).toBe("demo-project");
  });

  it("decodes percent-encoding and survives malformed input", () => {
    expect(idFromPathname("/projects/a%20b")).toBe("a b");
    expect(idFromPathname("/projects/%E0%A4%A")).toBe("%E0%A4%A");
  });

  it("treats the placeholder shell id and empty paths as no id", () => {
    expect(idFromPathname("/projects/_")).toBeNull();
    expect(idFromPathname("/")).toBeNull();
  });
});
