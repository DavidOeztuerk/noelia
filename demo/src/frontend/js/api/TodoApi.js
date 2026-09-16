/**
 * The todo endpoints.
 *
 * None of them takes an owner: the server reads it from the token, so a
 * caller cannot ask for someone else's list by changing a parameter.
 */
export class TodoApi {
  /** @param {import("../core/HttpClient.js").HttpClient} http */
  constructor(http) {
    this.http = http;
  }

  list() {
    return this.http.get("/api/todos");
  }

  create(title) {
    return this.http.post("/api/todos", { title });
  }

  complete(todoId) {
    return this.http.patch(`/api/todos/${encodeURIComponent(todoId)}/complete`);
  }
}
