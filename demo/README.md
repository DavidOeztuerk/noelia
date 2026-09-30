# Noelia demo

One todo application, built twice and run three times.

It exists to answer a question a README cannot: **does the composition do what
it says?** The same application runs as a set of microservices behind a gateway
and as a single monolith, in Development, Staging and Production, from one
`docker compose`. The application code is identical in all six. Two things
differ, and both are decisions written into configuration rather than code:
where the state lives, and who may look at the operator dashboard.

The demo installs Noelia **as packages from nuget.org**, never as project
references. An architecture test in the library
(`DemoStaysAForeignConsumerTests`) keeps it that way, because a release gate
that builds against the working tree it was cut from proves nothing about the
packages.

## Running it

```bash
cp .env.example .env     # a key pair and an operator secret; see below

docker compose --profile dev     up -d --build --wait
docker compose --profile staging up -d --build --wait
docker compose --profile prod    up -d --build --wait
docker compose --profile all     up -d --build --wait   # all three at once
```

`--profile all` runs 15 containers. Each is small, but on a machine where that
is too much, run the stages one at a time — they are independent, down to
having a network each.

### Addresses

| URL | Behind it |
|---|---|
| `http://micro-dev.localhost:8080` | Microservices: frontend, gateway, user and todo service |
| `http://mono-dev.localhost:8080` | The same application as one host |
| `https://micro-staging.localhost:8443` · `https://mono-staging.localhost:8443` | Staging |
| `https://micro-prod.localhost:8443` · `https://mono-prod.localhost:8443` | Production |
| `…/noelia` | The operator dashboard |
| `…/api-docs` | The API description — Development only |

`*.localhost` resolves to 127.0.0.1 in every current browser without an
`/etc/hosts` entry. The staged hosts redirect plain HTTP to HTTPS and serve a
certificate the image generates at build time for exactly these names, so the
browser will warn — that warning is the honest signal that this is a local demo.

The plain-HTTP redirect names the port the browser really uses for HTTPS: the
edge reads `NOELIA_EXTERNAL_HTTPS_PORT`, which compose sets from
`NOELIA_HTTPS_PORT` (default 8443). With `NOELIA_HTTPS_PORT=0` Docker picks the
host port and the container cannot know it, so the redirect then omits the port
— use a fixed port if you want to follow redirects.

**Forwarded headers.** Each stage network has a fixed /24 (`172.29.10.0` dev,
`172.29.20.0` staging, `172.29.30.0` prod; override the three-octet prefix with
`NOELIA_NET_DEV`, `NOELIA_NET_STAGING`, `NOELIA_NET_PROD` if it collides with
something on your machine). The edge is `.2` and each stage's gateway `.3`.
Services believe `X-Forwarded-For`/`-Proto` only from those addresses (the
gateway and monolith: the edge; user and todo service: edge and gateway), via
`Demo__TrustedProxies__n`. Without configuration the default is loopback only.
This is a pattern for a fixed-topology demo, not guidance for production, where
the proxy address comes from the platform.

No service publishes a port of its own. Everything arrives through the edge,
because a gateway that can be walked around is not a boundary.

## The two things that differ

### Where the state lives

| | Development | Staging and Production |
|---|---|---|
| Cache | in the process | Valkey |
| Rate counters | in the process, per replica | Valkey, across replicas |
| Token revocation | in the process, per replica | Valkey |
| Refresh tokens | SQLite | SQLite |
| Data protection key ring | this container's filesystem | Valkey, encrypted with the master key |
| Users and todos | in the process | **in the process too** — lost on a service restart, even in Production |

**What survives a restart, and what does not.** Valkey runs with an append-only
file (`appendonly yes`, `everysec`) on a named volume per stage
(`valkey-staging-data`, `valkey-prod-data`), so revocations, the key ring, the
audit chain and rate counters survive a restart of the Valkey container; a crash
can lose about the last second. The SQLite session files survive on their own
volumes. **User accounts and todos do not: those stores are in-memory in every
stage, "Production" included** — they are lost when the user, todo or monolith
service restarts, while the sessions that refer to them persist. The demo does
not ship persistent providers for them; Staging and Production show a hint on
the login, register and todo pages saying so. `docker compose down -v` removes
the volumes, and with them all of the above.

One line in `appsettings.{Environment}.json` — `Demo:Providers:Redis` — decides
it. Nothing above the composition root knows which of the two it got, which is
the property the port cut exists to produce.

Valkey rather than Redis: the BSD-3-Clause fork under Linux Foundation
governance, wire-compatible with the same client.
[SOVEREIGNTY.md](../SOVEREIGNTY.md) names it as the sovereign choice, and a demo
that recommended one thing and ran another would be making the argument badly.

### Who may look at the dashboard

| | Development | Staging | Production |
|---|---|---|---|
| user, todo, monolith | open | operator secret | operator secret, reason recorded |
| gateway | open | operator secret, JSON only | operator secret, JSON only, reason recorded |

Two levers, deliberately separate: whether the module is composed at all, and
who the visibility policy admits. A stage answering `None` does not run the page
— the composition report says so — rather than running it behind a policy that
refuses everyone.

The gateway still holds no JWT signing key and verifies no application token.
Operator authentication is a separate boundary: its `OperatorReports` mode
checks the shared operator header and permits only `/noelia/report.json` and
`/noelia/audit-chain.json`. HTML, assets and other dashboard routes remain 404
even with that header. Nginx independently exposes only those two exact paths on
`gateway-staging.localhost:8443` and `gateway-prod.localhost:8443` (HTTPS).
The operator secret is mounted as a file, not a JWT key or an environment value.

Dependency declarations are host-specific: only user-service and monolith own
SQLite sessions; only token readers declare revocation storage. Gateway HTTP
destinations are derived from its static Ocelot routes at startup. Restart after
changing routes to refresh declarations; dynamic service discovery is not covered.
Redis endpoint parsing emits hosts only, never credentials, and does not pretend
RESP traffic is guarded by the HTTP policy. Local resources use `localhost` as a
locality marker, not a claim that an HTTP server runs there. Declarations and
factory allow-policy are configuration evidence, not observed traffic or proof
that Ocelot's independent transport is protected.

The isolated frontend check builds no backends and touches no demo data:

```sh
# From demo/; requires the existing Playwright/Chromium installation.
docker build -t noelia-demo-frontend:local-check src/frontend
PLAYWRIGHT_MODULE="$PWD/src/frontend/node_modules/playwright/index.mjs" \
  node eng/frontend-report-smoke.mjs noelia-demo-frontend:local-check
```

It uses temporary loopback ports, checks the three stage index pages and nginx's
gateway HTML/asset denials, then removes only its own container. It deliberately
accepts the local self-signed TLS certificate and is neither a TLS-trust test
nor a replacement for full Compose/gateway/control-plane acceptance.

To open a staged dashboard:

```bash
curl -k -H "X-Noelia-Operator: $NOELIA_DASHBOARD_OPERATOR_SECRET" \
  https://mono-prod.localhost:8443/noelia
```

Without the header it is a 404 — not a 403 and not a login form. Nothing in the
response distinguishes it from a path that was never routed.

## Secrets

### Reviewed composition baselines and isolated candidate builds

`eng/composition-baselines.json` records exact expected running-module IDs for
gateway, issuer, verifier and monolith, each with InMemory or Redis providers.
These are reviewed design expectations, not sets learned from a running report.
The Development host tests compare actual reports with the InMemory baselines;
Redis-stage acceptance still requires the running stack. Module presence does
not imply effective security or a completed security-check run.

The CP demo configuration contains matching baseline copies. From its repository:

```sh
node eng/check-demo-baselines.mjs ../Noelia/demo/eng/composition-baselines.json
```

Docker accepts an optional `NOELIA_VERSION` alongside `NOELIA_SOURCE`; both
restore and publish use the requested version. If omitted, the committed
`Directory.Packages.props` version remains in effect. Use a **new unique** version
and feed directory when package contents change; never overwrite public 6.4.0
or clear the global package cache. Example from the Noelia repository:

```sh
dotnet pack Noelia.slnx -c Release -p:Version=6.4.1-security.YOUR_UNIQUE_ID \
  -o demo/.local-feed/YOUR_UNIQUE_ID
cd demo
NOELIA_VERSION=6.4.1-security.YOUR_UNIQUE_ID \
  NOELIA_SOURCE=/src/.local-feed/YOUR_UNIQUE_ID \
  docker compose --profile all build
```

For a separately named acceptance stack, use Compose `-p` and explicitly supplied
test credentials (`--env-file` or environment), not an existing stack's data.
`NOELIA_HTTP_PORT` and `NOELIA_HTTPS_PORT` default to 8080/8443; set them to 0 for
Docker-assigned loopback ports and inspect `docker compose port edge 8080` /
`docker compose port edge 8443` using the same project/profile options. Do not
assume the CP's checked-in 8080/8443 addresses then match that isolated stack.
Building images alone does not constitute a running-stack acceptance test.

For the schema-3 candidate, the automated isolated report smoke builds and starts
all three stages with fresh synthetic keys, dynamic ports and a UUID project name:

```sh
# From demo/, after packing a uniquely versioned local candidate as above:
node eng/compose-report-smoke.mjs 6.4.1-security.YOUR_UNIQUE_ID /src/.local-feed/YOUR_UNIQUE_ID
```

It ignores `.env` and inherited application credentials, checks the actual
published dependency versions inside all 12 hosts, compares live reports with
the eight reviewed roles, and tests operator-only report access through nginx.
The staging/production Redis implementations run against disposable Valkey.
Only this run's containers, networks and synthetic database volumes are removed
afterwards; images and secret-free report artifacts remain. The self-signed demo
certificate is accepted only by the smoke's requests. This is **not** CP live
collection, a browser journey, a TLS-trust check or a durability/audit-completeness
test. An interrupted process may require cleanup of its printed UUID project;
never substitute an existing user's project name.

To include real CP collection and Chromium overview/egress/composition pages for
each of the six fleets, first build the sibling CP in Release, then run:

```sh
NOELIA_CP_SMOKE_MODULE=/absolute/path/to/NoeliaControlPlane/eng/demo-live-smoke.mjs \
PLAYWRIGHT_MODULE=/absolute/path/to/demo/src/frontend/node_modules/playwright/index.mjs \
node eng/compose-report-smoke.mjs 6.4.1-security.YOUR_UNIQUE_ID /src/.local-feed/YOUR_UNIQUE_ID
```

The CP helper uses one fleet per fresh process (no licence bypass), the checked-in
Development demo roles, and fresh content/data directories. It never loads the
WorkerTransfer configuration. Screenshots, reports and synthetic CP data remain
in the printed temporary directory; CP processes are stopped automatically.
This still does not cover the application's login/logout journeys.

### Runtime secrets

`.env` holds three values and is gitignored. Copy `.env.example` and fill it in:

```bash
# A key pair. The issuing service gets the private half; everyone else
# verifies. Generate with Noelia:
#   SigningKey.GenerateKeyPair(kid: "2026-08").ToEnvironmentLines()
NOELIA_JWT_KID=
NOELIA_JWT_PRIVATE_KEY=
NOELIA_JWT_PUBLIC_KEY=

# openssl rand -base64 32
NOELIA_DASHBOARD_OPERATOR_SECRET=

# The master key the encryption provider opens, and with it the data protection
# key ring. openssl rand -base64 32
NOELIA_MASTER_KEY=
```

They reach the containers as **Docker secrets**, not as environment entries, and
are read by the `KeyPerFile` configuration provider from `/run/secrets`. A
container's environment is visible to anyone on the host through
`docker inspect` and `/proc/<pid>/environ`; a tmpfs mounted into one container
is not.

## Checking it

```bash
# The library and the demo
dotnet test Noelia.TodoDemo.sln

# The browser flows a DOM cannot answer for: login, logout, cookies, CSP,
# console errors. Runs against whichever stack is up.
cd src/frontend && npm install && npx playwright install chromium
npm test        # unit, jsdom
npm run e2e     # browser, against http://mono-dev.localhost:8080

DEMO_BASE_URL=https://micro-prod.localhost:8443 npm run e2e

# What every running service says about its own security posture.
# Exits non-zero for an unallowed Fail, for a service that reported nothing,
# for an expected check ID missing from a service's latest run, and for a
# latest run that is incomplete. Only the latest run per service counts.
python3 eng/security-checks.py                        # --profile all
python3 eng/security-checks.py --profile staging
python3 eng/security-checks.py -p my-project --env-file my.env   # a stack under its own project name

# The gate's own tests (stdlib unittest, fixture logs)
python3 -m unittest discover -s eng -p 'test_*.py'
```

What the gate expects is written down once: `eng/security-check-expectations.json`
lists the check IDs (with the module each belongs to), the services per stage and
the provider set, and takes each service role's modules from
`eng/composition-baselines.json`. A `Composition` check is expected everywhere; any
other only where its module is in the role — so a module left out on purpose
(`OmittedModules` in the expectations file) takes its checks with it and is not a
failure, while the same absence undeclared is one.

Acceptances are scoped, never global: `eng/accepted-findings.txt` lines read
`<scope> <check id> <reason>`, scope being a stage (`prod`) or one service
(`gateway-prod`); the command line takes `--allow gateway-prod/noelia.x.y=reason`.
An acceptance covers a Fail — or, with `--fail-on-warning`, a Warning — of exactly
that check and never a *missing* one. The runner writes no run marker, so a run is
recognised by its check IDs restarting their ordinal order; a run truncated after
its last expected ID cannot be told from a complete one.

**The brake is real, and a repeated suite will find it.** The E2E run makes a
few dozen requests per host, and `/api/auth/login` is limited to ten a minute
from one address on purpose — that is where brute force goes. Running all six
hosts back to back several times within a minute trips it, and the next run
fails on something that is not a defect. Clear the counters between rounds:

```bash
# Only the rate-limit keys (`rl:*`). Never FLUSHALL: the same instance holds
# token revocations, the data protection key ring and the audit chain, and
# emptying it un-revokes tokens and starts a new chain.
docker compose exec valkey-prod    sh -c "valkey-cli --scan --pattern 'rl:*' | xargs -r valkey-cli DEL"
docker compose exec valkey-staging sh -c "valkey-cli --scan --pattern 'rl:*' | xargs -r valkey-cli DEL"
```

(The demo's Valkey has no password and no TLS; it is reachable only from its own
stage network. Add `-a`/`--tls` to both `valkey-cli` calls if you add either.)

`eng/security-checks.py` exists because a release gate can be green next to a
`noelia.headers.browser-baseline: Fail`, and was. It reads the checks out of the
logs — there is deliberately no endpoint to ask, since an anonymous URL listing
a service's security posture is a reconnaissance surface.

## Building against a candidate

Before a Noelia version is on nuget.org:

```bash
dotnet pack ../Noelia.slnx -c Release -o .local-feed
rm -rf ~/.nuget/packages/noelia.*
dotnet restore Noelia.TodoDemo.sln --configfile NuGet.Local.Config --force --no-cache

NOELIA_SOURCE=/src/.local-feed docker compose --profile all up -d --build --wait
```

The cache has to be cleared by hand: a candidate keeps its version number while
its contents change, and NuGet has no way to know that.

## What is in here

| | |
|---|---|
| `src/gateway` | Ocelot, routing only. Reads no token and builds no principal. |
| `src/services/UserService` | Registration, sign-in, sessions. The only holder of the private key. |
| `src/services/TodoService` | Todos. Verifies signatures, holds nothing it could sign with. |
| `src/monolith` | Both feature assemblies in one host, one mediator pipeline. |
| `src/shared/Demo.Platform` | The one place the stage's two decisions are read. |
| `src/frontend` | Plain HTML, CSS and ES modules. The edge proxy is built from here. |
| `probes/` | One project per shipped package, each installing exactly that package. |
| `tests/` | The application's own tests. |
| `eng/` | The scripts the gate runs. |
