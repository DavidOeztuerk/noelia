import { ApiError } from "./ApiError.js";
import { Session } from "./Session.js";

/**
 * Talks to the API through the gateway.
 *
 * Every non-2xx becomes an {@link ApiError}, so callers never inspect a status
 * code themselves and never mistake an error body for data.
 */
export class HttpClient {
  /** @param {string} baseUrl Prefix for every path; empty means same origin. */
  constructor(baseUrl = "") {
    this.baseUrl = baseUrl;
    this.#refreshing = null;
  }

  #refreshing;

  get(path) {
    return this.#request("GET", path);
  }

  post(path, body) {
    return this.#request("POST", path, body);
  }

  patch(path, body) {
    return this.#request("PATCH", path, body);
  }

  /**
   * Sends the request, and on a 401 renews the access token once and repeats.
   *
   * Once, not in a loop: if the second attempt is refused too, the session is
   * genuinely over and retrying would only spin.
   */
  async #request(method, path, body) {
    try {
      return await this.#send(method, path, body);
    } catch (error) {
      if (!(error instanceof ApiError) || !error.isUnauthorized || path.startsWith("/api/auth/")) {
        throw error;
      }

      if (!(await this.renew())) throw error;
      return this.#send(method, path, body);
    }
  }

  /**
   * Exchanges the refresh cookie for a new access token.
   *
   * Shared between concurrent callers: three requests failing at once must not
   * become three refreshes, because rotation would then treat two of them as a
   * replay.
   *
   * @returns {Promise<boolean>} whether a session is now in force.
   */
  renew() {
    this.#refreshing ??= this.#send("POST", "/api/auth/refresh")
      .then((session) => {
        if (Session.isActive) Session.renew(session.accessToken);
        else Session.start(session);
        return true;
      })
      .catch(() => {
        Session.clear();
        return false;
      })
      .finally(() => {
        this.#refreshing = null;
      });

    return this.#refreshing;
  }

  async #send(method, path, body) {
    const session = Session.current();

    const response = await fetch(`${this.baseUrl}${path}`, {
      method,
      headers: {
        Accept: "application/json",
        ...(body === undefined ? {} : { "Content-Type": "application/json" }),
        ...(session ? { Authorization: `Bearer ${session.accessToken}` } : {})
      },
      body: body === undefined ? undefined : JSON.stringify(body)
    });

    if (!response.ok) {
      throw await ApiError.fromResponse(response);
    }

    // 204 and an empty body are both "nothing to read", not "null data".
    if (response.status === 204 || response.headers.get("content-length") === "0") {
      return null;
    }

    return response.json();
  }
}
