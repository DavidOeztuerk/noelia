import { test, expect } from "@playwright/test";

/**
 * The stage hint, in a real browser.
 *
 * test/stage-hint.test.js proves the function against a jsdom and the markup
 * against a regex. Neither proves that the page module actually calls it, that
 * the CSP lets the module load, or that `hidden` survives the stylesheet. That
 * is what this asserts, on each page that carries the slot: visible with the
 * in-memory warning from Staging on, absent in Development.
 *
 * The stage is read from the host the run was pointed at (DEMO_BASE_URL), the
 * same way the page reads it — so a run against mono-staging.localhost that
 * found the hint hidden is a real defect, not a mis-configured test.
 */

const stage = (baseURL) => new URL(baseURL).hostname.split(".")[0].split("-")[1] ?? "dev";
const shows = (baseURL) => ["staging", "prod"].includes(stage(baseURL));

const email = () => `hint.${Date.now()}.${Math.random().toString(36).slice(2, 8)}@example.com`;

for (const path of ["/login", "/register"]) {
  test(`the stage hint on ${path} matches the stage of the host`, async ({ page, baseURL }) => {
    await page.goto(path);
    const hint = page.locator("[data-stage-hint]");

    if (shows(baseURL)) {
      await expect(hint).toBeVisible();
      await expect(hint).toContainText("Arbeitsspeicher");
      await expect(hint).toContainText("neu startet");
    } else {
      await expect(hint).toBeHidden();
    }
  });
}

test("the stage hint on the task page matches the stage of the host", async ({ page, baseURL }) => {
  await page.goto("/register");
  await page.getByRole("textbox", { name: "Anzeigename" }).fill("Ada Lovelace");
  await page.getByRole("textbox", { name: "E-Mail" }).fill(email());
  await page.getByRole("textbox", { name: "Passwort" }).fill("a-long-enough-password");
  await page.getByRole("button", { name: "Registrieren" }).click();
  await expect(page.getByRole("heading", { name: "Meine Aufgaben" })).toBeVisible();

  const hint = page.locator("[data-stage-hint]");
  if (shows(baseURL)) {
    await expect(hint).toBeVisible();
    await expect(hint).toContainText("Arbeitsspeicher");
  } else {
    await expect(hint).toBeHidden();
  }
});
