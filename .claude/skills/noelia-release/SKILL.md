---
name: noelia-release
description: Use when cutting a Noelia release — bumping the version, packing, verifying against the demo and ContosoInvoicing, and publishing all 14 packages. Covers the whole gate in order, including the steps that have actually been forgotten before.
---

# Cutting a Noelia release

One version across all 14 packages. The gate below is in the order it has to run,
and every step exists because skipping it once cost a patch release.

## Before anything

```bash
git status                       # must be clean
gh issue list --state open       # a release is not the place to discover one
```

## 1. Bump exactly two places

```bash
# Directory.Build.props
<VersionPrefix>X.Y.Z</VersionPrefix>

# demo/Directory.Packages.props   ← this one has been forgotten
<NoeliaVersion>X.Y.Z</NoeliaVersion>
```

Forgetting the demo pin does not fail locally — the old packages are still in the
NuGet cache. It fails in CI as `NU1603`, after the push.

## 2. The library gate

```bash
dotnet build Noelia.slnx -c Release          # warnings are errors
dotnet test  Noelia.slnx --filter "Category=Unit"
dotnet test  Noelia.slnx --filter "Category=Integration"   # needs Docker running
```

`Category=Integration` **throws rather than skips** without a container runtime.
That is deliberate: a silently skipped integration suite is indistinguishable
from a passing one.

If `PackageDependencyBudgetTests` fails on a missing `project.assets.json`,
restore first — it reads the restored graph, not the project file.

## 3. Pack to the local feed

```bash
dotnet pack Noelia.slnx -c Release -o demo/.local-feed
rm -rf ~/.nuget/packages/noelia.*     # a candidate keeps its version number
```

The cache purge is not optional. Without it the demo restores yesterday's
candidate under today's number and every test passes against the wrong code.

## 4. The demo, as a foreign consumer

```bash
cd demo
dotnet restore Noelia.TodoDemo.sln --configfile NuGet.Local.Config --force --no-cache
dotnet build  Noelia.TodoDemo.sln --no-restore -c Release
dotnet test   Noelia.TodoDemo.sln --no-build  -c Release
```

`DemoStaysAForeignConsumerTests` enforces that nothing under `demo/` takes a
project reference out of it. A gate compiling against the working tree it was cut
from proves nothing about the packages.

## 5. The stack, and a service that is not the demo

```bash
NOELIA_SOURCE=/src/.local-feed docker compose --profile all up -d --build --wait
python3 eng/security-checks.py          # fails on an unallowed Fail
cd src/frontend && npm test && npm run e2e
```

Then **ContosoInvoicing** (`~/Projects/ContosoInvoicing`), which is outside the
demo and declares things the demo never does. Every time it has been run against
a candidate it has found something the demo could not: a file path classified as
a public host, SaaS domains missing from the third-country list, a missing
`using` in the documented snippet, a crash on first run in Production.

```bash
cd ~/Projects/ContosoInvoicing
dotnet restore --configfile NuGet.Local.Config --force --no-cache
dotnet build -c Release && dotnet run -c Release
noelia analyze .                        # the tool, on a real consumer
```

## 6. Documentation, in the same commit

- `README.md` — the package table and the section for whatever changed
- `MIGRATION.md` — a section per version, **German**, newest at the top
- `CLAUDE.md` — the package count and version line

A breaking change is announced as `[Obsolete]` in a minor release before it is
removed in the next major. If a change has no migration note, it is either not
released or not understood.

## 7. Publish

```bash
git commit && git push
gh release create vX.Y.Z --title "..." --notes "..."
```

`.github/workflows/publish.yml` refuses anything that is not stable SemVer, not
an ancestor of `origin/main`, or not matching `<VersionPrefix>`. It runs the full
suite again and pushes via OIDC Trusted Publishing — there is no stored API key
to rotate.

Wait for all 14 packages to appear on nuget.org before bumping any downstream
repository (ContosoInvoicing, NoeliaControlPlane) off the local feed.
