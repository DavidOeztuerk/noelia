/**
 * Walks every dashboard in the running stack and prints what is not Pass.
 *
 * Written because reading the stack with curl and grep missed two things a
 * person found by opening the page: a failing data-protection check, and a
 * service that registered no revocation store at all. The dashboard is where
 * Noelia says what it is doing; a check of the stack that does not read it is
 * not a check of the stack.
 *
 *   cd src/frontend
 *   NOELIA_DASHBOARD_OPERATOR_SECRET=… node eng/audit-dashboards.mjs
 */
import { chromium } from "@playwright/test";

const secret = process.env.NOELIA_DASHBOARD_OPERATOR_SECRET;
const browser = await chromium.launch();
const context = await browser.newContext({ ignoreHTTPSErrors: true });
await context.setExtraHTTPHeaders({ "X-Noelia-Operator": secret });

const targets = [];
for (const stage of ["dev", "staging", "prod"]) {
  const tls = stage !== "dev";
  const base = tls ? "https" : "http";
  const port = tls ? 8443 : 8080;
  for (const svc of ["gateway", "user", "todo", "mono"]) {
    targets.push([`${svc}-${stage}`, `${base}://${svc}-${stage}.localhost:${port}/noelia`]);
  }
}

for (const [name, url] of targets) {
  const page = await context.newPage();
  const noise = [];
  page.on("console", m => { if (m.type() === "error" || m.type() === "warning") noise.push(m.text()); });
  page.on("pageerror", e => noise.push("pageerror: " + e.message));

  try {
    const response = await page.goto(url, { waitUntil: "networkidle" });
    if (response.status() !== 200) { console.log(`\n### ${name}  -> ${response.status()}`); await page.close(); continue; }

    const data = await page.evaluate(() => {
      const s = {};
      for (const h of document.querySelectorAll("h2")) {
        const p = []; let n = h.nextElementSibling;
        while (n && n.tagName !== "H2") { p.push(n.innerText); n = n.nextElementSibling; }
        s[h.innerText] = p.join("\n").trim();
      }
      const lines = (s["Security checks"] || "").split("\n");
      return {
        notPass: lines.filter(l => /Fail ·|Warning ·/.test(l)).map(l => l.split("\t").slice(0,3).join(" | ")),
        health: (s["Health"] || "").split("\n").filter(l => l && !l.startsWith("Check")).join(" ; "),
        noContract: [...document.querySelectorAll("p.warning")].length,
        modules: (s["Composition"] || "").split("\n").filter(l => /\texcluded|\tnot composed/.test(l)).length
      };
    });

    console.log(`\n### ${name}`);
    console.log("  health:", data.health);
    for (const f of data.notPass) console.log("  ", f);
    if (data.noContract) console.log("  !! Module ohne Vertrag:", data.noContract);
    if (noise.length) console.log("  !! Konsole:", [...new Set(noise)].join(" | "));
  } catch (e) {
    // A refused connection is the right answer for a service that composes no
    // dashboard: outside Development the gateway holds no key and could not
    // recognise an operator, so there is no address for one.
    const refused = /ERR_HTTP2_PROTOCOL_ERROR|ECONNRESET|ERR_CONNECTION_CLOSED|ERR_EMPTY_RESPONSE/
      .test(e.message);

    console.log(`\n### ${name}  -> ${refused ? "no dashboard here, by design" : "ERROR " + e.message.slice(0, 80)}`);
  } finally { await page.close(); }
}

await browser.close();
