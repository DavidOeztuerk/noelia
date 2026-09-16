/**
 * The signed-in person, as this tab knows them.
 *
 * The access token lives in memory and nowhere else — not in sessionStorage,
 * where any script on the page can read it. It is short-lived by design, and
 * a reload gets a new one from the refresh cookie rather than remembering the
 * old one.
 *
 * The refresh token is not here at all. It is an HttpOnly cookie, which is the
 * point: script cannot read it, so a cross-site scripting bug cannot carry it
 * away.
 */
export class Session {
  static #current = null;

  /** @returns {{accessToken: string, displayName: string, userId: string}|null} */
  static current() {
    return Session.#current;
  }

  static start({ accessToken, displayName, userId }) {
    Session.#current = { accessToken, displayName, userId };
  }

  /** Replaces only the token, after a refresh. */
  static renew(accessToken) {
    if (Session.#current) Session.#current = { ...Session.#current, accessToken };
  }

  static clear() {
    Session.#current = null;
  }

  static get isActive() {
    return Session.#current !== null;
  }
}
