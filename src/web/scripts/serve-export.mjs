#!/usr/bin/env node
// Minimal static server for `out/` that applies the same URL -> file rules the
// .NET API must implement (see docs/architecture/0005-web-routing.md and
// scripts/export-routing.mjs). Used for screenshots and manual checks.
//
//   node scripts/serve-export.mjs [port] [dir]
import { createServer } from "node:http";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, extname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { createRouter } from "./export-routing.mjs";

const TYPES = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".txt": "text/plain; charset=utf-8",
  ".ico": "image/x-icon",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".woff2": "font/woff2",
  ".woff": "font/woff",
  ".map": "application/json",
};

function listFiles(root, rel = "") {
  const out = [];
  for (const name of readdirSync(join(root, rel))) {
    const r = rel ? `${rel}/${name}` : name;
    if (statSync(join(root, r)).isDirectory()) out.push(...listFiles(root, r));
    else out.push(r);
  }
  return out;
}

export function startServer({ port = 3000, dir } = {}) {
  const root = resolve(dir ?? join(dirname(fileURLToPath(import.meta.url)), "..", "out"));
  const router = createRouter(listFiles(root));

  const server = createServer((req, res) => {
    const url = new URL(req.url ?? "/", "http://localhost");
    if (req.method !== "GET" && req.method !== "HEAD") {
      res.writeHead(405, { Allow: "GET, HEAD" }).end();
      return;
    }
    const r = router.resolve(url.pathname);
    const send = (status, file, extra = {}) => {
      const body = readFileSync(join(root, file));
      res.writeHead(status, {
        "Content-Type": TYPES[extname(file)] ?? "application/octet-stream",
        "Content-Length": body.length,
        "Cache-Control": file.startsWith("_next/static/") ? "public, max-age=31536000, immutable" : "no-cache",
        ...extra,
      });
      res.end(req.method === "HEAD" ? undefined : body);
    };

    switch (r.kind) {
      case "file":
        return send(200, r.file);
      case "redirect":
        res.writeHead(308, { Location: r.location + url.search }).end();
        return;
      case "api":
        // The real API owns /api and /hubs; the static server never answers them.
        res.writeHead(404, { "Content-Type": "application/problem+json" });
        res.end(JSON.stringify({ title: "Not found", status: 404, code: "request.not_found" }));
        return;
      case "bad_request":
        res.writeHead(400).end();
        return;
      default: {
        const wantsHtml = (req.headers.accept ?? "").includes("text/html");
        if (r.looksLikeAsset || !wantsHtml) {
          res.writeHead(404, { "Content-Type": "text/plain" }).end("Not found");
          return;
        }
        return send(404, "404.html");
      }
    }
  });

  return new Promise((ok) => server.listen(port, () => ok({ server, port: server.address().port, root })));
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  const port = Number(process.argv[2] ?? 3000);
  const { root } = await startServer({ port, dir: process.argv[3] });
  console.log(`serving ${root} on http://localhost:${port}`);
}
