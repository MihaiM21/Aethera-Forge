import { describe, expect, it } from "vitest";
import { createRouter } from "./export-routing.mjs";

type Result = { kind: string; file?: string; location?: string; looksLikeAsset?: boolean };
const router = createRouter([
  "index.html",
  "404.html",
  "_not-found.html",
  "login.html",
  "login.txt",
  "login/__next.login.__PAGE__.txt",
  "dashboard.html",
  "projects.html",
  "projects/_.html",
  "projects/_.txt",
  "applications.html",
  "applications/_.html",
  "applications/_/deployments.html",
  "applications/_/deployments/_.html",
  "_next/static/chunks/app.js",
  "favicon.ico",
]) as { resolve: (p: string) => Result };

const r = (p: string) => router.resolve(p);

describe("export routing rules", () => {
  it("maps / to index.html", () => {
    expect(r("/")).toEqual({ kind: "file", file: "index.html" });
  });

  it("serves exact files first (assets, payloads)", () => {
    expect(r("/_next/static/chunks/app.js")).toEqual({ kind: "file", file: "_next/static/chunks/app.js" });
    expect(r("/favicon.ico")).toEqual({ kind: "file", file: "favicon.ico" });
    expect(r("/login.txt")).toEqual({ kind: "file", file: "login.txt" });
  });

  it("maps /name to name.html even when a name/ directory exists", () => {
    expect(r("/login")).toEqual({ kind: "file", file: "login.html" });
    expect(r("/projects")).toEqual({ kind: "file", file: "projects.html" });
  });

  it("maps a dynamic segment to the _ shell", () => {
    expect(r("/projects/demo-project")).toEqual({ kind: "file", file: "projects/_.html" });
    expect(r("/applications/0192f3")).toEqual({ kind: "file", file: "applications/_.html" });
    expect(r("/projects/a%20b")).toEqual({ kind: "file", file: "projects/_.html" });
  });

  it("matches nested dynamic routes and prefers literal segments", () => {
    expect(r("/applications/abc/deployments")).toEqual({
      kind: "file",
      file: "applications/_/deployments.html",
    });
    expect(r("/applications/abc/deployments/xyz")).toEqual({
      kind: "file",
      file: "applications/_/deployments/_.html",
    });
  });

  it("does not match a wildcard against zero or extra segments", () => {
    expect(r("/projects/a/b").kind).toBe("not_found");
    expect(r("/applications/abc/nothing").kind).toBe("not_found");
  });

  it("redirects trailing slashes to the canonical path", () => {
    expect(r("/login/")).toEqual({ kind: "redirect", location: "/login" });
    expect(r("/projects/abc/")).toEqual({ kind: "redirect", location: "/projects/abc" });
  });

  it("reports unknown routes and flags asset-looking paths", () => {
    expect(r("/nope")).toEqual({ kind: "not_found", looksLikeAsset: false });
    expect(r("/missing.js")).toEqual({ kind: "not_found", looksLikeAsset: true });
  });

  it("leaves /api and /hubs to the API", () => {
    expect(r("/api/v1/auth/me").kind).toBe("api");
    expect(r("/hubs/jobs").kind).toBe("api");
  });

  it("rejects traversal and malformed input", () => {
    expect(r("/projects/../etc/passwd").kind).toBe("bad_request");
    expect(r("/%2e%2e/secret").kind).toBe("bad_request");
    expect(r("/%E0%A4%A").kind).toBe("bad_request");
    expect(r("/a%5Cb").kind).toBe("bad_request");
  });
});
