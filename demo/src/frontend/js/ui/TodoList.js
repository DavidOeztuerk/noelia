/**
 * Renders the todo list.
 *
 * Builds nodes rather than assigning innerHTML: a title is text the user typed,
 * and putting it through innerHTML would let one user's todo run script in
 * another user's browser.
 */
export class TodoList {
  /**
   * @param {HTMLElement} list
   * @param {HTMLElement} emptyState
   * @param {(todoId: string) => Promise<void>} onComplete
   */
  constructor(list, emptyState, onComplete) {
    this.list = list;
    this.emptyState = emptyState;
    this.onComplete = onComplete;
  }

  /** @param {Array<{id: string, title: string, isCompleted: boolean}>} todos */
  render(todos) {
    this.list.replaceChildren(...todos.map((todo) => this.#item(todo)));
    this.emptyState.hidden = todos.length > 0;
  }

  #item(todo) {
    const item = document.createElement("li");
    item.className = `todo${todo.isCompleted ? " todo--done" : ""}`;

    const title = document.createElement("span");
    title.className = "todo__title";
    title.textContent = todo.title;
    item.append(title);

    if (todo.isCompleted) {
      const state = document.createElement("span");
      state.className = "todo__state";
      state.textContent = "Erledigt";
      item.append(state);
      return item;
    }

    const button = document.createElement("button");
    button.type = "button";
    button.className = "button button--small";
    button.textContent = "Erledigen";
    button.addEventListener("click", async () => {
      button.disabled = true;
      try {
        await this.onComplete(todo.id);
      } finally {
        // `finally`, not `catch`. The page reports failures through
        // Page.guard(), which handles the error and resolves normally — so the
        // catch below never ran, and after an HTTP 500 the button stayed
        // disabled with no way back except a reload. On success this button is
        // about to be replaced by the re-rendered list, so re-enabling it costs
        // nothing.
        button.disabled = false;
      }
    });
    item.append(button);

    return item;
  }
}
