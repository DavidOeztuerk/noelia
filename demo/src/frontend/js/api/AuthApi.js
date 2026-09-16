/** The endpoints that start, extend and end a session. */
export class AuthApi {
  /** @param {import("../core/HttpClient.js").HttpClient} http */
  constructor(http) {
    this.http = http;
  }

  register({ displayName, email, password }) {
    return this.http.post("/api/auth/register", { displayName, email, password });
  }

  login({ email, password }) {
    return this.http.post("/api/auth/login", { email, password });
  }

  /**
   * Ends the session server-side. Forgetting the token here would only stop
   * this tab from sending it; the refresh cookie would still work everywhere
   * else it was copied.
   */
  signOut() {
    return this.http.post("/api/auth/sign-out");
  }
}
