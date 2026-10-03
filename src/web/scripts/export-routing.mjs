// Reference implementation of the URL -> file rules the .NET API must apply
// when serving the static export (docs/architecture/0005-web-routing.md).
// Pure functions over a list of exported file paths, so it is unit-testable and
// is reused by scripts/serve-export.mjs and scripts/verify-export.mjs.

/** Paths that are never served by the static handler: the API owns them. */
export const RESERVED_PREFIXES = ["/api/", "/hubs/"];

/** Exported pages that are not routes. */
const NON_ROUTE_HTML = new Set(["index.html", "404.html", "_not-found.html"]);

/** Directory segment used for dynamic route parameters (`generateStaticParams`). */
export const PLACEHOLDER = "_";

/**
 * @param {string[]} files  posix paths relative to the export root, e.g. "login.html", "_next/static/x.js"
 */
export function createRouter(files) {
  const fileSet = new Set(files);

  /** Dynamic page templates, e.g. ["projects", "_"] -> "projects/_.html". */
  const templates = files
    .filter((f) => f.endsWith(".html") && !NON_ROUTE_HTML.has(f))
    .map((f) => ({ file: f, segments: f.slice(0, -".html".length).split("/") }))
    .filter((t) => t.segments.includes(PLACEHOLDER))
    .map((t) => ({ ...t, wildcards: t.segments.filter((s) => s === PLACEHOLDER).length }))
    .sort((a, b) => a.wildcards - b.wildcards || a.file.localeCompare(b.file));

  function matchTemplate(segments) {
    for (const t of templates) {
      if (t.segments.length !== segments.length) continue;
      const ok = t.segments.every((s, i) => (s === PLACEHOLDER ? segments[i] !== "" : s === segments[i]));
      if (ok) return t.file;
    }
    return null;
  }

  /**
   * @param {string} rawPath  the request path without query string (still percent-encoded)
   * @returns {{kind:"file"|"redirect"|"not_found"|"bad_request"|"api", file?:string, location?:string, looksLikeAsset?:boolean}}
   */
  function resolve(rawPath) {
    if (!rawPath.startsWith("/")) return { kind: "bad_request" };
    if (RESERVED_PREFIXES.some((p) => rawPath.startsWith(p) || rawPath === p.slice(0, -1))) {
      return { kind: "api" };
    }

    let decoded;
    try {
      decoded = decodeURIComponent(rawPath);
    } catch {
      return { kind: "bad_request" };
    }
    if (decoded.includes("\0") || decoded.includes("\\")) return { kind: "bad_request" };

    const trailingSlash = decoded.length > 1 && decoded.endsWith("/");
    const trimmed = trailingSlash ? decoded.replace(/\/+$/, "") : decoded;
    const segments = trimmed.split("/").slice(1);
    if (segments.some((s) => s === ".." || s === ".")) return { kind: "bad_request" };

    if (trimmed === "" || trimmed === "/") return { kind: "file", file: "index.html" };

    const rel = segments.join("/");
    let target = null;
    if (fileSet.has(rel)) target = rel; // assets, _next/*, *.txt payloads, favicon
    else if (fileSet.has(`${rel}.html`)) target = `${rel}.html`; // /login -> login.html
    else target = matchTemplate(segments); // /projects/abc -> projects/_.html

    if (target) {
      // Canonical form has no trailing slash (next.config trailingSlash: false).
      if (trailingSlash) return { kind: "redirect", location: trimmed };
      return { kind: "file", file: target };
    }

    const last = segments[segments.length - 1];
    return { kind: "not_found", looksLikeAsset: last.includes(".") };
  }

  return { resolve, templates: templates.map((t) => t.file) };
}
