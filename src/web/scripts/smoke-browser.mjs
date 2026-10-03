#!/usr/bin/env node
// Browser part of deploy/smoke.sh: logs in through /login with headless Chromium, waits for /dashboard
// and checks that the status bar says "Connected" (i.e. the SignalR hub connection works through the
// API's own origin check), then saves a screenshot.
//
//   node scripts/smoke-browser.mjs <baseUrl> <email> <password> [screenshotPath]
//
// Uses playwright-core with the Chromium that is already installed under /opt/pw-browsers
// (PLAYWRIGHT_BROWSERS_PATH); it never downloads a browser. Prints "[ ok ] ..." lines, exits 1 on failure.
import { existsSync, mkdirSync, readdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { chromium } from "playwright-core";

const [base, email, password, shot = "screenshots/smoke-dashboard-dark.png"] = process.argv.slice(2);
if (!base || !email || !password) {
  console.error("usage: smoke-browser.mjs <baseUrl> <email> <password> [screenshotPath]");
  process.exit(2);
}

function findChromium() {
  if (process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE) return process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE;
  const dir = process.env.PLAYWRIGHT_BROWSERS_PATH ?? "/opt/pw-browsers";
  if (!existsSync(dir)) return undefined;
  for (const name of readdirSync(dir).filter((d) => d.startsWith("chromium-")).sort().reverse()) {
    const exe = join(dir, name, "chrome-linux", "chrome");
    if (existsSync(exe)) return exe;
  }
  return undefined;
}

const ok = (message) => console.log(`[ ok ] ${message}`);
const problems = [];

const browser = await chromium.launch({ executablePath: findChromium(), args: ["--no-sandbox"] });
let failure = null;
try {
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, colorScheme: "dark" });
  await context.addInitScript(() => {
    try {
      localStorage.setItem("aethera-theme", "dark");
    } catch {}
  });
  const page = await context.newPage();
  page.on("pageerror", (error) => problems.push(`page error: ${error.message}`));
  page.on("console", (message) => {
    // A CSP violation or a refused connection means the security headers broke the app.
    if (message.type() === "error" && /Content Security Policy|Refused to/i.test(message.text())) problems.push(`console: ${message.text()}`);
  });

  await page.goto(`${base}/login`, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "Log in" }).waitFor({ timeout: 15_000 });
  ok("/login renders the sign-in form");

  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Log in" }).click();

  await page.waitForURL("**/dashboard", { timeout: 20_000 });
  await page.getByRole("heading", { name: "Dashboard" }).waitFor({ timeout: 20_000 });
  ok("signed in: the browser reached /dashboard");

  const statusBar = page.locator('[data-slot="status-bar"]');
  await statusBar.waitFor({ timeout: 10_000 });
  await page.waitForFunction(
    () => {
      const text = document.querySelector('[data-slot="status-bar"]')?.textContent ?? "";
      return /\bConnected\b/.test(text) && !/unavailable/i.test(text);
    },
    undefined,
    { timeout: 20_000 },
  );
  ok(`status bar: "${(await statusBar.innerText()).replace(/\s+/g, " ").trim()}"`);

  // Let the boot screen and live regions settle before the picture.
  await page.waitForTimeout(500);
  const file = resolve(shot);
  mkdirSync(dirname(file), { recursive: true });
  await page.screenshot({ path: file });
  ok(`screenshot ${shot}`);

  if (problems.length > 0) throw new Error(problems.join("\n"));
  ok("no page errors or CSP violations");
} catch (error) {
  failure = error;
} finally {
  await browser.close();
}

if (failure) {
  console.error(`[fail] browser smoke: ${failure.message}`);
  process.exit(1);
}
