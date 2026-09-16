/** The one-line status area at the top of a page. */
export class Notice {
  /** @param {HTMLElement} element */
  constructor(element) {
    this.element = element;
  }

  info(message) {
    this.#show(message, false);
  }

  error(message) {
    this.#show(message, true);
  }

  clear() {
    this.element.hidden = true;
    this.element.textContent = "";
  }

  #show(message, isError) {
    this.element.textContent = message;
    this.element.classList.toggle("notice--error", isError);
    this.element.hidden = false;
  }
}
