# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Noelia is a set of 13 NuGet packages that form a shared foundation for .NET
microservices (CQRS pipeline, security/identity, caching, messaging, health,
resilience, observability). It is a library, not an application — there is
nothing to run locally except the tests. Targets `net10.0`; version `5.0.0`.

`README.md` (~1900 lines) is the consumer-facing manual and the authority on
public behaviour. `MIGRATION.md` records every breaking change per version.
Read the relevant section of those before changing a public API.

## Commands

```bash
dotnet restore Noelia.slnx
dotnet build   Noelia.slnx                  # warnings are errors
dotnet test    Noelia.slnx

# one project
dotnet test tests/Noelia.Infrastructure.Tests

# one test / class / namespace
dotnet test tests/Noelia.Infrastructure.Tests --filter "FullyQualifiedName~PackageDependencyBudgetTests"
dotnet test tests/Noelia.Infrastructure.Tests --filter "Category=Unit"      # or Category=Integration

# coverage (Infrastructure project only)
dotnet test tests/Noelia.Infrastructure.Tests --settings tests/Noelia.Infrastructure.Tests/coverage.runsettings

dotnet pack Noelia.slnx -c Release -o packages
```

Two things about the test suite:

- **`Category=Integration` needs a container runtime.** The Redis conformance
  suites use Testcontainers and **throw rather than skip** when Docker is not
  reachable — a silently skipped integration suite is indistinguishable from a
  passing one. Start Docker, or filter with `Category=Unit`.
- **`PackageDependencyBudgetTests` reads `obj/project.assets.json`**, so a
  restore must have happened for every `src/` project or it fails on a missing
  file rather than on a real budget breach.

## Architecture

### Ports in, providers out

Clean Architecture with the dependency rule enforced mechanically, not by
review. `Noelia.Core` (entities, identity primitives), `Noelia.Contracts`
(boundary DTOs) and `Noelia.Abstractions` (**every** port) set
`<NoeliaProviderFree>true</NoeliaProviderFree>`; the `NoeliaDependencyGuard`
target in `Directory.Build.targets` fails their build with **`NOELIA0001`** for
any package reference that is not a platform abstraction, and **`NOELIA0002`**
for an ASP.NET framework reference.

`Noelia.Infrastructure` is the engine: it consumes ports and names no driver.
Drivers live in their own packages (`Noelia.Redis`, `Noelia.InMemory`,
`Noelia.Passwords.*`, `Noelia.Messaging.MassTransit`,
`Noelia.Data.EntityFrameworkCore`). The rationale is
`docs/adr/0001-souveraenitaet-durch-portschnitt.md` and `SOVEREIGNTY.md`.

### Composition: modules that declare requirements

`AddNoelia(configuration, environment, serviceName, noelia => …)` in
`src/Noelia.Infrastructure/Extensions/NoeliaServiceCollectionExtensions.cs` is
the single service-side entry point. It builds a `NoeliaBuilder`
(`src/Noelia.Abstractions/Hosting/`) over `NoeliaModuleCatalogue.All`
(`src/Noelia.Infrastructure/Builder/NoeliaModuleCatalogue.cs`).

- `NoeliaModule` is a `record struct` wrapping a string, **not an enum**, so an
  external package can define its own module and hand it to `Use`.
- Each catalogue entry pairs a registration action with a
  `NoeliaModuleContract` — `Requires<T>(providerHints…)` and
  `Provides<T>(packageId, registration)`. A requirement carries the exact
  package and call that satisfies it, and that is what the startup failure
  message prints. **Never add a `Requires` without at least one hint** (the
  builder throws).
- Registration order is the catalogue's, never the caller's: modules read what
  earlier ones registered, so reordering two lines in a composition root must
  not change behaviour. `Use`/`Without` only select; last mention wins.
- `UseDefaults()` is an explicit call. Everything in it must compose with
  nothing else registered — anything needing a provider decision (cache, broker,
  secret store, key, hashing algorithm, session store) stays out of it.
- Requirements are recorded via `RequiresProvider(…)`
  (`src/Noelia.Application/Hosting/ProviderRequirementValidator.cs`) and
  verified at startup, not at call time, because providers are usually
  registered after the module that needs them.

The pipeline half is `UseNoelia(environment, serviceName[, configure])` in
`src/Noelia.Infrastructure/Extensions/ServiceCollectionExtensions.cs`. It reads
the `NoeliaComposition` out of the container, so **a step whose module was left
out is skipped rather than throwing**. The default chain's order carries
security weight (health checks before rate limiting, rate limiting outside
authentication, audit after the principal) — changing it is a behavioural
change, and the reasons are in the comments there.

### CQRS

`AddCQRS(assemblies)` (`src/Noelia.Application/`) scans what it is handed: the
`CachingBehavior`/`CacheInvalidationBehavior` pair is only added to the MediatR
pipeline if some type implements `ICacheableQuery` or `ICacheInvalidatingCommand`,
so a service that caches nothing needs no cache provider. The
`tests/Noelia.Fixtures.PlainCqrs` and `tests/Noelia.Fixtures.CachingCqrs`
projects exist as separate assemblies purely so that scan can be tested with a
genuinely empty and a genuinely non-empty assembly.

### Self-verification

The 5.0 line exists because of a class of defect where something was
*registered* and therefore *present* but not *effective*, and nothing noticed
(`docs/MASTERPLAN-5.0.md` §1 lists eleven instances). Two mechanisms answer it
and new work is expected to extend them:

- **`ISecurityCheck`** (`src/Noelia.Abstractions/Security/Checks/SecurityChecks.cs`)
  — an executable assertion that a promised property actually holds, returning a
  `SecurityCheckResult`. Summaries and remediations describe *shapes and
  actions only*: never copy configuration values, keys, tokens, connection
  strings or raw exception text into them.
- **`Noelia.Dashboard`** — a read-only, server-rendered view of the composition
  that actually happened and the latest check run.

## Invariants the build and tests enforce

Breaking any of these fails CI, usually with a message explaining why:

| Guard | Rule |
|---|---|
| `NoeliaDependencyGuard` (MSBuild) | provider-free projects take no infrastructure package |
| `Architecture/PackageDependencyBudgetTests` | every shipped package's **exact** restored NuGet count is pinned, and mirrored in the README table — adding a dependency means updating both, deliberately |
| `Architecture/HttpPackageStaysThinTests`, `DashboardPackageStaysThinTests` | `Noelia.Http` and `Noelia.Dashboard` declare **zero** `PackageReference`s; that is their entire reason to exist as separate packages |
| `Architecture/ProviderIndependenceTests` | no driver assembly reachable from the engine |
| `Architecture/OptionsConsumptionTests` | every registered `*Options` type has a reader — an option nobody reads is a false claim, not a stub |
| `Directory.Build.props` | `TreatWarningsAsErrors`, plus the XML-documentation diagnostics (`CS1570` … `CS1734`) as errors. `CS1591` (missing comment) is the only one suppressed |

Package versions are managed centrally in `Directory.Packages.props` with
transitive pinning; add `<PackageVersion>` there and a bare `<PackageReference>`
in the project. MediatR, MassTransit and FluentAssertions are pinned to their
last Apache-2.0 releases — upgrading them is a licensing decision, not a chore.

## Conventions

- **Ports get conformance suites, not per-implementation tests.** A promise an
  interface cannot express (a revocation takes effect on the next check; counting
  and deciding are indivisible) goes in an abstract `…Conformance` base class
  that every implementation inherits — see
  `tests/Noelia.Infrastructure.Tests/Security/RedisTokenRevocationConformanceTests.cs`.
- Tests are xUnit + FluentAssertions + NSubstitute, `[Trait("Category", …)]` on
  every class. Tests touching process-wide state (Serilog's static logger,
  `Console.Out`) join `SerilogGlobalStateCollection`, which disables
  parallelisation.
- **Comments explain why, not what**, and are unusually load-bearing here —
  `Directory.Build.targets`, the middleware order, and the module catalogue all
  carry the reasoning inline. Match that register rather than stripping it.
- Public members carry XML docs written as prose for a consumer. Both English
  (code, XML docs, README, SOVEREIGNTY) and German (`docs/`, `MIGRATION.md`
  from 4.4.3 down, MSBuild comments) appear — follow the language already in
  the file you are editing.
- 5.0 renamed *everything* to the `Noelia`/`noelia`/`NOELIA` identity: package
  ids, namespaces, config prefixes, env vars (`NOELIA_JWT_*`), storage key
  prefixes, the `noelia_refresh_tokens` table, the `NoeliaTenant` filter,
  `noelia.*` meter names and cryptographic domain separators. Anything new
  follows that; a domain separator or storage prefix is data-format surface, so
  changing one is a migration, not a rename.

## Releasing

SemVer, all 13 packages shipped under one version. A breaking change is
announced as `[Obsolete]` in a minor release before it is removed in the next
major. `.github/workflows/publish.yml` runs on a published GitHub release (tag
`vX.Y.Z`) or manual dispatch; it refuses anything that is not stable SemVer,
not an ancestor of `origin/main`, or not matching `<VersionPrefix>` in
`Directory.Build.props`, then builds, **runs the full test suite**, and pushes
via OIDC Trusted Publishing (no stored API key). `docs/RELEASE-GATE-5.0.md`
describes the out-of-repo acceptance gate against a real consumer project.

## Docs map

- `docs/MASTERPLAN-5.0.md` — why 5.0 is a major, and the five decisions behind it
- `docs/befunde/` — one document per security finding: what it did vs. what it
  claimed, a Noelia-only reproduction, and status. Its "Geprüft und in Ordnung"
  table records places already audited and found sound, so they are not re-read.
- `docs/adr/0001-souveraenitaet-durch-portschnitt.md` — the port cut
- `docs/PLAN-DASHBOARD-5.0.md`, `docs/PROMPTS-5.0.md` — dashboard plan, working prompts
