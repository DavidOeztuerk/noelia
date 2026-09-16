import { Page } from "./Page.js";
import { AuthApi } from "../api/AuthApi.js";
import { FormController } from "../ui/FormController.js";
import { Session } from "../core/Session.js";
import { ApiError } from "../core/ApiError.js";

/** The sign-in page. */
class LoginPage extends Page {
  constructor() {
    super();
    this.auth = new AuthApi(this.http);
    this.form = new FormController(
      document.querySelector("[data-auth-form]"),
      (values) => this.#submit(values)
    );
  }

  async #submit({ email, password }) {
    this.notice.clear();
    this.form.showFieldErrors({});

    try {
      const result = await this.auth.login({ email, password });
      Session.start(result);
      Page.redirectToApp();
    } catch (error) {
      if (error instanceof ApiError) {
        this.form.showFieldErrors(error.fieldErrors);
        this.notice.error(error.message);
        return;
      }
      throw error;
    }
  }
}

// No session probe on the way in: the refresh cookie is HttpOnly, so the
// only way to ask is to call /refresh, which answers 401 for every anonymous
// visitor and puts a failed request in the console of a page where nothing
// is wrong. Someone already signed in simply signs in again.
new LoginPage();
