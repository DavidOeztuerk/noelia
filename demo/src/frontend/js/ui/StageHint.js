/**
 * Says out loud what the demo's providers cannot promise.
 *
 * Sessions, revocations and the key ring live in Valkey from Staging on, so
 * those stages look like they persist. The user and todo stores do not: they
 * are in-memory in every stage, "Production" included. A person who registers
 * on https://micro-prod.localhost, restarts a service and finds the account
 * gone has met a demo limitation, not a defect in Noelia — so the page says so
 * before it happens.
 */
export function stageOf(hostname) {
  return hostname.split(".")[0].split("-")[1] ?? "dev";
}

/**
 * @param {HTMLElement | null} element
 * @param {string} hostname
 * @returns {boolean} whether the hint is shown
 */
export function showStageHint(element, hostname) {
  if (!element) return false;

  const show = stageOf(hostname) === "staging" || stageOf(hostname) === "prod";
  if (show) {
    element.textContent =
      "Demo: Konten und Aufgaben liegen im Arbeitsspeicher und gehen verloren, "
      + "wenn ein Dienst neu startet.";
  }
  element.hidden = !show;
  return show;
}
