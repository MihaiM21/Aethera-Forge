import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";

// Parses the ported tokens in globals.css and asserts the WCAG pairs promised
// in docs/design/tokens.md section 2.5. Re-run whenever a colour token changes.
const css = readFileSync(path.resolve(import.meta.dirname, "globals.css"), "utf8").replace(/\r\n/g, "\n");

function block(selectorStart: string): Record<string, string> {
  const start = css.indexOf(selectorStart);
  if (start < 0) throw new Error(`missing ${selectorStart}`);
  const open = css.indexOf("{", start);
  const close = css.indexOf("\n}", open);
  const vars: Record<string, string> = {};
  for (const m of css.slice(open, close).matchAll(/(--[\w-]+):\s*([^;]+);/g)) vars[m[1]] = m[2].trim();
  return vars;
}

function resolver(vars: Record<string, string>) {
  const get = (name: string, depth = 0): string => {
    const v = vars[name];
    if (v === undefined) throw new Error(`undefined ${name}`);
    const ref = v.match(/^var\((--[\w-]+)\)$/);
    if (ref && depth < 10) return get(ref[1], depth + 1);
    return v;
  };
  return get;
}

function luminance(hex: string): number {
  const n = parseInt(hex.slice(1), 16);
  const ch = [(n >> 16) & 255, (n >> 8) & 255, n & 255].map((c) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * ch[0] + 0.7152 * ch[1] + 0.0722 * ch[2];
}
function ratio(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const themes = {
  dark: resolver(block(":root,\n[data-theme=\"dark\"]")),
  light: resolver(block("[data-theme=\"light\"] {")),
};

// [foreground token, background token, min ratio]
const PAIRS: Array<[string, string, number]> = [
  ["--foreground", "--background", 4.5],
  ["--foreground", "--card", 4.5],
  ["--foreground", "--popover", 4.5],
  ["--muted-foreground", "--background", 4.5],
  ["--muted-foreground", "--card", 4.5],
  ["--muted-foreground", "--popover", 4.5],
  ["--ae-accent-text", "--background", 4.5],
  ["--ae-accent-text", "--card", 4.5],
  ["--primary-foreground", "--primary", 4.5],
  ["--primary-foreground", "--primary-hover", 4.5],
  ["--success", "--background", 4.5],
  ["--warning", "--background", 4.5],
  ["--danger", "--background", 4.5],
  ["--info", "--background", 4.5],
  ["--destructive-foreground", "--destructive", 4.5],
  ["--input", "--background", 3],
  ["--input", "--card", 3],
  ["--ring", "--background", 3],
];

describe.each(Object.entries(themes))("%s theme contrast", (_name, get) => {
  it.each(PAIRS)("%s on %s >= %s:1", (fg, bg, min) => {
    expect(ratio(get(fg), get(bg))).toBeGreaterThanOrEqual(min);
  });
});

describe("token drift", () => {
  it("keeps the brand lime values from the design system", () => {
    expect(themes.dark("--primary")).toBe("#b8f26b");
    expect(themes.dark("--background")).toBe("#0b0d0d");
    expect(themes.light("--ae-accent-text")).toBe("#3b6a07");
  });
});
