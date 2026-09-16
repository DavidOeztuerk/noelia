import { test, expect } from "@playwright/test";

/**
 * What the unit tests cannot say.
 *
 * The frontend suite covers FormController and TodoList against a jsdom, which
 * is the right place for the logic in them. None of it exercises a real fetch,
 * a real redirect, a real cookie or a real Content-Security-Policy — so the
 * whole stack could be green while the application failed on the first click.
 * That was findings 8 and 9 together: a button that stayed disabled after a
 * failed request, and no test anywhere that would have seen it.
 *
 * Every test here also asserts that nothing reached the console. A page that
 * works while logging an error is a page that is about to stop working.
 */

const credentials = () => ({
  displayName: "Ada Lovelace",
  email: `ada.${Date.now()}.${Math.random().toString(36).slice(2, 8)}@example.com`,
  password: "a-long-enough-password"
});

/**
 * Collects what the application said, not what the network did.
 *
 * Chrome writes "Failed to load resource: … 401" to the console for any
 * non-2xx response, including the ones these tests deliberately provoke — a
 * sign-out is supposed to make the next call 401, and a route stubbed to 500 is
 * the point of that test. Asserting on those would be asserting that the server
 * never refuses anything.
 *
 * What is left is what actually matters: an uncaught exception, a failed
 * module import, a Content-Security-Policy violation, a warning the page
 * emitted itself.
 */
function watchConsole(page) {
  const fromTheNetwork = /^Failed to load resource:/;

  const noise = [];
  page.on("console", (message) => {
    const text = message.text();
    const interesting = message.type() === "error" || message.type() === "warning";

    if (interesting && !fromTheNetwork.test(text)) {
      noise.push(`${message.type()}: ${text}`);
    }
  });
  page.on("pageerror", (error) => noise.push(`pageerror: ${error.message}`));
  return noise;
}

async function register(page, user) {
  await page.goto("/register");
  await page.getByRole("textbox", { name: "Anzeigename" }).fill(user.displayName);
  await page.getByRole("textbox", { name: "E-Mail" }).fill(user.email);
  await page.getByRole("textbox", { name: "Passwort" }).fill(user.password);
  await page.getByRole("button", { name: "Registrieren" }).click();
  await expect(page.getByRole("heading", { name: "Meine Aufgaben" })).toBeVisible();
}

test("a person can register, add a todo and complete it", async ({ page }) => {
  const noise = watchConsole(page);
  const user = credentials();

  await register(page, user);

  await page.getByRole("textbox", { name: "Titel" }).fill("Prove the flow end to end");
  await page.getByRole("button", { name: "Anlegen" }).click();

  const item = page.getByRole("listitem").filter({ hasText: "Prove the flow end to end" });
  await expect(item).toBeVisible();

  await item.getByRole("button", { name: "Erledigen" }).click();
  await expect(item.getByText("Erledigt")).toBeVisible();
  await expect(item.getByRole("button", { name: "Erledigen" })).toHaveCount(0);

  expect(noise).toEqual([]);
});

test("signing out sends the next visit back to the login page", async ({ page }) => {
  const noise = watchConsole(page);
  await register(page, credentials());

  await page.getByRole("button", { name: "Abmelden" }).click();
  await expect(page).toHaveURL(/\/login$/);

  await page.goto("/");
  await expect(page).toHaveURL(/\/login$/);

  expect(noise).toEqual([]);
});

test("the completion button comes back when the request fails", async ({ page }) => {
  const noise = watchConsole(page);
  await register(page, credentials());

  await page.getByRole("textbox", { name: "Titel" }).fill("Fail this one on purpose");
  await page.getByRole("button", { name: "Anlegen" }).click();

  const item = page.getByRole("listitem").filter({ hasText: "Fail this one on purpose" });
  await expect(item).toBeVisible();

  // The server answers 500 exactly once. Page.guard() handles the error and
  // resolves, which is why a `catch` in TodoList never ran and the row died.
  await page.route("**/api/todos/*/complete", (route) =>
    route.fulfill({ status: 500, contentType: "application/json", body: "{}" })
  );

  const button = item.getByRole("button", { name: "Erledigen" });
  await button.click();
  await expect(button).toBeEnabled();

  await page.unroute("**/api/todos/*/complete");
  await button.click();
  await expect(item.getByText("Erledigt")).toBeVisible();

  // The notice about the failure is not console noise, so nothing should have
  // reached the console even though a request failed.
  expect(noise).toEqual([]);
});

test("the refresh cookie is HttpOnly and Strict, and Secure exactly when the transport is", async ({
  page,
  context,
  baseURL
}) => {
  await register(page, credentials());

  const refresh = (await context.cookies()).find((cookie) => cookie.name === "noelia.rt");
  expect(refresh, "the sign-in has to leave a refresh cookie").toBeDefined();

  expect(refresh.httpOnly).toBe(true);
  expect(refresh.sameSite).toBe("Strict");
  expect(refresh.path).toBe("/api/auth");

  // Asserted, not assumed: the flag follows the transport. Over the staged
  // hosts TLS ends at the edge, so this only holds while the application reads
  // the forwarded scheme — which is what Noelia 5.1.0 fixed.
  expect(refresh.secure).toBe(baseURL.startsWith("https://"));
});

test("every static response carries the same four headers", async ({ page }) => {
  const required = [
    "content-security-policy",
    "x-content-type-options",
    "x-frame-options",
    "referrer-policy"
  ];

  for (const path of ["/", "/index.html", "/login", "/js/pages/dashboard.js", "/css/main.css"]) {
    const response = await page.request.get(path);
    expect(response.status(), `${path} should be served`).toBe(200);

    const headers = response.headers();
    for (const header of required) {
      // nginx does not inherit add_header into a location that declares its
      // own. While Cache-Control lived in a `location ~ \.(js|css|html)$`
      // block, every script, every stylesheet and /index.html silently lost all
      // four of these while /login kept them.
      expect(headers[header], `${path} is missing ${header}`).toBeTruthy();
    }
  }
});

test("api responses carry exactly one of each security header", async ({ page }) => {
  const response = await page.request.get("/api/todos", { failOnStatusCode: false });
  expect(response.status()).toBe(401);

  for (const header of ["content-security-policy", "referrer-policy", "x-content-type-options"]) {
    const values = response.headersArray().filter((entry) => entry.name.toLowerCase() === header);

    // One owner per boundary. Both nginx and the application used to write
    // these on /api/, which left two Content-Security-Policy headers for the
    // browser to intersect and two different Referrer-Policy values.
    expect(values.length, `${header} appears ${values.length} times`).toBe(1);
  }
});

test("the login endpoint refuses a caller who keeps guessing", async ({ page }) => {
  // The interesting limit is not the global one. Ten attempts a minute from one
  // address is generous for a person and useless for a script, and it is
  // configured on /api/auth/login alone so that nothing else in the demo is
  // braked by someone else's brute force.
  const statuses = [];
  for (let attempt = 0; attempt < 14; attempt += 1) {
    const response = await page.request.post("/api/auth/login", {
      data: { email: `nobody.${attempt}@example.com`, password: "wrong-but-long-enough" },
      failOnStatusCode: false
    });
    statuses.push(response.status());
    if (response.status() === 429) break;
  }

  expect(statuses, "the brake has to close before the fourteenth guess").toContain(429);

  // 401 before that: the limit refuses the caller, it does not accept them.
  expect(statuses.filter((status) => status === 200)).toHaveLength(0);
});
