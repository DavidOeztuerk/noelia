import { Page } from "./Page.js";
import { TodoApi } from "../api/TodoApi.js";
import { AuthApi } from "../api/AuthApi.js";
import { TodoList } from "../ui/TodoList.js";
import { FormController } from "../ui/FormController.js";
import { Session } from "../core/Session.js";
import { HttpClient } from "../core/HttpClient.js";

/** The signed-in view: one person's todos. */
class DashboardPage extends Page {
  constructor(session, http) {
    super(http);
    this.session = session;
    this.todos = new TodoApi(this.http);
    this.auth = new AuthApi(this.http);

    this.list = new TodoList(
      document.querySelector("[data-todo-list]"),
      document.querySelector("[data-todo-empty]"),
      (todoId) => this.#complete(todoId)
    );

    this.form = new FormController(
      document.querySelector("[data-todo-form]"),
      ({ title }) => this.#create(title)
    );

    document.querySelector("[data-logout]").addEventListener("click", () => this.#signOut());
  }

  /**
   * Withdraws the token on the server, and only then forgets it here.
   *
   * It used to clear the local session in a `finally`, on the argument that
   * leaving someone signed in because the network was down is the worse
   * outcome. The argument does not hold: the refresh cookie is HttpOnly, so
   * clearing local state signs nobody out — it hides that they are still
   * signed in, and the next visit renews the session and lets them straight
   * back in. Saying "you are signed out" while the server disagrees is the
   * worse outcome, because it is the one nobody checks.
   */
  async #signOut() {
    try {
      await this.auth.signOut();
    } catch (error) {
      this.notice.error(
        "Abmelden fehlgeschlagen — du bist weiterhin angemeldet. Bitte erneut versuchen."
      );
      return;
    }

    Session.clear();
    Page.redirectToLogin();
  }

  async start() {
    document.querySelector("[data-session-name]").textContent = this.session.displayName;
    await this.#refresh();
  }

  #refresh() {
    return this.guard(async () => this.list.render(await this.todos.list()));
  }

  async #create(title) {
    await this.guard(async () => {
      await this.todos.create(title);
      this.form.reset();
      this.notice.info("Aufgabe angelegt.");
      this.list.render(await this.todos.list());
    }, this.form);
  }

  async #complete(todoId) {
    await this.guard(async () => {
      await this.todos.complete(todoId);

      // Say so. The list rerenders and the button changes to "Erledigt", which
      // is enough for somebody watching the screen and nothing at all for
      // somebody listening to it — the live region kept announcing "Aufgabe
      // angelegt." from whenever the last one was created.
      this.notice.info("Aufgabe erledigt.");
      this.list.render(await this.todos.list());
    });
  }
}

const http = new HttpClient();
const session = await Page.requireSession(http);
if (session) new DashboardPage(session, http).start();
