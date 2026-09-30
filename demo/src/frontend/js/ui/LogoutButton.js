/**
 * The sign-out button, which is only a button once there is a session to end.
 *
 * It used to be live from the first paint while its handler was attached only
 * after the refresh cookie had been exchanged. A click in that gap did nothing
 * at all — no request, no message — and the person was left believing they had
 * signed out. The markup now ships it disabled (the same arrangement as
 * `data-needs-js` on the forms) and this enables it, so "clickable" and
 * "will do something" are the same statement.
 */
export class LogoutButton {
  /**
   * @param {HTMLButtonElement} button
   * @param {() => Promise<boolean>} signOut
   *   Resolves true once the server has ended the session. Anything else keeps
   *   the session, and the button comes back so the person can try again.
   */
  constructor(button, signOut) {
    this.button = button;
    this.signOut = signOut;
    this.busy = false;
    this.button.addEventListener("click", () => this.#click());
  }

  /** Called when the session is ready, and not before. */
  enable() {
    this.#set(true);
  }

  async #click() {
    if (this.busy || this.button.disabled) return;

    this.busy = true;
    this.#set(false);

    let ended = false;
    try {
      ended = (await this.signOut()) === true;
    } catch {
      // The caller reports its own failures; an exception that reaches here is
      // still "the session was not ended", never an unhandled rejection.
      ended = false;
    } finally {
      this.busy = false;
      // A signed-out page is about to navigate away; re-enabling it there would
      // only invite a second request against a session that no longer exists.
      if (!ended) this.#set(true);
    }
  }

  #set(enabled) {
    this.button.disabled = !enabled;
    if (enabled) {
      this.button.removeAttribute("aria-disabled");
      this.button.removeAttribute("title");
    } else {
      this.button.setAttribute("aria-disabled", "true");
    }
  }
}
