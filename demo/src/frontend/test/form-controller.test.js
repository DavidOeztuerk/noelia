import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";

/**
 * The form has to hand the server what the person typed.
 *
 * Written after it did not: the controller disabled the form before reading it,
 * and a disabled control is left out of FormData entirely — so every field
 * arrived as null and registration answered 400. Nothing caught it, because
 * every other test in this repository posts JSON and never touches a form.
 */
async function withForm(html, body) {
  const dom = new JSDOM(`<!doctype html><body>${html}</body>`, { url: "http://localhost" });

  // The module reads these off the global object, as it does in a browser.
  for (const name of ["FormData", "HTMLFormElement", "Event", "document"]) {
    globalThis[name] = name === "document" ? dom.window.document : dom.window[name];
  }

  const { FormController } = await import("../js/ui/FormController.js");
  return body(dom, FormController);
}

const registrationForm = `
  <form data-form novalidate>
    <input name="displayName" value="Ada Lovelace" />
    <input name="email" value="ada@example.com" />
    <input name="password" value="a-long-enough-password" />
    <p data-error-for="email" hidden></p>
    <button type="submit">Registrieren</button>
  </form>`;

test("submitting hands over every field", async () => {
  await withForm(registrationForm, async (dom, FormController) => {
    let submitted = null;

    const form = dom.window.document.querySelector("[data-form]");
    new FormController(form, (values) => {
      submitted = values;
      return Promise.resolve();
    });

    form.dispatchEvent(new dom.window.Event("submit", { cancelable: true }));
    await new Promise((resolve) => setTimeout(resolve, 0));

    assert.deepEqual(submitted, {
      displayName: "Ada Lovelace",
      email: "ada@example.com",
      password: "a-long-enough-password"
    });
  });
});

test("the form is disabled while the request is in flight", async () => {
  await withForm(registrationForm, async (dom, FormController) => {
    const form = dom.window.document.querySelector("[data-form]");
    let busyDuringSubmit = null;

    new FormController(form, () => {
      busyDuringSubmit = form.getAttribute("aria-busy");
      return Promise.resolve();
    });

    form.dispatchEvent(new dom.window.Event("submit", { cancelable: true }));
    await new Promise((resolve) => setTimeout(resolve, 0));

    assert.equal(busyDuringSubmit, "true", "the button must not be clickable twice");
    assert.equal(form.getAttribute("aria-busy"), "false", "and released afterwards");
  });
});

test("a server-side field error lands next to its field", async () => {
  await withForm(registrationForm, async (dom, FormController) => {
    const form = dom.window.document.querySelector("[data-form]");
    const controller = new FormController(form, () => Promise.resolve());

    controller.showFieldErrors({ email: ["Diese Adresse ist bereits vergeben."] });

    const slot = dom.window.document.querySelector('[data-error-for="email"]');
    assert.equal(slot.textContent, "Diese Adresse ist bereits vergeben.");
    assert.equal(slot.hidden, false);
    assert.equal(form.elements.email.getAttribute("aria-invalid"), "true");
  });
});
