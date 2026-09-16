/**
 * Builds the list of dashboards for the stage this page is being served from.
 *
 * Written after `/noelia` on a micro host answered with the user service's
 * dashboard. The page said which service it was, but the address did not, and
 * an address that implies "the whole stack" while showing a quarter of it is
 * the kind of small untruth this demo exists to avoid.
 */
const host = location.hostname;                      // micro-dev.localhost
const stage = host.split(".")[0].split("-")[1] ?? "dev";
const secure = location.protocol === "https:";
const port = location.port ? `:${location.port}` : "";

const services = [
  ["Gateway", "gateway", "routes API traffic; reads no token"],
  ["User service", "user", "registration, sign-in, sessions"],
  ["Todo service", "todo", "todos; verifies signatures only"],
  ["Monolith", "mono", "the same application in one process"]
];

const list = document.querySelector("[data-dashboards]");

for (const [label, name, what] of services) {
  // The gateway composes no dashboard outside Development, so there is no
  // address for one. Listing it anyway would promise a page that does not
  // exist and leave a "route not found" in the gateway's log on every click.
  if (name === "gateway" && stage !== "dev") {
    continue;
  }

  const item = document.createElement("li");
  const link = document.createElement("a");

  link.href = `${secure ? "https" : "http"}://${name}-${stage}.localhost${port}/noelia`;
  link.textContent = `${label} — ${name}-${stage}.localhost`;

  const note = document.createElement("span");
  note.className = "muted";
  note.textContent = ` · ${what}`;

  item.append(link, note);
  list.append(item);
}

document.querySelector("[data-note]").textContent =
  stage === "dev"
    ? "Development: every dashboard is open."
    : "Staging and Production: each needs the operator secret in X-Noelia-Operator, "
      + "and the gateway composes no dashboard at all — it holds no key and could not "
      + "recognise an operator.";
