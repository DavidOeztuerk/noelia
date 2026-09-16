import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";

/**
 * The "Erledigen" button has to come back after a failed request.
 *
 * Written after it did not. The button disabled itself before the call and
 * re-enabled itself in a `catch` — but the page reports failures through
 * Page.guard(), which handles the error and resolves normally. The catch never
 * ran. After an HTTP 500 the row was dead and the only way back was a reload,
 * which is exactly the moment a user wants to try again.
 *
 * The success path is covered too, because a `finally` that re-enables a button
 * the re-render is about to discard must not resurrect a completed row.
 */
async function withList(body) {
  const dom = new JSDOM(`<!doctype html><body><ul data-todo-list></ul><p data-todo-empty hidden></p></body>`, {
    url: "http://localhost"
  });

  for (const name of ["document", "Event", "HTMLElement"]) {
    globalThis[name] = name === "document" ? dom.window.document : dom.window[name];
  }

  const { TodoList } = await import("../js/ui/TodoList.js");
  return body(dom, TodoList);
}

const open = [{ id: "t-1", title: "Write the migration note", isCompleted: false }];

test("the button comes back when completing fails", async () => {
  await withList(async (dom, TodoList) => {
    // What Page.guard() does: it reports the failure and resolves. A rejecting
    // stub here would test a page that does not exist.
    const list = new TodoList(
      dom.window.document.querySelector("[data-todo-list]"),
      dom.window.document.querySelector("[data-todo-empty]"),
      async () => {
        /* swallowed, as guard() swallows */
      }
    );

    list.render(open);
    const button = dom.window.document.querySelector("button");
    assert.equal(button.disabled, false);

    button.dispatchEvent(new dom.window.Event("click"));
    await new Promise((resolve) => setTimeout(resolve, 0));

    assert.equal(button.disabled, false, "the row has to stay usable after a failure");
  });
});

test("the button is disabled while the request is in flight", async () => {
  await withList(async (dom, TodoList) => {
    let release;
    const inFlight = new Promise((resolve) => {
      release = resolve;
    });

    const list = new TodoList(
      dom.window.document.querySelector("[data-todo-list]"),
      dom.window.document.querySelector("[data-todo-empty]"),
      () => inFlight
    );

    list.render(open);
    const button = dom.window.document.querySelector("button");

    button.dispatchEvent(new dom.window.Event("click"));
    await new Promise((resolve) => setTimeout(resolve, 0));
    assert.equal(button.disabled, true, "a second click must not send a second request");

    release();
    await new Promise((resolve) => setTimeout(resolve, 0));
    assert.equal(button.disabled, false);
  });
});

test("a completed todo has no button to re-enable", async () => {
  await withList(async (dom, TodoList) => {
    const list = new TodoList(
      dom.window.document.querySelector("[data-todo-list]"),
      dom.window.document.querySelector("[data-todo-empty]"),
      async () => {}
    );

    list.render([{ id: "t-1", title: "Done already", isCompleted: true }]);

    assert.equal(dom.window.document.querySelector("button"), null);
    assert.match(dom.window.document.body.textContent, /Erledigt/);
  });
});
