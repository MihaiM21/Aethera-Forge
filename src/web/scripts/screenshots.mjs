#!/usr/bin/env node
// Captures review screenshots of the static export with Playwright + the
// preinstalled Chromium (no browser download). Needs a PREVIEW build so
// authenticated pages render without an API:
//
//   pnpm build:preview && pnpm screenshots [outDir]
//
// The API is mocked with page.route; nothing here touches a real backend.
import { existsSync, mkdirSync, readdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright-core";
import { startServer } from "./serve-export.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const outDir = resolve(process.argv[2] ?? join(here, "..", "screenshots"));
mkdirSync(outDir, { recursive: true });

function findChromium() {
  if (process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE) return process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE;
  const base = process.env.PLAYWRIGHT_BROWSERS_PATH ?? "/opt/pw-browsers";
  if (!existsSync(base)) return undefined;
  for (const dir of readdirSync(base).filter((d) => d.startsWith("chromium-")).sort().reverse()) {
    const exe = join(base, dir, "chrome-linux", "chrome");
    if (existsSync(exe)) return exe;
  }
  return undefined;
}

const { server, port } = await startServer({ port: 0 });
const base = `http://localhost:${port}`;
const browser = await chromium.launch({ executablePath: findChromium(), args: ["--no-sandbox"] });

const problem = (status, code, title) => ({
  status,
  contentType: "application/problem+json",
  body: JSON.stringify({ type: `urn:aethera:problem:${code}`, title, status, code }),
});

async function newPage(theme, { width = 1280, height = 800, setupRequired = false, booted = true, reduceMotion = false } = {}) {
  const context = await browser.newContext({
    viewport: { width, height },
    deviceScaleFactor: 1,
    reducedMotion: reduceMotion ? "reduce" : "no-preference",
    colorScheme: theme,
  });
  await context.addInitScript(
    ([t, b]) => {
      try {
        localStorage.setItem("aethera-theme", t);
        if (b) sessionStorage.setItem("aethera-booted", "1");
      } catch {}
    },
    [theme, booted],
  );
  const page = await context.newPage();
  await page.route("**/api/v1/**", (route) => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith("/auth/setup") && route.request().method() === "GET") {
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ setupRequired }) });
    }
    if (url.pathname.endsWith("/auth/csrf")) {
      return route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ token: "preview" }) });
    }
    if (url.pathname.endsWith("/auth/me")) {
      return route.fulfill(problem(401, "auth.unauthenticated", "Unauthenticated"));
    }
    return route.fulfill(problem(404, "request.not_found", "Not found"));
  });
  return { context, page };
}

async function shot(page, name) {
  const file = join(outDir, `${name}.png`);
  await page.screenshot({ path: file });
  console.log("wrote", file);
}

for (const theme of ["dark", "light"]) {
  // Login (boot already played this session -> form).
  {
    const { context, page } = await newPage(theme);
    await page.goto(`${base}/login`, { waitUntil: "networkidle" });
    await page.getByRole("button", { name: "Log in" }).waitFor();
    await shot(page, `login-${theme}`);
    await context.close();
  }

  // Setup (wait for the BootLines intro, then the form).
  {
    const { context, page } = await newPage(theme, { setupRequired: true });
    await page.goto(`${base}/setup`, { waitUntil: "networkidle" });
    await page.getByRole("button", { name: "Create owner account" }).waitFor({ timeout: 8000 });
    await shot(page, `setup-${theme}`);
    await context.close();
  }

  // Design gallery: current theme, full page.
  {
    const { context, page } = await newPage(theme, { width: 1280, height: 900 });
    await page.goto(`${base}/design`, { waitUntil: "networkidle" });
    await page.getByRole("button", { name: "Current theme" }).click();
    await page.waitForTimeout(400);
    await page.screenshot({ path: join(outDir, `design-${theme}.png`), fullPage: true });
    console.log("wrote", join(outDir, `design-${theme}.png`));
    await context.close();
  }

  // Dashboard in the app shell (preview mode).
  {
    const { context, page } = await newPage(theme, { height: 800 });
    await page.goto(`${base}/dashboard?preview=1`, { waitUntil: "networkidle" });
    await page.getByRole("heading", { name: "Dashboard" }).waitFor();
    await page.waitForTimeout(300);
    await shot(page, `dashboard-${theme}`);

    // Command palette over the shell.
    await page.keyboard.press("Control+k");
    await page.getByRole("dialog").waitFor();
    await page.waitForTimeout(300);
    await shot(page, `palette-${theme}`);
    await context.close();
  }
}

// Boot screen mid-animation (dark only) and the phone layout with the sheet open.
{
  const { context, page } = await newPage("dark", { booted: false });
  await page.goto(`${base}/login`, { waitUntil: "domcontentloaded" });
  await page.getByText("verifying runtime boundaries").waitFor();
  await page.waitForTimeout(500);
  await shot(page, "login-boot-dark");
  await context.close();
}
{
  const { context, page } = await newPage("dark", { width: 390, height: 780 });
  await page.goto(`${base}/dashboard?preview=1`, { waitUntil: "networkidle" });
  await page.getByRole("heading", { name: "Dashboard" }).waitFor();
  await shot(page, "dashboard-mobile-dark");
  await page.getByRole("button", { name: "Open navigation" }).click();
  await page.getByRole("dialog").waitFor();
  await page.waitForTimeout(400);
  await shot(page, "dashboard-mobile-nav-dark");
  await context.close();
}

await browser.close();
server.close();
