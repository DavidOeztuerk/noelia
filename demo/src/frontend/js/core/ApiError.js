/**
 * A request the server refused, carrying what the caller needs to react:
 * the status, and — for a 400 — which field was wrong.
 */
export class ApiError extends Error {
  /**
   * @param {string} message Human-readable, already in the user's language.
   * @param {number} status HTTP status code.
   * @param {Record<string, string[]>} fieldErrors Per-field messages, keyed by field name.
   */
  constructor(message, status, fieldErrors = {}) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.fieldErrors = fieldErrors;
  }

  /** True when the session is gone and the caller should send the user to the login page. */
  get isUnauthorized() {
    return this.status === 401;
  }

  /**
   * Builds an error from a response, reading RFC 9457 problem details when present.
   * @param {Response} response
   */
  static async fromResponse(response) {
    const problem = await response.json().catch(() => ({}));

    return new ApiError(
      problem.detail || problem.title || `Anfrage fehlgeschlagen (${response.status})`,
      response.status,
      problem.errors ?? {}
    );
  }
}
