import { HttpClient } from "../core/HttpClient.js";
import { Notice } from "../ui/Notice.js";
import { Session } from "../core/Session.js";
import { ApiError } from "../core/ApiError.js";

/**
 * What every page shares: an HTTP client, a notice area, and one place where
 * errors turn into something the user can read.
 */
export class Page {
  /** @param {HttpClient} [http] Shared, so one refresh serves every caller. */
  constructor(http) {
    this.http = http ?? new HttpClient();
    this.notice = new Notice(document.querySelector("[data-notice]"));
  }

  /**
   * Runs an action and reports failure in the notice area.
   * A lost session sends the user to the login page instead — staying on a
   * page whose every request will fail is worse than moving them.
   * @param {() => Promise<void>} action
   * @param {import("../ui/FormController.js").FormController} [form]
   *   Receives the server's per-field messages when one was rejected.
   */
  async guard(action, form) {
    try {
      form?.showFieldErrors({});
      await action();
    } catch (error) {
      if (error instanceof ApiError && error.isUnauthorized) {
        Session.clear();
        Page.redirectToLogin();
        return;
      }

      if (error instanceof ApiError) form?.showFieldErrors(error.fieldErrors);
      this.notice.error(error instanceof Error ? error.message : String(error));
    }
  }

  static redirectToLogin() {
    window.location.assign("/login");
  }

  static redirectToApp() {
    window.location.assign("/");
  }

  /**
   * Establishes the session for a page that needs one, or sends the visitor
   * away.
   *
   * There is nothing stored to read: the access token lives only in memory and
   * a reload loses it. The refresh cookie is what survives, so a page begins by
   * exchanging it.
   *
   * @param {import("../core/HttpClient.js").HttpClient} http
   */
  static async requireSession(http) {
    if (await http.renew()) return Session.current();

    Page.redirectToLogin();
    return null;
  }
}
