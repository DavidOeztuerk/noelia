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
V=$(grep -o '<VersionPrefix>[^<]*' Directory.Build.props | cut -d'>' -f2)-local.$(date -u +%Y%m%d%H%M%S)
PKGS=$(mktemp -d)
dotnet pack Noelia.slnx -c Release -p:Version=$V -o demo/.local-feed/$V
```

The candidate carries a version of its own (`X.Y.Z-local.<stamp>`), lives in its
own feed folder, and is restored into its own package folder (`$PKGS`). Nothing
is packed under the real number, and nothing is deleted from
`~/.nuget/packages`: a test build under a real version number in the global
folder would be handed silently to every other project on the machine that
restores that number (#63). The old `rm -rf ~/.nuget/packages/noelia.*` is gone
for that reason, not forgotten. `demo/eng/release-gate.mjs` does the same.

## 4. The demo, as a foreign consumer

```bash
cd demo
# demo/NuGet.Config maps * to nuget.org, so a bare --source is ignored: say where Noelia.* comes from
cat > $PKGS/NuGet.Config <<EOF
<configuration><packageSources><clear />
<add key="candidate" value="$PWD/.local-feed/$V" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
<packageSourceMapping><packageSource key="candidate"><package pattern="Noelia.*" /></packageSource>
<packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping></configuration>
EOF
dotnet restore Noelia.TodoDemo.sln --packages $PKGS --configfile $PKGS/NuGet.Config -p:NoeliaVersion=$V
dotnet build  Noelia.TodoDemo.sln --no-restore -c Release -p:NoeliaVersion=$V
dotnet test   Noelia.TodoDemo.sln --no-build  -c Release -p:NoeliaVersion=$V
```

`DemoStaysAForeignConsumerTests` enforces that nothing under `demo/` takes a
project reference out of it. A gate compiling against the working tree it was cut
from proves nothing about the packages.

`demo/probes/Noelia.M2M.Probe` covers the receiving side of service-to-service
rights as a package consumer: an issuer signs a token over HTTP, a receiver with
one `RequireServiceCaller` door plus `MapServiceFallback` answers the named caller
2xx and a stranger, no token and a person's token with the uniform 404, and the
report after start (`ISecurityCheckReport.Latest`) shows `noelia.m2m.caller-policy`
as `Pass`. It runs with the demo test run above, so it needs the same candidate.

## 5. The stack, and a service that is not the demo

```bash
NOELIA_VERSION=$V NOELIA_SOURCE=/src/.local-feed/$V docker compose --profile all up -d --build --wait
python3 eng/security-checks.py          # fails on an unallowed Fail
cd src/frontend && npm test && npm run e2e
```

`security-checks.py` now expects `noelia.m2m.caller-policy` to be `Pass` on the
demo UserService (the issuer, with its one rate-limited token endpoint); `NotApplicable`
or `Warning` there is a finding, see `eng/security-check-expectations.json`. The
receiving side (a door with its fallback) is not composed in any demo service and is
covered by `Noelia.M2M.Probe` in step 4, not by the stack.

Then **ContosoInvoicing** (`~/Projects/ContosoInvoicing`), which is outside the
demo and declares things the demo never does. Every time it has been run against
a candidate it has found something the demo could not: a file path classified as
a public host, SaaS domains missing from the third-country list, a missing
`using` in the documented snippet, a crash on first run in Production.

Do not run it by hand: ContosoInvoicing pins Noelia 6.4.0 and `noelia analyze` calls
whatever tool is installed globally, so the old four lines built and started the
wrong version. The script works on a throw-away copy, with an own package folder,
and fails instead of reporting green when it cannot prove the candidate ran.

```bash
V="$(sed -nE 's|.*<VersionPrefix>([0-9.]+)</VersionPrefix>.*|\1|p' Directory.Build.props)-gate.$(date -u +%Y%m%d%H%M%S)"
FEED="$(mktemp -d)"
dotnet pack Noelia.slnx -c Release -p:Version="$V" -o "$FEED"
demo/eng/contoso-candidate-check.sh \
  --contoso ~/Projects/ContosoInvoicing --noelia "$PWD" --version "$V" --feed "$FEED"
```

What it proves, and fails on otherwise:

- **Pin** — every `Noelia.*` entry in `obj/project.assets.json` is exactly `$V`.
- **Source** — restore uses its own `NuGet.Config` (`<clear/>`, `Noelia.*` only from the feed) and a private `NUGET_PACKAGES`; nothing new lands in `~/.nuget/packages`.
- **Tool** — `noelia` is the candidate's own (`dotnet dnx Noelia.Cli --version $V`), version and `+sha` match the checkout.
- **Secret** — the service starts in Production with a throw-away `Operator__Secret` (never printed) and its `report.json` says `invoicing` / `Production`.
- **Commit** — for every restored `Noelia.*` package, `repository@commit` in the nuspec equals `git rev-parse HEAD`; a missing commit fails.

`noelia analyze` exit 1 (findings) is printed and read by you; exit 2 (tool error)
fails. A bare `X.Y.Z` is refused: the version needs the prerelease suffix. NU1900
(NuGetAudit offline) under `-warnaserror` can be a false alarm of the network, not of
the candidate; rerun online, never disable the audit. This step will be adjusted
again with #55 (`.env`) and #63 (the cache purge above goes away).

## 6. Documentation, in the same commit

- `README.md` — the package table and the section for whatever changed
- `MIGRATION.md` — a section per version, **German**, newest at the top
- `CLAUDE.md` — the package count and version line

If a change has no migration note, it is either not released or not
understood.

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
