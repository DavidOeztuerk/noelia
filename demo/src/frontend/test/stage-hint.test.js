import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import { readFileSync } from "node:fs";
import { showStageHint } from "../js/ui/StageHint.js";

/**
 * User and Todo data are in-memory in every stage; sessions persist from
 * Staging on. The page has to say so where a person could be misled.
 */
const hint = () => new JSDOM('<p data-stage-hint hidden></p>').window.document.querySelector("[data-stage-hint]");

for (const host of ["micro-staging.localhost", "mono-staging.localhost", "micro-prod.localhost", "mono-prod.localhost"]) {
  test(`${host} shows the in-memory limit`, () => {
    const element = hint();
    assert.equal(showStageHint(element, host), true);
    assert.equal(element.hidden, false);
    assert.match(element.textContent, /Arbeitsspeicher/);
    assert.match(element.textContent, /neu startet/);
  });
}

for (const host of ["micro-dev.localhost", "mono-dev.localhost", "localhost"]) {
  test(`${host} shows nothing`, () => {
    const element = hint();
    assert.equal(showStageHint(element, host), false);
    assert.equal(element.hidden, true);
  });
}

test("every page that calls it has a slot for it", () => {
  for (const page of ["index.html", "login.html", "register.html"]) {
    assert.match(readFileSync(new URL(`../${page}`, import.meta.url), "utf8"), /data-stage-hint/, page);
  }
});
