#!/usr/bin/env node
// Generates src/lib/api/schema.d.ts from the OpenAPI document that the .NET
// build writes to openapi/aethera.v1.json (ADR 0003 section 9).
// Exits 0 with a message when the document does not exist yet, so it is safe
// to run before the API has produced it.
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const input = resolve(root, "openapi/aethera.v1.json");
const output = resolve(root, "src/lib/api/schema.d.ts");

if (!existsSync(input)) {
  console.log(
    "gen:api: openapi/aethera.v1.json does not exist yet; nothing to generate.\n" +
      "         The .NET build writes it (see docs/architecture/0003-api-conventions.md section 9).\n" +
      "         Skipping.",
  );
  process.exit(0);
}

const bin = resolve(root, "node_modules/.bin/openapi-typescript");
const result = spawnSync(bin, ["openapi/aethera.v1.json", "-o", "src/lib/api/schema.d.ts"], {
  cwd: root,
  stdio: "inherit",
});
if (result.error) {
  console.error(`gen:api: could not run openapi-typescript (${result.error.message}). Run pnpm install first.`);
  process.exit(1);
}
if (result.status === 0) console.log(`gen:api: wrote ${output}`);
process.exit(result.status ?? 1);
