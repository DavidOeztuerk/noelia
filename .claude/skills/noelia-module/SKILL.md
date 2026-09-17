---
name: noelia-module
description: Use when adding anything to the Noelia library — a module, a provider, a security check, an option type, or a dashboard section. Encodes the composition rules and the one defect class this codebase keeps finding in itself.
---

# Adding to Noelia

## The defect this codebase keeps finding

Something is **registered and therefore present, but not effective, and nothing
notices**. `docs/MASTERPLAN-5.0.md` §1 lists eleven instances; `MIGRATION.md`'s
5.1.0 section lists nine more found afterwards; 6.4.0 found another in the
standalone dashboard report. Every rule below exists because of it.

## A provider composes; it does not register beside

```csharp
// Wrong. The built-in modules register their own during Build(), so this is
// overwritten and nothing says so.
services.AddRedisCache("app");
services.AddNoelia(configuration, environment, "app", noelia => noelia.UseDefaults());

// Right.
services.AddNoelia(configuration, environment, "app", noelia => noelia
    .UseDefaults()
    .UseRedisCache("app"));
```

This is what `UseX(...)` exists for next to `AddX(...)`. A new pair must be added
to `ProviderPairs.All` in `Noelia.Cli` — `CliProviderPairTests` reflects over the
shipped assemblies and fails the build until it is.

## A test asserts the effect, never the fluent return

Three `HealthCheckBuilder` methods once had commented-out bodies and passing
`…_ReturnsSelf` tests. **If the test would still pass with the body deleted, it
is not testing anything.**

Where reflection or a query drives an assertion, prove it is not vacuous:

```csharp
pairs.Should().HaveCountGreaterThan(5,
    "a reflection query that found none would make the assertion below pass "
    + "without checking anything");
```

## Module contracts

`NoeliaModuleCatalogue.All` pairs a registration action with a contract:

- `Requires<T>(hints…)` — **never without at least one hint**; the builder throws.
  The hint names the exact package and call, and that is what the startup failure
  prints.
- `Provides<T>(packageId, registration)`
- `RegistersNothing(reason)` — for a module that genuinely registers nothing, such
  as a pipeline step. Without it the dashboard reports "running, but no declared
  effect", which is correct for a bug and wrong for a design.

Registration order is the catalogue's, never the caller's. Reordering two lines
in a composition root must not change behaviour; `Use`/`Without` only select.

`UseDefaults()` must compose with nothing else registered. Anything needing a
provider decision — cache, broker, secret store, key, hashing, session store —
stays out of it.

Requirements are verified **at startup**, not at call time, because providers are
usually registered after the module that needs them.

## Security checks

Implement `ISecurityCheck`. The id is a stable lowercase dotted string; the
runner enforces that and rejects duplicates.

- `Category == Composition` runs **whether or not its module is composed**. Every
  other category runs only for modules in the composition. If you write a second
  place that selects checks, it must apply this same rule — the standalone
  dashboard report selected by module alone and silently never ran them.
- Summary and remediation describe **shapes and actions only**. Never a
  configuration value, key, token, connection string or raw exception text. The
  runner catches exceptions and substitutes a fixed sentence for exactly this
  reason.
- Prefer `NotApplicable` over a loud `Pass` where the thing checked is absent. A
  green tick against something that does not apply is noise in the one document
  meant to cut through it.
- Cite an obligation with `References` only where the article genuinely asks
  about what the check measures. See the `noelia-evidence` skill.

## Options

`OptionsConsumptionTests` requires every registered `*Options` type to have a
reader. An option nobody reads is a false claim, not a stub.

## Ports

A promise an interface cannot express goes in an abstract `…Conformance` base
class that **every** implementation inherits — not in a per-implementation test.
See `RedisTokenRevocationConformanceTests`.

Ports live in `Noelia.Abstractions`, which is provider-free: `NoeliaProviderFree`
makes the build fail with `NOELIA0001` for an infrastructure package and
`NOELIA0002` for an ASP.NET reference.

## Dashboard sections

A section is a `DashboardSection` member, a row in `DashboardPage.Sections`, and a
case in the render switch. The router asks the page which sections exist, so a
section cannot acquire a link that routes nowhere.

Four states, and they are not one state: `Absent` (nothing of this kind is
registered — a decision), `Unavailable`, `Faulted` (registered and stopped
answering — an outage), `Present`. A failure is styled as a failure and never as
a note.

Add the section to `WholeDashboard()` in the test helpers, or every test using it
silently stops covering the new page.

## Comments

Comments explain **why**, not what, and are unusually load-bearing here —
`Directory.Build.targets`, the middleware order and the module catalogue all carry
the reasoning inline. Match that register rather than stripping it. Public members
carry XML docs written as prose for a consumer.

Follow the language already in the file: English for code, XML docs, README and
SOVEREIGNTY; German for `docs/`, `MIGRATION.md` and MSBuild comments.
