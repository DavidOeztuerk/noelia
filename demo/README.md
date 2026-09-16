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
| gateway | open | not composed | not composed |

Two levers, deliberately separate: whether the module is composed at all, and
who the visibility policy admits. A stage answering `None` does not run the page
— the composition report says so — rather than running it behind a policy that
refuses everyone.

The gateway is `None` outside Development for a reason worth reading: it holds
no key and verifies no token by design, so it could not recognise an operator if
it wanted to. Showing it anyway would teach the wrong lesson.

To open a staged dashboard:

```bash
curl -k -H "X-Noelia-Operator: $NOELIA_DASHBOARD_OPERATOR_SECRET" \
  https://mono-prod.localhost:8443/noelia
```

Without the header it is a 404 — not a 403 and not a login form. Nothing in the
response distinguishes it from a path that was never routed.

## Secrets

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
# A Fail nobody allowed exits non-zero.
python3 eng/security-checks.py
python3 eng/security-checks.py --profile dev
```

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
