import { defineConfig, devices } from "@playwright/test";

/**
 * Runs against a stack that is already up. Which one is a choice:
 *
 *   docker compose --profile dev up -d --wait
 *   npm run e2e
 *
 *   docker compose --profile prod up -d --wait
 *   DEMO_BASE_URL=https://mono-prod.localhost:8443 \
 *   DEMO_OPERATOR_SECRET="$NOELIA_DASHBOARD_OPERATOR_SECRET" npm run e2e
 *
 * The staged hosts serve a certificate this machine generated for names that
 * resolve nowhere else, so the browser is told to accept it. That is a decision
 * about a demo certificate and not a habit: `ignoreHTTPSErrors` is the reason
 * the TLS half of these tests can run at all, and it would be wrong anywhere a
 * real certificate exists.
 */
const baseURL = process.env.DEMO_BASE_URL ?? "http://mono-dev.localhost:8080";

export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
  reporter: process.env.CI ? "list" : "line",
  use: {
    baseURL,
    ignoreHTTPSErrors: true,
    trace: "retain-on-failure"
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }]
});
