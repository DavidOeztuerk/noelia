/**
 * Wraps a form: disables it while submitting, and puts server-side field
 * errors next to the fields they belong to.
 */
export class FormController {
  /**
   * @param {HTMLFormElement} form
   * @param {(values: Record<string, string>) => Promise<void>} onSubmit
   */
  constructor(form, onSubmit) {
    this.form = form;
    this.onSubmit = onSubmit;
    this.form.addEventListener("submit", (event) => this.#handle(event));
  }

  /**
   * Shows one message per field. Fields the server did not complain about are
   * cleared, so a fixed error does not linger next to a now-valid input.
   * @param {Record<string, string[]>} fieldErrors
   */
  showFieldErrors(fieldErrors) {
    for (const slot of this.form.querySelectorAll("[data-error-for]")) {
      const field = slot.dataset.errorFor;
      const messages = fieldErrors[field] ?? fieldErrors[field?.toLowerCase()];

      slot.textContent = messages?.[0] ?? "";
      slot.hidden = !messages?.length;
      this.form.elements[field]?.setAttribute("aria-invalid", messages?.length ? "true" : "false");
    }
  }

  reset() {
    this.form.reset();
    this.showFieldErrors({});
  }

  async #handle(event) {
    event.preventDefault();

    // The browser's own validation first: it is instant and needs no round trip.
    if (!this.form.reportValidity()) return;

    // Read before disabling: a disabled control is left out of FormData
    // entirely, so doing this the other way round submits an empty object and
    // the server sees every field as null.
    const values = Object.fromEntries(
      [...new FormData(this.form)].map(([key, value]) => [key, String(value).trim()])
    );

    this.#setBusy(true);
    try {
      await this.onSubmit(values);
    } finally {
      this.#setBusy(false);
    }
  }

  #setBusy(isBusy) {
    for (const element of this.form.elements) {
      element.disabled = isBusy;
    }
    this.form.setAttribute("aria-busy", String(isBusy));
  }
}
