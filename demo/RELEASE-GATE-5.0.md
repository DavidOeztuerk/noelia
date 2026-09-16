# Local Noelia 5.0 release-gate state

**Recorded:** 2026-09-15 · **Candidate:** `5.0.0` · **Result:** passed

This directory is not a Git repository. The work below is a local acceptance
state, not a commit and not permission to publish a package. WorkerTransfer was
not read or changed as part of this gate.

## Consumer boundary

- `NuGet.Config` contains NuGet.org only and no GitHub Packages source or PAT.
- Every project consumes `Noelia.*` through `PackageReference`; there is no
  `ProjectReference` into the framework repository.
- `Directory.Packages.props` pins one `NoeliaVersion` for all thirteen package
  ids. It now points to `5.0.0`; the package gate also supplies that exact
  candidate version explicitly.
- `eng/test-package-version.sh` copies this directory to a fresh temporary
  workspace, excludes `.env`, `bin`, `obj`, package caches and local feeds, and
  restores into a new package cache using candidate-source mapping.
- `NuGet.Docker.Config` uses NuGet.org only for the public build. When
  `NOELIA_SOURCE` points at a staged candidate, the Dockerfile switches to
  `NuGet.Docker.Local.Config`, which maps `Noelia.*` to that candidate and all
  other packages to NuGet.org. Neither config contains a credential.

## Architecture state

- `src/gateway` plus separate User and Todo API processes remains the
  microservice composition. `MicroserviceEndToEndTests` proves a token issued
  by User is verified by Todo and that a second owner cannot list or mutate the
  first owner's item.
- `src/monolith/Demo.Monolith.Api` composes the same User and Todo features in
  one host. It has no Ocelot package, loaded assembly or gateway process.
- Gateway, User, Todo and Monolith install `Noelia.Dashboard`. Their policy is
  development plus local caller only; Production has a recorded reason but the
  visibility predicate remains false. Container tests require an empty 404.
- Every host gives the dashboard a marked `GateCanary` configuration value;
  the rendered HTML must never contain it.

## Isolated package matrix

Each project below directly references exactly one distinct `Noelia.*` package:

| Probe | Direct package | Tests |
|---|---|---:|
| `Noelia.Abstractions.Probe` | `Noelia.Abstractions` | 1 |
| `Noelia.Application.Probe` | `Noelia.Application` | 1 |
| `Noelia.Contracts.Probe` | `Noelia.Contracts` | 2 |
| `Noelia.Core.Probe` | `Noelia.Core` | 2 |
| `Noelia.Dashboard.Probe` | `Noelia.Dashboard` | 5 |
| `Noelia.Data.EntityFrameworkCore.Probe` | `Noelia.Data.EntityFrameworkCore` | 1 |
| `Noelia.Http.Probe` | `Noelia.Http` | 1 |
| `Noelia.InMemory.Probe` | `Noelia.InMemory` | 1 |
| `Noelia.SecurityHeaders.Probe` | `Noelia.Infrastructure` | 7 |
| `Noelia.Messaging.MassTransit.Probe` | `Noelia.Messaging.MassTransit` | 1 |
| `Noelia.Passwords.Argon2.Probe` | `Noelia.Passwords.Argon2` | 1 |
| `Noelia.Passwords.BCrypt.Probe` | `Noelia.Passwords.BCrypt` | 1 |
| `Noelia.Encryption.Probe` | `Noelia.Redis` | 5 |

`Core`, `Contracts`, `Abstractions` and `Application` do not register runtime
infrastructure and therefore have no artificial module activation. Their
probes verify the public wire, identity, sanitisation, CQRS and module-contract
promises instead.

## Recorded execution

The candidate package gate completed with:

- 29 isolated package tests;
- 26 microservice tests;
- 6 monolith tests;
- 3 frontend DOM tests;
- zero known NuGet or npm vulnerabilities;
- zero candidate-canary occurrences in result, HTML, log or gate transcript.

An intentional second run omitted `Noelia.Redis`. Restore failed with `NU1101`
inside the temporary workspace. Hashes of the original NuGet files and every
existing `bin/obj` file were identical before and after the failure.

`eng/test-docker-health.sh` built all four microservice containers and the
separate monolith container from the same thirteen packages. Compose reported
all containers healthy; `/health/live` and `/health/ready` succeeded on
Gateway, User, Todo and Monolith; `/noelia` returned 404 for every Production
API host. Temporary containers, networks, volumes and staged package feeds were
removed. The local `.env` contents were never printed and its mode is `600`.

## Intentionally `NotApplicable`

`noelia.sessions.refresh-cookie` is `NotApplicable` in User and Monolith. They
write an application-owned refresh-token cookie and do not register an ASP.NET
Cookie Authentication handler for Noelia to inspect. Separate HTTP tests assert
the emitted cookie is `HttpOnly` and `SameSite=Strict`; Noelia correctly avoids
claiming a runtime pass for configuration it does not own.

The stable `5.0.0` package ids are not treated as publicly available until
NuGet.org can restore all thirteen anonymously. The local package and Docker
gates above have passed; after publication, rerun the ordinary restore and both
Compose files against the public feed.
