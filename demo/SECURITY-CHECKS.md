# Security checks for Noelia

Health checks answer whether a process can serve traffic. Security checks must
answer a different question: whether the running composition still satisfies
its declared security boundary. A reachable database can be healthy while its
TLS validation is disabled; a JWT service can be live while using a development
key. Those states must not be collapsed into one green health result.

## Two layers

1. **Package probes** run before a Noelia release. Each probe installs only the
   package and module under test, then proves its externally visible security
   properties. These checks are executable evidence and later give the Noelia
   dashboard a one-module composition to render.
2. **Runtime checks** inspect deployment configuration and provider state. They
   now ship in `Noelia.Abstractions`/`Noelia.Infrastructure`, run at startup and
   on explicit operator request, not on every HTTP request.

The package-independent runtime contract contains:

- stable check code, module and category;
- `Pass`, `Warning`, `Fail` or `NotApplicable`;
- severity and a safe summary;
- remediation text;
- no key material, connection string, token, path containing a secret, or raw
  exception in either the result or logs.

The runner applies a timeout per check so a security backend cannot hang
application startup. A `Fail` is reported but does not redefine liveness.

## Exposure model

There must be no anonymous `/security` equivalent of `/health`. Detailed
results belong in the explicitly authorised `Noelia.Dashboard` package or in a
local startup report. The dashboard returns an empty 404 without its operator
policy, refuses Production without a written exposure reason, returns only
sanitized fields and shapes, disables caching and exposes no write method.
Liveness never depends on a security check; readiness may fail only when the
application explicitly chooses that policy.

## Runtime check catalogue in 5.0

- `noelia.composition.providers`
- `noelia.jwt.key-separation`
- `noelia.headers.browser-baseline`
- `noelia.sessions.refresh-cookie`
- `noelia.cors.credentialed-origins`
- `noelia.secrets.provider`
- `noelia.encryption.aead`
- `noelia.ratelimit.degradation`
- `noelia.revocation.degradation`
- `noelia.dashboard.operator-access`

Release-time architecture tests separately pin the exact dependency count for
all thirteen packages; the CI vulnerability audit checks their complete restored
graphs.

Microservice, monolith and isolated-package consumers must exercise the same
contract. The dashboard may display only checks belonging to modules contained
in `NoeliaComposition`; silence for an uninstalled module is intentional, not a
passing result.

## `NotApplicable` is a boundary, not a pass

The accepted User-Service and Monolith report
`noelia.sessions.refresh-cookie` as `NotApplicable`. They do not use ASP.NET
Cookie Authentication; their application code emits a refresh-token cookie
directly. Noelia therefore has no registered cookie-handler options it can
truthfully attest. HTTP tests separately inspect the actual `Set-Cookie` header
for `HttpOnly` and `SameSite=Strict`.

Packages without runtime infrastructure (`Noelia.Core`, `Noelia.Contracts`,
`Noelia.Abstractions`, `Noelia.Application`) likewise do not get invented empty
modules. Their isolated probes test their public contracts instead. This is an
activation-level `NotApplicable`, not a runtime security-check result.

## Canary and release-gate coverage

The release gate places a unique `NOELIA_GATE_CANARY` into hostile-looking
configuration sections, runs the startup report, serializes all results,
captures logs and renders every authorised dashboard composition. It then scans
those artifacts plus the full build/test/audit transcript for the exact value.
The accepted 5.0 candidate produced zero matches.

`eng/test-package-version.sh` also uses a fresh package cache and workspace. An
intentional restore failure with `Noelia.Redis` missing proved the normal Demo
configuration and existing build artifacts remain byte-for-byte unchanged.
`eng/test-docker-health.sh` separately proves both architectures are healthy
while every Production dashboard remains an empty 404.
