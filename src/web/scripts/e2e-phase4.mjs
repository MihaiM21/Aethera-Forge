#!/usr/bin/env node
// Phase 4 end-to-end run in a real browser against the real stack (API serving the static export, PostgreSQL, Redis,
// a real agent with Docker-in-Docker). Driven by `E2E_PHASES=ui bash deploy/agent-e2e.sh`; can also be run by hand:
//
//   node scripts/e2e-phase4.mjs --base http://127.0.0.1:15411 --email owner@e2e.example.com --password '...' \
//        --server e2e-server --out screenshots/phase4
//
// It walks: sign in -> project -> Create Application wizard (image, env, server, domain) -> deployment detail (pipeline, live
// logs) -> application tabs -> redeploy -> rollback picker -> a failing deployment (failed step highlighted) -> a Redis service from
// a template with its generated credentials -> dashboard, domains, secrets, settings (audit log). Every page is captured in dark
// and light into --out. Exits non-zero on the first failed expectation.
import { existsSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { join, resolve } from "node:path";
import { chromium } from "playwright-core";

const args = Object.fromEntries(
  process.argv
    .slice(2)
    .reduce((acc, v, i, all) => (v.startsWith("--") ? [...acc, [v.slice(2), all[i + 1]]] : acc), []),
);
const BASE = args.base ?? "http://127.0.0.1:5000";
const EMAIL = args.email ?? "owner@e2e.example.com";
const PASSWORD = args.password ?? "correct horse battery staple";
const SERVER = args.server ?? "e2e-server";
const OUT = resolve(args.out ?? "screenshots/phase4");
const HOST = args.host ?? "ui.localtest.me";
// Fresh screenshots only: a leftover of an earlier (possibly failed) run would be mistaken for this one.
rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

function findChromium() {
  if (process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE) return process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE;
  const candidates = [
    "C:/Program Files/Google/Chrome/Application/chrome.exe",
    "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
    "/usr/bin/google-chrome",
    "/usr/bin/chromium",
  ];
  for (const c of candidates) if (existsSync(c)) return c;
  const base = process.env.PLAYWRIGHT_BROWSERS_PATH ?? "/opt/pw-browsers";
  if (existsSync(base)) {
    for (const dir of readdirSync(base).filter((d) => d.startsWith("chromium-")).sort().reverse()) {
      const exe = join(base, dir, "chrome-linux", "chrome");
      if (existsSync(exe)) return exe;
    }
  }
  return undefined;
}

let step = 0;
const ok = (msg) => console.log(`[ ok ] ${msg}`);
function fail(msg) {
  console.error(`[fail] ${msg}`);
  process.exitCode = 1;
  throw new Error(msg);
}
async function expectVisible(locator, what, timeout = 20_000) {
  try {
    await locator.first().waitFor({ state: "visible", timeout });
  } catch {
    fail(`${what}: not visible within ${timeout / 1000}s`);
  }
}
async function selectContaining(locator, text, what) {
  const value = await locator.evaluate((el, t) => [...el.options].find((o) => o.textContent.includes(t))?.value, text);
  if (!value) fail(`${what}: no option contains "${text}"`);
  await locator.selectOption(value);
}
const slug = (s) => s.toLowerCase().replace(/[^a-z0-9]+/g, "-");

const browser = await chromium.launch({ executablePath: findChromium(), args: ["--no-sandbox"] });

async function newContext(theme) {
  const context = await browser.newContext({ viewport: { width: 1360, height: 900 }, colorScheme: theme, baseURL: BASE });
  await context.addInitScript(
    ([t]) => {
      try {
        localStorage.setItem("aethera-theme", t);
        sessionStorage.setItem("aethera-booted", "1");
      } catch {}
    },
    [theme],
  );
  return context;
}

async function login(page) {
  await page.goto("/login");
  await page.getByLabel(/email/i).fill(EMAIL);
  await page.getByLabel(/password/i).fill(PASSWORD);
  await page.getByRole("button", { name: /sign in|log in/i }).click();
  await page.waitForURL(/\/dashboard/, { timeout: 30_000 });
}

async function shot(page, name, theme) {
  await page.waitForTimeout(400); // let polled data and transitions settle
  const file = join(OUT, `${String(++step).padStart(2, "0")}-${name}-${theme}.png`);
  await page.screenshot({ path: file, fullPage: false });
  return file;
}

/** Text of the first status pill of the page (the deployment / workload status). */
const pill = (page) => page.locator('[data-slot="status-pill"]').first();
async function waitForPill(page, pattern, what, timeout = 240_000) {
  const end = Date.now() + timeout;
  let last = "";
  while (Date.now() < end) {
    last = ((await pill(page).textContent({ timeout: 2000 }).catch(() => "")) ?? "").trim();
    if (pattern.test(last)) return last;
    await page.waitForTimeout(1500);
  }
  fail(`${what}: status stayed "${last}" (waited for ${pattern})`);
}

const dark = await newContext("dark");
const page = await dark.newPage();
const consoleErrors = [];
page.on("pageerror", (e) => consoleErrors.push(String(e)));
page.on("console", (m) => {
  if (m.type() === "error" && !/favicon|Failed to load resource/.test(m.text())) consoleErrors.push(m.text());
});

await login(page);
ok("signed in through the login form");
await shot(page, "dashboard-empty", "dark");

// ---------------------------------------------------------------------------------------------------------- project
await page.goto("/projects");
await expectVisible(page.getByRole("heading", { name: "Projects" }), "projects page");
await shot(page, "projects-empty", "dark");
await page.getByRole("button", { name: "New project" }).first().click();
await page.getByLabel("Name", { exact: true }).fill("Demo");
await page.getByRole("button", { name: "Create project" }).click();
await page.waitForURL(/\/projects\/[0-9a-f-]{36}/, { timeout: 20_000 });
await expectVisible(page.getByRole("heading", { name: "Demo" }), "project page");
await expectVisible(page.getByRole("button", { name: /Production/ }), "production environment of the new project");
ok("project created, its environment is listed");
await shot(page, "project", "dark");

// --------------------------------------------------------------------------------------- Create Application wizard
await page.getByRole("link", { name: /Add application/ }).click();
await page.waitForURL(/\/applications\/new/);
await expectVisible(page.getByRole("heading", { name: "Create application" }), "wizard");
await page.getByLabel("Project", { exact: true }).selectOption({ label: "Demo" });
await page.getByRole("radio", { name: /Docker image/ }).check();
await page.getByLabel("Image", { exact: true }).fill("nginx");
await page.getByLabel("Tag", { exact: true }).fill("alpine");
await page.getByLabel("Application name").fill("Web");
await shot(page, "wizard-source", "dark");
await page.getByRole("button", { name: "Continue" }).click();
await expectVisible(page.getByText(/needs no build/), "build step for an image");
await page.getByRole("button", { name: "Continue" }).click();
await page.getByRole("button", { name: "Add variable" }).click();
await page.getByLabel("Variable name").fill("GREETING");
await page.getByLabel("Variable value").fill("hello from aethera");
await shot(page, "wizard-env", "dark");
await page.getByRole("button", { name: "Continue" }).click();
await selectContaining(page.getByLabel("Server", { exact: true }), SERVER, "server select");
await page.getByLabel("Container port").fill("80");
await page.getByLabel("Health check path").fill("/");
await shot(page, "wizard-server", "dark");
await page.getByRole("button", { name: "Continue" }).click();
await page.getByLabel("Domain", { exact: true }).fill(HOST);
await page.getByRole("switch", { name: "HTTPS" }).click(); // plain http: no certificate can be issued for a test host
await shot(page, "wizard-domain", "dark");
await page.getByRole("button", { name: "Continue" }).click();
await expectVisible(page.getByText(`http://${HOST}`), "review shows the domain");
await shot(page, "wizard-review", "dark");
await page.getByRole("button", { name: "Create and deploy" }).click();
await page.waitForURL(/\/deployments\/[0-9a-f-]{36}/, { timeout: 60_000 });
ok("wizard created the application and queued the first deployment");

// ---------------------------------------------------------------------------------------- deployment detail (live)
await expectVisible(page.getByRole("list", { name: "Deployment pipeline" }), "pipeline stepper");
await shot(page, "deployment-live", "dark");
const final = await waitForPill(page, /Running|Failed/, "first deployment");
if (!/Running/.test(final)) {
  await shot(page, "deployment-unexpected-failure", "dark");
  fail(`the first deployment ended as "${final}": ${await page.locator("[role=alert]").first().textContent().catch(() => "")}`);
}
ok("first deployment reached Running");
const firstDeploymentUrl = page.url();
await expectVisible(page.getByRole("log", { name: /deploy log/i }), "deploy log");
await page.waitForFunction(() => /Deployment #1 is running/.test(document.querySelector('[role="log"]')?.textContent ?? ""), null, { timeout: 30_000 }).catch(() => fail("the deploy log does not narrate the result"));
ok("live pipeline log shows the deployment narration");
await shot(page, "deployment-running", "dark");
const stepsText = await page.getByRole("list", { name: "Deployment pipeline" }).textContent();
for (const s of ["Source", "Image", "Container", "Health check", "Running"]) if (!stepsText?.includes(s)) fail(`pipeline lacks the ${s} step`);
ok("pipeline stepper lists the nine steps");

// ---------------------------------------------------------------------------------------------- application tabs
await page.getByRole("link", { name: /^Web$/ }).first().click().catch(async () => {
  await page.getByRole("link", { name: /Application/ }).first().click();
});
await page.waitForURL(/\/applications\/[0-9a-f-]{36}/);
await expectVisible(page.getByRole("heading", { name: /Web/ }), "application page");
await waitForPill(page, /Running/, "application status");
const appUrl = page.url().split("#")[0];
await shot(page, "app-overview", "dark");
for (const [tab, check] of [
  ["Deployments", /#1/],
  ["Logs", /./],
  ["Environment", /GREETING/],
  ["Domains", new RegExp(HOST.replace(/\./g, "\\."))],
  ["Storage", /No volumes/],
  ["Networking", /Ports/],
  ["Resources", /Limits/],
  ["Build", /Image/],
  ["Health", /Health check/],
  ["Settings", /General/],
]) {
  await page.getByRole("tab", { name: tab, exact: true }).click();
  await expectVisible(page.getByText(check).first(), `${tab} tab`);
  if (["Deployments", "Logs", "Environment", "Domains", "Settings"].includes(tab)) await shot(page, `app-${slug(tab)}`, "dark");
}
ok("all eleven application tabs render with data");

// runtime logs: nginx has served the health probe by now
await page.getByRole("tab", { name: "Logs", exact: true }).click();
await page.waitForFunction(() => (document.querySelector('[role="log"]')?.textContent ?? "").trim().length > 20, null, { timeout: 40_000 }).catch(() => fail("no runtime log lines from the container"));
ok("live application logs show container output");

// -------------------------------------------------------------------------------------------- redeploy + rollback
await page.getByRole("button", { name: "Deploy", exact: true }).click();
await page.waitForURL(/\/deployments\/[0-9a-f-]{36}/, { timeout: 30_000 });
await waitForPill(page, /Running/, "second deployment");
ok("second deployment reached Running (the first became a rollback point)");
const beforeRollback = page.url();
await page.getByRole("button", { name: /Roll back/ }).click();
await expectVisible(page.getByRole("dialog").getByRole("radio"), "rollback picker lists a rollback point");
await shot(page, "rollback-picker", "dark");
await page.getByRole("dialog").getByRole("button", { name: "Roll back" }).click();
await page.waitForURL((u) => /\/deployments\/[0-9a-f-]{36}/.test(u.pathname) && u.toString() !== beforeRollback, { timeout: 30_000 });
await waitForPill(page, /Running/, "rollback deployment");
await expectVisible(page.getByText("Rollback", { exact: false }), "rollback deployment is marked");
ok("rolled back through the picker: a new deployment re-applied the old one");
await shot(page, "deployment-rollback", "dark");

// ---------------------------------------------------------------------------------- a failing deployment is explained
await page.goto("/applications/new");
await page.getByLabel("Project", { exact: true }).selectOption({ label: "Demo" });
await page.getByRole("radio", { name: /Docker image/ }).check();
await page.getByLabel("Image", { exact: true }).fill("nginx");
await page.getByLabel("Tag", { exact: true }).fill("no-such-tag-aethera");
await page.getByLabel("Application name").fill("Broken");
for (let i = 0; i < 3; i++) await page.getByRole("button", { name: "Continue" }).click();
await selectContaining(page.getByLabel("Server", { exact: true }), SERVER, "server select");
await page.getByLabel("Container port").fill("80");
for (let i = 0; i < 2; i++) await page.getByRole("button", { name: "Continue" }).click();
await page.getByRole("button", { name: "Create and deploy" }).click();
await page.waitForURL(/\/deployments\/[0-9a-f-]{36}/, { timeout: 60_000 });
await waitForPill(page, /Failed/, "the broken deployment", 180_000);
await expectVisible(page.getByRole("alert").filter({ hasText: /Failed at/ }), "failure banner names the failed step");
await expectVisible(page.getByRole("group", { name: /failure details/ }), "the failed step carries its details");
const banner = await page.getByRole("alert").filter({ hasText: /Failed at/ }).first().textContent();
ok(`failure is explained: ${banner?.replace(/\s+/g, " ").trim().slice(0, 120)}`);
await shot(page, "deployment-failed", "dark");

// ------------------------------------------------------------------------------------------- service from a template
await page.goto("/services/new");
await page.getByRole("button", { name: /Redis/ }).first().click();
await expectVisible(page.getByText(/Credentials are generated/), "template summary mentions generated credentials");
await page.getByLabel("Project", { exact: true }).selectOption({ label: "Demo" });
await shot(page, "service-new", "dark");
await page.getByRole("button", { name: "Create and deploy" }).click();
await page.waitForURL(/\/deployments\/[0-9a-f-]{36}/, { timeout: 60_000 });
await waitForPill(page, /Running/, "the Redis service deployment");
ok("Redis service deployed from its template");
await page.goto("/services");
await expectVisible(page.getByRole("link", { name: "Redis" }), "services list");
await page.getByRole("link", { name: "Redis" }).first().click();
await page.waitForURL(/\/services\/[0-9a-f-]{36}/);
await waitForPill(page, /Running/, "service status");
await expectVisible(page.getByText("redis:6379"), "connection info");
await shot(page, "service-overview", "dark");
await page.getByRole("tab", { name: "Environment", exact: true }).click();
await expectVisible(page.getByText("REDIS_PASSWORD"), "generated credential is listed");
await page.getByRole("button", { name: "Reveal REDIS_PASSWORD" }).click();
await page.getByRole("button", { name: /Reveal the current value/ }).click();
await expectVisible(page.getByRole("dialog").locator("code"), "revealed secret value");
const revealed = await page.getByRole("dialog").locator("code").textContent();
if (!revealed || revealed.length < 16) fail("the revealed generated password looks wrong");
ok("generated credential is a secret: masked in the list, revealed on demand by an administrator");
await page.keyboard.press("Escape");

// ----------------------------------------------------------------------------------------------------- ops pages
await page.goto("/domains");
await expectVisible(page.getByText(HOST), "domain listed");
await shot(page, "domains", "dark");
await page.goto("/secrets");
await expectVisible(page.getByText("REDIS_PASSWORD"), "secrets list");
await shot(page, "secrets", "dark");
await page.goto("/registries");
await expectVisible(page.getByText("No registries yet"), "registries empty state");
await page.getByRole("button", { name: "Add registry" }).first().click();
await page.getByLabel("Name", { exact: true }).fill("GHCR");
await page.getByLabel("URL", { exact: true }).fill("ghcr.io");
await page.getByRole("button", { name: "Add registry" }).last().click();
await expectVisible(page.getByText("ghcr.io"), "registry created");
await shot(page, "registries", "dark");
await page.goto("/settings#audit");
await expectVisible(page.getByRole("button", { name: /application\.deploy_requested/ }).first(), "audit log has the deployments");
await expectVisible(page.getByRole("button", { name: /secret\.revealed/ }).first(), "audit log recorded the reveal");
ok("audit log shows deploys and the secret reveal");
await shot(page, "settings-audit", "dark");
await page.goto("/settings#tokens");
await page.getByRole("button", { name: "New token" }).click();
await page.getByLabel("Name", { exact: true }).fill("ci");
await page.getByRole("button", { name: "Create token" }).click();
await expectVisible(page.getByText("Token created"), "token created");
const token = await page.getByRole("dialog").locator("code").textContent();
if (!token?.startsWith("aeth_")) fail(`unexpected token format: ${token?.slice(0, 8)}`);
await page.getByRole("button", { name: "Done" }).click();
ok("API token created (shown once)");
await page.goto("/deployments");
await expectVisible(page.getByRole("link", { name: "#1" }).first(), "deployments list");
await shot(page, "deployments", "dark");
await page.goto("/dashboard");
await expectVisible(page.getByText("Recent deployments"), "dashboard");
await page.waitForFunction(() => /running/i.test(document.body.innerText) && /Broken/.test(document.body.innerText), null, { timeout: 30_000 });
await shot(page, "dashboard", "dark");
await page.waitForFunction(() => /1 server/.test(document.querySelector('[data-slot="status-bar"]')?.textContent ?? ""), null, { timeout: 30_000 }).catch(() => fail("the status bar does not show the real server count"));
ok("dashboard shows live servers, applications, deployments and the failure; the status bar counts the server");

// ----------------------------------------------------------------------------------------------- light theme + mobile
const light = await newContext("light");
const lp = await light.newPage();
await login(lp);
const toVisit = [
  ["dashboard", "/dashboard"],
  ["deployments", "/deployments"],
  ["applications", "/applications"],
  ["app-overview", appUrl.replace(BASE, "")],
  ["services", "/services"],
  ["settings-audit", "/settings#audit"],
];
for (const [name, path] of toVisit) {
  await lp.goto(path);
  await lp.waitForLoadState("networkidle").catch(() => {});
  await shot(lp, name, "light");
}
await lp.goto(firstDeploymentUrl.replace(BASE, ""));
await lp.waitForLoadState("networkidle").catch(() => {});
await shot(lp, "deployment-detail", "light");
ok("light theme captured");

const mobile = await browser.newContext({ viewport: { width: 390, height: 844 }, colorScheme: "dark", baseURL: BASE });
await mobile.addInitScript(() => {
  try {
    localStorage.setItem("aethera-theme", "dark");
    sessionStorage.setItem("aethera-booted", "1");
  } catch {}
});
const mp = await mobile.newPage();
await login(mp);
await mp.goto("/applications/new");
await mp.waitForLoadState("networkidle").catch(() => {});
await shot(mp, "wizard-mobile", "dark");
const overflow = await mp.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1);
if (overflow) fail("the wizard overflows horizontally at 390px");
ok("wizard fits a phone screen");

if (consoleErrors.length > 0) fail(`browser console errors:\n  ${consoleErrors.slice(0, 5).join("\n  ")}`);
ok("no console errors in the whole run");
await browser.close();
console.log(`\n[ ok ] phase 4 end-to-end run passed; ${step} screenshots in ${OUT}`);
