import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import { readFileSync } from "node:fs";

/**
 * The sign-out button must not be live before there is a session to end.
 *
 * The race: index.html shipped the button enabled while dashboard.js attached
 * its handler only after the refresh exchange. A click in between did nothing,
 * silently. These tests read the real markup so a button that is "disabled" in
 * the unit and enabled in the page cannot pass.
 */
const markup = readFileSync(new URL("../index.html", import.meta.url), "utf8");

async function withPage(body) {
  const dom = new JSDOM(markup, { url: "https://micro-prod.localhost:8443/" });
  const { LogoutButton } = await import("../js/ui/LogoutButton.js");
  return body(dom, LogoutButton, dom.window.document.querySelector("[data-logout]"));
}

const click = (dom, button) => {
  button.dispatchEvent(new dom.window.Event("click"));
  return new Promise((resolve) => setTimeout(resolve, 0));
};

test("the shipped markup starts the button disabled", () => {
  const button = new JSDOM(markup).window.document.querySelector("[data-logout]");
  assert.equal(button.disabled, true);
  assert.equal(button.getAttribute("aria-disabled"), "true");
});

test("a click before the session is ready does nothing", async () => {
  await withPage(async (dom, LogoutButton, button) => {
    let calls = 0;
    new LogoutButton(button, async () => { calls++; return true; });

    await click(dom, button);
    assert.equal(calls, 0);
  });
});

test("enable() makes it clickable and a click signs out", async () => {
  await withPage(async (dom, LogoutButton, button) => {
    let calls = 0;
    const logout = new LogoutButton(button, async () => { calls++; return true; });
    logout.enable();

    assert.equal(button.disabled, false);
    assert.equal(button.hasAttribute("aria-disabled"), false);

    await click(dom, button);
    assert.equal(calls, 1);
  });
});

test("a failed sign-out keeps the session and gives the button back", async () => {
  await withPage(async (dom, LogoutButton, button) => {
    let calls = 0;
    const logout = new LogoutButton(button, async () => { calls++; return false; });
    logout.enable();

    await click(dom, button);
    assert.equal(button.disabled, false, "the person can try again");

    await click(dom, button);
    assert.equal(calls, 2);
  });
});

test("a sign-out request that throws also gives the button back", async () => {
  await withPage(async (dom, LogoutButton, button) => {
    const logout = new LogoutButton(button, async () => { throw new Error("boom"); });
    logout.enable();

    await click(dom, button);
    assert.equal(button.disabled, false);
  });
});

test("the button is disabled while the request is in flight, and clicks do not stack", async () => {
  await withPage(async (dom, LogoutButton, button) => {
    let calls = 0;
    let release;
    const logout = new LogoutButton(button, () => {
      calls++;
      return new Promise((resolve) => { release = resolve; });
    });
    logout.enable();

    await click(dom, button);
    assert.equal(button.disabled, true);
    await click(dom, button);
    assert.equal(calls, 1);

    release(true);
    await new Promise((resolve) => setTimeout(resolve, 0));
    assert.equal(button.disabled, true, "a signed-out page stays disabled while it navigates");
  });
});
