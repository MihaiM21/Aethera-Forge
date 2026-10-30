import { describe, expect, it } from "vitest";
import { parseScope, publishedAddress, SCOPES, SCOPE_HINT, SCOPE_LABEL, STATUS_TONE } from "./format";

describe("publishedAddress", () => {
  it("uses the mesh address for a private port", () => {
    expect(publishedAddress({ publishedPort: 5432, bindScope: "private" }, { meshAddress: "10.210.0.3", publicHost: "db.example.com" })).toBe("10.210.0.3:5432");
  });
  it("uses the public host for a public port and says nothing without one", () => {
    expect(publishedAddress({ publishedPort: 8080, bindScope: "public" }, { meshAddress: "10.210.0.3", publicHost: "203.0.113.7" })).toBe("203.0.113.7:8080");
    expect(publishedAddress({ publishedPort: 8080 }, { meshAddress: "10.210.0.3" })).toBeNull();
  });
  it("has no address for a private port without a mesh address, for loopback and for an unpublished port", () => {
    expect(publishedAddress({ publishedPort: 5432, bindScope: "private" }, { meshAddress: null })).toBeNull();
    expect(publishedAddress({ publishedPort: 5432, bindScope: "loopback" }, { meshAddress: "10.210.0.3", publicHost: "x" })).toBeNull();
    expect(publishedAddress({ publishedPort: null, bindScope: "private" }, { meshAddress: "10.210.0.3" })).toBeNull();
  });
});

describe("scopes", () => {
  it("parses anything unknown as public", () => {
    expect(parseScope("private")).toBe("private");
    expect(parseScope("loopback")).toBe("loopback");
    expect(parseScope("whatever")).toBe("public");
    expect(parseScope(undefined)).toBe("public");
  });
  it("labels and explains every scope", () => {
    for (const s of SCOPES) {
      expect(SCOPE_LABEL[s]).toBeTruthy();
      expect(SCOPE_HINT[s]).toBeTruthy();
    }
    expect(STATUS_TONE.up).toBe("success");
    expect(STATUS_TONE.failed).toBe("danger");
  });
});
