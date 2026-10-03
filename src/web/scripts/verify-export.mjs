#!/usr/bin/env node
// Checks that `next build` produced the files the API relies on, and that the
// documented URL -> file rules resolve them (docs/architecture/0005-web-routing.md).
import { existsSync, readdirSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createRouter } from "./export-routing.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..", "out");
if (!existsSync(root)) {
  console.error("verify:export: out/ does not exist. Run `pnpm build` first.");
  process.exit(1);
}

function listFiles(rel = "") {
  const out = [];
  for (const name of readdirSync(join(root, rel))) {
    const r = rel ? `${rel}/${name}` : name;
    if (statSync(join(root, r)).isDirectory()) out.push(...listFiles(r));
    else out.push(r);
  }
  return out;
}

const router = createRouter(listFiles());

// [request path, expected file]
const expectations = [
  ["/", "index.html"],
  ["/login", "login.html"],
  ["/setup", "setup.html"],
  ["/dashboard", "dashboard.html"],
  ["/design", "design.html"],
  ["/projects", "projects.html"],
  ["/applications", "applications.html"],
  ["/services", "services.html"],
  ["/servers", "servers.html"],
  ["/deployments", "deployments.html"],
  ["/domains", "domains.html"],
  ["/registries", "registries.html"],
  ["/secrets", "secrets.html"],
  ["/monitoring", "monitoring.html"],
  ["/settings", "settings.html"],
  ["/projects/_", "projects/_.html"],
  ["/projects/demo-project", "projects/_.html"],
  ["/projects/0192f3c8-aaaa-bbbb-cccc-111122223333", "projects/_.html"],
];

let failed = 0;
for (const [path, file] of expectations) {
  const r = router.resolve(path);
  const ok = r.kind === "file" && r.file === file && existsSync(join(root, file));
  if (!ok) failed++;
  console.log(`${ok ? "ok  " : "FAIL"} ${path.padEnd(52)} -> ${r.file ?? r.kind}`);
}

for (const f of ["404.html"]) {
  const ok = existsSync(join(root, f));
  if (!ok) failed++;
  console.log(`${ok ? "ok  " : "FAIL"} ${f} present`);
}

const r404 = router.resolve("/nope/nothing");
if (r404.kind !== "not_found") {
  failed++;
  console.log("FAIL unknown route should be not_found");
}

if (failed) {
  console.error(`verify:export: ${failed} check(s) failed`);
  process.exit(1);
}
console.log("verify:export: all checks passed");
