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
   * Withdraws the token, then forgets it. The local session is cleared even
   * when the call fails: leaving someone signed in because the network was
   * down is the worse of the two outcomes.
   */
  async #signOut() {
    try {
      await this.auth.signOut();
    } finally {
      Session.clear();
      Page.redirectToLogin();
    }
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
      this.list.render(await this.todos.list());
    });
  }
}

const http = new HttpClient();
const session = await Page.requireSession(http);
if (session) new DashboardPage(session, http).start();
