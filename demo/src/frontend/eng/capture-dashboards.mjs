/**
 * Captures the operator dashboard from every stage of a running stack.
 *
 * The showcase on GitHub Pages cannot run the demo — Pages serves static files
 * and nothing else. What it can do is show what the dashboard actually says in
 * each stage, which is the difference the demo exists to make. These are real
 * screenshots of a real run, taken here rather than drawn, so the page cannot
 * drift away from the thing it describes.
 *
 * It lives beside the frontend package because that is where Playwright is
 * installed; the other scripts in demo/eng need nothing but a shell.
 *
 * Run with the stack up:
 *
 *   docker compose --profile all up -d --wait
 *   cd src/frontend
 *   NOELIA_DASHBOARD_OPERATOR_SECRET=… node eng/capture-dashboards.mjs
 */

import { chromium } from "@playwright/test";
import { mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const output = resolve(here, "../../../../docs/pages/assets");

const secret = process.env.NOELIA_DASHBOARD_OPERATOR_SECRET;
if (!secret) {
  console.error("NOELIA_DASHBOARD_OPERATOR_SECRET is required for the staged hosts.");
  process.exit(2);
}

const shots = [
  {
    name: "dashboard-dev",
    url: "http://mono-dev.localhost:8080/noelia",
    headers: {},
    caption: "Development — open to anyone who can reach the port"
  },
  {
    name: "dashboard-staging",
    url: "https://mono-staging.localhost:8443/noelia",
    headers: { "X-Noelia-Operator": secret },
    caption: "Staging — an operator secret, and state in Valkey"
  },
  {
    name: "dashboard-prod",
    url: "https://mono-prod.localhost:8443/noelia",
    headers: { "X-Noelia-Operator": secret },
    caption: "Production — the same, with the exposure reason recorded"
  },
  {
    name: "dashboard-refused",
    url: "https://mono-prod.localhost:8443/noelia",
    headers: {},
    caption: "Production without the secret — an indistinguishable 404"
  },
  {
    name: "app",
    url: "http://mono-dev.localhost:8080/login",
    headers: {},
    caption: "The application the stack is built around"
  }
];

await mkdir(output, { recursive: true });

// The staged hosts serve a certificate generated for names that resolve only on
// this machine. Accepting it here is a statement about a demo certificate, not
// a habit worth carrying anywhere else.
const browser = await chromium.launch();
const context = await browser.newContext({
  ignoreHTTPSErrors: true,
  viewport: { width: 1280, height: 900 },
  // 1, not 2. A retina capture of a full dashboard is 2.5 MB, and five of
  // them would put 7 MB of screenshots in a repository that ships packages.
  deviceScaleFactor: 1
});

let failures = 0;

for (const shot of shots) {
  const page = await context.newPage();
  try {
    await page.setExtraHTTPHeaders(shot.headers);
    const response = await page.goto(shot.url, { waitUntil: "networkidle" });

    await page.screenshot({
      path: `${output}/${shot.name}.png`,
      fullPage: shot.name.startsWith("dashboard") && !shot.name.endsWith("refused")
    });

    console.log(`${shot.name.padEnd(20)} ${response?.status()}  ${shot.caption}`);
  } catch (error) {
    failures += 1;
    console.error(`${shot.name.padEnd(20)} failed: ${error.message}`);
  } finally {
    await page.close();
  }
}

await browser.close();
process.exit(failures === 0 ? 0 : 1);
